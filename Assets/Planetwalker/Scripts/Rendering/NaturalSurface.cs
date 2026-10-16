using System;
using System.Diagnostics;
using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Terrain steepness on a fixed spherical grid (V0.1.2A), independent of the terrain mesh: elevation
    /// sampled once per cell center (sea level floor, so coasts are not "cliffs"), gradient by least
    /// squares over the cell's neighbours (no extra elevation samples). Cached per terrain.
    /// Steepness unit: elevation (fraction of the radius) per radian along the surface.
    /// </summary>
    public sealed class TerrainSlopeField
    {
        public const int Resolution = 128;

        public TerrainSlopeField(HydrologyGrid grid, Func<Vector3, float> elevation, float seaLevel)
        {
            var watch = Stopwatch.StartNew();
            Grid = grid;
            int count = grid.CellCount;
            Elevation = new float[count];
            Slope = new float[count];
            for (int c = 0; c < count; c++)
            {
                Elevation[c] = Mathf.Max(elevation(grid.GetCenter(c)), seaLevel);
            }
            for (int c = 0; c < count; c++)
            {
                Vector3 p = grid.GetCenter(c);
                Vector3 e1 = Vector3.Cross(p, Mathf.Abs(p.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Vector3 e2 = Vector3.Cross(p, e1);
                // Least-squares gradient g minimizing sum (g . t_k - dh_k)^2 over the neighbour offsets t_k.
                float a11 = 0f, a12 = 0f, a22 = 0f, b1 = 0f, b2 = 0f;
                for (int k = grid.GetNeighborStart(c); k < grid.GetNeighborEnd(c); k++)
                {
                    int other = grid.GetNeighbor(k);
                    Vector3 t = grid.GetCenter(other) - p;
                    float x = Vector3.Dot(t, e1), y = Vector3.Dot(t, e2);
                    float dh = Elevation[other] - Elevation[c];
                    a11 += x * x;
                    a12 += x * y;
                    a22 += y * y;
                    b1 += x * dh;
                    b2 += y * dh;
                }
                float det = a11 * a22 - a12 * a12;
                if (Mathf.Abs(det) > 1e-20f)
                {
                    float gx = (a22 * b1 - a12 * b2) / det, gy = (a11 * b2 - a12 * b1) / det;
                    Slope[c] = Mathf.Sqrt(gx * gx + gy * gy);
                }
            }
            Milliseconds = (float)watch.Elapsed.TotalMilliseconds;
        }

        public HydrologyGrid Grid { get; }
        public readonly float[] Elevation;
        public readonly float[] Slope;
        public float Milliseconds { get; }

        /// <summary>Smooth steepness at a direction (cell and neighbours, weights (1 - d^2)^2 over 1.4 local cell spacings).</summary>
        public float Sample(Vector3 direction)
        {
            int cell = Grid.CellFromDirection(direction);
            float radius = 1.4f * Grid.LocalSpacing(direction); // inside the 8-neighbourhood everywhere
            float sum = 0f, weights = 0f;
            for (int k = Grid.GetNeighborStart(cell) - 1; k < Grid.GetNeighborEnd(cell); k++)
            {
                int other = k < Grid.GetNeighborStart(cell) ? cell : Grid.GetNeighbor(k);
                float d = (Grid.GetCenter(other) - direction).magnitude / radius;
                float w = Mathf.Max(0f, 1f - d * d);
                w = w * w + 1e-6f;
                sum += Slope[other] * w;
                weights += w;
            }
            return sum / weights;
        }
    }

    /// <summary>Slope field cache: rebuilt only when the terrain changes (never for mesh resolution or views).</summary>
    public sealed class TerrainSlopeCache
    {
        HydrologyGrid grid;
        PlanetDefinition cachedTerrain;
        TerrainSlopeField cached;

        public int ComputeCount { get; private set; }

        public TerrainSlopeField Get(PlanetInstance planet)
        {
            if (cached != null && cachedTerrain.TerrainEquals(planet.Definition))
            {
                return cached;
            }
            grid ??= new HydrologyGrid(TerrainSlopeField.Resolution);
            float spacing = Mathf.PI * 0.5f / grid.Resolution; // band-limited to the grid and the legacy world (V0.2.0C)
            cached = new TerrainSlopeField(grid, d => planet.GetLegacyElevation(d, spacing), planet.SeaLevel);
            cachedTerrain = planet.Definition.Clone();
            ComputeCount++;
            return cached;
        }

        public void Clear()
        {
            cached = null;
            cachedTerrain = null;
        }
    }

    /// <summary>
    /// Natural Planet surface colors (V0.1.2A): the artistic reading of the scientific layers. The biome
    /// system decides which environments exist; this decides how they look. Reads a
    /// <see cref="BiomeResult"/> (and its climate) and a <see cref="TerrainSlopeField"/>; changes nothing.
    /// </summary>
    /// <remarks>
    /// Per land vertex (a unit direction and its elevation; mesh independent):
    /// 1. Biome blend: interpolated suitability vector of the nearby land cells, sharpened as w^p
    ///    (p from 3 = crisp to 1.25 = smooth, Biome Blend), top three biomes only (no muddy mix of many),
    ///    weighted average of the palette colors. Ids are never interpolated; ocean never blended.
    /// 2. Multi-scale variation, continuous 3D noise on the unit sphere (no UV / face dependence):
    ///    regional (frequency 2.2, brightness and warmth), landscape (9, vegetation density / dry soil
    ///    patches on vegetated biomes), fine (40, slight brightness).
    /// 3. Terrain: bare rock on steep ground (smooth thresholds on the slope field, Rock Exposure),
    ///    mild desaturation and lightening with elevation, rock partly covered where the ice / snow
    ///    potential biome dominates. No snow simulation: the pale ice / snow color is a provisional
    ///    visualization of a climatic potential, not an asserted ice sheet.
    /// 4. Saturation.
    /// Underwater vertices keep the existing ocean-floor palette (the ocean sphere is drawn over them).
    /// </remarks>
    public sealed class NaturalSurface
    {
        // Palette (sRGB, restrained), index = Biome.
        static readonly Color[] Palette =
        {
            new Color(0.3f, 0.3f, 0.3f), // Ocean (never used on land)
            new Color(0.13f, 0.3f, 0.12f), // Tropical rainforest: deep rich forest green
            new Color(0.42f, 0.44f, 0.2f), // Tropical seasonal forest / savanna: warm olive, golden grass
            new Color(0.76f, 0.64f, 0.45f), // Hot desert: sand, muted amber
            new Color(0.48f, 0.48f, 0.33f), // Dry woodland / shrubland: sage, dusty olive
            new Color(0.6f, 0.57f, 0.38f), // Steppe: pale olive, straw
            new Color(0.6f, 0.57f, 0.5f), // Cold desert: grey-beige
            new Color(0.36f, 0.48f, 0.23f), // Temperate grassland: natural medium green
            new Color(0.2f, 0.33f, 0.16f), // Temperate forest: deep neutral green
            new Color(0.14f, 0.28f, 0.21f), // Temperate rainforest: dark blue-green
            new Color(0.16f, 0.26f, 0.18f), // Boreal forest: cool conifer green
            new Color(0.46f, 0.47f, 0.4f), // Tundra: grey-green, muted brown
            new Color(0.5f, 0.49f, 0.46f), // Alpine: stone grey, sparse vegetation
            new Color(0.84f, 0.86f, 0.89f), // Ice / snow potential: off-white, slightly cool (provisional)
        };

        // Vegetation cover of each biome (0 bare .. 1 closed canopy): drives dry soil patches.
        static readonly float[] Vegetation = { 0f, 1f, 0.7f, 0.05f, 0.5f, 0.45f, 0.1f, 0.8f, 1f, 1f, 1f, 0.4f, 0.2f, 0f };

        static readonly Color SoilColor = new Color(0.52f, 0.44f, 0.33f);
        static readonly Color RockColor = new Color(0.42f, 0.4f, 0.37f);

        // Land weight below which the interpolated suitability fades to the pointwise classification.
        const float FallbackWeight = 0.3f;

        readonly float[] scores = new float[Biomes.Count];
        readonly float[] pointScores = new float[Biomes.Count];
        readonly int regionalSeed, landscapeSeed, fineSeed, warmthSeed;
        readonly float exponent, rockStart, rockEnd;

        public NaturalSurface(BiomeResult biomes, TerrainSlopeField slope, NaturalSurfaceSettings settings, int seed)
        {
            BiomeData = biomes;
            SlopeField = slope;
            Settings = settings.Clone();
            Settings.Sanitize();
            Seed = seed;
            regionalSeed = PlanetNoise.DeriveSeed(seed, 9301);
            landscapeSeed = PlanetNoise.DeriveSeed(seed, 9302);
            fineSeed = PlanetNoise.DeriveSeed(seed, 9303);
            warmthSeed = PlanetNoise.DeriveSeed(seed, 9304);
            exponent = Mathf.Lerp(3f, 1.25f, Settings.biomeBlend);
            // Calibrated on the reference planets: land steepness p90 ~0.14, p99 ~0.35 (slope field 128).
            float scale = 0.5f + Settings.rockExposure; // 0.5 .. 1.5
            rockStart = 0.14f / scale;
            rockEnd = 0.42f / scale;
        }

        public BiomeResult BiomeData { get; }
        public TerrainSlopeField SlopeField { get; }
        public NaturalSurfaceSettings Settings { get; }
        public int Seed { get; }

        public static Color PaletteColor(Biome biome) => Palette[(int)biome];

        /// <summary>Rock coverage (0 .. 1) for a steepness: smooth, zero on flat ground.</summary>
        public float RockCover(float steepness)
        {
            float t = Mathf.Clamp01((steepness - rockStart) / (rockEnd - rockStart));
            return t * t * (3f - 2f * t) * 0.85f;
        }

        /// <summary>Natural color (sRGB) of a land point: unit direction and elevation (fraction of the radius).</summary>
        public Color Evaluate(Vector3 direction, float elevation)
        {
            return Evaluate(direction, elevation, scores, pointScores);
        }

        /// <summary>
        /// Same as <see cref="Evaluate(Vector3, float)"/> with caller-owned scratch buffers (<see cref="Biomes.Count"/>
        /// floats each): safe to call from several threads at once (V0.2.0B adaptive terrain workers). Everything
        /// else it reads is immutable.
        /// </summary>
        public Color Evaluate(Vector3 direction, float elevation, float[] scores, float[] pointScores)
        {
            ClimateResult climate = BiomeData.Climate;
            float landWeight = BiomeData.SampleScores(direction, scores);
            if (landWeight < FallbackWeight)
            {
                // Few or no land climate cells within reach (coasts, islets smaller than a climate cell):
                // fade towards a classification of the interpolated climate itself, so the color stays
                // continuous where the nearby land cells drop out of the blend.
                float height = Mathf.Max(0f, elevation - climate.SeaLevel) * 100f * climate.Settings.elevationScale;
                BiomeClassifier.Scores(climate.Sample(climate.Temperature, direction), climate.Sample(climate.Precipitation, direction),
                    climate.Settings.lapseRate * height, BiomeData.Settings, pointScores);
                float t = landWeight / FallbackWeight;
                for (int b = 1; b < Biomes.Count; b++)
                {
                    scores[b] = scores[b] * t + pointScores[b] * (1f - t);
                }
            }

            // 1. Top three biomes, sharpened suitability weights.
            int b1 = 0, b2 = 0, b3 = 0;
            float s1 = 0f, s2 = 0f, s3 = 0f, s4 = 0f;
            for (int b = 1; b < Biomes.Count; b++)
            {
                float s = scores[b];
                if (s > s1)
                {
                    s4 = s3; b3 = b2; s3 = s2; b2 = b1; s2 = s1; b1 = b; s1 = s;
                }
                else if (s > s2)
                {
                    s4 = s3; b3 = b2; s3 = s2; b2 = b; s2 = s;
                }
                else if (s > s3)
                {
                    s4 = s3; b3 = b; s3 = s;
                }
                else if (s > s4)
                {
                    s4 = s;
                }
            }
            // Weights relative to the fourth biome: the third fades to zero before it swaps rank with the
            // fourth, so the top-three selection never makes the color jump.
            float w4 = Mathf.Pow(s4, exponent);
            float w1 = Mathf.Pow(Mathf.Max(s1, 1e-6f), exponent) - w4 + 1e-9f, w2 = Mathf.Pow(s2, exponent) - w4, w3 = Mathf.Pow(s3, exponent) - w4;
            float total = w1 + w2 + w3;
            w1 /= total;
            w2 /= total;
            w3 /= total;
            Color color = Palette[b1] * w1 + Palette[b2] * w2 + Palette[b3] * w3;
            float vegetation = Vegetation[b1] * w1 + Vegetation[b2] * w2 + Vegetation[b3] * w3;
            float ice = (b1 == (int)Biome.IcePotential ? w1 : 0f) + (b2 == (int)Biome.IcePotential ? w2 : 0f) + (b3 == (int)Biome.IcePotential ? w3 : 0f);

            // 2. Multi-scale variation (continuous on the sphere).
            float v = Settings.regionalVariation;
            if (v > 0f)
            {
                float regional = PlanetNoise.Fractal(direction * 2.2f, regionalSeed, 2);
                float landscape = PlanetNoise.Fractal(direction * 9f, landscapeSeed, 2);
                float fine = PlanetNoise.Gradient(direction.x * 40f, direction.y * 40f, direction.z * 40f, fineSeed);
                float warmth = PlanetNoise.Fractal(direction * 1.6f, warmthSeed, 2);
                // Vegetation density: dry soil patches show through vegetated biomes (not on ice / rock / desert).
                float dry = vegetation * v * Mathf.Clamp(0.5f * landscape + 0.3f * regional, 0f, 0.35f);
                color = Color.Lerp(color, SoilColor, dry);
                // Warmth and brightness (subtle).
                float shift = 0.05f * v * warmth;
                color.r *= 1f + shift;
                color.b *= 1f - shift;
                color *= 1f + v * (0.1f * regional + 0.05f * landscape + 0.03f * fine);
            }

            // 3. Terrain: rock on steep ground, mild elevation tint.
            float rock = RockCover(SlopeField.Sample(direction)) * (1f - 0.6f * ice);
            if (rock > 0f)
            {
                color = Color.Lerp(color, RockColor * (1f + 0.15f * Mathf.Clamp01((elevation - climate.SeaLevel) / 0.04f)), rock);
            }
            float high = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.012f, 0.04f, elevation - climate.SeaLevel));
            if (high > 0f)
            {
                float grey = 0.3f * color.r + 0.59f * color.g + 0.11f * color.b;
                color = Color.Lerp(color, new Color(grey, grey, grey), 0.25f * high) * (1f + 0.06f * high);
            }

            // 4. Saturation.
            float luminance = 0.3f * color.r + 0.59f * color.g + 0.11f * color.b;
            color = Color.LerpUnclamped(new Color(luminance, luminance, luminance), color, Settings.saturation);
            color.r = Mathf.Clamp01(color.r);
            color.g = Mathf.Clamp01(color.g);
            color.b = Mathf.Clamp01(color.b);
            color.a = 1f;
            return color;
        }
    }
}
