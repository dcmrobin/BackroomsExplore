using UnityEngine;
using System.Collections.Generic;

public class DungeonChunk : MonoBehaviour
{
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    
    private bool[,,] voxelGrid;
    private Vector3Int chunkSize;
    
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
        
        var meshData = GenerateSimpleMesh();
        ApplyMesh(meshData);
    }
    
    private MeshData GenerateSimpleMesh()
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
                        Vector3 offset = new Vector3(x, y, z);
                        
                        // Check neighbors - only render faces where neighbor is EMPTY
                        // This automatically creates hollow rooms!
                        bool leftEmpty = x == 0 || !voxelGrid[x - 1, y, z];
                        bool rightEmpty = x == chunkSize.x - 1 || !voxelGrid[x + 1, y, z];
                        bool bottomEmpty = y == 0 || !voxelGrid[x, y - 1, z];
                        bool topEmpty = y == chunkSize.y - 1 || !voxelGrid[x, y + 1, z];
                        bool frontEmpty = z == 0 || !voxelGrid[x, y, z - 1];
                        bool backEmpty = z == chunkSize.z - 1 || !voxelGrid[x, y, z + 1];
                        
                        // Determine material type
                        byte materialID = 0; // Default wall
                        
                        // Floor check: solid with empty above
                        if (bottomEmpty || (y > 0 && !voxelGrid[x, y - 1, z]))
                            materialID = 1; // Floor
                        // Ceiling check: solid with empty below  
                        else if (topEmpty || (y < chunkSize.y - 1 && !voxelGrid[x, y + 1, z]))
                            materialID = 2; // Ceiling
                        
                        // Add faces where neighbor is empty
                        if (leftEmpty) AddFace(meshData, offset, FaceDirection.Left, materialID);
                        if (rightEmpty) AddFace(meshData, offset, FaceDirection.Right, materialID);
                        if (bottomEmpty) AddFace(meshData, offset, FaceDirection.Bottom, materialID);
                        if (topEmpty) AddFace(meshData, offset, FaceDirection.Top, materialID);
                        if (frontEmpty) AddFace(meshData, offset, FaceDirection.Front, materialID);
                        if (backEmpty) AddFace(meshData, offset, FaceDirection.Back, materialID);
                    }
                }
            }
        }
        
        return meshData;
    }
    
    private enum FaceDirection { Left, Right, Bottom, Top, Front, Back }
    
    private void AddFace(MeshData meshData, Vector3 offset, FaceDirection direction, byte materialID)
    {
        int baseIndex = meshData.vertices.Count;
        
        Vector3[] vertices;
        Vector2[] uvs;
        
        switch (direction)
        {
            case FaceDirection.Left:
                vertices = new Vector3[] {
                    new Vector3(0,0,0), new Vector3(0,1,0), 
                    new Vector3(0,1,1), new Vector3(0,0,1)
                };
                uvs = new Vector2[] { new Vector2(0,0), new Vector2(0,1), new Vector2(1,1), new Vector2(1,0) };
                break;
                
            case FaceDirection.Right:
                vertices = new Vector3[] {
                    new Vector3(1,0,1), new Vector3(1,1,1), 
                    new Vector3(1,1,0), new Vector3(1,0,0)
                };
                uvs = new Vector2[] { new Vector2(0,0), new Vector2(0,1), new Vector2(1,1), new Vector2(1,0) };
                break;
                
            case FaceDirection.Bottom:
                vertices = new Vector3[] {
                    new Vector3(0,0,1), new Vector3(1,0,1), 
                    new Vector3(1,0,0), new Vector3(0,0,0)
                };
                uvs = new Vector2[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
                break;
                
            case FaceDirection.Top:
                vertices = new Vector3[] {
                    new Vector3(0,1,0), new Vector3(1,1,0), 
                    new Vector3(1,1,1), new Vector3(0,1,1)
                };
                uvs = new Vector2[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
                break;
                
            case FaceDirection.Front:
                vertices = new Vector3[] {
                    new Vector3(0,0,0), new Vector3(1,0,0), 
                    new Vector3(1,1,0), new Vector3(0,1,0)
                };
                uvs = new Vector2[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
                break;
                
            case FaceDirection.Back:
                vertices = new Vector3[] {
                    new Vector3(1,0,1), new Vector3(0,0,1), 
                    new Vector3(0,1,1), new Vector3(1,1,1)
                };
                uvs = new Vector2[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
                break;
                
            default:
                return;
        }
        
        // Add vertices
        foreach (var vertex in vertices)
        {
            meshData.vertices.Add(vertex + offset);
        }
        
        // Add triangles (CCW winding for outward normals)
        meshData.triangles.Add(baseIndex);
        meshData.triangles.Add(baseIndex + 1);
        meshData.triangles.Add(baseIndex + 2);
        meshData.triangles.Add(baseIndex + 2);
        meshData.triangles.Add(baseIndex + 3);
        meshData.triangles.Add(baseIndex);
        
        // Add UVs
        meshData.uv.AddRange(uvs);
        
        // Add colors (material ID in R, light level in G)
        float lightLevel = CalculateLightLevel(offset + new Vector3(0.5f, 0.5f, 0.5f));
        Color vertexColor = new Color(materialID / 3f, lightLevel, 0, 1);
        
        for (int i = 0; i < 4; i++)
        {
            meshData.colors.Add(vertexColor);
        }
    }
    
    private float CalculateLightLevel(Vector3 position)
    {
        // Simple lighting: darker near bottom, lighter near top
        float heightFactor = position.y / chunkSize.y;
        float baseLight = 0.2f + heightFactor * 0.6f;
        
        // Darken corners slightly
        float cornerFactor = 1f - (Mathf.Abs(position.x - chunkSize.x/2f) / (chunkSize.x/2f) * 
                                  Mathf.Abs(position.z - chunkSize.z/2f) / (chunkSize.z/2f)) * 0.3f;
        
        return Mathf.Clamp01(baseLight * cornerFactor);
    }
    
    private void ApplyMesh(MeshData meshData)
    {
        if (meshData.vertices.Count == 0)
        {
            if (meshFilter != null) meshFilter.mesh = null;
            if (meshCollider != null) meshCollider.sharedMesh = null;
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
        
        meshFilter.mesh = mesh;
        meshCollider.sharedMesh = mesh;
    }
    
    public bool GetVoxel(Vector3Int localPos)
    {
        if (localPos.x >= 0 && localPos.x < chunkSize.x &&
            localPos.y >= 0 && localPos.y < chunkSize.y &&
            localPos.z >= 0 && localPos.z < chunkSize.z)
        {
            return voxelGrid[localPos.x, localPos.y, localPos.z];
        }
        return false;
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