// Planetwalker terrain: vertex-colored surface lit by the main directional light
// (the star) plus scene ambient. URP only.
Shader "Planetwalker/PlanetTerrain"
{
    Properties
    {
        _AmbientStrength ("Ambient Strength", Range(0, 2)) = 1
        _LightWrap ("Light Wrap", Range(0, 0.5)) = 0.08
        _Brightness ("Brightness", Range(0, 2)) = 1

        // Atmospheric haze between the camera and the terrain (V0.1.2B). Set per renderer by
        // PlanetVisualController from the atmosphere material; strength 0 (default) = no haze.
        [HideInInspector] _AtmoShell ("Atmosphere center (xyz) / shell radius (w), world", Vector) = (0, 0, 0, 1)
        [HideInInspector] _AtmoParams ("Ocean radius, density, falloff, strength", Vector) = (0.975, 0.03, 3, 0)
        [HideInInspector] _AtmoParams2 ("Night level, warm strength, surface cap", Vector) = (0.02, 0.3, 5, 0)
        [HideInInspector] _AtmoColor ("Atmosphere day color", Color) = (0.30, 0.55, 1.0, 1)
        [HideInInspector] _AtmoWarmColor ("Atmosphere warm color", Color) = (1.0, 0.55, 0.30, 1)

        // Cloud shadows (V0.1.2C), set per renderer; enabled 0 (default) = none.
        [HideInInspector][NoScaleOffset] _CloudCube ("Cloud Field", Cube) = "black" {}
        [HideInInspector] _CloudParams ("Threshold, density, detail, enabled", Vector) = (0, 0.75, 0.16, 0)
        [HideInInspector] _CloudParams2 ("Weather, convection, shadow, shell radius", Vector) = (0.5, 0.5, 0, 1)
        [HideInInspector] _CloudParams3 ("Axis (xyz), time", Vector) = (0, 1, 0, 0)
        [HideInInspector] _CloudSeed ("Noise offset (xyz), softness", Vector) = (0, 0, 0, 0.12)
        [HideInInspector] _CloudLocalScale ("Planet units per object unit", Float) = 1
        // Night darkness (V0.1.2D), set per renderer; 1 (default) = ambient everywhere (previous look).
        [HideInInspector] _NightAmbient ("Ambient kept on the night side", Float) = 1
        // Skylight on the day side (V0.2.0D), times the atmosphere strength.
        _SkylightStrength ("Skylight Strength", Range(0, 1)) = 0.35
        // Light bounced by the sunlit ground onto slopes facing away from the sky (V0.2.0E).
        _BounceStrength ("Ground Bounce", Range(0, 1)) = 0.6
        // Close-range ground (V0.2.0D), adaptive patches only, set per frame by AdaptiveTerrainRenderer; enabled 0
        // (default) = none (legacy faces, surface detail off).
        [HideInInspector] _GroundOrigin ("Noise origin (world xyz), meters per planet unit", Vector) = (0, 0, 0, 1)
        [HideInInspector] _GroundParams ("Strength, seed, enabled, -", Vector) = (0, 0, 0, 0)
        [HideInInspector] _GeoParams ("Geology strength, debug view, enabled, -", Vector) = (0, 0, 0, 0)
        [HideInInspector] _PatchStitch ("Stitched edges E, N, W, S", Vector) = (0, 0, 0, 0)
        [HideInInspector] _PatchMorph ("Normal blend band", Float) = 0.25
        // Physically based materials (V0.2.0F), set per frame by AdaptiveTerrainRenderer: mode (1 = PBR), detail
        // strength, debug view, -. Arrays from PlanetSurfaceLibrary. Mode 0 (default) = the V0.2.0E procedural look.
        [HideInInspector] _SurfParams ("PBR mode, strength, debug view, -", Vector) = (0, 1, 0, 0)
        [HideInInspector][NoScaleOffset] _SurfAlbedo ("Surface Albedo", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset] _SurfNormal ("Surface Normal", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset] _SurfMask ("Surface Mask", 2DArray) = "" {}
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half _AmbientStrength;
            half _LightWrap;
            half _Brightness;
            float4 _AtmoShell;
            float4 _AtmoParams;
            float4 _AtmoParams2;
            half4 _AtmoColor;
            half4 _AtmoWarmColor;
            float4 _CloudParams;
            float4 _CloudParams2;
            float4 _CloudParams3;
            float4 _CloudSeed;
            float _CloudLocalScale;
            half _NightAmbient;
            half _SkylightStrength;
            half _BounceStrength;
            // Adaptive terrain patches (V0.2.0B): vertices are relative to each patch's origin, so the planet-space
            // position (cloud shadows) comes from the world position and the planet's world-to-object matrix.
            // Both zero for the legacy faces, whose object space is already the planet's.
            float4x4 _PlanetFromWorld;
            float _AdaptivePatch;
            float4 _GroundOrigin;
            float4 _GroundParams;
            // Surface geology (V0.2.0E): strength, debug view, enabled. LOD seam: stitched edges (E, N, W, S) of the
            // patch being drawn and the width of the normal blend band (fraction of the patch).
            float4 _GeoParams;
            float4 _PatchStitch;
            float _PatchMorph;
            float4 _SurfParams;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "PlanetAtmosphereCommon.hlsl"
            #include "PlanetCloudsCommon.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 coarseNormalOS : TANGENT; // adaptive patches (V0.2.0E): the coarser level's normal
                half4 color : COLOR;
                float4 grid : TEXCOORD0;         // adaptive patches: grid u, v, height above sea (m), reference slope
                float4 geo : TEXCOORD1;          // bedrock, sediment, erosion, strata
                float4 flow : TEXCOORD2;         // downslope direction, concavity
                float4 rock : TEXCOORD3;         // province rock color, sedimentary weight (V0.2.0F)
                float4 cover : TEXCOORD4;        // green grass, dry grass, forest floor, snow (V0.2.0F.2)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                float4 grid : TEXCOORD3;
                float4 geo : TEXCOORD4;
                float4 flow : TEXCOORD5;
                half4 rock : TEXCOORD6;          // rock color, sedimentary weight
                float family : TEXCOORD7;        // rock family field (V0.2.0F)
                half4 cover : TEXCOORD8;         // ground cover (V0.2.0F.2)
                half4 color : COLOR;
            };

            // ---- Close-range ground (V0.2.0D) ------------------------------------------------------------------------
            // The terrain geometry stops at the finest detail band (~80 m) and its colors come from coarse fields, so
            // within a few hundred meters the ground reads as smooth, uniform plastic. This layer adds what the eye
            // expects there, as presentation only (the heights, the sampler, the meshes are untouched): soil patches,
            // bare rock on locally steep ground, small brightness variation and a fine bump, from 1 to 64 m.
            // Anchoring: the noise is a function of the planet-space position in meters, measured from an origin
            // snapped to a 1024 m lattice near the camera (AdaptiveTerrainRenderer, in double): small, precise float
            // coordinates, and the noise is periodic over 1024 m, so moving the origin by a lattice step changes
            // nothing (no swimming, no pops, no dependence on the floating origin or on the patch level).
            // Each octave fades out with the pixel footprint (no aliasing, nothing at all from altitude or orbit).
            #define GROUND_PERIOD 1024.0

            uint GroundHash(int3 c, uint seed)
            {
                uint h = (uint)c.x * 73856093u ^ (uint)c.y * 19349663u ^ (uint)c.z * 83492791u ^ seed * 2654435761u;
                h ^= h >> 15;
                h *= 0x2c1b3c6du;
                h ^= h >> 12;
                h *= 0x297a2d39u;
                h ^= h >> 15;
                return h;
            }

            // Value noise in [0, 1], periodic over GROUND_PERIOD (cell: a power of two dividing it).
            float GroundNoise(float3 p, float cell, uint seed)
            {
                float3 q = p / cell;
                float3 i = floor(q);
                float3 f = q - i;
                float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
                int mask = (int)(GROUND_PERIOD / cell) - 1;
                int3 c0 = (int3)i & mask;
                int3 c1 = ((int3)i + 1) & mask;
                const float k = 1.0 / 16777215.0;
                float n000 = (GroundHash(int3(c0.x, c0.y, c0.z), seed) & 0xFFFFFFu) * k;
                float n100 = (GroundHash(int3(c1.x, c0.y, c0.z), seed) & 0xFFFFFFu) * k;
                float n010 = (GroundHash(int3(c0.x, c1.y, c0.z), seed) & 0xFFFFFFu) * k;
                float n110 = (GroundHash(int3(c1.x, c1.y, c0.z), seed) & 0xFFFFFFu) * k;
                float n001 = (GroundHash(int3(c0.x, c0.y, c1.z), seed) & 0xFFFFFFu) * k;
                float n101 = (GroundHash(int3(c1.x, c0.y, c1.z), seed) & 0xFFFFFFu) * k;
                float n011 = (GroundHash(int3(c0.x, c1.y, c1.z), seed) & 0xFFFFFFu) * k;
                float n111 = (GroundHash(int3(c1.x, c1.y, c1.z), seed) & 0xFFFFFFu) * k;
                return lerp(lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y),
                            lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y), u.z);
            }

            // Weight of an octave of this cell size for a pixel footprint (meters): 1 while a cell spans 4+ pixels,
            // 0 once it spans 2 or less.
            float GroundOctave(float cell, float footprint)
            {
                return saturate(2.0 - 4.0 * footprint / cell);
            }

            // Fine relief (meters, centered) from the 8 m to 1 m octaves, each faded by the footprint.
            float GroundBumpHeight(float3 p, float footprint, uint seed)
            {
                float h = 0.0;
                float cell = 8.0;
                [unroll] for (int o = 0; o < 4; o++)
                {
                    float w = GroundOctave(cell, footprint);
                    if (w > 0.0)
                    {
                        h += (GroundNoise(p, cell, seed + 11u + (uint)o) - 0.5) * cell * 0.10 * w;
                    }
                    cell *= 0.5;
                }
                return h;
            }

            struct GroundSurface
            {
                half3 albedo;     // multiplied into the vertex color
                half rock;        // 0 .. 1 cover by bare rock
                float3 normalWS;  // bumped normal
            };

            GroundSurface EvaluateGround(float3 positionWS, float3 normalWS, half3 baseColor)
            {
                GroundSurface g;
                g.albedo = 1.0;
                g.rock = 0.0;
                g.normalWS = normalWS;
                float3x3 planetFromWorld = (float3x3)_PlanetFromWorld;
                float3 p = mul(planetFromWorld, positionWS - _GroundOrigin.xyz) * _GroundOrigin.w; // meters
                float footprint = max(length(ddx(p)), length(ddy(p)));
                float near = GroundOctave(64.0, footprint);
                if (near <= 0.0)
                {
                    return g;
                }
                uint seed = (uint)_GroundParams.y;
                half strength = _GroundParams.x;
                float3 upWS = normalize(positionWS - _AtmoShell.xyz);

                // Fine bump: gradient of the relief along two tangent directions (any basis: only the full tangent
                // gradient is used), finite differences of 0.25 m.
                float3 t = normalize(cross(upWS, abs(upWS.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0)));
                float3 b = cross(upWS, t);
                float3 tp = normalize(mul(planetFromWorld, t));
                float3 bp = normalize(mul(planetFromWorld, b));
                const float e = 0.25;
                float h0 = GroundBumpHeight(p, footprint, seed);
                float gt = (GroundBumpHeight(p + tp * e, footprint, seed) - h0) / e;
                float gb = (GroundBumpHeight(p + bp * e, footprint, seed) - h0) / e;
                g.normalWS = normalize(normalWS - (gt * t + gb * b) * strength);

                // Soil and pale patches (64 / 16 m), small brightness variation (8 to 2 m).
                float patchNoise = GroundNoise(p, 64.0, seed + 3u) * 0.65 + GroundNoise(p, 16.0, seed + 4u) * 0.35;
                float grain = 0.0;
                float cell = 8.0;
                [unroll] for (int o = 0; o < 3; o++)
                {
                    grain += (GroundNoise(p, cell, seed + 7u + (uint)o) - 0.5) * GroundOctave(cell, footprint);
                    cell *= 0.5;
                }
                // Snow and ice (near-white vertex colors) stay clean: no soil, less rock.
                half snow = smoothstep(0.6, 0.8, min(baseColor.r, min(baseColor.g, baseColor.b)));
                half soil = smoothstep(0.58, 0.78, patchNoise) * (1.0 - snow);
                half pale = smoothstep(0.42, 0.22, patchNoise);
                half3 tint = lerp(half3(1.0, 1.0, 1.0), half3(0.80, 0.72, 0.62), soil * 0.55);
                tint *= 1.0 + pale * 0.10;
                tint *= 1.0 + grain * 0.22;

                // Bare rock where the ground is locally steep (the patch normal at this distance carries the 80 m band
                // the coarse rock field cannot see), broken up by the noise.
                float steep = 1.0 - saturate(dot(normalWS, upWS));
                g.rock = smoothstep(0.12, 0.42, steep + (patchNoise - 0.5) * 0.25) * near * strength * (1.0 - 0.6 * snow);
                g.albedo = lerp(half3(1.0, 1.0, 1.0), tint, near * strength);
                return g;
            }

            #include "PlanetTerrainGeology.hlsl"
            #include "PlanetTerrainMaterials.hlsl"

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionOS = _AdaptivePatch > 0.5 ? mul(_PlanetFromWorld, float4(output.positionWS, 1.0)).xyz : input.positionOS.xyz;
                float3 normalOS = input.normalOS;
                // LOD seam (V0.2.0E): along an edge stitched to a coarser neighbor, blend towards the coarser level's
                // normal (band limit x 2), which is exactly the normal the neighbor has on the shared vertices.
                if (_AdaptivePatch > 0.5 && dot(_PatchStitch, 1.0) > 0.0)
                {
                    float2 uv = input.grid.xy;
                    float band = max(_PatchMorph, 1e-3);
                    float w = max(max(_PatchStitch.x * (1.0 - smoothstep(0.0, band, 1.0 - uv.x)), _PatchStitch.y * (1.0 - smoothstep(0.0, band, 1.0 - uv.y))),
                                  max(_PatchStitch.z * (1.0 - smoothstep(0.0, band, uv.x)), _PatchStitch.w * (1.0 - smoothstep(0.0, band, uv.y))));
                    normalOS = normalize(lerp(normalOS, input.coarseNormalOS.xyz, w));
                }
                output.normalWS = TransformObjectToWorldNormal(normalOS);
                output.color = input.color;
                output.grid = input.grid;
                output.geo = input.geo;
                output.flow = input.flow;
                output.rock = input.rock;
                output.family = input.coarseNormalOS.w;
                output.cover = input.cover;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                half3 baseColor = input.color.rgb;
                bool pbr = _SurfParams.x > 0.5 && _AdaptivePatch > 0.5;
                half smoothness = 0.0;
                half occlusion = 1.0;
                if (pbr)
                {
                    // Physically based materials (V0.2.0F) distributed by the surface geology.
                    PbrSurface surface = EvaluateSurfacePbr(input.positionWS, normalWS, baseColor, input.grid, input.geo, input.flow,
                        input.rock.rgb, input.rock.a, input.family, input.cover);
                    if (_SurfParams.z > 0.5)
                    {
                        return half4(surface.debug, 1.0);
                    }
                    baseColor = surface.albedo;
                    normalWS = surface.normalWS;
                    smoothness = surface.smoothness;
                    occlusion = surface.occlusion;
                }
                else if (_GeoParams.z > 0.5 && _AdaptivePatch > 0.5)
                {
                    // Surface geology (V0.2.0E): replaces the V0.2.0D ground layer (whose soil tint it includes).
                    GeologySurface geology = EvaluateGeology(input.positionWS, normalWS, baseColor, input.grid, input.geo, input.flow, input.rock.rgb);
                    if (_GeoParams.y > 0.5)
                    {
                        Light debugLight = GetMainLight();
                        half lambert = 0.35 + 0.65 * saturate(dot(normalWS, debugLight.direction));
                        return half4(geology.debug * lambert, 1.0);
                    }
                    baseColor = geology.albedo;
                    normalWS = geology.normalWS;
                }
                else if (_GroundParams.z > 0.5 && _AdaptivePatch > 0.5)
                {
                    GroundSurface ground = EvaluateGround(input.positionWS, normalWS, baseColor);
                    half3 rockColor = half3(0.24, 0.23, 0.21) * (0.8 + 0.6 * dot(baseColor, half3(0.3, 0.4, 0.3)));
                    baseColor = lerp(baseColor * ground.albedo, rockColor * ground.albedo, ground.rock);
                    normalWS = ground.normalWS;
                }
                Light mainLight = GetMainLight();
                half wrapped = (dot(normalWS, mainLight.direction) + _LightWrap) / (1.0 + _LightWrap);
                half3 direct = mainLight.color * saturate(wrapped);
                half lightAttenuation = 1.0; // shadows on the direct light (PBR: applied to the full BRDF below)
                // Planet shadow beyond the terminator (only with the controller: _NightAmbient < 1).
                if (_NightAmbient < 1.0)
                {
                    lightAttenuation *= PlanetShadow(input.positionWS, _AtmoShell.xyz, mainLight.direction, -0.1);
                }
                // Cloud shadows dim the direct light only (colors unchanged).
                if (_CloudParams.w > 0.0)
                {
                    lightAttenuation *= 1.0 - CloudShadow(input.positionOS * _CloudLocalScale, normalize(TransformWorldToObjectDir(mainLight.direction)));
                }
                half3 ambient = SampleSH(normalWS) * _AmbientStrength
                    * DaylightAmbient(input.positionWS, _AtmoShell.xyz, mainLight.direction, _NightAmbient);
                // Skylight (V0.2.0D): on the day side the sunlit atmosphere lights the ground from the whole sky dome
                // (hemispherical: full on flat ground, half on a vertical wall), bluish. It fills the slopes turned away
                // from the sun the way the sky does, and fades out with the daylight: the night stays dark.
                if (_AtmoParams.w > 0.0)
                {
                    float3 upWS = normalize(input.positionWS - _AtmoShell.xyz);
                    half skyAmount = SkylightAmount(input.positionWS, _AtmoShell.xyz, mainLight.direction);
                    half sky = skyAmount * (0.5 + 0.5 * dot(normalWS, upWS));
                    // V0.2.0E: the sky dome's light is bluish, not the saturated limb color (steep shade and snow read
                    // blue-violet otherwise): half desaturated.
                    half3 skyColor = lerp(dot(_AtmoColor.rgb, half3(0.3, 0.59, 0.11)).xxx, _AtmoColor.rgb, 0.5);
                    ambient += skyColor * mainLight.color * (sky * _SkylightStrength * _AtmoParams.w);
                    // Ground bounce (V0.2.0E): the sunlit ground around a slope turned away from the sky (steep faces,
                    // gullies) lights it from below, tinted by the local ground. Follows the sun's height: zero at night.
                    half bounce = (0.5 - 0.5 * dot(normalWS, upWS)) * saturate(dot(upWS, mainLight.direction)) * skyAmount;
                    ambient += baseColor * mainLight.color * (bounce * _BounceStrength);
                }
                direct *= lightAttenuation;
                half3 color;
                if (pbr)
                {
                    float3 viewWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                    color = (PbrDirect(baseColor, smoothness, normalWS, viewWS, mainLight.direction, mainLight.color * lightAttenuation, _LightWrap)
                        + baseColor * ambient * occlusion) * _Brightness;
                }
                else
                {
                    color = baseColor * (direct + ambient) * _Brightness;
                }
                if (_AtmoParams.w > 0.0)
                {
                    AtmosphereInputs a;
                    a.center = _AtmoShell.xyz;
                    a.shellRadius = _AtmoShell.w;
                    a.oceanRadius = _AtmoParams.x;
                    a.density = _AtmoParams.y;
                    a.falloff = _AtmoParams.z;
                    a.surfaceCap = _AtmoParams2.z;
                    a.nightLevel = _AtmoParams2.x;
                    a.warmStrength = _AtmoParams2.y;
                    a.dayColor = _AtmoColor.rgb;
                    a.warmColor = _AtmoWarmColor.rgb;
                    a.lightDirection = mainLight.direction;
                    float3 toSurface = input.positionWS - _WorldSpaceCameraPos;
                    float distance = length(toSurface);
                    bool hitsOcean;
                    color += AtmosphereLight(a, _WorldSpaceCameraPos, toSurface / distance, distance, hitsOcean) * mainLight.color * _AtmoParams.w;
                }
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            Cull Back
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half DepthFrag(Varyings input) : SV_Target
            {
                return input.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            Varyings DepthNormalsVert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 DepthNormalsFrag(Varyings input) : SV_Target
            {
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
