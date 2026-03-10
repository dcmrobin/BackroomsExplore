using UnityEngine;
using System.Collections.Generic;

// ---------------------------------------------------------------------------
// Surface archetypes — what a surface fundamentally looks like.
// Each surface (wall/floor/ceiling) picks one independently.
// ---------------------------------------------------------------------------
public enum WallArchetype   { Plaster, Stone, Brick, Tile, Metal, Organic, Alien }
public enum FloorArchetype  { Carpet, Concrete, WoodPlanks, Tile, Stone, Grate, Organic }
public enum CeilingArchetype{ Plaster, Tile, Panels, Stone, Organic, Alien }

// ---------------------------------------------------------------------------
// BiomeDefinition
// ---------------------------------------------------------------------------
public class BiomeDefinition
{
    public int    id;
    public string name;

    // Noise-space centre — determines where in the world this biome appears
    public Vector3 noiseSpaceCentre;

    // Each surface has its own independently chosen archetype and colours
    public WallArchetype    wallArchetype;
    public FloorArchetype   floorArchetype;
    public CeilingArchetype ceilingArchetype;

    // Per-surface base colours — chosen to suit each archetype
    public Color wallBaseColor;
    public Color floorBaseColor;
    public Color ceilingBaseColor;

    // Per-surface accent colours — used for pattern details
    public Color wallAccentColor;
    public Color floorAccentColor;
    public Color ceilingAccentColor;

    // Per-surface pattern — each surface picks independently
    public BiomeDefinition.SurfacePattern wallPattern;
    public BiomeDefinition.SurfacePattern floorPattern;
    public BiomeDefinition.SurfacePattern ceilingPattern;

    // Per-surface grain — floors tend to be rougher, ceilings smoother
    public float wallGrain;
    public float floorGrain;
    public float ceilingGrain;

    // Per-surface pattern parameters
    public float wallPatternScale;
    public float floorPatternScale;
    public float ceilingPatternScale;
    public float wallPatternStrength;
    public float floorPatternStrength;
    public float ceilingPatternStrength;

    // Lighting
    public float lightPlacementChance;
    public float lightDecay;
    public int   lightPropagationSteps;
    public Color lightTint;

    // World generation — how rooms and corridors are shaped in this biome
    public float roomThreshold;          // noise value needed to seed a room
    public float roomExpansionThreshold; // noise value needed to expand a room face
    public int   roomSizeMin;            // minimum side length for a valid room
    public int   roomSizeMax;            // maximum side length before expansion stops
    public int   corridorWidth;          // carved corridor half-width (voxels)
    public int   corridorHeight;         // carved corridor height (voxels)

    // Pre-generated textures
    public Texture2D wallTexture;
    public Texture2D floorTexture;
    public Texture2D ceilingTexture;

    public enum SurfacePattern { Cracks, Veins, Spots, Stripes, Grid, None }
}

// ---------------------------------------------------------------------------
// BiomeGenerationSettings — all tunable ranges, shown in the Unity Inspector
// on the InfiniteChunkManager component. Every value that was previously
// hardcoded in GenerateBiomeAppearance now reads from here.
// ---------------------------------------------------------------------------
[System.Serializable]
public class BiomeGenerationSettings
{
    [Header("World Layout")]
    [Tooltip("Total number of distinct biomes to generate")]
    [Range(4, 64)]  public int biomeCount = 18;
    [Tooltip("Texture resolution for each generated surface texture (power of 2)")]
    public TextureResolution textureResolution = TextureResolution._256;

    [Header("Biome Size & Shape")]
    [Tooltip("World-space scale of the 3 noise axes used for Voronoi biome placement. " +
             "SMALLER value = LARGER biome regions (noise varies more slowly). " +
             "LARGER value = SMALLER, more fragmented biomes. " +
             "Try 0.005 (huge biomes) → 0.05 (small biomes). Default 0.015.")]
    [Range(0.001f, 0.1f)]
    public float noiseSampleScale = 0.015f;

