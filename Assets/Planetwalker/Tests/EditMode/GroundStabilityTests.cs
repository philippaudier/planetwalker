using System;
using System.Collections.Generic;
using NUnit.Framework;
using Planetwalker.CameraControl;
using Planetwalker.Core;
using Planetwalker.Persistence;
using Planetwalker.Rendering;
using Planetwalker.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Ground-level stability and LOD consistency (V0.2.0F.3): the explore camera keeps its clearance from the
    /// surface itself (not only vertically) and its near plane in front of it on steep slopes (the 1.7 m mountain
    /// anomaly), the authoritative height and the patch geometry do not depend on the patch budget, near-ground patch
    /// meshes are finite and valid, and the selection is stable for a fixed camera at the default budget.
    /// </summary>
    public class GroundStabilityTests
    {
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

        WorldTestHost CreateHost(int seed = 1337)
        {
            var host = new WorldTestHost(created);
            WorldConfiguration c = WorldArchiveTestUtil.Tweaked(seed, flipFlags: false);
            c.Terrain.resolution = 16;
            host.Apply(c);
            host.Definition.seed = seed;
            host.Regenerate();
            PlanetRenderFrame.GetOrCreate(host.Root.transform);
            return host;
        }

        static PlanetExploreCamera CreateExplorer(WorldTestHost host)
        {
            host.Camera.enabled = false;
            PlanetExploreCamera explorer = host.Camera.gameObject.AddComponent<PlanetExploreCamera>();
            explorer.Target = host.Root.transform;
            explorer.Planet = host.Current;
            return explorer;
        }

        /// <summary>The steepest land slope among a spiral of directions (finite differences over 20 m), with its uphill direction.</summary>
        static (Vector3d at, Vector3d uphill, double degrees) Steepest(PlanetExploreCamera explorer, PlanetInstance planet)
        {
            PlanetScale scale = planet.Scale;
            double step = scale.ToUnits(20.0) / planet.Radius;
            (Vector3d, Vector3d, double) best = (Vector3d.Up, Vector3d.Right, 0.0);
            const int count = 40000;
            for (int k = 0; k < count; k++)
            {
                double y = 1.0 - 2.0 * (k + 0.5) / count, r = Math.Sqrt(1.0 - y * y), phi = k * 2.399963229728653;
                var d = new Vector3d(r * Math.Cos(phi), y, r * Math.Sin(phi));
                if (planet.Height.GetElevation(d) - planet.SeaLevel < 0.0002) continue;
                Vector3d north = PlanetTangentFrame.At(d).North, east = Vector3d.Cross(north, d).Normalized;
                double h0 = explorer.FloorRadius(d);
                double dn = explorer.FloorRadius((d + north * step).Normalized) - h0;
                double de = explorer.FloorRadius((d + east * step).Normalized) - h0;
                double rise = Math.Sqrt(dn * dn + de * de);
                double degrees = Math.Atan2(rise, scale.ToUnits(20.0)) * (180.0 / Math.PI);
                if (degrees > best.Item3)
                {
                    best = (d, (north * dn + east * de).Normalized, degrees);
                }
            }
            return best;
        }

        // ------------------------------------------------------------------ Clearance (the mountain anomaly)

        [Test]
        public void SteepSlope_TheClearanceHoldsForTheDistanceToTheSurface_AndTheNearPlaneStaysInFrontOfIt()
        {
            WorldTestHost host = CreateHost();
            PlanetExploreCamera explorer = CreateExplorer(host);
            PlanetInstance planet = host.Current;
            (Vector3d at, Vector3d uphill, double degrees) = Steepest(explorer, planet);
            Assert.Greater(degrees, 45.0, "A steep slope for the regression");
            Camera camera = explorer.GetComponent<Camera>();
            float renderScale = Mathf.Abs(host.Root.transform.lossyScale.x);
            var log = new System.Text.StringBuilder();
            foreach (double requested in new[] { PlanetExploreCamera.EyeHeightMeters, 3.0, 10.0, 100.0 })
            {
                explorer.SetPose(at * (explorer.FloorRadius(at) + planet.Scale.ToUnits(requested)), uphill, PlanetTangentFrame.At(at).North);
                explorer.Place();
                double surface = explorer.SurfaceDistanceMeters;
                double near = planet.Scale.ToMeters(camera.nearClipPlane / renderScale);
                log.Append($" {requested} m: vertical {explorer.ClearanceMeters:0.00} m, surface {surface:0.00} m, near {near:0.000} m;");
                Assert.GreaterOrEqual(surface, explorer.MinClearanceMeters * 0.99, $"{requested} m above a {degrees:0} degree slope: the surface is at least the minimum clearance away");
                Assert.Less(near, surface * 0.5, $"{requested} m: the near plane stays well in front of the closest surface (before: a quarter of the vertical clearance, beyond the slope)");
                Assert.GreaterOrEqual(explorer.ClearanceMeters, requested * 0.999, "Never lowered below the requested clearance");
            }
            TestContext.WriteLine($"slope {degrees:0.0} deg:" + log);
        }

        [Test]
        public void FlatGround_TheSurfaceDistanceEqualsTheVerticalClearance()
        {
            WorldTestHost host = CreateHost();
            PlanetExploreCamera explorer = CreateExplorer(host);
            PlanetInstance planet = host.Current;
            // Over the sea the floor is the flat sea surface.
            Vector3d sea = Vector3d.Up;
            const int count = 4000;
            for (int k = 0; k < count; k++)
            {
                double y = 1.0 - 2.0 * (k + 0.5) / count, r = Math.Sqrt(1.0 - y * y), phi = k * 2.399963229728653;
                var d = new Vector3d(r * Math.Cos(phi), y, r * Math.Sin(phi));
                if (planet.Height.GetElevation(d) - planet.SeaLevel < -0.003) { sea = d; break; }
            }
            foreach (double meters in new[] { 2.0, 50.0, 1000.0 })
            {
                explorer.SetPose(sea * (explorer.FloorRadius(sea) + planet.Scale.ToUnits(meters)), PlanetTangentFrame.At(sea).North, PlanetTangentFrame.At(sea).North);
                explorer.Place();
                Assert.AreEqual(explorer.ClearanceMeters, explorer.SurfaceDistanceMeters, meters * 1e-3, $"{meters} m over the sea");
                Assert.AreEqual(meters, explorer.ClearanceMeters, meters * 1e-3, "The requested clearance is kept on flat ground");
            }
        }

        [Test]
        public void SteepSlope_ClimbingKeepsTheClearance_AtAnyFrameRate()
        {
            WorldTestHost host = CreateHost();
            PlanetExploreCamera explorer = CreateExplorer(host);
            PlanetInstance planet = host.Current;
            (Vector3d at, Vector3d uphill, double _) = Steepest(explorer, planet);
            var ends = new List<Vector3d>();
            foreach (int fps in new[] { 30, 60, 144 })
            {
                explorer.SetPose(at * (explorer.FloorRadius(at) + planet.Scale.ToUnits(PlanetExploreCamera.EyeHeightMeters)), uphill, PlanetTangentFrame.At(at).North);
                explorer.Place();
                double minimum = double.MaxValue, previous = explorer.Position.Magnitude, largestStep = 0.0;
                for (int i = 0; i < 2 * fps; i++)
                {
                    explorer.Advance(1.0 / fps, new ExploreInput { Forward = 1.0 });
                    minimum = Math.Min(minimum, explorer.SurfaceDistanceMeters);
                    double r = explorer.Position.Magnitude;
                    largestStep = Math.Max(largestStep, planet.Scale.ToMeters(Math.Abs(r - previous)));
                    previous = r;
                }
                Assert.GreaterOrEqual(minimum, explorer.MinClearanceMeters * 0.99, $"{fps} fps: never closer to the surface than the minimum clearance");
                ends.Add(explorer.Position);
                TestContext.WriteLine($"{fps} fps: closest {minimum:0.00} m, largest vertical step per frame {largestStep:0.00} m");
            }
            // Frame-rate independence: the same 2 s climb ends at nearly the same place.
            double spread = Math.Max(planet.Scale.ToMeters((ends[0] - ends[2]).Magnitude), planet.Scale.ToMeters((ends[1] - ends[2]).Magnitude));
            double travelled = planet.Scale.ToMeters((ends[2] - at * explorer.FloorRadius(at)).Magnitude);
            Assert.Less(spread, Math.Max(0.1 * travelled, 1.0), "30 / 60 / 144 fps end the climb at the same place");
        }

        // ------------------------------------------------------------------ Budget independence and patch validity

        static readonly Dictionary<int, PlanetInstance> Planets = new Dictionary<int, PlanetInstance>();

        static AdaptiveSurfaceSnapshot Snapshot(int seed = 1337)
        {
            if (!Planets.TryGetValue(seed, out PlanetInstance planet))
            {
                planet = new PlanetInstance(new PlanetDefinition { seed = seed });
                Planets[seed] = planet;
            }
            return new AdaptiveSurfaceSnapshot(1, planet, null, PlanetColorRamp.CreateDefault(), true, 16);
        }

        /// <summary>A view 10 m above the land at a direction, looking at the horizon (frustum from a real camera).</summary>
        static TerrainLodView GroundView(TerrainElevationSampler sampler, Vector3 d, float meters)
        {
            PlanetInstance planet = sampler.Planet;
            float surface = sampler.Radius * (1f + Mathf.Max(planet.Height.GetElevation(d), planet.SeaLevel));
            Vector3 position = d * (surface + (float)(meters / planet.Scale.RadiusMeters) * sampler.Radius);
            var view = new TerrainLodView(position, 60f, 1080);
            var go = new GameObject("Ground LOD Camera");
            try
            {
                Camera camera = go.AddComponent<Camera>();
                camera.fieldOfView = 60f;
                camera.aspect = 16f / 9f;
                camera.nearClipPlane = 1e-6f;
                camera.farClipPlane = 1000f;
                camera.transform.position = position;
                Vector3 tangent = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                camera.transform.rotation = Quaternion.LookRotation(tangent, d);
                view.Frustum = GeometryUtility.CalculateFrustumPlanes(camera);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            return view;
        }

        static Vector3 LandDirection(PlanetInstance planet)
        {
            const int count = 4000;
            for (int k = 0; k < count; k++)
            {
                double y = 1.0 - 2.0 * (k + 0.5) / count, r = Math.Sqrt(1.0 - y * y), phi = k * 2.399963229728653;
                var d = new Vector3((float)(r * Math.Cos(phi)), (float)y, (float)(r * Math.Sin(phi)));
                if (planet.Height.GetElevation(d) - planet.SeaLevel > 0.0002f) return d;
            }
            Assert.Fail("No land");
            return Vector3.up;
        }

        [Test]
        public void TheAuthoritativeHeightAndThePatchGeometry_DoNotDependOnTheBudget()
        {
            AdaptiveSurfaceSnapshot snapshot = Snapshot();
            TerrainElevationSampler sampler = snapshot.Sampler;
            PlanetInstance planet = snapshot.Planet;
            Vector3 d = LandDirection(planet);
            TerrainLodView view = GroundView(sampler, d, 10f);
            var probes = new List<Vector3d>();
            for (int k = 0; k < 300; k++)
            {
                double y = 1.0 - 2.0 * (k + 0.5) / 300, r = Math.Sqrt(1.0 - y * y), phi = k * 2.399963229728653;
                probes.Add(new Vector3d(r * Math.Cos(phi), y, r * Math.Sin(phi)));
            }
            probes.Add(new Vector3d(d));
            List<float> reference = probes.ConvertAll(p => planet.Height.GetElevation(p));

            var selections = new Dictionary<int, TerrainLodSelection>();
            foreach (int budget in new[] { 384, 512, 1024 })
            {
                var selector = new TerrainLodSelector(new AdaptiveTerrainSettings { maxLeafPatches = budget });
                selections[budget] = selector.Select(sampler, view);
                for (int k = 0; k < probes.Count; k++)
                {
                    Assert.AreEqual(reference[k], planet.Height.GetElevation(probes[k]), $"Budget {budget}: the authoritative height is unchanged");
                }
            }
            Assert.GreaterOrEqual(selections[1024].MaxLevel, selections[384].MaxLevel, "A larger budget never shows less detail under the camera");

            // A patch shown at two budgets is built identically: the budget changes which patches are displayed, never their surface.
            var builder = new AdaptivePatchGeometryBuilder();
            var a = new PatchGeometryData(snapshot.Topology.VertexCount);
            var b = new PatchGeometryData(snapshot.Topology.VertexCount);
            int shared = 0;
            foreach (PlanetPatchId id in selections[384].Leaves)
            {
                if (!selections[1024].IsLeaf(id) || id.Level < 10) continue;
                builder.Build(snapshot, id, a);
                new AdaptivePatchGeometryBuilder().Build(snapshot, id, b);
                for (int v = 0; v < a.Vertices.Length; v++)
                {
                    Assert.AreEqual(a.Vertices[v], b.Vertices[v], $"{id}: vertex {v}");
                    Assert.AreEqual(a.Normals[v], b.Normals[v], $"{id}: normal {v}");
                }
                if (++shared == 6) break;
            }
            Assert.Greater(shared, 0, "Some deep patches are displayed at both budgets");
        }

        [Test]
        public void NearGroundPatches_HaveFiniteVertices_UnitNormals_AndValidNonDegenerateTriangles_ForEveryMask()
        {
            AdaptiveSurfaceSnapshot snapshot = Snapshot();
            TerrainElevationSampler sampler = snapshot.Sampler;
            Vector3 d = LandDirection(snapshot.Planet);
            var selector = new TerrainLodSelector(new AdaptiveTerrainSettings());
            TerrainLodSelection selection = selector.Select(sampler, GroundView(sampler, d, 2f));
            Assert.GreaterOrEqual(selection.MaxLevel, 14, "Near the ground the finest patches are deep");
            PatchTopology topology = snapshot.Topology;
            var builder = new AdaptivePatchGeometryBuilder();
            var data = new PatchGeometryData(topology.VertexCount);
            int checkedPatches = 0;
            foreach (PlanetPatchId id in selection.Leaves)
            {
                if (id.Level < selection.MaxLevel - 1) continue;
                builder.Build(snapshot, id, data);
                for (int v = 0; v < topology.VertexCount; v++)
                {
                    Vector3 p = data.Vertices[v], n = data.Normals[v];
                    Assert.IsTrue(float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z), $"{id}: vertex {v} finite");
                    Assert.IsTrue(float.IsFinite(n.x) && float.IsFinite(n.y) && float.IsFinite(n.z), $"{id}: normal {v} finite");
                    Assert.AreEqual(1f, n.magnitude, 1e-3f, $"{id}: normal {v} unit length");
                }
                Vector3 outward = CubeSphereCoordinates.CenterDirection(id);
                for (int mask = 0; mask < PatchTopology.MaskCount; mask++)
                {
                    int[] t = topology.Indices(mask);
                    for (int i = 0; i < t.Length; i += 3)
                    {
                        Assert.IsTrue(t[i] >= 0 && t[i] < topology.VertexCount && t[i + 1] < topology.VertexCount && t[i + 2] < topology.VertexCount, $"{id} mask {mask}: index in range");
                        bool skirt = t[i] >= topology.GridVertexCount || t[i + 1] >= topology.GridVertexCount || t[i + 2] >= topology.GridVertexCount;
                        if (skirt) continue; // skirts are vertical walls hanging below the edges (checked by the seam tests)
                        Vector3 cross = Vector3.Cross(data.Vertices[t[i + 1]] - data.Vertices[t[i]], data.Vertices[t[i + 2]] - data.Vertices[t[i]]);
                        Assert.Greater(cross.magnitude, 0f, $"{id} mask {mask}: triangle {i / 3} not degenerate");
                        Assert.Greater(Vector3.Dot(cross, outward), 0f, $"{id} mask {mask}: triangle {i / 3} faces outward (not inverted)");
                    }
                }
                checkedPatches++;
            }
            Assert.Greater(checkedPatches, 10);
        }

        [Test]
        public void FixedCamera_TheDefaultBudgetSelectionIsStable()
        {
            Assert.AreEqual(1024, new AdaptiveTerrainSettings().maxLeafPatches, "V0.2.0F.3 default budget");
            Assert.GreaterOrEqual(new AdaptiveTerrainSettings().maxPatchMeshes, 2 * new AdaptiveTerrainSettings().maxLeafPatches, "Room for two coverings");
            AdaptiveSurfaceSnapshot snapshot = Snapshot();
            TerrainElevationSampler sampler = snapshot.Sampler;
            Vector3 d = LandDirection(snapshot.Planet);
            foreach (float meters in new[] { 2f, 100f, 1000f })
            {
                var selector = new TerrainLodSelector(new AdaptiveTerrainSettings());
                TerrainLodView view = GroundView(sampler, d, meters);
                var first = new HashSet<PlanetPatchId>(selector.Select(sampler, view).Leaves);
                Assert.LessOrEqual(first.Count, 1024);
                for (int frame = 0; frame < 20; frame++)
                {
                    TerrainLodSelection s = selector.Select(sampler, view);
                    Assert.IsTrue(first.SetEquals(s.Leaves), $"{meters} m, frame {frame}: the same leaves for the same camera");
                }
            }
        }
    }
}
