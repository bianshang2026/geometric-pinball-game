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
    /// Tracks active audio clip generation tasks
    /// </summary>
    public static class AudioClipTaskTracker
    {
#if UNITY_EDITOR
        [Serializable]
        private class PersistedTask
        {
            public string taskId;
            public string generatorId;
            public string prompt;
            public string status;
            public int    progress;
            public string audioPath;
            public string errorMessage;
            public long   startTimeTicks;
            public long   endTimeTicks;
            public string previewUrl;
            public string placeholderPath;
            public string backendTaskId;
        }

        public class AudioClipTaskInfo : IGenerationTaskInfo
        {
            public string TaskId { get; set; }
            public string GeneratorId { get; set; }
            public string Prompt { get; set; }
            public string Status { get; set; }
            public int Progress { get; set; }
            public string AudioPath { get; set; }
            public string ErrorMessage { get; set; }
            public string PreviewUrl { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public string PlaceholderPath { get; set; }
            public string BackendTaskId { get; set; }
        }

        private static readonly GenerationTaskTrackerStore<AudioClipTaskInfo, PersistedTask> Store =
            new GenerationTaskTrackerStore<AudioClipTaskInfo, PersistedTask>(
                "TJGen_Audio", BuildPersisted, FromPersisted);

        private static PersistedTask BuildPersisted(AudioClipTaskInfo info) => new PersistedTask
        {
            taskId          = info.TaskId,
            generatorId     = info.GeneratorId,
            prompt          = info.Prompt ?? "",
            status          = info.Status,
            progress        = info.Progress,
            audioPath       = info.AudioPath ?? "",
            errorMessage    = info.ErrorMessage ?? "",
            startTimeTicks  = info.StartTime.Ticks,
            endTimeTicks    = info.EndTime?.Ticks ?? 0,
            previewUrl      = info.PreviewUrl ?? "",
            placeholderPath = info.PlaceholderPath ?? "",
            backendTaskId   = info.BackendTaskId ?? ""
        };

        private static AudioClipTaskInfo FromPersisted(PersistedTask p) => new AudioClipTaskInfo
        {
            TaskId          = p.taskId,
            GeneratorId     = p.generatorId,
            Prompt          = p.prompt,
            Status          = p.status,
            Progress        = p.progress,
            AudioPath       = p.audioPath,
            ErrorMessage    = p.errorMessage,
            PreviewUrl      = p.previewUrl,
            StartTime       = new DateTime(p.startTimeTicks),
            EndTime         = p.endTimeTicks > 0 ? (DateTime?)new DateTime(p.endTimeTicks) : null,
            PlaceholderPath = p.placeholderPath,
            BackendTaskId   = p.backendTaskId
        };

        internal static void ApplyTaskUpdate(AudioClipTaskInfo task, Action<AudioClipTaskInfo> mutate) =>
            Store.ApplyTaskUpdate(task, mutate);

        public static string CreateTask(string generatorId, string prompt, string placeholderPath = null, string backendTaskId = null)
        {
            string taskId = Store.AllocateTaskId("audio");
            var task = new AudioClipTaskInfo
            {
                TaskId          = taskId,
                GeneratorId     = generatorId,
                Prompt          = prompt ?? "",
                Status          = "generating",
                StartTime       = DateTime.Now,
                PlaceholderPath = placeholderPath,
                BackendTaskId   = backendTaskId
            };
            Store.RegisterTask(taskId, task);
            return taskId;
        }

        public static void MarkCompleted(string taskId, string audioPath, string previewUrl = null)
        {
            var task = Store.GetTask(taskId);
            if (task == null) return;
            Store.ApplyTaskUpdate(task, t =>
            {
                t.Status    = "completed";
                t.Progress  = 100;
                t.AudioPath = audioPath;
                t.PreviewUrl = previewUrl;
                t.EndTime   = DateTime.Now;
            });
        }

        public static void MarkFailed(string taskId, string errorMessage)
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

        public static AudioClipTaskInfo GetTask(string taskId) => Store.GetTask(taskId);

        public static List<AudioClipTaskInfo> GetAllTasks() => Store.GetAllTasks();

        public static AudioClipTaskInfo GetTaskByBackendId(string backendTaskId) =>
            Store.GetTaskByBackendId(backendTaskId);

        public static AudioClipTaskInfo CreateRecoveredTask(
            string backendTaskId, string prompt, string placeholderPath, long timestampMs, string generatorId = null)
        {
            return Store.CreateRecoveredTask(backendTaskId, () => new AudioClipTaskInfo
            {
                TaskId          = $"recovered_{backendTaskId}",
                BackendTaskId   = backendTaskId,
                GeneratorId     = generatorId ?? "",
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
    /// CustomTool for generating audio clips (background music / SFX) using TJGenerators Music pipeline.
    /// Supports text-to-audio generation via Sonilo.
    /// Output is an AudioClip asset (WAV) saved to Assets/TJGenerators/History/.
    /// Domain-reload recovery for generate_sound_effect and generate_tts is also implemented in
    /// AudioDomainReloadRecovery below (same file); those tools share AudioClipTaskTracker and AudioPipelineHost.
    /// </summary>
    public static class GenerateAudioClipTool
    {
        [ExecuteCustomTool.CustomTool("generate_audio_clip",
            "Generate a background music (BGM) or ambient audio clip from a text prompt using AI. " +
            "This tool is for looping music tracks and ambient soundscapes ONLY — NOT for sound effects (SFX). " +
            "The output is a WAV AudioClip asset saved to Assets/TJGenerators/History/. " +
            "Parameters: generator_id (optional, default 'sonilo-music'), prompt (text description of music style/mood/scene), " +
            "output_path (optional asset save path), duration_seconds (optional float, seconds, 1-180, default 90), " +
            "output_format (optional, 'wav'|'mp3', default 'wav'). " +
            "IMPORTANT: Generation takes 1-3 minutes. After calling this tool, wait at least 5 seconds " +
            "before the first query_audio_clip_status call, then poll every 10-15 seconds. " +
            "A placeholder_path (WAV) is returned immediately — you can assign it to an AudioSource right away.")]
        public static object GenerateAudioClip(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateAudioClipTool] Generating audio clip with parameters: {parameters}");

                string generatorId = parameters["generator_id"]?.ToString() ?? "sonilo-music";
                string prompt = parameters["prompt"]?.ToString();
                string outputPath = parameters["output_path"]?.ToString();
                string sessionId = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());
                bool playOnAwake = parameters["play_on_awake"] != null ? parameters["play_on_awake"].ToObject<bool>() : true;

                if (string.IsNullOrEmpty(prompt))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "'prompt' parameter is required" }
                    };
                }

                int maxLen = TJGeneratorsPromptLimits.GetMaxLength("sonilo-music");
                if (maxLen > 0 && prompt.Length > maxLen)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "INVALID_PARAMS" },
                        { "message", $"Prompt length ({prompt.Length}) exceeds the {maxLen} character limit. Please shorten your music description." }
                    };
                }

                // Load music generator config
                var config = ConfigManager.GetGeneratorConfig(ConfigType.Music, generatorId);
                if (config == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Cannot find music generator config for '{generatorId}'. Valid value: 'sonilo-music'." }
                    };
                }

                // Create generator and set inputs
                var generator = new DynamicGenerator(config);
                generator.SetTextPrompt(prompt);

                // Apply optional parameters
                ApplyAudioParameters(generator, parameters);

                // 阶段1：同步提交任务到后端，立即获取 backendTaskId 或失败原因
                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[GenerateAudioClipTool] 任务提交失败 [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[GenerateAudioClipTool] 任务提交成功，backend_task_id={submitResult.BackendTaskId}");

                // 提交成功后才创建 placeholder（避免在鉴权失败时留下无用文件）
                var (placeholderPath, audioPath) = BuildAudioPaths(outputPath, generator);

                // Create tracked task
                string capturedBackendTaskId = submitResult.BackendTaskId;
                string taskId = AudioClipTaskTracker.CreateTask(generatorId, prompt, placeholderPath, capturedBackendTaskId);

                // Create pipeline host with audio-specific callbacks
                var host = new AudioPipelineHost(placeholderPath, audioPath, sessionId, isBgm: true, playOnAwake: playOnAwake,
                    (savedPath, previewUrl) =>
                    {
                        AudioClipTaskTracker.MarkCompleted(taskId, savedPath, previewUrl);
                        var t = AudioClipTaskTracker.GetTask(taskId);
                        GenerationNotifier.NotifyCompleted("generate_audio_clip", taskId, capturedBackendTaskId,
                            new JObject
                            {
                                ["session_id"]       = sessionId,
                                ["generator_id"]     = generatorId,
                                ["prompt"]           = prompt ?? "",
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
                        GenerationNotifier.NotifyFailed("generate_audio_clip", taskId, capturedBackendTaskId, errorMsg,
                            new JObject { ["session_id"] = sessionId, ["generator_id"] = generatorId, ["prompt"] = prompt ?? "" });
                    });

                // 阶段2：异步轮询（跳过提交）
                var pipeline = new GenerationPipeline(host, ConfigType.Music, GenerationRequestOrigin.Agent, sessionId, "generate_audio_clip");
                string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(placeholderPath);
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId));

                TJLog.Log($"[GenerateAudioClipTool] 轮询已启动，task_id={taskId}, backend_task_id={submitResult.BackendTaskId}, path: {audioPath}");

                return new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "message",
                        "Audio clip generation started. " +
                        "STEP 1 (do now): Note the placeholder_path — a silent WAV is available immediately. " +
                        "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                        "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn (~90s) " +
                        "containing ALL generation results (audio_path, preview_url, timing, etc.). " +
                        "*** POLLING IS STRICTLY FORBIDDEN — do NOT call query_audio_clip_status repeatedly. " +
                        "Only call query_audio_clip_status ONCE as a one-time fallback if no notification arrives. ***" },
                    { "task_id",            taskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       generatorId },
                    { "prompt",             prompt },
                    { "placeholder_path",   placeholderPath },
                    { "preview_url",        PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "estimated_wait_seconds", 90 },
                    { "notification_mode",  "bg_task_done" }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateAudioClipTool] Error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error generating audio clip: {e.Message}" }
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

        [ExecuteCustomTool.CustomTool("query_audio_clip_status",
            "Query the status of an audio clip generation task. Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "When completed, returns 'audio_path' with the AudioClip asset path in the project. " +
            "Status values: 'generating', 'completed', 'failed'. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QueryAudioClipStatus(JObject parameters)
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
                TJLog.LogError($"[GenerateAudioClipTool] Query error: {e}");
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

        [ExecuteCustomTool.CustomTool("list_audio_clip_tasks", "List all active and recent audio clip generation tasks")]
        public static object ListAudioClipTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks = AudioClipTaskTracker.GetAllTasks();
                var taskList = new List<Dictionary<string, object>>();

                foreach (var task in tasks)
                {
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
                TJLog.LogError($"[GenerateAudioClipTool] List error: {e}");
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
        private static (string placeholderPath, string audioPath) BuildAudioPaths(string outputPath, DynamicGenerator generator)
        {
            string ext =
                "."
                + TJGenerators.Utils.TJGeneratorsAudioAssetPathUtility.NormalizeImportedAudioFileExtension(
                    generator?.AudioFormat ?? "wav"
                );
            string audioPath;
            if (!string.IsNullOrEmpty(outputPath))
            {
                string dir = Path.GetDirectoryName(outputPath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir))
                    EnsureAssetDatabaseFolder(dir);
                string stem = Path.GetFileNameWithoutExtension(outputPath);
                if (string.IsNullOrEmpty(stem))
                    stem = "New AudioClip";
                audioPath = TJGenerators.Utils.TJGeneratorsAudioAssetPathUtility.GenerateUniqueAudioPath(
                    $"{dir}/{stem}{ext}");
            }
            else
            {
                if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                    AssetDatabase.CreateFolder("Assets", "TJGenerators");
                if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                    AssetDatabase.CreateFolder("Assets/TJGenerators", "History");
                string uniqueName = "Music_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                audioPath = TJGenerators.Utils.TJGeneratorsAudioAssetPathUtility.GenerateUniqueAudioPath(
                    "Assets/TJGenerators/History/" + uniqueName + ext);
            }

            // CreateBlankAudioClip always materializes a .wav sibling; keep downloadPath as configured ext.
            string downloadPath = audioPath;
            string placeholderPath = TJGeneratorsAudioUtils.CreateBlankAudioClip(audioPath);
            return (placeholderPath, downloadPath);
        }

        private static void EnsureAssetDatabaseFolder(string folderPath)
        {
            // Recursively create each missing folder segment via AssetDatabase so Unity tracks them
            folderPath = folderPath.Replace('\\', '/').TrimEnd('/');
            string[] parts = folderPath.Split('/');
            string current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static void ApplyAudioParameters(DynamicGenerator generator, JObject parameters)
        {
            if (parameters["duration_seconds"] != null)
                generator.SetParameter("durationSeconds", parameters["duration_seconds"].ToObject<float>());

            // Always set outputFormat — if omitted, default to "wav" so fal.ai doesn't
            // fall back to aac (which Unity can't import without ffmpeg).
            string fmt = parameters["output_format"]?.ToString();
            if (string.IsNullOrWhiteSpace(fmt) || (fmt != "wav" && fmt != "mp3"))
                fmt = "wav";
            generator.SetParameter("outputFormat", fmt);
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>
    /// Automatically resumes interrupted generate_audio_clip / generate_sound_effect / generate_tts
    /// tasks after domain reload. Sound-effect and TTS tools delegate recovery here because they
    /// reuse AudioClipTaskTracker and AudioPipelineHost from this file.
    /// </summary>
    [InitializeOnLoad]
    public static class AudioDomainReloadRecovery
    {
        private static readonly HashSet<string> ManagedToolNames = new HashSet<string>
        {
            "generate_audio_clip",
            "generate_sound_effect",
            "generate_tts"
        };

        static AudioDomainReloadRecovery()
        {
            CustomToolDomainReloadRecovery.Schedule(ResumeInterruptedTasks);
        }

        private static void ResumeInterruptedTasks()
        {
            CustomToolDomainReloadRecovery.Resume(
                "GenerateAudioClipTool",
                ConfigType.Music,
                t => !string.IsNullOrEmpty(t.toolName) && ManagedToolNames.Contains(t.toolName),
                () => AudioClipTaskTracker.GetAllTasks(),
                (interrupted, _, generator) =>
                {
                    string toolName = interrupted.toolName;

                    var trackerTask = AudioClipTaskTracker.GetTaskByBackendId(interrupted.backendTaskId);
                    if (trackerTask != null)
                    {
                        CustomToolDomainReloadRecovery.MarkTrackerRecoveringIfNeeded(trackerTask.Status, () =>
                        {
                            AudioClipTaskTracker.ApplyTaskUpdate(trackerTask, t => t.Status = "recovering");
                        });
                    }
                    else
                    {
                        string placeholderPath = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);
                        trackerTask = AudioClipTaskTracker.CreateRecoveredTask(
                            interrupted.backendTaskId,
                            interrupted.prompt,
                            placeholderPath,
                            interrupted.timestamp,
                            interrupted.modelVersion);
                    }

                    string placeholderPathForHost = trackerTask.PlaceholderPath ?? "";
                    if (string.IsNullOrEmpty(placeholderPathForHost))
                        placeholderPathForHost = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);

                    string ext = "." + TJGenerators.Utils.TJGeneratorsAudioAssetPathUtility.NormalizeImportedAudioFileExtension(
                        generator.AudioFormat ?? "wav");
                    string audioDownloadPath = placeholderPathForHost;
                    if (!string.IsNullOrEmpty(placeholderPathForHost))
                    {
                        string placeholderExt = Path.GetExtension(placeholderPathForHost);
                        if (!string.Equals(placeholderExt, ext, StringComparison.OrdinalIgnoreCase))
                            audioDownloadPath = Path.ChangeExtension(placeholderPathForHost, ext);
                    }

                    bool isBgm = toolName == "generate_audio_clip";
                    string sessionId = interrupted.sessionId ?? "";
                    string capturedBackendTaskId = interrupted.backendTaskId;
                    string taskId = trackerTask.TaskId;

                    var host = new AudioPipelineHost(
                        placeholderPathForHost,
                        audioDownloadPath,
                        sessionId,
                        isBgm: isBgm,
                        playOnAwake: false,
                        (savedPath, previewUrl) =>
                        {
                            AudioClipTaskTracker.MarkCompleted(taskId, savedPath, previewUrl);
                            var t = AudioClipTaskTracker.GetTask(taskId);
                            GenerationNotifier.NotifyCompleted(toolName, taskId, capturedBackendTaskId,
                                new JObject
                                {
                                    ["session_id"] = sessionId,
                                    ["generator_id"] = t?.GeneratorId ?? interrupted.modelVersion ?? "",
                                    ["prompt"] = t?.Prompt ?? interrupted.prompt ?? "",
                                    ["audio_path"] = savedPath ?? "",
                                    ["preview_url"] = previewUrl ?? "",
                                    ["progress"] = 100,
                                    ["start_time"] = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                    ["end_time"] = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                    ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
                                });
                        },
                        errorMsg =>
                        {
                            AudioClipTaskTracker.MarkFailed(taskId, errorMsg);
                            GenerationNotifier.NotifyFailed(toolName, taskId, capturedBackendTaskId, errorMsg,
                                new JObject
                                {
                                    ["session_id"] = sessionId,
                                    ["generator_id"] = trackerTask.GeneratorId ?? interrupted.modelVersion ?? "",
                                    ["prompt"] = trackerTask.Prompt ?? interrupted.prompt ?? ""
                                });
                        });

                    CustomToolDomainReloadRecovery.StartPolling(
                        "GenerateAudioClipTool", host, ConfigType.Music,
                        sessionId, toolName, generator, interrupted.backendTaskId);
                });
        }
    }

    /// <summary>
    /// IGenerationPipelineHost implementation for headless audio clip generation via custom tools.
    /// Handles audio saving and task lifecycle callbacks.
    /// </summary>
    internal class AudioPipelineHost : HeadlessPipelineHostBase, IMediaAssetPipelineHost
    {
        private readonly string _placeholderPath;   // WAV placeholder — available immediately
        private readonly string _audioDownloadPath; // MP3 download target path
        private readonly string _sessionId;
        private readonly bool _isBgm;        // controls BGMPlayer auto-creation
        private readonly bool _playOnAwake;  // maps to AudioSource.playOnAwake
        private readonly Action<string, string> _onCompleted;
        private readonly Action<string> _onFailed;

        public AudioPipelineHost(string placeholderPath, string audioDownloadPath, string sessionId, bool isBgm, bool playOnAwake, Action<string, string> onCompleted, Action<string> onFailed)
        {
            _placeholderPath = placeholderPath;
            _audioDownloadPath = audioDownloadPath;
            _sessionId = sessionId ?? "";
            _isBgm = isBgm;
            _playOnAwake = playOnAwake;
            _onCompleted = onCompleted;
            _onFailed = onFailed;
        }

        protected override string DialogLogTag => "GenerateAudioClipTool";
        protected override Action<string> DialogFailedCallback => errorMessage => _onFailed?.Invoke(errorMessage);

        public override TJGeneratorsAssetReference GetTargetAsset() => null;

        public void StartEditorCoroutine(IEnumerator coroutine)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(coroutine);
        }

        public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator) =>
            _type == PipelineMediaType.Audio ? _audioDownloadPath : null;

        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Audio) return;

            TJLog.Log($"[GenerateAudioClipTool] Audio clip saved: {savePath}");

            var oldClip = TJGeneratorsAudioUtils.TryLoadAudioClip(_placeholderPath);
            var newClip = TJGeneratorsAudioUtils.TryLoadAudioClip(savePath);
            if (newClip == null)
            {
                TJLog.LogError(
                    $"[GenerateAudioClipTool] 无法将 {savePath} 导入为 AudioClip。"
                    + " 后端返回 AAC/MP4 时需安装 ffmpeg 并加入 PATH 以自动转 WAV。"
                );
            }

            // When the placeholder and download path are the same (in-place overwrite), match
            // AudioSources by asset path as well as by object reference, because Unity may
            // return a new managed object for the reimported asset while the AudioSource still
            // holds a reference to the pre-reimport object.
            bool pathsAreSame = string.Equals(_placeholderPath, savePath, StringComparison.OrdinalIgnoreCase);
            bool foundExistingSource = false;

            if (newClip != null)
            {
                foreach (var source in UnityObjectCompat.FindObjectsOfType<AudioSource>())
                {
                    if (source.clip == null) continue;
                    bool matchByRef = oldClip != null && source.clip == oldClip;
                    bool matchByPath = pathsAreSame && string.Equals(
                        AssetDatabase.GetAssetPath(source.clip), _placeholderPath, StringComparison.OrdinalIgnoreCase);

                    if (matchByRef || matchByPath)
                    {
                        source.clip = newClip;
                        source.playOnAwake = _playOnAwake;
                        EditorUtility.SetDirty(source);
                        TJLog.Log($"[GenerateAudioClipTool] Updated AudioSource on '{source.gameObject.name}'.");
                        foundExistingSource = true;
                    }
                }
                if (foundExistingSource)
                    UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            }

            // When generating BGM and no AudioSource in the scene already references this clip,
            // auto-create a BGMPlayer so the music plays when entering Play Mode.
            if (_isBgm && !foundExistingSource && newClip != null)
            {
                var go = GameObject.Find("BGMPlayer");
                bool isNew = go == null;
                if (isNew)
                {
                    go = new GameObject("BGMPlayer");
                    Undo.RegisterCreatedObjectUndo(go, "生成 BGM AudioSource");
                }

                var bgmSource = go.GetComponent<AudioSource>();
                if (bgmSource == null)
                    bgmSource = Undo.AddComponent<AudioSource>(go);

                Undo.RecordObject(bgmSource, "生成 BGM Clip");
                bgmSource.clip = newClip;
                bgmSource.loop = true;
                bgmSource.spatialBlend = 0f;
                bgmSource.playOnAwake = _playOnAwake;
                EditorUtility.SetDirty(bgmSource);
                EditorUtility.SetDirty(go);
                UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
                TJLog.Log($"[GenerateAudioClipTool] Auto-created BGMPlayer in active scene: {savePath}");
            }

            // Delete the WAV placeholder only if the real audio was saved to a different path.
            bool savedToSamePath = string.Equals(_placeholderPath, savePath, StringComparison.OrdinalIgnoreCase);
            if (!savedToSamePath &&
                !string.IsNullOrEmpty(_placeholderPath) &&
                AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(_placeholderPath) != null)
            {
                AssetDatabase.DeleteAsset(_placeholderPath);
            }

            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));
            TJGeneratorsGenerationLabel.EnableSessionLabel(TJGeneratorsAssetReference.FromPath(savePath), _sessionId);
            _onCompleted?.Invoke(savePath, generator.CurrentPreviewUrl);
        }
    }
#endif
}
