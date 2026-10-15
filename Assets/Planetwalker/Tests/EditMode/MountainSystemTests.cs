using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>V0.1.1B mountain systems: provinces, ranges, envelope and ridges.</summary>
    public class MountainSystemTests
    {
        const int SampleCount = 3000;

        static Vector3 FibonacciDirection(int i, int count)
        {
            float y = 1f - 2f * (i + 0.5f) / count;
            float r = Mathf.Sqrt(1f - y * y);
            float phi = i * 2.39996323f;
            return new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi));
        }

        /// <summary>Fraction of land samples with mountain influence above 0.5.</summary>
        static float MountainousLandFraction(PlanetHeightGenerator generator, float seaLevel)
        {
            int land = 0, mountainous = 0;
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                if (generator.GetElevation(d) > seaLevel)
                {
                    land++;
                    if (generator.GetMountainInfluence(d) > 0.5f)
                    {
                        mountainous++;
                    }
                }
            }
            return land > 0 ? mountainous / (float)land : 0f;
        }

        // TEST A
        [Test]
        public void SameSeed_ProducesIdenticalMountainProvinces()
        {
            var definition = new PlanetDefinition { seed = 609538, mountainRangeCoverage = 0.8f };
            MountainSystem a = new PlanetHeightGenerator(definition).Mountains;
            MountainSystem b = new PlanetHeightGenerator(definition.Clone()).Mountains;
            Assert.Greater(a.RangeCount, 0);
            Assert.AreEqual(a.ProvinceCount, b.ProvinceCount);
            Assert.AreEqual(a.RangeCount, b.RangeCount);
            for (int i = 0; i < a.ProvinceCount; i++)
            {
                Assert.AreEqual(a.GetProvinceCenter(i), b.GetProvinceCenter(i));
            }
            for (int i = 0; i < a.RangeCount; i++)
            {
                Assert.AreEqual(a.GetRangeCenter(i), b.GetRangeCenter(i));
                Assert.AreEqual(a.GetRangeNormal(i), b.GetRangeNormal(i));
            }
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                Assert.AreEqual(a.GetInfluence(d), b.GetInfluence(d), 0f);
            }
        }

        // TEST B
        [Test]
        public void DifferentSeeds_ProduceDifferentMountainDistributions()
        {
            int[] seeds = { 868497, 280695, 554492, 335559 };
            for (int s = 1; s < seeds.Length; s++)
            {
                var a = new PlanetHeightGenerator(new PlanetDefinition { seed = seeds[s - 1] });
                var b = new PlanetHeightGenerator(new PlanetDefinition { seed = seeds[s] });
                double difference = 0;
                for (int i = 0; i < SampleCount; i++)
                {
                    Vector3 d = FibonacciDirection(i, SampleCount);
                    difference += Mathf.Abs(a.Mountains.GetInfluence(d) - b.Mountains.GetInfluence(d));
                }
                Assert.Greater(difference / SampleCount, 0.005, $"Seeds {seeds[s - 1]} and {seeds[s]}");
            }
        }

        // TEST C
        [Test]
        public void ZeroMountainStrength_RemovesMountainElevation()
        {
            // With strength 0, no range parameter may change the terrain: it equals a planet
            // that has no ranges at all.
            var noStrength = new PlanetHeightGenerator(new PlanetDefinition { mountainStrength = 0f, mountainRangeCoverage = 1f, mountainRuggedness = 1f });
            var noRanges = new PlanetHeightGenerator(new PlanetDefinition { mountainRangeCoverage = 0f });
            var withMountains = new PlanetHeightGenerator(new PlanetDefinition { mountainRangeCoverage = 1f });
            Assert.AreEqual(0, noRanges.Mountains.RangeCount);
            int raised = 0;
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                Assert.AreEqual(noRanges.GetElevation(d), noStrength.GetElevation(d), 0f);
                if (withMountains.GetElevation(d) > noStrength.GetElevation(d) + 1e-4f)
                {
                    raised++;
                }
            }
            Assert.Greater(raised, 0, "Sanity: non-zero strength must raise some terrain");
        }

        // TEST D
        [Test]
        public void RangeCoverage_ChangesMountainDistribution()
        {
            float low = 0f, high = 0f;
            const int seeds = 12;
            for (int s = 0; s < seeds; s++)
            {
                low += MountainousLandFraction(new PlanetHeightGenerator(new PlanetDefinition { seed = 1000 + s, mountainRangeCoverage = 0.2f }), 0f);
                high += MountainousLandFraction(new PlanetHeightGenerator(new PlanetDefinition { seed = 1000 + s, mountainRangeCoverage = 0.9f }), 0f);
            }
            Assert.Greater(high / seeds, low / seeds * 1.5f + 0.01f);

            var none = new PlanetHeightGenerator(new PlanetDefinition { mountainRangeCoverage = 0f });
            Assert.AreEqual(0, none.Mountains.RangeCount);
            Assert.AreEqual(0, none.Mountains.ProvinceCount);
        }

        // TEST E
        [Test]
        public void Ruggedness_ChangesLocalRelief_NotGeography()
        {
            var smooth = new PlanetHeightGenerator(new PlanetDefinition { seed = 280695, mountainRuggedness = 0f });
            var rugged = new PlanetHeightGenerator(new PlanetDefinition { seed = 280695, mountainRuggedness = 1f });
            double reliefDifference = 0;
            int mountainSamples = 0;
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                // Ruggedness never moves continents or ranges...
                Assert.AreEqual(smooth.GetContinentalField(d), rugged.GetContinentalField(d), 0f);
                Assert.AreEqual(smooth.GetMountainInfluence(d), rugged.GetMountainInfluence(d), 0f);
                // ...but changes the relief where mountains exist.
                if (smooth.GetMountainInfluence(d) > 0.5f)
                {
                    mountainSamples++;
                    reliefDifference += Mathf.Abs(smooth.GetElevation(d) - rugged.GetElevation(d));
                }
            }
            Assert.Greater(mountainSamples, 10);
            Assert.Greater(reliefDifference / mountainSamples, 0.001);
        }

        // TEST F
        [Test]
        public void MountainInfluence_IsContinuousAcrossCubeFaceBoundaries()
        {
            var generator = new PlanetHeightGenerator(new PlanetDefinition { seed = 609538, mountainRangeCoverage = 1f });
            const int resolution = 128;
            const float step = 1e-4f;
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                CubeSphereGenerator.GetFaceAxes(face, out _, out Vector3 axisA, out _);
                for (int i = 0; i <= resolution; i++)
                {
                    Vector3 onEdge = CubeSphereGenerator.GetSphereDirection(face, resolution, i, resolution);
                    Vector3 inside = (onEdge - axisA * step).normalized;
                    Vector3 outside = (onEdge + axisA * step).normalized;
                    float center = generator.GetMountainInfluence(onEdge);
                    Assert.AreEqual(center, generator.GetMountainInfluence(inside), 0.01f);
                    Assert.AreEqual(center, generator.GetMountainInfluence(outside), 0.01f);
                }
            }
            // Shared border vertices of +Y and +X sample identical mountain elevation.
            for (int i = 0; i <= resolution; i++)
            {
                Vector3 fromTop = CubeSphereGenerator.GetSphereDirection(0, resolution, i, resolution);
                Vector3 fromRight = CubeSphereGenerator.GetSphereDirection(3, resolution - i, 0, resolution);
                Assert.AreEqual(generator.GetElevation(fromTop), generator.GetElevation(fromRight), 0f);
            }
        }

        // TEST G
        [Test]
        public void Elevations_RemainFinite_AtMountainParameterExtremes()
        {
            var extremes = new[]
            {
                new PlanetDefinition
                {
                    mountainStrength = PlanetDefinition.MaxMountainStrength,
                    mountainRangeCoverage = 1f,
                    mountainRangeLength = PlanetDefinition.MaxMountainRangeLength,
                    mountainRuggedness = 1f,
                    mountainFrequency = PlanetDefinition.MaxMountainFrequency,
                },
                new PlanetDefinition
                {
                    seed = int.MinValue,
                    mountainRangeCoverage = 1f,
                    mountainRangeLength = PlanetDefinition.MinMountainRangeLength,
                    mountainRuggedness = 0f,
                    mountainFrequency = PlanetDefinition.MinMountainFrequency,
                },
                new PlanetDefinition { seed = int.MaxValue, mountainRangeCoverage = 1f, mountainRangeLength = 2f },
            };
            foreach (PlanetDefinition definition in extremes)
            {
                var generator = new PlanetHeightGenerator(definition);
                float bound = definition.continentStrength * 1.5f + definition.mountainStrength + definition.detailStrength * 3f;
                for (int i = 0; i < SampleCount; i++)
                {
                    Vector3 d = FibonacciDirection(i, SampleCount);
                    float e = generator.GetElevation(d);
                    float influence = generator.GetMountainInfluence(d);
                    Assert.IsFalse(float.IsNaN(e) || float.IsInfinity(e));
                    Assert.LessOrEqual(Mathf.Abs(e), bound);
                    Assert.IsTrue(influence >= 0f && influence <= 1f);
                }
            }
        }

        // TEST H
        [Test]
        public void MountainInfluence_FadesSmoothly()
        {
            // Lipschitz check of the envelope (including range endpoints and overlaps).
            // The steepest legitimate transition is a range core: influence falls from 1 to 0
            // over the narrowest half-width (0.055 * (1 - 0.35) ~ 0.036 rad) with a smoothstep
            // whose peak slope is 1.5 / width, i.e. about 42 per radian. 60 per radian leaves
            // margin for meanders; a hard edge or seam would exceed it by orders of magnitude.
            const float step = 0.001f;
            const float maxSlope = 60f;
            int[] seeds = { 609538, 868497, 280695 };
            foreach (int seed in seeds)
            {
                MountainSystem mountains = new PlanetHeightGenerator(new PlanetDefinition { seed = seed, mountainRangeCoverage = 1f }).Mountains;
                for (int i = 0; i < SampleCount; i++)
                {
                    Vector3 d = FibonacciDirection(i, SampleCount);
                    Vector3 tangent = Vector3.Cross(d, Mathf.Abs(d.y) < 0.99f ? Vector3.up : Vector3.right).normalized;
                    Vector3 next = (d + tangent * step).normalized;
                    float change = Mathf.Abs(mountains.GetInfluence(next) - mountains.GetInfluence(d));
                    Assert.LessOrEqual(change, maxSlope * step, $"Seed {seed}, sample {i}");
                }

                // Walking across each range: from its core to well outside its foothills.
                for (int r = 0; r < mountains.RangeCount; r++)
                {
                    Vector3 center = mountains.GetRangeCenter(r);
                    Vector3 across = mountains.GetRangeNormal(r);
                    float previous = mountains.GetInfluence(center);
                    for (float angle = step; angle < 0.6f; angle += step)
                    {
                        Vector3 p = center * Mathf.Cos(angle) + across * Mathf.Sin(angle);
                        float value = mountains.GetInfluence(p);
                        Assert.LessOrEqual(Mathf.Abs(value - previous), maxSlope * step);
                        previous = value;
                    }
                }
            }
        }

        // TEST I
        [Test]
        public void MountainGeneration_DoesNotModifyContinentalField()
        {
            // Golden values recorded with the V0.1.1A generator (default parameters). The
            // tolerance only absorbs float evaluation differences between runtimes.
            var golden = new (int seed, float[] values)[]
            {
                (1337, new[] { -1.1687149f, -0.8449931f, 0.20118722f, -0.0884976f, -0.6762469f }),
                (609538, new[] { -0.31708363f, 0.3747716f, 0.14683577f, -0.6189029f, 0.29999474f }),
                (-42, new[] { 0.10132222f, 0.15112476f, -0.3486376f, -0.4714679f, 0.3184904f }),
            };
            var directions = new[]
            {
                new Vector3(1f, 0f, 0f), new Vector3(0.3f, 0.8f, -0.52f), new Vector3(-0.6f, -0.2f, 0.77f),
                new Vector3(0.1f, -0.95f, 0.29f), new Vector3(-0.7f, 0.7f, 0.14f),
            };
            foreach ((int seed, float[] values) in golden)
            {
                var generator = new PlanetHeightGenerator(new PlanetDefinition { seed = seed });
                for (int i = 0; i < directions.Length; i++)
                {
                    Assert.AreEqual(values[i], generator.GetContinentalField(directions[i].normalized), 1e-5f, $"Seed {seed}, direction {i}");
                }
            }

            // No mountain parameter influences the continental field.
            var reference = new PlanetHeightGenerator(new PlanetDefinition());
            var mountainous = new PlanetHeightGenerator(new PlanetDefinition
            {
                mountainStrength = 0.1f, mountainRangeCoverage = 1f, mountainRangeLength = 2f,
                mountainRuggedness = 1f, mountainFrequency = 15f,
            });
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                Assert.AreEqual(reference.GetContinentalField(d), mountainous.GetContinentalField(d), 0f);
            }
        }

        // TEST K
        [Test]
        public void MountainCoverage_StaysWithinReasonableBounds()
        {
            // Rationale: at default settings mountains should be a minority landform
            // (Earth: roughly 10-25% of land, depending on the definition), never cover most
            // of the land, and vary between planets so that both plains-dominated and
            // mountainous worlds occur. Bounds are deliberately loose.
            const int seeds = 24;
            float sum = 0f, min = 1f, max = 0f;
            for (int s = 0; s < seeds; s++)
            {
                var definition = new PlanetDefinition { seed = 7919 * s + 13 };
                float fraction = MountainousLandFraction(new PlanetHeightGenerator(definition), definition.seaLevel);
                Assert.LessOrEqual(fraction, 0.4f, "Seed " + definition.seed);
                sum += fraction;
                min = Mathf.Min(min, fraction);
                max = Mathf.Max(max, fraction);
            }
            float mean = sum / seeds;
            Assert.GreaterOrEqual(mean, 0.02f);
            Assert.LessOrEqual(mean, 0.2f);
            Assert.Greater(max - min, 0.05f, "Coverage should vary between planets");
        }

        // TEST L
        [Test]
        public void DefaultMountains_HaveNoSingleVertexSpikes()
        {
            // A spike is a vertex standing above (or below) ALL EIGHT grid neighbours with a
            // slope steeper than 35 degrees on every side. Ridges never qualify (their
            // along-ridge neighbours sit at a similar height, including diagonal ridges) and
            // ordinary peaks at this resolution measure ~20 degrees at most under default
            // settings; a needle produced by aliased noise would exceed the bound.
            const int resolution = 128;
            float spacing = Mathf.PI * 0.5f / resolution;
            float threshold = Mathf.Tan(35f * Mathf.Deg2Rad) * spacing;
            int[] seeds = { 609538, 280695, 335559 };
            foreach (int seed in seeds)
            {
                var generator = new PlanetHeightGenerator(new PlanetDefinition { seed = seed });
                int edge = resolution + 1;
                var elevations = new float[edge * edge];
                for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
                {
                    for (int y = 0; y < edge; y++)
                    {
                        for (int x = 0; x < edge; x++)
                        {
                            elevations[x + y * edge] = generator.GetElevation(CubeSphereGenerator.GetSphereDirection(face, x, y, resolution));
                        }
                    }
                    for (int y = 1; y < resolution; y++)
                    {
                        for (int x = 1; x < resolution; x++)
                        {
                            float e = elevations[x + y * edge];
                            float above = float.MaxValue, below = float.MaxValue;
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    if (dx == 0 && dy == 0)
                                    {
                                        continue;
                                    }
                                    float neighbour = elevations[x + dx + (y + dy) * edge];
                                    above = Mathf.Min(above, e - neighbour);
                                    below = Mathf.Min(below, neighbour - e);
                                }
                            }
                            Assert.Less(above, threshold, $"Spike at seed {seed}, face {face}, ({x}, {y})");
                            Assert.Less(below, threshold, $"Pit at seed {seed}, face {face}, ({x}, {y})");
                        }
                    }
                }
            }
        }
    }
}
