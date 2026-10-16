using System;
using System.Collections.Generic;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// V0.1.1D-B.3: shoreline and river transition polish. Letters refer to the milestone's
    /// regression list (A - P). Reference seeds 926747 / 204018 are regression references only.
    /// </summary>
    public class LakeTransitionPolishTests
    {
        const float Threshold = 7e-4f;
        static readonly int[] Seeds = { 926747, 204018, 868497, 609538, 280695 };
        static readonly (int Hydrology, int Terrain)[] Matrix = { (64, 256), (128, 256), (256, 256), (128, 64) };

        sealed class Scene
        {
            public PlanetInstance Planet;
            public LakeSet Lakes;
            public RiverNetwork Network;
            public LakeRiverLayer Layer;
            public PlanetSurfaceSampler Sampler;
            public LakeMeshBuilder Water;
            public RiverRibbonBuilder Ribbons;
        }

        static readonly Dictionary<(int, int), HydrologyResult> Hydrology = new Dictionary<(int, int), HydrologyResult>();
        static readonly Dictionary<(int, int), PlanetSurfaceSampler> Samplers = new Dictionary<(int, int), PlanetSurfaceSampler>();
        static readonly Dictionary<(int, int, int), Scene> Scenes = new Dictionary<(int, int, int), Scene>();

        static PlanetSurfaceSampler Sampler(Func<Vector3, float> elevation, float radius, int resolution)
        {
            var faces = new IReadOnlyList<Vector3>[CubeSphereGenerator.FaceCount];
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                var list = new List<Vector3>();
                for (int y = 0; y <= resolution; y++)
                {
                    for (int x = 0; x <= resolution; x++)
                    {
                        Vector3 d = CubeSphereGenerator.GetSphereDirection(face, x, y, resolution);
                        list.Add(d * (radius * (1f + elevation(d))));
                    }
                }
                faces[face] = list;
            }
            var sampler = new PlanetSurfaceSampler();
            sampler.Capture(faces, resolution);
            return sampler;
        }

        static RiverNetwork Network(HydrologyResult hydrology)
        {
            var system = new RiverNetworkSystem();
            RiverNetwork network = system.Build(hydrology, Threshold);
            system.BuildPaths(network, 2);
            return network;
        }

        static Scene Build(int seed, int hydrology, int terrain)
        {
            if (Scenes.TryGetValue((seed, hydrology, terrain), out Scene scene))
            {
                return scene;
            }
            var planet = new PlanetInstance(new PlanetDefinition { seed = seed, resolution = terrain });
            if (!Hydrology.TryGetValue((seed, hydrology), out HydrologyResult result))
            {
                result = new HydrologySystem().Compute(new HydrologyGrid(hydrology), planet);
                Hydrology.Add((seed, hydrology), result);
            }
            LakeSet lakes = new LakeCache().Get(result, new LakeSettings(), new LakeContext(seed, planet.Height.GetMountainInfluence));
            if (!Samplers.TryGetValue((seed, terrain), out PlanetSurfaceSampler sampler))
            {
                sampler = Sampler(planet.GetLegacyElevation, planet.Radius, terrain);
                Samplers.Add((seed, terrain), sampler);
            }
            RiverNetwork network = Network(result);
            LakeRiverLayer layer = LakeRiverIntegration.Build(network, lakes);
            var water = new LakeMeshBuilder();
            water.BuildGeometry(lakes, planet, sampler);
            var ribbons = new RiverRibbonBuilder();
            ribbons.BuildGeometry(network, sampler, planet, new RiverSettings(), layer, water);
            scene = new Scene { Planet = planet, Lakes = lakes, Network = network, Layer = layer, Sampler = sampler, Water = water, Ribbons = ribbons };
            Scenes.Add((seed, hydrology, terrain), scene);
            return scene;
        }

        /// <summary>Reference seeds at the standard configuration plus the resolution matrix of 926747 / 204018.</summary>
        static IEnumerable<(Scene Scene, string Context)> AllScenes()
        {
            foreach (int seed in Seeds)
            {
                yield return (Build(seed, 128, 256), $"seed {seed} 128/256");
            }
            foreach (int seed in new[] { 926747, 204018 })
            {
                foreach ((int h, int t) in Matrix)
                {
                    if (h != 128 || t != 256)
                    {
                        yield return (Build(seed, h, t), $"seed {seed} {h}/{t}");
                    }
                }
            }
        }

        static bool HasTriangleBetween(RiverRibbonBuilder ribbons, int pairA, int pairB)
        {
            for (int t = 0; t < ribbons.Triangles.Count; t += 3)
            {
                bool a = false, b = false;
                for (int e = 0; e < 3; e++)
                {
                    int v = ribbons.Triangles[t + e];
                    a |= v == pairA || v == pairA + 1;
                    b |= v == pairB || v == pairB + 1;
                }
                if (a && b)
                {
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------ A, B, E, F (lake surface)

        // A, B
        [Test]
        public void AB_LakeTriangles_AreNonDegenerate_NotSlivered_AndOutward()
        {
            foreach ((Scene scene, string context) in AllScenes())
            {
                LakeMeshBuilder water = scene.Water;
                Assert.AreEqual(0, water.LastDiagnostics.DegenerateTrianglesSkipped, context + ": degenerate clipped triangle");
                int slivers = 0;
                for (int t = 0; t < water.Triangles.Count; t += 3)
                {
                    Vector3 a = water.Vertices[water.Triangles[t]], b = water.Vertices[water.Triangles[t + 1]], c = water.Vertices[water.Triangles[t + 2]];
                    float shape = LakeMeshBuilder.ShapeOf(a, b, c);
                    Assert.Greater(shape, 1e-4f, $"{context}: near-degenerate lake triangle (boundary flags {water.WaterAttributes[water.Triangles[t]].y}/{water.WaterAttributes[water.Triangles[t + 1]].y}/{water.WaterAttributes[water.Triangles[t + 2]].y}, "
                        + $"edges {(b - a).magnitude:0.000000} {(c - b).magnitude:0.000000} {(a - c).magnitude:0.000000})");
                    slivers += shape < LakeMeshBuilder.SliverShape ? 1 : 0;
                    Vector3 normal = Vector3.Cross(b - a, c - a);
                    Assert.Greater(Vector3.Dot(normal, a + b + c), 0f, context + ": inward lake triangle");
                }
                Assert.AreEqual(slivers, water.LastDiagnostics.Slivers, context + ": measured sliver count");
                Assert.LessOrEqual(slivers, Mathf.Max(2, water.Triangles.Count / 3 / 200), context + ": slivers above 0.5% of the triangles");
            }
        }

        // E, F
        [Test]
        public void EF_LakeSurfaces_StayLevel_AndHaveNoAccidentalHoles()
        {
            foreach ((Scene scene, string context) in AllScenes())
            {
                LakeMeshBuilder water = scene.Water;
                for (int lake = 0; lake < scene.Lakes.Count; lake++)
                {
                    float waterRadius = scene.Planet.Radius * (1f + scene.Lakes.GetLake(lake).Level);
                    for (int v = water.GetLakeVertexStart(lake); v < water.GetLakeVertexStart(lake + 1); v++)
                    {
                        Assert.AreEqual(waterRadius, water.Vertices[v].magnitude, 2e-6f * scene.Planet.Radius, context + ": water off its level");
                    }
                }
                LakeMeshDiagnostics d = water.LastDiagnostics;
                Assert.AreEqual(0, d.CrackEdges, context + ": hole inside the water");
                Assert.AreEqual(0, d.NonManifoldEdges, context);
                Assert.AreEqual(0, d.TerrainIntersectionWarnings, context);

                // Every open mesh edge is a boundary segment (shoreline or basin cut), and every
                // boundary segment closes into loops: the only openings are real shorelines.
                var open = new Dictionary<long, int>();
                for (int t = 0; t < water.Triangles.Count; t += 3)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        int i = water.Triangles[t + e], j = water.Triangles[t + (e + 1) % 3];
                        long key = ((long)Mathf.Min(i, j) << 32) | (uint)Mathf.Max(i, j);
                        open.TryGetValue(key, out int uses);
                        open[key] = uses + 1;
                    }
                }
                var segments = new HashSet<long>();
                var degree = new Dictionary<int, int>();
                for (int s = 0; s < water.BoundarySegmentCount; s++)
                {
                    water.GetBoundarySegment(s, out int a, out int b, out _);
                    segments.Add(((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b));
                    degree.TryGetValue(a, out int da);
                    degree[a] = da + 1;
                    degree.TryGetValue(b, out int db);
                    degree[b] = db + 1;
                }
                foreach (KeyValuePair<long, int> edge in open)
                {
                    if (edge.Value == 1)
                    {
                        Assert.IsTrue(segments.Contains(edge.Key), context + ": open edge that is not a shoreline (hole)");
                    }
                }
                foreach (KeyValuePair<int, int> entry in degree)
                {
                    Assert.AreEqual(0, entry.Value % 2, context + ": open shoreline loop");
                }
            }
        }

        // ------------------------------------------------------------ C, D

        // C
        [Test]
        public void C_ShorelineGeneration_IsDeterministic()
        {
            Scene scene = Build(926747, 128, 256);
            var again = new LakeMeshBuilder();
            again.BuildGeometry(scene.Lakes, scene.Planet, scene.Sampler);
            CollectionAssert.AreEqual(scene.Water.Vertices, again.Vertices);
            CollectionAssert.AreEqual(scene.Water.Triangles, again.Triangles);
            Assert.AreEqual(scene.Water.BoundarySegmentCount, again.BoundarySegmentCount);
            for (int s = 0; s < again.BoundarySegmentCount; s++)
            {
                scene.Water.GetBoundarySegment(s, out int a0, out int b0, out bool c0);
                again.GetBoundarySegment(s, out int a1, out int b1, out bool c1);
                Assert.AreEqual((a0, b0, c0), (a1, b1, c1));
            }
            int n = scene.Sampler.Resolution;
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                for (int y = 0; y <= n; y += 3)
                {
                    for (int x = 0; x <= n; x += 3)
                    {
                        Assert.AreEqual(scene.Water.GetVertexFlags(face, x, y), again.GetVertexFlags(face, x, y));
                    }
                }
            }
            var ribbons = new RiverRibbonBuilder();
            ribbons.BuildGeometry(scene.Network, scene.Sampler, scene.Planet, new RiverSettings(), scene.Layer, again);
            CollectionAssert.AreEqual(scene.Ribbons.Vertices, ribbons.Vertices);
            CollectionAssert.AreEqual(scene.Ribbons.Triangles, ribbons.Triangles);
        }

        // D
        [Test]
        public void D_Water_NeverExtendsBeyondTheAuthorizedBasin()
        {
            foreach ((Scene scene, string context) in AllScenes())
            {
                LakeSet lakes = scene.Lakes;
                HydrologyGrid grid = lakes.Hydrology.Grid;
                LakeMeshBuilder water = scene.Water;
                for (int lake = 0; lake < lakes.Count; lake++)
                {
                    for (int t = water.GetLakeTriangleStart(lake); t < water.GetLakeTriangleStart(lake + 1); t += 3)
                    {
                        Vector3 centroid = (water.Vertices[water.Triangles[t]] + water.Vertices[water.Triangles[t + 1]]
                            + water.Vertices[water.Triangles[t + 2]]).normalized;
                        int cell = grid.CellFromDirection(centroid);
                        bool authorized = LakeMeshBuilder.IsInLakeRegion(lakes, lake, cell);
                        // Triangles straddling the region border have their centroid at most one cell out.
                        for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell) && !authorized; k++)
                        {
                            authorized = LakeMeshBuilder.IsInLakeRegion(lakes, lake, grid.GetNeighbor(k));
                        }
                        Assert.IsTrue(authorized, $"{context}: water of lake {lake} beyond its footprint / shore ring");
                    }
                    for (int v = water.GetLakeVertexStart(lake); v < water.GetLakeVertexStart(lake + 1); v++)
                    {
                        if (water.WaterAttributes[v].y < 0.5f)
                        {
                            Assert.IsTrue(LakeMeshBuilder.IsInLakeRegion(lakes, lake, grid.CellFromDirection(water.Vertices[v].normalized)),
                                $"{context}: interior water of lake {lake} outside its region");
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------ G, H, I, J, L (transitions)

        /// <summary>A ribbon edge vertex is past the shoreline on land when it is not under the water and not
        /// a fold-collapsed copy of the previous vertex (which adds no surface).</summary>
        static bool EdgeOnLand(Scene scene, int lake, int vertex)
        {
            RiverRibbonBuilder ribbons = scene.Ribbons;
            if (scene.Water.IsUnderWater(lake, ribbons.Vertices[vertex].normalized))
            {
                return false;
            }
            return ribbons.Vertices[vertex] != ribbons.Vertices[vertex - 2];
        }

        // G, I, L
        [Test]
        public void GIL_RiverInlets_ReachTheShoreline_StayInTheWater_AndFlareGently()
        {
            int inlets = 0;
            foreach ((Scene scene, string context) in AllScenes())
            {
                RiverRibbonBuilder ribbons = scene.Ribbons;
                for (int ribbon = 0; ribbon < ribbons.RibbonCount; ribbon++)
                {
                    int first = ribbons.GetRibbonStart(ribbon), end = ribbons.GetRibbonStart(ribbon + 1);
                    Assert.Greater(end - first, 2, context + ": ribbon of fewer than two samples");
                    int crossing = ribbons.GetRibbonEndCrossing(ribbon);
                    if (crossing < 0)
                    {
                        continue;
                    }
                    inlets++;
                    int lake = scene.Layer.GetReach(ribbons.GetRibbonReach(ribbon)).EndLake;
                    // G: reaches the rendered shoreline from land, continuous across it.
                    Assert.IsFalse(scene.Water.IsUnderWater(lake, ribbons.SampleDirections[crossing - 2]), context);
                    Assert.IsTrue(HasTriangleBetween(ribbons, crossing - 2, crossing), context + ": gap before the inlet");
                    Assert.IsTrue(HasTriangleBetween(ribbons, crossing, crossing + 2), context + ": gap after the inlet");
                    // No wedge: past the crossing both edges stay in the lake water (hidden by it).
                    for (int v = crossing + 2; v < end; v += 2)
                    {
                        Assert.IsFalse(EdgeOnLand(scene, lake, v) || EdgeOnLand(scene, lake, v + 1), $"{context}: inlet ribbon edge on land past the shoreline "
                            + $"(pair {(v - crossing) / 2} of {(end - crossing) / 2}, center under {scene.Water.IsUnderWater(lake, ribbons.SampleDirections[v])}, "
                            + $"left under {scene.Water.IsUnderWater(lake, ribbons.Vertices[v].normalized)} right under {scene.Water.IsUnderWater(lake, ribbons.Vertices[v + 1].normalized)}, "
                            + $"left = prev {ribbons.Vertices[v] == ribbons.Vertices[v - 2]}, right = prev {ribbons.Vertices[v + 1] == ribbons.Vertices[v - 1]}, "
                            + $"half width {ribbons.GetVertexHalfWidth(v)}, left dist {(ribbons.Vertices[v].normalized - ribbons.SampleDirections[v]).magnitude * scene.Planet.Radius}, "
                            + $"right dist {(ribbons.Vertices[v + 1].normalized - ribbons.SampleDirections[v]).magnitude * scene.Planet.Radius})");
                    }
                    // I: bounded, monotonic, smooth flare.
                    for (int v = first + 2; v < end; v += 2)
                    {
                        float flare = ribbons.GetVertexFlare(v), previous = ribbons.GetVertexFlare(v - 2);
                        Assert.That(flare, Is.InRange(1f, 1.3f + 1e-4f), context + ": flare out of bounds");
                        if (v <= crossing && ribbons.GetRibbonStartCrossing(ribbon) < 0)
                        {
                            Assert.GreaterOrEqual(flare + 1e-5f, previous, context + ": flare not monotonic towards the lake");
                            Assert.LessOrEqual(flare - previous, 0.16f, context + ": abrupt flare step");
                        }
                    }
                    Assert.AreEqual(1.3f, ribbons.GetVertexFlare(crossing), 1e-4f, context);
                }
                // L: no hidden-only ribbon was emitted; every ribbon has a visible sample.
                for (int ribbon = 0; ribbon < ribbons.RibbonCount; ribbon++)
                {
                    bool visible = false;
                    for (int v = ribbons.GetRibbonStart(ribbon); v < ribbons.GetRibbonStart(ribbon + 1) && !visible; v += 2)
                    {
                        visible = !ribbons.IsVertexSubmerged(v) || scene.Water.GetLakeUnderWater(ribbons.SampleDirections[v]) < 0;
                    }
                    Assert.IsTrue(visible, context + ": detached ribbon entirely under a lake");
                }
            }
            Assert.Greater(inlets, 20, "Inlets were tested");
        }

        // H, J
        [Test]
        public void HJ_LakeOutlets_LeaveTheShoreline_StayInTheWater_AndFlareGently()
        {
            int outlets = 0;
            foreach ((Scene scene, string context) in AllScenes())
            {
                RiverRibbonBuilder ribbons = scene.Ribbons;
                for (int ribbon = 0; ribbon < ribbons.RibbonCount; ribbon++)
                {
                    int crossing = ribbons.GetRibbonStartCrossing(ribbon);
                    if (crossing < 0)
                    {
                        continue;
                    }
                    outlets++;
                    RiverReach reach = scene.Layer.GetReach(ribbons.GetRibbonReach(ribbon));
                    int first = ribbons.GetRibbonStart(ribbon), end = ribbons.GetRibbonStart(ribbon + 1);
                    Assert.AreEqual(LakeClass.Open, scene.Lakes.GetLake(reach.StartLake).Class, context);
                    Assert.IsFalse(scene.Water.IsUnderWater(reach.StartLake, ribbons.SampleDirections[crossing + 2]), context);
                    Assert.IsTrue(HasTriangleBetween(ribbons, crossing - 2, crossing), context + ": gap before the outlet");
                    Assert.IsTrue(HasTriangleBetween(ribbons, crossing, crossing + 2), context + ": gap after the outlet");
                    for (int v = first; v < crossing; v += 2)
                    {
                        Assert.IsTrue(scene.Water.IsUnderWater(reach.StartLake, ribbons.SampleDirections[v]), context + ": detached outlet start");
                        bool onLand = (v > first && EdgeOnLand(scene, reach.StartLake, v)) || (v > first && EdgeOnLand(scene, reach.StartLake, v + 1));
                        Assert.IsFalse(onLand, $"{context}: outlet ribbon edge on land before the shoreline (pair {(v - crossing) / 2}, "
                            + $"left under {scene.Water.IsUnderWater(reach.StartLake, ribbons.Vertices[v].normalized)} right under {scene.Water.IsUnderWater(reach.StartLake, ribbons.Vertices[v + 1].normalized)}, "
                            + $"left = next {ribbons.Vertices[v] == ribbons.Vertices[v + 2]}, right = next {ribbons.Vertices[v + 1] == ribbons.Vertices[v + 3]})");
                    }
                    for (int v = first + 2; v < end; v += 2)
                    {
                        float flare = ribbons.GetVertexFlare(v), previous = ribbons.GetVertexFlare(v - 2);
                        Assert.That(flare, Is.InRange(1f, 1.3f + 1e-4f), context);
                        if (v > crossing && ribbons.GetRibbonEndCrossing(ribbon) < 0)
                        {
                            Assert.LessOrEqual(flare, previous + 1e-5f, context + ": flare not decreasing away from the lake");
                            Assert.LessOrEqual(previous - flare, 0.16f, context + ": abrupt flare step");
                        }
                    }
                }
            }
            Assert.Greater(outlets, 10, "Outlets were tested");
        }

        // K
        [Test]
        public void K_ClosedLakesHaveNoOutlet_AndOpenLakesAtMostOne()
        {
            foreach ((Scene scene, string context) in AllScenes())
            {
                var outlets = new Dictionary<int, int>();
                for (int ribbon = 0; ribbon < scene.Ribbons.RibbonCount; ribbon++)
                {
                    RiverReach reach = scene.Layer.GetReach(scene.Ribbons.GetRibbonReach(ribbon));
                    if (reach.Start != ReachStart.LakeOutlet)
                    {
                        continue;
                    }
                    Assert.AreEqual(LakeClass.Open, scene.Lakes.GetLake(reach.StartLake).Class, context + ": outlet ribbon of a closed lake");
                    outlets.TryGetValue(reach.StartLake, out int count);
                    outlets[reach.StartLake] = count + 1;
                }
                foreach (KeyValuePair<int, int> entry in outlets)
                {
                    Assert.AreEqual(1, entry.Value, $"{context}: duplicate outlet ribbons for lake {entry.Key}");
                }
            }
        }

        // ------------------------------------------------------------ O, P

        // O
        [Test]
        public void O_LakeAndRiverIdentities_DoNotDependOnTheTerrainMesh()
        {
            foreach (int seed in new[] { 926747, 204018 })
            {
                Scene coarse = Build(seed, 128, 64), fine = Build(seed, 128, 256);
                Assert.AreSame(coarse.Lakes.Hydrology, fine.Lakes.Hydrology, "Same hydrology analysis");
                Assert.AreEqual(coarse.Lakes.Count, fine.Lakes.Count);
                for (int lake = 0; lake < coarse.Lakes.Count; lake++)
                {
                    Lake a = coarse.Lakes.GetLake(lake), b = fine.Lakes.GetLake(lake);
                    Assert.AreEqual((a.Id, a.Key, a.Class, a.Level, a.CellCount, a.OutletCell), (b.Id, b.Key, b.Class, b.Level, b.CellCount, b.OutletCell));
                }
                Assert.AreEqual(coarse.Layer.ReachCount, fine.Layer.ReachCount);
                for (int r = 0; r < coarse.Layer.ReachCount; r++)
                {
                    RiverReach a = coarse.Layer.GetReach(r), b = fine.Layer.GetReach(r);
                    Assert.AreEqual((a.River, a.FirstNode, a.LastNode, a.Start, a.End, a.StartLake, a.EndLake),
                        (b.River, b.FirstNode, b.LastNode, b.Start, b.End, b.StartLake, b.EndLake));
                }
                // Building the geometry never touches the data (recomputed independently).
                var planet = new PlanetInstance(new PlanetDefinition { seed = seed });
                LakeSet again = new LakeCache().Get(new HydrologySystem().Compute(new HydrologyGrid(128), planet), new LakeSettings(),
                    new LakeContext(seed, planet.Height.GetMountainInfluence));
                for (int lake = 0; lake < again.Count; lake++)
                {
                    Assert.AreEqual(fine.Lakes.GetLake(lake).Key, again.GetLake(lake).Key);
                    Assert.AreEqual(fine.Lakes.GetLake(lake).Level, again.GetLake(lake).Level);
                }
            }
        }

        // P
        [Test]
        public void P_Diagnostics_DescribeMeasuredConditions()
        {
            foreach ((Scene scene, string context) in AllScenes())
            {
                LakeMeshBuilder water = scene.Water;
                LakeMeshDiagnostics d = water.LastDiagnostics;
                int hidden = 0;
                for (int lake = 0; lake < scene.Lakes.Count; lake++)
                {
                    hidden += water.GetLakeTriangleStart(lake + 1) == water.GetLakeTriangleStart(lake) ? 1 : 0;
                }
                Assert.AreEqual(hidden, d.UnrepresentedLakes, context);
                int shore = 0, cuts = 0;
                for (int s = 0; s < water.BoundarySegmentCount; s++)
                {
                    water.GetBoundarySegment(s, out _, out _, out bool cut);
                    shore += cut ? 0 : 1;
                    cuts += cut ? 1 : 0;
                }
                Assert.AreEqual(shore, d.ShorelineSegments, context);
                Assert.AreEqual(cuts, d.BasinCutSegments, context);

                // Flags agree with the counts.
                int dropped = 0, problems = 0;
                int n = scene.Sampler.Resolution;
                var seen = new HashSet<Vector3>();
                for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
                {
                    for (int y = 0; y <= n; y++)
                    {
                        for (int x = 0; x <= n; x++)
                        {
                            ShorelineFlags flags = water.GetVertexFlags(face, x, y);
                            if (flags == ShorelineFlags.None || !seen.Add(CubeSphereGenerator.GetSphereDirection(face, x, y, n)))
                            {
                                continue;
                            }
                            dropped += (flags & ShorelineFlags.Dropped) != 0 ? 1 : 0;
                            problems += (flags & ShorelineFlags.Problem) != 0 ? 1 : 0;
                        }
                    }
                }
                Assert.AreEqual(d.DroppedPuddles, dropped, context);
                Assert.AreEqual(d.DegenerateTrianglesSkipped + d.TerrainIntersectionWarnings == 0, problems == 0, context);

                // Transition markers match the ribbons and the counters.
                RiverRibbonBuilder ribbons = scene.Ribbons;
                int inlets = 0, outlets = 0, unresolved = 0;
                for (int m = 0; m < ribbons.MarkerCount; m++)
                {
                    ribbons.GetMarker(m, out _, out TransitionMarker kind);
                    inlets += kind == TransitionMarker.Inlet ? 1 : 0;
                    outlets += kind == TransitionMarker.Outlet ? 1 : 0;
                    unresolved += kind == TransitionMarker.Unresolved ? 1 : 0;
                }
                int endCrossings = 0, startCrossings = 0;
                for (int ribbon = 0; ribbon < ribbons.RibbonCount; ribbon++)
                {
                    endCrossings += ribbons.GetRibbonEndCrossing(ribbon) >= 0 ? 1 : 0;
                    startCrossings += ribbons.GetRibbonStartCrossing(ribbon) >= 0 ? 1 : 0;
                }
                Assert.AreEqual(endCrossings, inlets, context);
                Assert.AreEqual(startCrossings, outlets, context);
                Assert.AreEqual(ribbons.LastDiagnostics.UnresolvedShorelineIntersections, unresolved, context);
                Assert.LessOrEqual(inlets, ribbons.LastDiagnostics.RiverToLakeTransitions, context);
            }

            // A lake smaller than a render quad of a coarse mesh: not drawn, and reported as such.
            const int mesh = 8;
            Vector3 quadCenter = (CubeSphereGenerator.GetSphereDirection(0, 3, 4, mesh) + CubeSphereGenerator.GetSphereDirection(0, 4, 4, mesh)
                + CubeSphereGenerator.GetSphereDirection(0, 3, 5, mesh) + CubeSphereGenerator.GetSphereDirection(0, 4, 5, mesh)).normalized;
            Func<Vector3, float> terrain = d => 0.012f + 0.002f * d.x + Bowl(d, quadCenter, 0.06f, 0.004f) - (d.x < -0.6f ? 0.03f : 0f);
            HydrologyResult hydrology = new HydrologySystem().Compute(new HydrologyGrid(64), terrain, 0f);
            LakeSet lakes = new LakeSystem().Select(new DepressionAnalyzer().Analyze(hydrology),
                new LakeSettings { abundance = 1f, minimumArea = LakeSettings.MinMinimumArea, fillingFactor = 0.7f }, new LakeContext(1));
            int small = lakes.GetLakeOfCell(hydrology.Grid.CellFromDirection(quadCenter));
            Assert.GreaterOrEqual(small, 0, "The synthetic lake is selected");
            var coarseWater = new LakeMeshBuilder();
            coarseWater.BuildGeometry(lakes, 100f, Sampler(terrain, 100f, mesh));
            Assert.AreEqual(coarseWater.GetLakeTriangleStart(small), coarseWater.GetLakeTriangleStart(small + 1), "Not representable at mesh 8");
            Assert.GreaterOrEqual(coarseWater.LastDiagnostics.UnrepresentedLakes, 1, "Reported");
            var fineWater = new LakeMeshBuilder();
            fineWater.BuildGeometry(lakes, 100f, Sampler(terrain, 100f, 128));
            Assert.Greater(fineWater.GetLakeTriangleStart(small + 1), fineWater.GetLakeTriangleStart(small), "Drawn at mesh 128");
        }

        static float Bowl(Vector3 d, Vector3 center, float radius, float depth)
        {
            float a = Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, center.normalized), -1f, 1f)) / radius;
            return a < 1f ? -depth * (1f - a * a) : 0f;
        }

        // Matrix report (generation costs, measured once per configuration).
        [Test]
        public void ResolutionMatrix_ReferenceSeeds_Report()
        {
            foreach (int seed in new[] { 926747, 204018 })
            {
                foreach ((int h, int t) in Matrix)
                {
                    Scene scene = Build(seed, h, t);
                    var water = new LakeMeshBuilder();
                    water.BuildGeometry(scene.Lakes, scene.Planet, scene.Sampler);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var ribbons = new RiverRibbonBuilder();
                    ribbons.BuildGeometry(scene.Network, scene.Sampler, scene.Planet, new RiverSettings(), scene.Layer, water);
                    float ribbonMs = (float)watch.Elapsed.TotalMilliseconds;
                    LakeMeshDiagnostics d = water.LastDiagnostics;
                    RiverGeometryDiagnostics r = ribbons.LastDiagnostics;
                    string context = $"seed {seed} {h}/{t}";
                    Assert.AreEqual(0, d.CrackEdges, context + " cracks");
                    Assert.AreEqual(0, d.NonManifoldEdges, context + " non-manifold");
                    Assert.AreEqual(0, d.TerrainIntersectionWarnings, context + " intersections");
                    Assert.AreEqual(0, d.DegenerateTrianglesSkipped, context + " degenerate");
                    // Gaps (no rendered nor hydrological shoreline near a reach end) exist on coarse grids
                    // (204018 at hydrology 64, as in B.2); they must be reported, never silent.
                    Assert.LessOrEqual(r.LakeInflowGaps + r.LakeOutflowGaps, r.UnresolvedShorelineIntersections, context + " unreported gap");
                    TestContext.WriteLine($"seed {seed} {h}/{t}: lakes {scene.Lakes.Count} (not shown {d.UnrepresentedLakes}), tris {d.Triangles}, slivers {d.Slivers}, "
                        + $"shore {d.ShorelineSegments} seg, cuts {d.BasinCutSegments} (at vertex {d.BasinCutsAtVertex}, max drop {d.MaxBasinCutDrop:0.000000}), "
                        + $"detached parts {d.DetachedFragments}, puddles dropped {d.DroppedPuddles}, welded {d.WeldedBoundaryVertices}, water {water.LastMilliseconds:0.0} ms; gaps {r.LakeInflowGaps}/{r.LakeOutflowGaps}, "
                        + $"inlets {r.RiverToLakeTransitions}, outlets {r.LakeToRiverTransitions}, unresolved {r.UnresolvedShorelineIntersections}, "
                        + $"clamped edges {r.ClampedTransitionEdges}, hidden reaches {r.HiddenReachesSkipped}, ribbons {ribbonMs:0.0} ms (transitions {r.TransitionMilliseconds:0.0})");
                }
            }
        }

        // ------------------------------------------------------------ M, N (Unity objects)

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
            root = new GameObject("Transition Polish Test");
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
            visual.Lakes.showLakes = true;
            visual.Rivers.showRivers = true;
            return visual;
        }

        // M
        [Test]
        public void M_ShorelineDebugView_IsConsistentAcrossCubeFaceSeams()
        {
            PlanetVisualController visual = CreateVisual();
            visual.DebugView = PlanetDebugView.LakeShoreline;
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 926747, resolution = 64 }));
            Assert.IsFalse(visual.LakesVisible, "Water hidden in the shoreline view");
            Assert.IsTrue(visual.RiversVisible, "Ribbons drawn over the shoreline view");
            var seen = new Dictionary<Vector3, Color>();
            int shared = 0, shoreline = 0;
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                Mesh mesh = root.transform.Find("Terrain " + CubeSphereGenerator.GetFaceName(face)).GetComponent<MeshFilter>().sharedMesh;
                Vector3[] vertices = mesh.vertices;
                Color[] colors = mesh.colors;
                for (int i = 0; i < vertices.Length; i++)
                {
                    Assert.AreNotEqual(Color.magenta, colors[i], "Missing data");
                    shoreline += colors[i].r > 0.99f && colors[i].g > 0.99f && colors[i].b > 0.99f ? 1 : 0;
                    if (seen.TryGetValue(vertices[i], out Color other))
                    {
                        Assert.AreEqual(other, colors[i], "Seam color mismatch at " + vertices[i]);
                        shared++;
                    }
                    else
                    {
                        seen.Add(vertices[i], colors[i]);
                    }
                }
            }
            Assert.Greater(shared, 0);
            Assert.Greater(shoreline, 0, "Shoreline vertices are shown");
        }

        // N
        [Test]
        public void N_VisualSettings_DoNotRecomputeHydrologyOrRebuildUnrelatedGeometry()
        {
            PlanetVisualController visual = CreateVisual();
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 204018, resolution = 48 }));
            int hydrology = visual.HydrologyCache.ComputeCount;
            int analyses = visual.LakeCache.AnalysisCount;
            int water = visual.LakeMeshBuildCount;
            int ribbons = visual.RiverRibbonBuildCount;

            visual.Lakes.color = new Color(0.1f, 0.3f, 0.4f);
            visual.RefreshColors();
            Assert.AreEqual(ribbons, visual.RiverRibbonBuildCount, "Lake color does not rebuild the rivers");
            Assert.AreEqual(water, visual.LakeMeshBuildCount);

            visual.Rivers.widthMultiplier = 1.4f;
            visual.RefreshColors();
            Assert.AreEqual(water, visual.LakeMeshBuildCount, "River width does not rebuild the lakes");
            Assert.AreEqual(ribbons + 1, visual.RiverRibbonBuildCount);

            // Entering and leaving the shoreline view reuses the water geometry of this mesh.
            visual.DebugView = PlanetDebugView.LakeShoreline;
            visual.RefreshColors();
            visual.DebugView = PlanetDebugView.None;
            visual.RefreshColors();
            Assert.AreEqual(water, visual.LakeMeshBuildCount, "Debug view reuses the lake geometry");
            Assert.AreEqual(hydrology, visual.HydrologyCache.ComputeCount);
            Assert.AreEqual(analyses, visual.LakeCache.AnalysisCount);
        }
    }
}
