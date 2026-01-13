using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using System.Collections.Concurrent;

public class DungeonChunk : MonoBehaviour
{
    [Header("Lighting Settings")]
    [SerializeField] private bool enableLighting = true;
    [SerializeField] private float lightPlacementChance = 0.2f;
    [SerializeField] private int maxLightsPerChunk = 8;
    [SerializeField] private int maxLightLevel = 15;
    [SerializeField] private int minLightLevel = 0;
    
    [Header("Performance")]
    [SerializeField] private bool useMeshCache = true;
    [SerializeField] private bool skipEmptyChunks = true;
    [SerializeField] private bool smoothLighting = false; // Keep false for performance
    
    [Header("Textures")]
    [SerializeField] private Texture2D wallTexture;
    [SerializeField] private Texture2D floorTexture;
    [SerializeField] private Texture2D ceilingTexture;
    [SerializeField] private Vector2 textureScale = Vector2.one;
    
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    
    private NativeArray<byte> voxelData;
    private byte[,,] lightMap;
    private Vector3Int chunkSize;
    private List<Vector3Int> lightPositions = new List<Vector3Int>();
    
    // Reference to the chunk manager for cross-chunk checks
    private InfiniteChunkManager chunkManager;
    private Vector3Int chunkCoord;
    private int worldSeed;
    
    // Material IDs (must match shader)
    private const byte MATERIAL_WALL = 0;
    private const byte MATERIAL_FLOOR = 1;
    private const byte MATERIAL_CEILING = 2;
    private const byte MATERIAL_LIGHT = 3;
    
    // Optimized arrays for faster access
    private byte[] voxelDataArray;
    private Queue<Vector3Int> lightBfsQueue = new Queue<Vector3Int>();
    
    // Cache
    private static ConcurrentDictionary<int, Mesh> meshCache = new ConcurrentDictionary<int, Mesh>();
    private int lastVoxelHash = 0;
    
    public void Initialize(Vector3Int size)
    {
        chunkSize = size;
        
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshCollider = GetComponent<MeshCollider>();
        
        if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();
        if (meshCollider == null) meshCollider = gameObject.AddComponent<MeshCollider>();
    }
    
    public void SetChunkCoord(Vector3Int coord, int seed)
    {
        chunkCoord = coord;
        worldSeed = seed;
    }
    
    public Vector3Int GetChunkCoord() => chunkCoord;
    
    public Vector3 GetChunkWorldPosition() => transform.position;
    
    public void GenerateMesh(NativeArray<byte> voxelData)
    {
        this.voxelData = voxelData;
        
        // Convert to managed array
        int voxelCount = chunkSize.x * chunkSize.y * chunkSize.z;
        voxelDataArray = new byte[voxelCount];
        voxelData.CopyTo(voxelDataArray);
        
        // Skip empty chunks
        if (skipEmptyChunks && IsChunkEmpty())
        {
            ClearMesh();
            return;
        }
        
        // Check cache first
        int voxelHash = useMeshCache ? CalculateVoxelHash() : 0;
        
        if (useMeshCache && voxelHash == lastVoxelHash && meshFilter.mesh != null)
        {
            return; // Already have correct mesh
        }
        
        if (useMeshCache && meshCache.TryGetValue(voxelHash, out Mesh cachedMesh))
        {
            meshFilter.mesh = cachedMesh;
            if (meshCollider != null) meshCollider.sharedMesh = cachedMesh;
            lastVoxelHash = voxelHash;
            return;
        }
        
        try
        {
            if (enableLighting)
            {
                PlaceLights();
                CalculateLightingBFS();
            }
            
            Mesh mesh = new Mesh();
            
            if (smoothLighting)
            {
                GenerateSmoothLitMesh(mesh);
            }
            else
            {
                GenerateFlatLitMesh(mesh);
            }
            
            // Cache the mesh
            if (useMeshCache)
            {
                meshCache[voxelHash] = mesh;
                lastVoxelHash = voxelHash;
            }
            
            meshFilter.mesh = mesh;
            if (meshCollider != null) meshCollider.sharedMesh = mesh;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error generating mesh for chunk {chunkCoord}: {e.Message}");
        }
    }
    
