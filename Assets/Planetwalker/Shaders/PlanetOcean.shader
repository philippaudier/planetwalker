// Planetwalker ocean: semi-transparent sea-level sphere. URP only.
// _OceanShading = 0: the original flat look (Fresnel rim, Blinn glint), unchanged.
// _OceanShading = 1 (V0.1.2B): bathymetry-aware color from per-vertex normalized depths (TEXCOORD1:
// x = local depth, y = broad shelf depth, both / the planet's basin depth scale; written by
// OceanDepthLayer, continuous over the sphere), deep navy to lighter shallow blue, restrained turquoise
// on broad shelves only, a roughness-controlled GGX sun highlight (clamped: no white blobs), Fresnel sky
// reflection, and the same main-light wrap as the terrain (terminator softness). No waves.
Shader "Planetwalker/PlanetOcean"
{
    Properties
    {
        _Color ("Water Color (flat mode)", Color) = (0.02, 0.09, 0.24, 1)
        _RimColor ("Rim / Sky Reflection Color", Color) = (0.20, 0.38, 0.60, 1)
        _Opacity ("Opacity (facing)", Range(0, 1)) = 0.74
        _FresnelPower ("Fresnel Power", Range(1, 10)) = 4
        _SpecularStrength ("Glint Strength (flat mode)", Range(0, 2)) = 0.35
        _SpecularPower ("Glint Sharpness (flat mode)", Range(16, 2048)) = 600
        _AmbientStrength ("Ambient Strength", Range(0, 2)) = 1

        [Header(Bathymetry shading V0.1.2B)]
        [Toggle] _OceanShading ("Bathymetry Shading", Float) = 0
        _DeepColor ("Deep Water", Color) = (0.008, 0.035, 0.11, 1)
        _ShallowColor ("Shallow Water", Color) = (0.05, 0.16, 0.32, 1)
        _ShelfColor ("Shelf Turquoise", Color) = (0.07, 0.30, 0.36, 1)
        _DepthContrast ("Depth Contrast", Range(0, 1)) = 0.5
        _ShelfVisibility ("Shelf Visibility", Range(0, 1)) = 0.5
        _Roughness ("Roughness", Range(0.08, 0.8)) = 0.35
        _SpecularIntensity ("Specular Intensity", Range(0, 1)) = 0.35
        _LightWrap ("Light Wrap (terminator)", Range(0, 0.5)) = 0.08

        // Atmospheric haze between the camera and the water (V0.1.2B), set per renderer by
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
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "PlanetAtmosphereCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _RimColor;
                half _Opacity;
                half _FresnelPower;
                half _SpecularStrength;
                half _SpecularPower;
                half _AmbientStrength;
                half _OceanShading;
                half4 _DeepColor;
                half4 _ShallowColor;
                half4 _ShelfColor;
                half _DepthContrast;
                half _ShelfVisibility;
                half _Roughness;
                half _SpecularIntensity;
                half _LightWrap;
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
            CBUFFER_END

            #include "PlanetCloudsCommon.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 depth : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 depth : TEXCOORD2;
                float3 positionOS : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.depth = input.depth;
                output.positionOS = input.positionOS.xyz;
                return output;
            }

            half4 FlatOcean(float3 normalWS, float3 viewWS, Light mainLight)
            {
                half ndl = saturate(dot(normalWS, mainLight.direction));
                half fresnel = pow(1.0 - saturate(dot(normalWS, viewWS)), _FresnelPower);
                float3 halfDir = normalize(mainLight.direction + viewWS);
                half glint = pow(saturate(dot(normalWS, halfDir)), _SpecularPower) * _SpecularStrength * ndl;

                half3 ambient = SampleSH(normalWS) * _AmbientStrength;
                half3 water = lerp(_Color.rgb, _RimColor.rgb, fresnel);
                half3 color = water * (mainLight.color * ndl + ambient) + mainLight.color * glint;
                half alpha = saturate(lerp(_Opacity, 1.0, fresnel) + glint);
                return half4(color, alpha);
            }

            half4 BathymetryOcean(float3 normalWS, float3 viewWS, Light mainLight, float2 depth)
            {
                // Depth color: smooth curve of the normalized depth (no thresholds: no contour lines).
                float deep = saturate(depth.x / 1.2);
                deep = deep * (2.0 - deep);
                half3 water = lerp(_DeepColor.rgb, lerp(_ShallowColor.rgb, _DeepColor.rgb, deep), _DepthContrast);
                // Shelves: broad shallow areas only (shelf depth small) and only where the water is
                // locally shallow too. Smooth ramps; the tint stays below the shelf color.
                half shelf = (1.0 - smoothstep(0.1, 0.45, depth.y)) * (1.0 - smoothstep(0.15, 0.55, depth.x));
                shelf *= _ShelfVisibility;
                water = lerp(water, _ShelfColor.rgb, shelf * 0.85);

                half nl = dot(normalWS, mainLight.direction);
                half wrapped = saturate((nl + _LightWrap) / (1.0 + _LightWrap));
                half nv = saturate(dot(normalWS, viewWS));
                half fresnel = pow(1.0 - nv, _FresnelPower);
                half3 ambient = SampleSH(normalWS) * _AmbientStrength;
                half3 lit = lerp(water, _RimColor.rgb, fresnel * 0.6) * (mainLight.color * wrapped + ambient);

                // GGX highlight (URP's minimal BRDF form), Schlick Fresnel from water's F0 = 0.02. Clamped
                // so a smooth ocean never produces a white blob; roughness widens and dims it.
                float3 halfDir = SafeNormalize(mainLight.direction + viewWS);
                float nh = saturate(dot(normalWS, halfDir));
                float lh = saturate(dot(mainLight.direction, halfDir));
                float a = _Roughness * _Roughness;
                float a2 = a * a;
                float d = nh * nh * (a2 - 1.0) + 1.0;
                float spec = a2 / (d * d * max(0.1, lh * lh) * (_Roughness * 4.0 + 2.0));
                float f = 0.02 + 0.98 * pow(1.0 - lh, 5.0);
                // Soft saturation y / (1 + y) (no clamped plateau, so no flat white disc), at most 0.6 x intensity:
                // low roughness = small bright point, high roughness = broad faint sheen.
                float y = spec * f * 4.0;
                half highlight = (y / (1.0 + y)) * 0.6 * _SpecularIntensity * saturate(nl);
                half3 color = lit + mainLight.color * highlight;
                // The first coastal strip is a little more opaque: the terrain palette just below sea level is a
                // pale beach / shallow tone that would otherwise show as a bright contour through the water.
                half coastal = 1.0 - smoothstep(0.0, 0.12, depth.x);
                half opacity = lerp(_Opacity, max(_Opacity, 0.9), coastal);
                half alpha = saturate(lerp(opacity, 1.0, fresnel) + highlight * 0.5);
                return half4(color, alpha);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                float3 viewWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                Light mainLight = GetMainLight();
                // Cloud shadows: dim the main light for the water (colors unchanged).
                if (_CloudParams.w > 0.0)
                {
                    mainLight.color *= 1.0 - CloudShadow(input.positionOS * _CloudLocalScale, normalize(TransformWorldToObjectDir(mainLight.direction)));
                }
                half4 color = _OceanShading > 0.5
                    ? BathymetryOcean(normalWS, viewWS, mainLight, input.depth)
                    : FlatOcean(normalWS, viewWS, mainLight);
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
                    // Divided by alpha: the haze lies in front of the water, not under it.
                    color.rgb += AtmosphereLight(a, _WorldSpaceCameraPos, toSurface / distance, distance, hitsOcean)
                        * mainLight.color * _AtmoParams.w / max(color.a, 0.05);
                }
                return color;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
