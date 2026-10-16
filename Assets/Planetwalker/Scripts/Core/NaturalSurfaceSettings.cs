using System;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// Natural Planet appearance (V0.1.2A). Artistic only: changing these values recolors the terrain
    /// and never recomputes terrain, hydrology, climate or biomes.
    /// </summary>
    [Serializable]
    public sealed class NaturalSurfaceSettings
    {
        [Tooltip("Natural Planet: colors from biomes, terrain and multi-scale variation. Off: the classic "
            + "elevation palette.")]
        public bool enabled = true;

        [Tooltip("How much neighbouring biomes blend: 0 = crisp dominant biome colors, 1 = smooth "
            + "suitability-weighted blend. Does not change the classification (Ecotone Sharpness does).")]
        [Range(0f, 1f)]
        public float biomeBlend = 0.5f;

        [Tooltip("Strength of the deterministic regional / landscape / fine color variation. Range [0, 1].")]
        [Range(0f, 1f)]
        public float regionalVariation = 0.5f;

        [Tooltip("How readily steep terrain shows bare rock. Range [0, 1].")]
        [Range(0f, 1f)]
        public float rockExposure = 0.5f;

        [Tooltip("Overall color saturation. Range [0.5, 1.3].")]
        [Range(0.5f, 1.3f)]
        public float saturation = 0.9f;

        public void Sanitize()
        {
            biomeBlend = Mathf.Clamp01(biomeBlend);
            regionalVariation = Mathf.Clamp01(regionalVariation);
            rockExposure = Mathf.Clamp01(rockExposure);
            saturation = Mathf.Clamp(saturation, 0.5f, 1.3f);
        }

        public NaturalSurfaceSettings Clone()
        {
            return (NaturalSurfaceSettings)MemberwiseClone();
        }

        public bool AppearanceEquals(NaturalSurfaceSettings other)
        {
            return other != null && enabled == other.enabled && biomeBlend == other.biomeBlend
                && regionalVariation == other.regionalVariation && rockExposure == other.rockExposure && saturation == other.saturation;
        }
    }
}
