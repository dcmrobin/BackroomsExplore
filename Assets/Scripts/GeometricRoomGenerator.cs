using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class CuboidRoomFitter : MonoBehaviour
{
    [Header("Noise Settings")]
    [SerializeField] private Vector3Int gridSize = new Vector3Int(64, 32, 64);
    [SerializeField] private float noiseScale = 0.1f;
    [SerializeField] [Range(0, 1)] private float fillThreshold = 0.45f;
    [SerializeField] private int smoothingIterations = 3;
    
    [Header("Room Settings")]
    [SerializeField] private int minRoomSize = 3;
    [SerializeField] private int maxRoomSize = 15;
    [SerializeField] private bool allowNonCubic = true;
    [SerializeField] private float aspectRatioTolerance = 0.5f;
    
    [Header("Corridor Settings")]
    [SerializeField] private int corridorWidth = 2;
    [SerializeField] private int corridorHeight = 3;
    
    [Header("Visualization")]
    [SerializeField] private Material caveMaterial;
    [SerializeField] private bool generateOnStart = true;
    [SerializeField] private bool showRoomBounds = true;
    
    private bool[,,] noiseGrid;
    private bool[,,] finalGrid;
    private List<CuboidRoom> rooms = new List<CuboidRoom>();
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    
    private class CuboidRoom
    {
        public Vector3Int minBounds;
        public Vector3Int maxBounds;
        public Vector3Int center;
        public Vector3Int size;
        public List<CuboidRoom> connections = new List<CuboidRoom>();
        
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
        
        public bool ContainsPoint(Vector3Int point)
        {
            return point.x >= minBounds.x && point.x <= maxBounds.x &&
                   point.y >= minBounds.y && point.y <= maxBounds.y &&
                   point.z >= minBounds.z && point.z <= maxBounds.z;
        }
        
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
        InitializeComponents();
        GenerateNoise();
        FindCubicRoomsInNoise();
        ConnectRooms();
        CarveRoomsAndCorridors();
        GenerateMesh();
        
        Debug.Log($"Generated {rooms.Count} cubic rooms");
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
        
        // Generate 3D Perlin noise
        for (int x = 0; x < gridSize.x; x++)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                for (int z = 0; z < gridSize.z; z++)
                {
                    float noiseValue = Mathf.PerlinNoise(
                        x * noiseScale + 1000,
                        y * noiseScale + z * noiseScale + 2000
                    );
                    
                    // Vertical bias - keep rooms more grounded
                    float verticalBias = 1f - Mathf.Abs(y - gridSize.y * 0.3f) / (gridSize.y * 0.3f);
                    noiseValue *= (0.7f + 0.3f * verticalBias);
                    
                    noiseGrid[x, y, z] = noiseValue > fillThreshold;
                }
            }
        }
        
        SmoothNoise();
    }

    private void SmoothNoise()
    {
        for (int i = 0; i < smoothingIterations; i++)
        {
            bool[,,] smoothed = new bool[gridSize.x, gridSize.y, gridSize.z];
            
            for (int x = 0; x < gridSize.x; x++)
            {
                for (int y = 0; y < gridSize.y; y++)
                {
                    for (int z = 0; z < gridSize.z; z++)
                    {
                        int solidNeighbors = CountNeighbors(x, y, z, noiseGrid);
                        smoothed[x, y, z] = solidNeighbors >= 14; // Keep only if well-supported
                    }
                }
            }
            
            noiseGrid = smoothed;
        }
    }

    private int CountNeighbors(int x, int y, int z, bool[,,] grid)
    {
        int count = 0;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    int nz = z + dz;
                    
                    if (nx >= 0 && nx < gridSize.x &&
                        ny >= 0 && ny < gridSize.y &&
                        nz >= 0 && nz < gridSize.z &&
                        grid[nx, ny, nz])
                    {
                        count++;
                    }
                }
            }
        }
        return count;
    }

    private void FindCubicRoomsInNoise()
    {
        rooms.Clear();
        bool[,,] visited = new bool[gridSize.x, gridSize.y, gridSize.z];
        
        // Scan through grid looking for potential room locations
        for (int x = 0; x < gridSize.x - minRoomSize; x++)
        {
            for (int y = 0; y < gridSize.y - minRoomSize; y++)
            {
                for (int z = 0; z < gridSize.z - minRoomSize; z++)
                {
                    if (!noiseGrid[x, y, z] || visited[x, y, z]) continue;
                    
                    // Find the largest possible cuboid starting from this point
                    CuboidRoom room = FindLargestCuboidAt(x, y, z, visited);
                    if (room != null && room.Volume >= minRoomSize * minRoomSize * minRoomSize)
                    {
                        // Check if room overlaps with existing rooms
                        bool overlaps = false;
                        foreach (var existingRoom in rooms)
                        {
                            if (room.Overlaps(existingRoom))
                            {
                                overlaps = true;
                                break;
                            }
                        }
                        
                        if (!overlaps)
                        {
                            rooms.Add(room);
                            
                            // Mark all cells in this room as visited
                            for (int rx = room.minBounds.x; rx <= room.maxBounds.x; rx++)
                            {
                                for (int ry = room.minBounds.y; ry <= room.maxBounds.y; ry++)
                                {
                                    for (int rz = room.minBounds.z; rz <= room.maxBounds.z; rz++)
                                    {
                                        if (rx >= 0 && rx < gridSize.x &&
                                            ry >= 0 && ry < gridSize.y &&
                                            rz >= 0 && rz < gridSize.z)
                                        {
                                            visited[rx, ry, rz] = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        
        Debug.Log($"Found {rooms.Count} potential cubic rooms in noise");
    }

    private CuboidRoom FindLargestCuboidAt(int startX, int startY, int startZ, bool[,,] visited)
    {
        // Find the maximum possible cuboid starting from this point
        int maxWidth = FindMaxDimension(startX, startY, startZ, Vector3Int.right);
        int maxHeight = FindMaxDimension(startX, startY, startZ, Vector3Int.up);
        int maxDepth = FindMaxDimension(startX, startY, startZ, Vector3Int.forward);
        
        // Try different cuboid sizes, favoring larger volumes
        CuboidRoom bestRoom = null;
        int bestVolume = 0;
        
        // Try different combinations of dimensions
        for (int w = minRoomSize; w <= Mathf.Min(maxWidth, maxRoomSize); w++)
        {
            for (int h = minRoomSize; h <= Mathf.Min(maxHeight, maxRoomSize); h++)
            {
                for (int d = minRoomSize; d <= Mathf.Min(maxDepth, maxRoomSize); d++)
                {
                    // Check if this entire cuboid is solid in noise grid
                    if (IsCuboidSolid(startX, startY, startZ, w, h, d))
                    {
                        int volume = w * h * d;
                        
                        // Apply aspect ratio constraints if not allowing non-cubic
                        if (!allowNonCubic)
                        {
                            float maxDim = Mathf.Max(w, h, d);
                            float minDim = Mathf.Min(w, h, d);
                            if (maxDim / minDim > 2f) continue; // Too elongated
                        }
                        
                        if (volume > bestVolume)
                        {
                            bestVolume = volume;
                            bestRoom = new CuboidRoom(
                                new Vector3Int(startX, startY, startZ),
                                new Vector3Int(startX + w - 1, startY + h - 1, startZ + d - 1)
                            );
                        }
                    }
                }
            }
        }
        
        return bestRoom;
    }

    private int FindMaxDimension(int x, int y, int z, Vector3Int direction)
    {
        int length = 0;
        Vector3Int current = new Vector3Int(x, y, z);
        
        while (current.x >= 0 && current.x < gridSize.x &&
               current.y >= 0 && current.y < gridSize.y &&
               current.z >= 0 && current.z < gridSize.z &&
               noiseGrid[current.x, current.y, current.z] &&
               length < maxRoomSize)
        {
            length++;
            current += direction;
        }
        
        return length;
    }

    private bool IsCuboidSolid(int startX, int startY, int startZ, int width, int height, int depth)
    {
        for (int x = startX; x < startX + width; x++)
        {
            for (int y = startY; y < startY + height; y++)
            {
                for (int z = startZ; z < startZ + depth; z++)
                {
                    if (x >= gridSize.x || y >= gridSize.y || z >= gridSize.z ||
                        !noiseGrid[x, y, z])
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    private void ConnectRooms()
    {
        if (rooms.Count < 2) return;
        
        // Sort rooms by volume (largest first)
        var sortedRooms = rooms.OrderByDescending(r => r.Volume).ToList();
        
        // Create minimum spanning tree
        List<CuboidRoom> connected = new List<CuboidRoom>();
        List<CuboidRoom> unconnected = new List<CuboidRoom>(sortedRooms);
        
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
                    float distance = Vector3.Distance(connectedRoom.center, unconnectedRoom.center);
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
                CreateCorridor(closestConnected, closestRoom);
                closestConnected.connections.Add(closestRoom);
                closestRoom.connections.Add(closestConnected);
                
                connected.Add(closestRoom);
                unconnected.Remove(closestRoom);
            }
        }
        
        // Add some extra connections for loops
        AddExtraConnections();
    }

    private void AddExtraConnections()
    {
        if (rooms.Count < 3) return;
        
        int extraConnections = Mathf.Max(1, rooms.Count / 3);
        
        for (int i = 0; i < extraConnections; i++)
        {
            CuboidRoom roomA = rooms[Random.Range(0, rooms.Count)];
            CuboidRoom roomB = rooms[Random.Range(0, rooms.Count)];
            
            if (roomA != roomB && 
                !roomA.connections.Contains(roomB) &&
                Vector3.Distance(roomA.center, roomB.center) < GetAverageRoomDistance() * 1.5f)
            {
                CreateCorridor(roomA, roomB);
                roomA.connections.Add(roomB);
                roomB.connections.Add(roomA);
            }
        }
    }

    private float GetAverageRoomDistance()
    {
        if (rooms.Count < 2) return 0;
        
        float total = 0;
        int pairs = 0;
        
        for (int i = 0; i < rooms.Count; i++)
        {
            for (int j = i + 1; j < rooms.Count; j++)
            {
                total += Vector3.Distance(rooms[i].center, rooms[j].center);
                pairs++;
            }
        }
        
        return total / pairs;
    }

    private void CreateCorridor(CuboidRoom roomA, CuboidRoom roomB)
    {
        Vector3Int start = roomA.center;
        Vector3Int end = roomB.center;
        
        // Create L-shaped corridor
        CreateCorridorSegment(start, new Vector3Int(end.x, start.y, start.z));
        CreateCorridorSegment(new Vector3Int(end.x, start.y, start.z), 
                            new Vector3Int(end.x, end.y, start.z));
        CreateCorridorSegment(new Vector3Int(end.x, end.y, start.z), 
                            new Vector3Int(end.x, end.y, end.z));
    }

    private void CreateCorridorSegment(Vector3Int start, Vector3Int end)
    {
        Vector3Int dir = end - start;
        int steps = Mathf.Max(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z));
        
        if (steps == 0) return;
        
        Vector3 step = new Vector3(dir.x / (float)steps, dir.y / (float)steps, dir.z / (float)steps);
        
        for (int i = 0; i <= steps; i++)
        {
            Vector3 pos = start + step * i;
            Vector3Int gridPos = new Vector3Int(Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.y), Mathf.RoundToInt(pos.z));
            
            // Create corridor cross-section
            for (int dx = -corridorWidth; dx <= corridorWidth; dx++)
            {
                for (int dy = -corridorHeight; dy <= corridorHeight; dy++)
                {
                    for (int dz = -corridorWidth; dz <= corridorWidth; dz++)
                    {
                        // Simple box corridor
                        if (Mathf.Abs(dx) + Mathf.Abs(dy) + Mathf.Abs(dz) <= corridorWidth + 1)
                        {
                            Vector3Int corridorCell = gridPos + new Vector3Int(dx, dy, dz);
                            
                            if (corridorCell.x >= 0 && corridorCell.x < gridSize.x &&
                                corridorCell.y >= 0 && corridorCell.y < gridSize.y &&
                                corridorCell.z >= 0 && corridorCell.z < gridSize.z)
                            {
                                finalGrid[corridorCell.x, corridorCell.y, corridorCell.z] = true;
                            }
                        }
                    }
                }
            }
        }
    }

    private void CarveRoomsAndCorridors()
    {
        // Carve all rooms into final grid
        foreach (var room in rooms)
        {
            room.CarveIntoGrid(finalGrid);
        }
    }

    private void GenerateMesh()
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        
        // Simple greedy meshing
        for (int x = 0; x < gridSize.x; x++)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                for (int z = 0; z < gridSize.z; z++)
                {
                    if (finalGrid[x, y, z])
                    {
                        AddCubeFaces(x, y, z, vertices, triangles);
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
        mesh.Optimize();
        
        meshFilter.mesh = mesh;
        
        Debug.Log($"Generated mesh with {vertices.Count} vertices");
    }

    private void AddCubeFaces(int x, int y, int z, List<Vector3> vertices, List<int> triangles)
    {
        Vector3 offset = new Vector3(x, y, z);
        
        // Front
        if (z == 0 || !finalGrid[x, y, z - 1])
            AddFace(offset, new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0), vertices, triangles);
        
        // Back
        if (z == gridSize.z - 1 || !finalGrid[x, y, z + 1])
            AddFace(offset, new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1), vertices, triangles);
        
        // Left
        if (x == 0 || !finalGrid[x - 1, y, z])
            AddFace(offset, new Vector3(0,0,1), new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1), vertices, triangles);
        
        // Right
        if (x == gridSize.x - 1 || !finalGrid[x + 1, y, z])
            AddFace(offset, new Vector3(1,0,0), new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0), vertices, triangles);
        
        // Bottom
        if (y == 0 || !finalGrid[x, y - 1, z])
            AddFace(offset, new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0), vertices, triangles);
        
        // Top
        if (y == gridSize.y - 1 || !finalGrid[x, y + 1, z])
            AddFace(offset, new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1), vertices, triangles);
    }

    private void AddFace(Vector3 offset, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, List<Vector3> vertices, List<int> triangles)
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
        if (!showRoomBounds || rooms == null) return;
        
        // Draw room bounds
        Gizmos.color = Color.cyan;
        foreach (var room in rooms)
        {
            Gizmos.DrawWireCube(room.Bounds.center, room.Bounds.size);
            
            // Draw connections
            Gizmos.color = Color.green;
            foreach (var connectedRoom in room.connections)
            {
                Gizmos.DrawLine(room.center, connectedRoom.center);
            }
            Gizmos.color = Color.cyan;
        }
    }
}