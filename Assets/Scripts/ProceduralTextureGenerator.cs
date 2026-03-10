using UnityEngine;

public enum SurfaceType { Wall, Floor, Ceiling }

/// <summary>
/// Generates a Texture2D for a given biome surface.
///
/// Key design decisions:
///   - Grain uses value noise (random lattice + smooth interpolation) instead of
///     sin products. Sin products always produce a grid cross-pattern regardless
///     of rotation; value noise produces genuinely aperiodic organic variation.
///   - Seamless textures (~40% of biomes) use the 4D torus trick: sample 2D noise
///     along the surface of a torus in 4D space. This is mathematically seamless
///     with zero visible symmetry, unlike the cosine-mirror approach which folds.
///   - The vignette border is removed from the default path. Only a small fraction
///     of biomes (seed-gated) get a very faint depth hint, and it never goes dark.
///   - Every pattern parameter (frequency, rotation, phase, scale ratios) is
///     derived independently from textureSeed so no two biomes share structure.
/// </summary>
public static class ProceduralTextureGenerator
{
    public static Texture2D Generate(BiomeDefinition biome, SurfaceType surface, int size)
    {
        // Pull per-surface fields — each surface is fully independent
        Color baseColor;
        Color accentColor;
        BiomeDefinition.SurfacePattern pattern;
        float grain, patternScale, patternStrength;

        switch (surface)
        {
            case SurfaceType.Floor:
                baseColor       = biome.floorBaseColor;
                accentColor     = biome.floorAccentColor;
                pattern         = biome.floorPattern;
                grain           = biome.floorGrain;
                patternScale    = biome.floorPatternScale;
                patternStrength = biome.floorPatternStrength;
                break;
            case SurfaceType.Ceiling:
                baseColor       = biome.ceilingBaseColor;
                accentColor     = biome.ceilingAccentColor;
                pattern         = biome.ceilingPattern;
                grain           = biome.ceilingGrain;
                patternScale    = biome.ceilingPatternScale;
                patternStrength = biome.ceilingPatternStrength;
                break;
            default: // Wall
                baseColor       = biome.wallBaseColor;
                accentColor     = biome.wallAccentColor;
                pattern         = biome.wallPattern;
                grain           = biome.wallGrain;
                patternScale    = biome.wallPatternScale;
                patternStrength = biome.wallPatternStrength;
                break;
        }

        // Unique structural seed per biome position AND per surface type
        int surfaceOffset = (int)surface * 92837111;
        int textureSeed   = biome.id
            ^ (int)(biome.noiseSpaceCentre.x * 1000000f) * 374761393
            ^ (int)(biome.noiseSpaceCentre.y * 1000000f) * 1274126177
            ^ (int)(biome.noiseSpaceCentre.z * 1000000f) * 668265263
            ^ surfaceOffset;

        bool depthHint = ((uint)(textureSeed >> 8) % 100) < 25;

        Color[] pixels = new Color[size * size];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = baseColor;

        // Pass 1: Multi-octave value noise grain (4D torus — fully seamless)
        if (grain > 0.01f)
            ApplyGrain(pixels, size, grain, textureSeed, seamless: true);

        // Pass 2: Surface pattern using per-surface parameters and accent colour
        if (pattern != BiomeDefinition.SurfacePattern.None && patternStrength > 0.01f)
            ApplyPattern(pixels, size, pattern, patternScale * size, patternStrength,
                         accentColor, surface, textureSeed, seamless: true);

        // Pass 3: Optional faint low-frequency depth hint
        if (depthHint)
            ApplyDepthHint(pixels, size, textureSeed);

        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
        tex.SetPixels(pixels);
        tex.Apply(true);
        tex.wrapMode   = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        tex.anisoLevel = 4;
        return tex;
    }

    // =========================================================================
    // Grain: value noise fBm
    // Uses a hash-based random lattice with bicubic interpolation — produces
    // genuinely aperiodic results, unlike sin products which always tile at
    // their base frequency.
    // =========================================================================

