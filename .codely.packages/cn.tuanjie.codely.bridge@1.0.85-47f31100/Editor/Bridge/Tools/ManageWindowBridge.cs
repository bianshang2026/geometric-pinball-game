using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;
using UnityTcp.Editor.Native;

#if UNITY_EDITOR_WIN
using Cn.Tuanjie.Codely.Editor;
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Bridges editor-window operations for browser/window bridge integrations via
    /// NativeWindowBridge (libdatachannel) — window listing, input, and native streaming server.
    /// </summary>
    // Auto-start the native stream server on domain reload so the stream page
    // can reconnect immediately after Unity restarts without manual intervention.
    [InitializeOnLoad]
    public static class ManageWindowBridge
    {
        private const int AutoStartRetryDelayMs = 2000;
        private const int AutoStartRetryMaxAttempts = 30;
        private const double WindowListPushDebounceSeconds = 0.1;
        private static int s_AutoStartRetryCount;
        private static bool s_AutoStartRetryStopped;
        private static bool s_WindowListChangeTrackingRegistered;
        private static bool s_WindowListPushScheduled;
        private static double s_WindowListPushDueTime;
        private static string s_LastPushedWindowListJson = "";
#if UNITY_EDITOR_WIN
        private const uint EVENT_OBJECT_CREATE = 0x8000;
        private const uint EVENT_OBJECT_DESTROY = 0x8001;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const int OBJID_WINDOW = 0;

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

        private static IntPtr s_WinEventHook = IntPtr.Zero;
        private static WinEventDelegate s_WinEventDelegate;
        private static int s_WinWindowEventPending;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
#endif

        static ManageWindowBridge()
        {
            // Explicitly reset retry state on domain reload for clarity.
            s_AutoStartRetryCount = 0;
            s_AutoStartRetryStopped = false;

            // Unhook WinEvent before domain reload / editor quit so the managed
            // delegate does not dangle after Mono tears down the old AppDomain.
            // Without this the hook callback fires into freed managed memory
            // during the post-shutdown message loop, causing a native crash.
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;

            if (!IsStreamingSupportedOnCurrentPlatform())
            {
#if UNITY_EDITOR_LINUX
                CodelyLogger.Log("[NWB] Native streaming server is disabled on Linux.");
#elif UNITY_EDITOR_OSX
                CodelyLogger.Log("[NWB] Native streaming server is disabled on macOS. ");
#endif
                return;
            }

            if (EditorAutomationGuard.ShouldSkipBridgeAutoStart())
            {
                return;
            }

            // Delay to let the editor finish loading before starting the server.
            EditorApplication.delayCall += AutoStartStreamServer;
        }

        private static void OnBeforeAssemblyReload()
        {
            // Unregister the WinEvent hook so the old managed delegate is not
            // invoked after the current AppDomain is torn down.
            UnregisterWinWindowEventHook();
        }

        private static void OnEditorQuitting()
        {
            // Stop the WebRTC streaming server BEFORE the native DLL is
            // unloaded. NWB_StopServer tears down the PeerConnection whose
            // destructor joins libdatachannel's internal processor thread.
            // Without this, native threads survive DLL unload and Mono's
            // thread manager fails to abort them — hanging the editor.
            // StopServer already runs NWB_StopServer on a background thread
            // with a timeout so the main thread is not blocked.
            NativeWindowBridgeHost.StopServer();

            // Must unhook BEFORE Application.Shutdown.CleanupMono destroys the
            // Mono runtime; otherwise PeekMessage dispatches a pending
            // WinEvent callback into freed managed thunk memory → crash.
            UnregisterWindowListChangeTracking();
        }

        /// <summary>
        /// Linux/macOS support for NativeWindowBridge streaming is not production-ready yet.
        /// Keep this hard-disabled until Linux/macOS native plugin/runtime paths are completed.
        /// </summary>
        private static bool IsStreamingSupportedOnCurrentPlatform()
        {
#if UNITY_EDITOR_LINUX
            return false;
#elif UNITY_EDITOR_OSX
            return false;
#else
            return true;
#endif
        }

        private static void AutoStartStreamServer()
        {
            if (EditorAutomationGuard.ShouldSkipBridgeAutoStart())
            {
                return;
            }

            if (!IsStreamingSupportedOnCurrentPlatform())
            {
                return;
            }

            if (!NativeWindowBridgeHost.IsAvailable(forceRefresh: true))
            {
                string reason = NativeWindowBridgeHost.GetAvailabilityReason();

                if (IsNonRetryablePluginFailure(reason))
                {
                    if (!s_AutoStartRetryStopped)
                    {
                        s_AutoStartRetryStopped = true;
                        CodelyLogger.LogError(
                            "[NWB] AutoStartStreamServer detected non-retryable plugin failure. " +
                            $"reason={reason}. Please deploy a macOS x86_64/universal NativeWindowBridge + libdatachannel.");
                    }
                    return;
                }

                s_AutoStartRetryCount++;
                if (s_AutoStartRetryCount <= AutoStartRetryMaxAttempts)
                {
                    if (s_AutoStartRetryCount == 1 || s_AutoStartRetryCount % 5 == 0)
                    {
                        CodelyLogger.LogWarning(
                            "[NWB] AutoStartStreamServer: plugin not available yet; " +
                            $"attempt={s_AutoStartRetryCount}/{AutoStartRetryMaxAttempts}, reason={reason}. " +
                            $"Retrying in {AutoStartRetryDelayMs}ms...");
                    }

                    EditorApplication.delayCall += () =>
                    {
                        System.Threading.Tasks.Task.Delay(AutoStartRetryDelayMs).ContinueWith(_ =>
                            EditorApplication.delayCall += AutoStartStreamServer);
                    };
                }
                else if (!s_AutoStartRetryStopped)
                {
                    s_AutoStartRetryStopped = true;
                    CodelyLogger.LogWarning(
                        "[NWB] AutoStartStreamServer stopped retrying after max attempts. " +
                        $"reason={reason}. You can still call start_stream_server manually after fixing plugin loading.");
                }
                return;
            }

            // Reset retry state once plugin becomes available.
            s_AutoStartRetryCount = 0;
            s_AutoStartRetryStopped = false;

            // Use port=0 for OS-assigned dynamic port to avoid conflicts
            // when multiple Unity Editor instances run on the same machine.
            if (NativeWindowBridgeHost.StartOrAttach(0, out int boundPort))
            {
                PushWindowListToNative(force: true);
                RegisterWindowListChangeTracking();
                CodelyLogger.Log("[NWB] AutoStartStreamServer success on port " + boundPort);
            }
            else
            {
                CodelyLogger.LogWarning(
                    "[NWB] AutoStartStreamServer failed (dynamic port). " +
                    "detail=" + NativeWindowBridgeHost.GetAvailabilityReason());
            }
        }

        private static bool IsNonRetryablePluginFailure(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return false;
            }

            string normalized = reason.ToLowerInvariant();
            // Architecture mismatch will never recover by waiting; retry only adds log noise.
            return normalized.Contains("wrong architecture")
                || normalized.Contains("incompatible architecture")
                || normalized.Contains("mach-o file, but is an incompatible architecture")
                || normalized.Contains("no suitable image found");
        }

        private static readonly Dictionary<string, Func<JObject, object>> ActionHandlers =
            new Dictionary<string, Func<JObject, object>>
            {
                { "list_windows", _ => ListWindows() },
                { "focus_window", FocusWindow },
                { "resolve_native_window", ResolveNativeWindow },
                { "input", SendInput },
                { "start_stream_server", StartStreamServer },
                { "stop_stream_server", _ => StopStreamServer() },
                { "get_stream_server_status", _ => GetStreamServerStatus() },
                { "start_offscreen_stream", StartOffscreenStream },
                { "stop_offscreen_stream", _ => StopOffscreenStream() },
            };

        public static object HandleCommand(JObject @params)
            => ActionRouter.Route(@params, ActionHandlers);

        private static object ListWindows()
        {
            try
            {
                EditorWindow[] allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
                var windows = new List<object>(allWindows.Length);

                foreach (EditorWindow window in allWindows)
                {
                    if (window == null)
                    {
                        continue;
                    }

                    string nativeHandle = GetNativeWindowHandle(window);
                    bool docked = NativeWindowBridgeHost.IsWindowDocked(window);

                    // Unity position: logical points, top-left origin.
                    // Pass as-is to native side; C++ does coordinate conversion using
                    // actual display bounds (handles Retina scaling correctly).
                    Rect pos = window.position;

                    string viewType = "";
                    string fullType = window.GetType().FullName;
                    if (fullType == "UnityEditor.GameView") viewType = "game_view";
                    else if (fullType == "UnityEditor.SceneView") viewType = "scene_view";
                    else if (fullType == "UnityEditor.InspectorWindow") viewType = "inspector";
                    else if (fullType == "UnityEditor.ProjectBrowser") viewType = "project";
                    else if (fullType == "UnityEditor.ConsoleWindow") viewType = "console";
                    else if (fullType == "UnityEditor.SceneHierarchyWindow") viewType = "hierarchy";

                    long instanceId = window.GetStableInstanceId();
                    int x = Mathf.RoundToInt(pos.x);
                    int y = Mathf.RoundToInt(pos.y);
                    int w = Mathf.RoundToInt(pos.width);
                    int h = Mathf.RoundToInt(pos.height);

                    windows.Add(
                        new
                        {
                            // New schema fields.
                            windowId = instanceId,
                            instanceID = instanceId,
                            title = window.titleContent?.text ?? string.Empty,
                            typeName = fullType,
                            viewType,
                            isFocused = EditorWindow.focusedWindow == window,
                            docked,
                            position = new
                            {
                                x = pos.x,
                                y = pos.y,
                                width = pos.width,
                                height = pos.height,
                            },

                            // Legacy flat fields kept for native parser compatibility.
                            // NativeWindowBridge.cpp currently parses: id/type/x/y/w/h.
                            id = instanceId,
                            type = fullType,
                            x,
                            y,
                            w,
                            h,

                            nativeWindowHandle = nativeHandle,
                            supportsNativeHandle = !string.IsNullOrEmpty(nativeHandle),
                            supportsInputInjection = true,
                            supportsRegionCapture = docked,
                        }
                    );
                }

                return Response.Success("Window list retrieved.", windows);
            }
            catch (Exception ex)
            {
                return Response.Error($"Failed to list editor windows: {ex.Message}");
            }
        }

        private static object FocusWindow(JObject @params)
        {
            EditorWindow target = FindTargetWindow(@params);
            if (target == null)
            {
                return Response.Error("Target window not found.");
            }

            try
            {
                target.Show();
                target.Focus();
                target.Repaint();
                return Response.Success(
                    "Window focused.",
                    new
                    {
                        windowId = target.GetStableInstanceId(),
                        title = target.titleContent?.text ?? string.Empty,
                        typeName = target.GetType().FullName,
                        isFocused = EditorWindow.focusedWindow == target,
                    }
                );
            }
            catch (Exception ex)
            {
                return Response.Error($"Failed to focus window: {ex.Message}");
            }
        }

        private static object ResolveNativeWindow(JObject @params)
        {
            EditorWindow target = FindTargetWindow(@params);
            if (target == null)
            {
                return Response.Error("Target window not found.");
            }

            string nativeWindowHandle = GetNativeWindowHandle(target);
            if (string.IsNullOrEmpty(nativeWindowHandle))
            {
                return Response.Error(
                    "Native window handle is unavailable on this platform or Unity state."
                );
            }

            return Response.Success(
                "Native window handle resolved.",
                new
                {
                    windowId = target.GetStableInstanceId(),
                    nativeWindowHandle,
                }
            );
        }

        private static object SendInput(JObject @params)
        {
            string eventType = @params?["eventType"]?.ToString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(eventType))
            {
                return Response.Error("'eventType' parameter is required for input action.");
            }

            EditorWindow target = FindTargetWindow(@params);
            if (target == null)
            {
                return Response.Error("Target window not found for input action.");
            }

            target.Show();
            target.Focus();

            if (eventType == "focus")
            {
                target.Repaint();
                return Response.Success(
                    "Focus input applied.",
                    new { windowId = target.GetStableInstanceId(), eventType }
                );
            }

            Event evt = BuildInputEvent(eventType, @params);
            if (evt == null)
            {
                return Response.Error($"Unsupported input eventType: {eventType}");
            }

            target.SendEvent(evt);
            target.Repaint();
            return Response.Success(
                "Input event dispatched.",
                new { windowId = target.GetStableInstanceId(), eventType }
            );
        }

        private static Event BuildInputEvent(string eventType, JObject @params)
        {
            Event evt = new Event
            {
                mousePosition = new Vector2(
                    @params?["x"]?.ToObject<float?>() ?? 0f,
                    @params?["y"]?.ToObject<float?>() ?? 0f
                ),
                button = @params?["button"]?.ToObject<int?>() ?? 0,
                modifiers = ReadModifiers(@params),
            };

            switch (eventType)
            {
                case "mousemove":
                    evt.type = EventType.MouseMove;
                    return evt;
                case "mousedown":
                    evt.type = EventType.MouseDown;
                    return evt;
                case "mouseup":
                    evt.type = EventType.MouseUp;
                    return evt;
                case "wheel":
                    evt.type = EventType.ScrollWheel;
                    evt.delta = new Vector2(
                        @params?["deltaX"]?.ToObject<float?>() ?? 0f,
                        @params?["deltaY"]?.ToObject<float?>() ?? 0f
                    );
                    return evt;
                case "keydown":
                    evt.type = EventType.KeyDown;
                    evt.keyCode = ParseKeyCode(@params?["code"]?.ToString(), @params?["key"]?.ToString());
                    evt.character = ReadCharacter(@params?["key"]?.ToString(), @params?["text"]?.ToString());
                    return evt;
                case "keyup":
                    evt.type = EventType.KeyUp;
                    evt.keyCode = ParseKeyCode(@params?["code"]?.ToString(), @params?["key"]?.ToString());
                    evt.character = ReadCharacter(@params?["key"]?.ToString(), @params?["text"]?.ToString());
                    return evt;
                case "textinput":
                    evt.type = EventType.KeyDown;
                    evt.keyCode = KeyCode.None;
                    evt.character = ReadCharacter(@params?["key"]?.ToString(), @params?["text"]?.ToString());
                    return evt;
                default:
                    return null;
            }
        }

        private static EventModifiers ReadModifiers(JObject @params)
        {
            EventModifiers modifiers = EventModifiers.None;
            if (@params?["alt"]?.ToObject<bool?>() == true) modifiers |= EventModifiers.Alt;
            if (@params?["ctrl"]?.ToObject<bool?>() == true) modifiers |= EventModifiers.Control;
            if (@params?["shift"]?.ToObject<bool?>() == true) modifiers |= EventModifiers.Shift;
            if (@params?["meta"]?.ToObject<bool?>() == true) modifiers |= EventModifiers.Command;
            return modifiers;
        }

        private static char ReadCharacter(string key, string text)
        {
            string candidate = !string.IsNullOrEmpty(text) ? text : key;
            return string.IsNullOrEmpty(candidate) ? '\0' : candidate[0];
        }

        private static KeyCode ParseKeyCode(string code, string key)
        {
            if (!string.IsNullOrEmpty(code) && Enum.TryParse(code, true, out KeyCode fromCode))
            {
                return fromCode;
            }
            if (!string.IsNullOrEmpty(key) && Enum.TryParse(key, true, out KeyCode fromKey))
            {
                return fromKey;
            }
            return KeyCode.None;
        }

        private static EditorWindow FindTargetWindow(JObject @params)
        {
            long? instanceId = @params?["instanceID"]?.ToObject<long?>() ?? @params?["windowId"]?.ToObject<long?>();
            string windowType = @params?["windowType"]?.ToString();
            string title = @params?["title"]?.ToString();

            EditorWindow[] allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();

            if (instanceId.HasValue)
            {
                EditorWindow byId = allWindows.FirstOrDefault(w => w != null && w.GetStableInstanceId() == instanceId.Value);
                if (byId != null) return byId;
            }

            if (!string.IsNullOrEmpty(windowType))
            {
                EditorWindow byType = allWindows.FirstOrDefault(w =>
                    w != null &&
                    (
                        string.Equals(w.GetType().FullName, windowType, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(w.GetType().Name, windowType, StringComparison.OrdinalIgnoreCase)
                    )
                );
                if (byType != null) return byType;
            }

            if (!string.IsNullOrEmpty(title))
            {
                EditorWindow byTitle = allWindows.FirstOrDefault(w =>
                    w != null &&
                    string.Equals(w.titleContent?.text ?? string.Empty, title, StringComparison.OrdinalIgnoreCase)
                );
                if (byTitle != null) return byTitle;
            }

            return null;
        }

        private static string GetNativeWindowHandle(EditorWindow window)
        {
#if UNITY_EDITOR_WIN
            IntPtr hwnd = NativeWindowHelper.GetHWND(window);
            if (hwnd == IntPtr.Zero)
            {
                return string.Empty;
            }
            return "0x" + hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture);
#else
            _ = window;
            return string.Empty;
#endif
        }

        // -------------------------------------------------------------------
        // Native streaming server (NativeWindowBridge C++ plugin)
        // -------------------------------------------------------------------

        private const int DefaultStreamServerPort = 10087;

        /// <summary>
        /// Start or re-attach to the native HTTP streaming server.
        /// Also pushes the current EditorWindow list into the native layer.
        /// </summary>
        private static object StartStreamServer(JObject @params)
        {
            if (!IsStreamingSupportedOnCurrentPlatform())
            {
                return Response.Error(
                    "Native streaming server is disabled on this platform. ");
            }

            if (!NativeWindowBridgeHost.IsAvailable(forceRefresh: true))
            {
                CodelyLogger.LogWarning("[NWB] StartStreamServer aborted: plugin unavailable. " +
                    NativeWindowBridgeHost.GetAvailabilityReason());
                return Response.Error(
                    "NativeWindowBridge plugin not available: " +
                    NativeWindowBridgeHost.GetAvailabilityReason());
            }

            // Default to port=0 (OS-assigned) to avoid conflicts with multiple
            // Unity Editor instances. Callers can still pass a fixed port explicitly.
            int port = @params?["port"]?.Value<int>() ?? 0;
            bool allowPortFallback = @params?["allowPortFallback"]?.Value<bool>() ?? false;
            int requestedPort = (port > 0 && allowPortFallback) ? FindFirstAvailablePort(port, 20) : port;
            if (!NativeWindowBridgeHost.StartOrAttach(requestedPort, out int boundPort))
            {
                CodelyLogger.LogError("[NWB] StartStreamServer failed. requestedPort=" + requestedPort +
                    ", detail=" + NativeWindowBridgeHost.GetAvailabilityReason());
                return Response.Error(
                    $"Failed to start native stream server (requestedPort={requestedPort}). " +
                    $"Detail: {NativeWindowBridgeHost.GetAvailabilityReason()}.");
            }

            // Tell native side our PID so it can enumerate OS-native windows (CGWindowID).
            NativeWindowBridgeHost.SetUnityPid(System.Diagnostics.Process.GetCurrentProcess().Id);

            // Push Unity metadata (InstanceID, type names) for best-effort merging.
            PushWindowListToNative(force: true);
            RegisterWindowListChangeTracking();

            string signalingUrl = $"http://127.0.0.1:{boundPort}";
            CodelyLogger.Log("[NWB] StartStreamServer success. requestedPort=" + requestedPort +
                ", boundPort=" + boundPort + ", url=" + signalingUrl);
            return Response.Success("Native stream server started.",
                new
                {
                    signalingUrl,
                    port = boundPort,
                    running = true,
                    requestedPort,
                });
        }

        /// <summary>
        /// Find the first available local TCP port in [startPort, startPort + maxOffset].
        /// Falls back to the original startPort when probing fails.
        /// </summary>
        private static int FindFirstAvailablePort(int startPort, int maxOffset)
        {
            // port=0 means OS-assigned dynamic port; skip probing entirely.
            if (startPort <= 0)
            {
                return 0;
            }

            int upper = Math.Max(startPort, startPort + Math.Max(0, maxOffset));
            for (int candidate = startPort; candidate <= upper; candidate++)
            {
                if (IsLocalPortAvailable(candidate))
                {
                    return candidate;
                }
            }

            return startPort;
        }

        /// <summary>
        /// Probe whether a TCP port can be bound on loopback.
        /// </summary>
        private static bool IsLocalPortAvailable(int port)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try
                {
                    listener?.Stop();
                }
                catch
                {
                    // Ignore stop errors in probe path.
                }
            }
        }

        private static object StopStreamServer()
        {
            UnregisterWindowListChangeTracking();
            s_LastPushedWindowListJson = "";
            NativeWindowBridgeHost.StopServer();
            return Response.Success("Native stream server stopped.",
                new { running = false });
        }

        private static object GetStreamServerStatus()
        {
            bool platformSupported = IsStreamingSupportedOnCurrentPlatform();
            bool running = platformSupported && NativeWindowBridgeHost.IsRunning();
            int port = running ? NativeWindowBridgeHost.GetBoundPort() : 0;
            string streamStatus = running ? NativeWindowBridgeHost.GetStreamStatusJson() : "";
            return Response.Success("Stream server status.",
                new
                {
                    running,
                    port,
                    streamStatus,
                    platformSupported,
#if UNITY_EDITOR_WIN
                    platform = "windows",
#elif UNITY_EDITOR_OSX
                    platform = "macos",
#elif UNITY_EDITOR_LINUX
                    platform = "linux",
#else
                    platform = "unknown",
#endif
                });
        }

        /// <summary>
        /// Collect EditorWindow info and push to native as JSON array.
        /// </summary>
        internal static bool PushWindowListToNative(bool force = false)
        {
            try
            {
                var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
                var sb = new StringBuilder("[");
                bool first = true;
                foreach (var w in windows)
                {
                    if (w == null) continue;
                    var pos = w.position;
                    long instanceId = w.GetStableInstanceId();
                    string title = w.titleContent?.text ?? "";
                    string typeName = w.GetType().FullName ?? w.GetType().Name;
                    if (IsStreamControlWindow(w, title, typeName)) continue;

                    if (!first) sb.Append(',');
                    first = false;

                    string viewType = "";
                    if (typeName == "UnityEditor.GameView") viewType = "game_view";
                    else if (typeName == "UnityEditor.SceneView") viewType = "scene_view";
                    else if (typeName == "UnityEditor.InspectorWindow") viewType = "inspector";
                    else if (typeName == "UnityEditor.ProjectBrowser") viewType = "project";
                    else if (typeName == "UnityEditor.ConsoleWindow") viewType = "console";
                    else if (typeName == "UnityEditor.SceneHierarchyWindow") viewType = "hierarchy";

                    // Pass Unity coordinates as-is (logical points, top-left origin).
                    // C++ side converts to macOS bottom-left coords using display bounds.
                    sb.Append('{');
                    sb.AppendFormat("\"id\":{0}", instanceId);
                    sb.AppendFormat(",\"title\":\"{0}\"", EscapeJson(title));
                    sb.AppendFormat(",\"type\":\"{0}\"", EscapeJson(typeName));
                    sb.AppendFormat(",\"viewType\":\"{0}\"", viewType);
                    sb.AppendFormat(",\"docked\":{0}", NativeWindowBridgeHost.IsWindowDocked(w) ? "true" : "false");
                    sb.AppendFormat(",\"x\":{0}", (int)pos.x);
                    sb.AppendFormat(",\"y\":{0}", (int)pos.y);
                    sb.AppendFormat(",\"w\":{0}", (int)pos.width);
                    sb.AppendFormat(",\"h\":{0}", (int)pos.height);
                    sb.Append('}');
                }
                sb.Append(']');
                string windowsJson = sb.ToString();
                if (!force && windowsJson == s_LastPushedWindowListJson)
                {
                    return false;
                }

                if (NativeWindowBridgeHost.UpdateWindowList(windowsJson))
                {
                    s_LastPushedWindowListJson = windowsJson;
                    return true;
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB] PushWindowListToNative failed: {ex.Message}");
            }
            return false;
        }

        private static bool IsStreamControlWindow(EditorWindow window, string title, string typeName)
        {
            if (window == null) return false;
            if (typeName == "UnityTcp.Editor.Native.StreamingMaskWindow") return true;
            return title.EndsWith(" - Streaming", StringComparison.Ordinal);
        }

        private static void RegisterWindowListChangeTracking()
        {
            if (s_WindowListChangeTrackingRegistered)
            {
                return;
            }

            s_WindowListChangeTrackingRegistered = true;
            RegisterWinWindowEventHook();
        }

        private static void UnregisterWindowListChangeTracking()
        {
            if (!s_WindowListChangeTrackingRegistered)
            {
                return;
            }

            s_WindowListChangeTrackingRegistered = false;
            UnregisterWinWindowEventHook();
            if (s_WindowListPushScheduled)
            {
                EditorApplication.update -= PushQueuedWindowListToNativeTick;
                s_WindowListPushScheduled = false;
            }
        }

        private static void RegisterWinWindowEventHook()
        {
#if UNITY_EDITOR_WIN
            if (s_WinEventHook != IntPtr.Zero)
            {
                return;
            }

            try
            {
                s_WinEventDelegate = OnWinWindowEvent;
                uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                s_WinEventHook = SetWinEventHook(
                    EVENT_OBJECT_CREATE,
                    EVENT_OBJECT_DESTROY,
                    IntPtr.Zero,
                    s_WinEventDelegate,
                    pid,
                    0,
                    WINEVENT_OUTOFCONTEXT);
                if (s_WinEventHook == IntPtr.Zero)
                {
                    CodelyLogger.LogWarning("[NWB] SetWinEventHook(EVENT_OBJECT_CREATE/DESTROY) failed; dynamic window auto-add notifications are disabled.");
                    return;
                }

                s_WinWindowEventPending = 0;
                EditorApplication.update += ProcessWinWindowEventTick;
                CodelyLogger.Log("[NWB] Registered WinEvent hook for Unity editor window create/destroy.");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB] Register WinEvent hook failed: {ex.Message}");
                s_WinEventHook = IntPtr.Zero;
                s_WinEventDelegate = null;
            }
