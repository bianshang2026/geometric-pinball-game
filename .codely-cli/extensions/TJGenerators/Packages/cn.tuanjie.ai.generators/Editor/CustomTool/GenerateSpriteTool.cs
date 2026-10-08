using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Tracks active sprite generation tasks
    /// </summary>
    public static class SpriteTaskTracker
    {
#if UNITY_EDITOR
        [Serializable]
        private class PersistedTask
        {
            public string taskId;
            public string generatorId;
            public string prompt;
            public string imagePath;
            public string typeId;
            public string styleId;
            public string status;
            public int    progress;
            public string spritePath;
            public string errorMessage;
            public long   startTimeTicks;
            public long   endTimeTicks;
            public string previewUrl;
            public string placeholderPath;
            public string backendTaskId;
        }

        public class SpriteTaskInfo : IGenerationTaskInfo
        {
            public string TaskId { get; set; }
            public string GeneratorId { get; set; }
            public string Prompt { get; set; }
            public string ImagePath { get; set; }
            public string TypeId { get; set; }
            public string StyleId { get; set; }
            public string Status { get; set; }
            public int Progress { get; set; }
            public string SpritePath { get; set; }
            public string ErrorMessage { get; set; }
            public string PreviewUrl { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public string PlaceholderPath { get; set; }
            public string BackendTaskId { get; set; }
        }

        private static readonly GenerationTaskTrackerStore<SpriteTaskInfo, PersistedTask> Store =
            new GenerationTaskTrackerStore<SpriteTaskInfo, PersistedTask>(
                "TJGen_Sprite", BuildPersisted, FromPersisted);

        private static PersistedTask BuildPersisted(SpriteTaskInfo info) => new PersistedTask
        {
            taskId          = info.TaskId,
            generatorId     = info.GeneratorId,
            prompt          = info.Prompt ?? "",
            imagePath       = info.ImagePath ?? "",
            typeId          = info.TypeId ?? "",
            styleId         = info.StyleId ?? "",
            status          = info.Status,
            progress        = info.Progress,
            spritePath      = info.SpritePath ?? "",
            errorMessage    = info.ErrorMessage ?? "",
            startTimeTicks  = info.StartTime.Ticks,
            endTimeTicks    = info.EndTime?.Ticks ?? 0,
            previewUrl      = info.PreviewUrl ?? "",
            placeholderPath = info.PlaceholderPath ?? "",
            backendTaskId   = info.BackendTaskId ?? ""
        };

        private static SpriteTaskInfo FromPersisted(PersistedTask p) => new SpriteTaskInfo
        {
            TaskId          = p.taskId,
            GeneratorId     = p.generatorId,
            Prompt          = p.prompt,
            ImagePath       = p.imagePath,
            TypeId          = p.typeId,
            StyleId         = p.styleId,
            Status          = p.status,
            Progress        = p.progress,
            SpritePath      = p.spritePath,
            ErrorMessage    = p.errorMessage,
            PreviewUrl      = p.previewUrl,
            StartTime       = new DateTime(p.startTimeTicks),
            EndTime         = p.endTimeTicks > 0 ? (DateTime?)new DateTime(p.endTimeTicks) : null,
            PlaceholderPath = p.placeholderPath,
            BackendTaskId   = p.backendTaskId
        };

        internal static void ApplyTaskUpdate(SpriteTaskInfo task, Action<SpriteTaskInfo> mutate) =>
            Store.ApplyTaskUpdate(task, mutate);

        public static string CreateTask(string generatorId, string prompt, string imagePath = null, string typeId = null, string styleId = null, string placeholderPath = null, string backendTaskId = null)
        {
            string taskId = Store.AllocateTaskId("sprite");
            var task = new SpriteTaskInfo
            {
                TaskId          = taskId,
                GeneratorId     = generatorId,
                Prompt          = prompt ?? "",
                ImagePath       = imagePath ?? "",
                TypeId          = typeId ?? "",
                StyleId         = styleId ?? "",
                Status          = "generating",
                StartTime       = DateTime.Now,
                PlaceholderPath = placeholderPath,
                BackendTaskId   = backendTaskId
            };
            Store.RegisterTask(taskId, task);
            return taskId;
        }

        public static void MarkTaskCompleted(string taskId, string spritePath, string previewUrl = null)
        {
            var task = Store.GetTask(taskId);
            if (task == null) return;
            Store.ApplyTaskUpdate(task, t =>
            {
                t.Status     = "completed";
                t.Progress   = 100;
                t.SpritePath = spritePath;
                t.PreviewUrl = previewUrl;
                t.EndTime    = DateTime.Now;
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

        public static SpriteTaskInfo GetTask(string taskId) => Store.GetTask(taskId);

        public static List<SpriteTaskInfo> GetAllTasks() => Store.GetAllTasks();

        public static SpriteTaskInfo GetTaskByBackendId(string backendTaskId) =>
            Store.GetTaskByBackendId(backendTaskId);

        public static SpriteTaskInfo CreateRecoveredTask(
            string backendTaskId, string prompt, string placeholderPath, long timestampMs, string generatorId = null)
        {
            return Store.CreateRecoveredTask(backendTaskId, () => new SpriteTaskInfo
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
    /// CustomTool for generating 2D sprite assets using TJGenerators Sprite pipeline (Huoshan SeeDream).
    /// Supports text-to-sprite and image-to-sprite generation with content type and art style selection.
    /// Output is a PNG imported as Sprite (Texture2D) saved to Assets/TJGenerators/History/.
    /// </summary>
    public static class GenerateSpriteTool
    {
        [ExecuteCustomTool.CustomTool("generate_sprite",
            "Generate a 2D sprite (game icon, item image, UI element, character portrait) from a text prompt " +
            "or reference image using AI. Output is a PNG imported as a Unity Sprite asset, saved to Assets/TJGenerators/History/. " +
            "Key parameters: generator_id (default 'frontier-game-design'; also 'huoshan_seedream', 'frontier-effect'), prompt (text description), " +
            "image_path (optional reference image), type_id (content type, e.g. 'weapon_melee', 'ui_icon'), " +
            "style_id (art style, e.g. 'pixel', 'anime', 'cartoon'), size (output resolution), " +
            "is_segmentation (bool, auto-remove background, default true), output_path (optional save path), " +
            "imageSize (frontier-game-design only, 'square_hd'/'square'/'portrait_4_3'/'portrait_16_9'/'landscape_4_3'/'landscape_16_9', default 'square_hd'), " +
            "outputFormat (frontier-game-design only, 'png'/'jpeg', default 'png'). " +
            "IMPORTANT: Generation takes 1-3 minutes. Wait at least 5 seconds before the first query_sprite_status call, " +
            "then poll every 10-15 seconds. A placeholder_path is returned immediately — you can assign it to a SpriteRenderer right away.")]
        public static object GenerateSprite(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateSpriteTool] Generating sprite with parameters: {parameters}");

                string generatorId = parameters["generator_id"]?.ToString() ?? "huoshan_seedream";
                string prompt = parameters["prompt"]?.ToString();
                string imagePath = parameters["image_path"]?.ToString();
                string typeId = parameters["type_id"]?.ToString();
                string styleId = parameters["style_id"]?.ToString();
                string outputPath = parameters["output_path"]?.ToString();
                string sessionId = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());

                if (string.IsNullOrEmpty(prompt) && string.IsNullOrEmpty(imagePath))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "Either 'prompt' or 'image_path' must be provided" }
                    };
                }

                int maxLen = TJGeneratorsPromptLimits.GetMaxLength(generatorId);
                if (maxLen > 0 && !string.IsNullOrEmpty(prompt) && prompt.Length > maxLen)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "INVALID_PARAMS" },
                        { "message", $"Prompt length ({prompt.Length}) exceeds the {maxLen} character limit for '{generatorId}'." }
                    };
                }

                // Load sprite generator config
                var config = ConfigManager.GetGeneratorConfig(ConfigType.Sprite, generatorId);
                if (config == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Cannot find sprite generator config for '{generatorId}'. Valid values: 'frontier-game-design', 'huoshan_seedream', 'frontier-effect'." }
                    };
                }

                // Create generator and set inputs
                var generator = new DynamicGenerator(config);

                if (!string.IsNullOrEmpty(prompt))
                    generator.SetTextPrompt(prompt);

                if (!string.IsNullOrEmpty(imagePath))
                    generator.SetImagePath(imagePath);

                // Resolve and apply type/style selections
                if (!string.IsNullOrEmpty(typeId))
                {
                    var typeOption = FindTypeOption(config, typeId);
                    if (typeOption != null)
                        generator.SetTypeSelection(typeOption);
                    else
                        TJLog.LogWarning($"[GenerateSpriteTool] type_id '{typeId}' not found in config, ignoring.");
                }

                if (!string.IsNullOrEmpty(styleId))
                {
                    var styleOption = FindStyleOption(config, styleId);
                    if (styleOption != null)
                        generator.SetStyleSelection(styleOption);
                    else
                        TJLog.LogWarning($"[GenerateSpriteTool] style_id '{styleId}' not found in config, ignoring.");
                }

                // Apply optional parameters
                ApplySpriteParameters(generator, parameters);

                // 阶段1：同步提交任务到后端，立即获取 backendTaskId 或失败原因
                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[GenerateSpriteTool] 任务提交失败 [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[GenerateSpriteTool] 任务提交成功，backend_task_id={submitResult.BackendTaskId}");

                // 提交成功后才创建 placeholder（避免在鉴权失败时留下无用文件）
                string placeholderPath = CreatePlaceholderTexture(outputPath);

                // Create tracked task
                string capturedBackendTaskId = submitResult.BackendTaskId;
                string taskId = SpriteTaskTracker.CreateTask(generatorId, prompt, imagePath, typeId, styleId, placeholderPath, capturedBackendTaskId);

                // Create pipeline host
                var host = new SpritePipelineHost(placeholderPath, sessionId,
                    (savedPath, previewUrl) =>
                    {
                        SpriteTaskTracker.MarkTaskCompleted(taskId, savedPath, previewUrl);
                        var t = SpriteTaskTracker.GetTask(taskId);
                        GenerationNotifier.NotifyCompleted("generate_sprite", taskId, capturedBackendTaskId,
                            new JObject
                            {
                                ["session_id"]       = sessionId,
                                ["generator_id"]     = generatorId,
                                ["prompt"]           = prompt ?? "",
                                ["image_path"]       = savedPath,
                                ["preview_url"]      = previewUrl ?? "",
                                ["progress"]         = 100,
                                ["start_time"]       = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["end_time"]         = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
                            });
                    },
                    errorMsg =>
                    {
                        SpriteTaskTracker.MarkTaskFailed(taskId, errorMsg);
                        GenerationNotifier.NotifyFailed("generate_sprite", taskId, capturedBackendTaskId, errorMsg,
                            new JObject { ["session_id"] = sessionId, ["generator_id"] = generatorId, ["prompt"] = prompt ?? "" });
                    });

                string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(placeholderPath);

                // 阶段2：异步轮询（跳过提交）
                var pipeline = new GenerationPipeline(host, ConfigType.Sprite, GenerationRequestOrigin.Agent, sessionId, "generate_sprite");
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId));

                TJLog.Log($"[GenerateSpriteTool] 轮询已启动，task_id={taskId}, backend_task_id={submitResult.BackendTaskId}, placeholder: {placeholderPath}");

                var result = new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "message",
                        "Sprite generation started. " +
                        "STEP 1 (do now): Apply placeholder_path to a SpriteRenderer (or use `place_assets_in_scene` skill). " +
                        "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                        "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn (~90s) " +
                        "containing ALL generation results (image_path, preview_url, timing, etc.). " +
                        "*** POLLING IS STRICTLY FORBIDDEN — do NOT call query_sprite_status repeatedly. " +
                        "Only call query_sprite_status ONCE as a last-resort fallback if no notification arrives. ***" },
                    { "task_id",            taskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       generatorId },
                    { "prompt",             prompt ?? "" },
                    { "placeholder_path",   placeholderPath },
                    { "estimated_wait_seconds", 90 },
                    { "preview_url",        PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "notification_mode",  "bg_task_done" }
                };

                if (!string.IsNullOrEmpty(typeId)) result["type_id"] = typeId;
                if (!string.IsNullOrEmpty(styleId)) result["style_id"] = styleId;

                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateSpriteTool] Error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error generating sprite: {e.Message}" }
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

        [ExecuteCustomTool.CustomTool("query_sprite_status",
            "Query the status of a sprite generation task. Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "When completed, returns 'sprite_path' with the Sprite asset path in the project. " +
            "Status values: 'generating', 'completed', 'failed'. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QuerySpriteStatus(JObject parameters)
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

                var task = SpriteTaskTracker.GetTask(taskId);

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

                if (!string.IsNullOrEmpty(task.TypeId)) result["type_id"] = task.TypeId;
                if (!string.IsNullOrEmpty(task.StyleId)) result["style_id"] = task.StyleId;
                if (!string.IsNullOrEmpty(task.SpritePath)) result["sprite_path"] = task.SpritePath;
                result["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);
                if (!string.IsNullOrEmpty(task.ErrorMessage)) result["error"] = task.ErrorMessage;

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
                TJLog.LogError($"[GenerateSpriteTool] Query error: {e}");
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

        [ExecuteCustomTool.CustomTool("list_sprite_tasks", "List all active and recent sprite generation tasks")]
        public static object ListSpriteTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks = SpriteTaskTracker.GetAllTasks();
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

                    if (!string.IsNullOrEmpty(task.TypeId)) taskData["type_id"] = task.TypeId;
                    if (!string.IsNullOrEmpty(task.StyleId)) taskData["style_id"] = task.StyleId;
                    if (!string.IsNullOrEmpty(task.SpritePath)) taskData["sprite_path"] = task.SpritePath;
                    taskData["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);
                    if (!string.IsNullOrEmpty(task.ErrorMessage)) taskData["error"] = task.ErrorMessage;
                    if (task.EndTime.HasValue) taskData["end_time"] = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");

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
                TJLog.LogError($"[GenerateSpriteTool] List error: {e}");
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

        private static string CreatePlaceholderTexture(string outputPath)
        {
            string placeholderPath;
            if (!string.IsNullOrEmpty(outputPath))
            {
                string dir = Path.GetDirectoryName(outputPath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir))
                    EnsureAssetDatabaseFolder(dir);
                placeholderPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.ChangeExtension(outputPath, ".png"));
            }
            else
            {
                if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                    AssetDatabase.CreateFolder("Assets", "TJGenerators");
                if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                    AssetDatabase.CreateFolder("Assets/TJGenerators", "History");
                string uniqueName = "Sprite_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                placeholderPath = AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/History/" + uniqueName);
            }

            // Create 1x1 gray placeholder PNG
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1f));
            tex.Apply();
            byte[] pngBytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            string absolutePath = PathUtils.ToAbsoluteAssetPath(placeholderPath);
            File.WriteAllBytes(absolutePath, pngBytes);
            PathUtils.ImportAssetAfterDiskWrite(placeholderPath);
            GeneratedTextureImportUtils.ConfigureImportedTexture(
                placeholderPath, TextureImporterType.Sprite, alphaIsTransparency: true);

            return placeholderPath;
        }

        private static SelectorOptionConfig FindTypeOption(GeneratorConfig config, string typeId)
        {
            return config.typeSelector?.options?.FirstOrDefault(o => o.id == typeId);
        }

        private static SelectorOptionConfig FindStyleOption(GeneratorConfig config, string styleId)
        {
            return config.styleSelector?.options?.FirstOrDefault(o => o.id == styleId);
        }

        private static void ApplySpriteParameters(DynamicGenerator generator, JObject parameters)
        {
            if (parameters["is_segmentation"] != null)
                generator.SetParameter("isSegmentation", parameters["is_segmentation"].ToObject<bool>());

            if (parameters["size"] != null)
                generator.SetParameter("size", parameters["size"].ToString());

            if (parameters["q_value"] != null)
                generator.SetParameter("qValue", parameters["q_value"].ToObject<int>());

            if (parameters["resize_width"] != null)
                generator.SetParameter("resizeWidth", parameters["resize_width"].ToObject<int>());

            if (parameters["imageSize"] != null)
                generator.SetParameter("imageSize", parameters["imageSize"].ToString());

            if (parameters["outputFormat"] != null)
                generator.SetParameter("outputFormat", parameters["outputFormat"].ToString());
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>
    /// Automatically resumes interrupted generate_sprite tasks after domain reload.
    /// </summary>
    [InitializeOnLoad]
    public static class SpriteDomainReloadRecovery
    {
        static SpriteDomainReloadRecovery()
        {
            CustomToolDomainReloadRecovery.Schedule(ResumeInterruptedTasks);
        }

        private static void ResumeInterruptedTasks()
        {
            CustomToolDomainReloadRecovery.Resume(
                "GenerateSpriteTool",
                ConfigType.Sprite,
                t => t.toolName == "generate_sprite",
                () => SpriteTaskTracker.GetAllTasks(),
                (interrupted, _, generator) =>
                {
                    var trackerTask = SpriteTaskTracker.GetTaskByBackendId(interrupted.backendTaskId);
                    if (trackerTask != null)
                    {
                        CustomToolDomainReloadRecovery.MarkTrackerRecoveringIfNeeded(trackerTask.Status, () =>
                        {
                            SpriteTaskTracker.ApplyTaskUpdate(trackerTask, t => t.Status = "recovering");
                        });
                    }
                    else
                    {
                        string placeholderPath = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);
                        trackerTask = SpriteTaskTracker.CreateRecoveredTask(
                            interrupted.backendTaskId, interrupted.prompt, placeholderPath, interrupted.timestamp,
                            interrupted.modelVersion);
                    }

                    string placeholderPathForHost = trackerTask.PlaceholderPath ?? "";
                    if (string.IsNullOrEmpty(placeholderPathForHost))
                        placeholderPathForHost = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);

                    var host = new SpriteRecoveryHost(
                        placeholderPathForHost, interrupted.backendTaskId, interrupted.sessionId, generator);
                    CustomToolDomainReloadRecovery.StartPolling(
                        "GenerateSpriteTool", host, ConfigType.Sprite,
                        interrupted.sessionId, "generate_sprite", generator, interrupted.backendTaskId);
                });
        }
    }

    /// <summary>
    /// Headless pipeline host for resuming sprite tasks after domain reload.
    /// </summary>
    internal class SpriteRecoveryHost : HeadlessPipelineHostBase, IMediaAssetPipelineHost
    {
        private readonly string _placeholderPath;
        private readonly TJGeneratorsAssetReference _placeholderRef;
        private readonly string _backendTaskId;
        private readonly string _sessionId;
        private readonly ModelGeneratorBase _generator;

        public SpriteRecoveryHost(string placeholderPath, string backendTaskId, string sessionId, ModelGeneratorBase generator)
        {
            _placeholderPath = placeholderPath ?? "";
            _placeholderRef = string.IsNullOrEmpty(_placeholderPath)
                ? null
                : TJGeneratorsAssetReference.FromPath(_placeholderPath);
            _backendTaskId = backendTaskId;
            _sessionId = sessionId ?? "";
            _generator = generator;
        }

        protected override string DialogLogTag => "SpriteRecovery";

        public override TJGeneratorsAssetReference GetTargetAsset() => _placeholderRef;

        public override void Repaint()
        {
            if (_generator == null) return;
            var trackerTask = SpriteTaskTracker.GetTaskByBackendId(_backendTaskId);
            if (trackerTask == null || !TJGeneratorsTaskRecovery.IsRecoverableTrackerStatus(trackerTask.Status)) return;

            int progress = _generator.CurrentProgress;
            if (progress <= trackerTask.Progress) return;

            SpriteTaskTracker.ApplyTaskUpdate(trackerTask, t =>
            {
                t.Status = "generating";
                t.Progress = progress;
            });
        }

        public override void ShowDialog(string title, string message)
        {
            base.ShowDialog(title, message);

            if (ErrorDialogUtils.IsErrorDialog(title))
            {
                var trackerTask = SpriteTaskTracker.GetTaskByBackendId(_backendTaskId);
                if (trackerTask != null)
                {
                    var friendlyError = ErrorDialogUtils.ConvertToUserFriendlyError(title, message);
                    SpriteTaskTracker.MarkTaskFailed(trackerTask.TaskId, friendlyError.TechnicalMessage);
                    GenerationNotifier.NotifyFailed(
                        "generate_sprite",
                        trackerTask.TaskId,
                        _backendTaskId,
                        friendlyError.TechnicalMessage,
                        new JObject
                        {
                            ["session_id"] = _sessionId,
                            ["generator_id"] = trackerTask.GeneratorId ?? "",
                            ["prompt"] = trackerTask.Prompt ?? ""
                        });
                }
            }
        }

        public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator) =>
            _type == PipelineMediaType.Texture ? _placeholderPath : null;

        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return;

            TJLog.Log($"[GenerateSpriteTool] Recovered sprite saved: {savePath}");

            GeneratedTextureImportUtils.ConfigureImportedTexture(
                savePath, TextureImporterType.Sprite, alphaIsTransparency: true);

            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));
            TJGeneratorsGenerationLabel.EnableSessionLabel(TJGeneratorsAssetReference.FromPath(savePath), _sessionId);

            var trackerTask = SpriteTaskTracker.GetTaskByBackendId(_backendTaskId);
            if (trackerTask == null) return;

            string previewUrl = generator?.CurrentPreviewUrl;
            SpriteTaskTracker.MarkTaskCompleted(trackerTask.TaskId, savePath, previewUrl);
            var t = SpriteTaskTracker.GetTask(trackerTask.TaskId);
            GenerationNotifier.NotifyCompleted(
                "generate_sprite",
                trackerTask.TaskId,
                _backendTaskId,
                new JObject
                {
                    ["session_id"] = _sessionId,
                    ["generator_id"] = t?.GeneratorId ?? "",
                    ["prompt"] = t?.Prompt ?? "",
                    ["image_path"] = savePath ?? "",
                    ["preview_url"] = previewUrl ?? "",
                    ["progress"] = 100,
                    ["start_time"] = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                    ["end_time"] = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                    ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
                });
        }
    }

    /// <summary>
    /// IGenerationPipelineHost implementation for headless sprite generation via custom tools.
    /// Handles texture saving with Sprite import settings and task lifecycle callbacks.
    /// </summary>
    internal class SpritePipelineHost : HeadlessPipelineHostBase, IMediaAssetPipelineHost
    {
        private readonly string _placeholderPath;
        private readonly TJGeneratorsAssetReference _placeholderRef;
        private readonly string _sessionId;
        private readonly Action<string, string> _onCompleted;
        private readonly Action<string> _onFailed;

        public SpritePipelineHost(string placeholderPath, string sessionId, Action<string, string> onCompleted, Action<string> onFailed)
        {
            _placeholderPath = placeholderPath;
            _placeholderRef = TJGeneratorsAssetReference.FromPath(placeholderPath);
            _sessionId = sessionId ?? "";
            _onCompleted = onCompleted;
            _onFailed = onFailed;
        }

        protected override string DialogLogTag => "GenerateSpriteTool";
        protected override Action<string> DialogFailedCallback => errorMessage => _onFailed?.Invoke(errorMessage);

        public override TJGeneratorsAssetReference GetTargetAsset() => _placeholderRef;

        public void StartEditorCoroutine(IEnumerator coroutine)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(coroutine);
        }

        public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return null;

            // Return the placeholder path directly — pipeline overwrites the file bytes in-place.
            // Do NOT delete the placeholder: deleting would assign a new GUID on reimport,
            // breaking any SpriteRenderer references set up before generation completed.
            return _placeholderPath;
        }

        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return;

            TJLog.Log($"[GenerateSpriteTool] Sprite saved: {savePath}");

            GeneratedTextureImportUtils.ConfigureImportedTexture(
                savePath, TextureImporterType.Sprite, alphaIsTransparency: true);

            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));
            TJGeneratorsGenerationLabel.EnableSessionLabel(TJGeneratorsAssetReference.FromPath(savePath), _sessionId);
            _onCompleted?.Invoke(savePath, generator.CurrentPreviewUrl);
        }
    }
#endif
}
