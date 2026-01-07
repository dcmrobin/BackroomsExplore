using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class RoomAndCorridorGenerator : MonoBehaviour
{
    [Header("Noise Settings")]
    [SerializeField] private float noiseScale = 0.08f;
    [Range(0, 1)] [SerializeField] private float fillThreshold = 0.45f;
    [SerializeField] private int smoothingIterations = 2;
    
    [Header("Room Settings")]
    [SerializeField] private int minRoomSize = 4;
    [SerializeField] private int maxRoomSize = 12;
    [SerializeField] private float verticalBiasStrength = 0.3f;
    [SerializeField] private int maxCrossChunkRoomSpan = 2;
    
    [Header("Corridor Settings")]
    [SerializeField] private int minCorridorWidth = 2;
    [SerializeField] private int maxCorridorWidth = 4;
    [SerializeField] private int minCorridorHeight = 3;
    [SerializeField] private int maxCorridorHeight = 5;
    [SerializeField] private bool variableCorridorSize = true;
    [SerializeField] private float crossChunkCorridorChance = 0.3f;
    
    [Header("Chunk Settings")]
    [SerializeField] private int scanStep = 2;
    
    private Dictionary<Vector3Int, List<DungeonRoom>> roomsByChunk = new Dictionary<Vector3Int, List<DungeonRoom>>();
    private List<DungeonCorridor> allCorridors = new List<DungeonCorridor>();
    
    [System.Serializable]
    public class DungeonRoom
    {
        public Vector3Int minBounds; // WORLD coordinates
        public Vector3Int maxBounds; // WORLD coordinates
        public Vector3Int center;
        public Vector3Int size;
        public List<Vector3Int> occupiedChunks = new List<Vector3Int>();
        
        public Bounds WorldBounds => new Bounds(
            new Vector3(minBounds.x + size.x / 2f, minBounds.y + size.y / 2f, minBounds.z + size.z / 2f),
            new Vector3(size.x, size.y, size.z)
        );
        
        public DungeonRoom(Vector3Int min, Vector3Int max)
        {
            minBounds = min;
            maxBounds = max;
            size = max - min + Vector3Int.one;
            center = minBounds + size / 2;
        }
        
        public bool Overlaps(DungeonRoom other)
        {
            return !(maxBounds.x < other.minBounds.x || minBounds.x > other.maxBounds.x ||
                     maxBounds.y < other.minBounds.y || minBounds.y > other.maxBounds.y ||
                     maxBounds.z < other.minBounds.z || minBounds.z > other.maxBounds.z);
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
    }
    
    [System.Serializable]
    public class DungeonCorridor
    {
        public DungeonRoom roomA;
        public DungeonRoom roomB;
        public Vector3Int start; // WORLD coordinates
        public Vector3Int end;   // WORLD coordinates
        public int width;
        public int height;
        public List<Vector3Int> pathCells = new List<Vector3Int>();
        public List<Vector3Int> affectedChunks = new List<Vector3Int>();
    }
    
    public void GenerateForChunk(Vector3Int chunkCoord, Vector3Int chunkSize, ref bool[,,] voxelGrid, 
                                 Dictionary<Vector3Int, bool[,,]> neighborNoiseGrids = null)
    {
        // Initialize voxel grid as empty
        InitializeVoxelGrid(chunkSize, ref voxelGrid);
        
        // Step 1: Generate noise for this chunk (with neighbor awareness for continuity)
        bool[,,] noiseGrid = GenerateNoiseForChunk(chunkCoord, chunkSize, neighborNoiseGrids);
        
        // Step 2: Find rooms in the noise (can span multiple chunks)
        List<DungeonRoom> newRooms = FindCubicRooms(chunkCoord, chunkSize, noiseGrid);
        
        // Step 3: Register rooms and determine which chunks they occupy
        foreach (var room in newRooms)
        {
            RegisterRoom(room, chunkSize);
        }
        
        // Step 4: Get all rooms that affect this chunk
        List<DungeonRoom> roomsInChunk = GetRoomsAffectingChunk(chunkCoord);
        
        // Step 5: Carve rooms into voxel grid (as SOLID cubes with inverted normals)
        CarveRoomsIntoGrid(chunkCoord, chunkSize, roomsInChunk, ref voxelGrid);
        
        // Step 6: Connect rooms (including cross-chunk connections)
        ConnectRooms(chunkCoord, chunkSize, roomsInChunk, ref voxelGrid);
    }
    
    private void InitializeVoxelGrid(Vector3Int chunkSize, ref bool[,,] voxelGrid)
    {
        voxelGrid = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
        // Start with all false (empty) - rooms will be carved as SOLID
    }
    
    private bool[,,] GenerateNoiseForChunk(Vector3Int chunkCoord, Vector3Int chunkSize, 
                                          Dictionary<Vector3Int, bool[,,]> neighborNoiseGrids)
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
                    
                    // For boundaries, check neighbor noise for continuity
                    bool useNeighborNoise = false;
                    bool neighborSolid = false;
                    
                    // Check if we're near a chunk boundary
                    if (neighborNoiseGrids != null)
                    {
                        // Left boundary
                        if (x == 0 && neighborNoiseGrids.ContainsKey(new Vector3Int(-1, 0, 0)))
                        {
                            var neighborGrid = neighborNoiseGrids[new Vector3Int(-1, 0, 0)];
                            if (neighborGrid != null && neighborGrid.GetLength(0) > 0)
                            {
                                useNeighborNoise = true;
                                neighborSolid = neighborGrid[chunkSize.x - 1, y, z];
                            }
                        }
                        // Similar for other boundaries...
                    }
                    
                    float noiseValue;
                    if (useNeighborNoise)
                    {
                        // Use neighbor's noise value for continuity
                        noiseValue = neighborSolid ? 1.0f : 0.0f;
                    }
                    else
                    {
                        // Generate fresh noise
                        noiseValue = Mathf.PerlinNoise(
                            worldPos.x * noiseScale + 1000,
                            worldPos.y * noiseScale + worldPos.z * noiseScale + 2000
                        );
                        
                        // Add vertical bias
                        float verticalBias = 1f - Mathf.Abs(y - chunkSize.y * 0.3f) / (chunkSize.y * 0.3f);
                        noiseValue *= (0.7f + verticalBiasStrength * verticalBias);
                    }
                    
                    bool isSolid = noiseValue > fillThreshold;
                    
                    // Fill the scan step area
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
        
        // Smooth the noise
        SmoothNoise(chunkSize, ref noiseGrid);
        
        return noiseGrid;
    }
    
    private void SmoothNoise(Vector3Int chunkSize, ref bool[,,] noiseGrid)
    {
        if (noiseGrid == null) return;
        
        for (int i = 0; i < smoothingIterations; i++)
        {
            bool[,,] smoothed = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
            
            for (int x = 0; x < chunkSize.x; x++)
            {
                for (int y = 0; y < chunkSize.y; y++)
                {
                    for (int z = 0; z < chunkSize.z; z++)
                    {
                        int solidNeighbors = CountNeighbors(x, y, z, noiseGrid, chunkSize);
                        smoothed[x, y, z] = solidNeighbors >= 14;
                    }
                }
            }
            
            noiseGrid = smoothed;
        }
    }
    
    private int CountNeighbors(int x, int y, int z, bool[,,] grid, Vector3Int size)
    {
        if (grid == null) return 0;
        
        int count = 0;
        int xStart = Mathf.Max(0, x - 1);
        int xEnd = Mathf.Min(size.x - 1, x + 1);
        int yStart = Mathf.Max(0, y - 1);
        int yEnd = Mathf.Min(size.y - 1, y + 1);
        int zStart = Mathf.Max(0, z - 1);
        int zEnd = Mathf.Min(size.z - 1, z + 1);
        
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
    
    private List<DungeonRoom> FindCubicRooms(Vector3Int chunkCoord, Vector3Int chunkSize, bool[,,] noiseGrid)
    {
        List<DungeonRoom> foundRooms = new List<DungeonRoom>();
        if (noiseGrid == null) return foundRooms;
        
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
        bool[,,] occupied = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
        
        for (int x = 0; x < chunkSize.x - minRoomSize; x += scanStep)
        {
            for (int y = 0; y < chunkSize.y - minRoomSize; y += scanStep)
            {
                for (int z = 0; z < chunkSize.z - minRoomSize; z += scanStep)
                {
                    if (occupied[x, y, z] || !noiseGrid[x, y, z]) continue;
                    
                    if (QuickSolidCheck(x, y, z, minRoomSize, noiseGrid, chunkSize))
                    {
                        DungeonRoom room = FindBestCuboidAt(x, y, z, occupied, noiseGrid, chunkSize, worldOffset);
                        if (room != null && room.size.x >= minRoomSize && room.size.y >= minRoomSize && room.size.z >= minRoomSize)
                        {
                            foundRooms.Add(room);
                            MarkRoomOccupied(room, occupied, chunkSize, worldOffset);
                        }
                    }
                }
            }
        }
        
        return foundRooms;
    }
    
    private bool QuickSolidCheck(int x, int y, int z, int checkSize, bool[,,] noiseGrid, Vector3Int chunkSize)
    {
        if (noiseGrid == null) return false;
        
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
    
    private DungeonRoom FindBestCuboidAt(int localX, int localY, int localZ, bool[,,] occupied, 
                                        bool[,,] noiseGrid, Vector3Int chunkSize, Vector3Int worldOffset)
    {
        if (noiseGrid == null) return null;
        
        // Find max dimensions in each direction
        int maxX = FindMaxDimension(localX, localY, localZ, Vector3Int.right, maxRoomSize, noiseGrid, chunkSize);
        int maxY = FindMaxDimension(localX, localY, localZ, Vector3Int.up, maxRoomSize, noiseGrid, chunkSize);
        int maxZ = FindMaxDimension(localX, localY, localZ, Vector3Int.forward, maxRoomSize, noiseGrid, chunkSize);
        
        if (maxX < minRoomSize || maxY < minRoomSize || maxZ < minRoomSize)
            return null;
        
        DungeonRoom bestRoom = null;
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
                    bestRoom = new DungeonRoom(worldMin, worldMax);
                }
            }
        }
        
        return bestRoom;
    }
    
    private int FindMaxDimension(int x, int y, int z, Vector3Int direction, int maxLength, 
                                bool[,,] noiseGrid, Vector3Int chunkSize)
    {
        if (noiseGrid == null) return 0;
        
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
        if (noiseGrid == null) return false;
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
    
    private void MarkRoomOccupied(DungeonRoom room, bool[,,] occupied, Vector3Int chunkSize, Vector3Int worldOffset)
    {
        if (occupied == null) return;
        
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
    
    private void RegisterRoom(DungeonRoom room, Vector3Int chunkSize)
    {
        // Determine which chunks this room occupies
        Vector3Int minChunk = WorldToChunkCoord(room.minBounds, chunkSize);
        Vector3Int maxChunk = WorldToChunkCoord(room.maxBounds, chunkSize);
        
        for (int x = minChunk.x; x <= maxChunk.x; x++)
        {
            for (int y = minChunk.y; y <= maxChunk.y; y++)
            {
                for (int z = minChunk.z; z <= maxChunk.z; z++)
                {
                    Vector3Int chunkCoord = new Vector3Int(x, y, z);
                    room.occupiedChunks.Add(chunkCoord);
                    
                    if (!roomsByChunk.ContainsKey(chunkCoord))
                        roomsByChunk[chunkCoord] = new List<DungeonRoom>();
                    
                    if (!roomsByChunk[chunkCoord].Contains(room))
                        roomsByChunk[chunkCoord].Add(room);
                }
            }
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
    
    public List<DungeonRoom> GetRoomsAffectingChunk(Vector3Int chunkCoord)
    {
        if (roomsByChunk.ContainsKey(chunkCoord))
            return roomsByChunk[chunkCoord];
        return new List<DungeonRoom>();
    }
    
    private void CarveRoomsIntoGrid(Vector3Int chunkCoord, Vector3Int chunkSize, 
                                   List<DungeonRoom> rooms, ref bool[,,] voxelGrid)
    {
        if (voxelGrid == null || rooms == null) return;
        
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
        
        foreach (var room in rooms)
        {
            // Convert room bounds to local chunk coordinates
            Vector3Int localMin = room.minBounds - worldOffset;
            Vector3Int localMax = room.maxBounds - worldOffset;
            
            // Clamp to chunk bounds
            localMin = Vector3Int.Max(localMin, Vector3Int.zero);
            localMax = Vector3Int.Min(localMax, chunkSize - Vector3Int.one);
            
            // Make the entire room SOLID (not hollow)
            // The mesh will render with inverted normals to create the "hollow" appearance
            for (int x = localMin.x; x <= localMax.x; x++)
            {
                for (int y = localMin.y; y <= localMax.y; y++)
                {
                    for (int z = localMin.z; z <= localMax.z; z++)
                    {
                        if (x >= 0 && x < chunkSize.x &&
                            y >= 0 && y < chunkSize.y &&
                            z >= 0 && z < chunkSize.z)
                        {
                            voxelGrid[x, y, z] = true; // SOLID
                        }
                    }
                }
            }
        }
    }
    
    private void ConnectRooms(Vector3Int chunkCoord, Vector3Int chunkSize, 
                             List<DungeonRoom> rooms, ref bool[,,] voxelGrid)
    {
        if (voxelGrid == null || rooms.Count < 2) return;
        
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
        
        // Connect rooms that are in or adjacent to this chunk
        foreach (var room in rooms)
        {
            // Find nearby rooms to connect to
            foreach (var otherRoom in GetNearbyRooms(room, 2, chunkSize))
            {
                if (room == otherRoom || AreRoomsConnected(room, otherRoom)) continue;
                
                // Chance for cross-chunk connection
                if (room.occupiedChunks[0] != otherRoom.occupiedChunks[0] && 
                    Random.value > crossChunkCorridorChance)
                    continue;
                
                CreateCorridorBetween(room, otherRoom, worldOffset, chunkSize, ref voxelGrid);
            }
        }
    }
    
    private List<DungeonRoom> GetNearbyRooms(DungeonRoom room, int chunkRadius, Vector3Int chunkSize)
    {
        List<DungeonRoom> nearbyRooms = new List<DungeonRoom>();
        
        foreach (var roomChunk in room.occupiedChunks)
        {
            for (int dx = -chunkRadius; dx <= chunkRadius; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -chunkRadius; dz <= chunkRadius; dz++)
                    {
                        Vector3Int nearbyChunk = roomChunk + new Vector3Int(dx, dy, dz);
                        if (roomsByChunk.ContainsKey(nearbyChunk))
                        {
                            foreach (var r in roomsByChunk[nearbyChunk])
                            {
                                if (!nearbyRooms.Contains(r) && r != room)
                                    nearbyRooms.Add(r);
                            }
                        }
                    }
                }
            }
        }
        
        return nearbyRooms;
    }
    
    private bool AreRoomsConnected(DungeonRoom roomA, DungeonRoom roomB)
    {
        foreach (var corridor in allCorridors)
        {
            if ((corridor.roomA == roomA && corridor.roomB == roomB) ||
                (corridor.roomA == roomB && corridor.roomB == roomA))
            {
                return true;
            }
        }
        return false;
    }
    
    private void CreateCorridorBetween(DungeonRoom roomA, DungeonRoom roomB, 
                                      Vector3Int worldOffset, Vector3Int chunkSize, ref bool[,,] voxelGrid)
    {
        DungeonCorridor corridor = new DungeonCorridor();
        corridor.roomA = roomA;
        corridor.roomB = roomB;
        
        Vector3Int startPoint = roomA.GetClosestSurfacePoint(roomB.center);
        Vector3Int endPoint = roomB.GetClosestSurfacePoint(roomA.center);
        
        corridor.start = startPoint;
        corridor.end = endPoint;
        
        List<Vector3Int> path = new List<Vector3Int>();
        
        // Create L-shaped path
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
        
        int corridorWidth = variableCorridorSize ? 
            Random.Range(minCorridorWidth, maxCorridorWidth + 1) : minCorridorWidth;
        int corridorHeight = variableCorridorSize ? 
            Random.Range(minCorridorHeight, maxCorridorHeight + 1) : minCorridorHeight;
        
        corridor.width = corridorWidth;
        corridor.height = corridorHeight;
        
        // Determine which chunks this corridor affects
        corridor.affectedChunks = GetCorridorChunks(corridor, chunkSize);
        
        // Carve corridor in this chunk's portion
        CarveCorridorInChunk(corridor, worldOffset, chunkSize, ref voxelGrid);
        
        allCorridors.Add(corridor);
    }
    
    private void GenerateLinePath(Vector3Int start, Vector3Int end, List<Vector3Int> path)
    {
        Vector3Int current = start;
        
        while (current != end)
        {
            if (!path.Contains(current))
                path.Add(current);
            
            if (current.x != end.x)
                current.x += current.x < end.x ? 1 : -1;
            else if (current.y != end.y)
                current.y += current.y < end.y ? 1 : -1;
            else if (current.z != end.z)
                current.z += current.z < end.z ? 1 : -1;
        }
        
        if (!path.Contains(end))
            path.Add(end);
    }
    
    private List<Vector3Int> GetCorridorChunks(DungeonCorridor corridor, Vector3Int chunkSize)
    {
        List<Vector3Int> chunks = new List<Vector3Int>();
        
        foreach (var point in corridor.pathCells)
        {
            Vector3Int chunk = WorldToChunkCoord(point, chunkSize);
            if (!chunks.Contains(chunk))
                chunks.Add(chunk);
        }
        
        return chunks;
    }
    
    private void CarveCorridorInChunk(DungeonCorridor corridor, Vector3Int worldOffset, 
                                     Vector3Int chunkSize, ref bool[,,] voxelGrid)
    {
        if (voxelGrid == null) return;
        
        for (int i = 0; i < corridor.pathCells.Count; i++)
        {
            Vector3Int worldCell = corridor.pathCells[i];
            Vector3Int localCell = worldCell - worldOffset;
            
            // Skip if not in this chunk
            if (localCell.x < 0 || localCell.x >= chunkSize.x ||
                localCell.y < 0 || localCell.y >= chunkSize.y ||
                localCell.z < 0 || localCell.z >= chunkSize.z)
                continue;
                
            bool isHorizontal = (i > 0 && Mathf.Abs(corridor.pathCells[i].x - corridor.pathCells[i-1].x) > 0) || 
                               (i < corridor.pathCells.Count - 1 && Mathf.Abs(corridor.pathCells[i+1].x - corridor.pathCells[i].x) > 0);
            
            int halfWidth = corridor.width / 2;
            int startY = localCell.y - corridor.height / 2;
            
            // Carve corridor as SOLID (inverted normals will hollow it)
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
                            
                            if (IsInGrid(corridorCell, voxelGrid))
                            {
                                voxelGrid[corridorCell.x, corridorCell.y, corridorCell.z] = true;
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
                            
                            if (IsInGrid(corridorCell, voxelGrid))
                            {
                                voxelGrid[corridorCell.x, corridorCell.y, corridorCell.z] = true;
                            }
                        }
                    }
                }
            }
        }
    }
    
    private bool IsInGrid(Vector3Int pos, bool[,,] grid)
    {
        if (grid == null) return false;
        return pos.x >= 0 && pos.x < grid.GetLength(0) &&
               pos.y >= 0 && pos.y < grid.GetLength(1) &&
               pos.z >= 0 && pos.z < grid.GetLength(2);
    }
    
    public void ClearChunkData(Vector3Int chunkCoord)
    {
        if (roomsByChunk.ContainsKey(chunkCoord))
        {
            // Remove room references from other chunks
            foreach (var room in roomsByChunk[chunkCoord])
            {
                room.occupiedChunks.Remove(chunkCoord);
                if (room.occupiedChunks.Count == 0)
                {
                    // Remove room from all chunks
                    foreach (var chunk in roomsByChunk.Keys)
                    {
                        if (roomsByChunk[chunk].Contains(room))
                            roomsByChunk[chunk].Remove(room);
                    }
                }
            }
            roomsByChunk.Remove(chunkCoord);
        }
        
        // Remove corridors that only affected this chunk
        allCorridors.RemoveAll(c => c.affectedChunks.Count == 1 && c.affectedChunks[0] == chunkCoord);
    }
}