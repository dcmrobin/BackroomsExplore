using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;

public class DungeonLightingManager : MonoBehaviour
{
    [Header("Lighting Settings")]
    [SerializeField] private float globalAmbientLight = 0.05f;
    [SerializeField] private float lightDecayRate = 0.15f;
    [SerializeField] private int lightPropagationSteps = 10;
    [SerializeField] private bool smoothLighting = true;
    [SerializeField] private Color sunlightColor = Color.white;
    [SerializeField] private float sunlightIntensity = 0.3f;
    [SerializeField] private Vector3 sunlightDirection = Vector3.down;
    
    [Header("Performance")]
    [SerializeField] private bool asyncLighting = true;
    [SerializeField] private int lightsPerFrame = 10;
    
    private InfiniteChunkManager chunkManager;
    private Dictionary<Vector3Int, LightSource> lightSources = new Dictionary<Vector3Int, LightSource>();
    private Dictionary<Vector3Int, float[,,]> chunkLightmaps = new Dictionary<Vector3Int, float[,,]>();
    
    private struct LightSource
    {
        public Vector3Int worldPosition;
        public float intensity;
        public Color color;
        public float range;
    }
    
    void Start()
    {
        chunkManager = GetComponent<InfiniteChunkManager>();
    }
    
    public void AddLightSource(Vector3Int worldPosition, float intensity, Color color, float range)
    {
        LightSource light = new LightSource
        {
            worldPosition = worldPosition,
            intensity = intensity,
            color = color,
            range = range
        };
        
        lightSources[worldPosition] = light;
        
        // Update lighting for affected chunks
        UpdateLightingAroundPosition(worldPosition, range);
    }
    
    public void RemoveLightSource(Vector3Int worldPosition)
    {
        if (lightSources.ContainsKey(worldPosition))
        {
            float range = lightSources[worldPosition].range;
            lightSources.Remove(worldPosition);
            
            // Update lighting for affected chunks
            UpdateLightingAroundPosition(worldPosition, range);
        }
    }
    
    private async void UpdateLightingAroundPosition(Vector3 worldPosition, float range)
    {
        // Find all chunks within light range
        List<Vector3Int> affectedChunks = GetChunksInRadius(worldPosition, range);
        
        if (asyncLighting)
        {
            await UpdateChunksLightingAsync(affectedChunks);
        }
        else
        {
            UpdateChunksLighting(affectedChunks);
        }
    }
    
    private List<Vector3Int> GetChunksInRadius(Vector3 center, float radius)
    {
        List<Vector3Int> chunks = new List<Vector3Int>();
        Vector3Int chunkSize = chunkManager.chunkSize;
        
        Vector3Int centerChunk = WorldToChunkCoord(center);
        int radiusInChunks = Mathf.CeilToInt(radius / Mathf.Min(chunkSize.x, chunkSize.z));
        
        for (int x = -radiusInChunks; x <= radiusInChunks; x++)
        {
            for (int z = -radiusInChunks; z <= radiusInChunks; z++)
            {
                Vector3Int chunkCoord = centerChunk + new Vector3Int(x, 0, z);
                
                // Quick distance check
                Vector3 chunkWorldPos = ChunkCoordToWorld(chunkCoord);
                if (Vector3.Distance(center, chunkWorldPos) <= radius + chunkSize.magnitude)
                {
                    chunks.Add(chunkCoord);
                }
            }
        }
        
        return chunks;
    }
    
    private async Task UpdateChunksLightingAsync(List<Vector3Int> chunkCoords)
    {
        foreach (var chunkCoord in chunkCoords)
        {
            await UpdateChunkLightingAsync(chunkCoord);
        }
    }
    
    private async Task UpdateChunkLightingAsync(Vector3Int chunkCoord)
    {
        // Get chunk data
        bool[,,] voxels = GetChunkVoxels(chunkCoord);
        if (voxels == null) return;
        
        float[,,] lightmap = new float[voxels.GetLength(0), voxels.GetLength(1), voxels.GetLength(2)];
        
        // Calculate lighting on background thread
        await Task.Run(() =>
        {
            CalculateChunkLighting(chunkCoord, voxels, ref lightmap);
        });
        
        // Apply lighting on main thread
        chunkLightmaps[chunkCoord] = lightmap;
        UpdateChunkVisuals(chunkCoord, lightmap);
    }
    
    private void UpdateChunksLighting(List<Vector3Int> chunkCoords)
    {
        foreach (var chunkCoord in chunkCoords)
        {
            UpdateChunkLighting(chunkCoord);
        }
    }
    