    [Tooltip("How much each biome centre is jittered within its grid cell (0–1). " +
             "0 = biomes on a perfectly regular grid (uniform sizes). " +
             "1 = fully random placement (irregular, some very small biomes). " +
             "Default 0.9 gives natural irregular variation.")]
    [Range(0f, 1f)]
    public float biomeJitter = 0.9f;

    [Tooltip("Blend distance at biome borders (world units). Higher values create " +
             "a gradual transition zone; 0 = hard biome edges.")]
    [Range(0f, 200f)]
    public float biomeBorderBlend = 0f;

    [Header("Grain (surface noise roughness)")]
    public FloatRange wallGrain    = new FloatRange(0.2f, 0.7f);
    public FloatRange floorGrain   = new FloatRange(0.4f, 0.9f);
    public FloatRange ceilingGrain = new FloatRange(0.1f, 0.4f);

    [Header("Pattern Scale (UV frequency of cracks/veins/grid etc.)")]
    public FloatRange wallPatternScale    = new FloatRange(0.04f, 0.16f);
    public FloatRange floorPatternScale   = new FloatRange(0.05f, 0.20f);
    public FloatRange ceilingPatternScale = new FloatRange(0.03f, 0.13f);

    [Header("Pattern Strength (how visible the pattern overlay is)")]
    [Range(0,1)] public float wallPatternStrengthMin    = 0.2f;
    [Range(0,1)] public float wallPatternStrengthMax    = 0.75f;
    [Range(0,1)] public float floorPatternStrengthMin   = 0.2f;
    [Range(0,1)] public float floorPatternStrengthMax   = 0.75f;
    [Range(0,1)] public float ceilingPatternStrengthMin = 0.15f;
    [Range(0,1)] public float ceilingPatternStrengthMax = 0.60f;

    [Header("Lighting — Placement")]
    [Tooltip("Min/max fraction of ceiling voxels that become light sources")]
    public FloatRange lightPlacementChance = new FloatRange(0.05f, 0.35f);

    [Header("Lighting — Propagation Steps")]
    [Tooltip("Min steps. More steps = light travels further. Bias is towards Max.")]
    [Range(2, 25)] public int lightPropagationStepsMin = 3;
    [Tooltip("Max steps (distribution is biased towards this end)")]
    [Range(2, 25)] public int lightPropagationStepsMax = 25;

    [Header("Lighting — Decay")]
    [Tooltip("Multiplicative decay per propagation hop (lower = faster falloff). " +
             "This overrides the per-biome lightDecay field to use a consistent exponential model.")]
    public FloatRange lightDecay = new FloatRange(0.06f, 0.22f);

    [Header("Lighting — Tint")]
    [Tooltip("Chance that a biome's light colour matches its wall archetype hue")]
    [Range(0,1)] public float lightMatchesWallChance = 0.6f;
    [Tooltip("Chance that light is pure white (no saturation)")]
    [Range(0,1)] public float lightWhiteChance = 0.4f;
    [Tooltip("Saturation range when light is tinted")]
    public FloatRange lightSaturation = new FloatRange(0.2f, 0.6f);

    [Header("World Gen — Room Density")]
    [Tooltip("Noise threshold to seed a new room. Higher = fewer, sparser rooms")]
    public FloatRange roomThreshold          = new FloatRange(0.55f, 0.75f);
    [Tooltip("Noise threshold for room expansion. Usually slightly lower than roomThreshold")]
    public FloatRange roomExpansionThreshold = new FloatRange(0.45f, 0.65f);

    [Header("World Gen — Room Size")]
    [Tooltip("Minimum voxel side length for a room to be kept")]
    public IntRange roomSizeMin = new IntRange(3, 6);
    [Tooltip("Maximum voxel side length before expansion stops")]
    public IntRange roomSizeMax = new IntRange(12, 40);

    [Header("World Gen — Corridors")]
    [Tooltip("Corridor width in voxels (carved as a square cross-section)")]
    public IntRange corridorWidth  = new IntRange(2, 6);
    [Tooltip("Corridor height in voxels")]
    public IntRange corridorHeight = new IntRange(3, 6);
}

