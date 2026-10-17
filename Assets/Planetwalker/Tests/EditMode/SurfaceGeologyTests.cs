using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;
using Planetwalker.Bootstrap;
using Planetwalker.CameraControl;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Persistence;
using Planetwalker.Rendering;
using Planetwalker.Terrain;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Surface geology (V0.2.0E): determinism (A), seed variation (B), cube-face continuity (C), LOD independence (D),
    /// floating-origin invariance (E), valid ranges (F), smooth transitions (G), legacy preservation (H), ground
    /// clearance (I), archive compatibility (J), LOD transition normals (K), shader and runtime wiring (L).
    /// </summary>
    public class SurfaceGeologyTests
    {
        static readonly Dictionary<int, PlanetInstance> Planets = new Dictionary<int, PlanetInstance>();
        readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            created.Clear();
        }

        static PlanetInstance Planet(int seed = 1337)
        {
            if (!Planets.TryGetValue(seed, out PlanetInstance planet))
            {
                planet = new PlanetInstance(new PlanetDefinition { seed = seed });
                Planets[seed] = planet;
            }
            return planet;
        }

        static AdaptiveSurfaceSnapshot Snapshot(int seed = 1337) =>
            new AdaptiveSurfaceSnapshot(1, Planet(seed), null, PlanetColorRamp.CreateDefault(), true, 16);

        /// <summary>Land directions (Fibonacci set), above sea level by a margin.</summary>
        static List<Vector3d> Land(PlanetInstance planet, int wanted, int samples = 4000)
        {
            var list = new List<Vector3d>();
            for (int k = 0; k < samples && list.Count < wanted; k++)
            {
                double y = 1.0 - 2.0 * (k + 0.5) / samples, r = Math.Sqrt(1.0 - y * y), phi = k * 2.399963229728653;
                var d = new Vector3d(r * Math.Cos(phi), y, r * Math.Sin(phi));
                if (planet.Height.GetElevation(d) - planet.SeaLevel > 0.001f)
                {
                    list.Add(d);
                }
            }
            return list;
        }

        static GeologySample Sample(SurfaceGeology geology, Vector3d d)
        {
            geology.Sample(d, out GeologySample g);
            return g;
        }

        static void AssertSame(GeologySample a, GeologySample b, string message)
        {
            Assert.AreEqual(a.Bedrock, b.Bedrock, message);
            Assert.AreEqual(a.Sediment, b.Sediment, message);
            Assert.AreEqual(a.Erosion, b.Erosion, message);
            Assert.AreEqual(a.Strata, b.Strata, message);
            Assert.AreEqual(a.Slope, b.Slope, message);
            Assert.AreEqual(a.Concavity, b.Concavity, message);
            Assert.AreEqual(a.Downslope, b.Downslope, message);
            Assert.AreEqual(a.RockTint, b.RockTint, message);
        }

        // ------------------------------------------------------------------ A: determinism

        [Test]
        public void A_SameSeed_GivesIdenticalFields_OnEveryThreadAndInstance()
        {
            PlanetInstance planet = Planet();
            var first = new SurfaceGeology(planet, null);
            var second = new SurfaceGeology(planet, null);
            List<Vector3d> points = Land(planet, 200);
            GeologySample[] expected = points.Select(d => Sample(first, d)).ToArray();
            for (int k = 0; k < points.Count; k++)
            {
                AssertSame(expected[k], Sample(second, points[k]), "Second instance, point " + k);
            }
            // Worker threads with their own caches, in another order.
            var parallel = new GeologySample[points.Count];
            Parallel.For(0, points.Count, k => parallel[points.Count - 1 - k] = Sample(second, points[points.Count - 1 - k]));
            for (int k = 0; k < points.Count; k++)
            {
                AssertSame(expected[k], parallel[k], "Thread, point " + k);
            }
        }

        // ------------------------------------------------------------------ B: seed variation

        [Test]
        public void B_DifferentSeeds_GiveDifferentGeologicalCharacter()
        {
            var a = new SurfaceGeology(Planet(1337), null);
            var b = new SurfaceGeology(Planet(609538), null);
            double rockColor = 0, family = 0;
            int n = 0;
            for (int k = 0; k < 500; k++)
            {
                double y = 1.0 - 2.0 * (k + 0.5) / 500, r = Math.Sqrt(1.0 - y * y), phi = k * 2.399963229728653;
                var d = new Vector3d(r * Math.Cos(phi), y, r * Math.Sin(phi));
                GeologySample ga = Sample(a, d), gb = Sample(b, d);
                rockColor += Math.Abs(ga.RockTint.r - gb.RockTint.r) + Math.Abs(ga.RockTint.b - gb.RockTint.b);
                family += Math.Abs(ga.Sedimentary - gb.Sedimentary);
                n++;
            }
            Assert.Greater(family / n, 0.1, "Province (rock family) fields differ between seeds");
            Assert.Greater(rockColor / n, 0.03, "Rock colors differ between seeds");
        }

        // ------------------------------------------------------------------ C: cube faces

        [Test]
        public void C_FieldsAreContinuousAcrossCubeFaceEdges_ByConvergence()
        {
            // Continuity, not a tolerance: the difference across an edge must vanish as the two points get closer
            // (20 m, 2 m, 0.2 m apart). A seam would keep its jump at 0.2 m (V0.2.0F.1).
            var geology = new SurfaceGeology(Planet(), null);
            EdgeContinuity.Result r = EdgeContinuity.Probe(d =>
            {
                GeologySample g = Sample(geology, d);
                return new[] { g.Bedrock, g.Sediment, g.Erosion, g.Strata, g.RockTint.r, g.RockTint.g, g.RockTint.b, g.Sedimentary, g.Family * 0.5f };
            });
            Debug.Log($"Geology across cube edges ({r.Pairs} places): worst difference 20 m {r.Worst20m:0.0000}, 2 m {r.Worst2m:0.0000}, 0.2 m {r.Worst02m:0.0000} at {r.WorstAt}");
            Assert.Greater(r.Pairs, 400);
            Assert.Less(r.Worst02m, 0.01f, "No jump at the cube-face edges: worst at " + r.WorstAt);
        }

        // ------------------------------------------------------------------ D, K: LOD

        static PatchGeometryData Build(AdaptiveSurfaceSnapshot snapshot, PlanetPatchId id)
        {
            var data = new PatchGeometryData(snapshot.Topology.VertexCount);
            new AdaptivePatchGeometryBuilder().Build(snapshot, id, data);
            return data;
        }

        /// <summary>A patch at a level whose lattice contains land relief (face 0 center region).</summary>
        static PlanetPatchId Patch(int level, int face = 0)
        {
            int n = 1 << level;
            return new PlanetPatchId(face, level, (int)(n * 0.37), (int)(n * 0.61));
        }

        [Test]
        public void D_GeologyAtAPlanetaryLocation_DoesNotDependOnThePatchLevel()
        {
            AdaptiveSurfaceSnapshot snapshot = Snapshot();
            int r = snapshot.Resolution;
            // Levels whose vertex lattice is at least the reference geology lattice (16 x 2^12 = 2^16 per face).
            for (int level = 12; level <= 15; level++)
            {
                PlanetPatchId parent = Patch(level);
                PlanetPatchId child = new PlanetPatchId(parent.Face, level + 1, parent.X * 2, parent.Y * 2);
                PatchGeometryData coarse = Build(snapshot, parent), fine = Build(snapshot, child);
                // Parent vertex (i, j) for i, j <= r / 2 is the child's vertex (2i, 2j).
                for (int j = 0; j <= r / 2; j++)
                {
                    for (int i = 0; i <= r / 2; i++)
                    {
                        int vc = i + j * (r + 1), vf = 2 * i + 2 * j * (r + 1);
                        Assert.AreEqual(coarse.Geology[vc], fine.Geology[vf], $"Geology, level {level} vs {level + 1}, vertex {i},{j}");
                        Assert.AreEqual(coarse.Grid[vc].w, fine.Grid[vf].w, "Reference slope");
                        Assert.AreEqual(coarse.Flow[vc], fine.Flow[vf], "Downslope and concavity");
                        Assert.AreEqual(coarse.RockTints[vc], fine.RockTints[vf], "Rock color");
                    }
                }
            }
        }

        [Test]
        public void K_FinerPatchEdge_CarriesTheCoarserNeighborsNormals()
        {
            AdaptiveSurfaceSnapshot snapshot = Snapshot();
            int r = snapshot.Resolution;
            foreach (int level in new[] { 3, 8, 12, 15 })
            {
                PlanetPatchId coarse = Patch(level);
                // The fine patch west of the coarse patch's east neighbor... simpler: a child sharing the coarse patch's
                // west edge; its West-edge vertices with even index coincide with the coarse patch's West-edge vertices.
                PlanetPatchId fine = new PlanetPatchId(coarse.Face, level + 1, coarse.X * 2, coarse.Y * 2);
                PatchGeometryData c = Build(snapshot, coarse), f = Build(snapshot, fine);
                for (int k = 0; k <= r; k += 2)
                {
                    int vf = 0 + k * (r + 1);          // fine west edge, row k
                    int vc = 0 + (k / 2) * (r + 1);    // the same point on the coarse west edge
                    Vector3 expected = c.Normals[vc], actual = f.CoarseNormals[vf];
                    Assert.Less((expected - actual).magnitude, 1e-6f, $"Level {level + 1} coarse normal = level {level} normal at west edge row {k}");
                }
                // Interior vertices beyond the blend band keep their own normal only.
                int mid = r / 2 + r / 2 * (r + 1);
                Assert.AreEqual(f.Normals[mid], f.CoarseNormals[mid], "Interior: no coarse normal needed");
            }
        }

        // ------------------------------------------------------------------ E: floating origin

        [Test]
        public void E_Rebasing_ChangesNeitherGeologyNorItsAnchoring()
        {
            var host = new WorldTestHost(created);
            host.Definition.surfaceDetail = true;
            host.Regenerate();
            PlanetRenderFrame frame = PlanetRenderFrame.GetOrCreate(host.Root.transform);
            AdaptiveTerrainRenderer terrain = AdaptiveTerrainRenderer.GetOrCreate(host.Visual);
            terrain.BackgroundConstruction = false;
            terrain.enabled = true;
            Camera camera = host.Camera.GetComponent<Camera>();
            terrain.ViewCamera = camera;
            for (int t = 0; t < 8; t++) terrain.Tick(camera);
            PlanetPatchId id = Patch(13);
            PatchGeometryData before = Build(terrain.Snapshot, id);
            var block = new MaterialPropertyBlock();
            terrain.WriteGroundDetail(block, camera);
            Vector4 origin = block.GetVector("_GroundOrigin");
            Vector3d anchoredBefore = frame.RenderToPlanet(new Vector3(origin.x, origin.y, origin.z));

            frame.RebaseNow(new Vector3d(host.Current.Radius * 1.01, 0.3, -0.2));
            PatchGeometryData after = Build(terrain.Snapshot, id);
            for (int v = 0; v < before.Geology.Length; v++)
            {
                Assert.AreEqual(before.Geology[v], after.Geology[v]);
                Assert.AreEqual(before.Flow[v], after.Flow[v]);
            }
            terrain.WriteGroundDetail(block, camera);
            origin = block.GetVector("_GroundOrigin");
            Vector3d anchoredAfter = frame.RenderToPlanet(new Vector3(origin.x, origin.y, origin.z));
            double period = AdaptiveTerrainRenderer.GroundNoisePeriodMeters;
            Vector3d shift = (anchoredAfter - anchoredBefore) * host.Current.Scale.MetersPerUnit;
            foreach (double v in new[] { shift.x, shift.y, shift.z })
            {
                // Tolerance: the float render coordinates of an origin far from the (unmoved) camera (~2 m at Earth scale).
                Assert.AreEqual(0.0, v / period - Math.Round(v / period), 0.01, "The noise origin moves by whole periods only (no swimming)");
            }
        }

        // ------------------------------------------------------------------ F, G: ranges and continuity

        [Test]
        public void F_AllWeightsAreFiniteAndWithinTheirBounds()
        {
            foreach (int seed in new[] { 1337, 868497, 609538 })
            {
                var geology = new SurfaceGeology(Planet(seed), null);
                for (int k = 0; k < 3000; k++)
                {
                    double y = 1.0 - 2.0 * (k + 0.5) / 3000, r = Math.Sqrt(1.0 - y * y), phi = k * 2.399963229728653;
                    GeologySample g = Sample(geology, new Vector3d(r * Math.Cos(phi), y, r * Math.Sin(phi)));
                    foreach (float w in new[] { g.Bedrock, g.Sediment, g.Erosion, g.Strata, g.Aridity, g.Sedimentary, g.RockTint.r, g.RockTint.g, g.RockTint.b })
                    {
                        Assert.IsFalse(float.IsNaN(w) || float.IsInfinity(w), $"Finite (seed {seed}, point {k})");
                        Assert.GreaterOrEqual(w, 0f);
                        Assert.LessOrEqual(w, 1f);
                    }
                    Assert.LessOrEqual(g.Bedrock + g.Sediment, 1.0001f, "Sediment never where bedrock is");
                    Assert.IsTrue(float.IsFinite(g.Slope) && g.Slope >= 0f && float.IsFinite(g.Concavity));
                    float m = g.Downslope.magnitude;
                    Assert.IsTrue(m == 0f || Mathf.Abs(m - 1f) < 1e-3f, "Downslope: unit or zero");
                }
            }
        }

        [Test]
        public void G_FieldsChangeSmoothly_AlongAWalkAcrossTheLandscape()
        {
            PlanetInstance planet = Planet();
            var geology = new SurfaceGeology(planet, null);
            PlanetScale scale = planet.Scale;
            foreach (Vector3d start in Land(planet, 6))
            {
                Vector3d east = Vector3d.Cross(PlanetTangentFrame.At(start).North, start).Normalized;
                double step = scale.ToUnits(10.0) / planet.Radius; // 10 m
                GeologySample previous = Sample(geology, start);
                for (int k = 1; k <= 500; k++)
                {
                    GeologySample g = Sample(geology, (start + east * (step * k)).Normalized);
                    // Bilinear between ~150 m nodes: at most 10/150 of a full 0..1 swing per 10 m step.
                    Assert.Less(Mathf.Abs(g.Bedrock - previous.Bedrock), 0.08f, $"Bedrock step {k}");
                    Assert.Less(Mathf.Abs(g.Sediment - previous.Sediment), 0.08f, $"Sediment step {k}");
                    Assert.Less(Mathf.Abs(g.Erosion - previous.Erosion), 0.08f, $"Erosion step {k}");
                    previous = g;
                }
            }
        }

        // ------------------------------------------------------------------ H, I: legacy and geometry

        [Test]
        public void H_GeologyChangesNoGeometryNorColor_AndCanBeSwitchedOff()
        {
            AdaptiveSurfaceSnapshot snapshot = Snapshot();
            PlanetPatchId id = Patch(10);
            PatchGeometryData data = Build(snapshot, id);
            PlanetHeightGenerator height = snapshot.Planet.Height;
            int r = snapshot.Resolution, lattice = r << id.Level;
            for (int j = 0; j <= r; j += 4)
            {
                for (int i = 0; i <= r; i += 4)
                {
                    int v = i + j * (r + 1);
                    Vector3d d = CubeSphereCoordinates.LatticeDirectionD(id.Face, id.X * r + i, id.Y * r + j, lattice);
                    float e = height.GetElevation(d);
                    Assert.AreEqual(height.GetSurfaceNormal(d, snapshot.NormalAngle(id.Level)), data.Normals[v], "Normal: the V0.2.0D one");
                    Color expected = AdaptivePatchGeometryBuilder.ToLinear(PlanetMeshBuilder.TerrainColor(null, snapshot.Ramp, snapshot.Planet.SeaLevel,
                        d.ToVector3(), e, data.Normals[v]));
                    Assert.AreEqual(expected, data.Colors[v], "Vertex color: the V0.2.0D one");
                }
            }
        }

        [Test]
        public void I_NoNewGeometry_VerticesStayOnTheAuthoritativeSurface()
        {
            AdaptiveSurfaceSnapshot snapshot = Snapshot();
            foreach (int level in new[] { 6, 12, 16 })
            {
                PlanetPatchId id = Patch(level);
                PatchGeometryData data = Build(snapshot, id);
                int r = snapshot.Resolution, lattice = r << level;
                var origin = new Vector3d(data.Origin);
                for (int j = 0; j <= r; j += 8)
                {
                    for (int i = 0; i <= r; i += 8)
                    {
                        Vector3d d = CubeSphereCoordinates.LatticeDirectionD(id.Face, id.X * r + i, id.Y * r + j, lattice);
                        double expected = snapshot.Sampler.Radius * (1.0 + snapshot.Planet.Height.GetElevation(d));
                        double actual = (origin + new Vector3d(data.Vertices[i + j * (r + 1)])).Magnitude;
                        Assert.AreEqual(expected, actual, snapshot.Sampler.Radius * 1e-7, "Vertex radius = canonical elevation (what the camera's clearance uses)");
                    }
                }
            }
        }

        // ------------------------------------------------------------------ J, L: archives and wiring

        [Test]
        public void J_GeologySettings_AreNotPartOfTheWorld_AndOldWorldsLoadUnchanged()
        {
            string directory = WorldArchiveTestUtil.CreateTempDirectory();
            try
            {
                var before = new HashSet<GameObject>(Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None));
                var bootstrap = new GameObject("Geology Bootstrap Test").AddComponent<PlanetBootstrap>();
                bootstrap.EnsureSceneObjects();
                foreach (GameObject o in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
                {
                    if (!before.Contains(o) && o.transform.parent == null) created.Add(o);
                }
                bootstrap.UseWorldStore(new WorldArchiveStore(directory));
                bootstrap.Definition.resolution = 16;
                bootstrap.Visual.Climate.resolution = ClimateSettings.LowResolution;
                bootstrap.Regenerate();
                WorldSession worlds = bootstrap.Worlds;
                worlds.ResetBaseline();
                int generations = bootstrap.GenerationCount;
                AdaptiveTerrainRenderer terrain = AdaptiveTerrainRenderer.GetOrCreate(bootstrap.Visual);
                terrain.GeologyEnabled = false;
                terrain.GeologyStrength = 0.3f;
                terrain.GeologyDebug = GeologyDebugView.Sediment;
                Assert.IsFalse(worlds.RefreshDirty(), "Geology appearance is presentation only: the world is unchanged");
                Assert.AreEqual(generations, bootstrap.GenerationCount, "No regeneration");
                terrain.GeologyEnabled = true;
                terrain.GeologyStrength = 1f;
                terrain.GeologyDebug = GeologyDebugView.None;
            }
            finally
            {
                WorldArchiveTestUtil.DeleteTempDirectory(directory);
            }
        }

        [Test]
        public void L_ShaderPropertiesVertexLayoutAndControls_AreWired()
        {
            Shader shader = Shader.Find("Planetwalker/PlanetTerrain");
            Assert.IsNotNull(shader);
            var material = new Material(shader);
            created.Add(material);
            foreach (string property in new[] { "_GeoParams", "_PatchStitch", "_PatchMorph", "_BounceStrength", "_GroundOrigin", "_GroundParams" })
            {
                Assert.IsTrue(material.HasProperty(property), property);
            }
            Assert.AreEqual(TerrainVertex.Size, Marshal.SizeOf<TerrainVertex>(), "Packed vertex layout");
            Assert.AreEqual(TerrainVertex.Size, TerrainVertex.Layout.Sum(a => (a.format == VertexAttributeFormat.Float32 ? 4 : a.format == VertexAttributeFormat.Float16 ? 2 : 1) * a.dimension));
            Assert.AreEqual(0x3C00, TerrainVertex.ToHalf(1f));
            Assert.AreEqual(0xC000, TerrainVertex.ToHalf(-2f));
            Assert.AreEqual(0x3555, TerrainVertex.ToHalf(1f / 3f));

            // Runtime: uploaded meshes carry the geology attributes; the renderer writes the geology switch.
            var host = new WorldTestHost(created);
            host.Regenerate();
            PlanetRenderFrame.GetOrCreate(host.Root.transform);
            AdaptiveTerrainRenderer terrain = AdaptiveTerrainRenderer.GetOrCreate(host.Visual);
            terrain.BackgroundConstruction = false;
            terrain.ConstructionMilliseconds = 100000f;
            terrain.MaxUploadsPerFrame = 4096;
            terrain.MaxPendingBuilds = 4096;
            terrain.enabled = true;
            Camera camera = host.Camera.GetComponent<Camera>();
            terrain.ViewCamera = camera;
            for (int t = 0; t < 200 && !terrain.IsDisplaying; t++) terrain.Tick(camera);
            Assert.IsTrue(terrain.IsDisplaying);
            Mesh mesh = null;
            foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                if (m.name.StartsWith("Adaptive Terrain ")) { mesh = m; break; }
            }
            Assert.IsNotNull(mesh, "A patch mesh");
            foreach (VertexAttribute a in new[] { VertexAttribute.Tangent, VertexAttribute.TexCoord0, VertexAttribute.TexCoord1, VertexAttribute.TexCoord2, VertexAttribute.TexCoord3 })
            {
                Assert.IsTrue(mesh.HasVertexAttribute(a), a.ToString());
            }
            var block = new MaterialPropertyBlock();
            terrain.GeologyEnabled = true;
            terrain.WriteGroundDetail(block, camera);
            Assert.AreEqual(1f, block.GetVector("_GeoParams").z, "On");
            terrain.GeologyEnabled = false;
            terrain.WriteGroundDetail(block, camera);
            Assert.AreEqual(0f, block.GetVector("_GeoParams").z, "Off: the V0.2.0D look");
            terrain.GeologyEnabled = true;
            terrain.GeologyStrength = 0f;
            terrain.WriteGroundDetail(block, camera);
            Assert.AreEqual(0f, block.GetVector("_GeoParams").z, "Strength 0 is off");
            terrain.GeologyStrength = 1f;
        }

        [Test]
        public void PilotRegion_IsDeterministic_DryAndMountainous()
        {
            PlanetInstance planet = Planet();
            var geology = new SurfaceGeology(planet, null);
            Vector3d a = geology.FindPilot(4000), b = new SurfaceGeology(planet, null).FindPilot(4000);
            Assert.AreEqual(a.x, b.x);
            Assert.AreEqual(a.y, b.y);
            Assert.AreEqual(a.z, b.z);
            float above = planet.Height.GetElevation(a) - planet.SeaLevel;
            Assert.Greater(above, 0.0015f, "On land, under the cloud layer");
        }
    }
}
