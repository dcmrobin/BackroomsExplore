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
        
        var meshData = GenerateMeshWithRoomShells();
        ApplyMesh(meshData);
    }
    
    private MeshData GenerateMeshWithRoomShells()
    {
        MeshData meshData = new MeshData();
        
        // First pass: Mark which voxels are room shells vs interiors
        bool[,,] isRoomShell = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
        bool[,,] isRoomInterior = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
        
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    if (voxelGrid[x, y, z])
                    {
                        // A voxel is part of a room SHELL if:
                        // 1. It's on the edge of a solid region (has at least one empty neighbor)
                        // 2. OR it's completely surrounded but we want to render it anyway (for corridors)
                        
                        bool leftEmpty = x == 0 || !voxelGrid[x - 1, y, z];
                        bool rightEmpty = x == chunkSize.x - 1 || !voxelGrid[x + 1, y, z];
                        bool bottomEmpty = y == 0 || !voxelGrid[x, y - 1, z];
                        bool topEmpty = y == chunkSize.y - 1 || !voxelGrid[x, y + 1, z];
                        bool frontEmpty = z == 0 || !voxelGrid[x, y, z - 1];
                        bool backEmpty = z == chunkSize.z - 1 || !voxelGrid[x, y, z + 1];
                        
                        // Check if this is on the surface of a solid region
                        isRoomShell[x, y, z] = leftEmpty || rightEmpty || bottomEmpty || topEmpty || frontEmpty || backEmpty;
                        
                        // Check if this is interior (completely surrounded by solid)
                        isRoomInterior[x, y, z] = !leftEmpty && !rightEmpty && !bottomEmpty && !topEmpty && !frontEmpty && !backEmpty;
                    }
                }
            }
        }
        
        // Second pass: Generate mesh only for shell voxels
        for (int x = 0; x < chunkSize.x; x++)
        {
            for (int y = 0; y < chunkSize.y; y++)
            {
                for (int z = 0; z < chunkSize.z; z++)
                {
                    if (voxelGrid[x, y, z] && isRoomShell[x, y, z])
                    {
                        Vector3 offset = new Vector3(x, y, z);
                        
                        // Determine material
                        byte materialID = DetermineMaterialID(x, y, z, isRoomInterior[x, y, z]);
                        
                        // Check neighbors - only render faces where neighbor is EMPTY or not part of shell
                        bool leftSolid = x > 0 && voxelGrid[x - 1, y, z];
                        bool rightSolid = x < chunkSize.x - 1 && voxelGrid[x + 1, y, z];
                        bool bottomSolid = y > 0 && voxelGrid[x, y - 1, z];
                        bool topSolid = y < chunkSize.y - 1 && voxelGrid[x, y + 1, z];
                        bool frontSolid = z > 0 && voxelGrid[x, y, z - 1];
                        bool backSolid = z < chunkSize.z - 1 && voxelGrid[x, y, z + 1];
                        
                        // For room interiors: INVERT the face check
                        // Render faces where neighbor is ALSO part of the shell (creating inward faces)
                        if (isRoomInterior[x, y, z])
                        {
                            // Special case: interior voxel that should still render inward faces
                            // Check if neighbor is also interior but we want a face between them
                            if (leftSolid && isRoomInterior[x - 1, y, z]) 
                                AddFace(meshData, offset, FaceDirection.Left, materialID, true);
                            if (rightSolid && isRoomInterior[x + 1, y, z]) 
                                AddFace(meshData, offset, FaceDirection.Right, materialID, true);
                            if (bottomSolid && isRoomInterior[x, y - 1, z]) 
                                AddFace(meshData, offset, FaceDirection.Bottom, materialID, true);
                            if (topSolid && isRoomInterior[x, y + 1, z]) 
                                AddFace(meshData, offset, FaceDirection.Top, materialID, true);
                            if (frontSolid && isRoomInterior[x, y, z - 1]) 
                                AddFace(meshData, offset, FaceDirection.Front, materialID, true);
                            if (backSolid && isRoomInterior[x, y, z + 1]) 
                                AddFace(meshData, offset, FaceDirection.Back, materialID, true);
                        }
                        else
                        {
                            // Normal exterior faces - render where neighbor is empty
                            if (!leftSolid) AddFace(meshData, offset, FaceDirection.Left, materialID, false);
                            if (!rightSolid) AddFace(meshData, offset, FaceDirection.Right, materialID, false);
                            if (!bottomSolid) AddFace(meshData, offset, FaceDirection.Bottom, materialID, false);
                            if (!topSolid) AddFace(meshData, offset, FaceDirection.Top, materialID, false);
                            if (!frontSolid) AddFace(meshData, offset, FaceDirection.Front, materialID, false);
                            if (!backSolid) AddFace(meshData, offset, FaceDirection.Back, materialID, false);
                        }
                    }
                }
            }
        }
        
        return meshData;
    }
    
    private byte DetermineMaterialID(int x, int y, int z, bool isInterior)
    {
        if (isInterior)
        {
            // Interior walls - use wall material
            return 0;
        }
        
        // Check if this is likely a floor (solid with empty above)
        if (y < chunkSize.y - 1 && !voxelGrid[x, y + 1, z])
            return 1; // Floor
        
        // Check if this is likely a ceiling (solid with empty below)
        if (y > 0 && !voxelGrid[x, y - 1, z])
            return 2; // Ceiling
        
        // Default to wall
        return 0;
    }
    
    private enum FaceDirection { Left, Right, Bottom, Top, Front, Back }
    
    private void AddFace(MeshData meshData, Vector3 offset, FaceDirection direction, 
                        byte materialID, bool invertNormals)
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
        
        // Add triangles with proper winding for normals
        if (!invertNormals)
        {
            // Outward-facing normals (CCW winding)
            meshData.triangles.Add(baseIndex);
            meshData.triangles.Add(baseIndex + 1);
            meshData.triangles.Add(baseIndex + 2);
            meshData.triangles.Add(baseIndex + 2);
            meshData.triangles.Add(baseIndex + 3);
            meshData.triangles.Add(baseIndex);
        }
        else
        {
            // Inward-facing normals (CW winding)
            meshData.triangles.Add(baseIndex);
            meshData.triangles.Add(baseIndex + 3);
            meshData.triangles.Add(baseIndex + 2);
            meshData.triangles.Add(baseIndex + 2);
            meshData.triangles.Add(baseIndex + 1);
            meshData.triangles.Add(baseIndex);
        }
        
        // Add UVs
        meshData.uv.AddRange(uvs);
        
        // Add colors (material ID in R, light level in G)
        float lightLevel = CalculateLightLevel(offset + new Vector3(0.5f, 0.5f, 0.5f), invertNormals);
        Color vertexColor = new Color(materialID / 3f, lightLevel, 0, 1);
        
        for (int i = 0; i < 4; i++)
        {
            meshData.colors.Add(vertexColor);
        }
    }
    
    private float CalculateLightLevel(Vector3 position, bool isInteriorFace)
    {
        if (isInteriorFace)
        {
            // Room interiors are darker
            return 0.3f;
        }
        
        // Simple lighting: darker near bottom, lighter near top
        float heightFactor = position.y / chunkSize.y;
        float baseLight = 0.2f + heightFactor * 0.6f;
        
        return Mathf.Clamp01(baseLight);
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
        
        // Don't recalculate normals - we handle them via winding order
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