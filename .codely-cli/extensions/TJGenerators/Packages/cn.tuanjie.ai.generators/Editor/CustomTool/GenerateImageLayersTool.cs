using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
    /// Tracks image-layering (generate_image_layers) tasks across domain reloads.
    /// </summary>
    public static class ImageLayersTaskTracker
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
            public int progress;
            public int layerCount;
            public string layer0Path;
            public string layersFolder;
            public string errorMessage;
            public long startTimeTicks;
            public long endTimeTicks;
            public string previewUrl;
            public string placeholderPath;
            public string backendTaskId;
        }

        public class ImageLayersTaskInfo : IGenerationTaskInfo
        {
            public string TaskId { get; set; }
            public string GeneratorId { get; set; }
            public string Prompt { get; set; }
            public string ImagePath { get; set; }
            public string Status { get; set; }
            public int Progress { get; set; }
            public int LayerCount { get; set; }
            public string Layer0Path { get; set; }
            public string LayersFolder { get; set; }
            public string ErrorMessage { get; set; }
            public string PreviewUrl { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public string PlaceholderPath { get; set; }
            public string BackendTaskId { get; set; }
        }

        private static readonly GenerationTaskTrackerStore<ImageLayersTaskInfo, PersistedTask> Store =
            new GenerationTaskTrackerStore<ImageLayersTaskInfo, PersistedTask>(
                "TJGen_ImageLayers", BuildPersisted, FromPersisted);

        private static PersistedTask BuildPersisted(ImageLayersTaskInfo info) => new PersistedTask
        {
            taskId = info.TaskId,
            generatorId = info.GeneratorId,
            prompt = info.Prompt ?? "",
            imagePath = info.ImagePath ?? "",
            status = info.Status,
            progress = info.Progress,
            layerCount = info.LayerCount,
            layer0Path = info.Layer0Path ?? "",
            layersFolder = info.LayersFolder ?? "",
            errorMessage = info.ErrorMessage ?? "",
            startTimeTicks = info.StartTime.Ticks,
            endTimeTicks = info.EndTime?.Ticks ?? 0,
            previewUrl = info.PreviewUrl ?? "",
            placeholderPath = info.PlaceholderPath ?? "",
            backendTaskId = info.BackendTaskId ?? ""
        };

        private static ImageLayersTaskInfo FromPersisted(PersistedTask p) => new ImageLayersTaskInfo
        {
            TaskId = p.taskId,
            GeneratorId = p.generatorId,
            Prompt = p.prompt,
            ImagePath = p.imagePath,
            Status = p.status,
            Progress = p.progress,
            LayerCount = p.layerCount,
            Layer0Path = p.layer0Path,
            LayersFolder = p.layersFolder,
            ErrorMessage = p.errorMessage,
            PreviewUrl = p.previewUrl,
            StartTime = new DateTime(p.startTimeTicks),
            EndTime = p.endTimeTicks > 0 ? (DateTime?)new DateTime(p.endTimeTicks) : null,
            PlaceholderPath = p.placeholderPath,
            BackendTaskId = p.backendTaskId
        };

        internal static void ApplyTaskUpdate(ImageLayersTaskInfo task, Action<ImageLayersTaskInfo> mutate) =>
            Store.ApplyTaskUpdate(task, mutate);

        public static string CreateTask(
            string generatorId,
            string prompt,
            string imagePath,
            int layerCount,
            string placeholderPath = null,
            string backendTaskId = null)
        {
            string taskId = Store.AllocateTaskId("image_layers");
            var task = new ImageLayersTaskInfo
            {
                TaskId = taskId,
                GeneratorId = generatorId,
                Prompt = prompt ?? "",
                ImagePath = imagePath ?? "",
                LayerCount = layerCount,
                Status = "generating",
                StartTime = DateTime.Now,
                PlaceholderPath = placeholderPath,
                BackendTaskId = backendTaskId
            };
            Store.RegisterTask(taskId, task);
            return taskId;
        }

        public static void MarkTaskCompleted(
            string taskId,
            string layer0Path,
            string layersFolder,
            string previewUrl = null)
        {
            var task = Store.GetTask(taskId);
            if (task == null) return;
            Store.ApplyTaskUpdate(task, t =>
            {
                t.Status = "completed";
                t.Progress = 100;
                t.Layer0Path = layer0Path;
                t.LayersFolder = layersFolder;
                t.PreviewUrl = previewUrl;
                t.EndTime = DateTime.Now;
            });
        }

        public static void MarkTaskFailed(string taskId, string errorMessage)
        {
            var task = Store.GetTask(taskId);
            if (task == null) return;
            Store.ApplyTaskUpdate(task, t =>
            {
                t.Status = "failed";
                t.ErrorMessage = errorMessage;
                t.EndTime = DateTime.Now;
            });
        }

        public static ImageLayersTaskInfo GetTask(string taskId) => Store.GetTask(taskId);

        public static List<ImageLayersTaskInfo> GetAllTasks() => Store.GetAllTasks();

        public static ImageLayersTaskInfo GetTaskByBackendId(string backendTaskId) =>
            Store.GetTaskByBackendId(backendTaskId);

        public static ImageLayersTaskInfo CreateRecoveredTask(
            string backendTaskId,
            string prompt,
            string placeholderPath,
            long timestampMs,
            int layerCount = 4,
            string generatorId = null,
            string imagePath = null)
        {
            return Store.CreateRecoveredTask(backendTaskId, () => new ImageLayersTaskInfo
            {
                TaskId = $"recovered_{backendTaskId}",
                BackendTaskId = backendTaskId,
                GeneratorId = generatorId ?? GenerateImageLayersTool.GeneratorId,
                Prompt = prompt ?? "",
                ImagePath = imagePath ?? "",
                LayerCount = layerCount > 0 ? layerCount : 4,
                PlaceholderPath = placeholderPath ?? "",
                Status = "recovering",
                Progress = 0,
                StartTime = timestampMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).LocalDateTime
                    : DateTime.Now
            });
        }

        public static void RemoveTask(string taskId) => Store.RemoveTask(taskId);

        public static void CleanupCompletedTasks() => Store.CleanupCompletedTasks();
