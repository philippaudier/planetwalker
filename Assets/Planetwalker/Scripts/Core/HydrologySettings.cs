using System;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// Hydrology analysis and display settings (V0.1.1C). Not a planet property: changing
    /// them never changes the terrain.
    /// </summary>
    [Serializable]
    public sealed class HydrologySettings
    {
        public const int PreviewResolution = 64;
        public const int StandardResolution = 128;
        public const int HighResolution = 256;
        public const float MinRiverNetworkThreshold = 1e-5f;
        public const float MaxRiverNetworkThreshold = 1e-2f;

        [Tooltip("Cells per cube face edge of the hydrology grid: 64 (preview), 128 (standard) or "
            + "256 (high). Independent of the render mesh resolution.")]
        public int resolution = StandardResolution;

        [Tooltip("Minimum drained area for a cell to belong to the river network (and to be shown as a "
            + "river candidate), as a fraction of the planet surface (1e-4 = 0.01%). Single source for "
            + "both; the UI exposes it as River Density. Never recomputes hydrology.")]
        [Range(MinRiverNetworkThreshold, MaxRiverNetworkThreshold)]
        public float riverNetworkThreshold = 7e-4f;

        public void Sanitize()
        {
            resolution = resolution <= PreviewResolution ? PreviewResolution
                : resolution <= StandardResolution ? StandardResolution : HighResolution;
            riverNetworkThreshold = Mathf.Clamp(riverNetworkThreshold, MinRiverNetworkThreshold, MaxRiverNetworkThreshold);
        }
    }
}
