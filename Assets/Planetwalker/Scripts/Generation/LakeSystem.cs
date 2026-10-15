using System;
using System.Collections.Generic;
using System.Diagnostics;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>Hydrological class of a lake.</summary>
    public enum LakeClass : byte
    {
        /// <summary>Filled to its spill elevation; water leaves through a surface outlet.</summary>
        Open,
        /// <summary>No active surface outlet: provisional level strictly below the spill elevation.</summary>
        Closed,
    }

    /// <summary>Selection outcome of a depression candidate (debug views and statistics).</summary>
    public enum LakeCandidateStatus : byte
    {
        NotCandidate,
        Accepted,
        /// <summary>Storage volume below the abundance threshold.</summary>
        BelowThreshold,
        /// <summary>Smaller than the minimum area / cell count, or too shallow.</summary>
        TooSmall,
        /// <summary>Would exceed the maximum lake area or flood most of its own catchment.</summary>
        TooLarge,
        /// <summary>Encloses or is enclosed by an accepted lake (same physical space).</summary>
        Nested,
        /// <summary>Would touch or nearly touch another accepted lake.</summary>
        TooClose,
    }

    /// <summary>
    /// An accepted inland lake: a geographic entity with a stable key, a footprint on the
    /// hydrology grid, a flat water level and its relationship to the drainage network.
    /// Elevations are fractions of the radius; areas in steradians.
    /// </summary>
    public struct Lake
    {
        /// <summary>Index in the lake set (lakes are sorted by <see cref="Key"/>).</summary>
        public int Id;
        /// <summary>Stable identifier: the lowest cell of the lake basin (independent of iteration
        /// order; unchanged while the same basin stays selected).</summary>
        public int Key;
        /// <summary>Depression candidate the lake fills.</summary>
        public int Candidate;
        public LakeClass Class;
        /// <summary>Water surface elevation.</summary>
        public float Level;
        public float Bottom;
        /// <summary>Spill elevation of the containing depression (Level == Spill for open lakes).</summary>
        public float Spill;
        public double Area;
        public double Volume;
        public int CellCount;
        public Vector3 Centroid;
        /// <summary>Topographic catchment of the basin.</summary>
        public double CatchmentArea;
        /// <summary>Lake-aware drained area reaching the lake (inflow for closed lakes, outflow for open ones).</summary>
        public double SupplyArea;
        /// <summary>Open lakes: first dry cell of the outflow (on the base drainage), else -1.</summary>
        public int OutletCell;
        /// <summary>Open lakes: lake cell the outflow leaves from, else -1.</summary>
        public int OutletLakeCell;
        /// <summary>Crest of the spill saddle and first cell of the receiving drainage path.</summary>
        public int SpillCell, ReceivingCell;
        /// <summary>Lake cells are [FootprintStart, FootprintStart + CellCount) of the footprint list.</summary>
        public int FootprintStart;
        /// <summary>Closed lakes: dry floor of the basin below the spill elevation (rivers reaching it
        /// end in the lake), [BasinStart, BasinStart + BasinCount) of the basin list.</summary>
        public int BasinStart, BasinCount;
        /// <summary>Small lake accepted through the mountain-lake quota.</summary>
        public bool IsMountainLake;

        public float MaxDepth => Level - Bottom;
    }

    /// <summary>Planet-dependent inputs of the selection (seed variation, terrain context).</summary>
    public struct LakeContext
    {
        public int Seed;
        /// <summary>Optional mountain influence in [0, 1] at a direction (small mountain lakes).</summary>
        public Func<Vector3, float> MountainInfluence;

        public LakeContext(int seed, Func<Vector3, float> mountainInfluence = null)
        {
            Seed = seed;
            MountainInfluence = mountainInfluence;
        }
    }

    /// <summary>
    /// Selected lakes of a planet (V0.1.1D-B.1). Pure data: footprints, levels, classes,
    /// outlets and the lake-aware surface-water accumulation. Never modifies the terrain, the
    /// hydrology (base D8 drainage) or the depression analysis.
    /// </summary>
    public sealed class LakeSet
    {
        internal Lake[] lakes = Array.Empty<Lake>();
        internal int[] footprintCells = Array.Empty<int>();
        internal int[] basinCells = Array.Empty<int>();
        internal int[] lakeOfCell;
        internal int[] basinLakeOfCell;
        internal double[] surfaceAccumulation;
        internal LakeCandidateStatus[] candidateStatus;

        internal LakeSet(DepressionHierarchy depressions, LakeSettings settings, int seed)
        {
            Depressions = depressions;
            Hydrology = depressions.Hydrology;
            Settings = settings.Clone();
            Seed = seed;
        }

        public DepressionHierarchy Depressions { get; }
        public HydrologyResult Hydrology { get; }
        /// <summary>Copy of the selection settings used.</summary>
        public LakeSettings Settings { get; }
        public int Seed { get; }

        public int Count => lakes.Length;
        public int OpenCount { get; internal set; }
        public int ClosedCount { get; internal set; }
        public int MountainLakeCount { get; internal set; }
        public int CandidateCount => Depressions.CandidateCount;
        public double TotalArea { get; internal set; }
        /// <summary>Id of the largest lake, or -1.</summary>
        public int LargestLake { get; internal set; }
        /// <summary>Budget ratio K: a depression overflows when catchment &gt;= K * lake area (seeded aridity).</summary>
        public float OverflowRatio { get; internal set; }
        public float SelectionMilliseconds { get; internal set; }

        public Lake GetLake(int id) => lakes[id];

        /// <summary>Lake whose WATER covers the cell, or -1.</summary>
        public int GetLakeOfCell(int cell) => lakeOfCell[cell];

        /// <summary>Closed lake whose dry basin floor (below the spill) contains the cell, or -1.</summary>
        public int GetBasinLakeOfCell(int cell) => basinLakeOfCell[cell];

        /// <summary>Lake whose water or closed basin floor contains the cell (surface water ends there), or -1.</summary>
        public int GetCaptureLakeOfCell(int cell) => lakeOfCell[cell] >= 0 ? lakeOfCell[cell] : basinLakeOfCell[cell];

        public int GetFootprintCell(int lake, int index) => footprintCells[lakes[lake].FootprintStart + index];

        public int GetBasinCell(int lake, int index) => basinCells[lakes[lake].BasinStart + index];

        /// <summary>Water depth at a lake cell (level minus terrain), 0 outside lakes.</summary>
        public float GetDepth(int cell)
        {
            int lake = lakeOfCell[cell];
            return lake >= 0 ? lakes[lake].Level - Hydrology.GetElevation(cell) : 0f;
        }

        /// <summary>
        /// Lake-aware drained area (steradians): the base D8 accumulation where water reaching a
        /// closed lake (or its dry basin floor) stops there instead of continuing downstream.
        /// </summary>
        public double GetSurfaceAccumulation(int cell) => surfaceAccumulation[cell];

        public LakeCandidateStatus GetCandidateStatus(int node) => candidateStatus[node];
    }

    /// <summary>
    /// Deterministic lake selection and classification from a <see cref="DepressionHierarchy"/>.
    /// </summary>
    /// <remarks>
    /// Selection (geography first, seed second):
    /// - Eligible candidates: at least <see cref="MinLakeCells"/> cells and the minimum area at
    ///   spill, depth &gt;= <see cref="MinDepth"/>.
    /// - Significance = storage volume at spill, times a seeded factor in [0.7, 1.4]. Candidates
    ///   are visited by decreasing significance; the abundance slider sets the volume threshold
    ///   (10^-6 .. 10^-8 of the surface x radius). Small mountain lakes below the threshold get a
    ///   separate, small quota (mountain influence &gt;= 0.35, at least 5% of the threshold).
    /// - Greedy acceptance: no accepted lake may enclose / be enclosed by another (antichain of the
    ///   hierarchy), lakes stay at least 3 cells apart, a lake may not exceed
    ///   <see cref="MaxLakeAreaFraction"/> of the planet or <see cref="MaxCatchmentShare"/> of its
    ///   catchment (no flooded continental interiors). A rejected large basin lets its
    ///   sub-depressions compete.
    /// Classification (provisional water budget, no climate): with K a seeded planet aridity in
    /// [<see cref="MinOverflowRatio"/>, <see cref="MaxOverflowRatio"/>], a depression overflows
    /// (OPEN, level = spill) when its water supply (catchment, or the base D8 flow through it when
    /// upstream basins overflow into it) is at least K times its lake surface at spill AND
    /// its base D8 outflow actually leaves through the spill saddle (always true for top-level
    /// depressions; nested ones whose D8 path crosses another part of the rim stay closed).
    /// Otherwise it is CLOSED at bottom + phi (spill - bottom), phi = filling factor x
    /// (0.5 + 0.5 min(1, supply ratio / K)) clamped to [0.1, 0.95], strictly below the spill. If that
    /// level is below the merge height of its sub-pockets, the lake is the connected pocket holding
    /// the deepest point (the sub-depression on the path to the bottom), so it is always one water body.
    /// This is NOT a hydrological equilibrium; it is a documented, deterministic approximation.
    /// </remarks>
    public sealed class LakeSystem
    {
        public const int MinLakeCells = 3;
        public const float MinDepth = 2e-4f;
        public const float MaxLakeAreaFraction = 3e-3f;
        public const float MaxCatchmentShare = 0.6f;
        public const float MinOverflowRatio = 2f;
        public const float MaxOverflowRatio = 5.5f;
        public const float MountainInfluenceThreshold = 0.35f;
        public const float MinClosedFill = 0.1f;
        public const float MaxClosedFill = 0.95f;
        /// <summary>Lakes are separated by at least this many dry cells (no merged shorelines).</summary>
        public const int SeparationRings = 2;

        /// <summary>Volume threshold (fraction of the surface x radius) for an abundance in [0, 1].</summary>
        public static double VolumeThreshold(float abundance)
        {
            return Math.Pow(10.0, Mathf.Lerp(-6f, -8f, Mathf.Clamp01(abundance)));
        }

        public static float OverflowRatio(int seed)
        {
            return Mathf.Lerp(MinOverflowRatio, MaxOverflowRatio, PlanetNoise.Hash01(seed, 7101));
        }

        public LakeSet Select(DepressionHierarchy depressions, LakeSettings settings, LakeContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            settings.Sanitize();
            var set = new LakeSet(depressions, settings, context.Seed);
            HydrologyResult hydrology = depressions.Hydrology;
            HydrologyGrid grid = hydrology.Grid;
            int cellCount = grid.CellCount;
            int nodeCount = depressions.NodeCount;
            double total = grid.TotalArea;
            float overflowRatio = OverflowRatio(context.Seed);
            set.OverflowRatio = overflowRatio;

            var status = new LakeCandidateStatus[nodeCount];
            var lakeOfCell = new int[cellCount];
            var basinOfCell = new int[cellCount];
            var halo = new int[cellCount];
            for (int cell = 0; cell < cellCount; cell++)
            {
                lakeOfCell[cell] = -1;
                basinOfCell[cell] = -1;
                halo[cell] = -1;
            }

            // Eligible candidates and their significance.
            double minArea = settings.minimumArea * total;
            double threshold = VolumeThreshold(settings.abundance) * total;
            var order = new List<int>();
            var significance = new double[nodeCount];
            for (int n = 0; n < nodeCount; n++)
            {
                if (!depressions.IsCandidate(n))
                {
                    continue;
                }
                DepressionCandidate d = depressions.GetNode(n);
                if (d.CellsAtSpill < MinLakeCells || d.AreaAtSpill < minArea || d.MaxDepth < MinDepth)
                {
                    status[n] = LakeCandidateStatus.TooSmall;
                    continue;
                }
                // Seeded variation keyed by the basin's own lowest cell (not by iteration order).
                float jitter = Mathf.Lerp(0.7f, 1.4f, PlanetNoise.Hash01(context.Seed, d.BottomCell));
                significance[n] = d.VolumeAtSpill * jitter;
                status[n] = LakeCandidateStatus.BelowThreshold;
                order.Add(n);
            }
            order.Sort((a, b) =>
            {
                int c = significance[b].CompareTo(significance[a]);
                return c != 0 ? c : a.CompareTo(b);
            });

            var accepted = new List<PendingLake>();
            var hasAcceptedDescendant = new bool[nodeCount];
            var isAccepted = new bool[nodeCount];

            // Pass 1: significant basins.
            foreach (int n in order)
            {
                if (significance[n] < threshold)
                {
                    break;
                }
                TryAccept(n, false);
            }

            // Pass 2: a few small mountain lakes (terrain context), never more than the quota.
            if (context.MountainInfluence != null)
            {
                int quota = Mathf.RoundToInt(Mathf.Lerp(1f, 6f, settings.abundance));
                int mountainLakes = 0;
                foreach (int n in order)
                {
                    if (mountainLakes >= quota || significance[n] < 0.05 * threshold)
                    {
                        break;
                    }
                    if (significance[n] >= threshold || status[n] != LakeCandidateStatus.BelowThreshold)
                    {
                        continue;
                    }
                    DepressionCandidate d = depressions.GetNode(n);
                    if (d.MaxDepth < 2f * MinDepth || context.MountainInfluence(d.Centroid) < MountainInfluenceThreshold)
                    {
                        continue;
                    }
                    if (TryAccept(n, true))
                    {
                        mountainLakes++;
                    }
                }
            }

            // Final ids: sorted by the stable key (lowest basin cell).
            accepted.Sort((a, b) => a.Lake.Key.CompareTo(b.Lake.Key));
            var remap = new int[accepted.Count];
            for (int i = 0; i < accepted.Count; i++)
            {
                remap[accepted[i].TemporaryId] = i;
            }
            var lakes = new Lake[accepted.Count];
            var footprints = new List<int>();
            var basins = new List<int>();
            double totalArea = 0;
            int largest = -1;
            for (int i = 0; i < accepted.Count; i++)
            {
                Lake lake = accepted[i].Lake;
                lake.Id = i;
                lake.FootprintStart = footprints.Count;
                footprints.AddRange(accepted[i].Footprint);
                lake.BasinStart = basins.Count;
                lake.BasinCount = accepted[i].Basin.Count;
                basins.AddRange(accepted[i].Basin);
                lakes[i] = lake;
                totalArea += lake.Area;
                if (largest < 0 || lake.Area > lakes[largest].Area)
                {
                    largest = i;
                }
                set.OpenCount += lake.Class == LakeClass.Open ? 1 : 0;
                set.ClosedCount += lake.Class == LakeClass.Closed ? 1 : 0;
                set.MountainLakeCount += lake.IsMountainLake ? 1 : 0;
            }
            for (int cell = 0; cell < cellCount; cell++)
            {
                if (lakeOfCell[cell] >= 0)
                {
                    lakeOfCell[cell] = remap[lakeOfCell[cell]];
                }
                if (basinOfCell[cell] >= 0)
                {
                    basinOfCell[cell] = remap[basinOfCell[cell]];
                }
            }

            set.lakes = lakes;
            set.footprintCells = footprints.ToArray();
            set.basinCells = basins.ToArray();
            set.lakeOfCell = lakeOfCell;
            set.basinLakeOfCell = basinOfCell;
            set.candidateStatus = status;
            set.TotalArea = totalArea;
            set.LargestLake = largest;
            ComputeSurfaceAccumulation(set);
            set.SelectionMilliseconds = (float)stopwatch.Elapsed.TotalMilliseconds;
            return set;

            bool TryAccept(int n, bool mountainLake)
            {
                if (isAccepted[n])
                {
                    return false; // already holds the water of an enclosing candidate
                }
                // Same physical space: an accepted ancestor or descendant blocks this candidate.
                if (hasAcceptedDescendant[n])
                {
                    status[n] = LakeCandidateStatus.Nested;
                    return false;
                }
                for (int p = depressions.GetNode(n).Parent; p >= 0; p = depressions.GetNode(p).Parent)
                {
                    if (isAccepted[p])
                    {
                        status[n] = LakeCandidateStatus.Nested;
                        return false;
                    }
                }

                var pending = Evaluate(depressions, n, settings, overflowRatio, minArea, out LakeCandidateStatus failure);
                if (pending == null)
                {
                    status[n] = failure;
                    return false;
                }
                foreach (int cell in pending.Footprint)
                {
                    if (halo[cell] >= 0)
                    {
                        status[n] = LakeCandidateStatus.TooClose;
                        return false;
                    }
                }

                pending.TemporaryId = accepted.Count;
                pending.Lake.IsMountainLake = mountainLake;
                accepted.Add(pending);
                // The water may sit in a sub-pocket of the candidate (closed lakes below the merge level).
                int holder = pending.Lake.Candidate;
                isAccepted[holder] = true;
                status[holder] = LakeCandidateStatus.Accepted;
                if (holder != n)
                {
                    status[n] = LakeCandidateStatus.Nested;
                }
                for (int p = depressions.GetNode(holder).Parent; p >= 0; p = depressions.GetNode(p).Parent)
                {
                    hasAcceptedDescendant[p] = true;
                }
                foreach (int cell in pending.Footprint)
                {
                    lakeOfCell[cell] = pending.TemporaryId;
                }
                foreach (int cell in pending.Basin)
                {
                    basinOfCell[cell] = pending.TemporaryId;
                }
                MarkHalo(grid, pending.Footprint, halo, pending.TemporaryId);
                return true;
            }
        }

        sealed class PendingLake
        {
            public Lake Lake;
            public List<int> Footprint;
            public List<int> Basin;
            public int TemporaryId;
        }

        /// <summary>Classification, level and footprint of one candidate; null if it cannot be a lake.</summary>
        PendingLake Evaluate(DepressionHierarchy depressions, int n, LakeSettings settings, float overflowRatio,
            double minArea, out LakeCandidateStatus failure)
        {
            HydrologyResult hydrology = depressions.Hydrology;
            HydrologyGrid grid = hydrology.Grid;
            DepressionCandidate d = depressions.GetNode(n);
            failure = LakeCandidateStatus.NotCandidate;

            var atSpill = new List<int>();
            depressions.CollectFootprint(n, d.Spill, atSpill);

            // Water supply: the topographic catchment, or more when upstream basins overflow into
            // this one (base D8 flow leaving the footprint).
            FindMainExit(hydrology, atSpill, out int exitLakeCell, out int exitCell);
            double supply = Math.Max(d.CatchmentArea, exitLakeCell >= 0 ? hydrology.GetAccumulation(exitLakeCell) : 0.0);
            double supplyRatio = supply / Math.Max(d.AreaAtSpill, 1e-30);

            // Open only if the budget allows overflow AND the base drainage leaves through the saddle.
            bool outletOnSaddle = exitCell >= 0 && IsAtSpill(hydrology, d, exitCell);
            bool open = supplyRatio >= overflowRatio && outletOnSaddle;

            float level;
            List<int> footprint;
            int holder = n;
            if (open)
            {
                level = d.Spill;
                footprint = atSpill;
            }
            else
            {
                float wetness = Mathf.Min(1f, (float)(supplyRatio / overflowRatio));
                float phi = Mathf.Clamp(settings.fillingFactor * (0.5f + 0.5f * wetness), MinClosedFill, MaxClosedFill);
                level = d.Bottom + phi * (d.Spill - d.Bottom);
                // Below its merge height a depression holds separate pockets: the lake is the connected
                // pocket containing the deepest point (deterministic; the other pockets stay dry and
                // remain candidates of their own).
                while (depressions.GetNode(holder).ChildA >= 0 && level <= depressions.GetNode(holder).MergeHeight)
                {
                    holder = LowerChild(depressions, holder);
                }
                DepressionCandidate pocket = depressions.GetNode(holder);
                if (!(level < pocket.Spill))
                {
                    level = pocket.Spill - Mathf.Max(1e-7f, 1e-3f * pocket.MaxDepth);
                }
                if (!(level > pocket.Bottom) || !(level < pocket.Spill))
                {
                    failure = LakeCandidateStatus.TooSmall; // no representable level (float resolution)
                    return null;
                }
                footprint = new List<int>();
                depressions.CollectFootprint(holder, level, footprint);
                if (holder != n)
                {
                    atSpill.Clear();
                    depressions.CollectFootprint(holder, pocket.Spill, atSpill);
                }
                d = pocket;
            }

            double area = 0, volume = 0;
            Vector3 centroid = Vector3.zero;
            foreach (int cell in footprint)
            {
                float cellArea = grid.GetArea(cell);
                area += cellArea;
                volume += cellArea * (double)(level - hydrology.GetElevation(cell));
                centroid += grid.GetCenter(cell) * cellArea;
            }
            if (footprint.Count < MinLakeCells || area < minArea || level - d.Bottom < MinDepth)
            {
                failure = LakeCandidateStatus.TooSmall;
                return null;
            }
            if (area > MaxLakeAreaFraction * grid.TotalArea || area > MaxCatchmentShare * d.CatchmentArea)
            {
                failure = LakeCandidateStatus.TooLarge;
                return null;
            }

            var basin = new List<int>();
            if (!open)
            {
                // Dry floor between the closed level and the spill: rivers reaching it end in the lake.
                var inLake = new HashSet<int>(footprint);
                foreach (int cell in atSpill)
                {
                    if (!inLake.Contains(cell))
                    {
                        basin.Add(cell);
                    }
                }
            }

            return new PendingLake
            {
                Footprint = footprint,
                Basin = basin,
                Lake = new Lake
                {
                    Key = d.BottomCell,
                    Candidate = holder,
                    Class = open ? LakeClass.Open : LakeClass.Closed,
                    Level = level,
                    Bottom = d.Bottom,
                    Spill = d.Spill,
                    Area = area,
                    Volume = volume,
                    CellCount = footprint.Count,
                    Centroid = centroid.sqrMagnitude > 0f ? centroid.normalized : d.Centroid,
                    CatchmentArea = d.CatchmentArea,
                    OutletCell = open ? exitCell : -1,
                    OutletLakeCell = open ? exitLakeCell : -1,
                    SpillCell = d.SpillCell,
                    ReceivingCell = d.ReceivingCell,
                },
            };
        }

        /// <summary>Child holding the deepest point (ties: lowest cell id).</summary>
        static int LowerChild(DepressionHierarchy depressions, int node)
        {
            DepressionCandidate d = depressions.GetNode(node);
            DepressionCandidate a = depressions.GetNode(d.ChildA), b = depressions.GetNode(d.ChildB);
            return a.Bottom < b.Bottom || (a.Bottom == b.Bottom && a.BottomCell < b.BottomCell) ? d.ChildA : d.ChildB;
        }

        /// <summary>
        /// Main exit of the base D8 drainage from a footprint: the exit edge carrying the largest
        /// drained area (ties: lowest cell id). -1 if no flow leaves.
        /// </summary>
        static void FindMainExit(HydrologyResult hydrology, List<int> footprint, out int lakeCell, out int exitCell)
        {
            lakeCell = -1;
            exitCell = -1;
            double best = -1;
            var members = new HashSet<int>(footprint);
            foreach (int cell in footprint)
            {
                int receiver = hydrology.GetReceiver(cell);
                if (receiver < 0 || members.Contains(receiver))
                {
                    continue;
                }
                double flow = hydrology.GetAccumulation(cell);
                if (flow > best || (flow == best && cell < lakeCell))
                {
                    best = flow;
                    lakeCell = cell;
                    exitCell = receiver;
                }
            }
        }

        /// <summary>True when the exit cell lies on the spill saddle (the rim is at the lake level there).</summary>
        static bool IsAtSpill(HydrologyResult hydrology, DepressionCandidate d, int exitCell)
        {
            float tolerance = Mathf.Max(1e-6f, 0.02f * d.MaxDepth);
            return exitCell == d.SpillCell || exitCell == d.ReceivingCell
                || (hydrology.IsLand(exitCell) && hydrology.GetElevation(exitCell) <= d.Spill + tolerance);
        }

        static void MarkHalo(HydrologyGrid grid, List<int> footprint, int[] halo, int id)
        {
            // Footprint plus SeparationRings rings: another lake may not start inside.
            var frontier = new List<int>(footprint);
            foreach (int cell in footprint)
            {
                halo[cell] = id;
            }
            var next = new List<int>();
            for (int ring = 0; ring < SeparationRings; ring++)
            {
                next.Clear();
                foreach (int cell in frontier)
                {
                    for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                    {
                        int other = grid.GetNeighbor(k);
                        if (halo[other] != id)
                        {
                            halo[other] = id;
                            next.Add(other);
                        }
                    }
                }
                frontier.Clear();
                frontier.AddRange(next);
            }
        }

        /// <summary>
        /// Lake-aware drained area: base D8 accumulation in reverse flood order, except that flow
        /// leaving a closed lake's capture zone (water + dry basin floor) stops there, and flow
        /// leaving an open lake always continues from its single outlet. The base receivers are
        /// not modified.
        /// </summary>
        static void ComputeSurfaceAccumulation(LakeSet set)
        {
            HydrologyResult hydrology = set.Hydrology;
            HydrologyGrid grid = hydrology.Grid;
            int count = grid.CellCount;
            var accumulation = new double[count];
            var supply = new double[set.lakes.Length];
            for (int cell = 0; cell < count; cell++)
            {
                accumulation[cell] = grid.GetArea(cell);
            }
            for (int k = hydrology.OrderCount - 1; k >= 0; k--)
            {
                int cell = hydrology.GetOrderedCell(k);
                int receiver = hydrology.GetReceiver(cell);
                if (!hydrology.IsLand(cell) || receiver < 0)
                {
                    continue;
                }
                int zone = set.GetCaptureLakeOfCell(cell);
                if (zone >= 0 && set.GetCaptureLakeOfCell(receiver) != zone)
                {
                    Lake lake = set.lakes[zone];
                    if (lake.Class == LakeClass.Closed)
                    {
                        supply[zone] += accumulation[cell];
                        continue;
                    }
                    if (cell != lake.OutletLakeCell)
                    {
                        // An open lake has ONE outlet: flow the base D8 sends over another rim cell
                        // at the same level (ties on a flat shore) leaves through the outlet. The
                        // outlet cell is lower in flood order, so it has not been propagated yet.
                        accumulation[lake.OutletCell] += accumulation[cell];
                        continue;
                    }
                }
                accumulation[receiver] += accumulation[cell];
            }
            for (int i = 0; i < set.lakes.Length; i++)
            {
                Lake lake = set.lakes[i];
                lake.SupplyArea = lake.Class == LakeClass.Open ? accumulation[lake.OutletLakeCell] : supply[i];
                set.lakes[i] = lake;
            }
            set.surfaceAccumulation = accumulation;
        }
    }

    /// <summary>
    /// Caches the lake data. The depression analysis is rebuilt only for a new hydrology
    /// analysis (terrain or grid resolution change); selection settings rebuild the lake set
    /// only; display settings (visibility, color) never reach this cache.
    /// </summary>
    public sealed class LakeCache
    {
        readonly DepressionAnalyzer analyzer = new DepressionAnalyzer();
        readonly LakeSystem system = new LakeSystem();
        DepressionHierarchy depressions;
        LakeSet cached;
        LakeSettings cachedSettings;

        /// <summary>Number of depression analyses (candidate extraction).</summary>
        public int AnalysisCount { get; private set; }

        /// <summary>Number of lake selections.</summary>
        public int SelectionCount { get; private set; }

        public DepressionHierarchy Depressions => depressions;

        public LakeSet Current => cached;

        public LakeSet Get(HydrologyResult hydrology, LakeSettings settings, LakeContext context)
        {
            settings.Sanitize();
            if (depressions == null || depressions.Hydrology != hydrology || depressions.HydrologyVersion != hydrology.Version)
            {
                depressions = analyzer.Analyze(hydrology);
                AnalysisCount++;
                cached = null;
            }
            if (cached == null || cached.Seed != context.Seed || !settings.SelectionEquals(cachedSettings))
            {
                cached = system.Select(depressions, settings, context);
                cachedSettings = settings.Clone();
                SelectionCount++;
            }
            return cached;
        }

        public void Clear()
        {
            depressions = null;
            cached = null;
            cachedSettings = null;
        }
    }
}
