using System;
using System.Threading.Tasks;
using Planetwalker.Core;
using UnityEngine;

namespace Planetwalker.Generation
{
    /// <summary>What the base (planetary) terrain knows at one direction: the context the detail layers read.</summary>
    public struct TerrainBaseSample
    {
        /// <summary>Planetary elevation e0 (fraction of the radius): continents, mountain systems, V0.1 detail.</summary>
        public float Elevation;
        /// <summary>Continental field (coastline at 0).</summary>
        public float Continental;
        /// <summary>Mountain range core envelope in [0, 1], coastal suppression included.</summary>
        public float Core;
        /// <summary>Mountain influence in [0, 1] (foothills mid), coastal suppression included.</summary>
        public float Influence;
        /// <summary>Cross-range stretch vector of the ridges (zero outside ranges).</summary>
        public Vector3 Anisotropy;
    }

    /// <summary>Contribution of each detail band at a direction (fraction of the radius), for diagnostics.</summary>
    public struct TerrainDetailSample
    {
        public float Regional, Local, Micro;
        public float Total => Regional + Local + Micro;
    }

    /// <summary>The three detail bands of V0.2.0C, by physical wavelength.</summary>
    public enum TerrainDetailBand
    {
        /// <summary>Broad valleys, secondary ridges, plateaus: 20 km and above.</summary>
        Regional = 0,
        /// <summary>Hills, ravines, rocky ridges: 1.5-20 km.</summary>
        Local = 1,
        /// <summary>Near-surface relief: below 1.5 km.</summary>
        Micro = 2,
    }

    /// <summary>
    /// Octaves of the surface detail (V0.2.0C) and their amplitude bounds. The spectrum continues the planetary
    /// terrain's own finest octave (the V0.1 detail layer, <c>detailFrequency x 8</c>) with a gain of
    /// 2^-0.8 per octave (Hurst exponent 0.8, the roughness of natural terrain at these scales), each octave
    /// doubling the frequency, down to a wavelength of <see cref="MinWavelengthMeters"/> at the planet's physical
    /// scale (<see cref="PlanetScale"/>). Amplitudes are fractions of the radius, so the detail is physically
    /// plausible (hundreds of meters at 100 km, meters at 100 m) under the artistically exaggerated planetary
    /// relief. Octaves are grouped into bands by physical wavelength; each band has a user multiplier.
    /// Also provides the conservative bounds the LOD error model needs.
    /// </summary>
    public sealed class TerrainDetailProfile
    {
        public const double MinWavelengthMeters = 80.0;
        public const double RegionalMinWavelengthMeters = 20000.0;
        public const double LocalMinWavelengthMeters = 1500.0;
        public const int MaxOctaves = 14;
        /// <summary>Amplitude ratio between consecutive octaves (2^-0.8).</summary>
        public const float OctaveGain = 0.5743492f;
        /// <summary>Largest mask product applied to a band's amplitude (mountain boost x rugged character).</summary>
        public const float MaxBoost = 3f * 1.3f;
        /// <summary>Peak slope of unit gradient noise per lattice cell (rise over run, measured: ~2.7).</summary>
        public const float NoisePeakSlopePerCell = 3f;

        public TerrainDetailProfile(PlanetScale scale, float detailFrequency, float detailStrength, float regional, float local, float micro)
        {
            Scale = scale;
            Multipliers = new[] { regional, local, micro };
            // The V0.1 detail layer: 4 octaves from detailFrequency, gain 0.5, normalized by 1.875: its finest
            // octave is the anchor the new spectrum continues.
            float anchorFrequency = detailFrequency * 8f;
            float anchorAmplitude = detailStrength * (0.125f / 1.875f);
            var frequencies = new float[MaxOctaves];
            var amplitudes = new float[MaxOctaves];
            var bands = new TerrainDetailBand[MaxOctaves];
            int count = 0;
            float frequency = anchorFrequency, amplitude = anchorAmplitude;
            while (count < MaxOctaves)
            {
                frequency *= 2f;
                amplitude *= OctaveGain;
                double wavelength = scale.WavelengthOfFrequency(frequency);
                if (wavelength < MinWavelengthMeters)
                {
                    break;
                }
                frequencies[count] = frequency;
                amplitudes[count] = amplitude;
                bands[count] = wavelength >= RegionalMinWavelengthMeters ? TerrainDetailBand.Regional
                    : wavelength >= LocalMinWavelengthMeters ? TerrainDetailBand.Local : TerrainDetailBand.Micro;
                count++;
            }
            OctaveCount = count;
            Frequencies = new float[count];
            Amplitudes = new float[count];
            Bands = new TerrainDetailBand[count];
            Array.Copy(frequencies, Frequencies, count);
            Array.Copy(amplitudes, Amplitudes, count);
            Array.Copy(bands, Bands, count);
            BandAmplitude = new float[3];
            for (int k = 0; k < count; k++)
            {
                Amplitudes[k] *= Multipliers[(int)Bands[k]];
                BandAmplitude[(int)Bands[k]] += Amplitudes[k];
            }
        }

