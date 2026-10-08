#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Unity.InternalBridge;
using UnityEditor;
using UnityEditor.PackageManager;
#if UNITY_2021_2_OR_NEWER
using UnityEditor.SceneManagement;
#else
using UnityEditor.Experimental.SceneManagement;
#endif
using UnityEngine;
using UnityTcp.Editor.Helpers;
namespace Cn.Tuanjie.Codely.Editor
{
    /// <summary>
    /// Describes a sub-object (e.g. a Mesh or Material) selected inside a
    /// container asset file (FBX, atlas texture).  Only present when the
    /// selection is a sub-asset; never present for top-level asset files.
    /// </summary>
    [Serializable]
    public class DropItemSelected
    {
        public string type;     // C# type name, e.g. "Mesh", "Material"
        public string name;     // Object name as shown in the Project window
        public string fileId;   // decimal YAML fileID (GlobalObjectId.targetObjectId)
    }

    /// <summary>
    /// 用于序列化选中对象信息
    /// </summary>
    [Serializable]
    public class DropItemData
    {
        public string path;
        public string type;
        public string name;
        public string guid;         // Asset GUID (stable across sessions)
        /// <summary>
        /// Decimal string of the YAML fileID (GlobalObjectId.targetObjectId).
        /// Empty for GameObjects (use instanceId). String on the wire so values
        /// above Int32 / JS Number.MAX_SAFE_INTEGER are not truncated.
        /// </summary>
        public string fileId;
        /// <summary>Non-null when the selected object lives inside a container
        /// file (sub-asset).  Carries type + name + fileId of the sub-object so
        /// callers can distinguish e.g. "Material #25" inside an FBX from the
        /// top-level FBX asset.
        /// </summary>
        public DropItemSelected selected;
        public string hierarchyPath;
        public long instanceId;
        /// <summary>staged | sceneInstance | subAsset | projectAsset</summary>
        public string kind;
        /// <summary>Persistent YAML fileID of a GameObject (GlobalObjectId.targetObjectId).</summary>
        public string goFileId;
        /// <summary>Persistent GUID of a GameObject's source asset/scene.</summary>
        public string goGuid;
        public string[] componentNames;
    }

    [Serializable]
    public class DropItemsPayload
    {
        public DropItemData[] items;
    }

    public class CodelyWindow : EditorWindow
    {
        private static bool s_reloadEventsRegistered = false;
        private const string TAURI_ICONS_BASE_PATH = TauriUtils.TAURI_ICONS_BASE_PATH;

        [InitializeOnLoadMethod]
        private static void RegisterReloadEvents()
        {
            if (TauriUtils.IsAutomatedEditorRun()) return;

            if (s_reloadEventsRegistered)
            {
                return;
            }

            s_reloadEventsRegistered = true;

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#if UNITY_2020_1_OR_NEWER
            Events.registeringPackages += OnPackagesRegistering;
#endif
            TryAutoOpenAfterInstall();
        }

        /// <summary>
        /// Called when packages are about to change (before domain reload).
        /// Only react when *this* package is being installed, removed, or upgraded; do not kill on theme change / script recompile / play mode.
        /// </summary>
#if UNITY_2020_1_OR_NEWER
        private static void OnPackagesRegistering(PackageRegistrationEventArgs args)
        {
            foreach (var package in args.added)
            {
                if (package.name == "cn.tuanjie.codely.bridge")
                {
                    SessionState.SetBool(SESSION_STATE_AUTO_OPEN_AFTER_INSTALL_KEY, true);
                    break;
                }
            }

            bool packageUpgradeDetected = false;
            foreach (var package in args.changedTo)
            {
                if (package.name == "cn.tuanjie.codely.bridge")
                {
                    packageUpgradeDetected = true;
                    break;
                }
            }

            if (packageUpgradeDetected)
            {
                foreach (var package in args.changedFrom)
                {
                    if (package.name == "cn.tuanjie.codely.bridge")
                    {
                        CleanupForPackageRemovalOrUpgrade();
                        break;
                    }
                }
            }

            foreach (var package in args.removed)
            {
                if (package.name == "cn.tuanjie.codely.bridge")
                {
                    CleanupForPackageRemovalOrUpgrade();
                    break;
                }
            }
        }
#endif

        private static void CleanupForPackageRemovalOrUpgrade()
        {
#if UNITY_EDITOR_WIN
            // Synchronously shut down ALL active WebView2 BrowserInstances before
            // domain reload. NB_ShutdownAll pumps messages while waiting for every
            // STA thread to exit, so when it returns all native resources are fully
            // cleaned up. Using per-window NB_Destroy (async, detached thread) here
            // would leave cleanup threads running while the old NativeBrowser.dll is
            // unloaded during domain reload, causing a native crash.
            try { NativeBrowserAPI.NB_ShutdownAll(5000); }
            catch (Exception ex) { CodelyLogger.LogWarning($"Codely: NB_ShutdownAll failed during package upgrade: {ex.Message}"); }

            // Clear all C# handle references so the subsequent per-window
            // PrepareForPackageRemovalOrUpgrade calls won't attempt NB_Destroy
            // on already-finalized instances (same pattern as OnEditorQuitting).
            foreach (var window in Resources.FindObjectsOfTypeAll<CodelyWindow>())
            {
                if (window == null) continue;
                window._serializedWebView2Handle = 0;
                window._serializedWebView2Valid  = false;
                window._webView2Host = null;
            }
            foreach (var window in Resources.FindObjectsOfTypeAll<DrillWindow>())
                window?.ClearWebView2OnQuit();
#endif

            foreach (var window in Resources.FindObjectsOfTypeAll<CodelyWindow>())
                window?.PrepareForPackageRemovalOrUpgrade();
            foreach (var window in Resources.FindObjectsOfTypeAll<DrillWindow>())
                window?.PrepareForPackageRemovalOrUpgrade();

            // The Tauri process outlives the package: it is never killed from
            // Unity. Removing/upgrading the package only tears down this
            // Editor's IPC client and its own SessionState.
            ReleaseTauriSessionState();

            CodelyLayoutInstaller.RemoveInstalledLayout();
            foreach (var window in Resources.FindObjectsOfTypeAll<CodelyWindow>())
                window?.Close();
            foreach (var window in Resources.FindObjectsOfTypeAll<DrillWindow>())
                window?.Close();
        }

        internal void PrepareForPackageRemovalOrUpgrade()
        {
            ReleaseIpcClient();
            _preserveTauriProcessOnDestroy = false;

#if UNITY_EDITOR_WIN
            if (_serializedWebView2Valid && _serializedWebView2Handle != 0)
            {
                NativeBrowserAPI.NB_Destroy(new IntPtr(_serializedWebView2Handle));
            }
            _serializedWebView2Handle = 0;
            _serializedWebView2Valid = false;
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

        private static void OnBeforeAssemblyReload()
        {
            // Switching editor theme triggers a domain reload. We must stop background IPC threads
            // before the domain is torn down to prevent native/mono crashes.
            StopIpcClientStatic();
        }

        private static void OnEditorQuitting()
        {
#if UNITY_EDITOR_WIN
            // 在 Unity 卸载 native DLL / 销毁窗口之前同步关闭所有 WebView2 实例：
            //  1) m_controller->Close() 必须在父 HWND 被销毁前完成，否则 msedgewebview2.exe 子进程残留；
            //  2) 父 HWND 销毁时需要 STA 线程已退出，否则归 STA 所有的 WebView2 子窗口无法整理 → 主线程挂起。
            // ShutdownAll 内部 pump 消息以驱动 WebView2 的跨 apartment marshaling。
            try { NativeBrowserAPI.NB_ShutdownAll(3000); }
            catch (Exception ex) { CodelyLogger.LogWarning($"Codely: NB_ShutdownAll failed: {ex.Message}"); }

            // ShutdownAll 已 Finalize 但未 delete 实例；清空所有 C# 持有的 handle，
            // 避免后续每个窗口的 OnDestroy 再次调用 NB_Destroy 触发后台 delete 线程。
            foreach (var window in Resources.FindObjectsOfTypeAll<CodelyWindow>())
            {
                if (window == null) continue;
                window._serializedWebView2Handle = 0;
                window._serializedWebView2Valid  = false;
                window._webView2Host = null;
            }
            foreach (var window in Resources.FindObjectsOfTypeAll<DrillWindow>())
                window?.ClearWebView2OnQuit();
#endif
            // The Tauri app has its own lifetime and is shared across every
            // Editor, so quitting this one only tears down its IPC client and
            // clears its own SessionState; the process keeps running.
            ReleaseTauriSessionState();
        }

        /// <summary>
        /// Called when Play Mode state changes.
        /// WebView handle survives domain reload; visibility follows <see cref="_isWindowVisible"/> so a hidden tab does not cover other docks.
        /// </summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
#if UNITY_EDITOR_OSX
            var windows = Resources.FindObjectsOfTypeAll<CodelyWindow>();
            foreach (var window in windows)
            {
                if (window == null) continue;

                switch (state)
                {
                    case PlayModeStateChange.EnteredPlayMode:
                    case PlayModeStateChange.EnteredEditMode:
                        if (window._webViewBridge != null && window._webViewBridge.IsInitialized)
                        {
                            window._webViewBridge.SetHidden(!window._isWindowVisible);
                            window.UpdateWebViewFrame();
                        }
                        break;
                }
            }
#endif
        }

        private static void StopIpcClientStatic()
        {
            CodelyIpcManager.StopAll();
        }

        /// <summary>
        /// Drops this Editor's claim on the Tauri process: stops the IPC client and clears the
        /// SessionState describing it, leaving the process itself running. Uses SessionState only,
        /// so it works when no EditorWindow instance is available (package removal, editor quit).
        /// </summary>
        private static void ReleaseTauriSessionState()
        {
            SessionState.EraseInt(SESSION_STATE_TAURI_PID_KEY);
            SessionState.EraseString(SESSION_STATE_TAURI_WINDOW_HANDLE_KEY);
            SessionState.EraseInt(SESSION_STATE_TAURI_PORT_KEY);
            SessionState.EraseString(SESSION_STATE_TAURI_SINGLE_INSTANCE_KEY);
            StopIpcClientStatic();
        }

#if UNITY_EDITOR_WIN
        #region Windows API Declarations

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        // ── Screenshot capture (PrintWindow + DIB section) ─────────────────────
        // Used by CaptureDragSnapshot() to freeze the WebView2 content as a
        // Texture2D right before SetVisible(false) hides it, so DrawDropOverlay
        // can render the snapshot as the overlay background.

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int  biWidth;
            public int  biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int  biXPelsPerMeter;
            public int  biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            // BI_RGB at 32 bpp has no color table; one RGBQUAD slot keeps the
            // struct layout valid for CreateDIBSection's signature.
            public uint bmiColors0;
        }

        private const uint PW_RENDERFULLCONTENT = 0x00000002; // captures DirectComposition / WebView2 content
        private const uint DIB_RGB_COLORS       = 0;
        private const uint BI_RGB               = 0;

        private const int VK_LBUTTON = 0x01;

        #endregion
#elif UNITY_EDITOR_OSX
        #region macOS CoreGraphics

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern bool CGEventSourceButtonState(uint state, uint button);

        private const uint kCGEventSourceStateHIDSystemState = 1;
        private const uint kCGMouseButtonLeft = 0;

        private static bool IsPrimaryMouseButtonDownOsx()
        {
            return CGEventSourceButtonState(kCGEventSourceStateHIDSystemState, kCGMouseButtonLeft);
        }

        #endregion
#endif

        #region Constants

        // Shared keys — canonical values live in TauriUtils; aliases kept to avoid touching all call sites.
        private const string SESSION_STATE_TAURI_PID_KEY  = TauriUtils.SESSION_STATE_TAURI_PID_KEY;
        private const string SESSION_STATE_TAURI_PORT_KEY = TauriUtils.SESSION_STATE_TAURI_PORT_KEY;
        private const int    DEFAULT_TAURI_PORT           = TauriUtils.DEFAULT_TAURI_PORT;
        private const int    MAX_PORT_ATTEMPTS            = TauriUtils.MAX_PORT_ATTEMPTS;

        // CodelyWindow-specific keys
        private const string SESSION_STATE_TAURI_WINDOW_HANDLE_KEY  = "Codely_TauriWindowHandle";
        private const string SESSION_STATE_AUTO_OPEN_AFTER_INSTALL_KEY = "Codely_AutoOpenAfterInstall";
        private const string SESSION_STATE_WINDOW_WAS_OPEN_KEY      = "Codely_WindowWasOpen";
        private const string SESSION_STATE_UPDATE_VERSION_KEY       = "Codely_UpdateVersion";
        // Cached capability advertised by `/api/tauri/status`: "1" → the Tauri process is
        // single-instance and shared across every Unity Editor in this user session.
        // Informational only — Unity never kills the Tauri process regardless of this flag.
        // Stored in SessionState so it survives domain reloads and is readable from static handlers.
        private const string SESSION_STATE_TAURI_SINGLE_INSTANCE_KEY = "Codely_TauriSingleInstance";

        #endregion


        private Process _tauriProcess;

#if UNITY_EDITOR_WIN
        private WebView2Host _webView2Host;
        private IntPtr _currentGUIViewHWND = IntPtr.Zero;
        private bool _webView2Initializing = false;
        private bool _webView2Initialized = false;

        // When a freshly created WebView2 finishes its first navigation we push
        // focus into the content once (so the page's autofocus input gets a
        // caret on open, matching browser behaviour). Guarded so SPA reloads /
        // later navigations don't steal focus from whatever window the user is
        // currently in.
        private bool _webView2DidInitialFocus = false;

        private int _lastTopInsetPx = -1;

        // Persists across host disposal/recreation. Used to seed the native
        // m_topInset before the first SyncBounds runs, avoiding a frame where
        // the WebView2 covers the tab bar. Updated whenever OnGUI computes a
        // valid inset; never reset (unlike _lastTopInsetPx).
        private int _cachedTopInsetPx = -1;

        // Domain Reload 跨域保活：序列化 BrowserInstance 指针，OnEnable 时恢复复用
        [SerializeField] private long _serializedWebView2Handle = 0;
        [SerializeField] private bool _serializedWebView2Valid  = false;
#endif

        private float _searchStartTime;
        private IntPtr _lastWindowParentHandle = IntPtr.Zero;

        /// <summary>
        /// Whether this EditorWindow tab is currently visible (not covered by another tab in the same dock).
        /// Serialized so Domain Reload preserves it; otherwise WebView restore wrongly calls SetVisible(true) and covers other tabs.
        /// </summary>
        [SerializeField] private bool _isWindowVisible = true;

        private int _tauriServerPort = -1;
        private bool _serverReady = false;

        private bool _isDragOverlay = false;
        private bool _isDragExitInThisFrame = false;

#if UNITY_EDITOR_WIN
        // Snapshot of the WebView2 content captured the instant the drag overlay
        // activates (before SetVisible(false) hides the webview). Rendered as the
        // background of DrawDropOverlay so the user keeps seeing their page under
        // a translucent green frame instead of an opaque colored rectangle.
        // Disposed in RestoreTauriFromDragOverlay / OnBecameInvisible / OnDisable.
        private Texture2D _dragSnapshotTex;
        // Top inset (in physical pixels) at the moment the snapshot was captured,
        // so DrawDropOverlay can sample only the WebView2 portion of the GUIView
        // bitmap (skipping the tab strip above it).
        private int _dragSnapshotTopInsetPx;
#endif

        // Cached overlay textures + styles for the rounded "Release to upload"
        // panel. Built lazily on first DrawDropOverlay call and rebuilt only when
        // the editor skin (pro/light) changes. Disposed in OnDisable.
        private Texture2D _overlayFrameTex;     // 32×32, 8 px corner, 2 px #35C9A9 border, 10% green fill
        private Texture2D _overlayCardTex;      // 40×40, 12 px corner, surface-card fill, no border
        private Texture2D _overlayChipTex;      // 16×16, 4 px corner, chip bg + 1 px border
        private Texture2D _overlayUploadIcon;   // 64×64 anti-aliased upload glyph (document + up arrow)
        private GUIStyle  _overlayFrameStyle;
        private GUIStyle  _overlayCardStyle;
        private GUIStyle  _overlayChipStyle;
        private bool      _overlayResourcesIsProSkin;
        private bool      _overlayResourcesBuilt;
        
#if UNITY_EDITOR_OSX
        // macOS WebView support
        private MacOSWebViewBridge _webViewBridge;
        private Vector2 _lastWindowSize;
        private bool _webViewInitialized = false;
        private IntPtr _pendingStableGUIViewHandle = IntPtr.Zero;
        private bool _webViewFrameSyncPending = false;
        [SerializeField] private long _serializedWebViewHandle = 0;
        [SerializeField] private bool _serializedWebViewInitialized = false;
        // Set in OnEnable when we restore a serialized WebView but the Tauri server
        // is NOT the same live instance it was loaded against (it was killed and
        // relaunched, or we re-attached to a fresh one). The restored page is then
        // bound to a dead/replaced backend and sits on a white screen — its
        // EventSource silently reconnects but the SPA never re-runs its load
        // handshake. Consumed by OnEditorUpdate once the new server is HTTP-ready.
        private bool _pendingWebViewReloadAfterRelaunch = false;

        // HTTP-readiness gate for the embedded WebView. _tauriServerPort is assigned
        // the instant we pick the port — long before the spawned Tauri process binds
        // it — so loading on "port > 0" alone races the server and lands on a
        // permanent connection-refused white screen (WKWebView never retries a failed
        // navigation). We only create/reload the WebView after /api/tauri/status
        // actually answers on that port. _serverHttpReady and _serverReadyProbeRunning
        // are written from the probe's background task, so they are volatile.
        private volatile bool _serverHttpReady = false;
        private volatile bool _serverReadyProbeRunning = false;
        private int _serverReadyProbePort = -1;
#endif

        private string _ipcOwnerId;
        private bool _ipcAcquired;
        private bool _ipcEventsSubscribed;

        [SerializeField] private bool _eventTrackedOnEnable = false;
        private bool _panelClosedEventSent;
        private bool _panelOpenedMetricQueued;
        /// <summary>
        /// True for throwaway <see cref="CreateInstance"/> windows discarded during
        /// <see cref="ShowWindow"/> fallback; suppresses open/close lifecycle metrics.
        /// </summary>
        private bool _suppressPanelLifecycleMetrics;

        // Theme tracking
        private bool _lastObservedIsProSkin;
        private string _lastSentThemeMode = null;
        private bool _themeModeDirty = true; // If true, we should keep trying to send the theme until it succeeds
        private bool _isDetachMode = false;
        /// <summary>When true, <see cref="CleanupResourcesAsync"/> clears Unity-side state but does not kill the Tauri process (e.g. workspace switch from Tauri).</summary>
        private bool _preserveTauriProcessOnDestroy = false;

        // Setup UI (when cowork not found): show install guide inside this window instead of CodelySetupWindow
        private bool _showSetupUI = false;
        private const string SetupDownloadUrl = "https://codely.tuanjie.cn/download";
        private const float SetupLogoTopMargin = 16f; // 顶部 Logo+文字 距窗口顶端的距离
        private const string SetupIconsPath = "Packages/cn.tuanjie.codely.bridge/Editor/Bridge/Icons/";
        private const string SetupFontsPath = "Packages/cn.tuanjie.codely.bridge/Editor/Bridge/fonts/";
        private static readonly Color SetupPrimaryGreen = new Color(1f / 255f, 167f / 255f, 127f / 255f, 1f); // #01A77F
        private static readonly Color SetupPrimaryBtnNormal = new Color(1f / 255f, 167f / 255f, 127f / 255f, 1f); // #01A77F
        private static readonly Color SetupPrimaryBtnHover = new Color(77f / 255f, 193f / 255f, 165f / 255f, 1f); // #4dc1a5
        private static readonly Color SetupPrimaryBtnPress = new Color(1f / 255f, 136f / 255f, 102f / 255f, 1f); // #018866
        private static readonly Color SetupTextPrimary = new Color(0.95f, 0.95f, 0.95f, 1f);
        private static readonly Color SetupTextSecondary = new Color(0.60f, 0.60f, 0.65f, 1f);
        private static readonly Color SetupLinkNormal = new Color(107f / 255f, 114f / 255f, 128f / 255f, 1f); // #6B7280
        private static readonly Color SetupLinkHover = new Color(1f, 1f, 1f, 1f); // #FFFFFF
        private static readonly Color SetupLinkPress = new Color(156f / 255f, 163f / 255f, 175f / 255f, 1f); // #9CA3AF
        private static readonly Color SetupMainTitleColor = new Color(1f, 1f, 1f, 1f); // #FFFFFF
        private static readonly Color SetupSubtitleColor = new Color(156f / 255f, 163f / 255f, 175f / 255f, 1f); // #9CA3AF
        private bool _setupStylesBuilt;
        private GUIStyle _setupLogoNameStyle;
        private GUIStyle _setupMainTitleStyle;
        private GUIStyle _setupSubtitleStyle;
        private GUIStyle _setupPrimaryBtnStyle;
        private GUIStyle _setupLinkStyle;
        private Texture2D _setupGreenTex;
        private Texture2D _setupPrimaryBtnNormalTex;
        private Texture2D _setupPrimaryBtnHoverTex;
        private Texture2D _setupPrimaryBtnPressTex;
        private Texture2D _setupLogoTex;
        private Texture2D _setupIllustrationTex;
        private Texture2D _setupDownloadIconTex;
        private Rect _setupIllustrationRect;
        private static bool _setupLinkPressed;
        private bool _isAttachingToEditor = false;

