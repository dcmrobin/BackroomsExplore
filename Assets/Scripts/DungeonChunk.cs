using UnityEngine;
using System.Collections.Generic;

public class DungeonChunk : MonoBehaviour
{
    [Header("Lighting Settings")]
    [SerializeField] private float lightPlacementChance = 0.2f;
    [SerializeField] private int minLightsPerRoom = 1;
    [SerializeField] private int maxLightsPerRoom = 3;
    [SerializeField] private float lightDecay = 0.15f;
    [SerializeField] private int lightPropagationSteps = 15;
    [SerializeField] private float lightSourceIntensity = 1.0f;
    [SerializeField] private bool smoothLighting = true;
    
    [Header("Textures")]
    [SerializeField] private Texture2D wallTexture;
    [SerializeField] private Texture2D floorTexture;
    [SerializeField] private Texture2D ceilingTexture;
    [SerializeField] private Vector2 textureScale = Vector2.one;
    
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    
    private bool[,,] voxelGrid;
    private float[,,] lightGrid;
    private Vector3Int chunkSize;
    private List<Vector3Int> lightPositions = new List<Vector3Int>();
    
    // Reference to the chunk manager for cross-chunk checks
    private InfiniteChunkManager chunkManager;
    private Vector3Int chunkCoord;
    
    // Material IDs (must match shader)
    private const byte MATERIAL_WALL = 0;
    private const byte MATERIAL_FLOOR = 1;
    private const byte MATERIAL_CEILING = 2;
    private const byte MATERIAL_LIGHT = 3;
    
    public void Initialize(Vector3Int size)
    {
        chunkSize = size;
        
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshCollider = GetComponent<MeshCollider>();
        
        if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();
        if (meshCollider == null) meshCollider = gameObject.AddComponent<MeshCollider>();
        
        // Initialize light grid
        lightGrid = new float[size.x, size.y, size.z];
        
        // Get reference to chunk manager
        chunkManager = FindObjectOfType<InfiniteChunkManager>();
    }
    
    public void SetChunkCoord(Vector3Int coord)
    {
        chunkCoord = coord;
    }
    
    public void GenerateMesh(bool[,,] grid)
    {
        voxelGrid = grid;
        
        // Place lights in the chunk
        PlaceLights();
        
        // Calculate lighting
        CalculateVoxelLighting();
        
        // Generate mesh with lighting data
        var meshData = GenerateLitMesh();
        ApplyMesh(meshData);
    }
    
