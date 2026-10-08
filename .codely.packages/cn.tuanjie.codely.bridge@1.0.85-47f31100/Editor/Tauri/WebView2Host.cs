#if UNITY_EDITOR_WIN
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using static Cn.Tuanjie.Codely.Editor.NativeBrowserAPI;

using UnityTcp.Editor.Helpers;
namespace Cn.Tuanjie.Codely.Editor
{
    /// <summary>
    /// WebView2 嵌入式浏览器宿主（Windows）。
    ///
    /// 所有线程安全、STA 消息泵、WebView2 COM 生命周期均由 NativeBrowser.dll 内部处理，
    /// 本类只负责 P/Invoke 调用和委托 GC 保护。
    /// </summary>
    internal sealed class WebView2Host : IDisposable
    {
        IntPtr   m_handle;

        // 委托实例必须持有强引用，否则 GC 回收后 native 调用函数指针会崩溃
        NavigationCallback    m_onNav;
        LoadCompletedCallback m_onLoad;
        TitleChangedCallback  m_onTitle;
        MessageCallback       m_onMessage;
        DestroyedCallback     m_onDestroyed;

        public event Action            OnParentDestroyed;
        public event Action<string>    OnTitleChanged;
        public event Action<string>    OnWebMessageReceived;
        public event Action<bool>      OnLoadCompleted;
        public event Action<string>    OnNavigationStarting;

        public bool IsReady => m_handle != IntPtr.Zero;

        /// <summary>
        /// 原生 BrowserInstance 指针，用于序列化跨 Domain Reload 保活。
        /// </summary>
        public IntPtr Handle => m_handle;

        // ── Domain Reload 恢复 ───────────────────────────────────────────────

        /// <summary>
        /// 从序列化的原生句柄恢复，复用已存活的 BrowserInstance。
        /// 替换旧的（已失效的）托管回调指针为新委托，使 WebView2 继续正常工作。
        /// </summary>
        /// <returns>恢复成功返回 true；handle 无效或已被销毁返回 false。</returns>
        public bool RestoreFromHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return false;
            if (!NB_IsHandleValid(handle))  return false;

            m_handle = handle;

            // 重新构造委托实例（旧域的委托已失效，必须在新域中重新创建）
            m_onNav       = (url, _)  => OnNavigationStarting?.Invoke(url);
            m_onLoad      = (ok,  _)  => OnLoadCompleted?.Invoke(ok);
            m_onTitle     = (t,   _)  => OnTitleChanged?.Invoke(t);
            m_onMessage   = (json, _) => OnWebMessageReceived?.Invoke(json);
            m_onDestroyed = (_)       => OnParentDestroyed?.Invoke();

            var cb = new Callbacks
            {
                onNavigationStarting  = m_onNav,
                onLoadCompleted       = m_onLoad,
                onTitleChanged        = m_onTitle,
                onWebMessageReceived  = m_onMessage,
                onParentDestroyed     = m_onDestroyed,
                ctx                   = IntPtr.Zero,
            };

            NB_SetCallbacks(handle, ref cb);
            return true;
        }

        // ── 初始化（同步返回，WebView2 在 native STA 线程异步初始化）──────────

        public void Init(IntPtr parentHwnd, string initialUrl = "about:blank")
        {
            if (!NB_IsRuntimeAvailable())
                throw new InvalidOperationException(
                    "[WebView2Host] WebView2 Runtime not installed. " +
                    "Please install Microsoft Edge or WebView2 Evergreen Runtime.");

            string version = Marshal.PtrToStringUni(NB_GetRuntimeVersion());
            CodelyLogger.Log($"[WebView2Host] Runtime version: {version}");

            string dataFolder = Path.Combine(
                    Application.temporaryCachePath,
                    "WebView2_" + parentHwnd.ToInt64().ToString("X"))
                .Replace('/', '\\');

            // 构造回调结构，成员委托持有到 Dispose
            m_onNav       = (url, _)     => OnNavigationStarting?.Invoke(url);
            m_onLoad      = (ok,  _)     => OnLoadCompleted?.Invoke(ok);
            m_onTitle     = (t,   _)     => OnTitleChanged?.Invoke(t);
            m_onMessage   = (json, _)    => OnWebMessageReceived?.Invoke(json);
            m_onDestroyed = (_)          => OnParentDestroyed?.Invoke();

            var cb = new Callbacks
            {
                onNavigationStarting  = m_onNav,
                onLoadCompleted       = m_onLoad,
                onTitleChanged        = m_onTitle,
                onWebMessageReceived  = m_onMessage,
                onParentDestroyed     = m_onDestroyed,
                ctx                   = IntPtr.Zero,
            };

            m_handle = NB_Create(parentHwnd, dataFolder, initialUrl, ref cb);

            if (m_handle == IntPtr.Zero)
                throw new Exception("[WebView2Host] NB_Create failed (check native logs)");

            CodelyLogger.Log($"[WebView2Host] Created, handle=0x{m_handle:X}, url={initialUrl}");
        }

        // ── 公开 API（线程安全，native 内部转发到 STA 线程）──────────────────

        public void Navigate(string url)
        {
            if (IsReady && !string.IsNullOrEmpty(url))
                NB_Navigate(m_handle, url);
        }

        public void GoBack()      { if (IsReady) NB_GoBack(m_handle); }
        public void GoForward()   { if (IsReady) NB_GoForward(m_handle); }
        public void Reload()      { if (IsReady) NB_Reload(m_handle); }

        public void ExecuteScript(string js)
        {
            if (IsReady && !string.IsNullOrEmpty(js))
                NB_ExecuteScript(m_handle, js);
        }

        public void PostWebMessage(string json)
        {
            if (IsReady && !string.IsNullOrEmpty(json))
                NB_PostWebMessage(m_handle, json);
        }

        public void SetTopInset(int topPx)
        {
            if (IsReady) NB_SetTopInset(m_handle, topPx);
        }

        /// <summary>
        /// Set WebView bounds in parent client coordinates (pixels).
        /// Use this to match the exact content area from Unity layout (embedRect).
        /// </summary>
        public void Resize(int x, int y, int w, int h)
        {
            if (IsReady && w > 0 && h > 0) NB_Resize(m_handle, x, y, w, h);
        }

        public void SetVisible(bool visible)
        {
            if (IsReady) NB_SetVisible(m_handle, visible);
        }

        public void ShowDevTools()
        {
            if (IsReady) NB_OpenDevTools(m_handle);
        }

        /// <summary>
        /// 将键盘焦点移入 WebView2 内容。嵌入 Unity GUIView 后，导航完成时父窗口仍持有
        /// Win32 焦点，HTML 的 autofocus / element.focus() 不会显示光标也收不到键盘输入；
        /// 调用本方法把焦点交给内容（等价于浏览器中窗口被激活时内容自动获得焦点）。
        /// </summary>
        public void MoveFocus()
        {
            if (IsReady) NB_MoveFocus(m_handle);
        }

        // ── 清理 ──────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (m_handle != IntPtr.Zero)
            {
                NB_Destroy(m_handle);
                m_handle = IntPtr.Zero;
            }
            // 清空委托引用，让 GC 可以回收（各类型不同，不能链式赋值）
            m_onNav       = null;
            m_onLoad      = null;
            m_onTitle     = null;
            m_onMessage   = null;
            m_onDestroyed = null;
        }
    }
}
#endif
