using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Burst;

public class InfiniteChunkManager : MonoBehaviour
{
    private static readonly Vector3Int[] CardinalDirections =
    {
        Vector3Int.right, Vector3Int.left,
        Vector3Int.up, Vector3Int.down,
        Vector3Int.forward, Vector3Int.back
    };

    [Header("Chunk Settings")]
    public Vector3Int chunkSize = new Vector3Int(80, 40, 80);
    [SerializeField] private int renderDistance = 3;
    [SerializeField] private bool useObjectPooling = true;

    [Header("Generation Settings")]
    // How many finished chunks to upload to the GPU per frame.
    // 1 is recommended — each upload still costs a few ms even with the fast API.
    [SerializeField] private int maxMeshUploadsPerFrame = 1;
    // Max simultaneous background generation threads.
    [SerializeField] private int maxConcurrentGenerations = 4;
    [SerializeField] private bool cancelDistantGeneration = true;

    [Header("Seed Settings")]
    [SerializeField] private int worldSeed = 123456;
    [SerializeField] private bool randomizeSeed = true;

    [Header("References")]
    [SerializeField] private DungeonChunk chunkPrefab;
    [SerializeField] private Transform    playerTransform;
    [SerializeField] private Material     chunkMaterial;

    private CrossChunkRoomGenerator roomGenerator;

    // All accessed only on main thread
    private Dictionary<Vector3Int, DungeonChunk>      loadedChunks    = new Dictionary<Vector3Int, DungeonChunk>();
    private Dictionary<Vector3Int, NativeArray<byte>> chunkVoxelCache = new Dictionary<Vector3Int, NativeArray<byte>>();
    private Queue<DungeonChunk> chunkPool = new Queue<DungeonChunk>();
    private Transform chunkContainer;

    // Generation pipeline
    private PriorityQueue<ChunkGenerationTask> generationQueue     = new PriorityQueue<ChunkGenerationTask>();
    private HashSet<Vector3Int>                currentlyGenerating = new HashSet<Vector3Int>();

    // Background threads push finished results here; main thread drains each frame
    private readonly Queue<ChunkBuildResult> readyToUpload         = new Queue<ChunkBuildResult>();
    private readonly Queue<ChunkBuildResult> readyToUploadBoundary = new Queue<ChunkBuildResult>();
    private readonly object                  uploadLock            = new object();

    // Tracks chunks currently being rebuilt as boundary updates (separate from fresh generation)
    private HashSet<Vector3Int> currentlyRebuildingBoundary = new HashSet<Vector3Int>();

    private Vector3Int currentPlayerChunkCoord = Vector3Int.zero;
    private Vector3Int lastPlayerChunkCoord    = Vector3Int.zero;

    private HashSet<Vector3Int>       chunksNeedingBoundaryUpdate = new HashSet<Vector3Int>();
    private readonly List<Vector3Int> chunksToUnloadBuffer        = new List<Vector3Int>();
    private readonly List<Vector3Int> boundaryUpdateBuffer        = new List<Vector3Int>();

    // -------------------------------------------------------------------------
    // Inner types
    // -------------------------------------------------------------------------

    private class ChunkGenerationTask : System.IComparable<ChunkGenerationTask>
    {
        public Vector3Int chunkCoord;
        public int   priority;
        public float timestamp;

        public int CompareTo(ChunkGenerationTask other)
        {
            int pc = priority.CompareTo(other.priority);
            return pc != 0 ? pc : timestamp.CompareTo(other.timestamp);
        }
    }

    // Everything computed off main thread; uploaded on main thread
    public class ChunkBuildResult
    {
        public Vector3Int        chunkCoord;
        public DungeonChunk      chunk;
        public NativeArray<byte> voxelData;
        public DungeonChunk.MeshData meshData;
        public bool              success;
    }

    // -------------------------------------------------------------------------
    // Priority queue
    // -------------------------------------------------------------------------

    private class PriorityQueue<T> where T : System.IComparable<T>
    {
        private List<T> heap = new List<T>();

        public void Enqueue(T item)
        {
            heap.Add(item);
            int i = heap.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (heap[i].CompareTo(heap[p]) >= 0) break;
                Swap(i, p); i = p;
            }
        }

