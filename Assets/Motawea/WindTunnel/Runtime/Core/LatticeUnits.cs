using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// Conversion between physical (SI) and lattice units for the LBM solver.
    ///
    /// The lattice velocity is pinned to a low Mach value (compressibility error in
    /// LBM grows with Ma²), which fixes the time step. The TRT collision operator
    /// stays stable with the molecular viscosity a few orders of magnitude below
    /// what BGK tolerated, so the stability floor sits low enough to reach the
    /// automotive Reynolds range; the WALE LES eddy viscosity models what the grid
    /// cannot resolve. <see cref="EffectiveReynolds"/> reports the Reynolds number
    /// the clamped molecular viscosity corresponds to.
    /// </summary>
    public readonly struct LatticeUnits
    {
        /// <summary>Cell size in meters.</summary>
        public readonly float Dx;

        /// <summary>Time step in seconds.</summary>
        public readonly float Dt;

        /// <summary>Freestream speed in lattice units (cells per step).</summary>
        public readonly float ULattice;

        /// <summary>Lattice kinematic viscosity actually used (after stability clamp).</summary>
        public readonly float NuLattice;

        /// <summary>Symmetric (viscous) TRT relaxation time τ⁺ = 3ν + 0.5, before LES augmentation.</summary>
        public readonly float Tau;

        /// <summary>Reynolds number requested from physical inputs.</summary>
        public readonly float TargetReynolds;

        /// <summary>Reynolds number the clamped viscosity actually resolves (LES models the rest).</summary>
        public readonly float EffectiveReynolds;

        public const float DefaultLatticeSpeed = 0.08f;

        /// <summary>
        /// Stability floor on the lattice viscosity. TRT (odd modes relaxed at ω⁻ = 1,
        /// see Lbm.compute) plus the WALE eddy viscosity is stable far below the old
        /// BGK floor of (0.505 − 0.5)/3 ≈ 1.7e-3; this value corresponds to τ⁺ ≈
        /// 0.5000045 and an effective Reynolds number in the millions at road-car
        /// scale. The clamp is a floor against τ⁺ collapsing onto the 0.5 cliff, not a
        /// magic-parameter constraint: ω⁻ is fixed, so it stays well-conditioned at any
        /// viscosity.
        /// </summary>
        public const float MinNuLattice = 1.5e-6f;

        public LatticeUnits(float cellSizeM, float freestreamMs, AirProperties air,
                            float referenceLengthM, float latticeSpeed = DefaultLatticeSpeed)
        {
            Dx = cellSizeM;
            ULattice = latticeSpeed;
            Dt = latticeSpeed * cellSizeM / Mathf.Max(freestreamMs, 0.01f);

            float nuPhys = air.KinematicViscosity;
            float nuLatTarget = nuPhys * Dt / (Dx * Dx);
            TargetReynolds = freestreamMs * referenceLengthM / nuPhys;

            NuLattice = Mathf.Max(nuLatTarget, MinNuLattice);
            Tau = 3f * NuLattice + 0.5f;

            float lLattice = referenceLengthM / cellSizeM;
            EffectiveReynolds = ULattice * lLattice / NuLattice;
        }

        /// <summary>Converts a lattice-units force to Newtons (mass scale ρ_phys·dx³, ρ_lattice = 1).</summary>
        public float ForceToNewtons(float forceLattice, float physicalDensity)
            => forceLattice * physicalDensity * Dx * Dx * Dx * Dx / (Dt * Dt);

        /// <summary>Converts a lattice velocity to m/s.</summary>
        public float VelocityToMs(float uLattice) => uLattice * Dx / Dt;

        /// <summary>Dynamic pressure q = ½ρu² in lattice units (ρ_lattice = 1).</summary>
        public float DynamicPressureLattice => 0.5f * ULattice * ULattice;
    }
}
