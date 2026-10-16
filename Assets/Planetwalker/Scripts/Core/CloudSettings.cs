using System;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// Procedural cloud systems (V0.1.2C). Rendering only: nothing here regenerates terrain, hydrology,
    /// climate or biomes. The weather seed is independent of the planet seed, so the same geography can
    /// carry radically different skies (with Coverage, Weather Systems and Convection).
    /// </summary>
    [Serializable]
    public sealed class CloudSettings
    {
        [Tooltip("Cloud layer over the Natural Planet view. Off: the V0.1.2B look.")]
        public bool showClouds = true;

        [Tooltip("Fraction of the sky covered by clouds (area). Range [0, 1].")]
        [Range(0f, 1f)]
        public float coverage = 0.45f;

        [Tooltip("Cloud opacity: thin veils (0) to thick decks (1). Range [0, 1].")]
        [Range(0f, 1f)]
        public float density = 0.5f;

        [Tooltip("Small-scale structure: smooth sheets (0) to broken edges, holes and fine texture (1). Range [0, 1].")]
        [Range(0f, 1f)]
        public float detail = 0.5f;

        [Tooltip("Weight of the weather systems (cyclones, comma clouds, frontal arcs). Range [0, 1].")]
        [Range(0f, 1f)]
        public float weatherStrength = 0.5f;

        [Tooltip("Tropical convection: warm humid regions build cloud clusters. Range [0, 1].")]
        [Range(0f, 1f)]
        public float convection = 0.5f;

        [Tooltip("Darkening of the sunlit surface under the clouds. Range [0, 1].")]
        [Range(0f, 1f)]
        public float shadowStrength = 0.25f;

        [Tooltip("Cloud layer height above the ocean, fraction of the ocean radius (rendering only). Range [0.002, 0.02].")]
        [Range(MinAltitude, MaxAltitude)]
        public float altitude = 0.006f;

        [Tooltip("Slow deterministic cloud motion along the prevailing zonal winds. Off by default (reproducible screenshots).")]
        public bool motion;

        [Tooltip("Weather seed: 0 = derived from the planet seed. Any other value gives another sky over the same geography.")]
        public int weatherSeed;

        public const float MinAltitude = 0.002f;
        public const float MaxAltitude = 0.02f;

        public void Sanitize()
        {
            coverage = Mathf.Clamp01(coverage);
            density = Mathf.Clamp01(density);
            detail = Mathf.Clamp01(detail);
            weatherStrength = Mathf.Clamp01(weatherStrength);
            convection = Mathf.Clamp01(convection);
            shadowStrength = Mathf.Clamp01(shadowStrength);
            altitude = Mathf.Clamp(altitude, MinAltitude, MaxAltitude);
        }

        public CloudSettings Clone()
        {
            return (CloudSettings)MemberwiseClone();
        }

        /// <summary>Effective weather seed for a planet seed.</summary>
        public int EffectiveWeatherSeed(int planetSeed)
        {
            return weatherSeed != 0 ? weatherSeed : planetSeed;
        }

        public bool AppearanceEquals(CloudSettings other)
        {
            return other != null && showClouds == other.showClouds && coverage == other.coverage && density == other.density
                && detail == other.detail && weatherStrength == other.weatherStrength && convection == other.convection
                && shadowStrength == other.shadowStrength && altitude == other.altitude && motion == other.motion
                && weatherSeed == other.weatherSeed;
        }
    }
}
