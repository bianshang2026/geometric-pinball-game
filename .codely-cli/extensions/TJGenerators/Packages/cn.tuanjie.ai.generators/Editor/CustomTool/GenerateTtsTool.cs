using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Codely.Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEditor;

#if UNITY_EDITOR
using TJGenerators;
using TJGenerators.Generators;
using TJGenerators.Config;
using TJGenerators.Pipeline;
using TJGenerators.Utils;
using Unity.EditorCoroutines.Editor;
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// CustomTool for generating speech audio (TTS) using MiniMax TTS via the fal-minimax-tts endpoint.
    /// Supports text-to-speech with preset and custom voice IDs.
    /// Output is an MP3 AudioClip asset saved to Assets/TJGenerators/History/.
    /// Domain-reload recovery is handled by AudioDomainReloadRecovery in GenerateAudioClipTool.cs
    /// (shares AudioClipTaskTracker and AudioPipelineHost).
    /// </summary>
    public static class GenerateTtsTool
    {
        /// <summary>Built-in MiniMax preset used when <c>voice_id</c> is omitted or blank.</summary>
        internal const string DefaultVoiceId = "Chinese (Mandarin)_Gentleman";

        [ExecuteCustomTool.CustomTool("generate_tts",
            "Generate speech audio (TTS) from text using MiniMax TTS. " +
            "Supports Chinese (Mandarin), English, and Japanese voices with preset or custom voice IDs. " +
            "Output is an MP3 AudioClip asset saved to Assets/TJGenerators/History/. " +
            "Parameters: prompt (text to synthesize, required), " +
            "voice_id (optional; omit or empty → defaults to 'Chinese (Mandarin)_Gentleman'. " +
            "For a custom voice: call voice_clone first, then pass the returned custom_voice_id here), " +
            "output_path (optional asset save path), " +
            "play_on_awake (optional bool, default false). " +
            "IMPORTANT: Generation takes 10-30 seconds. A placeholder_path (MP3) is returned immediately. " +
            "A <bg_task_done> notification will arrive automatically upon completion.")]
        public static object GenerateTts(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateTtsTool] Generating TTS with parameters: {parameters}");

                string prompt = parameters["prompt"]?.ToString();
                string voiceId = ResolveVoiceId(parameters);
                string outputPath = parameters["output_path"]?.ToString();
                string sessionId = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());
                bool playOnAwake = parameters["play_on_awake"] != null ? parameters["play_on_awake"].ToObject<bool>() : false;

                if (string.IsNullOrEmpty(prompt))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "'prompt' parameter is required" }
                    };
                }

                int maxLen = TJGeneratorsPromptLimits.GetMaxLength("minimax-tts");
                if (prompt.Length > maxLen)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "INVALID_PARAMS" },
                        { "message", $"Prompt length ({prompt.Length}) exceeds the {maxLen} character limit." }
                    };
                }

                // Load minimax-tts generator config
                var config = ConfigManager.GetGeneratorConfig(ConfigType.Music, "minimax-tts");
                if (config == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "Cannot find generator config for 'minimax-tts'. Ensure the TJGenerators package is installed and the Editor has finished compiling." }
                    };
                }

                // Create generator and set inputs
                var generator = new DynamicGenerator(config);
                generator.SetTextPrompt(prompt);

                // Always apply resolved voiceId (default when omitted/blank) so backend never gets voiceid:""
                ApplyTtsParameters(generator, parameters);

                // 阶段1：同步提交任务到后端
                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[GenerateTtsTool] 任务提交失败 [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[GenerateTtsTool] 任务提交成功，backend_task_id={submitResult.BackendTaskId}");

                // 提交成功后才创建 placeholder
                var (placeholderPath, audioDownloadPath) = BuildTtsPaths(outputPath);

                // Create tracked task
                string capturedBackendTaskId = submitResult.BackendTaskId;
                string taskId = AudioClipTaskTracker.CreateTask("minimax-tts", prompt, placeholderPath, capturedBackendTaskId);

                // Create pipeline host with audio-specific callbacks
                var host = new AudioPipelineHost(placeholderPath, audioDownloadPath, sessionId, isBgm: false, playOnAwake: playOnAwake,
                    (savedPath, previewUrl) =>
                    {
                        AudioClipTaskTracker.MarkCompleted(taskId, savedPath, previewUrl);
                        var t = AudioClipTaskTracker.GetTask(taskId);
                        GenerationNotifier.NotifyCompleted("generate_tts", taskId, capturedBackendTaskId,
                            new JObject
                            {
                                ["session_id"]       = sessionId,
                                ["generator_id"]     = "minimax-tts",
                                ["prompt"]           = prompt ?? "",
                                ["voice_id"]         = voiceId ?? "",
                                ["audio_path"]       = savedPath ?? "",
                                ["preview_url"]      = previewUrl ?? "",
                                ["progress"]         = 100,
                                ["start_time"]       = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["end_time"]         = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
                            });
                    },
                    errorMsg =>
                    {
                        AudioClipTaskTracker.MarkFailed(taskId, errorMsg);
                        GenerationNotifier.NotifyFailed("generate_tts", taskId, capturedBackendTaskId, errorMsg,
                            new JObject { ["session_id"] = sessionId, ["generator_id"] = "minimax-tts", ["prompt"] = prompt ?? "", ["voice_id"] = voiceId ?? "" });
                    });

                // 阶段2：异步轮询（跳过提交）。Domain reload 恢复见 GenerateAudioClipTool.AudioDomainReloadRecovery。
                var pipeline = new GenerationPipeline(host, ConfigType.Music, GenerationRequestOrigin.Agent, sessionId, "generate_tts");
                string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(placeholderPath);
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId));

                TJLog.Log($"[GenerateTtsTool] 轮询已启动，task_id={taskId}, backend_task_id={submitResult.BackendTaskId}, placeholder: {placeholderPath}");

                return new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "message",
                        "TTS generation started. " +
                        "STEP 1 (do now): Note the placeholder_path — a silent MP3 is available immediately. " +
                        "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                        "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn (~15s) " +
                        "containing ALL generation results (audio_path, preview_url, timing, etc.). " +
                        "*** POLLING IS STRICTLY FORBIDDEN. Only call query_tts_status ONCE as a last-resort fallback. ***" },
                    { "task_id",            taskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       "minimax-tts" },
                    { "prompt",             prompt },
                    { "voice_id",           voiceId },
                    { "placeholder_path",   placeholderPath },
                    { "estimated_wait_seconds", 15 },
                    { "notification_mode",  "bg_task_done" },
                    { "preview_url",        PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateTtsTool] Error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error generating TTS: {e.Message}" }
                };
            }
