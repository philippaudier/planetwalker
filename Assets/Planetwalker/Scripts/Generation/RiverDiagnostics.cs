using System;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// Development diagnostics separating D8 topology limits from trajectory geometry.
    /// Not used at runtime by default (costs a few elevation samples per path point).
    /// </summary>
    public static class RiverDiagnostics
    {
        public struct DirectionDeviation
        {
            /// <summary>Mean angle (degrees) between raw D8 steps and the continuous steepest descent.</summary>
            public float RawMeanDegrees;
            /// <summary>Mean angle (degrees) between smoothed trajectory segments and the continuous steepest descent.</summary>
            public float PathMeanDegrees;
            /// <summary>Fraction of raw D8 steps deviating by more than 22.5 degrees (half the D8 angular step).</summary>
            public float RawOver22Fraction;
            public float PathOver22Fraction;
            public int Samples;
        }

        public struct GridAlignment
        {
            /// <summary>First-order rivers analysed (at least <see cref="MinimumCells"/> cells).</summary>
            public int Rivers;
            /// <summary>Fraction whose overall direction lies within 5 degrees of a D8 axis.</summary>
            public float AlignedFraction;
            /// <summary>Fraction expected without any grid imprint (8 axes x 10 degrees / 360).</summary>
            public float ExpectedFraction;
        }

        public const int MinimumCells = 4;

        /// <summary>Slopes below this (elevation units per radian) are treated as flat.</summary>
        public const double MinimumSlope = 1e-3;

        /// <summary>
        /// D8 direction quantization: angle between each river's D8 step and the continuous
        /// steepest-descent direction of the SAME discretized (corrected) surface, estimated by a
        /// least-squares plane through the cell's neighbours (filled depressions and near-flat
        /// cells are excluded: their direction is arbitrary). This isolates the error caused by
        /// restricting flow to 8 directions (a topology property, at most ~22.5 degrees on an
        /// ideal plane), and measures how much of it the smoothed trajectories recover.
        /// Comparing with the continuous terrain gradient instead would mix in a scale mismatch
        /// (valley-floor rivers vs cell-scale valley-side slopes and surface detail).
        /// </summary>
        public static DirectionDeviation MeasureDirectionDeviation(RiverNetwork network)
        {
            HydrologyResult hydrology = network.Hydrology;
            HydrologyGrid grid = hydrology.Grid;
            double rawSum = 0, pathSum = 0;
            int rawCount = 0, pathCount = 0, rawOver = 0, pathOver = 0;

            for (int node = 0; node < network.NodeCount; node++)
            {
                int cell = network.GetNodeCell(node);
                int receiver = hydrology.GetReceiver(cell);
                if (receiver < 0 || hydrology.IsInDepression(cell) || !TryGridDownhill(hydrology, cell, out Vector3 downhill))
                {
                    continue;
                }
                Vector3 p = grid.GetCenter(cell);
                float angle = Angle(grid.GetCenter(receiver) - p, p, downhill);
                rawSum += angle;
                rawOver += angle > 22.5f ? 1 : 0;
                rawCount++;
            }

            for (int river = 0; river < network.RiverCount; river++)
            {
                for (int i = network.GetPathStart(river); i < network.GetPathEnd(river) - 1; i++)
                {
                    Vector3 a = network.GetPathPoint(i);
                    Vector3 b = network.GetPathPoint(i + 1);
                    Vector3 mid = (a + b).normalized;
                    int cell = grid.CellFromDirection(mid);
                    if (!hydrology.IsLand(cell) || hydrology.GetReceiver(cell) < 0 || hydrology.IsInDepression(cell)
                        || !TryGridDownhill(hydrology, cell, out Vector3 downhill))
                    {
                        continue;
                    }
                    float angle = Angle(b - a, mid, downhill);
                    pathSum += angle;
                    pathOver += angle > 22.5f ? 1 : 0;
                    pathCount++;
                }
            }

            return new DirectionDeviation
            {
                RawMeanDegrees = rawCount > 0 ? (float)(rawSum / rawCount) : 0f,
                PathMeanDegrees = pathCount > 0 ? (float)(pathSum / pathCount) : 0f,
                RawOver22Fraction = rawCount > 0 ? rawOver / (float)rawCount : 0f,
                PathOver22Fraction = pathCount > 0 ? pathOver / (float)pathCount : 0f,
                Samples = rawCount,
            };
        }

        /// <summary>
        /// "Comb" diagnostic: share of first-order rivers whose overall direction (source to end)
        /// is aligned with one of the 8 D8 axes of their cube face. Endpoints do not depend on
        /// smoothing, so this measures the drainage TOPOLOGY, not the rendering.
        /// </summary>
        public static GridAlignment MeasureGridAlignment(RiverNetwork network)
        {
            HydrologyGrid grid = network.Hydrology.Grid;
            int rivers = 0, aligned = 0;
            for (int river = 0; river < network.RiverCount; river++)
            {
                River data = network.GetRiver(river);
                if (data.MaxStrahler != 1 || data.NodeCount < MinimumCells)
                {
                    continue;
                }
                grid.GetFaceCoordinates(data.SourceCell, out int face, out int i0, out int j0);
                grid.GetFaceCoordinates(data.EndCell, out int endFace, out int i1, out int j1);
                if (face != endFace)
                {
                    continue; // axes are only defined within one face
                }
                float angle = Mathf.Atan2(j1 - j0, i1 - i0) * Mathf.Rad2Deg;
                float offset = Mathf.Abs(Mathf.Repeat(angle + 22.5f, 45f) - 22.5f);
                rivers++;
                aligned += offset <= 5f ? 1 : 0;
            }
            return new GridAlignment
            {
                Rivers = rivers,
                AlignedFraction = rivers > 0 ? aligned / (float)rivers : 0f,
                ExpectedFraction = 80f / 360f,
            };
        }

        /// <summary>Steepest descent of the corrected surface around a cell (plane fit).</summary>
        static bool TryGridDownhill(HydrologyResult hydrology, int cell, out Vector3 downhill)
        {
            HydrologyGrid grid = hydrology.Grid;
            Vector3 p = grid.GetCenter(cell);
            Vector3 e1 = Vector3.Cross(p, Mathf.Abs(p.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 e2 = Vector3.Cross(p, e1);
            double sxx = 0, sxy = 0, syy = 0, sxf = 0, syf = 0;
            double center = hydrology.GetFilledElevation(cell);
            for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
            {
                int other = grid.GetNeighbor(k);
                Vector3 v = grid.GetCenter(other) - p;
                double x = Vector3.Dot(v, e1), y = Vector3.Dot(v, e2);
                double df = hydrology.GetFilledElevation(other) - center;
                sxx += x * x;
                sxy += x * y;
                syy += y * y;
                sxf += x * df;
                syf += y * df;
            }
            double determinant = sxx * syy - sxy * sxy;
            downhill = Vector3.zero;
            if (Math.Abs(determinant) < 1e-30)
            {
                return false;
            }
            double gx = (syy * sxf - sxy * syf) / determinant;
            double gy = (sxx * syf - sxy * sxf) / determinant;
            // Near-flat cells (resolved flats, epsilon slopes) have no meaningful direction.
            if (gx * gx + gy * gy < MinimumSlope * MinimumSlope)
            {
                return false;
            }
            downhill = -(e1 * (float)gx + e2 * (float)gy);
            downhill.Normalize();
            return true;
        }

        static float Angle(Vector3 step, Vector3 at, Vector3 downhill)
        {
            Vector3 tangent = step - at * Vector3.Dot(step, at);
            return tangent.sqrMagnitude < 1e-20f ? 0f : Vector3.Angle(tangent, downhill);
        }
    }
}
