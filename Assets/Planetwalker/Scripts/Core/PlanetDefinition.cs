using System;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// Configuration of a procedural planet. Pure data: no generation or rendering logic.
    /// </summary>
    /// <remarks>
    /// Elevation convention: every elevation-related value (strengths, sea level) is
    /// dimensionless and expressed as a fraction of the reference radius. An elevation of
    /// 0.01 means "1% of the radius above the reference sphere". The terrain shape is
    /// therefore independent of <see cref="radius"/>, which is only a rendering scale.
    /// Physical units (e.g. a radius in kilometres) can later be introduced next to the
    /// rendering radius without changing the generator.
    ///
    /// Frequencies are expressed in noise cycles per unit of sphere direction
    /// (the noise is sampled on the unit sphere), so they are also scale independent.
    /// </remarks>
    [Serializable]
    public sealed class PlanetDefinition
    {
        public const float MinRadius = 1f;
        public const float MaxRadius = 10000f;
        public const int MinResolution = 8;
        public const int MaxResolution = 256;
        public const float MinContinentFrequency = 0.25f;
        public const float MaxContinentFrequency = 4f;
        public const float MaxContinentStrength = 0.1f;
        public const float MinMountainFrequency = 1f;
        public const float MaxMountainFrequency = 20f;
        public const float MaxMountainStrength = 0.1f;
        public const float MinDetailFrequency = 4f;
        public const float MaxDetailFrequency = 80f;
        public const float MaxDetailStrength = 0.02f;
        public const float MinSeaLevel = -0.05f;
        public const float MaxSeaLevel = 0.05f;
        public const float MaxContinentalWarpStrength = 0.5f;
        public const float MinContinentalWarpFrequency = 0.5f;
        public const float MinMountainRangeLength = 0.5f;
        public const float MaxMountainRangeLength = 2f;
        public const float MaxContinentalWarpFrequency = 4f;

        [Tooltip("Master seed. Same seed + same parameters = same planet.")]
        public int seed = 1337;

        [Tooltip("Reference radius in Unity rendering units. Range [1, 10000].")]
        [Range(MinRadius, MaxRadius)]
        public float radius = 100f;

        [Tooltip("Subdivisions per cube face edge (64 => 65 vertices per edge). Range [8, 256].")]
        [Range(MinResolution, MaxResolution)]
        public int resolution = 64;

        [Tooltip("Frequency of the continental noise. Low values = few large continents. Range [0.25, 4].")]
        [Range(MinContinentFrequency, MaxContinentFrequency)]
        public float continentFrequency = 1.1f;

        [Tooltip("Height of continents / depth of ocean basins, as a fraction of the radius. Range [0, 0.1].")]
        [Range(0f, MaxContinentStrength)]
        public float continentStrength = 0.025f;

        [Tooltip("Frequency of the ridge noise inside mountain ranges. Range [1, 20].")]
        [Range(MinMountainFrequency, MaxMountainFrequency)]
        public float mountainFrequency = 5f;

        // Mountain heights are artistic relative elevations (fraction of the radius, strongly
        // exaggerated for readability from orbit), not physical kilometres.
        [Tooltip("Maximum mountain height, as a fraction of the radius (artistic, not physical km). "
            + "0 removes mountains. Range [0, 0.1].")]
        [Range(0f, MaxMountainStrength)]
        public float mountainStrength = 0.045f;

        [Tooltip("Frequency of the small-scale surface detail noise. Range [4, 80].")]
        [Range(MinDetailFrequency, MaxDetailFrequency)]
        public float detailFrequency = 24f;

        [Tooltip("Amplitude of the surface detail noise, as a fraction of the radius. Range [0, 0.02].")]
        [Range(0f, MaxDetailStrength)]
        public float detailStrength = 0.003f;

        [Tooltip("Ocean surface elevation, as a fraction of the radius. 0 = reference sphere. Range [-0.05, 0.05].")]
        [Range(MinSeaLevel, MaxSeaLevel)]
        public float seaLevel = 0f;

        // Continental shaping (V0.1.1A). New fields keep their defaults when an older
        // serialized definition is loaded.

        [Tooltip("Domain warp of the continental field: typical angular displacement of the sampling "
            + "direction, in radians. 0 = no warp. Bends outlines into peninsulas and gulfs; above ~0.3 "
            + "outlines start folding into swirls. Range [0, 0.5].")]
        [Range(0f, MaxContinentalWarpStrength)]
        public float continentalWarpStrength = 0.2f;

        [Tooltip("Frequency of the warp noise. Low = broad bends, high = tighter swirls. Range [0.5, 4].")]
        [Range(MinContinentalWarpFrequency, MaxContinentalWarpFrequency)]
        public float continentalWarpFrequency = 1.1f;

        [Tooltip("Small-scale shoreline irregularity (bays, capes, coastal islands), applied only near "
            + "coastlines. 0 = smooth coasts. Range [0, 1].")]
        [Range(0f, 1f)]
        public float coastalComplexity = 0.5f;

        // Mountain systems (V0.1.1B). New fields keep their defaults when an older
        // serialized definition is loaded.

        [Tooltip("How many mountain provinces and ranges the planet tends to have. 0 = no ranges "
            + "(mostly plains). The actual count also varies per seed. Range [0, 1].")]
        [Range(0f, 1f)]
        public float mountainRangeCoverage = 0.5f;

        [Tooltip("Advanced: length multiplier of mountain ranges and extent of provinces. Range [0.5, 2].")]
        [Range(MinMountainRangeLength, MaxMountainRangeLength)]
        public float mountainRangeLength = 1f;

        [Tooltip("Local relief character inside ranges: 0 = rounded massive mountains, 1 = sharp "
            + "crests and deeper valleys. Does not move ranges. Range [0, 1].")]
        [Range(0f, 1f)]
        public float mountainRuggedness = 0.5f;

        public PlanetDefinition Clone()
        {
            return (PlanetDefinition)MemberwiseClone();
        }

        /// <summary>
        /// True when both definitions produce the same normalized elevation field. Radius and
        /// mesh resolution are rendering choices and are ignored. Every new terrain-affecting
        /// field must be added here, or terrain-derived caches (hydrology) would go stale.
        /// </summary>
        public bool TerrainEquals(PlanetDefinition other)
        {
            return other != null
                && seed == other.seed
                && continentFrequency == other.continentFrequency
                && continentStrength == other.continentStrength
                && mountainFrequency == other.mountainFrequency
                && mountainStrength == other.mountainStrength
                && detailFrequency == other.detailFrequency
                && detailStrength == other.detailStrength
                && seaLevel == other.seaLevel
                && continentalWarpStrength == other.continentalWarpStrength
                && continentalWarpFrequency == other.continentalWarpFrequency
                && coastalComplexity == other.coastalComplexity
                && mountainRangeCoverage == other.mountainRangeCoverage
                && mountainRangeLength == other.mountainRangeLength
                && mountainRuggedness == other.mountainRuggedness;
        }

        /// <summary>Clamps every parameter into its documented range.</summary>
        public void Sanitize()
        {
            radius = Mathf.Clamp(radius, MinRadius, MaxRadius);
            resolution = Mathf.Clamp(resolution, MinResolution, MaxResolution);
            continentFrequency = Mathf.Clamp(continentFrequency, MinContinentFrequency, MaxContinentFrequency);
            continentStrength = Mathf.Clamp(continentStrength, 0f, MaxContinentStrength);
            mountainFrequency = Mathf.Clamp(mountainFrequency, MinMountainFrequency, MaxMountainFrequency);
            mountainStrength = Mathf.Clamp(mountainStrength, 0f, MaxMountainStrength);
            detailFrequency = Mathf.Clamp(detailFrequency, MinDetailFrequency, MaxDetailFrequency);
            detailStrength = Mathf.Clamp(detailStrength, 0f, MaxDetailStrength);
            seaLevel = Mathf.Clamp(seaLevel, MinSeaLevel, MaxSeaLevel);
            continentalWarpStrength = Mathf.Clamp(continentalWarpStrength, 0f, MaxContinentalWarpStrength);
            continentalWarpFrequency = Mathf.Clamp(continentalWarpFrequency, MinContinentalWarpFrequency, MaxContinentalWarpFrequency);
            coastalComplexity = Mathf.Clamp01(coastalComplexity);
            mountainRangeCoverage = Mathf.Clamp01(mountainRangeCoverage);
            mountainRangeLength = Mathf.Clamp(mountainRangeLength, MinMountainRangeLength, MaxMountainRangeLength);
            mountainRuggedness = Mathf.Clamp01(mountainRuggedness);
        }
    }
}
