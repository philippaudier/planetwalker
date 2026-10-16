using System;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// Climate analysis settings (V0.1.1E-A). Not a planet property: the climate is a downstream
    /// analysis that reads the terrain and never changes it.
    /// </summary>
    /// <remarks>
    /// Units: temperatures in degrees Celsius (K = C + 273.15), physical elevations in kilometres,
    /// distances in kilometres along the sphere (the planet is mapped onto <see cref="planetRadiusKm"/>).
    /// Moisture and precipitation are normalized indices (see <c>ClimateResult</c>), not mm/year.
    /// </remarks>
    [Serializable]
    public sealed class ClimateSettings
    {
        public const int LowResolution = 32;
        public const int DefaultResolution = 64;
        public const int HighResolution = 128;
        public const float MaxAxialTilt = 45f;
        public const float MinElevationScale = 0.2f;
        public const float MaxElevationScale = 3f;

        [Tooltip("Cells per cube face edge of the climate grid: 32, 64 (default) or 128. Independent of the "
            + "terrain mesh and hydrology resolutions.")]
        public int resolution = DefaultResolution;

        [Tooltip("Planetary rotation axis (normalized internally). Latitude = asin(dot(direction, axis)).")]
        public Vector3 rotationAxis = Vector3.up;

        [Tooltip("Obliquity of the rotation axis relative to the orbit, in degrees. Drives the annual-mean "
            + "insolation (circular orbit, fixed luminosity). Range [0, 45].")]
        [Range(0f, MaxAxialTilt)]
        public float axialTilt = 23.5f;

        [Tooltip("Climate elevation scale: kilometres of physical elevation per 1% of the radius above sea "
            + "level. Terrain elevations are artistic (exaggerated); this only maps them for the climate "
            + "and never changes the terrain. Range [0.2, 3].")]
        [Range(MinElevationScale, MaxElevationScale)]
        public float elevationScale = 1f;

        [Tooltip("Environmental lapse rate, degrees C per km.")]
        [Range(0f, 10f)]
        public float lapseRate = 6.5f;

        [Tooltip("Planet radius used to express climate distances (km). Rendering scale is unaffected.")]
        [Range(1000f, 20000f)]
        public float planetRadiusKm = 6371f;

        [Tooltip("Sea-level annual-mean temperature at the equator for an Earth-like tilt (23.44 deg), degrees C. "
            + "Calibrates the linear energy-balance relation T = A + B * insolation.")]
        public float equatorTemperature = 27f;

        [Tooltip("Sea-level annual-mean temperature at the poles for an Earth-like tilt, degrees C (calibration).")]
        public float poleTemperature = -25f;

        [Tooltip("Continental amplification: far from the ocean the annual-mean departure from 10 C grows by up "
            + "to this fraction (hot interiors hotter, cold interiors colder). Oceans are unaffected.")]
        [Range(0f, 0.5f)]
        public float continentality = 0.15f;

        [Tooltip("e-folding distance of the ocean influence over land, km.")]
        [Range(100f, 3000f)]
        public float oceanInfluenceDistance = 700f;

        [Tooltip("Amplitude of the small deterministic temperature variation, degrees C (0 = none).")]
        [Range(0f, 3f)]
        public float temperatureVariation = 0.75f;

        [Tooltip("Maximum regional rotation of the prevailing winds, degrees (smooth, seed-dependent; 0 = none).")]
        [Range(0f, 45f)]
        public float windPerturbation = 20f;

        // Moisture calibration (V0.1.1E-A.1, measured on the 7 reference seeds: temperate land about x0.6 of
        // the planet mean, windward temperate coasts about x1.2, continental interiors x0.1 .. x1.3 depending on
        // the planet, arid land below 60 deg about 38%). Dimensionless or km; see ClimateGenerator.

        [Tooltip("Share of the rain falling on land returned to the air (evapotranspiration). Earth land: "
            + "evapotranspiration / precipitation is about 0.6. Water is moved, never created. Range [0, 0.8].")]
        [Range(0f, 0.8f)]
        public float landRecycling = 0.7f;

        [Tooltip("Strength of the mid-latitude storm-track rain tendency (westerlies). Range [0, 3].")]
        [Range(0f, 3f)]
        public float stormTracks = 2.5f;

        [Tooltip("Background rain-out e-folding travel distance at tendency 1, km. Range [500, 6000].")]
        [Range(500f, 6000f)]
        public float rainLength = 2500f;

        [Tooltip("Ocean evaporation relaxation e-folding travel distance, km. Range [100, 3000].")]
        [Range(100f, 3000f)]
        public float evaporationLength = 500f;

        [Tooltip("Large-scale rain needs nearly saturated air: its rate is scaled by relative humidity^k "
            + "(0 = independent of humidity). Orographic and saturation rain are unaffected. Range [0, 3].")]
        [Range(0f, 3f)]
        public float humidityRainExponent = 1f;

        public void Sanitize()
        {
            resolution = resolution <= LowResolution ? LowResolution : resolution <= DefaultResolution ? DefaultResolution : HighResolution;
            if (!(rotationAxis.sqrMagnitude > 1e-12f) || float.IsNaN(rotationAxis.x))
            {
                rotationAxis = Vector3.up;
            }
            axialTilt = Mathf.Clamp(axialTilt, 0f, MaxAxialTilt);
            elevationScale = Mathf.Clamp(elevationScale, MinElevationScale, MaxElevationScale);
            lapseRate = Mathf.Clamp(lapseRate, 0f, 10f);
            planetRadiusKm = Mathf.Clamp(planetRadiusKm, 1000f, 20000f);
            continentality = Mathf.Clamp(continentality, 0f, 0.5f);
            oceanInfluenceDistance = Mathf.Clamp(oceanInfluenceDistance, 100f, 3000f);
            temperatureVariation = Mathf.Clamp(temperatureVariation, 0f, 3f);
            windPerturbation = Mathf.Clamp(windPerturbation, 0f, 45f);
            landRecycling = Mathf.Clamp(landRecycling, 0f, 0.8f);
            stormTracks = Mathf.Clamp(stormTracks, 0f, 3f);
            rainLength = Mathf.Clamp(rainLength, 500f, 6000f);
            evaporationLength = Mathf.Clamp(evaporationLength, 100f, 3000f);
            humidityRainExponent = Mathf.Clamp(humidityRainExponent, 0f, 3f);
        }

        public ClimateSettings Clone()
        {
            return (ClimateSettings)MemberwiseClone();
        }

        /// <summary>True when both settings produce the same climate (every field is climate-relevant).</summary>
        public bool ClimateEquals(ClimateSettings other)
        {
            return other != null && resolution == other.resolution && rotationAxis == other.rotationAxis
                && axialTilt == other.axialTilt && elevationScale == other.elevationScale && lapseRate == other.lapseRate
                && planetRadiusKm == other.planetRadiusKm && equatorTemperature == other.equatorTemperature
                && poleTemperature == other.poleTemperature && continentality == other.continentality
                && oceanInfluenceDistance == other.oceanInfluenceDistance && temperatureVariation == other.temperatureVariation
                && windPerturbation == other.windPerturbation && landRecycling == other.landRecycling
                && stormTracks == other.stormTracks && rainLength == other.rainLength && evaporationLength == other.evaporationLength
                && humidityRainExponent == other.humidityRainExponent;
        }
    }
}
