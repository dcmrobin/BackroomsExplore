using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class CrossChunkRoomGenerator : MonoBehaviour
{
    [Header("Noise Settings")]
    [SerializeField] private float noiseScale = 0.08f;
    [Range(0, 1)] [SerializeField] private float fillThreshold = 0.45f;
    [SerializeField] private int smoothingIterations = 2;
    
    [Header("Room Settings")]
    [SerializeField] private int minRoomSize = 4;
    [SerializeField] private int maxRoomSize = 12;
    
    [Header("Corridor Settings")]
    [SerializeField] private int minCorridorWidth = 2;
    [SerializeField] private int maxCorridorWidth = 4;
    [SerializeField] private int minCorridorHeight = 3;
    [SerializeField] private int maxCorridorHeight = 5;
    [SerializeField] private bool variableCorridorSize = true;

    [Header("Vertical Connections")]
    [SerializeField] private float verticalConnectionChance = 0.2f;
    [SerializeField] private int minStairHeight = 4;
    [SerializeField] private int maxStairHeight = 8;
    
    [Header("Generation Optimization")]
    [SerializeField] private int scanStep = 2;
    [SerializeField] private int maxChunkRangeForConnections = 2;
    
    // GLOBAL STORAGE (not per-chunk)
    private Dictionary<int, CuboidRoom> allRooms = new Dictionary<int, CuboidRoom>();
    private Dictionary<int, Corridor> allCorridors = new Dictionary<int, Corridor>();
    private Dictionary<Vector3Int, HashSet<int>> chunkToRooms = new Dictionary<Vector3Int, HashSet<int>>();
    private Dictionary<Vector3Int, HashSet<int>> chunkToCorridors = new Dictionary<Vector3Int, HashSet<int>>();
    private Dictionary<Vector3Int, bool[,,]> noiseGridsByChunk = new Dictionary<Vector3Int, bool[,,]>();
    private HashSet<Vector3Int> processedChunks = new HashSet<Vector3Int>();
    
    private int nextRoomId = 0;
    private int nextCorridorId = 0;
    private Vector3Int currentChunkSize;
    
    // Same classes as before but modified for global scope
    private class CuboidRoom
    {
        public int id;
        public Vector3Int minBounds; // WORLD coordinates
        public Vector3Int maxBounds; // WORLD coordinates
        public Vector3Int center;
        public Vector3Int size;
        
        public CuboidRoom(int id, Vector3Int min, Vector3Int max)
        {
            this.id = id;
            minBounds = min;
            maxBounds = max;
            size = max - min + Vector3Int.one;
            center = minBounds + size / 2;
        }
        
        public bool Overlaps(CuboidRoom other)
        {
            return !(maxBounds.x < other.minBounds.x || minBounds.x > other.maxBounds.x ||
                     maxBounds.y < other.minBounds.y || minBounds.y > other.maxBounds.y ||
                     maxBounds.z < other.minBounds.z || minBounds.z > other.maxBounds.z);
        }
        
        public void CarveIntoGrid(bool[,,] grid, Vector3Int chunkOffset, Vector3Int chunkSize)
        {
            Vector3Int localMin = minBounds - chunkOffset;
            Vector3Int localMax = maxBounds - chunkOffset;
            
            localMin = Vector3Int.Max(localMin, Vector3Int.zero);
            localMax = Vector3Int.Min(localMax, chunkSize - Vector3Int.one);
            
            for (int x = localMin.x; x <= localMax.x; x++)
            {
                for (int y = localMin.y; y <= localMax.y; y++)
                {
                    for (int z = localMin.z; z <= localMax.z; z++)
                    {
                        if (x >= 0 && x < grid.GetLength(0) &&
                            y >= 0 && y < grid.GetLength(1) &&
                            z >= 0 && z < grid.GetLength(2))
                        {
                            grid[x, y, z] = true;
                        }
                    }
                }
            }
        }
        
        public Vector3Int GetClosestSurfacePoint(Vector3Int target)
        {
            Vector3Int closest = center;
            float closestDist = Vector3Int.Distance(center, target);
            
            Vector3Int[] faceCenters = {
                new Vector3Int(minBounds.x, center.y, center.z),
                new Vector3Int(maxBounds.x, center.y, center.z),
                new Vector3Int(center.x, minBounds.y, center.z),
                new Vector3Int(center.x, maxBounds.y, center.z),
                new Vector3Int(center.x, center.y, minBounds.z),
                new Vector3Int(center.x, center.y, maxBounds.z)
            };
            
            foreach (var faceCenter in faceCenters)
            {
                float dist = Vector3Int.Distance(faceCenter, target);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = faceCenter;
                }
            }
            
            return closest;
        }
        
        public List<Vector3Int> GetOccupiedChunks(Vector3Int chunkSize)
        {
            List<Vector3Int> chunks = new List<Vector3Int>();
            
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
            
            return chunks;
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
    
    private class Corridor
    {
        public int id;
        public int roomAId;
        public int roomBId;
        public Vector3Int start;
        public Vector3Int end;
        public int width;
        public int height;
        public List<Vector3Int> pathCells = new List<Vector3Int>();
        
        public void CarveIntoGrid(bool[,,] grid, Vector3Int chunkOffset, Vector3Int chunkSize)
        {
            foreach (var worldCell in pathCells)
            {
                Vector3Int localCell = worldCell - chunkOffset;
                
                if (localCell.x < 0 || localCell.x >= chunkSize.x ||
                    localCell.y < 0 || localCell.y >= chunkSize.y ||
                    localCell.z < 0 || localCell.z >= chunkSize.z)
                    continue;
                    
                int cellIndex = pathCells.IndexOf(worldCell);
                bool isHorizontal = false;
                
                if (cellIndex > 0)
                {
                    isHorizontal = Mathf.Abs(pathCells[cellIndex].x - pathCells[cellIndex-1].x) > 0;
                }
                else if (cellIndex < pathCells.Count - 1)
                {
                    isHorizontal = Mathf.Abs(pathCells[cellIndex+1].x - pathCells[cellIndex].x) > 0;
                }
                
                int halfWidth = width / 2;
                int startY = localCell.y - height / 2;
                
                if (isHorizontal)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -halfWidth; dz <= halfWidth; dz++)
                        {
                            for (int dy = 0; dy < height; dy++)
                            {
                                Vector3Int corridorCell = new Vector3Int(
                                    localCell.x + dx,
                                    startY + dy,
                                    localCell.z + dz
                                );
                                
                                if (IsInGrid(corridorCell, grid, chunkSize))
                                {
                                    grid[corridorCell.x, corridorCell.y, corridorCell.z] = true;
                                }
                            }
                        }
                    }
                }
                else
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -halfWidth; dx <= halfWidth; dx++)
                        {
                            for (int dy = 0; dy < height; dy++)
                            {
                                Vector3Int corridorCell = new Vector3Int(
                                    localCell.x + dx,
                                    startY + dy,
                                    localCell.z + dz
                                );
                                
                                if (IsInGrid(corridorCell, grid, chunkSize))
                                {
                                    grid[corridorCell.x, corridorCell.y, corridorCell.z] = true;
                                }
                            }
                        }
                    }
                }
            }
        }
        
        private bool IsInGrid(Vector3Int pos, bool[,,] grid, Vector3Int chunkSize)
        {
            return pos.x >= 0 && pos.x < chunkSize.x &&
                   pos.y >= 0 && pos.y < chunkSize.y &&
                   pos.z >= 0 && pos.z < chunkSize.z;
        }
        
        public List<Vector3Int> GetAffectedChunks(Vector3Int chunkSize)
        {
            List<Vector3Int> chunks = new List<Vector3Int>();
            
            foreach (var cell in pathCells)
            {
                Vector3Int chunk = WorldToChunkCoord(cell, chunkSize);
                if (!chunks.Contains(chunk))
                    chunks.Add(chunk);
            }
            
            return chunks;
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
        noiseGridsByChunk.Clear();
        processedChunks.Clear();
        nextRoomId = 0;
        nextCorridorId = 0;
    }
    
    public void GenerateForChunk(Vector3Int chunkCoord, Vector3Int chunkSize, ref bool[,,] finalGrid)
    {
        currentChunkSize = chunkSize;
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
        
        // Step 1: Generate noise for this chunk
        bool[,,] noiseGrid = GenerateNoiseForChunk(chunkCoord, chunkSize);
        noiseGridsByChunk[chunkCoord] = noiseGrid;
        
        // Step 2: Find rooms in this chunk (only if not already processed)
        if (!processedChunks.Contains(chunkCoord))
        {
            List<CuboidRoom> newRooms = FindCubicRoomsInChunk(chunkCoord, chunkSize, noiseGrid);
            foreach (var room in newRooms)
            {
                RegisterRoom(room, chunkSize);
            }
            
            // Step 3: Connect rooms (including cross-chunk connections)
            ConnectAllRoomsInArea(chunkCoord, chunkSize);
            
            processedChunks.Add(chunkCoord);
        }
        
        // Step 4: Get ALL rooms that affect this chunk (from anywhere)
        List<CuboidRoom> allRelevantRooms = GetRoomsForChunk(chunkCoord, chunkSize);
        
        // Step 5: Clear final grid and carve rooms
        ClearGrid(ref finalGrid, chunkSize);
        foreach (var room in allRelevantRooms)
        {
            room.CarveIntoGrid(finalGrid, worldOffset, chunkSize);
        }
        
        // Step 6: Get and carve corridors that affect this chunk
        List<Corridor> relevantCorridors = GetCorridorsForChunk(chunkCoord, chunkSize);
        foreach (var corridor in relevantCorridors)
        {
            corridor.CarveIntoGrid(finalGrid, worldOffset, chunkSize);
        }
        
        // Step 7: Create vertical connections
        CreateVerticalConnections(chunkCoord, chunkSize, allRelevantRooms, worldOffset, ref finalGrid);
    }
    
    private bool[,,] GenerateNoiseForChunk(Vector3Int chunkCoord, Vector3Int chunkSize)
    {
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
        bool[,,] noiseGrid = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
        
        for (int x = 0; x < chunkSize.x; x += scanStep)
        {
            for (int y = 0; y < chunkSize.y; y += scanStep)
            {
                for (int z = 0; z < chunkSize.z; z += scanStep)
                {
                    Vector3Int worldPos = new Vector3Int(x, y, z) + worldOffset;
                    
                    float noiseValue = Mathf.PerlinNoise(
                        worldPos.x * noiseScale + 1000,
                        worldPos.y * noiseScale + worldPos.z * noiseScale + 2000
                    );
                    
                    float verticalBias = 1f - Mathf.Abs(y - chunkSize.y * 0.3f) / (chunkSize.y * 0.3f);
                    noiseValue *= (0.7f + 0.3f * verticalBias);
                    
                    bool isSolid = noiseValue > fillThreshold;
                    
                    for (int dx = 0; dx < scanStep && x + dx < chunkSize.x; dx++)
                    {
                        for (int dy = 0; dy < scanStep && y + dy < chunkSize.y; dy++)
                        {
                            for (int dz = 0; dz < scanStep && z + dz < chunkSize.z; dz++)
                            {
                                noiseGrid[x + dx, y + dy, z + dz] = isSolid;
                            }
                        }
                    }
                }
            }
        }
        
        SmoothNoiseFast(ref noiseGrid, chunkSize);
        
        return noiseGrid;
    }
    
    private void SmoothNoiseFast(ref bool[,,] noiseGrid, Vector3Int chunkSize)
    {
        for (int i = 0; i < smoothingIterations; i++)
        {
            bool[,,] smoothed = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
            
            for (int x = 0; x < chunkSize.x; x++)
            {
                for (int y = 0; y < chunkSize.y; y++)
                {
                    for (int z = 0; z < chunkSize.z; z++)
                    {
                        int solidNeighbors = CountNeighborsFast(x, y, z, noiseGrid, chunkSize);
                        smoothed[x, y, z] = solidNeighbors >= 14;
                    }
                }
            }
            
            noiseGrid = smoothed;
        }
    }
    
    private int CountNeighborsFast(int x, int y, int z, bool[,,] grid, Vector3Int chunkSize)
    {
        int count = 0;
        int xStart = Mathf.Max(0, x - 1);
        int xEnd = Mathf.Min(chunkSize.x - 1, x + 1);
        int yStart = Mathf.Max(0, y - 1);
        int yEnd = Mathf.Min(chunkSize.y - 1, y + 1);
        int zStart = Mathf.Max(0, z - 1);
        int zEnd = Mathf.Min(chunkSize.z - 1, z + 1);
        
        for (int nx = xStart; nx <= xEnd; nx++)
        {
            for (int ny = yStart; ny <= yEnd; ny++)
            {
                for (int nz = zStart; nz <= zEnd; nz++)
                {
                    if (grid[nx, ny, nz]) count++;
                }
            }
        }
        
        return count;
    }
    
    private List<CuboidRoom> FindCubicRoomsInChunk(Vector3Int chunkCoord, Vector3Int chunkSize, bool[,,] noiseGrid)
    {
        List<CuboidRoom> rooms = new List<CuboidRoom>();
        bool[,,] occupied = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
        
        for (int x = 0; x < chunkSize.x - minRoomSize; x += scanStep)
        {
            for (int y = 0; y < chunkSize.y - minRoomSize; y += scanStep)
            {
                for (int z = 0; z < chunkSize.z - minRoomSize; z += scanStep)
                {
                    if (occupied[x, y, z] || !noiseGrid[x, y, z]) continue;
                    
                    if (QuickSolidCheck(x, y, z, minRoomSize, noiseGrid, chunkSize))
                    {
                        CuboidRoom room = FindBestCuboidAt(x, y, z, occupied, noiseGrid, chunkSize, worldOffset);
                        if (room != null && room.size.x >= minRoomSize && room.size.y >= minRoomSize && room.size.z >= minRoomSize)
                        {
                            // Check if room overlaps with any existing room
                            bool overlaps = false;
                            foreach (var existingRoom in allRooms.Values)
                            {
                                if (room.Overlaps(existingRoom))
                                {
                                    overlaps = true;
                                    break;
                                }
                            }
                            
                            if (!overlaps)
                            {
                                room.id = nextRoomId++;
                                rooms.Add(room);
                                MarkRoomOccupied(room, occupied, chunkSize, worldOffset);
                            }
                        }
                    }
                }
            }
        }
        
        return rooms;
    }
    
    private bool QuickSolidCheck(int x, int y, int z, int checkSize, bool[,,] noiseGrid, Vector3Int chunkSize)
    {
        int sampleCount = 5;
        for (int i = 0; i < sampleCount; i++)
        {
            int sx = x + Random.Range(0, checkSize);
            int sy = y + Random.Range(0, checkSize);
            int sz = z + Random.Range(0, checkSize);
            
            if (sx >= chunkSize.x || sy >= chunkSize.y || sz >= chunkSize.z || 
                !noiseGrid[sx, sy, sz])
            {
                return false;
            }
        }
        return true;
    }
    
    private CuboidRoom FindBestCuboidAt(int localX, int localY, int localZ, bool[,,] occupied, 
                                       bool[,,] noiseGrid, Vector3Int chunkSize, Vector3Int worldOffset)
    {
        int maxX = FindMaxDimension(localX, localY, localZ, Vector3Int.right, maxRoomSize, noiseGrid, chunkSize);
        int maxY = FindMaxDimension(localX, localY, localZ, Vector3Int.up, maxRoomSize, noiseGrid, chunkSize);
        int maxZ = FindMaxDimension(localX, localY, localZ, Vector3Int.forward, maxRoomSize, noiseGrid, chunkSize);
        
        if (maxX < minRoomSize || maxY < minRoomSize || maxZ < minRoomSize)
            return null;
        
        CuboidRoom bestRoom = null;
        int bestVolume = 0;
        
        int attempts = 20;
        for (int i = 0; i < attempts; i++)
        {
            int sizeX = Random.Range(minRoomSize, Mathf.Min(maxX, maxRoomSize) + 1);
            int sizeY = Random.Range(minRoomSize, Mathf.Min(maxY, maxRoomSize) + 1);
            int sizeZ = Random.Range(minRoomSize, Mathf.Min(maxZ, maxRoomSize) + 1);
            
            if (CheckCuboidFit(localX, localY, localZ, sizeX, sizeY, sizeZ, occupied, noiseGrid, chunkSize))
            {
                int volume = sizeX * sizeY * sizeZ;
                if (volume > bestVolume)
                {
                    bestVolume = volume;
                    Vector3Int worldMin = new Vector3Int(localX, localY, localZ) + worldOffset;
                    Vector3Int worldMax = worldMin + new Vector3Int(sizeX - 1, sizeY - 1, sizeZ - 1);
                    bestRoom = new CuboidRoom(-1, worldMin, worldMax); // ID will be set later
                }
            }
        }
        
        return bestRoom;
    }
    
    private int FindMaxDimension(int x, int y, int z, Vector3Int direction, int maxLength, 
                                bool[,,] noiseGrid, Vector3Int chunkSize)
    {
        int length = 0;
        Vector3Int current = new Vector3Int(x, y, z);
        
        while (current.x >= 0 && current.x < chunkSize.x &&
               current.y >= 0 && current.y < chunkSize.y &&
               current.z >= 0 && current.z < chunkSize.z &&
               noiseGrid[current.x, current.y, current.z] &&
               length < maxLength)
        {
            length++;
            current += direction;
        }
        
        return length;
    }
    
    private bool CheckCuboidFit(int x, int y, int z, int sizeX, int sizeY, int sizeZ, 
                               bool[,,] occupied, bool[,,] noiseGrid, Vector3Int chunkSize)
    {
        if (x + sizeX > chunkSize.x || y + sizeY > chunkSize.y || z + sizeZ > chunkSize.z)
            return false;
        
        int samples = Mathf.Min(sizeX * sizeY * sizeZ / 10, 50);
        for (int i = 0; i < samples; i++)
        {
            int sx = x + Random.Range(0, sizeX);
            int sy = y + Random.Range(0, sizeY);
            int sz = z + Random.Range(0, sizeZ);
            
            if (!noiseGrid[sx, sy, sz] || occupied[sx, sy, sz])
                return false;
        }
        
        return true;
    }
    
    private void MarkRoomOccupied(CuboidRoom room, bool[,,] occupied, Vector3Int chunkSize, Vector3Int worldOffset)
    {
        Vector3Int localMin = room.minBounds - worldOffset;
        Vector3Int localMax = room.maxBounds - worldOffset;
        
        int padding = 1;
        for (int x = localMin.x - padding; x <= localMax.x + padding; x++)
        {
            for (int y = localMin.y - padding; y <= localMax.y + padding; y++)
            {
                for (int z = localMin.z - padding; z <= localMax.z + padding; z++)
                {
                    if (x >= 0 && x < chunkSize.x && y >= 0 && y < chunkSize.y && z >= 0 && z < chunkSize.z)
                    {
                        occupied[x, y, z] = true;
                    }
                }
            }
        }
    }
    
    private void RegisterRoom(CuboidRoom room, Vector3Int chunkSize)
    {
        allRooms[room.id] = room;
        
        List<Vector3Int> occupiedChunks = room.GetOccupiedChunks(chunkSize);
        foreach (var chunkCoord in occupiedChunks)
        {
            if (!chunkToRooms.ContainsKey(chunkCoord))
                chunkToRooms[chunkCoord] = new HashSet<int>();
            chunkToRooms[chunkCoord].Add(room.id);
        }
        
        Debug.Log($"Registered room {room.id} at {room.minBounds} to {room.maxBounds} in {occupiedChunks.Count} chunks");
    }
    
    private void ConnectAllRoomsInArea(Vector3Int centerChunk, Vector3Int chunkSize)
    {
        // Get all rooms in a certain radius around this chunk
        List<CuboidRoom> roomsInArea = new List<CuboidRoom>();
        
        for (int dx = -maxChunkRangeForConnections; dx <= maxChunkRangeForConnections; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -maxChunkRangeForConnections; dz <= maxChunkRangeForConnections; dz++)
                {
                    Vector3Int checkChunk = centerChunk + new Vector3Int(dx, dy, dz);
                    if (chunkToRooms.ContainsKey(checkChunk))
                    {
                        foreach (int roomId in chunkToRooms[checkChunk])
                        {
                            CuboidRoom room = allRooms[roomId];
                            if (!roomsInArea.Contains(room))
                                roomsInArea.Add(room);
                        }
                    }
                }
            }
        }
        
        if (roomsInArea.Count < 2) return;
        
        // Minimum Spanning Tree
        List<CuboidRoom> connected = new List<CuboidRoom>();
        List<CuboidRoom> unconnected = new List<CuboidRoom>(roomsInArea);
        
        connected.Add(unconnected[0]);
        unconnected.RemoveAt(0);
        
        while (unconnected.Count > 0)
        {
            CuboidRoom closestRoom = null;
            CuboidRoom closestConnected = null;
            float closestDistance = float.MaxValue;
            
            foreach (CuboidRoom connectedRoom in connected)
            {
                foreach (CuboidRoom unconnectedRoom in unconnected)
                {
                    float distance = Vector3Int.Distance(connectedRoom.center, unconnectedRoom.center);
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        closestRoom = unconnectedRoom;
                        closestConnected = connectedRoom;
                    }
                }
            }
            
            if (closestRoom != null && closestConnected != null)
            {
                if (!AreRoomsConnected(closestConnected, closestRoom))
                {
                    Corridor corridor = CreateCorridorBetween(closestConnected, closestRoom, chunkSize);
                    if (corridor != null)
                    {
                        RegisterCorridor(corridor, chunkSize);
                    }
                }
                
                connected.Add(closestRoom);
                unconnected.Remove(closestRoom);
            }
        }
        
        // Add extra random connections
        AddExtraCorridors(roomsInArea, chunkSize);
    }
    
    private Corridor CreateCorridorBetween(CuboidRoom roomA, CuboidRoom roomB, Vector3Int chunkSize)
    {
        Corridor corridor = new Corridor();
        corridor.id = nextCorridorId++;
        corridor.roomAId = roomA.id;
        corridor.roomBId = roomB.id;
        
        Vector3Int startPoint = roomA.GetClosestSurfacePoint(roomB.center);
        Vector3Int endPoint = roomB.GetClosestSurfacePoint(roomA.center);
        
        corridor.start = startPoint;
        corridor.end = endPoint;
        
        List<Vector3Int> path = new List<Vector3Int>();
        
        // L-shaped path
        Vector3Int mid1 = new Vector3Int(endPoint.x, startPoint.y, startPoint.z);
        GenerateLinePath(startPoint, mid1, path);
        
        if (mid1.y != endPoint.y)
        {
            Vector3Int mid2 = new Vector3Int(endPoint.x, endPoint.y, startPoint.z);
            GenerateLinePath(mid1, mid2, path);
            mid1 = mid2;
        }
        
        GenerateLinePath(mid1, endPoint, path);
        
        corridor.pathCells = path;
        
        corridor.width = variableCorridorSize ? 
            Random.Range(minCorridorWidth, maxCorridorWidth + 1) : minCorridorWidth;
        corridor.height = variableCorridorSize ? 
            Random.Range(minCorridorHeight, maxCorridorHeight + 1) : minCorridorHeight;
        
        Debug.Log($"Created corridor {corridor.id} from room {roomA.id} to {roomB.id}, length: {path.Count} cells");
        
        return corridor;
    }
    
    private void GenerateLinePath(Vector3Int start, Vector3Int end, List<Vector3Int> path)
    {
        Vector3Int current = start;
        
        while (current != end)
        {
            path.Add(current);
            
            if (current.x != end.x)
                current.x += current.x < end.x ? 1 : -1;
            else if (current.y != end.y)
                current.y += current.y < end.y ? 1 : -1;
            else if (current.z != end.z)
                current.z += current.z < end.z ? 1 : -1;
        }
        
        path.Add(end);
    }
    
    private void RegisterCorridor(Corridor corridor, Vector3Int chunkSize)
    {
        allCorridors[corridor.id] = corridor;
        
        List<Vector3Int> affectedChunks = corridor.GetAffectedChunks(chunkSize);
        foreach (var chunkCoord in affectedChunks)
        {
            if (!chunkToCorridors.ContainsKey(chunkCoord))
                chunkToCorridors[chunkCoord] = new HashSet<int>();
            chunkToCorridors[chunkCoord].Add(corridor.id);
        }
    }
    
    private bool AreRoomsConnected(CuboidRoom roomA, CuboidRoom roomB)
    {
        foreach (var corridor in allCorridors.Values)
        {
            if ((corridor.roomAId == roomA.id && corridor.roomBId == roomB.id) ||
                (corridor.roomAId == roomB.id && corridor.roomBId == roomA.id))
            {
                return true;
            }
        }
        return false;
    }
    
    private void AddExtraCorridors(List<CuboidRoom> rooms, Vector3Int chunkSize)
    {
        if (rooms.Count < 3) return;
        
        int extraCount = Mathf.Max(1, rooms.Count / 4);
        
        for (int i = 0; i < extraCount; i++)
        {
            CuboidRoom roomA = rooms[Random.Range(0, rooms.Count)];
            CuboidRoom roomB = rooms[Random.Range(0, rooms.Count)];
            
            if (roomA != roomB && !AreRoomsConnected(roomA, roomB))
            {
                Corridor corridor = CreateCorridorBetween(roomA, roomB, chunkSize);
                if (corridor != null)
                {
                    RegisterCorridor(corridor, chunkSize);
                }
            }
        }
    }
    
    private List<CuboidRoom> GetRoomsForChunk(Vector3Int chunkCoord, Vector3Int chunkSize)
    {
        List<CuboidRoom> rooms = new List<CuboidRoom>();
        
        if (chunkToRooms.ContainsKey(chunkCoord))
        {
            foreach (int roomId in chunkToRooms[chunkCoord])
            {
                if (allRooms.ContainsKey(roomId))
                    rooms.Add(allRooms[roomId]);
            }
        }
        
        return rooms;
    }
    
    private List<Corridor> GetCorridorsForChunk(Vector3Int chunkCoord, Vector3Int chunkSize)
    {
        List<Corridor> corridors = new List<Corridor>();
        
        if (chunkToCorridors.ContainsKey(chunkCoord))
        {
            foreach (int corridorId in chunkToCorridors[chunkCoord])
            {
                if (allCorridors.ContainsKey(corridorId))
                    corridors.Add(allCorridors[corridorId]);
            }
        }
        
        return corridors;
    }
    
    private void ClearGrid(ref bool[,,] grid, Vector3Int chunkSize)
    {
        grid = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
    }
    
    private void CreateVerticalConnections(Vector3Int chunkCoord, Vector3Int chunkSize, 
                                          List<CuboidRoom> rooms, Vector3Int worldOffset, ref bool[,,] finalGrid)
    {
        if (rooms.Count == 0) return;
        
        foreach (var room in rooms)
        {
            if (Random.value < verticalConnectionChance)
            {
                Vector3Int staircaseBase = room.minBounds + new Vector3Int(2, 0, 2);
                Vector3Int localPos = staircaseBase - worldOffset;
                
                if (localPos.x >= 0 && localPos.x < chunkSize.x &&
                    localPos.z >= 0 && localPos.z < chunkSize.z)
                {
                    CreateStaircase(localPos, chunkSize, ref finalGrid);
                }
            }
        }
    }
    
    private void CreateStaircase(Vector3Int localPos, Vector3Int chunkSize, ref bool[,,] finalGrid)
    {
        int stairHeight = Random.Range(minStairHeight, maxStairHeight + 1);
        int stairWidth = 3;
        int stairDepth = 3;
        
        for (int step = 0; step < stairHeight; step++)
        {
            for (int x = 0; x < stairWidth; x++)
            {
                for (int z = 0; z < stairDepth; z++)
                {
                    int voxelX = localPos.x + x;
                    int voxelY = localPos.y + step;
                    int voxelZ = localPos.z + z;
                    
                    if (voxelX >= 0 && voxelX < chunkSize.x &&
                        voxelY >= 0 && voxelY < chunkSize.y &&
                        voxelZ >= 0 && voxelZ < chunkSize.z)
                    {
                        finalGrid[voxelX, voxelY, voxelZ] = true;
                    }
                }
            }
        }
        
        int landingHeight = stairHeight;
        for (int x = 0; x < stairWidth; x++)
        {
            for (int z = 0; z < stairDepth; z++)
            {
                for (int y = landingHeight; y < landingHeight + 2; y++)
                {
                    int voxelX = localPos.x + x;
                    int voxelY = localPos.y + y;
                    int voxelZ = localPos.z + z;
                    
                    if (voxelX >= 0 && voxelX < chunkSize.x &&
                        voxelY >= 0 && voxelY < chunkSize.y &&
                        voxelZ >= 0 && voxelZ < chunkSize.z)
                    {
                        finalGrid[voxelX, voxelY, voxelZ] = true;
                    }
                }
            }
        }
    }
    
    public void ClearChunkData(Vector3Int chunkCoord)
    {
        if (noiseGridsByChunk.ContainsKey(chunkCoord))
            noiseGridsByChunk.Remove(chunkCoord);
            
        // Note: We don't clear rooms/corridors as they are global
        // The manager will handle unloading meshes
    }
    
    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;
        
        // Draw all rooms
        Gizmos.color = new Color(0, 1, 1, 0.3f);
        foreach (var room in allRooms.Values)
        {
            Vector3 center = new Vector3(
                room.minBounds.x + room.size.x / 2f,
                room.minBounds.y + room.size.y / 2f,
                room.minBounds.z + room.size.z / 2f
            );
            Gizmos.DrawWireCube(center, room.size);
        }
        
        // Draw corridors
        Gizmos.color = new Color(1, 0.5f, 0, 0.5f);
        foreach (var corridor in allCorridors.Values)
        {
            if (corridor.pathCells.Count > 0)
            {
                for (int i = 0; i < corridor.pathCells.Count - 1; i++)
                {
                    Vector3 start = corridor.pathCells[i];
                    Vector3 end = corridor.pathCells[i + 1];
                    Gizmos.DrawLine(start, end);
                }
            }
        }
    }
}