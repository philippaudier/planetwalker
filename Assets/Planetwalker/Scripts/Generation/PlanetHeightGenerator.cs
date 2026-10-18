using System;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// Mesh-independent elevation function of a planet.
    /// </summary>
    /// <remarks>
    /// The generator is an immutable snapshot of a <see cref="PlanetDefinition"/>: editing
    /// the definition afterwards does not affect it. It holds no mutable state, so
    /// <see cref="GetElevation"/> is deterministic, order independent and thread safe.
    /// It is the single source of truth for terrain height, meant to be reused by future
    /// systems (collisions, vegetation, biomes, hydrology, streaming).
    ///
    /// Elevation is dimensionless, relative to the reference radius: the surface point
    /// for direction d is d * radius * (1 + GetElevation(d)).
    ///
    /// V0.2.0C: the canonical elevation is the planetary terrain (<see cref="GetBaseElevation"/>, unchanged
    /// since V0.1) plus the multi-scale surface detail (<see cref="TerrainDetail"/>) when the definition enables
    /// it. <see cref="GetElevation(Vector3)"/> is the full function every consumer that needs the true surface
    /// (patch positions, future collisions) must use; <see cref="GetElevation(Vector3, float)"/> band-limits the
    /// detail to a sample spacing for analysis grids and normals. With surface detail off, every value is
    /// bit-identical to V0.2.0B.
    ///
    /// This is plausible-looking layered noise, not a tectonic or erosion simulation.
    /// </remarks>
    public sealed class PlanetHeightGenerator
    {
        // Noise streams: one independent seed per layer.
        const int ContinentStream = 1;
        // Stream 2 was the V0.1.0 mountain region mask (retired in V0.1.1B).
        // Stream 3 (mountain ridges) is used by MountainSystem.
        const int DetailStream = 4;
        const int WarpStreamX = 5;
        const int WarpStreamY = 6;
        const int WarpStreamZ = 7;
        const int ContinentRegionalStream = 8;
        const int CoastStream = 9;
        const int CoastModulationStream = 10;
        const int AsymmetryStream = 11;
        // Per-planet "character" values (scalars derived from the seed, not noise fields).
        const int CharacterFrequencyStream = 20;
        const int CharacterAsymmetryStream = 21;
        const int CharacterFragmentationStream = 22;
        const int CharacterLandStream = 23;

        const int DetailOctaves = 4;

        // ---- Continental field (V0.1.1A) -------------------------------------------
        // c = (macro + asymmetry + regional) * gain + bias, then coastal detail near c = 0.
        // The coastline is at c = 0; the bimodal profile below turns c into elevation.

        // A. Macro structure: largest landmasses and ocean basins.
        const int MacroOctaves = 3;
        // Planet-scale, single-octave field that unbalances hemispheres (supercontinent
        // vs ocean-dominated side). Its amplitude varies per seed.
        const float AsymmetryFrequencyScale = 0.45f;
        const float MaxAsymmetryAmplitude = 0.35f;
        // B. Regional structure: peninsulas, gulfs, continental extensions.
        const int RegionalOctaves = 2;
        const float RegionalFrequencyScale = 2.7f;
        const float MinRegionalAmplitude = 0.35f;
        const float MaxRegionalAmplitude = 0.75f;
        // C. Coastal detail: bays, capes, small islands. Only applied where |c| is below
        // CoastalInfluenceBand, so continental interiors and abyssal plains stay coherent.
        const int CoastOctaves = 3;
        const float CoastFrequencyScale = 7f;
        const float MaxCoastAmplitude = 0.5f;
        const float CoastModulationFrequencyScale = 2f;

        /// <summary>
        /// Half-width, in continental-field units, of the band around the coastline where
        /// coastal detail is applied. Outside it, coastal complexity has no effect.
        /// </summary>
        public const float CoastalInfluenceBand = 0.3f;

        // Spreads the combined continental noise over roughly [-1, 1].
        const float ContinentNoiseGain = 2.2f;
        // Shifts the field towards the ocean side so that, at sea level 0, roughly a third
        // of the surface is land on average (Earth: ~29%). Per-seed jitter adds variety.
        const float ContinentBias = -0.17f;
        const float LandBiasJitter = 0.07f;
        // Per-seed scale of continentFrequency: fewer large vs more numerous continents.
        const float MinCharacterFrequency = 0.8f;
        const float MaxCharacterFrequency = 1.3f;
        // The tangential part of the 2-octave warp noise has an RMS of ~0.28; this gain
        // normalizes it to ~1 so continentalWarpStrength reads as an RMS angle in radians.
        const float WarpNoiseGain = 3.5f;
        const int WarpOctaves = 2;

        // Continental profile (in units of continentStrength). Coastline is at c = 0.
        const float ShelfStart = -0.35f;   // below: abyssal ocean floor
        const float ShelfEnd = 0.04f;      // above: continental platform
        const float AbyssDepth = -1f;
        const float AbyssVariation = 0.25f;
        const float LandSlope = 0.55f;

        // Mountains are suppressed near and below the coastline (continental field units),
        // so they barely move coastlines and never form chains across ocean basins.
        const float MountainCoastStart = 0.02f;
        const float MountainCoastEnd = 0.25f;
        // Extra surface detail inside range cores (reuses the detail sample).
        const float MountainDetailBoost = 2f;

        readonly int continentSeed;
        readonly int detailSeed;
        readonly int warpSeedX;
        readonly int warpSeedY;
        readonly int warpSeedZ;
        readonly int continentRegionalSeed;
        readonly int coastSeed;
        readonly int coastModulationSeed;
        readonly int asymmetrySeed;

        readonly float macroFrequency;
        readonly float asymmetryAmplitude;
        readonly float regionalAmplitude;
        readonly float landBias;
        readonly float warpStrength;
        readonly float warpFrequency;
        readonly float coastAmplitude;
        readonly float continentStrength;
        readonly float mountainStrength;
        readonly MountainSystem mountains;
        readonly float detailFrequency;
        readonly float detailStrength;
        readonly TerrainDetail surfaceDetail;

        public PlanetHeightGenerator(PlanetDefinition definition)
        {
            PlanetDefinition d = definition.Clone();
            d.Sanitize();

            Seed = d.seed;
            SeaLevel = d.seaLevel;
            continentSeed = PlanetNoise.DeriveSeed(d.seed, ContinentStream);
            detailSeed = PlanetNoise.DeriveSeed(d.seed, DetailStream);
            warpSeedX = PlanetNoise.DeriveSeed(d.seed, WarpStreamX);
            warpSeedY = PlanetNoise.DeriveSeed(d.seed, WarpStreamY);
            warpSeedZ = PlanetNoise.DeriveSeed(d.seed, WarpStreamZ);
            continentRegionalSeed = PlanetNoise.DeriveSeed(d.seed, ContinentRegionalStream);
            coastSeed = PlanetNoise.DeriveSeed(d.seed, CoastStream);
            coastModulationSeed = PlanetNoise.DeriveSeed(d.seed, CoastModulationStream);
            asymmetrySeed = PlanetNoise.DeriveSeed(d.seed, AsymmetryStream);

            // Planet character: a few seed-derived scalars that shift the statistics of
            // the continental field (size, balance, fragmentation, land share) so seeds
            // differ in arrangement, not just in noise phase. No templates involved.
            macroFrequency = d.continentFrequency * Mathf.Lerp(MinCharacterFrequency, MaxCharacterFrequency,
                PlanetNoise.Hash01(d.seed, CharacterFrequencyStream));
            asymmetryAmplitude = MaxAsymmetryAmplitude * PlanetNoise.Hash01(d.seed, CharacterAsymmetryStream);
            regionalAmplitude = Mathf.Lerp(MinRegionalAmplitude, MaxRegionalAmplitude,
                PlanetNoise.Hash01(d.seed, CharacterFragmentationStream));
            landBias = ContinentBias + LandBiasJitter * (2f * PlanetNoise.Hash01(d.seed, CharacterLandStream) - 1f);

            warpStrength = d.continentalWarpStrength;
            warpFrequency = d.continentalWarpFrequency;
            coastAmplitude = MaxCoastAmplitude * d.coastalComplexity;
            continentStrength = d.continentStrength;
            mountainStrength = d.mountainStrength;
            detailFrequency = d.detailFrequency;
            detailStrength = d.detailStrength;

            // Built last: province placement reads the (already configured) continental field.
            mountains = new MountainSystem(d, this);

            // Surface detail (V0.2.0C): reads the finished planetary terrain (relief field of e0).
            if (d.surfaceDetail)
            {
                var profile = new TerrainDetailProfile(PlanetScale.Of(d), d.detailFrequency, d.detailStrength, d.regionalDetail, d.localDetail, d.microDetail);
                if (profile.OctaveCount > 0)
                {
                    // Regional landforms (V0.2.0G.1): strength 0 builds none (the terrain is then exactly V0.2.0F.3's).
                    RegionalLandforms landforms = null;
                    if (d.regionalLandforms > 0f)
                    {
                        // The ranges' drainage network (V0.2.0G.1.1), from e0 alone: built once, then a lookup.
                        PlanetScale scale = PlanetScale.Of(d);
                        var valleys = new ValleyNetwork(dir => { SampleBase(dir, out TerrainBaseSample s); return s; },
                            d.seaLevel, scale.RadiusMeters, d.seed, RegionalLandforms.MaxValleyDepthMeters); // depths scaled by the strength in the landforms
                        landforms = new RegionalLandforms(scale, d.seed, d.regionalLandforms, d.seaLevel, valleys);
                    }
                    // Erosion stage (V0.2.0G.2A): the foundation, zero displacement; strength 0 builds none.
                    ErosionSampler erosion = d.erosionStrength > 0f
                        ? new ErosionSampler(new ErosionContext(PlanetScale.Of(d), d.seed, d.seaLevel, d.erosionStrength, landforms))
                        : null;
                    surfaceDetail = new TerrainDetail(profile, new TerrainReliefField(GetBaseElevation), d.seed, d.seaLevel, landforms, erosion);
                }
            }
        }

        /// <summary>The surface detail layers (V0.2.0C), or null when the definition has them off.</summary>
        public TerrainDetail SurfaceDetail => surfaceDetail;

        public bool HasSurfaceDetail => surfaceDetail != null;

        /// <summary>Mountain provinces and ranges of this planet (read-only descriptors).</summary>
        public MountainSystem Mountains => mountains;

        public int Seed { get; }

        /// <summary>Ocean surface elevation, in the same units as <see cref="GetElevation"/>.</summary>
        public float SeaLevel { get; }

        /// <summary>
        /// Signed elevation of the surface in the given direction, as a fraction of the
        /// reference radius: the canonical, full-resolution terrain. <paramref name="normalizedDirection"/>
        /// must be unit length.
        /// </summary>
        public float GetElevation(Vector3 normalizedDirection)
        {
            if (surfaceDetail == null)
            {
                return GetBaseElevation(normalizedDirection);
            }
            float e = SampleBase(normalizedDirection, out TerrainBaseSample context);
            return e + surfaceDetail.Evaluate(normalizedDirection, context, 0f);
        }

        /// <summary>
        /// Elevation band-limited to a sample spacing (radians): surface-detail octaves finer than the spacing
        /// can resolve fade out (see <see cref="TerrainDetailProfile.Weight"/>). For analysis grids, legacy
        /// meshes' normals and anything that samples the terrain at a fixed resolution. Spacing 0 = full.
        /// The planetary terrain itself is never band-limited.
        /// </summary>
        public float GetElevation(Vector3 normalizedDirection, float sampleSpacing)
        {
            if (surfaceDetail == null)
            {
                return GetBaseElevation(normalizedDirection);
            }
            float e = SampleBase(normalizedDirection, out TerrainBaseSample context);
            return e + surfaceDetail.Evaluate(normalizedDirection, context, sampleSpacing);
        }

        /// <summary>
        /// Canonical elevation at a double-precision direction (V0.2.0C): the planetary terrain at its float
        /// rounding, the surface detail with full precision. Adaptive patch vertices use this overload.
        /// </summary>
        public float GetElevation(Vector3d direction)
        {
            return GetElevation(direction, 0f);
        }

        /// <summary>
        /// Band-limited elevation at a double-precision direction (see <see cref="GetElevation(Vector3, float)"/>).
        /// Below <see cref="FineSpacing"/> the planetary terrain comes from <see cref="FineBaseElevation"/>: its
        /// single-precision evaluation carries a few centimeters of rounding noise from sample to sample, which a
        /// sub-meter sampling grid turns into visible grain (0.4 m steps at Earth scale: slopes of 0.1 from nothing).
        /// A pure function of (direction, spacing): the same everywhere, whatever patch or thread asks.
        /// </summary>
        public float GetElevation(Vector3d direction, float sampleSpacing)
        {
            return GetElevation(direction, sampleSpacing, out _);
        }

        /// <summary>
        /// <see cref="GetElevation(Vector3d, float)"/>, also returning the planetary context of the sample (continental
        /// field, mountain envelope and influence, ridge anisotropy; V0.2.0E surface geology): the same evaluation, no
        /// extra cost. The context's elevation is the planetary terrain e0 (without the surface detail).
        /// </summary>
        public float GetElevation(Vector3d direction, float sampleSpacing, out TerrainBaseSample context)
        {
            bool fine = sampleSpacing == 0f || sampleSpacing < FineSpacing;
            if (fine)
            {
                // Everything from the lattice: the smooth e0 and the (slowly varying) context of the nearest sample.
                float baseElevation = FineBaseElevation(direction, out context);
                context.Elevation = baseElevation;
                if (surfaceDetail == null)
                {
                    return baseElevation;
                }
                return baseElevation + surfaceDetail.Evaluate(direction, context, sampleSpacing);
            }
            Vector3 dir = direction.ToVector3();
            float e = SampleBase(dir, out context); // GetBaseElevation is SampleBase
            if (surfaceDetail == null)
            {
                return e;
            }
            return e + surfaceDetail.Evaluate(direction, context, sampleSpacing);
        }

        /// <summary>
        /// Sample spacings (radians) below which <see cref="FineBaseElevation"/> replaces the direct planetary
        /// terrain: the fine lattice spacing itself, so every vertex of a coarser level (a lattice point, where the
        /// two agree) and every finer vertex share one canonical surface.
        /// </summary>
        public const float FineSpacing = Mathf.PI * 0.5f / FineLatticeSize;

        /// <summary>Cells per face edge of the fine base lattice (2^16: 150 m at Earth scale, far below the planetary terrain's ~200 km features).</summary>
        public const int FineLatticeSize = 1 << 16;

        [ThreadStatic] static FineLatticeCache fineCache;

        /// <summary>
        /// The planetary terrain, smooth at any scale (V0.2.0C): bicubic (Catmull-Rom) interpolation of e0 sampled
        /// on the global face lattice of <see cref="FineLatticeSize"/> cells. e0 has no feature below ~200 km, so the
        /// interpolation is exact to a meter or less away from mountain crest lines (where the ridged noise has a
        /// slope discontinuity that the cubic rounds over one cell: up to ~20 m at Earth scale), while the
        /// per-sample rounding noise of the float evaluation becomes a smooth 150 m ripple of a few centimeters. At a
        /// lattice point the value is the lattice sample itself (one evaluation: coarse levels cost nothing extra).
        /// Continuous across face edges (edge lattice points are shared; a stencil extends past the edge along the
        /// face's own parametrization). Lattice samples are cached per thread.
        /// </summary>
        public float FineBaseElevation(Vector3d direction)
        {
            return FineBaseElevation(direction, out _);
        }

        /// <summary>
        /// <see cref="FineBaseElevation(Vector3d)"/>, also returning the base context (continental field, mountain
        /// envelope, ridge anisotropy) interpolated bilinearly from the cell's four lattice samples: those fields
        /// vary over hundreds of kilometers, so the interpolation is exact for every practical purpose, continuous
        /// across cells (the anisotropy shifts the ridge noise domain: it must not jump) and spares a direct
        /// evaluation per sample.
        /// </summary>
        public float FineBaseElevation(Vector3d direction, out TerrainBaseSample context)
        {
            int face = CubeSphereGenerator.DirectionToFaceCoordinates(direction, out double a, out double b);
            double x = (a + 1.0) * 0.5 * FineLatticeSize, y = (b + 1.0) * 0.5 * FineLatticeSize;
            int ci = (int)Math.Floor(x + 1e-7), cj = (int)Math.Floor(y + 1e-7);
            double tx = x - ci, ty = y - cj;
            fineCache ??= new FineLatticeCache();
            ref readonly TerrainBaseSample s00 = ref fineCache.Get(this, face, ci, cj);
            if (Math.Abs(tx) < 1e-6 && Math.Abs(ty) < 1e-6)
            {
                context = s00;
                return s00.Elevation; // a lattice point: the sample itself
            }
            ref readonly TerrainBaseSample s10 = ref fineCache.Get(this, face, ci + 1, cj);
            ref readonly TerrainBaseSample s01 = ref fineCache.Get(this, face, ci, cj + 1);
            ref readonly TerrainBaseSample s11 = ref fineCache.Get(this, face, ci + 1, cj + 1);
            float fx = (float)tx, fy = (float)ty;
            float w00 = (1f - fx) * (1f - fy), w10 = fx * (1f - fy), w01 = (1f - fx) * fy, w11 = fx * fy;
            context = new TerrainBaseSample
            {
                Continental = s00.Continental * w00 + s10.Continental * w10 + s01.Continental * w01 + s11.Continental * w11,
                Core = s00.Core * w00 + s10.Core * w10 + s01.Core * w01 + s11.Core * w11,
                Influence = s00.Influence * w00 + s10.Influence * w10 + s01.Influence * w01 + s11.Influence * w11,
                Anisotropy = s00.Anisotropy * w00 + s10.Anisotropy * w10 + s01.Anisotropy * w01 + s11.Anisotropy * w11,
                Undulation = s00.Undulation * w00 + s10.Undulation * w10 + s01.Undulation * w01 + s11.Undulation * w11,
            };
            // The blend runs in double: in float its 5 p1 / 4 p2 terms would round by ~10 cm at Earth scale,
            // re-introducing the very grain the lattice removes. The samples stay the float lattice values.
            Span<double> rows = stackalloc double[4];
            for (int r = 0; r < 4; r++)
            {
                int j = cj - 1 + r;
                rows[r] = CatmullRom(fineCache.Get(this, face, ci - 1, j).Elevation, fineCache.Get(this, face, ci, j).Elevation,
                    fineCache.Get(this, face, ci + 1, j).Elevation, fineCache.Get(this, face, ci + 2, j).Elevation, tx);
            }
            return (float)CatmullRom(rows[0], rows[1], rows[2], rows[3], ty);
        }

        static double CatmullRom(double p0, double p1, double p2, double p3, double t)
        {
            double t2 = t * t, t3 = t2 * t;
            return 0.5 * ((2.0 * p1) + (-p0 + p2) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t2 + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t3);
        }

        /// <summary>Per-thread direct-mapped cache of fine-lattice base samples (keyed by generator, face, i, j).</summary>
        sealed class FineLatticeCache
        {
            const int Slots = 4096;
            readonly PlanetHeightGenerator[] owners = new PlanetHeightGenerator[Slots];
            readonly long[] keys = new long[Slots];
            readonly TerrainBaseSample[] values = new TerrainBaseSample[Slots];

            public ref readonly TerrainBaseSample Get(PlanetHeightGenerator generator, int face, int i, int j)
            {
                // Lattice indices may step one past the face (stencils at an edge): the lattice direction continues
                // the face's parametrization there, a valid point of the sphere.
                long key = ((long)face << 40) | ((long)(i + 1) << 20) | (uint)(j + 1);
                int slot = (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 52) & (Slots - 1);
                if (owners[slot] != generator || keys[slot] != key)
                {
                    generator.SampleBase(CubeSphereGenerator.GetSphereDirection(face, i, j, FineLatticeSize), out values[slot]);
                    owners[slot] = generator;
                    keys[slot] = key;
                }
                return ref values[slot];
            }
        }

        /// <summary>
        /// Surface normal at a double-precision direction, band-limited to the sampling angle (V0.2.0C): the
        /// finite differences are taken in double on the smooth fine base, so normals stay clean at sub-meter spacings.
        /// </summary>
        public Vector3 GetSurfaceNormal(Vector3d direction, float sampleAngle)
        {
            Vector3 dir = direction.ToVector3();
            Vector3 reference = Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.right;
            Vector3d tangent = Vector3d.Cross(direction, new Vector3d(reference)).Normalized;
            Vector3d bitangent = Vector3d.Cross(direction, tangent);
            Vector3d dirA = (direction + tangent * sampleAngle).Normalized;
            Vector3d dirB = (direction + bitangent * sampleAngle).Normalized;
            Vector3d center = direction * (1.0 + GetElevation(direction, sampleAngle));
            Vector3d a = dirA * (1.0 + GetElevation(dirA, sampleAngle)) - center;
            Vector3d b = dirB * (1.0 + GetElevation(dirB, sampleAngle)) - center;
            Vector3d normal = Vector3d.Cross(a, b);
            double length = normal.Magnitude;
            return length > 1e-24 ? (normal / length).ToVector3() : dir;
        }

        /// <summary>
        /// Elevation for relief bounds (V0.2.0G.1): the detail octaves band-limited to the sample spacing (radians), the
        /// regional landforms complete. Landforms narrower than a coarse spacing (valleys, plateau scarps) fade in
        /// band-limited samples, yet the patches sample them: bounds must contain them without widening every margin.
        /// </summary>
        public float GetBoundsElevation(Vector3 normalizedDirection, float sampleSpacing)
        {
            if (surfaceDetail == null)
            {
                return GetBaseElevation(normalizedDirection);
            }
            float e = SampleBase(normalizedDirection, out TerrainBaseSample context);
            surfaceDetail.Evaluate(new Vector3d(normalizedDirection), normalizedDirection, context, sampleSpacing, 0f, out TerrainDetailSample sample);
            return e + sample.Total;
        }

        /// <summary>Base elevation and the detail of each band at a direction (diagnostics; zero bands without detail).</summary>
        public float SampleLayers(Vector3 normalizedDirection, out TerrainDetailSample detail)
        {
            if (surfaceDetail == null)
            {
                detail = default;
                return GetBaseElevation(normalizedDirection);
            }
            float e = SampleBase(normalizedDirection, out TerrainBaseSample context);
            surfaceDetail.Evaluate(normalizedDirection, context, 0f, out detail);
            return e;
        }

        /// <summary>
        /// Planetary terrain only (continents, mountain systems, V0.1 detail): the V0.2.0B elevation function,
        /// bit for bit. The surface detail is added on top of it.
        /// </summary>
        public float GetBaseElevation(Vector3 normalizedDirection)
        {
            return SampleBase(normalizedDirection, out _);
        }

        /// <summary>The planetary terrain and the context the surface detail reads (one evaluation).</summary>
        public float SampleBase(Vector3 normalizedDirection, out TerrainBaseSample context)
        {
            Vector3 dir = normalizedDirection;
            context = default;

            // A. Continental shape: a bimodal profile (abyssal basins vs continental
            // platforms joined by a shelf/slope) rather than raw noise, so that land and
            // ocean read as distinct domains.
            float c = GetContinentalField(dir);
            float shelf = SmoothStep(ShelfStart, ShelfEnd, c);
            float oceanFloor = AbyssDepth + AbyssVariation * c;
            float landHeight = LandSlope * c;
            float elevation = Mathf.Lerp(oceanFloor, landHeight, shelf) * continentStrength;

            // B. Mountain systems: provinces and ranges define where, ridges define how.
            // Added on top of the continental relief; the continental field is untouched.
            float landMask = SmoothStep(-0.05f, 0.2f, c);
            float mountainCore = 0f;
            if (mountainStrength > 0f)
            {
                float landFactor = SmoothStep(MountainCoastStart, MountainCoastEnd, c);
                if (landFactor > 0f)
                {
                    elevation += mountainStrength * landFactor * mountains.Evaluate(dir, out mountainCore, out float influence, out Vector3 anisotropy);
                    mountainCore *= landFactor;
                    context.Influence = landFactor * influence;
                    context.Anisotropy = anisotropy;
                }
            }

            // C. Surface detail, softened under water, stronger inside mountain ranges.
            if (detailStrength > 0f)
            {
                float detail = PlanetNoise.Fractal(dir * detailFrequency, detailSeed, DetailOctaves);
                float undulation = detailStrength * detail * Mathf.Lerp(0.5f, 1f, landMask) * (1f + MountainDetailBoost * mountainCore);
                elevation += undulation;
                context.Undulation = undulation;
            }

            context.Elevation = elevation;
            context.Continental = c;
            context.Core = mountainCore;
            return elevation;
        }

        /// <summary>
        /// Signed continental field for a unit direction: negative over ocean basins,
        /// positive over continents, coastline at 0 (before sea level is applied).
        /// Mountains and detail are not included. Deterministic and mesh independent.
        /// </summary>
        public float GetContinentalField(Vector3 normalizedDirection)
        {
            // Domain warp: the continental layers are sampled at a displaced direction, so
            // outlines bend into peninsulas and gulfs. Mountains and detail are not warped.
            Vector3 p = warpStrength > 0f ? Warp(normalizedDirection) : normalizedDirection;

            float macro = PlanetNoise.Fractal(p * macroFrequency, continentSeed, MacroOctaves);
            float asymmetry = asymmetryAmplitude == 0f ? 0f : asymmetryAmplitude * PlanetNoise.Gradient(
                p.x * AsymmetryFrequencyScale, p.y * AsymmetryFrequencyScale, p.z * AsymmetryFrequencyScale, asymmetrySeed);
            float regional = PlanetNoise.Fractal(p * (macroFrequency * RegionalFrequencyScale), continentRegionalSeed, RegionalOctaves);
            float c = (macro + asymmetry + regionalAmplitude * regional) * ContinentNoiseGain + landBias;

            if (coastAmplitude > 0f)
            {
                // Smooth bump: 1 on the coastline, 0 beyond the band (C1 continuous).
                float proximity = 1f - SmoothStep(0f, CoastalInfluenceBand, Mathf.Abs(c));
                if (proximity > 0f)
                {
                    // Coastal roughness varies along coasts, so some shores stay smooth.
                    float modulation = 0.6f + 0.6f * PlanetNoise.Gradient(
                        p.x * (macroFrequency * CoastModulationFrequencyScale),
                        p.y * (macroFrequency * CoastModulationFrequencyScale),
                        p.z * (macroFrequency * CoastModulationFrequencyScale), coastModulationSeed);
                    float coast = PlanetNoise.Fractal(p * (macroFrequency * CoastFrequencyScale), coastSeed, CoastOctaves);
                    c += coastAmplitude * proximity * Mathf.Clamp01(modulation) * coast * ContinentNoiseGain;
                }
            }
            return c;
        }

        /// <summary>
        /// Mountain influence in [0, 1] as it applies to the terrain (coastal suppression
        /// included): 0 = none, mid = foothills / province highlands, 1 = range cores.
        /// </summary>
        public float GetMountainInfluence(Vector3 normalizedDirection)
        {
            float landFactor = SmoothStep(MountainCoastStart, MountainCoastEnd, GetContinentalField(normalizedDirection));
            return landFactor > 0f ? landFactor * mountains.GetInfluence(normalizedDirection) : 0f;
        }

        /// <summary>
        /// Continental mask in [0, 1] for diagnostics: 0 = abyssal ocean, ~0.5 = coastline,
        /// 1 = continental interior.
        /// </summary>
        public float GetContinentalMask(Vector3 normalizedDirection)
        {
            return SmoothStep(ShelfStart, -ShelfStart, GetContinentalField(normalizedDirection));
        }

        /// <summary>
        /// Tangent-space domain warp. Three independent noise channels form a 3D vector
        /// that depends only on the direction (no cube-face or UV dependence); its radial
        /// component is removed so the warp slides the sampling point along the sphere.
        /// </summary>
        Vector3 Warp(Vector3 dir)
        {
            Vector3 w = PlanetNoise.FractalVector(dir * warpFrequency, warpSeedX, warpSeedY, warpSeedZ, WarpOctaves);
            w -= dir * Vector3.Dot(w, dir);
            return (dir + w * (warpStrength * WarpNoiseGain)).normalized;
        }

        /// <summary>
        /// Unit surface normal for a unit direction, derived from neighbouring elevation
        /// samples. Depends only on the direction and the sampling angle (in radians),
        /// never on a mesh, so normals are identical across cube faces. With surface detail
        /// (V0.2.0C) the samples are band-limited to the sampling angle: the normal is that of
        /// the relief a mesh of that spacing resolves (no aliasing grain), consistent on both
        /// sides of any boundary sampled at the same angle.
        /// </summary>
        public Vector3 GetSurfaceNormal(Vector3 normalizedDirection, float sampleAngle)
        {
            return GetSurfaceNormal(normalizedDirection, GetElevation(normalizedDirection, sampleAngle), sampleAngle);
        }

        /// <summary>
        /// Same as <see cref="GetSurfaceNormal(Vector3, float)"/>, reusing an elevation the
        /// caller already sampled at <paramref name="normalizedDirection"/> (2 extra samples;
        /// 3 with surface detail, whose band-limited center differs from the full elevation).
        /// </summary>
        public Vector3 GetSurfaceNormal(Vector3 normalizedDirection, float elevation, float sampleAngle)
        {
            Vector3 dir = normalizedDirection;
            if (surfaceDetail != null)
            {
                elevation = GetElevation(dir, sampleAngle);
            }
            Vector3 reference = Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.right;
            Vector3 tangent = Vector3.Cross(dir, reference).normalized;
            Vector3 bitangent = Vector3.Cross(dir, tangent);

            Vector3 dirA = (dir + tangent * sampleAngle).normalized;
            Vector3 dirB = (dir + bitangent * sampleAngle).normalized;

            Vector3 center = dir * (1f + elevation);
            Vector3 a = dirA * (1f + GetElevation(dirA, sampleAngle)) - center;
            Vector3 b = dirB * (1f + GetElevation(dirB, sampleAngle)) - center;

            // Cross(tangent, bitangent) == dir, so this points outward.
            Vector3 normal = Vector3.Cross(a, b);
            float length = normal.magnitude;
            return length > 1e-12f ? normal / length : dir;
        }

        static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
