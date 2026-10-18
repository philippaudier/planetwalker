using System;
using System.Collections.Generic;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Persistence;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Fluvial incision morphology and headwater continuity (V0.2.0G.2B.1), measured on the final authoritative terrain
    /// (not on the formula): channels start from nothing and grow with their drained area, no terminal wall at a head, the
    /// incision's own cross slopes stay bounded, the detail bands have no step across the ranges (the anisotropy swing
    /// that drew near-vertical walls), and the whole channel floor keeps descending.
    /// </summary>
    public class IncisionContinuityTests
    {
        const int Seed = 1337;
        static PlanetInstance on, off;

        static void Ensure()
        {
            if (on != null) return;
            WorldConfiguration c = WorldArchiveTestUtil.Tweaked(Seed, flipFlags: false);
            PlanetDefinition d = c.Terrain.Clone();
            d.seed = Seed; d.surfaceDetail = true; d.regionalLandforms = 1f; d.erosionStrength = 1f; d.erosionModel = 1;
            on = new PlanetInstance(d);
            PlanetDefinition e = d.Clone(); e.erosionStrength = 0f;
            off = new PlanetInstance(e);
        }

        static float HeightMeters(PlanetInstance p, Vector3d d) => (float)((p.Height.GetElevation(d) - p.SeaLevel) * p.Scale.RadiusMeters);

        static (HashSet<int> channels, int[] channelUpstream) Channels(ValleyNetwork net)
        {
            var channels = new HashSet<int>();
            for (int s = 0; s < net.SegmentCount; s++) channels.Add(net.GetSegment(s).Cell);
            var upstream = new int[net.Hydrology.Grid.CellCount];
            foreach (int cell in channels) { int r = net.Hydrology.GetReceiver(cell); if (r >= 0) upstream[r]++; }
            return (channels, upstream);
        }

        [Test]
        public void Headwaters_StartFromNothing_AndGrowWithTheDrainedArea()
        {
            Ensure();
            ValleyNetwork net = on.Height.SurfaceDetail.Landforms.Valleys;
            (HashSet<int> channels, int[] upstream) = Channels(net);
            var smallHeads = new List<float>(); var byCells = new SortedDictionary<int, List<float>>();
            foreach (int cell in channels)
            {
                float inc = net.NodeIncision(cell);
                int cells = (int)Math.Round(net.Hydrology.GetAccumulation(cell) / (net.Hydrology.Grid.TotalArea / net.Hydrology.Grid.CellCount));
                if (upstream[cell] == 0 && cells <= 3) smallHeads.Add(inc);
                int bucket = cells < 4 ? 0 : cells < 8 ? 1 : cells < 16 ? 2 : cells < 32 ? 3 : 4;
                (byCells.TryGetValue(bucket, out List<float> l) ? l : byCells[bucket] = new List<float>()).Add(inc);
            }
            Assert.Greater(smallHeads.Count, 50, "True headwaters (no channel upstream, at most 3 cells)");
            smallHeads.Sort();
            float headP90 = smallHeads[(int)(smallHeads.Count * 0.9)];
            var medians = new List<float>();
            foreach (var pair in byCells) { pair.Value.Sort(); medians.Add(pair.Value[pair.Value.Count / 2]); }
            TestContext.WriteLine($"headwaters (<= 3 cells): incision p50 {smallHeads[smallHeads.Count / 2]:0} m, p90 {headP90:0} m; median incision by drained cells (<4, <8, <16, <32, 32+): {string.Join(" ", medians.ConvertAll(m => m.ToString("0")))} m");
            Assert.Less(headP90, 120f, "A channel head carries little incision (activation from the drained area)");
            for (int i = 1; i < medians.Count; i++) Assert.GreaterOrEqual(medians[i], medians[i - 1] * 0.85f, "Incision grows with the drained area (bucket medians non-decreasing within the height bound)");
            Assert.Greater(medians[medians.Count - 1], 3f * Math.Max(medians[0], 1f), "Trunks are far deeper than headwaters");
        }

        [Test]
        public void NoTerminalWall_TheIncisionTapersAlongHeadCurves()
        {
            Ensure();
            ValleyNetwork net = on.Height.SurfaceDetail.Landforms.Valleys;
            double R = on.Scale.RadiusMeters;
            (HashSet<int> channels, int[] upstream) = Channels(net);
            int heads = 0; float worst = 0f; var seen = new HashSet<int>();
            for (int s = 0; s < net.SegmentCount; s++)
            {
                ValleyNetwork.Segment g = net.GetSegment(s);
                if (!seen.Add(g.Cell) || upstream[g.Cell] != 0) continue;
                heads++;
                // 30 km back along the head curve's first direction, then down the curve: the incision contribution only.
                Vector3d a = g.A, dir = (g.B - g.A).Normalized;
                float previous = float.NaN;
                for (int k = -120; k <= 120; k++)
                {
                    Vector3d d = (a + dir * (k * 250.0 / R)).Normalized;
                    float inc = HeightMeters(on, d) - HeightMeters(off, d);
                    if (!float.IsNaN(previous)) worst = Mathf.Max(worst, Mathf.Abs(inc - previous));
                    previous = inc;
                }
            }
            TestContext.WriteLine($"{heads} channel heads: worst incision change {worst:0} m per 250 m step along the head");
            Assert.Greater(heads, 100);
            Assert.Less(worst, 75f, "No terminal wall: at most a 17-degree ramp of incision along a head (the valley is followed up to its source)");
        }

        [Test]
        public void CrossSections_IncisionSlopesAreBounded_AndTheDetailBandsHaveNoStep()
        {
            Ensure();
            ValleyNetwork net = on.Height.SurfaceDetail.Landforms.Valleys;
            double R = on.Scale.RadiusMeters;
            float worstIncisionSlope = 0f, worstRegionalStep = 0f, worstLocalStep = 0f; int sections = 0;
            Vector3d worstAxis = Vector3d.Up, worstAcross = Vector3d.Right; int worstK = 0;
            for (int s = 0; s < net.SegmentCount; s += Math.Max(1, net.SegmentCount / 300))
            {
                ValleyNetwork.Segment g = net.GetSegment(s);
                if (g.IncisionB < 150f) continue;
                sections++;
                Vector3d axis = g.B, across = Vector3d.Cross((g.B - g.A).Normalized, axis).Normalized;
                float prevInc = float.NaN, prevReg = float.NaN, prevLoc = float.NaN;
                for (int k = -600; k <= 600; k++)
                {
                    Vector3d d = (axis + across * (k * 10.0 / R)).Normalized;
                    on.Height.GetElevation(d, 0f, out TerrainBaseSample ctx);
                    on.Height.SurfaceDetail.Evaluate(d, d.ToVector3(), ctx, 0f, out TerrainDetailSample sm);
                    float inc = (float)(sm.Erosion * R), reg = (float)(sm.Regional * R), loc = (float)(sm.Local * R);
                    if (!float.IsNaN(prevInc))
                    {
                        worstIncisionSlope = Mathf.Max(worstIncisionSlope, Mathf.Abs(inc - prevInc) / 10f);
                        if (Mathf.Abs(reg - prevReg) > worstRegionalStep) { worstRegionalStep = Mathf.Abs(reg - prevReg); worstAxis = axis; worstAcross = across; worstK = k; }
                        worstLocalStep = Mathf.Max(worstLocalStep, Mathf.Abs(loc - prevLoc));
                    }
                    prevInc = inc; prevReg = reg; prevLoc = loc;
                }
            }
            // The worst regional change re-sampled at 1 m: a continuous ramp spreads it, a step keeps it in one meter.
            float worstPerMeter = 0f; float prev1 = float.NaN;
            for (int m = -15; m <= 15; m++)
            {
                Vector3d d = (worstAxis + worstAcross * ((worstK * 10.0 + m) / R)).Normalized;
                on.Height.GetElevation(d, 0f, out TerrainBaseSample ctx);
                on.Height.SurfaceDetail.Evaluate(d, d.ToVector3(), ctx, 0f, out TerrainDetailSample sm);
                float reg = (float)(sm.Regional * R);
                if (!float.IsNaN(prev1)) worstPerMeter = Mathf.Max(worstPerMeter, Mathf.Abs(reg - prev1));
                prev1 = reg;
            }
            TestContext.WriteLine($"{sections} cross-sections (12 km, 10 m steps): worst incision slope {worstIncisionSlope:0.00} (rise/run), worst regional band change {worstRegionalStep:0.0} m / 10 m (at 1 m: {worstPerMeter:0.0} m / m), local {worstLocalStep:0.0} m / 10 m");
            Assert.Greater(sections, 50);
            Assert.Less(worstIncisionSlope, 1.0f, "The incision's own walls stay under 45 degrees (depth at most half the incision half width)");
            Assert.Less(worstRegionalStep, 60f, "No step in the regional band across the ranges (183 m per 10 m before the fix)");
            Assert.Less(worstPerMeter, worstRegionalStep * 0.3f + 1f, "...and the worst change is a ramp spread over the 10 m, not a jump within one meter");
            Assert.Less(worstLocalStep, 40f, "Nor in the local band");
        }

        [Test]
        public void ChannelFloors_DescendOnTheFinalTerrain_FromHeadToOutlet()
        {
            Ensure();
            ValleyNetwork net = on.Height.SurfaceDetail.Landforms.Valleys;
            HydrologyResult h = net.Hydrology;
            (HashSet<int> channels, int[] upstream) = Channels(net);
            int paths = 0, steps = 0, newUphill = 0, deepened = 0;
            foreach (int head in channels)
            {
                if (upstream[head] != 0) continue;
                int cell = head, guard = 0;
                float prevBefore = float.NaN, prevAfter = float.NaN;
                while (cell >= 0 && h.IsLand(cell) && guard++ < 400)
                {
                    var d = new Vector3d(h.Grid.GetCenter(cell));
                    if (net.Nearest(d, out ValleyNetwork.ChannelQuery q) && q.DistanceMeters < q.HalfWidthMeters)
                    {
                        float before = HeightMeters(off, q.Closest), after = HeightMeters(on, q.Closest);
                        if (!float.IsNaN(prevBefore))
                        {
                            steps++;
                            float dropBefore = prevBefore - before, dropAfter = prevAfter - after;
                            if (dropBefore > 0f && dropAfter < -0.02f * dropBefore) newUphill++;
                        }
                        if (after < before - 50f) deepened++;
                        prevBefore = before; prevAfter = after;
                    }
                    cell = h.GetReceiver(cell);
                }
                if (guard > 2) paths++;
            }
            TestContext.WriteLine($"{paths} head-to-outlet paths, {steps} node steps, new uphill steps {newUphill}, nodes deepened by over 50 m: {deepened}");
            Assert.Greater(paths, 100);
            Assert.AreEqual(0, newUphill, "The incision never turns a descending floor into an ascending one, on whole paths");
            Assert.Greater(deepened, 200);
        }
    }
}
