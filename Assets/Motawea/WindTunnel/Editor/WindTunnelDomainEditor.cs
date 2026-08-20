using UnityEditor;
using UnityEngine;

namespace Motawea.WindTunnel.Editor
{
    [CustomEditor(typeof(WindTunnelDomain))]
    public class WindTunnelDomainEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var domain = (WindTunnelDomain)target;
            DrawDefaultInspector();

            EditorGUILayout.Space();

            Vector3Int d = WindTunnelDomain.ComputeDims(domain.size, domain.resolution);
            int nx = d.x, ny = d.y, nz = d.z;
            float dx = domain.size.x / nx;
            long cells = (long)nx * ny * nz;
            float memMB = cells * TunnelAutoFit.BytesPerCell / (1024f * 1024f);

            EditorGUILayout.HelpBox(
                $"Grid: {nx}×{ny}×{nz} = {cells / 1_000_000f:0.0}M cells · cell {dx * 1000f:0} mm · ~{memMB:0} MB VRAM\n" +
                $"Wind: {domain.inletSpeedMs:0.#} m/s ({domain.inletSpeedMs * 3.6f:0.#} km/h) along local +X — vehicle nose must face -X.",
                MessageType.Info);

            if (domain.vehicle == null)
                EditorGUILayout.HelpBox("Assign an AeroVehicle (GameObject > Wind Tunnel > Sample Test Vehicle to try one).", MessageType.Warning);

            using (new EditorGUI.DisabledScope(domain.vehicle == null))
            {
                if (GUILayout.Button("Fit tunnel to vehicle"))
                {
                    Undo.RecordObject(domain.transform, "Fit tunnel");
                    Undo.RecordObject(domain, "Fit tunnel");
                    if (domain.vehicle != null) Undo.RecordObject(domain.vehicle.transform, "Fit tunnel");
                    var plan = domain.FitToVehicle();
                    if (plan.valid) EditorUtility.SetDirty(domain);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Start / Restart")) domain.StartSimulation();
                if (GUILayout.Button("Pause")) domain.StopSimulation();
                if (GUILayout.Button("Reset flow")) domain.ResetFlow();
                if (GUILayout.Button("Re-voxelize")) domain.Revoxelize();
            }
            if (GUILayout.Button("Open dashboard")) AeroDashboardWindow.Open();

            if (domain.Solver != null && domain.HasSample)
            {
                var s = domain.LatestSample;
                EditorGUILayout.HelpBox(
                    $"Step {domain.Solver.StepCount:N0} · {s}\n" +
                    $"Blockage {s.blockageRatio:P1} · CV {domain.ConvergenceCV:P2}{(domain.IsConverged ? " (converged)" : "")}",
                    MessageType.None);
            }

            if (!Application.isPlaying && domain.IsRunning)
                EditorGUILayout.HelpBox("Edit-mode simulation is ticked while the Wind Tunnel Dashboard window is open.", MessageType.Info);
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected, typeof(WindTunnelDomain))]
        static void DrawTunnelGizmo(WindTunnelDomain domain, GizmoType type)
        {
            var t = domain.transform;
            bool selected = (type & GizmoType.Selected) != 0;

            Gizmos.matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            Gizmos.color = selected ? new Color(0.3f, 0.8f, 1f, 0.9f) : new Color(0.3f, 0.8f, 1f, 0.35f);
            Gizmos.DrawWireCube(Vector3.zero, domain.size);

            // Inlet plane + wind direction arrows (wind blows along local +X).
            Vector3 half = domain.size * 0.5f;
            Gizmos.color = selected ? new Color(0.4f, 1f, 0.5f, 0.9f) : new Color(0.4f, 1f, 0.5f, 0.4f);
            Vector3 inletCenter = new Vector3(-half.x, 0f, 0f);
            Gizmos.DrawWireCube(inletCenter, new Vector3(0.01f, domain.size.y, domain.size.z));

            float arrow = Mathf.Min(domain.size.x * 0.15f, 2f);
            for (int iy = -1; iy <= 1; iy += 2)
            for (int iz = -1; iz <= 1; iz += 2)
            {
                Vector3 p = new Vector3(-half.x, iy * half.y * 0.5f, iz * half.z * 0.5f);
                Gizmos.DrawLine(p, p + new Vector3(arrow, 0, 0));
                Gizmos.DrawLine(p + new Vector3(arrow, 0, 0), p + new Vector3(arrow * 0.7f, 0, arrow * 0.12f));
                Gizmos.DrawLine(p + new Vector3(arrow, 0, 0), p + new Vector3(arrow * 0.7f, 0, -arrow * 0.12f));
            }

            // Turntable circle on the floor.
            if (domain.vehicle != null)
            {
                Gizmos.matrix = Matrix4x4.identity;
                Gizmos.color = new Color(1f, 0.8f, 0.2f, selected ? 0.9f : 0.4f);
                Vector3 pivot = domain.vehicle.TurntablePivotPosition;
                Vector3 fwd = t.rotation * Vector3.right;
                Vector3 side = t.rotation * Vector3.forward;
                const int seg = 48;
                const float r = 2.5f;
                Vector3 prev = pivot + fwd * r;
                for (int i = 1; i <= seg; i++)
                {
                    float a = i / (float)seg * Mathf.PI * 2f;
                    Vector3 p = pivot + (fwd * Mathf.Cos(a) + side * Mathf.Sin(a)) * r;
                    Gizmos.DrawLine(prev, p);
                    prev = p;
                }
            }
        }
    }
}
