// Movable CFD-style slice plane: samples the solved 3D field on a quad and maps
// speed ratio (sequential ramp) or pressure coefficient (diverging ramp) to color.
Shader "WindTunnel/FlowSlice"
{
    Properties
    {
        _Opacity ("Opacity", Range(0, 1)) = 0.85
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

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
            float _UInlet;      // lattice freestream speed
            int _Mode;          // 0 = speed ratio, 1 = pressure coefficient
            float _Opacity;
            float _CpRange;     // |Cp| mapped to ramp ends

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 lattice = mul(_WorldToLattice, float4(i.positionWS, 1.0)).xyz;
                float3 uvw = lattice / _DimsF;
                if (any(uvw < 0.0) || any(uvw > 1.0)) discard;

                float4 s = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, uvw, 0);

                float3 rgb;
                if (_Mode == 0)
                {
                    float ratio = length(s.xyz) / max(_UInlet, 1e-5);
                    rgb = SpeedRamp(ratio / 1.6);   // 1.6x freestream at the hot end
                }
                else
                {
                    // LBM pressure: p = cs^2 (rho - 1), cs^2 = 1/3. Cp = (p - p_inf) / q.
                    float cp = ((s.w - 1.0) / 3.0) / (0.5 * _UInlet * _UInlet);
                    rgb = CpRamp(cp / max(_CpRange, 1e-3));
                }
                return half4(rgb, _Opacity);
            }
            ENDHLSL
        }
    }
}
