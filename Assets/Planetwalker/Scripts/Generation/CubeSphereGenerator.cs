using System;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// Cube-sphere topology: six cube faces, each subdivided into a regular grid and
    /// projected onto the unit sphere.
    /// </summary>
    /// <remarks>
    /// Seam guarantee: grid coordinates are computed as (2i - n) / n, which is exactly
    /// antisymmetric in IEEE arithmetic, and cube-face axes only contain 0 / ±1. A point
    /// shared by two faces therefore produces bit-identical cube coordinates, hence a
    /// bit-identical sphere direction, elevation and normal. Faces cannot crack apart.
    /// </remarks>
    public static class CubeSphereGenerator
    {
        public const int FaceCount = 6;

        static readonly Vector3[] FaceNormals =
        {
            Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back,
        };

        static readonly string[] FaceNames = { "+Y", "-Y", "-X", "+X", "+Z", "-Z" };

        public static string GetFaceName(int face)
        {
            return FaceNames[face];
        }

        /// <summary>
        /// Face frame: <paramref name="normal"/> points out of the face, and
        /// Cross(axisA, axisB) == normal, which makes the triangle winding outward-facing.
        /// </summary>
        public static void GetFaceAxes(int face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB)
        {
            normal = FaceNormals[face];
            axisA = new Vector3(normal.y, normal.z, normal.x);
            axisB = Vector3.Cross(normal, axisA);
        }

        public static int VerticesPerEdge(int resolution)
        {
            return resolution + 1;
        }

        public static int VerticesPerFace(int resolution)
        {
            return (resolution + 1) * (resolution + 1);
        }

        public static int IndicesPerFace(int resolution)
        {
            return resolution * resolution * 6;
        }

        /// <summary>Grid coordinate in [-1, 1] for vertex index [0, resolution].</summary>
        public static float GridCoordinate(int index, int resolution)
        {
            return (2 * index - resolution) / (float)resolution;
        }

        /// <summary>Unit sphere direction of the grid vertex (x, y) of a face.</summary>
        public static Vector3 GetSphereDirection(int face, int x, int y, int resolution)
        {
            GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            Vector3 cubePoint = normal + axisA * GridCoordinate(x, resolution) + axisB * GridCoordinate(y, resolution);
            return CubeToSphere(cubePoint);
        }

        /// <summary>
        /// Maps a point on the surface of the [-1, 1] cube to the unit sphere. This mapping
        /// distributes vertices more evenly than a plain normalization (less crowding
        /// near cube corners).
        /// </summary>
        /// <summary>Cube face whose region contains a direction (largest component; the generator's numbering).</summary>
        public static int DominantFace(Vector3d d)
        {
            double ax = Math.Abs(d.x), ay = Math.Abs(d.y), az = Math.Abs(d.z);
            if (ay >= ax && ay >= az)
            {
                return d.y >= 0 ? 0 : 1;
            }
            if (ax >= az)
            {
                return d.x >= 0 ? 3 : 2;
            }
            return d.z >= 0 ? 4 : 5;
        }

        /// <summary>
        /// Inverse projection in double precision (V0.2.0C): the face and face coordinates (a, b) in [-1, 1] of a
        /// direction, by Newton iterations on the cube-to-sphere mapping from the gnomonic guess (accurate to
        /// ~1e-13; the float inverse of the adaptive terrain delegates here).
        /// </summary>
        public static int DirectionToFaceCoordinates(Vector3d direction, out double a, out double b)
        {
            int face = DominantFace(direction);
            FaceCoordinates(direction, face, 1.0, out a, out b);
            return face;
        }

        /// <summary>
        /// Coordinates of a direction in a given face's parametrization (V0.2.0E), continued past the face's edges up to
        /// |a|, |b| = <paramref name="limit"/> (1: the face itself, as <see cref="DirectionToFaceCoordinates"/>).
        /// The direction must be in that face's hemisphere.
        /// </summary>
        public static void FaceCoordinates(Vector3d direction, int face, double limit, out double a, out double b)
        {
            GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            double dn = Vector3d.Dot(direction, new Vector3d(normal));
            double u = Vector3d.Dot(direction, new Vector3d(axisA)) / dn;
            double v = Vector3d.Dot(direction, new Vector3d(axisB)) / dn;
            double x = Math.Max(-limit, Math.Min(limit, u)), y = Math.Max(-limit, Math.Min(limit, v));
            for (int iteration = 0; iteration < 12; iteration++)
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
                x = Math.Max(-limit, Math.Min(limit, x + (j11 * ru - j01 * rv) / det));
                y = Math.Max(-limit, Math.Min(limit, y + (j00 * rv - j10 * ru) / det));
                if (Math.Abs(ru) + Math.Abs(rv) < 1e-15)
                {
                    break;
                }
            }
            a = x;
            b = y;
        }

        /// <summary>The cube-to-sphere mapping in the face frame (a, b, 1), projected gnomonically (double).</summary>
        static void Gnomonic(double a, double b, out double u, out double v)
        {
            double a2 = a * a, b2 = b * b;
            double sa = a * Math.Sqrt(1.0 - b2 * 0.5 - 0.5 + b2 / 3.0);
            double sb = b * Math.Sqrt(1.0 - a2 * 0.5 - 0.5 + a2 / 3.0);
            double sn = Math.Sqrt(1.0 - a2 * 0.5 - b2 * 0.5 + a2 * b2 / 3.0);
            u = sa / sn;
            v = sb / sn;
        }

        public static Vector3 CubeToSphere(Vector3 p)
        {
            float x2 = p.x * p.x;
            float y2 = p.y * p.y;
            float z2 = p.z * p.z;
            var s = new Vector3(
                p.x * Mathf.Sqrt(1f - y2 * 0.5f - z2 * 0.5f + y2 * z2 / 3f),
                p.y * Mathf.Sqrt(1f - x2 * 0.5f - z2 * 0.5f + x2 * z2 / 3f),
                p.z * Mathf.Sqrt(1f - x2 * 0.5f - y2 * 0.5f + x2 * y2 / 3f));
            return s.normalized;
        }

        /// <summary>
        /// Fills the outward-facing (clockwise, Unity convention) triangle list of one
        /// face grid. Only depends on the resolution, so it can be cached.
        /// </summary>
        public static void FillFaceTriangles(int[] triangles, int resolution, int vertexOffset = 0, int indexOffset = 0)
        {
            int edge = resolution + 1;
            int t = indexOffset;
            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int i = vertexOffset + x + y * edge;
                    triangles[t++] = i;
                    triangles[t++] = i + edge + 1;
                    triangles[t++] = i + edge;
                    triangles[t++] = i;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + edge + 1;
                }
            }
        }
    }
}
