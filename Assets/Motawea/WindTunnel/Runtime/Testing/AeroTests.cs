using System;
using System.Collections.Generic;
using UnityEngine;

namespace Motawea.WindTunnel
{
    public enum AeroTestKind
    {
        [Tooltip("Run to convergence at one speed: Cd, CdA, lift balance, drag power.")]
        ConstantSpeedDrag,
        [Tooltip("Turntable yaw sweep: crosswind sensitivity, Cd(psi) and Cy(psi).")]
        YawSweep,
        [Tooltip("Vertical ride-height sweep: Cd(h) and Cl(h).")]
        RideHeightSweep,
        [Tooltip("Angle-of-attack (pitch) sweep: Cl(alpha), Cd(alpha) and lift/drag. The aircraft counterpart of the yaw sweep.")]
        AngleOfAttackSweep
    }

    /// <summary>One test procedure in the queue (SAE J1252-style workflow).</summary>
    [Serializable]
    public class AeroTestDefinition
    {
        [Tooltip("Disabled tests stay in the queue but are skipped by the runner.")]
        public bool enabled = true;
        public string testName = "Drag test";
        public AeroTestKind kind = AeroTestKind.ConstantSpeedDrag;

        [Tooltip("Freestream test speed in m/s.")]
        [Min(0.5f)] public float speedMs = 30f;

        [Header("Yaw sweep")]
        public float yawFromDeg = -15f;
        public float yawToDeg = 15f;
        [Range(2, 25)] public int yawPoints = 7;

        [Header("Ride-height sweep")]
        [Tooltip("Vehicle vertical offset range in meters, relative to its authored position.")]
        public float rideFromM = 0f;
        public float rideToM = 0.08f;
        [Range(2, 15)] public int ridePoints = 5;

        [Header("Angle-of-attack sweep")]
        [Tooltip("Nose-up is positive, in degrees.")]
        public float alphaFromDeg = -4f;
        public float alphaToDeg = 12f;
        [Range(2, 25)] public int alphaPoints = 9;

        [Header("Ground simulation")]
        public GroundSimulation ground = GroundSimulation.FixedFloor;
        public bool rotatingWheels = true;

        [Header("Averaging")]
        [Tooltip("Flow-through times averaged into each measurement point, as a wind tunnel averages a run. A bluff-body wake never stops oscillating, so a single reading is a point on that oscillation; the mean over a window is the measurement, and its spread is the uncertainty.")]
        [Range(1f, 10f)] public float averageOverFlowThroughs = 3f;

        [Header("Limits")]
        [Tooltip("Hard cap on solver steps per measurement point (safety against non-convergence). Must exceed the settling allowance plus the averaging window — roughly 4.5 flow-throughs at the default settings, which is ~22k steps at Ultra and ~29k at Extreme.")]
        public int maxStepsPerPoint = 48000;

        public IReadOnlyList<float> EnumeratePoints()
        {
            var points = new List<float>();
            switch (kind)
            {
                case AeroTestKind.ConstantSpeedDrag:
                    points.Add(0f);
                    break;
                case AeroTestKind.YawSweep:
                    for (int i = 0; i < yawPoints; i++)
                        points.Add(Mathf.Lerp(yawFromDeg, yawToDeg, yawPoints > 1 ? i / (yawPoints - 1f) : 0f));
                    break;
                case AeroTestKind.RideHeightSweep:
                    for (int i = 0; i < ridePoints; i++)
                        points.Add(Mathf.Lerp(rideFromM, rideToM, ridePoints > 1 ? i / (ridePoints - 1f) : 0f));
                    break;
                case AeroTestKind.AngleOfAttackSweep:
                    for (int i = 0; i < alphaPoints; i++)
                        points.Add(Mathf.Lerp(alphaFromDeg, alphaToDeg, alphaPoints > 1 ? i / (alphaPoints - 1f) : 0f));
                    break;
            }
            return points;
        }

        public string ParameterName => kind switch
        {
            AeroTestKind.YawSweep => "Yaw angle (deg)",
            AeroTestKind.RideHeightSweep => "Ride height offset (m)",
            AeroTestKind.AngleOfAttackSweep => "Angle of attack (deg)",
            _ => "-"
        };