[System.Serializable]
public struct IntRange
{
    public int min;
    public int max;
    public IntRange(int min, int max) { this.min = min; this.max = max; }
    public int Random(System.Random rng) => min + rng.Next(max - min + 1);
    public int Clamp(int v) => Mathf.Clamp(v, min, max);
}

[System.Serializable]
public struct FloatRange
{
    public float min;
    public float max;
    public FloatRange(float min, float max) { this.min = min; this.max = max; }
    public float Lerp(float t) => Mathf.Lerp(min, max, t);
    public float Random(System.Random rng) => Lerp((float)rng.NextDouble());
}

public enum TextureResolution { _64 = 64, _128 = 128, _256 = 256, _512 = 512 }

// ---------------------------------------------------------------------------
// BiomeRegistry
// ---------------------------------------------------------------------------
public class BiomeRegistry
{
    private Vector3 noiseOffset0, noiseOffset1, noiseOffset2;
    private List<BiomeDefinition> biomes = new List<BiomeDefinition>();
    private int worldSeed;
    private BiomeGenerationSettings cfg;

    public IReadOnlyList<BiomeDefinition> AllBiomes => biomes;

    public void Initialize(int seed, BiomeGenerationSettings settings)
    {
        worldSeed = seed;
        cfg       = settings ?? new BiomeGenerationSettings();
        System.Random rng = new System.Random(seed);

        noiseOffset0 = RandomOffset(rng);
        noiseOffset1 = RandomOffset(rng);
        noiseOffset2 = RandomOffset(rng);

        GenerateBiomes(rng);
        GenerateAllTextures();
    }

    public BiomeDefinition GetBiomeAt(Vector3Int worldPos)
        => NearestBiome(SampleNoiseCoord(worldPos));

    public BiomeDefinition GetBiomeAt(Vector3 worldPos)
        => GetBiomeAt(new Vector3Int(
            Mathf.FloorToInt(worldPos.x),
            Mathf.FloorToInt(worldPos.y),
            Mathf.FloorToInt(worldPos.z)));

    // -----------------------------------------------------------------------
    // Biome generation
    // -----------------------------------------------------------------------

    private void GenerateBiomes(System.Random rng)
    {
        biomes.Clear();
        int count     = cfg.biomeCount;
        int gridDim   = Mathf.CeilToInt(Mathf.Pow(count, 1f / 3f));
        float cellSize = 1f / gridDim;
        int placed    = 0;

        for (int gx = 0; gx < gridDim && placed < count; gx++)
        for (int gy = 0; gy < gridDim && placed < count; gy++)
        for (int gz = 0; gz < gridDim && placed < count; gz++)
        {
            // Jitter: 0 = centre of cell, 1 = fully random within cell
            float jx = Mathf.Lerp(0.5f, (float)rng.NextDouble(), cfg.biomeJitter);
            float jy = Mathf.Lerp(0.5f, (float)rng.NextDouble(), cfg.biomeJitter);
            float jz = Mathf.Lerp(0.5f, (float)rng.NextDouble(), cfg.biomeJitter);
            float cx = Mathf.Clamp01((gx + jx) * cellSize);
            float cy = Mathf.Clamp01((gy + jy) * cellSize);
            float cz = Mathf.Clamp01((gz + jz) * cellSize);

            BiomeDefinition b = new BiomeDefinition
            {
                id               = placed,
                name             = $"Biome_{placed:D2}",
                noiseSpaceCentre = new Vector3(cx, cy, cz)
            };

            GenerateBiomeAppearance(b, rng);
            biomes.Add(b);
            placed++;
        }
    }