    private bool IsChunkEmpty()
    {
        for (int i = 0; i < voxelDataArray.Length; i++)
        {
            if (voxelDataArray[i] != 0)
                return false;
        }
        return true;
    }
    
    private int CalculateVoxelHash()
    {
        // Simple but fast hash for caching
        unchecked
        {
            int hash = 17;
            int step = Mathf.Max(1, voxelDataArray.Length / 100);
            
            for (int i = 0; i < voxelDataArray.Length; i += step)
            {
                hash = hash * 31 + voxelDataArray[i];
            }
            
            // Include lighting state in hash
            hash = hash * 31 + (enableLighting ? 1 : 0);
            hash = hash * 31 + (smoothLighting ? 1 : 0);
            
            return hash;
        }
    }
    
    private void PlaceLights()
    {
        lightPositions.Clear();
        
        if (!enableLighting) return;
        
        int chunkSeed = GetChunkSeed();
        System.Random deterministicRandom = new System.Random(chunkSeed);
        
        // Sparse sampling for 16x16x16 chunks
        int sampleStep = 2;
        
        for (int x = 0; x < chunkSize.x; x += sampleStep)
        {
            for (int y = 1; y < chunkSize.y - 1; y += sampleStep)
            {
                for (int z = 0; z < chunkSize.z; z += sampleStep)
                {
                    if (!GetVoxel(x, y, z)) continue;
                    
                    // Check if this is a ceiling voxel (air above, solid below)
                    bool isCeiling = !GetVoxel(x, y + 1, z) && GetVoxel(x, y - 1, z);
                    
                    if (isCeiling)
                    {
                        int positionHash = GetPositionHash(x, y, z);
                        float chance = (positionHash % 1000) / 1000f;
                        
                        if (chance < lightPlacementChance && lightPositions.Count < maxLightsPerChunk)
                        {
                            lightPositions.Add(new Vector3Int(x, y, z));
                        }
                    }
                }
            }
        }
        
        // Ensure at least one light if there are rooms
        if (lightPositions.Count == 0 && HasRooms())
        {
            // Find center ceiling position
            int centerX = chunkSize.x / 2;
            int centerZ = chunkSize.z / 2;
            
            for (int y = chunkSize.y - 1; y >= 0; y--)
            {
                if (GetVoxel(centerX, y, centerZ) && y < chunkSize.y - 1 && !GetVoxel(centerX, y + 1, centerZ))
                {
                    lightPositions.Add(new Vector3Int(centerX, y, centerZ));
                    break;
                }
            }
        }
    }
    
    private bool HasRooms()
    {
        // Quick check for solid blocks (potential rooms)
        int solidCount = 0;
        for (int i = 0; i < voxelDataArray.Length; i++)
        {
            if (voxelDataArray[i] != 0)
                solidCount++;
        }
        return solidCount > 10; // Arbitrary threshold
    }
    
