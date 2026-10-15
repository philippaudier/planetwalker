// Planetwalker rivers: terrain-hugging ribbons lit like the terrain, with a minimum
// on-screen width (visible from orbit) and a distance-scaled lift plus depth offset
// (no z-fighting, no visible floating up close). No waves, foam or flow. URP only.
Shader "Planetwalker/PlanetRiver"
{
    Properties
    {
        _Color ("Water Color", Color) = (0.16, 0.36, 0.62, 1)
        _AmbientStrength ("Ambient Strength", Range(0, 2)) = 1
        _SpecularStrength ("Sheen", Range(0, 1)) = 0.25
        _MinPixelHalfWidth ("Min Half Width (px)", Range(0, 4)) = 0.75
        _MinLift ("Min Lift (world units)", Float) = 0.02
        _LiftPerDistance ("Lift per Camera Distance", Float) = 0.0004
        _OceanRadius ("Ocean Radius (object space)", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry+10"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Cull Off
            ZWrite On
            Offset -1, -2

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _AmbientStrength;
                half _SpecularStrength;
                float _MinPixelHalfWidth;
                float _MinLift;
                float _LiftPerDistance;
                float _OceanRadius;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 side : TEXCOORD0;      // xyz: side direction (object space), w: +1 / -1
                float2 width : TEXCOORD1;     // x: half width (object units), y: 1 under lake water
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
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 sideWS = normalize(TransformObjectToWorldDir(input.side.xyz));
                float scale = length(float3(UNITY_MATRIX_M[0].x, UNITY_MATRIX_M[1].x, UNITY_MATRIX_M[2].x));

                float distanceToCamera = distance(positionWS, _WorldSpaceCameraPos);
                // World size of one pixel at this distance (perspective).
                float pixelWorld = 2.0 * distanceToCamera / (UNITY_MATRIX_P[1][1] * _ScreenParams.y);
                float extra = max(0.0, _MinPixelHalfWidth * pixelWorld - input.width.x * scale);
                positionWS += sideWS * input.side.w * extra;
                // Parts under the ocean or under lake water are not lifted: a river mouth or a
                // lake inflow ends under the water surface instead of floating above it.
                bool underWater = length(input.positionOS.xyz) <= _OceanRadius || input.width.y > 0.5;
                float lift = underWater ? 0.0 : max(_MinLift, _LiftPerDistance * distanceToCamera);
                positionWS += normalWS * lift;

                output.positionWS = positionWS;
                output.normalWS = normalWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                float3 viewWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                Light mainLight = GetMainLight();
                half ndl = saturate(dot(normalWS, mainLight.direction));
                half3 ambient = SampleSH(normalWS) * _AmbientStrength;
                float3 halfDir = normalize(mainLight.direction + viewWS);
                half sheen = pow(saturate(dot(normalWS, halfDir)), 64.0) * _SpecularStrength * ndl;
                half3 color = _Color.rgb * (mainLight.color * ndl + ambient) + mainLight.color * sheen;
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