        // Loading UI 状态
        private bool _showUpdateLoadingUI = false;
        private string _updateVersion = "";
        private float _loadingAnimationTime = 0f;
        private bool _isExpectingTauriRestart = false;
        private DateTime _suppressCloseUntilUtc = DateTime.MinValue;
        private const double UPDATE_RESTART_DISCONNECT_GRACE_SECONDS = 8d;

        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3),
        };

        public static void ShowWindow()
        {
            // 已存在实例时，直接 focus，保留用户当前布局
            if (HasOpenInstances<CodelyWindow>())
            {
                foreach (var existing in Resources.FindObjectsOfTypeAll<CodelyWindow>())
                {
                    if (existing == null) continue;
                    existing.minSize = new Vector2(800, 600);
                    existing.ApplyTitleIcon();
                    existing.Show();
                    existing.Focus();
                    return;
                }
            }

            // 新建实例，作为 main editor 最左侧的 dock panel 插入（结构参考
            // Editor/Layout/Tuanjie AI Mode.wlt）。其他已存在的 dock 不会被销毁，
            // 只会按 SplitView 比例收缩让出空间。
            var window = CreateInstance<CodelyWindow>();
            window._suppressPanelLifecycleMetrics = true;
            window.titleContent = new GUIContent(" Tuanjie AI");
            window.minSize = new Vector2(800, 600);

            if (CodelyWindowDocker.TryDockLeftmost(window))
            {
                window._suppressPanelLifecycleMetrics = false;
                window.ApplyTitleIcon();
                window.Show();
                window.Focus();
                return;
            }

            // Fallback：反射失败时退回原有的 tab dock 行为，至少能把窗口打开
            DestroyImmediate(window);

            var editorAssembly = typeof(UnityEditor.Editor).Assembly;
            var inspectorType = editorAssembly.GetType("UnityEditor.InspectorWindow");
            var sceneViewType = typeof(UnityEditor.SceneView);
            var gameViewType = editorAssembly.GetType("UnityEditor.GameView");
            var consoleType = editorAssembly.GetType("UnityEditor.ConsoleWindow");
            var hierarchyType = editorAssembly.GetType("UnityEditor.SceneHierarchyWindow");

            var desiredDockNextTo = new List<Type>();
            if (hierarchyType != null) desiredDockNextTo.Add(hierarchyType);
            if (inspectorType != null) desiredDockNextTo.Add(inspectorType);
            desiredDockNextTo.Add(sceneViewType);
            if (gameViewType != null) desiredDockNextTo.Add(gameViewType);
            if (consoleType != null) desiredDockNextTo.Add(consoleType);

            var fallback = GetWindow<CodelyWindow>("Tuanjie AI", true, desiredDockNextTo.ToArray());
            fallback.minSize = new Vector2(800, 600);
            fallback.ApplyTitleIcon();
            fallback.Show();
        }

        /// <summary>
        /// If a CodelyWindow instance is open, focus it; otherwise open one.
        /// </summary>
        public static void FocusOrOpenWindow()
        {
            if (HasOpenInstances<CodelyWindow>())
            {
                var windows = Resources.FindObjectsOfTypeAll<CodelyWindow>();
                if (windows.Length > 0 && windows[0] != null)
                {
                    windows[0].Focus();
                    return;
                }
            }
            ShowWindow();
        }
        [MenuItem("AI/Open Assistant %l", priority = 995)]
        public static void ToggleCodely()
        {
            if (string.IsNullOrEmpty(GetCodelyRunnable()))
            {
                ShowWindow();
                return;
            }

            // If drill walkthrough has not been completed, open DrillWindow instead
            if (!TauriUtils.IsDrillCompleted())
            {
                DrillWindow.OpenWalkThrough();
                return;
            }

            var windows = Resources.FindObjectsOfTypeAll<CodelyWindow>();
            // 判断主 Codely 窗口是否打开
            if (HasOpenInstances<CodelyWindow>())
            {
                var readyWindow = GetReadyWindowOrNull(windows);
                if (readyWindow == null)
                {
                    ShowWindow();
                    CodelyLogger.Log("Codely: Window exists but not initialized, reopening");
                    return;
                }

                foreach (var window in windows)
                {
                    if (window != null && window.GetEditorWindowHandle() != IntPtr.Zero)
                    {
                        window.Show();
                        window.Focus();
                    }
                }
            }
            else
            {
                ShowWindow();
                CodelyLogger.Log("Codely: Window opened via Ctrl+L");
            }
        }
        [MenuItem("AI/Open CLI", priority = 996)]
        public static void OpenCodelyTerminal()
        {
            string cliPath = DrillWindow.GetCodelyCliRunnable();
            if (string.IsNullOrEmpty(cliPath))
            {
                EditorUtility.DisplayDialog("Tuanjie AI CLI Not Found",
                    "codely CLI not found. Set CODELY_HOME (containing codely[.exe]) or CODELY_APP_HOME (containing cli/bin/<platform>/codely[.exe]).",
                    "OK");
                return;
            }
            try
            {
                CodelyLogger.Log("Codely: Opening CLI in terminal: " + cliPath);
#if UNITY_EDITOR_WIN
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k \"{cliPath}\"",
                    UseShellExecute = true,
                });
#elif UNITY_EDITOR_OSX
                // AppleScript-escape: backslash first, then double quote.
                // `quoted form of` then shell-quotes the path at AppleScript runtime,
                // so spaces in paths like "/Applications/Codely Cowork.app/..." survive
                // into Terminal's shell as a single argument.
                string asEscaped = cliPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "osascript",
                    // `do script` launches the CLI in a new Terminal tab; a follow-up
                    // `activate` brings Terminal to the foreground so the user can type
                    // straight away instead of clicking back to the new window.
                    Arguments = $"-e 'tell application \"Terminal\" to do script (quoted form of \"{asEscaped}\")' -e 'tell application \"Terminal\" to activate'",
                    UseShellExecute = true,
                });
#else
                Process.Start(new ProcessStartInfo
                {
                    FileName = cliPath,
                    UseShellExecute = true,
                });
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely: Failed to open CLI in terminal: {ex.Message}");
                EditorUtility.DisplayDialog("Tuanjie AI", $"Failed to open terminal: {ex.Message}", "OK");
            }
        }

        [MenuItem("AI/Force Reload", priority = 999)]
        public static void ForceReloadCodely()
        {
            var window = GetWindow<CodelyWindow>();
            if (window != null)
            {
                // Clean up the Tauri process
                window.CleanupTauriProcess();
                CodelyEventTracker.SuppressNextPanelClose();
                // Close the Codely window
                window.Close();
            }
            EditorApplication.delayCall += () =>
            {
                ToggleCodely();
            };
        }

        // [MenuItem("Window/Codely/Force Quit", priority = 1010)]
        // public static void QuitCodely()
        // {
        //     // Get the current Codely window
        //     var window = GetWindow<CodelyWindow>();
        //     if (window != null)
        //     {
        //         // Clean up the Tauri process
        //         window.CleanupTauriProcess();
        //         // Close the Codely window
        //         window.Close();
        //         CodelyLogger.Log("Codely: Quit Codely - Tauri process cleaned up and window closed");
        //     }
        // }

        [MenuItem("Assets/AI/Add to context", false, 110)]
        private static void AddSelectedAssetsToCodely()
        {
            CodelyLogger.Log("AddSelectedAssetsToCodely");
            SendSelectedItemsToTauriInternal("Assets");
        }

        private static bool _pendingGameObjectSelection = false;
        private static List<DropItemData> _pendingContextItems = null;

        // IPC owner used by the static "Add to context" path so it can start the
        // shared IPC client without an open CodelyWindow. Released once the queued
        // items are delivered (a live window's own owner keeps the client alive).
        private const string STATIC_CONTEXT_IPC_OWNER_ID = "CodelyWindow:StaticContextSender";
        private static bool _staticContextIpcAcquired = false;

        [MenuItem("GameObject/AI/Add to context", false, 110)]
        private static void AddSelectedGameObjectsToCodely()
        {
            if (_pendingGameObjectSelection) return;
            _pendingGameObjectSelection = true;
            EditorApplication.delayCall += () =>
            {
                _pendingGameObjectSelection = false;
                CodelyLogger.Log("AddSelectedGameObjectsToCodely");
                SendSelectedItemsToTauriInternal("GameObject");
            };
        }

        // [MenuItem("Window/Codely/Add Console to Context %&d", priority = 1001)]
        // private static void AddUnityConsoleToContext()
        // {
        //     SendDebugUnityConsoleMessageStatic();
        // }

        // [MenuItem("Window/Codely/New Session %&n", priority = 1000)]
        // private static void FocusInputWithNewSession()
        // {
        //     SendFocusInputWithNewSessionMessageStatic();
        // }

// #if UNITY_EDITOR_OSX
//         [MenuItem("Window/Codely/Open DevTools", priority = 1002)]
//         private static void OpenDevTools()
//         {
//             var windows = Resources.FindObjectsOfTypeAll<CodelyWindow>();
//             var readyWindow = GetReadyWindowOrNull(windows);
//             if (readyWindow != null && readyWindow._webViewBridge != null)
//             {
//                 readyWindow._webViewBridge.ShowInspector();
//                 CodelyLogger.Log("Codely: Opening Web Inspector...");
//             }
//             else
//             {
//                 CodelyLogger.LogWarning("Codely: No Codely window ready or WebView not initialized");
//             }
//         }

//         [MenuItem("Window/Codely/Open DevTools", true, priority = 1002)]
//         private static bool ValidateOpenDevTools()
//         {
//             var windows = Resources.FindObjectsOfTypeAll<CodelyWindow>();
//             var readyWindow = GetReadyWindowOrNull(windows);
//             return readyWindow != null && readyWindow._webViewBridge != null && readyWindow._webViewBridge.IsInitialized;
//         }
// #elif UNITY_EDITOR_WIN
//         [MenuItem("Window/Codely/Open DevTools", priority = 1002)]
//         private static void OpenDevTools()
//         {
//             var windows = Resources.FindObjectsOfTypeAll<CodelyWindow>();
//             var readyWindow = GetReadyWindowOrNull(windows);
//             if (readyWindow?._webView2Host != null && readyWindow._webView2Host.IsReady)
//             {
//                 readyWindow._webView2Host.ShowDevTools();
//                 CodelyLogger.Log("Codely: Opening WebView2 DevTools...");
//             }
//             else
//             {
//                 CodelyLogger.LogWarning("Codely: No Codely window ready or WebView2 not initialized");
//             }
//         }

//         [MenuItem("Window/Codely/Open DevTools", true, priority = 1002)]
//         private static bool ValidateOpenDevTools()
//         {
//             var windows = Resources.FindObjectsOfTypeAll<CodelyWindow>();
//             var readyWindow = GetReadyWindowOrNull(windows);
//             return readyWindow?._webView2Host != null && readyWindow._webView2Host.IsReady;
//         }
// #endif

        private static void SendSelectedItemsToTauriInternal(string source)
        {
            // Capture selection immediately — it may change once focus moves.
            var dropItems = BuildDropItemsFromSelection();
            if (dropItems == null)
                return;

            if (CodelyIpcManager.IsConnected)
            {
                SendContextItemsViaIpc(dropItems);
                return;
            }

            // IPC isn't connected yet. Make sure the shared IPC client is started
            // (it may not be, if no CodelyWindow is open) so the queued items can be
            // delivered once it connects. Do NOT open the CodelyWindow here — adding
            // context must not force the assistant window open.
            EnsureStaticContextIpcAcquired();

            _pendingContextItems = dropItems;
            // Guard against double-subscription if triggered multiple times before IPC connects
            CodelyIpcManager.OnConnectionChanged -= OnIpcConnectionChangedForPendingContext;
            CodelyIpcManager.OnConnectionChanged += OnIpcConnectionChangedForPendingContext;
        }

        private static List<DropItemData> BuildDropItemsFromSelection()
        {
            var selectedObjects = Selection.objects;
            if (selectedObjects == null || selectedObjects.Length == 0)
                return null;

            var dropItems = new List<DropItemData>();
            foreach (var obj in selectedObjects)
            {
                if (obj == null) continue;
                dropItems.Add(BuildDropItem(obj));
            }
            return dropItems.Count > 0 ? dropItems : null;
        }

        private static void SendContextItemsViaIpc(List<DropItemData> dropItems)
        {
            var payload = new DropItemsPayload { items = dropItems.ToArray() };
            string json = JsonUtility.ToJson(payload);
            CodelyIpcManager.TrySend(IpcMessageType.AddContexts, json);
        }

        private static void OnIpcConnectionChangedForPendingContext(bool connected)
        {
            if (!connected) return;
            CodelyIpcManager.OnConnectionChanged -= OnIpcConnectionChangedForPendingContext;
            var items = _pendingContextItems;
            _pendingContextItems = null;
            if (items != null && items.Count > 0)
                SendContextItemsViaIpc(items);

            // Items delivered (SendMessage is synchronous): drop our standalone IPC
            // owner so the shared client can stop when no CodelyWindow keeps it alive.
            ReleaseStaticContextIpcIfHeld();
        }

        /// <summary>
        /// Start the shared IPC client on behalf of the static "Add to context" path
        /// when no CodelyWindow is open. Idempotent; the window's own owner (if any)
        /// is independent and unaffected.
        /// </summary>
        private static void EnsureStaticContextIpcAcquired()
        {
            if (_staticContextIpcAcquired)
                return;
            CodelyIpcManager.Acquire(STATIC_CONTEXT_IPC_OWNER_ID);
            _staticContextIpcAcquired = true;
        }

        private static void ReleaseStaticContextIpcIfHeld()
        {
            if (!_staticContextIpcAcquired)
                return;
            _staticContextIpcAcquired = false;
            CodelyIpcManager.Release(STATIC_CONTEXT_IPC_OWNER_ID);
        }

        private static CodelyWindow GetReadyWindowOrNull(CodelyWindow[] windows)
        {
            if (windows == null || windows.Length == 0)
            {
                return null;
            }

            foreach (var window in windows)
            {
                if (window == null) continue;

#if UNITY_EDITOR_WIN
                if (window._webView2Initialized || window.GetEditorWindowHandle() != IntPtr.Zero)
                    return window;
#else
                if (window.GetEditorWindowHandle() != IntPtr.Zero)
                    return window;
#endif
            }

            return null;
        }

        private static void TryAutoOpenAfterInstall()
        {
            // Skip auto-open during unattended editor runs (batch mode / -nographics CI / QA
            // automation). Creating the window there triggers a native 'SUCCEEDED(hr)' assertion
            // that flips otherwise-passing test runs to FAIL. See TauriUtils.IsAutomatedEditorRun.
            if (TauriUtils.IsAutomatedEditorRun())
            {
                return;
            }

            if (!SessionState.GetBool(SESSION_STATE_AUTO_OPEN_AFTER_INSTALL_KEY, false))
            {
                return;
            }

            SessionState.EraseBool(SESSION_STATE_AUTO_OPEN_AFTER_INSTALL_KEY);
            EditorApplication.delayCall += () =>
            {
                if (!HasOpenInstances<CodelyWindow>())
                {
                    ToggleCodely();
                    CodelyLogger.Log("Codely: Auto-opened via ToggleCodely after package install");
                }
            };
        }

        private void OnEnable()
        {
            if (TauriUtils.IsAutomatedEditorRun()) return;

            ApplyTitleIcon();

            if (string.IsNullOrEmpty(GetCodelyRunnable()))
            {
                CodelyLogger.LogError("Codely: cowork runnable not found. Make sure cowork is installed.");
                _showSetupUI = true;
                return;
            }

            _lastWindowParentHandle = GetEditorWindowHandle();
            _lastObservedIsProSkin = EditorGUIUtility.isProSkin;
            _lastSentThemeMode = null;
            _themeModeDirty = true;

#if UNITY_EDITOR_OSX
            if (_webViewBridge == null)
            {
                _webViewBridge = new MacOSWebViewBridge();
                _webViewInitialized = false;
            }

            // Try to restore from serialized handle
            if (_serializedWebViewInitialized && _serializedWebViewHandle != 0)
            {
                IntPtr savedHandle = new IntPtr(_serializedWebViewHandle);
                try
                {
                    if (_webViewBridge.RestoreFromHandle(savedHandle))
                    {
                        _webViewInitialized = true;
                        _webViewBridge.SetHidden(!_isWindowVisible);
                        QueueWebViewFrameSync();

                        // Clear serialized data after successful restore to prevent accumulation
                        _serializedWebViewHandle = 0;
                        _serializedWebViewInitialized = false;

                        // Schedule validation check after a short delay to ensure content is loaded
                        EditorApplication.delayCall += () =>
                        {
                            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
                            {
                                EditorApplication.delayCall += () => ValidateRestoredWebView();
                            });
                        };
                    }
                    else
                    {
                        CodelyLogger.Log("Codely: WebView handle invalid, will recreate");
                        _serializedWebViewHandle = 0;
                        _serializedWebViewInitialized = false;
                        _webViewInitialized = false;
                    }
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"Codely: Exception while restoring WebView: {ex.Message}");
                    _serializedWebViewHandle = 0;
                    _serializedWebViewInitialized = false;
                    _webViewInitialized = false;
                }
            }
            else if (_webViewBridge.IsInitialized)
            {
                _webViewInitialized = true;
                _webViewBridge.SetHidden(!_isWindowVisible);
                QueueWebViewFrameSync();
            }

            bool tauriRestoredOSX = TryRestoreTauriProcess();
            // Only a successful TryRestoreTauriProcess() proves the *same* Tauri
            // process the embedded WebView was loaded against is still alive — the
            // one case its page is guaranteed not stale. Attaching via the project
            // lock or relaunching means the backend is a fresh instance, so a
            // WebView restored from the serialized handle must be reloaded once the
            // new server connects (see _pendingWebViewReloadAfterRelaunch).
            bool restoredSameTauriInstance = tauriRestoredOSX;
            if (!tauriRestoredOSX)
            {
                tauriRestoredOSX = TryAttachToExistingProjectTauriFromLock();
            }

            if (_webViewInitialized && !restoredSameTauriInstance)
            {
                _pendingWebViewReloadAfterRelaunch = true;
                // The backend changed; force a fresh readiness probe before the
                // restored WebView is reloaded (the new server may reuse the same
                // port, so the stale "ready" must be cleared explicitly).
                ResetServerReadyGate();
            }

            AcquireIpcClient();

            if (!tauriRestoredOSX && !_isDetachMode)
            {
                LaunchTauriApplication();
            }
#else
            // Windows: 使用 WebView2 内嵌浏览器，Tauri 以 server-only 模式运行
            // 先判断是否需要新启动，若需要则先杀掉残留进程再连接 IPC，
            // 避免 IPC 连接到旧进程导致管道冲突和端口竞态。
            bool tauriRestored = TryRestoreTauriProcess();
            if (!tauriRestored)
            {
                tauriRestored = TryAttachToExistingProjectTauriFromLock();
            }

            AcquireIpcClient();

            if (!tauriRestored && !_isDetachMode)
            {
                LaunchTauriApplication();
            }

            // 尝试从序列化 handle 恢复 WebView2（Domain Reload 后复用已存活的 BrowserInstance）
            bool webView2Restored = false;
            bool hasAnotherLiveWindow = HasAnotherLiveCodelyWindow();
            if (!hasAnotherLiveWindow && _serializedWebView2Valid && _serializedWebView2Handle != 0)
            {
                IntPtr savedHandle = new IntPtr(_serializedWebView2Handle);
                try
                {
                    var host = new WebView2Host();
                    if (host.RestoreFromHandle(savedHandle))
                    {
                        _webView2Host      = host;
                        _webView2Initialized = true;
                        _currentGUIViewHWND  = NativeWindowHelper.GetHWND(this);
                        host.SetVisible(_isWindowVisible);
                        webView2Restored   = true;
                        CodelyLogger.Log("[Codely] WebView2 restored from handle after Domain Reload");
                        Repaint();
                    }
                    else
                    {
                        CodelyLogger.LogWarning("[Codely] WebView2 handle invalid after Domain Reload, will recreate");
                    }
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"[Codely] Exception restoring WebView2: {ex.Message}");
                }
                finally
                {
                    _serializedWebView2Handle = 0;
                    _serializedWebView2Valid  = false;
                }
            }

            if (!webView2Restored)
            {
                _webView2Initialized = false;
                _currentGUIViewHWND  = IntPtr.Zero;
            }
