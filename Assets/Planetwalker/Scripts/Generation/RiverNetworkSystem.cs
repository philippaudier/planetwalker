using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>Role of a river cell in the network (flags can combine, e.g. Source | Mouth).</summary>
    [Flags]
    public enum RiverNodeKind : byte
    {
        None = 0,
        /// <summary>No selected upstream cell.</summary>
        Source = 1,
        /// <summary>Two or more selected upstream cells join here.</summary>
        Confluence = 2,
        /// <summary>Last land cell before the ocean.</summary>
        Mouth = 4,
        /// <summary>Endorheic outlet (planet without ocean).</summary>
        Sink = 8,
    }

    /// <summary>
    /// A river as a geographic entity: a main stem from one source to its end. At each
    /// confluence the branch draining the larger area continues the river; the other one
    /// ends there as a tributary (its <see cref="ParentRiver"/>).
    /// </summary>
    public struct River
    {
        public int Id;
        /// <summary>River this one flows into, or -1 for a river reaching the ocean / a sink.</summary>
        public int ParentRiver;
        public int SourceCell;
        /// <summary>Last cell belonging to this river.</summary>
        public int EndCell;
        /// <summary>Cell of the parent river where this tributary joins it, or -1.</summary>
        public int JunctionCell;
        /// <summary>Ocean cell receiving this river, or -1 (tributary or sink).</summary>
        public int MouthCell;
        public int FirstNode;
        public int NodeCount;
        public int MaxStrahler;
        /// <summary>Drained area at the river end, in steradians (not a discharge).</summary>
        public double DrainedArea;
        /// <summary>Length along the hydrological path, in radians (multiply by the radius).</summary>
        public float Length;
        public int Basin;
    }

    /// <summary>Continuous portion between two important nodes (source, confluence, mouth).</summary>
    public struct RiverSegment
    {
        public int StartCell;
        public int EndCell;
        public int CellCount;
        public int Strahler;
        public int River;
    }

    /// <summary>
    /// River network extracted from a <see cref="HydrologyResult"/> (V0.1.1D-A). Pure data,
    /// independent of any rendering: future systems (riparian vegetation, wetlands, fertile
    /// valleys, roads, settlements) can query rivers, orders and drained areas per cell.
    /// It represents a POTENTIAL drainage network (drained area), not a physical discharge.
    /// </summary>
    public sealed class RiverNetwork
    {
        internal int[] nodeOfCell;
        internal int[] nodeCell;
        internal int[] nodeDownstream;
        internal int[] nodeStrahler;
        internal int[] nodeRiver;
        internal int[] nodeUpstreamCount;
        internal RiverNodeKind[] nodeKind;
        internal River[] rivers;
        internal int[] riverNodes;
        internal RiverSegment[] segments;

        internal Vector3[] pathPoints = Array.Empty<Vector3>();
        internal float[] pathAccumulation = Array.Empty<float>();
        internal int[] pathStart = Array.Empty<int>();
        internal int[] pathPiece = Array.Empty<int>();
        internal HashSet<int> crossingCells = new HashSet<int>();

        internal RiverNetwork(HydrologyResult hydrology, float threshold)
        {
            Hydrology = hydrology;
            Threshold = threshold;
            HydrologyVersion = hydrology.Version;
            PathSmoothingIterations = -1;
        }

        public HydrologyResult Hydrology { get; }
        /// <summary>Minimum drained area of a river cell, fraction of the planet surface.</summary>
        public float Threshold { get; }
        public int HydrologyVersion { get; }

        public int NodeCount { get; internal set; }
        public int RiverCount => rivers.Length;
        public int SegmentCount => segments.Length;
        public int SourceCount { get; internal set; }
        public int ConfluenceCount { get; internal set; }
        public int MouthCount { get; internal set; }
        public int SinkCount { get; internal set; }
        public int MaxStrahler { get; internal set; }
        /// <summary>Total length along hydrological paths, in radians.</summary>
        public double TotalLength { get; internal set; }

        public float ExtractionMilliseconds { get; internal set; }
        public float TopologyMilliseconds { get; internal set; }
        public float ClassificationMilliseconds { get; internal set; }
        public float SmoothingMilliseconds { get; internal set; }
        public int PathSmoothingIterations { get; internal set; }

        /// <summary>Crossings between raw drainage paths of different rivers (D8 graph). For
        /// steepest-descent D8 these cannot occur inside a face grid (see report); counted anyway.</summary>
        public int GraphCrossingCount { get; internal set; }

        /// <summary>Crossings remaining after smoothing correction (should be 0).</summary>
        public int SmoothingCrossingCount { get; internal set; }

        /// <summary>Trajectory pieces reverted to the raw path to remove smoothing crossings.</summary>
        public int CorrectedPieceCount { get; internal set; }

        /// <summary>True for cells where a non-topological crossing was detected (diagnostics).</summary>
        public bool IsCrossingCell(int cell) => crossingCells.Contains(cell);

        public bool IsRiverCell(int cell) => nodeOfCell[cell] >= 0;
        /// <summary>Node index of a cell, or -1 when the cell is not part of the network.</summary>
        public int GetNodeOfCell(int cell) => nodeOfCell[cell];
        public int GetNodeCell(int node) => nodeCell[node];
        /// <summary>Downstream node, or -1 at a mouth / sink. Node indices increase downstream.</summary>
        public int GetDownstreamNode(int node) => nodeDownstream[node];
        public int GetUpstreamCount(int node) => nodeUpstreamCount[node];
        public int GetStrahler(int node) => nodeStrahler[node];
        public RiverNodeKind GetNodeKind(int node) => nodeKind[node];
        public int GetRiverOfNode(int node) => nodeRiver[node];
        public River GetRiver(int river) => rivers[river];
        /// <summary>Node of a river, ordered from its source (0) downstream.</summary>
        public int GetRiverNode(int river, int index) => riverNodes[rivers[river].FirstNode + index];
        public RiverSegment GetSegment(int segment) => segments[segment];

        /// <summary>Smoothed trajectory of a river: points [GetPathStart, GetPathEnd) on the unit
        /// sphere, from the source to the junction / mouth (mouths end on the ocean cell).</summary>
        public int GetPathStart(int river) => pathStart[river];
        public int GetPathEnd(int river) => pathStart[river + 1];
        public Vector3 GetPathPoint(int index) => pathPoints[index];
        /// <summary>Drained area along the trajectory, fraction of the planet surface.</summary>
        public float GetPathAccumulation(int index) => pathAccumulation[index];
        public int PathPointCount => pathStart.Length > 0 ? pathStart[pathStart.Length - 1] : 0;
    }

    /// <summary>
    /// Builds <see cref="RiverNetwork"/> data from a hydrology result. Stages: extraction,
    /// topology, classification (Strahler + main stems + segments), trajectories.
    /// Deterministic and linear in the number of cells.
    /// </summary>
    public sealed class RiverNetworkSystem
    {
        /// <summary>Extraction, topology and classification (trajectories are built separately).</summary>
        public RiverNetwork Build(HydrologyResult hydrology, float threshold)
        {
            var network = new RiverNetwork(hydrology, threshold);
            HydrologyGrid grid = hydrology.Grid;
            int cellCount = grid.CellCount;
            var stopwatch = Stopwatch.StartNew();

            // 1. Extraction: drained area above the threshold. Accumulation never decreases
            // downstream, so a selected cell's whole path to its outlet is selected too: the
            // network is continuous by construction. Nodes are numbered upstream first
            // (reverse flood order), hence every node index is lower than its downstream one.
            var nodeOfCell = new int[cellCount];
            var nodeCells = new List<int>();
            for (int cell = 0; cell < cellCount; cell++)
            {
                nodeOfCell[cell] = -1;
            }
            for (int k = hydrology.OrderCount - 1; k >= 0; k--)
            {
                int cell = hydrology.GetOrderedCell(k);
                if (hydrology.IsRiverCandidate(cell, threshold))
                {
                    nodeOfCell[cell] = nodeCells.Count;
                    nodeCells.Add(cell);
                }
            }
            int nodeCount = nodeCells.Count;
            network.nodeOfCell = nodeOfCell;
            network.nodeCell = nodeCells.ToArray();
            network.NodeCount = nodeCount;
            network.ExtractionMilliseconds = Lap(stopwatch);

            // 2. Topology: downstream links, upstream counts, node kinds.
            var downstream = new int[nodeCount];
            var upstreamCount = new int[nodeCount];
            var kind = new RiverNodeKind[nodeCount];
            for (int node = 0; node < nodeCount; node++)
            {
                int cell = network.nodeCell[node];
                int receiver = hydrology.GetReceiver(cell);
                downstream[node] = receiver >= 0 ? nodeOfCell[receiver] : -1;
                if (downstream[node] >= 0)
                {
                    upstreamCount[downstream[node]]++;
                }
                else if (receiver >= 0)
                {
                    kind[node] |= RiverNodeKind.Mouth;
                }
                else
                {
                    kind[node] |= RiverNodeKind.Sink;
                }
            }
            for (int node = 0; node < nodeCount; node++)
            {
                if (upstreamCount[node] == 0)
                {
                    kind[node] |= RiverNodeKind.Source;
                }
                else if (upstreamCount[node] >= 2)
                {
                    kind[node] |= RiverNodeKind.Confluence;
                }
            }
            network.nodeDownstream = downstream;
            network.nodeUpstreamCount = upstreamCount;
            network.nodeKind = kind;
            network.TopologyMilliseconds = Lap(stopwatch);

            // 3. Classification. Strahler orders upstream first, and for every node its main
            // upstream branch (largest drained area, ties: lowest cell id).
            var strahler = new int[nodeCount];
            ComputeStrahler(downstream, strahler);
            var mainDonor = new int[nodeCount];
            for (int node = 0; node < nodeCount; node++)
            {
                mainDonor[node] = -1;
            }
            for (int node = 0; node < nodeCount; node++)
            {
                int down = downstream[node];
                if (down < 0)
                {
                    continue;
                }
                int current = mainDonor[down];
                if (current < 0 || IsLargerBranch(hydrology, network.nodeCell[node], network.nodeCell[current]))
                {
                    mainDonor[down] = node;
                }
            }

            // Main stems, downstream first: a node continues the river of its downstream node
            // if it is that node's main branch; otherwise it starts a tributary there.
            var nodeRiver = new int[nodeCount];
            var riverParent = new List<int>();
            for (int node = nodeCount - 1; node >= 0; node--)
            {
                int down = downstream[node];
                if (down >= 0 && mainDonor[down] == node)
                {
                    nodeRiver[node] = nodeRiver[down];
                }
                else
                {
                    nodeRiver[node] = riverParent.Count;
                    riverParent.Add(down >= 0 ? nodeRiver[down] : -1);
                }
            }
            int riverCount = riverParent.Count;
            var nodesPerRiver = new int[riverCount + 1];
            for (int node = 0; node < nodeCount; node++)
            {
                nodesPerRiver[nodeRiver[node] + 1]++;
            }
            for (int river = 0; river < riverCount; river++)
            {
                nodesPerRiver[river + 1] += nodesPerRiver[river];
            }
            var riverNodes = new int[nodeCount];
            var fill = new int[riverCount];
            for (int node = 0; node < nodeCount; node++)
            {
                int river = nodeRiver[node];
                riverNodes[nodesPerRiver[river] + fill[river]++] = node;
            }

            var rivers = new River[riverCount];
            double totalLength = 0;
            for (int river = 0; river < riverCount; river++)
            {
                int first = nodesPerRiver[river];
                int count = nodesPerRiver[river + 1] - first;
                int sourceNode = riverNodes[first];
                int endNode = riverNodes[first + count - 1];
                int endCell = network.nodeCell[endNode];
                int down = downstream[endNode];
                int receiver = hydrology.GetReceiver(endCell);
                float length = 0f;
                for (int i = 1; i < count; i++)
                {
                    length += (grid.GetCenter(network.nodeCell[riverNodes[first + i]]) - grid.GetCenter(network.nodeCell[riverNodes[first + i - 1]])).magnitude;
                }
                if (receiver >= 0)
                {
                    length += (grid.GetCenter(receiver) - grid.GetCenter(endCell)).magnitude;
                }
                rivers[river] = new River
                {
                    Id = river,
                    ParentRiver = riverParent[river],
                    SourceCell = network.nodeCell[sourceNode],
                    EndCell = endCell,
                    JunctionCell = down >= 0 ? network.nodeCell[down] : -1,
                    MouthCell = down < 0 && receiver >= 0 ? receiver : -1,
                    FirstNode = first,
                    NodeCount = count,
                    MaxStrahler = strahler[endNode],
                    DrainedArea = hydrology.GetAccumulation(endCell),
                    Length = length,
                    Basin = hydrology.GetBasin(endCell),
                };
                totalLength += length;
            }

            // Segments: a new segment starts at every source and confluence.
            var segments = new List<RiverSegment>();
            var nodeSegment = new int[nodeCount];
            for (int node = 0; node < nodeCount; node++)
            {
                bool starts = (kind[node] & (RiverNodeKind.Source | RiverNodeKind.Confluence)) != 0;
                if (starts)
                {
                    nodeSegment[node] = segments.Count;
                    segments.Add(new RiverSegment
                    {
                        StartCell = network.nodeCell[node],
                        EndCell = network.nodeCell[node],
                        CellCount = 1,
                        Strahler = strahler[node],
                        River = nodeRiver[node],
                    });
                }
                else
                {
                    int segment = nodeSegment[mainDonor[node]];
                    nodeSegment[node] = segment;
                    RiverSegment value = segments[segment];
                    value.EndCell = network.nodeCell[node];
                    value.CellCount++;
                    segments[segment] = value;
                }
            }

            int sources = 0, confluences = 0, mouths = 0, sinks = 0, maxOrder = 0;
            for (int node = 0; node < nodeCount; node++)
            {
                sources += (kind[node] & RiverNodeKind.Source) != 0 ? 1 : 0;
                confluences += (kind[node] & RiverNodeKind.Confluence) != 0 ? 1 : 0;
                mouths += (kind[node] & RiverNodeKind.Mouth) != 0 ? 1 : 0;
                sinks += (kind[node] & RiverNodeKind.Sink) != 0 ? 1 : 0;
                maxOrder = Math.Max(maxOrder, strahler[node]);
            }

            network.nodeStrahler = strahler;
            network.nodeRiver = nodeRiver;
            network.rivers = rivers;
            network.riverNodes = riverNodes;
            network.segments = segments.ToArray();
            network.SourceCount = sources;
            network.ConfluenceCount = confluences;
            network.MouthCount = mouths;
            network.SinkCount = sinks;
            network.MaxStrahler = maxOrder;
            network.TotalLength = totalLength;
            network.ClassificationMilliseconds = Lap(stopwatch);
            return network;
        }

        /// <summary>
        /// Strahler orders of a forest given as downstream links where every node's index is
        /// lower than its downstream node's (upstream-first numbering). Sources get 1; a node
        /// gets k + 1 when two or more of its upstream branches have the maximal order k,
        /// otherwise k.
        /// </summary>
        public static void ComputeStrahler(int[] downstream, int[] order)
        {
            int count = downstream.Length;
            var best = new int[count];
            var bestCount = new int[count];
            for (int node = 0; node < count; node++)
            {
                order[node] = best[node] == 0 ? 1 : bestCount[node] >= 2 ? best[node] + 1 : best[node];
                int down = downstream[node];
                if (down < 0)
                {
                    continue;
                }
                if (down <= node)
                {
                    throw new ArgumentException("Nodes must be numbered upstream first.");
                }
                if (order[node] > best[down])
                {
                    best[down] = order[node];
                    bestCount[down] = 1;
                }
                else if (order[node] == best[down])
                {
                    bestCount[down]++;
                }
            }
        }

        // Trajectory polish (V0.1.1D-A.1).
        const float RelaxationLimit = 0.35f;      // max move of a control point, in cell spacings
        const float AnchorTangentFraction = 0.3f;  // auxiliary point distance, fraction of the segment
        const int MaxCrossingRounds = 3;

        /// <summary>
        /// Builds the smoothed trajectories of every river (geometry stage; topology is untouched).
        /// </summary>
        /// <remarks>
        /// For each river (parents before tributaries), the control polygon runs through its cell
        /// centers plus the junction cell (tributary) or the ocean cell (mouth). Then:
        /// 1. Corridor-constrained relaxation: interior control points move toward the average of
        ///    their neighbours but never further than <see cref="RelaxationLimit"/> cell spacing
        ///    from their own cell center, which reduces the D8 staircase inside the corridor.
        /// 2. Tangent-continuous confluences: a confluence stays a fixed point, but two auxiliary
        ///    points along the local bisector make the river pass through it without a corner.
        /// 3. Tributary ends reach the exact junction point, arriving along a direction blended
        ///    with the parent's tangent there (a "Y" instead of a D8 "T").
        /// 4. Spherical Chaikin corner cutting between anchors (sources, confluences, ends fixed).
        /// 5. Crossing analysis: crossings that only exist after smoothing revert the involved
        ///    pieces to the raw path (bounded rounds); crossings of the raw drainage paths are only
        ///    counted, never "fixed" by inventing a junction.
        /// Every point stays within a fraction of a cell of the drainage corridor: no shortcuts
        /// across ridges, no jumps, continuous across cube faces. No randomness is involved.
        /// </remarks>
        public void BuildPaths(RiverNetwork network, int iterations)
        {
            var stopwatch = Stopwatch.StartNew();
            iterations = Mathf.Clamp(iterations, 0, 6);
            HydrologyGrid grid = network.Hydrology.Grid;
            var rawPieces = new HashSet<long>();
            var crossings = new List<RiverCrossingAnalyzer.Crossing>();
            int corrected = 0;
            int smoothingCrossings = 0;

            BuildRawPolylines(network, out Vector3[] rawPoints, out int[] rawStart);
            RiverCrossingAnalyzer.FindCrossings(rawPoints, rawStart, grid, crossings);
            var graphPairs = new HashSet<long>();
            var crossingCells = new HashSet<int>();
            foreach (RiverCrossingAnalyzer.Crossing crossing in crossings)
            {
                graphPairs.Add(PairKey(crossing.LineA, crossing.LineB));
                crossingCells.Add(grid.CellFromDirection(crossing.Location));
            }
            network.GraphCrossingCount = crossings.Count;

            for (int round = 0; ; round++)
            {
                BuildAllPaths(network, iterations, rawPieces);
                RiverCrossingAnalyzer.FindCrossings(network.pathPoints, network.pathStart, grid, crossings);
                smoothingCrossings = 0;
                int added = 0;
                foreach (RiverCrossingAnalyzer.Crossing crossing in crossings)
                {
                    if (graphPairs.Contains(PairKey(crossing.LineA, crossing.LineB)))
                    {
                        continue; // manifestation of a crossing already present in the drainage paths
                    }
                    smoothingCrossings++;
                    crossingCells.Add(grid.CellFromDirection(crossing.Location));
                    added += rawPieces.Add(PieceKey(crossing.LineA, network.pathPiece[crossing.SegmentA])) ? 1 : 0;
                    added += rawPieces.Add(PieceKey(crossing.LineB, network.pathPiece[crossing.SegmentB])) ? 1 : 0;
                }
                if (smoothingCrossings == 0 || added == 0 || round + 1 >= MaxCrossingRounds)
                {
                    break;
                }
                corrected += added;
            }

            network.SmoothingCrossingCount = smoothingCrossings;
            network.CorrectedPieceCount = corrected;
            network.crossingCells = crossingCells;
            network.PathSmoothingIterations = iterations;
            network.SmoothingMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
        }

        static long PairKey(int a, int b)
        {
            return ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
        }

        static long PieceKey(int river, int piece)
        {
            return ((long)river << 32) | (uint)piece;
        }

        /// <summary>Raw drainage paths (cell centers + junction / mouth cell) of every river.</summary>
        internal static void BuildRawPolylines(RiverNetwork network, out Vector3[] points, out int[] start)
        {
            HydrologyGrid grid = network.Hydrology.Grid;
            var list = new List<Vector3>();
            start = new int[network.RiverCount + 1];
            for (int river = 0; river < network.RiverCount; river++)
            {
                start[river] = list.Count;
                River data = network.rivers[river];
                for (int i = 0; i < data.NodeCount; i++)
                {
                    list.Add(grid.GetCenter(network.nodeCell[network.GetRiverNode(river, i)]));
                }
                if (data.JunctionCell >= 0 || data.MouthCell >= 0)
                {
                    list.Add(grid.GetCenter(data.JunctionCell >= 0 ? data.JunctionCell : data.MouthCell));
                }
            }
            start[network.RiverCount] = list.Count;
            points = list.ToArray();
        }

        void BuildAllPaths(RiverNetwork network, int iterations, HashSet<long> rawPieces)
        {
            HydrologyResult hydrology = network.Hydrology;
            HydrologyGrid grid = hydrology.Grid;
            double totalArea = grid.TotalArea;
            float spacing = Mathf.PI * 0.5f / grid.Resolution;

            var points = new List<Vector3>();
            var accumulation = new List<float>();
            var pieces = new List<int>();
            var start = new int[network.RiverCount + 1];
            var junctionTangents = new Dictionary<int, Vector3>();

            var raw = new List<Vector3>();
            var relaxed = new List<Vector3>();
            var areas = new List<float>();
            var anchors = new List<bool>();
            var cells = new List<int>();
            var piecePoints = new List<Vector3>();
            var pieceAccumulation = new List<float>();
            var scratchPoints = new List<Vector3>();
            var scratchAccumulation = new List<float>();

            // River ids are created downstream first: a parent always precedes its tributaries.
            for (int river = 0; river < network.RiverCount; river++)
            {
                start[river] = points.Count;
                River data = network.rivers[river];
                raw.Clear();
                areas.Clear();
                anchors.Clear();
                cells.Clear();
                for (int i = 0; i < data.NodeCount; i++)
                {
                    int node = network.GetRiverNode(river, i);
                    int cell = network.nodeCell[node];
                    raw.Add(grid.GetCenter(cell));
                    areas.Add((float)(hydrology.GetAccumulation(cell) / totalArea));
                    anchors.Add(i == 0 || (network.nodeKind[node] & RiverNodeKind.Confluence) != 0);
                    cells.Add(cell);
                }
                if (data.JunctionCell >= 0 || data.MouthCell >= 0)
                {
                    int endCell = data.JunctionCell >= 0 ? data.JunctionCell : data.MouthCell;
                    raw.Add(grid.GetCenter(endCell));
                    // Junction / mouth point keeps this river's own drained area (no width jump).
                    areas.Add((float)(data.DrainedArea / totalArea));
                    anchors.Add(true);
                    cells.Add(endCell);
                }
                anchors[anchors.Count - 1] = true;
                int count = raw.Count;
                if (count == 1)
                {
                    points.Add(raw[0]);
                    accumulation.Add(areas[0]);
                    pieces.Add(0);
                    continue;
                }

                // 1. Corridor-constrained relaxation of interior, non-anchor control points.
                relaxed.Clear();
                relaxed.AddRange(raw);
                int relaxationPasses = 2 * iterations;
                float limit = RelaxationLimit * spacing;
                for (int pass = 0; pass < relaxationPasses; pass++)
                {
                    for (int i = 1; i < count - 1; i++)
                    {
                        if (anchors[i])
                        {
                            continue;
                        }
                        Vector3 target = ((relaxed[i - 1] + relaxed[i + 1]) * 0.5f).normalized;
                        Vector3 moved = Vector3.Lerp(relaxed[i], target, 0.5f);
                        Vector3 offset = moved - raw[i];
                        if (offset.magnitude > limit)
                        {
                            moved = raw[i] + offset * (limit / offset.magnitude);
                        }
                        relaxed[i] = moved.normalized;
                    }
                }

                // 2-4. Pieces between anchors, with tangent-continuous anchors.
                int piece = 0;
                piecePoints.Clear();
                pieceAccumulation.Clear();
                Vector3 pendingAfter = Vector3.zero;
                float pendingAfterArea = 0f;
                bool hasPendingAfter = false;
                for (int i = 0; i < count; i++)
                {
                    bool usesRaw = iterations == 0 || rawPieces.Contains(PieceKey(river, piece));
                    Vector3 point = anchors[i] || usesRaw ? raw[i] : relaxed[i];
                    if (piecePoints.Count == 0)
                    {
                        piecePoints.Add(point);
                        pieceAccumulation.Add(areas[i]);
                        if (hasPendingAfter)
                        {
                            piecePoints.Add(pendingAfter);
                            pieceAccumulation.Add(pendingAfterArea);
                            hasPendingAfter = false;
                        }
                        continue;
                    }

                    bool isEnd = i == count - 1;
                    // Raw (reverted) pieces get no tangent points: they follow the drainage path.
                    bool nextRaw = !isEnd && rawPieces.Contains(PieceKey(river, piece + 1));
                    if (anchors[i] && iterations > 0)
                    {
                        Vector3 previous = piecePoints[piecePoints.Count - 1];
                        if (!isEnd)
                        {
                            // Interior confluence: pass through it along the bisector tangent.
                            Vector3 next = rawPieces.Contains(PieceKey(river, piece + 1)) ? raw[i + 1] : (anchors[i + 1] ? raw[i + 1] : relaxed[i + 1]);
                            Vector3 tangent = Tangent(point, next - previous);
                            junctionTangents[cells[i]] = tangent;
                            if (!usesRaw)
                            {
                                float before = AnchorTangentFraction * (point - previous).magnitude;
                                piecePoints.Add((point - tangent * before).normalized);
                                pieceAccumulation.Add(Mathf.Lerp(pieceAccumulation[pieceAccumulation.Count - 1], areas[i], 1f - AnchorTangentFraction));
                            }
                            if (!nextRaw)
                            {
                                float after = AnchorTangentFraction * (next - point).magnitude;
                                pendingAfter = (point + tangent * after).normalized;
                                pendingAfterArea = Mathf.Lerp(areas[i], areas[i + 1], AnchorTangentFraction);
                                hasPendingAfter = true;
                            }
                        }
                        else if (data.JunctionCell >= 0 && !usesRaw)
                        {
                            // Tributary end: arrive blended with the parent's direction ("Y").
                            Vector3 incoming = Tangent(point, point - previous);
                            Vector3 direction = incoming;
                            if (junctionTangents.TryGetValue(data.JunctionCell, out Vector3 parentTangent)
                                && Vector3.Dot(incoming, parentTangent) > -0.3f)
                            {
                                direction = Tangent(point, incoming + parentTangent);
                            }
                            float before = AnchorTangentFraction * (point - previous).magnitude;
                            piecePoints.Add((point - direction * before).normalized);
                            pieceAccumulation.Add(areas[i]);
                        }
                    }

                    piecePoints.Add(point);
                    pieceAccumulation.Add(areas[i]);
                    if (anchors[i])
                    {
                        // Close the piece [previous anchor .. this anchor] and restart from here.
                        if (!usesRaw)
                        {
                            Smooth(piecePoints, pieceAccumulation, iterations, scratchPoints, scratchAccumulation);
                        }
                        AppendPiece(points, accumulation, pieces, piece, piecePoints, pieceAccumulation);
                        piecePoints.Clear();
                        pieceAccumulation.Clear();
                        piece++;
                        if (!isEnd)
                        {
                            // The anchor also starts the next piece, followed by its tangent point.
                            piecePoints.Add(point);
                            pieceAccumulation.Add(areas[i]);
                            if (hasPendingAfter)
                            {
                                piecePoints.Add(pendingAfter);
                                pieceAccumulation.Add(pendingAfterArea);
                                hasPendingAfter = false;
                            }
                        }
                    }
                }
            }
            start[network.RiverCount] = points.Count;

            network.pathPoints = points.ToArray();
            network.pathAccumulation = accumulation.ToArray();
            network.pathPiece = pieces.ToArray();
            network.pathStart = start;
        }

        /// <summary>Unit tangent at a point of the unit sphere, along a direction.</summary>
        static Vector3 Tangent(Vector3 point, Vector3 direction)
        {
            Vector3 tangent = direction - point * Vector3.Dot(direction, point);
            return tangent.sqrMagnitude > 1e-20f ? tangent.normalized : Vector3.zero;
        }

        static void AppendPiece(List<Vector3> points, List<float> accumulation, List<int> pieces, int pieceIndex,
            List<Vector3> piece, List<float> pieceAccumulation)
        {
            // Consecutive pieces share their anchor: skip the duplicate first point.
            int first = points.Count > 0 && points[points.Count - 1] == piece[0] ? 1 : 0;
            for (int i = first; i < piece.Count; i++)
            {
                points.Add(piece[i]);
                accumulation.Add(pieceAccumulation[i]);
                pieces.Add(pieceIndex);
            }
        }

        /// <summary>Chaikin corner cutting with fixed endpoints, on the unit sphere.</summary>
        static void Smooth(List<Vector3> points, List<float> accumulation, int iterations,
            List<Vector3> scratchPoints, List<float> scratchAccumulation)
        {
            for (int pass = 0; pass < iterations && points.Count >= 3; pass++)
            {
                scratchPoints.Clear();
                scratchAccumulation.Clear();
                int last = points.Count - 1;
                scratchPoints.Add(points[0]);
                scratchAccumulation.Add(accumulation[0]);
                for (int i = 0; i < last; i++)
                {
                    if (i > 0)
                    {
                        scratchPoints.Add((points[i] * 0.75f + points[i + 1] * 0.25f).normalized);
                        scratchAccumulation.Add(accumulation[i] * 0.75f + accumulation[i + 1] * 0.25f);
                    }
                    if (i < last - 1)
                    {
                        scratchPoints.Add((points[i] * 0.25f + points[i + 1] * 0.75f).normalized);
                        scratchAccumulation.Add(accumulation[i] * 0.25f + accumulation[i + 1] * 0.75f);
                    }
                }
                scratchPoints.Add(points[last]);
                scratchAccumulation.Add(accumulation[last]);
                points.Clear();
                points.AddRange(scratchPoints);
                accumulation.Clear();
                accumulation.AddRange(scratchAccumulation);
            }
        }

        static bool IsLargerBranch(HydrologyResult hydrology, int cell, int other)
        {
            double a = hydrology.GetAccumulation(cell);
            double b = hydrology.GetAccumulation(other);
            return a > b || (a == b && cell < other);
        }

        static float Lap(Stopwatch stopwatch)
        {
            float milliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            return milliseconds;
        }
    }

    /// <summary>
    /// Caches the river network. Separate invalidations: a new hydrology analysis or a new
    /// threshold rebuilds the network; a smoothing change rebuilds trajectories only;
    /// visual parameters (width, color) never reach this cache.
    /// </summary>
    public sealed class RiverNetworkCache
    {
        readonly RiverNetworkSystem system = new RiverNetworkSystem();
        RiverNetwork cached;

        /// <summary>Number of topology builds (extraction + topology + classification).</summary>
        public int BuildCount { get; private set; }

        /// <summary>Number of trajectory builds.</summary>
        public int PathBuildCount { get; private set; }

        public RiverNetwork Current => cached;

        public RiverNetwork Get(HydrologyResult hydrology, float threshold, int smoothingIterations)
        {
            if (cached == null || cached.Hydrology != hydrology || cached.HydrologyVersion != hydrology.Version
                || cached.Threshold != threshold)
            {
                cached = system.Build(hydrology, threshold);
                BuildCount++;
            }
            if (cached.PathSmoothingIterations != smoothingIterations)
            {
                system.BuildPaths(cached, smoothingIterations);
                PathBuildCount++;
            }
            return cached;
        }

        public void Clear()
        {
            cached = null;
        }
    }
}
