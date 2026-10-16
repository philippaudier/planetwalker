using System;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// Planetary forcing of the climate (V0.1.1E-A): latitude from the rotation axis, annual-mean
    /// insolation from the axial tilt, and the three-cell prevailing wind pattern. Pure functions.
    /// </summary>
    public static class ClimateForcing
    {
        /// <summary>Axial tilt of the Earth (degrees), used to calibrate temperatures.</summary>
        public const float EarthTilt = 23.44f;

        /// <summary>Latitude (radians, positive towards the axis) of a unit direction.</summary>
        public static float Latitude(Vector3 direction, Vector3 axis)
        {
            return Mathf.Asin(Mathf.Clamp(Vector3.Dot(direction, axis.normalized), -1f, 1f));
        }

        /// <summary>
        /// Daily-mean top-of-atmosphere insolation at a latitude for a solar declination, as a fraction of
        /// the solar constant S0 (circular orbit): Q = (h0 sin(lat) sin(dec) + cos(lat) cos(dec) sin(h0)) / pi,
        /// h0 the sunset hour angle (0 in polar night, pi in polar day).
        /// </summary>
        public static double DailyMeanInsolation(double latitude, double declination)
        {
            double x = -Math.Tan(latitude) * Math.Tan(declination);
            double h0 = x >= 1.0 ? 0.0 : x <= -1.0 ? Math.PI : Math.Acos(x);
            double q = (h0 * Math.Sin(latitude) * Math.Sin(declination) + Math.Cos(latitude) * Math.Cos(declination) * Math.Sin(h0)) / Math.PI;
            return Math.Max(0.0, q);
        }

        /// <summary>
        /// Annual-mean insolation (fraction of S0) at a latitude: the daily mean averaged over the orbit
        /// (orbital longitude sampled uniformly = uniform in time for a circular orbit), declination
        /// sin(dec) = sin(tilt) sin(longitude). Its area-weighted global mean is 1/4 for any tilt.
        /// </summary>
        public static double AnnualMeanInsolation(double latitude, double tiltRadians, int samples = 360)
        {
            double sum = 0.0;
            for (int k = 0; k < samples; k++)
            {
                double longitude = 2.0 * Math.PI * (k + 0.5) / samples;
                double declination = Math.Asin(Math.Sin(tiltRadians) * Math.Sin(longitude));
                sum += DailyMeanInsolation(latitude, declination);
            }
            return sum / samples;
        }

        /// <summary>Annual-mean insolation table over latitude (1 degree steps, -90..90) for a tilt in degrees.</summary>
        public static float[] InsolationTable(float tiltDegrees)
        {
            var table = new float[181];
            double tilt = tiltDegrees * Math.PI / 180.0;
            for (int i = 0; i <= 180; i++)
            {
                table[i] = (float)AnnualMeanInsolation((i - 90) * Math.PI / 180.0, tilt);
            }
            return table;
        }

        /// <summary>Linear interpolation in an <see cref="InsolationTable"/>.</summary>
        public static float SampleTable(float[] table, float latitude)
        {
            float x = Mathf.Clamp(latitude * Mathf.Rad2Deg + 90f, 0f, 180f);
            int i = Mathf.Min(179, (int)x);
            return Mathf.Lerp(table[i], table[i + 1], x - i);
        }

        /// <summary>
        /// Prevailing surface wind (tangent vector, |w| about 0.2..1.2) of a simplified three-cell
        /// circulation, blended smoothly with latitude a = |lat|: zonal u = -sin(6a) - 0.2 (trade
        /// easterlies peaking at 15 deg, westerlies at 45 deg, polar easterlies at 75 deg, weak easterly
        /// at the calm belt boundaries so the vector never vanishes), meridional v = -0.35 sin(6a) towards
        /// the equator in the trades and polar cells, poleward in the westerlies. Polar easterlies are
        /// weakened, and the wind fades smoothly within ~9 deg of a pole where east is undefined. A
        /// smooth seed-dependent rotation (at most <paramref name="perturbationDegrees"/>) adds regional
        /// variation without breaking the belts.
        /// </summary>
        public static Vector3 PrevailingWind(Vector3 direction, Vector3 axis, int seed, float perturbationDegrees)
        {
            axis = axis.normalized;
            Vector3 east = Vector3.Cross(axis, direction);
            float cosLatitude = east.magnitude;
            if (cosLatitude < 1e-6f)
            {
                return Vector3.zero;
            }
            east /= cosLatitude;
            Vector3 north = Vector3.Cross(direction, east);
            float latitude = Mathf.Asin(Mathf.Clamp(Vector3.Dot(direction, axis), -1f, 1f));
            float a = Mathf.Abs(latitude);
            float s = Mathf.Sin(6f * a);
            float polar = 1f - 0.4f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(60f * Mathf.Deg2Rad, 75f * Mathf.Deg2Rad, a));
            float u = (-s - 0.2f) * polar;
            float v = -0.35f * s * Mathf.Sign(latitude) * polar;
            float fade = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(cosLatitude / 0.15f));
            if (perturbationDegrees > 0f)
            {
                float angle = perturbationDegrees * Mathf.Deg2Rad * Mathf.Clamp(PlanetNoise.Fractal(direction * 1.6f, PlanetNoise.DeriveSeed(seed, 9101), 2) * 1.6f, -1f, 1f);
                float speed = 1f + 0.2f * PlanetNoise.Fractal(direction * 1.6f, PlanetNoise.DeriveSeed(seed, 9102), 2);
                float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                float ru = u * cos - v * sin, rv = u * sin + v * cos;
                u = ru * speed;
                v = rv * speed;
            }
            Vector3 wind = (east * u + north * v) * fade;
            // Exactly tangent (removes float drift).
            return wind - direction * Vector3.Dot(wind, direction);
        }
    }
}
