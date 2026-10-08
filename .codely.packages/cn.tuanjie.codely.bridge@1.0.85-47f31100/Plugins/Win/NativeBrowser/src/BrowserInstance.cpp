#include "BrowserInstance.h"
#include <wrl/event.h>        // Microsoft::WRL::Callback
#include <shlwapi.h>

// ── 静态成员定义 ──────────────────────────────────────────────────────────────
std::mutex                                  BrowserInstance::s_mapMutex;
std::unordered_map<HWND, BrowserInstance*> BrowserInstance::s_hwndMap;

std::unordered_set<BrowserInstance*>       BrowserInstance::s_liveInstances;
std::mutex                                  BrowserInstance::s_liveSetMutex;

// ── WinEvent 回调（WINEVENT_INCONTEXT → 在 Unity 主线程同步调用）────────────
void CALLBACK BrowserInstance::WinEventProc(
    HWINEVENTHOOK /*hook*/, DWORD /*event*/,
    HWND hwnd, LONG objId, LONG childId,
    DWORD /*idEventThread*/, DWORD /*dwmsEventTime*/)
{
    if (objId != 0 /*OBJID_WINDOW*/ || childId != 0 /*CHILDID_SELF*/) return;

    BrowserInstance* inst = nullptr;
    {
        std::lock_guard<std::mutex> lk(s_mapMutex);
        auto it = s_hwndMap.find(hwnd);
        if (it == s_hwndMap.end()) return;
        inst = it->second;
    }
    if (!inst || inst->m_shutdown.load()) return;

    RECT rc = {};
    if (!GetClientRect(hwnd, &rc)) return;
    inst->PostResizeBounds(rc.right, rc.bottom);
}

// ── Constructor / Destructor ──────────────────────────────────────────────────

BrowserInstance::BrowserInstance(HWND parentHwnd,
                                 std::wstring userDataFolder,
                                 std::wstring initialUrl,
                                 NB_Callbacks callbacks)
    : m_parentHwnd(parentHwnd)
    , m_userDataFolder(std::move(userDataFolder))
    , m_initialUrl(std::move(initialUrl))
    , m_callbacks(callbacks)
{
    {
        std::lock_guard<std::mutex> lk(s_liveSetMutex);
        s_liveInstances.insert(this);
    }

    m_staThread = std::make_unique<STAThread>(L"NativeBrowser-STA");

    m_wndProcHook = std::make_unique<WndProcHook>(
        parentHwnd,
        [this](int w, int h) { PostResizeBounds(w, h); },
        [this] { FireParentDestroyed(); }
    );

    m_staThread->Post([this] { InitOnSTAThread(); });
}

BrowserInstance::~BrowserInstance()
{
    {
        std::lock_guard<std::mutex> lk(s_liveSetMutex);
        s_liveInstances.erase(this);
    }

    // 析构时确保两阶段都完成（正常情况下 NB_Destroy 已经完成了 Phase 1；
    // 若析构由 NB_Destroy 的后台线程 delete 触发，Phase 2 也已完成，均幂等）。
    PrepareShutdown();
    FinalizeShutdown();
}

// ── 两阶段关闭 ────────────────────────────────────────────────────────────────

bool BrowserInstance::PrepareShutdown()
{
    // m_shutdown 保证幂等：只有第一次调用执行实际逻辑
    if (m_shutdown.exchange(true)) return false;

    // 1. 从全局映射删除 → WinEvent/WM_SIZE 回调立即找不到此实例（线程安全）
    {
        std::lock_guard<std::mutex> lk(s_mapMutex);
        s_hwndMap.erase(m_parentHwnd);
    }

    // 2. 卸载 WndProcHook（调用 RemoveWindowSubclass，必须在 Unity 主线程）
    m_wndProcHook.reset();

    // WinEvent 的 UnhookWinEvent 需在 STA 线程（与 SetWinEventHook 同线程），
    // 留给 FinalizeShutdown 通过 STA Post 完成。

    return true;
}

