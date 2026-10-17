// Planetwalker surface geology appearance (V0.2.0E), included by PlanetTerrain.shader after its ground noise
// (GroundHash, GroundNoise, GroundOctave, GroundBumpHeight; planet-anchored, periodic over 1024 m, faded by footprint).
//
// Per vertex (AdaptivePatchGeometryBuilder, from the SurfaceGeology lattice: the same values at every LOD level):
//   grid (TEXCOORD0): patch grid u, v; height above sea level (m); reference slope (m / m)
//   geo  (TEXCOORD1): bedrock, sediment, erosion, strata (0 .. 1)
//   flow (TEXCOORD2): downslope direction (planet space), concavity (+ hollows)
//   rock (TEXCOORD3): province rock color
//
// Three materials, large structures first: bedrock where the geology exposes it (boundaries broken by noise at the
// scale the pixel can show), loose sediment (debris and gravel derived from the local rock, alluvium on vegetated
// land) on flat and concave ground, the biome's soil elsewhere; snow (the biome color) stays on top except on steep
// rock. Rock carries its province color, a body variation, strata where the province is sedimentary (horizontal
// layers at their true altitude: the planet's own height above sea level, warped gently, two thicknesses), erosion
// streaks down the slope (noise smeared along the downslope direction: still a function of the planet position) and
// weathering (darker hollows, lighter edges). Every pattern fades with the pixel footprint: from orbit nothing of it
// is evaluated, at 1.7 m the gravel and soil grain take over. The shading normal gets a bump that depends on the
// material (rougher rock, stones in the gravel, soft soil).
#ifndef PLANETWALKER_TERRAIN_GEOLOGY_INCLUDED
#define PLANETWALKER_TERRAIN_GEOLOGY_INCLUDED

struct GeologySurface
{
    half3 albedo;
    float3 normalWS;
    half3 debug;   // debug view color (unlit), when one is selected
};

// Tone (0 .. 1) of the stratum at altitude z: one hashed value per layer of this thickness, sharp-ish boundaries.
float GeoLayerTone(float z, float thickness, uint seed)
{
    float q = z / thickness;
    float i = floor(q);
    float f = q - i;
    float a = (GroundHash(int3((int)i, 7, 3), seed) & 0xFFFFu) / 65535.0;
    float b = (GroundHash(int3((int)i + 1, 7, 3), seed) & 0xFFFFu) / 65535.0;
    return lerp(a, b, smoothstep(0.82, 1.0, f));
}

// Distance to the nearest block boundary (F2 - F1, in cells) of a periodic 3D Voronoi pattern, one jittered feature
// point per cell (cell: a power of two dividing the period): the polygonal fracture network of jointed rock.
float GeoCellEdge(float3 p, float cell, uint seed, out float blockId)
{
    blockId = 0.0;
    float3 q = p / cell;
    float3 i = floor(q);
    float3 f = q - i;
    int mask = (int)(GROUND_PERIOD / cell) - 1;
    float d1 = 8.0, d2 = 8.0;
    [unroll] for (int z = -1; z <= 1; z++)
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
    {
        int3 c = ((int3)i + int3(x, y, z)) & mask;
        uint h = GroundHash(c, seed);
        float3 jitter = float3(h & 1023u, (h >> 10) & 1023u, (h >> 20) & 1023u) / 1023.0;
        float3 r = float3(x, y, z) + 0.1 + jitter * 0.8 - f;
        float d = dot(r, r);
        if (d < d1) { d2 = d1; d1 = d; blockId = (h >> 8 & 0xFFFFu) / 65535.0; }
        else if (d < d2) { d2 = d; }
    }
    return sqrt(d2) - sqrt(d1);
}

// Streaks down the slope: value noise averaged along the downslope direction (elongated cells), centered.
float GeoStreaks(float3 p, float3 flow, float cell, float stretch, uint seed)
{
    float s = 0.0;
    [unroll] for (int k = 0; k < 4; k++)
    {
        s += GroundNoise(p + flow * ((k - 1.5) * stretch), cell, seed);
    }
    return (s * 0.25 - 0.5) * 2.0;
}

