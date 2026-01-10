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
    [SerializeField] private bool asyncGeneration = false;
    
    [Header("Seed Settings")]
    [SerializeField] private int worldSeed = 123456;
    [SerializeField] private bool randomizeSeed = true;
    
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
    private BinaryHeap<ChunkGenerationJob> generationQueue = new BinaryHeap<ChunkGenerationJob>();
    private HashSet<Vector3Int> currentlyGenerating = new HashSet<Vector3Int>();
    
    // State
    private Vector3Int currentPlayerChunkCoord = Vector3Int.zero;
    private Vector3Int lastPlayerChunkCoord = Vector3Int.zero;
    
    // Track chunks that need boundary updates
    private HashSet<Vector3Int> chunksNeedingBoundaryUpdate = new HashSet<Vector3Int>();
    
    private class ChunkGenerationJob : System.IComparable<ChunkGenerationJob>
    {
        public Vector3Int chunkCoord;
        public int priority; // Lower number = higher priority
        public float timestamp;
        
        public int CompareTo(ChunkGenerationJob other)
        {
            // Primary sort by priority, secondary by timestamp (FIFO for same priority)
            int priorityCompare = priority.CompareTo(other.priority);
            if (priorityCompare != 0)
                return priorityCompare;
            return timestamp.CompareTo(other.timestamp);
        }
    }
    
    private class BinaryHeap<T> where T : System.IComparable<T>
    {
        private List<T> heap = new List<T>();
        
        public void Enqueue(T item)
        {
            heap.Add(item);
            int i = heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (heap[i].CompareTo(heap[parent]) >= 0)
                    break;
                    
                // Swap
                T temp = heap[i];
                heap[i] = heap[parent];
                heap[parent] = temp;
                i = parent;
            }
        }
        
        public bool TryDequeue(out T item)
        {
            if (heap.Count == 0)
            {
                item = default;
                return false;
            }
            
            item = heap[0];
            int lastIndex = heap.Count - 1;
            heap[0] = heap[lastIndex];
            heap.RemoveAt(lastIndex);
            
            if (heap.Count > 0)
                Heapify(0);
                
            return true;
        }
        
        private void Heapify(int i)
        {
            int smallest = i;
            int left = 2 * i + 1;
            int right = 2 * i + 2;
            
            if (left < heap.Count && heap[left].CompareTo(heap[smallest]) < 0)
                smallest = left;
                
            if (right < heap.Count && heap[right].CompareTo(heap[smallest]) < 0)
                smallest = right;
                
            if (smallest != i)
            {
                T temp = heap[i];
                heap[i] = heap[smallest];
                heap[smallest] = temp;
                Heapify(smallest);
            }
        }
        
        public bool ContainsCoord(Vector3Int coord)
        {
            foreach (var item in heap)
            {
                if (item is ChunkGenerationJob job && job.chunkCoord == coord)
                    return true;
            }
            return false;
        }
        
        public void RemoveCoord(Vector3Int coord)
        {
            for (int i = 0; i < heap.Count; i++)
            {
                if (heap[i] is ChunkGenerationJob job && job.chunkCoord == coord)
                {
                    heap[i] = heap[heap.Count - 1];
                    heap.RemoveAt(heap.Count - 1);
                    
                    if (i < heap.Count)
                        Heapify(i);
                    break;
                }
            }
        }
        
        public void Clear()
        {
            heap.Clear();
        }
        
        public int Count => heap.Count;
    }
    
    void Start()
    {
        // Initialize seed
        if (randomizeSeed)
        {
            worldSeed = Random.Range(int.MinValue, int.MaxValue);
        }
        Debug.Log($"World seed: {worldSeed}");
        
        if (playerTransform == null)
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null) playerTransform = player.transform;
            else playerTransform = Camera.main?.transform;
        }
        
        roomGenerator = GetComponent<CrossChunkRoomGenerator>();
        if (roomGenerator == null)
            roomGenerator = gameObject.AddComponent<CrossChunkRoomGenerator>();
        
        roomGenerator.Initialize(chunkSize, worldSeed);
        
        chunkContainer = new GameObject("Chunks").transform;
        chunkContainer.SetParent(transform);
        
        InitializeObjectPool();
        UpdateGenerationQueue();
    }
    
    void Update()
    {
        if (playerTransform == null) return;
        
        UpdatePlayerChunk();
        
        if (currentPlayerChunkCoord != lastPlayerChunkCoord)
        {
            UpdateGenerationQueue();
            lastPlayerChunkCoord = currentPlayerChunkCoord;
            
            if (GetChunkDistance(currentPlayerChunkCoord, lastPlayerChunkCoord) > 2)
            {
                roomGenerator.PruneDistantData(currentPlayerChunkCoord);
            }
        }
        
        ProcessGenerationQueue();
        
        ProcessBoundaryUpdates();
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
        generationQueue.Clear();
        
        for (int x = -renderDistance; x <= renderDistance; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -renderDistance; z <= renderDistance; z++)
                {
                    Vector3Int chunkCoord = currentPlayerChunkCoord + new Vector3Int(x, y, z);
                    
                    if (loadedChunks.ContainsKey(chunkCoord) || 
                        currentlyGenerating.Contains(chunkCoord))
                        continue;
                    
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
        if (distance == 0) return 0;
        if (distance == 1) return 1;
        if (distance == 2) return 2;
        return 3 + distance;
    }
    
    private void ProcessGenerationQueue()
    {
        int chunksProcessed = 0;
        
        while (chunksProcessed < maxChunksPerFrame && generationQueue.Count > 0)
        {
            if (generationQueue.TryDequeue(out ChunkGenerationJob job))
            {
                int currentDistance = GetChunkDistance(job.chunkCoord, currentPlayerChunkCoord);
                
                if (cancelDistantGeneration && currentDistance > renderDistance + 1)
                {
                    currentlyGenerating.Remove(job.chunkCoord);
                    continue;
                }
                
                currentlyGenerating.Add(job.chunkCoord);
                
                if (asyncGeneration)
                {
                    GenerateChunkImmediate(job.chunkCoord);
                }
                else
                {
                    GenerateChunkImmediate(job.chunkCoord);
                }
                
                currentlyGenerating.Remove(job.chunkCoord);
                chunksProcessed++;
            }
        }
    }
    
    private void ProcessBoundaryUpdates()
    {
        if (chunksNeedingBoundaryUpdate.Count == 0) return;
        
        int maxUpdates = Mathf.Min(3, chunksNeedingBoundaryUpdate.Count);
        List<Vector3Int> toProcess = new List<Vector3Int>();
        
        foreach (var coord in chunksNeedingBoundaryUpdate)
        {
            toProcess.Add(coord);
            if (toProcess.Count >= maxUpdates) break;
        }
        
        foreach (var coord in toProcess)
        {
            chunksNeedingBoundaryUpdate.Remove(coord);
            UpdateChunkBoundaryMeshes(coord);
        }
    }
    
    private void GenerateChunkImmediate(Vector3Int chunkCoord)
    {
        int currentDistance = GetChunkDistance(chunkCoord, currentPlayerChunkCoord);
        if (cancelDistantGeneration && currentDistance > renderDistance)
            return;
        
        if (loadedChunks.ContainsKey(chunkCoord)) return;
        
        DungeonChunk chunk = GetChunkFromPool();
        if (chunk == null)
        {
            Debug.LogWarning("Failed to get chunk from pool");
            return;
        }
        
        try
        {
            Vector3 worldPos = ChunkCoordToWorld(chunkCoord);
            chunk.transform.position = worldPos;
            chunk.transform.SetParent(chunkContainer);
            chunk.name = $"Chunk_{chunkCoord.x}_{chunkCoord.y}_{chunkCoord.z}";
            
            chunk.SetChunkCoord(chunkCoord, worldSeed);
            
            MeshRenderer renderer = chunk.GetComponent<MeshRenderer>();
            if (renderer != null && chunkMaterial != null)
            {
                renderer.material = new Material(chunkMaterial);
            }
            
            if (!chunkVoxelCache.TryGetValue(chunkCoord, out bool[,,] voxelGrid))
            {
                voxelGrid = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
                roomGenerator.GenerateForChunk(chunkCoord, chunkSize, ref voxelGrid);
                chunkVoxelCache[chunkCoord] = voxelGrid;
                
                MarkAdjacentChunksForUpdate(chunkCoord);
            }
            
            chunk.Initialize(chunkSize);
            chunk.GenerateMesh(voxelGrid);
            
            loadedChunks[chunkCoord] = chunk;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error generating chunk {chunkCoord}: {e.Message}");
            ReturnChunkToPool(chunk);
        }
    }
    
    private void MarkAdjacentChunksForUpdate(Vector3Int newChunkCoord)
    {
        Vector3Int[] directions = {
            Vector3Int.right, Vector3Int.left,
            Vector3Int.up, Vector3Int.down,
            Vector3Int.forward, Vector3Int.back
        };
        
        foreach (var dir in directions)
        {
            Vector3Int adjacentCoord = newChunkCoord + dir;
            if (loadedChunks.ContainsKey(adjacentCoord))
            {
                chunksNeedingBoundaryUpdate.Add(adjacentCoord);
            }
        }
    }
    
    public bool TryGetVoxelData(Vector3Int chunkCoord, Vector3Int localPos, out bool isSolid)
    {
        if (currentlyGenerating.Contains(chunkCoord))
        {
            isSolid = true;
            return false;
        }
        
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
    
    public void UpdateChunkBoundaryMeshes(Vector3Int chunkCoord)
    {
        if (loadedChunks.TryGetValue(chunkCoord, out DungeonChunk chunk))
        {
            if (chunkVoxelCache.TryGetValue(chunkCoord, out bool[,,] voxelGrid))
            {
                chunk.GenerateMesh(voxelGrid);
            }
        }
    }
    
    public Vector3Int WorldToChunkCoord(Vector3 worldPos)
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
            
            roomGenerator.ClearChunkData(chunkCoord);
            chunkVoxelCache.Remove(chunkCoord);
            
            UpdateAdjacentChunks(chunkCoord);
        }
    }
    
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
            chunksNeedingBoundaryUpdate.Add(adjacentCoord);
        }
    }
    
    private void InitializeObjectPool()
    {
        if (chunkPrefab == null)
        {
            Debug.LogError("Chunk Prefab is not assigned!");
            return;
        }
        
        for (int i = 0; i < 15; i++)
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
        else if (useObjectPooling)
        {
            DungeonChunk chunk = Instantiate(chunkPrefab);
            return chunk;
        }
        else
        {
            return Instantiate(chunkPrefab);
        }
    }
    
    private void ReturnChunkToPool(DungeonChunk chunk)
    {
        if (chunk == null) return;
        
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
}