void BrowserInstance::PostFinalizeShutdown()
{
    // m_finalized 保证幂等
    if (m_finalized.exchange(true)) return;

    // 确保 PrepareShutdown 已完成（若直接调用 FinalizeShutdown 而未先 Prepare）
    PrepareShutdown();

    // 捕获 WinEvent hook 供 STA 线程卸载（原子交换，避免与 RegisterWinEventHook 竞争）
    HWINEVENTHOOK hook = m_winEventHook.exchange(nullptr);

    // 向 STA 线程投递：卸载 WinEvent hook + 关闭 WebView2 COM 对象
    m_staThread->Post([this, hook] {
        if (hook) UnhookWinEvent(hook);   // 与 SetWinEventHook 同线程（STA）

        if (m_webview)
        {
            m_webview->remove_NavigationStarting(m_navigationToken);
            m_webview->remove_DocumentTitleChanged(m_titleToken);
            m_webview->remove_WebMessageReceived(m_messageToken);
            m_webview->remove_NavigationCompleted(m_loadToken);
        }
        if (m_controller) m_controller->Close();
        m_webview.Reset();
        m_controller.Reset();
        m_env.Reset();
    });

    // 投递 WM_QUIT，让 STA 线程在处理完上述 close 工作后退出。
    // 不在此函数内等待——由调用方负责（NB_ShutdownAll 用 pumped wait 避免死锁）。
    m_staThread->RequestQuit();
}

HANDLE BrowserInstance::GetStaThreadHandle() const
{
    return m_staThread ? m_staThread->ThreadHandle() : nullptr;
}

void BrowserInstance::FinalizeShutdown(DWORD timeoutMs)
{
    PostFinalizeShutdown();   // 幂等
    if (m_staThread)
        m_staThread->Shutdown(timeoutMs);   // RequestQuit 已发；这里只是 WaitForSingleObject
}

// ── 跨 Domain Reload 恢复 ─────────────────────────────────────────────────────

bool BrowserInstance::IsLive(void* handle)
{
    if (!handle) return false;
    std::lock_guard<std::mutex> lk(s_liveSetMutex);
    return s_liveInstances.count(static_cast<BrowserInstance*>(handle)) > 0;
}

void BrowserInstance::SetCallbacks(const NB_Callbacks& callbacks)
{
    std::lock_guard<std::mutex> lk(m_callbacksMutex);
    m_callbacks = callbacks;
}

// ── Public API ────────────────────────────────────────────────────────────────

void BrowserInstance::Navigate(const wchar_t* url)
{
    std::wstring u(url ? url : L"");
    m_staThread->Post([this, u = std::move(u)] {
        if (m_webview) m_webview->Navigate(u.c_str());
    });
}

void BrowserInstance::GoBack()    { m_staThread->Post([this] { if (m_webview) m_webview->GoBack(); }); }
void BrowserInstance::GoForward() { m_staThread->Post([this] { if (m_webview) m_webview->GoForward(); }); }
void BrowserInstance::Reload()    { m_staThread->Post([this] { if (m_webview) m_webview->Reload(); }); }
void BrowserInstance::Stop()      { m_staThread->Post([this] { if (m_webview) m_webview->Stop(); }); }

void BrowserInstance::Resize(int x, int y, int w, int h)
{
    m_staThread->Post([this, x, y, w, h] {
        if (m_controller) m_controller->put_Bounds({ x, y, x + w, y + h });
    });
}

void BrowserInstance::SetVisible(bool v)
{
    m_staThread->Post([this, v] {
        if (m_controller) m_controller->put_IsVisible(v ? TRUE : FALSE);
    });
}

void BrowserInstance::ExecuteScript(const wchar_t* js)
{
    std::wstring s(js ? js : L"");
    m_staThread->Post([this, s = std::move(s)] {
        if (m_webview) m_webview->ExecuteScript(s.c_str(), nullptr);
    });
}

