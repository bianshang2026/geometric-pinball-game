#if UNITY_EDITOR_WIN
using System;
using System.Runtime.InteropServices;

namespace Cn.Tuanjie.Codely.Editor
{
    /// <summary>
    /// NativeBrowser.dll 的 P/Invoke 声明。
    /// DLL 内部管理 STA 线程、WebView2 COM 生命周期和 WndProcHook，
    /// C# 侧只需持有不透明句柄并调用 API。
    /// </summary>
    internal static class NativeBrowserAPI
    {
        const string DLL = "NativeBrowser";

        // ── 回调委托（必须 stdcall + 持有强引用，防止 GC 回收）────────────────
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public delegate void NavigationCallback(string url, IntPtr ctx);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void LoadCompletedCallback(bool success, IntPtr ctx);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public delegate void TitleChangedCallback(string title, IntPtr ctx);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public delegate void MessageCallback(string json, IntPtr ctx);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void DestroyedCallback(IntPtr ctx);

        [StructLayout(LayoutKind.Sequential)]
        public struct Callbacks
        {
            public NavigationCallback    onNavigationStarting;
            public LoadCompletedCallback onLoadCompleted;
            public TitleChangedCallback  onTitleChanged;
            public MessageCallback       onWebMessageReceived;
            public DestroyedCallback     onParentDestroyed;
            public IntPtr                ctx;
        }

        // ── 生命周期 ──────────────────────────────────────────────────────────
        [DllImport(DLL, CharSet = CharSet.Unicode)]
        public static extern IntPtr NB_Create(
            IntPtr parentHwnd,
            string userDataFolder,
            string initialUrl,
            ref Callbacks callbacks);

        [DllImport(DLL)]
        public static extern void NB_Destroy(IntPtr handle);

        /// <summary>
        /// 同步关闭所有活跃 BrowserInstance（总耗时最长 timeoutMs）。
        /// 在调用线程 pump 消息以驱动 WebView2 跨 apartment marshaling，避免父 HWND
        /// 所属线程被阻塞导致 Close 死锁。仅 PrepareShutdown + FinalizeShutdown，
        /// 不释放内存（进程即将退出由 OS 回收）。必须在 EditorApplication.quitting 调用一次。
        /// </summary>
        [DllImport(DLL)]
        public static extern void NB_ShutdownAll(uint timeoutMs);

        /// <summary>
        /// 替换 BrowserInstance 内的托管回调函数指针。
        /// Unity Domain Reload 后旧委托已失效，必须在 OnEnable 中调用此函数注册新委托。
        /// </summary>
        [DllImport(DLL)]
        public static extern void NB_SetCallbacks(IntPtr handle, ref Callbacks callbacks);

        /// <summary>
        /// 判断 handle 是否仍指向一个活跃的 BrowserInstance（不解引用指针，安全）。
        /// 用于 Domain Reload 后验证序列化的句柄是否仍然有效。
        /// </summary>
        [DllImport(DLL)]
        public static extern bool NB_IsHandleValid(IntPtr handle);

        // ── 导航 ──────────────────────────────────────────────────────────────
        [DllImport(DLL, CharSet = CharSet.Unicode)]
        public static extern void NB_Navigate(IntPtr handle, string url);

        [DllImport(DLL)] public static extern void NB_GoBack   (IntPtr handle);
        [DllImport(DLL)] public static extern void NB_GoForward(IntPtr handle);
        [DllImport(DLL)] public static extern void NB_Reload   (IntPtr handle);
        [DllImport(DLL)] public static extern void NB_Stop     (IntPtr handle);

        // ── 布局 ──────────────────────────────────────────────────────────────
        [DllImport(DLL)] public static extern void NB_Resize     (IntPtr handle, int x, int y, int w, int h);
        [DllImport(DLL)] public static extern void NB_SetVisible (IntPtr handle, bool visible);
        [DllImport(DLL)] public static extern void NB_SetTopInset(IntPtr handle, int topPx);

        // ── 脚本 ──────────────────────────────────────────────────────────────
        [DllImport(DLL, CharSet = CharSet.Unicode)]
        public static extern void NB_ExecuteScript(IntPtr handle, string js);

        [DllImport(DLL, CharSet = CharSet.Unicode)]
        public static extern void NB_PostWebMessage(IntPtr handle, string json);

        // ── 焦点 ──────────────────────────────────────────────────────────────
        // 将键盘焦点移入 WebView2 内容（ICoreWebView2Controller::MoveFocus PROGRAMMATIC）。
        // 嵌入 Unity 后导航完成时焦点仍在父窗口，autofocus/element.focus() 不生效，需显式调用。
        [DllImport(DLL)] public static extern void NB_MoveFocus(IntPtr handle);

        // ── 调试 ──────────────────────────────────────────────────────────────
        [DllImport(DLL)] public static extern void NB_OpenDevTools(IntPtr handle);

        // ── Runtime 检测 ──────────────────────────────────────────────────────
        [DllImport(DLL)] public static extern bool NB_IsRuntimeAvailable();

        [DllImport(DLL, CharSet = CharSet.Unicode)]
        public static extern IntPtr NB_GetRuntimeVersion(); // 返回 DLL 内部静态字符串
    }
}
#endif
