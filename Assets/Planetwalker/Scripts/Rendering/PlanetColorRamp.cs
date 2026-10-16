using System;
using UnityEngine;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Elevation-based terrain palette. Elevations are relative to sea level and use the
    /// planet convention (fraction of the reference radius), so the snow line or the
    /// beach band are absolute altitudes that do not depend on the rendering scale.
    /// Colors are authored in sRGB.
    /// </summary>
    [Serializable]
    public sealed class PlanetColorRamp
    {
        [Serializable]
        public struct Stop
        {
            [Tooltip("Elevation above sea level (fraction of radius). Stops must be sorted ascending.")]
            public float elevation;
            public Color color;

            public Stop(float elevation, Color color)
            {
                this.elevation = elevation;
                this.color = color;
            }
        }

        [Tooltip("Color stops sorted by ascending elevation relative to sea level.")]
        public Stop[] stops;

        [Tooltip("Color of bare rock on steep land slopes.")]
        public Color rockColor = new Color(0.42f, 0.39f, 0.36f);

        [Tooltip("Steepness (1 - cos(slope angle)) where rock starts to appear.")]
        [Range(0f, 1f)] public float rockSteepnessStart = 0.05f;

        [Tooltip("Steepness where rock fully replaces the elevation color (times rockStrength).")]
        [Range(0f, 1f)] public float rockSteepnessEnd = 0.2f;

        [Range(0f, 1f)] public float rockStrength = 0.75f;

        /// <summary>Deep copy (an immutable snapshot for worker threads, V0.2.0B).</summary>
        public PlanetColorRamp Clone()
        {
            var copy = (PlanetColorRamp)MemberwiseClone();
            copy.stops = stops != null ? (Stop[])stops.Clone() : null;
            return copy;
        }

        /// <summary>True when both ramps produce the same colors.</summary>
        public bool ContentEquals(PlanetColorRamp other)
        {
            if (other == null || rockColor != other.rockColor || rockSteepnessStart != other.rockSteepnessStart
                || rockSteepnessEnd != other.rockSteepnessEnd || rockStrength != other.rockStrength
                || (stops == null) != (other.stops == null) || (stops != null && stops.Length != other.stops.Length))
            {
                return false;
            }
            for (int i = 0; stops != null && i < stops.Length; i++)
            {
                if (stops[i].elevation != other.stops[i].elevation || stops[i].color != other.stops[i].color)
                {
                    return false;
                }
            }
            return true;
        }

        public static PlanetColorRamp CreateDefault()
        {
            return new PlanetColorRamp
            {
                stops = new[]
                {
                    new Stop(-0.030f, new Color(0.035f, 0.075f, 0.16f)), // deep ocean floor
                    new Stop(-0.012f, new Color(0.07f, 0.16f, 0.28f)),
                    new Stop(-0.004f, new Color(0.16f, 0.34f, 0.42f)),   // shelf
                    new Stop(-0.0008f, new Color(0.42f, 0.58f, 0.55f)),  // shallow water
                    new Stop(0.0000f, new Color(0.64f, 0.62f, 0.50f)),   // beach
                    new Stop(0.0004f, new Color(0.52f, 0.54f, 0.38f)),
                    new Stop(0.0012f, new Color(0.40f, 0.47f, 0.27f)),   // lowland plains
                    new Stop(0.0050f, new Color(0.33f, 0.43f, 0.22f)),
                    new Stop(0.0110f, new Color(0.48f, 0.43f, 0.29f)),   // highlands
                    new Stop(0.0180f, new Color(0.43f, 0.37f, 0.30f)),
                    new Stop(0.0250f, new Color(0.47f, 0.45f, 0.43f)),   // mountains
                    new Stop(0.0310f, new Color(0.62f, 0.61f, 0.60f)),
                    new Stop(0.0360f, new Color(0.92f, 0.93f, 0.95f)),   // high peaks
                },
            };
        }

        /// <param name="elevationAboveSea">Elevation minus sea level.</param>
        /// <param name="steepness">1 - dot(surfaceNormal, radialDirection).</param>
        public Color Evaluate(float elevationAboveSea, float steepness)
        {
            Color color = EvaluateElevation(elevationAboveSea);
            if (elevationAboveSea > 0f && rockStrength > 0f && rockSteepnessEnd > rockSteepnessStart)
            {
                float t = Mathf.Clamp01((steepness - rockSteepnessStart) / (rockSteepnessEnd - rockSteepnessStart));
                color = Color.Lerp(color, rockColor, t * t * (3f - 2f * t) * rockStrength);
            }
            return color;
        }

        public Color EvaluateElevation(float elevationAboveSea)
        {
            if (stops == null || stops.Length == 0)
            {
                return Color.magenta;
            }
            if (elevationAboveSea <= stops[0].elevation)
            {
                return stops[0].color;
            }
            for (int i = 1; i < stops.Length; i++)
            {
                if (elevationAboveSea <= stops[i].elevation)
                {
                    Stop a = stops[i - 1];
                    Stop b = stops[i];
                    float range = b.elevation - a.elevation;
                    float t = range > 0f ? (elevationAboveSea - a.elevation) / range : 1f;
                    return Color.Lerp(a.color, b.color, t);
                }
            }
            return stops[stops.Length - 1].color;
        }
    }
}
