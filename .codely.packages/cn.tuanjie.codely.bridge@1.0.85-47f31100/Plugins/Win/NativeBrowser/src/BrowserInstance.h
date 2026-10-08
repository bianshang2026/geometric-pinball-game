#pragma once
#include <windows.h>
#include <wrl/client.h>
#include <WebView2.h>
#include <string>
#include <memory>
#include <atomic>
#include <mutex>
#include <unordered_map>
#include <unordered_set>
#include "../include/NativeBrowser.h"
#include "STAThread.h"
#include "WndProcHook.h"


class BrowserInstance {
public:
    BrowserInstance(HWND            parentHwnd,
                    std::wstring    userDataFolder,
                    std::wstring    initialUrl,
                    NB_Callbacks    callbacks);
    ~BrowserInstance();

    // ── 跨 Domain Reload 恢复支持 ─────────────────────────────────────────
    // 用于验证一个 void* 是否仍指向活跃的 BrowserInstance，避免野指针崩溃
    static bool IsLive(void* handle);
    // 替换托管回调函数指针（Domain Reload 后旧指针已失效，必须在 OnEnable 中调用）
    void SetCallbacks(const NB_Callbacks& callbacks);

    // 活跃实例注册表（供 NativeBrowser.cpp 的 NB_IsHandleValid 使用）
    static std::unordered_set<BrowserInstance*> s_liveInstances;
    static std::mutex                           s_liveSetMutex;

    BrowserInstance(const BrowserInstance&)            = delete;
    BrowserInstance& operator=(const BrowserInstance&) = delete;

    void Navigate    (const wchar_t* url);
    void GoBack      ();
    void GoForward   ();
    void Reload      ();
    void Stop        ();
    void Resize      (int x, int y, int w, int h);
    void SetVisible  (bool v);
    void SetTopInset (int px);
    void ExecuteScript  (const wchar_t* js);
    void PostWebMessage (const wchar_t* json);
    void OpenDevTools   ();
    void MoveFocus      ();

    bool PrepareShutdown();
    void FinalizeShutdown(DWORD timeoutMs = 5000);

    // 拆分版 FinalizeShutdown：仅投递 close 工作和 WM_QUIT 到 STA 线程，不等待。
    // 调用方需通过 GetStaThreadHandle() 自行等待——通常用 MsgWaitForMultipleObjects
    // pump 消息，避免 WebView2 的 cross-apartment marshaling 在父 HWND 所属线程上死锁。
    void PostFinalizeShutdown();
    HANDLE GetStaThreadHandle() const;

    void PostResizeBounds(int w, int h);

private:
    void InitOnSTAThread();
    void SyncBounds();
    void RegisterWinEventHook();

    static void CALLBACK WinEventProc(HWINEVENTHOOK hook, DWORD event,
                                      HWND hwnd, LONG objId, LONG childId,
                                      DWORD idEventThread, DWORD dwmsEventTime);

    static std::mutex                                  s_mapMutex;
    static std::unordered_map<HWND, BrowserInstance*> s_hwndMap;

    void FireNavigationStarting (const std::wstring& url);
    void FireLoadCompleted      (bool success);
    void FireTitleChanged       (const std::wstring& title);
    void FireWebMessageReceived (const std::wstring& json);
    void FireParentDestroyed    ();

    HWND         m_parentHwnd;
    std::wstring m_userDataFolder;
    std::wstring m_initialUrl;
    NB_Callbacks m_callbacks;

    std::unique_ptr<STAThread>    m_staThread;
    std::unique_ptr<WndProcHook>  m_wndProcHook;

    Microsoft::WRL::ComPtr<ICoreWebView2Environment> m_env;
    Microsoft::WRL::ComPtr<ICoreWebView2Controller>  m_controller;
    Microsoft::WRL::ComPtr<ICoreWebView2>            m_webview;

    EventRegistrationToken m_navigationToken  = {};
    EventRegistrationToken m_titleToken       = {};
    EventRegistrationToken m_messageToken     = {};
    EventRegistrationToken m_loadToken        = {};

    std::atomic<HWINEVENTHOOK> m_winEventHook { nullptr };

    std::atomic<LONG> m_pendingW       { 0 };
    std::atomic<LONG> m_pendingH       { 0 };
    std::atomic<bool> m_resizePending  { false };

    std::atomic<bool> m_ready     { false };
    std::atomic<bool> m_shutdown  { false };
    std::atomic<bool> m_finalized { false };
    std::atomic<int>  m_topInset  { 0 };

    // 保护 m_callbacks 的读写，SetCallbacks 在 Unity 主线程调用，
    // Fire* 系列在 STA 线程调用，需要互斥
    mutable std::mutex m_callbacksMutex;
};
