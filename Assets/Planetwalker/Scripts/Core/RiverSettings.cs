using System;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// River network trajectory and display settings (V0.1.1D-A). Not a planet property:
    /// changing them never changes the terrain or the hydrology. The river selection
    /// threshold is <see cref="HydrologySettings.riverNetworkThreshold"/> (single source).
    /// </summary>
    [Serializable]
    public sealed class RiverSettings
    {
        public const int MaxSmoothingIterations = 3;

        [Tooltip("Render rivers on the normal (artistic) view. Off by default like Show Hydrology: "
            + "when on, every regeneration also runs the hydrology analysis.")]
        public bool showRivers;

        [Tooltip("Trajectory smoothing: 0 = raw cell-to-cell path, 1 = 3 corner-cutting passes. "
            + "Smoothed paths always stay inside the hydrological corridor.")]
        [Range(0f, 1f)]
        public float smoothing = 0.67f;

        [Tooltip("Visual width multiplier (does not rebuild the network).")]
        [Range(0.25f, 4f)]
        public float widthMultiplier = 1f;

        [Tooltip("Width of a river draining the reference area, as a fraction of the planet radius "
            + "(artistic: strongly exaggerated so rivers read from orbit).")]
        [Range(0.0001f, 0.01f)]
        public float referenceWidth = 0.0012f;

        [Tooltip("Drained area (fraction of the planet surface) that has the reference width.")]
        [Range(1e-5f, 1e-2f)]
        public float referenceArea = 5e-4f;

        [Tooltip("Width grows as (drained area / reference area) ^ exponent (hydraulic geometry ~0.5).")]
        [Range(0.1f, 1f)]
        public float widthExponent = 0.5f;

        [Tooltip("Width bounds, as fractions of the planet radius (before the multiplier).")]
        [Range(0f, 0.01f)]
        public float minWidth = 0.0005f;

        [Range(0.001f, 0.05f)]
        public float maxWidth = 0.006f;

        [Tooltip("Rivers never get thinner than this on screen (half width, in pixels), so they stay "
            + "visible from orbit.")]
        [Range(0f, 4f)]
        public float minPixelHalfWidth = 0.75f;

        [Tooltip("Display filter: rivers that reach the sea directly, have no tributary and are shorter "
            + "than this (in planet radii) are not drawn. They stay in the network data.")]
        [Range(0f, 0.5f)]
        public float minDisplayLength = 0.08f;

        [Tooltip("Width tapers from 30% to 100% over this distance from a source (planet radii).")]
        [Range(0f, 0.2f)]
        public float sourceTaperLength = 0.03f;

        public Color color = new Color(0.16f, 0.36f, 0.62f);

        public int SmoothingIterations => Mathf.Clamp(Mathf.RoundToInt(smoothing * MaxSmoothingIterations), 0, MaxSmoothingIterations);

        /// <summary>
        /// River width (fraction of the planet radius, multiplier included) for a drained area
        /// given as a fraction of the planet surface. Depends on area only, so it is continuous
        /// along a river, non-decreasing downstream, independent of the selection threshold and
        /// of the hydrology resolution. Never negative.
        /// </summary>
        public float GetWidth(double drainedAreaFraction)
        {
            double ratio = Math.Max(drainedAreaFraction, 0.0) / Math.Max(referenceArea, 1e-12f);
            float raw = referenceWidth * (float)Math.Pow(ratio, widthExponent);
            float lower = Mathf.Max(0f, Mathf.Min(minWidth, maxWidth));
            return Mathf.Clamp(raw, lower, Mathf.Max(lower, maxWidth)) * Mathf.Max(0f, widthMultiplier);
        }

        /// <summary>
        /// Display width at a path point: <see cref="GetWidth"/> with a taper near the source.
        /// Non-decreasing downstream because both factors are.
        /// </summary>
        public float GetDisplayWidth(double drainedAreaFraction, float distanceFromSource)
        {
            float taper = sourceTaperLength > 0f ? Mathf.SmoothStep(0.3f, 1f, Mathf.Clamp01(distanceFromSource / sourceTaperLength)) : 1f;
            return GetWidth(drainedAreaFraction) * taper;
        }

        public void Sanitize()
        {
            smoothing = Mathf.Clamp01(smoothing);
            widthMultiplier = Mathf.Clamp(widthMultiplier, 0.25f, 4f);
            widthExponent = Mathf.Clamp(widthExponent, 0.1f, 1f);
            minWidth = Mathf.Max(0f, minWidth);
            maxWidth = Mathf.Max(minWidth, maxWidth);
            minPixelHalfWidth = Mathf.Max(0f, minPixelHalfWidth);
        }
    }
}
