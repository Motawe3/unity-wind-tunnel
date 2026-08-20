using System;
using System.Collections.Generic;
using UnityEngine;

namespace Motawea.WindTunnel
{
    public enum TunnelResolution
    {
        Coarse = 128,
        Medium = 192,
        Fine = 256,
        [Tooltip("Resolves ~25 cm features (wing gaps, splitters) on a full-size tunnel.")]
        Ultra = 384,
        [Tooltip("Heaviest tier: ~3 GB GPU memory on a 26 m tunnel. Watch the cell-count guard.")]
        Extreme = 512
    }

    public enum GroundSimulation
    {
        [Tooltip("No floor: vehicle in free air (use for aircraft-like checks).")]
        OpenFloor,
        [Tooltip("Stationary floor, like a fixed-ground wind tunnel.")]
        FixedFloor,
        [Tooltip("Rolling road: floor moves at freestream speed (most road-realistic).")]
        MovingBelt
    }

    /// <summary>
    /// The virtual wind tunnel. Owns the voxelizer and LBM solver, converts between
    /// world and lattice space, ticks the simulation, and publishes engineering samples
    /// (Cd, Cl front/rear, Cy, ...) with convergence tracking.
    ///
    /// Local axes: +X streamwise (wind blows along +X, so the vehicle nose should face
    /// -X), +Y up, +Z lateral.
    /// </summary>
    [AddComponentMenu("Wind Tunnel/Tunnel Domain")]
    [ExecuteAlways] // OnDisable must release GPU buffers in edit mode too
    public class WindTunnelDomain : MonoBehaviour
    {
        [Header("Tunnel")]
        [Tooltip("Tunnel dimensions in meters: X = length (streamwise), Y = height, Z = width.")]
        public Vector3 size = new Vector3(20f, 5f, 8f);
        public TunnelResolution resolution = TunnelResolution.Medium;

        [Tooltip("Advanced: exact streamwise cell count, overriding the tier. Set by the auto-fit when it is locking a cell size so two differently-sized vehicles solve at the same resolution. 0 = use the tier.")]
        [Min(0)] public int streamwiseCellsOverride;

        [Header("Test conditions")]
        [Tooltip("Freestream air speed in m/s (30 m/s = 108 km/h).")]
        [Min(0.5f)] public float inletSpeedMs = 30f;
        public AirProperties air = AirProperties.StandardSeaLevel;
        public GroundSimulation ground = GroundSimulation.FixedFloor;
        [Tooltip("Apply a rotating-wall boundary condition on tagged wheels (rolling road realism).")]
        public bool rotatingWheels = true;

        [Header("Vehicle")]
        public AeroVehicle vehicle;
        [Tooltip("Sizes the tunnel around the vehicle, seats it at the right station, and applies its class policy (ground plane, wheel rotation, working fluid). Runs on the first start with a new vehicle when 'fit automatically' is on.")]
        public AutoFitSettings autoFit = new AutoFitSettings();
        [Tooltip("Seal hollow game models (no underbody/floor pan) during voxelization: each vertical column is closed between its lowest and highest surface. Prevents huge fake forces from interior air pockets; ground clearance is preserved. Disable only for watertight CAD meshes.")]
        public bool sealOpenModels = true;
        [Tooltip("Interpolated (Bouzidi) walls: place the wall at its true sub-cell position instead of exactly half-way between cell centres. Half-way bounce-back turns every smooth surface into a staircase of cell-sized steps, and those steps do not shrink relative to the boundary layer as the grid is refined. Takes effect on the next (re-)voxelization.")]
        public bool interpolatedWalls;
        [Tooltip("Equilibrium wall model (PROTOTYPE, Stage 2.5c): at the first fluid cell off the body, replace the LES eddy viscosity with the value that reproduces Spalding law-of-the-wall shear. Only meaningful when the first cell sits inside the boundary layer (y/delta <~ 0.2, i.e. fine grids). Default off.")]
        public bool wallModel;

