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
    [SerializeField] private int maxRoomSize = 20;
    [SerializeField] private bool allowEdgeRooms = true; // NEW: Allow rooms at chunk edges
    [SerializeField] private float edgeRoomChance = 0.3f; // Chance for rooms to touch edges
    
    [Header("Corridor Settings")]
    [SerializeField] private int corridorWidth = 3;
    [SerializeField] private int corridorHeight = 4;
    [SerializeField] private bool corridorsTouchEdges = true; // NEW: Corridors can go to edges
    
    [Header("Vertical Connections")]
    [SerializeField] private float verticalConnectionChance = 0.3f;
    [SerializeField] private int verticalShaftMinHeight = 3;
    [SerializeField] private int verticalShaftMaxHeight = 12;
    
    [Header("Generation Optimization")]
    [SerializeField] private int scanStep = 2;
    [SerializeField] private int maxGenerationRange = 5;
    
    // GLOBAL STORAGE - Cross-chunk aware
    private Dictionary<int, CuboidRoom> allRooms = new Dictionary<int, CuboidRoom>();
    private Dictionary<int, Connection> allConnections = new Dictionary<int, Connection>();
    private Dictionary<Vector3Int, HashSet<int>> chunkToRooms = new Dictionary<Vector3Int, HashSet<int>>();
    private Dictionary<Vector3Int, HashSet<int>> chunkToConnections = new Dictionary<Vector3Int, HashSet<int>>();
    
    // Spatial partitioning for faster room queries
    private Dictionary<Vector3Int, HashSet<int>> roomSpatialGrid = new Dictionary<Vector3Int, HashSet<int>>();
    private int spatialGridSize = 16;
    
    private int nextRoomId = 0;
    private int nextConnectionId = 0;
    private Vector3Int currentChunkSize;
    private HashSet<Vector3Int> initializedChunks = new HashSet<Vector3Int>();
    
    private class CuboidRoom
    {
        public int id;
        public Vector3Int minBounds; // WORLD coordinates
        public Vector3Int maxBounds; // WORLD coordinates
        public Vector3Int center;
        public Vector3Int size;
        public Vector3Int generationChunk;
        
        public CuboidRoom(int id, Vector3Int min, Vector3Int max, Vector3Int genChunk)
        {
            this.id = id;
            minBounds = min;
            maxBounds = max;
            size = max - min + Vector3Int.one;
            center = minBounds + size / 2;
            generationChunk = genChunk;
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
        
        private Vector3Int WorldToChunkCoord(Vector3Int worldPos, Vector3Int chunkSize)
        {
            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x / (float)chunkSize.x),
                Mathf.FloorToInt(worldPos.y / (float)chunkSize.y),
                Mathf.FloorToInt(worldPos.z / (float)chunkSize.z)
            );
        }
    }
    
    private class Connection
    {
        public int id;
        public int roomAId;
        public int roomBId;
        public bool isVertical;
        public List<Vector3Int> path = new List<Vector3Int>();
        public int width;
        public int height;
        
        public List<Vector3Int> GetOccupiedChunks(Vector3Int chunkSize)
        {
            HashSet<Vector3Int> chunks = new HashSet<Vector3Int>();
            
            foreach (var point in path)
            {
                Vector3Int chunk = WorldToChunkCoord(point, chunkSize);
                chunks.Add(chunk);
                
                // Include neighboring chunks since connections can cross boundaries
                foreach (var dir in new Vector3Int[] { Vector3Int.up, Vector3Int.down, 
                    Vector3Int.right, Vector3Int.left, Vector3Int.forward, Vector3Int.back })
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
        allConnections.Clear();
        chunkToRooms.Clear();
        chunkToConnections.Clear();
        roomSpatialGrid.Clear();
        initializedChunks.Clear();
        nextRoomId = 0;
        nextConnectionId = 0;
    }
    
    public void GenerateForChunk(Vector3Int chunkCoord, Vector3Int chunkSize, ref bool[,,] finalGrid)
    {
        currentChunkSize = chunkSize;
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
        
        // Step 1: Clear the grid
        ClearGrid(ref finalGrid, chunkSize);
        
        // Step 2: Initialize rooms for this chunk if not already done
        if (!initializedChunks.Contains(chunkCoord))
        {
            InitializeChunkRooms(chunkCoord, chunkSize);
            initializedChunks.Add(chunkCoord);
        }
        
        // Step 3: Get ALL rooms that affect this chunk
        List<CuboidRoom> relevantRooms = GetRoomsForChunk(chunkCoord, chunkSize);
        
        // Step 4: Carve rooms into grid (NOW INCLUDING EDGES)
        foreach (var room in relevantRooms)
        {
            CarveRoomIntoGridWithEdges(room, worldOffset, chunkSize, ref finalGrid);
        }
        
        // Step 5: Get and carve connections that affect this chunk
        List<Connection> relevantConnections = GetConnectionsForChunk(chunkCoord, chunkSize);
        foreach (var connection in relevantConnections)
        {
            CarveConnectionIntoGridWithEdges(connection, worldOffset, chunkSize, ref finalGrid);
        }
    }
    
    private void InitializeChunkRooms(Vector3Int chunkCoord, Vector3Int chunkSize)
    {
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
        
        // Generate noise for potential room locations - INCLUDING EDGES
        List<Vector3Int> potentialRoomCenters = FindPotentialRoomCentersIncludingEdges(chunkCoord, chunkSize);
        
        foreach (var localCenter in potentialRoomCenters)
        {
            Vector3Int worldCenter = localCenter + worldOffset;
            
            // Check if this area is already occupied by a room
            if (IsAreaOccupied(worldCenter, minRoomSize))
                continue;
            
            // Try to create a room - ALLOWING EDGE ROOMS
            CuboidRoom room = TryCreateRoomAtWithEdges(worldCenter, chunkCoord, chunkSize);
            if (room != null)
            {
                RegisterRoom(room, chunkSize);
                
                // Try to connect to nearby rooms
                ConnectRoomToNeighbors(room, chunkSize);
            }
        }
    }
    
    private List<Vector3Int> FindPotentialRoomCentersIncludingEdges(Vector3Int chunkCoord, Vector3Int chunkSize)
    {
        List<Vector3Int> centers = new List<Vector3Int>();
        Vector3Int worldOffset = Vector3Int.Scale(chunkCoord, chunkSize);
        
        // Allow centers at the very edges of chunks
        int minX = allowEdgeRooms ? 0 : minRoomSize/2;
        int maxX = allowEdgeRooms ? chunkSize.x : chunkSize.x - minRoomSize/2;
        int minY = allowEdgeRooms ? 0 : minRoomSize/2;
        int maxY = allowEdgeRooms ? chunkSize.y : chunkSize.y - minRoomSize/2;
        int minZ = allowEdgeRooms ? 0 : minRoomSize/2;
        int maxZ = allowEdgeRooms ? chunkSize.z : chunkSize.z - minRoomSize/2;
        
        int step = Mathf.Max(scanStep, minRoomSize / 2);
        
        for (int x = minX; x < maxX; x += step)
        {
            for (int y = minY; y < maxY; y += step)
            {
                for (int z = minZ; z < maxZ; z += step)
                {
                    // Increase chance for edge rooms
                    bool isAtEdge = x == 0 || x == chunkSize.x - 1 || 
                                   y == 0 || y == chunkSize.y - 1 || 
                                   z == 0 || z == chunkSize.z - 1;
                    
                    Vector3Int worldPos = new Vector3Int(x, y, z) + worldOffset;
                    float noise = GetNoiseAt(worldPos);
                    
                    // Edge rooms get a chance boost
                    float threshold = isAtEdge && Random.value < edgeRoomChance ? 
                        fillThreshold * 0.8f : fillThreshold;
                    
                    if (noise > threshold)
                    {
                        centers.Add(new Vector3Int(x, y, z));
                    }
                }
            }
        }
        
        return centers;
    }
    
    private CuboidRoom TryCreateRoomAtWithEdges(Vector3Int worldCenter, Vector3Int genChunk, Vector3Int chunkSize)
    {
        // Determine room size
        int sizeX = Random.Range(minRoomSize, maxRoomSize + 1);
        int sizeY = Random.Range(minRoomSize, Mathf.Min(maxRoomSize, chunkSize.y) + 1);
        int sizeZ = Random.Range(minRoomSize, maxRoomSize + 1);
        
        // Decide if room should be centered or can extend beyond chunk
        bool centerInChunk = Random.value > 0.5f;
        
        Vector3Int minBounds, maxBounds;
        
        if (centerInChunk)
        {
            // Traditional centered room
            minBounds = worldCenter - new Vector3Int(sizeX / 2, sizeY / 2, sizeZ / 2);
            maxBounds = minBounds + new Vector3Int(sizeX - 1, sizeY - 1, sizeZ - 1);
        }
        else
        {
            // Allow room to extend from edge
            Vector3Int chunkWorldMin = Vector3Int.Scale(genChunk, chunkSize);
            Vector3Int chunkWorldMax = chunkWorldMin + chunkSize - Vector3Int.one;
            
            // Choose an anchor point (could be at edge)
            Vector3Int anchor = worldCenter;
            
            // Sometimes anchor at chunk edge
            if (Random.value < 0.3f)
            {
                if (Random.value > 0.5f) anchor.x = chunkWorldMin.x;
                else anchor.x = chunkWorldMax.x;
            }
            if (Random.value < 0.3f)
            {
                if (Random.value > 0.5f) anchor.z = chunkWorldMin.z;
                else anchor.z = chunkWorldMax.z;
            }
            
            minBounds = anchor;
            maxBounds = anchor + new Vector3Int(sizeX - 1, sizeY - 1, sizeZ - 1);
        }
        
        // Quick overlap check
        if (CheckRoomOverlap(minBounds, maxBounds))
            return null;
        
        return new CuboidRoom(nextRoomId++, minBounds, maxBounds, genChunk);
    }
    
    private void CarveRoomIntoGridWithEdges(CuboidRoom room, Vector3Int worldOffset, Vector3Int chunkSize, ref bool[,,] grid)
    {
        Vector3Int localMin = room.minBounds - worldOffset;
        Vector3Int localMax = room.maxBounds - worldOffset;
        
        // DON'T clamp to grid bounds - carve whatever part is in this chunk
        // This allows rooms to extend beyond chunk boundaries
        for (int x = Mathf.Max(localMin.x, 0); x <= Mathf.Min(localMax.x, chunkSize.x - 1); x++)
        {
            for (int y = Mathf.Max(localMin.y, 0); y <= Mathf.Min(localMax.y, chunkSize.y - 1); y++)
            {
                for (int z = Mathf.Max(localMin.z, 0); z <= Mathf.Min(localMax.z, chunkSize.z - 1); z++)
                {
                    grid[x, y, z] = true;
                }
            }
        }
    }
    
    private void CarveConnectionIntoGridWithEdges(Connection connection, Vector3Int worldOffset, Vector3Int chunkSize, ref bool[,,] grid)
    {
        foreach (var worldPoint in connection.path)
        {
            Vector3Int localPoint = worldPoint - worldOffset;
            
            // Only carve if point is within or adjacent to this chunk
            // This allows corridors to extend to edges
            if (localPoint.x < -corridorWidth || localPoint.x >= chunkSize.x + corridorWidth ||
                localPoint.y < -corridorHeight || localPoint.y >= chunkSize.y + corridorHeight ||
                localPoint.z < -corridorWidth || localPoint.z >= chunkSize.z + corridorWidth)
                continue;
            
            // Carve corridor around the path point
            int halfWidth = connection.width / 2;
            int startY = localPoint.y - (connection.isVertical ? 0 : connection.height / 2);
            int endY = startY + (connection.isVertical ? connection.width : connection.height);
            
            for (int dx = -halfWidth; dx <= halfWidth; dx++)
            {
                for (int dz = -halfWidth; dz <= halfWidth; dz++)
                {
                    for (int dy = 0; dy < (connection.isVertical ? connection.width : connection.height); dy++)
                    {
                        int y = startY + dy;
                        
                        Vector3Int carvePos = new Vector3Int(
                            localPoint.x + dx,
                            y,
                            localPoint.z + dz
                        );
                        
                        // Convert to local chunk coordinates
                        Vector3Int localCarvePos = carvePos; // Already local
                        
                        // Only carve if within chunk bounds
                        if (localCarvePos.x >= 0 && localCarvePos.x < chunkSize.x &&
                            localCarvePos.y >= 0 && localCarvePos.y < chunkSize.y &&
                            localCarvePos.z >= 0 && localCarvePos.z < chunkSize.z)
                        {
                            grid[localCarvePos.x, localCarvePos.y, localCarvePos.z] = true;
                        }
                    }
                }
            }
        }
    }
    
    private void ConnectRoomToNeighbors(CuboidRoom newRoom, Vector3Int chunkSize)
    {
        // Find nearby rooms (including in other chunks)
        List<CuboidRoom> nearbyRooms = FindNearbyRooms(newRoom.center, maxGenerationRange * Mathf.Max(chunkSize.x, chunkSize.z));
        
        if (nearbyRooms.Count == 0) return;
        
        // Sort by distance
        nearbyRooms.Sort((a, b) => 
            Vector3Int.Distance(newRoom.center, a.center).CompareTo(
            Vector3Int.Distance(newRoom.center, b.center)));
        
        // Connect to closest room
        CuboidRoom closestRoom = nearbyRooms[0];
        
        if (!AreRoomsConnected(newRoom, closestRoom))
        {
            // Decide connection type
            bool makeVertical = Random.value < verticalConnectionChance && 
                               Mathf.Abs(newRoom.center.y - closestRoom.center.y) > minRoomSize;
            
            Connection connection = CreateConnectionBetween(newRoom, closestRoom, makeVertical, chunkSize);
            if (connection != null)
            {
                RegisterConnection(connection, chunkSize);
            }
        }
    }
    
    private Connection CreateConnectionBetween(CuboidRoom roomA, CuboidRoom roomB, bool vertical, Vector3Int chunkSize)
    {
        Connection connection = new Connection
        {
            id = nextConnectionId++,
            roomAId = roomA.id,
            roomBId = roomB.id,
            isVertical = vertical,
            width = corridorWidth,
            height = vertical ? corridorWidth : corridorHeight
        };
        
        if (vertical)
        {
            CreateVerticalConnection(roomA, roomB, connection);
        }
        else
        {
            CreateHorizontalConnection(roomA, roomB, connection, chunkSize);
        }
        
        return connection.path.Count > 0 ? connection : null;
    }
    
    private void CreateHorizontalConnection(CuboidRoom roomA, CuboidRoom roomB, Connection connection, Vector3Int chunkSize)
    {
        // Get connection points - PREFER POINTS AT CHUNK EDGES FOR CROSS-CHUNK CONNECTIONS
        Vector3Int pointA = GetConnectionPointOnRoomWithEdgePreference(roomA, roomB.center, chunkSize);
        Vector3Int pointB = GetConnectionPointOnRoomWithEdgePreference(roomB, roomA.center, chunkSize);
        
        // Ensure points are at least corridor width apart from room interiors
        pointA = AdjustPointForCorridor(pointA, roomA, connection.width);
        pointB = AdjustPointForCorridor(pointB, roomB, connection.width);
        
        // Create path that can cross chunk boundaries
        List<Vector3Int> path = new List<Vector3Int>();
        
        // Start from pointA
        Vector3Int current = pointA;
        path.Add(current);
        
        // Move in X direction first (allows crossing chunk X boundaries)
        int xDir = Mathf.Clamp(pointB.x - pointA.x, -1, 1);
        while (current.x != pointB.x)
        {
            current.x += xDir;
            path.Add(current);
        }
        
        // Then move in Z direction (allows crossing chunk Z boundaries)
        int zDir = Mathf.Clamp(pointB.z - current.z, -1, 1);
        while (current.z != pointB.z)
        {
            current.z += zDir;
            path.Add(current);
        }
        
        // Finally adjust Y if needed
        if (current.y != pointB.y)
        {
            int yDir = Mathf.Clamp(pointB.y - current.y, -1, 1);
            while (current.y != pointB.y)
            {
                current.y += yDir;
                path.Add(current);
            }
        }
        
        connection.path = path;
    }
    
    private Vector3Int GetConnectionPointOnRoomWithEdgePreference(CuboidRoom room, Vector3Int target, Vector3Int chunkSize)
    {
        // Get all potential connection points on room faces
        List<Vector3Int> facePoints = new List<Vector3Int>();
        List<float> distances = new List<float>();
        
        // Generate points along each face
        for (int x = room.minBounds.x; x <= room.maxBounds.x; x += Mathf.Max(1, room.size.x / 3))
        {
            // -Y face (floor)
            facePoints.Add(new Vector3Int(x, room.minBounds.y, room.center.z));
            distances.Add(Vector3Int.Distance(new Vector3Int(x, room.minBounds.y, room.center.z), target));
            
            // +Y face (ceiling)
            facePoints.Add(new Vector3Int(x, room.maxBounds.y, room.center.z));
            distances.Add(Vector3Int.Distance(new Vector3Int(x, room.maxBounds.y, room.center.z), target));
        }
        
        for (int z = room.minBounds.z; z <= room.maxBounds.z; z += Mathf.Max(1, room.size.z / 3))
        {
            // -X face
            facePoints.Add(new Vector3Int(room.minBounds.x, room.center.y, z));
            distances.Add(Vector3Int.Distance(new Vector3Int(room.minBounds.x, room.center.y, z), target));
            
            // +X face
            facePoints.Add(new Vector3Int(room.maxBounds.x, room.center.y, z));
            distances.Add(Vector3Int.Distance(new Vector3Int(room.maxBounds.x, room.center.y, z), target));
        }
        
        for (int y = room.minBounds.y; y <= room.maxBounds.y; y += Mathf.Max(1, room.size.y / 3))
        {
            // -Z face
            facePoints.Add(new Vector3Int(room.center.x, y, room.minBounds.z));
            distances.Add(Vector3Int.Distance(new Vector3Int(room.center.x, y, room.minBounds.z), target));
            
            // +Z face
            facePoints.Add(new Vector3Int(room.center.x, y, room.maxBounds.z));
            distances.Add(Vector3Int.Distance(new Vector3Int(room.center.x, y, room.maxBounds.z), target));
        }
        
        // Prefer points at chunk boundaries for cross-chunk connections
        float bestScore = float.MaxValue;
        Vector3Int bestPoint = room.center;
        
        for (int i = 0; i < facePoints.Count; i++)
        {
            float distanceScore = distances[i];
            
            // Bonus for points at chunk boundaries
            Vector3Int chunkCoord = WorldToChunkCoord(facePoints[i], chunkSize);
            Vector3Int localPos = facePoints[i] - Vector3Int.Scale(chunkCoord, chunkSize);
            
            bool isAtChunkEdge = localPos.x == 0 || localPos.x == chunkSize.x - 1 ||
                                localPos.z == 0 || localPos.z == chunkSize.z - 1;
            
            if (isAtChunkEdge && corridorsTouchEdges)
            {
                distanceScore *= 0.7f; // Prefer edge points
            }
            
            if (distanceScore < bestScore)
            {
                bestScore = distanceScore;
                bestPoint = facePoints[i];
            }
        }
        
        return bestPoint;
    }
    
    private Vector3Int AdjustPointForCorridor(Vector3Int point, CuboidRoom room, int corridorWidth)
    {
        // Move point slightly outside the room to create corridor entrance
        if (point.x == room.minBounds.x) point.x -= 1;
        else if (point.x == room.maxBounds.x) point.x += 1;
        else if (point.z == room.minBounds.z) point.z -= 1;
        else if (point.z == room.maxBounds.z) point.z += 1;
        else if (point.y == room.minBounds.y) point.y -= 1;
        else if (point.y == room.maxBounds.y) point.y += 1;
        
        return point;
    }
    
    private Vector3Int WorldToChunkCoord(Vector3Int worldPos, Vector3Int chunkSize)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / (float)chunkSize.x),
            Mathf.FloorToInt(worldPos.y / (float)chunkSize.y),
            Mathf.FloorToInt(worldPos.z / (float)chunkSize.z)
        );
    }
    
    // ... [Previous helper methods remain mostly the same, but ensure they allow edge generation]
    
    private float GetNoiseAt(Vector3Int worldPos)
    {
        return Mathf.PerlinNoise(
            worldPos.x * noiseScale,
            worldPos.y * noiseScale + worldPos.z * noiseScale * 0.5f
        );
    }
    
    private bool IsAreaOccupied(Vector3Int center, int checkSize)
    {
        Vector3Int checkMin = center - Vector3Int.one * (checkSize / 2);
        Vector3Int checkMax = center + Vector3Int.one * (checkSize / 2);
        
        Vector3Int gridCell = GetSpatialGridCell(center);
        
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    Vector3Int neighborCell = gridCell + new Vector3Int(dx, dy, dz);
                    if (roomSpatialGrid.TryGetValue(neighborCell, out HashSet<int> roomIds))
                    {
                        foreach (int roomId in roomIds)
                        {
                            CuboidRoom room = allRooms[roomId];
                            if (room.Overlaps(new CuboidRoom(-1, checkMin, checkMax, Vector3Int.zero)))
                                return true;
                        }
                    }
                }
            }
        }
        
        return false;
    }
    
    private Vector3Int GetSpatialGridCell(Vector3Int worldPos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / (float)spatialGridSize),
            Mathf.FloorToInt(worldPos.y / (float)spatialGridSize),
            Mathf.FloorToInt(worldPos.z / (float)spatialGridSize)
        );
    }
    
    private bool CheckRoomOverlap(Vector3Int minBounds, Vector3Int maxBounds)
    {
        CuboidRoom testRoom = new CuboidRoom(-1, minBounds, maxBounds, Vector3Int.zero);
        
        Vector3Int gridMin = GetSpatialGridCell(minBounds);
        Vector3Int gridMax = GetSpatialGridCell(maxBounds);
        
        for (int x = gridMin.x; x <= gridMax.x; x++)
        {
            for (int y = gridMin.y; y <= gridMax.y; y++)
            {
                for (int z = gridMin.z; z <= gridMax.z; z++)
                {
                    Vector3Int gridCell = new Vector3Int(x, y, z);
                    if (roomSpatialGrid.TryGetValue(gridCell, out HashSet<int> roomIds))
                    {
                        foreach (int roomId in roomIds)
                        {
                            if (testRoom.Overlaps(allRooms[roomId]))
                                return true;
                        }
                    }
                }
            }
        }
        
        return false;
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
    }
    
    private List<CuboidRoom> FindNearbyRooms(Vector3Int center, float maxDistance)
    {
        List<CuboidRoom> nearby = new List<CuboidRoom>();
        
        Vector3Int gridCell = GetSpatialGridCell(center);
        int gridRadius = Mathf.CeilToInt(maxDistance / spatialGridSize) + 1;
        
        for (int dx = -gridRadius; dx <= gridRadius; dx++)
        {
            for (int dy = -gridRadius; dy <= gridRadius; dy++)
            {
                for (int dz = -gridRadius; dz <= gridRadius; dz++)
                {
                    Vector3Int checkCell = gridCell + new Vector3Int(dx, dy, dz);
                    if (roomSpatialGrid.TryGetValue(checkCell, out HashSet<int> roomIds))
                    {
                        foreach (int roomId in roomIds)
                        {
                            CuboidRoom room = allRooms[roomId];
                            if (room.id != nextRoomId - 1 &&
                                Vector3Int.Distance(center, room.center) <= maxDistance)
                            {
                                nearby.Add(room);
                            }
                        }
                    }
                }
            }
        }
        
        return nearby;
    }
    
    private bool AreRoomsConnected(CuboidRoom roomA, CuboidRoom roomB)
    {
        foreach (var connection in allConnections.Values)
        {
            if ((connection.roomAId == roomA.id && connection.roomBId == roomB.id) ||
                (connection.roomAId == roomB.id && connection.roomBId == roomA.id))
            {
                return true;
            }
        }
        return false;
    }
    
    private void CreateVerticalConnection(CuboidRoom roomA, CuboidRoom roomB, Connection connection)
    {
        CuboidRoom lowerRoom = roomA.center.y < roomB.center.y ? roomA : roomB;
        CuboidRoom upperRoom = roomA.center.y > roomB.center.y ? roomA : roomB;
        
        Vector3Int startPoint = new Vector3Int(
            lowerRoom.center.x,
            lowerRoom.maxBounds.y,
            lowerRoom.center.z
        );
        
        Vector3Int endPoint = new Vector3Int(
            upperRoom.center.x,
            upperRoom.minBounds.y,
            upperRoom.center.z
        );
        
        int currentY = startPoint.y;
        while (currentY <= endPoint.y)
        {
            connection.path.Add(new Vector3Int(startPoint.x, currentY, startPoint.z));
            currentY++;
            
            if (connection.path.Count > verticalShaftMaxHeight * 2)
                break;
        }
    }
    
    private void RegisterConnection(Connection connection, Vector3Int chunkSize)
    {
        allConnections[connection.id] = connection;
        
        List<Vector3Int> occupiedChunks = connection.GetOccupiedChunks(chunkSize);
        foreach (var chunkCoord in occupiedChunks)
        {
            if (!chunkToConnections.ContainsKey(chunkCoord))
                chunkToConnections[chunkCoord] = new HashSet<int>();
            chunkToConnections[chunkCoord].Add(connection.id);
        }
    }
    
    private List<CuboidRoom> GetRoomsForChunk(Vector3Int chunkCoord, Vector3Int chunkSize)
    {
        List<CuboidRoom> rooms = new List<CuboidRoom>();
        
        if (chunkToRooms.TryGetValue(chunkCoord, out HashSet<int> roomIds))
        {
            foreach (int roomId in roomIds)
            {
                if (allRooms.TryGetValue(roomId, out CuboidRoom room))
                    rooms.Add(room);
            }
        }
        
        return rooms;
    }
    
    private List<Connection> GetConnectionsForChunk(Vector3Int chunkCoord, Vector3Int chunkSize)
    {
        List<Connection> connections = new List<Connection>();
        
        if (chunkToConnections.TryGetValue(chunkCoord, out HashSet<int> connectionIds))
        {
            foreach (int connectionId in connectionIds)
            {
                if (allConnections.TryGetValue(connectionId, out Connection connection))
                    connections.Add(connection);
            }
        }
        
        return connections;
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
    
    public void ClearChunkData(Vector3Int chunkCoord)
    {
        initializedChunks.Remove(chunkCoord);
    }
}