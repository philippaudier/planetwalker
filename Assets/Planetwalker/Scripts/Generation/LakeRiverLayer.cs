using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>Lake-aware state of a river node.</summary>
    public enum RiverLakeState : byte
    {
        /// <summary>Dry channel carrying enough lake-aware flow: drawn.</summary>
        Visible,
        /// <summary>Under the water of a lake.</summary>
        InLake,
        /// <summary>On the dry floor of a closed basin: its water ends in the lake.</summary>
        InClosedBasin,
        /// <summary>Base channel whose supply is captured upstream by a closed lake (below the threshold).</summary>
        Inactive,
    }

    public enum ReachStart : byte
    {
        /// <summary>River source of the base network.</summary>
        Source,
        /// <summary>Outflow of an open lake.</summary>
        LakeOutlet,
        /// <summary>Continuation of a visible tributary below a channel captured upstream.</summary>
        Confluence,
        /// <summary>The lake-aware drained area reaches the threshold again (a new source).</summary>
        Reactivated,
    }

    public enum ReachEnd : byte
    {
        /// <summary>End of the base river: junction, ocean mouth or sink.</summary>
        RiverEnd,
        /// <summary>Enters a lake (directly or across a closed basin floor).</summary>
        LakeInflow,
    }

    /// <summary>A visible, continuous portion of a river in the lake-aware surface-water network.</summary>
    public struct RiverReach
    {
        public int River;
        /// <summary>Range of node indices along the river (0 = source), inclusive.</summary>
        public int FirstNode, LastNode;
        public ReachStart Start;
        public ReachEnd End;
        /// <summary>Lake the reach flows out of (LakeOutlet), else -1.</summary>
        public int StartLake;
        /// <summary>Lake the reach flows into (LakeInflow), else -1.</summary>
        public int EndLake;
        /// <summary>Cell where the reach leaves the base river path to enter the lake (LakeInflow), else -1.</summary>
        public int EntryCell;
        /// <summary>Closed basins: path from the entry cell down to the shoreline,
        /// [ConnectorStart, ConnectorStart + ConnectorCount) of the connector cells.</summary>
        public int ConnectorStart, ConnectorCount;
    }

    public enum LakeInteractionKind : byte
    {
        Inflow,
        Outflow,
    }

    /// <summary>Role of a cell in the lake-aware surface water (higher values take priority).</summary>
    public enum SurfaceWaterRole : byte
    {
        None,
        /// <summary>Base river channel whose supply is captured by a closed lake upstream (not drawn).</summary>
        CapturedChannel,
        /// <summary>Visible river cell.</summary>
        River,
        /// <summary>Visible river cell of a reach leaving an open lake.</summary>
        OutflowRiver,
        /// <summary>Path across a closed basin floor down to the shoreline.</summary>
        Connector,
        /// <summary>Cell where a river enters a lake or its closed basin.</summary>
        Inflow,
        /// <summary>First dry cell of an open lake's outflow.</summary>
        Outlet,
    }

    /// <summary>A river meeting a lake (explicit topology, not proximity).</summary>
    public struct LakeInteraction
    {
        public int Lake;
        public int River;
        public int Reach;
        public LakeInteractionKind Kind;
        /// <summary>Inflow: entry cell (lake or basin floor). Outflow: first dry cell.</summary>
        public int Cell;
    }

    /// <summary>
    /// Lake-aware surface-water connectivity (V0.1.1D-B.1), between the base D8 drainage
    /// (<see cref="HydrologyResult"/>, never modified) and the visible river geometry.
    /// </summary>
    /// <remarks>
    /// Every river node of the base <see cref="RiverNetwork"/> gets a state: under a lake, on a
    /// closed basin floor, inactive (its lake-aware drained area, <see cref="LakeSet.GetSurfaceAccumulation"/>,
    /// fell below the river threshold because a closed lake captured its supply) or visible.
    /// Visible runs become reaches with an explicit start (source, open-lake outlet, confluence,
    /// reactivation) and end (base river end or lake inflow). Rivers reaching the dry floor of a
    /// closed basin continue along a steepest-descent connector down to the shoreline, so they end
    /// at the water instead of crossing the floor along the filled-surface D8 path. No confluence
    /// is invented and the base graph is untouched.
    /// </remarks>
    public sealed class LakeRiverLayer
    {
        internal RiverLakeState[] nodeState;
        internal int[] nodeLake;
        internal double[] nodeDeficit;
        internal RiverReach[] reaches = Array.Empty<RiverReach>();
        internal int[] riverReachStart;
        internal int[] connectorCells = Array.Empty<int>();
        internal LakeInteraction[] interactions = Array.Empty<LakeInteraction>();
        internal int[] lakeOutflowReach;
        internal int[] lakeInflowCount;
        SurfaceWaterRole[] cellRoles;

        internal LakeRiverLayer(RiverNetwork network, LakeSet lakes)
        {
            Network = network;
            Lakes = lakes;
        }

        public RiverNetwork Network { get; }
        public LakeSet Lakes { get; }

        public int ReachCount => reaches.Length;
        public int InteractionCount => interactions.Length;
        public int HiddenNodeCount { get; internal set; }
        public int InactiveNodeCount { get; internal set; }
        /// <summary>Visible reaches that leave a closed lake (must be 0).</summary>
        public int ClosedLakeOutflows { get; internal set; }
        /// <summary>Open-lake outflows leaving elsewhere than the lake outlet (D8 ties on the shore).</summary>
        public int SecondaryOutflows { get; internal set; }
        /// <summary>Closed-basin inflows whose connector could not reach the shoreline.</summary>
        public int ConnectorFailures { get; internal set; }
        public float Milliseconds { get; internal set; }

        public RiverLakeState GetNodeState(int node) => nodeState[node];

        /// <summary>Lake of a node under water / on a closed basin floor, else -1.</summary>
        public int GetNodeLake(int node) => nodeLake[node];

        /// <summary>Base minus lake-aware drained area at a node (steradians, &gt;= 0).</summary>
        public double GetNodeDeficit(int node) => nodeDeficit[node];

        public RiverReach GetReach(int reach) => reaches[reach];

        /// <summary>Reaches of a river are [GetRiverReachStart(r), GetRiverReachStart(r + 1)).</summary>
        public int GetRiverReachStart(int river) => riverReachStart[river];

        public int GetConnectorCell(int reach, int index) => connectorCells[reaches[reach].ConnectorStart + index];

        public LakeInteraction GetInteraction(int index) => interactions[index];

        /// <summary>Reach flowing out of an open lake, or -1 (closed lake, or outflow below the river threshold).</summary>
        public int GetLakeOutflowReach(int lake) => lakeOutflowReach[lake];

        public int GetLakeInflowCount(int lake) => lakeInflowCount[lake];

        /// <summary>Role of a cell in the lake-aware surface water (computed once, on first use).</summary>
        public SurfaceWaterRole GetCellRole(int cell)
        {
            if (cellRoles == null)
            {
                BuildCellRoles();
            }
            return cellRoles[cell];
        }

        void BuildCellRoles()
        {
            var roles = new SurfaceWaterRole[Lakes.Hydrology.Grid.CellCount];
            void Mark(int cell, SurfaceWaterRole role)
            {
                if (cell >= 0 && role > roles[cell])
                {
                    roles[cell] = role;
                }
            }
            for (int node = 0; node < Network.NodeCount; node++)
            {
                if (nodeState[node] == RiverLakeState.Inactive)
                {
                    Mark(Network.GetNodeCell(node), SurfaceWaterRole.CapturedChannel);
                }
            }
            for (int r = 0; r < reaches.Length; r++)
            {
                RiverReach reach = reaches[r];
                SurfaceWaterRole role = reach.Start == ReachStart.LakeOutlet ? SurfaceWaterRole.OutflowRiver : SurfaceWaterRole.River;
                for (int i = reach.FirstNode; i <= reach.LastNode; i++)
                {
                    Mark(Network.GetNodeCell(Network.GetRiverNode(reach.River, i)), role);
                }
                for (int c = 0; c < reach.ConnectorCount; c++)
                {
                    Mark(connectorCells[reach.ConnectorStart + c], SurfaceWaterRole.Connector);
                }
                Mark(reach.EntryCell, SurfaceWaterRole.Inflow);
            }
            for (int lake = 0; lake < Lakes.Count; lake++)
            {
                Mark(Lakes.GetLake(lake).OutletCell, SurfaceWaterRole.Outlet);
            }
            cellRoles = roles;
        }
    }

    /// <summary>Builds <see cref="LakeRiverLayer"/> instances (deterministic, linear in the network size).</summary>
    public static class LakeRiverIntegration
    {
        const int MaxConnectorSteps = 4096;

        public static LakeRiverLayer Build(RiverNetwork network, LakeSet lakes)
        {
            var stopwatch = Stopwatch.StartNew();
            if (lakes.Hydrology != network.Hydrology)
            {
                throw new ArgumentException("River network and lakes must come from the same hydrology analysis.");
            }
            var layer = new LakeRiverLayer(network, lakes);
            HydrologyResult hydrology = network.Hydrology;
            HydrologyGrid grid = hydrology.Grid;
            double threshold = network.Threshold * grid.TotalArea;
            int nodeCount = network.NodeCount;

            var state = new RiverLakeState[nodeCount];
            var nodeLake = new int[nodeCount];
            var deficit = new double[nodeCount];
            int hidden = 0, inactive = 0;
            for (int node = 0; node < nodeCount; node++)
            {
                int cell = network.GetNodeCell(node);
                double surface = lakes.GetSurfaceAccumulation(cell);
                deficit[node] = Math.Max(0.0, hydrology.GetAccumulation(cell) - surface);
                nodeLake[node] = lakes.GetCaptureLakeOfCell(cell);
                if (lakes.GetLakeOfCell(cell) >= 0)
                {
                    state[node] = RiverLakeState.InLake;
                    hidden++;
                }
                else if (lakes.GetBasinLakeOfCell(cell) >= 0)
                {
                    state[node] = RiverLakeState.InClosedBasin;
                    hidden++;
                }
                else if (surface < threshold)
                {
                    state[node] = RiverLakeState.Inactive;
                    inactive++;
                }
                else
                {
                    state[node] = RiverLakeState.Visible;
                }
            }
            layer.nodeState = state;
            layer.nodeLake = nodeLake;
            layer.nodeDeficit = deficit;
            layer.HiddenNodeCount = hidden;
            layer.InactiveNodeCount = inactive;

            // Rivers whose end (junction) is visible: they continue a captured parent channel.
            var visibleEnd = new bool[network.RiverCount];
            for (int river = 0; river < network.RiverCount; river++)
            {
                River data = network.GetRiver(river);
                int last = network.GetRiverNode(river, data.NodeCount - 1);
                visibleEnd[river] = state[last] == RiverLakeState.Visible && (data.JunctionCell < 0 || lakes.GetCaptureLakeOfCell(data.JunctionCell) < 0);
            }
            var tributaryAt = new HashSet<int>();
            for (int river = 0; river < network.RiverCount; river++)
            {
                River data = network.GetRiver(river);
                if (data.JunctionCell >= 0 && visibleEnd[river])
                {
                    tributaryAt.Add(data.JunctionCell);
                }
            }

            var reaches = new List<RiverReach>();
            var connectors = new List<int>();
            var interactions = new List<LakeInteraction>();
            var riverReachStart = new int[network.RiverCount + 1];
            var outflowReach = new int[lakes.Count];
            var inflowCount = new int[lakes.Count];
            for (int i = 0; i < lakes.Count; i++)
            {
                outflowReach[i] = -1;
            }
            var path = new List<int>();

            for (int river = 0; river < network.RiverCount; river++)
            {
                riverReachStart[river] = reaches.Count;
                River data = network.GetRiver(river);
                int count = data.NodeCount;
                int i = 0;
                while (i < count)
                {
                    int node = network.GetRiverNode(river, i);
                    if (state[node] != RiverLakeState.Visible)
                    {
                        i++;
                        continue;
                    }
                    var reach = new RiverReach
                    {
                        River = river,
                        FirstNode = i,
                        StartLake = -1,
                        EndLake = -1,
                        EntryCell = -1,
                    };
                    if (i == 0)
                    {
                        reach.Start = ReachStart.Source;
                    }
                    else
                    {
                        int previous = network.GetRiverNode(river, i - 1);
                        int previousLake = nodeLake[previous];
                        if (state[previous] == RiverLakeState.InLake && lakes.GetLake(previousLake).Class == LakeClass.Open)
                        {
                            reach.Start = ReachStart.LakeOutlet;
                            reach.StartLake = previousLake;
                        }
                        else
                        {
                            reach.Start = tributaryAt.Contains(network.GetNodeCell(node)) ? ReachStart.Confluence : ReachStart.Reactivated;
                        }
                    }

                    if (reach.Start == ReachStart.Reactivated)
                    {
                        // A new source must not appear on the rim of a closed basin, where it would
                        // read as an outflow: it starts once clear of the basin.
                        while (i < count && state[network.GetRiverNode(river, i)] == RiverLakeState.Visible
                            && TouchesClosedBasin(grid, lakes, network.GetNodeCell(network.GetRiverNode(river, i)))
                            && !tributaryAt.Contains(network.GetNodeCell(network.GetRiverNode(river, i))))
                        {
                            i++;
                        }
                        if (i >= count || state[network.GetRiverNode(river, i)] != RiverLakeState.Visible)
                        {
                            continue;
                        }
                        reach.FirstNode = i;
                    }

                    int j = i;
                    while (j + 1 < count && state[network.GetRiverNode(river, j + 1)] == RiverLakeState.Visible)
                    {
                        j++;
                    }
                    reach.LastNode = j;
                    int entry = -1;
                    if (j + 1 < count)
                    {
                        entry = network.GetNodeCell(network.GetRiverNode(river, j + 1));
                    }
                    else if (data.JunctionCell >= 0 && lakes.GetCaptureLakeOfCell(data.JunctionCell) >= 0)
                    {
                        entry = data.JunctionCell;
                    }
                    if (entry >= 0)
                    {
                        int lake = lakes.GetCaptureLakeOfCell(entry);
                        reach.End = ReachEnd.LakeInflow;
                        reach.EndLake = lake;
                        reach.EntryCell = entry;
                        if (lakes.GetLakeOfCell(entry) < 0)
                        {
                            reach.ConnectorStart = connectors.Count;
                            if (BuildConnector(hydrology, lakes, lake, entry, path))
                            {
                                connectors.AddRange(path);
                                reach.ConnectorCount = path.Count;
                            }
                            else
                            {
                                layer.ConnectorFailures++;
                            }
                        }
                        inflowCount[lake]++;
                        interactions.Add(new LakeInteraction { Lake = lake, River = river, Reach = reaches.Count, Kind = LakeInteractionKind.Inflow, Cell = entry });
                    }
                    else
                    {
                        reach.End = ReachEnd.RiverEnd;
                    }
                    if (reach.Start == ReachStart.LakeOutlet)
                    {
                        int firstCell = network.GetNodeCell(network.GetRiverNode(river, reach.FirstNode));
                        Lake lake = lakes.GetLake(reach.StartLake);
                        if (firstCell == lake.OutletCell)
                        {
                            outflowReach[reach.StartLake] = reaches.Count;
                        }
                        else
                        {
                            layer.SecondaryOutflows++;
                        }
                        interactions.Add(new LakeInteraction { Lake = reach.StartLake, River = river, Reach = reaches.Count, Kind = LakeInteractionKind.Outflow, Cell = firstCell });
                    }
                    reaches.Add(reach);
                    i = j + 1;
                }
            }
            riverReachStart[network.RiverCount] = reaches.Count;

            foreach (RiverReach reach in reaches)
            {
                if (reach.Start == ReachStart.LakeOutlet && lakes.GetLake(reach.StartLake).Class == LakeClass.Closed)
                {
                    layer.ClosedLakeOutflows++;
                }
            }

            layer.reaches = reaches.ToArray();
            layer.riverReachStart = riverReachStart;
            layer.connectorCells = connectors.ToArray();
            layer.interactions = interactions.ToArray();
            layer.lakeOutflowReach = outflowReach;
            layer.lakeInflowCount = inflowCount;
            layer.Milliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
            return layer;
        }

        static bool TouchesClosedBasin(HydrologyGrid grid, LakeSet lakes, int cell)
        {
            for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
            {
                int lake = lakes.GetCaptureLakeOfCell(grid.GetNeighbor(k));
                if (lake >= 0 && lakes.GetLake(lake).Class == LakeClass.Closed)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Path from a closed-basin floor cell down to the lake: steepest descent on the REAL
        /// terrain (it cannot leave the basin below its spill), then, if it stops in a dry flat,
        /// the flood-parent chain (non-increasing flood key, ends at a minimum under the lake).
        /// Cells after <paramref name="start"/>, the last one being a lake cell.
        /// </summary>
        static bool BuildConnector(HydrologyResult hydrology, LakeSet lakes, int lake, int start, List<int> path)
        {
            HydrologyGrid grid = hydrology.Grid;
            DepressionHierarchy depressions = lakes.Depressions;
            path.Clear();
            int current = start;
            for (int step = 0; step < MaxConnectorSteps; step++)
            {
                if (lakes.GetLakeOfCell(current) == lake)
                {
                    return true;
                }
                int best = -1;
                float bestSlope = 0f;
                float elevation = hydrology.GetElevation(current);
                for (int k = grid.GetNeighborStart(current); k < grid.GetNeighborEnd(current); k++)
                {
                    int other = grid.GetNeighbor(k);
                    if (lakes.GetCaptureLakeOfCell(other) != lake)
                    {
                        continue;
                    }
                    float slope = (elevation - hydrology.GetElevation(other)) / grid.GetNeighborDistance(k);
                    if (slope > bestSlope)
                    {
                        bestSlope = slope;
                        best = other;
                    }
                }
                if (best < 0)
                {
                    best = depressions.GetFloodParent(current);
                    if (best < 0 || lakes.GetCaptureLakeOfCell(best) != lake)
                    {
                        return false;
                    }
                }
                path.Add(best);
                current = best;
            }
            return false;
        }
    }
}
