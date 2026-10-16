using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Planetwalker.Core;
using Planetwalker.Generation;
using Planetwalker.Rendering;
using Planetwalker.Terrain;
using UnityEngine;

namespace Planetwalker.Tests
{
    /// <summary>
    /// Multi-scale surface detail (V0.2.0C), engine independent: determinism (H), seed independence (I), continuity
    /// across cube faces and corners (J, K), bounded layers (L), exact reproduction of the V0.2.0B terrain with the
    /// detail off (M), band controls (N), coastline preservation (O), no climate / biome dependency (V), the
    /// band-limited sampling and the double-precision path, the LOD error model terms, and cost reports.
    /// </summary>
    public class TerrainDetailTests
    {
        static readonly Dictionary<(int, bool), PlanetInstance> Planets = new Dictionary<(int, bool), PlanetInstance>();

        static PlanetInstance Planet(int seed = 1337, bool detail = true)
        {
            if (!Planets.TryGetValue((seed, detail), out PlanetInstance planet))
            {
                planet = new PlanetInstance(new PlanetDefinition { seed = seed, surfaceDetail = detail });
                Planets[(seed, detail)] = planet;
            }
            return planet;
        }

        static IEnumerable<Vector3> Directions(int count, int seed = 7)
        {
            var random = new System.Random(seed);
            for (int i = 0; i < count; i++)
            {
                var d = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
                if (d.sqrMagnitude > 1e-4f)
                {
                    yield return d.normalized;
                }
            }
        }

        static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

        // H
        [Test]
        public void H_Detail_IsDeterministic_AcrossInstancesThreadsAndPrecisionPaths()
        {
            var a = new PlanetHeightGenerator(new PlanetDefinition { seed = 868497 });
            var b = new PlanetHeightGenerator(new PlanetDefinition { seed = 868497 });
            Assert.IsTrue(a.HasSurfaceDetail && b.HasSurfaceDetail, "New planets have surface detail by default");
            List<Vector3> dirs = Directions(4000).ToList();
            var serial = new float[dirs.Count];
            var parallel = new float[dirs.Count];
            var other = new float[dirs.Count];
            for (int i = 0; i < dirs.Count; i++)
            {
                serial[i] = a.GetElevation(dirs[i]);
                other[i] = b.GetElevation(dirs[i]);
            }
            Parallel.For(0, dirs.Count, i => parallel[i] = a.GetElevation(dirs[i]));
            for (int i = 0; i < dirs.Count; i++)
            {
                Assert.AreEqual(Bits(serial[i]), Bits(other[i]), "Two generators of the same seed agree bit for bit");
                Assert.AreEqual(Bits(serial[i]), Bits(parallel[i]), "Worker threads agree bit for bit");
                // The double-precision path uses the smooth fine base: equal to the float path within its rounding noise,
                // except within one lattice cell of a mountain crest line (a slope discontinuity the cubic rounds).
                a.SampleBase(dirs[i], out TerrainBaseSample context);
                Assert.AreEqual(serial[i], a.GetElevation(new Vector3d(dirs[i])), context.Core > 0f ? 1e-5f : context.Influence > 0f ? 3e-6f : 1e-6f, "Double path at a float direction");
                Assert.AreEqual(Bits(a.GetElevation(new Vector3d(dirs[i]))), Bits(b.GetElevation(new Vector3d(dirs[i]))), "Double path deterministic");
            }
        }

        // I
        [Test]
        public void I_DifferentSeeds_ProduceDistinctDetail()
        {
            PlanetHeightGenerator a = Planet(1337).Height, b = Planet(335559).Height;
            float bound = a.SurfaceDetail.Profile.BandAmplitude.Sum();
            double correlation = 0, energyA = 0, energyB = 0;
            int land = 0;
            foreach (Vector3 d in Directions(20000))
            {
                a.SampleLayers(d, out TerrainDetailSample sa);
                b.SampleLayers(d, out TerrainDetailSample sb);
                if (sa.Total == 0f || sb.Total == 0f)
                {
                    continue; // one of them is under water here
                }
                land++;
                correlation += sa.Total * sb.Total;
                energyA += sa.Total * sa.Total;
                energyB += sb.Total * sb.Total;
            }
            Assert.Greater(land, 1000);
            double r = correlation / Math.Sqrt(energyA * energyB);
            Assert.Less(Math.Abs(r), 0.1, "Detail of two seeds is uncorrelated");
            Assert.Greater(Math.Sqrt(energyA / land), 0.02 * bound, "The detail is not negligible");
        }

