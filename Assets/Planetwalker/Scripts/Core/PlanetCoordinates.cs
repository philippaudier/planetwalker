using System;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// Local tangent frame at a point of the sphere (V0.2.0C): <see cref="Up"/> radial, <see cref="North"/> and
    /// <see cref="East"/> tangent, right handed (East = North x Up). The pole reference follows the convention the
    /// generator already uses for surface normals: +Y unless the point is within about 8 degrees of the Y poles,
    /// then +X. The frame is orthonormal everywhere, free of NaN and deterministic; like every tangent frame on a
    /// sphere it cannot be continuous at every point, so the switch is explicit and the same for every caller.
    /// </summary>
    public readonly struct PlanetTangentFrame
    {
        /// <summary>|up.y| above this uses the +X reference (identical rule to PlanetHeightGenerator.GetSurfaceNormal).</summary>
        public const double PoleThreshold = 0.99;

        public PlanetTangentFrame(Vector3d up, Vector3d north, Vector3d east)
        {
            Up = up;
            North = north;
            East = east;
        }

        public Vector3d Up { get; }
        public Vector3d North { get; }
        public Vector3d East { get; }

        /// <summary>Frame at a direction (any length, not zero).</summary>
        public static PlanetTangentFrame At(Vector3d direction)
        {
            Vector3d up = direction.Normalized;
            if (up.SqrMagnitude == 0)
            {
                throw new ArgumentException("A tangent frame needs a non-zero direction.", nameof(direction));
            }
            Vector3d reference = Math.Abs(up.y) < PoleThreshold ? Vector3d.Up : Vector3d.Right;
            Vector3d north = (reference - up * Vector3d.Dot(reference, up)).Normalized;
            Vector3d east = Vector3d.Cross(north, up);
            return new PlanetTangentFrame(up, north, east);
        }

        /// <summary>Components (east, north, up) of a planet-space vector.</summary>
        public Vector3d ToLocal(Vector3d planetVector) =>
            new Vector3d(Vector3d.Dot(planetVector, East), Vector3d.Dot(planetVector, North), Vector3d.Dot(planetVector, Up));

        /// <summary>Planet-space vector of local (east, north, up) components.</summary>
        public Vector3d FromLocal(Vector3d local) => East * local.x + North * local.y + Up * local.z;

        /// <summary>Planet-space vector of local (east, north, up) components.</summary>
        public Vector3d FromLocal(double east, double north, double up) => East * east + North * north + Up * up;
    }

    /// <summary>
    /// Planet-centered coordinate conventions (V0.2.0C). "Planet space" is the planet object's local space: the
    /// center at the origin, the cube-sphere axes of <c>CubeSphereGenerator</c> (unchanged), render units. A
    /// position is a <see cref="Vector3d"/>; a surface point is a unit direction and a radial distance.
    /// </summary>
    public static class PlanetCoordinates
    {
        /// <summary>Position at a radial distance (render units) along a direction.</summary>
        public static Vector3d Position(Vector3d direction, double radialDistance) => direction.Normalized * radialDistance;

        /// <summary>Position at an altitude (render units) above the terrain of a planet, along a direction.</summary>
        public static Vector3d AtAltitude(Vector3d direction, PlanetScale scale, double surfaceElevation, double altitudeUnits) =>
            direction.Normalized * (scale.SurfaceRadius(surfaceElevation) + altitudeUnits);

        /// <summary>Unit direction of a position (zero for the center).</summary>
        public static Vector3d Direction(Vector3d position) => position.Normalized;

        /// <summary>Radial distance from the center.</summary>
        public static double RadialDistance(Vector3d position) => position.Magnitude;

        /// <summary>Geodesic angle (radians) between two directions, stable for nearby and opposite directions.</summary>
        public static double Angle(Vector3d a, Vector3d b)
        {
            Vector3d u = a.Normalized, v = b.Normalized;
            return 2.0 * Math.Atan2((u - v).Magnitude, (u + v).Magnitude);
        }

        /// <summary>Moves a direction by a tangent displacement (east, north meters at the surface) along the sphere.</summary>
        public static Vector3d Step(Vector3d direction, PlanetScale scale, double eastMeters, double northMeters)
        {
            PlanetTangentFrame frame = PlanetTangentFrame.At(direction);
            double angle = Math.Sqrt(eastMeters * eastMeters + northMeters * northMeters) / scale.RadiusMeters;
            if (angle == 0)
            {
                return frame.Up;
            }
            Vector3d tangent = (frame.East * eastMeters + frame.North * northMeters).Normalized;
            return (frame.Up * Math.Cos(angle) + tangent * Math.Sin(angle)).Normalized;
        }
    }
}
