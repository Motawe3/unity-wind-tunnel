using UnityEngine;
using UnityEngine.UIElements;

namespace Motawea.WindTunnel.UI
{
    /// <summary>
    /// Compact single-test workflow, mirroring the runtime HUD: pick a test with
    /// prev/next (which aborts and resets the flow), Run test fills one progress bar
    /// across the whole procedure, and completion turns it green. Hosted by the
    /// editor dashboard.
    /// </summary>
    public class AeroTestQueueView : VisualElement
    {
        static readonly Color Accent = new Color(0.2f, 0.76f, 0.82f);
        static readonly Color Done = new Color(0.37f, 0.79f, 0.38f);
        static readonly Color TextDim = new Color(0.55f, 0.62f, 0.7f);
        static readonly Color TextMain = new Color(0.9f, 0.92f, 0.95f);
        static readonly Color BarBg = new Color(0.12f, 0.14f, 0.17f);

        AeroTestRunner _runner;
        int _selected;
        bool _sessionDone;
        bool _wasRunning;

        readonly Label _name;
        readonly Label _status;
        readonly VisualElement _fill;

        public AeroTestDefinition SelectedTest =>
            _runner != null && _runner.testQueue.Count > 0
                ? _runner.testQueue[Mathf.Clamp(_selected, 0, _runner.testQueue.Count - 1)]
                : null;

        public AeroTestQueueView()
        {
            var nav = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            nav.Add(NavButton("<", () => Move(-1), 26));
            _name = new Label("—")
            {
                style =
                {
                    fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold, color = TextMain,
                    flexGrow = 1, unityTextAlign = TextAnchor.MiddleCenter
                }
            };
            nav.Add(_name);
            nav.Add(NavButton(">", () => Move(1), 26));
            Add(nav);

            var bar = new VisualElement
            {
                style = { height = 3, backgroundColor = BarBg, marginTop = 3, marginBottom = 3 }
            };
            _fill = new VisualElement { style = { height = Length.Percent(100), width = Length.Percent(0), backgroundColor = Accent } };
            bar.Add(_fill);
            Add(bar);

            _status = new Label("")
            {
                style = { fontSize = 10, color = TextDim, marginBottom = 4, whiteSpace = WhiteSpace.Normal }
            };
            Add(_status);

            var run = NavButton("Run test", Run, 0);
            run.style.flexGrow = 1;
            Add(run);
        }

        static Button NavButton(string text, System.Action action, int width)
        {
            var b = new Button(() => action()) { text = text, focusable = false };
            if (width > 0) b.style.width = width;
            return b;
        }

        public void Bind(AeroTestRunner runner)
        {
            _runner = runner;
            _selected = 0;
            _sessionDone = false;
            Refresh();
        }

        void Move(int delta)
        {
            if (_runner == null || _runner.testQueue.Count == 0) return;
            int n = _runner.testQueue.Count;
            _selected = (_selected + delta % n + n) % n;

            // Clean slate on selection change.
            if (_runner.IsRunning) _runner.AbortQueue();
            if (_runner.tunnel != null) _runner.tunnel.ResetFlow();
            _sessionDone = false;
            Refresh();
        }

        void Run()
        {
            if (_runner == null || SelectedTest == null) return;
            if (_runner.IsRunning) _runner.AbortQueue();
            _sessionDone = false;
            _runner.StartSingle(SelectedTest);
        }

        public void Refresh()
        {
            var test = SelectedTest;
            if (test == null)
            {
                _name.text = "No tests queued";
                _status.text = "Add tests on the AeroTestRunner component.";
                _fill.style.width = Length.Percent(0);
                return;
            }

            bool running = _runner.IsRunning;
            // Detect session completion by the running->idle transition.
            if (_wasRunning && !running && _runner.LastCompletedSession != null)
                _sessionDone = true;
            _wasRunning = running;

            _name.text = $"{test.testName}  ({_selected + 1}/{_runner.testQueue.Count})";
            _name.style.color = running && _runner.CurrentTest == test ? Accent : TextMain;

            float pct = running ? _runner.SessionProgress01 * 100f : (_sessionDone ? 100f : 0f);
            _fill.style.width = Length.Percent(pct);
            _fill.style.backgroundColor = _sessionDone && !running ? Done : Accent;

            _status.text = running
                ? _runner.StatusLine
                : _sessionDone
                    ? "Test complete — report ready to export."
                    : $"{test.kind} · {test.speedMs * 3.6f:0} km/h · {test.ground}";
        }
    }
}
