using System.Collections.Generic;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools.Jobs
{
    /// <summary>
    /// Step jobs behind the <c>manage_editor</c> actions that used to answer "pending" and make the
    /// client poll. Each now runs to the real end state — compilation finished, editor idle, play
    /// mode entered/paused/stopped — and answers once, on the original request.
    ///
    /// They are <see cref="StepJob"/>s rather than coroutines because every one of these waits can
    /// span a domain reload: compiling triggers one by definition, and entering or leaving play
    /// mode does too unless the project disabled domain reload. A coroutine would die there.
    ///
    /// Responses carry no operation id: there is nothing for a client to correlate or poll against
    /// once a call only returns when its work is done. Failures are message-only — the client
    /// renders an async response as a success whenever it carries a <c>data</c> payload, so error
    /// details go in the message.
    /// </summary>
    internal static class CompileResult
    {
        /// <summary>
        /// Builds the completed-compilation payload the way the old wait_for_compile did, from
        /// whatever diagnostics the editor can still report after the reload.
        ///
        /// Everything the caller needs goes in the <c>data</c> payload and nowhere else: the client
        /// unwraps one level (<c>response.data</c>) and reads the result from there, so a key put
        /// beside <c>data</c> — a pipeline hint, for one — never reaches the caller. For the
        /// same reason <c>data</c> carries no <c>success</c> key: the client reads a nested
        /// <c>success:false</c> as "the command failed" and renders it with a generic message,
        /// losing the diagnostics. Compile outcome is reported as <c>hasErrors</c> instead, and a
        /// genuine failure is raised through <see cref="StepJob.Fail"/>.
        /// </summary>
        public static object Build(bool compileObserved, bool includeConsole = false)
        {
            var errors = CompilationHelper.GetCompilationErrors();
            var warnings = CompilationHelper.GetCompilationWarnings();
            JObject console = includeConsole
                ? ReadConsole.ReadForCompilationPipeline()
                : new JObject
                {
                    ["included"] = false,
                };
            bool consoleReadSucceeded =
                includeConsole && console["read_success"]?.ToObject<bool?>() == true;

            var data = new Dictionary<string, object>
            {
                ["status"] = "completed",
                ["pipeline_kind"] = "compile",
                // Default pipeline calls include the effective post-compile Console view and
                // report whether Collapse is active, so callers no longer need a second
                // unity_console.get. If inclusion was explicitly disabled or the internal read
                // failed, retain the compatibility hint for a manual fallback.
                ["requires_console_validation"] = !consoleReadSucceeded,
                ["console"] = console,
                // False means compilation never started while we watched. The counts below are then
                // the previous build's, so the caller must not read them as "this edit is clean".
                ["compile_observed"] = compileObserved,
            };
            if (errors.HasValue)
            {
                data["errors"] = errors.Value;
                data["hasErrors"] = errors.Value > 0;
            }
            if (warnings.HasValue)
            {
                data["warnings"] = warnings.Value;
                data["hasWarnings"] = warnings.Value > 0;
            }

            string message;
            if (!compileObserved)
            {
                message = "No compilation was observed — the scripts were already up to date, or " +
                          "the editor never started one. Any error/warning counts below are from " +
                          "the previous build; validate via console.";
            }
            else
            {
                message = errors.HasValue
                    ? (errors.Value > 0 ? "Compilation completed with errors" : "Compilation completed successfully")
                    : "Compilation completed (validate via console)";
            }

            if (includeConsole && !consoleReadSucceeded)
                message += " The post-compile console could not be embedded; read unity_console as a fallback.";

            return Response.Success(message, data);
        }

        /// <summary>
        /// The <c>data</c> payload of a compile response, which is where every field lives. Returns
        /// the response itself when it has no payload, so callers can read it either way.
        /// </summary>
        public static JObject PayloadOf(JObject response)
            => response?["data"] as JObject ?? response;
    }

    /// <summary>
    /// Stops play mode, refreshes the Asset Database, waits for importing and compilation to become idle, then reads
    /// diagnostics on a later editor frame. A <see cref="StepJob"/> is required because Unity may
    /// not publish the latest compile errors and warnings to the Console until after the frame in
    /// which <see cref="AssetDatabase.Refresh(ImportAssetOptions)"/> returns.
    /// </summary>
    public class RefreshJob : StepJob
    {
        public bool RefreshStarted;

        protected override JobStep[] BuildSteps() => new[]
        {
            JobStep.Nested("ensure-edit-mode",
                () => new StopPlayModeJob(),
                onComplete: () =>
                {
                    if (!LastSubJobSucceeded)
                        CompleteWithJson(LastSubJobResponseJson, failed: true);
                },
                skip: PlayModeState.IsStopped),
            new JobStep("refresh", Refresh),
            new JobStep("await-editor-idle", null,
                () => !CompilationHelper.IsCompiling() && !EditorApplication.isUpdating),
            new JobStep("read-diagnostics", Report),
        };

        private void Refresh()
        {
            if (RefreshStarted) return;
            RefreshStarted = true;

            try
            {
                ReadConsole.HandleCommand(new JObject { ["action"] = "clear" });
                CompilationHelper.ResetCounts();
                CompilationHelper.EnsureScenesSavedBeforeReload();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            catch (System.Exception e)
            {
                CodelyLogger.LogError($"[ManageEditor] Refresh failed: {e}");
                FailWith(Response.ErrorWithCode(
                    "refresh_failed",
                    $"Asset database refresh failed: {e.Message}"));
            }
        }

        private void Report()
        {
            int? errors = CompilationHelper.GetCompilationErrors();
            int? warnings = CompilationHelper.GetCompilationWarnings();
            JObject console = ReadConsole.ReadForCompilationPipeline();
            bool consoleReadSucceeded = console["read_success"]?.ToObject<bool?>() == true;

            var data = new Dictionary<string, object>
            {
                ["status"] = "completed",
                ["pipeline_kind"] = "refresh",
                ["requires_console_validation"] = !consoleReadSucceeded,
                ["console"] = console,
            };
            if (errors.HasValue)
            {
                data["errors"] = errors.Value;
                data["hasErrors"] = errors.Value > 0;
            }
            if (warnings.HasValue)
            {
                data["warnings"] = warnings.Value;
                data["hasWarnings"] = warnings.Value > 0;
            }

            string message = errors.HasValue && errors.Value > 0
                ? "Asset database refresh completed with errors."
                : "Asset database refresh completed successfully.";
            if (!consoleReadSucceeded)
                message += " The console could not be embedded; read unity_console as a fallback.";
            Complete(Response.Success(message, data));
        }
    }

    /// <summary>
    /// Waits until the editor is neither compiling nor importing, then reports the compilation
    /// result. Runs as the second half of <see cref="CompilePipelineJob"/>.
    /// </summary>
    public class WaitForCompileJob : StepJob
    {
        /// <summary>
        /// How long to keep looking for compilation to start before concluding it already finished
        /// (or never began). Wall-clock rather than a frame count: Unity flips isCompiling only
        /// after the asset refresh that precedes it, and on a large project that refresh runs for
        /// hundreds of frames, so a frame count expires long before the compile it was waiting for
        /// begins — answering "compilation completed successfully" with the previous build's error
        /// counts, before a single script has been rebuilt.
        ///
        /// The default is short so a bare wait ("is the editor compiling right now?") on an idle
        /// editor answers the honest "no" without stalling for the long window.
        /// <see cref="CompilePipelineJob"/>, which has just requested a compile and therefore
        /// knows one is coming, raises it well past Unity's start-up lag.
        /// </summary>
        public double SettleSeconds = 2;

        /// <summary>
        /// Session-clock deadline for the detect phase, pushed forward for as long as the editor is
        /// updating so the window only counts down against an idle editor.
        /// </summary>
        public double SettleUntil;

        public bool SawCompiling;
        public bool IncludeConsole;

        protected override JobStep[] BuildSteps() => new[]
        {
            // Do not report "done" in the gap between requesting a compile and Unity actually
            // starting one — that would answer before a single script had been rebuilt.
            new JobStep("detect-compile",
                () =>
                {
                    // An import/refresh still in flight is what compilation comes out of, so the
                    // settle window does not start counting down until it ends — see SettleWindow.
                    SettleUntil = SettleWindow.Advance(
                        SettleUntil, EditorApplication.timeSinceStartup, SettleSeconds,
                        EditorApplication.isUpdating);
                    if (CompilationHelper.IsCompiling()) SawCompiling = true;
                },
                () => SawCompiling
                      || SettleWindow.Expired(SettleUntil, EditorApplication.timeSinceStartup,
                                              EditorApplication.isUpdating)),

            new JobStep("await-compile", null,
                () => !CompilationHelper.IsCompiling() && !EditorApplication.isUpdating),

            new JobStep("report", () => Complete(CompileResult.Build(SawCompiling, IncludeConsole))),
        };
    }

    /// <summary>
    /// The full compile pipeline: clear console, request compilation, then wait for it to finish —
    /// the second half as a nested <see cref="WaitForCompileJob"/>. Backs
    /// <c>start_compilation_pipeline</c> (alias <c>request_compile</c>), which can clear the
    /// script assembly cache first.
    /// </summary>
    public class CompilePipelineJob : StepJob
    {
        public bool ClearCache;
        public bool IncludeConsole = true;
        public bool CacheCleared;
        public bool Started;

        protected override JobStep[] BuildSteps() => new[]
        {
            new JobStep("start-pipeline", StartPipeline),

            JobStep.Nested("await-compile",
                // We have just requested this compile, so wait properly for Unity to get around to
                // starting it rather than concluding after a couple of idle seconds that it never
                // will — see WaitForCompileJob.SettleSeconds.
                () => new WaitForCompileJob
                {
                    SettleSeconds = 30,
                    IncludeConsole = IncludeConsole,
                },
                onComplete: ReportFromSubJob),
        };

        private void StartPipeline()
        {
            if (Started) return;
            Started = true;

            var started = CompilationHelper.StartCompilationPipeline(ClearCache);
            if (started == null)
            {
                Fail("The compilation pipeline returned no response.");
                return;
            }

            var json = started as JObject ?? JObject.FromObject(started);

            if (json["success"]?.ToObject<bool?>() == false)
            {
                // Play-mode block and other refusals: message-only so the client renders a failure.
                Fail(json["message"]?.ToString()
                     ?? json["error"]?.ToString()
                     ?? "Failed to start the compilation pipeline.");
                return;
            }

            CacheCleared = json?["cache_cleared"]?.ToObject<bool?>() ?? false;
        }

        // Forward the nested wait's result as this job's own, annotated with what the start step
        // learned (whether the cache was cleared). The annotations go into the `data` payload,
        // which is the only part of the response the client unwraps to — see CompileResult.Build.
        private void ReportFromSubJob()
        {
            if (!LastSubJobSucceeded)
            {
                CompleteWithJson(LastSubJobResponseJson, failed: true);
                return;
            }

            var result = LastSubJobResponse;
            var payload = CompileResult.PayloadOf(result);
            if (payload == null)
            {
                Complete(CompileResult.Build(
                    compileObserved: true,
                    includeConsole: IncludeConsole));
                return;
            }

            if (ClearCache) payload["cache_cleared"] = CacheCleared;
            // Lets a caller tell "the compile ran and something went wrong" apart from "the
            // pipeline was refused before compiling" — only the former is worth retrying.
            payload["compile_started"] = true;

            CompleteWithJson(result.ToString(Formatting.None));
        }
    }

    /// <summary>
    /// Waits until every other in-flight bridge job — async task, step, and coroutine alike, as
    /// enrolled in <see cref="JobRegistry"/> — has finished, and the editor is neither compiling
    /// nor importing. Step-job form so the wait survives any domain reload the outstanding work
    /// triggers.
    ///
    /// The job is itself registered in the registry it watches, so jobs enrolled under
    /// <see cref="CommandName"/> are excluded from the wait — that covers this job and any
    /// concurrent wait_for_idle, which would otherwise hold each other in flight forever.
    /// </summary>
    public class WaitForIdleJob : StepJob
    {
        /// <summary>Registry name wait_for_idle jobs enroll under; excluded from the wait.</summary>
        public const string CommandName = "wait_for_idle";

        public double StartedAt;

        protected override JobStep[] BuildSteps() => new[]
        {
            new JobStep("await-idle",
                () => { if (StartedAt <= 0) StartedAt = EditorApplication.timeSinceStartup; },
                () => PendingJobs() == 0
                      && !CompilationHelper.IsCompiling()
                      && !EditorApplication.isUpdating),

            new JobStep("report", () => Complete(Response.Success(
                "Editor is idle: no jobs in flight, not compiling, not importing.",
                new
                {
                    pendingJobs = 0,
                    isCompiling = false,
                    isUpdating = false,
                    elapsed = StartedAt > 0 ? EditorApplication.timeSinceStartup - StartedAt : 0,
                }))),
        };

        private static int PendingJobs()
        {
            int count = 0;
            foreach (var handle in JobRegistry.Snapshot())
            {
                if (handle.Name != CommandName) count++;
            }
            return count;
        }
    }

    /// <summary>Enters play mode and answers once the editor is actually playing.</summary>
    public class PlayJob : StepJob
    {
        public bool Issued;
        public bool WasAlreadyPlaying;

        /// <summary>
        /// Frames to allow for the transition to begin. Entering play mode is refused outright when
        /// scripts do not compile, and Unity reports that only by leaving both isPlaying and
        /// isPlayingOrWillChangePlaymode false — there is no failure callback to hook.
        /// </summary>
        public int GraceFrames = 30;

        protected override JobStep[] BuildSteps() => new[]
        {
            new JobStep("enter-play", () =>
            {
                if (Issued) return;
                Issued = true;
                WasAlreadyPlaying = EditorApplication.isPlaying;
                if (WasAlreadyPlaying) return;

                // Force the player loop to run at full speed even when the editor is not the
                // focused OS window; without this Time.frameCount barely advances in the
                // background. The runtime resets it when play mode ends.
                Application.runInBackground = true;
                EditorApplication.isPlaying = true;
            }),

            new JobStep("await-playing", () =>
            {
                if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                if (--GraceFrames <= 0)
                    Fail("Failed to enter play mode: the editor never started playing. " +
                         "This usually means scripts failed to compile — check the console.");
            },
            () => EditorApplication.isPlaying && !EditorApplication.isCompiling),

            new JobStep("report", () => Complete(Response.Success(
                WasAlreadyPlaying ? "Already in play mode." : "Entered play mode.",
                new { playMode = "playing" }))),
        };
    }

    /// <summary>
    /// Sets the requested pause state and answers once the editor reaches it. Legacy pause
    /// requests omit an explicit target and retain the original toggle behavior.
    /// </summary>
    public class PauseJob : StepJob
    {
        public bool Issued;
        public bool TogglePaused;
        public bool TargetPaused;

        protected override JobStep[] BuildSteps() => new[]
        {
            new JobStep("set-pause", () =>
            {
                if (Issued) return;
                Issued = true;

                if (!EditorApplication.isPlaying)
                {
                    Fail("Cannot pause/resume: Not in play mode.");
                    return;
                }

                if (TogglePaused)
                    TargetPaused = !EditorApplication.isPaused;
                EditorApplication.isPaused = TargetPaused;
            }),

            new JobStep("await-pause", null,
                () => EditorApplication.isPaused == TargetPaused),

            new JobStep("report", () => Complete(Response.Success(
                TargetPaused ? "Game paused." : "Game resumed.",
                new { playMode = TargetPaused ? "paused" : "playing" }))),
        };
    }

    /// <summary>Leaves play mode and answers once the editor is back in edit mode.</summary>
    public class StopPlayModeJob : StepJob
    {
        public bool Issued;
        public bool WasPlaying;

        protected override JobStep[] BuildSteps() => new[]
        {
            new JobStep("request-stop", () =>
            {
                if (Issued) return;
                Issued = true;

                WasPlaying = !PlayModeState.IsStopped();
                if (WasPlaying) EditorApplication.isPlaying = false;
            }),

            new JobStep("await-stop", null, PlayModeState.IsStopped),

            new JobStep("report", () => Complete(Response.Success(
                WasPlaying ? "Exited play mode." : "Already stopped (not in play mode).",
                new { playMode = "stopped" }))),
        };
    }

    internal static class PlayModeState
    {
        /// <summary>
        /// True once play mode is fully over. isPlayingOrWillChangePlaymode stays true through the
        /// teardown, and exiting play mode reloads the domain, so the compile check keeps the job
        /// from answering while the editor is still rebuilding.
        /// </summary>
        public static bool IsStopped()
            => !EditorApplication.isPlaying
               && !EditorApplication.isPlayingOrWillChangePlaymode
               && !EditorApplication.isCompiling;
    }
}
