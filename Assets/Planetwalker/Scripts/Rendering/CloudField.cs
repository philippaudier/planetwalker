using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Planetwalker.Core;
using Planetwalker.Generation;
using UnityEngine;

namespace Planetwalker.Rendering
{
    /// <summary>One deterministic weather system: a cyclonic spiral bounded by its radius.</summary>
    public struct WeatherSystem
    {
        public Vector3 Center;
        public Vector3 East;
        public Vector3 North;
        public float RadiusDegrees;
        public int Arms;
        public float Tightness;
        /// <summary>+1 northern hemisphere (counterclockwise seen from above), -1 southern.</summary>
        public float Hand;
        public float Phase;
        public float Strength;
        public bool Tropical;
    }

    /// <summary>
    /// Planetary cloud data (V0.1.2C): a synthetic, deterministic cloud snapshot, independent of the
    /// camera and of the terrain mesh, defined on the texels of a cube map (sampled in directions, so
    /// the GPU's seamless cube filtering keeps every face border continuous). Four channels:
    /// Base (large-scale cloud potential), Systems (weather-system cloud pattern), Convective (tropical
    /// convective clusters), Envelope (where a weather system governs the clouds).
    /// The renderer adds the local scale (broken edges, holes, internal texture, puffy convective
    /// cells) per pixel.
    /// </summary>
    /// <remarks>
    /// Climate as a prior, not as weather: the annual-mean climate (precipitation index, relative
    /// humidity = moisture / capacity(T), temperature) only biases where clouds are likely; latitude
    /// tendencies (equatorial convergence, dry subtropics, midlatitude storm belts) and seed-derived
    /// weather variability (planetary noise, warped synoptic noise, zonally stretched frontal bands,
    /// convective clusters, bounded cyclones) decide the actual snapshot, so the clouds are not a
    /// recolored precipitation map. Climate fields are read through the smooth climate sampler (no
    /// climate-cell blocks). Without a climate result (classic palette, climate never computed) the
    /// prior is latitude-only: showing clouds never forces a climate computation.
    ///
    /// Weather systems govern their area: inside the envelope the potential moves towards the system's
    /// own pattern (cloudy spiral arms and comma head, clear dry slots, clear tropical eye), so the
    /// structures read as systems instead of being lost in the noise. Coverage is an area fraction:
    /// the threshold is the area-weighted quantile of the deterministic potential (fixed point, since
    /// systems are expressed relative to it), so 45% coverage covers about 45% of the sphere.
    /// </remarks>
    public sealed class CloudField
    {
        public const int Resolution = 128;
        /// <summary>Width of the soft cloud edge in potential units (alpha 0 .. 1 over it).</summary>
        public const float Softness = 0.14f;
        /// <summary>Mean potential added by convective clusters (times the Convection setting).</summary>
        public const float ConvectiveWeight = 0.25f;
        /// <summary>How strongly a system governs its envelope (times the Weather Systems setting).</summary>
        public const float SystemWeight = 1.4f;

        readonly List<WeatherSystem> systems = new List<WeatherSystem>();
        readonly int noiseBase, noiseSynoptic, noiseBand, noiseCluster, warpX, warpY, warpZ;

