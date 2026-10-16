// Planetwalker atmosphere shell (V0.1.2B): the sky part of the atmosphere (limb glow, sky seen from
// inside the shell). See PlanetAtmosphereCommon.hlsl for the model. The haze between the camera and
// the terrain / ocean is added by the terrain and ocean shaders themselves (same function, same
// parameters, copied from this material by PlanetVisualController), so relief rising into the
// atmosphere is hazed by the air in front of it and a mountain on the limb is covered by the glow at
// its base, clear at its top.
//
// Back faces (Cull Front), ZTest LEqual, additive (Blend One One), no depth write, no depth texture:
// only rays that miss the ocean sphere are drawn here, and any relief in front of the shell's back
// face hides them (it gets its own haze instead). Works with the camera inside or outside the shell.
Shader "Planetwalker/PlanetAtmosphere"
{
    Properties
    {
        _AtmosphereColor ("Day Color", Color) = (0.30, 0.55, 1.0, 1)
        _WarmColor ("Terminator Tint", Color) = (1.0, 0.55, 0.30, 1)
        _Strength ("Strength", Range(0, 2)) = 1
        _Density ("Density", Range(0, 1)) = 0.03
        _Falloff ("Height Falloff", Range(0, 8)) = 3
        _PlanetRatio ("Ocean Radius / Shell Radius", Range(0.5, 1)) = 0.975
        _NightLevel ("Night Rim Level", Range(0, 0.2)) = 0.02
        _WarmStrength ("Terminator Tint Strength", Range(0, 1)) = 0.3
        _SurfaceOpticalDepth ("Surface Haze Soft Cap (terrain / ocean)", Range(0.5, 20)) = 5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+100"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Sky"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Front

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "PlanetAtmosphereCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _AtmosphereColor;
                half4 _WarmColor;
                half _Strength;
                half _Density;
                half _Falloff;
                float _PlanetRatio;
                half _NightLevel;
                half _WarmStrength;
                half _SurfaceOpticalDepth;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionOS = input.positionOS.xyz;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Object space: shell = unit sphere, ocean sphere = _PlanetRatio.
                Light mainLight = GetMainLight();
                AtmosphereInputs a;
                a.center = 0;
                a.shellRadius = 1.0;
                a.oceanRadius = _PlanetRatio;
                a.density = _Density;
                a.falloff = _Falloff;
                a.surfaceCap = _SurfaceOpticalDepth;
                a.nightLevel = _NightLevel;
                a.warmStrength = _WarmStrength;
                a.dayColor = _AtmosphereColor.rgb;
                a.warmColor = _WarmColor.rgb;
                a.lightDirection = normalize(TransformWorldToObjectDir(mainLight.direction));
                float3 origin = TransformWorldToObject(_WorldSpaceCameraPos);
                float3 ray = normalize(input.positionOS - origin);
                bool hitsOcean;
                half3 light = AtmosphereLight(a, origin, ray, 0.0, hitsOcean);
                if (hitsOcean)
                {
                    return 0; // the ocean / terrain shaders add their own haze
                }
                return half4(light * mainLight.color * _Strength, 0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
