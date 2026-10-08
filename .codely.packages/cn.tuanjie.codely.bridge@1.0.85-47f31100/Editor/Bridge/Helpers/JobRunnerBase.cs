using System;
using System.Collections.Generic;
using UnityEditor;
using UnityTcp.Editor.Native;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Shared lifecycle for the async job runners: an in-flight job registry, the per-frame tick
    /// loop, timeout, cancellation, and the requestId → NativeUnityTcpBridgeHost.EnqueueResponse
    /// delivery. Subclasses implement only the one differing step — <see cref="Advance"/> — which
    /// polls a Task or MoveNexts a coroutine.
    ///
    /// Everything runs on the editor main thread (Tick is called once per editor frame from the
    /// top-level command loop) — except detach, which <see cref="JobControl"/> may apply from any
    /// thread through the <see cref="JobRegistry"/> handle enrolled here. The handle touches only
    /// thread-safe state (the JobContext delivery gate, DetachedJobs, the native response queue),
    /// never this runner's job list. Cancel requests are queued in JobControl and applied here at
    /// tick time. Jobs are runtime-only and are NOT persisted across domain reloads.
    /// </summary>
    public abstract class JobRunnerBase
    {
        public const int DefaultTimeoutSeconds = 300;

        /// <summary>Result of advancing a job one step.</summary>
        protected enum Step
        {
            Running,    // still in flight — keep ticking
            Finished,   // done — respond to the client and drop it
            HandedOff,  // another runner took ownership — drop it, it will respond
        }

        protected sealed class Job
        {
            public JobContext Ctx;
            public object     Work;      // Task or IEnumerator
            public double     Deadline;  // EditorApplication.timeSinceStartup; 0 = no timeout

            public bool TimedOut => Deadline > 0 && EditorApplication.timeSinceStartup > Deadline;
        }

        // The JobRegistry face of a runtime job: detach goes through the JobContext delivery
        // gate, so it is safe from any thread and can never double-answer the request.
        private sealed class Handle : JobRegistry.IJobHandle
        {
            private readonly JobContext _ctx;
            private readonly string _runner;

            public Handle(JobContext ctx, string runner)
            {
                _ctx = ctx;
                _runner = runner;
            }

            public string JobId     => _ctx.JobId;
            public ulong  RequestId => _ctx.RequestId;
            public string Name      => _ctx.Name;
            public string Runner    => _runner;
            public bool   Detached  => _ctx.Detached;

            public bool TryDetach(string reason, out string error)
            {
                if (!_ctx.TryClaimRequestDelivery())
                {
                    error = _ctx.Detached
                        ? "Job is already detached."
                        : "Job already completed — its request has been answered.";
                    return false;
                }

                _ctx.MarkDetached();
                DetachedJobs.MarkPending(_ctx);

                error = null;
                if (_ctx.RequestId == 0) return true;  // no live native request to ack (tests)
                CodelyLogger.Log($"[JobRunner] detaching job '{_ctx.Name}' with '{reason}': {_ctx.RequestId}");
                try { NativeUnityTcpBridgeHost.EnqueueResponse(_ctx.RequestId, _ctx.ToDetachedResponseJson(reason)); }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"[JobRunner] Failed to ack detach for '{_ctx.Name}': {ex.Message}");
                }
                return true;
            }
        }

        private readonly List<Job> _jobs = new List<Job>();

        /// <summary>Registry label for jobs enrolled here: "task" or "coroutine".</summary>
        protected abstract string RunnerName { get; }

        /// <summary>
        /// Creates a job/context bound to a native request id, with a name for the response.
        /// When <paramref name="detached"/> is true the finished job is memorized for later
        /// polling instead of being sent on the request (see <see cref="DetachedJobs"/>).
        /// </summary>
        public JobContext CreateJob(ulong requestId, string name, bool detached = false)
            => new JobContext(requestId, name, detached);

        protected void Enroll(JobContext ctx, object work, int timeoutSeconds)
        {
            if (ctx == null)  throw new ArgumentNullException(nameof(ctx));
            if (work == null) throw new ArgumentNullException(nameof(work));

            _jobs.Add(new Job
            {
                Ctx = ctx,
                Work = work,
                Deadline = timeoutSeconds > 0
                    ? EditorApplication.timeSinceStartup + timeoutSeconds
                    : 0,
            });

            if (ctx.Detached) DetachedJobs.MarkPending(ctx);
            // Registering under an already-known JobId overwrites — that is what a hand-off
            // between runners relies on (same ctx, new owner).
            JobRegistry.Register(new Handle(ctx, RunnerName));
        }

        /// <summary>Advance one job by one step. Implemented per runner (Task vs coroutine).</summary>
        protected abstract Step Advance(Job job);

        /// <summary>
        /// Releases a job's work when it leaves the registry (finished, faulted, timed out, or
        /// cancelled). Coroutine jobs override this to Dispose their enumerators — a C# iterator's
        /// finally blocks run ONLY on Dispose, never on a caught throw, so without this the
        /// enumerator's cleanup (e.g. dropping a log-capture subscription) would never execute.
        /// Base is a no-op; Task jobs need no disposal (disposing an in-flight Task throws).
        /// </summary>
        protected virtual void Cleanup(Job job) { }

        /// <summary>
        /// Called once per editor frame from the top-level loop: advances every job by one step
        /// and enqueues the response for any that finished (or timed out / threw / was cancelled
        /// through <see cref="JobControl"/>).
        /// </summary>
        public void Tick()
        {
            for (int i = _jobs.Count - 1; i >= 0; i--)
            {
                var job = _jobs[i];

                // Cross-thread cancel requests are applied here, at tick time, so the runner's
                // state is only ever touched from the main thread. (Detach needs no tick — it
                // goes through the registry handle immediately.)
                if (JobControl.TryTakeCancel(job.Ctx.JobId, out string cancelReason))
                {
                    job.Ctx.SetError(Response.Error(cancelReason ?? "Job canceled."));
                    Remove(i, job);
                    SafeCleanup(job);
                    Finalize(job.Ctx);
                    continue;
                }

                Step step;
                if (job.TimedOut)
                {
                    job.Ctx.SetError(Response.Error("Operation timed out."));
                    step = Step.Finished;
                }
                else
                {
                    try { step = Advance(job); }
                    catch (Exception ex) { job.Ctx.SetError(Response.Error(SafeError(ex))); step = Step.Finished; }
                }

                if (step == Step.Running) continue;

                // A handed-off job now belongs to another runner, which has re-registered the
                // same job id — removing the registry entry here would tear down its handle.
                _jobs.RemoveAt(i);
                if (step == Step.HandedOff) continue;

                JobRegistry.Unregister(job.Ctx.JobId);
                // Dispose the work (runs the coroutine's finally blocks) before finalizing so any
                // cleanup it performs — e.g. releasing a log-capture subscription — happens now.
                SafeCleanup(job);
                Finalize(job.Ctx);
            }
        }

        /// <summary>
        /// Fails and responds to every in-flight job — e.g. on play-mode exit, server stop, or
        /// domain reload — so no client request is left hanging.
        /// </summary>
        public void CancelAll(string reason = "Operation canceled.")
        {
            var snapshot = _jobs.ToArray();
            _jobs.Clear();
            foreach (var job in snapshot)
            {
                JobRegistry.Unregister(job.Ctx.JobId);
                job.Ctx.SetError(Response.Error(reason));
                SafeCleanup(job);
                Finalize(job.Ctx);
            }
        }

        private void Remove(int index, Job job)
        {
            _jobs.RemoveAt(index);
            JobRegistry.Unregister(job.Ctx.JobId);
        }

        // Cleanup must never throw into the tick loop or the cancel sweep — a user coroutine's
        // finally block could raise. Swallow and log so one bad job can't strand the others.
        private void SafeCleanup(Job job)
        {
            try { Cleanup(job); }
            catch (Exception ex) { CodelyLogger.LogWarning($"[JobRunner] Cleanup failed: {ex.Message}"); }
        }

        // Deliver a finished job. The single-delivery gate decides where the outcome goes: if the
        // original request is still unanswered this claims it and responds now; otherwise — the
        // job launched detached, or a mid-flight detach (possibly from another thread) got there
        // first — the outcome is memorized for polling. Exactly one of the two ever happens.
        private static void Finalize(JobContext ctx)
        {
            try
            {
                if (ctx.TryClaimRequestDelivery())
                    NativeUnityTcpBridgeHost.EnqueueResponse(ctx.RequestId, ctx.ToResponseJson());
                else
                    DetachedJobs.Store(ctx);
            }
            catch (Exception ex)
            {
                // Delivery must never throw into the tick loop or the cancel sweep — the other
                // jobs still need their turn, and this one is already out of the registry.
                CodelyLogger.LogWarning($"[JobRunner] Failed to deliver '{ctx.Name}' result: {ex.Message}");
            }
        }

        // Reading Exception.StackTrace can itself throw on Mono when the stack passes through
        // async state machines — read it defensively.
        internal static string SafeError(Exception e)
        {
            if (e == null) return "Unknown error.";
            string trace;
            try { trace = e.StackTrace; } catch { trace = "(stack trace unavailable)"; }
            return $"{e.Message}\n{trace}";
        }
    }
}
