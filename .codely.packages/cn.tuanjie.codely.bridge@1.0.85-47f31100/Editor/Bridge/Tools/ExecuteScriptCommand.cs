using System;
using System.Collections.Generic;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityTcp.Editor.Helpers;
using UnityTcp.Editor.Tools.Jobs;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Mode-aware entry points for the split editor/runtime script commands.
    /// The outer StepJob owns the native request while any async script runner is detached and
    /// collected by job id, preventing the inner and outer runners from answering the same request.
    /// </summary>
    public static class ExecuteScriptCommand
    {
        public const string EditorCommandName = "exec_editor_script";
        public const string RuntimeCommandName = "exec_runtime_script";
        private const int ModeSwitchBudgetSeconds = 120;

        public static object HandleEditorScript(JObject @params)
            => Handle(@params, EditorCommandName, playMode: false);

        public static object HandleRuntimeScript(JObject @params)
            => Handle(@params, RuntimeCommandName, playMode: true);

        private static object Handle(JObject @params, string commandName, bool playMode)
        {
            @params = @params ?? new JObject();
            object validationError = Validate(@params, playMode);
            if (validationError != null)
                return validationError;

            int scriptTimeout = ExecuteCSharpScript.ResolveAsyncTimeoutSeconds(@params);
            int outerTimeout = scriptTimeout == 0
                ? 0
                : Math.Min(int.MaxValue - ModeSwitchBudgetSeconds, scriptTimeout) +
                  ModeSwitchBudgetSeconds;

            return StepJobRunner.Start(
                CommandContext.RequestId,
                commandName,
                new ScriptExecutionJob
                {
                    CommandName = commandName,
                    PlayMode = playMode,
                    ParamsJson = @params.ToString(Formatting.None),
                },
                outerTimeout);
        }

        internal static object Validate(JObject @params, bool playMode)
        {
            if (@params?["execution_mode"] != null)
                return Response.Error(
                    "'execution_mode' is not accepted by exec_editor_script or " +
                    "exec_runtime_script; the command name selects the required mode.");

            if (!playMode && @params?["record_game_view"] != null)
                return Response.Error(
                    "'record_game_view' is supported only by exec_runtime_script.");

            if (playMode && @params?["enable_repl"] != null)
                return Response.Error(
                    "'enable_repl' is not accepted by exec_runtime_script. " +
                    "Use exec_editor_script for REPL sessions.");

            if (playMode && @params?["unlock_domain_reload"] != null)
                return Response.Error(
                    "'unlock_domain_reload' is not accepted by exec_runtime_script. " +
                    "Use exec_editor_script for REPL sessions.");

            if (playMode && @params?["script_session_id"] != null)
                return Response.Error(
                    "'script_session_id' is not accepted by exec_runtime_script. " +
                    "Use exec_editor_script for REPL sessions.");

            return null;
        }
    }

    /// <summary>
    /// Serialized orchestration state for a split script command. Params are retained as JSON text;
    /// no JObject, Task, iterator, or other domain-bound object is persisted.
    /// </summary>
    public class ScriptExecutionJob : StepJob
    {
        public string CommandName;
        public bool PlayMode;
        public string ParamsJson;
        public string InnerJobId;
        public bool ScriptStarted;

        protected override JobStep[] BuildSteps() => new[]
        {
            JobStep.Nested(
                PlayMode ? "ensure-play-mode" : "ensure-edit-mode",
                CreateModeSwitchJob,
                OnModeSwitchComplete,
                skip: IsTargetModeReady),

            new JobStep(
                "start-script",
                StartScript,
                skip: () => IsFinished),

            new JobStep(
                "await-script",
                PollScript,
                () => IsFinished,
                () => IsFinished || string.IsNullOrEmpty(InnerJobId)),
        };

        protected virtual bool IsTargetModeReady()
            => PlayMode
                ? EditorApplication.isPlaying && !EditorApplication.isCompiling
                : PlayModeState.IsStopped();

        protected virtual StepJob CreateModeSwitchJob()
            => PlayMode ? (StepJob)new PlayJob() : new StopPlayModeJob();

        protected virtual object ExecuteScript(JObject @params)
            => ExecuteCSharpScript.HandleCommandForOrchestration(@params, CommandName);

        protected virtual DetachedJobs.Status CheckInnerJob(
            string jobId, out string responseJson)
            => DetachedJobs.Check(jobId, out responseJson);

        private void OnModeSwitchComplete()
        {
            if (LastSubJobSucceeded)
                return;

            if (!string.IsNullOrEmpty(LastSubJobResponseJson))
                CompleteWithJson(LastSubJobResponseJson, failed: true);
            else
                Fail($"Failed to prepare the mode required by {CommandName}; script was not executed.");
        }

        private void StartScript()
        {
            if (ScriptStarted)
                return;
            ScriptStarted = true;

            JObject @params;
            try
            {
                @params = string.IsNullOrEmpty(ParamsJson)
                    ? new JObject()
                    : JObject.Parse(ParamsJson);
            }
            catch (Exception ex)
            {
                Fail($"Could not restore parameters for {CommandName}: {ex.Message}");
                return;
            }

            object validationError = ExecuteScriptCommand.Validate(@params, PlayMode);
            if (validationError != null)
            {
                CompleteObject(validationError);
                return;
            }

            object result;
            try
            {
                result = ExecuteScript(@params);
            }
            catch (Exception ex)
            {
                Fail(JobRunnerBase.SafeError(ex));
                return;
            }

            if (result is JobContext inner)
            {
                InnerJobId = inner.JobId;
                return;
            }

            CompleteObject(result);
        }

        private void PollScript()
        {
            var status = CheckInnerJob(InnerJobId, out string responseJson);
            switch (status)
            {
                case DetachedJobs.Status.Pending:
                    return;
                case DetachedJobs.Status.Complete:
                    CompleteResponseJson(responseJson);
                    return;
                default:
                    Fail(
                        $"The asynchronous {CommandName} job disappeared before producing a result.");
                    return;
            }
        }

        private void CompleteObject(object response)
        {
            string json = JsonConvert.SerializeObject(
                response ?? Response.Success($"{CommandName} completed."));
            CompleteResponseJson(json);
        }

        private void CompleteResponseJson(string json)
        {
            bool failed = false;
            try
            {
                failed = JObject.Parse(json)?["success"]?.ToObject<bool?>() == false;
            }
            catch
            {
                // Preserve malformed inner output verbatim; delivery is still terminal.
            }
            CompleteWithJson(json, failed);
        }
    }
}
