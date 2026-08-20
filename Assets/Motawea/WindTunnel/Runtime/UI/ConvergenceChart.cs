using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Motawea.WindTunnel.UI
{
    /// <summary>
    /// Lightweight UI Toolkit line chart for live Cd convergence monitoring
    /// (engineers judge a run by its convergence trace, not a single number).
    /// </summary>
    public class ConvergenceChart : VisualElement
    {
        readonly List<float> _values = new List<float>(512);
        Color _lineColor = new Color(0.25f, 0.55f, 1f);

        public Color LineColor
        {
            get => _lineColor;
            set { _lineColor = value; MarkDirtyRepaint(); }
        }

        public ConvergenceChart()
        {
            style.minHeight = 140;
            style.flexGrow = 1;
            style.backgroundColor = new Color(0.10f, 0.12f, 0.15f);
            style.borderTopLeftRadius = style.borderTopRightRadius =
                style.borderBottomLeftRadius = style.borderBottomRightRadius = 4;
            generateVisualContent += OnGenerateVisualContent;
        }

        public void SetSeries(IReadOnlyList<AeroSample> samples)
        {
            _values.Clear();
            if (samples != null)
            {
                int start = Mathf.Max(0, samples.Count - 512);
                for (int i = start; i < samples.Count; i++)
                    _values.Add(samples[i].cd);
            }
            MarkDirtyRepaint();
        }

        void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            var rect = contentRect;
            if (_values.Count < 2 || rect.width < 10 || rect.height < 10) return;

            float min = float.MaxValue, max = float.MinValue;
            foreach (float v in _values)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (Mathf.Approximately(min, max)) { min -= 0.01f; max += 0.01f; }
            float pad = (max - min) * 0.15f;
            min -= pad; max += pad;

            const float left = 8f, right = 8f, top = 8f, bottom = 8f;
            float w = rect.width - left - right;
            float h = rect.height - top - bottom;

            var painter = ctx.painter2D;
            painter.lineWidth = 1f;
            painter.strokeColor = new Color(1f, 1f, 1f, 0.12f);
            for (int g = 0; g <= 4; g++)
            {
                float y = top + h * g / 4f;
                painter.BeginPath();
                painter.MoveTo(new Vector2(left, y));
                painter.LineTo(new Vector2(left + w, y));
                painter.Stroke();
            }

            painter.lineWidth = 2f;
            painter.strokeColor = _lineColor;
            painter.BeginPath();
            for (int i = 0; i < _values.Count; i++)
            {
                float x = left + w * i / (_values.Count - 1);
                float y = top + h * (1f - (_values[i] - min) / (max - min));
                if (i == 0) painter.MoveTo(new Vector2(x, y));
                else painter.LineTo(new Vector2(x, y));
            }
            painter.Stroke();
        }
    }
}
