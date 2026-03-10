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

    [Header("Lighting Defaults (overridden per biome)")]
    [SerializeField] private float lightPlacementChance = 0.2f;
    [SerializeField] private float lightDecay = 0.15f;
    [SerializeField] private int   lightPropagationSteps = 4;
    [SerializeField] private float lightSourceIntensity = 1.0f;
    [SerializeField] private bool  smoothLighting = false;

    [Header("Texture Scale")]
    [SerializeField] private Vector2 textureScale = Vector2.one;

    private MeshFilter   meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;

    // voxelData is owned by InfiniteChunkManager — never disposed here
    private NativeArray<byte> voxelData;
    private float[,,] lightGrid;
    private Vector3Int chunkSize;
    private List<Vector3Int>      lightPositions        = new List<Vector3Int>();
    private readonly HashSet<int> lightSourceIndexCache = new HashSet<int>();

    private InfiniteChunkManager chunkManager;
    private Vector3Int chunkCoord;
    private int        worldSeed;

    // Active biome — set before mesh build, used for textures and lighting
    private BiomeDefinition activeBiome;
    private BiomeRegistry   biomeRegistry;

    // Per-biome material instance — cached so we don't recreate every rebuild
    private Material biomeMaterial;

    private const byte  MATERIAL_WALL         = 0;
    private const byte  MATERIAL_FLOOR        = 1;
    private const byte  MATERIAL_CEILING      = 2;
    private const byte  MATERIAL_LIGHT        = 3;
    private const float MATERIAL_ENCODE_SCALE = 1f / 3f;

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

    public void SetChunkCoord(Vector3Int coord, int seed)
    {
        chunkCoord = coord;
        worldSeed  = seed;
    }

    public void SetChunkManager(InfiniteChunkManager manager) { chunkManager = manager; }
    public void SetBiomeRegistry(BiomeRegistry registry)      { biomeRegistry = registry; }
    public Vector3Int GetChunkCoord()        => chunkCoord;
    public Vector3   GetChunkWorldPosition() => transform.position;

    // -------------------------------------------------------------------------
    // Biome resolution
    // -------------------------------------------------------------------------

    // Determines which biome owns this chunk (using its world-centre position).
    // Called on the background thread — BiomeRegistry is read-only after init.
    private BiomeDefinition ResolveBiome()
    {
        if (biomeRegistry == null) return null;

        // Sample at chunk centre for a stable single-biome-per-chunk result
        Vector3Int chunkWorldOrigin = Vector3Int.Scale(chunkCoord, chunkSize);
        Vector3Int centre = chunkWorldOrigin + chunkSize / 2;
        return biomeRegistry.GetBiomeAt(centre);
    }

    // Applies the biome's lighting parameters to the active fields used during
    // light computation. Called on background thread before PlaceLights().
    private void ApplyBiomeLighting(BiomeDefinition biome)
    {
        if (biome == null) return;
        lightPlacementChance  = biome.lightPlacementChance;
        lightDecay            = biome.lightDecay;
        lightPropagationSteps = biome.lightPropagationSteps;
    }

    // Applies biome textures to the renderer material.
    // Must be called on the MAIN THREAD after UploadMesh.
    private void ApplyBiomeMaterial(BiomeDefinition biome)
    {
        if (biome == null || meshRenderer == null) return;

        // Reuse the material instance if the biome hasn't changed
        if (biomeMaterial == null || activeBiome?.id != biome.id)
        {
            if (biomeMaterial != null) Destroy(biomeMaterial);

            // Clone the shared chunk material so we can set per-biome textures
            Material src = chunkManager != null ? chunkManager.ChunkMaterial : null;
            biomeMaterial = src != null
                ? new Material(src)
                : new Material(Shader.Find("Standard"));

            if (biome.wallTexture    != null) biomeMaterial.SetTexture("_WallTex",  biome.wallTexture);
            if (biome.wallTexture    != null) biomeMaterial.SetTexture("_MainTex",  biome.wallTexture); // fallback for non-biome shaders
            if (biome.floorTexture   != null) biomeMaterial.SetTexture("_FloorTex", biome.floorTexture);
            if (biome.ceilingTexture != null) biomeMaterial.SetTexture("_CeilTex",  biome.ceilingTexture);

            // Tint the material slightly with the biome's wall colour so even
            // shaders that don't support _FloorTex/_CeilTex still look distinct.
            biomeMaterial.SetColor("_LightTint", biome.lightTint);
            biomeMaterial.color = Color.Lerp(Color.white, biome.wallBaseColor, 0.35f); // fallback tint

            activeBiome = biome;
        }

        meshRenderer.sharedMaterial = biomeMaterial;
    }

    // -------------------------------------------------------------------------
    // Public generation API
    // -------------------------------------------------------------------------

    public void GenerateMesh(NativeArray<byte> externalVoxelData)
    {
        voxelData = externalVoxelData;
        int count = chunkSize.x * chunkSize.y * chunkSize.z;
        voxelDataArray = new byte[count];
        voxelData.CopyTo(voxelDataArray);

        BiomeDefinition biome = ResolveBiome();
        ApplyBiomeLighting(biome);

        MeshData md = BuildMeshDataInternal(recalculateLighting: true);
        UploadMesh(md, biome);
    }

    // Step 1 (background thread): resolve biome, compute lighting, build mesh arrays
    public MeshData BuildMeshData(NativeArray<byte> externalVoxelData, out BiomeDefinition biome)
    {
        voxelData = externalVoxelData;
        int count = chunkSize.x * chunkSize.y * chunkSize.z;
        voxelDataArray = new byte[count];
        voxelData.CopyTo(voxelDataArray);

        biome = ResolveBiome();
        ApplyBiomeLighting(biome);

        return BuildMeshDataInternal(recalculateLighting: true);
    }

    // Legacy overload — keeps boundary rebuild path working without biome output
    public MeshData BuildMeshData(NativeArray<byte> externalVoxelData)
        => BuildMeshData(externalVoxelData, out _);

    // Step 2 (main thread): upload mesh and apply biome material
    public void UploadMesh(MeshData md, BiomeDefinition biome = null)
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
            bool needsUInt32  = md.vertices.Length > 65535;

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

            // Apply biome material (textures + tint) on main thread
            if (biome != null) ApplyBiomeMaterial(biome);

            // Bake physics collider off main thread
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

        // How it works:
        // 1. Collect all valid ceiling voxels (solid with open space below, open above).
        // 2. Divide the XZ plane into a grid of cells sized by minLightSpacing.
        //    Each cell can hold at most one light — this prevents ceiling floods.
        // 3. For each occupied cell, pick a candidate by deterministic hash and
        //    accept it only if its hash value < lightPlacementChance.
        //    lightPlacementChance now means "fraction of spacing cells that get a light"
        //    rather than "per-voxel probability", so the density is predictable.
        // 4. If the whole chunk has no lights at all, place one fallback light.

        // Minimum spacing between lights in voxels — derived from placement chance.
        // chance≈1 → spacing 2 (dense), chance≈0 → spacing very large (sparse).
        // We clamp to [2, 32] so it stays reasonable.
        int spacing = Mathf.Clamp(Mathf.RoundToInt(2f + (1f - lightPlacementChance) * 30f), 2, 32);

        // ceilCandidates[cellKey] = best candidate voxel for that XZ cell
        var cellCandidates = new Dictionary<long, Vector3Int>();
        int  fallbackCeilingCount = 0;
        Vector3Int fallbackPos    = Vector3Int.zero;
        int chunkSeed = GetChunkSeed();
        System.Random rng = new System.Random(chunkSeed);

        int totalVoxels = chunkSize.x * chunkSize.y * chunkSize.z;
        for (int i = 0; i < totalVoxels; i++)
        {
            if (voxelDataArray[i] == 0) continue;

            Vector3Int coord = IndexToCoord(i);
            int x = coord.x, y = coord.y, z = coord.z;

            bool isCeiling = (y == chunkSize.y - 1 || !GetVoxel(x, y + 1, z))
                           && (y > 0 && GetVoxel(x, y - 1, z));
            if (!isCeiling) continue;

            // Reservoir-sample one fallback across all ceiling voxels
            fallbackCeilingCount++;
            if (rng.Next(fallbackCeilingCount) == 0)
                fallbackPos = coord;

            // Map this voxel into a spacing-grid cell in world space
            Vector3Int world = Vector3Int.Scale(chunkCoord, chunkSize) + coord;
            int cellX = Mathf.FloorToInt(world.x / (float)spacing);
            int cellZ = Mathf.FloorToInt(world.z / (float)spacing);
            long cellKey = ((long)(cellX + 100000)) * 200001 + (cellZ + 100000);

            // Keep the voxel with the highest deterministic hash value per cell
            // (acts as a stable per-cell random pick without needing a list)
            float h = GetDeterministicValue01(x, y, z);
            if (!cellCandidates.TryGetValue(cellKey, out Vector3Int existing)
                || h > GetDeterministicValue01(existing.x, existing.y, existing.z))
            {
                cellCandidates[cellKey] = coord;
            }
        }

        // Accept each cell's best candidate based on lightPlacementChance
        foreach (var kvp in cellCandidates)
        {
            Vector3Int c = kvp.Value;
            if (GetDeterministicValue01(c.x, c.y, c.z) < lightPlacementChance)
                lightPositions.Add(c);
        }

        if (lightPositions.Count == 0 && fallbackCeilingCount > 0)
            lightPositions.Add(fallbackPos);

        lightSourceIndexCache.Clear();
        foreach (var lp in lightPositions)
            lightSourceIndexCache.Add(CoordToIndex(lp.x, lp.y, lp.z));
    }

    // -------------------------------------------------------------------------
    // Lighting propagation — single IJob, one sync point
    // -------------------------------------------------------------------------

    private void CalculateVoxelLightingOptimized()
    {
        int total       = chunkSize.x * chunkSize.y * chunkSize.z;
        var voxelNative = new NativeArray<byte>(voxelDataArray, Allocator.TempJob);
        var lightNative = new NativeArray<float>(total, Allocator.TempJob, NativeArrayOptions.ClearMemory);

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

    [BurstCompile]
    private struct PropagateAllStepsJob : IJob
    {
        public NativeArray<byte>  voxelData;
        public NativeArray<float> lightGrid;
        public int sizeX, sizeY, sizeZ, steps;
        public float lightDecay;   // kept for BiomeDefinition compatibility, unused below

        public void Execute()
        {
            int total = sizeX * sizeY * sizeZ;
            int yz    = sizeY * sizeZ;

            // Multiplicative decay per step: each hop multiplies light by this
            // factor. Produces exponential falloff (looks like real light) and
            // doesn't rely on the exact number of steps to converge.
            // A value of 0.85 means light halves every ~4.5 hops.
            const float DECAY = 0.85f;

            // Each "step" is two sweeps — one forward (+X+Y+Z) and one backward
            // (-X-Y-Z). This ensures light propagates equally in all 6 directions
            // regardless of iteration order. Without the reverse sweep, light
            // travelling against the iteration direction needs one full pass per
            // voxel and barely moves in the available steps.
            for (int step = 0; step < steps; step++)
            {
                // Forward sweep: i = 0 → total-1
                for (int i = 0; i < total; i++)
                {
                    if (voxelData[i] != 0) continue;
                    int x = i / yz, rem = i - x * yz, y = rem / sizeZ, z = rem - y * sizeZ;
                    float mx = 0f;
                    if (x > 0)         { int n = i-yz;   if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                    if (x < sizeX - 1) { int n = i+yz;   if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                    if (y > 0)         { int n = i-sizeZ; if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                    if (y < sizeY - 1) { int n = i+sizeZ; if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                    if (z > 0)         { int n = i-1;     if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                    if (z < sizeZ - 1) { int n = i+1;     if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                    if (mx > 0f) lightGrid[i] = math.max(lightGrid[i], mx * DECAY);
                }

                // Backward sweep: i = total-1 → 0
                for (int i = total - 1; i >= 0; i--)
                {
                    if (voxelData[i] != 0) continue;
                    int x = i / yz, rem = i - x * yz, y = rem / sizeZ, z = rem - y * sizeZ;
                    float mx = 0f;
                    if (x > 0)         { int n = i-yz;   if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                    if (x < sizeX - 1) { int n = i+yz;   if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                    if (y > 0)         { int n = i-sizeZ; if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                    if (y < sizeY - 1) { int n = i+sizeZ; if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                    if (z > 0)         { int n = i-1;     if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                    if (z < sizeZ - 1) { int n = i+1;     if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                    if (mx > 0f) lightGrid[i] = math.max(lightGrid[i], mx * DECAY);
                }
            }

            // Solid voxel face lighting: solid voxels inherit the brightest
            // adjacent air voxel's value (slightly dimmed) so that faces on
            // lit walls look correct rather than black.
            for (int i = 0; i < total; i++)
            {
                if (voxelData[i] == 0) continue;
                int x = i / yz, rem = i - x * yz, y = rem / sizeZ, z = rem - y * sizeZ;
                float mx = 0f;
                if (x > 0)         { int n = i-yz;   if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                if (x < sizeX - 1) { int n = i+yz;   if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                if (y > 0)         { int n = i-sizeZ; if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                if (y < sizeY - 1) { int n = i+sizeZ; if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                if (z > 0)         { int n = i-1;     if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                if (z < sizeZ - 1) { int n = i+1;     if (voxelData[n]==0) mx = math.max(mx, lightGrid[n]); }
                lightGrid[i] = math.max(lightGrid[i], mx * 0.8f);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Mesh building
    // -------------------------------------------------------------------------

    private MeshData BuildFlatLitMesh()
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
        Vector3 off    = new Vector3(x, y, z);
        bool    isLight = lightSourceIndexCache.Contains(CoordToIndex(x, y, z));

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
        Vector3 off    = new Vector3(x, y, z);
        bool    isLight = lightSourceIndexCache.Contains(CoordToIndex(x, y, z));
        byte    wallMat = isLight ? MATERIAL_LIGHT : MATERIAL_WALL;

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

    private float GetVL(int x, int y, int z)
    {
        if (IsInGrid(new Vector3Int(x, y, z)))
            return lightGrid[x, y, z];

        // Out of bounds — try neighbouring chunk
        if (chunkManager != null)
        {
            float neighbourLight = chunkManager.GetLightAtWorldPos(
                Vector3Int.Scale(chunkCoord, chunkSize) + new Vector3Int(x, y, z));
            if (neighbourLight >= 0f) return neighbourLight;
        }
        // Clamp fallback (better than black)
        return lightGrid[
            Mathf.Clamp(x, 0, chunkSize.x - 1),
            Mathf.Clamp(y, 0, chunkSize.y - 1),
            Mathf.Clamp(z, 0, chunkSize.z - 1)];
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
        if (isHorizontal) { w = Vector3.Distance(v0, v3); h = Vector3.Distance(v0, v1); }
        else              { w = Vector3.Distance(v0, v1); h = Vector3.Distance(v0, v3); }

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
        if (chunkManager == null) { Debug.LogError("[DungeonChunk] chunkManager is null."); return false; }

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
        if (IsInGrid(adj))
        {
            if (!GetVoxel(adj.x, adj.y, adj.z))
                return lightGrid[adj.x, adj.y, adj.z];
        }
        else if (chunkManager != null)
        {
            // Sample light from the adjacent chunk so faces on chunk boundaries
            // receive the correct light level rather than defaulting to the
            // source voxel's own (usually 0) value.
            float neighbourLight = chunkManager.GetLightAtWorldPos(
                Vector3Int.Scale(chunkCoord, chunkSize) + adj);
            if (neighbourLight >= 0f) return neighbourLight;
        }
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

    /// <summary>
    /// Returns the baked light value at a local-space position.
    /// Used by InfiniteChunkManager.GetLightAtWorldPos for cross-chunk lookups.
    /// Returns -1 if lighting hasn't been computed yet.
    /// </summary>
    public float GetLightValue(Vector3Int localPos)
    {
        if (lightGrid == null) return -1f;
        if (localPos.x < 0 || localPos.x >= chunkSize.x ||
            localPos.y < 0 || localPos.y >= chunkSize.y ||
            localPos.z < 0 || localPos.z >= chunkSize.z) return -1f;
        return lightGrid[localPos.x, localPos.y, localPos.z];
    }

    public void Clear()
    {
        if (meshFilter   != null && meshFilter.mesh   != null) Destroy(meshFilter.mesh);
        if (meshCollider != null) meshCollider.sharedMesh = null;
        if (biomeMaterial != null) { Destroy(biomeMaterial); biomeMaterial = null; }

        lightGrid    = null;
        activeBiome  = null;
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
        UploadMesh(md, activeBiome);
    }
}