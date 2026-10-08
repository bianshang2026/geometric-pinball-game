using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Watches for native modal dialogs that block the editor main thread (e.g. the Input
    /// System "enable backends and restart" prompt). Nothing is ever clicked automatically —
    /// closing a dialog is always the client's (AI agent's) decision, made through
    /// <c>manage_dialog.click</c>.
    ///
    /// When a dialog appears the watcher does two things, both from a context that keeps
    /// running while the main thread is captive inside the dialog's message pump:
    ///   1. Detaches every in-flight runner job (<see cref="JobControl.DetachAll"/>) with a
    ///      reason that names the dialog, its message and its buttons, and instructs the client
    ///      to pick a button via <c>manage_dialog.click</c> and collect results later via
    ///      <c>manage_job.status</c> — so no caller is left hanging on a blocked request.
    ///   2. Broadcasts a "modal_dialog" notification with the same description to every
    ///      connected client.
    ///
    /// Why not the main thread: while a modal dialog is up, EditorApplication.update — and with
    /// it the bridge's whole main-thread command loop — never runs. The probe differs by
    /// platform (see <see cref="ModalDialogScanner"/>): a background thread on Windows (Win32
    /// reads/clicks are thread-safe), or the common-modes run-loop timer on macOS (AppKit is
    /// main-thread-only, and the timer keeps firing inside a modal session). The macOS probe
    /// also services click requests marshalled from the BackgroundCommandPump worker.
    ///
    /// Known limitation: dialogs raised while managed code cannot run at all (mid domain
    /// reload / import with no InitializeOnLoad yet) are only picked up once the reload
    /// finishes and the watcher restarts.
    /// </summary>
    [InitializeOnLoad]
    public static class DialogWatcher
    {
        private const string NotifyEventType = "modal_dialog";
        private const int PollIntervalMs = 300;

        // Main-thread liveness heartbeat, stamped by EditorApplication.update. A scanned dialog
        // only counts as blocking once this has gone stale: a visible in-process dialog can be
        // modeless (a plugin tool window — Win32 has no airtight modal test for an ownerless
        // "#32770"), and escalating one would wrongly detach every job and reject every
        // main-thread command for as long as it stays open. The update loop stopping is the
        // ground truth — every main-thread modal pump stops it, a modeless dialog does not.
        private static volatile int s_lastMainThreadTick;
        // Must exceed the editor's slowest legitimate update cadence (an unfocused editor
        // throttles its loop aggressively) so an idle-but-healthy main thread is never taken
        // for a blocked one. The price is only escalation latency on a real modal dialog.
        private const int MainThreadStalledMs = 2000;

        // Dialog titles the watcher must ignore outright: transient editor-owned popups that
        // resolve on their own and must not detach jobs or ping the client. Matched
        // case-insensitively against the trimmed title. Tuanjie ships localized, so each
        // popup needs its Chinese title alongside the English one.
        private static readonly string[] IgnoredTitles =
        {
            "Compiling Scripts",
            "正在编译脚本",
            "编译脚本",
        };

        public static bool IsSupported => ModalDialogScanner.IsSupported;

#if UNITY_EDITOR_OSX
        private static readonly Dictionary<IntPtr, string> s_macReported =
            new Dictionary<IntPtr, string>();
        private static bool s_macErrorLogged;

        // Click requests marshalled from other threads (the BackgroundCommandPump worker) onto
        // the run-loop probe — AppKit is main-thread-only, and during a modal session the probe
        // is the only main-thread context still running. See ClickOpenDialog.
        //
        // Ownership handshake: State moves 0 → 1 when the probe claims the request (click in
        // progress) or 0 → 2 when the waiter withdraws it after timing out. A withdrawn request
        // is never clicked — the waiter already reported failure, and a stale click landing
        // later (possibly after a client retry) would press a button twice. Whoever concludes
        // the handshake disposes Done: the waiter on the completed path, the probe on the
        // withdrawn path. (A grace-timeout — probe claimed but never signaled in time — leaves
        // Done to the GC: safe, since a never-waited-via-WaitHandle MRES holds no kernel handle.)
        private sealed class PendingClick
        {
            public const int StateWaiting   = 0;
            public const int StateClaimed   = 1;
            public const int StateWithdrawn = 2;

            public int State;
            public string TitlePattern;
            public string ButtonPattern;
            public string Failure;
            public string ClickedTitle;
            public string ClickedButton;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static readonly ConcurrentQueue<PendingClick> s_pendingClicks =
            new ConcurrentQueue<PendingClick>();
        private const int ClickMarshalTimeoutMs = 3000;
        // Extra wait when the probe claims the request just as the marshal timeout expires —
        // the click is being performed, so report its real outcome instead of a false failure.
        private const int ClickGraceTimeoutMs = 1000;

        public static bool WatcherRunning => MacModalDialogScanner.ProbeRunning;
#else
        private static Thread s_thread;
        private static volatile bool s_stop;

        public static bool WatcherRunning => s_thread != null && s_thread.IsAlive;
#endif

        static DialogWatcher()
        {
            if (EditorAutomationGuard.ShouldSkipBridgeAutoStart()) return;

            // Force main-thread-only static init NOW, on this ([InitializeOnLoad]) thread,
            // before any watcher thread exists: the Windows watcher thread reads
            // UnityTcpBridge.IsRunning in Notify, and if that were the bridge's first touch its
            // cctor would record the watcher thread as "the main thread" and start the server
            // off-thread. CaptureMainThread gives the Win32 scanner the main thread id its
            // owning-thread test needs.
            ModalDialogScanner.CaptureMainThread();
            _ = UnityTcpBridge.IsRunning;

            if (!IsSupported || UnityEngine.Application.isBatchMode) return;

            s_lastMainThreadTick = Environment.TickCount;
            EditorApplication.update -= StampMainThreadHeartbeat;
            EditorApplication.update += StampMainThreadHeartbeat;
            AssemblyReloadEvents.beforeAssemblyReload += StopWatcher;
            EditorApplication.quitting += StopWatcher;
            StartWatcher();
        }

        // ---- Watcher lifecycle ------------------------------------------- //

#if UNITY_EDITOR_OSX
        private static void StartWatcher()
        {
            if (WatcherRunning) return;
            if (MacModalDialogScanner.StartMainThreadProbe(MacTick, PollIntervalMs / 1000.0))
                CodelyLogger.Verbose("[DialogWatcher] run-loop probe started");
            else
                CodelyLogger.LogWarning("[DialogWatcher] failed to start run-loop probe");
        }

        // The probe MUST stop before every domain reload: its native timer holds a callback
        // into this domain's code.
        private static void StopWatcher() => MacModalDialogScanner.StopMainThreadProbe();

        private static void MacTick()
        {
            try
            {
                DrainPendingClicks();
                Tick(s_macReported);
                s_macErrorLogged = false;
            }
            catch (Exception e)
            {
                if (!s_macErrorLogged)
                {
                    s_macErrorLogged = true;
                    CodelyLogger.LogWarning($"[DialogWatcher] probe tick failed: {e.Message}");
                }
            }
        }
#else
        private static void StartWatcher()
        {
            if (WatcherRunning) return;
            s_stop = false;
            s_thread = new Thread(WatchLoop)
            {
                Name = "CodelyDialogWatcher",
                IsBackground = true,
            };
            s_thread.Start();
            CodelyLogger.Verbose("[DialogWatcher] watcher started");
        }

        private static void StopWatcher()
        {
            s_stop = true;
            var thread = s_thread;
            s_thread = null;
            if (thread != null && thread.IsAlive)
            {
                // Must outlast one legitimate WatchLoop iteration: a single tick performs two
                // SendMessageTimeout text reads (200 ms budget each) per control, so a busy
                // dialog can hold the loop well past a second — abandoning the thread then
                // would let it run on into the domain unload.
                try { thread.Join(3000); } catch { }
            }
        }

        private static void WatchLoop()
        {
            var reported = new Dictionary<IntPtr, string>();
            bool errorLogged = false;

            while (!s_stop)
            {
                try
                {
                    Tick(reported);
                    errorLogged = false;
                }
                catch (Exception e)
                {
                    // Log the first failure of a streak, then stay quiet so a persistent
                    // Win32 hiccup cannot flood the console from a background thread.
                    if (!errorLogged)
                    {
                        errorLogged = true;
                        CodelyLogger.LogWarning($"[DialogWatcher] watcher tick failed: {e.Message}");
                    }
                }

                Thread.Sleep(PollIntervalMs);
            }
        }
#endif

        // ---- Detection ---------------------------------------------------- //

        private static void StampMainThreadHeartbeat()
            => s_lastMainThreadTick = Environment.TickCount;

        // Unchecked subtraction stays correct across TickCount wraparound (~24.9 days).
        private static bool IsMainThreadStalled()
            => unchecked(Environment.TickCount - s_lastMainThreadTick) > MainThreadStalledMs;

        // `reported` maps dialog handle → title, so each dialog is reported (and jobs detached)
        // exactly once — a reused handle showing a different title counts as a new dialog.
        private static void Tick(Dictionary<IntPtr, string> reported)
        {
            List<ModalDialogInfo> dialogs = ModalDialogScanner.Scan();

            // Forget dialogs that have closed so a reused handle is treated as a new dialog.
            var open = new HashSet<IntPtr>(dialogs.Select(d => d.Handle));
            foreach (var stale in reported.Keys.Where(h => !open.Contains(h)).ToList())
                reported.Remove(stale);

            // Blocked-main-thread signal for the command pump: while an actionable, non-ignored
            // dialog is up AND the editor update loop has actually stalled (see the heartbeat —
            // the scan alone cannot rule out a modeless dialog), every command waiting for (or
            // arriving at) the main-thread queue is answered immediately with instructions to
            // click the dialog and retry — instead of sitting behind a frozen editor loop.
            // Re-asserted every poll so commands that race the block are swept on the next one;
            // cleared as soon as no such dialog remains or the heartbeat resumes.
            bool stalled = IsMainThreadStalled();
            var blocking = stalled
                ? dialogs.FirstOrDefault(d => IsActionable(d) && !IsIgnoredTitle(d.Title))
                : null;
            BackgroundCommandPump.SetMainThreadBlocked(
                blocking == null ? null : BuildBlockedCommandReason(blocking));

            // Nothing to escalate while the main thread is alive: the dialogs stay unreported,
            // so a scanned-before-it-blocks dialog is picked up the moment the heartbeat stops.
            if (!stalled) return;

            foreach (var dialog in dialogs)
            {
                // A dialog with no clickable buttons — a progress/busy popup, or one scanned
                // before its buttons were constructed — is not actionable: there is nothing
                // manage_dialog.click could press, so detaching jobs and telling the client to
                // click would be a dead end. Skip WITHOUT marking it reported, so it is
                // reconsidered every poll and picked up the moment buttons appear.
                if (!IsActionable(dialog))
                    continue;

                // Known transient editor popups (e.g. "Compiling Scripts") close on their own —
                // detaching jobs and asking the client to click would only cause churn.
                if (IsIgnoredTitle(dialog.Title))
                    continue;

                if (reported.TryGetValue(dialog.Handle, out string title) && title == dialog.Title)
                    continue;
                reported[dialog.Handle] = dialog.Title;
                Notify(dialog);
                DetachAllForDialog(dialog);
            }
        }

        /// <summary>True when the dialog has at least one button a client could click.</summary>
        internal static bool IsActionable(ModalDialogInfo dialog)
            => dialog?.Buttons != null && dialog.Buttons.Count > 0;

        /// <summary>True when the title is on the <see cref="IgnoredTitles"/> list.</summary>
        internal static bool IsIgnoredTitle(string title)
        {
            string trimmed = title?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return false;

            foreach (string ignored in IgnoredTitles)
            {
                if (string.Equals(trimmed, ignored, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void Notify(ModalDialogInfo dialog)
        {
            var payload = ModalDialogScanner.Describe(dialog);
            payload["event"] = "detected";
            payload["timestamp"] = DateTime.UtcNow.ToString("o");

            try
            {
                if (UnityTcpBridge.IsRunning)
                    UnityTcpBridge.NotifyAll(NotifyEventType, payload);
            }
            catch (Exception e)
            {
                CodelyLogger.Verbose($"[DialogWatcher] notify failed: {e.Message}");
            }
        }

        // Shared first half of the client-facing texts: what is blocking, and what can be clicked.
        private static string DescribeBlockingDialog(ModalDialogInfo dialog)
        {
            string message = dialog.Message ?? string.Empty;
            if (message.Length > 300) message = message.Substring(0, 300) + "…";
            string buttons = string.Join(", ", dialog.Buttons.Select(b => $"'{b.Label}'"));

            return $"A modal dialog is blocking the Unity editor main thread. " +
                   $"Title: '{dialog.Title}'. Message: '{message}'. Buttons: [{buttons}]. ";
        }

        private const string ClickInstruction =
            "Use the manage_dialog tool (action 'click', param 'button' = a regex over the button labels, " +
            "optional 'title' = a regex over the dialog title) to choose and click one of the buttons";

        // Builds the reason handed to every job detached because a modal dialog holds the main
        // thread: it names the dialog and its buttons, and tells the client exactly how to
        // unblock (click a button via manage_dialog) and how to collect the job's result later.
        internal static string BuildBlockingReason(ModalDialogInfo dialog)
            => DescribeBlockingDialog(dialog) +
               "This job keeps running but cannot progress until the dialog is closed. " +
               ClickInstruction +
               ", then poll manage_job (action 'status') with this job id to collect the result.";

        // Builds the answer for a main-thread command that cannot execute while the dialog is up
        // (see BackgroundCommandPump.SetMainThreadBlocked): unlike a detached job, the command
        // did NOT run — the client must close the dialog and retry it.
        internal static string BuildBlockedCommandReason(ModalDialogInfo dialog)
            => DescribeBlockingDialog(dialog) +
               "This command was NOT executed because the editor main thread cannot process commands while the dialog is open. " +
               ClickInstruction +
               " to close the dialog, then retry this command.";

        private static void DetachAllForDialog(ModalDialogInfo dialog)
        {
            try
            {
                string reason = BuildBlockingReason(dialog);
                CodelyLogger.Log($"[DialogWatcher] detaching jobs blocked by dialog '{dialog.Title}': {reason}");
                int detached = JobControl.DetachAll(reason);
                if (detached > 0)
                    CodelyLogger.Log(
                        $"[DialogWatcher] detached {detached} in-flight job(s) blocked by dialog '{dialog.Title}'");
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[DialogWatcher] detach-all failed: {e.Message}");
            }
        }

        // ---- Clicking (AI-triggered only) --------------------------------- //

        /// <summary>
        /// Clicks a button on an open dialog. <paramref name="titlePattern"/> is an optional
        /// regex filter over dialog titles; <paramref name="buttonPattern"/> is a regex over
        /// button labels. Returns a human-readable failure reason, or null on success with the
        /// clicked dialog/button in the out parameters.
        ///
        /// Callable from any thread — the manage_dialog command runs on the
        /// BackgroundCommandPump worker so it stays usable while a modal dialog holds the main
        /// thread. On Windows the Win32 scan/click is inherently cross-thread; on macOS the
        /// request is marshalled onto the run-loop probe, the one main-thread context that keeps
        /// running during a modal session.
        /// </summary>
        public static string ClickOpenDialog(string titlePattern, string buttonPattern,
            out string clickedTitle, out string clickedButton)
        {
            clickedTitle = null;
            clickedButton = null;

            if (!IsSupported) return "Modal dialog automation is only supported on the Windows and macOS editors.";
            if (string.IsNullOrEmpty(buttonPattern)) return "'button' parameter is required.";

#if UNITY_EDITOR_OSX
            // AppKit is main-thread-only. On the main thread (fallback dispatch path) click
            // directly; from any other thread hand the request to the probe and wait.
            if (MainThreadHelper.IsMainThread)
                return ClickCore(titlePattern, buttonPattern, out clickedTitle, out clickedButton);

            if (!WatcherRunning)
                return "Dialog probe is not running; cannot click from a background thread on macOS.";

            var request = new PendingClick { TitlePattern = titlePattern, ButtonPattern = buttonPattern };
            s_pendingClicks.Enqueue(request);
            if (!request.Done.Wait(ClickMarshalTimeoutMs))
            {
                // Withdraw the request so the probe cannot perform the click after this
                // failure has been reported (see PendingClick). If the withdrawal loses the
                // race — the probe claimed it just as the timeout expired — the click is in
                // progress: give it a grace period and report the real outcome.
                if (Interlocked.CompareExchange(ref request.State,
                        PendingClick.StateWithdrawn, PendingClick.StateWaiting)
                    == PendingClick.StateWaiting)
                    return "Timed out waiting for the main-thread probe to perform the click.";

                if (!request.Done.Wait(ClickGraceTimeoutMs))
                    return "Timed out waiting for the main-thread probe to perform the click.";
            }

            clickedTitle = request.ClickedTitle;
            clickedButton = request.ClickedButton;
            string failure = request.Failure;
            request.Done.Dispose();
            return failure;
#else
            return ClickCore(titlePattern, buttonPattern, out clickedTitle, out clickedButton);
#endif
        }

#if UNITY_EDITOR_OSX
        // Runs on the probe (main thread): performs every click other threads have queued.
        private static void DrainPendingClicks()
        {
            while (s_pendingClicks.TryDequeue(out var request))
            {
                // Claim before clicking (see PendingClick): a waiter that already timed out has
                // withdrawn the request and told its client the click failed — performing it
                // anyway would be a stale click nobody asked for.
                if (Interlocked.CompareExchange(ref request.State,
                        PendingClick.StateClaimed, PendingClick.StateWaiting)
                    != PendingClick.StateWaiting)
                {
                    request.Done.Dispose();
                    continue;
                }

                try
                {
                    request.Failure = ClickCore(request.TitlePattern, request.ButtonPattern,
                        out string title, out string button);
                    request.ClickedTitle = title;
                    request.ClickedButton = button;
                }
                catch (Exception e)
                {
                    request.Failure = $"Click failed: {e.Message}";
                }
                finally
                {
                    request.Done.Set();
                }
            }
        }
#endif

        private static string ClickCore(string titlePattern, string buttonPattern,
            out string clickedTitle, out string clickedButton)
        {
            clickedTitle = null;
            clickedButton = null;

            var dialogs = ModalDialogScanner.Scan();
            if (dialogs.Count == 0) return "No open modal dialogs found.";

            foreach (var dialog in dialogs)
            {
                if (!string.IsNullOrEmpty(titlePattern) && !SafeIsMatch(dialog.Title, titlePattern))
                    continue;

                var button = dialog.Buttons.FirstOrDefault(b => SafeIsMatch(b.Label, buttonPattern));
                if (button == null) continue;

                if (!ModalDialogScanner.ClickButton(dialog, button))
                    return $"Failed to post click to button '{button.Label}' on dialog '{dialog.Title}'.";

                clickedTitle = dialog.Title;
                clickedButton = button.Label;
                return null;
            }

            return "No open dialog matched the given title/button patterns.";
        }

        private static bool SafeIsMatch(string input, string pattern)
        {
            try
            {
                return Regex.IsMatch(input ?? string.Empty, pattern,
                    RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
            }
            catch { return false; }
        }
    }
}
