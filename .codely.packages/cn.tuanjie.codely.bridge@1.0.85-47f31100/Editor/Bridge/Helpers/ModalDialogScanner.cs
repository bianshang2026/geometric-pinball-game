using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Codely.Newtonsoft.Json.Linq;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>One clickable button on a native modal dialog.</summary>
    public sealed class ModalButtonInfo
    {
        /// <summary>Platform handle: HWND on Windows, NSButton pointer on macOS.</summary>
        public IntPtr Handle;
        /// <summary>Win32 dialog control id; 0 on macOS.</summary>
        public int ControlId;
        /// <summary>Normalized label (accelerator '&amp;' stripped, trimmed).</summary>
        public string Label;
    }

    /// <summary>A native modal dialog currently open in this editor process.</summary>
    public sealed class ModalDialogInfo
    {
        /// <summary>Platform handle: HWND on Windows, NSWindow pointer on macOS.</summary>
        public IntPtr Handle;
        public string Title;
        public string Message;
        public List<ModalButtonInfo> Buttons = new List<ModalButtonInfo>();
    }

    /// <summary>
    /// Platform facade over the native modal dialog scanners
    /// (<see cref="Win32ModalDialogScanner"/> / <see cref="MacModalDialogScanner"/>).
    ///
    /// Threading contract differs by platform and callers must respect it:
    ///   - Windows: Scan/ClickButton are callable from any thread (that is the whole point —
    ///     the background watcher reaches into the blocked main thread's dialog).
    ///   - macOS: AppKit is main-thread-only, so Scan/ClickButton must run on the main thread.
    ///     While a dialog is up that is only possible from the common-modes run-loop timer
    ///     (see MacModalDialogScanner.StartMainThreadProbe), which DialogWatcher uses as
    ///     its probe there instead of a background thread.
    /// </summary>
    public static class ModalDialogScanner
    {
        /// <summary>
        /// Strips the Win32 accelerator marker, trims, and collapses runs of whitespace:
        /// "&amp;Yes " → "Yes", "Enable &amp; Restart" → "Enable Restart". Whitespace collapsing
        /// matters because removing an '&amp;' between words would otherwise leave a double
        /// space that button regexes would have to know about.
        /// </summary>
        public static string NormalizeButtonLabel(string label)
        {
            string stripped = (label ?? string.Empty).Replace("&", string.Empty).Trim();
            return Regex.Replace(stripped, @"\s{2,}", " ");
        }

        public static JObject Describe(ModalDialogInfo dialog) => new JObject
        {
            ["title"] = dialog.Title,
            ["message"] = dialog.Message,
            ["buttons"] = new JArray(dialog.Buttons.Select(b => b.Label)),
            ["handle"] = dialog.Handle.ToInt64(),
        };

        public static JArray Describe(IEnumerable<ModalDialogInfo> dialogs)
            => new JArray(dialogs.Select(Describe));

        public static bool IsSupported =>
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            true;
#else
            false;
#endif

        /// <summary>
        /// Records the calling thread as the editor main thread, where the platform scanner
        /// needs it (Windows uses it to reject dialogs pumping on other threads — those do not
        /// block the main thread). Call once from the main thread before any background scan.
        /// </summary>
        public static void CaptureMainThread()
        {
#if UNITY_EDITOR_WIN
            Win32ModalDialogScanner.CaptureMainThread();
#endif
        }

        public static List<ModalDialogInfo> Scan()
        {
#if UNITY_EDITOR_WIN
            return Win32ModalDialogScanner.Scan();
#elif UNITY_EDITOR_OSX
            return MacModalDialogScanner.Scan();
#else
            return new List<ModalDialogInfo>();
#endif
        }

        public static bool ClickButton(ModalDialogInfo dialog, ModalButtonInfo button)
        {
#if UNITY_EDITOR_WIN
            return Win32ModalDialogScanner.ClickButton(dialog, button);
#elif UNITY_EDITOR_OSX
            return MacModalDialogScanner.ClickButton(dialog, button);
#else
            return false;
#endif
        }
    }
}
