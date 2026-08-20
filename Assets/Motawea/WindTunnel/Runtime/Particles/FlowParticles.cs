using UnityEngine;
using UnityEngine.Rendering;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// Smoke rake: emits GPU tracer particles from a rectangle at this transform's
    /// position (local Z = rake width, local Y = rake height) and advects them through
    /// the tunnel's solved velocity field. Move/rotate it like a wind-tunnel smoke
    /// wand; multiple rakes may target the same tunnel.
    /// </summary>
    [AddComponentMenu("Wind Tunnel/Flow Particle Rake")]
    [ExecuteAlways] // edit-mode lifecycle: the Wind Tunnel dashboard ticks rakes outside play mode
    public class FlowParticles : MonoBehaviour
    {
        public WindTunnelDomain tunnel;

        [Range(1024, 1048576)] public int particleCount = 65536;
        [Tooltip("Rake width in meters (local Z).")]
        [Min(0.05f)] public float rakeWidth = 4f;
        [Tooltip("Rake height in meters (local Y).")]
        [Min(0.05f)] public float rakeHeight = 2.5f;
        [Tooltip("Nominal tracer lifetime in tunnel flow-through times.")]
        [Range(0.2f, 4f)] public float lifetimeFlowThroughs = 1.5f;

        [Header("Look")]
        [Min(0.001f)] public float particleSize = 0.03f;
        [Tooltip("Trail length in history segments. 1 = plain tracer dots.")]
        [Range(1, 32)] public int trailSegments = 12;
        [Tooltip("Flow travel (in solver steps) between recorded trail points. Total trail length ≈ segments × spacing, independent of playback speed and steps-per-tick. Effective spacing can't be shorter than one tick's travel.")]
        [Range(2f, 64f)] public float trailSpacingSteps = 16f;
        [Tooltip("Slow-motion factor for the tracers only — the solver keeps its own pace. In (quasi-)steady flow slowed tracers still follow the same paths.")]
        [Range(0.02f, 1f)] public float playbackSpeed = 1f;
        [Range(0f, 2f)] public float intensity = 0.8f;
        [Tooltip("Fades tracers whose paths the vehicle never deformed. At zero all smoke shows; raising it hides the undisturbed outer mass so only flow interacting with the body stays visible.")]
        [Range(0f, 1f)] public float depthContrast = 0f;

        [Header("Speed color ramp")]
        [Tooltip("Tracer color below freestream speed.")]
        public Color slowColor = new Color(0.15f, 0.35f, 1f);
        [Tooltip("Tracer color at freestream speed.")]
        public Color freestreamColor = new Color(0.95f, 0.97f, 1f);
        [Tooltip("Tracer color above freestream speed.")]
        public Color fastColor = new Color(1f, 0.25f, 0.1f);

        ComputeShader _cs;
        int _kInit, _kUpdate;
        ComputeBuffer _buffer;
        ComputeBuffer _trail;
        int _trailHead;
        float _trailAccum;
        Material _material;
        long _lastSolverStep;
        bool _initialized;
        uint _frame;

        const int Stride = 9 * sizeof(float);

        void OnEnable()
        {
            // Resources load lazily in EnsureResources: OnEnable timing differs between
            // edit mode, play mode, and domain reloads, so never rely on it having run.
            _initialized = false;
            // Draws must be issued per camera at render time: a RenderPrimitives call
            // from an editor update expires before the Scene view repaints.
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        bool EnsureResources()
        {
            if (_cs == null)
            {
                _cs = Resources.Load<ComputeShader>("WindTunnel/FlowParticles");
                if (_cs == null)
                {
                    Debug.LogError("Wind Tunnel: missing Resources/WindTunnel/FlowParticles.compute.", this);
                    enabled = false;
                    return false;
                }
                _kInit = _cs.FindKernel("ParticlesInit");
                _kUpdate = _cs.FindKernel("ParticlesUpdate");
            }

            if (_material == null)
            {
                var shader = Shader.Find("WindTunnel/FlowParticle");
                if (shader == null)
                {
                    Debug.LogError("Wind Tunnel: missing WindTunnel/FlowParticle shader (check that it compiled).", this);
                    enabled = false;
                    return false;
                }
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            return true;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            _buffer?.Release(); _buffer = null;
            _trail?.Release(); _trail = null;
            if (_material != null) DestroyImmediate(_material);
            _material = null;
        }

        void Update()
        {
            if (Application.isPlaying)
                Tick();
        }

        /// <summary>Advances the particle simulation; safe to call from an editor ticker too.</summary>
        public void Tick()
        {
            if (tunnel == null || tunnel.Solver == null) return;
            if (!EnsureResources()) return;

            EnsureBuffer();

            long solverStep = tunnel.Solver.StepCount;
            float stepsThisTick = Mathf.Clamp(solverStep - _lastSolverStep, 0, 256) * playbackSpeed;
            _lastSolverStep = solverStep;

            bool needInit = !_initialized;
            if (!needInit && stepsThisTick > 0)
            {
                // Advance the trail ring by flow distance, not by tick, so trail length
                // is independent of playback speed and steps-per-tick. At most one
                // advance per tick (skipped ring slots would hold stale positions).
                _trailAccum = Mathf.Min(_trailAccum + stepsThisTick, trailSpacingSteps);
                if (_trailAccum >= trailSpacingSteps)
                {
                    _trailAccum = 0f;
                    _trailHead = (_trailHead + 1) % TrailPoints;
                }
            }

            BindSimUniforms(stepsThisTick);

            if (needInit)
            {
                _cs.SetBuffer(_kInit, "_Particles", _buffer);
                _cs.SetBuffer(_kInit, "_Trail", _trail);
                _cs.SetTexture(_kInit, "_VelocityTex", tunnel.Solver.VelocityField);
                _cs.SetTexture(_kInit, "_FluidMask", tunnel.Solver.FluidMask);
                _cs.Dispatch(_kInit, Mathf.CeilToInt(particleCount / 64f), 1, 1);
                _initialized = true;
            }
            else if (stepsThisTick > 0)
            {
                _cs.SetBuffer(_kUpdate, "_Particles", _buffer);
                _cs.SetBuffer(_kUpdate, "_Trail", _trail);
                _cs.SetTexture(_kUpdate, "_VelocityTex", tunnel.Solver.VelocityField);
                _cs.SetTexture(_kUpdate, "_FluidMask", tunnel.Solver.FluidMask);
                _cs.Dispatch(_kUpdate, Mathf.CeilToInt(particleCount / 64f), 1, 1);
            }
        }

        int TrailPoints => trailSegments + 1;

        /// <summary>Removes all visible particles; they re-emit gradually if the tunnel keeps running.</summary>
        public void Clear()
        {
            _initialized = false;
            _trailHead = 0;
            _trailAccum = 0f;
        }

        void EnsureBuffer()
        {
            int trailCount = particleCount * TrailPoints;
            if (_buffer != null && _buffer.count == particleCount &&
                _trail != null && _trail.count == trailCount) return;

            _buffer?.Release();
            _buffer = new ComputeBuffer(particleCount, Stride);
            _trail?.Release();
            _trail = new ComputeBuffer(trailCount, 4 * sizeof(float));
            _trailHead = 0;
            _initialized = false;
        }

        void BindSimUniforms(float stepsThisTick)
        {
            var dims = tunnel.Dims;
            var w2l = tunnel.WorldToLattice;

            Vector3 right = transform.forward * rakeWidth;   // local Z spans width
            Vector3 up = transform.up * rakeHeight;
            Vector3 origin = transform.position - 0.5f * (right + up);

            // Clamp the emission rectangle into the lattice: a rake dragged (or left)
            // outside the tunnel would otherwise spawn instantly-recycled particles.
            Vector3 o = w2l.MultiplyPoint3x4(origin);
            Vector3 rL = w2l.MultiplyVector(right);
            Vector3 uL = w2l.MultiplyVector(up);
            Vector3 far = o + rL + uL;
            for (int a = 0; a < 3; a++)
            {
                float lo = Mathf.Min(o[a], far[a]), hi = Mathf.Max(o[a], far[a]);
                float dimMax = dims[a] - 1.5f;
                float shift = 0f;
                if (lo < 1.5f) shift = 1.5f - lo;
                else if (hi > dimMax) shift = dimMax - hi;
                o[a] += shift;
            }

            _cs.SetInt("_ParticleCount", particleCount);
            _cs.SetInt("_TrailPoints", TrailPoints);
            _cs.SetInt("_TrailHead", _trailHead);
            _cs.SetVector("_Dims", new Vector3(dims.x, dims.y, dims.z));
            _cs.SetFloat("_StepsThisTick", stepsThisTick);
            _cs.SetFloat("_LifeSteps", lifetimeFlowThroughs * tunnel.FlowThroughSteps);
            _cs.SetFloat("_UInlet", tunnel.Units.ULattice);
            _cs.SetInt("_FrameSeed", unchecked((int)(_frame++ * 2654435761u)));
            _cs.SetVector("_RakeOrigin", o);
            _cs.SetVector("_RakeRight", rL);
            _cs.SetVector("_RakeUp", uL);
        }

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) return;
            // Deliberately no Solver/IsRunning check: a paused (or even shut-down)
            // tunnel keeps the last smoke state visible while the user navigates.
            if (tunnel == null || _buffer == null || _trail == null || _material == null) return;
            if (!_initialized) return;

            _material.SetBuffer("_Particles", _buffer);
            _material.SetBuffer("_Trail", _trail);
            _material.SetInt("_TrailPoints", TrailPoints);
            _material.SetInt("_TrailHead", _trailHead);
            _material.SetMatrix("_LatticeToWorld", tunnel.LatticeToWorld);
            _material.SetFloat("_UInlet", tunnel.Units.ULattice);
            _material.SetFloat("_ParticleSize", particleSize);
            _material.SetFloat("_Intensity", intensity);
            _material.SetFloat("_DepthContrast", depthContrast);
            _material.SetColor("_ColorSlow", slowColor);
            _material.SetColor("_ColorMid", freestreamColor);
            _material.SetColor("_ColorFast", fastColor);

            var bounds = new Bounds(tunnel.transform.position, tunnel.EffectiveSize * 1.2f);
            var rp = new RenderParams(_material)
            {
                camera = cam, // this draw belongs to the camera being rendered right now
                worldBounds = bounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 6 * trailSegments, particleCount);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.8f);
            Vector3 right = transform.forward * rakeWidth;
            Vector3 up = transform.up * rakeHeight;
            Vector3 o = transform.position - 0.5f * (right + up);
            Gizmos.DrawLine(o, o + right);
            Gizmos.DrawLine(o + right, o + right + up);
            Gizmos.DrawLine(o + right + up, o + up);
            Gizmos.DrawLine(o + up, o);
        }
    }
}
