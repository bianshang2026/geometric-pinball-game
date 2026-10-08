#include "../include/NativeBrowser.h"
#include "BrowserInstance.h"
#include <WebView2.h>
#include <string>
#include <thread>
#include <vector>

// ── 生命周期 ──────────────────────────────────────────────────────────────

NB_API void* NB_Create(HWND parentHwnd, const wchar_t* userDataFolder,
                        const wchar_t* initialUrl, const NB_Callbacks* callbacks)
{
    if (!parentHwnd || !IsWindow(parentHwnd)) return nullptr;

    try
    {
        auto* inst = new BrowserInstance(
            parentHwnd,
            userDataFolder ? userDataFolder : L"",
            initialUrl     ? initialUrl     : L"about:blank",
            callbacks      ? *callbacks     : NB_Callbacks{}
        );
        return inst;
    }
    catch (...) { return nullptr; }
}

NB_API void NB_Destroy(void* handle)
{
    if (!handle) return;
    auto* inst = static_cast<BrowserInstance*>(handle);

    inst->PrepareShutdown();
    std::thread([inst] {
        inst->FinalizeShutdown();
        delete inst;
    }).detach();
}

NB_API void NB_ShutdownAll(uint32_t timeoutMs)
{
    // 复制一份活跃实例列表，避免在等待期间与 NB_Destroy 的后台 delete 线程竞争。
    std::vector<BrowserInstance*> insts;
    {
        std::lock_guard<std::mutex> lk(BrowserInstance::s_liveSetMutex);
        insts.assign(BrowserInstance::s_liveInstances.begin(),
                     BrowserInstance::s_liveInstances.end());
    }
    if (insts.empty()) return;

    // Phase 1: PrepareShutdown 必须在调用线程（父 HWND 所属线程）上执行，
    // 因为 ~WndProcHook 调用 RemoveWindowSubclass 要求窗口所属线程。
    for (auto* inst : insts)
        try { inst->PrepareShutdown(); } catch (...) {}

    // Phase 2: 并行投递 close + WM_QUIT 到所有 STA 线程，收集线程句柄等待。
    std::vector<HANDLE> threads;
    threads.reserve(insts.size());
    for (auto* inst : insts)
    {
        try
        {
            inst->PostFinalizeShutdown();
            if (HANDLE h = inst->GetStaThreadHandle()) threads.push_back(h);
        }
        catch (...) {}
    }

    // Phase 3: 在调用线程 pump 消息等待 STA 线程退出。
    // 不能 WaitForSingleObject：ICoreWebView2Controller::Close 会跨 apartment 把
    // 工作 marshal 回父 HWND 所属线程；若调用线程阻塞不 pump 消息，STA 线程将
    // 永久卡在 Close 内部，msedgewebview2.exe 不会被通知退出，并且后续父 HWND
    // 销毁时无法整理归 STA 所有的子窗口 → 主线程挂起。
    const DWORD start = GetTickCount();
    while (!threads.empty())
    {
        const DWORD elapsed = GetTickCount() - start;
        if (elapsed >= timeoutMs) break;

        DWORD result = MsgWaitForMultipleObjectsEx(
            (DWORD)threads.size(), threads.data(),
            timeoutMs - elapsed,
            QS_ALLINPUT, MWMO_INPUTAVAILABLE);

        if (result == WAIT_TIMEOUT || result == WAIT_FAILED) break;

        if (result < WAIT_OBJECT_0 + threads.size())
        {
            threads.erase(threads.begin() + (result - WAIT_OBJECT_0));
            continue;
        }

        // 输入队列有消息：pump 以让 cross-apartment marshaling 完成。
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT)
            {
                // 调用方的主消息循环需要这条；重新投递并立即返回。
                PostQuitMessage((int)msg.wParam);
                return;
            }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

NB_API void NB_SetCallbacks(void* handle, const NB_Callbacks* callbacks)
{
    if (!handle || !callbacks) return;
    if (!BrowserInstance::IsLive(handle)) return;
    static_cast<BrowserInstance*>(handle)->SetCallbacks(*callbacks);
}

NB_API bool NB_IsHandleValid(void* handle)
{
    return BrowserInstance::IsLive(handle);
}

// ── 导航 ──────────────────────────────────────────────────────────────────

NB_API void NB_Navigate   (void* h, const wchar_t* url) { if (h) static_cast<BrowserInstance*>(h)->Navigate(url); }
NB_API void NB_GoBack     (void* h)                     { if (h) static_cast<BrowserInstance*>(h)->GoBack(); }
NB_API void NB_GoForward  (void* h)                     { if (h) static_cast<BrowserInstance*>(h)->GoForward(); }
NB_API void NB_Reload     (void* h)                     { if (h) static_cast<BrowserInstance*>(h)->Reload(); }
NB_API void NB_Stop       (void* h)                     { if (h) static_cast<BrowserInstance*>(h)->Stop(); }

// ── 布局 ──────────────────────────────────────────────────────────────────

NB_API void NB_Resize     (void* h, int x, int y, int w, int h2) { if (h) static_cast<BrowserInstance*>(h)->Resize(x, y, w, h2); }
NB_API void NB_SetVisible (void* h, bool v)                      { if (h) static_cast<BrowserInstance*>(h)->SetVisible(v); }
NB_API void NB_SetTopInset(void* h, int px)                      { if (h) static_cast<BrowserInstance*>(h)->SetTopInset(px); }

// ── 脚本 ──────────────────────────────────────────────────────────────────

NB_API void NB_ExecuteScript  (void* h, const wchar_t* js)   { if (h) static_cast<BrowserInstance*>(h)->ExecuteScript(js); }
NB_API void NB_PostWebMessage (void* h, const wchar_t* json) { if (h) static_cast<BrowserInstance*>(h)->PostWebMessage(json); }

// ── 焦点 ──────────────────────────────────────────────────────────────────

NB_API void NB_MoveFocus(void* h) { if (h) static_cast<BrowserInstance*>(h)->MoveFocus(); }

// ── 调试 ──────────────────────────────────────────────────────────────────

NB_API void NB_OpenDevTools(void* h) { if (h) static_cast<BrowserInstance*>(h)->OpenDevTools(); }

// ── Runtime 检测 ──────────────────────────────────────────────────────────

NB_API bool NB_IsRuntimeAvailable()
{
    LPWSTR version = nullptr;
    HRESULT hr = GetAvailableCoreWebView2BrowserVersionString(nullptr, &version);
    if (version) CoTaskMemFree(version);
    return SUCCEEDED(hr);
}

NB_API const wchar_t* NB_GetRuntimeVersion()
{
    static wchar_t s_buf[128] = {};
    LPWSTR version = nullptr;
    if (SUCCEEDED(GetAvailableCoreWebView2BrowserVersionString(nullptr, &version)) && version)
    {
        wcsncpy_s(s_buf, version, _TRUNCATE);
        CoTaskMemFree(version);
    }
    return s_buf;
}
