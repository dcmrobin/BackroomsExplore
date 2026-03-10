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
    [SerializeField] private int  renderDistance = 2;
    [SerializeField] private bool useObjectPooling = true;

    [Header("Generation Settings")]
    [SerializeField] private int  maxMeshUploadsPerFrame = 1;
    [SerializeField] private int  maxConcurrentGenerations = 2;
    [SerializeField] private bool cancelDistantGeneration = true;

    [Header("Seed Settings")]
    [SerializeField] private int  worldSeed = 123456;
    [SerializeField] private bool randomizeSeed = true;

    [Header("References")]
    [SerializeField] private DungeonChunk chunkPrefab;
    [SerializeField] private Transform    playerTransform;
    // Base material cloned per biome — must support _MainTex, _FloorTex, _CeilTex
    // (or fall back gracefully to Standard shader with wall tint)
    [SerializeField] private Material chunkMaterial;

    /// <summary>Exposed so DungeonChunk can clone it for per-biome instances.</summary>
    public Material ChunkMaterial => chunkMaterial;

    [Header("Biome Generation")]
    [SerializeField] private BiomeGenerationSettings biomeSettings = new BiomeGenerationSettings();

    private CrossChunkRoomGenerator roomGenerator;
    private BiomeRegistry           biomeRegistry;

    // All accessed only on main thread
    private Dictionary<Vector3Int, DungeonChunk>      loadedChunks    = new Dictionary<Vector3Int, DungeonChunk>();
    private Dictionary<Vector3Int, NativeArray<byte>> chunkVoxelCache = new Dictionary<Vector3Int, NativeArray<byte>>();
    private Queue<DungeonChunk> chunkPool = new Queue<DungeonChunk>();
    private Transform chunkContainer;

    // Generation pipeline
    private PriorityQueue<ChunkGenerationTask> generationQueue     = new PriorityQueue<ChunkGenerationTask>();
    private HashSet<Vector3Int>                currentlyGenerating = new HashSet<Vector3Int>();

    private readonly Queue<ChunkBuildResult> readyToUpload         = new Queue<ChunkBuildResult>();
    private readonly Queue<ChunkBuildResult> readyToUploadBoundary = new Queue<ChunkBuildResult>();
    private readonly object                  uploadLock            = new object();

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

    public class ChunkBuildResult
    {
        public Vector3Int        chunkCoord;
        public DungeonChunk      chunk;
        public NativeArray<byte> voxelData;
        public DungeonChunk.MeshData meshData;
        public BiomeDefinition   biome;   // resolved on background thread, applied on main thread
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

        // Initialise biome registry first — all biomes and textures generated here
        biomeRegistry = new BiomeRegistry();
        biomeRegistry.Initialize(worldSeed, biomeSettings);
        Debug.Log($"BiomeRegistry initialised with {biomeRegistry.AllBiomes.Count} biomes.");

        roomGenerator = GetComponent<CrossChunkRoomGenerator>() ?? gameObject.AddComponent<CrossChunkRoomGenerator>();
        roomGenerator.Initialize(chunkSize, worldSeed);

        chunkContainer = new GameObject("Chunks").transform;
        chunkContainer.SetParent(transform);

        InitializeObjectPool();
        UpdateGenerationQueue();

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
                readyToUploadBoundary.Dequeue();
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
            if (loadedChunks.ContainsKey(coord))      continue;
            if (currentlyGenerating.Contains(coord))  continue;
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
            chunk.SetBiomeRegistry(biomeRegistry);  // pass registry so chunk can resolve its biome
            chunk.Initialize(chunkSize);

            int voxelCount = chunkSize.x * chunkSize.y * chunkSize.z;
            NativeArray<byte> voxelData = new NativeArray<byte>(
                voxelCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            Vector3Int capturedCoord = task.chunkCoord;
            Vector3Int capturedSize  = chunkSize;

            // Resolve the biome for this chunk's world-space centre on the main thread
            // (BiomeRegistry.GetBiomeAt is read-only and thread-safe, but resolving
            // here keeps the ThreadPool lambda free of Unity API calls).
            Vector3 chunkWorldCentre = new Vector3(
                (capturedCoord.x + 0.5f) * capturedSize.x,
                (capturedCoord.y + 0.5f) * capturedSize.y,
                (capturedCoord.z + 0.5f) * capturedSize.z);
            BiomeDefinition capturedBiome = biomeRegistry.GetBiomeAt(chunkWorldCentre);

            // Collect neighbour light seeds HERE on the main thread before dispatching.
            // The background thread must never read loadedChunks (race condition).
            float[][] capturedSeeds = CollectNeighbourLightSeeds(capturedCoord);
            chunk.SetAllNeighbourLightSeeds(capturedSeeds);
            chunk.SetAllNeighbourBorderVoxels(CollectNeighbourBorderVoxels(task.chunkCoord));

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var result = new ChunkBuildResult
                {
                    chunkCoord = capturedCoord,
                    chunk      = chunk,
                    voxelData  = voxelData,
                    biome      = capturedBiome,
                    success    = false
                };

                try
                {
                    // Pass biome so room size, corridor dimensions etc vary per biome
                    roomGenerator.GenerateForChunk(capturedCoord, capturedSize, ref voxelData, capturedBiome);
                    // BuildMeshData uses seeds already set via SetAllNeighbourLightSeeds
                    result.meshData = chunk.BuildMeshData(voxelData, out result.biome);
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
    // Upload coroutines
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
                    || chunkVoxelCache.ContainsKey(result.chunkCoord);

                if (discard)
                {
                    if (result.voxelData.IsCreated) result.voxelData.Dispose();
                    ReturnChunkToPool(result.chunk);
                    uploaded++;
                    continue;
                }

                chunkVoxelCache[result.chunkCoord] = result.voxelData;
                // Pass biome so UploadMesh can apply material on main thread
                result.chunk.UploadMesh(result.meshData, result.biome);
                loadedChunks[result.chunkCoord] = result.chunk;

                // Push this new chunk's light outward to all neighbours (depth 0 = fresh)
                cascadeDepth.Remove(result.chunkCoord);
                MarkAdjacentChunksForUpdate(result.chunkCoord, 0);

                // Also schedule THIS new chunk for a boundary rebuild so it pulls
                // light inward from its already-lit neighbours. Without this, a new
                // chunk sitting next to a brightly lit one stays dark until the
                // neighbour happens to trigger a cascade update.
                chunksNeedingBoundaryUpdate.Add(result.chunkCoord);

                uploaded++;
            }

            yield return null;
        }
    }

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
                    result.chunk.UploadMesh(result.meshData, result.biome);
                    // Cascade: push this chunk's freshly-computed boundary light
                    // to its own neighbours so light propagates across more than
                    // one chunk boundary. Pass the accumulated depth so we stop
                    // cascading once light has fallen below the visible threshold.
                    int depth = cascadeDepth.TryGetValue(result.chunkCoord, out int d) ? d : 0;
                    cascadeDepth.Remove(result.chunkCoord);
                    MarkAdjacentChunksForUpdate(result.chunkCoord, depth);
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

            Vector3Int   capturedCoord = coord;
            DungeonChunk capturedChunk = chunk;

            // Copy voxel data into a managed array HERE on the main thread.
            // The NativeArray in chunkVoxelCache can be disposed at any time if
            // the chunk unloads — copying now means the background thread never
            // touches the NativeArray directly, eliminating the race condition.
            byte[] voxelSnapshot = voxelData.ToArray();

            // Collect fresh neighbour seeds AND border voxels on main thread.
            // Both must be pre-baked here so the background thread never reads
            // chunkVoxelCache or loadedChunks directly.
            float[][] boundarySeeds   = CollectNeighbourLightSeeds(capturedCoord);
            bool[][]  borderVoxels    = CollectNeighbourBorderVoxels(capturedCoord);
            capturedChunk.SetAllNeighbourLightSeeds(boundarySeeds);
            capturedChunk.SetAllNeighbourBorderVoxels(borderVoxels);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var result = new ChunkBuildResult
                {
                    chunkCoord = capturedCoord,
                    chunk      = capturedChunk,
                    voxelData  = default, // boundary rebuilds don't own voxel data
                    success    = false
                };

                try
                {
                    result.meshData = capturedChunk.BuildMeshDataFromSnapshot(voxelSnapshot, out result.biome);
                    result.success  = true;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Thread] Boundary rebuild {capturedCoord} failed: {e.Message} {e.StackTrace}");
                }

                lock (uploadLock)
                    readyToUploadBoundary.Enqueue(result);
            });
        }
    }

    // Maps chunk coord → how many cascade hops it has already travelled.
    private Dictionary<Vector3Int, int> cascadeDepth = new Dictionary<Vector3Int, int>();
    // Maps "chunkCoord_faceIndex" → hash of last exported light slice on that face.
    // We only cascade to a neighbour if the slice actually changed — this prevents
    // two adjacent chunks with stable light from endlessly rebuilding each other.
    private Dictionary<long, int> exportedLightHash = new Dictionary<long, int>();
    private const float LIGHT_CASCADE_THRESHOLD = 0.02f;
    private const int MAX_CASCADE_DEPTH = 8;

    private void MarkAdjacentChunksForUpdate(Vector3Int coord, int depth = 0)
    {
        if (depth >= MAX_CASCADE_DEPTH) return;

        // Export this chunk's boundary light slices and push them to each loaded neighbour,
        // then mark the neighbour for a lighting-aware rebuild so cross-chunk light bleeds in.
        if (loadedChunks.TryGetValue(coord, out DungeonChunk srcChunk))
        {
            for (int fi = 0; fi < CardinalDirections.Length; fi++)
            {
                Vector3Int dir = CardinalDirections[fi];
                Vector3Int adj = coord + dir;
                if (!loadedChunks.TryGetValue(adj, out DungeonChunk adjChunk)) continue;

                float[] slice = srcChunk.ExportBoundaryLightSlice(dir);
                if (slice == null) continue;

                float maxVal = 0f;
                int   sliceHash = 17;
                foreach (float v in slice)
                {
                    if (v > maxVal) maxVal = v;
                    sliceHash = sliceHash * 31 + v.GetHashCode();
                }
                if (maxVal < LIGHT_CASCADE_THRESHOLD) continue;

                // Only cascade if this face's light actually changed
                long hashKey = ((long)(coord.x + 10000) * 20001L + (coord.y + 10000)) * 20001L
                             + (coord.z + 10000) + (long)fi * 400060001L;
                if (exportedLightHash.TryGetValue(hashKey, out int prevHash) && prevHash == sliceHash)
                    continue;
                exportedLightHash[hashKey] = sliceHash;

                adjChunk.SetNeighbourLightSeeds(-dir, slice);

                int existingDepth = cascadeDepth.TryGetValue(adj, out int d) ? d : int.MaxValue;
                if (depth + 1 < existingDepth)
                {
                    cascadeDepth[adj] = depth + 1;
                    chunksNeedingBoundaryUpdate.Add(adj);
                }
            }
        }
        else
        {
            foreach (var dir in CardinalDirections)
            {
                Vector3Int adj = coord + dir;
                if (loadedChunks.ContainsKey(adj))
                    chunksNeedingBoundaryUpdate.Add(adj);
            }
        }
    }

    // Called by DungeonChunk.SetNeighbourLightSeeds — schedules a lighting+mesh rebuild.
    public void MarkChunkForLightingRebuild(Vector3Int coord)
    {
        if (loadedChunks.ContainsKey(coord))
            chunksNeedingBoundaryUpdate.Add(coord);
    }

    // MAIN THREAD ONLY. For each of 6 faces, collects the border row of voxel solidity
    // from the adjacent chunk so the background thread can do cross-chunk face culling
    // without ever touching chunkVoxelCache or loadedChunks.
    private bool[][] CollectNeighbourBorderVoxels(Vector3Int coord)
    {
        bool[][] result = new bool[6][];
        int sx = chunkSize.x, sy = chunkSize.y, sz = chunkSize.z;

        // Face order: +X=0,-X=1,+Y=2,-Y=3,+Z=4,-Z=5
        Vector3Int[] dirs = {
            Vector3Int.right, Vector3Int.left,
            Vector3Int.up,    Vector3Int.down,
            Vector3Int.forward, Vector3Int.back
        };

        for (int fi = 0; fi < 6; fi++)
        {
            Vector3Int nCoord = coord + dirs[fi];
            if (!chunkVoxelCache.TryGetValue(nCoord, out NativeArray<byte> nVox)) continue;

            bool[] border;
            if (fi <= 1) // X faces: border is sy*sz, u=y, v=z
            {
                border = new bool[sy * sz];
                int bx = (fi == 0) ? 0 : sx - 1; // neighbour's border x
                for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                    border[y * sz + z] = nVox[bx * sy * sz + y * sz + z] != 0;
            }
            else if (fi <= 3) // Y faces: border is sx*sz, u=x, v=z
            {
                border = new bool[sx * sz];
                int by = (fi == 2) ? 0 : sy - 1;
                for (int x = 0; x < sx; x++)
                for (int z = 0; z < sz; z++)
                    border[x * sz + z] = nVox[x * sy * sz + by * sz + z] != 0;
            }
            else // Z faces: border is sx*sy, u=x, v=y
            {
                border = new bool[sx * sy];
                int bz = (fi == 4) ? 0 : sz - 1;
                for (int x = 0; x < sx; x++)
                for (int y = 0; y < sy; y++)
                    border[x * sy + y] = nVox[x * sy * sz + y * sz + bz] != 0;
            }
            result[fi] = border;
        }
        return result;
    }

    // MAIN THREAD ONLY. Collects boundary light slices from all 6 loaded neighbours
    // of chunkCoord. Returns a float[6][] ready to pass to chunk.SetAllNeighbourLightSeeds()
    // before dispatching the build job. This avoids the background-thread race condition
    // that existed when DungeonChunk called GetLightAtWorldPos from a ThreadPool thread.
    private float[][] CollectNeighbourLightSeeds(Vector3Int chunkCoord)
    {
        float[][] seeds = new float[6][];

        int sx = chunkSize.x, sy = chunkSize.y, sz = chunkSize.z;
        Vector3Int origin = Vector3Int.Scale(chunkCoord, chunkSize);

        // Face indices match FaceIndexToDir in DungeonChunk: +X=0,-X=1,+Y=2,-Y=3,+Z=4,-Z=5
        Vector3Int[] dirs = {
            Vector3Int.right, Vector3Int.left,
            Vector3Int.up,    Vector3Int.down,
            Vector3Int.forward, Vector3Int.back
        };

        for (int fi = 0; fi < 6; fi++)
        {
            Vector3Int dir = dirs[fi];

            // Only sample if the neighbour chunk is loaded and lit
            Vector3Int neighbourCoord = chunkCoord + dir;
            if (!loadedChunks.TryGetValue(neighbourCoord, out DungeonChunk neighbour)) continue;

            // Sample the row of voxels that sit just INSIDE the neighbour (1 voxel past the boundary)
            float[] slice;
            bool anyData = false;

            if (dir == Vector3Int.right || dir == Vector3Int.left)
            {
                int wx = dir == Vector3Int.right ? origin.x + sx : origin.x - 1;
                slice = new float[sy * sz];
                for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                {
                    float v = GetLightAtWorldPos(new Vector3Int(wx, origin.y + y, origin.z + z));
                    if (v >= 0f) { slice[y * sz + z] = v; anyData = true; }
                }
            }
            else if (dir == Vector3Int.up || dir == Vector3Int.down)
            {
                int wy = dir == Vector3Int.up ? origin.y + sy : origin.y - 1;
                slice = new float[sx * sz];
                for (int x = 0; x < sx; x++)
                for (int z = 0; z < sz; z++)
                {
                    float v = GetLightAtWorldPos(new Vector3Int(origin.x + x, wy, origin.z + z));
                    if (v >= 0f) { slice[x * sz + z] = v; anyData = true; }
                }
            }
            else // forward / back
            {
                int wz = dir == Vector3Int.forward ? origin.z + sz : origin.z - 1;
                slice = new float[sx * sy];
                for (int x = 0; x < sx; x++)
                for (int y = 0; y < sy; y++)
                {
                    float v = GetLightAtWorldPos(new Vector3Int(origin.x + x, origin.y + y, wz));
                    if (v >= 0f) { slice[x * sy + y] = v; anyData = true; }
                }
            }

            if (anyData) seeds[fi] = slice;
        }

        return seeds;
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

    /// <summary>
    /// Returns the baked light level at a world-space voxel position by looking
    /// it up in the owning chunk's DungeonChunk component.
    /// Returns -1 if the chunk isn't loaded or hasn't been lit yet.
    /// Called by DungeonChunk.GetFaceLightLevel / GetVL for cross-chunk lighting.
    /// </summary>
    public float GetLightAtWorldPos(Vector3Int worldPos)
    {
        Vector3Int coord = new Vector3Int(
            Mathf.FloorToInt((float)worldPos.x / chunkSize.x),
            Mathf.FloorToInt((float)worldPos.y / chunkSize.y),
            Mathf.FloorToInt((float)worldPos.z / chunkSize.z));

        if (!loadedChunks.TryGetValue(coord, out DungeonChunk chunk)) return -1f;

        Vector3Int local = worldPos - Vector3Int.Scale(coord, chunkSize);
        return chunk.GetLightValue(local);
    }

    public Vector3Int WorldToChunkCoord(Vector3 worldPos)
        => new Vector3Int(
            Mathf.FloorToInt(worldPos.x / chunkSize.x),
            Mathf.FloorToInt(worldPos.y / chunkSize.y),
            Mathf.FloorToInt(worldPos.z / chunkSize.z));

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
        // Clear cached light hashes for this chunk's 6 faces so a freshly
        // generated chunk at the same coord gets proper cascade initialisation.
        for (int fi = 0; fi < 6; fi++)
        {
            long hashKey = ((long)(coord.x + 10000) * 20001L + (coord.y + 10000)) * 20001L
                         + (coord.z + 10000) + (long)fi * 400060001L;
            exportedLightHash.Remove(hashKey);
        }

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