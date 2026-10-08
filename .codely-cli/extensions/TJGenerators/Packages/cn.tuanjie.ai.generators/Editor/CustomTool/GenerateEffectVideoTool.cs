using System;
using System.Collections;
using System.Collections.Generic;
using Codely.Newtonsoft.Json.Linq;
using UnityEngine;

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using TJGenerators;
using TJGenerators.Generators;
using TJGenerators.Config;
using TJGenerators.Pipeline;
using TJGenerators.PostProcessing;
using TJGenerators.Utils;
using Unity.EditorCoroutines.Editor;
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Tracks active effect video generation tasks.
    /// </summary>
    public static class EffectVideoTaskTracker
    {
#if UNITY_EDITOR
        [Serializable]
        private class PersistedTask
        {
            public string taskId;
            public string prompt;
            public string status;
            public int    progress;
            public string videoPath;
            public string materialPath;
            public string errorMessage;
            public long   startTimeTicks;
            public long   endTimeTicks;
            public string previewUrl;
            public string placeholderPath;
            public string backendTaskId;
        }

        public class EffectVideoTaskInfo : IGenerationTaskInfo
        {
            public string TaskId { get; set; }
            public string Prompt { get; set; }
            public string Status { get; set; }
            public int Progress { get; set; }
            public string VideoPath { get; set; }
            public string MaterialPath { get; set; }
            public string ErrorMessage { get; set; }
            public string PreviewUrl { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public string PlaceholderPath { get; set; }
            public string BackendTaskId { get; set; }
        }

        private static readonly GenerationTaskTrackerStore<EffectVideoTaskInfo, PersistedTask> Store =
            new GenerationTaskTrackerStore<EffectVideoTaskInfo, PersistedTask>(
                "TJGen_EffectVideo", BuildPersisted, FromPersisted);

        private static PersistedTask BuildPersisted(EffectVideoTaskInfo info) => new PersistedTask
        {
            taskId          = info.TaskId,
            prompt          = info.Prompt ?? "",
            status          = info.Status,
            progress        = info.Progress,
            videoPath       = info.VideoPath ?? "",
            materialPath    = info.MaterialPath ?? "",
            errorMessage    = info.ErrorMessage ?? "",
            startTimeTicks  = info.StartTime.Ticks,
            endTimeTicks    = info.EndTime?.Ticks ?? 0,
            previewUrl      = info.PreviewUrl ?? "",
            placeholderPath = info.PlaceholderPath ?? "",
            backendTaskId   = info.BackendTaskId ?? ""
        };

        private static EffectVideoTaskInfo FromPersisted(PersistedTask p) => new EffectVideoTaskInfo
        {
            TaskId          = p.taskId,
            Prompt          = p.prompt,
            Status          = p.status,
            Progress        = p.progress,
            VideoPath       = p.videoPath,
            MaterialPath    = p.materialPath,
            ErrorMessage    = p.errorMessage,
            PreviewUrl      = p.previewUrl,
            StartTime       = new DateTime(p.startTimeTicks),
            EndTime         = p.endTimeTicks > 0 ? (DateTime?)new DateTime(p.endTimeTicks) : null,
            PlaceholderPath = p.placeholderPath,
            BackendTaskId   = p.backendTaskId
        };

        internal static void ApplyTaskUpdate(EffectVideoTaskInfo task, Action<EffectVideoTaskInfo> mutate) =>
            Store.ApplyTaskUpdate(task, mutate);

        public static string CreateTask(string prompt, string placeholderPath, string backendTaskId = null)
        {
            string taskId = Store.AllocateTaskId("effectvideo");
            var task = new EffectVideoTaskInfo
            {
                TaskId          = taskId,
                Prompt          = prompt ?? "",
                Status          = "generating",
                StartTime       = DateTime.Now,
                PlaceholderPath = placeholderPath,
                BackendTaskId   = backendTaskId
            };
            Store.RegisterTask(taskId, task);
            return taskId;
        }

        public static void MarkTaskCompleted(string taskId, string videoPath, string materialPath, string previewUrl)
        {
            var task = Store.GetTask(taskId);
            if (task == null) return;
            Store.ApplyTaskUpdate(task, t =>
            {
                t.Status       = "completed";
                t.Progress     = 100;
                t.VideoPath    = videoPath;
                t.MaterialPath = materialPath;
                t.PreviewUrl   = previewUrl;
                t.EndTime      = DateTime.Now;
            });
        }

        public static void MarkTaskFailed(string taskId, string errorMessage)
        {
            var task = Store.GetTask(taskId);
            if (task == null) return;
            Store.ApplyTaskUpdate(task, t =>
            {
                t.Status       = "failed";
                t.ErrorMessage = errorMessage;
                t.EndTime      = DateTime.Now;
            });
        }

        public static EffectVideoTaskInfo GetTask(string taskId) => Store.GetTask(taskId);

        public static List<EffectVideoTaskInfo> GetAllTasks() => Store.GetAllTasks();

        public static EffectVideoTaskInfo GetTaskByBackendId(string backendTaskId) =>
            Store.GetTaskByBackendId(backendTaskId);

        public static EffectVideoTaskInfo CreateRecoveredTask(
            string backendTaskId, string prompt, string placeholderPath, long timestampMs)
        {
            return Store.CreateRecoveredTask(backendTaskId, () => new EffectVideoTaskInfo
            {
                TaskId          = $"recovered_{backendTaskId}",
                BackendTaskId   = backendTaskId,
                Prompt          = prompt ?? "",
                PlaceholderPath = placeholderPath ?? "",
                Status          = "recovering",
                Progress        = 0,
                StartTime       = timestampMs > 0
                                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).LocalDateTime
                                    : DateTime.Now
            });
        }

        public static void RemoveTask(string taskId) => Store.RemoveTask(taskId);

        public static void CleanupCompletedTasks() => Store.CleanupCompletedTasks();
#endif
    }

    /// <summary>
    /// CustomTool for generating effect videos with automatic green-screen keying.
    /// Backend: gpt-image-2 (auto green-screen prompt) → seedance2 (green-screen video).
    /// Unity: creates ChromaKey material → VideoPlayer + RenderTexture plays transparent video at runtime.
    /// No frame extraction — the shader keys in real-time during playback.
    /// </summary>
    public static class GenerateEffectVideoTool
    {
        [ExecuteCustomTool.CustomTool("generate_effect_video",
            "Generate an effect video with automatic green-screen keying. " +
            "Backend: 1) generates art with auto green-screen background (prompt doesn't need 'green screen'), " +
            "2) generates green-screen effect video. " +
            "Unity: 3) creates ChromaKey material — use with VideoPlayer + RenderTexture for real-time transparent playback. " +
            "Key parameters: prompt (VFX description, e.g. 'fire explosion', 'magic glow'), " +
            "videoDuration (optional: 4-15 seconds, default 5), " +
            "videoRatio (optional: '16:9', '9:16', '1:1', '4:3', default '16:9'), " +
            "videoResolution (optional: '720p' or '480p', default '720p'), " +
            "output_path (optional save path). " +
            "IMPORTANT: Full pipeline takes 90-180 seconds. " +
            "Wait at least 10 seconds before the first query_effect_video_status call. " +
            "A placeholder_path is returned immediately — you can reference it right away.")]
        public static object GenerateEffectVideo(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateEffectVideoTool] Generating effect video with parameters: {parameters}");

                string generatorId = "effect_video_wf";
                string prompt      = parameters["prompt"]?.ToString();
                string outputPath  = parameters["output_path"]?.ToString();
                string sessionId   = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());

                if (string.IsNullOrEmpty(prompt))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "'prompt' parameter is required" }
                    };
                }

                var config = ConfigManager.GetGeneratorConfig(ConfigType.Video, generatorId);
                if (config == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Cannot find video generator config for '{generatorId}'." }
                    };
                }

                var generator = new DynamicGenerator(config);
                generator.SetTextPrompt(prompt);
                GenerateVideoTool.ApplyVideoParametersInternal(generator, generatorId, parameters);

                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[GenerateEffectVideoTool] Submit failed [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[GenerateEffectVideoTool] Submit success, backend_task_id={submitResult.BackendTaskId}");

                string placeholderPath = GenerateVideoTool.CreatePlaceholderVideo(outputPath, "EffectVideo");
                string capturedBackendTaskId = submitResult.BackendTaskId;
                string taskId = EffectVideoTaskTracker.CreateTask(prompt, placeholderPath, capturedBackendTaskId);

                var host = new EffectVideoPipelineHost(
                    placeholderPath,
                    sessionId,
                    (savedPath, previewUrl) =>
                        GenerateEffectVideoTool.CompleteEffectVideoTask(
                            taskId, capturedBackendTaskId, savedPath, previewUrl, sessionId, prompt),
                    errorMsg =>
                    {
                        EffectVideoTaskTracker.MarkTaskFailed(taskId, errorMsg);
                        GenerationNotifier.NotifyFailed("generate_effect_video", taskId, capturedBackendTaskId, errorMsg,
                            new JObject { ["session_id"] = sessionId, ["generator_id"] = generatorId, ["prompt"] = prompt ?? "" });
                    }
                );

                var pipeline = new GenerationPipeline(host, ConfigType.Video, GenerationRequestOrigin.Agent, sessionId, "generate_effect_video");
                string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(placeholderPath);
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId));

                TJLog.Log($"[GenerateEffectVideoTool] Pipeline started, task_id={taskId}, backend_task_id={submitResult.BackendTaskId}, placeholder: {placeholderPath}");

                return new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "message",
                        "Effect video generation started (image → video → ChromaKey material). " +
                        "END THIS RESPONSE TURN immediately. A <bg_task_done> notification will arrive (~2-3 min) " +
                        "containing video_path and material_path. " +
                        "POLLING IS STRICTLY FORBIDDEN — only call query_effect_video_status ONCE after 300s if no notification arrives." },
                    { "task_id",            taskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       generatorId },
                    { "prompt",             prompt ?? "" },
                    { "placeholder_path",   placeholderPath },
                    { "preview_url",        PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "estimated_wait_seconds", 180 },
                    { "notification_mode",  "bg_task_done" }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateEffectVideoTool] Error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error generating effect video: {e.Message}" }
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

        [ExecuteCustomTool.CustomTool("query_effect_video_status",
            "Query the status of an effect video generation task. Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "When completed, returns 'video_path' (green-screen MP4) and 'material_path' (ChromaKey material). " +
            "Status values: 'generating', 'recovering', 'completed', 'failed', 'interrupted'. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QueryEffectVideoStatus(JObject parameters)
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

                var task = EffectVideoTaskTracker.GetTask(taskId);
                if (task == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Task '{taskId}' not found." }
                    };
                }

                var result = new Dictionary<string, object>
                {
                    { "success",       true },
                    { "task_id",       task.TaskId },
                    { "generator_id",  "effect_video_wf" },
                    { "status",        task.Status },
                    { "progress",      task.Progress },
                    { "prompt",        task.Prompt },
                    { "start_time",    task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                };

                if (!string.IsNullOrEmpty(task.VideoPath))     result["video_path"]     = task.VideoPath;
                if (!string.IsNullOrEmpty(task.MaterialPath))  result["material_path"]  = task.MaterialPath;
                result["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);
                if (!string.IsNullOrEmpty(task.ErrorMessage))  result["error"]          = task.ErrorMessage;

                if (task.EndTime.HasValue)
                {
                    result["end_time"]         = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                    result["duration_seconds"]  = (int)(task.EndTime.Value - task.StartTime).TotalSeconds;
                }

                if ((task.Status == "generating" || task.Status == "recovering") && !string.IsNullOrEmpty(task.PlaceholderPath))
                    result["placeholder_path"] = task.PlaceholderPath;

                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[QueryEffectVideoStatus] Query error: {e}");
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

        [ExecuteCustomTool.CustomTool("list_effect_video_tasks", "List all active and recent effect video generation tasks")]
        public static object ListEffectVideoTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks    = EffectVideoTaskTracker.GetAllTasks();
                var taskList = new List<Dictionary<string, object>>();

                foreach (var task in tasks)
                {
                    var taskData = new Dictionary<string, object>
                    {
                        { "task_id",      task.TaskId },
                        { "generator_id", "effect_video_wf" },
                        { "status",       task.Status },
                        { "progress",     task.Progress },
                        { "prompt",       task.Prompt },
                        { "start_time",   task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                    };

                    if (!string.IsNullOrEmpty(task.VideoPath))     taskData["video_path"]    = task.VideoPath;
                    if (!string.IsNullOrEmpty(task.MaterialPath))  taskData["material_path"] = task.MaterialPath;
                    taskData["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);
                    if (!string.IsNullOrEmpty(task.ErrorMessage))  taskData["error"]         = task.ErrorMessage;
                    if (task.EndTime.HasValue) taskData["end_time"] = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");

                    taskList.Add(taskData);
                }

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "count",   taskList.Count },
                    { "tasks",   taskList }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[ListEffectVideoTasks] List error: {e}");
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

        internal static void CompleteEffectVideoTask(
            string taskId, string backendTaskId, string savedPath, string previewUrl, string sessionId, string prompt)
        {
            TJLog.Log($"[GenerateEffectVideoTool] Video downloaded, creating ChromaKey material: {savedPath}");

            var postResult = GreenScreenVideoPostProcess.EnsureChromaKeyMaterial(savedPath);

            string materialPath = "";
            if (postResult.Success)
            {
                materialPath = postResult.MaterialPath;
                GreenScreenVideoPostProcess.SetupEffectVideoInScene(savedPath, materialPath);
            }
            else
            {
                TJLog.LogError($"[GenerateEffectVideoTool] ChromaKey material creation failed: {postResult.Error}");
            }

            EffectVideoTaskTracker.MarkTaskCompleted(taskId, savedPath, materialPath, previewUrl);

            var t = EffectVideoTaskTracker.GetTask(taskId);
            var notifyPayload = new JObject
            {
                ["session_id"]       = sessionId,
                ["generator_id"]     = "effect_video_wf",
                ["prompt"]           = prompt ?? t?.Prompt ?? "",
                ["video_path"]       = savedPath ?? "",
                ["material_path"]    = materialPath ?? "",
                ["preview_url"]      = previewUrl ?? "",
                ["progress"]         = 100,
                ["start_time"]       = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                ["end_time"]         = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
            };
            if (!postResult.Success)
                notifyPayload["warning"] = $"ChromaKey material creation failed: {postResult.Error}";

            GenerationNotifier.NotifyCompleted("generate_effect_video", taskId, backendTaskId, notifyPayload);
        }

        /// <summary>
        /// Restore prompt after domain reload. Session tracker takes precedence.
        /// </summary>
        internal static void ApplyEffectVideoRecoveryGeneratorSettings(
            DynamicGenerator generator, InterruptedTaskData interrupted, EffectVideoTaskTracker.EffectVideoTaskInfo trackerTask = null)
        {
            if (generator == null || interrupted == null) return;

            string prompt = !string.IsNullOrEmpty(trackerTask?.Prompt) ? trackerTask.Prompt : interrupted.prompt;
            if (!string.IsNullOrEmpty(prompt))
                generator.SetTextPrompt(prompt);
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Automatically resumes interrupted generate_effect_video tasks after domain reload.
    /// </summary>
    [InitializeOnLoad]
    public static class EffectVideoDomainReloadRecovery
    {
        static EffectVideoDomainReloadRecovery()
        {
            CustomToolDomainReloadRecovery.Schedule(ResumeInterruptedTasks);
        }

        private static void ResumeInterruptedTasks()
        {
            CustomToolDomainReloadRecovery.Resume(
                "GenerateEffectVideoTool",
                ConfigType.Video,
                t => t.toolName == "generate_effect_video",
                () => EffectVideoTaskTracker.GetAllTasks(),
                (interrupted, _, generator) =>
                {
                    var trackerTask = EffectVideoTaskTracker.GetTaskByBackendId(interrupted.backendTaskId);
                    if (trackerTask != null)
                    {
                        CustomToolDomainReloadRecovery.MarkTrackerRecoveringIfNeeded(trackerTask.Status, () =>
                        {
                            EffectVideoTaskTracker.ApplyTaskUpdate(trackerTask, t => t.Status = "recovering");
                        });
                    }
                    else
                    {
                        string placeholderPath = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);
                        trackerTask = EffectVideoTaskTracker.CreateRecoveredTask(
                            interrupted.backendTaskId, interrupted.prompt, placeholderPath, interrupted.timestamp);
                    }

                    string placeholderPathForHost = trackerTask.PlaceholderPath ?? "";
                    if (string.IsNullOrEmpty(placeholderPathForHost))
                        placeholderPathForHost = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);

                    string sessionId = interrupted.sessionId ?? "";
                    string capturedBackendTaskId = interrupted.backendTaskId;
                    string taskId = trackerTask.TaskId;
                    string prompt = trackerTask.Prompt ?? interrupted.prompt ?? "";

                    GenerateEffectVideoTool.ApplyEffectVideoRecoveryGeneratorSettings(generator, interrupted, trackerTask);

                    var host = new EffectVideoPipelineHost(
                        placeholderPathForHost,
                        sessionId,
                        (savedPath, previewUrl) =>
                            GenerateEffectVideoTool.CompleteEffectVideoTask(
                                taskId, capturedBackendTaskId, savedPath, previewUrl, sessionId, prompt),
                        errorMsg =>
                        {
                            EffectVideoTaskTracker.MarkTaskFailed(taskId, errorMsg);
                            GenerationNotifier.NotifyFailed("generate_effect_video", taskId, capturedBackendTaskId, errorMsg,
                                new JObject
                                {
                                    ["session_id"]   = sessionId,
                                    ["generator_id"] = "effect_video_wf",
                                    ["prompt"]       = prompt
                                });
                        });

                    CustomToolDomainReloadRecovery.StartPolling(
                        "GenerateEffectVideoTool", host, ConfigType.Video,
                        sessionId, "generate_effect_video", generator, interrupted.backendTaskId);
                });
        }
    }

    /// <summary>
    /// Pipeline host for effect video generation.
    /// After video download, creates a ChromaKey material for real-time green-screen keying.
    /// </summary>
    internal class EffectVideoPipelineHost : HeadlessPipelineHostBase, IMediaAssetPipelineHost
    {
        private readonly string _placeholderPath;
        private readonly TJGeneratorsAssetReference _placeholderRef;
        private readonly string _sessionId;
        private readonly Action<string, string> _onVideoDownloaded;
        private readonly Action<string> _onFailed;

        public EffectVideoPipelineHost(
            string placeholderPath,
            string sessionId,
            Action<string, string> onVideoDownloaded,
            Action<string> onFailed)
        {
            _placeholderPath   = placeholderPath;
            _placeholderRef    = TJGeneratorsAssetReference.FromPath(placeholderPath);
            _sessionId         = sessionId ?? "";
            _onVideoDownloaded = onVideoDownloaded;
            _onFailed          = onFailed;
        }

        protected override string DialogLogTag => "GenerateEffectVideoTool";
        protected override Action<string> DialogFailedCallback => errorMessage => _onFailed?.Invoke(errorMessage);

        public override TJGeneratorsAssetReference GetTargetAsset() => _placeholderRef;

        public void StartEditorCoroutine(IEnumerator coroutine)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(coroutine);
        }

        public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator) =>
            _type == PipelineMediaType.Video ? _placeholderPath : null;

        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Video) return;

            TJLog.Log($"[GenerateEffectVideoTool] Video saved: {savePath}");

            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));
            TJGeneratorsGenerationLabel.EnableSessionLabel(TJGeneratorsAssetReference.FromPath(savePath), _sessionId);

            string previewUrl = generator.CurrentPreviewUrl;
            _onVideoDownloaded?.Invoke(savePath, previewUrl);
        }
    }
#endif
}