    private void CalculateLightingBFS()
    {
        if (!enableLighting) return;
        
        // Initialize light map
        if (lightMap == null || 
            lightMap.GetLength(0) != chunkSize.x ||
            lightMap.GetLength(1) != chunkSize.y ||
            lightMap.GetLength(2) != chunkSize.z)
        {
            lightMap = new byte[chunkSize.x, chunkSize.y, chunkSize.z];
        }
        else
        {
            // Fast clear
            System.Array.Clear(lightMap, 0, lightMap.Length);
        }
        
        lightBfsQueue.Clear();
        
        // Initialize light sources
        foreach (var lightPos in lightPositions)
        {
            lightMap[lightPos.x, lightPos.y, lightPos.z] = (byte)maxLightLevel;
            lightBfsQueue.Enqueue(lightPos);
        }
        
        // BFS propagation (like Minecraft)
        Vector3Int[] directions = {
            Vector3Int.right, Vector3Int.left,
            Vector3Int.up, Vector3Int.down,
            Vector3Int.forward, Vector3Int.back
        };
        
        while (lightBfsQueue.Count > 0)
        {
            Vector3Int current = lightBfsQueue.Dequeue();
            byte currentLight = lightMap[current.x, current.y, current.z];
            
            if (currentLight <= minLightLevel) continue;
            
            byte nextLight = (byte)(currentLight - 1);
            
            foreach (var dir in directions)
            {
                Vector3Int neighbor = current + dir;
                
                if (!IsInGrid(neighbor)) continue;
                
                // Skip solid blocks (they don't propagate light)
                if (GetVoxel(neighbor.x, neighbor.y, neighbor.z)) continue;
                
                byte neighborLight = lightMap[neighbor.x, neighbor.y, neighbor.z];
                
                if (neighborLight < nextLight)
                {
                    lightMap[neighbor.x, neighbor.y, neighbor.z] = nextLight;
                    
                    if (nextLight > minLightLevel)
                    {
                        lightBfsQueue.Enqueue(neighbor);
                    }
                }
            }
        }
    }
    
    private void GenerateFlatLitMesh(Mesh mesh)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector2> uv = new List<Vector2>();
        List<Color> colors = new List<Color>();
        List<Vector3> normals = new List<Vector3>();
        
        // Pre-allocate with reasonable capacity
        int estimatedFaces = chunkSize.x * chunkSize.y * 2;
        vertices.Capacity = estimatedFaces * 4;
        triangles.Capacity = estimatedFaces * 6;
        
        // Generate faces with per-face lighting
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    if (!GetVoxel(x, y, z)) continue;
                    
                    Vector3 offset = new Vector3(x, y, z);
                    
                    // Check if this is a light source
                    bool isLightSource = false;
                    foreach (var lightPos in lightPositions)
                    {
                        if (lightPos.x == x && lightPos.y == y && lightPos.z == z)
                        {
                            isLightSource = true;
                            break;
                        }
                    }
                    
                    byte materialID = GetMaterialID(x, y, z, isLightSource);
                    
                    // Generate faces only if adjacent voxel is empty
                    if (ShouldGenerateFace(x, y, z, Vector3Int.left))
                    {
                        float faceLight = GetFaceLightLevel(x, y, z, Vector3Int.left);
                        AddFace(offset, 
                            new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(0,0,1),
                            vertices, triangles, uv, colors, normals, 
                            materialID, false, faceLight);
                    }
                    
                    if (ShouldGenerateFace(x, y, z, Vector3Int.right))
                    {
                        float faceLight = GetFaceLightLevel(x, y, z, Vector3Int.right);
                        AddFace(offset, 
                            new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(1,0,0),
                            vertices, triangles, uv, colors, normals, 
                            materialID, false, faceLight);
                    }
                    
