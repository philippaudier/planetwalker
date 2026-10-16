using System;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Terrain
{
    /// <summary>
    /// Adapter between the existing elevation field and the adaptive terrain (V0.2.0A). Reads the planet's own
    /// <see cref="Planetwalker.Generation.PlanetHeightGenerator"/> (the geographical authority): same function, same
    /// units (elevation as a fraction of the radius), no added noise, no dependence on any mesh. A surface point is
    /// computed with the exact expression of the existing planet mesh, so equal directions give equal positions.
    /// </summary>
    public sealed class TerrainElevationSampler
    {
        /// <summary>Margin added to the sampled relief range for conservative bounds (fraction of that range).</summary>
        public const float ReliefMargin = 0.15f;

        public TerrainElevationSampler(PlanetInstance planet)
        {
            Planet = planet ?? throw new ArgumentNullException(nameof(planet));
            Radius = planet.Radius;
            float range = Mathf.Max(planet.MaxElevation - planet.MinElevation, 1e-6f);
            // The planet statistics are sampled (8192 directions): widen them for bounds that must contain the terrain.
            MinElevation = Mathf.Min(planet.MinElevation, planet.SeaLevel) - range * ReliefMargin;
            MaxElevation = Mathf.Max(planet.MaxElevation, planet.SeaLevel) + range * ReliefMargin;
        }

        public PlanetInstance Planet { get; }

        /// <summary>Reference radius (rendering units).</summary>
        public float Radius { get; }

        /// <summary>Conservative elevation bounds (fraction of the radius), sea level included.</summary>
        public float MinElevation { get; }
        public float MaxElevation { get; }

        public float MinRadius => Radius * (1f + MinElevation);
        public float MaxRadius => Radius * (1f + MaxElevation);

        /// <summary>Elevation (fraction of the radius) in a unit direction: the existing generator, unchanged.</summary>
        public float Elevation(Vector3 direction) => Planet.Height.GetElevation(direction);

        /// <summary>Terrain point (object space) in a unit direction, exactly as the existing planet mesh computes it.</summary>
        public Vector3 SurfacePoint(Vector3 direction, float elevation)
        {
            float vertexRadius = Radius * (1f + elevation);
            return direction * vertexRadius;
        }

        public Vector3 SurfacePoint(Vector3 direction) => SurfacePoint(direction, Elevation(direction));

        /// <summary>Surface normal with the existing method (finite differences at the given angular spacing).</summary>
        public Vector3 Normal(Vector3 direction, float elevation, float sampleAngle) =>
            Planet.Height.GetSurfaceNormal(direction, elevation, sampleAngle);
    }
}
