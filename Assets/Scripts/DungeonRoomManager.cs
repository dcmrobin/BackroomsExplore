using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class DungeonRoomManager : MonoBehaviour
{
    [Header("Cross-Chunk Settings")]
    [SerializeField] private int maxRoomChunkSpan = 3;
    [SerializeField] private float crossChunkConnectionChance = 0.3f;
    
    [Header("Structure Templates")]
    [SerializeField] private DungeonStructure[] structureTemplates;
    
    private InfiniteChunkManager chunkManager;
    private Dictionary<Vector3Int, List<DungeonStructure>> structuresByChunk = new Dictionary<Vector3Int, List<DungeonStructure>>();
    private Dictionary<string, DungeonStructure> activeStructures = new Dictionary<string, DungeonStructure>();
    
    [System.Serializable]
    public class DungeonStructure
    {
        public string structureId;
        public Vector3Int boundsMin;
        public Vector3Int boundsMax;
        public List<Vector3Int> occupiedVoxels = new List<Vector3Int>();
        public List<Vector3Int> entrancePoints = new List<Vector3Int>();
        public StructureType type;
        
        public enum StructureType { Room, Corridor, Staircase, Chamber, Special }
    }
    
    void Start()
    {
        chunkManager = GetComponent<InfiniteChunkManager>();
    }
    
    public void GenerateStructuresForChunk(Vector3Int chunkCoord, bool[,,] voxelGrid)
    {
        // Determine what structures should be in this chunk
        List<DungeonStructure> structures = new List<DungeonStructure>();
        
        // 1. Check for continuing structures from neighbors
        structures.AddRange(GetContinuingStructures(chunkCoord));
        
        // 2. Generate new structures
        if (ShouldGenerateNewStructure(chunkCoord))
        {
            DungeonStructure newStructure = GenerateRandomStructure(chunkCoord);
            if (newStructure != null)
            {
                structures.Add(newStructure);
                RegisterStructure(newStructure);
            }
        }
        
        // 3. Apply structures to voxel grid
        foreach (var structure in structures)
        {
            ApplyStructureToGrid(structure, chunkCoord, ref voxelGrid);
        }
        
        structuresByChunk[chunkCoord] = structures;
    }
    
    private List<DungeonStructure> GetContinuingStructures(Vector3Int chunkCoord)
    {
        List<DungeonStructure> continuingStructures = new List<DungeonStructure>();
        
        // Check adjacent chunks for structures that might extend into this chunk
        Vector3Int[] neighborOffsets = {
            Vector3Int.left, Vector3Int.right,
            Vector3Int.down, Vector3Int.up,
            Vector3Int.back, Vector3Int.forward
        };
        
        foreach (var offset in neighborOffsets)
        {
            Vector3Int neighborCoord = chunkCoord + offset;
            
            if (structuresByChunk.TryGetValue(neighborCoord, out var neighborStructures))
            {
                foreach (var structure in neighborStructures)
                {
                    if (StructureExtendsIntoChunk(structure, chunkCoord))
                    {
                        continuingStructures.Add(structure);
                    }
                }
            }
        }
        
        return continuingStructures;
    }
    
    private bool StructureExtendsIntoChunk(DungeonStructure structure, Vector3Int chunkCoord)
    {
        // Check if structure bounds intersect with this chunk
        Vector3 chunkWorldPos = chunkCoord * chunkManager.chunkSize;
        Bounds chunkBounds = new Bounds(
            chunkWorldPos + (Vector3)chunkManager.chunkSize * 0.5f,
            chunkManager.chunkSize
        );
        
        Bounds structureBounds = new Bounds(
            (Vector3)(structure.boundsMin + structure.boundsMax) * 0.5f,
            (Vector3)(structure.boundsMax - structure.boundsMin)
        );
        
        return chunkBounds.Intersects(structureBounds);
    }
    
    private bool ShouldGenerateNewStructure(Vector3Int chunkCoord)
    {
        // Use Perlin noise to determine structure density
        float noise = Mathf.PerlinNoise(
            chunkCoord.x * 0.1f + 1000,
            chunkCoord.z * 0.1f + 2000
        );
        
        return noise > 0.6f; // 40% chance for new structure
    }
    
    private DungeonStructure GenerateRandomStructure(Vector3Int chunkCoord)
    {
        if (structureTemplates.Length == 0) return null;
        
        // Pick random template
        DungeonStructure template = structureTemplates[Random.Range(0, structureTemplates.Length)];
        
        // Create instance
        DungeonStructure structure = new DungeonStructure
        {
            structureId = System.Guid.NewGuid().ToString(),
            type = template.type,
            boundsMin = Vector3Int.zero,
            boundsMax = template.boundsMax - template.boundsMin,
            entrancePoints = new List<Vector3Int>(template.entrancePoints)
        };
        
        // Position in world (center in chunk)
        Vector3Int chunkWorldPos = chunkCoord * chunkManager.chunkSize;
        Vector3Int structureCenter = chunkWorldPos + chunkManager.chunkSize / 2;
        
        structure.boundsMin = structureCenter - structure.boundsMax / 2;
        structure.boundsMax = structureCenter + structure.boundsMax / 2;
        
        // Generate occupied voxels
        GenerateStructureVoxels(structure, template);
        
        return structure;
    }
    
    private void GenerateStructureVoxels(DungeonStructure structure, DungeonStructure template)
    {
        structure.occupiedVoxels.Clear();
        
        // Simple copy for now - in reality would apply rotations, scaling, etc.
        foreach (var voxel in template.occupiedVoxels)
        {
            Vector3Int worldVoxel = structure.boundsMin + voxel;
            structure.occupiedVoxels.Add(worldVoxel);
        }
    }
    
    private void ApplyStructureToGrid(DungeonStructure structure, Vector3Int chunkCoord, ref bool[,,] voxelGrid)
    {
        Vector3Int chunkWorldPos = chunkCoord * chunkManager.chunkSize;
        
        foreach (var worldVoxel in structure.occupiedVoxels)
        {
            // Convert to chunk-local coordinates
            Vector3Int localVoxel = worldVoxel - chunkWorldPos;
            
            if (localVoxel.x >= 0 && localVoxel.x < voxelGrid.GetLength(0) &&
                localVoxel.y >= 0 && localVoxel.y < voxelGrid.GetLength(1) &&
                localVoxel.z >= 0 && localVoxel.z < voxelGrid.GetLength(2))
            {
                // Make voxel solid (or hollow based on structure type)
                voxelGrid[localVoxel.x, localVoxel.y, localVoxel.z] = true;
            }
        }
    }
    
    private void RegisterStructure(DungeonStructure structure)
    {
        activeStructures[structure.structureId] = structure;
        
        // Determine which chunks this structure occupies
        List<Vector3Int> affectedChunks = GetChunksForStructure(structure);
        
        foreach (var chunkCoord in affectedChunks)
        {
            if (!structuresByChunk.ContainsKey(chunkCoord))
            {
                structuresByChunk[chunkCoord] = new List<DungeonStructure>();
            }
            
            if (!structuresByChunk[chunkCoord].Contains(structure))
            {
                structuresByChunk[chunkCoord].Add(structure);
            }
        }
    }
    
    private List<Vector3Int> GetChunksForStructure(DungeonStructure structure)
    {
        List<Vector3Int> chunks = new List<Vector3Int>();
        
        Vector3Int minChunk = WorldToChunkCoord(structure.boundsMin);
        Vector3Int maxChunk = WorldToChunkCoord(structure.boundsMax);
        
        for (int x = minChunk.x; x <= maxChunk.x; x++)
        {
            for (int y = minChunk.y; y <= maxChunk.y; y++)
            {
                for (int z = minChunk.z; z <= maxChunk.z; z++)
                {
                    chunks.Add(new Vector3Int(x, y, z));
                }
            }
        }
        
        return chunks;
    }
    
    private Vector3Int WorldToChunkCoord(Vector3Int worldPos)
    {
        Vector3Int chunkSize = chunkManager.chunkSize;
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / (float)chunkSize.x),
            Mathf.FloorToInt(worldPos.y / (float)chunkSize.y),
            Mathf.FloorToInt(worldPos.z / (float)chunkSize.z)
        );
    }
    
    public void ClearChunkStructures(Vector3Int chunkCoord)
    {
        if (structuresByChunk.ContainsKey(chunkCoord))
        {
            structuresByChunk.Remove(chunkCoord);
        }
    }
    
    public DungeonStructure GetStructureAtPosition(Vector3 worldPosition)
    {
        Vector3Int voxelPos = new Vector3Int(
            Mathf.FloorToInt(worldPosition.x),
            Mathf.FloorToInt(worldPosition.y),
            Mathf.FloorToInt(worldPosition.z)
        );
        
        foreach (var structure in activeStructures.Values)
        {
            if (structure.occupiedVoxels.Contains(voxelPos))
            {
                return structure;
            }
        }
        
        return null;
    }
    
    public List<DungeonStructure> GetNearbyStructures(Vector3 worldPosition, float radius)
    {
        List<DungeonStructure> nearby = new List<DungeonStructure>();
        
        foreach (var structure in activeStructures.Values)
        {
            Vector3 structureCenter = (Vector3)(structure.boundsMin + structure.boundsMax) * 0.5f;
            
            if (Vector3.Distance(structureCenter, worldPosition) <= radius)
            {
                nearby.Add(structure);
            }
        }
        
        return nearby;
    }
}