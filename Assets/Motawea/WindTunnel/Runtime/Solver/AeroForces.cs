using System;
using System.Collections.Generic;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>Cell classification. Must match Voxelize.compute / Lbm.compute.</summary>
    public enum AeroCellType : uint
    {
        Fluid = 0,
        Solid = 1,
        Ground = 2,
        Inlet = 3,
        Outlet = 4,
        FarField = 5,
        Wheel0 = 6
    }

    /// <summary>Raw force/moment sample from the solver, averaged over a step window (lattice units).</summary>
    public struct ForceSample
    {
        public Vector3 ForceLattice;
        public Vector3 MomentLattice;   // about the turntable pivot
        public int Steps;
        public long SolverStep;
    }

    /// <summary>
    /// One aerodynamic measurement in engineering terms. Axis convention:
    /// +X streamwise (drag), +Y up (lift), +Z lateral (side force), per SAE J670/ISO 8855
    /// wind axes adapted to Unity handedness.
    /// </summary>
    [Serializable]
    public struct AeroSample
    {
        public float cd;             // drag coefficient
        public float cl;             // total lift coefficient (positive = lift)
        public float clFront;        // front-axle lift coefficient
        public float clRear;         // rear-axle lift coefficient
        public float cy;             // side-force coefficient
        public float cdA;            // drag area, m²
        public float frontalAreaM2;  // reference area
        public float dragForceN;
        public float liftForceN;
        public float sideForceN;
        public float aeroPowerW;     // drag force × speed
        public float airSpeedMs;
        public float airDensity;
        public float reynoldsTarget;
        public float reynoldsEffective;
        public float blockageRatio;
        public bool liftSplitValid;  // false when no wheels were tagged
        public long solverStep;
        public float simulatedTimeS;

        public override string ToString()
            => $"Cd={cd:0.000} CdA={cdA:0.000} m² ClF={clFront:0.000} ClR={clRear:0.000} Cy={cy:0.000}";

        /// <summary>
        /// Mean of the last <paramref name="count"/> samples of a history — the
        /// measurement a wind tunnel actually reports. A bluff-body wake is unsteady by
        /// nature (this solver measures 5% run-to-run scatter on an SUV at every
        /// resolution), so a single instantaneous reading is a point on an oscillation,
        /// not a result. Reference quantities (areas, speed, Reynolds) are carried from
        /// the newest sample rather than averaged — they are constant across the window
        /// and averaging them would only add noise to an exact number.
        /// </summary>
        public static AeroSample Average(IReadOnlyList<AeroSample> history, int count)
        {
            if (history == null || history.Count == 0) return default;
            count = Mathf.Clamp(count, 1, history.Count);
            int start = history.Count - count;

            AeroSample mean = history[history.Count - 1];   // reference fields + step
            float cd = 0f, cl = 0f, clF = 0f, clR = 0f, cy = 0f, cdA = 0f;
            float drag = 0f, lift = 0f, side = 0f, power = 0f;

            for (int i = start; i < history.Count; i++)
            {
                var s = history[i];
                cd += s.cd; cl += s.cl; clF += s.clFront; clR += s.clRear; cy += s.cy; cdA += s.cdA;
                drag += s.dragForceN; lift += s.liftForceN; side += s.sideForceN; power += s.aeroPowerW;
            }

            float n = count;
            mean.cd = cd / n;
            mean.cl = cl / n;
            mean.clFront = clF / n;
            mean.clRear = clR / n;
            mean.cy = cy / n;
            mean.cdA = cdA / n;
            mean.dragForceN = drag / n;
            mean.liftForceN = lift / n;
            mean.sideForceN = side / n;
            mean.aeroPowerW = power / n;
            return mean;
        }

        /// <summary>
        /// Standard error of the mean Cd over the same window, as a fraction of |Cd|.
        ///
        /// The divider is the number of *flow-through times* spanned, not the number of
        /// samples: consecutive force samples inside one flow-through are strongly
        /// autocorrelated (they are the same eddies passing), so counting them as
        /// independent would understate the uncertainty several-fold. A wake decorrelates
        /// on roughly the flow-through timescale, which makes this the honest divider.
        /// </summary>
        public static float StandardErrorFraction(IReadOnlyList<AeroSample> history, int count,
                                                  float independentSamples)
        {
            if (history == null || history.Count == 0) return float.PositiveInfinity;
            count = Mathf.Clamp(count, 1, history.Count);
            if (count < 2) return float.PositiveInfinity;

            int start = history.Count - count;
            float mean = 0f;
            for (int i = start; i < history.Count; i++) mean += history[i].cd;
            mean /= count;

            float variance = 0f;
            for (int i = start; i < history.Count; i++)
            {
                float d = history[i].cd - mean;
                variance += d * d;
            }
            float std = Mathf.Sqrt(variance / count);

            float sem = std / Mathf.Sqrt(Mathf.Max(independentSamples, 1f));
            return Mathf.Abs(mean) > 1e-6f ? sem / Mathf.Abs(mean) : float.PositiveInfinity;
        }
    }

    public static class AeroForces
    {
        /// <summary>
        /// Converts a lattice force/moment sample into engineering coefficients.
        /// Axle stations and pivot are lattice-space X coordinates used to split total
        /// lift into front/rear axle contributions by static equivalence.
        /// </summary>
        public static AeroSample Compute(
            ForceSample sample,
            LatticeUnits units,
            AirProperties air,
            float referenceAreaM2,
            float measuredFrontalAreaM2,
            float tunnelCrossSectionM2,
            float frontAxleXLattice,
            float rearAxleXLattice,
            float pivotXLattice,
            bool liftSplitValid)
        {
            float qLat = units.DynamicPressureLattice;
            // Floor the reference area at one lattice cell. A degenerate silhouette
            // (a body thinner than the soft-voxel solid threshold measures zero) would
            // otherwise divide the force by ~nothing and report a Cd in the millions,
            // which reads as a plausible-looking number rather than an obvious failure.
            // WindTunnelDomain warns separately when the area is this small.
            float areaLat = Mathf.Max(referenceAreaM2 / (units.Dx * units.Dx), 1f);
            float qA = Mathf.Max(qLat * areaLat, 1e-9f);

            Vector3 f = sample.ForceLattice;
            Vector3 m = sample.MomentLattice;

            float cd = f.x / qA;
            float cl = f.y / qA;
            float cy = f.z / qA;

            // Static equivalence: vertical reactions at the axle stations that reproduce
            // total lift and the pitching moment (about +Z at the pivot).
            float clFront, clRear;
            float xf = frontAxleXLattice - pivotXLattice;
            float xr = rearAxleXLattice - pivotXLattice;
            if (liftSplitValid && Mathf.Abs(xf - xr) > 1e-3f)
            {
                float lf = (m.z - f.y * xr) / (xf - xr);
                float lr = f.y - lf;
                clFront = lf / qA;
                clRear = lr / qA;
            }
            else
            {
                clFront = clRear = 0.5f * cl;
                liftSplitValid = false;
            }

            float rho = air.Density;
            float u = units.VelocityToMs(units.ULattice);
            float qPhys = 0.5f * rho * u * u;

            return new AeroSample
            {
                cd = cd,
                cl = cl,
                clFront = clFront,
                clRear = clRear,
                cy = cy,
                cdA = cd * referenceAreaM2,
                frontalAreaM2 = referenceAreaM2,
                dragForceN = cd * qPhys * referenceAreaM2,
                liftForceN = cl * qPhys * referenceAreaM2,
                sideForceN = cy * qPhys * referenceAreaM2,
                aeroPowerW = cd * qPhys * referenceAreaM2 * u,
                airSpeedMs = u,
                airDensity = rho,
                reynoldsTarget = units.TargetReynolds,
                reynoldsEffective = units.EffectiveReynolds,
                blockageRatio = tunnelCrossSectionM2 > 0f
                    ? (measuredFrontalAreaM2 > 0f ? measuredFrontalAreaM2 : referenceAreaM2) / tunnelCrossSectionM2
                    : 0f,
                liftSplitValid = liftSplitValid,
                solverStep = sample.SolverStep,
                simulatedTimeS = sample.SolverStep * units.Dt
            };
        }
    }
}
