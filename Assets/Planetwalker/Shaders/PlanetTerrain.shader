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
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                half4 color : COLOR;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionOS = input.positionOS.xyz;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                Light mainLight = GetMainLight();
                half wrapped = (dot(normalWS, mainLight.direction) + _LightWrap) / (1.0 + _LightWrap);
                half3 direct = mainLight.color * saturate(wrapped);
                // Planet shadow beyond the terminator (only with the controller: _NightAmbient < 1).
                if (_NightAmbient < 1.0)
                {
                    direct *= PlanetShadow(input.positionWS, _AtmoShell.xyz, mainLight.direction, -0.1);
                }
                // Cloud shadows dim the direct light only (colors unchanged).
                if (_CloudParams.w > 0.0)
                {
                    direct *= 1.0 - CloudShadow(input.positionOS * _CloudLocalScale, normalize(TransformWorldToObjectDir(mainLight.direction)));
                }
                half3 ambient = SampleSH(normalWS) * _AmbientStrength
                    * DaylightAmbient(input.positionWS, _AtmoShell.xyz, mainLight.direction, _NightAmbient);
                half3 color = input.color.rgb * (direct + ambient) * _Brightness;
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