    private void UpdateChunkLighting(Vector3Int chunkCoord)
    {
        bool[,,] voxels = GetChunkVoxels(chunkCoord);
        if (voxels == null) return;
        
        float[,,] lightmap = new float[voxels.GetLength(0), voxels.GetLength(1), voxels.GetLength(2)];
        CalculateChunkLighting(chunkCoord, voxels, ref lightmap);
        
        chunkLightmaps[chunkCoord] = lightmap;
        UpdateChunkVisuals(chunkCoord, lightmap);
    }
    
    private void CalculateChunkLighting(Vector3Int chunkCoord, bool[,,] voxels, ref float[,,] lightmap)
    {
        Vector3Int chunkWorldPos = Vector3Int.FloorToInt(ChunkCoordToWorld(chunkCoord));
        
        // Initialize with ambient light
        for (int x = 0; x < voxels.GetLength(0); x++)
        {
            for (int y = 0; y < voxels.GetLength(1); y++)
            {
                for (int z = 0; z < voxels.GetLength(2); z++)
                {
                    lightmap[x, y, z] = globalAmbientLight;
                    
                    // Add sunlight if at top and empty
                    if (y == voxels.GetLength(1) - 1 && !voxels[x, y, z])
                    {
                        lightmap[x, y, z] += sunlightIntensity;
                    }
                }
            }
        }
        
        // Add light sources
        foreach (var lightSource in lightSources.Values)
        {
            AddLightContribution(lightSource, chunkWorldPos, voxels, ref lightmap);
        }
        
        // Propagate light
        for (int i = 0; i < lightPropagationSteps; i++)
        {
            PropagateLight(voxels, ref lightmap);
        }
        
        // Apply smoothing if enabled
        if (smoothLighting)
        {
            SmoothLightmap(voxels, ref lightmap);
        }
    }
    
    private void AddLightContribution(LightSource light, Vector3Int chunkOrigin, 
                                     bool[,,] voxels, ref float[,,] lightmap)
    {
        Vector3Int localPos = light.worldPosition - chunkOrigin;
        
        // Check if light is within or near this chunk
        if (IsPositionInChunk(localPos, voxels.GetLength(0), voxels.GetLength(1), voxels.GetLength(2)))
        {
            // Light source is inside chunk
            if (!voxels[localPos.x, localPos.y, localPos.z])
            {
                lightmap[localPos.x, localPos.y, localPos.z] = 
                    Mathf.Max(lightmap[localPos.x, localPos.y, localPos.z], light.intensity);
            }
        }
        
        // Calculate light falloff in surrounding area
        int rangeInVoxels = Mathf.CeilToInt(light.range);
        Vector3Int start = Vector3Int.Max(localPos - Vector3Int.one * rangeInVoxels, Vector3Int.zero);
        Vector3Int end = Vector3Int.Min(localPos + Vector3Int.one * rangeInVoxels, 
                                       new Vector3Int(voxels.GetLength(0) - 1, 
                                                     voxels.GetLength(1) - 1, 
                                                     voxels.GetLength(2) - 1));
        
        for (int x = start.x; x <= end.x; x++)
        {
            for (int y = start.y; y <= end.y; y++)
            {
                for (int z = start.z; z <= end.z; z++)
                {
                    if (voxels[x, y, z]) continue; // Skip solid voxels
                    
                    float distance = Vector3Int.Distance(localPos, new Vector3Int(x, y, z));
                    if (distance <= light.range)
                    {
                        float attenuation = 1f / (1f + lightDecayRate * distance * distance);
                        float lightValue = light.intensity * attenuation;
                        
                        lightmap[x, y, z] = Mathf.Max(lightmap[x, y, z], lightValue);
                    }
                }
            }
        }
    }
    
    private void PropagateLight(bool[,,] voxels, ref float[,,] lightmap)
    {
        float[,,] newLightmap = (float[,,])lightmap.Clone();
        
        for (int x = 0; x < voxels.GetLength(0); x++)
        {
            for (int y = 0; y < voxels.GetLength(1); y++)
            {
                for (int z = 0; z < voxels.GetLength(2); z++)
                {
                    if (voxels[x, y, z]) continue;
                    
                    float maxNeighbor = 0f;
                    
                    // Check 6-direction neighbors
                    if (x > 0) maxNeighbor = Mathf.Max(maxNeighbor, lightmap[x - 1, y, z]);
                    if (x < voxels.GetLength(0) - 1) maxNeighbor = Mathf.Max(maxNeighbor, lightmap[x + 1, y, z]);
                    if (y > 0) maxNeighbor = Mathf.Max(maxNeighbor, lightmap[x, y - 1, z]);
                    if (y < voxels.GetLength(1) - 1) maxNeighbor = Mathf.Max(maxNeighbor, lightmap[x, y + 1, z]);
                    if (z > 0) maxNeighbor = Mathf.Max(maxNeighbor, lightmap[x, y, z - 1]);
                    if (z < voxels.GetLength(2) - 1) maxNeighbor = Mathf.Max(maxNeighbor, lightmap[x, y, z + 1]);
                    
                    // Apply decay
                    float propagated = Mathf.Max(0, maxNeighbor - lightDecayRate);
                    newLightmap[x, y, z] = Mathf.Max(newLightmap[x, y, z], propagated);
                }
            }
        }
        
        lightmap = newLightmap;
    }
    
