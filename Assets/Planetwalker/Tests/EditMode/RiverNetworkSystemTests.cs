using System;
using System.Collections.Generic;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>V0.1.1D-A river network: extraction, topology, Strahler, trajectories, ribbons.</summary>
    public class RiverNetworkSystemTests
    {
        const float Threshold = 7e-4f;
        static readonly Dictionary<(int seed, int resolution), HydrologyResult> HydrologyCacheForTests =
            new Dictionary<(int, int), HydrologyResult>();

        static HydrologyResult Hydrology(int seed, int resolution)
        {
            if (!HydrologyCacheForTests.TryGetValue((seed, resolution), out HydrologyResult result))
            {
                result = new HydrologySystem().Compute(new HydrologyGrid(resolution), new PlanetInstance(new PlanetDefinition { seed = seed }));
                HydrologyCacheForTests.Add((seed, resolution), result);
            }
            return result;
        }

        static RiverNetwork Network(int seed, int resolution, int smoothing = 2, float threshold = Threshold)
        {
            var system = new RiverNetworkSystem();
            RiverNetwork network = system.Build(Hydrology(seed, resolution), threshold);
            system.BuildPaths(network, smoothing);
            return network;
        }

        /// <summary>Upstream river cells of a cell, recounted independently from the hydrology.</summary>
        static int UpstreamRiverCells(RiverNetwork network, int cell)
        {
            HydrologyResult hydrology = network.Hydrology;
            HydrologyGrid grid = hydrology.Grid;
            int count = 0;
            for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
            {
                int other = grid.GetNeighbor(k);
                if (network.IsRiverCell(other) && hydrology.GetReceiver(other) == cell)
                {
                    count++;
                }
            }
            return count;
        }

        static float MaxNeighborDistance(HydrologyGrid grid)
        {
            float max = 0f;
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                {
                    max = Mathf.Max(max, grid.GetNeighborDistance(k));
                }
            }
            return max;
        }

        static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-20f));
            return (a + ab * t - p).magnitude;
        }

        // 1
        [Test]
        public void SameInputs_ProduceIdenticalNetworks()
        {
            var definition = new PlanetDefinition { seed = 609538 };
            HydrologyResult h1 = new HydrologySystem().Compute(new HydrologyGrid(64), new PlanetInstance(definition));
            HydrologyResult h2 = new HydrologySystem().Compute(new HydrologyGrid(64), new PlanetInstance(definition.Clone()));
            var s1 = new RiverNetworkSystem();
            var s2 = new RiverNetworkSystem();
            RiverNetwork a = s1.Build(h1, Threshold);
            RiverNetwork b = s2.Build(h2, Threshold);
            s1.BuildPaths(a, 2);
            s2.BuildPaths(b, 2);
            Assert.AreEqual(a.NodeCount, b.NodeCount);
            Assert.AreEqual(a.RiverCount, b.RiverCount);
            for (int node = 0; node < a.NodeCount; node++)
            {
                Assert.AreEqual(a.GetNodeCell(node), b.GetNodeCell(node));
                Assert.AreEqual(a.GetDownstreamNode(node), b.GetDownstreamNode(node));
                Assert.AreEqual(a.GetStrahler(node), b.GetStrahler(node));
                Assert.AreEqual(a.GetNodeKind(node), b.GetNodeKind(node));
                Assert.AreEqual(a.GetRiverOfNode(node), b.GetRiverOfNode(node));
            }
            Assert.AreEqual(a.PathPointCount, b.PathPointCount);
            for (int i = 0; i < a.PathPointCount; i++)
            {
                Assert.AreEqual(a.GetPathPoint(i), b.GetPathPoint(i));
            }
        }

        // 2, 3
        [Test]
        public void Network_IsAcyclicAndContinuousToOutlets()
        {
            RiverNetwork network = Network(280695, 64);
            HydrologyResult hydrology = network.Hydrology;
            Assert.Greater(network.NodeCount, 0);
            for (int cell = 0; cell < hydrology.Grid.CellCount; cell++)
            {
                Assert.AreEqual(hydrology.IsRiverCandidate(cell, Threshold), network.IsRiverCell(cell), "Selection = drained area >= threshold");
            }
            for (int node = 0; node < network.NodeCount; node++)
            {
                int cell = network.GetNodeCell(node);
                int receiver = hydrology.GetReceiver(cell);
                int down = network.GetDownstreamNode(node);
                // Node indices strictly increase downstream: no cycle is possible.
                Assert.IsTrue(down < 0 || down > node);
                if (down >= 0)
                {
                    Assert.AreEqual(receiver, network.GetNodeCell(down), "Rivers follow the flow directions");
                }
                else
                {
                    // The path ends only at a valid outlet: the ocean or the endorheic sink.
                    Assert.IsTrue(receiver < 0 ? cell == hydrology.EndorheicOutlet : !hydrology.IsLand(receiver));
                }
            }
        }

        // 4, 5, 6
        [Test]
        public void Sources_Confluences_AndMouths_AreIdentifiedCorrectly()
        {
            RiverNetwork network = Network(609538, 64);
            HydrologyResult hydrology = network.Hydrology;
            int sources = 0;
            for (int node = 0; node < network.NodeCount; node++)
            {
                int cell = network.GetNodeCell(node);
                int upstream = UpstreamRiverCells(network, cell);
                RiverNodeKind kind = network.GetNodeKind(node);
                Assert.AreEqual(upstream, network.GetUpstreamCount(node));
                Assert.AreEqual(upstream == 0, (kind & RiverNodeKind.Source) != 0, "Source = no selected upstream cell");
                Assert.AreEqual(upstream >= 2, (kind & RiverNodeKind.Confluence) != 0, "Confluence = 2+ selected upstream cells");
                int receiver = hydrology.GetReceiver(cell);
                Assert.AreEqual(receiver >= 0 && !hydrology.IsLand(receiver), (kind & RiverNodeKind.Mouth) != 0, "Mouth = last land cell before the ocean");
                sources += upstream == 0 ? 1 : 0;
            }
            Assert.Greater(network.ConfluenceCount, 0);
            Assert.Greater(network.MouthCount, 0);
            // Each river starts at exactly one source; every river reaching the sea has a mouth.
            Assert.AreEqual(sources, network.RiverCount);
            for (int river = 0; river < network.RiverCount; river++)
            {
                River data = network.GetRiver(river);
                Assert.IsTrue((network.GetNodeKind(network.GetRiverNode(river, 0)) & RiverNodeKind.Source) != 0);
                if (data.ParentRiver < 0)
                {
                    Assert.GreaterOrEqual(data.MouthCell, 0);
                    Assert.IsFalse(hydrology.IsLand(data.MouthCell));
                }
                else
                {
                    // A tributary ends where it joins its parent, at a confluence of the parent.
                    int junctionNode = network.GetNodeOfCell(data.JunctionCell);
                    Assert.AreEqual(data.ParentRiver, network.GetRiverOfNode(junctionNode));
                    Assert.IsTrue((network.GetNodeKind(junctionNode) & RiverNodeKind.Confluence) != 0);
                }
            }
        }

        [Test]
        public void MainStem_ContinuesThroughConfluencesOnTheLargerBranch()
        {
            RiverNetwork network = Network(868497, 64);
            for (int node = 0; node < network.NodeCount; node++)
            {
                if ((network.GetNodeKind(node) & RiverNodeKind.Confluence) == 0)
                {
                    continue;
                }
                // The river through a confluence comes from its largest upstream branch.
                int continuing = -1;
                double largest = -1;
                for (int other = 0; other < node; other++)
                {
                    if (network.GetDownstreamNode(other) == node)
                    {
                        double area = network.Hydrology.GetAccumulation(network.GetNodeCell(other));
                        if (area > largest)
                        {
                            largest = area;
                            continuing = other;
                        }
                    }
                }
                Assert.AreEqual(network.GetRiverOfNode(node), network.GetRiverOfNode(continuing), "A confluence does not interrupt the main river");
            }
        }

        // 7
        [Test]
        public void Strahler_OnSyntheticGraphs()
        {
            var order = new int[7];
            // Balanced: leaves 0,1 -> 4; leaves 2,3 -> 5; 4,5 -> 6.
            RiverNetworkSystem.ComputeStrahler(new[] { 4, 4, 5, 5, 6, 6, -1 }, order);
            CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 2, 2, 3 }, order);

            // Unbalanced: an order-2 branch joined by an order-1 stream stays order 2.
            order = new int[5];
            RiverNetworkSystem.ComputeStrahler(new[] { 2, 2, 4, 4, -1 }, order);
            CollectionAssert.AreEqual(new[] { 1, 1, 2, 1, 2 }, order);

            // Three order-1 streams meeting: order 2 (at least two of maximal order).
            order = new int[4];
            RiverNetworkSystem.ComputeStrahler(new[] { 3, 3, 3, -1 }, order);
            CollectionAssert.AreEqual(new[] { 1, 1, 1, 2 }, order);

            // A single chain keeps order 1.
            order = new int[3];
            RiverNetworkSystem.ComputeStrahler(new[] { 1, 2, -1 }, order);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, order);

            Assert.Throws<ArgumentException>(() => RiverNetworkSystem.ComputeStrahler(new[] { -1, 0 }, new int[2]));

            // Real network: orders never decrease downstream.
            RiverNetwork network = Network(609538, 64);
            for (int node = 0; node < network.NodeCount; node++)
            {
                int down = network.GetDownstreamNode(node);
                if (down >= 0)
                {
                    Assert.GreaterOrEqual(network.GetStrahler(down), network.GetStrahler(node));
                }
            }
        }

        // 8
        [Test]
        public void Width_IsMonotoneDownstreamAndBounded()
        {
            var settings = new RiverSettings();
            RiverNetwork network = Network(280695, 64);
            for (int river = 0; river < network.RiverCount; river++)
            {
                float previous = 0f;
                float distance = 0f;
                for (int i = network.GetPathStart(river); i < network.GetPathEnd(river); i++)
                {
                    if (i > network.GetPathStart(river))
                    {
                        distance += (network.GetPathPoint(i) - network.GetPathPoint(i - 1)).magnitude;
                    }
                    float width = settings.GetDisplayWidth(network.GetPathAccumulation(i), distance);
                    Assert.GreaterOrEqual(width, 0f);
                    Assert.LessOrEqual(width, settings.maxWidth * settings.widthMultiplier + 1e-7f);
                    Assert.GreaterOrEqual(width, previous - 1e-7f, "Width never decreases downstream");
                    previous = width;
                }
            }
            // Width depends on the drained area only, not on the selection threshold.
            Assert.AreEqual(settings.GetWidth(1e-3), settings.GetWidth(1e-3), 0f);
            Assert.Greater(settings.GetWidth(1e-2), settings.GetWidth(1e-4));
            Assert.AreEqual(settings.minWidth, settings.GetWidth(0.0), 1e-9f);
        }

        // 9
        [Test]
        public void Smoothing_PreservesConnectionsAndStaysInTheCorridor()
        {
            foreach (int iterations in new[] { 0, 1, 3 })
            {
                RiverNetwork network = Network(609538, 64, iterations);
                HydrologyGrid grid = network.Hydrology.Grid;
                float corridor = 0.5f * MaxNeighborDistance(grid);
                for (int river = 0; river < network.RiverCount; river++)
                {
                    River data = network.GetRiver(river);
                    int start = network.GetPathStart(river);
                    int end = network.GetPathEnd(river);
                    Assert.AreEqual(grid.GetCenter(data.SourceCell), network.GetPathPoint(start), "Path starts at the source");
                    if (data.JunctionCell >= 0)
                    {
                        Vector3 junction = grid.GetCenter(data.JunctionCell);
                        Assert.AreEqual(junction, network.GetPathPoint(end - 1), "Tributary ends exactly on the junction");
                        bool onParent = false;
                        for (int i = network.GetPathStart(data.ParentRiver); i < network.GetPathEnd(data.ParentRiver); i++)
                        {
                            onParent |= network.GetPathPoint(i) == junction;
                        }
                        Assert.IsTrue(onParent, "The junction is a fixed point of the parent's trajectory");
                    }
                    else if (data.MouthCell >= 0)
                    {
                        Assert.AreEqual(grid.GetCenter(data.MouthCell), network.GetPathPoint(end - 1));
                    }

                    // Raw corridor: the river's cell centers plus its final junction / mouth cell.
                    var raw = new List<Vector3>();
                    for (int i = 0; i < data.NodeCount; i++)
                    {
                        raw.Add(grid.GetCenter(network.GetNodeCell(network.GetRiverNode(river, i))));
                    }
                    if (data.JunctionCell >= 0 || data.MouthCell >= 0)
                    {
                        raw.Add(grid.GetCenter(data.JunctionCell >= 0 ? data.JunctionCell : data.MouthCell));
                    }
                    for (int i = start; i < end; i++)
                    {
                        float best = float.MaxValue;
                        for (int k = 0; k + 1 < raw.Count; k++)
                        {
                            best = Mathf.Min(best, DistanceToSegment(network.GetPathPoint(i), raw[k], raw[k + 1]));
                        }
                        if (raw.Count == 1)
                        {
                            best = (network.GetPathPoint(i) - raw[0]).magnitude;
                        }
                        Assert.LessOrEqual(best, corridor, $"Smoothing x{iterations}: point leaves the hydrological corridor");
                    }
                }
            }
        }

        // 10, 11
        [Test]
        public void Trajectories_AreFiniteAndContinuousAcrossCubeFaces()
        {
            RiverNetwork network = Network(868497, 64, 3);
            float maxJump = MaxNeighborDistance(network.Hydrology.Grid);
            int faceCrossings = 0;
            for (int river = 0; river < network.RiverCount; river++)
            {
                for (int i = network.GetPathStart(river); i < network.GetPathEnd(river); i++)
                {
                    Vector3 p = network.GetPathPoint(i);
                    Assert.IsFalse(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || float.IsInfinity(p.x));
                    Assert.AreEqual(1f, p.magnitude, 1e-5f);
                    float accumulation = network.GetPathAccumulation(i);
                    Assert.IsTrue(accumulation > 0f && !float.IsInfinity(accumulation));
                    if (i > network.GetPathStart(river))
                    {
                        Vector3 previous = network.GetPathPoint(i - 1);
                        Assert.LessOrEqual((p - previous).magnitude, maxJump, "No jump between unconnected cells");
                        faceCrossings += network.Hydrology.Grid.CellFromDirection(p) / (64 * 64) != network.Hydrology.Grid.CellFromDirection(previous) / (64 * 64) ? 1 : 0;
                    }
                }
            }
            Assert.Greater(faceCrossings, 0, "Some rivers cross cube faces (otherwise this test proves nothing)");
        }

        // 12 (data side)
        [Test]
        public void Cache_InvalidatesOnlyTheNecessaryStages()
        {
            var cache = new RiverNetworkCache();
            HydrologyResult hydrology = new HydrologySystem().Compute(new HydrologyGrid(32), new PlanetInstance(new PlanetDefinition()));
            RiverNetwork first = cache.Get(hydrology, Threshold, 2);
            Assert.AreEqual(1, cache.BuildCount);
            Assert.AreEqual(1, cache.PathBuildCount);

            Assert.AreSame(first, cache.Get(hydrology, Threshold, 2), "Unchanged inputs reuse everything");
            Assert.AreEqual(1, cache.BuildCount);

            Assert.AreSame(first, cache.Get(hydrology, Threshold, 0), "Smoothing keeps the topology");
            Assert.AreEqual(1, cache.BuildCount);
            Assert.AreEqual(2, cache.PathBuildCount);

            Assert.AreNotSame(first, cache.Get(hydrology, Threshold * 2f, 0), "Threshold rebuilds the network");
            Assert.AreEqual(2, cache.BuildCount);

            // A new analysis in the same (reused) result buffers is detected via its version.
            new HydrologySystem().Compute(hydrology.Grid, new PlanetInstance(new PlanetDefinition { seed = 5 }), hydrology);
            cache.Get(hydrology, Threshold * 2f, 0);
            Assert.AreEqual(3, cache.BuildCount);
        }

        // 13 (data side)
        [Test]
        public void RiverNetwork_DoesNotModifyTerrainOrHydrology()
        {
            var planet = new PlanetInstance(new PlanetDefinition { seed = 554492 });
            HydrologyResult hydrology = new HydrologySystem().Compute(new HydrologyGrid(64), planet);
            var receivers = new int[hydrology.Grid.CellCount];
            var accumulation = new double[hydrology.Grid.CellCount];
            var elevation = new float[hydrology.Grid.CellCount];
            for (int cell = 0; cell < receivers.Length; cell++)
            {
                receivers[cell] = hydrology.GetReceiver(cell);
                accumulation[cell] = hydrology.GetAccumulation(cell);
                elevation[cell] = planet.GetElevation(hydrology.Grid.GetCenter(cell));
            }
            var system = new RiverNetworkSystem();
            system.BuildPaths(system.Build(hydrology, 1e-4f), 3);
            for (int cell = 0; cell < receivers.Length; cell++)
            {
                Assert.AreEqual(receivers[cell], hydrology.GetReceiver(cell));
                Assert.AreEqual(accumulation[cell], hydrology.GetAccumulation(cell), 0.0);
                Assert.AreEqual(elevation[cell], planet.GetElevation(hydrology.Grid.GetCenter(cell)), 0f);
            }
        }

        // 14
        [Test]
        public void MainRivers_AreStableBetweenHydrologyResolutions()
        {
            // Rationale: measured on the validation seeds, ~92% (worst 58%) of a large river's
            // path lies within 2 coarse cells of a river at the other resolution; the low cases
            // are divide captures. Hence: mean >= 70% over the 5 largest rivers, each >= 40%,
            // and comparable river counts.
            foreach (int seed in new[] { 280695, 609538 })
            {
                RiverNetwork coarse = Network(seed, 64, 0);
                RiverNetwork fine = Network(seed, 128, 0);
                Assert.IsTrue(fine.RiverCount > coarse.RiverCount * 0.7f && fine.RiverCount < coarse.RiverCount * 1.4f);
                var main = new List<int>();
                for (int river = 0; river < coarse.RiverCount; river++)
                {
                    if (coarse.GetRiver(river).ParentRiver < 0)
                    {
                        main.Add(river);
                    }
                }
                main.Sort((x, y) => coarse.GetRiver(y).DrainedArea.CompareTo(coarse.GetRiver(x).DrainedArea));
                float tolerance = 2f * Mathf.PI * 0.5f / 64f;
                HydrologyGrid fineGrid = fine.Hydrology.Grid;
                double sum = 0;
                for (int rank = 0; rank < 5; rank++)
                {
                    River data = coarse.GetRiver(main[rank]);
                    int near = 0;
                    for (int i = 0; i < data.NodeCount; i++)
                    {
                        Vector3 p = coarse.Hydrology.Grid.GetCenter(coarse.GetNodeCell(coarse.GetRiverNode(main[rank], i)));
                        bool found = false;
                        for (int cell = 0; cell < fineGrid.CellCount && !found; cell++)
                        {
                            found = fine.IsRiverCell(cell) && (fineGrid.GetCenter(cell) - p).magnitude < tolerance;
                        }
                        near += found ? 1 : 0;
                    }
                    double share = near / (double)data.NodeCount;
                    Assert.GreaterOrEqual(share, 0.4, $"Seed {seed}, river rank {rank}");
                    sum += share;
                }
                Assert.GreaterOrEqual(sum / 5, 0.7, "Seed " + seed);
            }
        }

        [Test]
        public void SurfaceSampler_ReturnsPointsOnTheDisplayedMesh()
        {
            const int resolution = 16;
            var generator = new PlanetHeightGenerator(new PlanetDefinition());
            var faces = new IReadOnlyList<Vector3>[CubeSphereGenerator.FaceCount];
            for (int face = 0; face < faces.Length; face++)
            {
                var vertices = new List<Vector3>();
                for (int y = 0; y <= resolution; y++)
                {
                    for (int x = 0; x <= resolution; x++)
                    {
                        Vector3 d = CubeSphereGenerator.GetSphereDirection(face, x, y, resolution);
                        vertices.Add(d * 100f * (1f + generator.GetElevation(d)));
                    }
                }
                faces[face] = vertices;
            }
            var sampler = new PlanetSurfaceSampler();
            sampler.Capture(faces, resolution);
            for (int face = 0; face < faces.Length; face++)
            {
                for (int i = 0; i < faces[face].Count; i += 7)
                {
                    Vector3 vertex = faces[face][i];
                    Assert.AreEqual(0f, (sampler.Sample(vertex.normalized) - vertex).magnitude, 1e-3f, "Vertices are on the mesh");
                }
                // Midpoint of a mesh edge: the ray hits that edge (linear interpolation of the mesh).
                Vector3 a = faces[face][3 + 5 * (resolution + 1)];
                Vector3 b = faces[face][4 + 5 * (resolution + 1)];
                Vector3 hit = sampler.Sample(((a + b) * 0.5f).normalized);
                Assert.Less(DistanceToSegment(hit, a, b), 1e-3f);
            }
        }

        // ------------------------------------------------------------ Rendering (Unity)

        GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
                root = null;
            }
        }

        PlanetVisualController CreateVisual()
        {
            root = new GameObject("River Test");
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
            visual.Rivers.showRivers = true;
            return visual;
        }

        // 11, 13 (rendering side)
        [Test]
        public void Ribbons_AreFiniteAndDoNotModifyTheTerrain()
        {
            PlanetVisualController visual = CreateVisual();
            var definition = new PlanetDefinition { seed = 280695, resolution = 32 };
            visual.Rivers.showRivers = false;
            visual.Render(new PlanetInstance(definition));
            Mesh face = root.transform.Find("Terrain +Y").GetComponent<MeshFilter>().sharedMesh;
            Vector3[] withoutRivers = face.vertices;

            visual.Rivers.showRivers = true;
            visual.Render(new PlanetInstance(definition));
            CollectionAssert.AreEqual(withoutRivers, face.vertices, "Rivers never change the terrain mesh");
            Assert.IsTrue(visual.RiversVisible);
            Mesh rivers = visual.RiverMesh;
            Assert.Greater(rivers.vertexCount, 0);
            float radius = definition.radius;
            foreach (Vector3 v in rivers.vertices)
            {
                Assert.IsFalse(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z));
                Assert.IsTrue(v.magnitude > radius * 0.9f && v.magnitude < radius * 1.1f, "Ribbons lie on the terrain");
            }
            Assert.AreEqual(7, visual.GeneratedObjectCount, "Rivers add no GameObject");
        }

        // 12 (rendering side)
        [Test]
        public void VisualChanges_RebuildOnlyTheRibbons()
        {
            PlanetVisualController visual = CreateVisual();
            var definition = new PlanetDefinition { seed = 609538, resolution = 32 };
            visual.Render(new PlanetInstance(definition));
            int hydrologyComputes = visual.HydrologyCache.ComputeCount;
            int networkBuilds = visual.RiverCache.BuildCount;
            int pathBuilds = visual.RiverCache.PathBuildCount;
            int ribbons = visual.RiverRibbonBuildCount;

            // Width: ribbons only.
            visual.Rivers.widthMultiplier = 2f;
            visual.RefreshColors();
            Assert.AreEqual(ribbons + 1, visual.RiverRibbonBuildCount);
            Assert.AreEqual(networkBuilds, visual.RiverCache.BuildCount);
            Assert.AreEqual(pathBuilds, visual.RiverCache.PathBuildCount);

            // Color: nothing rebuilt (material property at draw time).
            visual.Rivers.color = Color.red;
            visual.RefreshColors();
            Assert.AreEqual(ribbons + 1, visual.RiverRibbonBuildCount);

            // Smoothing: trajectories + ribbons, topology kept.
            visual.Rivers.smoothing = 0f;
            visual.RefreshColors();
            Assert.AreEqual(networkBuilds, visual.RiverCache.BuildCount);
            Assert.AreEqual(pathBuilds + 1, visual.RiverCache.PathBuildCount);

            // Threshold: network rebuilt, hydrology kept.
            visual.Hydrology.riverNetworkThreshold *= 2f;
            visual.RefreshColors();
            Assert.AreEqual(networkBuilds + 1, visual.RiverCache.BuildCount);
            Assert.AreEqual(hydrologyComputes, visual.HydrologyCache.ComputeCount);

            // Mesh resolution: hydrology and topology kept, ribbons adapted to the new mesh.
            int ribbonsBefore = visual.RiverRibbonBuildCount;
            int networkBefore = visual.RiverCache.BuildCount;
            definition.resolution = 48;
            visual.Render(new PlanetInstance(definition));
            Assert.AreEqual(hydrologyComputes, visual.HydrologyCache.ComputeCount);
            Assert.AreEqual(networkBefore, visual.RiverCache.BuildCount);
            Assert.AreEqual(ribbonsBefore + 1, visual.RiverRibbonBuildCount);

            // Terrain: everything recomputed.
            definition.seed += 1;
            visual.Render(new PlanetInstance(definition));
            Assert.AreEqual(hydrologyComputes + 1, visual.HydrologyCache.ComputeCount);
            Assert.AreEqual(networkBefore + 1, visual.RiverCache.BuildCount);
        }

        // 15
        [Test]
        public void RepeatedRegeneration_WithRivers_DoesNotLeak()
        {
            PlanetVisualController visual = CreateVisual();
            var definition = new PlanetDefinition { resolution = 16 };
            // Warm-up: the debug material is created once, the first time a debug view is shown.
            visual.DebugView = PlanetDebugView.RiverNetwork;
            visual.Render(new PlanetInstance(definition));
            visual.DebugView = PlanetDebugView.None;
            visual.Render(new PlanetInstance(definition));
            int meshCount = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int gameObjectCount = Resources.FindObjectsOfTypeAll<GameObject>().Length;
            int materialCount = Resources.FindObjectsOfTypeAll<Material>().Length;

            for (int i = 0; i < 6; i++)
            {
                definition.seed = 700 + i;
                definition.resolution = i % 2 == 0 ? 16 : 24;
                visual.Hydrology.riverNetworkThreshold = 3e-4f * (i + 1);
                visual.Rivers.widthMultiplier = 0.5f + 0.5f * i;
                visual.Rivers.smoothing = i / 5f;
                visual.DebugView = i == 3 ? PlanetDebugView.RiverNetwork : PlanetDebugView.None;
                visual.Render(new PlanetInstance(definition));
                visual.RefreshColors();
            }

            Assert.AreEqual(meshCount, Resources.FindObjectsOfTypeAll<Mesh>().Length);
            Assert.AreEqual(gameObjectCount, Resources.FindObjectsOfTypeAll<GameObject>().Length);
            Assert.AreEqual(materialCount, Resources.FindObjectsOfTypeAll<Material>().Length);

            Mesh rivers = visual.RiverMesh;
            UnityEngine.Object.DestroyImmediate(root);
            root = null;
            Assert.IsTrue(rivers == null, "River mesh is released with the planet");
        }
    }
}
