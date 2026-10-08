#pragma once
#include <windows.h>
#include <atomic>
#include <functional>
#include <queue>
#include <mutex>
#include <string>

/// STA 线程 + Win32 消息泵 + 工作队列。
/// 所有 WebView2 COM 对象必须在同一 STA 线程上创建和访问。
class STAThread {
public:
    static constexpr UINT WM_EXECUTE = WM_APP + 1;

    /// 启动 STA 线程，阻塞直到线程就绪（message-only 窗口已创建）。
    explicit STAThread(std::wstring name = L"WebView2-STA");
    ~STAThread();

    STAThread(const STAThread&)            = delete;
    STAThread& operator=(const STAThread&) = delete;

    /// 在 STA 线程异步执行 work（线程安全，可从任意线程调用）。
    void Post(std::function<void()> work);

    /// 仅向 STA 线程投递 WM_QUIT，不等待退出（线程安全，幂等）。
    /// 调用方负责后续等待 ThreadHandle()。
    void RequestQuit();

    /// 停止 STA 线程（发送 WM_QUIT）并等待退出（最多 timeoutMs ms）。
    /// 等待使用 WaitForSingleObject——若调用方需要在等待期间 pump 消息，请使用 RequestQuit + ThreadHandle 自行 MsgWaitForMultipleObjects。
    void Shutdown(DWORD timeoutMs = 5000);

    HWND   PumpHWND()     const { return m_pumpHwnd; }
    HANDLE ThreadHandle() const { return m_thread; }

private:
    static DWORD WINAPI ThreadProc(LPVOID param);
    void Run();
    void DrainQueue();

    std::wstring                      m_name;
    HANDLE                            m_thread      = nullptr;
    HANDLE                            m_readyEvent  = nullptr;   // 线程就绪信号
    HWND                              m_pumpHwnd    = nullptr;   // message-only 窗口
    std::mutex                        m_queueMutex;
    std::queue<std::function<void()>> m_queue;
    std::atomic<bool>                 m_shutdown    { false };
};