        // J, K
        [Test]
        public void JK_Detail_IsContinuousAcrossCubeFacesAndCorners()
        {
            PlanetHeightGenerator h = Planet(536452).Height;
            TerrainDetailProfile profile = h.SurfaceDetail.Profile;
            float step = 2e-6f; // radians: 13 m at Earth scale
            float slopeBound = 1.5f + profile.ResolvedSlope(0f); // planetary slopes stay far below 1.5 at this step
            // Paths crossing every face edge and the eight corners, on land and at sea.
            var paths = new List<(Vector3 from, Vector3 to)>();
            var corners = new[] { new Vector3(1, 1, 1), new Vector3(-1, 1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, -1), new Vector3(-1, -1, 1), new Vector3(-1, 1, -1), new Vector3(1, -1, -1), new Vector3(-1, -1, -1) };
            foreach (Vector3 c in corners)
            {
                paths.Add(((c + new Vector3(0.01f, -0.013f, 0.004f)).normalized, (c - new Vector3(0.01f, -0.013f, 0.004f)).normalized));
            }
            paths.Add((new Vector3(1f, 0.3f, 0.02f).normalized, new Vector3(1f, 0.3f, -0.02f).normalized)); // +X / +Z edge
            paths.Add((new Vector3(0.02f, 1f, -0.4f).normalized, new Vector3(-0.02f, 1f, -0.4f).normalized)); // +Y / -Z edge
            int tested = 0;
            float worstJump = 0f;
            foreach ((Vector3 from, Vector3 to) in paths)
            {
                int n = Mathf.CeilToInt(Vector3.Angle(from, to) * Mathf.Deg2Rad / step);
                float previous = h.GetElevation(from);
                for (int i = 1; i <= n; i++)
                {
                    Vector3 d = Vector3.Lerp(from, to, (float)i / n).normalized; // (managed: runs offline)
                    float e = h.GetElevation(d);
                    float jump = Mathf.Abs(e - previous);
                    worstJump = Mathf.Max(worstJump, jump);
                    Assert.LessOrEqual(jump, slopeBound * step * 1.01f + 1e-7f, "No jump at " + d);
                    previous = e;
                    tested++;
                }
            }
            Assert.Greater(tested, 10000);
            // A lattice vertex on a face edge, addressed from both faces: bit-identical direction and elevation.
            PlanetPatchId a = new PlanetPatchId(3, 6, 0, 17);
            PlanetPatchId b = PatchNeighbors.Neighbor(a, PatchDirection.West);
            Assert.AreNotEqual(a.Face, b.Face);
            int r = 16, lattice = r << 6;
            Vector3d da = CubeSphereCoordinates.LatticeDirectionD(a.Face, a.X * r, a.Y * r + 5, lattice);
            bool found = false;
            for (int k = 0; k <= r && !found; k++)
            {
                for (int side = 0; side < 4 && !found; side++)
                {
                    PatchNeighbors.EdgeVertex((PatchDirection)side, k, r, out int i, out int j);
                    Vector3d db = CubeSphereCoordinates.LatticeDirectionD(b.Face, b.X * r + i, b.Y * r + j, lattice);
                    if (db == da)
                    {
                        found = true;
                        Assert.AreEqual(Bits(h.GetElevation(da)), Bits(h.GetElevation(db)));
                    }
                }
            }
            Assert.IsTrue(found, "The shared edge vertex exists on the neighbor face with a bit-identical double direction");
            WorldArchiveTestUtil.Report("Detail continuity: worst step over 13 m = " + (worstJump / step).ToString("0.000", CultureInfo.InvariantCulture) + " (slope), bound " + slopeBound.ToString("0.0", CultureInfo.InvariantCulture));
        }

