using System;
using System.Collections.Generic;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Persistence;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Orbit camera precision (V0.2.0C fix). Reproduces the reported jitter: with a float orbit state (yaw, pitch,
    /// distance) the camera could only move in steps of the float resolution near 100 render units and 170 degrees
    /// (about 1.7 m at Earth scale, 13 m after a few unwrapped turns), so a slow orbit close to the ground advanced
    /// by visible jumps. The state is now double, the yaw wrapped into [-180, 180).
    /// </summary>
    public class OrbitCameraPrecisionTests
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

        WorldTestHost CreateHost()
        {
            var host = new WorldTestHost(created);
            WorldConfiguration c = WorldArchiveTestUtil.Tweaked(1337, flipFlags: false);
            c.Terrain.resolution = 16;
            host.Apply(c);
            host.Regenerate();
            PlanetRenderFrame.GetOrCreate(host.Root.transform);
            return host;
        }

        [Test]
        public void SlowLowOrbit_MovesSmoothly_AfterManyTurns()
        {
            WorldTestHost host = CreateHost();
            PlanetScale scale = host.Current.Scale;
            PlanetRenderFrame frame = PlanetRenderFrame.GetOrCreate(host.Root.transform);
            double metersPerUnit = scale.ToMeters(1.0);
            float bounding = host.Visual.BoundingRadius;
            float altitude = (float)scale.ToUnits(200.0);
            host.Camera.SetView(170f, 15f, bounding + altitude);
            // Many full turns first (the float yaw grew without bound before the fix).
            for (int i = 0; i < 4; i++)
            {
                host.Camera.Orbit(360.0, 0.0);
            }
            host.Camera.Orbit(0.37, 0.0);
            for (int f = 0; f < 600; f++)
            {
                host.Camera.Advance(1.0 / 60.0); // settle (the smoothing converges)
            }
            Assert.That(host.Camera.TargetYaw, Is.InRange(-180f, 180f), "Yaw wrapped into [-180, 180)");

            // A slow drag: 0.0002 degree per frame (about 22 m at Earth scale) for 120 frames, then 120 frames of settling.
            // Reference: the same exponential smoothing simulated exactly in double (rotation smoothing 12 per second).
            Transform cameraTransform = host.Camera.transform;
            double expectedYaw = host.Camera.PreciseYaw, expectedTarget = expectedYaw, pitch = host.Camera.PrecisePitch * Math.PI / 180.0;
            double distance = host.Camera.PreciseDistance, blend = 1.0 - Math.Exp(-12.0 / 60.0);
            double worst = 0, longest = 0;
            Vector3d previous = frame.RenderToPlanet(cameraTransform.position);
            for (int f = 0; f < 240; f++)
            {
                if (f < 120)
                {
                    host.Camera.Orbit(0.0002, 0.0);
                    expectedTarget += 0.0002;
                }
                host.Camera.Advance(1.0 / 60.0);
                expectedYaw += (expectedTarget - expectedYaw) * blend;
                double y = expectedYaw * Math.PI / 180.0;
                var expected = new Vector3d(-Math.Cos(pitch) * Math.Sin(y), Math.Sin(pitch), -Math.Cos(pitch) * Math.Cos(y)) * distance;
                Vector3d placed = frame.RenderToPlanet(cameraTransform.position);
                worst = Math.Max(worst, (placed - expected).Magnitude * metersPerUnit);
                longest = Math.Max(longest, (placed - previous).Magnitude * metersPerUnit);
                previous = placed;
            }
            TestContext.WriteLine($"slow orbit 200 m above the ground: largest step {longest:0.00} m, largest deviation from the exact smooth path {worst * 1000.0:0.0} mm");
            Assert.Greater(longest, 0.5, "Setup: the camera moves");
            Assert.Less(worst, 0.01, "On the exact smooth path within 1 cm (float state: meter-scale steps)");
        }

        [Test]
        public void YawWrap_KeepsTheViewAndTheArchiveConvention()
        {
            WorldTestHost host = CreateHost();
            float distance = host.Camera.TargetDistance;
            host.Camera.SetView(-137.5f, 41f, distance);
            Assert.AreEqual(-137.5f, host.Camera.TargetYaw, 1e-4f, "Archive convention preserved");
            Vector3d before = host.Camera.PreciseOffset;
            host.Camera.SetView(-137.5f + 3f * 360f, 41f, distance);
            Assert.AreEqual(-137.5f, host.Camera.TargetYaw, 1e-3f, "Whole turns removed");
            Assert.That((host.Camera.PreciseOffset - before).Magnitude, Is.LessThan(1e-9 * distance), "Same view");
            // Crossing the wrap during smoothing: no jump of the placed camera.
            host.Camera.SetView(179.9f, 10f, distance);
            Vector3d last = host.Camera.PreciseOffset;
            host.Camera.Orbit(0.5, 0.0);
            for (int f = 0; f < 120; f++)
            {
                host.Camera.Advance(1.0 / 60.0);
                Vector3d now = host.Camera.PreciseOffset;
                Assert.That((now - last).Magnitude, Is.LessThan(0.5 * Math.PI / 180.0 * distance), $"frame {f}: no jump across +-180");
                last = now;
            }
            Assert.That(host.Camera.TargetYaw, Is.InRange(-180f, 180f));
            Assert.AreEqual(-179.6, host.Camera.PreciseYaw, 1e-3, "Arrived on the other side of the wrap");
        }
    }
}
