using UnityEngine;
using System.Collections.Generic;
using System.Collections.Concurrent;

public class InfiniteChunkManager : MonoBehaviour
{
    [Header("Chunk Settings")]
    public Vector3Int chunkSize = new Vector3Int(80, 40, 80); // Match original size
    [SerializeField] private int renderDistance = 3;
    [SerializeField] private bool useObjectPooling = true;
    
    [Header("References")]
    [SerializeField] private DungeonChunk chunkPrefab;
    [SerializeField] private Transform playerTransform;
    [SerializeField] private Material chunkMaterial;
    
    private RoomAndCorridorGenerator roomGenerator;
    
    // Chunk storage
    private Dictionary<Vector3Int, DungeonChunk> loadedChunks = new Dictionary<Vector3Int, DungeonChunk>();
    private Dictionary<Vector3Int, ChunkData> chunkDataCache = new Dictionary<Vector3Int, ChunkData>();
    private Queue<DungeonChunk> chunkPool = new Queue<DungeonChunk>();
    private Transform chunkContainer;
    
    // State
    private Vector3Int currentPlayerChunkCoord = Vector3Int.zero;
    private ConcurrentQueue<Vector3Int> chunksToGenerate = new ConcurrentQueue<Vector3Int>();
    private HashSet<Vector3Int> generatingChunks = new HashSet<Vector3Int>();
    
    private class ChunkData
    {
        public bool[,,] voxelGrid;
        public bool[,,] noiseGrid;
        public bool isGenerated = false;
    }
    
