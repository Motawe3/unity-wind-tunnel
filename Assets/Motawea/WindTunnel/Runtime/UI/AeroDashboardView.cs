using UnityEngine;
using UnityEngine.UIElements;

namespace Motawea.WindTunnel.UI
{
    /// <summary>
    /// UI Toolkit dashboard: live coefficient readouts, convergence trace, run status.
    /// Hosted by the Wind Tunnel editor window and reusable inside a runtime UIDocument.
    /// Call <see cref="Refresh"/> periodically with the live objects.
    /// </summary>
    public class AeroDashboardView : VisualElement
    {
        readonly Label _status;
        readonly Label _testStatus;
        readonly ConvergenceChart _chart;

        readonly Label _cd, _cdA, _clF, _clR, _cy, _drag, _power, _area, _blockage, _re, _speed, _cv;

        public AeroDashboardView()
        {
            style.paddingLeft = style.paddingRight = style.paddingTop = style.paddingBottom = 8;
            style.flexGrow = 1;

            _status = AddSection("Tunnel");
            var grid = NewRow();
            _cd = AddMetric(grid, "Cd");
            _cdA = AddMetric(grid, "CdA (m²)");
            _clF = AddMetric(grid, "Cl front");
            _clR = AddMetric(grid, "Cl rear");
            Add(grid);

            var grid2 = NewRow();
            _cy = AddMetric(grid2, "Cy");
            _drag = AddMetric(grid2, "Drag (N)");
            _power = AddMetric(grid2, "Power (kW)");
            _speed = AddMetric(grid2, "Speed (km/h)");
            Add(grid2);

            var grid3 = NewRow();
            _area = AddMetric(grid3, "Frontal area (m²)");
            _blockage = AddMetric(grid3, "Blockage");
            _re = AddMetric(grid3, "Re (effective)");
            _cv = AddMetric(grid3, "Convergence CV");
            Add(grid3);

            AddSectionHeader("Cd convergence");
            _chart = new ConvergenceChart();
            Add(_chart);

            _testStatus = AddSection("Test queue");
        }

        Label AddSection(string title)
        {
            AddSectionHeader(title);
            var value = new Label("—")
            {
                style = { fontSize = 12, color = new Color(0.8f, 0.85f, 0.9f), whiteSpace = WhiteSpace.Normal }
            };
            Add(value);
            return value;
        }

        void AddSectionHeader(string title)
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

        static VisualElement NewRow() => new VisualElement
        {
            style = { flexDirection = FlexDirection.Row, marginBottom = 4 }
        };

        static Label AddMetric(VisualElement row, string caption)
        {
            var tile = new VisualElement
            {
                style =
                {
                    flexGrow = 1,
                    flexBasis = 0,
                    marginRight = 4,
                    paddingLeft = 8, paddingRight = 8, paddingTop = 6, paddingBottom = 6,
                    backgroundColor = new Color(0.13f, 0.15f, 0.18f),
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4
                }
            };
            tile.Add(new Label(caption)
            {
                style = { fontSize = 10, color = new Color(0.55f, 0.62f, 0.7f) }
            });
            var value = new Label("—")
            {
                style = { fontSize = 16, unityFontStyleAndWeight = FontStyle.Bold, color = Color.white }
            };
            tile.Add(value);
            row.Add(tile);
            return value;
        }

        public void Refresh(WindTunnelDomain tunnel, AeroTestRunner runner)
        {
            if (tunnel == null)
            {
                _status.text = "No WindTunnelDomain selected.";
                return;
            }

            var dims = tunnel.Dims;
            string state = tunnel.Solver == null ? "not initialized"
                : tunnel.IsRunning ? $"running — step {tunnel.Solver.StepCount:N0}"
                : "paused";
            string vehicle = tunnel.vehicle != null ? tunnel.vehicle.Name : "no vehicle";
            _status.text = $"{vehicle} · {state} · grid {dims.x}×{dims.y}×{dims.z} ({(long)dims.x * dims.y * dims.z / 1_000_000f:0.0}M cells) " +
                           $"· cell {tunnel.CellSize * 1000f:0} mm · {tunnel.ground}" +
                           (tunnel.IsConverged ? " · CONVERGED" : "");

            if (tunnel.HasSample)
            {
                var s = tunnel.LatestSample;
                _cd.text = s.cd.ToString("0.000");
                _cdA.text = s.cdA.ToString("0.000");
                _clF.text = s.liftSplitValid ? s.clFront.ToString("0.000") : $"{s.clFront:0.000}*";
                _clR.text = s.liftSplitValid ? s.clRear.ToString("0.000") : $"{s.clRear:0.000}*";
                _cy.text = s.cy.ToString("0.000");
                _drag.text = s.dragForceN.ToString("0.#");
                _power.text = (s.aeroPowerW / 1000f).ToString("0.##");
                _speed.text = (s.airSpeedMs * 3.6f).ToString("0.#");
                _area.text = s.frontalAreaM2.ToString("0.000");
                _blockage.text = s.blockageRatio.ToString("P1");
                _re.text = s.reynoldsEffective.ToString("0.##e0");
                _cv.text = float.IsInfinity(tunnel.ConvergenceCV) ? "—" : tunnel.ConvergenceCV.ToString("P2");
            }

            _chart.SetSeries(tunnel.SampleHistory);

            _testStatus.text = runner == null
                ? "No AeroTestRunner in scene."
                : runner.IsRunning
                    ? runner.StatusLine
                    : $"{runner.StatusLine} · {runner.testQueue.Count} test(s) queued" +
                      (runner.LastCompletedSession != null
                          ? $" · last session: {runner.LastCompletedSession.tests.Count} test(s) done"
                          : "");
        }
    }
}
