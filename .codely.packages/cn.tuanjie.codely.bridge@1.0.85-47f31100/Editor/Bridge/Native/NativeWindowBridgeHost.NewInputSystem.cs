using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityTcp.Editor.Native
{
    internal static partial class NativeWindowBridgeHost
    {
#if UNITY_EDITOR_WIN
        // ── New Input System (com.unity.inputsystem) input injection ──
        //
        // The new Input System (activeInputHandler=2) processes keyboard through
        // WM_INPUT (Raw Input), NOT WM_KEYDOWN. The WM_INPUT path is guarded by
        // `isFocused = GetApplication().IsFocused()` which returns false when the
        // editor is offscreen. So neither WM_KEYDOWN nor WM_INPUT reaches the new
        // Input System in streaming/offscreen mode.
        //
        // Solution: use InputSystem.QueueStateEvent to directly inject keyboard
        // state into the new Input System's managed event queue, bypassing the
        // native Win32 message pipeline and the isFocused check entirely.
        //
        // QueueStateEvent replaces the ENTIRE keyboard state, so we must track
        // all held keys and send the complete state on each keydown/keyup.
        // s_WinHeldKeys already tracks held keys as browser key strings.
        private static Type s_InputSystemType;
        private static Type s_KeyboardType;
        private static Type s_KeyboardStateType;
        private static Type s_KeyType;
        private static System.Reflection.MethodInfo s_QueueStateEventMethod;
        private static System.Reflection.MethodInfo s_GetDeviceMethod;
        private static System.Reflection.MethodInfo s_KeySetMethod;
        private static bool s_InputSystemRefResolved;

        // Mouse-specific reflection cache (resolved alongside keyboard refs).
        private static Type s_MouseType;
        private static Type s_MouseStateType;
        private static System.Reflection.MethodInfo s_MouseGetDeviceMethod;
        private static System.Reflection.MethodInfo s_MouseQueueStateEventMethod;
        // Keep the latest known screen position for cleanup paths.
        private static Vector2 s_InputSystemMouseScreenPos = Vector2.zero;
        private static bool s_InputSystemMouseScreenPosValid;
        // Browser button values currently held (for building MouseState.buttons bitmask).
        private static readonly HashSet<int> s_InputSystemHeldMouseButtons = new HashSet<int>();
        private static System.Reflection.FieldInfo s_InputManagerInstanceField;
        private static System.Reflection.FieldInfo s_InputManagerHasFocusField;
        private static int s_InputSystemDiagCount = 0;

        // Consume one diagnostic log budget slot for noisy reflection/input probes.
        private static bool TryConsumeInputSystemDiagBudget(int limit = 3)
        {
            if (s_InputSystemDiagCount >= limit) return false;
            s_InputSystemDiagCount++;
            return true;
        }

        // Emit InputSystem diagnostics with a shared rate limit.
        private static void LogInputSystemDiagLimited(string message, int limit = 3)
        {
            if (TryConsumeInputSystemDiagBudget(limit))
                LogVerbose(message);
        }

        /// <summary>
        /// Lazily resolve Input System types via reflection. The package may not
        /// be installed in all projects, so all access is reflection-based.
        /// </summary>
        private static bool TryResolveInputSystemRefs()
        {
            if (s_InputSystemRefResolved) return true;

            try
            {
                // Find the InputSystem assembly by name first (more reliable than GetType)
                const string asmName = "Unity.InputSystem";
                System.Reflection.Assembly inputAsm = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == asmName)
                    {
                        inputAsm = asm;
                        break;
                    }
                }

                // Fallback: search by type name
                if (inputAsm == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var type = asm.GetType("UnityEngine.InputSystem.InputSystem");
                        if (type != null)
                        {
                            inputAsm = asm;
                            break;
                        }
                    }
                }

                if (inputAsm == null)
                {
                    if (TryConsumeInputSystemDiagBudget())
                    {
                        var asmNames = new List<string>();
                        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            string n = a.GetName().Name;
                            if (n.Contains("Input") || n.Contains("input"))
                                asmNames.Add(n);
                        }
                        LogVerbose($"[NWB-NewInputSystem] Could not find InputSystem assembly. Input-related assemblies: [{string.Join(", ", asmNames)}]");
                    }
                    return false;
                }

                s_InputSystemType = inputAsm.GetType("UnityEngine.InputSystem.InputSystem");
                if (s_InputSystemType == null)
                {
                    LogInputSystemDiagLimited("[NWB-NewInputSystem] Found assembly but InputSystem type is null");
                    return false;
                }

                s_KeyboardType = s_InputSystemType.Assembly.GetType("UnityEngine.InputSystem.Keyboard");
                s_KeyboardStateType = s_InputSystemType.Assembly.GetType("UnityEngine.InputSystem.LowLevel.KeyboardState");
                s_KeyType = s_InputSystemType.Assembly.GetType("UnityEngine.InputSystem.Key");

                if (s_KeyboardType == null || s_KeyboardStateType == null || s_KeyType == null)
                {
                    LogInputSystemDiagLimited($"[NWB-NewInputSystem] Type resolution failed: Keyboard={s_KeyboardType != null} KeyboardState={s_KeyboardStateType != null} Key={s_KeyType != null}");
                    return false;
                }

                // InputSystem.GetDevice<Keyboard>() -> Keyboard.current
                // Use GetMethods + filter to avoid AmbiguousMatchException from multiple GetDevice overloads.
                foreach (var m in s_InputSystemType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (m.Name == "GetDevice" && m.IsGenericMethod && m.GetParameters().Length == 0)
                    {
                        s_GetDeviceMethod = m.MakeGenericMethod(s_KeyboardType);
                        break;
                    }
                }

                if (s_GetDeviceMethod == null)
                {
                    LogInputSystemDiagLimited("[NWB-NewInputSystem] GetDevice method not found");
                    return false;
                }

                // InputSystem.QueueStateEvent<TState>(InputDevice device, TState state, double time = -1)
                foreach (var m in s_InputSystemType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (m.Name == "QueueStateEvent" && m.IsGenericMethod)
                    {
                        var parms = m.GetParameters();
                        if (parms.Length == 3 && parms[2].ParameterType == typeof(double))
                        {
                            s_QueueStateEventMethod = m.MakeGenericMethod(s_KeyboardStateType);
                            break;
                        }
                    }
                }

                if (s_QueueStateEventMethod == null)
                {
                    LogInputSystemDiagLimited("[NWB-NewInputSystem] QueueStateEvent method not found");
                    return false;
                }

                // KeyboardState.Set(Key key, bool state) - used to set individual key bits.
                foreach (var m in s_KeyboardStateType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (m.Name == "Set")
                    {
                        var parms = m.GetParameters();
                        if (parms.Length == 2 && parms[0].ParameterType == s_KeyType && parms[1].ParameterType == typeof(bool))
                        {
                            s_KeySetMethod = m;
                            break;
                        }
                    }
                }

                // InputManager.s_Manager (singleton) and m_HasFocus field.
                var inputManagerType = s_InputSystemType.Assembly.GetType("UnityEngine.InputSystem.InputManager");
                if (inputManagerType != null)
                {
                    s_InputManagerInstanceField = s_InputSystemType.GetField("s_Manager",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (s_InputManagerInstanceField != null)
                    {
                        s_InputManagerHasFocusField = inputManagerType.GetField("m_HasFocus",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    }
                }

                // ── Mouse type resolution ──
                // Same pattern as Keyboard: resolve Mouse device type, MouseState struct,
                // GetDevice<Mouse>() and QueueStateEvent<MouseState>() via reflection.
                s_MouseType = s_InputSystemType.Assembly.GetType("UnityEngine.InputSystem.Mouse");
                s_MouseStateType = s_InputSystemType.Assembly.GetType("UnityEngine.InputSystem.LowLevel.MouseState");

                if (s_MouseType != null && s_MouseStateType != null)
                {
                    // GetDevice<Mouse>()
                    foreach (var m in s_InputSystemType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    {
                        if (m.Name == "GetDevice" && m.IsGenericMethod && m.GetParameters().Length == 0)
                        {
                            s_MouseGetDeviceMethod = m.MakeGenericMethod(s_MouseType);
                            break;
                        }
                    }

                    // QueueStateEvent<MouseState>(InputDevice, MouseState, double)
                    foreach (var m in s_InputSystemType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    {
                        if (m.Name == "QueueStateEvent" && m.IsGenericMethod)
                        {
                            var parms = m.GetParameters();
                            if (parms.Length == 3 && parms[2].ParameterType == typeof(double))
                            {
                                s_MouseQueueStateEventMethod = m.MakeGenericMethod(s_MouseStateType);
                                break;
                            }
                        }
                    }
                }

                // Mark as resolved only after everything above succeeds.
                s_InputSystemRefResolved = true;
                return true;
            }
            catch (Exception ex)
            {
                LogInputSystemDiagLimited($"[NWB-NewInputSystem] TryResolveInputSystemRefs exception: type={ex.GetType().Name} msg={ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Parse an InputSystem.Key enum name using Enum.IsDefined/Enum.Parse,
        /// returning null on failure instead of throwing ArgumentException.
        /// </summary>
        private static object TryParseKey(string keyName)
        {
            if (Enum.IsDefined(s_KeyType, keyName))
                return Enum.Parse(s_KeyType, keyName);
            return null;
        }

        /// <summary>
        /// Map a browser key string to an InputSystem.Key enum value.
        /// Uses TryParse to avoid throwing on cross-version enum name changes.
        /// </summary>
        private static object BrowserKeyToInputSystemKey(string key)
        {
            if (string.IsNullOrEmpty(key) || s_KeyType == null) return null;

            // Single-character keys: a-z, 0-9, symbols
            if (key.Length == 1)
            {
                char c = key[0];
                if (c >= 'a' && c <= 'z') return TryParseKey(c.ToString().ToUpperInvariant());
                if (c >= 'A' && c <= 'Z') return TryParseKey(c.ToString());
                if (c >= '0' && c <= '9') return TryParseKey("Digit" + c);
                switch (c)
                {
                    case ' ': return TryParseKey("Space");
                    case '-': return TryParseKey("Minus");
                    case '=': return TryParseKey("Equals");
                    case '[': return TryParseKey("LeftBracket");
                    case ']': return TryParseKey("RightBracket");
                    case ';': return TryParseKey("Semicolon");
                    case ',': return TryParseKey("Comma");
                    case '.': return TryParseKey("Period");
                    case '/': return TryParseKey("Slash");
                    case '`': return TryParseKey("Backquote");
                    case '\\': return TryParseKey("Backslash");
                    case '\'': return TryParseKey("Quote");
                }
                return null;
            }

            switch (key)
            {
                case "Enter":
                case "Return": return TryParseKey("Enter");
                case "Tab": return TryParseKey("Tab");
                case "Escape": return TryParseKey("Escape");
                case "Backspace": return TryParseKey("Backspace");
                case "Delete": return TryParseKey("Delete");
                case "Insert": return TryParseKey("Insert");
                case "Home": return TryParseKey("Home");
                case "End": return TryParseKey("End");
                case "PageUp": return TryParseKey("PageUp");
                case "PageDown": return TryParseKey("PageDown");
                case "ArrowUp": return TryParseKey("UpArrow");
                case "ArrowDown": return TryParseKey("DownArrow");
                case "ArrowLeft": return TryParseKey("LeftArrow");
                case "ArrowRight": return TryParseKey("RightArrow");
                case "Shift": return TryParseKey("LeftShift");
                case "Control": return TryParseKey("LeftCtrl");
                case "Alt": return TryParseKey("LeftAlt");
                case "Meta": return TryParseKey("LeftMeta");
                case "CapsLock": return TryParseKey("CapsLock");
                case "F1": return TryParseKey("F1");
                case "F2": return TryParseKey("F2");
                case "F3": return TryParseKey("F3");
                case "F4": return TryParseKey("F4");
                case "F5": return TryParseKey("F5");
                case "F6": return TryParseKey("F6");
                case "F7": return TryParseKey("F7");
                case "F8": return TryParseKey("F8");
                case "F9": return TryParseKey("F9");
                case "F10": return TryParseKey("F10");
                case "F11": return TryParseKey("F11");
                case "F12": return TryParseKey("F12");
                // Numpad keys
                case "Numpad0": return TryParseKey("Numpad0");
                case "Numpad1": return TryParseKey("Numpad1");
                case "Numpad2": return TryParseKey("Numpad2");
                case "Numpad3": return TryParseKey("Numpad3");
                case "Numpad4": return TryParseKey("Numpad4");
                case "Numpad5": return TryParseKey("Numpad5");
                case "Numpad6": return TryParseKey("Numpad6");
                case "Numpad7": return TryParseKey("Numpad7");
                case "Numpad8": return TryParseKey("Numpad8");
                case "Numpad9": return TryParseKey("Numpad9");
                case "NumpadEnter": return TryParseKey("NumpadEnter");
                case "NumpadMultiply": return TryParseKey("NumpadMultiply");
                case "NumpadAdd": return TryParseKey("NumpadAdd");
                case "NumpadSubtract": return TryParseKey("NumpadSubtract");
                case "NumpadDecimal": return TryParseKey("NumpadDecimal");
                case "NumpadDivide": return TryParseKey("NumpadDivide");
                // Extended keys
                case "ContextMenu": return TryParseKey("ContextMenu");
                case "PrintScreen": return TryParseKey("PrintScreen");
                case "ScrollLock": return TryParseKey("ScrollLock");
                case "Pause": return TryParseKey("Pause");
                case "NumLock": return TryParseKey("NumLock");
                default: return null;
            }
        }

        /// <summary>
        /// Inject the current set of held keys into the new Input System via QueueStateEvent.
        /// This bypasses the native Win32 message pipeline and the isFocused check that blocks
        /// WM_INPUT in offscreen mode. Call after updating s_WinHeldKeys.
        /// </summary>
        private static void QueueInputSystemKeyboardState()
        {
            if (!TryResolveInputSystemRefs())
            {
                LogInputSystemDiagLimited("[NWB-NewInputSystem] TryResolveInputSystemRefs returned false - InputSystem package not found");
                return;
            }

            try
            {
                // Get Keyboard.current
                object keyboard = s_GetDeviceMethod.Invoke(null, null);
                if (keyboard == null) return;

                if (s_InputManagerInstanceField != null && s_InputManagerHasFocusField != null)
                {
                    object manager = s_InputManagerInstanceField.GetValue(null);
                    if (manager != null)
                    {
                        bool currentFocus = (bool)s_InputManagerHasFocusField.GetValue(manager);
                        if (!currentFocus)
                            s_InputManagerHasFocusField.SetValue(manager, true);
                    }
                }

                object state = Activator.CreateInstance(s_KeyboardStateType);
                foreach (string heldKey in s_WinHeldKeys)
                {
                    object inputKey = BrowserKeyToInputSystemKey(heldKey);
                    if (inputKey != null && s_KeySetMethod != null)
                        s_KeySetMethod.Invoke(state, new object[] { inputKey, true });
                }

                double eventTime = EditorApplication.timeSinceStartup;
                s_QueueStateEventMethod.Invoke(null, new object[] { keyboard, state, eventTime });
            }
            catch (Exception ex)
            {
                LogVerbose($"[NWB-NewInputSystem] QueueInputSystemKeyboardState error: type={ex.GetType().Name} msg={ex.Message}");
            }
        }

        /// <summary>
        /// Inject mouse position and button state into the new Input System via QueueStateEvent.
        /// Used during streaming (browser mouse events) and by the LegacyMouseBridge (after streaming).
        /// </summary>
        private static void QueueInputSystemMouseState(Vector2 screenPos, int browserButton, bool isPressed)
        {
            if (!s_InputSystemRefResolved)
            {
                if (!TryResolveInputSystemRefs()) return;
            }

            if (s_MouseGetDeviceMethod == null || s_MouseQueueStateEventMethod == null) return;

            try
            {
                object mouse = s_MouseGetDeviceMethod.Invoke(null, null);
                if (mouse == null) return;

                // Update held button set.
                if (browserButton >= 0)
                {
                    if (isPressed)
                        s_InputSystemHeldMouseButtons.Add(browserButton);
                    else
                        s_InputSystemHeldMouseButtons.Remove(browserButton);
                }

                s_InputSystemMouseScreenPos = screenPos;
                s_InputSystemMouseScreenPosValid = true;

                if (s_InputManagerInstanceField != null && s_InputManagerHasFocusField != null)
                {
                    object manager = s_InputManagerInstanceField.GetValue(null);
                    if (manager != null)
                    {
                        bool currentFocus = (bool)s_InputManagerHasFocusField.GetValue(manager);
                        if (!currentFocus)
                            s_InputManagerHasFocusField.SetValue(manager, true);
                    }
                }

                // Build MouseState with position and button bitmask.
                // buttons bitmask: bit0=left, bit1=right, bit2=middle
                // Browser button mapping: 0=left→bit0, 1=middle→bit2, 2=right→bit1
                ushort buttons = 0;
                foreach (int btn in s_InputSystemHeldMouseButtons)
                {
                    switch (btn)
                    {
                        case 0: buttons |= 0x0001; break;  // left - > bit 0
                        case 1: buttons |= 0x0004; break;  // middle - > bit 2
                        case 2: buttons |= 0x0002; break;  // right - > bit 1
                    }
                }

                object state = Activator.CreateInstance(s_MouseStateType);
                var posField = s_MouseStateType.GetField("position");
                posField?.SetValue(state, screenPos);

                var buttonsField = s_MouseStateType.GetField("buttons");
                buttonsField?.SetValue(state, buttons);

                // Set clickCount=1 on mousedown events to prevent FastMouse.MergeForward
                // from merging the mousedown (position A) with subsequent mousedrag
                // events (position B). Without this, the merged event keeps only the
                // mousedrag's position, so the game's InputReader sets
                // startingTouchPosition = mousedrag position instead of mousedown position,
                // making dragLen = 0 and preventing drag detection.
                // MergeForward skips merging when clickCount differs between events.
                if (browserButton >= 0 && isPressed)
                {
                    var clickCountField = s_MouseStateType.GetField("clickCount");
                    clickCountField?.SetValue(state, (ushort)1);
                }

                double eventTime = EditorApplication.timeSinceStartup;
                s_MouseQueueStateEventMethod.Invoke(null, new object[] { mouse, state, eventTime });
            }
            catch (Exception ex)
            {
                LogInputSystemDiagLimited($"[NWB-NewInputSystem] QueueInputSystemMouseState error: type={ex.GetType().Name} msg={ex.Message}");
            }
        }

        /// <summary>
        /// Clear all held mouse buttons and queue a neutral MouseState.
        /// Called from cleanup paths (play mode transition, stop offscreen, etc.)
        /// </summary>
        private static void ClearInputSystemMouseState()
        {
            if (s_InputSystemHeldMouseButtons.Count == 0) return;
            s_InputSystemHeldMouseButtons.Clear();
            Vector2 releasePos = s_InputSystemMouseScreenPosValid ? s_InputSystemMouseScreenPos : Vector2.zero;
            QueueInputSystemMouseState(releasePos, -1, false);
        }

        // ── Post-streaming InputSystem recovery via domain reload ──
        //
        // Root cause: After streaming stops, PollAbsoluteMousePosition() stops queuing
        // events into the native event buffer. The native NewInput state (event buffer,
        // window handle in ScreenCoordinatesToPlayerDisplayCoordinates) is corrupted.
        //
        // A domain reload fixes this because:
        //   1. Native side: RuntimeStatic<InputSystemState> is destroyed and recreated
        //      → InputEventState::Reset() clears buffers
        //   2. NewInput::Open() is called again with the correct GameView window handle
        //      → RegisterRawInput() re-registers with fresh state
        //   3. InputSystem package's InputManager re-initializes
        //      → onUpdate, onShouldRunUpdate callbacks re-registered
        //
        // Domain reload can only be triggered in Edit mode (not during Play).
        // If the user stops streaming while in Play mode, we fall back to the
        // LegacyMouseBridge which polls Input.mousePosition and injects via
        // QueueStateEvent until the native pipeline recovers.

        // SessionState key to request a domain reload after exiting Play mode.
        private const string s_PendingReloadAfterPlayKey = "Codely_NWB_PendingReloadAfterPlay";

        /// <summary>
        /// Called from StopOffscreenCapture to recover the native InputSystem pipeline.
        /// Triggers a domain reload if in Edit mode, or schedules one for after Play
        /// mode exits. Falls back to LegacyMouseBridge if domain reload is unavailable.
        /// </summary>
        internal static void RecoverInputSystemAfterStreaming()
        {
            if (!EditorApplication.isPlaying)
            {
                // Edit mode: domain reload is safe and clean.
                // It fully resets native InputSystem state (event buffers, window handles,
                // raw input registration) and re-initializes the InputSystem package.
                LogVerbose("[NWB-NewInputSystem] RecoverInputSystemAfterStreaming: requesting domain reload (Edit mode)");
                EditorApplication.delayCall += () =>
                {
                    EditorUtility.RequestScriptReload();
                };
            }
            else
            {
                // Play mode: can't domain reload.
                LogVerbose("[NWB-NewInputSystem] RecoverInputSystemAfterStreaming: in Play mode, using LegacyMouseBridge fallback");
                // Schedule a domain reload for when Play mode exits.
                SessionState.SetBool(s_PendingReloadAfterPlayKey, true);
            }
        }
#endif
    }
}