void BrowserInstance::PostWebMessage(const wchar_t* json)
{
    std::wstring s(json ? json : L"");
    m_staThread->Post([this, s = std::move(s)] {
        if (m_webview) m_webview->PostWebMessageAsJson(s.c_str());
    });
}

void BrowserInstance::OpenDevTools()
{
    m_staThread->Post([this] {
        if (m_webview) m_webview->OpenDevToolsWindow();
    });
}

void BrowserInstance::MoveFocus()
{
    // 必须在拥有 controller 的 STA 线程上调用。PROGRAMMATIC 让 WebView2 取得 Win32 焦点，
    // 内容文档随之获得系统焦点，挂起的 autofocus / 已聚焦元素的光标得以显示并接收键盘输入。
    m_staThread->Post([this] {
        if (m_controller && !m_shutdown.load())
            m_controller->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
    });
}

void BrowserInstance::SetTopInset(int px)
{
    m_topInset.store(px < 0 ? 0 : px);
    m_staThread->Post([this] { SyncBounds(); });
}

// ── Resize 合并投递（主线程调用）─────────────────────────────────────────────
void BrowserInstance::PostResizeBounds(int w, int h)
{
    m_pendingW.store((LONG)w);
    m_pendingH.store((LONG)h);

    bool expected = false;
    if (m_resizePending.compare_exchange_strong(expected, true))
    {
        m_staThread->Post([this] {
            m_resizePending.store(false);
            if (!m_controller || m_shutdown.load()) return;
            LONG w     = m_pendingW.load();
            LONG h     = m_pendingH.load();
            int  inset = m_topInset.load();
            m_controller->put_Bounds({ 0, inset, (int)w, (int)h });
        });
    }
}

// ── WinEvent Hook ─────────────────────────────────────────────────────────────

void BrowserInstance::RegisterWinEventHook()
{
    HMODULE hMod = nullptr;
    GetModuleHandleExW(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&BrowserInstance::WinEventProc),
        &hMod);

    DWORD parentThreadId = GetWindowThreadProcessId(m_parentHwnd, nullptr);
    m_winEventHook = SetWinEventHook(
        EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
        hMod,
        WinEventProc,
        0,
        parentThreadId,
        WINEVENT_INCONTEXT
    );
}

// ── WebView2 Initialization (runs on STA thread) ──────────────────────────────