#endif
    }

    /// <summary>
    /// CustomTool for splitting one image into multiple RGBA layers via image-layering.
    /// </summary>
    public static class GenerateImageLayersTool
    {
        public const string GeneratorId = "image-layering";
        public const string SeedreamGeneratorId = "seedream-image-layering";
        private const string ToolName = "generate_image_layers";

        // Seedream 自动分层：底图 + 最多 16 层 = 17 张；CollectIndexedSiblingPaths 遇缺口自动停止
        internal const int SeedreamMaxLayerCount = 17;

        /// <summary>provider 参数 → generator id（默认 qwen，保持旧行为不变）</summary>
        internal static string ResolveGeneratorId(string provider)
        {
            return string.Equals(provider, "seedream_pro", StringComparison.OrdinalIgnoreCase)
                ? SeedreamGeneratorId
                : GeneratorId;
        }

        [ExecuteCustomTool.CustomTool("generate_image_layers",
            "Split one image into multiple independent RGBA layers using AI. " +
            "Providers: qwen (default, 1-8 layers via num_layers, prompt required) or " +
            "seedream_pro (Seedream 5.0 Pro layer decomposition: auto base image + up to 16 transparent PNG layers, " +
            "prompt optional, num_layers ignored, optional size tier 1K/1.5K/2K/auto). " +
            "Output: N PNG textures under Assets/TJGenerators/History/ (layer 0 overwrites placeholder; " +
            "extra layers saved as {basename}_1.png, {basename}_2.png, ...). " +
            "Parameters: image_path (required), provider (optional qwen|seedream_pro, default qwen), " +
            "prompt (required for qwen; optional split hint for seedream_pro), " +
            "num_layers (optional int 1-8, default 4, qwen only), size (optional 1K|1.5K|2K|auto, seedream_pro only), " +
            "output_path (optional). " +
            "IMPORTANT: Wait for <bg_task_done>. Do NOT poll query_image_layers_status repeatedly.")]
        public static object GenerateImageLayers(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateImageLayersTool] Generating layers with parameters: {parameters}");

                string provider = parameters["provider"]?.ToString();
                string generatorId = ResolveGeneratorId(provider);
                bool isSeedream = string.Equals(generatorId, SeedreamGeneratorId, StringComparison.OrdinalIgnoreCase);

                string prompt = parameters["prompt"]?.ToString();
                string imagePath = parameters["image_path"]?.ToString();
                string outputPath = parameters["output_path"]?.ToString();
                string sessionId = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());

                if (string.IsNullOrEmpty(imagePath))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "INVALID_PARAMS" },
                        { "message", "'image_path' is required for image layering" }
                    };
                }

                // qwen 需要 prompt 描述图片；seedream_pro 的 prompt 是可选的拆分提示词（留空自动拆分）
                if (!isSeedream && (string.IsNullOrEmpty(prompt) || string.IsNullOrWhiteSpace(prompt)))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "INVALID_PARAMS" },
                        { "message", "'prompt' is required for image layering (provider=qwen). " +
                                     "For seedream_pro the prompt is optional." }
                    };
                }

                int maxLen = TJGeneratorsPromptLimits.GetMaxLength(generatorId);
                if (maxLen > 0 && !string.IsNullOrEmpty(prompt) && prompt.Length > maxLen)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "INVALID_PARAMS" },
                        { "message",
                            $"Prompt length ({prompt.Length}) exceeds the {maxLen} character limit for '{generatorId}'." }
                    };
                }

                int numLayers = ParseNumLayers(parameters["num_layers"]);
                if (isSeedream && parameters["num_layers"] != null)
                {
                    TJLog.Log("[GenerateImageLayersTool] seedream_pro auto-splits layers; ignoring num_layers parameter.");
                }
                int expectedLayerCount = isSeedream ? SeedreamMaxLayerCount : numLayers;

                string absPath = ResolveImagePath(imagePath);
                if (string.IsNullOrEmpty(absPath))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "FILE_NOT_FOUND" },
                        { "message", $"Image file not found: {imagePath}" }
                    };
                }

                var config = ConfigManager.GetGeneratorConfig(ConfigType.Image, generatorId);
                if (config == null)
                {
                    // Fallback to package config if runtime cache lacks the new model
                    config = ConfigManager.GetPackageGeneratorConfig(ConfigType.Image, generatorId);
                }

                if (config == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message",
                            $"Cannot find image generator config for '{generatorId}'. " +
                            "Ensure GeneratorConfig.json includes it under imageGenerators, then clear config cache." }
                    };
                }

                var generator = new DynamicGenerator(config);
                generator.SetTextPrompt(prompt);
                generator.SetHistoryDisplayPrompt((prompt ?? "").Trim());
                generator.SetImagePath(absPath);
                if (isSeedream)
                {
                    string size = ParseSeedreamSize(parameters["size"]);
                    if (!string.IsNullOrEmpty(size))
                        generator.SetParameter("size", size);
                }
                else
                {
                    generator.SetParameter("numLayers", numLayers);
                    generator.SetParameter("outputFormat", "png");
                }

                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[GenerateImageLayersTool] 任务提交失败 [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", submitResult.ErrorCode },
                        { "message", submitResult.Message }
                    };
                }

                string placeholderPath = CreatePlaceholderTexture(outputPath);
                string capturedBackendTaskId = submitResult.BackendTaskId;
                string taskId = ImageLayersTaskTracker.CreateTask(
                    generatorId, prompt, imagePath, expectedLayerCount, placeholderPath, capturedBackendTaskId);

                var host = new ImageLayersPipelineHost(
                    placeholderPath,
                    sessionId,
                    expectedLayerCount,
                    taskId,
                    capturedBackendTaskId,
                    errorMsg =>
                    {
                        ImageLayersTaskTracker.MarkTaskFailed(taskId, errorMsg);
                        GenerationNotifier.NotifyFailed(ToolName, taskId, capturedBackendTaskId, errorMsg,
                            new JObject
                            {
                                ["session_id"] = sessionId,
                                ["generator_id"] = generatorId,
                                ["prompt"] = prompt ?? "",
                                ["input_image_path"] = imagePath ?? ""
                            });
                    });

                string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(placeholderPath);
                var pipeline = new GenerationPipeline(host, ConfigType.Image, GenerationRequestOrigin.Agent, sessionId, ToolName);
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId));

                TJLog.Log($"[GenerateImageLayersTool] 轮询已启动，task_id={taskId}, backend_task_id={submitResult.BackendTaskId}");

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "submission_success", true },
                    { "message",
                        "Image layering started. " +
                        "STEP 1 (optional): Apply placeholder_path if you need a preview of layer 0. " +
                        "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                        "STEP 3 (automatic): A <bg_task_done> notification will appear (~90s) with " +
                        "layer_0_path, layers_folder, layer_count, layers_found, and layer_paths. " +
                        "*** POLLING IS STRICTLY FORBIDDEN — only call query_image_layers_status ONCE as fallback. ***" },
                    { "task_id", taskId },
                    { "backend_task_id", submitResult.BackendTaskId },
                    { "status", "submitted" },
                    { "generator_id", generatorId },
                    { "provider", isSeedream ? "seedream_pro" : "qwen" },
                    { "prompt", prompt },
                    { "input_image_path", imagePath },
                    { "auto_layers", isSeedream },
                    { "placeholder_path", placeholderPath },
                    { "estimated_wait_seconds", 90 },
                    { "notification_mode", "bg_task_done" },
                    { "preview_url", PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) }
                };
                // num_layers 仅 qwen 有意义；seedream 自动分层省略该键（避免输出字面 null 干扰按字段存在性判分支）
                if (!isSeedream)
                    result["num_layers"] = numLayers;
                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateImageLayersTool] Error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error generating image layers: {e.Message}" }
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

        [ExecuteCustomTool.CustomTool("query_image_layers_status",
            "Query image-layering task status. Use ONLY as a one-time fallback if no <bg_task_done> arrives. " +
            "When completed, returns layer_0_path, layers_folder, layer_count, and layer_paths. " +
            "WARNING: Do NOT call repeatedly.")]
        public static object QueryImageLayersStatus(JObject parameters)
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

                var task = ImageLayersTaskTracker.GetTask(taskId);
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
                    { "prompt", task.Prompt ?? "" },
                    { "layer_count", task.LayerCount },
                    { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                };

                if (!string.IsNullOrEmpty(task.ImagePath))
                    result["input_image_path"] = task.ImagePath;
                if (!string.IsNullOrEmpty(task.Layer0Path))
                    result["layer_0_path"] = task.Layer0Path;
                if (!string.IsNullOrEmpty(task.LayersFolder))
                    result["layers_folder"] = task.LayersFolder;

                result["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);

                if (!string.IsNullOrEmpty(task.ErrorMessage))
                    result["error"] = task.ErrorMessage;

                if (task.EndTime.HasValue)
                {
                    result["end_time"] = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                    result["duration_seconds"] = (int)(task.EndTime.Value - task.StartTime).TotalSeconds;
                }

                if (task.Status == "generating" || task.Status == "recovering")
                {
                    if (!string.IsNullOrEmpty(task.PlaceholderPath))
                        result["placeholder_path"] = task.PlaceholderPath;
                }

                if (task.Status == "completed" && !string.IsNullOrEmpty(task.Layer0Path))
                {
                    var layerPaths = CollectLayerPaths(task.Layer0Path, task.LayerCount);
                    result["layer_paths"] = layerPaths;
                    result["layers_found"] = layerPaths.Count;
                }

                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateImageLayersTool] Query error: {e}");
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

        [ExecuteCustomTool.CustomTool("list_image_layers_tasks", "List all active and recent image-layering tasks")]
        public static object ListImageLayersTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                ImageLayersTaskTracker.CleanupCompletedTasks();
                var tasks = ImageLayersTaskTracker.GetAllTasks();
                var taskList = new List<Dictionary<string, object>>();

                foreach (var task in tasks)
                {
                    var taskData = new Dictionary<string, object>
                    {
                        { "task_id", task.TaskId },
                        { "generator_id", task.GeneratorId },
                        { "status", task.Status },
                        { "progress", task.Progress },
                        { "prompt", task.Prompt ?? "" },
                        { "layer_count", task.LayerCount },
                        { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                    };

                    if (!string.IsNullOrEmpty(task.ImagePath))
                        taskData["input_image_path"] = task.ImagePath;
                    if (!string.IsNullOrEmpty(task.Layer0Path))
                        taskData["layer_0_path"] = task.Layer0Path;
                    if (!string.IsNullOrEmpty(task.LayersFolder))
                        taskData["layers_folder"] = task.LayersFolder;

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
                TJLog.LogError($"[GenerateImageLayersTool] List error: {e}");
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
        /// Parse num_layers from tool params. Invalid / float strings keep default 4 (not clamp-to-1).
        /// </summary>
        internal static int ParseNumLayers(JToken token, int defaultValue = 4)
        {
            if (token == null || token.Type == JTokenType.Null)
                return Mathf.Clamp(defaultValue, 1, 8);

            try
            {
                if (token.Type == JTokenType.Integer)
                    return Mathf.Clamp(token.Value<int>(), 1, 8);
                if (token.Type == JTokenType.Float)
                    return Mathf.Clamp((int)token.Value<double>(), 1, 8);
            }
            catch
            {
                // fall through to string parse
            }

            string raw = token.ToString();
            if (string.IsNullOrWhiteSpace(raw))
                return Mathf.Clamp(defaultValue, 1, 8);

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int asInt))
                return Mathf.Clamp(asInt, 1, 8);

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double asDouble))
                return Mathf.Clamp((int)asDouble, 1, 8);

            TJLog.LogWarning($"[GenerateImageLayersTool] Invalid num_layers '{raw}', using default: {defaultValue}");
            return Mathf.Clamp(defaultValue, 1, 8);
        }

        /// <summary>
        /// Parse seedream_pro size tier from tool params: 1K / 1.5K / 2K；空或 auto 返回空串（跟随输入图）。
        /// </summary>
        internal static string ParseSeedreamSize(JToken token)
        {
            string raw = token?.ToString()?.Trim();
            if (string.IsNullOrEmpty(raw) || string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase))
                return "";
            if (raw == "1K" || raw == "1.5K" || raw == "2K")
                return raw;
            TJLog.LogWarning($"[GenerateImageLayersTool] Invalid size '{raw}', using auto.");
            return "";
        }

        private static string ResolveImagePath(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath))
                return null;
            if (Path.IsPathRooted(imagePath))
                return File.Exists(imagePath) ? imagePath : null;
            if (imagePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || imagePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                string abs = PathUtils.ToAbsoluteAssetPath(imagePath.Replace("\\", "/"));
                return File.Exists(abs) ? abs : null;
            }

            string fallback = Path.Combine(Application.dataPath, imagePath).Replace("\\", "/");
            return File.Exists(fallback) ? fallback : null;
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
                string uniqueName = "ImageLayers_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                placeholderPath = AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/History/" + uniqueName);
            }

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1f));
            tex.Apply();
            byte[] pngBytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            string absolutePath = PathUtils.ToAbsoluteAssetPath(placeholderPath);
            File.WriteAllBytes(absolutePath, pngBytes);
            PathUtils.ImportAssetAfterDiskWrite(placeholderPath);
            return placeholderPath;
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
        /// Collect layer_0 + {basename}_1 … paths that exist on disk / in AssetDatabase.
        /// </summary>
        internal static List<string> CollectLayerPaths(string layer0Path, int expectedCount) =>
            GeneratedTextureImportUtils.CollectIndexedSiblingPaths(layer0Path, expectedCount);

        internal static void FinalizeLayersAndNotify(
            string taskId,
            string backendTaskId,
            string sessionId,
            string layer0Path,
            int requestedLayerCount,
            string previewUrl,
            string toolName = null)
        {
            if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(layer0Path))
                return;

            var trackerTask = ImageLayersTaskTracker.GetTask(taskId);
            if (trackerTask != null
                && (trackerTask.Status == "completed" || trackerTask.Status == "failed"))
                return;

            int expected = requestedLayerCount > 0
                ? requestedLayerCount
                : (trackerTask?.LayerCount > 0 ? trackerTask.LayerCount : 4);

            var layerPaths = CollectLayerPaths(layer0Path, expected);
            GeneratedTextureImportUtils.ConfigureLayerTextures(layerPaths);
            for (int i = 0; i < layerPaths.Count; i++)
            {
                string path = layerPaths[i];
                if (string.IsNullOrEmpty(path))
                    continue;
                TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));
                TJGeneratorsGenerationLabel.EnableSessionLabel(
                    TJGeneratorsAssetReference.FromPath(path), sessionId);
            }

            string layersFolder = Path.GetDirectoryName(layer0Path)?.Replace('\\', '/') ?? "";
            string effectivePreview = PreviewUrlHelper.GetPreviewUrl(previewUrl, backendTaskId);
            ImageLayersTaskTracker.MarkTaskCompleted(taskId, layer0Path, layersFolder, effectivePreview);
            var t = ImageLayersTaskTracker.GetTask(taskId);

            var layerPathsToken = new JArray();
            foreach (string p in layerPaths)
                layerPathsToken.Add(p ?? "");

            GenerationNotifier.NotifyCompleted(
                string.IsNullOrEmpty(toolName) ? ToolName : toolName,
                taskId,
                backendTaskId,
                new JObject
                {
                    ["session_id"] = sessionId ?? "",
                    ["generator_id"] = t?.GeneratorId ?? GeneratorId,
                    ["prompt"] = t?.Prompt ?? "",
                    ["input_image_path"] = t?.ImagePath ?? "",
                    ["layer_0_path"] = layer0Path ?? "",
                    ["layers_folder"] = layersFolder,
                    ["layer_count"] = expected,
                    ["layers_found"] = layerPaths.Count,
                    ["layer_paths"] = layerPathsToken,
                    ["preview_url"] = effectivePreview ?? "",
                    ["progress"] = 100,
                    ["start_time"] = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                    ["end_time"] = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                    ["duration_seconds"] = (t != null && t.EndTime.HasValue)
                        ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds
                        : 0
                });
        }
