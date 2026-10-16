using System;
using System.Collections.Generic;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// V0.1.1D-B.2: terrain-conforming lake shorelines and river / lake transitions. Letters in
    /// comments refer to the milestone's required test list (A - O).
    /// </summary>
    public class LakeShorelineTests
    {
        const float Threshold = 7e-4f;
        static readonly int[] ReferenceSeeds = { 868497, 609538, 280695, 554492, 335559 };

        // ------------------------------------------------------------ Scenes (cached)

        sealed class Scene
        {
            public PlanetInstance Planet;
            public LakeSet Lakes;
            public RiverNetwork Network;
            public LakeRiverLayer Layer;
            public PlanetSurfaceSampler Sampler;
            public LakeMeshBuilder Water;
            public RiverRibbonBuilder Ribbons;
        }

        static readonly Dictionary<(int, int), HydrologyResult> Hydrology = new Dictionary<(int, int), HydrologyResult>();
        static readonly Dictionary<(int, int), PlanetSurfaceSampler> Samplers = new Dictionary<(int, int), PlanetSurfaceSampler>();
        static readonly Dictionary<(int, int, int), Scene> Scenes = new Dictionary<(int, int, int), Scene>();

        static HydrologyResult PlanetHydrology(int seed, int resolution)
        {
            if (!Hydrology.TryGetValue((seed, resolution), out HydrologyResult result))
            {
                result = new HydrologySystem().Compute(new HydrologyGrid(resolution), new PlanetInstance(new PlanetDefinition { seed = seed }));
                Hydrology.Add((seed, resolution), result);
            }
            return result;
        }

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

        static RiverNetwork Network(HydrologyResult hydrology)
        {
            var system = new RiverNetworkSystem();
            RiverNetwork network = system.Build(hydrology, Threshold);
            system.BuildPaths(network, 2);
            return network;
        }

        static Scene Build(int seed, int hydrologyResolution, int terrainResolution)
        {
            if (Scenes.TryGetValue((seed, hydrologyResolution, terrainResolution), out Scene scene))
            {
                return scene;
            }
            var planet = new PlanetInstance(new PlanetDefinition { seed = seed, resolution = terrainResolution });
            HydrologyResult hydrology = PlanetHydrology(seed, hydrologyResolution);
            LakeSet lakes = new LakeCache().Get(hydrology, new LakeSettings(), new LakeContext(seed, planet.Height.GetMountainInfluence));
            if (!Samplers.TryGetValue((seed, terrainResolution), out PlanetSurfaceSampler sampler))
            {
                sampler = Sampler(planet.GetLegacyElevation, planet.Radius, terrainResolution);
                Samplers.Add((seed, terrainResolution), sampler);
            }
            RiverNetwork network = Network(hydrology);
            LakeRiverLayer layer = LakeRiverIntegration.Build(network, lakes);
            var water = new LakeMeshBuilder();
            water.BuildGeometry(lakes, planet, sampler);
            var ribbons = new RiverRibbonBuilder();
            ribbons.BuildGeometry(network, sampler, planet, new RiverSettings(), layer, water);
            scene = new Scene
            {
                Planet = planet, Lakes = lakes, Network = network, Layer = layer, Sampler = sampler, Water = water, Ribbons = ribbons,
            };
            Scenes.Add((seed, hydrologyResolution, terrainResolution), scene);
            return scene;
        }

        static readonly (int Hydrology, int Terrain)[] ResolutionMatrix = { (64, 256), (128, 256), (256, 256), (128, 64) };

        // ------------------------------------------------------------ Shared assertions

        /// <summary>Every vertex on the lake's water sphere, radial normals, valid outward triangles (A, C, H, I).</summary>
        static void AssertWaterSurface(Scene scene, string context)
        {
            LakeMeshBuilder water = scene.Water;
            float radius = scene.Planet.Radius;
            for (int lake = 0; lake < scene.Lakes.Count; lake++)
            {
                float waterRadius = radius * (1f + scene.Lakes.GetLake(lake).Level);
                for (int v = water.GetLakeVertexStart(lake); v < water.GetLakeVertexStart(lake + 1); v++)
                {
                    Vector3 p = water.Vertices[v];
                    Assert.AreEqual(waterRadius, p.magnitude, 2e-6f * radius, $"{context}: lake {lake} vertex {v} off the water level");
                    Assert.Less((water.Normals[v] - p.normalized).magnitude, 1e-5f, $"{context}: non-radial normal");
                }
                for (int t = water.GetLakeTriangleStart(lake); t < water.GetLakeTriangleStart(lake + 1); t += 3)
                {
                    int ia = water.Triangles[t], ib = water.Triangles[t + 1], ic = water.Triangles[t + 2];
                    Assert.IsTrue(ia >= water.GetLakeVertexStart(lake) && ia < water.GetLakeVertexStart(lake + 1), "Triangle of its own lake");
                    Vector3 a = water.Vertices[ia], b = water.Vertices[ib], c = water.Vertices[ic];
                    Vector3 normal = Vector3.Cross(b - a, c - a);
                    Assert.Greater(normal.magnitude, 0f, $"{context}: degenerate lake triangle");
                    Assert.Greater(Vector3.Dot(normal, a + b + c), 0f, $"{context}: inverted lake triangle");
                    // Explicit cosine (Vector3.normalized returns zero below 1e-5: small triangles).
                    Vector3 sum = a + b + c;
                    double cosine = Vector3.Dot(normal, sum) / ((double)normal.magnitude * sum.magnitude);
                    Assert.Greater(cosine, 0.5, $"{context}: steep (non-level) lake triangle");
                }
            }
            LakeMeshDiagnostics d = water.LastDiagnostics;
            Assert.Less(d.MaxLevelDeviation, 2e-6f, context);
            Assert.AreEqual(0, d.TerrainIntersectionWarnings, $"{context}: water below the displayed terrain");
            Assert.AreEqual(0, d.CrackEdges, $"{context}: open edge inside the water");
            Assert.AreEqual(0, d.SeamMismatches, $"{context}: seam crack");
            Assert.AreEqual(0, d.NonManifoldEdges, $"{context}: overlapping water patches");
        }

        static void AssertRibbonTrianglesValid(RiverRibbonBuilder ribbons, string context)
        {
            for (int t = 0; t < ribbons.Triangles.Count; t += 3)
            {
                Vector3 a = ribbons.Vertices[ribbons.Triangles[t]], b = ribbons.Vertices[ribbons.Triangles[t + 1]], c = ribbons.Vertices[ribbons.Triangles[t + 2]];
                Assert.Greater(Vector3.Cross(b - a, c - a).magnitude, 0f, $"{context}: degenerate ribbon triangle");
            }
        }

        static bool HasTriangleBetween(RiverRibbonBuilder ribbons, int pairA, int pairB)
        {
            for (int t = 0; t < ribbons.Triangles.Count; t += 3)
            {
                bool a = false, b = false;
                for (int e = 0; e < 3; e++)
                {
                    int v = ribbons.Triangles[t + e];
                    a |= v == pairA || v == pairA + 1;
                    b |= v == pairB || v == pairB + 1;
                }
                if (a && b)
                {
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------ A, B, C

        // A
        [Test]
        public void A_ShorelineVertices_LieOnTheWaterLevelAndOnTheDisplayedTerrain()
        {
            Scene scene = Build(868497, 128, 256);
            LakeMeshBuilder water = scene.Water;
            Assert.Greater(water.LastDiagnostics.ShorelineVertices, 100);
            AssertWaterSurface(scene, "868497 128/256");
            int checkedVertices = 0;
            for (int s = 0; s < water.BoundarySegmentCount; s++)
            {
                water.GetBoundarySegment(s, out int a, out int b, out bool cut);
                if (cut)
                {
                    continue;
                }
                foreach (int v in new[] { a, b })
                {
                    Vector3 p = water.Vertices[v];
                    Vector3 terrain = scene.Sampler.Sample(p.normalized);
                    Assert.AreEqual(terrain.magnitude, p.magnitude, 2e-5f * scene.Planet.Radius, "Shoreline vertex on the displayed terrain");
                    checkedVertices++;
                }
            }
            Assert.Greater(checkedVertices, 0);
            Assert.Greater(water.LastDiagnostics.ShorelineLength, 0f);
        }

        // B
        [Test]
        public void B_TerrainEdgeIntersection_MatchesTheExpectedInterpolatedPoint()
        {
            const float waterRadius = 100f;
            // Radial edge: the crossing is where the radius equals the water radius.
            Vector3 d = new Vector3(0.3f, 0.8f, 0.52f).normalized;
            Vector3 crossing = LakeMeshBuilder.ShorelineDirection(d * 99f, d * 102f, waterRadius);
            Assert.Less((crossing - d).magnitude, 1e-6f);

            // Synthetic terrain triangle edges crossing the level: exact point on the edge, at the level.
            var random = new System.Random(7);
            for (int i = 0; i < 200; i++)
            {
                Vector3 wetDirection = RandomDirection(random);
                Vector3 dryDirection = (wetDirection + RandomDirection(random) * 0.02f).normalized;
                Vector3 wet = wetDirection * (waterRadius * (1f - 0.002f * (float)random.NextDouble() - 1e-5f));
                Vector3 dry = dryDirection * (waterRadius * (1f + 0.002f * (float)random.NextDouble() + 1e-5f));
                Vector3 point = LakeMeshBuilder.ShorelineDirection(wet, dry, waterRadius) * waterRadius;

                // On the edge: collinear with it, between both ends.
                Vector3 edge = dry - wet;
                float t = Vector3.Dot(point - wet, edge) / edge.sqrMagnitude;
                Assert.That(t, Is.InRange(-1e-5f, 1f + 1e-5f));
                Assert.Less((wet + edge * t - point).magnitude, 2e-4f, "Point on the terrain edge");

                // Expected interpolation: the radius varies along the edge, |wet + t edge| = R.
                Assert.AreEqual(waterRadius, (wet + edge * t).magnitude, 2e-4f);
                // Independent reference: bisection of |wet + s edge| = R in double precision.
                double lo = 0.0, hi = 1.0;
                for (int k = 0; k < 60; k++)
                {
                    double mid = 0.5 * (lo + hi);
                    double x = wet.x + mid * edge.x, y = wet.y + mid * edge.y, z = wet.z + mid * edge.z;
                    if (Math.Sqrt(x * x + y * y + z * z) < waterRadius)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }
                Assert.AreEqual(0.5 * (lo + hi), t, 1e-4, "Interpolated position along the edge");
            }
        }

        static Vector3 RandomDirection(System.Random random)
        {
            return new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f).normalized;
        }

        // C
        [Test]
        public void C_InteriorWater_StaysOnTheConstantRadiusSurface_EvenOnACoarseMesh()
        {
            // The configuration where V0.1.1D-B.1 raised 11-24% of the interior vertices.
            Scene scene = Build(868497, 128, 64);
            LakeMeshBuilder water = scene.Water;
            int interior = 0;
            for (int lake = 0; lake < scene.Lakes.Count; lake++)
            {
                float waterRadius = scene.Planet.Radius * (1f + scene.Lakes.GetLake(lake).Level);
                for (int v = water.GetLakeVertexStart(lake); v < water.GetLakeVertexStart(lake + 1); v++)
                {
                    if (water.WaterAttributes[v].y > 0.5f)
                    {
                        continue;
                    }
                    interior++;
                    Assert.AreEqual(waterRadius, water.Vertices[v].magnitude, 2e-6f * scene.Planet.Radius, "No artificial water relief");
                    // Interior vertices are wet displayed vertices: the terrain is below them.
                    Assert.Less(scene.Sampler.Sample(water.Vertices[v].normalized).magnitude, waterRadius + 1e-4f);
                }
            }
            Assert.Greater(interior, 0);
            Assert.LessOrEqual(interior, water.LastDiagnostics.InteriorVertices, "Interior vertices (some wet vertices at the level are shoreline points)");
            AssertWaterSurface(scene, "868497 128/64");
        }

        // ------------------------------------------------------------ D

        // D
        [Test]
        public void D_Water_NeverFloodsLowTerrainOutsideTheLakeRegion()
        {
            int unrelatedLow = 0;
            foreach (int seed in new[] { 868497, 609538 })
            {
                Scene scene = Build(seed, 64, 256);
                LakeSet lakes = scene.Lakes;
                HydrologyGrid grid = lakes.Hydrology.Grid;
                LakeMeshBuilder water = scene.Water;
                for (int lake = 0; lake < lakes.Count; lake++)
                {
                    for (int v = water.GetLakeVertexStart(lake); v < water.GetLakeVertexStart(lake + 1); v++)
                    {
                        Vector3 d = water.Vertices[v].normalized;
                        int cell = grid.CellFromDirection(d);
                        if (water.WaterAttributes[v].y < 0.5f)
                        {
                            Assert.IsTrue(LakeMeshBuilder.IsInLakeRegion(lakes, lake, cell),
                                $"Seed {seed}: water of lake {lake} outside its footprint / shore ring");
                        }
                        else
                        {
                            // Boundary vertices lie in the region or on its border (adjacent cell).
                            bool near = LakeMeshBuilder.IsInLakeRegion(lakes, lake, cell);
                            for (int k = grid.GetNeighborStart(cell); k < grid.GetNeighborEnd(cell) && !near; k++)
                            {
                                near = LakeMeshBuilder.IsInLakeRegion(lakes, lake, grid.GetNeighbor(k));
                            }
                            Assert.IsTrue(near, $"Seed {seed}: boundary of lake {lake} away from its region");
                        }
                    }
                }
                // The constraint is exercised: low displayed terrain outside the lake regions exists
                // near the lakes (a global level threshold would have flooded it).
                for (int lake = 0; lake < lakes.Count; lake++)
                {
                    Lake data = lakes.GetLake(lake);
                    float spread = Mathf.Sqrt((float)data.Area) * 2f;
                    for (int i = 0; i < 64; i++)
                    {
                        float a = i / 64f * Mathf.PI * 2f;
                        Vector3 tangent = Vector3.Cross(data.Centroid, Mathf.Abs(data.Centroid.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                        Vector3 bitangent = Vector3.Cross(data.Centroid, tangent);
                        Vector3 d = (data.Centroid + (tangent * Mathf.Cos(a) + bitangent * Mathf.Sin(a)) * spread).normalized;
                        float height = scene.Sampler.Sample(d).magnitude / scene.Planet.Radius - 1f;
                        if (height < data.Level && !LakeMeshBuilder.IsInLakeRegion(lakes, lake, grid.CellFromDirection(d)))
                        {
                            unrelatedLow++;
                            Assert.IsFalse(water.IsUnderWater(lake, d), $"Seed {seed}: unrelated low terrain flooded by lake {lake}");
                        }
                    }
                }
            }
            Assert.Greater(unrelatedLow, 0, "Low-lying terrain outside the lake regions was tested");
        }

        // ------------------------------------------------------------ E, F, G

        // E
        [Test]
        public void E_RiverEntrances_CrossTheRenderedShorelineWithoutGap()
        {
            int transitions = 0;
            foreach (int seed in ReferenceSeeds)
            {
                foreach ((int hydrology, int terrain) in new[] { (64, 64), (128, 256) })
                {
                    Scene scene = Build(seed, hydrology, terrain);
                    RiverRibbonBuilder ribbons = scene.Ribbons;
                    string context = $"Seed {seed} {hydrology}/{terrain}";
                    AssertRibbonTrianglesValid(ribbons, context);
                    for (int ribbon = 0; ribbon < ribbons.RibbonCount; ribbon++)
                    {
                        int crossing = ribbons.GetRibbonEndCrossing(ribbon);
                        if (crossing < 0)
                        {
                            continue;
                        }
                        transitions++;
                        int lake = scene.Layer.GetReach(ribbons.GetRibbonReach(ribbon)).EndLake;
                        int first = ribbons.GetRibbonStart(ribbon), end = ribbons.GetRibbonStart(ribbon + 1);
                        Assert.Greater(crossing, first, $"{context}: the ribbon reaches the shore from land");
                        Assert.IsFalse(scene.Water.IsUnderWater(lake, ribbons.SampleDirections[crossing - 2]), $"{context}: dry before the shore");
                        for (int v = crossing + 2; v < end; v += 2)
                        {
                            Assert.IsTrue(scene.Water.IsUnderWater(lake, ribbons.SampleDirections[v]), $"{context}: continues under the water");
                            Assert.IsTrue(ribbons.IsVertexSubmerged(v), $"{context}: never lifted above the lake");
                        }
                        Assert.Greater(end - 2, crossing, $"{context}: the ribbon continues past the shoreline");
                        Assert.IsTrue(ribbons.IsVertexSubmerged(crossing));
                        // No gap: the ribbon surface is continuous across the shoreline.
                        Assert.IsTrue(HasTriangleBetween(ribbons, crossing - 2, crossing), $"{context}: gap before the shoreline");
                        Assert.IsTrue(HasTriangleBetween(ribbons, crossing, crossing + 2), $"{context}: gap after the shoreline");
                        // No abrupt width change.
                        // Transition samples (shore and under-water part); upstream width steps belong
                        // to the river itself (confluences), not to the mouth.
                        for (int v = crossing; v < Mathf.Min(end, crossing + 8); v += 2)
                        {
                            float ratio = ribbons.GetVertexHalfWidth(v) / Mathf.Max(ribbons.GetVertexHalfWidth(v - 2), 1e-9f);
                            Assert.That(ratio, Is.InRange(0.6f, 1.6f), $"{context}: width jump at the mouth (pair {(v - crossing) / 2} from the shoreline, river {ribbons.GetRibbonRiver(ribbon)}, half widths {ribbons.GetVertexHalfWidth(v - 2)} -> {ribbons.GetVertexHalfWidth(v)})");
                        }
                    }
                    Assert.AreEqual(0, ribbons.LastDiagnostics.LakeInflowGaps, context);
                }
            }
            Assert.Greater(transitions, 5, "River entrances were tested");
        }

        // F
        [Test]
        public void F_LakeOutlets_StartOnTheRenderedShorelineWithoutGap()
        {
            int transitions = 0;
            foreach (int seed in ReferenceSeeds)
            {
                foreach ((int hydrology, int terrain) in new[] { (64, 64), (128, 256) })
                {
                    Scene scene = Build(seed, hydrology, terrain);
                    RiverRibbonBuilder ribbons = scene.Ribbons;
                    string context = $"Seed {seed} {hydrology}/{terrain}";
                    for (int ribbon = 0; ribbon < ribbons.RibbonCount; ribbon++)
                    {
                        int crossing = ribbons.GetRibbonStartCrossing(ribbon);
                        RiverReach reach = scene.Layer.GetReach(ribbons.GetRibbonReach(ribbon));
                        if (crossing < 0)
                        {
                            continue;
                        }
                        Assert.AreEqual(ReachStart.LakeOutlet, reach.Start, context);
                        Assert.AreEqual(LakeClass.Open, scene.Lakes.GetLake(reach.StartLake).Class, context);
                        transitions++;
                        int first = ribbons.GetRibbonStart(ribbon), end = ribbons.GetRibbonStart(ribbon + 1);
                        Assert.Greater(crossing, first, $"{context}: the ribbon starts under the lake water");
                        for (int v = first; v < crossing; v += 2)
                        {
                            Assert.IsTrue(scene.Water.IsUnderWater(reach.StartLake, ribbons.SampleDirections[v]), $"{context}: hidden start");
                            Assert.IsTrue(ribbons.IsVertexSubmerged(v), context);
                        }
                        Assert.Less(crossing + 2, end, context);
                        Assert.IsFalse(scene.Water.IsUnderWater(reach.StartLake, ribbons.SampleDirections[crossing + 2]), $"{context}: dry after the shore");
                        Assert.IsTrue(HasTriangleBetween(ribbons, crossing - 2, crossing), $"{context}: gap before the shoreline");
                        Assert.IsTrue(HasTriangleBetween(ribbons, crossing, crossing + 2), $"{context}: gap after the shoreline");
                        for (int v = first + 2; v <= Mathf.Min(end - 2, crossing + 2); v += 2)
                        {
                            float ratio = ribbons.GetVertexHalfWidth(v) / Mathf.Max(ribbons.GetVertexHalfWidth(v - 2), 1e-9f);
                            Assert.That(ratio, Is.InRange(0.6f, 1.6f), $"{context}: width jump at the outlet (pair {(v - crossing) / 2} from the shoreline, half widths {ribbons.GetVertexHalfWidth(v - 2)} -> {ribbons.GetVertexHalfWidth(v)})");
                        }
                    }
                    Assert.AreEqual(0, ribbons.LastDiagnostics.LakeOutflowGaps, context);
                }
            }
            Assert.Greater(transitions, 0, "Lake outlets were tested");
        }

        // G
        [Test]
        public void G_ClosedLakes_NeverGetARenderedOutlet()
        {
            int closed = 0;
            foreach (int seed in ReferenceSeeds)
            {
                Scene scene = Build(seed, 64, 64);
                for (int lake = 0; lake < scene.Lakes.Count; lake++)
                {
                    if (scene.Lakes.GetLake(lake).Class != LakeClass.Closed)
                    {
                        continue;
                    }
                    closed++;
                    Assert.Less(scene.Layer.GetLakeOutflowReach(lake), 0, $"Seed {seed}: closed lake {lake} has an outflow");
                    for (int ribbon = 0; ribbon < scene.Ribbons.RibbonCount; ribbon++)
                    {
                        RiverReach reach = scene.Layer.GetReach(scene.Ribbons.GetRibbonReach(ribbon));
                        Assert.AreNotEqual(lake, reach.StartLake, $"Seed {seed}: a ribbon leaves closed lake {lake}");
                    }
                }
                Assert.LessOrEqual(scene.Ribbons.LastDiagnostics.LakeToRiverTransitions, scene.Lakes.OpenCount + scene.Layer.SecondaryOutflows);
            }
            Assert.Greater(closed, 0, "Closed lakes were tested");
        }

        // ------------------------------------------------------------ H, I, J, K

        // H, I
        [Test]
        public void HI_LakeAndTransitionMeshes_HaveNoDegenerateTriangles_AndConsistentNormals()
        {
            foreach ((int hydrology, int terrain) in ResolutionMatrix)
            {
                Scene scene = Build(868497, hydrology, terrain);
                AssertWaterSurface(scene, $"{hydrology}/{terrain}");
                AssertRibbonTrianglesValid(scene.Ribbons, $"{hydrology}/{terrain}");
            }
        }

        // J
        [Test]
        public void J_ShorelineGeometry_IsContinuousAcrossCubeFaceSeams()
        {
            // Synthetic lake across the +X / +Y seam (same construction as the V0.1.1D-B.1 test).
            Vector3 seam = new Vector3(1f, 1f, 0.15f).normalized;
            Func<Vector3, float> terrain = d => 0.012f + 0.004f * d.x + Bowl(d, seam, 0.09f, 0.004f) - (d.x < -0.6f ? 0.03f : 0f);
            HydrologyResult hydrology = new HydrologySystem().Compute(new HydrologyGrid(64), terrain, 0f);
            LakeSet lakes = new LakeSystem().Select(new DepressionAnalyzer().Analyze(hydrology),
                new LakeSettings { abundance = 1f, minimumArea = LakeSettings.MinMinimumArea, fillingFactor = 0.7f }, new LakeContext(1));
            int lake = lakes.GetLakeOfCell(hydrology.Grid.CellFromDirection(seam));
            Assert.GreaterOrEqual(lake, 0);
            foreach (int meshResolution in new[] { 32, 64, 256 })
            {
                PlanetSurfaceSampler sampler = Sampler(terrain, 100f, meshResolution);
                var water = new LakeMeshBuilder();
                water.BuildGeometry(lakes, 100f, sampler);
                LakeMeshDiagnostics d = water.LastDiagnostics;
                string context = "Mesh " + meshResolution;
                Assert.AreEqual(0, d.CrackEdges, context);
                Assert.AreEqual(0, d.SeamMismatches, context);
                Assert.AreEqual(0, d.NonManifoldEdges, context);
                Assert.Greater(d.SeamEdges, 0, context + ": water edges shared along the seam");

                // Both faces hold water, and seam vertices are shared (one vertex per seam point).
                var faces = new HashSet<int>();
                var positions = new HashSet<Vector3>();
                for (int v = water.GetLakeVertexStart(lake); v < water.GetLakeVertexStart(lake + 1); v++)
                {
                    Vector3 p = water.Vertices[v];
                    faces.Add(Mathf.Abs(p.x) > Mathf.Abs(p.y) ? 3 : 0);
                    Assert.IsTrue(positions.Add(p), context + ": duplicated vertex (seam strip)");
                }
                Assert.AreEqual(2, faces.Count, context);

                // Boundary segments form closed loops: every boundary vertex has an even number of
                // segments (2, or 4 where two shoreline loops touch at a vertex lying at the level).
                var degree = new Dictionary<int, int>();
                for (int s = 0; s < water.BoundarySegmentCount; s++)
                {
                    water.GetBoundarySegment(s, out int a, out int b, out _);
                    degree.TryGetValue(a, out int da);
                    degree[a] = da + 1;
                    degree.TryGetValue(b, out int db);
                    degree[b] = db + 1;
                }
                foreach (KeyValuePair<int, int> entry in degree)
                {
                    Assert.AreEqual(0, entry.Value % 2, context + ": open shoreline");
                }
            }
        }

        static float Bowl(Vector3 d, Vector3 center, float radius, float depth)
        {
            float a = Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, center.normalized), -1f, 1f)) / radius;
            return a < 1f ? -depth * (1f - a * a) : 0f;
        }

        // K
        [Test]
        public void K_ResolutionMatrix_ProducesValidShorelinesAndTransitions()
        {
            foreach ((int hydrology, int terrain) in ResolutionMatrix)
            {
                Scene scene = Build(868497, hydrology, terrain);
                string context = $"868497 hydrology {hydrology} / terrain {terrain}";
                LakeMeshDiagnostics d = scene.Water.LastDiagnostics;
                RiverGeometryDiagnostics r = scene.Ribbons.LastDiagnostics;
                AssertWaterSurface(scene, context);
                int drawn = 0;
                for (int lake = 0; lake < scene.Lakes.Count; lake++)
                {
                    drawn += scene.Water.GetLakeTriangleStart(lake + 1) > scene.Water.GetLakeTriangleStart(lake) ? 1 : 0;
                }
                Assert.AreEqual(scene.Lakes.Count, drawn + d.UnrepresentedLakes, context);
                Assert.Greater(drawn, 0, context);
                if (terrain >= hydrology)
                {
                    Assert.AreEqual(0, d.UnrepresentedLakes, context + ": every lake is shown when the mesh is as fine as the grid");
                }
                Assert.Greater(r.RiverToLakeTransitions, 0, context);
                Assert.AreEqual(0, r.LakeInflowGaps + r.LakeOutflowGaps, context);

                // Timing and size (generation only, measured once).
                var again = new LakeMeshBuilder();
                again.BuildGeometry(scene.Lakes, scene.Planet, scene.Sampler);
                TestContext.WriteLine(
                    $"{context}: lakes {scene.Lakes.Count} (not shown {d.UnrepresentedLakes}), triangles {d.Triangles}, shoreline {d.ShorelineLength:0.0} u / {d.ShorelineSegments} segments, "
                    + $"basin cuts {d.BasinCutSegments} (max drop {d.MaxBasinCutDrop:0.00000}), rejected wet {d.RejectedWetVertices}, seam edges {d.SeamEdges}, "
                    + $"water {again.LastMilliseconds:0.0} ms; rivers in {r.RiverToLakeTransitions} / out {r.LakeToRiverTransitions}, unresolved {r.UnresolvedShorelineIntersections}, "
                    + $"exposed {r.ExposedTransitionEnds}, transitions {r.TransitionMilliseconds:0.0} ms");
            }
        }

        // ------------------------------------------------------------ L, M

        // L
        [Test]
        public void L_ShorelineRendering_LeavesTheHydrologyUntouched()
        {
            HydrologyResult hydrology = PlanetHydrology(609538, 128);
            var planet = new PlanetInstance(new PlanetDefinition { seed = 609538, resolution = 64 });
            LakeSet lakes = new LakeCache().Get(hydrology, new LakeSettings(), new LakeContext(609538, planet.Height.GetMountainInfluence));
            RiverNetwork network = Network(hydrology);
            LakeRiverLayer layer = LakeRiverIntegration.Build(network, lakes);
            string before = Fingerprint(hydrology, lakes, network, layer);

            foreach (int terrain in new[] { 64, 256 })
            {
                PlanetSurfaceSampler sampler = Sampler(planet.GetLegacyElevation, planet.Radius, terrain);
                var water = new LakeMeshBuilder();
                water.BuildGeometry(lakes, planet, sampler);
                new RiverRibbonBuilder().BuildGeometry(network, sampler, planet, new RiverSettings(), layer, water);
            }
            Assert.AreEqual(before, Fingerprint(hydrology, lakes, network, layer), "Rendering changed the hydrological data");

            // The terrain resolution is not a hydrological input: same lakes for a 64 or 256 mesh.
            var fine = new PlanetInstance(new PlanetDefinition { seed = 609538, resolution = 256 });
            HydrologyResult other = new HydrologySystem().Compute(new HydrologyGrid(128), fine);
            LakeSet otherLakes = new LakeCache().Get(other, new LakeSettings(), new LakeContext(609538, fine.Height.GetMountainInfluence));
            RiverNetwork otherNetwork = Network(other);
            Assert.AreEqual(before, Fingerprint(other, otherLakes, otherNetwork, LakeRiverIntegration.Build(otherNetwork, otherLakes)));
        }

        static string Fingerprint(HydrologyResult hydrology, LakeSet lakes, RiverNetwork network, LakeRiverLayer layer)
        {
            var hash = new System.Text.StringBuilder();
            long receivers = 17;
            for (int cell = 0; cell < hydrology.Grid.CellCount; cell++)
            {
                receivers = receivers * 31 + hydrology.GetReceiver(cell);
            }
            hash.Append(receivers).Append('|');
            for (int i = 0; i < lakes.Count; i++)
            {
                Lake lake = lakes.GetLake(i);
                hash.Append(lake.Id).Append(',').Append(lake.Key).Append(',').Append(lake.Class).Append(',')
                    .Append(lake.Level.ToString("R")).Append(',').Append(lake.CellCount).Append(',').Append(lake.OutletCell).Append(';');
            }
            hash.Append('|');
            for (int river = 0; river < network.RiverCount; river++)
            {
                River data = network.GetRiver(river);
                hash.Append(data.ParentRiver).Append(',').Append(data.JunctionCell).Append(',').Append(data.MouthCell).Append(',')
                    .Append(data.NodeCount).Append(';');
            }
            hash.Append('|');
            for (int r = 0; r < layer.ReachCount; r++)
            {
                RiverReach reach = layer.GetReach(r);
                hash.Append(reach.River).Append(',').Append(reach.FirstNode).Append(',').Append(reach.LastNode).Append(',')
                    .Append(reach.StartLake).Append(',').Append(reach.EndLake).Append(';');
            }
            return hash.ToString();
        }

        // M
        [Test]
        public void M_RepeatedGeneration_ProducesIdenticalGeometryAndDiagnostics()
        {
            Scene scene = Build(868497, 128, 256);
            var water = new LakeMeshBuilder();
            water.BuildGeometry(scene.Lakes, scene.Planet, scene.Sampler);
            CollectionAssert.AreEqual(scene.Water.Vertices, water.Vertices);
            CollectionAssert.AreEqual(scene.Water.Triangles, water.Triangles);
            CollectionAssert.AreEqual(scene.Water.WaterAttributes, water.WaterAttributes);
            LakeMeshDiagnostics a = scene.Water.LastDiagnostics, b = water.LastDiagnostics;
            Assert.AreEqual(a.ShorelineLength, b.ShorelineLength);
            Assert.AreEqual(a.ShorelineSegments, b.ShorelineSegments);
            Assert.AreEqual(a.BasinCutSegments, b.BasinCutSegments);
            Assert.AreEqual(a.Triangles, b.Triangles);

            var ribbons = new RiverRibbonBuilder();
            ribbons.BuildGeometry(scene.Network, scene.Sampler, scene.Planet, new RiverSettings(), scene.Layer, water);
            CollectionAssert.AreEqual(scene.Ribbons.Vertices, ribbons.Vertices);
            CollectionAssert.AreEqual(scene.Ribbons.Triangles, ribbons.Triangles);
            Assert.AreEqual(scene.Ribbons.LastDiagnostics.RiverToLakeTransitions, ribbons.LastDiagnostics.RiverToLakeTransitions);
            Assert.AreEqual(scene.Ribbons.LastDiagnostics.LakeToRiverTransitions, ribbons.LastDiagnostics.LakeToRiverTransitions);
            // Internal water (no surface passed) gives the same ribbons.
            var standalone = new RiverRibbonBuilder();
            standalone.BuildGeometry(scene.Network, scene.Sampler, scene.Planet, new RiverSettings(), scene.Layer);
            CollectionAssert.AreEqual(scene.Ribbons.Vertices, standalone.Vertices);
        }

        // ------------------------------------------------------------ N, O (Unity objects)

        GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                Object.DestroyImmediate(root);
                root = null;
            }
        }

        PlanetVisualController CreateVisual()
        {
            root = new GameObject("Shoreline Test");
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
            visual.Lakes.showLakes = true;
            visual.Rivers.showRivers = true;
            return visual;
        }

        // N
        [Test]
        public void N_VisualOnlyChanges_DoNotRecomputeHydrologyOrLakes()
        {
            PlanetVisualController visual = CreateVisual();
            var definition = new PlanetDefinition { seed = 868497, resolution = 48 };
            visual.Render(new PlanetInstance(definition));
            int hydrology = visual.HydrologyCache.ComputeCount;
            int analyses = visual.LakeCache.AnalysisCount;
            int selections = visual.LakeCache.SelectionCount;
            int water = visual.LakeMeshBuildCount;
            int ribbons = visual.RiverRibbonBuildCount;

            // Lake color / visibility, river visibility: nothing rebuilt.
            visual.Lakes.color = Color.green;
            visual.RefreshColors();
            visual.Lakes.showLakes = false;
            visual.RefreshColors();
            visual.Lakes.showLakes = true;
            visual.RefreshColors();
            visual.Rivers.showRivers = false;
            visual.RefreshColors();
            visual.Rivers.showRivers = true;
            visual.RefreshColors();
            Assert.AreEqual(hydrology, visual.HydrologyCache.ComputeCount);
            Assert.AreEqual(analyses, visual.LakeCache.AnalysisCount);
            Assert.AreEqual(selections, visual.LakeCache.SelectionCount);
            Assert.AreEqual(water, visual.LakeMeshBuildCount, "No lake geometry rebuild");

            // River width: ribbons and their transitions only.
            int ribbonsBefore = visual.RiverRibbonBuildCount;
            visual.Rivers.widthMultiplier = 1.7f;
            visual.RefreshColors();
            Assert.AreEqual(ribbonsBefore + 1, visual.RiverRibbonBuildCount);
            Assert.AreEqual(water, visual.LakeMeshBuildCount, "River width does not rebuild the lakes");
            Assert.AreEqual(selections, visual.LakeCache.SelectionCount);

            // Terrain resolution: new shoreline, same hydrology and lake selection.
            definition.resolution = 96;
            visual.Render(new PlanetInstance(definition));
            Assert.AreEqual(hydrology, visual.HydrologyCache.ComputeCount, "Terrain resolution is not a hydrology input");
            Assert.AreEqual(analyses, visual.LakeCache.AnalysisCount);
            Assert.AreEqual(selections, visual.LakeCache.SelectionCount);
            Assert.AreEqual(water + 1, visual.LakeMeshBuildCount, "Shoreline rebuilt once for the new mesh");
            Assert.Greater(visual.RiverRibbonBuildCount, ribbons);

            // Lake abundance: selection + geometry, never the hydrology or the candidates.
            visual.Lakes.abundance = 0.85f;
            visual.RefreshColors();
            Assert.AreEqual(hydrology, visual.HydrologyCache.ComputeCount);
            Assert.AreEqual(analyses, visual.LakeCache.AnalysisCount);
            Assert.AreEqual(selections + 1, visual.LakeCache.SelectionCount);
            Assert.AreEqual(water + 2, visual.LakeMeshBuildCount);

            // Nothing changed: nothing rebuilt (no per-frame work).
            int finalWater = visual.LakeMeshBuildCount, finalRibbons = visual.RiverRibbonBuildCount;
            visual.RefreshColors();
            visual.RefreshColors();
            Assert.AreEqual(finalWater, visual.LakeMeshBuildCount);
            Assert.AreEqual(finalRibbons, visual.RiverRibbonBuildCount);
        }

        // O
        [Test]
        public void O_RegeneratedLakeAndTransitionMeshes_AreReleased()
        {
            PlanetVisualController visual = CreateVisual();
            var definition = new PlanetDefinition { seed = 554492, resolution = 32 };
            visual.Render(new PlanetInstance(definition));
            visual.Render(new PlanetInstance(definition));
            int meshCount = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int materialCount = Resources.FindObjectsOfTypeAll<Material>().Length;
            int gameObjectCount = Resources.FindObjectsOfTypeAll<GameObject>().Length;
            Mesh lakeMesh = visual.LakeMesh, riverMesh = visual.RiverMesh;
            Assert.IsNotNull(lakeMesh);
            Assert.IsNotNull(riverMesh);

            for (int i = 0; i < 6; i++)
            {
                definition.seed = 554492 + (i % 2);
                definition.resolution = i % 3 == 0 ? 24 : 40;
                visual.Rivers.widthMultiplier = 1f + i * 0.2f;
                visual.Lakes.abundance = 0.4f + i * 0.1f;
                visual.Render(new PlanetInstance(definition));
                visual.RefreshColors();
            }
            Assert.AreEqual(meshCount, Resources.FindObjectsOfTypeAll<Mesh>().Length, "Lake / river meshes are reused, not leaked");
            Assert.AreEqual(materialCount, Resources.FindObjectsOfTypeAll<Material>().Length);
            Assert.AreEqual(gameObjectCount, Resources.FindObjectsOfTypeAll<GameObject>().Length);
            Assert.AreSame(lakeMesh, visual.LakeMesh);
            Assert.AreSame(riverMesh, visual.RiverMesh);

            Object.DestroyImmediate(root);
            root = null;
            Assert.IsTrue(lakeMesh == null, "Lake mesh released with the planet");
            Assert.IsTrue(riverMesh == null, "River mesh released with the planet");
        }
    }
}
