using System;
using System.Collections.Generic;
using System.Diagnostics;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// Hydrology foundation (V0.1.1C): drainage analysis of an existing elevation field on a
    /// <see cref="HydrologyGrid"/>. Read-only with respect to the terrain.
    /// </summary>
    /// <remarks>
    /// Pipeline:
    /// 1. Sample the real elevation at every cell center.
    /// 2. Priority-Flood with epsilon (Barnes et al. 2014) seeded from coastal ocean cells
    ///    builds a depression-free "hydrological" surface (double precision) in which every
    ///    land cell has a strictly lower neighbour. Closed depressions and flats are raised
    ///    just enough to drain toward their spill point. The real elevation is kept separately.
    /// 3. Flow direction: steepest descent (drop / distance) on the corrected surface. Since
    ///    receivers are strictly lower, cycles are impossible and every path ends at an outlet.
    /// 4. Flow accumulation: each cell contributes its spherical area, propagated downstream
    ///    in reverse flood order (a valid topological order), so nothing is counted twice.
    ///    This is a drained AREA, not a physical discharge (no rain, evaporation, infiltration).
    /// 5. Basins: land cells grouped by the outlet they drain to.
    ///
    /// A planet without ocean drains to its lowest land cell (an endorheic outlet).
    /// </remarks>
    public sealed class HydrologySystem
    {
        /// <summary>Minimum rise per cell on the corrected surface (fraction of the radius).</summary>
        public const double FloodEpsilon = 1e-9;

        /// <summary>A land cell raised by more than this belongs to a filled depression (flats
        /// resolved by epsilon steps stay below it for chains of up to ~1000 cells).</summary>
        public const double DepressionTolerance = 1e-6;

        const int BasinPaletteSize = 10;

        static int versionCounter;

        // Scratch buffers reused across computations.
        int[] heapCells = Array.Empty<int>();
        double[] heapKeys = Array.Empty<double>();
        long[] heapOrder = Array.Empty<long>();
        int heapCount;
        long pushCounter;
        bool[] closed = Array.Empty<bool>();
        int[] stack = Array.Empty<int>();
        int[] terminalBasin = Array.Empty<int>();

        /// <summary>Hydrology of a generated planet (elevation from its height generator).</summary>
        public HydrologyResult Compute(HydrologyGrid grid, PlanetInstance planet, HydrologyResult reuse = null)
        {
            return Compute(grid, planet.Height.GetElevation, planet.SeaLevel, reuse);
        }

        /// <summary>
        /// Hydrology of an arbitrary elevation field (also used by synthetic tests).
        /// <paramref name="reuse"/> may be a previous result on the same grid; its buffers are
        /// then overwritten instead of reallocated.
        /// </summary>
        public HydrologyResult Compute(HydrologyGrid grid, Func<Vector3, float> elevationFunction, float seaLevel,
            HydrologyResult reuse = null)
        {
            var stopwatch = Stopwatch.StartNew();
            int count = grid.CellCount;
            HydrologyResult result = reuse != null && reuse.Grid == grid ? reuse : new HydrologyResult(grid);
            result.SeaLevel = seaLevel;
            EnsureScratch(count);

            float[] elevation = result.elevation;
            double[] filled = result.filled;
            int[] receiver = result.receiver;
            bool[] land = result.land;
            int[] order = result.order;

            // 1. Real elevation.
            int landCount = 0;
            for (int cell = 0; cell < count; cell++)
            {
                elevation[cell] = elevationFunction(grid.GetCenter(cell));
                land[cell] = elevation[cell] > seaLevel;
                receiver[cell] = -1;
                closed[cell] = false;
                if (land[cell])
                {
                    landCount++;
                }
            }

            // 2. Priority-Flood seeded from ocean cells that touch land (water surface level).
            heapCount = 0;
            pushCounter = 0;
            for (int cell = 0; cell < count; cell++)
            {
                if (land[cell])
                {
                    continue;
                }
                closed[cell] = true;
                filled[cell] = seaLevel;
                if (HasLandNeighbor(grid, land, cell))
                {
                    Push(cell, seaLevel);
                }
            }
            result.EndorheicOutlet = -1;
            if (heapCount == 0 && landCount > 0)
            {
                int lowest = -1;
                for (int cell = 0; cell < count; cell++)
                {
                    if (land[cell] && (lowest < 0 || elevation[cell] < elevation[lowest]))
                    {
                        lowest = cell;
                    }
                }
                closed[lowest] = true;
                filled[lowest] = elevation[lowest];
                result.EndorheicOutlet = lowest;
                Push(lowest, filled[lowest]);
            }

            int orderCount = 0;
            while (heapCount > 0)
            {
                int cell = Pop();
                order[orderCount++] = cell;
                double next = filled[cell] + FloodEpsilon;
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                {
                    int other = grid.GetNeighbor(k);
                    if (closed[other])
                    {
                        continue;
                    }
                    closed[other] = true;
                    filled[other] = Math.Max(elevation[other], next);
                    Push(other, filled[other]);
                }
            }
            result.OrderCount = orderCount;

            // 3. Steepest descent on the corrected surface (ties: lowest neighbour id).
            for (int cell = 0; cell < count; cell++)
            {
                if (!land[cell] || cell == result.EndorheicOutlet)
                {
                    continue;
                }
                int best = -1;
                double bestSlope = 0;
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                {
                    int other = grid.GetNeighbor(k);
                    double drop = filled[cell] - filled[other];
                    if (drop > 0)
                    {
                        double slope = drop / grid.GetNeighborDistance(k);
                        if (slope > bestSlope)
                        {
                            bestSlope = slope;
                            best = other;
                        }
                    }
                }
                receiver[cell] = best;
            }

            // 4. Accumulation in reverse flood order (upstream cells first).
            double[] accumulation = result.accumulation;
            for (int cell = 0; cell < count; cell++)
            {
                accumulation[cell] = grid.GetArea(cell);
            }
            double maxAccumulation = 0;
            for (int k = orderCount - 1; k >= 0; k--)
            {
                int cell = order[k];
                if (land[cell] && receiver[cell] >= 0)
                {
                    accumulation[receiver[cell]] += accumulation[cell];
                }
            }

            // 5. Basins, downstream first: a land cell joins the basin of its receiver.
            int[] basin = result.basin;
            int basinCount = 0;
            for (int cell = 0; cell < count; cell++)
            {
                basin[cell] = -1;
                terminalBasin[cell] = -1;
            }
            for (int k = 0; k < orderCount; k++)
            {
                int cell = order[k];
                if (!land[cell])
                {
                    continue;
                }
                maxAccumulation = Math.Max(maxAccumulation, accumulation[cell]);
                int down = receiver[cell];
                if (down < 0)
                {
                    basin[cell] = basinCount++;
                }
                else if (land[down])
                {
                    basin[cell] = basin[down];
                }
                else
                {
                    if (terminalBasin[down] < 0)
                    {
                        terminalBasin[down] = basinCount++;
                    }
                    basin[cell] = terminalBasin[down];
                }
            }

            // Statistics.
            var basinAreas = new double[basinCount];
            double landArea = 0;
            for (int cell = 0; cell < count; cell++)
            {
                if (land[cell])
                {
                    basinAreas[basin[cell]] += grid.GetArea(cell);
                    landArea += grid.GetArea(cell);
                }
            }
            double largest = 0;
            for (int b = 0; b < basinCount; b++)
            {
                largest = Math.Max(largest, basinAreas[b]);
            }

            result.BasinCount = basinCount;
            result.basinAreas = basinAreas;
            result.basinColors = ColorBasins(grid, result);
            result.LandCellCount = landCount;
            result.LandArea = landArea;
            result.LargestBasinArea = largest;
            result.MeanBasinArea = basinCount > 0 ? landArea / basinCount : 0;
            result.MaxAccumulation = maxAccumulation;
            result.DepressionCount = CountDepressions(grid, result);
            stopwatch.Stop();
            result.ComputeMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
            result.Version = ++versionCounter;
            return result;
        }

        static bool HasLandNeighbor(HydrologyGrid grid, bool[] land, int cell)
        {
            for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
            {
                if (land[grid.GetNeighbor(k)])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Connected regions of land cells raised by the flood (closed depressions).</summary>
        int CountDepressions(HydrologyGrid grid, HydrologyResult result)
        {
            int count = grid.CellCount;
            for (int cell = 0; cell < count; cell++)
            {
                closed[cell] = false;
            }
            int depressions = 0;
            for (int cell = 0; cell < count; cell++)
            {
                if (closed[cell] || !result.IsInDepression(cell))
                {
                    continue;
                }
                depressions++;
                int top = 0;
                stack[top++] = cell;
                closed[cell] = true;
                while (top > 0)
                {
                    int current = stack[--top];
                    for (int k = grid.GetNeighborStart(current); k < grid.GetNeighborEnd(current); k++)
                    {
                        int other = grid.GetNeighbor(k);
                        if (!closed[other] && result.IsInDepression(other))
                        {
                            closed[other] = true;
                            stack[top++] = other;
                        }
                    }
                }
            }
            return depressions;
        }

        /// <summary>
        /// Greedy coloring of the basin adjacency graph so that neighbouring basins never
        /// share a palette color (unless a basin touches more basins than the palette holds).
        /// </summary>
        static int[] ColorBasins(HydrologyGrid grid, HydrologyResult result)
        {
            int basinCount = result.BasinCount;
            var colors = new int[basinCount];
            if (basinCount == 0)
            {
                return colors;
            }
            var edges = new HashSet<long>();
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                int a = result.basin[cell];
                if (a < 0)
                {
                    continue;
                }
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                {
                    int b = result.basin[grid.GetNeighbor(k)];
                    if (b >= 0 && b != a)
                    {
                        edges.Add(((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b));
                    }
                }
            }
            var adjacency = new List<int>[basinCount];
            foreach (long edge in edges)
            {
                int a = (int)(edge >> 32);
                int b = (int)(edge & 0xffffffff);
                (adjacency[a] ??= new List<int>()).Add(b);
                (adjacency[b] ??= new List<int>()).Add(a);
            }
            var used = new bool[BasinPaletteSize];
            for (int b = 0; b < basinCount; b++)
            {
                colors[b] = -1;
            }
            for (int b = 0; b < basinCount; b++)
            {
                Array.Clear(used, 0, used.Length);
                if (adjacency[b] != null)
                {
                    foreach (int other in adjacency[b])
                    {
                        if (colors[other] >= 0)
                        {
                            used[colors[other]] = true;
                        }
                    }
                }
                // Start the search at a hashed offset so palettes look varied, not banded.
                int start = (int)((uint)PlanetNoise.DeriveSeed(b, 1) % BasinPaletteSize);
                colors[b] = start;
                for (int k = 0; k < BasinPaletteSize; k++)
                {
                    int candidate = (start + k) % BasinPaletteSize;
                    if (!used[candidate])
                    {
                        colors[b] = candidate;
                        break;
                    }
                }
            }
            return colors;
        }

        void EnsureScratch(int count)
        {
            if (closed.Length >= count)
            {
                return;
            }
            heapCells = new int[count];
            heapKeys = new double[count];
            heapOrder = new long[count];
            closed = new bool[count];
            stack = new int[count];
            terminalBasin = new int[count];
        }

        // Binary min-heap keyed by (filled elevation, push order): deterministic ties.

        void Push(int cell, double key)
        {
            int index = heapCount++;
            long sequence = pushCounter++;
            while (index > 0)
            {
                int parent = (index - 1) >> 1;
                if (!Less(key, sequence, heapKeys[parent], heapOrder[parent]))
                {
                    break;
                }
                heapCells[index] = heapCells[parent];
                heapKeys[index] = heapKeys[parent];
                heapOrder[index] = heapOrder[parent];
                index = parent;
            }
            heapCells[index] = cell;
            heapKeys[index] = key;
            heapOrder[index] = sequence;
        }

        int Pop()
        {
            int top = heapCells[0];
            heapCount--;
            if (heapCount > 0)
            {
                int cell = heapCells[heapCount];
                double key = heapKeys[heapCount];
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
                    if (!Less(heapKeys[child], heapOrder[child], key, sequence))
                    {
                        break;
                    }
                    heapCells[index] = heapCells[child];
                    heapKeys[index] = heapKeys[child];
                    heapOrder[index] = heapOrder[child];
                    index = child;
                }
                heapCells[index] = cell;
                heapKeys[index] = key;
                heapOrder[index] = sequence;
            }
            return top;
        }

        static bool Less(double keyA, long orderA, double keyB, long orderB)
        {
            return keyA < keyB || (keyA == keyB && orderA < orderB);
        }
    }

    /// <summary>
    /// Result of a hydrology analysis. Arrays are indexed by <see cref="HydrologyGrid"/> cell id.
    /// Areas are in steradians (fractions of the planet surface: divide by 4 pi).
    /// </summary>
    public sealed class HydrologyResult
    {
        internal readonly float[] elevation;
        internal readonly double[] filled;
        internal readonly int[] receiver;
        internal readonly bool[] land;
        internal readonly double[] accumulation;
        internal readonly int[] basin;
        internal readonly int[] order;
        internal double[] basinAreas = Array.Empty<double>();
        internal int[] basinColors = Array.Empty<int>();

        internal HydrologyResult(HydrologyGrid grid)
        {
            Grid = grid;
            int count = grid.CellCount;
            elevation = new float[count];
            filled = new double[count];
            receiver = new int[count];
            land = new bool[count];
            accumulation = new double[count];
            basin = new int[count];
            order = new int[count];
        }

        public HydrologyGrid Grid { get; }
        public float SeaLevel { get; internal set; }
        public int BasinCount { get; internal set; }
        public int LandCellCount { get; internal set; }
        public int DepressionCount { get; internal set; }
        /// <summary>Land cell used as outlet when the planet has no ocean, else -1.</summary>
        public int EndorheicOutlet { get; internal set; }
        public int OrderCount { get; internal set; }
        public double LandArea { get; internal set; }
        public double LargestBasinArea { get; internal set; }
        public double MeanBasinArea { get; internal set; }
        /// <summary>Largest drained area of a land cell, in steradians.</summary>
        public double MaxAccumulation { get; internal set; }
        public float ComputeMilliseconds { get; internal set; }

        /// <summary>
        /// Changes every time this result is (re)computed, including when its buffers are
        /// reused. Lets derived caches (river network) detect a new analysis.
        /// </summary>
        public int Version { get; internal set; }

        /// <summary>Real terrain elevation at the cell center (never modified).</summary>
        public float GetElevation(int cell) => elevation[cell];

        /// <summary>Depression-free hydrological elevation (>= real elevation on land).</summary>
        public double GetFilledElevation(int cell) => filled[cell];

        public bool IsLand(int cell) => land[cell];

        /// <summary>Downstream cell, or -1 for outlets (ocean cells, endorheic outlet).</summary>
        public int GetReceiver(int cell) => receiver[cell];

        /// <summary>Drained (contributing) area in steradians, including the cell itself.</summary>
        public double GetAccumulation(int cell) => accumulation[cell];

        /// <summary>Drained area as a fraction of the planet surface.</summary>
        public double GetAccumulationFraction(int cell) => accumulation[cell] / Grid.TotalArea;

        /// <summary>Basin id of a land cell, -1 for ocean cells.</summary>
        public int GetBasin(int cell) => basin[cell];

        public double GetBasinArea(int basinId) => basinAreas[basinId];

        /// <summary>Palette index of a basin; neighbouring basins get different indices.</summary>
        public int GetBasinColorIndex(int basinId) => basinColors[basinId];

        /// <summary>Cell order of the flood (non-decreasing corrected elevation).</summary>
        public int GetOrderedCell(int index) => order[index];

        public bool IsInDepression(int cell)
        {
            return land[cell] && filled[cell] - elevation[cell] > HydrologySystem.DepressionTolerance;
        }

        /// <summary>Land cell whose drained area reaches the threshold (fraction of surface).</summary>
        public bool IsRiverCandidate(int cell, float threshold)
        {
            return land[cell] && accumulation[cell] >= threshold * Grid.TotalArea;
        }
    }

    /// <summary>
    /// Caches the hydrology of the current planet. Recomputes only when a terrain-affecting
    /// parameter (<see cref="PlanetDefinition.TerrainEquals"/>) or the grid resolution
    /// changes; display parameters such as the river threshold never trigger a recompute.
    /// </summary>
    public sealed class HydrologyCache
    {
        readonly Dictionary<int, HydrologyGrid> grids = new Dictionary<int, HydrologyGrid>();
        readonly Dictionary<int, HydrologyResult> buffers = new Dictionary<int, HydrologyResult>();
        readonly HydrologySystem system = new HydrologySystem();
        PlanetDefinition cachedTerrain;
        int cachedResolution;
        HydrologyResult cached;

        /// <summary>Number of hydrology computations performed (for tests and diagnostics).</summary>
        public int ComputeCount { get; private set; }

        public HydrologyResult Current => cached;

        public HydrologyResult Get(PlanetInstance planet, int resolution)
        {
            if (cached != null && cachedResolution == resolution && cachedTerrain.TerrainEquals(planet.Definition))
            {
                return cached;
            }
            if (!grids.TryGetValue(resolution, out HydrologyGrid grid))
            {
                grid = new HydrologyGrid(resolution);
                grids.Add(resolution, grid);
            }
            buffers.TryGetValue(resolution, out HydrologyResult reuse);
            cached = system.Compute(grid, planet, reuse);
            buffers[resolution] = cached;
            cachedTerrain = planet.Definition.Clone();
            cachedResolution = resolution;
            ComputeCount++;
            return cached;
        }

        /// <summary>Releases grids and buffers (they are plain managed arrays).</summary>
        public void Clear()
        {
            grids.Clear();
            buffers.Clear();
            cached = null;
            cachedTerrain = null;
        }
    }
}