#else
            return new Dictionary<string, object>
            {
                { "success", false },
                { "message", "This tool only works in Unity Editor." }
            };
#endif
        }

        [ExecuteCustomTool.CustomTool("query_tts_status",
            "Query the status of a TTS generation task. Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "When completed, returns 'audio_path' with the AudioClip asset path in the project. " +
            "Status values: 'generating', 'completed', 'failed'. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QueryTtsStatus(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                string taskId = parameters["task_id"]?.ToString();

                if (string.IsNullOrEmpty(taskId))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "'task_id' parameter is required" }
                    };
                }

                var task = AudioClipTaskTracker.GetTask(taskId);

                if (task == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Task '{taskId}' not found. It may have been completed and cleaned up." }
                    };
                }

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "task_id", task.TaskId },
                    { "generator_id", task.GeneratorId },
                    { "status", task.Status },
                    { "progress", task.Progress },
                    { "prompt", task.Prompt },
                    { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                };

                if (!string.IsNullOrEmpty(task.AudioPath))
                    result["audio_path"] = task.AudioPath;

                result["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);

                if (!string.IsNullOrEmpty(task.ErrorMessage))
                    result["error"] = task.ErrorMessage;

                if (task.EndTime.HasValue)
                {
                    result["end_time"] = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                    result["duration_seconds"] = (int)(task.EndTime.Value - task.StartTime).TotalSeconds;
                }

                if (task.Status == "generating")
                {
                    if (!string.IsNullOrEmpty(task.PlaceholderPath))
                        result["placeholder_path"] = task.PlaceholderPath;
                }

                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateTtsTool] Query error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error querying task status: {e.Message}" }
                };
            }
