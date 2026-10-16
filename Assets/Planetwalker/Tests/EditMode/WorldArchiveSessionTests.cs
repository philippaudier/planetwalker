using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Planetwalker.Bootstrap;
using Planetwalker.CameraControl;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Persistence;
using Planetwalker.Rendering;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using TestHost = Planetwalker.Tests.WorldTestHost;

namespace Planetwalker.Tests
{
    /// <summary>
    /// World archive session (V0.1.3A): save / load against a live planet (visual controller, orbit camera, solar
    /// clock), unsaved-change tracking, regeneration counts, deterministic reconstruction and leaks. Archives go
    /// to an isolated temporary folder.
    /// </summary>
    public class WorldArchiveSessionTests
    {
        readonly List<Object> created = new List<Object>();
        string directory;
        WorldArchiveStore store;

        [SetUp]
        public void SetUp()
        {
            directory = WorldArchiveTestUtil.CreateTempDirectory();
            store = new WorldArchiveStore(directory);
            Assert.AreNotEqual(Path.GetFullPath(PlanetBootstrap.DefaultWorldsDirectory), store.Directory, "Never the user's archives");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in created)
            {
                if (o != null)
                {
                    Object.DestroyImmediate(o);
                }
            }
            created.Clear();
            WorldArchiveTestUtil.DeleteTempDirectory(directory);
        }

        TestHost CreateHost(WorldConfiguration configuration = null)
        {
            var host = new TestHost(created);
            if (configuration != null)
            {
                host.Apply(configuration);
            }
            host.Regenerate();
            host.Session = new WorldSession(host, store);
            host.Session.ResetBaseline();
            return host;
        }

        /// <summary>A different planet in a fresh host: what a restarted application looks like before loading.</summary>
        TestHost CreateOtherHost()
        {
            WorldConfiguration other = Sessionable(777);
            other.Clouds.weatherSeed = 9;
            other.Terrain.seaLevel = -0.004f;
            return CreateHost(other);
        }

        static WorldConfiguration Sessionable(int seed = 536452)
        {
            // Natural Planet on (climate and biomes computed), rivers off, low resolutions: fast and complete.
            WorldConfiguration c = WorldArchiveTestUtil.Tweaked(seed, flipFlags: false);
            c.Terrain.resolution = 16;
            return c;
        }

