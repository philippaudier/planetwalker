using System;
using System.Collections.Generic;
using System.Diagnostics;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// Terrestrial biomes of V0.1.1E-B.1, after Whittaker's annual temperature / precipitation biome
    /// diagram (the climate only provides annual means). Categories that need precipitation
    /// seasonality are merged or deferred: Mediterranean climates (no claim: "dry woodland and
    /// shrubland" is only the warm, moderately dry envelope), the tropical seasonal forest / savanna
    /// split (one category, as in Whittaker), monsoon forests.
    /// </summary>
    public enum Biome : byte
    {
        /// <summary>Non-terrestrial (ocean) cell.</summary>
        Ocean,
        TropicalRainforest,
        /// <summary>Tropical seasonal forest and savanna (one Whittaker category: no seasonality proxy).</summary>
        TropicalSeasonalForestSavanna,
        HotDesert,
        /// <summary>Warm, moderately dry woodland / shrubland (not asserted Mediterranean).</summary>
        DryWoodlandShrubland,
        /// <summary>Semi-arid grassland and shrub steppe.</summary>
        Steppe,
        ColdDesert,
        TemperateGrassland,
        TemperateForest,
        TemperateRainforest,
        BorealForest,
        Tundra,
        /// <summary>Treeless highlands: cold because of elevation, not latitude.</summary>
        Alpine,
        /// <summary>Climatic potential for permanent ice / snow (provisional: annual mean only).</summary>
        IcePotential,
    }

    public static class Biomes
    {
        public const int Count = 14;

        static readonly string[] Names =
        {
            "Ocean", "Tropical Rainforest", "Tropical Seasonal Forest / Savanna", "Hot Desert", "Dry Woodland / Shrubland",
            "Steppe", "Cold Desert", "Temperate Grassland", "Temperate Forest", "Temperate Rainforest", "Boreal Forest",
            "Tundra", "Alpine", "Ice / Snow Potential",
        };

        public static string Name(Biome biome) => Names[(int)biome];

        /// <summary>Biomes whose climate supports closed or open forest.</summary>
        public static bool IsForest(Biome biome)
        {
            return biome == Biome.TropicalRainforest || biome == Biome.TropicalSeasonalForestSavanna || biome == Biome.TemperateForest
                || biome == Biome.TemperateRainforest || biome == Biome.BorealForest;
        }

        /// <summary>Biomes that require abundant water (used by monotonicity tests).</summary>
        public static bool IsWet(Biome biome)
        {
            return biome == Biome.TropicalRainforest || biome == Biome.TemperateRainforest || biome == Biome.TemperateForest;
        }
    }

    /// <summary>
    /// Ecological interpretation of the climate (V0.1.1E-B.1). Reads a <see cref="ClimateResult"/> on
    /// its own grid (no second climate model, no mesh dependence) and never modifies it.
    /// </summary>
    /// <remarks>
    /// Intermediate variables, per land cell:
    /// - Thermal regime: annual mean temperature T (C).
    /// - Evaporative demand D(T) = max(0.12, 0.8 exp(0.05 (T - 15))): a monotonic Thornthwaite-like
    ///   proxy of potential evapotranspiration relative to the planet-mean precipitation (Earth: about
    ///   1000 mm of rain, PET about 800 mm at 15 C, 1450 mm at 27 C, 380 mm at 0 C).
    /// - Aridity index AI = P / (aridity x D(T)), P the climate precipitation index (x planet mean;
    ///   NOT mm/year). Read like the UNEP aridity index P / PET: arid below 0.2, semi-arid 0.2-0.5,
    ///   sub-humid 0.5-0.65, humid above. Relative, not a calibrated Koppen-Geiger classification.
    /// - Elevation cooling = lapse rate x climate elevation (km): separates alpine (cold because high)
    ///   from tundra (cold because polar).
    /// Suitability: each biome has a soft envelope in (T, ln AI) (logistic edges whose width is set by
    /// the ecotone sharpness); raw scores are normalized to sum 1 per land cell. Dominant = highest
    /// score (ties: lower biome id), secondary = second, margin = their difference (ecotone when small).
    /// Regularization (conservative, continuous): a cell blends its scores with the area-weighted mean
    /// of its land neighbours, with a weight going smoothly from 0.6 at margin 0 to 0 at margin 0.2 (one
    /// Jacobi pass): near-ties follow their neighbourhood, confident cells (narrow alpine or coastal
    /// belts) are untouched, the ocean never contributes. Then an isolated cell (no land neighbour of the
    /// same biome) whose margin is below 0.25 and whose second biome is the most common among its land
    /// neighbours swaps its two best scores (it joins its surroundings; nothing else changes).
    /// </remarks>
    public sealed class BiomeClassifier
    {
        public const float RegularizationMargin = 0.2f;
        /// <summary>Largest weight of the neighbourhood in the regularization (at margin 0).</summary>
        public const float RegularizationWeight = 0.6f;
        public const float EcotoneMargin = 0.15f;
        const float BaseTemperatureWidth = 1.5f;
        const float BaseAridityWidth = 0.18f; // ln units (x1.2)

        /// <summary>Envelope of a biome: annual temperature and aridity index ranges (open ends: infinity).</summary>
        struct Envelope
        {
            public float TMin, TMax, AiMin, AiMax;

            public Envelope(float tMin, float tMax, float aiMin, float aiMax)
            {
                TMin = tMin;
                TMax = tMax;
                AiMin = aiMin;
                AiMax = aiMax;
            }
        }

        const float Inf = float.PositiveInfinity;

        // Whittaker-style envelopes, indexed by Biome (Ocean, Alpine and IcePotential handled separately).
        static readonly Envelope[] Envelopes =
        {
            default, // Ocean
            new Envelope(20f, Inf, 1.6f, Inf), // TropicalRainforest
            new Envelope(18f, Inf, 0.25f, 1.6f), // TropicalSeasonalForestSavanna
            new Envelope(14f, Inf, 0f, 0.2f), // HotDesert
            new Envelope(12f, 20f, 0.35f, 0.75f), // DryWoodlandShrubland
            new Envelope(-5f, 18f, 0.2f, 0.5f), // Steppe
            new Envelope(-6f, 14f, 0f, 0.2f), // ColdDesert
            new Envelope(2f, 18f, 0.5f, 0.9f), // TemperateGrassland
            new Envelope(4f, 20f, 0.9f, 2.2f), // TemperateForest
            new Envelope(4f, 20f, 2.2f, Inf), // TemperateRainforest
            new Envelope(-7f, 4f, 0.5f, Inf), // BorealForest
            new Envelope(-14f, -6f, 0f, Inf), // Tundra
            default, // Alpine
            new Envelope(-Inf, -12f, 0f, Inf), // IcePotential
        };

        /// <summary>Relative evaporative demand (1 at 15 C), monotonic in temperature.</summary>
        public static float EvaporativeDemand(float temperature)
        {
            return Mathf.Max(0.12f, 0.8f * Mathf.Exp(0.05f * (temperature - 15f)));
        }

        /// <summary>Relative aridity index P / (aridity x D(T)) (UNEP-like reading, not mm based).</summary>
        public static float AridityIndex(float precipitation, float temperature, BiomeSettings settings)
        {
            return Mathf.Max(0f, precipitation) / (settings.aridity * EvaporativeDemand(temperature));
        }

        static float Sigmoid(float x)
        {
            return 1f / (1f + Mathf.Exp(-x));
        }

        static float Band(float x, float min, float max, float width)
        {
            float low = float.IsInfinity(min) ? 1f : Sigmoid((x - min) / width);
            float high = float.IsInfinity(max) ? 1f : Sigmoid((max - x) / width);
            return low * high;
        }

        /// <summary>
        /// Normalized suitability of every biome (index = <see cref="Biome"/>, Ocean = 0) for a land cell
        /// with annual temperature, precipitation index and elevation cooling (C).
        /// </summary>
        public static void Scores(float temperature, float precipitation, float elevationCooling, BiomeSettings settings, float[] scores)
        {
            float wT = BaseTemperatureWidth / settings.ecotoneSharpness;
            float wA = BaseAridityWidth / settings.ecotoneSharpness;
            float lnAi = Mathf.Log(Mathf.Max(AridityIndex(precipitation, temperature, settings), 1e-4f));
            float sum = 0f;
            scores[0] = 0f;
            // Alpine factor: how much of the cold comes from elevation (cooling of 4 .. 9 C at sensitivity 1).
            float alpine = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(4f, 9f, elevationCooling * settings.alpineSensitivity));
            float treeless = Sigmoid((1f - temperature) / wT); // above the treeline: annual mean below ~1 C
            for (int b = 1; b < Biomes.Count; b++)
            {
                float s;
                if (b == (int)Biome.Alpine)
                {
                    s = alpine * Band(temperature, -12f, 1f, wT) * 1.2f;
                }
                else
                {
                    Envelope e = Envelopes[b];
                    float aiMin = e.AiMin > 0f ? Mathf.Log(e.AiMin) : -Inf;
                    float aiMax = float.IsInfinity(e.AiMax) ? Inf : Mathf.Log(e.AiMax);
                    s = Band(temperature, e.TMin, e.TMax, wT) * Band(lnAi, aiMin, aiMax, wA);
                    if (b == (int)Biome.BorealForest)
                    {
                        s *= 1f - alpine * treeless;
                    }
                    else if (b == (int)Biome.Tundra)
                    {
                        s *= 1f - alpine;
                    }
                }
                scores[b] = s;
                sum += s;
            }
            if (sum > 0f)
            {
                for (int b = 1; b < Biomes.Count; b++)
                {
                    scores[b] /= sum;
                }
            }
            else
            {
                scores[(int)Biome.Tundra] = 1f; // unreachable with logistic envelopes; keeps the result valid
            }
        }

        /// <summary>Highest and second highest scores (ties: lower id first).</summary>
        public static void TopTwo(float[] scores, int offset, out Biome first, out float firstScore, out Biome second, out float secondScore)
        {
            int a = 1, b = 0;
            float sa = -1f, sb = -1f;
            for (int i = 1; i < Biomes.Count; i++)
            {
                float s = scores[offset + i];
                if (s > sa)
                {
                    b = a;
                    sb = sa;
                    a = i;
                    sa = s;
                }
                else if (s > sb)
                {
                    b = i;
                    sb = s;
                }
            }
            first = (Biome)a;
            firstScore = sa;
            second = (Biome)b;
            secondScore = Mathf.Max(0f, sb);
        }

        public BiomeResult Classify(ClimateResult climate, BiomeSettings settings)
        {
            var watch = Stopwatch.StartNew();
            BiomeSettings s = settings.Clone();
            s.Sanitize();
            HydrologyGrid grid = climate.Grid;
            int count = grid.CellCount;
            var result = new BiomeResult(climate, s);
            float lapse = climate.Settings.lapseRate;
            var cellScores = new float[Biomes.Count];
            float[] scores = result.Scores;
            for (int c = 0; c < count; c++)
            {
                if (climate.IsOcean[c])
                {
                    continue;
                }
                result.Aridity[c] = AridityIndex(climate.Precipitation[c], climate.Temperature[c], s);
                Scores(climate.Temperature[c], climate.Precipitation[c], lapse * climate.HeightKm[c], s, cellScores);
                Array.Copy(cellScores, 0, scores, c * Biomes.Count, Biomes.Count);
            }
            Assign(result);
            result.Diagnostics.RegionsBeforeRegularization = CountRegions(result, out _, out result.Diagnostics.SingleCellRegionsBeforeRegularization);
            Regularize(result);
            Assign(result);
            MergeIsolatedCells(result);
            Assign(result);
            result.ComputeStatistics();
            result.Diagnostics.ClassificationMilliseconds = (float)watch.Elapsed.TotalMilliseconds;
            return result;
        }

        static void Assign(BiomeResult result)
        {
            ClimateResult climate = result.Climate;
            for (int c = 0; c < climate.Grid.CellCount; c++)
            {
                if (climate.IsOcean[c])
                {
                    result.Dominant[c] = Biome.Ocean;
                    result.Secondary[c] = Biome.Ocean;
                    result.DominantScore[c] = 0f;
                    result.SecondaryScore[c] = 0f;
                    result.Margin[c] = 0f;
                    continue;
                }
                TopTwo(result.Scores, c * Biomes.Count, out Biome first, out float firstScore, out Biome second, out float secondScore);
                result.Dominant[c] = first;
                result.Secondary[c] = second;
                result.DominantScore[c] = firstScore;
                result.SecondaryScore[c] = secondScore;
                result.Margin[c] = firstScore - secondScore;
            }
        }

        /// <summary>Ecotone cells only: area-weighted average of their scores with their land neighbours (one Jacobi pass).</summary>
        static void Regularize(BiomeResult result)
        {
            ClimateResult climate = result.Climate;
            HydrologyGrid grid = climate.Grid;
            float[] source = (float[])result.Scores.Clone();
            int n = Biomes.Count;
            int changed = 0;
            for (int c = 0; c < grid.CellCount; c++)
            {
                if (climate.IsOcean[c] || result.Margin[c] >= RegularizationMargin)
                {
                    continue;
                }
                float blend = RegularizationWeight * Mathf.SmoothStep(1f, 0f, result.Margin[c] / RegularizationMargin);
                float total = 0f;
                for (int b = 1; b < n; b++)
                {
                    result.Scores[c * n + b] = 0f;
                }
                for (int k = grid.GetNeighborStart(c); k < grid.GetNeighborEnd(c); k++)
                {
                    int other = grid.GetNeighbor(k);
                    if (climate.IsOcean[other])
                    {
                        continue;
                    }
                    float a = grid.GetArea(other);
                    total += a;
                    for (int b = 1; b < n; b++)
                    {
                        result.Scores[c * n + b] += source[other * n + b] * a;
                    }
                }
                for (int b = 1; b < n; b++)
                {
                    float neighbourhood = total > 0f ? result.Scores[c * n + b] / total : source[c * n + b];
                    result.Scores[c * n + b] = Mathf.Lerp(source[c * n + b], neighbourhood, blend);
                }
                changed++;
            }
            result.Diagnostics.RegularizedCells = changed;
        }

        const float IsolatedMargin = 0.25f;

        /// <summary>Isolated low-margin cells whose second biome dominates their neighbourhood join it (scores swapped).</summary>
        static void MergeIsolatedCells(BiomeResult result)
        {
            ClimateResult climate = result.Climate;
            HydrologyGrid grid = climate.Grid;
            int n = Biomes.Count;
            var counts = new int[n];
            var swaps = new List<int>();
            for (int c = 0; c < grid.CellCount; c++)
            {
                if (climate.IsOcean[c] || result.Margin[c] >= IsolatedMargin)
                {
                    continue;
                }
                Array.Clear(counts, 0, n);
                bool isolated = true;
                int land = 0;
                for (int k = grid.GetNeighborStart(c); k < grid.GetNeighborEnd(c); k++)
                {
                    int other = grid.GetNeighbor(k);
                    if (climate.IsOcean[other])
                    {
                        continue;
                    }
                    land++;
                    counts[(int)result.Dominant[other]]++;
                    isolated &= result.Dominant[other] != result.Dominant[c];
                }
                if (!isolated || land == 0)
                {
                    continue;
                }
                int majority = 1;
                for (int b = 2; b < n; b++)
                {
                    if (counts[b] > counts[majority])
                    {
                        majority = b;
                    }
                }
                if ((int)result.Secondary[c] == majority)
                {
                    swaps.Add(c); // decided on the pre-merge state (order independent)
                }
            }
            foreach (int c in swaps)
            {
                int first = c * n + (int)result.Dominant[c], second = c * n + (int)result.Secondary[c];
                (result.Scores[first], result.Scores[second]) = (result.Scores[second], result.Scores[first]);
            }
            result.Diagnostics.MergedIsolatedCells = swaps.Count;
        }

        /// <summary>Connected regions of equal dominant biome over the land cell graph; returns the region count.</summary>
        internal static int CountRegions(BiomeResult result, out List<(Biome Biome, double Area)> regions, out int singleCells)
        {
            return CountRegions(result, out regions, out singleCells, out _);
        }

        internal static int CountRegions(BiomeResult result, out List<(Biome Biome, double Area)> regions, out int singleCells, out double singleCellArea)
        {
            ClimateResult climate = result.Climate;
            HydrologyGrid grid = climate.Grid;
            var visited = new bool[grid.CellCount];
            var stack = new Stack<int>();
            regions = new List<(Biome, double)>();
            singleCells = 0;
            singleCellArea = 0;
            for (int c = 0; c < grid.CellCount; c++)
            {
                if (visited[c] || climate.IsOcean[c])
                {
                    continue;
                }
                Biome biome = result.Dominant[c];
                double area = 0;
                int cells = 0;
                visited[c] = true;
                stack.Push(c);
                while (stack.Count > 0)
                {
                    int v = stack.Pop();
                    area += grid.GetArea(v);
                    cells++;
                    for (int k = grid.GetNeighborStart(v); k < grid.GetNeighborEnd(v); k++)
                    {
                        int other = grid.GetNeighbor(k);
                        if (!visited[other] && !climate.IsOcean[other] && result.Dominant[other] == biome)
                        {
                            visited[other] = true;
                            stack.Push(other);
                        }
                    }
                }
                regions.Add((biome, area));
                singleCells += cells == 1 ? 1 : 0;
                singleCellArea += cells == 1 ? area : 0;
            }
            return regions.Count;
        }
    }

    /// <summary>Biome classification of a climate result (one value per climate cell) and its statistics.</summary>
    public sealed class BiomeResult
    {
        public BiomeResult(ClimateResult climate, BiomeSettings settings)
        {
            Climate = climate;
            Settings = settings;
            int n = climate.Grid.CellCount;
            Scores = new float[n * Biomes.Count];
            Dominant = new Biome[n];
            Secondary = new Biome[n];
            DominantScore = new float[n];
            SecondaryScore = new float[n];
            Margin = new float[n];
            Aridity = new float[n];
            TerrestrialFraction = new float[Biomes.Count];
            SurfaceFraction = new float[Biomes.Count];
        }

        public ClimateResult Climate { get; }
        public BiomeSettings Settings { get; }

        /// <summary>Normalized suitabilities, [cell * Biomes.Count + biome] (0 over the ocean).</summary>
        public readonly float[] Scores;
        public readonly Biome[] Dominant;
        public readonly Biome[] Secondary;
        public readonly float[] DominantScore;
        public readonly float[] SecondaryScore;
        /// <summary>Dominant minus secondary suitability (0 .. 1; small = ecotone).</summary>
        public readonly float[] Margin;
        /// <summary>Relative aridity index (0 over the ocean).</summary>
        public readonly float[] Aridity;
        /// <summary>Area fraction of the land covered by each biome (index = Biome).</summary>
        public readonly float[] TerrestrialFraction;
        /// <summary>Area fraction of the whole planet covered by each biome (Ocean included).</summary>
        public readonly float[] SurfaceFraction;

        public BiomeDiagnostics Diagnostics;

        internal void ComputeStatistics()
        {
            HydrologyGrid grid = Climate.Grid;
            var area = new double[Biomes.Count];
            double total = 0, land = 0, arid = 0, drylands = 0, forest = 0, ecotone = 0;
            for (int c = 0; c < grid.CellCount; c++)
            {
                double a = grid.GetArea(c);
                total += a;
                area[(int)Dominant[c]] += a;
                if (Climate.IsOcean[c])
                {
                    continue;
                }
                land += a;
                arid += Aridity[c] < 0.2f ? a : 0;
                drylands += Aridity[c] < 0.65f ? a : 0;
                forest += Biomes.IsForest(Dominant[c]) ? a : 0;
                ecotone += Margin[c] < BiomeClassifier.EcotoneMargin ? a : 0;
            }
            int represented = 0;
            Biome largest = Biome.Ocean;
            for (int b = 0; b < Biomes.Count; b++)
            {
                SurfaceFraction[b] = (float)(area[b] / total);
                TerrestrialFraction[b] = b == 0 || land <= 0 ? 0f : (float)(area[b] / land);
                if (b > 0 && TerrestrialFraction[b] >= 0.001f)
                {
                    represented++;
                }
                if (b > 0 && (largest == Biome.Ocean || area[b] > area[(int)largest]))
                {
                    largest = (Biome)b;
                }
            }
            Diagnostics.LandFraction = (float)(land / total);
            Diagnostics.BiomesRepresented = represented;
            Diagnostics.DominantBiome = land > 0 ? largest : Biome.Ocean;
            Diagnostics.AridFraction = land > 0 ? (float)(arid / land) : 0f;
            Diagnostics.DrylandFraction = land > 0 ? (float)(drylands / land) : 0f;
            Diagnostics.ForestFraction = land > 0 ? (float)(forest / land) : 0f;
            Diagnostics.EcotoneFraction = land > 0 ? (float)(ecotone / land) : 0f;
            Diagnostics.Regions = BiomeClassifier.CountRegions(this, out List<(Biome Biome, double Area)> regions, out Diagnostics.SingleCellRegions,
                out double singleArea);
            Diagnostics.SingleCellLandFraction = land > 0 ? (float)(singleArea / land) : 0f;
            regions.Sort((x, y) => y.Area.CompareTo(x.Area));
            LargestRegions = regions.GetRange(0, Math.Min(5, regions.Count)).ConvertAll(r => (r.Biome, land > 0 ? (float)(r.Area / land) : 0f));
        }

        readonly float[] blendBuffer = new float[Biomes.Count];

        /// <summary>Largest connected land regions of one biome (biome, fraction of the land), largest first.</summary>
        public List<(Biome Biome, float LandFraction)> LargestRegions { get; private set; } = new List<(Biome, float)>();

        /// <summary>
        /// Dominant biome at a direction from interpolated suitabilities of the nearby LAND cells (weights
        /// (1 - d^2)^2 over 1.5 cells, as the climate views): boundaries follow the score iso-lines
        /// instead of the cell staircase; ids themselves are never interpolated, ocean cells never blended.
        /// </summary>
        public Biome SampleBiome(Vector3 direction)
        {
            HydrologyGrid grid = Climate.Grid;
            int cell = grid.CellFromDirection(direction);
            // Fast path: all land cells around agree.
            Biome common = Biome.Ocean;
            bool uniform = true;
            for (int k = grid.GetNeighborStart(cell) - 1; k < grid.GetNeighborEnd(cell) && uniform; k++)
            {
                int other = k < grid.GetNeighborStart(cell) ? cell : grid.GetNeighbor(k);
                if (Climate.IsOcean[other])
                {
                    continue;
                }
                if (common == Biome.Ocean)
                {
                    common = Dominant[other];
                }
                else if (Dominant[other] != common)
                {
                    uniform = false;
                }
            }
            if (uniform)
            {
                return common;
            }
            float[] blended = blendBuffer;
            Array.Clear(blended, 0, blended.Length);
            float radius = 1.5f * Mathf.PI * 0.5f / grid.Resolution;
            float weights = 0f;
            for (int k = grid.GetNeighborStart(cell) - 1; k < grid.GetNeighborEnd(cell); k++)
            {
                int other = k < grid.GetNeighborStart(cell) ? cell : grid.GetNeighbor(k);
                if (Climate.IsOcean[other])
                {
                    continue;
                }
                float d = (grid.GetCenter(other) - direction).magnitude / radius;
                float w = Mathf.Max(0f, 1f - d * d);
                w = w * w + 1e-6f;
                weights += w;
                for (int b = 1; b < Biomes.Count; b++)
                {
                    blended[b] += Scores[other * Biomes.Count + b] * w;
                }
            }
            BiomeClassifier.TopTwo(blended, 0, out Biome first, out _, out _, out _);
            return first;
        }

        /// <summary>
        /// Interpolated suitability vector at a direction (land cells only, weights (1 - d^2)^2 over 1.4
        /// local cell spacings, normalized to sum 1) written into <paramref name="blended"/> (length Biomes.Count).
        /// Returns the total land weight (0 when no land cell is within reach; the vector is then all
        /// zero): small values mean the vector rests on distant cells and callers should fade it out.
        /// </summary>
        public float SampleScores(Vector3 direction, float[] blended)
        {
            Array.Clear(blended, 0, Biomes.Count);
            HydrologyGrid grid = Climate.Grid;
            int cell = grid.CellFromDirection(direction);
            float radius = 1.4f * grid.LocalSpacing(direction); // inside the 8-neighbourhood everywhere
            float weights = 0f;
            for (int k = grid.GetNeighborStart(cell) - 1; k < grid.GetNeighborEnd(cell); k++)
            {
                int other = k < grid.GetNeighborStart(cell) ? cell : grid.GetNeighbor(k);
                if (Climate.IsOcean[other])
                {
                    continue;
                }
                float d = (grid.GetCenter(other) - direction).magnitude / radius;
                float w = Mathf.Max(0f, 1f - d * d);
                w *= w;
                if (w <= 0f)
                {
                    continue;
                }
                weights += w;
                int offset = other * Biomes.Count;
                for (int b = 1; b < Biomes.Count; b++)
                {
                    blended[b] += Scores[offset + b] * w;
                }
            }
            if (weights <= 0f)
            {
                return 0f;
            }
            for (int b = 1; b < Biomes.Count; b++)
            {
                blended[b] /= weights;
            }
            return weights;
        }
        /// <summary>Smooth sample of a land field (ocean cells excluded from the blend).</summary>
        public float SampleLand(float[] field, Vector3 direction)
        {
            HydrologyGrid grid = Climate.Grid;
            int cell = grid.CellFromDirection(direction);
            float radius = 1.5f * Mathf.PI * 0.5f / grid.Resolution;
            float sum = 0f, weights = 0f;
            for (int k = grid.GetNeighborStart(cell) - 1; k < grid.GetNeighborEnd(cell); k++)
            {
                int other = k < grid.GetNeighborStart(cell) ? cell : grid.GetNeighbor(k);
                if (Climate.IsOcean[other])
                {
                    continue;
                }
                float d = (grid.GetCenter(other) - direction).magnitude / radius;
                float w = Mathf.Max(0f, 1f - d * d);
                w = w * w + 1e-6f;
                sum += field[other] * w;
                weights += w;
            }
            return weights > 0f ? sum / weights : 0f;
        }
    }

    /// <summary>Measured biome diagnostics (area-weighted, land = cells above sea level).</summary>
    public struct BiomeDiagnostics
    {
        public float LandFraction;
        public int BiomesRepresented;
        public Biome DominantBiome;
        /// <summary>Land with aridity index below 0.2 (arid + hyper-arid) / below 0.65 (all drylands).</summary>
        public float AridFraction, DrylandFraction;
        /// <summary>Land whose dominant biome supports forest.</summary>
        public float ForestFraction;
        /// <summary>Land whose two best biomes are within <see cref="BiomeClassifier.EcotoneMargin"/>.</summary>
        public float EcotoneFraction;
        /// <summary>Connected single-biome land regions, and those made of one cell, after / before regularization.</summary>
        public int Regions, SingleCellRegions, RegionsBeforeRegularization, SingleCellRegionsBeforeRegularization;
        public int RegularizedCells;
        /// <summary>Land fraction covered by single-cell regions (narrow coastal / mountain features and noise).</summary>
        public float SingleCellLandFraction;
        /// <summary>Isolated low-margin cells that joined the biome of their neighbourhood.</summary>
        public int MergedIsolatedCells;
        public float ClassificationMilliseconds;
    }

    /// <summary>Biome cache: recomputes only for a new climate result or new biome settings.</summary>
    public sealed class BiomeCache
    {
        readonly BiomeClassifier classifier = new BiomeClassifier();
        ClimateResult cachedClimate;
        BiomeSettings cachedSettings;
        BiomeResult cached;

        public int ComputeCount { get; private set; }

        public BiomeResult Current => cached;

        public bool IsCurrent(ClimateResult climate, BiomeSettings settings)
        {
            settings.Sanitize();
            return cached != null && cachedClimate == climate && cachedSettings.BiomeEquals(settings);
        }

        public BiomeResult Get(ClimateResult climate, BiomeSettings settings)
        {
            if (IsCurrent(climate, settings))
            {
                return cached;
            }
            cached = classifier.Classify(climate, settings);
            cachedClimate = climate;
            cachedSettings = settings.Clone();
            ComputeCount++;
            return cached;
        }

        public void Clear()
        {
            cached = null;
            cachedClimate = null;
            cachedSettings = null;
        }
    }
}
