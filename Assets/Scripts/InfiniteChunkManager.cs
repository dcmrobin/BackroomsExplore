using UnityEngine;
using System.Collections.Generic;

public class InfiniteChunkManager : MonoBehaviour
{
    [Header("Chunk Settings")]
    public Vector3Int chunkSize = new Vector3Int(80, 40, 80);
    [SerializeField] private int renderDistance = 3;
    [SerializeField] private bool useObjectPooling = true;
    
    [Header("Generation Settings")]
    [SerializeField] private int maxChunksPerFrame = 1;
    [SerializeField] private bool cancelDistantGeneration = true;
    
    [Header("References")]
    [SerializeField] private DungeonChunk chunkPrefab;
    [SerializeField] private Transform playerTransform;
    [SerializeField] private Material chunkMaterial;
    
    private CrossChunkRoomGenerator roomGenerator;
    
    // Chunk storage
    private Dictionary<Vector3Int, DungeonChunk> loadedChunks = new Dictionary<Vector3Int, DungeonChunk>();
    private Dictionary<Vector3Int, bool[,,]> chunkVoxelCache = new Dictionary<Vector3Int, bool[,,]>();
    private Queue<DungeonChunk> chunkPool = new Queue<DungeonChunk>();
    private Transform chunkContainer;
    
    // Generation queues with priorities
    private PriorityQueue<ChunkGenerationJob> generationQueue = new PriorityQueue<ChunkGenerationJob>();
    private HashSet<Vector3Int> currentlyGenerating = new HashSet<Vector3Int>();
    
    // State
    private Vector3Int currentPlayerChunkCoord = Vector3Int.zero;
    private Vector3Int lastPlayerChunkCoord = Vector3Int.zero;
    
    private class ChunkGenerationJob
    {
        public Vector3Int chunkCoord;
        public int priority; // Lower number = higher priority
        public float timestamp;
    }
    
    private class PriorityQueue<T> where T : ChunkGenerationJob
    {
        private List<T> items = new List<T>();
        
        public void Enqueue(T item)
        {
            items.Add(item);
            items.Sort((a, b) => a.priority.CompareTo(b.priority));
        }
        
        public bool TryDequeue(out T item)
        {
            if (items.Count == 0)
            {
                item = null;
                return false;
            }
            
            item = items[0];
            items.RemoveAt(0);
            return true;
        }
        
        public bool ContainsCoord(Vector3Int coord)
        {
            foreach (var item in items)
            {
                if (item.chunkCoord == coord) return true;
            }
            return false;
        }
        
        public void RemoveCoord(Vector3Int coord)
        {
            items.RemoveAll(item => item.chunkCoord == coord);
        }
        
        public void Clear()
        {
            items.Clear();
        }
        
        public int Count => items.Count;
    }
    
