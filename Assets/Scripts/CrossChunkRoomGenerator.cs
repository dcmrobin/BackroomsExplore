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
    private Dictionary<Vector3Int, bool[,,]> voxelGridsByChunk = new Dictionary<Vector3Int, bool[,,]>();
    private HashSet<Vector3Int> processedChunks = new HashSet<Vector3Int>();
    
    private int nextRoomId = 0;
    private int nextCorridorId = 0;
    private Vector3Int currentChunkSize;
    
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
        voxelGridsByChunk.Clear();
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
            CarveRoomIntoGrid(room, worldOffset, chunkSize, ref finalGrid);
        }
        
        // Step 6: Get and carve corridors that affect this chunk
        List<Corridor> relevantCorridors = GetCorridorsForChunk(chunkCoord, chunkSize);
        foreach (var corridor in relevantCorridors)
        {
            CarveCorridorIntoGrid(corridor, worldOffset, chunkSize, ref finalGrid);
        }
        
        // Step 7: Create vertical connections
        CreateVerticalConnections(chunkCoord, chunkSize, allRelevantRooms, worldOffset, ref finalGrid);
        
        // Step 8: CRITICAL - Check neighboring chunks to remove boundary walls
        RemoveBoundaryWalls(chunkCoord, chunkSize, ref finalGrid);
        
        // Store the final voxel grid for boundary checking
        voxelGridsByChunk[chunkCoord] = (bool[,,])finalGrid.Clone();
    }
    
    // NEW: This is the key fix - remove walls at chunk boundaries
    private void RemoveBoundaryWalls(Vector3Int chunkCoord, Vector3Int chunkSize, ref bool[,,] finalGrid)
    {
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
        
        // Check all 6 directions
        Vector3Int[] neighborDirections = {
            Vector3Int.right, Vector3Int.left,
            Vector3Int.up, Vector3Int.down,
            Vector3Int.forward, Vector3Int.back
        };
        
        foreach (var direction in neighborDirections)
        {
            Vector3Int neighborCoord = chunkCoord + direction;
            
            // If we have the neighbor's voxel data, check for openings
            if (voxelGridsByChunk.TryGetValue(neighborCoord, out bool[,,] neighborGrid))
            {
                Vector3Int neighborOffset = Vector3Int.Scale(neighborCoord, chunkSize);
                
                // Determine which face we're checking
                if (direction == Vector3Int.right) // +X face
                {
                    int boundaryX = chunkSize.x - 1;
                    for (int y = 0; y < chunkSize.y; y++)
                    {
                        for (int z = 0; z < chunkSize.z; z++)
                        {
                            // If this voxel is solid and touches the boundary
                            if (finalGrid[boundaryX, y, z])
                            {
                                // Check the corresponding voxel in the neighbor
                                if (neighborGrid[0, y, z])
                                {
                                    // Both are solid - check if we should create an opening
                                    // (We'll remove this boundary voxel if it's part of a corridor or room that continues)
                                    bool shouldBeOpen = CheckIfShouldBeOpen(new Vector3Int(boundaryX, y, z), worldOffset, 
                                                                           new Vector3Int(0, y, z), neighborOffset);
                                    if (shouldBeOpen)
                                    {
                                        finalGrid[boundaryX, y, z] = false;
                                    }
                                }
                            }
                        }
                    }
                }
                else if (direction == Vector3Int.left) // -X face
                {
                    int boundaryX = 0;
                    for (int y = 0; y < chunkSize.y; y++)
                    {
                        for (int z = 0; z < chunkSize.z; z++)
                        {
                            if (finalGrid[boundaryX, y, z])
                            {
                                if (neighborGrid[chunkSize.x - 1, y, z])
                                {
                                    bool shouldBeOpen = CheckIfShouldBeOpen(new Vector3Int(boundaryX, y, z), worldOffset,
                                                                           new Vector3Int(chunkSize.x - 1, y, z), neighborOffset);
                                    if (shouldBeOpen)
                                    {
                                        finalGrid[boundaryX, y, z] = false;
                                    }
                                }
                            }
                        }
                    }
                }
                else if (direction == Vector3Int.forward) // +Z face
                {
                    int boundaryZ = chunkSize.z - 1;
                    for (int x = 0; x < chunkSize.x; x++)
                    {
                        for (int y = 0; y < chunkSize.y; y++)
                        {
                            if (finalGrid[x, y, boundaryZ])
                            {
                                if (neighborGrid[x, y, 0])
                                {
                                    bool shouldBeOpen = CheckIfShouldBeOpen(new Vector3Int(x, y, boundaryZ), worldOffset,
                                                                           new Vector3Int(x, y, 0), neighborOffset);
                                    if (shouldBeOpen)
                                    {
                                        finalGrid[x, y, boundaryZ] = false;
                                    }
                                }
                            }
                        }
                    }
                }
                else if (direction == Vector3Int.back) // -Z face
                {
                    int boundaryZ = 0;
                    for (int x = 0; x < chunkSize.x; x++)
                    {
                        for (int y = 0; y < chunkSize.y; y++)
                        {
                            if (finalGrid[x, y, boundaryZ])
                            {
                                if (neighborGrid[x, y, chunkSize.z - 1])
                                {
                                    bool shouldBeOpen = CheckIfShouldBeOpen(new Vector3Int(x, y, boundaryZ), worldOffset,
                                                                           new Vector3Int(x, y, chunkSize.z - 1), neighborOffset);
                                    if (shouldBeOpen)
                                    {
                                        finalGrid[x, y, boundaryZ] = false;
                                    }
                                }
                            }
                        }
                    }
                }
                // Similar for Y directions if needed
            }
        }
    }
    
    private bool CheckIfShouldBeOpen(Vector3Int localPos, Vector3Int worldOffset, 
                                    Vector3Int neighborLocalPos, Vector3Int neighborWorldOffset)
    {
        // Convert to world positions
        Vector3Int worldPos = localPos + worldOffset;
        Vector3Int neighborWorldPos = neighborLocalPos + neighborWorldOffset;
        
        // Check if these positions are part of any corridor
        foreach (var corridor in allCorridors.Values)
        {
            // Check if either position is on the corridor path
            bool thisPosInCorridor = corridor.pathCells.Contains(worldPos);
            bool neighborPosInCorridor = corridor.pathCells.Contains(neighborWorldPos);
            
            // If both positions are in the same corridor, they should be connected
            if (thisPosInCorridor && neighborPosInCorridor)
            {
                return true;
            }
        }
        
        // Check if these positions are part of rooms that should connect
        // (You could add room connection logic here if needed)
        
        return false;
    }
    
    private void CarveRoomIntoGrid(CuboidRoom room, Vector3Int worldOffset, Vector3Int chunkSize, ref bool[,,] grid)
    {
        Vector3Int localMin = room.minBounds - worldOffset;
        Vector3Int localMax = room.maxBounds - worldOffset;
        
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
    
    private void CarveCorridorIntoGrid(Corridor corridor, Vector3Int worldOffset, Vector3Int chunkSize, ref bool[,,] grid)
    {
        foreach (var worldCell in corridor.pathCells)
        {
            Vector3Int localCell = worldCell - worldOffset;
            
            if (localCell.x < 0 || localCell.x >= chunkSize.x ||
                localCell.y < 0 || localCell.y >= chunkSize.y ||
                localCell.z < 0 || localCell.z >= chunkSize.z)
                continue;
                
            int cellIndex = corridor.pathCells.IndexOf(worldCell);
            bool isHorizontal = false;
            
            if (cellIndex > 0)
            {
                isHorizontal = Mathf.Abs(corridor.pathCells[cellIndex].x - corridor.pathCells[cellIndex-1].x) > 0;
            }
            else if (cellIndex < corridor.pathCells.Count - 1)
            {
                isHorizontal = Mathf.Abs(corridor.pathCells[cellIndex+1].x - corridor.pathCells[cellIndex].x) > 0;
            }
            
            int halfWidth = corridor.width / 2;
            int startY = localCell.y - corridor.height / 2;
            
            if (isHorizontal)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -halfWidth; dz <= halfWidth; dz++)
                    {
                        for (int dy = 0; dy < corridor.height; dy++)
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
                        for (int dy = 0; dy < corridor.height; dy++)
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
                    bestRoom = new CuboidRoom(-1, worldMin, worldMax);
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
    }
    
    private void ConnectAllRoomsInArea(Vector3Int centerChunk, Vector3Int chunkSize)
    {
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
        
        AddExtraCorridors(roomsInArea, chunkSize);
    }
    
    private Corridor CreateCorridorBetween(CuboidRoom roomA, CuboidRoom roomB, Vector3Int chunkSize)
    {
        Corridor corridor = new Corridor();
        corridor.id = nextCorridorId++;
        corridor.roomAId = roomA.id;
        corridor.roomBId = roomB.id;
        
        Vector3Int startPoint = GetClosestSurfacePoint(roomA, roomB.center);
        Vector3Int endPoint = GetClosestSurfacePoint(roomB, roomA.center);
        
        corridor.start = startPoint;
        corridor.end = endPoint;
        
        List<Vector3Int> path = new List<Vector3Int>();
        
        // Create an L-shaped path with better boundary handling
        CreateLShapedPath(startPoint, endPoint, path, chunkSize);
        
        corridor.pathCells = path;
        
        corridor.width = variableCorridorSize ? 
            Random.Range(minCorridorWidth, maxCorridorWidth + 1) : minCorridorWidth;
        corridor.height = variableCorridorSize ? 
            Random.Range(minCorridorHeight, maxCorridorHeight + 1) : minCorridorHeight;
        
        return corridor;
    }
    
    private Vector3Int GetClosestSurfacePoint(CuboidRoom room, Vector3Int target)
    {
        Vector3Int closest = room.center;
        float closestDist = Vector3Int.Distance(room.center, target);
        
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
            float dist = Vector3Int.Distance(faceCenter, target);
            if (dist < closestDist)
            {
                closestDist = dist;
                closest = faceCenter;
            }
        }
        
        return closest;
    }
    
    private void CreateLShapedPath(Vector3Int start, Vector3Int end, List<Vector3Int> path, Vector3Int chunkSize)
    {
        // First move in X direction
        Vector3Int current = start;
        int xDir = Mathf.Clamp(end.x - start.x, -1, 1);
        
        while (current.x != end.x)
        {
            path.Add(current);
            current.x += xDir;
        }
        
        // Then move in Z direction
        int zDir = Mathf.Clamp(end.z - current.z, -1, 1);
        
        while (current.z != end.z)
        {
            path.Add(current);
            current.z += zDir;
        }
        
        // Finally adjust Y if needed
        if (current.y != end.y)
        {
            int yDir = Mathf.Clamp(end.y - current.y, -1, 1);
            
            // Create staircase-like path for vertical movement
            while (current.y != end.y)
            {
                path.Add(current);
                current.y += yDir;
                // Move forward one step for each vertical step
                current.z += zDir;
                path.Add(current);
            }
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
        if (voxelGridsByChunk.ContainsKey(chunkCoord))
            voxelGridsByChunk.Remove(chunkCoord);
    }
    
    // NEW: Method to regenerate boundary walls when a neighbor is loaded
    public void UpdateChunkBoundaries(Vector3Int chunkCoord)
    {
        if (!voxelGridsByChunk.ContainsKey(chunkCoord)) return;
        
        // Get all neighboring chunks
        Vector3Int[] neighborDirections = {
            Vector3Int.right, Vector3Int.left,
            Vector3Int.forward, Vector3Int.back
        };
        
        foreach (var direction in neighborDirections)
        {
            Vector3Int neighborCoord = chunkCoord + direction;
            
            // If both chunks are loaded, we might need to update them
            if (voxelGridsByChunk.ContainsKey(neighborCoord))
            {
                // In a more advanced system, you could regenerate both chunks
                // For now, we'll just flag that boundaries might need updating
                Debug.Log($"Adjacent chunks {chunkCoord} and {neighborCoord} are both loaded - boundaries should align");
            }
        }
    }
}