        public CloudField(int weatherSeed, ClimateResult climate, Vector3 axis)
        {
            var watch = Stopwatch.StartNew();
            Seed = weatherSeed;
            Climate = climate;
            Axis = axis.sqrMagnitude > 0f ? axis.normalized : Vector3.up;
            noiseBase = PlanetNoise.DeriveSeed(weatherSeed, 9501);
            noiseSynoptic = PlanetNoise.DeriveSeed(weatherSeed, 9502);
            noiseBand = PlanetNoise.DeriveSeed(weatherSeed, 9503);
            noiseCluster = PlanetNoise.DeriveSeed(weatherSeed, 9507);
            warpX = PlanetNoise.DeriveSeed(weatherSeed, 9504);
            warpY = PlanetNoise.DeriveSeed(weatherSeed, 9505);
            warpZ = PlanetNoise.DeriveSeed(weatherSeed, 9506);
            GenerateSystems();

            int n = Resolution, count = 6 * n * n;
            Base = new float[count];
            Systems = new float[count];
            Envelope = new float[count];
            Convective = new float[count];
            TexelWeight = new float[count];
            Parallel.For(0, 6, face =>
            {
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        int i = face * n * n + y * n + x;
                        float u = 2f * (x + 0.5f) / n - 1f, v = 2f * (y + 0.5f) / n - 1f;
                        Vector3 d = TexelDirection(face, u, v).normalized;
                        Vector3 warp = PlanetNoise.FractalVector(d * 2.2f, warpX, warpY, warpZ, 2) * 0.25f;
                        float prior = Prior(d, out float convective);
                        Base[i] = BaseAt(d, warp, prior);
                        Systems[i] = SystemsAt(d, out float envelope);
                        Envelope[i] = envelope;
                        // Convective clusters (mesoscale systems) separated by clear gaps, not a uniform belt.
                        float cluster = PlanetNoise.Fractal((d + warp) * 7f, noiseCluster, 3);
                        Convective[i] = convective * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.05f, 0.35f, cluster));
                        TexelWeight[i] = 1f / Mathf.Pow(1f + u * u + v * v, 1.5f);
                    }
                }
            });
            Milliseconds = (float)watch.Elapsed.TotalMilliseconds;
        }

        public int Seed { get; }
        public ClimateResult Climate { get; }
        public bool ClimateInformed => Climate != null;
        public Vector3 Axis { get; }
        public readonly float[] Base;
        public readonly float[] Systems;
        public readonly float[] Envelope;
        public readonly float[] Convective;
        /// <summary>Texel solid angle (relative).</summary>
        public readonly float[] TexelWeight;
        public IReadOnlyList<WeatherSystem> WeatherSystems => systems;
        public float Milliseconds { get; }

        // ------------------------------------------------------------------ Cube map layout

        /// <summary>
        /// Direction of face coordinates (u, v in [-1, 1], v downwards) in Unity's cube map layout
        /// (verified on the GPU by the cloud tests).
        /// </summary>
        public static Vector3 TexelDirection(int face, float u, float v)
        {
            switch ((CubemapFace)face)
            {
                case CubemapFace.PositiveX: return new Vector3(1f, -v, -u);
                case CubemapFace.NegativeX: return new Vector3(-1f, -v, u);
                case CubemapFace.PositiveY: return new Vector3(u, 1f, v);
                case CubemapFace.NegativeY: return new Vector3(u, -1f, -v);
                case CubemapFace.PositiveZ: return new Vector3(u, -v, 1f);
                default: return new Vector3(-u, -v, -1f);
            }
        }

        /// <summary>Inverse of <see cref="TexelDirection"/>.</summary>
        public static int FaceCoordinates(Vector3 d, out float u, out float v)
        {
            float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
            if (ax >= ay && ax >= az)
            {
                u = (d.x > 0f ? -d.z : d.z) / ax;
                v = -d.y / ax;
                return d.x > 0f ? 0 : 1;
            }
            if (ay >= az)
            {
                u = d.x / ay;
                v = (d.y > 0f ? d.z : -d.z) / ay;
                return d.y > 0f ? 2 : 3;
            }
            u = (d.z > 0f ? d.x : -d.x) / az;
            v = -d.y / az;
            return d.z > 0f ? 4 : 5;
        }

        /// <summary>Bilinear CPU sample of a texel field (debug views, tests; the GPU filters seamlessly).</summary>
        public float Sample(float[] field, Vector3 direction)
        {
            int n = Resolution;
            int face = FaceCoordinates(direction, out float u, out float v);
            float fx = Mathf.Clamp((u + 1f) * 0.5f * n - 0.5f, 0f, n - 1f), fy = Mathf.Clamp((v + 1f) * 0.5f * n - 0.5f, 0f, n - 1f);
            int x0 = Mathf.Min((int)fx, n - 2), y0 = Mathf.Min((int)fy, n - 2);
            float tx = fx - x0, ty = fy - y0;
            int i = face * n * n + y0 * n + x0;
            float a = Mathf.Lerp(field[i], field[i + 1], tx), b = Mathf.Lerp(field[i + n], field[i + n + 1], tx);
            return Mathf.Lerp(a, b, ty);
        }

        /// <summary>RGBA texels of one face: R base, G systems, B convective, A envelope.</summary>
        public void FillFace(int face, Color[] pixels)
        {
            int n = Resolution;
            for (int k = 0; k < n * n; k++)
            {
                int i = face * n * n + k;
                pixels[k] = new Color(Base[i], Systems[i], Convective[i], Envelope[i]);
            }
        }

        // ------------------------------------------------------------------ Coverage

        /// <summary>
        /// Deterministic cloud potential (the renderer adds zero-mean local detail): base plus convective
        /// clusters, moved towards the weather system's pattern (around the threshold) inside its envelope.
        /// Mirrored by CloudPotential in PlanetCloudsCommon.hlsl.
        /// </summary>
        public static float Potential(float baseValue, float systemsValue, float convectiveValue, float envelope, CloudSettings s, float threshold)
        {
            float background = baseValue + s.convection * ConvectiveWeight * convectiveValue;
            float system = threshold + (systemsValue - 0.4f);
            return Mathf.Lerp(background, system, Mathf.Clamp01(envelope * s.weatherStrength * SystemWeight));
        }

        float PotentialAt(int i, CloudSettings s, float threshold) => Potential(Base[i], Systems[i], Convective[i], Envelope[i], s, threshold);

        /// <summary>
        /// Potential threshold for the settings: area-weighted quantile so that the covered area
        /// (alpha above one half) is the coverage fraction (three fixed-point passes, since the systems
        /// are expressed relative to the threshold). Above every potential at zero coverage.
        /// </summary>
        public float Threshold(CloudSettings s)
        {
            if (s.coverage <= 0f)
            {
                return 1e4f;
            }
            if (s.coverage >= 1f)
            {
                return -1e4f;
            }
            float threshold = 0.4f;
            for (int pass = 0; pass < 3; pass++)
            {
                threshold = Quantile(s, threshold, 1f - s.coverage);
            }
            return threshold;
        }

        float Quantile(CloudSettings s, float threshold, float fraction)
        {
            int count = Base.Length;
            float min = float.MaxValue, max = float.MinValue;
            double total = 0;
            for (int i = 0; i < count; i++)
            {
                float p = PotentialAt(i, s, threshold);
                min = Mathf.Min(min, p);
                max = Mathf.Max(max, p);
                total += TexelWeight[i];
            }
            const int bins = 4096;
            var histogram = new double[bins];
            float scale = (bins - 1) / Mathf.Max(1e-6f, max - min);
            for (int i = 0; i < count; i++)
            {
                histogram[(int)((PotentialAt(i, s, threshold) - min) * scale)] += TexelWeight[i];
            }
            double target = total * (1f - fraction), above = 0;
            for (int b = bins - 1; b >= 0; b--)
            {
                above += histogram[b];
                if (above >= target)
                {
                    return min + b / scale;
                }
            }
            return min;
        }

        /// <summary>Cloud alpha (before density and local detail) of the deterministic potential at a direction.</summary>
        public float Coverage(Vector3 direction, CloudSettings s, float threshold)
        {
            float p = Potential(Sample(Base, direction), Sample(Systems, direction), Sample(Convective, direction), Sample(Envelope, direction), s, threshold);
            return Mathf.Clamp01((p - threshold) / Softness + 0.5f);
        }

        /// <summary>Area-weighted mean cloud alpha (before density and local detail).</summary>
        public float MeanCoverage(CloudSettings s, float threshold)
        {
            double sum = 0, total = 0;
            for (int i = 0; i < Base.Length; i++)
            {
                sum += Mathf.Clamp01((PotentialAt(i, s, threshold) - threshold) / Softness + 0.5f) * TexelWeight[i];
                total += TexelWeight[i];
            }
            return (float)(sum / total);
        }

        // ------------------------------------------------------------------ Generation

        /// <summary>
        /// Climate prior in [0, 1] (where clouds are likely) and the convective potential. Climate-informed
        /// when a climate result is available, latitude-only otherwise.
        /// </summary>
        float Prior(Vector3 d, out float convective)
        {
            float latitude = Mathf.Abs(ClimateForcing.Latitude(d, Axis)) * Mathf.Rad2Deg;
            float zones = LatitudeTendency(latitude);
            if (Climate == null)
            {
                convective = Mathf.Exp(-Sq(latitude / 13f));
                return Mathf.Clamp01(0.5f + zones);
            }
            float precipitation = Climate.Sample(Climate.Precipitation, d);
            float temperature = Climate.Sample(Climate.Temperature, d);
            float moisture = Climate.Sample(Climate.Moisture, d);
            float humidity = Mathf.Clamp01(moisture / Mathf.Max(1e-3f, ClimateGenerator.Capacity(temperature)));
            float wet = Mathf.Clamp01(0.5f + 0.3f * Mathf.Log(Mathf.Max(precipitation, 0.05f), 2f));
            convective = Mathf.Clamp01((temperature - 20f) / 7f) * Mathf.Clamp01((wet - 0.35f) / 0.45f);
            return Mathf.Clamp01(0.4f * wet + 0.15f * humidity + 0.45f * (0.5f + zones));
        }

        /// <summary>Latitude tendencies (not masks): equatorial convergence, dry subtropics, midlatitude storm belts.</summary>
        public static float LatitudeTendency(float latitudeDegrees)
        {
            float a = latitudeDegrees;
            return 0.12f * Mathf.Exp(-Sq(a / 9f)) + 0.2f * Mathf.Exp(-Sq((a - 55f) / 14f)) - 0.2f * Mathf.Exp(-Sq((a - 25f) / 9f));
        }

        float BaseAt(Vector3 d, Vector3 warp, float prior)
        {
            float latitude = Mathf.Abs(ClimateForcing.Latitude(d, Axis)) * Mathf.Rad2Deg;
            float planetary = PlanetNoise.Fractal(d * 1.7f, noiseBase, 3);
            float synoptic = PlanetNoise.Fractal((d + warp) * 5.5f, noiseSynoptic, 4);
            // Frontal bands: noise compressed along the axis (features stretched east-west), only in the
            // midlatitudes (no rings around the poles).
            float belt = Mathf.Exp(-Sq((latitude - 50f) / 14f));
            Vector3 q = d + warp * 0.5f;
            q += Axis * (Vector3.Dot(q, Axis) * 2.5f);
            float band = PlanetNoise.Fractal(q * 4f, noiseBand, 3);
            return 0.3f * prior + 0.25f * planetary + 0.45f * synoptic + 0.3f * belt * band;
        }

        void GenerateSystems()
        {
            Vector3 e1 = Vector3.Cross(Axis, Mathf.Abs(Axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 e2 = Vector3.Cross(Axis, e1);
            int midlatitude = 5 + Mathf.FloorToInt(PlanetNoise.Hash01(Seed, 9600) * 4f); // 5 .. 8
            int tropical = Mathf.FloorToInt(PlanetNoise.Hash01(Seed, 9601) * 3f);       // 0 .. 2
            int stream = 9610;
            for (int k = 0; k < midlatitude + tropical; k++)
            {
                bool isTropical = k >= midlatitude;
                for (int attempt = 0; attempt < 24; attempt++)
                {
                    float h1 = PlanetNoise.Hash01(Seed, stream++), h2 = PlanetNoise.Hash01(Seed, stream++), h3 = PlanetNoise.Hash01(Seed, stream++);
                    float hemisphere = h1 < 0.5f ? 1f : -1f;
                    float latitude = hemisphere * (isTropical ? 10f + 12f * h2 : 38f + 24f * h2) * Mathf.Deg2Rad;
                    float longitude = h3 * 2f * Mathf.PI;
                    Vector3 c = (Axis * Mathf.Sin(latitude) + (e1 * Mathf.Cos(longitude) + e2 * Mathf.Sin(longitude)) * Mathf.Cos(latitude)).normalized;
                    float spacing = isTropical ? 20f : 28f;
                    bool free = true;
                    foreach (WeatherSystem other in systems)
                    {
                        if (Vector3.Angle(other.Center, c) < spacing)
                        {
                            free = false;
                            break;
                        }
                    }
                    if (!free)
                    {
                        continue;
                    }
                    float h4 = PlanetNoise.Hash01(Seed, stream++), h5 = PlanetNoise.Hash01(Seed, stream++), h6 = PlanetNoise.Hash01(Seed, stream++);
                    Vector3 east = Vector3.Cross(Axis, c).normalized;
                    systems.Add(new WeatherSystem
                    {
                        Center = c,
                        East = east,
                        North = Vector3.Cross(c, east),
                        RadiusDegrees = isTropical ? 3.5f + 2.5f * h4 : 9f + 6f * h4,
                        Arms = isTropical ? 2 + (h5 < 0.5f ? 0 : 1) : (h5 < 0.8f ? 1 : 2),
                        Tightness = isTropical ? 1.5f + 0.7f * h6 : 0.9f + 0.7f * h6,
                        Hand = hemisphere,
                        Phase = h5 * 2f * Mathf.PI,
                        Strength = 0.6f + 0.4f * h6,
                        Tropical = isTropical,
                    });
                    break;
                }
            }
        }

        /// <summary>
        /// Weather-system cloud pattern in [0, 1] (spiral arms and comma head cloudy, dry slots and the
        /// tropical eye clear) and its envelope in [0, 1] (zero beyond ~3 radii of every system); the
        /// system with the largest envelope wins where systems overlap.
        /// </summary>
        public float SystemsAt(Vector3 d, out float envelope)
        {
            float pattern = 0f;
            envelope = 0f;
            foreach (WeatherSystem s in systems)
            {
                float cos = Vector3.Dot(d, s.Center);
                float reach = Mathf.Cos(Mathf.Min(170f, 3f * s.RadiusDegrees) * Mathf.Deg2Rad);
                if (cos < reach)
                {
                    continue;
                }
                float angle = Mathf.Acos(Mathf.Clamp(cos, -1f, 1f)) * Mathf.Rad2Deg;
                float x = Vector3.Dot(d, s.East), y = Vector3.Dot(d, s.North);
                float phi = Mathf.Atan2(y, x);
                // Midlatitude systems are stretched east-west (frontal tail); tropical ones are round.
                float stretch = s.Tropical ? 1f : 1f + 0.4f * Mathf.Abs(Mathf.Cos(phi));
                // Organic, not geometric: noise perturbs the radius and the arm phase.
                Vector3 warp = PlanetNoise.FractalVector(d * (110f / s.RadiusDegrees), warpX, warpY, warpZ, 2);
                float rho = angle / (s.RadiusDegrees * stretch) * (1f + 0.18f * warp.x);
                float spiral = 0.5f + 0.5f * Mathf.Cos(s.Arms * (s.Hand * phi - s.Tightness * Mathf.Log(rho + 0.08f)) + s.Phase + 1.1f * warp.y);
                // Broad arms (cloud shields), broken by noise, not thin drawn lines.
                float arms = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 0.65f, spiral)) * Mathf.Clamp01(0.75f + 0.6f * warp.z);
                float core = 0.9f * Mathf.Exp(-Sq(rho / (s.Tropical ? 0.35f : 0.2f))) * (0.55f + 0.45f * spiral); // curled head
                float value = Mathf.Max(arms, core);
                if (s.Tropical)
                {
                    value *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.07f, 0.16f, rho)); // clear eye
                }
                // Smooth fade to zero at 3 radii (bounded).
                float e = Mathf.Exp(-rho * rho * 0.7f) * Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(2.2f, 3f, angle / s.RadiusDegrees)) * s.Strength;
                if (e > envelope)
                {
                    envelope = e;
                    pattern = value;
                }
            }
            return pattern;
        }

        static float Sq(float x) => x * x;
    }

    /// <summary>Cloud field cache: rebuilt only for a new weather seed, climate result or rotation axis.</summary>
    public sealed class CloudFieldCache
    {
        CloudField cached;

        public int ComputeCount { get; private set; }
        public CloudField Current => cached;

        public CloudField Get(int weatherSeed, ClimateResult climate, Vector3 axis)
        {
            if (cached != null && cached.Seed == weatherSeed && cached.Climate == climate && cached.Axis == axis.normalized)
            {
                return cached;
            }
            cached = new CloudField(weatherSeed, climate, axis);
            ComputeCount++;
            return cached;
        }

        public void Clear()
        {
            cached = null;
        }
    }
}