    void Start()
    {
        if (playerTransform == null)
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null) playerTransform = player.transform;
            else playerTransform = Camera.main?.transform;
        }
        
        roomGenerator = GetComponent<RoomAndCorridorGenerator>();
        if (roomGenerator == null)
            roomGenerator = gameObject.AddComponent<RoomAndCorridorGenerator>();
        
        chunkContainer = new GameObject("Chunks").transform;
        chunkContainer.SetParent(transform);
        
        InitializeObjectPool();
        GenerateInitialChunks();
    }
    
    void Update()
    {
        if (playerTransform == null) return;
        
        UpdatePlayerChunk();
        ManageChunks();
        GenerateQueuedChunks();
    }
    
    private void UpdatePlayerChunk()
    {
        Vector3 playerPos = playerTransform.position;
        Vector3Int newChunkCoord = WorldToChunkCoord(playerPos);
        
        if (newChunkCoord != currentPlayerChunkCoord)
        {
            currentPlayerChunkCoord = newChunkCoord;
        }
    }
    
    private Vector3Int WorldToChunkCoord(Vector3 worldPos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / chunkSize.x),
            Mathf.FloorToInt(worldPos.y / chunkSize.y),
            Mathf.FloorToInt(worldPos.z / chunkSize.z)
        );
    }
    
    private Vector3 ChunkCoordToWorld(Vector3Int chunkCoord)
    {
        return new Vector3(
            chunkCoord.x * chunkSize.x,
            chunkCoord.y * chunkSize.y,
            chunkCoord.z * chunkSize.z
        );
    }
    
    private void ManageChunks()
    {
        // Unload distant chunks
        List<Vector3Int> chunksToUnload = new List<Vector3Int>();
        foreach (var kvp in loadedChunks)
        {
            int distance = GetChunkDistance(kvp.Key, currentPlayerChunkCoord);
            if (distance > renderDistance)
            {
                chunksToUnload.Add(kvp.Key);
            }
        }
        
        foreach (var coord in chunksToUnload)
        {
            UnloadChunk(coord);
        }
        
        // Queue new chunks
        for (int x = -renderDistance; x <= renderDistance; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -renderDistance; z <= renderDistance; z++)
                {
                    Vector3Int chunkCoord = currentPlayerChunkCoord + new Vector3Int(x, y, z);
                    
                    if (!loadedChunks.ContainsKey(chunkCoord) && 
                        !generatingChunks.Contains(chunkCoord))
                    {
                        chunksToGenerate.Enqueue(chunkCoord);
                        generatingChunks.Add(chunkCoord);
                    }
                }
            }
        }
    }
    
    private void GenerateQueuedChunks()
    {
        if (chunksToGenerate.Count == 0) return;
        
        // Generate up to 1 chunk per frame (room generation is heavier)
        if (chunksToGenerate.TryDequeue(out Vector3Int chunkCoord))
        {
            GenerateChunkImmediate(chunkCoord);
            generatingChunks.Remove(chunkCoord);
        }
    }
    
    private void GenerateChunkImmediate(Vector3Int chunkCoord)
    {
        if (loadedChunks.ContainsKey(chunkCoord)) return;
        
        DungeonChunk chunk = GetChunkFromPool();
        Vector3 worldPos = ChunkCoordToWorld(chunkCoord);
        chunk.transform.position = worldPos;
        chunk.transform.SetParent(chunkContainer);
        chunk.name = $"Chunk_{chunkCoord.x}_{chunkCoord.y}_{chunkCoord.z}";
        
        // Set material
        MeshRenderer renderer = chunk.GetComponent<MeshRenderer>();
        if (renderer != null && chunkMaterial != null)
        {
            renderer.material = new Material(chunkMaterial);
        }
        
        // Generate or retrieve chunk data
        if (!chunkDataCache.TryGetValue(chunkCoord, out ChunkData data))
        {
            data = new ChunkData();
            GenerateChunkWithRooms(chunkCoord, data);
            chunkDataCache[chunkCoord] = data;
        }
        
        // Generate mesh
        chunk.Initialize(chunkSize);
        chunk.GenerateMesh(data.voxelGrid);
        
        loadedChunks[chunkCoord] = chunk;
    }
    
    private void GenerateChunkWithRooms(Vector3Int chunkCoord, ChunkData chunkData)
    {
        // Get neighbor noise data for continuity
        Dictionary<Vector3Int, bool[,,]> neighborNoiseGrids = GetNeighborNoiseGrids(chunkCoord);
        
        // Let the room generator handle everything
        roomGenerator.GenerateForChunk(chunkCoord, chunkSize, ref chunkData.voxelGrid, neighborNoiseGrids);
        chunkData.isGenerated = true;
        
        // Debug info
        int solidCount = 0;
        for (int x = 0; x < chunkSize.x; x++)
            for (int y = 0; y < chunkSize.y; y++)
                for (int z = 0; z < chunkSize.z; z++)
                    if (chunkData.voxelGrid[x, y, z]) solidCount++;
        
        Debug.Log($"Chunk {chunkCoord}: {solidCount} solid voxels, {roomGenerator.GetRoomsAffectingChunk(chunkCoord).Count} rooms");
    }

    private Dictionary<Vector3Int, bool[,,]> GetNeighborNoiseGrids(Vector3Int chunkCoord)
    {
        var neighborNoiseGrids = new Dictionary<Vector3Int, bool[,,]>();
        
        // Check adjacent chunks for their noise data
        Vector3Int[] neighborOffsets = {
            new Vector3Int(-1, 0, 0), new Vector3Int(1, 0, 0),
            new Vector3Int(0, -1, 0), new Vector3Int(0, 1, 0),
            new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 1)
        };
        
        foreach (var offset in neighborOffsets)
        {
            Vector3Int neighborCoord = chunkCoord + offset;
            if (chunkDataCache.TryGetValue(neighborCoord, out ChunkData neighborData))
            {
                neighborNoiseGrids[offset] = neighborData.noiseGrid;
            }
        }
        
        return neighborNoiseGrids;
    }
    
    private int GetChunkDistance(Vector3Int a, Vector3Int b)
    {
        return Mathf.Max(
            Mathf.Abs(a.x - b.x),
            Mathf.Abs(a.y - b.y),
            Mathf.Abs(a.z - b.z)
        );
    }
    
    private void UnloadChunk(Vector3Int chunkCoord)
    {
        if (loadedChunks.TryGetValue(chunkCoord, out DungeonChunk chunk))
        {
            ReturnChunkToPool(chunk);
            loadedChunks.Remove(chunkCoord);
            
            // Clear generator data for this chunk
            roomGenerator.ClearChunkData(chunkCoord);
        }
    }
    
    private void InitializeObjectPool()
    {
        if (chunkPrefab == null)
        {
            Debug.LogError("Chunk Prefab is not assigned!");
            return;
        }
        
        for (int i = 0; i < 10; i++)
        {
            DungeonChunk chunk = Instantiate(chunkPrefab);
            chunk.gameObject.SetActive(false);
            chunkPool.Enqueue(chunk);
        }
    }
    
    private void GenerateInitialChunks()
    {
        // Generate initial area
        for (int x = -1; x <= 1; x++)
        {
            for (int z = -1; z <= 1; z++)
            {
                Vector3Int coord = new Vector3Int(x, 0, z);
                if (!generatingChunks.Contains(coord))
                {
                    chunksToGenerate.Enqueue(coord);
                    generatingChunks.Add(coord);
                }
            }
        }
    }
    
    private DungeonChunk GetChunkFromPool()
    {
        if (useObjectPooling && chunkPool.Count > 0)
        {
            DungeonChunk chunk = chunkPool.Dequeue();
            chunk.gameObject.SetActive(true);
            return chunk;
        }
        else
        {
            DungeonChunk chunk = Instantiate(chunkPrefab);
            return chunk;
        }
    }
    
    private void ReturnChunkToPool(DungeonChunk chunk)
    {
        if (useObjectPooling)
        {
            chunk.Clear();
            chunk.gameObject.SetActive(false);
            chunkPool.Enqueue(chunk);
        }
        else
        {
            Destroy(chunk.gameObject);
        }
    }
    
    /*void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;
        
        // Draw loaded chunks
        Gizmos.color = new Color(0, 1, 0, 0.3f);
        foreach (var kvp in loadedChunks)
        {
            Vector3 center = ChunkCoordToWorld(kvp.Key) + (Vector3)chunkSize * 0.5f;
            Gizmos.DrawWireCube(center, chunkSize);
        }
        
        // Draw player chunk
        Gizmos.color = Color.red;
        Vector3 playerChunkCenter = ChunkCoordToWorld(currentPlayerChunkCoord) + (Vector3)chunkSize * 0.5f;
        Gizmos.DrawWireCube(playerChunkCenter, chunkSize);
        
        // Draw rooms (optional - can be heavy)
        if (roomGenerator != null)
        {
            Gizmos.color = new Color(1, 0.5f, 0, 0.4f);
            var rooms = roomGenerator.GetRoomsInChunk(currentPlayerChunkCoord);
            foreach (var room in rooms)
            {
                Vector3 worldCenter = ChunkCoordToWorld(room.chunkCoord) + room.LocalBounds.center;
                Gizmos.DrawWireCube(worldCenter, room.LocalBounds.size);
            }
        }
    }*/
}