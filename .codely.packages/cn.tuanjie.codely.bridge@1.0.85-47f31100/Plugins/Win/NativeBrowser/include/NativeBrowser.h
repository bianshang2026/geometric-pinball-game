#pragma once
#include <windows.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef NATIVEBROWSER_EXPORTS
#  define NB_API extern "C" __declspec(dllexport)
#else
#  define NB_API extern "C" __declspec(dllimport)
#endif

// ── 回调函数类型 ──────────────────────────────────────────────────────────────
// 所有回调均从 STA 线程通过 PostMessage 投递到 Unity 主线程执行。
typedef void (__stdcall* NB_NavigationCallback)  (const wchar_t* url,   void* ctx);
typedef void (__stdcall* NB_LoadCompletedCallback)(bool success,          void* ctx);
typedef void (__stdcall* NB_TitleChangedCallback) (const wchar_t* title,  void* ctx);
typedef void (__stdcall* NB_MessageCallback)      (const wchar_t* json,   void* ctx);
typedef void (__stdcall* NB_DestroyedCallback)    (void* ctx);

#pragma pack(push, 8)
struct NB_Callbacks {
    NB_NavigationCallback    onNavigationStarting;   // 可为 null
    NB_LoadCompletedCallback onLoadCompleted;        // 可为 null
    NB_TitleChangedCallback  onTitleChanged;         // 可为 null
    NB_MessageCallback       onWebMessageReceived;   // 可为 null
    NB_DestroyedCallback     onParentDestroyed;      // 父 HWND 销毁时通知 C#
    void*                    ctx;                    // 透传给所有回调
};
#pragma pack(pop)

// ── 生命周期 ──────────────────────────────────────────────────────────────────
// NB_Create：启动独立 STA 线程，异步初始化 WebView2，完成后自动导航 initialUrl。
// 返回不透明句柄；失败（参数无效、Runtime 未安装等）返回 null。
NB_API void* NB_Create(
    HWND            parentHwnd,
    const wchar_t*  userDataFolder,
    const wchar_t*  initialUrl,
    const NB_Callbacks* callbacks   // 可为 null
);

NB_API void  NB_Destroy(void* handle);

// NB_ShutdownAll：同步关闭所有活跃 BrowserInstance（总耗时最长 timeoutMs）。
// 在调用线程上 pump 消息以驱动 WebView2 跨 apartment marshaling，避免父 HWND
// 所属线程被阻塞导致 Close 死锁。仅 PrepareShutdown + FinalizeShutdown，不释放
// 内存（进程即将退出，OS 回收即可，避免与 NB_Destroy 后台线程的 delete 竞争）。
// 必须在 Unity Editor 退出时（EditorApplication.quitting）调用一次。
NB_API void  NB_ShutdownAll(uint32_t timeoutMs);

// NB_SetCallbacks：替换托管回调函数指针。
// Unity Domain Reload 后旧指针失效，必须在 OnEnable 中调用此函数注册新委托。
NB_API void  NB_SetCallbacks(void* handle, const NB_Callbacks* callbacks);

// NB_IsHandleValid：判断 handle 是否仍指向一个活跃的 BrowserInstance。
// 不解引用指针，安全用于 Domain Reload 后的句柄验证。
NB_API bool  NB_IsHandleValid(void* handle);

// ── 导航（线程安全，内部转发到 STA 线程）──────────────────────────────────────
NB_API void  NB_Navigate    (void* handle, const wchar_t* url);
NB_API void  NB_GoBack      (void* handle);
NB_API void  NB_GoForward   (void* handle);
NB_API void  NB_Reload      (void* handle);
NB_API void  NB_Stop        (void* handle);

// ── 布局（线程安全）──────────────────────────────────────────────────────────
// NB_Create 内部已 hook 父 HWND 的 WM_SIZE，通常无需手动调用 NB_Resize。
NB_API void  NB_Resize      (void* handle, int x, int y, int w, int h);
NB_API void  NB_SetVisible  (void* handle, bool visible);
NB_API void  NB_SetTopInset (void* handle, int topPx);

// ── 脚本 ─────────────────────────────────────────────────────────────────────
NB_API void  NB_ExecuteScript  (void* handle, const wchar_t* js);
NB_API void  NB_PostWebMessage (void* handle, const wchar_t* json);

// ── Focus ──
// NB_MoveFocus: move keyboard focus into the WebView2 content (equivalent to
// ICoreWebView2Controller::MoveFocus with the PROGRAMMATIC reason). When the
// WebView2 is embedded as a child of Unity's GUIView, the parent keeps Win32
// focus after navigation, so HTML autofocus / element.focus() neither show a
// caret nor receive keystrokes; the host must call this to hand focus to the
// content. Thread-safe; forwarded to the STA thread internally.
NB_API void  NB_MoveFocus      (void* handle);

// ── 调试 ─────────────────────────────────────────────────────────────────────
NB_API void  NB_OpenDevTools(void* handle);

// ── Runtime 检测（同步，可在任意线程调用）───────────────────────────────────
NB_API bool           NB_IsRuntimeAvailable();
NB_API const wchar_t* NB_GetRuntimeVersion();  // 返回内部静态缓冲区