        [Tooltip("Soft voxels: cells only partially covered by geometry (thin wing elements, endplates, sub-cell gaps) act as partially open instead of snapping to fully solid. Fine details keep interacting with the flow at any resolution. Takes effect on the next (re-)voxelization.")]
        public bool softVoxels = true;

        [Header("Solver")]
        [Tooltip("LBM steps advanced per Tick/frame. Higher = faster convergence, more GPU time per frame.")]
        [Range(1, 128)] public int stepsPerTick = 16;
        [Tooltip("Solver steps between force samples.")]
        [Range(10, 2000)] public int sampleIntervalSteps = 100;
        [Tooltip("WALE LES constant Cw (advanced). 0.5 is calibrated for this D3Q19 solver (sharp-body Cd validates Re-independently there); the classical 0.325 under-damps the lattice and grid noise reads as spurious drag.")]
        [Range(0.2f, 0.8f)] public float lesCw = 0.5f;

        [Header("Convergence")]
        [Tooltip("Coefficient of variation of Cd over the window below which the run counts as converged.")]
        [Range(0.001f, 0.1f)] public float convergenceTolerance = 0.01f;
        [Range(3, 50)] public int convergenceWindow = 10;
        [Tooltip("Startup transient discarded before any force sample is taken, in flow-through times. Removes the impulsive-start spike from the history/chart, as in wind-tunnel practice.")]
        [Range(0f, 1f)] public float transientSkipFlowThroughs = 0.3f;

        public const float BlockageWarningRatio = 0.075f;
        // Accidental-grid guard, sized to the per-cell memory model: 80M cells at
        // TunnelAutoFit.BytesPerCell (118 B, FP16 lattice) is ~8.8 GB — the working set a
        // 3080-class GPU can actually hold. The FP32-era value was 20M (~3.8 GB at 190 B).
        public const int MaxCells = 80_000_000;
        const int MaxHistory = 4096;

        // ---- runtime state ----
        VehicleVoxelizer _voxelizer;
        LbmSolver _solver;
        long _stepsAtLastSample;
        float _frontAxleX, _rearAxleX, _pivotX;
        bool _liftSplitValid;
        Vector3 _cachedDomainPos, _cachedVehiclePos;
        Quaternion _cachedDomainRot, _cachedVehicleRot;
        float _lastAutoRevoxTime;
        bool _transientDiscarded;
        AeroVehicle _fittedVehicle;

        public bool IsRunning { get; private set; }
        public LbmSolver Solver => _solver;
        public LatticeUnits Units { get; private set; }
        public Vector3Int Dims { get; private set; }
        public float CellSize { get; private set; }
        public Vector3 EffectiveSize { get; private set; }
        public Matrix4x4 WorldToLattice { get; private set; }
        public Matrix4x4 LatticeToWorld { get; private set; }
        /// <summary>Reference area the coefficients are divided by (frontal, planform or override).</summary>
        public float FrontalAreaM2 { get; private set; }
        /// <summary>Frontal silhouette measured from the voxelized body, m².</summary>
        public float MeasuredFrontalAreaM2 { get; private set; }
        /// <summary>Planform (projected-from-above) area measured from the voxelized body, m².</summary>
        public float MeasuredPlanformAreaM2 { get; private set; }
        /// <summary>Which convention <see cref="FrontalAreaM2"/> follows.</summary>
        public AeroReferenceAreaMode ReferenceAreaMode { get; private set; } = AeroReferenceAreaMode.FrontalSilhouette;
        public float BlockageRatio { get; private set; }
        public AeroSample LatestSample { get; private set; }
        public bool HasSample { get; private set; }
        public List<AeroSample> SampleHistory { get; } = new List<AeroSample>();

        public event Action<AeroSample> SampleReady;

