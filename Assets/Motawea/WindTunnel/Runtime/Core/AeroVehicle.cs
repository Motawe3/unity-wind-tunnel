using System.Collections.Generic;
using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// Marks the root of the test vehicle. All active MeshFilters underneath are
    /// voxelized into the tunnel. Also declares what kind of craft this is, which
    /// drives the tunnel auto-fit and the reference-area policy, and supplies the axle
    /// locations used for the front/rear lift split.
    /// </summary>
    [AddComponentMenu("Wind Tunnel/Aero Vehicle")]
    public class AeroVehicle : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("Name used in reports, exported file names and the UI. Leave empty to fall back to the GameObject name — which is usually the asset's file name (\"range-rover-sport-svr-2022\") rather than something you would put in front of a client.")]
        public string displayName;

        [Header("Classification")]
        [Tooltip("What kind of craft this is. Drives the ground boundary condition, wheel rotation, reference-area convention, how the auto-fit seats the body in the tunnel, and which direction of lift counts as an improvement.")]
        public AeroVehicleClass vehicleClass = AeroVehicleClass.RoadVehicle;

        [Tooltip("Watercraft only: air loads on the superstructure above the waterline, or resistance of the deeply submerged hull in water. Neither includes wave-making resistance — the solver has no free surface.")]
        public WatercraftMode watercraftMode = WatercraftMode.AboveWaterlineAir;

        [Tooltip("Watercraft only: height of the waterline above the lowest point of the hull, in meters. Leave 0 to use the waterline marker, or 25% of the hull height if there is no marker.")]
        [Min(0f)] public float waterlineFromKeelM;

        [Tooltip("Watercraft only: optional child transform marking the waterline plane. Its Y position is used directly.")]
        public Transform waterlineMarker;

        [Header("Reference area")]
        [Tooltip("Which projected area the coefficients are divided by. Automatic follows the vehicle class: frontal silhouette for ground and marine bodies, wing planform for aircraft.")]
        public AeroReferenceAreaMode referenceAreaMode = AeroReferenceAreaMode.Automatic;

        [Tooltip("Reference area in m². Used when the mode is Manual; also honoured when non-zero for backwards compatibility with scenes authored before the mode existed.")]
        [Min(0f)] public float referenceAreaOverride;

        [Header("Geometry")]
        [Tooltip("Optional explicit turntable pivot; defaults to the center of the vehicle bounds projected to the ground.")]
        public Transform turntablePivot;

        public List<AeroWheel> Wheels { get; } = new List<AeroWheel>();

        void OnEnable() => RefreshWheels();

        public void RefreshWheels()
        {
            Wheels.Clear();
            GetComponentsInChildren(true, Wheels);
        }

        /// <summary>
        /// What this vehicle is called anywhere a person will read it: reports, export
        /// file names, the comparison picker, the HUD. Falls back to the GameObject
        /// name so a vehicle that was never named still identifies itself.
        /// </summary>
        public string Name => string.IsNullOrWhiteSpace(displayName) ? gameObject.name : displayName.Trim();

        /// <summary>The per-class rulebook for this vehicle.</summary>
        public AeroVehicleProfile Profile => AeroVehicleProfile.For(vehicleClass, watercraftMode);

        /// <summary>
        /// The reference-area convention actually in force: an explicit mode wins, a
        /// non-zero override means Manual, otherwise the class decides.
        /// </summary>
        public AeroReferenceAreaMode EffectiveAreaMode
        {
            get
            {
                if (referenceAreaMode != AeroReferenceAreaMode.Automatic) return referenceAreaMode;
                if (referenceAreaOverride > 0f) return AeroReferenceAreaMode.Manual;
                return Profile.AreaMode;
            }
        }

        /// <summary>World-space bounds of all renderers under the vehicle root.</summary>
        public Bounds ComputeBounds()
        {
            var renderers = GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return new Bounds(transform.position, Vector3.one);

            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);
            return b;
        }

        /// <summary>
        /// Bounds of the geometry the voxelizer will actually see (active MeshFilters,
        /// AeroIgnore subtrees excluded), measured in a frame-aligned space: a world
        /// point p maps to <c>Quaternion.Inverse(frame) * p</c>. Pass the tunnel's
        /// rotation to get extents along the tunnel's streamwise/vertical/lateral axes
        /// instead of the world-axis AABB, which over-measures a rotated tunnel.
        /// </summary>
        public bool TryComputeAeroBounds(Quaternion frame, out Bounds bounds)
        {
            Quaternion inv = Quaternion.Inverse(frame);
            bool any = false;
            bounds = default;

            foreach (var mf in GetComponentsInChildren<MeshFilter>())
            {
                Mesh mesh = mf.sharedMesh;
                if (mesh == null) continue;
                if (mf.GetComponentInParent<AeroIgnore>() != null) continue;

                Bounds local = mesh.bounds;
                Matrix4x4 toWorld = mf.transform.localToWorldMatrix;
                Vector3 c = local.center, e = local.extents;

                for (int corner = 0; corner < 8; corner++)
                {
                    var offset = new Vector3(
                        (corner & 1) == 0 ? -e.x : e.x,
                        (corner & 2) == 0 ? -e.y : e.y,
                        (corner & 4) == 0 ? -e.z : e.z);
                    Vector3 p = inv * toWorld.MultiplyPoint3x4(c + offset);
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            return any;
        }

        /// <summary>
        /// Height (in the given frame) of the plane the vehicle rests on: the lowest
        /// point of the tagged wheels when there are any — a tyre's contact patch, not
        /// the bodywork underside — otherwise the lowest geometry.
        /// </summary>
        public float ContactHeight(Quaternion frame, in Bounds aeroBounds)
        {
            Quaternion inv = Quaternion.Inverse(frame);
            RefreshWheels();

            float lowest = float.MaxValue;
            foreach (var wheel in Wheels)
            {
                if (wheel == null || !wheel.isActiveAndEnabled) continue;
                float y = (inv * wheel.Center).y - wheel.EffectiveRadius;
                lowest = Mathf.Min(lowest, y);
            }
            return lowest < float.MaxValue ? lowest : aeroBounds.min.y;
        }

        /// <summary>
        /// Height (in the given frame) of the waterline plane: the marker if one is
        /// assigned, else the keel offset, else a quarter of the hull height — a
        /// reasonable loaded draft for a planing hull, and the value to override when
        /// the real draft is known.
        /// </summary>
        public float WaterlineHeight(Quaternion frame, in Bounds aeroBounds)
        {
            if (waterlineMarker != null)
                return (Quaternion.Inverse(frame) * waterlineMarker.position).y;
            if (waterlineFromKeelM > 0f)
                return aeroBounds.min.y + waterlineFromKeelM;
            return aeroBounds.min.y + 0.25f * aeroBounds.size.y;
        }

        public Vector3 TurntablePivotPosition
        {
            get
            {
                if (turntablePivot != null) return turntablePivot.position;
                var b = ComputeBounds();
                return new Vector3(b.center.x, b.min.y, b.center.z);
            }
        }
    }
}
