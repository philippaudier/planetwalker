// Planetwalker clouds (V0.1.2C), shared by the cloud shell and by the terrain / ocean shaders (cloud
// shadows), so shadows always match the clouds. Directions are planet object-space unit vectors.
//
// _CloudCube (CloudField, cube map): R large-scale potential, G weather-system pattern, B convective
// clusters, A system envelope. Potential = lerp(R + convection x 0.25 x B, threshold + G - 0.4,
// A x weather x 1.4) (as on the CPU, whose area quantile gives the coverage threshold), plus zero-mean
// local detail evaluated per pixel: 3D gradient-noise fBm (broken
// edges and holes, stretched east-west in the midlatitude belts) and billowy noise in convective
// areas. Motion: flow map along the zonal three-cell wind profile (same shape as the climate's
// prevailing winds): two samples rotated upstream around the planet axis by a bounded offset, cross-faded
// (no growing shear, no drift), a pure function of the simulation time, continuous everywhere.
//
// Each including shader declares, in its UnityPerMaterial buffer:
//   float4 _CloudParams;  // x threshold, y max alpha (density), z detail amplitude, w enabled (> 0)
//   float4 _CloudParams2; // x weather strength, y convection, z shadow strength, w shell radius (planet units)
//   float4 _CloudParams3; // xyz rotation axis (planet space), w simulation time
//   float4 _CloudSeed;    // xyz noise offset, w edge softness
#ifndef PLANETWALKER_CLOUDS_COMMON_INCLUDED
#define PLANETWALKER_CLOUDS_COMMON_INCLUDED

TEXTURECUBE(_CloudCube);
SAMPLER(sampler_CloudCube);

float3 CloudHash3(float3 p)
{
    p = frac(p * float3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return frac((p.xxy + p.yxx) * p.zyx) * 2.0 - 1.0;
}

// Gradient noise, roughly [-0.7, 0.7], quintic fade.
float CloudNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = p - i;
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float n000 = dot(CloudHash3(i), f);
    float n100 = dot(CloudHash3(i + float3(1, 0, 0)), f - float3(1, 0, 0));
    float n010 = dot(CloudHash3(i + float3(0, 1, 0)), f - float3(0, 1, 0));
    float n110 = dot(CloudHash3(i + float3(1, 1, 0)), f - float3(1, 1, 0));
    float n001 = dot(CloudHash3(i + float3(0, 0, 1)), f - float3(0, 0, 1));
    float n101 = dot(CloudHash3(i + float3(1, 0, 1)), f - float3(1, 0, 1));
    float n011 = dot(CloudHash3(i + float3(0, 1, 1)), f - float3(0, 1, 1));
    float n111 = dot(CloudHash3(i + float3(1, 1, 1)), f - float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y),
                lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y), u.z);
}

float CloudFbm(float3 p, int octaves)
{
    float sum = 0.0, amplitude = 1.0, norm = 0.0;
    for (int i = 0; i < octaves; i++)
    {
        sum += amplitude * CloudNoise(p);
        norm += amplitude;
        amplitude *= 0.5;
        p = p * 2.03 + 11.7;
    }
    return sum / norm;
}

// Upstream sampling direction at the simulation time (zonal advection, rotation around the axis).
float3 CloudAdvect(float3 d, float3 axis, float time)
{
    float s = clamp(dot(d, axis), -1.0, 1.0);
    float latitude = abs(asin(s));
    float east = 0.5 * (-sin(6.0 * latitude) - 0.2);
    float angle = -east * time;
    float c = cos(angle), sn = sin(angle);
    return d * c + cross(axis, d) * sn + axis * (s * (1.0 - c));
}