        static float[] TerrainSamples(PlanetInstance planet, int count = 2048)
        {
            var samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / count;
                float r = Mathf.Sqrt(1f - y * y);
                float phi = i * 2.39996323f;
                samples[i] = planet.GetElevation(new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi)));
            }
            return samples;
        }

        // ------------------------------------------------------------ C, F

        // C (session)
        [Test]
        public void C_Session_KeepsItsIdentityAcrossSaves_AndNewWorldsGetNewIds()
        {
            TestHost host = CreateHost(Sessionable());
            WorldSession session = host.Session;
            Assert.IsFalse(session.HasArchive);
            Assert.IsTrue(session.Save("First").Success);
            Guid id = session.PlanetId;
            host.Visual.Clouds.coverage = 0.1f;
            Assert.IsTrue(session.Save().Success);
            Assert.AreEqual(id, session.PlanetId, "Saving again keeps the identity");
            Assert.IsTrue(session.SaveAsNew("Second").Success);
            Assert.AreNotEqual(id, session.PlanetId, "Save As New creates a new identity");
            Assert.AreEqual(2, store.List().Count);
            Assert.AreEqual(1, store.List().Select(e => e.PlanetSeed).Distinct().Count(), "Same seed, two worlds");
        }

        // F
        [Test]
        public void F_EveryRelevantSetting_SurvivesSaveAndLoad()
        {
            WorldConfiguration tweaked = Sessionable();
            TestHost source = CreateHost(tweaked);
            Assert.IsTrue(source.Session.Save("Everything").Success);
            WorldConfiguration saved = source.Session.CaptureConfiguration();
            WorldArchiveTestUtil.AssertSameConfiguration(tweaked, saved, "Capture reads every live settings object");

            TestHost target = CreateOtherHost();
            WorldOperationResult load = target.Session.Load(source.Session.PlanetId);
            Assert.IsTrue(load.Success, load.Message);
            WorldArchiveTestUtil.AssertSameConfiguration(saved, target.Session.CaptureConfiguration(), "Restored configuration");
            Assert.AreEqual(536452, target.Definition.seed);
            Assert.AreEqual(4242, target.Visual.Clouds.weatherSeed);
            Assert.AreEqual(0.7f, target.Visual.Solar.twilightWarmth);
            Assert.AreEqual("Everything", target.Session.DisplayName);
            Assert.AreEqual(source.Session.PlanetId, target.Session.PlanetId);
            Assert.IsFalse(target.Session.IsDirty);
            Assert.IsEmpty(load.Warnings);
        }

        // ------------------------------------------------------------ G (live planet unchanged)

        // G (session)
        [Test]
        public void G_ARejectedArchive_LeavesTheCurrentPlanetUntouched()
        {
            TestHost host = CreateHost(Sessionable());
            Assert.IsTrue(host.Session.Save("Current").Success);
            Guid bad = Guid.NewGuid();
            File.WriteAllText(store.PathFor(bad), "{ \"archiveSchemaVersion\": 1, \"planetSeed\": ");
            WorldConfiguration before = host.Session.CaptureConfiguration();
            PlanetInstance planet = host.Current;
            int regenerations = host.Regenerations;
            WorldOperationResult result = host.Session.Load(bad);
            Assert.IsFalse(result.Success);
            StringAssert.Contains("Corrupt archive", result.Message);
            Assert.AreEqual(regenerations, host.Regenerations, "Nothing regenerated");
            Assert.AreSame(planet, host.Current);
            WorldArchiveTestUtil.AssertSameConfiguration(before, host.Session.CaptureConfiguration(), "Nothing applied");
            Assert.AreEqual("Current", host.Session.DisplayName, "Identity unchanged");
        }

        [Test]
        public void G_FailedRegeneration_RestoresThePreviousConfiguration()
        {
            TestHost source = CreateHost(Sessionable());
            Assert.IsTrue(source.Session.Save("Target").Success);
            TestHost host = CreateOtherHost();
            WorldConfiguration before = host.Session.CaptureConfiguration();
            host.FailNextRegeneration = true;
            // The failure is logged (Console) before the rollback: expected here.
            LogAssert.Expect(LogType.Exception, new Regex("Simulated generation failure"));
            WorldOperationResult result = host.Session.Load(source.Session.PlanetId);
            Assert.IsFalse(result.Success);
            StringAssert.Contains("previous configuration was restored", result.Message);
            WorldArchiveTestUtil.AssertSameConfiguration(before, host.Session.CaptureConfiguration(), "Rolled back");
            Assert.AreEqual(777, host.Current.Definition.seed, "The previous planet was regenerated");
            Assert.IsFalse(host.Session.HasArchive, "Identity unchanged");
        }

        // ------------------------------------------------------------ N (current world)

        // N (session)
        [Test]
        public void N_DeletingTheCurrentWorld_KeepsThePlanetAsAnUnsavedWorld()
        {
            TestHost host = CreateHost(Sessionable());
            Assert.IsTrue(host.Session.Save("Doomed").Success);
            Assert.IsTrue(host.Session.SaveAsNew("Survivor").Success);
            Guid survivor = host.Session.PlanetId;
            Guid doomed = store.List().Single(e => e.DisplayName == "Doomed").PlanetId;
            Assert.IsTrue(host.Session.Delete(doomed).Success);
            Assert.IsTrue(host.Session.HasArchive, "Deleting another world does not affect the current one");
            Assert.IsTrue(host.Session.Delete(survivor).Success);
            Assert.IsFalse(host.Session.HasArchive);
            Assert.IsTrue(host.Session.IsDirty, "Only in memory now");
            Assert.AreEqual(0, store.List().Count);
            Assert.IsTrue(host.Session.Save().Success);
            Assert.AreNotEqual(survivor, host.Session.PlanetId, "Saved again under a new identity");
        }

        // ------------------------------------------------------------ O, P, Q

        // O
        [Test]
        public void O_UnsavedChanges_AreDetected_AndClearedBySaving()
        {
            TestHost host = CreateHost(Sessionable());
            WorldSession session = host.Session;
            Assert.IsFalse(session.RefreshDirty(), "A fresh world equals its startup baseline");
            float sea = host.Definition.seaLevel;
            host.Definition.seaLevel = sea + 0.001f;
            Assert.IsTrue(session.RefreshDirty(), "Terrain change");
            CollectionAssert.AreEqual(new[] { "terrain.seaLevel" }, session.UnsavedFields());
            host.Definition.seaLevel = sea;
            Assert.IsFalse(session.RefreshDirty(), "Reverting the change clears the indicator");
            foreach (Action change in new Action[]
            {
                () => host.Visual.Clouds.coverage += 0.05f,
                () => host.Visual.Clouds.weatherSeed = 99,
                () => host.Visual.Climate.axialTilt += 1f,
                () => host.Visual.Rivers.showRivers = true,
                () => host.Visual.OceanAtmosphere.atmosphereStrength += 0.1f,
                () => host.Visual.Solar.twilightWarmth = 0.2f,
                () => host.Visual.Biomes.aridity = 1.5f,
            })
            {
                change();
                Assert.IsTrue(session.RefreshDirty());
                Assert.IsTrue(session.Save().Success);
                Assert.IsFalse(session.IsDirty, "Saved");
                Assert.IsFalse(session.RefreshDirty());
            }

            // Throttled tracking: nothing is compared until something touches the configuration.
            int checks = session.DirtyCheckCount;
            for (int frame = 0; frame < 100; frame++)
            {
                session.Tick(frame * 0.016f + 100f);
            }
            Assert.AreEqual(checks, session.DirtyCheckCount, "No comparison per frame while untouched");
            host.Definition.mountainStrength = 0.06f;
            session.Touch(200f);
            session.Tick(200.01f);
            Assert.IsTrue(session.IsDirty, "A touch re-evaluates the state");
            int touched = session.DirtyCheckCount;
            for (int frame = 0; frame < 200; frame++)
            {
                session.Tick(200.02f + frame * 0.016f);
            }
            Assert.LessOrEqual(session.DirtyCheckCount - touched, Mathf.CeilToInt(WorldSession.TouchWindow / WorldSession.CheckInterval) + 1,
                "Throttled inside the touch window, none after it");
        }

        // P
        [Test]
        public void P_CloudAnimation_NeverMarksTheWorldModified()
        {
            WorldConfiguration c = Sessionable();
            c.Clouds.motion = true;
            TestHost host = CreateHost(c);
            Assert.IsTrue(host.Session.Save("Clouds").Success);
            for (int i = 0; i < 50; i++)
            {
                host.Visual.CloudSimulationTime += 0.37;
                host.Visual.ApplyCloudTime();
            }
            Assert.IsFalse(host.Session.RefreshDirty());
            Assert.Greater(host.Visual.CloudSimulationTime, 10.0);
        }

        // Q
        [Test]
        public void Q_SolarProgression_Camera_AndDebugViews_NeverMarkTheWorldModified()
        {
            TestHost host = CreateHost(Sessionable());
            Assert.IsTrue(host.Session.Save("Presentation").Success);
            host.Visual.SetCycleDuration(60f);
            host.Visual.SetDayNightCycle(true);
            for (int i = 0; i < 30; i++)
            {
                host.Now += 1.7;
                host.Visual.TickSolar();
            }
            Assert.AreNotEqual(SolarSettings.DefaultSolarTime, host.Visual.Solar.solarTime, "The clock progressed");
            host.Visual.SetSolarTime(0.1f);
            host.Visual.SetSolarPaused(true);
            host.Camera.SetView(120f, -40f, host.Camera.TargetDistance * 1.3f);
            host.Visual.DebugView = PlanetDebugView.ClimateTemperature;
            host.Visual.RefreshColors();
            host.Visual.DebugView = PlanetDebugView.None;
            host.Visual.ClimateWindDirectionColors = true;
            Assert.IsFalse(host.Session.RefreshDirty(), "Presentation is transient: " + string.Join(", ", host.Session.UnsavedFields()));
        }

        // ------------------------------------------------------------ R, S, T, U

        // R
        [Test]
        public void R_ListingRenamingDuplicatingDeleting_NeverRegenerate()
        {
            TestHost host = CreateHost(Sessionable());
            for (int i = 0; i < 6; i++)
            {
                Assert.IsTrue(host.Session.SaveAsNew("World " + i).Success);
            }
            int regenerations = host.Regenerations;
            PlanetInstance planet = host.Current;
            int uploads = host.Visual.CloudUploadCount;
            List<WorldArchiveEntry> entries = store.List();
            Assert.AreEqual(6, entries.Count);
            Assert.IsTrue(host.Session.Rename(entries[0].PlanetId, "Renamed").Success);
            Assert.IsTrue(host.Session.Duplicate(entries[1].PlanetId).Success);
            Assert.IsTrue(host.Session.Delete(entries[2].PlanetId).Success);
            store.List();
            Assert.AreEqual(regenerations, host.Regenerations);
            Assert.AreSame(planet, host.Current);
            Assert.AreSame(planet, host.Visual.Planet);
            Assert.AreEqual(uploads, host.Visual.CloudUploadCount);
        }

        // S
        [Test]
        public void S_Saving_DoesNotRegenerate_NorResetCloudsOrTheSolarClock()
        {
            WorldConfiguration c = Sessionable();
            c.Clouds.motion = true;
            TestHost host = CreateHost(c);
            host.Visual.SetCycleDuration(120f);
            host.Visual.SetDayNightCycle(true);
            host.Now += 17.0;
            host.Visual.TickSolar();
            host.Visual.CloudSimulationTime = 12.5;
            host.Visual.ApplyCloudTime();
            float solar = host.Visual.Solar.solarTime;
            int regenerations = host.Regenerations;
            PlanetInstance planet = host.Current;
            Mesh[] meshes = host.Root.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh).ToArray();
            int[] meshVersions = meshes.Select(m => m.vertexCount).ToArray();
            WorldConfiguration before = host.Session.CaptureConfiguration();

            WorldOperationResult result = host.Session.Save("Running");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(regenerations, host.Regenerations, "No regeneration");
            Assert.AreSame(planet, host.Visual.Planet);
            Assert.AreEqual(12.5, host.Visual.CloudSimulationTime, "Cloud motion continues from where it was");
            Assert.IsTrue(host.Visual.SolarClock.Running, "The day / night cycle keeps running");
            Assert.AreEqual(solar, host.Visual.Solar.solarTime);
            host.Now += 3.0;
            host.Visual.TickSolar();
            Assert.Greater(host.Visual.Solar.solarTime, solar, "...and keeps progressing");
            CollectionAssert.AreEqual(meshVersions, host.Root.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh.vertexCount).ToArray());
            WorldArchiveTestUtil.AssertSameConfiguration(before, host.Session.CaptureConfiguration(), "No setting changed by saving");
            PlanetArchive saved = store.Load(host.Session.PlanetId).Archive;
            Assert.AreEqual(solar, saved.Presentation.Solar.solarTime, "The current solar time is captured");
            Assert.IsTrue(saved.Presentation.Solar.dayNightCycle);
        }

        // T, U
        [Test]
        public void TU_Loading_AppliesSettingsInOrder_ThenRegeneratesExactlyOnce_ThenRestoresPresentation()
        {
            TestHost source = CreateHost(Sessionable());
            Assert.IsTrue(source.Session.Save("Ordered").Success);
            TestHost host = CreateOtherHost();
            int before = host.Regenerations;
            WorldOperationResult result = host.Session.Load(source.Session.PlanetId);
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(1, host.Regenerations - before, "U: exactly one regeneration");
            Assert.AreEqual(1, result.Regenerations);
            CollectionAssert.AreEqual(new[]
            {
                "terrain", "hydrology", "rivers", "lakes", "climate", "biomes", "surface", "oceanAtmosphere", "clouds", "lighting", "presentation",
            }, host.Session.LastApplyOrder, "T: controlled order");
            Assert.AreEqual(10, host.AppliedGroupsAtRegeneration, "T: regenerated after every settings group, before the presentation");
        }

        // U (real application object)
        [Test]
        public void U_Bootstrap_LoadTriggersASingleRegeneration()
        {
            var before = new HashSet<GameObject>(Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None));
            var bootstrapObject = new GameObject("Archive Bootstrap Test");
            var bootstrap = bootstrapObject.AddComponent<PlanetBootstrap>();
            bootstrap.EnsureSceneObjects();
            foreach (GameObject o in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                if (!before.Contains(o) && o.transform.parent == null)
                {
                    created.Add(o);
                }
            }
            bootstrap.UseWorldStore(store);
            bootstrap.Definition.resolution = 16;
            bootstrap.Visual.Climate.resolution = ClimateSettings.LowResolution;
            bootstrap.Regenerate();
            WorldSession worlds = bootstrap.Worlds;
            Assert.AreSame(store, worlds.Store);
            worlds.ResetBaseline();
            bootstrap.Definition.seed = 609538;
            bootstrap.Visual.Clouds.weatherSeed = 31337;
            Assert.IsTrue(worlds.RefreshDirty());
            bootstrap.OrbitCamera.SetView(-75f, 33f, bootstrap.OrbitCamera.TargetDistance * 1.2f);
            Assert.IsTrue(worlds.Save("Bootstrap World").Success);
            CameraPresentation view = ((IWorldHost)bootstrap).CaptureCamera();

            bootstrap.Definition.seed = 1;
            bootstrap.Visual.Clouds.weatherSeed = 0;
            bootstrap.Regenerate();
            bootstrap.OrbitCamera.SetView(10f, 0f, bootstrap.OrbitCamera.TargetDistance);
            int count = bootstrap.GenerationCount;
            WorldOperationResult result = worlds.Load(worlds.PlanetId);
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(count + 1, bootstrap.GenerationCount, "One regeneration through the real bootstrap");
            Assert.AreEqual(609538, bootstrap.Current.Definition.seed);
            Assert.AreEqual(31337, bootstrap.Visual.Clouds.weatherSeed);
            Assert.AreEqual(view.yaw, bootstrap.OrbitCamera.TargetYaw, 1e-3f);
            Assert.AreEqual(view.pitch, bootstrap.OrbitCamera.TargetPitch, 1e-3f);
            Assert.AreEqual(view.distance, bootstrap.OrbitCamera.TargetDistance, 1e-3f);
            Assert.IsFalse(worlds.IsDirty);
        }

        // ------------------------------------------------------------ V, W, X, Y

        // V, W, X
        [TestCase(1337)]
        [TestCase(536452)]
        [TestCase(868497)]
        [TestCase(609538)]
        public void VWX_ReferenceSeeds_ReconstructTerrainClimateBiomesAndCloudsExactly(int seed)
        {
            TestHost source = CreateHost(Sessionable(seed));
            Assert.IsTrue(source.Session.Save("Reference " + seed).Success);
            Assert.IsNotNull(source.Visual.CurrentClimate, "Natural Planet computed the climate");
            Assert.IsNotNull(source.Visual.CurrentBiomes);
            Assert.IsNotNull(source.Visual.CurrentCloudField);

            TestHost target = CreateOtherHost();
            Assert.IsTrue(target.Session.Load(source.Session.PlanetId).Success);

            // V: terrain (sampled elevation field, statistics, rendered vertices). Same generator: exact (tolerance 0).
            CollectionAssert.AreEqual(TerrainSamples(source.Current), TerrainSamples(target.Current), "V: elevation samples");
            Assert.AreEqual(source.Current.LandFraction, target.Current.LandFraction);
            Assert.AreEqual(source.Current.MaxElevation, target.Current.MaxElevation);
            MeshFilter[] a = source.Root.GetComponentsInChildren<MeshFilter>();
            MeshFilter[] b = target.Root.GetComponentsInChildren<MeshFilter>();
            for (int i = 0; i < a.Length; i++)
            {
                CollectionAssert.AreEqual(a[i].sharedMesh.vertices, b[i].sharedMesh.vertices, "V: mesh " + a[i].name);
                CollectionAssert.AreEqual(a[i].sharedMesh.colors32, b[i].sharedMesh.colors32, "W: Natural Planet colors " + a[i].name);
            }

            // W: climate and biome fields.
            ClimateResult ca = source.Visual.CurrentClimate, cb = target.Visual.CurrentClimate;
            Assert.IsNotNull(cb);
            CollectionAssert.AreEqual(ca.Temperature, cb.Temperature, "W: temperature");
            CollectionAssert.AreEqual(ca.Moisture, cb.Moisture, "W: moisture");
            CollectionAssert.AreEqual(ca.Precipitation, cb.Precipitation, "W: precipitation");
            CollectionAssert.AreEqual(source.Visual.CurrentBiomes.Dominant, target.Visual.CurrentBiomes.Dominant, "W: dominant biomes");
            CollectionAssert.AreEqual(source.Visual.CurrentBiomes.Scores, target.Visual.CurrentBiomes.Scores, "W: suitabilities");

            // X: weather seed and the cloud field.
            Assert.AreEqual(4242, target.Visual.Clouds.weatherSeed);
            Assert.IsTrue(SettingsFields.AreEqual(source.Visual.Clouds, target.Visual.Clouds));
            Assert.AreEqual(4242, target.Visual.CurrentCloudField.Seed);
            CollectionAssert.AreEqual(source.Visual.CurrentCloudField.Base, target.Visual.CurrentCloudField.Base, "X: cloud field");
            CollectionAssert.AreEqual(source.Visual.CurrentCloudField.Systems, target.Visual.CurrentCloudField.Systems, "X: weather systems");
            Assert.AreEqual(source.Visual.CloudThreshold, target.Visual.CloudThreshold, "X: coverage threshold");
        }

        // X (planet-derived sky)
        [Test]
        public void X_WeatherSeedZero_StillMeansDerivedFromThePlanetSeed()
        {
            WorldConfiguration c = Sessionable(868497);
            c.Clouds.weatherSeed = 0;
            TestHost source = CreateHost(c);
            Assert.IsTrue(source.Session.Save("Planet sky").Success);
            TestHost target = CreateOtherHost();
            Assert.IsTrue(target.Session.Load(source.Session.PlanetId).Success);
            Assert.AreEqual(0, target.Visual.Clouds.weatherSeed);
            Assert.AreEqual(868497, target.Visual.CurrentCloudField.Seed);
        }

        // Y
        [Test]
        public void Y_CameraAndSolarPresentation_AreRestored()
        {
            TestHost source = CreateHost(Sessionable());
            source.Camera.SetView(-137.5f, 41f, source.Camera.TargetDistance * 1.25f);
            source.Visual.SetCycleDuration(600f);
            source.Visual.SetSolarTime(0.8125f);
            source.Visual.SetSolarPaused(true);
            source.Visual.SetDayNightCycle(true);
            Assert.IsTrue(source.Session.Save("View").Success);
            CameraPresentation view = source.CaptureCamera();

            TestHost target = CreateOtherHost();
            target.Visual.SetSolarTime(0.2f);
            Assert.IsTrue(target.Session.Load(source.Session.PlanetId).Success);
            Assert.AreEqual(view.yaw, target.Camera.TargetYaw, 1e-4f);
            Assert.AreEqual(view.pitch, target.Camera.TargetPitch, 1e-4f);
            Assert.AreEqual(view.distance, target.Camera.TargetDistance, 1e-3f);
            Assert.AreEqual(view.distance, target.Camera.CurrentDistance, 1e-3f, "Jumps there (no slow fly-in)");
            Assert.AreEqual(0.8125f, target.Visual.Solar.solarTime);
            Assert.AreEqual(600f, target.Visual.Solar.cycleDuration);
            Assert.IsTrue(target.Visual.Solar.dayNightCycle);
            Assert.IsTrue(target.Visual.Solar.paused);
            Assert.IsFalse(target.Visual.SolarClock.Running, "Paused: the clock waits");
            Assert.Less(Vector3.Angle(source.Visual.SunDirection, target.Visual.SunDirection), 0.01f, "Same sun direction");
            target.Visual.SetSolarPaused(false);
            target.Now += 6.0;
            target.Visual.TickSolar();
            Assert.AreEqual(0.8125f + 6f / 600f, target.Visual.Solar.solarTime, 1e-5f, "Resumes from the restored time");

            // An archive without presentation keeps the current view and clock.
            PlanetArchive archive = store.Load(source.Session.PlanetId).Archive;
            archive.Presentation = new PresentationState();
            archive.PlanetId = Guid.NewGuid();
            Assert.IsTrue(store.Save(archive).Success);
            target.Camera.SetView(5f, 6f, target.Camera.TargetDistance);
            target.Visual.SetSolarTime(0.3f);
            Assert.IsTrue(target.Session.Load(archive.PlanetId).Success);
            Assert.AreEqual(5f, target.Camera.TargetYaw, 1e-4f);
            Assert.AreEqual(0.3f, target.Visual.Solar.solarTime, 1e-6f);
        }

        // ------------------------------------------------------------ Z

        // Z
        [Test]
        public void Z_RepeatedSaveLoadCycles_DoNotLeak()
        {
            TestHost source = CreateHost(Sessionable());
            Assert.IsTrue(source.Session.Save("A").Success);
            Guid a = source.Session.PlanetId;
            source.Visual.Clouds.weatherSeed = 77;
            source.Definition.seed = 1337;
            source.Regenerate();
            Assert.IsTrue(source.Session.SaveAsNew("B").Success);
            Guid b = source.Session.PlanetId;

            // Warm up (first load of each creates the per-planet caches), then count.
            Assert.IsTrue(source.Session.Load(a).Success);
            Assert.IsTrue(source.Session.Load(b).Success);
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            int textures = Resources.FindObjectsOfTypeAll<Texture>().Length;
            int objects = Resources.FindObjectsOfTypeAll<GameObject>().Length;
            for (int i = 0; i < 6; i++)
            {
                Assert.IsTrue(source.Session.Load(i % 2 == 0 ? a : b).Success);
                Assert.IsTrue(source.Session.Save().Success);
            }
            Assert.AreEqual(meshes, Resources.FindObjectsOfTypeAll<Mesh>().Length, "Meshes");
            Assert.AreEqual(materials, Resources.FindObjectsOfTypeAll<Material>().Length, "Materials");
            Assert.AreEqual(textures, Resources.FindObjectsOfTypeAll<Texture>().Length, "Textures");
            Assert.AreEqual(objects, Resources.FindObjectsOfTypeAll<GameObject>().Length, "GameObjects");
            Assert.AreEqual(2, store.List().Count);
            Assert.AreEqual(0, Directory.GetFiles(directory, "*" + WorldArchiveStore.TempExtension).Length);
        }

        // ------------------------------------------------------------ Performance report

        [Test]
        public void Report_SaveListLoadPerformance_ReferenceSeedsAtDefaultSettings()
        {
            var lines = new List<string> { "seed | save ms | file bytes | load total ms | read ms | regeneration ms | regenerations" };
            var ids = new List<Guid>();
            foreach (int seed in WorldArchiveTestUtil.ReferenceSeeds)
            {
                // Default panel settings (render resolution 64, climate 64, Natural Planet, clouds).
                var host = new TestHost(created);
                host.Definition.resolution = 64;
                host.Definition.seed = seed;
                host.Visual.Climate.resolution = ClimateSettings.DefaultResolution;
                host.Regenerate();
                host.Session = new WorldSession(host, store);
                host.Session.ResetBaseline();
                WorldOperationResult save = host.Session.Save("Reference " + seed);
                Assert.IsTrue(save.Success);
                ids.Add(host.Session.PlanetId);

                var other = new TestHost(created);
                other.Definition.resolution = 64;
                other.Definition.seed = seed + 1;
                other.Visual.Climate.resolution = ClimateSettings.DefaultResolution;
                other.Regenerate();
                other.Session = new WorldSession(other, store);
                int before = other.Regenerations;
                WorldOperationResult load = other.Session.Load(host.Session.PlanetId);
                Assert.IsTrue(load.Success, load.Message);
                Assert.AreEqual(1, other.Regenerations - before);
                lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} | {1:0.0} | {2} | {3:0} | {4:0.0} | {5:0} | {6}",
                    seed, save.Milliseconds, save.Bytes, load.Milliseconds, load.ReadMilliseconds, load.RegenerationMilliseconds,
                    other.Regenerations - before));
                Assert.Less(save.Milliseconds, 200.0, "Saving is lightweight");
                Assert.Less(save.Bytes, 16 * 1024);
            }
            for (int i = 0; i < 16; i++)
            {
                Assert.IsTrue(store.Duplicate(ids[i % ids.Count]).Success);
            }
            var stopwatch = Stopwatch.StartNew();
            List<WorldArchiveEntry> entries = store.List();
            stopwatch.Stop();
            Assert.AreEqual(20, entries.Count);
            lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "listing 20 archives: {0:0.0} ms", stopwatch.Elapsed.TotalMilliseconds));
            Assert.Less(stopwatch.Elapsed.TotalMilliseconds, 1000.0);
            WorldArchiveTestUtil.Report(string.Join("\n", lines));
        }
    }
}
