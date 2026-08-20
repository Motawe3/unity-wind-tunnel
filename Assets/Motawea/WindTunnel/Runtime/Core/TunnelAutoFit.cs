using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// Knobs for <see cref="TunnelAutoFit"/>. The defaults implement the sizing rules
    /// in <see cref="AeroVehicleProfile"/> at a blockage target tighter than the
    /// warning threshold, on a memory budget that suits a ~4 GB discrete GPU.
    /// </summary>
    [Serializable]
    public class AutoFitSettings
    {
        [Tooltip("Re-fit the tunnel automatically whenever the vehicle reference changes (a car swap) or the simulation starts with a vehicle it has not fitted yet.")]
        public bool fitAutomatically = true;

        [Tooltip("Apply the vehicle class policy on fit: ground boundary condition, wheel rotation, working fluid.")]
        public bool applyClassPolicy = true;

        [Tooltip("Move the vehicle to its correct station in the tunnel (upstream margin, laterally centred, seated on the floor / waterline). When off, the tunnel is built around the vehicle where it stands instead.")]
        public bool positionVehicle = true;

        [Tooltip("Also re-aim the smoke rakes and the slice plane at the fitted body.")]
        public bool fitVisualization = true;

        [Tooltip("Scales every clearance margin from the class profile. 1 = the recommended domain, below 1 = a tighter (cheaper, more wall-interference) domain, above 1 = more clear air.")]
        [Range(0.5f, 2f)] public float marginScale = 1f;

        [Tooltip("Cross-section is enlarged until the frontal area is at most this fraction of it. Wind-tunnel practice keeps blockage under ~7.5% uncorrected; 5% leaves headroom for a yaw sweep, which grows the silhouette.")]
        [Range(0.01f, 0.075f)] public float targetBlockage = 0.05f;

        [Tooltip("Pick the resolution tier automatically: the finest one that fits the memory budget below.")]
        public bool autoResolution = true;

        [Tooltip("Lock the cell size (metres) instead of the tier, choosing the streamwise cell count to match it. " +
                 "Two different vehicles fitted with the same value are solved at the same resolution, which is what " +
                 "makes a cross-vehicle A/B like-for-like — the tunnel length differs per vehicle, so a shared tier " +
                 "does NOT give a shared cell size. 0 = off (use tiers).")]
        [Min(0f)] public float matchCellSizeM;

        [Tooltip("Ceiling for the automatic tier. Extreme is a memory and frame-time cliff — raise this deliberately.")]
        public TunnelResolution maxAutoResolution = TunnelResolution.Ultra;

        [Tooltip("GPU memory the lattice may use, in GB (~190 bytes per cell). Leave headroom for the rest of the scene.")]
        [Range(0.25f, 8f)] public float memoryBudgetGB = 2f;

        [Tooltip("Pin the tunnel floor to this world height instead of to the vehicle's own contact plane, so the simulated floor coincides with the visible ground plane of the scene. Ignored for tunnels that are not level.")]
        public bool pinFloorToPlane = true;

        [Tooltip("World Y of the visible ground/water plane the tunnel floor is pinned to.")]
        public float floorPlaneY;

        [Tooltip("Warn when the fitted grid puts fewer than this many cells along the body length — the point where a uniform grid stops resolving shape differences.")]
        [Range(8, 128)] public int minCellsAcrossBody = 24;
    }

    /// <summary>Everything the auto-fit decided, before anything is applied.</summary>
    public struct AutoFitPlan
    {
        public bool valid;
        public string error;

        public Vector3 tunnelSize;
        public Vector3 tunnelCenter;          // world
        public TunnelResolution resolution;
        public Vector3Int dims;
        /// <summary>Exact streamwise cell count when a cell size was locked; 0 = use the tier.</summary>
        public int streamwiseCells;
        public float cellSizeM;
        public long cellCount;
        public float memoryGB;

        public Vector3 vehicleDelta;          // world translation to apply to the vehicle
        public Vector3 rakePosition;          // world, upstream of the body
        public Vector2 rakeSize;              // width (lateral), height
        public Vector3 slicePosition;         // world, at the body's mid-length

        public float bodyLengthM, bodyWidthM, bodyHeightM;
        public float cellsAcrossBody;
        public float estimatedFrontalAreaM2;
        public float estimatedBlockage;
        public GroundSimulation ground;
        public bool rotatingWheels;
        public AeroFluidMedium medium;
        public AeroPlacement placement;

        public List<string> notes;

        public string Summary()
        {
            var ic = CultureInfo.InvariantCulture;
            if (!valid) return "Auto-fit failed: " + error;
            return string.Format(ic,
                "tunnel {0:0.##}×{1:0.##}×{2:0.##} m · {3} ({4}×{5}×{6}, {7:0} mm cells, {8:0.0}M cells, {9:0.0} GB) · " +
                "{10:0} cells across the body · blockage ≈ {11:P1} · {12} · {13}",
                tunnelSize.x, tunnelSize.y, tunnelSize.z, resolution, dims.x, dims.y, dims.z,
                cellSizeM * 1000f, cellCount / 1e6f, memoryGB, cellsAcrossBody, estimatedBlockage,
                ground, medium);
        }
    }

    /// <summary>
    /// Sizes the tunnel around whatever vehicle is under test and seats the vehicle in
    /// it, following the class rulebook in <see cref="AeroVehicleProfile"/>.
    ///
    /// The trade-off this has to resolve: at a fixed resolution tier the streamwise
    /// cell count is constant, so a longer domain means coarser cells on the body. The
    /// domain extents therefore come from aerodynamic requirements (wake length,
    /// blockage) and the resolution tier is then chosen as fine as the memory budget
    /// allows, rather than the other way around.
    /// </summary>
    public static class TunnelAutoFit
    {
        /// <summary>Bytes per lattice cell: 19 FP16 distributions packed to 40 B ×2 buffers
        /// + fields (see DESIGN.md). Was 190 with FP32 lattice storage.</summary>
        public const float BytesPerCell = 118f;

        /// <summary>
        /// Floor on how far a re-fit may trim the class clearances when blockage has
        /// room to spare. Wall interference does not vanish just because the body is
        /// slender, so the walls never come closer than 60% of the recommended gap.
        /// </summary>
        public const float MinMarginFraction = 0.6f;

        public static AutoFitPlan Plan(AeroVehicle vehicle, Quaternion tunnelRotation,
                                       Vector3 currentTunnelCenter, TunnelResolution currentResolution,
                                       AutoFitSettings settings, float measuredFrontalAreaM2 = 0f)
        {
            var plan = new AutoFitPlan { notes = new List<string>() };
            settings ??= new AutoFitSettings();

            if (vehicle == null)
            {
                plan.error = "no vehicle assigned";
                return plan;
            }
            if (!vehicle.TryComputeAeroBounds(tunnelRotation, out Bounds body) ||
                body.size.x <= 0f || body.size.y <= 0f || body.size.z <= 0f)
            {
                plan.error = $"'{vehicle.Name}' has no readable MeshFilter geometry to measure";
                return plan;
            }

            var profile = vehicle.Profile;
            Quaternion inv = Quaternion.Inverse(tunnelRotation);
            float scale = Mathf.Max(settings.marginScale, 0.1f);

            // ---- the plane the body sits on, and its height above it ----------------
            float baseY;
            switch (profile.Placement)
            {
                case AeroPlacement.SeatOnFloor:
                    baseY = vehicle.ContactHeight(tunnelRotation, body);
                    break;
                case AeroPlacement.WaterlineOnFloor:
                    baseY = vehicle.WaterlineHeight(tunnelRotation, body);
                    break;
                default:
                    baseY = body.min.y;
                    break;
            }
            baseY = Mathf.Clamp(baseY, body.min.y, body.max.y - 1e-3f);

            float lengthM = body.size.x;
            float widthM = body.size.z;
            bool grounded = profile.Placement != AeroPlacement.CenterInDomain;
            // For a grounded body only the part above the plane is in the flow — the
            // submerged hull of a boat is outside the domain entirely.
            float heightM = grounded ? body.max.y - baseY : body.size.y;

            plan.bodyLengthM = lengthM;
            plan.bodyWidthM = widthM;
            plan.bodyHeightM = heightM;

            if (profile.Placement == AeroPlacement.WaterlineOnFloor && heightM < 0.25f * body.size.y)
                plan.notes.Add("waterline sits high on the hull — only " +
                               $"{heightM:0.##} m of {body.size.y:0.##} m is above it; set 'waterline from keel' if this is wrong");

            // ---- domain extents from the class margins ------------------------------
            float up = profile.UpstreamLengths * scale;
            float down = profile.DownstreamLengths * scale;
            float side = profile.SideWidths * scale;
            float above = profile.AboveHeights * scale;
            float below = grounded ? 0f : profile.BelowHeights * scale;

            float domLength = lengthM * (1f + up + down);
            float domWidth = widthM * (1f + 2f * side);
            float domHeight = heightM * (1f + above + below);

            // ---- blockage: enlarge the cross-section until the body is small in it ---
            float frontal = measuredFrontalAreaM2 > 0f
                ? measuredFrontalAreaM2
                : profile.FrontalFillFactor * widthM * heightM;
            plan.estimatedFrontalAreaM2 = frontal;

            float target = Mathf.Clamp(settings.targetBlockage, 0.005f, 0.5f);
            float requiredCross = frontal / target;
            float cross = domWidth * domHeight;
            if (cross < requiredCross && cross > 0f)
            {
                float grow = Mathf.Sqrt(requiredCross / cross);
                domWidth *= grow;
                domHeight *= grow;
                plan.notes.Add($"cross-section enlarged ×{grow:0.00} to hold blockage at {target:P1}");
            }
            else if (measuredFrontalAreaM2 > 0f && cross > requiredCross)
            {
                // Slender bodies (a wing, a hull) get a cross-section from the class
                // margins that is far larger than blockage requires, and every extra
                // cell of it is paid for in the memory budget — which is what forces a
                // coarser tier. Claw some back, but only against a *measured* frontal
                // area (an estimate that reads low would shrink the walls onto the
                // body), and never below MinMarginFraction of the class clearances.
                float floorWidth = widthM * (1f + 2f * side * MinMarginFraction);
                float floorHeight = heightM * (1f + (above + below) * MinMarginFraction);
                float shrink = Mathf.Sqrt(requiredCross / cross);
                float newWidth = Mathf.Max(domWidth * shrink, floorWidth);
                float newHeight = Mathf.Max(domHeight * shrink, floorHeight);
                if (newWidth < domWidth * 0.98f || newHeight < domHeight * 0.98f)
                {
                    plan.notes.Add($"cross-section trimmed to {newWidth / domWidth:0.00}×{newHeight / domHeight:0.00} " +
                                   "of the class clearances — blockage allows it, and the cells buy resolution");
                    domWidth = newWidth;
                    domHeight = newHeight;
                }
            }
            plan.estimatedBlockage = frontal / Mathf.Max(domWidth * domHeight, 1e-6f);

            plan.tunnelSize = new Vector3(domLength, domHeight, domWidth);

            // ---- resolution tier under the memory budget ----------------------------
            plan.resolution = ChooseResolution(plan.tunnelSize, currentResolution, settings, plan.notes,
                                               out plan.dims, out plan.cellSizeM,
                                               out plan.cellCount, out plan.memoryGB,
                                               out plan.streamwiseCells);
            plan.cellsAcrossBody = lengthM / Mathf.Max(plan.cellSizeM, 1e-6f);
            if (plan.cellsAcrossBody < settings.minCellsAcrossBody)
                plan.notes.Add($"only {plan.cellsAcrossBody:0} cells span the body " +
                               $"(target {settings.minCellsAcrossBody}) — raise the memory budget or shrink the margins");

            // The tunnel's grid rounds the two cross-stream axes to whole cells; plan
            // against the size the domain will actually have, so the margins the
            // vehicle is placed against are the real ones.
            var effective = new Vector3(plan.dims.x * plan.cellSizeM,
                                        plan.dims.y * plan.cellSizeM,
                                        plan.dims.z * plan.cellSizeM);

            // ---- where the box goes, and where the body goes inside it ---------------
            // "Level" means the tunnel's up axis is world up; only then does pinning
            // the floor to a world height have a meaning.
            bool level = Vector3.Angle(tunnelRotation * Vector3.up, Vector3.up) < 1f;
            Vector3 currentCenterF = inv * currentTunnelCenter;

            float centerX, centerZ;
            if (settings.positionVehicle)
            {
                centerX = currentCenterF.x;
                centerZ = currentCenterF.z;
            }
            else
            {
                centerX = body.min.x - up * lengthM + effective.x * 0.5f;
                centerZ = body.center.z;
            }

            // Pinning the floor to a world height only means anything if the vehicle is
            // free to move onto it; when the tunnel is built around a body that stays
            // put, the floor is that body's own contact (or waterline) plane.
            bool canPin = settings.pinFloorToPlane && level && settings.positionVehicle;
            float floorY;
            if (grounded)
            {
                floorY = canPin ? settings.floorPlaneY : baseY;
                if (settings.pinFloorToPlane && !level && settings.positionVehicle)
                    plan.notes.Add("tunnel is not level — floor pinned to the body's own contact plane instead of the world plane");
            }
            else
            {
                floorY = canPin ? settings.floorPlaneY : body.center.y - effective.y * 0.5f;
            }

            float centerY = floorY + effective.y * 0.5f;
            plan.tunnelCenter = tunnelRotation * new Vector3(centerX, centerY, centerZ);

            Vector3 deltaF = Vector3.zero;
            if (settings.positionVehicle)
            {
                float domainMinX = centerX - effective.x * 0.5f;
                deltaF.x = domainMinX + up * lengthM - body.min.x;
                deltaF.z = centerZ - body.center.z;
                deltaF.y = profile.Placement switch
                {
                    AeroPlacement.SeatOnFloor => floorY - baseY,
                    AeroPlacement.WaterlineOnFloor => floorY - baseY,
                    _ => centerY - body.center.y
                };
            }
            plan.vehicleDelta = tunnelRotation * deltaF;

            // ---- visualization aims -------------------------------------------------
            Vector3 bodyCenterF = body.center + deltaF;
            float bodyMinXF = body.min.x + deltaF.x;
            float bodyBaseYF = baseY + deltaF.y;
            float rakeX = Mathf.Lerp(centerX - effective.x * 0.5f, bodyMinXF, 0.35f);
            plan.rakeSize = new Vector2(widthM * 1.6f, heightM * 1.6f);
            // A grounded rake sits ON the floor: emitting tracers below it just wastes
            // half the smoke on particles that are born inside a wall.
            float rakeY = grounded
                ? bodyBaseYF + plan.rakeSize.y * 0.5f
                : bodyCenterF.y;
            plan.rakePosition = tunnelRotation * new Vector3(rakeX, rakeY, centerZ);
            plan.slicePosition = tunnelRotation * new Vector3(bodyCenterF.x, centerY, centerZ);

            plan.ground = profile.Ground;
            plan.rotatingWheels = profile.RotatingWheels && vehicle.Wheels.Count > 0;
            plan.medium = profile.Medium;
            plan.placement = profile.Placement;
            plan.valid = true;
            return plan;
        }

        /// <summary>
        /// Finest tier whose grid fits the memory budget and the domain's own cell
        /// guard. Tiers set the streamwise cell count; the other two axes follow from
        /// the aspect ratio, so a wide domain costs memory as fast as a fine one.
        /// </summary>
        static TunnelResolution ChooseResolution(Vector3 size, TunnelResolution current,
                                                 AutoFitSettings settings, List<string> notes,
                                                 out Vector3Int dims, out float cellSize,
                                                 out long cells, out float memoryGB,
                                                 out int streamwiseCells)
        {
            var tiers = (TunnelResolution[])Enum.GetValues(typeof(TunnelResolution));
            Array.Sort(tiers, (a, b) => ((int)b).CompareTo((int)a)); // finest first

            TunnelResolution chosen = tiers[tiers.Length - 1];
            bool found = false;
            streamwiseCells = 0;
            float budgetBytes = Mathf.Max(settings.memoryBudgetGB, 0.05f) * 1024f * 1024f * 1024f;

            // A locked cell size cannot come from the tiers: the domain length scales
            // with the vehicle, so the same tier gives every vehicle a different dx —
            // which is exactly what makes two auto-fitted runs incomparable. Solve for
            // the streamwise cell count instead; nothing in the solver requires it to
            // be one of the presets.
            if (settings.matchCellSizeM > 0f)
            {
                int nx = Mathf.Clamp(Mathf.RoundToInt(size.x / settings.matchCellSizeM), 32, 2048);
                var d = WindTunnelDomain.ComputeDims(size, current, nx);
                long c = (long)d.x * d.y * d.z;
                float achieved = size.x / nx;

                if (c > WindTunnelDomain.MaxCells)
                {
                    notes.Add($"a {settings.matchCellSizeM * 1000f:0.#} mm cell needs {c / 1e6f:0.0}M cells here, past the " +
                              $"{WindTunnelDomain.MaxCells / 1e6f:0}M guard — falling back to tiers");
                }
                else
                {
                    if (c * BytesPerCell > budgetBytes)
                        notes.Add($"a {settings.matchCellSizeM * 1000f:0.#} mm cell needs " +
                                  $"{c * BytesPerCell / (1024f * 1024f * 1024f):0.0} GB, over the " +
                                  $"{settings.memoryBudgetGB:0.##} GB budget — honoured anyway, because a locked cell " +
                                  "size is what makes runs comparable");
                    notes.Add($"cell size locked at {achieved * 1000f:0.#} mm ({nx} streamwise cells)");

                    streamwiseCells = nx;
                    dims = d;
                    cellSize = achieved;
                    cells = c;
                    memoryGB = c * BytesPerCell / (1024f * 1024f * 1024f);
                    return NearestTier(nx);
                }
            }

            if (!settings.autoResolution)
            {
                chosen = current;
                found = true;
                long kept = CellsAt(size, current);
                if (kept > WindTunnelDomain.MaxCells)
                    notes.Add($"{current} on this domain is {kept / 1e6f:0.0}M cells, past the " +
                              $"{WindTunnelDomain.MaxCells / 1e6f:0}M guard — the run will refuse to start");
                else if (kept * BytesPerCell > budgetBytes)
                    notes.Add($"{current} on this domain needs {kept * BytesPerCell / (1024f * 1024f * 1024f):0.0} GB, " +
                              $"over the {settings.memoryBudgetGB:0.##} GB budget (automatic tier selection is off)");
            }
            else
            {
                foreach (var tier in tiers)
                {
                    if ((int)tier > (int)settings.maxAutoResolution) continue;
                    long c = CellsAt(size, tier);
                    if (c > WindTunnelDomain.MaxCells) continue;
                    if (c * BytesPerCell > budgetBytes) continue;
                    chosen = tier;
                    found = true;
                    break;
                }
            }

            if (!found)
                notes.Add($"no tier fits {settings.memoryBudgetGB:0.##} GB — falling back to {chosen}; " +
                          "raise the budget or shrink the tunnel");

            streamwiseCells = 0;      // tier decides; no override
            dims = WindTunnelDomain.ComputeDims(size, chosen);
            cellSize = size.x / (int)chosen;
            cells = (long)dims.x * dims.y * dims.z;
            memoryGB = cells * BytesPerCell / (1024f * 1024f * 1024f);
            return chosen;
        }

        static long CellsAt(Vector3 size, TunnelResolution tier)
        {
            var d = WindTunnelDomain.ComputeDims(size, tier);
            return (long)d.x * d.y * d.z;
        }

        /// <summary>Tier label closest to a free cell count, for display only.</summary>
        static TunnelResolution NearestTier(int streamwiseCells)
        {
            TunnelResolution best = TunnelResolution.Coarse;
            int bestDistance = int.MaxValue;
            foreach (TunnelResolution tier in Enum.GetValues(typeof(TunnelResolution)))
            {
                int distance = Mathf.Abs((int)tier - streamwiseCells);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = tier;
            }
            return best;
        }
    }
}