    private void GenerateBiomeAppearance(BiomeDefinition b, System.Random rng)
    {
        // Each surface picks its archetype independently.
        // "Normal" archetypes (plaster/carpet/concrete) are weighted heavily —
        // roughly 60% chance, with exotic/alien options rarer.
        b.wallArchetype    = PickWallArchetype(rng);
        b.floorArchetype   = PickFloorArchetype(rng);
        b.ceilingArchetype = PickCeilingArchetype(rng);

        // Generate colours appropriate to each archetype
        GenerateWallColors(b, rng);
        GenerateFloorColors(b, rng);
        GenerateCeilingColors(b, rng);

        // Each surface picks its own pattern independently
        b.wallPattern    = PickPatternForArchetype(b.wallArchetype,    rng);
        b.floorPattern   = PickPatternForArchetype(b.floorArchetype,   rng);
        b.ceilingPattern = PickPatternForArchetype(b.ceilingArchetype, rng);

        // Grain
        b.wallGrain    = cfg.wallGrain.Random(rng);
        b.floorGrain   = cfg.floorGrain.Random(rng);
        b.ceilingGrain = cfg.ceilingGrain.Random(rng);

        // Pattern scale
        b.wallPatternScale    = cfg.wallPatternScale.Random(rng);
        b.floorPatternScale   = cfg.floorPatternScale.Random(rng);
        b.ceilingPatternScale = cfg.ceilingPatternScale.Random(rng);

        // Pattern strength
        b.wallPatternStrength    = cfg.wallPatternStrengthMin    + (float)rng.NextDouble() * (cfg.wallPatternStrengthMax    - cfg.wallPatternStrengthMin);
        b.floorPatternStrength   = cfg.floorPatternStrengthMin   + (float)rng.NextDouble() * (cfg.floorPatternStrengthMax   - cfg.floorPatternStrengthMin);
        b.ceilingPatternStrength = cfg.ceilingPatternStrengthMin + (float)rng.NextDouble() * (cfg.ceilingPatternStrengthMax - cfg.ceilingPatternStrengthMin);

        // Lighting
        b.lightPlacementChance  = cfg.lightPlacementChance.Random(rng);
        b.lightDecay            = cfg.lightDecay.Random(rng);

        // Propagation steps: uniform random in [min,max], squared to bias towards max
        float t = (float)rng.NextDouble();
        int stepRange = Mathf.Max(0, cfg.lightPropagationStepsMax - cfg.lightPropagationStepsMin);
        b.lightPropagationSteps = cfg.lightPropagationStepsMin + Mathf.RoundToInt(t * t * stepRange);

        // Light tint
        float lightHue = (float)rng.NextDouble() < cfg.lightMatchesWallChance
            ? WallHue(b.wallArchetype, rng)
            : (float)rng.NextDouble();
        float lightSat = (float)rng.NextDouble() < cfg.lightWhiteChance
            ? 0f
            : cfg.lightSaturation.Random(rng);
        b.lightTint = Color.HSVToRGB(lightHue, lightSat, 1f);

        // World generation — room shape and corridor dimensions
        // Expansion threshold is always kept slightly below room threshold
        b.roomThreshold          = cfg.roomThreshold.Random(rng);
        b.roomExpansionThreshold = Mathf.Min(b.roomThreshold - 0.05f,
                                             cfg.roomExpansionThreshold.Random(rng));
        b.roomSizeMin    = cfg.roomSizeMin.Random(rng);
        b.roomSizeMax    = cfg.roomSizeMax.Random(rng);
        // Ensure max is always meaningfully larger than min
        b.roomSizeMax    = Mathf.Max(b.roomSizeMax, b.roomSizeMin + 4);
        b.corridorWidth  = cfg.corridorWidth.Random(rng);
        b.corridorHeight = cfg.corridorHeight.Random(rng);
        // Ensure corridor height is at least as wide as its width
        b.corridorHeight = Mathf.Max(b.corridorHeight, b.corridorWidth);
    }

    // -----------------------------------------------------------------------
    // Archetype pickers — biased towards normal/realistic options
    // -----------------------------------------------------------------------

    // Roll a weighted index. weights[] should sum to 1.
    private static int WeightedPick(float[] weights, System.Random rng)
    {
        float r = (float)rng.NextDouble();
        float acc = 0f;
        for (int i = 0; i < weights.Length - 1; i++)
        {
            acc += weights[i];
            if (r < acc) return i;
        }
        return weights.Length - 1;
    }

