using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class GeometricRoomGenerator : MonoBehaviour
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
    
    [Header("Lighting")]
    [SerializeField] private float lightPlacementChance = 0.2f;
    [SerializeField] private int minLightsPerRoom = 1;
    [SerializeField] private int maxLightsPerRoom = 3;
    [SerializeField] private float lightDecay = 0.15f;
    [SerializeField] private int lightPropagationSteps = 15;
    [SerializeField] private float lightSourceIntensity = 1.0f;
    
    [Header("Textures")]
    [SerializeField] private Material dungeonMaterial;
    [SerializeField] private Texture2D wallTexture;
    [SerializeField] private Texture2D floorTexture;
    [SerializeField] private Texture2D ceilingTexture;
    [SerializeField] private Vector2 textureScale = Vector2.one;
    
    [Header("Generation Optimization")]
    [SerializeField] private int scanStep = 2;
    
    [Header("Visualization")]
    [SerializeField] private bool generateOnStart = true;
    
    private bool[,,] noiseGrid;
    private bool[,,] finalGrid; // True = solid, False = empty
    private float[,,] lightGrid; // Stores light level for each voxel (0-1)
    private List<CuboidRoom> rooms = new List<CuboidRoom>();
    private List<Corridor> corridors = new List<Corridor>();
    private List<Vector3Int> lightPositions = new List<Vector3Int>();
    private HashSet<Vector3Int> lightPositionSet = new HashSet<Vector3Int>();
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    
    // Material IDs
    private const byte MATERIAL_WALL = 0;
    private const byte MATERIAL_FLOOR = 1;
    private const byte MATERIAL_CEILING = 2;
    private const byte MATERIAL_LIGHT = 3;
    
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
                        if (IsInGrid(x, y, z, grid))
                        {
                            grid[x, y, z] = true;
                        }
                    }
                }
            }
        }
        
        private bool IsInGrid(int x, int y, int z, bool[,,] grid)
        {
            return x >= 0 && x < grid.GetLength(0) &&
                   y >= 0 && y < grid.GetLength(1) &&
                   z >= 0 && z < grid.GetLength(2);
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
            
            for (int i = 0; i < pathCells.Count; i++)
            {
                Vector3Int cell = pathCells[i];
                
                bool isHorizontal = (i > 0 && Mathf.Abs(pathCells[i].x - pathCells[i-1].x) > 0) || 
                                   (i < pathCells.Count - 1 && Mathf.Abs(pathCells[i+1].x - pathCells[i].x) > 0);
                
                int halfWidth = width / 2;
                int startY = cell.y - height / 2;
                
                if (isHorizontal)
                {
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
        
        PlaceLights();
        
        Debug.Log($"Light placement: {stopwatch.ElapsedMilliseconds}ms");
        stopwatch.Restart();
        
        CalculateVoxelLighting();
        
        Debug.Log($"Lighting calculation: {stopwatch.ElapsedMilliseconds}ms");
        stopwatch.Restart();
        
        SetupMaterial();
        GenerateLitMesh();
        
        Debug.Log($"Mesh generation: {stopwatch.ElapsedMilliseconds}ms");
        Debug.Log($"Total: {rooms.Count} rooms, {corridors.Count} corridors, {lightPositions.Count} lights");
        
        stopwatch.Stop();
    }

    private void InitializeComponents()
    {
        if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
        
        if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();
        
        if (dungeonMaterial != null)
        {
            meshRenderer.material = new Material(dungeonMaterial);
        }
    }

    private void SetupMaterial()
    {
        if (meshRenderer.material == null) return;
        
        if (wallTexture != null && floorTexture != null && ceilingTexture != null)
        {
            meshRenderer.material.SetTexture("_WallTex", wallTexture);
            meshRenderer.material.SetTexture("_FloorTex", floorTexture);
            meshRenderer.material.SetTexture("_CeilingTex", ceilingTexture);
            meshRenderer.material.SetTextureScale("_MainTex", textureScale);
        }
    }

    private void GenerateNoise()
    {
        noiseGrid = new bool[gridSize.x, gridSize.y, gridSize.z];
        finalGrid = new bool[gridSize.x, gridSize.y, gridSize.z];
        lightGrid = new float[gridSize.x, gridSize.y, gridSize.z];
        
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
                    
                    float verticalBias = 1f - Mathf.Abs(y - gridSize.y * 0.3f) / (gridSize.y * 0.3f);
                    noiseValue *= (0.7f + 0.3f * verticalBias);
                    
                    bool isSolid = noiseValue > fillThreshold;
                    
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
        
        for (int x = 0; x < gridSize.x - minRoomSize; x += scanStep)
        {
            for (int y = 0; y < gridSize.y - minRoomSize; y += scanStep)
            {
                for (int z = 0; z < gridSize.z - minRoomSize; z += scanStep)
                {
                    if (occupied[x, y, z] || !noiseGrid[x, y, z]) continue;
                    
                    if (QuickSolidCheck(x, y, z, minRoomSize))
                    {
                        CuboidRoom room = FindBestCuboidAt(x, y, z, occupied);
                        if (room != null && room.size.x >= minRoomSize && room.size.y >= minRoomSize && room.size.z >= minRoomSize)
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
        int maxX = FindMaxDimension(startX, startY, startZ, Vector3Int.right, maxRoomSize);
        int maxY = FindMaxDimension(startX, startY, startZ, Vector3Int.up, maxRoomSize);
        int maxZ = FindMaxDimension(startX, startY, startZ, Vector3Int.forward, maxRoomSize);
        
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
        if (x + sizeX > gridSize.x || y + sizeY > gridSize.y || z + sizeZ > gridSize.z)
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

    private void MarkRoomOccupied(CuboidRoom room, bool[,,] occupied)
    {
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
        
        AddExtraCorridors();
    }

    private void CreateCorridorBetween(CuboidRoom roomA, CuboidRoom roomB)
    {
        Corridor corridor = new Corridor();
        corridor.roomA = roomA;
        corridor.roomB = roomB;
        
        Vector3Int startPoint = roomA.GetClosestSurfacePoint(roomB.center);
        Vector3Int endPoint = roomB.GetClosestSurfacePoint(roomA.center);
        
        corridor.start = startPoint;
        corridor.end = endPoint;
        
        List<Vector3Int> path = new List<Vector3Int>();
        
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
        // Clear grid
        for (int x = 0; x < gridSize.x; x++)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                for (int z = 0; z < gridSize.z; z++)
                {
                    finalGrid[x, y, z] = false;
                }
            }
        }
        
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

    private void PlaceLights()
    {
        lightPositions.Clear();
        lightPositionSet.Clear();
        
        foreach (var room in rooms)
        {
            int lightsInRoom = Random.Range(minLightsPerRoom, maxLightsPerRoom + 1);
            
            for (int i = 0; i < lightsInRoom; i++)
            {
                if (Random.value <= lightPlacementChance)
                {
                    // Place light in ceiling (top layer of room)
                    int lightX = Random.Range(room.minBounds.x + 1, room.maxBounds.x);
                    int lightY = room.maxBounds.y; // Ceiling level
                    int lightZ = Random.Range(room.minBounds.z + 1, room.maxBounds.z);
                    
                    Vector3Int lightPos = new Vector3Int(lightX, lightY, lightZ);
                    
                    // Make sure position is valid and in grid
                    if (IsInGrid(lightPos) && finalGrid[lightPos.x, lightPos.y, lightPos.z])
                    {
                        lightPositions.Add(lightPos);
                        lightPositionSet.Add(lightPos);
                    }
                }
            }
        }
        
        // Also place some lights in corridors
        foreach (var corridor in corridors)
        {
            if (Random.value < lightPlacementChance * 0.3f) // Fewer lights in corridors
            {
                if (corridor.pathCells.Count > 0)
                {
                    // Pick a random cell along the corridor path
                    int index = Random.Range(0, corridor.pathCells.Count);
                    Vector3Int cell = corridor.pathCells[index];
                    
                    // Place light at ceiling level of corridor
                    Vector3Int lightPos = new Vector3Int(
                        cell.x,
                        cell.y + corridor.height / 2, // Ceiling of corridor
                        cell.z
                    );
                    
                    if (IsInGrid(lightPos) && finalGrid[lightPos.x, lightPos.y, lightPos.z])
                    {
                        lightPositions.Add(lightPos);
                        lightPositionSet.Add(lightPos);
                    }
                }
            }
        }
    }

    private void CalculateVoxelLighting()
    {
        // Initialize light grid to zero
        for (int x = 0; x < gridSize.x; x++)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                for (int z = 0; z < gridSize.z; z++)
                {
                    lightGrid[x, y, z] = 0f;
                }
            }
        }
        
        // FIRST: Set initial light values for light sources AND their neighboring air voxels
        foreach (var lightPos in lightPositions)
        {
            if (!IsInGrid(lightPos)) continue;
            
            // Set the light source itself to full brightness (it will glow)
            lightGrid[lightPos.x, lightPos.y, lightPos.z] = lightSourceIntensity;
            
            // Also set neighboring EMPTY voxels to light (light radiates from source)
            // This is key: light emits from the source into adjacent empty space
            Vector3Int[] directions = {
                Vector3Int.right, Vector3Int.left,
                Vector3Int.up, Vector3Int.down,
                Vector3Int.forward, Vector3Int.back
            };
            
            foreach (var dir in directions)
            {
                Vector3Int neighbor = lightPos + dir;
                if (IsInGrid(neighbor) && !finalGrid[neighbor.x, neighbor.y, neighbor.z])
                {
                    // Direct neighbors get almost full light (small decay)
                    lightGrid[neighbor.x, neighbor.y, neighbor.z] = 
                        Mathf.Max(lightGrid[neighbor.x, neighbor.y, neighbor.z], 
                        lightSourceIntensity - lightDecay * 0.5f);
                }
            }
        }
        
        // SECOND: Propagate light through empty space (multi-pass propagation)
        for (int step = 0; step < lightPropagationSteps; step++)
        {
            float[,,] newLightGrid = (float[,,])lightGrid.Clone();
            
            for (int x = 0; x < gridSize.x; x++)
            {
                for (int y = 0; y < gridSize.y; y++)
                {
                    for (int z = 0; z < gridSize.z; z++)
                    {
                        // Skip solid voxels - they don't propagate light
                        if (finalGrid[x, y, z]) continue;
                        
                        // Get the brightest light from all 6 neighboring EMPTY voxels
                        float maxNeighborLight = 0f;
                        
                        // Check all 6 directions
                        if (x > 0 && !finalGrid[x-1, y, z])
                            maxNeighborLight = Mathf.Max(maxNeighborLight, lightGrid[x-1, y, z]);
                        
                        if (x < gridSize.x - 1 && !finalGrid[x+1, y, z])
                            maxNeighborLight = Mathf.Max(maxNeighborLight, lightGrid[x+1, y, z]);
                        
                        if (y > 0 && !finalGrid[x, y-1, z])
                            maxNeighborLight = Mathf.Max(maxNeighborLight, lightGrid[x, y-1, z]);
                        
                        if (y < gridSize.y - 1 && !finalGrid[x, y+1, z])
                            maxNeighborLight = Mathf.Max(maxNeighborLight, lightGrid[x, y+1, z]);
                        
                        if (z > 0 && !finalGrid[x, y, z-1])
                            maxNeighborLight = Mathf.Max(maxNeighborLight, lightGrid[x, y, z-1]);
                        
                        if (z < gridSize.z - 1 && !finalGrid[x, y, z+1])
                            maxNeighborLight = Mathf.Max(maxNeighborLight, lightGrid[x, y, z+1]);
                        
                        // Apply decay to propagated light
                        float propagatedLight = Mathf.Max(0, maxNeighborLight - lightDecay);
                        
                        // Keep the brighter of: current light or propagated light
                        newLightGrid[x, y, z] = Mathf.Max(newLightGrid[x, y, z], propagatedLight);
                    }
                }
            }
            
            // Update light grid for next iteration
            lightGrid = newLightGrid;
        }
        
        // THIRD: Now solid voxels can receive light from adjacent empty voxels
        // This makes walls near lights appear lit
        for (int x = 0; x < gridSize.x; x++)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                for (int z = 0; z < gridSize.z; z++)
                {
                    // Only process solid voxels
                    if (!finalGrid[x, y, z]) continue;
                    
                    // Check if this is a light source - it should be bright
                    if (lightPositionSet.Contains(new Vector3Int(x, y, z)))
                    {
                        lightGrid[x, y, z] = lightSourceIntensity;
                        continue;
                    }
                    
                    // For non-light solid voxels, get light from adjacent empty space
                    float maxAdjacentLight = 0f;
                    
                    // Check all 6 directions for empty neighbors that have light
                    if (x > 0 && !finalGrid[x-1, y, z])
                        maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGrid[x-1, y, z]);
                    
                    if (x < gridSize.x - 1 && !finalGrid[x+1, y, z])
                        maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGrid[x+1, y, z]);
                    
                    if (y > 0 && !finalGrid[x, y-1, z])
                        maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGrid[x, y-1, z]);
                    
                    if (y < gridSize.y - 1 && !finalGrid[x, y+1, z])
                        maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGrid[x, y+1, z]);
                    
                    if (z > 0 && !finalGrid[x, y, z-1])
                        maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGrid[x, y, z-1]);
                    
                    if (z < gridSize.z - 1 && !finalGrid[x, y, z+1])
                        maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGrid[x, y, z+1]);
                    
                    // Solid voxels get attenuated light from neighbors
                    // Walls get less light than the air next to them
                    lightGrid[x, y, z] = Mathf.Max(lightGrid[x, y, z], maxAdjacentLight * 0.7f);
                }
            }
        }
        
        // FOURTH: Smooth lighting for better visual quality
        SmoothLighting();
    }

    private void SmoothLighting()
    {
        // Simple 3x3x3 box blur for smoother transitions
        float[,,] smoothed = new float[gridSize.x, gridSize.y, gridSize.z];
        
        for (int x = 0; x < gridSize.x; x++)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                for (int z = 0; z < gridSize.z; z++)
                {
                    float sum = 0;
                    int count = 0;
                    
                    // 3x3x3 neighborhood
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
                                    nz >= 0 && nz < gridSize.z)
                                {
                                    // Only include voxels of the same type in smoothing
                                    // (solid with solid, empty with empty)
                                    if (finalGrid[nx, ny, nz] == finalGrid[x, y, z])
                                    {
                                        sum += lightGrid[nx, ny, nz];
                                        count++;
                                    }
                                }
                            }
                        }
                    }
                    
                    if (count > 0)
                        smoothed[x, y, z] = sum / count;
                    else
                        smoothed[x, y, z] = lightGrid[x, y, z];
                }
            }
        }
        
        // Copy back with some original preservation
        for (int x = 0; x < gridSize.x; x++)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                for (int z = 0; z < gridSize.z; z++)
                {
                    // Blend original and smoothed (70% smoothed, 30% original)
                    lightGrid[x, y, z] = smoothed[x, y, z] * 0.7f + lightGrid[x, y, z] * 0.3f;
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

    private void GenerateLitMesh()
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector2> uv = new List<Vector2>();
        List<Color> colors = new List<Color>();
        List<Vector3> normals = new List<Vector3>();
        
        // Generate mesh with lighting data
        for (int x = 0; x < gridSize.x; x++)
        {
            for (int y = 0; y < gridSize.y; y++)
            {
                for (int z = 0; z < gridSize.z; z++)
                {
                    if (finalGrid[x, y, z])
                    {
                        AddFacesWithLightingData(x, y, z, vertices, triangles, uv, colors, normals);
                    }
                }
            }
        }
        
        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.uv = uv.ToArray();
        mesh.colors = colors.ToArray();
        mesh.normals = normals.ToArray();
        mesh.RecalculateBounds();
        mesh.Optimize();
        
        meshFilter.mesh = mesh;
        
        Debug.Log($"Generated mesh: {vertices.Count} vertices, {triangles.Count/3} triangles, {lightPositions.Count} lights");
    }

    private void AddFacesWithLightingData(int x, int y, int z, List<Vector3> vertices, List<int> triangles, 
                                          List<Vector2> uv, List<Color> colors, List<Vector3> normals)
    {
        Vector3 offset = new Vector3(x, y, z);
        
        // Get the light level for this voxel (0-1)
        float voxelLight = lightGrid[x, y, z];
        
        // Check if this voxel is a light source
        bool isLight = lightPositionSet.Contains(new Vector3Int(x, y, z));
        
        // LEFT FACE
        if (x == 0 || !finalGrid[x - 1, y, z])
        {
            byte materialID = MATERIAL_WALL;
            if (isLight) materialID = MATERIAL_LIGHT;
            AddFaceWithVoxelLighting(offset, 
                new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(0,0,1),
                vertices, triangles, uv, colors, normals, materialID, false, voxelLight);
        }
        
        // RIGHT FACE
        if (x == gridSize.x - 1 || !finalGrid[x + 1, y, z])
        {
            byte materialID = MATERIAL_WALL;
            if (isLight) materialID = MATERIAL_LIGHT;
            AddFaceWithVoxelLighting(offset, 
                new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(1,0,0),
                vertices, triangles, uv, colors, normals, materialID, false, voxelLight);
        }
        
        // BOTTOM FACE - FLOOR
        if (y == 0 || !finalGrid[x, y - 1, z])
        {
            byte materialID = MATERIAL_FLOOR;
            if (isLight) materialID = MATERIAL_LIGHT;
            
            AddFaceWithVoxelLighting(offset, 
                new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0),
                vertices, triangles, uv, colors, normals, materialID, true, voxelLight);
        }
        
        // TOP FACE - CEILING
        if (y == gridSize.y - 1 || !finalGrid[x, y + 1, z])
        {
            byte materialID = MATERIAL_CEILING;
            if (isLight) materialID = MATERIAL_LIGHT;
            
            AddFaceWithVoxelLighting(offset, 
                new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1),
                vertices, triangles, uv, colors, normals, materialID, true, voxelLight);
        }
        
        // FRONT FACE
        if (z == 0 || !finalGrid[x, y, z - 1])
        {
            byte materialID = MATERIAL_WALL;
            if (isLight) materialID = MATERIAL_LIGHT;
            AddFaceWithVoxelLighting(offset, 
                new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0),
                vertices, triangles, uv, colors, normals, materialID, false, voxelLight);
        }
        
        // BACK FACE
        if (z == gridSize.z - 1 || !finalGrid[x, y, z + 1])
        {
            byte materialID = MATERIAL_WALL;
            if (isLight) materialID = MATERIAL_LIGHT;
            AddFaceWithVoxelLighting(offset, 
                new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1),
                vertices, triangles, uv, colors, normals, materialID, false, voxelLight);
        }
    }

    private void AddFaceWithVoxelLighting(Vector3 offset, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
                                       List<Vector3> vertices, List<int> triangles, List<Vector2> uvList, 
                                       List<Color> colors, List<Vector3> normals, byte materialID, 
                                       bool isHorizontal, float voxelLight)
    {
        int baseIndex = vertices.Count;
        
        // Add vertices
        vertices.Add(v0 + offset);
        vertices.Add(v1 + offset);
        vertices.Add(v2 + offset);
        vertices.Add(v3 + offset);
        
        // Add triangles
        triangles.Add(baseIndex);
        triangles.Add(baseIndex + 1);
        triangles.Add(baseIndex + 2);
        triangles.Add(baseIndex + 2);
        triangles.Add(baseIndex + 3);
        triangles.Add(baseIndex);
        
        // Add UVs
        float width = 1f;
        float height = 1f;
        
        if (isHorizontal)
        {
            width = Vector3.Distance(v0, v3);
            height = Vector3.Distance(v0, v1);
            uvList.Add(new Vector2(0, 0));
            uvList.Add(new Vector2(0, height * textureScale.y));
            uvList.Add(new Vector2(width * textureScale.x, height * textureScale.y));
            uvList.Add(new Vector2(width * textureScale.x, 0));
        }
        else
        {
            width = Vector3.Distance(v0, v1);
            height = Vector3.Distance(v0, v3);
            uvList.Add(new Vector2(0, 0));
            uvList.Add(new Vector2(0, height * textureScale.y));
            uvList.Add(new Vector2(width * textureScale.x, height * textureScale.y));
            uvList.Add(new Vector2(width * textureScale.x, 0));
        }
        
        // Calculate face normal
        Vector3 normal = Vector3.Cross(v1 - v0, v2 - v1).normalized;
        if (isHorizontal && normal.y < 0) normal = -normal;
        
        // Add normals
        for (int i = 0; i < 4; i++)
        {
            normals.Add(normal);
        }
        
        // Add vertex colors
        // Red channel = material ID (normalized 0-1)
        // Green channel = voxel light level (0-1)
        // Blue/Alpha = unused
        Color vertexColor = new Color(
            materialID / 3f,    // Red: Material ID (normalized to 0-1, max ID = 3)
            voxelLight,         // Green: Voxel light level
            0f,                 // Blue: Unused
            1f                  // Alpha: Full opacity
        );
        
        for (int i = 0; i < 4; i++)
        {
            colors.Add(vertexColor);
        }
    }

    [ContextMenu("Generate New")]
    private void GenerateNew()
    {
        // Clean up old mesh
        if (meshFilter != null && meshFilter.mesh != null)
        {
            DestroyImmediate(meshFilter.mesh);
        }
        
        Generate();
    }
}