// Planetwalker clouds (V0.1.2C): cloud shell slightly above the ocean, drawn with Graphics.RenderMesh
// (no GameObject). Cloud data and local detail: PlanetCloudsCommon.hlsl (shared with the cloud shadows).
// Front faces only, ZTest Always, no depth write: the cloud layer is above everything on the planet (the
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
        _CloudParams2 ("Weather, convection, shadow, shell radius", Vector) = (0.5, 0.5, 0.25, 1)
        _CloudParams3 ("Axis (xyz), time", Vector) = (0, 1, 0, 0)
        _CloudSeed ("Noise offset (xyz), softness", Vector) = (0, 0, 0, 0.12)
        _CloudColor ("Lit Cloud Color", Color) = (0.96, 0.96, 0.95, 1)
        _CloudShadeColor ("Shaded Cloud Tint", Color) = (0.72, 0.78, 0.88, 1)
        _CloudWarmColor ("Terminator Tint", Color) = (1.0, 0.82, 0.66, 1)
        _LightWrap ("Light Wrap (terminator)", Range(0, 0.5)) = 0.08
        _NightAmbient ("Ambient kept on the night side", Range(0, 1)) = 1
        _TwilightWarmth ("Twilight Warmth (0.5 = default tint)", Range(0, 1)) = 0.5
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
            ZTest Always
            Cull Back

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
                float convective, grain;
                float potential = CloudPotential(d, 4, convective, grain);
                half alpha = saturate((potential - _CloudParams.x) / _CloudSeed.w + 0.5) * _CloudParams.y * grain;
                if (alpha <= 0.002)
                {
                    return 0;
                }
                float3 normalWS = normalize(TransformObjectToWorldNormal(d));
                float3 viewWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                // Thicker at grazing angles (longer path through the layer).
                half mu = saturate(dot(normalWS, viewWS));
                alpha = 1.0 - pow(saturate(1.0 - alpha), 1.0 / max(mu, 0.3));

                Light mainLight = GetMainLight();
                half nl = dot(normalWS, mainLight.direction);
                half lit = saturate((nl + _LightWrap) / (1.0 + _LightWrap));
                // Planet shadow: clouds sit higher, so they keep the sunset light a little longer.
                if (_NightAmbient < 1.0)
                {
                    lit *= PlanetShadow(input.positionWS, _AtmoShell.xyz, mainLight.direction, -0.14);
                }
                // Self-shadowing: denser cloud towards the sun shades this point (relief, sharper at low sun).
                float3 lightOS = normalize(TransformWorldToObjectDir(mainLight.direction));
                float3 towardSun = normalize(d + (lightOS - d * dot(lightOS, d)) * 0.012);
                float convectiveSun;
                half selfShadow = saturate((CloudPotential(towardSun, 2, convectiveSun) - potential) * 2.5) * 0.4;
                // Warm light near the terminator (sunlit side only), scaled by the twilight warmth; subtle, never uniform.
                half warm = (1.0 - smoothstep(0.0, 0.35, nl)) * smoothstep(-0.12, 0.05, nl) * 0.9 * _TwilightWarmth;
                half3 litColor = lerp(_CloudColor.rgb, _CloudColor.rgb * _CloudWarmColor.rgb, warm);
                half body = 0.86 + 0.14 * saturate(alpha / max(_CloudParams.y, 0.05));
                half3 ambient = SampleSH(normalWS) * _CloudShadeColor.rgb
                    * DaylightAmbient(input.positionWS, _AtmoShell.xyz, mainLight.direction, _NightAmbient); // no self-lit night clouds
                half3 color = litColor * mainLight.color * (lit * (1.0 - selfShadow) * body) + ambient * _CloudColor.rgb;

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
                    half3 haze = AtmosphereLight(a, _WorldSpaceCameraPos, toCloud / distance, distance, hitsOcean);
                    if (hitsOcean)
                    {
                        // In front of the cloud (rays missing the ocean get the shell's limb glow instead).
                        color += haze * mainLight.color * _AtmoParams.w / max(alpha, 0.05);
                    }
                }
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
