// Planetwalker physically based surface materials (V0.2.0F), included by PlanetTerrain.shader after the geology
// (GeologyContext, GeologySetup, GeologyRockMacro). The geology stays the authority on where things are; the six
// CC0 scans (PlanetSurfaceLibrary: Texture2DArrays, one layer per material) say what they are made of.
//
// Per pixel:
//  1. Weights of the six materials from the geology (SurfaceWeights: the CPU mirror is SurfaceMaterials.Weights):
//     rock (V0.2.0E bedrock mask with its scale-aware breakup) split by province (sedimentary weight, family:
//     granite / basalt / sandstone / limestone), loose ground split by slope and erosion (gravel / dry sand).
//  2. The two largest weights only (bounded cost), reduced by the third (continuous rank changes), then blended by
//     the materials' height maps (sand fills the hollows between stones, rock pokes through debris).
//  3. Triplanar projection on the planet's axes, in planet-space meters from the V0.2.0D snapped origin (periodic
//     over 1024 m, tiles of 2 m divide it: floating-origin and snap invariant, no cube-face seam, no float precision
//     loss). Sharp axis blend (power 8); axes under 2 % skipped. Normals: whiteout blend per axis with the axis sign
//     handled, in planet space, then to world space.
//  4. Color: the scans' own detail, its mean shifted towards the geology's colors (province rock tint with strata,
//     streaks and weathering; biome ground color), so the planet keeps its large-scale identity and no tile repeats a
//     photograph's absolute color. Far away (texels below a pixel for every material) only the means remain: from a
//     few kilometers up the cost is that of the procedural mode.
//  5. Coast (V0.2.0F addendum): a wet band above the waterline, its height varying along the shore (0.4 .. 2.5 m, lower
//     on steep shores), wet below sea level: darker albedo, smoother (water film). Rock stays rock: no forced beach.
#ifndef PLANETWALKER_TERRAIN_MATERIALS_INCLUDED
#define PLANETWALKER_TERRAIN_MATERIALS_INCLUDED

#define SURFACE_MATERIALS_MAX 11
TEXTURE2D_ARRAY(_SurfAlbedo);
SAMPLER(sampler_SurfAlbedo);
TEXTURE2D_ARRAY(_SurfNormal);
SAMPLER(sampler_SurfNormal);
TEXTURE2D_ARRAY(_SurfMask);
SAMPLER(sampler_SurfMask);
// Per layer: mean albedo (linear rgb), tile size (m). Outside the material buffer: set by the property block.
float4 _SurfMean[SURFACE_MATERIALS_MAX];

struct PbrSurface
{
    half3 albedo;
    float3 normalWS;
    half smoothness;
    half occlusion;
    half3 debug;        // debug view color (unlit), when one is selected
    half wet;
};

// ---- 1. Material weights (mirror of SurfaceMaterials.Weights / GravelShare) -----------------------------------------
// 0-3 rock (granite, sandstone, limestone, basalt), 4 dry sand, 5 gravel, 6 grass, 7 dry grass, 8 forest floor, 9 soil,
// 10 snow. cover = green grass, dry grass, forest floor, snow (per vertex, from the biomes; V0.2.0F.2).
#define SURFACE_MATERIALS 11

