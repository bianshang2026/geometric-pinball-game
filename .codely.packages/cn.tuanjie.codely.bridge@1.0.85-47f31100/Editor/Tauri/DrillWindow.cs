#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.InternalBridge;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;
namespace Cn.Tuanjie.Codely.Editor
{
    /// <summary>
    /// Walkthrough window that opens the Tauri server at the / route.
    /// Manages the Tauri process independently via SessionState; when CodelyWindow is
    /// already running it will naturally reuse the same process through the shared
    /// SESSION_STATE_TAURI_PORT_KEY / SESSION_STATE_TAURI_PID_KEY values.
    /// </summary>
    public class DrillWindow : EditorWindow
    {
        #region Constants

        private const string DRILL_ROUTE = "/?route=/drill";

        /// <summary>
        /// SessionState key tracking whether the auto-open-on-load check has already run
        /// this editor session. Without this guard every domain reload (script recompile)
        /// would re-pop the walkthrough window for users who haven't completed it yet.
        /// </summary>
        private const string SESSION_STATE_DRILL_AUTO_OPENED_KEY = "Codely_DrillAutoOpened";

        #endregion

        #region Auto-Open On Load

        /// <summary>
        /// On editor load (startup / domain reload), open the walkthrough once per editor
        /// session if <c>~/.codely/data.json</c> reports <c>drillCompleted: false</c>.
        /// Skipped when the cowork runnable is missing, when a CodelyWindow or DrillWindow
        /// is already open, or when this session has already attempted the auto-open.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void AutoOpenOnLoadIfDrillIncomplete()
        {
            // Never auto-pop the walkthrough during unattended editor runs (batch mode / -nographics
            // CI / QA automation). Opening the window there triggers a native 'SUCCEEDED(hr)'
            // assertion that flips otherwise-passing test runs to FAIL.
            if (TauriUtils.IsAutomatedEditorRun())
                return;

            if (SessionState.GetBool(SESSION_STATE_DRILL_AUTO_OPENED_KEY, false))
                return;
            // Defer until the editor's first idle tick so EditorWindow operations are safe.
            EditorApplication.delayCall += () =>
            {
                if (SessionState.GetBool(SESSION_STATE_DRILL_AUTO_OPENED_KEY, false))
                    return;
                SessionState.SetBool(SESSION_STATE_DRILL_AUTO_OPENED_KEY, true);
                if (string.IsNullOrEmpty(TauriUtils.GetCodelyRunnable()))
                    return;

                if (HasOpenInstances<DrillWindow>())
                    return;

                if (TauriUtils.IsDrillCompleted())
                    return;

                OpenWalkThrough();
            };
        }

        #endregion

        #region Fields

        // Short-timeout client for single-instance APP discovery probes
        // (GET /api/tauri/status, POST /api/tauri/attach-embed-workspace).
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3),
        };

        private Process _tauriProcess;
        private int     _tauriServerPort = -1;
        private bool    _serverReady     = false;
        private bool    _isWindowVisible = true;
        private IntPtr  _lastWindowParentHandle = IntPtr.Zero;

        private string _ipcOwnerId;
        private bool _ipcAcquired;
        private bool _ipcEventsSubscribed;

        [SerializeField] private bool _eventTrackedOnEnable = false;
        private bool _windowClosedEventSent;
        private bool _drillCompletionHandled;

#if UNITY_EDITOR_WIN
        private WebView2Host _webView2Host;
        private IntPtr _currentGUIViewHWND  = IntPtr.Zero;
        private bool   _webView2Initializing = false;
        private bool   _webView2Initialized  = false;
        private int    _lastTopInsetPx       = -1;
        private int    _cachedTopInsetPx     = -1;
#endif

#if UNITY_EDITOR_OSX
        private MacOSWebViewBridge _webViewBridge;
        private Vector2 _lastWindowSize;
        private bool    _webViewInitialized = false;
        [SerializeField] private long _serializedWebViewHandle      = 0;
        [SerializeField] private bool _serializedWebViewInitialized = false;
