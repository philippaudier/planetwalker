using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>
    /// Structured mountain systems (V0.1.1B): deterministic mountain provinces containing
    /// curved mountain ranges, evaluated as a smooth envelope (WHERE mountains are) and
    /// shaped by anisotropic ridged noise (HOW they look).
    /// </summary>
    /// <remarks>
    /// Descriptors are generated once per planet from a hash-based local PRNG (no
    /// UnityEngine.Random) and are immutable afterwards, so sampling is allocation free,
    /// deterministic and thread safe. Ranges are great-circle arcs deformed by a bow, a
    /// meander and a variable width; every quantity depends only on the unit direction, so
    /// cube faces cannot introduce seams. This is procedural geography, not tectonics.
    ///
    /// The output of <see cref="Evaluate"/> is a relative profile in [0, 1]; the caller
    /// scales it by mountainStrength (an artistic fraction of the planet radius, not km).
    /// </remarks>
    public sealed class MountainSystem
    {
        public const int MaxProvinces = 6;
        public const int MaxRanges = 32;

        // Placement.
        const float ProvincesAtFullCoverage = 6f;
        const float IsolatedGroupsAtFullCoverage = 1.5f;
        const float MinActivity = 0.35f;            // per-seed tectonic "activity"
        const float MaxActivity = 1.65f;
        const int PlacementAttempts = 16;
        const float MinPlacementField = 0.08f;       // continental field: on land
        const float BranchProbability = 0.45f;

        // Range geometry (angles in radians on the unit sphere).
        const float MinHalfLength = 0.22f;
        const float MaxHalfLength = 0.55f;
        const float MaxAllowedHalfLength = 0.9f;
        const float CapSafetyMargin = 0.05f;
        const float MinWidth = 0.055f;
        const float MaxWidth = 0.11f;
        const float MaxBowFraction = 0.3f;           // bow amplitude / half length
        const float MeanderFraction = 0.6f;          // meander amplitude / width
        const float AlongNoiseWavelength = 0.22f;    // radians per noise unit along a range
        const float WidthJitter = 0.35f;

        // Cross-section, in units of the local half-width.
        const float FoothillStart = 0.6f;
        const float FoothillReach = 2.6f;
        const float EndFadeStart = 0.5f;             // |s| where endpoints start fading
        const float CenterlineWidth = 0.25f;

        // Regional massifs and passes along a range.
        const float PassFloor = 0.3f;

        // Profile weights (sum to 1 so the profile stays in [0, 1]).
        const float ProvinceWeight = 0.06f;
        const float FoothillWeight = 0.14f;
        const float CoreWeight = 0.8f;
        const float MinCrestShare = 0.5f;            // of CoreWeight, at ruggedness 0
        const float MaxCrestShare = 0.8f;            // at ruggedness 1
        const float ValleyDepth = 0.45f;             // dome reduction in ridge troughs

        // Ridge noise.
        const int RidgeOctaves = 4;
        const float RidgeAnisotropy = 2.5f;          // cross-range frequency multiplier
        const float CrestThreshold = 0.35f;

        const int ProvinceStream = 40;
        const int AlongStream = 41;
        const int RidgeStream = 3;                   // same stream as V0.1.0 mountains
        const int ActivityStream = 42;

        struct Province
        {
            public Vector3 Center;
            public float InvOneMinusCosExtent;
            public float Intensity;
        }

        struct Range
        {
            public Vector3 Center;       // arc midpoint
            public Vector3 Along;        // unit tangent at the midpoint, along the arc
            public Vector3 Normal;       // great-circle plane normal
            public float InvSinHalfLength;
            public float Width;
            public float Bow;
            public float Meander;
            public float MaxCross;       // early rejection on cross-track distance
            public float BoundCos;       // early rejection cap around the midpoint
            public float NoiseScale;
            public float NoiseLane;
            public float Intensity;
        }

        readonly Province[] provinces = new Province[MaxProvinces];
        readonly Range[] ranges = new Range[MaxRanges];
        readonly int provinceCount;
        int rangeCount; // only grows during construction

        readonly int alongSeed;
        readonly int ridgeSeed;
        readonly float ridgeFrequency;
        readonly float ridgeGain;
        readonly float crestSharpness;
        readonly float crestWeight;
        readonly float domeWeight;

        /// <summary>Local deterministic PRNG: hash of (seed, counter).</summary>
        struct HashRandom
        {
            readonly int seed;
            int counter;

            public HashRandom(int seed)
            {
                this.seed = seed;
                counter = 0;
            }

            public float Next()
            {
                return PlanetNoise.Hash01(seed, counter++);
            }

            public float Range(float min, float max)
            {
                return min + (max - min) * Next();
            }
        }

        /// <param name="continents">Used read-only to place provinces on land.</param>
        internal MountainSystem(PlanetDefinition d, PlanetHeightGenerator continents)
        {
            alongSeed = PlanetNoise.DeriveSeed(d.seed, AlongStream);
            ridgeSeed = PlanetNoise.DeriveSeed(d.seed, RidgeStream);
            ridgeFrequency = d.mountainFrequency;

            float rugged = d.mountainRuggedness;
            ridgeGain = Mathf.Lerp(0.4f, 0.55f, rugged);
            crestSharpness = Mathf.Lerp(1.1f, 1.7f, rugged);
            crestWeight = CoreWeight * Mathf.Lerp(MinCrestShare, MaxCrestShare, rugged);
            domeWeight = CoreWeight - crestWeight;

            var random = new HashRandom(PlanetNoise.DeriveSeed(d.seed, ProvinceStream));
            float activity = Mathf.Lerp(MinActivity, MaxActivity, PlanetNoise.Hash01(d.seed, ActivityStream));
            float lengthScale = d.mountainRangeLength;

            int wantedProvinces = Mathf.Min(MaxProvinces,
                Mathf.FloorToInt(d.mountainRangeCoverage * activity * ProvincesAtFullCoverage + random.Next()));
            for (int i = 0; i < wantedProvinces; i++)
            {
                if (!TryPlace(ref random, continents, out Vector3 center))
                {
                    continue;
                }
                float extent = random.Range(0.3f, 0.55f) * Mathf.Sqrt(lengthScale);
                provinces[provinceCount++] = new Province
                {
                    Center = center,
                    InvOneMinusCosExtent = 1f / (1f - Mathf.Cos(extent)),
                    Intensity = random.Range(0.5f, 1f),
                };

                // 1 to 3 roughly parallel ranges sharing a province orientation.
                Vector3 orientation = TangentDirection(center, random.Range(0f, Mathf.PI * 2f));
                int rangesInProvince = 1 + (random.Next() < 0.75f ? 1 : 0) + (random.Next() < 0.35f ? 1 : 0);
                for (int r = 0; r < rangesInProvince && rangeCount < MaxRanges; r++)
                {
                    Vector3 rangeCenter = Move(center, TangentDirection(center, random.Range(0f, Mathf.PI * 2f)),
                        random.Range(0f, 0.5f) * extent);
                    Vector3 along = ProjectToTangent(rangeCenter, Rotate(orientation, center, random.Range(-0.45f, 0.45f)));
                    float halfLength = random.Range(MinHalfLength, MaxHalfLength) * lengthScale;
                    float width = random.Range(MinWidth, MaxWidth);
                    float intensity = random.Range(0.8f, 1f);
                    AddRange(ref random, rangeCenter, along, halfLength, width, intensity);

                    if (random.Next() < BranchProbability && rangeCount < MaxRanges)
                    {
                        AddBranch(ref random, rangeCount - 1, halfLength, width, intensity);
                    }
                }
            }

            // Occasional isolated mountain groups: short, compact massifs.
            int groups = Mathf.FloorToInt(d.mountainRangeCoverage * activity * IsolatedGroupsAtFullCoverage + random.Next());
            for (int i = 0; i < groups && rangeCount < MaxRanges; i++)
            {
                if (TryPlace(ref random, continents, out Vector3 center))
                {
                    AddRange(ref random, center, TangentDirection(center, random.Range(0f, Mathf.PI * 2f)),
                        random.Range(0.06f, 0.14f), random.Range(0.06f, 0.1f), random.Range(0.6f, 0.9f));
                }
            }
        }

        public int ProvinceCount => provinceCount;

        public int RangeCount => rangeCount;

        public Vector3 GetProvinceCenter(int index)
        {
            return provinces[index].Center;
        }

        public Vector3 GetRangeCenter(int index)
        {
            return ranges[index].Center;
        }

        /// <summary>Unit direction perpendicular to the range at its midpoint (across the range).</summary>
        public Vector3 GetRangeNormal(int index)
        {
            return ranges[index].Normal;
        }

        /// <summary>
        /// Relative mountain profile in [0, 1] for a unit direction (before the coastal
        /// suppression and mountainStrength applied by the caller).
        /// <paramref name="coreInfluence"/> is the range core envelope in [0, 1].
        /// </summary>
        public float Evaluate(Vector3 p, out float coreInfluence)
        {
            SampleEnvelope(p, out float core, out float foothills, out float province, out _, out Vector3 crossSum, out float weightSum);
            coreInfluence = core;
            if (core <= 0f && foothills <= 0f && province <= 0f)
            {
                return 0f;
            }

            float profile = ProvinceWeight * province + FoothillWeight * foothills;
            if (core > 0f)
            {
                // Ridges stretched along the local range direction: the cross-range
                // component of the sampling point is amplified, so ridges and the valleys
                // between them run parallel to the range.
                Vector3 q = p;
                if (weightSum > 0f)
                {
                    q += crossSum * ((RidgeAnisotropy - 1f) / weightSum);
                }
                float ridges = PlanetNoise.Ridged(q * ridgeFrequency, ridgeSeed, RidgeOctaves, 2f, ridgeGain);
                float crest = Mathf.Pow(Mathf.Clamp01((ridges - CrestThreshold) / (1f - CrestThreshold)), crestSharpness);
                float dome = core * core * (3f - 2f * core);
                float valley = 1f - ValleyDepth * (1f - Mathf.Clamp01(ridges));
                profile += domeWeight * dome * valley + crestWeight * core * crest;
            }
            return profile;
        }

        /// <summary>Mountain influence in [0, 1]: 0 none, mid foothills, 1 range cores.</summary>
        public float GetInfluence(Vector3 p)
        {
            SampleEnvelope(p, out float core, out float foothills, out float province, out _, out _, out _);
            return 1f - (1f - core) * (1f - 0.5f * foothills) * (1f - 0.25f * province);
        }

        /// <summary>Range centerline mask in [0, 1] (geometry only, for diagnostics).</summary>
        public float GetCenterlineMask(Vector3 p)
        {
            SampleEnvelope(p, out _, out _, out _, out float centerline, out _, out _);
            return centerline;
        }

        void SampleEnvelope(Vector3 p, out float core, out float foothills, out float province, out float centerline,
            out Vector3 crossSum, out float weightSum)
        {
            float coreRemaining = 1f;
            float footRemaining = 1f;
            float provinceRemaining = 1f;
            centerline = 0f;
            crossSum = Vector3.zero;
            weightSum = 0f;

            for (int i = 0; i < provinceCount; i++)
            {
                float q = (1f - Vector3.Dot(p, provinces[i].Center)) * provinces[i].InvOneMinusCosExtent;
                if (q < 1f)
                {
                    provinceRemaining *= 1f - provinces[i].Intensity * (1f - SmoothStep(0f, 1f, q));
                }
            }

            for (int i = 0; i < rangeCount; i++)
            {
                // Early rejection: outside the range's bounding cap, or too far across it.
                if (Vector3.Dot(p, ranges[i].Center) < ranges[i].BoundCos)
                {
                    continue;
                }
                float cross = Vector3.Dot(p, ranges[i].Normal);
                if (Mathf.Abs(cross) > ranges[i].MaxCross)
                {
                    continue;
                }
                // Along-track parameter in [-1, 1] between the endpoints (sin of the arc angle
                // over sin of the half length: no trigonometry needed).
                float s = Vector3.Dot(p, ranges[i].Along) * ranges[i].InvSinHalfLength;
                float absS = Mathf.Abs(s);
                if (absS >= 1f)
                {
                    continue;
                }

                float n = s * ranges[i].NoiseScale;
                float lane = ranges[i].NoiseLane;
                float meander = PlanetNoise.Gradient(n, lane, 0.5f, alongSeed);
                float widthNoise = PlanetNoise.Gradient(n, lane, 3.5f, alongSeed);
                float massifNoise = PlanetNoise.Gradient(n * 0.7f, lane, 6.5f, alongSeed);

                float offset = cross - ranges[i].Bow * (1f - s * s) - ranges[i].Meander * meander;
                float width = ranges[i].Width * (1f + WidthJitter * Mathf.Clamp(widthNoise * 2f, -1f, 1f));
                float distance = Mathf.Abs(offset) / width;
                if (distance >= FoothillReach)
                {
                    continue;
                }

                float endFade = 1f - SmoothStep(EndFadeStart, 1f, absS);
                // Massifs (tall, broad sections) alternate with lower passes.
                float massif = Mathf.Lerp(PassFloor, 1f, SmoothStep(-0.35f, 0.35f, massifNoise));
                float intensity = ranges[i].Intensity * endFade;

                float coreValue = (1f - SmoothStep(0f, 1f, distance)) * intensity * massif;
                float footValue = (1f - SmoothStep(FoothillStart, FoothillReach, distance)) * intensity * Mathf.Lerp(0.6f, 1f, massif);
                coreRemaining *= 1f - coreValue;
                footRemaining *= 1f - footValue;
                centerline = Mathf.Max(centerline, (1f - SmoothStep(0f, CenterlineWidth, distance)) * endFade);
                crossSum += ranges[i].Normal * (cross * footValue);
                weightSum += footValue;
            }

            // Soft union of overlapping ranges: continuous, never exceeds 1.
            core = 1f - coreRemaining;
            foothills = 1f - footRemaining;
            province = 1f - provinceRemaining;
        }

        bool TryPlace(ref HashRandom random, PlanetHeightGenerator continents, out Vector3 center)
        {
            for (int attempt = 0; attempt < PlacementAttempts; attempt++)
            {
                Vector3 candidate = RandomDirection(ref random);
                if (continents.GetContinentalField(candidate) > MinPlacementField)
                {
                    center = candidate;
                    return true;
                }
            }
            center = Vector3.zero;
            return false;
        }

        void AddRange(ref HashRandom random, Vector3 center, Vector3 along, float halfLength, float width, float intensity)
        {
            halfLength = Mathf.Min(halfLength, MaxAllowedHalfLength);
            along = ProjectToTangent(center, along);
            float bow = random.Range(-MaxBowFraction, MaxBowFraction) * halfLength;
            float meander = MeanderFraction * width;
            float maxCross = Mathf.Abs(bow) + meander + width * (1f + WidthJitter) * FoothillReach;
            // Keep the whole envelope inside the bounding cap, and the cap inside the
            // hemisphere where the along-track parameter is valid: no truncation seams.
            halfLength = Mathf.Min(halfLength, Mathf.PI * 0.5f - CapSafetyMargin - maxCross - 0.02f);
            float reach = halfLength + maxCross + 0.02f;
            ranges[rangeCount++] = new Range
            {
                Center = center,
                Along = along,
                Normal = Vector3.Cross(center, along),
                InvSinHalfLength = 1f / Mathf.Sin(halfLength),
                Width = width,
                Bow = bow,
                Meander = meander,
                MaxCross = maxCross,
                BoundCos = Mathf.Cos(reach),
                NoiseScale = halfLength / AlongNoiseWavelength,
                NoiseLane = random.Range(0f, 200f) + 0.37f,
                Intensity = intensity,
            };
        }

        /// <summary>Secondary range starting on the parent arc and diverging from it.</summary>
        void AddBranch(ref HashRandom random, int parentIndex, float parentHalfLength, float parentWidth, float parentIntensity)
        {
            Range parent = ranges[parentIndex];
            float startAngle = random.Range(-0.6f, 0.6f) * parentHalfLength;
            Vector3 start = parent.Center * Mathf.Cos(startAngle) + parent.Along * Mathf.Sin(startAngle);
            Vector3 tangent = -parent.Center * Mathf.Sin(startAngle) + parent.Along * Mathf.Cos(startAngle);
            float sign = random.Next() < 0.5f ? -1f : 1f;
            Vector3 direction = Rotate(tangent, start, sign * random.Range(0.6f, 1.15f));
            float halfLength = parentHalfLength * random.Range(0.35f, 0.6f);
            Vector3 center = start * Mathf.Cos(halfLength) + direction * Mathf.Sin(halfLength);
            Vector3 along = -start * Mathf.Sin(halfLength) + direction * Mathf.Cos(halfLength);
            AddRange(ref random, center, along, halfLength, parentWidth * 0.75f, parentIntensity * 0.75f);
        }

        static Vector3 RandomDirection(ref HashRandom random)
        {
            float z = random.Range(-1f, 1f);
            float phi = random.Range(0f, Mathf.PI * 2f);
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return new Vector3(r * Mathf.Cos(phi), z, r * Mathf.Sin(phi));
        }

        /// <summary>Unit tangent at <paramref name="point"/> at the given azimuth.</summary>
        static Vector3 TangentDirection(Vector3 point, float azimuth)
        {
            Vector3 reference = Mathf.Abs(point.y) < 0.99f ? Vector3.up : Vector3.right;
            Vector3 t1 = Vector3.Cross(point, reference).normalized;
            Vector3 t2 = Vector3.Cross(point, t1);
            return t1 * Mathf.Cos(azimuth) + t2 * Mathf.Sin(azimuth);
        }

        /// <summary>Rotates a tangent vector around the surface normal at <paramref name="point"/>.</summary>
        static Vector3 Rotate(Vector3 tangent, Vector3 point, float angle)
        {
            return tangent * Mathf.Cos(angle) + Vector3.Cross(point, tangent) * Mathf.Sin(angle);
        }

        static Vector3 ProjectToTangent(Vector3 point, Vector3 vector)
        {
            return (vector - point * Vector3.Dot(vector, point)).normalized;
        }

        /// <summary>Moves a point along a great circle by <paramref name="angle"/> radians.</summary>
        static Vector3 Move(Vector3 point, Vector3 tangent, float angle)
        {
            return (point * Mathf.Cos(angle) + tangent * Mathf.Sin(angle)).normalized;
        }

        static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
