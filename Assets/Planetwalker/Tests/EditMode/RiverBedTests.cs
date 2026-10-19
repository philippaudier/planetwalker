using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Persistence;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>
    /// River beds along the whole network (V0.2.0G.2E): one reference hydrology (the channel network drains the very
    /// field the river network's hydrology reads), every river node on a carved channel, a bounded, deterministic,
    /// band-limited bed with a C1 composite cross-section measured on the engine's own functions, progressive headwaters,
    /// regimes and curvature asymmetry, downhill paths and compatible confluences, exact compatibility of archived worlds
    /// (riverBeds = 0 reproduces the recorded G.2B fingerprint bit for bit), and measured cost.
    /// </summary>
    public class RiverBedTests
    {
        const float Threshold = 1e-5f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Dictionary<(int, float, int), PlanetInstance> Planets = new Dictionary<(int, float, int), PlanetInstance>();

        static PlanetInstance Planet(int seed = 1337, float strength = 1f, int beds = 1)
        {
            if (!Planets.TryGetValue((seed, strength, beds), out PlanetInstance planet))
            {
                planet = new PlanetInstance(new PlanetDefinition { seed = seed, erosionStrength = strength, riverBeds = beds });
                Planets[(seed, strength, beds)] = planet;
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

        static ValleyNetwork Network(PlanetInstance p) => p.Height.SurfaceDetail.Landforms.Valleys;

        static float ErosionMeters(PlanetInstance p, Vector3d d)
        {
            p.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
            return (float)(s.Erosion * p.Scale.RadiusMeters);
        }

        /// <summary>Terrain-space points on developed channel axes (every n-th segment end, inverted), with their query.</summary>
        static IEnumerable<(Vector3d point, ValleyNetwork.ChannelQuery query)> AxisPoints(ValleyNetwork net, float minHalfWidth, int every)
        {
            for (int s = 0; s < net.SegmentCount; s += every)
            {
                ValleyNetwork.Segment g = net.GetSegment(s);
                if (g.ChannelHalfWidthB < minHalfWidth) continue;
                Vector3d p = net.Invert(g.B);
                if (net.Nearest(p, out ValleyNetwork.ChannelQuery q) && q.DistanceMeters < 1f) yield return (p, q);
            }
        }

        sealed class Fnv
        {
            ulong h = 14695981039346656037UL;
            public void Add(int v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; } }
            public void Add(float v) => Add(BitConverter.SingleToInt32Bits(v));
            public void Add(double v) { long b = BitConverter.DoubleToInt64Bits(v); Add((int)b); Add((int)(b >> 32)); }
            public string Hex => h.ToString("x16");
        }

        // ------------------------------------------------------------------ compatibility

        /// <summary>
        /// The V0.2.0G.2B terrain fingerprint (Fixtures, recorded on the unmodified tree before G.2E): a world with
        /// riverBeds = 0 reproduces it bit for bit: canonical and legacy elevations at 20 000 directions, the hydrology at
        /// 64 and 128 (receivers, land, accumulation), the valley network (segments, channels, geometry, incision).
        /// </summary>
        [Test]
        public void RiverBedsOff_ReproducesTheRecordedG2BTerrain_BitForBit()
        {
            string path = Path.Combine(Application.dataPath, "Planetwalker/Tests/EditMode/Fixtures/V0.2.0G.2E_Fingerprint_G2B.txt");
            string[] lines = File.ReadAllLines(path);
            string Line(string prefix) => lines.First(l => l.StartsWith(prefix, StringComparison.Ordinal));
            var worlds = new Dictionary<string, PlanetDefinition>
            {
                ["default1337"] = new PlanetDefinition { seed = 1337, riverBeds = 0 },
                ["default310929"] = new PlanetDefinition { seed = 310929, riverBeds = 0 },
                ["tweaked536452"] = WorldArchiveTestUtil.Tweaked(536452, flipFlags: false).Terrain.Clone(),
                ["default1337noErosion"] = new PlanetDefinition { seed = 1337, erosionStrength = 0f, riverBeds = 0 },
            };
            PlanetDefinition tf = worlds["tweaked536452"].Clone(); tf.seed = 1337; tf.erosionStrength = 1f; tf.erosionModel = 1;
            worlds["tweaked1337fluvial"] = tf;
            foreach (KeyValuePair<string, PlanetDefinition> w in worlds)
            {
                Assert.AreEqual(0, w.Value.riverBeds, w.Key);
                var planet = new PlanetInstance(w.Value);
                var canonical = new Fnv(); var legacy = new Fnv();
                foreach (Vector3d d in Spiral(20000))
                {
                    canonical.Add(planet.Height.GetElevation(d));
                    legacy.Add(planet.GetLegacyElevation(d.ToVector3(), Mathf.PI * 0.5f / 128));
                }
                string line = Line(w.Key + ": canonical ");
                Assert.AreEqual(Word(line, 1), canonical.Hex, w.Key + ": canonical elevations");
                Assert.AreEqual(Word(line, 3), legacy.Hex, w.Key + ": legacy elevations at the hydrology spacing");
                foreach (int res in new[] { 64, 128 })
                {
                    HydrologyResult h = new HydrologySystem().Compute(new HydrologyGrid(res), planet);
                    var rec = new Fnv(); var land = new Fnv(); var acc = new Fnv();
                    for (int c = 0; c < h.Grid.CellCount; c++) { rec.Add(h.GetReceiver(c)); land.Add(h.IsLand(c) ? 1 : 0); acc.Add(h.GetAccumulation(c)); }
                    string hl = Line(w.Key + ": hydrology " + res + ":");
                    Assert.AreEqual(Word(hl, 3), rec.Hex, w.Key + ": receivers at " + res);
                    Assert.AreEqual(Word(hl, 5), land.Hex, w.Key + ": land at " + res);
                    Assert.AreEqual(Word(hl, 7), acc.Hex, w.Key + ": accumulation at " + res);
                }
                ValleyNetwork net = Network(planet);
                Assert.IsFalse(net.WholeNetwork);
                var seg = new Fnv();
                for (int s = 0; s < net.SegmentCount; s++)
                {
                    ValleyNetwork.Segment g = net.GetSegment(s);
                    seg.Add(g.A.x); seg.Add(g.A.y); seg.Add(g.A.z); seg.Add(g.B.x); seg.Add(g.B.y); seg.Add(g.B.z);
                    seg.Add(g.HalfWidthA); seg.Add(g.HalfWidthB); seg.Add(g.DepthA); seg.Add(g.DepthB); seg.Add(g.IncisionA); seg.Add(g.IncisionB); seg.Add(g.Cell);
                }
                string vl = Line(w.Key + ": valleys:");
                Assert.AreEqual(int.Parse(Word(vl, 2), Inv), net.SegmentCount, w.Key + ": segments");
                Assert.AreEqual(int.Parse(Word(vl, 4), Inv), net.ChannelCount, w.Key + ": channels");
                Assert.AreEqual(Word(vl, 6), seg.Hex, w.Key + ": segment geometry and incision");
                TestContext.WriteLine($"{w.Key}: canonical {canonical.Hex}, legacy {legacy.Hex}, {net.SegmentCount} segments: identical to the recorded G.2B fingerprint");
            }
        }

        static string Word(string line, int index) => line.Substring(line.IndexOf(": ", StringComparison.Ordinal) + 2).Split(' ')[index];

        // ------------------------------------------------------------------ one hydrology

        [Test]
        public void OneHydrology_TheChannelNetworkDrainsTheRiverNetworksField_Exactly()
        {
            foreach (int seed in new[] { 1337, 310929 })
            {
                PlanetInstance planet = Planet(seed);
                ValleyNetwork net = Network(planet);
                Assert.IsTrue(net.WholeNetwork);
                HydrologyResult carved = net.Hydrology;
                HydrologyResult legacy = new HydrologySystem().Compute(new HydrologyGrid(ValleyNetwork.GridResolution), planet);
                Assert.AreEqual(legacy.Grid.CellCount, carved.Grid.CellCount);
                int cellsChecked = 0;
                for (int c = 0; c < legacy.Grid.CellCount; c++)
                {
                    Assert.AreEqual(legacy.IsLand(c), carved.IsLand(c), $"seed {seed}: land at cell {c}");
                    Assert.AreEqual(legacy.GetReceiver(c), carved.GetReceiver(c), $"seed {seed}: receiver of cell {c}");
                    Assert.AreEqual(legacy.GetAccumulation(c), carved.GetAccumulation(c), $"seed {seed}: accumulation of cell {c}");
                    Assert.AreEqual(legacy.GetElevation(c), carved.GetElevation(c), $"seed {seed}: elevation of cell {c}");
                    cellsChecked++;
                }
                Assert.AreEqual(legacy.BasinCount, carved.BasinCount);
                Assert.AreEqual(legacy.DepressionCount, carved.DepressionCount);
                Assert.AreEqual(legacy.EndorheicOutlet, carved.EndorheicOutlet);
                Assert.AreEqual(legacy.LandCellCount, carved.LandCellCount);
                // Drainage continuity: following the receivers from every land cell reaches the same terminal cell (a sea cell
                // or the endorheic outlet) on both: trivially so with identical receivers, stated as the basin check.
                TestContext.WriteLine($"seed {seed}: {cellsChecked} cells identical (receivers, land, accumulation, elevation); {carved.BasinCount} basins, {net.ChannelCount} channel cells ({net.ValleyCount} valley cells), {net.SegmentCount} segments, built in {net.BuildMilliseconds:0} ms");
            }
        }

        /// <summary>Compared with a G.2B world of the same seed: how much the legacy hydrology at 128 moves (informative, bounded).</summary>
        [Test]
        public void OneHydrology_OutletChanges_AgainstTheG2BWorld_AreMeasuredAndSmall()
        {
            PlanetInstance on = Planet(1337), off = Planet(1337, 1f, 0);
            var grid = new HydrologyGrid(128);
            HydrologyResult a = new HydrologySystem().Compute(grid, on), b = new HydrologySystem().Compute(grid, off);
            int land = 0, receiverChanged = 0, landChanged = 0;
            for (int c = 0; c < grid.CellCount; c++)
            {
                if (a.IsLand(c) != b.IsLand(c)) landChanged++;
                if (!b.IsLand(c)) continue;
                land++;
                if (a.GetReceiver(c) != b.GetReceiver(c)) receiverChanged++;
            }
            TestContext.WriteLine($"legacy hydrology 128, G.2E vs G.2B world (seed 1337): {land} land cells, receivers changed {receiverChanged} ({100.0 * receiverChanged / land:0.00} %), land/sea changed {landChanged}, basins {a.BasinCount} vs {b.BasinCount}");
            Assert.AreEqual(0, landChanged, "The coastline never moves");
            Assert.Less(receiverChanged, land * 0.02, "The band-limited corridor calm moves under 2 % of the drainage");
        }

        // ------------------------------------------------------------------ corridor snapping

        [Test]
        public void CorridorMap_EveryRiverNodeWithInflow_IsOnItsChannel_NoMismatchedLink()
        {
            PlanetInstance planet = Planet(1337);
            ValleyNetwork net = Network(planet);
            HydrologyResult hydrology = new HydrologySystem().Compute(new HydrologyGrid(128), planet);
            var system = new RiverNetworkSystem();
            RiverNetwork network = system.Build(hydrology, Threshold);
            system.BuildPaths(network, new RiverSettings().SmoothingIterations);
            var corridors = new RiverCorridorMap(network, net, planet.Scale.RadiusMeters);
            int sources = 0, mouths = 0, nodes = 0, weightOne = 0, weightOneLinks = 0, links = 0, sourceHalfCells = 0;
            for (int r = 0; r < network.RiverCount; r++)
            {
                River data = network.GetRiver(r);
                int points = data.NodeCount + (data.JunctionCell >= 0 || data.MouthCell >= 0 ? 1 : 0);
                if (data.MouthCell >= 0) mouths++;
                for (int i = 0; i < points; i++)
                {
                    int cell = i < data.NodeCount ? network.GetNodeCell(network.GetRiverNode(r, i)) : data.JunctionCell >= 0 ? data.JunctionCell : data.MouthCell;
                    // A river's first node may be a source cell (no inflow: no channel curve); every other point is on a channel
                    // cell, or is the sea mouth point on the terminal curve.
                    bool firstIsSource = !net.IsChannel(network.GetNodeCell(network.GetRiverNode(r, 0)));
                    bool source = (i == 0 && firstIsSource) || (i == 1 && firstIsSource); // the source point and the half cell after it (a 2-point source river ends in it)
                    if (i == 0 && firstIsSource) sources++;
                    nodes++;
                    float w = corridors.Weight(r, i);
                    if (w >= 1f) weightOne++;
                    else Assert.IsTrue(source, $"river {r} point {i} (cell {cell}, land {hydrology.IsLand(cell)}) weight {w}: only a source cell (no inflow) may be off its channel");
                    if (i + 1 < points)
                    {
                        links++;
                        float wm = corridors.Weight(r, i + 0.5f);
                        if (wm >= 1f) weightOneLinks++;
                        else { Assert.IsTrue(source, $"river {r} link {i}: only the half cell after a source may fade"); sourceHalfCells++; }
                    }
                }
            }
            TestContext.WriteLine($"{network.RiverCount} rivers, {nodes} trajectory points ({sources} source cells without inflow, {mouths} mouths): weight 1 on {weightOne} points and {weightOneLinks} / {links} links (fading half cells after sources {sourceHalfCells}); corridor map: channel nodes {corridors.ChannelNodes} / {corridors.NodeCount}, matched {corridors.MatchedLinks}, mismatched {corridors.MismatchedLinks}");
            Assert.AreEqual(0, corridors.MismatchedLinks, "one hydrology: the legacy flow is the carved flow everywhere");
            Assert.Greater(weightOne, nodes - 2 * sources - 1, "every point but the sources (and the end of a 2-point source river) is on its channel, mouths included");
            Assert.AreEqual(links - sources, weightOneLinks, "every link but the half cell after a source is snapped (density 100 %: 29 % of the trajectory points are sources; at the default density sources are rare)");
            TestContext.WriteLine($"snapped trajectory length at density 100 %: {100.0 * (weightOneLinks + 0.5 * sourceHalfCells) / links:0.0} % (a source half cell counts half)");
        }

        // ------------------------------------------------------------------ bounds, coast, exact zero

        [Test]
        public void Bed_IsBoundedFinite_NeverRaises_KeepsTheCoast_AndIsExactlyZeroAwayFromChannels()
        {
            foreach (int seed in new[] { 1337, 310929 })
            {
                PlanetInstance on = Planet(seed), off = Planet(seed, 0f), strong = Planet(seed, 2f);
                ValleyNetwork net = Network(on);
                double R = on.Scale.RadiusMeters;
                int land = 0, bedded = 0, zeroFar = 0, far = 0;
                float deepest = 0f;
                foreach (Vector3d d in Spiral(60000))
                {
                    float eOff = off.Height.GetElevation(d), eOn = on.Height.GetElevation(d);
                    bool wasLand = eOff > off.SeaLevel;
                    Assert.AreEqual(wasLand, eOn > on.SeaLevel, "Land / sea unchanged by the bed");
                    Assert.AreEqual(wasLand, strong.Height.GetElevation(d) > strong.SeaLevel, "Land / sea unchanged at strength 2");
                    on.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                    Assert.IsTrue(float.IsFinite(s.Erosion));
                    Assert.LessOrEqual(s.Erosion, 0f, "Incision and bed only, never a rise");
                    if (!wasLand) { Assert.AreEqual(0f, s.Erosion); continue; }
                    land++;
                    float shaped = on.Height.GetBaseElevation(d.ToVector3()) + s.Landform - on.SeaLevel;
                    Assert.GreaterOrEqual(s.Erosion, -shaped * 0.35f - 1e-9f, "At most 35 % of the landform-shaped height");
                    deepest = Mathf.Max(deepest, (float)(-s.Erosion * R));
                    if (s.Landforms.Valley <= 0f && s.Erosion < 0f) bedded++;
                    // Exactly zero beyond every reach: the widest feature is 13.75 km (skewed valley), 12 km (floodplain + margin).
                    if (!net.Nearest(d, out ValleyNetwork.ChannelQuery q) || q.DistanceMeters > 15000f)
                    {
                        far++;
                        if (s.Erosion == 0f) zeroFar++;
                        Assert.AreEqual(0f, s.Erosion, "Exactly zero (bitwise) away from every channel");
                    }
                    if (s.Erosion < 0f)
                    {
                        strong.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s2);
                        Assert.LessOrEqual(s2.Erosion, s.Erosion, "Strength 2 carves at least as much");
                        Assert.GreaterOrEqual(s2.Erosion, 2f * s.Erosion - 1e-9f, "...and at most twice");
                    }
                }
                TestContext.WriteLine($"seed {seed}: {land} land samples, {bedded} bedded outside the ranges, {far} far from every channel ({zeroFar} exactly zero), deepest {deepest:0} m");
                // The bed is narrow (a few hundred meters on a 72 km cell): ~0.2 % of the land at random; the channels themselves are bedded.
                Assert.Greater(bedded, 20, "beds exist on the plains (random samples)");
                int axisBedded = 0, axisPlains = 0;
                foreach ((Vector3d p, ValleyNetwork.ChannelQuery q) in AxisPoints(net, 10f, 9))
                {
                    if (q.Valley > 0f) continue;
                    axisPlains++;
                    if (ErosionMeters(on, p) < -0.5f) axisBedded++;
                }
                TestContext.WriteLine($"seed {seed}: {axisBedded} / {axisPlains} developed plains axis points carved by more than 0.5 m");
                Assert.Greater(axisBedded, axisPlains * 0.9, "developed channels on the plains have a bed");
                Assert.AreEqual(off.LandFraction, on.LandFraction, "Same shoreline");
                Assert.Greater(far, 1000);
            }
        }

        [Test]
        public void Bed_IsDeterministic_AcrossInstancesAndThreads()
        {
            var a = new PlanetInstance(new PlanetDefinition { seed = 1337 });
            var b = new PlanetInstance(new PlanetDefinition { seed = 1337 });
            ValleyNetwork na = Network(a), nb = Network(b);
            Assert.AreEqual(na.SegmentCount, nb.SegmentCount);
            for (int s = 0; s < na.SegmentCount; s += 5)
            {
                ValleyNetwork.Segment ga = na.GetSegment(s), gb = nb.GetSegment(s);
                Assert.AreEqual(ga.A, gb.A);
                Assert.AreEqual(ga.ChannelHalfWidthB, gb.ChannelHalfWidthB);
                Assert.AreEqual(ga.CurvatureB, gb.CurvatureB);
                Assert.AreEqual(ga.FloodplainDepthB, gb.FloodplainDepthB);
            }
            var probes = new List<Vector3d>(Spiral(20000));
            foreach ((Vector3d p, _) in AxisPoints(na, 5f, 25)) probes.Add(p);
            var sequential = probes.ConvertAll(d => a.Height.GetElevation(d));
            var other = new float[probes.Count];
            Parallel.For(0, probes.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => other[i] = b.Height.GetElevation(probes[i]));
            for (int i = 0; i < probes.Count; i++) Assert.AreEqual(sequential[i], other[i]);
            var bed = new float[probes.Count];
            Parallel.For(0, probes.Count, i => bed[i] = na.Bed(probes[i], 0f, 1f, out _, out _));
            for (int i = 0; i < probes.Count; i += 7) Assert.AreEqual(nb.Bed(probes[i], 0f, 1f, out _, out _), bed[i]);
        }

        // ------------------------------------------------------------------ cross-section continuity, on the engine

        /// <summary>Numeric height and slope on both sides of every zone boundary of the coded cross-section, real and synthetic sections.</summary>
        [Test]
        public void CrossSection_IsC1AtEveryBoundary_OnTheCodedFunction_IncludingTheClampedCases()
        {
            ValleyNetwork net = Network(Planet(1337));
            var sections = new List<(ChannelSection c, float asym)>();
            for (int s = 0; s < net.SegmentCount && sections.Count < 400; s += 13)
            {
                ValleyNetwork.Segment g = net.GetSegment(s);
                if (g.ChannelHalfWidthB < 10f) continue;
                sections.Add((new ChannelSection
                {
                    HalfWidth = g.ChannelHalfWidthB, Depth = g.ChannelDepthB, BankWidth = g.BankWidthB,
                    FloodplainHalfWidth = g.FloodplainHalfWidthB, FloodplainDepth = g.FloodplainDepthB, Asymmetry = g.AsymmetryB,
                }, Math.Abs(RiverBedMorphology.NormalizedCurvature(g.CurvatureB)) * g.AsymmetryB));
            }
            Assert.Greater(sections.Count, 200, "real sections");
            // Synthetic: lambda = 0 (long gentle bank: the parabola's edge slope is kept), clamped (short steep bank), extreme.
            sections.Add((new ChannelSection { HalfWidth = 100f, Depth = 10f, BankWidth = 400f, FloodplainHalfWidth = 600f, FloodplainDepth = 4f, Asymmetry = 1f }, 1f));
            sections.Add((new ChannelSection { HalfWidth = 100f, Depth = 10f, BankWidth = 12f, FloodplainHalfWidth = 0f, FloodplainDepth = 0f, Asymmetry = 0.3f }, 0.5f));
            sections.Add((new ChannelSection { HalfWidth = 8f, Depth = 4f, BankWidth = 10f, FloodplainHalfWidth = 0f, FloodplainDepth = 0f, Asymmetry = 0.3f }, 0f));
            sections.Add((new ChannelSection { HalfWidth = 1200f, Depth = 40f, BankWidth = 1500f, FloodplainHalfWidth = 8000f, FloodplainDepth = 15f, Asymmetry = 1f }, 1f));
            int clamped = 0, boundaries = 0;
            float worstJump = 0f, worstSlopeRatio = 0f;
            foreach ((ChannelSection c, float asym) in sections)
            {
                float Height(float sAxis)
                {
                    RiverBedMorphology.CrossSection(c, sAxis, asym, out float excess, out float plainDepth, out _, out _);
                    return -(plainDepth + excess);
                }
                float shift = RiverBedMorphology.ThalwegShift * c.HalfWidth * asym;
                float dv = Mathf.Min(c.FloodplainDepth, 0.44f * c.Depth);
                var points = new List<float> { shift }; // the thalweg
                foreach (int side in new[] { 1, -1 })
                {
                    bool outer = side > 0;
                    float wSide = outer ? c.HalfWidth - shift : c.HalfWidth + shift;
                    float e = RiverBedMorphology.EdgeShare * c.Depth * (outer ? 1f + RiverBedMorphology.EdgeTilt * asym : 1f - RiverBedMorphology.EdgeTilt * asym);
                    float bSide = c.BankWidth * (outer ? 1f - RiverBedMorphology.OuterBankShare * asym : 1f + RiverBedMorphology.InnerBankShare * asym);
                    if (2f * (c.Depth - e) / wSide > 3f * (e - dv) / bSide) clamped++;
                    points.Add(shift + side * wSide);            // channel -> bank
                    points.Add(shift + side * (wSide + bSide));  // bank -> floodplain (or terrain)
                    if (c.FloodplainHalfWidth > 0f)
                    {
                        points.Add(side * c.FloodplainHalfWidth);                                           // floodplain -> margin
                        points.Add(side * c.FloodplainHalfWidth * (1f + RiverBedMorphology.MarginShare));   // margin -> terrain
                    }
                }
                foreach (float x in points)
                {
                    boundaries++;
                    float jump = Math.Abs(Height(x + 1e-3f) - Height(x - 1e-3f));
                    worstJump = Math.Max(worstJump, jump);
                    Assert.Less(jump, 0.002f, "C0: no height step at a boundary");
                    float Slope(float at, float eps) => (Height(at + eps) - Height(at - eps)) / (2f * eps);
                    float big = Math.Abs(Slope(x + 1f, 0.5f) - Slope(x - 1f, 0.5f));
                    float small = Math.Abs(Slope(x + 0.05f, 0.025f) - Slope(x - 0.05f, 0.025f));
                    // C1: the slope difference across the boundary shrinks with the separation (a kink would keep it).
                    Assert.LessOrEqual(small, 0.2f * big + 0.002f, $"C1 at x = {x:0.00} m of a section {c.HalfWidth:0}/{c.Depth:0}/{c.BankWidth:0}/{c.FloodplainHalfWidth:0}: slope difference {small:0.0000} at 0.1 m vs {big:0.0000} at 2 m");
                    if (big > 1e-4f) worstSlopeRatio = Math.Max(worstSlopeRatio, small / big);
                }
                // Monotone zones: from the thalweg outwards the height never decreases (no secondary groove).
                foreach (int side in new[] { 1, -1 })
                {
                    float reach = Math.Max(c.FloodplainHalfWidth * 1.5f, c.HalfWidth * 1.3f + c.BankWidth * 2f) + 2f;
                    float previous = Height(shift);
                    for (float x = 0.5f; x <= reach; x += 0.5f)
                    {
                        float h = Height(shift + side * x);
                        Assert.GreaterOrEqual(h, previous - 1e-4f, $"monotone outward at {side * x:0.0} m");
                        previous = h;
                    }
                }
            }
            TestContext.WriteLine($"{sections.Count} sections ({clamped} with the edge-slope clamp active), {boundaries} boundaries: worst height step {worstJump * 1000f:0.000} mm, worst slope-difference ratio 0.1 m / 2 m {worstSlopeRatio:0.000}");
            Assert.Greater(clamped, 0, "the clamp is exercised");
        }

        /// <summary>On the terrain itself (bed + incision + calmed octaves), across real channels: no kink at any step.</summary>
        [Test]
        public void Terrain_AcrossChannels_HasNoKink_AndTheUnionNeverExceedsItsBound()
        {
            PlanetInstance planet = Planet(1337);
            ValleyNetwork net = Network(planet);
            double R = planet.Scale.RadiusMeters;
            int sections = 0; float worstRatio = 0f;
            foreach ((Vector3d p, ValleyNetwork.ChannelQuery q) in AxisPoints(net, 20f, 40))
            {
                if (sections >= 150) break;
                sections++;
                Vector3d up = p.Normalized, flow = (q.Flow - up * Vector3d.Dot(q.Flow, up)).Normalized, side = Vector3d.Cross(up, flow).Normalized;
                float reach = Math.Max(q.FloodplainHalfWidthMeters * 1.6f, q.ChannelHalfWidthMeters * 1.4f + q.BankWidthMeters * 2.2f);
                float Bed(double meters) => net.Bed((p + side * (meters / R)).Normalized, 0f, 1f, out _, out _);
                // Second differences at two steps: a C1 field halves them with the step; a kink keeps them.
                float worstBig = 0f, worstSmall = 0f;
                for (double x = -reach; x <= reach; x += 2.0)
                {
                    float big = Math.Abs(Bed(x + 2.0) - 2f * Bed(x) + Bed(x - 2.0));
                    float small = Math.Abs(Bed(x + 0.5) - 2f * Bed(x) + Bed(x - 0.5));
                    worstBig = Math.Max(worstBig, big);
                    worstSmall = Math.Max(worstSmall, small);
                }
                if (worstBig > 1e-3f) worstRatio = Math.Max(worstRatio, worstSmall / worstBig);
                Assert.LessOrEqual(worstSmall, 0.125f * worstBig + 1e-3f, $"no kink across the channel at {p}: second difference {worstSmall:0.0000} m at 0.5 m vs {worstBig:0.0000} m at 2 m");
            }
            Assert.Greater(sections, 50);
            // Union bound: at every axis point the bed is at most 2^(1/8) x the deepest single contribution ... measured as the
            // depth against the node's own channel depth (the single contribution there): never above it by more than 9.1 %.
            int checkedPoints = 0, multi = 0, maxChannels = 0; float worstExcess = 0f;
            foreach ((Vector3d p, ValleyNetwork.ChannelQuery q) in AxisPoints(net, 5f, 17))
            {
                float depth = net.Bed(p, 0f, 1f, out _, out _, out float largest, out int channels);
                if (largest <= 0f) continue;
                Assert.GreaterOrEqual(depth, largest - 1e-5f, "the union never carves less than its deepest contribution");
                float bound = largest * Mathf.Pow(channels, 1f / RiverBedMorphology.UnionPower);
                Assert.LessOrEqual(depth, bound + 1e-4f, $"the p-norm union of {channels} channels stays under N^(1/p) x the deepest");
                if (channels == 1) Assert.AreEqual(largest, depth, "one contribution is rendered exactly");
                worstExcess = Math.Max(worstExcess, depth / largest);
                if (channels > 1) multi++;
                maxChannels = Math.Max(maxChannels, channels);
                checkedPoints++;
            }
            TestContext.WriteLine($"{sections} sections scanned, worst second-difference ratio {worstRatio:0.000}; {checkedPoints} axis points ({multi} with several contributing channels, at most {maxChannels}), deepest union / deepest contribution {worstExcess:0.000}");
            Assert.Greater(checkedPoints, 500);
            Assert.Less(worstExcess, Mathf.Pow(maxChannels, 1f / RiverBedMorphology.UnionPower) + 1e-3f);
        }

        // ------------------------------------------------------------------ morphology

        [Test]
        public void Morphology_Development_Regimes_AndAsymmetry()
        {
            // Development: nothing at one cell, growing, full at the developed count; regimes from the gradient.
            ChannelSection one = RiverBedMorphology.Evaluate(new ChannelState { Cells = 1f, Gradient = 0.002f, Erodibility = 1f });
            Assert.AreEqual(0f, one.HalfWidth); Assert.AreEqual(0f, one.Depth); Assert.AreEqual(0f, one.FloodplainHalfWidth);
            float previous = 0f;
            for (float a = 1f; a <= 64f; a *= 1.25f)
            {
                ChannelSection c = RiverBedMorphology.Evaluate(new ChannelState { Cells = a, Gradient = 0.002f, Erodibility = 1f });
                Assert.GreaterOrEqual(c.HalfWidth, previous, "width grows with the drained area");
                Assert.LessOrEqual(c.Depth, 0.5f * c.BankWidth + 1e-4f);
                Assert.LessOrEqual(c.FloodplainDepth, RiverBedMorphology.MaxFloodplainDepthMeters);
                previous = c.HalfWidth;
            }
            ChannelSection developed = RiverBedMorphology.Evaluate(new ChannelState { Cells = RiverBedMorphology.DevelopedCells, Gradient = 0.002f, Erodibility = 1f });
            Assert.AreEqual(1f, developed.Development, 1e-5f);
            ChannelSection rocky = RiverBedMorphology.Evaluate(new ChannelState { Cells = 50f, Gradient = 0.03f, Erodibility = 1f });
            ChannelSection alluvial = RiverBedMorphology.Evaluate(new ChannelState { Cells = 50f, Gradient = 0.0002f, Erodibility = 1f });
            Assert.Greater(rocky.Rocky, 0.9f); Assert.Less(rocky.Alluvial, 0.01f);
            Assert.Greater(alluvial.Alluvial, 0.9f); Assert.Less(alluvial.Rocky, 0.01f);
            Assert.Less(rocky.HalfWidth, alluvial.HalfWidth, "a torrent is narrower");
            Assert.Greater(rocky.Depth, alluvial.Depth, "...and relatively deeper");
            Assert.Less(rocky.BankWidth, alluvial.BankWidth, "...with steeper banks");
            Assert.AreEqual(0f, rocky.FloodplainHalfWidth, "no floodplain for a torrent");
            Assert.Greater(alluvial.FloodplainHalfWidth, alluvial.HalfWidth * 3f, "a floodplain for the alluvial channel");
            ChannelSection mountain = RiverBedMorphology.Evaluate(new ChannelState { Cells = 50f, Gradient = 0.0002f, Erodibility = 1f, Influence = 0.5f });
            Assert.AreEqual(0f, mountain.FloodplainHalfWidth, "inside the ranges the valleys take over: no floodplain");
            ChannelSection shore = RiverBedMorphology.Evaluate(new ChannelState { Cells = 50f, Gradient = 0.0002f, Erodibility = 1f, Shore = true, HeadroomMeters = 0f });
            Assert.AreEqual(0f, shore.FloodplainHalfWidth, "no floodplain lowering on a lake shore without headroom");
            Assert.Greater(shore.HalfWidth, 0f, "the channel itself continues through");
            ChannelSection lowShore = RiverBedMorphology.Evaluate(new ChannelState { Cells = 50f, Gradient = 0.0002f, Erodibility = 1f, Shore = true, HeadroomMeters = 3f });
            Assert.LessOrEqual(lowShore.FloodplainDepth, 3f, "a floodplain never lowers a shore under the lake level");
            Assert.Greater(lowShore.FloodplainHalfWidth, 0f);
            // Asymmetry: symmetric at zero, outer bank shorter and deeper edge at one.
            float Top(ChannelSection c, float asym, int side)
            {
                for (float x = 0f; x < 5000f; x += 0.5f)
                {
                    RiverBedMorphology.CrossSection(c, side * x, asym, out float excess, out _, out _, out _);
                    if (x > 1f && excess <= 0f) return x;
                }
                return float.NaN;
            }
            for (float x = 0f; x <= alluvial.HalfWidth * 1.3f + alluvial.BankWidth * 2f; x += 7f)
            {
                RiverBedMorphology.CrossSection(alluvial, x, 0f, out float plus, out _, out _, out _);
                RiverBedMorphology.CrossSection(alluvial, -x, 0f, out float minus, out _, out _, out _);
                Assert.AreEqual(plus, minus, 1e-5f, "symmetric without curvature");
            }
            float outerTop = Top(alluvial, 1f, 1), innerTop = Top(alluvial, 1f, -1);
            Assert.Less(outerTop, innerTop, "the outer bank top is closer to the axis than the inner (point bar) top");
            // The channel edges at full asymmetry: the thalweg is shifted toward the outer bank, the outer edge is deeper.
            float shift = RiverBedMorphology.ThalwegShift * alluvial.HalfWidth;
            RiverBedMorphology.CrossSection(alluvial, shift + 0.99f * (alluvial.HalfWidth - shift), 1f, out float outerEdge, out _, out _, out _);
            RiverBedMorphology.CrossSection(alluvial, shift - 0.99f * (alluvial.HalfWidth + shift), 1f, out float innerEdge, out _, out _, out _);
            Assert.Greater(outerEdge, innerEdge, "the outer edge is deeper (transverse tilt)");
            // On the planet: bends exist on the plains and straight reaches are symmetric.
            ValleyNetwork net = Network(Planet(1337));
            int bends = 0, straight = 0;
            for (int s = 0; s < net.SegmentCount; s++)
            {
                ValleyNetwork.Segment g = net.GetSegment(s);
                if (g.ChannelHalfWidthB < 20f || g.ValleyB > 0f) continue;
                float kappa = Math.Abs(RiverBedMorphology.NormalizedCurvature(g.CurvatureB));
                if (kappa > 0.5f) bends++; else if (kappa < 0.05f) straight++;
            }
            TestContext.WriteLine($"plains segments: {bends} in bends (|kappa_n| > 0.5), {straight} straight (< 0.05); outer / inner bank tops {outerTop:0} / {innerTop:0} m for the alluvial reference");
            Assert.Greater(bends, 50);
            Assert.Greater(straight, 50);
        }

        [Test]
        public void SmallRivers_DevelopmentIsMeasured_OnTheDisplayedNetworks()
        {
            PlanetInstance planet = Planet(1337);
            ValleyNetwork net = Network(planet);
            HydrologyResult hydrology = new HydrologySystem().Compute(new HydrologyGrid(128), planet);
            var system = new RiverNetworkSystem();
            foreach (float threshold in new[] { new HydrologySettings().riverNetworkThreshold, Threshold })
            {
                RiverNetwork network = system.Build(hydrology, threshold);
                var byOrder = new SortedDictionary<int, (int nodes, int developed)>();
                int riversWithoutBed = 0, nodes = 0, developedNodes = 0;
                for (int r = 0; r < network.RiverCount; r++)
                {
                    River data = network.GetRiver(r);
                    bool anyBed = false;
                    for (int i = 0; i < data.NodeCount; i++)
                    {
                        int node = network.GetRiverNode(r, i);
                        float d = net.NodeSection(network.GetNodeCell(node)).Development;
                        int order = network.GetStrahler(node);
                        (int n, int dev) = byOrder.TryGetValue(order, out var v) ? v : (0, 0);
                        byOrder[order] = (n + 1, dev + (d >= 0.5f ? 1 : 0));
                        nodes++;
                        if (d >= 0.5f) { developedNodes++; anyBed = true; }
                    }
                    if (!anyBed) riversWithoutBed++;
                }
                TestContext.WriteLine($"threshold {threshold:0.0e0}: {network.RiverCount} rivers, {nodes} nodes, {100.0 * developedNodes / nodes:0.0} % with a developed bed (D >= 0.5); rivers without any developed node {riversWithoutBed}; by Strahler order: "
                    + string.Join(", ", byOrder.Select(kv => $"{kv.Key}: {kv.Value.developed}/{kv.Value.nodes}")));
                if (threshold > Threshold)
                {
                    Assert.Greater(developedNodes, nodes * 0.9, "at the default density the displayed rivers have a developed bed on most of their length");
                    Assert.Less(riversWithoutBed, network.RiverCount * 0.02, "at the default density almost no displayed river lacks a bed");
                }
            }
        }

        // ------------------------------------------------------------------ paths and confluences

        [Test]
        public void Paths_HeadToOutlet_TheBedNeverCreatesAnUphillStep_AndConfluencesAreCompatible()
        {
            PlanetInstance on = Planet(1337), before = Planet(1337, 0f);
            ValleyNetwork net = Network(on);
            HydrologyResult h = net.Hydrology;
            int cellCount = h.Grid.CellCount;
            var upstream = new int[cellCount];
            for (int c = 0; c < cellCount; c++) { int r = h.GetReceiver(c); if (r >= 0 && net.IsChannel(c)) upstream[r]++; }
            int paths = 0, steps = 0, newUphill = 0, plainsSteps = 0;
            var seen = new HashSet<int>();
            for (int s = 0; s < net.SegmentCount; s += ValleyNetwork.Subdivisions)
            {
                int head = net.GetSegment(s).Cell;
                if (upstream[head] != 0 || !seen.Add(head)) continue;
                int cell = head, guard = 0;
                float prevBefore = float.NaN, prevAfter = float.NaN;
                while (cell >= 0 && h.IsLand(cell) && net.IsChannel(cell) && guard++ < 400)
                {
                    var d = new Vector3d(h.Grid.GetCenter(cell));
                    if (net.Nearest(d, out ValleyNetwork.ChannelQuery q) && q.ChannelHalfWidthMeters > 0f && q.DistanceMeters < Math.Max(q.ChannelHalfWidthMeters, q.HalfWidthMeters * q.Valley))
                    {
                        Vector3d axis = net.Invert(q.Closest.Normalized);
                        float b = before.Height.GetElevation(axis), a = on.Height.GetElevation(axis);
                        if (!float.IsNaN(prevBefore))
                        {
                            steps++;
                            if (q.Valley <= 0f) plainsSteps++;
                            float dropBefore = prevBefore - b, dropAfter = prevAfter - a;
                            if (dropBefore > 0f && dropAfter < -0.02f * dropBefore) newUphill++;
                        }
                        prevBefore = b; prevAfter = a;
                    }
                    cell = h.GetReceiver(cell);
                }
                if (guard > 2) paths++;
            }
            TestContext.WriteLine($"{paths} head-to-outlet channel paths, {steps} node steps ({plainsSteps} on the plains), new uphill steps {newUphill}");
            Assert.Greater(paths, 200);
            Assert.Greater(plainsSteps, 1000);
            Assert.AreEqual(0, newUphill, "the bed never turns a descending floor into an ascending one");
            // Confluences: the trunk node is at least as wide, nearly as deep, and the bed depth is continuous through the
            // shared point (sampled 200 m apart along the tributary's last curve and the trunk's curve from it).
            int junctions = 0; float worstWidth = 0f, worstDepth = 0f, worstStep = 0f;
            double R = on.Scale.RadiusMeters;
            for (int c = 0; c < cellCount && junctions < 300; c++)
            {
                if (upstream[c] < 2 || !net.IsChannel(c)) continue;
                ChannelSection trunk = net.NodeSection(c);
                if (trunk.HalfWidth < 10f) continue;
                net.GetCellSegments(c, out int start, out int end);
                for (int s = start; s < end; s += ValleyNetwork.Subdivisions)
                {
                    // The upstream cell of this curve: the channel neighbour draining into c whose curves end at this curve's start.
                    ValleyNetwork.Segment first = net.GetSegment(s);
                    for (int k = h.Grid.GetNeighborStart(c); k < h.Grid.GetNeighborEnd(c); k++)
                    {
                        int a = h.Grid.GetNeighbor(k);
                        if (!net.IsChannel(a) || h.GetReceiver(a) != c) continue;
                        net.GetCellSegments(a, out int aStart, out int aEnd);
                        if (aEnd <= aStart || (net.GetSegment(aEnd - 1).B - first.A).Magnitude > 1e-9) continue;
                        ChannelSection trib = net.NodeSection(a);
                        if (trib.HalfWidth < 10f) continue;
                        junctions++;
                        worstWidth = Math.Max(worstWidth, trib.HalfWidth - trunk.HalfWidth);
                        worstDepth = Math.Max(worstDepth, 0.9f * trib.Depth - trunk.Depth);
                        // Bed depth along the path through the shared point.
                        var profile = new List<float>();
                        for (int seg = aEnd - ValleyNetwork.Subdivisions; seg < aEnd; seg++) profile.Add(net.Bed(net.Invert(net.GetSegment(seg).B), 0f, 1f, out _, out _));
                        for (int seg = s; seg < s + ValleyNetwork.Subdivisions; seg++) profile.Add(net.Bed(net.Invert(net.GetSegment(seg).B), 0f, 1f, out _, out _));
                        for (int i = 1; i < profile.Count; i++) worstStep = Math.Max(worstStep, Math.Abs(profile[i] - profile[i - 1]));
                    }
                }
            }
            TestContext.WriteLine($"{junctions} junction curves: worst tributary - trunk half width {worstWidth:0.00} m, worst 0.9 x tributary depth - trunk depth {worstDepth:0.00} m, worst bed-depth step between ~10 km samples through a junction {worstStep:0.0} m");
            Assert.Greater(junctions, 50);
            Assert.LessOrEqual(worstWidth, 1e-3f, "a trunk is never narrower than a tributary joining it");
            Assert.LessOrEqual(worstDepth, 1e-3f, "a trunk is at least 90 % as deep as a tributary joining it");
        }

        // ------------------------------------------------------------------ band limit, LOD, bounds

        [Test]
        public void BandLimit_AbsentAtTheLegacySpacing_CountedByTheLodTerm_ContainedByTheBounds()
        {
            PlanetInstance planet = Planet(1337);
            ErosionSampler erosion = planet.Height.SurfaceDetail.Erosion;
            ValleyNetwork net = Network(planet);
            Assert.IsTrue(erosion.HasRiverBeds);
            Assert.AreEqual(0f, erosion.UnresolvedAmplitude(0f));
            Assert.Greater(net.UnresolvedBedMeters(1e-5f), 0f);
            Assert.Greater(net.UnresolvedBedMeters(1e-4f), net.UnresolvedBedMeters(1e-5f), "coarser: more unresolved");
            Assert.LessOrEqual(net.UnresolvedBedMeters(1f), RiverBedMorphology.MaxTotalDepthMeters);
            Assert.GreaterOrEqual(planet.Height.SurfaceDetail.UnresolvedAmplitude(1e-4f), erosion.UnresolvedAmplitude(1e-4f));
            int bedded = 0;
            foreach ((Vector3d d, _) in AxisPoints(net, 10f, 30))
            {
                planet.Height.GetElevation(d, 0f, out TerrainBaseSample ctx);
                planet.Height.SampleLayers(d.ToVector3(), out TerrainDetailSample s);
                erosion.Evaluate(d, d.ToVector3(), ctx, s.Landforms, 0f, out ErosionSample full);
                erosion.Evaluate(d, d.ToVector3(), ctx, s.Landforms, 0.01f, out ErosionSample coarse);
                Assert.AreEqual(0f, coarse.Displacement, "Absent at the legacy spacing (hydrology, lakes and climate never see the bed)");
                Assert.AreEqual(0f, coarse.Floor); Assert.AreEqual(0f, coarse.Bed);
                erosion.Evaluate(d, d.ToVector3(), ctx, s.Landforms, 1e-4f, out ErosionSample mid);
                Assert.GreaterOrEqual(mid.Displacement, full.Displacement - 1e-9f, "Faded, never deepened, at an intermediate spacing");
                if (full.Displacement < 0f) bedded++;
                // Relief bounds evaluate the bed completely: the bounds elevation differs from the canonical one by the band-limited octaves only.
                float bounds = planet.Height.GetBoundsElevation(d.ToVector3(), 1e-4f);
                float canonical = planet.Height.GetElevation(d);
                Assert.LessOrEqual(Math.Abs(bounds - canonical), planet.Height.SurfaceDetail.Profile.UnresolvedAmplitude(1e-4f) + 1e-7f, "bounds contain the bed");
            }
            Assert.Greater(bedded, 100);
        }

        // ------------------------------------------------------------------ archives

        [Test]
        public void Archives_LoadWithoutTheFieldAsZero_HonorItWhenPresent_VersionsExact_DeclaredLast()
        {
            Assert.AreEqual(1, new PlanetDefinition().riverBeds, "new worlds carve beds");
            Assert.AreEqual("Genesis V0.2.0G.2E", PlanetArchive.CurrentGeneratorVersion);
            Assert.AreEqual(GeneratorCompatibility.Exact, PlanetArchiveSerializer.CompatibilityOf("Genesis V0.2.0G.2B"), "a G.2B archive is reproduced exactly (riverBeds absent -> 0)");
            Assert.IsFalse(new PlanetDefinition { riverBeds = 0 }.TerrainEquals(new PlanetDefinition()), "a terrain setting (hydrology caches invalidate)");
            Assert.IsFalse(new PlanetDefinition { riverBeds = 0 }.SurfaceDetailEquals(new PlanetDefinition()));
            PlanetArchive archive = WorldArchiveTestUtil.NewArchive("G.2B world");
            archive.Configuration.Terrain.riverBeds = 1;
            JsonValue root = JsonReader.Parse(PlanetArchiveSerializer.Serialize(archive));
            Assert.IsTrue(root.Find("terrain").Remove("riverBeds"));
            ArchiveReadResult read = PlanetArchiveSerializer.Read(root.ToJson());
            Assert.IsTrue(read.Ok, read.Report.Summary());
            Assert.AreEqual(0, read.Archive.Configuration.Terrain.riverBeds, "An archive before G.2E keeps the G.2B terrain");
            Assert.IsEmpty(read.Report.Warnings, "a documented compatibility rule, not a warning");
            Assert.AreEqual(GeneratorCompatibility.Exact, read.Compatibility);
            ArchiveReadResult old = PlanetArchiveSerializer.Read(WorldArchiveTestUtil.V013AArchiveJson);
            Assert.AreEqual(0, old.Archive.Configuration.Terrain.riverBeds);
            foreach (int beds in new[] { 0, 1 })
            {
                PlanetArchive a = WorldArchiveTestUtil.NewArchive("Beds " + beds);
                a.Configuration.Terrain.riverBeds = beds;
                ArchiveReadResult r = PlanetArchiveSerializer.Read(PlanetArchiveSerializer.Serialize(a));
                Assert.AreEqual(beds, r.Archive.Configuration.Terrain.riverBeds);
            }
            List<string> members = JsonReader.Parse(PlanetArchiveSerializer.Serialize(archive)).Find("terrain").Members.Select(m => m.Key).ToList();
            Assert.AreEqual("riverBeds", members[members.Count - 1], "declared last in the terrain group");
            Assert.AreEqual("erosionModel", members[members.Count - 2]);
            // Out-of-range values are clamped by Sanitize (as every setting).
            var def = new PlanetDefinition { riverBeds = 7 }; def.Sanitize(); Assert.AreEqual(1, def.riverBeds);
        }

        // ------------------------------------------------------------------ river surface on the beds

        /// <summary>
        /// The canonical river surface on a bedded planet (default configuration, density 100 %): nearly every spine
        /// sample snapped, the displayed half width the active channel's (never the artistic ribbon), the level-0 water
        /// surface the bed plus the fill share of the thalweg depth (at least the width rule), banks inside the channel.
        /// </summary>
        [Test]
        public void Surface_RidesInTheChannels_AtTheFillDepth()
        {
            var c = new WorldConfiguration();
            c.Hydrology.riverNetworkThreshold = Threshold;
            PlanetDefinition def = c.Terrain.Clone();
            def.seed = 1337; def.surfaceDetail = true; def.regionalLandforms = 1f; def.erosionStrength = 1f; def.erosionModel = 1; def.riverBeds = 1;
            PlanetInstance planet = Planet(1337);
            ValleyNetwork net = Network(planet);
            var hydrology = new HydrologySystem().Compute(new HydrologyGrid(c.Hydrology.resolution), planet);
            var system = new RiverNetworkSystem();
            RiverNetwork network = system.Build(hydrology, Threshold);
            system.BuildPaths(network, c.Rivers.SmoothingIterations);
            var faces = new Mesh[CubeSphereGenerator.FaceCount];
            var meshBuilder = new Planetwalker.Rendering.PlanetMeshBuilder();
            for (int f = 0; f < faces.Length; f++) { faces[f] = new Mesh(); meshBuilder.BuildFace(faces[f], f, planet, Planetwalker.Rendering.PlanetColorRamp.CreateDefault()); }
            var sampler = new Planetwalker.Rendering.PlanetSurfaceSampler();
            sampler.Capture(faces, def.resolution);
            foreach (Mesh m in faces) UnityEngine.Object.DestroyImmediate(m);
            var spine = new Planetwalker.Rendering.RiverRibbonBuilder();
            spine.BuildGeometry(network, sampler, planet, c.Rivers, null, null, false);
            var corridors = new RiverCorridorMap(network, net, planet.Scale.RadiusMeters);
            var surface = new Planetwalker.Rendering.RiverSurface();
            surface.Rebuild(planet, network, spine, corridors, 1f, 0);
            Planetwalker.Rendering.RiverSurfaceDiagnostics d = surface.Diagnostics;
            double Rm = planet.Scale.RadiusMeters;
            int snapped = 0, inChannels = 0, widthOk = 0, waterOk = 0, checkedWater = 0, banksInside = 0; float maxWater = 0f; var widths = new List<float>();
            for (int i = 0; i < surface.SpineSampleCount; i++)
            {
                surface.GetSpineSample(i, out Vector3d dir, out float hw, out float w, out _, out float e0);
                if (w < 0.999f) continue;
                snapped++;
                if (!net.Nearest(dir, out ValleyNetwork.ChannelQuery q) || q.ChannelHalfWidthMeters <= 0f) continue;
                inChannels++;
                float hwM = (float)(hw * Rm);
                widths.Add(hwM);
                if (hwM <= Math.Max(q.ChannelHalfWidthMeters, 4f) + 0.5f) widthOk++;
                surface.GetSpineBanks(i, out float hwL, out float hwR);
                if (hwL <= hw + 1e-9f && hwR <= hw + 1e-9f) banksInside++;
                if (i % 50 != 0) continue;
                // Level-0 water: bed + max(width rule, fill share x thalweg depth).
                float bed = planet.Height.GetSurfaceElevation(dir, surface.GetLevelStep(0));
                float expected = Math.Max(surface.WaterDepth(hw), Planetwalker.Rendering.RiverSurface.WaterFillShare * (float)(q.ChannelDepthMeters / Rm) * w);
                checkedWater++;
                float actual = e0 - bed;
                if (Math.Abs(actual - expected) * Rm < 0.1) waterOk++;
                maxWater = Math.Max(maxWater, (float)(actual * Rm));
            }
            // Mouths: the legacy extension past a snapped sea mouth is translated, never collapsed onto the curve's end: no
            // two consecutive placed samples of a ribbon closer than a meter, and the ribbon ends under the canonical ocean
            // (or is a documented unreachable coast).
            int collapsed = 0, endsAbove = 0, oceanRibbons = 0;
            for (int r = 0; r < surface.RibbonCount; r++)
            {
                int a = surface.GetRibbonStart(r), b = surface.GetRibbonStart(r + 1);
                if (b - a < 2) continue;
                for (int i = a + 1; i < b; i++)
                {
                    surface.GetSpineSample(i - 1, out Vector3d p0, out _, out float w0, out _, out _);
                    surface.GetSpineSample(i, out Vector3d p1, out _, out float w1, out _, out _);
                    if ((p1 - p0).Magnitude * Rm < 1.0)
                    {
                        collapsed++;
                        River rv = network.GetRiver(surface.GetSpineRiver(a));
                        if (collapsed <= 12)
                        {
                            int bi = Math.Min(spine.GetSpineStart(r + 1) - 1, spine.GetSpineStart(r) + i - a);
                            Planetwalker.Rendering.RiverSpineSample s0 = spine.GetSpineSample(Math.Max(spine.GetSpineStart(r), bi - 1)), s1 = spine.GetSpineSample(bi);
                            corridors.Snap(new Vector3d(s0.Direction.normalized), s0.River, s0.Param, 1f, out RiverCorridorMap.Projection q0);
                            corridors.Snap(new Vector3d(s1.Direction.normalized), s1.River, s1.Param, 1f, out RiverCorridorMap.Projection q1);
                            TestContext.WriteLine($"collapsed: ribbon {r} samples {i - 1 - a}/{i - a} of {b - a}, weights {w0:0.00}/{w1:0.00}, mouth {rv.MouthCell >= 0}, junction {rv.JunctionCell >= 0}, params {s0.Param:0.00}/{s1.Param:0.00}, legacy gap {(new Vector3d(s1.Direction.normalized) - new Vector3d(s0.Direction.normalized)).Magnitude * Rm:0} m, segments {q0.Segment}/{q1.Segment}, offsets {q0.OffsetMeters:0}/{q1.OffsetMeters:0} m, nodes {rv.NodeCount}");
                        }
                    }
                }
                if (network.GetRiver(surface.GetSpineRiver(a)).MouthCell < 0) continue;
                oceanRibbons++;
                surface.GetSpineSample(b - 1, out Vector3d end, out _, out _, out _, out _);
                if (planet.Radius * (1f + planet.Height.GetElevation(end)) >= planet.OceanRadius)
                {
                    endsAbove++;
                    if (endsAbove <= 12) TestContext.WriteLine($"end above the ocean: ribbon {r} ({b - a} samples), {(planet.Height.GetElevation(end) - planet.SeaLevel) * Rm:0.0} m above the sea, last weight {LastWeight(surface, b - 1)}, hydrology land cell {hydrology.IsLand(hydrology.Grid.CellFromDirection(end.ToVector3()))}");
                }
            }
            TestContext.WriteLine($"ribbons: consecutive placed samples closer than 1 m: {collapsed}; ocean-bound ribbons {oceanRibbons}, ends above the canonical ocean {endsAbove} (coast extensions {d.CoastRibbons}, unreachable {d.CoastUnreached})");
            // Two consecutive samples may still project onto the same end of a terminal curve where the legacy trajectory
            // overshoots it (a zero-length piece, nothing drawn): bounded, measured, never the mouth extension itself.
            Assert.Less(collapsed, surface.SpineSampleCount * 0.002, "coincident consecutive samples stay rare (zero-length pieces)");
            Assert.LessOrEqual(endsAbove, d.CoastUnreached + 2, "ocean-bound ribbons end under the canonical ocean");
            widths.Sort();
            TestContext.WriteLine($"{network.RiverCount} rivers, {surface.SpineSampleCount} spine samples: snapped {snapped} ({100.0 * snapped / surface.SpineSampleCount:0.0} %), in channels {inChannels}, width within the channel {widthOk}, banks inside {banksInside}; water checked {checkedWater}, as expected {waterOk}, max water depth {maxWater:0.0} m; displayed half width p50 {widths[widths.Count / 2]:0} m p90 {widths[(int)(widths.Count * 0.9)]:0} m max {widths[widths.Count - 1]:0} m; mismatched links {d.MismatchedLinks}");
            surface.Dispose();
            Assert.AreEqual(0, d.MismatchedLinks);
            Assert.Greater(snapped, surface.SpineSampleCount * 0.85, "the river length is snapped onto the channels (density 100 %: the source half cells and the mouth extensions are legacy)");
            Assert.Greater(inChannels, snapped * 0.5, "developed channels under most snapped samples");
            Assert.Greater(widthOk, inChannels * 0.98, "the displayed half width never exceeds the active channel (the nearest-channel query may pick a neighbour at a junction)");
            Assert.AreEqual(inChannels, banksInside, "the banks never exceed the displayed half width");
            Assert.GreaterOrEqual(waterOk, checkedWater * 0.995, "the level-0 water surface is the bed plus the fill depth (the nearest-channel query may read a neighbouring cell's depth at a junction)");
            Assert.LessOrEqual(maxWater, RiverBedMorphology.MaxDepthMeters * 1.3f * Planetwalker.Rendering.RiverSurface.WaterFillShare + 8.05f);
        }

        static string LastWeight(Planetwalker.Rendering.RiverSurface surface, int i) { surface.GetSpineSample(i, out _, out _, out float w, out _, out _); return w.ToString("0.00", Inv); }

        // ------------------------------------------------------------------ cost

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
            PlanetInstance on = Planet(1337), off = Planet(1337, 1f, 0);
            ValleyNetwork net = Network(on);
            double offUs = Math.Min(Time(d => off.Height.GetElevation(d)), Time(d => off.Height.GetElevation(d)));
            double onUs = Math.Min(Time(d => on.Height.GetElevation(d)), Time(d => on.Height.GetElevation(d)));
            double bedUs = Time(d => net.Bed(d, 0f, 1f, out _, out _));
            var sw2 = Stopwatch.StartNew();
            var fresh = new PlanetInstance(new PlanetDefinition { seed = 1337 });
            double planetMs = sw2.Elapsed.TotalMilliseconds;
            sw2.Restart();
            var freshOff = new PlanetInstance(new PlanetDefinition { seed = 1337, riverBeds = 0 });
            double planetOffMs = sw2.Elapsed.TotalMilliseconds;
            ValleyNetwork n310 = Network(Planet(310929));
            TestContext.WriteLine($"canonical elevation: {offUs:0.00} us (G.2B terrain), {onUs:0.00} us with the river beds ({(onUs - offUs) / offUs * 100.0:+0.0;-0.0} %); bed field alone {bedUs:0.00} us; planet construction {planetMs:0} ms with beds (network {Network(fresh).BuildMilliseconds:0} ms, {net.SegmentCount} segments, {net.ChannelCount} channel cells) vs {planetOffMs:0} ms without (network {Network(freshOff).BuildMilliseconds:0} ms); seed 310929: {n310.SegmentCount} segments, {n310.ChannelCount} channels, {n310.BuildMilliseconds:0} ms");
            Assert.Less(net.SegmentCount, 200000);
            Assert.Less(n310.SegmentCount, 200000);
            Assert.Less(Network(fresh).BuildMilliseconds, 8000.0, "Bounded preprocessing (loose: not a benchmark)");
            Assert.Less(onUs, offUs * 2.5 + 3.0, "Loose bound: the bed is a hash lookup and a few segments, not a benchmark");
        }
    }
}