#endif

            // Report after Tauri restore/launch so HTTP port is available when possible.
            TrackPanelOpenedIfNeeded();

            EditorApplication.update += OnEditorUpdate;
        }

        private static Texture2D LoadIcon(string fileName) =>
            AssetDatabase.LoadAssetAtPath<Texture2D>(TAURI_ICONS_BASE_PATH + fileName);

        private void ApplyTitleIcon()
        {
            var icon = EditorGUIUtility.isProSkin
                ? LoadIcon("title_icon.png")
                : LoadIcon("title_icon_light_theme.png");

            if (icon == null)
                return;

            titleContent = new GUIContent(" Tuanjie AI", icon);
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;

            ReleaseIpcClient();

#if UNITY_EDITOR_WIN
            DisposeDragSnapshot();
#endif
            DisposeOverlayResources();

            if (_setupGreenTex != null) { DestroyImmediate(_setupGreenTex); _setupGreenTex = null; }
            if (_setupPrimaryBtnNormalTex != null) { DestroyImmediate(_setupPrimaryBtnNormalTex); _setupPrimaryBtnNormalTex = null; }
            if (_setupPrimaryBtnHoverTex != null) { DestroyImmediate(_setupPrimaryBtnHoverTex); _setupPrimaryBtnHoverTex = null; }
            if (_setupPrimaryBtnPressTex != null) { DestroyImmediate(_setupPrimaryBtnPressTex); _setupPrimaryBtnPressTex = null; }
            _setupStylesBuilt = false;

#if UNITY_EDITOR_WIN
            // Domain Reload 保活：序列化 handle，让 WebView2 在 Reload 期间保持存活，
            // OnEnable 中恢复复用，避免重建导致的白屏闪烁。可见性由 _isWindowVisible（序列化）在恢复时决定。
            if (_webView2Host != null && _webView2Host.IsReady)
            {
                // 先将回调清零：Domain Reload 后旧委托失效，清零可防止 Reload 期间意外触发崩溃
                var emptyCallbacks = new NativeBrowserAPI.Callbacks { ctx = IntPtr.Zero };
                NativeBrowserAPI.NB_SetCallbacks(_webView2Host.Handle, ref emptyCallbacks);

                _serializedWebView2Handle = _webView2Host.Handle.ToInt64();
                _serializedWebView2Valid  = true;
                _webView2Host = null;  // 释放托管引用；native BrowserInstance 继续存活
            }
            else
            {
                _serializedWebView2Handle = 0;
                _serializedWebView2Valid  = false;
                _webView2Host?.SetVisible(false);
            }
#endif

#if UNITY_EDITOR_OSX
            // Serialize WebView handle for restoration after domain reload
            if (_webViewBridge != null && _webViewBridge.IsInitialized)
            {
                try
                {
                    IntPtr handle = _webViewBridge.Handle;
                    if (handle != IntPtr.Zero)
                    {
                        _serializedWebViewHandle = handle.ToInt64();
                        _serializedWebViewInitialized = true;
                    }
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"Codely: Failed to serialize WebView handle: {ex.Message}");
                    _serializedWebViewHandle = 0;
                    _serializedWebViewInitialized = false;
                }
            }
#endif
        }

        private void OnDestroy()
        {
            TryNotifyPanelClosed();

            bool preserveSharedResources = WindowResourcePreservationPolicy.ShouldPreserveSharedResources(
                _isDetachMode,
                GetLiveCodelyWindowCount());

            if (!preserveSharedResources)
            {
                ReleaseIpcClient();
            }
#if UNITY_EDITOR_WIN
            // 若 OnDisable 序列化了 handle 但 OnEnable 未运行（窗口被真正关闭而非 Domain Reload），
            // 需要手动销毁 native BrowserInstance，防止内存泄漏
            if (!preserveSharedResources && _serializedWebView2Valid && _serializedWebView2Handle != 0)
            {
                NativeBrowserAPI.NB_Destroy(new IntPtr(_serializedWebView2Handle));
                _serializedWebView2Handle = 0;
                _serializedWebView2Valid  = false;
            }
            if (!preserveSharedResources)
            {
                DisposeWebView2Host();
            }
#endif

            // 清除序列化的 WebView 指针，避免下次启动时尝试恢复已销毁的 WebView
#if UNITY_EDITOR_OSX
            _serializedWebViewHandle = 0;
            _serializedWebViewInitialized = false;
#endif

            // 如果处于 detach 模式，关闭窗口时保留 Tauri 进程
            if (preserveSharedResources)
            {
                _preserveTauriProcessOnDestroy = true;
                _isDetachMode = false;
            }

            // 异步清理资源，避免阻塞窗口关闭
            CleanupResourcesAsync();
        }

        /// <summary>
        /// 异步清理资源，不阻塞窗口关闭
        /// </summary>
        private async void CleanupResourcesAsync()
        {
            // The Tauri process is never killed on close — it is shared across every
            // Unity Editor / Cowork window in this user session, and it owns its own
            // lifetime (it exits by itself once its last workspace closes).
            // `_preserveTauriProcessOnDestroy` is set when the Editor window is
            // closing because of a workspace switch initiated from the Tauri
            // side: in that case we additionally keep the WebView alive (it is
            // reused by the next session).
            bool preserveTauri = _preserveTauriProcessOnDestroy;
            _preserveTauriProcessOnDestroy = false;
            _tauriProcess = null;
            _tauriServerPort = -1;
            SessionState.EraseInt(SESSION_STATE_TAURI_PID_KEY);
            SessionState.EraseInt(SESSION_STATE_TAURI_PORT_KEY);

#if UNITY_EDITOR_OSX
            var webViewToDestroy = _webViewBridge;
            _webViewBridge = null;
            _serializedWebViewHandle      = 0;
            _serializedWebViewInitialized = false;
#endif

            // Background-thread cleanup: stop IPC client. The shared Tauri
            // process is owned app-wide and survives this window's teardown;
            // when its last workspace closes, Tauri exits on its own via its
            // window-close-requested handlers.
            await System.Threading.Tasks.Task.Run(() =>
            {
                try { StopIpcClientStatic(); }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"Codely: Error stopping IPC client: {ex.Message}");
                }

                if (preserveTauri)
                {
                    CodelyLogger.Log("Codely: Workspace switch close — Tauri process left running, local state cleared");
                }
            });

#if UNITY_EDITOR_OSX
            // WKWebView 必须在主线程销毁
            if (!preserveTauri && webViewToDestroy != null && webViewToDestroy.IsInitialized)
            {
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        webViewToDestroy.Destroy();
                        CodelyLogger.Log("Codely: WebView destroyed asynchronously");
                    }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogWarning($"Codely: Error destroying WebView: {ex.Message}");
                    }
                };
            }
#endif
        }

        private void OnGUI()
        {
            if (_showSetupUI)
            {
                DrawSetupUI();
                return;
            }

#if UNITY_EDITOR_OSX
            // macOS: Use WKWebView
            var webViewRect = GUILayoutUtility.GetRect(
                GUIContent.none,
                GUIStyle.none,
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(true));

            HandleDragAndDrop(webViewRect);

            if (_isDragOverlay)
            {
                var overlayRect = new Rect(0, 0, position.width, position.height);
                DrawDropOverlay(overlayRect);
            }
            else if (_webViewBridge == null || !_webViewBridge.IsInitialized)
            {
                GUI.Box(webViewRect, "Loading Tuanjie AI...");
            }
#else
            // Windows: 使用 WebView2 内嵌浏览器
            // embedRect 由 Unity 布局系统分配，y > 0 部分即为标题/Tab 栏高度
            var embedRect = GUILayoutUtility.GetRect(
                GUIContent.none,
                GUIStyle.none,
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(true));

            // 处理 Unity 拖拽事件
            HandleDragAndDrop(embedRect);

            if (_isDragOverlay && !_showUpdateLoadingUI)
            {
                // WebView2 已通过 SetVisible(false) 隐藏，在 GUIView 上绘制拖拽覆盖层
                var overlayRect = new Rect(0, 0, position.width, position.height);
                DrawDropOverlay(overlayRect);
            }
            else if (!_webView2Initialized)
            {
                GUI.Box(embedRect, "Loading Tuanjie AI...");

                // Focus-independent backstop: 失焦启动时 EditorApplication.update / delayCall
                // 都被节流，server_ready 事件链可能整段卡住。OnGUI 在窗口收到 WM_PAINT 时仍会跑
                // （CodelyIpcServer.WakeUnityMainThread 强制 InvalidateRect 触发了 WM_PAINT），
                // 因此在此处兜底拾起 IPC 后台线程已写入的 URL，直接推进 WebView2 初始化。
                // 只在 Layout 阶段触发一次；Repaint 等阶段仅作绘制。
                // TryInitializeWebView2 内部有 _webView2Initializing/_webView2Initialized 重入保护，
                // 后续 delayCall 真正跑起来 OnTauriServerReady 再调一次也是无副作用 no-op。
                if (Event.current.type == EventType.Layout &&
                    !_webView2Initializing &&
                    !HasAnotherLiveCodelyWindow() &&
                    _isWindowVisible)
                {
                    if (_tauriServerPort <= 0)
                    {
                        string pendingUrl = CodelyIpcManager.LastServerReadyUrl;
                        if (!string.IsNullOrEmpty(pendingUrl) &&
                            Uri.TryCreate(pendingUrl, UriKind.Absolute, out var pendingUri) &&
                            pendingUri.Port > 0)
                        {
                            _tauriServerPort = pendingUri.Port;
                            SessionState.SetInt(SESSION_STATE_TAURI_PORT_KEY, _tauriServerPort);
                            CodelyLogger.Log($"[Codely] OnGUI backstop: picked up port={_tauriServerPort} from background-thread server_ready cache");
                        }
                    }
                    if (_tauriServerPort > 0)
                    {
                        TryInitializeWebView2();
                    }
                }
            }

            if (_webView2Host != null && _webView2Host.IsReady)
            {
                Vector2 contentOrigin = GUIUtility.GUIToScreenPoint(Vector2.zero);
                int insetPx = UnityEngine.Mathf.Max(0,
                    UnityEngine.Mathf.RoundToInt((contentOrigin.y - position.y) * EditorGUIUtility.pixelsPerPoint));
                _cachedTopInsetPx = insetPx;
                if (insetPx != _lastTopInsetPx)
                {
                    _lastTopInsetPx = insetPx;
                    _webView2Host.SetTopInset(insetPx);
                }
            }
#endif

            // Update loading overlay on top of GUIView (native WebView hidden — same idea as drag overlay on Windows).
            if (_showUpdateLoadingUI)
            {
                DrawUpdateLoadingUI();
            }
        }

        private void BuildSetupStyles()
        {
            if (_setupStylesBuilt) return;

            _setupGreenTex = MakeSetupTex(SetupPrimaryGreen);
            _setupPrimaryBtnNormalTex = MakeSetupTex(SetupPrimaryBtnNormal);
            _setupPrimaryBtnHoverTex = MakeSetupTex(SetupPrimaryBtnHover);
            _setupPrimaryBtnPressTex = MakeSetupTex(SetupPrimaryBtnPress);
            _setupLogoTex = AssetDatabase.LoadAssetAtPath<Texture2D>(SetupIconsPath + "codely_logo.png");
            _setupIllustrationTex = AssetDatabase.LoadAssetAtPath<Texture2D>(SetupIconsPath + "setup_main.png");
            _setupDownloadIconTex = AssetDatabase.LoadAssetAtPath<Texture2D>(SetupIconsPath + "setup_download.png");

            var logoFont = AssetDatabase.LoadAssetAtPath<Font>(SetupFontsPath + "NotoSans-Regular.ttf")
                ?? AssetDatabase.LoadAssetAtPath<Font>(SetupFontsPath + "Inter.ttc");
            _setupLogoNameStyle = new GUIStyle(EditorStyles.label)
            {
                font = logoFont ?? EditorStyles.label.font,
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = SetupTextPrimary },
                hover = { textColor = SetupTextPrimary },
            };

            _setupMainTitleStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 28,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = SetupMainTitleColor },
                hover = { textColor = SetupMainTitleColor },
            };

            _setupSubtitleStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = SetupSubtitleColor },
                hover = { textColor = SetupSubtitleColor },
            };

            _setupPrimaryBtnStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 44,
                normal = { background = null, textColor = Color.white },
                hover = { background = null, textColor = Color.white },
                active = { background = null, textColor = Color.white },
                focused = { background = null, textColor = Color.white },
            };

            _setupLinkStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = SetupLinkNormal },
                hover = { textColor = SetupLinkHover },
                active = { textColor = SetupLinkPress },
            };
            _setupLinkStyle.normal.textColor = SetupLinkNormal;
            _setupLinkStyle.hover.textColor = SetupLinkHover;
            _setupLinkStyle.active.textColor = SetupLinkPress;
            _setupLinkStyle.fontStyle = FontStyle.Normal;

            _setupStylesBuilt = true;
        }

        private static Texture2D MakeSetupTex(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void DrawUpdateLoadingUI()
        {
            _loadingAnimationTime += Time.deltaTime;

            float w = position.width;
            float h = position.height;

            // Full-screen overlay matching design: solid #2d2d2d
            const float bgR = 0x2d / 255f;
            Color bgColor = new Color(bgR, bgR, bgR, 1f);
            GUI.DrawTexture(new Rect(0, 0, w, h), EditorGUIUtility.whiteTexture, ScaleMode.StretchToFill, true, 0f, bgColor, 0f, 0f);

            const float contentWidth = 480f;
            float innerW = Mathf.Min(contentWidth, w - 48f);
            float centerX = w * 0.5f;

            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            GUIStyle versionStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.62f, 0.62f, 0.62f, 1f) }
            };
            GUIStyle statusStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.78f, 0.78f, 0.78f, 1f) }
            };
            GUIStyle footerStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = new Color(0.42f, 0.42f, 0.42f, 1f) }
            };

            const string statusText = "Tuanjie AI is updating. Please wait...";
            const string footerText = "The window will automatically restore when the update completes.";
            string versionLine = $"Updating to version {_updateVersion}";

            float titleH = titleStyle.CalcHeight(new GUIContent("Updating Tuanjie AI"), innerW);
            float versionH = versionStyle.CalcHeight(new GUIContent(versionLine), innerW);
            float statusH = statusStyle.CalcHeight(new GUIContent(statusText), innerW);
            float footerH = footerStyle.CalcHeight(new GUIContent(footerText), innerW);

            const float gapTitleToVersion = 8f;
            const float gapVersionToSpinner = 28f;
            const float spinnerBlockH = 56f;
            const float gapSpinnerToStatus = 22f;
            const float gapStatusToDivider = 20f;
            const float dividerH = 1f;
            const float gapDividerToFooter = 14f;

            float dividerW = innerW * 0.55f;
            float totalH = titleH + gapTitleToVersion + versionH + gapVersionToSpinner + spinnerBlockH +
                gapSpinnerToStatus + statusH + gapStatusToDivider + dividerH + gapDividerToFooter + footerH;

            float x = centerX - innerW * 0.5f;
            float y = (h - totalH) * 0.5f;

            GUI.Label(new Rect(x, y, innerW, titleH), "Updating Tuanjie AI", titleStyle);
            y += titleH + gapTitleToVersion;
            GUI.Label(new Rect(x, y, innerW, versionH), versionLine, versionStyle);
            y += versionH + gapVersionToSpinner;

            // Original 8-dot spinner logic; discs instead of textured quads (circles)
            float spinnerCenterY = y + spinnerBlockH * 0.5f;
            float rotation = _loadingAnimationTime * 2f;
            float radius = 20f;
            Color loadingColor = new Color(0.1f, 0.7f, 0.5f, 1f);
            Handles.BeginGUI();
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    float angle = (i * 45f + rotation) * Mathf.Deg2Rad;
                    float dotX = centerX + Mathf.Cos(angle) * radius;
                    float dotY = spinnerCenterY + Mathf.Sin(angle) * radius;
                    float dotSize = 6f - i * 0.5f;
                    float alpha = 1f - (i * 0.12f);
                    Handles.color = new Color(loadingColor.r, loadingColor.g, loadingColor.b, alpha);
                    Handles.DrawSolidDisc(new Vector3(dotX, dotY, 0f), Vector3.forward, dotSize * 0.5f);
                }
            }
            finally
            {
                Handles.EndGUI();
            }

            y += spinnerBlockH + gapSpinnerToStatus;
            GUI.Label(new Rect(x, y, innerW, statusH), statusText, statusStyle);
            y += statusH + gapStatusToDivider;

            Color dividerColor = new Color(0.38f, 0.38f, 0.38f, 1f);
            float divX = centerX - dividerW * 0.5f;
            EditorGUI.DrawRect(new Rect(divX, y, dividerW, dividerH), dividerColor);
            y += dividerH + gapDividerToFooter;
            GUI.Label(new Rect(x, y, innerW, footerH), footerText, footerStyle);

            Repaint();
        }

        /// <summary>
        /// Same pattern as drag overlay: hide native WebView so IMGUI can draw on top; restore when loading ends.
        /// </summary>
        private void ApplyWebViewHiddenForUpdateLoading(bool hideNativeWebView)
        {
            bool applied = false;
#if UNITY_EDITOR_OSX
            if (_webViewBridge != null && _webViewBridge.IsInitialized)
            {
                _webViewBridge.SetHidden(hideNativeWebView);
                if (!hideNativeWebView)
                {
                    UpdateWebViewFrame();
                }
                applied = true;
            }
#else
            if (_webView2Host != null && _webView2Host.IsReady)
            {
                _webView2Host.SetVisible(!hideNativeWebView);
                applied = true;
            }
#endif
            if (applied)
            {
                Repaint();
            }
        }

        private void DrawSetupUI()
        {
            BuildSetupStyles();

            float w = position.width;
            float h = position.height;

            const float padH = 48f;
            const float logoSize = 40f;
            const float illustrationHeight = 120f;
            const float spacing = 24f;
            const float btnHeight = 44f;

            // 顶部：Logo (codely_logo.png) + "Codely" 固定在窗口最顶端
            float logoGroupW = logoSize + 10f + 80f;
            float logoX = (w - logoGroupW) * 0.5f;
            float logoY = SetupLogoTopMargin;
            var logoRect = new Rect(logoX, logoY, logoSize, logoSize);
            if (_setupLogoTex != null)
                GUI.DrawTexture(logoRect, _setupLogoTex, ScaleMode.ScaleToFit);
            else
            {
                GUI.Box(logoRect, "");
                var logoIcon = EditorGUIUtility.IconContent("d_console.warnicon.sml");
                if (logoIcon != null && logoIcon.image != null)
                    GUI.Label(logoRect, logoIcon);
            }
            GUI.Label(new Rect(logoX + logoSize + 10f, logoY + (logoSize - 20f) * 0.5f, 120f, 22f), "Tuanjie AI", _setupLogoNameStyle);

            // 其余内容（插图、主标题、副标题、主按钮、次要链接）作为一个整体，在窗口垂直居中

            const float illusW = 230f;
            float illusH = _setupIllustrationTex != null
                ? illusW * ((float)_setupIllustrationTex.height / _setupIllustrationTex.width)
                : illustrationHeight;
            float blockHeight = illusH + spacing + 36f + 8f + 24f + 56f + btnHeight + 16f + 24f;
            float y = (h - blockHeight) * 0.5f;

            // 插图：setup_main.png（保持原图宽高比，显示区域宽度 230 像素）
            if (_setupIllustrationTex != null)
            {
                float illusX = (w - illusW) * 0.5f;
                _setupIllustrationRect = new Rect(illusX, y, illusW, illusH);
                GUI.DrawTexture(_setupIllustrationRect, _setupIllustrationTex, ScaleMode.ScaleToFit);
            }
            else
            {
                _setupIllustrationRect = new Rect(padH, y, w - padH * 2f, illustrationHeight);
                GUI.Box(_setupIllustrationRect, "");
            }
            y += illusH + spacing;

            // 主标题（距副标题 8px）
            float titleW = w - padH * 2f;
            GUI.Label(new Rect(padH, y, titleW, 36f), "Tuanjie AI App", _setupMainTitleStyle);
            y += 44f;

            // 副标题（距主按钮 56px）
            GUI.Label(new Rect(padH, y, titleW, 24f), "Download Tuanjie AI App to get started", _setupSubtitleStyle);
            y += 56f;

            // 主按钮：Download for Desktop（距次要链接 16px）
            const float btnW = 360f;
            const float btnH = 44f;
            const float btnRadius = 8f;
            const float btnIconSize = 20f; // 按钮前图标尺寸（可调）
            const int btnIconTextGap = 2; // 图标与文字间隔（空格数，可调）
            float btnX = (w - btnW) * 0.5f;
            var btnRect = new Rect(btnX, y, btnW, btnH);
            EditorGUIUtility.AddCursorRect(btnRect, MouseCursor.Link);
            bool btnHover = Event.current != null && btnRect.Contains(Event.current.mousePosition);
            bool btnPress = Event.current != null && btnHover && Event.current.type == EventType.MouseDown && Event.current.button == 0;
            Texture2D btnTex = btnPress ? _setupPrimaryBtnPressTex : (btnHover ? _setupPrimaryBtnHoverTex : _setupPrimaryBtnNormalTex);
            GUI.DrawTexture(btnRect, btnTex, ScaleMode.StretchToFill, true, 0f, Color.white, 0f, btnRadius);
            var downloadContent = _setupDownloadIconTex != null
                ? new GUIContent(new string(' ', btnIconTextGap) + "Download for Desktop", _setupDownloadIconTex)
                : new GUIContent("Download for Desktop");
            using (new EditorGUIUtility.IconSizeScope(new Vector2(btnIconSize, btnIconSize)))
            {
                if (GUI.Button(btnRect, downloadContent, _setupPrimaryBtnStyle))
                    Application.OpenURL(SetupDownloadUrl);
            }
            y += btnH + 12f;

            // 次要链接：I have installed the desktop client.（点击即重新检测）
            var linkContent = new GUIContent("I have installed the desktop client.");
            float linkW = _setupLinkStyle.CalcSize(linkContent).x;
            float linkX = (w - linkW) * 0.5f;
            var linkRect = new Rect(linkX, y, linkW + 8f, 24f);
            EditorGUIUtility.AddCursorRect(linkRect, MouseCursor.Link);
            if (Event.current != null)
            {
                bool over = linkRect.Contains(Event.current.mousePosition);
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                    _setupLinkPressed = over;
                else if (Event.current.type == EventType.MouseUp || Event.current.type == EventType.MouseLeaveWindow)
                    _setupLinkPressed = false;
            }
            if (GUI.Button(linkRect, linkContent, _setupLinkStyle))
            {
                string runnable = GetCodelyRunnablePublic();
                if (!string.IsNullOrEmpty(runnable))
                {
                    CodelyEventTracker.SuppressNextPanelClose();
                    Close();
                    ToggleCodely();
                }
                else
                    EditorUtility.DisplayDialog("Not Found", "Tuanjie AI App is still not detected. Please ensure it is installed.", "OK");
            }
            bool linkHover = Event.current != null && linkRect.Contains(Event.current.mousePosition);
            Color linkColor = _setupLinkPressed ? SetupLinkPress : (linkHover ? SetupLinkHover : SetupLinkNormal);
            EditorGUI.DrawRect(new Rect(linkX + 4f, linkRect.yMax - 2f, linkW, 1f), linkColor);
        }

        private void OnEditorUpdate()
        {
#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
            // Flush pending metrics as soon as the HTTP port becomes available.
            // In some flows (async launch / throttled update loop), `_tauriServerPort`
            // is still -1 when `codely_panel_opened` is first queued; polling
            // SessionState here prevents it from being delayed until close.
            if (_tauriServerPort <= 0)
            {
                int portFromSession = SessionState.GetInt(SESSION_STATE_TAURI_PORT_KEY, -1);
                if (portFromSession > 0)
                {
                    _tauriServerPort = portFromSession;
                }
            }
            if (_tauriServerPort > 0)
            {
                CodelyEventTracker.FlushPending(_tauriServerPort);
                if (_panelOpenedMetricQueued && !CodelyEventTracker.HasPending)
                {
                    MarkPanelOpenedMetricDelivered();
                }
            }
#endif
#if UNITY_EDITOR_OSX
            // macOS: keep syncing theme mode (dark/light) to Tauri once IPC is connected
            CheckThemeChange();

            if (_webViewBridge == null)
            {
                _webViewBridge = new MacOSWebViewBridge();
                _webViewInitialized = false;
            }

            if (!_webViewInitialized)
            {
                InitializeWebView();
            }
            else if (_webViewBridge.IsInitialized)
            {
                // A WebView restored from a previous (now dead/replaced) server must
                // be reloaded so the SPA re-bootstraps instead of sitting on a white
                // screen — but only once the relaunched server actually answers, or
                // we just swap one connection-refused page for another.
                if (_pendingWebViewReloadAfterRelaunch && _tauriServerPort > 0)
                {
                    if (_serverHttpReady)
                    {
                        _pendingWebViewReloadAfterRelaunch = false;
                        CodelyLogger.Log("Codely: Tauri server ready after relaunch — reloading embedded WebView to re-bootstrap the UI");
                        _webViewBridge.LoadURL(GetCurrentWebViewUrl());
                    }
                    else
                    {
                        EnsureServerReadyProbe(_tauriServerPort);
                    }
                }

                IntPtr currentHandle = GetEditorWindowHandle();
                if (currentHandle != _lastWindowParentHandle && currentHandle != IntPtr.Zero)
                {
                    QueueWebViewFrameSync();
                }

                Vector2 currentSize = new Vector2(position.width, position.height);
                if (currentSize != _lastWindowSize)
                {
                    _lastWindowSize = currentSize;
                    QueueWebViewFrameSync();
                }

                TryApplyQueuedWebViewFrameSync();
                UpdateDragPassthrough();
            }
#else
            // Windows: 主题检测
            CheckThemeChange();

            // 拖拽穿透检测：当 Unity 拖拽发生时隐藏 WebView2，让 GUIView 接收拖拽事件
            UpdateDragPassthrough();

            if (!_isWindowVisible) return;
            if (HasAnotherLiveCodelyWindow()) return;

            // 检测 GUIView HWND 是否变化（停靠/浮动切换时会变化）
            // 注意：GetHWND 内含 EnumWindows 枚举，仅在 WebView2 未初始化时调用，避免每帧开销和潜在崩溃
            if (!_webView2Initialized)
            {
                try
                {
                    IntPtr currentHWND = NativeWindowHelper.GetHWND(this);
                    if (currentHWND != IntPtr.Zero && currentHWND != _currentGUIViewHWND)
                    {
                        CodelyLogger.Log($"[Codely] OnEditorUpdate: GUIView HWND changed → 0x{currentHWND.ToString("X")}");

                        // Reset _lastTopInsetPx alongside disposal: the new BrowserInstance
                        // starts with native m_topInset=0, so the OnGUI guard
                        // `insetPx != _lastTopInsetPx` must not short-circuit the first
                        // SetTopInset call, otherwise the new WebView2 renders from y=0
                        // and overlaps the dock's tab bar.
                        DisposeWebView2Host();
                        _currentGUIViewHWND = currentHWND;

                        if (_serverReady || _tauriServerPort > 0)
                        {
                            TryInitializeWebView2();
                        }
                    }
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"[Codely] OnEditorUpdate GetHWND failed: {ex.Message}");
                }
            }
