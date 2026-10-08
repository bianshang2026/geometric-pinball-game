using System.Threading.Tasks;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Drives Tasks to completion on the editor loop. On success the Task's result is treated as
    /// an optional <see cref="Response"/> (null → JobContext synthesizes from the job name).
    /// On fault/cancel the context is marked failed with a Response.Error.
    ///
    /// Usage:
    ///   var ctx = AsyncTaskRunner.CreateJob(requestId, "execute_csharp_script");
    ///   AsyncTaskRunner.RunJob(ctx, SomeAsyncMethod()); // Task&lt;object&gt; may return Response.Success(...)
    /// The top-level loop calls AsyncTaskRunner.Tick() each frame; the finished job's response is
    /// enqueued to the client automatically.
    /// </summary>
    public static class AsyncTaskRunner
    {
        private static readonly Impl _impl = new Impl();

        /// <summary>
        /// Creates a job/context bound to a native request id, with a name for the response.
        /// Pass detached: true to memorize the result for later polling instead of responding.
        /// </summary>
        public static JobContext CreateJob(ulong requestId, string name, bool detached = false)
            => _impl.CreateJob(requestId, name, detached);

        /// <summary>Enrolls a Task to be driven to completion under the given job.</summary>
        public static void RunJob(JobContext ctx, Task task,
                                  int timeoutSeconds = JobRunnerBase.DefaultTimeoutSeconds)
            => _impl.Run(ctx, task, timeoutSeconds);

        /// <summary>Advances all in-flight Tasks by one step; called each editor frame.</summary>
        public static void Tick() => _impl.Tick();

        /// <summary>Fails and responds to every in-flight Task (shutdown / play-mode exit).</summary>
        public static void CancelAll(string reason = "Operation canceled.") => _impl.CancelAll(reason);

        private sealed class Impl : JobRunnerBase
        {
            protected override string RunnerName => "task";

            public void Run(JobContext ctx, Task task, int timeoutSeconds)
                => Enroll(ctx, task, timeoutSeconds);

            protected override Step Advance(Job job)
            {
                var task = (Task)job.Work;
                if (!task.IsCompleted) return Step.Running;

                if (task.IsFaulted)
                {
                    var ex = task.Exception?.InnerException ?? task.Exception;
                    if (!job.Ctx.HasOutcome)
                        job.Ctx.SetError(Response.Error(SafeError(ex)));
                    return Step.Finished;
                }
                if (task.IsCanceled)
                {
                    if (!job.Ctx.HasOutcome)
                        job.Ctx.SetError(Response.Error("Task was canceled."));
                    return Step.Finished;
                }

                // Work may have already called SetResult/SetError (e.g. ScheduleTask wrapper).
                if (job.Ctx.HasOutcome)
                    return Step.Finished;

                var raw = UnwrapTaskResult(task);
                if (raw == null || JobContext.IsResponse(raw))
                {
                    // A returned Response may itself be an error (success:false) — e.g. a script that
                    // threw inside its awaited Task. Route it to SetError so the job is marked failed,
                    // not silently reported as a success outcome.
                    if (raw is System.Collections.IDictionary dict && Equals(dict["success"], false))
                        job.Ctx.SetError(raw);
                    else
                        job.Ctx.SetResult(raw); // null → synthesized from job name
                }
                else
                    job.Ctx.SetResult(Response.Success(
                        job.Ctx.Name ?? "Operation completed.",
                        new { job = job.Ctx.Name, id = job.Ctx.JobId, result = raw }));

                return Step.Finished;
            }

            // Task<T>.Result via reflection (T is unknown here); non-generic Task has no result.
            private static object UnwrapTaskResult(Task task)
            {
                var type = task.GetType();
                return type.IsGenericType ? type.GetProperty("Result")?.GetValue(task) : null;
            }
        }
    }
}
