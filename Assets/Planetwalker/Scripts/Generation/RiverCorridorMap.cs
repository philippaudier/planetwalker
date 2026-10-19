using System;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// The carved corridor each displayed river follows (V0.2.0G.2D): the correspondence between the river network's
    /// drainage nodes (legacy hydrology, display resolution) and the valley network's channel curves (the terrain's own
    /// drainage of e0, <see cref="ValleyNetwork.GridResolution"/>), plus the exact projection of a trajectory point onto
    /// the carved axis in terrain space (the meander inverted). There is no second hydrology here: a river is snapped
    /// only onto the curves of the cells it already drains through, and only where its legacy flow and the carved flow
    /// agree (a corridor weight of 1); where they disagree, or where no channel is carved (plains, outside the ranges),
    /// the weight falls to 0 over half a cell and the legacy trajectory is kept. Everything is a pure function of the two
    /// networks: deterministic, thread safe after construction, no allocation per query.
    /// </summary>
    public sealed class RiverCorridorMap
    {
        /// <summary>Most Newton iterations of the meander inversion, stopped once the residual is below the tolerance.</summary>
        const int InversionIterations = 12;
        const double InversionToleranceMeters = 0.01;
        const double JacobianStep = 1e-6;

        readonly ValleyNetwork valleys;
        readonly RiverNetwork network;
        readonly double radiusMeters;
        // Corridor weight knots per river, at half-node steps: index 2k is node k, 2k + 1 the midpoint to node k + 1.
        readonly float[] knots;
        readonly int[] knotStart;
        // Valley grid cell of each river point (nodes, then the junction / mouth cell when the path ends there), and the
        // first segment of the carved curve the river follows through that cell (the curve from its own upstream cell;
        // -1: every curve of the cell, for a head or an unmatched entry).
        readonly int[] valleyCells, curveStart;
        readonly int[] cellStart;

        /// <summary>The query result: where the carved axis is and what the corridor measures there.</summary>
        public struct Projection
        {
            /// <summary>Corridor weight in [0, 1] at this trajectory parameter (0: legacy trajectory kept).</summary>
            public float Weight;
            /// <summary>Distance from the legacy trajectory point to the carved axis (meters): the horizontal discrepancy.</summary>
            public float OffsetMeters;
            /// <summary>Distance left between the snapped point's meander image and the axis (meters): the inversion residual.</summary>
            public float ResidualMeters;
            /// <summary>Valley and incision half widths of the corridor at the projection (meters; incision 0 when none).</summary>
            public float HalfWidthMeters, IncisionHalfWidthMeters, IncisionMeters;
            /// <summary>Carved valley depth of the corridor at the projection (meters, G.1.1).</summary>
            public float DepthMeters;
            public int Segment;
        }

        public RiverCorridorMap(RiverNetwork network, ValleyNetwork valleys, double radiusMeters)
        {
            this.network = network ?? throw new ArgumentNullException(nameof(network));
            this.valleys = valleys;
            this.radiusMeters = radiusMeters;
            int rivers = network.RiverCount;
            knotStart = new int[rivers + 1];
            cellStart = new int[rivers + 1];
            for (int r = 0; r < rivers; r++)
            {
                int points = PointCount(network.GetRiver(r));
                cellStart[r + 1] = cellStart[r] + points;
                knotStart[r + 1] = knotStart[r] + Math.Max(1, 2 * points - 1);
            }
            valleyCells = new int[cellStart[rivers]];
            curveStart = new int[cellStart[rivers]];
            knots = new float[knotStart[rivers]];
            if (valleys == null)
            {
                return; // no carved corridors: every weight stays 0
            }
            HydrologyGrid legacyGrid = network.Hydrology.Grid;
            HydrologyResult carved = valleys.Hydrology;
            bool sameGrid = legacyGrid.Resolution == carved.Grid.Resolution;
            for (int r = 0; r < rivers; r++)
            {
                River data = network.GetRiver(r);
                int points = PointCount(data);
                int c0 = cellStart[r], k0 = knotStart[r];
                for (int i = 0; i < points; i++)
                {
                    int legacyCell = i < data.NodeCount ? network.GetNodeCell(network.GetRiverNode(r, i))
                        : data.JunctionCell >= 0 ? data.JunctionCell : data.MouthCell;
                    valleyCells[c0 + i] = sameGrid ? legacyCell : carved.Grid.CellFromDirection(legacyGrid.GetCenter(legacyCell));
                }
                for (int i = 0; i < points; i++)
                {
                    int cell = valleyCells[c0 + i];
                    bool channel = valleys.IsChannel(cell);
                    curveStart[c0 + i] = i > 0 && channel ? CurveFrom(valleyCells[c0 + i - 1], cell) : -1;
                    knots[k0 + 2 * i] = channel ? 1f : 0f;
                    if (i + 1 < points)
                    {
                        int next = valleyCells[c0 + i + 1];
                        bool linked = channel && valleys.IsChannel(next) && (next == cell || carved.GetReceiver(cell) == next);
                        knots[k0 + 2 * i + 1] = linked ? 1f : 0f;
                        if (channel && valleys.IsChannel(next))
                        {
                            if (linked) MatchedLinks++; else MismatchedLinks++;
                        }
                    }
                    if (channel) ChannelNodes++;
                }
                NodeCount += points;
            }
        }

        static int PointCount(River data) => data.NodeCount + (data.JunctionCell >= 0 || data.MouthCell >= 0 ? 1 : 0);

        /// <summary>First segment of the curve of <paramref name="cell"/> that starts at the midpoint to <paramref name="upstream"/>, or -1.</summary>
        int CurveFrom(int upstream, int cell)
        {
            if (upstream == cell)
            {
                return -1;
            }
            HydrologyGrid grid = valleys.Hydrology.Grid;
            Vector3d mid = (new Vector3d(grid.GetCenter(upstream)) + new Vector3d(grid.GetCenter(cell))).Normalized;
            valleys.GetCellSegments(cell, out int start, out int end);
            for (int s = start; s < end; s += ValleyNetwork.Subdivisions)
            {
                if ((valleys.GetSegment(s).A - mid).Magnitude < 1e-9)
                {
                    return s;
                }
            }
            return -1;
        }

        /// <summary>River trajectory points (nodes plus junction / mouth ends) over the network.</summary>
        public int NodeCount { get; }

        /// <summary>Trajectory points lying on a carved channel cell.</summary>
        public int ChannelNodes { get; }

        /// <summary>Links between two channel points whose legacy flow is also the carved flow (snapped) / is not (faded).</summary>
        public int MatchedLinks { get; }

        public int MismatchedLinks { get; }

        public bool HasCorridors => valleys != null;

        /// <summary>Corridor weight along a river at a trajectory parameter (node index, fractional), linear between half-node knots.</summary>
        public float Weight(int river, float param)
        {
            int k0 = knotStart[river], count = knotStart[river + 1] - k0;
            if (count <= 1)
            {
                return count == 1 ? knots[k0] : 0f;
            }
            float x = Mathf.Clamp(param * 2f, 0f, count - 1);
            int a = Mathf.Min((int)x, count - 2);
            float t = x - a;
            return knots[k0 + a] + (knots[k0 + a + 1] - knots[k0 + a]) * t;
        }

        /// <summary>
        /// Projects a legacy trajectory point of a river onto the carved axis of the cells it drains through there (the
        /// point's own node and its two neighbours: a connected piece of corridor). Returns the terrain-space point whose
        /// meander image lies on the axis, blended with the legacy point by the corridor weight. With a weight of 0 the
        /// legacy point is returned unchanged and nothing is evaluated.
        /// </summary>
        public Vector3d Snap(Vector3d legacy, int river, float param, out Projection projection) => Snap(legacy, river, param, 1f, out projection);

        /// <param name="weightScale">Extra factor on the corridor weight (0 .. 1): e.g. fading before a lake transition.</param>
        public Vector3d Snap(Vector3d legacy, int river, float param, float weightScale, out Projection projection)
        {
            projection = default;
            projection.Segment = -1;
            float weight = valleys != null ? Weight(river, param) * Mathf.Clamp01(weightScale) : 0f;
            projection.Weight = weight;
            if (weight <= 0f)
            {
                return legacy;
            }
            int c0 = cellStart[river], points = cellStart[river + 1] - c0;
            int k = Mathf.Clamp(Mathf.RoundToInt(param), 0, points - 1);
            Vector3d q = valleys.Meander(legacy);
            double bestDistance = double.MaxValue;
            int bestSegment = -1;
            double bestT = 0.0;
            Vector3d bestClosest = default;
            for (int i = Math.Max(0, k - 1); i <= Math.Min(points - 1, k + 1); i++)
            {
                // The curve the river follows through that cell only: the union over the three points is one connected
                // polyline, so the closest point moves continuously along the trajectory (no jump between curves).
                int start, end;
                if (curveStart[c0 + i] >= 0)
                {
                    start = curveStart[c0 + i];
                    end = start + ValleyNetwork.Subdivisions;
                }
                else
                {
                    valleys.GetCellSegments(valleyCells[c0 + i], out start, out end);
                }
                for (int s = start; s < end; s++)
                {
                    ValleyNetwork.Segment g = valleys.GetSegment(s);
                    Vector3d ab = g.B - g.A;
                    double len2 = Vector3d.Dot(ab, ab);
                    double t = len2 > 0.0 ? Math.Max(0.0, Math.Min(1.0, Vector3d.Dot(q - g.A, ab) / len2)) : 0.0;
                    Vector3d closest = g.A + ab * t;
                    double distance = (q - closest).Magnitude;
                    if (distance < bestDistance || (distance == bestDistance && s < bestSegment))
                    {
                        bestDistance = distance;
                        bestSegment = s;
                        bestT = t;
                        bestClosest = closest;
                    }
                }
            }
            if (bestSegment < 0)
            {
                projection.Weight = 0f;
                return legacy;
            }
            // The chord point projected onto the sphere: the axis the terrain carves (the chord's sagitta, up to ~25 m for a
            // 20 km chord, is radial and means nothing on the surface).
            Vector3d target = bestClosest.Normalized;
            Vector3d d = Invert(target, legacy, out double residual);
            ValleyNetwork.Segment b = valleys.GetSegment(bestSegment);
            float ft = (float)bestT;
            projection.Segment = bestSegment;
            projection.OffsetMeters = (float)((q - target).Magnitude * radiusMeters);
            projection.ResidualMeters = (float)(residual * radiusMeters);
            projection.HalfWidthMeters = b.HalfWidthA + (b.HalfWidthB - b.HalfWidthA) * ft;
            projection.IncisionHalfWidthMeters = b.IncisionHalfWidthA + (b.IncisionHalfWidthB - b.IncisionHalfWidthA) * ft;
            projection.IncisionMeters = b.IncisionA + (b.IncisionB - b.IncisionA) * ft;
            projection.DepthMeters = b.DepthA + (b.DepthB - b.DepthA) * ft;
            return weight >= 1f ? d : (legacy + (d - legacy) * weight).Normalized;
        }

        /// <summary>
        /// The terrain-space direction whose meander image is <paramref name="target"/> (unit), by Newton steps in the
        /// tangent plane from <paramref name="start"/> (the displacement's gradient is close to 1 in places, where a plain
        /// fixed point crawls). <paramref name="residual"/>: what is left (unit-sphere units).
        /// </summary>
        public Vector3d Invert(Vector3d target, Vector3d start, out double residual)
        {
            Vector3d d = start;
            double tolerance = InversionToleranceMeters / radiusMeters;
            residual = double.MaxValue;
            for (int i = 0; i < InversionIterations; i++)
            {
                Vector3d m = valleys.Meander(d);
                Vector3d gap = target - m;
                residual = gap.Magnitude;
                if (residual < tolerance) break;
                Vector3d e1 = Vector3d.Cross(d, Math.Abs(d.y) < 0.9 ? Vector3d.Up : Vector3d.Right).Normalized;
                Vector3d e2 = Vector3d.Cross(d, e1);
                Vector3d m1 = valleys.Meander((d + e1 * JacobianStep).Normalized) - m;
                Vector3d m2 = valleys.Meander((d + e2 * JacobianStep).Normalized) - m;
                double j11 = Vector3d.Dot(m1, e1) / JacobianStep, j12 = Vector3d.Dot(m2, e1) / JacobianStep;
                double j21 = Vector3d.Dot(m1, e2) / JacobianStep, j22 = Vector3d.Dot(m2, e2) / JacobianStep;
                double det = j11 * j22 - j12 * j21;
                double g1 = Vector3d.Dot(gap, e1), g2 = Vector3d.Dot(gap, e2);
                if (Math.Abs(det) < 1e-3)
                {
                    d = (d + gap).Normalized; // degenerate Jacobian: a plain step
                    continue;
                }
                double u = (g1 * j22 - j12 * g2) / det, v = (j11 * g2 - j21 * g1) / det;
                d = (d + e1 * u + e2 * v).Normalized;
            }
            residual = (target - valleys.Meander(d)).Magnitude;
            return d;
        }

        /// <summary>
        /// Where a tributary's carved valley meets its trunk's (V0.2.0G.2D): the downstream end of the curve the river
        /// follows through its junction cell, half a cell past the junction node where its legacy trajectory stops. In
        /// terrain space (the meander inverted). False when the river does not end on a carved, matched junction.
        /// </summary>
        public bool TryGetConfluence(int river, out Vector3d direction)
        {
            direction = default;
            River data = network.GetRiver(river);
            if (valleys == null || data.ParentRiver < 0 || data.JunctionCell < 0)
            {
                return false;
            }
            int c0 = cellStart[river], last = cellStart[river + 1] - c0 - 1;
            if (curveStart[c0 + last] < 0 || Weight(river, last) < 1f || (last > 0 && Weight(river, last - 0.5f) < 1f))
            {
                return false;
            }
            Vector3d end = valleys.GetSegment(curveStart[c0 + last] + ValleyNetwork.Subdivisions - 1).B.Normalized;
            direction = Invert(end, new Vector3d(valleys.Hydrology.Grid.GetCenter(valleyCells[c0 + last])), out _);
            return true;
        }
    }
}