#else
            return new Dictionary<string, object>
            {
                { "success", false },
                { "message", "This tool only works in Unity Editor." }
            };
#endif
        }

        [ExecuteCustomTool.CustomTool("list_tts_tasks", "List all active and recent TTS generation tasks")]
        public static object ListTtsTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var allTasks = AudioClipTaskTracker.GetAllTasks();
                var taskList = new List<Dictionary<string, object>>();

                foreach (var task in allTasks)
                {
                    if (task.GeneratorId != "minimax-tts")
                        continue;

                    var taskData = new Dictionary<string, object>
                    {
                        { "task_id", task.TaskId },
                        { "generator_id", task.GeneratorId },
                        { "status", task.Status },
                        { "progress", task.Progress },
                        { "prompt", task.Prompt },
                        { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                    };

                    if (!string.IsNullOrEmpty(task.AudioPath))
                        taskData["audio_path"] = task.AudioPath;

                    taskData["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);

                    if (!string.IsNullOrEmpty(task.ErrorMessage))
                        taskData["error"] = task.ErrorMessage;

                    if (task.EndTime.HasValue)
                        taskData["end_time"] = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");

                    taskList.Add(taskData);
                }

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "count", taskList.Count },
                    { "tasks", taskList }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateTtsTool] List error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error listing tasks: {e.Message}" }
                };
            }
#else
            return new Dictionary<string, object>
            {
                { "success", false },
                { "message", "This tool only works in Unity Editor." }
            };
#endif
        }

#if UNITY_EDITOR
        private static (string placeholderPath, string downloadPath) BuildTtsPaths(string outputPath)
        {
            string audioPath;
            if (!string.IsNullOrEmpty(outputPath))
            {
                string dir = Path.GetDirectoryName(outputPath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir))
                    EnsureAssetDatabaseFolder(dir);
                audioPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.ChangeExtension(outputPath, ".mp3"));
            }
            else
            {
                if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                    AssetDatabase.CreateFolder("Assets", "TJGenerators");
                if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                    AssetDatabase.CreateFolder("Assets/TJGenerators", "History");
                string uniqueName = "TTS_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                audioPath = AssetDatabase.GenerateUniqueAssetPath(
                    "Assets/TJGenerators/History/" + uniqueName + ".mp3");
            }

            // Create a blank MP3 placeholder so AI Agent can assign it immediately
            TJGeneratorsAudioUtils.CreateBlankMp3Clip(audioPath);

            return (audioPath, audioPath);
        }

        private static void EnsureAssetDatabaseFolder(string folderPath)
        {
            folderPath = folderPath.Replace('\\', '/').TrimEnd('/');
            string[] parts = folderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        /// <summary>
        /// Resolves <c>voice_id</c>: omitted / null / whitespace → <see cref="DefaultVoiceId"/>.
        /// Empty string from the agent must not reach the backend as <c>voiceid:""</c>.
        /// </summary>
        internal static string ResolveVoiceId(JObject parameters)
        {
            string voiceId = parameters?["voice_id"]?.ToString();
            return string.IsNullOrWhiteSpace(voiceId) ? DefaultVoiceId : voiceId.Trim();
        }

        private static void ApplyTtsParameters(DynamicGenerator generator, JObject parameters)
        {
            generator.SetParameter("voiceId", ResolveVoiceId(parameters));
        }

        /// <summary>Test hook for offline parameter-mapping checks.</summary>
        internal static void ApplyTtsParametersInternal(DynamicGenerator generator, JObject parameters)
            => ApplyTtsParameters(generator, parameters);
#endif
    }
}
