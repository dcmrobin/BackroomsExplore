using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class GeometricRoomGenerator : MonoBehaviour
{
    [Header("Generation Settings")]
    [SerializeField] private Vector3Int gridSize = new Vector3Int(64, 32, 64);
    [SerializeField] private int numberOfRooms = 10;
    [SerializeField] private float minDistanceBetweenRooms = 8f;
    
    [Header("Room Settings")]
    [SerializeField] private Vector2Int roomWidthRange = new Vector2Int(4, 10);
    [SerializeField] private Vector2Int roomHeightRange = new Vector2Int(3, 6);
    [SerializeField] private Vector2Int roomDepthRange = new Vector2Int(4, 10);
    
    [Header("Corridor Settings")]
    [SerializeField] private int corridorWidth = 2;
    [SerializeField] private int corridorHeight = 3;
    [SerializeField] private float connectionChance = 0.7f;
    
    [Header("Visualization")]
    [SerializeField] private Material caveMaterial;
    [SerializeField] private bool generateOnStart = true;
    [SerializeField] private bool showRoomCenters = true;
    
    private bool[,,] caveGrid;
    private List<Room> rooms = new List<Room>();
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    
    [System.Serializable]
    private class Room
    {
        public Vector3Int center;
        public Vector3Int size;
        public Bounds bounds;
        public List<Room> connections = new List<Room>();
        
        public Room(Vector3Int center, Vector3Int size)
        {
            this.center = center;
            this.size = size;
            
            Vector3 min = new Vector3(
                center.x - size.x / 2,
                center.y - size.y / 2,
                center.z - size.z / 2
            );
            
            Vector3 max = new Vector3(
                center.x + size.x / 2,
                center.y + size.y / 2,
                center.z + size.z / 2
            );
            
            bounds = new Bounds();
            bounds.SetMinMax(min, max);
        }
        
        public bool Overlaps(Room other, float minDistance = 0)
        {
            return bounds.Intersects(other.bounds) || 
                   Vector3.Distance(center, other.center) < minDistance;
        }
        
        public void CarveIntoGrid(bool[,,] grid)
        {
            Vector3Int min = Vector3Int.FloorToInt(bounds.min);
            Vector3Int max = Vector3Int.CeilToInt(bounds.max);
            
            for (int x = min.x; x <= max.x; x++)
            {
                for (int y = min.y; y <= max.y; y++)
                {
                    for (int z = min.z; z <= max.z; z++)
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
            GenerateRooms();
        }
    }

    public void GenerateRooms()
    {
        InitializeComponents();
        InitializeGrid();
        GenerateRoomPositions();
        ConnectRooms();
        CarveRoomsIntoGrid();
        GenerateMesh();
        
        if (showRoomCenters)
        {
            DrawRoomCenters();
        }
    }

    private void InitializeComponents()
    {
        if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
        
        if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();
        
        if (caveMaterial != null) meshRenderer.material = caveMaterial;
    }

    private void InitializeGrid()
    {
        caveGrid = new bool[gridSize.x, gridSize.y, gridSize.z];
    }

    private void GenerateRoomPositions()
    {
        rooms.Clear();
        int maxAttempts = numberOfRooms * 10;
        int attempts = 0;
        
        while (rooms.Count < numberOfRooms && attempts < maxAttempts)
        {
            attempts++;
            
            // Generate random room size
            Vector3Int roomSize = new Vector3Int(
                Random.Range(roomWidthRange.x, roomWidthRange.y + 1),
                Random.Range(roomHeightRange.x, roomHeightRange.y + 1),
                Random.Range(roomDepthRange.x, roomDepthRange.y + 1)
            );
            
            // Generate random position with padding from edges
            Vector3Int roomCenter = new Vector3Int(
                Random.Range(roomSize.x / 2 + 1, gridSize.x - roomSize.x / 2 - 1),
                Random.Range(roomSize.y / 2 + 1, gridSize.y - roomSize.y / 2 - 1),
                Random.Range(roomSize.z / 2 + 1, gridSize.z - roomSize.z / 2 - 1)
            );
            
            Room newRoom = new Room(roomCenter, roomSize);
            
            // Check for overlaps with existing rooms
            bool overlaps = false;
            foreach (var existingRoom in rooms)
            {
                if (newRoom.Overlaps(existingRoom, minDistanceBetweenRooms))
                {
                    overlaps = true;
                    break;
                }
            }
            
            if (!overlaps)
            {
                rooms.Add(newRoom);
                Debug.Log($"Created room {rooms.Count}: Center={roomCenter}, Size={roomSize}");
            }
        }
        
        Debug.Log($"Successfully placed {rooms.Count} rooms out of {numberOfRooms} attempted");
    }

    private void ConnectRooms()
    {
        if (rooms.Count < 2) return;
        
        // Create a minimum spanning tree to ensure all rooms are connected
        List<Room> connected = new List<Room>();
        List<Room> unconnected = new List<Room>(rooms);
        
        // Start with first room
        connected.Add(unconnected[0]);
        unconnected.RemoveAt(0);
        
        while (unconnected.Count > 0)
        {
            float closestDistance = float.MaxValue;
            Room closestConnected = null;
            Room closestUnconnected = null;
            
            // Find closest pair of connected-unconnected rooms
            foreach (Room connectedRoom in connected)
            {
                foreach (Room unconnectedRoom in unconnected)
                {
                    float distance = Vector3.Distance(connectedRoom.center, unconnectedRoom.center);
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        closestConnected = connectedRoom;
                        closestUnconnected = unconnectedRoom;
                    }
                }
            }
            
            // Connect them
            if (closestConnected != null && closestUnconnected != null)
            {
                CreateCorridor(closestConnected, closestUnconnected);
                closestConnected.connections.Add(closestUnconnected);
                closestUnconnected.connections.Add(closestConnected);
                
                connected.Add(closestUnconnected);
                unconnected.Remove(closestUnconnected);
            }
        }
        
        // Add some extra random connections for more complexity
        AddExtraConnections();
    }

    private void AddExtraConnections()
    {
        if (rooms.Count < 3) return;
        
        int extraConnections = Mathf.FloorToInt(rooms.Count * 0.3f);
        
        for (int i = 0; i < extraConnections; i++)
        {
            Room roomA = rooms[Random.Range(0, rooms.Count)];
            Room roomB = rooms[Random.Range(0, rooms.Count)];
            
            if (roomA != roomB && 
                !roomA.connections.Contains(roomB) && 
                Random.value < connectionChance)
            {
                CreateCorridor(roomA, roomB);
                roomA.connections.Add(roomB);
                roomB.connections.Add(roomA);
            }
        }
    }

    private void CreateCorridor(Room roomA, Room roomB)
    {
        // Create L-shaped corridor (horizontal then vertical or vice versa)
        // This creates more natural looking corridors
        
        Vector3Int start = roomA.center;
        Vector3Int end = roomB.center;
        
        // Decide whether to go horizontal first or vertical first
        if (Random.value > 0.5f)
        {
            // Horizontal first (X), then vertical (Y), then depth (Z)
            CreateCorridorSegment(
                start,
                new Vector3Int(end.x, start.y, start.z),
                corridorWidth,
                corridorHeight
            );
            
            CreateCorridorSegment(
                new Vector3Int(end.x, start.y, start.z),
                new Vector3Int(end.x, end.y, start.z),
                corridorWidth,
                corridorHeight
            );
            
            CreateCorridorSegment(
                new Vector3Int(end.x, end.y, start.z),
                new Vector3Int(end.x, end.y, end.z),
                corridorWidth,
                corridorHeight
            );
        }
        else
        {
            // Vertical first (Y), then horizontal (X), then depth (Z)
            CreateCorridorSegment(
                start,
                new Vector3Int(start.x, end.y, start.z),
                corridorWidth,
                corridorHeight
            );
            
            CreateCorridorSegment(
                new Vector3Int(start.x, end.y, start.z),
                new Vector3Int(end.x, end.y, start.z),
                corridorWidth,
                corridorHeight
            );
            
            CreateCorridorSegment(
                new Vector3Int(end.x, end.y, start.z),
                new Vector3Int(end.x, end.y, end.z),
                corridorWidth,
                corridorHeight
            );
        }
    }

    private void CreateCorridorSegment(Vector3Int start, Vector3Int end, int width, int height)
    {
        // Create a rectangular corridor between two points
        Vector3Int direction = end - start;
        int steps = Mathf.Max(Mathf.Abs(direction.x), Mathf.Abs(direction.y), Mathf.Abs(direction.z));
        
        if (steps == 0) return;
        
        Vector3 step = new Vector3(
            direction.x / (float)steps,
            direction.y / (float)steps,
            direction.z / (float)steps
        );
        
        for (int i = 0; i <= steps; i++)
        {
            Vector3 currentPos = start + step * i;
            Vector3Int gridPos = Vector3Int.RoundToInt(currentPos);
            
            // Create cross-section of corridor
            int halfWidth = width / 2;
            int halfHeight = height / 2;
            
            for (int dx = -halfWidth; dx <= halfWidth; dx++)
            {
                for (int dy = -halfHeight; dy <= halfHeight; dy++)
                {
                    for (int dz = 0; dz < 1; dz++) // Only 1 unit deep in the non-direction axis
                    {
                        // We need to determine which axis we're not moving in
                        Vector3Int offset;
                        if (Mathf.Abs(direction.x) > 0)
                        {
                            // Moving in X direction, corridor extends in Y and Z
                            offset = new Vector3Int(0, dy, dx);
                        }
                        else if (Mathf.Abs(direction.y) > 0)
                        {
                            // Moving in Y direction, corridor extends in X and Z
                            offset = new Vector3Int(dx, 0, dz);
                        }
                        else
                        {
                            // Moving in Z direction, corridor extends in X and Y
                            offset = new Vector3Int(dx, dy, 0);
                        }
                        
                        Vector3Int corridorCell = gridPos + offset;
                        
                        if (IsInGrid(corridorCell))
                        {
                            caveGrid[corridorCell.x, corridorCell.y, corridorCell.z] = true;
                        }
                    }
                }
            }
        }
    }

    private bool IsInGrid(Vector3Int pos)
    {
        return pos.x >= 0 && pos.x < gridSize.x &&
               pos.y >= 0 && pos.y < gridSize.y &&
               pos.z >= 0 && pos.z < gridSize.z;
    }

    private void CarveRoomsIntoGrid()
    {
        foreach (Room room in rooms)
        {
            room.CarveIntoGrid(caveGrid);
        }
    }

    private void GenerateMesh()
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        
        for (int x = 0; x < gridSize.x; x++)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                for (int z = 0; z < gridSize.z; z++)
                {
                    if (caveGrid[x, y, z])
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
        
        Debug.Log($"Generated mesh with {vertices.Count} vertices and {triangles.Count / 3} triangles");
    }

    private void AddCubeFaces(int x, int y, int z, List<Vector3> vertices, List<int> triangles)
    {
        Vector3 offset = new Vector3(x, y, z);
        
        // Front face (facing negative Z)
        if (z == 0 || !caveGrid[x, y, z - 1])
        {
            int vIndex = vertices.Count;
            vertices.Add(new Vector3(0, 0, 0) + offset);
            vertices.Add(new Vector3(1, 0, 0) + offset);
            vertices.Add(new Vector3(1, 1, 0) + offset);
            vertices.Add(new Vector3(0, 1, 0) + offset);
            
            triangles.Add(vIndex);
            triangles.Add(vIndex + 1);
            triangles.Add(vIndex + 2);
            triangles.Add(vIndex + 2);
            triangles.Add(vIndex + 3);
            triangles.Add(vIndex);
        }
        
        // Back face (facing positive Z)
        if (z == gridSize.z - 1 || !caveGrid[x, y, z + 1])
        {
            int vIndex = vertices.Count;
            vertices.Add(new Vector3(0, 0, 1) + offset);
            vertices.Add(new Vector3(1, 0, 1) + offset);
            vertices.Add(new Vector3(1, 1, 1) + offset);
            vertices.Add(new Vector3(0, 1, 1) + offset);
            
            triangles.Add(vIndex + 1);
            triangles.Add(vIndex);
            triangles.Add(vIndex + 3);
            triangles.Add(vIndex + 3);
            triangles.Add(vIndex + 2);
            triangles.Add(vIndex + 1);
        }
        
        // Left face (facing negative X)
        if (x == 0 || !caveGrid[x - 1, y, z])
        {
            int vIndex = vertices.Count;
            vertices.Add(new Vector3(0, 0, 0) + offset);
            vertices.Add(new Vector3(0, 1, 0) + offset);
            vertices.Add(new Vector3(0, 1, 1) + offset);
            vertices.Add(new Vector3(0, 0, 1) + offset);
            
            triangles.Add(vIndex);
            triangles.Add(vIndex + 1);
            triangles.Add(vIndex + 2);
            triangles.Add(vIndex + 2);
            triangles.Add(vIndex + 3);
            triangles.Add(vIndex);
        }
        
        // Right face (facing positive X)
        if (x == gridSize.x - 1 || !caveGrid[x + 1, y, z])
        {
            int vIndex = vertices.Count;
            vertices.Add(new Vector3(1, 0, 0) + offset);
            vertices.Add(new Vector3(1, 0, 1) + offset);
            vertices.Add(new Vector3(1, 1, 1) + offset);
            vertices.Add(new Vector3(1, 1, 0) + offset);
            
            triangles.Add(vIndex);
            triangles.Add(vIndex + 1);
            triangles.Add(vIndex + 2);
            triangles.Add(vIndex + 2);
            triangles.Add(vIndex + 3);
            triangles.Add(vIndex);
        }
        
        // Bottom face (facing negative Y)
        if (y == 0 || !caveGrid[x, y - 1, z])
        {
            int vIndex = vertices.Count;
            vertices.Add(new Vector3(0, 0, 0) + offset);
            vertices.Add(new Vector3(0, 0, 1) + offset);
            vertices.Add(new Vector3(1, 0, 1) + offset);
            vertices.Add(new Vector3(1, 0, 0) + offset);
            
            triangles.Add(vIndex);
            triangles.Add(vIndex + 1);
            triangles.Add(vIndex + 2);
            triangles.Add(vIndex + 2);
            triangles.Add(vIndex + 3);
            triangles.Add(vIndex);
        }
        
        // Top face (facing positive Y)
        if (y == gridSize.y - 1 || !caveGrid[x, y + 1, z])
        {
            int vIndex = vertices.Count;
            vertices.Add(new Vector3(0, 1, 0) + offset);
            vertices.Add(new Vector3(1, 1, 0) + offset);
            vertices.Add(new Vector3(1, 1, 1) + offset);
            vertices.Add(new Vector3(0, 1, 1) + offset);
            
            triangles.Add(vIndex);
            triangles.Add(vIndex + 1);
            triangles.Add(vIndex + 2);
            triangles.Add(vIndex + 2);
            triangles.Add(vIndex + 3);
            triangles.Add(vIndex);
        }
    }

    private void DrawRoomCenters()
    {
        foreach (Room room in rooms)
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.position = room.center;
            sphere.transform.localScale = Vector3.one * 0.5f;
            sphere.GetComponent<Renderer>().material.color = Color.red;
            sphere.name = $"RoomCenter_{room.center}";
            
            // Draw connection lines
            foreach (Room connectedRoom in room.connections)
            {
                Debug.DrawLine(room.center, connectedRoom.center, Color.green, 10f);
            }
        }
    }

    // Editor button for testing
    [ContextMenu("Generate New Rooms")]
    private void GenerateNewRooms()
    {
        GenerateRooms();
    }

    void OnDrawGizmosSelected()
    {
        if (rooms == null) return;
        
        Gizmos.color = Color.blue;
        foreach (Room room in rooms)
        {
            Gizmos.DrawWireCube(room.bounds.center, room.bounds.size);
            
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(room.center, 0.3f);
            Gizmos.color = Color.blue;
        }
    }
}