        /// <summary>Steps for the freestream to traverse the tunnel once (transient scale).</summary>
        public float FlowThroughSteps => Dims.x > 0 ? Dims.x / Mathf.Max(Units.ULattice, 1e-4f) : 0f;

        public float ConvergenceCV { get; private set; } = float.PositiveInfinity;
        public bool IsConverged { get; private set; }

        // ------------------------------------------------------------------ lifecycle

        public void StartSimulation()
        {
            if (vehicle == null)
            {
                Debug.LogError("Wind Tunnel: assign an AeroVehicle to the WindTunnelDomain before starting.", this);
                return;
            }

            // A tunnel that was fitted to a different vehicle is the wrong tunnel for
            // this one — a domain sized for an SUV reports blockage-inflated numbers
            // for a boat. Only a *swap* triggers this: a hand-built tunnel starting
            // for the first time is left exactly as authored, which is what the
            // validation harnesses depend on.
            if (autoFit != null && autoFit.fitAutomatically &&
                _fittedVehicle != null && _fittedVehicle != vehicle)
                FitToVehicle();

            ComputeGrid();
            long cells = (long)Dims.x * Dims.y * Dims.z;
            if (cells > MaxCells)
            {
                Debug.LogError($"Wind Tunnel: grid {Dims} = {cells:N0} cells exceeds the {MaxCells:N0} cell guard. Reduce tunnel size or resolution.", this);
                return;
            }

            _voxelizer ??= new VehicleVoxelizer();
            RunVoxelization();

            if (_solver != null && _solver.Dims != Dims)
            {
                _solver.Dispose();
                _solver = null;
            }
            _solver ??= new LbmSolver(Dims, _voxelizer.FlagsBuffer, _voxelizer.CoverageBuffer);
            _solver.SetFields(_voxelizer.FlagsBuffer, _voxelizer.CoverageBuffer, _voxelizer.SurfaceFractionBuffer);

            ResetFlow();
            CacheTransforms();
            IsRunning = true;
        }

        public void StopSimulation() => IsRunning = false;

        public void ResumeSimulation()
        {
            if (_solver != null) IsRunning = true;
        }

        /// <summary>Resets the flow field to freestream and clears sample history.</summary>
        public void ResetFlow()
        {
            if (_solver == null) return;
            var p = BuildParams();
            _solver.Reset(p);
            SampleHistory.Clear();
            HasSample = false;
            IsConverged = false;
            ConvergenceCV = float.PositiveInfinity;
            _stepsAtLastSample = 0;
            _transientDiscarded = false;
        }

        /// <summary>Re-voxelizes after the vehicle (or tunnel) moved — yaw step, ride height step.</summary>
        public void Revoxelize(bool resetFlow = true)
        {
            if (_voxelizer == null || _solver == null) return;
            RunVoxelization();
            _solver.SetFields(_voxelizer.FlagsBuffer, _voxelizer.CoverageBuffer, _voxelizer.SurfaceFractionBuffer);
            if (resetFlow) ResetFlow();
        }

        public void ShutdownSimulation()
        {
            IsRunning = false;
            _solver?.Dispose(); _solver = null;
            _voxelizer?.Dispose(); _voxelizer = null;
        }

        void Update()
        {
            if (Application.isPlaying && IsRunning)
                Tick();
        }

        void OnDisable() => ShutdownSimulation();

        /// <summary>Advances the solver. Called from Update in play mode or an editor ticker.</summary>
        public void Tick()
        {
            if (_solver == null || !IsRunning) return;

            FollowTransformChanges();

            var p = BuildParams();
            _solver.Step(stepsPerTick, p);

            // Discard the impulsive-start transient (pressure wave from initializing
            // the field around the body) before any measurement, as tunnels do.
            if (!_transientDiscarded)
            {
                if (_solver.StepCount < transientSkipFlowThroughs * FlowThroughSteps)
                    return;
                _solver.DiscardForces();
                _transientDiscarded = true;
                _stepsAtLastSample = _solver.StepCount;
                return;
            }

            if (_solver.StepCount - _stepsAtLastSample >= sampleIntervalSteps)
            {
                _stepsAtLastSample = _solver.StepCount;
                _solver.SampleForces(HandleForceSample);
            }
        }