#endif
        }

        private void QueueWebViewFrameSync()
        {
#if UNITY_EDITOR_OSX
            if (_webViewFrameSyncPending)
            {
            return;
            }

            _webViewFrameSyncPending = true;
            _pendingStableGUIViewHandle = IntPtr.Zero;
#endif
        }

        private void TryApplyQueuedWebViewFrameSync()
        {
#if UNITY_EDITOR_OSX
            if (!_webViewFrameSyncPending || _webViewBridge == null || !_webViewBridge.IsInitialized)
            {
                return;
            }

            IntPtr currentHandle = GetEditorWindowHandle();
            var decision = WindowHostStabilityPolicy.Evaluate(
                currentHandle,
                _lastWindowParentHandle,
                _pendingStableGUIViewHandle);

            _lastWindowParentHandle = decision.NextStableHandle;
            _pendingStableGUIViewHandle = decision.NextPendingHandle;

            if (!decision.ShouldApply)
            {
                return;
            }

            _webViewFrameSyncPending = false;
            UpdateWebViewFrameWithHandle(_lastWindowParentHandle);
#endif
        }

        protected virtual void OnBeforeRemovedAsTab()
        {
#if UNITY_EDITOR_WIN
            DisposeWebView2Host();
#endif
        }

        protected virtual void OnBecameInvisible()
        {
            _isWindowVisible = false;

#if UNITY_EDITOR_OSX
            _isDragOverlay = false;
            _webViewFrameSyncPending = false;
            _pendingStableGUIViewHandle = IntPtr.Zero;
            if (_webViewBridge != null && _webViewBridge.IsInitialized)
            {
                _webViewBridge.SetHidden(true);
            }
#else
            _isDragOverlay = false;
            DisposeDragSnapshot();
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
                QueueWebViewFrameSync();
                FocusEmbeddedInputOnBecameVisible();
            }
            SendThemeColorsToTauri();
#else
            if (_webView2Host != null)
            {
                _webView2Host.SetVisible(true);
                FocusEmbeddedInputOnBecameVisible();
            }
            else if (!_webView2Initialized && !HasAnotherLiveCodelyWindow() && (_serverReady || _tauriServerPort > 0))
            {
                // Freshly created WebView2: initial focus is pushed into the
                // content by OnLoadCompleted (see _webView2DidInitialFocus).
                TryInitializeWebView2();
            }
            SendThemeColorsToTauri();
