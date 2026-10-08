#include "STAThread.h"
#include <objbase.h>
#include <stdexcept>

static constexpr wchar_t kWndClass[] = L"NativeBrowserSTAPump";

static LRESULT CALLBACK PumpWndProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l)
{
    return DefWindowProcW(hwnd, msg, w, l);
}

// ── 构造/析构 ──────────────────────────────────────────────────────────────

STAThread::STAThread(std::wstring name)
    : m_name(std::move(name))
{
    m_readyEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!m_readyEvent)
        throw std::runtime_error("STAThread: CreateEvent failed");

    m_thread = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
    if (!m_thread)
    {
        CloseHandle(m_readyEvent);
        throw std::runtime_error("STAThread: CreateThread failed");
    }

    // 等待线程就绪（m_pumpHwnd 已创建，CoInitialize 已完成）
    WaitForSingleObject(m_readyEvent, INFINITE);
    CloseHandle(m_readyEvent);
    m_readyEvent = nullptr;
}

STAThread::~STAThread()
{
    Shutdown(5000);
    if (m_thread)
    {
        CloseHandle(m_thread);
        m_thread = nullptr;
    }
}

// ── 公开 API ──────────────────────────────────────────────────────────────

void STAThread::Post(std::function<void()> work)
{
    {
        std::lock_guard<std::mutex> lk(m_queueMutex);
        m_queue.push(std::move(work));
    }
    if (m_pumpHwnd)
        PostMessageW(m_pumpHwnd, WM_EXECUTE, 0, 0);
}

void STAThread::RequestQuit()
{
    // 幂等：仅第一次调用真正发送 WM_QUIT
    if (m_shutdown.exchange(true)) return;
    if (m_pumpHwnd)
        PostMessageW(m_pumpHwnd, WM_QUIT, 0, 0);
}

void STAThread::Shutdown(DWORD timeoutMs)
{
    RequestQuit();
    if (m_thread)
        WaitForSingleObject(m_thread, timeoutMs);
}

// ── 线程实现 ──────────────────────────────────────────────────────────────

DWORD WINAPI STAThread::ThreadProc(LPVOID param)
{
    reinterpret_cast<STAThread*>(param)->Run();
    return 0;
}

void STAThread::Run()
{
    // STA COM 初始化（必须在 GetMessage 循环前完成）
    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr) && hr != S_FALSE)
    {
        SetEvent(m_readyEvent);
        return;
    }

    // 注册 message-only 窗口类（忽略"已注册"错误，重用已有注册即可）
    HINSTANCE inst = GetModuleHandleW(nullptr);
    WNDCLASSEXW wc  = {};
    wc.cbSize        = sizeof(wc);
    wc.lpfnWndProc   = PumpWndProc;
    wc.hInstance     = inst;
    wc.lpszClassName = kWndClass;
    RegisterClassExW(&wc); // 忽略返回值；若已注册直接复用

    // 创建 message-only 窗口（HWND_MESSAGE = (HWND)-3）
    m_pumpHwnd = CreateWindowExW(
        0, kWndClass, L"", 0,
        0, 0, 0, 0,
        HWND_MESSAGE, nullptr, inst, nullptr);

    // 通知构造函数：线程已就绪
    SetEvent(m_readyEvent);

    if (!m_pumpHwnd)
    {
        CoUninitialize();
        return;
    }

    // 处理构造函数返回前就入队的工作项
    DrainQueue();

    // Win32 消息泵——同时服务用户工作队列和 WebView2 内部 COM 回调
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        if (msg.message == WM_EXECUTE)
        {
            DrainQueue();
            continue;  // 线程消息无需 Translate/Dispatch
        }
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    // 最后清理一次队列（处理 Shutdown 前投递的工作）
    DrainQueue();

    DestroyWindow(m_pumpHwnd);
    m_pumpHwnd = nullptr;

    UnregisterClassW(kWndClass, inst);
    CoUninitialize();
}

void STAThread::DrainQueue()
{
    for (;;)
    {
        std::function<void()> work;
        {
            std::lock_guard<std::mutex> lk(m_queueMutex);
            if (m_queue.empty()) break;
            work = std::move(m_queue.front());
            m_queue.pop();
        }
        // 在 STA 线程上执行，异常不向上传播（避免崩溃消息泵）
        try { work(); }
        catch (...) {}
    }
}
