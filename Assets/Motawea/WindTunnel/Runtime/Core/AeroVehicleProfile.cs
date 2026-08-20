using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// What kind of craft is under test. The class drives the whole test setup:
    /// which boundary condition the domain floor gets, whether wheels rotate, which
    /// reference area convention the coefficients use, how the body is placed in the
    /// tunnel, and which direction of lift counts as "better" when two runs are
    /// compared.
    /// </summary>
    public enum AeroVehicleClass
    {
        [Tooltip("Road car, SUV, van, truck. Ground plane on, wheels rotating, frontal reference area (SAE practice); less lift is better.")]
        RoadVehicle,
        [Tooltip("Race car. Same ground simulation as a road vehicle, but downforce (negative lift) is the objective.")]
        Motorsport,
        [Tooltip("Fixed-wing aircraft or multirotor drone. Free air (no ground plane), wing planform reference area, lift/drag ratio is the objective.")]
        Aircraft,
        [Tooltip("Boat or ship. The waterline becomes a plane in the tunnel — see Watercraft mode for what is actually simulated.")]
        Watercraft,
        [Tooltip("Bare validation shape (plate, cube, sphere, Ahmed body) or any body with no ground interaction. Free air, frontal area, no wheels.")]
        ReferenceBody
    }

    /// <summary>
    /// What a watercraft test actually solves. The solver is single-phase with no free
    /// surface, so neither mode produces wave-making resistance — the dominant term for
    /// a real planing hull. Both are honest about a different half of the problem.
    /// </summary>
    public enum WatercraftMode
    {
        [Tooltip("Wind loads on the part above the waterline, in AIR — how ship superstructures are actually measured in a wind tunnel. The waterline becomes the tunnel floor and the submerged hull is outside the domain.")]
        AboveWaterlineAir,
        [Tooltip("Hull resistance in WATER, deeply submerged (no free surface). Gives pressure + friction drag but NO wave-making resistance, so absolute numbers are far below towing-tank values.")]
        SubmergedHull
    }

    /// <summary>Which projected area the coefficients are divided by.</summary>
    public enum AeroReferenceAreaMode
    {
        [Tooltip("Pick the convention that matches the vehicle class: frontal silhouette for ground/marine bodies, wing planform for aircraft.")]
        Automatic,
        [Tooltip("Frontal silhouette projected along the wind axis (SAE J1594 automotive convention).")]
        FrontalSilhouette,
        [Tooltip("Planform area projected from above (aeronautical wing-area convention).")]
        Planform,
        [Tooltip("Use the reference area override value verbatim.")]
        Manual
    }

    /// <summary>Which way lift has to move for a design change to count as an improvement.</summary>
    public enum AeroLiftObjective
    {
        [Tooltip("Less lift is better (road-car stability, race-car downforce).")]
        LowerIsBetter,
        [Tooltip("More lift is better (aircraft).")]
        HigherIsBetter,
        [Tooltip("Lift is reported but never scored.")]
        Informational
    }

    /// <summary>How the body is seated inside the tunnel by the auto-fit.</summary>
    public enum AeroPlacement
    {
        [Tooltip("Wheel contact patches (or the lowest geometry) sit on the tunnel floor.")]
        SeatOnFloor,
        [Tooltip("The waterline sits on the tunnel floor; everything below it is outside the domain.")]
        WaterlineOnFloor,
        [Tooltip("The body is centred in the domain cross-section, free of any ground plane.")]
        CenterInDomain
    }

    /// <summary>
    /// The per-class rulebook the auto-fit and the class policy read. Margins are in
    /// body extents (a value of 1.5 upstream means "one and a half body lengths of
    /// clear air ahead of the nose"), following the usual external-aerodynamics domain
    /// sizing guidance, trimmed to what a uniform interactive grid can afford: the
    /// wake needs the room downstream far more than the inlet needs it upstream.
    /// </summary>
    public readonly struct AeroVehicleProfile
    {
        public readonly GroundSimulation Ground;
        public readonly bool RotatingWheels;
        public readonly AeroReferenceAreaMode AreaMode;
        public readonly AeroLiftObjective LiftObjective;
        public readonly AeroFluidMedium Medium;
        public readonly AeroPlacement Placement;

        /// <summary>Clear air ahead of the nose, in body lengths.</summary>
        public readonly float UpstreamLengths;
        /// <summary>Clear air behind the tail, in body lengths — this is where the wake lives.</summary>
        public readonly float DownstreamLengths;
        /// <summary>Clear air on each side, in body widths.</summary>
        public readonly float SideWidths;
        /// <summary>Clear air above, in body heights.</summary>
        public readonly float AboveHeights;
        /// <summary>Clear air below, in body heights. Zero for anything resting on a plane.</summary>
        public readonly float BelowHeights;

        /// <summary>
        /// Frontal area as a fraction of the bounding rectangle (width × height), used
        /// to predict blockage before the first voxelization has measured anything.
        /// Refined from the real measurement on the next fit.
        /// </summary>
        public readonly float FrontalFillFactor;

        AeroVehicleProfile(GroundSimulation ground, bool rotatingWheels, AeroReferenceAreaMode areaMode,
                           AeroLiftObjective liftObjective, AeroFluidMedium medium, AeroPlacement placement,
                           float up, float down, float side, float above, float below, float fill)
        {
            Ground = ground;
            RotatingWheels = rotatingWheels;
            AreaMode = areaMode;
            LiftObjective = liftObjective;
            Medium = medium;
            Placement = placement;
            UpstreamLengths = up;
            DownstreamLengths = down;
            SideWidths = side;
            AboveHeights = above;
            BelowHeights = below;
            FrontalFillFactor = fill;
        }

        public static AeroVehicleProfile For(AeroVehicleClass cls, WatercraftMode watercraft)
        {
            switch (cls)
            {
                case AeroVehicleClass.RoadVehicle:
                    // MovingBelt is the road-realistic ground simulation but diverged
                    // under the previous solver and is not re-verified — FixedFloor is
                    // the safe default the policy applies (see README limitations).
                    return new AeroVehicleProfile(GroundSimulation.FixedFloor, true,
                        AeroReferenceAreaMode.FrontalSilhouette, AeroLiftObjective.LowerIsBetter,
                        AeroFluidMedium.Air, AeroPlacement.SeatOnFloor,
                        1.5f, 3.5f, 2.0f, 2.5f, 0f, 0.80f);

                case AeroVehicleClass.Motorsport:
                    // 0.65 measured, not guessed: an open-wheel/GT silhouette fills more
                    // of its bounding rectangle than it looks like it should once wings,
                    // tyres and sidepods are counted (AMR23: 0.816 m² of a 1.16 m² box).
                    return new AeroVehicleProfile(GroundSimulation.FixedFloor, true,
                        AeroReferenceAreaMode.FrontalSilhouette, AeroLiftObjective.LowerIsBetter,
                        AeroFluidMedium.Air, AeroPlacement.SeatOnFloor,
                        1.5f, 4.0f, 2.0f, 2.5f, 0f, 0.65f);

                case AeroVehicleClass.Aircraft:
                    return new AeroVehicleProfile(GroundSimulation.OpenFloor, false,
                        AeroReferenceAreaMode.Planform, AeroLiftObjective.HigherIsBetter,
                        AeroFluidMedium.Air, AeroPlacement.CenterInDomain,
                        2.0f, 4.0f, 2.0f, 2.0f, 2.0f, 0.20f);

                case AeroVehicleClass.Watercraft:
                    return watercraft == WatercraftMode.SubmergedHull
                        // Deeply submerged: no free surface anywhere near the hull, so
                        // the domain is plain free stream in water.
                        ? new AeroVehicleProfile(GroundSimulation.OpenFloor, false,
                            AeroReferenceAreaMode.FrontalSilhouette, AeroLiftObjective.Informational,
                            AeroFluidMedium.FreshWater, AeroPlacement.CenterInDomain,
                            1.5f, 4.0f, 2.0f, 2.0f, 2.0f, 0.55f)
                        // Above-waterline: the water surface is a wall and the hull
                        // below it simply is not in the domain.
                        : new AeroVehicleProfile(GroundSimulation.FixedFloor, false,
                            AeroReferenceAreaMode.FrontalSilhouette, AeroLiftObjective.Informational,
                            AeroFluidMedium.Air, AeroPlacement.WaterlineOnFloor,
                            1.5f, 3.5f, 2.0f, 2.5f, 0f, 0.55f);

                default:
                    return new AeroVehicleProfile(GroundSimulation.OpenFloor, false,
                        AeroReferenceAreaMode.FrontalSilhouette, AeroLiftObjective.Informational,
                        AeroFluidMedium.Air, AeroPlacement.CenterInDomain,
                        2.0f, 4.0f, 2.5f, 2.5f, 2.5f, 0.95f);
            }
        }

        /// <summary>Human-readable name of the reference-area convention this profile uses.</summary>
        public static string AreaBasisLabel(AeroReferenceAreaMode mode) => mode switch
        {
            AeroReferenceAreaMode.Planform => "planform",
            AeroReferenceAreaMode.Manual => "manual override",
            _ => "frontal silhouette"
        };
    }
}
