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
