// Planetwalker lakes: flat water surfaces with the ocean's visual language (Fresnel rim, sun
// glint), a little darker and greener, more opaque where deeper. Drawn before the ocean and
// writing depth. A small, distance-scaled
// radial lift plus a depth offset avoids z-fighting with the shore while staying sub-pixel.
// No waves, foam or underwater rendering. URP only.
Shader "Planetwalker/PlanetLake"
{
    Properties
    {
        _Color ("Deep Water Color", Color) = (0.04, 0.17, 0.27, 1)
        _ShallowColor ("Shallow Water Color", Color) = (0.12, 0.33, 0.38, 1)
        _RimColor ("Rim Color", Color) = (0.25, 0.42, 0.55, 1)
        _ShallowOpacity ("Opacity (shallow)", Range(0, 1)) = 0.6
        _DeepOpacity ("Opacity (deep)", Range(0, 1)) = 0.88
        _FresnelPower ("Fresnel Power", Range(1, 10)) = 4
        _SpecularStrength ("Glint Strength", Range(0, 2)) = 0.3
        _SpecularPower ("Glint Sharpness", Range(16, 2048)) = 500
        _AmbientStrength ("Ambient Strength", Range(0, 2)) = 1
        _MinLift ("Min Lift (world units)", Float) = 0.005
        _LiftPerDistance ("Lift per Camera Distance", Float) = 0.0002
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent-10"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Blend SrcAlpha OneMinusSrcAlpha
            // A lake surface never overlaps itself; writing depth keeps the (later) ocean sphere
            // from showing through where the terrain under a lake dips below sea level.
            ZWrite On
            Cull Back
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _ShallowColor;
                half4 _RimColor;
                half _ShallowOpacity;
                half _DeepOpacity;
                half _FresnelPower;
                half _SpecularStrength;
                half _SpecularPower;
                half _AmbientStrength;
                float _MinLift;
                float _LiftPerDistance;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 water : TEXCOORD0;   // x: relative depth (0 shore .. 1 deepest), y: 1 on the shore
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float depth : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float distanceToCamera = distance(positionWS, _WorldSpaceCameraPos);
                positionWS += normalWS * max(_MinLift, _LiftPerDistance * distanceToCamera);
                output.positionWS = positionWS;
                output.normalWS = normalWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.depth = input.water.x;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                float3 viewWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                Light mainLight = GetMainLight();

                half depth = saturate(input.depth * 2.0);
                half ndl = saturate(dot(normalWS, mainLight.direction));
                half fresnel = pow(1.0 - saturate(dot(normalWS, viewWS)), _FresnelPower);
                float3 halfDir = normalize(mainLight.direction + viewWS);
                half glint = pow(saturate(dot(normalWS, halfDir)), _SpecularPower) * _SpecularStrength * ndl;

                half3 ambient = SampleSH(normalWS) * _AmbientStrength;
                half3 body = lerp(_ShallowColor.rgb, _Color.rgb, depth);
                half3 water = lerp(body, _RimColor.rgb, fresnel);
                half3 color = water * (mainLight.color * ndl + ambient) + mainLight.color * glint;
                half alpha = saturate(lerp(lerp(_ShallowOpacity, _DeepOpacity, depth), 1.0, fresnel) + glint);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