    private WallArchetype PickWallArchetype(System.Random rng)
    {
        // Plaster=30%, Stone=20%, Brick=20%, Tile=10%, Metal=8%, Organic=7%, Alien=5%
        float[] w = { 0.30f, 0.20f, 0.20f, 0.10f, 0.08f, 0.07f, 0.05f };
        return (WallArchetype)WeightedPick(w, rng);
    }

    private FloorArchetype PickFloorArchetype(System.Random rng)
    {
        // Carpet=30%, Concrete=20%, WoodPlanks=20%, Tile=12%, Stone=10%, Grate=5%, Organic=3%
        float[] w = { 0.30f, 0.20f, 0.20f, 0.12f, 0.10f, 0.05f, 0.03f };
        return (FloorArchetype)WeightedPick(w, rng);
    }

    private CeilingArchetype PickCeilingArchetype(System.Random rng)
    {
        // Plaster=40%, Tile=20%, Panels=18%, Stone=12%, Organic=6%, Alien=4%
        float[] w = { 0.40f, 0.20f, 0.18f, 0.12f, 0.06f, 0.04f };
        return (CeilingArchetype)WeightedPick(w, rng);
    }

    // Pick a pattern that makes sense for a given archetype.
    // Uses an overloaded method per archetype enum type.
    private BiomeDefinition.SurfacePattern PickPatternForArchetype(WallArchetype a, System.Random rng)
    {
        switch (a)
        {
            case WallArchetype.Plaster:  return WeightedPattern(rng, none:0.3f, cracks:0.35f, spots:0.2f, veins:0.15f);
            case WallArchetype.Stone:    return WeightedPattern(rng, cracks:0.5f, veins:0.3f, none:0.2f);
            case WallArchetype.Brick:    return WeightedPattern(rng, grid:0.7f, stripes:0.2f, none:0.1f);
            case WallArchetype.Tile:     return WeightedPattern(rng, grid:0.6f, stripes:0.3f, none:0.1f);
            case WallArchetype.Metal:    return WeightedPattern(rng, stripes:0.4f, grid:0.35f, cracks:0.25f);
            case WallArchetype.Organic:  return WeightedPattern(rng, veins:0.5f, spots:0.3f, cracks:0.2f);
            default:                     return WeightedPattern(rng, veins:0.4f, spots:0.3f, cracks:0.3f);
        }
    }

    private BiomeDefinition.SurfacePattern PickPatternForArchetype(FloorArchetype a, System.Random rng)
    {
        switch (a)
        {
            case FloorArchetype.Carpet:     return WeightedPattern(rng, none:0.4f, spots:0.3f, stripes:0.3f);
            case FloorArchetype.Concrete:   return WeightedPattern(rng, cracks:0.5f, none:0.3f, spots:0.2f);
            case FloorArchetype.WoodPlanks: return WeightedPattern(rng, stripes:0.7f, grid:0.2f, none:0.1f);
            case FloorArchetype.Tile:       return WeightedPattern(rng, grid:0.7f, stripes:0.2f, none:0.1f);
            case FloorArchetype.Stone:      return WeightedPattern(rng, cracks:0.4f, veins:0.3f, grid:0.3f);
            case FloorArchetype.Grate:      return WeightedPattern(rng, grid:0.8f, stripes:0.2f);
            default:                        return WeightedPattern(rng, veins:0.5f, spots:0.3f, cracks:0.2f);
        }
    }

