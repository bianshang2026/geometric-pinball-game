using System;
using System.Collections.Concurrent;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Cross-thread control plane for in-flight runner jobs (step / async task / coroutine).
    /// Both entry points are callable from any thread — in particular from the
    /// <see cref="BackgroundCommandPump"/> worker while the main thread is busy or blocked:
    ///
    /// Detach (<see cref="TryDetach"/>) takes effect immediately: it claims the job's
    /// single-delivery gate, answers the original request with a "detached" ack carrying the job
    /// id, and marks the job pending in <see cref="DetachedJobs"/> — all through thread-safe
    /// state, so it works even mid-command. The job keeps running; its eventual outcome is
    /// collected by polling <c>manage_job.status</c>.
    ///
    /// Cancel (<see cref="RequestCancel"/>) cannot safely tear a job out of a runner from another
    /// thread — runner state is main-thread-only — so it is queued here and applied by the owning
    /// runner on its next tick: the job is failed with the given reason and answered (or
    /// memorized, if detached).
    ///
    /// Cancel requests are keyed by job id, last post wins. A request against a job id no runner
    /// owns (finished, mistyped, or lost to a domain reload) is pruned after
    /// <see cref="StaleAfterMs"/> so the table cannot grow unbounded.
    /// </summary>
    public static class JobControl
    {
        private sealed class CancelRequest
        {
            public string Reason;
            public int    PostedAtTick;  // Environment.TickCount — readable from any thread,
                                         // unlike the editor session clock
        }

        private static readonly ConcurrentDictionary<string, CancelRequest> _cancels =
            new ConcurrentDictionary<string, CancelRequest>();

        private const int StaleAfterMs = 120_000;

        /// <summary>
        /// Immediately detaches a running job, resolved by job id (or, when that fails, by the
        /// native request id the client is stuck waiting on). Callable from any thread. On success
        /// the original request has been answered with a detach ack and the job's outcome will be
        /// collectable via <c>manage_job.status</c>.
        /// </summary>
        public static bool TryDetach(string jobId, ulong requestId, string reason, out string detachedJobId, out string error)
        {
            detachedJobId = null;

            JobRegistry.IJobHandle handle;
            if (!JobRegistry.TryGet(jobId, out handle) && !JobRegistry.TryGetByRequestId(requestId, out handle))
            {
                error = "No in-flight job matches the given job id / request id.";
                return false;
            }

            detachedJobId = handle.JobId;
            return handle.TryDetach(reason, out error);
        }

        /// <summary>
        /// Detaches every in-flight job that is not already detached, all with the same reason.
        /// Callable from any thread — used by <see cref="DialogWatcher"/> when a native
        /// modal dialog blocks the main thread, so every caller stuck waiting on a job gets an
        /// immediate answer telling it what is blocking and how to unblock it. Returns how many
        /// jobs were detached.
        /// </summary>
        public static int DetachAll(string reason)
        {
            int detached = 0;
            foreach (var handle in JobRegistry.Snapshot())
            {
                if (handle.TryDetach(reason, out _))
                    detached++;
            }
            return detached;
        }

        /// <summary>
        /// Requests a cancel; callable from any thread. Applied by the owning runner on its next
        /// tick. Returns false when no in-flight job matches (the request is still queued briefly,
        /// in case the caller raced the job's enrollment).
        /// </summary>
        public static bool RequestCancel(string jobId, string reason = null)
        {
            if (string.IsNullOrEmpty(jobId)) return false;
            Prune();
            _cancels[jobId] = new CancelRequest { Reason = reason, PostedAtTick = Environment.TickCount };
            return JobRegistry.TryGet(jobId, out _);
        }

        /// <summary>
        /// Consumes the pending cancel for a job, if any. Called by the runners at tick time
        /// (main thread), once per job per tick.
        /// </summary>
        internal static bool TryTakeCancel(string jobId, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(jobId) || _cancels.IsEmpty) return false;
            if (!_cancels.TryRemove(jobId, out var request)) return false;

            reason = request.Reason;
            return true;
        }

        internal static int PendingCancelCount => _cancels.Count;

        internal static void Clear() => _cancels.Clear();

        private static void Prune()
        {
            if (_cancels.IsEmpty) return;
            int now = Environment.TickCount;
            foreach (var kv in _cancels)
            {
                // unchecked subtraction stays correct across TickCount wraparound
                if (unchecked(now - kv.Value.PostedAtTick) > StaleAfterMs)
                    _cancels.TryRemove(kv.Key, out _);
            }
        }
    }
}