        // ------------------------------------------------------------------ internals

        /// <summary>
        /// The lattice grid is anchored to this transform. If the tunnel or the vehicle
        /// is moved mid-run, the world-to-lattice mapping goes stale and the vehicle's
        /// voxels no longer match its visible pose — so re-anchor and re-voxelize
        /// (throttled, without resetting the flow, so interactive dragging stays live).
        /// </summary>
        void FollowTransformChanges()
        {
            bool domainMoved = transform.position != _cachedDomainPos ||
                               transform.rotation != _cachedDomainRot;
            bool vehicleMoved = vehicle != null &&
                                (vehicle.transform.position != _cachedVehiclePos ||
                                 vehicle.transform.rotation != _cachedVehicleRot);
            if (!domainMoved && !vehicleMoved) return;
            if (Time.realtimeSinceStartup - _lastAutoRevoxTime < 0.25f) return;

            _lastAutoRevoxTime = Time.realtimeSinceStartup;
            if (domainMoved) RecomputeLatticeAnchor();
            Revoxelize(resetFlow: false);
            CacheTransforms();
        }

        void CacheTransforms()
        {
            _cachedDomainPos = transform.position;
            _cachedDomainRot = transform.rotation;
            if (vehicle != null)
            {
                _cachedVehiclePos = vehicle.transform.position;
                _cachedVehicleRot = vehicle.transform.rotation;
            }
        }

        void RecomputeLatticeAnchor()
        {
            Vector3 origin = transform.position - transform.rotation * (EffectiveSize * 0.5f);
            LatticeToWorld = Matrix4x4.TRS(origin, transform.rotation, Vector3.one * CellSize);
            WorldToLattice = LatticeToWorld.inverse;
        }

        /// <summary>
        /// Grid dimensions a tunnel of this size gets at this tier. The tier sets the
        /// streamwise cell count; the cross-stream axes follow at the same cell size.
        /// Shared with the auto-fit so its memory estimate matches what is allocated.
        /// </summary>
        public static Vector3Int ComputeDims(Vector3 tunnelSize, TunnelResolution tier, int streamwiseCells = 0)
        {
            int nx = streamwiseCells > 0 ? streamwiseCells : (int)tier;
            float dx = Mathf.Max(tunnelSize.x, 1e-4f) / nx;
            return new Vector3Int(
                nx,
                Mathf.Max(Mathf.RoundToInt(tunnelSize.y / dx), 8),
                Mathf.Max(Mathf.RoundToInt(tunnelSize.z / dx), 8));
        }

        /// <summary>Streamwise cell count in force: the override when set, else the tier.</summary>
        public int StreamwiseCells => streamwiseCellsOverride > 0 ? streamwiseCellsOverride : (int)resolution;

        void ComputeGrid()
        {
            int nx = StreamwiseCells;
            float dx = size.x / nx;
            Vector3Int d = ComputeDims(size, resolution, streamwiseCellsOverride);
            int ny = d.y;
            int nz = d.z;

            CellSize = dx;
            Dims = new Vector3Int(nx, ny, nz);
            EffectiveSize = new Vector3(nx * dx, ny * dx, nz * dx);

            Vector3 origin = transform.position - transform.rotation * (EffectiveSize * 0.5f);
            LatticeToWorld = Matrix4x4.TRS(origin, transform.rotation, Vector3.one * dx);
            WorldToLattice = LatticeToWorld.inverse;

            float refLength = ComputeReferenceLength();
            Units = new LatticeUnits(dx, inletSpeedMs, air, refLength);
        }

