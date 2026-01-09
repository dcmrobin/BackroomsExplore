using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class CrossChunkRoomGenerator : MonoBehaviour
{
    [Header("3D Noise Settings")]
    [SerializeField] private Vector3 noiseScale = new Vector3(0.03f, 0.03f, 0.03f);
    [SerializeField] private Vector3 noiseOffset = Vector3.zero;
    [SerializeField] private bool useNoiseCache = true; // NEW: Performance optimization
    
    [Header("Room Detection")]
    [SerializeField] private float roomThreshold = 0.65f;
    [SerializeField] private float roomExpansionThreshold = 0.55f;
    [SerializeField] private int minRoomSize = 4;
    [SerializeField] private int maxRoomSize = 30;
    [SerializeField] private int roomScanStep = 4;
    
    [Header("Room Expansion")]
    [SerializeField] private int maxExpansionSteps = 20;
    [SerializeField] private float expansionStepSize = 1.0f;
    [SerializeField] private bool expandToGrid = true;
    [SerializeField] private int gridAlignment = 2;
    
    [Header("Corridor Settings")]
    [SerializeField] private int minCorridorWidth = 3;
    [SerializeField] private int maxCorridorWidth = 5;
    [SerializeField] private int corridorHeight = 4;
    [SerializeField] private int verticalShaftSize = 3;
    [SerializeField] private float verticalConnectionChance = 0.3f;
    
    [Header("Memory Management")]
    [SerializeField] private int maxRoomsToKeep = 1000;
    [SerializeField] private int pruneRadius = 10; // Chunks
    
    // Global storage
    private Dictionary<int, CuboidRoom> allRooms = new Dictionary<int, CuboidRoom>();
    private Dictionary<int, Corridor> allCorridors = new Dictionary<int, Corridor>();
    private Dictionary<Vector3Int, HashSet<int>> chunkToRooms = new Dictionary<Vector3Int, HashSet<int>>();
    private Dictionary<Vector3Int, HashSet<int>> chunkToCorridors = new Dictionary<Vector3Int, HashSet<int>>();
    
    // Spatial partitioning - FIXED: Actually used now
    private Dictionary<Vector3Int, HashSet<int>> roomSpatialGrid = new Dictionary<Vector3Int, HashSet<int>>();
    private int spatialGridSize = 32;
    
    // State
    private int nextRoomId = 0;
    private int nextCorridorId = 0;
    private Vector3Int currentChunkSize;
    private HashSet<Vector3Int> processedChunks = new HashSet<Vector3Int>();
    
    // Performance optimizations
    private Dictionary<Vector3Int, float> noiseCache = new Dictionary<Vector3Int, float>();
    private object generationLock = new object(); // FIXED: Thread safety
    
    // Room class
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
            
            // Ensure minimum size - FIXED: Prevent 1x1x1 rooms
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
            {
                for (int y = minChunk.y; y <= maxChunk.y; y++)
                {
                    for (int z = minChunk.z; z <= maxChunk.z; z++)
                    {
                        chunks.Add(new Vector3Int(x, y, z));
                    }
                }
            }
            
            return chunks.ToList();
        }
        
        public int Volume => size.x * size.y * size.z;
        
        private Vector3Int WorldToChunkCoord(Vector3Int worldPos, Vector3Int chunkSize)
        {
            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x / (float)chunkSize.x),
                Mathf.FloorToInt(worldPos.y / (float)chunkSize.y),
                Mathf.FloorToInt(worldPos.z / (float)chunkSize.z)
            );
        }
        
        private static readonly Vector3Int Vector3One = new Vector3Int(1, 1, 1);
    }
    
    // Corridor class
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
            
            foreach (var point in path)
            {
                Vector3Int chunk = WorldToChunkCoord(point, chunkSize);
                chunks.Add(chunk);
                
                // Include neighboring chunks for corridor width/height
                Vector3Int[] neighbors = {
                    Vector3Int.up, Vector3Int.down, Vector3Int.right,
                    Vector3Int.left, Vector3Int.forward, Vector3Int.back
                };
                
                foreach (var dir in neighbors)
                {
                    chunks.Add(chunk + dir);
                }
            }
            
            return chunks.ToList();
        }
        
        private Vector3Int WorldToChunkCoord(Vector3Int worldPos, Vector3Int chunkSize)
        {
            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x / (float)chunkSize.x),
                Mathf.FloorToInt(worldPos.y / (float)chunkSize.y),
                Mathf.FloorToInt(worldPos.z / (float)chunkSize.z)
            );
        }
    }
    
    public void Initialize(Vector3Int chunkSize)
    {
        currentChunkSize = chunkSize;
        allRooms.Clear();
        allCorridors.Clear();
        chunkToRooms.Clear();
        chunkToCorridors.Clear();
        roomSpatialGrid.Clear();
        processedChunks.Clear();
        noiseCache.Clear();
        nextRoomId = 0;
        nextCorridorId = 0;
        
        noiseOffset = new Vector3(
            Random.Range(-1000f, 1000f),
            Random.Range(-1000f, 1000f),
            Random.Range(-1000f, 1000f)
        );
    }
    
    public void GenerateForChunk(Vector3Int chunkCoord, Vector3Int chunkSize, ref bool[,,] finalGrid)
    {
        lock (generationLock) // FIXED: Thread safety
        {
            currentChunkSize = chunkSize;
            Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
            
            ClearGrid(ref finalGrid, chunkSize);
            
            if (!processedChunks.Contains(chunkCoord))
            {
                GenerateRoomsForChunk(chunkCoord);
                processedChunks.Add(chunkCoord);
            }
            
            // Get rooms for this chunk (direct access, no double lookup)
            List<CuboidRoom> relevantRooms = GetRoomsForChunkDirect(chunkCoord);
            foreach (var room in relevantRooms)
            {
                if (room.isActive)
                    CarveCuboidRoomIntoGrid(room, worldOffset, chunkSize, ref finalGrid);
            }
            
            // Get corridors for this chunk (direct access)
            List<Corridor> relevantCorridors = GetCorridorsForChunkDirect(chunkCoord);
            foreach (var corridor in relevantCorridors)
            {
                if (corridor.isActive)
                    CarveGeometricCorridorIntoGrid(corridor, worldOffset, chunkSize, ref finalGrid);
            }
        }
    }
    
    private void GenerateRoomsForChunk(Vector3Int chunkCoord)
    {
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, currentChunkSize);
        
        for (int x = 0; x < currentChunkSize.x; x += roomScanStep)
        {
            for (int y = 0; y < currentChunkSize.y; y += roomScanStep)
            {
                for (int z = 0; z < currentChunkSize.z; z += roomScanStep)
                {
                    Vector3Int worldPos = new Vector3Int(x, y, z) + worldOffset;
                    
                    float noiseValue = GetCachedNoise(worldPos);
                    
                    if (noiseValue > roomThreshold && !IsPointInAnyRoomOptimized(worldPos))
                    {
                        CreateAndExpandRoom(worldPos, chunkCoord);
                    }
                }
            }
        }
        
        ConnectRooms();
    }
    
    private float GetCachedNoise(Vector3Int worldPos)
    {
        if (useNoiseCache && noiseCache.TryGetValue(worldPos, out float cachedValue))
            return cachedValue;
        
        float x = (worldPos.x + noiseOffset.x) * noiseScale.x;
        float y = (worldPos.y + noiseOffset.y) * noiseScale.y;
        float z = (worldPos.z + noiseOffset.z) * noiseScale.z;
        
        float noiseValue = Mathf.PerlinNoise(x + z * 0.5f, y + z * 0.5f);
        
        if (useNoiseCache)
            noiseCache[worldPos] = noiseValue;
        
        return noiseValue;
    }
    
    private bool IsPointInAnyRoomOptimized(Vector3Int worldPos)
    {
        Vector3Int gridCell = GetSpatialGridCell(worldPos);
        
        if (roomSpatialGrid.TryGetValue(gridCell, out HashSet<int> roomIds))
        {
            foreach (int roomId in roomIds)
            {
                if (allRooms.TryGetValue(roomId, out CuboidRoom room) && room.isActive && room.ContainsPoint(worldPos))
                    return true;
            }
        }
        return false;
    }
    
    private void CreateAndExpandRoom(Vector3Int seedPos, Vector3Int genChunk)
    {
        CuboidRoom room = new CuboidRoom(nextRoomId++, seedPos, genChunk);
        ExpandRoomGeometrically(room);
        
        // Ensure minimum viable room
        if (room.size.x >= minRoomSize && room.size.y >= minRoomSize && room.size.z >= minRoomSize)
        {
            RegisterRoom(room);
        }
        else
        {
            // Room too small, discard it
            nextRoomId--; // Reuse the ID
        }
    }
    
    private void ExpandRoomGeometrically(CuboidRoom room)
    {
        Vector3Int currentMin = room.center;
        Vector3Int currentMax = room.center;
        bool[] expansionBlocked = new bool[6]; // Track permanently blocked directions
        
        for (int step = 0; step < maxExpansionSteps; step++)
        {
            bool expanded = false;
            
            // Try each direction if not permanently blocked
            if (!expansionBlocked[0] && TryExpandDirection(room, ref currentMin, ref currentMax, Vector3Int.left))
                expanded = true;
            else
                expansionBlocked[0] = true;
                
            if (!expansionBlocked[1] && TryExpandDirection(room, ref currentMin, ref currentMax, Vector3Int.right))
                expanded = true;
            else
                expansionBlocked[1] = true;
                
            if (!expansionBlocked[2] && TryExpandDirection(room, ref currentMin, ref currentMax, Vector3Int.down))
                expanded = true;
            else
                expansionBlocked[2] = true;
                
            if (!expansionBlocked[3] && TryExpandDirection(room, ref currentMin, ref currentMax, Vector3Int.up))
                expanded = true;
            else
                expansionBlocked[3] = true;
                
            if (!expansionBlocked[4] && TryExpandDirection(room, ref currentMin, ref currentMax, Vector3Int.back))
                expanded = true;
            else
                expansionBlocked[4] = true;
                
            if (!expansionBlocked[5] && TryExpandDirection(room, ref currentMin, ref currentMax, Vector3Int.forward))
                expanded = true;
            else
                expansionBlocked[5] = true;
            
            if (!expanded || RoomExceedsMaxSize(currentMin, currentMax))
                break;
        }
        
        room.SetBounds(currentMin, currentMax);
    }
    
    private bool TryExpandDirection(CuboidRoom room, ref Vector3Int currentMin, ref Vector3Int currentMax, Vector3Int direction)
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
        else // forward
        {
            expandMin = new Vector3Int(currentMin.x, currentMin.y, currentMax.z + 1);
            expandMax = new Vector3Int(currentMax.x, currentMax.y, currentMax.z + gridAlignment);
        }
        
        if (WouldOverlapOtherRooms(expandMin, expandMax, room.id))
            return false;
        
        int validSamples = 0;
        int totalSamples = 0;
        
        for (int x = expandMin.x; x <= expandMax.x; x += gridAlignment)
        {
            for (int y = expandMin.y; y <= expandMax.y; y += gridAlignment)
            {
                for (int z = expandMin.z; z <= expandMax.z; z += gridAlignment)
                {
                    totalSamples++;
                    Vector3Int samplePos = new Vector3Int(x, y, z);
                    float noiseValue = GetCachedNoise(samplePos);
                    
                    if (noiseValue > roomExpansionThreshold)
                    {
                        validSamples++;
                    }
                }
            }
        }
        
        if (totalSamples > 0 && (float)validSamples / totalSamples >= 0.6f)
        {
            if (direction == Vector3Int.left) currentMin.x = expandMin.x;
            else if (direction == Vector3Int.right) currentMax.x = expandMax.x;
            else if (direction == Vector3Int.down) currentMin.y = expandMin.y;
            else if (direction == Vector3Int.up) currentMax.y = expandMax.y;
            else if (direction == Vector3Int.back) currentMin.z = expandMin.z;
            else currentMax.z = expandMax.z;
            
            return true;
        }
        
        return false;
    }
    
    private bool RoomExceedsMaxSize(Vector3Int min, Vector3Int max)
    {
        Vector3Int size = max - min + new Vector3Int(1, 1, 1);
        return size.x > maxRoomSize || size.y > maxRoomSize || size.z > maxRoomSize;
    }
    
    private bool WouldOverlapOtherRooms(Vector3Int min, Vector3Int max, int excludeRoomId)
    {
        // Quick spatial grid check
        Vector3Int testMinChunk = GetSpatialGridCell(min);
        Vector3Int testMaxChunk = GetSpatialGridCell(max);
        
        for (int x = testMinChunk.x; x <= testMaxChunk.x; x++)
        {
            for (int y = testMinChunk.y; y <= testMaxChunk.y; y++)
            {
                for (int z = testMinChunk.z; z <= testMaxChunk.z; z++)
                {
                    Vector3Int gridCell = new Vector3Int(x, y, z);
                    if (roomSpatialGrid.TryGetValue(gridCell, out HashSet<int> roomIds))
                    {
                        foreach (int roomId in roomIds)
                        {
                            if (roomId != excludeRoomId && allRooms.TryGetValue(roomId, out CuboidRoom otherRoom) && otherRoom.isActive)
                            {
                                // Quick AABB overlap test
                                if (!(max.x < otherRoom.minBounds.x || min.x > otherRoom.maxBounds.x ||
                                      max.y < otherRoom.minBounds.y || min.y > otherRoom.maxBounds.y ||
                                      max.z < otherRoom.minBounds.z || min.z > otherRoom.maxBounds.z))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
        }
        
        return false;
    }
    
    private void ConnectRooms()
    {
        List<CuboidRoom> activeRooms = allRooms.Values.Where(r => r.isActive).ToList();
        
        if (activeRooms.Count < 2)
            return;
        
        // Create minimum spanning tree
        List<CuboidRoom> connectedRooms = new List<CuboidRoom>();
        List<CuboidRoom> unconnectedRooms = new List<CuboidRoom>(activeRooms);
        
        CuboidRoom startRoom = unconnectedRooms[Random.Range(0, unconnectedRooms.Count)];
        connectedRooms.Add(startRoom);
        unconnectedRooms.Remove(startRoom);
        
        while (unconnectedRooms.Count > 0)
        {
            float minDistance = float.MaxValue;
            CuboidRoom closestUnconnected = null;
            CuboidRoom closestConnected = null;
            
            foreach (var connectedRoom in connectedRooms)
            {
                foreach (var unconnectedRoom in unconnectedRooms)
                {
                    float distance = Vector3Int.Distance(connectedRoom.center, unconnectedRoom.center);
                    
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        closestUnconnected = unconnectedRoom;
                        closestConnected = connectedRoom;
                    }
                }
            }
            
            if (closestUnconnected != null && closestConnected != null)
            {
                CreateGeometricCorridor(closestConnected, closestUnconnected);
                connectedRooms.Add(closestUnconnected);
                unconnectedRooms.Remove(closestUnconnected);
            }
        }
    }
    
    private void CreateGeometricCorridor(CuboidRoom roomA, CuboidRoom roomB)
    {
        bool makeVertical = Random.value < verticalConnectionChance && 
                           Mathf.Abs(roomA.center.y - roomB.center.y) > minRoomSize;
        
        Corridor corridor = new Corridor
        {
            id = nextCorridorId++,
            roomAId = roomA.id,
            roomBId = roomB.id,
            isVertical = makeVertical,
            width = Random.Range(minCorridorWidth, maxCorridorWidth + 1),
            height = makeVertical ? verticalShaftSize : corridorHeight
        };
        
        GenerateStraightCorridorPath(roomA, roomB, corridor);
        
        // INTENTIONAL: We don't check if corridor intersects other rooms
        // This is a design choice - creates interesting overlaps
        
        allCorridors[corridor.id] = corridor;
        RegisterCorridor(corridor);
    }
    
    private void GenerateStraightCorridorPath(CuboidRoom roomA, CuboidRoom roomB, Corridor corridor)
    {
        Vector3Int pointA = GetGeometricConnectionPoint(roomA, roomB.center);
        Vector3Int pointB = GetGeometricConnectionPoint(roomB, roomA.center);
        
        // FIXED: Corridor path at appropriate height
        if (!corridor.isVertical)
        {
            // Horizontal corridors should be at floor level
            pointA.y = roomA.minBounds.y;
            pointB.y = roomB.minBounds.y;
        }
        
        Vector3Int current = pointA;
        corridor.path.Add(current);
        
        int xDir = Mathf.Clamp(pointB.x - pointA.x, -1, 1);
        while (current.x != pointB.x)
        {
            current.x += xDir;
            corridor.path.Add(current);
        }
        
        int zDir = Mathf.Clamp(pointB.z - current.z, -1, 1);
        while (current.z != pointB.z)
        {
            current.z += zDir;
            corridor.path.Add(current);
        }
        
        if (current.y != pointB.y)
        {
            int yDir = Mathf.Clamp(pointB.y - current.y, -1, 1);
            while (current.y != pointB.y)
            {
                current.y += yDir;
                corridor.path.Add(current);
            }
        }
    }
    
    private Vector3Int GetGeometricConnectionPoint(CuboidRoom room, Vector3Int target)
    {
        float minDistance = float.MaxValue;
        Vector3Int bestPoint = room.center;
        
        Vector3Int[] faceCenters = {
            new Vector3Int(room.minBounds.x, room.center.y, room.center.z),
            new Vector3Int(room.maxBounds.x, room.center.y, room.center.z),
            new Vector3Int(room.center.x, room.minBounds.y, room.center.z),
            new Vector3Int(room.center.x, room.maxBounds.y, room.center.z),
            new Vector3Int(room.center.x, room.center.y, room.minBounds.z),
            new Vector3Int(room.center.x, room.center.y, room.maxBounds.z)
        };
        
        foreach (var faceCenter in faceCenters)
        {
            float distance = Vector3Int.Distance(faceCenter, target);
            if (distance < minDistance)
            {
                minDistance = distance;
                bestPoint = faceCenter;
            }
        }
        
        return bestPoint;
    }
    
    private void CarveCuboidRoomIntoGrid(CuboidRoom room, Vector3Int worldOffset, Vector3Int chunkSize, ref bool[,,] grid)
    {
        Vector3Int localMin = room.minBounds - worldOffset;
        Vector3Int localMax = room.maxBounds - worldOffset;
        
        int startX = Mathf.Max(localMin.x, 0);
        int endX = Mathf.Min(localMax.x, chunkSize.x - 1);
        int startY = Mathf.Max(localMin.y, 0);
        int endY = Mathf.Min(localMax.y, chunkSize.y - 1);
        int startZ = Mathf.Max(localMin.z, 0);
        int endZ = Mathf.Min(localMax.z, chunkSize.z - 1);
        
        for (int x = startX; x <= endX; x++)
        {
            for (int y = startY; y <= endY; y++)
            {
                for (int z = startZ; z <= endZ; z++)
                {
                    grid[x, y, z] = true;
                }
            }
        }
    }
    
    private void CarveGeometricCorridorIntoGrid(Corridor corridor, Vector3Int worldOffset, Vector3Int chunkSize, ref bool[,,] grid)
    {
        foreach (var worldPoint in corridor.path)
        {
            Vector3Int localCenter = worldPoint - worldOffset;
            int halfWidth = corridor.width / 2;
            
            // FIXED: Corridor centered vertically on path point
            int startY = localCenter.y - corridor.height / 2;
            
            for (int dx = -halfWidth; dx <= halfWidth; dx++)
            {
                for (int dz = -halfWidth; dz <= halfWidth; dz++)
                {
                    for (int dy = 0; dy < corridor.height; dy++)
                    {
                        int y = startY + dy;
                        
                        Vector3Int localPos = new Vector3Int(
                            localCenter.x + dx,
                            y,
                            localCenter.z + dz
                        );
                        
                        if (localPos.x >= 0 && localPos.x < chunkSize.x &&
                            localPos.y >= 0 && localPos.y < chunkSize.y &&
                            localPos.z >= 0 && localPos.z < chunkSize.z)
                        {
                            grid[localPos.x, localPos.y, localPos.z] = true;
                        }
                    }
                }
            }
        }
    }
    
    private void RegisterRoom(CuboidRoom room)
    {
        allRooms[room.id] = room;
        
        // Register in spatial grid
        Vector3Int gridMin = GetSpatialGridCell(room.minBounds);
        Vector3Int gridMax = GetSpatialGridCell(room.maxBounds);
        
        for (int x = gridMin.x; x <= gridMax.x; x++)
        {
            for (int y = gridMin.y; y <= gridMax.y; y++)
            {
                for (int z = gridMin.z; z <= gridMax.z; z++)
                {
                    Vector3Int gridCell = new Vector3Int(x, y, z);
                    if (!roomSpatialGrid.ContainsKey(gridCell))
                        roomSpatialGrid[gridCell] = new HashSet<int>();
                    roomSpatialGrid[gridCell].Add(room.id);
                }
            }
        }
        
        // Register with chunks
        List<Vector3Int> occupiedChunks = room.GetOccupiedChunks(currentChunkSize);
        foreach (var chunkCoord in occupiedChunks)
        {
            if (!chunkToRooms.ContainsKey(chunkCoord))
                chunkToRooms[chunkCoord] = new HashSet<int>();
            chunkToRooms[chunkCoord].Add(room.id);
        }
    }
    
    private void RegisterCorridor(Corridor corridor)
    {
        List<Vector3Int> occupiedChunks = corridor.GetOccupiedChunks(currentChunkSize);
        foreach (var chunkCoord in occupiedChunks)
        {
            if (!chunkToCorridors.ContainsKey(chunkCoord))
                chunkToCorridors[chunkCoord] = new HashSet<int>();
            chunkToCorridors[chunkCoord].Add(corridor.id);
        }
    }
    
    private Vector3Int GetSpatialGridCell(Vector3Int worldPos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / (float)spatialGridSize),
            Mathf.FloorToInt(worldPos.y / (float)spatialGridSize),
            Mathf.FloorToInt(worldPos.z / (float)spatialGridSize)
        );
    }
    
    private List<CuboidRoom> GetRoomsForChunkDirect(Vector3Int chunkCoord)
    {
        List<CuboidRoom> rooms = new List<CuboidRoom>();
        
        if (chunkToRooms.TryGetValue(chunkCoord, out HashSet<int> roomIds))
        {
            foreach (int roomId in roomIds)
            {
                if (allRooms.TryGetValue(roomId, out CuboidRoom room) && room.isActive)
                    rooms.Add(room);
            }
        }
        
        return rooms;
    }
    
    private List<Corridor> GetCorridorsForChunkDirect(Vector3Int chunkCoord)
    {
        List<Corridor> corridors = new List<Corridor>();
        
        if (chunkToCorridors.TryGetValue(chunkCoord, out HashSet<int> corridorIds))
        {
            foreach (int corridorId in corridorIds)
            {
                if (allCorridors.TryGetValue(corridorId, out Corridor corridor) && corridor.isActive)
                    corridors.Add(corridor);
            }
        }
        
        return corridors;
    }
    
    private void ClearGrid(ref bool[,,] grid, Vector3Int chunkSize)
    {
        if (grid == null || grid.GetLength(0) != chunkSize.x || 
            grid.GetLength(1) != chunkSize.y || grid.GetLength(2) != chunkSize.z)
        {
            grid = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
        }
        else
        {
            System.Array.Clear(grid, 0, grid.Length);
        }
    }
    
    // FIXED: Memory management - prune distant rooms
    public void PruneDistantData(Vector3Int centerChunk)
    {
        lock (generationLock)
        {
            // Mark distant rooms as inactive
            foreach (var room in allRooms.Values)
            {
                if (room.isActive)
                {
                    Vector3Int roomCenterChunk = WorldToChunkCoord(room.center, currentChunkSize);
                    int distance = GetChunkDistance(roomCenterChunk, centerChunk);
                    
                    if (distance > pruneRadius)
                    {
                        room.isActive = false;
                        
                        // Remove from spatial grid
                        RemoveRoomFromSpatialGrid(room);
                        
                        // Remove from chunk mappings
                        List<Vector3Int> occupiedChunks = room.GetOccupiedChunks(currentChunkSize);
                        foreach (var chunkCoord in occupiedChunks)
                        {
                            if (chunkToRooms.TryGetValue(chunkCoord, out HashSet<int> roomIds))
                            {
                                roomIds.Remove(room.id);
                                if (roomIds.Count == 0)
                                    chunkToRooms.Remove(chunkCoord);
                            }
                        }
                    }
                }
            }
            
            // Mark distant corridors as inactive
            foreach (var corridor in allCorridors.Values)
            {
                if (corridor.isActive && corridor.path.Count > 0)
                {
                    Vector3Int corridorCenterChunk = WorldToChunkCoord(corridor.path[corridor.path.Count / 2], currentChunkSize);
                    int distance = GetChunkDistance(corridorCenterChunk, centerChunk);
                    
                    if (distance > pruneRadius)
                    {
                        corridor.isActive = false;
                        
                        // Remove from chunk mappings
                        List<Vector3Int> occupiedChunks = corridor.GetOccupiedChunks(currentChunkSize);
                        foreach (var chunkCoord in occupiedChunks)
                        {
                            if (chunkToCorridors.TryGetValue(chunkCoord, out HashSet<int> corridorIds))
                            {
                                corridorIds.Remove(corridor.id);
                                if (corridorIds.Count == 0)
                                    chunkToCorridors.Remove(chunkCoord);
                            }
                        }
                    }
                }
            }
            
            // Optionally: Remove completely if too many inactive items
            if (allRooms.Count > maxRoomsToKeep * 2)
            {
                RemoveInactiveRooms();
            }
        }
    }
    
    private void RemoveRoomFromSpatialGrid(CuboidRoom room)
    {
        Vector3Int gridMin = GetSpatialGridCell(room.minBounds);
        Vector3Int gridMax = GetSpatialGridCell(room.maxBounds);
        
        for (int x = gridMin.x; x <= gridMax.x; x++)
        {
            for (int y = gridMin.y; y <= gridMax.y; y++)
            {
                for (int z = gridMin.z; z <= gridMax.z; z++)
                {
                    Vector3Int gridCell = new Vector3Int(x, y, z);
                    if (roomSpatialGrid.TryGetValue(gridCell, out HashSet<int> roomIds))
                    {
                        roomIds.Remove(room.id);
                        if (roomIds.Count == 0)
                            roomSpatialGrid.Remove(gridCell);
                    }
                }
            }
        }
    }
    
    private void RemoveInactiveRooms()
    {
        List<int> roomsToRemove = new List<int>();
        
        foreach (var kvp in allRooms)
        {
            if (!kvp.Value.isActive)
                roomsToRemove.Add(kvp.Key);
        }
        
        foreach (int roomId in roomsToRemove)
        {
            allRooms.Remove(roomId);
        }
    }
    
    private Vector3Int WorldToChunkCoord(Vector3Int worldPos, Vector3Int chunkSize)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / (float)chunkSize.x),
            Mathf.FloorToInt(worldPos.y / (float)chunkSize.y),
            Mathf.FloorToInt(worldPos.z / (float)chunkSize.z)
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
    
    public void ClearChunkData(Vector3Int chunkCoord)
    {
        lock (generationLock)
        {
            processedChunks.Remove(chunkCoord);
        }
    }
}