using Motawea.WindTunnel.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Motawea.WindTunnel.Editor
{
    /// <summary>
    /// Editor host for the result-comparison view — the same element the runtime HUD
    /// puts on screen, so the analysis is identical in both places.
    /// </summary>
    public class AeroComparisonWindow : EditorWindow
    {
        AeroComparisonView _view;

        [MenuItem("Window/Wind Tunnel/Compare Results")]
        public static void Open()
        {
            var window = GetWindow<AeroComparisonWindow>();
            window.titleContent = new GUIContent("Wind Tunnel Compare");
            window.minSize = new Vector2(720, 520);
            window.Show();
        }

        void CreateGUI()
        {
            _view = new AeroComparisonView { style = { flexGrow = 1 } };
            // Docked in a window there is nothing to dim or dismiss.
            _view.style.backgroundColor = new Color(0.055f, 0.067f, 0.086f);
            _view.style.position = Position.Relative;
            _view.Closed += Close;
            rootVisualElement.Add(_view);
        }

        void OnFocus() => _view?.Reload();
    }
}