        /// <summary>
        /// Characteristic length for the Reynolds number: body length for ground and
        /// marine craft, mean aerodynamic chord (planform area / span) for aircraft,
        /// which is the aeronautical convention. Only the reported Reynolds numbers
        /// depend on it — the lattice viscosity does not.
        /// </summary>
        float ComputeReferenceLength()
        {
            if (vehicle == null) return 4.5f;

            if (vehicle.TryComputeAeroBounds(transform.rotation, out Bounds local))
            {
                if (vehicle.vehicleClass == AeroVehicleClass.Aircraft &&
                    MeasuredPlanformAreaM2 > 0f && local.size.z > 0.01f)
                    return Mathf.Max(MeasuredPlanformAreaM2 / local.size.z, 0.05f);
                return Mathf.Max(local.size.x, 0.05f);
            }

            Bounds b = vehicle.ComputeBounds();
            Vector3 axis = transform.rotation * Vector3.right;
            Vector3 e = b.extents;
            // Extent of the AABB projected on the streamwise axis.
            float ext = Mathf.Abs(axis.x) * e.x + Mathf.Abs(axis.y) * e.y + Mathf.Abs(axis.z) * e.z;
            return Mathf.Max(2f * ext, 0.5f);
        }

        void RunVoxelization()
        {
            var wheels = WheelLatticeData.Build(vehicle, WorldToLattice, CellSize);
            bool groundIsWall = ground != GroundSimulation.OpenFloor;
            _voxelizer.Voxelize(vehicle, Dims, WorldToLattice, CellSize, groundIsWall, wheels,
                                sealOpenModels, softVoxels, interpolatedWalls);

            MeasuredFrontalAreaM2 = _voxelizer.FrontalAreaM2;
            MeasuredPlanformAreaM2 = _voxelizer.PlanformAreaM2;
            SelectReferenceArea();

            // Blockage is always about the frontal obstruction, even when the
            // coefficients are normalized by planform area (aircraft) or an override.
            BlockageRatio = MeasuredFrontalAreaM2 / (EffectiveSize.y * EffectiveSize.z);

            if (BlockageRatio > BlockageWarningRatio)
                Debug.LogWarning($"Wind Tunnel: blockage ratio {BlockageRatio:P1} exceeds {BlockageWarningRatio:P0}. " +
                                 "Coefficients will read high; enlarge the tunnel cross-section.", this);
            if (_voxelizer.SolidCellCount == 0)
                Debug.LogWarning("Wind Tunnel: voxelization produced no solid cells — is the vehicle inside the tunnel?", this);

            ComputeAxleStations(wheels);
        }

        /// <summary>
        /// Applies the vehicle's reference-area policy. An aircraft is normalized by
        /// wing planform area and a car by frontal silhouette; using one convention for
        /// the other makes the coefficient meaningless even though the force is right.
        /// </summary>
        void SelectReferenceArea()
        {
            ReferenceAreaMode = vehicle != null ? vehicle.EffectiveAreaMode : AeroReferenceAreaMode.FrontalSilhouette;

            switch (ReferenceAreaMode)
            {
                case AeroReferenceAreaMode.Manual:
                    FrontalAreaM2 = vehicle != null && vehicle.referenceAreaOverride > 0f
                        ? vehicle.referenceAreaOverride
                        : MeasuredFrontalAreaM2;
                    break;
                case AeroReferenceAreaMode.Planform:
                    if (MeasuredPlanformAreaM2 > 0f)
                    {
                        FrontalAreaM2 = MeasuredPlanformAreaM2;
                    }
                    else
                    {
                        FrontalAreaM2 = MeasuredFrontalAreaM2;
                        ReferenceAreaMode = AeroReferenceAreaMode.FrontalSilhouette;
                        Debug.LogWarning("Wind Tunnel: planform area measured as zero; falling back to the frontal silhouette.", this);
                    }
                    break;
                default:
                    FrontalAreaM2 = MeasuredFrontalAreaM2;
                    break;
            }
        }

