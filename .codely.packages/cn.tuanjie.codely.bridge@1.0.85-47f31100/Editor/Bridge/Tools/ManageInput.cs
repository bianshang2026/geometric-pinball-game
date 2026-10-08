using System;
using System.Collections;
using System.Collections.Generic;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityTcp.Editor.Helpers;

#if CODELY_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Controls;
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Simulates mouse and keyboard input across multiple editor frames via the coroutine queue.
    ///
    /// Mouse actions (coordinate origin: bottom-left, Y increases upward — Unity screen space):
    ///   All positional mouse actions (click/move/drag/scroll/down) accept an optional "target"
    ///   param that picks the path explicitly instead of hit-testing:
    ///     "auto" (default) – UI when the point is over a UI element, else the virtual device.
    ///     "ui"             – EventSystem/uGUI only; errors if nothing is hit (never the device).
    ///     "world"/"device" – virtual device only; skips the UI raycast (drives gameplay input even
    ///                        under a UI element). mouse_up follows whatever mouse_down pressed.
    ///   mouse_click  – Click at (x, y). Params: x, y, button (0=left,1=right,2=middle, default 0).
    ///   mouse_move   – Move mouse to (x, y). Params: x, y. Over UI: updates UI hover (enter/exit)
    ///                  via EventSystem only. Over non-UI: moves the virtual-device cursor only.
    ///   mouse_drag   – Drag from (from_x, from_y) to (x, y) at drag_speed px/s (default 500, 0=instant).
    ///                  Params: from_x, from_y, x, y, button, drag_speed.
    ///   mouse_scroll – Scroll at (x, y). Params: x, y, scroll_x, scroll_y.
    ///   mouse_down   – Press and hold a mouse button. Params: x, y, button. Presses the hit UI
    ///                  element via EventSystem (held until mouse_up); else Input System device.
    ///   mouse_up     – Release a held mouse button. Params: button. Releases an EventSystem press
    ///                  (firing click if press+release land on the same object) or the device button.
    ///
    /// Keyboard actions (require the Input System package — compiled only when CODELY_INPUT_SYSTEM is
    /// defined, i.e. the 'com.unity.inputsystem' package is installed; otherwise these actions return
    /// an error):
    ///   key_press – Tap a key for one frame. Params: key (e.g. "W", "Space", "LeftShift", "Enter").
    ///               When a uGUI/TMP input field is focused, editing/navigation keys (Backspace,
    ///               Delete, Enter, Tab, Escape, Home, End, arrows) are dispatched to that field via
    ///               the IMGUI event queue instead of the gameplay device (device injection can't
    ///               reach text fields). Printable text should use type_text.
    ///   key_down  – Hold a key down until key_up is called. Params: key.
    ///   key_up    – Release a previously held key. Params: key.
    ///
    /// Text-entry action (no Input System package required — pure EventSystem/IMGUI):
    ///   type_text – Type a whole string into the currently focused uGUI InputField or TMP_InputField.
    ///               Params: text (string). Select the field first (e.g. mouse_click on it) so it is
    ///               EventSystem.currentSelectedGameObject. Errors if no input field is focused.
    ///               This is the reliable route for text entry: input fields read characters from the
    ///               IMGUI event queue, which simulated device keys never populate.
    ///
    /// Virtual-device lifecycle (gameplay device input runs through non-hardware virtual devices so it
    /// survives per-frame hardware polling — see VirtualInputDevices):
    ///   create_virtual_devices  – Create the virtual mouse/keyboard (disables the real ones while
    ///                             active). REQUIRED before keyboard input or a mouse action over
    ///                             non-UI; those actions error out if it has not been called. UI
    ///                             interactions (click/drag/etc. that hit a UI element) do NOT need it.
    ///   destroy_virtual_devices – Remove the virtual devices and restore the real ones. Also happens
    ///                             automatically on play-mode exit. No params.
    ///
    /// Sequence action:
    ///   sequence – Execute an ordered list of input steps, one after another across frames.
    ///              Params:
    ///                steps         (array, required) – Each element is a JSON object with an "action"
    ///                                field plus all parameters for that action. Any step may also
    ///                                carry a "delay_after" (int) field: extra frames to wait after
    ///                                that step finishes, on top of the global step_delay.

    ///                start_delay   (int, default 0)  – Frames to wait before the very first step runs.
    ///                step_delay    (int, default 1)  – Frames to wait between consecutive steps.
    ///                stop_on_error (bool, default true) – Abort the sequence when a step fails.
    ///              Returns a summary with per-step results.
    ///              Example:
    ///                {
    ///                  "action": "sequence",
    ///                  "step_delay": 1,
    ///                  "steps": [
    ///                    { "action": "mouse_click", "x": 100, "y": 200 },
    ///                    { "action": "key_press",   "key": "Space"},
    ///                    { "action": "mouse_drag",  "from_x": 50, "from_y": 50, "x": 300, "y": 50, "drag_speed": 300, "delay_after": 2 }
    ///                  ]
    ///                }
    ///
    /// Notes:
    ///   - UI vs device is an either/or per action: if the position raycasts onto a UI element, the
    ///     action runs the EventSystem (uGUI) path ONLY and returns; otherwise it runs the virtual-
    ///     device path ONLY. The two are never combined in one action (no double-dispatch), and a UI
    ///     hit never falls through to the device even when the element can't handle that action.
    ///   - The EventSystem path calls uGUI handlers directly and is the reliable route for PlayMode
    ///     UI, because device-state injection can be overridden by hardware polling before game code
    ///     runs. The device path drives world/gameplay input via the non-hardware virtual devices.
    ///   - For mouse_click and key_press, press and release are separated by a yield so each state is
    ///     visible to game code for a full frame (WasPressedThisFrame / WasReleasedThisFrame).
    ///   - mouse_drag interpolates position across frames at drag_speed pixels per second so that
    ///     scroll views and sliders receive per-frame drag events (matching real user behaviour).
    ///   - Held keyboard keys and mouse buttons are tracked statically and cleared on PlayMode exit.
    /// </summary>
    public static class ManageInput
    {
        // Guards against two input commands running concurrently: the bridge advances all running
        // coroutines in parallel and they share static state (VirtualInputDevices, UI press/hover).
        private static bool _isProcessing;

        // EventSystem press state: bridges an EventSystem-dispatched mouse_down to its matching
        // mouse_up (two separate commands). The device-injection path can be overridden by hardware
        // polling, but EventSystem dispatch calls uGUI handlers directly, so it is reliable for UI.
        private static PointerEventData _uiPressData;
        private static GameObject       _uiPressHandler;

        // Currently hovered UI object, for mouse_move pointerEnter/pointerExit dispatch.
        private static GameObject       _uiHoverObject;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingPlayMode)
                return;

            // Virtual-device teardown is handled by VirtualInputDevices' own play-mode hook.

            // A coroutine stopped by the bridge on play-mode exit is dropped without disposal, so its
            // finally never runs; reset the guard here so input is not permanently wedged as "busy".
            _isProcessing = false;

            // Drop any dangling EventSystem press/hover state so it does not leak into the next session.
            _uiPressData = null;
            _uiPressHandler = null;
            _uiHoverObject = null;
        }

        private static readonly string[] ValidActions =
        {
            "mouse_click", "mouse_move", "mouse_drag", "mouse_scroll", "mouse_down", "mouse_up",
            "key_press", "key_down", "key_up", "type_text",
            "create_virtual_devices", "destroy_virtual_devices", "sequence",
        };

        // ── Public entry points ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Command entry point. Starts the input coroutine under the CoroutineRunner (input runs
        /// across multiple editor frames) and returns the job — the response is delivered later
        /// when the coroutine finishes.
        /// </summary>
        public static object HandleCommand(JObject @params)
        {
            var ctx = CoroutineRunner.CreateJob(CommandContext.RequestId, CommandContext.CommandType);
            CoroutineRunner.RunJob(ctx, HandleCommandCoroutine(@params, r => ctx.SetResult(r)));
            return ctx;
        }

        /// <summary>
        /// Coroutine entry point. Each yield advances one editor tick so that press/release
        /// states and drag interpolation are visible to game code across multiple frames.
        /// </summary>
        public static IEnumerator HandleCommandCoroutine(JObject @params, Action<object> setResult)
        {
            if (@params == null)
            {
                setResult(Response.Error("Parameters cannot be null."));
                yield break;
            }

            if (!ActionRouter.TryResolve(@params, ValidActions, out var action, out var resolveError))
            {
                setResult(resolveError);
                yield break;
            }

            // Input simulation only has an effect in PlayMode: the EventSystem is inactive in edit
            // mode and injected device state has no running game to consume it. Fail clearly instead
            // of reporting a success that did nothing.
            if (!EditorApplication.isPlaying)
            {
                setResult(Response.Error(
                    $"manage_input requires PlayMode. Enter Play mode before dispatching '{action}'."));
                yield break;
            }

            // Serialize input commands: two overlapping commands would advance in parallel and
            // cross-contaminate the shared held-key / pressed-button sets.
            if (_isProcessing)
            {
                setResult(Response.Error(
                    "Another manage_input command is still running. Input commands must run one at a time."));
                yield break;
            }

            _isProcessing = true;
            try
            {
                object result = null;
                IEnumerator step = action == "sequence"
                    ? SequenceCoroutine(@params,             r => result = r)
                    : DispatchStepCoroutine(action, @params, r => result = r);

                Exception caughtEx = null;
                while (true)
                {
                    bool hasNext;
                    try   { hasNext = step.MoveNext(); }
                    catch (Exception ex) { caughtEx = ex; break; }
                    if (!hasNext) break;
                    yield return step.Current;
                }

                if (caughtEx != null)
                {
                    CodelyLogger.LogError($"[ManageInput] Action '{action}' threw: {caughtEx}");
                    setResult(Response.Error($"Input simulation failed: {caughtEx.Message}"));
                }
                else
                {
                    setResult(result ?? Response.Success("Done."));
                }
            }
            finally
            {
                _isProcessing = false;
            }
        }

        // Note: there is deliberately no synchronous HandleCommand entry point. manage_input is a
        // coroutine-only command (see UnityTcpBridge.IsCoroutineCommand). Draining the coroutine
        // synchronously would collapse the press/release frames into one tick and busy-wait the
        // editor for the whole duration of a multi-frame drag, so callers must use the coroutine.

        // ── EventSystem helpers ────────────────────────────────────────────────────────────────

        private static EventSystem GetEventSystem() =>
            EditorApplication.isPlaying ? EventSystem.current : null;

        private static PointerEventData.InputButton ToInputButton(int button)
        {
            switch (button)
            {
                case 1:  return PointerEventData.InputButton.Right;
                case 2:  return PointerEventData.InputButton.Middle;
                default: return PointerEventData.InputButton.Left;
            }
        }

        private static bool TryRaycastUI(Vector2 screenPos, EventSystem es, out RaycastResult hit)
        {
            var pd      = new PointerEventData(es) { position = screenPos };
            var results = new List<RaycastResult>();
            es.RaycastAll(pd, results);
            if (results.Count > 0) { hit = results[0]; return true; }
            hit = default;
            return false;
        }

        // Which path a positional mouse action takes.
        private enum InputTarget { Auto, UI, World }

        // Optional "target" param that forces the path instead of hit-testing:
        //   "ui"              → EventSystem/uGUI path only; errors if nothing is hit (never falls to
        //                       the device), so callers can drive UI hidden under other UI.
        //   "world"/"device"  → virtual-device path only; skips the UI raycast entirely, so callers
        //                       can drive gameplay input even under a UI element.
        //   omitted / "auto"  → hit-test: UI when the point is over a UI element, else the device.
        private static InputTarget ParseTarget(JObject p)
        {
            switch (p["target"]?.ToString()?.ToLowerInvariant())
            {
                case "ui":                   return InputTarget.UI;
                case "world": case "device": return InputTarget.World;
                default:                     return InputTarget.Auto;
            }
        }

        // Transitions the tracked hover object: pointerExit on the old one, pointerEnter on the new.
        // Pass newHover = null to clear hover (e.g. the cursor left all UI).
        private static void UpdateHover(EventSystem es, GameObject newHover, RaycastResult hit, Vector2 screenPos)
        {
            if (newHover == _uiHoverObject) return;
            var pd = new PointerEventData(es) { position = screenPos, pointerCurrentRaycast = hit };
            if (_uiHoverObject != null)
                ExecuteEvents.ExecuteHierarchy(_uiHoverObject, pd, ExecuteEvents.pointerExitHandler);
            if (newHover != null)
                ExecuteEvents.ExecuteHierarchy(newHover, pd, ExecuteEvents.pointerEnterHandler);
            _uiHoverObject = newHover;
        }

        // Standard error for a device op requested while the Input System package is unavailable
        // (CODELY_INPUT_SYSTEM undefined). Does not touch any Input System type, so it lives outside
        // the #if and can be used from the #else branches of the device ops.
        private static object InputBackendDisabled(string op) => Response.Error(
            $"{op} requires the Input System package, but it is not available (CODELY_INPUT_SYSTEM is " +
            "not defined). Install the 'com.unity.inputsystem' package via Package Manager.");

        // Device input (keyboard, or a mouse action over non-UI) requires the virtual devices to be
        // created first. Returns false and writes an error response when they are not active, so the
        // caller can `yield break`. Devices are created explicitly via the create_virtual_devices action.
        private static bool VirtualDevicesReady(Action<object> setResult)
        {
#if CODELY_INPUT_SYSTEM
            if (VirtualInputDevices.Active) return true;
            setResult(Response.Error(
                "Virtual input devices are not created. Call the 'create_virtual_devices' action first, " +
                "then 'destroy_virtual_devices' when done."));
#else
            setResult(InputBackendDisabled("This operation"));
#endif
            return false;
        }

        // ── mouse_click coroutine ──────────────────────────────────────────────────────────────

        private static IEnumerator MouseClickCoroutine(JObject p, Action<object> setResult)
        {
            float x      = p["x"]?.ToObject<float>() ?? 0f;
            float y      = p["y"]?.ToObject<float>() ?? 0f;
            int   button = p["button"]?.ToObject<int>() ?? 0;
            Vector2 screenPos = new Vector2(x, y);
            InputTarget target = ParseTarget(p);

            // ── UI handling only (target=ui, or target=auto over a UI element). ──
            EventSystem es = GetEventSystem();
            if (target != InputTarget.World && es != null && TryRaycastUI(screenPos, es, out RaycastResult hit))
            {
                var go = hit.gameObject;
                var pd = new PointerEventData(es)
                {
                    position              = screenPos,
                    pressPosition         = screenPos,
                    button                = ToInputButton(button),
                    pointerCurrentRaycast = hit,
                    pointerPressRaycast   = hit
                };

                // pointerDown
                var pressHandler = ExecuteEvents.ExecuteHierarchy(go, pd, ExecuteEvents.pointerDownHandler);
                if (pressHandler != null)
                {
                    pd.pointerPress    = pressHandler;
                    pd.rawPointerPress = go;
                }

                yield return null; // one frame held

                // pointerUp + pointerClick
                if (pressHandler != null)
                    ExecuteEvents.Execute(pressHandler, pd, ExecuteEvents.pointerUpHandler);

                var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
                if (clickHandler != null)
                    ExecuteEvents.Execute(clickHandler, pd, ExecuteEvents.pointerClickHandler);

                setResult(Response.Success(
                    $"Clicked UI element '{go.name}' at ({x:F1}, {y:F1})",
                    new { x, y, button, hitGameObject = go.name, method = "EventSystem" }));
                yield break;
            }

            if (target == InputTarget.UI)
            {
                setResult(Response.Error($"target=ui but no UI element was hit at ({x:F1}, {y:F1})."));
                yield break;
            }

#if CODELY_INPUT_SYSTEM
            // ── Virtual device handling only (target=world, or target=auto with no UI hit). ──
            if (!VirtualDevicesReady(setResult)) yield break;
            VirtualInputDevices.SetMousePosition(screenPos);
            VirtualInputDevices.PressButton(button);
            try
            {
                yield return WaitObservationFrames(); // WasPressedThisFrame visible
            }
            finally
            {
                VirtualInputDevices.ReleaseButton(button); // always release, even on error
            }
            yield return WaitObservationFrames(); // WasReleasedThisFrame visible

            setResult(Response.Success(
                $"Mouse button {button} clicked at ({x:F1}, {y:F1})",
                new { x, y, button, method = "VirtualDevice" }));
            yield break;
#else
            setResult(InputBackendDisabled("mouse_click over non-UI"));
#endif
        }

        // ── mouse_move coroutine ───────────────────────────────────────────────────────────────

        private static IEnumerator MouseMoveCoroutine(JObject p, Action<object> setResult)
        {
            float x = p["x"]?.ToObject<float>() ?? 0f;
            float y = p["y"]?.ToObject<float>() ?? 0f;
            Vector2 screenPos = new Vector2(x, y);
            InputTarget target = ParseTarget(p);

            // ── UI handling only: over a UI element, just update hover (pointerEnter/pointerExit). ──
            EventSystem es = GetEventSystem();
            if (target != InputTarget.World && es != null && TryRaycastUI(screenPos, es, out RaycastResult hit))
            {
                UpdateHover(es, hit.gameObject, hit, screenPos);
                setResult(Response.Success(
                    $"Mouse hover updated at ({x:F1}, {y:F1})",
                    new { x, y, hitGameObject = hit.gameObject.name, method = "EventSystem" }));
                yield break;
            }

            if (target == InputTarget.UI)
            {
                setResult(Response.Error($"target=ui but no UI element was hit at ({x:F1}, {y:F1})."));
                yield break;
            }

            // The cursor is no longer over UI: clear any stale hover so the last element gets its
            // pointerExit. This is UI teardown for leaving UI, not device work.
            if (es != null && _uiHoverObject != null)
                UpdateHover(es, null, default, screenPos);

            // ── Virtual device handling only: move the game cursor via the virtual mouse. ──
            if (!VirtualDevicesReady(setResult)) yield break;
#if CODELY_INPUT_SYSTEM
            VirtualInputDevices.SetMousePosition(screenPos);
            yield return WaitObservationFrames();
            setResult(Response.Success(
                $"Mouse moved to ({x:F1}, {y:F1})",
                new { x, y, method = "VirtualDevice" }));
#endif
        }

        // ── mouse_drag coroutine ───────────────────────────────────────────────────────────────

        private static IEnumerator MouseDragCoroutine(JObject p, Action<object> setResult)
        {
            float fromX     = p["from_x"]?.ToObject<float>() ?? 0f;
            float fromY     = p["from_y"]?.ToObject<float>() ?? 0f;
            float toX       = p["x"]?.ToObject<float>() ?? 0f;
            float toY       = p["y"]?.ToObject<float>() ?? 0f;
            int   button    = p["button"]?.ToObject<int>() ?? 0;
            float dragSpeed = p["drag_speed"]?.ToObject<float>() ?? 500f;

            Vector2 screenFrom = new Vector2(fromX, fromY);
            Vector2 screenTo   = new Vector2(toX, toY);
            InputTarget target = ParseTarget(p);

            // ── UI handling only: full uGUI drag lifecycle with per-frame dragHandler dispatch. ──
            EventSystem es = GetEventSystem();
            if (target != InputTarget.World && es != null && TryRaycastUI(screenFrom, es, out RaycastResult startHit))
            {
                var go         = startHit.gameObject;
                var dragTarget = ExecuteEvents.GetEventHandler<IDragHandler>(go);
                if (dragTarget != null)
                {
                    var pd = new PointerEventData(es)
                    {
                        position              = screenFrom,
                        pressPosition         = screenFrom,
                        button                = PointerEventData.InputButton.Left, // uGUI only responds to left-button drags
                        pointerCurrentRaycast = startHit,
                        pointerPressRaycast   = startHit,
                        pointerDrag           = dragTarget
                    };

                    var pressHandler = ExecuteEvents.ExecuteHierarchy(go, pd, ExecuteEvents.pointerDownHandler);
                    if (pressHandler != null)
                        pd.pointerPress = pressHandler;

                    ExecuteEvents.Execute(dragTarget, pd, ExecuteEvents.initializePotentialDrag);
                    ExecuteEvents.Execute(dragTarget, pd, ExecuteEvents.beginDragHandler);
                    pd.dragging = true;
                    yield return null;

                    // Interpolate drag position across frames
                    foreach (bool _ in IterateDragFrames(pd, dragTarget, screenFrom, screenTo, dragSpeed))
                        yield return null;

                    // End drag: raycast drop target, fire pointerUp + drop + endDrag. If the drag ends
                    // over empty (non-UI) space there is no drop target — clear the stale start raycast
                    // so drop does NOT fire back on the drag source.
                    if (TryRaycastUI(screenTo, es, out RaycastResult endHit))
                        pd.pointerCurrentRaycast = endHit;
                    else
                        pd.pointerCurrentRaycast = default;

                    if (pd.pointerPress != null)
                        ExecuteEvents.Execute(pd.pointerPress, pd, ExecuteEvents.pointerUpHandler);

                    if (pd.pointerCurrentRaycast.gameObject != null)
                        ExecuteEvents.ExecuteHierarchy(pd.pointerCurrentRaycast.gameObject, pd, ExecuteEvents.dropHandler);

                    pd.dragging = false;
                    ExecuteEvents.Execute(dragTarget, pd, ExecuteEvents.endDragHandler);
                    yield return null;

                    setResult(Response.Success(
                        $"Dragged '{dragTarget.name}' from ({fromX:F1}, {fromY:F1}) to ({toX:F1}, {toY:F1}) at {dragSpeed:F0} px/s",
                        new { fromX, fromY, toX, toY, hitGameObject = dragTarget.name, method = "EventSystem" }));
                    yield break;
                }

                // UI was hit but nothing here is draggable — stay on the UI path; do NOT fall through
                // to a device drag (that would drag "through" the UI the user pointed at).
                setResult(Response.Error(
                    $"UI element '{go.name}' at ({fromX:F1}, {fromY:F1}) is not draggable (no IDragHandler)."));
                yield break;
            }

            if (target == InputTarget.UI)
            {
                setResult(Response.Error($"target=ui but no UI element was hit at ({fromX:F1}, {fromY:F1})."));
                yield break;
            }

#if CODELY_INPUT_SYSTEM
            // ── Virtual device handling only (target=world, or target=auto with no UI hit). ──
            if (!VirtualDevicesReady(setResult)) yield break;
            // Press at start on the virtual mouse; the manager holds the button + position each frame.
            VirtualInputDevices.SetMousePosition(screenFrom);
            VirtualInputDevices.PressButton(button);
            try
            {
                yield return WaitObservationFrames();

                float distance = Vector2.Distance(screenFrom, screenTo);
                float duration = dragSpeed > 0f ? distance / dragSpeed : 0f;

                if (duration <= 0f)
                {
                    VirtualInputDevices.AddMouseDelta(screenTo - screenFrom);
                    VirtualInputDevices.SetMousePosition(screenTo);
                    yield return null;
                }
                else
                {
                    float startTime = Time.realtimeSinceStartup;
                    Vector2 prevPos = screenFrom;
                    float t = 0f;

                    while (t < 1f)
                    {
                        float elapsed = Time.realtimeSinceStartup - startTime;
                        t = Mathf.Clamp01(elapsed / duration);
                        Vector2 currentPos = Vector2.Lerp(screenFrom, screenTo, t);
                        VirtualInputDevices.AddMouseDelta(currentPos - prevPos);
                        VirtualInputDevices.SetMousePosition(currentPos);
                        prevPos = currentPos;
                        yield return null;
                    }
                }
            }
            finally
            {
                VirtualInputDevices.ReleaseButton(button); // always release, even on error
            }
            yield return WaitObservationFrames();

            setResult(Response.Success(
                $"Mouse drag injected from ({fromX:F1}, {fromY:F1}) to ({toX:F1}, {toY:F1}) at {dragSpeed:F0} px/s",
                new { fromX, fromY, toX, toY, button, method = "VirtualDevice" }));
            yield break;
#else
            setResult(Response.Error(
                $"No UI element at ({fromX:F1}, {fromY:F1}) and the Input System package is not installed."));
#endif
        }

        // ── mouse_scroll coroutine ─────────────────────────────────────────────────────────────

        private static IEnumerator MouseScrollCoroutine(JObject p, Action<object> setResult)
        {
            float x       = p["x"]?.ToObject<float>() ?? 0f;
            float y       = p["y"]?.ToObject<float>() ?? 0f;
            float scrollX = p["scroll_x"]?.ToObject<float>() ?? 0f;
            float scrollY = p["scroll_y"]?.ToObject<float>() ?? 0f;
            Vector2 screenPos   = new Vector2(x, y);
            Vector2 scrollDelta = new Vector2(scrollX, scrollY);
            InputTarget target  = ParseTarget(p);

            // ── UI handling only: dispatch the uGUI scroll handler. We deliberately do NOT also inject
            // a device scroll — doing both double-applies (InputSystemUIInputModule would read the
            // injected scroll next frame and scroll the same element again). ──
            EventSystem es = GetEventSystem();
            if (target != InputTarget.World && es != null && TryRaycastUI(screenPos, es, out RaycastResult hit))
            {
                var pd = new PointerEventData(es)
                {
                    position              = screenPos,
                    scrollDelta           = scrollDelta,
                    pointerCurrentRaycast = hit
                };
                ExecuteEvents.ExecuteHierarchy(hit.gameObject, pd, ExecuteEvents.scrollHandler);
                setResult(Response.Success(
                    $"Scroll ({scrollX:F1}, {scrollY:F1}) at ({x:F1}, {y:F1})",
                    new { x, y, scrollX, scrollY, method = "EventSystem" }));
                yield break;
            }

            if (target == InputTarget.UI)
            {
                setResult(Response.Error($"target=ui but no UI element was hit at ({x:F1}, {y:F1})."));
                yield break;
            }

#if CODELY_INPUT_SYSTEM
            // ── Virtual device handling only (target=world, or target=auto with no UI hit). ──
            if (VirtualInputDevices.Active)
            {
                // Scroll is a momentary control; the manager applies it one frame then auto-zeroes it.
                VirtualInputDevices.SetMousePosition(screenPos);
                VirtualInputDevices.Scroll(scrollDelta);
                yield return WaitObservationFrames();
                setResult(Response.Success(
                    $"Scroll ({scrollX:F1}, {scrollY:F1}) at ({x:F1}, {y:F1})",
                    new { x, y, scrollX, scrollY, method = "VirtualDevice" }));
                yield break;
            }
            setResult(Response.Error(
                "mouse_scroll over non-UI needs the virtual mouse — call the 'create_virtual_devices' action first."));
#else
            setResult(InputBackendDisabled("mouse_scroll over non-UI"));
#endif
        }

        // ── mouse_down coroutine ───────────────────────────────────────────────────────────────

        private static IEnumerator MouseDownCoroutine(JObject p, Action<object> setResult)
        {
            float x      = p["x"]?.ToObject<float>() ?? 0f;
            float y      = p["y"]?.ToObject<float>() ?? 0f;
            int   button = p["button"]?.ToObject<int>() ?? 0;
            Vector2 screenPos = new Vector2(x, y);
            InputTarget target = ParseTarget(p);

            // ── UI handling only: press-and-hold a UI element; the matching mouse_up releases it. ──
            EventSystem es = GetEventSystem();
            if (target != InputTarget.World && es != null && TryRaycastUI(screenPos, es, out RaycastResult hit))
            {
                if (_uiPressData != null)
                {
                    setResult(Response.Error("A UI element is already held. Call mouse_up first."));
                    yield break;
                }

                var go = hit.gameObject;
                var pd = new PointerEventData(es)
                {
                    position              = screenPos,
                    pressPosition         = screenPos,
                    button                = ToInputButton(button),
                    pointerCurrentRaycast = hit,
                    pointerPressRaycast   = hit
                };

                var pressHandler = ExecuteEvents.ExecuteHierarchy(go, pd, ExecuteEvents.pointerDownHandler);
                pd.rawPointerPress = go; // remember the hit object so mouse_up can fire the click handler
                if (pressHandler != null)
                    pd.pointerPress = pressHandler;

                _uiPressData    = pd;
                _uiPressHandler = pressHandler;

                setResult(Response.Success(
                    $"Pressed UI element '{go.name}' at ({x:F1}, {y:F1})",
                    new { x, y, button, hitGameObject = go.name, method = "EventSystem" }));
                yield break;
            }

            if (target == InputTarget.UI)
            {
                setResult(Response.Error($"target=ui but no UI element was hit at ({x:F1}, {y:F1})."));
                yield break;
            }

#if CODELY_INPUT_SYSTEM
            // ── Virtual device handling only (target=world, or target=auto with no UI hit). ──
            if (!VirtualDevicesReady(setResult)) yield break;
            if (VirtualInputDevices.IsButtonHeld(button))
            {
                setResult(Response.Error($"Mouse button {button} is already held. Call mouse_up first."));
                yield break;
            }

            VirtualInputDevices.SetMousePosition(screenPos);
            VirtualInputDevices.PressButton(button);
            yield return WaitObservationFrames();

            setResult(Response.Success(
                $"Mouse button {button} pressed at ({x:F1}, {y:F1})",
                new { x, y, button, method = "VirtualDevice" }));
#else
            setResult(InputBackendDisabled("mouse_down"));
            yield break;
#endif
        }

        // ── mouse_up coroutine ─────────────────────────────────────────────────────────────────

        private static IEnumerator MouseUpCoroutine(JObject p, Action<object> setResult)
        {
            int button = p["button"]?.ToObject<int>() ?? 0;

            // EventSystem path: release a UI element held by a prior mouse_down.
            if (_uiPressData != null)
            {
                var pd           = _uiPressData;
                var pressHandler = _uiPressHandler;
                var pressedGo    = pd.rawPointerPress;
                pd.position      = pd.pressPosition; // mouse_up carries no coordinates → release in place

                if (pressHandler != null)
                    ExecuteEvents.Execute(pressHandler, pd, ExecuteEvents.pointerUpHandler);

                // uGUI treats a press+release on the same object as a click.
                var clickHandler = pressedGo != null
                    ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(pressedGo)
                    : null;
                if (clickHandler != null)
                    ExecuteEvents.Execute(clickHandler, pd, ExecuteEvents.pointerClickHandler);

                string name = pressedGo != null ? pressedGo.name : "(unknown)";
                _uiPressData    = null;
                _uiPressHandler = null;

                setResult(Response.Success(
                    $"Released UI element '{name}'",
                    new { button, hitGameObject = name, method = "EventSystem" }));
                yield break;
            }

#if CODELY_INPUT_SYSTEM
            if (!VirtualDevicesReady(setResult)) yield break;
            if (!VirtualInputDevices.IsButtonHeld(button))
            {
                setResult(Response.Error($"Mouse button {button} is not currently held. Call mouse_down first."));
                yield break;
            }

            VirtualInputDevices.ReleaseButton(button);
            yield return WaitObservationFrames();

            setResult(Response.Success($"Mouse button {button} released", new { button, method = "VirtualDevice" }));
#else
            setResult(InputBackendDisabled("mouse_up"));
            yield break;
#endif
        }

        // ── key_press coroutine ────────────────────────────────────────────────────────────────

        private static IEnumerator KeyPressCoroutine(JObject p, Action<object> setResult)
        {
            string keyName = p["key"]?.ToString();
            if (string.IsNullOrEmpty(keyName))
            {
                setResult(Response.Error("'key' parameter is required. Examples: \"W\", \"Space\", \"Enter\", \"LeftShift\", \"A\"."));
                yield break;
            }

#if CODELY_INPUT_SYSTEM
            if (!TryResolveKey(keyName, out Key key))
            {
                setResult(Response.Error(
                    $"Invalid key name: \"{keyName}\". Use Input System Key enum names (e.g. \"W\", \"Space\", \"LeftShift\", \"A\", \"Enter\")."));
                yield break;
            }

            // Focused-input-field path: when a uGUI/TMP field is selected, editing/navigation keys
            // (Backspace, Enter, arrows, …) must be dispatched to it through the IMGUI event queue —
            // device injection never reaches uGUI text entry. This runs before the device gate, so it
            // needs no virtual devices (mirroring the mouse EventSystem path). Printable text should
            // use the type_text action. Non-editing keys with no field focused fall through to the
            // device path below so gameplay still sees them.
            if (TryMapEditingKey(key, out Event editEv))
            {
                TextInputSink sink = ResolveFocusedTextField();
                if (sink.Valid)
                {
                    sink.Process(editEv);
                    sink.Finish();
                    yield return null; // let the label / onValueChanged settle a frame
                    setResult(Response.Success(
                        $"Key '{key}' sent to input field '{sink.Name}'",
                        new { key = key.ToString(), field = sink.Name, method = "EventSystem" }));
                    yield break;
                }
            }

            if (!VirtualDevicesReady(setResult)) yield break;

            string keyStr = key.ToString();
            if (VirtualInputDevices.IsKeyHeld(key))
            {
                setResult(Response.Error($"Key '{keyStr}' is already held. Call key_up first, or use key_down/key_up for explicit control."));
                yield break;
            }

            // Press on the virtual keyboard, hold for observation frames, then release. The manager
            // re-applies the held state every update phase so gameplay code sees it persistently.
            VirtualInputDevices.PressKey(key);
            yield return WaitObservationFrames();
            VirtualInputDevices.ReleaseKey(key);
            yield return WaitObservationFrames();

            setResult(Response.Success($"Key '{keyStr}' pressed", new { key = keyStr }));
#else
            setResult(InputBackendDisabled("key_press"));
            yield break;
#endif
        }

        // ── key_down coroutine ─────────────────────────────────────────────────────────────────

        private static IEnumerator KeyDownCoroutine(JObject p, Action<object> setResult)
        {
            string keyName = p["key"]?.ToString();
            if (string.IsNullOrEmpty(keyName))
            {
                setResult(Response.Error("'key' parameter is required."));
                yield break;
            }

#if CODELY_INPUT_SYSTEM
            if (!TryResolveKey(keyName, out Key key))
            {
                setResult(Response.Error($"Invalid key name: \"{keyName}\"."));
                yield break;
            }

            if (!VirtualDevicesReady(setResult)) yield break;

            string keyStr = key.ToString();
            if (VirtualInputDevices.IsKeyHeld(key))
            {
                setResult(Response.Error($"Key '{keyStr}' is already held. Call key_up first."));
                yield break;
            }

            VirtualInputDevices.PressKey(key);
            yield return WaitObservationFrames();

            setResult(Response.Success($"Key '{keyStr}' held down", new { key = keyStr }));
#else
            setResult(InputBackendDisabled("key_down"));
            yield break;
#endif
        }

        // ── key_up coroutine ───────────────────────────────────────────────────────────────────

        private static IEnumerator KeyUpCoroutine(JObject p, Action<object> setResult)
        {
            string keyName = p["key"]?.ToString();
            if (string.IsNullOrEmpty(keyName))
            {
                setResult(Response.Error("'key' parameter is required."));
                yield break;
            }

#if CODELY_INPUT_SYSTEM
            if (!TryResolveKey(keyName, out Key key))
            {
                setResult(Response.Error($"Invalid key name: \"{keyName}\"."));
                yield break;
            }

            if (!VirtualDevicesReady(setResult)) yield break;

            string keyStr = key.ToString();
            if (!VirtualInputDevices.IsKeyHeld(key))
            {
                setResult(Response.Error($"Key '{keyStr}' is not currently held. Call key_down first."));
                yield break;
            }

            VirtualInputDevices.ReleaseKey(key);
            yield return WaitObservationFrames();

            setResult(Response.Success($"Key '{keyStr}' released", new { key = keyStr }));
#else
            setResult(InputBackendDisabled("key_up"));
            yield break;
#endif
        }

        // ── type_text coroutine ────────────────────────────────────────────────────────────────
        // Types a whole string into the focused uGUI/TMP input field. uGUI and TMP input fields read
        // characters from the IMGUI event queue (Event.PopEvent), which device-state injection never
        // populates — so simulated keyboard keys can't reach them. This dispatches each character as
        // an IMGUI KeyDown event straight to the focused field, the analog of the mouse EventSystem
        // path. Needs no virtual devices and no Input System package (pure EventSystem + IMGUI).

        private static IEnumerator TypeTextCoroutine(JObject p, Action<object> setResult)
        {
            string text = p["text"]?.ToString();
            if (string.IsNullOrEmpty(text))
            {
                setResult(Response.Error("'text' parameter is required and must be a non-empty string."));
                yield break;
            }

            TextInputSink sink = ResolveFocusedTextField();
            if (!sink.Valid)
            {
                setResult(Response.Error(
                    "type_text needs a focused uGUI InputField or TMP_InputField. Select one first " +
                    "(e.g. mouse_click on it) so it becomes EventSystem.currentSelectedGameObject."));
                yield break;
            }

            foreach (char c in text)
                sink.Process(NewCharEvent(c));
            sink.Finish();

            yield return null; // let the label / onValueChanged settle a frame

            setResult(Response.Success(
                $"Typed {text.Length} character(s) into '{sink.Name}'",
                new { text, characters = text.Length, field = sink.Name, method = "EventSystem" }));
        }

        // ── Focused text-field dispatch ────────────────────────────────────────────────────────
        // A resolved sink over the focused input field. uGUI InputField is called directly; TMP's
        // TMP_InputField is reached by reflection so the bridge still compiles without TextMeshPro.

        private struct TextInputSink
        {
            public string        Name;
            public Action<Event> Process; // InputField.ProcessEvent(e)
            public Action        Finish;  // InputField.ForceLabelUpdate()
            public bool Valid => Process != null;
        }

        private static bool         _tmpTypeResolved;
        private static System.Type  _tmpInputFieldType;

        private static TextInputSink ResolveFocusedTextField()
        {
            var sink = new TextInputSink();

            EventSystem es  = GetEventSystem();
            GameObject   sel = es != null ? es.currentSelectedGameObject : null;
            if (sel == null) return sink;

            var uiField = sel.GetComponent<UnityEngine.UI.InputField>();
            if (uiField != null)
            {
                sink.Name    = sel.name;
                sink.Process = e => uiField.ProcessEvent(e);
                sink.Finish  = () => uiField.ForceLabelUpdate();
                return sink;
            }

            // TMP_InputField (optional package) via reflection — no hard assembly reference.
            if (!_tmpTypeResolved)
            {
                _tmpInputFieldType = System.Type.GetType("TMPro.TMP_InputField, Unity.TextMeshPro");
                _tmpTypeResolved   = true;
            }
            if (_tmpInputFieldType != null)
            {
                Component tmp = sel.GetComponent(_tmpInputFieldType);
                if (tmp != null)
                {
                    var process = _tmpInputFieldType.GetMethod("ProcessEvent", new[] { typeof(Event) });
                    var finish  = _tmpInputFieldType.GetMethod("ForceLabelUpdate", System.Type.EmptyTypes);
                    if (process != null)
                    {
                        sink.Name    = sel.name;
                        sink.Process = e => process.Invoke(tmp, new object[] { e });
                        sink.Finish  = () => finish?.Invoke(tmp, null);
                    }
                }
            }
            return sink;
        }

        // Builds the IMGUI KeyDown event that InputField.KeyPressed consumes for a printable char.
        private static Event NewCharEvent(char c) =>
            new Event { type = EventType.KeyDown, character = c };