#endif

        #endregion

        #region Menu Command
        [MenuItem("AI/Open Walkthrough", priority = 997)]
        public static void OpenWalkThrough()
        {
            // Guard the actual window-creation call site: under batch mode / -nographics automation,
            // GetWindow forces native window + graphics-surface creation that fails with a
            // 'SUCCEEDED(hr)' native assertion. Stay closed regardless of which caller reached here.
            if (TauriUtils.IsAutomatedEditorRun())
                return;

            // Without the cowork runnable the DrillWindow WebView has nothing to
            // load and would sit forever on "Loading WalkThrough...". Fall back to
            // CodelyWindow, which surfaces the install/setup UI instead.
            if (string.IsNullOrEmpty(TauriUtils.GetCodelyRunnable()))
            {
                CodelyWindow.ShowWindow();
                return;
            }

            var window = GetWindow<DrillWindow>("Tuanjie AI WalkThrough", true);
            window.minSize = new Vector2(1200, 800);
            window.ApplyTitleIcon();
            window.Show();
        }

        #endregion

        #region EditorWindow Lifecycle

        private static Texture2D LoadIcon(string fileName) =>
            AssetDatabase.LoadAssetAtPath<Texture2D>(TauriUtils.TAURI_ICONS_BASE_PATH + fileName);

        private void ApplyTitleIcon()
        {
            // Unity title bar uses fixed icon size; use theme-specific asset for visibility.
            var icon = EditorGUIUtility.isProSkin
                ? LoadIcon("title_icon.png")
                : LoadIcon("title_icon_light_theme.png");

            if (icon == null)
                return;

            titleContent = new GUIContent(" Tuanjie AI WalkThrough", icon);
        }

        private void OnEnable()
        {
            if (TauriUtils.IsAutomatedEditorRun()) return;

            if (!_eventTrackedOnEnable)
            {
                _eventTrackedOnEnable = true;
                CodelyEventTracker.Track(CodelyEventTracker.WelcomeWindowShown);
            }

            ApplyTitleIcon();
            _lastWindowParentHandle = GetEditorWindowHandle();

            // Try to reuse a Tauri process that CodelyWindow (or a previous DrillWindow) already started
            bool restored = TryRestoreTauriProcess();
            if (!restored)
            {
                restored = TryAttachFromLockFile();
            }

            AcquireIpcClient();

            if (!restored)
            {
                EnsureTauriProcess();
            }

#if UNITY_EDITOR_OSX
            if (_webViewBridge == null)
            {
                _webViewBridge     = new MacOSWebViewBridge();
                _webViewInitialized = false;
            }

            if (_serializedWebViewInitialized && _serializedWebViewHandle != 0)
            {
                var savedHandle = new IntPtr(_serializedWebViewHandle);
                try
                {
                    if (_webViewBridge.RestoreFromHandle(savedHandle))
                    {
                        _webViewInitialized        = true;
                        _serializedWebViewHandle      = 0;
                        _serializedWebViewInitialized = false;
                        _webViewBridge.SetHidden(false);
                        UpdateWebViewFrame();
                    }
                    else
                    {
                        _serializedWebViewHandle      = 0;
                        _serializedWebViewInitialized = false;
                    }
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"DrillWindow: Exception restoring WebView: {ex.Message}");
                    _serializedWebViewHandle      = 0;
                    _serializedWebViewInitialized = false;
                }
            }
            else if (_webViewBridge.IsInitialized)
            {
                _webViewInitialized = true;
                _webViewBridge.SetHidden(false);
                UpdateWebViewFrame();
            }

            if (_serverReady && !_webViewInitialized)
            {
                InitializeWebView();
            }
#else
            _webView2Initialized = false;
            _currentGUIViewHWND  = IntPtr.Zero;

            if (_serverReady)
            {
                TryInitializeWebView2();
            }
#endif

            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;

            // Must fire before ReleaseIpcClient: OnDisable runs before OnDestroy and
            // tears down the IPC client when DrillWindow is the last owner.
            TryNotifyWindowClosed();

            ReleaseIpcClient();

#if UNITY_EDITOR_WIN
            _webView2Host?.SetVisible(false);
#endif

#if UNITY_EDITOR_OSX
            if (_webViewBridge != null && _webViewBridge.IsInitialized)
            {
                try
                {
                    IntPtr handle = _webViewBridge.Handle;
                    if (handle != IntPtr.Zero)
                    {
                        _serializedWebViewHandle      = handle.ToInt64();
                        _serializedWebViewInitialized = true;
                    }
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"DrillWindow: Failed to serialize WebView handle: {ex.Message}");
                }
            }
#endif
        }

        private void OnDestroy()
        {
            TryNotifyWindowClosed();
            ReleaseIpcClient();

#if UNITY_EDITOR_WIN
            DisposeWebView2Host();
#endif

#if UNITY_EDITOR_OSX
            _serializedWebViewHandle      = 0;
            _serializedWebViewInitialized = false;

            var webViewToDestroy = _webViewBridge;
            _webViewBridge = null;

            if (webViewToDestroy != null && webViewToDestroy.IsInitialized)
            {
                EditorApplication.delayCall += () =>
                {
                    try { webViewToDestroy.Destroy(); }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogWarning($"DrillWindow: Error destroying WebView: {ex.Message}");
                    }
                };
            }