#endif
        }

        /// <summary>
        /// When the tab becomes visible again (already-initialized WebView), push
        /// native focus into the embedded content so the page's input field gets a
        /// caret without the user having to click first. Deferred one tick so the
        /// view's SetVisible / frame-sync completes before focus is moved.
        /// </summary>
        private void FocusEmbeddedInputOnBecameVisible()
        {
            EditorApplication.delayCall += () =>
            {
                if (this == null || !_isWindowVisible)
                {
                    return;
                }

                Focus();
#if UNITY_EDITOR_OSX
                _webViewBridge?.Focus();
#else
                _webView2Host?.MoveFocus();
#endif
            };
        }

        private async void LaunchTauriApplication()
        {
            try
            {
                // Logged-in state affects logging only; launch uses the current session token (no RefreshAccessToken).
                string unityAccessToken = UnityConnectSession.GetAccessToken();
                string unityUserId = UnityConnectSession.GetUserId();
                if (UnityConnectSession.IsLoggedIn())
                {
                    CodelyLogger.Log($"Codely: User is logged in, launching with current Unity access token (UserId: {unityUserId})");
                }
                else
                {
                    CodelyLogger.Log("Codely: User is not logged in, launching without Unity credentials");
                }

                await LaunchTauriApplicationInternal(unityAccessToken, unityUserId);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely: Failed to launch Tauri application: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task LaunchTauriApplicationInternal(string unityAccessToken, string unityUserId)
        {
            try
            {
                _isDetachMode = false;

                if (TryAttachToExistingProjectTauriFromLock())
                {
                    CodelyLogger.Log("Codely: Existing Tauri process for this workspace detected via lock file, skip launch.");
                    return;
                }

                // Before spawning a new APP, check whether a single-instance
                // APP is already running on this machine (possibly for another
                // workspace). If so, ask it to attach our workspace — this
                // avoids starting a duplicate process and lets multiple Unity
                // Editors share one APP instance.
                if (await TryAttachViaRunningSingleInstanceAppAsync())
                {
                    CodelyLogger.Log("Codely: Attached to existing single-instance APP; skip launch.");
                    return;
                }

                // Find the Tauri executable
                string tauriExePath = GetCodelyRunnable();

                if (string.IsNullOrEmpty(tauriExePath))
                {
                    CodelyLogger.LogError($"Codely: Tauri executable not found at: {tauriExePath}");
                    Repaint();
                    return;
                }

                // Second attach attempt; a process can appear during earlier launch steps.
                if (TryAttachToExistingProjectTauriFromLock())
                {
                    CodelyLogger.Log("Codely: Existing Tauri process for this workspace detected, skip launch.");
                    return;
                }

                if (await TryAttachViaRunningSingleInstanceAppAsync())
                {
                    CodelyLogger.Log("Codely: Attached to existing single-instance APP on second attempt; skip launch.");
                    return;
                }

                // Find an available port for the Tauri server
                int port = FindAvailablePort();
                if (port < 0)
                {
                    CodelyLogger.LogError("Codely: Could not find an available port for Tauri server");
                    return;
                }

                // Store the port for later use
                _tauriServerPort = port;
                SessionState.SetInt(SESSION_STATE_TAURI_PORT_KEY, port);
                CodelyEventTracker.FlushPending(_tauriServerPort);
#if UNITY_EDITOR_OSX
                // Freshly spawned process: its HTTP server is not bound yet. Clear
                // the readiness gate so InitializeWebView waits for /api/tauri/status
                // before navigating (the port is set here, well before bind).
                ResetServerReadyGate();
#endif

                // Get the Unity project root directory
                string workspaceDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

                // Get the editor type (tuanjie or unity)
                string editorType = GetEditorType();

#if UNITY_EDITOR_OSX
                // On macOS, use server-only mode (no window creation to avoid crashes)
                // Build arguments with optional Unity tokens
                string arguments = $"\"{workspaceDir}\" --embed --editor-type \"{editorType}\" --port {port}";

                if (!string.IsNullOrEmpty(unityAccessToken) && !string.IsNullOrEmpty(unityUserId))
                {
                    arguments += $" --unity-access-token \"{unityAccessToken}\" --unity-user-id \"{unityUserId}\"";
                    CodelyLogger.Log($"Codely: Launching Tauri in server-only mode with Unity credentials (UserId: {unityUserId})");
                }
                else
                {
                    CodelyLogger.Log($"Codely: Launching Tauri in server-only mode without Unity credentials");
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = tauriExePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                TauriUtils.SanitizePkgEnvironment(startInfo);

                CodelyLogger.Log($"Codely: Launching Tauri in server-only mode with workspace-dir: {workspaceDir}, editor-type: {editorType}, port: {port}");
#else
                // Windows: 与 macOS 相同，使用 server-only 模式，由 WebView2 渲染 UI
                string arguments = $"\"{workspaceDir}\" --embed --editor-type \"{editorType}\" --port {port}";

                if (!string.IsNullOrEmpty(unityAccessToken) && !string.IsNullOrEmpty(unityUserId))
                {
                    arguments += $" --unity-access-token \"{unityAccessToken}\" --unity-user-id \"{unityUserId}\"";
                    CodelyLogger.Log($"Codely: Launching Tauri in server-only mode with Unity credentials (UserId: {unityUserId})");
                }
                else
                {
                    CodelyLogger.Log("Codely: Launching Tauri in server-only mode without Unity credentials");
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = tauriExePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                TauriUtils.SanitizePkgEnvironment(startInfo);

                CodelyLogger.Log($"Codely: Launching Tauri in server-only mode with workspace-dir: {workspaceDir}, editor-type: {editorType}, port: {port}");
#endif

                _tauriProcess = Process.Start(startInfo);
                _searchStartTime = (float)EditorApplication.timeSinceStartup;

                // Save process ID for restoration after recompilation
                SessionState.SetInt(SESSION_STATE_TAURI_PID_KEY, _tauriProcess.Id);

                CodelyLogger.Log($"Codely: Launched Tauri application (PID: {_tauriProcess.Id}, Port: {port})");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely: Failed to launch Tauri application: {ex.Message}");
            }
        }

        private bool TryAttachToExistingProjectTauriFromLock()
        {
            try
            {
                if (!TryResolveVerifiedTauriProcessFromProjectLock(
                        ProjectLockTauriResolveMode.AttachExternalInstance,
                        out Process existingProcess,
                        out int lockPort,
                        out bool? hasStandaloneWindow))
                {
                    return false;
                }

                _tauriProcess = existingProcess;
                SessionState.SetInt(SESSION_STATE_TAURI_PID_KEY, existingProcess.Id);
                // Detach when a standalone window exists (or when the field is absent → conservative default).
                _isDetachMode = hasStandaloneWindow ?? true;
#if UNITY_EDITOR_OSX
                if (_webViewBridge != null && _webViewBridge.IsInitialized)
                {
                    _webViewBridge.SetHidden(true);
                }
#else
                DisposeWebView2Host();
#endif
                Repaint();

                _tauriServerPort = lockPort;
                SessionState.SetInt(SESSION_STATE_TAURI_PORT_KEY, lockPort);
                CodelyEventTracker.FlushPending(_tauriServerPort);
                CodelyLogger.Log($"Codely[LockAttach]: Restored server port from lock. port={lockPort}");
                if (hasStandaloneWindow.HasValue)
                {
                    CodelyLogger.Log($"Codely[LockAttach]: Synced detach mode from status has_standalone_window={hasStandaloneWindow.Value}");
                }

                // Refresh the single-instance capability cache so later
                // cleanup paths (OnEditorQuitting, CleanupResourcesAsync) don't
                // mistake an attached single-instance APP for a legacy
                // per-workspace build and kill the shared process when this
                // Editor quits. Without this, a freshly-started Editor that
                // attaches via the lock file would default to
                // singleInstance=false and take down every other workspace's
                // UI on quit.
                RefreshTauriSingleInstanceCapability(_tauriServerPort);

                CodelyLogger.Log($"Codely[LockAttach]: Attached to existing Tauri process from lock. pid={existingProcess.Id}, port={lockPort}");
                return true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely[LockAttach]: Failed to attach existing process from lock file: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Discover a running single-instance APP via
        /// <c>~/.codely/app.lock.meta.json</c> (written by the APP on startup).
        /// If found, POST the current workspace to
        /// <c>/api/tauri/attach-embed-workspace</c> so the APP registers this
        /// workspace and writes the per-workspace lock; then attach via the
        /// normal lock-file flow.
        ///
        /// Returns <c>false</c> when no running single-instance APP is
        /// discovered or any step fails — caller falls back to launching a new
        /// APP process.
        /// </summary>
        /// <summary>
        /// Async version of the single-instance APP attach flow.
        ///
        /// Fast checks (file reads, PID lookup) run on the calling thread;
        /// HTTP requests (1s + 3s timeouts) and the polling loop (2s) are
        /// dispatched to a background thread so the Editor main thread is
        /// never blocked for the ~6s worst case.
        ///
        /// When the background work succeeds, the final Unity-specific attach
        /// (<see cref="TryAttachToExistingProjectTauriFromLock"/>) runs on the
        /// calling thread to safely touch WebView, SessionState, and Repaint.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> TryAttachViaRunningSingleInstanceAppAsync()
        {
            try
            {
                string appMetaPath = TauriUtils.GetGlobalAppMetaPath();
                if (string.IsNullOrEmpty(appMetaPath) || !File.Exists(appMetaPath))
                {
                    return false;
                }

                string json = TryReadTextFileWithRetry(appMetaPath, retryCount: 2, retryDelayMs: 30);
                if (string.IsNullOrEmpty(json))
                {
                    return false;
                }

                int pid = TryExtractIntFieldFromJson(json, "pid");
                int port = TryExtractIntFieldFromJson(json, "http_port");
                if (pid <= 0 || port <= 0 || port > 65535)
                {
                    return false;
                }

                // Process liveness check before any HTTP request — avoids
                // blocking on a dead process's port.
                try
                {
                    var proc = Process.GetProcessById(pid);
                    if (proc.HasExited) return false;
                }
                catch (ArgumentException)
                {
                    // PID not found → stale meta file.
                    return false;
                }

                // Prepare values needed by the background work.
                string workspaceDir = GetWorkspaceDirectory();
                string editorType = GetEditorType();
                string lockDir = GetLockDirForWorkspace(workspaceDir);
                string lockFilePath = lockDir != null ? Path.Combine(lockDir, ".codely.lock") : Path.Combine(workspaceDir, ".codely.lock");
                string payload = BuildAttachEmbedWorkspacePayload(workspaceDir, editorType);

                // Run HTTP requests and polling on a background thread so the
                // Editor main thread is not blocked for up to ~6 seconds
                // (1s GET timeout + 3s POST timeout + 2s polling loop).
                bool backgroundSuccess = await System.Threading.Tasks.Task.Run(() =>
                {
                    // Verify the process is actually a single-instance APP (and
                    // responsive on the advertised port).
                    bool singleInstance = false;
                    try
                    {
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                        {
                            var resp = _httpClient.GetAsync($"http://127.0.0.1:{port}/api/tauri/status", cts.Token).GetAwaiter().GetResult();
                            if (!resp.IsSuccessStatusCode) return false;
                            string statusBody = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            TryParseJsonSingleInstanceField(statusBody, out singleInstance);
                        }
                    }
                    catch
                    {
                        return false;
                    }

                    if (!singleInstance)
                    {
                        // Stale meta file from a legacy APP that didn't advertise the flag.
                        return false;
                    }

                    CodelyLogger.Log($"Codely[SingleInstance]: Discovered running APP pid={pid}, port={port}. Requesting attach-embed-workspace for {workspaceDir}");

                    try
                    {
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                        {
                            var resp = _httpClient.PostAsync($"http://127.0.0.1:{port}/api/tauri/attach-embed-workspace", content, cts.Token).GetAwaiter().GetResult();
                            if (!resp.IsSuccessStatusCode)
                            {
                                CodelyLogger.LogWarning($"Codely[SingleInstance]: attach-embed-workspace returned {(int)resp.StatusCode}");
                                return false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogWarning($"Codely[SingleInstance]: attach-embed-workspace request failed: {ex.Message}");
                        return false;
                    }

                    // The APP writes the per-workspace lock file as part of
                    // create_window_session; wait briefly for it to appear.
                    // We only check File.Exists here (no Unity APIs) — the
                    // full attach runs back on the main thread afterwards.
                    const int maxAttempts = 20;
                    const int attemptDelayMs = 100;
                    for (int i = 0; i < maxAttempts; i++)
                    {
                        if (File.Exists(lockFilePath))
                        {
                            return true;
                        }
                        System.Threading.Thread.Sleep(attemptDelayMs);
                    }

                    CodelyLogger.LogWarning("Codely[SingleInstance]: attach succeeded via HTTP but per-workspace lock did not appear in time.");
                    return false;
                });

                if (!backgroundSuccess)
                {
                    return false;
                }

                // Back on main thread: do the Unity-specific attach via the
                // normal lock-file path so all bookkeeping (port, detach mode,
                // pid, WebView state) is populated consistently.

                // Prime the capability cache early. The follow-up
                // TryAttachToExistingProjectTauriFromLock() will also refresh
                // this, but setting it up-front prevents a race where an
                // Editor-quit fires before the per-workspace lock attach
                // completes and the legacy kill path tears down the shared APP.
                SessionState.SetString(SESSION_STATE_TAURI_SINGLE_INSTANCE_KEY, "1");

                if (TryAttachToExistingProjectTauriFromLock())
                {
                    return true;
                }

                CodelyLogger.LogWarning("Codely[SingleInstance]: Lock file appeared but attach failed.");
                return false;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely[SingleInstance]: TryAttachViaRunningSingleInstanceApp failed: {ex.Message}");
                return false;
            }
        }

        private static string BuildAttachEmbedWorkspacePayload(string workspaceDir, string editorType)
        {
            // Manual JSON encoding to avoid pulling in Newtonsoft / System.Text.Json in the Editor assembly.
            string pathEscaped = EscapeJsonString(workspaceDir ?? string.Empty);
            string editorEscaped = EscapeJsonString(editorType ?? "unity");
            return "{\"path\":\"" + pathEscaped + "\",\"editorType\":\"" + editorEscaped + "\"}";
        }

        private static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string TryReadTextFileWithRetry(string filePath, int retryCount, int retryDelayMs)
            => TauriUtils.TryReadFileWithRetry(filePath, retryCount, retryDelayMs);

        private static int TryExtractIntFieldFromJson(string json, string fieldName)
            => TauriUtils.ExtractIntFromJson(json, fieldName);

        // Match "workspace_dir": "<value>" — null values won't match (non-quoted), which is intentional
        private const string WorkspaceDirJsonFieldPattern = "\"workspace_dir\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"";
        // `has_standalone_window` tells the bridge whether the workspace has a
        // native Tauri window open in standalone (non-embed) mode. `null` when
        // no native window is registered for this workspace. This is a
        // distinct field from `embed_mode` (which is forced to `true` on the
        // Editor WebView path for the React frontend's layout), so both
        // consumers of the same endpoint get the semantics they need.
        private const string HasStandaloneWindowJsonFieldPattern = "\"has_standalone_window\"\\s*:\\s*(true|false|null)";
        // Legacy field returned by older APP builds that don't yet know about
        // `has_standalone_window`. On the legacy APP `embed_mode=true` means
        // "the main window is embedded in the Editor WebView" → no standalone
        // window exists → bridge should attach. `embed_mode=false` means the
        // main window is a standalone Tauri APP window → bridge must stay
        // detached. We map it to `has_standalone_window = !embed_mode` as a
        // fallback when the new field is absent. New bridges may still pair
        // with old APPs, so this fallback must remain.
        private const string EmbedModeJsonFieldPattern = "\"embed_mode\"\\s*:\\s*(true|false)";
        private const string SingleInstanceJsonFieldPattern = "\"single_instance\"\\s*:\\s*(true|false)";

        private static bool TryParseJsonWorkspaceDirField(string json, out string workspaceDir)
        {
            workspaceDir = null;
            if (string.IsNullOrEmpty(json))
                return false;

            Match m = Regex.Match(json, WorkspaceDirJsonFieldPattern);
            if (!m.Success)
                return false;

            workspaceDir = m.Groups[1].Value;
            return true;
        }

        /// <summary>
        /// Parses the <c>has_standalone_window</c> field. Sets
        /// <paramref name="hasStandaloneWindow"/> to the parsed bool, or to
        /// <c>null</c> when the JSON has <c>"has_standalone_window": null</c>
        /// (workspace registered but no native window). Returns <c>false</c>
        /// when the field is absent so callers can decide how to treat that
        /// (currently: conservatively stay detached).
        /// </summary>
        private static bool TryParseJsonHasStandaloneWindowField(string json, out bool? hasStandaloneWindow)
        {
            hasStandaloneWindow = null;
            if (string.IsNullOrEmpty(json))
                return false;

            Match m = Regex.Match(json, HasStandaloneWindowJsonFieldPattern, RegexOptions.IgnoreCase);
            if (!m.Success)
                return false;

            string raw = m.Groups[1].Value;
            if (string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
            {
                hasStandaloneWindow = null;
                return true;
            }

            if (bool.TryParse(raw, out bool parsed))
            {
                hasStandaloneWindow = parsed;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Parses the legacy <c>embed_mode</c> field from the Tauri status
        /// response. Used as a fallback for old APP builds that don't yet
        /// emit <c>has_standalone_window</c>. Returns <c>false</c> when the
        /// field is missing.
        /// </summary>
        private static bool TryParseJsonEmbedModeField(string json, out bool embedMode)
        {
            embedMode = false;
            if (string.IsNullOrEmpty(json))
                return false;

            Match m = Regex.Match(json, EmbedModeJsonFieldPattern, RegexOptions.IgnoreCase);
            if (!m.Success)
                return false;

            return bool.TryParse(m.Groups[1].Value, out embedMode);
        }

        /// <summary>
        /// Returns true when the Tauri `/api/tauri/status` response includes
        /// `"single_instance": true`. An absent field is treated as false so
        /// legacy Tauri builds (one process per workspace) keep the classic
        /// kill-on-close behaviour.
        /// </summary>
        private static bool TryParseJsonSingleInstanceField(string json, out bool singleInstance)
        {
            singleInstance = false;
            if (string.IsNullOrEmpty(json))
                return false;

            Match m = Regex.Match(json, SingleInstanceJsonFieldPattern, RegexOptions.IgnoreCase);
            if (!m.Success)
                return false;

            return bool.TryParse(m.Groups[1].Value, out singleInstance);
        }

        /// <summary>
        /// Probes the running Tauri server at the given port for its
        /// `single_instance` capability flag and caches the answer in
        /// SessionState. Blocking HTTP call, 1 s timeout — safe to call from
        /// OnTauriServerReady (already on a background-capable path) or any
        /// other place that has a live port. Never throws; on error (e.g.
        /// legacy Tauri without the field) the cache is set to "0" so the
        /// legacy kill-on-close code path runs.
        /// </summary>
        private static void RefreshTauriSingleInstanceCapability(int port)
        {
            if (port <= 0) return;
            bool singleInstance = false;
            try
            {
                string url = $"http://127.0.0.1:{port}/api/tauri/status";
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(1)))
                {
                    var response = _httpClient.GetAsync(url, cts.Token).GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode)
                    {
                        string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        TryParseJsonSingleInstanceField(json, out singleInstance);
                    }
                }
            }
            catch
            {
                // Network/parse error → treat as legacy (singleInstance=false).
            }
            SessionState.SetString(SESSION_STATE_TAURI_SINGLE_INSTANCE_KEY, singleInstance ? "1" : "0");
        }

        /// <summary>
        /// Calls GET http://127.0.0.1:{port}/api/tauri/status?workspaceDir=... on the running Tauri process
        /// only to query standalone-window state. Workspace ownership is verified
        /// from <c>.codely.lock.meta.json</c>, not from the status response.
        /// Returns false on any HTTP/network error so that a failed check causes attach to be skipped.
        /// When <paramref name="port"/> is less than or equal to 0, no HTTP request is made and this returns <c>true</c>
        /// (legacy behaviour).
        ///
        /// <paramref name="hasStandaloneWindow"/> reports whether the workspace has a native Tauri window open in
        /// standalone (non-embed) mode (from the <c>has_standalone_window</c> field). This lets the bridge
        /// distinguish between a pre-existing standalone Tauri APP window (<c>true</c> → Unity stays detached)
        /// and an embedded window it previously owned (<c>false</c> → Unity attaches). <c>null</c> means no
        /// native window is registered for this workspace.
        /// </summary>
        private static bool TryQueryTauriStandaloneWindowState(int port, string expectedWorkspaceDir, out bool? hasStandaloneWindow)
        {
            hasStandaloneWindow = null;
            if (port <= 0)
                return true;

            try
            {
                string workspaceDirParam = Uri.EscapeDataString(expectedWorkspaceDir ?? string.Empty);
                string url = $"http://127.0.0.1:{port}/api/tauri/status?workspaceDir={workspaceDirParam}";
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var response = _httpClient.GetAsync(url, cts.Token).GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        CodelyLogger.LogWarning($"Codely[LockAttach]: status request returned {(int)response.StatusCode}, skip attach.");
                        return false;
                    }

                    string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (TryParseJsonHasStandaloneWindowField(json, out bool? parsed))
                    {
                        hasStandaloneWindow = parsed;
                    }
                    else if (TryParseJsonEmbedModeField(json, out bool legacyEmbedMode))
                    {
                        // Legacy APP (pre-has_standalone_window): derive the new
                        // semantics from the old field. embed_mode=true → embedded
                        // main window → no standalone window → attach.
                        hasStandaloneWindow = !legacyEmbedMode;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely[LockAttach]: standalone state query failed: {ex.Message}, skip attach.");
                return false;
            }
        }

        /// <summary>
        /// True when lock metadata JSON contains a workspace_dir that matches the expected project path (normalized).
        /// </summary>
        private static bool LockMetaWorkspaceMatchesJson(string lockMetaJson, string expectedWorkspaceDir)
        {
            if (string.IsNullOrEmpty(lockMetaJson) || string.IsNullOrEmpty(expectedWorkspaceDir))
                return false;

            if (!TryParseJsonWorkspaceDirField(lockMetaJson, out string remoteWorkspace))
                return false;

            return string.Equals(
                NormalizePathForComparison(expectedWorkspaceDir),
                NormalizePathForComparison(remoteWorkspace),
                StringComparison.OrdinalIgnoreCase);
        }

        private enum ProjectLockTauriResolveMode
        {
            /// <summary>Attach to an already-running desktop Tauri: require valid <c>http_port</c> in meta; query standalone state via HTTP.</summary>
            AttachExternalInstance,
            /// <summary>Refresh PID after in-place update: verify <c>workspace_dir</c> from lock metadata.</summary>
            RefreshProcessIdentity,
        }

        /// <summary>
        /// Reads <c>.codely.lock.meta.json</c>, verifies workspace, returns a live <see cref="Process"/> for the lock PID.
        /// </summary>
        /// <param name="lockHttpPortFromMeta"><c>http_port</c> from JSON, or -1 if absent/invalid.</param>
        private bool TryResolveVerifiedTauriProcessFromProjectLock(
            ProjectLockTauriResolveMode mode,
            out Process process,
            out int lockHttpPortFromMeta,
            out bool? hasStandaloneWindow)
        {
            process = null;
            lockHttpPortFromMeta = -1;
            hasStandaloneWindow = null;

            string workspaceDir = GetWorkspaceDirectory();
            string lockDir = GetLockDirForWorkspace(workspaceDir);
            string lockFilePath = lockDir != null ? Path.Combine(lockDir, ".codely.lock") : Path.Combine(workspaceDir, ".codely.lock");
            string lockMetaPath = lockDir != null ? Path.Combine(lockDir, ".codely.lock.meta.json") : Path.Combine(workspaceDir, ".codely.lock.meta.json");

            if (mode == ProjectLockTauriResolveMode.AttachExternalInstance)
            {
                CodelyLogger.Log($"Codely[LockAttach]: Checking lock metadata. workspace={workspaceDir}, meta={lockMetaPath}");
            }

            string lockContent = TryReadTextFileWithRetry(lockMetaPath, retryCount: 3, retryDelayMs: 50);
            if (string.IsNullOrEmpty(lockContent))
            {
                if (mode == ProjectLockTauriResolveMode.AttachExternalInstance)
                {
                    CodelyLogger.Log($"Codely[LockAttach]: Lock metadata not found or empty, skip attach. meta={lockMetaPath}, lock={lockFilePath}");
                }

                return false;
            }

            int lockPid = TryExtractIntFieldFromJson(lockContent, "pid");
            int lockPort = TryExtractIntFieldFromJson(lockContent, "http_port");
            lockHttpPortFromMeta = lockPort;

            if (mode == ProjectLockTauriResolveMode.AttachExternalInstance)
            {
                CodelyLogger.Log($"Codely[LockAttach]: Parsed lock metadata. pid={lockPid}, http_port={lockPort}");
            }

            if (lockPid <= 0)
            {
                if (mode == ProjectLockTauriResolveMode.AttachExternalInstance)
                {
                    CodelyLogger.LogWarning($"Codely[LockAttach]: Lock file found but pid is invalid. lock={lockFilePath}");
                }

                return false;
            }

            if (mode == ProjectLockTauriResolveMode.AttachExternalInstance)
            {
                if (lockPort <= 0 || lockPort > 65535)
                {
                    CodelyLogger.LogWarning($"Codely[LockAttach]: Lock file found but http_port is missing or invalid. lock={lockFilePath}, port={lockPort}");
                    return false;
                }
            }

            // Check process is alive BEFORE making any HTTP request, to avoid blocking on a dead process's port.
            Process existingProcess;
            try
            {
                existingProcess = Process.GetProcessById(lockPid);
            }
            catch (ArgumentException)
            {
                if (mode == ProjectLockTauriResolveMode.AttachExternalInstance)
                {
                    CodelyLogger.LogWarning($"Codely[LockAttach]: Lock pid not found in process table. pid={lockPid}, meta={lockMetaPath}");
                }

                return false;
            }

            if (existingProcess.HasExited)
            {
                if (mode == ProjectLockTauriResolveMode.AttachExternalInstance)
                {
                    CodelyLogger.LogWarning($"Codely[LockAttach]: Lock pid exists but already exited. pid={lockPid}");
                }

                return false;
            }

            if (mode == ProjectLockTauriResolveMode.AttachExternalInstance)
            {
                CodelyLogger.Log($"Codely[LockAttach]: Found alive process. pid={existingProcess.Id}, name={existingProcess.ProcessName}");
            }

            bool workspaceOk = LockMetaWorkspaceMatchesJson(lockContent, workspaceDir);
            if (!workspaceOk)
            {
                if (mode == ProjectLockTauriResolveMode.AttachExternalInstance)
                {
                    CodelyLogger.LogWarning($"Codely[LockAttach]: Lock metadata workspace mismatch, skip attach. pid={lockPid}, meta={lockMetaPath}");
                }

                return false;
            }

            if (mode == ProjectLockTauriResolveMode.AttachExternalInstance)
            {
                // Status is now only used to distinguish standalone vs embedded state.
                if (!TryQueryTauriStandaloneWindowState(lockPort, workspaceDir, out hasStandaloneWindow))
                {
                    CodelyLogger.LogWarning($"Codely[LockAttach]: Standalone state unavailable, skip attach. pid={lockPid}, port={lockPort}");
                    return false;
                }
            }

            process = existingProcess;
            return true;
        }

        /// <summary>
        /// After an in-place Tauri update the app restarts with a new PID; Unity may still hold the old <see cref="_tauriProcess"/>.
        /// Refresh from <c>.codely.lock.meta.json</c> when IPC/server is back so close/cleanup kills the live process.
        /// </summary>
        private void TryRefreshTauriProcessFromProjectLock()
        {
            try
            {
                if (!TryResolveVerifiedTauriProcessFromProjectLock(
                        ProjectLockTauriResolveMode.RefreshProcessIdentity,
                        out Process fresh,
                        out _,
                        out _))
                {
                    return;
                }

                if (_tauriProcess != null && !_tauriProcess.HasExited && _tauriProcess.Id == fresh.Id)
                {
                    fresh.Dispose();
                    return;
                }

                Process previous = _tauriProcess;
                _tauriProcess = fresh;
                SessionState.SetInt(SESSION_STATE_TAURI_PID_KEY, fresh.Id);
                // Do not overwrite _tauriServerPort / SESSION_STATE_TAURI_PORT_KEY here: URL/on-ready may already have the live port.

                try
                {
                    previous?.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // already disposed
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"Codely: Failed to dispose previous Tauri process handle: {ex.Message}");
                }

                CodelyLogger.Log($"Codely: Refreshed Tauri process tracking from lock meta (PID: {fresh.Id})");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely: TryRefreshTauriProcessFromProjectLock failed: {ex.Message}");
            }
        }

        private static string NormalizePathForComparison(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            // Replace backslashes, collapse multiple slashes into one, trim trailing slash
            string normalized = path.Replace('\\', '/');
            normalized = Regex.Replace(normalized, "/+", "/");
            return normalized.TrimEnd('/');
        }

        private static string GetWorkspaceDirectory() => TauriUtils.GetWorkspaceDirectory();

        /// <summary>
        /// Returns the lock directory for the given workspace under ~/.codely/projects/&lt;hash&gt;/.
        /// Hash is the first 16 hex chars of SHA256(normalized workspace path), matching single_instance.rs.
        /// </summary>
        private static string GetLockDirForWorkspace(string workspaceDir)
            => TauriUtils.GetLockDirForWorkspace(workspaceDir);

        /// <summary>
        /// Resolves the full path to the cowork runnable without requiring a Unity restart.
        /// Reads CODELY_APP_HOME from the process environment or OS-level store (registry on Windows,
        /// login / interactive-login shell on macOS), appends the platform-specific executable name, and verifies the
        /// file exists. Returns null (with an error logged) if CODELY_APP_HOME is unset or the
        /// executable is missing.
        /// </summary>
        internal static string GetCodelyRunnablePublic() => TauriUtils.GetCodelyRunnable();

        private static string GetCodelyRunnable() => TauriUtils.GetCodelyRunnable();

        /// <summary>
        /// 尝试从 SessionState 恢复 Tauri 进程（域重载后调用）。
        /// </summary>
        private bool TryRestoreTauriProcess()
        {
            int savedPid = SessionState.GetInt(SESSION_STATE_TAURI_PID_KEY, -1);

            if (savedPid > 0)
            {
                try
                {
                    var process = Process.GetProcessById(savedPid);
                    if (!process.HasExited)
                    {
                        _tauriProcess = process;

                        _tauriServerPort = SessionState.GetInt(SESSION_STATE_TAURI_PORT_KEY, -1);
                        if (_tauriServerPort > 0)
                        {
                            CodelyLogger.Log($"Codely: Restored Tauri server port from SessionState (Port: {_tauriServerPort})");
                        }

#if UNITY_EDITOR_OSX
                        // macOS: 从 SessionState 恢复窗口句柄（用于 macOS native 资源追踪）
                        string savedHandleStr = SessionState.GetString(SESSION_STATE_TAURI_WINDOW_HANDLE_KEY, "");
                        if (!string.IsNullOrEmpty(savedHandleStr) && long.TryParse(savedHandleStr, out long handleValue))
                        {
                            try
                            {
                                IntPtr savedHandle = new IntPtr(handleValue);
                                CodelyLogger.Log($"Codely: Restored Tauri handle from SessionState (Handle: 0x{savedHandle.ToString("X")})");
                            }
                            catch (Exception ex)
                            {
                                CodelyLogger.LogWarning($"Codely: Failed to restore handle: {ex.Message}");
                            }
                        }
#endif

                        CodelyLogger.Log($"Codely: Restored Tauri process from SessionState (PID: {savedPid})");

                        // Refresh single-instance capability so later attach
                        // attempts know this process is shared across workspaces.
                        if (_tauriServerPort > 0)
                        {
                            RefreshTauriSingleInstanceCapability(_tauriServerPort);
                        }

                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // Process doesn't exist
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"Codely: Failed to restore process: {ex.Message}");
                }
            }

            // Clear stale state
            SessionState.EraseInt(SESSION_STATE_TAURI_PID_KEY);
            SessionState.EraseString(SESSION_STATE_TAURI_WINDOW_HANDLE_KEY);
            SessionState.EraseInt(SESSION_STATE_TAURI_PORT_KEY);
            return false;
        }

        /// <summary>
        /// Drops this window's references to the Tauri process and its IPC client.
        /// The process itself is left running and is re-attached to on the next open.
        /// </summary>
        private void CleanupTauriProcess()
        {
#if UNITY_EDITOR_WIN
            DisposeWebView2Host();
#endif
            _tauriProcess = null;
            SessionState.EraseInt(SESSION_STATE_TAURI_PID_KEY);
            SessionState.EraseString(SESSION_STATE_TAURI_WINDOW_HANDLE_KEY);
            SessionState.EraseInt(SESSION_STATE_TAURI_PORT_KEY);
            _tauriServerPort = -1;

            // Stop IPC client when window is destroyed
            StopIpcClient();
        }

        private void AcquireIpcClient()
        {
            if (!_ipcAcquired)
            {
                if (_ipcOwnerId == null) _ipcOwnerId = $"CodelyWindow:{this.GetStableInstanceId()}";
                CodelyIpcManager.Acquire(_ipcOwnerId);
                _ipcAcquired = true;
            }

            if (_ipcEventsSubscribed)
            {
                return;
            }

            CodelyIpcManager.OnServerReady += OnTauriServerReady;
            CodelyIpcManager.OnSetEmbedMode += OnIpcSetEmbedMode;
            CodelyIpcManager.OnCloseCodelyEditorOnWorkspaceSwitch += OnIpcCloseCodelyEditorOnWorkspaceSwitch;
            CodelyIpcManager.OnConnectionChanged += OnIpcConnectionChanged;
            CodelyIpcManager.OnUpdateRestart += OnIpcUpdateRestart;
            _ipcEventsSubscribed = true;
        }

        private void ReleaseIpcClient()
        {
            if (_ipcEventsSubscribed)
            {
                CodelyIpcManager.OnServerReady -= OnTauriServerReady;
                CodelyIpcManager.OnSetEmbedMode -= OnIpcSetEmbedMode;
                CodelyIpcManager.OnCloseCodelyEditorOnWorkspaceSwitch -= OnIpcCloseCodelyEditorOnWorkspaceSwitch;
                CodelyIpcManager.OnConnectionChanged -= OnIpcConnectionChanged;
                CodelyIpcManager.OnUpdateRestart -= OnIpcUpdateRestart;
                _ipcEventsSubscribed = false;
            }

            if (_ipcAcquired && !string.IsNullOrEmpty(_ipcOwnerId))
            {
                CodelyIpcManager.Release(_ipcOwnerId);
                _ipcAcquired = false;
            }
        }

        #region Panel lifecycle metrics

        private void TrackPanelOpenedIfNeeded()
        {
            if (_suppressPanelLifecycleMetrics)
            {
                return;
            }

            if (_eventTrackedOnEnable || _panelOpenedMetricQueued)
            {
                return;
            }

            if (CodelyEventTracker.Track(
                    CodelyEventTracker.CodelyPanelOpened,
                    tauriServerPort: _tauriServerPort))
            {
                _eventTrackedOnEnable = true;
                return;
            }

            _panelOpenedMetricQueued = true;
        }

        private void MarkPanelOpenedMetricDelivered()
        {
            if (!_eventTrackedOnEnable && _panelOpenedMetricQueued)
            {
                _eventTrackedOnEnable = true;
                _panelOpenedMetricQueued = false;
            }
        }

        private void TryNotifyPanelClosed()
        {
            if (!_eventTrackedOnEnable)
            {
                return;
            }

            if (_suppressPanelLifecycleMetrics)
            {
                return;
            }

            if (CodelyEventTracker.ConsumeSuppressNextPanelClose())
            {
                return;
            }

            CodelyEventTracker.TryTrackClose(
                CodelyEventTracker.CodelyPanelClosed,
                _tauriServerPort,
                ref _panelClosedEventSent);
        }

        #endregion

        private void AttachToEditorFromDetachMode()
        {
            if (_isAttachingToEditor || !_isDetachMode)
            {
                return;
            }

            _isAttachingToEditor = true;
            Repaint();

            try
            {
                _isDetachMode = false;
                Repaint();

#if UNITY_EDITOR_OSX
                if (!_webViewInitialized)
                {
                    InitializeWebView();
                }
                else if (_webViewBridge != null && _webViewBridge.IsInitialized)
                {
                    _webViewBridge.LoadURL(GetCurrentWebViewUrl());
                    _webViewBridge.SetHidden(false);
                    UpdateWebViewFrame();
                }
#else
                if (_webView2Host != null && _webView2Host.IsReady)
                {
                    _webView2Host.Navigate(GetCurrentWebViewUrl());
                }
                else
                {
                    TryInitializeWebView2();
                }
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely: Attach to editor failed: {ex.Message}");
            }
            finally
            {
                _isAttachingToEditor = false;
                Repaint();
            }
        }

        /// <summary>
        /// Called when Tauri sends a setEmbedMode IPC message.
        /// When embedMode is true, exit detach mode and load the main webview.
        /// </summary>
        private void OnIpcSetEmbedMode(bool embedMode)
        {
            CodelyLogger.Log($"Codely: Received setEmbedMode={embedMode} via IPC");
            if (embedMode)
            {
                AttachToEditorFromDetachMode();
            }
            else
            {
                // User triggered detach from the TitleBar button — switch Unity panel to detach mode
                OnSetEmbedModeFromTauri(false);
            }
        }

        /// <summary>
        /// Tauri switched to another project and will change the IPC pipe; close this EditorWindow if still in detach mode.
        /// Only clears Unity-side state; the Tauri process keeps running.
        /// </summary>
        private void OnIpcCloseCodelyEditorOnWorkspaceSwitch()
        {
            CodelyLogger.Log("Codely: Workspace switch from Tauri — closing Codely window (detach mode), preserving Tauri process");

            _preserveTauriProcessOnDestroy = true;
            _isDetachMode = false;
            Close();
        }

        /// <summary>
        /// Called when the IPC connection to the Tauri server changes.
        /// If the connection drops and the Tauri process has exited, close this window.
        /// </summary>
        private void OnIpcConnectionChanged(bool connected)
        {
            if (connected)
            {
                // Tauri 重新连接
                _suppressCloseUntilUtc = DateTime.UtcNow.AddSeconds(UPDATE_RESTART_DISCONNECT_GRACE_SECONDS);
                TryRefreshTauriProcessFromProjectLock();
                if (_isExpectingTauriRestart)
                {
                    _isExpectingTauriRestart = false;
                    _showUpdateLoadingUI = false;
                    ApplyWebViewHiddenForUpdateLoading(false);
                    CodelyLogger.Log("Codely: Tauri reconnected after update, hiding loading UI");
                    // Repaint 必须在主线程调用
                    EditorApplication.delayCall += Repaint;
                }
            }
            else
            {
                // Tauri 断开连接
                EditorApplication.delayCall += () =>
                {
                    // 预期更新重启场景，或处于重连后的保护窗口内，不要关闭窗口
                    if (_isExpectingTauriRestart || DateTime.UtcNow < _suppressCloseUntilUtc)
                    {
                        CodelyLogger.Log("Codely: Tauri disconnected during update/reconnect grace period, waiting...");
                        return;
                    }

                    if (_tauriProcess != null && _tauriProcess.HasExited)
                    {
                        CodelyLogger.Log("Codely: Tauri process exited, closing Codely window");
                        Close();
                    }
                };
            }
        }

        /// <summary>
        /// Called when Tauri sends an update restart notification.
        /// </summary>
        private void OnIpcUpdateRestart(string payload)
        {
            CodelyLogger.Log($"Codely: Tauri update restart detected, payload: {payload}");

            // 解析版本信息
            _updateVersion = "latest";
            if (!string.IsNullOrEmpty(payload))
            {
                try
                {
                    var versionData = JsonUtility.FromJson<UpdateVersionData>(payload);
                    if (!string.IsNullOrEmpty(versionData?.version))
                    {
                        _updateVersion = versionData.version;
                    }
                }
                catch
                {
                    _updateVersion = "latest";
                }
            }

            // 保存状态
            SessionState.SetBool(SESSION_STATE_WINDOW_WAS_OPEN_KEY, true);
            SessionState.SetString(SESSION_STATE_UPDATE_VERSION_KEY, _updateVersion);

            // 显示 loading UI
            _showUpdateLoadingUI = true;
            _isExpectingTauriRestart = true;
            _suppressCloseUntilUtc = DateTime.UtcNow.AddSeconds(UPDATE_RESTART_DISCONNECT_GRACE_SECONDS);

#if UNITY_EDITOR_WIN
            if (_isDragOverlay)
            {
                _isDragOverlay = false;
                DisposeDragSnapshot();
            }
#endif
            ApplyWebViewHiddenForUpdateLoading(true);

            // 开始动画
            _loadingAnimationTime = 0f;

            // Repaint 必须在主线程调用
            EditorApplication.delayCall += Repaint;

            // 设置超时保护（30秒）
            EditorApplication.delayCall += () =>
            {
                System.Threading.Tasks.Task.Delay(30000).ContinueWith(_ =>
                {
                    EditorApplication.delayCall += () =>
                    {
                        if (_isExpectingTauriRestart)
                        {
                            _isExpectingTauriRestart = false;
                            _showUpdateLoadingUI = false;
                            ApplyWebViewHiddenForUpdateLoading(false);
                            CodelyLogger.LogWarning("Codely: Tauri restart timeout");
                            EditorUtility.DisplayDialog(
                                "Update Timeout",
                                "Tauri update took too long. Please check if the update completed manually.",
                                "OK"
                            );
                            Close();
                        }
                    };
                });
            };
        }

        [Serializable]
        private class UpdateVersionData
        {
            public string version;
        }

        /// <summary>
        /// Stop the IPC client.
        /// </summary>
        private void StopIpcClient()
        {
            StopIpcClientStatic();
            CodelyLogger.Log("Codely: IPC client stopped");
        }

        private IntPtr GetEditorWindowHandle()
        {
            return EditorWindowNativeHandleHelper.GetGUIViewHandle(this);
        }

        private static int GetLiveCodelyWindowCount()
        {
            return Resources.FindObjectsOfTypeAll<CodelyWindow>().Count(window => window != null);
        }

        private bool HasAnotherLiveCodelyWindow()
        {
            return Resources.FindObjectsOfTypeAll<CodelyWindow>().Any(window => window != null && window != this);
        }

        /// <summary>
        /// Check for theme changes and send to Tauri if changed
        /// </summary>
        private void CheckThemeChange()
        {
            try
            {
                bool currentIsProSkin = EditorGUIUtility.isProSkin;

                // Mark dirty when theme changes
                if (currentIsProSkin != _lastObservedIsProSkin)
                {
                    _lastObservedIsProSkin = currentIsProSkin;
                    _themeModeDirty = true;
                    CodelyLogger.Log($"Codely: Theme changed to {(currentIsProSkin ? "dark" : "light")}");
                }

                // If we haven't successfully sent the current theme yet, keep trying until it succeeds
                if (_themeModeDirty)
                {
                    if (TrySendThemeModeToTauri())
                    {
                        _themeModeDirty = false;
                    }
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely: Error checking theme change: {ex.Message}");
            }
        }

        /// <summary>
        /// Send debugUnityConsole message to Tauri
        /// </summary>
        private static void SendDebugUnityConsoleMessageStatic()
        {
            try
            {
                bool sent = CodelyIpcManager.TrySend(IpcMessageType.DebugUnityConsole, "");

                if (sent)
                {
                    FocusEmbeddedTauriWindow();
                    CodelyLogger.Log("Codely: Sent debugUnityConsole message to Tauri");
                }
                else
                {
                    CodelyLogger.LogWarning("Codely: Failed to send debugUnityConsole message (IPC not connected)");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely: Error sending debugUnityConsole message: {ex.Message}");
            }
        }

        /// <summary>
        /// Send focusInputWithNewSession message to Tauri (Ctrl+N shortcut).
        /// 因 Tauri 窗口已通过 SetParent 嵌入 Unity，子窗口无法通过 Tauri 的 set_focus 抢焦点，
        /// 必须由父进程 (Unity) 调用 SetFocus(子窗口句柄) 将焦点交给嵌入的 Tauri 窗口。
        /// </summary>
        private static void SendFocusInputWithNewSessionMessageStatic()
        {
            try
            {
                bool sent = CodelyIpcManager.TrySend(IpcMessageType.FocusInputWithNewSession, "");

                if (sent)
                {
                    // 将焦点从 Unity 转移到嵌入的 Tauri 子窗口，否则焦点会一直留在 Unity
                    FocusEmbeddedTauriWindow();
                    CodelyLogger.Log("Codely: Sent focusInputWithNewSession message to Tauri");
                }
                else
                {
                    CodelyLogger.LogWarning("Codely: Failed to send focusInputWithNewSession message (IPC not connected)");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely: Error sending focusInputWithNewSession message: {ex.Message}");
            }
        }

        /// <summary>
        /// 将键盘焦点设到已嵌入的 Tauri 子窗口上（仅当通过 SetParent 嵌入时有效）。
        /// </summary>
        private static void FocusEmbeddedTauriWindow()
        {
#if UNITY_EDITOR_WIN
            // WebView2 内嵌为 Unity GUIView 的子窗口：仅 EditorWindow.Focus() 只会把 Win32
            // 焦点给到 GUIView 父窗口，WebView2 内容不会自动接管，HTML 的 autofocus /
            // element.focus() 因此不显示光标、收不到键盘输入。必须再调用 MoveFocus 把焦点
            // 显式交给 WebView2 内容（等价于浏览器中窗口被激活时内容自动获得焦点）。
            var windows = Resources.FindObjectsOfTypeAll<CodelyWindow>();
            if (windows.Length > 0)
            {
                var win = windows[0];
                win.Focus();
                win._webView2Host?.MoveFocus();
            }
#elif UNITY_EDITOR_OSX
            // On macOS, make both the Unity window and the embedded WKWebView focused.
            var windows = Resources.FindObjectsOfTypeAll<CodelyWindow>();
            if (windows.Length == 0) return;

            var window = windows[0];
            window.Focus();
            window._webViewBridge?.Focus();
#endif
        }

        /// <summary>
        /// Send theme colors to Tauri
        /// </summary>
        private bool TrySendThemeModeToTauri()
        {
            try
            {
                // 只发送主题类型（dark 或 light），Rust 端会根据类型获取颜色
                string themeMode = EditorGUIUtility.isProSkin ? "dark" : "light";

                // No need to send duplicates
                if (_lastSentThemeMode == themeMode)
                {
                    return true;
                }

                if (!CodelyIpcManager.IsConnected)
                {
                    return false;
                }

                bool sent = CodelyIpcManager.TrySend(IpcMessageType.SetColors, themeMode);

                if (sent)
                {
                    _lastSentThemeMode = themeMode;
                }

                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely: Error sending theme colors: {ex.Message}");
                return false;
            }
        }

        // Backwards-compatible wrapper
        private void SendThemeColorsToTauri()
        {
            // Mark dirty and attempt once
            _themeModeDirty = true;
            TrySendThemeModeToTauri();
        }

        /// <summary>
        /// Returns <c>"tuanjie"</c> if the project uses the Tuanjie engine, otherwise <c>"unity"</c>.
        /// Public wrapper kept for external callers; implementation lives in <see cref="TauriUtils"/>.
        /// </summary>
        public static string GetEditorType() => TauriUtils.GetEditorType();

        /// <summary>
        /// Handle drag and drop events from Unity editor (Project/Hierarchy) into the Codely window.
        /// The web view is temporarily hidden by UpdateDragPassthrough() so that Unity's GUIView
        /// receives mouse messages and dispatches DragUpdated/DragPerform here.
        /// </summary>
        private void HandleDragAndDrop(Rect dropArea)
        {
            Event evt = Event.current;
            _isDragExitInThisFrame = false;
            switch (evt.type)
            {
                case EventType.DragUpdated:
                    if (dropArea.Contains(evt.mousePosition))
                    {
                        bool hasObjects = DragAndDrop.objectReferences != null && DragAndDrop.objectReferences.Length > 0;
                        bool hasPaths = DragAndDrop.paths != null && DragAndDrop.paths.Length > 0;

                        if (hasObjects || hasPaths)
                        {
                            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                            evt.Use();
                        }
                    }
                    break;

                case EventType.DragPerform:
                    if (dropArea.Contains(evt.mousePosition))
                    {
                        DragAndDrop.AcceptDrag();
                        evt.Use();
                        AddContexts();
                        RestoreTauriFromDragOverlay();
                    }
                    break;

                case EventType.DragExited:
                    RestoreTauriFromDragOverlay();
                    _isDragExitInThisFrame = true;
                    break;
            }
        }

        /// <summary>
        /// Polls each frame: when a Unity drag is detected and the cursor is over this window,
        /// hides the web view so that Unity's GUIView receives drag events.
        /// </summary>
        private void UpdateDragPassthrough()
        {
#if UNITY_EDITOR_WIN
            if (_webView2Host == null || !_webView2Host.IsReady) return;
#elif UNITY_EDITOR_OSX
            if (_webViewBridge == null || !_webViewBridge.IsInitialized) return;
#endif

            bool hasDragData =
                DragAndDrop.objectReferences != null && DragAndDrop.objectReferences.Length > 0; // Only handle Unity object references. External file path should be handled by Tauri.
#if UNITY_EDITOR_WIN
            bool mouseDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
#elif UNITY_EDITOR_OSX
            bool mouseDown = IsPrimaryMouseButtonDownOsx();
#endif

            if (!hasDragData || !mouseDown)
            {
                // No active drag: clear the per-frame "drag exit" guard. It's normally reset at
                // the top of HandleDragAndDrop, but that only runs from OnGUI. If the user drags
                // out of this window (DragExited sets the flag), releases the mouse outside, and
                // starts a fresh drag before this window next repaints, the stale flag would
                // suppress the next overlay activation in UpdateDragPassthrough.
                _isDragExitInThisFrame = false;
                return;
            }

#if UNITY_EDITOR_WIN
            // Use cursor-position check as fallback: when WebView2 is on top it intercepts OS mouse
            // messages, preventing Unity from updating mouseOverWindow for this EditorWindow.
            if (!_isDragOverlay && (mouseOverWindow == this || IsCursorOverThisWindow()) && !_isDragExitInThisFrame)
            {
                // Freeze the current WebView2 frame as a Texture2D BEFORE hiding it, so the
                // overlay can render it as background and the user doesn't see a blank rect.
                CaptureDragSnapshot();
                _webView2Host.SetVisible(false);
#elif UNITY_EDITOR_OSX
            string titleTrim = (titleContent?.text ?? string.Empty).Trim();
            if (!_isDragOverlay &&
                (mouseOverWindow == this) &&
                !_isDragExitInThisFrame)
            {
                _webViewBridge.SetHidden(true);
#endif
                _isDragOverlay = true;
                Repaint();
            }
        }

#if UNITY_EDITOR_WIN
        /// <summary>
        /// Returns true when the OS cursor lies within this EditorWindow's screen rect.
        /// Used as a fallback for mouseOverWindow when WebView2 blocks Unity's hover detection.
        ///
        /// Prefers comparing the cursor against the backing GUIView HWND's screen rect
        /// (both in physical pixels — no DPI conversion involved). This avoids two
        /// failure modes of the legacy position+pixelsPerPoint approach:
        ///   1. EditorGUIUtility.pixelsPerPoint is documented for the current GUI
        ///      rendering operation; outside OnGUI (we're called from
        ///      EditorApplication.update) it may reflect the wrong monitor on
        ///      multi-monitor mixed-DPI setups.
        ///   2. EditorWindow.position semantics differ between floating and docked
        ///      tabs (docked excludes the tab strip), so logical-point containment
        ///      can miss the dock host's slack.
        /// </summary>
        private bool IsCursorOverThisWindow()
        {
            if (!GetCursorPos(out POINT cp)) return false;

            IntPtr hwnd = _currentGUIViewHWND;
            if (hwnd != IntPtr.Zero && IsWindow(hwnd) && GetWindowRect(hwnd, out RECT wr))
            {
                return cp.X >= wr.Left && cp.X < wr.Right
                    && cp.Y >= wr.Top  && cp.Y < wr.Bottom;
            }

            // Fallback for the brief window before _currentGUIViewHWND is populated
            // (first frames after OnEnable, before WebView2 initializes).
            float ppp = EditorGUIUtility.pixelsPerPoint;
            if (ppp <= 0f) ppp = 1f;
            return position.Contains(new Vector2(cp.X / ppp, cp.Y / ppp));
        }
#endif


        /// <summary>
        /// 拖拽结束后恢复 WebView2 可见性（仅当本窗口标签仍可见；否则保持隐藏以免盖住其他 tab）。
        /// </summary>
        private void RestoreTauriFromDragOverlay()
        {
            if (!_isDragOverlay) return;
            _isDragOverlay = false;
#if UNITY_EDITOR_WIN
            DisposeDragSnapshot();
            _webView2Host?.SetVisible(true);
#elif UNITY_EDITOR_OSX
            _webViewBridge?.SetHidden(false);
#endif
            Repaint();
        }

        // Brand green used by the React GlobalDragOverlay (#35C9A9).
        private static readonly Color s_dragBrandGreen = new Color(0x35 / 255f, 0xC9 / 255f, 0xA9 / 255f, 1f);

        /// <summary>
        /// Layered drag overlay modeled on continue/gui's GlobalDragOverlay.tsx:
        ///   1. Frozen WebView2 snapshot as background (Windows only — falls back
        ///      to a flat dim panel on macOS where capture isn't wired up).
        ///   2. Translucent backdrop (the "badge background @ 50% alpha" layer).
        ///   3. Rounded frame with #35C9A9 2px border and #35C9A9 @ ~10% fill (8 px corner).
        ///   4. Centered rounded card (12 px corner) with upload icon, title, body, and chips.
        /// Corner rounding is delivered via small procedurally-built 9-slice textures
        /// (BuildRoundedRectTex) cached in EnsureOverlayResources and reused across frames.
        /// </summary>
        private void DrawDropOverlay(Rect rect)
        {
            var prevColor = GUI.color;
            EnsureOverlayResources();

            // ── 1. Background: snapshot if we have one, otherwise a flat dim panel.
#if UNITY_EDITOR_WIN
            if (_dragSnapshotTex != null)
            {
                float texH    = _dragSnapshotTex.height;
                float insetPx = Mathf.Clamp(_dragSnapshotTopInsetPx, 0, texH - 1);
                float vHeight = (texH - insetPx) / texH;

                // Outset by 1 px on each side (total +2 px) so physical→logical-point
                // rounding doesn't leave a sliver of original UI peeking at the edges.
                var bgRect = new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f);

                GUI.color = Color.white;
                GUI.DrawTextureWithTexCoords(bgRect, _dragSnapshotTex, new Rect(0f, 0f, 1f, vHeight));
            }
            else
#endif
            {
                GUI.color = _lastObservedIsProSkin
                    ? new Color(0.13f, 0.15f, 0.16f, 1.00f)
                    : new Color(0.92f, 0.92f, 0.92f, 1.00f);
                GUI.DrawTexture(rect, EditorGUIUtility.whiteTexture);
            }

            // ── 2. Backdrop tint. The React component uses `${vscBadgeBackground}80`
            // (~50% alpha), which over a dark Codely page barely changes the colour.
            // Match that subtlety — too much tint here makes "outside the frame" look
            // visibly darker than "inside the frame", which doesn't match the target.
            GUI.color = _lastObservedIsProSkin
                ? new Color(0f, 0f, 0f, 0.18f)
                : new Color(0f, 0f, 0f, 0.10f);
            GUI.DrawTexture(rect, EditorGUIUtility.whiteTexture);

            GUI.color = Color.white;

            // ── 3. Rounded green frame (12 px outer padding, 8 px corner radius).
            const float OuterPad = 12f;
            var inner = new Rect(rect.x + OuterPad, rect.y + OuterPad,
                                 Mathf.Max(0f, rect.width  - OuterPad * 2f),
                                 Mathf.Max(0f, rect.height - OuterPad * 2f));
            DrawNineSlice(inner, _overlayFrameStyle);

            // ── 4. Centered card with icon + title + body + chips.
            float cardW = Mathf.Min(380f, inner.width  - 32f);
            float cardH = Mathf.Min(220f, inner.height - 32f);
            if (cardW < 200f || cardH < 140f)
            {
                GUI.color = prevColor;
                return; // panel too small for the card — just leave the green frame.
            }
            var card = new Rect(inner.center.x - cardW * 0.5f, inner.center.y - cardH * 0.5f, cardW, cardH);
            DrawNineSlice(card, _overlayCardStyle);

            Color textDefault = _lastObservedIsProSkin
                ? new Color(0.94f, 0.94f, 0.96f) : new Color(0.10f, 0.10f, 0.12f);
            Color textSecondary = _lastObservedIsProSkin
                ? new Color(0.65f, 0.66f, 0.70f) : new Color(0.40f, 0.40f, 0.45f);

            // Icon — bilinear-filtered AA upload glyph (built once in EnsureOverlayResources).
            const float IconSize = 32f;
            var iconRect = new Rect(card.center.x - IconSize * 0.5f, card.y + 24f, IconSize, IconSize);
            if (_overlayUploadIcon != null)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(iconRect, _overlayUploadIcon, ScaleMode.ScaleToFit);
            }

            // Title — "Release to upload" (bold, slightly larger than the body).
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize  = 15,
                normal    = { textColor = textDefault },
                wordWrap  = false,
            };
            var titleRect = new Rect(card.x, iconRect.yMax + 12f, card.width, 22f);
            GUI.color = Color.white;
            GUI.Label(titleRect, "Release to upload", titleStyle);

            // Body — "Drop your scripts, logs, or assets to start a new session instantly."
            var descStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.UpperCenter,
                fontSize  = 11,
                wordWrap  = true,
                normal    = { textColor = textSecondary },
            };
            const float DescW = 240f;
            var descRect = new Rect(card.center.x - DescW * 0.5f, titleRect.yMax + 4f, DescW, 36f);
            GUI.Label(descRect, "Drop your scripts, logs, or assets to start a new session instantly.", descStyle);

            // Chips — .cs / .meta / .json (rounded pills via 9-slice texture).
            string[] chips = { ".cs", ".meta", ".json" };
            var chipStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize  = 10,
                normal    = { textColor = textSecondary },
            };
            const float ChipH = 18f;
            const float ChipHPad = 8f;
            const float ChipGap = 6f;

            var chipWidths = new float[chips.Length];
            float totalChipsW = 0f;
            for (int i = 0; i < chips.Length; i++)
            {
                float w = chipStyle.CalcSize(new GUIContent(chips[i])).x + ChipHPad * 2f;
                chipWidths[i] = w;
                totalChipsW  += w;
            }
            totalChipsW += ChipGap * (chips.Length - 1);

            float chipsY  = Mathf.Min(descRect.yMax + 14f, card.yMax - ChipH - 18f);
            float cursorX = card.center.x - totalChipsW * 0.5f;
            for (int i = 0; i < chips.Length; i++)
            {
                var cr = new Rect(cursorX, chipsY, chipWidths[i], ChipH);
                DrawNineSlice(cr, _overlayChipStyle);
                GUI.color = Color.white;
                GUI.Label(cr, chips[i], chipStyle);
                cursorX += chipWidths[i] + ChipGap;
            }

            GUI.color = prevColor;
        }

        // ── Rounded-corner overlay resources ────────────────────────────────────

        /// <summary>
        /// Build (or rebuild on skin change) the small textures + 9-slice GUIStyles
        /// used by DrawDropOverlay. Cheap: ~30 KB of textures generated once, reused
        /// across every drag.
        /// </summary>
        private void EnsureOverlayResources()
        {
            if (_overlayResourcesBuilt && _overlayResourcesIsProSkin == _lastObservedIsProSkin)
                return;

            DisposeOverlayResources();

            bool isPro = _lastObservedIsProSkin;

            Color green     = s_dragBrandGreen;
            Color greenFill = new Color(green.r, green.g, green.b, 0.10f);

            Color cardBg = isPro
                ? new Color(0.16f, 0.17f, 0.18f, 0.98f)   // bg-codely-color-surface-card (dark)
                : new Color(1.00f, 1.00f, 1.00f, 0.98f);  // surface-card (light)

            Color chipBg = isPro
                ? new Color(0.21f, 0.22f, 0.23f, 1.00f)
                : new Color(0.95f, 0.95f, 0.96f, 1.00f);
            Color chipBorder = isPro
                ? new Color(0.34f, 0.34f, 0.37f, 1.00f)
                : new Color(0.80f, 0.80f, 0.83f, 1.00f);

            Color iconColor = isPro
                ? new Color(0.62f, 0.63f, 0.68f, 1.00f)
                : new Color(0.48f, 0.50f, 0.55f, 1.00f);

            _overlayFrameTex     = BuildRoundedRectTex(32, 8,  2, greenFill, green);
            _overlayCardTex      = BuildRoundedRectTex(40, 12, 0, cardBg,    Color.clear);
            _overlayChipTex      = BuildRoundedRectTex(16, 4,  1, chipBg,    chipBorder);
            _overlayUploadIcon   = BuildUploadIconTex(iconColor, 64);

            _overlayFrameStyle = new GUIStyle { normal = { background = _overlayFrameTex }, border = new RectOffset(8,  8,  8,  8)  };
            _overlayCardStyle  = new GUIStyle { normal = { background = _overlayCardTex  }, border = new RectOffset(12, 12, 12, 12) };
            _overlayChipStyle  = new GUIStyle { normal = { background = _overlayChipTex  }, border = new RectOffset(4,  4,  4,  4)  };

            _overlayResourcesIsProSkin = isPro;
            _overlayResourcesBuilt     = true;
        }

        private void DisposeOverlayResources()
        {
            if (_overlayFrameTex   != null) { DestroyImmediate(_overlayFrameTex);   _overlayFrameTex   = null; }
            if (_overlayCardTex    != null) { DestroyImmediate(_overlayCardTex);    _overlayCardTex    = null; }
            if (_overlayChipTex    != null) { DestroyImmediate(_overlayChipTex);    _overlayChipTex    = null; }
            if (_overlayUploadIcon != null) { DestroyImmediate(_overlayUploadIcon); _overlayUploadIcon = null; }
            _overlayFrameStyle = null;
            _overlayCardStyle  = null;
            _overlayChipStyle  = null;
            _overlayResourcesBuilt = false;
        }

        /// <summary>
        /// 9-slice helper. GUIStyle.Draw is a Repaint-only operation, so the call is
        /// gated and silently no-ops in Layout/MouseMove/etc.
        /// </summary>
        private static void DrawNineSlice(Rect rect, GUIStyle style)
        {
            if (style == null || style.normal.background == null) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            style.Draw(rect, GUIContent.none, false, false, false, false);
        }

        /// <summary>
        /// Rasterizes an anti-aliased rounded rectangle into a Texture2D using a
        /// signed-distance field. Designed for GUIStyle 9-slice: pass <c>border</c>
        /// equal to <paramref name="radius"/> so the rounded corners stay 1:1 and the
        /// straight edges + interior stretch to fill any size.
        /// </summary>
        /// <param name="size">Texture side length in pixels. Must be at least 2 × (radius + max(borderPx, 1)).</param>
        /// <param name="radius">Corner radius in pixels.</param>
        /// <param name="borderPx">Border thickness in pixels (0 for solid fill).</param>
        /// <param name="fill">Interior colour (alpha respected).</param>
        /// <param name="border">Border colour (alpha respected); ignored when <paramref name="borderPx"/> &lt;= 0.</param>
        private static Texture2D BuildRoundedRectTex(int size, int radius, int borderPx, Color fill, Color border)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags  = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[size * size];
            var transparent = new Color(0f, 0f, 0f, 0f);
            float hs = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // SDF of a rounded rect inscribed in [0, size]² with corner radius `radius`.
                    float fx = x + 0.5f - hs;
                    float fy = y + 0.5f - hs;
                    float qx = Mathf.Abs(fx) - (hs - radius);
                    float qy = Mathf.Abs(fy) - (hs - radius);
                    float mxq = Mathf.Max(qx, 0f);
                    float myq = Mathf.Max(qy, 0f);
                    float outerSDF = Mathf.Sqrt(mxq * mxq + myq * myq) + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    float inside = -outerSDF; // > 0 inside the rounded rect

                    // Anti-alias the outer edge over a 1 px band centred on the boundary.
                    float outsideAlpha = Mathf.Clamp01(inside + 0.5f);
                    if (outsideAlpha <= 0f)
                    {
                        pixels[y * size + x] = transparent;
                        continue;
                    }

                    Color baseColor;
                    if (borderPx <= 0)
                    {
                        baseColor = fill;
                    }
                    else
                    {
                        // Soft transition between border and fill at depth == borderPx.
                        float fillT = Mathf.Clamp01(inside - borderPx + 0.5f);
                        baseColor = Color.Lerp(border, fill, fillT);
                    }

                    pixels[y * size + x] = new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * outsideAlpha);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>
        /// Rasterizes a small "document + up-arrow" upload glyph (mirroring the SVG
        /// from continue/gui's UploadIcon) into a Texture2D using 3×3 super-sampling
        /// for anti-aliasing. The result is drawn with bilinear filtering, so the
        /// 64×64 source looks clean at the 32×32 display size used by the card.
        /// </summary>
        private static Texture2D BuildUploadIconTex(Color color, int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags  = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[size * size];
            const int SS = 3; // 3×3 = 9 sub-samples per pixel.
            float invSS = 1f / SS;
            float invSize = 1f / size;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < SS; sy++)
                    {
                        for (int sx = 0; sx < SS; sx++)
                        {
                            float fx = (x + (sx + 0.5f) * invSS) * invSize;
                            float fy = (y + (sy + 0.5f) * invSS) * invSize;
                            if (IsInsideUploadGlyph(fx, fy)) hits++;
                        }
                    }
                    float alpha = hits / (float)(SS * SS);
                    int outY = size - 1 - y; // flip to bottom-up for Color32[] / SetPixels32
                    pixels[outY * size + x] = new Color(color.r, color.g, color.b, color.a * alpha);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        // Vector definition of the upload glyph in normalised top-down 0..1 coords.
        // Approximates continue/gui's UploadIcon.tsx: a rounded document with a 2 px
        // stroke and a solid up-arrow on top.
        private static bool IsInsideUploadGlyph(float fx, float fy)
        {
            // Document outline as a stroke (inside outer rect AND outside inner rect).
            bool inOuter = IsInsideRoundedRect(fx, fy, 0.20f, 0.08f, 0.80f, 0.92f, 0.06f);
            bool inInner = IsInsideRoundedRect(fx, fy, 0.25f, 0.13f, 0.75f, 0.87f, 0.04f);
            if (inOuter && !inInner) return true;

            // Up-arrow on top of the document.
            //   Shaft:  vertical bar [0.44, 0.56] × [0.42, 0.72]
            //   Head:   triangle peak (0.50, 0.25), base (0.33, 0.45) → (0.67, 0.45)
            if (fx >= 0.44f && fx <= 0.56f && fy >= 0.42f && fy <= 0.72f) return true;
            if (PointInTriangle(fx, fy, 0.50f, 0.25f, 0.33f, 0.45f, 0.67f, 0.45f)) return true;

            return false;
        }

        private static bool IsInsideRoundedRect(float px, float py, float left, float top, float right, float bottom, float r)
        {
            if (px < left || px > right || py < top || py > bottom) return false;
            // Determine which (if any) corner zone the point falls into.
            float cx, cy;
            if      (px < left  + r && py < top    + r) { cx = left  + r; cy = top    + r; }
            else if (px > right - r && py < top    + r) { cx = right - r; cy = top    + r; }
            else if (px < left  + r && py > bottom - r) { cx = left  + r; cy = bottom - r; }
            else if (px > right - r && py > bottom - r) { cx = right - r; cy = bottom - r; }
            else return true; // straight edge zone — inside.
            float dx = px - cx;
            float dy = py - cy;
            return dx * dx + dy * dy <= r * r;
        }

        private static bool PointInTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
            float d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
            float d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }

#if UNITY_EDITOR_WIN
        /// <summary>
        /// Captures the GUIView's pixels (which include the visible WebView2 child)
        /// into <see cref="_dragSnapshotTex"/> using PrintWindow + PW_RENDERFULLCONTENT.
        /// Must run BEFORE _webView2Host.SetVisible(false) — otherwise we'd capture
        /// the GUIView with the WebView2 already hidden.
        /// </summary>
        private void CaptureDragSnapshot()
        {
            DisposeDragSnapshot();

            IntPtr hwnd = _currentGUIViewHWND;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            if (!GetWindowRect(hwnd, out RECT wr)) return;

            int width  = wr.Right  - wr.Left;
            int height = wr.Bottom - wr.Top;
            if (width <= 0 || height <= 0) return;
            // Hard upper bound to avoid pathological allocations if some Win32 oddity
            // returns a huge rect.
            if (width > 8192 || height > 8192) return;

            IntPtr hdcScreen = IntPtr.Zero;
            IntPtr hdcMem    = IntPtr.Zero;
            IntPtr hBitmap   = IntPtr.Zero;
            IntPtr hOld      = IntPtr.Zero;

            try
            {
                hdcScreen = GetDC(IntPtr.Zero);
                if (hdcScreen == IntPtr.Zero) return;
                hdcMem = CreateCompatibleDC(hdcScreen);
                if (hdcMem == IntPtr.Zero) return;

                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize        = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                        biWidth       = width,
                        // Negative height → top-down DIB. Row 0 is the top of the image.
                        biHeight      = -height,
                        biPlanes      = 1,
                        biBitCount    = 32,
                        biCompression = BI_RGB,
                    },
                    bmiColors0 = 0,
                };

                IntPtr bits;
                hBitmap = CreateDIBSection(hdcMem, ref bmi, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero) return;

                hOld = SelectObject(hdcMem, hBitmap);

                if (!PrintWindow(hwnd, hdcMem, PW_RENDERFULLCONTENT)) return;

                // Copy the DIB bits into a managed array, swapping BGRA→RGBA, forcing
                // alpha=255 (PrintWindow leaves alpha undefined), and flipping row
                // order so Texture2D's bottom-up convention is satisfied.
                int stride = width * 4;
                int byteCount = stride * height;
                var raw = new byte[byteCount];
                Marshal.Copy(bits, raw, 0, byteCount);

                var pixels = new Color32[width * height];
                for (int ty = 0; ty < height; ty++)
                {
                    int srcRow = (height - 1 - ty) * stride;
                    int dstRow = ty * width;
                    for (int x = 0; x < width; x++)
                    {
                        int s = srcRow + x * 4;
                        pixels[dstRow + x] = new Color32(raw[s + 2], raw[s + 1], raw[s + 0], 255);
                    }
                }

                _dragSnapshotTex = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    hideFlags  = HideFlags.HideAndDontSave,
                    wrapMode   = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                _dragSnapshotTex.SetPixels32(pixels);
                _dragSnapshotTex.Apply(false, true);

                // Remember the inset so DrawDropOverlay can crop out the tab strip.
                _dragSnapshotTopInsetPx = Mathf.Max(0, _cachedTopInsetPx);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[Codely] CaptureDragSnapshot failed: {ex.Message}");
                DisposeDragSnapshot();
            }
            finally
            {
                if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero) SelectObject(hdcMem, hOld);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (hdcMem  != IntPtr.Zero) DeleteDC(hdcMem);
                if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        private void DisposeDragSnapshot()
        {
            if (_dragSnapshotTex != null)
            {
                DestroyImmediate(_dragSnapshotTex);
                _dragSnapshotTex = null;
            }
            _dragSnapshotTopInsetPx = 0;
        }
#endif

        /// <summary>
        /// Collect items from the current DragAndDrop operation and send them to Tauri via IPC.
        /// Handles both Unity asset/GameObject references and external file paths (e.g. from Windows Explorer).
        /// </summary>
        private void AddContexts()
        {
            try
            {
                var dropItems = new List<DropItemData>();

                // Unity object references (dragged from Project/Hierarchy windows)
                if (DragAndDrop.objectReferences != null)
                {
                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        if (obj == null) continue;
                        dropItems.Add(BuildDropItem(obj));
                    }
                }

                // External file paths (dragged from Windows Explorer or other apps)
                if (dropItems.Count == 0 && DragAndDrop.paths != null)
                {
                    CodelyLogger.LogWarning("Externel file path should be handled by Tauri");
                }

                if (dropItems.Count == 0)
                {
                    return;
                }
                var ipcPayload = new DropItemsPayload { items = dropItems.ToArray() };
                string ipcJson = JsonUtility.ToJson(ipcPayload);
                bool ipcSent = CodelyIpcManager.TrySend(IpcMessageType.AddContexts, ipcJson);
                CodelyLogger.Log(ipcSent
                    ? "Codely: Sent drop items via IPC"
                    : "Codely: Failed to send drop items via IPC (not connected)");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely: Failed to send dropped items: {ex.Message}");
            }
        }

        /// <summary>
        /// Get currently selected objects and send them to Tauri via IPC
        /// </summary>
        public void SendSelectedItemsToTauri()
        {
            try
            {
                var dropItems = BuildDropItemsFromSelection();
                if (dropItems == null)
                {
                    CodelyLogger.LogWarning("Codely: No objects selected");
                    return;
                }
                SendContextItemsViaIpc(dropItems);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely: Failed to send selected items: {ex.Message}");
            }
        }

        private static DropItemData BuildDropItem(UnityEngine.Object obj)
        {
            var itemData = new DropItemData { name = obj.name, type = obj.GetType().Name, kind = obj.GetKind() };
            string assetPath = AssetDatabase.GetAssetPath(obj);

            if (obj is UnityEngine.GameObject go
                && (string.IsNullOrEmpty(assetPath)
                    || PrefabStageUtility.GetPrefabStage(go) != null
                    || (go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.path))))
            {
                itemData.path = GetSceneOrPrefabPath(go);
                itemData.hierarchyPath = go.GetHierarchyPathWithIndex();
                itemData.instanceId = go.GetStableInstanceId();
                itemData.goFileId = go.FormatLocalFileId() ?? "";
                itemData.goGuid = go.FormatGlobalObjectGuid() ?? "";
                itemData.componentNames = go.GetComponentTypeNames();
                if (string.IsNullOrEmpty(itemData.path))
                    itemData.path = itemData.hierarchyPath;
                return itemData;
            }

            if (!string.IsNullOrEmpty(assetPath))
            {
                itemData.path = assetPath.Replace('\\', '/');
                itemData.guid = obj.GetAssetGuid();
                itemData.fileId = obj.FormatLocalFileId() ?? "";
                if (AssetDatabase.IsSubAsset(obj) && !string.IsNullOrEmpty(itemData.fileId))
                {
                    itemData.selected = new DropItemSelected
                    {
                        type = obj.GetType().Name,
                        name = obj.name,
                        fileId = itemData.fileId,
                    };
                }
                return itemData;
            }

            itemData.path = obj.name;
            return itemData;
        }

        private static string GetSceneOrPrefabPath(UnityEngine.GameObject go)
        {
            if (go == null) return "";
            if (go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.path))
                return go.scene.path.Replace('\\', '/');
            string stagePath = InstanceIdExtensions.GetPrefabStageAssetPath(
                PrefabStageUtility.GetPrefabStage(go));
            if (!string.IsNullOrEmpty(stagePath))
                return stagePath.Replace('\\', '/');
            string assetPath = AssetDatabase.GetAssetPath(go);
            return string.IsNullOrEmpty(assetPath) ? "" : assetPath.Replace('\\', '/');
        }


        /// <summary>
        /// Tauri server 就绪时回调（macOS 和 Windows 均适用）
        /// </summary>
        private void OnTauriServerReady(string url)
        {
            CodelyLogger.Log($"Codely: Tauri server ready at {url}");

            // 从 URL 中提取端口（Windows / macOS 逻辑一致）
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                int receivedPort = uri.Port;

                // 验证端口是否匹配预期端口
                if (_tauriServerPort > 0 && receivedPort != _tauriServerPort)
                {
                    CodelyLogger.LogWarning($"Codely: Received server_ready from unexpected port {receivedPort}, expected {_tauriServerPort}. Ignoring.");
                    return;
                }

                _tauriServerPort = receivedPort;
                SessionState.SetInt(SESSION_STATE_TAURI_PORT_KEY, _tauriServerPort);
            }

            TrackPanelOpenedIfNeeded();
            CodelyEventTracker.FlushPending(_tauriServerPort);
            if (_panelOpenedMetricQueued && !CodelyEventTracker.HasPending)
            {
                MarkPanelOpenedMetricDelivered();
            }

            // Probe the capability flag as soon as the server is up so that
            // later cleanup paths (OnEditorQuitting, CleanupResourcesAsync)
            // know whether they are allowed to leave the process running.
            // Legacy Tauri builds → singleInstance=false → legacy kill-on-close.
            RefreshTauriSingleInstanceCapability(_tauriServerPort);

            _serverReady = true;

