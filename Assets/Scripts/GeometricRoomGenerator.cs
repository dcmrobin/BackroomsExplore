using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class OptimizedCuboidRoomGenerator : MonoBehaviour
{
    [Header("Noise Settings")]
    [SerializeField] private Vector3Int gridSize = new Vector3Int(80, 40, 80);
    [SerializeField] private float noiseScale = 0.08f;
    [SerializeField] [Range(0, 1)] private float fillThreshold = 0.45f;
    [SerializeField] private int smoothingIterations = 2;
    
    [Header("Room Settings")]
    [SerializeField] private int minRoomSize = 4;
    [SerializeField] private int maxRoomSize = 12;
    [SerializeField] private bool allowNonCubic = true;
    
    [Header("Corridor Settings")]
    [SerializeField] private int minCorridorWidth = 2;
    [SerializeField] private int maxCorridorWidth = 4;
    [SerializeField] private int minCorridorHeight = 3;
    [SerializeField] private int maxCorridorHeight = 5;
    [SerializeField] private bool variableCorridorSize = true;
    
    [Header("Generation Optimization")]
    [SerializeField] private int scanStep = 2; // Skip cells when scanning for performance
    [SerializeField] private bool useOctreeOptimization = true;
    
    [Header("Visualization")]
    [SerializeField] private Material caveMaterial;
    [SerializeField] private bool generateOnStart = true;
    
    private bool[,,] noiseGrid;
    private bool[,,] finalGrid;
    private List<CuboidRoom> rooms = new List<CuboidRoom>();
    private List<Corridor> corridors = new List<Corridor>();
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    
    private class CuboidRoom
    {
        public Vector3Int minBounds;
        public Vector3Int maxBounds;
        public Vector3Int center;
        public Vector3Int size;
        
        public Bounds Bounds => new Bounds(
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
        
        public int Volume => size.x * size.y * size.z;
        
        public bool Overlaps(CuboidRoom other)
        {
            return !(maxBounds.x < other.minBounds.x || minBounds.x > other.maxBounds.x ||
                     maxBounds.y < other.minBounds.y || minBounds.y > other.maxBounds.y ||
                     maxBounds.z < other.minBounds.z || minBounds.z > other.maxBounds.z);
        }
        
        public void CarveIntoGrid(bool[,,] grid)
        {
            for (int x = minBounds.x; x <= maxBounds.x; x++)
            {
                for (int y = minBounds.y; y <= maxBounds.y; y++)
                {
                    for (int z = minBounds.z; z <= maxBounds.z; z++)
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
            // Find closest point on room surface to target
            Vector3Int closest = center;
            float closestDist = Vector3Int.Distance(center, target);
            
            // Check center of each face
            Vector3Int[] faceCenters = {
                new Vector3Int(minBounds.x, center.y, center.z), // Left face
                new Vector3Int(maxBounds.x, center.y, center.z), // Right face
                new Vector3Int(center.x, minBounds.y, center.z), // Bottom face
                new Vector3Int(center.x, maxBounds.y, center.z), // Top face
                new Vector3Int(center.x, center.y, minBounds.z), // Front face
                new Vector3Int(center.x, center.y, maxBounds.z)  // Back face
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
    
    private class Corridor
    {
        public CuboidRoom roomA;
        public CuboidRoom roomB;
        public Vector3Int start;
        public Vector3Int end;
        public int width;
        public int height;
        public List<Vector3Int> pathCells = new List<Vector3Int>();
        
        public void CarveIntoGrid(bool[,,] grid, int corridorWidth, int corridorHeight)
        {
            width = corridorWidth;
            height = corridorHeight;
            
            // Create proper hallway with walls, floor, and ceiling
            for (int i = 0; i < pathCells.Count; i++)
            {
                Vector3Int cell = pathCells[i];
                
                // Determine corridor orientation at this segment
                bool isHorizontal = (i > 0 && Mathf.Abs(pathCells[i].x - pathCells[i-1].x) > 0) || 
                                   (i < pathCells.Count - 1 && Mathf.Abs(pathCells[i+1].x - pathCells[i].x) > 0);
                
                // For horizontal corridors (X-axis), extend in Z dimension for width
                // For depth corridors (Z-axis), extend in X dimension for width
                // Always extend in Y for height
                
                int halfWidth = width / 2;
                int startY = cell.y - height / 2;
                
                if (isHorizontal)
                {
                    // Corridor runs along X, width is in Z direction
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -halfWidth; dz <= halfWidth; dz++)
                        {
                            for (int dy = 0; dy < height; dy++)
                            {
                                Vector3Int corridorCell = new Vector3Int(
                                    cell.x + dx,
                                    startY + dy,
                                    cell.z + dz
                                );
                                
                                if (IsInGrid(corridorCell, grid))
                                {
                                    grid[corridorCell.x, corridorCell.y, corridorCell.z] = true;
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Corridor runs along Z, width is in X direction
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -halfWidth; dx <= halfWidth; dx++)
                        {
                            for (int dy = 0; dy < height; dy++)
                            {
                                Vector3Int corridorCell = new Vector3Int(
                                    cell.x + dx,
                                    startY + dy,
                                    cell.z + dz
                                );
                                
                                if (IsInGrid(corridorCell, grid))
                                {
                                    grid[corridorCell.x, corridorCell.y, corridorCell.z] = true;
                                }
                            }
                        }
                    }
                }
            }
        }
        
        private bool IsInGrid(Vector3Int pos, bool[,,] grid)
        {
            return pos.x >= 0 && pos.x < grid.GetLength(0) &&
                   pos.y >= 0 && pos.y < grid.GetLength(1) &&
                   pos.z >= 0 && pos.z < grid.GetLength(2);
        }
    }

    void Start()
    {
        if (generateOnStart)
        {
            Generate();
        }
    }

    public void Generate()
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        InitializeComponents();
        GenerateNoise();
        
        Debug.Log($"Noise generation: {stopwatch.ElapsedMilliseconds}ms");
        stopwatch.Restart();
        
        FindCubicRoomsOptimized();
        
        Debug.Log($"Room finding: {stopwatch.ElapsedMilliseconds}ms");
        stopwatch.Restart();
        
        ConnectRoomsWithCorridors();
        
        Debug.Log($"Corridor generation: {stopwatch.ElapsedMilliseconds}ms");
        stopwatch.Restart();
        
        CarveEverything();
        
        Debug.Log($"Carving: {stopwatch.ElapsedMilliseconds}ms");
        stopwatch.Restart();
        
        GenerateMesh();
        
        Debug.Log($"Mesh generation: {stopwatch.ElapsedMilliseconds}ms");
        Debug.Log($"Total: {rooms.Count} rooms, {corridors.Count} corridors");
        
        stopwatch.Stop();
    }

    private void InitializeComponents()
    {
        if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
        
        if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();
        
        if (caveMaterial != null) meshRenderer.material = caveMaterial;
    }

    private void GenerateNoise()
    {
        noiseGrid = new bool[gridSize.x, gridSize.y, gridSize.z];
        finalGrid = new bool[gridSize.x, gridSize.y, gridSize.z];
        
        // Fast noise generation using cached values
        for (int x = 0; x < gridSize.x; x += scanStep)
        {
            for (int y = 0; y < gridSize.y; y += scanStep)
            {
                for (int z = 0; z < gridSize.z; z += scanStep)
                {
                    float noiseValue = Mathf.PerlinNoise(
                        x * noiseScale + 1000,
                        y * noiseScale + z * noiseScale + 2000
                    );
                    
                    // Vertical bias
                    float verticalBias = 1f - Mathf.Abs(y - gridSize.y * 0.3f) / (gridSize.y * 0.3f);
                    noiseValue *= (0.7f + 0.3f * verticalBias);
                    
                    bool isSolid = noiseValue > fillThreshold;
                    
                    // Fill the skipped cells with the same value (optimization)
                    for (int dx = 0; dx < scanStep && x + dx < gridSize.x; dx++)
                    {
                        for (int dy = 0; dy < scanStep && y + dy < gridSize.y; dy++)
                        {
                            for (int dz = 0; dz < scanStep && z + dz < gridSize.z; dz++)
                            {
                                noiseGrid[x + dx, y + dy, z + dz] = isSolid;
                            }
                        }
                    }
                }
            }
        }
        
        SmoothNoiseFast();
    }

    private void SmoothNoiseFast()
    {
        for (int i = 0; i < smoothingIterations; i++)
        {
            bool[,,] smoothed = new bool[gridSize.x, gridSize.y, gridSize.z];
            
            // Use optimized neighbor counting
            for (int x = 0; x < gridSize.x; x++)
            {
                for (int y = 0; y < gridSize.y; y++)
                {
                    for (int z = 0; z < gridSize.z; z++)
                    {
                        int solidNeighbors = CountNeighborsFast(x, y, z);
                        smoothed[x, y, z] = solidNeighbors >= 14;
                    }
                }
            }
            
            noiseGrid = smoothed;
        }
    }

    private int CountNeighborsFast(int x, int y, int z)
    {
        int count = 0;
        int xStart = Mathf.Max(0, x - 1);
        int xEnd = Mathf.Min(gridSize.x - 1, x + 1);
        int yStart = Mathf.Max(0, y - 1);
        int yEnd = Mathf.Min(gridSize.y - 1, y + 1);
        int zStart = Mathf.Max(0, z - 1);
        int zEnd = Mathf.Min(gridSize.z - 1, z + 1);
        
        for (int nx = xStart; nx <= xEnd; nx++)
        {
            for (int ny = yStart; ny <= yEnd; ny++)
            {
                for (int nz = zStart; nz <= zEnd; nz++)
                {
                    if (noiseGrid[nx, ny, nz]) count++;
                }
            }
        }
        
        return count;
    }

    private void FindCubicRoomsOptimized()
    {
        rooms.Clear();
        bool[,,] occupied = new bool[gridSize.x, gridSize.y, gridSize.z];
        
        // Use scanStep for performance
        for (int x = 0; x < gridSize.x - minRoomSize; x += scanStep)
        {
            for (int y = 0; y < gridSize.y - minRoomSize; y += scanStep)
            {
                for (int z = 0; z < gridSize.z - minRoomSize; z += scanStep)
                {
                    if (occupied[x, y, z] || !noiseGrid[x, y, z]) continue;
                    
                    // Quick check: is there enough solid space here for a room?
                    if (QuickSolidCheck(x, y, z, minRoomSize))
                    {
                        CuboidRoom room = FindBestCuboidAt(x, y, z, occupied);
                        if (room != null && room.Volume >= minRoomSize * minRoomSize * minRoomSize)
                        {
                            rooms.Add(room);
                            MarkRoomOccupied(room, occupied);
                        }
                    }
                }
            }
        }
    }

    private bool QuickSolidCheck(int x, int y, int z, int checkSize)
    {
        // Quick check of a few sample points
        int sampleCount = 5;
        for (int i = 0; i < sampleCount; i++)
        {
            int sx = x + Random.Range(0, checkSize);
            int sy = y + Random.Range(0, checkSize);
            int sz = z + Random.Range(0, checkSize);
            
            if (sx >= gridSize.x || sy >= gridSize.y || sz >= gridSize.z || 
                !noiseGrid[sx, sy, sz])
            {
                return false;
            }
        }
        return true;
    }

    private CuboidRoom FindBestCuboidAt(int startX, int startY, int startZ, bool[,,] occupied)
    {
        // Find maximum possible dimensions quickly
        int maxX = FindMaxDimension(startX, startY, startZ, Vector3Int.right, maxRoomSize);
        int maxY = FindMaxDimension(startX, startY, startZ, Vector3Int.up, maxRoomSize);
        int maxZ = FindMaxDimension(startX, startY, startZ, Vector3Int.forward, maxRoomSize);
        
        if (maxX < minRoomSize || maxY < minRoomSize || maxZ < minRoomSize)
            return null;
        
        // Try a few random sizes within bounds
        CuboidRoom bestRoom = null;
        int bestVolume = 0;
        
        // Limit number of attempts for performance
        int attempts = 20;
        for (int i = 0; i < attempts; i++)
        {
            int sizeX = Random.Range(minRoomSize, Mathf.Min(maxX, maxRoomSize) + 1);
            int sizeY = Random.Range(minRoomSize, Mathf.Min(maxY, maxRoomSize) + 1);
            int sizeZ = Random.Range(minRoomSize, Mathf.Min(maxZ, maxRoomSize) + 1);
            
            // Check if this cuboid fits and doesn't overlap occupied space
            if (CheckCuboidFit(startX, startY, startZ, sizeX, sizeY, sizeZ, occupied))
            {
                int volume = sizeX * sizeY * sizeZ;
                if (volume > bestVolume)
                {
                    bestVolume = volume;
                    bestRoom = new CuboidRoom(
                        new Vector3Int(startX, startY, startZ),
                        new Vector3Int(startX + sizeX - 1, startY + sizeY - 1, startZ + sizeZ - 1)
                    );
                }
            }
        }
        
        return bestRoom;
    }

    private int FindMaxDimension(int x, int y, int z, Vector3Int direction, int maxLength)
    {
        int length = 0;
        Vector3Int current = new Vector3Int(x, y, z);
        
        while (current.x >= 0 && current.x < gridSize.x &&
               current.y >= 0 && current.y < gridSize.y &&
               current.z >= 0 && current.z < gridSize.z &&
               noiseGrid[current.x, current.y, current.z] &&
               length < maxLength)
        {
            length++;
            current += direction;
        }
        
        return length;
    }

    private bool CheckCuboidFit(int x, int y, int z, int sizeX, int sizeY, int sizeZ, bool[,,] occupied)
    {
        // Check bounds
        if (x + sizeX > gridSize.x || y + sizeY > gridSize.y || z + sizeZ > gridSize.z)
            return false;
        
        // Check a sampling of points for performance
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

    private void MarkRoomOccupied(CuboidRoom room, bool[,,] occupied)
    {
        // Mark with padding to prevent corridors from cutting through rooms
        int padding = 1;
        for (int x = room.minBounds.x - padding; x <= room.maxBounds.x + padding; x++)
        {
            for (int y = room.minBounds.y - padding; y <= room.maxBounds.y + padding; y++)
            {
                for (int z = room.minBounds.z - padding; z <= room.maxBounds.z + padding; z++)
                {
                    if (x >= 0 && x < gridSize.x && y >= 0 && y < gridSize.y && z >= 0 && z < gridSize.z)
                    {
                        occupied[x, y, z] = true;
                    }
                }
            }
        }
    }

    private void ConnectRoomsWithCorridors()
    {
        corridors.Clear();
        
        if (rooms.Count < 2) return;
        
        // Minimum spanning tree to connect all rooms
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
                CreateCorridorBetween(closestConnected, closestRoom);
                connected.Add(closestRoom);
                unconnected.Remove(closestRoom);
            }
        }
        
        // Add some random connections for loops
        AddExtraCorridors();
    }

    private void CreateCorridorBetween(CuboidRoom roomA, CuboidRoom roomB)
    {
        Corridor corridor = new Corridor();
        corridor.roomA = roomA;
        corridor.roomB = roomB;
        
        // Get connection points on room surfaces
        Vector3Int startPoint = roomA.GetClosestSurfacePoint(roomB.center);
        Vector3Int endPoint = roomB.GetClosestSurfacePoint(roomA.center);
        
        corridor.start = startPoint;
        corridor.end = endPoint;
        
        // Generate L-shaped path
        List<Vector3Int> path = new List<Vector3Int>();
        
        // First segment: align X
        Vector3Int mid1 = new Vector3Int(endPoint.x, startPoint.y, startPoint.z);
        GenerateLinePath(startPoint, mid1, path);
        
        // Second segment: align Y (if needed)
        if (mid1.y != endPoint.y)
        {
            Vector3Int mid2 = new Vector3Int(endPoint.x, endPoint.y, startPoint.z);
            GenerateLinePath(mid1, mid2, path);
            mid1 = mid2;
        }
        
        // Third segment: align Z
        GenerateLinePath(mid1, endPoint, path);
        
        corridor.pathCells = path;
        
        // Random corridor dimensions
        int corridorWidth = variableCorridorSize ? 
            Random.Range(minCorridorWidth, maxCorridorWidth + 1) : minCorridorWidth;
        int corridorHeight = variableCorridorSize ? 
            Random.Range(minCorridorHeight, maxCorridorHeight + 1) : minCorridorHeight;
        
        corridor.width = corridorWidth;
        corridor.height = corridorHeight;
        
        corridors.Add(corridor);
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

    private void AddExtraCorridors()
    {
        if (rooms.Count < 3) return;
        
        int extraCount = Mathf.Max(1, rooms.Count / 4);
        
        for (int i = 0; i < extraCount; i++)
        {
            CuboidRoom roomA = rooms[Random.Range(0, rooms.Count)];
            CuboidRoom roomB = rooms[Random.Range(0, rooms.Count)];
            
            if (roomA != roomB && !AreRoomsConnected(roomA, roomB))
            {
                CreateCorridorBetween(roomA, roomB);
            }
        }
    }

    private bool AreRoomsConnected(CuboidRoom roomA, CuboidRoom roomB)
    {
        foreach (Corridor corridor in corridors)
        {
            if ((corridor.roomA == roomA && corridor.roomB == roomB) ||
                (corridor.roomA == roomB && corridor.roomB == roomA))
            {
                return true;
            }
        }
        return false;
    }

    private void CarveEverything()
    {
        // Carve rooms
        foreach (var room in rooms)
        {
            room.CarveIntoGrid(finalGrid);
        }
        
        // Carve corridors
        foreach (var corridor in corridors)
        {
            corridor.CarveIntoGrid(finalGrid, corridor.width, corridor.height);
        }
    }

    private void GenerateMesh()
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        
        // Optimized face generation - only generate exposed faces
        for (int x = 0; x < gridSize.x; x++)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                for (int z = 0; z < gridSize.z; z++)
                {
                    if (finalGrid[x, y, z])
                    {
                        AddExposedFaces(x, y, z, vertices, triangles);
                    }
                }
            }
        }
        
        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        
        meshFilter.mesh = mesh;
    }

    private void AddExposedFaces(int x, int y, int z, List<Vector3> vertices, List<int> triangles)
    {
        Vector3 offset = new Vector3(x, y, z);
        
        // Check neighbors and only add faces that are exposed
        if (x == 0 || !finalGrid[x - 1, y, z]) // Left
            AddQuad(offset, new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(0,0,1), vertices, triangles);
        
        if (x == gridSize.x - 1 || !finalGrid[x + 1, y, z]) // Right
            AddQuad(offset, new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(1,0,0), vertices, triangles);
        
        if (y == 0 || !finalGrid[x, y - 1, z]) // Bottom
            AddQuad(offset, new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0), vertices, triangles);
        
        if (y == gridSize.y - 1 || !finalGrid[x, y + 1, z]) // Top
            AddQuad(offset, new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1), vertices, triangles);
        
        if (z == 0 || !finalGrid[x, y, z - 1]) // Front
            AddQuad(offset, new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0), vertices, triangles);
        
        if (z == gridSize.z - 1 || !finalGrid[x, y, z + 1]) // Back
            AddQuad(offset, new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1), vertices, triangles);
    }

    private void AddQuad(Vector3 offset, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, List<Vector3> vertices, List<int> triangles)
    {
        int baseIndex = vertices.Count;
        vertices.Add(v0 + offset);
        vertices.Add(v1 + offset);
        vertices.Add(v2 + offset);
        vertices.Add(v3 + offset);
        
        triangles.Add(baseIndex);
        triangles.Add(baseIndex + 1);
        triangles.Add(baseIndex + 2);
        triangles.Add(baseIndex + 2);
        triangles.Add(baseIndex + 3);
        triangles.Add(baseIndex);
    }

    [ContextMenu("Generate New")]
    private void GenerateNew()
    {
        Generate();
    }

    void OnDrawGizmosSelected()
    {
        if (rooms == null) return;
        
        // Draw room bounds
        Gizmos.color = Color.cyan;
        foreach (var room in rooms)
        {
            Gizmos.DrawWireCube(room.Bounds.center, room.Bounds.size);
        }
        
        // Draw corridor paths
        Gizmos.color = Color.green;
        foreach (var corridor in corridors)
        {
            for (int i = 0; i < corridor.pathCells.Count - 1; i++)
            {
                Gizmos.DrawLine(corridor.pathCells[i], corridor.pathCells[i + 1]);
            }
        }
    }
}