using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Codely.Newtonsoft.Json.Linq;
using Codely.Microsoft.CodeAnalysis;
using Codely.Microsoft.CodeAnalysis.Emit;
using Codely.Microsoft.CodeAnalysis.CSharp;
using Codely.Microsoft.CodeAnalysis.CSharp.Scripting;
using Codely.Microsoft.CodeAnalysis.CSharp.Syntax;
using Codely.Microsoft.CodeAnalysis.Scripting;
using UnityEngine;
using UnityEditor;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools
{
    public static class ExecuteCSharpScript
    {
        static readonly List<string> s_CapturedLogs = new List<string>();
        static bool s_IsCapturingLogs;

        // REPL session state. All static so a domain reload (which wipes static fields) is
        // equivalent to session destruction with no explicit cleanup needed. Guarded by the fact
        // that every tool call runs synchronously on the Unity main thread — no locking needed.
        static ScriptState<object> s_SessionState;
        static string s_SessionId;
        static int s_SubmissionCount;
        static List<string> s_SessionImports;

        static void ResetSession()
        {
            s_SessionState = null;
            s_SessionId = null;
            s_SubmissionCount = 0;
            s_SessionImports = null;
        }

        // Exposes the active session id so `beforeAssemblyReload` can push a destruction
        // notification before the reload wipes this class's static session fields.
        public static string ActiveSessionId => s_SessionState != null ? s_SessionId : null;

        // True while a one-shot execution (enable_repl:false) is running its script. Repl.Vars uses
        // this to stay empty during a one-shot even when a session exists in the background --
        // one-shot must not read session state, only bypass it.
        static bool s_InOneShotExecution;

        internal static ScriptState<object> CurrentSessionStateForVars =>
            s_InOneShotExecution ? null : s_SessionState;

        static readonly string s_ShadowCopyDir = Path.Combine(
            Application.temporaryCachePath,
            "CodelyScriptRefs"
        );

        static readonly string[] s_ShadowCopyAssemblyNames =
        {
            "Assembly-CSharp",
            "Assembly-CSharp-Editor"
        };

        static readonly List<ScriptFixProvider> s_FixProviders = new List<ScriptFixProvider>
        {
            new FixMissingImports(),
            new FixMissingAssemblyReference(),
            new FixUnqualifiedUnityStaticMethod(),
            new FixMissingParenthesis(),
            new FixMissingBrace(),
            new FixMissingSquareBracket(),
            new FixMissingSemicolon(),
            new FixAmbiguousReference()
        };

        const int k_MaxFixIterations = 50;
        const string k_TextMeshProUGUIKeyword = "TextMeshProUGUI";
        const string k_TmpSettingsAssetPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";

        // The 11th item is only used to detect "there's more" — it is never itself displayed.
        const int k_MaxCollectionPreviewItems = 11;
        const int k_MaxCollectionPreviewChars = 2000;
        const int k_MaxResultChars = 8000;

        /// <summary>
        /// Hard cap for async Task / IEnumerator job deadlines (seconds).
        /// Callers may opt into no deadline with an explicit <c>timeoutSeconds: 0</c>.
        /// </summary>
        public const int MaxAsyncTimeoutSeconds = 3600;

        /// <summary>
        /// Request-scoped deadline applied to ScheduleTask / ScheduleCoroutine (and session
        /// counterparts). Set at the start of <see cref="HandleCommand"/>; main-thread only.
        /// </summary>
        static int s_AsyncTimeoutSeconds = JobRunnerBase.DefaultTimeoutSeconds;

        // Split Edit Mode/Play Mode commands keep the native request on an outer StepJob. Any inner
        // Task/coroutine must therefore run detached on request 0 and be collected by that outer
        // job. These fields are request-scoped on the editor main thread and are restored before
        // HandleCommandForOrchestration returns.
        static bool m_OrchestrationDelivery;
        static string m_OrchestrationCommandName;

        internal static string DisplayCommandName =>
            !string.IsNullOrEmpty(m_OrchestrationCommandName)
                ? m_OrchestrationCommandName
                : (string.IsNullOrEmpty(CommandContext.CommandType)
                    ? "execute_csharp_script"
                    : CommandContext.CommandType);

        internal static object HandleCommandForOrchestration(JObject @params, string commandName)
        {
            bool previousDelivery = m_OrchestrationDelivery;
            string previousName = m_OrchestrationCommandName;
            m_OrchestrationDelivery = true;
            m_OrchestrationCommandName = commandName;
            try
            {
                return HandleCommand(@params);
            }
            finally
            {
                m_OrchestrationDelivery = previousDelivery;
                m_OrchestrationCommandName = previousName;
            }
        }

        static JobContext CreateTaskJob()
            => AsyncTaskRunner.CreateJob(
                m_OrchestrationDelivery ? 0 : CommandContext.RequestId,
                DisplayCommandName,
                detached: m_OrchestrationDelivery);

        static JobContext CreateCoroutineJob()
            => CoroutineRunner.CreateJob(
                m_OrchestrationDelivery ? 0 : CommandContext.RequestId,
                DisplayCommandName,
                detached: m_OrchestrationDelivery);

        /// <summary>
        /// Resolve the bounded deadline for Task / IEnumerator jobs.
        /// Omitted / invalid → <see cref="JobRunnerBase.DefaultTimeoutSeconds"/> (300).
        /// Explicit 0 → no deadline (opt-in infinite). Positive values are capped at
        /// <see cref="MaxAsyncTimeoutSeconds"/>.
        /// </summary>
        internal static int ResolveAsyncTimeoutSeconds(JObject @params)
        {
            var token = @params?["timeoutSeconds"];
            if (token == null || token.Type == Codely.Newtonsoft.Json.Linq.JTokenType.Null)
                return JobRunnerBase.DefaultTimeoutSeconds;

            int? requested = token.ToObject<int?>();
            if (requested == null)
                return JobRunnerBase.DefaultTimeoutSeconds;

            if (requested.Value == 0)
                return 0; // explicit opt-in: never time out

            if (requested.Value < 0)
                return JobRunnerBase.DefaultTimeoutSeconds;

            return requested.Value > MaxAsyncTimeoutSeconds
                ? MaxAsyncTimeoutSeconds
                : requested.Value;
        }

        public static object HandleCommand(JObject @params)
        {
            s_AsyncTimeoutSeconds = ResolveAsyncTimeoutSeconds(@params);

            string script = @params["script"]?.ToString();
            string scriptPath = @params["script_path"]?.ToString();
            string description =
                (@params["summary"] ?? @params["description"])?.ToString();

            // At least one of script or script_path must be provided
            if (string.IsNullOrEmpty(script) && string.IsNullOrEmpty(scriptPath))
                return Response.Error("'script' parameter is required.");

            // Enforce the caller's declared execution mode against Unity's actual play state.
            // 'play' requires play mode, 'editor' requires edit mode; omitted means either is fine.
            string executionMode = @params["execution_mode"]?.ToString();
            if (!string.IsNullOrEmpty(executionMode))
            {
                bool isPlaying = UnityEditor.EditorApplication.isPlaying;
                // These branches refuse execution: the script never runs, so they
                // must return an error rather than a misleading success response.
                switch (executionMode)
                {
                    case "play":
                        if (!isPlaying)
                            return Response.Error(
                                "execution_mode is 'play' but Unity is not in play mode. " +
                                "Enter play mode before running this script.");
                        break;
                    case "editor":
                        if (isPlaying)
                            return Response.Error(
                                "execution_mode is 'editor' but Unity is in play mode. " +
                                "Exit play mode before running this script.");
                        break;
                    default:
                        return Response.Error(
                            $"Invalid execution_mode '{executionMode}'. Expected 'play', 'editor', or omitted.");
                }
            }

            bool enableRepl = @params["enable_repl"]?.ToObject<bool>() ?? false;
            bool unlockDomainReload = @params["unlock_domain_reload"]?.ToObject<bool>() ?? false;
            JObject recordGameView = @params["record_game_view"] as JObject;
            if (@params["record_game_view"] != null && recordGameView == null)
                return Response.Error("'record_game_view' must be an object.");
            if (recordGameView != null &&
                !ManageScreenshot.CanAttachExecuteCSharpGameViewRecording() &&
                ManageScreenshot.TryGetActiveGameViewMp4RecordingConflict(out object recordingConflict))
                return recordingConflict;
            if (recordGameView == null && ManageScreenshot.HasGameViewMp4Recording)
            {
                if (!ManageScreenshot.HasSplitGameViewMp4Recording)
                    return Response.Error(
                        $"Another {DisplayCommandName} command owns the active Game View MP4 recording. " +
                        "Wait for that command to finish and collect its recording automatically.");

                return Response.Error(
                    $"{DisplayCommandName} cannot run inside a separately started Game View MP4 recording. " +
                    "The split start-recording -> execute-script -> finish-recording workflow can miss " +
                    "short-lived effects because compilation and tool-call latency consume the recording window. " +
                    "Collect the current recording with finish_game_view_recording and its recording_id without " +
                    "triggering the effect, then retry this one-shot script with record_game_view so recording " +
                    "starts after compilation and immediately before user code executes.");
            }
            if (recordGameView != null && enableRepl)
                return Response.Error(
                    "'record_game_view' is currently supported only for one-shot execution (enable_repl:false).");
            if (recordGameView != null && !EditorApplication.isPlaying)
                return Response.Error("'record_game_view' requires Unity to already be in Play Mode.");
            var scriptSessionIdToken = @params["script_session_id"];
            bool scriptSessionIdProvided = scriptSessionIdToken != null;
            bool scriptSessionIdIsNewSentinel = scriptSessionIdProvided &&
                (scriptSessionIdToken.Type == Codely.Newtonsoft.Json.Linq.JTokenType.Null ||
                 scriptSessionIdToken.ToString() == "new");
            string scriptSessionId = scriptSessionIdProvided && !scriptSessionIdIsNewSentinel
                ? scriptSessionIdToken.ToString()
                : null;

            if (!enableRepl && scriptSessionIdProvided)
                return Response.Success(
                    "'script_session_id' was provided but 'enable_repl' is false — a one-shot execution does not " +
                    "use a session. Either omit 'script_session_id', or set 'enable_repl': true to create a " +
                    "session.");

            if (!string.IsNullOrEmpty(description))
                CodelyLogger.Log($"[ExecuteCSharpScript] Description: {description}");

            // If script_path is provided (legacy support), read the file content
            if (!string.IsNullOrEmpty(scriptPath))
            {
                try
                {
                    if (!File.Exists(scriptPath))
                    {
                        // A U+FFFD in the path means the bytes were already decoded as UTF-8 with
                        // replacement at the TCP layer — the client sent a non-UTF-8 path and the
                        // original bytes are unrecoverable here. Diagnose it instead of a vague 404.
                        if (scriptPath.IndexOf((char)0xFFFD) >= 0)
                            return Response.Error(
                                $"Script file not found, and the path contains replacement characters (U+FFFD): '{scriptPath}'. " +
                                "The path was likely sent in a non-UTF-8 encoding (JSON must be UTF-8). " +
                                "Fix the client to send the path as UTF-8 — the original path cannot be recovered on this side.");
                        return Response.Error($"Script file not found: {scriptPath}");
                    }

                    script = ReadScriptFileSmart(scriptPath);
                    CodelyLogger.Log($"[ExecuteCSharpScript] Loaded script from file: {scriptPath} ({script.Length} chars)");

                    if (string.IsNullOrWhiteSpace(script))
                        return Response.Error($"Script file is empty: {scriptPath}");
                }
                catch (IOException ioEx)
                {
                    return Response.Error($"Failed to read script file: {ioEx.Message}");
                }
                catch (UnauthorizedAccessException uaEx)
                {
                    return Response.Error($"Access denied to script file: {uaEx.Message}");
                }
            }
            // Auto-detect if script parameter is a file path
            // Heuristic: single line, ends with .cs, and file exists
            else if (!string.IsNullOrEmpty(script))
            {
                var trimmedScript = script.Trim();
                bool looksLikePath = !trimmedScript.Contains("\n") &&
                                     trimmedScript.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

                if (looksLikePath && File.Exists(trimmedScript))
                {
                    try
                    {
                        script = ReadScriptFileSmart(trimmedScript);
                        CodelyLogger.Log($"[ExecuteCSharpScript] Auto-detected and loaded script from path: {trimmedScript} ({script.Length} chars)");

                        if (string.IsNullOrWhiteSpace(script))
                            return Response.Error($"Script file is empty: {trimmedScript}");
                    }
                    catch (IOException ioEx)
                    {
                        return Response.Error($"Failed to read script file: {ioEx.Message}");
                    }
                    catch (UnauthorizedAccessException uaEx)
                    {
                        return Response.Error($"Access denied to script file: {uaEx.Message}");
                    }
                }
            }

            ScheduleTmpEssentialsImportIfNeeded(script);

            bool captureLogs = @params["capture_logs"]?.ToObject<bool>() ?? true;
            string[] imports = @params["imports"]?.ToObject<string[]>() ?? new[]
            {
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "UnityEngine",
                "UnityEditor",
                "UnityEditor.SceneManagement",
                "UnityEngine.SceneManagement"
            };

            // `imports` overrides rather than extends the default list, so a caller that supplies
            // its own would otherwise make `Repl` unresolvable. Add it unconditionally on both
            // paths -- Repl.Id/Repl.ById are stateless and work fine one-shot too.
            if (Array.IndexOf(imports, typeof(Repl).Namespace) < 0)
                imports = imports.Concat(new[] { typeof(Repl).Namespace }).ToArray();

            return enableRepl
                ? HandleSessionExecution(script, imports, scriptSessionId, scriptSessionIdProvided,
                    scriptSessionIdIsNewSentinel, captureLogs, unlockDomainReload)
                : HandleOneShotExecution(script, imports, captureLogs, recordGameView);
        }

        static object HandleOneShotExecution(
            string script, string[] imports, bool captureLogs, JObject recordGameView)
        {
            // Repl.Vars must stay empty for the whole duration of this one-shot's own script code,
            // even the part that runs later via JobContext (ScheduleTask/ScheduleCoroutine) — so the
            // flag is only cleared here for a truly synchronous result; an async hand-off leaves it
            // set and ScheduleTask/ScheduleCoroutine clear it themselves when the job actually finishes.
            s_InOneShotExecution = true;
            bool handedOffToJob = false;
            try
            {
                CodelyLogger.Log($"[ExecuteCSharpScript] Executing script ({script.Length} chars, {imports.Length} imports)");

                // Returns a fully-built response for the synchronous case (a value, or the script's
                // OWN runtime failure, both with captured logs), or a JobContext for the async case
                // (a runner job was scheduled and will enqueue the response when it finishes). Log
                // capture is owned by ExecuteFromCompilation, scoped tightly around the invoke. The
                // stopwatch threads through the same async paths (ScheduleTask/ScheduleCoroutine) so
                // elapsed_ms reflects real completion time even when the script finishes on a later
                // frame, not just the time to schedule it.
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = ExecuteScriptInternal(
                    script, imports, captureLogs, stopwatch, recordGameView);

                if (result is JobContext ctx)
                {
                    handedOffToJob = true;
                    return ctx;
                }

                CodelyLogger.Log($"[ExecuteCSharpScript] Response: {Codely.Newtonsoft.Json.JsonConvert.SerializeObject(result)}");
                return result;
            }
            catch (BlockingCallException bce)
            {
                // Rejection happens before this call starts recording. Do not discard
                // another agent's active Game View MP4 session.
                CodelyLogger.LogWarning($"[ExecuteCSharpScript] Rejected script: {bce.Message}");
                return Response.Error(bce.Message);
            }
            catch (NoTopLevelStatementsException ne)
            {
                CodelyLogger.LogWarning($"[ExecuteCSharpScript] Rejected script: {ne.Message}");
                return Response.Error(ne.Message);
            }
            catch (Exception e)
            {
                // Compilation / setup failure before the script ran — no captured logs to attach.
                var errorResponse = BuildScriptFailureResponse(e, new List<string>());
                CodelyLogger.LogError(
                    $"[ExecuteCSharpScript] Script execution failed: {e?.Message}\n{SafeGetStackTrace(e)}");
                CodelyLogger.Log($"[ExecuteCSharpScript] Error Response: {Codely.Newtonsoft.Json.JsonConvert.SerializeObject(errorResponse)}");
                return errorResponse;
            }
            finally
            {
                if (!handedOffToJob)
                    s_InOneShotExecution = false;
            }
        }

        // Session (REPL) execution path. Unlike the one-shot path, compile errors and blocking-call
        // rejections leave `s_SessionState` untouched (the session survives), while a runtime
        // exception is captured into `ScriptState.Exception` (via `catchException: e => true`) so it
        // reports as a failure without discarding the state the script already built up.
        static object HandleSessionExecution(string script, string[] imports, string requestedSessionId,
            bool requestedSessionIdProvided, bool requestedIsNewSentinel, bool captureLogs,
            bool unlockDomainReload)
        {
            string sessionStatus;
            if (requestedIsNewSentinel)
            {
                ResetSession();
                sessionStatus = "created";
            }
            else if (requestedSessionIdProvided)
            {
                if (s_SessionState == null || requestedSessionId != s_SessionId)
                    return Response.Success(
                        $"Script session '{requestedSessionId}' not found — it was likely destroyed by a " +
                        "domain reload (script compilation, AssetDatabase.Refresh, or entering play mode). " +
                        "Previously declared variables are gone. Pass script_session_id:\"new\" to start a " +
                        "fresh session and re-declare what you need. If you stored any object handles with " +
                        "Repl.Id() before the reload, call Repl.ById() in the new session to recover them.");
                sessionStatus = "continued";
            }
            else
            {
                sessionStatus = s_SessionState == null ? "created" : "continued";
            }

            bool isFirstSubmission = s_SessionState == null;
            if (isFirstSubmission)
            {
                s_SessionId = Guid.NewGuid().ToString();
                s_SessionImports = new List<string>(imports);
            }
            else
            {
                foreach (var import in imports)
                    if (!s_SessionImports.Contains(import))
                        s_SessionImports.Add(import);
            }

            // Snapshot this call's own identity/count before the script runs. A script can
            // reflectively call HandleCommand on itself (the documented workaround for reaching
            // unlock_domain_reload/script_session_id, which this tool's exposed surface doesn't
            // pass through) -- that nested call mutates these same static fields mid-execution.
            // Restoring the snapshot right after the run keeps this call's own reported identity
            // and count tied to the continuation it actually captured, regardless of what a nested
            // call left the fields pointing at in the meantime.
            string executingSessionId = s_SessionId;
            int submissionIndexBeforeRun = s_SubmissionCount;

            // Guard only applies once the session has something to lose: an empty session's first
            // submission is exempt (nothing to lose, and it keeps the common "new session, first
            // call is AssetDatabase.Refresh()" workflow working). `unlockDomainReload` is a plain
            // request parameter, not persisted state, so it is inherently scoped to this one call.
            if (s_SubmissionCount > 0 && !unlockDomainReload)
            {
                var guardBlock = ReplGuard.FindReloadTrigger(script);
                if (guardBlock != null)
                    return Response.Success(guardBlock, new
                    {
                        script_session_id = s_SessionId,
                        session_status = sessionStatus,
                        submission_count = s_SubmissionCount
                    });
            }

            try
            {
                CodelyLogger.Log(
                    $"[ExecuteCSharpScript] Executing script in session {s_SessionId} " +
                    $"({script.Length} chars, submission {s_SubmissionCount})");
                StartLogCapture(captureLogs);
                SaveScriptToTemp(script);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var references = BuildBaseReferences();
                var baseOptions = ScriptOptions.Default
                    .WithEmitDebugInformation(true)
                    // Distinct per-submission file names so a multi-submission stack trace can be
                    // traced back to the specific submission that threw, not just "Submission#0".
                    .WithFilePath($"Submission_{s_SubmissionCount}.csx")
                    .WithFileEncoding(Encoding.UTF8);

                // Continuation-aware: ScriptFix's diagnostics must see session-scoped declarations
                // from prior submissions, or FixMissingAssemblyReference mistakes a real
                // prior-submission variable for a missing type/assembly. Session scripts also skip
                // HoistUsingDirectives — top-level `using` directives carry across ContinueWith
                // submissions natively, so hoisting would just duplicate them into the
                // options-level imports on top of leaving them in the script text.
                var previousScript = isFirstSubmission ? null : s_SessionState.Script;
                var compilation = CompileAndAutoFix(ref script, s_SessionImports, references,
                    baseOptions, hoistUsingDirectives: false, previousScript, out var candidateScript);

                var errors = compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                if (errors.Any())
                    // ScriptFix already tried its best above; remaining errors are reported as-is
                    // and the session is left untouched — `candidateScript` was never assigned to
                    // `s_SessionState`.
                    throw new Exception(string.Join("\n", errors.Select(d => d.ToString())));

                // Reject scripts that would block the Unity main thread, reusing the compilation
                // just produced above (continuation-aware, so it sees session-scoped declarations).
                CheckForBlockingCalls(script, s_SessionImports, references, compilation);

                bool possibleSessionRebuild = sessionStatus == "created" && !requestedIsNewSentinel;

                var scriptStateTask = isFirstSubmission
                    ? candidateScript.RunAsync(globals: null, catchException: e => true)
                    : candidateScript.RunFromAsync(s_SessionState, catchException: e => true);

                // CheckForBlockingCalls above already rejects a genuine top-level `await`, so in
                // practice this task is always complete by the time we get here. This fallback only
                // guards against that assumption ever being wrong, without reintroducing the
                // `.Wait()` deadlock the old synchronous RunScriptSync had.
                if (!scriptStateTask.IsCompleted)
                    return ScheduleSessionOuterTask(scriptStateTask, captureLogs, stopwatch,
                        executingSessionId, sessionStatus, submissionIndexBeforeRun, possibleSessionRebuild);

                var newState = UnwrapScriptStateTask(scriptStateTask);

                // Commit the session's continuation point now, synchronously. It is already fully
                // valid the instant this submission's own top-level code finished running, regardless
                // of whether ReturnValue turns out to be a Task/coroutine still in flight — a later
                // submission's ContinueWith/RunFromAsync only needs this ScriptState object, not the
                // resolved value of what this one returned. So there is no concurrent-continuation
                // race to guard against here (no busy gate, no identity check on completion needed):
                // by the time any later submission can possibly arrive (commands are dispatched
                // serially), this one has already fully committed.
                s_SessionState = newState;
                s_SessionId = executingSessionId;
                s_SubmissionCount = submissionIndexBeforeRun + 1;

                if (newState.Exception == null)
                {
                    if (newState.ReturnValue is Task innerTask)
                        return ScheduleSessionTask(innerTask, captureLogs, stopwatch,
                            s_SessionId, sessionStatus, s_SubmissionCount);

                    if (IsUnityCoroutine(newState.ReturnValue, out var routine))
                        return ScheduleSessionCoroutine(routine, captureLogs, stopwatch,
                            s_SessionId, sessionStatus, s_SubmissionCount);
                }

                stopwatch.Stop();
                var logs = captureLogs ? StopLogCapture() : new List<string>();

                var response = newState.Exception != null
                    ? BuildSessionFailureResponse(newState.Exception, logs, possibleSessionRebuild,
                        s_SessionId, sessionStatus, s_SubmissionCount)
                    : BuildSessionSuccessResponse(newState.ReturnValue, logs, stopwatch,
                        s_SessionId, sessionStatus, s_SubmissionCount);

                CodelyLogger.Log($"[ExecuteCSharpScript] Response: {Codely.Newtonsoft.Json.JsonConvert.SerializeObject(response)}");
                return response;
            }
            catch (BlockingCallException bce)
            {
                if (captureLogs) StopLogCapture();
                CodelyLogger.LogWarning($"[ExecuteCSharpScript] Rejected script: {bce.Message}");
                return Response.Success(bce.Message, new
                {
                    script_session_id = s_SessionId,
                    session_status = sessionStatus,
                    submission_count = s_SubmissionCount
                });
            }
            catch (Exception e)
            {
                var logs = captureLogs ? StopLogCapture() : new List<string>();
                var stackTrace = SafeGetStackTrace(e);
                var enhancedError = ReplGuard.EnhanceError(e,
                    possibleSessionRebuild: sessionStatus == "created" && !requestedIsNewSentinel);
                var errorResponse = Response.Success(
                    $"C# script execution failed: {enhancedError}\n{stackTrace}",
                    new
                    {
                        error = enhancedError,
                        stack_trace = stackTrace,
                        script_session_id = s_SessionId,
                        session_status = sessionStatus,
                        submission_count = s_SubmissionCount,
                        logs,
                        log_count = logs.Count
                    }
                );
                CodelyLogger.LogError(
                    $"[ExecuteCSharpScript] Script execution failed: {e?.Message}\n{stackTrace}");
                CodelyLogger.Log($"[ExecuteCSharpScript] Error Response: {Codely.Newtonsoft.Json.JsonConvert.SerializeObject(errorResponse)}");
                return errorResponse;
            }
        }

        // Unwraps an already-completed Task<ScriptState<object>>. Callers must confirm IsCompleted
        // first (HandleSessionExecution does; the incomplete case goes through
        // ScheduleSessionOuterTask instead) — unlike the old RunScriptSync, this never blocks.
        static ScriptState<object> UnwrapScriptStateTask(System.Threading.Tasks.Task<ScriptState<object>> task)
        {
            try
            {
                return task.Result;
            }
            catch (AggregateException ae)
            {
                throw ae.InnerException ?? ae;
            }
        }

        static string SafeTypeName(object value)
        {
            if (value == null)
                return null;
            try { return value.GetType().FullName; }
            catch { return null; }
        }

        // Reading Exception.StackTrace can itself throw on Mono/Unity: when the exception's call
        // stack passes through Roslyn's async scripting state machine (Script`1+<RunSubmissionsAsync>),
        // Mono's StackTrace.ConvertAsyncStateMachineMethod tries to decode custom attributes on a type
        // it cannot fully load and raises a TypeLoadException. Access it defensively so surfacing the
        // error never crashes the handler; fall back to the async-safe frame list when it does.
        static string SafeGetStackTrace(Exception e)
        {
            if (e == null)
                return string.Empty;

            try
            {
                return e.StackTrace ?? string.Empty;
            }
            catch (Exception traceEx)
            {
                CodelyLogger.LogWarning(
                    $"[ExecuteCSharpScript] Could not format exception stack trace ({traceEx.GetType().Name}): {traceEx.Message}");

                // Best-effort manual walk that avoids ToString()/attribute decoding on async frames.
                // needFileInfo:true resolves line numbers from the emitted PDB when available.
                try
                {
                    var trace = new System.Diagnostics.StackTrace(e, true);
                    var sb = new StringBuilder();
                    foreach (var frame in trace.GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>())
                    {
                        var method = frame.GetMethod();
                        if (method == null)
                            continue;
                        sb.Append($"  at {method.DeclaringType?.FullName}.{method.Name}");
                        var line = frame.GetFileLineNumber();
                        if (line > 0)
                            sb.Append($" (in {Path.GetFileName(frame.GetFileName())}:{line})");
                        sb.AppendLine();
                    }
                    return sb.Length > 0 ? sb.ToString() : "(stack trace unavailable)";
                }
                catch
                {
                    return "(stack trace unavailable)";
                }
            }
        }

        // Formats a script's return value for the response. Never throws: a user type's ToString()
        // (or even GetType()) can throw, and since the script's side effects already happened by
        // this point, letting that exception escape would report a successful execution as a
        // failure — the caller would then retry and double up on side effects (e.g. re-creating a
        // GameObject). Non-string IEnumerables get a bounded element-by-element preview instead of
        // the useless default `ToString()` (e.g. `System.Collections.Generic.List\`1[System.Int32]`).
        // Internal rather than private so Repl.Vars can reuse the same value-preview logic when
        // rendering its own table instead of duplicating the truncation/exception-guard rules.
        internal static string FormatResultValue(object result)
        {
            if (result == null)
                return null;

            // ReplVars implements IReadOnlyDictionary (hence IEnumerable) for programmatic indexing,
            // but a bare `Repl.Vars` submission should render its own Name/Type/Value table, not the
            // generic bounded-collection preview below.
            string formatted = result is ReplVars
                ? SafeToString(result)
                : !(result is string) && result is IEnumerable enumerable
                    ? FormatEnumerablePreview(enumerable)
                    : SafeToString(result);

            return TruncateIfTooLong(formatted, k_MaxResultChars);
        }

        static string SafeToString(object value)
        {
            if (value == null)
                return null;

            string typeName = "?";
            try { typeName = value.GetType().FullName; }
            catch { /* keep placeholder */ }

            try
            {
                return value.ToString();
            }
            catch (Exception ex)
            {
                return $"<{typeName}> (ToString threw {ex.GetType().Name})";
            }
        }

        // Bounded preview: the enumerable can be lazy or infinite (an iterator doing
        // `while (true) yield return ...`), so "enumerate to the end to get a count" would itself
        // freeze the main thread. Only ICollection/arrays get an exact "+N more" (their Count/Length
        // is O(1) and needs no enumeration); anything else just says "more".
        static string FormatEnumerablePreview(IEnumerable enumerable)
        {
            IEnumerator enumerator;
            try
            {
                enumerator = enumerable.GetEnumerator();
            }
            catch (Exception ex)
            {
                return $"<enumeration threw {ex.GetType().Name} while getting enumerator>";
            }

            var items = new List<string>();
            Exception enumerationError = null;

            try
            {
                while (items.Count < k_MaxCollectionPreviewItems)
                {
                    bool moved;
                    try
                    {
                        moved = enumerator.MoveNext();
                    }
                    catch (Exception ex)
                    {
                        enumerationError = ex;
                        break;
                    }

                    if (!moved)
                        break;

                    object current;
                    try { current = enumerator.Current; }
                    catch (Exception ex)
                    {
                        items.Add($"<enumeration threw {ex.GetType().Name} reading Current>");
                        continue;
                    }

                    items.Add(SafeToString(current) ?? "null");
                }
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }

            // Hitting the cap means there's at least one more element beyond what we collected —
            // the last slot only proved that, so it's dropped from the displayed items.
            bool hasMore = items.Count == k_MaxCollectionPreviewItems;
            if (hasMore)
                items.RemoveAt(items.Count - 1);

            var sb = new StringBuilder();
            sb.Append("[ ").Append(string.Join(", ", items));

            if (hasMore)
            {
                if (enumerable is ICollection collection)
                    sb.Append($", ... (+{collection.Count - items.Count} more)");
                else
                    sb.Append(", ... (more)");
            }
            sb.Append(" ]");

            if (enumerationError != null)
                sb.Append($" (enumeration threw {enumerationError.GetType().Name})");

            return TruncateIfTooLong(sb.ToString(), k_MaxCollectionPreviewChars);
        }

        static string TruncateIfTooLong(string text, int maxChars)
        {
            if (text == null || text.Length <= maxChars)
                return text;
            return text.Substring(0, maxChars) + $"... (truncated, total {text.Length} chars)";
        }

        static void ScheduleTmpEssentialsImportIfNeeded(string script)
        {
            if (string.IsNullOrEmpty(script))
                return;

            if (script.IndexOf(k_TextMeshProUGUIKeyword, StringComparison.Ordinal) < 0)
                return;

            if (File.Exists(k_TmpSettingsAssetPath))
                return;

            TmpEssentialsAutoImporter.ScheduleImport();
            CodelyLogger.Log("[ExecuteCSharpScript] Scheduled TMP essential resources import because script references TextMeshProUGUI.");
        }

        // Thrown when a script contains a call that would block the Unity main thread.
        class BlockingCallException : Exception
        {
            public BlockingCallException(string message) : base(message) { }
        }

        // Thrown when a script declares types but has no top-level statements, so running it is a no-op.
        internal class NoTopLevelStatementsException : Exception
        {
            public NoTopLevelStatementsException(string message) : base(message) { }
        }

        // A Roslyn submission that contains only declarations (types, methods, usings) compiles and
        // runs cleanly while doing nothing at all — the submission body is empty. Reporting that as a
        // success is actively misleading: the caller sees no error, no logs, and no effect, and is left
        // debugging the declared code instead of the missing call. Reject it instead.
        internal static void CheckForTopLevelStatements(Compilation compilation)
        {
            var root = compilation.SyntaxTrees.First().GetRoot();

            // Global statements only ever appear at the root of a script compilation unit, so a
            // shallow scan is enough — and it never mistakes a method body for an executable statement.
            if (root.ChildNodes().OfType<GlobalStatementSyntax>().Any())
                return;

            throw new NoTopLevelStatementsException(
                "Script contains only declarations and no top-level statements — nothing was executed. " +
                "This is a Roslyn script submission: it requires at least one top-level statement, so " +
                "add a call to the declared code at the end, or write the body as top-level statements.");
        }

        // Scans the (already compiled) script for calls that synchronously block the main thread and
        // throws BlockingCallException if any are found. Detection is SEMANTIC, not name-based: each
        // call is bound to its real symbol and matched against the exact declaring type, so innocent
        // look-alikes (string.Join, Enumerable.Join, Path.Join, a user type's own .Result/.Wait(),
        // SpinWait.SpinOnce, etc.) are never flagged. Symbols that cannot be resolved are skipped.
        static void CheckForBlockingCalls(string script, List<string> imports, List<MetadataReference> references,
            Compilation compilation)
        {
            if (string.IsNullOrEmpty(script))
                return;

            // Reuse the compilation produced by CompileAndAutoFix when it matches the final script.
            // Only fall back to a fresh (expensive) compilation when one wasn't provided.
            if (compilation == null)
            {
                var options = ScriptOptions.Default
                    .WithReferences(references)
                    .WithImports(imports);
                compilation = CSharpScript.Create(script, options).GetCompilation();
            }
            var tree = compilation.SyntaxTrees.First();
            var model = compilation.GetSemanticModel(tree);

            var blockingViolations = new List<string>();
            var awaitViolations = new List<string>();

            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is InvocationExpressionSyntax invocation)
                {
                    if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method && IsBlockingMethod(method))
                        blockingViolations.Add($"  - Line {GetLine(invocation)}: {method.ContainingType.Name}.{method.Name}(...)");
                }
                else if (node is MemberAccessExpressionSyntax member && !(member.Parent is InvocationExpressionSyntax))
                {
                    if (model.GetSymbolInfo(member).Symbol is IPropertySymbol property && IsBlockingProperty(property))
                        blockingViolations.Add($"  - Line {GetLine(member)}: {property.ContainingType.Name}.{property.Name}");
                }
                else if (node is AwaitExpressionSyntax awaitExpr)
                {
                    if (!HasEnclosingFunctionBody(awaitExpr))
                        awaitViolations.Add($"  - Line {GetLine(awaitExpr)}: top-level `await`");
                }
                // ForEachStatementSyntax (`await foreach (var x in ...)`) and
                // ForEachVariableStatementSyntax (`await foreach (var (a, b) in ...)`) share this
                // base — one check covers both destructuring forms.
                else if (node is CommonForEachStatementSyntax forEach && forEach.AwaitKeyword != default)
                {
                    if (!HasEnclosingFunctionBody(forEach))
                        awaitViolations.Add($"  - Line {GetLine(forEach)}: top-level `await foreach`");
                }
                else if (node is UsingStatementSyntax usingStmt && usingStmt.AwaitKeyword != default)
                {
                    if (!HasEnclosingFunctionBody(usingStmt))
                        awaitViolations.Add($"  - Line {GetLine(usingStmt)}: top-level `await using (...)`");
                }
                // `await using var x = ...;` — the AwaitKeyword lives on the declaration statement,
                // not on an AwaitExpressionSyntax node.
                else if (node is LocalDeclarationStatementSyntax localDecl && localDecl.AwaitKeyword != default)
                {
                    if (!HasEnclosingFunctionBody(localDecl))
                        awaitViolations.Add($"  - Line {GetLine(localDecl)}: top-level `await using` declaration");
                }
            }

            if (blockingViolations.Count == 0 && awaitViolations.Count == 0)
                return;

            var message = new StringBuilder(
                $"Blocking calls are not allowed in {DisplayCommandName} — the script runs on the Unity " +
                "main thread and these would freeze the editor:");

            if (blockingViolations.Count > 0)
            {
                var seen = new HashSet<string>();
                message.Append("\n").Append(string.Join("\n", blockingViolations.Where(v => seen.Add(v))));
                message.Append("\nDisallowed: Thread.Sleep, Thread.SpinWait, Thread.Join, Task.Wait/WaitAll/WaitAny, " +
                    "Task/ValueTask.Result, Monitor.Wait, WaitHandle.WaitOne, and GetAwaiter().GetResult(). " +
                    "Use EditorApplication.update or EditorApplication.delayCall instead.");
            }

            if (awaitViolations.Count > 0)
            {
                var seen = new HashSet<string>();
                message.Append("\n").Append(string.Join("\n", awaitViolations.Where(v => seen.Add(v))));
                message.Append("\nA top-level await deadlocks: its continuation is posted back to the main " +
                    "thread's SynchronizationContext, but the main thread is blocked waiting for the script " +
                    "to finish — a permanent freeze. Use EditorApplication.update/delayCall to poll instead, " +
                    "or move the awaited work into an async local function/lambda (fire-and-forget or " +
                    "synchronously waited with a timeout while checking the result).");
            }

            throw new BlockingCallException(message.ToString());
        }

        // True if `node` sits inside some function body (lambda, local function, method, or
        // accessor) — an await there runs on that body's own continuation, not the script's
        // synchronous top level, so it cannot deadlock the main thread the way a top-level await
        // does. AnonymousFunctionExpressionSyntax alone covers every lambda/anonymous-method shape
        // (simple, parenthesized, `delegate {}`), so it doesn't need to be enumerated per subtype.
        static bool HasEnclosingFunctionBody(SyntaxNode node)
        {
            foreach (var ancestor in node.Ancestors())
            {
                if (ancestor is AnonymousFunctionExpressionSyntax
                    || ancestor is LocalFunctionStatementSyntax
                    || ancestor is MethodDeclarationSyntax
                    || ancestor is AccessorDeclarationSyntax)
                    return true;
            }
            return false;
        }

        // True only for methods declared on the specific blocking types below — never for a same-named
        // method on any other type.
        static bool IsBlockingMethod(IMethodSymbol method)
        {
            var declaringType = method.ContainingType?.OriginalDefinition?.ToDisplayString();
            switch (method.Name)
            {
                case "Sleep":
                case "SpinWait":
                case "Join":
                    return declaringType == "System.Threading.Thread";
                case "Wait":
                    return declaringType == "System.Threading.Tasks.Task"
                        || declaringType == "System.Threading.Monitor"
                        || declaringType == "System.Threading.SemaphoreSlim"
                        || declaringType == "System.Threading.ManualResetEventSlim"
                        || declaringType == "System.Threading.CountdownEvent";
                case "WaitAll":
                case "WaitAny":
                    return declaringType == "System.Threading.Tasks.Task"
                        || declaringType == "System.Threading.WaitHandle";
                case "WaitOne":
                    return declaringType == "System.Threading.WaitHandle";
                case "GetResult":
                    // task.GetAwaiter().GetResult() — the awaiter types live in this namespace and
                    // all end in "Awaiter" (TaskAwaiter, TaskAwaiter<T>, ConfiguredTaskAwaiter, ...).
                    return method.ContainingType?.ContainingNamespace?.ToDisplayString()
                            == "System.Runtime.CompilerServices"
                        && method.ContainingType.Name.EndsWith("Awaiter", StringComparison.Ordinal);
                default:
                    return false;
            }
        }

        // True only for Task<T>.Result / ValueTask<T>.Result — the blocking synchronous accessors.
        static bool IsBlockingProperty(IPropertySymbol property)
        {
            if (property.Name != "Result")
                return false;
            var declaringType = property.ContainingType?.OriginalDefinition?.ToDisplayString();
            return declaringType == "System.Threading.Tasks.Task<TResult>"
                || declaringType == "System.Threading.Tasks.ValueTask<TResult>";
        }

        static int GetLine(SyntaxNode node) =>
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        static object ExecuteScriptInternal(string script, string[] imports, bool captureLogs,
            System.Diagnostics.Stopwatch stopwatch, JObject recordGameView)
        {
            SaveScriptToTemp(script);

            try
            {
                // Build minimal base references — no pre-loaded optional modules
                var references = BuildBaseReferences();
                var fixedImports = new List<string>(imports);
                var fixedScript = script;

                // Compile and auto-fix before execution. Returns the compilation for the final
                // script so the blocking-call scan can reuse it. `previousScript: null` — one-shot
                // has no prior submission to continue from.
                var compilation = CompileAndAutoFix(ref fixedScript, fixedImports, references,
                    ScriptOptions.Default, hoistUsingDirectives: true, previousScript: null, out _);

                // Reject scripts that would block the Unity main thread. Even with the async/coroutine
                // runners, a synchronous blocking wait (Thread.Sleep, Task.Wait/.Result,
                // GetAwaiter().GetResult(), …) still freezes the whole editor (and this bridge) before
                // any runner can tick. The scan only flags those blocking calls — never `await` — so
                // await-based async scripts and coroutines pass through untouched.
                CheckForBlockingCalls(fixedScript, fixedImports, references, compilation);

                var options = ScriptOptions.Default
                    .WithReferences(references)
                    .WithImports(fixedImports)
                    // Emit a PDB so runtime exceptions carry line numbers that map back to the
                    // submission source — without this the stack trace only shows
                    // "Submission#0+<<Initialize>>d__0.MoveNext" with no position.
                    .WithEmitDebugInformation(true)
                    .WithFilePath("CodelyScript.csx")
                    .WithFileEncoding(Encoding.UTF8);

                // Compile, load, invoke, and — for scripts that run across frames — schedule the
                // async work on a runner. Returns a fully-built response when the script ran
                // synchronously, or a JobContext when it went async.
                return ExecuteFromCompilation(
                    fixedScript, options, captureLogs, stopwatch, recordGameView);
            }
            catch (AggregateException ae)
            {
                if (ae.InnerException != null)
                    throw ae.InnerException;
                throw;
            }
        }

        // Compiles, emits to in-memory PE+PDB, loads via Assembly.Load(byte[]), and invokes the
        // submission entry point via reflection — bypassing InteractiveAssemblyLoader entirely.
        // On Mono with ACP=936, the loader's RegisterDependency calls Assembly.GetName() ->
        // get_code_base(), which throws EILSEQ when the project path contains non-ASCII
        // characters (e.g. Chinese) that require code page conversion.
        //
        // Roslyn's entry point always returns Task<object>. We must NOT call .Wait() — that blocks
        // the Unity main thread (and this bridge). When the script ran synchronously (e.g. "1 + 2")
        // we build and return its response directly; when it is still running (awaited) we hand the
        // Task to the AsyncTaskRunner, and when it returned a coroutine/Task to run across frames we
        // hand that to the CoroutineRunner/AsyncTaskRunner — returning a JobContext so the reply is
        // deferred until the runner job finishes. (We also avoid CSharpScript.EvaluateAsync, whose
        // InteractiveAssemblyLoader hits the same EILSEQ path described above.)
        //
        // Returns a fully-built response (sync) or a JobContext (async). `stopwatch` was started
        // when the command came in and is read (never stopped here) wherever a response is built,
        // including from ScheduleTask/ScheduleCoroutine's completion callbacks — so elapsed_ms is
        // accurate for a script that finishes several frames later, not just the time to schedule it.
        static object ExecuteFromCompilation(string script, ScriptOptions options, bool captureLogs,
            System.Diagnostics.Stopwatch stopwatch, JObject recordGameView)
        {
            var compilation = CSharpScript.Create(script, options).GetCompilation();

            byte[] pe, pdb;
            using (var peStream = new MemoryStream())
            using (var pdbStream = new MemoryStream())
            {
                var emitResult = compilation.Emit(
                    peStream, pdbStream, null, null, null,
                    new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
                    CancellationToken.None);

                if (!emitResult.Success)
                    throw new Exception("Script compilation failed during emit:\n" +
                        string.Join("\n", emitResult.Diagnostics
                            .Where(d => d.Severity == DiagnosticSeverity.Error)
                            .Select(d => $"  {d.Id}: {d.GetMessage()}")));

                pe = peStream.ToArray();
                pdb = pdbStream.ToArray();
            }

            // Only once the script is known to compile: reject a declaration-only submission, which
            // would "succeed" while doing nothing. Checking this before emit would mask the real
            // diagnostics of a script that both fails to compile and lacks an entry call — including
            // errors that are themselves the reason it has none (a namespace declaration, illegal in
            // script code, makes every statement inside it a non-global one).
            CheckForTopLevelStatements(compilation);

            var assembly = Assembly.Load(pe, pdb);
            var entryPoint = compilation.GetEntryPoint(CancellationToken.None);

            var type = assembly.GetType(entryPoint.ContainingType.MetadataName, throwOnError: false)
                ?? throw new Exception(
                    $"Submission type '{entryPoint.ContainingType.MetadataName}' not found.");
            var method = type.GetMethod(entryPoint.Name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new Exception($"Entry point '{entryPoint.Name}' not found on '{type.FullName}'.");

            ManageScreenshot.Mp4RecordingHandle recording = null;
            if (recordGameView != null)
            {
                if (!ManageScreenshot.TryBeginOrAttachExecuteCSharpGameViewRecording(
                        recordGameView, out recording, out _, out object recordingError))
                {
                    return recordingError;
                }
            }

            // Capture logs around the invoke and any scheduled async/coroutine work. Sync paths
            // stop capture before returning; ScheduleTask/ScheduleCoroutine stop it when the job
            // finishes so logs from awaited continuations / coroutine frames are included.
            object raw;
            StartLogCapture(captureLogs);
            try
            {
                // Roslyn submission entry points take a single object[] whose [0]=globals,
                // [1]=previous submission result (both null for standalone scripts). A
                // 1-element array throws IndexOutOfRangeException at runtime.
                raw = method.Invoke(null, new object[] { new object[2] });
            }
            catch (TargetInvocationException tie)
            {
                ManageScreenshot.CancelGameViewMp4Recording(recording);
                // The script threw during its synchronous execution — report it (with logs) as the
                // synchronous paths do; a script's own failure is output, not a bridge error.
                return BuildScriptFailureResponse(tie.InnerException ?? tie,
                    captureLogs ? StopLogCapture() : new List<string>());
            }

            if (raw is Task<object> scriptTask)
            {
                // Completed synchronously (e.g. "1 + 2" or a script that returned before awaiting).
                if (scriptTask.IsCompleted)
                {
                    if (scriptTask.IsFaulted)
                    {
                        ManageScreenshot.CancelGameViewMp4Recording(recording);
                        return BuildScriptFailureResponse(
                            scriptTask.Exception?.InnerException ?? scriptTask.Exception,
                            captureLogs ? StopLogCapture() : new List<string>());
                    }

                    var userResult = scriptTask.Result;

                    // The script returned a Task to run across frames → AsyncTaskRunner.
                    if (userResult is Task innerTask)
                        return ScheduleTask(innerTask, captureLogs, stopwatch, recording);

                    // The script returned a coroutine to run across frames → CoroutineRunner.
                    if (IsUnityCoroutine(userResult, out var routine))
                        return ScheduleCoroutine(routine, captureLogs, stopwatch, recording);

                    // Plain value — fully synchronous. Return the response with captured logs.
                    var logs = captureLogs ? StopLogCapture() : new List<string>();
                    return recording == null
                        ? BuildScriptSuccessResponse(userResult, logs, stopwatch)
                        : ScheduleRecordingCompletion(userResult, logs, stopwatch, recording);
                }

                return ScheduleTask(scriptTask, captureLogs, stopwatch, recording);
            }

            var synchronousLogs = captureLogs ? StopLogCapture() : new List<string>();
            return recording == null
                ? BuildScriptSuccessResponse(raw, synchronousLogs, stopwatch)
                : ScheduleRecordingCompletion(raw, synchronousLogs, stopwatch, recording);
        }

        // A script returning a plain `IEnumerable<T>` (e.g. an iterator method like
        // `IEnumerable<int> Foo() { yield return 1; }`) and one returning an actual Unity coroutine
        // (`IEnumerator Foo() { yield return new WaitForSeconds(1); }`) are NOT distinguishable by a
        // plain `is IEnumerator` check: the C# compiler's iterator state machine always implements
        // IEnumerator regardless of the method's declared return type, so a data-yielding
        // `IEnumerable<int>` result would otherwise get misrouted into ScheduleCoroutine (which
        // discards it — coroutines have no return value) instead of being formatted as data.
        //
        // [Verified against real GetInterfaces() output — see dev_docs/execute_csharp_script.md §13.3]
        // The generated state machine for a NON-generic `IEnumerator Foo()` (Unity's coroutine
        // signature) on this toolchain still implements `IEnumerator<object>` — checking for a
        // generic `IEnumerator<T>` implementation (the original approach here) is therefore never a
        // reliable signal; it is effectively always true and never actually distinguishes the two
        // cases. What that same state machine does NOT implement is `IEnumerable`/`IEnumerable<T>` —
        // a method declared `IEnumerable<T> Foo()` needs its state machine to also serve as the
        // sequence itself (so a caller's `foreach` / repeated `GetEnumerator()` works), a plain
        // `IEnumerator Foo()` coroutine never does. So the reliable check is the opposite interface:
        // does the runtime value implement `IEnumerable` at all (which `IEnumerable<T>` always does,
        // by interface inheritance)?
        static bool IsUnityCoroutine(object value, out IEnumerator routine)
        {
            routine = value as IEnumerator;
            if (routine == null)
                return false;

            return !(value is IEnumerable);
        }

        // Cached across calls: rebuilding this from scratch means CreateFromFile-ing every loaded
        // assembly's PE image, which is hundreds of MB / several seconds on a large project. A
        // domain reload wipes these static fields for free, and s_CachedAssemblyCount catches the
        // rarer case of a script loading an assembly itself (Assembly.LoadFrom/LoadFile) without a
        // reload — main-thread-only access, no locking needed.
        static List<MetadataReference> s_CachedBaseReferences;
        static int s_CachedAssemblyCount = -1;

        // Returns a fresh copy every call: ScriptFix providers append to the list they're handed
        // (via ScriptFixContext) as part of a single execution's auto-fix attempts, and that must
        // never leak into the cached baseline for the next execution.
        static List<MetadataReference> BuildBaseReferences()
        {
            int assemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length;
            if (s_CachedBaseReferences == null || assemblyCount != s_CachedAssemblyCount)
            {
                s_CachedBaseReferences = ComputeBaseReferences();
                s_CachedAssemblyCount = assemblyCount;
            }
            return new List<MetadataReference>(s_CachedBaseReferences);
        }

        static List<MetadataReference> ComputeBaseReferences()
        {
            var references = new List<MetadataReference>();
            var addedLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Core + Unity assemblies (Unity types are added inside AddCoreAssemblyReferences)
            AddCoreAssemblyReferences(references);

            foreach (var r in references.OfType<PortableExecutableReference>())
                if (r.FilePath != null) addedLocations.Add(r.FilePath);

            // 2. Reference all loaded non-dynamic assemblies so scripts can use any
            //    runtime type (package assemblies, third-party DLLs, etc.)
            //    This fixes CS0246/CS0311 when referencing types from packages like Codely.Utilities.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                var loc = asm.Location;
                if (string.IsNullOrEmpty(loc)) continue;
                if (!addedLocations.Add(loc)) continue;

                // Assembly-CSharp / -Editor are handled via shadow copy below
                if (s_ShadowCopyAssemblyNames.Contains(GetAssemblySimpleName(asm))) continue;

                try { references.Add(MetadataReference.CreateFromFile(loc)); }
                catch { /* skip unreadable assemblies */ }
            }

            // 3. Assembly-CSharp / -Editor via shadow copy (avoids domain reload file locks)
            foreach (var assemblyName in s_ShadowCopyAssemblyNames)
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => GetAssemblySimpleName(a) == assemblyName);
                if (asm == null || string.IsNullOrEmpty(asm.Location))
                    continue;

                var shadowPath = CreateShadowCopy(asm.Location);
                if (addedLocations.Add(shadowPath))
                    references.Add(MetadataReference.CreateFromFile(shadowPath));
            }

            return references;
        }

        // Builds the Script object for one compile attempt: a fresh standalone compile when there's
        // no prior submission to continue from (one-shot, or a session's first submission), or a
        // ContinueWith off `previousScript` for a session continuation — so diagnostics resolve
        // symbols declared by earlier submissions instead of misreporting them as CS0103.
        static Script<object> CreateOrContinueScript(string script, ScriptOptions options, Script previousScript) =>
            previousScript == null
                ? CSharpScript.Create(script, options)
                : previousScript.ContinueWith<object>(script, options);

        // Iteratively compiles the script using the same scripting engine as execution,
        // then applies auto-fixes until clean or exhausted.
        // `baseOptions` supplies everything but References/Imports (e.g. a session's debug info and
        // per-submission file path) — each iteration layers the current `references`/`imports` on
        // top via WithReferences/WithImports. `previousScript` continuation-compiles off a prior
        // session submission (null for one-shot or a session's first submission).
        // Returns the Compilation matching the final `script`, and — via `resultScript` — the exact
        // Script object that produced it, so a session caller can execute it directly
        // (RunAsync/RunFromAsync) without a second, non-continuation-aware recompile.
        static Compilation CompileAndAutoFix(ref string script, List<string> imports, List<MetadataReference> references,
            ScriptOptions baseOptions, bool hoistUsingDirectives, Script previousScript, out Script<object> resultScript)
        {
            if (hoistUsingDirectives)
                HoistUsingDirectives(ref script, imports);

            var addedLocations = new HashSet<string>(references
                .OfType<PortableExecutableReference>()
                .Select(r => r.FilePath ?? ""));

            var context = new ScriptFixContext(imports, references, addedLocations);

            for (int iteration = 0; iteration < k_MaxFixIterations; iteration++)
            {
                var scriptOptions = baseOptions.WithReferences(references).WithImports(imports);
                var scriptObj = CreateOrContinueScript(script, scriptOptions, previousScript);
                var compilation = scriptObj.GetCompilation();
                var tree = compilation.SyntaxTrees.First();

                var errors = compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                if (!errors.Any())
                {
                    CodelyLogger.Log($"[ExecuteCSharpScript] Compilation check passed (iteration {iteration})");
                    resultScript = scriptObj;
                    return compilation;
                }

                int errorCountBefore = errors.Count;
                bool anyFixed = false;
                var updatedTree = tree;

                foreach (var diagnostic in errors)
                {
                    foreach (var fix in s_FixProviders)
                    {
                        if (!fix.CanFix(diagnostic))
                            continue;

                        var treeBeforeFix = updatedTree;
                        if (fix.ApplyFix(ref updatedTree, diagnostic, context))
                        {
                            anyFixed = true;
                            CodelyLogger.Log($"[ExecuteCSharpScript] {fix.GetType().Name} applied for {diagnostic.Id}");

                            // If the tree was modified, remaining diagnostic spans are stale.
                            // Break out and let the outer loop recompile with fresh diagnostics.
                            if (!ReferenceEquals(updatedTree, treeBeforeFix))
                                goto fixesApplied;
                        }
                    }
                }

                fixesApplied:
                if (!anyFixed)
                {
                    CodelyLogger.LogWarning("[ExecuteCSharpScript] Auto-fix could not resolve remaining errors:\n" +
                        string.Join("\n", errors.Select(e => $"  {e.Id}: {e.GetMessage()}")));
                    // `script` was not modified this iteration, so `compilation`/`scriptObj` still match it.
                    resultScript = scriptObj;
                    return compilation;
                }

                if (!ReferenceEquals(updatedTree, tree))
                {
                    var candidate = updatedTree.GetText().ToString();

                    // Verify the fix reduced errors; if it made things worse, skip this fix
                    var checkOptions = baseOptions.WithReferences(references).WithImports(imports);
                    var checkScript = CreateOrContinueScript(candidate, checkOptions, previousScript);
                    var checkErrors = checkScript.GetCompilation().GetDiagnostics()
                        .Count(d => d.Severity == DiagnosticSeverity.Error);

                    if (checkErrors > errorCountBefore)
                    {
                        CodelyLogger.LogWarning($"[ExecuteCSharpScript] Auto-fix increased errors ({errorCountBefore} → {checkErrors}), reverting");
                        continue;
                    }

                    script = candidate;
                }
            }

            // Loop exhausted while still applying fixes each iteration: `script` was mutated since
            // the last compile above. Compile it once more so the returned Compilation/Script always
            // match the final `script` text — the session path executes `resultScript` directly (no
            // separate recompile), so this pair must be trustworthy even on this rare exhaustion path.
            var finalOptions = baseOptions.WithReferences(references).WithImports(imports);
            resultScript = CreateOrContinueScript(script, finalOptions, previousScript);
            return resultScript.GetCompilation();
        }

        // Parses top-level `using` directives out of the script, merges them into `imports`,
        // and returns the script body with those directives removed.
        static void HoistUsingDirectives(ref string script, List<string> imports)
        {
            var root = SyntaxFactory.ParseSyntaxTree(script).GetCompilationUnitRoot();
            if (root.Usings.Count == 0)
                return;

            foreach (var usingDirective in root.Usings)
            {
                var namespaceName = usingDirective.Name.ToString();
                if (!imports.Contains(namespaceName))
                    imports.Add(namespaceName);
            }

            // Remove the using directives from the script body
            var stripped = root.RemoveNodes(root.Usings, SyntaxRemoveOptions.KeepNoTrivia);
            script = stripped?.GetText().ToString().TrimStart() ?? script;
        }

        static void AddCoreAssemblyReferences(List<MetadataReference> references)
        {
            var coreTypes = new[]
            {
                typeof(object),
                typeof(System.Linq.Enumerable),
                typeof(System.Collections.Generic.List<>),
                typeof(System.Collections.ArrayList),
                typeof(System.Threading.Tasks.Task),
                typeof(System.Text.StringBuilder),
                typeof(System.IO.File),
                typeof(System.Text.RegularExpressions.Regex),
                typeof(System.Math),
                // Unity
                typeof(UnityEngine.Debug),
                typeof(UnityEngine.GameObject),
                typeof(UnityEngine.Transform),
                typeof(UnityEngine.Component),
                typeof(UnityEngine.MonoBehaviour),
                typeof(UnityEngine.Object),
                typeof(UnityEngine.UI.Button),
                typeof(UnityEngine.UI.Image),
                typeof(UnityEngine.UI.Text),
                typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.EventSystems.BaseEventData),
                // UnityEditor
                typeof(UnityEditor.EditorApplication),
                typeof(UnityEditor.EditorUtility),
                typeof(UnityEditor.AssetDatabase),
                typeof(UnityEditor.Selection),
                typeof(UnityEditor.SceneManagement.EditorSceneManager),
            };

            var addedLocations = new HashSet<string>();
            foreach (var type in coreTypes)
            {
                var location = type.Assembly.Location;
                if (!string.IsNullOrEmpty(location) && addedLocations.Add(location))
                    references.Add(MetadataReference.CreateFromFile(location));
            }

            foreach (var name in new[] { "netstandard", "System.Runtime", "System.Core" })
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => GetAssemblySimpleName(a) == name);
                if (asm != null && !string.IsNullOrEmpty(asm.Location) && addedLocations.Add(asm.Location))
                    references.Add(MetadataReference.CreateFromFile(asm.Location));
            }
        }

        static string CreateShadowCopy(string sourcePath)
        {
            var sourceTime = File.GetLastWriteTimeUtc(sourcePath).Ticks;
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            var ext = Path.GetExtension(sourcePath);
            var versionedName = $"{fileName}_{sourceTime}{ext}";
            var destPath = Path.Combine(s_ShadowCopyDir, versionedName);

            Directory.CreateDirectory(s_ShadowCopyDir);

            if (File.Exists(destPath))
            {
                CodelyLogger.Log($"[ExecuteCSharpScript] Shadow copy exists: {versionedName}");
            }
            else
            {
                CleanupOldShadowCopies(fileName, sourceTime);
                File.Copy(sourcePath, destPath, overwrite: false);
                CodelyLogger.Log($"[ExecuteCSharpScript] Shadow copy created: {versionedName}");
            }

            var pdbSource = Path.ChangeExtension(sourcePath, ".pdb");
            var pdbDest = Path.ChangeExtension(destPath, ".pdb");
            if (File.Exists(pdbSource) && !File.Exists(pdbDest))
            {
                try { File.Copy(pdbSource, pdbDest, overwrite: false); }
                catch (IOException)
                {
                    CodelyLogger.LogWarning($"[ExecuteCSharpScript] Could not copy PDB for {fileName}");
                }
            }

            return destPath;
        }

        static void CleanupOldShadowCopies(string assemblyName, long currentTimestamp)
        {
            try
            {
                if (!Directory.Exists(s_ShadowCopyDir))
                    return;

                foreach (var file in Directory.GetFiles(s_ShadowCopyDir, $"{assemblyName}_*"))
                {
                    var nameNoExt = Path.GetFileNameWithoutExtension(file);
                    var lastUnderscore = nameNoExt.LastIndexOf('_');
                    if (lastUnderscore <= 0)
                        continue;

                    if (long.TryParse(nameNoExt.Substring(lastUnderscore + 1), out var fileTimestamp)
                        && fileTimestamp < currentTimestamp)
                    {
                        try { File.Delete(file); }
                        catch (IOException)
                        {
                            CodelyLogger.LogWarning($"[ExecuteCSharpScript] Could not delete old shadow copy: {Path.GetFileName(file)}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[ExecuteCSharpScript] Shadow copy cleanup failed: {e.Message}");
            }
        }

        // Reads a script file with encoding auto-detection. File.ReadAllText assumes UTF-8 when
        // there is no BOM, which corrupts files saved in a local ANSI code page (e.g. GBK/936 on
        // zh-CN Windows) — Chinese identifiers/strings then arrive as '�' and Roslyn reports
        // "error CS1056: Unexpected character". We honor any BOM, validate the bytes as UTF-8
        // ourselves (Mono's UTF8Encoding.throwOnInvalidBytes is unreliable and silently substitutes
        // '�' instead of throwing), and only fall back to a local code page when they are not UTF-8.
        static string ReadScriptFileSmart(string path)
        {
            var bytes = File.ReadAllBytes(path);

            // 1) Honor an explicit BOM.
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            // 2) No BOM: if the bytes are valid UTF-8 (the common case), decode as UTF-8.
            if (IsValidUtf8(bytes))
                return new UTF8Encoding(false).GetString(bytes);

            // 3) Not UTF-8 — if the bytes are structurally valid GBK/936 (the dominant non-UTF-8
            //    case for these scripts: zh-CN Windows), decode as GBK.
            if (IsValidGbk(bytes))
            {
                CodelyLogger.LogWarning(
                    $"[ExecuteCSharpScript] '{Path.GetFileName(path)}' is not valid UTF-8; " +
                    "decoded as GBK/936. Save the file as UTF-8 to avoid encoding issues.");
                return Encoding.GetEncoding(936).GetString(bytes);
            }

            // 4) Neither UTF-8 nor GBK — encoding cannot be detected confidently (e.g. Shift-JIS,
            //    Big5, or a single-byte ANSI page). Decode with the system default as a last resort
            //    and warn LOUDLY so the result is not silently trusted.
            var fallback = Encoding.Default;
            if (fallback.CodePage == 65001) // system is itself UTF-8 — useless for non-UTF-8 bytes
                fallback = Encoding.GetEncoding(936);
            CodelyLogger.LogWarning(
                $"[ExecuteCSharpScript] Could not confidently detect the encoding of " +
                $"'{Path.GetFileName(path)}' (not UTF-8, not GBK). Decoding with {fallback.WebName} " +
                $"(cp {fallback.CodePage}) as a last resort — output may be garbled. " +
                "Save the file as UTF-8 to fix this.");
            return fallback.GetString(bytes);
        }

        // Manual UTF-8 validation — does not rely on UTF8Encoding throwing (Mono does not).
        static bool IsValidUtf8(byte[] bytes)
        {
            int i = 0, n = bytes.Length;
            while (i < n)
            {
                byte b = bytes[i];
                if (b <= 0x7F) { i++; continue; }

                int extra;
                if ((b & 0xE0) == 0xC0) { extra = 1; if (b < 0xC2) return false; }      // 2-byte, reject overlong
                else if ((b & 0xF0) == 0xE0) { extra = 2; }                              // 3-byte
                else if ((b & 0xF8) == 0xF0) { extra = 3; if (b > 0xF4) return false; }  // 4-byte, reject > U+10FFFF
                else return false;                                                       // lone continuation / invalid lead

                if (i + extra >= n) return false;                                        // truncated sequence
                for (int j = 1; j <= extra; j++)
                    if ((bytes[i + j] & 0xC0) != 0x80) return false;                     // bad continuation byte

                i += extra + 1;
            }
            return true;
        }

        // Manual GBK/936 structural validation — does not rely on the decoder throwing (Mono does not).
        // GBK: single bytes 0x00-0x7F (ASCII) and 0x80 (euro in cp936); double bytes have a lead
        // byte 0x81-0xFE followed by a trailing byte 0x40-0x7E or 0x80-0xFE.
        static bool IsValidGbk(byte[] bytes)
        {
            int i = 0, n = bytes.Length;
            while (i < n)
            {
                byte b = bytes[i];
                if (b <= 0x7F || b == 0x80) { i++; continue; }  // ASCII / euro
                if (b == 0xFF) return false;                    // not a valid lead byte

                if (i + 1 >= n) return false;                   // dangling lead byte
                byte t = bytes[i + 1];
                bool validTrail = (t >= 0x40 && t <= 0x7E) || (t >= 0x80 && t <= 0xFE);
                if (!validTrail) return false;

                i += 2;
            }
            return true;
        }

        static void SaveScriptToTemp(string script)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("HHmmss");
                var tempPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "ExecutedCSharpScripts");
                Directory.CreateDirectory(tempPath);
                var filePath = Path.Combine(tempPath, $"script_{timestamp}_{script.Length}.cs");
                File.WriteAllText(filePath, script);
                CodelyLogger.Log($"[ExecuteCSharpScript] Script saved: {filePath}");
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[ExecuteCSharpScript] Failed to save script to temp: {e.Message}");
            }
        }

        static void StartLogCapture(bool enabled)
        {
            if (!enabled)
            {
                s_IsCapturingLogs = false;
                return;
            }
            s_CapturedLogs.Clear();
            s_IsCapturingLogs = true;
            // Remove first so a nested/repeated start cannot subscribe twice (duplicate log lines).
            Application.logMessageReceived -= OnLogMessageReceived;
            Application.logMessageReceived += OnLogMessageReceived;
        }

        static List<string> StopLogCapture()
        {
            Application.logMessageReceived -= OnLogMessageReceived;
            s_IsCapturingLogs = false;
            var logs = new List<string>(s_CapturedLogs);
            s_CapturedLogs.Clear();
            return logs;
        }

        static void OnLogMessageReceived(string logString, string stackTrace, LogType type)
        {
            if (!s_IsCapturingLogs)
                return;

            // Suppress this tool's own internal trace logs from the captured output —
            // the caller wants their script's logs, not our scaffolding.
            if (!string.IsNullOrEmpty(logString) && logString.StartsWith("[ExecuteCSharpScript]"))
                return;

            var entry = new StringBuilder();
            entry.Append($"[{type}] {logString}");
            if ((type == LogType.Error || type == LogType.Exception) && !string.IsNullOrEmpty(stackTrace))
                entry.Append($"\n{stackTrace}");

            s_CapturedLogs.Add(entry.ToString());
        }

        // Assembly.GetName() calls get_code_base() which throws EILSEQ on Mono when the project
        // path contains non-ASCII characters (ACP=936). Fall back to parsing FullName — it holds
        // the simple name before the first comma and never touches the code base.
        static string GetAssemblySimpleName(Assembly asm)
        {
            try
            {
                return asm.GetName().Name;
            }
            catch
            {
                var fullName = asm.FullName ?? "";
                var comma = fullName.IndexOf(',');
                return comma >= 0 ? fullName.Substring(0, comma) : fullName;
            }
        }

        // Builds the response returned for a successfully executed one-shot script — whether it
        // completed synchronously or (via ScheduleTask/ScheduleCoroutine) on a later frame. `stopwatch`
        // was started when the command came in, so elapsed_ms is the real end-to-end time either way.
        // Only the one-shot path reaches this (HandleSessionExecution builds its own response).
        static object BuildScriptSuccessResponse(object result, List<string> logs,
            System.Diagnostics.Stopwatch stopwatch, object recordingResponse = null)
        {
            var formatted = FormatResultValue(result);
            var data = new Dictionary<string, object>
            {
                { "result", formatted },
                { "elapsed_ms", stopwatch.ElapsedMilliseconds },
                { "logs", logs },
                { "log_count", logs.Count },
            };

            if (recordingResponse is Dictionary<string, object> recording)
            {
                bool recordingSucceeded =
                    recording.TryGetValue("success", out object success) &&
                    success is bool succeeded &&
                    succeeded;
                if (!recordingSucceeded)
                {
                    string failureMessage = recording.TryGetValue("message", out object failure)
                        ? failure?.ToString()
                        : "unknown recording error";
                    return Response.Error(
                        $"C# script executed successfully, but Game View MP4 recording failed: {failureMessage}");
                }

                data["recording"] = recording.TryGetValue("data", out object recordingData)
                    ? recordingData
                    : recording;
            }

            var message = string.IsNullOrEmpty(formatted)
                ? "C# script executed successfully."
                : $"C# script executed successfully.\nresult: {formatted}";
            return Response.Success(message, data);
        }

        static JobContext ScheduleRecordingCompletion(
            object result, List<string> logs, System.Diagnostics.Stopwatch stopwatch,
            ManageScreenshot.Mp4RecordingHandle recording)
        {
            var ctx = CreateCoroutineJob();

            IEnumerator Wrapper()
            {
                bool collected = false;
                try
                {
                    while (ManageScreenshot.GetGameViewMp4RecordingStatus(recording) ==
                           ManageScreenshot.Mp4RecordingStatus.Pending)
                        yield return null;

                    object recordingResponse =
                        ManageScreenshot.CollectGameViewMp4Recording(recording);
                    collected = true;
                    ctx.SetResult(BuildScriptSuccessResponse(
                        result, logs, stopwatch, recordingResponse));
                }
                finally
                {
                    if (!collected)
                        ManageScreenshot.CancelGameViewMp4Recording(recording);
                    s_InOneShotExecution = false;
                }
            }

            CoroutineRunner.RunJob(ctx, Wrapper(), timeoutSeconds: 0);
            return ctx;
        }

        // Builds the response returned when a script throws (synchronously, or from an awaited Task /
        // coroutine driven by a runner). A plain Response.Error with the whole failure in `message`
        // and NO `data` payload: the client renders a message-only error as a FAILED tool call (❌),
        // but a response carrying `data` as a success (it shows the data and ignores success:false).
        // So a data-bearing failure would look like a pass — keep failures data-free. Any captured
        // logs are appended to the message (the dropped data was their only other channel).
        static object BuildScriptFailureResponse(Exception e, List<string> logs)
        {
            var enhancedError = ReplGuard.EnhanceError(e);
            var message = $"C# script execution failed: {enhancedError}\n{SafeGetStackTrace(e)}";
            if (logs != null && logs.Count > 0)
                message += "\n\nLogs:\n" + string.Join("\n", logs);
            return Response.Error(message);
        }

        // timeoutSeconds comes from the request (default 300s, cap 3600s; explicit 0 = no
        // deadline). Unbounded scripts used to pass 0 unconditionally and could hang forever.
        //
        // Drives a Task the script returned or is still running on the AsyncTaskRunner.
        // Wrapper returns a Response; AsyncTaskRunner delivers it (SetError for a success:false
        // failure Response, SetResult otherwise).
        static JobContext ScheduleTask(
            Task task, bool captureLogs, System.Diagnostics.Stopwatch stopwatch,
            ManageScreenshot.Mp4RecordingHandle recording = null)
        {
            var ctx = CreateTaskJob();

            async Task<object> Wrapper()
            {
                try
                {
                    await task;
                    // Surface the script's actual return value (the sync and completed-task paths
                    // already do). For a Task<T> that is the awaited T; a non-generic Task has none.
                    var userResult = GetTaskResult(task);
                    var logs = captureLogs ? StopLogCapture() : new List<string>();
                    if (recording == null)
                        return BuildScriptSuccessResponse(userResult, logs, stopwatch);

                    while (ManageScreenshot.GetGameViewMp4RecordingStatus(recording) ==
                           ManageScreenshot.Mp4RecordingStatus.Pending)
                        await Task.Delay(10);

                    object recordingResponse =
                        ManageScreenshot.CollectGameViewMp4Recording(recording);
                    recording = null;
                    return BuildScriptSuccessResponse(
                        userResult, logs, stopwatch, recordingResponse);
                }
                catch (Exception e)
                {
                    ManageScreenshot.CancelGameViewMp4Recording(recording);
                    recording = null;
                    var logs = captureLogs ? StopLogCapture() : new List<string>();
                    return BuildScriptFailureResponse(e, logs);
                }
                finally
                {
                    ManageScreenshot.CancelGameViewMp4Recording(recording);
                    // This job only ever runs for the one-shot path (the session path never returns
                    // a JobContext) — the flag was left set by HandleOneShotExecution when it handed
                    // off to us instead of resetting it itself, so this is where it finally clears.
                    s_InOneShotExecution = false;
                }
            }

            AsyncTaskRunner.RunJob(ctx, Wrapper(), timeoutSeconds: s_AsyncTimeoutSeconds);
            return ctx;
        }

        // Extracts Task<T>.Result via reflection (T is unknown at compile time). A non-generic Task
        // has no result → null. Only called after the Task has completed successfully.
        static object GetTaskResult(Task task)
        {
            var type = task.GetType();
            if (!type.IsGenericType)
                return null;

            var result = type.GetProperty("Result")?.GetValue(task);

            // A non-generic `async Task` is really a Task<VoidTaskResult> at runtime, so Result is
            // the internal VoidTaskResult sentinel — not a user value. Report it as an empty result
            // (compare by name; the type is internal and can't be referenced directly).
            if (result != null && result.GetType().FullName == "System.Threading.Tasks.VoidTaskResult")
                return "";

            return result;
        }

        // Drives a coroutine the script returned on the CoroutineRunner (one MoveNext per frame).
        // CoroutineRunner nests yielded IEnumerators, so `yield return routine` runs it to completion.
        static JobContext ScheduleCoroutine(IEnumerator routine, bool captureLogs,
            System.Diagnostics.Stopwatch stopwatch,
            ManageScreenshot.Mp4RecordingHandle recording = null)
        {
            var ctx = CreateCoroutineJob();

            IEnumerator Wrapper()
            {
                try
                {
                    yield return routine;
                    var logs = captureLogs ? StopLogCapture() : new List<string>();
                    if (recording != null)
                    {
                        while (ManageScreenshot.GetGameViewMp4RecordingStatus(recording) ==
                               ManageScreenshot.Mp4RecordingStatus.Pending)
                            yield return null;
                    }

                    object recordingResponse = recording == null
                        ? null
                        : ManageScreenshot.CollectGameViewMp4Recording(recording);
                    recording = null;
                    ctx.SetResult(BuildScriptSuccessResponse(
                        null, logs, stopwatch, recordingResponse));
                }
                finally
                {
                    ManageScreenshot.CancelGameViewMp4Recording(recording);
                    // If the nested routine throws, JobRunnerBase SetError's and this frame never
                    // reaches the success path — still drop the log subscription.
                    if (captureLogs && s_IsCapturingLogs)
                        StopLogCapture();

                    // This job only ever runs for the one-shot path — the flag was left set by
                    // HandleOneShotExecution when it handed off to us instead of resetting it itself,
                    // so this is where it finally clears. (The session-path counterpart,
                    // ScheduleSessionCoroutine below, never touches this flag.)
                    s_InOneShotExecution = false;
                }
            }

            CoroutineRunner.RunJob(ctx, Wrapper(), timeoutSeconds: s_AsyncTimeoutSeconds);
            return ctx;
        }

        // --- session-path async scheduling ---
        // Counterparts to ScheduleTask/ScheduleCoroutine above, used when a session submission's
        // ReturnValue is itself a Task/coroutine still in flight. Unlike the one-shot versions, the
        // caller (HandleSessionExecution) has already committed s_SessionState/s_SessionId/
        // s_SubmissionCount synchronously before scheduling — these only need to produce the eventual
        // response, not decide whether the submission "counts" (see the comment in
        // HandleSessionExecution for why no busy gate / identity check is needed here).

        // Builds the response for a successfully executed session submission — whether it completed
        // synchronously or (via ScheduleSessionTask/ScheduleSessionCoroutine/ScheduleSessionOuterTask)
        // on a later frame.
        static object BuildSessionSuccessResponse(object result, List<string> logs,
            System.Diagnostics.Stopwatch stopwatch, string sessionId, string sessionStatus, int submissionCount)
        {
            // Same contract as BuildScriptSuccessResponse: data.result plus a message the UI can show.
            var formatted = FormatResultValue(result);
            var message = string.IsNullOrEmpty(formatted)
                ? "C# script executed successfully."
                : $"C# script executed successfully.\nresult: {formatted}";
            return Response.Success(
                message,
                new
                {
                    result = formatted,
                    return_type = SafeTypeName(result),
                    elapsed_ms = stopwatch.ElapsedMilliseconds,
                    script_session_id = sessionId,
                    session_status = sessionStatus,
                    submission_count = submissionCount,
                    logs,
                    log_count = logs.Count
                });
        }

        // Builds the response for a session submission whose script raised an exception the
        // session's `catchException` callback captured (synchronously), or whose returned Task later
        // threw (asynchronously). Uses Response.Success (not Response.Error) deliberately, matching
        // the rest of the session path: a script's own runtime failure is not a bridge-level error,
        // and the session itself survives — only this submission is reported as failed.
        static object BuildSessionFailureResponse(Exception exception, List<string> logs,
            bool possibleSessionRebuild, string sessionId, string sessionStatus, int submissionCount)
        {
            var stackTrace = SafeGetStackTrace(exception);
            var enhancedError = ReplGuard.EnhanceError(exception, possibleSessionRebuild);
            return Response.Success(
                $"C# script execution failed: {enhancedError}\n{stackTrace}",
                new
                {
                    error = enhancedError,
                    stack_trace = stackTrace,
                    script_session_id = sessionId,
                    session_status = sessionStatus,
                    submission_count = submissionCount,
                    logs,
                    log_count = logs.Count
                });
        }

        // Drives a Task a session submission returned (its own ReturnValue — not the outer
        // RunAsync/RunFromAsync task) on the AsyncTaskRunner.
        static JobContext ScheduleSessionTask(Task task, bool captureLogs, System.Diagnostics.Stopwatch stopwatch,
            string sessionId, string sessionStatus, int submissionCount)
        {
            var ctx = CreateTaskJob();

            async Task<object> Wrapper()
            {
                try
                {
                    await task;
                    var userResult = GetTaskResult(task);
                    var logs = captureLogs ? StopLogCapture() : new List<string>();
                    return BuildSessionSuccessResponse(userResult, logs, stopwatch, sessionId, sessionStatus, submissionCount);
                }
                catch (Exception e)
                {
                    var logs = captureLogs ? StopLogCapture() : new List<string>();
                    // possibleSessionRebuild only applies to the "session was silently rebuilt after a
                    // reload" compile-time hint — by the time this task was scheduled the session had
                    // already been successfully created/continued, so it is always false here.
                    return BuildSessionFailureResponse(e, logs, possibleSessionRebuild: false,
                        sessionId, sessionStatus, submissionCount);
                }
            }

            AsyncTaskRunner.RunJob(ctx, Wrapper(), timeoutSeconds: s_AsyncTimeoutSeconds);
            return ctx;
        }

        // Drives a coroutine a session submission returned on the CoroutineRunner. As with
        // ScheduleCoroutine, an exception thrown while the runner is directly pumping the nested
        // `routine` (as opposed to this wrapper's own code) is not caught here — it falls through to
        // JobRunnerBase's generic Response.Error(SafeError(ex)) instead of the nicer
        // ReplGuard.EnhanceError treatment. That is a pre-existing limitation shared with the
        // one-shot path's ScheduleCoroutine, not something new to the session path.
        static JobContext ScheduleSessionCoroutine(IEnumerator routine, bool captureLogs,
            System.Diagnostics.Stopwatch stopwatch, string sessionId, string sessionStatus, int submissionCount)
        {
            var ctx = CreateCoroutineJob();

            IEnumerator Wrapper()
            {
                try
                {
                    yield return routine;
                    var logs = captureLogs ? StopLogCapture() : new List<string>();
                    ctx.SetResult(BuildSessionSuccessResponse(null, logs, stopwatch, sessionId, sessionStatus, submissionCount));
                }
                finally
                {
                    if (captureLogs && s_IsCapturingLogs)
                        StopLogCapture();
                }
            }

            CoroutineRunner.RunJob(ctx, Wrapper(), timeoutSeconds: s_AsyncTimeoutSeconds);
            return ctx;
        }

        // Defensive fallback for the case where the RunAsync/RunFromAsync task itself has not
        // completed synchronously. Currently unreachable — CheckForBlockingCalls rejects a genuine
        // top-level `await` before this is ever scheduled — but awaits it properly instead of the old
        // RunScriptSync's `.Wait()`, which would deadlock here (the classic top-level-await deadlock
        // this whole guard exists to prevent). Does not re-check ReturnValue for a further nested
        // Task/coroutine, matching the scope of one-shot's own equivalent fallback
        // (ExecuteFromCompilation's `if (!scriptTask.IsCompleted) return ScheduleTask(scriptTask, ...)`).
        static JobContext ScheduleSessionOuterTask(System.Threading.Tasks.Task<ScriptState<object>> scriptStateTask,
            bool captureLogs, System.Diagnostics.Stopwatch stopwatch, string executingSessionId,
            string sessionStatus, int submissionIndexBeforeRun, bool possibleSessionRebuild)
        {
            var ctx = CreateTaskJob();

            async Task<object> Wrapper()
            {
                ScriptState<object> newState;
                try
                {
                    newState = await scriptStateTask;
                }
                catch (Exception e)
                {
                    // The outer task itself faulted (a bridge-level failure, not a script exception —
                    // those are captured into ScriptState.Exception via catchException, not thrown
                    // here). The session was never committed for this submission, matching the
                    // synchronous path's outer `catch (Exception e)` behavior.
                    var failLogs = captureLogs ? StopLogCapture() : new List<string>();
                    return BuildSessionFailureResponse(e, failLogs, possibleSessionRebuild,
                        executingSessionId, sessionStatus, submissionIndexBeforeRun);
                }

                s_SessionState = newState;
                s_SessionId = executingSessionId;
                s_SubmissionCount = submissionIndexBeforeRun + 1;

                var logs = captureLogs ? StopLogCapture() : new List<string>();
                return newState.Exception != null
                    ? BuildSessionFailureResponse(newState.Exception, logs, possibleSessionRebuild,
                        s_SessionId, sessionStatus, s_SubmissionCount)
                    : BuildSessionSuccessResponse(newState.ReturnValue, logs, stopwatch,
                        s_SessionId, sessionStatus, s_SubmissionCount);
            }

            AsyncTaskRunner.RunJob(ctx, Wrapper(), timeoutSeconds: s_AsyncTimeoutSeconds);
            return ctx;
        }
    }
}
