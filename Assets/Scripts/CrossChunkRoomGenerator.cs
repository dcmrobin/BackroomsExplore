using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public class CrossChunkRoomGenerator : MonoBehaviour
{
    [Header("3D Noise Settings")]
    [SerializeField] private Vector3 noiseScale = new Vector3(0.03f, 0.03f, 0.03f);
    [SerializeField] private Vector3 noiseOffset = Vector3.zero;
    [SerializeField] private bool useNoiseCache = true;

    [Header("Room Detection — Global Defaults (overridden per biome)")]
    [Tooltip("Noise threshold to seed a room. Used when no biome is available.")]
    [SerializeField] private float roomThreshold = 0.65f;
    [Tooltip("Noise threshold for room expansion. Used when no biome is available.")]
    [SerializeField] private float roomExpansionThreshold = 0.55f;
    [Tooltip("Minimum room side length (voxels). Used when no biome is available.")]
    [SerializeField] private int minRoomSize = 4;
    [Tooltip("Maximum room side length (voxels). Used when no biome is available.")]
    [SerializeField] private int maxRoomSize = 30;
    [Tooltip("Voxel step between room seed probes. Larger = faster but misses small rooms.")]
    [SerializeField] private int roomScanStep = 4;

    [Header("Room Expansion — Global Defaults")]
    [SerializeField] private int maxExpansionSteps = 20;
    [SerializeField] private bool expandToGrid = true;
    [SerializeField] private int gridAlignment = 2;

    [Header("Corridor Settings — Global Defaults (overridden per biome)")]
    [Tooltip("Minimum corridor width (voxels). Biome value is clamped to this floor.")]
    [SerializeField] private int minCorridorWidth = 2;
    [Tooltip("Maximum corridor width (voxels). Biome value is clamped to this ceiling.")]
    [SerializeField] private int maxCorridorWidth = 8;
    [Tooltip("Corridor height (voxels). Used when no biome is available.")]
    [SerializeField] private int corridorHeight = 4;
    [Tooltip("Vertical shaft cross-section size.")]
    [SerializeField] private int verticalShaftSize = 3;
    [Tooltip("Chance a corridor between rooms at different Y levels becomes a vertical shaft.")]
    [Range(0, 1)]
    [SerializeField] private float verticalConnectionChance = 0.3f;

    [Header("Memory Management")]
    [SerializeField] private int maxRoomsToKeep = 1000;
    [SerializeField] private int pruneRadius = 10;

    // -------------------------------------------------------------------------
    // Private state
    // -------------------------------------------------------------------------

    // Global storage
    private Dictionary<int, CuboidRoom> allRooms = new Dictionary<int, CuboidRoom>();
    private Dictionary<int, Corridor> allCorridors = new Dictionary<int, Corridor>();
    private Dictionary<Vector3Int, HashSet<int>> chunkToRooms = new Dictionary<Vector3Int, HashSet<int>>();
    private Dictionary<Vector3Int, HashSet<int>> chunkToCorridors = new Dictionary<Vector3Int, HashSet<int>>();

    // Spatial partitioning
    private Dictionary<Vector3Int, HashSet<int>> roomSpatialGrid = new Dictionary<Vector3Int, HashSet<int>>();
    private int spatialGridSize = 32;

    // State
    private int nextRoomId = 0;
    private int nextCorridorId = 0;
    private Vector3Int currentChunkSize;
    private HashSet<Vector3Int> processedChunks = new HashSet<Vector3Int>();
    private int worldSeed;

    // Performance
    private Dictionary<Vector3Int, float> noiseCache = new Dictionary<Vector3Int, float>();
    private const int MAX_NOISE_CACHE_SIZE = 50000;
    private object generationLock = new object();
    private System.Random globalRandom;

    // -------------------------------------------------------------------------
    // Active biome world-gen values — set at the start of GenerateForChunk,
    // read by all private helpers so nothing needs passing through every call.
    // -------------------------------------------------------------------------
    private float _roomThreshold          = 0.65f;
    private float _roomExpansionThreshold = 0.55f;
    private int   _minRoomSize            = 4;
    private int   _maxRoomSize            = 30;
    private int   _corridorWidth          = 4;
    private int   _corridorHeight         = 4;

    // =========================================================================
    // Room class
    // =========================================================================
    private class CuboidRoom
    {
        public int id;
        public Vector3Int center;
        public Vector3Int minBounds;
        public Vector3Int maxBounds;
        public Vector3Int size;
        public Vector3Int generationChunk;
        public bool isActive = true;

        public CuboidRoom(int id, Vector3Int center, Vector3Int genChunk)
        {
            this.id = id;
            this.center = center;
            this.minBounds = center;
            this.maxBounds = center;
            this.size = Vector3Int.one;
            this.generationChunk = genChunk;
        }

        public void SetBounds(Vector3Int min, Vector3Int max)
        {
            minBounds = min;
            maxBounds = max;
            size = max - min + Vector3One;
            center = minBounds + size / 2;

            if (size.x < 2) { maxBounds.x = minBounds.x + 1; size.x = 2; }
            if (size.y < 2) { maxBounds.y = minBounds.y + 1; size.y = 2; }
            if (size.z < 2) { maxBounds.z = minBounds.z + 1; size.z = 2; }
        }

        public bool ContainsPoint(Vector3Int worldPos)
        {
            return worldPos.x >= minBounds.x && worldPos.x <= maxBounds.x &&
                   worldPos.y >= minBounds.y && worldPos.y <= maxBounds.y &&
                   worldPos.z >= minBounds.z && worldPos.z <= maxBounds.z;
        }

        public bool Overlaps(CuboidRoom other)
        {
            return !(maxBounds.x < other.minBounds.x || minBounds.x > other.maxBounds.x ||
                     maxBounds.y < other.minBounds.y || minBounds.y > other.maxBounds.y ||
                     maxBounds.z < other.minBounds.z || minBounds.z > other.maxBounds.z);
        }

        public List<Vector3Int> GetOccupiedChunks(Vector3Int chunkSize)
        {
            HashSet<Vector3Int> chunks = new HashSet<Vector3Int>();
            Vector3Int minChunk = WorldToChunkCoord(minBounds, chunkSize);
            Vector3Int maxChunk = WorldToChunkCoord(maxBounds, chunkSize);
            for (int x = minChunk.x; x <= maxChunk.x; x++)
            for (int y = minChunk.y; y <= maxChunk.y; y++)
            for (int z = minChunk.z; z <= maxChunk.z; z++)
                chunks.Add(new Vector3Int(x, y, z));
            return chunks.ToList();
        }

        public int Volume => size.x * size.y * size.z;

        private Vector3Int WorldToChunkCoord(Vector3Int worldPos, Vector3Int chunkSize)
        {
            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x / (float)chunkSize.x),
                Mathf.FloorToInt(worldPos.y / (float)chunkSize.y),
                Mathf.FloorToInt(worldPos.z / (float)chunkSize.z));
        }

        private static readonly Vector3Int Vector3One = new Vector3Int(1, 1, 1);
    }

    // =========================================================================
    // Corridor class
    // =========================================================================
    private class Corridor
    {
        public int id;
        public int roomAId;
        public int roomBId;
        public bool isVertical;
        public List<Vector3Int> path = new List<Vector3Int>();
        public int width;
        public int height;
        public bool isActive = true;

        public List<Vector3Int> GetOccupiedChunks(Vector3Int chunkSize)
        {
            HashSet<Vector3Int> chunks = new HashSet<Vector3Int>();
            Vector3Int[] neighbors = {
                Vector3Int.up, Vector3Int.down, Vector3Int.right,
                Vector3Int.left, Vector3Int.forward, Vector3Int.back
            };
            foreach (var point in path)
            {
                Vector3Int chunk = WorldToChunkCoord(point, chunkSize);
                chunks.Add(chunk);
                foreach (var dir in neighbors)
                    chunks.Add(chunk + dir);
            }
            return chunks.ToList();
        }

        private Vector3Int WorldToChunkCoord(Vector3Int worldPos, Vector3Int chunkSize)
        {
            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x / (float)chunkSize.x),
                Mathf.FloorToInt(worldPos.y / (float)chunkSize.y),
                Mathf.FloorToInt(worldPos.z / (float)chunkSize.z));
        }
    }

    // =========================================================================
    // Public API
    // =========================================================================

    public void Initialize(Vector3Int chunkSize, int seed)
    {
        currentChunkSize = chunkSize;
        worldSeed = seed;
        globalRandom = new System.Random(worldSeed);

        System.Random offsetRandom = new System.Random(worldSeed);
        noiseOffset = new Vector3(
            offsetRandom.Next(-10000, 10000),
            offsetRandom.Next(-10000, 10000),
            offsetRandom.Next(-10000, 10000));

        Debug.Log($"Generator initialized with seed: {worldSeed}, noise offset: {noiseOffset}");

        allRooms.Clear();
        allCorridors.Clear();
        chunkToRooms.Clear();
        chunkToCorridors.Clear();
        roomSpatialGrid.Clear();
        processedChunks.Clear();
        noiseCache.Clear();
        nextRoomId = 0;
        nextCorridorId = 0;
    }

    /// <summary>
    /// Generate voxel data for one chunk. Accepts an optional BiomeDefinition;
    /// when provided, room size, density, and corridor dimensions are taken from
    /// the biome rather than the global Inspector defaults.
    /// </summary>
    public void GenerateForChunk(Vector3Int chunkCoord, Vector3Int chunkSize,
                                 ref NativeArray<byte> finalGrid, BiomeDefinition biome = null)
    {
        lock (generationLock)
        {
            currentChunkSize = chunkSize;

            // --- Apply active biome values (or fall back to Inspector defaults) ---
            if (biome != null)
            {
                _roomThreshold          = biome.roomThreshold;
                _roomExpansionThreshold = biome.roomExpansionThreshold;
                _minRoomSize            = biome.roomSizeMin;
                _maxRoomSize            = biome.roomSizeMax;
                _corridorWidth          = biome.corridorWidth;
                _corridorHeight         = biome.corridorHeight;
            }
            else
            {
                _roomThreshold          = roomThreshold;
                _roomExpansionThreshold = roomExpansionThreshold;
                _minRoomSize            = minRoomSize;
                _maxRoomSize            = maxRoomSize;
                _corridorWidth          = (minCorridorWidth + maxCorridorWidth) / 2;
                _corridorHeight         = corridorHeight;
            }

            ClearGrid(ref finalGrid, chunkSize);

            if (!processedChunks.Contains(chunkCoord))
            {
                GenerateRoomsForChunk(chunkCoord);
                processedChunks.Add(chunkCoord);
            }

            List<CuboidRoom> relevantRooms = GetRoomsForChunkDirect(chunkCoord);
            foreach (var room in relevantRooms)
                if (room.isActive)
                    CarveCuboidRoomIntoGrid(room, Vector3Int.Scale(chunkCoord, chunkSize), chunkSize, ref finalGrid);

            List<Corridor> relevantCorridors = GetCorridorsForChunkDirect(chunkCoord);
            foreach (var corridor in relevantCorridors)
                if (corridor.isActive)
                    CarveGeometricCorridorIntoGrid(corridor, Vector3Int.Scale(chunkCoord, chunkSize), chunkSize, ref finalGrid);
        }
    }

    public void PruneDistantData(Vector3Int centerChunk)
    {
        lock (generationLock)
        {
            foreach (var room in allRooms.Values)
            {
                if (room.isActive)
                {
                    Vector3Int roomCenterChunk = WorldToChunkCoord(room.center, currentChunkSize);
                    if (GetChunkDistance(roomCenterChunk, centerChunk) > pruneRadius)
                    {
                        room.isActive = false;
                        RemoveRoomFromSpatialGrid(room);
                        foreach (var chunkCoord in room.GetOccupiedChunks(currentChunkSize))
                        {
                            if (chunkToRooms.TryGetValue(chunkCoord, out HashSet<int> ids))
                            {
                                ids.Remove(room.id);
                                if (ids.Count == 0) chunkToRooms.Remove(chunkCoord);
                            }
                        }
                    }
                }
            }

            foreach (var corridor in allCorridors.Values)
            {
                if (corridor.isActive && corridor.path.Count > 0)
                {
                    Vector3Int mid = WorldToChunkCoord(corridor.path[corridor.path.Count / 2], currentChunkSize);
                    if (GetChunkDistance(mid, centerChunk) > pruneRadius)
                    {
                        corridor.isActive = false;
                        foreach (var chunkCoord in corridor.GetOccupiedChunks(currentChunkSize))
                        {
                            if (chunkToCorridors.TryGetValue(chunkCoord, out HashSet<int> ids))
                            {
                                ids.Remove(corridor.id);
                                if (ids.Count == 0) chunkToCorridors.Remove(chunkCoord);
                            }
                        }
                    }
                }
            }

            if (allRooms.Count > maxRoomsToKeep * 2)
                RemoveInactiveRooms();

            if (noiseCache.Count > MAX_NOISE_CACHE_SIZE / 2)
                noiseCache.Clear();
        }
    }

    public void ClearChunkData(Vector3Int chunkCoord)
    {
        lock (generationLock)
            processedChunks.Remove(chunkCoord);
    }

    // =========================================================================
    // Generation
    // =========================================================================

    private void GenerateRoomsForChunk(Vector3Int chunkCoord)
    {
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, currentChunkSize);
        int chunkSeed = GetChunkSeed(chunkCoord);
        System.Random chunkRandom = new System.Random(chunkSeed);

        for (int x = 0; x < currentChunkSize.x; x += roomScanStep)
        for (int y = 0; y < currentChunkSize.y; y += roomScanStep)
        for (int z = 0; z < currentChunkSize.z; z += roomScanStep)
        {
            Vector3Int worldPos = new Vector3Int(x, y, z) + worldOffset;
            float noiseValue = GetCachedNoise(worldPos);
            if (noiseValue > _roomThreshold && !IsPointInAnyRoomOptimized(worldPos))
                CreateAndExpandRoom(worldPos, chunkCoord, chunkRandom);
        }

        ConnectRoomsForChunk(chunkCoord, chunkRandom);
    }

    private void CreateAndExpandRoom(Vector3Int seedPos, Vector3Int genChunk, System.Random chunkRandom)
    {
        CuboidRoom room = new CuboidRoom(nextRoomId++, seedPos, genChunk);
        ExpandRoomGeometrically(room, chunkRandom);

        if (room.size.x >= _minRoomSize && room.size.y >= _minRoomSize && room.size.z >= _minRoomSize)
            RegisterRoom(room);
        else
            nextRoomId--;
    }

    private void ExpandRoomGeometrically(CuboidRoom room, System.Random chunkRandom)
    {
        Vector3Int currentMin = room.center;
        Vector3Int currentMax = room.center;
        bool[] expansionBlocked = new bool[6];

        List<int> directionOrder = new List<int> { 0, 1, 2, 3, 4, 5 };
        directionOrder = directionOrder.OrderBy(x => chunkRandom.Next()).ToList();

        for (int step = 0; step < maxExpansionSteps; step++)
        {
            bool expanded = false;
            var activeDirections = directionOrder.Where(dir => !expansionBlocked[dir]).ToList();
            if (activeDirections.Count == 0) break;

            foreach (int dirIndex in activeDirections)
            {
                Vector3Int direction = IndexToDirection(dirIndex);
                if (TryExpandDirection(room, ref currentMin, ref currentMax, direction, chunkRandom))
                {
                    expanded = true;
                    activeDirections = activeDirections.OrderBy(x => chunkRandom.Next()).ToList();
                }
                else
                {
                    expansionBlocked[dirIndex] = true;
                }
            }

            directionOrder = activeDirections;
            if (!expanded || RoomExceedsMaxSize(currentMin, currentMax)) break;
        }

        room.SetBounds(currentMin, currentMax);
    }

    private bool TryExpandDirection(CuboidRoom room, ref Vector3Int currentMin, ref Vector3Int currentMax,
                                    Vector3Int direction, System.Random chunkRandom)
    {
        Vector3Int expandMin, expandMax;

        if (direction == Vector3Int.left)
        {
            expandMin = new Vector3Int(currentMin.x - gridAlignment, currentMin.y, currentMin.z);
            expandMax = new Vector3Int(currentMin.x - 1, currentMax.y, currentMax.z);
        }
        else if (direction == Vector3Int.right)
        {
            expandMin = new Vector3Int(currentMax.x + 1, currentMin.y, currentMin.z);
            expandMax = new Vector3Int(currentMax.x + gridAlignment, currentMax.y, currentMax.z);
        }
        else if (direction == Vector3Int.down)
        {
            expandMin = new Vector3Int(currentMin.x, currentMin.y - gridAlignment, currentMin.z);
            expandMax = new Vector3Int(currentMax.x, currentMin.y - 1, currentMax.z);
        }
        else if (direction == Vector3Int.up)
        {
            expandMin = new Vector3Int(currentMin.x, currentMax.y + 1, currentMin.z);
            expandMax = new Vector3Int(currentMax.x, currentMax.y + gridAlignment, currentMax.z);
        }
        else if (direction == Vector3Int.back)
        {
            expandMin = new Vector3Int(currentMin.x, currentMin.y, currentMin.z - gridAlignment);
            expandMax = new Vector3Int(currentMax.x, currentMax.y, currentMin.z - 1);
        }
        else
        {
            expandMin = new Vector3Int(currentMin.x, currentMin.y, currentMax.z + 1);
            expandMax = new Vector3Int(currentMax.x, currentMax.y, currentMax.z + gridAlignment);
        }

        if (WouldOverlapOtherRooms(expandMin, expandMax, room.id)) return false;

        int validSamples = 0, totalSamples = 0;
        for (int x = expandMin.x; x <= expandMax.x; x += gridAlignment)
        for (int y = expandMin.y; y <= expandMax.y; y += gridAlignment)
        for (int z = expandMin.z; z <= expandMax.z; z += gridAlignment)
        {
            totalSamples++;
            if (GetCachedNoise(new Vector3Int(x, y, z)) > _roomExpansionThreshold)
                validSamples++;
        }

        if (totalSamples > 0 && (float)validSamples / totalSamples >= 0.6f)
        {
            if      (direction == Vector3Int.left)  currentMin.x = expandMin.x;
            else if (direction == Vector3Int.right) currentMax.x = expandMax.x;
            else if (direction == Vector3Int.down)  currentMin.y = expandMin.y;
            else if (direction == Vector3Int.up)    currentMax.y = expandMax.y;
            else if (direction == Vector3Int.back)  currentMin.z = expandMin.z;
            else                                    currentMax.z = expandMax.z;
            return true;
        }
        return false;
    }

    private bool RoomExceedsMaxSize(Vector3Int min, Vector3Int max)
    {
        Vector3Int size = max - min + new Vector3Int(1, 1, 1);
        return size.x > _maxRoomSize || size.y > _maxRoomSize || size.z > _maxRoomSize;
    }

    // =========================================================================
    // Corridor generation
    // =========================================================================

    private void ConnectRoomsForChunk(Vector3Int chunkCoord, System.Random chunkRandom)
    {
        HashSet<int> candidateIds = new HashSet<int>();
        Vector3Int[] searchOffsets = {
            Vector3Int.zero,
            Vector3Int.right, Vector3Int.left,
            Vector3Int.up,    Vector3Int.down,
            Vector3Int.forward, Vector3Int.back
        };

        foreach (var offset in searchOffsets)
        {
            if (chunkToRooms.TryGetValue(chunkCoord + offset, out HashSet<int> ids))
                foreach (var id in ids) candidateIds.Add(id);
        }

        List<CuboidRoom> candidates = new List<CuboidRoom>();
        foreach (var id in candidateIds)
            if (allRooms.TryGetValue(id, out CuboidRoom r) && r.isActive)
                candidates.Add(r);

        if (candidates.Count < 2) return;

        HashSet<long> existingConnections = new HashSet<long>();
        foreach (var corridor in allCorridors.Values)
            if (corridor.isActive)
                existingConnections.Add(CorridorKey(corridor.roomAId, corridor.roomBId));

        // Prim's MST
        List<CuboidRoom> connected   = new List<CuboidRoom>();
        List<CuboidRoom> unconnected = new List<CuboidRoom>(candidates);

        int startIndex = Mathf.Abs(chunkRandom.Next()) % unconnected.Count;
        connected.Add(unconnected[startIndex]);
        unconnected.RemoveAt(startIndex);

        while (unconnected.Count > 0)
        {
            float minDist = float.MaxValue;
            CuboidRoom bestUnconnected = null, bestConnected = null;

            foreach (var c in connected)
            foreach (var u in unconnected)
            {
                float d = Vector3Int.Distance(c.center, u.center);
                if (d < minDist) { minDist = d; bestUnconnected = u; bestConnected = c; }
            }

            if (bestUnconnected != null)
            {
                long key = CorridorKey(bestConnected.id, bestUnconnected.id);
                if (!existingConnections.Contains(key))
                {
                    CreateGeometricCorridor(bestConnected, bestUnconnected, chunkRandom);
                    existingConnections.Add(key);
                }
                connected.Add(bestUnconnected);
                unconnected.Remove(bestUnconnected);
            }
        }
    }

    private long CorridorKey(int idA, int idB)
    {
        int lo = Mathf.Min(idA, idB), hi = Mathf.Max(idA, idB);
        return ((long)lo << 32) | (uint)hi;
    }

    private void CreateGeometricCorridor(CuboidRoom roomA, CuboidRoom roomB, System.Random chunkRandom)
    {
        bool makeVertical = chunkRandom.NextDouble() < verticalConnectionChance &&
                            Mathf.Abs(roomA.center.y - roomB.center.y) > _minRoomSize;

        // Jitter biome corridor width slightly per corridor (+/- 1) for variety.
        // Still seeded off the room pair so width is stable across chunk rebuilds.
        int widthSeed = roomA.id ^ roomB.id ^ worldSeed;
        System.Random widthRandom = new System.Random(widthSeed);
        int corridorWidth = Mathf.Clamp(_corridorWidth + widthRandom.Next(-1, 2),
                                        minCorridorWidth, maxCorridorWidth);

        Corridor corridor = new Corridor
        {
            id         = nextCorridorId++,
            roomAId    = roomA.id,
            roomBId    = roomB.id,
            isVertical = makeVertical,
            width      = corridorWidth,
            height     = makeVertical ? verticalShaftSize : _corridorHeight
        };

        GenerateStraightCorridorPath(roomA, roomB, corridor);
        allCorridors[corridor.id] = corridor;
        RegisterCorridor(corridor);
    }

    private void GenerateStraightCorridorPath(CuboidRoom roomA, CuboidRoom roomB, Corridor corridor)
    {
        Vector3Int pointA = GetGeometricConnectionPoint(roomA, roomB.center);
        Vector3Int pointB = GetGeometricConnectionPoint(roomB, roomA.center);

        if (!corridor.isVertical)
        {
            pointA.y = roomA.minBounds.y;
            pointB.y = roomB.minBounds.y;
        }

        Vector3Int current = pointA;
        corridor.path.Add(current);

        int xDir = Mathf.Clamp(pointB.x - pointA.x, -1, 1);
        while (current.x != pointB.x) { current.x += xDir; corridor.path.Add(current); }

        int zDir = Mathf.Clamp(pointB.z - current.z, -1, 1);
        while (current.z != pointB.z) { current.z += zDir; corridor.path.Add(current); }

        if (current.y != pointB.y)
        {
            int yDir = Mathf.Clamp(pointB.y - current.y, -1, 1);
            while (current.y != pointB.y) { current.y += yDir; corridor.path.Add(current); }
        }
    }

    private Vector3Int GetGeometricConnectionPoint(CuboidRoom room, Vector3Int target)
    {
        Vector3Int[] faceCenters = {
            new Vector3Int(room.minBounds.x, room.center.y, room.center.z),
            new Vector3Int(room.maxBounds.x, room.center.y, room.center.z),
            new Vector3Int(room.center.x, room.minBounds.y, room.center.z),
            new Vector3Int(room.center.x, room.maxBounds.y, room.center.z),
            new Vector3Int(room.center.x, room.center.y, room.minBounds.z),
            new Vector3Int(room.center.x, room.center.y, room.maxBounds.z)
        };

        float minDistance = float.MaxValue;
        Vector3Int bestPoint = room.center;
        foreach (var fc in faceCenters)
        {
            float d = Vector3Int.Distance(fc, target);
            if (d < minDistance) { minDistance = d; bestPoint = fc; }
        }
        return bestPoint;
    }

    // =========================================================================
    // Carving
    // =========================================================================

    private void CarveCuboidRoomIntoGrid(CuboidRoom room, Vector3Int worldOffset,
                                         Vector3Int chunkSize, ref NativeArray<byte> grid)
    {
        Vector3Int localMin = room.minBounds - worldOffset;
        Vector3Int localMax = room.maxBounds - worldOffset;

        int startX = Mathf.Max(localMin.x, 0), endX = Mathf.Min(localMax.x, chunkSize.x - 1);
        int startY = Mathf.Max(localMin.y, 0), endY = Mathf.Min(localMax.y, chunkSize.y - 1);
        int startZ = Mathf.Max(localMin.z, 0), endZ = Mathf.Min(localMax.z, chunkSize.z - 1);

        int xCount = endX - startX + 1;
        int yCount = endY - startY + 1;
        int zCount = endZ - startZ + 1;

        if (xCount <= 0 || yCount <= 0 || zCount <= 0) return;

        int total = xCount * yCount * zCount;
        new CarveRoomSolidJob
        {
            grid      = grid,
            chunkSize = new int3(chunkSize.x, chunkSize.y, chunkSize.z),
            min       = new int3(startX, startY, startZ),
            maxValid  = new int3(chunkSize.x - 1, chunkSize.y - 1, chunkSize.z - 1),
            yCount    = yCount,
            zCount    = zCount
        }.Schedule(total, 64).Complete();
    }

    private void CarveGeometricCorridorIntoGrid(Corridor corridor, Vector3Int worldOffset,
                                                Vector3Int chunkSize, ref NativeArray<byte> grid)
    {
        foreach (var worldPoint in corridor.path)
        {
            Vector3Int localCenter = worldPoint - worldOffset;
            int halfWidth = corridor.width / 2;
            int startY = localCenter.y - corridor.height / 2;

            for (int dx = -halfWidth; dx <= halfWidth; dx++)
            for (int dz = -halfWidth; dz <= halfWidth; dz++)
            for (int dy = 0; dy < corridor.height; dy++)
            {
                Vector3Int localPos = new Vector3Int(localCenter.x + dx, startY + dy, localCenter.z + dz);
                if (localPos.x >= 0 && localPos.x < chunkSize.x &&
                    localPos.y >= 0 && localPos.y < chunkSize.y &&
                    localPos.z >= 0 && localPos.z < chunkSize.z)
                {
                    grid[localPos.x * (chunkSize.y * chunkSize.z) + localPos.y * chunkSize.z + localPos.z] = 1;
                }
            }
        }
    }

    // =========================================================================
    // Registration / spatial helpers
    // =========================================================================

    private void RegisterRoom(CuboidRoom room)
    {
        allRooms[room.id] = room;

        Vector3Int gridMin = GetSpatialGridCell(room.minBounds);
        Vector3Int gridMax = GetSpatialGridCell(room.maxBounds);

        for (int x = gridMin.x; x <= gridMax.x; x++)
        for (int y = gridMin.y; y <= gridMax.y; y++)
        for (int z = gridMin.z; z <= gridMax.z; z++)
        {
            Vector3Int cell = new Vector3Int(x, y, z);
            if (!roomSpatialGrid.ContainsKey(cell)) roomSpatialGrid[cell] = new HashSet<int>();
            roomSpatialGrid[cell].Add(room.id);
        }

        foreach (var chunkCoord in room.GetOccupiedChunks(currentChunkSize))
        {
            if (!chunkToRooms.ContainsKey(chunkCoord)) chunkToRooms[chunkCoord] = new HashSet<int>();
            chunkToRooms[chunkCoord].Add(room.id);
        }
    }

    private void RegisterCorridor(Corridor corridor)
    {
        foreach (var chunkCoord in corridor.GetOccupiedChunks(currentChunkSize))
        {
            if (!chunkToCorridors.ContainsKey(chunkCoord)) chunkToCorridors[chunkCoord] = new HashSet<int>();
            chunkToCorridors[chunkCoord].Add(corridor.id);
        }
    }

    private void RemoveRoomFromSpatialGrid(CuboidRoom room)
    {
        Vector3Int gridMin = GetSpatialGridCell(room.minBounds);
        Vector3Int gridMax = GetSpatialGridCell(room.maxBounds);

        for (int x = gridMin.x; x <= gridMax.x; x++)
        for (int y = gridMin.y; y <= gridMax.y; y++)
        for (int z = gridMin.z; z <= gridMax.z; z++)
        {
            Vector3Int cell = new Vector3Int(x, y, z);
            if (roomSpatialGrid.TryGetValue(cell, out HashSet<int> ids))
            {
                ids.Remove(room.id);
                if (ids.Count == 0) roomSpatialGrid.Remove(cell);
            }
        }
    }

    private void RemoveInactiveRooms()
    {
        List<int> toRemove = new List<int>();
        foreach (var kvp in allRooms)
            if (!kvp.Value.isActive) toRemove.Add(kvp.Key);
        foreach (int id in toRemove) allRooms.Remove(id);
    }

    private bool IsPointInAnyRoomOptimized(Vector3Int worldPos)
    {
        Vector3Int cell = GetSpatialGridCell(worldPos);
        if (roomSpatialGrid.TryGetValue(cell, out HashSet<int> ids))
            foreach (int id in ids)
                if (allRooms.TryGetValue(id, out CuboidRoom r) && r.isActive && r.ContainsPoint(worldPos))
                    return true;
        return false;
    }

    private bool WouldOverlapOtherRooms(Vector3Int min, Vector3Int max, int excludeId)
    {
        Vector3Int testMin = GetSpatialGridCell(min);
        Vector3Int testMax = GetSpatialGridCell(max);

        for (int x = testMin.x; x <= testMax.x; x++)
        for (int y = testMin.y; y <= testMax.y; y++)
        for (int z = testMin.z; z <= testMax.z; z++)
        {
            Vector3Int cell = new Vector3Int(x, y, z);
            if (roomSpatialGrid.TryGetValue(cell, out HashSet<int> ids))
                foreach (int id in ids)
                    if (id != excludeId && allRooms.TryGetValue(id, out CuboidRoom other) && other.isActive)
                        if (!(max.x < other.minBounds.x || min.x > other.maxBounds.x ||
                              max.y < other.minBounds.y || min.y > other.maxBounds.y ||
                              max.z < other.minBounds.z || min.z > other.maxBounds.z))
                            return true;
        }
        return false;
    }

    private List<CuboidRoom> GetRoomsForChunkDirect(Vector3Int chunkCoord)
    {
        List<CuboidRoom> rooms = new List<CuboidRoom>();
        if (chunkToRooms.TryGetValue(chunkCoord, out HashSet<int> ids))
            foreach (int id in ids)
                if (allRooms.TryGetValue(id, out CuboidRoom r) && r.isActive)
                    rooms.Add(r);
        return rooms;
    }

    private List<Corridor> GetCorridorsForChunkDirect(Vector3Int chunkCoord)
    {
        List<Corridor> corridors = new List<Corridor>();
        if (chunkToCorridors.TryGetValue(chunkCoord, out HashSet<int> ids))
            foreach (int id in ids)
                if (allCorridors.TryGetValue(id, out Corridor c) && c.isActive)
                    corridors.Add(c);
        return corridors;
    }

    private Vector3Int GetSpatialGridCell(Vector3Int worldPos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / (float)spatialGridSize),
            Mathf.FloorToInt(worldPos.y / (float)spatialGridSize),
            Mathf.FloorToInt(worldPos.z / (float)spatialGridSize));
    }

    private float GetCachedNoise(Vector3Int worldPos)
    {
        if (useNoiseCache && noiseCache.TryGetValue(worldPos, out float cached))
            return cached;

        float x = (worldPos.x + noiseOffset.x) * noiseScale.x;
        float y = (worldPos.y + noiseOffset.y) * noiseScale.y;
        float z = (worldPos.z + noiseOffset.z) * noiseScale.z;
        float value = (Mathf.PerlinNoise(x, y + z) + Mathf.PerlinNoise(x + z, y)) * 0.5f;

        if (useNoiseCache)
        {
            if (noiseCache.Count >= MAX_NOISE_CACHE_SIZE) noiseCache.Clear();
            noiseCache[worldPos] = value;
        }
        return value;
    }

    private int GetChunkSeed(Vector3Int c)
        => worldSeed ^ (c.x * 73856093) ^ (c.y * 19349663) ^ (c.z * 83492791);

    private Vector3Int WorldToChunkCoord(Vector3Int worldPos, Vector3Int chunkSize)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / (float)chunkSize.x),
            Mathf.FloorToInt(worldPos.y / (float)chunkSize.y),
            Mathf.FloorToInt(worldPos.z / (float)chunkSize.z));
    }

    private int GetChunkDistance(Vector3Int a, Vector3Int b)
        => Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z));

    private Vector3Int IndexToDirection(int index)
    {
        switch (index)
        {
            case 0: return Vector3Int.left;
            case 1: return Vector3Int.right;
            case 2: return Vector3Int.down;
            case 3: return Vector3Int.up;
            case 4: return Vector3Int.back;
            case 5: return Vector3Int.forward;
            default: return Vector3Int.zero;
        }
    }

    // =========================================================================
    // Grid management
    // =========================================================================

    private void ClearGrid(ref NativeArray<byte> grid, Vector3Int chunkSize)
    {
        int voxelCount = chunkSize.x * chunkSize.y * chunkSize.z;
        if (!grid.IsCreated || grid.Length != voxelCount)
        {
            if (grid.IsCreated) grid.Dispose();
            grid = new NativeArray<byte>(voxelCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }
        else
        {
            new ClearByteGridJob { grid = grid }.Schedule(voxelCount, 128).Complete();
        }
    }

    // =========================================================================
    // Jobs
    // =========================================================================

    [BurstCompile]
    private struct ClearByteGridJob : IJobParallelFor
    {
        public NativeArray<byte> grid;
        public void Execute(int index) => grid[index] = 0;
    }

    [BurstCompile]
    private struct CarveRoomSolidJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction]
        public NativeArray<byte> grid;
        [ReadOnly] public int3 chunkSize;
        [ReadOnly] public int3 min;
        [ReadOnly] public int3 maxValid;
        [ReadOnly] public int yCount;
        [ReadOnly] public int zCount;

        public void Execute(int index)
        {
            int xOffset = index / (yCount * zCount);
            int rem     = index - xOffset * yCount * zCount;
            int yOffset = rem / zCount;
            int zOffset = rem - yOffset * zCount;

            int x = min.x + xOffset;
            int y = min.y + yOffset;
            int z = min.z + zOffset;

            if (x < 0 || x > maxValid.x || y < 0 || y > maxValid.y || z < 0 || z > maxValid.z)
                return;

            grid[x * (chunkSize.y * chunkSize.z) + y * chunkSize.z + z] = 1;
        }
    }
}