void SurfaceWeights(float rockShare, float erosion, float slope, float sedimentary, float family, float4 cover, float heightM, out float w[SURFACE_MATERIALS])
{
    float snowOnRock = saturate(cover.w) * (1.0 - smoothstep(0.6, 1.2, slope));
    float rock = saturate(rockShare) * (1.0 - snowOnRock);
    float crystalline = 1.0 - saturate(sedimentary);
    float basalt = smoothstep(0.1, 0.55, family);
    float limestone = smoothstep(-0.35, 0.35, family);
    w[0] = rock * crystalline * (1.0 - basalt);
    w[1] = rock * (1.0 - crystalline) * (1.0 - limestone);
    w[2] = rock * (1.0 - crystalline) * limestone;
    w[3] = rock * crystalline * basalt;
    float loose = 1.0 - rock;
    float snow = saturate(cover.w);
    float green = saturate(cover.x), dry = saturate(cover.y), forest = saturate(cover.z);
    float plants = saturate(green + dry + forest);
    float beach = 1.0 - smoothstep(3.0, 20.0, heightM);
    float vegetation = (1.0 - snow) * plants * (1.0 - smoothstep(0.35, 0.9, slope)) * (1.0 - 0.6 * saturate(erosion)) * (1.0 - beach);
    float sum = green + dry + forest;
    float k = sum > 1e-5 ? vegetation / sum : 0.0;
    w[10] = loose * snow;
    w[6] = loose * green * k;
    w[7] = loose * dry * k;
    w[8] = loose * forest * k;
    float bare = loose * max(0.0, 1.0 - snow - vegetation);
    float gravel = saturate(smoothstep(0.08, 0.35, slope) * 0.75 + saturate(erosion) * 0.35 + crystalline * 0.15);
    float soil = saturate(plants * 1.5) * (1.0 - beach);
    w[5] = bare * gravel;
    w[9] = bare * (1.0 - gravel) * soil;
    w[4] = bare * (1.0 - gravel) * (1.0 - soil);
}

// ---- 2. The two strongest (mirror of SurfaceMaterials.TopTwo) ----------------------------------------------------------
void TopTwo(float w[SURFACE_MATERIALS], out int first, out float firstWeight, out int second, out float secondWeight)
{
    // Ties keep the lower index (strict comparisons), as on the CPU. Weights are >= 0.
    first = 0; second = 0;
    float v1 = -1.0, v2 = -1.0, v3 = -1.0;
    [unroll] for (int k = 0; k < SURFACE_MATERIALS; k++)
    {
        float v = w[k];
        if (v > v1) { v3 = v2; v2 = v1; second = first; v1 = v; first = k; }
        else if (v > v2) { v3 = v2; v2 = v; second = k; }
        else if (v > v3) { v3 = v; }
    }
    float w3 = max(v3, 0.0);
    float a = max(v1 - w3, 1e-6), b = max(v2 - w3, 0.0);
    firstWeight = a / (a + b);
    secondWeight = b / (a + b);
}

// ---- 3. Triplanar sampling of one layer --------------------------------------------------------------------------------
struct LayerSample
{
    half3 albedo;
    float3 normalP;  // planet space
    half3 mask;      // smoothness, AO, height
};

