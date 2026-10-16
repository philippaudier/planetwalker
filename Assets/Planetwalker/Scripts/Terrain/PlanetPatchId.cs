using System;
using System.Globalization;
using Planetwalker.Generation;

namespace Planetwalker.Terrain
{
    /// <summary>Cardinal directions in a cube face's own frame: East = +A, North = +B (see <see cref="CubeSphereGenerator.GetFaceAxes"/>).</summary>
    public enum PatchDirection
    {
        East = 0,
        North = 1,
        West = 2,
        South = 3,
    }

    /// <summary>
    /// Stable address of a terrain patch (V0.2.0A): cube face, subdivision level and integer position. Pure
    /// integers (no floating point in the identity), packed into one 64-bit key for hashing and equality.
    /// </summary>
    /// <remarks>
    /// Face numbering is the existing one (<see cref="CubeSphereGenerator"/>: +Y, -Y, -X, +X, +Z, -Z). At level L a
    /// face is a 2^L x 2^L grid of patches; x grows along the face's A axis ("east"), y along its B axis
    /// ("north"); (0, 0) is the south-west corner, i.e. face coordinates (-1, -1). Children are numbered
    /// 0 = south-west, 1 = south-east, 2 = north-west, 3 = north-east (child (2x + (i &amp; 1), 2y + (i &gt;&gt; 1))).
    /// "North / east" are face-local: they change orientation from one face to the next.
    /// Key layout: face (3 bits) | level (5 bits) | x (24 bits) | y (24 bits). No two valid patches share a key.
    /// </remarks>
    public readonly struct PlanetPatchId : IEquatable<PlanetPatchId>, IComparable<PlanetPatchId>
    {
        /// <summary>Deepest level an identifier can address (x, y &lt; 2^24).</summary>
        public const int MaxLevel = 24;

        readonly ulong key;

        public PlanetPatchId(int face, int level, int x, int y)
        {
            if ((uint)face >= CubeSphereGenerator.FaceCount)
            {
                throw new ArgumentOutOfRangeException(nameof(face), face, "Face must be in [0, 5].");
            }
            if ((uint)level > MaxLevel)
            {
                throw new ArgumentOutOfRangeException(nameof(level), level, "Level must be in [0, " + MaxLevel + "].");
            }
            int size = 1 << level;
            if ((uint)x >= (uint)size || (uint)y >= (uint)size)
            {
                throw new ArgumentOutOfRangeException(nameof(x), "Patch (" + x + ", " + y + ") is outside the 2^" + level + " grid.");
            }
            key = ((ulong)(uint)face << 61) | ((ulong)(uint)level << 56) | ((ulong)(uint)x << 24) | (uint)y;
        }

        PlanetPatchId(ulong key)
        {
            this.key = key;
        }

        public int Face => (int)(key >> 61);
        public int Level => (int)((key >> 56) & 0x1F);
        public int X => (int)((key >> 24) & 0xFFFFFF);
        public int Y => (int)(key & 0xFFFFFF);

        /// <summary>Packed identity (stable across sessions and machines).</summary>
        public ulong Key => key;

        /// <summary>Patches per face edge at this level (2^level).</summary>
        public int GridSize => 1 << Level;

        public static PlanetPatchId Root(int face) => new PlanetPatchId(face, 0, 0, 0);

        public static PlanetPatchId FromKey(ulong key)
        {
            var id = new PlanetPatchId(key);
            // Re-validates through the public constructor (throws on an invalid key).
            return new PlanetPatchId(id.Face, id.Level, id.X, id.Y);
        }

        public bool HasParent => Level > 0;

        public PlanetPatchId Parent
        {
            get
            {
                if (Level == 0)
                {
                    throw new InvalidOperationException("A level-0 patch (whole cube face) has no parent.");
                }
                return new PlanetPatchId(Face, Level - 1, X >> 1, Y >> 1);
            }
        }

        public bool CanSubdivide => Level < MaxLevel;

        /// <summary>Child 0..3: 0 = south-west, 1 = south-east, 2 = north-west, 3 = north-east.</summary>
        public PlanetPatchId Child(int index)
        {
            if ((uint)index > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            if (!CanSubdivide)
            {
                throw new InvalidOperationException("Maximum level reached.");
            }
            return new PlanetPatchId(Face, Level + 1, (X << 1) | (index & 1), (Y << 1) | (index >> 1));
        }

        /// <summary>Which child of its parent this patch is (0..3).</summary>
        public int ChildIndex => Level == 0 ? -1 : (X & 1) | ((Y & 1) << 1);

        /// <summary>Ancestor at a coarser (or equal) level.</summary>
        public PlanetPatchId AncestorAt(int level)
        {
            if (level < 0 || level > Level)
            {
                throw new ArgumentOutOfRangeException(nameof(level));
            }
            int shift = Level - level;
            return new PlanetPatchId(Face, level, X >> shift, Y >> shift);
        }

        /// <summary>True when <paramref name="other"/> is this patch or lies inside it.</summary>
        public bool Contains(PlanetPatchId other)
        {
            return other.Face == Face && other.Level >= Level && other.AncestorAt(Level).key == key;
        }

        public bool Equals(PlanetPatchId other) => key == other.key;
        public override bool Equals(object obj) => obj is PlanetPatchId other && other.key == key;

        public override int GetHashCode()
        {
            // 64-bit mix (splitmix64 finalizer) folded to 32 bits: well spread for dictionaries.
            ulong z = key + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (int)(z ^ (z >> 32));
        }

        public int CompareTo(PlanetPatchId other) => key.CompareTo(other.key);

        public static bool operator ==(PlanetPatchId a, PlanetPatchId b) => a.key == b.key;
        public static bool operator !=(PlanetPatchId a, PlanetPatchId b) => a.key != b.key;

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} L{1} ({2}, {3})", CubeSphereGenerator.GetFaceName(Face), Level, X, Y);
        }

        public static PatchDirection Opposite(PatchDirection direction) => (PatchDirection)(((int)direction + 2) & 3);
    }
}
