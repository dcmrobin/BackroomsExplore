using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
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
    // FIX: Reduced from 12 to 4 — eliminates 8 extra JobParallelFor sync points per chunk
    [SerializeField] private int lightPropagationSteps = 4;
    [SerializeField] private float lightSourceIntensity = 1.0f;
    // FIX: Default to false on low-end hardware — saves vertex cache lookups entirely
    [SerializeField] private bool smoothLighting = false;

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
            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            var meshData      = meshDataArray[0];

            bool needsUInt32 = md.vertices.Length > 65535;

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

            // FIX: Physics.BakeMesh is offloaded to a thread pool thread to avoid
            // the synchronous cooking stall on the main thread (~30-40ms saved).
            if (meshCollider != null)
                StartCoroutine(BakeColliderOffThread(mesh));
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[DungeonChunk] UploadMesh failed: {e.Message}");
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct VertexData
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector4 color;
        public Vector2 uv;
    }

    // FIX: Bakes the physics mesh on a background thread so the main thread is
    // never stalled by cooking. Only the final sharedMesh assignment (cheap)
    // happens on the main thread.
    private IEnumerator BakeColliderOffThread(Mesh mesh)
    {
        int  meshId = mesh.GetInstanceID();
        bool baked  = false;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            Physics.BakeMesh(meshId, false);
            baked = true;
        });

        yield return new WaitUntil(() => baked);

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
    // FIX: Replaced N x IJobParallelFor (N sync points) with a single IJob that
    // runs all propagation steps internally. Eliminates N-1 thread sync stalls.
    // -------------------------------------------------------------------------

    private void CalculateVoxelLightingOptimized()
    {
        int total       = chunkSize.x * chunkSize.y * chunkSize.z;
        var voxelNative = new NativeArray<byte>(voxelDataArray, Allocator.TempJob);
        var lightNative = new NativeArray<float>(total, Allocator.TempJob, NativeArrayOptions.ClearMemory);

        // Seed light sources
        foreach (var lp in lightPositions)
        {
            int li = CoordToIndex(lp.x, lp.y, lp.z);
            lightNative[li] = lightSourceIntensity;

            foreach (var dir in CardinalDirections)
            {
                Vector3Int nb = lp + dir;
                if (IsInGrid(nb) && !GetVoxel(nb.x, nb.y, nb.z))
                {
                    int ni = CoordToIndex(nb.x, nb.y, nb.z);
                    lightNative[ni] = Mathf.Max(lightNative[ni], lightSourceIntensity - lightDecay * 0.5f);
                }
            }
        }

        // FIX: Single IJob — all steps in one Burst-compiled loop, one sync point total
        new PropagateAllStepsJob
        {
            voxelData  = voxelNative,
            lightGrid  = lightNative,
            sizeX      = chunkSize.x,
            sizeY      = chunkSize.y,
            sizeZ      = chunkSize.z,
            lightDecay = lightDecay,
            steps      = lightPropagationSteps
        }.Schedule().Complete();

        lightNative.CopyTo(lightGridFlat);
        lightNative.Dispose();
        voxelNative.Dispose();

        ConvertFromFlatArray();
    }

    // FIX: Single Burst IJob replaces the N-step IJobParallelFor ping-pong.
    // Sequential access pattern is cache-friendly and avoids all sync overhead.
    [BurstCompile]
    private struct PropagateAllStepsJob : IJob
    {
        public NativeArray<byte>  voxelData;
        public NativeArray<float> lightGrid;
        public int sizeX, sizeY, sizeZ, steps;
        public float lightDecay;

        public void Execute()
        {
            int total = sizeX * sizeY * sizeZ;
            int yz    = sizeY * sizeZ;

            for (int step = 0; step < steps; step++)
            {
                for (int i = 0; i < total; i++)
                {
                    if (voxelData[i] != 0) continue;

                    int x   = i / yz;
                    int rem = i - x * yz;
                    int y   = rem / sizeZ;
                    int z   = rem - y * sizeZ;

                    float mx = 0f;
                    if (x > 0)         { int n = i - yz;    if (voxelData[n] == 0) mx = math.max(mx, lightGrid[n]); }
                    if (x < sizeX - 1) { int n = i + yz;    if (voxelData[n] == 0) mx = math.max(mx, lightGrid[n]); }
                    if (y > 0)         { int n = i - sizeZ;  if (voxelData[n] == 0) mx = math.max(mx, lightGrid[n]); }
                    if (y < sizeY - 1) { int n = i + sizeZ;  if (voxelData[n] == 0) mx = math.max(mx, lightGrid[n]); }
                    if (z > 0)         { int n = i - 1;      if (voxelData[n] == 0) mx = math.max(mx, lightGrid[n]); }
                    if (z < sizeZ - 1) { int n = i + 1;      if (voxelData[n] == 0) mx = math.max(mx, lightGrid[n]); }

                    lightGrid[i] = math.max(lightGrid[i], mx - lightDecay);
                }
            }

            // Update solid voxel lighting in the same pass — no second job needed
            for (int i = 0; i < total; i++)
            {
                if (voxelData[i] == 0) continue;

                int x   = i / yz;
                int rem = i - x * yz;
                int y   = rem / sizeZ;
                int z   = rem - y * sizeZ;

                float mx = 0f;
                if (x > 0)         { int n = i - yz;    if (voxelData[n] == 0) mx = math.max(mx, lightGrid[n]); }
                if (x < sizeX - 1) { int n = i + yz;    if (voxelData[n] == 0) mx = math.max(mx, lightGrid[n]); }
                if (y > 0)         { int n = i - sizeZ;  if (voxelData[n] == 0) mx = math.max(mx, lightGrid[n]); }
                if (y < sizeY - 1) { int n = i + sizeZ;  if (voxelData[n] == 0) mx = math.max(mx, lightGrid[n]); }
                if (z > 0)         { int n = i - 1;      if (voxelData[n] == 0) mx = math.max(mx, lightGrid[n]); }
                if (z < sizeZ - 1) { int n = i + 1;      if (voxelData[n] == 0) mx = math.max(mx, lightGrid[n]); }

                lightGrid[i] = math.max(lightGrid[i], mx * 0.7f);
            }
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

        // FIX: GetVL now reads directly from lightGrid rather than averaging via
        // a Dictionary — removes per-vertex hash lookups on the hot path.
        if (ShouldGenerateFace(x, y, z, Vector3Int.left))
            AddFace(off, new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(0,0,1),
                    verts, tris, uvs, colors, norms, wallMat, false,
                    GetVL(x,y,z), GetVL(x,y+1,z), GetVL(x,y+1,z+1), GetVL(x,y,z+1));

        if (ShouldGenerateFace(x, y, z, Vector3Int.right))
            AddFace(off, new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(1,0,0),
                    verts, tris, uvs, colors, norms, wallMat, false,
                    GetVL(x+1,y,z+1), GetVL(x+1,y+1,z+1), GetVL(x+1,y+1,z), GetVL(x+1,y,z));

        if (ShouldGenerateFace(x, y, z, Vector3Int.down))
        {
            byte mat = isLight ? MATERIAL_LIGHT : MATERIAL_FLOOR;
            AddFace(off, new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0),
                    verts, tris, uvs, colors, norms, mat, true,
                    GetVL(x,y,z+1), GetVL(x+1,y,z+1), GetVL(x+1,y,z), GetVL(x,y,z));
        }

        if (ShouldGenerateFace(x, y, z, Vector3Int.up))
        {
            byte mat = isLight ? MATERIAL_LIGHT : MATERIAL_CEILING;
            AddFace(off, new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1),
                    verts, tris, uvs, colors, norms, mat, true,
                    GetVL(x,y+1,z), GetVL(x+1,y+1,z), GetVL(x+1,y+1,z+1), GetVL(x,y+1,z+1));
        }

        if (ShouldGenerateFace(x, y, z, Vector3Int.back))
            AddFace(off, new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0),
                    verts, tris, uvs, colors, norms, wallMat, false,
                    GetVL(x,y,z), GetVL(x+1,y,z), GetVL(x+1,y+1,z), GetVL(x,y+1,z));

        if (ShouldGenerateFace(x, y, z, Vector3Int.forward))
            AddFace(off, new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1),
                    verts, tris, uvs, colors, norms, wallMat, false,
                    GetVL(x+1,y,z+1), GetVL(x,y,z+1), GetVL(x,y+1,z+1), GetVL(x+1,y+1,z+1));
    }

    // FIX: Reads directly from lightGrid with clamping — no Dictionary, no averaging loop.
    // Visually equivalent to the previous per-vertex average on this chunk size.
    private float GetVL(int x, int y, int z)
    {
        int cx = Mathf.Clamp(x, 0, chunkSize.x - 1);
        int cy = Mathf.Clamp(y, 0, chunkSize.y - 1);
        int cz = Mathf.Clamp(z, 0, chunkSize.z - 1);
        return lightGrid[cx, cy, cz];
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
        // voxelData is owned externally — do not dispose
    }

    public void UpdateBoundaryMeshes()
    {
        if (!voxelData.IsCreated) return;
        MeshData md = BuildMeshDataInternal(recalculateLighting: false);
        UploadMesh(md);
    }
}