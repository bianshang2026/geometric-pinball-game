#pragma once
#include <windows.h>
#include <commctrl.h>
#include <functional>

/// 使用 SetWindowSubclass 对父 HWND 进行子类化，
/// 拦截 WM_SIZE（同步 WebView2 大小）和 WM_DESTROY（通知 C# 重建）。
/// SetWindowSubclass 比 SetWindowLongPtr 更安全，支持多层 hook 共存。
class WndProcHook {
public:
    WndProcHook(HWND                              hwnd,
                std::function<void(int w, int h)> onResize,
                std::function<void()>             onDestroy);
    ~WndProcHook();  // 自动 Unhook

    WndProcHook(const WndProcHook&)            = delete;
    WndProcHook& operator=(const WndProcHook&) = delete;

private:
    static LRESULT CALLBACK SubclassProc(
        HWND hwnd, UINT msg, WPARAM w, LPARAM l,
        UINT_PTR subclassId, DWORD_PTR data);

    HWND                              m_hwnd;
    bool                              m_installed = false;
    std::function<void(int w, int h)> m_onResize;
    std::function<void()>             m_onDestroy;

    static constexpr UINT_PTR kSubclassId = 0xC0DE1234;
};