LayerSample SampleLayer(float3 p, float3 nP, float3 blend, float layer, float tile, half normalStrength, bool sampleNormal)
{
    float3 axisSign = sign(nP);
    axisSign = float3(axisSign.x != 0 ? axisSign.x : 1, axisSign.y != 0 ? axisSign.y : 1, axisSign.z != 0 ? axisSign.z : 1);
    float3 q = p / tile;
    float2 uvX = float2(q.z * axisSign.x, q.y);
    float2 uvY = float2(q.x * axisSign.y, q.z);
    float2 uvZ = float2(-q.x * axisSign.z, q.y);

    LayerSample s;
    s.albedo = 0;
    s.mask = 0;
    float3 n = 0;
    if (blend.x > 0.0)
    {
        s.albedo += SAMPLE_TEXTURE2D_ARRAY(_SurfAlbedo, sampler_SurfAlbedo, uvX, layer).rgb * blend.x;
        s.mask += SAMPLE_TEXTURE2D_ARRAY(_SurfMask, sampler_SurfMask, uvX, layer).rgb * blend.x;
        half3 t = sampleNormal ? UnpackNormalScale(SAMPLE_TEXTURE2D_ARRAY(_SurfNormal, sampler_SurfNormal, uvX, layer), normalStrength) : half3(0, 0, 1);
        t.x *= axisSign.x;
        t = half3(t.xy + nP.zy, abs(t.z) * nP.x);
        n += t.zyx * blend.x;
    }
    if (blend.y > 0.0)
    {
        s.albedo += SAMPLE_TEXTURE2D_ARRAY(_SurfAlbedo, sampler_SurfAlbedo, uvY, layer).rgb * blend.y;
        s.mask += SAMPLE_TEXTURE2D_ARRAY(_SurfMask, sampler_SurfMask, uvY, layer).rgb * blend.y;
        half3 t = sampleNormal ? UnpackNormalScale(SAMPLE_TEXTURE2D_ARRAY(_SurfNormal, sampler_SurfNormal, uvY, layer), normalStrength) : half3(0, 0, 1);
        t.x *= axisSign.y;
        t = half3(t.xy + nP.xz, abs(t.z) * nP.y);
        n += t.xzy * blend.y;
    }
    if (blend.z > 0.0)
    {
        s.albedo += SAMPLE_TEXTURE2D_ARRAY(_SurfAlbedo, sampler_SurfAlbedo, uvZ, layer).rgb * blend.z;
        s.mask += SAMPLE_TEXTURE2D_ARRAY(_SurfMask, sampler_SurfMask, uvZ, layer).rgb * blend.z;
        half3 t = sampleNormal ? UnpackNormalScale(SAMPLE_TEXTURE2D_ARRAY(_SurfNormal, sampler_SurfNormal, uvZ, layer), normalStrength) : half3(0, 0, 1);
        t.x *= -axisSign.z;
        t = half3(t.xy + nP.xy, abs(t.z) * nP.z);
        n += t.xyz * blend.z;
    }
    s.normalP = normalize(n);
    return s;
}

// Planet-space triplanar blend weights: sharp (power 8), negligible axes dropped (fewer samples), renormalized.
float3 TriplanarBlend(float3 nP)
{
    float3 b = pow(abs(nP), 12.0);
    b /= max(b.x + b.y + b.z, 1e-6);
    b = saturate((b - 0.06) / 0.94); // grazing projections (stretched) dropped
    return b / max(b.x + b.y + b.z, 1e-6);
}

// ---- Projection scale (V0.2.0F.1) -----------------------------------------------------------------------------------
// Stable footprint: the size of a pixel at this distance (meters), from the distance to the camera and the vertical
// field of view at a fixed reference of 1080 lines. Unlike screen derivatives it does not change with the triangle a
// pixel belongs to (mesh LOD), with the camera's rotation or with the render resolution: the scale of a point depends on
// its distance only, and moving the camera changes it continuously.
float StableFootprint(float3 positionWS)
{
    float3x3 planetFromWorld = (float3x3)_PlanetFromWorld;
    float distanceM = length(mul(planetFromWorld, positionWS - _WorldSpaceCameraPos)) * _GroundOrigin.w;
    float pixelAngle = 2.0 / (abs(UNITY_MATRIX_P._m11) * 1080.0);
    return max(distanceM * pixelAngle, 1e-5);
}

// Scale state for a footprint and a material's tile: continuous position s in 0..3 along the steps of 8 (tile x 1, 8,
// 64, 512: 2 / 16 / 128 / 1024 m, all divisors of the 1024 m anchoring period), so a tile spans about 320 pixels.
// Returns (step index 0..2, blend 0..1 towards the next step): a pure scale over 60 % of each step, a smoothstep
// crossfade over the last 40 % (about 1.2 octaves of distance), continuous everywhere (at a step boundary both sides
// show the same scale). Mirrored on the CPU by SurfaceScales.Blend.
float ScalePosition(float footprint, float tile)
{
    return clamp(log2(max(footprint * 320.0 / tile, 1e-6)) / 3.0, 0.0, 3.0);
}

float2 ScaleBlend(float s)
{
    float i = min(floor(s), 2.0);
    return float2(i, smoothstep(0.6, 1.0, s - i));
}

