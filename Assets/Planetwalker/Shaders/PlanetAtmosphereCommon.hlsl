// Planetwalker atmosphere light (V0.1.2B), shared by the atmosphere shell (sky / limb) and by the
// terrain and ocean shaders (haze between the camera and a surface point). Simplest robust
// approximation, not a scattering model: the view ray is intersected analytically with the shell
// sphere and the ocean sphere; tau = path / thickness x exp(-falloff x height of the segment's lowest
// point above the ocean), amount = 1 - exp(-density x tau^1.5). Rays ending on a surface use
// tau' = cap x (1 - exp(-tau / cap)) (soft cap): a light veil looking down, a stronger haze on grazing
// paths (mountain bases on the limb meet the limb glow), mountain tops above the shell stay clear.
// Lit by the main light at the segment midpoint: bright day rim, dim night rim, restrained warm tint
// near the terminator on long paths only. Returns the added light (multiply by light color, strength).
#ifndef PLANETWALKER_ATMOSPHERE_COMMON_INCLUDED
#define PLANETWALKER_ATMOSPHERE_COMMON_INCLUDED

struct AtmosphereInputs
{
    float3 center;       // sphere center (same space as origin / ray)
    float shellRadius;
    float oceanRadius;
    float density;
    float falloff;
    float surfaceCap;    // soft cap of tau for rays ending on a surface
    half nightLevel;
    half warmStrength;
    half3 dayColor;
    half3 warmColor;
    float3 lightDirection; // unit, towards the light, same space
};

// surfaceDistance > 0: the ray ends on a surface at that distance (terrain / ocean haze).
// surfaceDistance <= 0: sky ray, ends where it leaves the shell or meets the ocean sphere.
// hitsOcean: the infinite ray meets the ocean sphere in front of the camera (inside the shell).
half3 AtmosphereLight(AtmosphereInputs a, float3 origin, float3 ray, float surfaceDistance, out bool hitsOcean)
{
    hitsOcean = false;
    float3 o = origin - a.center;
    float b = dot(o, ray);
    float oo = dot(o, o);
    float disc = b * b - (oo - a.shellRadius * a.shellRadius);
    if (disc <= 0.0)
    {
        return 0;
    }
    float root = sqrt(disc);
    float t0 = max(0.0, -b - root);
    float t1 = -b + root;
    float discOcean = b * b - (oo - a.oceanRadius * a.oceanRadius);
    if (discOcean > 0.0)
    {
        float tp = -b - sqrt(discOcean);
        if (tp > 0.0 && tp < t1)
        {
            t1 = tp;
            hitsOcean = true;
        }
    }
    bool surface = surfaceDistance > 0.0;
    if (surface)
    {
        t1 = min(t1, surfaceDistance);
    }
    float path = t1 - t0;
    if (path <= 0.0)
    {
        return 0;
    }
    float thickness = max(1e-4, a.shellRadius - a.oceanRadius);
    float tc = clamp(-b, t0, t1);
    float height = saturate((length(o + ray * tc) - a.oceanRadius) / thickness);
    float optical = path / thickness * exp(-a.falloff * height);
    if (surface)
    {
        optical = a.surfaceCap * (1.0 - exp(-optical / a.surfaceCap));
    }
    float amount = 1.0 - exp(-a.density * optical * sqrt(optical));
    float3 mid = normalize(o + ray * (0.5 * (t0 + t1)));
    float mu = dot(mid, a.lightDirection);
    half day = smoothstep(-0.25, 0.3, mu);
    // Twilight (V0.1.2D): sunlight reaching the air near the terminator crossed a long path and lost its
    // blue first (Rayleigh-like reddening), strongest low in the atmosphere; the warm tint is added on
    // top of the reddened sky, never blended with the blue (blue + orange blends read purple).
    half twilight = (1.0 - smoothstep(-0.05, 0.4, mu)) * (1.0 - 0.5 * height) * saturate(a.warmStrength / 0.3);
    half3 reddening = exp(-half3(0.15, 0.45, 1.0) * 3.0 * twilight);
    half warm = (1.0 - smoothstep(0.0, 0.3, abs(mu - 0.03))) * a.warmStrength * saturate((optical - 3.0) / 6.0);
    half3 color = a.dayColor * reddening + a.warmColor * (warm * 1.2);
    return color * (amount * max(day, a.nightLevel));
}

// Scene ambient kept at a surface point (V0.1.2D): 1 on the day side, nightAmbient on the night side,
// smooth across the twilight zone. Uses the geocentric up (planet center), not the surface normal, so
// relief does not change it and terrain, ocean and clouds darken together. The sky ambient is a
// day-side phenomenon: the night stays genuinely dark.
half DaylightAmbient(float3 positionWS, float3 center, float3 lightDirection, half nightAmbient)
{
    float s = dot(normalize(positionWS - center), lightDirection);
    return lerp(nightAmbient, 1.0, smoothstep(-0.25, 0.1, s));
}

// Planet shadow (V0.1.2D): direct sunlight reaches a surface point only while the sun is above its
// geocentric horizon. Slopes of the (exaggerated) relief facing a sun that has already set would
// otherwise glow beyond the terminator. horizon: the geocentric sun height where the light is gone
// (more negative for clouds, which sit higher). Applied after, and independently of, the light wrap.
half PlanetShadow(float3 positionWS, float3 center, float3 lightDirection, half horizon)
{
    float s = dot(normalize(positionWS - center), lightDirection);
    return smoothstep(horizon, horizon + 0.12, s);
}


// Skylight amount at a surface point (V0.2.0D): the sunlit sky dome's irradiance relative to the direct sun, from the
// geocentric sun height only (zero on the night side, rising through the twilight, full by mid-morning).
half SkylightAmount(float3 positionWS, float3 center, float3 lightDirection)
{
    float s = dot(normalize(positionWS - center), lightDirection);
    return smoothstep(-0.1, 0.35, s);
}

#endif
