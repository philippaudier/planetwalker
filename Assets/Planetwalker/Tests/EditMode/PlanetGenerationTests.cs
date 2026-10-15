using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Tests
{
    public class PlanetGenerationTests
    {
        const int SampleCount = 2000;

        static Vector3 FibonacciDirection(int i, int count)
        {
            float y = 1f - 2f * (i + 0.5f) / count;
            float r = Mathf.Sqrt(1f - y * y);
            float phi = i * 2.39996323f;
            return new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi));
        }

        static float MeanAbsoluteDifference(PlanetDefinition a, PlanetDefinition b)
        {
            var ga = new PlanetHeightGenerator(a);
            var gb = new PlanetHeightGenerator(b);
            double sum = 0;
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                sum += Mathf.Abs(ga.GetElevation(d) - gb.GetElevation(d));
            }
            return (float)(sum / SampleCount);
        }

        static float MaxAbsoluteDifference(PlanetDefinition a, PlanetDefinition b)
        {
            var ga = new PlanetHeightGenerator(a);
            var gb = new PlanetHeightGenerator(b);
            float max = 0f;
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                max = Mathf.Max(max, Mathf.Abs(ga.GetElevation(d) - gb.GetElevation(d)));
            }
            return max;
        }

        // TEST A
        [Test]
        public void SameSeedAndParameters_ProduceIdenticalElevations()
        {
            var definition = new PlanetDefinition { seed = 4242 };
            var first = new PlanetHeightGenerator(definition);
            var second = new PlanetHeightGenerator(definition.Clone());

            var forward = new float[SampleCount];
            for (int i = 0; i < SampleCount; i++)
            {
                forward[i] = first.GetElevation(FibonacciDirection(i, SampleCount));
            }
            // Reverse order on another instance: no dependency on sampling order or state.
            for (int i = SampleCount - 1; i >= 0; i--)
            {
                Assert.AreEqual(forward[i], second.GetElevation(FibonacciDirection(i, SampleCount)), 0f, "Sample " + i);
            }
        }

        // TEST B
        [Test]
        public void ChangingSeed_ChangesTerrain()
        {
            float difference = MeanAbsoluteDifference(new PlanetDefinition { seed = 1 }, new PlanetDefinition { seed = 2 });
            Assert.Greater(difference, 0.002f);
        }

        // TEST C
        [Test]
        public void ChangingContinentStrength_ChangesElevation()
        {
            var a = new PlanetDefinition { continentStrength = 0.01f };
            var b = new PlanetDefinition { continentStrength = 0.05f };
            Assert.Greater(MeanAbsoluteDifference(a, b), 0.001f);
        }

        // TEST D
        [Test]
        public void ChangingMountainStrength_ChangesElevation()
        {
            // Since V0.1.1B mountains are confined to ranges (a few percent of the surface),
            // so the sphere-wide mean is small; check the relief where ranges exist instead.
            var a = new PlanetDefinition { mountainStrength = 0f };
            var b = new PlanetDefinition { mountainStrength = 0.08f };
            Assert.Greater(MaxAbsoluteDifference(a, b), 0.01f);
            Assert.Greater(MeanAbsoluteDifference(a, b), 0f);
        }

        [Test]
        public void GeneratorIsSnapshot_LaterDefinitionEditsDoNotAffectIt()
        {
            var definition = new PlanetDefinition();
            var generator = new PlanetHeightGenerator(definition);
            Vector3 d = FibonacciDirection(7, 100);
            float before = generator.GetElevation(d);
            definition.seed += 1;
            definition.mountainStrength = 0f;
            Assert.AreEqual(before, generator.GetElevation(d), 0f);
        }

        [Test]
        public void Elevation_IsFiniteAndWithinPlausibleBounds()
        {
            var definition = new PlanetDefinition();
            var generator = new PlanetHeightGenerator(definition);
            float bound = definition.continentStrength * 1.5f + definition.mountainStrength + definition.detailStrength;
            for (int i = 0; i < SampleCount; i++)
            {
                float e = generator.GetElevation(FibonacciDirection(i, SampleCount));
                Assert.IsFalse(float.IsNaN(e) || float.IsInfinity(e));
                Assert.LessOrEqual(Mathf.Abs(e), bound);
            }
        }

        [Test]
        public void DefaultPlanet_HasBothLandAndOcean()
        {
            var planet = new PlanetInstance(new PlanetDefinition());
            Assert.Greater(planet.LandFraction, 0.1f);
            Assert.Less(planet.LandFraction, 0.7f);
            Assert.Less(planet.MinElevation, planet.SeaLevel);
            Assert.Greater(planet.MaxElevation, planet.SeaLevel);
        }

        [Test]
        public void Noise_IsContinuousAcrossNegativeCoordinates()
        {
            const float epsilon = 1e-4f;
            for (int i = -3; i <= 3; i++)
            {
                float a = PlanetNoise.Gradient(i - epsilon, 0.37f, -0.81f, 99);
                float b = PlanetNoise.Gradient(i + epsilon, 0.37f, -0.81f, 99);
                Assert.AreEqual(a, b, 0.01f, "Discontinuity at x = " + i);
            }
        }

        [Test]
        public void Noise_StaysInExpectedRange()
        {
            for (int i = 0; i < 5000; i++)
            {
                float x = (i * 0.731f) % 40f - 20f;
                float y = (i * 1.379f) % 40f - 20f;
                float z = (i * 2.113f) % 40f - 20f;
                float n = PlanetNoise.Gradient(x, y, z, 7);
                Assert.LessOrEqual(Mathf.Abs(n), 1.1f);
                float r = PlanetNoise.Ridged(new Vector3(x, y, z), 7, 5);
                Assert.GreaterOrEqual(r, 0f);
                Assert.LessOrEqual(r, 1f);
            }
        }

        [Test]
        public void SurfaceNormal_IsUnitAndOutward()
        {
            var generator = new PlanetHeightGenerator(new PlanetDefinition());
            for (int i = 0; i < 500; i++)
            {
                Vector3 d = FibonacciDirection(i, 500);
                Vector3 n = generator.GetSurfaceNormal(d, 0.01f);
                Assert.AreEqual(1f, n.magnitude, 1e-4f);
                Assert.Greater(Vector3.Dot(n, d), 0.3f);
            }
        }

        [Test]
        public void Sanitize_ClampsOutOfRangeValues()
        {
            var definition = new PlanetDefinition { resolution = 100000, radius = -5f, seaLevel = 3f };
            definition.Sanitize();
            Assert.AreEqual(PlanetDefinition.MaxResolution, definition.resolution);
            Assert.AreEqual(PlanetDefinition.MinRadius, definition.radius);
            Assert.AreEqual(PlanetDefinition.MaxSeaLevel, definition.seaLevel);
        }
    }
}
