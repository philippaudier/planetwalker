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
        _NightLevel ("Night Rim Level", Range(0, 0.2)) = 0.003
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

            // Sky seen from inside the atmosphere (V0.2.0D). The limb model above integrates a thin, faint layer: right for
            // the planet seen from space, but from the ground it leaves the daytime zenith almost black (~3%). This adds the
            // sky dome a camera inside the layer sees, weighted by how deep it is in the layer (zero above it: the orbital
            // look is unchanged): blue overhead, paler towards the horizon, a soft glow around the sun, lit by the sun's
            // height at the camera (fades through the twilight, black at night). An approximation, not a scattering model.
            half3 SkyFromInside(float3 origin, float3 ray, float3 lightDirection, float oceanRadius)
            {
                float thickness = max(1e-4, 1.0 - oceanRadius);
                float height = saturate((length(origin) - oceanRadius) / thickness);
                half inside = (1.0 - height) * (1.0 - height);
                if (inside <= 0.0)
                {
                    return 0;
                }
                float3 up = normalize(origin);
                float elevation = saturate(dot(ray, up));
                half horizon = 1.0 - elevation;
                half sunHeight = dot(up, lightDirection);
                half day = smoothstep(-0.12, 0.3, sunHeight);
                half3 zenith = _AtmosphereColor.rgb * 0.55;
                half3 rim = lerp(_AtmosphereColor.rgb, half3(1.0, 1.0, 1.0), 0.45) * 0.62;
                half3 sky = lerp(zenith, rim, horizon * horizon * horizon);
                // Reddened near the horizon when the sun is low (the twilight tint, restrained).
                half low = (1.0 - smoothstep(0.0, 0.35, sunHeight)) * horizon * horizon * _WarmStrength;
                sky = lerp(sky, _WarmColor.rgb * 0.55, saturate(low));
                half glow = pow(saturate(dot(ray, lightDirection)), 12.0) * 0.6;
                return (sky + glow * lerp(half3(1.0, 1.0, 1.0), _WarmColor.rgb, low)) * (day * inside);
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
                light += SkyFromInside(origin, ray, a.lightDirection, a.oceanRadius);
                return half4(light * mainLight.color * _Strength, 0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