#if CODELY_INPUT_SYSTEM
        // Maps an Input System editing/navigation key to the IMGUI event a focused input field reads.
        // Printable characters are intentionally excluded — those go through type_text. Returns false
        // for any key not in this set, so key_press falls back to the gameplay device path.
        private static bool TryMapEditingKey(Key key, out Event ev)
        {
            ev = new Event { type = EventType.KeyDown };
            switch (key)
            {
                case Key.Backspace:   ev.keyCode = KeyCode.Backspace;                          return true;
                case Key.Delete:      ev.keyCode = KeyCode.Delete;                             return true;
                case Key.Enter:       ev.keyCode = KeyCode.Return;      ev.character = '\n';    return true;
                case Key.NumpadEnter: ev.keyCode = KeyCode.KeypadEnter; ev.character = '\n';    return true;
                case Key.Tab:         ev.keyCode = KeyCode.Tab;         ev.character = '\t';    return true;
                case Key.Escape:      ev.keyCode = KeyCode.Escape;                             return true;
                case Key.LeftArrow:   ev.keyCode = KeyCode.LeftArrow;                          return true;
                case Key.RightArrow:  ev.keyCode = KeyCode.RightArrow;                         return true;
                case Key.UpArrow:     ev.keyCode = KeyCode.UpArrow;                            return true;
                case Key.DownArrow:   ev.keyCode = KeyCode.DownArrow;                          return true;
                case Key.Home:        ev.keyCode = KeyCode.Home;                               return true;
                case Key.End:         ev.keyCode = KeyCode.End;                                return true;
                default:              ev = null;                                               return false;
            }
        }
