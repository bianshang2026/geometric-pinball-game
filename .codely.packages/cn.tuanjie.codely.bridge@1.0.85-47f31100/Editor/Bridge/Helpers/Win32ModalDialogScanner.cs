#if UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Enumerates and clicks native modal dialogs (Win32 class "#32770") owned by this editor
    /// process. This covers <c>EditorUtility.DisplayDialog</c>/<c>DisplayDialogComplex</c>,
    /// MessageBox-style prompts from native editor code, and common dialogs — the popups that
    /// block the editor main thread inside a modal message pump.
    ///
    /// Every API here is safe to call from a background thread: while the main thread is stuck
    /// in the modal loop it still pumps window messages, which is exactly what lets another
    /// thread read control text and post a button click. All cross-thread text reads go through
    /// <c>SendMessageTimeout</c> so a genuinely hung (non-pumping) main thread — e.g. during a
    /// long import — can never wedge the caller.
    /// </summary>
    public static class Win32ModalDialogScanner
    {
        private const int WM_GETTEXT = 0x000D;
        private const int WM_GETTEXTLENGTH = 0x000E;
        private const int WM_COMMAND = 0x0111;
        private const int BM_CLICK = 0x00F5;
        private const int BN_CLICKED = 0;
        private const int GWL_STYLE = -16;
        private const long BS_TYPEMASK = 0x0000000FL;
        private const long BS_DEFPUSHBUTTON = 0x00000001L;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint TextReadTimeoutMs = 200;
        private const uint GW_OWNER = 4;

        // Win32 thread id of the editor main thread, captured on it at [InitializeOnLoad] time
        // (see DialogWatcher's static ctor → ModalDialogScanner.CaptureMainThread). 0 = never
        // captured (batch mode / tests) — the owning-thread test is then skipped.
        private static uint s_mainThreadId;

        /// <summary>Records the calling thread as the editor main thread. Main thread only.</summary>
        internal static void CaptureMainThread()
        {
            try { s_mainThreadId = GetCurrentThreadId(); } catch { s_mainThreadId = 0; }

            // Marshal both enum callbacks to native NOW, on the main thread. Mono converts a
            // delegate to a function pointer once per delegate INSTANCE and caches it on the
            // instance, so this is the only delegate→ftnptr conversion these two ever get —
            // every later EnumWindows/EnumChildWindows P/Invoke reuses the cached pointer.
            // Skipping this would leave the first conversion to the watcher thread's first
            // scan, the exact path that has crashed the editor (Mono assertion in
            // delegate_hash_table_add under mono_delegate_to_ftnptr).
            try
            {
                Marshal.GetFunctionPointerForDelegate(s_topLevelProc);
                Marshal.GetFunctionPointerForDelegate(s_childProc);
            }
            catch { }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // Long-lived delegates handed to EnumWindows/EnumChildWindows. These MUST be the only
        // EnumWindowsProc instances ever marshalled: passing a fresh closure per scan made Mono
        // register a new delegate→ftnptr mapping every 300 ms poll on the watcher thread, and
        // that runtime bookkeeping (a GC-coupled hash table) asserted and killed the editor
        // (delegate_hash_table_add). Per-scan state travels through lParam as a GCHandle
        // instead of closure captures, which also keeps concurrent scans (watcher thread +
        // BackgroundCommandPump worker) isolated.
        private static readonly EnumWindowsProc s_topLevelProc = TopLevelScanProc;
        private static readonly EnumWindowsProc s_childProc = ChildScanProc;

        private sealed class TopLevelScanState
        {
            public readonly List<ModalDialogInfo> Dialogs = new List<ModalDialogInfo>();
            public uint OwnPid;
        }

        private sealed class ChildScanState
        {
            public ModalDialogInfo Info;
            public List<string> MessageParts;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        /// <summary>
        /// Returns the visible "#32770" dialogs of this process that plausibly block the editor
        /// main thread. Being a visible in-process dialog is not enough — a modeless "#32770"
        /// (plugin tool window, another thread's MessageBox) blocks nothing, and reporting it
        /// would make the bridge reject every main-thread command for as long as it is open. So
        /// two modal signals are also required: the dialog must belong to the main UI thread
        /// (a dialog pumping on another thread leaves the main thread free), and when it has an
        /// owner window that owner must be disabled — the defining Win32 modal behavior; an
        /// enabled owner means modeless. Ownerless main-thread dialogs are kept: a thread-modal
        /// MessageBox with a null owner still blocks the main thread. That keep is deliberately
        /// loose — a modeless ownerless "#32770" passes it too, and Win32 offers no airtight
        /// modal test for one — so <see cref="DialogWatcher"/> only escalates a scanned dialog
        /// (detach jobs, reject main-thread commands) once the editor update loop has actually
        /// stalled; the scan itself just nominates candidates.
        /// </summary>
        public static List<ModalDialogInfo> Scan()
        {
            var state = new TopLevelScanState { OwnPid = GetCurrentProcessId() };
            var handle = GCHandle.Alloc(state);
            try { EnumWindows(s_topLevelProc, GCHandle.ToIntPtr(handle)); }
            finally { handle.Free(); }
            return state.Dialogs;
        }

        private static bool TopLevelScanProc(IntPtr hWnd, IntPtr lParam)
        {
            var state = (TopLevelScanState)GCHandle.FromIntPtr(lParam).Target;
            try
            {
                if (!IsWindowVisible(hWnd)) return true;
                uint tid = GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != state.OwnPid) return true;
                if (s_mainThreadId != 0 && tid != s_mainThreadId) return true;
                if (GetClassNameSafe(hWnd) != "#32770") return true;

                IntPtr owner = GetWindow(hWnd, GW_OWNER);
                if (owner != IntPtr.Zero && IsWindowEnabled(owner)) return true;

                state.Dialogs.Add(ReadDialog(hWnd));
            }
            catch
            {
                // A window can vanish mid-enumeration; skip it and keep scanning.
            }
            return true;
        }

        private static ModalDialogInfo ReadDialog(IntPtr hWnd)
        {
            var state = new ChildScanState
            {
                Info = new ModalDialogInfo
                {
                    Handle = hWnd,
                    Title = GetText(hWnd),
                },
                MessageParts = new List<string>(),
            };

            var handle = GCHandle.Alloc(state);
            try { EnumChildWindows(hWnd, s_childProc, GCHandle.ToIntPtr(handle)); }
            finally { handle.Free(); }

            state.Info.Message = string.Join("\n", state.MessageParts);
            return state.Info;
        }

        private static bool ChildScanProc(IntPtr hChild, IntPtr lParam)
        {
            var state = (ChildScanState)GCHandle.FromIntPtr(lParam).Target;
            try
            {
                if (!IsWindowVisible(hChild)) return true;
                string cls = GetClassNameSafe(hChild);
                switch (cls)
                {
                    case "Button":
                    {
                        long style = (long)GetWindowLongPtr(hChild, GWL_STYLE);
                        // Only push buttons; checkboxes/radios/groupboxes share the class.
                        if ((style & BS_TYPEMASK) > BS_DEFPUSHBUTTON) break;
                        if (!IsWindowEnabled(hChild)) break;
                        string label = ModalDialogScanner.NormalizeButtonLabel(GetText(hChild));
                        if (label.Length == 0) break;
                        state.Info.Buttons.Add(new ModalButtonInfo
                        {
                            Handle = hChild,
                            ControlId = GetDlgCtrlID(hChild),
                            Label = label,
                        });
                        break;
                    }
                    case "Static":
                    case "Edit":
                    case "SysLink":
                    {
                        string text = GetText(hChild).Trim();
                        if (text.Length > 0) state.MessageParts.Add(text);
                        break;
                    }
                }
            }
            catch
            {
                // Ignore controls that disappear while being read.
            }
            return true;
        }

        /// <summary>
        /// Clicks a dialog button by posting WM_COMMAND (falling back to BM_CLICK when the
        /// control has no id). Posting rather than sending keeps this thread from blocking on
        /// the dialog's message pump.
        /// </summary>
        public static bool ClickButton(ModalDialogInfo dialog, ModalButtonInfo button)
        {
            if (dialog == null || button == null) return false;
            if (!IsWindow(dialog.Handle) || !IsWindow(button.Handle)) return false;

            if (button.ControlId != 0)
            {
                IntPtr wParam = (IntPtr)((BN_CLICKED << 16) | (button.ControlId & 0xFFFF));
                return PostMessage(dialog.Handle, WM_COMMAND, wParam, button.Handle);
            }
            return PostMessage(button.Handle, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        }

        private static string GetClassNameSafe(IntPtr hWnd)
        {
            var sb = new StringBuilder(64);
            return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
        }

        private static string GetText(IntPtr hWnd)
        {
            SendMessageTimeout(hWnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero,
                SMTO_ABORTIFHUNG, TextReadTimeoutMs, out IntPtr lengthResult);
            int length = (int)lengthResult;
            if (length <= 0) return string.Empty;

            var sb = new StringBuilder(length + 1);
            SendMessageTimeout(hWnd, WM_GETTEXT, (IntPtr)sb.Capacity, sb,
                SMTO_ABORTIFHUNG, TextReadTimeoutMs, out _);
            return sb.ToString();
        }
    }
}
#endif