#if UNITY_EDITOR_WIN
            // 直接调用——已在主线程，再 delayCall 一次只是在失焦场景下浪费一次 wake。
            if (!_webView2Initialized)
            {
                TryInitializeWebView2();
            }
#elif UNITY_EDITOR_OSX
            if (!_webViewInitialized)
            {
                InitializeWebView();
            }
            else if (_webViewBridge != null && _webViewBridge.IsInitialized)
            {
                // Check if WebView has valid content, reload if necessary
                if (!_webViewBridge.HasValidContent())
                {
                    CodelyLogger.LogWarning($"Codely: WebView has no valid content, reloading page...");
                    _webViewBridge.LoadURL(GetCurrentWebViewUrl());
                }
                else
                {
                    CodelyLogger.Log($"Codely: IPC reconnected, WebView content preserved (no reload)");
                }
            }
#endif
            TryRefreshTauriProcessFromProjectLock();
        }

        private void OnSetEmbedModeFromTauri(bool embedMode)
        {
            CodelyLogger.Log($"Codely: Received set_embed={embedMode} from Tauri");

            _isDetachMode = !embedMode;
            Repaint();

#if UNITY_EDITOR_OSX
            if (_webViewBridge != null && _webViewBridge.IsInitialized)
            {
                _webViewBridge.LoadURL(GetCurrentWebViewUrl());
                _webViewBridge.SetHidden(!_isWindowVisible);
                UpdateWebViewFrame();
            }
            else if (_serverReady || _tauriServerPort > 0)
            {
                InitializeWebView();
            }
#else
            if (_webView2Host != null && _webView2Host.IsReady)
            {
                _webView2Host.Navigate(GetCurrentWebViewUrl());
                _webView2Host.SetVisible(_isWindowVisible);
            }
            else if (
                !_webView2Initialized &&
                !HasAnotherLiveCodelyWindow() &&
                (_serverReady || _tauriServerPort > 0))
            {
                TryInitializeWebView2();
            }
#endif
        }