    void Start()
    {
        if (playerTransform == null)
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null) playerTransform = player.transform;
            else playerTransform = Camera.main?.transform;
        }
        
        roomGenerator = GetComponent<CrossChunkRoomGenerator>();
        if (roomGenerator == null)
            roomGenerator = gameObject.AddComponent<CrossChunkRoomGenerator>();
        
        // Initialize the generator with chunk size
        roomGenerator.Initialize(chunkSize);
        
        chunkContainer = new GameObject("Chunks").transform;
        chunkContainer.SetParent(transform);
        
        InitializeObjectPool();
        UpdateGenerationQueue();
    }
    
    void Update()
    {
        if (playerTransform == null) return;
        
        UpdatePlayerChunk();
        
        // Only update queue if player moved to new chunk
        if (currentPlayerChunkCoord != lastPlayerChunkCoord)
        {
            UpdateGenerationQueue();
            lastPlayerChunkCoord = currentPlayerChunkCoord;
        }
        
        ProcessGenerationQueue();
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
    
    private void UpdateGenerationQueue()
    {
        // Add new chunks to generate
        for (int x = -renderDistance; x <= renderDistance; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -renderDistance; z <= renderDistance; z++)
                {
                    Vector3Int chunkCoord = currentPlayerChunkCoord + new Vector3Int(x, y, z);
                    
                    // Skip if already loaded or being generated
                    if (loadedChunks.ContainsKey(chunkCoord) || 
                        currentlyGenerating.Contains(chunkCoord) ||
                        generationQueue.ContainsCoord(chunkCoord))
                        continue;
                    
                    // Calculate priority based on distance
                    int distance = GetChunkDistance(chunkCoord, currentPlayerChunkCoord);
                    int priority = CalculatePriority(distance, chunkCoord);
                    
                    generationQueue.Enqueue(new ChunkGenerationJob
                    {
                        chunkCoord = chunkCoord,
                        priority = priority,
                        timestamp = Time.time
                    });
                }
            }
        }
        
        // Remove chunks that are now out of range
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
    }
    
    private int CalculatePriority(int distance, Vector3Int chunkCoord)
    {
        // Priority levels:
        // 0: Player's current chunk
        // 1: Immediate neighbors (distance = 1)
        // 2: Chunks at distance = 2
        // 3+: Further chunks
        
        if (distance == 0) return 0;
        if (distance == 1) return 1;
        if (distance == 2) return 2;
        
        // Further chunks get lower priority
        return 3 + distance;
    }
    
    private void ProcessGenerationQueue()
    {
        int chunksProcessed = 0;
        
        while (chunksProcessed < maxChunksPerFrame && generationQueue.Count > 0)
        {
            if (generationQueue.TryDequeue(out ChunkGenerationJob job))
            {
                // Check if this chunk is still within range
                int currentDistance = GetChunkDistance(job.chunkCoord, currentPlayerChunkCoord);
                
                if (cancelDistantGeneration && currentDistance > renderDistance + 1)
                {
                    // Skip this chunk - it's now too far away
                    currentlyGenerating.Remove(job.chunkCoord);
                    continue;
                }
                
                currentlyGenerating.Add(job.chunkCoord);
                GenerateChunkImmediate(job.chunkCoord);
                currentlyGenerating.Remove(job.chunkCoord);
                chunksProcessed++;
            }
        }
    }
    
    private void GenerateChunkImmediate(Vector3Int chunkCoord)
    {
        // Quick check: is this chunk still needed?
        int currentDistance = GetChunkDistance(chunkCoord, currentPlayerChunkCoord);
        if (cancelDistantGeneration && currentDistance > renderDistance)
        {
            // Don't generate - chunk is now out of range
            return;
        }
        
        if (loadedChunks.ContainsKey(chunkCoord)) return;
        
        DungeonChunk chunk = GetChunkFromPool();
        Vector3 worldPos = ChunkCoordToWorld(chunkCoord);
        chunk.transform.position = worldPos;
        chunk.transform.SetParent(chunkContainer);
        chunk.name = $"Chunk_{chunkCoord.x}_{chunkCoord.y}_{chunkCoord.z}";
        
        // Set chunk coordinate for cross-chunk checks
        chunk.SetChunkCoord(chunkCoord);
        
        // Set material
        MeshRenderer renderer = chunk.GetComponent<MeshRenderer>();
        if (renderer != null && chunkMaterial != null)
        {
            renderer.material = new Material(chunkMaterial);
        }
        
        // Generate or retrieve chunk data
        if (!chunkVoxelCache.TryGetValue(chunkCoord, out bool[,,] voxelGrid))
        {
            voxelGrid = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
            roomGenerator.GenerateForChunk(chunkCoord, chunkSize, ref voxelGrid);
            chunkVoxelCache[chunkCoord] = voxelGrid;
        }
        
        // Generate mesh
        chunk.Initialize(chunkSize);
        chunk.GenerateMesh(voxelGrid);
        
        loadedChunks[chunkCoord] = chunk;
    }
    
    // NEW METHOD: Allows chunks to query voxel data from other chunks
    public bool TryGetVoxelData(Vector3Int chunkCoord, Vector3Int localPos, out bool isSolid)
    {
        if (chunkVoxelCache.TryGetValue(chunkCoord, out bool[,,] voxelGrid))
        {
            if (localPos.x >= 0 && localPos.x < chunkSize.x &&
                localPos.y >= 0 && localPos.y < chunkSize.y &&
                localPos.z >= 0 && localPos.z < chunkSize.z)
            {
                isSolid = voxelGrid[localPos.x, localPos.y, localPos.z];
                return true;
            }
        }
        
        isSolid = false;
        return false;
    }
    
    // NEW METHOD: Get direct access to a chunk's voxel grid
    public bool[,,] GetChunkVoxelGrid(Vector3Int chunkCoord)
    {
        if (chunkVoxelCache.TryGetValue(chunkCoord, out bool[,,] voxelGrid))
        {
            return voxelGrid;
        }
        return null;
    }
    
    // NEW METHOD: Update chunk mesh when adjacent chunks load/unload
    public void UpdateChunkBoundaryMeshes(Vector3Int chunkCoord)
    {
        if (loadedChunks.TryGetValue(chunkCoord, out DungeonChunk chunk))
        {
            // Get the voxel grid for this chunk
            if (chunkVoxelCache.TryGetValue(chunkCoord, out bool[,,] voxelGrid))
            {
                // Regenerate mesh with updated boundary information
                chunk.GenerateMesh(voxelGrid);
            }
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
            chunkVoxelCache.Remove(chunkCoord);
            
            // Update adjacent chunks that might have been relying on this chunk
            UpdateAdjacentChunks(chunkCoord);
        }
    }
    
    // NEW METHOD: Update adjacent chunks when a chunk unloads
    private void UpdateAdjacentChunks(Vector3Int unloadedChunkCoord)
    {
        Vector3Int[] directions = {
            Vector3Int.right, Vector3Int.left,
            Vector3Int.up, Vector3Int.down,
            Vector3Int.forward, Vector3Int.back
        };
        
        foreach (var dir in directions)
        {
            Vector3Int adjacentCoord = unloadedChunkCoord + dir;
            UpdateChunkBoundaryMeshes(adjacentCoord);
        }
    }
    
    private void InitializeObjectPool()
    {
        if (chunkPrefab == null)
        {
            Debug.LogError("Chunk Prefab is not assigned!");
            return;
        }
        
        for (int i = 0; i < 15; i++) // Larger pool for faster chunk swapping
        {
            DungeonChunk chunk = Instantiate(chunkPrefab);
            chunk.gameObject.SetActive(false);
            chunkPool.Enqueue(chunk);
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
    
    void OnDrawGizmosSelected()
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
    }
}