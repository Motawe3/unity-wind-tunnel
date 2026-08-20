using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace Motawea.WindTunnel.UI
{
    /// <summary>
    /// Runtime console controller. Attach next to a UIDocument whose source asset
    /// follows the SimulationScene.uxml element naming (telemetry tiles, scanner image,
    /// tunnel/tracer controls). References are auto-found in the scene when left empty.
    /// The scanner view is rendered straight from the solver's 3D field into a
    /// RenderTexture — no extra camera involved.
    /// </summary>
    [AddComponentMenu("Wind Tunnel/Aero Simulation HUD")]
    [RequireComponent(typeof(UIDocument))]
    public class WindTunnelHUD : MonoBehaviour
    {
        public WindTunnelDomain tunnel;
        public AeroTestRunner runner;
        public FlowSlice slice;
        [Tooltip("Vehicle-surface heatmap driven by the VEHICLE SURFACE controls. Auto-found; created on the tunnel object on first use when the scene has none.")]
        public SurfaceHeatmap heatmap;
        [Tooltip("Rakes driven by the tracer controls. Auto-filled with every rake in the scene when empty.")]
        public List<FlowParticles> rakes = new List<FlowParticles>();

        [Tooltip("Resolution of the scanner (slice image) render target.")]
        [Range(128, 1024)] public int scannerResolution = 512;

        [Tooltip("Debug stats readout (FPS) in small text top-center of the screen. " +
                 "Toggled at runtime with F2 (wired in the scene's camera/hotkey script).")]
        public bool debugStats;

        UIDocument _document;
        SliceScannerRenderer _scanner;

        Label _status, _cd, _cda, _clf, _clr, _cy, _drag, _power, _wind,
              _gridInfo, _cvText, _windValue, _legendMin, _legendMax, _testStatus, _hintText, _testName, _debugText,
              _vehicleName;
        VisualElement _led, _sliceImage, _testProgressFill, _debugBar;
        Button _btnPause, _modeSpeed, _modePressure;
        Button _heatOff, _heatPressure, _heatShear, _heatSpeed;
        Slider _heatCpRange, _heatShearRange;
        VisualElement _heatKey, _heatKeyGradient;
        Label _heatKeyTitle, _heatKeyMin, _heatKeyMax, _heatKeyDesc;
        Texture2D _keyPressureTex, _keySeqTex;
        readonly List<(Button button, AeroRampPreset preset)> _rampButtons = new List<(Button, AeroRampPreset)>();
        ConvergenceChart _chart;
        Slider _windSlider;
        DropdownField _resolutionField;
        Button _btnExport;
        AeroComparisonView _compare;
        float _nextRefresh;
        int _fpsFrames;
        float _fpsWindowStart, _fps;
        string _exportNote;
        int _selectedTest;
        bool _sessionDone;

        AeroTestDefinition SelectedTest =>
            runner != null && runner.testQueue.Count > 0
                ? runner.testQueue[Mathf.Clamp(_selectedTest, 0, runner.testQueue.Count - 1)]
                : null;

        void OnEnable()
        {
            _document = GetComponent<UIDocument>();
            AutoFind();
            if (runner != null) runner.SessionCompleted += OnSessionCompleted;
            _scanner = new SliceScannerRenderer(scannerResolution);
            _fpsWindowStart = Time.unscaledTime;
            _fpsFrames = 0;
            BindUI(_document.rootVisualElement);
        }

        void OnDisable()
        {
            if (runner != null) runner.SessionCompleted -= OnSessionCompleted;
            _scanner?.Dispose();
            _scanner = null;
            if (_keyPressureTex != null) Destroy(_keyPressureTex);
            if (_keySeqTex != null) Destroy(_keySeqTex);
            _keyPressureTex = null;
            _keySeqTex = null;
        }

        void OnSessionCompleted(AeroTestSession session) => _sessionDone = true;

        void AutoFind()
        {
            if (tunnel == null) tunnel = FindFirstObjectByType<WindTunnelDomain>();
            if (runner == null) runner = FindFirstObjectByType<AeroTestRunner>();
            if (slice == null) slice = FindFirstObjectByType<FlowSlice>();
            if (heatmap == null) heatmap = FindFirstObjectByType<SurfaceHeatmap>();
            if (rakes.Count == 0)
                rakes.AddRange(FindObjectsByType<FlowParticles>(FindObjectsSortMode.None));
        }

        // ------------------------------------------------------------------ binding

        void BindUI(VisualElement root)
        {
            _status = root.Q<Label>("status-text");
            _led = root.Q<VisualElement>("status-led");
            _cd = root.Q<Label>("m-cd");
            _cda = root.Q<Label>("m-cda");
            _clf = root.Q<Label>("m-clf");
            _clr = root.Q<Label>("m-clr");
            _cy = root.Q<Label>("m-cy");
            _drag = root.Q<Label>("m-drag");
            _power = root.Q<Label>("m-power");
            _wind = root.Q<Label>("m-wind");
            _gridInfo = root.Q<Label>("grid-info");
            _vehicleName = root.Q<Label>("vehicle-name");
            _cvText = root.Q<Label>("cv-text");
            _legendMin = root.Q<Label>("legend-min");
            _legendMax = root.Q<Label>("legend-max");
            _hintText = root.Q<Label>("hint-text");
            _debugBar = root.Q<VisualElement>("debug-bar");
            _debugText = root.Q<Label>("debug-text");
            ApplyDebugVisibility();

            var chartHost = root.Q<VisualElement>("chart-host");
            if (chartHost != null)
            {
                _chart = new ConvergenceChart { LineColor = new Color(0.2f, 0.76f, 0.82f) };
                chartHost.Add(_chart);
            }

            _sliceImage = root.Q<VisualElement>("slice-image");
            if (_sliceImage != null && _scanner?.Texture != null)
                _sliceImage.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(_scanner.Texture));

            // Tunnel controls.
            Wire<Button>(root, "btn-run", b => b.clicked += RunSelectedTest);
            _btnPause = root.Q<Button>("btn-pause");
            if (_btnPause != null) _btnPause.clicked += TogglePause;
            Wire<Button>(root, "btn-reset", b => b.clicked += () => tunnel?.ResetFlow());
            Wire<Button>(root, "btn-clear", b => b.clicked += () => rakes.ForEach(r => { if (r != null) r.Clear(); }));

            // Test pager + reports (same utilities as the editor dashboard).
            _testStatus = root.Q<Label>("test-status");
            _testName = root.Q<Label>("test-name");
            _testProgressFill = root.Q<VisualElement>("test-progress-fill");
            Wire<Button>(root, "btn-prev-test", b => b.clicked += () => MoveTestSelection(-1));
            Wire<Button>(root, "btn-next-test", b => b.clicked += () => MoveTestSelection(1));
            Wire<Button>(root, "btn-abort", b => b.clicked += () => runner?.AbortQueue());
            _btnExport = root.Q<Button>("btn-export");
            if (_btnExport != null) _btnExport.clicked += ExportReport;
            Wire<Button>(root, "btn-compare", b => b.clicked += OpenComparison);
            Wire<Button>(root, "btn-open-reports", b => b.clicked += OpenReportsFolder);
            UpdateTestPager();

            // Collapsible panels.
            WireCollapse(root, "panel-data", "collapse-data", "expand-data");
            WireCollapse(root, "panel-visuals", "collapse-visuals", "expand-visuals");

            _windSlider = root.Q<Slider>("wind-slider");
            _windValue = root.Q<Label>("wind-value");
            if (_windSlider != null)
            {
                if (tunnel != null) _windSlider.SetValueWithoutNotify(tunnel.inletSpeedMs * 3.6f);
                _windSlider.RegisterValueChangedCallback(e =>
                {
                    if (_windValue != null) _windValue.text = $"{e.newValue:0} KM/H";
                });
                // Apply on release: changing speed re-derives the unit mapping, so it
                // restarts the run rather than fighting the solver mid-drag.
                _windSlider.RegisterCallback<PointerCaptureOutEvent>(_ =>
                {
                    ApplyWind();
                    if (tunnel != null && tunnel.IsRunning) tunnel.StartSimulation();
                });
                if (_windValue != null && tunnel != null)
                    _windValue.text = $"{tunnel.inletSpeedMs * 3.6f:0} KM/H";
            }

            Wire<SliderInt>(root, "sim-steps", s =>
            {
                if (tunnel != null) s.SetValueWithoutNotify(tunnel.stepsPerTick);
                s.RegisterValueChangedCallback(e => { if (tunnel != null) tunnel.stepsPerTick = e.newValue; });
            });

            Wire<DropdownField>(root, "tunnel-res", d =>
            {
                _resolutionField = d;
                d.choices = new List<string>(System.Enum.GetNames(typeof(TunnelResolution)));
                if (tunnel != null) d.SetValueWithoutNotify(tunnel.resolution.ToString());
                d.RegisterValueChangedCallback(e =>
                {
                    if (tunnel == null) return;
                    if (!System.Enum.TryParse(e.newValue, out TunnelResolution res) || res == tunnel.resolution)
                        return;
                    tunnel.resolution = res;
                    // Picking a tier by hand is a decision, so the auto-fit stops
                    // choosing one — otherwise the next vehicle swap silently reverts
                    // it and this control lies about what is running.
                    if (tunnel.autoFit != null) tunnel.autoFit.autoResolution = false;
                    // The grid must rebuild around the new cell size: restart a live
                    // run; a stopped tunnel just picks it up on the next start.
                    if (tunnel.IsRunning) tunnel.StartSimulation();
                });
            });

            // Slice controls.
            _modeSpeed = root.Q<Button>("slice-mode-speed");
            _modePressure = root.Q<Button>("slice-mode-pressure");
            if (_modeSpeed != null) _modeSpeed.clicked += () => SetSliceMode(FlowSliceMode.SpeedRatio);
            if (_modePressure != null) _modePressure.clicked += () => SetSliceMode(FlowSliceMode.PressureCoefficient);
            Wire<Slider>(root, "slice-pos", s =>
            {
                if (slice != null) s.SetValueWithoutNotify(slice.StreamwisePosition01);
                s.RegisterValueChangedCallback(e => { if (slice != null) slice.StreamwisePosition01 = e.newValue; });
            });
            Wire<Slider>(root, "slice-opacity", s =>
            {
                if (slice != null) s.SetValueWithoutNotify(slice.opacity);
                s.RegisterValueChangedCallback(e => { if (slice != null) slice.opacity = e.newValue; });
            });

            // Vehicle-surface heatmap controls + bottom-center color key.
            _heatOff = root.Q<Button>("heat-off");
            _heatPressure = root.Q<Button>("heat-pressure");
            _heatShear = root.Q<Button>("heat-shear");
            _heatSpeed = root.Q<Button>("heat-speed");
            if (_heatOff != null) _heatOff.clicked += () => SetHeatmapMode(null);
            if (_heatPressure != null) _heatPressure.clicked += () => SetHeatmapMode(SurfaceHeatmapMode.PressureCoefficient);
            if (_heatShear != null) _heatShear.clicked += () => SetHeatmapMode(SurfaceHeatmapMode.WallShear);
            if (_heatSpeed != null) _heatSpeed.clicked += () => SetHeatmapMode(SurfaceHeatmapMode.SpeedRatio);
            _heatCpRange = root.Q<Slider>("heat-cp-range");
            _heatShearRange = root.Q<Slider>("heat-shear-range");
            if (_heatCpRange != null)
            {
                if (heatmap != null) _heatCpRange.SetValueWithoutNotify(heatmap.cpRange);
                _heatCpRange.RegisterValueChangedCallback(e => { if (heatmap != null) heatmap.cpRange = e.newValue; });
            }
            if (_heatShearRange != null)
            {
                if (heatmap != null) _heatShearRange.SetValueWithoutNotify(heatmap.shearRange);
                _heatShearRange.RegisterValueChangedCallback(e => { if (heatmap != null) heatmap.shearRange = e.newValue; });
            }
            _heatKey = root.Q<VisualElement>("heatmap-key");
            _heatKeyGradient = root.Q<VisualElement>("heatmap-key-gradient");
            _heatKeyTitle = root.Q<Label>("heatmap-key-title");
            _heatKeyMin = root.Q<Label>("heatmap-key-min");
            _heatKeyMax = root.Q<Label>("heatmap-key-max");
            _heatKeyDesc = root.Q<Label>("heatmap-key-desc");
            UpdateHeatmapKey();

            // Tracer controls. Count/trail rebuild the GPU buffer (destroying live
            // particles), so they apply on release, not per wheel-notch/drag-tick.
            Wire<SliderInt>(root, "p-count", s =>
            {
                if (rakes.Count > 0 && rakes[0] != null) s.SetValueWithoutNotify(rakes[0].particleCount);
                s.RegisterCallback<PointerCaptureOutEvent>(_ => ForEachRake(r => r.particleCount = s.value));
            });
            Wire<Slider>(root, "p-spacing", s =>
            {
                if (rakes.Count > 0 && rakes[0] != null) s.SetValueWithoutNotify(rakes[0].trailSpacingSteps);
                s.RegisterValueChangedCallback(e => ForEachRake(r => r.trailSpacingSteps = e.newValue));
            });
            Wire<Slider>(root, "p-size", s =>
            {
                if (rakes.Count > 0 && rakes[0] != null) s.SetValueWithoutNotify(rakes[0].particleSize);
                s.RegisterValueChangedCallback(e => ForEachRake(r => r.particleSize = e.newValue));
            });
            Wire<SliderInt>(root, "p-trail", s =>
            {
                if (rakes.Count > 0 && rakes[0] != null) s.SetValueWithoutNotify(rakes[0].trailSegments);
                s.RegisterCallback<PointerCaptureOutEvent>(_ => ForEachRake(r => r.trailSegments = s.value));
            });
            Wire<Slider>(root, "p-speed", s =>
            {
                if (rakes.Count > 0 && rakes[0] != null) s.SetValueWithoutNotify(rakes[0].playbackSpeed);
                s.RegisterValueChangedCallback(e => ForEachRake(r => r.playbackSpeed = e.newValue));
            });
            Wire<Slider>(root, "p-glow", s =>
            {
                if (rakes.Count > 0 && rakes[0] != null) s.SetValueWithoutNotify(rakes[0].intensity);
                s.RegisterValueChangedCallback(e => ForEachRake(r => r.intensity = e.newValue));
            });
            Wire<Slider>(root, "p-contrast", s =>
            {
                if (rakes.Count > 0 && rakes[0] != null) s.SetValueWithoutNotify(rakes[0].depthContrast);
                s.RegisterValueChangedCallback(e => ForEachRake(r => r.depthContrast = e.newValue));
            });

            _rampButtons.Clear();
            foreach (var preset in AeroRampPresets.All)
            {
                var btn = root.Q<Button>($"ramp-{preset.key}");
                if (btn == null) continue;
                var captured = preset;
                _rampButtons.Add((btn, captured));
                btn.clicked += () => ApplyRamp(captured);
            }

            SetSliceMode(slice != null ? slice.mode : FlowSliceMode.SpeedRatio);

            // No control may keep keyboard/wheel focus: a focused button re-fires on
            // Space (the run/pause hotkey), and a focused slider absorbs the scroll
            // wheel while the user dollies the camera — silently changing values like
            // particle COUNT, which rebuilds the buffer and wipes the smoke.
            root.Query<Button>().ForEach(b => b.focusable = false);
            root.Query<Slider>().ForEach(s => s.focusable = false);
            root.Query<SliderInt>().ForEach(s => s.focusable = false);
            root.Query<DropdownField>().ForEach(d => d.focusable = false);
        }

        void OnValidate() => ApplyDebugVisibility();

        /// <summary>Shows/hides the debug stats readout. Bound to F2 in FlyCamera.</summary>
        public void ToggleDebugStats()
        {
            debugStats = !debugStats;
            ApplyDebugVisibility();
        }

        void ApplyDebugVisibility()
        {
            if (_debugBar != null)
                _debugBar.style.display = debugStats ? DisplayStyle.Flex : DisplayStyle.None;
        }

        static void Wire<T>(VisualElement root, string name, System.Action<T> setup) where T : VisualElement
        {
            var el = root.Q<T>(name);
            if (el != null) setup(el);
        }

        void ForEachRake(System.Action<FlowParticles> action)
        {
            foreach (var r in rakes)
                if (r != null) action(r);
        }

        void ApplyWind()
        {
            if (tunnel != null && _windSlider != null)
                tunnel.inletSpeedMs = _windSlider.value / 3.6f;
        }

        void TogglePause()
        {
            if (tunnel == null) return;
            if (tunnel.IsRunning) tunnel.StopSimulation();
            else tunnel.ResumeSimulation();
        }

        void SetSliceMode(FlowSliceMode mode)
        {
            if (slice != null) slice.mode = mode;
            bool speed = mode == FlowSliceMode.SpeedRatio;
            _modeSpeed?.EnableInClassList("seg-active", speed);
            _modePressure?.EnableInClassList("seg-active", !speed);
            if (_legendMin != null) _legendMin.text = speed ? "0" : "−Cp";
            if (_legendMax != null) _legendMax.text = speed ? "1.6× V∞" : "+Cp";
        }

        void SetHeatmapMode(SurfaceHeatmapMode? mode)
        {
            if (mode == null)
            {
                if (heatmap != null) heatmap.enabled = false;
            }
            else
            {
                // The heatmap needs no scene setup: first use adds the component
                // to the tunnel object (same behavior as the editor dashboard).
                if (heatmap == null && tunnel != null)
                {
                    heatmap = tunnel.GetComponent<SurfaceHeatmap>();
                    if (heatmap == null) heatmap = tunnel.gameObject.AddComponent<SurfaceHeatmap>();
                }
                if (heatmap == null) return;
                heatmap.tunnel = tunnel;
                heatmap.mode = mode.Value;
                heatmap.enabled = true;
                if (_heatCpRange != null) heatmap.cpRange = _heatCpRange.value;
                if (_heatShearRange != null) heatmap.shearRange = _heatShearRange.value;
            }
            UpdateHeatmapKey();
        }

        /// <summary>
        /// Bottom-center color key + button highlights. Cheap, and re-run on every
        /// readout refresh: the heatmap can also be toggled from the editor
        /// dashboard, and the Pa scale follows the wind slider.
        /// </summary>
        void UpdateHeatmapKey()
        {
            bool on = heatmap != null && heatmap.enabled && heatmap.isActiveAndEnabled;
            _heatOff?.EnableInClassList("seg-active", !on);
            _heatPressure?.EnableInClassList("seg-active", on && heatmap.mode == SurfaceHeatmapMode.PressureCoefficient);
            _heatShear?.EnableInClassList("seg-active", on && heatmap.mode == SurfaceHeatmapMode.WallShear);
            _heatSpeed?.EnableInClassList("seg-active", on && heatmap.mode == SurfaceHeatmapMode.SpeedRatio);

            if (_heatKey == null) return;
            _heatKey.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (!on) return;

            switch (heatmap.mode)
            {
                case SurfaceHeatmapMode.PressureCoefficient:
                {
                    if (_keyPressureTex == null) _keyPressureTex = AeroRamps.BuildLegendTexture(AeroRampKind.Pressure);
                    SetKeyGradient(_keyPressureTex);
                    float q = tunnel != null
                        ? 0.5f * tunnel.air.Density * tunnel.inletSpeedMs * tunnel.inletSpeedMs
                        : 0f;
                    float pa = heatmap.cpRange * q;
                    if (_heatKeyTitle != null) _heatKeyTitle.text = "SURFACE PRESSURE";
                    if (_heatKeyMin != null) _heatKeyMin.text = q > 0f ? $"−{pa:0} PA" : $"CP −{heatmap.cpRange:0.0}";
                    if (_heatKeyMax != null) _heatKeyMax.text = q > 0f ? $"+{pa:0} PA" : $"CP +{heatmap.cpRange:0.0}";
                    if (_heatKeyDesc != null)
                        _heatKeyDesc.text = "BLUE SUCTION · GREEN ≈ FREESTREAM STATIC · RED COMPRESSION (STAGNATION)";
                    break;
                }
                case SurfaceHeatmapMode.WallShear:
                {
                    if (_keySeqTex == null) _keySeqTex = AeroRamps.BuildLegendTexture(AeroRampKind.Speed);
                    SetKeyGradient(_keySeqTex);
                    if (_heatKeyTitle != null) _heatKeyTitle.text = "WALL SHEAR (RELATIVE)";
                    if (_heatKeyMin != null) _heatKeyMin.text = "0";
                    if (_heatKeyMax != null) _heatKeyMax.text = $"{heatmap.shearRange:0.0}× V∞";
                    if (_heatKeyDesc != null)
                        _heatKeyDesc.text = "BLUE STALLED / SEPARATED FLOW · GREEN ATTACHED · RED FAST ATTACHED FLOW";
                    break;
                }
                default:
                {
                    if (_keySeqTex == null) _keySeqTex = AeroRamps.BuildLegendTexture(AeroRampKind.Speed);
                    SetKeyGradient(_keySeqTex);
                    if (_heatKeyTitle != null) _heatKeyTitle.text = "SURFACE SPEED";
                    if (_heatKeyMin != null) _heatKeyMin.text = "0";
                    if (_heatKeyMax != null) _heatKeyMax.text = "1.6× V∞";
                    if (_heatKeyDesc != null)
                        _heatKeyDesc.text = "BLUE STAGNANT AIR · GREEN ≈ FREESTREAM SPEED · RED ACCELERATED FLOW";
                    break;
                }
            }
        }

        void SetKeyGradient(Texture2D tex)
        {
            if (_heatKeyGradient != null)
                _heatKeyGradient.style.backgroundImage = new StyleBackground(tex);
        }

        /// <summary>RUN TEST: runs the selected test procedure; free-run wind when no tests exist.</summary>
        void RunSelectedTest()
        {
            _exportNote = null;
            _sessionDone = false;
            ApplyWind();

            if (runner != null && SelectedTest != null)
            {
                if (runner.IsRunning) runner.AbortQueue();
                runner.StartSingle(SelectedTest);
            }
            else
            {
                tunnel?.StartSimulation();
            }
        }

        void MoveTestSelection(int delta)
        {
            if (runner == null || runner.testQueue.Count == 0) return;
            int n = runner.testQueue.Count;
            _selectedTest = (_selectedTest + delta % n + n) % n;

            // Switching tests starts a clean slate: abort any run and reset the flow.
            if (runner.IsRunning) runner.AbortQueue();
            tunnel?.ResetFlow();
            _sessionDone = false;
            _exportNote = null;
            UpdateTestPager();
        }

        void UpdateTestPager()
        {
            if (_testName == null) return;
            var test = SelectedTest;
            if (test == null)
            {
                _testName.text = "NO TESTS QUEUED";
                return;
            }
            bool running = runner != null && runner.IsRunning && runner.CurrentTest == test;
            _testName.text = $"{test.testName}  ({_selectedTest + 1}/{runner.testQueue.Count})" +
                             (running ? "  · RUNNING" : "");
        }

        void WireCollapse(VisualElement root, string panelName, string collapseButton, string expandButton)
        {
            var panel = root.Q<VisualElement>(panelName);
            if (panel == null) return;
            Wire<Button>(root, collapseButton, b => b.clicked += () => panel.AddToClassList("panel-collapsed"));
            Wire<Button>(root, expandButton, b => b.clicked += () => panel.RemoveFromClassList("panel-collapsed"));
        }

        /// <summary>
        /// True when the given screen position (bottom-left origin, as reported by
        /// the input system) lies over a pickable HUD element. The scene's camera
        /// script uses this so a scroll over the panels drives the panel's scroll
        /// view instead of also dollying the camera.
        /// </summary>
        public bool IsPointerOverUI(Vector2 screenPosition)
        {
            var panel = _document != null ? _document.rootVisualElement?.panel : null;
            if (panel == null) return false;
            Vector2 panelPos = RuntimePanelUtils.ScreenToPanel(panel,
                new Vector2(screenPosition.x, Screen.height - screenPosition.y));
            return panel.Pick(panelPos) != null;
        }

        /// <summary>Pauses the run, captures the game view, then opens the folder.</summary>
        public void TakeScreenshot()
        {
            tunnel?.StopSimulation();
            string path = AeroScreenshot.Capture(tunnel != null && tunnel.vehicle != null ? tunnel.vehicle.Name : null);
            _exportNote = $"SCREENSHOT → {path}";
            StartCoroutine(RevealAfterWrite(path));
        }

        static IEnumerator RevealAfterWrite(string path)
        {
            // The PNG is written at end-of-frame; give it a moment before opening.
            yield return new WaitForSecondsRealtime(0.5f);
            AeroScreenshot.RevealFolder(path);
        }

        void ApplyRamp(in AeroRampPreset preset)
        {
            AeroRampPresets.Apply(rakes, preset);
            foreach (var (button, p) in _rampButtons)
                button.EnableInClassList("seg-active", p.key == preset.key);
        }

        void ExportReport()
        {
            var session = runner != null ? runner.LastCompletedSession : null;
            if (session == null)
            {
                _exportNote = "NO COMPLETED SESSION — RUN THE QUEUE FIRST";
                return;
            }

            string dir = AeroSessionArchive.DefaultDirectory;
            string baseName = AeroReportExporter.ExportAll(session, dir);
            _exportNote = $"SAVED {baseName}.html/.csv/.json → {dir}";
            _compare?.Reload();
        }

        /// <summary>Shows the folder exported reports are written to.</summary>
        public void OpenReportsFolder()
        {
            string dir = AeroSessionArchive.DefaultDirectory;
            AeroSessionArchive.OpenDirectory(dir);
            _exportNote = $"REPORTS FOLDER → {dir}";
        }

        /// <summary>True while the comparison modal is up — camera input stands down.</summary>
        public bool IsModalOpen => _compare != null && _compare.parent != null;

        /// <summary>Opens the result-comparison modal over the whole console.</summary>
        public void OpenComparison()
        {
            var root = _document != null ? _document.rootVisualElement : null;
            if (root == null) return;

            if (_compare == null)
            {
                _compare = new AeroComparisonView();
                _compare.Closed += CloseComparison;
                _compare.focusable = true;
                _compare.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.keyCode == KeyCode.Escape) CloseComparison();
                });
            }

            if (_compare.parent == null) root.Add(_compare);
            _compare.Reload();
            _compare.Focus();
        }

        public void CloseComparison()
        {
            if (_compare != null && _compare.parent != null)
                _compare.RemoveFromHierarchy();
        }

        // ------------------------------------------------------------------ refresh

        void Update()
        {
            RenderScanner();
            _fpsFrames++;

            if (Time.unscaledTime < _nextRefresh) return;

            // Frames per elapsed second over the refresh window, rather than a
            // single frame's delta — steadier to read while the solver hitches.
            float window = Time.unscaledTime - _fpsWindowStart;
            if (window > 0f) _fps = _fpsFrames / window;
            _fpsFrames = 0;
            _fpsWindowStart = Time.unscaledTime;

            _nextRefresh = Time.unscaledTime + 0.1f;
            RefreshReadouts();
        }

        void RenderScanner() => _scanner?.Render(tunnel, slice);

        void RefreshReadouts()
        {
            // Ahead of the tunnel check: frame rate is worth showing even with no tunnel bound.
            if (debugStats && _debugText != null) _debugText.text = $"FPS {Mathf.RoundToInt(_fps)}";

            if (tunnel == null)
            {
                if (_status != null) _status.text = "NO TUNNEL";
                return;
            }

            // The vehicle can be swapped underneath us, so read the name every refresh
            // rather than caching it at bind time.
            if (_vehicleName != null)
                _vehicleName.text = tunnel.vehicle != null
                    ? tunnel.vehicle.Name.ToUpperInvariant()
                    : "NO VEHICLE";

            bool running = tunnel.IsRunning && tunnel.Solver != null;
            if (_status != null)
                _status.text = tunnel.Solver == null ? "STANDBY"
                    : running ? (tunnel.IsConverged ? "CONVERGED" : "RUNNING")
                    : "PAUSED";
            _led?.EnableInClassList("led-running", running && !tunnel.IsConverged);
            _led?.EnableInClassList("led-converged", running && tunnel.IsConverged);
            if (_btnPause != null) _btnPause.text = running ? "PAUSE" : "RESUME";

            if (tunnel.HasSample)
            {
                var s = tunnel.LatestSample;
                Set(_cd, s.cd, "0.000");
                Set(_cda, s.cdA, "0.000");
                Set(_clf, s.clFront, "0.000");
                Set(_clr, s.clRear, "0.000");
                Set(_cy, s.cy, "0.000");
                Set(_drag, s.dragForceN, "0");
                Set(_power, s.aeroPowerW / 1000f, "0.0");
                Set(_wind, s.airSpeedMs * 3.6f, "0");
            }

            // The auto-fit can pick a tier of its own on a vehicle swap; show what is
            // actually running rather than the last thing anyone selected.
            if (_resolutionField != null && _resolutionField.value != tunnel.resolution.ToString())
                _resolutionField.SetValueWithoutNotify(tunnel.resolution.ToString());

            if (_gridInfo != null && tunnel.Solver != null)
            {
                var d = tunnel.Dims;
                _gridInfo.text = $"GRID {d.x}×{d.y}×{d.z} · CELL {tunnel.CellSize * 1000f:0} MM · " +
                                 $"BLOCKAGE {tunnel.BlockageRatio:P1} · RE {tunnel.Units.EffectiveReynolds:0.0e0} · " +
                                 $"STEP {tunnel.Solver.StepCount:N0}";
            }

            if (_cvText != null)
                _cvText.text = float.IsInfinity(tunnel.ConvergenceCV) ? "" : $"CV {tunnel.ConvergenceCV:P2}";

            if (_testStatus != null)
            {
                if (!string.IsNullOrEmpty(_exportNote))
                    _testStatus.text = _exportNote;
                else if (runner == null)
                    _testStatus.text = "NO TEST RUNNER IN SCENE";
                else if (runner.IsRunning)
                    _testStatus.text = runner.StatusLine.ToUpperInvariant();
                else if (_sessionDone)
                    _testStatus.text = "TEST COMPLETE — REPORT READY TO EXPORT";
                else
                    _testStatus.text = "PICK A TEST WITH < > AND PRESS RUN TEST";
            }
            _btnExport?.EnableInClassList("btn-attention", _sessionDone && string.IsNullOrEmpty(_exportNote));

            _chart?.SetSeries(tunnel.SampleHistory);
            UpdateTestPager();
            UpdateHeatmapKey();

            if (_testProgressFill != null)
            {
                bool testing = runner != null && runner.IsRunning;
                float pct = testing ? runner.SessionProgress01 * 100f : (_sessionDone ? 100f : 0f);
                _testProgressFill.style.width = Length.Percent(pct);
                _testProgressFill.EnableInClassList("test-progress-fill-done", _sessionDone && !testing);
            }

            if (_hintText != null)
            {
                bool locked = UnityEngine.Cursor.lockState == CursorLockMode.Locked;
                _hintText.text = locked
                    ? "MOUSE LOCKED — MMB UNLOCK · WASD/QE MOVE · SHIFT FAST · SPACE RUN/PAUSE · C SCREENSHOT"
                    : "MMB LOCK MOUSE · RMB HOLD FLY · SHIFT FAST · SPACE RUN/PAUSE · C SCREENSHOT";
                _hintText.EnableInClassList("hint-text-locked", locked);
            }
        }

        static void Set(Label label, float value, string format)
        {
            if (label != null) label.text = value.ToString(format);
        }
    }
}