#if UNITY_EDITOR_WIN
        /// <summary>
        /// 销毁当前 WebView2Host 并重置所有相关状态。
        /// </summary>
        private void DisposeWebView2Host()
        {
            if (_webView2Host != null)
            {
                _webView2Host.Dispose();
                _webView2Host = null;
            }
            _webView2Initialized = false;
            _currentGUIViewHWND = IntPtr.Zero;
            _lastTopInsetPx = -1;
        }

        /// <summary>
        /// 异步初始化 WebView2：查找 GUIView HWND → 创建 WebView2Host → 导航到 Tauri 服务器。
        /// 最多等待 500ms（10 次 × 50ms）让 HWND 就绪。
        /// </summary>
        private void TryInitializeWebView2()
        {
            if (_webView2Initializing || _webView2Initialized) return;
            if (HasAnotherLiveCodelyWindow()) return;
            _webView2Initializing = true;

            CodelyLogger.Log("[Codely] TryInitializeWebView2: started");

            try
            {
                if (_tauriServerPort <= 0)
                {
                    CodelyLogger.LogWarning("[Codely] TryInitializeWebView2: skipped — Tauri server port not available");
                    return;
                }

                CodelyLogger.Log($"[Codely] TryInitializeWebView2: port={_tauriServerPort}, searching HWND...");

                // 单次尝试：阻塞 Sleep 在主线程上对失焦场景毫无帮助（既不会让 GUIView 提早出现，
                // 也阻碍其他消息泵处理）。HWND 暂不可用时直接退出，OnGUI 的 backstop 会在下一次
                // paint 自动重试，且那时 GUIView 必已就绪（用户能看到 "Loading ..." 占位证明）。
                IntPtr hwnd = NativeWindowHelper.GetHWND(this);

                if (hwnd == IntPtr.Zero)
                {
                    CodelyLogger.LogWarning("[Codely] TryInitializeWebView2: GUIView HWND not yet available, will retry from OnGUI/OnEditorUpdate");
                    return;
                }

                CodelyLogger.Log($"[Codely] TryInitializeWebView2: HWND=0x{hwnd:X}, creating WebView2Host...");

                _currentGUIViewHWND = hwnd;

                var host = new WebView2Host();
                _webView2DidInitialFocus = false;
                host.OnLoadCompleted += (success) =>
                {
                    // 首次导航完成后把焦点送入内容一次，让页面的 autofocus 输入框拿到光标
                    // （对齐浏览器中打开 URL 的体验）。仅当本窗口可见时执行，且只做一次，
                    // 避免 SPA 后续刷新/路由切换时从用户当前窗口抢走焦点。
                    if (!success || _webView2DidInitialFocus || !_isWindowVisible) return;
                    _webView2DidInitialFocus = true;
                    // OnLoadCompleted 由 native STA 线程回调投递；MoveFocus 内部会再转发回
                    // STA 线程，这里无需切线程，但用 delayCall 确保在 Unity 主线程节奏上执行。
                    EditorApplication.delayCall += () => _webView2Host?.MoveFocus();
                };
                host.OnParentDestroyed += () =>
                {
                    CodelyLogger.Log("[Codely] WebView2Host: parent HWND destroyed, will recreate");
                    var destroyedHost = _webView2Host;
                    _webView2Host = null;
                    _webView2Initialized = false;
                    _currentGUIViewHWND = IntPtr.Zero;
                    _lastTopInsetPx = -1;
                    EditorApplication.delayCall += () =>
                    {
                        destroyedHost?.Dispose();
                        TryInitializeWebView2();
                    };
                };

                string url = GetCurrentWebViewUrl();
                CodelyLogger.Log($"[Codely] TryInitializeWebView2: calling Init url={url}");

                // Init 同步返回，WebView2 在 native STA 线程异步初始化
                host.Init(hwnd, url);

                _webView2Host = host;
                _webView2Initialized = true;

                // Seed native m_topInset BEFORE SetVisible / first SyncBounds.
                // Otherwise the controller's first SyncBounds (called on the STA
                // thread immediately after CreateCoreWebView2Controller completes)
                // uses m_topInset=0 and the WebView2 paints over the tab bar
                // until OnGUI later pushes the real inset.
                if (_cachedTopInsetPx >= 0)
                {
                    _webView2Host.SetTopInset(_cachedTopInsetPx);
                    _lastTopInsetPx = _cachedTopInsetPx;
                }

                _webView2Host.SetVisible(_isWindowVisible);
                CodelyLogger.Log($"[Codely] WebView2 host created at {url} (HWND: 0x{hwnd:X})");
                if (_showUpdateLoadingUI)
                {
                    ApplyWebViewHiddenForUpdateLoading(true);
                }
                Repaint();
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"[Codely] TryInitializeWebView2: exception — {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                _webView2Initializing = false;
                CodelyLogger.Log($"[Codely] TryInitializeWebView2: finished (initialized={_webView2Initialized})");
            }
        }
