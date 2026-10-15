// Planetwalker ocean: simple semi-transparent sea-level sphere with a Fresnel rim and
// a sun glint. No waves, refraction or depth effects. URP only.
Shader "Planetwalker/PlanetOcean"
{
    Properties
    {
        _Color ("Water Color", Color) = (0.02, 0.09, 0.24, 1)
        _RimColor ("Rim Color", Color) = (0.20, 0.38, 0.60, 1)
        _Opacity ("Opacity (facing)", Range(0, 1)) = 0.74
        _FresnelPower ("Fresnel Power", Range(1, 10)) = 4
        _SpecularStrength ("Glint Strength", Range(0, 2)) = 0.35
        _SpecularPower ("Glint Sharpness", Range(16, 2048)) = 600
        _AmbientStrength ("Ambient Strength", Range(0, 2)) = 1
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

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _RimColor;
                half _Opacity;
                half _FresnelPower;
                half _SpecularStrength;
                half _SpecularPower;
                half _AmbientStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                float3 viewWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                Light mainLight = GetMainLight();

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
            ENDHLSL
        }
    }

    FallBack Off
}