                    if (ShouldGenerateFace(x, y, z, Vector3Int.down))
                    {
                        float faceLight = GetFaceLightLevel(x, y, z, Vector3Int.down);
                        AddFace(offset, 
                            new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0),
                            vertices, triangles, uv, colors, normals, 
                            materialID, true, faceLight);
                    }
                    
                    if (ShouldGenerateFace(x, y, z, Vector3Int.up))
                    {
                        float faceLight = GetFaceLightLevel(x, y, z, Vector3Int.up);
                        AddFace(offset, 
                            new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1),
                            vertices, triangles, uv, colors, normals, 
                            materialID, true, faceLight);
                    }
                    
                    if (ShouldGenerateFace(x, y, z, Vector3Int.back))
                    {
                        float faceLight = GetFaceLightLevel(x, y, z, Vector3Int.back);
                        AddFace(offset, 
                            new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0),
                            vertices, triangles, uv, colors, normals, 
                            materialID, false, faceLight);
                    }
                    
                    if (ShouldGenerateFace(x, y, z, Vector3Int.forward))
                    {
                        float faceLight = GetFaceLightLevel(x, y, z, Vector3Int.forward);
                        AddFace(offset, 
                            new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1),
                            vertices, triangles, uv, colors, normals, 
                            materialID, false, faceLight);
                    }
                }
            }
        }
        
        // Apply to mesh
        if (vertices.Count == 0)
        {
            ClearMesh();
            return;
        }
        
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.uv = uv.ToArray();
        mesh.colors = colors.ToArray();
        mesh.normals = normals.ToArray();
        mesh.RecalculateBounds();
        
        if (mesh.vertexCount > 65535)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
    }
    
    private void GenerateSmoothLitMesh(Mesh mesh)
    {
        // For now, just use flat lighting for performance
        GenerateFlatLitMesh(mesh);
    }
    
    private byte GetMaterialID(int x, int y, int z, bool isLightSource)
    {
        if (isLightSource) return MATERIAL_LIGHT;
        
        // Determine material based on face orientation (simplified)
        if (y == 0 || !GetVoxel(x, y - 1, z)) return MATERIAL_FLOOR; // Bottom face
        if (y == chunkSize.y - 1 || !GetVoxel(x, y + 1, z)) return MATERIAL_CEILING; // Top face
        return MATERIAL_WALL; // Everything else
    }
    
    private bool ShouldGenerateFace(int x, int y, int z, Vector3Int direction)
    {
        Vector3Int adjPos = new Vector3Int(x, y, z) + direction;
        
        if (IsInGrid(adjPos))
        {
            return !GetVoxel(adjPos.x, adjPos.y, adjPos.z);
        }
        else
        {
            // At chunk boundary - check adjacent chunk if possible
            return !IsSolidInAdjacentChunk(x, y, z, direction);
        }
    }
    
    private float GetFaceLightLevel(int x, int y, int z, Vector3Int faceNormal)
    {
        if (!enableLighting) return 1.0f;
        
        Vector3Int adjPos = new Vector3Int(x, y, z) + faceNormal;
        
        if (IsInGrid(adjPos) && !GetVoxel(adjPos.x, adjPos.y, adjPos.z))
        {
            // Air block adjacent - use its light
            return lightMap[adjPos.x, adjPos.y, adjPos.z] / (float)maxLightLevel;
        }
        
        // Inside solid or at boundary - use minimal ambient light
        return 0.1f;
    }
    
    private bool IsSolidInAdjacentChunk(int x, int y, int z, Vector3Int direction)
    {
        if (chunkManager == null) 
        {
            chunkManager = FindObjectOfType<InfiniteChunkManager>();
            if (chunkManager == null) return false;
        }
        
        // Calculate adjacent chunk coordinate
        Vector3Int adjacentChunkCoord = chunkCoord;
        Vector3Int localPosInAdjacentChunk = new Vector3Int(x, y, z);
        
        // Adjust based on direction
        if (direction.x == -1 && x == 0) // Left boundary
        {
            adjacentChunkCoord += Vector3Int.left;
            localPosInAdjacentChunk.x = chunkSize.x - 1;
        }
        else if (direction.x == 1 && x == chunkSize.x - 1) // Right boundary
        {
            adjacentChunkCoord += Vector3Int.right;
            localPosInAdjacentChunk.x = 0;
        }
        else if (direction.y == -1 && y == 0) // Bottom boundary
        {
            adjacentChunkCoord += Vector3Int.down;
            localPosInAdjacentChunk.y = chunkSize.y - 1;
        }
        else if (direction.y == 1 && y == chunkSize.y - 1) // Top boundary
        {
            adjacentChunkCoord += Vector3Int.up;
            localPosInAdjacentChunk.y = 0;
        }
        else if (direction.z == -1 && z == 0) // Front boundary
        {
            adjacentChunkCoord += Vector3Int.back;
            localPosInAdjacentChunk.z = chunkSize.z - 1;
        }
        else if (direction.z == 1 && z == chunkSize.z - 1) // Back boundary
        {
            adjacentChunkCoord += Vector3Int.forward;
            localPosInAdjacentChunk.z = 0;
        }
        else
        {
            return false;
        }
        
        // Try to get voxel data from adjacent chunk
        if (chunkManager.TryGetVoxelData(adjacentChunkCoord, localPosInAdjacentChunk, out bool isSolid))
        {
            return isSolid;
        }
        
        return false; // Assume not solid if chunk not loaded
    }
    
    private void AddFace(Vector3 offset, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
                        List<Vector3> vertices, List<int> triangles, List<Vector2> uv, 
                        List<Color> colors, List<Vector3> normals,
                        byte materialID, bool isHorizontal, float faceLight)
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
        if (isHorizontal)
        {
            uv.Add(new Vector2(0, 0));
            uv.Add(new Vector2(0, textureScale.y));
            uv.Add(new Vector2(textureScale.x, textureScale.y));
            uv.Add(new Vector2(textureScale.x, 0));
        }
        else
        {
            uv.Add(new Vector2(0, 0));
            uv.Add(new Vector2(0, textureScale.y));
            uv.Add(new Vector2(textureScale.x, textureScale.y));
            uv.Add(new Vector2(textureScale.x, 0));
        }
        
        // Calculate normal
        Vector3 normal = Vector3.Cross(v1 - v0, v2 - v1).normalized;
        
        // Add normals (all same for flat shading)
        for (int i = 0; i < 4; i++)
        {
            normals.Add(normal);
        }
        
        // Add colors with lighting
        Color color = new Color(materialID / 3f, faceLight, 0, 1);
        colors.Add(color);
        colors.Add(color);
        colors.Add(color);
        colors.Add(color);
    }
    
    private void ClearMesh()
    {
        if (meshFilter != null && meshFilter.mesh != null)
            Destroy(meshFilter.mesh);
        if (meshCollider != null)
            meshCollider.sharedMesh = null;
    }
    
    // Helper methods
    private bool IsInGrid(Vector3Int pos)
    {
        return pos.x >= 0 && pos.x < chunkSize.x &&
               pos.y >= 0 && pos.y < chunkSize.y &&
               pos.z >= 0 && pos.z < chunkSize.z;
    }
    
    private bool GetVoxel(int x, int y, int z)
    {
        if (x < 0 || x >= chunkSize.x || y < 0 || y >= chunkSize.y || z < 0 || z >= chunkSize.z)
            return false;
        
        return voxelDataArray[CoordToIndex(x, y, z)] != 0;
    }
    
    private int CoordToIndex(int x, int y, int z)
    {
        return x * chunkSize.y * chunkSize.z + y * chunkSize.z + z;
    }
    
    private Vector3Int IndexToCoord(int index)
    {
        int z = index % chunkSize.z;
        int y = (index / chunkSize.z) % chunkSize.y;
        int x = index / (chunkSize.y * chunkSize.z);
        return new Vector3Int(x, y, z);
    }
    
    private int GetChunkSeed()
    {
        return worldSeed ^ (chunkCoord.x * 73856093) ^ (chunkCoord.y * 19349663) ^ (chunkCoord.z * 83492791);
    }
    
    private int GetPositionHash(int x, int y, int z)
    {
        int hash = 17;
        hash = hash * 31 + worldSeed;
        hash = hash * 31 + chunkCoord.x;
        hash = hash * 31 + chunkCoord.y;
        hash = hash * 31 + chunkCoord.z;
        hash = hash * 31 + x;
        hash = hash * 31 + y;
        hash = hash * 31 + z;
        return hash;
    }
    
    public void Clear()
    {
        ClearMesh();
        lightPositions.Clear();
        voxelDataArray = null;
        lightMap = null;
    }
    
    public void UpdateBoundaryMeshes()
    {
        if (voxelData.IsCreated)
        {
            GenerateMesh(voxelData);
        }
    }
}