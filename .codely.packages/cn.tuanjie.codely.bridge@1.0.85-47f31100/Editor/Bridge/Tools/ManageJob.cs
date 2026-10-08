using System;
using System.Collections.Generic;
using Codely.Newtonsoft.Json.Linq;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// <c>manage_job</c>: surface over in-flight runner jobs (step / async task /
    /// coroutine). Everything here touches only thread-safe state, so the whole command runs on
    /// the <see cref="BackgroundCommandPump"/> worker thread — which is the point: it stays
    /// responsive while the main thread is busy running the very job being queried.
    ///
    /// Actions:
    ///   status (alias: check) — poll a job by id: running / pending / complete (the finished
    ///     result is returned and collected — single collection) / unknown.
    ///   list — snapshot of every in-flight job.
    ///   cancel — queue a cancel for a job id; the owning runner fails it on its next tick.
    ///
    /// Detaching is not a client action: jobs are detached by the bridge itself (see
    /// <see cref="DialogWatcher"/> / <see cref="JobControl"/>, e.g. when a modal dialog blocks
    /// the main thread), and the client then collects them here via status.
    /// </summary>
    public static class ManageJob
    {
        private static readonly Dictionary<string, Func<JObject, object>> ActionHandlers =
            new Dictionary<string, Func<JObject, object>>
            {
                { "status", p => Status(p?["job_id"]?.ToString() ?? p?["id"]?.ToString()) },
                { "list", _ => ListJobs() },
                { "cancel", p => Cancel(
                    p?["job_id"]?.ToString() ?? p?["id"]?.ToString(),
                    p?["reason"]?.ToString()) },
            };

        private static readonly Dictionary<string, string> ActionAliases =
            new Dictionary<string, string> { { "check", "status" } };

        public static object HandleCommand(JObject @params)
            => ActionRouter.Route(@params, ActionHandlers, ActionAliases);

        private static object Status(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
                return Response.Error("manage_job.status requires a 'job_id'.");

            switch (DetachedJobs.Check(jobId, out string json))
            {
                case DetachedJobs.Status.Complete:
                    return Response.Success("Job complete.",
                        new { id = jobId, status = "complete", result = ParseOrRaw(json) });
                case DetachedJobs.Status.Pending:
                    return Response.Success("Job still running.",
                        new { id = jobId, status = "pending" });
            }

            // Not in the detached store — an attached job in flight still shows up here.
            if (JobRegistry.TryGet(jobId, out var handle))
                return Response.Success("Job still running (attached).",
                    new { id = jobId, status = "running", job = handle.Name, runner = handle.Runner });

            return Response.Error(
                $"Unknown job id '{jobId}' (never existed, already collected, or expired).");
        }

        private static object ListJobs()
        {
            var jobs = new List<object>();
            foreach (var handle in JobRegistry.Snapshot())
            {
                jobs.Add(new
                {
                    id = handle.JobId,
                    job = handle.Name,
                    runner = handle.Runner,
                    request_id = handle.RequestId,
                    detached = handle.Detached,
                });
            }
            return Response.Success($"{jobs.Count} job(s) in flight.", new { jobs });
        }

        private static object Cancel(string jobId, string reason)
        {
            if (string.IsNullOrEmpty(jobId))
                return Response.Error("manage_job.cancel requires a 'job_id'.");

            var cancelReason = string.IsNullOrEmpty(reason) ? "client aborted" : reason;
            bool known = JobControl.RequestCancel(jobId, cancelReason);
            if (!known)
            {
                // Still queued briefly in case enrollment races the cancel — report soft success
                // so the client does not retry forever on a finished job.
                return Response.Success(
                    $"Cancel requested for job '{jobId}' (no matching in-flight job; request queued briefly).",
                    new { id = jobId, status = "cancel_requested", known = false });
            }

            return Response.Success(
                $"Cancel requested for job '{jobId}'.",
                new { id = jobId, status = "cancel_requested", known = true });
        }

        private static object ParseOrRaw(string json)
        {
            try { return JToken.Parse(json); }
            catch { return json; }
        }
    }
}
