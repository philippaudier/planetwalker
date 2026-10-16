using System;
using System.Globalization;
using UnityEngine;

namespace Planetwalker.Core
{
    /// <summary>
    /// Double-precision vector (V0.2.0C): planet-centered positions, render-origin offsets and tangent frames.
    /// Unity's <see cref="Vector3"/> keeps about 7 significant digits: at the surface of a planet whose radius is
    /// 100 render units that is 0.5 m at Earth scale, far too coarse for close-range exploration. Everything that
    /// positions the camera or a terrain patch relative to the planet center is computed here and rounded to float
    /// only once, relative to a nearby render origin. Pure managed math: usable on worker threads, deterministic.
    /// </summary>
    [Serializable]
    public struct Vector3d : IEquatable<Vector3d>
    {
        public double x, y, z;

        public Vector3d(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public Vector3d(Vector3 v)
        {
            x = v.x;
            y = v.y;
            z = v.z;
        }

        public static readonly Vector3d Zero = new Vector3d(0, 0, 0);
        public static readonly Vector3d Right = new Vector3d(1, 0, 0);
        public static readonly Vector3d Up = new Vector3d(0, 1, 0);
        public static readonly Vector3d Forward = new Vector3d(0, 0, 1);

        public double SqrMagnitude => x * x + y * y + z * z;
        public double Magnitude => Math.Sqrt(x * x + y * y + z * z);

        /// <summary>Unit vector (zero stays zero).</summary>
        public Vector3d Normalized
        {
            get
            {
                double m = Magnitude;
                return m > 0 ? new Vector3d(x / m, y / m, z / m) : Zero;
            }
        }

        /// <summary>Rounded to single precision (the only place a double position becomes a Unity vector).</summary>
        public Vector3 ToVector3() => new Vector3((float)x, (float)y, (float)z);

        public static double Dot(Vector3d a, Vector3d b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static Vector3d Cross(Vector3d a, Vector3d b) =>
            new Vector3d(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

        public static double Distance(Vector3d a, Vector3d b) => (a - b).Magnitude;

        public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3d operator -(Vector3d a) => new Vector3d(-a.x, -a.y, -a.z);
        public static Vector3d operator *(Vector3d a, double s) => new Vector3d(a.x * s, a.y * s, a.z * s);
        public static Vector3d operator *(double s, Vector3d a) => new Vector3d(a.x * s, a.y * s, a.z * s);
        public static Vector3d operator /(Vector3d a, double s) => new Vector3d(a.x / s, a.y / s, a.z / s);

        public static explicit operator Vector3(Vector3d v) => v.ToVector3();
        public static implicit operator Vector3d(Vector3 v) => new Vector3d(v);

        public bool Equals(Vector3d other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is Vector3d other && Equals(other);
        public override int GetHashCode() => unchecked(x.GetHashCode() * 31 * 31 + y.GetHashCode() * 31 + z.GetHashCode());
        public static bool operator ==(Vector3d a, Vector3d b) => a.Equals(b);
        public static bool operator !=(Vector3d a, Vector3d b) => !a.Equals(b);

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:R}, {1:R}, {2:R})", x, y, z);
    }
}
