using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Persistence;
using Planetwalker.Rendering;
using Planetwalker.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Drainage-guided fluvial incision (V0.2.0G.2B): deterministic, exactly the G.1.1 terrain at strength 0 and on
    /// archived worlds, bounded and finite, downhill-continuous along whole drainage paths, continuous at confluences,
    /// open (no pits, no loops), continuous across cube faces, independent of LOD paths and of the floating origin,
    /// coast-, lake-, plateau- and plain-safe, thread safe, and measured.
    /// </summary>
    public class FluvialIncisionTests
    {
        static readonly Dictionary<(int, float, int), PlanetInstance> Planets = new Dictionary<(int, float, int), PlanetInstance>();
        readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in created) if (o != null) Object.DestroyImmediate(o);
            created.Clear();
        }

        static PlanetInstance Planet(int seed = 1337, float strength = 1f, int model = 1)
        {
            if (!Planets.TryGetValue((seed, strength, model), out PlanetInstance planet))
            {
                planet = new PlanetInstance(new PlanetDefinition { seed = seed, erosionStrength = strength, erosionModel = model });
                Planets[(seed, strength, model)] = planet;
            }
            return planet;
        }

        static IEnumerable<Vector3d> Spiral(int count)
        {
            for (int k = 0; k < count; k++)
            {
                double y = 1.0 - 2.0 * (k + 0.5) / count, r = Math.Sqrt(1.0 - y * y), phi = k * 2.399963229728653;
                yield return new Vector3d(r * Math.Cos(phi), y, r * Math.Sin(phi));
            }
        }

        static float ErosionMeters(PlanetInstance p, Vector3d d)
        {
            p.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
            return (float)(s.Erosion * p.Scale.RadiusMeters);
        }

        /// <summary>Points on the network's channel axes (every n-th segment end), where the incision lives.</summary>
        static IEnumerable<Vector3d> AxisPoints(ValleyNetwork network, int every = 3)
        {
            for (int s = 0; s < network.SegmentCount; s += every)
            {
                yield return network.GetSegment(s).B;
            }
        }

        // 1
        [Test]
        public void Deterministic_AcrossRunsAndFreshContexts()
        {
            foreach (int seed in new[] { 1337, 310929 })
            {
                var a = new PlanetInstance(new PlanetDefinition { seed = seed });
                var b = new PlanetInstance(new PlanetDefinition { seed = seed });
                int incised = 0;
                ValleyNetwork na = a.Height.SurfaceDetail.Landforms.Valleys, nb = b.Height.SurfaceDetail.Landforms.Valleys;
                foreach (Vector3d d in Spiral(6000))
                {
                    Assert.AreEqual(a.Height.GetElevation(d), b.Height.GetElevation(d));
                    Assert.AreEqual(ErosionMeters(a, d), ErosionMeters(b, d));
                }
                foreach (Vector3d d in AxisPoints(na))
                {
                    float ea = ErosionMeters(a, d), eb = ErosionMeters(b, d);
                    Assert.AreEqual(ea, eb);
                    if (ea < 0f) incised++;
                }
                Assert.Greater(incised, 100, $"seed {seed}: the incision exists along the channels");
                for (int s = 0; s < na.SegmentCount; s += 7) Assert.AreEqual(na.GetSegment(s).IncisionB, nb.GetSegment(s).IncisionB);
            }
        }

        // 2, 3
        [Test]
        public void StrengthZero_AndArchivedWorlds_ReproduceG11Exactly()
        {
            PlanetInstance off = Planet(1337, 0f, 1), none = Planet(1337, 1f, 0), g11 = Planet(1337, 0f, 0);
            Assert.IsNull(off.Height.SurfaceDetail.Erosion, "Strength 0: no stage, no work");
            foreach (Vector3d d in Spiral(20000))
            {
                float reference = g11.Height.GetElevation(d);
                Assert.AreEqual(reference, off.Height.GetElevation(d), "Strength 0 = G.1.1");
                Assert.AreEqual(reference, none.Height.GetElevation(d), "Model none (a G.2A world) = G.1.1");
            }
            // Archive written before G.2B (erosionStrength present, erosionModel absent): model none, terrain unchanged.
            PlanetArchive archive = WorldArchiveTestUtil.NewArchive("G.2A world");
            archive.Configuration.Terrain.erosionModel = 1;
            JsonValue root = JsonReader.Parse(PlanetArchiveSerializer.Serialize(archive));
            Assert.IsTrue(root.Find("terrain").Remove("erosionModel"));
            ArchiveReadResult read = PlanetArchiveSerializer.Read(root.ToJson());
            Assert.IsTrue(read.Ok, read.Report.Summary());
            Assert.AreEqual(0, read.Archive.Configuration.Terrain.erosionModel, "A G.2A archive keeps the zero-displacement model");
            Assert.IsEmpty(read.Report.Warnings);
            Assert.AreEqual(GeneratorCompatibility.Exact, read.Compatibility);
            ArchiveReadResult old = PlanetArchiveSerializer.Read(WorldArchiveTestUtil.V013AArchiveJson);
            Assert.AreEqual(0, old.Archive.Configuration.Terrain.erosionModel);
            // The model is archived and honored when present; new worlds use the fluvial model.
            Assert.AreEqual(1, new PlanetDefinition().erosionModel);
            foreach (int model in new[] { 0, 1 })
            {
                PlanetArchive a = WorldArchiveTestUtil.NewArchive("Model " + model);
                a.Configuration.Terrain.erosionModel = model;
                ArchiveReadResult r = PlanetArchiveSerializer.Read(PlanetArchiveSerializer.Serialize(a));
                Assert.AreEqual(model, r.Archive.Configuration.Terrain.erosionModel);
            }
            Assert.IsFalse(new PlanetDefinition { erosionModel = 0 }.TerrainEquals(new PlanetDefinition()), "A terrain setting");
        }

        // 4, 11
        [Test]
        public void Incision_IsBoundedFinite_NeverRaises_KeepsTheCoastline_AndScalesWithStrength()
        {
            foreach (int seed in new[] { 1337, 310929 })
            {
                PlanetInstance on = Planet(seed, 1f), strong = Planet(seed, 2f), off = Planet(seed, 0f);
                double R = on.Scale.RadiusMeters;
                float deepest = 0f;
                int land = 0, incised = 0;
                foreach (Vector3d d in Spiral(60000))
                {
                    float eOff = off.Height.GetElevation(d), eOn = on.Height.GetElevation(d);
                    bool wasLand = eOff > off.SeaLevel;
                    Assert.AreEqual(wasLand, eOn > on.SeaLevel, "Land / sea unchanged");
                    Assert.AreEqual(wasLand, strong.Height.GetElevation(d) > strong.SeaLevel, "Land / sea unchanged at strength 2");
                    on.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                    Assert.IsTrue(float.IsFinite(s.Erosion));
                    Assert.LessOrEqual(s.Erosion, 0f, "Incision only, never a rise");
                    if (!wasLand)
                    {
                        Assert.AreEqual(0f, s.Erosion, "Nothing at sea");
                        continue;
                    }
                    land++;
                    float e0 = on.Height.GetBaseElevation(d.ToVector3());
                    float shaped = e0 + s.Landform - on.SeaLevel;
                    Assert.GreaterOrEqual(s.Erosion, -shaped * 0.35f - 1e-9f, "At most 35 % of the landform-shaped height");
                    Assert.GreaterOrEqual(-s.Erosion * R, 0.0);
                    Assert.LessOrEqual(-s.Erosion * R, ValleyNetwork.MaxIncisionMeters * 1.13, "Bounded by the deepest node incision (soft max)");
                    deepest = Mathf.Max(deepest, (float)(-s.Erosion * R));
                    if (s.Erosion < 0f)
                    {
                        incised++;
                        strong.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s2);
                        Assert.LessOrEqual(s2.Erosion, s.Erosion, "Strength 2 incises at least as much");
                        Assert.GreaterOrEqual(s2.Erosion, 2f * s.Erosion - 1e-9f, "...and at most twice (the height bound)");
                    }
                }
                int axisIncised = 0;
                foreach (Vector3d d in AxisPoints(on.Height.SurfaceDetail.Landforms.Valleys))
                {
                    on.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                    if (s.Erosion < 0f) axisIncised++;
                    deepest = Mathf.Max(deepest, (float)(-s.Erosion * R));
                }
                TestContext.WriteLine($"seed {seed}: {incised} of {land} scattered land samples incised, {axisIncised} channel points incised, deepest {deepest:0} m");
                Assert.Greater(axisIncised, 100);
                Assert.Greater(deepest, 300f, "A visible incision");
                Assert.AreEqual(off.LandFraction, on.LandFraction, "Same shoreline");
            }
        }

        // 5, 6: whole drainage paths, upstream to downstream, on the final terrain.
        [Test]
        public void ChannelFloors_StayDownhillAlongWholePaths_AndContinuousAtConfluences()
        {
            PlanetInstance on = Planet(1337, 1f), off = Planet(1337, 0f);
            ValleyNetwork network = on.Height.SurfaceDetail.Landforms.Valleys;
            HydrologyResult h = network.Hydrology;
            double R = on.Scale.RadiusMeters;
            int paths = 0, nodes = 0, newUphill = 0, confluences = 0;
            float worstIncisionStep = 0f, worstConfluenceStep = 0f;
            var upstreamCount = new int[h.Grid.CellCount];
            for (int cell = 0; cell < h.Grid.CellCount; cell++) { int r = h.GetReceiver(cell); if (r >= 0) upstreamCount[r]++; }
            // Start at channel heads (strong incision somewhere on the path) and follow the receivers to the outlet.
            for (int s = 0; s < network.SegmentCount; s += 3)
            {
                ValleyNetwork.Segment g = network.GetSegment(s);
                if (g.IncisionB < 200f) continue;
                int cell = g.Cell;
                var profile = new List<(Vector3d d, float before, float after, float incision)>();
                int steps = 0;
                while (cell >= 0 && h.IsLand(cell) && steps++ < 400)
                {
                    var d = new Vector3d(h.Grid.GetCenter(cell));
                    // The channel axis near the node: the network's own closest point (meander included).
                    if (network.Nearest(d, out ValleyNetwork.ChannelQuery q) && q.DistanceMeters < q.IncisionHalfWidthMeters)
                    {
                        Vector3d axis = q.Closest;
                        float before = off.Height.GetElevation(axis), after = on.Height.GetElevation(axis);
                        profile.Add((axis, before, after, ErosionMeters(on, axis)));
                    }
                    cell = h.GetReceiver(cell);
                }
                if (profile.Count < 4) continue;
                paths++;
                for (int i = 1; i < profile.Count; i++)
                {
                    nodes++;
                    // No new uphill step: where the pre-erosion floor descends, the eroded floor descends too (within 2 % of the drop).
                    float dropBefore = profile[i - 1].before - profile[i].before;
                    float dropAfter = profile[i - 1].after - profile[i].after;
                    if (dropBefore > 0f && dropAfter < -0.02f * dropBefore) newUphill++;
                    // Incision varies smoothly along the network: at most 450 m between consecutive nodes (~78 km apart).
                    worstIncisionStep = Mathf.Max(worstIncisionStep, Mathf.Abs(profile[i].incision - profile[i - 1].incision));
                }
            }
            // Confluences: the incision just upstream on a tributary vs just downstream on the trunk.
            for (int cell = 0; cell < h.Grid.CellCount; cell++)
            {
                if (upstreamCount[cell] < 2 || !h.IsLand(cell)) continue;
                var d = new Vector3d(h.Grid.GetCenter(cell));
                if (!network.Nearest(d, out ValleyNetwork.ChannelQuery q) || q.IncisionMeters < 100f) continue;
                confluences++;
                float at = ErosionMeters(on, q.Closest);
                int receiver = h.GetReceiver(cell);
                if (receiver < 0 || !h.IsLand(receiver)) continue;
                var dr = new Vector3d(h.Grid.GetCenter(receiver));
                if (!network.Nearest(dr, out ValleyNetwork.ChannelQuery qr)) continue;
                float downstream = ErosionMeters(on, qr.Closest);
                // The trunk right below a confluence is incised at least 85 % as deep as the junction (downstream regularization).
                worstConfluenceStep = Mathf.Max(worstConfluenceStep, Mathf.Max(0f, (-at) * 0.85f - (-downstream)));
            }
            TestContext.WriteLine($"{paths} paths, {nodes} node steps, new uphill steps {newUphill}, worst incision step {worstIncisionStep:0} m, {confluences} confluences (terrain-level shortfall {worstConfluenceStep:0} m, informative: the valley region fades out of the ranges)");
            Assert.Greater(paths, 20);
            Assert.AreEqual(0, newUphill, "The incision never turns a descending channel floor into an ascending one");
            Assert.Less(worstIncisionStep, 1000f, "Smooth along the network: under 1 km of change per ~78 km node step (a 1.3 % grade)");
            Assert.Greater(confluences, 10);
            // The downstream regularization, read on the network itself: a trunk node right below a confluence carries at
            // least 85 % of the junction's incision (the terrain then applies the same height bound and region to both).
            int junctions = 0; float worstNode = 0f;
            var seenCells = new HashSet<int>();
            for (int s = 0; s < network.SegmentCount; s++)
            {
                int cell = network.GetSegment(s).Cell;
                if (!seenCells.Add(cell) || upstreamCount[cell] < 2 || network.NodeIncision(cell) < 100f) continue;
                int receiver = h.GetReceiver(cell);
                if (receiver < 0 || !h.IsLand(receiver) || network.NodeIncision(receiver) <= 0f) continue; // the channel leaves the ranges there
                junctions++;
                worstNode = Mathf.Max(worstNode, 0.85f * network.NodeIncision(cell) - network.NodeIncision(receiver));
            }
            TestContext.WriteLine($"{junctions} junction -> trunk node pairs, worst shortfall {worstNode:0} m");
            Assert.Greater(junctions, 10);
            Assert.Less(worstNode, 1f, "A trunk is at least 85 % as deep as the tributary joining it");
        }

        // 7: incised floors are elongated corridors (as in LandformTests), never isolated pits or rings.
        [Test]
        public void IncisedFloors_AreOpenCorridors()
        {
            PlanetInstance planet = Planet(310929, 1f);
            ValleyNetwork network = planet.Height.SurfaceDetail.Landforms.Valleys;
            double R = planet.Scale.RadiusMeters;
            int floors = 0, open = 0;
            foreach (Vector3d d0 in AxisPoints(network, 1))
            {
                if (!network.Nearest(d0, out ValleyNetwork.ChannelQuery q) || q.IncisionMeters < 100f) continue;
                Vector3d d = q.Closest;
                network.Incision(d, 0f, out float channel);
                if (channel < 0.9f) continue;
                floors++;
                // A ring at 1.5 x the local incision half width: an open corridor crosses it on two sides, a pit or a
                // ring would leave all or none of it incised.
                double ring = 1.5 * q.IncisionHalfWidthMeters / R;
                Vector3d north = PlanetTangentFrame.At(d).North, east = Vector3d.Cross(north, d).Normalized;
                int inside = 0;
                for (int k = 0; k < 16; k++)
                {
                    double a = k * Math.PI / 8.0;
                    Vector3d p = (d + (north * Math.Cos(a) + east * Math.Sin(a)) * ring).Normalized;
                    network.Incision(p, 0f, out float c);
                    if (c > 0.3f) inside++;
                }
                if (inside >= 1 && inside <= 12) open++;
            }
            TestContext.WriteLine($"{floors} incised floor samples, {open} on open corridors");
            Assert.Greater(floors, 30);
            Assert.Greater(open, floors * 0.9, "Incised floors continue along a path and are bounded on the sides");
        }

        // 8
        [Test]
        public void ContinuousAcrossCubeFaceEdges()
        {
            PlanetInstance planet = Planet(1337, 1f);
            double R = planet.Scale.RadiusMeters;
            EdgeContinuity.Result r = EdgeContinuity.Probe(d =>
            {
                planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                return new[] { planet.Height.GetElevation(d) * (float)R, (float)(s.Erosion * R) };
            }, 300);
            TestContext.WriteLine($"{r.Pairs} pairs: worst 20 m {r.Worst20m:0.0000}, 2 m {r.Worst2m:0.0000}, 0.2 m {r.Worst02m:0.0000} at {r.WorstAt}");
            Assert.Less(r.Worst02m, Mathf.Max(0.05f, r.Worst20m * 0.05f), "No seam: the difference shrinks with the separation");
        }

        // 9, 10
        [Test]
        public void LodPaths_AndFloatingOriginRebase_SeeTheSameIncision()
        {
            PlanetInstance planet = Planet(1337, 1f);
            var snapshot = new AdaptiveSurfaceSnapshot(1, planet, null, PlanetColorRamp.CreateDefault(), true, 16);
            var builder = new AdaptivePatchGeometryBuilder();
            var data = new PatchGeometryData(snapshot.Topology.VertexCount);
            int shared = 0;
            foreach (Vector3d d in AxisPoints(planet.Height.SurfaceDetail.Landforms.Valleys, 1))
            {
                if (ErosionMeters(planet, d) > -100f) continue;
                PlanetPatchId fine = CubeSphereCoordinates.PatchAt(d.ToVector3(), 14);
                builder.Build(snapshot, fine, data);
                Vector3 reference = data.Origin + data.Vertices[0];
                for (int level = 10; level <= 13; level++)
                {
                    int shift = 14 - level;
                    if ((fine.X & ((1 << shift) - 1)) != 0 || (fine.Y & ((1 << shift) - 1)) != 0) continue;
                    builder.Build(snapshot, new PlanetPatchId(fine.Face, level, fine.X >> shift, fine.Y >> shift), data);
                    Assert.Less(((data.Origin + data.Vertices[0]) - reference).magnitude, planet.Radius * 2e-7f, $"level {level} vs 14 on an incised point");
                    shared++;
                }
                if (shared > 12) break;
            }
            Assert.Greater(shared, 0);
            // Coarse sampling fades the incision, never deepens it (band limit), and the LOD margin counts it.
            ErosionSampler erosion = planet.Height.SurfaceDetail.Erosion;
            Assert.AreEqual(0f, erosion.UnresolvedAmplitude(0f));
            Assert.Greater(erosion.UnresolvedAmplitude(1e-3f), 0f);
            Assert.GreaterOrEqual(planet.Height.SurfaceDetail.UnresolvedAmplitude(1e-3f), erosion.UnresolvedAmplitude(1e-3f));
            foreach (Vector3d d in Spiral(3000))
            {
                planet.Height.GetElevation(d, 0f, out TerrainBaseSample ctx);
                planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                erosion.Evaluate(d, d.ToVector3(), ctx, s.Landforms, 0f, out ErosionSample full);
                erosion.Evaluate(d, d.ToVector3(), ctx, s.Landforms, 0.01f, out ErosionSample coarse);
                Assert.GreaterOrEqual(coarse.Displacement, full.Displacement - 1e-9f, "Faded, not deepened");
                Assert.AreEqual(0f, coarse.Displacement, "Absent at the legacy spacing (hydrology, lakes and climate never see it)");
            }
            // Floating origin: a rebase never changes planet-space values.
            var host = new WorldTestHost(created);
            WorldConfiguration c = WorldArchiveTestUtil.Tweaked(1337, flipFlags: false);
            c.Terrain.resolution = 16;
            c.Terrain.erosionStrength = 1f;
            c.Terrain.erosionModel = 1;
            host.Apply(c);
            host.Definition.seed = 1337;
            host.Regenerate();
            PlanetRenderFrame frame = PlanetRenderFrame.GetOrCreate(host.Root.transform);
            PlanetInstance p = host.Current;
            var probes = new List<Vector3d>(Spiral(500));
            var before = probes.ConvertAll(d => (p.Height.GetElevation(d), ErosionMeters(p, d)));
            foreach (Vector3d origin in new[] { probes[3] * p.Radius * 1.001, probes[200] * p.Radius * 1.0001, Vector3d.Zero })
            {
                frame.RebaseNow(origin);
                for (int i = 0; i < probes.Count; i++)
                {
                    Assert.AreEqual(before[i].Item1, p.Height.GetElevation(probes[i]));
                    Assert.AreEqual(before[i].Item2, ErosionMeters(p, probes[i]));
                }
            }
        }

        // 11, 12: lakes, plateaus and coastal plains.
        [Test]
        public void Plateaus_CoastalPlains_AndTheLegacyWorld_AreUntouched()
        {
            PlanetInstance on = Planet(310929, 1f), off = Planet(310929, 0f);
            int plateau = 0, plain = 0;
            foreach (Vector3d d in Spiral(60000))
            {
                on.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                if (s.Landforms.Plateau > 0.9f) { plateau++; Assert.AreEqual(0f, s.Erosion, "No incision on plateau interiors"); }
                if (s.Landforms.CoastalPlain > 0.9f) { plain++; Assert.AreEqual(0f, s.Erosion, "No incision on coastal plains"); }
                // The legacy world (156 km spacing): the lakes, rivers and climate are computed from it, unchanged.
                Assert.AreEqual(off.GetLegacyElevation(d.ToVector3()), on.GetLegacyElevation(d.ToVector3()), "Legacy (hydrology) surface unchanged");
            }
            Assert.Greater(plateau, 200);
            Assert.Greater(plain, 200);
            // Hence the same lake and basin analysis as G.1.1.
            var grid = new HydrologyGrid(64);
            HydrologyResult a = new HydrologySystem().Compute(grid, on), b = new HydrologySystem().Compute(grid, off);
            Assert.AreEqual(b.BasinCount, a.BasinCount);
            Assert.AreEqual(b.DepressionCount, a.DepressionCount);
            for (int cell = 0; cell < grid.CellCount; cell += 5) Assert.AreEqual(b.GetReceiver(cell), a.GetReceiver(cell));
        }

        // 13
        [Test]
        public void ConcurrentSampling_MatchesSequential()
        {
            PlanetInstance planet = Planet(1337, 1f);
            var probes = new List<Vector3d>(Spiral(20000));
            var sequential = probes.ConvertAll(d => planet.Height.GetElevation(d));
            var parallel = new float[probes.Count];
            Parallel.For(0, probes.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => parallel[i] = planet.Height.GetElevation(probes[i]));
            for (int i = 0; i < probes.Count; i++) Assert.AreEqual(sequential[i], parallel[i]);
            ValleyNetwork network = planet.Height.SurfaceDetail.Landforms.Valleys;
            var found = new bool[probes.Count];
            var dist = new float[probes.Count];
            Parallel.For(0, probes.Count, i => { found[i] = network.Nearest(probes[i], out ValleyNetwork.ChannelQuery q); dist[i] = q.DistanceMeters; });
            for (int i = 0; i < probes.Count; i += 13)
            {
                bool f = network.Nearest(probes[i], out ValleyNetwork.ChannelQuery q);
                Assert.AreEqual(f, found[i]);
                if (f) Assert.AreEqual(q.DistanceMeters, dist[i]);
            }
        }

        // 14
        [Test]
        public void Cost_IsMeasured()
        {
            var probes = new List<Vector3d>(Spiral(100000));
            double Time(Func<Vector3d, float> f)
            {
                float sink = 0f;
                foreach (Vector3d d in probes) sink += f(d);
                var sw = Stopwatch.StartNew();
                for (int rep = 0; rep < 3; rep++) foreach (Vector3d d in probes) sink += f(d);
                Assert.IsTrue(float.IsFinite(sink));
                return sw.Elapsed.TotalMilliseconds * 1000.0 / (3 * probes.Count);
            }
            PlanetInstance off = Planet(1337, 0f), on = Planet(1337, 1f);
            ValleyNetwork network = on.Height.SurfaceDetail.Landforms.Valleys;
            double offUs = Math.Min(Time(d => off.Height.GetElevation(d)), Time(d => off.Height.GetElevation(d)));
            double onUs = Math.Min(Time(d => on.Height.GetElevation(d)), Time(d => on.Height.GetElevation(d)));
            double queryUs = Time(d => network.Nearest(d, out ValleyNetwork.ChannelQuery q) ? q.DistanceMeters : 0f);
            double incisionUs = Time(d => network.Incision(d, 0f, out _));
            var sw = Stopwatch.StartNew();
            var fresh = new PlanetInstance(new PlanetDefinition { seed = 1337 });
            double planetMs = sw.Elapsed.TotalMilliseconds;
            TestContext.WriteLine($"canonical elevation: {offUs:0.00} us without erosion, {onUs:0.00} us with the fluvial incision ({(onUs - offUs) / offUs * 100.0:+0.0;-0.0} %); channel query {queryUs:0.00} us; incision field {incisionUs:0.00} us; planet construction {planetMs:0} ms (network {fresh.Height.SurfaceDetail.Landforms.Valleys.BuildMilliseconds:0} ms, {network.SegmentCount} segments)");
            Assert.Less(onUs, offUs * 2.0 + 2.0, "Loose bound: the incision is a hash lookup and a few segments, not a benchmark");
        }
    }
}
