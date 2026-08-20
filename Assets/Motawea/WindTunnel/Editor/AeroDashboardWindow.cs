using System.IO;
using Motawea.WindTunnel.UI;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Motawea.WindTunnel.Editor
{
    /// <summary>
    /// The Wind Tunnel control room: pick a tunnel, start/stop the solver (works in edit
    /// mode — this window is the edit-mode ticker), run the test queue, export reports.
    /// </summary>
    public class AeroDashboardWindow : EditorWindow
    {
        WindTunnelDomain _tunnel;
        AeroTestRunner _runner;
        FlowSlice _slice;
        SurfaceHeatmap _heatmap;
        FlowParticles[] _rakes = System.Array.Empty<FlowParticles>();
        AeroDashboardView _view;
        AeroVisualControlsView _visuals;
        AeroTestQueueView _queueView;
        SliceScannerRenderer _scanner;
        ObjectField _tunnelField;
        ObjectField _runnerField;
        double _lastRepaint;

        [MenuItem("Window/Wind Tunnel/Dashboard")]
        public static void Open()
        {
            var w = GetWindow<AeroDashboardWindow>();
            w.titleContent = new GUIContent("Wind Tunnel");
            w.minSize = new Vector2(420, 480);
        }

        void CreateGUI()
        {
            var root = rootVisualElement;

            var toolbar = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 6, marginLeft = 6, marginRight = 6 }
            };
            toolbar.Add(Btn("Free run", () => { AutoPick(); _tunnel?.StartSimulation(); }));
            toolbar.Add(Btn("Pause", () => _tunnel?.StopSimulation()));
            toolbar.Add(Btn("Resume", () => _tunnel?.ResumeSimulation()));
            toolbar.Add(Btn("Reset flow", () => _tunnel?.ResetFlow()));
            toolbar.Add(Btn("Stop & Clear", StopAndClear));
            toolbar.Add(Btn("Run all tests", () => { AutoPick(); _runner?.StartQueue(); }));
            toolbar.Add(Btn("Abort tests", () => _runner?.AbortQueue()));
            toolbar.Add(Btn("Fit tunnel", FitTunnel));
            toolbar.Add(Btn("Export report", ExportAll));
            toolbar.Add(Btn("Open reports", () => AeroSessionArchive.OpenDirectory(AeroSessionArchive.DefaultDirectory)));
            toolbar.Add(Btn("Compare results", AeroComparisonWindow.Open));
            toolbar.Add(Btn("Screenshot", CaptureScreenshot));
            root.Add(toolbar);

            var pickers = new VisualElement { style = { marginLeft = 6, marginRight = 6, marginTop = 4 } };
            _tunnelField = new ObjectField("Tunnel") { objectType = typeof(WindTunnelDomain), allowSceneObjects = true };
            _tunnelField.RegisterValueChangedCallback(e => _tunnel = e.newValue as WindTunnelDomain);
            pickers.Add(_tunnelField);
            _runnerField = new ObjectField("Test runner") { objectType = typeof(AeroTestRunner), allowSceneObjects = true };
            _runnerField.RegisterValueChangedCallback(e => _runner = e.newValue as AeroTestRunner);
            pickers.Add(_runnerField);
            root.Add(pickers);

            _view = new AeroDashboardView();
            var scroll = new ScrollView { style = { flexGrow = 1 } };
            scroll.Add(_view);

            // Test queue with per-test enable toggles (same as the runtime HUD).
            _queueView = new AeroTestQueueView
            {
                style = { paddingLeft = 8, paddingRight = 8 }
            };
            scroll.Add(_queueView);

            // Same tunnel/visual utilities as the runtime HUD.
            _visuals = new AeroVisualControlsView
            {
                style = { paddingLeft = 8, paddingRight = 8, paddingBottom = 8 }
            };
            scroll.Add(_visuals);
            root.Add(scroll);

            AutoPick();
        }

        static Button Btn(string text, System.Action action)
        {
            var b = new Button(() => action()) { text = text };
            b.style.marginRight = 2;
            return b;
        }

        void CaptureScreenshot()
        {
            AutoPick();
            _tunnel?.StopSimulation();
            string path = AeroScreenshot.Capture(_tunnel != null && _tunnel.vehicle != null ? _tunnel.vehicle.Name : null);
            // The PNG is written when the game view next renders a frame.
            EditorApplication.QueuePlayerLoopUpdate();
            Debug.Log($"Wind Tunnel: screenshot queued → {path}");

            double start = EditorApplication.timeSinceStartup;
            void RevealWhenWritten()
            {
                if (EditorApplication.timeSinceStartup - start < 0.5) return;
                EditorApplication.update -= RevealWhenWritten;
                EditorUtility.RevealInFinder(path);
            }
            EditorApplication.update += RevealWhenWritten;
        }

        void StopAndClear()
        {
            AutoPick();
            _runner?.AbortQueue();
            _tunnel?.StopSimulation();
            foreach (var rake in FindObjectsByType<FlowParticles>(FindObjectsSortMode.None))
                rake.Clear();
            SceneView.RepaintAll();
        }

        void AutoPick()
        {
            if (_tunnel == null)
            {
                _tunnel = FindFirstObjectByType<WindTunnelDomain>();
                if (_tunnelField != null) _tunnelField.SetValueWithoutNotify(_tunnel);
            }
            if (_runner == null)
            {
                _runner = FindFirstObjectByType<AeroTestRunner>();
                if (_runnerField != null) _runnerField.SetValueWithoutNotify(_runner);
            }
            if (_slice == null) _slice = FindFirstObjectByType<FlowSlice>();
            if (_heatmap == null) _heatmap = FindFirstObjectByType<SurfaceHeatmap>();
            if (_rakes.Length == 0) _rakes = FindObjectsByType<FlowParticles>(FindObjectsSortMode.None);
            _visuals?.Bind(_tunnel, _slice, _rakes, _heatmap);
            _queueView?.Bind(_runner);
        }

        void FitTunnel()
        {
            AutoPick();
            if (_tunnel == null || _tunnel.vehicle == null)
            {
                EditorUtility.DisplayDialog("Wind Tunnel", "Assign a tunnel with a vehicle before fitting.", "OK");
                return;
            }

            Undo.RecordObject(_tunnel, "Fit tunnel");
            Undo.RecordObject(_tunnel.transform, "Fit tunnel");
            Undo.RecordObject(_tunnel.vehicle.transform, "Fit tunnel");
            _tunnel.FitToVehicle();
            EditorUtility.SetDirty(_tunnel);
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Writes HTML (people), CSV (spreadsheets) and the JSON archive (the
        /// comparison tool) into one folder, so a session is never exported in a form
        /// that cannot be compared later.
        /// </summary>
        void ExportAll()
        {
            AutoPick();
            var session = _runner != null ? _runner.LastCompletedSession : null;
            if (session == null)
            {
                EditorUtility.DisplayDialog("Wind Tunnel", "No completed test session to export. Run the test queue first.", "OK");
                return;
            }

            string directory = EditorUtility.SaveFolderPanel("Export Wind Tunnel report into",
                AeroSessionArchive.DefaultDirectory, "");
            if (string.IsNullOrEmpty(directory)) directory = AeroSessionArchive.DefaultDirectory;

            string baseName = AeroReportExporter.ExportAll(session, directory);
            Debug.Log($"Wind Tunnel: exported {baseName}.html / .csv / {AeroReportExporter.JsonExtension} → {directory}");
            EditorUtility.RevealInFinder(Path.Combine(directory, baseName + ".html"));
        }

        void OnEnable() => EditorApplication.update += EditorTick;

        void OnDisable()
        {
            EditorApplication.update -= EditorTick;
            _scanner?.Dispose();
            _scanner = null;
        }

        // Edit-mode simulation driver. In play mode the components tick themselves.
        void EditorTick()
        {
            if (!Application.isPlaying)
            {
                if (_tunnel != null && _tunnel.IsRunning)
                {
                    _tunnel.Tick();
                    foreach (var rake in FindObjectsByType<FlowParticles>(FindObjectsSortMode.None))
                        if (rake.isActiveAndEnabled) rake.Tick();
                    EditorApplication.QueuePlayerLoopUpdate();
                    SceneView.RepaintAll();
                }
                _runner?.Tick();
            }

            if (EditorApplication.timeSinceStartup - _lastRepaint > 0.1)
            {
                _lastRepaint = EditorApplication.timeSinceStartup;
                _view?.Refresh(_tunnel, _runner);
                _queueView?.Refresh();
                UpdateScanner();
            }
        }

        void UpdateScanner()
        {
            if (_visuals == null) return;
            _scanner ??= new SliceScannerRenderer(384);
            if (_scanner.Render(_tunnel, _slice))
                _visuals.SetScannerTexture(_scanner.Texture);
        }
    }
}
