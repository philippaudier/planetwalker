using System.Collections.Generic;
using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Finds points on the DISPLAYED terrain mesh (not on the continuous elevation function).
    /// Between vertices the render mesh is piecewise linear and can lie above the true surface
    /// in valleys, so overlays placed on the analytic surface would sink into the mesh. This
    /// sampler intersects the ray from the planet center with the exact render triangle.
    /// </summary>
    public sealed class PlanetSurfaceSampler
    {
        const int NewtonIterations = 5;
        const float NewtonStep = 1e-3f;

        readonly List<Vector3>[] faceVertices = new List<Vector3>[CubeSphereGenerator.FaceCount];
        int resolution;

        public PlanetSurfaceSampler()
        {
            for (int face = 0; face < faceVertices.Length; face++)
            {
                faceVertices[face] = new List<Vector3>();
            }
        }

        public int Resolution => resolution;

        /// <summary>Reads the current vertex positions of the six face meshes (object space).</summary>
        public void Capture(Mesh[] faceMeshes, int meshResolution)
        {
            resolution = meshResolution;
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                faceMeshes[face].GetVertices(faceVertices[face]);
            }
        }

        /// <summary>Uses explicit face vertex lists (same layout as the face meshes).</summary>
        public void Capture(IReadOnlyList<Vector3>[] faces, int meshResolution)
        {
            resolution = meshResolution;
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                faceVertices[face].Clear();
                for (int i = 0; i < faces[face].Count; i++)
                {
                    faceVertices[face].Add(faces[face][i]);
                }
            }
        }

        /// <summary>Displayed vertex (x, y) of a face grid (object space, same layout as the face meshes).</summary>
        public Vector3 GetVertex(int face, int x, int y)
        {
            return faceVertices[face][x + y * (resolution + 1)];
        }

        /// <summary>
        /// Render quad (face, x0, y0) containing a unit direction, and the position inside it
        /// (tx, ty in [0, 1]); the quad is split along its (x0, y0) - (x0 + 1, y0 + 1) diagonal.
        /// </summary>
        public void Locate(Vector3 direction, out int face, out int x0, out int y0, out float tx, out float ty)
        {
            face = DominantFace(direction);
            CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            float depth = Vector3.Dot(direction, normal);
            float u = Vector3.Dot(direction, axisA) / depth;
            float v = Vector3.Dot(direction, axisB) / depth;

            // Invert the cube-to-sphere mapping: find grid parameters (a, b) whose vertex
            // direction projects to (u, v). Start from the gnomonic guess.
            float a = Mathf.Clamp(u, -1f, 1f);
            float b = Mathf.Clamp(v, -1f, 1f);
            for (int iteration = 0; iteration < NewtonIterations; iteration++)
            {
                Project(normal, axisA, axisB, a, b, out float pu, out float pv);
                Project(normal, axisA, axisB, a + NewtonStep, b, out float au, out float av);
                Project(normal, axisA, axisB, a, b + NewtonStep, out float bu, out float bv);
                float j00 = (au - pu) / NewtonStep, j10 = (av - pv) / NewtonStep;
                float j01 = (bu - pu) / NewtonStep, j11 = (bv - pv) / NewtonStep;
                float determinant = j00 * j11 - j01 * j10;
                if (Mathf.Abs(determinant) < 1e-12f)
                {
                    break;
                }
                float ru = u - pu, rv = v - pv;
                a = Mathf.Clamp(a + (j11 * ru - j01 * rv) / determinant, -1f, 1f);
                b = Mathf.Clamp(b + (j00 * rv - j10 * ru) / determinant, -1f, 1f);
            }

            int n = resolution;
            float fx = (a + 1f) * 0.5f * n;
            float fy = (b + 1f) * 0.5f * n;
            x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, n - 1);
            y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, n - 1);
            tx = fx - x0;
            ty = fy - y0;
        }

        /// <summary>Point of the rendered terrain in a unit direction (object space).</summary>
        public Vector3 Sample(Vector3 direction)
        {
            Locate(direction, out int face, out int x0, out int y0, out float tx, out float ty);
            int edge = resolution + 1;
            List<Vector3> vertices = faceVertices[face];
            int i = x0 + y0 * edge;
            Vector3 p00 = vertices[i];
            Vector3 p11 = vertices[i + edge + 1];
            // Same diagonal split as CubeSphereGenerator.FillFaceTriangles.
            Vector3 third = ty > tx ? vertices[i + edge] : vertices[i + 1];
            Vector3 planeNormal = Vector3.Cross(p11 - p00, third - p00);
            float denominator = Vector3.Dot(direction, planeNormal);
            if (Mathf.Abs(denominator) < 1e-12f)
            {
                return direction * p00.magnitude;
            }
            return direction * (Vector3.Dot(p00, planeNormal) / denominator);
        }

        static void Project(Vector3 normal, Vector3 axisA, Vector3 axisB, float a, float b, out float u, out float v)
        {
            Vector3 s = CubeSphereGenerator.CubeToSphere(normal + axisA * a + axisB * b);
            float depth = Vector3.Dot(s, normal);
            u = Vector3.Dot(s, axisA) / depth;
            v = Vector3.Dot(s, axisB) / depth;
        }

        static int DominantFace(Vector3 d)
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
    }
}
