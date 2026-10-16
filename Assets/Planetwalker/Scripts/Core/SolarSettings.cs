using System;
using System.Globalization;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// Planetary lighting and time presentation (V0.1.2D). A visual solar clock, not an orbital
    /// simulation: it only orients the existing star light. Independent of the planet seed, the weather
    /// seed and the cloud animation time; never regenerates anything.
    /// </summary>
    [Serializable]
    public sealed class SolarSettings
    {
        /// <summary>Solar time of the validated default view (the star's original orientation).</summary>
        public const float DefaultSolarTime = 0.5f;
        public const float MinCycleDuration = 20f;
        public const float MaxCycleDuration = 1800f;

        [Tooltip("Automatic day / night progression (off: the solar time only changes with the scrubber).")]
        public bool dayNightCycle;

        [Tooltip("Pauses the automatic progression (the cloud motion is independent).")]
        public bool paused;

        [Tooltip("Real seconds for one full day / night cycle. Range [20, 1800].")]
        [Range(MinCycleDuration, MaxCycleDuration)]
        public float cycleDuration = 240f;

        [Tooltip("Visual solar time in [0, 1): 0.5 = the default sun (noon over the default view), 0 = midnight there.")]
        [Range(0f, 1f)]
        public float solarTime = DefaultSolarTime;

        [Tooltip("Warm sunrise / sunset tint of the atmospheric limb, the haze and the cloud edges. 0.5 = the V0.1.2C look. Range [0, 1].")]
        [Range(0f, 1f)]
        public float twilightWarmth = 0.5f;

        [Tooltip("Ambient light kept on the night side (fraction of the scene ambient). Low = a genuinely dark night. Range [0, 1].")]
        [Range(0f, 1f)]
        public float nightAmbient = 0.04f;

        public void Sanitize()
        {
            cycleDuration = Mathf.Clamp(cycleDuration, MinCycleDuration, MaxCycleDuration);
            solarTime = SolarClock.Wrap(solarTime);
            twilightWarmth = Mathf.Clamp01(twilightWarmth);
            nightAmbient = Mathf.Clamp01(nightAmbient);
        }

        public SolarSettings Clone()
        {
            return (SolarSettings)MemberwiseClone();
        }

        /// <summary>Shading parameters (time excluded: the sun direction is the light's orientation).</summary>
        public bool ShadingEquals(SolarSettings other)
        {
            return other != null && twilightWarmth == other.twilightWarmth && nightAmbient == other.nightAmbient;
        }

        /// <summary>Human-readable local solar time ("12:00" at 0.5).</summary>
        public static string Format(float solarTime)
        {
            int minutes = Mathf.FloorToInt(SolarClock.Wrap(solarTime) * 1440f) % 1440;
            return (minutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Deterministic visual solar clock: the time is computed from an anchor (value, real time), never
    /// accumulated per frame, so it does not depend on the frame rate and pause / resume never drifts.
    /// </summary>
    public sealed class SolarClock
    {
        double anchorRealTime;
        float anchorValue = SolarSettings.DefaultSolarTime;

        public bool Running { get; private set; }

        public static float Wrap(float value)
        {
            float w = value - Mathf.Floor(value);
            return w >= 1f ? 0f : w;
        }

        /// <summary>Solar time at a real time (seconds) for a cycle duration (seconds).</summary>
        public float Evaluate(double now, float cycleDuration)
        {
            if (!Running)
            {
                return anchorValue;
            }
            double t = anchorValue + (now - anchorRealTime) / Math.Max(1e-3, cycleDuration);
            return Wrap((float)(t - Math.Floor(t)));
        }

        /// <summary>Starts (or restarts) the progression from a value at a real time.</summary>
        public void Start(double now, float value)
        {
            anchorValue = Wrap(value);
            anchorRealTime = now;
            Running = true;
        }

        /// <summary>Stops the progression, keeping the current value.</summary>
        public float Stop(double now, float cycleDuration)
        {
            anchorValue = Evaluate(now, cycleDuration);
            Running = false;
            return anchorValue;
        }

        /// <summary>Jumps to a value (scrubber); a running clock continues from there.</summary>
        public void Set(double now, float value)
        {
            anchorValue = Wrap(value);
            anchorRealTime = now;
        }

        /// <summary>Changes the cycle duration without a jump (re-anchors at the current value).</summary>
        public void ChangeDuration(double now, float oldDuration)
        {
            anchorValue = Evaluate(now, oldDuration);
            anchorRealTime = now;
        }
    }
}