        public PlanetScale Scale { get; }
        public int OctaveCount { get; }
        /// <summary>Cycles per radian of each octave.</summary>
        public float[] Frequencies { get; }
        /// <summary>Base amplitude (fraction of the radius) of each octave, user multiplier applied, masks not applied.</summary>
        public float[] Amplitudes { get; }
        public TerrainDetailBand[] Bands { get; }
        /// <summary>Sum of the base amplitudes of each band (index = <see cref="TerrainDetailBand"/>).</summary>
        public float[] BandAmplitude { get; }
        public float[] Multipliers { get; }

        /// <summary>Wavelength (radians) of an octave.</summary>
        public float Wavelength(int octave) => 2f * Mathf.PI / Frequencies[octave];

        /// <summary>Wavelength (meters) of an octave.</summary>
        public double WavelengthMeters(int octave) => Scale.WavelengthOfFrequency(Frequencies[octave]);

        /// <summary>
        /// Band-limit weight of an octave for a sample spacing (radians): 0 at and below the Nyquist wavelength
        /// (2 x spacing), 1 from 4 x spacing up; linear in between. Spacing 0 = every octave.
        /// </summary>
        public float Weight(int octave, float spacing)
        {
            if (spacing <= 0f)
            {
                return 1f;
            }
            return Mathf.Clamp01((Wavelength(octave) / spacing - 2f) * 0.5f);
        }

        /// <summary>
        /// Largest displacement (fraction of the radius) the octaves NOT resolved at a sample spacing can add:
        /// the aliasing error of a mesh with that spacing (conservative: full masks, no cancellation).
        /// </summary>
        public float UnresolvedAmplitude(float spacing)
        {
            float sum = 0f;
            for (int k = 0; k < OctaveCount; k++)
            {
                sum += Amplitudes[k] * MaxBoost * (1f - Weight(k, spacing));
            }
            return sum;
        }

        /// <summary>
        /// Typical slope (rise over run) the octaves resolved at a sample spacing add to the terrain: RMS-like
        /// noise slope (half the peak) with the mountain boost only, for the LOD error model.
        /// </summary>
        public float TypicalSlope(float spacing)
        {
            float sum = 0f;
            for (int k = 0; k < OctaveCount; k++)
            {
                sum += Amplitudes[k] * 3f * Frequencies[k] * (0.5f * NoisePeakSlopePerCell) * Weight(k, spacing);
            }
            return sum;
        }

        /// <summary>
        /// Largest slope (rise over run) the octaves resolved at a sample spacing can add to the terrain
        /// (conservative: peak noise slope, full masks).
        /// </summary>
        public float ResolvedSlope(float spacing)
        {
            float sum = 0f;
            for (int k = 0; k < OctaveCount; k++)
            {
                sum += Amplitudes[k] * MaxBoost * Frequencies[k] * NoisePeakSlopePerCell * Weight(k, spacing);
            }
            return sum;
        }
    }

    /// <summary>
    /// Slope of the planetary terrain (|gradient of e0|, rise over run) on a coarse per-face grid, built once per
    /// planet (V0.2.0C): the "relief context" the detail reads to stay calm on plains, plateau tops and valley
    /// floors and to dissect flanks and escarpments. Stored on gnomonic face grids (u = a / n) so a lookup is a
    /// dominant-axis test and a bilinear read, continuous across face edges (edge samples coincide). Pure data
    /// after construction: thread safe, deterministic.
    /// </summary>
    public sealed class TerrainReliefField
    {
        public const int Resolution = 128;

