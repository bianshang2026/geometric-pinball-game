#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.CodeEditor;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

using UnityTcp.Editor.Helpers;
namespace Cn.Tuanjie.Codely.Editor
{
    /// <summary>
    /// IPC 消息类型
    /// </summary>
    public static class IpcMessageType
    {
        public const string ShowLines = "showLines";
        public const string OpenFile = "openFile";
        public const string SelectAsset = "selectAsset";
        public const string SetColors = "setColors";
        public const string GetColors = "getColors";
        public const string Recompile = "recompile";
        public const string AddContexts = "addContexts";
        public const string DebugUnityConsole = "debugUnityConsole";
        public const string FocusInputWithNewSession = "focusInputWithNewSession";
        public const string SetEmbedMode = "setEmbedMode";
        /// <summary>Tauri switched workspace; close Codely EditorWindow if in detach mode (handled in CodelyWindow).</summary>
        public const string CloseCodelyEditorOnWorkspaceSwitch = "closeCodelyEditorOnWorkspaceSwitch";
        public const string DrillCompleted = "drillCompleted";
        public const string FocusWindow = "focusWindow";
        public const string UpdateRestart = "updateRestart";
        public const string TrackEvent = "trackEvent";
    }

    /// <summary>
    /// IPC 消息结构
    /// </summary>
    [Serializable]
    public class IpcMessage
    {
        public string messageType;
        public string filepath;
        public int startLine;
        public int endLine;
        // For setColors message
        public string data; // JSON string containing theme colors
    }

    /// <summary>
    /// Codely IPC 客户端 - Unity 作为客户端连接到 Tauri 服务器
    /// </summary>
    public class CodelyIpcClient : IDisposable
    {
        public const string PIPE_NAME_PREFIX = "CodelyUnityIpc";
        private static int s_drillCompletedPending;

        internal static bool ConsumePendingDrillCompleted()
        {
            return Interlocked.Exchange(ref s_drillCompletedPending, 0) != 0;
        }

#if UNITY_EDITOR_WIN
        // Native named-pipe client (NIC_*) replaces System.IO.Pipes.NamedPipeClientStream.
        // Unity 2019's Mono 5.x has known bugs around pipe I/O cancellation that surface as
        // hangs and leaked threads on assembly reload; the native singleton owns the worker
        // thread and exposes a non-blocking dequeue API the managed side polls.
        private const string NIC_DLL = "NativeIpcClient";

