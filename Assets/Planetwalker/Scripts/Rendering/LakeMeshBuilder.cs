using System;
using System.Collections.Generic;
using System.Diagnostics;
using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Builds one combined water-surface mesh for the lakes of a <see cref="LakeSet"/>
    /// (V0.1.1D-B.1). Pure representation: never changes the terrain mesh or the lake data.
    /// </summary>
    /// <remarks>
    /// The hydrology grid and the displayed terrain mesh can have different resolutions, so the
    /// surface is built on its own refined equi-angular grid (2 to 4 subdivisions per hydrology
    /// cell, aligned with the cells) covering each lake footprint plus its shore ring (adjacent
    /// cells of the basin rim; cells beyond the outlet saddle are excluded):
    /// 1. Water exists where the TRUE terrain is below the level (marching triangles on
    ///    level - elevation: smooth, sub-cell shorelines, islands kept), restricted to the water
    ///    connected to the footprint (pockets behind land above the level stay dry).
    /// 2. Interior vertices sit exactly at the flat level, on a sphere of radius R (1 + level).
    /// 3. Shoreline vertices and the few vertices where the water region is cut (outlet lip) are
    ///    placed on the DISPLAYED terrain (+ a tiny lift): no gap and no floating edge, whatever
    ///    the mesh resolution. Where the displayed mesh is coarser than the terrain and rises
    ///    above the level inside the lake, interior vertices are raised onto it (documented
    ///    conservative fallback, counted as "conformed" in the diagnostics).
    /// Vertices are shared through integer cube coordinates, so the surface is continuous across
    /// cube faces. Triangles are checked for degeneracy and outward winding.
    /// </remarks>
    public sealed class LakeMeshBuilder
    {
        /// <summary>Lift of vertices placed on the displayed terrain (fraction of the radius).</summary>
        public const float SurfaceLift = 2e-5f;
        const float DegenerateRelativeArea = 1e-12f;

        readonly List<Vector3> vertices = new List<Vector3>();
        readonly List<Vector3> normals = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<int> triangles = new List<int>();
        readonly List<int> lakeVertexStart = new List<int>();
        LakeMeshDiagnostics diagnostics;

        // Per-lake scratch.
        readonly Dictionary<long, int> vertexIndex = new Dictionary<long, int>();
        readonly List<Vector3> gridDirections = new List<Vector3>();
        readonly List<float> gridField = new List<float>();
        readonly List<float> gridDisplayed = new List<float>();
        readonly List<int> gridQuadCount = new List<int>();
        readonly List<bool> gridCubeCorner = new List<bool>();
        readonly List<int> gridOutput = new List<int>();
        readonly List<int> quads = new List<int>();
        readonly Dictionary<long, int> edgeOutput = new Dictionary<long, int>();
        readonly List<int> region = new List<int>();

        public int LastVertexCount => vertices.Count;
        public float LastMilliseconds { get; private set; }

        /// <summary>Lake-grid subdivisions per hydrology cell used by the last build.</summary>
        public int LastSubdivision { get; private set; }

        public IReadOnlyList<Vector3> Vertices => vertices;
        public IReadOnlyList<Vector3> Normals => normals;
        public IReadOnlyList<int> Triangles => triangles;

        /// <summary>Per vertex: x = relative depth (0 shore .. 1 deepest), y = 1 when placed on the
        /// displayed terrain (shoreline, outlet lip), else 0.</summary>
        public IReadOnlyList<Vector2> WaterAttributes => uvs;

        /// <summary>Vertices of lake i are [GetLakeVertexStart(i), GetLakeVertexStart(i + 1)).</summary>
        public int GetLakeVertexStart(int lake) => lake < lakeVertexStart.Count ? lakeVertexStart[lake] : vertices.Count;

        public LakeMeshDiagnostics LastDiagnostics => diagnostics;

        public void Build(Mesh mesh, LakeSet lakes, PlanetInstance planet, PlanetSurfaceSampler sampler)
        {
            var stopwatch = Stopwatch.StartNew();
            BuildGeometry(lakes, planet, sampler);
            mesh.Clear();
            mesh.name = "Planet Lakes";
            mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (planet.Radius * 2.4f));
            LastMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// True if a direction lies under the water of a lake: true terrain below the level, in the
        /// lake footprint or in its shore ring (where the water surface is built, see
        /// <see cref="IsShoreCell"/>).
        /// </summary>
        public static bool IsSubmerged(LakeSet lakes, PlanetInstance planet, int lake, Vector3 direction)
        {
            return IsSubmerged(lakes, planet.Height.GetElevation, lake, direction);
        }

        public static bool IsSubmerged(LakeSet lakes, Func<Vector3, float> elevation, int lake, Vector3 direction)
        {
            HydrologyGrid grid = lakes.Hydrology.Grid;
            int cell = grid.CellFromDirection(direction);
            bool near = lakes.GetLakeOfCell(cell) == lake || IsShoreCell(lakes, lake, cell);
            return near && elevation(direction) < lakes.GetLake(lake).Level;
        }

        /// <summary>
        /// Dry land cell next to the lake that belongs to its basin rim: its flood key is at or above
        /// the level (it holds the water back). Cells beyond the outlet saddle (flood key below the
        /// level: they drain away from the basin) are excluded, so the water never spills over them.
        /// </summary>
        public static bool IsShoreCell(LakeSet lakes, int lake, int cell)
        {
            if (lakes.GetLakeOfCell(cell) >= 0 || !lakes.Hydrology.IsLand(cell)
                || lakes.Depressions.GetFloodKey(cell) < lakes.GetLake(lake).Level)
            {
                return false;
            }
            HydrologyGrid grid = lakes.Hydrology.Grid;
            for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
            {
                if (lakes.GetLakeOfCell(grid.GetNeighbor(k)) == lake)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Lake-grid subdivisions per hydrology cell for a display mesh resolution.</summary>
        public static int Subdivision(int hydrologyResolution, int meshResolution)
        {
            int k = Mathf.CeilToInt(meshResolution / (float)Mathf.Max(1, hydrologyResolution));
            return k <= 2 ? 2 : 4;
        }

        /// <summary>Computes the geometry without touching any Unity object (testable).</summary>
        public void BuildGeometry(LakeSet lakes, PlanetInstance planet, PlanetSurfaceSampler sampler)
        {
            BuildGeometry(lakes, planet.Height.GetElevation, planet.Radius, sampler);
        }

        /// <summary>Same, for any elevation function (the true terrain the lakes were computed on).</summary>
        public void BuildGeometry(LakeSet lakes, Func<Vector3, float> elevation, float radius, PlanetSurfaceSampler sampler)
        {
            vertices.Clear();
            normals.Clear();
            uvs.Clear();
            triangles.Clear();
            lakeVertexStart.Clear();
            diagnostics = default;
            HydrologyGrid grid = lakes.Hydrology.Grid;
            int k = Subdivision(grid.Resolution, sampler.Resolution);
            LastSubdivision = k;
            for (int lake = 0; lake < lakes.Count; lake++)
            {
                lakeVertexStart.Add(vertices.Count);
                BuildLake(lakes, lake, elevation, radius, sampler, k);
            }
            diagnostics.Lakes = lakes.Count;
            diagnostics.Vertices = vertices.Count;
            diagnostics.Triangles = triangles.Count / 3;
        }

        void BuildLake(LakeSet lakes, int lakeId, Func<Vector3, float> elevation, float radius, PlanetSurfaceSampler sampler, int k)
        {
            HydrologyGrid grid = lakes.Hydrology.Grid;
            Lake lake = lakes.GetLake(lakeId);
            int n = grid.Resolution;
            int size = n * k;
            float level = lake.Level;
            float depthScale = Mathf.Max(lake.MaxDepth, 1e-6f);

            // Region: footprint + first ring (sorted cells: deterministic).
            region.Clear();
            var regionSet = new HashSet<int>();
            for (int i = 0; i < lake.CellCount; i++)
            {
                int cell = lakes.GetFootprintCell(lakeId, i);
                if (regionSet.Add(cell))
                {
                    region.Add(cell);
                }
                for (int e = grid.GetNeighborStart(cell); e < grid.GetNeighborEnd(cell); e++)
                {
                    int other = grid.GetNeighbor(e);
                    if (!regionSet.Contains(other) && IsShoreCell(lakes, lakeId, other))
                    {
                        regionSet.Add(other);
                        region.Add(other);
                    }
                }
            }
            region.Sort();

            vertexIndex.Clear();
            gridDirections.Clear();
            gridField.Clear();
            gridDisplayed.Clear();
            gridQuadCount.Clear();
            gridCubeCorner.Clear();
            gridOutput.Clear();
            quads.Clear();
            edgeOutput.Clear();

            // Quads of the refined grid covering the region; corners shared through cube keys.
            foreach (int cell in region)
            {
                grid.GetFaceCoordinates(cell, out int face, out int ci, out int cj);
                for (int b = 0; b < k; b++)
                {
                    for (int a = 0; a < k; a++)
                    {
                        int qi = ci * k + a, qj = cj * k + b;
                        int v00 = Vertex(face, qi, qj, size);
                        int v10 = Vertex(face, qi + 1, qj, size);
                        int v11 = Vertex(face, qi + 1, qj + 1, size);
                        int v01 = Vertex(face, qi, qj + 1, size);
                        quads.Add(v00);
                        quads.Add(v10);
                        quads.Add(v11);
                        quads.Add(v01);
                        gridQuadCount[v00]++;
                        gridQuadCount[v10]++;
                        gridQuadCount[v11]++;
                        gridQuadCount[v01]++;
                    }
                }
            }
            int vertexCount = gridDirections.Count;

            // Scalar field: level - true elevation (positive = under water).
            for (int v = 0; v < vertexCount; v++)
            {
                gridField.Add(level - elevation(gridDirections[v]));
                gridDisplayed.Add(float.NaN);
                gridOutput.Add(-1);
            }

            // Keep only the water connected to the footprint (seeds: footprint cell centers).
            var connected = new bool[vertexCount];
            var adjacencyStart = new int[vertexCount + 1];
            int quadCount = quads.Count / 4;
            for (int q = 0; q < quadCount; q++)
            {
                for (int c = 0; c < 4; c++)
                {
                    adjacencyStart[quads[4 * q + c] + 1] += c == 0 || c == 2 ? 3 : 2;
                }
            }
            for (int v = 0; v < vertexCount; v++)
            {
                adjacencyStart[v + 1] += adjacencyStart[v];
            }
            var adjacency = new int[adjacencyStart[vertexCount]];
            var fill = new int[vertexCount];
            for (int q = 0; q < quadCount; q++)
            {
                int v00 = quads[4 * q], v10 = quads[4 * q + 1], v11 = quads[4 * q + 2], v01 = quads[4 * q + 3];
                // Edges of both triangles (diagonal v00-v11, as in the triangulation below).
                AddEdge(adjacency, adjacencyStart, fill, v00, v10);
                AddEdge(adjacency, adjacencyStart, fill, v10, v11);
                AddEdge(adjacency, adjacencyStart, fill, v11, v01);
                AddEdge(adjacency, adjacencyStart, fill, v01, v00);
                AddEdge(adjacency, adjacencyStart, fill, v00, v11);
            }
            var stack = new Stack<int>();
            for (int i = 0; i < lake.CellCount; i++)
            {
                int cell = lakes.GetFootprintCell(lakeId, i);
                grid.GetFaceCoordinates(cell, out int face, out int ci, out int cj);
                int center = Vertex(face, ci * k + k / 2, cj * k + k / 2, size);
                if (center < vertexCount && gridField[center] > 0f && !connected[center])
                {
                    connected[center] = true;
                    stack.Push(center);
                }
            }
            while (stack.Count > 0)
            {
                int v = stack.Pop();
                for (int e = adjacencyStart[v]; e < adjacencyStart[v + 1]; e++)
                {
                    int other = adjacency[e];
                    if (!connected[other] && gridField[other] > 0f)
                    {
                        connected[other] = true;
                        stack.Push(other);
                    }
                }
            }
            for (int v = 0; v < vertexCount; v++)
            {
                if (gridField[v] > 0f && !connected[v])
                {
                    gridField[v] = -1e-9f; // disconnected pocket: dry
                    diagnostics.DisconnectedWetVertices++;
                }
            }

            // Marching triangles.
            for (int q = 0; q < quadCount; q++)
            {
                int v00 = quads[4 * q], v10 = quads[4 * q + 1], v11 = quads[4 * q + 2], v01 = quads[4 * q + 3];
                EmitTriangle(sampler, radius, level, depthScale, v00, v11, v01);
                EmitTriangle(sampler, radius, level, depthScale, v00, v10, v11);
            }
        }

        int Vertex(int face, int i, int j, int size)
        {
            CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            int a = 2 * i - size, b = 2 * j - size;
            int x = Mathf.RoundToInt(normal.x * size + axisA.x * a + axisB.x * b);
            int y = Mathf.RoundToInt(normal.y * size + axisA.y * a + axisB.y * b);
            int z = Mathf.RoundToInt(normal.z * size + axisA.z * a + axisB.z * b);
            long span = 2L * size + 1;
            long key = ((x + (long)size) * span + (y + size)) * span + (z + size);
            if (vertexIndex.TryGetValue(key, out int index))
            {
                return index;
            }
            index = gridDirections.Count;
            vertexIndex.Add(key, index);
            Vector3 direction = (normal + axisA * Warp(i, size) + axisB * Warp(j, size)).normalized;
            gridDirections.Add(direction);
            gridQuadCount.Add(0);
            gridCubeCorner.Add(Mathf.Abs(x) == size && Mathf.Abs(y) == size && Mathf.Abs(z) == size);
            if (gridField.Count > 0)
            {
                // Created after the field pass (only footprint-center lookups do this; never in practice).
                gridField.Add(-1f);
                gridDisplayed.Add(float.NaN);
                gridOutput.Add(-1);
            }
            return index;
        }

        /// <summary>Equi-angular warp, exact at the face borders and center (same map as the hydrology grid).</summary>
        static float Warp(int i, int size)
        {
            if (i == 0)
            {
                return -1f;
            }
            if (i == size)
            {
                return 1f;
            }
            if (2 * i == size)
            {
                return 0f;
            }
            return (float)Math.Tan((2.0 * i / size - 1.0) * Math.PI * 0.25);
        }

        static void AddEdge(int[] adjacency, int[] start, int[] fill, int a, int b)
        {
            adjacency[start[a] + fill[a]++] = b;
            adjacency[start[b] + fill[b]++] = a;
        }

        float Displayed(PlanetSurfaceSampler sampler, float radius, int v)
        {
            if (float.IsNaN(gridDisplayed[v]))
            {
                gridDisplayed[v] = sampler.Sample(gridDirections[v]).magnitude / radius - 1f;
            }
            return gridDisplayed[v];
        }

        int OutputGridVertex(PlanetSurfaceSampler sampler, float radius, float level, float depthScale, int v)
        {
            if (gridOutput[v] >= 0)
            {
                return gridOutput[v];
            }
            Vector3 direction = gridDirections[v];
            float displayed = Displayed(sampler, radius, v);
            int expected = gridCubeCorner[v] ? 3 : 4;
            bool cut = gridQuadCount[v] < expected; // water reaches the edge of the built region
            float height;
            if (cut)
            {
                height = displayed + SurfaceLift;
                diagnostics.LipVertices++;
                diagnostics.MaxLipDrop = Mathf.Max(diagnostics.MaxLipDrop, level - height);
            }
            else if (displayed + SurfaceLift > level)
            {
                height = displayed + SurfaceLift;
                diagnostics.ConformedVertices++;
            }
            else
            {
                height = level;
            }
            diagnostics.InteriorVertices++;
            gridOutput[v] = AddOutput(direction, radius, height, Mathf.Clamp01(gridField[v] / depthScale), cut ? 1f : 0f);
            return gridOutput[v];
        }

        int OutputEdgeVertex(PlanetSurfaceSampler sampler, float radius, int wet, int dry)
        {
            long key = ((long)Mathf.Min(wet, dry) << 32) | (uint)Mathf.Max(wet, dry);
            if (edgeOutput.TryGetValue(key, out int index))
            {
                return index;
            }
            float fw = gridField[wet], fd = gridField[dry];
            // Kept off the grid vertices so that no zero-area triangle (hence no hole) can appear.
            float t = Mathf.Clamp(fw / (fw - fd), 0.02f, 0.98f);
            Vector3 direction = Vector3.Lerp(gridDirections[wet], gridDirections[dry], t).normalized;
            // Shoreline: on the displayed terrain, so the water meets it without a gap or a step.
            float height = sampler.Sample(direction).magnitude / radius - 1f + SurfaceLift;
            diagnostics.ShorelineVertices++;
            index = AddOutput(direction, radius, height, 0f, 1f);
            edgeOutput.Add(key, index);
            return index;
        }

        int AddOutput(Vector3 direction, float radius, float height, float depth, float attached)
        {
            vertices.Add(direction * (radius * (1f + height)));
            normals.Add(direction);
            uvs.Add(new Vector2(depth, attached));
            return vertices.Count - 1;
        }

        void EmitTriangle(PlanetSurfaceSampler sampler, float radius, float level, float depthScale, int a, int b, int c)
        {
            bool wa = gridField[a] > 0f, wb = gridField[b] > 0f, wc = gridField[c] > 0f;
            int wet = (wa ? 1 : 0) + (wb ? 1 : 0) + (wc ? 1 : 0);
            if (wet == 0)
            {
                return;
            }
            if (wet == 3)
            {
                AddTriangle(OutputGridVertex(sampler, radius, level, depthScale, a), OutputGridVertex(sampler, radius, level, depthScale, b),
                    OutputGridVertex(sampler, radius, level, depthScale, c));
                return;
            }
            // Rotate so that the odd vertex (alone in its state) comes first, keeping the order.
            int p = a, q = b, r = c;
            bool odd = wet == 1 ? true : false;
            for (int rotation = 0; rotation < 3; rotation++)
            {
                bool wp = gridField[p] > 0f, wq = gridField[q] > 0f, wr = gridField[r] > 0f;
                if (wp == odd && wq != odd && wr != odd)
                {
                    break;
                }
                int t = p;
                p = q;
                q = r;
                r = t;
            }
            if (wet == 1)
            {
                // p wet, q and r dry: one corner triangle.
                int pq = OutputEdgeVertex(sampler, radius, p, q);
                int pr = OutputEdgeVertex(sampler, radius, p, r);
                AddTriangle(OutputGridVertex(sampler, radius, level, depthScale, p), pq, pr);
            }
            else
            {
                // p dry, q and r wet: quadrilateral q, r, rp, pq.
                int qv = OutputGridVertex(sampler, radius, level, depthScale, q);
                int rv = OutputGridVertex(sampler, radius, level, depthScale, r);
                int pq = OutputEdgeVertex(sampler, radius, q, p);
                int rp = OutputEdgeVertex(sampler, radius, r, p);
                AddTriangle(pq, qv, rv);
                AddTriangle(pq, rv, rp);
            }
        }

        /// <summary>Emits a triangle if it is not degenerate, with outward winding (terrain convention).</summary>
        void AddTriangle(int a, int b, int c)
        {
            Vector3 pa = vertices[a], pb = vertices[b], pc = vertices[c];
            Vector3 normal = Vector3.Cross(pb - pa, pc - pa);
            float area = normal.magnitude;
            if (!(area > DegenerateRelativeArea * pa.sqrMagnitude))
            {
                diagnostics.DegenerateTrianglesSkipped++;
                return;
            }
            if (Vector3.Dot(normal, pa + pb + pc) < 0f)
            {
                int t = b;
                b = c;
                c = t;
                diagnostics.WindingCorrections++;
            }
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }
    }

    /// <summary>Measured lake surface diagnostics (see <see cref="LakeMeshBuilder.LastDiagnostics"/>).</summary>
    public struct LakeMeshDiagnostics
    {
        public int Lakes;
        public int Vertices;
        public int Triangles;
        /// <summary>Water vertices of the refined grid (flat, conformed or lip).</summary>
        public int InteriorVertices;
        /// <summary>Interior vertices raised onto a displayed mesh coarser than the terrain.</summary>
        public int ConformedVertices;
        /// <summary>Shoreline vertices (placed on the displayed terrain).</summary>
        public int ShorelineVertices;
        /// <summary>Vertices where the water reaches the edge of the built region (outlet lip).</summary>
        public int LipVertices;
        /// <summary>Largest drop of a lip vertex below the level (fraction of the radius).</summary>
        public float MaxLipDrop;
        /// <summary>Sub-cell pockets below the level but separated from the lake: left dry.</summary>
        public int DisconnectedWetVertices;
        public int DegenerateTrianglesSkipped;
        /// <summary>Triangles whose winding was flipped to face outward (marching cases).</summary>
        public int WindingCorrections;
    }
}
