using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Terrain;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>
    /// LOD error model with per-patch relief bounds (V0.2.0C fix). Reproduces the reported bug: on an Earth-scale
    /// planet with exaggerated mountains, zooming towards a low plain (camera below the planet's highest possible
    /// summit, ~370 km above the plain) saturated the error of every patch within a few hundred kilometers, the
    /// budget spread evenly over that disk, and the finest levels under the camera disappeared.
    /// </summary>
    public class TerrainLodReliefBoundsTests
    {
        const float EarthKm = 6371f;

        static PlanetInstance planet;
        static TerrainElevationSampler sampler;
        static Vector3 plain;
        static float ground;

        [OneTimeSetUp]
        public void Setup()
        {
            planet = new PlanetInstance(new PlanetDefinition { seed = 456387 });
            sampler = new TerrainElevationSampler(planet);
            // Lowest land point of a deterministic Fibonacci scan (a low plain, far below the highest summits).
            float best = float.MaxValue;
            for (int i = 0; i < 4000; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / 4000f, r = Mathf.Sqrt(1f - y * y), phi = i * 2.39996323f;
                var d = new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi));
                float e = planet.Height.GetElevation(d);
                if (e > planet.SeaLevel + 0.001f && e < best)
                {
                    best = e;
                    plain = d;
                }
            }
            ground = sampler.Radius * (1f + best);
        }

        static TerrainLodView View(float altitudeKm, out Vector3 side)
        {
            side = Vector3.Cross(plain, Vector3.right).normalized;
            Vector3 position = plain * (ground + altitudeKm / EarthKm * sampler.Radius);
            var view = new TerrainLodView(position, 60f, 1080);
            var go = new GameObject("LOD Test Camera");
            try
            {
                Camera camera = go.AddComponent<Camera>();
                camera.fieldOfView = 60f;
                camera.aspect = 16f / 9f;
                camera.nearClipPlane = 0.0005f;
                camera.farClipPlane = 1000f;
                camera.transform.position = position;
                // Oblique, like the reported screenshots.
                camera.transform.rotation = Quaternion.LookRotation((-plain + side * 0.8f).normalized, plain);
                view.Frustum = GeometryUtility.CalculateFrustumPlanes(camera);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            return view;
        }

        static int DeepestUnderCamera(TerrainLodSelection selection)
        {
            int deepest = 0;
            foreach (PlanetPatchId id in selection.Leaves)
            {
                if (Vector3.Angle(CubeSphereCoordinates.CenterDirection(id), plain) < 1f)
                {
                    deepest = Mathf.Max(deepest, id.Level);
                }
            }
            return deepest;
        }

        [Test]
        public void ErrorsDecreaseWithDistance_BelowTheHighestSummit()
        {
            Assert.Greater(sampler.MaxRadius - ground, 300f / EarthKm * sampler.Radius, "Setup: the camera flies far below the highest possible summit");
            var selector = new TerrainLodSelector(new AdaptiveTerrainSettings());
            selector.Select(sampler, View(10f, out Vector3 side));
            float previous = float.MaxValue;
            var log = new System.Text.StringBuilder();
            foreach (float km in new[] { 20f, 80f, 200f, 400f })
            {
                PlanetPatchId id = CubeSphereCoordinates.PatchAt((plain + side * (km / EarthKm)).normalized, 10);
                float error = selector.ScreenError(id);
                log.Append($" {km} km: {error:0} px;");
                Assert.Less(error, previous, $"Level-10 patch {km} km away: smaller error than closer ones (no saturation)");
                Assert.Less(error, 1e5f, "Not saturated: the camera is outside its bounds");
                previous = error;
            }
            TestContext.WriteLine("level-10 screen errors at 10 km altitude:" + log);
        }

        [Test]
        public void ZoomingIn_NeverLosesDetailUnderTheCamera()
        {
            var selector = new TerrainLodSelector(new AdaptiveTerrainSettings());
            int previous = 0;
            var log = new System.Text.StringBuilder();
            foreach (float altitudeKm in new[] { 600f, 360f, 150f, 50f, 10f, 2f })
            {
                selector.Reset();
                TerrainLodSelection selection = selector.Select(sampler, View(altitudeKm, out _));
                int deepest = DeepestUnderCamera(selection);
                log.Append($" {altitudeKm} km: level {deepest} ({selection.Count}/{selection.Budget});");
                Assert.GreaterOrEqual(deepest, previous, $"{altitudeKm} km: closer means at least as fine under the camera");
                previous = deepest;
            }
            TestContext.WriteLine("deepest level under the camera:" + log);
            Assert.GreaterOrEqual(previous, 14, "At 2 km the ground under the camera reaches fine levels (before the fix: 8)");
        }

        [Test]
        public void ReliefBounds_ContainTheTerrain_AndAreLocal()
        {
            var selector = new TerrainLodSelector(new AdaptiveTerrainSettings());
            selector.Select(sampler, View(50f, out _));
            int misses = 0, checks = 0;
            float widest = 0f;
            foreach (int level in new[] { 4, 7, 10, 13 })
            {
                PlanetPatchId id = CubeSphereCoordinates.PatchAt(plain, level);
                Vector2 bounds = selector.ReliefBounds(id);
                widest = level >= 10 ? Mathf.Max(widest, bounds.y - bounds.x) : widest;
                CubeSphereCoordinates.FaceBounds(id, out float aMin, out float bMin, out float aMax, out float bMax);
                for (int j = 0; j <= 16; j++)
                {
                    for (int i = 0; i <= 16; i++)
                    {
                        Vector3 d = CubeSphereCoordinates.FaceToDirection(id.Face, Mathf.Lerp(aMin, aMax, i / 16f), Mathf.Lerp(bMin, bMax, j / 16f));
                        float r = sampler.Radius * (1f + planet.Height.GetElevation(d));
                        checks++;
                        misses += r < bounds.x - 1e-4f || r > bounds.y + 1e-4f ? 1 : 0;
                    }
                }
            }
            TestContext.WriteLine($"relief bounds: {misses} of {checks} fine samples outside; widest range at level >= 10: {widest / sampler.Radius * EarthKm:0.0} km");
            Assert.AreEqual(0, misses, "Bounds contain the full-resolution terrain");
            Assert.Less(widest, 0.1f * (sampler.MaxRadius - sampler.MinRadius), "Local bounds, far tighter than the planet's range");
        }

        [Test]
        public void BoundsAreCachedPerPlanet_AndSelectionStaysCheap()
        {
            var selector = new TerrainLodSelector(new AdaptiveTerrainSettings());
            TerrainLodView view = View(10f, out _);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            selector.Select(sampler, view);
            double cold = watch.Elapsed.TotalMilliseconds;
            int computed = selector.BoundComputeCount;
            watch.Restart();
            selector.Select(sampler, view);
            double warm = watch.Elapsed.TotalMilliseconds;
            TestContext.WriteLine($"selection at 10 km: cold {cold:0.0} ms ({computed} patch bounds sampled), warm {warm:0.00} ms");
            Assert.AreEqual(computed, selector.BoundComputeCount, "Same planet, same view: no bound recomputed");
            var other = new TerrainElevationSampler(new PlanetInstance(new PlanetDefinition { seed = 456388 }));
            selector.Select(other, view);
            Assert.Greater(selector.BoundComputeCount, computed, "Another planet: bounds recomputed (never reused across planets)");
        }
    }
}
