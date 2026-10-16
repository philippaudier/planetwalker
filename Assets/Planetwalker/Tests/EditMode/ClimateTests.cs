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
    /// V0.1.1E-A climate foundations. Letters refer to the milestone's test list (A - T). Physical
    /// assertions use deterministic synthetic planets; reference seeds only check robustness.
    /// </summary>
    public class ClimateTests
    {
        static readonly Dictionary<int, HydrologyGrid> Grids = new Dictionary<int, HydrologyGrid>();

        static HydrologyGrid Grid(int resolution)
        {
            if (!Grids.TryGetValue(resolution, out HydrologyGrid grid))
            {
                grid = new HydrologyGrid(resolution);
                Grids.Add(resolution, grid);
            }
            return grid;
        }

        /// <summary>Settings without seed-dependent variation (exact symmetries, clean comparisons).</summary>
        static ClimateSettings Clean(int resolution = 64)
        {
            return new ClimateSettings { resolution = resolution, temperatureVariation = 0f, windPerturbation = 0f };
        }

        static ClimateResult Synthetic(Func<Vector3, float> elevation, ClimateSettings settings, Func<Vector3, Vector3> wind = null, int seed = 1)
        {
            return new ClimateGenerator().Compute(Grid(settings.resolution), elevation, 0f, seed, settings, wind);
        }

        static float Longitude(Vector3 d) => Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;

        static float LatitudeDeg(Vector3 d) => Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;

        /// <summary>Uniform east (+1) / west (-1) wind around the world-up axis.</summary>
        static Func<Vector3, Vector3> Zonal(float sign)
        {
            return p =>
            {
                Vector3 east = Vector3.Cross(Vector3.up, p);
                return east.sqrMagnitude > 1e-8f ? east.normalized * sign : Vector3.zero;
            };
        }

        /// <summary>Area-weighted mean of a field over the cells matching a predicate.</summary>
        static float Mean(ClimateResult climate, float[] field, Func<int, bool> select)
        {
            double sum = 0, area = 0;
            for (int c = 0; c < climate.Grid.CellCount; c++)
            {
                if (select(c))
                {
                    sum += field[c] * climate.Grid.GetArea(c);
                    area += climate.Grid.GetArea(c);
                }
            }
            Assert.Greater(area, 0.0, "Empty selection");
            return (float)(sum / area);
        }

        static void AssertFinite(ClimateResult climate, string context)
        {
            for (int c = 0; c < climate.Grid.CellCount; c++)
            {
                Assert.IsTrue(IsFinite(climate.Temperature[c]) && IsFinite(climate.Moisture[c]) && IsFinite(climate.Precipitation[c])
                    && IsFinite(climate.OceanInfluence[c]) && IsFinite(climate.Wind[c].x) && IsFinite(climate.Wind[c].y) && IsFinite(climate.Wind[c].z),
                    $"{context}: non-finite value at cell {c}");
                Assert.GreaterOrEqual(climate.Moisture[c], 0f, context);
                Assert.GreaterOrEqual(climate.Precipitation[c], 0f, context);
            }
        }

        static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        // ------------------------------------------------------------ A, B, P (planets)

        // A
        [Test]
        public void A_SameSeedAndSettings_ProduceIdenticalClimate()
        {
            var planet = new PlanetInstance(new PlanetDefinition { seed = 868497 });
            ClimateResult a = new ClimateGenerator().Compute(planet, new ClimateSettings { resolution = 32 });
            ClimateResult b = new ClimateGenerator().Compute(new PlanetInstance(new PlanetDefinition { seed = 868497 }), new ClimateSettings { resolution = 32 });
            CollectionAssert.AreEqual(a.Temperature, b.Temperature);
            CollectionAssert.AreEqual(a.Moisture, b.Moisture);
            CollectionAssert.AreEqual(a.Precipitation, b.Precipitation);
            CollectionAssert.AreEqual(a.Wind, b.Wind);
            Assert.AreEqual(a.Diagnostics.Iterations, b.Diagnostics.Iterations);
        }

        // B
        [Test]
        public void B_DifferentSeeds_ProduceDistinctRegionalClimates()
        {
            ClimateResult a = new ClimateGenerator().Compute(new PlanetInstance(new PlanetDefinition { seed = 868497 }), new ClimateSettings { resolution = 32 });
            ClimateResult b = new ClimateGenerator().Compute(new PlanetInstance(new PlanetDefinition { seed = 609538 }), new ClimateSettings { resolution = 32 });
            int different = 0;
            for (int c = 0; c < a.Grid.CellCount; c++)
            {
                different += Mathf.Abs(a.Precipitation[c] - b.Precipitation[c]) > 0.3f || a.IsOcean[c] != b.IsOcean[c] ? 1 : 0;
            }
            Assert.Greater(different, a.Grid.CellCount / 10, "Regional climates differ");
        }

        // P
        [Test]
        public void P_Climate_IsIndependentOfTheTerrainMeshResolution()
        {
            ClimateResult coarse = new ClimateGenerator().Compute(new PlanetInstance(new PlanetDefinition { seed = 204018, resolution = 32 }),
                new ClimateSettings { resolution = 32 });
            ClimateResult fine = new ClimateGenerator().Compute(new PlanetInstance(new PlanetDefinition { seed = 204018, resolution = 256 }),
                new ClimateSettings { resolution = 32 });
            CollectionAssert.AreEqual(coarse.Temperature, fine.Temperature);
            CollectionAssert.AreEqual(coarse.Precipitation, fine.Precipitation);
        }

        // ------------------------------------------------------------ D, E, F (forcing)

        // D
        [Test]
        public void D_LatitudeFollowsTheRotationAxis()
        {
            var axis = new Vector3(0.3f, 0.9f, 0.2f);
            ClimateSettings settings = Clean(32);
            settings.rotationAxis = axis;
            ClimateResult climate = Synthetic(d => -0.01f, settings);
            Vector3 n = axis.normalized;
            float maxLatitude = -10f;
            for (int c = 0; c < climate.Grid.CellCount; c++)
            {
                Vector3 p = climate.Grid.GetCenter(c);
                Assert.AreEqual(Mathf.Asin(Vector3.Dot(p, n)), climate.Latitude[c], 1e-5f);
                maxLatitude = Mathf.Max(maxLatitude, climate.Latitude[c]);
            }
            Assert.Greater(maxLatitude * Mathf.Rad2Deg, 87f, "A cell near the rotation pole");
            Assert.AreEqual(0f, ClimateForcing.Latitude(Vector3.Cross(n, Vector3.right).normalized, axis), 1e-5f, "Equator");
            Assert.AreEqual(90f, ClimateForcing.Latitude(n, axis) * Mathf.Rad2Deg, 1e-3f, "Pole");
        }

        // E
        [Test]
        public void E_AnnualMeanInsolation_IsSymmetricBetweenHemispheres()
        {
            foreach (float tilt in new[] { 0f, 10f, ClimateForcing.EarthTilt, 45f })
            {
                double t = tilt * Math.PI / 180.0;
                for (int lat = 0; lat <= 90; lat += 5)
                {
                    double l = lat * Math.PI / 180.0;
                    Assert.AreEqual(ClimateForcing.AnnualMeanInsolation(l, t), ClimateForcing.AnnualMeanInsolation(-l, t), 1e-9, $"tilt {tilt} lat {lat}");
                }
            }
            // Symmetric planet (aquaplanet, no seed variation): mirrored climate.
            ClimateResult climate = Synthetic(d => -0.01f, Clean(32));
            for (int c = 0; c < climate.Grid.CellCount; c++)
            {
                Vector3 p = climate.Grid.GetCenter(c);
                int mirror = climate.Grid.CellFromDirection(new Vector3(p.x, -p.y, p.z));
                Assert.AreEqual(climate.Temperature[c], climate.Temperature[mirror], 1e-3f, "Mirrored temperature");
            }
            float north = Mean(climate, climate.Precipitation, c => climate.Latitude[c] > 0f);
            float south = Mean(climate, climate.Precipitation, c => climate.Latitude[c] < 0f);
            Assert.AreEqual(north, south, 0.02f * north, "Mirrored precipitation");
        }

        // F
        [Test]
        public void F_AxialTilt_RedistributesAnnualInsolationConsistently()
        {
            double Q(double latDeg, double tiltDeg) => ClimateForcing.AnnualMeanInsolation(latDeg * Math.PI / 180.0, tiltDeg * Math.PI / 180.0);
            Assert.AreEqual(0.0, Q(90, 0), 1e-12, "No tilt: the poles get no sun");
            Assert.Less(Q(90, 0), Q(90, 23.44));
            Assert.Less(Q(90, 23.44), Q(90, 45));
            Assert.Greater(Q(0, 0), Q(0, 23.44));
            Assert.Greater(Q(0, 23.44), Q(0, 45));
            // Earth: about 417 / 1361 at the equator and 173 / 1361 at the poles.
            Assert.AreEqual(0.306, Q(0, 23.44), 0.008);
            Assert.AreEqual(0.127, Q(90, 23.44), 0.008);
            foreach (double tilt in new[] { 0.0, 23.44, 45.0 })
            {
                double sum = 0;
                const int steps = 1800;
                for (int i = 0; i < steps; i++)
                {
                    double lat = -90.0 + 180.0 * (i + 0.5) / steps;
                    sum += Q(lat, tilt) * Math.Cos(lat * Math.PI / 180.0) * (Math.PI / steps);
                }
                Assert.AreEqual(0.25, sum / 2.0, 1e-3, $"Global mean insolation S0/4 at tilt {tilt}");
            }
        }

        // ------------------------------------------------------------ G, H (temperature)

        // G
        [Test]
        public void G_Elevation_LowersTemperatureByTheLapseRate()
        {
            Vector3 center = new Vector3(1f, 0.1f, 0.2f).normalized;
            Func<Vector3, float> flat = d => 0.005f;
            Func<Vector3, float> bump = d => 0.005f + 0.03f * Mathf.Exp(-Mathf.Pow(Vector3.Angle(d, center) / 6f, 2f));
            ClimateSettings settings = Clean(32);
            ClimateResult a = Synthetic(flat, settings), b = Synthetic(bump, settings);
            int tested = 0;
            for (int c = 0; c < a.Grid.CellCount; c++)
            {
                float dh = b.HeightKm[c] - a.HeightKm[c];
                Assert.AreEqual(-settings.lapseRate * dh, b.Temperature[c] - a.Temperature[c], 1e-3f);
                tested += dh > 1f ? 1 : 0;
            }
            Assert.Greater(tested, 5, "Mountain cells (> 1 km) tested");
            // Elevation mapping: 1% of the radius above sea level = elevationScale km.
            Assert.AreEqual(0.5f, a.HeightKm[0], 1e-4f);
        }

        // H
        [Test]
        public void H_OceanInfluence_DecreasesInland_AndAmplifiesContinentalTemperatures()
        {
            Func<Vector3, float> halfOcean = d => d.x < 0f ? -0.01f : 0.001f;
            ClimateSettings settings = Clean(32);
            ClimateResult climate = Synthetic(halfOcean, settings);
            HydrologyGrid grid = climate.Grid;
            for (int c = 0; c < grid.CellCount; c++)
            {
                if (climate.IsOcean[c])
                {
                    Assert.AreEqual(0f, climate.OceanDistanceKm[c]);
                    Assert.AreEqual(1f, climate.OceanInfluence[c]);
                    continue;
                }
                Assert.AreEqual(Mathf.Exp(-climate.OceanDistanceKm[c] / settings.oceanInfluenceDistance), climate.OceanInfluence[c], 1e-6f);
                for (int k = grid.GetNeighborStart(c); k < grid.GetNeighborEnd(c); k++)
                {
                    float step = 2f * Mathf.Asin(0.5f * grid.GetNeighborDistance(k)) * settings.planetRadiusKm;
                    Assert.LessOrEqual(climate.OceanDistanceKm[c], climate.OceanDistanceKm[grid.GetNeighbor(k)] + step + 1e-2f, "Shortest path over the sphere");
                }
            }
            // Coast vs interior at the same latitude and elevation: interiors amplify the departure from 10 C.
            int Cell(float lat, float lon) => grid.CellFromDirection(new Vector3(
                Mathf.Cos(lat * Mathf.Deg2Rad) * Mathf.Cos(lon * Mathf.Deg2Rad), Mathf.Sin(lat * Mathf.Deg2Rad), Mathf.Cos(lat * Mathf.Deg2Rad) * Mathf.Sin(lon * Mathf.Deg2Rad)));
            int coastCold = Cell(65f, 95f), interiorCold = Cell(65f, 10f);
            int coastHot = Cell(5f, 92f), interiorHot = Cell(5f, 0f);
            Assert.Greater(climate.OceanDistanceKm[interiorCold], climate.OceanDistanceKm[coastCold] + 1000f);
            Assert.Less(climate.Temperature[interiorCold], climate.Temperature[coastCold], "Cold interiors colder than coasts");
            Assert.Greater(climate.Temperature[interiorHot], climate.Temperature[coastHot], "Hot interiors hotter than coasts");
            // Modest: the annual-mean effect stays within the configured amplification.
            Assert.Less(Mathf.Abs(climate.Temperature[interiorHot] - climate.Temperature[coastHot]), 5f);
        }

        // ------------------------------------------------------------ I, J, K, L, T (transport)

        // I, T
        [Test]
        public void IT_Fields_AreFiniteAndNonNegative_ForPlanetsExtremeSettingsAndPoles()
        {
            foreach (int seed in new[] { 868497, 926747, 204018 })
            {
                var planet = new PlanetInstance(new PlanetDefinition { seed = seed });
                foreach (float tilt in new[] { 0f, 45f })
                {
                    ClimateResult climate = new ClimateGenerator().Compute(planet, new ClimateSettings { resolution = 32, axialTilt = tilt, elevationScale = 3f });
                    AssertFinite(climate, $"seed {seed} tilt {tilt}");
                    Assert.Less(climate.Diagnostics.MassBalanceError, 1e-4, "Moisture is neither created nor destroyed by the numerics");
                }
            }
            // Rotation poles on a cube corner and on a face center (no NaN near the poles).
            foreach (Vector3 axis in new[] { new Vector3(1f, 1f, 1f), Vector3.forward, Vector3.up })
            {
                ClimateSettings settings = new ClimateSettings { resolution = 32, rotationAxis = axis };
                ClimateResult climate = new ClimateGenerator().Compute(new PlanetInstance(new PlanetDefinition { seed = 554492 }), settings);
                AssertFinite(climate, "axis " + axis);
            }
        }

        // J
        [Test]
        public void J_OceansAreTheMoistureSource()
        {
            ClimateResult climate = Synthetic(d => d.x < 0f ? -0.01f : 0.001f, Clean(32));
            double ocean = 0;
            for (int c = 0; c < climate.Grid.CellCount; c++)
            {
                if (climate.IsOcean[c])
                {
                    ocean += climate.Evaporation[c];
                }
                else
                {
                    Assert.AreEqual(0f, climate.Evaporation[c], "Land never creates moisture");
                }
            }
            Assert.Greater(ocean, 0.0);
            Assert.Greater(climate.Diagnostics.Evaporation, 0.0);
            Assert.AreEqual(climate.Diagnostics.Evaporation + climate.Diagnostics.Recycled, climate.Diagnostics.Precipitation,
                0.02 * climate.Diagnostics.Precipitation, "Steady state: precipitation balances ocean evaporation + land recycling");
            Assert.Less(climate.Diagnostics.Recycled, climate.Diagnostics.Precipitation, "Recycling only returns part of the rain");

            ClimateResult dry = Synthetic(d => 0.002f, Clean(32));
            for (int c = 0; c < dry.Grid.CellCount; c++)
            {
                Assert.AreEqual(0f, dry.Moisture[c], "No ocean, no moisture");
                Assert.AreEqual(0f, dry.Precipitation[c]);
            }
        }

        // K
        [Test]
        public void K_MoistureTransport_CrossesCubeFaceSeamsContinuously()
        {
            // Ocean patch on the +X face, wind blowing east (towards -z here), across the +X / -Z seam at
            // longitude -45 deg; land elsewhere.
            Vector3 source = new Vector3(1f, 0f, 0.35f).normalized;
            Func<Vector3, float> elevation = d => Vector3.Angle(d, source) < 14f ? -0.01f : 0.001f;
            ClimateResult climate = Synthetic(elevation, Clean(64), Zonal(1f));
            HydrologyGrid grid = climate.Grid;
            var profile = new List<(float Lon, float Moisture, int Face)>();
            for (float lon = 0f; lon >= -75f; lon -= 1f)
            {
                var p = new Vector3(Mathf.Cos(lon * Mathf.Deg2Rad), 0.02f, Mathf.Sin(lon * Mathf.Deg2Rad)).normalized;
                int cell = grid.CellFromDirection(p);
                grid.GetFaceCoordinates(cell, out int face, out _, out _);
                profile.Add((lon, climate.Moisture[cell], face));
            }
            // Downwind decay without jumps: the ratio between samples one degree apart across the seam
            // stays within the range of ratios measured on either face.
            var within = new List<float>();
            var across = new List<float>();
            for (int i = 1; i < profile.Count; i++)
            {
                Assert.Greater(profile[i].Moisture, 0f, "Moisture reaches lon " + profile[i].Lon);
                float ratio = profile[i].Moisture / profile[i - 1].Moisture;
                // Over land the moisture mostly decays downwind; small local rises come from the plume shape.
                Assert.That(ratio, Is.InRange(0.6f, 1.25f), $"Smooth profile at lon {profile[i].Lon}");
                if (profile[i].Lon <= -20f)
                {
                    (profile[i].Face != profile[i - 1].Face ? across : within).Add(ratio);
                }
            }
            Assert.Greater(across.Count, 0, "The profile crosses a cube-face seam");
            within.Sort();
            foreach (float ratio in across)
            {
                Assert.That(ratio, Is.InRange(within[0] - 0.03f, within[within.Count - 1] + 0.03f), "No discontinuity at the seam");
            }
        }

        // K (V0.1.1E-A.1): no grid imprint. On an aquaplanet the climate depends on latitude only, so any
        // departure from the zonal mean is numerical; it must not concentrate along cube-face seams.
        [Test]
        public void K2_Aquaplanet_HasNoSeamImprint()
        {
            ClimateResult climate = Synthetic(d => -0.01f, Clean(64));
            HydrologyGrid grid = climate.Grid;
            var sum = new double[361];
            var area = new double[361];
            int Bin(int i) => Mathf.Clamp(Mathf.RoundToInt((climate.Latitude[i] * Mathf.Rad2Deg + 90f) * 2f), 0, 360);
            for (int i = 0; i < grid.CellCount; i++)
            {
                sum[Bin(i)] += climate.Moisture[i] * grid.GetArea(i);
                area[Bin(i)] += grid.GetArea(i);
            }
            double seam = 0, seamCount = 0, interior = 0, interiorCount = 0;
            int n = grid.Resolution;
            for (int i = 0; i < grid.CellCount; i++)
            {
                float zonal = (float)(sum[Bin(i)] / area[Bin(i)]);
                double deviation = Mathf.Abs(climate.Moisture[i] - zonal) / Mathf.Max(0.02f, zonal);
                grid.GetFaceCoordinates(i, out _, out int ci, out int cj);
                int toEdgeI = Mathf.Min(ci, n - 1 - ci), toEdgeJ = Mathf.Min(cj, n - 1 - cj);
                if (toEdgeI < 2 && toEdgeJ < 2)
                {
                    continue; // the 8 cube corners (3 neighbouring faces) are reported, not asserted
                }
                if (Mathf.Min(toEdgeI, toEdgeJ) < 2)
                {
                    seam += deviation;
                    seamCount++;
                }
                else
                {
                    interior += deviation;
                    interiorCount++;
                }
            }
            double seamMean = seam / seamCount, interiorMean = interior / interiorCount;
            TestContext.WriteLine($"Aquaplanet moisture deviation from the zonal mean: seams {seamMean * 100:0.00}%, face interiors {interiorMean * 100:0.00}%");
            Assert.Less(seamMean, 1.5 * interiorMean + 0.002, "Seams are not imprinted on the moisture field");
            Assert.Less(interiorMean, 0.03, "Zonal symmetry (numerical noise only)");
        }

        // L
        [Test]
        public void L_Winds_AreTangentFiniteNonZero_AndFormThreeCellBelts()
        {
            foreach (int resolution in new[] { 32, 64, 128 })
            {
                HydrologyGrid grid = Grid(resolution);
                for (int c = 0; c < grid.CellCount; c++)
                {
                    Vector3 p = grid.GetCenter(c);
                    Vector3 w = ClimateForcing.PrevailingWind(p, Vector3.up, 868497, 20f);
                    Assert.IsTrue(IsFinite(w.x) && IsFinite(w.y) && IsFinite(w.z));
                    Assert.Less(Mathf.Abs(Vector3.Dot(w, p)), 1e-5f, "Tangent");
                    Assert.Greater(w.magnitude, 1e-3f, "Never zero-length at a cell center");
                }
            }
            float Zonal(float lat)
            {
                var p = new Vector3(Mathf.Cos(lat * Mathf.Deg2Rad), Mathf.Sin(lat * Mathf.Deg2Rad), 0f);
                Vector3 east = Vector3.Cross(Vector3.up, p).normalized;
                return Vector3.Dot(ClimateForcing.PrevailingWind(p, Vector3.up, 1, 0f), east);
            }
            foreach (float sign in new[] { 1f, -1f })
            {
                Assert.Less(Zonal(15f * sign), -0.5f, "Trade easterlies");
                Assert.Greater(Zonal(45f * sign), 0.5f, "Mid-latitude westerlies");
                Assert.Less(Zonal(75f * sign), -0.3f, "Polar easterlies");
            }
            // Smooth: no abrupt direction change between nearby latitudes.
            for (float lat = -80f; lat < 80f; lat += 0.5f)
            {
                Assert.Less(Mathf.Abs(Zonal(lat + 0.5f) - Zonal(lat)), 0.1f);
            }
        }

        // ------------------------------------------------------------ M, N, O (precipitation)

        /// <summary>Equatorial land strip between two oceans with a north-south ridge at longitude 0.</summary>
        static float Strip(Vector3 d, bool ridge)
        {
            float lon = Longitude(d), lat = LatitudeDeg(d);
            if (Mathf.Abs(lon) > 35f || Mathf.Abs(lat) > 40f)
            {
                return -0.01f;
            }
            float h = 0.002f;
            if (ridge)
            {
                h += 0.025f * Mathf.Exp(-(lon / 4f) * (lon / 4f)); // 2.5 km at elevation scale 1
            }
            return h;
        }

        static float Band(ClimateResult climate, float[] field, float lonMin, float lonMax)
        {
            return Mean(climate, field, c =>
            {
                Vector3 p = climate.Grid.GetCenter(c);
                float lon = Longitude(p);
                return Mathf.Abs(LatitudeDeg(p)) < 12f && lon >= lonMin && lon <= lonMax;
            });
        }

        // M, N
        [Test]
        public void MN_MountainBarriers_MakeWetWindwardAndDryLeewardSides_FollowingTheWind()
        {
            ClimateSettings settings = Clean(64);
            // Eastward wind moves towards decreasing longitude here (east = up x p): upwind is +lon.
            foreach (float sign in new[] { 1f, -1f })
            {
                ClimateResult ridge = Synthetic(d => Strip(d, true), settings, Zonal(sign));
                ClimateResult flat = Synthetic(d => Strip(d, false), settings, Zonal(sign));
                float upMin = sign > 0f ? 1.5f : -7f, upMax = sign > 0f ? 7f : -1.5f;
                float downMin = sign > 0f ? -7f : 1.5f, downMax = sign > 0f ? -1.5f : 7f;
                string context = sign > 0f ? "east wind" : "west wind (reversed)";

                float windward = Band(ridge, ridge.Precipitation, upMin, upMax);
                float leeward = Band(ridge, ridge.Precipitation, downMin, downMax);
                float windwardFlat = Band(flat, flat.Precipitation, upMin, upMax);
                float leewardFlat = Band(flat, flat.Precipitation, downMin, downMax);
                Assert.Greater(windward, 1.3f * windwardFlat, context + ": windward rain increases");
                Assert.Less(leeward, 0.8f * leewardFlat, context + ": leeward rain shadow");
                Assert.Greater(windward, 1.5f * leeward, context + ": windward wetter than leeward");

                // Moisture drops across the barrier, more than over the flat strip.
                float before = Band(ridge, ridge.Moisture, sign > 0f ? 9f : -15f, sign > 0f ? 15f : -9f);
                float after = Band(ridge, ridge.Moisture, sign > 0f ? -15f : 9f, sign > 0f ? -9f : 15f);
                float beforeFlat = Band(flat, flat.Moisture, sign > 0f ? 9f : -15f, sign > 0f ? 15f : -9f);
                float afterFlat = Band(flat, flat.Moisture, sign > 0f ? -15f : 9f, sign > 0f ? -9f : 15f);
                Assert.Less(after / before, 0.8f * (afterFlat / beforeFlat), context + ": moisture lost over the barrier");
            }
        }

        // O
        [Test]
        public void O_AquaplanetPrecipitation_FollowsLargeScaleLatitudeTendencies()
        {
            ClimateResult climate = Synthetic(d => -0.01f, Clean(64));
            float BandP(float min, float max) => Mean(climate, climate.Precipitation, c =>
            {
                float a = Mathf.Abs(climate.Latitude[c]) * Mathf.Rad2Deg;
                return a >= min && a < max;
            });
            float BandT(float min, float max) => Mean(climate, climate.Temperature, c =>
            {
                float a = Mathf.Abs(climate.Latitude[c]) * Mathf.Rad2Deg;
                return a >= min && a < max;
            });
            float equator = BandP(0f, 10f), subtropics = BandP(20f, 32f), midLatitudes = BandP(40f, 58f), polar = BandP(70f, 90f);
            TestContext.WriteLine($"Aquaplanet precipitation: equator {equator:0.00}, subtropics {subtropics:0.00}, mid-latitudes {midLatitudes:0.00}, polar {polar:0.00}; "
                + $"T equator {BandT(0f, 10f):0.0}, mid {BandT(40f, 58f):0.0}, polar {BandT(70f, 90f):0.0}");
            Assert.Greater(equator, 1.5f * subtropics, "Humid equatorial belt, dry subtropics");
            Assert.Greater(midLatitudes, subtropics, "Mid-latitude storm tracks");
            Assert.Less(polar, 0.7f * midLatitudes, "Cold polar air holds little moisture");
            Assert.That(BandT(0f, 10f), Is.InRange(22f, 31f));
            Assert.Less(BandT(70f, 90f), -5f);
            Assert.Greater(BandT(40f, 58f), BandT(70f, 90f));
        }

        // V0.1.1E-A.1 calibration guards (measured on reference planets, not tuned per seed).
        [Test]
        public void Calibration_TemperateCoastsAreWet_AndAridityIsNotSystematic()
        {
            var grid = new HydrologyGrid(64);
            float cellAngle = Mathf.PI * 0.5f / 64f;
            double arid = 0, coasts = 0, temperate = 0;
            int[] seeds = { 868497, 280695, 335559 };
            foreach (int seed in seeds)
            {
                ClimateResult c = new ClimateGenerator().Compute(new PlanetInstance(new PlanetDefinition { seed = seed }), new ClimateSettings(), grid);
                float Lat(int i) => Mathf.Abs(c.Latitude[i]) * Mathf.Rad2Deg;
                bool Temperate(int i) => !c.IsOcean[i] && Lat(i) >= 38f && Lat(i) < 58f;
                double aridArea = 0, landArea = 0;
                for (int i = 0; i < c.Grid.CellCount; i++)
                {
                    if (!c.IsOcean[i] && Lat(i) < 60f)
                    {
                        landArea += c.Grid.GetArea(i);
                        aridArea += c.Precipitation[i] < 0.25f ? c.Grid.GetArea(i) : 0;
                    }
                }
                arid += aridArea / landArea;
                coasts += Mean(c, c.Precipitation, i =>
                {
                    if (!Temperate(i) || c.OceanDistanceKm[i] > 500f || c.Wind[i].sqrMagnitude < 1e-8f) return false;
                    Vector3 upwind = (c.Grid.GetCenter(i) - c.Wind[i].normalized * cellAngle * 3f).normalized;
                    return c.IsOcean[c.Grid.CellFromDirection(upwind)];
                });
                temperate += Mean(c, c.Precipitation, Temperate);
            }
            arid /= seeds.Length;
            coasts /= seeds.Length;
            temperate /= seeds.Length;
            TestContext.WriteLine($"Arid land below 60 deg {arid * 100:0}%, windward temperate coasts x{coasts:0.00}, temperate land x{temperate:0.00}");
            Assert.Less(arid, 0.45, "Deserts exist but continents are not systematically arid");
            Assert.Greater(coasts, 1f, "Windward temperate coasts (westerlies from the ocean) are wet");
            Assert.Greater(coasts, temperate, "Coasts facing the westerlies are wetter than temperate land overall");
            Assert.Greater(temperate, 0.45f, "Temperate land is not desert on average");
        }

        [Test]
        public void Legends_MatchTheirViews()
        {
            var settings = new ClimateSettings();
            foreach (PlanetDebugView view in new[] { PlanetDebugView.ClimateTemperature, PlanetDebugView.ClimateMoisture,
                         PlanetDebugView.ClimatePrecipitation, PlanetDebugView.ClimateWinds, PlanetDebugView.ClimateContinentality })
            {
                ClimateDebugColors.Legend legend = ClimateDebugColors.GetLegend(view, settings);
                Assert.IsNotEmpty(legend.Title, view.ToString());
                Assert.Greater(legend.Gradient.Length, 16);
                Assert.AreEqual(legend.TickPositions.Length, legend.TickLabels.Length);
                for (int i = 0; i < legend.TickPositions.Length; i++)
                {
                    Assert.That(legend.TickPositions[i], Is.InRange(0f, 1f));
                    Assert.IsTrue(i == 0 || legend.TickPositions[i] > legend.TickPositions[i - 1], view + ": ticks in order");
                }
            }
            // The bar follows the data transform: x1 (planet mean) sits where the log2 axis crosses 0.
            ClimateDebugColors.Legend rain = ClimateDebugColors.GetLegend(PlanetDebugView.ClimatePrecipitation, settings);
            int mean = Array.IndexOf(rain.TickLabels, "x1");
            Assert.AreEqual(Mathf.InverseLerp(-3.32f, 3f, 0f), rain.TickPositions[mean], 1e-4f);
            int sample = Mathf.RoundToInt(rain.TickPositions[mean] * (rain.Gradient.Length - 1));
            Color expected = ClimateDebugColors.Precipitation(1f);
            Assert.Less(Mathf.Abs(rain.Gradient[sample].g - expected.g) + Mathf.Abs(rain.Gradient[sample].b - expected.b), 0.05f, "Bar color at x1 = map color at x1");
            ClimateDebugColors.Legend temperature = ClimateDebugColors.GetLegend(PlanetDebugView.ClimateTemperature, settings);
            int zero = Array.IndexOf(temperature.TickLabels, "0");
            Assert.AreEqual(50f / 80f, temperature.TickPositions[zero], 1e-4f, "0 C at its place on the -50..30 bar");
        }

        // ------------------------------------------------------------ S (diagnostics)

        // S
        [Test]
        public void S_Diagnostics_AreAreaWeighted()
        {
            ClimateResult climate = new ClimateGenerator().Compute(new PlanetInstance(new PlanetDefinition { seed = 280695 }), new ClimateSettings { resolution = 32 });
            HydrologyGrid grid = climate.Grid;
            double t = 0, area = 0, frozen = 0, land = 0, landT = 0, unweighted = 0;
            for (int c = 0; c < grid.CellCount; c++)
            {
                double a = grid.GetArea(c);
                t += climate.Temperature[c] * a;
                area += a;
                frozen += climate.Temperature[c] < 0f ? a : 0;
                unweighted += climate.Temperature[c];
                if (!climate.IsOcean[c])
                {
                    land += a;
                    landT += climate.Temperature[c] * a;
                }
            }
            ClimateDiagnostics d = climate.Diagnostics;
            Assert.AreEqual(t / area, d.MeanTemperature, 1e-3);
            Assert.AreEqual(frozen / area, d.FrozenFraction, 1e-5);
            Assert.AreEqual(landT / land, d.LandMeanTemperature, 1e-3);
            Assert.AreEqual(land / area, d.LandFraction, 1e-5);
            Assert.AreNotEqual(unweighted / grid.CellCount, d.MeanTemperature, "Cells are not equal-area: weighting matters");
            Assert.AreEqual(4.0 * Math.PI, area, 1e-3, "Areas cover the sphere");
            Assert.AreEqual(1.0, Mean(climate, climate.Precipitation, c => true), 1e-4, "Precipitation index: planet mean 1");
        }

        // ------------------------------------------------------------ C, Q, R (runtime)

        GameObject root;

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                Object.DestroyImmediate(root);
                root = null;
            }
            foreach (Camera camera in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (camera.name == "Climate Test Camera")
                {
                    Object.DestroyImmediate(camera.gameObject);
                }
            }
        }

        PlanetVisualController CreateVisual(Quaternion rotation)
        {
            root = new GameObject("Climate Test");
            root.transform.rotation = rotation;
            var visual = root.AddComponent<PlanetVisualController>();
            visual.Hydrology.resolution = HydrologySettings.PreviewResolution;
            visual.Climate.resolution = ClimateSettings.LowResolution;
            return visual;
        }

        // C
        [Test]
        public void C_CameraAndPlanetOrientation_DoNotAffectTheClimate()
        {
            var definition = new PlanetDefinition { seed = 335559, resolution = 24 };
            ClimateResult reference = new ClimateGenerator().Compute(new PlanetInstance(definition), new ClimateSettings { resolution = 32 });
            foreach (Quaternion rotation in new[] { Quaternion.identity, Quaternion.Euler(37f, 120f, -15f) })
            {
                var camera = new GameObject("Climate Test Camera").AddComponent<Camera>();
                camera.transform.SetPositionAndRotation(rotation * new Vector3(0f, 80f, -300f), Quaternion.Euler(20f, rotation.eulerAngles.y, 0f));
                PlanetVisualController visual = CreateVisual(rotation);
                visual.DebugView = PlanetDebugView.ClimateTemperature;
                visual.Render(new PlanetInstance(definition));
                CollectionAssert.AreEqual(reference.Temperature, visual.CurrentClimate.Temperature);
                CollectionAssert.AreEqual(reference.Precipitation, visual.CurrentClimate.Precipitation);
                Object.DestroyImmediate(root);
                root = null;
                Object.DestroyImmediate(camera.gameObject);
            }
        }

        // Q
        [Test]
        public void Q_ViewChanges_DoNotRecomputeTheClimate_AndArrowsAreReleased()
        {
            PlanetVisualController visual = CreateVisual(Quaternion.identity);
            visual.DebugView = PlanetDebugView.ClimateTemperature;
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 926747, resolution = 24 }));
            Assert.AreEqual(1, visual.ClimateCache.ComputeCount);
            PlanetDebugView[] views =
            {
                PlanetDebugView.ClimateMoisture, PlanetDebugView.ClimatePrecipitation, PlanetDebugView.ClimateWinds,
                PlanetDebugView.ClimateContinentality, PlanetDebugView.None, PlanetDebugView.LakeDepth, PlanetDebugView.ClimateTemperature,
            };
            foreach (PlanetDebugView view in views)
            {
                visual.DebugView = view;
                visual.RefreshColors();
            }
            visual.DebugView = PlanetDebugView.ClimateWinds;
            visual.ClimateWindDirectionColors = true;
            visual.RefreshColors();
            visual.ClimateWindDirectionColors = false;
            visual.RefreshColors();
            visual.Rivers.widthMultiplier = 2f;
            visual.Lakes.color = Color.red;
            visual.Lakes.showLakes = !visual.Lakes.showLakes;
            visual.RefreshColors();
            Assert.AreEqual(1, visual.ClimateCache.ComputeCount, "Views, rivers and lakes never recompute the climate");
            Mesh arrows = visual.WindArrowMesh;
            Assert.IsNotNull(arrows, "Winds view built its arrows");
            Assert.Greater(arrows.vertexCount, 0);

            // Terrain mesh resolution alone: same climate result.
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 926747, resolution = 40 }));
            Assert.AreEqual(1, visual.ClimateCache.ComputeCount, "Mesh resolution is not a climate input");

            visual.Climate.axialTilt = 30f;
            visual.RefreshColors();
            Assert.AreEqual(2, visual.ClimateCache.ComputeCount, "Climate settings invalidate the climate");
            visual.Render(new PlanetInstance(new PlanetDefinition { seed = 926747, resolution = 40, seaLevel = 0.004f }));
            Assert.AreEqual(3, visual.ClimateCache.ComputeCount, "Sea level invalidates the climate");

            Object.DestroyImmediate(root);
            root = null;
            Assert.IsTrue(arrows == null, "Arrow mesh released with the planet");
        }

        // R
        [Test]
        public void R_Climate_LeavesHydrologyRiversAndLakesUnchanged()
        {
            PlanetVisualController visual = CreateVisual(Quaternion.identity);
            visual.Lakes.showLakes = true;
            visual.Rivers.showRivers = true;
            var planet = new PlanetInstance(new PlanetDefinition { seed = 609538, resolution = 32 });
            visual.Render(planet);
            HydrologyResult hydrology = visual.CurrentHydrology;
            LakeSet lakes = visual.CurrentLakes;
            RiverNetwork rivers = visual.CurrentRivers;
            int hydrologyCount = visual.HydrologyCache.ComputeCount, analyses = visual.LakeCache.AnalysisCount;
            var receivers = new int[hydrology.Grid.CellCount];
            for (int c = 0; c < receivers.Length; c++)
            {
                receivers[c] = hydrology.GetReceiver(c);
            }
            var levels = new float[lakes.Count];
            for (int i = 0; i < lakes.Count; i++)
            {
                levels[i] = lakes.GetLake(i).Level;
            }

            foreach (PlanetDebugView view in new[] { PlanetDebugView.ClimatePrecipitation, PlanetDebugView.ClimateWinds, PlanetDebugView.None })
            {
                visual.DebugView = view;
                visual.RefreshColors();
            }
            Assert.AreEqual(hydrologyCount, visual.HydrologyCache.ComputeCount);
            Assert.AreEqual(analyses, visual.LakeCache.AnalysisCount);
            Assert.AreSame(hydrology, visual.CurrentHydrology);
            Assert.AreSame(lakes, visual.CurrentLakes);
            Assert.AreSame(rivers, visual.CurrentRivers);
            for (int c = 0; c < receivers.Length; c++)
            {
                Assert.AreEqual(receivers[c], hydrology.GetReceiver(c));
            }
            for (int i = 0; i < lakes.Count; i++)
            {
                Assert.AreEqual(levels[i], lakes.GetLake(i).Level);
            }
        }

        // ------------------------------------------------------------ Reference planets and performance

        [Test]
        public void ReferencePlanets_Report()
        {
            foreach (int seed in new[] { 868497, 609538, 926747, 204018 })
            {
                var planet = new PlanetInstance(new PlanetDefinition { seed = seed });
                ClimateResult c = new ClimateGenerator().Compute(planet, new ClimateSettings());
                AssertFinite(c, "seed " + seed);
                ClimateDiagnostics d = c.Diagnostics;
                bool Land(int i) => !c.IsOcean[i];
                float Lat(int i) => Mathf.Abs(c.Latitude[i]) * Mathf.Rad2Deg;
                string Zone(float min, float max)
                {
                    bool any = false;
                    for (int i = 0; i < c.Grid.CellCount && !any; i++) any = Land(i) && Lat(i) >= min && Lat(i) < max;
                    return any ? $"{Mean(c, c.Temperature, i => Land(i) && Lat(i) >= min && Lat(i) < max):0} C / x{Mean(c, c.Precipitation, i => Land(i) && Lat(i) >= min && Lat(i) < max):0.00}" : "-";
                }
                bool AnyInterior = false;
                for (int i = 0; i < c.Grid.CellCount; i++) AnyInterior |= Land(i) && c.OceanDistanceKm[i] > 1200f;
                string interior = AnyInterior
                    ? $"x{Mean(c, c.Precipitation, i => Land(i) && c.OceanDistanceKm[i] > 1200f):0.00}" : "-";
                bool windwardAny = false, leewardAny = false;
                for (int i = 0; i < c.Grid.CellCount; i++)
                {
                    windwardAny |= Land(i) && c.Upslope[i] > 0.004f;
                    leewardAny |= Land(i) && c.Upslope[i] < -0.004f;
                }
                string slopes = windwardAny && leewardAny
                    ? $"windward x{Mean(c, c.Precipitation, i => Land(i) && c.Upslope[i] > 0.004f):0.00} / leeward x{Mean(c, c.Precipitation, i => Land(i) && c.Upslope[i] < -0.004f):0.00}"
                    : "-";
                TestContext.WriteLine($"seed {seed}: T min/mean/max {d.MinTemperature:0}/{d.MeanTemperature:0.0}/{d.MaxTemperature:0} C, land {d.LandMeanTemperature:0.0}, ocean {d.OceanMeanTemperature:0.0}, "
                    + $"below 0 C {d.FrozenFraction * 100f:0}%; land by zone (T / rain): equatorial {Zone(0, 12)}, subtropical {Zone(18, 32)}, temperate {Zone(38, 58)}, polar {Zone(65, 90)}; "
                    + $"coast (<300 km) x{Mean(c, c.Precipitation, i => Land(i) && c.OceanDistanceKm[i] < 300f):0.00}, interior (>1200 km) {interior}; slopes {slopes}; "
                    + $"rain land x{d.LandMeanPrecipitation:0.00} / ocean x{d.OceanMeanPrecipitation:0.00}; {d.Iterations} it, converged {d.Converged}, balance err {d.MassBalanceError:0.0e0}");
            }
        }

        [Test]
        public void Performance_Report()
        {
            var planet = new PlanetInstance(new PlanetDefinition { seed = 868497 });
            foreach (int resolution in new[] { 32, 64, 128 })
            {
                var generator = new ClimateGenerator();
                var grid = new HydrologyGrid(resolution);
                generator.Compute(planet, new ClimateSettings { resolution = resolution }, grid); // warm-up
                long before = GC.GetTotalMemory(true);
                ClimateResult c = generator.Compute(planet, new ClimateSettings { resolution = resolution }, grid);
                long after = GC.GetTotalMemory(false);
                ClimateDiagnostics d = c.Diagnostics;
                TestContext.WriteLine($"climate {resolution}: total {d.TotalMilliseconds:0} ms (temperature + ocean distance {d.TemperatureMilliseconds:0}, "
                    + $"moisture transport {d.MoistureMilliseconds:0}, precipitation {d.PrecipitationMilliseconds:0.0}), {d.Iterations} iterations, "
                    + $"converged {d.Converged}, managed allocation ~{(after - before) / 1024} KB, cells {grid.CellCount}");
            }
        }
    }
}