// Relief of the ground (meters, centered), material dependent: the V0.2.0D fine relief (8 .. 1 m), rougher on rock,
// softer on soil; stones in the sediment (0.5 m and 12 cm).
float GeoBumpHeight(float3 p, float footprint, uint seed, float rockMask, float sedMask)
{
    float h = GroundBumpHeight(p, footprint, seed) * lerp(0.6, 2.2, rockMask);
    // Jointed rock: each block slightly raised or sunk, grooves along the fractures; rough grain at close range.
    float w4 = GroundOctave(4.0, footprint);
    if (w4 > 0.0 && rockMask > 0.01)
    {
        float block;
        float edge = GeoCellEdge(p, 4.0, seed + 16u, block);
        float open = smoothstep(0.3, 0.7, GroundNoise(p, 16.0, seed + 22u)); // as the visible fractures
        h += ((block - 0.5) * 0.08 * open - (1.0 - smoothstep(0.0, 0.12, edge)) * 0.07 * open) * rockMask * w4;
        float w006 = GroundOctave(0.0625, footprint);
        if (w006 > 0.0)
        {
            h += (GroundNoise(p, 0.0625, seed + 19u) - 0.5) * 0.012 * rockMask * w006;
        }
    }
    float w05 = GroundOctave(0.5, footprint);
    if (w05 > 0.0 && sedMask > 0.0)
    {
        float stones = smoothstep(0.5, 0.85, GroundNoise(p, 0.5, seed + 41u)) * 0.09 * w05;
        float w0125 = GroundOctave(0.125, footprint);
        if (w0125 > 0.0)
        {
            stones += smoothstep(0.45, 0.85, GroundNoise(p, 0.125, seed + 42u)) * 0.025 * w0125;
        }
        h += stones * sedMask;
    }
    return h;
}

// Shared per-pixel geology context (V0.2.0F): position in planet-space meters, footprint, the per-vertex fields and
// the material masks with their scale-aware breakup. Used by the procedural appearance and by the PBR materials.
struct GeologyContext
{
    float3 p;            // planet-space meters from the snapped origin
    float footprint;     // meters per pixel
    uint seed;
    float3 upWS;
    float heightM, refSlope, bedrock, sediment, erosion, strata, concavity, flowLength;
    float3 flowP;
    float flowWeight;    // 0 .. 1: how defined the downslope direction is (V0.2.0F.1)
    float w128, w32, w8;
    float rockMask, sedMask, soilMask;
    half snow, vegetated;
};

// footprintOverride > 0 (V0.2.0F.1, PBR): the stable distance-based footprint instead of the screen derivatives.
GeologyContext GeologySetup(float3 positionWS, half3 soilColor, float4 grid, float4 geo, float4 flow, float footprintOverride)
{
    GeologyContext c;
    float3x3 planetFromWorld = (float3x3)_PlanetFromWorld;
    c.p = mul(planetFromWorld, positionWS - _GroundOrigin.xyz) * _GroundOrigin.w; // planet-space meters
    float3 p = c.p;
    c.footprint = footprintOverride > 0.0 ? footprintOverride : max(length(ddx(p)), length(ddy(p)));
    float footprint = c.footprint;
    c.seed = (uint)_GroundParams.y + 101u;
    uint seed = c.seed;
    c.upWS = normalize(positionWS - _AtmoShell.xyz);
    c.heightM = grid.z;
    c.refSlope = grid.w;
    c.bedrock = geo.x; c.sediment = geo.y; c.erosion = geo.z; c.strata = geo.w;
    float bedrock = c.bedrock, sediment = c.sediment;
    c.flowLength = length(flow.xyz);
    float flowLength = c.flowLength;
    c.flowP = flowLength > 1e-4 ? flow.xyz / flowLength : float3(0, 0, 0);
    // The per-vertex downslope directions are unit vectors; between vertices pointing different ways (ridges, valley
    // floors) their interpolation shortens. V0.2.0E switched the slope-oriented patterns on and off at a length of 0.05,
    // a hard threshold that drew straight lines across the triangles (darker streaked rock on one side, plain on the
    // other). The patterns now fade with the direction's definition.
    c.flowWeight = smoothstep(0.05, 0.4, flowLength);
    float flowWeight = c.flowWeight;
    float3 flowP = c.flowP;
    c.concavity = flow.w;

    // ---- Materials: boundaries broken at the scales the pixel resolves (smooth from far away).
    float w128 = GroundOctave(128.0, footprint), w32 = GroundOctave(32.0, footprint), w8 = GroundOctave(8.0, footprint);
    c.w128 = w128; c.w32 = w32; c.w8 = w8;
    // Elongated down the slope where there is one (rock ribs, talus chutes, gullies), isotropic on flat ground.
    float breakup = 0.0;
    if (w128 > 0.0)
    {
        breakup = (GroundNoise(p, 128.0, seed) - 0.5) * 0.5 * w128;
        if (w32 > 0.0) breakup += lerp(GroundNoise(p, 32.0, seed + 1u) - 0.5, flowWeight > 0.0 ? GeoStreaks(p, flowP, 32.0, 50.0, seed + 1u) * 0.5 : 0.0, flowWeight) * 0.4 * w32;
        if (w8 > 0.0) breakup += lerp(GroundNoise(p, 8.0, seed + 2u) - 0.5, flowWeight > 0.0 ? GeoStreaks(p, flowP, 8.0, 12.0, seed + 2u) * 0.5 : 0.0, flowWeight) * 0.25 * w8;
    }
    c.rockMask = smoothstep(0.3, 0.72, bedrock + breakup * 0.35);
    float largeBreakup = w128 > 0.0 ? (GroundNoise(p, 128.0, seed) - 0.5) * 0.5 * w128 : 0.0;
    c.sedMask = smoothstep(0.3, 0.6, sediment + largeBreakup * 0.3) * (1.0 - c.rockMask); // coherent bodies
    c.soilMask = saturate(1.0 - c.rockMask - c.sedMask);
    c.snow = smoothstep(0.6, 0.8, min(soilColor.r, min(soilColor.g, soilColor.b)));
    c.vegetated = saturate((soilColor.g - soilColor.r) * 6.0);
    return c;
}