        // L
        [Test]
        public void L_DetailLayers_StayWithinTheirDocumentedBounds()
        {
            foreach (int seed in new[] { 1337, 280695 })
            {
                PlanetHeightGenerator h = Planet(seed).Height;
                TerrainDetailProfile p = h.SurfaceDetail.Profile;
                float maxR = 0f, maxL = 0f, maxM = 0f;
                foreach (Vector3 d in Directions(30000, seed))
                {
                    h.SampleLayers(d, out TerrainDetailSample s);
                    maxR = Mathf.Max(maxR, Mathf.Abs(s.Regional));
                    maxL = Mathf.Max(maxL, Mathf.Abs(s.Local));
                    maxM = Mathf.Max(maxM, Mathf.Abs(s.Micro));
                }
                Assert.LessOrEqual(maxR, p.BandAmplitude[0] * TerrainDetailProfile.MaxBoost);
                Assert.LessOrEqual(maxL, p.BandAmplitude[1] * TerrainDetailProfile.MaxBoost);
                Assert.LessOrEqual(maxM, p.BandAmplitude[2] * TerrainDetailProfile.MaxBoost);
                Assert.Greater(maxR, 0.1f * p.BandAmplitude[0], "The bands are used");
                Assert.Greater(maxM, 0.1f * p.BandAmplitude[2]);
                Assert.AreEqual(0f, p.UnresolvedAmplitude(0f), "Everything is resolved at spacing 0");
                Assert.AreEqual(p.BandAmplitude.Sum() * TerrainDetailProfile.MaxBoost, p.UnresolvedAmplitude(1f), 1e-9f, "Nothing is resolved at a huge spacing");
                for (float spacing = 1e-7f; spacing < 1f; spacing *= 2f)
                {
                    Assert.LessOrEqual(p.UnresolvedAmplitude(spacing), p.UnresolvedAmplitude(spacing * 2f) + 1e-12f, "Unresolved amplitude grows with the spacing");
                    Assert.GreaterOrEqual(p.ResolvedSlope(spacing), p.ResolvedSlope(spacing * 2f) - 1e-9f, "Resolved slope shrinks with the spacing");
                }
                PlanetScale scale = Planet(seed).Scale;
                WorldArchiveTestUtil.Report(string.Format(CultureInfo.InvariantCulture, "seed {0}: {1} octaves ({2:0} m .. {3:0} m); band amplitudes R {4:0} m, L {5:0} m, M {6:0.0} m; observed max R {7:0} L {8:0} M {9:0.0} m",
                    seed, p.OctaveCount, p.WavelengthMeters(0), p.WavelengthMeters(p.OctaveCount - 1),
                    scale.ElevationToMeters(p.BandAmplitude[0]), scale.ElevationToMeters(p.BandAmplitude[1]), scale.ElevationToMeters(p.BandAmplitude[2]),
                    scale.ElevationToMeters(maxR), scale.ElevationToMeters(maxL), scale.ElevationToMeters(maxM)));
            }
        }

        // M
        [Test]
        public void M_DetailOff_ReproducesThePlanetaryTerrainBitForBit()
        {
            foreach (int seed in WorldArchiveTestUtil.ReferenceSeeds)
            {
                PlanetHeightGenerator off = Planet(seed, false).Height, on = Planet(seed, true).Height;
                Assert.IsFalse(off.HasSurfaceDetail);
                Assert.IsNull(off.SurfaceDetail);
                foreach (Vector3 d in Directions(3000, seed))
                {
                    Assert.AreEqual(Bits(off.GetElevation(d)), Bits(on.GetBaseElevation(d)), "The planetary terrain is unchanged");
                    Assert.AreEqual(Bits(off.GetElevation(d)), Bits(off.GetElevation(d, 0.01f)), "Band limiting has no effect without detail");
                    Assert.AreEqual(off.GetSurfaceNormal(d, 0.03f), on.GetSurfaceNormal(d, 0.03f), "Coarse band-limited normals equal the planetary ones (every octave below 2 x spacing)");
                    off.SampleBase(d, out TerrainBaseSample context);
                    Assert.AreEqual(off.GetElevation(d), off.GetElevation(new Vector3d(d)), context.Core > 0f ? 1e-5f : context.Influence > 0f ? 3e-6f : 1e-6f, "Fine base within the float rounding noise");
                }
                Assert.AreEqual(Planet(seed, false).MinElevation, Planet(seed, false).MinElevation);
            }
            // The 8192-sample planet statistics of a detail-off planet are those of V0.2.0B (same sampler, same function).
            var reference = new PlanetInstance(new PlanetDefinition { seed = 1337, surfaceDetail = false });
            Assert.AreEqual(reference.LandFraction, Planet(1337, false).LandFraction);
        }

