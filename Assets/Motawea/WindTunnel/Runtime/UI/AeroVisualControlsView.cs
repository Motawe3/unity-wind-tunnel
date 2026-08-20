using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Motawea.WindTunnel.UI
{
    /// <summary>
    /// Code-built control block matching the runtime HUD's tunnel/visual utilities:
    /// wind speed, slice scanner image + mode/opacity, tracer sliders, color ramps.
    /// Hosted by the editor dashboard window so editor and runtime expose the same
    /// controls. Call <see cref="Bind"/> whenever the scene references change.
    /// </summary>
    public class AeroVisualControlsView : VisualElement
    {
        static readonly Color Accent = new Color(0.2f, 0.76f, 0.82f);
        static readonly Color Dim = new Color(0.55f, 0.62f, 0.7f);

        WindTunnelDomain _tunnel;
        FlowSlice _slice;
        SurfaceHeatmap _heatmap;
        readonly List<FlowParticles> _rakes = new List<FlowParticles>();

        Slider _wind, _slicePos, _opacity, _size, _playback, _glow, _spacing, _contrast;
        Slider _heatCpRange, _heatShearRange;
        SliderInt _trail, _count, _simSteps;
        DropdownField _resolution;
        Label _windValue;
        Button _modeSpeed, _modePressure;
        Button _heatOff, _heatPressure, _heatShear, _heatSpeed;
        VisualElement _legendBar, _legendLabels;
        Label _legendMin, _legendMid, _legendMax;
        Texture2D _legendCpTex, _legendSeqTex;
        readonly List<(Button button, AeroRampPreset preset)> _rampButtons = new List<(Button, AeroRampPreset)>();
        VisualElement _scannerImage;

        public AeroVisualControlsView()
        {
            AddHeader("Wind");
            var windRow = Row();
            _wind = new Slider(30f, 250f) { value = 108f, style = { flexGrow = 1 } };
            _wind.RegisterValueChangedCallback(e => UpdateWindLabel(e.newValue));
            _wind.RegisterCallback<PointerCaptureOutEvent>(_ => ApplyWind());
            _windValue = new Label("108 km/h")
            {
                style = { minWidth = 64, unityTextAlign = TextAnchor.MiddleRight, color = Accent, unityFontStyleAndWeight = FontStyle.Bold }
            };
            windRow.Add(_wind);
            windRow.Add(_windValue);
            Add(windRow);

            _simSteps = new SliderInt("Sim speed", 1, 128) { value = 16 };
            StyleField(_simSteps);
            _simSteps.RegisterValueChangedCallback(e => { if (_tunnel != null) _tunnel.stepsPerTick = e.newValue; });
            Add(_simSteps);

            _resolution = new DropdownField("Resolution")
            {
                choices = new List<string>(System.Enum.GetNames(typeof(TunnelResolution)))
            };
            StyleField(_resolution);
            _resolution.RegisterValueChangedCallback(e =>
            {
                if (_tunnel == null) return;
                if (!System.Enum.TryParse(e.newValue, out TunnelResolution res) || res == _tunnel.resolution)
                    return;
                _tunnel.resolution = res;
                // Picking a tier by hand is a decision, so the auto-fit stops choosing
                // one — otherwise the next vehicle swap silently reverts it and the
                // control lies about what is running.
                if (_tunnel.autoFit != null) _tunnel.autoFit.autoResolution = false;
                // The grid must rebuild around the new cell size: restart a live run.
                if (_tunnel.IsRunning) _tunnel.StartSimulation();
            });
            Add(_resolution);

            AddHeader("Flow section — scanner");
            _scannerImage = new VisualElement
            {
                style =
                {
                    height = 220,
                    backgroundColor = new Color(0.05f, 0.06f, 0.08f),
                    backgroundSize = new BackgroundSize(BackgroundSizeType.Contain),
                    backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center),
                    backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center),
                    backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat),
                    borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1,
                    borderTopColor = new Color(0.16f, 0.19f, 0.24f), borderBottomColor = new Color(0.16f, 0.19f, 0.24f),
                    borderLeftColor = new Color(0.16f, 0.19f, 0.24f), borderRightColor = new Color(0.16f, 0.19f, 0.24f)
                }
            };
            Add(_scannerImage);

            var modeRow = Row();
            _modeSpeed = GhostButton("SPEED", () => SetSliceMode(FlowSliceMode.SpeedRatio));
            _modePressure = GhostButton("PRESSURE", () => SetSliceMode(FlowSliceMode.PressureCoefficient));
            modeRow.Add(_modeSpeed);
            modeRow.Add(_modePressure);
            Add(modeRow);

            _slicePos = LabeledSlider("Position", 0f, 1f, 0.5f,
                v => { if (_slice != null) _slice.StreamwisePosition01 = v; });
            _opacity = LabeledSlider("Plane opacity", 0f, 1f, 0.85f,
                v => { if (_slice != null) _slice.opacity = v; });

            AddHeader("Vehicle surface");
            var heatRow = Row();
            _heatOff = GhostButton("OFF", () => SetHeatmapMode(null));
            _heatPressure = GhostButton("PRESSURE", () => SetHeatmapMode(SurfaceHeatmapMode.PressureCoefficient));
            _heatShear = GhostButton("SHEAR", () => SetHeatmapMode(SurfaceHeatmapMode.WallShear));
            _heatSpeed = GhostButton("SPEED", () => SetHeatmapMode(SurfaceHeatmapMode.SpeedRatio));
            heatRow.Add(_heatOff);
            heatRow.Add(_heatPressure);
            heatRow.Add(_heatShear);
            heatRow.Add(_heatSpeed);
            Add(heatRow);

            _heatCpRange = LabeledSlider("Cp range", 0.2f, 3f, 1f,
                v => { if (_heatmap != null) _heatmap.cpRange = v; RefreshLegend(); });
            _heatShearRange = LabeledSlider("Shear range", 0.2f, 2f, 1.2f,
                v => { if (_heatmap != null) _heatmap.shearRange = v; RefreshLegend(); });

            _legendBar = new VisualElement
            {
                style =
                {
                    height = 10,
                    marginTop = 2,
                    backgroundSize = new BackgroundSize(Length.Percent(100), Length.Percent(100)),
                    borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1,
                    borderTopColor = new Color(0.16f, 0.19f, 0.24f), borderBottomColor = new Color(0.16f, 0.19f, 0.24f),
                    borderLeftColor = new Color(0.16f, 0.19f, 0.24f), borderRightColor = new Color(0.16f, 0.19f, 0.24f)
                }
            };
            Add(_legendBar);
            _legendLabels = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, justifyContent = Justify.SpaceBetween, marginBottom = 4 }
            };
            _legendMin = new Label { style = { color = Dim, fontSize = 10 } };
            _legendMid = new Label { style = { color = Dim, fontSize = 10 } };
            _legendMax = new Label { style = { color = Dim, fontSize = 10 } };
            _legendLabels.Add(_legendMin);
            _legendLabels.Add(_legendMid);
            _legendLabels.Add(_legendMax);
            Add(_legendLabels);
            RefreshHeatmapUI();

            AddHeader("Smoke & tracers");
            // Count/trail rebuild the particle buffer; apply on release only.
            _count = new SliderInt("Count", 4096, 262144) { value = 65536 };
            StyleField(_count);
            _count.RegisterCallback<PointerCaptureOutEvent>(_ => ForEachRake(r => r.particleCount = _count.value));
            Add(_count);
            _size = LabeledSlider("Size", 0.001f, 0.15f, 0.03f, v => ForEachRake(r => r.particleSize = v));
            _trail = new SliderInt("Trail", 1, 32) { value = 12 };
            StyleField(_trail);
            _trail.RegisterCallback<PointerCaptureOutEvent>(_ => ForEachRake(r => r.trailSegments = _trail.value));
            Add(_trail);
            _spacing = LabeledSlider("Trail gap", 2f, 64f, 16f, v => ForEachRake(r => r.trailSpacingSteps = v));
            _playback = LabeledSlider("Playback", 0.05f, 1f, 1f, v => ForEachRake(r => r.playbackSpeed = v));
            _glow = LabeledSlider("Intensity", 0.05f, 2f, 0.8f, v => ForEachRake(r => r.intensity = v));
            _contrast = LabeledSlider("Depth contrast", 0f, 1f, 0f, v => ForEachRake(r => r.depthContrast = v));

            var rampRow = Row();
            foreach (var preset in AeroRampPresets.All)
            {
                var captured = preset;
                var btn = GhostButton(preset.displayName, () => ApplyRamp(captured));
                _rampButtons.Add((btn, captured));
                rampRow.Add(btn);
            }
            Add(rampRow);

            // Focused sliders absorb the scroll wheel (silently changing values);
            // focused buttons re-fire on Space. Keep every control unfocusable.
            this.Query<Button>().ForEach(b => b.focusable = false);
            this.Query<Slider>().ForEach(s => s.focusable = false);
            this.Query<SliderInt>().ForEach(s => s.focusable = false);
            this.Query<DropdownField>().ForEach(d => d.focusable = false);
        }

        // ------------------------------------------------------------------ binding

        public void Bind(WindTunnelDomain tunnel, FlowSlice slice, IEnumerable<FlowParticles> rakes,
                         SurfaceHeatmap heatmap = null)
        {
            _tunnel = tunnel;
            _slice = slice;
            _heatmap = heatmap;
            _rakes.Clear();
            if (rakes != null)
                foreach (var r in rakes)
                    if (r != null) _rakes.Add(r);

            if (_tunnel != null)
            {
                _wind.SetValueWithoutNotify(_tunnel.inletSpeedMs * 3.6f);
                UpdateWindLabel(_wind.value);
                _simSteps.SetValueWithoutNotify(_tunnel.stepsPerTick);
                _resolution.SetValueWithoutNotify(_tunnel.resolution.ToString());
            }
            if (_slice != null)
            {
                _slicePos.SetValueWithoutNotify(_slice.StreamwisePosition01);
                _opacity.SetValueWithoutNotify(_slice.opacity);
                HighlightMode(_slice.mode);
            }
            if (_heatmap != null)
            {
                _heatCpRange.SetValueWithoutNotify(_heatmap.cpRange);
                _heatShearRange.SetValueWithoutNotify(_heatmap.shearRange);
            }
            RefreshHeatmapUI();
            if (_rakes.Count > 0)
            {
                var r = _rakes[0];
                _count.SetValueWithoutNotify(r.particleCount);
                _size.SetValueWithoutNotify(r.particleSize);
                _trail.SetValueWithoutNotify(r.trailSegments);
                _spacing.SetValueWithoutNotify(r.trailSpacingSteps);
                _playback.SetValueWithoutNotify(r.playbackSpeed);
                _glow.SetValueWithoutNotify(r.intensity);
                _contrast.SetValueWithoutNotify(r.depthContrast);
            }
        }

        public void SetScannerTexture(Texture texture)
        {
            if (texture is RenderTexture rt)
                _scannerImage.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(rt));
        }

        // ------------------------------------------------------------------ behavior

        void ApplyWind()
        {
            if (_tunnel == null) return;
            _tunnel.inletSpeedMs = _wind.value / 3.6f;
            // Changing speed re-derives the unit mapping: restart a live run.
            if (_tunnel.IsRunning) _tunnel.StartSimulation();
            RefreshLegend(); // the pressure legend's Pa scale depends on wind speed
        }

        void UpdateWindLabel(float kmh) => _windValue.text = $"{kmh:0} km/h";

        void SetSliceMode(FlowSliceMode mode)
        {
            if (_slice != null) _slice.mode = mode;
            HighlightMode(mode);
        }

        void HighlightMode(FlowSliceMode mode)
        {
            SetActive(_modeSpeed, mode == FlowSliceMode.SpeedRatio);
            SetActive(_modePressure, mode == FlowSliceMode.PressureCoefficient);
        }

        void SetHeatmapMode(SurfaceHeatmapMode? mode)
        {
            if (mode == null)
            {
                if (_heatmap != null) _heatmap.enabled = false;
            }
            else
            {
                EnsureHeatmap();
                if (_heatmap == null) return; // no tunnel to hang the component on
                _heatmap.tunnel = _tunnel;
                _heatmap.mode = mode.Value;
                _heatmap.enabled = true;
                _heatmap.cpRange = _heatCpRange.value;
                _heatmap.shearRange = _heatShearRange.value;
            }
            RefreshHeatmapUI();
        }

        // The heatmap needs no scene setup of its own, so first use just adds the
        // component to the tunnel object instead of asking the user to.
        void EnsureHeatmap()
        {
            if (_heatmap != null || _tunnel == null) return;
            _heatmap = _tunnel.GetComponent<SurfaceHeatmap>();
            if (_heatmap == null)
            {
                _heatmap = _tunnel.gameObject.AddComponent<SurfaceHeatmap>();
                _heatmap.tunnel = _tunnel;
            }
        }

        void RefreshHeatmapUI()
        {
            bool on = _heatmap != null && _heatmap.enabled;
            SetActive(_heatOff, !on);
            SetActive(_heatPressure, on && _heatmap.mode == SurfaceHeatmapMode.PressureCoefficient);
            SetActive(_heatShear, on && _heatmap.mode == SurfaceHeatmapMode.WallShear);
            SetActive(_heatSpeed, on && _heatmap.mode == SurfaceHeatmapMode.SpeedRatio);
            RefreshLegend();
        }

        void RefreshLegend()
        {
            bool on = _heatmap != null && _heatmap.enabled;
            var display = on ? DisplayStyle.Flex : DisplayStyle.None;
            _legendBar.style.display = display;
            _legendLabels.style.display = display;
            if (!on) return;

            switch (_heatmap.mode)
            {
                case SurfaceHeatmapMode.PressureCoefficient:
                    _legendCpTex ??= AeroRamps.BuildLegendTexture(AeroRampKind.Pressure);
                    _legendBar.style.backgroundImage = new StyleBackground(_legendCpTex);
                    // Cp → gauge pressure via the tunnel's dynamic pressure, like the
                    // scale bars on CFD surface plots.
                    float q = _tunnel != null
                        ? 0.5f * _tunnel.air.Density * _tunnel.inletSpeedMs * _tunnel.inletSpeedMs
                        : 0f;
                    float pa = _heatmap.cpRange * q;
                    _legendMin.text = q > 0f ? $"-{pa:0} Pa" : $"Cp -{_heatmap.cpRange:0.0}";
                    _legendMid.text = "0";
                    _legendMax.text = q > 0f ? $"+{pa:0} Pa" : $"Cp +{_heatmap.cpRange:0.0}";
                    break;
                case SurfaceHeatmapMode.WallShear:
                    _legendSeqTex ??= AeroRamps.BuildLegendTexture(AeroRampKind.Speed);
                    _legendBar.style.backgroundImage = new StyleBackground(_legendSeqTex);
                    _legendMin.text = "0";
                    _legendMid.text = "wall shear (relative)";
                    _legendMax.text = $"{_heatmap.shearRange:0.0} U∞";
                    break;
                default:
                    _legendSeqTex ??= AeroRamps.BuildLegendTexture(AeroRampKind.Speed);
                    _legendBar.style.backgroundImage = new StyleBackground(_legendSeqTex);
                    _legendMin.text = "0";
                    _legendMid.text = "speed / U∞";
                    _legendMax.text = "1.6";
                    break;
            }
        }

        void ApplyRamp(in AeroRampPreset preset)
        {
            AeroRampPresets.Apply(_rakes, preset);
            foreach (var (button, p) in _rampButtons)
                SetActive(button, p.key == preset.key);
        }

        void ForEachRake(System.Action<FlowParticles> action)
        {
            foreach (var r in _rakes)
                if (r != null) action(r);
        }

        // ------------------------------------------------------------------ building blocks

        void AddHeader(string title)
        {
            Add(new Label(title)
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    fontSize = 12,
                    marginTop = 10,
                    marginBottom = 4,
                    color = new Color(0.6f, 0.7f, 0.8f)
                }
            });
        }

        static VisualElement Row() => new VisualElement
        {
            style = { flexDirection = FlexDirection.Row, marginBottom = 4, alignItems = Align.Center }
        };

        Slider LabeledSlider(string label, float min, float max, float value, System.Action<float> onChange)
        {
            var slider = new Slider(label, min, max) { value = value };
            StyleField(slider);
            slider.RegisterValueChangedCallback(e => onChange(e.newValue));
            Add(slider);
            return slider;
        }

        static void StyleField(VisualElement field)
        {
            var label = field.Q<Label>(className: "unity-base-field__label");
            if (label != null)
            {
                label.style.minWidth = 96;
                label.style.color = Dim;
            }
        }

        static Button GhostButton(string text, System.Action onClick)
        {
            var b = new Button(() => onClick()) { text = text };
            b.style.flexGrow = 1;
            b.style.flexBasis = 0;
            return b;
        }

        static void SetActive(Button button, bool active)
        {
            button.style.borderTopColor = button.style.borderBottomColor =
                button.style.borderLeftColor = button.style.borderRightColor =
                    active ? Accent : new Color(0.25f, 0.28f, 0.33f);
            button.style.color = active ? Accent : new Color(0.78f, 0.82f, 0.86f);
        }
    }
}
