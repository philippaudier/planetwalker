// Test-only probe (V0.1.2C tests): evaluates the cloud HLSL (PlanetCloudsCommon.hlsl) on the GPU at the
// directions of an equirectangular grid (u = longitude, v = latitude), so the tests can compare the GPU
// with the CPU cloud field (cube map layout, seams, poles, motion).
// Pass 0: raw cube map texel (R base, G systems, B convective). Pass 1: R = cloud potential, G = alpha.
Shader "Hidden/Planetwalker/CloudProbe"
{
    Properties
    {
        _CloudCube ("Cloud Field", Cube) = "black" {}
    }
    SubShader
    {
        CGINCLUDE
        #include "UnityCG.cginc"
        #define TEXTURECUBE(name) samplerCUBE name
        #define SAMPLER(name) static const int name##_unused = 0
        #define SAMPLE_TEXTURECUBE_LOD(tex, smp, dir, lod) texCUBElod(tex, float4(dir, lod))
        float4 _CloudParams;
        float4 _CloudParams2;
        float4 _CloudParams3;
        float4 _CloudSeed;
        #include "../../Shaders/PlanetCloudsCommon.hlsl"

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        v2f Vert(appdata_img v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord;
            return o;
        }

        float3 Direction(float2 uv)
        {
            float lon = (uv.x * 2.0 - 1.0) * 3.14159265, lat = (uv.y - 0.5) * 3.14159265;
            return float3(cos(lat) * cos(lon), sin(lat), cos(lat) * sin(lon));
        }
        ENDCG

        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(v2f i) : SV_Target
            {
                return texCUBElod(_CloudCube, float4(Direction(i.uv), 0));
            }
            ENDCG
        }

        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(v2f i) : SV_Target
            {
                float convective;
                float3 d = Direction(i.uv);
                float potential = CloudPotential(d, 4, convective);
                return float4(potential, CloudAlpha(d, 4), convective, 1);
            }
            ENDCG
        }
    }
}
