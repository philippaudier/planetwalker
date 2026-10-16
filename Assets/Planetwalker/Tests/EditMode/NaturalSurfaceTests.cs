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
    /// <summary>V0.1.2A Natural Planet appearance. Letters refer to the milestone's test list (A - T).</summary>
    public class NaturalSurfaceTests
    {
        static readonly Dictionary<int, (PlanetInstance Planet, BiomeResult Biomes, TerrainSlopeField Slope)> Planets =
            new Dictionary<int, (PlanetInstance, BiomeResult, TerrainSlopeField)>();
        static HydrologyGrid climateGrid, slopeGrid;

        static (PlanetInstance Planet, BiomeResult Biomes, TerrainSlopeField Slope) Planet(int seed)
        {
            if (!Planets.TryGetValue(seed, out var entry))
            {
                climateGrid ??= new HydrologyGrid(64);
                slopeGrid ??= new HydrologyGrid(TerrainSlopeField.Resolution);
                var planet = new PlanetInstance(new PlanetDefinition { seed = seed });
                ClimateResult climate = new ClimateGenerator().Compute(planet, new ClimateSettings(), climateGrid);
                entry = (planet, new BiomeClassifier().Classify(climate, new BiomeSettings()),
                    new TerrainSlopeField(slopeGrid, planet.Height.GetElevation, planet.SeaLevel));
                Planets.Add(seed, entry);
            }
            return entry;
        }

        static NaturalSurface Surface(int seed, NaturalSurfaceSettings settings = null)
        {
            var p = Planet(seed);
            return new NaturalSurface(p.Biomes, p.Slope, settings ?? new NaturalSurfaceSettings(), seed);
        }

        /// <summary>Evenly spread land directions (Fibonacci sphere) with their elevation.</summary>
        static List<(Vector3 Direction, float Elevation)> LandPoints(PlanetInstance planet, int count)
        {
            var points = new List<(Vector3, float)>();
            for (int i = 0; i < count; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / count, r = Mathf.Sqrt(1f - y * y), phi = i * 2.39996323f;
                var d = new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi));
                float e = planet.GetElevation(d);
                if (e > planet.SeaLevel)
                {
                    points.Add((d, e));
                }
            }
            return points;
        }

        static float Luminance(Color c) => 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;

        static float Saturation(Color c)
        {
            Color.RGBToHSV(c, out _, out float s, out _);
            return s;
        }

        // ------------------------------------------------------------ A, B, F, G, S (sampler)

        // A, G
        [Test]
        public void AG_SameInputs_SameValidColors()
        {
            NaturalSurface a = Surface(868497), b = Surface(868497);
            foreach (var (d, e) in LandPoints(Planet(868497).Planet, 6000))
            {
                Color ca = a.Evaluate(d, e), cb = b.Evaluate(d, e);
                Assert.AreEqual(ca, cb);
                Assert.IsTrue(ca.r >= 0f && ca.r <= 1f && ca.g >= 0f && ca.g <= 1f && ca.b >= 0f && ca.b <= 1f && ca.a == 1f, "G: valid RGB");
                Assert.IsFalse(float.IsNaN(ca.r + ca.g + ca.b), "G: no NaN");
            }
        }

        // B
        [Test]
        public void B_DifferentSeeds_GiveDifferentNaturalPatterns()
        {
            // Same terrain and biomes, different appearance seed: the variation layers differ.
            var p = Planet(868497);
            var a = new NaturalSurface(p.Biomes, p.Slope, new NaturalSurfaceSettings(), 868497);
            var b = new NaturalSurface(p.Biomes, p.Slope, new NaturalSurfaceSettings(), 609538);
            float difference = 0f;
            List<(Vector3 Direction, float Elevation)> points = LandPoints(p.Planet, 4000);
            foreach (var (d, e) in points)
            {
                Color ca = a.Evaluate(d, e), cb = b.Evaluate(d, e);
                difference += Mathf.Abs(ca.r - cb.r) + Mathf.Abs(ca.g - cb.g) + Mathf.Abs(ca.b - cb.b);
            }
            Assert.Greater(difference / points.Count, 0.01f, "Seed-dependent variation");
            // Different planets: different landscapes altogether (land positions differ).
            Assert.AreNotEqual(LandPoints(Planet(868497).Planet, 500).Count, LandPoints(Planet(609538).Planet, 500).Count);
        }

        // F
        /// <summary>Color steps along three great circles (land runs only): (step, angle at its end, axis index).</summary>
        static List<(float Jump, float Angle, int Axis)> PathSteps(NaturalSurface surface, PlanetInstance planet, float step)
        {
            var steps = new List<(float, float, int)>();
            for (int axis = 0; axis < PathAxes.Length; axis++)
            {
                Color previous = default;
                bool previousLand = false;
                for (float a = 0f; a < 2f * Mathf.PI; a += step)
                {
                    Vector3 d = PathPoint(axis, a);
                    float e = planet.GetElevation(d);
                    bool land = e > planet.SeaLevel;
                    if (land)
                    {
                        Color c = surface.Evaluate(d, e);
                        if (previousLand)
                        {
                            steps.Add((Mathf.Abs(c.r - previous.r) + Mathf.Abs(c.g - previous.g) + Mathf.Abs(c.b - previous.b), a, axis));
                        }
                        previous = c;
                    }
                    previousLand = land;
                }
            }
            steps.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            return steps;
        }

        static readonly Vector3[] PathAxes = { new Vector3(0.2f, 1f, 0.1f), new Vector3(1f, 0.3f, 0.4f), new Vector3(0.1f, 0.4f, 1f) };

        static Vector3 PathPoint(int axis, float angle)
        {
            Vector3 n = PathAxes[axis].normalized, u = Vector3.Cross(n, Vector3.right).normalized, v = Vector3.Cross(n, u);
            return (u * Mathf.Cos(angle) + v * Mathf.Sin(angle)).normalized;
        }

        [Test]
        public void F_Colors_AreContinuousAlongPaths()
        {
            // A hard edge, a contour band or a sampling switch stays a jump however fine the step; a
            // continuous transition shrinks with it. The largest steps at 1/32 climate cell (64) are
            // subdivided 32 times more: their sub-steps must shrink accordingly.
            PlanetInstance planet = Planet(926747).Planet;
            float cell = Mathf.PI * 0.5f / 64f, step = cell / 32f;
            foreach (float blend in new[] { 0f, 0.5f, 1f })
            {
                NaturalSurface surface = Surface(926747, new NaturalSurfaceSettings { biomeBlend = blend });
                var coarse = PathSteps(surface, planet, cell / 4f);
                var fine = PathSteps(surface, planet, step);
                float refined = 0f, worst = fine[fine.Count - 1].Jump;
                for (int k = fine.Count - 10; k < fine.Count; k++)
                {
                    var (_, angle, axis) = fine[k];
                    Color previous = surface.Evaluate(PathPoint(axis, angle - step), planet.GetElevation(PathPoint(axis, angle - step)));
                    for (int s = 1; s <= 32; s++)
                    {
                        Vector3 d = PathPoint(axis, angle - step + step * s / 32f);
                        float e = planet.GetElevation(d);
                        if (e <= planet.SeaLevel) continue;
                        Color c = surface.Evaluate(d, e);
                        refined = Mathf.Max(refined, Mathf.Abs(c.r - previous.r) + Mathf.Abs(c.g - previous.g) + Mathf.Abs(c.b - previous.b));
                        previous = c;
                    }
                }
                TestContext.WriteLine($"blend {blend}: color step per 1/4 cell: median {coarse[coarse.Count / 2].Jump:0.000}, p99 {coarse[(int)(coarse.Count * 0.99f)].Jump:0.000}, "
                    + $"max {coarse[coarse.Count - 1].Jump:0.000}; per 1/32 cell: max {worst:0.000}; largest steps refined to 1/1024 cell: max {refined:0.000}");
                Assert.Less(refined, 0.2f * worst, $"blend {blend}: steps shrink with the sampling step (no discontinuity)");
                Assert.Less(refined, 0.02f, $"blend {blend}: no jump at fine scale");
                Assert.Less(coarse[coarse.Count - 1].Jump, 0.7f, $"blend {blend}: no abrupt palette switch");
            }
        }
        // S
        [Test]
        public void S_Colors_StayWithinNaturalLuminanceAndSaturation()
        {
            foreach (int seed in new[] { 1337, 868497, 609538, 280695, 926747 })
            {
                NaturalSurface surface = Surface(seed);
                var lum = new List<float>();
                var sat = new List<float>();
                int white = 0;
                List<(Vector3 Direction, float Elevation)> points = LandPoints(Planet(seed).Planet, 8000);
                foreach (var (d, e) in points)
                {
                    Color c = surface.Evaluate(d, e);
                    lum.Add(Luminance(c));
                    sat.Add(Saturation(c));
                    white += Luminance(c) > 0.85f ? 1 : 0;
                }
                sat.Sort();
                float meanLum = lum.Average(), p99Sat = sat[(int)(sat.Count * 0.99f)];
                TestContext.WriteLine($"seed {seed}: land luminance mean {meanLum:0.00}, saturation mean {sat.Average():0.00} (p99 {p99Sat:0.00}), near-white {100f * white / points.Count:0.0}%");
                Assert.That(meanLum, Is.InRange(0.2f, 0.6f), "Neither too dark nor washed out");
                Assert.Less(p99Sat, 0.75f, "No fluorescent colors");
                Assert.Less((float)white / points.Count, 0.2f, "No large pure-white areas");
            }
        }

        // ------------------------------------------------------------ L, M, N (terrain and cold)

        // L, M
        [Test]
        public void LM_RockOnSteepSlopes_NotOnFlatGround()
        {
            HydrologyGrid grid = new HydrologyGrid(TerrainSlopeField.Resolution);
            Vector3 peak = new Vector3(0.3f, 0.8f, 0.5f).normalized;
            // A steep ridge (rises 4% of the radius over ~2 degrees) on a flat plateau.
            Func<Vector3, float> terrain = d => 0.01f + 0.04f * Mathf.Exp(-Mathf.Pow(Vector3.Angle(d, peak) / 2f, 2f));
            var field = new TerrainSlopeField(grid, terrain, 0f);
            var flat = new TerrainSlopeField(grid, d => 0.01f, 0f);
            var surface = Surface(868497);
            float flank = 0f, plain = 0f;
            Vector3 side = Vector3.Cross(peak, Vector3.right).normalized;
            Vector3 flankDirection = (peak + side * Mathf.Tan(1.4f * Mathf.Deg2Rad)).normalized;
            Vector3 plainDirection = (peak + side * Mathf.Tan(15f * Mathf.Deg2Rad)).normalized;
            flank = surface.RockCover(field.Sample(flankDirection));
            plain = surface.RockCover(field.Sample(plainDirection));
            Assert.Greater(flank, 0.5f, "L: steep flank shows rock");
            Assert.Less(plain, 0.01f, "M: plateau shows none");
            for (int c = 0; c < grid.CellCount; c += 97)
            {
                Assert.Less(flat.Slope[c], 1e-3f, "M: flat terrain has no slope");
                Assert.AreEqual(0f, surface.RockCover(flat.Slope[c]), "M: and no rock");
            }
            // More exposure, more rock (same slope).
            float slope = field.Sample(flankDirection) * 0.6f;
            Assert.Greater(Surface(868497, new NaturalSurfaceSettings { rockExposure = 1f }).RockCover(slope),
                Surface(868497, new NaturalSurfaceSettings { rockExposure = 0f }).RockCover(slope));
            // Coasts are not cliffs: the field floors elevations at sea level.
            var coast = new TerrainSlopeField(grid, d => d.x > 0f ? 0.001f : -0.03f, 0f);
            int i = grid.CellFromDirection(new Vector3(0.01f, 0.2f, 1f).normalized);
            Assert.Less(surface.RockCover(coast.Slope[i]), 0.01f, "No rock band along coastlines");
        }

        // N
        [Test]
        public void N_ColdAndAlpineSurfaces_GetTheirProvisionalTreatment()
        {
            var grid = new HydrologyGrid(32);
            ClimateResult Climate(float t, float heightKm)
            {
                var c = new ClimateResult(grid, new ClimateSettings(), 1, 0f);
                for (int i = 0; i < grid.CellCount; i++)
                {
                    c.Temperature[i] = t;
                    c.Precipitation[i] = 0.6f;
                    c.HeightKm[i] = heightKm;
                }
                return c;
            }
            var flat = new TerrainSlopeField(new HydrologyGrid(TerrainSlopeField.Resolution), d => 0.005f, 0f);
            var settings = new NaturalSurfaceSettings { regionalVariation = 0f };
            Vector3 d = new Vector3(0.2f, 0.5f, 0.8f).normalized;
            Color ice = new NaturalSurface(new BiomeClassifier().Classify(Climate(-25f, 0.5f), new BiomeSettings()), flat, settings, 1).Evaluate(d, 0.005f);
            Color alpine = new NaturalSurface(new BiomeClassifier().Classify(Climate(-3f, 2.5f), new BiomeSettings()), flat, settings, 1).Evaluate(d, 0.005f);
            Color forest = new NaturalSurface(new BiomeClassifier().Classify(Climate(12f, 0.5f), new BiomeSettings()), flat, settings, 1).Evaluate(d, 0.005f);
            Assert.Greater(Luminance(ice), 0.7f, "Ice / snow potential reads pale");
            Assert.Less(Luminance(ice), 0.95f, "but not pure white");
            Assert.GreaterOrEqual(ice.b, ice.r, "slightly cool");
            Assert.Less(Saturation(alpine), 0.2f, "Alpine reads as stone grey");
            Assert.Greater(Luminance(ice), Luminance(alpine));
            Assert.Greater(forest.g, forest.r, "Temperate land stays green");
            // High mountains are not painted white without the ice biome.
            NaturalSurface real = Surface(204018);
            PlanetInstance planet = Planet(204018).Planet;
            int peaks = 0, whitePeaks = 0;
            foreach (var (direction, elevation) in LandPoints(planet, 20000))
            {
                if (elevation - planet.SeaLevel > 0.035f)
                {
                    peaks++;
                    int cell = real.BiomeData.Climate.Grid.CellFromDirection(direction);
                    if (Luminance(real.Evaluate(direction, elevation)) > 0.8f && real.BiomeData.Dominant[cell] != Biome.IcePotential)
                    {
                        whitePeaks++;
                    }
                }
            }
            Assert.Less(whitePeaks, Mathf.Max(1, peaks / 10), "No systematic mountain whitening");
        }

        // ------------------------------------------------------------ Controller: C, D, E, H, I, J, K, O, P, Q, R

        GameObject root;
        readonly List<GameObject> extra = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                Object.DestroyImmediate(root);
                root = null;
            }
            foreach (GameObject o in extra)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            extra.Clear();
        }

        PlanetVisualController CreateVisual(string name = "Natural Test")
        {
            var go = new GameObject(name);
            if (root == null) root = go; else extra.Add(go);
            var visual = go.AddComponent<PlanetVisualController>();
            visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
            visual.Climate.resolution = ClimateSettings.LowResolution;
            return visual;
        }

        static Color[] FaceColors(PlanetVisualController visual, int face)
        {
            return visual.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(face)).GetComponent<MeshFilter>().sharedMesh.colors;
        }

        static Vector3[] FaceVertices(PlanetVisualController visual, int face)
        {
            return visual.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(face)).GetComponent<MeshFilter>().sharedMesh.vertices;
        }

        // C, E
        [Test]
        public void CE_NaturalPlanet_IsSeamlessAndSeparatesLandFromSea()
        {
            PlanetVisualController visual = CreateVisual();
            var planet = new PlanetInstance(new PlanetDefinition { seed = 280695, resolution = 32 });
            visual.Render(planet);
            Assert.IsTrue(visual.NaturalPlanetShown, "Natural Planet is the default view");
            Assert.IsNotNull(visual.CurrentNaturalSurface);
            var seen = new Dictionary<Vector3, Color>();
            int shared = 0;
            PlanetColorRamp ramp = visual.ColorRamp;
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                Vector3[] vertices = FaceVertices(visual, face);
                Color[] colors = FaceColors(visual, face);
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (seen.TryGetValue(vertices[i], out Color other))
                    {
                        Assert.AreEqual(other, colors[i], "C: seam color mismatch");
                        shared++;
                    }
                    else
                    {
                        seen.Add(vertices[i], colors[i]);
                    }
                    Vector3 d = vertices[i].normalized;
                    float e = planet.GetElevation(d);
                    if (e <= planet.SeaLevel)
                    {
                        // E: under the sea, the ocean-floor palette (never a biome color).
                        Color expected = ramp.Evaluate(e - planet.SeaLevel, 0f);
                        Color shown = linear ? colors[i].gamma : colors[i];
                        if (e - planet.SeaLevel < -0.001f)
                        {
                            Assert.Less(Mathf.Abs(shown.b - expected.b) + Mathf.Abs(shown.g - expected.g), 0.08f, "E: ocean floor palette");
                        }
                    }
                }
            }
            Assert.Greater(shared, 0);
        }

        // D, K
        [Test]
        public void DK_Colors_IgnoreMeshResolutionCameraAndRotation()
        {
            PlanetVisualController coarse = CreateVisual("Coarse");
            coarse.Render(new PlanetInstance(new PlanetDefinition { seed = 609538, resolution = 16 }));
            PlanetVisualController fine = CreateVisual("Fine");
            fine.transform.rotation = Quaternion.Euler(30f, 75f, -20f);
            var camera = new GameObject("Natural Camera").AddComponent<Camera>();
            extra.Add(camera.gameObject);
            camera.transform.position = new Vector3(120f, 300f, -200f);
            fine.Render(new PlanetInstance(new PlanetDefinition { seed = 609538, resolution = 32 }));
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                Color[] a = FaceColors(coarse, face), b = FaceColors(fine, face);
                for (int y = 0; y <= 16; y++)
                {
                    for (int x = 0; x <= 16; x++)
                    {
                        Color ca = a[x + y * 17], cb = b[2 * x + 2 * y * 33];
                        Assert.Less(Mathf.Abs(ca.r - cb.r) + Mathf.Abs(ca.g - cb.g) + Mathf.Abs(ca.b - cb.b), 1e-4f,
                            "Same direction, same color (mesh resolution, camera and rotation ignored)");
                    }
                }
            }
        }

        // H, I, J, O, P
        [Test]
        public void HIJOP_ArtisticSettingsAndViews_RecomputeNoScientificData()
        {
            PlanetVisualController visual = CreateVisual();
            visual.Lakes.showLakes = true;
            visual.Rivers.showRivers = true;
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 926747, resolution = 24 }));
            int climate = visual.ClimateCache.ComputeCount, biomes = visual.BiomeCache.ComputeCount, slope = visual.SlopeCache.ComputeCount;
            int hydrology = visual.HydrologyCache.ComputeCount, lakes = visual.LakeCache.SelectionCount, samplers = visual.NaturalSurfaceBuildCount;
            BiomeResult biomeResult = visual.CurrentBiomes;
            ClimateResult climateResult = visual.CurrentClimate;
            float[] scores = (float[])biomeResult.Scores.Clone();
            float[] temperature = (float[])climateResult.Temperature.Clone();
            Color[] before = FaceColors(visual, 0);

            // O: artistic settings -> a new sampler and new colors only.
            visual.Surface.biomeBlend = 0.9f;
            visual.Surface.rockExposure = 0.9f;
            visual.Surface.saturation = 1.1f;
            visual.RefreshColors();
            Assert.AreEqual(samplers + 1, visual.NaturalSurfaceBuildCount);
            CollectionAssert.AreNotEqual(before, FaceColors(visual, 0), "Colors changed");
            // H, I, J: nothing scientific recomputed or modified.
            Assert.AreEqual(climate, visual.ClimateCache.ComputeCount, "I");
            Assert.AreEqual(biomes, visual.BiomeCache.ComputeCount, "H");
            Assert.AreEqual(hydrology, visual.HydrologyCache.ComputeCount, "J");
            Assert.AreEqual(lakes, visual.LakeCache.SelectionCount, "J");
            Assert.AreEqual(slope, visual.SlopeCache.ComputeCount);
            CollectionAssert.AreEqual(scores, visual.CurrentBiomes.Scores, "H: classification untouched");
            CollectionAssert.AreEqual(temperature, visual.CurrentClimate.Temperature, "I: climate untouched");

            // P: debug views and back: no scientific recomputation, the sampler is reused.
            int samplersNow = visual.NaturalSurfaceBuildCount;
            foreach (PlanetDebugView view in new[] { PlanetDebugView.BiomeMap, PlanetDebugView.ClimateTemperature, PlanetDebugView.LakeDepth,
                         PlanetDebugView.RiverNetwork, PlanetDebugView.None })
            {
                visual.DebugView = view;
                visual.RefreshColors();
            }
            Assert.AreEqual(climate, visual.ClimateCache.ComputeCount);
            Assert.AreEqual(biomes, visual.BiomeCache.ComputeCount);
            Assert.AreEqual(samplersNow, visual.NaturalSurfaceBuildCount, "Natural sampler reused when returning");

            // Mesh resolution: same data, same slope field, same sampler.
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 926747, resolution = 40 }));
            Assert.AreEqual(slope, visual.SlopeCache.ComputeCount, "Slope field independent of the mesh");
            Assert.AreEqual(samplersNow, visual.NaturalSurfaceBuildCount);
            // Classic palette toggle: recolor only.
            visual.Surface.enabled = false;
            visual.RefreshColors();
            Assert.IsFalse(visual.NaturalPlanetShown);
            visual.Surface.enabled = true;
            visual.RefreshColors();
            Assert.AreEqual(climate, visual.ClimateCache.ComputeCount);
            Assert.AreEqual(biomes, visual.BiomeCache.ComputeCount);
        }

        // Q, R
        [Test]
        public void QR_DebugViewsRemain_AndRiversAndLakesStillShow()
        {
            PlanetVisualController visual = CreateVisual();
            visual.Lakes.showLakes = true;
            visual.Rivers.showRivers = true;
            var planet = new PlanetInstance(new PlanetDefinition { seed = 609538, resolution = 32 });
            visual.Render(planet);
            Assert.IsTrue(visual.NaturalPlanetShown);
            Assert.IsTrue(visual.LakesVisible, "R: lakes drawn over the Natural Planet");
            Assert.IsTrue(visual.RiversVisible, "R: rivers drawn over the Natural Planet");
            Assert.Greater(visual.LakeMesh.vertexCount, 0);
            Assert.Greater(visual.RiverMesh.vertexCount, 0);
            foreach (PlanetDebugView view in Enum.GetValues(typeof(PlanetDebugView)))
            {
                visual.DebugView = view;
                visual.RefreshColors();
                Color[] colors = FaceColors(visual, 2);
                Assert.IsFalse(colors.Contains(Color.magenta), "Q: " + view + " still works");
            }
        }

        // ------------------------------------------------------------ Reports

        [Test]
        public void ReferencePlanets_AndPerformance_Report()
        {
            foreach (int seed in new[] { 1337, 868497, 609538, 280695, 926747 })
            {
                NaturalSurface surface = Surface(seed);
                var p = Planet(seed);
                List<(Vector3 Direction, float Elevation)> points = LandPoints(p.Planet, 10000);
                int rocky = 0, green = 0, sandy = 0, pale = 0;
                foreach (var (d, e) in points)
                {
                    Color c = surface.Evaluate(d, e);
                    rocky += surface.RockCover(p.Slope.Sample(d)) > 0.3f ? 1 : 0;
                    green += c.g > c.r * 1.1f && c.g > c.b ? 1 : 0;
                    sandy += c.r > c.g && c.g > c.b * 1.15f && Luminance(c) > 0.45f ? 1 : 0;
                    pale += Luminance(c) > 0.75f ? 1 : 0;
                }
                float n = points.Count / 100f;
                List<float> slopes = points.Select(q => p.Slope.Sample(q.Direction)).OrderBy(q => q).ToList();
                TestContext.WriteLine($"seed {seed}: land steepness p50 {slopes[slopes.Count / 2]:0.000}, p90 {slopes[(int)(slopes.Count * 0.9f)]:0.000}, "
                    + $"p97 {slopes[(int)(slopes.Count * 0.97f)]:0.000}, p99 {slopes[(int)(slopes.Count * 0.99f)]:0.000}, max {slopes[slopes.Count - 1]:0.000}");
                TestContext.WriteLine($"seed {seed}: land samples {points.Count}: green {green / n:0}%, sand/ochre {sandy / n:0}%, rock (cover > 0.3) {rocky / n:0.0}%, pale {pale / n:0}%; "
                    + $"slope field {p.Slope.Milliseconds:0} ms");
            }
            // Per-vertex cost at terrain resolution 256 (396,294 vertices, about 29% land).
            NaturalSurface s = Surface(868497);
            PlanetInstance planet = Planet(868497).Planet;
            var land = new List<(Vector3, float)>();
            for (int face = 0; face < 6; face++)
            {
                for (int y = 0; y <= 256; y += 1)
                {
                    for (int x = 0; x <= 256; x += 1)
                    {
                        Vector3 d = CubeSphereGenerator.GetSphereDirection(face, x, y, 256);
                        float e = planet.GetElevation(d);
                        if (e > planet.SeaLevel) land.Add((d, e));
                    }
                }
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (var (d, e) in land)
            {
                s.Evaluate(d, e);
            }
            double ms = watch.Elapsed.TotalMilliseconds;
            TestContext.WriteLine($"Natural color evaluation at terrain 256: {land.Count} land vertices in {ms:0} ms ({ms * 1000.0 / land.Count:0.00} us per vertex)");

            // Regeneration at terrain 256, warm caches: classic palette vs Natural Planet.
            PlanetVisualController visual = CreateVisual();
            visual.Climate.resolution = ClimateSettings.DefaultResolution;
            var definition = new PlanetDefinition { seed = 868497, resolution = 256 };
            var cold = System.Diagnostics.Stopwatch.StartNew();
            visual.Render(new PlanetInstance(definition));
            double first = cold.Elapsed.TotalMilliseconds;
            var warm = System.Diagnostics.Stopwatch.StartNew();
            visual.Render(new PlanetInstance(definition));
            double natural = warm.Elapsed.TotalMilliseconds;
            visual.Surface.enabled = false;
            warm.Restart();
            visual.Render(new PlanetInstance(definition));
            double classic = warm.Elapsed.TotalMilliseconds;
            visual.Surface.enabled = true;
            visual.DebugView = PlanetDebugView.BiomeMap;
            visual.RefreshColors();
            visual.DebugView = PlanetDebugView.None;
            warm.Restart();
            visual.RefreshColors();
            double switchBack = warm.Elapsed.TotalMilliseconds;
            TestContext.WriteLine($"Terrain 256 render: first (climate + biomes + slope field) {first:0} ms, natural (warm) {natural:0} ms, classic {classic:0} ms, "
                + $"switch back to Natural Planet (recolor) {switchBack:0} ms");
        }
    }
}