        [DllImport(NIC_DLL, EntryPoint = "NIC_StartClient", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int NIC_StartClient([MarshalAs(UnmanagedType.LPStr)] string pipeName);

        [DllImport(NIC_DLL, EntryPoint = "NIC_StopClient", CallingConvention = CallingConvention.Cdecl)]
        private static extern void NIC_StopClient();

        [DllImport(NIC_DLL, EntryPoint = "NIC_IsConnected", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NIC_IsConnected();

        [DllImport(NIC_DLL, EntryPoint = "NIC_IsRunning", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NIC_IsRunning();

        [DllImport(NIC_DLL, EntryPoint = "NIC_TryDequeueMessage", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NIC_TryDequeueMessage(byte[] buffer, int bufferCapacity, out int outBytes);

        [DllImport(NIC_DLL, EntryPoint = "NIC_SendMessage", CallingConvention = CallingConvention.Cdecl)]
        private static extern int NIC_SendMessage(byte[] payload, int payloadBytes);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildWindowsProc cb, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern int GetClassNameW(IntPtr hwnd, [Out] char[] buf, int max);

        private delegate bool EnumChildWindowsProc(IntPtr hwnd, IntPtr param);

        private const int SW_RESTORE = 9;
        private const string GUI_VIEW_CLASS = "UnityGUIViewWndClass";

        // Static delegate to keep the EnumChildWindows callback alive across calls.
        private static readonly EnumChildWindowsProc s_invalidateGUIViewsProc = InvalidateIfGUIView;
        private static readonly char[] s_classBuf = new char[256];

        [AOT.MonoPInvokeCallback(typeof(EnumChildWindowsProc))]
        private static bool InvalidateIfGUIView(IntPtr hwnd, IntPtr param)
        {
            try
            {
                int n = GetClassNameW(hwnd, s_classBuf, s_classBuf.Length);
                if (n > 0)
                {
                    // 注：s_classBuf 在 IPC 后台线程间复用，但 ProcessMessage / NotifyConnectionChanged
                    // 在同一 IPC client task 上顺序执行，不会并发——足够安全。
                    string cls = new string(s_classBuf, 0, n);
                    if (cls == GUI_VIEW_CLASS)
                    {
                        InvalidateRect(hwnd, IntPtr.Zero, false);
                    }
                }
            }
            catch { /* enumeration must continue even if one window fails */ }
            return true;
        }
#endif

        /// <summary>
        /// 从后台线程将 Unity Editor 进程窗口提到前台（不依赖主线程 update 循环）。
        /// Windows: AttachThreadInput 绕过前台切换限制，再调用 SetForegroundWindow。
        /// macOS: osascript 通过 PID 激活进程。
        /// </summary>
        private static void BringUnityToFront()
        {
            try
            {
#if UNITY_EDITOR_WIN
                var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SW_RESTORE);

                    // Windows 限制后台进程直接调用 SetForegroundWindow（会降级为任务栏闪烁）。
                    // AttachThreadInput 将当前线程的输入队列附加到前台窗口线程，
                    // 使 OS 认为本次调用来自"有资格"的线程，从而绕过该限制。
                    var foregroundHwnd = GetForegroundWindow();
                    uint foregroundThreadId = GetWindowThreadProcessId(foregroundHwnd, out _);
                    uint currentThreadId = GetCurrentThreadId();

                    bool attached = false;
                    if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                        attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                    try
                    {
                        BringWindowToTop(hwnd);
                        SetForegroundWindow(hwnd);
                    }
                    finally
                    {
                        if (attached)
                            AttachThreadInput(currentThreadId, foregroundThreadId, false);
                    }
                    CodelyLogger.Log("Codely IPC: BringUnityToFront - SetForegroundWindow called");
                }
                else
                {
                    CodelyLogger.LogWarning("Codely IPC: BringUnityToFront - MainWindowHandle is zero");
                }
#elif UNITY_EDITOR_OSX
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                System.Diagnostics.Process.Start("osascript",
                    $"-e 'tell application \"System Events\" to set frontmost of first process whose unix id is {pid} to true'");
                CodelyLogger.Log($"Codely IPC: BringUnityToFront - osascript activated pid {pid}");
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely IPC: BringUnityToFront failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 从后台线程"唤醒"Unity 主线程消息循环，使其在失焦下也跑一次 EditorWindow.OnGUI。
        ///
        /// 背景：Unity Editor 失焦时 EditorApplication.update / delayCall 都被节流到几乎为零，
        /// server_ready → OnTauriServerReady → TryInitializeWebView2 整条事件链会被卡住，导致
        /// CodelyWindow / DrillWindow 的 WebView2 一直停留在 "Loading ..." 占位，直到用户点回 Unity。
        ///
        /// 修复思路：让所有 <c>UnityGUIViewWndClass</c> 子窗口 InvalidateRect。InvalidateRect 跨线程
        /// 安全，会标记区域无效并通知目标线程；Win32 产生的 WM_PAINT 唤醒目标线程的 GetMessage，
        /// Unity 必须处理 WM_PAINT 且会跑一次 EditorWindow.OnGUI——这条路径在失焦下仍然可用
        /// （用户能看到 "Loading ..." 占位证明 OnGUI 能跑）。配合 OnGUI 内部的初始化 backstop，
        /// server_ready 一到 CodelyWindow / DrillWindow 就能在下一次 paint 里直接推进
        /// TryInitializeWebView2，而不必依赖被节流的 editor update loop。
        ///
        /// macOS / Linux: 无对应低开销机制，留作 no-op；当前已知问题只在 Windows 复现。
        /// </summary>
        private static void WakeUnityMainThread()
        {
            try
            {
#if UNITY_EDITOR_WIN
                var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    EnumChildWindows(hwnd, s_invalidateGUIViewsProc, IntPtr.Zero);
                }
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely IPC: WakeUnityMainThread failed: {ex.Message}");
            }
        }

        private CancellationTokenSource _cancellationTokenSource;
        private Task _clientTask;
        private bool _isDisposed = false;
        private bool _isConnected = false;
        private readonly object _lockObj = new object();

#if UNITY_EDITOR_WIN
        // Native NIC_* singleton owns the actual pipe handle; we just hold the name + state.
        private readonly string _pipeName; // Windows: Named pipe name
#else
        private Socket _socket; // macOS/Linux: Unix Domain Socket
        private readonly string _socketPath; // macOS/Linux: Socket file path

        private sealed class Unity2019UnixSocketEndPoint : EndPoint
        {
            private const int NativePathOffset = 2;
            private readonly string _path;

            public Unity2019UnixSocketEndPoint(string path)
            {
                if (string.IsNullOrEmpty(path))
                    throw new ArgumentException("Unix socket path must not be empty", nameof(path));

                _path = path;
            }

            public override AddressFamily AddressFamily => AddressFamily.Unix;

            public override SocketAddress Serialize()
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(_path);
                var socketAddress = new SocketAddress(AddressFamily.Unix, NativePathOffset + pathBytes.Length + 1);

                for (int i = 0; i < pathBytes.Length; i++)
                {
                    socketAddress[NativePathOffset + i] = pathBytes[i];
                }

                socketAddress[NativePathOffset + pathBytes.Length] = 0;
                return socketAddress;
            }

            public override EndPoint Create(SocketAddress socketAddress)
            {
                return this;
            }
        }
#endif

        /// <summary>
        /// 当 Tauri 服务器就绪时触发
        /// </summary>
        public event Action<string> OnServerReady;

        /// <summary>
        /// 当收到 ShowLines 消息时触发
        /// </summary>
        public event Action<string, int, int> OnShowLines;

        /// <summary>
        /// 当收到 OpenFile 消息时触发
        /// </summary>
        public event Action<string> OnOpenFile;

        /// <summary>
        /// 当收到 SelectAsset 消息时触发
        /// </summary>
        public event Action<string> OnSelectAsset;

        /// <summary>
        /// 当收到 SetEmbedMode 消息时触发，参数为 embedMode 布尔值
        /// </summary>
        public event Action<bool> OnSetEmbedMode;

        /// <summary>
        /// Tauri 切换工作区前在旧 IPC 上发送；CodelyWindow 仅在 detach 模式下关闭自身。
        /// </summary>
        public event Action OnCloseCodelyEditorOnWorkspaceSwitch;

        /// <summary>
        /// 当收到 DrillCompleted 消息时触发（演练完成，应关闭 DrillWindow 并打开 CodelyWindow）
        /// </summary>
        public event Action OnDrillCompleted;

        /// <summary>
        /// 连接状态改变时触发
        /// </summary>
        public event Action<bool> OnConnectionChanged;

        /// <summary>
        /// 当收到 UpdateRestart 消息时触发（Tauri 即将更新重启）
        /// </summary>
        public event Action<string> OnUpdateRestart;

        /// <summary>
        /// 是否已连接到 Tauri 服务器
        /// </summary>
        public bool IsConnected
        {
            get
            {
                lock (_lockObj)
                {
                    return _isConnected;
                }
            }
        }

        /// <summary>
        /// 构造函数：根据项目路径生成 pipe/socket 名称
        /// </summary>
        public CodelyIpcClient()
        {
#if UNITY_EDITOR_WIN
            _pipeName = GeneratePipeName();
#else
            _socketPath = GenerateSocketPath();
#endif
        }

#if UNITY_EDITOR_WIN
        /// <summary>
        /// 根据项目路径生成唯一的 pipe 名称 (Windows)
        /// </summary>
        private static string GeneratePipeName()
        {
            try
            {
                string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(projectPath));
                    string hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8).ToLower();

                    return $"{PIPE_NAME_PREFIX}-{hash}";
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely IPC: Failed to generate pipe name hash, using default: {ex.Message}");
                return PIPE_NAME_PREFIX;
            }
        }
#else
        /// <summary>
        /// 根据项目路径生成唯一的 socket 路径 (macOS/Linux)
        /// </summary>
        private static string GenerateSocketPath()
        {
            try
            {
                string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(projectPath));
                    string hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8).ToLower();

                    // Use TMPDIR environment variable or /tmp as fallback
                    string tmpDir = Environment.GetEnvironmentVariable("TMPDIR");
                    if (string.IsNullOrEmpty(tmpDir))
                    {
                        tmpDir = "/tmp";
                    }
                    tmpDir = tmpDir.TrimEnd('/');

                    return $"{tmpDir}/{PIPE_NAME_PREFIX}-{hash}.sock";
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely IPC: Failed to generate socket path hash, using default: {ex.Message}");
                string tmpDir = Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp";
                return $"{tmpDir.TrimEnd('/')}/{PIPE_NAME_PREFIX}.sock";
            }
        }
#endif

        /// <summary>
        /// 启动 IPC 客户端并连接到 Tauri 服务器
        /// </summary>
        public void Start()
        {
            lock (_lockObj)
            {
                if (_clientTask != null && !_clientTask.IsCompleted)
                {
                    CodelyLogger.Log("Codely IPC: Client already running");
                    return;
                }

                _cancellationTokenSource = new CancellationTokenSource();
                _clientTask = Task.Run(() => RunClientLoop(_cancellationTokenSource.Token));
#if UNITY_EDITOR_WIN
                CodelyLogger.Log($"Codely IPC: Client started, attempting to connect to Tauri server on named pipe '{_pipeName}'");
#else
                CodelyLogger.Log($"Codely IPC: Client started, attempting to connect to Tauri server on Unix socket '{_socketPath}'");
#endif
            }
        }

        /// <summary>
        /// 停止 IPC 客户端
        /// </summary>
        public void Stop()
        {
            CancellationTokenSource cts = null;
            Task clientTask = null;
#if !UNITY_EDITOR_WIN
            Socket socketToDispose = null;
#endif

            lock (_lockObj)
            {
                // Allow Stop to be called multiple times and also from Dispose
#if UNITY_EDITOR_WIN
                if (_cancellationTokenSource == null && _clientTask == null)
#else
                if (_cancellationTokenSource == null && _clientTask == null && _socket == null)
#endif
                {
                    return;
                }

                cts = _cancellationTokenSource;
                _cancellationTokenSource = null;

                clientTask = _clientTask;
                _clientTask = null;

                _isConnected = false;

#if !UNITY_EDITOR_WIN
                // Detach current connection reference so SendMessage won't use a disposed instance
                socketToDispose = _socket;
                _socket = null;
#endif
            }

            // Cancel outside lock to avoid deadlocks
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    cts.Dispose();
                }
            }

            // Wait for client loop to exit (best effort)
            if (clientTask != null)
            {
                try
                {
                    clientTask.Wait(2000);
                }
                catch
                {
                    // ignore
                }
            }

#if UNITY_EDITOR_WIN
            // Tear down the native worker thread and pipe handle. Idempotent on the native side.
            try { NIC_StopClient(); }
            catch (Exception ex) { CodelyLogger.LogWarning($"Codely IPC: NIC_StopClient failed: {ex.Message}"); }
#else
            if (socketToDispose != null)
            {
                try
                {
                    socketToDispose.Close();
                    socketToDispose.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
#endif

            CodelyLogger.Log("Codely IPC: Client stopped");
        }

        private async Task RunClientLoop(CancellationToken cancellationToken)
        {
#if UNITY_EDITOR_WIN
            // Hand off connect/reconnect/read to the native singleton, then poll its inbound
            // queue from this managed task. ProcessMessage runs on this thread (matching the
            // legacy behavior) so focusWindow can call BringUnityToFront() without waiting on
            // the editor update loop, which is exactly the case where focus is missing.
            await RunNativeClientLoopWin(cancellationToken);
            return;
#else
            int retryDelayMs = 100; // Start with 100ms
            int maxRetryDelayMs = 2000; // Max 2 seconds

            while (!cancellationToken.IsCancellationRequested)
            {
                Socket socket = null;
                try
                {
                    // 创建 Unix Domain Socket
                    socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    var endpoint = new Unity2019UnixSocketEndPoint(_socketPath);

                    CodelyLogger.Log("Codely IPC: Attempting to connect to Tauri server via Unix socket...");

                    // 连接到服务器（使用异步连接）
                    await Task.Run(() => socket.Connect(endpoint), cancellationToken);

                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // 连接成功，重置退化延迟
                    retryDelayMs = 100;

                    // 连接成功
                    lock (_lockObj)
                    {
                        _isConnected = true;
                        _socket = socket;
                    }

                    NotifyConnectionChanged(true);
                    CodelyLogger.Log("Codely IPC: Successfully connected to Tauri server via Unix socket!");

                    // 读取消息循环
                    byte[] buffer = new byte[4096];
                    StringBuilder messageBuffer = new StringBuilder();

                    while (!cancellationToken.IsCancellationRequested && socket.Connected)
                    {
                        try
                        {
                            int bytesRead = await Task.Run(() => socket.Receive(buffer), cancellationToken);

                            if (bytesRead == 0)
                            {
                                // 连接断开
                                break;
                            }

                            string data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                            messageBuffer.Append(data);

                            // 处理完整的消息（以换行符分隔）
                            string messages = messageBuffer.ToString();
                            int newlineIndex;
                            while ((newlineIndex = messages.IndexOf('\n')) >= 0)
                            {
                                string message = messages.Substring(0, newlineIndex).Trim();
                                messages = messages.Substring(newlineIndex + 1);

                                if (!string.IsNullOrWhiteSpace(message))
                                {
                                    ProcessMessage(message);
                                }
                            }
                            messageBuffer.Clear();
                            messageBuffer.Append(messages);
                        }
                        catch (Exception ex)
                        {
                            CodelyLogger.LogWarning($"Codely IPC: Error reading message: {ex.Message}");
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消，退出循环
                    break;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        CodelyLogger.Log($"Codely IPC: Connection error: {ex.Message}. Retrying in {retryDelayMs}ms...");
                    }
                }
                finally
                {
                    if (socket != null)
                    {
                        try
                        {
                            socket.Close();
                            socket.Dispose();
                        }
                        catch
                        {
                            // ignore
                        }
                    }

                    bool shouldNotify = false;
                    lock (_lockObj)
                    {
                        _isConnected = false;
                        if (_socket == socket)
                        {
                            _socket = null;
                        }
                        shouldNotify = !_isDisposed;
                    }

                    if (shouldNotify)
                    {
                        NotifyConnectionChanged(false);
                    }
                }

                // 如果连接断开，使用退化延迟后重试
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(retryDelayMs, cancellationToken);
                    // 指数退避，每次重试延迟翻倍，直到达到最大值
                    retryDelayMs = Math.Min(retryDelayMs * 2, maxRetryDelayMs);
                }
            }
#endif
        }

#if UNITY_EDITOR_WIN
        // Polls the native NIC_* singleton's inbound queue and forwards lines to ProcessMessage.
        // The native worker handles connect/reconnect; we only own state-transition notifications
        // and message dispatch. Buffer grows on demand if the native side reports a message
        // larger than capacity (this is the documented contract — outBytes carries required size).
        private async Task RunNativeClientLoopWin(CancellationToken cancellationToken)
        {
            try
            {
                if (NIC_StartClient(_pipeName) == 0)
                {
                    CodelyLogger.LogError($"Codely IPC: NIC_StartClient failed for pipe '{_pipeName}'");
                    return;
                }
            }
            catch (DllNotFoundException ex)
            {
                CodelyLogger.LogError(
                    $"Codely IPC: native plugin '{NIC_DLL}' not found ({ex.Message}). " +
                    "Build codely-bridge-native/tcp-bridge-native and copy NativeIpcClient.dll to Plugins/Win/.");
                return;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely IPC: NIC_StartClient threw: {ex.Message}");
                return;
            }

            CodelyLogger.Log($"Codely IPC: native client started for pipe '{_pipeName}', polling inbound queue");

            byte[] buffer = new byte[8192];
            bool prevConnected = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    bool nowConnected;
                    try { nowConnected = NIC_IsConnected() != 0; }
                    catch { nowConnected = false; }

                    if (nowConnected != prevConnected)
                    {
                        lock (_lockObj) { _isConnected = nowConnected; }
                        NotifyConnectionChanged(nowConnected);
                        if (nowConnected)
                            CodelyLogger.Log("Codely IPC: Successfully connected to Tauri server!");
                        else if (prevConnected)
                            CodelyLogger.Log("Codely IPC: Disconnected, native worker will retry");
                        prevConnected = nowConnected;
                    }

                    // Drain everything currently queued before sleeping.
                    while (true)
                    {
                        int outBytes = 0;
                        int rc;
                        try { rc = NIC_TryDequeueMessage(buffer, buffer.Length, out outBytes); }
                        catch (Exception ex)
                        {
                            CodelyLogger.LogWarning($"Codely IPC: NIC_TryDequeueMessage threw: {ex.Message}");
                            rc = 0;
                            outBytes = 0;
                        }

                        if (rc == 1)
                        {
                            if (outBytes > 0)
                            {
                                string line = Encoding.UTF8.GetString(buffer, 0, outBytes);
                                if (!string.IsNullOrWhiteSpace(line))
                                {
                                    ProcessMessage(line);
                                }
                            }
                            continue;
                        }

                        // rc == 0: either empty queue (outBytes == 0) or buffer too small
                        // (outBytes carries required size and the message is NOT popped).
                        if (outBytes > buffer.Length)
                        {
                            int needed = outBytes;
                            int newCap = buffer.Length;
                            while (newCap < needed) newCap *= 2;
                            buffer = new byte[newCap];
                            continue; // retry dequeue with the larger buffer
                        }

                        break; // queue is empty
                    }

                    try { await Task.Delay(50, cancellationToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                bool wasConnected;
                lock (_lockObj)
                {
                    wasConnected = _isConnected;
                    _isConnected = false;
                }
                if (wasConnected) NotifyConnectionChanged(false);
            }
        }
#endif

        private void NotifyConnectionChanged(bool connected)
        {
            EditorApplication.delayCall += () =>
            {
                lock (_lockObj)
                {
                    if (_isDisposed)
                    {
                        return;
                    }
                }

                OnConnectionChanged?.Invoke(connected);
            };
        }

        private void ProcessMessage(string messageJson)
        {
            try
            {
                lock (_lockObj)
                {
                    if (_isDisposed || _cancellationTokenSource == null)
                    {
                        return;
                    }
                }

                CodelyLogger.Log($"Codely IPC: ProcessMessage raw: {messageJson}");

                // Check if this is a server_ready message (simple JSON check)
                if (messageJson.Contains("\"type\":\"server_ready\"") || messageJson.Contains("\"type\": \"server_ready\""))
                {
                    // Extract URL from JSON manually
                    int urlStart = messageJson.IndexOf("\"url\"");
                    if (urlStart >= 0)
                    {
                        int colonPos = messageJson.IndexOf(":", urlStart);
                        int quoteStart = messageJson.IndexOf("\"", colonPos + 1);
                        int quoteEnd = messageJson.IndexOf("\"", quoteStart + 1);
                        if (quoteStart >= 0 && quoteEnd > quoteStart)
                        {
                            string url = messageJson.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                            CodelyLogger.Log($"Codely IPC: Received server_ready message with URL: {url}");
                            // 在 delayCall 入队前先写入 thread-safe 静态字段：失焦启动时主线程 update loop
                            // 被节流，OnTauriServerReady 不会及时跑；OnGUI 的 backstop 直接读这个 URL
                            // 解出端口，调 TryInitializeWebView2，绕过 delayCall 事件链。
                            CodelyIpcManager.SetLastServerReadyUrl(url);
                            EditorApplication.delayCall += () =>
                            {
                                OnServerReady?.Invoke(url);
                            };
                            // InvalidateRect GUIView 子窗口，强制走一次 WM_PAINT → OnGUI → backstop。
                            WakeUnityMainThread();
                            return;
                        }
                    }
                }

                var message = JsonUtility.FromJson<IpcMessage>(messageJson);

                if (message == null)
                {
                    CodelyLogger.LogWarning($"Codely IPC: Failed to parse message: {messageJson}");
                    return;
                }

                CodelyLogger.Log($"Codely IPC: Received message type '{message.messageType}', filepath: {message.filepath}");

                // focusWindow must bring Unity to the foreground immediately on this background
                // thread via OS-level APIs, because EditorApplication.delayCall won't run until
                // the editor update loop is active — which requires Unity to already have focus.
                if (message.messageType == IpcMessageType.FocusWindow)
                {
                    CodelyLogger.Log("Codely IPC: focusWindow received on background thread — calling BringUnityToFront()");
                    BringUnityToFront();
                }
                else if (message.messageType == IpcMessageType.DrillCompleted)
                {
                    Interlocked.Exchange(ref s_drillCompletedPending, 1);
                }

                // 在主线程中处理消息（Unity API 只能在主线程调用）
                EditorApplication.delayCall += () =>
                {
                    switch (message.messageType)
                    {
                        case IpcMessageType.ShowLines:
                            HandleShowLines(message.filepath, message.startLine, message.endLine);
                            break;

                        case IpcMessageType.OpenFile:
                            HandleOpenFile(message.filepath);
                            break;

                        case IpcMessageType.SelectAsset:
                            HandleSelectAsset(message.filepath);
                            break;

                        case IpcMessageType.GetColors:
                            HandleGetColors();
                            break;

                        case IpcMessageType.Recompile: // used when bridge is exported
                            HandleRecompile();
                            break;

                        case IpcMessageType.SetEmbedMode:
                            HandleSetEmbedMode(message.data);
                            break;

                        case IpcMessageType.CloseCodelyEditorOnWorkspaceSwitch:
                            OnCloseCodelyEditorOnWorkspaceSwitch?.Invoke();
                            break;

                        case IpcMessageType.DrillCompleted:
                            // Prefer event-driven transition first. If there is no listener on this
                            // IPC client, fallback to closing DrillWindow to avoid stuck walkthrough UI.
                            var drillCompletedHandler = OnDrillCompleted;
                            if (drillCompletedHandler != null)
                            {
                                drillCompletedHandler.Invoke();
                            }
                            else
                            {
                                ConsumePendingDrillCompleted();
                                foreach (var window in Resources.FindObjectsOfTypeAll<DrillWindow>())
                                {
                                    if (window == null) continue;
                                    try { window.Close(); }
                                    catch (Exception ex)
                                    {
                                        CodelyLogger.LogWarning($"Codely IPC: Failed to close DrillWindow: {ex.Message}");
                                    }
                                }
                            }
                            break;

                        case IpcMessageType.FocusWindow:
                            HandleFocusWindow();
                            break;

                        case IpcMessageType.UpdateRestart:
                            HandleUpdateRestart(message.data);
                            break;

                        default:
                            CodelyLogger.LogWarning($"Codely IPC: Unknown message type: {message.messageType}");
                            break;
                    }
                };

                if (message.messageType == IpcMessageType.DrillCompleted)
                {
                    WakeUnityMainThread();
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely IPC: Error processing message: {ex.Message}\nMessage: {messageJson}");
            }
        }

        private void HandleShowLines(string filepath, int startLine, int endLine)
        {
            CodelyLogger.Log($"Codely IPC: ShowLines - {filepath} ({startLine}-{endLine})");

            // 触发事件
            OnShowLines?.Invoke(filepath, startLine, endLine);

            // 选中文件并在 Project 窗口中高亮
            SelectAndPingAsset(filepath);

            // 打开文件并跳转到指定行
            OpenFileAtLine(filepath, startLine);
        }

        private void HandleOpenFile(string filepath)
        {
            CodelyLogger.Log($"Codely IPC: OpenFile - {filepath}");

            OnOpenFile?.Invoke(filepath);

            OpenFileAtLine(filepath, 0);
        }

        private void HandleSelectAsset(string filepath)
        {
            CodelyLogger.Log($"Codely IPC: SelectAsset - {filepath}");

            OnSelectAsset?.Invoke(filepath);

            SelectAndPingAsset(filepath);
        }

        private void HandleGetColors()
        {
            try
            {
                // Unity Editor theme mode (dark/light)
                string themeMode = EditorGUIUtility.isProSkin ? "dark" : "light";
                CodelyLogger.Log($"Codely IPC: GetColors - responding with theme mode: {themeMode}");

                // Send theme mode back to Tauri. Rust side will map mode -> colors.
                SendMessage(IpcMessageType.SetColors, themeMode);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely IPC: Error handling GetColors: {ex.Message}");
            }
        }

        private void HandleRecompile()
        {
            try
            {
                bool executed = EditorApplication.ExecuteMenuItem("Assets/Refresh");
                if (!executed)
                {
                    AssetDatabase.Refresh();
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely IPC: Error handling Recompile: {ex.Message}");
            }
        }

        private void HandleFocusWindow()
        {
            try
            {
                CodelyLogger.Log("Codely IPC: HandleFocusWindow called");
                var windows = Resources.FindObjectsOfTypeAll<CodelyWindow>();
                if (windows.Length > 0)
                {
                    CodelyLogger.Log($"Codely IPC: FocusWindow - found {windows.Length} CodelyWindow(s), calling Focus()");
                    windows[0].Focus();
                    CodelyLogger.Log("Codely IPC: FocusWindow - brought Codely window to foreground");
                }
                else
                {
                    CodelyLogger.LogWarning("Codely IPC: FocusWindow - no CodelyWindow instance found");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely IPC: Error handling FocusWindow: {ex.Message}");
            }
        }

        private void HandleUpdateRestart(string data)
        {
            try
            {
                OnUpdateRestart?.Invoke(data);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely IPC: Error handling UpdateRestart: {ex.Message}");
            }
        }

        private void HandleSetEmbedMode(string data)
        {
            bool embedMode = string.Equals(data, "true", StringComparison.OrdinalIgnoreCase);
            CodelyLogger.Log($"Codely IPC: SetEmbedMode - embedMode={embedMode}");
            OnSetEmbedMode?.Invoke(embedMode);
        }

        /// <summary>
        /// 在 Project 窗口中选中并高亮文件
        /// </summary>
        private void SelectAndPingAsset(string filepath)
        {
            try
            {
                // 将绝对路径转换为相对路径（相对于 Assets 目录）
                string relativePath = ConvertToAssetPath(filepath);

                if (string.IsNullOrEmpty(relativePath))
                {
                    CodelyLogger.LogWarning($"Codely IPC: Cannot convert path to asset path: {filepath}");
                    return;
                }

                // 加载资源
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(relativePath);

                if (asset == null)
                {
                    CodelyLogger.LogWarning($"Codely IPC: Asset not found at path: {relativePath}");
                    return;
                }

                // 选中资源
                Selection.activeObject = asset;

                // 在 Project 窗口中高亮显示（Ping）
                EditorGUIUtility.PingObject(asset);

                CodelyLogger.Log($"Codely IPC: Selected and pinged asset: {relativePath}");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely IPC: Error selecting asset: {ex.Message}");
            }
        }

        public static bool OpenWithCurrentEditor(string filePath, int line = -1, int column = -1)
        {
            // Use CurrentEditorInstallation (available since CodeEditor package was introduced)
            // rather than CurrentEditorPath, which doesn't exist on Unity 2020.
            string editorPath = CodeEditor.CurrentEditorInstallation;

            // Guard against an unconfigured / invalid external editor path. On macOS, passing an
            // empty or non-existent path into CodeEditor.OSOpenFile makes the native
            // LaunchOrReuseApp build a nil NSURL and crash with an uncatchable
            // NSInternalInconsistencyException (url != nil). Fall back to the OS default app,
            // which safely handles a missing handler instead of crashing the editor.
            if (string.IsNullOrEmpty(editorPath) || !EditorPathExists(editorPath))
            {
                CodelyLogger.LogWarning(
                    $"Codely IPC: External editor path is not configured or invalid ('{editorPath}'); " +
                    $"opening with default app: {filePath}");
                EditorUtility.OpenWithDefaultApp(filePath);
                return true;
            }

            string argumentsTemplate = GetEditorArguments(editorPath);

            string arguments = CodeEditor.ParseArgument(argumentsTemplate, filePath, line, column);

            return CodeEditor.OSOpenFile(editorPath, arguments);
        }

        /// <summary>
        /// 判断外部编辑器路径是否实际存在。macOS 上编辑器是 .app 包（目录），
        /// 其它平台是可执行文件，因此目录或文件存在都视为有效。
        /// </summary>
        private static bool EditorPathExists(string editorPath)
        {
            return File.Exists(editorPath) || Directory.Exists(editorPath);
        }

        /// <summary>
        /// 获取编辑器的参数配置
        /// </summary>
        private static string GetEditorArguments(string editorPath)
        {
            const string k_ArgumentKey = "kScriptEditorArgs";
            const string k_DefaultArgument = "$(File)";

            // 对于 macOS，有特殊的 key
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                string oldMacKey = "kScriptEditorArgs_" + editorPath;
                string oldMac = EditorPrefs.GetString(oldMacKey);
                if (!string.IsNullOrEmpty(oldMac))
                {
                    EditorPrefs.SetString(k_ArgumentKey + editorPath, oldMac);
                }
            }

            return EditorPrefs.GetString(k_ArgumentKey + editorPath, k_DefaultArgument);
        }

        /// <summary>
        /// 打开文件并跳转到指定行
        /// </summary>
        private void OpenFileAtLine(string filepath, int line)
        {
            try
            {
                // 将绝对路径转换为相对路径
                string relativePath = ConvertToAssetPath(filepath);

                if (string.IsNullOrEmpty(relativePath))
                {
                    // 如果不是 Assets 目录下的文件，尝试使用 Unity 默认的外部编辑器打开
                    if (File.Exists(filepath))
                    {
                        OpenWithCurrentEditor(filepath);
                    }
                    return;
                }

                // 加载资源
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(relativePath);

                if (asset != null)
                {
                    // 使用 Unity 的 API 打开文件
                    AssetDatabase.OpenAsset(asset, line);
                    CodelyLogger.Log($"Codely IPC: Opened asset at line {line}: {relativePath}");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely IPC: Error opening file: {ex.Message}");
            }
        }

        /// <summary>
        /// 将绝对路径转换为 Unity 资源路径（相对于项目根目录）
        /// </summary>
        private string ConvertToAssetPath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return null;

            if (absolutePath.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                absolutePath = absolutePath.Substring(8); // 去掉 "file:///"
            }
            else if (absolutePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                absolutePath = absolutePath.Substring(7); // 去掉 "file://"
            }

            // 标准化路径分隔符
            absolutePath = absolutePath.Replace('\\', '/');

            // 获取项目根目录
            string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');

            // 检查路径是否在项目目录内
            if (absolutePath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
            {
                // 获取相对路径
                string relativePath = absolutePath.Substring(projectPath.Length).TrimStart('/');

                // 确保以 Assets/ 开头或 Packages/ 开头
                if (relativePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                {
                    return relativePath;
                }
            }

            // 检查是否已经是相对路径
            if (absolutePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                absolutePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return absolutePath;
            }

            return null;
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            Stop();
        }

        /// <summary>
        /// Send a message to Tauri server
        /// </summary>
        public bool SendMessage(string messageType, string data = null)
        {
            lock (_lockObj)
            {
                if (!_isConnected)
                {
                    // Avoid noisy logs during domain reload / startup.
                    return false;
                }

                try
                {
                    // Create message
                    var message = new IpcMessage
                    {
                        messageType = messageType,
                        filepath = "",
                        startLine = 0,
                        endLine = 0,
                        data = data
                    };

                    // Serialize to JSON
                    string json = JsonUtility.ToJson(message);

#if UNITY_EDITOR_WIN
                    // Native side appends '\n' before writing — pass raw JSON bytes only.
                    byte[] payloadBytes = Encoding.UTF8.GetBytes(json);
                    int sent;
                    try { sent = NIC_SendMessage(payloadBytes, payloadBytes.Length); }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogWarning($"Codely IPC: NIC_SendMessage threw: {ex.Message}");
                        return false;
                    }
                    if (sent == 1)
                    {
                        CodelyLogger.Log($"Codely IPC: Sent message to Tauri: {messageType}");
                        return true;
                    }
                    return false;
#else
                    // Send to Tauri server (write to Unix socket)
                    if (_socket != null && _socket.Connected)
                    {
                        byte[] messageBytes = Encoding.UTF8.GetBytes(json + "\n");
                        _socket.Send(messageBytes);
                        CodelyLogger.Log($"Codely IPC: Sent message to Tauri: {messageType}");
                        return true;
                    }
                    else
                    {
                        return false;
                    }
#endif
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"Codely IPC: Error sending message: {ex.Message}");
                    return false;
                }
            }
        }
    }
}
#endif