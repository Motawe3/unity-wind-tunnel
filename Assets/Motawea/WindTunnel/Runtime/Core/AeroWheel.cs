using UnityEngine;

namespace Motawea.WindTunnel
{
    /// <summary>
    /// Tags a wheel so the solver can (a) apply a rotating-wall boundary condition to its
    /// voxels when "rotating wheels" is enabled and (b) locate the axles for the
    /// front/rear lift split. Place on each wheel; the spin axis is the local X (right)
    /// axis, matching Unity's usual wheel orientation.
    /// </summary>
    [AddComponentMenu("Wind Tunnel/Aero Wheel")]
    public class AeroWheel : MonoBehaviour
    {
        [Tooltip("Tire outer radius in meters. Auto-measured from renderer bounds when 0.")]
        [Min(0f)] public float radius;

        [Tooltip("Tire width in meters. Auto-measured from renderer bounds when 0.")]
        [Min(0f)] public float width;

        public Vector3 Center => transform.position;

        /// <summary>World-space spin axis (local right).</summary>
        public Vector3 Axis => transform.right;

        public float EffectiveRadius
        {
            get
            {
                if (radius > 0f) return radius;
                var r = GetComponentInChildren<Renderer>();
                return r != null ? Mathf.Max(r.bounds.extents.y, 0.05f) : 0.3f;
            }
        }

        public float EffectiveWidth
        {
            get
            {
                if (width > 0f) return width;
                var r = GetComponentInChildren<Renderer>();
                return r != null ? Mathf.Max(r.bounds.size.x, 0.05f) : 0.25f;
            }
        }
    }
}
