// File: OptimizedDungeonChunk.cs
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

public class OptimizedDungeonChunk : MonoBehaviour
{
    [Header("Performance")]
    [SerializeField] private bool skipEmptyChunks = true;
    
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    
    private NativeArray<byte> voxelData;
    private Vector3Int chunkSize;
    private Vector3Int chunkCoord;
    
    private byte[] voxelDataArray;
    
    public void Initialize(Vector3Int size)
    {
        chunkSize = size;
        
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshCollider = GetComponent<MeshCollider>();
        
        if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();
        if (meshCollider == null) meshCollider = gameObject.AddComponent<MeshCollider>();
        
        // Start with collider disabled
        if (meshCollider != null)
        {
            meshCollider.enabled = false;
            meshCollider.sharedMesh = null;
        }
    }
    
    public void SetChunkCoord(Vector3Int coord, int seed)
    {
        chunkCoord = coord;
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
            GenerateGreedyMesh(mesh);
            
            meshFilter.mesh = mesh;
            
            // Note: Collider will be enabled/disabled by ChunkManager based on distance
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error generating mesh for chunk {chunkCoord}: {e.Message}");
        }
    }
    
    private void GenerateGreedyMesh(Mesh mesh)
    {
        // Implement greedy meshing for better performance
        // This is simplified - for full greedy meshing, see: https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/
        
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector2> uvs = new List<Vector2>();
        
        // Simple face generation (replace with greedy meshing for better performance)
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    if (!GetVoxel(x, y, z)) continue;
                    
                    Vector3 offset = new Vector3(x, y, z);
                    
                    // Only generate visible faces
                    if (x == 0 || !GetVoxel(x - 1, y, z)) AddFace(offset, 0, vertices, triangles, uvs);
                    if (x == chunkSize.x - 1 || !GetVoxel(x + 1, y, z)) AddFace(offset, 1, vertices, triangles, uvs);
                    if (y == 0 || !GetVoxel(x, y - 1, z)) AddFace(offset, 2, vertices, triangles, uvs);
                    if (y == chunkSize.y - 1 || !GetVoxel(x, y + 1, z)) AddFace(offset, 3, vertices, triangles, uvs);
                    if (z == 0 || !GetVoxel(x, y, z - 1)) AddFace(offset, 4, vertices, triangles, uvs);
                    if (z == chunkSize.z - 1 || !GetVoxel(x, y, z + 1)) AddFace(offset, 5, vertices, triangles, uvs);
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
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        
        if (mesh.vertexCount > 65535)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
    }
    
    private void AddFace(Vector3 offset, int direction, List<Vector3> vertices, List<int> triangles, List<Vector2> uvs)
    {
        int baseIndex = vertices.Count;
        
        switch (direction)
        {
            case 0: // Left
                vertices.Add(offset + new Vector3(0, 0, 0));
                vertices.Add(offset + new Vector3(0, 1, 0));
                vertices.Add(offset + new Vector3(0, 1, 1));
                vertices.Add(offset + new Vector3(0, 0, 1));
                break;
            case 1: // Right
                vertices.Add(offset + new Vector3(1, 0, 1));
                vertices.Add(offset + new Vector3(1, 1, 1));
                vertices.Add(offset + new Vector3(1, 1, 0));
                vertices.Add(offset + new Vector3(1, 0, 0));
                break;
            case 2: // Bottom
                vertices.Add(offset + new Vector3(0, 0, 1));
                vertices.Add(offset + new Vector3(1, 0, 1));
                vertices.Add(offset + new Vector3(1, 0, 0));
                vertices.Add(offset + new Vector3(0, 0, 0));
                break;
            case 3: // Top
                vertices.Add(offset + new Vector3(0, 1, 0));
                vertices.Add(offset + new Vector3(1, 1, 0));
                vertices.Add(offset + new Vector3(1, 1, 1));
                vertices.Add(offset + new Vector3(0, 1, 1));
                break;
            case 4: // Back
                vertices.Add(offset + new Vector3(0, 0, 0));
                vertices.Add(offset + new Vector3(1, 0, 0));
                vertices.Add(offset + new Vector3(1, 1, 0));
                vertices.Add(offset + new Vector3(0, 1, 0));
                break;
            case 5: // Front
                vertices.Add(offset + new Vector3(1, 0, 1));
                vertices.Add(offset + new Vector3(0, 0, 1));
                vertices.Add(offset + new Vector3(0, 1, 1));
                vertices.Add(offset + new Vector3(1, 1, 1));
                break;
        }
        
        triangles.Add(baseIndex);
        triangles.Add(baseIndex + 1);
        triangles.Add(baseIndex + 2);
        triangles.Add(baseIndex + 2);
        triangles.Add(baseIndex + 3);
        triangles.Add(baseIndex);
        
        uvs.Add(new Vector2(0, 0));
        uvs.Add(new Vector2(0, 1));
        uvs.Add(new Vector2(1, 1));
        uvs.Add(new Vector2(1, 0));
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
            if (enabled && meshCollider.sharedMesh == null)
            {
                meshCollider.sharedMesh = meshFilter.sharedMesh;
            }
            else if (!enabled)
            {
                meshCollider.sharedMesh = null;
            }
        }
    }
    
    public void UpdateBoundaryMeshes()
    {
        if (voxelData.IsCreated && meshFilter != null && meshFilter.mesh != null)
        {
            // Regenerate mesh if needed
            GenerateMesh(voxelData);
        }
    }
}