// Normal map strength along the scale (V0.2.0F.2: nearly constant, 1 / 0.9 / 0.81 / 0.73). V0.2.0F.1 faded it fast
// (down to 0.29): a bumpier surface is darker on average, so the ground brightened and darkened as the camera climbed or
// descended.
half ScaleNormalStrength(float s)
{
    return exp2(-0.15 * s);
}

// One material at the scale state: one or (inside a transition band) two scales, every property blended alike
// (albedo, mask, renormalized normal). Normals are not sampled when their strength is negligible (far away).
LayerSample SampleLayerMultiScale(float3 p, float3 nP, float3 blend, float layer, float tile, float s, half normalStrength, half3 mean)
{
    float2 state = ScaleBlend(s);
    float scale = tile * exp2(3.0 * state.x);
    half strength = normalStrength * ScaleNormalStrength(s);
    bool normals = strength > 0.03;
    LayerSample a = SampleLayer(p, nP, blend, layer, scale, strength, normals);
    if (state.y <= 0.0)
    {
        return a;
    }
    LayerSample b = SampleLayer(p, nP, blend, layer, scale * 8.0, strength, normals);
    if (state.y >= 1.0)
    {
        return b;
    }
    // Contrast-preserving crossfade (V0.2.0F.2): averaging two unrelated scales of a texture lowers its contrast by
    // up to sqrt(2) mid-band (a washed-out, flatter look that comes and goes with the camera height). The deviation
    // from the mean is scaled back by 1 / sqrt(f^2 + (1 - f)^2), for the color and for the normal's tilt.
    float f = state.y;
    half restore = rsqrt(f * f + (1.0 - f) * (1.0 - f));
    LayerSample r;
    r.albedo = max(mean + (lerp(a.albedo, b.albedo, f) - mean) * restore, 0.0);
    r.mask = lerp(a.mask, b.mask, f);
    float3 tilt = lerp(a.normalP, b.normalP, f) - nP;
    r.normalP = normalize(nP + tilt * restore);
    return r;
}

// Color transfer: keep the scan's detail, move its mean towards a target color (bounded ratio: no extreme tints).
half3 TransferColor(half3 texel, half3 mean, half3 target, half amount)
{
    half3 ratio = clamp(target / max(mean, 1e-3), 0.25, 4.0);
    return texel * lerp(half3(1.0, 1.0, 1.0), ratio, amount);
}

// ---- 5. Coastal wetness -------------------------------------------------------------------------------------------------
half CoastalWetness(in GeologyContext c)
{
    // Height of the wet band above sea level: varies along the shore (planet-anchored noise), lower on steep shores.
    float along = GroundNoise(c.p, 64.0, c.seed + 61u) * 0.7 + GroundNoise(c.p, 16.0, c.seed + 62u) * 0.3;
    float bandHeight = lerp(0.4, 2.5, along) * lerp(1.0, 0.35, smoothstep(0.2, 0.7, c.refSlope));
    // Irregular edge (V0.2.0F.1): the height is perturbed by planet-anchored noise (8 / 32 m) so the wet line is not a
    // contour line; the transition spans the whole band and beyond.
    float ragged = (GroundNoise(c.p, 8.0, c.seed + 64u) - 0.5) * 0.7 + (GroundNoise(c.p, 32.0, c.seed + 65u) - 0.5) * 0.8;
    float h = c.heightM + ragged * bandHeight;
    return 1.0 - smoothstep(-0.3 * bandHeight, 1.6 * bandHeight, h);
}

// Damp sand above the wet band (V0.2.0F.1): spray and capillary moisture, fading inland over 4-10 m of height; its
// reach varies along the shore with a field of its own (no stripe parallel to the waterline).
half CoastalDamp(in GeologyContext c)
{
    float reach = lerp(4.0, 10.0, GroundNoise(c.p, 128.0, c.seed + 63u)) * lerp(1.0, 0.3, smoothstep(0.2, 0.7, c.refSlope));
    float ragged = (GroundNoise(c.p, 32.0, c.seed + 66u) - 0.5) * 0.8;
    return 1.0 - smoothstep(-0.2 * reach, 1.4 * reach, c.heightM + ragged * reach);
}

