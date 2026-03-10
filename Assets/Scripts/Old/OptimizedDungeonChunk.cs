using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

public class OptimizedDungeonChunk : MonoBehaviour
{
    [Header("Performance")]
    [SerializeField] private bool skipEmptyChunks = true;
    
    [Header("Textures")]
    [SerializeField] private Texture2D wallTexture;
    [SerializeField] private Texture2D floorTexture;
    [SerializeField] private Texture2D ceilingTexture;
    [SerializeField] private Vector2 textureScale = Vector2.one;
    
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    
    private NativeArray<byte> voxelData;
    private Vector3Int chunkSize;
    private Vector3Int chunkCoord;
    private int worldSeed;
    
    private byte[] voxelDataArray;
    
    // Material IDs (must match shader)
    private const byte MATERIAL_WALL = 0;
    private const byte MATERIAL_FLOOR = 1;
    private const byte MATERIAL_CEILING = 2;
    private const byte MATERIAL_LIGHT = 3;
    
    // Reference to chunk manager for cross-chunk checks
    private OptimizedChunkManager chunkManager;
    
    public void Initialize(Vector3Int size)
    {
        chunkSize = size;
        
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshCollider = GetComponent<MeshCollider>();
        
        if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();
        if (meshCollider == null) meshCollider = gameObject.AddComponent<MeshCollider>();
        
        // Get reference to chunk manager
        chunkManager = FindObjectOfType<OptimizedChunkManager>();
        
        // Start with collider disabled
        if (meshCollider != null)
        {
            meshCollider.enabled = false;
            meshCollider.sharedMesh = null;
        }
        
        // Apply textures to material
        if (meshRenderer.material != null)
        {
            meshRenderer.material.SetTexture("_WallTex", wallTexture);
            meshRenderer.material.SetTexture("_FloorTex", floorTexture);
            meshRenderer.material.SetTexture("_CeilingTex", ceilingTexture);
            meshRenderer.material.SetVector("_TextureScale", new Vector4(textureScale.x, textureScale.y, 0, 0));
        }
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
        
        try
        {
            Mesh mesh = new Mesh();
            GenerateGreedyMeshWithTextures(mesh);
            
            meshFilter.mesh = mesh;
            
            // Update collider if enabled
            if (meshCollider != null && meshCollider.enabled)
            {
                meshCollider.sharedMesh = mesh;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error generating mesh for chunk {chunkCoord}: {e.Message}");
        }
    }
    
    private void GenerateGreedyMeshWithTextures(Mesh mesh)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector2> uvs = new List<Vector2>();
        List<Color> colors = new List<Color>();
        
        // Generate faces with texture/material information
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    if (!GetVoxel(x, y, z)) continue;
                    
                    Vector3 offset = new Vector3(x, y, z);
                    
                    // Determine material based on position
                    byte materialID = GetMaterialID(x, y, z);
                    float faceLight = 0.8f; // Default lighting (could add proper lighting later)
                    
                    // Check if this is a light source (you'd need to detect this from voxel data)
                    bool isLightSource = false; // Set based on your voxel data
                    
                    if (isLightSource) materialID = MATERIAL_LIGHT;
                    
                    // Only generate faces that are visible (cross-chunk culling)
                    if (ShouldGenerateFace(x, y, z, Vector3Int.left))
                    {
                        AddFace(offset, 
                            new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(0,0,1),
                            vertices, triangles, uvs, colors, 
                            materialID, false, faceLight);
                    }
                    
                    if (ShouldGenerateFace(x, y, z, Vector3Int.right))
                    {
                        AddFace(offset, 
                            new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(1,0,0),
                            vertices, triangles, uvs, colors, 
                            materialID, false, faceLight);
                    }
                    
                    if (ShouldGenerateFace(x, y, z, Vector3Int.down))
                    {
                        AddFace(offset, 
                            new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0),
                            vertices, triangles, uvs, colors, 
                            materialID, true, faceLight);
                    }
                    
