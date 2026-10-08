using System;
using System.Collections.Concurrent;
using System.Threading;
using Codely.Newtonsoft.Json.Linq;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Pulls commands out of the native inbound queue on a dedicated fetch thread and sorts them
    /// into two queues: one drained by the editor main thread (Unity-API commands — the vast
    /// majority) and one executed immediately on a dedicated background worker thread (commands
    /// whose handling touches no Unity API, currently <c>manage_job</c>).
    ///
    /// Fetching off the main thread takes the native dequeue out of the frame budget; the
    /// background executor is what lets a client query, detach, or cancel a job even while the
    /// main thread is busy running it — the whole point of <see cref="JobControl"/> and
    /// <see cref="JobRegistry"/>.
    ///
    /// The pump owns the only callers of <c>NTB_TryDequeueCommand</c> while it runs (the shared
    /// inbound buffer in NativeUnityTcpBridgeHost is not thread-safe); the bridge's direct-poll
    /// path is only a fallback for when the pump could not start. Both paths go through
    /// <see cref="GatedTryDequeue"/>, which serializes every native dequeue — the one hole in
    /// the single-owner story is a fetch thread abandoned by a timed-out Stop join that is
    /// still stuck inside the native call, and the gate is what keeps the next generation (or
    /// the fallback) from dequeuing concurrently with it. Threads die with the domain,
    /// so the bridge stops the pump on beforeAssemblyReload and on Stop, and starts it again on
    /// the next Start. Anything still queued when the pump stops is answered with an error so no
    /// native request is left hanging.
    /// </summary>
    public static class BackgroundCommandPump
    {
        public delegate bool TryDequeueDelegate(out ulong requestId, out string commandText);

        private sealed class Pumped
        {
            public ulong  RequestId;
            public string Text;
        }

        private const int FetchIdleSleepMs  = 5;
        private const int WorkerWaitMs      = 100;
        // Must outlast the longest legitimate block inside a background command — the macOS
        // click marshal wait (3 s) plus its grace wait (1 s) in DialogWatcher — so Stop does
        // not abandon a healthy worker mid-command and let it run into the domain unload.
        private const int StopJoinTimeoutMs = 5000;

        // One generation of pump threads. Each Start creates a fresh Control, and both loops
        // run against THAT instance's flag — never shared static state — so a thread abandoned
        // by a timed-out Join exits at its next flag check instead of being revived by a later
        // Start. Until that check it may still be INSIDE the native dequeue, which is why every
        // dequeue also goes through _dequeueGate below.
        private sealed class Control
        {
            public volatile bool Running = true;
        }

        // Serializes every caller of the native dequeue (the shared inbound buffer is not
        // thread-safe). A fetch thread abandoned by a timed-out Stop join may still be stuck
        // inside NTB_TryDequeueCommand; while it holds this gate, the next generation's fetch
        // thread and the bridge's direct-poll fallback skip dequeuing instead of racing it.
        private static readonly object _dequeueGate = new object();

        private static readonly ConcurrentQueue<Pumped> _mainQueue       = new ConcurrentQueue<Pumped>();
        private static readonly ConcurrentQueue<Pumped> _backgroundQueue = new ConcurrentQueue<Pumped>();
        private static readonly AutoResetEvent _backgroundSignal = new AutoResetEvent(false);

        private static Thread _fetchThread;
        private static Thread _workerThread;
        private static volatile Control _control;   // null ⇔ pump stopped

        private static TryDequeueDelegate    _tryDequeue;
        private static Action<ulong, string> _backgroundHandler;
        private static Action<ulong, string> _respondError;

        // While non-null, the editor main thread is known to be blocked (a modal dialog — see
        // DialogWatcher): commands destined for the main-thread queue are answered immediately
        // with this reason instead of being queued behind a frozen editor loop. Background
        // commands (manage_job / manage_dialog) keep flowing — they are the way out.
        private static volatile string _mainThreadBlockedReason;

        public static bool IsRunning => _control != null;

        public static bool IsMainThreadBlocked => _mainThreadBlockedReason != null;

        /// <summary>Fetched-but-not-yet-executed commands across both queues.</summary>
        public static int QueuedCount => _mainQueue.Count + _backgroundQueue.Count;

        /// <summary>
        /// Starts the fetch and worker threads. <paramref name="backgroundHandler"/> runs on the
        /// worker thread and must not touch Unity APIs; <paramref name="respondError"/> answers a
        /// command that was fetched but will never execute (pump stop, handler crash).
        /// </summary>
        public static void Start(
            TryDequeueDelegate tryDequeue,
            Action<ulong, string> backgroundHandler,
            Action<ulong, string> respondError)
        {
            if (_control != null) return;

            _tryDequeue        = tryDequeue        ?? throw new ArgumentNullException(nameof(tryDequeue));
            _backgroundHandler = backgroundHandler ?? throw new ArgumentNullException(nameof(backgroundHandler));
            _respondError      = respondError      ?? throw new ArgumentNullException(nameof(respondError));

            var control = new Control();
            var fetch  = new Thread(() => FetchLoop(control))  { Name = "Codely.BackgroundCommandPump.Fetch",  IsBackground = true };
            var worker = new Thread(() => WorkerLoop(control)) { Name = "Codely.BackgroundCommandPump.Worker", IsBackground = true };
            try
            {
                fetch.Start();
                worker.Start();
            }
            catch (Exception ex)
            {
                // Roll back so IsRunning stays false and the bridge's direct-poll fallback
                // engages. A fetch thread that did start must be stopped first — it must not
                // keep dequeuing native alongside the main thread's direct polling.
                control.Running = false;
                _backgroundSignal.Set();
                Join(fetch);
                Join(worker);
                CodelyLogger.LogWarning(
                    $"[BackgroundCommandPump] Failed to start pump threads: {ex.Message}; falling back to direct polling.");
                return;
            }
            _fetchThread  = fetch;
            _workerThread = worker;
            _control = control;
        }

        /// <summary>
        /// Stops both threads, then fails every command still queued with <paramref name="reason"/>
        /// so its native request is not left hanging. Main thread only (joins the threads). Called
        /// before the native server stops — the error responses still need the listener.
        /// </summary>
        public static void Stop(string reason) => StopCore(reason, respond: true);

        public static void StopForReload() => StopCore(null, respond: false);

        private static void StopCore(string reason, bool respond)
        {
            var control = _control;
            if (control != null)
            {
                _control = null;
                control.Running = false;
                _backgroundSignal.Set();
                Join(_fetchThread);
                Join(_workerThread);
                _fetchThread  = null;
                _workerThread = null;
            }
            _mainThreadBlockedReason = null;
            if (respond)
                DrainWithError(reason);
            else
                ClearQueues();
        }

        /// <summary>
        /// Marks the main thread as blocked (non-null <paramref name="reason"/>) or unblocked
        /// (null). While blocked, every command destined for the main-thread queue — including
        /// those already waiting in it — is answered immediately with the reason instead of
        /// sitting behind a frozen editor loop; the reason tells the client how to unblock
        /// (click the dialog via manage_dialog) and to retry. Background-eligible commands are
        /// unaffected. Callable from any thread — the <see cref="DialogWatcher"/> probe drives
        /// it once per poll while a dialog is up, so commands that race the block are swept on
        /// the next poll.
        /// </summary>
        public static void SetMainThreadBlocked(string reason)
        {
            _mainThreadBlockedReason = reason;
            if (reason == null) return;

            while (_mainQueue.TryDequeue(out Pumped cmd))
            {
                try { _respondError?.Invoke(cmd.RequestId, reason); }
                catch { /* best effort — never throw into the watcher */ }
            }
        }

        /// <summary>
        /// Runs <paramref name="tryDequeue"/> under the shared native-dequeue gate; the only
        /// legitimate way to call the native dequeue. Returns false without dequeuing when the
        /// gate is held — an abandoned fetch thread still stuck inside the native call — so the
        /// caller just retries later instead of stacking up behind a hung native call.
        /// </summary>
        public static bool GatedTryDequeue(TryDequeueDelegate tryDequeue, out ulong requestId, out string commandText)
        {
            requestId   = 0;
            commandText = null;
            if (tryDequeue == null || !Monitor.TryEnter(_dequeueGate)) return false;
            try { return tryDequeue(out requestId, out commandText); }
            finally { Monitor.Exit(_dequeueGate); }
        }

        /// <summary>Main-thread drain: one queued main-thread command per call, FIFO.</summary>
        public static bool TryDequeueForMainThread(out ulong requestId, out string commandText)
        {
            if (_mainQueue.TryDequeue(out var cmd))
            {
                requestId   = cmd.RequestId;
                commandText = cmd.Text;
                return true;
            }
            requestId   = 0;
            commandText = null;
            return false;
        }

        // ---- routing ---------------------------------------------------- //

        // Command types whose whole surface is safe off the main thread: manage_job touches
        // only thread-safe job-control state, and manage_dialog (click) reaches a dialog that
        // is blocking the main thread — running it there is the point.
        private static readonly string[] BackgroundTypes = { "manage_job", "manage_dialog" };

        /// <summary>
        /// True when the command may run on the background worker instead of the main thread
        /// (see <see cref="BackgroundTypes"/>). Anything malformed routes to the main thread,
        /// which produces the proper error response.
        /// </summary>
        internal static bool RunsInBackground(string commandText)
        {
            if (string.IsNullOrEmpty(commandText)) return false;

            // Cheap containment pre-check: skips the JSON parse for the overwhelmingly common
            // case of a main-thread command.
            bool hasMarker = false;
            for (int i = 0; i < BackgroundTypes.Length && !hasMarker; i++)
                hasMarker = commandText.IndexOf(BackgroundTypes[i], StringComparison.Ordinal) >= 0;
            if (!hasMarker) return false;

            try
            {
                string type = JObject.Parse(commandText)["type"]?.ToString();
                foreach (string background in BackgroundTypes)
                {
                    if (string.Equals(type, background, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // ---- threads ---------------------------------------------------- //

        private static void FetchLoop(Control control)
        {
            while (control.Running)
            {
                bool got;
                ulong requestId = 0;
                string text = null;
                try { got = GatedTryDequeue(_tryDequeue, out requestId, out text); }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"[BackgroundCommandPump] Dequeue failed: {ex.Message}");
                    got = false;
                }

                if (!got)
                {
                    Thread.Sleep(FetchIdleSleepMs);
                    continue;
                }

                var cmd = new Pumped { RequestId = requestId, Text = text };
                if (RunsInBackground(text))
                {
                    _backgroundQueue.Enqueue(cmd);
                    _backgroundSignal.Set();
                }
                else
                {
                    // A blocked main thread will never drain its queue — answer right away with
                    // the how-to-unblock reason instead of letting the request hang.
                    string blocked = _mainThreadBlockedReason;
                    if (blocked != null)
                    {
                        try { _respondError(cmd.RequestId, blocked); }
                        catch { /* best effort */ }
                    }
                    else
                    {
                        _mainQueue.Enqueue(cmd);
                    }
                }
            }
        }

        private static void WorkerLoop(Control control)
        {
            while (control.Running)
            {
                _backgroundSignal.WaitOne(WorkerWaitMs);

                // An item already handed to the handler finishes, but stop prevents the worker
                // from starting another queued item. Ordinary stop answers it; reload stop leaves
                // native to restore its lease.
                while (control.Running && _backgroundQueue.TryDequeue(out var cmd))
                {
                    try { _backgroundHandler(cmd.RequestId, cmd.Text); }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogWarning($"[BackgroundCommandPump] Background command failed: {ex.Message}");
                        try { _respondError(cmd.RequestId, $"Background command failed: {ex.Message}"); }
                        catch { /* respondError must not kill the worker */ }
                    }
                }
            }
        }

        private static void Join(Thread thread)
        {
            if (thread == null) return;
            try
            {
                if (!thread.Join(StopJoinTimeoutMs))
                    CodelyLogger.LogWarning(
                        $"[BackgroundCommandPump] '{thread.Name}' did not stop within {StopJoinTimeoutMs} ms; abandoning it.");
            }
            catch { /* joining a dead thread is fine */ }
        }

        private static void DrainWithError(string reason)
        {
            if (_respondError == null) return;
            while (_mainQueue.TryDequeue(out Pumped cmd) || _backgroundQueue.TryDequeue(out cmd))
            {
                try { _respondError(cmd.RequestId, reason); }
                catch { /* best effort — the listener may already be gone */ }
            }
        }

        private static void ClearQueues()
        {
            while (_mainQueue.TryDequeue(out _)) { }
            while (_backgroundQueue.TryDequeue(out _)) { }
        }
    }
}
