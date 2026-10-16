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
    /// (V0.1.1D-B.2). Pure representation: never changes the terrain mesh or the lake data.
    /// </summary>
    /// <remarks>
    /// The water is clipped out of the DISPLAYED terrain triangles (the render mesh, read through
    /// <see cref="PlanetSurfaceSampler"/>), so the shoreline is the water-level contour of the
    /// terrain the player sees, at any mesh resolution:
    /// 1. Wet terrain vertices (displayed height below the level) are flood-filled from the lake
    ///    footprint across the render mesh edges, restricted to the lake's hydrological region
    ///    (footprint + basin-rim shore ring, see <see cref="IsShoreCell"/>). Low terrain outside
    ///    that region, or not connected to the footprint, never becomes part of the lake.
    /// 2. Each terrain triangle touching the water is clipped (marching triangles). Wet vertices
    ///    are projected onto the water sphere R (1 + level); shoreline vertices are the exact
    ///    intersection of the terrain edge with that sphere (quadratic solve), so they lie both on
    ///    the displayed terrain and on the water level: no gap, no step, no water relief.
    /// 3. Where wet terrain leaves the hydrological region (outlet saddle, coarse mesh), the
    ///    water is cut at the region boundary (bisection on the cell membership along the edge),
    ///    still at the level, and reported as a basin cut. The water is never lowered onto the
    ///    terrain nor raised over it.
    /// Inside a terrain triangle the water plane stays above the terrain plane (both planes pass
    /// through the same rays; their radial ratio is monotonic along a great circle), so the
    /// water never intersects the displayed terrain. Edges are identified by canonical integer
    /// cube coordinates, so the surface is continuous across cube faces and independent of the
    /// face traversal order. Lakes that the displayed mesh cannot represent (no displayed terrain
    /// vertex below the level) are not drawn and are counted.
    /// </remarks>
    public sealed class LakeMeshBuilder
    {
        /// <summary>Terrain vertices within this height of the level (fraction of the radius) count as dry.</summary>
        public const float LevelTolerance = 1e-6f;
        const float DegenerateRelativeArea = 1e-12f;
        const int CutBisections = 14;
        /// <summary>Shoreline points closer than this fraction of their terrain edge to the DRY end snap
        /// onto it (the terrain there is then above the water by at most 0.1% of the edge's rise).</summary>
        const float SnapFraction = 1e-3f;
        /// <summary>Shoreline points closer than this fraction of their terrain edge to the WET end snap
        /// onto the wet vertex (water stays above the terrain; removes thin slivers along the shore).</summary>
        const float WetSnapFraction = 0.02f;
        /// <summary>A wet-side snap never leaves the shoreline more than this far above the displayed
        /// terrain (fraction of the radius), so shoreline points stay on the terrain.</summary>
        const float SnapHeight = 1e-5f;
        /// <summary>Boundary vertices closer than this fraction of a render edge are welded.</summary>
        const float WeldFraction = 1e-3f;
        /// <summary>Triangles whose altitude is below this fraction of their longest edge are counted as slivers.</summary>
        public const float SliverShape = 0.01f;

        static readonly int[] NeighborX = { 1, -1, 0, 0, 1, -1 };
        static readonly int[] NeighborY = { 0, 0, 1, -1, 1, -1 };

        const byte Unvisited = 0, Water = 1, Rejected = 2, Dropped = 3;

        readonly List<Vector3> vertices = new List<Vector3>();
        readonly List<Vector3> normals = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<int> triangles = new List<int>();
        readonly List<int> lakeVertexStart = new List<int>();
        readonly List<int> lakeTriangleStart = new List<int>();
        readonly List<bool> outputOnSeam = new List<bool>();
        readonly List<int> boundarySegments = new List<int>();
        readonly List<bool> boundaryCut = new List<bool>();
        readonly HashSet<long> waterQuads = new HashSet<long>();
        LakeMeshDiagnostics diagnostics;

        // Inputs of the last build (water queries).
        LakeSet builtLakes;
        PlanetSurfaceSampler builtSampler;
        float builtRadius;

        // Per-lake scratch: terrain vertices by canonical cube key.
        readonly Dictionary<long, int> local = new Dictionary<long, int>();
        readonly List<long> localKey = new List<long>();
        readonly List<Vector3Int> localCube = new List<Vector3Int>();
        readonly List<Vector3> localDirection = new List<Vector3>();
        readonly List<Vector3> localPosition = new List<Vector3>();
        readonly List<float> localHeight = new List<float>();
        readonly List<bool> localWet = new List<bool>();
        readonly List<bool> localAllowed = new List<bool>();
        readonly List<bool> localFootprint = new List<bool>();
        readonly List<byte> localState = new List<byte>();
        readonly List<int> localOutput = new List<int>();
        readonly Dictionary<int, bool> allowedCells = new Dictionary<int, bool>();
        readonly Dictionary<(long, long), int> edgeOutput = new Dictionary<(long, long), int>();
        readonly HashSet<long> lakeQuads = new HashSet<long>();
        readonly List<long> sortedQuads = new List<long>();
        readonly Stack<int> stack = new Stack<int>();
        readonly Dictionary<(int, int, int), int> weldCells = new Dictionary<(int, int, int), int>();
        readonly List<int> weldMap = new List<int>();
        readonly List<int> seeds = new List<int>();
        readonly List<int> cornerSeeds = new List<int>();
        readonly List<int> members = new List<int>();
        readonly List<int> componentStart = new List<int>();
        // Shoreline debug flags of the render vertices touched by the last build (canonical cube key).
        readonly Dictionary<long, ShorelineFlags> vertexFlags = new Dictionary<long, ShorelineFlags>();
        int builtResolution;
        int clipA, clipB, clipC;
        readonly Vector3Int[] faceNormal = new Vector3Int[CubeSphereGenerator.FaceCount];
        readonly Vector3Int[] faceAxisA = new Vector3Int[CubeSphereGenerator.FaceCount];
        readonly Vector3Int[] faceAxisB = new Vector3Int[CubeSphereGenerator.FaceCount];

        // Current lake.
        LakeSet lakes;
        HydrologyGrid grid;
        PlanetSurfaceSampler sampler;
        int lakeId;
        int n;
        float radius, level, waterRadius, depthScale;
        Vector3 terrainA, terrainB, terrainC;

        public LakeMeshBuilder()
        {
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
                faceNormal[face] = Vector3Int.RoundToInt(normal);
                faceAxisA[face] = Vector3Int.RoundToInt(axisA);
                faceAxisB[face] = Vector3Int.RoundToInt(axisB);
            }
        }

        public int LastVertexCount => vertices.Count;
        public float LastMilliseconds { get; private set; }

        public IReadOnlyList<Vector3> Vertices => vertices;
        public IReadOnlyList<Vector3> Normals => normals;
        public IReadOnlyList<int> Triangles => triangles;

        /// <summary>Per vertex: x = relative depth of the displayed terrain under it (0 shore .. 1
        /// deepest), y = 1 on the water boundary (shoreline or basin cut), else 0.</summary>
        public IReadOnlyList<Vector2> WaterAttributes => uvs;

        /// <summary>Vertices of lake i are [GetLakeVertexStart(i), GetLakeVertexStart(i + 1)).</summary>
        public int GetLakeVertexStart(int lake) => lake < lakeVertexStart.Count ? lakeVertexStart[lake] : vertices.Count;

        /// <summary>Triangle indices of lake i are [GetLakeTriangleStart(i), GetLakeTriangleStart(i + 1)) of <see cref="Triangles"/>.</summary>
        public int GetLakeTriangleStart(int lake) => lake < lakeTriangleStart.Count ? lakeTriangleStart[lake] : triangles.Count;

        /// <summary>Boundary segments (shoreline or basin cut) of the water surface.</summary>
        public int BoundarySegmentCount => boundaryCut.Count;

        public void GetBoundarySegment(int segment, out int a, out int b, out bool basinCut)
        {
            a = boundarySegments[2 * segment];
            b = boundarySegments[2 * segment + 1];
            basinCut = boundaryCut[segment];
        }

        public LakeMeshDiagnostics LastDiagnostics => diagnostics;

        /// <summary>Lake set of the last build (water queries refer to it).</summary>
        public LakeSet BuiltLakes => builtLakes;

        public void Build(Mesh mesh, LakeSet lakes, PlanetInstance planet, PlanetSurfaceSampler sampler)
        {
            var stopwatch = Stopwatch.StartNew();
            BuildGeometry(lakes, planet, sampler);
            Upload(mesh, planet.Radius);
            LastMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
        }

        /// <summary>Uploads the geometry of the last build into a mesh.</summary>
        public void Upload(Mesh mesh, float planetRadius)
        {
            mesh.Clear();
            mesh.name = "Planet Lakes";
            mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (planetRadius * 2.4f));
        }

        /// <summary>
        /// Hydrological test (true terrain, not the displayed mesh): a direction lies under the
        /// water of a lake when the terrain is below its level in its footprint or shore ring.
        /// </summary>
        public static bool IsSubmerged(LakeSet lakes, PlanetInstance planet, int lake, Vector3 direction)
        {
            return IsSubmerged(lakes, planet.GetLegacyElevation, lake, direction); // the legacy world's surface (V0.2.0C)
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

        /// <summary>Hydrological region of a lake: its footprint and its shore ring.</summary>
        public static bool IsInLakeRegion(LakeSet lakes, int lake, int cell)
        {
            return lakes.GetLakeOfCell(cell) == lake || IsShoreCell(lakes, lake, cell);
        }

        /// <summary>Computes the geometry without touching any Unity object (testable).</summary>
        public void BuildGeometry(LakeSet lakes, PlanetInstance planet, PlanetSurfaceSampler sampler)
        {
            BuildGeometry(lakes, planet.Radius, sampler);
        }

        /// <summary>
        /// Same; the elevation function is not used any more (the water follows the displayed
        /// mesh), kept for callers of the V0.1.1D-B.1 signature.
        /// </summary>
        public void BuildGeometry(LakeSet lakes, Func<Vector3, float> elevation, float radius, PlanetSurfaceSampler sampler)
        {
            BuildGeometry(lakes, radius, sampler);
        }

        public void BuildGeometry(LakeSet lakes, float planetRadius, PlanetSurfaceSampler surface)
        {
            var stopwatch = Stopwatch.StartNew();
            vertices.Clear();
            normals.Clear();
            uvs.Clear();
            triangles.Clear();
            lakeVertexStart.Clear();
            lakeTriangleStart.Clear();
            outputOnSeam.Clear();
            boundarySegments.Clear();
            boundaryCut.Clear();
            waterQuads.Clear();
            diagnostics = default;
            builtLakes = lakes;
            builtSampler = surface;
            builtRadius = planetRadius;
            builtResolution = surface.Resolution;
            vertexFlags.Clear();

            this.lakes = lakes;
            grid = lakes.Hydrology.Grid;
            sampler = surface;
            radius = planetRadius;
            n = surface.Resolution;
            for (int lake = 0; lake < lakes.Count; lake++)
            {
                lakeVertexStart.Add(vertices.Count);
                lakeTriangleStart.Add(triangles.Count);
                int segmentStart = boundaryCut.Count;
                BuildLake(lake);
                WeldBoundary(lakeVertexStart[lake], lakeTriangleStart[lake], segmentStart);
                if (triangles.Count == lakeTriangleStart[lake])
                {
                    diagnostics.UnrepresentedLakes++;
                }
            }
            CheckEdges();
            diagnostics.Lakes = lakes.Count;
            diagnostics.Vertices = vertices.Count;
            diagnostics.Triangles = triangles.Count / 3;
            this.lakes = null;
            sampler = null;
            LastMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
        }

        void BuildLake(int lake)
        {
            lakeId = lake;
            Lake data = lakes.GetLake(lake);
            level = data.Level;
            waterRadius = radius * (1f + level);
            depthScale = Mathf.Max(data.MaxDepth, 1e-6f);
            local.Clear();
            localKey.Clear();
            localCube.Clear();
            localDirection.Clear();
            localPosition.Clear();
            localHeight.Clear();
            localWet.Clear();
            localAllowed.Clear();
            localFootprint.Clear();
            localState.Clear();
            localOutput.Clear();
            allowedCells.Clear();
            edgeOutput.Clear();
            lakeQuads.Clear();
            sortedQuads.Clear();
            stack.Clear();

            // 1. Seeds: wet displayed vertices inside the footprint cells (window around each cell
            //    center). Only for a lake smaller than a render quad (no such vertex), the wet
            //    in-region corners of the render quad holding a footprint cell center.
            seeds.Clear();
            cornerSeeds.Clear();
            int window = Mathf.CeilToInt(0.75f * n / grid.Resolution) + 1;
            for (int i = 0; i < data.CellCount; i++)
            {
                int cell = lakes.GetFootprintCell(lake, i);
                sampler.Locate(grid.GetCenter(cell), out int face, out int x0, out int y0, out _, out _);
                for (int y = Mathf.Max(0, y0 - window + 1); y <= Mathf.Min(n, y0 + window); y++)
                {
                    for (int x = Mathf.Max(0, x0 - window + 1); x <= Mathf.Min(n, x0 + window); x++)
                    {
                        int v = GetVertex(face, x, y);
                        bool corner = (x == x0 || x == x0 + 1) && (y == y0 || y == y0 + 1);
                        if (localWet[v] && localFootprint[v])
                        {
                            seeds.Add(v);
                        }
                        else if (corner && localWet[v] && localAllowed[v])
                        {
                            cornerSeeds.Add(v);
                        }
                    }
                }
            }
            if (seeds.Count == 0)
            {
                seeds.AddRange(cornerSeeds);
            }

            // 2. Flood across the render mesh edges, wet vertices of the hydrological region only,
            //    one connected component per new seed.
            members.Clear();
            componentStart.Clear();
            foreach (int seed in seeds)
            {
                if (localState[seed] == Water)
                {
                    continue;
                }
                componentStart.Add(members.Count);
                Visit(seed);
                while (stack.Count > 0)
                {
                    int v = stack.Pop();
                    members.Add(v);
                    Vector3Int p = localCube[v];
                    for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
                    {
                        if (!OnFace(p, face, out int x, out int y))
                        {
                            continue;
                        }
                        for (int k = 0; k < NeighborX.Length; k++)
                        {
                            int nx = x + NeighborX[k], ny = y + NeighborY[k];
                            if (nx < 0 || ny < 0 || nx > n || ny > n)
                            {
                                continue;
                            }
                            int other = GetVertex(face, nx, ny);
                            if (localWet[other] && localAllowed[other])
                            {
                                Visit(other);
                            }
                            else if (localWet[other] && localState[other] == Unvisited)
                            {
                                localState[other] = Rejected; // counted once
                                diagnostics.RejectedWetVertices++;
                                Flag(other, ShorelineFlags.Rejected);
                            }
                        }
                    }
                }
            }
            componentStart.Add(members.Count);

            // Disconnected parts of the lake (the displayed terrain rises above the level between
            // them) are kept, except single sampled vertices dipping below the level away from the
            // lake body: a feature smaller than one render quad, drawn as a tiny star of triangles.
            int largest = 0;
            for (int c = 1; c + 1 < componentStart.Count; c++)
            {
                if (componentStart[c + 1] - componentStart[c] > componentStart[largest + 1] - componentStart[largest])
                {
                    largest = c;
                }
            }
            for (int c = 0; c + 1 < componentStart.Count; c++)
            {
                int size = componentStart[c + 1] - componentStart[c];
                bool drop = c != largest && size == 1;
                if (c != largest && !drop)
                {
                    diagnostics.DetachedFragments++;
                }
                for (int m = componentStart[c]; m < componentStart[c + 1]; m++)
                {
                    int v = members[m];
                    if (drop)
                    {
                        localState[v] = Dropped;
                        diagnostics.DroppedPuddles++;
                        Flag(v, ShorelineFlags.Dropped);
                        continue;
                    }
                    Flag(v, c != largest ? ShorelineFlags.Water | ShorelineFlags.Fragment : ShorelineFlags.Water);
                    // Render quads around the vertex.
                    Vector3Int p = localCube[v];
                    for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
                    {
                        if (!OnFace(p, face, out int x, out int y))
                        {
                            continue;
                        }
                        for (int qy = y - 1; qy <= y; qy++)
                        {
                            for (int qx = x - 1; qx <= x; qx++)
                            {
                                if (qx >= 0 && qy >= 0 && qx < n && qy < n && lakeQuads.Add(QuadKey(face, qx, qy)))
                                {
                                    sortedQuads.Add(QuadKey(face, qx, qy));
                                }
                            }
                        }
                    }
                }
            }

            // 3. Clip the render triangles (same split as CubeSphereGenerator.FillFaceTriangles).
            sortedQuads.Sort();
            foreach (long quad in sortedQuads)
            {
                int face = (int)(quad / ((long)n * n));
                int rest = (int)(quad - (long)face * n * n);
                int qy = rest / n, qx = rest - qy * n;
                int v00 = GetVertex(face, qx, qy);
                int v10 = GetVertex(face, qx + 1, qy);
                int v11 = GetVertex(face, qx + 1, qy + 1);
                int v01 = GetVertex(face, qx, qy + 1);
                int before = triangles.Count;
                ClipTriangle(v00, v11, v01);
                ClipTriangle(v00, v10, v11);
                if (triangles.Count > before)
                {
                    waterQuads.Add(((long)lake << 40) | quad);
                }
            }
        }

        /// <summary>
        /// Welds boundary vertices of the current lake lying closer than <see cref="WeldFraction"/> of
        /// a render edge (two shoreline / cut points converging from different terrain edges): the
        /// triangle between them would be a near-degenerate needle. Triangles and boundary segments
        /// that collapse are removed (zero area by construction, no hole).
        /// </summary>
        void WeldBoundary(int vertexStart, int triangleStart, int segmentStart)
        {
            float cell = WeldFraction * radius * Mathf.PI * 0.5f / n;
            weldCells.Clear();
            weldMap.Clear();
            for (int v = vertexStart; v < vertices.Count; v++)
            {
                weldMap.Add(v);
                if (uvs[v].y < 0.5f)
                {
                    continue;
                }
                Vector3 p = vertices[v];
                int cx = Mathf.FloorToInt(p.x / cell), cy = Mathf.FloorToInt(p.y / cell), cz = Mathf.FloorToInt(p.z / cell);
                int target = -1;
                for (int dx = -1; dx <= 1 && target < 0; dx++)
                for (int dy = -1; dy <= 1 && target < 0; dy++)
                for (int dz = -1; dz <= 1 && target < 0; dz++)
                {
                    if (weldCells.TryGetValue((cx + dx, cy + dy, cz + dz), out int other) && (vertices[other] - p).sqrMagnitude < cell * cell)
                    {
                        target = other;
                    }
                }
                if (target >= 0)
                {
                    weldMap[v - vertexStart] = target;
                    diagnostics.WeldedBoundaryVertices++;
                }
                else
                {
                    weldCells[(cx, cy, cz)] = v;
                }
            }
            if (weldMap.Count == 0)
            {
                return;
            }
            int write = triangleStart;
            for (int t = triangleStart; t < triangles.Count; t += 3)
            {
                int a = weldMap[triangles[t] - vertexStart], b = weldMap[triangles[t + 1] - vertexStart], c = weldMap[triangles[t + 2] - vertexStart];
                if (a == b || b == c || a == c)
                {
                    diagnostics.CollapsedTriangles++;
                    continue;
                }
                triangles[write++] = a;
                triangles[write++] = b;
                triangles[write++] = c;
            }
            triangles.RemoveRange(write, triangles.Count - write);
            int segmentWrite = segmentStart;
            for (int s = segmentStart; s < boundaryCut.Count; s++)
            {
                int a = weldMap[boundarySegments[2 * s] - vertexStart], b = weldMap[boundarySegments[2 * s + 1] - vertexStart];
                if (a == b)
                {
                    if (boundaryCut[s]) diagnostics.BasinCutSegments--; else diagnostics.ShorelineSegments--;
                    continue;
                }
                boundarySegments[2 * segmentWrite] = a;
                boundarySegments[2 * segmentWrite + 1] = b;
                boundaryCut[segmentWrite] = boundaryCut[s];
                segmentWrite++;
            }
            boundarySegments.RemoveRange(2 * segmentWrite, boundarySegments.Count - 2 * segmentWrite);
            boundaryCut.RemoveRange(segmentWrite, boundaryCut.Count - segmentWrite);
        }

        void Visit(int v)
        {
            if (localState[v] != Water)
            {
                localState[v] = Water;
                stack.Push(v);
            }
        }

        long QuadKey(int face, int qx, int qy)
        {
            return ((long)face * n + qy) * n + qx;
        }

        /// <summary>Local index of the render vertex (face, x, y); shared by all faces through its cube key.</summary>
        int GetVertex(int face, int x, int y)
        {
            Vector3Int p = faceNormal[face] * n + faceAxisA[face] * (2 * x - n) + faceAxisB[face] * (2 * y - n);
            long span = 2L * n + 1;
            long key = ((p.x + (long)n) * span + (p.y + n)) * span + (p.z + n);
            if (local.TryGetValue(key, out int index))
            {
                return index;
            }
            index = localKey.Count;
            local.Add(key, index);
            Vector3 direction = CubeSphereGenerator.GetSphereDirection(face, x, y, n);
            Vector3 position = sampler.GetVertex(face, x, y);
            float height = position.magnitude / radius - 1f;
            int cell = grid.CellFromDirection(direction);
            localKey.Add(key);
            localCube.Add(p);
            localDirection.Add(direction);
            localPosition.Add(position);
            localHeight.Add(height);
            localWet.Add(height < level - LevelTolerance);
            localAllowed.Add(IsAllowed(cell));
            localFootprint.Add(lakes.GetLakeOfCell(cell) == lakeId);
            localState.Add(Unvisited);
            localOutput.Add(-1);
            return index;
        }

        bool IsAllowed(int cell)
        {
            if (!allowedCells.TryGetValue(cell, out bool allowed))
            {
                allowed = IsInLakeRegion(lakes, lakeId, cell);
                allowedCells.Add(cell, allowed);
            }
            return allowed;
        }

        bool OnFace(Vector3Int p, int face, out int x, out int y)
        {
            Vector3Int normal = faceNormal[face];
            if (p.x * normal.x + p.y * normal.y + p.z * normal.z != n)
            {
                x = y = 0;
                return false;
            }
            Vector3Int a = faceAxisA[face], b = faceAxisB[face];
            x = (p.x * a.x + p.y * a.y + p.z * a.z + n) / 2;
            y = (p.x * b.x + p.y * b.y + p.z * b.z + n) / 2;
            return true;
        }

        bool IsSeam(Vector3Int p)
        {
            int extremes = (Mathf.Abs(p.x) == n ? 1 : 0) + (Mathf.Abs(p.y) == n ? 1 : 0) + (Mathf.Abs(p.z) == n ? 1 : 0);
            return extremes >= 2;
        }

        void ClipTriangle(int a, int b, int c)
        {
            bool wa = localState[a] == Water, wb = localState[b] == Water, wc = localState[c] == Water;
            int wet = (wa ? 1 : 0) + (wb ? 1 : 0) + (wc ? 1 : 0);
            if (wet == 0)
            {
                return;
            }
            terrainA = localPosition[a];
            terrainB = localPosition[b];
            terrainC = localPosition[c];
            clipA = a;
            clipB = b;
            clipC = c;
            if (wet == 3)
            {
                AddTriangle(WaterVertex(a), WaterVertex(b), WaterVertex(c));
                return;
            }
            // Rotate so that the odd vertex (alone in its state) comes first, keeping the order.
            bool odd = wet == 1;
            int p = a, q = b, r = c;
            for (int rotation = 0; rotation < 3; rotation++)
            {
                if ((localState[p] == Water) == odd && (localState[q] == Water) != odd)
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
                // p under water, q and r not: one corner triangle.
                int pq = BoundaryVertex(p, q);
                int pr = BoundaryVertex(p, r);
                AddTriangle(WaterVertex(p), pq, pr);
                // The boundary exists even when snapping collapsed the triangle onto a terrain edge.
                AddBoundarySegment(pq, pr, q, r);
            }
            else
            {
                // p not under water, q and r under water: quadrilateral q, r, rp, pq.
                int qv = WaterVertex(q);
                int rv = WaterVertex(r);
                int pq = BoundaryVertex(q, p);
                int rp = BoundaryVertex(r, p);
                // Same convex quadrilateral either way: take the diagonal giving the fatter triangles
                // (no slivers when both shoreline points lie close to the dry vertex).
                if (Mathf.Min(Shape(qv, rv, rp), Shape(qv, rp, pq)) > Mathf.Min(Shape(pq, qv, rv), Shape(pq, rv, rp)))
                {
                    AddTriangle(qv, rv, rp);
                    AddTriangle(qv, rp, pq);
                }
                else
                {
                    AddTriangle(pq, qv, rv);
                    AddTriangle(pq, rv, rp);
                }
                AddBoundarySegment(rp, pq, p, p);
            }
        }

        /// <summary>Triangle shape: twice its area over its longest edge squared (altitude / longest
        /// edge; scale-free). Collapsed triangles (shared index) vanish and never limit a choice.</summary>
        float Shape(int a, int b, int c)
        {
            if (a == b || b == c || a == c)
            {
                return float.MaxValue;
            }
            return ShapeOf(vertices[a], vertices[b], vertices[c]);
        }

        /// <summary>Altitude over longest edge of a triangle (0 for a degenerate one).</summary>
        public static float ShapeOf(Vector3 a, Vector3 b, Vector3 c)
        {
            float longest = Mathf.Max((b - a).sqrMagnitude, Mathf.Max((c - b).sqrMagnitude, (a - c).sqrMagnitude));
            return longest > 0f ? Vector3.Cross(b - a, c - a).magnitude / longest : 0f;
        }

        void AddBoundarySegment(int a, int b, int outsideA, int outsideB)
        {
            if (a == b)
            {
                return;
            }
            bool cut = localWet[outsideA] || localWet[outsideB];
            boundarySegments.Add(a);
            boundarySegments.Add(b);
            boundaryCut.Add(cut);
            float length = (vertices[a] - vertices[b]).magnitude;
            if (cut)
            {
                diagnostics.BasinCutSegments++;
            }
            else
            {
                diagnostics.ShorelineSegments++;
                diagnostics.ShorelineLength += length;
            }
        }

        void Flag(int v, ShorelineFlags flags)
        {
            vertexFlags.TryGetValue(localKey[v], out ShorelineFlags current);
            vertexFlags[localKey[v]] = current | flags;
        }

        void FlagClipTriangle()
        {
            Flag(clipA, ShorelineFlags.Problem);
            Flag(clipB, ShorelineFlags.Problem);
            Flag(clipC, ShorelineFlags.Problem);
        }

        /// <summary>Shoreline debug flags of the render vertex (face, x, y) in the last build (None if untouched).</summary>
        public ShorelineFlags GetVertexFlags(int face, int x, int y)
        {
            int size = builtResolution;
            if (size <= 0)
            {
                return ShorelineFlags.None;
            }
            Vector3Int p = faceNormal[face] * size + faceAxisA[face] * (2 * x - size) + faceAxisB[face] * (2 * y - size);
            long span = 2L * size + 1;
            long key = ((p.x + (long)size) * span + (p.y + size)) * span + (p.z + size);
            return vertexFlags.TryGetValue(key, out ShorelineFlags flags) ? flags : ShorelineFlags.None;
        }

        /// <summary>
        /// Replaces the transition markers of the shoreline debug flags with those of a ribbon build
        /// on this surface (null clears them). Each marker flags the render vertices around it.
        /// </summary>
        public void MarkTransitions(RiverRibbonBuilder ribbons)
        {
            var keys = new List<long>(vertexFlags.Keys);
            foreach (long key in keys)
            {
                vertexFlags[key] &= ~ShorelineFlags.Markers;
            }
            if (ribbons == null || builtSampler == null || ribbons.Water != this)
            {
                return;
            }
            int size = builtResolution;
            for (int m = 0; m < ribbons.MarkerCount; m++)
            {
                ribbons.GetMarker(m, out Vector3 direction, out TransitionMarker kind);
                ShorelineFlags flag = kind == TransitionMarker.Inlet ? ShorelineFlags.Inlet
                    : kind == TransitionMarker.Outlet ? ShorelineFlags.Outlet
                    : kind == TransitionMarker.Unresolved ? ShorelineFlags.Unresolved : ShorelineFlags.Exposed;
                builtSampler.Locate(direction, out int face, out int x0, out int y0, out float tx, out float ty);
                int cx = x0 + (tx > 0.5f ? 1 : 0), cy = y0 + (ty > 0.5f ? 1 : 0);
                for (int y = Mathf.Max(0, cy - 1); y <= Mathf.Min(size, cy + 1); y++)
                {
                    for (int x = Mathf.Max(0, cx - 1); x <= Mathf.Min(size, cx + 1); x++)
                    {
                        Vector3Int p = faceNormal[face] * size + faceAxisA[face] * (2 * x - size) + faceAxisB[face] * (2 * y - size);
                        long span = 2L * size + 1;
                        long key = ((p.x + (long)size) * span + (p.y + size)) * span + (p.z + size);
                        vertexFlags.TryGetValue(key, out ShorelineFlags current);
                        vertexFlags[key] = current | flag;
                    }
                }
            }
        }

        /// <summary>Render mesh resolution of the last build.</summary>
        public int BuiltResolution => builtResolution;

        /// <summary>Wet terrain vertex projected onto the water sphere.</summary>
        int WaterVertex(int v)
        {
            if (localOutput[v] >= 0)
            {
                return localOutput[v];
            }
            float depth = Mathf.Clamp01((level - localHeight[v]) / depthScale);
            localOutput[v] = AddOutput(localDirection[v] * waterRadius, localDirection[v], depth, 0f, IsSeam(localCube[v]));
            diagnostics.InteriorVertices++;
            return localOutput[v];
        }

        /// <summary>
        /// Water boundary on the terrain edge (wet, other): the exact shoreline when the other end
        /// is dry, else the hydrological region boundary (basin cut). Shared by the triangles of
        /// both faces through the canonical edge key.
        /// </summary>
        int BoundaryVertex(int wet, int other)
        {
            long ka = localKey[wet], kb = localKey[other];
            var key = ka < kb ? (ka, kb) : (kb, ka);
            if (edgeOutput.TryGetValue(key, out int index))
            {
                return index;
            }
            bool seam = IsSeam(localCube[wet]) && IsSeam(localCube[other]);
            if (!localWet[other])
            {
                Flag(wet, ShorelineFlags.Shore);
                Flag(other, ShorelineFlags.Shore);
                Vector3 direction = ShorelineDirection(localPosition[wet], localPosition[other], waterRadius, out float t);
                if (t < WetSnapFraction && level - localHeight[wet] < SnapHeight)
                {
                    // The terrain vertex is (almost) at the level: it is the shoreline point itself.
                    // Sharing it avoids float-noise slivers; the edge decision is per edge, so both
                    // triangles of the edge agree.
                    index = WaterVertex(wet);
                    uvs[index] = new Vector2(uvs[index].x, 1f);
                    diagnostics.SnappedShorelineVertices++;
                }
                else if (t > 1f - SnapFraction)
                {
                    index = DryShoreVertex(other);
                    diagnostics.SnappedShorelineVertices++;
                }
                else
                {
                    index = AddOutput(direction * waterRadius, direction, 0f, 1f, seam);
                    diagnostics.ShorelineVertices++;
                }
            }
            else
            {
                // Wet terrain outside the lake region: cut at the level, on the edge part inside the
                // region, where the displayed terrain is highest (smallest step below the water edge):
                // the region boundary, or the wet vertex itself when the terrain descends towards it.
                Flag(wet, ShorelineFlags.BasinCut);
                Flag(other, ShorelineFlags.BasinCut);
                Vector3 from = localDirection[wet], to = localDirection[other];
                float lo = 0f, hi = 1f;
                for (int i = 0; i < CutBisections; i++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (IsAllowed(grid.CellFromDirection(Vector3.Lerp(from, to, mid).normalized)))
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }
                float boundary = 0.5f * (lo + hi);
                float boundaryHeight = Mathf.Lerp(localHeight[wet], localHeight[other], boundary);
                float drop;
                if (localHeight[wet] >= boundaryHeight || boundary < WetSnapFraction)
                {
                    index = WaterVertex(wet);
                    uvs[index] = new Vector2(uvs[index].x, 1f);
                    drop = level - localHeight[wet];
                    diagnostics.BasinCutsAtVertex++;
                }
                else
                {
                    Vector3 direction = Vector3.Lerp(from, to, boundary).normalized;
                    index = AddOutput(direction * waterRadius, direction, 0f, 1f, seam);
                    drop = level - (sampler.Sample(direction).magnitude / radius - 1f);
                }
                diagnostics.BasinCutVertices++;
                diagnostics.MaxBasinCutDrop = Mathf.Max(diagnostics.MaxBasinCutDrop, drop);
            }
            edgeOutput.Add(key, index);
            return index;
        }

        /// <summary>
        /// Unit direction of the point where the terrain edge from <paramref name="wet"/> (inside the
        /// sphere) to <paramref name="dry"/> crosses the sphere of radius <paramref name="sphereRadius"/>
        /// (object space positions). Exact: the point lies on the edge and on the sphere.
        /// </summary>
        public static Vector3 ShorelineDirection(Vector3 wet, Vector3 dry, float sphereRadius)
        {
            return ShorelineDirection(wet, dry, sphereRadius, out _);
        }

        /// <summary>Same, also returning the edge parameter t (0 at <paramref name="wet"/>, 1 at <paramref name="dry"/>).</summary>
        public static Vector3 ShorelineDirection(Vector3 wet, Vector3 dry, float sphereRadius, out float edgeParameter)
        {
            double dx = dry.x - wet.x, dy = dry.y - wet.y, dz = dry.z - wet.z;
            double a = dx * dx + dy * dy + dz * dz;
            double b = wet.x * dx + wet.y * dy + wet.z * dz;
            double c = (double)wet.x * wet.x + (double)wet.y * wet.y + (double)wet.z * wet.z - (double)sphereRadius * sphereRadius;
            double t = a > 0.0 ? (-b + Math.Sqrt(Math.Max(0.0, b * b - a * c))) / a : 0.0;
            t = Math.Max(0.0, Math.Min(1.0, t));
            edgeParameter = (float)t;
            var point = new Vector3((float)(wet.x + t * dx), (float)(wet.y + t * dy), (float)(wet.z + t * dz));
            return point.normalized;
        }

        /// <summary>Shoreline point snapped onto a dry terrain vertex lying (almost) at the level.</summary>
        int DryShoreVertex(int dry)
        {
            if (localOutput[dry] < 0)
            {
                localOutput[dry] = AddOutput(localDirection[dry] * waterRadius, localDirection[dry], 0f, 1f, IsSeam(localCube[dry]));
            }
            return localOutput[dry];
        }

        int AddOutput(Vector3 position, Vector3 direction, float depth, float boundary, bool seam)
        {
            vertices.Add(position);
            normals.Add(direction);
            uvs.Add(new Vector2(depth, boundary));
            outputOnSeam.Add(seam);
            float deviation = Mathf.Abs(position.magnitude - waterRadius) / radius;
            diagnostics.MaxLevelDeviation = Mathf.Max(diagnostics.MaxLevelDeviation, deviation);
            return vertices.Count - 1;
        }

        /// <summary>Emits a triangle if it is not degenerate, with outward winding (terrain convention).</summary>
        bool AddTriangle(int a, int b, int c)
        {
            Vector3 pa = vertices[a], pb = vertices[b], pc = vertices[c];
            Vector3 normal = Vector3.Cross(pb - pa, pc - pa);
            float area = normal.magnitude;
            if (a == b || b == c || a == c)
            {
                // Collapsed by a snap onto a terrain vertex: zero area by construction, nothing lost.
                diagnostics.CollapsedTriangles++;
                return false;
            }
            if (!(area > DegenerateRelativeArea * pa.sqrMagnitude))
            {
                diagnostics.DegenerateTrianglesSkipped++;
                FlagClipTriangle();
                return false;
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

            // Water plane vs terrain plane along the centroid ray (measured, never corrected).
            Vector3 centroid = (pa + pb + pc) / 3f;
            Vector3 ray = centroid.normalized;
            Vector3 terrainNormal = Vector3.Cross(terrainB - terrainA, terrainC - terrainA);
            float denominator = Vector3.Dot(ray, terrainNormal);
            if (Mathf.Abs(denominator) > 1e-12f)
            {
                float terrainRadius = Vector3.Dot(terrainA, terrainNormal) / denominator;
                if (centroid.magnitude < terrainRadius - 1e-5f * radius)
                {
                    diagnostics.TerrainIntersectionWarnings++;
                    FlagClipTriangle();
                }
            }
            return true;
        }

        /// <summary>Edge use over the whole surface: cracks (open edges inside the water), seams, manifoldness.</summary>
        void CheckEdges()
        {
            var uses = new Dictionary<long, int>(triangles.Count);
            for (int t = 0; t < triangles.Count; t += 3)
            {
                if (ShapeOf(vertices[triangles[t]], vertices[triangles[t + 1]], vertices[triangles[t + 2]]) < SliverShape)
                {
                    diagnostics.Slivers++;
                }
                for (int e = 0; e < 3; e++)
                {
                    int i = triangles[t + e], j = triangles[t + (e + 1) % 3];
                    long key = ((long)Mathf.Min(i, j) << 32) | (uint)Mathf.Max(i, j);
                    uses.TryGetValue(key, out int count);
                    uses[key] = count + 1;
                }
            }
            foreach (KeyValuePair<long, int> edge in uses)
            {
                int i = (int)(edge.Key >> 32), j = (int)(edge.Key & 0xffffffff);
                bool seam = outputOnSeam[i] && outputOnSeam[j];
                if (edge.Value > 2)
                {
                    diagnostics.NonManifoldEdges++;
                }
                else if (edge.Value == 1 && (uvs[i].y < 0.5f || uvs[j].y < 0.5f))
                {
                    diagnostics.CrackEdges++;
                    diagnostics.SeamMismatches += seam ? 1 : 0;
                }
                else if (edge.Value == 2 && seam)
                {
                    diagnostics.SeamEdges++;
                }
            }
        }

        // ------------------------------------------------------------ Water queries (rendered water)

        /// <summary>Water surface radius of a lake of the last build (object space).</summary>
        public float GetWaterRadius(int lake) => builtRadius * (1f + builtLakes.GetLake(lake).Level);

        /// <summary>
        /// True when a direction lies under the RENDERED water of a lake (last build): in a render
        /// quad holding water of that lake, displayed terrain below the level, inside the lake's
        /// hydrological region. Consistent with the clipped surface up to the planar chord sag.
        /// </summary>
        public bool IsUnderWater(int lake, Vector3 direction)
        {
            if (builtLakes == null || lake < 0 || lake >= builtLakes.Count)
            {
                return false;
            }
            int resolution = builtSampler.Resolution;
            builtSampler.Locate(direction, out int face, out int x0, out int y0, out _, out _);
            long quad = ((long)face * resolution + y0) * resolution + x0;
            if (!waterQuads.Contains(((long)lake << 40) | quad))
            {
                return false;
            }
            float height = builtSampler.Sample(direction).magnitude / builtRadius - 1f;
            if (height >= builtLakes.GetLake(lake).Level - LevelTolerance)
            {
                return false;
            }
            return IsInLakeRegion(builtLakes, lake, builtLakes.Hydrology.Grid.CellFromDirection(direction));
        }

        /// <summary>Lake whose rendered water covers a direction (lowest id), or -1.</summary>
        public int GetLakeUnderWater(Vector3 direction)
        {
            if (builtLakes == null || builtLakes.Count == 0)
            {
                return -1;
            }
            HydrologyGrid hydrologyGrid = builtLakes.Hydrology.Grid;
            int cell = hydrologyGrid.CellFromDirection(direction);
            int best = -1;
            int own = builtLakes.GetLakeOfCell(cell);
            if (own >= 0 && IsUnderWater(own, direction))
            {
                best = own;
            }
            for (int k = hydrologyGrid.GetNeighborStart(cell); k < hydrologyGrid.GetNeighborEnd(cell); k++)
            {
                int other = builtLakes.GetLakeOfCell(hydrologyGrid.GetNeighbor(k));
                if (other >= 0 && (best < 0 || other < best) && other != own && IsUnderWater(other, direction))
                {
                    best = other;
                }
            }
            return best;
        }
    }

    /// <summary>Per render vertex shoreline debug flags (see <see cref="LakeMeshBuilder.GetVertexFlags"/>).</summary>
    [Flags]
    public enum ShorelineFlags : ushort
    {
        None = 0,
        /// <summary>Displayed vertex under the water of a lake.</summary>
        Water = 1,
        /// <summary>End of a terrain edge crossed by the shoreline.</summary>
        Shore = 2,
        /// <summary>End of a terrain edge where the water stops at the hydrological region boundary.</summary>
        BasinCut = 4,
        /// <summary>Displayed vertex below the level next to the water but outside the lake region (left dry).</summary>
        Rejected = 8,
        /// <summary>Water in a part of the lake disconnected from its main body (kept).</summary>
        Fragment = 16,
        /// <summary>Single displayed vertex below the level away from the lake body (not drawn).</summary>
        Dropped = 32,
        /// <summary>Vertex of a terrain triangle whose clipped water was rejected or flagged (degenerate, intersection).</summary>
        Problem = 64,
        /// <summary>Near a river inlet crossing of the rendered shoreline.</summary>
        Inlet = 128,
        /// <summary>Near a river outlet crossing of the rendered shoreline.</summary>
        Outlet = 256,
        /// <summary>Near a lake transition whose rendered shoreline was not found.</summary>
        Unresolved = 512,
        /// <summary>Near an under-water ribbon end whose edges had to be pulled into the water.</summary>
        Exposed = 1024,
        Markers = Inlet | Outlet | Unresolved | Exposed,
    }

    /// <summary>Measured lake surface diagnostics (see <see cref="LakeMeshBuilder.LastDiagnostics"/>).</summary>
    public struct LakeMeshDiagnostics
    {
        public int Lakes;
        public int Vertices;
        public int Triangles;
        /// <summary>Wet displayed terrain vertices projected onto the water level.</summary>
        public int InteriorVertices;
        /// <summary>Exact terrain-edge / water-level intersections.</summary>
        public int ShorelineVertices;
        /// <summary>Shoreline points snapped onto a terrain vertex lying within 0.1% of its edge from the level.</summary>
        public int SnappedShorelineVertices;
        public int ShorelineSegments;
        /// <summary>Total rendered shoreline length (object units).</summary>
        public float ShorelineLength;
        /// <summary>Boundary vertices / segments where wet terrain leaves the lake's hydrological
        /// region (outlet saddle, coarse mesh): the water ends there at its level.</summary>
        public int BasinCutVertices, BasinCutSegments;
        /// <summary>Largest height of the water above the displayed terrain at a basin cut (fraction of the radius).</summary>
        public float MaxBasinCutDrop;
        /// <summary>Wet displayed vertices next to the water but outside the lake region (left dry).</summary>
        public int RejectedWetVertices;
        /// <summary>Lakes without any displayed terrain vertex below their level (not drawn).</summary>
        public int UnrepresentedLakes;
        /// <summary>Kept water parts disconnected from their lake's main body (displayed ridge above the level).</summary>
        public int DetachedFragments;
        /// <summary>Single displayed vertices below the level away from the lake body, not drawn.</summary>
        public int DroppedPuddles;
        /// <summary>Basin cuts placed on the wet vertex itself (terrain descending towards the region boundary).</summary>
        public int BasinCutsAtVertex;
        /// <summary>Triangles collapsed by a snap onto a terrain vertex (zero area by construction, not a hole).</summary>
        public int CollapsedTriangles;
        /// <summary>Emitted triangles thinner than <see cref="LakeMeshBuilder.SliverShape"/> (measured).</summary>
        public int Slivers;
        /// <summary>Boundary vertices welded onto a coincident one (needle triangles removed).</summary>
        public int WeldedBoundaryVertices;
        public int DegenerateTrianglesSkipped;
        /// <summary>Triangles whose winding was flipped to face outward (marching cases).</summary>
        public int WindingCorrections;
        /// <summary>Water triangles below the displayed terrain plane at their centroid (expected 0).</summary>
        public int TerrainIntersectionWarnings;
        /// <summary>Largest |vertex radius - water radius| (fraction of the radius).</summary>
        public float MaxLevelDeviation;
        /// <summary>Open edges inside the water (expected 0), and those on a cube-face seam.</summary>
        public int CrackEdges, SeamMismatches;
        /// <summary>Interior water edges lying on a cube-face seam (shared by both faces).</summary>
        public int SeamEdges;
        public int NonManifoldEdges;
    }
}