    private BiomeDefinition.SurfacePattern PickPatternForArchetype(CeilingArchetype a, System.Random rng)
    {
        switch (a)
        {
            case CeilingArchetype.Plaster: return WeightedPattern(rng, none:0.5f, cracks:0.3f, spots:0.2f);
            case CeilingArchetype.Tile:    return WeightedPattern(rng, grid:0.7f, stripes:0.2f, none:0.1f);
            case CeilingArchetype.Panels:  return WeightedPattern(rng, grid:0.6f, stripes:0.3f, none:0.1f);
            case CeilingArchetype.Stone:   return WeightedPattern(rng, cracks:0.5f, veins:0.3f, none:0.2f);
            case CeilingArchetype.Organic: return WeightedPattern(rng, veins:0.5f, spots:0.3f, cracks:0.2f);
            default:                       return WeightedPattern(rng, veins:0.4f, spots:0.4f, cracks:0.2f);
        }
    }

    // Named-parameter helper so pattern weights read clearly at call sites
    private static BiomeDefinition.SurfacePattern WeightedPattern(System.Random rng,
        float none=0f, float cracks=0f, float veins=0f,
        float spots=0f, float stripes=0f, float grid=0f)
    {
        float total = none + cracks + veins + spots + stripes + grid;
        if (total <= 0f) return BiomeDefinition.SurfacePattern.None;
        float r = (float)rng.NextDouble() * total;
        if ((r -= none)    <= 0) return BiomeDefinition.SurfacePattern.None;
        if ((r -= cracks)  <= 0) return BiomeDefinition.SurfacePattern.Cracks;
        if ((r -= veins)   <= 0) return BiomeDefinition.SurfacePattern.Veins;
        if ((r -= spots)   <= 0) return BiomeDefinition.SurfacePattern.Spots;
        if ((r -= stripes) <= 0) return BiomeDefinition.SurfacePattern.Stripes;
        return BiomeDefinition.SurfacePattern.Grid;
    }

    // -----------------------------------------------------------------------
    // Per-archetype colour generation
    // -----------------------------------------------------------------------

    private void GenerateWallColors(BiomeDefinition b, System.Random rng)
    {
        float v = (float)rng.NextDouble();
        switch (b.wallArchetype)
        {
            case WallArchetype.Plaster:
                // Off-white, cream, light grey — low sat, high value
                b.wallBaseColor  = Color.HSVToRGB(0.08f + v * 0.06f, 0.05f + v * 0.1f, 0.75f + v * 0.2f);
                b.wallAccentColor = Color.HSVToRGB(0.07f, 0.08f, 0.55f);
                break;
            case WallArchetype.Stone:
                b.wallBaseColor  = Color.HSVToRGB(0.05f + v * 0.1f, 0.08f + v * 0.12f, 0.35f + v * 0.25f);
                b.wallAccentColor = Color.HSVToRGB(0.06f, 0.05f, 0.2f);
                break;
            case WallArchetype.Brick:
                b.wallBaseColor  = Color.HSVToRGB(0.04f + v * 0.05f, 0.5f + v * 0.2f, 0.4f + v * 0.2f);
                b.wallAccentColor = Color.HSVToRGB(0.06f, 0.2f, 0.25f);
                break;
            case WallArchetype.Tile:
                float tileHue = (float)rng.NextDouble();
                b.wallBaseColor  = Color.HSVToRGB(tileHue, 0.1f + v * 0.3f, 0.6f + v * 0.3f);
                b.wallAccentColor = Color.HSVToRGB(tileHue, 0.05f, 0.3f);
                break;
            case WallArchetype.Metal:
                b.wallBaseColor  = Color.HSVToRGB(0.55f + v * 0.1f, 0.05f + v * 0.1f, 0.3f + v * 0.35f);
                b.wallAccentColor = Color.HSVToRGB(0.55f, 0.1f, 0.15f);
                break;
            case WallArchetype.Organic:
                b.wallBaseColor  = Color.HSVToRGB(0.25f + v * 0.2f, 0.3f + v * 0.3f, 0.2f + v * 0.25f);
                b.wallAccentColor = Color.HSVToRGB(0.3f + v * 0.1f, 0.5f, 0.3f);
                break;
            default: // Alien
                float alienHue = (float)rng.NextDouble();
                b.wallBaseColor  = Color.HSVToRGB(alienHue, 0.5f + v * 0.4f, 0.2f + v * 0.4f);
                b.wallAccentColor = Color.HSVToRGB((alienHue + 0.5f) % 1f, 0.7f, 0.5f);
                break;
        }
    }

