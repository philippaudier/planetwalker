using System;
using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Terrain
{
    /// <summary>
    /// Coordinate mathematics of the adaptive terrain (V0.2.0A), built on the existing cube-sphere projection
    /// (<see cref="CubeSphereGenerator"/>: same faces, axes and cube-to-sphere mapping), never a second one.
    /// </summary>
    /// <remarks>
    /// Face coordinates (a, b) are in [-1, 1] along the face's A and B axes, exactly the "grid coordinates" of the
    /// existing mesh. Every adaptive vertex lies on a per-face integer lattice: patch (L, x, y) with r subdivisions
    /// owns lattice indices [x r, (x + 1) r] of a lattice of N = r 2^L cells, and its face coordinate is computed by
    /// <see cref="CubeSphereGenerator.GridCoordinate"/>(index, N) — the formula of the existing planet mesh. IEEE
    /// division is correctly rounded, so the same rational value always gives the same float: a vertex shared by
    /// two patches (same face, any level, or across a face edge) has bit-identical cube coordinates, hence a
    /// bit-identical direction, elevation and position; and when N equals the planet mesh resolution, adaptive
    /// vertices equal the existing mesh vertices bit for bit. Exactness needs N &lt;= 2^24 (<see cref="MaxLatticeSize"/>).
    /// </remarks>
    public static class CubeSphereCoordinates
    {
        /// <summary>Largest per-face lattice with exact float coordinates (integers up to 2^24 are exact).</summary>
        public const int MaxLatticeSize = 1 << 24;

        const int NewtonIterations = 12;

        /// <summary>Deepest level usable with patches of <paramref name="resolution"/> subdivisions (exact lattice).</summary>
        public static int MaxLevelForResolution(int resolution)
        {
            int level = 0;
            while (level < PlanetPatchId.MaxLevel && (long)resolution << (level + 1) <= MaxLatticeSize)
            {
                level++;
            }
            return level;
        }

        /// <summary>Unit direction of face coordinates (a, b) in [-1, 1], with the existing mapping and operation order.</summary>
        public static Vector3 FaceToDirection(int face, float a, float b)
        {
            CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            return CubeSphereGenerator.CubeToSphere(normal + axisA * a + axisB * b);
        }

        /// <summary>Point on the [-1, 1] cube for face coordinates (a, b).</summary>
        public static Vector3 FaceToCube(int face, float a, float b)
        {
            CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            return normal + axisA * a + axisB * b;
        }

        /// <summary>Direction of lattice vertex (i, j) of a face lattice of <paramref name="size"/> cells (as the existing mesh at resolution = size).</summary>
        public static Vector3 LatticeDirection(int face, int i, int j, int size)
        {
            if (size > MaxLatticeSize)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "Lattice larger than 2^24: coordinates would not be exact.");
            }
            return FaceToDirection(face, CubeSphereGenerator.GridCoordinate(i, size), CubeSphereGenerator.GridCoordinate(j, size));
        }

        /// <summary>
        /// <see cref="LatticeDirection"/> in double precision (V0.2.0C): the same rational lattice coordinate and the
        /// same cube-to-sphere mapping evaluated in double, so a vertex shared by two patches (any levels, across a
        /// face edge) still has a bit-identical direction, now with ~1e-16 rather than ~6e-8 of angular precision
        /// (0.4 m at Earth scale, visible as moire at a 100 m altitude). Positions and fine detail use it; the float
        /// direction keeps feeding what must match the legacy faces bit for bit.
        /// </summary>
        public static Vector3d LatticeDirectionD(int face, int i, int j, int size)
        {
            if (size > MaxLatticeSize)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "Lattice larger than 2^24: coordinates would not be exact.");
            }
            CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            double a = (2.0 * i - size) / size, b = (2.0 * j - size) / size;
            var p = new Vector3d(normal.x + axisA.x * a + axisB.x * b, normal.y + axisA.y * a + axisB.y * b, normal.z + axisA.z * a + axisB.z * b);
            double x2 = p.x * p.x, y2 = p.y * p.y, z2 = p.z * p.z;
            var s = new Vector3d(
                p.x * Math.Sqrt(1.0 - y2 * 0.5 - z2 * 0.5 + y2 * z2 / 3.0),
                p.y * Math.Sqrt(1.0 - x2 * 0.5 - z2 * 0.5 + x2 * z2 / 3.0),
                p.z * Math.Sqrt(1.0 - x2 * 0.5 - y2 * 0.5 + x2 * y2 / 3.0));
            return s.Normalized;
        }

        /// <summary>Direction of the patch center in double precision.</summary>
        public static Vector3d CenterDirectionD(PlanetPatchId id)
        {
            return LatticeDirectionD(id.Face, id.X * 2 + 1, id.Y * 2 + 1, id.GridSize * 2);
        }

        /// <summary>Cube face whose region contains the direction (largest component; ties resolved like the surface sampler).</summary>
        public static int DominantFace(Vector3 d)
        {
            float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
            if (ay >= ax && ay >= az)
            {
                return d.y >= 0f ? 0 : 1;
            }
            if (ax >= az)
            {
                return d.x >= 0f ? 3 : 2;
            }
            return d.z >= 0f ? 4 : 5;
        }

        /// <summary>
        /// Inverse projection: face and face coordinates (a, b) of a direction (any length). Newton iterations in
        /// double precision on the existing cube-to-sphere mapping, from the gnomonic guess; accurate to ~1e-12 in
        /// (a, b) before the final float rounding.
        /// </summary>
        public static int DirectionToFace(Vector3 direction, out float a, out float b)
        {
            int face = DominantFace(direction);
            CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            double dn = Vector3.Dot(direction, normal);
            double u = Vector3.Dot(direction, axisA) / dn;
            double v = Vector3.Dot(direction, axisB) / dn;
            double x = Math.Max(-1.0, Math.Min(1.0, u)), y = Math.Max(-1.0, Math.Min(1.0, v));
            for (int iteration = 0; iteration < NewtonIterations; iteration++)
            {
                Gnomonic(x, y, out double pu, out double pv);
                const double h = 1e-7;
                Gnomonic(x + h, y, out double au, out double av);
                Gnomonic(x, y + h, out double bu, out double bv);
                double j00 = (au - pu) / h, j10 = (av - pv) / h, j01 = (bu - pu) / h, j11 = (bv - pv) / h;
                double det = j00 * j11 - j01 * j10;
                if (Math.Abs(det) < 1e-18)
                {
                    break;
                }
                double ru = u - pu, rv = v - pv;
                x = Math.Max(-1.0, Math.Min(1.0, x + (j11 * ru - j01 * rv) / det));
                y = Math.Max(-1.0, Math.Min(1.0, y + (j00 * rv - j10 * ru) / det));
                if (Math.Abs(ru) + Math.Abs(rv) < 1e-15)
                {
                    break;
                }
            }
            a = (float)x;
            b = (float)y;
            return face;
        }

        /// <summary>
        /// The existing cube-to-sphere mapping in double precision, expressed in the face frame (the mapping is
        /// symmetric in the three axes, so the face-local form (a, b, 1) is exact), then projected gnomonically.
        /// </summary>
        static void Gnomonic(double a, double b, out double u, out double v)
        {
            double a2 = a * a, b2 = b * b;
            double sa = a * Math.Sqrt(1.0 - b2 * 0.5 - 0.5 + b2 / 3.0);
            double sb = b * Math.Sqrt(1.0 - a2 * 0.5 - 0.5 + a2 / 3.0);
            double sn = Math.Sqrt(1.0 - a2 * 0.5 - b2 * 0.5 + a2 * b2 / 3.0);
            u = sa / sn;
            v = sb / sn;
        }

        /// <summary>Patch containing a direction at a level.</summary>
        public static PlanetPatchId PatchAt(Vector3 direction, int level)
        {
            int face = DirectionToFace(direction, out float a, out float b);
            int size = 1 << level;
            int x = Mathf.Clamp((int)Math.Floor((a + 1.0) * 0.5 * size), 0, size - 1);
            int y = Mathf.Clamp((int)Math.Floor((b + 1.0) * 0.5 * size), 0, size - 1);
            return new PlanetPatchId(face, level, x, y);
        }

        /// <summary>Face-coordinate bounds of a patch (exact lattice values).</summary>
        public static void FaceBounds(PlanetPatchId id, out float aMin, out float bMin, out float aMax, out float bMax)
        {
            int size = id.GridSize;
            aMin = CubeSphereGenerator.GridCoordinate(id.X, size);
            aMax = CubeSphereGenerator.GridCoordinate(id.X + 1, size);
            bMin = CubeSphereGenerator.GridCoordinate(id.Y, size);
            bMax = CubeSphereGenerator.GridCoordinate(id.Y + 1, size);
        }

        /// <summary>True when face coordinates (a, b) of the patch's face lie inside the patch (closed bounds).</summary>
        public static bool Contains(PlanetPatchId id, float a, float b)
        {
            FaceBounds(id, out float aMin, out float bMin, out float aMax, out float bMax);
            return a >= aMin && a <= aMax && b >= bMin && b <= bMax;
        }

        /// <summary>Corner direction: 0 = south-west, 1 = south-east, 2 = north-west, 3 = north-east.</summary>
        public static Vector3 CornerDirection(PlanetPatchId id, int corner)
        {
            int size = id.GridSize;
            return LatticeDirection(id.Face, id.X + (corner & 1), id.Y + (corner >> 1), size);
        }

        /// <summary>Direction of the patch center (center of its face-coordinate square).</summary>
        public static Vector3 CenterDirection(PlanetPatchId id)
        {
            int size = id.GridSize * 2;
            return LatticeDirection(id.Face, id.X * 2 + 1, id.Y * 2 + 1, size);
        }

        /// <summary>
        /// Angular radius (radians) of the patch around its center direction: the largest angle to its corners and
        /// edge midpoints (the boundary extremes of a cube-sphere patch).
        /// </summary>
        public static float AngularRadius(PlanetPatchId id)
        {
            Vector3 center = CenterDirection(id);
            int size = id.GridSize * 2;
            float maxAngle = 0f;
            for (int j = 0; j <= 2; j++)
            {
                for (int i = 0; i <= 2; i++)
                {
                    if (i == 1 && j == 1)
                    {
                        continue;
                    }
                    Vector3 d = LatticeDirection(id.Face, id.X * 2 + i, id.Y * 2 + j, size);
                    float dot = Mathf.Clamp(Vector3.Dot(center, d), -1f, 1f);
                    maxAngle = Mathf.Max(maxAngle, (float)Math.Acos(dot));
                }
            }
            return maxAngle;
        }
    }
}