                    if (ShouldGenerateFace(x, y, z, Vector3Int.up))
                    {
                        AddFace(offset, 
                            new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1),
                            vertices, triangles, uvs, colors, 
                            materialID, true, faceLight);
                    }
                    
                    if (ShouldGenerateFace(x, y, z, Vector3Int.back))
                    {
                        AddFace(offset, 
                            new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0),
                            vertices, triangles, uvs, colors, 
                            materialID, false, faceLight);
                    }
                    
                    if (ShouldGenerateFace(x, y, z, Vector3Int.forward))
                    {
                        AddFace(offset, 
                            new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1),
                            vertices, triangles, uvs, colors, 
                            materialID, false, faceLight);
                    }
                }
            }
        }
        
        if (vertices.Count == 0)
        {
            ClearMesh();
            return;
        }
        
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.colors = colors.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        
        if (mesh.vertexCount > 65535)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
    }
    
    private byte GetMaterialID(int x, int y, int z)
    {
        // Determine material based on face orientation
        if (y == 0 || !GetVoxel(x, y - 1, z)) return MATERIAL_FLOOR; // Bottom face
        if (y == chunkSize.y - 1 || !GetVoxel(x, y + 1, z)) return MATERIAL_CEILING; // Top face
        return MATERIAL_WALL; // Everything else
    }
    
    private bool ShouldGenerateFace(int x, int y, int z, Vector3Int direction)
    {
        Vector3Int adjPos = new Vector3Int(x, y, z) + direction;
        
        // Check within current chunk first
        if (IsInGrid(adjPos))
        {
            return !GetVoxel(adjPos.x, adjPos.y, adjPos.z);
        }
        else
        {
            // At chunk boundary - check adjacent chunk
            return !IsSolidInAdjacentChunk(x, y, z, direction);
        }
    }
    
    private bool IsSolidInAdjacentChunk(int x, int y, int z, Vector3Int direction)
    {
        if (chunkManager == null) return false;
        
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
                        List<Color> colors, byte materialID, bool isHorizontal, float faceLight)
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
        
        // Add UVs with texture scaling
        if (isHorizontal)
        {
            uv.Add(new Vector2(0, 0) * textureScale);
            uv.Add(new Vector2(0, 1) * textureScale);
            uv.Add(new Vector2(1, 1) * textureScale);
            uv.Add(new Vector2(1, 0) * textureScale);
        }
        else
        {
            uv.Add(new Vector2(0, 0) * textureScale);
            uv.Add(new Vector2(0, 1) * textureScale);
            uv.Add(new Vector2(1, 1) * textureScale);
            uv.Add(new Vector2(1, 0) * textureScale);
        }
        
        // Add colors with material ID and lighting
        // color.r = materialID (0-3)
        // color.g = lighting (0-1)
        // color.b = reserved for future use
        // color.a = 1
        Color color = new Color(materialID / 3f, faceLight, 0, 1);
        colors.Add(color);
        colors.Add(color);
        colors.Add(color);
        colors.Add(color);
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
    
    public void Clear()
    {
        ClearMesh();
        voxelDataArray = null;
        
        if (meshCollider != null)
        {
            meshCollider.enabled = false;
            meshCollider.sharedMesh = null;
        }
    }
    
    private void ClearMesh()
    {
        if (meshFilter != null && meshFilter.mesh != null)
            Destroy(meshFilter.mesh);
        
        if (meshCollider != null)
        {
            meshCollider.sharedMesh = null;
            meshCollider.enabled = false;
        }
    }
    
    public void SetColliderEnabled(bool enabled)
    {
        if (meshCollider != null)
        {
            meshCollider.enabled = enabled;
            if (enabled && meshCollider.sharedMesh == null && meshFilter != null)
            {
                meshCollider.sharedMesh = meshFilter.mesh;
            }
            else if (!enabled)
            {
                meshCollider.sharedMesh = null;
            }
        }
    }
    
    public void UpdateBoundaryMeshes()
    {
        if (voxelData.IsCreated && meshFilter != null)
        {
            GenerateMesh(voxelData);
        }
    }
    
    private bool IsInGrid(Vector3Int pos)
    {
        return pos.x >= 0 && pos.x < chunkSize.x &&
               pos.y >= 0 && pos.y < chunkSize.y &&
               pos.z >= 0 && pos.z < chunkSize.z;
    }
}