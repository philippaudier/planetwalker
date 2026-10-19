using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// Drainage network of the mountain ranges (V0.2.0G.1.1): the open, converging valley paths the regional landforms
    /// carve. Built once per planet from the planetary terrain e0 alone, then a pure lookup.
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
        }

        readonly Segment[] segments;
        readonly float[] nodeIncision;
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
        public ValleyNetwork(Func<Vector3, TerrainBaseSample> sample, float seaLevel, double radiusMeters, int seed, float maxDepthMeters, Func<Vector3, float> erodibility = null)
        {
            this.radiusMeters = radiusMeters;
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
                TerrainBaseSample s = sample(grid.GetCenter(cell));
                elevation[cell] = s.Elevation;
                influence[cell] = s.Influence;
                core[cell] = s.Core;
            });
            HydrologyResult hydrology = new HydrologySystem().Compute(grid, d => elevation[grid.CellFromDirection(d)], seaLevel);
            Hydrology = hydrology;

            double meanCellArea = grid.TotalArea / count;
            var channel = new bool[count];
            var cells = new float[count];
            for (int cell = 0; cell < count; cell++)
            {
                cells[cell] = (float)(hydrology.GetAccumulation(cell) / meanCellArea);
                channel[cell] = hydrology.IsLand(cell) && influence[cell] >= MinInfluence && cells[cell] >= MinCells;
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

            // Fluvial incision per channel node (V0.2.0G.2B), then propagated downstream.
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
                if (channel[cell] && receiver >= 0 && channel[receiver] && incision[cell] * DownstreamDecay > incision[receiver])
                {
                    incisionWidth[receiver] = Math.Max(incisionWidth[receiver], incisionWidth[cell]);
                    incision[receiver] = Math.Min(incision[cell] * DownstreamDecay, MaxDepthPerHalfWidth * incisionWidth[receiver]);
                }
            }

            nodeIncision = incision;

            var list = new List<Segment>();
            segmentStartOfCell = new int[count + 1];
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
                // when it leaves the ranges (the valley fades there with the influence), the node itself at the sea.
                Vector3d end = !downstream ? pb : channel[c] ? Mid(pb, new Vector3d(grid.GetCenter(c))) : new Vector3d(grid.GetCenter(c));
                Valley(cells[b], core[b], maxDepthMeters, out float widthB, out float depthB, out float broadB);
                float widthEnd = widthB, depthEnd = depthB;
                if (downstream)
                {
                    Valley(cells[c], core[c], maxDepthMeters, out float widthC, out float depthC, out _);
                    widthEnd = 0.5f * (widthB + widthC);
                    depthEnd = 0.5f * (depthB + depthC);
                }
                float skew = 0.25f * (2f * PlanetNoise.Hash01(seed, 1000003 + b) - 1f);
                double filledB = hydrology.GetFilledElevation(b), filledEnd = downstream ? 0.5 * (filledB + hydrology.GetFilledElevation(c)) : filledB;
                float incB = incision[b], incEnd = downstream && channel[c] ? 0.5f * (incB + incision[c]) : downstream ? incB * 0.5f : incB;
                float incWB = incisionWidth[b], incWEnd = downstream && channel[c] ? 0.5f * (incWB + incisionWidth[c]) : incWB;
                var node = new NodeInfo { Gradient = gradient[b], Erodibility = erodibilityOf[b], Cell = b };
                List<int> ups = upstream[b];
                if (ups == null)
                {
                    // A channel head: a straight start from the node, the valley and the incision tapering in from zero
                    // (V0.2.0G.2B.1: no terminal wall; a gorge can be followed up to its source).
                    Add(list, pb, end, end, widthB * 0.6f, widthEnd, 0f, depthEnd, broadB, skew, cells[b], filledB, filledEnd, true,
                        0f, incEnd, incWB * 0.8f, incWEnd, node);
                    continue;
                }
                foreach (int a in ups)
                {
                    Vector3d pa = new Vector3d(grid.GetCenter(a));
                    Valley(cells[a], core[a], maxDepthMeters, out float widthA, out float depthA, out _);
                    double filledStart = 0.5 * (hydrology.GetFilledElevation(a) + filledB);
                    Add(list, Mid(pa, pb), pb, end, 0.5f * (widthA + widthB), widthEnd, 0.5f * (depthA + depthB), depthEnd, broadB, skew, cells[b], filledStart, filledEnd, false,
                        0.5f * (incision[a] + incB), incEnd, 0.5f * (incisionWidth[a] + incWB), incWEnd, node);
                }
            }
            segments = list.ToArray();
            segmentStartOfCell[count] = list.Count;
            double reach = (Math.Max(BroadHalfWidthMeters, IncisionBroadHalfWidthMeters) + MeanderMeters) / radiusMeters;
            var cellsOf = new Dictionary<long, List<int>>();
            for (int s = 0; s < segments.Length; s++)
            {
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
            for (int cell = 0; cell < count; cell++)
            {
                if (channel[cell]) ChannelCount++;
            }
            BuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        }

        /// <summary>The drainage analysis of e0 the network comes from (diagnostics, tests).</summary>
        public HydrologyResult Hydrology { get; }

        public int ChannelCount { get; }
        public int SegmentCount => segments.Length;

        /// <summary>Regularized incision (meters, strength 1) at a hydrology grid cell's node (0 off the channels).</summary>
        public float NodeIncision(int cell) => nodeIncision[cell];
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

        struct NodeInfo { public float Gradient, Erodibility; public int Cell; }

        static void Valley(float cells, float core, float maxDepthMeters, out float halfWidth, out float depth, out float broad)
        {
            // Drained area from 2 cells (a tributary) to ~64 (a trunk valley): narrow V to broad U.
            float t = Mathf.Clamp01(Mathf.Log(Mathf.Max(cells, 1f) / MinCells, 2f) / 5f);
            halfWidth = (float)(NarrowHalfWidthMeters + (BroadHalfWidthMeters - NarrowHalfWidthMeters) * t);
            broad = Mathf.SmoothStep(0f, 1f, t) * Mathf.Lerp(1f, 0.7f, core);
            float wallShare = Mathf.Lerp(0.55f, 0.38f, broad);
            depth = Mathf.Min(maxDepthMeters, halfWidth * wallShare) * Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(core + t * 0.5f));
        }

        static Vector3d Mid(Vector3d a, Vector3d b) => (a + b).Normalized;

        /// <summary>Quadratic curve start -> end with control point <paramref name="control"/>, split into segments.</summary>
        void Add(List<Segment> list, Vector3d start, Vector3d control, Vector3d end, float w0, float w1, float d0, float d1,
            float broad, float skew, float cells, double f0, double f1, bool straight, float i0, float i1, float iw0, float iw1, in NodeInfo node)
        {
            Vector3d previous = start;
            for (int k = 1; k <= Subdivisions; k++)
            {
                double t = (double)k / Subdivisions, t0 = (double)(k - 1) / Subdivisions;
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
                });
                previous = p;
            }
        }

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
                float depth = (float)(g.DepthA + (g.DepthB - g.DepthA) * t) * profile * band;
                if (depth > carve)
                {
                    carve = depth;
                }
                corridor = Mathf.Max(corridor, profile);
            }
            return carve;
        }

        /// <summary>The query point displaced by the deterministic meander (the same for the valleys and the incision).</summary>
        /// <summary>
        /// The meander displacement: carving, incision and queries evaluate the segments at this displaced direction, so a
        /// segment chord in "meander space" is a winding curve on the terrain. Smooth, a few kilometers, invertible by
        /// fixed-point iteration (the displacement varies slowly against its own size): see RiverCorridorMap.
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
