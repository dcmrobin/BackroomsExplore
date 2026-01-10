using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;

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
    private int worldSeed;
    
    // Material IDs (must match shader)
    private const byte MATERIAL_WALL = 0;
    private const byte MATERIAL_FLOOR = 1;
    private const byte MATERIAL_CEILING = 2;
    private const byte MATERIAL_LIGHT = 3;
    
    // Optimized arrays for faster access
    private bool[] voxelGridFlat;
    private float[] lightGridFlat;
    
    public void Initialize(Vector3Int size)
    {
        chunkSize = size;
        
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshCollider = GetComponent<MeshCollider>();
        
        if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();
        if (meshCollider == null) meshCollider = gameObject.AddComponent<MeshCollider>();
        
        // Initialize flat arrays for faster access
        int voxelCount = size.x * size.y * size.z;
        voxelGridFlat = new bool[voxelCount];
        lightGridFlat = new float[voxelCount];
    }
    
    public void SetChunkCoord(Vector3Int coord, int seed)
    {
        chunkCoord = coord;
        worldSeed = seed;
    }
    
    public Vector3Int GetChunkCoord() => chunkCoord;
    
    public Vector3 GetChunkWorldPosition() => transform.position;
    
    public void GenerateMesh(bool[,,] grid)
    {
        voxelGrid = grid;
        
        // Convert to flat array for performance
        ConvertToFlatArray(grid);
        
        // Place lights
        PlaceLights();
        
        // Calculate lighting (optimized)
        CalculateVoxelLightingOptimized();
        
        // Generate mesh with lighting data
        GenerateLitMeshOptimized();
    }
    
    private void ConvertToFlatArray(bool[,,] grid)
    {
        int index = 0;
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    voxelGridFlat[index++] = grid[x, y, z];
                }
            }
        }
    }
    
    private void ConvertFromFlatArray()
    {
        int index = 0;
        lightGrid = new float[chunkSize.x, chunkSize.y, chunkSize.z];
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    lightGrid[x, y, z] = lightGridFlat[index++];
                }
            }
        }
    }
    
    private void PlaceLights()
    {
        lightPositions.Clear();
        
        // Create a deterministic random based on world seed and chunk coordinates
        int chunkSeed = GetChunkSeed();
        System.Random deterministicRandom = new System.Random(chunkSeed);
        
        int totalVoxels = chunkSize.x * chunkSize.y * chunkSize.z;
        
        // Pre-calculate indices for performance
        for (int i = 0; i < totalVoxels; i++)
        {
            if (voxelGridFlat[i])
            {
                Vector3Int coord = IndexToCoord(i);
                int x = coord.x;
                int y = coord.y;
                int z = coord.z;
                
                // Check if this is a ceiling voxel (solid below, empty above or at top)
                bool isCeiling = false;
                if (y == chunkSize.y - 1 || !GetVoxel(x, y + 1, z))
                {
                    // Make sure there's floor below
                    if (y > 0 && GetVoxel(x, y - 1, z))
                    {
                        isCeiling = true;
                    }
                }
                
                if (isCeiling)
                {
                    // Create a deterministic hash for this specific position
                    int positionHash = GetPositionHash(x, y, z);
                    
                    // Use the hash to create a deterministic random generator for this position
                    System.Random positionRandom = new System.Random(positionHash);
                    
                    // Get deterministic value between 0 and 1
                    float deterministicValue = (float)positionRandom.NextDouble();
                    
                    if (deterministicValue < lightPlacementChance)
                    {
                        lightPositions.Add(new Vector3Int(x, y, z));
                    }
                }
            }
        }
        
        // Ensure at least some lights per room if rooms exist
        if (lightPositions.Count == 0)
        {
            // Find ceiling positions to add at least one light
            List<Vector3Int> ceilingPositions = new List<Vector3Int>();
            
            for (int i = 0; i < totalVoxels; i++)
            {
                if (voxelGridFlat[i])
                {
                    Vector3Int coord = IndexToCoord(i);
                    int x = coord.x;
                    int y = coord.y;
                    int z = coord.z;
                    
                    if (y == chunkSize.y - 1 || !GetVoxel(x, y + 1, z))
                    {
                        if (y > 0 && GetVoxel(x, y - 1, z))
                        {
                            ceilingPositions.Add(new Vector3Int(x, y, z));
                        }
                    }
                }
            }
            
            if (ceilingPositions.Count > 0)
            {
                // Use deterministic selection based on chunk seed
                int index = deterministicRandom.Next(ceilingPositions.Count);
                lightPositions.Add(ceilingPositions[index]);
            }
        }
    }
    
    private void CalculateVoxelLightingOptimized()
    {
        int totalVoxels = chunkSize.x * chunkSize.y * chunkSize.z;
        
        // Initialize light grid
        for (int i = 0; i < totalVoxels; i++)
        {
            lightGridFlat[i] = 0f;
        }
        
        // Set initial light values for light sources
        foreach (var lightPos in lightPositions)
        {
            int lightIndex = CoordToIndex(lightPos.x, lightPos.y, lightPos.z);
            lightGridFlat[lightIndex] = lightSourceIntensity;
            
            // Light radiates from source into adjacent empty space
            Vector3Int[] directions = {
                Vector3Int.right, Vector3Int.left,
                Vector3Int.up, Vector3Int.down,
                Vector3Int.forward, Vector3Int.back
            };
            
            foreach (var dir in directions)
            {
                Vector3Int neighbor = lightPos + dir;
                if (IsInGrid(neighbor) && !GetVoxel(neighbor.x, neighbor.y, neighbor.z))
                {
                    int neighborIndex = CoordToIndex(neighbor.x, neighbor.y, neighbor.z);
                    lightGridFlat[neighborIndex] = Mathf.Max(lightGridFlat[neighborIndex], 
                        lightSourceIntensity - lightDecay * 0.5f);
                }
            }
        }
        
        // Propagate light through empty space using optimized algorithm
        float[] newLightGrid = new float[totalVoxels];
        System.Array.Copy(lightGridFlat, newLightGrid, totalVoxels);
        
        for (int step = 0; step < lightPropagationSteps; step++)
        {
            // Parallel processing for performance
            Parallel.For(0, totalVoxels, i =>
            {
                if (voxelGridFlat[i]) return;
                
                Vector3Int coord = IndexToCoord(i);
                float maxNeighborLight = 0f;
                
                // Check all 6 directions
                if (coord.x > 0)
                {
                    int leftIndex = CoordToIndex(coord.x - 1, coord.y, coord.z);
                    if (!voxelGridFlat[leftIndex])
                        maxNeighborLight = Mathf.Max(maxNeighborLight, lightGridFlat[leftIndex]);
                }
                
                if (coord.x < chunkSize.x - 1)
                {
                    int rightIndex = CoordToIndex(coord.x + 1, coord.y, coord.z);
                    if (!voxelGridFlat[rightIndex])
                        maxNeighborLight = Mathf.Max(maxNeighborLight, lightGridFlat[rightIndex]);
                }
                
                if (coord.y > 0)
                {
                    int downIndex = CoordToIndex(coord.x, coord.y - 1, coord.z);
                    if (!voxelGridFlat[downIndex])
                        maxNeighborLight = Mathf.Max(maxNeighborLight, lightGridFlat[downIndex]);
                }
                
                if (coord.y < chunkSize.y - 1)
                {
                    int upIndex = CoordToIndex(coord.x, coord.y + 1, coord.z);
                    if (!voxelGridFlat[upIndex])
                        maxNeighborLight = Mathf.Max(maxNeighborLight, lightGridFlat[upIndex]);
                }
                
                if (coord.z > 0)
                {
                    int backIndex = CoordToIndex(coord.x, coord.y, coord.z - 1);
                    if (!voxelGridFlat[backIndex])
                        maxNeighborLight = Mathf.Max(maxNeighborLight, lightGridFlat[backIndex]);
                }
                
                if (coord.z < chunkSize.z - 1)
                {
                    int forwardIndex = CoordToIndex(coord.x, coord.y, coord.z + 1);
                    if (!voxelGridFlat[forwardIndex])
                        maxNeighborLight = Mathf.Max(maxNeighborLight, lightGridFlat[forwardIndex]);
                }
                
                float propagatedLight = Mathf.Max(0, maxNeighborLight - lightDecay);
                newLightGrid[i] = Mathf.Max(newLightGrid[i], propagatedLight);
            });
            
            // Swap grids
            float[] temp = lightGridFlat;
            lightGridFlat = newLightGrid;
            newLightGrid = temp;
        }
        
        // Solid voxels receive light from adjacent empty voxels
        Parallel.For(0, totalVoxels, i =>
        {
            if (!voxelGridFlat[i]) return;
            
            Vector3Int coord = IndexToCoord(i);
            
            // Check if this is a light source
            bool isLightSource = false;
            foreach (var lightPos in lightPositions)
            {
                if (lightPos.x == coord.x && lightPos.y == coord.y && lightPos.z == coord.z)
                {
                    isLightSource = true;
                    break;
                }
            }
            
            if (isLightSource)
            {
                lightGridFlat[i] = lightSourceIntensity;
                return;
            }
            
            float maxAdjacentLight = 0f;
            
            if (coord.x > 0)
            {
                int leftIndex = CoordToIndex(coord.x - 1, coord.y, coord.z);
                if (!voxelGridFlat[leftIndex])
                    maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGridFlat[leftIndex]);
            }
            
            if (coord.x < chunkSize.x - 1)
            {
                int rightIndex = CoordToIndex(coord.x + 1, coord.y, coord.z);
                if (!voxelGridFlat[rightIndex])
                    maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGridFlat[rightIndex]);
            }
            
            if (coord.y > 0)
            {
                int downIndex = CoordToIndex(coord.x, coord.y - 1, coord.z);
                if (!voxelGridFlat[downIndex])
                    maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGridFlat[downIndex]);
            }
            
            if (coord.y < chunkSize.y - 1)
            {
                int upIndex = CoordToIndex(coord.x, coord.y + 1, coord.z);
                if (!voxelGridFlat[upIndex])
                    maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGridFlat[upIndex]);
            }
            
            if (coord.z > 0)
            {
                int backIndex = CoordToIndex(coord.x, coord.y, coord.z - 1);
                if (!voxelGridFlat[backIndex])
                    maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGridFlat[backIndex]);
            }
            
            if (coord.z < chunkSize.z - 1)
            {
                int forwardIndex = CoordToIndex(coord.x, coord.y, coord.z + 1);
                if (!voxelGridFlat[forwardIndex])
                    maxAdjacentLight = Mathf.Max(maxAdjacentLight, lightGridFlat[forwardIndex]);
            }
            
            lightGridFlat[i] = Mathf.Max(lightGridFlat[i], maxAdjacentLight * 0.7f);
        });
        
        // Smooth lighting if enabled
        if (smoothLighting)
        {
            SmoothLightingOptimized();
        }
        
        // Convert back to 3D array
        ConvertFromFlatArray();
    }
    
    private void SmoothLightingOptimized()
    {
        int totalVoxels = chunkSize.x * chunkSize.y * chunkSize.z;
        float[] smoothed = new float[totalVoxels];
        
        Parallel.For(0, totalVoxels, i =>
        {
            Vector3Int coord = IndexToCoord(i);
            float sum = 0;
            int count = 0;
            
            // 3x3x3 kernel
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int nx = coord.x + dx;
                        int ny = coord.y + dy;
                        int nz = coord.z + dz;
                        
                        if (nx >= 0 && nx < chunkSize.x &&
                            ny >= 0 && ny < chunkSize.y &&
                            nz >= 0 && nz < chunkSize.z)
                        {
                            int neighborIndex = CoordToIndex(nx, ny, nz);
                            if (voxelGridFlat[neighborIndex] == voxelGridFlat[i])
                            {
                                sum += lightGridFlat[neighborIndex];
                                count++;
                            }
                        }
                    }
                }
            }
            
            if (count > 0)
                smoothed[i] = sum / count;
            else
                smoothed[i] = lightGridFlat[i];
        });
        
        // Blend smoothed with original
        for (int i = 0; i < totalVoxels; i++)
        {
            lightGridFlat[i] = smoothed[i] * 0.7f + lightGridFlat[i] * 0.3f;
        }
    }
    
    private void GenerateLitMeshOptimized()
    {
        MeshData meshData = new MeshData();
        int totalVoxels = chunkSize.x * chunkSize.y * chunkSize.z;
        
        // Pre-allocate lists with estimated capacity
        int estimatedFaces = totalVoxels / 2; // Rough estimate
        meshData.vertices.Capacity = estimatedFaces * 4;
        meshData.triangles.Capacity = estimatedFaces * 6;
        meshData.uv.Capacity = estimatedFaces * 4;
        meshData.colors.Capacity = estimatedFaces * 4;
        meshData.normals.Capacity = estimatedFaces * 4;
        
        for (int i = 0; i < totalVoxels; i++)
        {
            if (voxelGridFlat[i])
            {
                Vector3Int coord = IndexToCoord(i);
                AddFacesWithCrossChunkCullingOptimized(coord.x, coord.y, coord.z, meshData);
            }
        }
        
        ApplyMesh(meshData);
    }
    
    private void AddFacesWithCrossChunkCullingOptimized(int x, int y, int z, MeshData meshData)
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
        if (ShouldGenerateFaceOptimized(x, y, z, Vector3Int.left))
        {
            byte materialID = isLightSource ? MATERIAL_LIGHT : MATERIAL_WALL;
            float faceLight = GetFaceLightLevelOptimized(x, y, z, Vector3Int.left);
            AddFaceOptimized(offset, 
                new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(0,0,1),
                meshData, materialID, false, faceLight);
        }
        
        // RIGHT FACE (Positive X)
        if (ShouldGenerateFaceOptimized(x, y, z, Vector3Int.right))
        {
            byte materialID = isLightSource ? MATERIAL_LIGHT : MATERIAL_WALL;
            float faceLight = GetFaceLightLevelOptimized(x, y, z, Vector3Int.right);
            AddFaceOptimized(offset, 
                new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(1,0,0),
                meshData, materialID, false, faceLight);
        }
        
        // BOTTOM FACE (Negative Y) - FLOOR
        if (ShouldGenerateFaceOptimized(x, y, z, Vector3Int.down))
        {
            byte materialID = isLightSource ? MATERIAL_LIGHT : MATERIAL_FLOOR;
            float faceLight = GetFaceLightLevelOptimized(x, y, z, Vector3Int.down);
            AddFaceOptimized(offset, 
                new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0),
                meshData, materialID, true, faceLight);
        }
        
        // TOP FACE (Positive Y) - CEILING
        if (ShouldGenerateFaceOptimized(x, y, z, Vector3Int.up))
        {
            byte materialID = isLightSource ? MATERIAL_LIGHT : MATERIAL_CEILING;
            float faceLight = GetFaceLightLevelOptimized(x, y, z, Vector3Int.up);
            AddFaceOptimized(offset, 
                new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1),
                meshData, materialID, true, faceLight);
        }
        
        // FRONT FACE (Negative Z)
        if (ShouldGenerateFaceOptimized(x, y, z, Vector3Int.back))
        {
            byte materialID = isLightSource ? MATERIAL_LIGHT : MATERIAL_WALL;
            float faceLight = GetFaceLightLevelOptimized(x, y, z, Vector3Int.back);
            AddFaceOptimized(offset, 
                new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0),
                meshData, materialID, false, faceLight);
        }
        
        // BACK FACE (Positive Z)
        if (ShouldGenerateFaceOptimized(x, y, z, Vector3Int.forward))
        {
            byte materialID = isLightSource ? MATERIAL_LIGHT : MATERIAL_WALL;
            float faceLight = GetFaceLightLevelOptimized(x, y, z, Vector3Int.forward);
            AddFaceOptimized(offset, 
                new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1),
                meshData, materialID, false, faceLight);
        }
    }
    
    private bool ShouldGenerateFaceOptimized(int x, int y, int z, Vector3Int direction)
    {
        // Check adjacent voxel in this chunk
        Vector3Int adjPos = new Vector3Int(x, y, z) + direction;
        
        if (IsInGrid(adjPos))
        {
            return !GetVoxel(adjPos.x, adjPos.y, adjPos.z);
        }
        else
        {
            // This voxel is at a chunk boundary
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
        return false;
    }
    
    private float GetFaceLightLevelOptimized(int x, int y, int z, Vector3Int faceNormal)
    {
        // Get light from the empty space adjacent to this face
        Vector3Int adjPos = new Vector3Int(x, y, z) + faceNormal;
        
        if (IsInGrid(adjPos))
        {
            if (!GetVoxel(adjPos.x, adjPos.y, adjPos.z))
            {
                return lightGrid[adjPos.x, adjPos.y, adjPos.z];
            }
        }
        
        // If adjacent is solid or out of bounds, use this voxel's light
        return lightGrid[x, y, z];
    }
    
    private void AddFaceOptimized(Vector3 offset, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
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
        if (meshData == null || meshData.vertices.Count == 0)
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
        
        return voxelGridFlat[CoordToIndex(x, y, z)];
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
        // Combine world seed with chunk coordinates for a unique but deterministic seed
        return worldSeed ^ (chunkCoord.x * 73856093) ^ (chunkCoord.y * 19349663) ^ (chunkCoord.z * 83492791);
    }
    
    private int GetPositionHash(int x, int y, int z)
    {
        // Create a unique hash for a specific position within this chunk
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
        if (meshFilter != null && meshFilter.mesh != null)
            Destroy(meshFilter.mesh);
        if (meshCollider != null && meshCollider.sharedMesh != null)
            meshCollider.sharedMesh = null;
            
        voxelGrid = null;
        lightGrid = null;
        lightPositions.Clear();
        voxelGridFlat = null;
        lightGridFlat = null;
    }
    
    public void UpdateBoundaryMeshes()
    {
        if (voxelGrid != null)
        {
            GenerateMesh(voxelGrid);
        }
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