// Macro rock tone (V0.2.0F shared): province color with body variation, strata at their true altitude, erosion
// streaks down the slope and weathering. The large structures, without the close-range joints and grain.
half3 GeologyRockMacro(in GeologyContext c, half3 rockTint, out float strataShow, out float strataZ)
{
    float3 p = c.p;
    float footprint = c.footprint;
    uint seed = c.seed;
    float w128 = c.w128, w32 = c.w32, w8 = c.w8;
    float heightM = c.heightM, refSlope = c.refSlope, strata = c.strata, erosion = c.erosion, concavity = c.concavity, flowLength = c.flowLength;
    float3 flowP = c.flowP;
    half3 rock = rockTint;
    if (w128 > 0.0)
    {
        float body = GroundNoise(p, 64.0, seed + 3u) * 0.6 + (w32 > 0.0 ? GroundNoise(p, 16.0, seed + 4u) * 0.4 : 0.2);
        rock *= 0.78 + 0.44 * body;
    }
    strataShow = strata * smoothstep(0.2, 0.55, refSlope);
    strataZ = heightM;
    float w37 = GroundOctave(18.0, footprint), w7 = GroundOctave(4.5, footprint);
    if (strataShow > 0.01 && w37 > 0.0)
    {
        float warp = (GroundNoise(p, 256.0, seed + 5u) - 0.5) * 30.0 + (GroundNoise(p, 64.0, seed + 6u) - 0.5) * 6.0;
        float z = heightM + warp;
        strataZ = z;
        float thick = GeoLayerTone(z, 18.0, seed + 7u);
        float tone = 1.0 + (thick - 0.5) * 0.8 * w37;
        if (w7 > 0.0)
        {
            tone *= 1.0 + (GeoLayerTone(z, 4.5, seed + 8u) - 0.5) * 0.35 * w7;
        }
        half3 layered = rock * tone;
        layered = lerp(layered, layered * half3(1.12, 0.98, 0.84), thick * 0.6 * w37); // alternating warmer beds
        rock = lerp(rock, layered, strataShow);
    }
    if (erosion > 0.01 && c.flowWeight > 0.0 && w32 > 0.0)
    {
        float streaks = GeoStreaks(p, flowP, 8.0, 14.0, seed + 9u) * w8 + GeoStreaks(p, flowP, 32.0, 48.0, seed + 10u) * 0.6 * w32;
        rock *= 1.0 + streaks * 0.6 * erosion * c.flowWeight;
    }
    rock *= 1.0 - 0.18 * saturate(concavity * 2.0) + 0.08 * saturate(-concavity * 2.0);
    return rock;
}

