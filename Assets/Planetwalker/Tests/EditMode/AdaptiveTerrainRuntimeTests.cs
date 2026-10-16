using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Planetwalker.Bootstrap;
using Planetwalker.Core;
using Planetwalker.Library;
using Planetwalker.Persistence;
using Planetwalker.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Adaptive terrain diagnostics in a live application (V0.2.0A): invalidation after regeneration and world
    /// loads (P), native resource cleanup and bounded meshes under repeated toggling (Q), no effect on the world's
    /// persistent settings (R), archive compatibility with the debug view active (S). Needs the Unity engine.
    /// </summary>
    public class AdaptiveTerrainRuntimeTests
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

        WorldTestHost CreateHost(int seed = 536452)
        {
            var host = new WorldTestHost(created);
            WorldConfiguration c = WorldArchiveTestUtil.Tweaked(seed, flipFlags: false);
            c.Terrain.resolution = 16;
            host.Apply(c);
            host.Regenerate();
            host.Session = new WorldSession(host, store);
            host.Session.ResetBaseline();
            return host;
        }

        static AdaptiveTerrainDebugView EnableView(WorldTestHost host, AdaptiveTerrainDebugMode mode, int budget = 64)
        {
            AdaptiveTerrainDebugView view = AdaptiveTerrainDebugView.GetOrCreate(host.Visual);
            view.Settings.maxLeafPatches = budget;
            view.Settings.maxPatchMeshes = budget * 2;
            view.Mode = mode;
            view.PreviewBuildMilliseconds = 10000f; // tests: build the whole covering in one tick
            view.enabled = true;
            return view;
        }

        static TerrainLodView View(PlanetInstance planet, float altitudeRadii, Vector3 direction)
        {
            float distance = planet.Radius * (1f + Mathf.Max(planet.MaxElevation, 0f)) + planet.Radius * altitudeRadii;
            return new TerrainLodView(direction.normalized * distance, 60f, 1080);
        }

        static int PatchMeshObjects() => Resources.FindObjectsOfTypeAll<Mesh>().Count(m => m.name.StartsWith("Terrain Patch") || m.name.StartsWith("Adaptive Patch Grid") || m.name == "Adaptive Terrain Overlay");

        /// <summary>Every displayed patch mesh holds exactly the geometry of the given planet.</summary>
        static void AssertPatchesSample(AdaptiveTerrainDebugView view, PlanetInstance planet)
        {
            var sampler = new TerrainElevationSampler(planet);
            var builder = new PatchMeshBuilder(view.Settings.patchResolution);
            Assert.IsNotEmpty(view.DisplayedPatches);
            foreach (PlanetPatchId id in view.DisplayedPatches.Take(12))
            {
                Assert.IsTrue(view.Meshes.TryGet(id, out PatchMeshCache.Entry entry), id + " resident");
                Assert.AreEqual(entry.Mesh.vertexCount, entry.Grid.vertexCount, "Vertex grid built with the patch");
                builder.Build(id, sampler, Color.white);
                Vector3[] vertices = entry.Mesh.vertices;
                for (int v = 0; v < vertices.Length; v += 7)
                {
                    Assert.AreEqual(builder.Vertices[v], vertices[v], id + ": vertex " + v + " samples the current planet");
                }
            }
        }

        // P
        [Test]
        public void P_RegenerationAndWorldLoads_InvalidateStalePatchGeometry()
        {
            WorldTestHost host = CreateHost(536452);
            AdaptiveTerrainDebugView view = EnableView(host, AdaptiveTerrainDebugMode.MeshPreview);
            Vector3 direction = new Vector3(0.2f, 0.9f, 0.3f);
            view.Tick(View(host.Current, 0.05f, direction));
            Assert.AreEqual(0, view.PendingPatches, "Complete covering built");
            Assert.AreSame(host.Current, view.SampledPlanet);
            AssertPatchesSample(view, host.Current);
            var firstCovering = view.DisplayedPatches.ToList();
            Vector3[] VerticesOf(PlanetPatchId id) => view.Meshes.TryGet(id, out PatchMeshCache.Entry e) ? e.Mesh.vertices : null;
            var oldVertices = firstCovering.ToDictionary(id => id, VerticesOf);
            Assert.IsTrue(host.Session.Save("World A").Success);
            Guid worldA = host.Session.PlanetId;

            // Regeneration with another seed: the next tick drops every patch of the old planet.
            host.Definition.seed = 868497;
            host.Regenerate();
            view.Tick(View(host.Current, 0.05f, direction));
            Assert.AreEqual(1, view.Invalidations);
            Assert.AreSame(host.Current, view.SampledPlanet);
            Assert.AreEqual(0, view.PendingPatches);
            AssertPatchesSample(view, host.Current);
            List<PlanetPatchId> common = view.DisplayedPatches.Where(oldVertices.ContainsKey).ToList();
            Assert.IsNotEmpty(common, "Same camera: the coverings share patches");
            Assert.IsTrue(common.Any(id => !VerticesOf(id).SequenceEqual(oldVertices[id])), "The same addresses now hold the new planet's terrain");

            // Loading a world replaces the planet too.
            WorldOperationResult load = host.Session.Load(worldA);
            Assert.IsTrue(load.Success, load.Message);
            view.Tick(View(host.Current, 0.05f, direction));
            Assert.AreEqual(2, view.Invalidations);
            Assert.AreSame(host.Current, view.SampledPlanet);
            AssertPatchesSample(view, host.Current);
            CollectionAssert.AreEqual(firstCovering, view.DisplayedPatches, "Same world, same camera: same covering");
            Assert.IsTrue(firstCovering.All(id => VerticesOf(id).SequenceEqual(oldVertices[id])), "Same world: identical patch geometry (deterministic)");

            // The overlay follows the planet as well.
            view.Mode = AdaptiveTerrainDebugMode.Overlay;
            view.Tick(View(host.Current, 0.05f, direction));
            Vector3[] outlineA = view.Overlay.Mesh.vertices;
            host.Definition.seed = 1337;
            host.Regenerate();
            view.Tick(View(host.Current, 0.05f, direction));
            Assert.AreEqual(3, view.Invalidations);
            Assert.IsTrue(view.Overlay.Mesh.vertexCount > 0);
            Assert.IsFalse(outlineA.SequenceEqual(view.Overlay.Mesh.vertices), "Outlines re-sampled on the new terrain");
        }

        // Overlay consistency with the selection (crack contract made visible).
        [Test]
        public void Overlay_MarksExactlyTheTransitionEdgesOfTheCovering()
        {
            WorldTestHost host = CreateHost(609538);
            AdaptiveTerrainDebugView view = EnableView(host, AdaptiveTerrainDebugMode.Overlay, 256);
            view.Tick(View(host.Current, 0.02f, new Vector3(-0.4f, 0.1f, 0.9f)));
            TerrainLodSelection selection = view.Selection;
            CoverageReport report = PatchCoverage.Validate(selection.Leaves.ToList());
            Assert.IsTrue(report.Valid, report.Problem);
            Assert.Greater(report.TransitionEdges, 0);
            Assert.AreEqual(report.TransitionEdges, view.Overlay.TransitionEdges, "Every T-junction edge is drawn red, and only those");
            Assert.AreEqual(selection.Count * 4 * (AdaptiveTerrainOverlay.EdgeSegments + 1), view.Overlay.Mesh.vertexCount);
            Assert.IsTrue(host.Visual.TerrainFacesVisible, "The overlay never hides the planet");
        }

        // Q
        [Test]
        public void Q_RepeatedToggling_ReleasesEveryMesh_AndMeshesStayBounded()
        {
            WorldTestHost host = CreateHost();
            int baseline = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            AdaptiveTerrainDebugView view = AdaptiveTerrainDebugView.GetOrCreate(host.Visual);
            Assert.AreSame(view, AdaptiveTerrainDebugView.GetOrCreate(host.Visual), "Never duplicated");
            Assert.AreEqual(1, host.Visual.GetComponents<AdaptiveTerrainDebugView>().Length);
            Assert.IsFalse(view.enabled, "Off by default");

            for (int round = 0; round < 8; round++)
            {
                EnableView(host, round % 2 == 0 ? AdaptiveTerrainDebugMode.MeshPreview : AdaptiveTerrainDebugMode.Overlay);
                for (int step = 0; step < 4; step++)
                {
                    view.Tick(View(host.Current, 2f / (1 + 4 * step), new Vector3(step, 1f, round)));
                }
                Assert.Greater(view.Selection.Count, 0);
                Assert.LessOrEqual(view.MeshObjects, view.MeshCapacity);
                Assert.AreEqual(round % 2 != 0, host.Visual.TerrainFacesVisible, "Faces hidden only by the mesh preview");
                view.enabled = false;
                Assert.AreEqual(baseline, Resources.FindObjectsOfTypeAll<Mesh>().Length, "Round " + round + ": every native mesh released");
                Assert.IsTrue(host.Visual.TerrainFacesVisible, "The planet's faces are restored");
                Assert.AreEqual(0, view.MeshObjects);
            }

            // Bounded meshes: the camera sweeps the planet with a mesh budget smaller than the covering.
            AdaptiveTerrainDebugView bounded = EnableView(host, AdaptiveTerrainDebugMode.MeshPreview, 128);
            bounded.Settings.maxPatchMeshes = 40;
            var random = new System.Random(5);
            bool exhausted = false;
            for (int step = 0; step < 30; step++)
            {
                var direction = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
                bounded.Tick(View(host.Current, 0.01f + 0.1f * (step % 3), direction));
                Assert.LessOrEqual(bounded.MeshObjects, 40, "Never more patch slots than the budget");
                Assert.LessOrEqual(bounded.Meshes.CreatedCount, 40, "Meshes are recycled, not reallocated");
                Assert.LessOrEqual(bounded.Selection.Count, 128);
                exhausted |= bounded.MeshBudgetExhausted;
            }
            Assert.IsTrue(exhausted, "The exhausted mesh budget is reported");
            Assert.LessOrEqual(PatchMeshObjects(), 2 * 40, "A surface and a vertex grid per patch slot");

            // Destroying the planet with the debug view active releases everything too.
            Object.DestroyImmediate(host.Root);
            Assert.AreEqual(0, PatchMeshObjects(), "No patch mesh survives its planet");
        }

        // R
        [Test]
        public void R_DebugSettings_NeverTouchTheWorldConfigurationOrTriggerRegeneration()
        {
            WorldTestHost host = CreateHost();
            WorldConfiguration before = host.Session.CaptureConfiguration();
            PlanetInstance planet = host.Current;
            int regenerations = host.Regenerations;
            AdaptiveTerrainDebugView view = EnableView(host, AdaptiveTerrainDebugMode.Overlay);
            TerrainLodView camera = View(host.Current, 0.3f, Vector3.forward);
            view.Tick(camera);
            view.Settings.maxScreenError = 2f;
            view.Settings.maxLeafPatches = 200;
            view.Settings.patchResolution = 8;
            view.Settings.hysteresis = 0f;
            view.Tick(camera);
            view.Mode = AdaptiveTerrainDebugMode.MeshPreview;
            view.FreezeSelection = true;
            view.Tick(View(host.Current, 0.01f, Vector3.right));
            view.enabled = false;

            Assert.AreEqual(regenerations, host.Regenerations, "No regeneration");
            Assert.AreSame(planet, host.Current);
            WorldArchiveTestUtil.AssertSameConfiguration(before, host.Session.CaptureConfiguration(), "World settings untouched");
            Assert.IsFalse(host.Session.RefreshDirty(), "No unsaved change");
            Assert.IsTrue(host.Session.Save("Diagnosed").Success);
            string json = File.ReadAllText(store.PathFor(host.Session.PlanetId));
            foreach (string key in new[] { "patchResolution", "maxScreenError", "maxLeafPatches", "maxPatchMeshes", "hysteresis", "assumedMaxSlope" })
            {
                StringAssert.DoesNotContain("\"" + key + "\"", json, "Diagnostic settings are never archived");
            }
        }

        // S
        [Test]
        public void S_ExistingArchives_LoadExactly_WithTheDebugViewActive_AndThumbnailsCaptureTheLegacyFaces()
        {
            File.WriteAllText(store.PathFor(WorldArchiveTestUtil.V013AArchiveId), WorldArchiveTestUtil.V013AArchiveJson);
            WorldTestHost host = CreateHost(1337);
            AdaptiveTerrainDebugView view = EnableView(host, AdaptiveTerrainDebugMode.MeshPreview);
            view.Tick(View(host.Current, 0.5f, Vector3.up));
            Assert.IsFalse(host.Visual.TerrainFacesVisible);
            // V0.2.0B: previews no longer wait; they capture the complete legacy faces, shown only for the capture.
            Assert.DoesNotThrow(() => WorldThumbnailRenderer.CapturePng(host.Visual, 64), "A thumbnail never captures the diagnostic preview");
            Assert.IsFalse(host.Visual.TerrainFacesVisible, "The preview is restored after the capture");

            WorldOperationResult load = host.Session.Load(WorldArchiveTestUtil.V013AArchiveId);
            Assert.IsTrue(load.Success, load.Message);
            Assert.AreEqual(GeneratorCompatibility.Exact, load.Compatibility);
            Assert.IsEmpty(load.Warnings);
            Assert.IsFalse(host.Session.IsDirty);
            view.Tick(View(host.Current, 0.5f, Vector3.up));
            Assert.AreSame(host.Current, view.SampledPlanet, "The debug view follows the loaded world");

            // Saving with the debug view active writes exactly what a save without it writes.
            PlanetArchive original = store.Load(WorldArchiveTestUtil.V013AArchiveId).Archive;
            Assert.IsTrue(host.Session.SaveAsNew("With Debug").Success);
            PlanetArchive withDebug = store.Load(host.Session.PlanetId).Archive;
            view.enabled = false;
            Assert.IsTrue(host.Visual.TerrainFacesVisible);
            Assert.IsTrue(host.Session.SaveAsNew("Without Debug").Success);
            PlanetArchive withoutDebug = store.Load(host.Session.PlanetId).Archive;
            WorldArchiveTestUtil.AssertSameConfiguration(original.Configuration, withDebug.Configuration);
            WorldArchiveTestUtil.AssertSameConfiguration(withoutDebug.Configuration, withDebug.Configuration);
            Assert.DoesNotThrow(() => WorldThumbnailRenderer.CapturePng(host.Visual, 64), "Previews work again once the preview is off");
        }
    }
}
