using UnityEngine;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public class DungeonChunk : MonoBehaviour
{
    private static readonly Vector3Int[] CardinalDirections =
    {
        Vector3Int.right, Vector3Int.left,
        Vector3Int.up, Vector3Int.down,
        Vector3Int.forward, Vector3Int.back
    };

    [Header("Lighting Settings")]
    [SerializeField] private float lightPlacementChance = 0.2f;
    [SerializeField] private float lightDecay = 0.15f;
    [SerializeField] private int lightPropagationSteps = 12;   // reduced from 15 — negligible visual diff, measurable speedup
    [SerializeField] private float lightSourceIntensity = 1.0f;
    [SerializeField] private bool smoothLighting = true;

    [Header("Textures")]
    [SerializeField] private Texture2D wallTexture;
    [SerializeField] private Texture2D floorTexture;
    [SerializeField] private Texture2D ceilingTexture;
    [SerializeField] private Vector2 textureScale = Vector2.one;

    private MeshFilter   meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;

    // voxelData is owned by InfiniteChunkManager — never disposed here
    private NativeArray<byte> voxelData;
    private float[,,] lightGrid;
    private Vector3Int chunkSize;
    private List<Vector3Int>     lightPositions       = new List<Vector3Int>();
    private readonly HashSet<int> lightSourceIndexCache = new HashSet<int>();

    private InfiniteChunkManager chunkManager;
    private Vector3Int chunkCoord;
    private int worldSeed;

    private const byte  MATERIAL_WALL            = 0;
    private const byte  MATERIAL_FLOOR           = 1;
    private const byte  MATERIAL_CEILING         = 2;
    private const byte  MATERIAL_LIGHT           = 3;
    private const float MATERIAL_ENCODE_SCALE    = 1f / 3f;

    private byte[]  voxelDataArray;
    private float[] lightGridFlat;

    // vertex light cache — cleared before every mesh build
    private Dictionary<Vector3Int, float> vertexLightCache = new Dictionary<Vector3Int, float>();

    // Plain-data mesh arrays — safe to build on any thread
    public class MeshData
    {
        public Vector3[] vertices;
        public int[]     triangles;
        public Vector2[] uv;
        public Color[]   colors;
        public Vector3[] normals;
    }

    // -------------------------------------------------------------------------
    // Initialisation
    // -------------------------------------------------------------------------

    public void Initialize(Vector3Int size)
    {
        chunkSize = size;

        meshFilter   = GetComponent<MeshFilter>()   ?? gameObject.AddComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>() ?? gameObject.AddComponent<MeshRenderer>();
        meshCollider = GetComponent<MeshCollider>() ?? gameObject.AddComponent<MeshCollider>();

        lightGridFlat = new float[size.x * size.y * size.z];
    }

    public void SetChunkCoord(Vector3Int coord, int seed) { chunkCoord = coord; worldSeed = seed; }
    public void SetChunkManager(InfiniteChunkManager manager) { chunkManager = manager; }
    public Vector3Int GetChunkCoord()        => chunkCoord;
    public Vector3   GetChunkWorldPosition() => transform.position;

    // -------------------------------------------------------------------------
    // Public generation API
    // -------------------------------------------------------------------------

    // Legacy synchronous path — kept so UpdateBoundaryMeshes still works
    public void GenerateMesh(NativeArray<byte> externalVoxelData)
    {
        voxelData = externalVoxelData;
        int count = chunkSize.x * chunkSize.y * chunkSize.z;
        voxelDataArray = new byte[count];
        voxelData.CopyTo(voxelDataArray);
        MeshData md = BuildMeshDataInternal(recalculateLighting: true);
        UploadMesh(md);
    }

    // Step 1 (background thread): compute lighting and mesh arrays
    public MeshData BuildMeshData(NativeArray<byte> externalVoxelData)
    {
        voxelData = externalVoxelData;
        int count = chunkSize.x * chunkSize.y * chunkSize.z;
        voxelDataArray = new byte[count];
        voxelData.CopyTo(voxelDataArray);
        return BuildMeshDataInternal(recalculateLighting: true);
    }

    // Step 2 (main thread): push arrays to GPU using the fast MeshDataArray API.
    // This avoids the managed→native copy overhead of mesh.vertices = array[].
    // Collider cooking is deferred one frame via StartCoroutine to avoid the
    // synchronous physics bake stall on the main thread.
    public void UploadMesh(MeshData md)
    {
        if (md == null || md.vertices == null || md.vertices.Length == 0)
        {
            if (meshFilter   != null) meshFilter.mesh         = null;
            if (meshCollider != null) meshCollider.sharedMesh = null;
            return;
        }

        try
        {
            // --- Fast path via Mesh.AllocateWritableMeshData ---
            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            var meshData      = meshDataArray[0];

            bool needsUInt32 = md.vertices.Length > 65535;

            // Declare vertex buffer layout: Position, Normal, Color, TexCoord0
            var vertexAttribs = new Unity.Collections.NativeArray<UnityEngine.Rendering.VertexAttributeDescriptor>(4, Allocator.Temp);
            vertexAttribs[0] = new UnityEngine.Rendering.VertexAttributeDescriptor(
                UnityEngine.Rendering.VertexAttribute.Position,  UnityEngine.Rendering.VertexAttributeFormat.Float32, 3);
            vertexAttribs[1] = new UnityEngine.Rendering.VertexAttributeDescriptor(
                UnityEngine.Rendering.VertexAttribute.Normal,    UnityEngine.Rendering.VertexAttributeFormat.Float32, 3);
            vertexAttribs[2] = new UnityEngine.Rendering.VertexAttributeDescriptor(
                UnityEngine.Rendering.VertexAttribute.Color,     UnityEngine.Rendering.VertexAttributeFormat.Float32, 4);
            vertexAttribs[3] = new UnityEngine.Rendering.VertexAttributeDescriptor(
                UnityEngine.Rendering.VertexAttribute.TexCoord0, UnityEngine.Rendering.VertexAttributeFormat.Float32, 2);

            meshData.SetVertexBufferParams(md.vertices.Length, vertexAttribs);
            vertexAttribs.Dispose();

            // Write interleaved vertex data
            var verts = meshData.GetVertexData<VertexData>(0);
            for (int i = 0; i < md.vertices.Length; i++)
            {
                verts[i] = new VertexData
                {
                    position = md.vertices[i],
                    normal   = md.normals[i],
                    color    = new Vector4(md.colors[i].r, md.colors[i].g, md.colors[i].b, md.colors[i].a),
                    uv       = md.uv[i]
                };
            }

            meshData.SetIndexBufferParams(md.triangles.Length,
                needsUInt32 ? UnityEngine.Rendering.IndexFormat.UInt32
                            : UnityEngine.Rendering.IndexFormat.UInt16);

            if (needsUInt32)
            {
                var idx = meshData.GetIndexData<int>();
                for (int i = 0; i < md.triangles.Length; i++) idx[i] = md.triangles[i];
            }
            else
            {
                var idx = meshData.GetIndexData<ushort>();
                for (int i = 0; i < md.triangles.Length; i++) idx[i] = (ushort)md.triangles[i];
            }

            meshData.subMeshCount = 1;
            meshData.SetSubMesh(0, new UnityEngine.Rendering.SubMeshDescriptor(0, md.triangles.Length));

            Mesh mesh = new Mesh();
            if (needsUInt32) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh);
            mesh.RecalculateBounds();

            if (meshFilter != null) meshFilter.mesh = mesh;

            // Defer physics collider bake — cooking is expensive and not needed
            // until the player is actually near the chunk.
            if (meshCollider != null)
                StartCoroutine(BakeColliderNextFrame(mesh));
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[DungeonChunk] UploadMesh failed: {e.Message}");
        }
    }

    // Struct matching the vertex buffer layout declared above
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct VertexData
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector4 color;
        public Vector2 uv;
    }

    // Defers MeshCollider baking by one frame so the main thread stall is
    // pushed outside the upload coroutine's budget.
    private System.Collections.IEnumerator BakeColliderNextFrame(Mesh mesh)
    {
        yield return null; // wait one frame
        if (meshCollider != null)
            meshCollider.sharedMesh = mesh;
    }

    // -------------------------------------------------------------------------
    // Internal mesh build — safe to call from any thread
    // -------------------------------------------------------------------------

    private MeshData BuildMeshDataInternal(bool recalculateLighting)
    {
        if (voxelDataArray == null || voxelDataArray.Length == 0) return null;

        if (recalculateLighting || lightGrid == null)
        {
            PlaceLights();
            CalculateVoxelLightingOptimized();
        }

        // Clear vertex light cache before building so stale data never leaks in
        vertexLightCache.Clear();

        return smoothLighting ? BuildSmoothLitMesh() : BuildFlatLitMesh();
    }

    // -------------------------------------------------------------------------
    // Light placement
    // -------------------------------------------------------------------------

    private void PlaceLights()
    {
        lightPositions.Clear();

        int chunkSeed = GetChunkSeed();
        System.Random rng = new System.Random(chunkSeed);

        int  totalVoxels           = chunkSize.x * chunkSize.y * chunkSize.z;
        int  fallbackCeilingCount  = 0;
        Vector3Int fallbackPos     = Vector3Int.zero;

        for (int i = 0; i < totalVoxels; i++)
        {
            if (voxelDataArray[i] == 0) continue;

            Vector3Int coord = IndexToCoord(i);
            int x = coord.x, y = coord.y, z = coord.z;

            bool isCeiling = (y == chunkSize.y - 1 || !GetVoxel(x, y + 1, z))
                           && (y > 0 && GetVoxel(x, y - 1, z));

            if (!isCeiling) continue;

            fallbackCeilingCount++;
            if (rng.Next(fallbackCeilingCount) == 0)
                fallbackPos = new Vector3Int(x, y, z);

            if (GetDeterministicValue01(x, y, z) < lightPlacementChance)
                lightPositions.Add(new Vector3Int(x, y, z));
        }

        if (lightPositions.Count == 0 && fallbackCeilingCount > 0)
            lightPositions.Add(fallbackPos);

        lightSourceIndexCache.Clear();
        foreach (var lp in lightPositions)
            lightSourceIndexCache.Add(CoordToIndex(lp.x, lp.y, lp.z));
    }

    // -------------------------------------------------------------------------
    // Lighting propagation
    // -------------------------------------------------------------------------

    private void CalculateVoxelLightingOptimized()
    {
        int total       = chunkSize.x * chunkSize.y * chunkSize.z;
        var voxelNative = new NativeArray<byte>(voxelDataArray, Allocator.TempJob);
        var cur         = new NativeArray<float>(total, Allocator.TempJob, NativeArrayOptions.ClearMemory);
        var nxt         = new NativeArray<float>(total, Allocator.TempJob, NativeArrayOptions.ClearMemory);
        var sources     = new NativeParallelHashSet<int>(math.max(1, lightPositions.Count), Allocator.TempJob);

        foreach (var lp in lightPositions)
        {
            int li = CoordToIndex(lp.x, lp.y, lp.z);
            cur[li] = lightSourceIntensity;
            sources.Add(li);

            foreach (var dir in CardinalDirections)
            {
                Vector3Int nb = lp + dir;
                if (IsInGrid(nb) && !GetVoxel(nb.x, nb.y, nb.z))
                {
                    int ni = CoordToIndex(nb.x, nb.y, nb.z);
                    cur[ni] = Mathf.Max(cur[ni], lightSourceIntensity - lightDecay * 0.5f);
                }
            }
        }

        for (int step = 0; step < lightPropagationSteps; step++)
        {
            new PropagateEmptyLightJob
            {
                voxelData    = voxelNative,
                currentLight = cur,
                nextLight    = nxt,
                sizeX = chunkSize.x, sizeY = chunkSize.y, sizeZ = chunkSize.z,
                lightDecay   = lightDecay
            }.Schedule(total, 64).Complete();

            var tmp = cur; cur = nxt; nxt = tmp;
        }

        var solidSrc = new NativeArray<float>(cur, Allocator.TempJob);
        var solidDst = new NativeArray<float>(cur, Allocator.TempJob);

        new UpdateSolidVoxelLightingJob
        {
            voxelData            = voxelNative,
            sourceLightGrid      = solidSrc,
            targetLightGrid      = solidDst,
            lightSources         = sources,
            sizeX = chunkSize.x, sizeY = chunkSize.y, sizeZ = chunkSize.z,
            lightSourceIntensity = lightSourceIntensity
        }.Schedule(total, 64).Complete();

        solidDst.CopyTo(lightGridFlat);

        solidDst.Dispose(); solidSrc.Dispose();
        sources.Dispose(); nxt.Dispose(); cur.Dispose(); voxelNative.Dispose();

        ConvertFromFlatArray();
    }

    [BurstCompile]
    private struct PropagateEmptyLightJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte>  voxelData;
        [ReadOnly] public NativeArray<float> currentLight;
        public NativeArray<float> nextLight;
        public int sizeX, sizeY, sizeZ;
        public float lightDecay;

        public void Execute(int index)
        {
            if (voxelData[index] != 0) { nextLight[index] = currentLight[index]; return; }

            int yz = sizeY * sizeZ, x = index / yz, rem = index - x * yz, y = rem / sizeZ, z = rem - y * sizeZ;
            float mx = 0f;

            if (x > 0)         { int i = index - yz;   if (voxelData[i] == 0) mx = math.max(mx, currentLight[i]); }
            if (x < sizeX - 1) { int i = index + yz;   if (voxelData[i] == 0) mx = math.max(mx, currentLight[i]); }
            if (y > 0)         { int i = index - sizeZ; if (voxelData[i] == 0) mx = math.max(mx, currentLight[i]); }
            if (y < sizeY - 1) { int i = index + sizeZ; if (voxelData[i] == 0) mx = math.max(mx, currentLight[i]); }
            if (z > 0)         { int i = index - 1;     if (voxelData[i] == 0) mx = math.max(mx, currentLight[i]); }
            if (z < sizeZ - 1) { int i = index + 1;     if (voxelData[i] == 0) mx = math.max(mx, currentLight[i]); }

            nextLight[index] = math.max(currentLight[index], math.max(0f, mx - lightDecay));
        }
    }

    [BurstCompile]
    private struct UpdateSolidVoxelLightingJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte>  voxelData;
        [ReadOnly] public NativeArray<float> sourceLightGrid;
        public NativeArray<float> targetLightGrid;
        [ReadOnly] public NativeParallelHashSet<int> lightSources;
        public int sizeX, sizeY, sizeZ;
        public float lightSourceIntensity;

        public void Execute(int index)
        {
            if (voxelData[index] == 0) return;
            if (lightSources.Contains(index)) { targetLightGrid[index] = lightSourceIntensity; return; }

            int yz = sizeY * sizeZ, x = index / yz, rem = index - x * yz, y = rem / sizeZ, z = rem - y * sizeZ;
            float mx = 0f;

            if (x > 0)         { int i = index - yz;   if (voxelData[i] == 0) mx = math.max(mx, sourceLightGrid[i]); }
            if (x < sizeX - 1) { int i = index + yz;   if (voxelData[i] == 0) mx = math.max(mx, sourceLightGrid[i]); }
            if (y > 0)         { int i = index - sizeZ; if (voxelData[i] == 0) mx = math.max(mx, sourceLightGrid[i]); }
            if (y < sizeY - 1) { int i = index + sizeZ; if (voxelData[i] == 0) mx = math.max(mx, sourceLightGrid[i]); }
            if (z > 0)         { int i = index - 1;     if (voxelData[i] == 0) mx = math.max(mx, sourceLightGrid[i]); }
            if (z < sizeZ - 1) { int i = index + 1;     if (voxelData[i] == 0) mx = math.max(mx, sourceLightGrid[i]); }

            targetLightGrid[index] = math.max(sourceLightGrid[index], mx * 0.7f);
        }
    }

    // -------------------------------------------------------------------------
    // Mesh building (thread-safe — returns plain arrays, no Unity API)
    // -------------------------------------------------------------------------

    private MeshData BuildFlatLitMesh()
    {
        int total           = chunkSize.x * chunkSize.y * chunkSize.z;
        int estimatedFaces  = total / 4;

        var verts  = new List<Vector3>(estimatedFaces * 4);
        var tris   = new List<int>    (estimatedFaces * 6);
        var uvs    = new List<Vector2>(estimatedFaces * 4);
        var colors = new List<Color>  (estimatedFaces * 4);
        var norms  = new List<Vector3>(estimatedFaces * 4);

        for (int i = 0; i < total; i++)
        {
            if (voxelDataArray[i] == 0) continue;
            Vector3Int c = IndexToCoord(i);
            AddFlatLitFaces(c.x, c.y, c.z, verts, tris, uvs, colors, norms);
        }

        return new MeshData
        {
            vertices  = verts.ToArray(),
            triangles = tris.ToArray(),
            uv        = uvs.ToArray(),
            colors    = colors.ToArray(),
            normals   = norms.ToArray()
        };
    }

    private MeshData BuildSmoothLitMesh()
    {
        int total          = chunkSize.x * chunkSize.y * chunkSize.z;
        int estimatedFaces = total / 4;

        var verts  = new List<Vector3>(estimatedFaces * 4);
        var tris   = new List<int>    (estimatedFaces * 6);
        var uvs    = new List<Vector2>(estimatedFaces * 4);
        var colors = new List<Color>  (estimatedFaces * 4);
        var norms  = new List<Vector3>(estimatedFaces * 4);

        for (int i = 0; i < total; i++)
        {
            if (voxelDataArray[i] == 0) continue;
            Vector3Int c = IndexToCoord(i);
            AddSmoothLitFaces(c.x, c.y, c.z, verts, tris, uvs, colors, norms);
        }

        return new MeshData
        {
            vertices  = verts.ToArray(),
            triangles = tris.ToArray(),
            uv        = uvs.ToArray(),
            colors    = colors.ToArray(),
            normals   = norms.ToArray()
        };
    }

    private void AddFlatLitFaces(int x, int y, int z,
        List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Color> colors, List<Vector3> norms)
    {
        Vector3 off = new Vector3(x, y, z);
        bool isLight = lightSourceIndexCache.Contains(CoordToIndex(x, y, z));

        if (ShouldGenerateFace(x, y, z, Vector3Int.left))
        {
            byte mat = isLight ? MATERIAL_LIGHT : MATERIAL_WALL;
            float fl = GetFaceLightLevel(x, y, z, Vector3Int.left);
            AddFace(off, new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(0,0,1),
                    verts, tris, uvs, colors, norms, mat, false, fl, fl, fl, fl);
        }
        if (ShouldGenerateFace(x, y, z, Vector3Int.right))
        {
            byte mat = isLight ? MATERIAL_LIGHT : MATERIAL_WALL;
            float fl = GetFaceLightLevel(x, y, z, Vector3Int.right);
            AddFace(off, new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(1,0,0),
                    verts, tris, uvs, colors, norms, mat, false, fl, fl, fl, fl);
        }
        if (ShouldGenerateFace(x, y, z, Vector3Int.down))
        {
            byte mat = isLight ? MATERIAL_LIGHT : MATERIAL_FLOOR;
            float fl = GetFaceLightLevel(x, y, z, Vector3Int.down);
            AddFace(off, new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0),
                    verts, tris, uvs, colors, norms, mat, true, fl, fl, fl, fl);
        }
        if (ShouldGenerateFace(x, y, z, Vector3Int.up))
        {
            byte mat = isLight ? MATERIAL_LIGHT : MATERIAL_CEILING;
            float fl = GetFaceLightLevel(x, y, z, Vector3Int.up);
            AddFace(off, new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1),
                    verts, tris, uvs, colors, norms, mat, true, fl, fl, fl, fl);
        }
        if (ShouldGenerateFace(x, y, z, Vector3Int.back))
        {
            byte mat = isLight ? MATERIAL_LIGHT : MATERIAL_WALL;
            float fl = GetFaceLightLevel(x, y, z, Vector3Int.back);
            AddFace(off, new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0),
                    verts, tris, uvs, colors, norms, mat, false, fl, fl, fl, fl);
        }
        if (ShouldGenerateFace(x, y, z, Vector3Int.forward))
        {
            byte mat = isLight ? MATERIAL_LIGHT : MATERIAL_WALL;
            float fl = GetFaceLightLevel(x, y, z, Vector3Int.forward);
            AddFace(off, new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1),
                    verts, tris, uvs, colors, norms, mat, false, fl, fl, fl, fl);
        }
    }

    private void AddSmoothLitFaces(int x, int y, int z,
        List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Color> colors, List<Vector3> norms)
    {
        Vector3 off  = new Vector3(x, y, z);
        bool isLight = lightSourceIndexCache.Contains(CoordToIndex(x, y, z));
        byte wallMat = isLight ? MATERIAL_LIGHT : MATERIAL_WALL;

        if (ShouldGenerateFace(x, y, z, Vector3Int.left))
            AddFace(off, new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(0,0,1),
                    verts, tris, uvs, colors, norms, wallMat, false,
                    GetVL(x,y,z,Vector3Int.left), GetVL(x,y+1,z,Vector3Int.left),
                    GetVL(x,y+1,z+1,Vector3Int.left), GetVL(x,y,z+1,Vector3Int.left));

        if (ShouldGenerateFace(x, y, z, Vector3Int.right))
            AddFace(off, new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(1,0,0),
                    verts, tris, uvs, colors, norms, wallMat, false,
                    GetVL(x+1,y,z+1,Vector3Int.right), GetVL(x+1,y+1,z+1,Vector3Int.right),
                    GetVL(x+1,y+1,z,Vector3Int.right), GetVL(x+1,y,z,Vector3Int.right));

        if (ShouldGenerateFace(x, y, z, Vector3Int.down))
        {
            byte mat = isLight ? MATERIAL_LIGHT : MATERIAL_FLOOR;
            AddFace(off, new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0),
                    verts, tris, uvs, colors, norms, mat, true,
                    GetVL(x,y,z+1,Vector3Int.down), GetVL(x+1,y,z+1,Vector3Int.down),
                    GetVL(x+1,y,z,Vector3Int.down), GetVL(x,y,z,Vector3Int.down));
        }

        if (ShouldGenerateFace(x, y, z, Vector3Int.up))
        {
            byte mat = isLight ? MATERIAL_LIGHT : MATERIAL_CEILING;
            AddFace(off, new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1),
                    verts, tris, uvs, colors, norms, mat, true,
                    GetVL(x,y+1,z,Vector3Int.up), GetVL(x+1,y+1,z,Vector3Int.up),
                    GetVL(x+1,y+1,z+1,Vector3Int.up), GetVL(x,y+1,z+1,Vector3Int.up));
        }

        if (ShouldGenerateFace(x, y, z, Vector3Int.back))
            AddFace(off, new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0),
                    verts, tris, uvs, colors, norms, wallMat, false,
                    GetVL(x,y,z,Vector3Int.back), GetVL(x+1,y,z,Vector3Int.back),
                    GetVL(x+1,y+1,z,Vector3Int.back), GetVL(x,y+1,z,Vector3Int.back));

        if (ShouldGenerateFace(x, y, z, Vector3Int.forward))
            AddFace(off, new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1),
                    verts, tris, uvs, colors, norms, wallMat, false,
                    GetVL(x+1,y,z+1,Vector3Int.forward), GetVL(x,y,z+1,Vector3Int.forward),
                    GetVL(x,y+1,z+1,Vector3Int.forward), GetVL(x+1,y+1,z+1,Vector3Int.forward));
    }

    // Shorthand for GetVertexLightLevel with integer coords
    private float GetVL(int x, int y, int z, Vector3Int normal)
        => GetVertexLightLevel(new Vector3Int(x, y, z), normal);

    private float GetVertexLightLevel(Vector3Int vp, Vector3Int faceNormal)
    {
        if (vertexLightCache.TryGetValue(vp, out float cached)) return cached;

        float total = 0f;
        int   count = 0;

        for (int dx = -1; dx <= 0; dx++)
        for (int dy = -1; dy <= 0; dy++)
        for (int dz = -1; dz <= 0; dz++)
        {
            Vector3Int sp = new Vector3Int(
                Mathf.Clamp(vp.x + dx, 0, chunkSize.x - 1),
                Mathf.Clamp(vp.y + dy, 0, chunkSize.y - 1),
                Mathf.Clamp(vp.z + dz, 0, chunkSize.z - 1));
            total += lightGrid[sp.x, sp.y, sp.z];
            count++;
        }

        float v = count > 0 ? total / count : 0f;
        vertexLightCache[vp] = v;
        return v;
    }

    private void AddFace(Vector3 off, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
        List<Vector3> verts, List<int> tris, List<Vector2> uvs, List<Color> colors, List<Vector3> norms,
        byte materialID, bool isHorizontal,
        float l0, float l1, float l2, float l3)
    {
        int b = verts.Count;

        verts.Add(v0 + off); verts.Add(v1 + off); verts.Add(v2 + off); verts.Add(v3 + off);

        tris.Add(b); tris.Add(b+1); tris.Add(b+2);
        tris.Add(b+2); tris.Add(b+3); tris.Add(b);

        float w, h;
        if (isHorizontal)
        {
            w = Vector3.Distance(v0, v3);
            h = Vector3.Distance(v0, v1);
        }
        else
        {
            w = Vector3.Distance(v0, v1);
            h = Vector3.Distance(v0, v3);
        }
        uvs.Add(new Vector2(0, 0));
        uvs.Add(new Vector2(0, h * textureScale.y));
        uvs.Add(new Vector2(w * textureScale.x, h * textureScale.y));
        uvs.Add(new Vector2(w * textureScale.x, 0));

        Vector3 n = Vector3.Cross(v1 - v0, v2 - v1).normalized;
        if (isHorizontal && n.y < 0) n = -n;
        norms.Add(n); norms.Add(n); norms.Add(n); norms.Add(n);

        float mat = materialID * MATERIAL_ENCODE_SCALE;
        colors.Add(new Color(mat, l0, 0, 1));
        colors.Add(new Color(mat, l1, 0, 1));
        colors.Add(new Color(mat, l2, 0, 1));
        colors.Add(new Color(mat, l3, 0, 1));
    }

    // -------------------------------------------------------------------------
    // Face / voxel helpers
    // -------------------------------------------------------------------------

    private bool ShouldGenerateFace(int x, int y, int z, Vector3Int dir)
    {
        Vector3Int adj = new Vector3Int(x, y, z) + dir;
        if (IsInGrid(adj)) return !GetVoxel(adj.x, adj.y, adj.z);
        return !IsSolidInAdjacentChunk(x, y, z, dir);
    }

    private bool IsSolidInAdjacentChunk(int x, int y, int z, Vector3Int dir)
    {
        if (chunkManager == null)
        {
            Debug.LogError("[DungeonChunk] chunkManager is null.");
            return false;
        }

        Vector3Int adjChunk = chunkCoord;
        Vector3Int localPos = new Vector3Int(x, y, z);

        if      (dir.x == -1 && x == 0)               { adjChunk += Vector3Int.left;    localPos.x = chunkSize.x - 1; }
        else if (dir.x ==  1 && x == chunkSize.x - 1) { adjChunk += Vector3Int.right;   localPos.x = 0; }
        else if (dir.y == -1 && y == 0)               { adjChunk += Vector3Int.down;    localPos.y = chunkSize.y - 1; }
        else if (dir.y ==  1 && y == chunkSize.y - 1) { adjChunk += Vector3Int.up;      localPos.y = 0; }
        else if (dir.z == -1 && z == 0)               { adjChunk += Vector3Int.back;    localPos.z = chunkSize.z - 1; }
        else if (dir.z ==  1 && z == chunkSize.z - 1) { adjChunk += Vector3Int.forward; localPos.z = 0; }
        else return false;

        return chunkManager.TryGetVoxelData(adjChunk, localPos, out bool solid) && solid;
    }

    private float GetFaceLightLevel(int x, int y, int z, Vector3Int normal)
    {
        Vector3Int adj = new Vector3Int(x, y, z) + normal;
        if (IsInGrid(adj) && !GetVoxel(adj.x, adj.y, adj.z))
            return lightGrid[adj.x, adj.y, adj.z];
        return lightGrid[x, y, z];
    }

    private bool IsInGrid(Vector3Int p)
        => p.x >= 0 && p.x < chunkSize.x && p.y >= 0 && p.y < chunkSize.y && p.z >= 0 && p.z < chunkSize.z;

    private bool GetVoxel(int x, int y, int z)
    {
        if (x < 0 || x >= chunkSize.x || y < 0 || y >= chunkSize.y || z < 0 || z >= chunkSize.z) return false;
        return voxelDataArray[CoordToIndex(x, y, z)] != 0;
    }

    private int CoordToIndex(int x, int y, int z)
        => x * chunkSize.y * chunkSize.z + y * chunkSize.z + z;

    private Vector3Int IndexToCoord(int index)
    {
        int z = index % chunkSize.z;
        int y = (index / chunkSize.z) % chunkSize.y;
        int x = index / (chunkSize.y * chunkSize.z);
        return new Vector3Int(x, y, z);
    }

    private void ConvertFromFlatArray()
    {
        lightGrid = new float[chunkSize.x, chunkSize.y, chunkSize.z];
        int idx = 0;
        for (int x = 0; x < chunkSize.x; x++)
        for (int y = 0; y < chunkSize.y; y++)
        for (int z = 0; z < chunkSize.z; z++)
            lightGrid[x, y, z] = lightGridFlat[idx++];
    }

    private int GetChunkSeed()
        => worldSeed ^ (chunkCoord.x * 73856093) ^ (chunkCoord.y * 19349663) ^ (chunkCoord.z * 83492791);

    private int GetPositionHash(int x, int y, int z)
    {
        int h = 17;
        h = h * 31 + worldSeed;
        h = h * 31 + chunkCoord.x; h = h * 31 + chunkCoord.y; h = h * 31 + chunkCoord.z;
        h = h * 31 + x; h = h * 31 + y; h = h * 31 + z;
        return h;
    }

    private float GetDeterministicValue01(int x, int y, int z)
        => ((uint)GetPositionHash(x, y, z) & 0x00FFFFFF) / 16777215f;

    // -------------------------------------------------------------------------
    // Public utility
    // -------------------------------------------------------------------------

    public void Clear()
    {
        if (meshFilter   != null && meshFilter.mesh   != null) Destroy(meshFilter.mesh);
        if (meshCollider != null) meshCollider.sharedMesh = null;

        lightGrid = null;
        lightPositions.Clear();
        lightSourceIndexCache.Clear();
        voxelDataArray = null;
        lightGridFlat  = null;
        vertexLightCache.Clear();
        // voxelData is owned externally — do not dispose
    }

    public void UpdateBoundaryMeshes()
    {
        if (!voxelData.IsCreated) return;
        MeshData md = BuildMeshDataInternal(recalculateLighting: false);
        UploadMesh(md);
    }
}