GeologySurface EvaluateGeology(float3 positionWS, float3 normalWS, half3 soilColor, float4 grid, float4 geo, float4 flow, half3 rockTint)
{
    GeologySurface s;
    s.albedo = soilColor;
    s.normalWS = normalWS;
    s.debug = 0;

    GeologyContext c = GeologySetup(positionWS, soilColor, grid, geo, flow, -1.0);
    float3x3 planetFromWorld = (float3x3)_PlanetFromWorld;
    float3 p = c.p;
    float footprint = c.footprint;
    uint seed = c.seed;
    half strength = _GeoParams.x;
    float3 upWS = c.upWS;
    float heightM = c.heightM, refSlope = c.refSlope;
    float bedrock = c.bedrock, sediment = c.sediment, erosion = c.erosion, strata = c.strata;
    float w128 = c.w128, w32 = c.w32, w8 = c.w8;
    float rockMask = c.rockMask, sedMask = c.sedMask, soilMask = c.soilMask;
    half snow = c.snow;

    // ---- Debug views (flat colors, lit simply by the caller).
    float view = _GeoParams.y;
    if (view > 0.5)
    {
        if (view < 1.5) // identity
        {
            s.debug = rockTint * rockMask + half3(0.95, 0.82, 0.35) * sedMask + half3(0.25, 0.55, 0.2) * soilMask;
            s.debug = lerp(s.debug, half3(0.25, 0.45, 1.0), 0.6 * strata * rockMask);
        }
        else if (view < 2.5) s.debug = bedrock.xxx;
        else if (view < 3.5) s.debug = sediment.xxx;
        else if (view < 4.5) s.debug = erosion.xxx;
        else
        {
            float t = saturate(refSlope / 1.2);
            s.debug = lerp(lerp(half3(0.1, 0.3, 0.9), half3(0.2, 0.85, 0.3), saturate(t * 2.0)), half3(0.95, 0.25, 0.15), saturate(t * 2.0 - 1.0));
        }
        return s;
    }

    // ---- Rock: province color, body variation, strata, erosion streaks, weathering.
    float strataShow, strataZ;
    half3 rock = GeologyRockMacro(c, rockTint, strataShow, strataZ);
    // Close range: jointed blocks (polygonal fractures of a 3D Voronoi network, 4 m blocks, 1 m cracks within),
    // bedding partings where the rock is layered, weathering speckles. Lines antialiased by their screen width.
    float w4 = GroundOctave(4.0, footprint);
    if (w4 > 0.0 && rockMask > 0.01)
    {
        float block;
        float edge = GeoCellEdge(p, 4.0, seed + 16u, block);
        float width = max(0.035, footprint / 4.0 * 1.5); // in cells: at least ~1.5 pixels
        // Fractures read only up close (a pixel under ~12 cm): from farther they would make a crackle glaze.
        float jointsNear = GroundOctave(0.5, footprint);
        float joints = (1.0 - smoothstep(0.0, width, edge)) * jointsNear;
        rock *= 1.0 + (block - 0.5) * 0.12 * w4; // each block its own weathering tone
        float w1 = GroundOctave(1.0, footprint);
        if (w1 > 0.0)
        {
            float blockFine;
            float edgeFine = GeoCellEdge(p, 1.0, seed + 17u, blockFine);
            float widthFine = max(0.03, footprint * 1.5);
            joints = max(joints, (1.0 - smoothstep(0.0, widthFine, edgeFine)) * 0.25 * w1 * step(0.55, blockFine));
            float w025r = GroundOctave(0.25, footprint);
            if (w025r > 0.0)
            {
                rock *= 1.0 + (GroundNoise(p, 0.25, seed + 18u) - 0.5) * 0.3 * w025r;
                // Mineral grain (6 cm, 1.5 cm): speckled crystals or sand grains of the rock.
                float w006 = GroundOctave(0.0625, footprint);
                if (w006 > 0.0)
                {
                    rock *= 1.0 + (GroundNoise(p, 0.0625, seed + 20u) - 0.5) * 0.3 * w006;
                    float w0015 = GroundOctave(0.015625, footprint);
                    if (w0015 > 0.0)
                    {
                        rock *= 1.0 + (GroundNoise(p, 0.015625, seed + 21u) - 0.5) * 0.35 * w0015;
                    }
                }
            }
        }
        if (strataShow > 0.01)
        {
            float bed = frac(strataZ / 4.5); // the thin layers' own boundaries
            float bedWidth = max(0.03, footprint / 4.5 * 1.5);
            joints = max(joints, (1.0 - smoothstep(0.0, bedWidth, min(bed, 1.0 - bed))) * 0.8 * strataShow * w4);
        }
        // Fractures open and close along their length (no complete mosaic): faded by a 16 m noise.
        joints *= smoothstep(0.3, 0.7, GroundNoise(p, 16.0, seed + 22u));
        rock *= 1.0 - 0.4 * joints;
    }

    // ---- Sediment: debris and gravel from the local rock (paler), alluvium where the land is vegetated.
    half vegetated = c.vegetated;
    half3 debris = lerp(rockTint * 1.15 + 0.03, soilColor, 0.3 + 0.45 * vegetated);
    if (w8 > 0.0)
    {
        debris *= 0.92 + 0.16 * GroundNoise(p, 4.0, seed + 11u) * w8 + 0.08 * (1.0 - w8);
    }
    float w05 = GroundOctave(0.5, footprint);
    if (w05 > 0.0 && sedMask > 0.0)
    {
        float stones = smoothstep(0.55, 0.8, GroundNoise(p, 0.5, seed + 41u)) * w05;
        float w0125 = GroundOctave(0.125, footprint);
        if (w0125 > 0.0)
        {
            stones = max(stones, smoothstep(0.5, 0.82, GroundNoise(p, 0.125, seed + 42u)) * 0.85 * w0125);
        }
        half3 stoneColor = rockTint * (0.7 + 0.7 * GroundNoise(p, 0.25, seed + 43u));
        debris = lerp(debris, stoneColor, stones * (1.0 - 0.6 * vegetated));
    }

    // ---- Soil: the biome color with patches and grain (the V0.2.0D ground tint), crumbly at close range.
    // Arid ground is made of the local rock: bare soil takes some of its color (less on vegetation and snow).
    half3 soil = lerp(soilColor, rockTint * 1.2 + 0.02, 0.35 * (1.0 - vegetated) * (1.0 - snow));
    if (w128 > 0.0)
    {
        float patchNoise = GroundNoise(p, 64.0, seed + 12u) * 0.65 + (w32 > 0.0 ? GroundNoise(p, 16.0, seed + 13u) * 0.35 : 0.175);
        half dark = smoothstep(0.58, 0.78, patchNoise) * (1.0 - snow);
        soil = lerp(soil, soil * half3(0.8, 0.72, 0.62), dark * 0.55) * (1.0 + smoothstep(0.42, 0.22, patchNoise) * 0.1);
        float w025 = GroundOctave(0.25, footprint);
        if (w025 > 0.0)
        {
            soil *= 1.0 + (GroundNoise(p, 0.25, seed + 14u) - 0.5) * 0.3 * w025;
            float w006 = GroundOctave(0.0625, footprint);
            if (w006 > 0.0)
            {
                soil *= 1.0 + (GroundNoise(p, 0.0625, seed + 15u) - 0.5) * 0.35 * w006; // grains and crumbs
            }
        }
    }

    // Thin cover over weathered rock around the outcrops' edges (between the materials, not a hard line).
    rock = lerp(rock, debris, 0.35 * (1.0 - rockMask) * rockMask * 4.0 * 0.5);
    half3 albedo = rock * rockMask + debris * sedMask + soil * soilMask;
    half snowCover = snow * (1.0 - 0.85 * rockMask * smoothstep(0.5, 0.9, refSlope));
    albedo = lerp(albedo, soilColor, snowCover);
    s.albedo = lerp(soilColor, albedo, strength);

    // ---- Bump (finite differences of the material relief; step from the footprint, 2 .. 25 cm).
    if (GroundOctave(8.0, footprint) > 0.0)
    {
        float3 t = normalize(cross(upWS, abs(upWS.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0)));
        float3 b = cross(upWS, t);
        float3 tp = normalize(mul(planetFromWorld, t));
        float3 bp = normalize(mul(planetFromWorld, b));
        float e = clamp(footprint, 0.02, 0.25);
        float h0 = GeoBumpHeight(p, footprint, seed, rockMask, sedMask);
        float gt = (GeoBumpHeight(p + tp * e, footprint, seed, rockMask, sedMask) - h0) / e;
        float gb = (GeoBumpHeight(p + bp * e, footprint, seed, rockMask, sedMask) - h0) / e;
        s.normalWS = normalize(normalWS - (gt * t + gb * b) * strength * (1.0 - snowCover * 0.6));
    }
    return s;
}

#endif