#endif

        // ── Virtual device lifecycle actions ───────────────────────────────────────────────────
        // Let a caller explicitly stand up the virtual devices before a run of input commands and
        // tear them down after (restoring the real devices), instead of relying on lazy creation on
        // first input and teardown on play-mode exit.

        private static IEnumerator CreateVirtualDevicesCoroutine(Action<object> setResult)
        {
#if CODELY_INPUT_SYSTEM
            VirtualInputDevices.Setup();
            setResult(Response.Success(
                "Virtual input devices created. Real mouse/keyboard are disabled while active; call " +
                "destroy_virtual_devices (or exit play mode) to restore them."));
#else
            setResult(InputBackendDisabled("create_virtual_devices"));
#endif
            yield break;
        }

        private static IEnumerator DestroyVirtualDevicesCoroutine(Action<object> setResult)
        {
#if CODELY_INPUT_SYSTEM
            VirtualInputDevices.Restore();
            setResult(Response.Success("Virtual input devices destroyed; real mouse/keyboard restored."));
#else
            setResult(InputBackendDisabled("destroy_virtual_devices"));
#endif
            yield break;
        }

        // Drives a possibly-nested coroutine tree with an explicit stack, surfacing only leaf yields
        // (e.g. null). Needed because the bridge's manual MoveNext pump does not recurse into a
        // nested IEnumerator the way UnityEngine.StartCoroutine does. Exceptions from any level
        // propagate out of MoveNext so the caller's try/catch still sees them.
        private static IEnumerator Flatten(IEnumerator root)
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(root);
            // Dispose every enumerator we pop (or abandon on exception) so inner coroutines' finally
            // blocks — e.g. releasing a held virtual mouse button — always run.
            try
            {
                while (stack.Count > 0)
                {
                    IEnumerator top = stack.Peek();
                    if (!top.MoveNext())
                    {
                        stack.Pop();
                        (top as IDisposable)?.Dispose();
                        continue;
                    }
                    if (top.Current is IEnumerator child)
                        stack.Push(child);
                    else
                        yield return top.Current;
                }
            }
            finally
            {
                while (stack.Count > 0)
                    (stack.Pop() as IDisposable)?.Dispose();
            }
        }

        // ── Shared step dispatcher ─────────────────────────────────────────────────────────────
        // Dispatches a single named action to the appropriate inner coroutine and pumps it to
        // completion. Used by both HandleCommandCoroutine (single action) and SequenceCoroutine.

        private static IEnumerator DispatchStepCoroutine(string action, JObject p, Action<object> setResult)
        {
            IEnumerator inner;
            switch (action)
            {
                case "mouse_click":  inner = MouseClickCoroutine(p,  setResult); break;
                case "mouse_move":   inner = MouseMoveCoroutine(p,   setResult); break;
                case "mouse_drag":   inner = MouseDragCoroutine(p,   setResult); break;
                case "mouse_scroll": inner = MouseScrollCoroutine(p, setResult); break;
                case "mouse_down":   inner = MouseDownCoroutine(p,   setResult); break;
                case "mouse_up":     inner = MouseUpCoroutine(p,     setResult); break;
                case "key_press":    inner = KeyPressCoroutine(p,    setResult); break;
                case "key_down":     inner = KeyDownCoroutine(p,     setResult); break;
                case "key_up":       inner = KeyUpCoroutine(p,       setResult); break;
                case "type_text":    inner = TypeTextCoroutine(p,    setResult); break;
                case "create_virtual_devices":  inner = CreateVirtualDevicesCoroutine(setResult);  break;
                case "destroy_virtual_devices": inner = DestroyVirtualDevicesCoroutine(setResult); break;
                default:
                    setResult(Response.Error(
                        $"Unknown action: '{action}'. Valid actions: " +
                        "mouse_click, mouse_move, mouse_drag, mouse_scroll, mouse_down, mouse_up, " +
                        "key_press, key_down, key_up, type_text, create_virtual_devices, destroy_virtual_devices, sequence."));
                    yield break;
            }

            // Flatten still used so a thrown MoveNext can be caught here (yield+catch is illegal).
            // CoroutineRunner now nests yielded IEnumerators; Flatten remains correct either way.
            inner = Flatten(inner);

            Exception caughtEx = null;
            while (true)
            {
                bool hasNext;
                try   { hasNext = inner.MoveNext(); }
                catch (Exception ex) { caughtEx = ex; break; }
                if (!hasNext) break;
                yield return inner.Current;
            }

            if (caughtEx != null)
            {
                CodelyLogger.LogError($"[ManageInput] Step '{action}' threw: {caughtEx}");
                setResult(Response.Error($"Step failed: {caughtEx.Message}"));
            }
        }

        // ── sequence coroutine ─────────────────────────────────────────────────────────────────

        private static IEnumerator SequenceCoroutine(JObject p, Action<object> setResult)
        {
            var stepsToken = p["steps"] as JArray;
            if (stepsToken == null || stepsToken.Count == 0)
            {
                setResult(Response.Error("'steps' array is required and must not be empty. " +
                    "Each element must be a JSON object with an 'action' field."));
                yield break;
            }

            int  startDelay  = Mathf.Max(0, p["start_delay"]?.ToObject<int>()   ?? 0);
            int  stepDelay   = Mathf.Max(0, p["step_delay"]?.ToObject<int>()    ?? 1);
            bool stopOnError = p["stop_on_error"]?.ToObject<bool>() ?? true;

            for (int d = 0; d < startDelay; d++)
                yield return null;

            int stepsTotal = stepsToken.Count;
            var stepResults = new List<object>(stepsTotal);
            int stepsCompleted = 0;

            for (int i = 0; i < stepsTotal; i++)
            {
                var stepToken = stepsToken[i] as JObject;
                if (stepToken == null)
                {
                    var errEntry = new Dictionary<string, object>
                    {
                        ["step"]    = i,
                        ["action"]  = null,
                        ["success"] = false,
                        ["message"] = "Step is not a valid JSON object."
                    };
                    stepResults.Add(errEntry);
                    if (stopOnError) break;
                    continue;
                }

                string stepAction = stepToken["action"]?.ToString()?.ToLowerInvariant();
                if (string.IsNullOrEmpty(stepAction))
                {
                    var errEntry = new Dictionary<string, object>
                    {
                        ["step"]    = i,
                        ["action"]  = null,
                        ["success"] = false,
                        ["message"] = "Step is missing the required 'action' field."
                    };
                    stepResults.Add(errEntry);
                    if (stopOnError) break;
                    continue;
                }

                // Run the step
                object stepResult = null;
                var stepCoroutine = DispatchStepCoroutine(stepAction, stepToken, r => stepResult = r);

                Exception stepEx = null;
                while (true)
                {
                    bool hasNext;
                    try   { hasNext = stepCoroutine.MoveNext(); }
                    catch (Exception ex) { stepEx = ex; break; }
                    if (!hasNext) break;
                    yield return stepCoroutine.Current;
                }

                if (stepEx != null)
                {
                    CodelyLogger.LogError($"[ManageInput] Sequence step {i} ('{stepAction}') threw: {stepEx}");
                    stepResult = Response.Error($"Step threw an exception: {stepEx.Message}");
                }

                bool stepSuccess = IsSuccessResult(stepResult);
                stepResults.Add(new Dictionary<string, object>
                {
                    ["step"]    = i,
                    ["action"]  = stepAction,
                    ["success"] = stepSuccess,
                    ["result"]  = stepResult
                });

                if (stepSuccess) stepsCompleted++;

                if (!stepSuccess && stopOnError)
                    break;

                // Wait after the step. The per-step delay_after always applies — it lets a final
                // animation/physics tick settle before the result is returned — while the global
                // step_delay only applies between steps, not after the last one.
                int delayAfter = Mathf.Max(0, stepToken["delay_after"]?.ToObject<int>() ?? 0);
                int totalDelay = delayAfter + (i < stepsTotal - 1 ? stepDelay : 0);
                for (int d = 0; d < totalDelay; d++)
                    yield return null;
            }

            bool allDone = stepsCompleted == stepsTotal;
            string summary = allDone
                ? $"Sequence completed: all {stepsTotal} step(s) succeeded."
                : $"Sequence stopped after {stepsCompleted}/{stepsTotal} step(s).";

            object sequenceResult = allDone
                ? Response.Success(summary, new { stepsCompleted, stepsTotal, stepResults })
                : Response.Error(summary,   new { stepsCompleted, stepsTotal, stepResults });

            setResult(sequenceResult);
        }

        // Returns true when the result dictionary carries success=true.
        private static bool IsSuccessResult(object result)
        {
            if (result is Dictionary<string, object> dict &&
                dict.TryGetValue("success", out object val))
                return val is bool b && b;
            return false;
        }

        // ── Drag frame iterator ────────────────────────────────────────────────────────────────
        // Uses IEnumerable<bool> so the caller can `foreach … yield return null` cleanly.

        private static IEnumerable<bool> IterateDragFrames(
            PointerEventData pd,
            GameObject dragTarget,
            Vector2 screenFrom,
            Vector2 screenTo,
            float dragSpeed)
        {
            float distance = Vector2.Distance(screenFrom, screenTo);
            float duration = dragSpeed > 0f ? distance / dragSpeed : 0f;

            if (duration <= 0f)
            {
                pd.delta    = screenTo - screenFrom;
                pd.position = screenTo;
                ExecuteEvents.Execute(dragTarget, pd, ExecuteEvents.dragHandler);
                yield return true;
                yield break;
            }

            float startTime = Time.realtimeSinceStartup;
            float t         = 0f;
            Vector2 prevPos = screenFrom;

            while (t < 1f)
            {
                float elapsed  = Time.realtimeSinceStartup - startTime;
                t              = Mathf.Clamp01(elapsed / duration);
                Vector2 curPos = Vector2.Lerp(screenFrom, screenTo, t);

                pd.delta    = curPos - prevPos;
                pd.position = curPos;
                ExecuteEvents.Execute(dragTarget, pd, ExecuteEvents.dragHandler);
                prevPos = curPos;

                yield return true;
            }
        }

