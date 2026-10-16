// Planetwalker rivers: terrain-hugging ribbons lit like the terrain, with a minimum
// on-screen width (visible from orbit) and a distance-scaled lift plus depth offset
// (no z-fighting, no visible floating up close). No waves, foam or flow. URP only.
// Drawn after the lakes (which write depth) and before the ocean: ribbon parts under a lake
// surface fail the depth test, so the river ends exactly along the rendered shoreline.
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
        _MouthColor ("Lake Mouth Color", Color) = (0.1, 0.27, 0.34, 1)
        _MouthBlend ("Lake Mouth Blend", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent-5"
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
                half4 _MouthColor;
                half _MouthBlend;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 side : TEXCOORD0;      // xyz: side direction (object space), w: +1 / -1
                float3 width : TEXCOORD1;     // x: half width (object units), y: lake water radius (object units) or 0, z: lake mouth blend
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float mouth : TEXCOORD2;
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
                // Parts under the ocean are not lifted: a river mouth ends under the water surface
                // instead of floating above it. Near a lake the lift never raises a vertex above the
                // lake's water radius, so the ribbon dives under the lake exactly where the terrain does.
                float radiusOS = length(input.positionOS.xyz);
                float lift = radiusOS <= _OceanRadius ? 0.0 : max(_MinLift, _LiftPerDistance * distanceToCamera);
                if (input.width.y > 0.0)
                {
                    lift = min(lift, max(0.0, (radiusOS - input.width.y) * scale));
                }
                positionWS += normalWS * lift;

                output.positionWS = positionWS;
                output.normalWS = normalWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.mouth = input.width.z;
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
                // Near a lake mouth the river tint drifts towards the lake's shallow water color so the
                // shoreline between the two materials does not draw attention.
                half3 tint = lerp(_Color.rgb, _MouthColor.rgb, saturate(input.mouth) * _MouthBlend);
                half3 color = tint * (mainLight.color * ndl + ambient) + mainLight.color * sheen;
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