#endif

            // DrillWindow does not kill the Tauri process on close; CodelyWindow owns process lifecycle.
            _tauriProcess    = null;
            _tauriServerPort = -1;

            CodelyLogger.Log("DrillWindow: OnDestroy called");
        }

        internal void PrepareForPackageRemovalOrUpgrade()
        {
            ReleaseIpcClient();

#if UNITY_EDITOR_WIN
            DisposeWebView2Host();
#endif

#if UNITY_EDITOR_OSX
            if (_webViewBridge != null && _webViewBridge.IsInitialized)
            {
                _webViewBridge.Destroy();
            }
            _webViewBridge = null;
            _webViewInitialized = false;
            _serializedWebViewHandle = 0;
            _serializedWebViewInitialized = false;
#endif
        }

        private void OnGUI()
        {
#if UNITY_EDITOR_OSX
            var webViewRect = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            if (_webViewBridge == null || !_webViewBridge.IsInitialized)
            {
                GUI.Box(webViewRect, "Loading WalkThrough...");
            }
#else
            var embedRect = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            Vector2 contentOrigin = GUIUtility.GUIToScreenPoint(Vector2.zero);
            int insetPx = Mathf.Max(0, Mathf.RoundToInt(
                (contentOrigin.y - position.y) * EditorGUIUtility.pixelsPerPoint));
            _cachedTopInsetPx = insetPx;

            if (CodelyIpcClient.ConsumePendingDrillCompleted())
            {
                CompleteDrillFromOnGUI();
                return;
            }

            if (!_webView2Initialized)
            {
                GUI.Box(embedRect, "Loading WalkThrough...");

                // Focus-independent backstop: 失焦启动时 delayCall 被节流，server_ready 事件链
                // 会卡住。OnGUI 仍会在 WM_PAINT 时跑（CodelyIpcServer.WakeUnityMainThread 强制
                // 触发了 InvalidateRect），所以这里直接读 IPC 后台线程缓存的 URL，解出端口后
                // 立刻调 TryInitializeWebView2，不依赖事件链。
                if (Event.current.type == EventType.Layout && !_webView2Initializing)
                {
                    // The IPC server_ready URL is authoritative for the actually-bound
                    // port. The value optimistically stored at launch can be stale — the
                    // bound port may have been bumped, or a duplicate process killed by
                    // single-instance never bound at all. Prefer the IPC port whenever it
                    // is available so we never point WebView2 at a dead port
                    // (the ERR_CONNECTION_REFUSED bug).
                    string pendingUrl = CodelyIpcManager.LastServerReadyUrl;
                    if (!string.IsNullOrEmpty(pendingUrl) &&
                        Uri.TryCreate(pendingUrl, UriKind.Absolute, out var pendingUri) &&
                        pendingUri.Port > 0)
                    {
                        if (_tauriServerPort != pendingUri.Port)
                        {
                            CodelyLogger.Log($"[DrillWindow] OnGUI backstop: using IPC server_ready port={pendingUri.Port} (was {_tauriServerPort})");
                            _tauriServerPort = pendingUri.Port;
                        }
                        _serverReady = true;
                    }
                    if (_tauriServerPort > 0)
                    {
                        TryInitializeWebView2();
                    }
                }
            }

            if (_webView2Host != null && _webView2Host.IsReady)
            {
                if (insetPx != _lastTopInsetPx)
                {
                    _lastTopInsetPx = insetPx;
                    _webView2Host.SetTopInset(insetPx);
                }
            }
#endif
        }

        private void OnEditorUpdate()
        {
#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
            // Flush pending metrics as soon as the HTTP port becomes available.
            if (_tauriServerPort <= 0)
            {
                int portFromSession = SessionState.GetInt(TauriUtils.SESSION_STATE_TAURI_PORT_KEY, -1);
                if (portFromSession > 0)
                {
                    _tauriServerPort = portFromSession;
                }
            }
            if (_tauriServerPort > 0)
            {
                CodelyEventTracker.FlushPending(_tauriServerPort);
            }
#endif
#if UNITY_EDITOR_OSX
            if (_webViewBridge == null)
            {
                _webViewBridge      = new MacOSWebViewBridge();
                _webViewInitialized = false;
            }

            // Skip update when window is not visible on any platform.
            if (!_isWindowVisible) return;

            if (!_webViewInitialized)
            {
                InitializeWebView();
            }
            else if (_webViewBridge.IsInitialized)
            {
                IntPtr currentHandle = GetEditorWindowHandle();
                if (currentHandle != _lastWindowParentHandle && currentHandle != IntPtr.Zero)
                {
                    _lastWindowParentHandle = currentHandle;
                    UpdateWebViewFrame();
                }

                Vector2 currentSize = new Vector2(position.width, position.height);
                if (currentSize != _lastWindowSize)
                {
                    _lastWindowSize = currentSize;
                    UpdateWebViewFrame();
                }
            }
#else
            if (!_isWindowVisible) return;

            if (!_webView2Initialized)
            {
                try
                {
                    IntPtr currentHWND = NativeWindowHelper.GetHWND(this);
                    if (currentHWND != IntPtr.Zero && currentHWND != _currentGUIViewHWND)
                    {
                        _currentGUIViewHWND = currentHWND;

                        if (_webView2Host != null)
                        {
                            _webView2Host.Dispose();
                            _webView2Host        = null;
                            _webView2Initialized = false;
                        }

                        if (_serverReady)
                        {
                            TryInitializeWebView2();
                        }
                    }
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"DrillWindow: OnEditorUpdate GetHWND failed: {ex.Message}");
                }
            }
