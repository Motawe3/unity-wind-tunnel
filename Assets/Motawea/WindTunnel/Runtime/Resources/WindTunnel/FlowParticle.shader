// GPU smoke tracer with history trail: each particle renders as a camera-facing
// ribbon through its last N recorded positions (a streaklet, like long-exposure
// wind-tunnel smoke), tapered and faded toward the tail, colored by local speed.
Shader "WindTunnel/FlowParticle"
{
    Properties
    {
        _Intensity ("Intensity", Range(0, 2)) = 0.8
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

            struct FlowParticle
            {
                float3 pos;
                float life;
                float3 vel;
                float maxLife;
                float disturb; // peak speed deficit below freestream (matches compute struct)
            };

            StructuredBuffer<FlowParticle> _Particles;
            StructuredBuffer<float4> _Trail;   // xyz lattice pos, w speed (w < 0 = unused slot)
            int _TrailPoints;
            int _TrailHead;

            float4x4 _LatticeToWorld;
            float _UInlet;         // lattice freestream speed
            float _ParticleSize;   // ribbon head width, world units
            float _Intensity;
            float _DepthContrast;  // 0 = show all smoke, 1 = only body-deformed flow
            float4 _ColorSlow;     // speed ramp: below freestream
            float4 _ColorMid;      // at freestream
            float4 _ColorFast;     // above freestream

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;         // x = age along trail, y = across ribbon (-1..1)
                float4 color : COLOR;
            };

            // Configurable ramp: slow -> freestream -> fast (colors set per rake).
            float3 SpeedRamp(float t)
            {
                return t < 1.0
                    ? lerp(_ColorSlow.rgb, _ColorMid.rgb, saturate(t))
                    : lerp(_ColorMid.rgb, _ColorFast.rgb, saturate(t - 1.0));
            }

            // Per-vertex: x = which segment end (0 = newer, 1 = older), y = side.
            static const float2 CORNERS[6] =
            {
                float2(0,  1), float2(1,  1), float2(0, -1),
                float2(1,  1), float2(1, -1), float2(0, -1)
            };

            Varyings Vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                uint points = (uint)max(_TrailPoints, 2);
                uint segCount = points - 1u;
                uint seg = vid / 6u;               // 0 = newest segment
                float2 k = CORNERS[vid % 6u];

                uint i0 = ((uint)_TrailHead + points - seg) % points;        // newer end
                uint i1 = ((uint)_TrailHead + points - seg - 1u) % points;   // older end
                float4 t0 = _Trail[iid * points + i0];
                float4 t1 = _Trail[iid * points + i1];

                FlowParticle p = _Particles[iid];
                float lifeT = saturate(p.life / max(p.maxLife, 1e-3));
                float fade = smoothstep(0.0, 0.1, lifeT) * smoothstep(1.0, 0.85, lifeT);
                if (t0.w < 0.0 || t1.w < 0.0) fade = 0.0;

                // Depth contrast: keep tracers by their peak speed deficit — "how blue
                // did this tracer ever get". Dimming alone cannot thin this smoke
                // (overlapping low-alpha ribbons restack into a solid mass), and a
                // moving threshold snaps: the freestream bulk all sits at deficit ≈ 0,
                // so it crosses any cut at once. Instead each tracer holds a stable
                // random rank and the never-slowed background thins out one tracer at
                // a time as the slider rises — cubed, since overdraw hides linear
                // density changes — while interacting flow keeps a guaranteed floor.
                if (_DepthContrast > 0.001)
                {
                    float vis = smoothstep(0.25, 0.5, p.disturb);
                    float bg = 1.0 - _DepthContrast;
                    bg = bg * bg * bg;
                    uint h = iid ^ 0x9E3779B9u;
                    h ^= h >> 16; h *= 0x7feb352du;
                    h ^= h >> 15; h *= 0x846ca68bu;
                    h ^= h >> 16;
                    float rank = (h & 0xFFFFu) / 65535.0;
                    float keep = lerp(bg, 1.0, vis);
                    fade *= saturate((keep - rank) / 0.08);
                }

                float3 w0 = mul(_LatticeToWorld, float4(t0.xyz, 1.0)).xyz;
                float3 w1 = mul(_LatticeToWorld, float4(t1.xyz, 1.0)).xyz;

                float age = (seg + k.x) / (float)segCount;   // 0 head .. 1 tail
                float3 endPos = lerp(w0, w1, k.x);

                float3 dir = w0 - w1;
                float dirLen = length(dir);
                if (dirLen < 1e-5) dir = float3(1, 0, 0); else dir /= dirLen;

                float3 viewDir = _WorldSpaceCameraPos - endPos;
                float3 side = cross(dir, viewDir);
                float sideLen = length(side);
                side = sideLen < 1e-5 ? float3(0, 1, 0) : side / sideLen;

                float width = _ParticleSize * (1.0 - 0.7 * age);
                float3 worldPos = endPos + side * (k.y * width);

                // Viewed head-on (looking along the flow), ribbons foreshorten to
                // sub-pixel dots and seem to vanish. Pad the ends apart in the camera
                // plane so every trail keeps a minimum on-screen footprint.
                float3 viewN = normalize(viewDir);
                float3 alongP = dir - viewN * dot(dir, viewN);
                float apLen = length(alongP);
                float3 apN = apLen > 1e-4 ? alongP / apLen : normalize(cross(viewN, side));
                float deficit = max(_ParticleSize - dirLen * apLen, 0.0);
                worldPos += apN * (deficit * (0.5 - k.x));

                float speed = k.x > 0.5 ? t1.w : t0.w;
                float ratio = speed / max(_UInlet, 1e-5);

                Varyings o;
                o.positionCS = TransformWorldToHClip(worldPos);
                o.uv = float2(age, k.y);
                o.color = float4(SpeedRamp(ratio), fade * (1.0 - age) * _Intensity);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                // Soft edge across the ribbon width.
                float mask = saturate(1.0 - i.uv.y * i.uv.y);
                return half4(i.color.rgb, i.color.a * mask * mask);
            }
            ENDHLSL
        }
    }
}
