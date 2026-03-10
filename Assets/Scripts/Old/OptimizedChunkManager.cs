// File: OptimizedChunkManager.cs (UPDATED)
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public class OptimizedChunkManager : MonoBehaviour
{
    [Header("Chunk Settings")]
    public int3 chunkSize = new int3(16, 16, 16);
    [SerializeField] private int renderDistance = 3;
    [SerializeField] private bool useObjectPooling = true;
    
    [Header("Performance")]
    [SerializeField] private int maxChunksPerFrame = 1;
    [SerializeField] private int maxQueuedJobs = 4;
    [SerializeField] private bool useJobSystem = true;
    
    [Header("Collider Optimization")]
    [SerializeField] private float colliderActivationDistance = 32f;
    [SerializeField] private bool disableDistantColliders = true;
    
    [Header("Seed")]
    [SerializeField] private int worldSeed = 123456;
    [SerializeField] private bool randomizeSeed = true;
    
    [Header("References")]
    [SerializeField] private OptimizedDungeonChunk chunkPrefab; // CHANGED TO OptimizedDungeonChunk
    [SerializeField] private Transform playerTransform;
    
    private BurstRoomGenerator roomGenerator;
    private Transform chunkContainer;
    
    // Chunk management
    private Dictionary<int, OptimizedDungeonChunk> loadedChunks = new Dictionary<int, OptimizedDungeonChunk>(); // CHANGED
    private Dictionary<int, NativeArray<byte>> chunkVoxelCache = new Dictionary<int, NativeArray<byte>>();
    private Queue<OptimizedDungeonChunk> chunkPool = new Queue<OptimizedDungeonChunk>(); // CHANGED
    
    // Job management
    private Queue<ChunkGenerationJob> activeJobs = new Queue<ChunkGenerationJob>();
    private HashSet<int> currentlyGenerating = new HashSet<int>();
    private List<int3> chunksToGenerate = new List<int3>();
    
    // State
    private int3 currentPlayerChunkCoord;
    private int3 lastPlayerChunkCoord;
    
    private struct ChunkGenerationJob
    {
        public int3 chunkCoord;
        public NativeArray<byte> voxelData;
        public JobHandle jobHandle;
        public int hash;
    }
    
    void Start()
    {
        // Initialize seed
        if (randomizeSeed)
        {
            worldSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
        }
        Debug.Log($"World seed: {worldSeed}");
        
        if (playerTransform == null)
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null) playerTransform = player.transform;
            else playerTransform = Camera.main?.transform;
        }
        
        roomGenerator = GetComponent<BurstRoomGenerator>();
        if (roomGenerator == null)
            roomGenerator = gameObject.AddComponent<BurstRoomGenerator>();
        
        roomGenerator.Initialize(chunkSize, worldSeed);
        
        chunkContainer = new GameObject("Chunks").transform;
        chunkContainer.SetParent(transform);
        
        InitializeObjectPool();
    }
    
    void OnDestroy()
    {
        // Complete all jobs
        roomGenerator.CompleteJobs();
        
        // Clean up NativeArrays
        foreach (var job in activeJobs)
        {
            if (job.voxelData.IsCreated)
                job.voxelData.Dispose();
        }
        
        foreach (var kvp in chunkVoxelCache)
        {
            if (kvp.Value.IsCreated)
                kvp.Value.Dispose();
        }
        chunkVoxelCache.Clear();
    }
    
    void Update()
    {
        if (playerTransform == null) return;
        
        // Update less frequently
        if (Time.frameCount % 2 == 0)
        {
            UpdatePlayerChunk();
            
            if (!currentPlayerChunkCoord.Equals(lastPlayerChunkCoord))
            {
                UpdateGenerationQueue();
                lastPlayerChunkCoord = currentPlayerChunkCoord;
            }
        }
        
        ProcessJobs();
        UpdateColliders();
    }
    
    void LateUpdate()
    {
        ProcessCompletedJobs();
    }
    
    private void UpdatePlayerChunk()
    {
        Vector3 playerPos = playerTransform.position;
        currentPlayerChunkCoord = WorldToChunkCoord(playerPos);
    }
    
    private void UpdateGenerationQueue()
    {
        chunksToGenerate.Clear();
        
        // Only generate chunks at player level and one above/below
        for (int x = -renderDistance; x <= renderDistance; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -renderDistance; z <= renderDistance; z++)
                {
                    int3 chunkCoord = currentPlayerChunkCoord + new int3(x, y, z);
                    int chunkHash = HashCoordinate(chunkCoord);
                    
                    if (!loadedChunks.ContainsKey(chunkHash) && 
                        !currentlyGenerating.Contains(chunkHash))
                    {
                        chunksToGenerate.Add(chunkCoord);
                    }
                }
            }
        }
        
        // Sort by distance to player
        chunksToGenerate.Sort((a, b) =>
        {
            float distA = math.distance(a, currentPlayerChunkCoord);
            float distB = math.distance(b, currentPlayerChunkCoord);
            return distA.CompareTo(distB);
        });
        
        // Unload distant chunks
        UnloadDistantChunks();
    }
    
    private void ProcessJobs()
    {
        // Start new jobs if we have capacity
        while (activeJobs.Count < maxQueuedJobs && 
               chunksToGenerate.Count > 0)
        {
            int3 chunkCoord = chunksToGenerate[0];
            chunksToGenerate.RemoveAt(0);
            
            StartChunkGenerationJob(chunkCoord);
        }
    }
    
    private void StartChunkGenerationJob(int3 chunkCoord)
    {
        int chunkHash = HashCoordinate(chunkCoord);
        
        if (currentlyGenerating.Contains(chunkHash) || loadedChunks.ContainsKey(chunkHash))
            return;
        
        int voxelCount = chunkSize.x * chunkSize.y * chunkSize.z;
        var voxelData = new NativeArray<byte>(voxelCount, Allocator.Persistent);
        
        JobHandle jobHandle;
        if (useJobSystem)
        {
            jobHandle = roomGenerator.ScheduleChunkGeneration(chunkCoord, voxelData);
        }
        else
        {
            // Fallback to immediate generation
            roomGenerator.CompleteJobs();
            GenerateChunkImmediate(chunkCoord, voxelData);
            jobHandle = default;
        }
        
        activeJobs.Enqueue(new ChunkGenerationJob
        {
            chunkCoord = chunkCoord,
            voxelData = voxelData,
            jobHandle = jobHandle,
            hash = chunkHash
        });
        
        currentlyGenerating.Add(chunkHash);
    }
    
    private void ProcessCompletedJobs()
    {
        // Process a limited number per frame
        int processed = 0;
        while (activeJobs.Count > 0 && processed < maxChunksPerFrame)
        {
            var job = activeJobs.Peek();
            if (job.jobHandle.IsCompleted)
            {
                activeJobs.Dequeue();
                job.jobHandle.Complete();
                
                if (!loadedChunks.ContainsKey(job.hash))
                {
                    InstantiateChunk(job.chunkCoord, job.voxelData, job.hash);
                }
                
                chunkVoxelCache[job.hash] = job.voxelData;
                currentlyGenerating.Remove(job.hash);
                processed++;
            }
            else
            {
                break;
            }
        }
    }
    
    private void GenerateChunkImmediate(int3 chunkCoord, NativeArray<byte> voxelData)
    {
        // Clear the array
        int voxelCount = chunkSize.x * chunkSize.y * chunkSize.z;
        for (int i = 0; i < voxelCount; i++)
        {
            voxelData[i] = 0;
        }
        
        // Fallback: fill bottom layer for testing
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int z = 0; z < chunkSize.z; z++)
            {
                int index = x * (chunkSize.y * chunkSize.z) + 0 * chunkSize.z + z;
                if (index < voxelData.Length)
                    voxelData[index] = 1;
            }
        }
    }
    
    private void InstantiateChunk(int3 chunkCoord, NativeArray<byte> voxelData, int chunkHash)
    {
        OptimizedDungeonChunk chunk = GetChunkFromPool();
        if (chunk == null) return;
        
        Vector3 worldPos = ChunkCoordToWorld(chunkCoord);
        chunk.transform.position = worldPos;
        chunk.transform.SetParent(chunkContainer);
        chunk.name = $"Chunk_{chunkCoord.x}_{chunkCoord.y}_{chunkCoord.z}";
        
        // Convert int3 to Vector3Int for compatibility
        Vector3Int chunkCoordV3 = new Vector3Int(chunkCoord.x, chunkCoord.y, chunkCoord.z);
        chunk.SetChunkCoord(chunkCoordV3, worldSeed);
        
        // Convert int3 to Vector3Int for chunk size
        Vector3Int chunkSizeV3 = new Vector3Int(chunkSize.x, chunkSize.y, chunkSize.z);
        chunk.Initialize(chunkSizeV3);
        
        // Generate mesh
        chunk.GenerateMesh(voxelData);
        
        loadedChunks[chunkHash] = chunk;
    }
    
    private void UpdateColliders()
    {
        if (!disableDistantColliders || playerTransform == null) return;
        
        Vector3 playerPos = playerTransform.position;
        
        foreach (var kvp in loadedChunks)
        {
            var chunk = kvp.Value;
            float distance = Vector3.Distance(chunk.transform.position + 
                new Vector3(chunkSize.x / 2f, chunkSize.y / 2f, chunkSize.z / 2f), 
                playerPos);
            
            chunk.SetColliderEnabled(distance <= colliderActivationDistance);
        }
    }
    
    private void UnloadDistantChunks()
    {
        List<int> toUnload = new List<int>();
        
        foreach (var kvp in loadedChunks)
        {
            var chunk = kvp.Value;
            Vector3Int chunkWorldPos = Vector3Int.FloorToInt(chunk.transform.position);
            int3 chunkCoord = WorldToChunkCoord(chunkWorldPos);
            float distance = math.distance(chunkCoord, currentPlayerChunkCoord);
            
            if (distance > renderDistance)
            {
                toUnload.Add(kvp.Key);
            }
        }
        
        foreach (var hash in toUnload)
        {
            UnloadChunk(hash);
        }
    }
    
    private void UnloadChunk(int chunkHash)
    {
        if (loadedChunks.TryGetValue(chunkHash, out OptimizedDungeonChunk chunk))
        {
            ReturnChunkToPool(chunk);
            loadedChunks.Remove(chunkHash);
            
            if (chunkVoxelCache.TryGetValue(chunkHash, out NativeArray<byte> voxelData))
            {
                if (voxelData.IsCreated)
                    voxelData.Dispose();
                chunkVoxelCache.Remove(chunkHash);
            }
        }
    }
    
    // Helper methods
    private int3 WorldToChunkCoord(Vector3 worldPos)
    {
        return new int3(
            Mathf.FloorToInt(worldPos.x / chunkSize.x),
            Mathf.FloorToInt(worldPos.y / chunkSize.y),
            Mathf.FloorToInt(worldPos.z / chunkSize.z)
        );
    }
    
    private Vector3 ChunkCoordToWorld(int3 chunkCoord)
    {
        return new Vector3(
            chunkCoord.x * chunkSize.x,
            chunkCoord.y * chunkSize.y,
            chunkCoord.z * chunkSize.z
        );
    }
    
    private int HashCoordinate(int3 coord)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + coord.x;
            hash = hash * 31 + coord.y;
            hash = hash * 31 + coord.z;
            return hash;
        }
    }
    
    private void InitializeObjectPool()
    {
        if (chunkPrefab == null)
        {
            Debug.LogError("Chunk Prefab is not assigned!");
            return;
        }
        
        for (int i = 0; i < 20; i++)
        {
            OptimizedDungeonChunk chunk = Instantiate(chunkPrefab);
            chunk.gameObject.SetActive(false);
            chunkPool.Enqueue(chunk);
        }
    }
    
    private OptimizedDungeonChunk GetChunkFromPool()
    {
        if (useObjectPooling && chunkPool.Count > 0)
        {
            OptimizedDungeonChunk chunk = chunkPool.Dequeue();
            chunk.gameObject.SetActive(true);
            return chunk;
        }
        else if (useObjectPooling)
        {
            // Create more if pool is empty
            OptimizedDungeonChunk chunk = Instantiate(chunkPrefab);
            return chunk;
        }
        else
        {
            return Instantiate(chunkPrefab);
        }
    }
    
    private void ReturnChunkToPool(OptimizedDungeonChunk chunk)
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
    
    // Public interface for chunks to query adjacent chunk data
    public bool TryGetVoxelData(Vector3Int chunkCoord, Vector3Int localPos, out bool isSolid)
    {
        int3 coord = new int3(chunkCoord.x, chunkCoord.y, chunkCoord.z);
        int hash = HashCoordinate(coord);
        
        if (chunkVoxelCache.TryGetValue(hash, out NativeArray<byte> voxelData))
        {
            if (localPos.x >= 0 && localPos.x < chunkSize.x &&
                localPos.y >= 0 && localPos.y < chunkSize.y &&
                localPos.z >= 0 && localPos.z < chunkSize.z)
            {
                int index = localPos.x * (chunkSize.y * chunkSize.z) + 
                           localPos.y * chunkSize.z + 
                           localPos.z;
                isSolid = voxelData[index] != 0;
                return true;
            }
        }
        
        isSolid = false;
        return false;
    }
    
    // Debug methods
    public int GetLoadedChunkCount() => loadedChunks.Count;
    public int GetActiveJobCount() => activeJobs.Count;
}