#endif
        }

        protected virtual void OnBecameInvisible()
        {
            _isWindowVisible = false;
#if UNITY_EDITOR_OSX
            _webViewBridge?.SetHidden(true);
#else
            _webView2Host?.SetVisible(false);
#endif
        }

        protected virtual void OnBecameVisible()
        {
            _isWindowVisible = true;
#if UNITY_EDITOR_OSX
            if (_webViewBridge != null && _webViewBridge.IsInitialized)
            {
                _webViewBridge.SetHidden(false);
                UpdateWebViewFrame();
            }
#else
            if (_webView2Host != null && _webView2Host.IsReady)
            {
                _webView2Host.SetVisible(true);
            }
            else if (!_webView2Initialized && _serverReady)
            {
                TryInitializeWebView2();
            }
#endif
        }

        protected virtual void OnBeforeRemovedAsTab()
        {
#if UNITY_EDITOR_WIN
            DisposeWebView2Host();
#endif
        }

        #endregion

        #region IPC

        private void AcquireIpcClient()
        {
            if (!_ipcAcquired)
            {
                if (_ipcOwnerId == null) _ipcOwnerId = $"DrillWindow:{this.GetStableInstanceId()}";
                CodelyIpcManager.Acquire(_ipcOwnerId);
                _ipcAcquired = true;
            }

            if (_ipcEventsSubscribed)
            {
                return;
            }

            CodelyIpcManager.OnServerReady += OnTauriServerReady;
            CodelyIpcManager.OnDrillCompleted += OnDrillCompleted;
            _ipcEventsSubscribed = true;
        }

        private void ReleaseIpcClient()
        {
            if (_ipcEventsSubscribed)
            {
                CodelyIpcManager.OnServerReady -= OnTauriServerReady;
                CodelyIpcManager.OnDrillCompleted -= OnDrillCompleted;
                _ipcEventsSubscribed = false;
            }

            if (_ipcAcquired && !string.IsNullOrEmpty(_ipcOwnerId))
            {
                CodelyIpcManager.Release(_ipcOwnerId);
                _ipcAcquired = false;
            }
        }

        private void OnTauriServerReady(string url)
        {
            CodelyLogger.Log($"DrillWindow: Tauri server ready at {url}");
            _serverReady = true;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                _tauriServerPort = uri.Port;
            }

            CodelyEventTracker.FlushPending(_tauriServerPort);

#if UNITY_EDITOR_WIN
            // 直接调用：已在主线程，多一次 delayCall 在失焦场景下只是浪费一次 wake。
            if (!_webView2Initialized)
            {
                TryInitializeWebView2();
            }
#elif UNITY_EDITOR_OSX
            if (!_webViewInitialized)
            {
                InitializeWebView();
            }
            else if (_webViewBridge != null && _webViewBridge.IsInitialized && !_webViewBridge.HasValidContent())
            {
                _webViewBridge.LoadURL(GetWebViewUrl());
            }
