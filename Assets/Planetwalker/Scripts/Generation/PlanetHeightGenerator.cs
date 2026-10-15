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
        }

        /// <summary>Mountain provinces and ranges of this planet (read-only descriptors).</summary>
        public MountainSystem Mountains => mountains;

        public int Seed { get; }

        /// <summary>Ocean surface elevation, in the same units as <see cref="GetElevation"/>.</summary>
        public float SeaLevel { get; }

        /// <summary>
        /// Signed elevation of the surface in the given direction, as a fraction of the
        /// reference radius. <paramref name="normalizedDirection"/> must be unit length.
        /// </summary>
        public float GetElevation(Vector3 normalizedDirection)
        {
            Vector3 dir = normalizedDirection;

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
                    elevation += mountainStrength * landFactor * mountains.Evaluate(dir, out mountainCore);
                    mountainCore *= landFactor;
                }
            }

            // C. Surface detail, softened under water, stronger inside mountain ranges.
            if (detailStrength > 0f)
            {
                float detail = PlanetNoise.Fractal(dir * detailFrequency, detailSeed, DetailOctaves);
                elevation += detailStrength * detail * Mathf.Lerp(0.5f, 1f, landMask) * (1f + MountainDetailBoost * mountainCore);
            }

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
        /// never on a mesh, so normals are identical across cube faces.
        /// </summary>
        public Vector3 GetSurfaceNormal(Vector3 normalizedDirection, float sampleAngle)
        {
            return GetSurfaceNormal(normalizedDirection, GetElevation(normalizedDirection), sampleAngle);
        }

        /// <summary>
        /// Same as <see cref="GetSurfaceNormal(Vector3, float)"/>, reusing an elevation the
        /// caller already sampled at <paramref name="normalizedDirection"/> (2 extra samples).
        /// </summary>
        public Vector3 GetSurfaceNormal(Vector3 normalizedDirection, float elevation, float sampleAngle)
        {
            Vector3 dir = normalizedDirection;
            Vector3 reference = Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.right;
            Vector3 tangent = Vector3.Cross(dir, reference).normalized;
            Vector3 bitangent = Vector3.Cross(dir, tangent);

            Vector3 dirA = (dir + tangent * sampleAngle).normalized;
            Vector3 dirB = (dir + bitangent * sampleAngle).normalized;

            Vector3 center = dir * (1f + elevation);
            Vector3 a = dirA * (1f + GetElevation(dirA)) - center;
            Vector3 b = dirB * (1f + GetElevation(dirB)) - center;

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
