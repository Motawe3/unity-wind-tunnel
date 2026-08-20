using UnityEngine;

namespace Motawea.WindTunnel.Samples
{
    /// <summary>
    /// Drives this transform in a continuous 360° loop around an assigned target,
    /// looking at it with an optional angular offset.
    ///
    /// Radius is the true distance to the pivot, elevation lifts the orbit ring off
    /// the pivot's horizontal plane, and the look offset is applied in view space
    /// (pitch/yaw/roll) so the subject can sit anywhere in frame.
    ///
    /// Editor-only tool: no input, no UI. Runs in play mode; with
    /// <see cref="applyInEditMode"/> it also parks the camera at
    /// <see cref="startAngle"/> so the shot can be framed without pressing Play.
    /// </summary>
    [AddComponentMenu("Wind Tunnel/Samples/Camera Orbit")]
    [ExecuteAlways]
    public class CameraOrbit : MonoBehaviour
    {
        [Tooltip("Transform to orbit around. Required.")]
        public Transform target;

        [Tooltip("Offset from the target's origin to the point actually orbited " +
                 "and looked at (e.g. lift it to the car's mid-height).")]
        public Vector3 pivotOffset = Vector3.zero;

        [Tooltip("Interpret Pivot Offset and the orbit plane in the target's local " +
                 "space, so the orbit follows the target's rotation. Off = world axes.")]
        public bool useTargetSpace = false;

        [Header("Orbit")]
        [Tooltip("Distance from the pivot, in metres.")]
        [Min(0.01f)] public float radius = 8f;

        [Tooltip("Degrees per second. Negative reverses the direction.")]
        public float degreesPerSecond = 20f;

        [Tooltip("Angle the loop starts at, measured around the up axis. " +
                 "0° places the camera on the pivot's -Z side, looking down +Z.")]
        [Range(-360f, 360f)] public float startAngle = 0f;

        [Tooltip("Degrees above (positive) or below (negative) the pivot's " +
                 "horizontal plane.")]
        [Range(-89f, 89f)] public float elevation = 12f;

        [Header("Look")]
        [Tooltip("Angular offset applied to the look direction in view space: " +
                 "X pitches the subject down/up in frame, Y yaws it left/right, " +
                 "Z rolls the camera.")]
        public Vector3 lookAngleOffset = Vector3.zero;

        [Tooltip("Aim at the pivot. Off leaves rotation alone so the camera can be " +
                 "aimed by hand (or by another script) while it still orbits.")]
        public bool lookAtPivot = true;

        [Header("Timing")]
        [Tooltip("Ignore Time.timeScale, so the orbit keeps moving while the " +
                 "simulation is paused.")]
        public bool useUnscaledTime = true;

        [Tooltip("Also position the camera in edit mode, at Start Angle.")]
        public bool applyInEditMode = true;

        /// <summary>Current angle around the up axis, degrees in [0, 360).</summary>
        public float Angle => _angle;

        float _angle;

        void OnEnable() => _angle = Mathf.Repeat(startAngle, 360f);

        void LateUpdate()
        {
            if (target == null) return;

            if (Application.isPlaying)
            {
                float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
                _angle = Mathf.Repeat(_angle + degreesPerSecond * dt, 360f);
            }
            else
            {
                if (!applyInEditMode) return;
                _angle = Mathf.Repeat(startAngle, 360f);
            }

            ApplyPose(_angle);
        }

        void OnValidate()
        {
            if (!Application.isPlaying)
                _angle = Mathf.Repeat(startAngle, 360f);
        }

        void ApplyPose(float angleDegrees)
        {
            GetBasis(out Vector3 pivot, out Quaternion basis);

            // Ring direction: swing the basis' -forward around up, then tilt by elevation.
            Quaternion swing = Quaternion.AngleAxis(angleDegrees, basis * Vector3.up);
            Vector3 right = swing * (basis * Vector3.right);
            Vector3 outward = swing * (basis * Vector3.back);
            Vector3 dir = Quaternion.AngleAxis(elevation, right) * outward;

            transform.position = pivot + dir * radius;

            if (!lookAtPivot) return;

            Vector3 up = basis * Vector3.up;
            Vector3 toPivot = pivot - transform.position;
            if (toPivot.sqrMagnitude < 1e-8f) return;

            // Offset is applied in view space so X/Y/Z read as pitch/yaw/roll.
            transform.rotation = Quaternion.LookRotation(toPivot, up)
                                 * Quaternion.Euler(lookAngleOffset);
        }

        void GetBasis(out Vector3 pivot, out Quaternion basis)
        {
            basis = useTargetSpace ? target.rotation : Quaternion.identity;
            pivot = target.position + basis * pivotOffset;
        }

        [ContextMenu("Set Start Angle From Current Position")]
        void SetStartAngleFromCurrentPosition()
        {
            if (target == null) return;

            GetBasis(out Vector3 pivot, out Quaternion basis);
            Vector3 local = Quaternion.Inverse(basis) * (transform.position - pivot);

            radius = Mathf.Max(0.01f, local.magnitude);
            elevation = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(local.y / radius, -1f, 1f))
                                    * Mathf.Rad2Deg, -89f, 89f);
            startAngle = Mathf.Repeat(Mathf.Atan2(local.x, -local.z) * Mathf.Rad2Deg, 360f);
            _angle = startAngle;
        }

        void OnDrawGizmosSelected()
        {
            if (target == null) return;

            GetBasis(out Vector3 pivot, out Quaternion basis);
            Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.8f);

            const int segments = 64;
            Vector3 previous = Vector3.zero;
            for (int i = 0; i <= segments; i++)
            {
                float a = i / (float)segments * 360f;
                Quaternion swing = Quaternion.AngleAxis(a, basis * Vector3.up);
                Vector3 right = swing * (basis * Vector3.right);
                Vector3 dir = Quaternion.AngleAxis(elevation, right) * (swing * (basis * Vector3.back));
                Vector3 point = pivot + dir * radius;

                if (i > 0) Gizmos.DrawLine(previous, point);
                previous = point;
            }

            Gizmos.DrawLine(transform.position, pivot);
        }
    }
}
