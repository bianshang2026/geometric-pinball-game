using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Codely.Newtonsoft.Json;
using UnityEditor;
using UnityTcp.Editor.Native;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Drives <see cref="StepJob"/>s one step per editor frame, and — unlike
    /// <see cref="AsyncTaskRunner"/> and <see cref="CoroutineRunner"/>, whose Tasks and iterators
    /// die with the domain — carries its jobs across domain reloads. Before the reload each job is
    /// serialized to SessionState (subclass type, field state, current step index, deadline); after
    /// it, the job is rebuilt from that JSON and resumes at the step it was on. That is what lets a
    /// single request span a recompile: "start a compile and answer when it has finished".
    ///
    /// The native listener and its pending requests live in the native DLL, which is not reloaded,
    /// so a job's request id stays valid across the reload and the finished job answers on the
    /// original request — the caller makes one call and gets one result. Pass
    /// <c>detached: true</c> for fire-and-poll instead, where the caller answers immediately with
    /// <c>ctx.ToPendingResponse()</c> and the client collects the result by job id through
    /// <see cref="DetachedJobs"/>.
    ///
    /// Jobs compose: a step built with <see cref="JobStep.Nested"/> runs a child StepJob to
    /// completion before the parent continues. The runner keeps a stack of frames per job — one per
    /// nesting level — and persists the whole stack, so a child keeps its own progress across a
    /// reload too. Exactly one step, at the deepest active frame, runs per editor frame.
    ///
    /// Usage:
    ///   return StepJobRunner.Start(CommandContext.RequestId, CommandContext.CommandType,
    ///                              new WaitForCompileJob { TimeoutSeconds = 180 });
    /// Returning that <see cref="JobContext"/> from a HandleCommand tells the command loop to hold
    /// the response until the runner delivers it.
    ///
    /// Main-thread only; <see cref="Tick"/> is called once per editor frame from the top-level
    /// command loop.
    /// </summary>
    [InitializeOnLoad]
    public static class StepJobRunner
    {
        private const string SessionKeyJobs = "Codely_StepJobRunner_Jobs";

        /// <summary>
        /// Cap on nested sub-jobs, guarding against a factory that returns a job nesting itself.
        /// </summary>
        public const int MaxNestingDepth = 8;

        // One nesting level of a job: the StepJob at that level and where it is in its steps.
        // Frame 0 is the root job; a composite step pushes a frame and pops it when the child ends.
        private sealed class Frame
        {
            public string TypeName;      // StepJob subclass, AssemblyQualifiedName
            public string StateJson;     // serialized subclass fields, refreshed on save
            public int    StepIndex;
            public bool   SubJobStarted; // this frame's current step already pushed its child

            [JsonIgnore] public StepJob Job;
        }

        // The persisted shape of one in-flight job.
        private sealed class Entry
        {
            public string      JobId;
            public string      Name;
            public ulong       RequestId;   // native request to answer on; ignored when Detached
            public bool        Detached;
            // Single-delivery gate: 1 once the original request has been answered (job launched
            // detached, or a mid-flight detach ack went out). Interlocked — a detach may claim it
            // from a non-main thread while Finish answers on the main thread. Persisted so a
            // detach that already acked is not re-answered after a domain reload.
            public int         Answered;
            public double      Deadline;    // EditorApplication.timeSinceStartup; 0 = no timeout
            public List<Frame> Frames = new List<Frame>();

            [JsonIgnore] public Frame Current => Frames[Frames.Count - 1];
            [JsonIgnore] public bool  Broken  => Frames.Count == 0 || Frames.Any(f => f.Job == null);

            [JsonIgnore]
            public bool TimedOut => Deadline > 0 && EditorApplication.timeSinceStartup > Deadline;
        }

        // The JobRegistry face of a step job: detach goes through the entry's Answered gate and
        // touches only thread-safe state, never the runner's entry list — that is what makes it
        // callable from the background command executor while the main thread is busy.
        private sealed class Handle : JobRegistry.IJobHandle
        {
            private readonly Entry _entry;

            public Handle(Entry entry) => _entry = entry;

            public string JobId     => _entry.JobId;
            public ulong  RequestId => _entry.RequestId;
            public string Name      => _entry.Name;
            public string Runner    => "step";
            public bool   Detached  => _entry.Detached;

            public bool TryDetach(string reason, out string error)
            {
                if (Interlocked.CompareExchange(ref _entry.Answered, 1, 0) != 0)
                {
                    error = _entry.Detached
                        ? "Job is already detached."
                        : "Job already completed — its request has been answered.";
                    return false;
                }

                _entry.Detached = true;
                DetachedJobs.MarkPending(_entry.JobId, _entry.Name);
                // The Detached flag must survive a domain reload, but SessionState is main-thread
                // only — ask the next Tick to persist (beforeAssemblyReload's Save is the backstop).
                _saveRequested = true;

                error = null;
                if (_entry.RequestId == 0) return true;  // no live native request to ack (tests)
                try
                {
                CodelyLogger.Log($"[StepJobRunner] detaching job '{_entry.Name}' with '{reason}': {_entry.RequestId}");

                    NativeUnityTcpBridgeHost.EnqueueResponse(_entry.RequestId, JsonConvert.SerializeObject(
                        Response.Success(
                            $"Job detached. Use the manage_job tool (action 'status', job_id '{_entry.JobId}') to query the job's status and collect its result.",
                            new
                            {
                                job = _entry.Name,
                                id = _entry.JobId,
                                status = "detached",
                                reason = reason ?? "Detached by request.",
                            })));
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning(
                        $"[StepJobRunner] Failed to ack detach for '{_entry.Name}': {ex.Message}");
                }
                return true;
            }
        }

        private static readonly List<Entry> _entries = new List<Entry>();

        // Set from any thread when state changed outside the tick (a cross-thread detach);
        // consumed by Tick on the main thread, which owns persistence.
        private static volatile bool _saveRequested;
        private static bool _initialized;

        static StepJobRunner()
        {
            if (EditorAutomationGuard.ShouldSkipBridgeAutoStart()) return;

            EnsureInitialized();
        }

        // Main-thread only. Explicit bridge/job starts need restoration too, even
        // when InitializeOnLoad deliberately skipped auto-start in this domain.
        internal static void EnsureInitialized()
        {
            if (_initialized) return;
            // Set before Load: a restored job's OnRestored may enroll another job.
            _initialized = true;
            Load();
            // Pending markers persist but only step jobs survive the reload; retire the rest so no
            // client is left polling a job that no longer exists.
            DetachedJobs.ReconcilePending(_entries.Where(e => e.Detached).Select(e => e.JobId).ToList());
            // Persist as late as possible so a job that keeps working right up to the reload saves
            // the state it actually ended on.
            AssemblyReloadEvents.beforeAssemblyReload -= Save;
            AssemblyReloadEvents.beforeAssemblyReload += Save;
        }

        /// <summary>
        /// Enrolls a step job and returns its context. Return that context from a HandleCommand to
        /// hold the client's request open until the job finishes; with <paramref name="detached"/>
        /// true, answer <see cref="JobContext.ToPendingResponse"/> instead and let the client poll
        /// the job id via <see cref="DetachedJobs.Check"/>.
        ///
        /// The timeout is wall-clock against the editor session clock, so time spent inside a
        /// domain reload counts against it — budget for that on jobs that trigger a recompile.
        /// Pass 0 for no timeout.
        /// </summary>
        public static JobContext Start(ulong requestId, string name, StepJob job,
                                       int timeoutSeconds = JobRunnerBase.DefaultTimeoutSeconds,
                                       bool detached = false)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            EnsureInitialized();

            var ctx = new JobContext(requestId, name, detached);
            var entry = new Entry
            {
                JobId = ctx.JobId,
                Name = ctx.Name,
                RequestId = requestId,
                Detached = detached,
                // A job launched detached is answered by the handler itself (ToPendingResponse),
                // so its delivery gate starts claimed.
                Answered = detached ? 1 : 0,
                Deadline = timeoutSeconds > 0
                    ? EditorApplication.timeSinceStartup + timeoutSeconds
                    : 0,
            };
            entry.Frames.Add(NewFrame(job));
            _entries.Add(entry);
            JobRegistry.Register(new Handle(entry));

            if (detached) DetachedJobs.MarkPending(ctx);
            Save();
            return ctx;
        }

        /// <summary>Number of jobs currently in flight (nesting does not count extra).</summary>
        public static int InFlightCount => _entries.Count;

        /// <summary>
        /// Advances every job by one step; called each editor frame. A job whose current step's
        /// leave condition is still false stays on that step and its Run is invoked again next
        /// frame. For a nested job, the step that runs is the one at the deepest active frame.
        ///
        /// Steps whose skip condition holds are passed over for free — a run of them is consumed
        /// and the first step that does not skip still runs — so "one step per frame" counts only
        /// steps that actually execute.
        /// </summary>
        public static void Tick()
        {
            if (_entries.Count == 0) return;

            // A cross-thread detach flipped an entry's Detached flag outside the tick; persist it
            // now so the job comes back detached if a domain reload lands before it finishes.
            bool dirty = _saveRequested;
            _saveRequested = false;

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];

                // Cross-thread cancel requests (see JobControl) are applied here, on the tick, so
                // entries are only ever touched from the main thread. (Detach needs no tick — it
                // goes through the registry handle immediately.)
                if (JobControl.TryTakeCancel(entry.JobId, out string cancelReason))
                {
                    Finish(i, entry, Response.Error(
                        cancelReason ?? $"Step job '{entry.Name}' canceled."));
                    dirty = true;
                    continue;
                }

                if (entry.Broken)
                {
                    // A frame failed to rebuild in this domain — the job can never progress again.
                    Finish(i, entry, Response.Error(
                        $"Step job '{entry.Name}' could not be restored after the domain reload."));
                    dirty = true;
                    continue;
                }

                if (entry.TimedOut)
                {
                    Finish(i, entry, Response.Error(
                        $"Step job '{entry.Name}' timed out at step '{StepPath(entry)}'."));
                    dirty = true;
                    continue;
                }

                bool done;
                try
                {
                    dirty |= Advance(entry, out done);
                }
                catch (Exception ex)
                {
                    // A throwing step (or BuildSteps) ends the job rather than stalling the loop —
                    // the other jobs in this frame must still get their tick.
                    Finish(i, entry, Response.Error(
                        $"Step job '{entry.Name}' failed at step '{StepPath(entry)}': {JobRunnerBase.SafeError(ex)}"));
                    dirty = true;
                    continue;
                }

                if (done)
                {
                    Finish(i, entry, null);
                    dirty = true;
                }
            }

            if (dirty) Save();
        }

        /// <summary>
        /// Fails and answers every in-flight job, and clears the persisted state. Called on bridge
        /// stop / editor quit — NOT on domain reload, which these jobs are built to survive.
        /// </summary>
        public static void CancelAll(string reason = "Operation canceled.")
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
                Finish(i, _entries[i], Response.Error(reason));
            Save();
        }

        // Advances the deepest active frame by one step. Returns whether anything worth persisting
        // changed; `done` reports that the whole job (root frame included) has finished.
        private static bool Advance(Entry entry, out bool done)
        {
            done = false;
            var frame = entry.Current;
            var job = frame.Job;
            var steps = job.Steps;

            // ── pass over skipped steps ───────────────────────────────────────────
            // Skipping is free: a whole run of skipped steps is consumed here and the first step
            // that does not skip still runs in this same frame. The index only moves forward, so
            // this cannot loop.
            bool skipped = !job.IsFinished && SkipAhead(frame, steps);

            // ── this frame's job is over ──────────────────────────────────────────
            if (job.IsFinished || frame.StepIndex >= steps.Length)
            {
                if (entry.Frames.Count == 1)
                {
                    done = true;
                    return true;
                }
                PopSubJob(entry);
                return true;
            }

            var step = steps[frame.StepIndex];

            // ── composite step: push the child and let it run from the next frame ─
            if (step.SubJob != null && !frame.SubJobStarted)
            {
                if (entry.Frames.Count >= MaxNestingDepth)
                    throw new InvalidOperationException(
                        $"Sub-job nesting exceeded {MaxNestingDepth} levels at step '{step.Name}'.");

                frame.SubJobStarted = true;
                var child = step.SubJob();
                if (child != null)
                {
                    entry.Frames.Add(NewFrame(child));
                    return true;
                }
                // A null factory result means "skip the child" — fall through and treat this as a
                // plain step so the conditional nesting costs the same single frame.
            }

            // ── plain step (and a composite step whose child has already finished) ─
            step.Run?.Invoke();

            if (job.IsFinished)
            {
                if (entry.Frames.Count == 1)
                    done = true;
                return true;
            }

            if (step.CanLeave == null || step.CanLeave())
            {
                frame.StepIndex++;
                frame.SubJobStarted = false;
                return true;
            }
            return skipped;
        }

        // Advances past every step whose skip condition currently holds. Returns whether the index
        // moved, which is what makes the skipping worth persisting.
        private static bool SkipAhead(Frame frame, JobStep[] steps)
        {
            bool moved = false;
            while (frame.StepIndex < steps.Length)
            {
                var step = steps[frame.StepIndex];
                if (step.Skip == null || !step.Skip()) break;

                frame.StepIndex++;
                frame.SubJobStarted = false;
                moved = true;
            }
            return moved;
        }

        // Ends a child frame: hands its response to the parent, runs the parent's onComplete hook,
        // then evaluates the parent's leave condition — all within this one tick, so a composite
        // step costs one frame to enter and one to leave.
        private static void PopSubJob(Entry entry)
        {
            var child = entry.Current;
            entry.Frames.RemoveAt(entry.Frames.Count - 1);

            var frame = entry.Current;
            var parent = frame.Job;
            parent.SetSubJobResponse(child.Job.ToResponseJson(entry.Name, entry.JobId));

            var steps = parent.Steps;
            if (frame.StepIndex >= steps.Length) return;   // parent finished underneath the child

            var step = steps[frame.StepIndex];
            step.OnSubJobComplete?.Invoke();

            if (parent.IsFinished) return;

            if (step.CanLeave == null || step.CanLeave())
            {
                frame.StepIndex++;
                frame.SubJobStarted = false;
            }
        }

        // Removes the job and delivers its outcome. A non-null errorResponse overrides whatever the
        // job itself recorded. The entry's Answered gate decides where the outcome goes: if the
        // original request is still unanswered this claims it and responds now; otherwise — the job
        // launched detached, or a mid-flight detach (possibly from another thread) got there
        // first — the outcome is memorized for polling. Exactly one of the two ever happens.
        private static void Finish(int index, Entry entry, object errorResponse)
        {
            _entries.RemoveAt(index);
            JobRegistry.Unregister(entry.JobId);

            string json;
            if (errorResponse != null)
                json = JsonConvert.SerializeObject(errorResponse);
            else
                json = entry.Frames[0].Job.ToResponseJson(entry.Name, entry.JobId);

            try
            {
                if (Interlocked.CompareExchange(ref entry.Answered, 1, 0) == 0)
                    NativeUnityTcpBridgeHost.EnqueueResponse(entry.RequestId, json);
                else
                    DetachedJobs.StoreJson(entry.JobId, json);
            }
            catch (Exception ex)
            {
                // Delivery must never throw into the tick loop — the other jobs still need their
                // frame, and this one is already out of the registry.
                CodelyLogger.LogWarning(
                    $"[StepJobRunner] Failed to deliver '{entry.Name}' result: {ex.Message}");
            }
        }

        private static Frame NewFrame(StepJob job) => new Frame
        {
            TypeName = job.GetType().AssemblyQualifiedName,
            Job = job,
        };

        // Used to build error messages, including for a job whose BuildSteps is what threw — so it
        // must never throw itself. Renders nested frames as "parent > child".
        private static string StepPath(Entry entry)
        {
            try
            {
                return string.Join(" > ", entry.Frames.Select(StepNameOf).ToArray());
            }
            catch { return "(unknown)"; }
        }

        private static string StepNameOf(Frame frame)
        {
            try
            {
                var steps = frame.Job?.Steps;
                return steps != null && frame.StepIndex >= 0 && frame.StepIndex < steps.Length
                    ? steps[frame.StepIndex].Name
                    : $"#{frame.StepIndex}";
            }
            catch { return $"#{frame.StepIndex}"; }
        }

        // ---- persistence ---------------------------------------------------- //

        private static void Save()
        {
            if (_entries.Count == 0)
            {
                SessionState.EraseString(SessionKeyJobs);
                return;
            }

            try
            {
                foreach (var frame in _entries.SelectMany(e => e.Frames))
                {
                    if (frame.Job != null)
                        frame.StateJson = JsonConvert.SerializeObject(frame.Job);
                }
                SessionState.SetString(SessionKeyJobs, JsonConvert.SerializeObject(_entries));
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[StepJobRunner] Failed to persist jobs: {ex.Message}");
            }
        }

        /// <summary>
        /// Test seam: persists every in-flight job, drops the live instances, and rebuilds them
        /// from SessionState — the same round trip a domain reload puts them through, minus the
        /// reload. Nothing in the editor calls this; jobs go through <see cref="Save"/> on
        /// <c>beforeAssemblyReload</c> and <see cref="Load"/> on the next domain's initialization.
        /// </summary>
        internal static void SimulateDomainReload()
        {
            Save();
            _entries.Clear();
            _initialized = false;
            EnsureInitialized();
        }

        private static void Load()
        {
            string json = SessionState.GetString(SessionKeyJobs, null);
            if (string.IsNullOrEmpty(json)) return;

            List<Entry> restored;
            try { restored = JsonConvert.DeserializeObject<List<Entry>>(json); }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[StepJobRunner] Failed to read persisted jobs: {ex.Message}");
                SessionState.EraseString(SessionKeyJobs);
                return;
            }
            if (restored == null) return;

            foreach (var entry in restored)
            {
                if (entry.Frames == null) entry.Frames = new List<Frame>();
                foreach (var frame in entry.Frames)
                {
                    frame.Job = Rebuild(frame, entry.Name);
                    if (frame.Job == null) continue;
                    try { frame.Job.OnRestored(); }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogWarning(
                            $"[StepJobRunner] OnRestored threw for '{entry.Name}': {ex.Message}");
                    }
                }
                // Entries with a broken frame are kept so Tick can report the failure to the client.
                _entries.Add(entry);
                // Re-register with the cross-thread registry — the old domain's handles died with
                // it, but the job id (and its native request) are still live.
                JobRegistry.Register(new Handle(entry));
            }
        }

        private static StepJob Rebuild(Frame frame, string jobName)
        {
            try
            {
                var type = ResolveType(frame.TypeName);
                if (type == null)
                {
                    CodelyLogger.LogWarning(
                        $"[StepJobRunner] Cannot resolve job type '{frame.TypeName}' for '{jobName}'.");
                    return null;
                }
                return JsonConvert.DeserializeObject(frame.StateJson ?? "{}", type) as StepJob;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning(
                    $"[StepJobRunner] Failed to restore job '{jobName}': {ex.Message}");
                return null;
            }
        }

        // Type.GetType resolves the assembly-qualified name directly in the common case; the scan
        // is the fallback for a job type whose assembly identity changed across the reload.
        private static Type ResolveType(string assemblyQualifiedName)
        {
            if (string.IsNullOrEmpty(assemblyQualifiedName)) return null;

            var type = Type.GetType(assemblyQualifiedName, throwOnError: false);
            if (type != null) return type;

            string fullName = assemblyQualifiedName.Split(',')[0].Trim();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null) return type;
                }
                catch { /* dynamic or unloadable assembly — keep scanning */ }
            }
            return null;
        }
    }
}
