using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// A generated planet: an immutable snapshot of its definition, its elevation function
    /// and a few mesh-independent statistics. Rendering consumes it; it knows nothing
    /// about meshes or GameObjects.
    /// </summary>
    public sealed class PlanetInstance
    {
        // Fibonacci-sphere sample count used for statistics (mesh independent).
        const int StatisticsSampleCount = 8192;

        public PlanetInstance(PlanetDefinition definition)
        {
            Definition = definition.Clone();
            Definition.Sanitize();
            Scale = PlanetScale.Of(Definition);
            Height = new PlanetHeightGenerator(Definition);
            ComputeStatistics();
        }

        /// <summary>Private copy of the definition used for generation. Do not modify.</summary>
        public PlanetDefinition Definition { get; }

        public PlanetHeightGenerator Height { get; }

        /// <summary>Physical scale contract (V0.2.0C): render units vs meters.</summary>
        public PlanetScale Scale { get; }

        public float Radius => Definition.radius;

        public float SeaLevel => Definition.seaLevel;

        public float OceanRadius => Definition.radius * (1f + Definition.seaLevel);

        /// <summary>Approximate minimum elevation (fraction of radius), from sampling.</summary>
        public float MinElevation { get; private set; }

        /// <summary>Approximate maximum elevation (fraction of radius), from sampling.</summary>
        public float MaxElevation { get; private set; }

        /// <summary>Approximate fraction of the surface above sea level, in [0, 1].</summary>
        public float LandFraction { get; private set; }

        public float GetElevation(Vector3 normalizedDirection)
        {
            return Height.GetElevation(normalizedDirection);
        }

        /// <summary>
        /// Angular spacing (radians) of the legacy face meshes: the band limit of the legacy world (V0.2.0C). The
        /// face meshes, and every analysis and water system built on them (hydrology, climate, bathymetry, slope
        /// field, lakes, rivers), sample the terrain band-limited to at least this spacing, so they agree with
        /// each other exactly; the adaptive terrain samples the full canonical function.
        /// </summary>
        public float LegacySampleSpacing => Mathf.PI * 0.5f / Definition.resolution;

        /// <summary>Elevation band-limited to the legacy world (see <see cref="LegacySampleSpacing"/>).</summary>
        public float GetLegacyElevation(Vector3 normalizedDirection)
        {
            return Height.GetElevation(normalizedDirection, LegacySampleSpacing);
        }

        /// <summary>Elevation band-limited to the coarser of the legacy world and an analysis grid spacing.</summary>
        public float GetLegacyElevation(Vector3 normalizedDirection, float gridSpacing)
        {
            return Height.GetElevation(normalizedDirection, Mathf.Max(gridSpacing, LegacySampleSpacing));
        }

        /// <summary>Distance from the planet center to the terrain surface, in rendering units.</summary>
        public float GetSurfaceRadius(Vector3 normalizedDirection)
        {
            return Definition.radius * (1f + Height.GetElevation(normalizedDirection));
        }

        void ComputeStatistics()
        {
            const float goldenAngle = 2.39996323f;
            float min = float.MaxValue;
            float max = float.MinValue;
            int land = 0;
            for (int i = 0; i < StatisticsSampleCount; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / StatisticsSampleCount;
                float r = Mathf.Sqrt(1f - y * y);
                float phi = i * goldenAngle;
                var direction = new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi));
                float elevation = Height.GetElevation(direction);
                min = Mathf.Min(min, elevation);
                max = Mathf.Max(max, elevation);
                if (elevation > Definition.seaLevel)
                {
                    land++;
                }
            }
            MinElevation = min;
            MaxElevation = max;
            LandFraction = land / (float)StatisticsSampleCount;
        }
    }
}
