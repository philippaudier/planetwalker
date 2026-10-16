using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// V0.1.1E-B.1 biome classification. Letters refer to the milestone's test list (A - T). Ecological
    /// assertions use synthetic climate fixtures; reference planets check robustness and diversity.
    /// </summary>
    public class BiomeTests
    {
        static readonly int[] Seeds = { 1337, 868497, 609538, 280695, 554492, 335559, 926747, 204018 };
        static readonly Dictionary<int, HydrologyGrid> Grids = new Dictionary<int, HydrologyGrid>();
        static readonly Dictionary<(int, int), ClimateResult> Climates = new Dictionary<(int, int), ClimateResult>();

        static HydrologyGrid Grid(int resolution)
        {
            if (!Grids.TryGetValue(resolution, out HydrologyGrid grid))
            {
                grid = new HydrologyGrid(resolution);
                Grids.Add(resolution, grid);
            }
            return grid;
        }

        static ClimateResult PlanetClimate(int seed, int resolution = 64)
        {
            if (!Climates.TryGetValue((seed, resolution), out ClimateResult climate))
            {
                climate = new ClimateGenerator().Compute(new PlanetInstance(new PlanetDefinition { seed = seed }),
                    new ClimateSettings { resolution = resolution }, Grid(resolution));
                Climates.Add((seed, resolution), climate);
            }
            return climate;
        }

        /// <summary>Synthetic climate: every field set by a function of the cell direction.</summary>
        static ClimateResult Fixture(int resolution, Func<Vector3, (float Temperature, float Precipitation, float HeightKm, bool Ocean)> field)
        {
            var climate = new ClimateResult(Grid(resolution), new ClimateSettings(), 1, 0f);
            for (int c = 0; c < climate.Grid.CellCount; c++)
            {
                var v = field(climate.Grid.GetCenter(c));
                climate.Temperature[c] = v.Temperature;
                climate.Precipitation[c] = v.Precipitation;
                climate.HeightKm[c] = v.HeightKm;
                climate.IsOcean[c] = v.Ocean;
                climate.Latitude[c] = Mathf.Asin(climate.Grid.GetCenter(c).y);
            }
            return climate;
        }

        /// <summary>Earth-like smooth zonal land planet: T by latitude, P with wet tropics / dry subtropics, smooth longitude variation.</summary>
        static (float, float, float, bool) Zonal(Vector3 d)
        {
            float lat = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg, a = Mathf.Abs(lat);
            float t = 27f - 52f * Mathf.Pow(a / 90f, 1.6f);
            float p = 0.35f + 2.2f * Mathf.Exp(-(a / 12f) * (a / 12f)) + 0.9f * Mathf.Exp(-((a - 50f) / 14f) * ((a - 50f) / 14f));
            float lon = Mathf.Atan2(d.z, d.x);
            p *= 0.6f + 0.4f * (0.5f + 0.5f * Mathf.Cos(lon)); // a smooth wet / dry contrast in longitude
            return (t, p, 0f, false);
        }

        static BiomeResult Classify(ClimateResult climate, BiomeSettings settings = null)
        {
            return new BiomeClassifier().Classify(climate, settings ?? new BiomeSettings());
        }

        static float[] ScoresAt(float t, float p, float cooling = 0f, BiomeSettings settings = null)
        {
            var scores = new float[Biomes.Count];
            BiomeClassifier.Scores(t, p, cooling, settings ?? new BiomeSettings(), scores);
            return scores;
        }

        static Biome Best(float[] scores)
        {
            BiomeClassifier.TopTwo(scores, 0, out Biome first, out _, out _, out _);
            return first;
        }

        static float Sum(float[] scores, Func<Biome, bool> select)
        {
            float s = 0f;
            for (int b = 1; b < Biomes.Count; b++)
            {
                s += select((Biome)b) ? scores[b] : 0f;
            }
            return s;
        }

        // ------------------------------------------------------------ A, B, C (planets)

        // A
        [Test]
        public void A_SameClimateAndSettings_ProduceTheSameBiomes()
        {
            ClimateResult climate = PlanetClimate(868497);
            BiomeResult a = Classify(climate), b = Classify(climate);
            CollectionAssert.AreEqual(a.Dominant, b.Dominant);
            CollectionAssert.AreEqual(a.Secondary, b.Secondary);
            CollectionAssert.AreEqual(a.Scores, b.Scores);
            CollectionAssert.AreEqual(a.TerrestrialFraction, b.TerrestrialFraction);
        }

        // B
        [Test]
        public void B_DifferentPlanets_HaveDifferentBiomeDistributions()
        {
            BiomeResult a = Classify(PlanetClimate(868497)), b = Classify(PlanetClimate(335559));
            float distance = 0f;
            for (int i = 1; i < Biomes.Count; i++)
            {
                distance += Mathf.Abs(a.TerrestrialFraction[i] - b.TerrestrialFraction[i]);
            }
            Assert.Greater(distance, 0.1f, "Planets differ in their biome distribution");
            // Same climate, different interpretation settings: different distribution too.
            BiomeResult drier = Classify(PlanetClimate(868497), new BiomeSettings { aridity = 2f });
            Assert.Greater(drier.Diagnostics.AridFraction, a.Diagnostics.AridFraction);
        }

        // C
        [Test]
        public void C_TerrainMeshResolution_DoesNotChangeTheBiomes()
        {
            ClimateResult coarse = new ClimateGenerator().Compute(new PlanetInstance(new PlanetDefinition { seed = 204018, resolution = 24 }),
                new ClimateSettings { resolution = 32 }, Grid(32));
            ClimateResult fine = new ClimateGenerator().Compute(new PlanetInstance(new PlanetDefinition { seed = 204018, resolution = 256 }),
                new ClimateSettings { resolution = 32 }, Grid(32));
            CollectionAssert.AreEqual(Classify(coarse).Dominant, Classify(fine).Dominant);
        }

        // ------------------------------------------------------------ D (seams), E, F, G, H (validity)

        // D
        [Test]
        public void D_Classification_HasNoCubeFaceSeamImprint()
        {
            // Smooth synthetic field: pointwise classification is seam-free by construction; the only grid
            // dependent step (ecotone regularization over neighbours) must not concentrate along seams.
            ClimateResult climate = Fixture(64, Zonal);
            BiomeResult result = Classify(climate);
            var pointwise = new float[Biomes.Count];
            double seamChanged = 0, seamCells = 0, interiorChanged = 0, interiorCells = 0;
            int n = climate.Grid.Resolution;
            for (int c = 0; c < climate.Grid.CellCount; c++)
            {
                BiomeClassifier.Scores(climate.Temperature[c], climate.Precipitation[c], 0f, result.Settings, pointwise);
                bool changed = Best(pointwise) != result.Dominant[c];
                climate.Grid.GetFaceCoordinates(c, out _, out int i, out int j);
                if (Mathf.Min(Mathf.Min(i, n - 1 - i), Mathf.Min(j, n - 1 - j)) < 2)
                {
                    seamChanged += changed ? 1 : 0;
                    seamCells++;
                }
                else
                {
                    interiorChanged += changed ? 1 : 0;
                    interiorCells++;
                }
            }
            double seamRate = seamChanged / seamCells, interiorRate = interiorChanged / interiorCells;
            TestContext.WriteLine($"Regularization changes: seams {seamRate * 100:0.00}%, face interiors {interiorRate * 100:0.00}%");
            Assert.Less(seamRate, 1.5 * interiorRate + 0.01, "Regularization is not imprinted along seams");
        }

        // D (display): seam vertices of the biome views get identical colors on both faces.
        [Test]
        public void D2_BiomeViews_AreConsistentAcrossCubeFaces()
        {
            PlanetVisualController visual = CreateVisual();
            var planet = new PlanetInstance(new PlanetDefinition { seed = 926747, resolution = 32 });
            foreach (PlanetDebugView view in new[] { PlanetDebugView.BiomeMap, PlanetDebugView.BiomeSuitability, PlanetDebugView.BiomeTransitions, PlanetDebugView.BiomeAridity })
            {
                visual.DebugView = view;
                visual.Render(planet);
                var seen = new Dictionary<Vector3, Color>();
                int shared = 0;
                for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
                {
                    Mesh mesh = root.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(face)).GetComponent<MeshFilter>().sharedMesh;
                    Vector3[] vertices = mesh.vertices;
                    Color[] colors = mesh.colors;
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        Assert.AreNotEqual(Color.magenta, colors[i], view + ": missing data");
                        if (seen.TryGetValue(vertices[i], out Color other))
                        {
                            Assert.AreEqual(other, colors[i], view + ": seam mismatch");
                            shared++;
                        }
                        else
                        {
                            seen.Add(vertices[i], colors[i]);
                        }
                    }
                }
                Assert.Greater(shared, 0);
            }
        }

        // E, F, G, H
        [Test]
        public void EFGH_EveryCellIsValid_ScoresAreBounded_AndOrdered()
        {
            foreach (int seed in new[] { 868497, 926747, 204018 })
            {
                BiomeResult r = Classify(PlanetClimate(seed));
                ClimateResult c = r.Climate;
                for (int i = 0; i < c.Grid.CellCount; i++)
                {
                    if (c.IsOcean[i])
                    {
                        Assert.AreEqual(Biome.Ocean, r.Dominant[i], "E: ocean is not terrestrial");
                        for (int b = 0; b < Biomes.Count; b++)
                        {
                            Assert.AreEqual(0f, r.Scores[i * Biomes.Count + b]);
                        }
                        continue;
                    }
                    Assert.AreNotEqual(Biome.Ocean, r.Dominant[i], "F: land has a terrestrial biome");
                    Assert.That((int)r.Dominant[i], Is.InRange(1, Biomes.Count - 1));
                    float sum = 0f;
                    for (int b = 0; b < Biomes.Count; b++)
                    {
                        float s = r.Scores[i * Biomes.Count + b];
                        Assert.IsTrue(!float.IsNaN(s) && s >= 0f && s <= 1f, "G: bounded score");
                        sum += s;
                    }
                    Assert.AreEqual(1f, sum, 1e-4f, "G: normalized");
                    Assert.GreaterOrEqual(r.DominantScore[i], r.SecondaryScore[i], "H: ordered");
                    Assert.AreNotEqual(r.Dominant[i], r.Secondary[i], "H: distinct");
                    Assert.AreEqual(r.DominantScore[i] - r.SecondaryScore[i], r.Margin[i], 1e-6f);
                    Assert.That(r.Margin[i], Is.InRange(0f, 1f));
                }
            }
            // H: stable tie-breaking (lower id wins), independent of input order effects.
            var ties = new float[Biomes.Count];
            ties[(int)Biome.Steppe] = 0.4f;
            ties[(int)Biome.TemperateGrassland] = 0.4f;
            ties[(int)Biome.Tundra] = 0.2f;
            BiomeClassifier.TopTwo(ties, 0, out Biome first, out _, out Biome second, out _);
            Assert.AreEqual(Biome.Steppe, first);
            Assert.AreEqual(Biome.TemperateGrassland, second);
        }

        // ------------------------------------------------------------ I, J, K, L (ecology)

        // I
        [Test]
        public void I_LessWater_NeverIncreasesForestSuitability()
        {
            foreach (float t in new[] { -4f, 8f, 15f, 24f })
            {
                float previous = float.MaxValue;
                for (float p = 4f; p >= 0.02f; p *= 0.9f)
                {
                    float forest = Sum(ScoresAt(t, p), Biomes.IsForest);
                    Assert.LessOrEqual(forest, previous + 1e-4f, $"T {t}: forest suitability rose as P fell to {p}");
                    previous = forest;
                }
                Assert.Greater(Sum(ScoresAt(t, 4f), Biomes.IsForest), 0.6f, $"T {t}: wet climates are forest-capable");
                Assert.Less(Sum(ScoresAt(t, 0.03f), Biomes.IsForest), 0.1f, $"T {t}: very dry climates are not");
            }
        }

        // J
        [Test]
        public void J_HigherEvaporativeDemand_NeverIncreasesWetBiomeSuitability()
        {
            for (float t = -10f; t <= 30f; t += 1f)
            {
                Assert.Greater(BiomeClassifier.EvaporativeDemand(t + 1f), BiomeClassifier.EvaporativeDemand(t) - 1e-6f, "Monotonic demand");
            }
            foreach (float t in new[] { 8f, 15f, 24f })
            {
                foreach (float p in new[] { 0.6f, 1.2f, 2.5f })
                {
                    float previous = float.MaxValue;
                    for (float aridity = 0.5f; aridity <= 2f; aridity += 0.1f)
                    {
                        float wet = Sum(ScoresAt(t, p, 0f, new BiomeSettings { aridity = aridity }), Biomes.IsWet);
                        Assert.LessOrEqual(wet, previous + 1e-4f, $"T {t} P {p}: wet biomes gained with demand x{aridity}");
                        previous = wet;
                    }
                }
            }
        }

        // K
        [Test]
        public void K_ColdFavoursColdBiomes_HeatFavoursTropicalOnes()
        {
            Func<Biome, bool> tropical = b => b == Biome.TropicalRainforest || b == Biome.TropicalSeasonalForestSavanna;
            Func<Biome, bool> cold = b => b == Biome.BorealForest || b == Biome.Tundra || b == Biome.IcePotential;
            foreach (float p in new[] { 0.8f, 1.5f, 3f })
            {
                Assert.Greater(Sum(ScoresAt(-8f, p), cold), 0.8f, "Cold climate");
                Assert.Less(Sum(ScoresAt(-8f, p), tropical), 0.01f);
                Assert.Greater(Sum(ScoresAt(27f, p), tropical), 0.8f, "Hot climate");
                Assert.Less(Sum(ScoresAt(27f, p), cold), 0.01f);
            }
            Assert.AreEqual(Biome.TropicalRainforest, Best(ScoresAt(27f, 4f)));
            Assert.AreEqual(Biome.HotDesert, Best(ScoresAt(27f, 0.1f)));
            Assert.AreEqual(Biome.TemperateForest, Best(ScoresAt(12f, 1.4f)));
            Assert.AreEqual(Biome.BorealForest, Best(ScoresAt(-2f, 1f)));
            Assert.AreEqual(Biome.Tundra, Best(ScoresAt(-10f, 0.5f)));
            Assert.AreEqual(Biome.IcePotential, Best(ScoresAt(-25f, 0.3f)));
            Assert.AreEqual(Biome.ColdDesert, Best(ScoresAt(5f, 0.05f)));
        }

        // L
        [Test]
        public void L_AlpineFollowsElevationCooling_NotLatitudeAlone()
        {
            // Same cold annual mean: from latitude (no elevation cooling) -> boreal / tundra; from altitude -> alpine.
            Assert.AreEqual(Biome.BorealForest, Best(ScoresAt(-2f, 1f, 0f)));
            Assert.AreEqual(Biome.Alpine, Best(ScoresAt(-2f, 1f, 14f)));
            Assert.AreEqual(Biome.Alpine, Best(ScoresAt(-8f, 0.6f, 14f)), "High and cold: alpine, not tundra");
            // Warm mountains keep their forest (below the treeline).
            Assert.AreEqual(Biome.TemperateForest, Best(ScoresAt(12f, 1.4f, 14f)));
            // Very cold summits: ice potential, not alpine; but cold highlands are not all ice.
            Assert.AreEqual(Biome.IcePotential, Best(ScoresAt(-22f, 0.5f, 20f)));
            Assert.AreNotEqual(Biome.IcePotential, Best(ScoresAt(-4f, 0.5f, 12f)));
            // Sensitivity: alpine appears at lower elevation when more sensitive.
            float normal = ScoresAt(-2f, 1f, 6f)[(int)Biome.Alpine];
            float sensitive = ScoresAt(-2f, 1f, 6f, new BiomeSettings { alpineSensitivity = 2f })[(int)Biome.Alpine];
            Assert.Greater(sensitive, normal);
            // Monotonic in elevation cooling.
            float previous = -1f;
            for (float cooling = 0f; cooling <= 16f; cooling += 1f)
            {
                float alpine = ScoresAt(-3f, 1f, cooling)[(int)Biome.Alpine];
                Assert.GreaterOrEqual(alpine, previous - 1e-5f);
                previous = alpine;
            }
        }

        // ------------------------------------------------------------ M, Q, R, S (statistics, stability)

        // M
        [Test]
        public void M_AreaWeightedFractions_SumCorrectly()
        {
            BiomeResult r = Classify(PlanetClimate(280695));
            Assert.AreEqual(1f, r.SurfaceFraction.Sum(), 1e-4f);
            Assert.AreEqual(1f, r.TerrestrialFraction.Skip(1).Sum(), 1e-4f);
            Assert.AreEqual(0f, r.TerrestrialFraction[0]);
            Assert.AreEqual(1f - r.Diagnostics.LandFraction, r.SurfaceFraction[0], 1e-4f, "Ocean share = 1 - land");
            Assert.AreEqual(r.Climate.Diagnostics.LandFraction, r.Diagnostics.LandFraction, 1e-4f);
            // Weighted by spherical cell areas (cells are not equal-area).
            double area = 0, forest = 0, land = 0;
            for (int i = 0; i < r.Climate.Grid.CellCount; i++)
            {
                if (r.Climate.IsOcean[i]) continue;
                double a = r.Climate.Grid.GetArea(i);
                land += a;
                forest += Biomes.IsForest(r.Dominant[i]) ? a : 0;
                area += a;
            }
            Assert.AreEqual(forest / land, r.Diagnostics.ForestFraction, 1e-4);
        }

        // Q
        [Test]
        public void Q_Ecotones_AreStable_WithoutNumericalArtifacts()
        {
            ClimateResult climate = PlanetClimate(609538);
            BiomeResult r = Classify(climate);
            Assert.That(r.Diagnostics.EcotoneFraction, Is.InRange(0.02f, 0.6f));
            // A tiny perturbation of the precipitation changes almost nothing.
            var perturbed = new ClimateResult(climate.Grid, climate.Settings, climate.Seed, climate.SeaLevel);
            for (int i = 0; i < climate.Grid.CellCount; i++)
            {
                perturbed.Temperature[i] = climate.Temperature[i];
                perturbed.Precipitation[i] = climate.Precipitation[i] * 1.001f;
                perturbed.HeightKm[i] = climate.HeightKm[i];
                perturbed.IsOcean[i] = climate.IsOcean[i];
            }
            BiomeResult p = Classify(perturbed);
            int land = 0, changed = 0;
            for (int i = 0; i < climate.Grid.CellCount; i++)
            {
                if (climate.IsOcean[i]) continue;
                land++;
                changed += r.Dominant[i] != p.Dominant[i] ? 1 : 0;
                Assert.AreEqual(r.Margin[i], p.Margin[i], 0.02f);
            }
            Assert.Less(changed, 0.01f * land, "No chaotic flips");
        }

        // R
        [Test]
        public void R_SmoothClimates_GiveContiguousRegions()
        {
            BiomeResult r = Classify(Fixture(64, Zonal));
            TestContext.WriteLine($"Synthetic zonal planet: {r.Diagnostics.Regions} regions ({r.Diagnostics.SingleCellRegions} single cells), "
                + $"before regularization {r.Diagnostics.RegionsBeforeRegularization} ({r.Diagnostics.SingleCellRegionsBeforeRegularization})");
            Assert.LessOrEqual(r.Diagnostics.SingleCellRegions, 4, "No salt-and-pepper patches");
            Assert.LessOrEqual(r.Diagnostics.Regions, 60, "Few, contiguous regions");
            Assert.LessOrEqual(r.Diagnostics.Regions, r.Diagnostics.RegionsBeforeRegularization, "Regularization never fragments");
            Assert.GreaterOrEqual(r.Diagnostics.BiomesRepresented, 8, "The zonal gradient spans many biomes");
        }

        // S
        [Test]
        public void S_ClimateGridResolutions_AgreeUpToDiscretization()
        {
            BiomeResult coarse = Classify(PlanetClimate(554492, 32));
            BiomeResult fine = Classify(PlanetClimate(554492, 64));
            double agree = 0, land = 0;
            for (int i = 0; i < fine.Climate.Grid.CellCount; i++)
            {
                if (fine.Climate.IsOcean[i]) continue;
                Vector3 d = fine.Climate.Grid.GetCenter(i);
                if (coarse.Climate.IsOcean[coarse.Climate.Grid.CellFromDirection(d)]) continue;
                double a = fine.Climate.Grid.GetArea(i);
                land += a;
                agree += coarse.SampleBiome(d) == fine.Dominant[i] ? a : 0;
            }
            TestContext.WriteLine($"Climate 32 vs 64 biome agreement: {agree / land * 100:0.0}% of the common land");
            Assert.Greater(agree / land, 0.7);
        }

        // ------------------------------------------------------------ N, O, T (runtime and cache)

        GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                Object.DestroyImmediate(root);
                root = null;
            }
        }

        PlanetVisualController CreateVisual()
        {
            root = new GameObject("Biome Test");
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
            visual.Climate.resolution = ClimateSettings.LowResolution;
            return visual;
        }

        // N, O, T
        [Test]
        public void NOT_CacheAndViews_RecomputeOnlyWhatChanged()
        {
            PlanetVisualController visual = CreateVisual();
            visual.DebugView = PlanetDebugView.BiomeMap;
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 609538, resolution = 24 }));
            Assert.AreEqual(1, visual.ClimateCache.ComputeCount);
            Assert.AreEqual(1, visual.BiomeCache.ComputeCount);
            BiomeResult biomes = visual.CurrentBiomes;

            // N: views (biome and climate) only recolor.
            foreach (PlanetDebugView view in new[] { PlanetDebugView.BiomeSuitability, PlanetDebugView.BiomeTransitions, PlanetDebugView.BiomeAridity,
                         PlanetDebugView.ClimateTemperature, PlanetDebugView.None, PlanetDebugView.BiomeMap })
            {
                visual.DebugView = view;
                visual.RefreshColors();
            }
            // O: river and lake rendering settings.
            visual.Rivers.widthMultiplier = 2.5f;
            visual.Rivers.showRivers = !visual.Rivers.showRivers;
            visual.Lakes.showLakes = !visual.Lakes.showLakes;
            visual.Lakes.color = Color.green;
            visual.RefreshColors();
            Assert.AreEqual(1, visual.ClimateCache.ComputeCount);
            Assert.AreEqual(1, visual.BiomeCache.ComputeCount);
            Assert.AreSame(biomes, visual.CurrentBiomes);

            // T: terrain mesh resolution -> nothing; biome settings -> biomes only; climate settings -> both.
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 609538, resolution = 40 }));
            Assert.AreEqual(1, visual.ClimateCache.ComputeCount);
            Assert.AreEqual(1, visual.BiomeCache.ComputeCount);
            visual.Biomes.aridity = 1.4f;
            visual.RefreshColors();
            Assert.AreEqual(1, visual.ClimateCache.ComputeCount);
            Assert.AreEqual(2, visual.BiomeCache.ComputeCount);
            visual.Climate.axialTilt = 30f;
            visual.RefreshColors();
            Assert.AreEqual(2, visual.ClimateCache.ComputeCount);
            Assert.AreEqual(3, visual.BiomeCache.ComputeCount);
            // A new seed recomputes both.
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 1, resolution = 40 }));
            Assert.AreEqual(3, visual.ClimateCache.ComputeCount);
            Assert.AreEqual(4, visual.BiomeCache.ComputeCount);

            // Biomes are not computed when no biome view is shown.
            visual.DebugView = PlanetDebugView.ClimateTemperature;
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 2, resolution = 40 }));
            Assert.AreEqual(4, visual.BiomeCache.ComputeCount);
        }

        [Test]
        public void T2_BiomeCache_KeysOnClimateAndSettings()
        {
            var cache = new BiomeCache();
            ClimateResult a = PlanetClimate(1337, 32), b = PlanetClimate(868497, 32);
            BiomeResult first = cache.Get(a, new BiomeSettings());
            Assert.AreSame(first, cache.Get(a, new BiomeSettings()));
            Assert.AreNotSame(first, cache.Get(a, new BiomeSettings { ecotoneSharpness = 1.5f }));
            Assert.AreNotSame(first, cache.Get(b, new BiomeSettings()));
            Assert.AreEqual(3, cache.ComputeCount);
        }

        // ------------------------------------------------------------ Reports

        [Test]
        public void ReferencePlanets_Report()
        {
            foreach (int seed in Seeds)
            {
                BiomeResult r = Classify(PlanetClimate(seed));
                BiomeDiagnostics d = r.Diagnostics;
                var distribution = Enumerable.Range(1, Biomes.Count - 1).Where(b => r.TerrestrialFraction[b] >= 0.005f)
                    .OrderByDescending(b => r.TerrestrialFraction[b])
                    .Select(b => $"{Biomes.Name((Biome)b)} {r.TerrestrialFraction[b] * 100:0}%");
                string largest = string.Join(", ", r.LargestRegions.Select(x => $"{Biomes.Name(x.Biome)} {x.LandFraction * 100:0.0}%"));
                TestContext.WriteLine($"seed {seed}: land {d.LandFraction * 100:0}% | {string.Join(" · ", distribution)} | "
                    + $"arid {d.AridFraction * 100:0}%, drylands {d.DrylandFraction * 100:0}%, forest {d.ForestFraction * 100:0}%, ecotones {d.EcotoneFraction * 100:0}% | "
                    + $"{d.BiomesRepresented} biomes, {d.Regions} regions ({d.SingleCellRegions} single cells = {d.SingleCellLandFraction * 100:0.0}% of land; before regularization {d.RegionsBeforeRegularization} / {d.SingleCellRegionsBeforeRegularization}), "
                    + $"{d.RegularizedCells} ecotone cells regularized, {d.MergedIsolatedCells} isolated cells merged | largest: {largest} | {d.ClassificationMilliseconds:0.0} ms");
            }
        }

        [Test]
        public void Performance_Report()
        {
            foreach (int resolution in new[] { 32, 64, 128 })
            {
                ClimateResult climate = PlanetClimate(868497, resolution);
                Classify(climate); // warm-up
                var watch = System.Diagnostics.Stopwatch.StartNew();
                BiomeResult r = Classify(climate);
                TestContext.WriteLine($"biomes at climate {resolution}: {watch.Elapsed.TotalMilliseconds:0.0} ms (climate {climate.Diagnostics.TotalMilliseconds:0} ms), {climate.Grid.CellCount} cells");
                if (resolution == 64)
                {
                    Assert.Less(watch.Elapsed.TotalMilliseconds, 100.0, "Classification well below 100 ms at 64");
                }
            }
        }
    }
}
