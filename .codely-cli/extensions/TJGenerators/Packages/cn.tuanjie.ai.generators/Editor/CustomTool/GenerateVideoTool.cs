using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Codely.Newtonsoft.Json;
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
    /// Tracks active video generation tasks.
    /// </summary>
    public static class VideoTaskTracker
    {
#if UNITY_EDITOR
        [Serializable]
        private class PersistedTask
        {
            public string taskId;
            public string generatorId;
            public string prompt;
            public string imagePath;
            public string status;
            public int    progress;
            public string videoPath;
            public string errorMessage;
            public long   startTimeTicks;
            public long   endTimeTicks;
            public string previewUrl;
            public string lastFrameUrl;
            public string placeholderPath;
            public string backendTaskId;
        }

        public class VideoTaskInfo : IGenerationTaskInfo
        {
            public string TaskId { get; set; }
            public string GeneratorId { get; set; }
            public string Prompt { get; set; }
            public string ImagePath { get; set; }
            public string Status { get; set; }
            public int Progress { get; set; }
            public string VideoPath { get; set; }
            public string ErrorMessage { get; set; }
            public string PreviewUrl { get; set; }
            public string LastFrameUrl { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public string PlaceholderPath { get; set; }
            public string BackendTaskId { get; set; }
        }

        private static readonly GenerationTaskTrackerStore<VideoTaskInfo, PersistedTask> Store =
            new GenerationTaskTrackerStore<VideoTaskInfo, PersistedTask>(
                "TJGen_Video", BuildPersisted, FromPersisted);

        private static PersistedTask BuildPersisted(VideoTaskInfo info) => new PersistedTask
        {
            taskId          = info.TaskId,
            generatorId     = info.GeneratorId,
            prompt          = info.Prompt ?? "",
            imagePath       = info.ImagePath ?? "",
            status          = info.Status,
            progress        = info.Progress,
            videoPath       = info.VideoPath ?? "",
            errorMessage    = info.ErrorMessage ?? "",
            startTimeTicks  = info.StartTime.Ticks,
            endTimeTicks    = info.EndTime?.Ticks ?? 0,
            previewUrl      = info.PreviewUrl ?? "",
            lastFrameUrl    = info.LastFrameUrl ?? "",
            placeholderPath = info.PlaceholderPath ?? "",
            backendTaskId   = info.BackendTaskId ?? ""
        };

        private static VideoTaskInfo FromPersisted(PersistedTask p) => new VideoTaskInfo
        {
            TaskId          = p.taskId,
            GeneratorId     = p.generatorId,
            Prompt          = p.prompt,
            ImagePath       = p.imagePath,
            Status          = p.status,
            Progress        = p.progress,
            VideoPath       = p.videoPath,
            ErrorMessage    = p.errorMessage,
            PreviewUrl      = p.previewUrl,
            LastFrameUrl    = p.lastFrameUrl,
            StartTime       = new DateTime(p.startTimeTicks),
            EndTime         = p.endTimeTicks > 0 ? (DateTime?)new DateTime(p.endTimeTicks) : null,
            PlaceholderPath = p.placeholderPath,
            BackendTaskId   = p.backendTaskId
        };

        internal static void ApplyTaskUpdate(VideoTaskInfo task, Action<VideoTaskInfo> mutate) =>
            Store.ApplyTaskUpdate(task, mutate);

        public static string CreateTask(string generatorId, string prompt, string imagePath, string placeholderPath, string backendTaskId = null)
        {
            string taskId = Store.AllocateTaskId("video");
            var task = new VideoTaskInfo
            {
                TaskId          = taskId,
                GeneratorId     = generatorId,
                Prompt          = prompt ?? "",
                ImagePath       = imagePath ?? "",
                Status          = "generating",
                StartTime       = DateTime.Now,
                PlaceholderPath = placeholderPath,
                BackendTaskId   = backendTaskId
            };
            Store.RegisterTask(taskId, task);
            return taskId;
        }

        public static void MarkTaskCompleted(string taskId, string videoPath, string previewUrl = null, string lastFrameUrl = null)
        {
            var task = Store.GetTask(taskId);
            if (task == null) return;
            Store.ApplyTaskUpdate(task, t =>
            {
                t.Status       = "completed";
                t.Progress     = 100;
                t.VideoPath    = videoPath;
                t.PreviewUrl   = previewUrl;
                t.LastFrameUrl = lastFrameUrl;
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

        public static VideoTaskInfo GetTask(string taskId) => Store.GetTask(taskId);

        public static List<VideoTaskInfo> GetAllTasks() => Store.GetAllTasks();

        public static VideoTaskInfo GetTaskByBackendId(string backendTaskId) =>
            Store.GetTaskByBackendId(backendTaskId);

        public static VideoTaskInfo CreateRecoveredTask(
            string backendTaskId, string prompt, string placeholderPath, long timestampMs, string generatorId = null, string imagePath = null)
        {
            return Store.CreateRecoveredTask(backendTaskId, () => new VideoTaskInfo
            {
                TaskId          = $"recovered_{backendTaskId}",
                BackendTaskId   = backendTaskId,
                GeneratorId     = generatorId ?? "",
                Prompt          = prompt ?? "",
                ImagePath       = imagePath ?? "",
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

    public static class GenerateVideoTool
    {
        [ExecuteCustomTool.CustomTool("generate_video",
            "Generate a video asset from a text prompt or reference image using AI. " +
            "Output is an MP4 (VideoClip) saved to Assets/TJGenerators/History/. " +
            "Key parameters: generator_id (default 'huoshan_seedance'), " +
            "prompt (text description), image_path (optional reference image — omit for text-to-video), " +
            "video_path (optional: local MP4 file path for motion/camera reference — auto-switches to multimodal mode), " +
            "reference_images (optional: array of image paths for style/character/material references in multimodal mode), " +
            "audio_paths (optional: array of audio file paths for mood/rhythm reference in multimodal mode, requires video_path), " +
            "mode (optional: 'text_to_video', 'reference_image', 'first_frame', 'first_last_frame', or 'multimodal', auto-detected from inputs), " +
            "model (optional: 'doubao-seedance-2-0-mini-260615' (default), 'doubao-seedance-2-0-260128', 'doubao-seedance-2-0-fast-260128'), " +
            "resolution (optional: '720p' or '480p', default '720p'), " +
            "ratio (optional: '16:9', '9:16', '1:1', '4:3', '3:4', '21:9', 'adaptive', default '16:9'), " +
            "duration (optional: 4-15 seconds, default 12), " +
            "return_last_frame (optional: bool, default true), " +
            "generate_audio (optional: bool, default true), " +
            "output_path (optional save path). " +
            "IMPORTANT: Generation takes 30-120 seconds. Wait at least 5 seconds before the first " +
            "query_video_status call, then poll every 5-10 seconds. " +
            "A placeholder_path is returned immediately — you can reference it right away.")]
        public static object GenerateVideo(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateVideoTool] Generating video with parameters: {parameters}");

                string generatorId = parameters["generator_id"]?.ToString() ?? "huoshan_seedance";
                string prompt      = parameters["prompt"]?.ToString();
                string imagePath   = parameters["image_path"]?.ToString();
                string outputPath  = parameters["output_path"]?.ToString();
                string sessionId   = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());
                string videoPath   = parameters["video_path"]?.ToString();

                List<string> referenceImagePaths = null;
                var refImagesToken = parameters["reference_images"];
                if (refImagesToken != null && refImagesToken.Type == JTokenType.Array)
                    referenceImagePaths = refImagesToken.ToObject<List<string>>();

                List<string> audioPaths = null;
                var audioPathsToken = parameters["audio_paths"];
                if (audioPathsToken != null && audioPathsToken.Type == JTokenType.Array)
                    audioPaths = audioPathsToken.ToObject<List<string>>();

                if (string.IsNullOrEmpty(prompt) && string.IsNullOrEmpty(imagePath) && string.IsNullOrEmpty(videoPath))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "Either 'prompt', 'image_path', or 'video_path' must be provided" }
                    };
                }

                // Load video generator config
                var config = ConfigManager.GetGeneratorConfig(ConfigType.Video, generatorId);
                if (config == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Cannot find video generator config for '{generatorId}'. Valid value: 'huoshan_seedance'." }
                    };
                }

                // Create generator and set inputs
                var generator = new DynamicGenerator(config);

                if (!string.IsNullOrEmpty(prompt))
                    generator.SetTextPrompt(prompt);

                // Upload reference video (if provided) and get TOS URL
                string videoTosUrl = null;
                if (!string.IsNullOrEmpty(videoPath))
                {
                    string absVideoPath = ResolveLocalPath(videoPath);
                    if (string.IsNullOrEmpty(absVideoPath) || !File.Exists(absVideoPath))
                    {
                        return new Dictionary<string, object>
                        {
                            { "success", false },
                            { "message", "video_path not found: " + videoPath }
                        };
                    }

                    // Backend only accepts .mp4
                    string ext = Path.GetExtension(absVideoPath).ToLower();
                    if (ext != ".mp4")
                    {
                        return new Dictionary<string, object>
                        {
                            { "success", false },
                            { "message", "video_path must be an MP4 file, got: " + ext + ". Use FFmpeg or similar to convert." }
                        };
                    }

                    var multipart = new MultipartRequestData
                    {
                        FilePath = absVideoPath,
                        FileName = Path.GetFileName(absVideoPath),
                        FileFieldName = "video"
                    };
                    string uploadUrl = ConfigManager.GetApiBaseUrl() + "upload/video";
                    TJLog.Log("[GenerateVideoTool] Uploading reference video: " + absVideoPath + " → " + uploadUrl);

                    var httpResult = GenerationBackendSyncSubmit.PostMultipart(
                        uploadUrl, multipart, GenerationRequestOrigin.Agent, sessionId, 120f);

                    if (!httpResult.IsSuccess)
                    {
                        return new Dictionary<string, object>
                        {
                            { "success", false },
                            { "message", "Video upload failed: " + httpResult.Error }
                        };
                    }

                    var uploadResp = JObject.Parse(httpResult.Body);
                    videoTosUrl = uploadResp["url"]?.ToString();
                    if (string.IsNullOrEmpty(videoTosUrl))
                    {
                        return new Dictionary<string, object>
                        {
                            { "success", false },
                            { "message", "Video upload returned empty URL" }
                        };
                    }

                    TJLog.Log("[GenerateVideoTool] Reference video uploaded: " + videoTosUrl);
                }

                // Set reference images: multimodal uses reference_images array; otherwise single image_path
                if (referenceImagePaths != null && referenceImagePaths.Count > 0)
                {
                    generator.SetImagePaths(referenceImagePaths);
                }
                else if (!string.IsNullOrEmpty(imagePath))
                {
                    generator.SetImagePath(imagePath);
                }

                // Inject videos array via SetExtraRawJsonField (after upload)
                if (!string.IsNullOrEmpty(videoTosUrl))
                {
                    generator.SetExtraRawJsonField("videos", "[" + JsonConvert.SerializeObject(videoTosUrl) + "]");
                }

                // Upload audio files (if provided) and inject audios array
                if (audioPaths != null && audioPaths.Count > 0)
                {
                    // Audio requires multimodal mode (needs video_path); backend rejects audio without video
                    if (string.IsNullOrEmpty(videoTosUrl))
                    {
                        return new Dictionary<string, object>
                        {
                            { "success", false },
                            { "message", "audio_paths requires video_path (multimodal mode). Audio-only reference is not supported." }
                        };
                    }

                    var audioTosUrls = new List<string>();
                    foreach (string audioPath in audioPaths)
                    {
                        if (string.IsNullOrEmpty(audioPath)) continue;
                        string absAudioPath = ResolveLocalPath(audioPath);
                        if (string.IsNullOrEmpty(absAudioPath) || !File.Exists(absAudioPath))
                        {
                            return new Dictionary<string, object>
                            {
                                { "success", false },
                                { "message", "audio_path not found: " + audioPath }
                            };
                        }
                        var audioMultipart = new MultipartRequestData
                        {
                            FilePath = absAudioPath,
                            FileName = Path.GetFileName(absAudioPath),
                            FileFieldName = "audio"
                        };
                        string audioUploadUrl = ConfigManager.GetApiBaseUrl() + "upload/audio";
                        var audioHttpResult = GenerationBackendSyncSubmit.PostMultipart(
                            audioUploadUrl, audioMultipart, GenerationRequestOrigin.Agent, sessionId, 60f);
                        if (!audioHttpResult.IsSuccess)
                        {
                            return new Dictionary<string, object>
                            {
                                { "success", false },
                                { "message", "Audio upload failed: " + audioHttpResult.Error }
                            };
                        }
                        var audioUploadResp = JObject.Parse(audioHttpResult.Body);
                        string audioTosUrl = audioUploadResp["url"]?.ToString();
                        if (!string.IsNullOrEmpty(audioTosUrl))
                            audioTosUrls.Add(audioTosUrl);
                    }
                    if (audioTosUrls.Count > 0)
                    {
                        generator.SetExtraRawJsonField("audios", JsonConvert.SerializeObject(audioTosUrls));
                    }
                }

                // Apply optional parameters (includes mode auto-detection with multimodal support)
                ApplyVideoParameters(generator, generatorId, parameters);

                // 阶段 1：同步提交任务到后端
                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[GenerateVideoTool] 任务提交失败 [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[GenerateVideoTool] 任务提交成功，backend_task_id={submitResult.BackendTaskId}");

                // 提交成功后再创建 placeholder（避免鉴权失败时留下无用文件）
                string placeholderPath = CreatePlaceholderVideo(outputPath);

                // 注册任务
                string capturedBackendTaskId = submitResult.BackendTaskId;
                string taskId = VideoTaskTracker.CreateTask(generatorId, prompt, imagePath, placeholderPath, capturedBackendTaskId);

                // 创建 pipeline host
                var host = new VideoPipelineHost(
                    placeholderPath,
                    sessionId,
                    (savedPath, previewUrl, lastFrameUrl) =>
                    {
                        VideoTaskTracker.MarkTaskCompleted(taskId, savedPath, previewUrl, lastFrameUrl);
                        var t = VideoTaskTracker.GetTask(taskId);
                        GenerationNotifier.NotifyCompleted("generate_video", taskId, capturedBackendTaskId,
                            new JObject
                            {
                                ["session_id"]       = sessionId,
                                ["generator_id"]     = generatorId,
                                ["prompt"]           = prompt ?? "",
                                ["video_path"]       = savedPath ?? "",
                                ["preview_url"]      = previewUrl ?? "",
                                ["last_frame_url"]   = lastFrameUrl ?? "",
                                ["progress"]         = 100,
                                ["start_time"]       = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["end_time"]         = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
                            });
                    },
                    errorMsg =>
                    {
                        VideoTaskTracker.MarkTaskFailed(taskId, errorMsg);
                        GenerationNotifier.NotifyFailed("generate_video", taskId, capturedBackendTaskId, errorMsg,
                            new JObject { ["session_id"] = sessionId, ["generator_id"] = generatorId, ["prompt"] = prompt ?? "" });
                    }
                );

                // 阶段 2：异步轮询（跳过提交）
                var pipeline = new GenerationPipeline(host, ConfigType.Video, GenerationRequestOrigin.Agent, sessionId, "generate_video");
                string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(placeholderPath);
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId));

                TJLog.Log($"[GenerateVideoTool] 轮询已启动，task_id={taskId}, backend_task_id={submitResult.BackendTaskId}, placeholder: {placeholderPath}");

                string mode;
                if (parameters["mode"] != null)
                {
                    mode = parameters["mode"].ToString();
                }
                else if (!string.IsNullOrEmpty(videoPath))
                    mode = "multimodal";
                else if (referenceImagePaths != null && referenceImagePaths.Count == 2)
                    mode = "first_last_frame";
                else if (referenceImagePaths != null && referenceImagePaths.Count == 1)
                    mode = "first_frame";
                else if (referenceImagePaths != null && referenceImagePaths.Count > 0)
                    mode = "reference_image";
                else if (string.IsNullOrEmpty(imagePath))
                    mode = "text_to_video";
                else
                    mode = "reference_image";

                return new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "message",
                        "Video generation started. " +
                        "STEP 1 (do now): Note the placeholder_path for later use. " +
                        "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                        "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn (~60s) " +
                        "containing ALL generation results (video_path, preview_url, last_frame_url, timing, etc.). " +
                        "*** POLLING IS STRICTLY FORBIDDEN — do NOT call query_video_status repeatedly. " +
                        "Only call query_video_status ONCE as a last-resort fallback if no notification arrives. ***" },
                    { "task_id",            taskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       generatorId },
                    { "mode",               mode },
                    { "prompt",             prompt ?? "" },
                    { "placeholder_path",   placeholderPath },
                    { "preview_url",        PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "estimated_wait_seconds", 60 },
                    { "notification_mode",  "bg_task_done" }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateVideoTool] Error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error generating video: {e.Message}" }
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

        [ExecuteCustomTool.CustomTool("query_video_status",
            "Query the status of a video generation task. Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "When completed, returns 'video_path' with the VideoClip asset path in the project. " +
            "Status values: 'generating', 'recovering', 'completed', 'failed', 'interrupted'. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QueryVideoStatus(JObject parameters)
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

                var task = VideoTaskTracker.GetTask(taskId);

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
                    { "success",      true },
                    { "task_id",      task.TaskId },
                    { "generator_id", task.GeneratorId },
                    { "status",       task.Status },
                    { "progress",     task.Progress },
                    { "prompt",       task.Prompt },
                    { "start_time",   task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                };

                if (!string.IsNullOrEmpty(task.ImagePath)) result["input_image_path"] = task.ImagePath;
                if (!string.IsNullOrEmpty(task.VideoPath)) result["video_path"]        = task.VideoPath;
                result["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);
                if (!string.IsNullOrEmpty(task.LastFrameUrl)) result["last_frame_url"] = task.LastFrameUrl;
                if (!string.IsNullOrEmpty(task.ErrorMessage)) result["error"]           = task.ErrorMessage;

                if (task.EndTime.HasValue)
                {
                    result["end_time"]         = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                    result["duration_seconds"]  = (int)(task.EndTime.Value - task.StartTime).TotalSeconds;
                }

                if (task.Status == "generating" || task.Status == "recovering")
                {
                    if (!string.IsNullOrEmpty(task.PlaceholderPath))
                        result["placeholder_path"] = task.PlaceholderPath;
                }

                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[QueryVideoStatus] Query error: {e}");
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

        [ExecuteCustomTool.CustomTool("list_video_tasks", "List all active and recent video generation tasks")]
        public static object ListVideoTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks    = VideoTaskTracker.GetAllTasks();
                var taskList = new List<Dictionary<string, object>>();

                foreach (var task in tasks)
                {
                    var taskData = new Dictionary<string, object>
                    {
                        { "task_id",      task.TaskId },
                        { "generator_id", task.GeneratorId },
                        { "status",       task.Status },
                        { "progress",     task.Progress },
                        { "prompt",       task.Prompt },
                        { "start_time",   task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                    };

                    if (!string.IsNullOrEmpty(task.ImagePath))    taskData["input_image_path"] = task.ImagePath;
                    if (!string.IsNullOrEmpty(task.VideoPath))   taskData["video_path"]        = task.VideoPath;
                    taskData["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);
                    if (!string.IsNullOrEmpty(task.LastFrameUrl)) taskData["last_frame_url"]  = task.LastFrameUrl;
                    if (!string.IsNullOrEmpty(task.ErrorMessage)) taskData["error"]             = task.ErrorMessage;
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
                TJLog.LogError($"[ListVideoTasks] List error: {e}");
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
        /// <summary>
        /// Resolve a Unity-relative path (Assets/... / Packages/... / Editor/...) to an absolute OS path.
        /// Already-absolute paths are returned as-is. Matches VideoWindow / other tools via PathUtils.
        /// </summary>
        private static string ResolveLocalPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            return PathUtils.ToAbsoluteAssetPath(path);
        }

        private static void EnsureAssetDatabaseFolder(string folderPath)
        {
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

        internal static string CreatePlaceholderVideo(string outputPath, string namePrefix = "Video")
        {
            string placeholderPath;
            if (!string.IsNullOrEmpty(outputPath))
            {
                string dir = Path.GetDirectoryName(outputPath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir))
                    EnsureAssetDatabaseFolder(dir);
                placeholderPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.ChangeExtension(outputPath, ".mp4"));
            }
            else
            {
                string folder = PathUtils.GetProjectBrowserInsertionFolderAssetPath();
                string uniqueName = namePrefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4";
                placeholderPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{uniqueName}");
            }

            return TJGeneratorsVideoUtils.CreateBlankVideoClip(placeholderPath);
        }

        internal static void ApplyVideoParametersInternal(DynamicGenerator generator, string generatorId, JObject parameters)
        {
            ApplyVideoParameters(generator, generatorId, parameters);
        }

        /// <summary>
        /// Restore prompt/image and generator-specific defaults after domain reload.
        /// Session tracker takes precedence over InterruptedTasks.json.
        /// </summary>
        internal static void ApplyVideoRecoveryGeneratorSettings(
            DynamicGenerator generator, InterruptedTaskData interrupted, VideoTaskTracker.VideoTaskInfo trackerTask = null)
        {
            if (generator == null || interrupted == null) return;

            string generatorId = trackerTask?.GeneratorId ?? interrupted.modelVersion ?? generator.GeneratorId ?? "";
            string prompt = !string.IsNullOrEmpty(trackerTask?.Prompt) ? trackerTask.Prompt : interrupted.prompt;
            string imagePath = !string.IsNullOrEmpty(trackerTask?.ImagePath) ? trackerTask.ImagePath : interrupted.imagePath;

            if (!string.IsNullOrEmpty(prompt))
                generator.SetTextPrompt(prompt);
            if (!string.IsNullOrEmpty(imagePath))
                generator.SetImagePath(imagePath);

            if (generatorId == "huoshan_seedance")
            {
                generator.SetParameter("mode",
                    !string.IsNullOrEmpty(imagePath) ? "reference_image" : "text_to_video");
            }
        }

        private static void ApplyVideoParameters(DynamicGenerator generator, string generatorId, JObject parameters)
        {
            // Huoshan SeeDream Video parameters
            if (generatorId == "huoshan_seedance")
            {
                if (parameters["model"] != null)
                    generator.SetParameter("model", parameters["model"].ToString());

                // Match Video window / tool docs: auto-detect when mode omitted.
                // Config default is text_to_video; still override when image/video inputs are present.
                if (parameters["mode"] != null)
                {
                    generator.SetParameter("mode", parameters["mode"].ToString());
                }
                else
                {
                    bool hasVideo = !string.IsNullOrEmpty(parameters["video_path"]?.ToString());
                    bool hasImage = !string.IsNullOrEmpty(parameters["image_path"]?.ToString());
                    int refImageCount = 0;
                    if (parameters["reference_images"] != null && parameters["reference_images"].Type == JTokenType.Array)
                    {
                        var list = parameters["reference_images"].ToObject<List<string>>();
                        refImageCount = list != null ? list.Count : 0;
                    }
                    bool hasRefImages = refImageCount > 0;
                    if (hasVideo)
                        generator.SetParameter("mode", "multimodal");
                    else if (hasRefImages && refImageCount == 2)
                        generator.SetParameter("mode", "first_last_frame");
                    else if (hasRefImages && refImageCount == 1)
                        generator.SetParameter("mode", "first_frame");
                    else if (hasImage || hasRefImages)
                        generator.SetParameter("mode", "reference_image");
                    else
                        generator.SetParameter("mode", "text_to_video");
                }

                if (parameters["resolution"] != null)
                    generator.SetParameter("resolution", parameters["resolution"].ToString());

                if (parameters["ratio"] != null)
                    generator.SetParameter("ratio", parameters["ratio"].ToString());

                if (parameters["duration"] != null)
                    generator.SetParameter("duration", parameters["duration"].ToObject<int>());

                if (parameters["return_last_frame"] != null)
                    generator.SetParameter("return_last_frame", parameters["return_last_frame"].ToObject<bool>());

                if (parameters["generate_audio"] != null)
                    generator.SetParameter("generate_audio", parameters["generate_audio"].ToObject<bool>());
            }

            // Effect Video Workflow parameters (生图+生视频)
            if (generatorId == "effect_video_wf")
            {
                if (parameters["videoDuration"] != null)
                    generator.SetParameter("videoDuration", parameters["videoDuration"].ToObject<int>());
                else if (parameters["duration"] != null)
                    generator.SetParameter("videoDuration", parameters["duration"].ToObject<int>());

                if (parameters["videoRatio"] != null)
                    generator.SetParameter("videoRatio", parameters["videoRatio"].ToString());
                else if (parameters["ratio"] != null)
                    generator.SetParameter("videoRatio", parameters["ratio"].ToString());

                if (parameters["videoResolution"] != null)
                    generator.SetParameter("videoResolution", parameters["videoResolution"].ToString());
                else if (parameters["resolution"] != null)
                    generator.SetParameter("videoResolution", parameters["resolution"].ToString());
            }
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>
    /// Automatically resumes interrupted generate_video tasks after domain reload.
    /// </summary>
    [InitializeOnLoad]
    public static class VideoDomainReloadRecovery
    {
        static VideoDomainReloadRecovery()
        {
            CustomToolDomainReloadRecovery.Schedule(ResumeInterruptedTasks);
        }

        private static void ResumeInterruptedTasks()
        {
            CustomToolDomainReloadRecovery.Resume(
                "GenerateVideoTool",
                ConfigType.Video,
                t => t.toolName == "generate_video",
                () => VideoTaskTracker.GetAllTasks(),
                (interrupted, _, generator) =>
                {
                    var trackerTask = VideoTaskTracker.GetTaskByBackendId(interrupted.backendTaskId);
                    if (trackerTask != null)
                    {
                        CustomToolDomainReloadRecovery.MarkTrackerRecoveringIfNeeded(trackerTask.Status, () =>
                        {
                            VideoTaskTracker.ApplyTaskUpdate(trackerTask, t => t.Status = "recovering");
                        });
                    }
                    else
                    {
                        string placeholderPath = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);
                        trackerTask = VideoTaskTracker.CreateRecoveredTask(
                            interrupted.backendTaskId, interrupted.prompt, placeholderPath, interrupted.timestamp,
                            interrupted.modelVersion, interrupted.imagePath);
                    }

                    string placeholderPathForHost = trackerTask.PlaceholderPath ?? "";
                    if (string.IsNullOrEmpty(placeholderPathForHost))
                        placeholderPathForHost = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);

                    GenerateVideoTool.ApplyVideoRecoveryGeneratorSettings(generator, interrupted, trackerTask);

                    string sessionId = interrupted.sessionId ?? "";
                    string capturedBackendTaskId = interrupted.backendTaskId;
                    string taskId = trackerTask.TaskId;

                    var host = new VideoPipelineHost(
                        placeholderPathForHost,
                        sessionId,
                        (savedPath, previewUrl, lastFrameUrl) =>
                        {
                            VideoTaskTracker.MarkTaskCompleted(taskId, savedPath, previewUrl, lastFrameUrl);
                            var t = VideoTaskTracker.GetTask(taskId);
                            GenerationNotifier.NotifyCompleted("generate_video", taskId, capturedBackendTaskId,
                                new JObject
                                {
                                    ["session_id"]       = sessionId,
                                    ["generator_id"]     = t?.GeneratorId ?? interrupted.modelVersion ?? "",
                                    ["prompt"]           = t?.Prompt ?? interrupted.prompt ?? "",
                                    ["video_path"]       = savedPath ?? "",
                                    ["preview_url"]      = previewUrl ?? "",
                                    ["last_frame_url"]   = lastFrameUrl ?? "",
                                    ["progress"]         = 100,
                                    ["start_time"]       = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                    ["end_time"]         = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                    ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
                                });
                        },
                        errorMsg =>
                        {
                            VideoTaskTracker.MarkTaskFailed(taskId, errorMsg);
                            GenerationNotifier.NotifyFailed("generate_video", taskId, capturedBackendTaskId, errorMsg,
                                new JObject
                                {
                                    ["session_id"]   = sessionId,
                                    ["generator_id"] = trackerTask.GeneratorId ?? interrupted.modelVersion ?? "",
                                    ["prompt"]       = trackerTask.Prompt ?? interrupted.prompt ?? ""
                                });
                        });

                    CustomToolDomainReloadRecovery.StartPolling(
                        "GenerateVideoTool", host, ConfigType.Video,
                        sessionId, "generate_video", generator, interrupted.backendTaskId);
                });
        }
    }

    /// <summary>
    /// IGenerationPipelineHost implementation for headless video generation via custom tools.
    /// Handles video saving and task lifecycle callbacks.
    /// </summary>
    internal class VideoPipelineHost : HeadlessPipelineHostBase, IMediaAssetPipelineHost
    {
        private readonly string _placeholderPath;
        private readonly TJGeneratorsAssetReference _placeholderRef;
        private readonly string _sessionId;
        private readonly Action<string, string, string> _onCompleted;
        private readonly Action<string> _onFailed;

        public VideoPipelineHost(string placeholderPath, string sessionId, Action<string, string, string> onCompleted, Action<string> onFailed)
        {
            _placeholderPath = placeholderPath;
            _placeholderRef  = TJGeneratorsAssetReference.FromPath(placeholderPath);
            _sessionId       = sessionId ?? "";
            _onCompleted     = onCompleted;
            _onFailed        = onFailed;
        }

        protected override string DialogLogTag => "GenerateVideoTool";
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

            TJLog.Log($"[GenerateVideoTool] Video saved: {savePath}");

            // 标记为 AI 生成资产
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));
            TJGeneratorsGenerationLabel.EnableSessionLabel(TJGeneratorsAssetReference.FromPath(savePath), _sessionId);

            // 提取 previewUrl 和 lastFrameUrl 从 generator
            string previewUrl = generator.CurrentPreviewUrl;
            string lastFrameUrl = null;

            // 尝试从响应中获取 last_frame_url
            if (generator is DynamicGenerator dynamicGen)
            {
                var lastFrameField = dynamicGen.GetParameter("last_frame_url");
                if (lastFrameField != null)
                    lastFrameUrl = lastFrameField.ToString();
            }

            _onCompleted?.Invoke(savePath, previewUrl, lastFrameUrl);
        }
    }
#endif
}