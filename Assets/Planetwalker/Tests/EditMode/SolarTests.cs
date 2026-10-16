using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Planetwalker.Bootstrap;
using Planetwalker.CameraControl;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using Planetwalker.UI;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>V0.1.2D planetary lighting and time. Letters refer to the milestone's test list (A - P).</summary>
    public class SolarTests
    {
        readonly List<Object> created = new List<Object>();
        double fakeNow;

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            created.Clear();
        }

        (PlanetVisualController Visual, Light Sun) CreateLitVisual(int seed = 1337, int resolution = 16)
        {
            var sunObject = new GameObject("Test Star");
            created.Add(sunObject);
            sunObject.transform.rotation = Quaternion.Euler(20f, 50f, 0f);
            var sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            var root = new GameObject("Solar Test");
            created.Add(root);
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
            visual.Climate.resolution = ClimateSettings.LowResolution;
            fakeNow = 1000.0;
            visual.SolarRealTime = () => fakeNow;
            visual.SetSun(sun);
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = seed, resolution = resolution }));
            return (visual, sun);
        }

        static void AssertDirection(Vector3 expected, Vector3 actual, string message = null, float degrees = 0.01f)
        {
            Assert.Less(Vector3.Angle(expected, actual), degrees, message);
        }

        // ------------------------------------------------------------ A - E (clock)

        // A, B
        [Test]
        public void AB_SolarTime_IsDeterministicAndFrameRateIndependent()
        {
            var clock = new SolarClock();
            clock.Start(100.0, 0.2f);
            Assert.AreEqual(0.45f, clock.Evaluate(160.0, 240f), 1e-6f);
            Assert.AreEqual(clock.Evaluate(160.0, 240f), clock.Evaluate(160.0, 240f));

            // Same end time reached with 1000 small frames or 3 big ones: same solar time and sun.
            var (fine, _) = CreateLitVisual();
            fine.SetCycleDuration(120f);
            fine.SetDayNightCycle(true);
            for (int i = 0; i < 1000; i++)
            {
                fakeNow += 0.017;
                fine.TickSolar();
            }
            float fineTime = fine.Solar.solarTime;
            Vector3 fineSun = fine.SunDirection;
            fakeNow = 1000.0;
            var (coarse, _) = CreateLitVisual();
            coarse.SetCycleDuration(120f);
            coarse.SetDayNightCycle(true);
            foreach (double step in new[] { 5.0, 0.002, 11.998 })
            {
                fakeNow += step;
                coarse.TickSolar();
            }
            Assert.AreEqual(1000 * 0.017, 17.0, 1e-9);
            Assert.AreEqual(fineTime, coarse.Solar.solarTime, 1e-5f, "Frame rate does not matter");
            AssertDirection(fineSun, coarse.SunDirection);
            Assert.AreEqual(SolarClock.Wrap(0.5f + 17f / 120f), fineTime, 1e-5f);
        }

        // C
        [Test]
        public void C_PauseResume_NeverDrifts()
        {
            var (visual, _) = CreateLitVisual();
            visual.SetCycleDuration(100f);
            visual.SetDayNightCycle(true);
            fakeNow += 10.0;
            visual.TickSolar();
            visual.SetSolarPaused(true);
            float paused = visual.Solar.solarTime;
            Assert.AreEqual(0.6f, paused, 1e-5f);
            fakeNow += 500.0; // time passes while paused
            visual.TickSolar();
            Assert.AreEqual(paused, visual.Solar.solarTime, "Frozen while paused");
            for (int i = 0; i < 200; i++)
            {
                visual.SetSolarPaused(false);
                visual.SetSolarPaused(true);
            }
            Assert.AreEqual(paused, visual.Solar.solarTime, "Repeated pause / resume: no drift");
            visual.SetSolarPaused(false);
            fakeNow += 25.0;
            visual.TickSolar();
            Assert.AreEqual(0.85f, visual.Solar.solarTime, 1e-5f, "Continues from the paused value");
        }

        // D
        [Test]
        public void D_SolarTime_WrapsAroundWithoutAJump()
        {
            var clock = new SolarClock();
            clock.Start(0.0, 0.9f);
            Assert.AreEqual(0.1f, clock.Evaluate(48.0, 240f), 1e-5f);
            Assert.AreEqual(0.9f, clock.Evaluate(240.0 * 1000, 240f), 1e-3f, "Whole cycles later: same time");
            var (visual, _) = CreateLitVisual();
            // The sun moves continuously across 1 -> 0.
            Assert.Less(Vector3.Angle(visual.SolarDirection(0.9999f), visual.SolarDirection(0f)), 0.05f);
            Assert.Less(Vector3.Angle(visual.SolarDirection(0f), visual.SolarDirection(0.0001f)), 0.05f);
            Assert.AreEqual(0f, SolarClock.Wrap(1f));
            Assert.AreEqual(0.25f, SolarClock.Wrap(-0.75f), 1e-6f);
            Assert.AreEqual("12:00", SolarSettings.Format(0.5f));
            Assert.AreEqual("18:00", SolarSettings.Format(0.75f));
        }

        // E
        [Test]
        public void E_Scrubbing_GivesTheExpectedSunDirection()
        {
            var (visual, sun) = CreateLitVisual();
            Vector3 baseSun = visual.BaseSunDirection;
            AssertDirection(Quaternion.Euler(20f, 50f, 0f) * Vector3.back, baseSun, "Default sun = the star's original orientation");
            Vector3 axis = Vector3.up; // climate default rotation axis, planet unrotated
            foreach (float t in new[] { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f })
            {
                visual.SetSolarTime(t);
                Vector3 expected = Quaternion.AngleAxis(-(t - 0.5f) * 360f, axis) * baseSun;
                AssertDirection(expected, visual.SunDirection, "t = " + t);
                AssertDirection(-expected, sun.transform.forward, "The light itself follows");
                Assert.AreEqual(Vector3.Dot(baseSun, axis), Vector3.Dot(visual.SunDirection, axis), 1e-5f, "Fixed declination (no seasons)");
            }
            // Later solar time: the sun moves westwards over a point (the planet's day, rotation around +axis).
            visual.SetSolarTime(0.5f);
            Vector3 noon = visual.SunDirection;
            visual.SetSolarTime(0.51f);
            Vector3 later = visual.SunDirection;
            Vector3 east = Vector3.Cross(axis, noon).normalized;
            Assert.Less(Vector3.Dot(later - noon, east), 0f, "Westward");
        }

        // ------------------------------------------------------------ F - J (coherence and independence)

        // F
        [Test]
        public void F_EveryShader_ReadsTheSameSun()
        {
            var (visual, sun) = CreateLitVisual();
            visual.SetSolarTime(0.3f);
            AssertDirection(visual.SunDirection, -sun.transform.forward, "One authoritative direction: the star light");
            string folder = Path.Combine(Application.dataPath, "Planetwalker", "Shaders");
            foreach (string file in new[] { "PlanetTerrain.shader", "PlanetOcean.shader", "PlanetAtmosphere.shader", "PlanetClouds.shader" })
            {
                string source = File.ReadAllText(Path.Combine(folder, file));
                StringAssert.Contains("GetMainLight()", source, file + " lights with URP's main light");
                foreach (string forbidden in new[] { "_SunDirection", "_SunDir", "_LightDir", "_SolarDirection" })
                {
                    StringAssert.DoesNotContain(forbidden, source, file + " has no independent sun vector");
                }
            }
            foreach (string file in new[] { "PlanetAtmosphereCommon.hlsl", "PlanetCloudsCommon.hlsl" })
            {
                string source = File.ReadAllText(Path.Combine(folder, file));
                StringAssert.DoesNotContain("_SunDir", source);
            }
            // Cloud shadows: the shadow function receives the main light direction in terrain and ocean.
            StringAssert.Contains("CloudShadow(input.positionOS * _CloudLocalScale, normalize(TransformWorldToObjectDir(mainLight.direction)))",
                File.ReadAllText(Path.Combine(folder, "PlanetTerrain.shader")));
            StringAssert.Contains("CloudShadow(input.positionOS * _CloudLocalScale, normalize(TransformWorldToObjectDir(mainLight.direction)))",
                File.ReadAllText(Path.Combine(folder, "PlanetOcean.shader")));
        }

        // G, H, I
        [Test]
        public void GHI_SolarChanges_RecomputeNothing_AndLeaveCloudTimeAlone()
        {
            var (visual, _) = CreateLitVisual(868497, 24);
            visual.RefreshColors(); // colors compared below come from the same recolor path
            int climate = visual.ClimateCache.ComputeCount, biomes = visual.BiomeCache.ComputeCount, slope = visual.SlopeCache.ComputeCount;
            int hydrology = visual.HydrologyCache.ComputeCount, bathymetry = visual.BathymetryCache.FieldComputeCount;
            int cloudFields = visual.CloudFieldCache.ComputeCount, uploads = visual.CloudUploadCount, natural = visual.NaturalSurfaceBuildCount;
            float[] scores = (float[])visual.CurrentBiomes.Scores.Clone();
            Mesh face = visual.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(3)).GetComponent<MeshFilter>().sharedMesh;
            Vector3[] vertices = face.vertices;
            Color[] colors = face.colors;
            visual.CloudSimulationTime = 1.2;
            visual.ApplyCloudTime();

            visual.SetDayNightCycle(true);
            for (int i = 0; i < 50; i++)
            {
                fakeNow += 3.0;
                visual.TickSolar();
                visual.SetSolarTime(i / 50f);
            }
            visual.SetSolarPaused(true);
            visual.Solar.twilightWarmth = 0.9f;
            visual.Solar.nightAmbient = 0.2f;
            visual.ApplyOceanAtmosphere();
            visual.RefreshColors();

            Assert.AreEqual(climate, visual.ClimateCache.ComputeCount, "G");
            Assert.AreEqual(biomes, visual.BiomeCache.ComputeCount, "G");
            Assert.AreEqual(slope, visual.SlopeCache.ComputeCount, "G");
            Assert.AreEqual(hydrology, visual.HydrologyCache.ComputeCount, "G");
            Assert.AreEqual(bathymetry, visual.BathymetryCache.FieldComputeCount, "G");
            Assert.AreEqual(cloudFields, visual.CloudFieldCache.ComputeCount, "G: weather field untouched");
            Assert.AreEqual(uploads, visual.CloudUploadCount, "G");
            Assert.AreEqual(natural, visual.NaturalSurfaceBuildCount, "G");
            Assert.AreEqual(1.2, visual.CloudSimulationTime, "H: cloud animation time independent of the solar clock");
            Assert.IsFalse(visual.Clouds.motion, "H: the solar clock does not touch cloud motion");
            CollectionAssert.AreEqual(scores, visual.CurrentBiomes.Scores, "I");
            CollectionAssert.AreEqual(vertices, face.vertices, "I: terrain geometry untouched");
            CollectionAssert.AreEqual(colors, face.colors, "I: surface colors untouched (lighting is in the shaders)");

            // H, the other way round: cloud motion time does not move the sun.
            float solarTime = visual.Solar.solarTime;
            visual.CloudSimulationTime = 5.0;
            visual.ApplyCloudTime();
            fakeNow += 100.0;
            visual.TickSolar();
            Assert.AreEqual(solarTime, visual.Solar.solarTime, "Paused sun stays paused while clouds move");
        }

        // J
        [Test]
        public void J_DebugViews_IgnoreTheLighting()
        {
            var (visual, _) = CreateLitVisual(609538, 16);
            MeshRenderer terrain = visual.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(4)).GetComponent<MeshRenderer>();
            var block = new MaterialPropertyBlock();
            terrain.GetPropertyBlock(block);
            Assert.AreEqual(visual.Solar.nightAmbient, block.GetFloat("_NightAmbient"), 1e-6f, "Natural view: night darkness on the terrain");
            foreach (PlanetDebugView view in new[] { PlanetDebugView.ClimateTemperature, PlanetDebugView.BiomeMap, PlanetDebugView.FlowAccumulation, PlanetDebugView.CloudCoverage })
            {
                visual.DebugView = view;
                visual.RefreshColors();
                Color[] before = visual.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(4)).GetComponent<MeshFilter>().sharedMesh.colors;
                visual.SetSolarTime(0.05f);
                visual.Solar.twilightWarmth = 1f;
                visual.ApplyOceanAtmosphere();
                visual.RefreshColors();
                Color[] after = visual.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(4)).GetComponent<MeshFilter>().sharedMesh.colors;
                CollectionAssert.AreEqual(before, after, view + ": data colors unchanged");
                Assert.IsFalse(terrain.HasPropertyBlock(), view + ": unlit debug material, no lighting properties");
                Assert.IsFalse(visual.CloudsVisible || visual.AtmosphereVisible || visual.OceanShadingActive, view.ToString());
                visual.SetSolarTime(0.5f);
                visual.Solar.twilightWarmth = 0.5f;
            }
            visual.DebugView = PlanetDebugView.None;
            visual.RefreshColors();
            Assert.IsTrue(visual.CloudsVisible && visual.AtmosphereVisible && visual.OceanShadingActive, "Natural Planet restored");
            Assert.IsTrue(terrain.HasPropertyBlock());
        }

        // ------------------------------------------------------------ K, L (contemplation)

        // K, L
        [Test]
        public void KL_Contemplation_HidesAndRestoresTheUi_CameraStaysUsable()
        {
            var before = new HashSet<GameObject>(Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None));
            var bootstrapObject = new GameObject("Bootstrap Test");
            var bootstrap = bootstrapObject.AddComponent<PlanetBootstrap>();
            bootstrap.EnsureSceneObjects();
            foreach (GameObject o in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                if (!before.Contains(o) && o.transform.parent == null) created.Add(o);
            }
            PlanetGenerationPanel panel = Object.FindAnyObjectByType<PlanetGenerationPanel>();
            Assert.IsNotNull(panel);
            Transform frame = panel.transform.Find("Panel");
            Assert.IsTrue(frame.gameObject.activeSelf);
            Assert.IsNotNull(bootstrap.Visual.SunLight, "The bootstrap hands its star to the solar clock");

            panel.SetContemplation(true);
            Assert.IsTrue(panel.Contemplating);
            Assert.IsFalse(frame.gameObject.activeSelf, "K: controls hidden");
            Button show = panel.GetComponentsInChildren<Button>(false).Single();
            Assert.AreEqual("Show controls (H)", show.GetComponentInChildren<Text>().text, "Never trapped: a visible way back");
            // L: nothing but that small button can catch the pointer, the orbit camera stays enabled.
            Graphic[] blocking = panel.GetComponentsInChildren<Graphic>(false).Where(g => g.raycastTarget).ToArray();
            Assert.IsTrue(blocking.All(g => g.transform.IsChildOf(show.transform)), "Only the restore button takes input");
            Assert.IsTrue(bootstrap.OrbitCamera.enabled);
            Assert.AreSame(bootstrap.Visual.transform, bootstrap.OrbitCamera.Target);

            panel.SetContemplation(false); // what the button, H and Esc call (listeners are registered in Play mode)
            Assert.IsFalse(panel.Contemplating);
            Assert.IsTrue(frame.gameObject.activeSelf, "K: controls restored");
            Assert.IsFalse(show.gameObject.activeSelf);
        }

        // ------------------------------------------------------------ M - P

        // M, N
        [Test]
        public void MN_ResourcesReused_RegenerationKeepsTheSun_ResetRestoresIt()
        {
            var (visual, sun) = CreateLitVisual();
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length, materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            int textures = Resources.FindObjectsOfTypeAll<Texture>().Length;
            for (int i = 0; i < 100; i++)
            {
                visual.SetSolarTime(i * 0.013f);
                visual.Solar.twilightWarmth = (i % 10) / 10f;
                visual.ApplyOceanAtmosphere();
            }
            Assert.AreEqual(meshes, Resources.FindObjectsOfTypeAll<Mesh>().Length, "M");
            Assert.AreEqual(materials, Resources.FindObjectsOfTypeAll<Material>().Length, "M");
            Assert.AreEqual(textures, Resources.FindObjectsOfTypeAll<Texture>().Length, "M");

            visual.SetSolarTime(0.8f);
            Vector3 evening = visual.SunDirection;
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 99, resolution = 16 }));
            AssertDirection(evening, visual.SunDirection, "N: regeneration keeps the solar time (presentation state)");
            AssertDirection(evening, -sun.transform.forward);
            visual.SetDayNightCycle(true);
            visual.ResetSun();
            Assert.AreEqual(SolarSettings.DefaultSolarTime, visual.Solar.solarTime);
            Assert.IsFalse(visual.Solar.dayNightCycle);
            AssertDirection(visual.BaseSunDirection, -sun.transform.forward, "N: reset = the validated default sun");
        }

        // O
        [Test]
        public void O_SolarHotPath_DoesNotAllocate()
        {
            var (visual, _) = CreateLitVisual();
            visual.SetDayNightCycle(true);
            for (int i = 0; i < 10; i++)
            {
                fakeNow += 0.016;
                visual.TickSolar();
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                fakeNow += 0.016;
                visual.TickSolar();
            }
            double perTick = watch.Elapsed.TotalMilliseconds / 1000.0;
            long allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
            TestContext.WriteLine($"solar tick: {perTick * 1000.0:0.0} us, {allocated} bytes allocated per frame");
            Assert.Less(allocated, 16, "No per-frame allocation");
        }

        // P
        [Test]
        public void P_Shaders_AndMissingSunFallBackSafely()
        {
            foreach (string name in new[] { PlanetVisualController.TerrainShaderName, PlanetVisualController.OceanShaderName,
                         PlanetVisualController.CloudShaderName, PlanetVisualController.AtmosphereShaderName })
            {
                Shader shader = Shader.Find(name);
                Assert.IsNotNull(shader, name);
                Assert.IsFalse(UnityEditor.ShaderUtil.ShaderHasError(shader), name + " compiles");
            }
            foreach (string name in new[] { PlanetVisualController.TerrainShaderName, PlanetVisualController.OceanShaderName })
            {
                var material = new Material(Shader.Find(name));
                created.Add(material);
                Assert.AreEqual(1f, material.GetFloat("_NightAmbient"), name + ": without the controller, the previous ambient (no change)");
            }
            var clouds = new Material(Shader.Find(PlanetVisualController.CloudShaderName));
            created.Add(clouds);
            Assert.AreEqual(0.5f, clouds.GetFloat("_TwilightWarmth"), "Default warmth = the V0.1.2C cloud tint");

            // No star: the clock still runs and nothing throws.
            var root = new GameObject("No Sun");
            created.Add(root);
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Climate.resolution = ClimateSettings.LowResolution;
            visual.SetSun(null);
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 1337, resolution = 8 }));
            visual.SetSolarTime(0.3f);
            visual.TickSolar();
            Assert.IsNull(visual.SunLight);
            Assert.AreEqual(0.3f, visual.Solar.solarTime, 1e-6f);
        }

        // Terminator math: the solar direction is a pure rotation (sun-facing hemisphere preserved).
        [Test]
        public void SunFacingHemisphere_FollowsTheSolarTime()
        {
            var (visual, _) = CreateLitVisual();
            Vector3 point = visual.BaseSunDirection; // subsolar point at noon
            visual.SetSolarTime(0.5f);
            Assert.AreEqual(1f, Vector3.Dot(point, visual.SunDirection), 1e-5f, "Noon: overhead");
            visual.SetSolarTime(0f);
            Assert.Less(Vector3.Dot(point, visual.SunDirection), 0f, "Midnight: night side");
            // Over a day the point is lit about half of the time (fixed declination of the default sun).
            int lit = 0;
            for (int i = 0; i < 360; i++)
            {
                lit += Vector3.Dot(Vector3.ProjectOnPlane(point, Vector3.up).normalized, visual.SolarDirection(i / 360f)) > 0f ? 1 : 0;
            }
            Assert.That(lit, Is.InRange(150, 210));
        }
    }
}