        public bool TryDequeue(out T item)
        {
            if (heap.Count == 0) { item = default; return false; }
            item = heap[0];
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            if (heap.Count > 0) Heapify(0);
            return true;
        }

        private void Heapify(int i)
        {
            int s = i, l = 2*i+1, r = 2*i+2;
            if (l < heap.Count && heap[l].CompareTo(heap[s]) < 0) s = l;
            if (r < heap.Count && heap[r].CompareTo(heap[s]) < 0) s = r;
            if (s != i) { Swap(i, s); Heapify(s); }
        }

        private void Swap(int a, int b) { T t = heap[a]; heap[a] = heap[b]; heap[b] = t; }

        public bool ContainsCoord(Vector3Int c)
        {
            foreach (var item in heap)
                if (item is ChunkGenerationTask t && t.chunkCoord == c) return true;
            return false;
        }

        public void RemoveCoord(Vector3Int c)
        {
            for (int i = 0; i < heap.Count; i++)
            {
                if (heap[i] is ChunkGenerationTask t && t.chunkCoord == c)
                {
                    heap[i] = heap[heap.Count - 1];
                    heap.RemoveAt(heap.Count - 1);
                    if (i < heap.Count) Heapify(i);
                    return;
                }
            }
        }

        public void Clear() => heap.Clear();
        public int Count    => heap.Count;
    }

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    void Start()
    {
        if (randomizeSeed)
            worldSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
        Debug.Log($"World seed: {worldSeed}");

        if (playerTransform == null)
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            playerTransform = player != null ? player.transform : Camera.main?.transform;
        }

        roomGenerator = GetComponent<CrossChunkRoomGenerator>() ?? gameObject.AddComponent<CrossChunkRoomGenerator>();
        roomGenerator.Initialize(chunkSize, worldSeed);

        chunkContainer = new GameObject("Chunks").transform;
        chunkContainer.SetParent(transform);

        InitializeObjectPool();
        UpdateGenerationQueue();

        // Coroutine uploads completed chunks to GPU, spreading work across frames
        StartCoroutine(UploadReadyChunks());
        StartCoroutine(UploadBoundaryRebuildResults());
    }

    void OnDestroy()
    {
        foreach (var kvp in chunkVoxelCache)
            if (kvp.Value.IsCreated) kvp.Value.Dispose();
        chunkVoxelCache.Clear();

        lock (uploadLock)
        {
            while (readyToUpload.Count > 0)
            {
                var r = readyToUpload.Dequeue();
                if (r.voxelData.IsCreated) r.voxelData.Dispose();
            }
            while (readyToUploadBoundary.Count > 0)
                readyToUploadBoundary.Dequeue(); // voxelData owned by cache, don't dispose
        }
    }

    void Update()
    {
        if (playerTransform == null) return;

        UpdatePlayerChunk();

        if (currentPlayerChunkCoord != lastPlayerChunkCoord)
        {
            int moved = GetChunkDistance(currentPlayerChunkCoord, lastPlayerChunkCoord);
            UpdateGenerationQueueIncremental();
            lastPlayerChunkCoord = currentPlayerChunkCoord;
            if (moved > 2) roomGenerator.PruneDistantData(currentPlayerChunkCoord);
        }

        ProcessGenerationQueue();
        ProcessBoundaryUpdates();
    }

    // -------------------------------------------------------------------------
    // Queue management
    // -------------------------------------------------------------------------

    private void UpdatePlayerChunk()
    {
        Vector3Int n = WorldToChunkCoord(playerTransform.position);
        if (n != currentPlayerChunkCoord) currentPlayerChunkCoord = n;
    }

    private void UpdateGenerationQueue()
    {
        generationQueue.Clear();
        EnqueueMissingChunks();
        UnloadOutOfRangeChunks();
    }

    private void UpdateGenerationQueueIncremental()
    {
        EnqueueMissingChunks();
        UnloadOutOfRangeChunks();
    }

    private void EnqueueMissingChunks()
    {
        for (int x = -renderDistance; x <= renderDistance; x++)
        for (int y = -1; y <= 1; y++)
        for (int z = -renderDistance; z <= renderDistance; z++)
        {
            Vector3Int coord = currentPlayerChunkCoord + new Vector3Int(x, y, z);
            if (loadedChunks.ContainsKey(coord))     continue;
            if (currentlyGenerating.Contains(coord)) continue;
            if (generationQueue.ContainsCoord(coord)) continue;
            int dist = GetChunkDistance(coord, currentPlayerChunkCoord);
            generationQueue.Enqueue(new ChunkGenerationTask
            {
                chunkCoord = coord,
                priority   = CalculatePriority(dist),
                timestamp  = Time.time
            });
        }
    }

    private void UnloadOutOfRangeChunks()
    {
        if (loadedChunks.Count == 0) return;

        var coordList = new List<Vector3Int>(loadedChunks.Keys);
        var native    = new NativeArray<int3>(coordList.Count, Allocator.TempJob);
        var flags     = new NativeArray<byte>(coordList.Count, Allocator.TempJob, NativeArrayOptions.ClearMemory);

        for (int i = 0; i < coordList.Count; i++)
            native[i] = new int3(coordList[i].x, coordList[i].y, coordList[i].z);

        new CalculateUnloadFlagsJob
        {
            loadedCoords   = native,
            unloadFlags    = flags,
            playerCoord    = new int3(currentPlayerChunkCoord.x, currentPlayerChunkCoord.y, currentPlayerChunkCoord.z),
            renderDistance = renderDistance
        }.Schedule(coordList.Count, 64).Complete();

        chunksToUnloadBuffer.Clear();
        for (int i = 0; i < coordList.Count; i++)
            if (flags[i] != 0) chunksToUnloadBuffer.Add(coordList[i]);

        flags.Dispose();
        native.Dispose();

        foreach (var coord in chunksToUnloadBuffer)
            UnloadChunk(coord);
    }

    private int CalculatePriority(int distance)
    {
        if (distance <= 0) return 0;
        if (distance == 1) return 1;
        if (distance == 2) return 2;
        return 3 + distance;
    }

    // -------------------------------------------------------------------------
    // Background generation dispatch
    // -------------------------------------------------------------------------

    private void ProcessGenerationQueue()
    {
        while (currentlyGenerating.Count < maxConcurrentGenerations && generationQueue.Count > 0)
        {
            if (!generationQueue.TryDequeue(out ChunkGenerationTask task)) break;

            int dist = GetChunkDistance(task.chunkCoord, currentPlayerChunkCoord);
            if (cancelDistantGeneration && dist > renderDistance + 1) continue;
            if (loadedChunks.ContainsKey(task.chunkCoord)) continue;

            currentlyGenerating.Add(task.chunkCoord);

            // Allocate objects on main thread (Unity API)
            DungeonChunk chunk = GetChunkFromPool();
            if (chunk == null)
            {
                currentlyGenerating.Remove(task.chunkCoord);
                Debug.LogWarning("[InfiniteChunkManager] Pool exhausted, skipping chunk.");
                continue;
            }

            chunk.transform.position = ChunkCoordToWorld(task.chunkCoord);
            chunk.transform.SetParent(chunkContainer);
            chunk.name = $"Chunk_{task.chunkCoord.x}_{task.chunkCoord.y}_{task.chunkCoord.z}";
            chunk.SetChunkCoord(task.chunkCoord, worldSeed);
            chunk.SetChunkManager(this);
            chunk.Initialize(chunkSize);

            int voxelCount = chunkSize.x * chunkSize.y * chunkSize.z;
            NativeArray<byte> voxelData = new NativeArray<byte>(
                voxelCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            // Capture loop variables for the closure
            Vector3Int capturedCoord = task.chunkCoord;
            Vector3Int capturedSize  = chunkSize;

            // Hand off the heavy work to a thread pool thread
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var result = new ChunkBuildResult
                {
                    chunkCoord = capturedCoord,
                    chunk      = chunk,
                    voxelData  = voxelData,
                    success    = false
                };

                try
                {
                    // Step 1: voxel generation (room carving) — pure data, thread-safe
                    roomGenerator.GenerateForChunk(capturedCoord, capturedSize, ref voxelData);

                    // Step 2: build mesh arrays — pure data, no Unity API
                    result.meshData = chunk.BuildMeshData(voxelData);
                    result.success  = true;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Thread] Chunk {capturedCoord} failed: {e.Message}\n{e.StackTrace}");
                }

                lock (uploadLock)
                    readyToUpload.Enqueue(result);
            });
        }
    }

    // -------------------------------------------------------------------------
    // Upload coroutine — main thread only, spreads GPU uploads across frames
    // -------------------------------------------------------------------------

    private IEnumerator UploadReadyChunks()
    {
        while (true)
        {
            int uploaded = 0;

            while (uploaded < maxMeshUploadsPerFrame)
            {
                ChunkBuildResult result = null;
                lock (uploadLock)
                {
                    if (readyToUpload.Count > 0)
                        result = readyToUpload.Dequeue();
                }
                if (result == null) break;

                currentlyGenerating.Remove(result.chunkCoord);

                bool discard = !result.success
                    || result.meshData == null
                    || (cancelDistantGeneration && GetChunkDistance(result.chunkCoord, currentPlayerChunkCoord) > renderDistance)
                    || chunkVoxelCache.ContainsKey(result.chunkCoord); // duplicate

                if (discard)
                {
                    if (result.voxelData.IsCreated) result.voxelData.Dispose();
                    ReturnChunkToPool(result.chunk);
                    uploaded++;
                    continue;
                }

                // Store voxel cache and upload mesh — both must happen on main thread
                chunkVoxelCache[result.chunkCoord] = result.voxelData;
                result.chunk.UploadMesh(result.meshData);
                loadedChunks[result.chunkCoord] = result.chunk;
                MarkAdjacentChunksForUpdate(result.chunkCoord);

                uploaded++;
            }

            yield return null;
        }
    }

    // Separate coroutine for boundary mesh uploads — keeps them out of the
    // main generation upload budget so fresh chunks aren't starved.
    private IEnumerator UploadBoundaryRebuildResults()
    {
        while (true)
        {
            int uploaded = 0;
            while (uploaded < maxMeshUploadsPerFrame)
            {
                ChunkBuildResult result = null;
                lock (uploadLock)
                {
                    if (readyToUploadBoundary.Count > 0)
                        result = readyToUploadBoundary.Dequeue();
                }
                if (result == null) break;

                currentlyRebuildingBoundary.Remove(result.chunkCoord);

                if (result.success && result.meshData != null
                    && loadedChunks.ContainsKey(result.chunkCoord))
                {
                    result.chunk.UploadMesh(result.meshData);
                }
                uploaded++;
            }
            yield return null;
        }
    }

    // -------------------------------------------------------------------------
    // Boundary updates
    // -------------------------------------------------------------------------

    private void ProcessBoundaryUpdates()
    {
        // Dispatch boundary rebuild tasks to background threads.
        // Completed results are uploaded by UploadBoundaryRebuildResults coroutine.
        if (chunksNeedingBoundaryUpdate.Count == 0) return;

        const int maxDispatchPerFrame = 2;
        boundaryUpdateBuffer.Clear();

        foreach (var coord in chunksNeedingBoundaryUpdate)
        {
            if (currentlyRebuildingBoundary.Contains(coord)) continue;
            boundaryUpdateBuffer.Add(coord);
            if (boundaryUpdateBuffer.Count >= maxDispatchPerFrame) break;
        }

        foreach (var coord in boundaryUpdateBuffer)
        {
            chunksNeedingBoundaryUpdate.Remove(coord);

            if (!loadedChunks.TryGetValue(coord, out DungeonChunk chunk)) continue;
            if (!chunkVoxelCache.TryGetValue(coord, out NativeArray<byte> voxelData)) continue;

            currentlyRebuildingBoundary.Add(coord);

            Vector3Int capturedCoord      = coord;
            DungeonChunk capturedChunk    = chunk;
            NativeArray<byte> capturedVox = voxelData;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var result = new ChunkBuildResult
                {
                    chunkCoord = capturedCoord,
                    chunk      = capturedChunk,
                    voxelData  = capturedVox, // reference only — owned by chunkVoxelCache
                    success    = false
                };

                try
                {
                    result.meshData = capturedChunk.BuildMeshData(capturedVox);
                    result.success  = true;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Thread] Boundary rebuild {capturedCoord} failed: {e.Message}");
                }

                lock (uploadLock)
                    readyToUploadBoundary.Enqueue(result);
            });
        }
    }

    private void MarkAdjacentChunksForUpdate(Vector3Int coord)
    {
        foreach (var dir in CardinalDirections)
        {
            Vector3Int adj = coord + dir;
            if (loadedChunks.ContainsKey(adj))
                chunksNeedingBoundaryUpdate.Add(adj);
        }
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public bool TryGetVoxelData(Vector3Int chunkCoord, Vector3Int localPos, out bool isSolid)
    {
        if (currentlyGenerating.Contains(chunkCoord)) { isSolid = true; return false; }

        if (chunkVoxelCache.TryGetValue(chunkCoord, out NativeArray<byte> voxelData))
        {
            if (localPos.x >= 0 && localPos.x < chunkSize.x &&
                localPos.y >= 0 && localPos.y < chunkSize.y &&
                localPos.z >= 0 && localPos.z < chunkSize.z)
            {
                isSolid = voxelData[localPos.x * (chunkSize.y * chunkSize.z) + localPos.y * chunkSize.z + localPos.z] != 0;
                return true;
            }
        }

        isSolid = false;
        return false;
    }

    public Vector3Int WorldToChunkCoord(Vector3 worldPos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / chunkSize.x),
            Mathf.FloorToInt(worldPos.y / chunkSize.y),
            Mathf.FloorToInt(worldPos.z / chunkSize.z)
        );
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private Vector3 ChunkCoordToWorld(Vector3Int c)
        => new Vector3(c.x * chunkSize.x, c.y * chunkSize.y, c.z * chunkSize.z);

    private int GetChunkDistance(Vector3Int a, Vector3Int b)
        => Mathf.Max(Mathf.Abs(a.x-b.x), Mathf.Abs(a.y-b.y), Mathf.Abs(a.z-b.z));

    private void UnloadChunk(Vector3Int coord)
    {
        if (!loadedChunks.TryGetValue(coord, out DungeonChunk chunk)) return;

        ReturnChunkToPool(chunk);
        loadedChunks.Remove(coord);
        roomGenerator.ClearChunkData(coord);

        if (chunkVoxelCache.TryGetValue(coord, out NativeArray<byte> vd))
        {
            if (vd.IsCreated) vd.Dispose();
            chunkVoxelCache.Remove(coord);
        }

        UpdateAdjacentChunks(coord);
    }

    private void UpdateAdjacentChunks(Vector3Int unloaded)
    {
        foreach (var dir in CardinalDirections)
        {
            Vector3Int adj = unloaded + dir;
            if (loadedChunks.ContainsKey(adj))
                chunksNeedingBoundaryUpdate.Add(adj);
        }
    }

    // -------------------------------------------------------------------------
    // Object pool
    // -------------------------------------------------------------------------

    private void InitializeObjectPool()
    {
        if (chunkPrefab == null) { Debug.LogError("Chunk Prefab is not assigned!"); return; }

        int xzDiam   = 2 * renderDistance + 1;
        int poolSize = xzDiam * xzDiam * 3 + maxConcurrentGenerations + 8;

        for (int i = 0; i < poolSize; i++)
        {
            DungeonChunk c = Instantiate(chunkPrefab);
            c.gameObject.SetActive(false);
            chunkPool.Enqueue(c);
        }
    }

    private DungeonChunk GetChunkFromPool()
    {
        if (useObjectPooling && chunkPool.Count > 0)
        {
            DungeonChunk c = chunkPool.Dequeue();
            c.gameObject.SetActive(true);
            return c;
        }
        if (useObjectPooling)
            Debug.LogWarning("[InfiniteChunkManager] Pool exhausted.");
        return Instantiate(chunkPrefab);
    }

    private void ReturnChunkToPool(DungeonChunk chunk)
    {
        if (chunk == null) return;
        if (useObjectPooling) { chunk.Clear(); chunk.gameObject.SetActive(false); chunkPool.Enqueue(chunk); }
        else Destroy(chunk.gameObject);
    }

    // -------------------------------------------------------------------------
    // Jobs
    // -------------------------------------------------------------------------

    [BurstCompile]
    private struct CalculateUnloadFlagsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int3> loadedCoords;
        public NativeArray<byte> unloadFlags;
        public int3 playerCoord;
        public int  renderDistance;

        public void Execute(int index)
        {
            int3 c = loadedCoords[index];
            unloadFlags[index] = (byte)(math.max(math.abs(c.x - playerCoord.x),
                math.max(math.abs(c.y - playerCoord.y), math.abs(c.z - playerCoord.z))) > renderDistance ? 1 : 0);
        }
    }
}