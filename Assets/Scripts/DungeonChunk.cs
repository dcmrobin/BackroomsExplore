using UnityEngine;
using System.Collections.Generic;

public class DungeonChunk : MonoBehaviour
{
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    
    private bool[,,] voxelGrid;
    private Vector3Int chunkSize;
    
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
    }
    
    public void GenerateMesh(bool[,,] grid)
    {
        voxelGrid = grid;
        
        // EXACT SAME MESH GENERATION AS ORIGINAL (but per-chunk)
        var meshData = GenerateLitMesh();
        ApplyMesh(meshData);
    }
    
    private MeshData GenerateLitMesh()
    {
        MeshData meshData = new MeshData();
        
        // Generate mesh with lighting data (simplified - no lighting for now)
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    if (voxelGrid[x, y, z])
                    {
                        AddFacesWithData(x, y, z, meshData);
                    }
                }
            }
        }
        
        return meshData;
    }
    
    private void AddFacesWithData(int x, int y, int z, MeshData meshData)
    {
        Vector3 offset = new Vector3(x, y, z);
        
        // EXACT SAME FACE CHECKING AS ORIGINAL
        // LEFT FACE
        if (x == 0 || !voxelGrid[x - 1, y, z])
        {
            byte materialID = MATERIAL_WALL;
            AddFace(offset, 
                new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(0,0,1),
                meshData, materialID, false);
        }
        
        // RIGHT FACE
        if (x == chunkSize.x - 1 || !voxelGrid[x + 1, y, z])
        {
            byte materialID = MATERIAL_WALL;
            AddFace(offset, 
                new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(1,0,0),
                meshData, materialID, false);
        }
        
        // BOTTOM FACE - FLOOR
        if (y == 0 || !voxelGrid[x, y - 1, z])
        {
            byte materialID = MATERIAL_FLOOR;
            AddFace(offset, 
                new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0),
                meshData, materialID, true);
        }
        
        // TOP FACE - CEILING
        if (y == chunkSize.y - 1 || !voxelGrid[x, y + 1, z])
        {
            byte materialID = MATERIAL_CEILING;
            AddFace(offset, 
                new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1),
                meshData, materialID, true);
        }
        
        // FRONT FACE
        if (z == 0 || !voxelGrid[x, y, z - 1])
        {
            byte materialID = MATERIAL_WALL;
            AddFace(offset, 
                new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0),
                meshData, materialID, false);
        }
        
        // BACK FACE
        if (z == chunkSize.z - 1 || !voxelGrid[x, y, z + 1])
        {
            byte materialID = MATERIAL_WALL;
            AddFace(offset, 
                new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1),
                meshData, materialID, false);
        }
    }
    
    private void AddFace(Vector3 offset, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
                        MeshData meshData, byte materialID, bool isHorizontal)
    {
        int baseIndex = meshData.vertices.Count;
        
        // Add vertices
        meshData.vertices.Add(v0 + offset);
        meshData.vertices.Add(v1 + offset);
        meshData.vertices.Add(v2 + offset);
        meshData.vertices.Add(v3 + offset);
        
        // Add triangles (same winding as original)
        meshData.triangles.Add(baseIndex);
        meshData.triangles.Add(baseIndex + 1);
        meshData.triangles.Add(baseIndex + 2);
        meshData.triangles.Add(baseIndex + 2);
        meshData.triangles.Add(baseIndex + 3);
        meshData.triangles.Add(baseIndex);
        
        // Add UVs (simplified - no texture scaling for now)
        float width = 1f;
        float height = 1f;
        
        if (isHorizontal)
        {
            width = Vector3.Distance(v0, v3);
            height = Vector3.Distance(v0, v1);
            meshData.uv.Add(new Vector2(0, 0));
            meshData.uv.Add(new Vector2(0, height));
            meshData.uv.Add(new Vector2(width, height));
            meshData.uv.Add(new Vector2(width, 0));
        }
        else
        {
            width = Vector3.Distance(v0, v1);
            height = Vector3.Distance(v0, v3);
            meshData.uv.Add(new Vector2(0, 0));
            meshData.uv.Add(new Vector2(0, height));
            meshData.uv.Add(new Vector2(width, height));
            meshData.uv.Add(new Vector2(width, 0));
        }
        
        // Add colors (material ID in R, light level in G)
        // For now, use constant lighting - add proper lighting later
        float lightLevel = 0.7f;
        Color vertexColor = new Color(materialID / 3f, lightLevel, 0, 1);
        
        for (int i = 0; i < 4; i++)
        {
            meshData.colors.Add(vertexColor);
        }
    }
    
    private void ApplyMesh(MeshData meshData)
    {
        if (meshData.vertices.Count == 0)
        {
            meshFilter.mesh = null;
            meshCollider.sharedMesh = null;
            return;
        }
        
        Mesh mesh = new Mesh();
        
        if (meshData.vertices.Count > 65535)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            
        mesh.vertices = meshData.vertices.ToArray();
        mesh.triangles = meshData.triangles.ToArray();
        mesh.uv = meshData.uv.ToArray();
        mesh.colors = meshData.colors.ToArray();
        
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.Optimize();
        
        meshFilter.mesh = mesh;
        meshCollider.sharedMesh = mesh;
    }
    
    public void Clear()
    {
        if (meshFilter != null && meshFilter.mesh != null)
            Destroy(meshFilter.mesh);
        if (meshCollider != null && meshCollider.sharedMesh != null)
            meshCollider.sharedMesh = null;
            
        voxelGrid = null;
    }
    
    private class MeshData
    {
        public List<Vector3> vertices = new List<Vector3>();
        public List<int> triangles = new List<int>();
        public List<Vector2> uv = new List<Vector2>();
        public List<Color> colors = new List<Color>();
    }
}