#endif

#if UNITY_EDITOR_OSX
        /// <summary>
        /// Clears the HTTP-readiness gate so the next InitializeWebView / reload
        /// waits for a fresh /api/tauri/status probe. Called whenever the backend is
        /// (re)launched or re-attached — the new server may reuse the old port, so a
        /// stale "ready" must be invalidated explicitly.
        /// </summary>
        private void ResetServerReadyGate()
        {
            _serverHttpReady = false;
            _serverReadyProbePort = -1;
        }

        /// <summary>
        /// Starts (at most one) background poll of /api/tauri/status on the given
        /// port and flips _serverHttpReady once the server answers. Cheap to call
        /// every tick: it no-ops while a probe is already in flight or already
        /// satisfied for this port. Main-thread only.
        /// </summary>
        private void EnsureServerReadyProbe(int port)
        {
            if (port <= 0) return;
            if (_serverHttpReady && _serverReadyProbePort == port) return;
            if (_serverReadyProbeRunning) return;

            _serverReadyProbePort = port;
            _serverHttpReady = false;
            _serverReadyProbeRunning = true;

            int probePort = port;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (await ProbeServerReadyAsync(probePort))
                        {
                            // Guard against a port change (another relaunch) mid-poll.
                            if (_serverReadyProbePort == probePort)
                            {
                                _serverHttpReady = true;
                            }
                            return;
                        }
                        await System.Threading.Tasks.Task.Delay(200);
                    }
                    CodelyLogger.LogWarning($"Codely: Tauri HTTP server on port {probePort} did not become ready within timeout");
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"Codely: server readiness probe failed: {ex.Message}");
                }
                finally
                {
                    _serverReadyProbeRunning = false;
                }
            });
        }

        /// <summary>
        /// Single non-throwing GET to /api/tauri/status; true on a 2xx response.
        /// A refused connection (server not bound yet) fails fast and returns false.
        /// </summary>
        private static async System.Threading.Tasks.Task<bool> ProbeServerReadyAsync(int port)
        {
            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(1)))
                {
                    var response = await _httpClient.GetAsync($"http://127.0.0.1:{port}/api/tauri/status", cts.Token);
                    return response.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Initialize WKWebView for macOS
        /// </summary>
        private void InitializeWebView()
        {
            if (_webViewBridge == null) return;
            if (_webViewInitialized) return;

            IntPtr guiViewHandle = GetEditorWindowHandle();
            if (guiViewHandle == IntPtr.Zero)
            {
                CodelyLogger.LogError("Codely: Failed to get GUIView handle for WebView");
                return;
            }

            if (_tauriServerPort <= 0)
            {
                CodelyLogger.LogWarning("Codely: Tauri server port not available yet, waiting...");
                return;
            }

            // Do not navigate until the Tauri HTTP server actually answers on the
            // port. Creating the WKWebView against a not-yet-bound port produces a
            // permanent connection-refused white screen (no auto-retry). The probe
            // runs off-thread; OnEditorUpdate calls back here every tick until ready.
            if (!_serverHttpReady)
            {
                EnsureServerReadyProbe(_tauriServerPort);
                return;
            }

            float x = 0;
            float y = 0;
            float w = position.width;
            float h = position.height;

            string url = GetCurrentWebViewUrl();

            if (_webViewBridge.Create(guiViewHandle, url, x, y, w, h))
            {
                _lastWindowSize = new Vector2(position.width, position.height);
                _webViewInitialized = true;
                // Created fresh against the current (ready) URL — no stale page left
                // to reload, so drop any relaunch-reload request to avoid a redundant
                // second navigation.
                _pendingWebViewReloadAfterRelaunch = false;

                _serializedWebViewHandle = _webViewBridge.Handle.ToInt64();
                _serializedWebViewInitialized = true;

                CodelyLogger.Log($"Codely: WKWebView initialized at {url}");

                // Install drag gateway to enable Unity GameObject drag and drop
                if (_webViewBridge.InstallDragGateway(guiViewHandle))
                {
                    CodelyLogger.Log("Codely: Drag gateway installed, Unity drag and drop enabled");
                }
                else
                {
                    CodelyLogger.LogWarning("Codely: Failed to install drag gateway");
                }

                _webViewBridge.SetHidden(!_isWindowVisible);
                if (_showUpdateLoadingUI)
                {
                    ApplyWebViewHiddenForUpdateLoading(true);
                }
                Repaint();
            }
            else
            {
                CodelyLogger.LogError("Codely: Failed to initialize WKWebView");
            }
        }
#endif

        private string GetCurrentWebViewUrl()
        {
            string route = _isDetachMode ? "/detach" : "/";
            // Include `workspaceDir` so the Tauri backend can route this
            // Editor-embedded WebView's requests to the correct workspace
            // without relying on a Tauri window label (this WebView has none).
            // `mode=embedded` declares the initial shell layout. Detach route
            // intentionally omits mode so the shell renders standalone UI.
            string workspaceDir = GetWorkspaceDirectory();
            string encodedDir = Uri.EscapeDataString(workspaceDir);
            string editorType = Uri.EscapeDataString(GetEditorType());
            string themeMode = EditorGUIUtility.isProSkin ? "dark" : "light";
            string query = $"workspaceDir={encodedDir}&editorType={editorType}&theme={themeMode}";
            if (!_isDetachMode)
            {
                query += "&mode=embedded";
            }
            return $"http://127.0.0.1:{_tauriServerPort}{route}?{query}";
        }

        private static bool IsPortAvailable(int port) => TauriUtils.IsPortAvailable(port);

        private static int FindAvailablePort() => TauriUtils.FindAvailablePort();

#if UNITY_EDITOR_OSX
        /// <summary>
        /// Validate that the restored WebView has valid content, recreate if not
        /// </summary>
        private void ValidateRestoredWebView()
        {
            if (_webViewBridge == null || !_webViewBridge.IsInitialized)
            {
                return;
            }

            // Check if WebView has valid content
            if (!_webViewBridge.HasValidContent())
            {
                CodelyLogger.LogWarning("Codely: Restored WebView has no valid content (white screen detected), recreating...");

                // Destroy the invalid WebView
                try
                {
                    _webViewBridge.Destroy();
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"Codely: Error destroying invalid WebView: {ex.Message}");
                }

                // Reset state to trigger recreation
                _webViewBridge = new MacOSWebViewBridge();
                _webViewInitialized = false;
                _serializedWebViewHandle = 0;
                _serializedWebViewInitialized = false;

                // Force repaint to trigger WebView recreation in OnEditorUpdate
                Repaint();
            }
            else
            {
                CodelyLogger.Log("Codely: Restored WebView content validation passed");
            }
        }

        /// <summary>
        /// Update WebView frame when window is resized
        /// </summary>
        private void UpdateWebViewFrame()
        {
            if (_webViewBridge == null || !_webViewBridge.IsInitialized) return;

            // Get GUIView handle (may have changed if tab was moved)
            IntPtr guiViewHandle = GetEditorWindowHandle();
            if (guiViewHandle == IntPtr.Zero)
            {
                CodelyLogger.LogWarning("Codely: Failed to get GUIView handle in UpdateWebViewFrame");
                return;
            }

            UpdateWebViewFrameWithHandle(guiViewHandle);
        }

        private void UpdateWebViewFrameWithHandle(IntPtr guiViewHandle)
        {
            if (_webViewBridge == null || !_webViewBridge.IsInitialized || guiViewHandle == IntPtr.Zero) return;

            // Update to full window size (in points, not pixels)
            float x = 0;
            float y = 0;
            float w = position.width;   // Use points directly
            float h = position.height;  // Use points directly

            // Use UpdateParentAndFrame to re-attach to correct parent if needed
            _webViewBridge.UpdateParentAndFrame(guiViewHandle, x, y, w, h);

            // Also update drag gateway frame
            _webViewBridge.UpdateDragGatewayFrame(x, y, w, h);
        }
#endif
    }
}
#endif
