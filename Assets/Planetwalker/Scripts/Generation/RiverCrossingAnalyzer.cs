using System.Collections.Generic;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>Why two river centerlines (or ribbons) touch without a topological junction.</summary>
    public enum RiverCrossingKind
    {
        None,
        /// <summary>The segments share an exact endpoint (junction, consecutive segments): valid.</summary>
        SharedJunction,
        /// <summary>The raw cell-to-cell drainage paths themselves cross (D8 graph).</summary>
        GraphInherent,
        /// <summary>Only the smoothed trajectories cross; the raw drainage paths do not.</summary>
        SmoothingInduced,
        /// <summary>Centerlines stay apart but the ribbons overlap because of their width.</summary>
        WidthOverlap,
    }

    /// <summary>
    /// Geometric crossing detection between polylines on the unit sphere. Segment pairs are
    /// tested in the local tangent plane at their midpoint (gnomonic projection), so no global
    /// planar projection is involved; candidates come from a spatial hash on a
    /// <see cref="HydrologyGrid"/> (each segment is bucketed in its midpoint cell and compared
    /// with the neighbouring cells only), which keeps the cost linear for bounded segment lengths.
    /// </summary>
    /// <remarks>
    /// Surface projection cannot create crossings: ribbons are placed by radial projection onto
    /// the terrain, a bijection of directions, so crossings are always decided on the sphere.
    /// </remarks>
    public static class RiverCrossingAnalyzer
    {
        public struct Crossing
        {
            public int LineA;
            public int SegmentA;
            public int LineB;
            public int SegmentB;
            public Vector3 Location;
        }

        /// <summary>
        /// Proper crossing of segments [a0, a1] and [b0, b1] (unit vectors, short arcs). Segments
        /// sharing an exact endpoint never cross (that is a junction).
        /// </summary>
        public static bool SegmentsCross(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1)
        {
            if (a0 == b0 || a0 == b1 || a1 == b0 || a1 == b1)
            {
                return false;
            }
            Vector3 m = (a0 + a1 + b0 + b1).normalized;
            Vector3 e1 = Vector3.Cross(m, Mathf.Abs(m.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 e2 = Vector3.Cross(m, e1);
            Vector2 p0 = Project(a0, m, e1, e2), p1 = Project(a1, m, e1, e2);
            Vector2 q0 = Project(b0, m, e1, e2), q1 = Project(b1, m, e1, e2);
            float d1 = Cross2(q1 - q0, p0 - q0);
            float d2 = Cross2(q1 - q0, p1 - q0);
            float d3 = Cross2(p1 - p0, q0 - p0);
            float d4 = Cross2(p1 - p0, q1 - p0);
            return d1 * d2 < 0f && d3 * d4 < 0f;
        }

        /// <summary>Smallest distance between two short segments (chord units, unit sphere).</summary>
        public static float SegmentDistance(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1)
        {
            if (SegmentsCross(a0, a1, b0, b1))
            {
                return 0f;
            }
            return Mathf.Min(Mathf.Min(PointSegment(a0, b0, b1), PointSegment(a1, b0, b1)),
                Mathf.Min(PointSegment(b0, a0, a1), PointSegment(b1, a0, a1)));
        }

        /// <summary>
        /// Finds proper crossings between polylines (points[start[l] .. start[l + 1])), excluding
        /// consecutive segments of the same polyline and segments sharing an exact endpoint.
        /// </summary>
        public static void FindCrossings(IReadOnlyList<Vector3> points, IReadOnlyList<int> start, HydrologyGrid grid,
            List<Crossing> output)
        {
            output.Clear();
            var buckets = new Dictionary<int, List<long>>();
            int lineCount = start.Count - 1;
            for (int line = 0; line < lineCount; line++)
            {
                for (int i = start[line]; i < start[line + 1] - 1; i++)
                {
                    int cell = grid.CellFromDirection((points[i] + points[i + 1]).normalized);
                    if (!buckets.TryGetValue(cell, out List<long> bucket))
                    {
                        bucket = new List<long>();
                        buckets.Add(cell, bucket);
                    }
                    bucket.Add(((long)line << 32) | (uint)i);
                }
            }

            var near = new List<long>();
            foreach (KeyValuePair<int, List<long>> entry in buckets)
            {
                near.Clear();
                near.AddRange(entry.Value);
                for (int k = grid.GetNeighborStart(entry.Key); k < grid.GetNeighborEnd(entry.Key); k++)
                {
                    if (buckets.TryGetValue(grid.GetNeighbor(k), out List<long> other))
                    {
                        near.AddRange(other);
                    }
                }
                foreach (long a in entry.Value)
                {
                    int lineA = (int)(a >> 32), segA = (int)(a & 0xffffffff);
                    foreach (long b in near)
                    {
                        // Each unordered pair once: compare global segment indices.
                        int lineB = (int)(b >> 32), segB = (int)(b & 0xffffffff);
                        if (segB <= segA || (lineA == lineB && segB - segA <= 1))
                        {
                            continue;
                        }
                        // Cheap rejection: segments farther apart than half their lengths cannot cross.
                        Vector3 midA = points[segA] + points[segA + 1];
                        Vector3 midB = points[segB] + points[segB + 1];
                        float reach = (points[segA + 1] - points[segA]).magnitude + (points[segB + 1] - points[segB]).magnitude;
                        if ((midA - midB).sqrMagnitude > reach * reach)
                        {
                            continue;
                        }
                        if (SegmentsCross(points[segA], points[segA + 1], points[segB], points[segB + 1]))
                        {
                            output.Add(new Crossing
                            {
                                LineA = lineA,
                                SegmentA = segA,
                                LineB = lineB,
                                SegmentB = segB,
                                Location = (points[segA] + points[segA + 1] + points[segB] + points[segB + 1]).normalized,
                            });
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Classifies the contact between two segments given their raw (drainage path) and
        /// smoothed versions and the ribbon half widths (unit-sphere units).
        /// </summary>
        public static RiverCrossingKind Classify(Vector3 rawA0, Vector3 rawA1, Vector3 rawB0, Vector3 rawB1,
            Vector3 smoothA0, Vector3 smoothA1, Vector3 smoothB0, Vector3 smoothB1, float halfWidthA, float halfWidthB)
        {
            if (smoothA0 == smoothB0 || smoothA0 == smoothB1 || smoothA1 == smoothB0 || smoothA1 == smoothB1)
            {
                return RiverCrossingKind.SharedJunction;
            }
            if (SegmentsCross(rawA0, rawA1, rawB0, rawB1))
            {
                return RiverCrossingKind.GraphInherent;
            }
            if (SegmentsCross(smoothA0, smoothA1, smoothB0, smoothB1))
            {
                return RiverCrossingKind.SmoothingInduced;
            }
            if (SegmentDistance(smoothA0, smoothA1, smoothB0, smoothB1) < halfWidthA + halfWidthB)
            {
                return RiverCrossingKind.WidthOverlap;
            }
            return RiverCrossingKind.None;
        }

        static Vector2 Project(Vector3 p, Vector3 m, Vector3 e1, Vector3 e2)
        {
            p /= Vector3.Dot(p, m);
            return new Vector2(Vector3.Dot(p, e1), Vector3.Dot(p, e2));
        }

        static float Cross2(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        static float PointSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-20f));
            return (a + ab * t - p).magnitude;
        }
    }
}