// Cloud potential sampled at the advected direction a (deterministic part as on the CPU + local detail).
// convective: the cluster channel; grain: internal opacity variation (0.5 .. 1) so decks are not flat paint.
float CloudPotentialAt(float3 a, int octaves, out float convective, out float grain)
{
    float3 axis = _CloudParams3.xyz;
    float time = _CloudParams3.w;
    float4 t = SAMPLE_TEXTURECUBE_LOD(_CloudCube, sampler_CloudCube, a, 0);
    convective = t.b;
    // Background (base + convective clusters), moved towards the weather system's own pattern inside
    // its envelope (CloudField.Potential).
    float background = t.r + _CloudParams2.y * 0.25 * t.b;
    float system = _CloudParams.x + (t.g - 0.4);
    float potential = lerp(background, system, saturate(t.a * _CloudParams2.x * 1.4));
    // Local detail; midlatitude belts stretched east-west, evolving slowly with time.
    float s = dot(a, axis);
    float belt = exp(-pow((abs(asin(clamp(s, -1.0, 1.0))) * 57.2958 - 50.0) / 14.0, 2.0));
    float3 q = a + axis * (s * 1.5 * belt);
    float3 p = q * 16.0 + _CloudSeed.xyz + float3(0.0, time * 0.015, 0.0);
    float detail = CloudFbm(p, octaves);
    float billow = 1.0 - abs(CloudNoise(p * 2.3 + 17.0)) * 1.4; // puffy convective cells
    potential += _CloudParams.z * detail + _CloudParams2.y * convective * 0.3 * (billow - 0.55);
    // Mid-scale (clumps, streaks of a few tens of km), not per-pixel grain.
    float fine = CloudNoise(p * 1.35 + 5.1) + 0.5 * CloudNoise(p * 2.8 + 2.3);
    grain = lerp(1.0, saturate(0.8 + fine * 0.75), 0.75 * saturate(_CloudParams.z / 0.3));
    return potential;
}

// Motion period (simulation time units): two samples advected by (phase - 1/2) x period along the zonal
// winds, cross-faded (triangle weights): bounded displacement (no growing shear, no drift), a pure
// function of the time.
#define CLOUD_MOTION_PERIOD 0.25

// Cloud potential at a direction at the simulation time.
float CloudPotential(float3 d, int octaves, out float convective, out float grain)
{
    float3 axis = _CloudParams3.xyz;
    float time = _CloudParams3.w;
    float phase = frac(time / CLOUD_MOTION_PERIOD);
    float w1 = 1.0 - abs(2.0 * phase - 1.0);
    float3 a2 = CloudAdvect(d, axis, (frac(phase + 0.5) - 0.5) * CLOUD_MOTION_PERIOD);
    float potential = CloudPotentialAt(a2, octaves, convective, grain);
    if (w1 > 1e-4)
    {
        float convective1, grain1;
        float3 a1 = CloudAdvect(d, axis, (phase - 0.5) * CLOUD_MOTION_PERIOD);
        float potential1 = CloudPotentialAt(a1, octaves, convective1, grain1);
        potential = lerp(potential, potential1, w1);
        convective = lerp(convective, convective1, w1);
        grain = lerp(grain, grain1, w1);
    }
    return potential;
}
float CloudPotential(float3 d, int octaves, out float convective)
{
    float grain;
    return CloudPotential(d, octaves, convective, grain);
}

// ---- Close-range cloud detail (V0.2.0D) -----------------------------------------------------------------------
// The detail above stops at ~R / 300 (about 50 km at Earth scale): right from orbit, but from the ground a pixel of the
// layer covers tens of meters, so the clouds read as smooth white masses. These octaves continue the spectrum from
// that scale down to ~200 m, each faded out with the pixel footprint (radians on the unit sphere): nothing changes
// from orbit, the finer ones appear as the camera comes close. They move with the same flow map as the field.
// towardSun: the same octaves sampled half a cell towards the sun (per octave), for relief shading.
#define CLOUD_FINE_FREQUENCY 270.0
#define CLOUD_FINE_OCTAVES 8

