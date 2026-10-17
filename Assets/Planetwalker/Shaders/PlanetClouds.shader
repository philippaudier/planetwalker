// Planetwalker clouds (V0.1.2C): cloud shell slightly above the ocean, drawn with Graphics.RenderMesh
// (no GameObject). Cloud data and local detail: PlanetCloudsCommon.hlsl (shared with the cloud shadows).
// Both faces (V0.2.0D: also seen from below), ZTest Always with analytic planet occlusion, no depth write: the cloud layer is above everything on the planet (the
// terrain heights are exaggerated, so relief would otherwise cut the clouds along its silhouettes); the
// far side of the planet is excluded by back-face culling (no z-fighting, no depth interaction),
// alpha blending, queue Transparent+50: after the ocean and before the atmosphere sky pass, whose
// additive limb glow stays on top near the horizon.
// Lighting: main light with the same wrap as terrain / ocean (terminator softness), cool ambient on the
// shadow side, darker towards the night, restrained warm tint near the terminator, self-shadowing
// towards the sun (relief), thicker look at grazing angles, atmospheric haze in front (same model as
// the terrain).
Shader "Planetwalker/PlanetClouds"
{
    Properties
    {
        [NoScaleOffset] _CloudCube ("Cloud Field", Cube) = "black" {}
        _CloudParams ("Threshold, density, detail, enabled", Vector) = (0, 0.75, 0.16, 0)
        // Depth test (V0.2.0D): Always from orbit (the exaggerated relief must not cut the layer along its silhouettes),
        // LessEqual near the ground (the camera within the relief or under the layer: relief hides what is behind it).
        [Enum(UnityEngine.Rendering.CompareFunction)] _CloudZTest ("Depth Test", Float) = 8
        _CloudParams2 ("Weather, convection, shadow, shell radius", Vector) = (0.5, 0.5, 0.25, 1)
        _CloudParams3 ("Axis (xyz), time", Vector) = (0, 1, 0, 0)
        _CloudSeed ("Noise offset (xyz), softness", Vector) = (0, 0, 0, 0.12)
        _CloudColor ("Lit Cloud Color", Color) = (0.96, 0.96, 0.95, 1)
        _CloudShadeColor ("Shaded Cloud Tint", Color) = (0.72, 0.78, 0.88, 1)
        _CloudWarmColor ("Terminator Tint", Color) = (1.0, 0.82, 0.66, 1)
        _LightWrap ("Light Wrap (terminator)", Range(0, 0.5)) = 0.08
        _NightAmbient ("Ambient kept on the night side", Range(0, 1)) = 1
        _TwilightWarmth ("Twilight Warmth (0.5 = default tint)", Range(0, 1)) = 0.5
        // Close-range detail (V0.2.0D): octaves finer than the orbital detail, shown only where a pixel is small.
        _CloudFineStrength ("Close-Range Detail", Range(0, 1)) = 1
        [HideInInspector] _AtmoShell ("Atmosphere center (xyz) / shell radius (w), world", Vector) = (0, 0, 0, 1)
        [HideInInspector] _AtmoParams ("Ocean radius, density, falloff, strength", Vector) = (0.975, 0.03, 3, 0)
        [HideInInspector] _AtmoParams2 ("Night level, warm strength, surface cap", Vector) = (0.02, 0.3, 5, 0)
        [HideInInspector] _AtmoColor ("Atmosphere day color", Color) = (0.30, 0.55, 1.0, 1)
        [HideInInspector] _AtmoWarmColor ("Atmosphere warm color", Color) = (1.0, 0.55, 0.30, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+50"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Clouds"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest [_CloudZTest]
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _CloudParams;
                float4 _CloudParams2;
                float4 _CloudParams3;
                float4 _CloudSeed;
                half4 _CloudColor;
                half4 _CloudShadeColor;
                half4 _CloudWarmColor;
                half _LightWrap;
                half _NightAmbient;
                half _TwilightWarmth;
                half _CloudFineStrength;
                float4 _AtmoShell;
                float4 _AtmoParams;
                float4 _AtmoParams2;
                half4 _AtmoColor;
                half4 _AtmoWarmColor;
            CBUFFER_END

            #include "PlanetCloudsCommon.hlsl"
            #include "PlanetAtmosphereCommon.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionOS = input.positionOS.xyz;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                if (_CloudParams.w <= 0.0)
                {
                    return 0;
                }
                float3 d = normalize(input.positionOS);
                // Pixel footprint on the unit sphere (before any non-uniform branch: derivatives).
                float footprint = max(length(ddx(d)), length(ddy(d)));
                Light mainLight = GetMainLight();
                float3 lightOS = normalize(TransformWorldToObjectDir(mainLight.direction));
                float convective, grain;
                float potential = CloudPotential(d, 4, convective, grain);
                // Close-range detail (V0.2.0D): broken, puffy edges where the cloud is thin, relief lit from the sun inside
                // the masses. Faded by the footprint: the orbital look is unchanged.
                float3 sunTangent = lightOS - d * dot(lightOS, d);
                sunTangent = dot(sunTangent, sunTangent) > 1e-8 ? normalize(sunTangent) : float3(0, 0, 0);
                float fineTowardSun, fineCoverage;
                float fine = CloudFine(d, sunTangent, footprint, fineTowardSun, fineCoverage) * _CloudFineStrength;
                fineTowardSun *= _CloudFineStrength;
                fineCoverage *= _CloudFineStrength;
                potential += fine * _CloudSeed.w * 0.6;
                half alpha = saturate((potential - _CloudParams.x) / _CloudSeed.w + 0.5) * _CloudParams.y * grain;
                // Thin wisps get thinner and denser lobes a bit thicker: the fine detail also shapes the opacity inside.
                alpha *= saturate(1.0 + fine * 0.2);
                if (alpha <= 0.002)
                {
                    return 0;
                }
                float3 normalWS = normalize(TransformObjectToWorldNormal(d));
                float3 viewWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                // Planet occlusion (V0.2.0D), analytic: the layer is drawn on both faces without depth test (seen from
                // above or from below), so a fragment whose view ray meets the ocean sphere first is behind the planet
                // (from under the layer, the far side's clouds would otherwise show through the ground).
                {
                    float3 toFragment = input.positionWS - _WorldSpaceCameraPos;
                    float fragmentDistance = length(toFragment);
                    float3 ray = toFragment / max(fragmentDistance, 1e-6);
                    float3 oc = _WorldSpaceCameraPos - _AtmoShell.xyz;
                    float b = dot(oc, ray);
                    float disc = b * b - (dot(oc, oc) - _AtmoParams.x * _AtmoParams.x);
                    if (disc > 0.0)
                    {
                        float tp = -b - sqrt(disc);
                        if (tp > 0.0 && tp < fragmentDistance)
                        {
                            discard;
                        }
                    }
                }
                // Thicker at grazing angles (longer path through the layer).
                half facing = dot(normalWS, viewWS);
                half mu = abs(facing);
                alpha = 1.0 - pow(saturate(1.0 - alpha), 1.0 / max(mu, 0.3));
                // Seen from below up close, the dense lobes are opaque bodies (the base of the layer), not a veil.
                if (facing < 0.0)
                {
                    alpha = lerp(alpha, saturate(alpha * (1.25 + 0.5 * saturate(fine))), fineCoverage);
                }

                half nl = dot(normalWS, mainLight.direction);
                half lit = saturate((nl + _LightWrap) / (1.0 + _LightWrap));
                // Planet shadow: clouds sit higher, so they keep the sunset light a little longer.
                if (_NightAmbient < 1.0)
                {
                    lit *= PlanetShadow(input.positionWS, _AtmoShell.xyz, mainLight.direction, -0.14);
                }
                // Self-shadowing: denser cloud towards the sun shades this point (relief, sharper at low sun).
                float3 towardSun = normalize(d + (lightOS - d * dot(lightOS, d)) * 0.012);
                float convectiveSun;
                half selfShadow = saturate((CloudPotential(towardSun, 2, convectiveSun) - potential + fine * _CloudSeed.w * 0.6) * 2.5) * 0.4;
                // Fine relief: a lobe whose density grows towards the sun is in its own shade, its sun side is bright.
                half fineShade = saturate(0.5 + (fine - fineTowardSun) * 2.2);
                lit *= lerp(1.0, 0.45 + 0.8 * fineShade, fineCoverage);
                // Warm light near the terminator (sunlit side only), scaled by the twilight warmth; subtle, never uniform.
                half warm = (1.0 - smoothstep(0.0, 0.35, nl)) * smoothstep(-0.12, 0.05, nl) * 0.9 * _TwilightWarmth;
                half3 litColor = lerp(_CloudColor.rgb, _CloudColor.rgb * _CloudWarmColor.rgb, warm);
                half body = 0.86 + 0.14 * saturate(alpha / max(_CloudParams.y, 0.05));
                body *= lerp(1.0, 0.9 + 0.2 * saturate(0.5 + fine), fineCoverage); // denser lobes a touch brighter
                half3 ambient = SampleSH(normalWS) * _CloudShadeColor.rgb
                    * DaylightAmbient(input.positionWS, _AtmoShell.xyz, mainLight.direction, _NightAmbient); // no self-lit night clouds
                half3 color = litColor * mainLight.color * (lit * (1.0 - selfShadow) * body) + ambient * _CloudColor.rgb;
                // Seen from below (inner face): the thick base of the layer, darker than its sunlit top.
                color *= facing < 0.0 ? 0.72 : 1.0;

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
                    float3 toCloud = input.positionWS - _WorldSpaceCameraPos;
                    float distance = length(toCloud);
                    bool hitsOcean;
                    half3 haze = AtmosphereLight(a, _WorldSpaceCameraPos, toCloud / distance, distance, hitsOcean) * mainLight.color * _AtmoParams.w;
                    // Camera inside the atmosphere (V0.2.0E): (1 - height in the layer)^2, as the sky from inside; zero from
                    // orbit, so the orbital look is unchanged.
                    float cameraHeight = saturate((length(_WorldSpaceCameraPos - _AtmoShell.xyz) - _AtmoParams.x) / max(_AtmoShell.w - _AtmoParams.x, 1e-4));
                    half inside = (1.0 - cameraHeight) * (1.0 - cameraHeight);
                    if (hitsOcean)
                    {
                        // In front of the cloud (rays missing the ocean get the shell's limb glow instead).
                        color += haze / max(alpha, 0.05);
                    }
                    else if (inside > 0.0)
                    {
                        // Horizon band fix (V0.2.0E): seen from inside the atmosphere, a distant part of the layer whose ray
                        // misses the ocean (the layer's own horizon, seen from a summit above it) got no aerial perspective
                        // and, being an infinitely thin shell seen edge-on, saturated to opaque white: a hard white strip
                        // against the blue sky. It now takes the haze in front of it, loses contrast with the path (the
                        // haze's share of the sky color stands for the extinction), and its limb fades out gradually.
                        half veil = saturate(dot(haze, half3(0.333, 0.333, 0.333)) / max(dot(_AtmoColor.rgb * mainLight.color, half3(0.333, 0.333, 0.333)) * _AtmoParams.w, 1e-3));
                        color = color * (1.0 - 0.7 * veil * inside) + haze * inside / max(alpha, 0.05);
                        if (facing > 0.0)
                        {
                            alpha *= lerp(1.0, smoothstep(0.0, 0.15, mu), inside);
                        }
                    }
                    // Sea of clouds seen from a summit above the deck (V0.2.0E horizon band): the deck's far part is a
                    // grazing view of an infinitely thin shell hundreds of kilometers away. Its ray ends on the ocean, so
                    // the haze above barely touched it (a short path in thin, high air) and it read as a hard white
                    // strip against the sky. Inside the atmosphere shell (never from orbit: the orbital look is
                    // unchanged), the grazing deck now fades towards the horizon sky's color and loses opacity.
                    if (cameraHeight < 0.999 && facing > 0.0)
                    {
                        half grazing = 1.0 - smoothstep(0.0, 0.22, mu);
                        float3 upAtCloud = normalize(input.positionWS - _AtmoShell.xyz);
                        half day = smoothstep(-0.12, 0.3, dot(upAtCloud, mainLight.direction));
                        half3 horizonSky = lerp(_AtmoColor.rgb, half3(1.0, 1.0, 1.0), 0.45) * 0.62 * mainLight.color * day * _AtmoParams.w;
                        color = lerp(color, horizonSky, 0.65 * grazing);
                        alpha *= lerp(1.0, 0.25 + 0.75 * smoothstep(0.0, 0.12, mu), grazing);
                    }
                }
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
