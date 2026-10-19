using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// Drainage network of the mountain ranges (V0.2.0G.1.1): the open, converging valley paths the regional landforms
    /// carve. Built once per planet from the planetary terrain e0 alone, then a pure lookup. V0.2.0G.2E extends it to the
    /// whole river network (<see cref="WholeNetwork"/>): one reference hydrology and a carved bed along every channel.
    /// </summary>
    /// <remarks>
    /// V0.2.0G.1 drew valleys along the zero lines of a smooth noise: level sets of a continuous field on a sphere are
    /// closed curves, hence rings, ovals and horseshoes with no downhill direction. Here the valleys follow drainage:
    /// <list type="number">
    /// <item>e0 (and its mountain influence) sampled at the centers of a <see cref="GridResolution"/> cube grid (~78 km
    /// cells at Earth scale), in parallel.</item>
    /// <item>The existing <see cref="HydrologySystem"/> on that field: Priority-Flood (depressions drain to their spill
    /// point) and steepest descent: every cell has one receiver, strictly lower on the filled surface, so the network is
    /// a forest of trees that end at the sea or at the endorheic outlet. No cycle is possible.</item>
    /// <item>Channels: land cells inside the ranges (influence at least <see cref="MinInfluence"/>) that drain at least
    /// <see cref="MinCells"/> cells. Each channel cell joins its receiver; the polyline through the cell centers is
    /// smoothed into quadratic curves through the edge midpoints (a tributary's curve ends where its receiver's begins:
    /// junctions are shared points), each curve split into <see cref="Subdivisions"/> segments.</item>
    /// <item>Width and depth grow with the drained area (narrow tributaries, broad trunk valleys) and are interpolated
    /// along each segment: continuous along the paths.</item>
    /// <item>A 3D hash grid indexes the segments by every cell their reach overlaps: a lookup reads one cell.</item>
    /// </list>
    /// Pure data after construction (immutable, thread safe), deterministic for a seed. Not iterated per vertex: a query
    /// is a hash read and a distance to a handful of segments. The grid is far coarser than the valleys: e0 has no relief
    /// finer than ~200 km, so its drainage at 78 km cells is the regional structure the valleys follow.
    /// <para>
    /// V0.2.0G.2E, whole-network mode: the hydrology is computed on the very field the river network's hydrology reads
    /// (the band-limited legacy elevation at the reference resolution, supplied by the generator), so the carved
    /// drainage and the displayed rivers are one hydrology; every cell a river can occupy (the minimum river threshold)
    /// carries a channel curve (the topological channel every river snaps onto), while the carved geometry is progressive
    /// (<see cref="RiverBedMorphology"/>: nothing at one drained cell, a developed channel at a few). Mountain valleys
    /// (G.1.1) and the fluvial incision (G.2B) keep their own perimeter (the ranges); the bed adds the active channel,
    /// its banks and, in low-gradient reaches, a floodplain, everywhere on land.
    /// </para>
    /// </remarks>
    public sealed class ValleyNetwork
    {
        public const int GridResolution = 128;
        public const float MinInfluence = 0.08f;
        public const int MinCells = 2;
        public const int Subdivisions = 4;
        const double HashCell = 0.01; // unit-sphere units (~64 km at Earth scale)
        const double NarrowHalfWidthMeters = 2500.0, BroadHalfWidthMeters = 11000.0;
        const double MeanderMeters = 3500.0, MeanderCellMeters = 25000.0;
        const int MeanderStream = 48;

        // Fluvial incision (V0.2.0G.2B): per channel node, I = K * a^m * S / (S + S0), a = normalized drained area
        // (log2 from 2 to 128 cells), S = channel gradient on the filled surface (rise over run), K = regional erodibility
        // (0.75 .. 1.25 from the planet's terrain character), scaled to MaxIncisionMeters. Propagated downstream with a
        // decay so a tributary's floor never steps up into a shallower trunk at a confluence.
        public const float MaxIncisionMeters = 1500f;
        const float IncisionAreaExponent = 0.5f;
        // Headwater initiation (V0.2.0G.2B.1): the incision activates smoothly with the drained area, from nothing at
        // 2 cells (a channel head) to full at ~9 cells (a = 0.35), and a head curve starts at zero depth; and a channel
        // is never deeper than half its incision half width (walls under ~38 degrees at the terrain scale).
        const float ActivationEnd = 0.35f;
        const float MaxDepthPerHalfWidth = 0.5f;
        const float GradientHalfSaturation = 0.03f;
        const float DownstreamDecay = 0.85f;
        const double IncisionNarrowHalfWidthMeters = 1200.0, IncisionBroadHalfWidthMeters = 5000.0;
        // Bed (V0.2.0G.2E): downstream regularization of the channel depth (a trunk at least this deep relative to the
        // tributary joining it), and the Newton inversion of the meander for the terrain-space curvature.
        const float BedDepthDecay = 0.9f;
        const int InversionIterations = 12;
        const double InversionTolerance = 1e-9; // unit-sphere units (~6 mm at Earth scale)
        const double JacobianStep = 1e-6;

        public struct Segment
        {
            public Vector3d A, B;
            /// <summary>Half width and depth (meters) at A and B; broadness (0 V .. 1 U) and side skew of the valley.</summary>
            public float HalfWidthA, HalfWidthB, DepthA, DepthB, Broad, Skew;
            /// <summary>Drained grid cells at the downstream node of the curve.</summary>
            public float Cells;
            /// <summary>Filled e0 at the segment's upstream and downstream ends (diagnostics: downhill direction).</summary>
            public double FilledA, FilledB;
            /// <summary>Fluvial incision (meters, at strength 1) and its half width (meters) at A and B (V0.2.0G.2B).</summary>
            public float IncisionA, IncisionB, IncisionHalfWidthA, IncisionHalfWidthB;
            /// <summary>Channel gradient (rise over run on the filled surface) and erodibility at the curve's node.</summary>
            public float Gradient, Erodibility;
            /// <summary>The channel cell this curve belongs to (hydrology grid index).</summary>
            public int Cell;
            /// <summary>Mountain valley weight at A and B (V0.2.0G.2E: 1 inside the ranges, 0 on a bed-only channel; always 1 before).</summary>
            public float ValleyA, ValleyB;
            /// <summary>Bed (V0.2.0G.2E): active channel half width and thalweg depth (meters, strength 1) at A and B.</summary>
            public float ChannelHalfWidthA, ChannelHalfWidthB, ChannelDepthA, ChannelDepthB;
            /// <summary>Bed: bank width per side, floodplain half width and lowering, curvature asymmetry and development at A and B.</summary>
            public float BankWidthA, BankWidthB, FloodplainHalfWidthA, FloodplainHalfWidthB, FloodplainDepthA, FloodplainDepthB, AsymmetryA, AsymmetryB, DevelopmentA, DevelopmentB;
            /// <summary>Bed: signed curvature of the terrain-space axis (1/m, positive turning left) at A and B.</summary>
            public float CurvatureA, CurvatureB;
        }

        /// <summary>The drainage corridor relevant at a direction (V0.2.0G.2B query).</summary>
        public struct ChannelQuery
        {
            public int Segment;
            /// <summary>Distance to the channel axis (meters) and position along the segment (0 at A, 1 at B).</summary>
            public float DistanceMeters, T;
            /// <summary>Closest point on the axis and the local flow direction (unit, A towards B: downhill).</summary>
            public Vector3d Closest, Flow;
            /// <summary>Drained cells at the corridor's node, valley half width (m), G.1.1 carve capacity (m).</summary>
            public float Cells, HalfWidthMeters, DepthMeters;
            /// <summary>Filled reference elevation of the channel there (fraction of the radius) and its gradient (rise over run).</summary>
            public double FilledElevation;
            public float Gradient;
            /// <summary>Incision (m, strength 1) and incision half width (m) there, and the erodibility of the node.</summary>
            public float IncisionMeters, IncisionHalfWidthMeters, Erodibility;
            /// <summary>Mountain valley weight there (V0.2.0G.2E).</summary>
            public float Valley;
            /// <summary>Bed there (V0.2.0G.2E, meters at strength 1): active channel, banks, floodplain, development, curvature (1/m).</summary>
            public float ChannelHalfWidthMeters, ChannelDepthMeters, BankWidthMeters, FloodplainHalfWidthMeters, FloodplainDepthMeters, Development, Curvature;
        }

        readonly Segment[] segments;
        readonly float[] nodeIncision;
        readonly ChannelSection[] nodeSection;
        // Segments of a hydrology cell's channel node are [segmentStartOfCell[cell], segmentStartOfCell[cell + 1]) (V0.2.0G.2D).
        readonly int[] segmentStartOfCell;
        readonly Dictionary<long, int[]> hash = new Dictionary<long, int[]>();
        readonly double radiusMeters;
        readonly int meanderSeedX, meanderSeedY, meanderSeedZ;
        readonly double meanderFrequency, meanderAmplitude;

        /// <summary>
        /// Builds the network. <paramref name="sample"/> returns e0 and its context at a unit direction (thread safe);
        /// <paramref name="maxDepthMeters"/> bounds the valley depth.
        /// </summary>
        /// <param name="erodibility">Regional erodibility signal in about -1 .. 1 (the terrain character), or null (uniform).</param>
        /// <param name="hydrologyElevation">
        /// V0.2.0G.2E: the elevation field the drainage is computed on (the band-limited legacy elevation at the reference
        /// resolution, thread safe), or null for e0 itself (the V0.2.0G.1.1 / G.2B network).
        /// </param>
        /// <param name="wholeNetwork">V0.2.0G.2E: channels on every cell a river can occupy, with a carved bed (false: the ranges only).</param>
        public ValleyNetwork(Func<Vector3, TerrainBaseSample> sample, float seaLevel, double radiusMeters, int seed, float maxDepthMeters,
            Func<Vector3, float> erodibility = null, Func<Vector3, float> hydrologyElevation = null, bool wholeNetwork = false)
        {
            this.radiusMeters = radiusMeters;
            WholeNetwork = wholeNetwork;
            meanderSeedX = PlanetNoise.DeriveSeed(seed, MeanderStream);
            meanderSeedY = PlanetNoise.DeriveSeed(seed, MeanderStream + 1);
            meanderSeedZ = PlanetNoise.DeriveSeed(seed, MeanderStream + 2);
            meanderFrequency = radiusMeters / MeanderCellMeters;
            meanderAmplitude = MeanderMeters / radiusMeters;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var grid = new HydrologyGrid(GridResolution);
            int count = grid.CellCount;
            var elevation = new float[count];
            var influence = new float[count];
            var core = new float[count];
            Parallel.For(0, count, cell =>
            {
                Vector3 center = grid.GetCenter(cell);
                TerrainBaseSample s = sample(center);
                elevation[cell] = hydrologyElevation != null ? hydrologyElevation(center) : s.Elevation;
                influence[cell] = s.Influence;
                core[cell] = s.Core;
            });
            HydrologyResult hydrology = new HydrologySystem().Compute(grid, d => elevation[grid.CellFromDirection(d)], seaLevel);
            Hydrology = hydrology;

            double meanCellArea = grid.TotalArea / count;
            var channel = new bool[count];
            var valley = new bool[count];
            var cells = new float[count];
            var inflow = new int[count];
            for (int cell = 0; cell < count; cell++)
            {
                int receiver = hydrology.GetReceiver(cell);
                if (receiver >= 0 && hydrology.IsLand(cell)) inflow[receiver]++;
            }
            for (int cell = 0; cell < count; cell++)
            {
                cells[cell] = (float)(hydrology.GetAccumulation(cell) / meanCellArea);
                valley[cell] = hydrology.IsLand(cell) && influence[cell] >= MinInfluence && cells[cell] >= MinCells;
                // Whole network: every cell the river network can select at its minimum threshold (density 100 %) that
                // receives a drainage path (a cell draining nothing but itself is a river source at density 100 %: it has
                // no bed, its half cell keeps the legacy trajectory, and the network stays bounded).
                channel[cell] = wholeNetwork
                    ? valley[cell] || (inflow[cell] > 0 && hydrology.IsRiverCandidate(cell, HydrologySettings.MinRiverNetworkThreshold))
                    : valley[cell];
            }
            // Upstream channel cells of each channel cell.
            var upstream = new List<int>[count];
            for (int cell = 0; cell < count; cell++)
            {
                int receiver = hydrology.GetReceiver(cell);
                if (channel[cell] && receiver >= 0 && channel[receiver])
                {
                    (upstream[receiver] ??= new List<int>()).Add(cell);
                }
            }

            // Fluvial incision per valley node (V0.2.0G.2B), then propagated downstream. Gradient and erodibility for every
            // channel node (the bed reads them too).
            var incision = new float[count];
            var incisionWidth = new float[count];
            var gradient = new float[count];
            var erodibilityOf = new float[count];
            for (int b = 0; b < count; b++)
            {
                if (!channel[b])
                {
                    continue;
                }
                int c = hydrology.GetReceiver(b);
                float slope = 0f;
                if (c >= 0)
                {
                    double run = (new Vector3d(grid.GetCenter(b)) - new Vector3d(grid.GetCenter(c))).Magnitude * radiusMeters;
                    double drop = (hydrology.GetFilledElevation(b) - (hydrology.IsLand(c) ? hydrology.GetFilledElevation(c) : seaLevel)) * radiusMeters;
                    slope = run > 0.0 ? (float)Math.Max(0.0, drop / run) : 0f;
                }
                gradient[b] = slope;
                float k = erodibility != null ? 0.75f + 0.5f * Mathf.Clamp01(0.5f + 0.5f * erodibility(grid.GetCenter(b))) : 1f;
                erodibilityOf[b] = k;
                if (!valley[b])
                {
                    continue;
                }
                float a = Mathf.Clamp01(Mathf.Log(Mathf.Max(cells[b], 1f) / MinCells, 2f) / 6f);
                float s = slope / (slope + GradientHalfSaturation);
                float activation = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(a / ActivationEnd));
                incisionWidth[b] = (float)(IncisionNarrowHalfWidthMeters + (IncisionBroadHalfWidthMeters - IncisionNarrowHalfWidthMeters) * a) * Mathf.Lerp(1f, 0.6f, s);
                incision[b] = Mathf.Min(MaxIncisionMeters * k * activation * Mathf.Pow(a, IncisionAreaExponent) * s, MaxDepthPerHalfWidth * incisionWidth[b]);
            }
            // Downstream regularization: highest cells first (reverse flood order), each pushing its depth, decayed, to
            // its receiver. A trunk is at least 85 % as deep as the tributary joining it: no uphill step at a confluence.
            for (int k = hydrology.OrderCount - 1; k >= 0; k--)
            {
                int cell = hydrology.GetOrderedCell(k);
                int receiver = hydrology.GetReceiver(cell);
                if (valley[cell] && receiver >= 0 && valley[receiver] && incision[cell] * DownstreamDecay > incision[receiver])
                {
                    incisionWidth[receiver] = Math.Max(incisionWidth[receiver], incisionWidth[cell]);
                    incision[receiver] = Math.Min(incision[cell] * DownstreamDecay, MaxDepthPerHalfWidth * incisionWidth[receiver]);
                }
            }
            nodeIncision = incision;

            // The main upstream of each channel cell (the largest drained area; lowest index on ties): the path the
            // curvature stencil and the gradient smoothing follow through the node.
            var mainUpstream = new int[count];
            for (int cell = 0; cell < count; cell++)
            {
                mainUpstream[cell] = -1;
                List<int> ups = upstream[cell];
                if (ups == null) continue;
                foreach (int a in ups)
                {
                    if (mainUpstream[cell] < 0 || cells[a] > cells[mainUpstream[cell]]) mainUpstream[cell] = a;
                }
            }

            // Bed morphology per channel node (V0.2.0G.2E): state (gradient smoothed along the path) -> section, then the
            // downstream regularization (a trunk never narrower or much shallower than a tributary joining it).
            nodeSection = new ChannelSection[count];
            if (wholeNetwork)
            {
                for (int b = 0; b < count; b++)
                {
                    if (!channel[b]) continue;
                    int c = hydrology.GetReceiver(b), a = mainUpstream[b];
                    float g = 0.5f * gradient[b]
                        + 0.25f * (a >= 0 ? gradient[a] : gradient[b])
                        + 0.25f * (c >= 0 && channel[c] ? gradient[c] : gradient[b]);
                    // Headroom above the filled surface: inside a filled depression the ground is under the fill level already
                    // (a dry basin floor, or a lake bed): unlimited; on its shore (the cell drains into the depression) the
                    // height above that level; elsewhere unlimited.
                    bool shore = !hydrology.IsInDepression(b) && c >= 0 && hydrology.IsLand(c) && hydrology.IsInDepression(c);
                    float headroom = shore ? (float)Math.Max(0.0, (hydrology.GetElevation(b) - hydrology.GetFilledElevation(c)) * radiusMeters) : 0f;
                    nodeSection[b] = RiverBedMorphology.Evaluate(new ChannelState
                    {
                        Cells = cells[b], Gradient = g, Influence = influence[b], Erodibility = erodibilityOf[b], Shore = shore, HeadroomMeters = headroom,
                    });
                }
                for (int k = hydrology.OrderCount - 1; k >= 0; k--)
                {
                    int cell = hydrology.GetOrderedCell(k);
                    int receiver = hydrology.GetReceiver(cell);
                    if (!channel[cell] || receiver < 0 || !channel[receiver]) continue;
                    ref ChannelSection up = ref nodeSection[cell];
                    ref ChannelSection down = ref nodeSection[receiver];
                    down.HalfWidth = Math.Max(down.HalfWidth, up.HalfWidth);
                    down.Depth = Math.Max(down.Depth, BedDepthDecay * up.Depth);
                    down.BankWidth = Math.Max(down.BankWidth, up.BankWidth);
                    down.FloodplainHalfWidth = Math.Max(down.FloodplainHalfWidth, up.FloodplainHalfWidth);
                    down.FloodplainDepth = Math.Max(down.FloodplainDepth, up.FloodplainDepth);
                    down.Development = Math.Max(down.Development, up.Development);
                    RiverBedMorphology.Bound(ref down);
                }
            }

            var list = new List<Segment>();
            segmentStartOfCell = new int[count + 1];
            // First segment of the curve a channel cell's path continues into at its receiver (the curve of the receiver
            // from that cell), and of each cell's main curve: the curvature stencil crosses the junctions through them.
            var curveFromCell = new int[count];
            var mainCurve = new int[count];
            for (int i = 0; i < count; i++) { curveFromCell[i] = -1; mainCurve[i] = -1; }
            for (int b = 0; b < count; b++)
            {
                segmentStartOfCell[b] = list.Count;
                if (!channel[b])
                {
                    continue;
                }
                Vector3d pb = new Vector3d(grid.GetCenter(b));
                int c = hydrology.GetReceiver(b);
                bool downstream = c >= 0 && hydrology.IsLand(c);
                // Downstream end: the midpoint to the receiver when the path continues as a channel, the receiver itself
                // when it leaves the ranges (the valley fades there with the influence), the node itself at the sea
                // (whole network: the sea receiver's center, the bed dying out with the height above the sea).
                Vector3d end = !downstream ? (wholeNetwork && c >= 0 ? new Vector3d(grid.GetCenter(c)) : pb)
                    : channel[c] ? Mid(pb, new Vector3d(grid.GetCenter(c))) : new Vector3d(grid.GetCenter(c));
                Valley(cells[b], core[b], maxDepthMeters, out float widthB, out float depthB, out float broadB);
                float widthEnd = widthB, depthEnd = depthB;
                // Valley weights: 1 everywhere before V0.2.0G.2E (every channel is a valley; the terrain is bit-identical),
                // tapering between valley and bed-only cells in whole-network mode.
                float valleyB = !wholeNetwork || valley[b] ? 1f : 0f, valleyEnd = valleyB;
                if (downstream)
                {
                    Valley(cells[c], core[c], maxDepthMeters, out float widthC, out float depthC, out _);
                    widthEnd = 0.5f * (widthB + widthC);
                    depthEnd = 0.5f * (depthB + depthC);
                    if (wholeNetwork) valleyEnd = 0.5f * (valleyB + (valley[c] ? 1f : 0f));
                }
                float skew = 0.25f * (2f * PlanetNoise.Hash01(seed, 1000003 + b) - 1f);
                double filledB = hydrology.GetFilledElevation(b), filledEnd = downstream ? 0.5 * (filledB + hydrology.GetFilledElevation(c)) : filledB;
                float incB = incision[b], incEnd = downstream && valley[c] ? 0.5f * (incB + incision[c]) : downstream ? incB * 0.5f : incB;
                float incWB = incisionWidth[b], incWEnd = downstream && valley[c] ? 0.5f * (incWB + incisionWidth[c]) : incWB;
                var node = new NodeInfo { Gradient = gradient[b], Erodibility = erodibilityOf[b], Cell = b, ValleyB = valleyB, ValleyEnd = valleyEnd };
                ChannelSection secB = nodeSection[b];
                ChannelSection secEnd = downstream && channel[c] ? Average(secB, nodeSection[c]) : downstream ? Average(secB, default) : secB;
                List<int> ups = upstream[b];
                if (ups == null)
                {
                    // A channel head: a straight start from the node, the valley and the incision tapering in from zero
                    // (V0.2.0G.2B.1: no terminal wall; a gorge can be followed up to its source).
                    mainCurve[b] = list.Count;
                    Add(list, pb, end, end, widthB * 0.6f, widthEnd, 0f, depthEnd, broadB, skew, cells[b], filledB, filledEnd, true,
                        0f, incEnd, incWB * 0.8f, incWEnd, node, default, secEnd);
                    continue;
                }
                foreach (int a in ups)
                {
                    Vector3d pa = new Vector3d(grid.GetCenter(a));
                    Valley(cells[a], core[a], maxDepthMeters, out float widthA, out float depthA, out _);
                    double filledStart = 0.5 * (hydrology.GetFilledElevation(a) + filledB);
                    node.ValleyA = wholeNetwork ? 0.5f * ((valley[a] ? 1f : 0f) + valleyB) : 1f;
                    curveFromCell[a] = list.Count;
                    if (a == mainUpstream[b]) mainCurve[b] = list.Count;
                    Add(list, Mid(pa, pb), pb, end, 0.5f * (widthA + widthB), widthEnd, 0.5f * (depthA + depthB), depthEnd, broadB, skew, cells[b], filledStart, filledEnd, false,
                        0.5f * (incision[a] + incB), incEnd, 0.5f * (incisionWidth[a] + incWB), incWEnd, node, Average(nodeSection[a], secB), secEnd);
                }
            }
            segments = list.ToArray();
            segmentStartOfCell[count] = list.Count;

            if (wholeNetwork)
            {
                ComputeCurvature(hydrology, channel, mainCurve, curveFromCell);
            }

            // Hash grid. Legacy mode keeps its global reach (bit-identical lookups); whole-network mode hashes each segment
            // by its own reach (bed-only channels are narrow: smaller buckets on the plains).
            double globalReach = (Math.Max(BroadHalfWidthMeters, IncisionBroadHalfWidthMeters) + MeanderMeters) / radiusMeters;
            var cellsOf = new Dictionary<long, List<int>>();
            for (int s = 0; s < segments.Length; s++)
            {
                double reach = wholeNetwork ? Reach(segments[s]) : globalReach;
                Vector3d sa = segments[s].A, sb = segments[s].B;
                var lo = new Vector3d(Math.Min(sa.x, sb.x), Math.Min(sa.y, sb.y), Math.Min(sa.z, sb.z));
                var hi = new Vector3d(Math.Max(sa.x, sb.x), Math.Max(sa.y, sb.y), Math.Max(sa.z, sb.z));
                long x0 = Cell(lo.x - reach), x1 = Cell(hi.x + reach), y0 = Cell(lo.y - reach), y1 = Cell(hi.y + reach), z0 = Cell(lo.z - reach), z1 = Cell(hi.z + reach);
                for (long x = x0; x <= x1; x++)
                for (long y = y0; y <= y1; y++)
                for (long z = z0; z <= z1; z++)
                {
                    long key = Key(x, y, z);
                    if (!cellsOf.TryGetValue(key, out List<int> bucket))
                    {
                        cellsOf[key] = bucket = new List<int>();
                    }
                    bucket.Add(s);
                }
            }
            foreach (KeyValuePair<long, List<int>> pair in cellsOf)
            {
                hash[pair.Key] = pair.Value.ToArray();
            }
            ChannelCount = 0;
            ValleyCount = 0;
            for (int cell = 0; cell < count; cell++)
            {
                if (channel[cell]) ChannelCount++;
                if (valley[cell]) ValleyCount++;
            }
            BuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        }

        /// <summary>Reach of a segment's hash footprint (unit-sphere units): the widest of its features, in meander space.</summary>
        double Reach(in Segment g)
        {
            double valleyReach = Math.Max(g.ValleyA, g.ValleyB) > 0f
                ? Math.Max(Math.Max(g.HalfWidthA, g.HalfWidthB) * 1.25, Math.Max(g.IncisionHalfWidthA, g.IncisionHalfWidthB)) : 0.0;
            double bedReach = Math.Max(
                Math.Max(g.FloodplainHalfWidthA, g.FloodplainHalfWidthB) * (1.0 + RiverBedMorphology.MarginShare),
                Math.Max(g.ChannelHalfWidthA, g.ChannelHalfWidthB) * (1.0 + RiverBedMorphology.ThalwegShift) + Math.Max(g.BankWidthA, g.BankWidthB) * (1.0 + RiverBedMorphology.InnerBankShare));
            return (Math.Max(valleyReach, bedReach) + 1.0) / radiusMeters; // + 1 m: the lookup never misses a boundary case
        }

        /// <summary>
        /// Signed curvature of the terrain-space axis at every segment end (V0.2.0G.2E): the segments live in meander
        /// space, so each end is first mapped to the terrain (meander inverted), then a three-point stencil along the
        /// path (through the junctions: the main curve of the upstream cell before a curve, the receiver's curve from
        /// this cell after it) gives the turning angle per length. A shared junction point gets one value for every
        /// curve meeting there: the asymmetry is continuous along the rivers.
        /// </summary>
        void ComputeCurvature(HydrologyResult hydrology, bool[] channel, int[] mainCurve, int[] curveFromCell)
        {
            int n = segments.Length;
            // Terrain-space points at each segment's ends and at a third and two thirds along its chord: the stencil spans
            // ~3 km (the meander winds at a 25 km wavelength with a 3.5 km amplitude: its bends have radii of a few km,
            // which a 10 km stencil would flatten five times over).
            var tA = new Vector3d[n];
            var tB = new Vector3d[n];
            var t1 = new Vector3d[n];
            var t2 = new Vector3d[n];
            Parallel.For(0, n, s =>
            {
                Vector3d a = segments[s].A, b = segments[s].B;
                tA[s] = Invert(a);
                tB[s] = Invert(b);
                t1[s] = Invert((a * (2.0 / 3.0) + b * (1.0 / 3.0)).Normalized);
                t2[s] = Invert((a * (1.0 / 3.0) + b * (2.0 / 3.0)).Normalized);
            });
            // Curves are Subdivisions consecutive segments; a curve's first segment index is a multiple of Subdivisions
            // within its cell block only in the sense of construction order: every curve has exactly Subdivisions segments.
            for (int s = 0; s < n; s++)
            {
                int first = s - (s % Subdivisions), last = first + Subdivisions - 1;
                int cell = segments[s].Cell;
                float curvatureA, curvatureB;
                if (s > first)
                {
                    curvatureA = Turning(t2[s - 1], tA[s], t1[s]);
                }
                else
                {
                    // Curve start: the main curve of the upstream cell ends here (a head curve starts straight: 0).
                    int up = UpstreamOfCurve(first, cell, hydrology, channel, curveFromCell);
                    curvatureA = up >= 0 && mainCurve[up] >= 0 ? Turning(t2[mainCurve[up] + Subdivisions - 1], tA[s], t1[s]) : 0f;
                }
                if (s < last)
                {
                    curvatureB = Turning(t2[s], tB[s], t1[s + 1]);
                }
                else
                {
                    // Curve end (the shared junction point): the main curve of this cell before it, the receiver's curve
                    // from this cell after it; one value for every curve of the cell.
                    int main = mainCurve[cell];
                    int next = curveFromCell[cell];
                    curvatureB = main >= 0 && next >= 0 ? Turning(t2[main + Subdivisions - 1], tB[s], t1[next]) : 0f;
                }
                segments[s].CurvatureA = curvatureA;
                segments[s].CurvatureB = curvatureB;
            }
        }

        /// <summary>The upstream cell whose path a curve (first segment index) continues, or -1 for a head curve.</summary>
        static int UpstreamOfCurve(int first, int cell, HydrologyResult hydrology, bool[] channel, int[] curveFromCell)
        {
            // curveFromCell[a] == first exactly for the upstream a this curve comes from (a has one receiver).
            HydrologyGrid grid = hydrology.Grid;
            for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
            {
                int a = grid.GetNeighbor(k);
                if (channel[a] && hydrology.GetReceiver(a) == cell && curveFromCell[a] == first) return a;
            }
            return -1;
        }

        /// <summary>Turning angle per length (1/m, positive turning left seen from above) at p1 between p0 -> p1 and p1 -> p2 (terrain space).</summary>
        float Turning(Vector3d p0, Vector3d p1, Vector3d p2)
        {
            Vector3d up = p1.Normalized;
            Vector3d c1 = p1 - p0, c2 = p2 - p1;
            c1 -= up * Vector3d.Dot(c1, up);
            c2 -= up * Vector3d.Dot(c2, up);
            double l1 = c1.Magnitude, l2 = c2.Magnitude;
            if (l1 <= 0.0 || l2 <= 0.0)
            {
                return 0f;
            }
            double angle = Math.Atan2(Vector3d.Dot(Vector3d.Cross(c1, c2), up), Vector3d.Dot(c1, c2));
            return (float)(angle / (0.5 * (l1 + l2) * radiusMeters));
        }

        /// <summary>The drainage analysis of e0 the network comes from (diagnostics, tests).</summary>
        public HydrologyResult Hydrology { get; }

        /// <summary>V0.2.0G.2E: channels on the whole river network with a carved bed (false: the ranges' valleys only).</summary>
        public bool WholeNetwork { get; }

        public int ChannelCount { get; }

        /// <summary>Channel cells inside the ranges (the mountain valleys; equals <see cref="ChannelCount"/> before V0.2.0G.2E).</summary>
        public int ValleyCount { get; }

        public int SegmentCount => segments.Length;

        /// <summary>Regularized incision (meters, strength 1) at a hydrology grid cell's node (0 off the channels).</summary>
        public float NodeIncision(int cell) => nodeIncision[cell];

        /// <summary>Bed section of a hydrology grid cell's node (V0.2.0G.2E; default off the channels or before G.2E).</summary>
        public ChannelSection NodeSection(int cell) => nodeSection[cell];

        public Segment GetSegment(int index) => segments[index];
        public double BuildMilliseconds { get; }

        /// <summary>
        /// Segments carved for a hydrology cell's channel node (V0.2.0G.2D): one curve per upstream channel (or the head
        /// start), each in <see cref="Subdivisions"/> chords, all ending at the same downstream point. Empty for a cell
        /// without a channel. Segment ends live in meander space: see <see cref="Meander"/>.
        /// </summary>
        public void GetCellSegments(int cell, out int start, out int end)
        {
            start = segmentStartOfCell[cell];
            end = segmentStartOfCell[cell + 1];
        }

        /// <summary>True when the hydrology cell carries a carved channel (a node of this network).</summary>
        public bool IsChannel(int cell) => segmentStartOfCell[cell + 1] > segmentStartOfCell[cell];

        /// <summary>Amplitude of the meander displacement (meters): the farthest a carved axis lies from its chord.</summary>
        public static double MeanderAmplitudeMeters => MeanderMeters;

        /// <summary>Narrowest valley half width (meters): the band limit and LOD margin reference.</summary>
        public static double MinHalfWidthMeters => NarrowHalfWidthMeters * 0.6;

        /// <summary>Narrowest incision half width (meters): its band limit and LOD margin reference (V0.2.0G.2B).</summary>
        public static double MinIncisionHalfWidthMeters => IncisionNarrowHalfWidthMeters * 0.6 * 0.8;

        /// <summary>Incision (meters, strength 1) a sampling at a spacing (radians) cannot resolve: the LOD margin term.</summary>
        public float UnresolvedIncisionMeters(float spacing) => MaxIncisionMeters * (1f - BandWeight(MinIncisionHalfWidthMeters * 2.0, spacing));

        /// <summary>Bed depth (meters, strength 1) a sampling at a spacing (radians) cannot resolve (V0.2.0G.2E; 0 without beds).</summary>
        public float UnresolvedBedMeters(float spacing) => WholeNetwork ? RiverBedMorphology.UnresolvedMeters(spacing * radiusMeters) : 0f;

        struct NodeInfo { public float Gradient, Erodibility, ValleyA, ValleyB, ValleyEnd; public int Cell; }

        static void Valley(float cells, float core, float maxDepthMeters, out float halfWidth, out float depth, out float broad)
        {
            // Drained area from 2 cells (a tributary) to ~64 (a trunk valley): narrow V to broad U.
            float t = Mathf.Clamp01(Mathf.Log(Mathf.Max(cells, 1f) / MinCells, 2f) / 5f);
            halfWidth = (float)(NarrowHalfWidthMeters + (BroadHalfWidthMeters - NarrowHalfWidthMeters) * t);
            broad = Mathf.SmoothStep(0f, 1f, t) * Mathf.Lerp(1f, 0.7f, core);
            float wallShare = Mathf.Lerp(0.55f, 0.38f, broad);
            depth = Mathf.Min(maxDepthMeters, halfWidth * wallShare) * Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(core + t * 0.5f));
        }

        static ChannelSection Average(in ChannelSection a, in ChannelSection b)
        {
            return new ChannelSection
            {
                Development = 0.5f * (a.Development + b.Development),
                Rocky = 0.5f * (a.Rocky + b.Rocky), Alluvial = 0.5f * (a.Alluvial + b.Alluvial),
                HalfWidth = 0.5f * (a.HalfWidth + b.HalfWidth), Depth = 0.5f * (a.Depth + b.Depth),
                BankWidth = 0.5f * (a.BankWidth + b.BankWidth),
                FloodplainHalfWidth = 0.5f * (a.FloodplainHalfWidth + b.FloodplainHalfWidth), FloodplainDepth = 0.5f * (a.FloodplainDepth + b.FloodplainDepth),
                Asymmetry = 0.5f * (a.Asymmetry + b.Asymmetry),
            };
        }

        static Vector3d Mid(Vector3d a, Vector3d b) => (a + b).Normalized;

        /// <summary>Quadratic curve start -> end with control point <paramref name="control"/>, split into segments.</summary>
        void Add(List<Segment> list, Vector3d start, Vector3d control, Vector3d end, float w0, float w1, float d0, float d1,
            float broad, float skew, float cells, double f0, double f1, bool straight, float i0, float i1, float iw0, float iw1, in NodeInfo node,
            in ChannelSection s0, in ChannelSection s1)
        {
            Vector3d previous = start;
            float v0 = straight ? node.ValleyB : node.ValleyA, v1 = node.ValleyEnd;
            for (int k = 1; k <= Subdivisions; k++)
            {
                double t = (double)k / Subdivisions, t0 = (double)(k - 1) / Subdivisions;
                float ft = (float)t, ft0 = (float)t0;
                Vector3d p = straight ? (start * (1.0 - t) + end * t).Normalized
                    : (start * ((1.0 - t) * (1.0 - t)) + control * (2.0 * (1.0 - t) * t) + end * (t * t)).Normalized;
                list.Add(new Segment
                {
                    A = previous, B = p,
                    HalfWidthA = (float)(w0 + (w1 - w0) * t0), HalfWidthB = (float)(w0 + (w1 - w0) * t),
                    DepthA = (float)(d0 + (d1 - d0) * t0), DepthB = (float)(d0 + (d1 - d0) * t),
                    Broad = broad, Skew = skew, Cells = cells,
                    FilledA = f0 + (f1 - f0) * t0, FilledB = f0 + (f1 - f0) * t,
                    IncisionA = (float)(i0 + (i1 - i0) * t0), IncisionB = (float)(i0 + (i1 - i0) * t),
                    IncisionHalfWidthA = (float)(iw0 + (iw1 - iw0) * t0), IncisionHalfWidthB = (float)(iw0 + (iw1 - iw0) * t),
                    Gradient = node.Gradient, Erodibility = node.Erodibility, Cell = node.Cell,
                    ValleyA = v0 + (v1 - v0) * ft0, ValleyB = v0 + (v1 - v0) * ft,
                    ChannelHalfWidthA = Lerp(s0.HalfWidth, s1.HalfWidth, ft0), ChannelHalfWidthB = Lerp(s0.HalfWidth, s1.HalfWidth, ft),
                    ChannelDepthA = Lerp(s0.Depth, s1.Depth, ft0), ChannelDepthB = Lerp(s0.Depth, s1.Depth, ft),
                    BankWidthA = Lerp(s0.BankWidth, s1.BankWidth, ft0), BankWidthB = Lerp(s0.BankWidth, s1.BankWidth, ft),
                    FloodplainHalfWidthA = Lerp(s0.FloodplainHalfWidth, s1.FloodplainHalfWidth, ft0), FloodplainHalfWidthB = Lerp(s0.FloodplainHalfWidth, s1.FloodplainHalfWidth, ft),
                    FloodplainDepthA = Lerp(s0.FloodplainDepth, s1.FloodplainDepth, ft0), FloodplainDepthB = Lerp(s0.FloodplainDepth, s1.FloodplainDepth, ft),
                    AsymmetryA = Lerp(s0.Asymmetry, s1.Asymmetry, ft0), AsymmetryB = Lerp(s0.Asymmetry, s1.Asymmetry, ft),
                    DevelopmentA = Lerp(s0.Development, s1.Development, ft0), DevelopmentB = Lerp(s0.Development, s1.Development, ft),
                });
                previous = p;
            }
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        static long Cell(double v) => (long)Math.Floor(v / HashCell);

        static long Key(long x, long y, long z) => ((x + 200) << 32) | ((y + 200) << 16) | (z + 200);

        /// <summary>
        /// The valley at a unit direction: the carve depth (meters, the deepest of the nearby valleys: their union) and how
        /// far into a valley floor the point is (1 on the axis, 0 at the rim and beyond). The point is first displaced by a
        /// gentle deterministic meander (a few kilometers) so the paths wind; valleys narrower than a sample spacing
        /// (radians, 0 = full) fade (band limit, two octaves).
        /// </summary>
        public float Carve(Vector3d direction, float spacing, out float corridor)
        {
            corridor = 0f;
            Vector3d q = Meander(direction);
            if (!hash.TryGetValue(Key(Cell(q.x), Cell(q.y), Cell(q.z)), out int[] bucket))
            {
                return 0f;
            }
            float carve = 0f;
            foreach (int s in bucket)
            {
                ref readonly Segment g = ref segments[s];
                if (g.ValleyA <= 0f && g.ValleyB <= 0f)
                {
                    continue; // a bed-only channel (V0.2.0G.2E): no mountain valley
                }
                Vector3d ab = g.B - g.A;
                double len2 = Vector3d.Dot(ab, ab);
                double t = len2 > 0.0 ? Math.Max(0.0, Math.Min(1.0, Vector3d.Dot(q - g.A, ab) / len2)) : 0.0;
                Vector3d closest = g.A + ab * t;
                Vector3d offset = q - closest;
                double distance = offset.Magnitude * radiusMeters;
                float halfWidth = (float)(g.HalfWidthA + (g.HalfWidthB - g.HalfWidthA) * t);
                // Asymmetric valleys: one side's wall a little wider. The side is the sine of the angle between the axis
                // and the offset, so the skew blends through zero across the axis AND around the segment ends (V0.2.0G.2B.1:
                // a sign flip beyond an end put a step in the width along the axis extension: a vertical wall, drawn as
                // teeth by the mesh).
                double sideNorm = len2 > 0.0 ? Vector3d.Dot(Vector3d.Cross(ab, q), offset) / (Math.Sqrt(len2) * Math.Max(offset.Magnitude, 1e-12)) : 0.0;
                float width = halfWidth * (1f + g.Skew * (float)Math.Max(-1.0, Math.Min(1.0, sideNorm / 0.35)));
                if (distance >= width)
                {
                    continue;
                }
                float profile = Profile((float)(distance / width), g.Broad);
                float band = BandWeight(halfWidth * 2.0, spacing);
                float valleyWeight = g.ValleyA + (g.ValleyB - g.ValleyA) * (float)t;
                float depth = (float)(g.DepthA + (g.DepthB - g.DepthA) * t) * profile * band * valleyWeight;
                if (depth > carve)
                {
                    carve = depth;
                }
                // V0.2.0G.2E: the corridor (the calm of the octaves) is band-limited like the depth, so the legacy field at
                // the hydrology spacing is exactly the field the drainage was computed on. Before: the profile alone.
                corridor = Mathf.Max(corridor, WholeNetwork ? profile * band * valleyWeight : profile);
            }
            return carve;
        }

        /// <summary>
        /// The meander displacement: carving, incision and queries evaluate the segments at this displaced direction, so a
        /// segment chord in "meander space" is a winding curve on the terrain. Smooth, a few kilometers, invertible by
        /// Newton iteration (the displacement varies slowly against its own size): see <see cref="Invert"/>.
        /// </summary>
        public Vector3d Meander(Vector3d direction)
        {
            double f = meanderFrequency;
            var w = new Vector3d(
                PlanetNoise.Gradient(direction.x * f, direction.y * f, direction.z * f, meanderSeedX),
                PlanetNoise.Gradient(direction.x * f, direction.y * f, direction.z * f, meanderSeedY),
                PlanetNoise.Gradient(direction.x * f, direction.y * f, direction.z * f, meanderSeedZ));
            w -= direction * Vector3d.Dot(w, direction);
            return (direction + w * meanderAmplitude).Normalized;
        }

        /// <summary>
        /// The terrain-space direction whose meander image is <paramref name="target"/> (unit; V0.2.0G.2E): Newton steps
        /// in the tangent plane from the target itself. Deterministic: the same target always gives the same direction.
        /// </summary>
        public Vector3d Invert(Vector3d target)
        {
            Vector3d d = target.Normalized;
            for (int i = 0; i < InversionIterations; i++)
            {
                Vector3d m = Meander(d);
                Vector3d gap = target - m;
                if (gap.Magnitude < InversionTolerance) break;
                Vector3d e1 = Vector3d.Cross(d, Math.Abs(d.y) < 0.9 ? Vector3d.Up : Vector3d.Right).Normalized;
                Vector3d e2 = Vector3d.Cross(d, e1);
                Vector3d m1 = Meander((d + e1 * JacobianStep).Normalized) - m;
                Vector3d m2 = Meander((d + e2 * JacobianStep).Normalized) - m;
                double j11 = Vector3d.Dot(m1, e1) / JacobianStep, j12 = Vector3d.Dot(m2, e1) / JacobianStep;
                double j21 = Vector3d.Dot(m1, e2) / JacobianStep, j22 = Vector3d.Dot(m2, e2) / JacobianStep;
                double det = j11 * j22 - j12 * j21;
                double g1 = Vector3d.Dot(gap, e1), g2 = Vector3d.Dot(gap, e2);
                if (Math.Abs(det) < 1e-3)
                {
                    d = (d + gap).Normalized;
                    continue;
                }
                double u = (g1 * j22 - j12 * g2) / det, v = (j11 * g2 - j21 * g1) / det;
                d = (d + e1 * u + e2 * v).Normalized;
            }
            return d;
        }

        /// <summary>
        /// Fluvial incision at a direction (meters, at strength 1; V0.2.0G.2B): each nearby channel contributes its node
        /// incision through a smooth bell profile (1 - x^2)^2 of its incision half width, band-limited like the valleys;
        /// the two strongest contributions are combined by a soft maximum (a confluence deepens a little, never the sum).
        /// <paramref name="channelness"/> is 1 on the nearest channel axis, 0 beyond its incision width.
        /// </summary>
        public float Incision(Vector3d direction, float spacing, out float channelness)
        {
            channelness = 0f;
            Vector3d q = Meander(direction);
            if (!hash.TryGetValue(Key(Cell(q.x), Cell(q.y), Cell(q.z)), out int[] bucket))
            {
                return 0f;
            }
            float best = 0f, second = 0f;
            foreach (int s in bucket)
            {
                ref readonly Segment g = ref segments[s];
                Vector3d ab = g.B - g.A;
                double len2 = Vector3d.Dot(ab, ab);
                double t = len2 > 0.0 ? Math.Max(0.0, Math.Min(1.0, Vector3d.Dot(q - g.A, ab) / len2)) : 0.0;
                Vector3d offset = q - (g.A + ab * t);
                double distance = offset.Magnitude * radiusMeters;
                float width = (float)(g.IncisionHalfWidthA + (g.IncisionHalfWidthB - g.IncisionHalfWidthA) * t);
                if (width <= 0f || distance >= width)
                {
                    continue;
                }
                float x = (float)(distance / width);
                float bell = (1f - x * x) * (1f - x * x);
                float value = (float)(g.IncisionA + (g.IncisionB - g.IncisionA) * t) * bell * BandWeight(width * 2.0, spacing);
                if (value > best) { second = best; best = value; }
                else if (value > second) { second = value; }
                channelness = Mathf.Max(channelness, bell);
            }
            if (best <= 0f)
            {
                return 0f;
            }
            // Soft maximum of the two strongest: smooth across junctions, at most ~12 % deeper than the deepest alone.
            float softness = 0.25f * best;
            float d = best - second;
            return 0.5f * (best + second + Mathf.Sqrt(d * d + softness * softness)) - 0.5f * softness;
        }

        /// <summary>
        /// The river bed at a direction (meters, at strength 1; V0.2.0G.2E): the composite cross-section of every nearby
        /// channel (<see cref="RiverBedMorphology.CrossSection"/>, oriented by the local curvature: the outer bank on the
        /// outside of the bend), each scale band-limited to its own width, the floodplain scaled by
        /// <paramref name="plainScale"/> (0 inside the mountain valleys, which take over), the contributions united by a
        /// p-norm: exactly 0 away from every channel, exactly one contribution alone, at most 9 % deeper where two equal
        /// ones overlap, smooth everywhere. <paramref name="channel"/>: 1 at a thalweg, 0 at the bank tops;
        /// <paramref name="floor"/>: 1 across the channel, banks and floodplain, 0 beyond the margin.
        /// </summary>
        public float Bed(Vector3d direction, float spacing, float plainScale, out float channel, out float floor)
        {
            return Bed(direction, spacing, plainScale, out channel, out floor, out _, out _);
        }

        /// <summary><see cref="Bed(Vector3d, float, float, out float, out float)"/>, also reporting the largest single-channel contribution and how many channels contribute (tests, diagnostics).</summary>
        public float Bed(Vector3d direction, float spacing, float plainScale, out float channel, out float floor, out float largestContribution, out int contributingChannels)
        {
            channel = 0f;
            floor = 0f;
            largestContribution = 0f;
            contributingChannels = 0;
            if (!WholeNetwork)
            {
                return 0f;
            }
            Vector3d q = Meander(direction);
            if (!hash.TryGetValue(Key(Cell(q.x), Cell(q.y), Cell(q.z)), out int[] bucket))
            {
                return 0f;
            }
            float depth = 0f;
            var section = new ChannelSection();
            // The segments of one channel cell are contiguous in a bucket (ascending segment index): within a cell the
            // contributions are one channel (their maximum: a point at a chord vertex is on two chords of the same axis),
            // across cells the p-norm unites distinct channels (a confluence).
            int runCell = -1;
            float runDepth = 0f, runChannel = 0f, runFloor = 0f;
            foreach (int s in bucket)
            {
                ref readonly Segment g = ref segments[s];
                if (g.Cell != runCell)
                {
                    if (runDepth > 0f)
                    {
                        depth = RiverBedMorphology.Union(depth, runDepth);
                        channel = RiverBedMorphology.Union(channel, runChannel);
                        floor = RiverBedMorphology.Union(floor, runFloor);
                        largestContribution = Math.Max(largestContribution, runDepth);
                        contributingChannels++;
                    }
                    runCell = g.Cell;
                    runDepth = 0f; runChannel = 0f; runFloor = 0f;
                }
                float wcMax = Math.Max(g.ChannelHalfWidthA, g.ChannelHalfWidthB);
                float wvMax = Math.Max(g.FloodplainHalfWidthA, g.FloodplainHalfWidthB);
                if (wcMax <= 0f && wvMax <= 0f)
                {
                    continue; // nothing carved on this curve (a minor drainage path)
                }
                Vector3d ab = g.B - g.A;
                double len2 = Vector3d.Dot(ab, ab);
                double t = len2 > 0.0 ? Math.Max(0.0, Math.Min(1.0, Vector3d.Dot(q - g.A, ab) / len2)) : 0.0;
                Vector3d closest = g.A + ab * t;
                Vector3d offset = q - closest;
                double distanceUnits = offset.Magnitude;
                float distance = (float)(distanceUnits * radiusMeters);
                float ft = (float)t;
                section.HalfWidth = Lerp(g.ChannelHalfWidthA, g.ChannelHalfWidthB, ft);
                section.Depth = Lerp(g.ChannelDepthA, g.ChannelDepthB, ft);
                section.BankWidth = Lerp(g.BankWidthA, g.BankWidthB, ft);
                section.FloodplainHalfWidth = Lerp(g.FloodplainHalfWidthA, g.FloodplainHalfWidthB, ft) * plainScale;
                section.FloodplainDepth = Lerp(g.FloodplainDepthA, g.FloodplainDepthB, ft) * plainScale;
                section.Asymmetry = Lerp(g.AsymmetryA, g.AsymmetryB, ft);
                float reach = Math.Max(section.FloodplainHalfWidth * (1f + RiverBedMorphology.MarginShare),
                    section.HalfWidth * (1f + RiverBedMorphology.ThalwegShift) + section.BankWidth * (1f + RiverBedMorphology.InnerBankShare));
                if (distance >= reach)
                {
                    continue;
                }
                // Side of the axis (positive: right of the flow, as the valley skew) and the bend: the outer bank is on the
                // right of a left turn. Signed distance positive toward the outer bank; symmetric where the axis is straight.
                double sideNorm = len2 > 0.0 ? Vector3d.Dot(Vector3d.Cross(ab, q), offset) : 0.0;
                float curvature = Lerp(g.CurvatureA, g.CurvatureB, ft);
                float kappa = RiverBedMorphology.NormalizedCurvature(curvature);
                float asymmetry = section.Asymmetry * Math.Abs(kappa);
                float sAxis = kappa * sideNorm >= 0.0 ? distance : -distance;
                RiverBedMorphology.CrossSection(section, sAxis, asymmetry, out float excess, out float plainDepth, out float plain, out float mask);
                float channelBand = BandWeight(2.0 * (section.HalfWidth + section.BankWidth), spacing);
                float plainBand = section.FloodplainHalfWidth > 0f ? BandWeight(2.0 * section.FloodplainHalfWidth * (1f + RiverBedMorphology.MarginShare), spacing) : 0f;
                float contribution = excess * channelBand + plainDepth * plainBand;
                if (contribution <= 0f)
                {
                    continue;
                }
                runDepth = Math.Max(runDepth, contribution);
                runChannel = Math.Max(runChannel, mask * channelBand);
                runFloor = Math.Max(runFloor, Math.Max(mask * channelBand, plain * plainBand));
            }
            if (runDepth > 0f)
            {
                depth = RiverBedMorphology.Union(depth, runDepth);
                channel = RiverBedMorphology.Union(channel, runChannel);
                floor = RiverBedMorphology.Union(floor, runFloor);
                largestContribution = Math.Max(largestContribution, runDepth);
                contributingChannels++;
            }
            channel = Math.Min(channel, 1f);
            floor = Math.Min(floor, 1f);
            return depth;
        }

        /// <summary>
        /// The nearest channel corridor at a direction (V0.2.0G.2B): one hash cell, a few segments, deterministic ties
        /// (smallest distance, then lowest segment index), no allocation. False when no channel is within reach.
        /// </summary>
        public bool Nearest(Vector3d direction, out ChannelQuery query)
        {
            query = default;
            Vector3d q = Meander(direction);
            if (!hash.TryGetValue(Key(Cell(q.x), Cell(q.y), Cell(q.z)), out int[] bucket))
            {
                return false;
            }
            double bestDistance = double.MaxValue;
            int bestIndex = -1;
            double bestT = 0.0;
            Vector3d bestClosest = default;
            foreach (int s in bucket)
            {
                ref readonly Segment g = ref segments[s];
                Vector3d ab = g.B - g.A;
                double len2 = Vector3d.Dot(ab, ab);
                double t = len2 > 0.0 ? Math.Max(0.0, Math.Min(1.0, Vector3d.Dot(q - g.A, ab) / len2)) : 0.0;
                Vector3d closest = g.A + ab * t;
                double distance = (q - closest).Magnitude;
                if (distance < bestDistance || (distance == bestDistance && s < bestIndex))
                {
                    bestDistance = distance;
                    bestIndex = s;
                    bestT = t;
                    bestClosest = closest;
                }
            }
            if (bestIndex < 0)
            {
                return false;
            }
            ref readonly Segment b = ref segments[bestIndex];
            float ft = (float)bestT;
            Vector3d flow = b.B - b.A;
            query = new ChannelQuery
            {
                Segment = bestIndex,
                DistanceMeters = (float)(bestDistance * radiusMeters),
                T = ft,
                Closest = bestClosest,
                Flow = flow.Magnitude > 0.0 ? flow.Normalized : Vector3d.Zero,
                Cells = b.Cells,
                HalfWidthMeters = b.HalfWidthA + (b.HalfWidthB - b.HalfWidthA) * ft,
                DepthMeters = b.DepthA + (b.DepthB - b.DepthA) * ft,
                FilledElevation = b.FilledA + (b.FilledB - b.FilledA) * bestT,
                Gradient = b.Gradient,
                IncisionMeters = b.IncisionA + (b.IncisionB - b.IncisionA) * ft,
                IncisionHalfWidthMeters = b.IncisionHalfWidthA + (b.IncisionHalfWidthB - b.IncisionHalfWidthA) * ft,
                Erodibility = b.Erodibility,
                Valley = Lerp(b.ValleyA, b.ValleyB, ft),
                ChannelHalfWidthMeters = Lerp(b.ChannelHalfWidthA, b.ChannelHalfWidthB, ft),
                ChannelDepthMeters = Lerp(b.ChannelDepthA, b.ChannelDepthB, ft),
                BankWidthMeters = Lerp(b.BankWidthA, b.BankWidthB, ft),
                FloodplainHalfWidthMeters = Lerp(b.FloodplainHalfWidthA, b.FloodplainHalfWidthB, ft),
                FloodplainDepthMeters = Lerp(b.FloodplainDepthA, b.FloodplainDepthB, ft),
                Development = Lerp(b.DevelopmentA, b.DevelopmentB, ft),
                Curvature = Lerp(b.CurvatureA, b.CurvatureB, ft),
            };
            return true;
        }

        float BandWeight(double widthMeters, float spacing)
        {
            if (spacing <= 0f)
            {
                return 1f;
            }
            double samples = widthMeters / (spacing * radiusMeters);
            return samples <= 2.0 ? 0f : Mathf.Clamp01((float)((Math.Log(samples, 2.0) - 1.0) * 0.5));
        }

        /// <summary>Valley cross-section at a normalized distance from the axis (0 = axis, 1 = rim): V (narrow) to U (broad).</summary>
        static float Profile(float s, float broad)
        {
            if (s >= 1f)
            {
                return 0f;
            }
            float v = Mathf.Pow(1f - s, 1.6f);
            float u = 1f - SmoothStep(0.35f, 1f, s);
            return Mathf.Lerp(v, u, broad);
        }

        static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