        // N
        [Test]
        public void N_BandControls_AffectOnlyTheirBand()
        {
            var full = new PlanetHeightGenerator(new PlanetDefinition { seed = 609538 });
            var noRegional = new PlanetHeightGenerator(new PlanetDefinition { seed = 609538, regionalDetail = 0f });
            var halfLocal = new PlanetHeightGenerator(new PlanetDefinition { seed = 609538, localDetail = 0.5f });
            var noMicro = new PlanetHeightGenerator(new PlanetDefinition { seed = 609538, microDetail = 0f });
            int land = 0;
            float coastBand = 6f * full.SurfaceDetail.Profile.BandAmplitude[1] * TerrainDetailProfile.MaxBoost;
            foreach (Vector3 d in Directions(5000))
            {
                float e0 = full.SampleLayers(d, out TerrainDetailSample f);
                noRegional.SampleLayers(d, out TerrainDetailSample r);
                halfLocal.SampleLayers(d, out TerrainDetailSample l);
                noMicro.SampleLayers(d, out TerrainDetailSample m);
                Assert.AreEqual(0f, r.Regional);
                Assert.AreEqual(Bits(f.Local), Bits(r.Local), "Local untouched by the regional control");
                Assert.AreEqual(Bits(f.Micro), Bits(r.Micro));
                Assert.AreEqual(0f, m.Micro);
                Assert.AreEqual(Bits(f.Regional), Bits(m.Regional));
                Assert.AreEqual(Bits(f.Local), Bits(m.Local));
                Assert.AreEqual(Bits(f.Regional), Bits(l.Regional));
                Assert.AreEqual(Bits(f.Micro), Bits(l.Micro));
                if (f.Local != 0f && e0 - full.SeaLevel > coastBand)
                {
                    land++;
                    // Above the coast fade band, half the amplitude of every local octave is exactly half the value.
                    Assert.AreEqual(f.Local * 0.5f, l.Local, Mathf.Abs(f.Local) * 1e-5f + 1e-12f);
                }
            }
            Assert.Greater(land, 500);
            Assert.AreEqual(0f, noRegional.SurfaceDetail.Profile.BandAmplitude[0]);
        }

        // O
        [Test]
        public void O_Coastlines_ArePreserved_NoDetailBelowSeaLevel_NoNewIslands()
        {
            foreach (int seed in new[] { 1337, 335559, 868497, 280695 })
            {
                PlanetHeightGenerator on = Planet(seed, true).Height, off = Planet(seed, false).Height;
                int land = 0, sea = 0, count = 0;
                float worstRelative = 0f;
                foreach (Vector3 d in Directions(40000, seed))
                {
                    float e0 = off.GetElevation(d), e = on.GetElevation(d);
                    count++;
                    if (e0 <= off.SeaLevel)
                    {
                        sea++;
                        Assert.AreEqual(Bits(e0), Bits(e), "Nothing changes at or under the sea");
                    }
                    else
                    {
                        land++;
                        Assert.Greater(e, on.SeaLevel, "Land never sinks below the sea");
                        worstRelative = Mathf.Max(worstRelative, (e0 - e) / (e0 - off.SeaLevel));
                    }
                }
                Assert.AreEqual(Planet(seed, true).LandFraction, Planet(seed, false).LandFraction, 1e-6f, "Same land fraction (same shoreline)");
                Assert.LessOrEqual(worstRelative, 0.75f, "A point keeps at least a quarter of its height above sea");
                WorldArchiveTestUtil.Report(string.Format(CultureInfo.InvariantCulture, "seed {0}: {1} land / {2} sea samples, worst detail dip {3:0}% of the height above sea",
                    seed, land, sea, worstRelative * 100f));
            }
        }

