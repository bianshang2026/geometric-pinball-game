using System;
using System.Collections;
using System.Threading;
using Codely.Newtonsoft.Json;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// A tracked async job: the native <see cref="RequestId"/> to reply on, plus an optional
    /// <see cref="Response"/> outcome that gets serialized when the job finishes.
    ///
    /// Created via a runner's CreateJob(requestId, name). A coroutine reports its outcome with
    /// <see cref="SetResult"/> / <see cref="SetError"/> (it cannot return a value). A Task may
    /// return a Response from its result; otherwise the AsyncTaskRunner fills the context.
    ///
    /// Result and Error are Response objects and may be null. When null, <see cref="ToResponseJson"/>
    /// synthesizes a Response from the job name/id.
    ///
    /// The original request is answered exactly once, guarded by a single-delivery gate
    /// (<see cref="TryClaimRequestDelivery"/>): either by the runner when the job finishes, or by
    /// a mid-flight detach (<see cref="JobControl"/>, possibly from a non-main thread) that
    /// early-returns the job id — whichever claims the gate first. The loser routes the outcome
    /// to <see cref="DetachedJobs"/> instead, so a detach racing a completion never double-answers
    /// and never loses the result.
    /// </summary>
    public sealed class JobContext
    {
        /// <summary>Native request id — passed to NativeUnityTcpBridgeHost.EnqueueResponse.</summary>
        public ulong RequestId { get; }

        /// <summary>Human-readable job name (e.g. the command type), echoed in the response.</summary>
        public string Name { get; }

        /// <summary>Stable id for correlating a detached job across the client's poll requests.</summary>
        public string JobId { get; }

        // 1 once the original request has been (or is being) answered — by the handler's early
        // pending response for a job launched detached, by a mid-flight detach ack, or by the
        // finish path. Interlocked, because detach may claim it from a non-main thread while the
        // runner finishes the job on the main thread.
        private int _requestAnswered;

        /// <summary>
        /// When true, the finished job is memorized (see <see cref="DetachedJobs"/>) instead of
        /// being sent on <see cref="RequestId"/>: the client early-returned with <see cref="JobId"/>
        /// and polls for the result later. Set at launch, or by a mid-flight detach (see
        /// <see cref="JobControl"/>).
        /// </summary>
        public bool Detached => Volatile.Read(ref _detached);
        private bool _detached;

        internal JobContext(ulong requestId, string name, bool detached)
        {
            RequestId = requestId;
            Name = name;
            _detached = detached;
            // A job launched detached is answered by the handler itself (ToPendingResponse), so
            // the gate starts claimed and the finish path goes straight to DetachedJobs.
            _requestAnswered = detached ? 1 : 0;
            JobId = Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Claims the one-and-only delivery on the original request. Returns true exactly once
        /// per context; thread-safe. The finish path answers the request only when it wins this
        /// claim — otherwise the result goes to <see cref="DetachedJobs"/>.
        /// </summary>
        internal bool TryClaimRequestDelivery()
            => Interlocked.CompareExchange(ref _requestAnswered, 1, 0) == 0;

        /// <summary>Success Response to send, or null to synthesize from the job name.</summary>
        internal object Result { get; private set; }

        /// <summary>Error Response to send, or null to synthesize from the job name.</summary>
        internal object Error { get; private set; }

        internal bool Failed { get; private set; }

        /// <summary>True once <see cref="SetResult"/> or <see cref="SetError"/> has been called.</summary>
        internal bool HasOutcome { get; private set; }

        /// <summary>
        /// Records the success Response. Pass null to let <see cref="ToResponseJson"/> build a
        /// default success from the job name.
        /// </summary>
        public void SetResult(object response = null)
        {
            Failed = false;
            Error = null;
            Result = response;
            HasOutcome = true;
        }

        /// <summary>
        /// Records the error Response. Pass null to let <see cref="ToResponseJson"/> build a
        /// default error from the job name.
        /// </summary>
        public void SetError(object response = null)
        {
            Failed = true;
            Result = null;
            Error = response;
            HasOutcome = true;
        }

        /// <summary>Builds the response JSON the runner enqueues to the client when finished.</summary>
        internal string ToResponseJson()
        {
            var jobData = new { job = Name, id = JobId };
            object response = Failed
                ? (Error ?? Response.Error(Name ?? "Operation failed.", jobData))
                : (Result ?? Response.Success(Name ?? "Operation completed.", jobData));
            return JsonConvert.SerializeObject(response);
        }

        /// <summary>
        /// Early-return response for a detached job: tells the client the job started and to poll
        /// for the result later using <see cref="JobId"/>.
        /// </summary>
        public object ToPendingResponse()
            => Response.Success(
                $"Job started. Use the manage_job tool (action 'status', job_id '{JobId}') to query the job's status and collect its result.",
                new { job = Name, id = JobId, status = "pending" });

        /// <summary>
        /// Flips an attached job to detached after launch. Callable from any thread — the caller
        /// must have claimed <see cref="TryClaimRequestDelivery"/> first (see
        /// <see cref="JobControl"/>).
        /// </summary>
        internal void MarkDetached() => Volatile.Write(ref _detached, true);

        /// <summary>
        /// Ack sent on the original request when the job is detached mid-flight: tells the caller
        /// to stop waiting, and how to poll <see cref="JobId"/> for the eventual result.
        /// </summary>
        internal string ToDetachedResponseJson(string reason)
            => JsonConvert.SerializeObject(Response.Success(
                $"Job detached. Use the manage_job tool (action 'status', job_id '{JobId}') to query the job's status and collect its result.",
                new { job = Name, id = JobId, status = "detached", reason = reason ?? "Detached by request." }));

        /// <summary>True when <paramref name="value"/> looks like a <see cref="Response"/> payload.</summary>
        internal static bool IsResponse(object value)
            => value is IDictionary d && d.Contains("success");
    }
}