    private static void ApplyGrain(Color[] pixels, int size,
                                    float roughness, int seed, bool seamless)
    {
        // Per-biome octave parameters derived entirely from seed
        float rot0   = Hash01(seed, 0, 1) * 6.2832f;
        float rot1   = Hash01(seed, 1, 2) * 6.2832f;
        float rot2   = Hash01(seed, 2, 3) * 6.2832f;
        float freq0  = 3f  + Hash01(seed, 3, 4) * 5f;   // 3–8
        float freq1  = 8f  + Hash01(seed, 4, 5) * 8f;   // 8–16
        float freq2  = 18f + Hash01(seed, 5, 6) * 10f;  // 18–28
        int   lseed0 = seed ^ 0x1A2B3C;
        int   lseed1 = seed ^ 0x4D5E6F;
        int   lseed2 = seed ^ 0x7A8B9C;

        float inv = 1f / size;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = x * inv;
            float v = y * inv;

            float n;
            if (seamless)
            {
                // 4D torus sampling: map (u,v) onto a torus surface in 4D so the
                // noise is periodic in both U and V with no mirror symmetry.
                n = TorusValueNoise(u, v, freq0, rot0, lseed0) * 0.55f
                  + TorusValueNoise(u, v, freq1, rot1, lseed1) * 0.30f
                  + TorusValueNoise(u, v, freq2, rot2, lseed2) * 0.15f;
            }
            else
            {
                n = RotatedValueNoise(u, v, freq0, rot0, lseed0) * 0.55f
                  + RotatedValueNoise(u, v, freq1, rot1, lseed1) * 0.30f
                  + RotatedValueNoise(u, v, freq2, rot2, lseed2) * 0.15f;
            }

            float delta = (n - 0.5f) * roughness * 0.55f;
            pixels[y * size + x] = AdjustBrightness(pixels[y * size + x], delta);
        }
    }

    // Value noise on a rotated grid — aperiodic, no sin cross-pattern
    private static float RotatedValueNoise(float u, float v,
                                            float freq, float rot, int seed)
    {
        float c = Mathf.Cos(rot), s = Mathf.Sin(rot);
        float ru = (u * c - v * s) * freq;
        float rv = (u * s + v * c) * freq;
        return SmoothedValueNoise(ru, rv, seed);
    }

    // 4D torus trick: map (u,v) to 4D via two independent circles, then sample
    // value noise in 4D. The result is seamless in both U and V, and because it's
    // not a simple mirror it has no bilateral symmetry.
    private static float TorusValueNoise(float u, float v,
                                          float freq, float rot, int seed)
    {
        float c = Mathf.Cos(rot), sr = Mathf.Sin(rot);
        float ru = (u * c - v * sr);
        float rv = (u * sr + v * c);

        // Map to torus surface: two circles of radius 1/(2π)
        float r = 1f / (2f * Mathf.PI);
        float x4 = r * Mathf.Cos(ru * 2f * Mathf.PI) * freq;
        float y4 = r * Mathf.Sin(ru * 2f * Mathf.PI) * freq;
        float z4 = r * Mathf.Cos(rv * 2f * Mathf.PI) * freq;
        float w4 = r * Mathf.Sin(rv * 2f * Mathf.PI) * freq;

        return SmoothedValueNoise4D(x4, y4, z4, w4, seed);
    }

    // 2D value noise with smooth (cubic) interpolation between lattice corners
    private static float SmoothedValueNoise(float x, float y, int seed)
    {
        int xi = Mathf.FloorToInt(x);
        int yi = Mathf.FloorToInt(y);
        float xf = x - xi;
        float yf = y - yi;

        float tx = xf * xf * (3f - 2f * xf); // smoothstep
        float ty = yf * yf * (3f - 2f * yf);

        float c00 = LatticeRand(xi,     yi,     seed);
        float c10 = LatticeRand(xi + 1, yi,     seed);
        float c01 = LatticeRand(xi,     yi + 1, seed);
        float c11 = LatticeRand(xi + 1, yi + 1, seed);

        return Mathf.Lerp(Mathf.Lerp(c00, c10, tx),
                          Mathf.Lerp(c01, c11, tx), ty);
    }

    // 4D value noise — used for seamless torus sampling
    private static float SmoothedValueNoise4D(float x, float y,
                                               float z, float w, int seed)
    {
        int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
        int zi = Mathf.FloorToInt(z), wi = Mathf.FloorToInt(w);
        float tx = SmoothT(x - xi), ty = SmoothT(y - yi);
        float tz = SmoothT(z - zi), tw = SmoothT(w - wi);

        // Trilinearly interpolate 16 lattice corners in 4D
        float v0 = Lerp4(LatticeRand4(xi,xi+1,yi,yi+1, zi,  wi,  seed,tx,ty),
                         LatticeRand4(xi,xi+1,yi,yi+1, zi,  wi+1,seed,tx,ty), tz);
        float v1 = Lerp4(LatticeRand4(xi,xi+1,yi,yi+1, zi+1,wi,  seed,tx,ty),
                         LatticeRand4(xi,xi+1,yi,yi+1, zi+1,wi+1,seed,tx,ty), tz);
        return Mathf.Lerp(v0, v1, tw);
    }

    private static float Lerp4(float a, float b, float t) => Mathf.Lerp(a, b, t);

    private static float LatticeRand4(int xi, int xi1, int yi, int yi1,
                                       int zi, int wi, int seed,
                                       float tx, float ty)
    {
        float c00 = LatticeRand4Pt(xi,  yi,  zi, wi, seed);
        float c10 = LatticeRand4Pt(xi1, yi,  zi, wi, seed);
        float c01 = LatticeRand4Pt(xi,  yi1, zi, wi, seed);
        float c11 = LatticeRand4Pt(xi1, yi1, zi, wi, seed);
        return Mathf.Lerp(Mathf.Lerp(c00, c10, tx),
                          Mathf.Lerp(c01, c11, tx), ty);
    }

    private static float LatticeRand4Pt(int x, int y, int z, int w, int seed)
    {
        int h = seed;
        h ^= x * 1664525  + 1013904223;
        h ^= y * 22695477 + 1;
        h ^= z * 214013   + 2531011;
        h ^= w * 6364136  + 1442695040;
        h ^= h >> 16;
        return ((uint)h & 0xFFFFFF) / 16777215f;
    }

    private static float SmoothT(float t) => t * t * (3f - 2f * t);

    // Hash a 2D lattice coordinate to a value in [0,1]
    private static float LatticeRand(int x, int y, int seed)
    {
        int h = seed;
        h ^= x * 1664525  + 1013904223;
        h ^= y * 22695477 + 1;
        h ^= h >> 16;
        return ((uint)h & 0xFFFFFF) / 16777215f;
    }

    // =========================================================================
    // Depth hint — a very gentle brightness variation across the face.
    // NOT a dark border — it's a flowing low-frequency wave that gives surfaces
    // some sense of depth without framing or vignetting each tile.
    // =========================================================================

    private static void ApplyDepthHint(Color[] pixels, int size, int seed)
    {
        float freq  = 1.2f + Hash01(seed, 90, 91) * 1.5f;
        float rot   = Hash01(seed, 92, 93) * 6.2832f;
        float phase = Hash01(seed, 94, 95) * 6.2832f;
        float amp   = 0.04f + Hash01(seed, 96, 97) * 0.04f; // max 0.08 brightness shift
        float inv   = 1f / size;
        float c = Mathf.Cos(rot), s = Mathf.Sin(rot);

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u  = x * inv, v = y * inv;
            float ru = u * c - v * s;
            float delta = Mathf.Sin(ru * freq * 6.2832f + phase) * amp;
            pixels[y * size + x] = AdjustBrightness(pixels[y * size + x], delta);
        }
    }

    // =========================================================================
    // Pattern dispatcher
    // =========================================================================

    private static void ApplyPattern(Color[] pixels, int size,
                                     BiomeDefinition.SurfacePattern pattern,
                                     float scale, float strength, Color accent,
                                     SurfaceType surface, int seed, bool seamless)
    {
        switch (pattern)
        {
            case BiomeDefinition.SurfacePattern.Cracks:
                ApplyCracks(pixels, size, scale, strength, accent, seed, seamless);   break;
            case BiomeDefinition.SurfacePattern.Veins:
                ApplyVeins(pixels, size, scale, strength, accent, seed, seamless);    break;
            case BiomeDefinition.SurfacePattern.Spots:
                ApplySpots(pixels, size, scale, strength, accent, seed);              break;
            case BiomeDefinition.SurfacePattern.Stripes:
                ApplyStripes(pixels, size, scale, strength, accent, surface, seed);   break;
            case BiomeDefinition.SurfacePattern.Grid:
                ApplyGrid(pixels, size, scale, strength, accent, seed);               break;
        }
    }

    // =========================================================================
    // Patterns
    // =========================================================================

    // --- Cracks ---
    // Two independently rotated and scaled Worley fields, blended with value
    // noise distortion so crack paths feel organic rather than cell-regular.
    private static void ApplyCracks(Color[] pixels, int size,
                                    float scale, float strength, Color accent,
                                    int seed, bool seamless)
    {
        float rot1   = Hash01(seed, 10, 11) * 3.14f;
        float rot2   = rot1 + 0.5f + Hash01(seed, 13, 14) * 1.0f;
        float scale2 = scale * (1.2f + Hash01(seed, 12, 13) * 0.6f);
        float distAmp = 0.08f + Hash01(seed, 15, 16) * 0.12f; // distortion amount
        float inv    = 1f / size;
        int   dseed  = seed ^ 0xABCDEF;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = x * inv, v = y * inv;

            // Distort UV with low-freq value noise to break cell regularity
            float du = (SmoothedValueNoise(u * 3.1f + 0.5f, v * 3.1f,       dseed) - 0.5f) * distAmp;
            float dv = (SmoothedValueNoise(u * 3.1f,        v * 3.1f + 0.5f, dseed ^ 0xFF) - 0.5f) * distAmp;
            float wu = seamless ? Fract(u + du) : u + du;
            float wv = seamless ? Fract(v + dv) : v + dv;

            float c1 = WorleyCrack(wu, wv, scale,  rot1, seed);
            float c2 = WorleyCrack(wu, wv, scale2, rot2, (int)(seed ^ 0xCAFE1234));
            float crack = Mathf.Max(c1, c2 * 0.55f);

            if (crack > 0.01f)
                pixels[y * size + x] = Color.Lerp(pixels[y * size + x],
                                                   accent * 0.4f, crack * strength);
        }
    }

    private static float WorleyCrack(float u, float v, float scale, float rot, int seed)
    {
        float c = Mathf.Cos(rot), s = Mathf.Sin(rot);
        float ru = (u * c - v * s) * scale;
        float rv = (u * s + v * c) * scale;
        float cx = Mathf.Floor(ru), cy = Mathf.Floor(rv);
        float minD = 1f;

        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            float fx = cx + dx + LatticeRand((int)(cx+dx), (int)(cy+dy), seed);
            float fy = cy + dy + LatticeRand((int)(cx+dx)+99, (int)(cy+dy)+37, seed);
            float ddx = ru - fx, ddy = rv - fy;
            minD = Mathf.Min(minD, Mathf.Sqrt(ddx*ddx + ddy*ddy));
        }
        return Mathf.Clamp01(1f - minD * 5f);
    }

    // --- Veins ---
    // Three families at independently seeded angles and frequencies.
    // Value noise distortion replaces the sin-distortion which caused regularity.
    private static void ApplyVeins(Color[] pixels, int size,
                                   float scale, float strength, Color accent,
                                   int seed, bool seamless)
    {
        float freq0  = scale * (0.3f + Hash01(seed, 20, 21) * 0.4f);
        float freq1  = scale * (0.5f + Hash01(seed, 21, 22) * 0.5f);
        float rot0   = Hash01(seed, 23, 24) * 6.2832f;
        float rot1   = Hash01(seed, 24, 25) * 6.2832f;
        float distAmp = 0.1f + Hash01(seed, 26, 27) * 0.15f;
        int   dseed  = seed ^ 0x123456;
        float inv    = 1f / size;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = x * inv, v = y * inv;

            // Value noise distortion — no sin, so no cross-pattern repetition
            float du = (SmoothedValueNoise(u * 4f, v * 4f,          dseed) - 0.5f) * distAmp;
            float dv = (SmoothedValueNoise(u * 4f + 3.7f, v * 4f,   dseed ^ 0xFF) - 0.5f) * distAmp;
            float wu = u + du, wv = v + dv;
            if (seamless) { wu = Fract(wu); wv = Fract(wv); }

            float c0 = Mathf.Cos(rot0), s0 = Mathf.Sin(rot0);
            float c1 = Mathf.Cos(rot1), s1 = Mathf.Sin(rot1);

            float ru0 = wu * c0 - wv * s0;
            float rv0 = wu * s0 + wv * c0;
            float ru1 = wu * c1 - wv * s1;
            float rv1 = wu * s1 + wv * c1;

            float v0 = Mathf.Clamp01(1f - Mathf.Abs(Mathf.Sin(ru0 * freq0 * 6.2832f
                                   + SmoothedValueNoise(rv0 * 2f, ru0 * 1.3f, dseed) * 2f)) * 4f);
            float v1 = Mathf.Clamp01(1f - Mathf.Abs(Mathf.Sin(ru1 * freq1 * 6.2832f
                                   + SmoothedValueNoise(rv1 * 2.5f, ru1 * 1.7f, dseed^0xAA) * 2f)) * 5f);

            float vein = Mathf.Max(v0, v1 * 0.6f);
            if (vein > 0.01f)
                pixels[y * size + x] = Color.Lerp(pixels[y * size + x],
                                                   accent, vein * strength * 0.65f);
        }
    }

    // --- Spots ---
    // Three size tiers: large blobs, medium spots, micro-dots.
    private static void ApplySpots(Color[] pixels, int size,
                                   float scale, float strength, Color accent, int seed)
    {
        System.Random rng = new System.Random((int)(seed ^ 0xBEEF1234));

        (float sMin, float sMax, float alpha, int mul)[] tiers = {
            (0.06f, 0.13f, 0.55f, 1),
            (0.025f, 0.06f, 0.40f, 2),
            (0.008f, 0.025f, 0.25f, 4),
        };

        int baseCount = Mathf.RoundToInt(scale * 8f);

        foreach (var (sMin, sMax, alpha, mul) in tiers)
        {
            for (int s = 0; s < baseCount * mul; s++)
            {
                float cx = (float)rng.NextDouble() * size;
                float cy = (float)rng.NextDouble() * size;
                float r  = size * (sMin + (float)rng.NextDouble() * (sMax - sMin));
                float r2 = r * r;

                int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - r));
                int x1 = Mathf.Min(size - 1, Mathf.CeilToInt(cx + r));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - r));
                int y1 = Mathf.Min(size - 1, Mathf.CeilToInt(cy + r));

                for (int py = y0; py <= y1; py++)
                for (int px = x0; px <= x1; px++)
                {
                    float dx = px - cx, dy = py - cy;
                    float t  = Mathf.Clamp01(1f - (dx*dx + dy*dy) / r2);
                    t = t * t;
                    if (t > 0.01f)
                        pixels[py * size + px] = Color.Lerp(pixels[py * size + px],
                                                             accent, t * strength * alpha);
                }
            }
        }
    }

    // --- Stripes ---
    // Two independently angled families at irrational frequency ratios so bands
    // never align into a visible grid when tiled.
    private static void ApplyStripes(Color[] pixels, int size,
                                     float scale, float strength, Color accent,
                                     SurfaceType surface, int seed)
    {
        float inv    = 1f / size;
        float rot0   = Hash01(seed, 30, 31) * 3.14f;
        float rot1   = rot0 + 0.6f + Hash01(seed, 32, 33) * 1.2f;
        float freq0  = scale;
        float freq1  = scale * (0.618f + Hash01(seed, 34, 35) * 0.5f); // golden-ratio offset
        float phase0 = Hash01(seed, 36, 37) * 6.2832f;
        float phase1 = Hash01(seed, 38, 39) * 6.2832f;
        float sharpA = 2f + Hash01(seed, 40, 41) * 3f; // band sharpness varies per biome
        float sharpB = 2f + Hash01(seed, 42, 43) * 4f;

        float c0 = Mathf.Cos(rot0), s0 = Mathf.Sin(rot0);
        float c1 = Mathf.Cos(rot1), s1 = Mathf.Sin(rot1);

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = x * inv, v = y * inv;
            float ru0 = u * c0 - v * s0;
            float ru1 = u * c1 - v * s1;

            float stripe0 = Mathf.Pow(Mathf.Abs(Mathf.Sin(ru0 * freq0 * 6.2832f + phase0)), sharpA);
            float stripe1 = Mathf.Pow(Mathf.Abs(Mathf.Sin(ru1 * freq1 * 6.2832f + phase1)), sharpB);
            float stripe  = Mathf.Max(stripe0, stripe1 * 0.5f);

            pixels[y * size + x] = Color.Lerp(pixels[y * size + x],
                                               accent, stripe * strength * 0.4f);
        }
    }

    // --- Grid ---
    // Two grids at different rotations and an irrational scale ratio so the
    // subgrid cells are never axis-aligned repeats of the main grid.
    private static void ApplyGrid(Color[] pixels, int size,
                                  float scale, float strength, Color accent, int seed)
    {
        float inv    = 1f / size;
        float lw     = 0.04f + Hash01(seed, 50, 51) * 0.04f; // line width varies
        float rot0   = Hash01(seed, 44, 45) * 0.52f;
        float rot1   = rot0 + 0.3f + Hash01(seed, 46, 47) * 0.5f;
        float scale1 = scale * (2.7f + Hash01(seed, 48, 49) * 1.0f); // subgrid
        float offU   = Hash01(seed, 52, 53);
        float offV   = Hash01(seed, 54, 55);

        float c0 = Mathf.Cos(rot0), s0 = Mathf.Sin(rot0);
        float c1 = Mathf.Cos(rot1), s1 = Mathf.Sin(rot1);

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = x * inv, v = y * inv;

            float ru0 = u * c0 - v * s0;
            float rv0 = u * s0 + v * c0;
            float ru1 = u * c1 - v * s1;
            float rv1 = u * s1 + v * c1;

            float u0 = Fract(ru0 * scale  + offU);
            float v0 = Fract(rv0 * scale  + offV);
            float u1 = Fract(ru1 * scale1 + offU * 1.7f);
            float v1 = Fract(rv1 * scale1 + offV * 1.7f);

            bool mainLine = u0 < lw || v0 < lw;
            bool subLine  = u1 < lw * 0.5f || v1 < lw * 0.5f;
            float t = mainLine ? strength * 0.55f : (subLine ? strength * 0.22f : 0f);
            if (t > 0f)
                pixels[y * size + x] = Color.Lerp(pixels[y * size + x], accent * 0.6f, t);
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static float Fract(float x) => x - Mathf.Floor(x);

    private static float Hash01(float a, float b, int seed)
    {
        int h = seed;
        h ^= (int)(a * 1000f) * 374761393;
        h ^= (int)(b * 1000f) * 1274126177;
        h ^= h >> 13;
        h *= -1037800021;
        h ^= h >> 15;
        return ((uint)h & 0x00FFFFFF) / 16777215f;
    }

    private static Color AdjustBrightness(Color c, float delta)
        => new Color(Mathf.Clamp01(c.r + delta),
                     Mathf.Clamp01(c.g + delta),
                     Mathf.Clamp01(c.b + delta),
                     c.a);
}