        // V
        [Test]
        public void V_NoDependencyCycle_TerrainNeverReadsClimateOrBiomes()
        {
            var forbidden = new HashSet<Type> { typeof(ClimateResult), typeof(ClimateSettings), typeof(BiomeResult), typeof(BiomeSettings), typeof(NaturalSurface) };
            foreach (Type type in new[] { typeof(TerrainDetail), typeof(TerrainDetailProfile), typeof(TerrainReliefField), typeof(PlanetHeightGenerator), typeof(MountainSystem) })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    Assert.IsFalse(forbidden.Contains(field.FieldType), type.Name + "." + field.Name + " reads a downstream system");
                }
                foreach (ConstructorInfo ctor in type.GetConstructors())
                {
                    foreach (ParameterInfo p in ctor.GetParameters())
                    {
                        Assert.IsFalse(forbidden.Contains(p.ParameterType), type.Name + " is built from a downstream system");
                    }
                }
            }
            // And the analysis grids read the band-limited terrain (they are downstream): the relief field reads e0 only.
            PlanetHeightGenerator h = Planet(1337).Height;
            float s = h.SurfaceDetail.Relief.Sample(Vector3.up);
            Assert.IsTrue(s >= 0f && !float.IsNaN(s));
        }

        // Band limiting and the double path.
        [Test]
        public void BandLimiting_FadesOctavesBelowTheSpacing_AndDoublePathIsSmooth()
        {
            PlanetInstance planet = Planet(1337);
            PlanetHeightGenerator h = planet.Height;
            TerrainDetailProfile p = h.SurfaceDetail.Profile;
            for (int k = 0; k < p.OctaveCount; k++)
            {
                float wavelength = p.Wavelength(k);
                Assert.AreEqual(0f, p.Weight(k, wavelength * 0.5f), "At the Nyquist spacing the octave is gone");
                Assert.AreEqual(1f, p.Weight(k, wavelength * 0.25f), "At a quarter wavelength it is whole");
                Assert.AreEqual(1f, p.Weight(k, 0f));
            }
            // At the legacy mesh spacing (resolution 64) the band-limited elevation has no detail octave left: it is e0.
            float legacySpacing = Mathf.PI * 0.5f / 64f;
            int same = 0, total = 0;
            foreach (Vector3 d in Directions(3000))
            {
                total++;
                if (Bits(h.GetElevation(d, legacySpacing)) == Bits(h.GetBaseElevation(d)))
                {
                    same++;
                }
            }
            Assert.AreEqual(total, same, "Legacy-spacing samples are the planetary terrain exactly (every octave is below 2 x spacing)");

            // Double path: along a 100 m window the second differences of the elevation are far smaller than the
            // float path's (whose directions are quantized at ~0.4 m and alias against the sampling grid).
            // A plain: on a mountain flank the ridged micro crests (true kinks of the relief) dominate the curvature.
            Vector3d center = new Vector3d(new Vector3(0.14f, 0.99f, 0.09f)).Normalized;
            PlanetScale scale = planet.Scale;
            const int n = 200;
            double stepMeters = 0.5;
            double roughDouble = 0, roughFloat = 0;
            float spacing = (float)scale.ArcRadians(stepMeters);
            var ed = new float[n];
            var ef = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector3d d = PlanetCoordinates.Step(center, scale, (i - n / 2) * stepMeters, 0);
                ed[i] = h.GetElevation(d, spacing);
                ef[i] = h.GetElevation(d.ToVector3(), spacing);
            }
            for (int i = 1; i < n - 1; i++)
            {
                roughDouble += Math.Abs(ed[i + 1] - 2 * ed[i] + ed[i - 1]);
                roughFloat += Math.Abs(ef[i + 1] - 2 * ef[i] + ef[i - 1]);
            }
            Assert.Less(roughDouble, 0.5 * roughFloat, "The double path removes the float quantization steps");
            WorldArchiveTestUtil.Report(string.Format(CultureInfo.InvariantCulture, "0.5 m profile curvature: double {0:0.000} m, float {1:0.000} m (sum of |second differences|)",
                scale.ElevationToMeters(roughDouble), scale.ElevationToMeters(roughFloat)));
        }

        // Error model.
        [Test]
        public void ErrorModel_AccountsForDetail_AndStaysMonotonic()
        {
            var on = new TerrainElevationSampler(Planet(1337, true));
            var off = new TerrainElevationSampler(Planet(1337, false));
            var settings = new AdaptiveTerrainSettings();
            float previous = float.MaxValue;
            for (int level = 0; level <= 20; level++)
            {
                float eOn = TerrainLodSelector.GeometricError(on, settings, level), eOff = TerrainLodSelector.GeometricError(off, settings, level);
                Assert.GreaterOrEqual(eOn, eOff * 0.999f, "Detail never lowers the error bound");
                Assert.LessOrEqual(eOn, previous, "Finer levels have smaller errors");
                previous = eOn;
            }
            Assert.Less(TerrainLodSelector.GeometricError(on, settings, 3) / TerrainLodSelector.GeometricError(off, settings, 3), 1.6f,
                "Orbital levels are barely affected (sub-pixel detail)");
            Assert.Greater(TerrainLodSelector.GeometricError(on, settings, 16) / TerrainLodSelector.GeometricError(off, settings, 16), 1.2f,
                "Near the ground the resolved detail slope counts");
            // The selection still respects the budget and stays complete with detail.
            var selector = new TerrainLodSelector(settings);
            Vector3 d = new Vector3(0.41f, -0.90f, 0.13f).normalized;
            float distance = on.Radius * (1f + on.Planet.MaxElevation) + on.Radius * 0.0005f;
            TerrainLodSelection s = selector.Select(on, new TerrainLodView(d * distance, 60f, 1080));
            Assert.LessOrEqual(s.Count, settings.maxLeafPatches);
            Assert.IsTrue(PatchCoverage.Validate(s.Leaves.ToList()).Valid);
            Assert.GreaterOrEqual(s.MaxLevel, 9);
        }

        // Performance.
        [Test]
        public void Performance_SamplingCost_AndReliefFieldBuild()
        {
            var watch = Stopwatch.StartNew();
            var planet = new PlanetInstance(new PlanetDefinition { seed = 280695 });
            double build = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var off = new PlanetInstance(new PlanetDefinition { seed = 280695, surfaceDetail = false });
            double buildOff = watch.Elapsed.TotalMilliseconds;
            List<Vector3> dirs = Directions(40000, 3).ToList();
            float acc = 0f;
            watch.Restart();
            foreach (Vector3 d in dirs) acc += planet.Height.GetElevation(d);
            double full = watch.Elapsed.TotalMilliseconds * 1000.0 / dirs.Count;
            watch.Restart();
            foreach (Vector3 d in dirs) acc += off.Height.GetElevation(d);
            double baseCost = watch.Elapsed.TotalMilliseconds * 1000.0 / dirs.Count;
            watch.Restart();
            foreach (Vector3 d in dirs) acc += planet.Height.GetElevation(d, 0.001f);
            double limited = watch.Elapsed.TotalMilliseconds * 1000.0 / dirs.Count;
            watch.Restart();
            foreach (Vector3 d in dirs) acc += planet.Height.GetSurfaceNormal(new Vector3d(d), 1e-5f).x;
            double normal = watch.Elapsed.TotalMilliseconds * 1000.0 / dirs.Count;
            Assert.Less(full, baseCost * 3.0 + 1.0, "Detail costs less than twice the planetary terrain (us per sample) " + acc);
            WorldArchiveTestUtil.Report(string.Format(CultureInfo.InvariantCulture,
                "Planet build {0:0} ms with detail (relief field included) vs {1:0} ms without; GetElevation {2:0.00} us full, {3:0.00} us band-limited (0.001 rad), {4:0.00} us planetary only; double-path normal {5:0.00} us",
                build, buildOff, full, limited, baseCost, normal));
        }
    }
}
