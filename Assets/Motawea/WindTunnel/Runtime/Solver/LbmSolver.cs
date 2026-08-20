using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Motawea.WindTunnel
{
    /// <summary>Per-run solver configuration in lattice units.</summary>
    public struct SolverParams
    {
        public float ULattice;
        public float NuLattice;
        public float LesCw;
        public bool GroundMoving;
        public bool WheelsRotating;
        /// <summary>Sub-cell (Bouzidi) wall placement instead of half-way bounce-back.</summary>
        public bool InterpolatedWalls;
        /// <summary>Equilibrium wall model (Spalding) at first-off-the-body fluid cells —
        /// Stage 2.5c prototype. Replaces the WALE eddy viscosity there with the value
        /// that reproduces the law-of-the-wall shear stress.</summary>
        public bool WallModel;
        public Vector3 PivotLattice;
        public WheelLatticeData Wheels;
    }

    /// <summary>
    /// GPU D3Q19 lattice-Boltzmann solver. Owns the distribution buffers, the
    /// macroscopic velocity/density 3D texture (for visualization), and the
    /// momentum-exchange force accumulator with async readback.
    /// </summary>
    public class LbmSolver : IDisposable
    {
        // Must match Lbm.compute.
        const float ForceScale = 4096f;
        const float MomentScale = 256f;

        readonly ComputeShader _cs;
        readonly int _kInit, _kStep, _kBlend;
        readonly Vector3Int _dims;
        readonly int _cellCount;

        ComputeBuffer _fA, _fB;
        ComputeBuffer _forceAccum;
        bool _srcIsA;

        // Double-buffered so the WALE gradient stencil in Lbm.compute reads a
        // complete previous step instead of racing the cells being overwritten.
        RenderTexture _velA, _velB;

        // Visualization snapshot, copied from the working buffers once per Step()
        // batch. Consumers must never sample the ping-pong buffers directly: they
        // are rebound as UAVs dozens of times per frame, and a missed UAV->SRV
        // transition on a sampled read shows up as intermittent garbage/flicker.
        RenderTexture _velDisplay;

        /// <summary>xyz = velocity (lattice units), w = density. 3D texture for visualization.
        /// A stable snapshot texture — safe to sample at any point in the frame.</summary>
        public RenderTexture VelocityField => _velDisplay;

        /// <summary>
        /// Temporal smoothing of the display snapshot: each Step() batch blends
        /// display = lerp(display, field, this). The LBM field carries standing
        /// acoustic (checkerboard) waves whose phase differs from batch to batch,
        /// so raw snapshots flicker; averaging cancels them while the wake, which
        /// evolves over many batches, stays sharp. 1 = raw copy (no smoothing).
        /// Purely visual — forces read the distribution buffers, never this.
        /// </summary>
        public float DisplaySmoothing { get; set; } = 0.15f;

        /// <summary>1 = fluid, 0 = solid. Samplers divide a trilinear velocity sample by this
        /// to reconstruct the fluid-only average near walls (solid zeros otherwise drag it down).</summary>
        public RenderTexture FluidMask { get; private set; }

        public Vector3Int Dims => _dims;
        public long StepCount { get; private set; }

        long _stepsAtWindowStart;
        static readonly int[] Zeros = new int[6];

        public LbmSolver(Vector3Int dims, ComputeBuffer flags, ComputeBuffer coverage)
        {
            _dims = dims;
            _cellCount = dims.x * dims.y * dims.z;

            _cs = Resources.Load<ComputeShader>("WindTunnel/Lbm");
            if (_cs == null)
                throw new InvalidOperationException("Wind Tunnel: missing Resources/WindTunnel/Lbm.compute");
            _kInit = _cs.FindKernel("LbmInit");
            _kStep = _cs.FindKernel("LbmStep");
            _kBlend = _cs.FindKernel("LbmBlendDisplay");

            // 19 distributions stored as FP16 deviations, packed two per uint ->
            // 10 uints per cell (Lbm.compute, "FP16 lattice storage").
            _fA = new ComputeBuffer(_cellCount * 10, sizeof(uint));
            _fB = new ComputeBuffer(_cellCount * 10, sizeof(uint));
            _forceAccum = new ComputeBuffer(6, sizeof(int));
            _forceAccum.SetData(Zeros);

            _velA = CreateVelocityTexture(dims, "Wind Tunnel Velocity Field A");
            _velB = CreateVelocityTexture(dims, "Wind Tunnel Velocity Field B");
            _velDisplay = CreateVelocityTexture(dims, "Wind Tunnel Velocity Display");

            FluidMask = new RenderTexture(dims.x, dims.y, 0, RenderTextureFormat.RFloat)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = dims.z,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "Wind Tunnel Fluid Mask"
            };
            FluidMask.Create();

            _flags = flags;
            _coverage = coverage;
        }

        static RenderTexture CreateVelocityTexture(Vector3Int dims, string name)
        {
            var rt = new RenderTexture(dims.x, dims.y, 0, RenderTextureFormat.ARGBHalf)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = dims.z,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = name
            };
            rt.Create();
            return rt;
        }

        ComputeBuffer _flags;
        ComputeBuffer _coverage;
        ComputeBuffer _surfaceFraction;
        ComputeBuffer _nuTDiag;

        /// <summary>
        /// Opt-in per-cell WALE eddy-viscosity capture (lattice units), written by every
        /// step while on. Purely diagnostic: default off, adds no physics. Cells that
        /// never collide (solids, inlets) keep 0 — the buffer is zero-initialised
        /// (see the DESIGN.md invariant on skippable writes).
        /// </summary>
        public bool CaptureNuT { get; set; }

        /// <summary>Per-cell ν_T after a Step() with <see cref="CaptureNuT"/> on; null before.</summary>
        public ComputeBuffer NuTField => _nuTDiag;

        /// <summary>Rebinds the cell flags and coverage (after re-voxelization the buffers may be recreated).</summary>
        public void SetFields(ComputeBuffer flags, ComputeBuffer coverage, ComputeBuffer surfaceFraction = null)
        {
            _flags = flags;
            _coverage = coverage;
            _surfaceFraction = surfaceFraction ?? coverage;
        }

        void BindCommon(int kernel, in SolverParams p)
        {
            _cs.SetInts("_Dims", _dims.x, _dims.y, _dims.z);
            _cs.SetFloat("_UInlet", p.ULattice);
            _cs.SetFloat("_Nu", p.NuLattice);
            _cs.SetFloat("_LesCw", p.LesCw);
            _cs.SetInt("_GroundMoving", p.GroundMoving ? 1 : 0);
            _cs.SetInt("_WheelsRotating", p.WheelsRotating ? 1 : 0);
            _cs.SetVector("_PivotLattice", p.PivotLattice);
            _cs.SetInt("_WheelCount", p.Wheels.Count);
            _cs.SetVectorArray("_WheelPos", p.Wheels.Positions ?? new Vector4[4]);
            _cs.SetVectorArray("_WheelAxis", p.Wheels.Axes ?? new Vector4[4]);
            _cs.SetBuffer(kernel, "_Flags", _flags);
            _cs.SetBuffer(kernel, "_Coverage", _coverage);
            // Always bound: an unbound StructuredBuffer is a device fault on some
            // backends even when the branch that reads it is switched off.
            _cs.SetBuffer(kernel, "_SurfaceFraction", _surfaceFraction ?? _coverage);
            _cs.SetInt("_InterpolatedWalls", p.InterpolatedWalls ? 1 : 0);
            _cs.SetTexture(kernel, "_FluidMask", FluidMask);
        }

        /// <summary>Resets the whole field to freestream equilibrium.</summary>
        public void Reset(in SolverParams p)
        {
            BindCommon(_kInit, p);
            // Initialize BOTH distribution buffers and velocity textures, not just the
            // first-read ones. Solid cells early-return in LbmStep and never write their
            // f entries, so unwritten regions keep whatever the allocation held — fresh
            // VRAM on the first run of a session, the previous run's field on later runs
            // (the driver recycles freed pages). Any read that slips through then makes
            // results depend on session order, which is exactly the kind of bug that
            // looks like physics.
            _cs.SetBuffer(_kInit, "_FDst", _fB);
            _cs.SetTexture(_kInit, "_Velocity", _velB);
            VehicleVoxelizer.DispatchLinear(_cs, _kInit, _cellCount);
            _cs.SetBuffer(_kInit, "_FDst", _fA);
            _cs.SetTexture(_kInit, "_Velocity", _velA);
            VehicleVoxelizer.DispatchLinear(_cs, _kInit, _cellCount);
            Graphics.CopyTexture(_velA, _velDisplay);
            _srcIsA = true;
            StepCount = 0;
            BeginForceWindow();
        }

        public void Step(int steps, in SolverParams p)
        {
            for (int s = 0; s < steps; s++)
            {
                BindCommon(_kStep, p);
                _cs.SetBuffer(_kStep, "_FSrc", _srcIsA ? _fA : _fB);
                _cs.SetBuffer(_kStep, "_FDst", _srcIsA ? _fB : _fA);
                _cs.SetTexture(_kStep, "_VelocityPrev", _srcIsA ? _velA : _velB);
                _cs.SetTexture(_kStep, "_Velocity", _srcIsA ? _velB : _velA);
                _cs.SetBuffer(_kStep, "_ForceAccum", _forceAccum);
                // Keywords, not uniforms: the all-off variant must compile to the exact
                // code the bit-reproducible demo gate was recorded against.
                if (p.WallModel) _cs.EnableKeyword("AERO_WALL_MODEL");
                else _cs.DisableKeyword("AERO_WALL_MODEL");
                if (CaptureNuT)
                {
                    if (_nuTDiag == null)
                    {
                        _nuTDiag = new ComputeBuffer(_cellCount, sizeof(float));
                        _nuTDiag.SetData(new float[_cellCount]);
                    }
                    _cs.EnableKeyword("AERO_NUT_TAP");
                    _cs.SetBuffer(_kStep, "_NuTOut", _nuTDiag);
                }
                else
                {
                    _cs.DisableKeyword("AERO_NUT_TAP");
                }
                VehicleVoxelizer.DispatchLinear(_cs, _kStep, _cellCount);
                _srcIsA = !_srcIsA;
                StepCount++;
            }
            if (steps > 0)
            {
                var latest = _srcIsA ? _velA : _velB;
                if (DisplaySmoothing >= 0.999f)
                {
                    Graphics.CopyTexture(latest, _velDisplay);
                }
                else
                {
                    _cs.SetInts("_Dims", _dims.x, _dims.y, _dims.z);
                    _cs.SetFloat("_DisplayBlend", Mathf.Clamp(DisplaySmoothing, 0.02f, 1f));
                    _cs.SetTexture(_kBlend, "_DisplaySrc", latest);
                    _cs.SetTexture(_kBlend, "_DisplayDst", _velDisplay);
                    VehicleVoxelizer.DispatchLinear(_cs, _kBlend, _cellCount);
                }
            }
        }

        void BeginForceWindow()
        {
            _forceAccum.SetData(Zeros);
            _stepsAtWindowStart = StepCount;
        }

        /// <summary>Throws away the force accumulated so far (used to discard the startup transient).</summary>
        public void DiscardForces() => BeginForceWindow();

        /// <summary>
        /// Asynchronously reads the force accumulator averaged over the steps since the
        /// last sample, then starts a new accumulation window.
        /// </summary>
        public void SampleForces(Action<ForceSample> onSample)
        {
            int windowSteps = (int)(StepCount - _stepsAtWindowStart);
            if (windowSteps <= 0) return;
            long stepAtRequest = StepCount;

            AsyncGPUReadback.Request(_forceAccum, request =>
            {
                if (request.hasError || onSample == null) return;
                var data = request.GetData<int>();
                float inv = 1f / windowSteps;
                onSample(new ForceSample
                {
                    ForceLattice = new Vector3(
                        data[0] / ForceScale, data[1] / ForceScale, data[2] / ForceScale) * inv,
                    MomentLattice = new Vector3(
                        data[3] / MomentScale, data[4] / MomentScale, data[5] / MomentScale) * inv,
                    Steps = windowSteps,
                    SolverStep = stepAtRequest
                });
            });

            // Queued after the readback on the GPU timeline, so the request sees the
            // pre-clear values.
            BeginForceWindow();
        }

        public void Dispose()
        {
            _fA?.Release(); _fA = null;
            _fB?.Release(); _fB = null;
            _forceAccum?.Release(); _forceAccum = null;
            _nuTDiag?.Release(); _nuTDiag = null;
            ReleaseVelocityTexture(ref _velA);
            ReleaseVelocityTexture(ref _velB);
            ReleaseVelocityTexture(ref _velDisplay);
            if (FluidMask != null)
            {
                FluidMask.Release();
                UnityEngine.Object.DestroyImmediate(FluidMask);
                FluidMask = null;
            }
        }

        static void ReleaseVelocityTexture(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            rt = null;
        }
    }
}
