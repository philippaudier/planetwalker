using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// Deterministic, allocation-free 3D gradient noise (improved Perlin style) driven by
    /// integer hashing instead of a permutation table: no shared state, no period, and any
    /// integer seed selects an independent noise field.
    /// </summary>
    public static class PlanetNoise
    {
        /// <summary>Derives an independent seed for a given noise layer.</summary>
        public static int DeriveSeed(int seed, int stream)
        {
            unchecked
            {
                return (int)Mix((uint)seed * 0x9E3779B1u + (uint)stream * 0x85EBCA77u + 0x165667B1u);
            }
        }

        /// <summary>Deterministic value in [0, 1) derived from a seed and a stream.</summary>
        public static float Hash01(int seed, int stream)
        {
            unchecked
            {
                return ((uint)DeriveSeed(seed, stream) >> 8) * (1f / 16777216f);
            }
        }

        /// <summary>
        /// Three independent fBm channels sampled at the same point, as a vector. Cheaper
        /// than three <see cref="Fractal"/> calls because the lattice cell and fade
        /// weights are shared between channels.
        /// </summary>
        public static Vector3 FractalVector(Vector3 p, int seedX, int seedY, int seedZ, int octaves,
            float lacunarity = 2f, float gain = 0.5f)
        {
            Vector3 sum = Vector3.zero;
            float amplitude = 1f;
            float amplitudeSum = 0f;
            float frequency = 1f;
            for (int i = 0; i < octaves; i++)
            {
                int offset = unchecked(i * 7919);
                sum += amplitude * GradientVector(p.x * frequency, p.y * frequency, p.z * frequency,
                    unchecked(seedX + offset), unchecked(seedY + offset), unchecked(seedZ + offset));
                amplitudeSum += amplitude;
                amplitude *= gain;
                frequency *= lacunarity;
            }
            return amplitudeSum > 0f ? sum / amplitudeSum : Vector3.zero;
        }

        /// <summary>Three independent <see cref="Gradient"/> channels at one point.</summary>
        public static Vector3 GradientVector(float x, float y, float z, int seedX, int seedY, int seedZ)
        {
            int x0 = FastFloor(x);
            int y0 = FastFloor(y);
            int z0 = FastFloor(z);
            float fx = x - x0;
            float fy = y - y0;
            float fz = z - z0;
            float u = Fade(fx);
            float v = Fade(fy);
            float w = Fade(fz);
            return new Vector3(
                Lattice(x0, y0, z0, fx, fy, fz, u, v, w, seedX),
                Lattice(x0, y0, z0, fx, fy, fz, u, v, w, seedY),
                Lattice(x0, y0, z0, fx, fy, fz, u, v, w, seedZ));
        }

        /// <summary>Coherent gradient noise in roughly [-1, 1].</summary>
        public static float Gradient(float x, float y, float z, int seed)
        {
            int x0 = FastFloor(x);
            int y0 = FastFloor(y);
            int z0 = FastFloor(z);
            float fx = x - x0;
            float fy = y - y0;
            float fz = z - z0;
            return Lattice(x0, y0, z0, fx, fy, fz, Fade(fx), Fade(fy), Fade(fz), seed);
        }

        /// <summary>Trilinear blend of the 8 corner gradients of one lattice cell.</summary>
        static float Lattice(int x0, int y0, int z0, float fx, float fy, float fz, float u, float v, float w, int seed)
        {
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            int z1 = z0 + 1;

            float n000 = Grad(Hash(x0, y0, z0, seed), fx, fy, fz);
            float n100 = Grad(Hash(x1, y0, z0, seed), fx - 1f, fy, fz);
            float n010 = Grad(Hash(x0, y1, z0, seed), fx, fy - 1f, fz);
            float n110 = Grad(Hash(x1, y1, z0, seed), fx - 1f, fy - 1f, fz);
            float n001 = Grad(Hash(x0, y0, z1, seed), fx, fy, fz - 1f);
            float n101 = Grad(Hash(x1, y0, z1, seed), fx - 1f, fy, fz - 1f);
            float n011 = Grad(Hash(x0, y1, z1, seed), fx, fy - 1f, fz - 1f);
            float n111 = Grad(Hash(x1, y1, z1, seed), fx - 1f, fy - 1f, fz - 1f);

            float nx00 = n000 + u * (n100 - n000);
            float nx10 = n010 + u * (n110 - n010);
            float nx01 = n001 + u * (n101 - n001);
            float nx11 = n011 + u * (n111 - n011);
            float nxy0 = nx00 + v * (nx10 - nx00);
            float nxy1 = nx01 + v * (nx11 - nx01);
            return nxy0 + w * (nxy1 - nxy0);
        }

        /// <summary>
        /// Fractal Brownian motion of <see cref="Gradient"/>, normalized by the total
        /// amplitude so the result stays in roughly [-1, 1] regardless of octave count.
        /// Each octave uses its own derived seed to decorrelate the layers.
        /// </summary>
        public static float Fractal(Vector3 p, int seed, int octaves, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f;
            float amplitude = 1f;
            float amplitudeSum = 0f;
            float frequency = 1f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amplitude * Gradient(p.x * frequency, p.y * frequency, p.z * frequency, unchecked(seed + i * 7919));
                amplitudeSum += amplitude;
                amplitude *= gain;
                frequency *= lacunarity;
            }
            return amplitudeSum > 0f ? sum / amplitudeSum : 0f;
        }

        /// <summary>
        /// Ridged multifractal noise in [0, 1]: sharp crests where the base noise crosses
        /// zero. Each octave is weighted by the previous one so that fine ridges appear
        /// mostly on top of large ridges, which reads as mountain ranges instead of
        /// uniform bumps.
        /// </summary>
        public static float Ridged(Vector3 p, int seed, int octaves, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f;
            float amplitude = 1f;
            float amplitudeSum = 0f;
            float frequency = 1f;
            float weight = 1f;
            for (int i = 0; i < octaves; i++)
            {
                float n = 1f - Mathf.Abs(Gradient(p.x * frequency, p.y * frequency, p.z * frequency, unchecked(seed + i * 7919)));
                n *= n;
                n *= weight;
                weight = Mathf.Clamp01(n * 2f);
                sum += n * amplitude;
                amplitudeSum += amplitude;
                amplitude *= gain;
                frequency *= lacunarity;
            }
            return amplitudeSum > 0f ? sum / amplitudeSum : 0f;
        }

        /// <summary>Floor that is correct for negative coordinates.</summary>
        static int FastFloor(float value)
        {
            int truncated = (int)value;
            return value < truncated ? truncated - 1 : truncated;
        }

        static float Fade(float t)
        {
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        static uint Hash(int x, int y, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)seed;
                h = (h ^ (uint)x) * 0x9E3779B1u;
                h = (h ^ (uint)y) * 0x85EBCA77u;
                h = (h ^ (uint)z) * 0xC2B2AE3Du;
                return Mix(h);
            }
        }

        /// <summary>Murmur3 finalizer: full avalanche of the input bits.</summary>
        static uint Mix(uint h)
        {
            unchecked
            {
                h ^= h >> 16;
                h *= 0x85EBCA6Bu;
                h ^= h >> 13;
                h *= 0xC2B2AE35u;
                h ^= h >> 16;
                return h;
            }
        }

        /// <summary>Dot product with one of the 12 cube-edge gradients (Perlin 2002).</summary>
        static float Grad(uint hash, float x, float y, float z)
        {
            switch (hash >> 28)
            {
                case 0: return x + y;
                case 1: return -x + y;
                case 2: return x - y;
                case 3: return -x - y;
                case 4: return x + z;
                case 5: return -x + z;
                case 6: return x - z;
                case 7: return -x - z;
                case 8: return y + z;
                case 9: return -y + z;
                case 10: return y - z;
                case 11: return -y - z;
                case 12: return x + y;
                case 13: return -x + y;
                case 14: return -y + z;
                default: return -y - z;
            }
        }
    }
}
