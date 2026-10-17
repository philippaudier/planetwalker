using System;
using System.Collections.Generic;
using NUnit.Framework;
using Planetwalker.Bootstrap;
using Planetwalker.CameraControl;
using Planetwalker.Core;
using Planetwalker.Persistence;
using Planetwalker.Terrain;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Surface exploration camera (V0.2.0D): clearance from the canonical elevation (K), floating-origin placement (J),
    /// orientation stability across the poles and cube-face edges (L), frame-rate independence (M), mode switching
    /// leaving the world untouched (P) and recovery after a regeneration (N).
    /// </summary>
    public class ExploreCameraTests
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

        /// <summary>A direction whose canonical elevation is above (land) or below (sea) the sea level by a margin.</summary>
        static Vector3d FindDirection(PlanetInstance planet, bool land)
        {
            const int count = 4000;
            for (int i = 0; i < count; i++)
            {
                double y = 1.0 - 2.0 * (i + 0.5) / count, r = Math.Sqrt(1.0 - y * y), phi = i * 2.399963229728653;
                var d = new Vector3d(r * Math.Cos(phi), y, r * Math.Sin(phi));
                double e = planet.Height.GetElevation(d) - planet.SeaLevel;
                if (land ? e > 0.003 : e < -0.003)
                {
                    return d;
                }
            }
            Assert.Fail(land ? "No land found" : "No sea found");
            return Vector3d.Up;
        }

        static Vector3d North(Vector3d direction) => PlanetTangentFrame.At(direction.Normalized).North;

        static void Place(PlanetExploreCamera explorer, Vector3d direction, double clearanceMeters, Vector3d look)
        {
            PlanetScale scale = explorer.Planet.Scale;
            Vector3d d = direction.Normalized;
            explorer.SetPose(d * (explorer.FloorRadius(d) + scale.ToUnits(clearanceMeters)), look, North(d));
            explorer.Place();
        }

        // ------------------------------------------------------------------ K: clearance

        [Test]
        public void K_Clearance_IsTheHeightAboveTheCanonicalTerrainOrTheSea()
        {
            WorldTestHost host = CreateHost();
            PlanetExploreCamera explorer = CreateExplorer(host);
            PlanetInstance planet = host.Current;
            PlanetScale scale = planet.Scale;

            Vector3d land = FindDirection(planet, true);
            Place(explorer, land, 100.0, North(land));
            Assert.AreEqual(100.0, explorer.ClearanceMeters, 1e-3, "Clearance above the terrain");
            double terrainMeters = scale.ToMeters(planet.Radius * (planet.Height.GetElevation(land) - planet.SeaLevel));
            Assert.AreEqual(100.0 + terrainMeters, explorer.AltitudeAboveSeaMeters, 1e-3, "Altitude above sea level = clearance + terrain height");

            Vector3d sea = FindDirection(planet, false);
            Place(explorer, sea, 250.0, North(sea));
            Assert.AreEqual(250.0, explorer.ClearanceMeters, 1e-3, "Over the sea the floor is the sea surface");
            Assert.AreEqual(explorer.ClearanceMeters, explorer.AltitudeAboveSeaMeters, 1e-3);

            // Below the ground: pushed up to the minimum clearance (never inside the terrain). V0.2.0F.3: the minimum holds
            // for the distance to the surface (equal to the vertical clearance on flat ground, smaller on a slope).
            Place(explorer, land, -500.0, North(land));
            Assert.GreaterOrEqual(explorer.ClearanceMeters, explorer.MinClearanceMeters - 1e-3, "Never below the minimum clearance");
            Assert.AreEqual(explorer.MinClearanceMeters, explorer.SurfaceDistanceMeters, explorer.MinClearanceMeters * 0.02, "Pushed up to the minimum distance to the surface, not further");

            // Flying down and around at full boost: the minimum clearance holds at every step.
            Place(explorer, land, 300.0, North(land));
            var input = new ExploreInput { Up = -1.0, Forward = 1.0, Right = 0.3, Boost = true };
            for (int i = 0; i < 600; i++)
            {
                explorer.Advance(1.0 / 60.0, input);
                Assert.GreaterOrEqual(explorer.ClearanceMeters, explorer.MinClearanceMeters - 1e-6, $"Step {i}");
                Assert.GreaterOrEqual(explorer.SurfaceDistanceMeters, explorer.MinClearanceMeters * 0.999 - 1e-6, $"Step {i}: distance to the surface");
            }
        }

        // ------------------------------------------------------------------ J: floating origin

        [Test]
        public void J_PlacementRebasesTheFloatingOrigin_WithoutChangingTheHeights()
        {
            WorldTestHost host = CreateHost();
            PlanetExploreCamera explorer = CreateExplorer(host);
            PlanetRenderFrame frame = host.Root.GetComponent<PlanetRenderFrame>();
            PlanetScale scale = host.Current.Scale;
            Vector3d land = FindDirection(host.Current, true);
            Place(explorer, land, 10.0, North(land));
            Assert.Greater(frame.RebaseCount, 0, "Close to the ground the origin follows the camera");
            Vector3 rendered = explorer.transform.position;
            Assert.Less(scale.ToMeters(rendered.magnitude / Math.Abs(host.Root.transform.lossyScale.x)), 10.0,
                "The camera is rendered near the world origin (small, precise float coordinates)");
            Assert.Less(scale.ToMeters((frame.RenderToPlanet(rendered) - explorer.Position).Magnitude), 0.01, "Render position maps back to the camera");
            Assert.AreEqual(10.0, explorer.ClearanceMeters, 1e-3, "Rebasing never changes the clearance");
            // Near plane at a quarter of the distance to the surface (V0.2.0F.3; the vertical clearance before, which on
            // slopes put it beyond the terrain in front), at least 5 cm; far plane past the planet.
            Camera camera = explorer.GetComponent<Camera>();
            double near = scale.ToMeters(camera.nearClipPlane / Math.Abs(host.Root.transform.lossyScale.x));
            Assert.AreEqual(0.25 * explorer.SurfaceDistanceMeters, near, 0.01);
            Assert.LessOrEqual(near, 2.5 + 0.01, "Never beyond a quarter of the vertical clearance");
            Assert.Greater(camera.farClipPlane, camera.nearClipPlane * 1000f);
        }

        // ------------------------------------------------------------------ L: orientation

        [Test]
        public void L_FlightAcrossThePolesAndCubeEdges_NeverRollsOrFlips()
        {
            WorldTestHost host = CreateHost();
            PlanetExploreCamera explorer = CreateExplorer(host);
            explorer.SpeedFactor = 10f;
            var starts = new[]
            {
                (new Vector3d(Math.Sin(0.1), Math.Cos(0.1), 0.0), Vector3d.Up),                       // towards the north pole
                (new Vector3d(0.02, -1.0, 0.01), new Vector3d(1.0, 0.0, 0.3)),                         // over the south pole
                (new Vector3d(1.0, 1.0, 0.98), new Vector3d(-1.0, 0.2, 1.0)),                          // across a cube corner
                (new Vector3d(1.0, 0.3, 0.999), new Vector3d(-1.0, 0.0, 1.0)),                         // along a cube edge
            };
            foreach ((Vector3d start, Vector3d heading) in starts)
            {
                Vector3d d = start.Normalized;
                Place(explorer, d, 50000.0, heading - d * Vector3d.Dot(heading, d));
                Vector3d plane = Vector3d.Cross(explorer.Position.Normalized, explorer.Forward).Normalized;
                Quaternion previous = explorer.transform.rotation;
                double travelled = 0.0;
                var input = new ExploreInput { Forward = 1.0, Boost = true };
                for (int i = 0; i < 400; i++)
                {
                    Vector3d before = explorer.Position.Normalized;
                    explorer.Advance(1.0 / 30.0, input);
                    Vector3d up = explorer.Position.Normalized;
                    double arc = Math.Atan2(Vector3d.Cross(before, up).Magnitude, Vector3d.Dot(before, up));
                    travelled += arc;
                    Assert.AreEqual(1.0, explorer.Forward.Magnitude, 1e-9, "Forward stays a unit vector");
                    Assert.AreEqual(0.0, Vector3d.Dot(explorer.Forward, up), 1e-9, "Forward stays tangent");
                    Assert.AreEqual(0.0, Vector3d.Dot(up, plane), 1e-6, "Straight flight follows a great circle");
                    Quaternion rotation = explorer.transform.rotation;
                    // The view only turns with the curvature of the ground flown over (the arc of this step), never more.
                    Assert.Less(Quaternion.Angle(previous, rotation), arc * (180.0 / Math.PI) + 0.05, $"No sudden turn (step {i} from {start})");
                    Vector3 renderUp = host.Root.transform.TransformDirection(up.ToVector3()).normalized;
                    Assert.Greater(Vector3.Dot(explorer.transform.up, renderUp), 0.999f, "No roll: the camera's up is the local up");
                    previous = rotation;
                }
                Assert.Greater(travelled, 0.2, "The flight went well past the pole / edge");
            }

            // A full turn on the spot right at the pole comes back to the same heading.
            Place(explorer, new Vector3d(1e-7, 1.0, 0.0), 1000.0, Vector3d.Right);
            Vector3d initial = explorer.Forward;
            for (int i = 0; i < 360; i++)
            {
                explorer.Advance(1.0 / 60.0, new ExploreInput { LookYaw = 1.0 });
            }
            Assert.Less((explorer.Forward - initial).Magnitude, 1e-6, "Yaw around the radial up at the pole");
            explorer.Advance(1.0 / 60.0, new ExploreInput { LookPitch = 500.0 });
            Assert.AreEqual(PlanetExploreCamera.MaxPitchDegrees, explorer.PitchDegrees, 1e-9, "Pitch clamped short of the vertical");
        }

        // ------------------------------------------------------------------ M: frame rate

        [Test]
        public void M_MovementAndDescent_DoNotDependOnTheFrameRate()
        {
            WorldTestHost host = CreateHost();
            PlanetExploreCamera explorer = CreateExplorer(host);
            Vector3d land = FindDirection(host.Current, true);

            double[] descent = new double[3];
            double[] distance = new double[3];
            int[] rates = { 30, 60, 144 };
            for (int r = 0; r < rates.Length; r++)
            {
                double dt = 1.0 / rates[r];
                Place(explorer, land, 100000.0, North(land));
                explorer.StartDescent(2000.0);
                for (int i = 0; i < 3 * rates[r]; i++)
                {
                    explorer.Advance(dt, default);
                }
                descent[r] = explorer.ClearanceMeters;

                Place(explorer, land, 1000.0, North(land));
                Vector3d start = explorer.Position.Normalized;
                for (int i = 0; i < 2 * rates[r]; i++)
                {
                    explorer.Advance(dt, new ExploreInput { Forward = 1.0 });
                }
                Vector3d end = explorer.Position.Normalized;
                distance[r] = host.Current.Scale.ToMeters(host.Current.Radius * Math.Atan2(Vector3d.Cross(start, end).Magnitude, Vector3d.Dot(start, end)));
            }
            Assert.Greater(descent[0], 2000.0);
            Assert.Less(descent[0], 100000.0 * 0.2, "The descent progressed");
            Assert.Greater(distance[0], 1000.0, "About one clearance per second");
            for (int r = 1; r < rates.Length; r++)
            {
                Assert.AreEqual(descent[0], descent[r], descent[0] * 1e-6, $"Descent at {rates[r]} fps");
                Assert.AreEqual(distance[0], distance[r], distance[0] * 0.02, $"Horizontal flight at {rates[r]} fps");
            }
        }

        [Test]
        public void Descend_GoesDownByDecades_ThenStopsAtEyeHeight()
        {
            WorldTestHost host = CreateHost();
            PlanetExploreCamera explorer = CreateExplorer(host);
            Vector3d land = FindDirection(host.Current, true);
            Place(explorer, land, 2000.0, North(land));
            var reached = new List<double>();
            for (int press = 0; press < 6; press++)
            {
                explorer.StartDescent(explorer.NextDescentTarget());
                for (int i = 0; i < 1200 && explorer.Descending; i++)
                {
                    explorer.Advance(1.0 / 60.0, default);
                }
                Assert.IsFalse(explorer.Descending, "Each step settles");
                reached.Add(explorer.ClearanceMeters);
            }
            Assert.AreEqual(200.0, reached[0], 200.0 * 0.011);
            Assert.AreEqual(reached[0] * 0.1, reached[1], reached[0] * 0.1 * 0.011, "Each step stops within 1% of a tenth of the previous clearance");
            Assert.AreEqual(PlanetExploreCamera.EyeHeightMeters, reached[2], PlanetExploreCamera.EyeHeightMeters * 0.011, "Last step: eye height");
            for (int i = 3; i < reached.Count; i++)
            {
                Assert.AreEqual(reached[2], reached[i], 0.05, "Stays at eye height");
            }
            Assert.GreaterOrEqual(PlanetExploreCamera.EyeHeightMeters, explorer.MinClearanceMeters, "The default minimum clearance allows eye height");
        }

        // ------------------------------------------------------------------ N: regeneration

        [Test]
        public void N_Regeneration_KeepsThePoseAndOnlyPushesTheCameraAboveTheNewTerrain()
        {
            WorldTestHost host = CreateHost();
            PlanetExploreCamera explorer = CreateExplorer(host);
            Vector3d land = FindDirection(host.Current, true);
            Place(explorer, land, 5.0, North(land));
            Vector3d direction = explorer.Position.Normalized;
            Vector3d look = explorer.LookDirection;

            host.Definition.seed = 609538;
            host.Regenerate();
            explorer.OnPlanetChanged(host.Current);
            Assert.AreSame(host.Current, explorer.Planet, "The explorer follows the new planet");
            Assert.Less((explorer.Position.Normalized - direction).Magnitude, 1e-12, "Same place: no teleport");
            Assert.Less((explorer.LookDirection - look).Magnitude, 1e-9, "Same view direction");
            Assert.GreaterOrEqual(explorer.ClearanceMeters, explorer.MinClearanceMeters - 1e-6, "Above the new terrain");
        }

        // ------------------------------------------------------------------ Close-range ground layer (Q, J)

        [Test]
        public void Q_GroundLayer_IsAnchoredToThePlanet_AndOffWithoutSurfaceDetail()
        {
            WorldTestHost host = CreateHost();
            host.Definition.surfaceDetail = true;
            host.Regenerate();
            PlanetExploreCamera explorer = CreateExplorer(host);
            Camera camera = explorer.GetComponent<Camera>();
            AdaptiveTerrainRenderer terrain = AdaptiveTerrainRenderer.GetOrCreate(host.Visual);
            terrain.BackgroundConstruction = false;
            terrain.ViewCamera = camera;
            terrain.enabled = true;
            PlanetRenderFrame frame = host.Root.GetComponent<PlanetRenderFrame>();
            PlanetScale scale = host.Current.Scale;
            Vector3d land = FindDirection(host.Current, true);
            var block = new MaterialPropertyBlock();
            for (int k = 0; k < 4; k++)
            {
                Place(explorer, land, 20.0 + 300.0 * k, North(land));
                for (int t = 0; t < 8; t++) terrain.Tick(camera);
                terrain.WriteGroundDetail(block, camera);
                Vector4 parameters = block.GetVector("_GroundParams");
                Assert.AreEqual(1f, parameters.z, "On with surface detail");
                Vector4 origin = block.GetVector("_GroundOrigin");
                Assert.AreEqual(scale.MetersPerUnit, origin.w, scale.MetersPerUnit * 1e-6);
                // The origin is a lattice point of planet space (meters), whatever the floating origin: the periodic
                // noise is then the same function of the planet position for every camera position.
                Vector3d planetMeters = frame.RenderToPlanet(new Vector3(origin.x, origin.y, origin.z)) * scale.MetersPerUnit;
                double period = AdaptiveTerrainRenderer.GroundNoisePeriodMeters;
                foreach (double v in new[] { planetMeters.x, planetMeters.y, planetMeters.z })
                {
                    Assert.AreEqual(0.0, v / period - Math.Round(v / period), 1e-4, "Origin on the 1024 m lattice");
                }
                Assert.Less(scale.ToMeters((frame.RenderToPlanet(new Vector3(origin.x, origin.y, origin.z)) - explorer.Position).Magnitude), 1000.0,
                    "Origin near the camera (small shader coordinates)");
            }

            host.Definition.surfaceDetail = false;
            host.Regenerate();
            for (int t = 0; t < 8; t++) terrain.Tick(camera);
            terrain.WriteGroundDetail(block, camera);
            Assert.AreEqual(0f, block.GetVector("_GroundParams").z, "Off without surface detail: the baseline look");
            terrain.GroundDetailStrength = 0f;
            host.Definition.surfaceDetail = true;
            host.Regenerate();
            for (int t = 0; t < 8; t++) terrain.Tick(camera);
            terrain.WriteGroundDetail(block, camera);
            Assert.AreEqual(0f, block.GetVector("_GroundParams").z, "Off at strength 0");
        }

        // ------------------------------------------------------------------ P: mode switch

        [Test]
        public void P_SwitchingOrbitalAndExploreModes_LeavesTheWorldUntouched()
        {
            string directory = WorldArchiveTestUtil.CreateTempDirectory();
            try
            {
                var before = new HashSet<GameObject>(Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None));
                var bootstrap = new GameObject("Explore Bootstrap Test").AddComponent<PlanetBootstrap>();
                bootstrap.EnsureSceneObjects();
                foreach (GameObject o in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
                {
                    if (!before.Contains(o) && o.transform.parent == null) created.Add(o);
                }
                bootstrap.UseWorldStore(new WorldArchiveStore(directory));
                bootstrap.Definition.resolution = 16;
                bootstrap.Visual.Climate.resolution = ClimateSettings.LowResolution;
                bootstrap.Regenerate();
                bootstrap.Worlds.ResetBaseline();

                PlanetInstance planet = bootstrap.Current;
                int generations = bootstrap.GenerationCount;
                string definition = JsonUtility.ToJson(bootstrap.Definition);
                string climate = JsonUtility.ToJson(bootstrap.Visual.Climate);
                string clouds = JsonUtility.ToJson(bootstrap.Visual.Clouds);
                var probe = new Vector3d(0.3, 0.8, -0.5).Normalized;
                double height = planet.Height.GetElevation(probe);

                Assert.IsNotNull(bootstrap.ExploreCamera, "The bootstrap adds the explorer");
                Assert.IsFalse(bootstrap.Exploring, "Starts in Orbital View");
                bootstrap.SetExploring(true);
                Assert.IsTrue(bootstrap.Exploring);
                Assert.IsFalse(bootstrap.OrbitCamera.enabled, "Exactly one camera controller");
                Assert.AreSame(planet, bootstrap.ExploreCamera.Planet);
                for (int i = 0; i < 120; i++)
                {
                    bootstrap.ExploreCamera.Advance(1.0 / 60.0, new ExploreInput { Forward = 1.0, Up = -0.5, LookYaw = 0.5 });
                }
                bootstrap.SetExploring(false);
                Assert.IsFalse(bootstrap.Exploring);
                Assert.IsTrue(bootstrap.OrbitCamera.enabled);

                Assert.AreEqual(generations, bootstrap.GenerationCount, "No regeneration");
                Assert.AreSame(planet, bootstrap.Current, "Same planet instance");
                Assert.AreEqual(definition, JsonUtility.ToJson(bootstrap.Definition), "Definition unchanged");
                Assert.AreEqual(climate, JsonUtility.ToJson(bootstrap.Visual.Climate), "Climate unchanged");
                Assert.AreEqual(clouds, JsonUtility.ToJson(bootstrap.Visual.Clouds), "Clouds unchanged");
                Assert.AreEqual(height, bootstrap.Current.Height.GetElevation(probe), "Terrain unchanged");
            }
            finally
            {
                WorldArchiveTestUtil.DeleteTempDirectory(directory);
            }
        }
    }
}
