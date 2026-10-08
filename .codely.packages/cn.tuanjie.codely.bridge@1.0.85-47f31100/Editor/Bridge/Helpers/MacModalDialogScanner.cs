#if UNITY_EDITOR_OSX
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// macOS implementation of the modal dialog scanner, built on the Objective-C runtime via
    /// P/Invoke (no native plugin required).
    ///
    /// AppKit is main-thread-only, so unlike the Windows scanner everything here MUST run on
    /// the main thread. That is still workable while a modal dialog holds the main thread
    /// captive because of how modality works on macOS: EditorUtility.DisplayDialog runs an
    /// NSAlert via runModal, which keeps the main run loop spinning in NSModalPanelRunLoopMode.
    /// That mode is one of the run loop's "common modes", so a CFRunLoopTimer registered with
    /// kCFRunLoopCommonModes keeps firing on the main thread inside the modal session — while
    /// Unity's own update loop (and the bridge command loop) is parked. DialogWatcher
    /// drives scans and clicks from that timer (<see cref="StartMainThreadProbe"/>) instead of
    /// from a background thread.
    ///
    /// Scanning reads NSApp.modalWindow and walks its view tree: NSTextField values become the
    /// dialog text, push-button NSButtons become the clickable options. DisplayDialog's "title"
    /// argument surfaces as the alert's bold messageText (the NSPanel itself usually has no
    /// window title), so when the window title is empty the first text field is promoted to
    /// <see cref="ModalDialogInfo.Title"/> to keep rule semantics aligned with Windows.
    ///
    /// Clicking uses -[NSButton performClick:], which runs the button action exactly like a
    /// user click and ends the modal session.
    ///
    /// Limitations: only the active modal window is visible (window-attached sheets don't block
    /// the main thread and are out of scope), and the probe only fires while the main thread is
    /// actually running a run loop — a main thread hung outside one (e.g. a long synchronous
    /// import) cannot be probed from managed code at all.
    /// </summary>
    public static class MacModalDialogScanner
    {
        private const string ObjCLib = "/usr/lib/libobjc.dylib";
        private const string CFLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string SystemLib = "/usr/lib/libSystem.dylib";
        private const int RTLD_LAZY = 1;

        // NSButtonType values we must not auto-click (suppression checkboxes, radios).
        private const long NSButtonTypeSwitch = 3;
        private const long NSButtonTypeRadio = 4;

        private const int MaxViewDepth = 12;

        // ---- Objective-C runtime ------------------------------------------ //

        [DllImport(ObjCLib)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(ObjCLib)]
        private static extern IntPtr sel_registerName(string name);

        // One typed declaration per call shape; on arm64 the x64 "cast objc_msgSend" shortcut
        // does not exist, so explicit signatures are required.
        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendPtr(IntPtr receiver, IntPtr sel);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendPtr(IntPtr receiver, IntPtr sel, IntPtr arg);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        private static extern void SendVoid(IntPtr receiver, IntPtr sel, IntPtr arg);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendBool(IntPtr receiver, IntPtr sel);

        [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendBool(IntPtr receiver, IntPtr sel, IntPtr arg);

        private static readonly IntPtr s_clsNSApplication = objc_getClass("NSApplication");
        private static readonly IntPtr s_clsNSButton = objc_getClass("NSButton");
        private static readonly IntPtr s_clsNSTextField = objc_getClass("NSTextField");

        private static readonly IntPtr s_selSharedApplication = sel_registerName("sharedApplication");
        private static readonly IntPtr s_selModalWindow = sel_registerName("modalWindow");
        private static readonly IntPtr s_selTitle = sel_registerName("title");
        private static readonly IntPtr s_selContentView = sel_registerName("contentView");
        private static readonly IntPtr s_selSubviews = sel_registerName("subviews");
        private static readonly IntPtr s_selCount = sel_registerName("count");
        private static readonly IntPtr s_selObjectAtIndex = sel_registerName("objectAtIndex:");
        private static readonly IntPtr s_selIsKindOfClass = sel_registerName("isKindOfClass:");
        private static readonly IntPtr s_selRespondsToSelector = sel_registerName("respondsToSelector:");
        private static readonly IntPtr s_selUTF8String = sel_registerName("UTF8String");
        private static readonly IntPtr s_selStringValue = sel_registerName("stringValue");
        private static readonly IntPtr s_selIsHidden = sel_registerName("isHidden");
        private static readonly IntPtr s_selIsEnabled = sel_registerName("isEnabled");
        private static readonly IntPtr s_selButtonType = sel_registerName("buttonType");
        private static readonly IntPtr s_selPerformClick = sel_registerName("performClick:");

        // ---- Scanning (main thread only) ---------------------------------- //

        /// <summary>Returns the active modal dialog, if any. Main thread only.</summary>
        public static List<ModalDialogInfo> Scan()
        {
            var dialogs = new List<ModalDialogInfo>();

            IntPtr app = SendPtr(s_clsNSApplication, s_selSharedApplication);
            if (app == IntPtr.Zero) return dialogs;

            IntPtr window = SendPtr(app, s_selModalWindow);
            if (window == IntPtr.Zero) return dialogs;

            var info = new ModalDialogInfo
            {
                Handle = window,
                Title = GetNSString(SendPtr(window, s_selTitle)).Trim(),
            };

            var texts = new List<string>();
            CollectControls(SendPtr(window, s_selContentView), info.Buttons, texts, 0);

            // An NSAlert panel has no window title; DisplayDialog's title argument is the bold
            // messageText, which is the first text field. Promote it so rules written against
            // the Windows (title, message) shape keep matching.
            if (info.Title.Length == 0 && texts.Count > 0)
            {
                info.Title = texts[0];
                texts.RemoveAt(0);
            }
            info.Message = string.Join("\n", texts);

            dialogs.Add(info);
            return dialogs;
        }

        private static void CollectControls(IntPtr view, List<ModalButtonInfo> buttons,
            List<string> texts, int depth)
        {
            if (view == IntPtr.Zero || depth > MaxViewDepth) return;
            if (SendBool(view, s_selIsHidden)) return;

            if (SendBool(view, s_selIsKindOfClass, s_clsNSButton))
            {
                if (!SendBool(view, s_selIsEnabled)) return;
                if (IsToggleButton(view)) return;
                string label = ModalDialogScanner.NormalizeButtonLabel(GetNSString(SendPtr(view, s_selTitle)));
                if (label.Length == 0) return;
                buttons.Add(new ModalButtonInfo { Handle = view, ControlId = 0, Label = label });
                return;
            }

            if (SendBool(view, s_selIsKindOfClass, s_clsNSTextField))
            {
                string text = GetNSString(SendPtr(view, s_selStringValue)).Trim();
                if (text.Length > 0) texts.Add(text);
                return;
            }

            IntPtr subviews = SendPtr(view, s_selSubviews);
            if (subviews == IntPtr.Zero) return;
            long count = (long)SendPtr(subviews, s_selCount);
            for (long i = 0; i < count; i++)
                CollectControls(SendPtr(subviews, s_selObjectAtIndex, (IntPtr)i), buttons, texts, depth + 1);
        }

        /// <summary>
        /// Filters out checkboxes ("Do not show again") and radios, which share the NSButton
        /// class with push buttons. The buttonType getter only exists on macOS 10.14+; on older
        /// systems the button is kept, which is safe because rules match by label.
        /// </summary>
        private static bool IsToggleButton(IntPtr button)
        {
            if (!SendBool(button, s_selRespondsToSelector, s_selButtonType)) return false;
            long type = (long)SendPtr(button, s_selButtonType);
            return type == NSButtonTypeSwitch || type == NSButtonTypeRadio;
        }

        /// <summary>Presses a scanned button via performClick:. Main thread only.</summary>
        public static bool ClickButton(ModalDialogInfo dialog, ModalButtonInfo button)
        {
            if (dialog == null || button == null || button.Handle == IntPtr.Zero) return false;
            SendVoid(button.Handle, s_selPerformClick, IntPtr.Zero);
            return true;
        }

        private static string GetNSString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return string.Empty;
            IntPtr utf8 = SendPtr(nsString, s_selUTF8String);
            if (utf8 == IntPtr.Zero) return string.Empty;

            int length = 0;
            while (Marshal.ReadByte(utf8, length) != 0) length++;
            if (length == 0) return string.Empty;
            var bytes = new byte[length];
            Marshal.Copy(utf8, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        // ---- Common-modes run-loop probe ---------------------------------- //

        private delegate void CFRunLoopTimerCallBack(IntPtr timer, IntPtr info);

        [DllImport(CFLib)]
        private static extern IntPtr CFRunLoopGetMain();

        [DllImport(CFLib)]
        private static extern double CFAbsoluteTimeGetCurrent();

        [DllImport(CFLib)]
        private static extern IntPtr CFRunLoopTimerCreate(IntPtr allocator, double fireDate,
            double interval, uint flags, int order, CFRunLoopTimerCallBack callout, IntPtr context);

        [DllImport(CFLib)]
        private static extern void CFRunLoopAddTimer(IntPtr runLoop, IntPtr timer, IntPtr mode);

        [DllImport(CFLib)]
        private static extern void CFRunLoopTimerInvalidate(IntPtr timer);

        [DllImport(CFLib)]
        private static extern void CFRelease(IntPtr cf);

        [DllImport(SystemLib)]
        private static extern IntPtr dlopen(string path, int mode);

        [DllImport(SystemLib)]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        private static IntPtr s_timer;
        // Keeps the marshaled callback alive for the timer's lifetime: if this delegate were
        // collected, the next timer fire would call into freed memory and crash the editor.
        private static CFRunLoopTimerCallBack s_timerCallback;
        private static Action s_onTick;

        public static bool ProbeRunning => s_timer != IntPtr.Zero;

        /// <summary>
        /// Registers a repeating timer on the MAIN run loop in kCFRunLoopCommonModes, so
        /// <paramref name="onTick"/> keeps firing on the main thread even while runModal has
        /// the main thread captive. The caller MUST stop the probe before every domain reload
        /// (see <see cref="StopMainThreadProbe"/>) — a stale native callback into an unloaded
        /// domain is a hard editor crash.
        /// </summary>
        public static bool StartMainThreadProbe(Action onTick, double intervalSeconds)
        {
            if (ProbeRunning) return true;
            if (onTick == null) return false;

            IntPtr commonModes = GetCommonModes();
            if (commonModes == IntPtr.Zero) return false;

            s_onTick = onTick;
            s_timerCallback = OnTimerFired;
            s_timer = CFRunLoopTimerCreate(IntPtr.Zero,
                CFAbsoluteTimeGetCurrent() + intervalSeconds, intervalSeconds,
                0, 0, s_timerCallback, IntPtr.Zero);
            if (s_timer == IntPtr.Zero)
            {
                s_timerCallback = null;
                s_onTick = null;
                return false;
            }

            CFRunLoopAddTimer(CFRunLoopGetMain(), s_timer, commonModes);
            return true;
        }

        public static void StopMainThreadProbe()
        {
            if (s_timer == IntPtr.Zero) return;
            CFRunLoopTimerInvalidate(s_timer);
            CFRelease(s_timer);
            s_timer = IntPtr.Zero;
            s_timerCallback = null;
            s_onTick = null;
        }

        private static void OnTimerFired(IntPtr timer, IntPtr info)
        {
            // Exceptions must never escape into native run-loop code.
            try { s_onTick?.Invoke(); } catch { }
        }

        private static IntPtr GetCommonModes()
        {
            IntPtr handle = dlopen(CFLib, RTLD_LAZY);
            if (handle == IntPtr.Zero) return IntPtr.Zero;
            IntPtr symbol = dlsym(handle, "kCFRunLoopCommonModes");
            return symbol == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(symbol);
        }
    }
}
#endif