PbrSurface EvaluateSurfacePbr(float3 positionWS, float3 normalWS, half3 soilColor, float4 grid, float4 geo, float4 flow,
    half3 rockTint, float sedimentary, float family, float4 cover)
{
    PbrSurface s;
    float footprint = StableFootprint(positionWS);
    GeologyContext c = GeologySetup(positionWS, soilColor, grid, geo, flow, footprint);
    float3x3 planetFromWorld = (float3x3)_PlanetFromWorld;
    float3 nP = normalize(mul(planetFromWorld, normalWS));
    half strength = _SurfParams.y;

    // 1-2. Materials.
    float w[SURFACE_MATERIALS];
    SurfaceWeights(c.rockMask, c.erosion, c.refSlope, sedimentary, family, cover, c.heightM, w);
    int m1, m2;
    float w1, w2;
    TopTwo(w, m1, w1, m2, w2);

    // Macro colors from the geology (the procedural appearance's large structures).
    float strataShow, strataZ;
    half3 rockMacro = GeologyRockMacro(c, rockTint, strataShow, strataZ);
    half3 groundTarget = lerp(lerp(soilColor, rockTint * 1.2 + 0.02, 0.35 * (1.0 - c.vegetated) * (1.0 - c.snow)), soilColor, c.vegetated);
    // V0.2.0F.1: below sea level the vertex color is the elevation palette's water and beach colors, not the ground's:
    // a beige / teal band painted over the sand through the clear shallow water. There the target is the geology's own
    // (the debris tone of the local rock), blended in over the 2 m above the waterline (no line at sea level).
    half underwater = 1.0 - smoothstep(-2.0, 2.0, c.heightM);
    groundTarget = lerp(groundTarget, rockTint * 1.15 + 0.03, underwater);
    half3 target1 = m1 < 4 ? rockMacro : groundTarget;
    half3 target2 = m2 < 4 ? rockMacro : groundTarget;
    // Layered rock takes more of the geology's macro tone: the scans' own bedding follows the projection axes, the
    // geology's strata lie at their true altitude (horizontal everywhere on the planet).
    half rockTransfer = lerp(0.55, 0.85, saturate(strataShow * 1.5));
    // Loose ground: the scans keep most of their own color (V0.2.0F.1: 0.3 .. 0.6 instead of 0.55 .. 0.95, which was close
    // to a flat fill of the biome color over the texture).
    half groundTransfer = lerp(0.3, 0.6, c.vegetated);
    // Snow keeps its own color (no biome tint).
    half transfer1 = m1 < 4 ? rockTransfer : (m1 == 10 ? 0.0 : groundTransfer);
    half transfer2 = m2 < 4 ? rockTransfer : (m2 == 10 ? 0.0 : groundTransfer);
    half3 mean1 = _SurfMean[m1].rgb, mean2 = _SurfMean[m2].rgb;

    // Texture detail down to the 1024 m scale; fades into the means beyond 16-32 m per pixel (15-30 km away at 1080p):
    // distant mountains keep the texture of their rock instead of turning into flat color a few kilometers away.
    float tile1 = _SurfMean[m1].w, tile2 = _SurfMean[m2].w;
    half detail = saturate(2.0 - footprint / 16.0);
    half normalStrength = strength * detail;
    float s1 = ScalePosition(footprint, tile1), s2 = ScalePosition(footprint, tile2);

    half3 albedo;
    half3 rawAlbedo = 0.5;
    float3 nOutP = nP;
    half smoothness, occlusion;
    half3 mask1 = half3(0.25, 1.0, 0.5), mask2 = half3(0.25, 1.0, 0.5);
    if (detail > 0.0)
    {
        float3 blend = TriplanarBlend(nP);
        LayerSample a = SampleLayerMultiScale(c.p, nP, blend, m1, tile1, s1, normalStrength, mean1);
        LayerSample b;
        b.albedo = mean2; b.normalP = nP; b.mask = mask2;
        if (w2 > 0.01)
        {
            b = SampleLayerMultiScale(c.p, nP, blend, m2, tile2, s2, normalStrength, mean2);
        }
        // Height blend: the two materials meet along the higher one's relief.
        float depth = 0.25;
        float h1 = a.mask.b + w1, h2 = b.mask.b + w2;
        float top = max(h1, h2) - depth;
        // The second material only enters the height blend gradually above its sampling threshold (V0.2.0F.1: it used
        // to start at full height-blend influence at w2 = 0.01, a hard material line).
        float b1 = max(h1 - top, 0.0) * step(1e-4, w1), b2 = max(h2 - top, 0.0) * smoothstep(0.01, 0.08, w2);
        float k = b1 / max(b1 + b2, 1e-5);
        half3 alb1 = TransferColor(a.albedo, mean1, target1, transfer1);
        half3 alb2 = TransferColor(b.albedo, mean2, target2, transfer2);
        albedo = lerp(alb2, alb1, k);
        rawAlbedo = lerp(lerp(mean2, mean1, w1), lerp(b.albedo, a.albedo, k), detail);
        albedo = lerp(lerp(TransferColor(mean2, mean2, target2, transfer2), TransferColor(mean1, mean1, target1, transfer1), w1), albedo, detail);
        nOutP = normalize(lerp(b.normalP, a.normalP, k));
        half3 mask = lerp(b.mask, a.mask, k);
        smoothness = lerp(lerp(0.25, 0.2, c.rockMask), mask.r, detail);
        occlusion = lerp(1.0, mask.g, detail * strength);
        mask1 = a.mask; mask2 = b.mask;
        s.debug = 0;
    }
    else
    {
        albedo = TransferColor(mean2, mean2, target2, transfer2) * (1.0 - w1) + TransferColor(mean1, mean1, target1, transfer1) * w1;
        rawAlbedo = lerp(mean2, mean1, w1);
        smoothness = lerp(0.25, 0.2, c.rockMask);
        occlusion = 1.0;
    }

    // Snow keeps the biome's color and smoothness on all but steep rock (as in the procedural mode).
    // V0.2.0F.2: snow is a material of its own (the snow scan); the biome-color overlay is gone.
    half snowCover = 0.0;
    half rawLuma = dot(rawAlbedo, half3(0.3, 0.59, 0.11));
    half meanLuma = dot(lerp(mean2, mean1, w1), half3(0.3, 0.59, 0.11));
    half grain = clamp(rawLuma / max(meanLuma, 1e-3), 0.6, 1.4);
    albedo = lerp(albedo, soilColor * lerp(1.0, grain, 0.5), snowCover);
    smoothness = lerp(smoothness, 0.45, snowCover);
    nOutP = normalize(lerp(nOutP, nP, snowCover * 0.6));

    // Loose ground (sand, gravel) varies at the landscape scale (V0.2.0F.1): planet-anchored 256 / 64 m patches of
    // brightness and warmth, so broad sandy surfaces (beaches, desert floors) are not one uniform tint. Rock keeps the
    // geology's macro tone (strata, streaks), already varied.
    half loose = 1.0 - c.rockMask;
    if (loose > 0.0 && c.w128 > 0.0)
    {
        float patches = GroundNoise(c.p, 256.0, c.seed + 71u) * 0.6 + GroundNoise(c.p, 64.0, c.seed + 72u) * 0.4 - 0.5;
        float warmth = GroundNoise(c.p, 512.0, c.seed + 73u) - 0.5;
        half3 shift = half3(1.0 + 0.06 * warmth, 1.0, 1.0 - 0.08 * warmth) * (1.0 + 0.22 * patches);
        albedo *= lerp(half3(1.0, 1.0, 1.0), shift, loose * (1.0 - snowCover));
    }

    // 5. Coast: wet band and below sea level, then a damp zone fading inland over a few times the band's height.
    half damp = CoastalDamp(c) * (1.0 - snowCover);
    albedo *= 1.0 - 0.1 * damp * loose;
    half wet = CoastalWetness(c) * (1.0 - snowCover);
    half porous = 1.0 - c.rockMask * 0.5; // sand darkens more than rock
    albedo *= lerp(1.0, lerp(0.8, 0.62, porous), wet);
    smoothness = lerp(smoothness, lerp(0.42, 0.5, porous), wet);
    nOutP = normalize(lerp(nOutP, nP, wet * 0.35)); // water fills the micro relief
    s.wet = wet;

    s.albedo = lerp(soilColor, albedo, strength);
    s.normalWS = normalize(mul(nOutP, planetFromWorld)); // planet -> world (rotation part, uniform scale)
    s.smoothness = smoothness;
    s.occlusion = occlusion;

    // Debug views.
    float view = _SurfParams.z;
    s.debug = 0;
    if (view > 0.5)
    {
        static const half3 ids[SURFACE_MATERIALS] = { half3(0.6, 0.6, 0.62), half3(0.9, 0.45, 0.2), half3(0.95, 0.9, 0.7), half3(0.15, 0.15, 0.2),
            half3(0.95, 0.85, 0.4), half3(0.45, 0.35, 0.6), half3(0.2, 0.75, 0.2), half3(0.75, 0.75, 0.25), half3(0.1, 0.4, 0.15),
            half3(0.45, 0.28, 0.15), half3(1.0, 1.0, 1.0) };
        if (view < 1.5) s.debug = ids[m1] * w1 + ids[m2] * w2;
        else if (view < 2.5) s.debug = s.albedo;
        else if (view < 3.5) s.debug = s.normalWS * 0.5 + 0.5;
        else if (view < 4.5) s.debug = (1.0 - smoothness).xxx;
        else if (view < 5.5) s.debug = TriplanarBlend(nP);
        else
        {
            // Scale: 2 m red, 16 m green, 128 m blue, 1024 m white; mixed colors inside the transition bands; dark
            // where the textures have faded into the means.
            if (view > 6.5)
            {
                s.debug = saturate(s.albedo / max(rawAlbedo, 1e-3) * 0.5);
                return s;
            }
            static const half3 scales[4] = { half3(0.9, 0.2, 0.15), half3(0.2, 0.8, 0.25), half3(0.2, 0.35, 0.95), half3(0.95, 0.95, 0.95) };
            float2 state = ScaleBlend(s1);
            int i = (int)state.x;
            s.debug = lerp(scales[i], scales[i + 1], state.y) * lerp(0.25, 1.0, detail);
        }
    }
    return s;
}

// GGX specular (dielectric F0 = 0.04) with energy-normalized Lambert diffuse; the caller adds the ambient terms.
half3 PbrDirect(half3 albedo, half smoothness, float3 n, float3 v, float3 l, half3 lightColor, half wrap)
{
    half perceptual = 1.0 - smoothness;
    half rough = max(perceptual * perceptual, 0.02);
    half nl = dot(n, l);
    half diffuseTerm = saturate((nl + wrap) / (1.0 + wrap));
    float3 h = SafeNormalize(l + v);
    float nh = saturate(dot(n, h));
    float lh = saturate(dot(l, h));
    float a2 = rough * rough;
    float d = nh * nh * (a2 - 1.0) + 1.0;
    float spec = a2 / (d * d * max(0.1, lh * lh) * (rough * 4.0 + 2.0)); // URP's minimal GGX form
    float fresnel = 0.04 + 0.96 * pow(1.0 - lh, 5.0);
    return lightColor * (albedo * diffuseTerm + spec * fresnel * saturate(nl));
}

#endif
