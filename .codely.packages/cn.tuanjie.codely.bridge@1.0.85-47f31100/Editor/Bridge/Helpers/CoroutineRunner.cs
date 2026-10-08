using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Drives coroutines to completion on the editor loop, one MoveNext per frame. A coroutine
    /// cannot return a value, so it reports its outcome through the JobContext handed to it
    /// (<c>ctx.SetResult(Response)</c> / <c>ctx.SetError(Response)</c>). Result and Error are optional
    /// Response objects — null means the runner synthesizes a Response from the job name.
    ///
    /// Supported yields:
    /// <list type="bullet">
    ///   <item><see cref="IEnumerator"/> / non-string <see cref="IEnumerable"/> — nested until complete</item>
    ///   <item><see cref="WaitForSeconds"/> — scaled time in Play Mode; editor clock otherwise</item>
    ///   <item><see cref="CustomYieldInstruction"/> — <c>keepWaiting</c>
    ///     (covers <see cref="WaitForSecondsRealtime"/>, <see cref="WaitUntil"/>, <see cref="WaitWhile"/>)</item>
    ///   <item><see cref="WaitForEndOfFrame"/> / <see cref="WaitForFixedUpdate"/> / null — one editor frame</item>
    /// </list>
    ///
    /// Usage:
    ///   var ctx = CoroutineRunner.CreateJob(requestId, "manage_input");
    ///   CoroutineRunner.RunJob(ctx, SomeCoroutine(ctx));   // pass ctx in so it can set a result
    /// The top-level loop calls CoroutineRunner.Tick() each frame; the finished job's response is
    /// enqueued to the client automatically.
    /// </summary>
    public static class CoroutineRunner
    {
        private static readonly Impl _impl = new Impl();

        // WaitForSeconds stores duration in a private field — no public API.
        private static readonly FieldInfo s_WaitForSecondsField =
            typeof(WaitForSeconds).GetField("m_Seconds", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Creates a job/context bound to a native request id, with a name for the response.
        /// Pass detached: true to memorize the result for later polling instead of responding.
        /// </summary>
        public static JobContext CreateJob(ulong requestId, string name, bool detached = false)
            => _impl.CreateJob(requestId, name, detached);

        /// <summary>Enrolls a coroutine to be driven to completion under the given job.</summary>
        public static void RunJob(JobContext ctx, IEnumerator routine,
                                  int timeoutSeconds = JobRunnerBase.DefaultTimeoutSeconds)
            => _impl.Run(ctx, routine, timeoutSeconds);

        /// <summary>Advances all in-flight coroutines by one step; called each editor frame.</summary>
        public static void Tick() => _impl.Tick();

        /// <summary>Fails and responds to every in-flight coroutine (shutdown / play-mode exit).</summary>
        public static void CancelAll(string reason = "Operation canceled.") => _impl.CancelAll(reason);

        /// <summary>
        /// Stack of nested enumerators for one job, plus any active time / custom wait.
        /// Stored as <see cref="JobRunnerBase.Job.Work"/>.
        /// </summary>
        private sealed class NestedRoutine
        {
            public readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();

            /// <summary>When set, the top coroutine is suspended until this clock time.</summary>
            public double? WaitDeadline;

            /// <summary>True → compare <see cref="WaitDeadline"/> against <see cref="Time.time"/> (scaled).</summary>
            public bool WaitUsesScaledTime;

            /// <summary>Active <see cref="CustomYieldInstruction"/> (Realtime / Until / While).</summary>
            public CustomYieldInstruction CustomWait;

            public NestedRoutine(IEnumerator root) => Stack.Push(root);
        }

        private sealed class Impl : JobRunnerBase
        {
            protected override string RunnerName => "coroutine";

            public void Run(JobContext ctx, IEnumerator routine, int timeoutSeconds)
                => Enroll(ctx, new NestedRoutine(routine), timeoutSeconds);

            // Dispose every enumerator still on the stack when the job leaves the registry. This
            // runs each iterator's finally blocks — the only way they execute when a coroutine
            // faults, times out, or is cancelled mid-run (a caught throw does not). On normal
            // completion the stack is already empty, so this is a no-op.
            protected override void Cleanup(Job job)
            {
                if (!(job.Work is NestedRoutine nested))
                    return;
                while (nested.Stack.Count > 0)
                {
                    var routine = nested.Stack.Pop();
                    try { (routine as IDisposable)?.Dispose(); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CoroutineRunner] Enumerator dispose failed: {ex.Message}");
                    }
                }
            }

            protected override Step Advance(Job job)
            {
                var nested = (NestedRoutine)job.Work;

                while (nested.Stack.Count > 0)
                {
                    // ── active WaitForSeconds ──────────────────────────────────────────
                    if (nested.WaitDeadline.HasValue)
                    {
                        double now = nested.WaitUsesScaledTime
                            ? Time.time
                            : EditorApplication.timeSinceStartup;
                        if (now < nested.WaitDeadline.Value)
                            return Step.Running;
                        nested.WaitDeadline = null;
                        // fall through — resume parent MoveNext
                    }

                    // ── active CustomYieldInstruction (WaitForSecondsRealtime / Until / While)
                    if (nested.CustomWait != null)
                    {
                        if (nested.CustomWait.keepWaiting)
                            return Step.Running;
                        nested.CustomWait = null;
                        // fall through — resume parent MoveNext
                    }

                    var top = nested.Stack.Peek();
                    if (!top.MoveNext())
                    {
                        nested.Stack.Pop();
                        continue; // resume parent immediately
                    }

                    var current = top.Current;

                    // CustomYieldInstruction is also IEnumerator — handle via keepWaiting first.
                    if (current is CustomYieldInstruction custom)
                    {
                        nested.CustomWait = custom;
                        return Step.Running;
                    }

                    if (TryGetNestedEnumerator(current, out var child))
                    {
                        nested.Stack.Push(child);
                        continue; // start child immediately
                    }

                    if (current is WaitForSeconds waitForSeconds)
                    {
                        float seconds = GetWaitForSeconds(waitForSeconds);
                        // Match Unity: scaled Time.time in Play Mode; editor clock in Edit Mode
                        // (Time.time does not advance while the editor is not playing).
                        if (Application.isPlaying)
                        {
                            nested.WaitUsesScaledTime = true;
                            nested.WaitDeadline = Time.time + seconds;
                        }
                        else
                        {
                            nested.WaitUsesScaledTime = false;
                            nested.WaitDeadline = EditorApplication.timeSinceStartup + seconds;
                        }
                        return Step.Running;
                    }

                    // null / WaitForEndOfFrame / WaitForFixedUpdate / anything else → one frame
                    return Step.Running;
                }

                return Step.Finished;
            }

            private static float GetWaitForSeconds(WaitForSeconds wait)
            {
                if (s_WaitForSecondsField != null)
                    return (float)s_WaitForSecondsField.GetValue(wait);
                return 0f;
            }

            // Unity treats yielded IEnumerators (and IEnumerables) as nested coroutines.
            // Strings are IEnumerable — do not nest them.
            private static bool TryGetNestedEnumerator(object current, out IEnumerator child)
            {
                child = null;
                if (current == null || current is string)
                    return false;

                if (current is IEnumerator asEnumerator)
                {
                    child = asEnumerator;
                    return true;
                }

                if (current is IEnumerable asEnumerable)
                {
                    child = asEnumerable.GetEnumerator();
                    return child != null;
                }

                return false;
            }
        }
    }
}
