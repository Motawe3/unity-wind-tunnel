// Vehicle-surface heatmap: while the overlay is active, SurfaceHeatmap.cs swaps
// this material onto every voxelized renderer of the car. Each pixel steps a
// short distance out along the surface normal into open flow and colors itself
// from the solved field there — the standard CFD surface-plot view (ParaView's
// pressure / wallShearStress coloring).
//
// Unlit on purpose: the colormap IS the data. A gentle hemispherical shade term
// keeps curvature readable without a light in the scene (batch/reports included).
Shader "WindTunnel/SurfaceHeatmap"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

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
            TEXTURE3D(_FluidMask);
            SAMPLER(sampler_FluidMask);

            float4x4 _WorldToLattice;
            float3 _DimsF;
            float _UInlet;       // lattice freestream speed
            int _Mode;           // 0 = pressure (Cp), 1 = wall shear (relative), 2 = speed ratio
            float _CpRange;      // |Cp| mapped to the ends of the diverging ramp
            float _ShearRange;   // near-wall tangential speed ratio at the hot end
            float _OffsetCells;  // first sample distance from the surface, in cells

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

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            // The LBM density field carries a standing period-2 "checkerboard"
            // acoustic mode (rho alternates sign every other cell), which reads as
            // a regular dimple pattern on the bodywork. A generic smoothing kernel
            // only attenuates it — a cubic B-spline leaves ~1/3 of it — but a
            // 2-cell box filter has an EXACT zero at that frequency: cos(pi/2) = 0
            // along every axis. Averaging 8 trilinear taps at the cell-corner
            // offsets (±0.5 per axis) IS that box filter, and because each tap is
            // hardware trilinear the composite kernel is a smooth quadratic
            // B-spline shape for free. Perfect notch, 8 taps per texture.
            void SampleFieldSmooth(float3 lattice, out float4 vel, out float mask)
            {
                vel = 0.0;
                mask = 0.0;
                [unroll]
                for (int c = 0; c < 8; c++)
                {
                    float3 o = float3((c & 1) != 0 ? 0.5 : -0.5,
                                      (c & 2) != 0 ? 0.5 : -0.5,
                                      (c & 4) != 0 ? 0.5 : -0.5);
                    float3 uvw = (lattice + o) / _DimsF;
                    vel += 0.125 * SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex, uvw, 0);
                    mask += 0.125 * SAMPLE_TEXTURE3D_LOD(_FluidMask, sampler_FluidMask, uvw, 0).r;
                }
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 lattice = mul(_WorldToLattice, float4(i.positionWS, 1.0)).xyz;
                // The lattice transform is rotation + uniform scale, so directions
                // just need renormalizing after the basis change.
                float3 nLat = normalize(mul((float3x3)_WorldToLattice, n));

                // March outward until the sample point sits in open flow: the first
                // cell or two straddle the wall (solid + partial coverage), where the
                // blend is dominated by bounce-back zeros. Cheap trilinear taps find
                // the distance; the actual read is one smoothed B-spline sample there.
                // Pixels that never reach open flow (cabin interiors, sealed pockets)
                // fall through with mask ~ 0 and render as no-data gray.
                float dist = _OffsetCells;
                [unroll]
                for (int it = 0; it < 3; it++)
                {
                    float3 uvw = (lattice + nLat * dist) / _DimsF;
                    if (any(uvw < 0.0) || any(uvw > 1.0)) break;
                    if (SAMPLE_TEXTURE3D_LOD(_FluidMask, sampler_FluidMask, uvw, 0).r > 0.35) break;
                    dist += 1.0;
                }
                float4 s;
                float mask;
                SampleFieldSmooth(lattice + nLat * dist, s, mask);

                float shade = 0.8 + 0.2 * saturate(dot(n, normalize(float3(0.25, 0.9, 0.35))));

                if (mask <= 0.05)
                    return half4(float3(0.42, 0.44, 0.47) * shade, 1.0);

                float3 rgb;
                if (_Mode == 0)
                {
                    // Wind-tunnel practice: Cp references the MEASURED freestream
                    // static pressure, not the nominal one. The domain's mean
                    // density settles slightly off rho0 = 1 (outlet/far-field BCs),
                    // which would shift the whole body into fake suction or
                    // compression — so read p_inf from a fixed probe just behind
                    // the inlet at 70% height, well clear of body and floor.
                    float rhoRef = SAMPLE_TEXTURE3D_LOD(_VelocityTex, sampler_VelocityTex,
                        float3(2.5 / _DimsF.x, 0.7, 0.5), 0).w;

                    // Solid cells write rho = 1 exactly, so a near-wall trilinear
                    // sample attenuates the gauge density (rho - 1) by the sampled
                    // open fraction — divide it back out to reconstruct the
                    // fluid-only value (same trick FlowParticles uses for velocity).
                    // Cp = ((rho - rhoRef)/3) / q, otherwise the slice plane's math.
                    // Jet ramp (green at zero) — the industry surface-plot look.
                    float rho = 1.0 + (s.w - 1.0) / max(mask, 0.2);
                    float cp = ((rho - rhoRef) / 3.0) / (0.5 * _UInlet * _UInlet);
                    rgb = PressureRamp(cp / max(_CpRange, 1e-3));
                }
                else
                {
                    float3 u = s.xyz / max(mask, 0.2);   // pore (fluid-only) velocity
                    if (_Mode == 1)
                    {
                        // Wall-shear pattern: tangential speed a fixed distance off
                        // the wall. Relative by design — the display texture holds no
                        // eddy viscosity, so absolute tau would be a made-up number;
                        // attached fast flow reads hot, separation reads cold, which
                        // is the engineering signal.
                        float3 ut = u - dot(u, n) * n;
                        rgb = SpeedRamp(length(ut) / max(_UInlet, 1e-5) / max(_ShearRange, 1e-3));
                    }
                    else
                    {
                        // Speed ratio, same 1.6x hot end as the slice plane.
                        rgb = SpeedRamp(length(u) / max(_UInlet, 1e-5) / 1.6);
                    }
                }
                return half4(rgb * shade, 1.0);
            }
            ENDHLSL
        }
    }
}
