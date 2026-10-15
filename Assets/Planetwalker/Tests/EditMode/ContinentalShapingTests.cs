using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>V0.1.1A continental shaping: domain warp, multiscale field, coastal detail.</summary>
    public class ContinentalShapingTests
    {
        const int SampleCount = 3000;

        static Vector3 FibonacciDirection(int i, int count)
        {
            float y = 1f - 2f * (i + 0.5f) / count;
            float r = Mathf.Sqrt(1f - y * y);
            float phi = i * 2.39996323f;
            return new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi));
        }

        /// <summary>Fraction of sample directions whose land/ocean classification differs.</summary>
        static float LandClassificationDifference(PlanetDefinition a, PlanetDefinition b)
        {
            var ga = new PlanetHeightGenerator(a);
            var gb = new PlanetHeightGenerator(b);
            int different = 0;
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                if (ga.GetContinentalField(d) > 0f != gb.GetContinentalField(d) > 0f)
                {
                    different++;
                }
            }
            return different / (float)SampleCount;
        }

        static float MeanFieldDifference(PlanetDefinition a, PlanetDefinition b)
        {
            var ga = new PlanetHeightGenerator(a);
            var gb = new PlanetHeightGenerator(b);
            double sum = 0;
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                sum += Mathf.Abs(ga.GetContinentalField(d) - gb.GetContinentalField(d));
            }
            return (float)(sum / SampleCount);
        }

        // TEST A
        [Test]
        public void SameSeedAndParameters_ProduceIdenticalElevations()
        {
            var definition = new PlanetDefinition
            {
                seed = 271828,
                continentalWarpStrength = 0.4f,
                continentalWarpFrequency = 2.5f,
                coastalComplexity = 1f,
            };
            var first = new PlanetHeightGenerator(definition);
            var second = new PlanetHeightGenerator(definition.Clone());
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                Assert.AreEqual(first.GetElevation(d), second.GetElevation(d), 0f);
                Assert.AreEqual(first.GetContinentalField(d), second.GetContinentalField(d), 0f);
            }
        }

        // TEST B
        [Test]
        public void DifferentSeeds_ProduceDifferentContinentalShapes()
        {
            float difference = LandClassificationDifference(new PlanetDefinition { seed = 10 }, new PlanetDefinition { seed = 11 });
            Assert.Greater(difference, 0.15f, "Land/ocean layout should differ substantially between seeds");
        }

        // TEST C
        [Test]
        public void ChangingWarpStrength_ModifiesContinentalGeometry()
        {
            var a = new PlanetDefinition { continentalWarpStrength = 0.1f };
            var b = new PlanetDefinition { continentalWarpStrength = 0.3f };
            Assert.Greater(LandClassificationDifference(a, b), 0.02f);
            Assert.Greater(MeanFieldDifference(a, b), 0.01f);
        }

        // TEST D
        [Test]
        public void ZeroWarpStrength_DisablesDomainWarping()
        {
            // With no warp, the warp frequency must have no effect at all.
            var a = new PlanetDefinition { continentalWarpStrength = 0f, continentalWarpFrequency = 0.5f };
            var b = new PlanetDefinition { continentalWarpStrength = 0f, continentalWarpFrequency = 4f };
            var ga = new PlanetHeightGenerator(a);
            var gb = new PlanetHeightGenerator(b);
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                Assert.AreEqual(ga.GetElevation(d), gb.GetElevation(d), 0f);
            }

            // Sanity check: with a warp, the frequency matters.
            a.continentalWarpStrength = b.continentalWarpStrength = 0.2f;
            Assert.Greater(MeanFieldDifference(a, b), 0.01f);
        }

        // TEST E
        [Test]
        public void CoastalComplexity_ModifiesCoastsOnly()
        {
            var smooth = new PlanetDefinition { coastalComplexity = 0f };
            var complex = new PlanetDefinition { coastalComplexity = 1f };
            var gs = new PlanetHeightGenerator(smooth);
            var gc = new PlanetHeightGenerator(complex);

            int nearCoast = 0, changedNearCoast = 0, farFromCoast = 0;
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                float baseField = gs.GetContinentalField(d);
                if (Mathf.Abs(baseField) >= PlanetHeightGenerator.CoastalInfluenceBand)
                {
                    // Interiors and abyssal plains are untouched (no fragmentation).
                    farFromCoast++;
                    Assert.AreEqual(gs.GetElevation(d), gc.GetElevation(d), 0f);
                }
                else if (Mathf.Abs(baseField) < PlanetHeightGenerator.CoastalInfluenceBand * 0.5f)
                {
                    nearCoast++;
                    if (Mathf.Abs(gs.GetContinentalField(d) - gc.GetContinentalField(d)) > 1e-4f)
                    {
                        changedNearCoast++;
                    }
                }
            }
            Assert.Greater(farFromCoast, SampleCount / 4);
            Assert.Greater(nearCoast, 50);
            Assert.Greater(changedNearCoast, nearCoast * 0.9f);
            Assert.Greater(LandClassificationDifference(smooth, complex), 0.005f, "Coastline should move");
        }

        [Test]
        public void HigherCoastalComplexity_DoesNotExplodeIntoIslands()
        {
            // Coastline changes must stay bounded: the land share moves only slightly.
            var smooth = new PlanetInstance(new PlanetDefinition { seed = 99, coastalComplexity = 0f });
            var complex = new PlanetInstance(new PlanetDefinition { seed = 99, coastalComplexity = 1f });
            Assert.AreEqual(smooth.LandFraction, complex.LandFraction, 0.06f);
        }

        // TEST F
        [Test]
        public void ContinentalField_IsContinuousAcrossCubeFaceBoundaries()
        {
            var generator = new PlanetHeightGenerator(new PlanetDefinition
            {
                continentalWarpStrength = PlanetDefinition.MaxContinentalWarpStrength,
                coastalComplexity = 1f,
            });
            const int resolution = 64;
            const float step = 1e-4f;
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                for (int i = 0; i <= resolution; i++)
                {
                    // Points on the face border, nudged a tiny step across it in both directions.
                    Vector3 onEdge = CubeSphereGenerator.GetSphereDirection(face, resolution, i, resolution);
                    CubeSphereGenerator.GetFaceAxes(face, out _, out Vector3 axisA, out _);
                    Vector3 inside = (onEdge - axisA * step).normalized;
                    Vector3 outside = (onEdge + axisA * step).normalized;
                    float center = generator.GetContinentalField(onEdge);
                    Assert.AreEqual(center, generator.GetContinentalField(inside), 0.02f);
                    Assert.AreEqual(center, generator.GetContinentalField(outside), 0.02f);
                }
            }

            // Shared border vertices sample the identical value on both faces.
            for (int i = 0; i <= resolution; i++)
            {
                Vector3 fromTop = CubeSphereGenerator.GetSphereDirection(0, resolution, i, resolution);
                Vector3 fromRight = CubeSphereGenerator.GetSphereDirection(3, resolution - i, 0, resolution);
                Assert.AreEqual(fromTop, fromRight);
                Assert.AreEqual(generator.GetContinentalField(fromTop), generator.GetContinentalField(fromRight), 0f);
            }
        }

        // TEST G
        [Test]
        public void Elevations_RemainFinite_AtParameterExtremes()
        {
            var extremes = new[]
            {
                new PlanetDefinition { continentalWarpStrength = 0f, coastalComplexity = 0f },
                new PlanetDefinition
                {
                    continentalWarpStrength = PlanetDefinition.MaxContinentalWarpStrength,
                    continentalWarpFrequency = PlanetDefinition.MaxContinentalWarpFrequency,
                    coastalComplexity = 1f,
                    continentFrequency = PlanetDefinition.MaxContinentFrequency,
                },
                new PlanetDefinition
                {
                    seed = int.MinValue,
                    continentalWarpStrength = PlanetDefinition.MaxContinentalWarpStrength,
                    continentalWarpFrequency = PlanetDefinition.MinContinentalWarpFrequency,
                    continentFrequency = PlanetDefinition.MinContinentFrequency,
                },
                new PlanetDefinition { seed = int.MaxValue, coastalComplexity = 1f },
            };
            foreach (PlanetDefinition definition in extremes)
            {
                var generator = new PlanetHeightGenerator(definition);
                for (int i = 0; i < SampleCount; i++)
                {
                    Vector3 d = FibonacciDirection(i, SampleCount);
                    float e = generator.GetElevation(d);
                    float m = generator.GetContinentalMask(d);
                    Assert.IsFalse(float.IsNaN(e) || float.IsInfinity(e));
                    Assert.IsTrue(m >= 0f && m <= 1f);
                    Vector3 n = generator.GetSurfaceNormal(d, e, 0.01f);
                    Assert.IsFalse(float.IsNaN(n.x) || float.IsNaN(n.y) || float.IsNaN(n.z));
                }
            }
        }

        // TEST H
        [Test]
        public void SeaLevel_StillControlsLandCoverage()
        {
            float low = new PlanetInstance(new PlanetDefinition { seaLevel = -0.01f }).LandFraction;
            float mid = new PlanetInstance(new PlanetDefinition { seaLevel = 0f }).LandFraction;
            float high = new PlanetInstance(new PlanetDefinition { seaLevel = 0.01f }).LandFraction;
            Assert.Greater(low, mid + 0.02f);
            Assert.Greater(mid, high + 0.02f);
        }

        // TEST I
        [Test]
        public void MountainControls_StillAffectRelief_AndNotTheContinents()
        {
            var flat = new PlanetDefinition { mountainStrength = 0f };
            var high = new PlanetDefinition { mountainStrength = 0.08f };
            var otherFrequency = new PlanetDefinition { mountainFrequency = 12f };
            var reference = new PlanetDefinition();

            var gFlat = new PlanetHeightGenerator(flat);
            var gHigh = new PlanetHeightGenerator(high);
            var gFreq = new PlanetHeightGenerator(otherFrequency);
            var gRef = new PlanetHeightGenerator(reference);

            // Since V0.1.1B mountains are confined to ranges, so compare peak differences
            // (where ranges exist) rather than sphere-wide means.
            float strengthDifference = 0f, frequencyDifference = 0f;
            for (int i = 0; i < SampleCount; i++)
            {
                Vector3 d = FibonacciDirection(i, SampleCount);
                strengthDifference = Mathf.Max(strengthDifference, Mathf.Abs(gHigh.GetElevation(d) - gFlat.GetElevation(d)));
                frequencyDifference = Mathf.Max(frequencyDifference, Mathf.Abs(gFreq.GetElevation(d) - gRef.GetElevation(d)));
                // Mountains stay independent from continental shaping.
                Assert.AreEqual(gRef.GetContinentalField(d), gHigh.GetContinentalField(d), 0f);
                Assert.AreEqual(gRef.GetContinentalField(d), gFreq.GetContinentalField(d), 0f);
            }
            Assert.Greater(strengthDifference, 0.01f);
            Assert.Greater(frequencyDifference, 0.003f);
        }
    }
}
