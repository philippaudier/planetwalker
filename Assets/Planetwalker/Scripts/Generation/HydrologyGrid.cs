using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// Global spherical cell graph used by hydrology, independent of the render mesh.
    /// </summary>
    /// <remarks>
    /// Six cube faces of <c>resolution x resolution</c> cells using the equi-angular cube map
    /// (grid parameter a maps to tan(a * pi / 4)), which keeps cell areas within ~1.4x of each
    /// other and has an exact inverse for direction -> cell lookups. Cells have unique ids
    /// (face * n * n + j * n + i) and explicit, symmetric 8-neighbourhoods that cross cube
    /// edges and corners (corner cells have 7 neighbours). Cell areas are exact spherical
    /// areas in steradians (they sum to 4 pi).
    ///
    /// The grid only depends on its resolution and can be shared by every planet.
    /// </remarks>
    public sealed class HydrologyGrid
    {
        const int MaxNeighborsPerCell = 10;

        readonly Vector3[] centers;
        readonly float[] areas;
        readonly int[] neighborStart;
        readonly int[] neighbors;
        readonly float[] neighborDistances;
        readonly float[] centerTangents;

        public HydrologyGrid(int resolution)
        {
            Resolution = Mathf.Max(2, resolution);
            int n = Resolution;
            CellCount = CubeSphereGenerator.FaceCount * n * n;

            centerTangents = new float[n];
            var cornerTangents = new float[n + 1];
            for (int i = 0; i < n; i++)
            {
                centerTangents[i] = Mathf.Tan(((2f * i + 1f) / n - 1f) * Mathf.PI * 0.25f);
            }
            for (int i = 0; i <= n; i++)
            {
                cornerTangents[i] = Mathf.Tan((2f * i / n - 1f) * Mathf.PI * 0.25f);
            }

            centers = new Vector3[CellCount];
            areas = new float[CellCount];
            double totalArea = 0;
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int cell = Index(face, i, j);
                        centers[cell] = (normal + axisA * centerTangents[i] + axisB * centerTangents[j]).normalized;
                        Vector3 c00 = (normal + axisA * cornerTangents[i] + axisB * cornerTangents[j]).normalized;
                        Vector3 c10 = (normal + axisA * cornerTangents[i + 1] + axisB * cornerTangents[j]).normalized;
                        Vector3 c11 = (normal + axisA * cornerTangents[i + 1] + axisB * cornerTangents[j + 1]).normalized;
                        Vector3 c01 = (normal + axisA * cornerTangents[i] + axisB * cornerTangents[j + 1]).normalized;
                        double area = SphericalTriangleArea(c00, c10, c11) + SphericalTriangleArea(c00, c11, c01);
                        areas[cell] = (float)area;
                        // Sum the stored values so accumulation conserves area exactly.
                        totalArea += areas[cell];
                    }
                }
            }
            TotalArea = totalArea;

            // Neighbourhoods: fixed-capacity scratch lists, symmetrized, then compacted (CSR).
            var scratch = new int[CellCount * MaxNeighborsPerCell];
            var counts = new int[CellCount];
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int cell = Index(face, i, j);
                        for (int dj = -1; dj <= 1; dj++)
                        {
                            for (int di = -1; di <= 1; di++)
                            {
                                if (di == 0 && dj == 0)
                                {
                                    continue;
                                }
                                int other = Neighbor(face, i + di, j + dj);
                                if (other != cell)
                                {
                                    AddUnique(scratch, counts, cell, other);
                                }
                            }
                        }
                    }
                }
            }
            for (int cell = 0; cell < CellCount; cell++)
            {
                for (int k = 0; k < counts[cell]; k++)
                {
                    AddUnique(scratch, counts, scratch[cell * MaxNeighborsPerCell + k], cell);
                }
            }

            neighborStart = new int[CellCount + 1];
            for (int cell = 0; cell < CellCount; cell++)
            {
                neighborStart[cell + 1] = neighborStart[cell] + counts[cell];
            }
            neighbors = new int[neighborStart[CellCount]];
            neighborDistances = new float[neighbors.Length];
            for (int cell = 0; cell < CellCount; cell++)
            {
                int start = neighborStart[cell];
                System.Array.Copy(scratch, cell * MaxNeighborsPerCell, neighbors, start, counts[cell]);
                System.Array.Sort(neighbors, start, counts[cell]);
                for (int k = start; k < start + counts[cell]; k++)
                {
                    neighborDistances[k] = (centers[neighbors[k]] - centers[cell]).magnitude;
                }
            }
        }

        public int Resolution { get; }

        public int CellCount { get; }

        /// <summary>Sum of all cell areas, in steradians (4 pi up to rounding).</summary>
        public double TotalArea { get; }

        public int Index(int face, int i, int j)
        {
            return face * Resolution * Resolution + j * Resolution + i;
        }

        public void GetFaceCoordinates(int cell, out int face, out int i, out int j)
        {
            int perFace = Resolution * Resolution;
            face = cell / perFace;
            int local = cell - face * perFace;
            j = local / Resolution;
            i = local - j * Resolution;
        }

        public Vector3 GetCenter(int cell)
        {
            return centers[cell];
        }

        /// <summary>Exact spherical area of the cell, in steradians.</summary>
        public float GetArea(int cell)
        {
            return areas[cell];
        }

        /// <summary>Neighbours of a cell are at indices [GetNeighborStart, GetNeighborEnd).</summary>
        public int GetNeighborStart(int cell)
        {
            return neighborStart[cell];
        }

        public int GetNeighborEnd(int cell)
        {
            return neighborStart[cell + 1];
        }

        public int GetNeighbor(int index)
        {
            return neighbors[index];
        }

        /// <summary>Chord distance between the two cell centers (unit sphere, ~ radians).</summary>
        public float GetNeighborDistance(int index)
        {
            return neighborDistances[index];
        }

        /// <summary>Cell containing a unit direction (exact inverse of the equi-angular map).</summary>
        public int CellFromDirection(Vector3 direction)
        {
            float ax = Mathf.Abs(direction.x);
            float ay = Mathf.Abs(direction.y);
            float az = Mathf.Abs(direction.z);
            int face;
            if (ay >= ax && ay >= az)
            {
                face = direction.y >= 0f ? 0 : 1;
            }
            else if (ax >= az)
            {
                face = direction.x >= 0f ? 3 : 2;
            }
            else
            {
                face = direction.z >= 0f ? 4 : 5;
            }
            CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            float depth = Vector3.Dot(direction, normal);
            float a = Mathf.Atan(Vector3.Dot(direction, axisA) / depth) * (4f / Mathf.PI);
            float b = Mathf.Atan(Vector3.Dot(direction, axisB) / depth) * (4f / Mathf.PI);
            return Index(face, ToCellIndex(a), ToCellIndex(b));
        }

        /// <summary>
        /// Local center spacing at a direction (great-circle radians, the smaller of the two grid
        /// directions): from 1 x (face centers) down to ~0.71 x (across face edges) the nominal
        /// pi / (2 n). Continuous over the sphere, also across cube edges, so kernels sized by it
        /// stay inside the 8-neighbourhood and vary smoothly.
        /// </summary>
        public float LocalSpacing(Vector3 direction)
        {
            float ax = Mathf.Abs(direction.x), ay = Mathf.Abs(direction.y), az = Mathf.Abs(direction.z);
            float major = Mathf.Max(ax, Mathf.Max(ay, az));
            float x, y;
            if (major == ay)
            {
                x = ax / ay;
                y = az / ay;
            }
            else if (major == ax)
            {
                x = ay / ax;
                y = az / ax;
            }
            else
            {
                x = ax / az;
                y = ay / az;
            }
            // Equi-angular map: tangent coordinate X = tan(a); |dp/da| = (1 + X^2) sqrt(1 + Y^2) / (1 + X^2 + Y^2).
            float r2 = 1f + x * x + y * y;
            float alongX = (1f + x * x) * Mathf.Sqrt(1f + y * y) / r2;
            float alongY = (1f + y * y) * Mathf.Sqrt(1f + x * x) / r2;
            return Mathf.Min(alongX, alongY) * Mathf.PI * 0.5f / Resolution;
        }

        int ToCellIndex(float parameter)
        {
            return Mathf.Clamp(Mathf.FloorToInt((parameter + 1f) * 0.5f * Resolution), 0, Resolution - 1);
        }

        /// <summary>
        /// Cell at grid position (i, j) of a face, following cube edges when (i, j) is
        /// outside the face: the coordinate along the shared edge is kept and the
        /// coordinate across it snaps to the first cell row of the adjacent face.
        /// </summary>
        int Neighbor(int face, int i, int j)
        {
            int n = Resolution;
            if (i >= 0 && i < n && j >= 0 && j < n)
            {
                return Index(face, i, j);
            }

            // Extended grid parameters (cell centers in [-1, 1], one step outside the face).
            float a = (2f * i + 1f) / n - 1f;
            float b = (2f * j + 1f) / n - 1f;
            CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            Vector3 p = normal + axisA * a + axisB * b;

            // Target face: dominant axis of the extended point (ties: x, then y, then z).
            float ax = Mathf.Abs(p.x);
            float ay = Mathf.Abs(p.y);
            float az = Mathf.Abs(p.z);
            int target;
            if (ax >= ay && ax >= az)
            {
                target = p.x >= 0f ? 3 : 2;
            }
            else if (ay >= az)
            {
                target = p.y >= 0f ? 0 : 1;
            }
            else
            {
                target = p.z >= 0f ? 4 : 5;
            }
            CubeSphereGenerator.GetFaceAxes(target, out _, out Vector3 targetA, out Vector3 targetB);
            return Index(target, ToCellIndex(Vector3.Dot(p, targetA)), ToCellIndex(Vector3.Dot(p, targetB)));
        }

        static void AddUnique(int[] scratch, int[] counts, int cell, int other)
        {
            int start = cell * MaxNeighborsPerCell;
            for (int k = 0; k < counts[cell]; k++)
            {
                if (scratch[start + k] == other)
                {
                    return;
                }
            }
            if (counts[cell] < MaxNeighborsPerCell)
            {
                scratch[start + counts[cell]++] = other;
            }
        }

        /// <summary>Van Oosterom-Strackee formula for unit vectors.</summary>
        static double SphericalTriangleArea(Vector3 a, Vector3 b, Vector3 c)
        {
            double triple = Vector3.Dot(a, Vector3.Cross(b, c));
            double denominator = 1.0 + Vector3.Dot(a, b) + Vector3.Dot(b, c) + Vector3.Dot(c, a);
            return 2.0 * System.Math.Abs(System.Math.Atan2(triple, denominator));
        }
    }
}
