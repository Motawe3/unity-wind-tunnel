using System.Collections.Generic;
using UnityEngine;

namespace Motawea.WindTunnel
{
    public enum SurfaceHeatmapMode
    {
        PressureCoefficient,
        WallShear,
        SpeedRatio
    }

    /// <summary>
    /// Paints the solved flow field directly onto the vehicle's bodywork — the
    /// classic CFD surface plot (pressure coloring, wall-shear pattern). While
    /// enabled it caches every voxelized renderer's materials and swaps in one
    /// shared heatmap material whose shader samples the field just off the
    /// surface; disabling the component restores the original materials.
    ///
    /// The overlay only engages while the tunnel has a live solver: with no flow
    /// data the car simply keeps its normal materials. Renderers the flow never
    /// sees (under an <see cref="AeroIgnore"/>, or SkinnedMeshRenderers, which the
    /// voxelizer ignores) are never painted, so what is colored is exactly what
    /// was simulated.
    /// </summary>
    [AddComponentMenu("Wind Tunnel/Surface Heatmap")]
    [ExecuteAlways] // OnDisable must restore the car's materials in edit mode too
    public class SurfaceHeatmap : MonoBehaviour
    {
        public WindTunnelDomain tunnel;
        public SurfaceHeatmapMode mode = SurfaceHeatmapMode.PressureCoefficient;

        [Tooltip("Cp magnitude mapped to the ends of the diverging ramp (matches the slice plane's setting).")]
        [Range(0.2f, 3f)] public float cpRange = 1f;

        [Tooltip("Near-wall tangential speed ratio (u_t/U∞) at the hot end of the shear ramp.")]
        [Range(0.2f, 2f)] public float shearRange = 1.2f;

        [Tooltip("Distance off the surface where the field is sampled, in lattice cells. Below ~1.5 the sample sits inside the wall's partial cells; above ~3 fine pressure features smear.")]
        [Range(1f, 4f)] public float sampleOffsetCells = 1.75f;

        Material _material;
        AeroVehicle _painted;
        readonly Dictionary<Renderer, Material[]> _original = new Dictionary<Renderer, Material[]>();

        /// <summary>True while the car is showing the heatmap instead of its own materials.</summary>
        public bool IsPainting => _painted != null;

        void OnEnable()
        {
#if UNITY_EDITOR
            // Saving a scene while painted would serialize the HideAndDontSave
            // heatmap material into every renderer — a missing-material (pink) car
            // on the next load. Hand the originals back around the save; the next
            // LateUpdate re-paints.
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += OnSceneSaving;
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving -= OnSceneSaving;
#endif
            Restore();
            if (_material != null) DestroyImmediate(_material);
            _material = null;
        }

#if UNITY_EDITOR
        void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path) => Restore();
#endif

        void LateUpdate()
        {
            // Lazy everything: with [ExecuteAlways] and the dashboard's editor
            // ticker there is no guarantee OnEnable ran before the first tick.
            if (tunnel == null || tunnel.Solver == null || tunnel.vehicle == null)
            {
                Restore();
                return;
            }

            // Car swapped under us (CarSpawner) — release the old one first.
            if (_painted != null && _painted != tunnel.vehicle)
                Restore();

            if (_material == null)
            {
                var shader = Shader.Find("WindTunnel/SurfaceHeatmap");
                if (shader == null)
                {
                    Debug.LogError("Wind Tunnel: missing WindTunnel/SurfaceHeatmap shader.", this);
                    enabled = false;
                    return;
                }
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            if (_painted == null)
                Paint(tunnel.vehicle);

            var dims = tunnel.Dims;
            _material.SetTexture("_VelocityTex", tunnel.Solver.VelocityField);
            _material.SetTexture("_FluidMask", tunnel.Solver.FluidMask);
            _material.SetMatrix("_WorldToLattice", tunnel.WorldToLattice);
            _material.SetVector("_DimsF", new Vector3(dims.x, dims.y, dims.z));
            _material.SetFloat("_UInlet", tunnel.Units.ULattice);
            _material.SetInt("_Mode", (int)mode);
            _material.SetFloat("_CpRange", cpRange);
            _material.SetFloat("_ShearRange", shearRange);
            _material.SetFloat("_OffsetCells", sampleOffsetCells);
        }

        /// <summary>
        /// Swaps the heatmap material onto every renderer the voxelizer would see,
        /// caching the originals. Mirrors VehicleVoxelizer.CollectTriangles's
        /// traversal (active MeshFilters, AeroIgnore subtrees skipped) so the
        /// painted set matches the simulated geometry.
        /// </summary>
        void Paint(AeroVehicle vehicle)
        {
            foreach (var mf in vehicle.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                if (mf.GetComponentInParent<AeroIgnore>() != null) continue;
                if (!mf.TryGetComponent<MeshRenderer>(out var renderer)) continue;
                if (_original.ContainsKey(renderer)) continue;

                // sharedMaterials on purpose, both ways: .materials would instantiate
                // copies (and dirty the user's assets in edit mode).
                var originals = renderer.sharedMaterials;
                _original[renderer] = originals;

                var swap = new Material[originals.Length];
                for (int i = 0; i < swap.Length; i++)
                    swap[i] = _material;
                renderer.sharedMaterials = swap;
            }
            _painted = vehicle;
        }

        /// <summary>Hands every cached renderer its original materials back.</summary>
        void Restore()
        {
            if (_original.Count == 0)
            {
                _painted = null;
                return;
            }

            foreach (var kv in _original)
            {
                var renderer = kv.Key;
                if (renderer == null) continue; // car was despawned

                // If someone re-assigned materials while the heatmap was up, theirs
                // is the newer intent — restoring the cache would clobber it.
                var current = renderer.sharedMaterials;
                bool stillOurs = current.Length == kv.Value.Length;
                for (int i = 0; stillOurs && i < current.Length; i++)
                    stillOurs = current[i] == _material;
                if (stillOurs)
                    renderer.sharedMaterials = kv.Value;
            }
            _original.Clear();
            _painted = null;
        }
    }
}
