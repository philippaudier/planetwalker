using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>V0.1.1C hydrology foundation: grid, Priority-Flood, flow, accumulation, basins.</summary>
    public class HydrologySystemTests
    {
        static readonly Dictionary<int, HydrologyGrid> Grids = new Dictionary<int, HydrologyGrid>();

        static HydrologyGrid Grid(int resolution)
        {
            if (!Grids.TryGetValue(resolution, out HydrologyGrid grid))
            {
                grid = new HydrologyGrid(resolution);
                Grids.Add(resolution, grid);
            }
            return grid;
        }

        static HydrologyResult ComputePlanet(PlanetDefinition definition, int resolution)
        {
            return new HydrologySystem().Compute(Grid(resolution), new PlanetInstance(definition));
        }

        /// <summary>
        /// Structural invariants every result must satisfy: finite values, every land cell in
        /// a basin, strictly decreasing corrected elevation downstream (hence no cycles), every
        /// path ending at a valid outlet, and exact local + global area conservation.
        /// </summary>
        static void AssertInvariants(HydrologyResult result)
        {
            HydrologyGrid grid = result.Grid;
            int count = grid.CellCount;
            var donorSum = new double[count];
            double terminalSum = 0;
            for (int cell = 0; cell < count; cell++)
            {
                float elevation = result.GetElevation(cell);
                Assert.IsFalse(float.IsNaN(elevation) || float.IsInfinity(elevation), "Elevation of cell " + cell);
                double filled = result.GetFilledElevation(cell);
                Assert.IsFalse(double.IsNaN(filled) || double.IsInfinity(filled));
                int receiver = result.GetReceiver(cell);
                if (result.IsLand(cell))
                {
                    Assert.GreaterOrEqual(filled, elevation, "Corrected surface is never below the terrain");
                    Assert.GreaterOrEqual(result.GetBasin(cell), 0, "Land cell without basin");
                    Assert.Less(result.GetBasin(cell), result.BasinCount);
                    if (cell == result.EndorheicOutlet)
                    {
                        Assert.AreEqual(-1, receiver);
                    }
                    else
                    {
                        Assert.GreaterOrEqual(receiver, 0, "Land cell without flow direction");
                        Assert.Less(result.GetFilledElevation(receiver), filled, "Flow must go strictly downhill");
                        Assert.IsTrue(IsNeighbor(grid, cell, receiver), "Flow must go to a neighbour");
                        // Same basin as its receiver unless it drains straight into the ocean.
                        if (result.IsLand(receiver))
                        {
                            Assert.AreEqual(result.GetBasin(receiver), result.GetBasin(cell));
                        }
                        donorSum[receiver] += result.GetAccumulation(cell);
                    }
                }
                else
                {
                    Assert.AreEqual(-1, receiver, "Ocean cells are outlets");
                    Assert.AreEqual(-1, result.GetBasin(cell));
                }
                if (receiver < 0)
                {
                    terminalSum += result.GetAccumulation(cell);
                }
            }
            for (int cell = 0; cell < count; cell++)
            {
                // Local conservation: own area plus exactly what flows in (nothing twice).
                double expected = grid.GetArea(cell) + donorSum[cell];
                Assert.AreEqual(expected, result.GetAccumulation(cell), 1e-9 * Math.Max(1.0, expected), "Cell " + cell);
            }
            // Global conservation: all area ends at an outlet exactly once.
            Assert.AreEqual(grid.TotalArea, terminalSum, 1e-9 * grid.TotalArea);
        }

        static bool IsNeighbor(HydrologyGrid grid, int cell, int other)
        {
            for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
            {
                if (grid.GetNeighbor(k) == other)
                {
                    return true;
                }
            }
            return false;
        }

        static int OutletOf(HydrologyResult result, int cell)
        {
            int steps = 0;
            while (result.GetReceiver(cell) >= 0)
            {
                cell = result.GetReceiver(cell);
                Assert.Less(++steps, result.Grid.CellCount, "Cycle detected");
            }
            return cell;
        }

        // ---------------------------------------------------------------- Planet tests

        // 1
        [Test]
        public void SameSeedAndParameters_ProduceIdenticalHydrology()
        {
            var definition = new PlanetDefinition { seed = 609538 };
            HydrologyResult a = new HydrologySystem().Compute(new HydrologyGrid(64), new PlanetInstance(definition));
            HydrologyResult b = new HydrologySystem().Compute(new HydrologyGrid(64), new PlanetInstance(definition.Clone()));
            Assert.AreEqual(a.BasinCount, b.BasinCount);
            Assert.AreEqual(a.DepressionCount, b.DepressionCount);
            for (int cell = 0; cell < a.Grid.CellCount; cell++)
            {
                Assert.AreEqual(a.GetReceiver(cell), b.GetReceiver(cell));
                Assert.AreEqual(a.GetBasin(cell), b.GetBasin(cell));
                Assert.AreEqual(a.GetAccumulation(cell), b.GetAccumulation(cell), 0.0);
                Assert.AreEqual(a.GetFilledElevation(cell), b.GetFilledElevation(cell), 0.0);
            }
        }

        // 2, 3, 4, 5, 6 on a real planet
        [Test]
        public void DefaultPlanet_SatisfiesAllHydrologyInvariants()
        {
            HydrologyResult result = ComputePlanet(new PlanetDefinition(), 64);
            Assert.Greater(result.LandCellCount, 0);
            Assert.Greater(result.BasinCount, 0);
            AssertInvariants(result);
            for (int cell = 0; cell < result.Grid.CellCount; cell++)
            {
                if (result.IsLand(cell))
                {
                    Assert.IsFalse(result.IsLand(OutletOf(result, cell)), "Planet with oceans drains to the ocean");
                }
            }
        }

        // 7
        [Test]
        public void Grid_NeighborhoodsAreSymmetricAndContinuousAcrossFaces()
        {
            HydrologyGrid grid = Grid(32);
            Assert.AreEqual(6 * 32 * 32, grid.CellCount);
            Assert.AreEqual(4.0 * Math.PI, grid.TotalArea, 1e-4);
            // Typical spacing ~ (pi / 2) / n; diagonal neighbours on stretched cells stay well within 3x.
            float maxDistance = 3f * Mathf.PI * 0.5f / grid.Resolution;
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                int degree = grid.GetNeighborEnd(cell) - grid.GetNeighborStart(cell);
                Assert.IsTrue(degree == 7 || degree == 8, $"Cell {cell} has {degree} neighbours");
                grid.GetFaceCoordinates(cell, out int face, out _, out _);
                for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                {
                    int other = grid.GetNeighbor(k);
                    Assert.IsTrue(IsNeighbor(grid, other, cell), "Neighbourhood must be symmetric");
                    Assert.Less(grid.GetNeighborDistance(k), maxDistance);
                }
                Assert.Greater(grid.GetArea(cell), 0f);
                Assert.AreEqual(cell, grid.CellFromDirection(grid.GetCenter(cell)), "Direction -> cell inverse");
            }
            // Every cell on a face border has neighbours on another face.
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                for (int t = 0; t < grid.Resolution; t++)
                {
                    foreach (int cell in new[] { grid.Index(face, 0, t), grid.Index(face, grid.Resolution - 1, t), grid.Index(face, t, 0), grid.Index(face, t, grid.Resolution - 1) })
                    {
                        bool crosses = false;
                        for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell); k++)
                        {
                            grid.GetFaceCoordinates(grid.GetNeighbor(k), out int otherFace, out _, out _);
                            crosses |= otherFace != face;
                        }
                        Assert.IsTrue(crosses, "Border cell " + cell + " must connect to the adjacent face");
                    }
                }
            }
        }

        // 8
        [Test]
        public void CubeCorners_AreConnectedAndDrainContinuously()
        {
            HydrologyGrid grid = Grid(32);
            int n = grid.Resolution;
            int cornerCells = 0;
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                if (grid.GetNeighborEnd(cell) - grid.GetNeighborStart(cell) == 7)
                {
                    cornerCells++;
                    grid.GetFaceCoordinates(cell, out _, out int i, out int j);
                    Assert.IsTrue((i == 0 || i == n - 1) && (j == 0 || j == n - 1), "Only corner cells have 7 neighbours");
                }
            }
            Assert.AreEqual(24, cornerCells, "8 cube corners x 3 faces");

            // The 3 cells meeting at a cube corner are mutual neighbours.
            Vector3 cornerDirection = new Vector3(1f, 1f, 1f).normalized;
            var atCorner = new List<int>();
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                atCorner.Add(cell);
            }
            atCorner.Sort((x, y) => Vector3.Dot(grid.GetCenter(y), cornerDirection).CompareTo(Vector3.Dot(grid.GetCenter(x), cornerDirection)));
            for (int k = 0; k < 3; k++)
            {
                Assert.AreEqual(7, grid.GetNeighborEnd(atCorner[k]) - grid.GetNeighborStart(atCorner[k]), "The 3 cells nearest a corner are corner cells");
            }
            Assert.IsTrue(IsNeighbor(grid, atCorner[0], atCorner[1]) && IsNeighbor(grid, atCorner[1], atCorner[2]) && IsNeighbor(grid, atCorner[0], atCorner[2]));

            // A smooth slope descending toward a small ocean just beside the corner: the corner
            // cells are land and drain across face boundaries without artificial pits.
            Vector3 target = new Vector3(1f, 1f, 0.6f).normalized;
            HydrologyResult result = new HydrologySystem().Compute(grid,
                d => 0.02f * (1f - Vector3.Dot(d, target)) - 0.0002f, 0f);
            AssertInvariants(result);
            Assert.AreEqual(0, result.DepressionCount);
            Assert.AreEqual(-1, result.EndorheicOutlet);
            for (int k = 0; k < 3; k++)
            {
                Assert.IsTrue(result.IsLand(atCorner[k]), "Corner cells are on the slope");
            }
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                if (result.IsLand(cell))
                {
                    Assert.Less(result.GetElevation(result.GetReceiver(cell)), result.GetElevation(cell));
                }
            }
        }

        // 9
        [Test]
        public void AlmostOceanicPlanet_Works()
        {
            HydrologyResult result = ComputePlanet(new PlanetDefinition { seed = 42, seaLevel = PlanetDefinition.MaxSeaLevel }, 64);
            Assert.Less(result.LandCellCount, result.Grid.CellCount / 10);
            AssertInvariants(result);
        }

        // 10
        [Test]
        public void VeryContinentalPlanet_Works()
        {
            HydrologyResult result = ComputePlanet(new PlanetDefinition { seed = 42, seaLevel = PlanetDefinition.MinSeaLevel }, 64);
            Assert.Greater(result.LandCellCount, result.Grid.CellCount / 2);
            AssertInvariants(result);

            // No ocean at all: everything drains to a single endorheic outlet.
            HydrologyResult dry = new HydrologySystem().Compute(Grid(32), d => 0.01f + 0.005f * d.x, -1f);
            AssertInvariants(dry);
            Assert.GreaterOrEqual(dry.EndorheicOutlet, 0);
            Assert.AreEqual(1, dry.BasinCount);
            Assert.AreEqual(dry.Grid.TotalArea, dry.GetAccumulation(dry.EndorheicOutlet), 1e-9 * dry.Grid.TotalArea);
        }

        // 11
        [Test]
        public void ClosedDepressions_AreFilledAndDrained()
        {
            HydrologyResult planet = ComputePlanet(new PlanetDefinition(), 64);
            Assert.Greater(planet.DepressionCount, 0, "Noise terrain has closed depressions");

            // Synthetic: a crater (rim higher than its floor) on a plateau sloping to an ocean.
            Vector3 center = new Vector3(0f, 0.6f, 0.8f).normalized;
            Func<Vector3, float> crater = d =>
            {
                float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, center), -1f, 1f));
                float rim = 0.01f * Mathf.Exp(-Mathf.Pow((angle - 0.3f) / 0.08f, 2f));
                return 0.02f + 0.01f * d.y + rim - 0.008f * Mathf.Exp(-Mathf.Pow(angle / 0.2f, 2f));
            };
            HydrologyResult result = new HydrologySystem().Compute(Grid(48), crater, 0.015f);
            AssertInvariants(result);
            Assert.GreaterOrEqual(result.DepressionCount, 1);
            int floor = result.Grid.CellFromDirection(center);
            Assert.IsTrue(result.IsInDepression(floor), "Crater floor is filled on the corrected surface");
            Assert.Greater(result.GetFilledElevation(floor), result.GetElevation(floor) + 0.001);
            Assert.IsFalse(result.IsLand(OutletOf(result, floor)), "Crater water spills to the ocean");
        }

        // 12
        [Test]
        public void ChangingResolution_PreservesLargeBasins()
        {
            // Rationale: the grid is a discretization of the same continuous terrain, so the
            // dominant basins must be the same regions. Measured on the validation seeds, 77-98%
            // of a large basin maps onto one basin at the other resolution; the exceptions are
            // divide captures (two basins separated by a near-flat divide merge or split, a
            // known D8 effect). Hence: area-weighted mean share >= 75% over the 5 largest
            // basins, and no basin below 45%.
            var definition = new PlanetDefinition { seed = 280695 };
            HydrologyResult fine = ComputePlanet(definition, 128);
            HydrologyResult coarse = ComputePlanet(definition, 64);
            var basinsBySize = new List<int>();
            for (int b = 0; b < fine.BasinCount; b++)
            {
                basinsBySize.Add(b);
            }
            basinsBySize.Sort((x, y) => fine.GetBasinArea(y).CompareTo(fine.GetBasinArea(x)));

            double weightedShare = 0, weight = 0;
            for (int rank = 0; rank < 5; rank++)
            {
                int basin = basinsBySize[rank];
                var overlap = new Dictionary<int, double>();
                for (int cell = 0; cell < fine.Grid.CellCount; cell++)
                {
                    if (fine.GetBasin(cell) != basin)
                    {
                        continue;
                    }
                    int coarseBasin = coarse.GetBasin(coarse.Grid.CellFromDirection(fine.Grid.GetCenter(cell)));
                    overlap.TryGetValue(coarseBasin, out double area);
                    overlap[coarseBasin] = area + fine.Grid.GetArea(cell);
                }
                int dominant = -1;
                double dominantArea = 0;
                foreach (KeyValuePair<int, double> pair in overlap)
                {
                    if (pair.Key >= 0 && pair.Value > dominantArea)
                    {
                        dominant = pair.Key;
                        dominantArea = pair.Value;
                    }
                }
                double share = dominantArea / fine.GetBasinArea(basin);
                Assert.GreaterOrEqual(share, 0.45, $"Basin rank {rank}");
                weightedShare += share * fine.GetBasinArea(basin);
                weight += fine.GetBasinArea(basin);
            }
            Assert.GreaterOrEqual(weightedShare / weight, 0.75);
        }

        // 13
        [Test]
        public void RiverThreshold_DoesNotTriggerRecompute()
        {
            var cache = new HydrologyCache();
            var definition = new PlanetDefinition { resolution = 16 };
            HydrologyResult result = cache.Get(new PlanetInstance(definition), 64);
            Assert.AreEqual(1, cache.ComputeCount);

            // The threshold is evaluated on the cached result.
            int dense = 0, sparse = 0;
            for (int cell = 0; cell < result.Grid.CellCount; cell++)
            {
                dense += result.IsRiverCandidate(cell, 1e-5f) ? 1 : 0;
                sparse += result.IsRiverCandidate(cell, 1e-3f) ? 1 : 0;
            }
            Assert.Greater(dense, sparse);
            Assert.Greater(sparse, 0);

            // Render-only changes (mesh resolution, radius) reuse the analysis...
            var renderOnly = definition.Clone();
            renderOnly.resolution = 128;
            renderOnly.radius = 250f;
            Assert.AreSame(result, cache.Get(new PlanetInstance(renderOnly), 64));
            Assert.AreEqual(1, cache.ComputeCount);

            // ...while terrain or grid changes recompute it.
            var otherTerrain = definition.Clone();
            otherTerrain.seed += 1;
            cache.Get(new PlanetInstance(otherTerrain), 64);
            Assert.AreEqual(2, cache.ComputeCount);
            cache.Get(new PlanetInstance(otherTerrain), 32);
            Assert.AreEqual(3, cache.ComputeCount);
        }

        [Test]
        public void TerrainEquals_CoversEveryTerrainField()
        {
            // Guards the hydrology cache: any PlanetDefinition field except the rendering-only
            // radius and resolution must invalidate it.
            foreach (FieldInfo field in typeof(PlanetDefinition).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var a = new PlanetDefinition();
                var b = new PlanetDefinition();
                if (field.FieldType == typeof(int))
                {
                    field.SetValue(b, (int)field.GetValue(a) + 1);
                }
                else if (field.FieldType == typeof(float))
                {
                    field.SetValue(b, (float)field.GetValue(a) + 0.001f);
                }
                else if (field.FieldType == typeof(bool))
                {
                    field.SetValue(b, !(bool)field.GetValue(a));
                }
                else
                {
                    Assert.Fail("Unhandled field type " + field.FieldType + " for " + field.Name);
                }
                bool renderingOnly = field.Name == nameof(PlanetDefinition.radius) || field.Name == nameof(PlanetDefinition.resolution);
                Assert.AreEqual(renderingOnly, a.TerrainEquals(b), field.Name);
            }
        }

        // 14
        [Test]
        public void Hydrology_DoesNotModifyTheTerrain()
        {
            var planet = new PlanetInstance(new PlanetDefinition { seed = 868497 });
            var directions = new Vector3[500];
            var before = new float[directions.Length];
            for (int i = 0; i < directions.Length; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / directions.Length;
                float r = Mathf.Sqrt(1f - y * y);
                directions[i] = new Vector3(r * Mathf.Cos(i * 2.4f), y, r * Mathf.Sin(i * 2.4f));
                before[i] = planet.GetElevation(directions[i]);
            }
            HydrologyResult result = new HydrologySystem().Compute(Grid(64), planet);
            for (int i = 0; i < directions.Length; i++)
            {
                Assert.AreEqual(before[i], planet.GetElevation(directions[i]), 0f);
            }
            // The stored real elevation is the terrain itself, kept separate from the corrected one.
            for (int cell = 0; cell < result.Grid.CellCount; cell += 97)
            {
                // The legacy world's surface (V0.2.0C): band-limited to the grid and the face meshes.
                Assert.AreEqual(planet.GetLegacyElevation(result.Grid.GetCenter(cell), Mathf.PI * 0.5f / result.Grid.Resolution), result.GetElevation(cell), 0f);
            }
        }

        // ---------------------------------------------------------------- Rendering (Unity)

        GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
                root = null;
            }
        }

        // 13 (rendering side)
        [Test]
        public void ThresholdChange_OnlyRecolors()
        {
            root = new GameObject("Hydrology Test");
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
            visual.DebugView = PlanetDebugView.RiverCandidates;
            visual.Render(new PlanetInstance(new PlanetDefinition { resolution = 32 }));
            Assert.AreEqual(1, visual.HydrologyCache.ComputeCount);
            Mesh mesh = root.transform.Find("Terrain +Y").GetComponent<MeshFilter>().sharedMesh;
            Vector3[] vertices = mesh.vertices;
            Color[] colors = mesh.colors;

            visual.Hydrology.riverNetworkThreshold = HydrologySettings.MinRiverNetworkThreshold;
            visual.RefreshColors();

            Assert.AreEqual(1, visual.HydrologyCache.ComputeCount, "No hydrology recompute");
            CollectionAssert.AreEqual(vertices, mesh.vertices, "Terrain geometry is untouched");
            CollectionAssert.AreNotEqual(colors, mesh.colors, "Denser river network is displayed");
        }

        // 15
        [Test]
        public void RepeatedRegeneration_WithHydrology_DoesNotLeak()
        {
            root = new GameObject("Hydrology Test");
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
            var definition = new PlanetDefinition { resolution = 16 };
            visual.DebugView = PlanetDebugView.DrainageBasins;
            visual.Render(new PlanetInstance(definition));
            int meshCount = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int gameObjectCount = Resources.FindObjectsOfTypeAll<GameObject>().Length;
            int materialCount = Resources.FindObjectsOfTypeAll<Material>().Length;

            PlanetDebugView[] views =
            {
                PlanetDebugView.RiverCandidates, PlanetDebugView.FlowAccumulation, PlanetDebugView.FlowDirection,
                PlanetDebugView.None, PlanetDebugView.DrainageBasins, PlanetDebugView.MountainRanges,
            };
            for (int i = 0; i < views.Length; i++)
            {
                definition.seed = 300 + i;
                visual.DebugView = views[i];
                visual.Hydrology.riverNetworkThreshold = 1e-4f * (i + 1);
                visual.Render(new PlanetInstance(definition));
                visual.RefreshColors();
            }

            Assert.AreEqual(7, visual.GeneratedObjectCount);
            Assert.AreEqual(meshCount, Resources.FindObjectsOfTypeAll<Mesh>().Length);
            Assert.AreEqual(gameObjectCount, Resources.FindObjectsOfTypeAll<GameObject>().Length);
            Assert.AreEqual(materialCount, Resources.FindObjectsOfTypeAll<Material>().Length);
            // One analysis per distinct terrain shown in a hydrology view (None/MountainRanges skip it).
            Assert.AreEqual(1 + 4, visual.HydrologyCache.ComputeCount);

            MeshRenderer terrain = root.transform.Find("Terrain +Y").GetComponent<MeshRenderer>();
            Material debugMaterial = terrain.sharedMaterial;
            UnityEngine.Object.DestroyImmediate(root);
            root = null;
            Assert.IsTrue(debugMaterial == null, "Runtime debug material is released with the planet");
        }

        // ---------------------------------------------------------------- Synthetic tests

        [Test]
        public void Synthetic_UniformSlope_FlowsDownhillWithoutDepressions()
        {
            // Elevation rises with y; ocean below y = -0.6.
            HydrologyResult result = new HydrologySystem().Compute(Grid(32), d => 0.01f * (d.y + 0.6f), 0f);
            AssertInvariants(result);
            Assert.AreEqual(0, result.DepressionCount);
            for (int cell = 0; cell < result.Grid.CellCount; cell++)
            {
                if (!result.IsLand(cell))
                {
                    continue;
                }
                int receiver = result.GetReceiver(cell);
                Assert.Less(result.GetElevation(receiver), result.GetElevation(cell), "Downhill on a uniform slope");
                Assert.AreEqual(result.GetElevation(cell), result.GetFilledElevation(cell), 1e-12, "No correction needed");
            }
        }

        [Test]
        public void Synthetic_FlatTerrain_DrainsWithoutCycles()
        {
            // Perfectly flat land with one ocean cap: flats are resolved toward the outlet.
            HydrologyResult result = new HydrologySystem().Compute(Grid(32), d => d.y < -0.8f ? -0.01f : 0.01f, 0f);
            AssertInvariants(result);
            Assert.AreEqual(1, result.BasinCount > 0 ? 1 : 0);
            int outlet = -1;
            for (int cell = 0; cell < result.Grid.CellCount; cell++)
            {
                if (result.IsLand(cell))
                {
                    int reached = OutletOf(result, cell);
                    Assert.IsFalse(result.IsLand(reached));
                    Assert.Less(result.Grid.GetCenter(reached).y, -0.7f, "Drains to the only ocean");
                    outlet = reached;
                }
            }
            Assert.GreaterOrEqual(outlet, 0);
        }

        [Test]
        public void Synthetic_MultipleOutlets_SplitBasinsAtTheDivide()
        {
            // Two polar oceans, highest along the equator: north drains north, south drains south.
            HydrologyResult result = new HydrologySystem().Compute(Grid(32), d => 0.02f * (0.8f - Mathf.Abs(d.y)), 0f);
            AssertInvariants(result);
            for (int cell = 0; cell < result.Grid.CellCount; cell++)
            {
                if (!result.IsLand(cell))
                {
                    continue;
                }
                float y = result.Grid.GetCenter(cell).y;
                if (Mathf.Abs(y) < 0.05f)
                {
                    continue;
                }
                float outletY = result.Grid.GetCenter(OutletOf(result, cell)).y;
                Assert.AreEqual(Mathf.Sign(y), Mathf.Sign(outletY), "Cell drains to the ocean of its hemisphere");
            }
        }

        [Test]
        public void Synthetic_Bowl_FillsToItsSpillPoint()
        {
            // A closed bowl around the north pole whose rim (0.02) is cut by a single valley
            // (bottom 0.008) running across it toward +x.
            Func<Vector3, float> bowl = d =>
            {
                float valley = d.x > 0f ? Mathf.Exp(-(d.z * d.z) / 0.003f) : 0f;
                float rim = 0.02f - 0.012f * valley;
                float floor = 0.005f;
                return d.y > 0.8f ? floor : d.y > 0.6f ? rim : -0.01f;
            };
            HydrologyResult result = new HydrologySystem().Compute(Grid(48), bowl, 0f);
            AssertInvariants(result);
            int pole = result.Grid.CellFromDirection(Vector3.up);
            Assert.IsTrue(result.IsInDepression(pole));
            // The bowl fills up to (just above) the lowest rim height, the notch, not the high rim.
            // The valley bottom is 0.008 at z = 0; grid cells sample slightly above it.
            Assert.Greater(result.GetFilledElevation(pole), 0.0079);
            Assert.Less(result.GetFilledElevation(pole), 0.011);
            Assert.IsFalse(result.IsLand(OutletOf(result, pole)));
        }
    }
}