        readonly float[][] slope = new float[CubeSphereGenerator.FaceCount][];

        public TerrainReliefField(Func<Vector3, float> elevation)
        {
            int edge = Resolution + 1;
            for (int face = 0; face < CubeSphereGenerator.FaceCount; face++)
            {
                slope[face] = new float[edge * edge];
            }
            // Central differences at a quarter cell: the base terrain's finest octave (about 1/192 rad) is resolved.
            float delta = 0.25f * (Mathf.PI * 0.25f) / Resolution;
            Parallel.For(0, CubeSphereGenerator.FaceCount * edge, row =>
            {
                int face = row / edge, j = row % edge;
                CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
                float v = 2f * j / Resolution - 1f;
                float[] target = slope[face];
                for (int i = 0; i < edge; i++)
                {
                    float u = 2f * i / Resolution - 1f;
                    Vector3 dir = (normal + axisA * u + axisB * v).normalized;
                    Vector3 reference = Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.right;
                    Vector3 tangent = Vector3.Cross(dir, reference).normalized;
                    Vector3 bitangent = Vector3.Cross(dir, tangent);
                    float da = elevation((dir + tangent * delta).normalized) - elevation((dir - tangent * delta).normalized);
                    float db = elevation((dir + bitangent * delta).normalized) - elevation((dir - bitangent * delta).normalized);
                    target[i + j * edge] = Mathf.Sqrt(da * da + db * db) / (2f * delta);
                }
            });
        }

        /// <summary>Cube face whose region contains the direction (largest component, the generator's numbering).</summary>
        static int DominantFace(Vector3 d)
        {
            float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
            if (ay >= ax && ay >= az)
            {
                return d.y >= 0f ? 0 : 1;
            }
            if (ax >= az)
            {
                return d.x >= 0f ? 3 : 2;
            }
            return d.z >= 0f ? 4 : 5;
        }

        /// <summary>Slope at a unit direction (bilinear on the face grid).</summary>
        public float Sample(Vector3 direction)
        {
            int face = DominantFace(direction);
            CubeSphereGenerator.GetFaceAxes(face, out Vector3 normal, out Vector3 axisA, out Vector3 axisB);
            float n = Vector3.Dot(direction, normal);
            float u = Mathf.Clamp(Vector3.Dot(direction, axisA) / n, -1f, 1f);
            float v = Mathf.Clamp(Vector3.Dot(direction, axisB) / n, -1f, 1f);
            float x = (u + 1f) * 0.5f * Resolution, y = (v + 1f) * 0.5f * Resolution;
            int i = Mathf.Min((int)x, Resolution - 1), j = Mathf.Min((int)y, Resolution - 1);
            float fx = x - i, fy = y - j;
            int edge = Resolution + 1;
            float[] s = slope[face];
            float s00 = s[i + j * edge], s10 = s[i + 1 + j * edge], s01 = s[i + (j + 1) * edge], s11 = s[i + 1 + (j + 1) * edge];
            return Mathf.Lerp(Mathf.Lerp(s00, s10, fx), Mathf.Lerp(s01, s11, fx), fy);
        }
    }

