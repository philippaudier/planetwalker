using System;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// Oceans and atmosphere appearance (V0.1.2B). Rendering only: changing these values updates
    /// material properties and never regenerates the planet or recomputes terrain, hydrology,
    /// climate, biomes or the terrain slope field.
    /// </summary>
    [Serializable]
    public sealed class OceanAtmosphereSettings
    {
        [Tooltip("Bathymetry-aware ocean shading (deep / shallow / coastal shelves, roughness-controlled "
            + "highlight). Off: the previous flat ocean.")]
        public bool oceanShading = true;

        [Tooltip("Strength of the shallow-to-deep color difference. Range [0, 1].")]
        [Range(0f, 1f)]
        public float depthContrast = 0.5f;

        [Tooltip("Visibility of the turquoise tint over broad, genuinely shallow shelves. Range [0, 1].")]
        [Range(0f, 1f)]
        public float shelfVisibility = 0.5f;

        [Tooltip("Ocean surface roughness: low = small, brighter sun highlight; high = broad, dim sheen. "
            + "Range [0.08, 0.8].")]
        [Range(MinRoughness, MaxRoughness)]
        public float oceanRoughness = 0.35f;

        [Tooltip("Sun highlight intensity. Range [0, 1].")]
        [Range(0f, 1f)]
        public float specularIntensity = 0.35f;

        [Tooltip("Thin atmospheric limb (rendering only).")]
        public bool atmosphere = true;

        [Tooltip("Atmosphere brightness. Range [0, 2].")]
        [Range(0f, 2f)]
        public float atmosphereStrength = 1f;

        [Tooltip("Atmosphere shell thickness, as a fraction of the ocean radius. Rendering parameter only "
            + "(no effect on terrain or planet physics). Range [0.005, 0.08].")]
        [Range(MinThickness, MaxThickness)]
        public float atmosphereThickness = 0.025f;

        [Tooltip("Day / night terminator softness (wrap of the main light on terrain and ocean). 0.2 = the "
            + "previous terrain look. Range [0, 1].")]
        [Range(0f, 1f)]
        public float terminatorSoftness = 0.2f;

        public const float MinRoughness = 0.08f;
        public const float MaxRoughness = 0.8f;
        public const float MinThickness = 0.005f;
        public const float MaxThickness = 0.08f;

        /// <summary>Main-light wrap applied to terrain and ocean for <see cref="terminatorSoftness"/>.</summary>
        public float LightWrap => terminatorSoftness * 0.4f;

        public void Sanitize()
        {
            depthContrast = Mathf.Clamp01(depthContrast);
            shelfVisibility = Mathf.Clamp01(shelfVisibility);
            oceanRoughness = Mathf.Clamp(oceanRoughness, MinRoughness, MaxRoughness);
            specularIntensity = Mathf.Clamp01(specularIntensity);
            atmosphereStrength = Mathf.Clamp(atmosphereStrength, 0f, 2f);
            atmosphereThickness = Mathf.Clamp(atmosphereThickness, MinThickness, MaxThickness);
            terminatorSoftness = Mathf.Clamp01(terminatorSoftness);
        }

        public OceanAtmosphereSettings Clone()
        {
            return (OceanAtmosphereSettings)MemberwiseClone();
        }

        public bool AppearanceEquals(OceanAtmosphereSettings other)
        {
            return other != null && oceanShading == other.oceanShading && depthContrast == other.depthContrast
                && shelfVisibility == other.shelfVisibility && oceanRoughness == other.oceanRoughness
                && specularIntensity == other.specularIntensity && atmosphere == other.atmosphere
                && atmosphereStrength == other.atmosphereStrength && atmosphereThickness == other.atmosphereThickness
                && terminatorSoftness == other.terminatorSoftness;
        }
    }
}