void BrowserInstance::InitOnSTAThread()
{
    using namespace Microsoft::WRL;

    HRESULT hr = CreateCoreWebView2EnvironmentWithOptions(
        nullptr,
        m_userDataFolder.c_str(),
        nullptr,
        Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
            [this](HRESULT result, ICoreWebView2Environment* env) -> HRESULT
            {
                if (FAILED(result) || !env) { FireLoadCompleted(false); return result; }
                m_env = env;

                return env->CreateCoreWebView2Controller(
                    m_parentHwnd,
                    Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
                        [this](HRESULT result, ICoreWebView2Controller* controller) -> HRESULT
                        {
                            if (FAILED(result) || !controller) { FireLoadCompleted(false); return result; }
                            m_controller = controller;
                            m_controller->get_CoreWebView2(&m_webview);

                            SyncBounds();

                            ComPtr<ICoreWebView2Settings> settings;
                            if (SUCCEEDED(m_webview->get_Settings(&settings)))
                            {
                                settings->put_IsStatusBarEnabled(FALSE);
                                settings->put_AreDevToolsEnabled(TRUE);
                            }

                            m_webview->add_NavigationStarting(
                                Callback<ICoreWebView2NavigationStartingEventHandler>(
                                    [this](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT
                                    {
                                        LPWSTR uri = nullptr;
                                        args->get_Uri(&uri);
                                        if (uri) { FireNavigationStarting(uri); CoTaskMemFree(uri); }
                                        return S_OK;
                                    }).Get(), &m_navigationToken);

                            m_webview->add_DocumentTitleChanged(
                                Callback<ICoreWebView2DocumentTitleChangedEventHandler>(
                                    [this](ICoreWebView2* wv, IUnknown*) -> HRESULT
                                    {
                                        LPWSTR title = nullptr;
                                        wv->get_DocumentTitle(&title);
                                        if (title) { FireTitleChanged(title); CoTaskMemFree(title); }
                                        return S_OK;
                                    }).Get(), &m_titleToken);

                            m_webview->add_WebMessageReceived(
                                Callback<ICoreWebView2WebMessageReceivedEventHandler>(
                                    [this](ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* args) -> HRESULT
                                    {
                                        LPWSTR json = nullptr;
                                        args->get_WebMessageAsJson(&json);
                                        if (json) { FireWebMessageReceived(json); CoTaskMemFree(json); }
                                        return S_OK;
                                    }).Get(), &m_messageToken);

                            m_webview->add_NavigationCompleted(
                                Callback<ICoreWebView2NavigationCompletedEventHandler>(
                                    [this](ICoreWebView2*, ICoreWebView2NavigationCompletedEventArgs* args) -> HRESULT
                                    {
                                        BOOL success = FALSE;
                                        args->get_IsSuccess(&success);
                                        FireLoadCompleted(success == TRUE);
                                        return S_OK;
                                    }).Get(), &m_loadToken);

                            m_ready = true;

                            {
                                std::lock_guard<std::mutex> lk(s_mapMutex);
                                s_hwndMap[m_parentHwnd] = this;
                            }
                            RegisterWinEventHook();

                            m_webview->Navigate(m_initialUrl.c_str());
                            return S_OK;
                        }).Get());
            }).Get());

    if (FAILED(hr))
        FireLoadCompleted(false);
}

void BrowserInstance::SyncBounds()
{
    if (!m_controller || !m_parentHwnd) return;
    RECT rc = {};
    GetClientRect(m_parentHwnd, &rc);
    int inset = m_topInset.load();
    m_controller->put_Bounds({ 0, inset, rc.right, rc.bottom });
}

// ── Callbacks ─────────────────────────────────────────────────────────────────

void BrowserInstance::FireNavigationStarting(const std::wstring& url)
{
    NB_NavigationCallback cb; void* ctx;
    { std::lock_guard<std::mutex> lk(m_callbacksMutex); cb = m_callbacks.onNavigationStarting; ctx = m_callbacks.ctx; }
    if (cb) cb(url.c_str(), ctx);
}

void BrowserInstance::FireLoadCompleted(bool success)
{
    NB_LoadCompletedCallback cb; void* ctx;
    { std::lock_guard<std::mutex> lk(m_callbacksMutex); cb = m_callbacks.onLoadCompleted; ctx = m_callbacks.ctx; }
    if (cb) cb(success, ctx);
}

void BrowserInstance::FireTitleChanged(const std::wstring& title)
{
    NB_TitleChangedCallback cb; void* ctx;
    { std::lock_guard<std::mutex> lk(m_callbacksMutex); cb = m_callbacks.onTitleChanged; ctx = m_callbacks.ctx; }
    if (cb) cb(title.c_str(), ctx);
}

void BrowserInstance::FireWebMessageReceived(const std::wstring& json)
{
    NB_MessageCallback cb; void* ctx;
    { std::lock_guard<std::mutex> lk(m_callbacksMutex); cb = m_callbacks.onWebMessageReceived; ctx = m_callbacks.ctx; }
    if (cb) cb(json.c_str(), ctx);
}

void BrowserInstance::FireParentDestroyed()
{
    NB_DestroyedCallback cb; void* ctx;
    { std::lock_guard<std::mutex> lk(m_callbacksMutex); cb = m_callbacks.onParentDestroyed; ctx = m_callbacks.ctx; }
    if (cb) cb(ctx);
}
