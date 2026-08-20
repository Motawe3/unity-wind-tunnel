using System;
using System.Collections.Generic;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// Executes a queue of aerodynamic test procedures against the tunnel: applies each
    /// measurement point's configuration (yaw turntable rotation, ride-height offset,
    /// ground mode), re-voxelizes, runs to convergence, records the sample, and
    /// restores the vehicle transform afterwards. Ticked from play-mode Update or the
    /// editor ticker.
    /// </summary>
    [AddComponentMenu("Wind Tunnel/Aero Test Runner")]
    public class AeroTestRunner : MonoBehaviour
    {
        public WindTunnelDomain tunnel;
        public List<AeroTestDefinition> testQueue = new List<AeroTestDefinition>();

        /// <summary>
        /// Flow-through times allowed for the wake to develop before the averaging
        /// window opens. Matches the convergence monitor's own settling allowance.
        /// </summary>
        public const float SettleFlowThroughs = 1.5f;

        /// <summary>
        /// Solver steps actually between force samples. The tunnel samples between tick
        /// batches, so the nominal interval is rounded up to a whole number of ticks.
        /// </summary>
        float EffectiveSampleInterval()
        {
            float nominal = Mathf.Max(tunnel.sampleIntervalSteps, 1);
            int perTick = Mathf.Max(tunnel.stepsPerTick, 1);
            return Mathf.Ceil(nominal / perTick) * perTick;
        }

        public bool IsRunning { get; private set; }
        public AeroTestSession CurrentSession { get; private set; }
        public AeroTestSession LastCompletedSession { get; private set; }
        public string StatusLine { get; private set; } = "Idle";
        public float PointProgress01 { get; private set; }

        /// <summary>Monotonic 0..1 progress across ALL points of the running session.</summary>
        public float SessionProgress01 => IsRunning && _totalPoints > 0
            ? Mathf.Clamp01((_donePoints + Mathf.Clamp01(PointProgress01)) / _totalPoints)
            : 0f;

        int _totalPoints;
        int _donePoints;

        public event Action<AeroTestSession> SessionCompleted;
        public event Action<AeroTestResult> TestCompleted;

        /// <summary>The test definition currently executing, null when idle.</summary>
        public AeroTestDefinition CurrentTest =>
            IsRunning && _active != null && _testIndex >= 0 && _testIndex < _active.Count
                ? _active[_testIndex] : null;

        List<AeroTestDefinition> _active;
        int _testIndex;
        int _pointIndex;
        IReadOnlyList<float> _points;
        AeroTestResult _currentResult;
        Vector3 _savedVehiclePos;
        Quaternion _savedVehicleRot;
        bool _transformSaved;
        float _savedAreaOverride;
        bool _areaLocked;

        /// <summary>Runs every enabled test in the queue.</summary>
        public void StartQueue() => StartSession(testQueue.FindAll(t => t.enabled));

        /// <summary>Runs a single test (regardless of its enabled flag).</summary>
        public void StartSingle(AeroTestDefinition definition)
        {
            if (definition == null) return;
            StartSession(new List<AeroTestDefinition> { definition });
        }

        void StartSession(List<AeroTestDefinition> tests)
        {
            if (IsRunning) return;
            if (tunnel == null || tunnel.vehicle == null)
            {
                Debug.LogError("Wind Tunnel: test runner needs a tunnel with an assigned vehicle.", this);
                return;
            }
            if (tests == null || tests.Count == 0)
            {
                Debug.LogWarning("Wind Tunnel: no enabled tests to run.", this);
                return;
            }

            CurrentSession = new AeroTestSession
            {
                vehicleName = tunnel.vehicle.Name,
                vehicleClass = tunnel.vehicle.vehicleClass,
                watercraftMode = tunnel.vehicle.watercraftMode,
                vehicleClassLabel = tunnel.vehicle.vehicleClass == AeroVehicleClass.Watercraft
                    ? $"{tunnel.vehicle.vehicleClass} ({tunnel.vehicle.watercraftMode})"
                    : tunnel.vehicle.vehicleClass.ToString(),
                packageVersion = WindTunnelVersion.Value,
                startedAtIso = DateTime.Now.ToString("s")
            };

            SaveVehicleTransform();

            // Record which convention the area follows *before* the lock below turns it
            // into an override. Otherwise every locked session reports "manual
            // override" and a later comparison can no longer tell a frontal-area run
            // from a planform one — which is the one check that must never be fooled.
            CurrentSession.referenceAreaMode = tunnel.vehicle.EffectiveAreaMode;
            CurrentSession.referenceAreaBasis = AeroVehicleProfile.AreaBasisLabel(CurrentSession.referenceAreaMode);

            // SAE practice: one reference area for the whole session, measured at the
            // authored (zero-yaw) pose — otherwise Cd(psi) divides by a growing
            // silhouette and reads lower under yaw. User overrides are respected.
            _savedAreaOverride = tunnel.vehicle.referenceAreaOverride;
            _areaLocked = false;
            if (_savedAreaOverride <= 0f)
            {
                tunnel.StartSimulation();
                if (tunnel.FrontalAreaM2 > 0f)
                {
                    tunnel.vehicle.referenceAreaOverride = tunnel.FrontalAreaM2;
                    _areaLocked = true;
                }
            }

            _active = tests;
            _testIndex = -1;
            _totalPoints = 0;
            _donePoints = 0;
            foreach (var t in tests)
            {
                _totalPoints += t.EnumeratePoints().Count;

                // The uncertainty on a point falls as 1/sqrt(averaged flow-throughs), so
                // the step cap is what sets the precision a run can reach. Say what the
                // cap buys, up front, rather than leaving a queue of caveated results.
                float minimum = Mathf.Max(t.averageOverFlowThroughs, 1f);
                float needed = (SettleFlowThroughs + minimum) * tunnel.FlowThroughSteps;
                float affordable = t.maxStepsPerPoint / Mathf.Max(tunnel.FlowThroughSteps, 1f) - SettleFlowThroughs;

                if (t.maxStepsPerPoint < needed)
                    Debug.LogWarning($"Wind Tunnel: '{t.testName}' caps each point at {t.maxStepsPerPoint:N0} steps, but " +
                                     $"settling plus a {minimum:0.#} flow-through average needs {needed:N0} on this " +
                                     "grid. Every point will read as unsettled — raise the cap or shorten the " +
                                     "averaging window.", this);
                else if (affordable < 4f * minimum)
                    Debug.Log($"Wind Tunnel: '{t.testName}' can average about {affordable:0.#} flow-throughs per point " +
                              $"within its {t.maxStepsPerPoint:N0}-step cap. Uncertainty falls as 1/√(flow-throughs), " +
                              "so raise the cap if the points come back unsettled.", this);
            }
            IsRunning = true;
            AdvanceToNextTest();
        }

        public void AbortQueue()
        {
            if (!IsRunning) return;
            IsRunning = false;
            RestoreVehicleTransform();
            RestoreAreaOverride();
            tunnel.StopSimulation();
            StatusLine = "Aborted";
        }

        void RestoreAreaOverride()
        {
            if (_areaLocked && tunnel != null && tunnel.vehicle != null)
                tunnel.vehicle.referenceAreaOverride = _savedAreaOverride;
            _areaLocked = false;
        }

        void Update()
        {
            if (Application.isPlaying)
                Tick();
        }

        /// <summary>
        /// Advances the state machine one frame. The solver itself is ticked by the
        /// tunnel's Update (play mode) or the Wind Tunnel editor ticker (edit mode).
        /// </summary>
        public void Tick()
        {
            if (!IsRunning || tunnel == null) return;

            var def = _active[_testIndex];
            long steps = tunnel.Solver?.StepCount ?? 0;
            PointProgress01 = Mathf.Clamp01(steps / (float)def.maxStepsPerPoint);
            StatusLine = $"{def.testName} — point {_pointIndex + 1}/{_points.Count} " +
                         $"({def.ParameterName}: {_points[_pointIndex]:0.###}) " +
                         $"steps {steps:N0}, CV {tunnel.ConvergenceCV:P2}";

            bool capped = steps >= def.maxStepsPerPoint;
            if (!tunnel.HasSample) return;

            // The measurement is the mean over an averaging window, not the reading
            // that happens to be current. Wait until the window is actually full, then
            // judge settling on the uncertainty OF THAT MEAN — a bluff-body wake never
            // stops oscillating, so demanding that the instantaneous signal go quiet is
            // a criterion this class of body can never meet.
            float minimumWindow = Mathf.Max(def.averageOverFlowThroughs, 1f);
            // Samples do not arrive at the nominal interval: the tunnel only checks
            // between tick batches, so the real spacing is the interval rounded UP to a
            // whole number of ticks (100 steps at 32 steps/tick is really 128). Sizing
            // the window off the nominal rate over-counts by that ratio and the window
            // never fills.
            float samplesPerFlowThrough = tunnel.FlowThroughSteps / EffectiveSampleInterval();

            // The averaging window GROWS with the run rather than trailing at a fixed
            // span. A fixed span pins the uncertainty at whatever that span buys
            // (~5% here) no matter how long the point runs, so running longer would
            // improve nothing; averaging everything since the wake settled makes the
            // uncertainty fall as 1/sqrt(time), which is what lets a longer cap
            // actually buy precision.
            float settleAfterSampling = Mathf.Max(SettleFlowThroughs - tunnel.transientSkipFlowThroughs, 0f);
            int skip = Mathf.CeilToInt(settleAfterSampling * samplesPerFlowThrough);
            int available = tunnel.SampleHistory.Count - skip;
            int wanted = Mathf.CeilToInt(minimumWindow * samplesPerFlowThrough);

            bool windowFull = available >= wanted;
            float flowThroughs = Mathf.Max(available, 0) / samplesPerFlowThrough;

            // The uncertainty is computed from whatever window exists, not only from a
            // full one: a point that stops at the step cap still has a mean, and that
            // mean's error is knowable and must be reported. Only *settling* requires
            // the full window — a wide band is a result, an absent band is a gap.
            float sem = available >= 2
                ? AeroSample.StandardErrorFraction(tunnel.SampleHistory, available, flowThroughs)
                : float.PositiveInfinity;
            bool settled = windowFull && sem < tunnel.convergenceTolerance;

            if (!settled && !capped) return;

            int averaged = Mathf.Clamp(available, 1, tunnel.SampleHistory.Count);
            _currentResult.points.Add(new AeroTestPointResult
            {
                parameter = _points[_pointIndex],
                sample = AeroSample.Average(tunnel.SampleHistory, averaged),
                converged = settled,
                // Recorded per point: a later comparison needs to know how much of a
                // delta between two runs is just run-to-run scatter.
                convergenceCv = float.IsInfinity(tunnel.ConvergenceCV) ? -1f : tunnel.ConvergenceCV,
                standardError = float.IsInfinity(sem) ? -1f : sem,
                samplesAveraged = averaged,
                flowThroughsAveraged = Mathf.Max(flowThroughs, 0f),
                solverSteps = steps
            });
            _donePoints++;
            if (capped && !settled)
                Debug.LogWarning($"Wind Tunnel: '{def.testName}' point {_points[_pointIndex]:0.###} hit the step cap " +
                                 $"before the mean settled (uncertainty {(sem < 0f || float.IsInfinity(sem) ? -1f : sem):P2} " +
                                 $"of Cd over {averaged} samples). The mean is still reported — it is just less certain.", this);

            _pointIndex++;
            if (_pointIndex < _points.Count)
            {
                ApplyPoint(def, _points[_pointIndex]);
            }
            else
            {
                CurrentSession.tests.Add(_currentResult);
                TestCompleted?.Invoke(_currentResult);
                AdvanceToNextTest();
            }
        }

        void AdvanceToNextTest()
        {
            _testIndex++;
            if (_testIndex >= _active.Count)
            {
                FinishSession();
                return;
            }

            var def = _active[_testIndex];
            _points = def.EnumeratePoints();
            _pointIndex = 0;
            _currentResult = new AeroTestResult
            {
                testName = def.testName,
                kind = def.kind,
                parameterName = def.ParameterName,
                speedMs = def.speedMs,
                ground = def.ground,
                rotatingWheels = def.rotatingWheels
            };
            ApplyPoint(def, _points[0]);
        }

        void ApplyPoint(AeroTestDefinition def, float parameter)
        {
            RestoreVehicleTransform();
            SaveVehicleTransform();

            var vehicleT = tunnel.vehicle.transform;
            switch (def.kind)
            {
                case AeroTestKind.YawSweep:
                    Vector3 pivot = tunnel.vehicle.TurntablePivotPosition;
                    Vector3 up = tunnel.transform.rotation * Vector3.up;
                    vehicleT.RotateAround(pivot, up, parameter);
                    break;
                case AeroTestKind.RideHeightSweep:
                    vehicleT.position += (tunnel.transform.rotation * Vector3.up) * parameter;
                    break;
                case AeroTestKind.AngleOfAttackSweep:
                    // Pitch about the lateral (+Z) axis. A positive rotation about +Z
                    // carries +X toward +Y, and the nose points along -X, so the sign
                    // is flipped to make positive alpha mean nose-up.
                    Vector3 pitchPivot = tunnel.vehicle.TurntablePivotPosition;
                    Vector3 lateral = tunnel.transform.rotation * Vector3.forward;
                    vehicleT.RotateAround(pitchPivot, lateral, -parameter);
                    break;
            }

            tunnel.inletSpeedMs = def.speedMs;
            tunnel.ground = def.ground;
            tunnel.rotatingWheels = def.rotatingWheels;
            tunnel.StartSimulation();
        }

        void FinishSession()
        {
            IsRunning = false;
            RestoreVehicleTransform();
            RestoreAreaOverride();
            tunnel.StopSimulation();

            CurrentSession.finishedAtIso = DateTime.Now.ToString("s");
            CurrentSession.fluidMedium = tunnel.air.medium;
            CurrentSession.airDensity = tunnel.air.Density;
            CurrentSession.airTemperatureC = tunnel.air.temperatureC;
            CurrentSession.kinematicViscosity = tunnel.air.KinematicViscosity;
            CurrentSession.frontalAreaM2 = tunnel.FrontalAreaM2;
            CurrentSession.referenceAreaLocked = _areaLocked;
            // referenceAreaMode/Basis were captured at StartSession, before the area
            // lock masked them as a manual override.
            CurrentSession.measuredFrontalAreaM2 = tunnel.MeasuredFrontalAreaM2;
            CurrentSession.measuredPlanformAreaM2 = tunnel.MeasuredPlanformAreaM2;
            CurrentSession.blockageRatio = tunnel.BlockageRatio;
            CurrentSession.gridInfo = $"{tunnel.Dims.x}×{tunnel.Dims.y}×{tunnel.Dims.z} @ {tunnel.CellSize * 1000f:0} mm";
            CurrentSession.tunnelSizeM = tunnel.EffectiveSize;
            CurrentSession.resolutionTier = tunnel.resolution;
            CurrentSession.cellSizeM = tunnel.CellSize;
            CurrentSession.cellCount = (long)tunnel.Dims.x * tunnel.Dims.y * tunnel.Dims.z;
            CurrentSession.softVoxels = tunnel.softVoxels;
            CurrentSession.sealOpenModels = tunnel.sealOpenModels;
            CurrentSession.lesCw = tunnel.lesCw;
            CurrentSession.convergenceTolerance = tunnel.convergenceTolerance;
            CurrentSession.reynoldsEffective = tunnel.Units.EffectiveReynolds;
            CurrentSession.reynoldsTarget = tunnel.Units.TargetReynolds;

            LastCompletedSession = CurrentSession;
            StatusLine = "Session complete";
            SessionCompleted?.Invoke(CurrentSession);
            CurrentSession = null;
        }

        void SaveVehicleTransform()
        {
            var t = tunnel.vehicle.transform;
            _savedVehiclePos = t.position;
            _savedVehicleRot = t.rotation;
            _transformSaved = true;
        }

        void RestoreVehicleTransform()
        {
            if (!_transformSaved || tunnel == null || tunnel.vehicle == null) return;
            var t = tunnel.vehicle.transform;
            t.position = _savedVehiclePos;
            t.rotation = _savedVehicleRot;
        }
    }
}
