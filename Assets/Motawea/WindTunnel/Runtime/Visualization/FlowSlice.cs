using UnityEngine;

namespace Motawea.WindTunnel
{
    public enum FlowSliceMode
    {
        SpeedRatio,
        PressureCoefficient
    }

    /// <summary>
    /// Movable slice plane through the solved flow field, showing a velocity or
    /// pressure-coefficient heatmap — the standard CFD post-processing view. Position,
    /// rotate and scale this object like any quad; it clips to the tunnel volume.
    /// </summary>
    [AddComponentMenu("Wind Tunnel/Flow Slice Plane")]
    [ExecuteAlways]
    public class FlowSlice : MonoBehaviour
    {
        public WindTunnelDomain tunnel;
        public FlowSliceMode mode = FlowSliceMode.SpeedRatio;
        [Tooltip("Fades the in-world plane. At zero the plane stops drawing entirely; the UI scanner is unaffected.")]
        [Range(0f, 1f)] public float opacity = 0.85f;
        [Tooltip("Cp magnitude mapped to the ends of the diverging ramp.")]
        [Range(0.2f, 3f)] public float cpRange = 1f;

        Material _material;
        MeshRenderer _renderer;

        /// <summary>
        /// Normalized streamwise position of the plane inside the tunnel
        /// (0 = inlet, 1 = outlet). Lateral/vertical placement is preserved.
        /// </summary>
        public float StreamwisePosition01
        {
            get
            {
                if (tunnel == null) return 0.5f;
                float half = tunnel.size.x * 0.5f;
                float x = tunnel.transform.InverseTransformPoint(transform.position).x;
                return Mathf.InverseLerp(-half, half, x);
            }
            set
            {
                if (tunnel == null) return;
                float half = tunnel.size.x * 0.5f;
                Vector3 local = tunnel.transform.InverseTransformPoint(transform.position);
                local.x = Mathf.Lerp(-half, half, Mathf.Clamp01(value));
                transform.position = tunnel.transform.TransformPoint(local);
            }
        }

        void OnEnable()
        {
            var shader = Shader.Find("WindTunnel/FlowSlice");
            if (shader == null)
            {
                Debug.LogError("Wind Tunnel: missing WindTunnel/FlowSlice shader.", this);
                enabled = false;
                return;
            }
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            var mf = GetComponent<MeshFilter>();
            if (mf == null)
            {
                mf = gameObject.AddComponent<MeshFilter>();
                mf.sharedMesh = BuildQuad();
            }
            else if (mf.sharedMesh == null)
            {
                mf.sharedMesh = BuildQuad();
            }

            _renderer = GetComponent<MeshRenderer>();
            if (_renderer == null)
                _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
        }

        void OnDisable()
        {
            if (_material != null) DestroyImmediate(_material);
            _material = null;
        }

        void LateUpdate()
        {
            if (tunnel == null || tunnel.Solver == null || _material == null)
            {
                if (_renderer != null) _renderer.enabled = false;
                return;
            }

            // Fully faded out: drop the quad rather than blend an invisible one. The
            // scanner samples the field through this transform, so it keeps working.
            if (opacity <= 0.001f)
            {
                _renderer.enabled = false;
                return;
            }
            _renderer.enabled = true;

            var dims = tunnel.Dims;
            _material.SetTexture("_VelocityTex", tunnel.Solver.VelocityField);
            _material.SetMatrix("_WorldToLattice", tunnel.WorldToLattice);
            _material.SetVector("_DimsF", new Vector3(dims.x, dims.y, dims.z));
            _material.SetFloat("_UInlet", tunnel.Units.ULattice);
            _material.SetInt("_Mode", (int)mode);
            _material.SetFloat("_Opacity", opacity);
            _material.SetFloat("_CpRange", cpRange);
        }

        static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "Wind Tunnel Slice Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
            };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
