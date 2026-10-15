using System;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// Lake selection and display settings (V0.1.1D-B.1). Not a planet property: changing them
    /// never changes the terrain or the hydrology, and never recomputes the depression analysis.
    /// </summary>
    [Serializable]
    public sealed class LakeSettings
    {
        public const float MinMinimumArea = 1e-5f;
        public const float MaxMinimumArea = 1e-3f;

        [Tooltip("Render lakes on the normal (artistic) view and integrate them with the rivers. Off by "
            + "default: when on, every regeneration also runs the hydrology and lake analysis.")]
        public bool showLakes;

        [Tooltip("Selection threshold: 0 = only the most significant basins, 1 = many lakes. Lowers the "
            + "required storage volume; never creates water outside real depressions.")]
        [Range(0f, 1f)]
        public float abundance = 0.5f;

        [Tooltip("Smallest lake kept, as a fraction of the planet surface (1e-4 = 0.01%).")]
        [Range(MinMinimumArea, MaxMinimumArea)]
        public float minimumArea = 4e-5f;

        [Tooltip("Provisional level of CLOSED lakes, as a fraction of their containment depth (scaled by "
            + "their water supply). Always strictly below the spill elevation. Open lakes stay at their outlet.")]
        [Range(0f, 1f)]
        public float fillingFactor = 0.7f;

        [Tooltip("Lake water color (display only).")]
        public Color color = new Color(0.04f, 0.17f, 0.27f);

        /// <summary>True when both settings select the same lakes (display fields excluded).</summary>
        public bool SelectionEquals(LakeSettings other)
        {
            return other != null && abundance == other.abundance && minimumArea == other.minimumArea
                && fillingFactor == other.fillingFactor;
        }

        public LakeSettings Clone()
        {
            return (LakeSettings)MemberwiseClone();
        }

        public void Sanitize()
        {
            abundance = Mathf.Clamp01(abundance);
            minimumArea = Mathf.Clamp(minimumArea, MinMinimumArea, MaxMinimumArea);
            fillingFactor = Mathf.Clamp01(fillingFactor);
        }
    }
}