    /// <summary>
    /// Deterministic multi-scale surface detail (V0.2.0C): the regional, local and near-surface relief added to
    /// the planetary terrain. Pure function of the direction (continuous 3D noise on the sphere: no cube face,
    /// patch, camera or thread dependence); the only state is immutable per-planet context.
    /// </summary>
    /// <remarks>
    /// Dependency order: planetary terrain (e0, continental field, mountain envelope) -> relief field (slope of
    /// e0) -> detail. Climate and biomes are downstream of the terrain and are never read here (no cycle).
    /// Geographic conditioning, all from those upstream signals:
    /// <list type="bullet">
    /// <item>Coasts: every band fades to zero within 6 x its own largest amplitude of elevation above sea level,
    /// so no band can push a point below the sea or move the coastline (the shoreline of e0 is preserved
    /// exactly; the worst total displacement near the shore is 0.75 x the height above sea); nothing under water.</item>
    /// <item>Mountains (range core envelope and influence): regional and local bands are ridged and stretched
    /// along the range direction (secondary crests parallel to the main ridges, broad valleys between them);
    /// their amplitude grows with the core envelope like the planetary detail does; regional valley floors are
    /// flattened by a soft maximum (intermontane basins).</item>
    /// <item>Relief (slope of e0 at ~80 km): flanks and escarpments get the full regional / local dissection,
    /// flat ground (plains, plateau tops, valley floors) keeps 35%.</item>
    /// <item>Character: a seeded planet-scale field selects smoother (eroded) or more rugged regions for the
    /// local and near-surface bands, a terrain-style signal standing in for aridity without reading the climate.</item>
    /// </list>
    /// Band limiting: octaves whose wavelength is below 4 x a requested sample spacing fade out (0 at 2 x, the
    /// Nyquist limit), so analysis grids and normals see a signal they can resolve. Positions of the adaptive
    /// terrain use the full function (spacing 0): the unresolved octaves are then only a sub-pixel aliasing
    /// noise bounded by <see cref="TerrainDetailProfile.UnresolvedAmplitude"/>, which the LOD error model counts.
    /// </remarks>
    public sealed class TerrainDetail
    {
        const int CharacterStream = 30;
        const int OctaveStream = 31;
        const float CharacterFrequency = 2.5f;
        const int CharacterOctaves = 2;
        const float CoastBandFactor = 6f;
        const float FlatFloor = 0.35f;
        const float SlopeStart = 0.02f;
        const float SlopeEnd = 0.15f;
        const float ValleyFloor = -0.55f;
        const float ValleyFloorSoftness = 0.2f;

        readonly int characterSeed;
        readonly int[] octaveSeeds;
        readonly float[] coastBand = new float[3];

        public TerrainDetail(TerrainDetailProfile profile, TerrainReliefField relief, int seed, float seaLevel)
        {
            Profile = profile;
            Relief = relief;
            SeaLevel = seaLevel;
            characterSeed = PlanetNoise.DeriveSeed(seed, CharacterStream);
            octaveSeeds = new int[profile.OctaveCount];
            for (int k = 0; k < profile.OctaveCount; k++)
            {
                octaveSeeds[k] = PlanetNoise.DeriveSeed(seed, OctaveStream + k);
            }
            for (int b = 0; b < 3; b++)
            {
                coastBand[b] = CoastBandFactor * profile.BandAmplitude[b] * TerrainDetailProfile.MaxBoost;
            }
        }

        public TerrainDetailProfile Profile { get; }
        public TerrainReliefField Relief { get; }
        public float SeaLevel { get; }

        /// <summary>Total detail (fraction of the radius) at a direction, band limited to a sample spacing (0 = full).</summary>
        public float Evaluate(Vector3 direction, in TerrainBaseSample context, float spacing)
        {
            Evaluate(direction, context, spacing, out TerrainDetailSample sample);
            return sample.Total;
        }

        /// <summary>Detail per band at a direction, band limited to a sample spacing (0 = full).</summary>
        public void Evaluate(Vector3 direction, in TerrainBaseSample context, float spacing, out TerrainDetailSample sample)
        {
            Evaluate(new Vector3d(direction), direction, context, spacing, out sample);
        }

        /// <summary>Total detail at a double-precision direction (fine octaves need it: see <see cref="PlanetNoise.Gradient(double, double, double, int)"/>).</summary>
        public float Evaluate(Vector3d direction, in TerrainBaseSample context, float spacing)
        {
            Evaluate(direction, direction.ToVector3(), context, spacing, out TerrainDetailSample sample);
            return sample.Total;
        }