    private void GenerateFloorColors(BiomeDefinition b, System.Random rng)
    {
        float v = (float)rng.NextDouble();
        switch (b.floorArchetype)
        {
            case FloorArchetype.Carpet:
                float carpetHue = (float)rng.NextDouble();
                b.floorBaseColor  = Color.HSVToRGB(carpetHue, 0.3f + v * 0.4f, 0.25f + v * 0.3f);
                b.floorAccentColor = Color.HSVToRGB(carpetHue, 0.4f, 0.15f);
                break;
            case FloorArchetype.Concrete:
                b.floorBaseColor  = Color.HSVToRGB(0.05f + v * 0.05f, 0.03f + v * 0.06f, 0.35f + v * 0.2f);
                b.floorAccentColor = Color.HSVToRGB(0.05f, 0.04f, 0.2f);
                break;
            case FloorArchetype.WoodPlanks:
                b.floorBaseColor  = Color.HSVToRGB(0.07f + v * 0.04f, 0.4f + v * 0.25f, 0.3f + v * 0.3f);
                b.floorAccentColor = Color.HSVToRGB(0.06f, 0.5f, 0.18f);
                break;
            case FloorArchetype.Tile:
                float floorTileHue = (float)rng.NextDouble();
                b.floorBaseColor  = Color.HSVToRGB(floorTileHue, 0.08f + v * 0.2f, 0.5f + v * 0.35f);
                b.floorAccentColor = Color.HSVToRGB(floorTileHue, 0.05f, 0.25f);
                break;
            case FloorArchetype.Stone:
                b.floorBaseColor  = Color.HSVToRGB(0.06f + v * 0.08f, 0.06f + v * 0.1f, 0.25f + v * 0.2f);
                b.floorAccentColor = Color.HSVToRGB(0.06f, 0.04f, 0.15f);
                break;
            case FloorArchetype.Grate:
                b.floorBaseColor  = Color.HSVToRGB(0.55f, 0.08f + v * 0.1f, 0.2f + v * 0.3f);
                b.floorAccentColor = Color.HSVToRGB(0.55f, 0.05f, 0.1f);
                break;
            default: // Organic
                b.floorBaseColor  = Color.HSVToRGB(0.3f + v * 0.15f, 0.35f + v * 0.2f, 0.15f + v * 0.2f);
                b.floorAccentColor = Color.HSVToRGB(0.35f, 0.5f, 0.2f);
                break;
        }
    }

    private void GenerateCeilingColors(BiomeDefinition b, System.Random rng)
    {
        float v = (float)rng.NextDouble();
        switch (b.ceilingArchetype)
        {
            case CeilingArchetype.Plaster:
                b.ceilingBaseColor  = Color.HSVToRGB(0.08f + v * 0.05f, 0.03f + v * 0.07f, 0.8f + v * 0.15f);
                b.ceilingAccentColor = Color.HSVToRGB(0.08f, 0.05f, 0.6f);
                break;
            case CeilingArchetype.Tile:
                float ceilTileHue = (float)rng.NextDouble();
                b.ceilingBaseColor  = Color.HSVToRGB(ceilTileHue, 0.08f + v * 0.15f, 0.65f + v * 0.25f);
                b.ceilingAccentColor = Color.HSVToRGB(ceilTileHue, 0.04f, 0.35f);
                break;
            case CeilingArchetype.Panels:
                b.ceilingBaseColor  = Color.HSVToRGB(0.55f + v * 0.1f, 0.04f + v * 0.08f, 0.6f + v * 0.25f);
                b.ceilingAccentColor = Color.HSVToRGB(0.55f, 0.06f, 0.35f);
                break;
            case CeilingArchetype.Stone:
                b.ceilingBaseColor  = Color.HSVToRGB(0.06f + v * 0.08f, 0.05f + v * 0.1f, 0.4f + v * 0.2f);
                b.ceilingAccentColor = Color.HSVToRGB(0.06f, 0.04f, 0.2f);
                break;
            case CeilingArchetype.Organic:
                b.ceilingBaseColor  = Color.HSVToRGB(0.28f + v * 0.12f, 0.3f + v * 0.25f, 0.2f + v * 0.2f);
                b.ceilingAccentColor = Color.HSVToRGB(0.32f, 0.5f, 0.25f);
                break;
            default: // Alien
                float alienHue = (float)rng.NextDouble();
                b.ceilingBaseColor  = Color.HSVToRGB(alienHue, 0.4f + v * 0.3f, 0.25f + v * 0.3f);
                b.ceilingAccentColor = Color.HSVToRGB((alienHue + 0.5f) % 1f, 0.6f, 0.4f);
                break;
        }
    }

