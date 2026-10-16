using System;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Rendering
{
    /// <summary>
    /// Planetary lighting and time (V0.1.2D). The authoritative solar state: one direction, computed
    /// from the visual solar time and applied to the scene's existing star light. Every shader (terrain,
    /// ocean, atmosphere, clouds, cloud shadows, haze) reads that light through URP's main light, so they
    /// always agree; no shader has its own sun vector.
    /// </summary>
    /// <remarks>
    /// Convention: the planet stays fixed (climate, weather field, cloud shadows, overlays and debug views
    /// keep their coordinates); the sun direction turns around the planet's rotation axis (the climate
    /// axis), westwards as the solar time grows, i.e. the planet's day. Solar time 0.5 = the star's
    /// original orientation (the validated default view). A future orbital / axial simulation only has to
    /// replace <see cref="SolarDirection"/>. Independent of the planet seed, the weather seed and the
    /// cloud animation time; changing it never regenerates or recomputes anything.
    /// </remarks>
    public sealed partial class PlanetVisualController
    {
        static readonly int NightAmbientId = Shader.PropertyToID("_NightAmbient");
        static readonly int TwilightWarmthId = Shader.PropertyToID("_TwilightWarmth");
        static readonly int WarmStrengthId2 = Shader.PropertyToID("_WarmStrength");

        [Tooltip("Planetary lighting and time presentation (V0.1.2D). Rendering only.")]
        [SerializeField] SolarSettings solar = new SolarSettings();

        [Tooltip("The star light (assigned by the bootstrap). Its orientation is driven by the solar clock.")]
        [SerializeField] Light sunLight;

        readonly SolarClock solarClock = new SolarClock();
        Vector3 baseSunDirection;
        bool baseSunCaptured;
        float appliedSolarTime = float.NaN;

        /// <summary>Real-time source of the solar clock (seconds). Replaceable for deterministic tests.</summary>
        public Func<double> SolarRealTime { get; set; } = () => Time.realtimeSinceStartupAsDouble;

        public SolarSettings Solar => solar;
        public Light SunLight => sunLight;
        public SolarClock SolarClock => solarClock;
        /// <summary>Direction towards the sun (world) at the solar time 0.5 (the star's original orientation).</summary>
        public Vector3 BaseSunDirection => baseSunDirection;
        /// <summary>Current direction towards the sun (world).</summary>
        public Vector3 SunDirection => SolarDirection(solar.solarTime);

        /// <summary>Assigns the star light and captures its orientation as the default sun (solar time 0.5).</summary>
        public void SetSun(Light light)
        {
            sunLight = light;
            if (light != null && !baseSunCaptured)
            {
                baseSunDirection = -light.transform.forward;
                baseSunCaptured = true;
            }
            ApplySun();
        }

        /// <summary>Direction towards the sun (world) at a solar time: the default sun turned around the planet's axis.</summary>
        public Vector3 SolarDirection(float solarTime)
        {
            Vector3 axis = transform.TransformDirection(climate.rotationAxis.sqrMagnitude > 0f ? climate.rotationAxis.normalized : Vector3.up).normalized;
            Vector3 baseDirection = baseSunCaptured ? baseSunDirection : (sunLight != null ? -sunLight.transform.forward : Vector3.forward);
            float degrees = (SolarClock.Wrap(solarTime) - SolarSettings.DefaultSolarTime) * 360f;
            return (Quaternion.AngleAxis(-degrees, axis) * baseDirection).normalized;
        }

        /// <summary>Orients the star light for the current solar time (transform only; nothing is recomputed).</summary>
        public void ApplySun()
        {
            solar.Sanitize();
            appliedSolarTime = solar.solarTime;
            if (sunLight == null)
            {
                return;
            }
            Vector3 toSun = SolarDirection(solar.solarTime);
            Vector3 up = transform.TransformDirection(climate.rotationAxis.sqrMagnitude > 0f ? climate.rotationAxis : Vector3.up);
            if (Mathf.Abs(Vector3.Dot(up.normalized, toSun)) > 0.99f)
            {
                up = Vector3.Cross(toSun, Vector3.right);
            }
            sunLight.transform.rotation = Quaternion.LookRotation(-toSun, up);
        }

        /// <summary>Manual solar time (scrubber). A running cycle continues from it.</summary>
        public void SetSolarTime(float solarTime)
        {
            solar.solarTime = SolarClock.Wrap(solarTime);
            solarClock.Set(SolarRealTime(), solar.solarTime);
            ApplySun();
        }

        /// <summary>Automatic progression on / off (off keeps the current time).</summary>
        public void SetDayNightCycle(bool enabled)
        {
            solar.dayNightCycle = enabled;
            SyncSolarClock();
        }

        public void SetSolarPaused(bool paused)
        {
            solar.paused = paused;
            SyncSolarClock();
        }

        public void SetCycleDuration(float seconds)
        {
            float old = solar.cycleDuration;
            solar.cycleDuration = Mathf.Clamp(seconds, SolarSettings.MinCycleDuration, SolarSettings.MaxCycleDuration);
            if (solarClock.Running)
            {
                solarClock.ChangeDuration(SolarRealTime(), old);
            }
        }

        /// <summary>Default sun: solar time 0.5, cycle stopped (the validated default view).</summary>
        public void ResetSun()
        {
            solar.dayNightCycle = false;
            solar.paused = false;
            SyncSolarClock(); // stops a running clock first (it would otherwise write its own time back)
            solar.solarTime = SolarSettings.DefaultSolarTime;
            solarClock.Set(SolarRealTime(), solar.solarTime);
            ApplySun();
        }

        /// <summary>Starts or stops the clock to match the settings (anchored at the current time: no jump, no drift).</summary>
        void SyncSolarClock()
        {
            bool shouldRun = solar.dayNightCycle && !solar.paused;
            double now = SolarRealTime();
            if (shouldRun && !solarClock.Running)
            {
                solarClock.Start(now, solar.solarTime);
            }
            else if (!shouldRun && solarClock.Running)
            {
                solar.solarTime = solarClock.Stop(now, solar.cycleDuration);
            }
            ApplySun();
        }

        /// <summary>
        /// Per frame (Play mode): advances the solar time when the cycle runs, applies a scrubbed time.
        /// Light transform only: no allocation, no recomputation.
        /// </summary>
        public void TickSolar()
        {
            if (solar.dayNightCycle && !solar.paused != solarClock.Running)
            {
                SyncSolarClock();
            }
            if (solarClock.Running)
            {
                solar.solarTime = solarClock.Evaluate(SolarRealTime(), solar.cycleDuration);
            }
            if (solar.solarTime != appliedSolarTime)
            {
                ApplySun();
            }
        }

        /// <summary>Night darkness and twilight warmth on a terrain / ocean / cloud property block.</summary>
        void WriteSolarShading(MaterialPropertyBlock block)
        {
            block.SetFloat(NightAmbientId, solar.nightAmbient);
            block.SetFloat(TwilightWarmthId, solar.twilightWarmth);
        }

        /// <summary>Atmosphere warm tint from the twilight warmth (0.5 = the material's V0.1.2C value).</summary>
        float TwilightWarmStrength(Material atmosphere)
        {
            float baseline = atmosphere != null && atmosphere.HasProperty(WarmStrengthId2) ? atmosphere.GetFloat(WarmStrengthId2) : 0.3f;
            return baseline * solar.twilightWarmth * 2f;
        }
    }
}
