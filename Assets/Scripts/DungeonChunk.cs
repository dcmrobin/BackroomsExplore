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
    [SerializeField] private float lightDecay = 0.88f;
    [SerializeField] private int   lightPropagationSteps = 12;
    [SerializeField] private float lightSourceIntensity = 1.0f;
    [SerializeField] private bool  smoothLighting = true;

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

    // Neighbour boundary light seeds — set by InfiniteChunkManager before rebuild
    // so that light from adjacent chunks bleeds correctly across borders.
    // Format: [face index 0..5][flat index within that face slice]
    // Face order matches CardinalDirections: +X,-X,+Y,-Y,+Z,-Z
    private float[][] neighbourLightSeeds = null;
    // Pre-baked border voxel rows from 6 neighbours — set on main thread before dispatch
    // so the background thread never needs to call TryGetVoxelData.
    private bool[][] neighbourBorderVoxels = null;

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

    // Called by InfiniteChunkManager on the MAIN THREAD before dispatching a
    // background build job. Injects all 6 neighbour boundary slices at once so
    // the background thread never needs to touch loadedChunks.
    public void SetAllNeighbourLightSeeds(float[][] seeds)
    {
        neighbourLightSeeds = seeds;
    }

    public void SetAllNeighbourBorderVoxels(bool[][] borders)
    {
        neighbourBorderVoxels = borders;
    }

    // Called by InfiniteChunkManager on the main thread after a neighbour chunk
    // finishes uploading. Stores the neighbour's boundary light slice so that
    // the next time this chunk rebuilds its lighting, light bleeds in correctly.
    public void SetNeighbourLightSeeds(Vector3Int fromDir, float[] boundarySlice)
    {
        if (neighbourLightSeeds == null)
            neighbourLightSeeds = new float[6][];

        // Map direction to face index (same order as CardinalDirections)
        // fromDir is the direction FROM this chunk TO the neighbour, so the
        // seed face is the face of THIS chunk that touches the neighbour.
        int faceIdx = DirectionToFaceIndex(fromDir);
        if (faceIdx >= 0)
        {
            neighbourLightSeeds[faceIdx] = boundarySlice;
            // Schedule a lighting-only rebuild (cheap — no mesh rebuild)
            if (chunkManager != null)
                chunkManager.MarkChunkForLightingRebuild(chunkCoord);
        }
    }

    // Pulls boundary light slices from all 6 cardinal neighbours that are already loaded
    // and seeds them into neighbourLightSeeds so the propagation job picks them up.
    // Safe to call from background thread — only reads already-baked float arrays.
    private void PullNeighbourLightSeeds()
    {
        if (chunkManager == null) return;
        if (neighbourLightSeeds == null)
            neighbourLightSeeds = new float[6][];

        int sx = chunkSize.x, sy = chunkSize.y, sz = chunkSize.z;
        Vector3Int origin = Vector3Int.Scale(chunkCoord, chunkSize);

        // For each of the 6 faces, sample the neighbour chunk's border voxels
        // using GetLightAtWorldPos (which returns -1 if not loaded / not lit yet).
        // We only overwrite a slot if the neighbour actually has data.
        Vector3Int[] dirs = {
            Vector3Int.right, Vector3Int.left,
            Vector3Int.up,    Vector3Int.down,
            Vector3Int.forward, Vector3Int.back
        };

        foreach (var dir in dirs)
        {
            int fi = DirectionToFaceIndex(dir);
            // World-space positions of the face voxels that sit just INSIDE the neighbour
            float[] slice;
            bool anyData = false;

            if (dir == Vector3Int.right || dir == Vector3Int.left)
            {
                int wx = dir == Vector3Int.right ? origin.x + sx : origin.x - 1;
                slice = new float[sy * sz];
                for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                {
                    float v = chunkManager.GetLightAtWorldPos(new Vector3Int(wx, origin.y + y, origin.z + z));
                    if (v >= 0f) { slice[y * sz + z] = v; anyData = true; }
                }
            }
            else if (dir == Vector3Int.up || dir == Vector3Int.down)
            {
                int wy = dir == Vector3Int.up ? origin.y + sy : origin.y - 1;
                slice = new float[sx * sz];
                for (int x = 0; x < sx; x++)
                for (int z = 0; z < sz; z++)
                {
                    float v = chunkManager.GetLightAtWorldPos(new Vector3Int(origin.x + x, wy, origin.z + z));
                    if (v >= 0f) { slice[x * sz + z] = v; anyData = true; }
                }
            }
            else // forward / back
            {
                int wz = dir == Vector3Int.forward ? origin.z + sz : origin.z - 1;
                slice = new float[sx * sy];
                for (int x = 0; x < sx; x++)
                for (int y = 0; y < sy; y++)
                {
                    float v = chunkManager.GetLightAtWorldPos(new Vector3Int(origin.x + x, origin.y + y, wz));
                    if (v >= 0f) { slice[x * sy + y] = v; anyData = true; }
                }
            }

            if (anyData)
                neighbourLightSeeds[fi] = slice;
        }
    }

    // Returns the light values on one face of this chunk (for the neighbour to seed from).
    // dir = direction of the face to export (+X, -X, +Y, -Y, +Z, -Z)
    public float[] ExportBoundaryLightSlice(Vector3Int dir)
    {
        if (lightGrid == null) return null;

        int sx = chunkSize.x, sy = chunkSize.y, sz = chunkSize.z;
        float[] slice;

        if (dir == Vector3Int.right)  { slice = new float[sy*sz]; for (int y=0;y<sy;y++) for (int z=0;z<sz;z++) slice[y*sz+z]=lightGrid[sx-1,y,z]; }
        else if (dir == Vector3Int.left)  { slice = new float[sy*sz]; for (int y=0;y<sy;y++) for (int z=0;z<sz;z++) slice[y*sz+z]=lightGrid[0,y,z]; }
        else if (dir == Vector3Int.up)    { slice = new float[sx*sz]; for (int x=0;x<sx;x++) for (int z=0;z<sz;z++) slice[x*sz+z]=lightGrid[x,sy-1,z]; }
        else if (dir == Vector3Int.down)  { slice = new float[sx*sz]; for (int x=0;x<sx;x++) for (int z=0;z<sz;z++) slice[x*sz+z]=lightGrid[x,0,z]; }
        else if (dir == Vector3Int.forward){ slice = new float[sx*sy]; for (int x=0;x<sx;x++) for (int y=0;y<sy;y++) slice[x*sy+y]=lightGrid[x,y,sz-1]; }
        else if (dir == Vector3Int.back)  { slice = new float[sx*sy]; for (int x=0;x<sx;x++) for (int y=0;y<sy;y++) slice[x*sy+y]=lightGrid[x,y,0]; }
        else slice = null;

        return slice;
    }

    private static int DirectionToFaceIndex(Vector3Int dir)
    {
        if (dir == Vector3Int.right)   return 0;
        if (dir == Vector3Int.left)    return 1;
        if (dir == Vector3Int.up)      return 2;
        if (dir == Vector3Int.down)    return 3;
        if (dir == Vector3Int.forward) return 4;
        if (dir == Vector3Int.back)    return 5;
        return -1;
    }

    private static readonly Vector3Int[] FaceIndexToDir = {
        Vector3Int.right, Vector3Int.left,
        Vector3Int.up,    Vector3Int.down,
        Vector3Int.forward, Vector3Int.back
    };

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

        // Neighbour light seeds are pre-baked on the main thread by
        // InfiniteChunkManager.CollectNeighbourLightSeeds() and injected via
        // SetAllNeighbourLightSeeds() before this background call.
        // PullNeighbourLightSeeds() was removed from here — it called
        // GetLightAtWorldPos which accessed loadedChunks from a background
        // thread, causing a race condition that made seeds silently return -1.
        return BuildMeshDataInternal(recalculateLighting: true);
    }

    // Legacy overload — keeps boundary rebuild path working without biome output
    public MeshData BuildMeshData(NativeArray<byte> externalVoxelData)
        => BuildMeshData(externalVoxelData, out _);

    // Boundary-rebuild path: takes a pre-copied managed byte[] so the background
    // thread never touches the NativeArray (which may be disposed on the main thread).
    public MeshData BuildMeshDataFromSnapshot(byte[] snapshot, out BiomeDefinition biome)
    {
        voxelDataArray = snapshot;

        biome = ResolveBiome();
        ApplyBiomeLighting(biome);

        return BuildMeshDataInternal(recalculateLighting: true);
    }

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

            // Ceiling = solid voxel with open air below it.
            // (The light hangs down from the ceiling into the room beneath.)
            bool isCeiling = (y > 0 && !GetVoxel(x, y - 1, z))
                           && (y == chunkSize.y - 1 || GetVoxel(x, y + 1, z));
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
        // Persistent allocator required — this runs on a ThreadPool thread which can
        // outlive the 4-frame TempJob lifetime, causing the "deleting allocation older
        // than permitted lifetime" errors. Persistent has no lifetime restriction.
        var voxelNative = new NativeArray<byte>(voxelDataArray, Allocator.Persistent);
        var lightNative = new NativeArray<float>(total, Allocator.Persistent, NativeArrayOptions.ClearMemory);

        // Seed from local light sources.
        // Light sources are solid ceiling voxels — the propagation job only runs on
        // air voxels, so we seed the air voxels immediately adjacent to each light.
        // The voxel directly below gets full intensity; the other 4 horizontal
        // neighbours get one decay step applied (they're one hop away).
        foreach (var lp in lightPositions)
        {
            // Solid voxel itself gets intensity so ExportBoundaryLightSlice sees it
            int li = CoordToIndex(lp.x, lp.y, lp.z);
            lightNative[li] = lightSourceIntensity;

            foreach (var dir in CardinalDirections)
            {
                Vector3Int nb = lp + dir;
                if (!IsInGrid(nb)) continue;
                if (GetVoxel(nb.x, nb.y, nb.z)) continue; // skip solid neighbours

                int ni = CoordToIndex(nb.x, nb.y, nb.z);
                // Voxel directly below the light = full intensity (the emitted face)
                // All other air neighbours = one decay step away
                float seedVal = (dir == Vector3Int.down)
                    ? lightSourceIntensity
                    : lightSourceIntensity * lightDecay;
                lightNative[ni] = Mathf.Max(lightNative[ni], seedVal);
            }
        }

        // Seed from neighbour chunk boundary slices.
        // Each seed value already represents light that has travelled some distance
        // in the neighbour — we apply one more decay step as it enters this chunk.
        if (neighbourLightSeeds != null)
        {
            int sx = chunkSize.x, sy = chunkSize.y, sz = chunkSize.z;
            for (int fi = 0; fi < 6; fi++)
            {
                float[] slice = neighbourLightSeeds[fi];
                if (slice == null) continue;
                Vector3Int faceDir = FaceIndexToDir[fi];

                // Walk the border face that touches the neighbour in faceDir direction
                if (faceDir == Vector3Int.right || faceDir == Vector3Int.left)
                {
                    int bx = faceDir == Vector3Int.right ? sx - 1 : 0;
                    for (int y = 0; y < sy && y*sz < slice.Length; y++)
                    for (int z = 0; z < sz && y*sz+z < slice.Length; z++)
                    {
                        float seedVal = slice[y*sz+z] * lightDecay;
                        if (seedVal <= 0.001f) continue;
                        int idx = CoordToIndex(bx, y, z);
                        if (voxelDataArray[idx] == 0)
                            lightNative[idx] = Mathf.Max(lightNative[idx], seedVal);
                    }
                }
                else if (faceDir == Vector3Int.up || faceDir == Vector3Int.down)
                {
                    int by = faceDir == Vector3Int.up ? sy - 1 : 0;
                    for (int x = 0; x < sx && x*sz < slice.Length; x++)
                    for (int z = 0; z < sz && x*sz+z < slice.Length; z++)
                    {
                        float seedVal = slice[x*sz+z] * lightDecay;
                        if (seedVal <= 0.001f) continue;
                        int idx = CoordToIndex(x, by, z);
                        if (voxelDataArray[idx] == 0)
                            lightNative[idx] = Mathf.Max(lightNative[idx], seedVal);
                    }
                }
                else // forward / back
                {
                    int bz = faceDir == Vector3Int.forward ? sz - 1 : 0;
                    for (int x = 0; x < sx && x*sy < slice.Length; x++)
                    for (int y = 0; y < sy && x*sy+y < slice.Length; y++)
                    {
                        float seedVal = slice[x*sy+y] * lightDecay;
                        if (seedVal <= 0.001f) continue;
                        int idx = CoordToIndex(x, y, bz);
                        if (voxelDataArray[idx] == 0)
                            lightNative[idx] = Mathf.Max(lightNative[idx], seedVal);
                    }
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
        public float lightDecay;   // per-hop multiplier, derived from biome falloff target

        public void Execute()
        {
            int total = sizeX * sizeY * sizeZ;
            int yz    = sizeY * sizeZ;

            // Use the biome-derived decay directly. It is computed as
            // falloffTarget^(1/steps) so light always reaches exactly
            // falloffTarget brightness at `steps` hops — no sharp cutoff.
            float DECAY = lightDecay;
            // Safety: clamp to a sane range in case of misconfiguration
            if (DECAY <= 0f || DECAY > 1f) DECAY = 0.85f;

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

    // =========================================================================
    // Greedy Meshing
    // =========================================================================
    // Merges adjacent coplanar faces that share the same material ID and
    // (for flat lighting) the same light level into single large quads.
    // On typical dungeon geometry this reduces vertex count by 60-80%.
    //
    // The algorithm processes each axis (X, Y, Z) in two passes (negative and
    // positive facing). For each 2D slice perpendicular to the axis:
    //   1. Build a mask of visible faces with their material/light key.
    //   2. Greedy-scan the mask: find the widest run in one dimension, then
    //      extend as far as possible in the other, mark used cells, emit quad.
    // =========================================================================

    // Key used to decide if two face slots can merge.
    private struct FaceKey : System.IEquatable<FaceKey>
    {
        public byte  mat;
        public float light; // quantised to 8 bits for merge tolerance
        public bool  valid;
        public bool Equals(FaceKey o) => valid && o.valid && mat == o.mat && light == o.light;
    }

    private MeshData BuildGreedyMesh(bool smooth)
    {
        int sx = chunkSize.x, sy = chunkSize.y, sz = chunkSize.z;
        int estimatedFaces = (sx * sy * sz) / 6;

        var verts  = new List<Vector3>(estimatedFaces * 4);
        var tris   = new List<int>    (estimatedFaces * 6);
        var uvs    = new List<Vector2>(estimatedFaces * 4);
        var colors = new List<Color>  (estimatedFaces * 4);
        var norms  = new List<Vector3>(estimatedFaces * 4);

        // We process 3 axes × 2 directions = 6 face orientations.
        // For each: (u,v) are the two axes in the slice plane; d is the normal axis.
        // Axis 0=X, 1=Y, 2=Z
        for (int axis = 0; axis < 3; axis++)
        {
            int uAxis = (axis + 1) % 3;
            int vAxis = (axis + 2) % 3;

            int dSize = axis == 0 ? sx : axis == 1 ? sy : sz;
            int uSize = uAxis == 0 ? sx : uAxis == 1 ? sy : sz;
            int vSize = vAxis == 0 ? sx : vAxis == 1 ? sy : sz;

            var mask    = new FaceKey[uSize * vSize];
            var lightV0 = new float  [uSize * vSize]; // per-vertex smooth light (corner 0)
            var lightV1 = new float  [uSize * vSize];
            var lightV2 = new float  [uSize * vSize];
            var lightV3 = new float  [uSize * vSize];

            int[] pos = new int[3];

            for (int facing = 0; facing < 2; facing++) // 0=negative, 1=positive normal
            {
                for (int d = 0; d < dSize; d++)
                {
                    // Build mask for this slice
                    for (int u = 0; u < uSize; u++)
                    for (int v = 0; v < vSize; v++)
                    {
                        pos[axis] = d;
                        pos[uAxis] = u;
                        pos[vAxis] = v;
                        int cx = pos[0], cy = pos[1], cz = pos[2];

                        // Is this voxel solid?
                        if (!GetVoxel(cx, cy, cz)) { mask[u*vSize+v] = default; continue; }

                        // Is there an open face in this direction?
                        int nx = cx, ny = cy, nz = cz;
                        if (axis == 0) nx += facing == 1 ? 1 : -1;
                        else if (axis == 1) ny += facing == 1 ? 1 : -1;
                        else nz += facing == 1 ? 1 : -1;

                        bool faceOpen;
                        if (nx >= 0 && nx < sx && ny >= 0 && ny < sy && nz >= 0 && nz < sz)
                            faceOpen = !GetVoxel(nx, ny, nz);
                        else
                        {
                            // Border — check adjacent chunk
                            Vector3Int dir = Vector3Int.zero;
                            if (axis == 0) dir.x = facing == 1 ? 1 : -1;
                            else if (axis == 1) dir.y = facing == 1 ? 1 : -1;
                            else dir.z = facing == 1 ? 1 : -1;
                            faceOpen = !IsSolidInAdjacentChunk(cx, cy, cz, dir);
                        }

                        if (!faceOpen) { mask[u*vSize+v] = default; continue; }

                        // Determine material
                        bool isLightSrc = lightSourceIndexCache.Contains(CoordToIndex(cx, cy, cz));
                        byte mat;
                        if (isLightSrc) mat = MATERIAL_LIGHT;
                        else if (axis == 1) mat = facing == 1 ? MATERIAL_CEILING : MATERIAL_FLOOR;
                        else mat = MATERIAL_WALL;

                        // Light — quantise to 1/255 steps so floats compare safely
                        float rawLight = smooth
                            ? GetVL(cx + (axis==0 ? (facing==1?1:-1) : 0),
                                    cy + (axis==1 ? (facing==1?1:-1) : 0),
                                    cz + (axis==2 ? (facing==1?1:-1) : 0))
                            : GetFaceLightLevel(cx, cy, cz, new Vector3Int(
                                axis==0?(facing==1?1:-1):0,
                                axis==1?(facing==1?1:-1):0,
                                axis==2?(facing==1?1:-1):0));
                        float quantLight = Mathf.Round(rawLight * 255f) / 255f;

                        mask[u*vSize+v] = new FaceKey { mat = mat, light = quantLight, valid = true };

                        // Store per-vertex smooth light for smooth mode.
                        // Each mask cell stores the light at its 4 corners in the
                        // air layer adjacent to this face (d + faceStep along normal).
                        // Corner layout:  V0=(u,v)  V1=(u,v+1)  V2=(u+1,v+1)  V3=(u+1,v)
                        // These match the quad corners emitted later so the greedy
                        // merge can read the correct corner value for any rect size.
                        if (smooth)
                        {
                            int faceStep = facing == 1 ? 1 : -1;
                            int ci = u * vSize + v;
                            int[] cp = new int[3];
                            // V0: corner at (u, v)
                            cp[axis]=d+faceStep; cp[uAxis]=u;   cp[vAxis]=v;   lightV0[ci]=GetVL(cp[0],cp[1],cp[2]);
                            // V1: corner at (u, v+1)
                            cp[axis]=d+faceStep; cp[uAxis]=u;   cp[vAxis]=v+1; lightV1[ci]=GetVL(cp[0],cp[1],cp[2]);
                            // V2: corner at (u+1, v+1)
                            cp[axis]=d+faceStep; cp[uAxis]=u+1; cp[vAxis]=v+1; lightV2[ci]=GetVL(cp[0],cp[1],cp[2]);
                            // V3: corner at (u+1, v)
                            cp[axis]=d+faceStep; cp[uAxis]=u+1; cp[vAxis]=v;   lightV3[ci]=GetVL(cp[0],cp[1],cp[2]);
                        }
                    }

                    // Greedy scan
                    for (int u = 0; u < uSize; u++)
                    for (int v = 0; v < vSize; )
                    {
                        FaceKey key = mask[u*vSize+v];
                        if (!key.valid) { v++; continue; }

                        // Find width along v
                        int w = 1;
                        while (v+w < vSize && mask[u*vSize+v+w].Equals(key)) w++;

                        // Find height along u
                        int h = 1;
                        bool done = false;
                        while (!done && u+h < uSize)
                        {
                            for (int k = 0; k < w; k++)
                                if (!mask[(u+h)*vSize+v+k].Equals(key)) { done = true; break; }
                            if (!done) h++;
                        }

                        // Emit quad
                        // Build the 4 corner positions in world space
                        float[] qp = new float[3];
                        qp[axis] = d + (facing == 1 ? 1 : 0);
                        qp[uAxis] = u;
                        qp[vAxis] = v;

                        Vector3 origin = new Vector3(qp[0], qp[1], qp[2]);

                        float[] qu = new float[3]; qu[uAxis] = h;
                        float[] qv = new float[3]; qv[vAxis] = w;

                        Vector3 uStep = new Vector3(qu[0], qu[1], qu[2]);
                        Vector3 vStep = new Vector3(qv[0], qv[1], qv[2]);

                        Vector3 p0 = origin;
                        Vector3 p1 = origin + uStep;
                        Vector3 p2 = origin + uStep + vStep;
                        Vector3 p3 = origin + vStep;

                        // Normal points INWARD (toward the hollow dungeon interior).
                        // facing==1 is the +axis face of a solid voxel → its open air side
                        // is in the +axis direction, so the inward normal is -axis.
                        // facing==0 is the -axis face → inward normal is +axis.
                        Vector3 normal = Vector3.zero;
                        normal[axis] = facing == 1 ? -1f : 1f;
                        // Winding: swap to keep front-face consistent with inward normals.
                        // Previously swapped on facing==0; now swap on facing==1.
                        if (facing == 1) { var tmp = p1; p1 = p3; p3 = tmp; }

                        // Light values at the 4 corners of the merged quad.
                        // Smooth: read from the correct per-corner array for each
                        // cell at the corner of the merged rectangle.
                        //   p0=(u,v)     → V0 of cell (u,   v  ) = lightV0
                        //   p1=(u+h,v)   → V3 of cell (u+h-1,v  ) = lightV3
                        //   p2=(u+h,v+w) → V2 of cell (u+h-1,v+w-1) = lightV2
                        //   p3=(u,v+w)   → V1 of cell (u,   v+w-1) = lightV1
                        float l0, l1, l2, l3;
                        if (smooth)
                        {
                            l0 = lightV0[u       * vSize + v    ];
                            l1 = lightV3[(u+h-1) * vSize + v    ];
                            l2 = lightV2[(u+h-1) * vSize + v+w-1];
                            l3 = lightV1[u       * vSize + v+w-1];
                        }
                        else { l0 = l1 = l2 = l3 = key.light; }

                        int b2 = verts.Count;
                        verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
                        tris.Add(b2); tris.Add(b2+1); tris.Add(b2+2);
                        tris.Add(b2+2); tris.Add(b2+3); tris.Add(b2);
                        norms.Add(normal); norms.Add(normal); norms.Add(normal); norms.Add(normal);

                        // UVs — scale by quad dimensions for correct texture tiling
                        float uLen = uStep.magnitude * textureScale.x;
                        float vLen = vStep.magnitude * textureScale.y;
                        uvs.Add(new Vector2(0,   0));
                        uvs.Add(new Vector2(uLen, 0));
                        uvs.Add(new Vector2(uLen, vLen));
                        uvs.Add(new Vector2(0,   vLen));

                        float matEnc = key.mat * MATERIAL_ENCODE_SCALE;
                        colors.Add(new Color(matEnc, l0, 0, 1));
                        colors.Add(new Color(matEnc, l1, 0, 1));
                        colors.Add(new Color(matEnc, l2, 0, 1));
                        colors.Add(new Color(matEnc, l3, 0, 1));

                        // Clear used region from mask
                        for (int hu = 0; hu < h; hu++)
                        for (int hv = 0; hv < w; hv++)
                            mask[(u+hu)*vSize+v+hv] = default;

                        v += w;
                    }
                }
            }
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

    // Keep old per-face helpers for reference (used by smooth vertex lookup in greedy)
    private MeshData BuildFlatLitMesh()  => BuildGreedyMesh(false);
    private MeshData BuildSmoothLitMesh() => BuildGreedyMesh(true);

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
        // Map direction to face index (matches FaceIndexToDir: +X=0,-X=1,+Y=2,-Y=3,+Z=4,-Z=5)
        int fi = -1;
        int bu = 0, bv = 0;
        if      (dir.x ==  1 && x == chunkSize.x - 1) { fi = 0; bu = y; bv = z; }
        else if (dir.x == -1 && x == 0)               { fi = 1; bu = y; bv = z; }
        else if (dir.y ==  1 && y == chunkSize.y - 1) { fi = 2; bu = x; bv = z; }
        else if (dir.y == -1 && y == 0)               { fi = 3; bu = x; bv = z; }
        else if (dir.z ==  1 && z == chunkSize.z - 1) { fi = 4; bu = x; bv = y; }
        else if (dir.z == -1 && z == 0)               { fi = 5; bu = x; bv = y; }
        else return false;

        // Use pre-baked data if available (thread-safe, set before background dispatch)
        if (neighbourBorderVoxels != null && fi < neighbourBorderVoxels.Length
            && neighbourBorderVoxels[fi] != null)
        {
            bool[] border = neighbourBorderVoxels[fi];
            // X-facing: u=y, v=z, stride=sz; Y-facing: u=x, v=z, stride=sz; Z-facing: u=x, v=y, stride=sy
            int stride = (fi <= 1) ? chunkSize.z : (fi <= 3) ? chunkSize.z : chunkSize.y;
            int idx = bu * stride + bv;
            if (idx >= 0 && idx < border.Length) return border[idx];
        }

        // Fallback for main-thread initial builds
        if (chunkManager == null) return false;
        Vector3Int adjChunk = chunkCoord + dir;
        Vector3Int localPos = new Vector3Int(
            dir.x == -1 ? chunkSize.x - 1 : (dir.x == 1 ? 0 : x),
            dir.y == -1 ? chunkSize.y - 1 : (dir.y == 1 ? 0 : y),
            dir.z == -1 ? chunkSize.z - 1 : (dir.z == 1 ? 0 : z));
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