using System;
using System.Collections.Generic;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using Planetwalker.Terrain;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Regional landform identity (V0.2.0G.1): determinism, coastline preservation, continuity across cube faces and
    /// along the surface (no hard region boundary), patch / LOD consistency, finite values, the three landforms present
    /// on a real planet, bounded shaping, and the exact V0.2.0F.3 terrain when off.
    /// </summary>
    public class LandformTests
    {
        const int Seed = 310929;
        static readonly Dictionary<(int, float), PlanetInstance> Planets = new Dictionary<(int, float), PlanetInstance>();

        static PlanetInstance Planet(float landforms = 1f, int seed = Seed)
        {
            if (!Planets.TryGetValue((seed, landforms), out PlanetInstance planet))
            {
                planet = new PlanetInstance(new PlanetDefinition { seed = seed, regionalLandforms = landforms });
                Planets[(seed, landforms)] = planet;
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

        static double Meters(PlanetInstance p, float elevation) => elevation * p.Scale.RadiusMeters;

        [Test]
        public void Off_BuildsNoLandforms_AndOnIsTheDefaultForNewWorlds()
        {
            Assert.AreEqual(1f, new PlanetDefinition().regionalLandforms);
            Assert.IsNull(Planet(0f).Height.SurfaceDetail.Landforms, "Strength 0: no landform layer (the V0.2.0F.3 terrain)");
            Assert.IsNotNull(Planet(1f).Height.SurfaceDetail.Landforms);
            foreach (Vector3d d in Spiral(2000))
            {
                Planet(0f).Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                Assert.AreEqual(0f, s.Landform);
                Assert.AreEqual(s.Regional + s.Local + s.Micro, s.Total, "Off: the total is the three bands, as before");
            }
            Assert.IsFalse(new PlanetDefinition { regionalLandforms = 0f }.TerrainEquals(new PlanetDefinition()), "A terrain setting (hydrology caches follow it)");
        }

        [Test]
        public void Deterministic_ForAFixedSeedAndDirection()
        {
            var a = new PlanetInstance(new PlanetDefinition { seed = Seed });
            var b = new PlanetInstance(new PlanetDefinition { seed = Seed });
            foreach (Vector3d d in Spiral(3000))
            {
                Assert.AreEqual(a.Height.GetElevation(d), b.Height.GetElevation(d));
                Assert.AreEqual(a.Height.GetElevation(d, 1e-4f), b.Height.GetElevation(d, 1e-4f));
                a.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample sa);
                b.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample sb);
                Assert.AreEqual(sa.Landforms.Displacement, sb.Landforms.Displacement);
                Assert.AreEqual(sa.Landforms.Valley, sb.Landforms.Valley);
                Assert.AreEqual(sa.Landforms.Plateau, sb.Landforms.Plateau);
                Assert.AreEqual(sa.Landforms.CoastalPlain, sb.Landforms.CoastalPlain);
            }
            // Evaluation order does not matter (no state): the same samples in reverse.
            var forward = new List<float>();
            foreach (Vector3d d in Spiral(500)) forward.Add(a.Height.GetElevation(d));
            var list = new List<Vector3d>(Spiral(500));
            for (int i = list.Count - 1; i >= 0; i--) Assert.AreEqual(forward[i], a.Height.GetElevation(list[i]));
        }

        [Test]
        public void TheCoastlineIsPreserved_NoLandSeaChange_AndShapingIsBounded()
        {
            PlanetInstance on = Planet(1f), off = Planet(0f), strong = Planet(2f);
            int land = 0;
            foreach (Vector3d d in Spiral(60000))
            {
                float eOff = off.Height.GetElevation(d);
                bool wasLand = eOff > off.SeaLevel;
                Assert.AreEqual(wasLand, on.Height.GetElevation(d) > on.SeaLevel, $"{d.ToVector3()}: land / sea unchanged");
                Assert.AreEqual(wasLand, strong.Height.GetElevation(d) > strong.SeaLevel, $"{d.ToVector3()}: land / sea unchanged at strength 2");
                on.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                float e0 = on.Height.GetBaseElevation(d.ToVector3());
                if (e0 <= on.SeaLevel)
                {
                    Assert.AreEqual(0f, s.Landform, "Nothing under the sea");
                    continue;
                }
                land++;
                float h = e0 - on.SeaLevel;
                Assert.Greater(h + s.Landform, 0.01f * h, "Landforms never push land under the sea, nor flatten it to the sea level");
                Assert.Less(Meters(on, s.Landform), 7000.0, "Bounded uplift");
            }
            Assert.Greater(land, 10000);
        }

        [Test]
        public void ContinuousAcrossCubeFaceEdges()
        {
            PlanetInstance planet = Planet();
            double R = planet.Scale.RadiusMeters;
            EdgeContinuity.Result r = EdgeContinuity.Probe(d =>
            {
                float e = planet.Height.GetElevation(d);
                planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                return new[] { (float)(e * R), (float)(s.Landform * R), s.Landforms.Valley, s.Landforms.Plateau, s.Landforms.CoastalPlain };
            }, 300);
            TestContext.WriteLine($"{r.Pairs} pairs: worst 20 m {r.Worst20m:0.0000}, 2 m {r.Worst2m:0.0000}, 0.2 m {r.Worst02m:0.0000} at {r.WorstAt}");
            Assert.Less(r.Worst02m, Mathf.Max(0.05f, r.Worst20m * 0.05f), "The difference shrinks with the separation: no seam (meters and weights)");
        }

        [Test]
        public void ContinuousAlongTheSurface_NoHardRegionBoundaries()
        {
            PlanetInstance planet = Planet();
            double R = planet.Scale.RadiusMeters;
            const double stepMeters = 25.0;
            int paths = 0;
            float worstWeight = 0f, worstShift = 0f;
            foreach (Vector3d start in Spiral(400))
            {
                if (planet.Height.GetElevation(start) <= planet.SeaLevel) continue;
                paths++;
                Vector3d north = PlanetTangentFrame.At(start).North;
                Vector3d d = start;
                bool previousLand = planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample previous) > planet.SeaLevel;
                for (int i = 0; i < 400; i++) // 10 km
                {
                    d = (d + north * (stepMeters / R)).Normalized;
                    north = PlanetTangentFrame.At(d).North;
                    float e0 = planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                    bool land = e0 > planet.SeaLevel;
                    if (!land || !previousLand)
                    {
                        // No landform under the sea, by definition: the coastline itself is checked elsewhere.
                        previous = s;
                        previousLand = land;
                        continue;
                    }
                    LandformSample a = previous.Landforms, b = s.Landforms;
                    worstWeight = Mathf.Max(worstWeight, Mathf.Max(Mathf.Abs(a.Valley - b.Valley), Mathf.Max(Mathf.Abs(a.Plateau - b.Plateau), Mathf.Abs(a.CoastalPlain - b.CoastalPlain))));
                    worstShift = Mathf.Max(worstShift, (float)(Math.Abs(s.Landform - previous.Landform) * R));
                    previous = s;
                }
            }
            TestContext.WriteLine($"{paths} paths of 10 km: worst weight step {worstWeight:0.0000}, worst shift step {worstShift:0.00} m per {stepMeters} m");
            Assert.Greater(paths, 100);
            Assert.Less(worstWeight, 0.05f, "Influence weights change gradually (no hard region boundary)");
            Assert.Less(worstShift, (float)(stepMeters * 2.0), "Landform slopes stay below ~63 degrees (no cliff jump)");
        }

        [Test]
        public void ValuesAndNormalsAreFinite()
        {
            PlanetInstance planet = Planet(2f);
            foreach (Vector3d d in Spiral(20000))
            {
                float e = planet.Height.GetElevation(d);
                Assert.IsTrue(float.IsFinite(e));
                planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                LandformSample l = s.Landforms;
                Assert.IsTrue(float.IsFinite(l.Displacement) && float.IsFinite(l.Calm) && float.IsFinite(l.Edge));
                foreach (float w in new[] { l.Valley, l.Plateau, l.CoastalPlain, l.Corridor, l.Calm, l.Edge })
                {
                    Assert.IsTrue(w >= 0f && w <= 1f, $"Weight in [0, 1]: {w}");
                }
            }
            foreach (Vector3d d in Spiral(2000))
            {
                foreach (float angle in new[] { 1e-6f, 1e-4f, 1e-2f })
                {
                    Vector3 n = planet.Height.GetSurfaceNormal(d, angle);
                    Assert.IsTrue(float.IsFinite(n.x) && float.IsFinite(n.y) && float.IsFinite(n.z));
                    Assert.AreEqual(1f, n.magnitude, 1e-3f);
                    Assert.Greater(Vector3.Dot(n, d.ToVector3()), 0f, "Outward");
                }
            }
        }

        /// <summary>The three landforms exist on a real planet, with the expected shape signatures.</summary>
        [Test]
        public void TheThreeLandformsArePresent_OnTheReferenceSeed()
        {
            PlanetInstance planet = Planet();
            double R = planet.Scale.RadiusMeters;
            float deepestValley = 0f, highestPlateau = 0f, lowestPlain = float.MaxValue;
            int plain = 0, plateau = 0, valley = 0;
            foreach (Vector3d d in Spiral(80000))
            {
                float e0 = planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                if (e0 <= planet.SeaLevel) continue;
                LandformSample l = s.Landforms;
                double shift = l.Displacement * R;
                if (l.Corridor > 0.9f) { valley++; deepestValley = Mathf.Max(deepestValley, (float)-shift); }
                if (l.Plateau > 0.9f) { plateau++; highestPlateau = Mathf.Max(highestPlateau, (float)shift); }
                if (l.CoastalPlain > 0.9f) { plain++; lowestPlain = Mathf.Min(lowestPlain, (float)((e0 + s.Total - planet.SeaLevel) * R)); }
            }
            TestContext.WriteLine($"valley floors {valley} (deepest {deepestValley:0} m), plateau interiors {plateau} (uplift up to {highestPlateau:0} m), coastal plains {plain} (lowest {lowestPlain:0} m)");
            Assert.Greater(valley, 20, "Valley corridors");
            Assert.Greater(deepestValley, 500f, "Valleys are carved hundreds of meters");
            Assert.Greater(plateau, 200, "Plateau interiors");
            Assert.Greater(highestPlateau, 1000f, "Plateaus are raised");
            Assert.Greater(plain, 200, "Coastal plains");
            Assert.Less(lowestPlain, 100f, "Coastal plains reach the sea at low height");
        }

        /// <summary>
        /// The shape does not depend on the patch or level: a lattice point shared by patches of several levels gets the
        /// very same position from each, and the band-limited elevation of a coarse level converges to the full one.
        /// </summary>
        [Test]
        public void PatchesOfEveryLevel_ShareTheSameSurface()
        {
            PlanetInstance planet = Planet();
            var snapshot = new AdaptiveSurfaceSnapshot(1, planet, null, PlanetColorRamp.CreateDefault(), true, 16);
            var builder = new AdaptivePatchGeometryBuilder();
            var data = new PatchGeometryData(snapshot.Topology.VertexCount);
            int checkedPoints = 0;
            foreach (Vector3d d in Spiral(400))
            {
                planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                if (s.Landforms.Valley + s.Landforms.Plateau + s.Landforms.CoastalPlain < 0.3f) continue;
                // The corner (0, 0) of the level-14 patch is a lattice point of every coarser ancestor too.
                PlanetPatchId fine = CubeSphereCoordinates.PatchAt(d.ToVector3(), 14);
                builder.Build(snapshot, fine, data);
                Vector3 reference = data.Origin + data.Vertices[0];
                for (int level = 10; level <= 13; level++)
                {
                    int shift = 14 - level;
                    if ((fine.X & ((1 << shift) - 1)) != 0 || (fine.Y & ((1 << shift) - 1)) != 0) continue; // not a corner of that ancestor
                    var coarse = new PlanetPatchId(fine.Face, level, fine.X >> shift, fine.Y >> shift);
                    builder.Build(snapshot, coarse, data);
                    Vector3 position = data.Origin + data.Vertices[0];
                    Assert.Less((position - reference).magnitude, planet.Radius * 2e-7f, $"{fine} vs level {level}: the same point");
                    checkedPoints++;
                }
                // Same direction, any patch: the canonical function itself.
                Vector3d corner = CubeSphereCoordinates.LatticeDirectionD(fine.Face, fine.X * 16, fine.Y * 16, 16 << 14);
                Assert.AreEqual(planet.Height.GetElevation(corner), planet.Height.GetElevation(corner, 0f));
            }
            TestContext.WriteLine($"{checkedPoints} shared lattice points");
        }

        [Test]
        public void BandLimitedSamples_FadeTheNarrowValleys_AndTheLodMarginCoversThem()
        {
            PlanetInstance planet = Planet();
            RegionalLandforms landforms = planet.Height.SurfaceDetail.Landforms;
            Assert.AreEqual(0f, landforms.UnresolvedValleyDepth(0f));
            Assert.AreEqual(landforms.MaxValleyDepth, landforms.UnresolvedValleyDepth(0.01f), 1e-9f, "A 64 km spacing resolves no valley");
            float previous = -1f;
            foreach (float spacing in new[] { 1e-6f, 1e-5f, 5e-5f, 1e-4f, 3e-4f, 1e-3f })
            {
                float u = landforms.UnresolvedValleyDepth(spacing);
                Assert.GreaterOrEqual(u, previous, "Coarser sampling misses more");
                previous = u;
            }
            Assert.GreaterOrEqual(planet.Height.SurfaceDetail.UnresolvedAmplitude(1e-3f),
                planet.Height.SurfaceDetail.Profile.UnresolvedAmplitude(1e-3f) + landforms.UnresolvedValleyDepth(1e-3f) - 1e-9f,
                "The LOD error margin counts the valleys");
            // Coarse sampling over a valley floor: the corridor fades, it never deepens.
            foreach (Vector3d d in Spiral(40000))
            {
                planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                if (s.Landforms.Corridor < 0.8f) continue;
                float full = planet.Height.GetElevation(d, 0f);
                float coarse = planet.Height.GetElevation(d, 0.01f);
                planet.Height.GetElevation(d, 0.01f, out TerrainBaseSample context);
                landforms.Evaluate(d, d.ToVector3(), context, 0.01f, out LandformSample coarseSample);
                landforms.Evaluate(d, d.ToVector3(), context, 0f, out LandformSample fullSample);
                Assert.GreaterOrEqual(coarseSample.Displacement, fullSample.Displacement - 1e-7f, "Faded, not deepened");
                Assert.IsTrue(float.IsFinite(full) && float.IsFinite(coarse));
            }
        }

        // ------------------------------------------------------------------ V0.2.0G.1.1: drainage valley network

        static ValleyNetwork Network(int seed = 1337) => Planet(1f, seed).Height.SurfaceDetail.Landforms.Valleys;

        /// <summary>
        /// The confirmed V0.2.0G.1 failure: corridors on the zero lines of a noise, closed rings by construction. The
        /// network's segments must form a forest: joining their endpoints never closes a cycle (union-find).
        /// </summary>
        [Test]
        public void ValleyNetwork_HasNoClosedLoop_ItIsAForestOfOpenPaths()
        {
            foreach (int seed in new[] { 1337, Seed })
            {
                ValleyNetwork network = Network(seed);
                Assert.Greater(network.SegmentCount, 100, $"seed {seed}: a network exists");
                var ids = new Dictionary<(long, long, long), int>();
                var parent = new List<int>();
                int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                int Id(Vector3d p)
                {
                    var key = ((long)Math.Round(p.x * 1e7), (long)Math.Round(p.y * 1e7), (long)Math.Round(p.z * 1e7));
                    if (!ids.TryGetValue(key, out int id)) { id = parent.Count; ids[key] = id; parent.Add(id); }
                    return id;
                }
                int cycles = 0, edges = 0;
                var seen = new HashSet<(int, int)>();
                for (int s = 0; s < network.SegmentCount; s++)
                {
                    ValleyNetwork.Segment g = network.GetSegment(s);
                    int a = Id(g.A), b = Id(g.B);
                    if (a == b || !seen.Add((Math.Min(a, b), Math.Max(a, b)))) continue; // the same edge twice (shared curve ends)
                    edges++;
                    int ra = Find(a), rb = Find(b);
                    if (ra == rb) cycles++; else parent[ra] = rb;
                }
                TestContext.WriteLine($"seed {seed}: {network.ChannelCount} channel cells, {network.SegmentCount} segments, {edges} distinct edges, built in {network.BuildMilliseconds:0} ms");
                Assert.AreEqual(0, cycles, $"seed {seed}: no closed loop in the valley network");
            }
        }

        [Test]
        public void ValleyNetwork_RunsDownhill_AndWidensDownstream()
        {
            ValleyNetwork network = Network();
            HydrologyResult h = network.Hydrology;
            int uphill = 0;
            for (int s = 0; s < network.SegmentCount; s++)
            {
                ValleyNetwork.Segment g = network.GetSegment(s);
                if (g.FilledB > g.FilledA + 1e-9) uphill++;
                Assert.Greater(g.HalfWidthA, 0f);
                Assert.IsTrue(float.IsFinite(g.DepthA) && float.IsFinite(g.DepthB) && g.DepthA >= 0f && g.DepthB >= 0f);
            }
            Assert.AreEqual(0, uphill, "Every segment descends on the drained (filled) surface");
            // Every channel path reaches an outlet in finite steps (no cycle in the receivers) and drains more downstream.
            int count = h.Grid.CellCount, narrowing = 0, paths = 0;
            for (int cell = 0; cell < count; cell += 7)
            {
                if (!h.IsLand(cell) || h.GetAccumulation(cell) <= 0.0) continue;
                paths++;
                int c = cell, steps = 0;
                while (h.GetReceiver(c) >= 0 && h.IsLand(h.GetReceiver(c)))
                {
                    int next = h.GetReceiver(c);
                    if (h.GetAccumulation(next) < h.GetAccumulation(c)) narrowing++;
                    c = next;
                    Assert.Less(++steps, count, "A drainage path ends");
                }
            }
            Assert.Greater(paths, 1000);
            Assert.AreEqual(0, narrowing, "The drained area grows downstream: valleys widen toward the trunk");
        }

        [Test]
        public void ValleyNetwork_IsDeterministic_AndBuiltWithinBounds()
        {
            var a = new PlanetInstance(new PlanetDefinition { seed = 1337 }).Height.SurfaceDetail.Landforms.Valleys;
            var b = new PlanetInstance(new PlanetDefinition { seed = 1337 }).Height.SurfaceDetail.Landforms.Valleys;
            Assert.AreEqual(a.SegmentCount, b.SegmentCount);
            for (int s = 0; s < a.SegmentCount; s++)
            {
                Assert.AreEqual(a.GetSegment(s).A, b.GetSegment(s).A);
                Assert.AreEqual(a.GetSegment(s).HalfWidthB, b.GetSegment(s).HalfWidthB);
            }
            foreach (Vector3d d in Spiral(2000))
            {
                Assert.AreEqual(a.Carve(d, 0f, out float ca), b.Carve(d, 0f, out float cb));
                Assert.AreEqual(ca, cb);
            }
            Assert.Less(a.SegmentCount, 200000, "Bounded size");
            TestContext.WriteLine($"build {a.BuildMilliseconds:0} ms / {b.BuildMilliseconds:0} ms, {a.SegmentCount} segments");
            Assert.Less(Math.Min(a.BuildMilliseconds, b.BuildMilliseconds), 5000.0, "Bounded preprocessing");
        }

        /// <summary>Valley floors are elongated paths: a floor point has floor neighbors along one direction, not all around.</summary>
        [Test]
        public void ValleyFloors_AreOpenCorridors_NotIsolatedPits()
        {
            PlanetInstance planet = Planet(1f, Seed); // the reference seed: 12.6% of its land is mountainous
            ValleyNetwork network = planet.Height.SurfaceDetail.Landforms.Valleys;
            double R = planet.Scale.RadiusMeters;
            int floors = 0, open = 0;
            var histogram = new int[17];
            foreach (Vector3d d in Spiral(200000))
            {
                network.Carve(d, 0f, out float corridor);
                if (corridor < 0.9f) continue;
                floors++;
                // Ring of 16 points 20 km away (beyond the broadest walls, ~14 km half width): an open corridor leaves the
                // ring on two sides at least, and the floor does not surround the point (a pit or ring would make all or
                // none of the ring floor).
                Vector3d north = PlanetTangentFrame.At(d).North, east = Vector3d.Cross(north, d).Normalized;
                int inside = 0;
                for (int k = 0; k < 16; k++)
                {
                    double a = k * Math.PI / 8.0;
                    Vector3d p = (d + (north * Math.Cos(a) + east * Math.Sin(a)) * (20000.0 / R)).Normalized;
                    network.Carve(p, 0f, out float c);
                    if (c > 0.3f) inside++;
                }
                if (inside >= 2 && inside <= 12) open++;
                histogram[inside]++;
            }
            TestContext.WriteLine($"{floors} floor samples, {open} on open corridors; ring floor counts 0..16: {string.Join(" ", histogram)}");
            Assert.Greater(floors, 30);
            Assert.Greater(open, floors * 0.9, "Valley floors continue along a path and are bounded by walls");
        }
    }
}
