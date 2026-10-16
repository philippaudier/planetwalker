using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
    /// Physical scale, planet coordinates and the floating render origin (V0.2.0C). A to E are engine independent
    /// (scale conversions, altitude, tangent frames and their pole rule, double round trips); F to H and X need the
    /// Unity engine (the render frame on a planet root, the terrain sampler's independence from the frame, the orbit
    /// camera's clip planes and double placement, the consistency of every planet system after a rebase).
    /// </summary>
    public class PlanetScaleAndFrameTests
    {
        static readonly PlanetScale Earth = new PlanetScale(100.0, PlanetScale.DefaultRadiusKilometers * 1000.0);

        readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in created)
            {
                if (o != null)
                {
                    Object.DestroyImmediate(o);
                }
            }
            created.Clear();
        }

        static Vector3d RandomDirection(System.Random random)
        {
            // Uniform on the sphere (Marsaglia).
            double z = 2.0 * random.NextDouble() - 1.0;
            double a = 2.0 * Math.PI * random.NextDouble();
            double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
            return new Vector3d(r * Math.Cos(a), r * Math.Sin(a), z);
        }

        static void AssertOrthonormal(PlanetTangentFrame frame, string context, double tolerance = 1e-12)
        {
            Assert.That(frame.Up.Magnitude, Is.EqualTo(1.0).Within(tolerance), context + ": |Up|");
            Assert.That(frame.North.Magnitude, Is.EqualTo(1.0).Within(tolerance), context + ": |North|");
            Assert.That(frame.East.Magnitude, Is.EqualTo(1.0).Within(tolerance), context + ": |East|");
            Assert.That(Math.Abs(Vector3d.Dot(frame.Up, frame.North)), Is.LessThan(tolerance), context + ": Up.North");
            Assert.That(Math.Abs(Vector3d.Dot(frame.Up, frame.East)), Is.LessThan(tolerance), context + ": Up.East");
            Assert.That(Math.Abs(Vector3d.Dot(frame.North, frame.East)), Is.LessThan(tolerance), context + ": North.East");
            Assert.That((Vector3d.Cross(frame.North, frame.Up) - frame.East).Magnitude, Is.LessThan(tolerance), context + ": East = North x Up");
            Assert.IsTrue(IsFinite(frame.Up) && IsFinite(frame.North) && IsFinite(frame.East), context + ": finite");
        }

        /// <summary>A minimal application like the renderer tests: tweaked configuration, resolution 16, one generation.</summary>
        WorldTestHost CreateHost(int seed)
        {
            var host = new WorldTestHost(created);
            WorldConfiguration c = WorldArchiveTestUtil.Tweaked(seed, flipFlags: false);
            c.Terrain.resolution = 16;
            host.Apply(c);
            host.Regenerate();
            return host;
        }

        static bool IsFinite(Vector3d v) => !double.IsNaN(v.x + v.y + v.z) && !double.IsInfinity(v.x + v.y + v.z);

        static bool SameBits(Vector3d a, Vector3d b) =>
            BitConverter.DoubleToInt64Bits(a.x) == BitConverter.DoubleToInt64Bits(b.x)
            && BitConverter.DoubleToInt64Bits(a.y) == BitConverter.DoubleToInt64Bits(b.y)
            && BitConverter.DoubleToInt64Bits(a.z) == BitConverter.DoubleToInt64Bits(b.z);

        static bool SameBits(Vector3 a, Vector3 b) =>
            BitConverter.SingleToInt32Bits(a.x) == BitConverter.SingleToInt32Bits(b.x)
            && BitConverter.SingleToInt32Bits(a.y) == BitConverter.SingleToInt32Bits(b.y)
            && BitConverter.SingleToInt32Bits(a.z) == BitConverter.SingleToInt32Bits(b.z);

        // ------------------------------------------------------------ A
        [Test]
        public void A_PlanetScale_ConversionsAreReversibleAndCalibrated()
        {
            foreach (double radiusKm in new[] { PlanetScale.MinRadiusKilometers, 1737.4, 6371.0, 69911.0, PlanetScale.MaxRadiusKilometers })
            {
                var scale = new PlanetScale(100.0, radiusKm * 1000.0);
                foreach (double x in new[] { 1e-6, 0.001, 1.0, 37.5, 100.0, 12345.678 })
                {
                    Assert.That(scale.ToMeters(scale.ToUnits(x)) / x, Is.EqualTo(1.0).Within(1e-9), "meters -> units -> meters");
                    Assert.That(scale.ToUnits(scale.ToMeters(x)) / x, Is.EqualTo(1.0).Within(1e-9), "units -> meters -> units");
                    Assert.That(scale.ElevationToMeters(scale.MetersToElevation(x)) / x, Is.EqualTo(1.0).Within(1e-9));
                    Assert.That(scale.WavelengthOfFrequency(scale.FrequencyOfWavelength(x)) / x, Is.EqualTo(1.0).Within(1e-9), "wavelength <-> frequency");
                    Assert.That(scale.ArcRadians(scale.ArcMeters(x)) / x, Is.EqualTo(1.0).Within(1e-9));
                }
                Assert.That(scale.MetersPerUnit * scale.UnitsPerMeter, Is.EqualTo(1.0).Within(1e-12));
                Assert.That(scale.SurfaceRadius(0.0), Is.EqualTo(100.0));
                Assert.That(scale.SurfaceRadius(0.045), Is.EqualTo(104.5).Within(1e-12));
            }
            // Earth: 1 unit = 63.71 km; a float coordinate of magnitude 100 steps by 2^-17 units = 0.49 m.
            Assert.That(Earth.MetersPerUnit, Is.EqualTo(63710.0).Within(1e-9));
            Assert.That(Earth.FloatPrecisionMeters(100.0), Is.EqualTo(0.486).Within(0.01));
            Assert.That(Earth.FloatPrecisionMeters(1.0), Is.EqualTo(0.0076).Within(0.0005));
            Assert.That(Earth.FloatPrecisionMeters(0.0), Is.GreaterThan(0.0));
            // A circumference-sized wavelength has frequency 1 cycle per radian.
            Assert.That(Earth.FrequencyOfWavelength(2.0 * Math.PI * Earth.RadiusMeters), Is.EqualTo(1.0).Within(1e-12));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PlanetScale(0.0, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PlanetScale(1.0, -1.0));

            Assert.AreEqual("5.3 m", PlanetScale.FormatDistance(5.3));
            Assert.AreEqual("0.0 m", PlanetScale.FormatDistance(0.0));
            Assert.AreEqual("-7.5 m", PlanetScale.FormatDistance(-7.5));
            Assert.AreEqual("123 m", PlanetScale.FormatDistance(123.4));
            Assert.AreEqual("999 m", PlanetScale.FormatDistance(999.4));
            Assert.AreEqual("4.57 km", PlanetScale.FormatDistance(4567.0));
            Assert.AreEqual("12,346 km", PlanetScale.FormatDistance(12345678.0));

            var table = new List<string> { "Float32 position step at Earth scale (1 unit = 63.71 km):" };
            foreach (double magnitude in new[] { 1.0, 10.0, 100.0, 1000.0 })
            {
                table.Add(string.Format(CultureInfo.InvariantCulture, "  |p| = {0,5:0} units -> {1}", magnitude,
                    PlanetScale.FormatDistance(Earth.FloatPrecisionMeters(magnitude))));
            }
            WorldArchiveTestUtil.Report(string.Join("\n", table));
        }

        // ------------------------------------------------------------ B
        [Test]
        public void B_Altitude_RoundTripsThroughAtAltitude()
        {
            var random = new System.Random(2026);
            foreach (double radiusKm in new[] { 10.0, 6371.0, 100000.0 })
            {
                var scale = new PlanetScale(100.0, radiusKm * 1000.0);
                for (int i = 0; i < 12; i++)
                {
                    Vector3d d = i < 6 ? new[] { Vector3d.Up, -Vector3d.Up, Vector3d.Right, -Vector3d.Right, Vector3d.Forward, -Vector3d.Forward }[i]
                        : RandomDirection(random) * (0.5 + random.NextDouble());
                    foreach (double elevation in new[] { -0.02, 0.0, 0.0123, 0.045 })
                    {
                        for (double h = 1.0; h <= 1e6; h *= 10.0)
                        {
                            Vector3d p = PlanetCoordinates.AtAltitude(d, scale, elevation, scale.ToUnits(h));
                            Assert.That(scale.AltitudeMeters(p, elevation), Is.EqualTo(h).Within(1e-6), "altitude " + h + " m, R " + radiusKm + " km");
                            Assert.That(scale.ToMeters(scale.AltitudeUnits(p, elevation)), Is.EqualTo(h).Within(1e-6));
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------ C
        [Test]
        public void C_TangentFrames_AreOrthonormalEverywhere()
        {
            var random = new System.Random(1337);
            for (int i = 0; i < 10000; i++)
            {
                Vector3d d = RandomDirection(random) * (0.01 + 200.0 * random.NextDouble());
                PlanetTangentFrame frame = PlanetTangentFrame.At(d);
                AssertOrthonormal(frame, "direction " + d);
                Assert.That((frame.Up - d.Normalized).Magnitude, Is.LessThan(1e-15), "Up is the direction");
                // Local <-> planet vectors round trip.
                var local = new Vector3d(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
                Assert.That((frame.ToLocal(frame.FromLocal(local)) - local).Magnitude, Is.LessThan(1e-14));
                // Away from the poles North points to +Y along the meridian.
                if (Math.Abs(frame.Up.y) < 0.98)
                {
                    Assert.That(frame.North.y, Is.GreaterThan(0.0), "North has a +Y component");
                    Assert.That(Math.Abs(Vector3d.Dot(frame.North, Vector3d.Cross(Vector3d.Up, frame.Up))), Is.LessThan(1e-12), "North lies in the meridian plane");
                }
            }
            Assert.Throws<ArgumentException>(() => PlanetTangentFrame.At(Vector3d.Zero));
        }

        // ------------------------------------------------------------ D
        [Test]
        public void D_TangentFrames_AreStableAtThePolesAndSwitchExactlyAtTheThreshold()
        {
            const double tiny = 1e-9;
            var poles = new List<Vector3d>();
            foreach (Vector3d axis in new[] { Vector3d.Up, -Vector3d.Up, Vector3d.Right, -Vector3d.Right })
            {
                poles.Add(axis);
                poles.Add(axis * 123.456);
                foreach (Vector3d off in new[] { Vector3d.Forward, -Vector3d.Forward, Vector3d.Up, Vector3d.Right, new Vector3d(1, 1, 1) })
                {
                    if (Math.Abs(Vector3d.Dot(off.Normalized, axis)) < 0.999)
                    {
                        poles.Add(axis + off.Normalized * tiny); // within 1e-9 rad of the pole
                    }
                }
            }
            foreach (Vector3d d in poles)
            {
                PlanetTangentFrame frame = PlanetTangentFrame.At(d);
                AssertOrthonormal(frame, "pole direction " + d);
                PlanetTangentFrame again = PlanetTangentFrame.At(d);
                Assert.IsTrue(SameBits(frame.Up, again.Up) && SameBits(frame.North, again.North) && SameBits(frame.East, again.East), "deterministic at " + d);
            }

            // The threshold band: |y| = 0.99 +- 1e-7, on both hemispheres and several longitudes. The reference is +Y
            // strictly below the threshold and +X from it on; North lies in the plane of Up and the reference.
            int distinguishable = 0;
            foreach (double sign in new[] { 1.0, -1.0 })
            {
                foreach (double longitude in new[] { 0.0, 0.7, 2.1, 4.0, 5.9 })
                {
                    foreach (double delta in new[] { -1e-7, -1e-12, 0.0, 1e-12, 1e-7 })
                    {
                        double y = sign * (PlanetTangentFrame.PoleThreshold + delta);
                        double r = Math.Sqrt(1.0 - y * y);
                        var d = new Vector3d(r * Math.Cos(longitude), y, r * Math.Sin(longitude));
                        PlanetTangentFrame frame = PlanetTangentFrame.At(d);
                        AssertOrthonormal(frame, "band direction " + d, 1e-11);
                        Vector3d reference = Math.Abs(frame.Up.y) < PlanetTangentFrame.PoleThreshold ? Vector3d.Up : Vector3d.Right;
                        Vector3d expectedNorth = (reference - frame.Up * Vector3d.Dot(reference, frame.Up)).Normalized;
                        Assert.That((frame.North - expectedNorth).Magnitude, Is.LessThan(1e-9), "reference rule at up.y = " + frame.Up.y.ToString("R", CultureInfo.InvariantCulture));
                        // Where the two references give distinguishable Norths (everywhere but the meridian of X in the
                        // southern band, where they coincide), the frame must not have used the other one.
                        Vector3d other = reference == Vector3d.Up ? Vector3d.Right : Vector3d.Up;
                        Vector3d otherNorth = (other - frame.Up * Vector3d.Dot(other, frame.Up)).Normalized;
                        if ((expectedNorth - otherNorth).Magnitude > 0.1)
                        {
                            Assert.That((frame.North - otherNorth).Magnitude, Is.GreaterThan(0.1), "the switch is exactly at the threshold");
                            distinguishable++;
                        }
                    }
                }
            }
            Assert.That(distinguishable, Is.GreaterThan(40), "the switch was observable on both sides of the threshold");
        }

        // ------------------------------------------------------------ E
        [Test]
        public void E_DoubleRoundTrips_PositionDirectionStepAndAngle()
        {
            var random = new System.Random(42);
            for (int i = 0; i < 2000; i++)
            {
                Vector3d d = RandomDirection(random);
                double radius = Math.Pow(10.0, -2.0 + 8.0 * random.NextDouble()); // 1e-2 .. 1e6 units
                Vector3d p = PlanetCoordinates.Position(d, radius);
                Assert.That(Math.Abs(PlanetCoordinates.RadialDistance(p) - radius) / radius, Is.LessThan(1e-13), "radial distance");
                Assert.That((PlanetCoordinates.Direction(p) - d).Magnitude, Is.LessThan(1e-13), "direction");
            }

            // 1 m north then 1 m south (one meridian, exactly reversible): back within 1e-9 m. 1 m east then 1 m west
            // along the local East of each point is only reversible up to the convergence of the meridians
            // (1 m x tan(latitude) / R), which the double arithmetic resolves well below.
            double worst = 0.0, worstEast = 0.0;
            for (int i = 0; i < 2000; i++)
            {
                Vector3d d = RandomDirection(random);
                Vector3d back = PlanetCoordinates.Step(PlanetCoordinates.Step(d, Earth, 0.0, 1.0), Earth, 0.0, -1.0);
                worst = Math.Max(worst, (back - d).Magnitude * Earth.RadiusMeters); // chord: the double floor itself (~1e-16 x R)
                if (Math.Abs(d.y) < 0.98)
                {
                    Vector3d backEast = PlanetCoordinates.Step(PlanetCoordinates.Step(d, Earth, 1.0, 0.0), Earth, -1.0, 0.0);
                    double meters = (backEast - d).Magnitude * Earth.RadiusMeters;
                    double latitude = Math.Asin(Math.Abs(d.y));
                    Assert.That(meters, Is.LessThan(Earth.ArcRadians(1.0) * Math.Tan(latitude) * 1.5 + 1e-9), "east then west, geometric bound");
                    worstEast = Math.Max(worstEast, meters);
                }
                Vector3d north = PlanetCoordinates.Step(d, Earth, 0.0, 2.5);
                // Angle resolves nearly equal unit vectors to ~eps/theta (2.5e-10 relative here): 1e-8 m is its floor.
                Assert.That(Earth.ArcMeters(PlanetCoordinates.Angle(d, north)), Is.EqualTo(2.5).Within(1e-8), "step length");
                if (Math.Abs(d.y) < 0.98)
                {
                    Assert.That(north.y, Is.GreaterThan(d.y), "north goes up in latitude");
                }
                Assert.That(north.Magnitude, Is.EqualTo(1.0).Within(1e-14));
            }
            // One ulp of a unit-vector component is 2.2e-16 x 6371 km = 1.4 nm: a few ulps is the best double can do.
            Assert.That(worst, Is.LessThan(5e-9), "north then south");
            WorldArchiveTestUtil.Report(string.Format(CultureInfo.InvariantCulture,
                "Step reversibility at Earth scale: north/south {0:0.0e0} m, east/west (meridian convergence) {1:0.0e0} m", worst, worstEast));
            Assert.That((PlanetCoordinates.Step(Vector3d.Forward, Earth, 0.0, 0.0) - Vector3d.Forward).Magnitude, Is.LessThan(1e-15), "zero step");

            // Angle of nearly equal vectors: 1 m / R, where acos would already be unusable (1.6e-7 rad).
            double oneMeter = Earth.ArcRadians(1.0);
            for (int i = 0; i < 200; i++)
            {
                Vector3d d = RandomDirection(random);
                PlanetTangentFrame frame = PlanetTangentFrame.At(d);
                Vector3d near = (frame.Up * Math.Cos(oneMeter) + frame.East * Math.Sin(oneMeter));
                Assert.That(PlanetCoordinates.Angle(d, near) / oneMeter, Is.EqualTo(1.0).Within(1e-8), "1 m angle");
                Assert.That(PlanetCoordinates.Angle(d, d), Is.EqualTo(0.0), "identical directions");
                Assert.That(PlanetCoordinates.Angle(d, -d), Is.EqualTo(Math.PI).Within(1e-12), "opposite directions");
                Assert.That(PlanetCoordinates.Angle(d * 3.0, d * 0.001), Is.EqualTo(0.0).Within(1e-15), "length independent");
            }
        }

        // ------------------------------------------------------------ F (Unity)
        [Test]
        public void F_RenderFrame_SnapsTheOriginAndKeepsRootAndPatchesConsistent()
        {
            var root = new GameObject("Frame Test Planet");
            created.Add(root);
            PlanetRenderFrame frame = PlanetRenderFrame.GetOrCreate(root.transform);
            Assert.AreSame(frame, PlanetRenderFrame.GetOrCreate(root.transform), "never duplicated");
            Assert.AreEqual(Vector3d.Zero, frame.Origin);
            Assert.AreEqual(0, frame.RebaseCount);
            Assert.IsTrue(frame.AutoRebase, "floating origin on by default");

            // Two planet-space points 0.3 units apart near the surface, and a camera 100 m above the first.
            var scale = Earth;
            Vector3d direction = new Vector3d(0.3, 0.5, -0.81).Normalized;
            Vector3d surface = PlanetCoordinates.Position(direction, 101.234567891);
            Vector3d other = surface + PlanetTangentFrame.At(direction).East * 0.3;
            Vector3d camera = PlanetCoordinates.AtAltitude(direction, scale, 0.01234567891, scale.ToUnits(100.0));
            var child = new GameObject("Child").transform;
            child.SetParent(root.transform, false);
            child.localPosition = surface.ToVector3();

            int rebased = 0;
            frame.Rebased += () => rebased++;
            frame.Rebase(camera);
            Assert.AreEqual(1, frame.RebaseCount);
            Assert.AreEqual(1, rebased);
            // Snapping: the root's world position is exactly -Origin and PlanetToRender(0) is exactly the root position.
            Assert.IsTrue(SameBits(root.transform.position, (-frame.Origin).ToVector3()), "root at -Origin, bitwise");
            Assert.IsTrue(SameBits(root.transform.position, frame.PlanetToRender(Vector3d.Zero)), "PlanetToRender(center) is the root position");
            Assert.IsTrue(SameBits(root.transform.position, frame.WorldRootPosition));
            Assert.That((frame.Origin - camera).Magnitude, Is.LessThan(1e-5), "origin at the camera (float snapped)");
            Assert.That(frame.PlanetToRender(camera).magnitude, Is.LessThan(2e-5), "the camera is at the world origin (within the float snap of the root: 7.6e-6 units at magnitude 100)");
            Assert.That(frame.OriginOffsetUnits, Is.EqualTo(camera.Magnitude).Within(1e-5));

            // Patch (double translation) vs child object (float transform hierarchy) at the same planet position, both within 1 unit.
            Vector3 patch = frame.PatchMatrix(surface.ToVector3()).GetPosition();
            Assert.That((patch - child.position).magnitude, Is.LessThan(1e-6), "patch and child coincide in render space");
            Assert.That(patch.magnitude, Is.LessThan(1.0));
            Vector3 patchOther = frame.PatchMatrix(other.ToVector3()).GetPosition();
            Assert.That((patchOther - patch).magnitude, Is.EqualTo(0.3).Within(1e-6), "relative positions preserved");
            Assert.That((frame.RenderToPlanet(patch) - new Vector3d(surface.ToVector3())).Magnitude, Is.LessThan(1e-6), "render -> planet");
            Assert.That((frame.RenderToPlanet(frame.PlanetToRender(camera)) - camera).Magnitude, Is.LessThan(1e-9), "exact near the origin");

            // Several rebases around the area: the relative positions survive each one, the count increments.
            var random = new System.Random(7);
            for (int i = 0; i < 5; i++)
            {
                Vector3d jitter = new Vector3d(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5) * 0.02;
                frame.Rebase(camera + jitter);
                Assert.AreEqual(i + 2, frame.RebaseCount);
                Assert.IsTrue(SameBits(root.transform.position, (-frame.Origin).ToVector3()), "snapped after rebase " + i);
                Vector3 a = frame.PatchMatrix(surface.ToVector3()).GetPosition(), b = frame.PatchMatrix(other.ToVector3()).GetPosition();
                Assert.That((b - a).magnitude, Is.EqualTo(0.3).Within(1e-6));
                Assert.That((a - child.position).magnitude, Is.LessThan(1e-6));
            }
            // MaybeRebase: nothing within a quarter of the altitude, a rebase beyond it; nothing when off. (At 1 km the
            // quarter-altitude threshold, 3.9e-3 units, is above the 1e-3 floor that applies at 100 m.)
            double altitude = scale.ToUnits(1000.0);
            int before = frame.RebaseCount;
            Assert.IsFalse(frame.MaybeRebase(frame.Origin + PlanetTangentFrame.At(direction).North * (0.2 * altitude), altitude));
            Assert.IsTrue(frame.MaybeRebase(frame.Origin + PlanetTangentFrame.At(direction).North * (0.3 * altitude), altitude));
            Assert.AreEqual(before + 1, frame.RebaseCount);
            Assert.IsFalse(frame.MaybeRebase(frame.Origin + PlanetTangentFrame.At(direction).North * 0.0005, 0.0), "below the minimum rebase distance");
            Assert.IsTrue(frame.MaybeRebase(frame.Origin + PlanetTangentFrame.At(direction).North * 0.002, 0.0));
            frame.AutoRebase = false;
            Assert.IsFalse(frame.MaybeRebase(Vector3d.Zero, 0.0), "auto rebase off");
            frame.AutoRebase = true;

            frame.Reset();
            Assert.AreEqual(Vector3.zero, root.transform.position);
            Assert.AreEqual(Vector3d.Zero, frame.Origin);
            Assert.IsTrue(SameBits(frame.PlanetToRender(surface), surface.ToVector3()), "identity mapping at zero origin");

            // A rotated, scaled root: the conversions follow the root's rotation and scale, snapping included.
            root.transform.rotation = Quaternion.Euler(23f, -140f, 8f);
            root.transform.localScale = new Vector3(2.5f, 2.5f, 2.5f);
            Vector3d q = new Vector3d(-40.0, 25.0, 88.0);
            Assert.That((frame.PlanetToRender(q) - root.transform.TransformPoint(q.ToVector3())).magnitude, Is.LessThan(1e-3), "rotated root, no rebase");
            Vector3d rendered = frame.PlanetVectorToRender(q);
            Assert.That((rendered.ToVector3() - root.transform.rotation * (q.ToVector3() * 2.5f)).magnitude, Is.LessThan(1e-3), "double quaternion rotation");
            Assert.That((frame.RenderVectorToPlanet(rendered) - q).Magnitude, Is.LessThan(1e-12), "inverse rotation and scale");
            frame.Rebase(camera);
            Assert.IsTrue(SameBits(root.transform.position, frame.PlanetToRender(Vector3d.Zero)), "snapped with rotation and scale");
            Assert.That(frame.PlanetToRender(camera).magnitude, Is.LessThan(2e-5), "camera within the float snap of the root");
            // The child goes through Unity's float hierarchy (two ~260 unit terms cancelling): ~1e-5 of error is its own.
            Assert.That((frame.PatchMatrix(surface.ToVector3()).GetPosition() - child.position).magnitude, Is.LessThan(1e-4), "patch and child with rotation and scale");
            Assert.That((frame.PatchMatrix(other.ToVector3()).GetPosition() - frame.PatchMatrix(surface.ToVector3()).GetPosition()).magnitude,
                Is.EqualTo(0.75).Within(1e-5), "scaled relative positions");

            // Disabling the frame undoes its move (leaving Play mode returns the authored scene).
            frame.enabled = false;
            Assert.AreEqual(Vector3.zero, root.transform.position);
            Assert.AreEqual(0, frame.Origin.Magnitude);
        }

        // ------------------------------------------------------------ G (Unity)
        [Test]
        public void G_Rebasing_NeverChangesCanonicalTerrainSamples()
        {
            var planet = new PlanetInstance(new PlanetDefinition { seed = 1337, resolution = 16 });
            var root = new GameObject("Frame Test Planet");
            created.Add(root);
            PlanetRenderFrame frame = PlanetRenderFrame.GetOrCreate(root.transform);
            var random = new System.Random(99);
            var directions = new Vector3[100];
            var before = new float[100];
            for (int i = 0; i < 100; i++)
            {
                directions[i] = RandomDirection(random).ToVector3().normalized;
                before[i] = planet.Height.GetElevation(directions[i]);
            }
            for (int r = 0; r < 4; r++)
            {
                frame.Rebase(PlanetCoordinates.AtAltitude(RandomDirection(random), planet.Scale, 0.01, planet.Scale.ToUnits(50.0 + r)));
                for (int i = 0; i < 100; i++)
                {
                    Assert.AreEqual(BitConverter.SingleToInt32Bits(before[i]), BitConverter.SingleToInt32Bits(planet.Height.GetElevation(directions[i])),
                        "the sampler never sees the render frame");
                }
            }
            Assert.AreEqual(4, frame.RebaseCount);
        }

        // ------------------------------------------------------------ H (Unity): orbit camera
        [Test]
        public void H_OrbitCamera_ClipPlanesFollowAltitude_AndPlacesItselfThroughTheFrame()
        {
            WorldTestHost host = CreateHost(1337);
            Camera camera = host.Camera.GetComponent<Camera>();
            PlanetScale scale = host.Current.Scale;
            float bounding = host.Visual.BoundingRadius;

            // Without a frame: the float path, the documented Euler offset, altitude-driven clip planes.
            host.Camera.SetView(33f, 15f, 400f);
            Vector3 expected = host.Root.transform.position + Quaternion.Euler(15f, 33f, 0f) * new Vector3(0f, 0f, -400f);
            Assert.That((camera.transform.position - expected).magnitude, Is.LessThan(1e-3), "float orbit placement");
            Assert.That(camera.nearClipPlane, Is.EqualTo(0.3f).Within(1e-6f), "orbit: the former near plane");
            Assert.That(camera.farClipPlane, Is.EqualTo(400f + 2f * bounding).Within(1e-3f));

            // With the frame: a 100 m altitude rebases the planet to the camera; near plane under 10 m.
            PlanetRenderFrame frame = PlanetRenderFrame.GetOrCreate(host.Root.transform);
            Assert.AreSame(frame, host.Camera.Frame);
            float altitude = (float)scale.ToUnits(100.0);
            host.Camera.SetView(33f, 15f, bounding + altitude);
            Assert.That(host.Camera.TargetDistance, Is.EqualTo(bounding + altitude).Within(1e-5f), "100 m above the bounding sphere is reachable");
            Assert.That(host.Camera.TargetAltitude, Is.EqualTo(altitude).Within(1e-5f));
            Assert.Greater(frame.RebaseCount, 0, "rebased to the camera");
            Assert.That(camera.transform.position.magnitude, Is.LessThan((float)(0.25 * altitude) + 1e-6f), "the camera stays at the world origin");
            Assert.That(camera.nearClipPlane, Is.LessThan((float)scale.ToUnits(10.0)), "near plane under 10 m");
            Assert.That(camera.nearClipPlane, Is.GreaterThanOrEqualTo(5e-5f));
            Assert.That(camera.farClipPlane, Is.EqualTo(bounding + altitude + 2f * bounding).Within(1e-3f));
            // The planet root moved so that the camera is ~R away from it, in the Euler direction, in double.
            double p = 15.0 * Math.PI / 180.0, y = 33.0 * Math.PI / 180.0;
            var offset = new Vector3d(-Math.Cos(p) * Math.Sin(y), Math.Sin(p), -Math.Cos(p) * Math.Cos(y)) * (bounding + altitude);
            Vector3d cameraPlanet = frame.RenderToPlanet(camera.transform.position);
            Assert.That((cameraPlanet - offset).Magnitude, Is.LessThan(1e-6), "double placement in planet space");
            Assert.That((frame.PlanetToRender(cameraPlanet) - camera.transform.position).magnitude, Is.LessThan(1e-9));
            Assert.That(scale.AltitudeMeters(cameraPlanet, (bounding - host.Current.Radius) / host.Current.Radius), Is.EqualTo(100.0).Within(1.0), "100 m above the bounding sphere (float distance: 0.5 m steps)");

            // Back to orbit: the clip planes widen again, the camera keeps working after many rebases.
            int rebases = frame.RebaseCount;
            host.Camera.SetView(33f, 15f, 400f);
            Assert.That(camera.nearClipPlane, Is.EqualTo(0.3f).Within(1e-6f));
            Assert.Greater(frame.RebaseCount, rebases, "a 400 unit jump rebases");
            Assert.That(camera.transform.position.magnitude, Is.LessThan(0.25f * (400f - bounding)));
            Assert.That((frame.RenderToPlanet(camera.transform.position).Magnitude), Is.EqualTo(400.0).Within(1e-4));
        }

        // ------------------------------------------------------------ X (Unity): every planet system agrees after a rebase
        [Test]
        public void X_AfterARebase_PatchesOceanRootAndHazeShellAgree()
        {
            WorldTestHost host = CreateHost(1337);
            PlanetVisualController visual = host.Visual;
            PlanetInstance planet = host.Current;
            PlanetScale scale = planet.Scale;
            PlanetRenderFrame frame = PlanetRenderFrame.GetOrCreate(host.Root.transform);

            AdaptiveTerrainRenderer terrain = AdaptiveTerrainRenderer.GetOrCreate(visual);
            terrain.Settings.maxLeafPatches = 96;
            terrain.BackgroundConstruction = false;
            terrain.ConstructionMilliseconds = 10000f;
            terrain.MaxUploadsPerFrame = 256;
            terrain.MaxPendingBuilds = 128;
            terrain.enabled = true;

            // Camera 100 m above the terrain under it.
            Vector3d direction = new Vector3d(0.3, 0.5, -0.81).Normalized;
            double elevation = planet.Height.GetElevation(direction.ToVector3());
            Vector3d cameraPlanet = PlanetCoordinates.AtAltitude(direction, scale, elevation, scale.ToUnits(100.0));
            var view = new TerrainLodView(cameraPlanet.ToVector3(), 60f, 1080);
            bool settled = false;
            for (int tick = 0; tick < 2000 && !settled; tick++)
            {
                terrain.Tick(view);
                settled = terrain.IsDisplaying && terrain.Covering.Converged && terrain.Scheduler.Pending == 0;
            }
            Assert.IsTrue(settled, "adaptive covering displayed");

            int applies = visual.OceanAtmosphereApplyCount;
            MethodInfo stale = typeof(PlanetVisualController).GetMethod("OceanAtmosphereStale", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(stale, "the staleness detector");
            visual.ApplyOceanAtmosphere();
            Assert.IsFalse((bool)stale.Invoke(visual, null), "fresh before the rebase");

            frame.Rebase(cameraPlanet);
            Transform root = host.Root.transform;
            Assert.That(root.position.magnitude, Is.GreaterThan(100f), "the planet moved away from the world origin");
            Assert.That(frame.PlanetToRender(cameraPlanet).magnitude, Is.LessThan(2e-5), "the camera is at the world origin (within the float snap of the root: 7.6e-6 units at magnitude 100)");
            // Patches: the renderer's matrix is the frame's, translation computed in double.
            float nearest = float.MaxValue;
            foreach (PlanetPatchId leaf in terrain.Displayed.Leaves)
            {
                Assert.IsTrue(terrain.Meshes.TryGet(leaf, out PatchMeshCache.Entry entry));
                Vector3 origin = entry.Geometry.Origin;
                Vector3 drawn = terrain.PatchMatrix(origin).GetPosition();
                Assert.That((drawn - frame.PlanetToRender(origin)).magnitude, Is.LessThan(1e-6), "patch " + leaf);
                nearest = Mathf.Min(nearest, drawn.magnitude);
            }
            Assert.That(nearest, Is.LessThan(0.5f * planet.Radius), "a patch origin near the camera in render space");
            // Ocean (child object) and the haze shell center (shader uniform) follow the root.
            Assert.That((visual.OceanRenderer.transform.position - root.position).magnitude, Is.LessThan(1e-5f), "ocean at the root");
            Assert.IsTrue((bool)stale.Invoke(visual, null), "the haze frame changed: stale");
            visual.ApplyOceanAtmosphere();
            Assert.Greater(visual.OceanAtmosphereApplyCount, applies, "re-applied after the rebase");
            Assert.IsFalse((bool)stale.Invoke(visual, null));
            var block = new MaterialPropertyBlock();
            visual.OceanRenderer.GetPropertyBlock(block);
            Vector4 shell = block.GetVector("_AtmoShell");
            Assert.That((new Vector3(shell.x, shell.y, shell.z) - root.position).magnitude, Is.LessThan(1e-5f), "_AtmoShell center at the root");
            Assert.That(shell.w, Is.GreaterThan(0f));
            // The legacy faces (children) and the atmosphere draw matrix (root) share the same translation.
            foreach (MeshRenderer face in visual.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (face.transform.parent == root)
                {
                    Assert.That((face.transform.position - root.position).magnitude, Is.LessThan(1e-5f), face.name + " at the root");
                }
            }
            Assert.That((root.localToWorldMatrix.GetPosition() - root.position).magnitude, Is.LessThan(1e-6f));
            terrain.enabled = false;
        }
    }
}
