using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Planetwalker.Bootstrap;
using Planetwalker.Core;
using Planetwalker.Library;
using Planetwalker.Persistence;
using Planetwalker.Rendering;
using Planetwalker.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// The adaptive terrain renderer in a live application (V0.2.0B): natural appearance equal to the legacy faces,
    /// Legacy / Adaptive switching without regeneration or unsaved changes, bounded and released resources,
    /// regeneration and world loads while patches are building, coexistence with ocean, atmosphere, clouds,
    /// rivers and lakes, thumbnails, and an erratic camera. Needs the Unity engine.
    /// </summary>
    public class AdaptiveTerrainRendererTests
    {
        readonly List<Object> created = new List<Object>();
        string directory;
        WorldArchiveStore store;

        [SetUp]
        public void SetUp()
        {
            directory = WorldArchiveTestUtil.CreateTempDirectory();
            store = new WorldArchiveStore(Path.Combine(directory, "Worlds"));
            Directory.CreateDirectory(store.Directory);
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

        WorldTestHost CreateHost(int seed = 1337, bool riversAndLakes = false)
        {
            var host = new WorldTestHost(created);
            WorldConfiguration c = WorldArchiveTestUtil.Tweaked(seed, flipFlags: false);
            c.Terrain.resolution = 16;
            c.Rivers.showRivers = riversAndLakes;
            c.Lakes.showLakes = riversAndLakes;
            host.Apply(c);
            host.Regenerate();
            host.Session = new WorldSession(host, store);
            host.Session.ResetBaseline();
            return host;
        }

        static AdaptiveTerrainRenderer EnableAdaptive(WorldTestHost host, bool background = false, int budget = 96)
        {
            AdaptiveTerrainRenderer terrain = AdaptiveTerrainRenderer.GetOrCreate(host.Visual);
            terrain.Settings.maxLeafPatches = budget;
            terrain.BackgroundConstruction = background;
            terrain.ConstructionMilliseconds = 10000f; // synchronous tests: build what is requested in one tick
            terrain.MaxUploadsPerFrame = 256;
            terrain.MaxPendingBuilds = 128;
            terrain.enabled = true;
            return terrain;
        }

        static TerrainLodView View(PlanetInstance planet, float altitudeRadii, Vector3 direction)
        {
            float distance = planet.Radius * (1f + Mathf.Max(planet.MaxElevation, 0f)) + planet.Radius * altitudeRadii;
            return new TerrainLodView(direction.normalized * distance, 60f, 1080);
        }

        /// <summary>Ticks until the adaptive covering is displayed and caught up (background builds: real time passes).</summary>
        static int RunUntilSettled(AdaptiveTerrainRenderer terrain, in TerrainLodView view, int maxTicks = 2000)
        {
            for (int tick = 1; tick <= maxTicks; tick++)
            {
                terrain.Tick(view);
                if (terrain.IsDisplaying && terrain.Covering.Converged && terrain.Scheduler.Pending == 0)
                {
                    return tick;
                }
                if (terrain.BackgroundConstruction)
                {
                    Thread.Sleep(2);
                }
            }
            Assert.Fail("The adaptive terrain did not settle");
            return maxTicks;
        }

        static int AdaptiveMeshObjects() => Resources.FindObjectsOfTypeAll<Mesh>().Count(m => m.name.StartsWith("Adaptive Terrain"));

        static Mesh LegacyFace(WorldTestHost host, int face) =>
            host.Visual.GetComponentsInChildren<MeshFilter>(true).First(f => f.name == "Terrain " + Planetwalker.Generation.CubeSphereGenerator.GetFaceName(face)).sharedMesh;

        // J, Y
        [Test]
        public void J_AdaptivePatches_ReproduceTheLegacyNaturalSurface()
        {
            WorldTestHost host = CreateHost(1337);
            Assert.IsTrue(host.Visual.NaturalPlanetShown);
            AdaptiveTerrainRenderer terrain = EnableAdaptive(host);
            RunUntilSettled(terrain, View(host.Current, 1f, Vector3.up));
            AdaptiveSurfaceSnapshot snapshot = terrain.Snapshot;
            Assert.AreSame(host.Visual.CurrentNaturalSurface, snapshot.Natural, "The authoritative Natural Planet data");
            // Level-0 patches of 16 quads lie exactly on the legacy face lattice (resolution 16).
            var builder = new AdaptivePatchGeometryBuilder();
            var data = new PatchGeometryData(snapshot.Topology.VertexCount);
            float worstColor = 0f, worstPosition = 0f;
            for (int face = 0; face < 6; face++)
            {
                Mesh legacy = LegacyFace(host, face);
                Vector3[] vertices = legacy.vertices;
                Color[] colors = legacy.colors;
                builder.Build(snapshot, PlanetPatchId.Root(face), data);
                for (int v = 0; v < snapshot.Topology.GridVertexCount; v++)
                {
                    worstPosition = Mathf.Max(worstPosition, ((data.Origin + data.Vertices[v]) - vertices[v]).magnitude);
                    Color d = data.Colors[v] - colors[v];
                    worstColor = Mathf.Max(worstColor, Mathf.Max(Mathf.Abs(d.r), Mathf.Max(Mathf.Abs(d.g), Mathf.Abs(d.b))));
                }
            }
            Assert.Less(worstPosition, host.Current.Radius * 2e-6f, "Same terrain points as the legacy faces");
            Assert.Less(worstColor, 2e-3f, "Same natural colors as the legacy faces (managed vs engine sRGB curve)");
            WorldArchiveTestUtil.Report(string.Format(CultureInfo.InvariantCulture,
                "Adaptive vs legacy on the shared lattice (seed 1337): worst position {0:0.0e0} units, worst color {1:0.0e0}", worstPosition, worstColor));
        }

        // S, T, Y
        [Test]
        public void STY_SwitchingRenderers_KeepsTheWorldUntouched()
        {
            WorldTestHost host = CreateHost(536452);
            WorldConfiguration before = host.Session.CaptureConfiguration();
            int regenerations = host.Regenerations;
            PlanetInstance planet = host.Current;
            Vector3[] legacyBefore = LegacyFace(host, 2).vertices;
            Color[] legacyColors = LegacyFace(host, 2).colors;
            TerrainLodView view = View(host.Current, 0.3f, new Vector3(-0.5f, 0.2f, 0.8f));
            for (int round = 0; round < 3; round++)
            {
                AdaptiveTerrainRenderer terrain = EnableAdaptive(host);
                Assert.IsTrue(host.Visual.TerrainFacesVisible, "Legacy stays on screen while the adaptive covering is prepared");
                RunUntilSettled(terrain, view);
                Assert.IsFalse(host.Visual.TerrainFacesVisible, "One authoritative surface: the faces are hidden while adaptive is shown");
                terrain.enabled = false;
                Assert.IsTrue(host.Visual.TerrainFacesVisible, "Back to legacy");
            }
            Assert.AreEqual(regenerations, host.Regenerations, "S: no regeneration");
            Assert.AreSame(planet, host.Current);
            WorldArchiveTestUtil.AssertSameConfiguration(before, host.Session.CaptureConfiguration(), "S: persistent settings untouched");
            Assert.IsFalse(host.Session.RefreshDirty(), "T: switching is not a modification of the world");
            CollectionAssert.AreEqual(legacyBefore, LegacyFace(host, 2).vertices, "Y: legacy geometry unchanged");
            CollectionAssert.AreEqual(legacyColors, LegacyFace(host, 2).colors, "Y: legacy colors unchanged");
        }

        // P, Q
        [Test]
        public void PQ_ResidentMeshesAreBounded_AndRepeatedSwitchingLeaksNothing()
        {
            WorldTestHost host = CreateHost(868497);
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            var random = new System.Random(3);
            for (int round = 0; round < 6; round++)
            {
                AdaptiveTerrainRenderer terrain = EnableAdaptive(host, background: round % 2 == 1, budget: 64);
                terrain.Settings.maxPatchMeshes = 100;
                for (int step = 0; step < 12; step++)
                {
                    var direction = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
                    terrain.Tick(View(host.Current, step % 2 == 0 ? 0.01f : 2f, direction));
                    int capacity = Math.Max(100, 2 * 64 + 64);
                    Assert.LessOrEqual(terrain.MeshSlots, capacity, "P: bounded resident meshes");
                    Assert.LessOrEqual(terrain.Meshes.CreatedCount, capacity, "P: slots are recycled, not reallocated");
                    Assert.LessOrEqual(terrain.Scheduler.Pending, terrain.MaxPendingBuilds, "Bounded pending work");
                    Assert.LessOrEqual(AdaptiveMeshObjects(), capacity);
                }
                terrain.enabled = false;
                Assert.AreEqual(meshes, Resources.FindObjectsOfTypeAll<Mesh>().Length, "Q: round " + round + ", every mesh released");
                Assert.AreEqual(materials, Resources.FindObjectsOfTypeAll<Material>().Length, "Q: no material instance created");
                Assert.IsTrue(host.Visual.TerrainFacesVisible);
            }
        }

        // R
        [Test]
        public void R_DestroyingThePlanetWhileBuilding_ReleasesEverything()
        {
            WorldTestHost host = CreateHost(609538);
            AdaptiveTerrainRenderer terrain = EnableAdaptive(host, background: true, budget: 256);
            for (int i = 0; i < 6; i++)
            {
                terrain.Tick(View(host.Current, 0.01f, Vector3.right));
            }
            Assert.Greater(terrain.Scheduler.Pending + terrain.ResidentPatches, 0, "Work in progress");
            PatchBuildScheduler scheduler = terrain.Scheduler;
            Object.DestroyImmediate(host.Root);
            Assert.AreEqual(0, AdaptiveMeshObjects(), "No adaptive mesh survives its planet");
            Thread.Sleep(200); // running worker builds finish into the void
            Assert.AreEqual(0, AdaptiveMeshObjects(), "Late worker results never resurrect a mesh");
            Assert.AreEqual(0, scheduler.ReadyCount);
        }

        // M, N
        [Test]
        public void MN_RegenerationAndWorldLoadsWhileBuilding_NeverShowOldPatchesOrAnEmptyPlanet()
        {
            WorldTestHost host = CreateHost(1337);
            Assert.IsTrue(host.Session.Save("World A").Success);
            Guid worldA = host.Session.PlanetId;
            AdaptiveTerrainRenderer terrain = EnableAdaptive(host, background: true, budget: 192);
            terrain.MaxUploadsPerFrame = 8; // slow uploads: the world changes mid-construction
            var direction = new Vector3(0.1f, 0.4f, 0.9f);
            int seedChanges = 0;
            for (int tick = 0; tick < 1500; tick++)
            {
                if (tick == 10 || tick == 30)
                {
                    host.Definition.seed = 4000 + tick; // regenerate while patches are building
                    host.Regenerate();
                    seedChanges++;
                }
                if (tick == 50)
                {
                    Assert.IsTrue(host.Session.Load(worldA).Success); // load a world while patches are building
                    seedChanges++;
                }
                terrain.Tick(View(host.Current, 0.05f, direction));
                Assert.IsTrue(host.Visual.TerrainFacesVisible || terrain.IsDisplaying, "Tick " + tick + ": never an empty planet");
                Assert.IsFalse(host.Visual.TerrainFacesVisible && terrain.IsDisplaying, "Tick " + tick + ": never two terrains");
                if (terrain.IsDisplaying)
                {
                    Assert.AreSame(host.Current, terrain.SampledPlanet, "Tick " + tick + ": the displayed patches belong to the current planet");
                    foreach (PlanetPatchId leaf in terrain.Displayed.Leaves)
                    {
                        Assert.IsTrue(terrain.Meshes.TryGet(leaf, out PatchMeshCache.Entry entry) && entry.Generation == terrain.Generation,
                            "Tick " + tick + ": obsolete patch displayed");
                    }
                    Assert.AreEqual(0, terrain.InvalidEdges);
                }
                if (tick > 60 && terrain.IsDisplaying && terrain.Covering.Converged && terrain.Scheduler.Pending == 0)
                {
                    break;
                }
                Thread.Sleep(1);
            }
            Assert.AreEqual(3, seedChanges);
            Assert.AreEqual(3, terrain.Invalidations, "Two regenerations and one load");
            Assert.IsTrue(terrain.IsDisplaying, "Settled on the loaded world");
            Assert.AreEqual(1337, terrain.SampledPlanet.Definition.seed);
            Assert.Greater(terrain.Scheduler.Cancelled + terrain.Scheduler.Discarded, 0, "Obsolete work was cancelled or discarded");
            // The displayed geometry is the loaded planet's.
            PlanetPatchId sample = terrain.Displayed.Leaves[terrain.Displayed.Count / 2];
            terrain.Meshes.TryGet(sample, out PatchMeshCache.Entry e);
            Vector3 corner = e.Geometry.Origin + e.Mesh.vertices[0];
            Assert.Less((corner - terrain.Snapshot.Sampler.SurfacePoint(CubeSphereCoordinates.CornerDirection(sample, 0))).magnitude, 1e-4f);
        }

        // U, V
        [Test]
        public void UV_OceanAtmosphereCloudsRiversAndLakes_AreUnaffectedBySwitching()
        {
            WorldTestHost host = CreateHost(536452, riversAndLakes: true);
            PlanetVisualController visual = host.Visual;
            var legacy = (visual.AtmosphereVisible, visual.OceanShadingActive, visual.CloudsVisible, visual.RiversVisible, visual.LakesVisible,
                visual.OceanRenderer.gameObject.activeInHierarchy, visual.OceanRenderer.enabled);
            int ribbons = visual.RiverRibbonBuildCount, lakeMeshes = visual.LakeMeshBuildCount;
            Mesh riverMesh = visual.RiverMesh, lakeMesh = visual.LakeMesh;
            AdaptiveTerrainRenderer terrain = EnableAdaptive(host);
            RunUntilSettled(terrain, View(host.Current, 0.5f, Vector3.forward));
            Assert.AreEqual(legacy, (visual.AtmosphereVisible, visual.OceanShadingActive, visual.CloudsVisible, visual.RiversVisible, visual.LakesVisible,
                visual.OceanRenderer.gameObject.activeInHierarchy, visual.OceanRenderer.enabled), "U: ocean, atmosphere, clouds, rivers, lakes unchanged");
            Assert.AreEqual(ribbons, visual.RiverRibbonBuildCount, "No river rebuild for a renderer switch");
            Assert.AreEqual(lakeMeshes, visual.LakeMeshBuildCount);
            Assert.AreSame(riverMesh, visual.RiverMesh);
            Assert.AreSame(lakeMesh, visual.LakeMesh);
            Assert.IsTrue(visual.RiversVisible && visual.LakesVisible);

            // V: after a regeneration in adaptive mode, rivers and lakes belong to the new planet like the patches.
            host.Definition.seed = 77;
            host.Regenerate();
            RunUntilSettled(terrain, View(host.Current, 0.5f, Vector3.forward));
            Assert.AreSame(host.Current, terrain.SampledPlanet);
            Assert.AreSame(host.Current, visual.Planet);
            Assert.Greater(visual.RiverRibbonBuildCount, ribbons, "Rivers rebuilt for the new planet");
            Assert.IsTrue(visual.CurrentRivers != null && visual.CurrentLakes != null);
            Assert.AreSame(visual.CurrentHydrology, visual.CurrentRivers.Hydrology, "Rivers of the current hydrology");
        }

        // W
        [Test]
        public void W_ThumbnailsCaptureTheCompleteLegacyPlanet_InAdaptiveMode()
        {
            WorldTestHost host = CreateHost(1337);
            byte[] legacyPng = WorldThumbnailRenderer.CapturePng(host.Visual, 64);
            AdaptiveTerrainRenderer terrain = EnableAdaptive(host);
            RunUntilSettled(terrain, View(host.Current, 0.5f, Vector3.up));
            Assert.IsFalse(host.Visual.TerrainFacesVisible);
            byte[] adaptivePng = WorldThumbnailRenderer.CapturePng(host.Visual, 64);
            Assert.IsFalse(host.Visual.TerrainFacesVisible, "The adaptive display is restored after the capture");
            Assert.IsTrue(terrain.IsDisplaying);
            CollectionAssert.AreEqual(legacyPng, adaptivePng, "Same preview in both modes: never an incomplete adaptive state");
        }

        // X
        [Test]
        public void X_ErraticCamera_KeepsEveryInvariant()
        {
            WorldTestHost host = CreateHost(868497);
            AdaptiveTerrainRenderer terrain = EnableAdaptive(host, background: true, budget: 160);
            terrain.MaxUploadsPerFrame = 12;
            terrain.MaxPendingBuilds = 24;
            var random = new System.Random(9);
            Vector3 direction = Vector3.up;
            float altitude = 1f;
            int displayedTicks = 0;
            var watch = Stopwatch.StartNew();
            for (int tick = 0; tick < 300; tick++)
            {
                if (tick % 5 == 0)
                {
                    direction = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
                }
                altitude = Mathf.Clamp(altitude * (random.NextDouble() < 0.5 ? 0.5f : 1.8f), 0.0005f, 6f);
                terrain.Tick(View(host.Current, altitude, direction));
                Assert.LessOrEqual(terrain.Scheduler.Pending, 24, "Bounded backlog under rapid zoom");
                Assert.AreEqual(0, terrain.InvalidEdges);
                if (terrain.IsDisplaying)
                {
                    displayedTicks++;
                    CoverageReport report = PatchCoverage.Validate(terrain.Displayed.Leaves.ToList());
                    Assert.IsTrue(report.Valid, "Tick " + tick + ": " + report.Problem);
                }
                Thread.Sleep(1);
            }
            Assert.Greater(displayedTicks, 100);
            WorldArchiveTestUtil.Report(string.Format(CultureInfo.InvariantCulture,
                "Erratic camera, 300 ticks ({0:0} ms): {1} builds done, {2} cancelled, {3} discarded, {4} uploads, {5} restitches, {6} splits, {7} merges",
                watch.Elapsed.TotalMilliseconds, terrain.Scheduler.Completed, terrain.Scheduler.Cancelled, terrain.Scheduler.Discarded, terrain.TotalUploads,
                terrain.Restitches, terrain.Covering.TotalSplits, terrain.Covering.TotalMerges));
        }

        // Unity timings (batch EditMode: no rendering, no Play-mode frame pacing).
        [Test]
        public void Performance_UnityTickCosts_ReferenceSeeds()
        {
            var lines = new List<string> { "seed | camera | ticks to settle (sync) | settled tick ms (still) | moving tick ms | select ms | patches shown | levels | mesh MB" };
            foreach (int seed in WorldArchiveTestUtil.ReferenceSeeds)
            {
                WorldTestHost host = CreateHost(seed);
                AdaptiveTerrainRenderer terrain = EnableAdaptive(host, budget: 512);
                terrain.ConstructionMilliseconds = 10000f;
                foreach ((string name, float altitude) in new[] { ("distant 7R", 7f), ("medium 1R", 1f), ("close 0.05R", 0.05f), ("near 0.0005R", 0.0005f) })
                {
                    var direction = new Vector3(0.3f, 0.5f, -0.81f);
                    TerrainLodView view = View(host.Current, altitude, direction);
                    int ticks = RunUntilSettled(terrain, view);
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < 20; i++)
                    {
                        terrain.Tick(view);
                    }
                    double still = watch.Elapsed.TotalMilliseconds / 20;
                    watch.Restart();
                    double select = 0;
                    for (int i = 0; i < 20; i++)
                    {
                        float a = 0.1f * i * Mathf.Deg2Rad;
                        var d = new Vector3(direction.x * Mathf.Cos(a) + direction.z * Mathf.Sin(a), direction.y, -direction.x * Mathf.Sin(a) + direction.z * Mathf.Cos(a));
                        terrain.Tick(View(host.Current, altitude, d));
                        select += terrain.LastSelectionMilliseconds;
                    }
                    double moving = watch.Elapsed.TotalMilliseconds / 20;
                    int min = terrain.Displayed.Leaves.Min(l => l.Level), max = terrain.Displayed.Leaves.Max(l => l.Level);
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} | {1} | {2} | {3:0.000} | {4:0.00} | {5:0.00} | {6} | {7}-{8} | {9:0.0}",
                        seed, name, ticks, still, moving, select / 20, terrain.Displayed.Count, min, max, terrain.EstimatedMeshBytes / (1024.0 * 1024.0)));
                }
                terrain.enabled = false;
            }
            WorldArchiveTestUtil.Report(string.Join("\n", lines));
        }
    }
}