#endif
    }

#if UNITY_EDITOR
    [InitializeOnLoad]
    public static class ImageLayersDomainReloadRecovery
    {
        static ImageLayersDomainReloadRecovery()
        {
            CustomToolDomainReloadRecovery.Schedule(ResumeInterruptedTasks);
        }

        private static void ResumeInterruptedTasks()
        {
            CustomToolDomainReloadRecovery.Resume(
                "GenerateImageLayersTool",
                ConfigType.Image,
                // generate_game_ui_kit 的 Step 2（seedream 图层拆分）也走本 tracker/host，一并恢复
                t => t.toolName == "generate_image_layers" || t.toolName == "generate_game_ui_kit",
                () => ImageLayersTaskTracker.GetAllTasks(),
                (interrupted, _, generator) =>
                {
                    var trackerTask = ImageLayersTaskTracker.GetTaskByBackendId(interrupted.backendTaskId);
                    if (trackerTask != null)
                    {
                        CustomToolDomainReloadRecovery.MarkTrackerRecoveringIfNeeded(trackerTask.Status, () =>
                        {
                            ImageLayersTaskTracker.ApplyTaskUpdate(trackerTask, t => t.Status = "recovering");
                        });
                    }
                    else
                    {
                        string placeholderPath = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);
                        int recoveredLayers = interrupted.numLayers > 0 ? interrupted.numLayers : 4;
                        trackerTask = ImageLayersTaskTracker.CreateRecoveredTask(
                            interrupted.backendTaskId,
                            interrupted.prompt,
                            placeholderPath,
                            interrupted.timestamp,
                            layerCount: recoveredLayers,
                            generatorId: interrupted.modelVersion,
                            imagePath: interrupted.imagePath);
                    }

                    string placeholderPathForHost = trackerTask.PlaceholderPath ?? "";
                    if (string.IsNullOrEmpty(placeholderPathForHost))
                        placeholderPathForHost = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);

                    int layerCount = trackerTask.LayerCount > 0
                        ? trackerTask.LayerCount
                        : (interrupted.numLayers > 0 ? interrupted.numLayers : 4);
                    var host = new ImageLayersRecoveryHost(
                        placeholderPathForHost,
                        interrupted.backendTaskId,
                        interrupted.sessionId,
                        layerCount,
                        generator,
                        interrupted.toolName);
                    CustomToolDomainReloadRecovery.StartPolling(
                        "GenerateImageLayersTool", host, ConfigType.Image,
                        interrupted.sessionId, interrupted.toolName, generator, interrupted.backendTaskId);
                });
        }
    }

    internal class ImageLayersRecoveryHost : HeadlessPipelineHostBase, IMediaAssetPipelineHost
    {
        private readonly string _placeholderPath;
        private readonly TJGeneratorsAssetReference _placeholderRef;
        private readonly string _backendTaskId;
        private readonly string _sessionId;
        private readonly int _layerCount;
        private readonly ModelGeneratorBase _generator;
        private readonly string _toolName;
        private string _layer0Path;
        private string _previewUrl;

        public ImageLayersRecoveryHost(
            string placeholderPath,
            string backendTaskId,
            string sessionId,
            int layerCount,
            ModelGeneratorBase generator,
            string toolName = null)
        {
            _placeholderPath = placeholderPath ?? "";
            _placeholderRef = string.IsNullOrEmpty(_placeholderPath)
                ? null
                : TJGeneratorsAssetReference.FromPath(_placeholderPath);
            _backendTaskId = backendTaskId;
            _sessionId = sessionId ?? "";
            _layerCount = layerCount > 0 ? layerCount : 4;
            _generator = generator;
            _toolName = toolName;
        }

        protected override string DialogLogTag => "ImageLayersRecovery";

        public override TJGeneratorsAssetReference GetTargetAsset() => _placeholderRef;

        public override void Repaint()
        {
            if (_generator == null) return;
            var trackerTask = ImageLayersTaskTracker.GetTaskByBackendId(_backendTaskId);
            if (trackerTask == null || !TJGeneratorsTaskRecovery.IsRecoverableTrackerStatus(trackerTask.Status)) return;

            int progress = _generator.CurrentProgress;
            if (progress <= trackerTask.Progress) return;

            ImageLayersTaskTracker.ApplyTaskUpdate(trackerTask, t =>
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
                var trackerTask = ImageLayersTaskTracker.GetTaskByBackendId(_backendTaskId);
                if (trackerTask != null)
                {
                    var friendlyError = ErrorDialogUtils.ConvertToUserFriendlyError(title, message);
                    ImageLayersTaskTracker.MarkTaskFailed(trackerTask.TaskId, friendlyError.TechnicalMessage);
                    GenerationNotifier.NotifyFailed(
                        "generate_image_layers",
                        trackerTask.TaskId,
                        _backendTaskId,
                        friendlyError.TechnicalMessage,
                        new JObject
                        {
                            ["session_id"] = _sessionId,
                            ["generator_id"] = trackerTask.GeneratorId ?? "",
                            ["prompt"] = trackerTask.Prompt ?? "",
                            ["input_image_path"] = trackerTask.ImagePath ?? ""
                        });
                }
            }
        }

        public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator) =>
            _type == PipelineMediaType.Texture ? _placeholderPath : null;

        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return;

            _layer0Path = savePath;
            if (!string.IsNullOrEmpty(generator?.CurrentPreviewUrl))
                _previewUrl = generator.CurrentPreviewUrl;

            GeneratedTextureImportUtils.ConfigureImportedTexture(
                savePath, TextureImporterType.Default, alphaIsTransparency: true);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));
            TJGeneratorsGenerationLabel.EnableSessionLabel(TJGeneratorsAssetReference.FromPath(savePath), _sessionId);
        }

        public override void OnGenerationCompleted(string assetPath)
        {
            string layer0 = !string.IsNullOrEmpty(_layer0Path) ? _layer0Path : assetPath;
            var trackerTask = ImageLayersTaskTracker.GetTaskByBackendId(_backendTaskId);
            if (trackerTask == null) return;

            int expected = trackerTask.LayerCount > 0 ? trackerTask.LayerCount : _layerCount;
            string preview = !string.IsNullOrEmpty(_previewUrl)
                ? _previewUrl
                : (_generator?.CurrentPreviewUrl ?? "");
            GenerateImageLayersTool.FinalizeLayersAndNotify(
                trackerTask.TaskId,
                _backendTaskId,
                _sessionId,
                layer0,
                expected,
                preview,
                _toolName);
        }
    }

    internal class ImageLayersPipelineHost : HeadlessPipelineHostBase, IMediaAssetPipelineHost
    {
        private readonly string _placeholderPath;
        private readonly TJGeneratorsAssetReference _placeholderRef;
        private readonly string _sessionId;
        private readonly int _layerCount;
        private readonly string _taskId;
        private readonly string _backendTaskId;
        private readonly Action<string> _onFailed;
        private readonly string _toolName;
        private string _layer0Path;
        private string _previewUrl;

        public ImageLayersPipelineHost(
            string placeholderPath,
            string sessionId,
            int layerCount,
            string taskId,
            string backendTaskId,
            Action<string> onFailed,
            string toolName = null)
        {
            _placeholderPath = placeholderPath;
            _placeholderRef = TJGeneratorsAssetReference.FromPath(placeholderPath);
            _sessionId = sessionId ?? "";
            _layerCount = layerCount > 0 ? layerCount : 4;
            _taskId = taskId;
            _backendTaskId = backendTaskId;
            _onFailed = onFailed;
            _toolName = toolName;
        }

        protected override string DialogLogTag => "GenerateImageLayersTool";
        protected override Action<string> DialogFailedCallback => errorMessage => _onFailed?.Invoke(errorMessage);

        public override TJGeneratorsAssetReference GetTargetAsset() => _placeholderRef;

        public void StartEditorCoroutine(IEnumerator coroutine)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(coroutine);
        }

        public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator) =>
            _type == PipelineMediaType.Texture ? _placeholderPath : null;

        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return;

            _layer0Path = savePath;
            if (!string.IsNullOrEmpty(generator?.CurrentPreviewUrl))
                _previewUrl = generator.CurrentPreviewUrl;

            TJLog.Log($"[GenerateImageLayersTool] Layer 0 saved: {savePath} (expected {_layerCount} layers; waiting for remaining downloads)");

            GeneratedTextureImportUtils.ConfigureImportedTexture(
                savePath, TextureImporterType.Default, alphaIsTransparency: true);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));
            TJGeneratorsGenerationLabel.EnableSessionLabel(TJGeneratorsAssetReference.FromPath(savePath), _sessionId);
        }

        public override void OnGenerationCompleted(string assetPath)
        {
            string layer0 = !string.IsNullOrEmpty(_layer0Path) ? _layer0Path : assetPath;
            GenerateImageLayersTool.FinalizeLayersAndNotify(
                _taskId,
                _backendTaskId,
                _sessionId,
                layer0,
                _layerCount,
                _previewUrl,
                _toolName);
        }
    }
#endif
}