    // Returns a hue representative of the wall archetype, used for light tint
    private float WallHue(WallArchetype a, System.Random rng)
    {
        switch (a)
        {
            case WallArchetype.Plaster: return 0.08f + (float)rng.NextDouble() * 0.05f;
            case WallArchetype.Stone:   return 0.06f + (float)rng.NextDouble() * 0.06f;
            case WallArchetype.Brick:   return 0.04f + (float)rng.NextDouble() * 0.04f;
            case WallArchetype.Metal:   return 0.55f + (float)rng.NextDouble() * 0.1f;
            default:                    return (float)rng.NextDouble();
        }
    }

    // -----------------------------------------------------------------------
    // Texture generation — passes per-surface archetype info to generator
    // -----------------------------------------------------------------------

    private void GenerateAllTextures()
    {
        int size = (int)cfg.textureResolution;
        foreach (var b in biomes)
        {
            b.wallTexture    = ProceduralTextureGenerator.Generate(b, SurfaceType.Wall,    size);
            b.floorTexture   = ProceduralTextureGenerator.Generate(b, SurfaceType.Floor,   size);
            b.ceilingTexture = ProceduralTextureGenerator.Generate(b, SurfaceType.Ceiling, size);
        }
    }

    // -----------------------------------------------------------------------
    // Noise space
    // -----------------------------------------------------------------------

    private Vector3 SampleNoiseCoord(Vector3Int worldPos)
    {
        float wx = worldPos.x * cfg.noiseSampleScale;
        float wy = worldPos.y * cfg.noiseSampleScale;
        float wz = worldPos.z * cfg.noiseSampleScale;

        float a0 = (Mathf.PerlinNoise(wx + noiseOffset0.x, wy + noiseOffset0.y + wz * 0.7f)
                  + Mathf.PerlinNoise(wx + noiseOffset0.z, wz + noiseOffset0.x + wy * 0.7f)) * 0.5f;
        float a1 = (Mathf.PerlinNoise(wy + noiseOffset1.y, wz + noiseOffset1.z + wx * 0.7f)
                  + Mathf.PerlinNoise(wz + noiseOffset1.x, wx + noiseOffset1.y + wz * 0.7f)) * 0.5f;
        float a2 = (Mathf.PerlinNoise(wz + noiseOffset2.z, wx + noiseOffset2.x + wy * 0.7f)
                  + Mathf.PerlinNoise(wy + noiseOffset2.y, wy + noiseOffset2.z + wx * 0.7f)) * 0.5f;

        return new Vector3(a0, a1, a2);
    }

    private BiomeDefinition NearestBiome(Vector3 noiseCoord)
    {
        BiomeDefinition best = biomes[0];
        float bestDist       = float.MaxValue;
        foreach (var b in biomes)
        {
            float d = (b.noiseSpaceCentre - noiseCoord).sqrMagnitude;
            if (d < bestDist) { bestDist = d; best = b; }
        }
        return best;
    }

    private static Vector3 RandomOffset(System.Random rng)
        => new Vector3(
            (float)(rng.NextDouble() * 20000 - 10000),
            (float)(rng.NextDouble() * 20000 - 10000),
            (float)(rng.NextDouble() * 20000 - 10000));
}