        // ------------------------------------------------------------------ auto-fit

        /// <summary>
        /// Sizes this tunnel around the assigned vehicle, seats the vehicle at the
        /// right station inside it and applies the vehicle class policy. Safe to call
        /// while stopped or running; a running solver is restarted on the new grid.
        /// </summary>
        public AutoFitPlan FitToVehicle(bool logSummary = true)
        {
            autoFit ??= new AutoFitSettings();
            // Feed the previous measurement back in: the first fit predicts the frontal
            // area from the bounding box, a re-fit knows what the silhouette really is.
            float measured = _fittedVehicle == vehicle ? MeasuredFrontalAreaM2 : 0f;
            var plan = TunnelAutoFit.Plan(vehicle, transform.rotation, transform.position,
                                          resolution, autoFit, measured);
            if (!plan.valid)
            {
                Debug.LogWarning($"Wind Tunnel: tunnel auto-fit skipped — {plan.error}.", this);
                return plan;
            }

            if (autoFit.applyClassPolicy)
            {
                ground = plan.ground;
                rotatingWheels = plan.rotatingWheels;
                if (air.medium != plan.medium)
                {
                    air.medium = plan.medium;
                    if (air.pressurePa <= 0f) air.pressurePa = 101325f;
                }
            }

            size = plan.tunnelSize;
            transform.position = plan.tunnelCenter;
            // A locked cell size sets the cell count directly; otherwise the tier does,
            // and any previous override must be cleared or it would silently win.
            streamwiseCellsOverride = plan.streamwiseCells;
            if (autoFit.autoResolution || plan.streamwiseCells > 0) resolution = plan.resolution;
            if (autoFit.positionVehicle && plan.vehicleDelta.sqrMagnitude > 1e-10f)
                vehicle.transform.position += plan.vehicleDelta;

            if (autoFit.fitVisualization) FitVisualization(plan);

            _fittedVehicle = vehicle;
            ComputeGrid();
            CacheTransforms();

            if (logSummary)
            {
                string notes = plan.notes.Count > 0 ? "\n  · " + string.Join("\n  · ", plan.notes) : "";
                Debug.Log($"Wind Tunnel: fitted the tunnel to '{vehicle.Name}' ({vehicle.vehicleClass}) — {plan.Summary()}{notes}", this);
            }

            // A live run is now solving on a stale grid; rebuild it around the new box.
            if (IsRunning) StartSimulation();
            return plan;
        }

        /// <summary>Re-aims the smoke rakes and slice plane bound to this tunnel at the fitted body.</summary>
        void FitVisualization(in AutoFitPlan plan)
        {
            foreach (var rake in FindObjectsByType<FlowParticles>(FindObjectsSortMode.None))
            {
                if (rake == null || rake.tunnel != this) continue;
                rake.transform.position = plan.rakePosition;
                rake.transform.rotation = transform.rotation;
                rake.rakeWidth = Mathf.Max(plan.rakeSize.x, 0.05f);
                rake.rakeHeight = Mathf.Max(plan.rakeSize.y, 0.05f);
            }

            foreach (var slice in FindObjectsByType<FlowSlice>(FindObjectsSortMode.None))
            {
                if (slice == null || slice.tunnel != this) continue;
                // Vertical centreline plane spanning the domain (normal = lateral).
                slice.transform.position = plan.slicePosition;
                slice.transform.rotation = transform.rotation * Quaternion.Euler(0f, 90f, 0f);
                slice.transform.localScale = new Vector3(plan.tunnelSize.x, plan.tunnelSize.y, 1f);
            }
        }

        void ComputeAxleStations(in WheelLatticeData wheels)
        {
            Vector3 pivot = vehicle != null ? vehicle.TurntablePivotPosition : transform.position;
            _pivotX = WorldToLattice.MultiplyPoint3x4(pivot).x;

            _liftSplitValid = wheels.Count >= 2;
            if (!_liftSplitValid) return;

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < wheels.Count; i++)
            {
                float x = wheels.Positions[i].x;
                min = Mathf.Min(min, x);
                max = Mathf.Max(max, x);
            }

