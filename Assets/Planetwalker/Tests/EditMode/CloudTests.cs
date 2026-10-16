using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>V0.1.2C procedural clouds. Letters refer to the milestone's test list (A - P).</summary>
    public class CloudTests
    {
        static readonly int[] ReferenceSeeds = { 1337, 868497, 609538, 536452 };
        static readonly Dictionary<int, ClimateResult> Climates = new Dictionary<int, ClimateResult>();
        static HydrologyGrid climateGrid;

        static ClimateResult Climate(int seed)
        {
            if (!Climates.TryGetValue(seed, out ClimateResult result))
            {
                climateGrid ??= new HydrologyGrid(64);
                result = new ClimateGenerator().Compute(new PlanetInstance(new PlanetDefinition { seed = seed }), new ClimateSettings(), climateGrid);
                Climates.Add(seed, result);
            }
            return result;
        }

        static CloudField Field(int seed) => new CloudField(seed, Climate(seed), Vector3.up);

        static float Latitude(Vector3 d) => Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;

        static IEnumerable<Vector3> Fibonacci(int count)
        {
            for (int i = 0; i < count; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / count, r = Mathf.Sqrt(1f - y * y), phi = i * 2.39996323f;
                yield return new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi));
            }
        }

        static float Correlation(IList<float> a, IList<float> b)
        {
            double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < a.Count; i++)
            {
                sab += (a[i] - ma) * (b[i] - mb);
                saa += (a[i] - ma) * (a[i] - ma);
                sbb += (b[i] - mb) * (b[i] - mb);
            }
            return (float)(sab / Math.Sqrt(saa * sbb));
        }

        GameObject root;
        readonly List<Object> extra = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                Object.DestroyImmediate(root);
                root = null;
            }
            foreach (Object o in extra)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            extra.Clear();
        }

        PlanetVisualController CreateVisual()
        {
            root = new GameObject("Cloud Test");
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
            visual.Climate.resolution = ClimateSettings.LowResolution;
            return visual;
        }

        // ------------------------------------------------------------ A, B, C, D (data)

        // A
        [Test]
        public void A_CloudData_IsDeterministic()
        {
            CloudField a = Field(1337), b = new CloudField(1337, Climate(1337), Vector3.up);
            CollectionAssert.AreEqual(a.Base, b.Base);
            CollectionAssert.AreEqual(a.Systems, b.Systems);
            CollectionAssert.AreEqual(a.Convective, b.Convective);
            Assert.AreEqual(a.WeatherSystems.Count, b.WeatherSystems.Count);
            var s = new CloudSettings();
            Assert.AreEqual(a.Threshold(s), b.Threshold(s));
            CloudField noClimate = new CloudField(1337, null, Vector3.up), again = new CloudField(1337, null, Vector3.up);
            CollectionAssert.AreEqual(noClimate.Base, again.Base, "Fallback (no climate) deterministic too");
            Assert.IsFalse(noClimate.ClimateInformed);
            TestContext.WriteLine($"cloud field generation: {a.Milliseconds:0} ms (climate-informed), {noClimate.Milliseconds:0} ms (latitude-only)");
        }

        // B
        [Test]
        public void B_WeatherSeeds_GiveDifferentSkies_OverTheSameGeography()
        {
            ClimateResult climate = Climate(868497);
            CloudField a = new CloudField(868497, climate, Vector3.up), b = new CloudField(4242, climate, Vector3.up);
            var s = new CloudSettings();
            float ta = a.Threshold(s), tb = b.Threshold(s);
            var ca = new List<float>();
            var cb = new List<float>();
            int differ = 0;
            foreach (Vector3 d in Fibonacci(6000))
            {
                float x = a.Coverage(d, s, ta), y = b.Coverage(d, s, tb);
                ca.Add(x);
                cb.Add(y);
                differ += Mathf.Abs(x - y) > 0.5f ? 1 : 0;
            }
            float r = Correlation(ca, cb);
            TestContext.WriteLine($"same climate, weather seeds 868497 / 4242: coverage correlation {r:0.00}, {100f * differ / ca.Count:0}% of the sky flips clear <-> cloudy");
            Assert.Less(r, 0.75f, "Not the same clouds");
            Assert.Greater(differ, 600, "A meaningful part of the sky differs");
            Assert.Greater(r, 0f, "Same climate prior: still related");
            // Sky character: the same planet can be mostly clear with few systems, or heavily convective.
            var clear = new CloudSettings { coverage = 0.25f, weatherStrength = 0.9f, convection = 0f };
            var stormy = new CloudSettings { coverage = 0.7f, weatherStrength = 0.4f, convection = 1f };
            float clearCover = a.MeanCoverage(clear, a.Threshold(clear)), stormyCover = a.MeanCoverage(stormy, a.Threshold(stormy));
            float tropicsClear = 0f, tropicsStormy = 0f;
            int tropics = 0;
            foreach (Vector3 d in Fibonacci(6000))
            {
                if (Mathf.Abs(Latitude(d)) < 12f)
                {
                    tropics++;
                    tropicsClear += a.Coverage(d, clear, a.Threshold(clear));
                    tropicsStormy += a.Coverage(d, stormy, a.Threshold(stormy));
                }
            }
            TestContext.WriteLine($"sky characters: clear {clearCover:0.00} (tropics {tropicsClear / tropics:0.00}), stormy {stormyCover:0.00} (tropics {tropicsStormy / tropics:0.00})");
            Assert.Greater(stormyCover - clearCover, 0.3f);
            Assert.Greater(tropicsStormy / tropics, tropicsClear / tropics + 0.3f, "Convection fills the tropics");
        }

        // C, D
        [Test]
        public void CD_Coverage_IsMonotonicAndZeroMeansNone()
        {
            CloudField field = Field(609538);
            float previous = -1f;
            foreach (float coverage in new[] { 0f, 0.1f, 0.25f, 0.45f, 0.6f, 0.8f, 0.95f })
            {
                var s = new CloudSettings { coverage = coverage };
                float mean = field.MeanCoverage(s, field.Threshold(s));
                TestContext.WriteLine($"coverage {coverage:0.00}: mean cloud alpha {mean:0.000}");
                Assert.GreaterOrEqual(mean, previous, "Monotonic");
                Assert.AreEqual(coverage, mean, 0.06f, "Coverage is an area fraction");
                previous = mean;
            }
            var none = new CloudSettings { coverage = 0f };
            Assert.AreEqual(0f, field.MeanCoverage(none, field.Threshold(none)));
            Assert.Greater(field.Threshold(none), 1000f, "Above any potential plus detail");

            // Controller: zero coverage = no cloud shell, no shadows.
            PlanetVisualController visual = CreateVisual();
            visual.Clouds.coverage = 0f;
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 609538, resolution = 16 }));
            Assert.IsFalse(visual.CloudsVisible);
            MeshRenderer terrain = visual.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(0)).GetComponent<MeshRenderer>();
            Assert.AreEqual(0f, BlockVector(terrain, "_CloudParams").w, "No cloud shadows");
            Assert.AreEqual(0f, BlockVector(visual.OceanRenderer, "_CloudParams").w);
        }

        // ------------------------------------------------------------ E, L (GPU)

        static Material Probe(List<Object> owner)
        {
            Shader shader = Shader.Find("Hidden/Planetwalker/CloudProbe");
            Assert.IsNotNull(shader, "Test probe shader");
            var material = new Material(shader);
            owner.Add(material);
            return material;
        }

        static Color[] Render(Material material, int pass, int width, int height, List<Object> owner)
        {
            var rt = new RenderTexture(width, height, 0, GraphicsFormat.R32G32B32A32_SFloat);
            owner.Add(rt);
            Graphics.Blit(null, rt, material, pass);
            var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false);
            owner.Add(texture);
            RenderTexture.active = rt;
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            RenderTexture.active = null;
            return texture.GetPixels();
        }

        static Vector3 ProbeDirection(int i, int j, int width, int height)
        {
            float lon = ((i + 0.5f) / width * 2f - 1f) * Mathf.PI, lat = ((j + 0.5f) / height - 0.5f) * Mathf.PI;
            return new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Sin(lon));
        }

        static Vector3 Advect(Vector3 d, Vector3 axis, float time)
        {
            float s = Mathf.Clamp(Vector3.Dot(d, axis), -1f, 1f);
            float east = 0.5f * (-Mathf.Sin(6f * Mathf.Abs(Mathf.Asin(s))) - 0.2f);
            float angle = -east * time;
            return d * Mathf.Cos(angle) + Vector3.Cross(axis, d) * Mathf.Sin(angle) + axis * (s * (1f - Mathf.Cos(angle)));
        }

        PlanetVisualController RenderedClouds(int seed, int resolution = 24)
        {
            PlanetVisualController visual = CreateVisual();
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = seed, resolution = resolution }));
            Assert.IsTrue(visual.CloudsVisible);
            return visual;
        }

        // E
        [Test]
        public void E_CloudField_IsSeamlessOnTheGpu_AtCubeEdgesAndPoles()
        {
            PlanetVisualController visual = RenderedClouds(868497);
            CloudField field = visual.CurrentCloudField;
            Material probe = Probe(extra);
            probe.SetTexture("_CloudCube", visual.CloudCube);
            const int width = 512, height = 256;
            Color[] raw = Render(probe, 0, width, height, extra);
            float worst = 0f;
            for (int j = 0; j < height; j++)
            {
                for (int i = 0; i < width; i++)
                {
                    Vector3 d = ProbeDirection(i, j, width, height);
                    worst = Mathf.Max(worst, Mathf.Abs(raw[i + j * width].r - field.Sample(field.Base, d)));
                }
            }
            TestContext.WriteLine($"GPU cube map vs CPU field: largest difference {worst:0.0000} (base channel)");
            Assert.Less(worst, 0.03f, "GPU sampling matches the CPU layout (no swapped or flipped faces)");

            // Full cloud potential without local detail (deterministic part), at fine steps across
            // cube edges and towards the poles: no jump anywhere.
            probe.SetVector("_CloudParams", new Vector4(visual.CloudThreshold, 1f, 0f, 1f));
            probe.SetVector("_CloudParams2", new Vector4(0.5f, 0f, 0f, 1f));
            probe.SetVector("_CloudParams3", new Vector4(0f, 1f, 0f, 0f));
            probe.SetVector("_CloudSeed", new Vector4(0f, 0f, 0f, CloudField.Softness));
            Color[] potential = Render(probe, 1, 2048, 1024, extra);
            // A seam would make the steps that cross a cube edge stand out from the steps inside the faces.
            float inside = 0f, crossing = 0f, polar = 0f;
            for (int j = 0; j < 1024; j++)
            {
                for (int i = 1; i < 2048; i++)
                {
                    Vector3 a = ProbeDirection(i - 1, j, 2048, 1024), b = ProbeDirection(i, j, 2048, 1024);
                    float stepValue = Mathf.Abs(potential[i + j * 2048].r - potential[i - 1 + j * 2048].r);
                    if (CloudField.FaceCoordinates(a, out _, out _) != CloudField.FaceCoordinates(b, out _, out _))
                    {
                        crossing = Mathf.Max(crossing, stepValue);
                    }
                    else
                    {
                        inside = Mathf.Max(inside, stepValue);
                    }
                    if (j < 12 || j > 1011)
                    {
                        polar = Mathf.Max(polar, stepValue);
                    }
                }
            }
            for (int i = 0; i < 2048; i += 4)
            {
                for (int j = 1; j < 1024; j++)
                {
                    Vector3 a = ProbeDirection(i, j - 1, 2048, 1024), b = ProbeDirection(i, j, 2048, 1024);
                    float stepValue = Mathf.Abs(potential[i + j * 2048].r - potential[i + (j - 1) * 2048].r);
                    if (CloudField.FaceCoordinates(a, out _, out _) != CloudField.FaceCoordinates(b, out _, out _))
                    {
                        crossing = Mathf.Max(crossing, stepValue);
                    }
                    else
                    {
                        inside = Mathf.Max(inside, stepValue);
                    }
                }
            }
            TestContext.WriteLine($"largest potential step between adjacent probe pixels (0.18 degree): inside faces {inside:0.0000}, "
                + $"across cube edges {crossing:0.0000}, near the poles {polar:0.0000}");
            Assert.LessOrEqual(crossing, inside * 1.1f + 0.002f, "No seam: crossing a cube edge is like any other step");
            Assert.LessOrEqual(polar, inside * 1.1f + 0.002f, "No polar discontinuity or pinching");
            Assert.Less(inside, 0.2f, "No discontinuity inside the faces either (crisp spiral arms are steep, not broken)");
        }

        // L
        [Test]
        public void L_Motion_IsDeterministicAtAGivenTime()
        {
            PlanetVisualController visual = RenderedClouds(1337);
            Material probe = Probe(extra);
            probe.SetTexture("_CloudCube", visual.CloudCube);
            probe.SetVector("_CloudParams", new Vector4(visual.CloudThreshold, 1f, 0.17f, 1f));
            probe.SetVector("_CloudParams2", new Vector4(0.5f, 0.5f, 0f, 1f));
            probe.SetVector("_CloudSeed", new Vector4(12f, -7f, 30f, CloudField.Softness));
            const float time = 0.8f;
            probe.SetVector("_CloudParams3", new Vector4(0f, 1f, 0f, time));
            Color[] first = Render(probe, 1, 256, 128, extra), second = Render(probe, 1, 256, 128, extra);
            CollectionAssert.AreEqual(first, second, "Same time, same clouds");
            probe.SetVector("_CloudParams3", new Vector4(0f, 1f, 0f, 0f));
            Color[] still = Render(probe, 1, 256, 128, extra);
            CollectionAssert.AreNotEqual(first, still, "Clouds move with time");

            // Deterministic part follows the documented zonal advection exactly (no detail, no convection).
            CloudField field = visual.CurrentCloudField;
            var s = new CloudSettings();
            probe.SetVector("_CloudParams", new Vector4(visual.CloudThreshold, 1f, 0f, 1f));
            probe.SetVector("_CloudParams2", new Vector4(s.weatherStrength, 0f, 0f, 1f));
            probe.SetVector("_CloudParams3", new Vector4(0f, 1f, 0f, time));
            Color[] advected = Render(probe, 1, 256, 128, extra);
            var noConvection = new CloudSettings { convection = 0f };
            float worst = 0f;
            for (int j = 4; j < 124; j++)
            {
                for (int i = 0; i < 256; i++)
                {
                    // Flow map: two samples advected by (phase - 1/2) x period, triangle cross-fade.
                    const float period = 0.25f;
                    float phase = time / period - Mathf.Floor(time / period);
                    float w1 = 1f - Mathf.Abs(2f * phase - 1f);
                    float phase2 = phase + 0.5f - Mathf.Floor(phase + 0.5f);
                    Vector3 d1 = Advect(ProbeDirection(i, j, 256, 128), Vector3.up, (phase - 0.5f) * period);
                    Vector3 d2 = Advect(ProbeDirection(i, j, 256, 128), Vector3.up, (phase2 - 0.5f) * period);
                    float p1 = CloudField.Potential(field.Sample(field.Base, d1), field.Sample(field.Systems, d1), 0f, field.Sample(field.Envelope, d1), noConvection, visual.CloudThreshold);
                    float p2 = CloudField.Potential(field.Sample(field.Base, d2), field.Sample(field.Systems, d2), 0f, field.Sample(field.Envelope, d2), noConvection, visual.CloudThreshold);
                    float cpu = Mathf.Lerp(p2, p1, w1);
                    worst = Mathf.Max(worst, Mathf.Abs(cpu - advected[i + j * 256].r));
                }
            }
            TestContext.WriteLine($"advected GPU potential vs CPU prediction at t = {time}: largest difference {worst:0.0000}");
            Assert.Less(worst, 0.04f);

            // Controller: the simulation time is only what it is set to (no frame dependence).
            visual.CloudSimulationTime = 2.5;
            visual.ApplyCloudTime();
            Assert.AreEqual(2.5f, BlockVector(visual.transform.Find("Terrain +Y").GetComponent<MeshRenderer>(), "_CloudParams3").w, 1e-6f);
            Assert.IsFalse(visual.Clouds.motion, "Motion off by default");
        }

        // ------------------------------------------------------------ F, G, H (plausibility)

        // F
        [Test]
        public void F_CloudCover_FollowsClimateTendencies()
        {
            var s = new CloudSettings();
            foreach (int seed in ReferenceSeeds)
            {
                CloudField field = Field(seed);
                ClimateResult climate = Climate(seed);
                float threshold = field.Threshold(s);
                float[] sum = new float[4];
                int[] count = new int[4];
                float dry = 0f, wet = 0f;
                int dryCount = 0, wetCount = 0;
                foreach (Vector3 d in Fibonacci(12000))
                {
                    float a = Mathf.Abs(Latitude(d));
                    int band = a < 12f ? 0 : a < 18f ? -1 : a < 32f ? 1 : a < 40f ? -1 : a < 65f ? 2 : 3;
                    float cover = field.Coverage(d, s, threshold);
                    if (band >= 0)
                    {
                        sum[band] += cover;
                        count[band]++;
                    }
                    float p = climate.Sample(climate.Precipitation, d);
                    if (p < 0.4f) { dry += cover; dryCount++; }
                    if (p > 1.6f) { wet += cover; wetCount++; }
                }
                float tropics = sum[0] / count[0], subtropics = sum[1] / count[1], midlatitudes = sum[2] / count[2], polar = sum[3] / count[3];
                TestContext.WriteLine($"seed {seed}: cover tropics {tropics:0.00}, subtropics {subtropics:0.00}, midlatitudes {midlatitudes:0.00}, polar {polar:0.00}; "
                    + $"dry regions (P < 0.4 x mean) {dry / Mathf.Max(1, dryCount):0.00}, wet (P > 1.6) {wet / Mathf.Max(1, wetCount):0.00}; {field.WeatherSystems.Count} systems");
                Assert.Greater(tropics, subtropics, "Equatorial convection over dry subtropics");
                Assert.Greater(midlatitudes, subtropics, "Storm belts over dry subtropics");
                Assert.Greater(wet / Mathf.Max(1, wetCount), dry / Mathf.Max(1, dryCount) + 0.1f, "Dry regions are often clearer");
            }
        }

        // G
        [Test]
        public void G_Clouds_AreNotAPrecipitationMap()
        {
            var s = new CloudSettings();
            foreach (int seed in ReferenceSeeds)
            {
                CloudField field = Field(seed);
                ClimateResult climate = Climate(seed);
                float threshold = field.Threshold(s);
                var cover = new List<float>();
                var rain = new List<float>();
                foreach (Vector3 d in Fibonacci(8000))
                {
                    cover.Add(field.Coverage(d, s, threshold));
                    rain.Add(Mathf.Log(Mathf.Max(0.05f, climate.Sample(climate.Precipitation, d))));
                }
                float r = Correlation(cover, rain);
                TestContext.WriteLine($"seed {seed}: correlation cloud cover / log precipitation {r:0.00}");
                Assert.That(r, Is.InRange(0.1f, 0.7f), "Informed by, but not a copy of, the precipitation climatology");
            }
        }

        // H
        [Test]
        public void H_WeatherSystems_AreBoundedAndVaried()
        {
            var radii = new List<float>();
            for (int seed = 1; seed <= 40; seed++)
            {
                var field = new CloudField(seed * 7919, null, Vector3.up);
                IReadOnlyList<WeatherSystem> systems = field.WeatherSystems;
                Assert.That(systems.Count, Is.InRange(4, 10), "Few systems, never a planet of cyclones");
                for (int i = 0; i < systems.Count; i++)
                {
                    WeatherSystem s = systems[i];
                    radii.Add(s.RadiusDegrees);
                    Assert.That(s.RadiusDegrees, Is.InRange(3f, 16.5f));
                    float latitude = Mathf.Abs(Latitude(s.Center));
                    Assert.That(latitude, s.Tropical ? Is.InRange(9f, 23f) : Is.InRange(37f, 63f), "Systems sit in their circulation belts");
                    Assert.AreEqual(Mathf.Sign(Latitude(s.Center)), s.Hand, "Cyclonic sense flips between hemispheres");
                    for (int k = 0; k < i; k++)
                    {
                        Assert.GreaterOrEqual(Vector3.Angle(s.Center, systems[k].Center), 19.9f, "Spaced apart");
                    }
                }
                // Bounded: no contribution beyond three radii of every system; values in [0, 1].
                foreach (Vector3 d in Fibonacci(1500))
                {
                    float v = field.SystemsAt(d, out float envelope);
                    Assert.That(v, Is.InRange(0f, 1f));
                    Assert.That(envelope, Is.InRange(0f, 1f));
                    if (systems.All(s => Vector3.Angle(d, s.Center) > 3f * s.RadiusDegrees))
                    {
                        Assert.AreEqual(0f, envelope, "Bounded: no influence beyond three radii");
                    }
                }
            }
            Assert.Greater(radii.Max() - radii.Min(), 8f, "Varied sizes");
        }

        // ------------------------------------------------------------ I, J, K, M, N, O (controller)

        static Vector4 BlockVector(Renderer renderer, string name)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return block.GetVector(name);
        }

        // I
        [Test]
        public void I_CloudSettings_InvalidateNoScientificCache()
        {
            PlanetVisualController visual = CreateVisual();
            visual.Lakes.showLakes = true;
            visual.Rivers.showRivers = true;
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 868497, resolution = 24 }));
            Assert.IsTrue(visual.CurrentCloudField.ClimateInformed, "Natural Planet computed the climate: clouds use it");
            int climate = visual.ClimateCache.ComputeCount, biomes = visual.BiomeCache.ComputeCount, slope = visual.SlopeCache.ComputeCount;
            int hydrology = visual.HydrologyCache.ComputeCount, lakes = visual.LakeCache.SelectionCount, natural = visual.NaturalSurfaceBuildCount;
            int bathymetry = visual.BathymetryCache.FieldComputeCount, cloudFields = visual.CloudFieldCache.ComputeCount;
            CloudSettings s = visual.Clouds;
            s.coverage = 0.7f;
            s.density = 0.9f;
            s.detail = 0.1f;
            s.weatherStrength = 1f;
            s.convection = 0.2f;
            s.shadowStrength = 0.6f;
            s.altitude = 0.015f;
            s.motion = true;
            visual.ApplyOceanAtmosphere();
            Assert.AreEqual(cloudFields, visual.CloudFieldCache.ComputeCount, "Appearance settings reuse the cloud field");
            s.weatherSeed = 777;
            visual.ApplyOceanAtmosphere();
            Assert.AreEqual(cloudFields + 1, visual.CloudFieldCache.ComputeCount, "A new sky rebuilds the cloud field only");
            visual.RefreshColors();
            Assert.AreEqual(climate, visual.ClimateCache.ComputeCount);
            Assert.AreEqual(biomes, visual.BiomeCache.ComputeCount);
            Assert.AreEqual(slope, visual.SlopeCache.ComputeCount);
            Assert.AreEqual(hydrology, visual.HydrologyCache.ComputeCount);
            Assert.AreEqual(lakes, visual.LakeCache.SelectionCount);
            Assert.AreEqual(natural, visual.NaturalSurfaceBuildCount);
            Assert.AreEqual(bathymetry, visual.BathymetryCache.FieldComputeCount);

            // Classic palette (no climate computed): clouds fall back to latitude-only, never force the climate.
            TearDown();
            visual = CreateVisual();
            visual.Surface.enabled = false;
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 868497, resolution = 16 }));
            Assert.IsTrue(visual.CloudsVisible);
            Assert.IsFalse(visual.CurrentCloudField.ClimateInformed);
            Assert.AreEqual(0, visual.ClimateCache.ComputeCount, "Showing clouds never computes the climate");
        }

        // J, K
        [Test]
        public void JK_ToggleAndDebugViews_RemoveCloudsAndShadows()
        {
            PlanetVisualController visual = RenderedClouds(536452);
            MeshRenderer terrain = visual.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(2)).GetComponent<MeshRenderer>();
            Assert.AreEqual(1f, BlockVector(terrain, "_CloudParams").w);
            Assert.AreEqual(visual.Clouds.shadowStrength, BlockVector(terrain, "_CloudParams2").z, 1e-6f);
            Assert.AreEqual(visual.CloudRadius, BlockVector(terrain, "_CloudParams2").w, 1e-4f, "Shadow projection uses the shell radius");
            var oceanBlock = new MaterialPropertyBlock();
            visual.OceanRenderer.GetPropertyBlock(oceanBlock);
            Assert.AreEqual(visual.Planet.OceanRadius, oceanBlock.GetFloat("_CloudLocalScale"), 1e-4f);

            // J: off = shell not drawn, shadow branch disabled in terrain and ocean (V0.1.2B shading path).
            visual.Clouds.showClouds = false;
            visual.ApplyOceanAtmosphere();
            Assert.IsFalse(visual.CloudsVisible);
            Assert.AreEqual(0f, BlockVector(terrain, "_CloudParams").w);
            Assert.AreEqual(0f, BlockVector(visual.OceanRenderer, "_CloudParams").w);
            Assert.AreEqual(0f, BlockVector(terrain, "_CloudParams2").z);
            visual.Clouds.showClouds = true;
            visual.ApplyOceanAtmosphere();
            Assert.IsTrue(visual.CloudsVisible);

            // K: scientific debug views (and the cloud data views themselves): no cloud shell, no shadows.
            foreach (PlanetDebugView view in new[] { PlanetDebugView.ClimateTemperature, PlanetDebugView.BiomeMap, PlanetDebugView.FlowAccumulation,
                         PlanetDebugView.LakeDepth, PlanetDebugView.CloudCoverage, PlanetDebugView.CloudStructure })
            {
                visual.DebugView = view;
                visual.RefreshColors();
                Assert.IsFalse(visual.CloudsVisible, view + ": no cloud shell");
                Assert.IsFalse(terrain.HasPropertyBlock(), view + ": no cloud shadows on the debug material");
                Color[] colors = visual.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(2)).GetComponent<MeshFilter>().sharedMesh.colors;
                Assert.IsFalse(colors.Contains(Color.magenta), view + " has its data");
            }
            visual.DebugView = PlanetDebugView.None;
            visual.RefreshColors();
            Assert.IsTrue(visual.CloudsVisible);
        }

        // M
        [Test]
        public void M_Resources_AreReusedAndReleased()
        {
            PlanetVisualController visual = RenderedClouds(1337, 16);
            Cubemap cube = visual.CloudCube;
            Mesh mesh = visual.CloudMesh;
            Material material = visual.CloudMaterialInUse;
            int uploads = visual.CloudUploadCount;
            for (int i = 0; i < 5; i++)
            {
                visual.Clouds.coverage = 0.2f + 0.1f * i;
                visual.Clouds.weatherSeed = i + 1;
                visual.Render(new PlanetInstance(new PlanetDefinition { seed = 1337 + i, resolution = 16 }));
            }
            Assert.AreSame(cube, visual.CloudCube, "Cube map reused (refilled)");
            Assert.AreSame(mesh, visual.CloudMesh);
            Assert.AreSame(material, visual.CloudMaterialInUse);
            Assert.AreEqual(uploads + 5, visual.CloudUploadCount);
            Assert.AreEqual(7, visual.GeneratedObjectCount, "Clouds add no GameObject");
            Object.DestroyImmediate(root);
            root = null;
            Assert.IsTrue(cube == null && mesh == null && material == null, "Released with the planet");
        }

        // N
        [Test]
        public void N_Shaders_CompileAndMissingOnesFallBackSafely()
        {
            foreach (string name in new[] { PlanetVisualController.CloudShaderName, PlanetVisualController.TerrainShaderName,
                         PlanetVisualController.OceanShaderName, PlanetVisualController.AtmosphereShaderName })
            {
                Shader shader = Shader.Find(name);
                Assert.IsNotNull(shader, name);
                Assert.IsFalse(UnityEditor.ShaderUtil.ShaderHasError(shader), name + " compiles");
            }
            Shader clouds = Shader.Find(PlanetVisualController.CloudShaderName);
            foreach (string p in new[] { "_CloudCube", "_CloudParams", "_CloudParams2", "_CloudParams3", "_CloudSeed", "_LightWrap", "_AtmoParams" })
            {
                Assert.GreaterOrEqual(clouds.FindPropertyIndex(p), 0, p);
            }
            var plain = new Material(clouds);
            extra.Add(plain);
            Assert.AreEqual(3050, plain.renderQueue, "Between the ocean (3000) and the atmosphere (3100)");
            var terrain = new Material(Shader.Find(PlanetVisualController.TerrainShaderName));
            extra.Add(terrain);
            Assert.AreEqual(0f, terrain.GetVector("_CloudParams").w, "Without the controller: no cloud shadows");

            PlanetVisualController visual = CreateVisual();
            var unlit = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            extra.Add(unlit);
            visual.SetCloudMaterial(unlit);
            LogAssert.Expect(LogType.Warning, new Regex("clouds disabled"));
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 1337, resolution = 16 }));
            Assert.IsFalse(visual.CloudsVisible, "No opaque generic sphere");
            Assert.IsTrue(visual.AtmosphereVisible, "Everything else unaffected");
        }

        // O
        [Test]
        public void O_Performance_AndAllocation_Report()
        {
            foreach (int seed in ReferenceSeeds)
            {
                CloudField field = Field(seed);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                float threshold = field.Threshold(new CloudSettings());
                double thresholdMs = watch.Elapsed.TotalMilliseconds;
                TestContext.WriteLine($"seed {seed}: cloud field {field.Milliseconds:0} ms, coverage threshold {thresholdMs:0.0} ms, {field.WeatherSystems.Count} weather systems "
                    + $"({field.WeatherSystems.Count(s => s.Tropical)} tropical), threshold {threshold:0.000}");
                Assert.Less(thresholdMs, 50.0);
            }
            PlanetVisualController visual = CreateVisual();
            visual.Climate.resolution = ClimateSettings.DefaultResolution;
            var definition = new PlanetDefinition { seed = 868497, resolution = 256 };
            var timer = System.Diagnostics.Stopwatch.StartNew();
            visual.Render(new PlanetInstance(definition));
            double first = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            visual.Render(new PlanetInstance(definition));
            double warm = timer.Elapsed.TotalMilliseconds;
            visual.Clouds.showClouds = false;
            visual.ApplyOceanAtmosphere();
            timer.Restart();
            visual.Render(new PlanetInstance(definition));
            double warmOff = timer.Elapsed.TotalMilliseconds;
            visual.Clouds.showClouds = true;
            visual.ApplyOceanAtmosphere();
            timer.Restart();
            for (int i = 0; i < 20; i++)
            {
                visual.Clouds.coverage = 0.3f + 0.01f * i;
                visual.ApplyOceanAtmosphere();
            }
            double coverageChange = timer.Elapsed.TotalMilliseconds / 20.0;
            visual.ApplyCloudTime();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                visual.CloudSimulationTime = i * 0.01;
                visual.ApplyCloudTime();
            }
            long perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
            TestContext.WriteLine($"Terrain 256 / climate 64: first render {first:0} ms, warm re-render {warm:0} ms (clouds off {warmOff:0} ms); "
                + $"coverage change {coverageChange:0.0} ms; motion frame update {perFrame} bytes allocated");
            Assert.Less(perFrame, 64, "No per-frame allocation for cloud motion");
            Assert.Less(coverageChange, 50.0);
        }
    }
}
