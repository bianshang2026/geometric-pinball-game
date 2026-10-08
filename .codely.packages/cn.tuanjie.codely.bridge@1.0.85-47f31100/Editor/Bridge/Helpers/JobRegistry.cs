using System.Collections.Concurrent;
using System.Collections.Generic;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Thread-safe index of every in-flight runner job, keyed by job id and by the native request
    /// id that started it. The request-id index is what lets a client detach a job it has not yet
    /// received a job id for — it always knows the request id of the call it is stuck waiting on.
    ///
    /// Runners register on enroll (and <see cref="StepJobRunner"/> re-registers restored jobs
    /// after a domain reload) and unregister when the job leaves their registry. The handle a
    /// runner registers is what makes cross-thread detach possible: its
    /// <see cref="IJobHandle.TryDetach"/> touches only thread-safe state (the
    /// <see cref="JobContext"/> delivery gate, <see cref="DetachedJobs"/>, the native response
    /// queue), never the runner's main-thread-only job list.
    /// </summary>
    public static class JobRegistry
    {
        /// <summary>A registered in-flight job, with the thread-safe detach entry point.</summary>
        public interface IJobHandle
        {
            string JobId { get; }
            ulong  RequestId { get; }
            string Name { get; }
            /// <summary>Owning runner: "step", "task", or "coroutine".</summary>
            string Runner { get; }
            bool   Detached { get; }

            /// <summary>
            /// Detaches the job: answers its original request with a detach ack carrying the job
            /// id, after which the outcome lands in <see cref="DetachedJobs"/> for polling.
            /// Callable from any thread. Returns false (with an explanation) when the job was
            /// already detached or already answered.
            /// </summary>
            bool TryDetach(string reason, out string error);
        }

        private static readonly ConcurrentDictionary<string, IJobHandle> _byJobId =
            new ConcurrentDictionary<string, IJobHandle>();
        private static readonly ConcurrentDictionary<ulong, string> _jobIdByRequestId =
            new ConcurrentDictionary<ulong, string>();

        internal static void Register(IJobHandle handle)
        {
            if (handle == null || string.IsNullOrEmpty(handle.JobId)) return;
            _byJobId[handle.JobId] = handle;
            if (handle.RequestId != 0)
                _jobIdByRequestId[handle.RequestId] = handle.JobId;
        }

        internal static void Unregister(string jobId)
        {
            if (string.IsNullOrEmpty(jobId)) return;
            if (_byJobId.TryRemove(jobId, out var handle) && handle.RequestId != 0)
            {
                // Only drop the request mapping if it still points at this job — a handed-off job
                // re-registers under the same request id before the old runner unregisters.
                if (_jobIdByRequestId.TryGetValue(handle.RequestId, out string mapped) && mapped == jobId)
                    _jobIdByRequestId.TryRemove(handle.RequestId, out _);
            }
        }

        public static bool TryGet(string jobId, out IJobHandle handle)
        {
            handle = null;
            return !string.IsNullOrEmpty(jobId) && _byJobId.TryGetValue(jobId, out handle);
        }

        public static bool TryGetByRequestId(ulong requestId, out IJobHandle handle)
        {
            handle = null;
            return requestId != 0
                && _jobIdByRequestId.TryGetValue(requestId, out string jobId)
                && _byJobId.TryGetValue(jobId, out handle);
        }

        /// <summary>Snapshot of every in-flight job, for <c>manage_job.list</c>.</summary>
        public static List<IJobHandle> Snapshot()
        {
            var result = new List<IJobHandle>();
            foreach (var kv in _byJobId)
                result.Add(kv.Value);
            return result;
        }

        public static int Count => _byJobId.Count;

        internal static void Clear()
        {
            _byJobId.Clear();
            _jobIdByRequestId.Clear();
        }
    }
}