#if CODELY_INPUT_SYSTEM
        // ── Input System helpers ───────────────────────────────────────────────────────────────

        // Gameplay device input (Mouse.current / Keyboard.current, WasPressedThisFrame, WASD, camera)
        // is driven through VirtualInputDevices — non-hardware-backed devices that persist injected
        // state against per-frame hardware polling (e.g. Tuanjie FastMouse / FastKeyboard). The
        // helpers below only compute how many frames to wait so gameplay code observes a transition.

        // ── Observation-frame timing (ported from uLoopMCP InputSystemUpdateHelper) ─────────────

        private static InputUpdateType ResolveInputUpdateType()
        {
            InputSettings settings = InputSystem.settings;
            if (settings == null)
                return InputUpdateType.Dynamic;

            switch (settings.updateMode)
            {
                case InputSettings.UpdateMode.ProcessEventsInFixedUpdate:
                    // Paused screens commonly set timeScale to 0, which stops fixed ticks; fall back
                    // to Dynamic so simulation stays responsive for pause menus.
                    return IsPausedFixedUpdate(settings) ? InputUpdateType.Dynamic : InputUpdateType.Fixed;
                case InputSettings.UpdateMode.ProcessEventsManually:
                    return InputUpdateType.Manual;
                default:
                    return InputUpdateType.Dynamic;
            }
        }

        private static bool IsPausedFixedUpdate(InputSettings settings) =>
            settings.updateMode == InputSettings.UpdateMode.ProcessEventsInFixedUpdate && Time.timeScale <= 0f;

        // Manual mode and paused-fixed mode have no automatic update to piggyback on, so those
        // require us to drive InputSystem.Update() ourselves.
        private static bool RequiresExplicitUpdate()
        {
            InputSettings settings = InputSystem.settings;
            if (settings == null)
                return false;
            if (settings.updateMode == InputSettings.UpdateMode.ProcessEventsManually)
                return true;
            return IsPausedFixedUpdate(settings);
        }

        // Frames to wait after a state change so gameplay code observes the transition.
        // Auto modes need 1; explicit non-manual needs 2; manual needs 3 (projects often pump their
        // own InputSystem.Update from Update(), so zero-duration taps need an extra frame).
        private static int GetMinimumObservationFrameCount()
        {
            if (!RequiresExplicitUpdate())
                return 1;
            return ResolveInputUpdateType() == InputUpdateType.Manual ? 3 : 2;
        }

        private static IEnumerator WaitObservationFrames()
        {
            int start = Time.frameCount;
            int need  = GetMinimumObservationFrameCount();
            while (Time.frameCount - start < need)
                yield return null;
        }

        private static bool TryResolveKey(string keyName, out Key key)
        {
            // Common alias: Unity legacy "Return" → Input System "Enter"
            if (string.Equals(keyName, "Return", StringComparison.OrdinalIgnoreCase))
            {
                key = Key.Enter;
                return true;
            }
            return Enum.TryParse(keyName, ignoreCase: true, out key) && key != Key.None;
        }
#endif
    }
}
