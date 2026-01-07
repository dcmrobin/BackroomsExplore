using UnityEngine;
using System.Collections.Generic;

public class ChunkRoomGenerator : MonoBehaviour
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
    
    [Header("Generation Optimization")]
    [SerializeField] private int scanStep = 2;
    
    // Cross-chunk storage
    private Dictionary<Vector3Int, List<CuboidRoom>> roomsByChunk = new Dictionary<Vector3Int, List<CuboidRoom>>();
    private Dictionary<Vector3Int, List<Corridor>> corridorsByChunk = new Dictionary<Vector3Int, List<Corridor>>();
    private Dictionary<Vector3Int, bool[,,]> noiseGridsByChunk = new Dictionary<Vector3Int, bool[,,]>();
    
    // Material IDs (same as original)
    private const byte MATERIAL_WALL = 0;
    private const byte MATERIAL_FLOOR = 1;
    private const byte MATERIAL_CEILING = 2;
    private const byte MATERIAL_LIGHT = 3;
    
    // EXACT SAME CLASSES AS ORIGINAL
    private class CuboidRoom
    {
        public Vector3Int minBounds; // WORLD coordinates
        public Vector3Int maxBounds; // WORLD coordinates
        public Vector3Int center;
        public Vector3Int size;
        
        public Bounds WorldBounds => new Bounds(
            new Vector3(minBounds.x + size.x / 2f, minBounds.y + size.y / 2f, minBounds.z + size.z / 2f),
            new Vector3(size.x, size.y, size.z)
        );
        
        public CuboidRoom(Vector3Int min, Vector3Int max)
        {
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
            // Convert world bounds to local chunk coordinates
            Vector3Int localMin = minBounds - chunkOffset;
            Vector3Int localMax = maxBounds - chunkOffset;
            
            // Clamp to chunk bounds
            localMin = Vector3Int.Max(localMin, Vector3Int.zero);
            localMax = Vector3Int.Min(localMax, chunkSize - Vector3Int.one);
            
            // Carve room (make it SOLID - exactly like original)
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
        public CuboidRoom roomA;
        public CuboidRoom roomB;
        public Vector3Int start; // WORLD coordinates
        public Vector3Int end;   // WORLD coordinates
        public int width;
        public int height;
        public List<Vector3Int> pathCells = new List<Vector3Int>();
        
        public void CarveIntoGrid(bool[,,] grid, Vector3Int chunkOffset, Vector3Int chunkSize)
        {
            // Convert path cells to local chunk coordinates
            foreach (var worldCell in pathCells)
            {
                Vector3Int localCell = worldCell - chunkOffset;
                
                // Skip if not in this chunk
                if (localCell.x < 0 || localCell.x >= chunkSize.x ||
                    localCell.y < 0 || localCell.y >= chunkSize.y ||
                    localCell.z < 0 || localCell.z >= chunkSize.z)
                    continue;
                
                // Determine if this segment is horizontal
                int cellIndex = pathCells.IndexOf(worldCell);
                bool isHorizontal = (cellIndex > 0 && Mathf.Abs(pathCells[cellIndex].x - pathCells[cellIndex-1].x) > 0) || 
                                   (cellIndex < pathCells.Count - 1 && Mathf.Abs(pathCells[cellIndex+1].x - pathCells[cellIndex].x) > 0);
                
                int halfWidth = width / 2;
                int startY = localCell.y - height / 2;
                
                // Carve corridor (make it SOLID - exactly like original)
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
    
    public void GenerateForChunk(Vector3Int chunkCoord, Vector3Int chunkSize, ref bool[,,] finalGrid)
    {
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
        
        // Step 1: Generate noise for this chunk (using world coordinates for consistency)
        bool[,,] noiseGrid = GenerateNoiseForChunk(chunkCoord, chunkSize);
        noiseGridsByChunk[chunkCoord] = noiseGrid;
        
        // Step 2: Find rooms in this chunk
        List<CuboidRoom> newRooms = FindCubicRoomsInChunk(chunkCoord, chunkSize, noiseGrid);
        
        // Step 3: Register rooms (for cross-chunk awareness)
        foreach (var room in newRooms)
        {
            RegisterRoom(room, chunkSize);
        }
        
        // Step 4: Get ALL rooms that affect this chunk (including from neighbors)
        List<CuboidRoom> allRelevantRooms = GetAllRoomsAffectingChunk(chunkCoord, chunkSize);
        
        // Step 5: Clear final grid and carve rooms
        ClearGrid(ref finalGrid, chunkSize);
        foreach (var room in allRelevantRooms)
        {
            room.CarveIntoGrid(finalGrid, worldOffset, chunkSize);
        }
        
        // Step 6: Get corridors that affect this chunk
        List<Corridor> relevantCorridors = GetCorridorsAffectingChunk(chunkCoord, chunkSize);
        
        // Step 7: Carve corridors
        foreach (var corridor in relevantCorridors)
        {
            corridor.CarveIntoGrid(finalGrid, worldOffset, chunkSize);
        }
        
        // Step 8: Connect rooms (minimum spanning tree - like original)
        ConnectRoomsWithCorridors(chunkCoord, chunkSize, allRelevantRooms);
    }
    
    private bool[,,] GenerateNoiseForChunk(Vector3Int chunkCoord, Vector3Int chunkSize)
    {
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
        bool[,,] noiseGrid = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
        
        // EXACT SAME NOISE GENERATION AS ORIGINAL
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
        
        // Smooth noise (same as original)
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
                        smoothed[x, y, z] = solidNeighbors >= 14; // Same threshold as original
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
        
        // EXACT SAME ROOM FINDING ALGORITHM AS ORIGINAL
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
                            rooms.Add(room);
                            MarkRoomOccupied(room, occupied, chunkSize, worldOffset);
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
        // Find max dimensions (same as original)
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
                    bestRoom = new CuboidRoom(worldMin, worldMax);
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
        List<Vector3Int> occupiedChunks = room.GetOccupiedChunks(chunkSize);
        
        foreach (var chunkCoord in occupiedChunks)
        {
            if (!roomsByChunk.ContainsKey(chunkCoord))
                roomsByChunk[chunkCoord] = new List<CuboidRoom>();
            
            if (!roomsByChunk[chunkCoord].Contains(room))
                roomsByChunk[chunkCoord].Add(room);
        }
    }
    
    private List<CuboidRoom> GetAllRoomsAffectingChunk(Vector3Int chunkCoord, Vector3Int chunkSize)
    {
        List<CuboidRoom> allRooms = new List<CuboidRoom>();
        
        // Get rooms from this chunk
        if (roomsByChunk.ContainsKey(chunkCoord))
        {
            allRooms.AddRange(roomsByChunk[chunkCoord]);
        }
        
        // Also get rooms from neighboring chunks that might extend into this one
        // (for rooms that span multiple chunks)
        Vector3Int[] neighborOffsets = {
            Vector3Int.left, Vector3Int.right,
            Vector3Int.down, Vector3Int.up,
            Vector3Int.back, Vector3Int.forward
        };
        
        foreach (var offset in neighborOffsets)
        {
            Vector3Int neighborCoord = chunkCoord + offset;
            if (roomsByChunk.ContainsKey(neighborCoord))
            {
                foreach (var room in roomsByChunk[neighborCoord])
                {
                    if (!allRooms.Contains(room) && room.GetOccupiedChunks(chunkSize).Contains(chunkCoord))
                    {
                        allRooms.Add(room);
                    }
                }
            }
        }
        
        return allRooms;
    }
    
    private void ClearGrid(ref bool[,,] grid, Vector3Int chunkSize)
    {
        grid = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
    }
    
    private List<Corridor> GetCorridorsAffectingChunk(Vector3Int chunkCoord, Vector3Int chunkSize)
    {
        List<Corridor> corridors = new List<Corridor>();
        
        if (corridorsByChunk.ContainsKey(chunkCoord))
        {
            corridors.AddRange(corridorsByChunk[chunkCoord]);
        }
        
        return corridors;
    }
    
    private void ConnectRoomsWithCorridors(Vector3Int chunkCoord, Vector3Int chunkSize, List<CuboidRoom> rooms)
    {
        if (rooms.Count < 2) return;
        
        // Get existing corridors for this chunk
        if (!corridorsByChunk.ContainsKey(chunkCoord))
            corridorsByChunk[chunkCoord] = new List<Corridor>();
        
        // Minimum Spanning Tree (same as original)
        List<CuboidRoom> connected = new List<CuboidRoom>();
        List<CuboidRoom> unconnected = new List<CuboidRoom>(rooms);
        
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
                Corridor corridor = CreateCorridorBetween(closestConnected, closestRoom, chunkSize);
                if (corridor != null)
                {
                    corridorsByChunk[chunkCoord].Add(corridor);
                    
                    // Register corridor in all affected chunks
                    List<Vector3Int> affectedChunks = corridor.GetAffectedChunks(chunkSize);
                    foreach (var affectedChunk in affectedChunks)
                    {
                        if (!corridorsByChunk.ContainsKey(affectedChunk))
                            corridorsByChunk[affectedChunk] = new List<Corridor>();
                        
                        if (!corridorsByChunk[affectedChunk].Contains(corridor))
                            corridorsByChunk[affectedChunk].Add(corridor);
                    }
                }
                
                connected.Add(closestRoom);
                unconnected.Remove(closestRoom);
            }
        }
        
        // Add extra corridors (same as original)
        AddExtraCorridors(chunkCoord, chunkSize, rooms);
    }
    
    private Corridor CreateCorridorBetween(CuboidRoom roomA, CuboidRoom roomB, Vector3Int chunkSize)
    {
        Corridor corridor = new Corridor();
        corridor.roomA = roomA;
        corridor.roomB = roomB;
        
        Vector3Int startPoint = roomA.GetClosestSurfacePoint(roomB.center);
        Vector3Int endPoint = roomB.GetClosestSurfacePoint(roomA.center);
        
        corridor.start = startPoint;
        corridor.end = endPoint;
        
        List<Vector3Int> path = new List<Vector3Int>();
        
        // L-shaped path (same as original)
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
    
    private void AddExtraCorridors(Vector3Int chunkCoord, Vector3Int chunkSize, List<CuboidRoom> rooms)
    {
        if (rooms.Count < 3) return;
        
        int extraCount = Mathf.Max(1, rooms.Count / 4);
        
        for (int i = 0; i < extraCount; i++)
        {
            CuboidRoom roomA = rooms[Random.Range(0, rooms.Count)];
            CuboidRoom roomB = rooms[Random.Range(0, rooms.Count)];
            
            if (roomA != roomB && !AreRoomsConnected(roomA, roomB, chunkCoord))
            {
                Corridor corridor = CreateCorridorBetween(roomA, roomB, chunkSize);
                if (corridor != null)
                {
                    corridorsByChunk[chunkCoord].Add(corridor);
                    
                    // Register in all affected chunks
                    List<Vector3Int> affectedChunks = corridor.GetAffectedChunks(chunkSize);
                    foreach (var affectedChunk in affectedChunks)
                    {
                        if (!corridorsByChunk.ContainsKey(affectedChunk))
                            corridorsByChunk[affectedChunk] = new List<Corridor>();
                        
                        if (!corridorsByChunk[affectedChunk].Contains(corridor))
                            corridorsByChunk[affectedChunk].Add(corridor);
                    }
                }
            }
        }
    }
    
    private bool AreRoomsConnected(CuboidRoom roomA, CuboidRoom roomB, Vector3Int chunkCoord)
    {
        if (!corridorsByChunk.ContainsKey(chunkCoord)) return false;
        
        foreach (Corridor corridor in corridorsByChunk[chunkCoord])
        {
            if ((corridor.roomA == roomA && corridor.roomB == roomB) ||
                (corridor.roomA == roomB && corridor.roomB == roomA))
            {
                return true;
            }
        }
        return false;
    }
    
    public void ClearChunkData(Vector3Int chunkCoord)
    {
        if (roomsByChunk.ContainsKey(chunkCoord))
            roomsByChunk.Remove(chunkCoord);
        
        if (corridorsByChunk.ContainsKey(chunkCoord))
            corridorsByChunk.Remove(chunkCoord);
        
        if (noiseGridsByChunk.ContainsKey(chunkCoord))
            noiseGridsByChunk.Remove(chunkCoord);
    }
}