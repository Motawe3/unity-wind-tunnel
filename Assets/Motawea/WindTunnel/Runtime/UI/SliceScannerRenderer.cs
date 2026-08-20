using UnityEngine;

namespace Motawea.WindTunnel.UI
{
    /// <summary>
    /// Renders the flow-slice cross-section into a RenderTexture straight from the
    /// solver's 3D field (no camera). Shared by the runtime HUD and the editor
    /// dashboard so both show the same scanner view.
    /// </summary>
    public class SliceScannerRenderer : System.IDisposable
    {
        public RenderTexture Texture { get; private set; }
        Material _material;
        readonly int _resolution;

        public SliceScannerRenderer(int resolution = 512)
        {
            _resolution = Mathf.Max(8, resolution);

            var shader = Shader.Find("WindTunnel/FlowSliceImage");
            if (shader != null)
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            Texture = new RenderTexture(_resolution, _resolution, 0, RenderTextureFormat.ARGB32)
            {
                name = "Wind Tunnel Scanner"
            };
            Texture.Create();
        }

        /// <summary>
        /// Matches the texture's pixel aspect to the slice rectangle's world aspect, so the
        /// blit stays isotropic and consumers can display it without distortion. Resized in
        /// place: the RenderTexture reference is handed out to UI backgrounds and must survive.
        /// </summary>
        void MatchAspect(float aspect)
        {
            int w, h;
            if (aspect >= 1f) { w = _resolution; h = Mathf.Max(8, Mathf.RoundToInt(_resolution / aspect)); }
            else { h = _resolution; w = Mathf.Max(8, Mathf.RoundToInt(_resolution * aspect)); }

            if (Texture.width == w && Texture.height == h) return;

            Texture.Release();
            Texture.width = w;
            Texture.height = h;
            Texture.Create();
        }

        /// <summary>Updates the texture from the current slice pose and field. Returns false when inputs are missing.</summary>
        public bool Render(WindTunnelDomain tunnel, FlowSlice slice)
        {
            if (_material == null || Texture == null || slice == null ||
                tunnel == null || tunnel.Solver == null) return false;

            var t = slice.transform;
            Vector3 right = t.TransformVector(new Vector3(1f, 0f, 0f));
            Vector3 up = t.TransformVector(new Vector3(0f, 1f, 0f));
            Vector3 origin = t.position - 0.5f * (right + up);

            float rightLen = right.magnitude, upLen = up.magnitude;
            if (rightLen > 1e-5f && upLen > 1e-5f) MatchAspect(rightLen / upLen);

            var dims = tunnel.Dims;
            _material.SetTexture("_VelocityTex", tunnel.Solver.VelocityField);
            _material.SetMatrix("_WorldToLattice", tunnel.WorldToLattice);
            _material.SetVector("_DimsF", new Vector3(dims.x, dims.y, dims.z));
            _material.SetVector("_SliceOrigin", origin);
            _material.SetVector("_SliceRight", right);
            _material.SetVector("_SliceUp", up);
            _material.SetFloat("_UInlet", tunnel.Units.ULattice);
            _material.SetInt("_Mode", (int)slice.mode);
            _material.SetFloat("_CpRange", slice.cpRange);

            Graphics.Blit(null, Texture, _material);
            return true;
        }

        public void Dispose()
        {
            if (Texture != null)
            {
                Texture.Release();
                Object.DestroyImmediate(Texture);
                Texture = null;
            }
            if (_material != null)
            {
                Object.DestroyImmediate(_material);
                _material = null;
            }
        }
    }
}