#endif
        }

        private void OnDrillCompleted()
        {
            CodelyIpcClient.ConsumePendingDrillCompleted();
            if (!BeginDrillCompletion())
                return;

            EditorApplication.delayCall += CompleteDrillTransition;
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private bool BeginDrillCompletion()
        {
            if (_drillCompletionHandled)
                return false;

            _drillCompletionHandled = true;
            CodelyEventTracker.Track(CodelyEventTracker.WelcomeButtonClick, "drillCompleted", _tauriServerPort);
            CodelyLogger.Log("DrillWindow: Drill completed, switching to Codely layout and opening CodelyWindow");
            return true;
        }

        private void CompleteDrillFromOnGUI()
        {
            if (!BeginDrillCompletion())
                return;

            PrepareAndCloseDrillWindow();
            EditorApplication.delayCall += OpenCodelyAfterDrill;
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private void CompleteDrillTransition()
        {
            PrepareAndCloseDrillWindow();
            OpenCodelyAfterDrill();
        }

        private void PrepareAndCloseDrillWindow()
        {
            SessionState.EraseInt(TauriUtils.SESSION_STATE_TAURI_PID_KEY);
            SessionState.EraseInt(TauriUtils.SESSION_STATE_TAURI_PORT_KEY);
            Close();
        }

        private static void OpenCodelyAfterDrill()
        {
            CodelyLayoutInstaller.ApplyCodelyLayout();
            CodelyWindow.ShowWindow();
        }

        #endregion

        #region Walkthrough lifecycle metrics

        private void TryNotifyWindowClosed()
        {
            CodelyEventTracker.TryTrackClose(
                CodelyEventTracker.WelcomeWindowClosed,
                _tauriServerPort,
                ref _windowClosedEventSent);
        }

        #endregion

        #region Tauri Process Management

        private bool TryRestoreTauriProcess()
        {
            int savedPid = SessionState.GetInt(TauriUtils.SESSION_STATE_TAURI_PID_KEY, -1);
            if (savedPid <= 0) return false;

            try
            {
                var process = Process.GetProcessById(savedPid);
                if (!process.HasExited)
                {
                    _tauriProcess    = process;
                    _tauriServerPort = SessionState.GetInt(TauriUtils.SESSION_STATE_TAURI_PORT_KEY, -1);
                    _serverReady     = _tauriServerPort > 0;
                    CodelyLogger.Log($"DrillWindow: Restored Tauri process from SessionState (PID: {savedPid}, port: {_tauriServerPort})");
                    return true;
                }
            }
            catch (ArgumentException) { }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"DrillWindow: Failed to restore Tauri process: {ex.Message}");
            }

            SessionState.EraseInt(TauriUtils.SESSION_STATE_TAURI_PID_KEY);
            SessionState.EraseInt(TauriUtils.SESSION_STATE_TAURI_PORT_KEY);
            return false;
        }

        private bool TryAttachFromLockFile()
        {
            try
            {
                string workspaceDir = TauriUtils.GetWorkspaceDirectory();

                // Primary: use the same per-project hash directory as CodelyWindow / Tauri
                string lockDir      = TauriUtils.GetLockDirForWorkspace(workspaceDir);
                string lockMetaPath = lockDir != null
                    ? Path.Combine(lockDir, ".codely.lock.meta.json")
                    : Path.Combine(workspaceDir, ".codely.lock.meta.json");

                string lockContent = TauriUtils.TryReadFileWithRetry(lockMetaPath, retryCount: 3, retryDelayMs: 50);

                // Fallback: legacy project-root path (older Tauri versions)
                if (string.IsNullOrEmpty(lockContent))
                {
                    string legacyPath = Path.Combine(workspaceDir, ".codely.lock.meta.json");
                    if (legacyPath != lockMetaPath)
                        lockContent = TauriUtils.TryReadFileWithRetry(legacyPath, retryCount: 1, retryDelayMs: 0);
                }

                if (string.IsNullOrEmpty(lockContent)) return false;

                int lockPid  = TauriUtils.ExtractIntFromJson(lockContent, "pid");
                int lockPort = TauriUtils.ExtractIntFromJson(lockContent, "http_port");
                if (lockPid <= 0) return false;
                if (lockPort <= 0 || lockPort > 65535) return false;

                var process = Process.GetProcessById(lockPid);
                if (process.HasExited) return false;

                _tauriProcess    = process;
                _tauriServerPort = lockPort;
                _serverReady     = true;
                SessionState.SetInt(TauriUtils.SESSION_STATE_TAURI_PID_KEY,  lockPid);
                SessionState.SetInt(TauriUtils.SESSION_STATE_TAURI_PORT_KEY, lockPort);

                CodelyLogger.Log($"DrillWindow: Attached to Tauri process from lock file (PID: {lockPid}, port: {lockPort}, meta: {lockMetaPath})");
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"DrillWindow: TryAttachFromLockFile failed: {ex.Message}");
                return false;
            }
        }

        private void LaunchTauriApplication()
        {
            string exePath = TauriUtils.GetCodelyRunnable();
            if (string.IsNullOrEmpty(exePath))
            {
                CodelyLogger.LogError("DrillWindow: cowork executable not found");
                return;
            }

            int port = TauriUtils.FindAvailablePort();
            if (port < 0)
            {
                CodelyLogger.LogError("DrillWindow: No available port found for Tauri server");
                return;
            }

            string workspaceDir     = TauriUtils.GetWorkspaceDirectory();
            string editorType       = TauriUtils.GetEditorType();
            string unityAccessToken = UnityConnectSession.GetAccessToken();
            string unityUserId      = UnityConnectSession.GetUserId();

            string arguments = $"\"{workspaceDir}\" --embed --editor-type \"{editorType}\" --port {port}";
            if (!string.IsNullOrEmpty(unityAccessToken) && !string.IsNullOrEmpty(unityUserId))
            {
                arguments += $" --unity-access-token \"{unityAccessToken}\" --unity-user-id \"{unityUserId}\"";
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName        = exePath,
                    Arguments       = arguments,
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    WindowStyle     = ProcessWindowStyle.Hidden,
                };
                TauriUtils.SanitizePkgEnvironment(startInfo);

                _tauriProcess    = Process.Start(startInfo);
                _tauriServerPort = port;
                SessionState.SetInt(TauriUtils.SESSION_STATE_TAURI_PID_KEY,  _tauriProcess.Id);
                SessionState.SetInt(TauriUtils.SESSION_STATE_TAURI_PORT_KEY, port);
                CodelyEventTracker.FlushPending(_tauriServerPort);
                CodelyLogger.Log($"DrillWindow: Launched Tauri (PID: {_tauriProcess.Id}, port: {port})");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"DrillWindow: Failed to launch Tauri: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensure a Tauri server is reachable for this workspace before pointing the
        /// WebView at it. A single-instance <c>cowork</c> APP may already be running
        /// (the Hub, another Editor, or a CodelyWindow). Spawning a fresh
        /// <c>--embed --port X</c> process in that case is futile: tauri_plugin_single_instance
        /// forwards the args to the existing instance and the new process exits without
        /// ever binding port X, leaving the WebView pointed at a dead port
        /// (ERR_CONNECTION_REFUSED). So attach to a running APP first, and only launch a
        /// brand-new process when none exists. Mirrors CodelyWindow's launch flow.
        /// </summary>
        private async void EnsureTauriProcess()
        {
            try
            {
                if (await TryAttachViaRunningSingleInstanceAppAsync())
                {
                    CodelyLogger.Log("DrillWindow: Attached to existing single-instance APP; skip launch.");
                    return;
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"DrillWindow: EnsureTauriProcess attach step failed: {ex.Message}");
            }

            LaunchTauriApplication();
        }

        /// <summary>
        /// Discover a running single-instance APP via <c>~/.codely/app.lock.meta.json</c>
        /// (written by the APP on startup). If found, POST this workspace to
        /// <c>/api/tauri/attach-embed-workspace</c> so the APP registers it and writes the
        /// per-workspace lock; then attach through the normal lock-file flow.
        ///
        /// Fast checks (file reads, PID lookup) run on the calling thread; HTTP requests
        /// (1s + 3s timeouts) and the lock-poll loop (2s) run on a background thread so the
        /// Editor main thread is never blocked for the ~6s worst case. The final Unity-side
        /// attach runs back on the calling thread.
        ///
        /// Returns <c>false</c> when no running single-instance APP is discovered or any
        /// step fails — the caller then launches a fresh APP process.
        /// </summary>
        private async Task<bool> TryAttachViaRunningSingleInstanceAppAsync()
        {
            try
            {
                string appMetaPath = TauriUtils.GetGlobalAppMetaPath();
                if (string.IsNullOrEmpty(appMetaPath) || !File.Exists(appMetaPath))
                    return false;

                string json = TauriUtils.TryReadFileWithRetry(appMetaPath, retryCount: 2, retryDelayMs: 30);
                if (string.IsNullOrEmpty(json))
                    return false;

                int pid  = TauriUtils.ExtractIntFromJson(json, "pid");
                int port = TauriUtils.ExtractIntFromJson(json, "http_port");
                if (pid <= 0 || port <= 0 || port > 65535)
                    return false;

                // Process liveness check before any HTTP request — avoids blocking on a
                // dead process's port.
                try
                {
                    var proc = Process.GetProcessById(pid);
                    if (proc.HasExited) return false;
                }
                catch (ArgumentException)
                {
                    return false; // PID not found → stale meta file
                }

                string workspaceDir = TauriUtils.GetWorkspaceDirectory();
                string editorType   = TauriUtils.GetEditorType();
                string lockDir      = TauriUtils.GetLockDirForWorkspace(workspaceDir);
                string lockMetaPath = lockDir != null
                    ? Path.Combine(lockDir, ".codely.lock.meta.json")
                    : Path.Combine(workspaceDir, ".codely.lock.meta.json");
                string payload = BuildAttachEmbedWorkspacePayload(workspaceDir, editorType);

                bool backgroundSuccess = await Task.Run(() =>
                {
                    // Verify the discovered process is a single-instance APP responding on
                    // the advertised port.
                    try
                    {
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                        {
                            var resp = _httpClient.GetAsync($"http://127.0.0.1:{port}/api/tauri/status", cts.Token).GetAwaiter().GetResult();
                            if (!resp.IsSuccessStatusCode) return false;
                            string statusBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            if (!StatusReportsSingleInstance(statusBody)) return false;
                        }
                    }
                    catch
                    {
                        return false;
                    }

                    CodelyLogger.Log($"DrillWindow[SingleInstance]: Discovered running APP pid={pid}, port={port}; requesting attach-embed-workspace for {workspaceDir}");

                    try
                    {
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                        {
                            var resp = _httpClient.PostAsync($"http://127.0.0.1:{port}/api/tauri/attach-embed-workspace", content, cts.Token).GetAwaiter().GetResult();
                            if (!resp.IsSuccessStatusCode)
                            {
                                CodelyLogger.LogWarning($"DrillWindow[SingleInstance]: attach-embed-workspace returned {(int)resp.StatusCode}");
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogWarning($"DrillWindow[SingleInstance]: attach-embed-workspace request failed: {ex.Message}");
                        return false;
                    }

                    // The APP writes the per-workspace lock meta during registration; wait
                    // briefly for it to appear (File.Exists only — no Unity APIs here).
                    for (int i = 0; i < 20; i++)
                    {
                        if (File.Exists(lockMetaPath)) return true;
                        Thread.Sleep(100);
                    }

                    CodelyLogger.LogWarning("DrillWindow[SingleInstance]: attach succeeded via HTTP but per-workspace lock meta did not appear in time.");
                    return false;
                });

                if (!backgroundSuccess)
                    return false;

                // Back on the main thread: attach via the normal lock-file flow so port,
                // PID, server-ready state and SessionState are populated consistently.
                if (TryAttachFromLockFile())
                {
                    CodelyLogger.Log("DrillWindow[SingleInstance]: Attached to running single-instance APP.");
                    return true;
                }

                CodelyLogger.LogWarning("DrillWindow[SingleInstance]: Lock meta appeared but attach failed.");
                return false;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"DrillWindow[SingleInstance]: TryAttachViaRunningSingleInstanceApp failed: {ex.Message}");
                return false;
            }
        }

        private static string BuildAttachEmbedWorkspacePayload(string workspaceDir, string editorType)
        {
            // Manual JSON encoding to avoid pulling Newtonsoft / System.Text.Json into the Editor assembly.
            string pathEscaped   = EscapeJsonString(workspaceDir ?? string.Empty);
            string editorEscaped = EscapeJsonString(editorType ?? "unity");
            return $"{{\"path\":\"{pathEscaped}\",\"editorType\":\"{editorEscaped}\"}}";
        }

        private static string EscapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static bool StatusReportsSingleInstance(string statusJson)
        {
            if (string.IsNullOrEmpty(statusJson)) return false;
            // Whitespace-insensitive check for "single_instance": true
            string compact = statusJson.Replace(" ", string.Empty).Replace("\t", string.Empty);
            return compact.Contains("\"single_instance\":true");
        }

        #endregion

        #region WebView URL

        private string GetWebViewUrl()
        {
            // Include workspaceDir + mode=embedded so the Tauri server injects
            // window.__CODELY_WORKSPACE_DIR__ and the shell can route this
            // Editor-embedded WebView's invokes (drill/getProgress,
            // drill/updateProgress, drill/drillCompleted, …) to the correct
            // workspace — this WebView has no Tauri window label. Without
            // workspaceDir the backend rejects every invoke with "Missing
            // workspace routing info", so drill/drillCompleted never reaches its
            // handler and Unity never receives the drillCompleted IPC.
            // Walkthrough open/close metrics are sent via CodelyEventTracker in
            // DrillWindow.cs, not the WebView.
            // DRILL_ROUTE already carries a leading "?", so append with "&".
            string workspaceDir = TauriUtils.GetWorkspaceDirectory();
            string encodedDir    = Uri.EscapeDataString(workspaceDir ?? string.Empty);
            string themeMode     = EditorGUIUtility.isProSkin ? "dark" : "light";
            return $"http://127.0.0.1:{_tauriServerPort}{DRILL_ROUTE}&workspaceDir={encodedDir}&theme={themeMode}&mode=embedded";
        }

        #endregion

        #region WebView2 (Windows)

#if UNITY_EDITOR_WIN
        private void TryInitializeWebView2()
        {
            if (_webView2Initializing || _webView2Initialized) return;
            if (_cachedTopInsetPx < 0)
            {
                Repaint();
                return;
            }
            _webView2Initializing = true;

            CodelyLogger.Log("[DrillWindow] TryInitializeWebView2: started");

            try
            {
                if (_tauriServerPort <= 0)
                {
                    CodelyLogger.LogWarning("[DrillWindow] TryInitializeWebView2: skipped — port not available");
                    return;
                }

                // 单次尝试：Sleep 阻塞主线程在失焦场景下毫无收益。GUIView HWND 不可用时退出，
                // OnGUI backstop / OnEditorUpdate 会在下一次 paint / tick 自动重试。
                IntPtr hwnd = NativeWindowHelper.GetHWND(this);

                if (hwnd == IntPtr.Zero)
                {
                    CodelyLogger.LogWarning("[DrillWindow] TryInitializeWebView2: GUIView HWND not yet available, will retry from OnGUI/OnEditorUpdate");
                    return;
                }

                _currentGUIViewHWND = hwnd;

                var host = new WebView2Host();
                host.OnParentDestroyed += () =>
                {
                    var destroyedHost    = _webView2Host;
                    _webView2Host        = null;
                    _webView2Initialized = false;
                    _currentGUIViewHWND  = IntPtr.Zero;
                    _lastTopInsetPx      = -1;
                    EditorApplication.delayCall += () =>
                    {
                        destroyedHost?.Dispose();
                        TryInitializeWebView2();
                    };
                };

                string url = GetWebViewUrl();
                host.Init(hwnd, url);

                _webView2Host        = host;
                _webView2Initialized = true;
                if (_cachedTopInsetPx >= 0)
                {
                    _webView2Host.SetTopInset(_cachedTopInsetPx);
                    _lastTopInsetPx = _cachedTopInsetPx;
                }
                _webView2Host.SetVisible(_isWindowVisible);
                CodelyLogger.Log($"[DrillWindow] WebView2 initialized at {url} (HWND: 0x{hwnd:X})");
                Repaint();
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"[DrillWindow] TryInitializeWebView2 exception: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _webView2Initializing = false;
            }
        }

        private void DisposeWebView2Host()
        {
            if (_webView2Host != null)
            {
                try { _webView2Host.Dispose(); }
                catch (Exception ex) { CodelyLogger.LogWarning($"DrillWindow: Error disposing WebView2Host: {ex.Message}"); }
                _webView2Host = null;
            }
            _webView2Initialized = false;
            _currentGUIViewHWND  = IntPtr.Zero;
            _lastTopInsetPx      = -1;
        }

        /// <summary>
        /// Editor 退出时由 CodelyWindow.OnEditorQuitting 调用：清空 native handle 引用，
        /// 不再调用 NB_Destroy（NB_ShutdownAll 已同步关闭 native 端）。
        /// </summary>
        internal void ClearWebView2OnQuit()
        {
            _webView2Host        = null;
            _webView2Initialized = false;
            _currentGUIViewHWND  = IntPtr.Zero;
            _lastTopInsetPx      = -1;
        }
#endif

        #endregion

        #region WKWebView (macOS)

#if UNITY_EDITOR_OSX
        private void InitializeWebView()
        {
            if (_webViewBridge == null || _webViewInitialized) return;

            IntPtr guiViewHandle = GetEditorWindowHandle();
            if (guiViewHandle == IntPtr.Zero || _tauriServerPort <= 0) return;

            string url = GetWebViewUrl();
            if (_webViewBridge.Create(guiViewHandle, url, 0, 0, position.width, position.height))
            {
                _lastWindowSize               = new Vector2(position.width, position.height);
                _webViewInitialized           = true;
                _serializedWebViewHandle      = _webViewBridge.Handle.ToInt64();
                _serializedWebViewInitialized = true;
                CodelyLogger.Log($"DrillWindow: WKWebView initialized at {url}");
                Repaint();
            }
            else
            {
                CodelyLogger.LogError("DrillWindow: Failed to initialize WKWebView");
            }
        }

        private void UpdateWebViewFrame()
        {
            if (_webViewBridge == null || !_webViewBridge.IsInitialized) return;

            IntPtr guiViewHandle = GetEditorWindowHandle();
            if (guiViewHandle == IntPtr.Zero) return;

            _webViewBridge.UpdateParentAndFrame(guiViewHandle, 0, 0, position.width, position.height);
        }
#endif

        #endregion

        #region Utilities

        private IntPtr GetEditorWindowHandle()
        {
            return EditorWindowNativeHandleHelper.GetGUIViewHandle(this);
        }

        /// <summary>
        /// Thin wrapper kept for binary compatibility: <see cref="CodelyWindow"/> references this as
        /// <c>DrillWindow.GetCodelyCliRunnable()</c>. The implementation lives in <see cref="TauriUtils"/>.
        /// </summary>
        internal static string GetCodelyCliRunnable() => TauriUtils.GetCodelyCliRunnable();

        #endregion
    }
}
#endif
