using System.Collections.Generic;
using System.Diagnostics;
using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;
using UnityEngine.Rendering;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Builds one combined ribbon mesh for the displayed rivers of a <see cref="RiverNetwork"/>.
    /// Pure representation: it reads network trajectories and never changes them.
    /// </summary>
    /// <remarks>
    /// Both ribbon edges are placed on the DISPLAYED terrain (<see cref="PlanetSurfaceSampler"/>),
    /// so rivers follow the relief exactly; the shader adds a small, distance-scaled lift and a
    /// minimum on-screen width. Each vertex stores its side direction and sign (UV0) and its
    /// world half width (UV1.x) for that purpose. Rivers stop one sample past the coastline so
    /// they visibly enter the (semi-transparent) ocean.
    /// With a <see cref="LakeRiverLayer"/> (V0.1.1D-B.1) the visible REACHES are drawn instead of
    /// whole rivers: they end a couple of samples under the water of the lake they enter and start
    /// just under the water of the open lake they leave (topology from the layer, exact shoreline
    /// from the true terrain), follow closed-basin connectors, and use the lake-aware drained area
    /// for their width. Samples under lake water are flagged (UV1.y = 1) so the shader does not
    /// lift them above the water surface.
    /// </remarks>
    public sealed class RiverRibbonBuilder
    {
        readonly List<Vector3> vertices = new List<Vector3>();
        readonly List<Vector3> normals = new List<Vector3>();
        readonly List<Vector4> sides = new List<Vector4>();
        readonly List<Vector2> widths = new List<Vector2>();
        readonly List<int> triangles = new List<int>();
        readonly List<int> ribbonRivers = new List<int>();
        readonly List<int> ribbonStarts = new List<int>();
        readonly List<Sample> samples = new List<Sample>();
        readonly List<int> sampleStarts = new List<int>();
        long[] sortedKeys = new long[0];
        int[] sortedSamples = new int[0];
        readonly Dictionary<long, long> cellRanges = new Dictionary<long, long>();
        readonly HashSet<long> overlapPairs = new HashSet<long>();
        readonly HashSet<int> unresolvedMouths = new HashSet<int>();
        readonly List<int> ribbonReaches = new List<int>();
        readonly List<Vector3> rawPolyline = new List<Vector3>();
        RiverGeometryDiagnostics diagnostics;

        // Samples kept under the water at a lake inflow / outflow, so the ribbon visibly enters it.
        const int SubmergedEndSamples = 2;
        // Lake inflows whose shoreline is not met by the entry cell are searched up to this many nodes further.
        const int LakeSearchNodes = 4;
        // Distance from the source used for reaches that do not start at a source (no taper).
        const float NoTaperDistance = 1e3f;

        // Ribbon end depth under the rendered ocean surface (fraction of the radius).
        const float SubmergeDepth = 0.0004f;
        // Maximum mouth extension beyond the hydrology ocean cell, in hydrology cells.
        const float MouthExtensionCells = 4f;
        // Unconnected ribbons keep this fraction of their centerline distance as total width.
        const float OverlapClearance = 0.9f;
        // Rivers joining at a junction may overlap within this many cells of it.
        const float JunctionZoneCells = 1.5f;
        // Triangles smaller than this fraction of r^2 are degenerate (collapsed fold vertices).
        const float DegenerateRelativeArea = 1e-10f;

        struct Sample
        {
            public Vector3 Direction;
            public int River;
            /// <summary>Angular half width (unit sphere).</summary>
            public float HalfWidth;
            /// <summary>Under lake water: not lifted by the shader.</summary>
            public bool Submerged;
        }
        readonly List<Vector3> points = new List<Vector3>();
        readonly List<float> pointAccumulation = new List<float>();
        readonly List<float> pointDistance = new List<float>();
        readonly List<float> pointParam = new List<float>();
        readonly List<Vector3> reachPoints = new List<Vector3>();
        readonly List<float> reachAccumulation = new List<float>();
        readonly List<float> reachDistance = new List<float>();
        readonly List<bool> reachSubmerged = new List<bool>();
        bool[] hasTributary = new bool[0];

        public int LastDisplayedRiverCount { get; private set; }
        public int LastVertexCount => vertices.Count;
        public float LastMilliseconds { get; private set; }

        /// <summary>
        /// Display filter: tiny coastal streams (no tributary, reaching the sea directly, shorter
        /// than <see cref="RiverSettings.minDisplayLength"/>) are not drawn.
        /// </summary>
        public static bool IsDisplayed(RiverNetwork network, int river, bool riverHasTributary, RiverSettings settings)
        {
            River data = network.GetRiver(river);
            return data.ParentRiver >= 0 || riverHasTributary || data.Length >= settings.minDisplayLength;
        }

        public static bool[] ComputeHasTributary(RiverNetwork network, bool[] buffer)
        {
            if (buffer == null || buffer.Length < network.RiverCount)
            {
                buffer = new bool[network.RiverCount];
            }
            System.Array.Clear(buffer, 0, buffer.Length);
            for (int river = 0; river < network.RiverCount; river++)
            {
                int parent = network.GetRiver(river).ParentRiver;
                if (parent >= 0)
                {
                    buffer[parent] = true;
                }
            }
            return buffer;
        }

        public void Build(Mesh mesh, RiverNetwork network, PlanetSurfaceSampler sampler, PlanetInstance planet, RiverSettings settings,
            LakeRiverLayer lakes = null)
        {
            var stopwatch = Stopwatch.StartNew();
            BuildGeometry(network, sampler, planet, settings, lakes);
            Upload(mesh, planet.Radius);
            LastMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
        }

        /// <summary>Ribbon vertex positions (object space) of the last build, two per path sample.</summary>
        public IReadOnlyList<Vector3> Vertices => vertices;

        public IReadOnlyList<int> Triangles => triangles;

        /// <summary>Per vertex: the unit direction of its ribbon sample (centerline), also its normal.</summary>
        public IReadOnlyList<Vector3> SampleDirections => normals;

        /// <summary>True for vertices of samples under lake water (drawn without lift, under the surface).</summary>
        public bool IsVertexSubmerged(int vertex) => widths[vertex].y > 0.5f;

        /// <summary>Number of ribbons (displayed rivers) in the last build.</summary>
        public int RibbonCount => ribbonRivers.Count;

        public int GetRibbonRiver(int ribbon) => ribbonRivers[ribbon];

        /// <summary>Reach of the lake layer drawn by a ribbon, or -1 when built without lakes.</summary>
        public int GetRibbonReach(int ribbon) => ribbonReaches[ribbon];

        /// <summary>Vertices of ribbon i are [GetRibbonStart(i), GetRibbonStart(i + 1)), left/right pairs.</summary>
        public int GetRibbonStart(int ribbon) => ribbon < ribbonStarts.Count ? ribbonStarts[ribbon] : vertices.Count;

        /// <summary>Measured geometry diagnostics of the last build (no hardcoded values).</summary>
        public RiverGeometryDiagnostics LastDiagnostics => diagnostics;

        /// <summary>Ocean cells of mouths that could not be visually connected (diagnostics view).</summary>
        public bool IsUnresolvedMouthCell(int cell) => unresolvedMouths.Contains(cell);

        /// <summary>Computes the ribbon geometry without touching any Unity object (testable).</summary>
        public void BuildGeometry(RiverNetwork network, PlanetSurfaceSampler sampler, PlanetInstance planet, RiverSettings settings,
            LakeRiverLayer lakes = null)
        {
            if (lakes != null && lakes.Network != network)
            {
                throw new System.ArgumentException("The lake layer was built for another river network.");
            }
            vertices.Clear();
            ribbonRivers.Clear();
            ribbonReaches.Clear();
            ribbonStarts.Clear();
            normals.Clear();
            sides.Clear();
            widths.Clear();
            triangles.Clear();
            samples.Clear();
            sampleStarts.Clear();
            unresolvedMouths.Clear();
            diagnostics = default;

            HydrologyResult hydrology = network.Hydrology;
            HydrologyGrid grid = hydrology.Grid;
            float radius = planet.Radius;
            float oceanRadius = planet.OceanRadius;
            // Fine enough to follow both the hydrology cells and the render mesh triangles.
            float cellSpacing = Mathf.PI * 0.5f / grid.Resolution;
            float meshSpacing = Mathf.PI * 0.5f / Mathf.Max(1, sampler.Resolution);
            float maxStep = 0.5f * Mathf.Min(cellSpacing, meshSpacing);

            // Pass 1: resampled centerlines, mouth handling, nominal widths.
            hasTributary = ComputeHasTributary(network, hasTributary);
            for (int river = 0; river < network.RiverCount; river++)
            {
                if (!IsDisplayed(network, river, hasTributary[river], settings))
                {
                    continue;
                }
                River data = network.GetRiver(river);
                int finalSegmentStart = Resample(network, river, maxStep);
                if (lakes != null)
                {
                    AddReaches(network, lakes, river, sampler, planet, settings, oceanRadius, radius, finalSegmentStart, maxStep,
                        cellSpacing, meshSpacing);
                    continue;
                }
                if (data.MouthCell >= 0)
                {
                    diagnostics.OceanMouths++;
                    ResolveMouth(network, data, sampler, oceanRadius, radius, finalSegmentStart, maxStep, cellSpacing, meshSpacing);
                }
                if (points.Count < 2)
                {
                    continue;
                }
                sampleStarts.Add(samples.Count);
                ribbonRivers.Add(river);
                ribbonReaches.Add(-1);
                for (int i = 0; i < points.Count; i++)
                {
                    samples.Add(new Sample
                    {
                        Direction = points[i],
                        River = river,
                        HalfWidth = 0.5f * settings.GetDisplayWidth(pointAccumulation[i], pointDistance[i]),
                    });
                }
            }
            sampleStarts.Add(samples.Count);
            LastDisplayedRiverCount = ribbonRivers.Count;

            // Pass 2: unrelated rivers whose ribbons would overlap get locally narrower ribbons.
            ClampWidthOverlaps(network, grid, cellSpacing);

            // Pass 3: ribbon geometry on the displayed terrain, without folds.
            for (int ribbon = 0; ribbon < ribbonRivers.Count; ribbon++)
            {
                ribbonStarts.Add(vertices.Count);
                AppendRibbon(sampler, radius, sampleStarts[ribbon], sampleStarts[ribbon + 1]);
            }
            CheckJunctions(network, sampler, radius, cellSpacing, lakes);
        }

        /// <summary>
        /// Lake-aware ribbons of one river: one ribbon per visible reach of the layer. Reach ends
        /// come from the topology (which lake is entered / left); the precise cut is where the
        /// path goes under that lake's water (true terrain below its level).
        /// </summary>
        void AddReaches(RiverNetwork network, LakeRiverLayer layer, int river, PlanetSurfaceSampler sampler, PlanetInstance planet,
            RiverSettings settings, float oceanRadius, float radius, int finalSegmentStart, float maxStep, float cellSpacing, float meshSpacing)
        {
            int first = layer.GetRiverReachStart(river), end = layer.GetRiverReachStart(river + 1);
            if (first == end)
            {
                return;
            }
            River data = network.GetRiver(river);
            RiverReach lastReach = layer.GetReach(end - 1);
            if (data.MouthCell >= 0 && lastReach.End == ReachEnd.RiverEnd && lastReach.LastNode == data.NodeCount - 1)
            {
                diagnostics.OceanMouths++;
                ResolveMouth(network, data, sampler, oceanRadius, radius, finalSegmentStart, maxStep, cellSpacing, meshSpacing);
            }
            LakeSet lakes = layer.Lakes;
            double totalArea = network.Hydrology.Grid.TotalArea;
            for (int r = first; r < end; r++)
            {
                RiverReach reach = layer.GetReach(r);
                reachPoints.Clear();
                reachAccumulation.Clear();
                reachDistance.Clear();
                reachSubmerged.Clear();

                // Start.
                int startIndex = FirstPointAtOrAfter(reach.FirstNode);
                if (reach.Start == ReachStart.Source)
                {
                    startIndex = 0;
                }
                int emerged = -1;
                if (reach.Start == ReachStart.LakeOutlet)
                {
                    // Start just under the water, where the path leaves the lake (searched from the
                    // last lake node to a little past the first dry one: sub-cell shorelines).
                    diagnostics.LakeOutflows++;
                    int windowStart = FirstPointAtOrAfter(reach.FirstNode - 1);
                    for (int i = windowStart; i < points.Count && pointParam[i] <= reach.FirstNode + LakeSearchNodes; i++)
                    {
                        if (!LakeMeshBuilder.IsSubmerged(lakes, planet, reach.StartLake, points[i]))
                        {
                            emerged = i;
                            break;
                        }
                    }
                    if (emerged < 0)
                    {
                        diagnostics.LakeOutflowGaps++;
                    }
                    else
                    {
                        startIndex = Mathf.Max(windowStart, emerged - SubmergedEndSamples);
                    }
                }

                // End.
                int endIndex = points.Count - 1;
                if (reach.End == ReachEnd.LakeInflow)
                {
                    diagnostics.LakeInflows++;
                    endIndex = LastPointAtOrBefore(reach.LastNode + 1);
                    float searchLimit = reach.ConnectorCount > 0 ? reach.LastNode + 1 : reach.LastNode + 1 + LakeSearchNodes;
                    int submerged = -1;
                    for (int i = FirstPointAtOrAfter(reach.LastNode); i < points.Count && pointParam[i] <= searchLimit; i++)
                    {
                        if (LakeMeshBuilder.IsSubmerged(lakes, planet, reach.EndLake, points[i]))
                        {
                            submerged = i;
                            break;
                        }
                    }
                    if (submerged >= 0)
                    {
                        endIndex = submerged;
                        for (int i = submerged + 1; i < points.Count && i <= submerged + SubmergedEndSamples; i++)
                        {
                            if (!LakeMeshBuilder.IsSubmerged(lakes, planet, reach.EndLake, points[i]))
                            {
                                break;
                            }
                            endIndex = i;
                        }
                    }
                    else if (reach.ConnectorCount == 0)
                    {
                        diagnostics.LakeInflowGaps++;
                    }
                }
                endIndex = Mathf.Max(endIndex, startIndex);

                float distanceOffset = reach.Start == ReachStart.Source ? 0f
                    : reach.Start == ReachStart.Reactivated ? -pointDistance[startIndex] : NoTaperDistance;
                for (int i = startIndex; i <= endIndex; i++)
                {
                    double deficit = DeficitAt(network, layer, river, pointParam[i]) / totalArea;
                    reachPoints.Add(points[i]);
                    reachAccumulation.Add((float)System.Math.Max(0.0, pointAccumulation[i] - deficit));
                    reachDistance.Add(pointDistance[i] + distanceOffset);
                    bool underStart = reach.Start == ReachStart.LakeOutlet && i < emerged;
                    bool underEnd = reach.End == ReachEnd.LakeInflow && LakeMeshBuilder.IsSubmerged(lakes, planet, reach.EndLake, points[i]);
                    reachSubmerged.Add(underStart || underEnd);
                }

                // Shore contacts: a reach that touches the water of a lake it neither enters nor
                // leaves (source on the rim, path cutting a shore cell). Leading samples of a new
                // source are trimmed; others stay under the water (not lifted), never on top of it.
                if (reachPoints.Count > 0)
                {
                    bool trimming = reach.Start == ReachStart.Source || reach.Start == ReachStart.Reactivated;
                    int trimmed = 0;
                    for (int i = 0; i < reachPoints.Count; i++)
                    {
                        int contact = SubmergingLake(lakes, planet, reachPoints[i]);
                        bool other = contact >= 0 && contact != reach.StartLake && contact != reach.EndLake;
                        if (trimming && other)
                        {
                            trimmed++;
                            continue;
                        }
                        trimming = false;
                        if (other)
                        {
                            reachSubmerged[i] = true;
                            diagnostics.ShoreContactSamples++;
                        }
                    }
                    if (trimmed > 0)
                    {
                        diagnostics.ShoreContactSamples += trimmed;
                        reachPoints.RemoveRange(0, trimmed);
                        reachAccumulation.RemoveRange(0, trimmed);
                        reachDistance.RemoveRange(0, trimmed);
                        reachSubmerged.RemoveRange(0, trimmed);
                    }
                }

                // Closed basins: continue along the connector down to the shoreline.
                if (reach.End == ReachEnd.LakeInflow && reach.ConnectorCount > 0 && !reachSubmerged[reachSubmerged.Count - 1])
                {
                    AppendConnector(network, layer, r, reach, lakes, planet, maxStep);
                }
                if (reachPoints.Count < 2)
                {
                    continue;
                }
                sampleStarts.Add(samples.Count);
                ribbonRivers.Add(river);
                ribbonReaches.Add(r);
                for (int i = 0; i < reachPoints.Count; i++)
                {
                    samples.Add(new Sample
                    {
                        Direction = reachPoints[i],
                        River = river,
                        HalfWidth = 0.5f * settings.GetDisplayWidth(reachAccumulation[i], reachDistance[i]),
                        Submerged = reachSubmerged[i],
                    });
                    diagnostics.SubmergedSamples += reachSubmerged[i] ? 1 : 0;
                }
            }
        }

        void AppendConnector(RiverNetwork network, LakeRiverLayer layer, int reachIndex, RiverReach reach, LakeSet lakes,
            PlanetInstance planet, float maxStep)
        {
            HydrologyGrid grid = network.Hydrology.Grid;
            int last = reachPoints.Count - 1;
            float accumulation = reachAccumulation[last];
            float distance = reachDistance[last];
            Vector3 previous = reachPoints[last];
            int kept = 0;
            for (int c = -1; c < reach.ConnectorCount; c++)
            {
                Vector3 target = grid.GetCenter(c < 0 ? reach.EntryCell : layer.GetConnectorCell(reachIndex, c));
                float length = (target - previous).magnitude;
                if (length < 1e-6f)
                {
                    continue;
                }
                int steps = Mathf.Max(1, Mathf.CeilToInt(length / maxStep));
                for (int s = 1; s <= steps; s++)
                {
                    Vector3 point = Vector3.Lerp(previous, target, s / (float)steps).normalized;
                    bool submerged = LakeMeshBuilder.IsSubmerged(lakes, planet, reach.EndLake, point);
                    reachPoints.Add(point);
                    reachAccumulation.Add(accumulation);
                    reachDistance.Add(distance + length * s / steps);
                    reachSubmerged.Add(submerged);
                    if (submerged && ++kept > SubmergedEndSamples)
                    {
                        diagnostics.ConnectorReaches++;
                        return;
                    }
                }
                distance += length;
                previous = target;
            }
            if (kept == 0)
            {
                diagnostics.LakeInflowGaps++;
            }
            else
            {
                diagnostics.ConnectorReaches++;
            }
        }

        /// <summary>Lake whose water covers a direction (footprint or shore cell, true terrain below its level), or -1.</summary>
        static int SubmergingLake(LakeSet lakes, PlanetInstance planet, Vector3 direction)
        {
            HydrologyGrid grid = lakes.Hydrology.Grid;
            int cell = grid.CellFromDirection(direction);
            int lake = lakes.GetLakeOfCell(cell);
            if (lake < 0)
            {
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell) && lake < 0; k++)
                {
                    int other = lakes.GetLakeOfCell(grid.GetNeighbor(k));
                    if (other >= 0 && LakeMeshBuilder.IsShoreCell(lakes, other, cell))
                    {
                        lake = other;
                    }
                }
            }
            return lake >= 0 && planet.GetElevation(direction) < lakes.GetLake(lake).Level ? lake : -1;
        }

        static double DeficitAt(RiverNetwork network, LakeRiverLayer layer, int river, float param)
        {
            int count = network.GetRiver(river).NodeCount;
            int a = Mathf.Clamp(Mathf.FloorToInt(param), 0, count - 1);
            int b = Mathf.Min(a + 1, count - 1);
            float t = Mathf.Clamp01(param - a);
            double da = layer.GetNodeDeficit(network.GetRiverNode(river, a));
            double db = layer.GetNodeDeficit(network.GetRiverNode(river, b));
            return da + (db - da) * t;
        }

        int FirstPointAtOrAfter(float param)
        {
            for (int i = 0; i < points.Count; i++)
            {
                if (pointParam[i] >= param - 1e-4f)
                {
                    return i;
                }
            }
            return points.Count - 1;
        }

        int LastPointAtOrBefore(float param)
        {
            for (int i = points.Count - 1; i >= 0; i--)
            {
                if (pointParam[i] <= param + 1e-4f)
                {
                    return i;
                }
            }
            return 0;
        }

        void Upload(Mesh mesh, float radius)
        {
            mesh.Clear();
            mesh.name = "Planet Rivers";
            mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, sides);
            mesh.SetUVs(1, widths);
            mesh.SetTriangles(triangles, 0, false);
            // The shader lifts / widens vertices slightly: use the planet bounds for culling.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (radius * 2.4f));
        }

        /// <summary>Resamples a river path; returns the sample index where its final segment starts.</summary>
        int Resample(RiverNetwork network, int river, float maxStep)
        {
            points.Clear();
            pointAccumulation.Clear();
            pointDistance.Clear();
            pointParam.Clear();
            int start = network.GetPathStart(river);
            int end = network.GetPathEnd(river);
            BuildRawPolyline(network, river);
            float distance = 0f;
            int finalSegmentStart = 0;
            int segment = 0;
            float paramA = ProjectOnRaw(network.GetPathPoint(start), ref segment);
            for (int i = start; i < end; i++)
            {
                Vector3 a = network.GetPathPoint(i);
                float accumulationA = network.GetPathAccumulation(i);
                if (i == end - 1)
                {
                    AddPoint(a, accumulationA, distance, paramA);
                    break;
                }
                if (i == end - 2)
                {
                    finalSegmentStart = points.Count;
                }
                Vector3 b = network.GetPathPoint(i + 1);
                float accumulationB = network.GetPathAccumulation(i + 1);
                float paramB = ProjectOnRaw(b, ref segment);
                float length = (b - a).magnitude;
                int steps = Mathf.Max(1, Mathf.CeilToInt(length / maxStep));
                for (int s = 0; s < steps; s++)
                {
                    float t = s / (float)steps;
                    AddPoint(Vector3.Lerp(a, b, t).normalized, Mathf.Lerp(accumulationA, accumulationB, t), distance + length * t,
                        Mathf.Lerp(paramA, paramB, t));
                }
                distance += length;
                paramA = paramB;
            }
            return finalSegmentStart;
        }

        /// <summary>Raw drainage path of a river: its node cell centers plus the junction / mouth cell.</summary>
        void BuildRawPolyline(RiverNetwork network, int river)
        {
            rawPolyline.Clear();
            HydrologyGrid grid = network.Hydrology.Grid;
            River data = network.GetRiver(river);
            for (int i = 0; i < data.NodeCount; i++)
            {
                rawPolyline.Add(grid.GetCenter(network.GetNodeCell(network.GetRiverNode(river, i))));
            }
            if (data.JunctionCell >= 0 || data.MouthCell >= 0)
            {
                rawPolyline.Add(grid.GetCenter(data.JunctionCell >= 0 ? data.JunctionCell : data.MouthCell));
            }
        }

        /// <summary>
        /// Node parameter of a trajectory point (k = k-th node center): projection on the raw path,
        /// advancing monotonically (trajectories stay in the drainage corridor, in order).
        /// </summary>
        float ProjectOnRaw(Vector3 p, ref int segment)
        {
            int last = rawPolyline.Count - 2;
            if (last < 0)
            {
                return 0f;
            }
            float best = SegmentDistance(p, segment, out float bestT);
            while (segment < last)
            {
                float next = SegmentDistance(p, segment + 1, out float nextT);
                if (next > best)
                {
                    break;
                }
                segment++;
                best = next;
                bestT = nextT;
            }
            return segment + bestT;
        }

        float SegmentDistance(Vector3 p, int segment, out float t)
        {
            Vector3 a = rawPolyline[segment], b = rawPolyline[segment + 1];
            Vector3 ab = b - a;
            t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-20f));
            return (a + ab * t - p).sqrMagnitude;
        }

        void AddPoint(Vector3 point, float accumulation, float distance, float param)
        {
            points.Add(point);
            pointAccumulation.Add(accumulation);
            pointDistance.Add(distance);
            pointParam.Add(param);
        }

        /// <summary>
        /// Makes an ocean-bound river end just under the RENDERED ocean surface. Root cause of
        /// the gaps: the path ends at the hydrology ocean cell center, but the render mesh
        /// interpolates the coastline differently (different resolution), so that point can
        /// still be rendered land. Clipping only applies to the final stretch (beyond the last
        /// land cell), so rivers are never cut inland. If the ribbon still ends on rendered
        /// land, it is extended along the final drainage direction, only across cells the
        /// hydrology classifies as ocean, until submerged; otherwise it is left unchanged and
        /// reported as an unresolved mouth.
        /// </summary>
        void ResolveMouth(RiverNetwork network, River data, PlanetSurfaceSampler sampler, float oceanRadius, float radius,
            int finalSegmentStart, float maxStep, float cellSpacing, float meshSpacing)
        {
            float submergedRadius = oceanRadius - SubmergeDepth * radius;
            for (int i = finalSegmentStart; i < points.Count; i++)
            {
                if (sampler.Sample(points[i]).magnitude < oceanRadius)
                {
                    // Keep up to 3 more samples to sink the end, but only while they stay under
                    // the water (the rendered shore can re-emerge, e.g. a sand bar).
                    int last = i;
                    for (int k = i + 1; k < points.Count && k - i <= 3; k++)
                    {
                        float rendered = sampler.Sample(points[k]).magnitude;
                        if (rendered >= oceanRadius)
                        {
                            break;
                        }
                        last = k;
                        if (rendered < submergedRadius)
                        {
                            break;
                        }
                    }
                    Truncate(last + 1);
                    return;
                }
            }

            int originalCount = points.Count;
            Vector3 end = points[originalCount - 1];
            Vector3 direction = network.GetPathPoint(network.GetPathEnd(data.Id) - 1) - network.GetPathPoint(network.GetPathEnd(data.Id) - 2);
            float maxExtension = MouthExtensionCells * cellSpacing + 2f * meshSpacing;
            float travelled = 0f;
            bool reachedWater = false;
            int submergedSamples = 0;
            Vector3 current = end;
            while (travelled < maxExtension)
            {
                Vector3 tangent = direction - current * Vector3.Dot(direction, current);
                if (tangent.sqrMagnitude < 1e-20f)
                {
                    break;
                }
                current = (current + tangent.normalized * maxStep).normalized;
                travelled += maxStep;
                if (network.Hydrology.IsLand(network.Hydrology.Grid.CellFromDirection(current)))
                {
                    break; // never extend across land
                }
                AddPoint(current, pointAccumulation[originalCount - 1], pointDistance[pointDistance.Count - 1] + maxStep,
                    pointParam[originalCount - 1]);
                float rendered = sampler.Sample(current).magnitude;
                if (rendered < oceanRadius)
                {
                    reachedWater = true;
                }
                if (reachedWater && (rendered < submergedRadius || ++submergedSamples >= 3))
                {
                    break;
                }
            }
            if (reachedWater)
            {
                diagnostics.MouthsExtended++;
            }
            else
            {
                Truncate(originalCount);
                diagnostics.MouthGaps++;
                unresolvedMouths.Add(data.MouthCell);
            }
        }

        void Truncate(int count)
        {
            int remove = points.Count - count;
            if (remove > 0)
            {
                points.RemoveRange(count, remove);
                pointAccumulation.RemoveRange(count, remove);
                pointDistance.RemoveRange(count, remove);
                pointParam.RemoveRange(count, remove);
            }
        }

        /// <summary>
        /// Narrows ribbons locally where two rivers that are not connected there would overlap
        /// (adjacent parallel rivers, nearby mouths). Rivers meeting at a junction are allowed to
        /// overlap near it: that overlap is what joins them visually.
        /// </summary>
        void ClampWidthOverlaps(RiverNetwork network, HydrologyGrid grid, float cellSpacing)
        {
            int count = samples.Count;
            if (count == 0)
            {
                return;
            }
            float maxHalf = 0f;
            for (int i = 0; i < count; i++)
            {
                maxHalf = Mathf.Max(maxHalf, samples[i].HalfWidth);
            }
            // 3D spatial hash with cubes as large as the largest possible overlap distance: two
            // overlapping samples are always in the same or an adjacent cube (27 cubes).
            // Lower bound keeps every axis index within 14 bits (2 / 16000 < 2^14 cubes).
            float cubeSize = Mathf.Max(2f * maxHalf / OverlapClearance, 2f / 16000f);
            if (sortedKeys.Length < count)
            {
                sortedKeys = new long[count];
                sortedSamples = new int[count];
            }
            for (int i = 0; i < count; i++)
            {
                sortedKeys[i] = CubeKey(samples[i].Direction, cubeSize);
                sortedSamples[i] = i;
            }
            System.Array.Sort(sortedKeys, sortedSamples, 0, count);
            cellRanges.Clear();
            for (int k = 0; k < count;)
            {
                long key = sortedKeys[k];
                int first = k;
                while (k < count && sortedKeys[k] == key)
                {
                    k++;
                }
                cellRanges[key] = ((long)first << 32) | (uint)(k - first);
            }

            overlapPairs.Clear();
            var clamped = new float[count];
            for (int i = 0; i < count; i++)
            {
                clamped[i] = samples[i].HalfWidth;
            }
            for (int i = 0; i < count; i++)
            {
                Sample si = samples[i];
                Vector3 d = si.Direction;
                int cx = Quantize(d.x, cubeSize), cy = Quantize(d.y, cubeSize), cz = Quantize(d.z, cubeSize);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!cellRanges.TryGetValue(PackKey(cx + dx, cy + dy, cz + dz), out long range))
                    {
                        continue;
                    }
                    int otherFirst = (int)(range >> 32), otherLength = (int)(range & 0xffffffff);
                    for (int b = otherFirst; b < otherFirst + otherLength; b++)
                    {
                        // Each pair once (j > i); both samples are narrowed when they overlap.
                        int j = sortedSamples[b];
                        if (j <= i)
                        {
                            continue;
                        }
                        Sample sj = samples[j];
                        if (sj.River == si.River)
                        {
                            continue;
                        }
                        float reach = (si.HalfWidth + sj.HalfWidth) / OverlapClearance;
                        float squared = (d - sj.Direction).sqrMagnitude;
                        if (squared < reach * reach && !AreJoinedHere(network, grid, si, sj, cellSpacing))
                        {
                            float distance = Mathf.Sqrt(squared);
                            overlapPairs.Add(((long)Mathf.Min(si.River, sj.River) << 32) | (uint)Mathf.Max(si.River, sj.River));
                            clamped[i] = Mathf.Min(clamped[i], 0.5f * OverlapClearance * distance);
                            clamped[j] = Mathf.Min(clamped[j], 0.5f * OverlapClearance * distance);
                        }
                    }
                }
            }
            for (int i = 0; i < count; i++)
            {
                Sample sample = samples[i];
                sample.HalfWidth = clamped[i];
                samples[i] = sample;
            }
            diagnostics.WidthOverlaps = overlapPairs.Count;
        }

        static int Quantize(float value, float size)
        {
            return Mathf.FloorToInt((value + 1f) / size);
        }

        static long PackKey(int x, int y, int z)
        {
            // 14 bits per axis (cube sizes are >= 1e-4 of the unit sphere diameter at most).
            return ((long)(x & 0x3fff) << 28) | ((long)(y & 0x3fff) << 14) | (long)(z & 0x3fff);
        }

        static long CubeKey(Vector3 d, float size)
        {
            return PackKey(Quantize(d.x, size), Quantize(d.y, size), Quantize(d.z, size));
        }

        /// <summary>True when the two rivers meet at a junction close to these samples.</summary>
        static bool AreJoinedHere(RiverNetwork network, HydrologyGrid grid, Sample a, Sample b, float cellSpacing)
        {
            River ra = network.GetRiver(a.River);
            River rb = network.GetRiver(b.River);
            int junction = -1;
            if (ra.ParentRiver == b.River)
            {
                junction = ra.JunctionCell;
            }
            else if (rb.ParentRiver == a.River)
            {
                junction = rb.JunctionCell;
            }
            else if (ra.JunctionCell >= 0 && ra.JunctionCell == rb.JunctionCell)
            {
                junction = ra.JunctionCell;
            }
            if (junction < 0)
            {
                return false;
            }
            Vector3 center = grid.GetCenter(junction);
            float zone = JunctionZoneCells * cellSpacing + a.HalfWidth + b.HalfWidth;
            return (a.Direction - center).magnitude < zone || (b.Direction - center).magnitude < zone;
        }

        void AppendRibbon(PlanetSurfaceSampler sampler, float radius, int first, int end)
        {
            int count = end - first;
            int firstVertex = vertices.Count;
            Vector3 previousTangent = Vector3.zero;
            Vector3 lastLeft = Vector3.zero, lastRight = Vector3.zero;
            for (int k = 0; k < count; k++)
            {
                Vector3 p = samples[first + k].Direction;
                Vector3 forward = samples[first + Mathf.Min(k + 1, count - 1)].Direction - samples[first + Mathf.Max(k - 1, 0)].Direction;
                Vector3 tangent = forward - p * Vector3.Dot(forward, p);
                tangent = tangent.sqrMagnitude > 1e-14f ? tangent.normalized : previousTangent;
                previousTangent = tangent;
                Vector3 side = Vector3.Cross(p, tangent);
                if (side.sqrMagnitude < 1e-12f)
                {
                    side = Vector3.Cross(p, Mathf.Abs(p.y) < 0.99f ? Vector3.up : Vector3.right);
                }
                side.Normalize();

                float angularHalfWidth = samples[first + k].HalfWidth;
                float halfWidth = angularHalfWidth * radius;
                Vector3 left = sampler.Sample((p + side * angularHalfWidth).normalized);
                Vector3 right = sampler.Sample((p - side * angularHalfWidth).normalized);

                // Fold prevention: on a tight bend the inner edge would move backwards and fold
                // triangles over; collapse it onto its previous vertex instead.
                if (k > 0)
                {
                    if (Vector3.Dot(left - lastLeft, tangent) <= 0f)
                    {
                        left = lastLeft;
                        diagnostics.CollapsedFoldVertices++;
                    }
                    if (Vector3.Dot(right - lastRight, tangent) <= 0f)
                    {
                        right = lastRight;
                        diagnostics.CollapsedFoldVertices++;
                    }
                }
                if (!IsFinite(left) || !IsFinite(right))
                {
                    diagnostics.NonFiniteVertices++;
                    left = right = p * radius;
                }
                lastLeft = left;
                lastRight = right;

                vertices.Add(left);
                vertices.Add(right);
                normals.Add(p);
                normals.Add(p);
                sides.Add(new Vector4(side.x, side.y, side.z, 1f));
                sides.Add(new Vector4(side.x, side.y, side.z, -1f));
                float submerged = samples[first + k].Submerged ? 1f : 0f;
                widths.Add(new Vector2(halfWidth, submerged));
                widths.Add(new Vector2(halfWidth, submerged));

                if (k > 0)
                {
                    int l0 = firstVertex + 2 * (k - 1), r0 = l0 + 1, l1 = l0 + 2, r1 = l0 + 3;
                    AddTriangle(l0, l1, r0);
                    AddTriangle(r0, l1, r1);
                }
            }
        }

        /// <summary>Emits a triangle only if it is valid: finite, non-degenerate, facing outward.</summary>
        void AddTriangle(int a, int b, int c)
        {
            Vector3 pa = vertices[a], pb = vertices[b], pc = vertices[c];
            Vector3 normal = Vector3.Cross(pb - pa, pc - pa);
            float area = normal.magnitude;
            if (area <= DegenerateRelativeArea * pa.sqrMagnitude)
            {
                diagnostics.DegenerateTrianglesSkipped++;
                return;
            }
            // Ribbon winding: valid triangles have their normal pointing toward the planet center
            // (Cull Off renders both sides); the opposite sign means a fold.
            if (Vector3.Dot(normal, pa + pb + pc) > 0f || float.IsNaN(area))
            {
                diagnostics.InvalidTrianglesRemoved++;
                return;
            }
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }

        /// <summary>Tributary ribbons must end on their junction (measured, not assumed).</summary>
        void CheckJunctions(RiverNetwork network, PlanetSurfaceSampler sampler, float radius, float cellSpacing, LakeRiverLayer lakes)
        {
            for (int ribbon = 0; ribbon < ribbonRivers.Count; ribbon++)
            {
                River data = network.GetRiver(ribbonRivers[ribbon]);
                if (data.JunctionCell < 0)
                {
                    continue;
                }
                if (lakes != null)
                {
                    RiverReach reach = lakes.GetReach(ribbonReaches[ribbon]);
                    if (reach.End != ReachEnd.RiverEnd || reach.LastNode != data.NodeCount - 1)
                    {
                        continue; // ends in a lake, not at its junction
                    }
                }
                int last = GetRibbonStart(ribbon + 1) - 2;
                Vector3 end = (vertices[last] + vertices[last + 1]) * 0.5f;
                Vector3 junction = sampler.Sample(network.Hydrology.Grid.GetCenter(data.JunctionCell));
                if ((end - junction).magnitude > 0.25f * cellSpacing * radius + 2f * widths[last].x)
                {
                    diagnostics.JunctionDefects++;
                }
            }
        }

        static bool IsFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }
    }

    /// <summary>Measured river ribbon diagnostics (see <see cref="RiverRibbonBuilder.LastDiagnostics"/>).</summary>
    public struct RiverGeometryDiagnostics
    {
        public int OceanMouths;
        /// <summary>Mouths extended along their final drainage direction to reach rendered water.</summary>
        public int MouthsExtended;
        /// <summary>Ocean-bound ribbons that could not be connected to rendered water.</summary>
        public int MouthGaps;
        /// <summary>Tributary ribbons not ending on their junction.</summary>
        public int JunctionDefects;
        /// <summary>Pairs of unconnected rivers whose ribbons overlapped (narrowed locally).</summary>
        public int WidthOverlaps;
        public int CollapsedFoldVertices;
        public int DegenerateTrianglesSkipped;
        /// <summary>Folded / invalid triangles detected and not emitted.</summary>
        public int InvalidTrianglesRemoved;
        public int NonFiniteVertices;
        /// <summary>Reaches ending in a lake / leaving an open lake (lake-aware ribbons only).</summary>
        public int LakeInflows, LakeOutflows;
        /// <summary>Lake inflows / outflows whose path never met the lake water near the shore (kept at the cell).</summary>
        public int LakeInflowGaps, LakeOutflowGaps;
        /// <summary>Inflows continued across a closed basin floor.</summary>
        public int ConnectorReaches;
        /// <summary>Ribbon samples under lake water (not lifted).</summary>
        public int SubmergedSamples;
        /// <summary>Samples touching a lake the reach neither enters nor leaves (trimmed at a source, else under water).</summary>
        public int ShoreContactSamples;
    }
}