        /// <summary>
        /// Detail per band. <paramref name="precise"/> drives the noise octaves (double: sub-meter precision at any
        /// scale); <paramref name="direction"/> is its float rounding, used by the coarse masks.
        /// </summary>
        public void Evaluate(Vector3d precise, Vector3 direction, in TerrainBaseSample context, float spacing, out TerrainDetailSample sample)
        {
            sample = default;
            float h = context.Elevation - SeaLevel;
            if (h <= 0f || Profile.OctaveCount == 0)
            {
                return; // nothing at or under the sea
            }
            TerrainDetailProfile profile = Profile;
            float m = context.Influence;
            float core = context.Core;

            // Masks (per band).
            float slope = Relief.Sample(direction);
            float dissection = Mathf.Lerp(FlatFloor, 1f, SmoothStep(SlopeStart, SlopeEnd, slope));
            float character = PlanetNoise.Fractal(direction * CharacterFrequency, characterSeed, CharacterOctaves);
            float smooth = SmoothStep(0.15f, 0.55f, character);
            float rugged = SmoothStep(-0.15f, -0.55f, character);
            float boostRegional = 1f + 2f * core;
            float boostMicro = 1f + 0.5f * core;
            float maskRegional = SmoothStep(0f, coastBand[0], h) * dissection * boostRegional;
            float maskLocal = SmoothStep(0f, coastBand[1], h) * dissection * boostRegional * Mathf.Lerp(1f, 0.45f, smooth) * Mathf.Lerp(1f, 1.3f, rugged);
            float maskMicro = SmoothStep(0f, coastBand[2], h) * Mathf.Lerp(0.6f, 1f, dissection) * boostMicro * Mathf.Lerp(1f, 0.35f, smooth) * Mathf.Lerp(1f, 1.3f, rugged);
            if (maskRegional <= 0f && maskLocal <= 0f && maskMicro <= 0f)
            {
                return;
            }

            // Ridges stretched along the range direction inside ranges (the planetary ridges' own trick).
            Vector3d q = precise + new Vector3d(context.Anisotropy) * m;
            float ridgeMixRegional = m, ridgeMixLocal = 0.7f * m, ridgeMixMicro = 0.3f * core;
            float plainRegional = 0f, ridgedRegional = 0f, plainLocal = 0f, ridgedLocal = 0f, plainMicro = 0f, ridgedMicro = 0f;
            float ridgeWeight = 1f;
            float resolvedRegional = 0f;
            TerrainDetailBand previousBand = TerrainDetailBand.Regional;
            for (int k = 0; k < profile.OctaveCount; k++)
            {
                float w = profile.Weight(k, spacing);
                if (w <= 0f)
                {
                    break; // finer octaves fade too
                }
                TerrainDetailBand band = profile.Bands[k];
                if (band == TerrainDetailBand.Regional)
                {
                    resolvedRegional += profile.Amplitudes[k] * w;
                }
                if (band != previousBand)
                {
                    ridgeWeight = 1f;
                    previousBand = band;
                }
                float amplitude = profile.Amplitudes[k] * w;
                float f = profile.Frequencies[k];
                Vector3d p = band == TerrainDetailBand.Micro ? precise : q;
                float n = PlanetNoise.Gradient(p.x * f, p.y * f, p.z * f, octaveSeeds[k]);
                // Ridged form from the same noise: sharp crests at the zero crossings, broad lows between; finer
                // octaves mostly on top of coarser crests (the Ridged chaining).
                float r = 1f - Mathf.Abs(n);
                r = r * r * ridgeWeight;
                ridgeWeight = Mathf.Clamp01(r * 2f);
                float ridged = 2f * r - 1f;
                switch (band)
                {
                    case TerrainDetailBand.Regional:
                        plainRegional += amplitude * n;
                        ridgedRegional += amplitude * ridged;
                        break;
                    case TerrainDetailBand.Local:
                        plainLocal += amplitude * n;
                        ridgedLocal += amplitude * ridged;
                        break;
                    default:
                        plainMicro += amplitude * n;
                        ridgedMicro += amplitude * ridged;
                        break;
                }
            }

            // Intermontane basins: regional valley floors flattened by a soft maximum, relative to the resolved
            // regional amplitude (so a band-limited sample fades consistently) and zero at zero (no offset).
            if (resolvedRegional > 0f)
            {
                float floor = ValleyFloor * resolvedRegional;
                float softness = ValleyFloorSoftness * resolvedRegional;
                ridgedRegional = SoftMax(ridgedRegional, floor, softness) - SoftMax(0f, floor, softness);
            }

            sample.Regional = Mathf.Lerp(plainRegional, ridgedRegional, ridgeMixRegional) * maskRegional;
            sample.Local = Mathf.Lerp(plainLocal, ridgedLocal, ridgeMixLocal) * maskLocal;
            sample.Micro = Mathf.Lerp(plainMicro, ridgedMicro, ridgeMixMicro) * maskMicro;
        }

        static float SoftMax(float a, float b, float softness)
        {
            float d = a - b;
            return 0.5f * (a + b + Mathf.Sqrt(d * d + softness * softness));
        }

        static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
