using System;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// Biome classification settings (V0.1.1E-B.1). The classification reads the climate result and
    /// never changes it; only these three interpretation parameters are exposed.
    /// </summary>
    [Serializable]
    public sealed class BiomeSettings
    {
        [Tooltip("Multiplier of the evaporative demand: higher = drier interpretation of the same rain "
            + "(more steppe and desert). 1 = Earth-like calibration. Range [0.5, 2].")]
        [Range(0.5f, 2f)]
        public float aridity = 1f;

        [Tooltip("Sharpness of the transitions between biomes (width of the ecological envelopes' edges). "
            + "Higher = narrower ecotones. Range [0.5, 2].")]
        [Range(0.5f, 2f)]
        public float ecotoneSharpness = 1f;

        [Tooltip("How readily cold highlands become alpine: scales the elevation cooling needed. Higher = "
            + "alpine at lower altitude. Range [0.5, 2].")]
        [Range(0.5f, 2f)]
        public float alpineSensitivity = 1f;

        public void Sanitize()
        {
            aridity = Mathf.Clamp(aridity, 0.5f, 2f);
            ecotoneSharpness = Mathf.Clamp(ecotoneSharpness, 0.5f, 2f);
            alpineSensitivity = Mathf.Clamp(alpineSensitivity, 0.5f, 2f);
        }

        public BiomeSettings Clone()
        {
            return (BiomeSettings)MemberwiseClone();
        }

        public bool BiomeEquals(BiomeSettings other)
        {
            return other != null && aridity == other.aridity && ecotoneSharpness == other.ecotoneSharpness
                && alpineSensitivity == other.alpineSensitivity;
        }
    }
}
