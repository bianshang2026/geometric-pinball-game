#include "WndProcHook.h"

WndProcHook::WndProcHook(HWND hwnd,
                         std::function<void(int w, int h)> onResize,
                         std::function<void()> onDestroy)
    : m_hwnd(hwnd)
    , m_onResize(std::move(onResize))
    , m_onDestroy(std::move(onDestroy))
{
    if (!hwnd || !IsWindow(hwnd)) return;

    // data 指向 this，SubclassProc 通过 data 拿回实例指针
    m_installed = SetWindowSubclass(hwnd, SubclassProc, kSubclassId,
                                    reinterpret_cast<DWORD_PTR>(this));
}

WndProcHook::~WndProcHook()
{
    if (m_installed && m_hwnd && IsWindow(m_hwnd))
        RemoveWindowSubclass(m_hwnd, SubclassProc, kSubclassId);
}

LRESULT CALLBACK WndProcHook::SubclassProc(
    HWND hwnd, UINT msg, WPARAM w, LPARAM l,
    UINT_PTR subclassId, DWORD_PTR data)
{
    auto* self = reinterpret_cast<WndProcHook*>(data);

    switch (msg)
    {
    case WM_SIZE:
    {
        int width  = LOWORD(l);
        int height = HIWORD(l);
        if (self && self->m_onResize && (width > 0 || height > 0))
            self->m_onResize(width, height);
        break;
    }
    case WM_DESTROY:
        // 先通知上层，再自动卸载 hook（窗口即将销毁，不需要再保留）
        if (self)
        {
            self->m_installed = false; // 防止析构函数重复 Remove
            if (self->m_onDestroy)
                self->m_onDestroy();
        }
        RemoveWindowSubclass(hwnd, SubclassProc, subclassId);
        break;
    }

    return DefSubclassProc(hwnd, msg, w, l);
}
