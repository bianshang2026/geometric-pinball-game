#if CODELY_INPUT_SYSTEM
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Reliable gameplay input injection via non-hardware-backed Input System devices.
    ///
    /// Real editor devices (e.g. Tuanjie FastMouse / FastKeyboard) re-poll hardware every player-loop
    /// frame and overwrite any injected state. A device added with InputSystem.AddDevice has no
    /// hardware backing, so its state persists. We hold the desired state (position, buttons, keys)
    /// and re-apply it every Input System update phase via onBeforeUpdate, writing with
    /// InputState.Change(dev, ev, InputState.currentUpdateType) so it lands in the exact buffer
    /// gameplay code reads that frame. Disabling the real same-type devices makes the virtual device
    /// become Device.current, so code that reads Mouse.current / Keyboard.current sees it too.
    ///
    /// Safety: the disabled real devices are always restored, and the virtual devices removed, on
    /// play-mode exit AND before assembly reload — so real input can never be left bricked. Restore()
    /// is also exposed as a manual panic button.
    ///
    /// Verified in Tuanjie 2022.3 (Active Input Handling = Both, ProcessEventsInDynamicUpdate).
    /// </summary>
    internal static class VirtualInputDevices
    {
        private static Mouse    _mouse;
        private static Keyboard _keyboard;

        private static readonly List<InputDevice> _disabledReal = new List<InputDevice>();
        private static bool _hooked;

        // Desired state, re-applied every update phase.
        private static readonly HashSet<int> _heldButtons = new HashSet<int>();
        private static readonly HashSet<int> _prevButtons = new HashSet<int>();
        private static readonly HashSet<Key> _heldKeys    = new HashSet<Key>();
        private static readonly HashSet<Key> _prevKeys    = new HashSet<Key>();

        private static Vector2 _mousePos;
        private static bool    _mousePosKnown;
        private static Vector2 _pendingScroll; // momentary — applied then zeroed each frame
        private static Vector2 _pendingDelta;  // momentary — applied then zeroed each frame

        public static bool Active => _hooked;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= Restore;
            AssemblyReloadEvents.beforeAssemblyReload += Restore;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
                Restore();
        }

        // ── Explicit lifecycle ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Eagerly creates both virtual devices (and disables the real ones), instead of waiting for
        /// the first input action to create them lazily. Idempotent. Pair with Restore().
        /// </summary>
        public static void Setup()
        {
            EnsureMouse();
            EnsureKeyboard();
        }

        // ── Mouse API ────────────────────────────────────────────────────────────────────────────
        // These do NOT create devices — callers must Setup() first (guarded via Active). They safely
        // no-op when inactive (the onBeforeUpdate handler ignores state when no device exists).

        public static Mouse Mouse => _mouse;

        public static void SetMousePosition(Vector2 screenPos) { _mousePos = screenPos; _mousePosKnown = true; }
        public static void AddMouseDelta(Vector2 delta) { _pendingDelta += delta; }
        public static void Scroll(Vector2 scroll)       { _pendingScroll += scroll; }
        public static void PressButton(int button)      { _heldButtons.Add(button); }
        public static void ReleaseButton(int button)    { _heldButtons.Remove(button); }
        public static bool IsButtonHeld(int button)     => _heldButtons.Contains(button);

        // ── Keyboard API ─────────────────────────────────────────────────────────────────────────

        public static void PressKey(Key key)   { _heldKeys.Add(key); }
        public static void ReleaseKey(Key key)  { _heldKeys.Remove(key); }
        public static bool IsKeyHeld(Key key)   => _heldKeys.Contains(key);

        // ── Device lifecycle ───────────────────────────────────────────────────────────────────────

        private static Mouse EnsureMouse()
        {
            if (_mouse == null || !_mouse.added)
            {
                _mouse = InputSystem.AddDevice<Mouse>("CodelyVirtualMouse");
                DisableReal<Mouse>(_mouse);
            }
            Hook();
            return _mouse;
        }

        private static Keyboard EnsureKeyboard()
        {
            if (_keyboard == null || !_keyboard.added)
            {
                _keyboard = InputSystem.AddDevice<Keyboard>("CodelyVirtualKeyboard");
                DisableReal<Keyboard>(_keyboard);
            }
            Hook();
            return _keyboard;
        }

        private static void DisableReal<T>(InputDevice keep) where T : InputDevice
        {
            foreach (InputDevice d in InputSystem.devices)
            {
                if (d is T && d != keep && d.enabled && !_disabledReal.Contains(d))
                {
                    InputSystem.DisableDevice(d);
                    _disabledReal.Add(d);
                }
            }
        }

        private static void Hook()
        {
            if (_hooked) return;
            InputSystem.onBeforeUpdate += OnBeforeUpdate;
            _hooked = true;
        }

        // ── Per-frame re-injection ─────────────────────────────────────────────────────────────────

        private static void OnBeforeUpdate()
        {
            InputUpdateType t = InputState.currentUpdateType;
            // Only the gameplay phases game code reads — skip Editor / BeforeRender noise.
            if ((t & (InputUpdateType.Dynamic | InputUpdateType.Fixed | InputUpdateType.Manual)) == 0)
                return;

            ApplyMouse(t);
            ApplyKeyboard(t);
        }

        private static void ApplyMouse(InputUpdateType t)
        {
            if (_mouse == null || !_mouse.added) return;

            using (StateEvent.From(_mouse, out InputEventPtr ev))
            {
                if (_mousePosKnown)
                    _mouse.position.WriteValueIntoEvent(_mousePos, ev);

                // Release buttons no longer held, then press held ones (diff handles releases).
                foreach (int b in _prevButtons)
                    if (!_heldButtons.Contains(b))
                        ButtonFor(b).WriteValueIntoEvent(0f, ev);
                foreach (int b in _heldButtons)
                    ButtonFor(b).WriteValueIntoEvent(1f, ev);

                // Momentary controls: always write (pending value or zero) so they never linger.
                _mouse.delta.WriteValueIntoEvent(_pendingDelta, ev);
                _mouse.scroll.WriteValueIntoEvent(_pendingScroll, ev);

                InputState.Change(_mouse, ev, t);
            }

            _pendingDelta = Vector2.zero;
            _pendingScroll = Vector2.zero;
            _prevButtons.Clear();
            _prevButtons.UnionWith(_heldButtons);
        }

        private static void ApplyKeyboard(InputUpdateType t)
        {
            if (_keyboard == null || !_keyboard.added) return;
            if (_heldKeys.Count == 0 && _prevKeys.Count == 0) return;

            using (StateEvent.From(_keyboard, out InputEventPtr ev))
            {
                foreach (Key k in _prevKeys)
                    if (!_heldKeys.Contains(k))
                        _keyboard[k].WriteValueIntoEvent(0f, ev);
                foreach (Key k in _heldKeys)
                    _keyboard[k].WriteValueIntoEvent(1f, ev);

                InputState.Change(_keyboard, ev, t);
            }

            _prevKeys.Clear();
            _prevKeys.UnionWith(_heldKeys);
        }

        private static ButtonControl ButtonFor(int button)
        {
            switch (button)
            {
                case 1:  return _mouse.rightButton;
                case 2:  return _mouse.middleButton;
                default: return _mouse.leftButton;
            }
        }

        // ── Teardown / panic restore ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Releases all held state, removes the virtual devices, and re-enables every real device we
        /// disabled. Safe to call anytime; also the manual panic button if input feels stuck.
        /// </summary>
        public static void Restore()
        {
            if (_hooked)
            {
                InputSystem.onBeforeUpdate -= OnBeforeUpdate;
                _hooked = false;
            }

            _heldButtons.Clear();
            _prevButtons.Clear();
            _heldKeys.Clear();
            _prevKeys.Clear();
            _mousePosKnown = false;
            _pendingScroll = Vector2.zero;
            _pendingDelta  = Vector2.zero;

            if (_mouse != null && _mouse.added)    InputSystem.RemoveDevice(_mouse);
            if (_keyboard != null && _keyboard.added) InputSystem.RemoveDevice(_keyboard);
            _mouse = null;
            _keyboard = null;

            foreach (InputDevice d in _disabledReal)
                if (d != null && d.added && !d.enabled)
                    InputSystem.EnableDevice(d);
            _disabledReal.Clear();
        }

        // Manual panic button: if real input ever feels stuck (e.g. a simulation left a device
        // disabled), this tears down the virtual devices and re-enables the real ones.
        private static void RestoreMenu() => Restore();
    }
}
#endif
