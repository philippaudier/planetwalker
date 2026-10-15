using System;
using System.Collections.Generic;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>
    /// V0.1.1D-B.1 lakes, data side: depression hierarchy, selection, classification, footprints,
    /// lake-aware river layer, lake-aware ribbons and water mesh geometry (no Unity objects).
    /// Numbers in comments refer to the milestone's required test list.
    /// </summary>
    public class LakeSystemTests
    {
        const int SyntheticResolution = 64;
        const int Seed = 1;

        // ------------------------------------------------------------ Synthetic terrains

        static float Angle(Vector3 d, Vector3 center)
        {
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, center.normalized), -1f, 1f));
        }

        /// <summary>Land plateau sloping gently south, ocean below y = -0.6.</summary>
        static float Plateau(Vector3 d)
        {
            return d.y < -0.6f ? -0.01f : 0.012f + 0.004f * d.y;
        }

        /// <summary>Paraboloid bowl of the given angular radius and depth (0 outside).</summary>
        static float Bowl(Vector3 d, Vector3 center, float radius, float depth)
        {
            float a = Angle(d, center) / radius;
            return a < 1f ? -depth * (1f - a * a) : 0f;
        }

        static float Pit(Vector3 d, Vector3 center, float width, float depth)
        {
            float a = Angle(d, center) / width;
            return -depth * Mathf.Exp(-a * a);
        }

        static readonly Vector3 North = new Vector3(0f, 0.6f, 0.8f).normalized;

        static LakeSettings Permissive(float filling = 0.7f)
        {
            return new LakeSettings { abundance = 1f, minimumArea = LakeSettings.MinMinimumArea, fillingFactor = filling };
        }

        sealed class Analysis
        {
            public HydrologyResult Hydrology;
            public DepressionHierarchy Depressions;
            public LakeSet Lakes;
        }

        static Analysis Analyze(Func<Vector3, float> terrain, LakeSettings settings = null, int resolution = SyntheticResolution,
            float seaLevel = 0f)
        {
            var hydrology = new HydrologySystem().Compute(new HydrologyGrid(resolution), terrain, seaLevel);
            var depressions = new DepressionAnalyzer().Analyze(hydrology);
            var lakes = new LakeSystem().Select(depressions, settings ?? Permissive(), new LakeContext(Seed));
            return new Analysis { Hydrology = hydrology, Depressions = depressions, Lakes = lakes };
        }

        /// <summary>Candidate whose footprint at spill contains the cell (the smallest one), with its ancestors.</summary>
        static int CandidateAt(DepressionHierarchy depressions, Vector3 direction)
        {
            return depressions.GetDeepestNode(depressions.Hydrology.Grid.CellFromDirection(direction));
        }

        static int LakeAt(LakeSet lakes, Vector3 direction)
        {
            return lakes.GetLakeOfCell(lakes.Hydrology.Grid.CellFromDirection(direction));
        }

        static List<int> Footprint(LakeSet lakes, int lake)
        {
            var cells = new List<int>();
            for (int i = 0; i < lakes.GetLake(lake).CellCount; i++)
            {
                cells.Add(lakes.GetFootprintCell(lake, i));
            }
            return cells;
        }

        static bool IsConnected(HydrologyGrid grid, List<int> cells)
        {
            if (cells.Count == 0)
            {
                return true;
            }
            var set = new HashSet<int>(cells);
            var seen = new HashSet<int> { cells[0] };
            var stack = new Stack<int>();
            stack.Push(cells[0]);
            while (stack.Count > 0)
            {
                int cell = stack.Pop();
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                {
                    int other = grid.GetNeighbor(k);
                    if (set.Contains(other) && seen.Add(other))
                    {
                        stack.Push(other);
                    }
                }
            }
            return seen.Count == set.Count;
        }

        static int OutletOf(HydrologyResult hydrology, int cell)
        {
            int steps = 0;
            while (hydrology.GetReceiver(cell) >= 0)
            {
                cell = hydrology.GetReceiver(cell);
                Assert.Less(++steps, hydrology.Grid.CellCount, "Cycle");
            }
            return cell;
        }

        /// <summary>Invariants every lake set must satisfy (tests 12, 13, 14, 28 and stable ids).</summary>
        static void AssertLakeInvariants(LakeSet lakes)
        {
            HydrologyResult hydrology = lakes.Hydrology;
            HydrologyGrid grid = hydrology.Grid;
            DepressionHierarchy depressions = lakes.Depressions;
            var owner = new Dictionary<int, int>();
            int previousKey = -1;
            for (int id = 0; id < lakes.Count; id++)
            {
                Lake lake = lakes.GetLake(id);
                Assert.AreEqual(id, lake.Id);
                Assert.Greater(lake.Key, previousKey, "Lakes are sorted by their stable key");
                previousKey = lake.Key;
                DepressionCandidate candidate = depressions.GetNode(lake.Candidate);
                Assert.AreEqual(candidate.BottomCell, lake.Key, "Key = lowest cell of the basin");
                Assert.IsFalse(float.IsNaN(lake.Level) || float.IsInfinity(lake.Level));
                Assert.Greater(lake.Level, hydrology.SeaLevel, "Lakes lie above sea level");
                // 13: never above the containment threshold; open = at the spill, closed = strictly below.
                Assert.LessOrEqual(lake.Level, lake.Spill);
                Assert.AreEqual(candidate.Spill, lake.Spill, 0f);
                if (lake.Class == LakeClass.Open)
                {
                    Assert.AreEqual(lake.Spill, lake.Level, 0f);
                    Assert.GreaterOrEqual(lake.OutletCell, 0, "Open lakes have an outlet");
                    Assert.AreEqual(-1, lakes.GetLakeOfCell(lake.OutletCell), "The outlet is dry");
                    Assert.AreEqual(lake.OutletCell, hydrology.GetReceiver(lake.OutletLakeCell), "Outlet follows the base drainage");
                    Assert.IsFalse(hydrology.IsLand(OutletOf(hydrology, lake.OutletCell)) && hydrology.EndorheicOutlet < 0,
                        "The outlet path reaches the ocean");
                }
                else
                {
                    Assert.Less(lake.Level, lake.Spill);
                    Assert.AreEqual(-1, lake.OutletCell);
                }
                Assert.Greater(lake.MaxDepth, 0f);
                Assert.Greater(lake.Area, 0.0);
                Assert.Greater(lake.Volume, 0.0);
                Assert.GreaterOrEqual(lake.CellCount, LakeSystem.MinLakeCells);

                // 12: only cells below the surface; positive depth.
                List<int> footprint = Footprint(lakes, id);
                double area = 0;
                foreach (int cell in footprint)
                {
                    Assert.IsTrue(hydrology.IsLand(cell), "No ocean cell in a lake");
                    Assert.Less(hydrology.GetElevation(cell), lake.Level, "Footprint cell above the water");
                    Assert.Greater(lakes.GetDepth(cell), 0f);
                    Assert.AreEqual(id, lakes.GetLakeOfCell(cell));
                    Assert.IsFalse(owner.ContainsKey(cell), "14: overlapping footprints");
                    owner.Add(cell, id);
                    area += grid.GetArea(cell);
                    for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                    {
                        Assert.IsTrue(hydrology.IsLand(grid.GetNeighbor(k)), "A lake never touches the ocean");
                    }
                }
                Assert.AreEqual(area, lake.Area, 1e-9 * Math.Max(1.0, area));
                // 28: one connected water body, bounded by terrain at or above the level.
                Assert.IsTrue(IsConnected(grid, footprint), "Lake " + id + " is one connected component");
                foreach (int cell in footprint)
                {
                    for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                    {
                        int other = grid.GetNeighbor(k);
                        if (lakes.GetLakeOfCell(other) != id)
                        {
                            Assert.GreaterOrEqual(depressions.GetFloodKey(other), lake.Level, "Shore cell must hold the water back");
                        }
                    }
                }
            }
            // 14: accepted candidates form an antichain and lakes are separated.
            for (int a = 0; a < lakes.Count; a++)
            {
                for (int b = 0; b < lakes.Count; b++)
                {
                    if (a != b)
                    {
                        Assert.IsFalse(depressions.IsWithin(lakes.GetLake(a).Candidate, lakes.GetLake(b).Candidate), "Nested lakes");
                    }
                }
            }
            foreach (KeyValuePair<int, int> entry in owner)
            {
                for (int k = grid.GetNeighborStart(entry.Key); k < grid.GetNeighborEnd(entry.Key); k++)
                {
                    int other = grid.GetNeighbor(k);
                    Assert.IsTrue(!owner.TryGetValue(other, out int lake) || lake == entry.Value, "Lakes touch");
                }
            }
        }

        // ------------------------------------------------------------ Depression analysis

        // 1
        [Test]
        public void SimpleClosedDepression_IsOneTopLevelCandidate()
        {
            Analysis analysis = Analyze(d => Plateau(d) + Bowl(d, North, 0.09f, 0.004f));
            int node = CandidateAt(analysis.Depressions, North);
            Assert.GreaterOrEqual(node, 0, "The bowl floor is a candidate");
            DepressionCandidate d = analysis.Depressions.GetNode(node);
            Assert.IsTrue(analysis.Depressions.IsCandidate(node));
            Assert.IsFalse(d.IsNested);
            Assert.AreEqual(DepressionOutlet.Ocean, d.Outlet);
            Assert.Greater(d.MaxDepth, 0.002f);
            Assert.Greater(d.AreaAtSpill, 0.0);
            Assert.Greater(d.VolumeAtSpill, 0.0);
            Assert.Less(Angle(d.Centroid, North), 0.09f, "Centroid inside the bowl");
            // The slope shifts the lowest point slightly downhill: within one cell of the bowl center.
            Assert.Less(Angle(analysis.Hydrology.Grid.GetCenter(d.BottomCell), North), 1.5f * Mathf.PI * 0.5f / SyntheticResolution,
                "Bottom at the bowl center");

            // Area-weighted metrics equal the sums over the footprint at spill.
            var footprint = new List<int>();
            analysis.Depressions.CollectFootprint(node, d.Spill, footprint);
            double area = 0, volume = 0;
            foreach (int cell in footprint)
            {
                area += analysis.Hydrology.Grid.GetArea(cell);
                volume += analysis.Hydrology.Grid.GetArea(cell) * (double)(d.Spill - analysis.Hydrology.GetElevation(cell));
            }
            Assert.AreEqual(footprint.Count, d.CellsAtSpill);
            Assert.AreEqual(area, d.AreaAtSpill, 1e-9);
            Assert.AreEqual(volume, d.VolumeAtSpill, 1e-6 * volume);

            // Consistent with the Priority-Flood: a top-level depression fills to its spill.
            Assert.AreEqual(d.Spill, analysis.Hydrology.GetFilledElevation(d.BottomCell), 1e-5);
            AssertLakeInvariants(analysis.Lakes);
            Assert.GreaterOrEqual(LakeAt(analysis.Lakes, North), 0, "The bowl becomes a lake");
        }

        // 2
        [Test]
        public void Depression_WithClearSpillPoint_SpillsThroughTheNotch()
        {
            // A notch cut through the bowl rim toward +x (east), clearly lower than the rest of the rim.
            Vector3 east = Vector3.Cross(Vector3.up, North).normalized;
            Vector3 notchDirection = (North + east * 0.09f).normalized;
            Func<Vector3, float> terrain = d =>
            {
                float bowl = Bowl(d, North, 0.09f, 0.005f);
                float notch = -0.003f * Mathf.Exp(-Mathf.Pow(Angle(d, notchDirection) / 0.03f, 2f));
                float rim = 0.003f * Mathf.Exp(-Mathf.Pow((Angle(d, North) - 0.09f) / 0.03f, 2f));
                return Plateau(d) + bowl + rim + notch;
            };
            Analysis analysis = Analyze(terrain);
            int node = CandidateAt(analysis.Depressions, North);
            DepressionCandidate d = analysis.Depressions.GetNode(node);
            HydrologyGrid grid = analysis.Hydrology.Grid;
            Vector3 spill = grid.GetCenter(d.SpillCell);
            Assert.Less(Angle(spill, notchDirection), 0.06f, "Spill location at the notch");
            Assert.GreaterOrEqual(d.ReceivingCell, 0);
            Assert.IsFalse(analysis.Hydrology.IsLand(OutletOf(analysis.Hydrology, d.ReceivingCell)), "Receiving path reaches the ocean");
            Assert.LessOrEqual(analysis.Hydrology.GetElevation(d.SpillCell), d.Spill + 1e-6f);
        }

        // 3
        [Test]
        public void NestedDepressions_FormAHierarchy()
        {
            // Two deep pits inside one wider, shallower basin: two leaves nested in a parent.
            Vector3 east = Vector3.Cross(Vector3.up, North).normalized;
            Vector3 a = (North + east * 0.04f).normalized, b = (North - east * 0.04f).normalized;
            Func<Vector3, float> terrain = d => Plateau(d) + Bowl(d, North, 0.1f, 0.0015f) + Pit(d, a, 0.03f, 0.004f) + Pit(d, b, 0.03f, 0.004f);
            Analysis analysis = Analyze(terrain);
            DepressionHierarchy depressions = analysis.Depressions;
            int leafA = CandidateAt(depressions, a), leafB = CandidateAt(depressions, b);
            Assert.AreNotEqual(leafA, leafB, "Two distinct pits");
            DepressionCandidate da = depressions.GetNode(leafA), db = depressions.GetNode(leafB);
            Assert.IsTrue(da.IsNested && db.IsNested);
            Assert.AreEqual(da.Parent, db.Parent, "Common parent");
            DepressionCandidate parent = depressions.GetNode(da.Parent);
            Assert.IsFalse(parent.IsNested);
            Assert.AreEqual(da.Spill, parent.MergeHeight, 0f);
            Assert.Greater(parent.Spill, parent.MergeHeight, "The parent holds more water above the merge level");
            Assert.AreEqual(1, da.NestingLevel);
            Assert.IsTrue(depressions.IsWithin(leafA, da.Parent));
            // Child footprints at their spill are inside the parent's.
            var parentCells = new List<int>();
            depressions.CollectFootprint(da.Parent, parent.Spill, parentCells);
            var childCells = new List<int>();
            depressions.CollectFootprint(leafA, da.Spill, childCells);
            Assert.IsTrue(new HashSet<int>(parentCells).IsSupersetOf(childCells));
            Assert.Greater(parent.AreaAtSpill, da.AreaAtSpill + db.AreaAtSpill);
            AssertLakeInvariants(analysis.Lakes);
        }

        // 4
        [Test]
        public void TwoDepressions_SharingASpillSaddle_SpillIntoEachOther()
        {
            Vector3 east = Vector3.Cross(Vector3.up, North).normalized;
            Vector3 a = (North + east * 0.05f).normalized, b = (North - east * 0.05f).normalized;
            Func<Vector3, float> terrain = d => Plateau(d) + Pit(d, a, 0.035f, 0.004f) + Pit(d, b, 0.035f, 0.004f);
            Analysis analysis = Analyze(terrain);
            DepressionHierarchy depressions = analysis.Depressions;
            int leafA = CandidateAt(depressions, a), leafB = CandidateAt(depressions, b);
            DepressionCandidate da = depressions.GetNode(leafA), db = depressions.GetNode(leafB);
            Assert.AreEqual(DepressionOutlet.Depression, da.Outlet);
            Assert.AreEqual(DepressionOutlet.Depression, db.Outlet);
            Assert.AreEqual(leafB, da.SpillTarget);
            Assert.AreEqual(leafA, db.SpillTarget);
            Assert.AreEqual(da.Spill, db.Spill, 0f, "Same saddle elevation");
            Assert.AreEqual(da.SpillCell, db.SpillCell, "Same saddle crest");
            Vector3 saddle = analysis.Hydrology.Grid.GetCenter(da.SpillCell);
            Assert.Less(Angle(saddle, North), 0.04f, "The saddle lies between the pits");
            // Same physical space: at most one of the two pits and their union is a lake.
            AssertLakeInvariants(analysis.Lakes);
        }

        // 5
        [Test]
        public void FlatBottomDepression_IsOneCandidate()
        {
            const float floor = 0.008f;
            Func<Vector3, float> terrain = d => Angle(d, North) < 0.07f ? floor : Plateau(d) + 0.002f;
            Analysis analysis = Analyze(terrain);
            DepressionHierarchy depressions = analysis.Depressions;
            int node = CandidateAt(depressions, North);
            DepressionCandidate d = depressions.GetNode(node);
            Assert.AreEqual(floor, d.Bottom, 0f);
            Assert.IsFalse(d.IsNested, "Flat minima are absorbed, not nested");
            Assert.AreEqual(-1, d.ChildA);
            int candidatesInFloor = 0;
            for (int n = 0; n < depressions.NodeCount; n++)
            {
                if (depressions.IsCandidate(n) && Angle(depressions.GetNode(n).Centroid, North) < 0.07f)
                {
                    candidatesInFloor++;
                }
            }
            Assert.AreEqual(1, candidatesInFloor, "No zero-depth candidates on the flat floor");
            AssertLakeInvariants(analysis.Lakes);
            int lake = LakeAt(analysis.Lakes, North);
            Assert.GreaterOrEqual(lake, 0);
            Assert.Greater(analysis.Lakes.GetLake(lake).Level, floor);
        }

        // 6
        [Test]
        public void OceanConnectedDepression_IsNotACandidate()
        {
            // A bowl straddling the coastline (y = -0.6): open to the sea, not a closed depression.
            Vector3 coast = new Vector3(0f, -0.6f, 0.8f).normalized;
            Analysis analysis = Analyze(d => Plateau(d) + Bowl(d, coast, 0.12f, 0.02f));
            HydrologyResult hydrology = analysis.Hydrology;
            DepressionHierarchy depressions = analysis.Depressions;
            for (int cell = 0; cell < hydrology.Grid.CellCount; cell++)
            {
                if (!hydrology.IsLand(cell))
                {
                    Assert.AreEqual(-1, depressions.GetDeepestNode(cell), "Ocean cells belong to no candidate");
                    Assert.AreEqual(-1, analysis.Lakes.GetLakeOfCell(cell));
                }
                else if (Angle(hydrology.Grid.GetCenter(cell), coast) < 0.12f)
                {
                    Assert.AreEqual(-1, analysis.Lakes.GetLakeOfCell(cell), "No lake in a bay");
                }
            }
            AssertLakeInvariants(analysis.Lakes);
        }

        // 7
        [Test]
        public void DepressionCrossingACubeFaceSeam_IsOneLake()
        {
            // Centered on the edge between the +X and +Y faces.
            Vector3 seam = new Vector3(1f, 1f, 0.15f).normalized;
            Analysis analysis = Analyze(d => 0.012f + 0.004f * d.x + Bowl(d, seam, 0.09f, 0.004f) - (d.x < -0.6f ? 0.03f : 0f));
            int lake = LakeAt(analysis.Lakes, seam);
            Assert.GreaterOrEqual(lake, 0);
            var faces = new HashSet<int>();
            foreach (int cell in Footprint(analysis.Lakes, lake))
            {
                analysis.Hydrology.Grid.GetFaceCoordinates(cell, out int face, out _, out _);
                faces.Add(face);
            }
            Assert.GreaterOrEqual(faces.Count, 2, "The footprint spans both faces");
            AssertLakeInvariants(analysis.Lakes);
        }

        // 8
        [Test]
        public void DepressionContainingAnIsland_KeepsTheIslandDry()
        {
            Func<Vector3, float> terrain = d => Plateau(d) + Bowl(d, North, 0.09f, 0.005f) + 0.008f * Mathf.Exp(-Mathf.Pow(Angle(d, North) / 0.02f, 2f));
            Analysis analysis = Analyze(terrain);
            int island = analysis.Hydrology.Grid.CellFromDirection(North);
            Assert.AreEqual(-1, analysis.Lakes.GetLakeOfCell(island), "The island stays dry");
            int lakeCells = 0, lake = -1;
            HydrologyGrid grid = analysis.Hydrology.Grid;
            for (int k = grid.GetNeighborStart(island); k < grid.GetNeighborEnd(island); k++)
            {
                lake = Mathf.Max(lake, analysis.Lakes.GetLakeOfCell(grid.GetNeighbor(k)));
            }
            Assert.GreaterOrEqual(lake, 0, "Water around the island");
            Lake data = analysis.Lakes.GetLake(lake);
            Assert.Greater(analysis.Hydrology.GetElevation(island), data.Level);
            lakeCells = data.CellCount;
            Assert.Greater(lakeCells, 8);
            AssertLakeInvariants(analysis.Lakes);
        }

        // ------------------------------------------------------------ Rivers and lakes

        static RiverNetwork Network(HydrologyResult hydrology, float threshold)
        {
            var system = new RiverNetworkSystem();
            RiverNetwork network = system.Build(hydrology, threshold);
            system.BuildPaths(network, 2);
            return network;
        }

        /// <summary>Topological validity of every river/lake connection (tests 10, 11, 20).</summary>
        static void AssertLayerValid(LakeRiverLayer layer)
        {
            RiverNetwork network = layer.Network;
            LakeSet lakes = layer.Lakes;
            HydrologyResult hydrology = network.Hydrology;
            Assert.AreEqual(0, layer.ClosedLakeOutflows, "Closed lakes emit no visible river");
            for (int r = 0; r < layer.ReachCount; r++)
            {
                RiverReach reach = layer.GetReach(r);
                River river = network.GetRiver(reach.River);
                Assert.LessOrEqual(reach.FirstNode, reach.LastNode);
                for (int i = reach.FirstNode; i <= reach.LastNode; i++)
                {
                    int node = network.GetRiverNode(reach.River, i);
                    Assert.AreEqual(RiverLakeState.Visible, layer.GetNodeState(node), "Reaches only contain visible nodes");
                    Assert.AreEqual(-1, lakes.GetCaptureLakeOfCell(network.GetNodeCell(node)), "A visible node is never in a lake");
                }
                int lastCell = network.GetNodeCell(network.GetRiverNode(reach.River, reach.LastNode));
                if (reach.End == ReachEnd.LakeInflow)
                {
                    Assert.GreaterOrEqual(reach.EndLake, 0);
                    Assert.AreEqual(reach.EndLake, lakes.GetCaptureLakeOfCell(reach.EntryCell), "Entry cell inside the lake (or its closed basin)");
                    Assert.AreEqual(reach.EntryCell, hydrology.GetReceiver(lastCell), "The river enters along the base drainage");
                    if (reach.ConnectorCount > 0)
                    {
                        int last = layer.GetConnectorCell(r, reach.ConnectorCount - 1);
                        Assert.AreEqual(reach.EndLake, lakes.GetLakeOfCell(last), "Connector ends on the water");
                    }
                }
                else
                {
                    Assert.AreEqual(river.NodeCount - 1, reach.LastNode, "A reach ends at a lake or at its river's end");
                }
                if (reach.Start == ReachStart.LakeOutlet)
                {
                    Lake lake = lakes.GetLake(reach.StartLake);
                    Assert.AreEqual(LakeClass.Open, lake.Class);
                    int previous = network.GetNodeCell(network.GetRiverNode(reach.River, reach.FirstNode - 1));
                    int first = network.GetNodeCell(network.GetRiverNode(reach.River, reach.FirstNode));
                    Assert.AreEqual(reach.StartLake, lakes.GetLakeOfCell(previous));
                    Assert.AreEqual(first, hydrology.GetReceiver(previous), "Outflow follows the base drainage");
                }
                else if (reach.Start == ReachStart.Source)
                {
                    Assert.AreEqual(0, reach.FirstNode);
                }
            }
            for (int lake = 0; lake < lakes.Count; lake++)
            {
                int outflow = layer.GetLakeOutflowReach(lake);
                if (outflow >= 0)
                {
                    RiverReach reach = layer.GetReach(outflow);
                    Assert.AreEqual(lakes.GetLake(lake).OutletCell,
                        network.GetNodeCell(network.GetRiverNode(reach.River, reach.FirstNode)), "Outflow starts at the outlet");
                }
                if (lakes.GetLake(lake).Class == LakeClass.Open && layer.GetLakeInflowCount(lake) > 0)
                {
                    Assert.GreaterOrEqual(outflow, 0, "An open lake fed by rivers has a downstream river");
                }
            }
        }

        // 9, 10
        [Test]
        public void OpenLake_ReceivesSeveralRivers_AndHasOneValidOutlet()
        {
            // The whole slope north of the bowl drains into it: large supply, several inflows.
            Analysis analysis = Analyze(d => Plateau(d) + Bowl(d, North, 0.08f, 0.004f));
            int lake = LakeAt(analysis.Lakes, North);
            Lake data = analysis.Lakes.GetLake(lake);
            Assert.AreEqual(LakeClass.Open, data.Class, "Large supply relative to the lake: it overflows");
            Assert.Greater(data.CatchmentArea / data.Area, LakeSystem.MaxOverflowRatio);
            Assert.Less(analysis.Hydrology.Grid.GetCenter(data.OutletCell).y, analysis.Hydrology.Grid.GetCenter(data.Key).y,
                "The outlet is on the downhill (south) rim");

            LakeRiverLayer layer = LakeRiverIntegration.Build(Network(analysis.Hydrology, 2e-4f), analysis.Lakes);
            Assert.GreaterOrEqual(layer.GetLakeInflowCount(lake), 2, "9: multiple incoming rivers");
            Assert.GreaterOrEqual(layer.GetLakeOutflowReach(lake), 0, "10: one downstream river from the outlet");
            int outflows = 0;
            for (int i = 0; i < layer.InteractionCount; i++)
            {
                LakeInteraction interaction = layer.GetInteraction(i);
                if (interaction.Lake == lake && interaction.Kind == LakeInteractionKind.Outflow)
                {
                    outflows++;
                    Assert.AreEqual(data.OutletCell, interaction.Cell);
                }
            }
            Assert.AreEqual(1, outflows, "Exactly one outflow");
            AssertLayerValid(layer);
            AssertLakeInvariants(analysis.Lakes);
        }

        // 11
        [Test]
        public void ClosedLake_HasNoOutgoingVisibleRiver()
        {
            // A broad bowl on top of a dome: its catchment is barely larger than the lake.
            Vector3 top = Vector3.up;
            Func<Vector3, float> terrain = d => 0.02f - 0.03f * Angle(d, top) * Angle(d, top) + Bowl(d, top, 0.09f, 0.004f);
            Analysis analysis = Analyze(terrain);
            int lake = LakeAt(analysis.Lakes, top);
            Assert.GreaterOrEqual(lake, 0);
            Lake data = analysis.Lakes.GetLake(lake);
            Assert.AreEqual(LakeClass.Closed, data.Class);
            Assert.Less(data.Level, data.Spill);

            // Dense network so that rivers do reach the lake.
            LakeRiverLayer layer = LakeRiverIntegration.Build(Network(analysis.Hydrology, LakeSettings.MinMinimumArea), analysis.Lakes);
            Assert.AreEqual(-1, layer.GetLakeOutflowReach(lake));
            RiverNetwork network = layer.Network;
            HydrologyGrid grid = analysis.Hydrology.Grid;
            for (int r = 0; r < layer.ReachCount; r++)
            {
                RiverReach reach = layer.GetReach(r);
                Assert.AreNotEqual(lake, reach.StartLake);
                if (reach.Start == ReachStart.Reactivated)
                {
                    int first = network.GetNodeCell(network.GetRiverNode(reach.River, reach.FirstNode));
                    for (int k = grid.GetNeighborStart(first); k < grid.GetNeighborEnd(first); k++)
                    {
                        Assert.AreNotEqual(lake, analysis.Lakes.GetCaptureLakeOfCell(grid.GetNeighbor(k)), "No pseudo-outflow on the rim");
                    }
                }
            }
            // Base channels leaving the basin lose the captured supply (explicit, base graph untouched).
            for (int node = 0; node < network.NodeCount; node++)
            {
                int cell = network.GetNodeCell(node);
                if (analysis.Lakes.GetCaptureLakeOfCell(cell) == lake)
                {
                    int receiver = analysis.Hydrology.GetReceiver(cell);
                    if (receiver >= 0 && analysis.Lakes.GetCaptureLakeOfCell(receiver) != lake)
                    {
                        Assert.Greater(analysis.Hydrology.GetAccumulation(receiver), analysis.Lakes.GetSurfaceAccumulation(receiver));
                    }
                }
            }
            Assert.Greater(layer.GetLakeInflowCount(lake), 0, "Rivers end in the closed lake");
            AssertLayerValid(layer);
            AssertLakeInvariants(analysis.Lakes);
        }

        // 19 (lake-aware accumulation)
        [Test]
        public void SurfaceAccumulation_ConservesArea_AndNeverExceedsTheBaseDrainage()
        {
            var planet = new PlanetInstance(new PlanetDefinition { seed = 335559 });
            HydrologyResult hydrology = new HydrologySystem().Compute(new HydrologyGrid(64), planet);
            LakeSet lakes = new LakeCache().Get(hydrology, new LakeSettings { abundance = 1f, fillingFactor = 0.3f },
                new LakeContext(planet.Definition.seed, planet.Height.GetMountainInfluence));
            Assert.Greater(lakes.ClosedCount, 0, "Test needs closed lakes");
            HydrologyGrid grid = hydrology.Grid;
            double terminal = 0, captured = 0;
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                double surface = lakes.GetSurfaceAccumulation(cell);
                Assert.LessOrEqual(surface, hydrology.GetAccumulation(cell) * (1 + 1e-12));
                Assert.GreaterOrEqual(surface, grid.GetArea(cell) * (1 - 1e-6));
                if (hydrology.GetReceiver(cell) < 0)
                {
                    terminal += surface;
                }
            }
            for (int i = 0; i < lakes.Count; i++)
            {
                if (lakes.GetLake(i).Class == LakeClass.Closed)
                {
                    captured += lakes.GetLake(i).SupplyArea;
                }
            }
            Assert.AreEqual(grid.TotalArea, terminal + captured, 1e-9 * grid.TotalArea, "Every area ends at an outlet or a closed lake");
        }

        // ------------------------------------------------------------ Planets

        static readonly int[] ReferenceSeeds = { 868497, 609538, 280695, 554492, 335559 };
        static readonly Dictionary<(int, int), HydrologyResult> PlanetHydrology = new Dictionary<(int, int), HydrologyResult>();

        static HydrologyResult PlanetAnalysis(int seed, int resolution, out PlanetInstance planet)
        {
            planet = new PlanetInstance(new PlanetDefinition { seed = seed });
            if (!PlanetHydrology.TryGetValue((seed, resolution), out HydrologyResult result))
            {
                result = new HydrologySystem().Compute(new HydrologyGrid(resolution), planet);
                PlanetHydrology.Add((seed, resolution), result);
            }
            return result;
        }

        static LakeSet PlanetLakes(int seed, int resolution, LakeSettings settings, out PlanetInstance planet)
        {
            HydrologyResult hydrology = PlanetAnalysis(seed, resolution, out planet);
            return new LakeCache().Get(hydrology, settings ?? new LakeSettings(), new LakeContext(seed, planet.Height.GetMountainInfluence));
        }

        // 12, 13, 14, 28 on real planets; 20 on real networks
        [Test]
        public void ReferencePlanets_SatisfyLakeAndConnectivityInvariants()
        {
            foreach (int seed in ReferenceSeeds)
            {
                LakeSet lakes = PlanetLakes(seed, 64, null, out _);
                Assert.Greater(lakes.Count, 0, "Seed " + seed);
                AssertLakeInvariants(lakes);
                LakeRiverLayer layer = LakeRiverIntegration.Build(Network(lakes.Hydrology, 7e-4f), lakes);
                AssertLayerValid(layer);
                // Every top-level candidate matches the Priority-Flood fill of its bottom.
                DepressionHierarchy depressions = lakes.Depressions;
                for (int n = 0; n < depressions.NodeCount; n++)
                {
                    DepressionCandidate d = depressions.GetNode(n);
                    if (depressions.IsCandidate(n) && !d.IsNested)
                    {
                        Assert.AreEqual(d.Spill, lakes.Hydrology.GetFilledElevation(d.BottomCell), 1e-5, "Candidate " + n);
                    }
                }
            }
        }

        // 15
        [Test]
        public void SameInputs_ProduceIdenticalLakes()
        {
            var definition = new PlanetDefinition { seed = 609538 };
            LakeSet a = Independent(definition);
            LakeSet b = Independent(definition.Clone());
            Assert.AreEqual(a.CandidateCount, b.CandidateCount);
            Assert.AreEqual(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Lake la = a.GetLake(i), lb = b.GetLake(i);
                Assert.AreEqual(la.Key, lb.Key);
                Assert.AreEqual(la.Class, lb.Class);
                Assert.AreEqual(la.Level, lb.Level, 0f);
                Assert.AreEqual(la.Area, lb.Area, 0.0);
                Assert.AreEqual(la.OutletCell, lb.OutletCell);
                CollectionAssert.AreEqual(Footprint(a, i), Footprint(b, i));
            }
            for (int n = 0; n < a.Depressions.NodeCount; n++)
            {
                Assert.AreEqual(a.Depressions.GetNode(n).Spill, b.Depressions.GetNode(n).Spill, 0f);
                Assert.AreEqual(a.GetCandidateStatus(n), b.GetCandidateStatus(n));
            }
        }

        static LakeSet Independent(PlanetDefinition definition)
        {
            var planet = new PlanetInstance(definition);
            HydrologyResult hydrology = new HydrologySystem().Compute(new HydrologyGrid(64), planet);
            return new LakeCache().Get(hydrology, new LakeSettings(), new LakeContext(definition.seed, planet.Height.GetMountainInfluence));
        }

        // 16
        [Test]
        public void LakeKeys_AreStable_WhenMoreLakesAreSelected()
        {
            foreach (int seed in ReferenceSeeds)
            {
                LakeSet few = PlanetLakes(seed, 64, new LakeSettings { abundance = 0.3f }, out _);
                LakeSet many = PlanetLakes(seed, 64, new LakeSettings { abundance = 0.9f }, out _);
                var byKey = new Dictionary<int, Lake>();
                for (int i = 0; i < many.Count; i++)
                {
                    byKey.Add(many.GetLake(i).Key, many.GetLake(i));
                }
                for (int i = 0; i < few.Count; i++)
                {
                    Lake lake = few.GetLake(i);
                    if (lake.IsMountainLake)
                    {
                        continue; // the small mountain-lake quota is filled after the main pass
                    }
                    Assert.IsTrue(byKey.TryGetValue(lake.Key, out Lake same), "Seed " + seed + ": lake " + lake.Key + " kept its key");
                    Assert.AreEqual(lake.Level, same.Level, 0f);
                    Assert.AreEqual(lake.CellCount, same.CellCount);
                }
            }
        }

        // 17, 18, 27
        [Test]
        public void Lakes_DoNotModifyTerrainOrBaseHydrology()
        {
            HydrologyResult hydrology = PlanetAnalysis(554492, 64, out PlanetInstance planet);
            HydrologyGrid grid = hydrology.Grid;
            int count = grid.CellCount;
            var receivers = new int[count];
            var accumulation = new double[count];
            var filled = new double[count];
            var elevation = new float[count];
            var continental = new float[count];
            var mountains = new float[count];
            for (int cell = 0; cell < count; cell++)
            {
                receivers[cell] = hydrology.GetReceiver(cell);
                accumulation[cell] = hydrology.GetAccumulation(cell);
                filled[cell] = hydrology.GetFilledElevation(cell);
                elevation[cell] = planet.GetElevation(grid.GetCenter(cell));
                continental[cell] = planet.Height.GetContinentalField(grid.GetCenter(cell));
                mountains[cell] = planet.Height.GetMountainInfluence(grid.GetCenter(cell));
            }
            var cache = new LakeCache();
            var context = new LakeContext(planet.Definition.seed, planet.Height.GetMountainInfluence);
            foreach (float abundance in new[] { 0f, 0.5f, 1f })
            {
                LakeSet lakes = cache.Get(hydrology, new LakeSettings { abundance = abundance, fillingFactor = abundance }, context);
                LakeRiverLayer layer = LakeRiverIntegration.Build(Network(hydrology, 3e-4f), lakes);
                Assert.IsNotNull(layer);
            }
            Assert.AreEqual(1, cache.AnalysisCount, "27: selection changes reuse the candidate analysis");
            Assert.AreEqual(3, cache.SelectionCount);
            for (int cell = 0; cell < count; cell++)
            {
                Assert.AreEqual(receivers[cell], hydrology.GetReceiver(cell));
                Assert.AreEqual(accumulation[cell], hydrology.GetAccumulation(cell), 0.0);
                Assert.AreEqual(filled[cell], hydrology.GetFilledElevation(cell), 0.0);
                Vector3 center = grid.GetCenter(cell);
                Assert.AreEqual(elevation[cell], planet.GetElevation(center), 0f);
                Assert.AreEqual(elevation[cell], hydrology.GetElevation(cell), 0f);
                Assert.AreEqual(continental[cell], planet.Height.GetContinentalField(center), 0f);
                Assert.AreEqual(mountains[cell], planet.Height.GetMountainInfluence(center), 0f);
            }
        }

        // 19
        [Test]
        public void DrainageInvariants_HoldAfterLakeGeneration()
        {
            HydrologyResult hydrology = PlanetAnalysis(868497, 64, out PlanetInstance planet);
            LakeSet lakes = PlanetLakes(868497, 64, new LakeSettings { abundance = 1f }, out _);
            HydrologyGrid grid = hydrology.Grid;
            double terminal = 0;
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                int receiver = hydrology.GetReceiver(cell);
                if (hydrology.IsLand(cell) && cell != hydrology.EndorheicOutlet)
                {
                    Assert.GreaterOrEqual(receiver, 0);
                    Assert.Less(hydrology.GetFilledElevation(receiver), hydrology.GetFilledElevation(cell));
                }
                if (receiver < 0)
                {
                    terminal += hydrology.GetAccumulation(cell);
                }
            }
            Assert.AreEqual(grid.TotalArea, terminal, 1e-9 * grid.TotalArea);
            Assert.Greater(lakes.Count, 0);
        }

        // 26
        [Test]
        public void ZeroLakePlanets_RemainValid()
        {
            // No depression at all: a uniform slope.
            Analysis slope = Analyze(d => 0.01f * (d.y + 0.6f));
            Assert.AreEqual(0, slope.Lakes.Count);
            Assert.AreEqual(0, slope.Lakes.Depressions.CandidateCount);
            AssertLakeInvariants(slope.Lakes);

            // Real planet, nothing selected: lake-aware ribbons equal the plain ones.
            LakeSet none = PlanetLakes(280695, 64, new LakeSettings { abundance = 0f, minimumArea = LakeSettings.MaxMinimumArea }, out PlanetInstance planet);
            Assert.AreEqual(0, none.Count);
            RiverNetwork network = Network(none.Hydrology, 7e-4f);
            LakeRiverLayer layer = LakeRiverIntegration.Build(network, none);
            Assert.AreEqual(network.RiverCount, layer.ReachCount, "One reach per river");
            PlanetSurfaceSampler sampler = Sampler(planet, 32);
            var plain = new RiverRibbonBuilder();
            plain.BuildGeometry(network, sampler, planet, new RiverSettings());
            var aware = new RiverRibbonBuilder();
            aware.BuildGeometry(network, sampler, planet, new RiverSettings(), layer);
            CollectionAssert.AreEqual(plain.Vertices, aware.Vertices);
            var mesh = new LakeMeshBuilder();
            mesh.BuildGeometry(none, planet, sampler);
            Assert.AreEqual(0, mesh.Vertices.Count);

            // A planet without ocean: the landmass root has no outlet, nothing breaks.
            Analysis dry = Analyze(d => 0.01f + Bowl(d, North, 0.09f, 0.004f) + 0.002f * d.y, null, 32, -0.05f);
            AssertLakeInvariants(dry.Lakes);
        }

        // 30
        [Test]
        public void ExtremeLakeSettings_RemainNumericallyStable()
        {
            HydrologyResult hydrology = PlanetAnalysis(609538, 64, out PlanetInstance planet);
            var context = new LakeContext(planet.Definition.seed, planet.Height.GetMountainInfluence);
            DepressionHierarchy depressions = new DepressionAnalyzer().Analyze(hydrology);
            var system = new LakeSystem();
            foreach (float abundance in new[] { -1f, 0f, 1f, 2f })
            {
                foreach (float area in new[] { 0f, LakeSettings.MinMinimumArea, LakeSettings.MaxMinimumArea, 1f })
                {
                    foreach (float filling in new[] { -1f, 0f, 1f, 5f })
                    {
                        var settings = new LakeSettings { abundance = abundance, minimumArea = area, fillingFactor = filling };
                        LakeSet lakes = system.Select(depressions, settings, context);
                        AssertLakeInvariants(lakes);
                        Assert.IsFalse(double.IsNaN(lakes.TotalArea));
                    }
                }
            }
            foreach (float sea in new[] { PlanetDefinition.MinSeaLevel, PlanetDefinition.MaxSeaLevel })
            {
                var definition = new PlanetDefinition { seed = 609538, seaLevel = sea };
                var extreme = new PlanetInstance(definition);
                HydrologyResult h = new HydrologySystem().Compute(new HydrologyGrid(32), extreme);
                LakeSet lakes = new LakeCache().Get(h, Permissive(), new LakeContext(definition.seed, extreme.Height.GetMountainInfluence));
                AssertLakeInvariants(lakes);
            }
        }

        // ------------------------------------------------------------ Geometry (no Unity objects)

        static PlanetSurfaceSampler Sampler(PlanetInstance planet, int meshResolution)
        {
            return Sampler(planet.Height.GetElevation, planet.Radius, meshResolution);
        }

        /// <summary>A displayed mesh (vertex lists like the face meshes) of any elevation function.</summary>
        static PlanetSurfaceSampler Sampler(Func<Vector3, float> elevation, float radius, int meshResolution)
        {
            var faces = new IReadOnlyList<Vector3>[CubeSphereGenerator.FaceCount];
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                var vertices = new List<Vector3>();
                for (int y = 0; y <= meshResolution; y++)
                {
                    for (int x = 0; x <= meshResolution; x++)
                    {
                        Vector3 d = CubeSphereGenerator.GetSphereDirection(face, x, y, meshResolution);
                        vertices.Add(d * (radius * (1f + elevation(d))));
                    }
                }
                faces[face] = vertices;
            }
            var sampler = new PlanetSurfaceSampler();
            sampler.Capture(faces, meshResolution);
            return sampler;
        }

        /// <summary>Mesh validity (29), shoreline coherence (28) and seam continuity (7, 24).</summary>
        static void AssertLakeMeshValid(LakeMeshBuilder builder, PlanetSurfaceSampler sampler)
        {
            IReadOnlyList<Vector3> vertices = builder.Vertices;
            IReadOnlyList<int> triangles = builder.Triangles;
            IReadOnlyList<Vector2> water = builder.WaterAttributes;
            var edgeUse = new Dictionary<long, int>();
            for (int t = 0; t < triangles.Count; t += 3)
            {
                Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
                Vector3 normal = Vector3.Cross(b - a, c - a);
                Assert.Greater(normal.magnitude, 0f, "Degenerate triangle");
                Assert.Greater(Vector3.Dot(normal, a + b + c), 0f, "Inverted triangle");
                for (int e = 0; e < 3; e++)
                {
                    int i = triangles[t + e], j = triangles[t + (e + 1) % 3];
                    long key = ((long)Mathf.Min(i, j) << 32) | (uint)Mathf.Max(i, j);
                    edgeUse.TryGetValue(key, out int uses);
                    edgeUse[key] = uses + 1;
                }
            }
            foreach (KeyValuePair<long, int> edge in edgeUse)
            {
                Assert.LessOrEqual(edge.Value, 2, "Non-manifold lake surface");
                if (edge.Value == 1)
                {
                    // Open edges only on the shoreline or the outlet lip (on the displayed terrain):
                    // no hole inside the water, no crack along cube-face seams.
                    int i = (int)(edge.Key >> 32), j = (int)(edge.Key & 0xffffffff);
                    Assert.AreEqual(1f, water[i].y, "Interior open edge (crack)");
                    Assert.AreEqual(1f, water[j].y, "Interior open edge (crack)");
                }
            }
            foreach (Vector3 v in vertices)
            {
                Assert.IsFalse(float.IsNaN(v.x) || float.IsInfinity(v.x));
                Assert.GreaterOrEqual(v.magnitude, sampler.Sample(v.normalized).magnitude - 1e-3f, "Water below the displayed terrain");
            }
        }

        // 29, 28, 7
        [Test]
        public void LakeMeshes_AreValidContinuousAndOnTheDisplayedTerrain()
        {
            // Synthetic lake across the +X / +Y cube-face seam, on a displayed mesh of the same terrain.
            Vector3 seam = new Vector3(1f, 1f, 0.15f).normalized;
            Func<Vector3, float> terrain = d => 0.012f + 0.004f * d.x + Bowl(d, seam, 0.09f, 0.004f) - (d.x < -0.6f ? 0.03f : 0f);
            Analysis analysis = Analyze(terrain);
            int seamLake = LakeAt(analysis.Lakes, seam);
            Assert.GreaterOrEqual(seamLake, 0);
            const float radius = 100f;
            foreach (int meshResolution in new[] { 32, 128 })
            {
                PlanetSurfaceSampler syntheticSampler = Sampler(terrain, radius, meshResolution);
                var synthetic = new LakeMeshBuilder();
                synthetic.BuildGeometry(analysis.Lakes, terrain, radius, syntheticSampler);
                int start = synthetic.GetLakeVertexStart(seamLake), end = synthetic.GetLakeVertexStart(seamLake + 1);
                var faces = new HashSet<int>();
                for (int v = start; v < end; v++)
                {
                    Vector3 d = synthetic.Vertices[v].normalized;
                    faces.Add(Mathf.Abs(d.x) > Mathf.Abs(d.y) ? 3 : 0);
                }
                Assert.AreEqual(2, faces.Count, "The water surface spans both faces");
                AssertLakeMeshValid(synthetic, syntheticSampler);

                // Real planet, display mesh coarser and finer than the hydrology grid.
                LakeSet lakes = PlanetLakes(868497, 64, null, out PlanetInstance planet);
                PlanetSurfaceSampler sampler = Sampler(planet.Height.GetElevation, planet.Radius, meshResolution);
                var builder = new LakeMeshBuilder();
                builder.BuildGeometry(lakes, planet, sampler);
                AssertLakeMeshValid(builder, sampler);
                for (int lake = 0; lake < lakes.Count; lake++)
                {
                    Assert.Greater(builder.GetLakeVertexStart(lake + 1), builder.GetLakeVertexStart(lake), "Lake " + lake + " is drawn");
                }
            }
        }

        // 21
        [Test]
        public void VisibleRivers_NeverCrossALakeWithoutAnInteraction()
        {
            foreach (int seed in ReferenceSeeds)
            {
                LakeSet lakes = PlanetLakes(seed, 64, null, out PlanetInstance planet);
                RiverNetwork network = Network(lakes.Hydrology, 7e-4f);
                LakeRiverLayer layer = LakeRiverIntegration.Build(network, lakes);
                PlanetSurfaceSampler sampler = Sampler(planet, 64);
                var ribbons = new RiverRibbonBuilder();
                ribbons.BuildGeometry(network, sampler, planet, new RiverSettings(), layer);
                Assert.AreEqual(0, ribbons.LastDiagnostics.LakeInflowGaps, "Seed " + seed + ": inflows reach the water");
                IReadOnlyList<Vector3> vertices = ribbons.Vertices;
                for (int ribbon = 0; ribbon < ribbons.RibbonCount; ribbon++)
                {
                    RiverReach reach = layer.GetReach(ribbons.GetRibbonReach(ribbon));
                    for (int v = ribbons.GetRibbonStart(ribbon); v < ribbons.GetRibbonStart(ribbon + 1); v += 2)
                    {
                        Vector3 center = ((vertices[v] + vertices[v + 1]) * 0.5f).normalized;
                        for (int lake = 0; lake < lakes.Count; lake++)
                        {
                            if (LakeMeshBuilder.IsSubmerged(lakes, planet, lake, center))
                            {
                                // Either an explicit inflow / outflow of that lake, or a classified shore
                                // contact kept under the water surface (never drawn on top of a lake).
                                Assert.IsTrue(reach.StartLake == lake || reach.EndLake == lake || ribbons.IsVertexSubmerged(v),
                                    "Seed " + seed + ": river " + reach.River + " crosses lake " + lake + " without interaction");
                            }
                        }
                    }
                }
            }
        }

        // 22 (data side)
        [Test]
        public void RiverWidthAndDensity_DoNotTouchTheLakeAnalysis()
        {
            HydrologyResult hydrology = PlanetAnalysis(609538, 64, out PlanetInstance planet);
            var cache = new LakeCache();
            var context = new LakeContext(planet.Definition.seed, planet.Height.GetMountainInfluence);
            LakeSet lakes = cache.Get(hydrology, new LakeSettings(), context);
            // A different river threshold / width only rebuilds the layer and the ribbons.
            LakeRiverLayer dense = LakeRiverIntegration.Build(Network(hydrology, 2e-4f), lakes);
            LakeRiverLayer sparse = LakeRiverIntegration.Build(Network(hydrology, 2e-3f), lakes);
            Assert.AreSame(lakes, cache.Get(hydrology, new LakeSettings(), context));
            Assert.AreEqual(1, cache.AnalysisCount);
            Assert.AreEqual(1, cache.SelectionCount);
            Assert.Greater(dense.ReachCount, sparse.ReachCount);
            // Display fields never invalidate the selection.
            var display = new LakeSettings { showLakes = true, color = Color.red };
            Assert.AreSame(lakes, cache.Get(hydrology, display, context));
            Assert.AreEqual(1, cache.SelectionCount);
            // A new hydrology analysis (resolution / terrain) invalidates the candidates.
            HydrologyResult other = PlanetAnalysis(609538, 32, out _);
            cache.Get(other, new LakeSettings(), context);
            Assert.AreEqual(2, cache.AnalysisCount);
        }
    }
}
