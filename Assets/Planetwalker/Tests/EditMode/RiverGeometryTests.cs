using System.Collections.Generic;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>V0.1.1D-A.1 river geometry polish: mouths, confluences, crossings, ribbons.</summary>
    public class RiverGeometryTests
    {
        const float Threshold = 7e-4f;

        sealed class Scene
        {
            public PlanetInstance Planet;
            public HydrologyResult Hydrology;
            public RiverNetwork Network;
            public PlanetSurfaceSampler Sampler;
        }

        static readonly Dictionary<string, Scene> Scenes = new Dictionary<string, Scene>();

        /// <summary>Planet + hydrology + network + a sampler on the same vertices as the render mesh.</summary>
        static Scene Build(int seed, int meshResolution = 64, int hydrologyResolution = 128, float threshold = Threshold, int smoothing = 2)
        {
            string key = $"{seed}/{meshResolution}/{hydrologyResolution}/{threshold}/{smoothing}";
            if (Scenes.TryGetValue(key, out Scene cached))
            {
                return cached;
            }
            var planet = new PlanetInstance(new PlanetDefinition { seed = seed, resolution = meshResolution });
            var faces = new IReadOnlyList<Vector3>[CubeSphereGenerator.FaceCount];
            for (int face = 0; face < faces.Length; face++)
            {
                var vertices = new List<Vector3>();
                for (int y = 0; y <= meshResolution; y++)
                {
                    for (int x = 0; x <= meshResolution; x++)
                    {
                        Vector3 d = CubeSphereGenerator.GetSphereDirection(face, x, y, meshResolution);
                        vertices.Add(d * planet.Radius * (1f + planet.GetElevation(d)));
                    }
                }
                faces[face] = vertices;
            }
            var sampler = new PlanetSurfaceSampler();
            sampler.Capture(faces, meshResolution);
            HydrologyResult hydrology = new HydrologySystem().Compute(new HydrologyGrid(hydrologyResolution), planet);
            var system = new RiverNetworkSystem();
            RiverNetwork network = system.Build(hydrology, threshold);
            system.BuildPaths(network, smoothing);
            var scene = new Scene { Planet = planet, Hydrology = hydrology, Network = network, Sampler = sampler };
            Scenes[key] = scene;
            return scene;
        }

        static RiverRibbonBuilder Ribbons(Scene scene, float widthMultiplier = 1f)
        {
            var builder = new RiverRibbonBuilder();
            builder.BuildGeometry(scene.Network, scene.Sampler, scene.Planet, new RiverSettings { widthMultiplier = widthMultiplier });
            return builder;
        }

        static void AssertValidRibbons(RiverRibbonBuilder ribbons, string context)
        {
            IReadOnlyList<Vector3> vertices = ribbons.Vertices;
            IReadOnlyList<int> triangles = ribbons.Triangles;
            Assert.AreEqual(0, triangles.Count % 3, context);
            Assert.AreEqual(0, ribbons.LastDiagnostics.NonFiniteVertices, context);
            foreach (Vector3 v in vertices)
            {
                Assert.IsFalse(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x), context);
            }
            for (int t = 0; t < triangles.Count; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                Assert.IsTrue(a >= 0 && b >= 0 && c >= 0 && a < vertices.Count && b < vertices.Count && c < vertices.Count, context);
                Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                Assert.Greater(normal.magnitude, 0f, $"{context}: degenerate triangle emitted");
                // Ribbon winding: emitted triangles face the planet center (no folds).
                Assert.Less(Vector3.Dot(normal, vertices[a] + vertices[b] + vertices[c]), 0f, $"{context}: folded triangle emitted");
            }
        }

        // TEST A
        [Test]
        public void SameInputs_ProduceIdenticalTrajectoriesAndGeometry()
        {
            Scene scene = Build(868497);
            var system = new RiverNetworkSystem();
            RiverNetwork again = system.Build(scene.Hydrology, Threshold);
            system.BuildPaths(again, 2);
            Assert.AreEqual(scene.Network.PathPointCount, again.PathPointCount);
            for (int i = 0; i < again.PathPointCount; i++)
            {
                Assert.AreEqual(scene.Network.GetPathPoint(i), again.GetPathPoint(i));
            }
            RiverRibbonBuilder first = Ribbons(scene);
            RiverRibbonBuilder second = Ribbons(scene);
            CollectionAssert.AreEqual(first.Vertices, second.Vertices);
            CollectionAssert.AreEqual(first.Triangles, second.Triangles);
        }

        // TEST B
        [Test]
        public void OceanMouths_EndUnderTheRenderedOcean_OrAreReported()
        {
            // Coastlines in every orientation come from several seeds. A mouth either ends under the
            // rendered ocean, or is reported as unresolved (water bodies smaller than the render
            // mesh can show). Tolerance: at most 15% unresolved at mesh 64 / hydrology 128 (measured
            // 2-4 of 29-45 mouths), and at most 5% when both resolutions match (256 / 256).
            foreach ((int seed, int mesh, int hydro, float maxUnresolved) in new[]
                     { (868497, 64, 128, 0.15f), (609538, 64, 128, 0.15f), (280695, 64, 128, 0.15f), (868497, 256, 256, 0.05f) })
            {
                Scene scene = Build(seed, mesh, hydro);
                RiverRibbonBuilder ribbons = Ribbons(scene);
                RiverGeometryDiagnostics diagnostics = ribbons.LastDiagnostics;
                int ocean = 0, submerged = 0;
                for (int ribbon = 0; ribbon < ribbons.RibbonCount; ribbon++)
                {
                    River river = scene.Network.GetRiver(ribbons.GetRibbonRiver(ribbon));
                    if (river.MouthCell < 0)
                    {
                        continue;
                    }
                    ocean++;
                    // Ribbon centerline end, on the rendered terrain.
                    int last = ribbons.GetRibbonStart(ribbon + 1) - 2;
                    Vector3 end = scene.Sampler.Sample(((ribbons.Vertices[last] + ribbons.Vertices[last + 1]) * 0.5f).normalized);
                    if (end.magnitude < scene.Planet.OceanRadius)
                    {
                        submerged++;
                    }
                }
                Assert.AreEqual(diagnostics.OceanMouths, ocean);
                Assert.AreEqual(ocean, submerged + diagnostics.MouthGaps, $"Seed {seed}: every mouth is submerged or reported");
                Assert.LessOrEqual(diagnostics.MouthGaps, maxUnresolved * ocean, $"Seed {seed} ({mesh}/{hydro})");
            }
        }

        // TEST C
        [Test]
        public void Confluences_AreExactAndTangentContinuous()
        {
            Scene scene = Build(868497);
            RiverNetwork network = scene.Network;
            HydrologyGrid grid = scene.Hydrology.Grid;
            int anchors = 0, smooth = 0;
            for (int river = 0; river < network.RiverCount; river++)
            {
                River data = network.GetRiver(river);
                if (data.JunctionCell >= 0)
                {
                    Assert.AreEqual(grid.GetCenter(data.JunctionCell), network.GetPathPoint(network.GetPathEnd(river) - 1));
                }
                for (int i = network.GetPathStart(river) + 1; i < network.GetPathEnd(river) - 1; i++)
                {
                    Vector3 p = network.GetPathPoint(i);
                    int node = network.GetNodeOfCell(grid.CellFromDirection(p));
                    if (node < 0 || (network.GetNodeKind(node) & RiverNodeKind.Confluence) == 0 || p != grid.GetCenter(network.GetNodeCell(node)))
                    {
                        continue;
                    }
                    anchors++;
                    float turn = Vector3.Angle(p - network.GetPathPoint(i - 1), network.GetPathPoint(i + 1) - p);
                    smooth += turn < 1f ? 1 : 0;
                }
            }
            Assert.Greater(anchors, 5);
            // Pieces reverted to the raw path (to remove a smoothing crossing) keep their corner.
            Assert.GreaterOrEqual(smooth, 0.85f * anchors, "Main rivers pass through confluences without a corner");

            RiverRibbonBuilder ribbons = Ribbons(scene);
            Assert.AreEqual(0, ribbons.LastDiagnostics.JunctionDefects);
            for (int ribbon = 0; ribbon < ribbons.RibbonCount; ribbon++)
            {
                River data = network.GetRiver(ribbons.GetRibbonRiver(ribbon));
                if (data.JunctionCell < 0)
                {
                    continue;
                }
                int last = ribbons.GetRibbonStart(ribbon + 1) - 2;
                Vector3 end = (ribbons.Vertices[last] + ribbons.Vertices[last + 1]) * 0.5f;
                Vector3 junction = scene.Sampler.Sample(grid.GetCenter(data.JunctionCell));
                Assert.Less((end - junction).magnitude, 0.01f * scene.Planet.Radius, "Tributary ribbon reaches its junction");
            }
        }

        // TEST D, K
        [Test]
        public void Ribbons_HaveNoInvalidGeometry_AtExtremeSettings()
        {
            foreach ((float threshold, int smoothing, int hydro) in new[]
                     { (HydrologySettings.MaxRiverNetworkThreshold, 0, 64), (Threshold, 3, 64), (5e-5f, 1, 64), (Threshold, 2, 128) })
            {
                Scene scene = Build(609538, 64, hydro, threshold, smoothing);
                foreach (float width in new[] { 0.25f, 1f, 4f })
                {
                    RiverRibbonBuilder ribbons = Ribbons(scene, width);
                    string context = $"threshold {threshold}, smoothing {smoothing}, hydrology {hydro}, width {width}";
                    AssertValidRibbons(ribbons, context);
                    Assert.AreEqual(0, ribbons.LastDiagnostics.JunctionDefects, context);
                    Assert.AreEqual(0, scene.Network.SmoothingCrossingCount, context);
                }
            }
            // Minimum density threshold (densest network) on a small grid.
            Scene dense = Build(280695, 32, 64, HydrologySettings.MinRiverNetworkThreshold, 2);
            AssertValidRibbons(Ribbons(dense, 4f), "minimum threshold");
        }

        // TEST E
        [Test]
        public void Width_ChangesSmoothlyNearConfluences()
        {
            Scene scene = Build(868497);
            RiverNetwork network = scene.Network;
            foreach (float multiplier in new[] { 0.25f, 4f })
            {
                var settings = new RiverSettings { widthMultiplier = multiplier };
                for (int river = 0; river < network.RiverCount; river++)
                {
                    float previous = -1f, distance = 0f;
                    for (int i = network.GetPathStart(river); i < network.GetPathEnd(river); i++)
                    {
                        if (i > network.GetPathStart(river))
                        {
                            distance += (network.GetPathPoint(i) - network.GetPathPoint(i - 1)).magnitude;
                        }
                        float width = settings.GetDisplayWidth(network.GetPathAccumulation(i), distance);
                        Assert.LessOrEqual(width, settings.maxWidth * multiplier + 1e-7f);
                        if (previous > 0f)
                        {
                            // Width never shrinks downstream. Beyond the source taper (an intended
                            // 30% -> 100% ramp), it never jumps by more than 50% between consecutive
                            // trajectory points: confluence increases are spread out.
                            Assert.GreaterOrEqual(width, previous - 1e-7f);
                            if (distance > settings.sourceTaperLength)
                            {
                                Assert.LessOrEqual(width, previous * 1.5f + 1e-7f, $"River {river}, multiplier {multiplier}");
                            }
                        }
                        previous = width;
                    }
                }
            }
        }

        // TEST F
        [Test]
        public void Ribbons_AreContinuousAcrossCubeFaces()
        {
            Scene scene = Build(868497);
            RiverRibbonBuilder ribbons = Ribbons(scene);
            float maxStep = 0.5f * Mathf.PI * 0.5f / 64f * scene.Planet.Radius * 1.6f;
            int faceCrossings = 0;
            for (int ribbon = 0; ribbon < ribbons.RibbonCount; ribbon++)
            {
                int start = ribbons.GetRibbonStart(ribbon), end = ribbons.GetRibbonStart(ribbon + 1);
                for (int v = start + 2; v < end; v += 2)
                {
                    Vector3 a = (ribbons.Vertices[v - 2] + ribbons.Vertices[v - 1]) * 0.5f;
                    Vector3 b = (ribbons.Vertices[v] + ribbons.Vertices[v + 1]) * 0.5f;
                    Assert.LessOrEqual((b - a).magnitude, maxStep, "No gap along a ribbon");
                    faceCrossings += DominantAxis(a) != DominantAxis(b) ? 1 : 0;
                }
            }
            Assert.Greater(faceCrossings, 0, "Some ribbons cross cube faces");
        }

        static int DominantAxis(Vector3 v)
        {
            float x = Mathf.Abs(v.x), y = Mathf.Abs(v.y), z = Mathf.Abs(v.z);
            return x >= y && x >= z ? (v.x > 0 ? 0 : 1) : y >= z ? (v.y > 0 ? 2 : 3) : (v.z > 0 ? 4 : 5);
        }

        // TEST G, H, L
        [Test]
        public void Geometry_DoesNotModifyHydrologyTopologyOrTerrain()
        {
            Scene scene = Build(609538);
            HydrologyResult hydrology = scene.Hydrology;
            RiverNetwork network = scene.Network;
            int cells = hydrology.Grid.CellCount;
            var receivers = new int[cells];
            var filled = new double[cells];
            var accumulation = new double[cells];
            var basins = new int[cells];
            var elevation = new float[cells];
            for (int c = 0; c < cells; c++)
            {
                receivers[c] = hydrology.GetReceiver(c);
                filled[c] = hydrology.GetFilledElevation(c);
                accumulation[c] = hydrology.GetAccumulation(c);
                basins[c] = hydrology.GetBasin(c);
                elevation[c] = scene.Planet.GetElevation(hydrology.Grid.GetCenter(c));
            }
            var kinds = new RiverNodeKind[network.NodeCount];
            var orders = new int[network.NodeCount];
            var downstream = new int[network.NodeCount];
            for (int n = 0; n < network.NodeCount; n++)
            {
                kinds[n] = network.GetNodeKind(n);
                orders[n] = network.GetStrahler(n);
                downstream[n] = network.GetDownstreamNode(n);
            }
            var parents = new int[network.RiverCount];
            for (int r = 0; r < network.RiverCount; r++)
            {
                parents[r] = network.GetRiver(r).ParentRiver;
            }

            var system = new RiverNetworkSystem();
            foreach (int smoothing in new[] { 0, 3, 2 })
            {
                system.BuildPaths(network, smoothing);
                foreach (float width in new[] { 0.25f, 4f })
                {
                    Ribbons(scene, width);
                }
            }

            for (int c = 0; c < cells; c++)
            {
                Assert.AreEqual(receivers[c], hydrology.GetReceiver(c));
                Assert.AreEqual(filled[c], hydrology.GetFilledElevation(c), 0.0);
                Assert.AreEqual(accumulation[c], hydrology.GetAccumulation(c), 0.0);
                Assert.AreEqual(basins[c], hydrology.GetBasin(c));
                Assert.AreEqual(elevation[c], scene.Planet.GetElevation(hydrology.Grid.GetCenter(c)), 0f);
            }
            for (int n = 0; n < network.NodeCount; n++)
            {
                Assert.AreEqual(kinds[n], network.GetNodeKind(n));
                Assert.AreEqual(orders[n], network.GetStrahler(n));
                Assert.AreEqual(downstream[n], network.GetDownstreamNode(n));
            }
            for (int r = 0; r < network.RiverCount; r++)
            {
                Assert.AreEqual(parents[r], network.GetRiver(r).ParentRiver);
            }
        }

        // TEST I
        [Test]
        public void Crossings_AreClassifiedCorrectly()
        {
            Vector3 P(float x, float y) => new Vector3(x, y, 1f).normalized;
            float noWidth = 0f;

            // Valid shared junction: a tributary ending exactly on the parent's point.
            Assert.AreEqual(RiverCrossingKind.SharedJunction, RiverCrossingAnalyzer.Classify(
                P(0, 0), P(0.01f, 0), P(0.01f, 0.01f), P(0.01f, 0),
                P(0, 0), P(0.01f, 0), P(0.01f, 0.01f), P(0.01f, 0), noWidth, noWidth));

            // Independent crossing already present in the drainage paths (X of two diagonals).
            Assert.AreEqual(RiverCrossingKind.GraphInherent, RiverCrossingAnalyzer.Classify(
                P(0, 0), P(0.01f, 0.01f), P(0.01f, 0), P(0, 0.01f),
                P(0, 0), P(0.01f, 0.01f), P(0.01f, 0), P(0, 0.01f), noWidth, noWidth));

            // Raw paths parallel, smoothed paths crossing.
            Assert.AreEqual(RiverCrossingKind.SmoothingInduced, RiverCrossingAnalyzer.Classify(
                P(0, 0), P(0.02f, 0), P(0, 0.005f), P(0.02f, 0.005f),
                P(0, 0), P(0.02f, 0.008f), P(0, 0.005f), P(0.02f, -0.002f), noWidth, noWidth));

            // Centerlines apart, ribbons overlapping.
            Assert.AreEqual(RiverCrossingKind.WidthOverlap, RiverCrossingAnalyzer.Classify(
                P(0, 0), P(0.02f, 0), P(0, 0.004f), P(0.02f, 0.004f),
                P(0, 0), P(0.02f, 0), P(0, 0.004f), P(0.02f, 0.004f), 0.003f, 0.003f));

            // Same configuration, narrow ribbons: no contact.
            Assert.AreEqual(RiverCrossingKind.None, RiverCrossingAnalyzer.Classify(
                P(0, 0), P(0.02f, 0), P(0, 0.004f), P(0.02f, 0.004f),
                P(0, 0), P(0.02f, 0), P(0, 0.004f), P(0.02f, 0.004f), 0.001f, 0.001f));

            // Real planets: steepest-descent D8 never crosses (see report), and smoothing-induced
            // crossings are corrected.
            foreach (int seed in new[] { 868497, 609538 })
            {
                Scene scene = Build(seed, 64, 128, 5e-5f, 2);
                Assert.AreEqual(0, scene.Network.GraphCrossingCount, "Seed " + seed);
                Assert.AreEqual(0, scene.Network.SmoothingCrossingCount, "Seed " + seed);
            }
        }

        [Test]
        public void CrossingSearch_FindsSyntheticCrossingsAndIgnoresJunctions()
        {
            var grid = new HydrologyGrid(64);
            var points = new List<Vector3>
            {
                grid.GetCenter(grid.Index(0, 10, 10)), grid.GetCenter(grid.Index(0, 11, 11)), // line 0: diagonal
                grid.GetCenter(grid.Index(0, 11, 10)), grid.GetCenter(grid.Index(0, 10, 11)), // line 1: crossing diagonal
                grid.GetCenter(grid.Index(0, 12, 12)), grid.GetCenter(grid.Index(0, 11, 11)), // line 2: joins line 0's end
            };
            var crossings = new List<RiverCrossingAnalyzer.Crossing>();
            RiverCrossingAnalyzer.FindCrossings(points, new[] { 0, 2, 4, 6 }, grid, crossings);
            Assert.AreEqual(1, crossings.Count);
            Assert.AreEqual(0, crossings[0].LineA);
            Assert.AreEqual(1, crossings[0].LineB);
        }

        // TEST J (data side; the Unity-side checks are in RiverNetworkSystemTests)
        [Test]
        public void WidthAndSmoothing_NeverRecomputeHydrologyOrTopology()
        {
            var hydrologyCache = new HydrologyCache();
            var riverCache = new RiverNetworkCache();
            var planet = new PlanetInstance(new PlanetDefinition { seed = 280695 });
            HydrologyResult hydrology = hydrologyCache.Get(planet, 64);
            RiverNetwork network = riverCache.Get(hydrology, Threshold, 2);
            foreach (int smoothing in new[] { 0, 1, 3 })
            {
                Assert.AreSame(hydrology, hydrologyCache.Get(planet, 64));
                Assert.AreSame(network, riverCache.Get(hydrology, Threshold, smoothing));
            }
            Assert.AreEqual(1, hydrologyCache.ComputeCount);
            Assert.AreEqual(1, riverCache.BuildCount);
            Assert.AreEqual(4, riverCache.PathBuildCount);
        }

        [Test]
        public void D8Diagnostics_AreFiniteAndSeparateTopologyFromGeometry()
        {
            Scene scene = Build(280695, 64, 128, 5e-5f, 2);
            RiverDiagnostics.DirectionDeviation deviation = RiverDiagnostics.MeasureDirectionDeviation(scene.Network);
            Assert.Greater(deviation.Samples, 100);
            Assert.IsTrue(deviation.RawMeanDegrees > 0f && deviation.RawMeanDegrees < 90f);
            Assert.IsTrue(deviation.PathMeanDegrees > 0f && deviation.PathMeanDegrees < 90f);

            // Grid alignment depends on river endpoints only: identical for any smoothing.
            RiverDiagnostics.GridAlignment smooth = RiverDiagnostics.MeasureGridAlignment(scene.Network);
            var system = new RiverNetworkSystem();
            RiverNetwork raw = system.Build(scene.Hydrology, 5e-5f);
            system.BuildPaths(raw, 0);
            RiverDiagnostics.GridAlignment rawAlignment = RiverDiagnostics.MeasureGridAlignment(raw);
            Assert.Greater(smooth.Rivers, 50);
            Assert.AreEqual(rawAlignment.AlignedFraction, smooth.AlignedFraction, 0f);
            Assert.AreEqual(80f / 360f, smooth.ExpectedFraction, 1e-6f);
        }
    }
}
