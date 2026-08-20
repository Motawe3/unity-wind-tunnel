// Renders the flow-slice cross-section into a RenderTexture for UI display:
// each texel maps to a point on the slice rectangle, sampled from the solved
// 3D field. The vehicle shows up naturally as the zero-velocity region.
Shader "WindTunnel/FlowSliceImage"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "AeroRamps.hlsl"

            TEXTURE3D(_VelocityTex);
            SAMPLER(sampler_VelocityTex);

            float4x4 _WorldToLattice;
            float3 _DimsF;
            float3 _SliceOrigin;   // world-space corner of the slice rectangle
            float3 _SliceRight;    // world-space edge vectors (full extents)
            float3 _SliceUp;
            float _UInlet;
            int _Mode;             // 0 = speed ratio, 1 = pressure coefficient
            float _CpRange;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 world = _SliceOrigin + _SliceRight * i.uv.x + _SliceUp * i.uv.y;
                float3 uvw = mul(_WorldToLattice, float4(world, 1.0)).xyz / _DimsF;

                // Outside the tunnel volume: console background.
                if (any(uvw < 0.0) || any(uvw > 1.0))
                    return half4(0.051, 0.063, 0.082, 1.0);

                float4 s = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, uvw, 0);

                float3 rgb;
                if (_Mode == 0)
                    rgb = SpeedRamp(length(s.xyz) / max(_UInlet, 1e-5) / 1.6);
                else
                {
                    float cp = ((s.w - 1.0) / 3.0) / (0.5 * _UInlet * _UInlet);
                    rgb = CpRamp(cp / max(_CpRange, 1e-3));
                }
                return half4(rgb, 1.0);
            }
            ENDHLSL
        }
    }
}
