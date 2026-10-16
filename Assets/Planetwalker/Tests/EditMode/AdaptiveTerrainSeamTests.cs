using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Rendering;
using Planetwalker.Terrain;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Seamless adaptive terrain (V0.2.0B), engine independent: stitched topology, watertight coverings across
    /// levels, faces and corners, consistent normals and colors on shared vertices, displayed-covering transitions
    /// (no hole while splitting / merging, balance, convergence), and the bounded, generation-safe build pipeline.
    /// </summary>
    public class AdaptiveTerrainSeamTests
    {
        static readonly Dictionary<int, PlanetInstance> Planets = new Dictionary<int, PlanetInstance>();

        static PlanetInstance Planet(int seed = 1337)
        {
            if (!Planets.TryGetValue(seed, out PlanetInstance planet))
            {
                planet = new PlanetInstance(new PlanetDefinition { seed = seed });
                Planets[seed] = planet;
            }
            return planet;
        }

        static AdaptiveSurfaceSnapshot Snapshot(int seed = 1337, int generation = 1, int resolution = 16) =>
            new AdaptiveSurfaceSnapshot(generation, Planet(seed), null, PlanetColorRamp.CreateDefault(), true, resolution);

        static TerrainLodView View(PlanetInstance planet, float altitudeRadii, Vector3 direction)
        {
            float distance = planet.Radius * (1f + Mathf.Max(planet.MaxElevation, 0f)) + planet.Radius * altitudeRadii;
            return new TerrainLodView(direction.normalized * distance, 60f, 1080);
        }

        static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

        static (int, int, int) Key(Vector3 v) => (Bits(v.x), Bits(v.y), Bits(v.z));

        static int Mask(IPatchCovering covering, PlanetPatchId leaf)
        {
            int mask = 0;
            for (int d = 0; d < 4; d++)
            {
                if (covering.Transition(leaf, (PatchDirection)d) == EdgeTransition.Coarser)
                {
                    mask |= 1 << d;
                }
            }
            return mask;
        }

        sealed class SurfaceReport
        {
            public int Vertices, Edges, Faces, Patches, TransitionEdges;
            public double WorstPositionSpread, WorstNormalSpread, WorstColorSpread;
            public int Euler => Vertices - Edges + Faces;
        }

        /// <summary>
        /// Builds every leaf of a covering with its stitch mask and checks the union of the surface triangles
        /// (skirts excluded) is a closed, consistently oriented 2-manifold: each directed edge exactly once and its
        /// reverse exactly once (no crack, no T-junction, no overlap, no flipped triangle). Vertices are identified by
        /// their exact direction (shared vertices are bit-identical by construction).
        /// </summary>
        static SurfaceReport BuildSurface(IPatchCovering covering, AdaptiveSurfaceSnapshot snapshot)
        {
            var report = new SurfaceReport();
            PatchTopology topology = snapshot.Topology;
            var builder = new AdaptivePatchGeometryBuilder();
            var data = new PatchGeometryData(topology.VertexCount);
            var ids = new Dictionary<(int, int, int), int>();
            var positions = new List<Vector3d>();
            var normals = new List<Vector3>();
            var colors = new List<Color>();
            var directed = new Dictionary<(int, int), int>();
            int r = snapshot.Resolution;
            int gridCount = topology.GridVertexCount;
            var vertexIds = new int[gridCount];
            foreach (PlanetPatchId leaf in covering.Leaves)
            {
                int mask = Mask(covering, leaf);
                report.TransitionEdges += CountBits(mask);
                builder.Build(snapshot, leaf, data);
                int lattice = r << leaf.Level;
                for (int j = 0; j <= r; j++)
                {
                    for (int i = 0; i <= r; i++)
                    {
                        int v = i + j * (r + 1);
                        Vector3 direction = CubeSphereCoordinates.LatticeDirection(leaf.Face, leaf.X * r + i, leaf.Y * r + j, lattice);
                        var key = Key(direction);
                        var p = new Vector3d(data.Origin) + new Vector3d(data.Vertices[v]);
                        if (!ids.TryGetValue(key, out int id))
                        {
                            id = ids.Count;
                            ids.Add(key, id);
                            positions.Add(p);
                            normals.Add(data.Normals[v]);
                            colors.Add(data.Colors[v]);
                        }
                        else
                        {
                            report.WorstPositionSpread = Math.Max(report.WorstPositionSpread, (positions[id] - p).Magnitude);
                            report.WorstNormalSpread = Math.Max(report.WorstNormalSpread, (normals[id] - data.Normals[v]).magnitude);
                            Color c = colors[id] - data.Colors[v];
                            report.WorstColorSpread = Math.Max(report.WorstColorSpread, Mathf.Max(Mathf.Abs(c.r), Mathf.Max(Mathf.Abs(c.g), Mathf.Abs(c.b))));
                        }
                        vertexIds[v] = id;
                    }
                }
                int[] indices = topology.Indices(mask);
                for (int t = 0; t < indices.Length; t += 3)
                {
                    if (indices[t] >= gridCount || indices[t + 1] >= gridCount || indices[t + 2] >= gridCount)
                    {
                        continue; // skirt
                    }
                    int a = vertexIds[indices[t]], b = vertexIds[indices[t + 1]], c = vertexIds[indices[t + 2]];
                    Assert.IsTrue(a != b && b != c && a != c, leaf + ": degenerate triangle");
                    foreach ((int, int) e in new[] { (a, b), (b, c), (c, a) })
                    {
                        directed.TryGetValue(e, out int n);
                        directed[e] = n + 1;
                    }
                    report.Faces++;
                }
                report.Patches++;
            }
            var used = new HashSet<int>();
            foreach (KeyValuePair<(int, int), int> e in directed)
            {
                Assert.AreEqual(1, e.Value, "Directed edge used once (no overlap, consistent winding)");
                Assert.IsTrue(directed.TryGetValue((e.Key.Item2, e.Key.Item1), out int back) && back == 1,
                    "Every edge is shared by exactly two triangles of opposite orientation (no crack, no T-junction)");
                used.Add(e.Key.Item1);
                used.Add(e.Key.Item2);
            }
            report.Edges = directed.Count / 2;
            report.Vertices = used.Count;
            return report;
        }

        static int CountBits(int mask)
        {
            int n = 0;
            for (; mask != 0; mask &= mask - 1)
            {
                n++;
            }
            return n;
        }

        struct Vector3d
        {
            public double X, Y, Z;
            public Vector3d(Vector3 v) { X = v.x; Y = v.y; Z = v.z; }
            public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d { X = a.X + b.X, Y = a.Y + b.Y, Z = a.Z + b.Z };
            public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d { X = a.X - b.X, Y = a.Y - b.Y, Z = a.Z - b.Z };
            public double Magnitude => Math.Sqrt(X * X + Y * Y + Z * Z);
        }

        // ------------------------------------------------------------ A - D, H, I (watertight surface)

        [Test]
        public void ABCDHI_MixedCoverings_FormAClosedWatertightSurface_WithSeamlessNormalsAndColors()
        {
            var cases = new[]
            {
                (seed: 1337, altitude: 0.002f, direction: new Vector3(1f, 1f, 1f)),      // a cube corner: three faces
                (seed: 536452, altitude: 0.02f, direction: new Vector3(1f, 0.02f, 0f)),  // along a face edge
                (seed: 868497, altitude: 0.3f, direction: new Vector3(0.3f, 0.5f, -0.81f)),
            };
            var lines = new List<string>();
            foreach (var c in cases)
            {
                AdaptiveSurfaceSnapshot snapshot = Snapshot(c.seed);
                var selector = new TerrainLodSelector(new AdaptiveTerrainSettings { maxLeafPatches = 160 });
                TerrainLodSelection selection = selector.Select(snapshot.Sampler, View(snapshot.Planet, c.altitude, c.direction));
                Assert.IsTrue(PatchCoverage.Validate(selection.Leaves.ToList()).Valid);
                Assert.Greater(selection.MaxLevel - selection.MinLevel, 2, "A covering spanning several levels");
                SurfaceReport report = BuildSurface(selection, snapshot);
                Assert.AreEqual(2, report.Euler, "V - E + F = 2: one closed sphere, no hole, no overlap");
                Assert.Greater(report.TransitionEdges, 0, "Fine / coarse transitions were stitched");
                Assert.Less(report.WorstPositionSpread, snapshot.Planet.Radius * 1e-6, "Shared vertices agree in planet space (float rounding only)");
                Assert.AreEqual(0.0, report.WorstNormalSpread, "Shared vertices have identical normals on every side (levels, faces)");
                Assert.AreEqual(0.0, report.WorstColorSpread, "Shared vertices have identical colors on every side");
                lines.Add(string.Format(CultureInfo.InvariantCulture, "seed {0}: {1} patches L{2}-{3}, {4} transition edges, V {5} E {6} F {7}, worst shared-vertex spread {8:0.0e0} units",
                    c.seed, report.Patches, selection.MinLevel, selection.MaxLevel, report.TransitionEdges, report.Vertices, report.Edges, report.Faces, report.WorstPositionSpread));
            }
            WorldArchiveTestUtil.Report("Watertight adaptive surfaces:\n" + string.Join("\n", lines));
        }

        // ------------------------------------------------------------ G, H (topology)

        [Test]
        public void GH_StitchTopology_IsDeterministic_ValidAndOutward_ForEveryMask()
        {
            AdaptiveSurfaceSnapshot snapshot = Snapshot(609538);
            PatchTopology topology = PatchTopology.For(16);
            Assert.AreSame(topology, PatchTopology.For(16), "Shared per resolution");
            Assert.Throws<ArgumentOutOfRangeException>(() => PatchTopology.For(15), "Stitching needs an even resolution");
            var builder = new AdaptivePatchGeometryBuilder();
            var data = new PatchGeometryData(topology.VertexCount);
            var id = new PlanetPatchId(3, 5, 7, 30);
            builder.Build(snapshot, id, data);
            int grid = topology.GridVertexCount;
            var distinct = new HashSet<string>();
            for (int mask = 0; mask < PatchTopology.MaskCount; mask++)
            {
                int[] indices = topology.Indices(mask);
                CollectionAssert.AreEqual(indices, PatchTopology.For(16).Indices(mask), "Deterministic");
                Assert.IsTrue(distinct.Add(string.Join(",", indices)), "Each mask has its own triangulation");
                int stitched = CountBits(mask);
                int surface = 0;
                for (int t = 0; t < indices.Length; t += 3)
                {
                    int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                    Assert.IsTrue(a >= 0 && b >= 0 && c >= 0 && a < topology.VertexCount && b < topology.VertexCount && c < topology.VertexCount);
                    Assert.IsTrue(a != b && b != c && a != c, "No degenerate index triple");
                    Vector3 n = Vector3.Cross(data.Vertices[b] - data.Vertices[a], data.Vertices[c] - data.Vertices[a]);
                    Assert.Greater(n.magnitude, 0f, "No zero-area triangle");
                    if (a < grid && b < grid && c < grid)
                    {
                        surface++;
                        Vector3 up = (data.Origin + data.Vertices[a]).normalized;
                        Assert.Greater(Vector3.Dot(n, up), 0f, "Surface triangles face outward (planet faces' winding)");
                    }
                    else
                    {
                        // Skirt: faces away from the patch center.
                        Vector3 middle = (data.Vertices[a] + data.Vertices[b] + data.Vertices[c]) / 3f;
                        Vector3 up = data.Origin.normalized;
                        Vector3 outward = middle - up * Vector3.Dot(middle, up);
                        Assert.Greater(Vector3.Dot(n, outward), 0f, "Skirts face outward of the patch");
                    }
                }
                // 8 triangles per 2x2 block, minus one per skipped odd edge vertex.
                Assert.AreEqual(16 * 16 * 2 - stitched * 8, surface, "mask " + mask);
            }
        }

        // ------------------------------------------------------------ E, F, K, L, X (displayed covering)

        [Test]
        public void EFKLX_DisplayedCovering_NeverHasAHole_StaysBalanced_AndConverges()
        {
            AdaptiveSurfaceSnapshot snapshot = Snapshot(536452);
            var random = new System.Random(17);
            var settings = new AdaptiveTerrainSettings { maxLeafPatches = 256 };
            var selector = new TerrainLodSelector(settings);
            var covering = new AdaptiveCovering();
            var ready = new HashSet<PlanetPatchId>();
            var needed = new List<PatchRequest>();
            Func<PlanetPatchId, bool> isReady = ready.Contains;
            int splits = 0, merges = 0, frames = 0;
            Vector3 direction = new Vector3(0.2f, 0.9f, -0.3f);
            float altitude = 3f;
            for (int frame = 0; frame < 400; frame++)
            {
                // Rapid, erratic camera: zooms in and out, jumps around the planet.
                if (frame % 7 == 0)
                {
                    direction = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
                }
                altitude = Mathf.Clamp(altitude * (random.NextDouble() < 0.5 ? 0.6f : 1.5f), 0.0005f, 8f);
                TerrainLodSelection target = selector.Select(snapshot.Sampler, View(snapshot.Planet, altitude, direction));
                covering.Update(target, settings.EffectiveMinLevel, isReady, selector.ScreenError, needed);
                Assert.AreEqual(needed.Count, needed.Select(n => n.Id).Distinct().Count(), "Each missing patch requested once");
                Assert.IsTrue(needed.All(n => !ready.Contains(n.Id)), "Only missing patches are requested");
                // Some of the needed meshes finish this frame (random latency); some old ones get evicted.
                foreach (PatchRequest request in needed)
                {
                    if (random.NextDouble() < 0.35)
                    {
                        ready.Add(request.Id);
                    }
                }
                if (ready.Count > 900)
                {
                    foreach (PlanetPatchId stale in ready.Where(id => !covering.IsLeaf(id) && random.NextDouble() < 0.3).ToList())
                    {
                        ready.Remove(stale);
                    }
                }
                if (covering.IsComplete)
                {
                    CoverageReport report = PatchCoverage.Validate(covering.Leaves.ToList());
                    Assert.IsTrue(report.Valid, "Frame " + frame + ": " + report.Problem);
                    Assert.IsTrue(covering.Leaves.All(isReady), "Only built patches are displayed");
                    frames++;
                }
                splits += covering.LastSplits;
                merges += covering.LastMerges;
            }
            Assert.Greater(frames, 300);
            Assert.Greater(splits, 50, "Subdivisions happened");
            Assert.Greater(merges, 50, "Merges happened");

            // With everything ready and a still camera, the displayed covering reaches the target exactly.
            TerrainLodSelection still = selector.Select(snapshot.Sampler, View(snapshot.Planet, 0.01f, Vector3.up));
            for (int frame = 0; frame < 64 && !covering.Converged; frame++)
            {
                covering.Update(still, settings.EffectiveMinLevel, isReady, selector.ScreenError, needed);
                foreach (PatchRequest request in needed)
                {
                    ready.Add(request.Id);
                }
            }
            Assert.IsTrue(covering.Converged);
            CollectionAssert.AreEqual(still.Leaves, covering.Leaves, "Converged to the target");
            WorldArchiveTestUtil.Report("Displayed covering under an erratic camera: " + splits + " splits, " + merges + " merges, valid on all " + frames + " frames");
        }

        [Test]
        public void K_AParentStaysDisplayedUntilAllItsChildrenAreReady()
        {
            AdaptiveSurfaceSnapshot snapshot = Snapshot();
            var settings = new AdaptiveTerrainSettings { maxLeafPatches = 64 };
            var selector = new TerrainLodSelector(settings);
            var covering = new AdaptiveCovering();
            var ready = new HashSet<PlanetPatchId>();
            var needed = new List<PatchRequest>();
            TerrainLodSelection target = selector.Select(snapshot.Sampler, View(snapshot.Planet, 0.05f, Vector3.forward));
            covering.Update(target, 0, ready.Contains, null, needed);
            Assert.IsFalse(covering.IsComplete, "Nothing displayed before the initial covering is built");
            Assert.AreEqual(6, needed.Count);
            needed.Take(5).ToList().ForEach(n => ready.Add(n.Id));
            covering.Update(target, 0, ready.Contains, null, needed);
            Assert.IsFalse(covering.IsComplete, "Five of six faces are not a covering");
            ready.Add(needed[0].Id);
            covering.Update(target, 0, ready.Contains, null, needed);
            Assert.IsTrue(covering.IsComplete);
            Assert.AreEqual(6, covering.Count);
            covering.Update(target, 0, ready.Contains, null, needed);
            Assert.IsNotEmpty(needed, "The target asks for finer patches");

            // Three of four children ready: the parent stays.
            PlanetPatchId parent = needed.Select(n => n.Id.Parent).First();
            for (int c = 0; c < 3; c++)
            {
                ready.Add(parent.Child(c));
            }
            covering.Update(target, 0, ready.Contains, null, needed);
            Assert.IsTrue(covering.IsLeaf(parent), "Parent kept until the whole region can be replaced");
            ready.Add(parent.Child(3));
            covering.Update(target, 0, ready.Contains, null, needed);
            Assert.IsFalse(covering.IsLeaf(parent));
            Assert.IsTrue(Enumerable.Range(0, 4).All(c => covering.IsLeaf(parent.Child(c))), "Replaced atomically by its four children");
        }

        // ------------------------------------------------------------ M, N, O (pipeline)

        [Test]
        public void MNO_Pipeline_IsBounded_CancelsUnneededWork_AndNeverDeliversAnOlderGeneration()
        {
            foreach (int workers in new[] { 0, 3 })
            {
                AdaptiveSurfaceSnapshot first = Snapshot(1337, 1);
                using var scheduler = new PatchBuildScheduler(workers, 12);
                var requests = Enumerable.Range(0, 64).Select(i => new PatchRequest { Id = new PlanetPatchId(i % 6, 3, i % 8, i / 8 % 8), Priority = 64 - i }).ToList();
                scheduler.Request(first, requests, _ => false);
                Assert.LessOrEqual(scheduler.Pending, 12, "O: bounded pending work");
                scheduler.RunSynchronous(1000);

                // The world changes while builds are in flight: none of them may reach the new world.
                AdaptiveSurfaceSnapshot second = Snapshot(868497, 2);
                scheduler.CancelAll();
                scheduler.Request(second, requests.Take(4).ToList(), _ => false);
                var delivered = new List<PatchGeometryData>();
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 20000 && (scheduler.Pending > 0 || scheduler.ReadyCount > 0))
                {
                    scheduler.Request(second, requests.Take(4).ToList(), id => delivered.Any(d => d.Id == id));
                    scheduler.RunSynchronous(1000);
                    while (scheduler.TryTakeResult(second, out PatchGeometryData data))
                    {
                        delivered.Add(data);
                    }
                    Thread.Sleep(1);
                }
                Assert.AreEqual(4, delivered.Count, "Exactly the requests of the new world (workers " + workers + ")");
                Assert.IsTrue(delivered.All(d => d.Generation == 2), "N: never a result of the older generation");
                var sampler = second.Sampler;
                foreach (PatchGeometryData d in delivered)
                {
                    Vector3 corner = d.Origin + d.Vertices[0];
                    Vector3 expected = sampler.SurfacePoint(CubeSphereCoordinates.CornerDirection(d.Id, 0));
                    Assert.Less((corner - expected).magnitude, 1e-4f, "Geometry of the new planet");
                }
                Assert.Greater(scheduler.Cancelled + scheduler.Discarded, 0, "M: obsolete work cancelled or discarded");

                // Unneeded queued requests are dropped as the camera moves on.
                AdaptiveSurfaceSnapshot third = Snapshot(1337, 3);
                scheduler.CancelAll();
                int cancelled = scheduler.Cancelled;
                scheduler.Request(third, requests, _ => false);
                scheduler.Request(third, requests.Skip(40).ToList(), _ => false);
                Assert.LessOrEqual(scheduler.Pending, 12);
                if (workers == 0)
                {
                    Assert.Greater(scheduler.Cancelled, cancelled, "Requests that left the needed list are cancelled");
                }
            }
        }

        // ------------------------------------------------------------ J, Y (sampling rules)

        [Test]
        public void JY_PatchSampling_UsesTheExistingElevationAndColorRules()
        {
            AdaptiveSurfaceSnapshot snapshot = Snapshot(609538);
            PlanetInstance planet = snapshot.Planet;
            var builder = new AdaptivePatchGeometryBuilder();
            var data = new PatchGeometryData(snapshot.Topology.VertexCount);
            var id = new PlanetPatchId(4, 6, 20, 41);
            builder.Build(snapshot, id, data);
            int r = snapshot.Resolution;
            for (int v = 0; v < snapshot.Topology.GridVertexCount; v += 5)
            {
                int i = v % (r + 1), j = v / (r + 1);
                Vector3 direction = CubeSphereCoordinates.LatticeDirection(id.Face, id.X * r + i, id.Y * r + j, r << id.Level);
                float elevation = planet.Height.GetElevation(direction);
                Vector3 expected = direction * (planet.Radius * (1f + elevation));
                Assert.Less(((data.Origin + data.Vertices[v]) - expected).magnitude, planet.Radius * 1e-6f, "Existing elevation field");
                Vector3 normal = planet.Height.GetSurfaceNormal(direction, elevation, snapshot.NormalSampleAngle);
                Assert.AreEqual(normal, data.Normals[v]);
                Color color = AdaptivePatchGeometryBuilder.ToLinear(PlanetMeshBuilder.TerrainColor(null, snapshot.Ramp, planet.SeaLevel, direction, elevation, normal));
                Assert.AreEqual(color, data.Colors[v], "The legacy color rule");
            }
            // The managed sRGB curve.
            Assert.AreEqual(0f, AdaptivePatchGeometryBuilder.ToLinear(Color.black).r);
            Assert.AreEqual(1f, AdaptivePatchGeometryBuilder.ToLinear(Color.white).r, 1e-6f);
            Assert.AreEqual(0.2140411f, AdaptivePatchGeometryBuilder.ToLinear(new Color(0.5f, 0.5f, 0.5f)).r, 1e-5f);
        }

        // ------------------------------------------------------------ Performance (offline / batch report)

        [Test]
        public void Performance_SelectionAndConstruction_ReferenceSeeds()
        {
            var lines = new List<string> { "seed | camera | target leaves | levels | select ms (moving) | build ms/patch | memory MB (2x budget slots)" };
            var builder = new AdaptivePatchGeometryBuilder();
            foreach (int seed in WorldArchiveTestUtil.ReferenceSeeds)
            {
                AdaptiveSurfaceSnapshot snapshot = Snapshot(seed);
                foreach ((string name, float altitude) in new[] { ("distant 7R", 7f), ("medium 1R", 1f), ("close 0.05R", 0.05f), ("near 0.0005R", 0.0005f) })
                {
                    var selector = new TerrainLodSelector(new AdaptiveTerrainSettings());
                    Vector3 direction = new Vector3(0.3f, 0.5f, -0.81f);
                    for (int warm = 0; warm < 5; warm++)
                    {
                        selector.Select(snapshot.Sampler, View(snapshot.Planet, altitude, direction));
                    }
                    var watch = Stopwatch.StartNew();
                    const int frames = 30;
                    TerrainLodSelection s = null;
                    for (int f = 0; f < frames; f++)
                    {
                        // Moving camera: orbiting by 0.2 degrees per frame.
                        float a = 0.2f * f * Mathf.Deg2Rad;
                        var d = new Vector3(direction.x * Mathf.Cos(a) + direction.z * Mathf.Sin(a), direction.y, -direction.x * Mathf.Sin(a) + direction.z * Mathf.Cos(a));
                        s = selector.Select(snapshot.Sampler, View(snapshot.Planet, altitude, d));
                    }
                    double selectMs = watch.Elapsed.TotalMilliseconds / frames;
                    var data = new PatchGeometryData(snapshot.Topology.VertexCount);
                    watch.Restart();
                    int built = Math.Min(24, s.Count);
                    for (int i = 0; i < built; i++)
                    {
                        builder.Build(snapshot, s.Leaves[i * s.Count / built], data);
                    }
                    double buildMs = watch.Elapsed.TotalMilliseconds / built;
                    long bytes = (2L * 512 + 64) * (snapshot.Topology.VertexCount * 40L + snapshot.Topology.Indices(0).Length * 2L);
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} | {1} | {2} | {3}-{4} | {5:0.00} | {6:0.00} | {7:0.0}",
                        seed, name, s.Count, s.MinLevel, s.MaxLevel, selectMs, buildMs, bytes / (1024.0 * 1024.0)));
                    Assert.Less(selectMs, 50.0);
                }
            }
            WorldArchiveTestUtil.Report(string.Join("\n", lines));
        }
    }
}
