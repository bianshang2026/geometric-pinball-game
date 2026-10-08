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
    /// Tracks active skybox generation tasks
    /// </summary>
    public static class SkyboxTaskTracker
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
            public string texturePath;
            public string errorMessage;
            public long   startTimeTicks;
            public long   endTimeTicks;
            public string previewUrl;
            public string placeholderPath;
            public string placeholderMaterialPath;
            public string backendTaskId;
        }

        public class SkyboxTaskInfo : IGenerationTaskInfo
        {
            public string TaskId { get; set; }
            public string GeneratorId { get; set; }
            public string Prompt { get; set; }
            public string ImagePath { get; set; }
            public string Status { get; set; }
            public int Progress { get; set; }
            public string TexturePath { get; set; }
            public string ErrorMessage { get; set; }
            public string PreviewUrl { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public string PlaceholderPath { get; set; }
            public string PlaceholderMaterialPath { get; set; }
            public string BackendTaskId { get; set; }
        }

        private static readonly GenerationTaskTrackerStore<SkyboxTaskInfo, PersistedTask> Store =
            new GenerationTaskTrackerStore<SkyboxTaskInfo, PersistedTask>(
                "TJGen_Skybox", BuildPersisted, FromPersisted);

        private static PersistedTask BuildPersisted(SkyboxTaskInfo info) => new PersistedTask
        {
            taskId                  = info.TaskId,
            generatorId             = info.GeneratorId,
            prompt                  = info.Prompt ?? "",
            imagePath               = info.ImagePath ?? "",
            status                  = info.Status,
            progress                = info.Progress,
            texturePath             = info.TexturePath ?? "",
            errorMessage            = info.ErrorMessage ?? "",
            startTimeTicks          = info.StartTime.Ticks,
            endTimeTicks            = info.EndTime?.Ticks ?? 0,
            previewUrl              = info.PreviewUrl ?? "",
            placeholderPath         = info.PlaceholderPath ?? "",
            placeholderMaterialPath = info.PlaceholderMaterialPath ?? "",
            backendTaskId           = info.BackendTaskId ?? ""
        };

        private static SkyboxTaskInfo FromPersisted(PersistedTask p) => new SkyboxTaskInfo
        {
            TaskId                  = p.taskId,
            GeneratorId             = p.generatorId,
            Prompt                  = p.prompt,
            ImagePath               = p.imagePath,
            Status                  = p.status,
            Progress                = p.progress,
            TexturePath             = p.texturePath,
            ErrorMessage            = p.errorMessage,
            PreviewUrl              = p.previewUrl,
            StartTime               = new DateTime(p.startTimeTicks),
            EndTime                 = p.endTimeTicks > 0 ? (DateTime?)new DateTime(p.endTimeTicks) : null,
            PlaceholderPath         = p.placeholderPath,
            PlaceholderMaterialPath = p.placeholderMaterialPath,
            BackendTaskId           = p.backendTaskId
        };

        internal static void ApplyTaskUpdate(SkyboxTaskInfo task, Action<SkyboxTaskInfo> mutate) =>
            Store.ApplyTaskUpdate(task, mutate);

        public static string CreateTask(string generatorId, string prompt, string imagePath, string placeholderPath, string backendTaskId = null)
        {
            string taskId = Store.AllocateTaskId("skybox");
            var taskInfo = new SkyboxTaskInfo
            {
                TaskId                  = taskId,
                GeneratorId             = generatorId,
                Prompt                  = prompt ?? "",
                ImagePath               = imagePath ?? "",
                Status                  = "generating",
                StartTime               = DateTime.Now,
                PlaceholderPath         = placeholderPath,
                PlaceholderMaterialPath = DeriveMaterialPath(placeholderPath),
                BackendTaskId           = backendTaskId
            };
            Store.RegisterTask(taskId, taskInfo);
            return taskId;
        }

        public static void MarkCompleted(string taskId, string texturePath, string previewUrl = null)
        {
            var task = Store.GetTask(taskId);
            if (task == null) return;
            Store.ApplyTaskUpdate(task, t =>
            {
                t.Status      = "completed";
                t.Progress    = 100;
                t.TexturePath = texturePath;
                t.PreviewUrl  = previewUrl;
                t.EndTime     = DateTime.Now;
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

        public static SkyboxTaskInfo GetTask(string taskId) => Store.GetTask(taskId);

        public static List<SkyboxTaskInfo> GetAllTasks() => Store.GetAllTasks();

        public static SkyboxTaskInfo GetTaskByBackendId(string backendTaskId) =>
            Store.GetTaskByBackendId(backendTaskId);

        public static SkyboxTaskInfo CreateRecoveredTask(
            string backendTaskId, string prompt, string placeholderPath, long timestampMs, string generatorId = null, string imagePath = null)
        {
            return Store.CreateRecoveredTask(backendTaskId, () => new SkyboxTaskInfo
            {
                TaskId                  = $"recovered_{backendTaskId}",
                BackendTaskId           = backendTaskId,
                GeneratorId             = generatorId ?? "",
                Prompt                  = prompt ?? "",
                ImagePath               = imagePath ?? "",
                PlaceholderPath         = placeholderPath ?? "",
                PlaceholderMaterialPath = DeriveMaterialPath(placeholderPath ?? ""),
                Status                  = "recovering",
                Progress                = 0,
                StartTime               = timestampMs > 0
                                            ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).LocalDateTime
                                            : DateTime.Now
            });
        }

        public static void RemoveTask(string taskId) => Store.RemoveTask(taskId);

        public static void CleanupCompletedTasks() => Store.CleanupCompletedTasks();

        public static string DeriveMaterialPath(string placeholderPath)
        {
            if (string.IsNullOrEmpty(placeholderPath))
                return "";

            string dir = Path.GetDirectoryName(placeholderPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir))
                dir = "Assets";
            return $"{dir}/{Path.GetFileNameWithoutExtension(placeholderPath)}_material.mat";
        }
#endif
    }

    /// <summary>
    /// CustomTool for generating skyboxes using TJGenerators Skybox pipeline.
    /// Supports text-to-skybox and image-to-skybox generation via Rodin Skybox.
    /// Output is a PNG imported as a Cubemap texture asset saved to Assets/TJGenerators/History/.
    /// </summary>
    public static class GenerateSkyboxTool
    {
        [ExecuteCustomTool.CustomTool("generate_skybox",
            "Generate a skybox (Cubemap) from a text prompt or reference image using AI. " +
            "The output is a PNG imported as a Cubemap texture saved to Assets/TJGenerators/History/. " +
            "Parameters: generator_id (optional, default 'rodin-skybox'), prompt (text description), " +
            "image_path (reference image path, optional), output_path (optional asset save path). " +
            "IMPORTANT ASYNC WORKFLOW: " +
            "(1) Call this tool to start the generation task. Returns task_id and placeholder assets immediately. " +
            "(2) Apply placeholder assets to the scene using the `place_assets_in_scene` skill. " +
            "(3) END YOUR CURRENT RESPONSE TURN immediately after step 2. " +
            "    *** POLLING IS STRICTLY FORBIDDEN. NEVER call `query_skybox_status` in a loop or repeatedly. *** " +
            "    Only call `query_skybox_status` ONCE as a last-resort fallback if no notification arrives after the estimated wait time. " +
            "(4) A <bg_task_done> notification will arrive automatically in your next turn when generation finishes. " +
            "    The notification payload contains ALL the same fields as `query_skybox_status` (texture_path, material_path, preview_url, generator_id, prompt, start_time, end_time, duration_seconds, etc.). " +
            "    If you receive the notification, the task is done — do NOT call `query_skybox_status` under any circumstances. " +
            "    Note: automatic notification requires a compatible CLI client version; if no notification arrives, call `query_skybox_status` ONCE only.")]
        public static object GenerateSkybox(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateSkyboxTool] Generating skybox with parameters: {parameters}");

                string generatorId = parameters["generator_id"]?.ToString() ?? "rodin-skybox";
                string prompt = parameters["prompt"]?.ToString();
                string imagePath = parameters["image_path"]?.ToString();
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

                // Validate/normalize output_path before submit so bad paths never leave orphan backend tasks.
                string normalizedOutputPath = null;
                if (!string.IsNullOrEmpty(outputPath))
                {
                    if (!PathUtils.TryNormalizeOutputAssetPath(outputPath, out normalizedOutputPath, out string pathError))
                    {
                        return new Dictionary<string, object>
                        {
                            { "success", false },
                            { "error_code", "INVALID_PARAMS" },
                            { "message", pathError }
                        };
                    }
                }

                // Load skybox generator config
                var config = ConfigManager.GetGeneratorConfig(ConfigType.Skybox, generatorId);
                if (config == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Cannot find skybox generator config for '{generatorId}'. Valid value: 'rodin-skybox'." }
                    };
                }

                // Create generator and set inputs
                var generator = new DynamicGenerator(config);

                if (!string.IsNullOrEmpty(prompt))
                    generator.SetTextPrompt(prompt);

                if (!string.IsNullOrEmpty(imagePath))
                    generator.SetImagePath(imagePath);

                // Apply optional parameters
                ApplySkyboxParameters(generator, generatorId, parameters);

                // 阶段1：同步提交任务到后端，立即获取 backendTaskId 或失败原因
                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[GenerateSkyboxTool] 任务提交失败 [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[GenerateSkyboxTool] 任务提交成功，backend_task_id={submitResult.BackendTaskId}");

                // 提交成功后才创建 placeholder（避免在鉴权失败时留下无用文件）
                string placeholderPath = CreatePlaceholderAssets(normalizedOutputPath);

                // Create tracked task (before starting so we have the taskId for the callback)
                string capturedBackendTaskId = submitResult.BackendTaskId;
                string taskId = SkyboxTaskTracker.CreateTask(generatorId, prompt, imagePath, placeholderPath, capturedBackendTaskId);
                string derivedMaterialPath = SkyboxTaskTracker.DeriveMaterialPath(placeholderPath);

                // Create pipeline host that handles texture saving and updates the tracker
                var host = new SkyboxPipelineHost(placeholderPath, sessionId,
                    (savedPath, previewUrl) =>
                    {
                        SkyboxTaskTracker.MarkCompleted(taskId, savedPath, previewUrl);
                        // Read the completed task to include timing fields in the notification,
                        // mirroring the full field set returned by query_skybox_status.
                        var t = SkyboxTaskTracker.GetTask(taskId);
                        GenerationNotifier.NotifyCompleted("generate_skybox", taskId, capturedBackendTaskId,
                            new JObject
                            {
                                ["session_id"]       = sessionId,
                                ["generator_id"]     = generatorId,
                                ["prompt"]           = prompt ?? "",
                                ["image_path"]       = imagePath ?? "",
                                ["texture_path"]     = savedPath,
                                ["material_path"]    = derivedMaterialPath,
                                ["preview_url"]      = previewUrl ?? "",
                                ["progress"]         = 100,
                                ["start_time"]       = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["end_time"]         = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["duration_seconds"] = (t != null && t.EndTime.HasValue)
                                    ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds
                                    : 0
                            });
                    },
                    errorMsg =>
                    {
                        SkyboxTaskTracker.MarkFailed(taskId, errorMsg);
                        GenerationNotifier.NotifyFailed("generate_skybox", taskId, capturedBackendTaskId, errorMsg,
                            new JObject
                            {
                                ["session_id"]  = sessionId,
                                ["generator_id"] = generatorId,
                                ["prompt"]      = prompt ?? ""
                            });
                    });

                // 阶段2：异步轮询（跳过提交）
                var pipeline = new GenerationPipeline(host, ConfigType.Skybox, GenerationRequestOrigin.Agent, sessionId, "generate_skybox");
                string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(placeholderPath);
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId));

                TJLog.Log($"[GenerateSkyboxTool] 轮询已启动，task_id={taskId}, backend_task_id={submitResult.BackendTaskId}, placeholder: {placeholderPath}");

                return new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "message",
                        "Skybox generation started. " +
                        "STEP 1 (do now): Use `place_assets_in_scene` skill to apply placeholder_material_path to the scene skybox. " +
                        "STEP 2 (critical): END THIS RESPONSE TURN immediately after applying the placeholder. " +
                        "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn (~90s) " +
                        "containing ALL generation results (texture_path, material_path, preview_url, timing, etc.). " +
                        "*** POLLING IS STRICTLY FORBIDDEN — do NOT call query_skybox_status repeatedly. " +
                        "Only call query_skybox_status ONCE as a last-resort fallback if no notification arrives. ***" },
                    { "task_id",            taskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       generatorId },
                    { "prompt",             prompt ?? "" },
                    { "image_path",         imagePath ?? "" },
                    { "placeholder_path",   placeholderPath },
                    { "placeholder_material_path", SkyboxTaskTracker.DeriveMaterialPath(placeholderPath) },
                    { "preview_url",        PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "estimated_wait_seconds", 90 },
                    { "notification_mode",  "bg_task_done" },
                    { "next_action",        "Apply placeholder NOW, then END YOUR TURN. Wait for <bg_task_done> notification with final texture_path." }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateSkyboxTool] Error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error generating skybox: {e.Message}" }
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

        [ExecuteCustomTool.CustomTool("query_skybox_status",
            "Query the status of a skybox generation task. Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "Status values: 'generating', 'recovering', 'completed', 'failed', 'interrupted'. " +
            "When completed, returns 'texture_path' with the Cubemap asset path in the project. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QuerySkyboxStatus(JObject parameters)
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

                var task = SkyboxTaskTracker.GetTask(taskId);

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
                    { "image_path", task.ImagePath },
                    { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                };

                if (!string.IsNullOrEmpty(task.TexturePath))
                    result["texture_path"] = task.TexturePath;

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
                    if (!string.IsNullOrEmpty(task.PlaceholderMaterialPath))
                        result["placeholder_material_path"] = task.PlaceholderMaterialPath;
                }

                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateSkyboxTool] Query error: {e}");
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

        [ExecuteCustomTool.CustomTool("list_skybox_tasks", "List all active and recent skybox generation tasks")]
        public static object ListSkyboxTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks = SkyboxTaskTracker.GetAllTasks();
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

                    if (!string.IsNullOrEmpty(task.TexturePath))
                        taskData["texture_path"] = task.TexturePath;

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
                TJLog.LogError($"[GenerateSkyboxTool] List error: {e}");
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
        /// <param name="outputPath">
        /// Already-normalized Assets-relative path from <see cref="PathUtils.TryNormalizeOutputAssetPath"/>, or null/empty for default History path.
        /// </param>
        private static string CreatePlaceholderAssets(string outputPath)
        {
            const string defaultHistoryPath = "Assets/TJGenerators/History/Skybox.png";
            string placeholderPath;
            if (!string.IsNullOrEmpty(outputPath))
            {
                string preferred = Path.ChangeExtension(outputPath, ".png").Replace('\\', '/');
                string dir = Path.GetDirectoryName(preferred)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir))
                    PathUtils.EnsureAssetFolder(dir);
                placeholderPath = PathUtils.GenerateUniqueAssetPathChecked(preferred, defaultHistoryPath);
            }
            else
            {
                PathUtils.EnsureAssetFolder("Assets/TJGenerators/History");
                string uniqueName = "Skybox_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                placeholderPath = PathUtils.GenerateUniqueAssetPathChecked(
                    "Assets/TJGenerators/History/" + uniqueName, defaultHistoryPath);
            }

            // Create 1x1 gray placeholder PNG
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1f));
            tex.Apply();
            byte[] pngBytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            string absolutePath = Path.GetFullPath(PathUtils.ToAbsoluteAssetPath(placeholderPath));
            if (!PathUtils.IsAbsolutePathUnderAssets(absolutePath))
                throw new InvalidOperationException(
                    $"Refusing to write skybox placeholder outside project Assets: '{placeholderPath}'");

            string parentDir = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);
            File.WriteAllBytes(absolutePath, pngBytes);
            PathUtils.ImportAssetAfterDiskWrite(placeholderPath);

            // Configure as Cubemap so AI Agent can load it immediately
            var importer = AssetImporter.GetAtPath(placeholderPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.textureShape = TextureImporterShape.TextureCube;
                importer.SaveAndReimport();
            }

            // Create placeholder skybox material using Skybox/Cubemap shader.
            // Keep exact DeriveMaterialPath so tracker / notifications stay in sync with the created .mat.
            string matPath = SkyboxTaskTracker.DeriveMaterialPath(placeholderPath);
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(matPath) != null)
            {
                TJLog.LogWarning(
                    $"[GenerateSkyboxTool] Replacing existing skybox material at '{matPath}' so placeholder material matches tracker path.");
                AssetDatabase.DeleteAsset(matPath);
            }

            var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(placeholderPath);
            var shader = Shader.Find("Skybox/Cubemap") ?? Shader.Find("Skybox/6 Sided");
            if (shader != null)
            {
                var mat = new Material(shader);
                if (cubemap != null)
                    mat.SetTexture("_Tex", cubemap);
                AssetDatabase.CreateAsset(mat, matPath);
                AssetDatabase.SaveAssets();
            }

            return placeholderPath;
        }

        private static void ApplySkyboxParameters(DynamicGenerator generator, string generatorId, JObject parameters)
        {
            // Rodin Skybox parameters
            if (generatorId == "rodin-skybox")
            {
                if (parameters["resolution"] != null)
                    generator.SetParameter("resolution", parameters["resolution"].ToString());

                if (parameters["high_res"] != null)
                    generator.SetParameter("highRes", parameters["high_res"].ToObject<bool>());
            }
        }

        /// <summary>
        /// Restore prompt/image after domain reload. Session tracker takes precedence.
        /// </summary>
        internal static void ApplySkyboxRecoveryGeneratorSettings(
            DynamicGenerator generator, InterruptedTaskData interrupted, SkyboxTaskTracker.SkyboxTaskInfo trackerTask = null)
        {
            if (generator == null || interrupted == null) return;

            string prompt = !string.IsNullOrEmpty(trackerTask?.Prompt) ? trackerTask.Prompt : interrupted.prompt;
            string imagePath = !string.IsNullOrEmpty(trackerTask?.ImagePath) ? trackerTask.ImagePath : interrupted.imagePath;

            if (!string.IsNullOrEmpty(prompt))
                generator.SetTextPrompt(prompt);
            if (!string.IsNullOrEmpty(imagePath))
                generator.SetImagePath(imagePath);
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>
    /// Automatically resumes interrupted generate_skybox tasks after domain reload.
    /// </summary>
    [InitializeOnLoad]
    public static class SkyboxDomainReloadRecovery
    {
        static SkyboxDomainReloadRecovery()
        {
            CustomToolDomainReloadRecovery.Schedule(ResumeInterruptedTasks);
        }

        private static void ResumeInterruptedTasks()
        {
            CustomToolDomainReloadRecovery.Resume(
                "GenerateSkyboxTool",
                ConfigType.Skybox,
                t => t.toolName == "generate_skybox",
                () => SkyboxTaskTracker.GetAllTasks(),
                (interrupted, _, generator) =>
                {
                    var trackerTask = SkyboxTaskTracker.GetTaskByBackendId(interrupted.backendTaskId);
                    if (trackerTask != null)
                    {
                        CustomToolDomainReloadRecovery.MarkTrackerRecoveringIfNeeded(trackerTask.Status, () =>
                        {
                            SkyboxTaskTracker.ApplyTaskUpdate(trackerTask, t => t.Status = "recovering");
                        });
                    }
                    else
                    {
                        string placeholderPath = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);
                        trackerTask = SkyboxTaskTracker.CreateRecoveredTask(
                            interrupted.backendTaskId, interrupted.prompt, placeholderPath, interrupted.timestamp,
                            interrupted.modelVersion, interrupted.imagePath);
                    }

                    string placeholderPathForHost = trackerTask.PlaceholderPath ?? "";
                    if (string.IsNullOrEmpty(placeholderPathForHost))
                        placeholderPathForHost = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);

                    string derivedMaterialPath = trackerTask.PlaceholderMaterialPath
                        ?? SkyboxTaskTracker.DeriveMaterialPath(placeholderPathForHost);

                    GenerateSkyboxTool.ApplySkyboxRecoveryGeneratorSettings(generator, interrupted, trackerTask);

                    string sessionId = interrupted.sessionId ?? "";
                    string capturedBackendTaskId = interrupted.backendTaskId;
                    string taskId = trackerTask.TaskId;

                    var host = new SkyboxPipelineHost(
                        placeholderPathForHost,
                        sessionId,
                        (savedPath, previewUrl) =>
                        {
                            SkyboxTaskTracker.MarkCompleted(taskId, savedPath, previewUrl);
                            var t = SkyboxTaskTracker.GetTask(taskId);
                            GenerationNotifier.NotifyCompleted("generate_skybox", taskId, capturedBackendTaskId,
                                new JObject
                                {
                                    ["session_id"]       = sessionId,
                                    ["generator_id"]     = t?.GeneratorId ?? interrupted.modelVersion ?? "",
                                    ["prompt"]           = t?.Prompt ?? interrupted.prompt ?? "",
                                    ["image_path"]       = t?.ImagePath ?? interrupted.imagePath ?? "",
                                    ["texture_path"]     = savedPath,
                                    ["material_path"]    = derivedMaterialPath,
                                    ["preview_url"]      = previewUrl ?? "",
                                    ["progress"]         = 100,
                                    ["start_time"]       = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                    ["end_time"]         = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                    ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
                                });
                        },
                        errorMsg =>
                        {
                            SkyboxTaskTracker.MarkFailed(taskId, errorMsg);
                            GenerationNotifier.NotifyFailed("generate_skybox", taskId, capturedBackendTaskId, errorMsg,
                                new JObject
                                {
                                    ["session_id"]   = sessionId,
                                    ["generator_id"] = trackerTask.GeneratorId ?? interrupted.modelVersion ?? "",
                                    ["prompt"]       = trackerTask.Prompt ?? interrupted.prompt ?? ""
                                });
                        });

                    CustomToolDomainReloadRecovery.StartPolling(
                        "GenerateSkyboxTool", host, ConfigType.Skybox,
                        sessionId, "generate_skybox", generator, interrupted.backendTaskId);
                });
        }
    }

    /// <summary>
    /// IGenerationPipelineHost implementation for headless skybox generation via custom tools.
    /// Handles texture saving, Cubemap import settings, and task lifecycle callbacks.
    /// </summary>
    internal class SkyboxPipelineHost : HeadlessPipelineHostBase, IMediaAssetPipelineHost
    {
        private readonly string _placeholderPath;
        private readonly string _placeholderMaterialPath;
        private readonly TJGeneratorsAssetReference _placeholderRef;
        private readonly string _sessionId;
        private readonly Action<string, string> _onCompleted;
        private readonly Action<string> _onFailed;

        public SkyboxPipelineHost(string placeholderPath, string sessionId, Action<string, string> onCompleted, Action<string> onFailed)
        {
            _placeholderPath = placeholderPath;
            _placeholderMaterialPath = SkyboxTaskTracker.DeriveMaterialPath(placeholderPath);
            _placeholderRef = TJGeneratorsAssetReference.FromPath(placeholderPath);
            _sessionId = sessionId ?? "";
            _onCompleted = onCompleted;
            _onFailed = onFailed;
        }

        protected override string DialogLogTag => "GenerateSkyboxTool";
        protected override Action<string> DialogFailedCallback => errorMessage => _onFailed?.Invoke(errorMessage);

        public override TJGeneratorsAssetReference GetTargetAsset() => _placeholderRef;

        public void StartEditorCoroutine(IEnumerator coroutine)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(coroutine);
        }

        public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return null;

            // Delete placeholder so the real download can use the same path
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(_placeholderPath) != null ||
                File.Exists(Path.GetFullPath(Path.Combine(Application.dataPath, "..", _placeholderPath))))
            {
                AssetDatabase.DeleteAsset(_placeholderPath);
            }
            return _placeholderPath;
        }

        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return;

            TJLog.Log($"[GenerateSkyboxTool] Skybox texture saved: {savePath}");

            // Import as Cubemap (TextureCube)
            var importer = AssetImporter.GetAtPath(savePath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.textureShape = TextureImporterShape.TextureCube;
                importer.SaveAndReimport();
            }

            // Update placeholder skybox material to use the real Cubemap texture
            if (!string.IsNullOrEmpty(_placeholderMaterialPath))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(_placeholderMaterialPath);
                var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(savePath);
                if (mat != null && cubemap != null)
                {
                    mat.SetTexture("_Tex", cubemap);
                    EditorUtility.SetDirty(mat);
                    AssetDatabase.SaveAssets();
                }
            }

            // Add AI Generated label and session label
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));
            TJGeneratorsGenerationLabel.EnableSessionLabel(TJGeneratorsAssetReference.FromPath(savePath), _sessionId);

            _onCompleted?.Invoke(savePath, generator.CurrentPreviewUrl);
        }
    }
#endif
}