            if (max - min < 1f)
            {
                _liftSplitValid = false;
                return;
            }

            float mid = 0.5f * (min + max);
            float frontSum = 0f, rearSum = 0f;
            int frontN = 0, rearN = 0;
            for (int i = 0; i < wheels.Count; i++)
            {
                float x = wheels.Positions[i].x;
                if (x < mid) { frontSum += x; frontN++; }
                else { rearSum += x; rearN++; }
            }
            _frontAxleX = frontSum / Mathf.Max(frontN, 1);
            _rearAxleX = rearSum / Mathf.Max(rearN, 1);
        }

        SolverParams BuildParams()
        {
            return new SolverParams
            {
                ULattice = Units.ULattice,
                NuLattice = Units.NuLattice,
                LesCw = lesCw,
                GroundMoving = ground == GroundSimulation.MovingBelt,
                WheelsRotating = rotatingWheels && ground != GroundSimulation.OpenFloor,
                InterpolatedWalls = interpolatedWalls,
                WallModel = wallModel,
                PivotLattice = WorldToLattice.MultiplyPoint3x4(
                    vehicle != null ? vehicle.TurntablePivotPosition : transform.position),
                Wheels = WheelLatticeData.Build(vehicle, WorldToLattice, CellSize)
            };
        }

        void HandleForceSample(ForceSample raw)
        {
            var s = AeroForces.Compute(
                raw, Units, air,
                FrontalAreaM2,
                MeasuredFrontalAreaM2,
                EffectiveSize.y * EffectiveSize.z,
                _frontAxleX, _rearAxleX, _pivotX, _liftSplitValid);

            LatestSample = s;
            HasSample = true;
            SampleHistory.Add(s);
            if (SampleHistory.Count > MaxHistory)
                SampleHistory.RemoveAt(0);

            UpdateConvergence(s);
            SampleReady?.Invoke(s);
        }

        void UpdateConvergence(in AeroSample latest)
        {
            IsConverged = false;
            if (SampleHistory.Count < convergenceWindow) return;

            // Ignore the initial transient: wait ~1.5 flow-through times.
            if (latest.solverStep < 1.5f * FlowThroughSteps) return;

            float mean = 0f;
            int start = SampleHistory.Count - convergenceWindow;
            for (int i = start; i < SampleHistory.Count; i++)
                mean += SampleHistory[i].cd;
            mean /= convergenceWindow;

            float var = 0f;
            for (int i = start; i < SampleHistory.Count; i++)
            {
                float d = SampleHistory[i].cd - mean;
                var += d * d;
            }
            float std = Mathf.Sqrt(var / convergenceWindow);

            // Low CV alone passes during slow monotonic drift (wake still developing).
            // Also require the two halves of the window to agree: the drift between
            // half-means must be inside the tolerance too.
            int half = convergenceWindow / 2;
            float meanOld = 0f, meanNew = 0f;
            for (int i = start; i < start + half; i++) meanOld += SampleHistory[i].cd;
            for (int i = SampleHistory.Count - half; i < SampleHistory.Count; i++) meanNew += SampleHistory[i].cd;
            meanOld /= half;
            meanNew /= half;
            float drift = Mathf.Abs(mean) > 1e-5f ? Mathf.Abs(meanNew - meanOld) / Mathf.Abs(mean) : float.PositiveInfinity;

            ConvergenceCV = Mathf.Abs(mean) > 1e-5f
                ? Mathf.Max(std / Mathf.Abs(mean), drift)
                : float.PositiveInfinity;
            IsConverged = ConvergenceCV < convergenceTolerance;
        }

        void OnValidate()
        {
            size = Vector3.Max(size, Vector3.one);
        }
    }
}