#else
            CodelyLogger.LogWarning("[NWB] Dynamic window auto-add notifications currently require Win32 SetWinEventHook.");
#endif
        }

        private static void UnregisterWinWindowEventHook()
        {
#if UNITY_EDITOR_WIN
            EditorApplication.update -= ProcessWinWindowEventTick;
            s_WinWindowEventPending = 0;
            if (s_WinEventHook == IntPtr.Zero)
            {
                return;
            }

            try
            {
                UnhookWinEvent(s_WinEventHook);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB] Unregister WinEvent hook failed: {ex.Message}");
            }
            finally
            {
                s_WinEventHook = IntPtr.Zero;
                s_WinEventDelegate = null;
            }
#endif
        }

#if UNITY_EDITOR_WIN
        private static void OnWinWindowEvent(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != 0)
            {
                return;
            }

            if (eventType != EVENT_OBJECT_CREATE && eventType != EVENT_OBJECT_DESTROY)
            {
                return;
            }

            System.Threading.Interlocked.Exchange(ref s_WinWindowEventPending, 1);
        }
#endif

        private static void ProcessWinWindowEventTick()
        {
#if UNITY_EDITOR_WIN
            if (System.Threading.Interlocked.Exchange(ref s_WinWindowEventPending, 0) == 0)
            {
                return;
            }

            QueueWindowListPush();
#endif
        }

        private static void QueueWindowListPush()
        {
            if (!NativeWindowBridgeHost.IsRunning())
            {
                UnregisterWindowListChangeTracking();
                return;
            }

            s_WindowListPushDueTime = EditorApplication.timeSinceStartup + WindowListPushDebounceSeconds;
            if (s_WindowListPushScheduled)
            {
                return;
            }

            s_WindowListPushScheduled = true;
            EditorApplication.update += PushQueuedWindowListToNativeTick;
        }

        private static void PushQueuedWindowListToNativeTick()
        {
            if (!NativeWindowBridgeHost.IsRunning())
            {
                UnregisterWindowListChangeTracking();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now < s_WindowListPushDueTime)
            {
                return;
            }

            EditorApplication.update -= PushQueuedWindowListToNativeTick;
            s_WindowListPushScheduled = false;

            // Auto-dock new popup windows into the composite DockArea before
            // pushing the window list, so the list already reflects docked state.
            bool docked = NativeWindowBridgeHost.InterceptAndDockNewPopupWindows();

            if (PushWindowListToNative())
            {
                bool notified = NativeWindowBridgeHost.NotifyEditorWindowsChanged();
                CodelyLogger.Log($"[NWB] Editor window list changed via WinEventHook; notifiedFrontend={notified} autoDocked={docked}");
            }
        }

        private static object StartOffscreenStream(JObject @params)
        {
            if (!IsStreamingSupportedOnCurrentPlatform())
            {
                return Response.Error(
                    "Offscreen stream is disabled on this platform. ");
            }

            if (!NativeWindowBridgeHost.IsAvailable())
            {
                return Response.Error(
                    "NativeWindowBridge plugin not available: " +
                    NativeWindowBridgeHost.GetAvailabilityReason());
            }

            if (!NativeWindowBridgeHost.IsRunning())
            {
                return Response.Error("Stream server not running. Call start_stream_server first.");
            }

            string windowType = @params?["windowType"]?.ToString() ?? "UnityEditor.GameView";
            int fps = @params?["fps"]?.Value<int>() ?? 30;
            int width = @params?["width"]?.Value<int>() ?? 0;
            int height = @params?["height"]?.Value<int>() ?? 0;

            bool ok = NativeWindowBridgeHost.StartOffscreenCapture(windowType, fps, width, height);
            return ok
                ? Response.Success("Offscreen capture started.",
                    new { windowType, fps, width, height, mode = "offscreen" })
                : Response.Error("Failed to start offscreen capture.");
        }

        private static object StopOffscreenStream()
        {
            // Browser stop should only disconnect P2P/native capture while
            // keeping Unity hidden. Full visual restore is explicitly triggered
            // by the mask window's "Disconnect & Restore" action.
            NativeWindowBridgeHost.DisconnectOffscreenKeepHidden();
            return Response.Success("Offscreen capture disconnected (hidden state kept).",
                new { mode = "disconnected_hidden" });
        }

        // Escape JSON string values including Unicode control characters (U+0000–U+001F).
        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
