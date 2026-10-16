using System;
using System.Globalization;

namespace Planetwalker.Core
{
    /// <summary>
    /// The physical scale contract of a planet (V0.2.0C): the single place where render units meet meters.
    /// </summary>
    /// <remarks>
    /// Two independent numbers define a planet's size: its <b>render radius</b> (Unity units, <c>PlanetDefinition.radius</c>,
    /// 100 by default: the visual size, unchanged since V0.1.0) and its <b>physical radius</b> (meters,
    /// <c>PlanetDefinition.radiusKilometers</c>, 6371 km by default: Earth). Horizontal distances on the sphere are
    /// physically consistent with that radius (a continent spans thousands of kilometers). Vertical relief is NOT:
    /// elevation is a dimensionless fraction of the radius produced by the artistic generator ("Mountains 4.5%"
    /// means 4.5% of the radius, i.e. hundreds of kilometers at Earth scale, about 30 x the real world). That
    /// exaggeration is deliberate and preserved; this class only converts, it never reinterprets generation
    /// settings. A physically calibrated vertical model is a future milestone. Immutable, allocation free.
    /// </remarks>
    public sealed class PlanetScale
    {
        public const double DefaultRadiusKilometers = 6371.0;
        public const double MinRadiusKilometers = 10.0;
        public const double MaxRadiusKilometers = 100000.0;

        public PlanetScale(double renderRadius, double radiusMeters)
        {
            if (!(renderRadius > 0) || !(radiusMeters > 0))
            {
                throw new ArgumentOutOfRangeException(nameof(renderRadius), "Radii must be positive.");
            }
            RenderRadius = renderRadius;
            RadiusMeters = radiusMeters;
            MetersPerUnit = radiusMeters / renderRadius;
            UnitsPerMeter = renderRadius / radiusMeters;
        }

        /// <summary>From a definition: its render radius and physical radius.</summary>
        public static PlanetScale Of(PlanetDefinition definition) =>
            new PlanetScale(definition.radius, definition.radiusKilometers * 1000.0);

        /// <summary>Planet radius in render (Unity) units.</summary>
        public double RenderRadius { get; }

        /// <summary>Planet radius in meters.</summary>
        public double RadiusMeters { get; }

        public double MetersPerUnit { get; }
        public double UnitsPerMeter { get; }

        public double ToMeters(double renderUnits) => renderUnits * MetersPerUnit;
        public double ToUnits(double meters) => meters * UnitsPerMeter;

        /// <summary>Physical height of an elevation (fraction of the radius), meters.</summary>
        public double ElevationToMeters(double elevation) => elevation * RadiusMeters;

        /// <summary>Elevation (fraction of the radius) of a physical height, meters.</summary>
        public double MetersToElevation(double meters) => meters / RadiusMeters;

        /// <summary>Radial distance from the center, render units, of a point at an elevation.</summary>
        public double SurfaceRadius(double elevation) => RenderRadius * (1.0 + elevation);

        /// <summary>Altitude (meters) of a planet-centered position above the terrain at <paramref name="surfaceElevation"/>.</summary>
        public double AltitudeMeters(Vector3d planetPosition, double surfaceElevation) =>
            ToMeters(planetPosition.Magnitude - SurfaceRadius(surfaceElevation));

        /// <summary>Altitude (render units) of a planet-centered position above the terrain at <paramref name="surfaceElevation"/>.</summary>
        public double AltitudeUnits(Vector3d planetPosition, double surfaceElevation) =>
            planetPosition.Magnitude - SurfaceRadius(surfaceElevation);

        /// <summary>Arc length (meters) of an angle at the surface.</summary>
        public double ArcMeters(double radians) => radians * RadiusMeters;

        /// <summary>Angle (radians) spanned by an arc length (meters) at the surface.</summary>
        public double ArcRadians(double meters) => meters / RadiusMeters;

        /// <summary>Spatial frequency (cycles per radian) of a wavelength in meters.</summary>
        public double FrequencyOfWavelength(double wavelengthMeters) => 2.0 * Math.PI * RadiusMeters / wavelengthMeters;

        /// <summary>Wavelength in meters of a spatial frequency (cycles per radian).</summary>
        public double WavelengthOfFrequency(double cyclesPerRadian) => 2.0 * Math.PI * RadiusMeters / cyclesPerRadian;

        /// <summary>
        /// Smallest representable step (meters) of a single-precision coordinate of the given magnitude (render
        /// units): the precision of a float32 position at that distance from its origin.
        /// </summary>
        public double FloatPrecisionMeters(double magnitudeUnits)
        {
            float m = (float)Math.Abs(magnitudeUnits);
            if (m == 0f)
            {
                return ToMeters(float.Epsilon);
            }
            int bits = BitConverter.SingleToInt32Bits(m);
            float next = BitConverter.Int32BitsToSingle(bits + 1);
            return ToMeters(next - m);
        }

        /// <summary>"123 m", "4.57 km", "12,345 km": for the UI.</summary>
        public static string FormatDistance(double meters)
        {
            double a = Math.Abs(meters);
            if (a < 1000.0)
            {
                return meters.ToString(a < 10.0 ? "0.0" : "0", CultureInfo.InvariantCulture) + " m";
            }
            double km = meters / 1000.0;
            return km.ToString(a < 100000.0 ? "0.00" : "N0", CultureInfo.InvariantCulture) + " km";
        }
    }
}