// Weight of a fine octave of this frequency (cells per unit) for a footprint: 1 while a cell spans 4+ pixels, 0 at 2.
float CloudFineWeight(float frequency, float footprint)
{
    return saturate(2.0 - 4.0 * footprint * frequency);
}

float CloudFineAt(float3 a, float3 sunTangent, float footprint, out float towardSun, out float coverage)
{
    float3 p = a * CLOUD_FINE_FREQUENCY + _CloudSeed.xyz * 3.1 + 41.0;
    float frequency = CLOUD_FINE_FREQUENCY;
    float amplitude = 1.0;
    float sum = 0.0;
    towardSun = 0.0;
    coverage = 0.0;
    for (int i = 0; i < CLOUD_FINE_OCTAVES; i++)
    {
        float w = CloudFineWeight(frequency, footprint);
        if (w <= 0.0)
        {
            break; // finer octaves are even smaller
        }
        // Billowy (puffy cauliflower lobes) blended with plain noise (streaks).
        float n = CloudNoise(p);
        float ns = CloudNoise(p + sunTangent * 0.5);
        float b = 0.3 - abs(n) * 1.6; // about zero-mean (no coverage bias)
        float bs = 0.3 - abs(ns) * 1.6;
        sum += amplitude * w * lerp(n, b, 0.6);
        towardSun += amplitude * w * lerp(ns, bs, 0.6);
        coverage = max(coverage, w);
        amplitude *= 0.58;
        frequency *= 2.03;
        p = p * 2.03 + 11.7;
    }
    return sum;
}

// Fine detail (zero-mean, faded) at a direction and the simulation time, with the field's two-sample flow map.
float CloudFine(float3 d, float3 sunTangent, float footprint, out float towardSun, out float coverage)
{
    towardSun = 0.0;
    coverage = 0.0;
    if (CloudFineWeight(CLOUD_FINE_FREQUENCY, footprint) <= 0.0)
    {
        return 0.0;
    }
    float3 axis = _CloudParams3.xyz;
    float time = _CloudParams3.w;
    float phase = frac(time / CLOUD_MOTION_PERIOD);
    float w1 = 1.0 - abs(2.0 * phase - 1.0);
    float3 a2 = CloudAdvect(d, axis, (frac(phase + 0.5) - 0.5) * CLOUD_MOTION_PERIOD);
    float fine = CloudFineAt(a2, sunTangent, footprint, towardSun, coverage);
    if (w1 > 1e-4)
    {
        float sun1, coverage1;
        float3 a1 = CloudAdvect(d, axis, (phase - 0.5) * CLOUD_MOTION_PERIOD);
        float fine1 = CloudFineAt(a1, sunTangent, footprint, sun1, coverage1);
        fine = lerp(fine, fine1, w1);
        towardSun = lerp(towardSun, sun1, w1);
    }
    return fine;
}

// Cloud alpha (0 .. density) at a direction.
float CloudAlpha(float3 d, int octaves)
{
    float convective, grain;
    float potential = CloudPotential(d, octaves, convective, grain);
    return saturate((potential - _CloudParams.x) / _CloudSeed.w + 0.5) * _CloudParams.y * grain;
}
// Shadow amount (0 .. shadow strength) on a surface point (planet units, planet space) lit along
// lightDirection (planet space, unit): cloud alpha where the ray towards the sun crosses the cloud
// shell (taken just above the point where relief reaches the shell: the layer is drawn above everything).
float CloudShadow(float3 positionPlanet, float3 lightDirection)
{
    if (_CloudParams.w <= 0.0 || _CloudParams2.z <= 0.0)
    {
        return 0.0;
    }
    float radius = max(_CloudParams2.w, length(positionPlanet) * 1.003);
    float b = dot(positionPlanet, lightDirection);
    float c = dot(positionPlanet, positionPlanet) - radius * radius;
    float t = -b + sqrt(b * b - c);
    float3 crossing = normalize(positionPlanet + lightDirection * t);
    return CloudAlpha(crossing, 2) * _CloudParams2.z;
}

#endif
