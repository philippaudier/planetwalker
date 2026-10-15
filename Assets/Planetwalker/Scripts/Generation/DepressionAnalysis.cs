using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>Where a depression overflows once it is full.</summary>
    public enum DepressionOutlet : byte
    {
        /// <summary>No outlet (the whole landmass of a planet without ocean).</summary>
        None,
        /// <summary>Overflows into terrain that drains to the ocean (top-level depression).</summary>
        Ocean,
        /// <summary>Overflows into a neighbouring depression; both then form their parent.</summary>
        Depression,
    }

    /// <summary>
    /// A node of the depression hierarchy: one closed topographic depression (a leaf: one local
    /// minimum and its catchment) or the union of two depressions that merge once both are
    /// full (a meta-depression). Elevations are fractions of the radius, areas steradians.
    /// "At spill" values describe the depression filled exactly to its spill elevation.
    /// </summary>
    public struct DepressionCandidate
    {
        /// <summary>Stable id: leaves in cell order of their minimum, then merges in elevation order.</summary>
        public int Id;
        /// <summary>Enclosing depression (formed when this one overflows into its sibling), or -1.</summary>
        public int Parent;
        /// <summary>Sub-depressions (-1 for leaves).</summary>
        public int ChildA, ChildB;
        /// <summary>Number of enclosing depressions (0 = top-level).</summary>
        public int NestingLevel;
        /// <summary>Lowest terrain cell of the depression.</summary>
        public int BottomCell;
        public float Bottom;
        /// <summary>Level above which the depression holds a single water body (its children are
        /// joined); equals <see cref="Bottom"/> for leaves.</summary>
        public float MergeHeight;
        /// <summary>Spill elevation (+infinity when there is no outlet).</summary>
        public float Spill;
        /// <summary>Crest cell of the spill saddle (-1 without outlet).</summary>
        public int SpillCell;
        /// <summary>First cell beyond the saddle: start of the receiving drainage path (-1 without outlet).</summary>
        public int ReceivingCell;
        public DepressionOutlet Outlet;
        /// <summary>Sibling depression receiving the overflow (Outlet == Depression), else -1.</summary>
        public int SpillTarget;
        public double AreaAtSpill;
        public double VolumeAtSpill;
        public int CellsAtSpill;
        /// <summary>Topographic catchment: cells whose steepest descent on the real terrain ends in
        /// this depression (not the base D8 drainage, which runs on the filled surface).</summary>
        public double CatchmentArea;
        public float CatchmentMaxElevation;
        /// <summary>Area-weighted centroid of the footprint at spill (unit vector).</summary>
        public Vector3 Centroid;

        /// <summary>Potential water depth when filled to the spill elevation.</summary>
        public float MaxDepth => Spill - Bottom;

        public bool IsNested => Parent >= 0;

        public bool HasOutlet => Outlet != DepressionOutlet.None;
    }

    /// <summary>
    /// Depression hierarchy of a <see cref="HydrologyResult"/> (V0.1.1D-B.1), a separate
    /// deterministic analysis of the REAL terrain elevation. Read-only with respect to the
    /// terrain and the hydrology.
    /// </summary>
    /// <remarks>
    /// The Priority-Flood result only keeps the filled surface, which cannot tell nested
    /// depressions or shared saddles apart. This pass rebuilds that structure explicitly:
    /// 1. Watershed by flooding: every local minimum (a land cell without a strictly lower
    ///    neighbour) and the ocean seed a priority flood keyed by the minimax ("fill") level
    ///    max(elevation, parent level). Each cell gets the label of the minimum it is reached
    ///    from at the lowest possible water level, and that level (its flood key).
    /// 2. Passes: for every pair of adjacent labels, the lowest saddle max(key a, key b).
    /// 3. Kruskal merge in increasing pass elevation: a depression whose lowest pass leads to
    ///    the ocean set overflows to the ocean (top-level); two depressions meeting at a pass
    ///    both spill there and become the children of a new meta-depression. Zero-depth flat
    ///    minima are absorbed instead of creating empty nodes.
    /// Footprint of depression D at water level w = cells of D's labels with flood key &lt; w.
    /// This set is connected (every such cell reaches its minimum below w) and never includes
    /// pockets separated from D by terrain above w, nor cells of other depressions.
    /// Cost: O(n log n) for the flood, linear for the rest.
    /// </remarks>
    public sealed class DepressionHierarchy
    {
        /// <summary>Depressions shallower than this (fraction of the radius) are not candidates.</summary>
        public const float CandidateDepthTolerance = 1e-6f;

        internal int[] label;
        internal float[] key;
        internal int[] floodParent;
        internal int[] deepestNode;
        internal int[] labelOwner;
        internal int[] labelCellStart;
        internal int[] labelCells;
        internal int[] nodeLabelStart;
        internal int[] nodeLabelEnd;
        internal int[] labelOrder;
        internal DepressionCandidate[] nodes;
        internal bool[] absorbed;
        internal int oceanLabel = -1;

        internal DepressionHierarchy(HydrologyResult hydrology)
        {
            Hydrology = hydrology;
            HydrologyVersion = hydrology.Version;
        }

        public HydrologyResult Hydrology { get; }
        public int HydrologyVersion { get; }

        /// <summary>Number of hierarchy nodes (candidates are the non-trivial ones).</summary>
        public int NodeCount => nodes.Length;
        public int CandidateCount { get; internal set; }
        public int LocalMinimumCount { get; internal set; }
        public float Milliseconds { get; internal set; }

        public DepressionCandidate GetNode(int node) => nodes[node];

        /// <summary>A real closed depression: finite spill, positive depth, at least one cell.</summary>
        public bool IsCandidate(int node)
        {
            DepressionCandidate d = nodes[node];
            return !absorbed[node] && d.HasOutlet && d.MaxDepth > CandidateDepthTolerance && d.CellsAtSpill > 0;
        }

        /// <summary>Minimax water level at which the cell is first submerged from its minimum.</summary>
        public float GetFloodKey(int cell) => key[cell];

        /// <summary>Basin label of the cell (watershed of one local minimum or the ocean).</summary>
        public int GetLabel(int cell) => label[cell];

        public bool IsOceanLabel(int labelId) => labelId == oceanLabel;

        /// <summary>Cell from which the flood reached this cell (-1 for minima and ocean): following
        /// it never increases the flood key and leads to the cell's minimum.</summary>
        public int GetFloodParent(int cell) => floodParent[cell];

        /// <summary>Smallest depression whose footprint at spill contains the cell, or -1.</summary>
        public int GetDeepestNode(int cell) => deepestNode[cell];

        /// <summary>True if <paramref name="node"/> is <paramref name="ancestor"/> or nested inside it.</summary>
        public bool IsWithin(int node, int ancestor)
        {
            for (int current = node; current >= 0; current = nodes[current].Parent)
            {
                if (current == ancestor)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True if the cell lies in the watershed of the depression (any level).</summary>
        public bool IsInCatchment(int cell, int node)
        {
            int l = label[cell];
            if (l < 0 || l == oceanLabel)
            {
                return false;
            }
            int owner = labelOwner[l];
            return owner >= 0 && IsWithin(owner, node);
        }

        /// <summary>
        /// Appends the cells of the depression submerged at <paramref name="level"/> (flood key
        /// below the level), in a deterministic order. Only meaningful for level &lt;= Spill.
        /// </summary>
        public void CollectFootprint(int node, float level, List<int> cells)
        {
            for (int k = nodeLabelStart[node]; k < nodeLabelEnd[node]; k++)
            {
                int l = labelOrder[k];
                for (int i = labelCellStart[l]; i < labelCellStart[l + 1]; i++)
                {
                    int cell = labelCells[i];
                    if (key[cell] >= level)
                    {
                        break; // cells are sorted by flood key
                    }
                    cells.Add(cell);
                }
            }
        }

        /// <summary>Appends every cell of the depression's catchment (watershed).</summary>
        public void CollectCatchment(int node, List<int> cells)
        {
            for (int k = nodeLabelStart[node]; k < nodeLabelEnd[node]; k++)
            {
                int l = labelOrder[k];
                for (int i = labelCellStart[l]; i < labelCellStart[l + 1]; i++)
                {
                    cells.Add(labelCells[i]);
                }
            }
        }
    }

    /// <summary>Builds <see cref="DepressionHierarchy"/> instances (see its remarks).</summary>
    public sealed class DepressionAnalyzer
    {
        // Heap scratch (binary min-heap keyed by (level, push order): deterministic ties).
        int[] heapCells = Array.Empty<int>();
        float[] heapKeys = Array.Empty<float>();
        long[] heapOrder = Array.Empty<long>();
        int heapCount;
        long pushCounter;

        public DepressionHierarchy Analyze(HydrologyResult hydrology)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new DepressionHierarchy(hydrology);
            HydrologyGrid grid = hydrology.Grid;
            int count = grid.CellCount;
            float seaLevel = hydrology.SeaLevel;
            EnsureScratch(count);

            var label = new int[count];
            var key = new float[count];
            var floodParent = new int[count];
            for (int cell = 0; cell < count; cell++)
            {
                label[cell] = -1;
                floodParent[cell] = -1;
            }

            // 1. Seeds: the ocean (one label, water surface at sea level) and local minima.
            int labelCount = 0;
            var seedCells = new List<int>();
            heapCount = 0;
            pushCounter = 0;
            bool hasOcean = false;
            for (int cell = 0; cell < count; cell++)
            {
                if (!hydrology.IsLand(cell))
                {
                    hasOcean = true;
                    break;
                }
            }
            if (hasOcean)
            {
                result.oceanLabel = labelCount++;
                seedCells.Add(-1);
                for (int cell = 0; cell < count; cell++)
                {
                    if (hydrology.IsLand(cell))
                    {
                        continue;
                    }
                    label[cell] = result.oceanLabel;
                    key[cell] = seaLevel;
                    if (HasLandNeighbor(grid, hydrology, cell))
                    {
                        Push(cell, seaLevel);
                    }
                }
            }
            int minima = 0;
            for (int cell = 0; cell < count; cell++)
            {
                if (!hydrology.IsLand(cell) || !IsLocalMinimum(grid, hydrology, cell))
                {
                    continue;
                }
                label[cell] = labelCount++;
                seedCells.Add(cell);
                key[cell] = hydrology.GetElevation(cell);
                Push(cell, key[cell]);
                minima++;
            }
            result.LocalMinimumCount = minima;

            // 2. Watershed by flooding + lowest pass between every pair of adjacent labels.
            var passIndex = new Dictionary<long, int>();
            var passHeight = new List<float>();
            var passLabelLo = new List<int>();
            var passLabelHi = new List<int>();
            var passCellLo = new List<int>();
            var passCellHi = new List<int>();
            while (heapCount > 0)
            {
                int cell = Pop();
                int own = label[cell];
                float level = key[cell];
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                {
                    int other = grid.GetNeighbor(k);
                    int otherLabel = label[other];
                    if (otherLabel < 0)
                    {
                        label[other] = own;
                        key[other] = Mathf.Max(hydrology.GetElevation(other), level);
                        floodParent[other] = cell;
                        Push(other, key[other]);
                    }
                    else if (otherLabel != own)
                    {
                        float height = Mathf.Max(level, key[other]);
                        int lo = Math.Min(own, otherLabel), hi = Math.Max(own, otherLabel);
                        int cellLo = own == lo ? cell : other, cellHi = own == lo ? other : cell;
                        long pair = ((long)lo << 32) | (uint)hi;
                        if (passIndex.TryGetValue(pair, out int index))
                        {
                            if (height < passHeight[index]
                                || (height == passHeight[index] && (cellLo < passCellLo[index]
                                    || (cellLo == passCellLo[index] && cellHi < passCellHi[index]))))
                            {
                                passHeight[index] = height;
                                passCellLo[index] = cellLo;
                                passCellHi[index] = cellHi;
                            }
                        }
                        else
                        {
                            passIndex.Add(pair, passHeight.Count);
                            passHeight.Add(height);
                            passLabelLo.Add(lo);
                            passLabelHi.Add(hi);
                            passCellLo.Add(cellLo);
                            passCellHi.Add(cellHi);
                        }
                    }
                }
            }

            // 3. Kruskal merge. Leaves first (one per local minimum, in cell order).
            var nodes = new List<DepressionCandidate>(minima * 2);
            var absorbed = new List<bool>(minima * 2);
            var labelOwner = new int[labelCount];
            var labelNext = new int[labelCount];
            var ownHead = new List<int>(minima * 2);
            for (int l = 0; l < labelCount; l++)
            {
                labelOwner[l] = -1;
                labelNext[l] = -1;
            }
            for (int l = 0; l < labelCount; l++)
            {
                if (l == result.oceanLabel)
                {
                    continue;
                }
                int seed = seedCells[l];
                float bottom = hydrology.GetElevation(seed);
                labelOwner[l] = nodes.Count;
                ownHead.Add(l);
                absorbed.Add(false);
                nodes.Add(new DepressionCandidate
                {
                    Id = nodes.Count,
                    Parent = -1,
                    ChildA = -1,
                    ChildB = -1,
                    BottomCell = seed,
                    Bottom = bottom,
                    MergeHeight = bottom,
                    Spill = float.PositiveInfinity,
                    SpillCell = -1,
                    ReceivingCell = -1,
                    SpillTarget = -1,
                    Outlet = DepressionOutlet.None,
                });
            }

            var edges = new int[passHeight.Count];
            for (int e = 0; e < edges.Length; e++)
            {
                edges[e] = e;
            }
            Array.Sort(edges, (a, b) =>
            {
                int c = passHeight[a].CompareTo(passHeight[b]);
                if (c != 0)
                {
                    return c;
                }
                c = passLabelLo[a].CompareTo(passLabelLo[b]);
                return c != 0 ? c : passLabelHi[a].CompareTo(passLabelHi[b]);
            });

            var setParent = new int[labelCount];
            var setNode = new int[labelCount];
            var setOcean = new bool[labelCount];
            for (int l = 0; l < labelCount; l++)
            {
                setParent[l] = l;
                setNode[l] = labelOwner[l];
                setOcean[l] = l == result.oceanLabel;
            }

            foreach (int e in edges)
            {
                int a = Find(setParent, passLabelLo[e]);
                int b = Find(setParent, passLabelHi[e]);
                if (a == b)
                {
                    continue;
                }
                float h = passHeight[e];
                int cellA = passCellLo[e], cellB = passCellHi[e];
                if (setOcean[a] && setOcean[b])
                {
                    Union(setParent, a, b);
                    continue;
                }
                if (setOcean[a] || setOcean[b])
                {
                    // The land set overflows into ocean-draining terrain.
                    int landSet = setOcean[a] ? b : a;
                    int inside = setOcean[a] ? cellB : cellA, outside = setOcean[a] ? cellA : cellB;
                    int n = setNode[landSet];
                    SetSpill(nodes, n, h, inside, outside, key, DepressionOutlet.Ocean, -1);
                    int root = Union(setParent, a, b);
                    setOcean[root] = true;
                    continue;
                }

                int nodeA = setNode[a], nodeB = setNode[b];
                bool flatA = nodes[nodeA].Bottom >= h, flatB = nodes[nodeB].Bottom >= h;
                int merged;
                if (flatA || flatB)
                {
                    // A zero-depth (flat) minimum stores no water: absorb it instead of nesting.
                    int keep = flatA && flatB ? Math.Min(nodeA, nodeB) : flatA ? nodeB : nodeA;
                    int drop = keep == nodeA ? nodeB : nodeA;
                    for (int l = ownHead[drop]; l >= 0;)
                    {
                        int next = labelNext[l];
                        labelOwner[l] = keep;
                        labelNext[l] = ownHead[keep];
                        ownHead[keep] = l;
                        l = next;
                    }
                    ownHead[drop] = -1;
                    absorbed[drop] = true;
                    DepressionCandidate kept = nodes[keep];
                    DepressionCandidate dropped = nodes[drop];
                    if (dropped.Bottom < kept.Bottom || (dropped.Bottom == kept.Bottom && dropped.BottomCell < kept.BottomCell))
                    {
                        kept.Bottom = dropped.Bottom;
                        kept.BottomCell = dropped.BottomCell;
                    }
                    nodes[keep] = kept;
                    merged = keep;
                }
                else
                {
                    // Both are real depressions: they spill into each other at this saddle.
                    merged = nodes.Count;
                    SetSpill(nodes, nodeA, h, cellA, cellB, key, DepressionOutlet.Depression, nodeB);
                    SetSpill(nodes, nodeB, h, cellB, cellA, key, DepressionOutlet.Depression, nodeA);
                    DepressionCandidate childA = nodes[nodeA], childB = nodes[nodeB];
                    childA.Parent = merged;
                    childB.Parent = merged;
                    nodes[nodeA] = childA;
                    nodes[nodeB] = childB;
                    bool aLower = childA.Bottom < childB.Bottom || (childA.Bottom == childB.Bottom && childA.BottomCell < childB.BottomCell);
                    ownHead.Add(-1);
                    absorbed.Add(false);
                    nodes.Add(new DepressionCandidate
                    {
                        Id = merged,
                        Parent = -1,
                        ChildA = nodeA,
                        ChildB = nodeB,
                        BottomCell = aLower ? childA.BottomCell : childB.BottomCell,
                        Bottom = aLower ? childA.Bottom : childB.Bottom,
                        MergeHeight = h,
                        Spill = float.PositiveInfinity,
                        SpillCell = -1,
                        ReceivingCell = -1,
                        SpillTarget = -1,
                        Outlet = DepressionOutlet.None,
                    });
                }
                int rootSet = Union(setParent, a, b);
                setNode[rootSet] = merged;
            }

            int nodeCount = nodes.Count;
            DepressionCandidate[] nodeArray = nodes.ToArray();
            bool[] absorbedArray = absorbed.ToArray();

            // Nesting levels (parents always have larger ids than their children).
            for (int n = nodeCount - 1; n >= 0; n--)
            {
                int parent = nodeArray[n].Parent;
                nodeArray[n].NestingLevel = parent >= 0 ? nodeArray[parent].NestingLevel + 1 : 0;
            }

            // Label ranges in depth-first order: each node owns a contiguous range of labels.
            var labelOrder = new int[labelCount];
            var nodeLabelStart = new int[nodeCount];
            var nodeLabelEnd = new int[nodeCount];
            int cursor = 0;
            var stack = new Stack<int>();
            for (int root = 0; root < nodeCount; root++)
            {
                if (absorbedArray[root] || nodeArray[root].Parent >= 0)
                {
                    continue;
                }
                // Iterative post-order: ranges close once both children are written.
                stack.Push(root);
                while (stack.Count > 0)
                {
                    int n = stack.Pop();
                    if (n < 0)
                    {
                        n = ~n;
                        nodeLabelEnd[n] = cursor;
                        continue;
                    }
                    nodeLabelStart[n] = cursor;
                    for (int l = ownHead[n]; l >= 0; l = labelNext[l])
                    {
                        labelOrder[cursor++] = l;
                    }
                    stack.Push(~n);
                    if (nodeArray[n].ChildB >= 0)
                    {
                        stack.Push(nodeArray[n].ChildB);
                    }
                    if (nodeArray[n].ChildA >= 0)
                    {
                        stack.Push(nodeArray[n].ChildA);
                    }
                }
            }

            // Cells of every label sorted by (flood key, cell id).
            var labelCellStart = new int[labelCount + 1];
            for (int cell = 0; cell < count; cell++)
            {
                if (label[cell] >= 0 && label[cell] != result.oceanLabel)
                {
                    labelCellStart[label[cell] + 1]++;
                }
            }
            for (int l = 0; l < labelCount; l++)
            {
                labelCellStart[l + 1] += labelCellStart[l];
            }
            int landCells = labelCellStart[labelCount];
            var labelCells = new int[landCells];
            var sortKeys = new long[landCells];
            var fill = new int[labelCount];
            for (int cell = 0; cell < count; cell++)
            {
                int l = label[cell];
                if (l < 0 || l == result.oceanLabel)
                {
                    continue;
                }
                int index = labelCellStart[l] + fill[l]++;
                labelCells[index] = cell;
                // Top bit flipped: the unsigned float order becomes the signed long order.
                sortKeys[index] = ((long)(OrderedBits(key[cell]) ^ 0x80000000u) << 32) | (uint)cell;
            }
            for (int l = 0; l < labelCount; l++)
            {
                int start = labelCellStart[l];
                int length = labelCellStart[l + 1] - start;
                if (length > 1)
                {
                    Array.Sort(sortKeys, labelCells, start, length);
                }
            }

            // Topographic catchments: steepest descent on the real terrain (distance weighted) to a
            // local minimum or the ocean. Flood labels cannot be used above the rims: there the
            // minimax level of a slope cell is its own elevation from every side.
            int[] descent = DescentLabels(grid, hydrology, label);

            // 4. Metrics at spill. A cell is submerged in the lowest node of its label's chain whose
            // spill exceeds its flood key, and in all of that node's ancestors.
            var deepest = new int[count];
            var area = new double[nodeCount];
            var areaElevation = new double[nodeCount];
            var cellCount = new int[nodeCount];
            var centroid = new Vector3[nodeCount];
            var catchment = new double[nodeCount];
            var catchmentMax = new float[nodeCount];
            for (int n = 0; n < nodeCount; n++)
            {
                catchmentMax[n] = float.NegativeInfinity;
            }
            for (int cell = 0; cell < count; cell++)
            {
                deepest[cell] = -1;
            }
            for (int l = 0; l < labelCount; l++)
            {
                if (l == result.oceanLabel)
                {
                    continue;
                }
                int owner = labelOwner[l];
                int current = owner;
                for (int i = labelCellStart[l]; i < labelCellStart[l + 1]; i++)
                {
                    int cell = labelCells[i];
                    float cellArea = grid.GetArea(cell);
                    float elevation = hydrology.GetElevation(cell);
                    while (current >= 0 && nodeArray[current].Spill <= key[cell])
                    {
                        current = nodeArray[current].Parent;
                    }
                    if (current < 0)
                    {
                        continue;
                    }
                    deepest[cell] = current;
                    area[current] += cellArea;
                    areaElevation[current] += (double)cellArea * elevation;
                    cellCount[current]++;
                    centroid[current] += grid.GetCenter(cell) * cellArea;
                }
            }
            for (int cell = 0; cell < count; cell++)
            {
                int l = descent[cell];
                if (l >= 0 && l != result.oceanLabel)
                {
                    int owner = labelOwner[l];
                    catchment[owner] += grid.GetArea(cell);
                    catchmentMax[owner] = Mathf.Max(catchmentMax[owner], hydrology.GetElevation(cell));
                }
            }
            // Children have lower ids than their parent: one ascending pass accumulates subtrees.
            int candidates = 0;
            for (int n = 0; n < nodeCount; n++)
            {
                if (absorbedArray[n])
                {
                    continue;
                }
                DepressionCandidate d = nodeArray[n];
                d.AreaAtSpill = area[n];
                d.CellsAtSpill = cellCount[n];
                d.VolumeAtSpill = d.HasOutlet ? Math.Max(0.0, d.Spill * area[n] - areaElevation[n]) : double.PositiveInfinity;
                d.Centroid = centroid[n].sqrMagnitude > 0f ? centroid[n].normalized : Vector3.zero;
                d.CatchmentArea = catchment[n];
                d.CatchmentMaxElevation = catchmentMax[n];
                nodeArray[n] = d;
                int parent = d.Parent;
                if (parent >= 0)
                {
                    area[parent] += area[n];
                    areaElevation[parent] += areaElevation[n];
                    cellCount[parent] += cellCount[n];
                    centroid[parent] += centroid[n];
                    catchment[parent] += catchment[n];
                    catchmentMax[parent] = Mathf.Max(catchmentMax[parent], catchmentMax[n]);
                }
            }

            result.label = label;
            result.key = key;
            result.floodParent = floodParent;
            result.deepestNode = deepest;
            result.labelOwner = labelOwner;
            result.labelCellStart = labelCellStart;
            result.labelCells = labelCells;
            result.nodeLabelStart = nodeLabelStart;
            result.nodeLabelEnd = nodeLabelEnd;
            result.labelOrder = labelOrder;
            result.nodes = nodeArray;
            result.absorbed = absorbedArray;
            for (int n = 0; n < nodeCount; n++)
            {
                candidates += result.IsCandidate(n) ? 1 : 0;
            }
            result.CandidateCount = candidates;
            result.Milliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// Label reached by steepest descent on the real terrain from every land cell (memoized,
        /// linear): a local minimum's label, or the ocean label when the descent reaches the sea.
        /// </summary>
        static int[] DescentLabels(HydrologyGrid grid, HydrologyResult hydrology, int[] label)
        {
            int count = grid.CellCount;
            var descent = new int[count];
            for (int cell = 0; cell < count; cell++)
            {
                descent[cell] = hydrology.IsLand(cell) ? -1 : label[cell];
            }
            var path = new List<int>();
            for (int start = 0; start < count; start++)
            {
                path.Clear();
                int cell = start;
                while (descent[cell] < 0)
                {
                    path.Add(cell);
                    float elevation = hydrology.GetElevation(cell);
                    int best = -1;
                    float bestSlope = 0f;
                    for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                    {
                        int other = grid.GetNeighbor(k);
                        float height = hydrology.IsLand(other) ? hydrology.GetElevation(other) : hydrology.SeaLevel;
                        float slope = (elevation - height) / grid.GetNeighborDistance(k);
                        if (slope > bestSlope)
                        {
                            bestSlope = slope;
                            best = other;
                        }
                    }
                    if (best < 0)
                    {
                        descent[cell] = label[cell]; // local minimum (its own seed label)
                        break;
                    }
                    cell = best;
                }
                int result = descent[cell];
                foreach (int visited in path)
                {
                    descent[visited] = result;
                }
            }
            return descent;
        }

        static void SetSpill(List<DepressionCandidate> nodes, int node, float height, int inside, int outside, float[] key,
            DepressionOutlet outlet, int target)
        {
            DepressionCandidate d = nodes[node];
            d.Spill = height;
            // The crest is the higher cell of the saddle pair (ties: lower id, so that two depressions
            // sharing a saddle report the same crest); the receiving path starts beyond it.
            d.SpillCell = key[outside] > key[inside] || (key[outside] == key[inside] && outside < inside) ? outside : inside;
            d.ReceivingCell = outside;
            d.Outlet = outlet;
            d.SpillTarget = target;
            nodes[node] = d;
        }

        static bool IsLocalMinimum(HydrologyGrid grid, HydrologyResult hydrology, int cell)
        {
            float elevation = hydrology.GetElevation(cell);
            float sea = hydrology.SeaLevel;
            for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
            {
                int other = grid.GetNeighbor(k);
                // Ocean neighbours count at the water surface (always below land).
                float height = hydrology.IsLand(other) ? hydrology.GetElevation(other) : sea;
                if (height < elevation)
                {
                    return false;
                }
            }
            return true;
        }

        static bool HasLandNeighbor(HydrologyGrid grid, HydrologyResult hydrology, int cell)
        {
            for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
            {
                if (hydrology.IsLand(grid.GetNeighbor(k)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Order-preserving unsigned encoding of a float (for deterministic sorting).</summary>
        static uint OrderedBits(float value)
        {
            uint bits = (uint)BitConverter.SingleToInt32Bits(value);
            return (bits & 0x80000000u) != 0 ? ~bits : bits | 0x80000000u;
        }

        static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        static int Union(int[] parent, int a, int b)
        {
            // Deterministic: the smaller root survives.
            int root = Math.Min(a, b);
            parent[Math.Max(a, b)] = root;
            return root;
        }

        void EnsureScratch(int count)
        {
            if (heapCells.Length >= count)
            {
                return;
            }
            heapCells = new int[count];
            heapKeys = new float[count];
            heapOrder = new long[count];
        }

        void Push(int cell, float level)
        {
            int index = heapCount++;
            long sequence = pushCounter++;
            while (index > 0)
            {
                int parent = (index - 1) >> 1;
                if (!Less(level, sequence, heapKeys[parent], heapOrder[parent]))
                {
                    break;
                }
                heapCells[index] = heapCells[parent];
                heapKeys[index] = heapKeys[parent];
                heapOrder[index] = heapOrder[parent];
                index = parent;
            }
            heapCells[index] = cell;
            heapKeys[index] = level;
            heapOrder[index] = sequence;
        }

        int Pop()
        {
            int top = heapCells[0];
            heapCount--;
            if (heapCount > 0)
            {
                int cell = heapCells[heapCount];
                float level = heapKeys[heapCount];
                long sequence = heapOrder[heapCount];
                int index = 0;
                while (true)
                {
                    int child = 2 * index + 1;
                    if (child >= heapCount)
                    {
                        break;
                    }
                    if (child + 1 < heapCount && Less(heapKeys[child + 1], heapOrder[child + 1], heapKeys[child], heapOrder[child]))
                    {
                        child++;
                    }
                    if (!Less(heapKeys[child], heapOrder[child], level, sequence))
                    {
                        break;
                    }
                    heapCells[index] = heapCells[child];
                    heapKeys[index] = heapKeys[child];
                    heapOrder[index] = heapOrder[child];
                    index = child;
                }
                heapCells[index] = cell;
                heapKeys[index] = level;
                heapOrder[index] = sequence;
            }
            return top;
        }

        static bool Less(float keyA, long orderA, float keyB, long orderB)
        {
            return keyA < keyB || (keyA == keyB && orderA < orderB);
        }
    }
}