    private void PlaceLights()
    {
        lightPositions.Clear();
        
        // Find rooms (solid areas) and place lights
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    if (voxelGrid[x, y, z])
                    {
                        // Check if this is a ceiling voxel (solid below, empty above or at top)
                        bool isCeiling = false;
                        if (y == chunkSize.y - 1 || !voxelGrid[x, y + 1, z])
                        {
                            // Make sure there's floor below
                            if (y > 0 && voxelGrid[x, y - 1, z])
                            {
                                isCeiling = true;
                            }
                        }
                        
                        if (isCeiling && Random.value < lightPlacementChance)
                        {
                            lightPositions.Add(new Vector3Int(x, y, z));
                        }
                    }
                }
            }
        }
    }
    
    private void CalculateVoxelLighting()
    {
        // Initialize light grid
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    lightGrid[x, y, z] = 0f;
                }
            }
        }
        
        // Set initial light values for light sources
        foreach (var lightPos in lightPositions)
        {
            lightGrid[lightPos.x, lightPos.y, lightPos.z] = lightSourceIntensity;
            
            // Light radiates from source into adjacent empty space
            Vector3Int[] directions = {
                Vector3Int.right, Vector3Int.left,
                Vector3Int.up, Vector3Int.down,
                Vector3Int.forward, Vector3Int.back
            };
            
            foreach (var dir in directions)
            {
                Vector3Int neighbor = lightPos + dir;
                if (IsInGrid(neighbor) && !voxelGrid[neighbor.x, neighbor.y, neighbor.z])
                {
                    lightGrid[neighbor.x, neighbor.y, neighbor.z] = 
                        Mathf.Max(lightGrid[neighbor.x, neighbor.y, neighbor.z], 
                        lightSourceIntensity - lightDecay * 0.5f);
                }
            }
        }
        
        // Propagate light through empty space
        for (int step = 0; step < lightPropagationSteps; step++)
        {
            float[,,] newLightGrid = (float[,,])lightGrid.Clone();
            
            for (int x = 0; x < chunkSize.x; x++)
            {
                for (int y = 0; y < chunkSize.y; y++)
                {
                    for (int z = 0; z < chunkSize.z; z++)
                    {
                        if (voxelGrid[x, y, z]) continue;
                        
                        float maxNeighborLight = 0f;
                        
                        if (x > 0 && !voxelGrid[x-1, y, z])
                            maxNeighborLight = Mathf.Max(maxNeighborLight, lightGrid[x-1, y, z]);
                        
                        if (x < chunkSize.x - 1 && !voxelGrid[x+1, y, z])
                            maxNeighborLight = Mathf.Max(maxNeighborLight, lightGrid[x+1, y, z]);
                        
                        if (y > 0 && !voxelGrid[x, y-1, z])
                            maxNeighborLight = Mathf.Max(maxNeighborLight, lightGrid[x, y-1, z]);
                        
                        if (y < chunkSize.y - 1 && !voxelGrid[x, y+1, z])
                            maxNeighborLight = Mathf.Max(maxNeighborLight, lightGrid[x, y+1, z]);
                        
                        if (z > 0 && !voxelGrid[x, y, z-1])
                            maxNeighborLight = Mathf.Max(maxNeighborLight, lightGrid[x, y, z-1]);
                        
                        if (z < chunkSize.z - 1 && !voxelGrid[x, y, z+1])
                            maxNeighborLight = Mathf.Max(maxNeighborLight, lightGrid[x, y, z+1]);
                        
                        float propagatedLight = Mathf.Max(0, maxNeighborLight - lightDecay);
                        newLightGrid[x, y, z] = Mathf.Max(newLightGrid[x, y, z], propagatedLight);
                    }
                }
            }
            
            lightGrid = newLightGrid;
        }
        
        // Solid voxels receive light from adjacent empty voxels
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    if (!voxelGrid[x, y, z]) continue;
                    
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
                    
                    if (isLightSource)
                    {
                        lightGrid[x, y, z] = lightSourceIntensity;
                        continue;
                    }
                    
                    float maxAdjacentLight = 0f;
                    
                    if (x > 0 && !voxelGrid[x-1, y, z])
                        maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGrid[x-1, y, z]);
                    
                    if (x < chunkSize.x - 1 && !voxelGrid[x+1, y, z])
                        maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGrid[x+1, y, z]);
                    
                    if (y > 0 && !voxelGrid[x, y-1, z])
                        maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGrid[x, y-1, z]);
                    
                    if (y < chunkSize.y - 1 && !voxelGrid[x, y+1, z])
                        maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGrid[x, y+1, z]);
                    
                    if (z > 0 && !voxelGrid[x, y, z-1])
                        maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGrid[x, y, z-1]);
                    
                    if (z < chunkSize.z - 1 && !voxelGrid[x, y, z+1])
                        maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGrid[x, y, z+1]);
                    
                    lightGrid[x, y, z] = Mathf.Max(lightGrid[x, y, z], maxAdjacentLight * 0.7f);
                }
            }
        }
        
        // Smooth lighting if enabled
        if (smoothLighting)
        {
            SmoothLighting();
        }
    }
    
    private void SmoothLighting()
    {
        float[,,] smoothed = new float[chunkSize.x, chunkSize.y, chunkSize.z];
        
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    float sum = 0;
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
                                
                                if (nx >= 0 && nx < chunkSize.x &&
                                    ny >= 0 && ny < chunkSize.y &&
                                    nz >= 0 && nz < chunkSize.z)
                                {
                                    if (voxelGrid[nx, ny, nz] == voxelGrid[x, y, z])
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
        
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    lightGrid[x, y, z] = smoothed[x, y, z] * 0.7f + lightGrid[x, y, z] * 0.3f;
                }
            }
        }
    }
    
    private MeshData GenerateLitMesh()
    {
        MeshData meshData = new MeshData();
        
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    if (voxelGrid[x, y, z])
                    {
                        AddFacesWithCrossChunkCulling(x, y, z, meshData);
                    }
                }
            }
        }
        
        return meshData;
    }
    
    private void AddFacesWithCrossChunkCulling(int x, int y, int z, MeshData meshData)
    {
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
        
        // LEFT FACE (Negative X)
        if (ShouldGenerateFace(x, y, z, Vector3Int.left))
        {
            byte materialID = isLightSource ? MATERIAL_LIGHT : MATERIAL_WALL;
            float faceLight = GetFaceLightLevel(x, y, z, Vector3Int.left);
            AddFace(offset, 
                new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(0,0,1),
                meshData, materialID, false, faceLight);
        }
        
        // RIGHT FACE (Positive X)
        if (ShouldGenerateFace(x, y, z, Vector3Int.right))
        {
            byte materialID = isLightSource ? MATERIAL_LIGHT : MATERIAL_WALL;
            float faceLight = GetFaceLightLevel(x, y, z, Vector3Int.right);
            AddFace(offset, 
                new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(1,0,0),
                meshData, materialID, false, faceLight);
        }
        
        // BOTTOM FACE (Negative Y) - FLOOR
        if (ShouldGenerateFace(x, y, z, Vector3Int.down))
        {
            byte materialID = isLightSource ? MATERIAL_LIGHT : MATERIAL_FLOOR;
            float faceLight = GetFaceLightLevel(x, y, z, Vector3Int.down);
            AddFace(offset, 
                new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0),
                meshData, materialID, true, faceLight);
        }
        
        // TOP FACE (Positive Y) - CEILING
        if (ShouldGenerateFace(x, y, z, Vector3Int.up))
        {
            byte materialID = isLightSource ? MATERIAL_LIGHT : MATERIAL_CEILING;
            float faceLight = GetFaceLightLevel(x, y, z, Vector3Int.up);
            AddFace(offset, 
                new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1),
                meshData, materialID, true, faceLight);
        }
        
        // FRONT FACE (Negative Z)
        if (ShouldGenerateFace(x, y, z, Vector3Int.back))
        {
            byte materialID = isLightSource ? MATERIAL_LIGHT : MATERIAL_WALL;
            float faceLight = GetFaceLightLevel(x, y, z, Vector3Int.back);
            AddFace(offset, 
                new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0),
                meshData, materialID, false, faceLight);
        }
        
        // BACK FACE (Positive Z)
        if (ShouldGenerateFace(x, y, z, Vector3Int.forward))
        {
            byte materialID = isLightSource ? MATERIAL_LIGHT : MATERIAL_WALL;
            float faceLight = GetFaceLightLevel(x, y, z, Vector3Int.forward);
            AddFace(offset, 
                new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1),
                meshData, materialID, false, faceLight);
        }
    }
    
    private bool ShouldGenerateFace(int x, int y, int z, Vector3Int direction)
    {
        // Check adjacent voxel in this chunk
        Vector3Int adjPos = new Vector3Int(x, y, z) + direction;
        
        if (IsInGrid(adjPos))
        {
            // If adjacent voxel is solid in this chunk, don't generate face
            return !voxelGrid[adjPos.x, adjPos.y, adjPos.z];
        }
        else
        {
            // This voxel is at a chunk boundary
            // Need to check adjacent chunk
            return !IsSolidInAdjacentChunk(x, y, z, direction);
        }
    }
    
    private bool IsSolidInAdjacentChunk(int x, int y, int z, Vector3Int direction)
    {
        if (chunkManager == null) 
        {
            chunkManager = FindObjectOfType<InfiniteChunkManager>();
            if (chunkManager == null) return false;
        }
        
        // Calculate which adjacent chunk we need to check
        Vector3Int adjacentChunkCoord = chunkCoord;
        Vector3Int localPosInAdjacentChunk = new Vector3Int(x, y, z);
        
        // Adjust chunk coordinate and local position based on direction
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
            // Not at boundary in this direction
            return false;
        }
        
        // Check if adjacent chunk is loaded
        if (chunkManager.TryGetVoxelData(adjacentChunkCoord, localPosInAdjacentChunk, out bool isSolid))
        {
            return isSolid;
        }
        
        // If adjacent chunk isn't loaded, we can't know for sure
        // Conservative approach: assume it's solid so we generate the face
        // This might create double walls at chunk boundaries until neighbor loads
        return false;
    }
    
    private float GetFaceLightLevel(int x, int y, int z, Vector3Int faceNormal)
    {
        // Get light from the empty space adjacent to this face
        Vector3Int adjPos = new Vector3Int(x, y, z) + faceNormal;
        
        if (IsInGrid(adjPos))
        {
            if (!voxelGrid[adjPos.x, adjPos.y, adjPos.z])
            {
                return lightGrid[adjPos.x, adjPos.y, adjPos.z];
            }
        }
        
        // If adjacent is solid or out of bounds, use this voxel's light
        return lightGrid[x, y, z];
    }
    
    private bool IsInGrid(Vector3Int pos)
    {
        return pos.x >= 0 && pos.x < chunkSize.x &&
               pos.y >= 0 && pos.y < chunkSize.y &&
               pos.z >= 0 && pos.z < chunkSize.z;
    }
    
    private void AddFace(Vector3 offset, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
                        MeshData meshData, byte materialID, bool isHorizontal, float faceLight)
    {
        int baseIndex = meshData.vertices.Count;
        
        // Add vertices
        meshData.vertices.Add(v0 + offset);
        meshData.vertices.Add(v1 + offset);
        meshData.vertices.Add(v2 + offset);
        meshData.vertices.Add(v3 + offset);
        
        // Add triangles
        meshData.triangles.Add(baseIndex);
        meshData.triangles.Add(baseIndex + 1);
        meshData.triangles.Add(baseIndex + 2);
        meshData.triangles.Add(baseIndex + 2);
        meshData.triangles.Add(baseIndex + 3);
        meshData.triangles.Add(baseIndex);
        
        // Add UVs with texture scaling
        float width = 1f;
        float height = 1f;
        
        if (isHorizontal)
        {
            width = Vector3.Distance(v0, v3);
            height = Vector3.Distance(v0, v1);
            meshData.uv.Add(new Vector2(0, 0));
            meshData.uv.Add(new Vector2(0, height * textureScale.y));
            meshData.uv.Add(new Vector2(width * textureScale.x, height * textureScale.y));
            meshData.uv.Add(new Vector2(width * textureScale.x, 0));
        }
        else
        {
            width = Vector3.Distance(v0, v1);
            height = Vector3.Distance(v0, v3);
            meshData.uv.Add(new Vector2(0, 0));
            meshData.uv.Add(new Vector2(0, height * textureScale.y));
            meshData.uv.Add(new Vector2(width * textureScale.x, height * textureScale.y));
            meshData.uv.Add(new Vector2(width * textureScale.x, 0));
        }
        
        // Calculate face normal
        Vector3 normal = Vector3.Cross(v1 - v0, v2 - v1).normalized;
        if (isHorizontal && normal.y < 0) normal = -normal;
        
        // Add normals
        for (int i = 0; i < 4; i++)
        {
            meshData.normals.Add(normal);
        }
        
        // Add colors (material ID in R, light level in G)
        Color vertexColor = new Color(materialID / 3f, faceLight, 0, 1);
        
        for (int i = 0; i < 4; i++)
        {
            meshData.colors.Add(vertexColor);
        }
    }
    
    private void ApplyMesh(MeshData meshData)
    {
        if (meshData == null || meshData.vertices.Count == 0) // FIXED: null check
        {
            if (meshFilter != null) meshFilter.mesh = null;
            if (meshCollider != null) meshCollider.sharedMesh = null;
            return;
        }
        
        try
        {
            Mesh mesh = new Mesh();
            
            if (meshData.vertices.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                
            mesh.vertices = meshData.vertices.ToArray();
            mesh.triangles = meshData.triangles.ToArray();
            mesh.uv = meshData.uv.ToArray();
            mesh.colors = meshData.colors.ToArray();
            mesh.normals = meshData.normals.ToArray();
            
            mesh.RecalculateBounds();
            
            // FIXED: Only optimize if not empty
            if (mesh.vertexCount > 0)
                mesh.Optimize();
            
            if (meshFilter != null) meshFilter.mesh = mesh;
            if (meshCollider != null) meshCollider.sharedMesh = mesh;
            
            if (meshRenderer != null && meshRenderer.material != null)
            {
                if (wallTexture != null)
                    meshRenderer.material.SetTexture("_WallTex", wallTexture);
                if (floorTexture != null)
                    meshRenderer.material.SetTexture("_FloorTex", floorTexture);
                if (ceilingTexture != null)
                    meshRenderer.material.SetTexture("_CeilingTex", ceilingTexture);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error applying mesh: {e.Message}");
        }
    }
    
    public void Clear()
    {
        if (meshFilter != null && meshFilter.mesh != null)
            Destroy(meshFilter.mesh);
        if (meshCollider != null && meshCollider.sharedMesh != null)
            meshCollider.sharedMesh = null;
            
        voxelGrid = null;
        lightGrid = null;
        lightPositions.Clear();
    }
    
    private class MeshData
    {
        public List<Vector3> vertices = new List<Vector3>();
        public List<int> triangles = new List<int>();
        public List<Vector2> uv = new List<Vector2>();
        public List<Color> colors = new List<Color>();
        public List<Vector3> normals = new List<Vector3>();
    }
}