        /// <summary>
        /// A standard queue for a vehicle class: the procedures an engineer would
        /// actually run on that kind of craft, at the ground simulation its class uses.
        /// </summary>
        public static List<AeroTestDefinition> StandardQueue(AeroVehicleClass cls, WatercraftMode watercraft,
                                                             float speedMs = 33.33f)
        {
            var profile = AeroVehicleProfile.For(cls, watercraft);
            var queue = new List<AeroTestDefinition>();

            AeroTestDefinition New(string name, AeroTestKind kind) => new AeroTestDefinition
            {
                testName = name,
                kind = kind,
                speedMs = speedMs,
                ground = profile.Ground,
                rotatingWheels = profile.RotatingWheels
            };

            switch (cls)
            {
                case AeroVehicleClass.RoadVehicle:
                case AeroVehicleClass.Motorsport:
                    queue.Add(New($"Drag test {speedMs * 3.6f:0} km/h", AeroTestKind.ConstantSpeedDrag));
                    queue.Add(New("Yaw sweep ±15°", AeroTestKind.YawSweep));
                    queue.Add(New("Ride-height sweep", AeroTestKind.RideHeightSweep));
                    break;
                case AeroVehicleClass.Aircraft:
                    queue.Add(New($"Cruise drag {speedMs * 3.6f:0} km/h", AeroTestKind.ConstantSpeedDrag));
                    queue.Add(New("Alpha sweep −4°…+12°", AeroTestKind.AngleOfAttackSweep));
                    queue.Add(New("Sideslip sweep ±15°", AeroTestKind.YawSweep));
                    break;
                default:
                    queue.Add(New($"Drag test {speedMs * 3.6f:0} km/h", AeroTestKind.ConstantSpeedDrag));
                    queue.Add(New("Yaw sweep ±15°", AeroTestKind.YawSweep));
                    break;
            }
            return queue;
        }
    }

    [Serializable]
    public class AeroTestPointResult
    {
        public float parameter;
        /// <summary>The measurement: mean of every force sample in the averaging window.</summary>
        public AeroSample sample;
        /// <summary>True when the mean settled inside its own uncertainty before the step cap.</summary>
        public bool converged;
        /// <summary>Coefficient of variation of Cd over the convergence window when this point was recorded.</summary>
        public float convergenceCv;
        /// <summary>
        /// Standard error of the mean Cd as a fraction — the uncertainty ON THE
        /// REPORTED NUMBER, which is what a delta against another run must beat.
        /// Divided by flow-throughs rather than samples, since samples within one
        /// flow-through are not independent.
        /// </summary>
        public float standardError = -1f;
        /// <summary>How many force samples went into the mean.</summary>
        public int samplesAveraged = 1;
        /// <summary>Flow-through times the average spans — the independent-sample count.</summary>
        public float flowThroughsAveraged;
        public long solverSteps;
    }

    [Serializable]
    public class AeroTestResult
    {
        public string testName;
        public AeroTestKind kind;
        public string parameterName;
        public float speedMs;
        public GroundSimulation ground;
        public bool rotatingWheels;
        public List<AeroTestPointResult> points = new List<AeroTestPointResult>();
    }

    /// <summary>
    /// A full run of the test queue, ready for report export — and, since it carries
    /// the whole test configuration, for a later like-for-like comparison against
    /// another session. Every field here is something two runs must agree on before
    /// their numbers may be differenced.
    /// </summary>
    [Serializable]
    public class AeroTestSession
    {
        /// <summary>Format marker for archived sessions; bump when fields change meaning.</summary>
        public string schemaVersion = CurrentSchema;
        public const string CurrentSchema = "windtunnel-session-1";

        public string packageVersion = "";

        /// <summary>
        /// False for sessions rebuilt from a CSV, where the grid, soft-voxel state and
        /// vehicle class were never recorded — a comparison must say so rather than
        /// silently assume the two runs used the same settings.
        /// </summary>
        public bool metadataComplete = true;

        public string vehicleName;
        public AeroVehicleClass vehicleClass = AeroVehicleClass.RoadVehicle;
        public WatercraftMode watercraftMode = WatercraftMode.AboveWaterlineAir;
        public string vehicleClassLabel = "";

        public string startedAtIso;
        public string finishedAtIso;

        // ---- fluid ----
        public AeroFluidMedium fluidMedium = AeroFluidMedium.Air;
        public float airDensity;
        public float airTemperatureC;
        public float kinematicViscosity;

        // ---- reference quantities ----
        /// <summary>Reference area the coefficients were divided by, m².</summary>
        public float frontalAreaM2;
        public AeroReferenceAreaMode referenceAreaMode = AeroReferenceAreaMode.FrontalSilhouette;
        public string referenceAreaBasis = "frontal silhouette";
        /// <summary>True when the runner held the area at its zero-yaw value for the session (SAE practice).</summary>
        public bool referenceAreaLocked;
        public float measuredFrontalAreaM2;
        public float measuredPlanformAreaM2;
        public float blockageRatio;

        // ---- grid / solver settings that make two runs comparable (or not) ----
        public string gridInfo;
        public Vector3 tunnelSizeM;
        public TunnelResolution resolutionTier = TunnelResolution.Medium;
        public float cellSizeM;
        public long cellCount;
        public bool softVoxels = true;
        public bool sealOpenModels = true;
        public float lesCw;
        public float convergenceTolerance;
        public float reynoldsEffective;
        public float reynoldsTarget;

        public List<AeroTestResult> tests = new List<AeroTestResult>();

        /// <summary>Short label for pickers: vehicle, class and when it ran.</summary>
        public string DisplayName =>
            $"{(string.IsNullOrEmpty(vehicleName) ? "(unnamed)" : vehicleName)} · {vehicleClass} · {startedAtIso}";
    }
}