    private void SmoothLightmap(bool[,,] voxels, ref float[,,] lightmap)
    {
        float[,,] smoothed = new float[voxels.GetLength(0), voxels.GetLength(1), voxels.GetLength(2)];
        
        for (int x = 0; x < voxels.GetLength(0); x++)
        {
            for (int y = 0; y < voxels.GetLength(1); y++)
            {
                for (int z = 0; z < voxels.GetLength(2); z++)
                {
                    float sum = 0;
                    int count = 0;
                    
                    // 3x3x3 kernel
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                int nx = x + dx;
                                int ny = y + dy;
                                int nz = z + dz;
                                
                                if (nx >= 0 && nx < voxels.GetLength(0) &&
                                    ny >= 0 && ny < voxels.GetLength(1) &&
                                    nz >= 0 && nz < voxels.GetLength(2) &&
                                    voxels[nx, ny, nz] == voxels[x, y, z])
                                {
                                    sum += lightmap[nx, ny, nz];
                                    count++;
                                }
                            }
                        }
                    }
                    
                    smoothed[x, y, z] = count > 0 ? sum / count : lightmap[x, y, z];
                }
            }
        }
        
        // Blend with original
        for (int x = 0; x < voxels.GetLength(0); x++)
        {
            for (int y = 0; y < voxels.GetLength(1); y++)
            {
                for (int z = 0; z < voxels.GetLength(2); z++)
                {
                    lightmap[x, y, z] = smoothed[x, y, z] * 0.7f + lightmap[x, y, z] * 0.3f;
                }
            }
        }
    }
    
    private bool[,,] GetChunkVoxels(Vector3Int chunkCoord)
    {
        // This would query the chunk manager for chunk data
        // For now, return null - implementation depends on your chunk data structure
        return null;
    }
    
    private void UpdateChunkVisuals(Vector3Int chunkCoord, float[,,] lightmap)
    {
        // This would update the chunk's mesh colors based on lighting
        // Implementation depends on your mesh generation system
    }
    
    private Vector3Int WorldToChunkCoord(Vector3 worldPos)
    {
        Vector3Int chunkSize = chunkManager.chunkSize;
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / chunkSize.x),
            Mathf.FloorToInt(worldPos.y / chunkSize.y),
            Mathf.FloorToInt(worldPos.z / chunkSize.z)
        );
    }
    
    private Vector3 ChunkCoordToWorld(Vector3Int chunkCoord)
    {
        Vector3Int chunkSize = chunkManager.chunkSize;
        return new Vector3(
            chunkCoord.x * chunkSize.x,
            chunkCoord.y * chunkSize.y,
            chunkCoord.z * chunkSize.z
        );
    }
    
    private bool IsPositionInChunk(Vector3Int localPos, int sizeX, int sizeY, int sizeZ)
    {
        return localPos.x >= 0 && localPos.x < sizeX &&
               localPos.y >= 0 && localPos.y < sizeY &&
               localPos.z >= 0 && localPos.z < sizeZ;
    }
    
    public float GetLightLevelAt(Vector3 worldPosition)
    {
        Vector3Int chunkCoord = WorldToChunkCoord(worldPosition);
        Vector3Int localPos = WorldToLocalVoxelCoord(worldPosition, chunkCoord);
        
        if (chunkLightmaps.TryGetValue(chunkCoord, out var lightmap))
        {
            if (localPos.x >= 0 && localPos.x < lightmap.GetLength(0) &&
                localPos.y >= 0 && localPos.y < lightmap.GetLength(1) &&
                localPos.z >= 0 && localPos.z < lightmap.GetLength(2))
            {
                return lightmap[localPos.x, localPos.y, localPos.z];
            }
        }
        
        return globalAmbientLight;
    }
    
    private Vector3Int WorldToLocalVoxelCoord(Vector3 worldPos, Vector3Int chunkCoord)
    {
        Vector3 chunkWorldPos = ChunkCoordToWorld(chunkCoord);
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x - chunkWorldPos.x),
            Mathf.FloorToInt(worldPos.y - chunkWorldPos.y),
            Mathf.FloorToInt(worldPos.z - chunkWorldPos.z)
        );
    }
    
    public void UpdateTimeOfDay(float time)
    {
        // Adjust sunlight based on time
        sunlightIntensity = Mathf.Lerp(0.1f, 0.8f, Mathf.Sin(time * Mathf.PI));
    }
}