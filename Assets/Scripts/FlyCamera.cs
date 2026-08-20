using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Motawea.WindTunnel.Samples
{
    /// <summary>
    /// Scene-view-style fly camera for the simulation sample.
    ///
    /// MIDDLE MOUSE:      toggle mouse lock — while locked, free-look is always on.
    /// Hold RIGHT MOUSE:  temporary free-look while unlocked.
    /// Free-look:         mouse to look, WASD to move, Q/E down/up,
    ///                    Shift for fast speed, scroll to tune speed.
    /// Scroll (idle):     dolly forward/back.
    /// SPACE:             run / pause the wind tunnel.
    /// C:                 screenshot (pauses the sim and opens the folder).
    /// </summary>
    [AddComponentMenu("Wind Tunnel/Samples/Fly Camera")]
    public class FlyCamera : MonoBehaviour
    {
        [Tooltip("Tunnel toggled by the Space hotkey. Auto-found when empty.")]
        public WindTunnelDomain tunnel;

        [Header("Move speed (m/s)")]
        [Tooltip("Speed without Shift held.")]
        [Min(0.01f)] public float moveSpeed = 4f;
        [Tooltip("Speed while Shift is held.")]
        [Min(0.01f)] public float fastMoveSpeed = 15f;

        [Header("Look")]
        [Tooltip("Degrees of rotation per pixel of mouse travel.")]
        [Range(0.02f, 1f)] public float lookSensitivity = 0.12f;

        [Header("Dolly")]
        [Tooltip("Meters per scroll notch when not flying.")]
        [Min(0.01f)] public float dollySpeed = 1.5f;

        [Header("Speed tuning (scroll while flying)")]
        [Tooltip("Multiplier applied to both speeds per scroll notch while flying.")]
        [Range(1.01f, 1.5f)] public float speedScrollFactor = 1.15f;
        public float minMoveSpeed = 0.2f;
        public float maxMoveSpeed = 200f;

        float _yaw, _pitch;
        bool _mouseLocked;
        UI.WindTunnelHUD _hud;

        public bool MouseLocked => _mouseLocked;

        void OnEnable()
        {
            Vector3 e = transform.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180f ? e.x - 360f : e.x;
            if (tunnel == null) tunnel = FindFirstObjectByType<WindTunnelDomain>();
        }

        void OnDisable() => SetCursorLocked(false);

        void Update()
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (mouse == null || keyboard == null) return;

            // A modal dialog owns the input while it is up: SPACE would otherwise
            // start the solver behind it and WASD would fly the camera away.
            if (_hud == null) _hud = FindFirstObjectByType<UI.WindTunnelHUD>();
            if (_hud != null && _hud.IsModalOpen) return;

            if (keyboard.spaceKey.wasPressedThisFrame)
                ToggleSimulation();

            if (keyboard.cKey.wasPressedThisFrame)
                TakeScreenshot();

            if (keyboard.f2Key.wasPressedThisFrame)
            {
                var hud = FindFirstObjectByType<UI.WindTunnelHUD>();
                if (hud != null) hud.ToggleDebugStats();
            }

            if (mouse.middleButton.wasPressedThisFrame)
            {
                _mouseLocked = !_mouseLocked;
                SetCursorLocked(_mouseLocked);
            }

            // Temporary free-look on RMB while not in locked mode.
            if (!_mouseLocked)
            {
                if (mouse.rightButton.wasPressedThisFrame) SetCursorLocked(true);
                if (mouse.rightButton.wasReleasedThisFrame) SetCursorLocked(false);
            }

            Vector2 mouseDelta = mouse.delta.ReadValue();
            float scrollSteps = ReadScrollSteps(mouse);
            bool flying = _mouseLocked || mouse.rightButton.isPressed;

            if (flying)
                Fly(keyboard, mouseDelta, scrollSteps);
            else if (Mathf.Abs(scrollSteps) > 0f && !PointerOverUI(mouse))
                transform.position += transform.forward * (scrollSteps * dollySpeed);
        }

        // A scroll over the HUD panels belongs to their scroll views, not the
        // camera dolly. Only consulted with a free cursor: while flying, the
        // cursor is locked and its position is meaningless.
        bool PointerOverUI(Mouse mouse)
        {
            if (_hud == null) _hud = FindFirstObjectByType<UI.WindTunnelHUD>();
            return _hud != null && _hud.IsPointerOverUI(mouse.position.ReadValue());
        }

        void Fly(Keyboard keyboard, Vector2 mouseDelta, float scrollSteps)
        {
            _yaw += mouseDelta.x * lookSensitivity;
            _pitch = Mathf.Clamp(_pitch - mouseDelta.y * lookSensitivity, -89f, 89f);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

            if (Mathf.Abs(scrollSteps) > 0f)
            {
                // Clamp the FACTOR so the slow/fast ratio is preserved: clamping each
                // speed separately let both saturate at the same limit, which made
                // Shift a no-op after scrolling.
                float factor = Mathf.Pow(speedScrollFactor, scrollSteps);
                float lo = minMoveSpeed / Mathf.Min(moveSpeed, fastMoveSpeed);
                float hi = maxMoveSpeed / Mathf.Max(moveSpeed, fastMoveSpeed);
                factor = Mathf.Clamp(factor, lo, hi);
                moveSpeed *= factor;
                fastMoveSpeed *= factor;
            }

            Vector3 dir = new Vector3(
                Axis(keyboard.dKey, keyboard.aKey),
                Axis(keyboard.eKey, keyboard.qKey),
                Axis(keyboard.wKey, keyboard.sKey));
            if (dir.sqrMagnitude < 1e-4f) return;

            bool fast = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            float speed = fast ? fastMoveSpeed : moveSpeed;
            transform.position += transform.rotation * (dir.normalized * (speed * Time.unscaledDeltaTime));
        }

        void ToggleSimulation()
        {
            if (tunnel == null) tunnel = FindFirstObjectByType<WindTunnelDomain>();
            if (tunnel == null) return;

            if (tunnel.Solver == null) tunnel.StartSimulation();
            else if (tunnel.IsRunning) tunnel.StopSimulation();
            else tunnel.ResumeSimulation();
        }

        void TakeScreenshot()
        {
            // Route through the HUD when present so its status line shows the path.
            var hud = FindFirstObjectByType<UI.WindTunnelHUD>();
            if (hud != null)
            {
                hud.TakeScreenshot();
                return;
            }

            if (tunnel == null) tunnel = FindFirstObjectByType<WindTunnelDomain>();
            if (tunnel != null) tunnel.StopSimulation();
            StartCoroutine(RevealAfterWrite(AeroScreenshot.Capture(
                tunnel != null && tunnel.vehicle != null ? tunnel.vehicle.Name : null)));
        }

        static IEnumerator RevealAfterWrite(string path)
        {
            yield return new WaitForSecondsRealtime(0.5f);
            AeroScreenshot.RevealFolder(path);
        }

        static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        static float Axis(ButtonControl positive, ButtonControl negative)
            => (positive.isPressed ? 1f : 0f) - (negative.isPressed ? 1f : 0f);

        // Windows reports ±120 per wheel notch, other platforms/devices ±1.
        static float ReadScrollSteps(Mouse mouse)
        {
            float raw = mouse.scroll.ReadValue().y;
            if (Mathf.Approximately(raw, 0f)) return 0f;
            float steps = Mathf.Abs(raw) >= 100f ? raw / 120f : raw;
            return Mathf.Clamp(steps, -3f, 3f);
        }
    }
}
