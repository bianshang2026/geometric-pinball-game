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
    /// Tracks active material generation tasks
    /// </summary>
    public static class MaterialTaskTracker
    {
#if UNITY_EDITOR
        [Serializable]
        private class PersistedTask
        {
            public string taskId;
            public string generatorId;
            public string prompt;
            public string presetId;
            public string patternId;
            public string styleId;
            public string status;
            public int    progress;
            public string texturePath;
            public string materialPath;
            public string errorMessage;
            public long   startTimeTicks;
            public long   endTimeTicks;
            public string previewUrl;
            public string placeholderPath;
            public string placeholderMaterialPath;
            public string backendTaskId;
        }

        public class MaterialTaskInfo : IGenerationTaskInfo
        {
            public string TaskId { get; set; }
            public string GeneratorId { get; set; }
            public string Prompt { get; set; }
            public string PresetId { get; set; }
            public string PatternId { get; set; }
            public string StyleId { get; set; }
            public string Status { get; set; }
            public int Progress { get; set; }
            public string TexturePath { get; set; }
            public string MaterialPath { get; set; }
            public string ErrorMessage { get; set; }
            public string PreviewUrl { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public string PlaceholderPath { get; set; }
            public string PlaceholderMaterialPath { get; set; }
            public string BackendTaskId { get; set; }
        }

        private static readonly GenerationTaskTrackerStore<MaterialTaskInfo, PersistedTask> Store =
            new GenerationTaskTrackerStore<MaterialTaskInfo, PersistedTask>(
                "TJGen_Material", BuildPersisted, FromPersisted);

        private static PersistedTask BuildPersisted(MaterialTaskInfo info) => new PersistedTask
        {
            taskId                  = info.TaskId,
            generatorId             = info.GeneratorId,
            prompt                  = info.Prompt ?? "",
            presetId                = info.PresetId ?? "",
            patternId               = info.PatternId ?? "",
            styleId                 = info.StyleId ?? "",
            status                  = info.Status,
            progress                = info.Progress,
            texturePath             = info.TexturePath ?? "",
            materialPath            = info.MaterialPath ?? "",
            errorMessage            = info.ErrorMessage ?? "",
            startTimeTicks          = info.StartTime.Ticks,
            endTimeTicks            = info.EndTime?.Ticks ?? 0,
            previewUrl              = info.PreviewUrl ?? "",
            placeholderPath         = info.PlaceholderPath ?? "",
            placeholderMaterialPath = info.PlaceholderMaterialPath ?? "",
            backendTaskId           = info.BackendTaskId ?? ""
        };

        private static MaterialTaskInfo FromPersisted(PersistedTask p) => new MaterialTaskInfo
        {
            TaskId                  = p.taskId,
            GeneratorId             = p.generatorId,
            Prompt                  = p.prompt,
            PresetId                = p.presetId,
            PatternId               = p.patternId,
            StyleId                 = p.styleId,
            Status                  = p.status,
            Progress                = p.progress,
            TexturePath             = p.texturePath,
            MaterialPath            = p.materialPath,
            ErrorMessage            = p.errorMessage,
            PreviewUrl              = p.previewUrl,
            StartTime               = new DateTime(p.startTimeTicks),
            EndTime                 = p.endTimeTicks > 0 ? (DateTime?)new DateTime(p.endTimeTicks) : null,
            PlaceholderPath         = p.placeholderPath,
            PlaceholderMaterialPath = p.placeholderMaterialPath,
            BackendTaskId           = p.backendTaskId
        };

        internal static void ApplyTaskUpdate(MaterialTaskInfo task, Action<MaterialTaskInfo> mutate) =>
            Store.ApplyTaskUpdate(task, mutate);

        public static string CreateTask(string generatorId, string prompt, string presetId, string patternId, string styleId, string placeholderPath = null, string placeholderMaterialPath = null, string backendTaskId = null)
        {
            string taskId = Store.AllocateTaskId("material");
            var task = new MaterialTaskInfo
            {
                TaskId                  = taskId,
                GeneratorId             = generatorId,
                Prompt                  = prompt ?? "",
                PresetId                = presetId ?? "",
                PatternId               = patternId ?? "",
                StyleId                 = styleId ?? "",
                Status                  = "generating",
                StartTime               = DateTime.Now,
                PlaceholderPath         = placeholderPath,
                PlaceholderMaterialPath = placeholderMaterialPath,
                BackendTaskId           = backendTaskId
            };
            Store.RegisterTask(taskId, task);
            return taskId;
        }

        public static void MarkCompleted(string taskId, string texturePath, string materialPath, string previewUrl = null)
        {
            var task = Store.GetTask(taskId);
            if (task == null) return;
            Store.ApplyTaskUpdate(task, t =>
            {
                t.Status      = "completed";
                t.Progress    = 100;
                t.TexturePath = texturePath;
                t.MaterialPath = materialPath;
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

        public static MaterialTaskInfo GetTask(string taskId) => Store.GetTask(taskId);

        public static List<MaterialTaskInfo> GetAllTasks() => Store.GetAllTasks();

        public static MaterialTaskInfo GetTaskByBackendId(string backendTaskId) =>
            Store.GetTaskByBackendId(backendTaskId);

        public static MaterialTaskInfo CreateRecoveredTask(
            string backendTaskId, string prompt, string placeholderPath, string placeholderMaterialPath, long timestampMs)
        {
            return Store.CreateRecoveredTask(backendTaskId, () => new MaterialTaskInfo
            {
                TaskId                  = $"recovered_{backendTaskId}",
                BackendTaskId           = backendTaskId,
                Prompt                  = prompt ?? "",
                PlaceholderPath         = placeholderPath ?? "",
                PlaceholderMaterialPath = placeholderMaterialPath ?? "",
                Status                  = "recovering",
                Progress                = 0,
                StartTime               = timestampMs > 0
                                            ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).LocalDateTime
                                            : DateTime.Now
            });
        }

        public static void RemoveTask(string taskId) => Store.RemoveTask(taskId);

        public static void CleanupCompletedTasks() => Store.CleanupCompletedTasks();
#endif
    }

    /// <summary>
    /// CustomTool for generating surface materials (PBR textures + Material asset) using TJGenerators Material pipeline.
    /// Supports text-to-material and image-to-material generation with material preset, texture pattern, and style selection.
    /// Output is a PNG texture + .mat Material asset saved to Assets/TJGenerators/History/.
    /// </summary>
    public static class GenerateMaterialTool
    {
        [ExecuteCustomTool.CustomTool("generate_material",
            "Generate a PBR surface material (texture PNG + Unity .mat Material asset) from a text prompt or reference image using AI. " +
            "Output is saved to Assets/TJGenerators/History/. " +
            "Parameters: generator_id (optional, default 'huoshan_seedream_material'), " +
            "prompt (optional text description of material appearance), " +
            "image_path (optional reference image file path — used as material template), " +
            "preset_id (optional material type preset; valid values: 'metal', 'wood', 'stone', 'fabric', 'leather', 'concrete', 'brick', 'tile', 'glass', 'ceramic', 'grass', 'sand', 'snow'), " +
            "pattern_id (optional texture pattern; valid values: 'uniform', 'horizontal_lines', 'vertical_lines', 'cross_hatch', 'diagonal', 'wave', 'noise', 'honeycomb', 'brick_layout', 'scales', 'cracks', 'woven'; " +
            "when set, uses a built-in pattern template image as input — requires template images to be pre-generated via AI/开发/生成纹理走势模板图 (TJGENERATORS_DEBUG), " +
            "style_id (optional material state; valid values: 'new', 'aged', 'dirty', 'wet', 'weathered'), " +
            "size (optional output resolution: '1920x1920', '2048x2048', '3072x3072', '3200x3200'; total pixels must be between ~3.69M and ~10.4M — do not use '1024x1024' or '4096x4096', default '2048x2048'), " +
            "output_path (optional custom save path for the texture PNG). " +
            "NOTE: Provide at least one of image_path, pattern_id, preset_id, or prompt. " +
            "IMPORTANT: Generation takes 1-3 minutes. Wait at least 5 seconds before the first query_material_status call, then poll every 10-15 seconds. " +
            "A placeholder_path and placeholder_material_path are returned immediately — you can assign the material to a renderer right away.")]
        public static object GenerateMaterial(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateMaterialTool] Generating material with parameters: {parameters}");

                string generatorId = parameters["generator_id"]?.ToString() ?? "huoshan_seedream_material";
                string userPrompt = parameters["prompt"]?.ToString();
                string imagePath = parameters["image_path"]?.ToString();
                string presetId = parameters["preset_id"]?.ToString();
                string patternId = parameters["pattern_id"]?.ToString();
                string styleId = parameters["style_id"]?.ToString();
                string outputPath = parameters["output_path"]?.ToString();
                string sessionId = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());

                int maxLen = TJGeneratorsPromptLimits.GetMaxLength(generatorId);
                if (maxLen > 0 && !string.IsNullOrEmpty(userPrompt) && userPrompt.Length > maxLen)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "PROMPT_TOO_LONG" },
                        { "message", $"Prompt length ({userPrompt.Length}) exceeds the {maxLen} character limit for '{generatorId}'." }
                    };
                }

                // Load material generator config
                var config = ConfigManager.GetGeneratorConfig(ConfigType.Material, generatorId);
                if (config == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Cannot find material generator config for '{generatorId}'. Valid value: 'huoshan_seedream_material'." }
                    };
                }

                // Resolve preset, pattern, style from config
                var presetOption = !string.IsNullOrEmpty(presetId)
                    ? config.materialPresetSelector?.options?.FirstOrDefault(o => o.id == presetId)
                    : null;
                var patternOption = !string.IsNullOrEmpty(patternId)
                    ? config.texturePatternSelector?.options?.FirstOrDefault(o => o.id == patternId)
                    : null;
                var styleOption = !string.IsNullOrEmpty(styleId)
                    ? config.materialStyleSelector?.options?.FirstOrDefault(o => o.id == styleId)
                    : null;

                if (!string.IsNullOrEmpty(presetId) && presetOption == null)
                    TJLog.LogWarning($"[GenerateMaterialTool] preset_id '{presetId}' not found in config, ignoring.");
                if (!string.IsNullOrEmpty(patternId) && patternOption == null)
                    TJLog.LogWarning($"[GenerateMaterialTool] pattern_id '{patternId}' not found in config, ignoring.");
                if (!string.IsNullOrEmpty(styleId) && styleOption == null)
                    TJLog.LogWarning($"[GenerateMaterialTool] style_id '{styleId}' not found in config, ignoring.");

                // Build combined prompt: preset + style + user prompt
                string combinedPrompt = BuildCombinedPrompt(presetOption, styleOption, userPrompt);

                // Determine image input: pattern template takes precedence over image_path
                string resolvedImagePath = imagePath;
                if (patternOption != null)
                {
                    string templatePath = TJGeneratorsMaterialTemplateGenerator.GetAbsoluteTemplatePath(patternOption.id);
                    if (File.Exists(templatePath))
                    {
                        resolvedImagePath = templatePath;
                        TJLog.Log($"[GenerateMaterialTool] Using pattern template image: {templatePath}");
                    }
                    else
                    {
                        TJLog.LogWarning($"[GenerateMaterialTool] Pattern template image not found: {templatePath}. " +
                            "Use AI/开发/生成纹理走势模板图 (TJGENERATORS_DEBUG) to pre-generate template images.");
                        resolvedImagePath = imagePath; // fall back to user-provided image
                    }
                }

                // Validate: need at least a prompt or image
                if (string.IsNullOrEmpty(combinedPrompt) && string.IsNullOrEmpty(resolvedImagePath))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "At least one of 'prompt', 'preset_id', 'pattern_id', or 'image_path' must be provided." }
                    };
                }

                // Create generator and set inputs
                var generator = new DynamicGenerator(config);

                if (!string.IsNullOrEmpty(combinedPrompt))
                    generator.SetTextPrompt(combinedPrompt);

                if (!string.IsNullOrEmpty(resolvedImagePath))
                    generator.SetImagePath(resolvedImagePath);

                // Apply additional parameters
                ApplyMaterialParameters(generator, parameters);

                // 阶段1：同步提交任务到后端，立即获取 backendTaskId 或失败原因
                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[GenerateMaterialTool] 任务提交失败 [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[GenerateMaterialTool] 任务提交成功，backend_task_id={submitResult.BackendTaskId}");

                // 提交成功后才创建 placeholder（避免在鉴权失败时留下无用文件）
                string placeholderPath = CreatePlaceholderTexture(outputPath);
                string placeholderMatPath = CreatePlaceholderMaterial(placeholderPath);

                // Create tracked task
                string capturedBackendTaskId = submitResult.BackendTaskId;
                string taskId = MaterialTaskTracker.CreateTask(generatorId, combinedPrompt, presetId, patternId, styleId, placeholderPath, placeholderMatPath, capturedBackendTaskId);

                // Create pipeline host
                var host = new MaterialPipelineHost(
                    placeholderPath,
                    placeholderMatPath,
                    presetOption,
                    sessionId,
                    (texPath, matPath, previewUrl) =>
                    {
                        MaterialTaskTracker.MarkCompleted(taskId, texPath, matPath, previewUrl);
                        var t = MaterialTaskTracker.GetTask(taskId);
                        GenerationNotifier.NotifyCompleted("generate_material", taskId, capturedBackendTaskId,
                            new JObject
                            {
                                ["session_id"]       = sessionId,
                                ["generator_id"]     = generatorId,
                                ["prompt"]           = combinedPrompt ?? "",
                                ["texture_path"]     = texPath ?? "",
                                ["material_path"]    = matPath ?? "",
                                ["preview_url"]      = previewUrl ?? "",
                                ["progress"]         = 100,
                                ["start_time"]       = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["end_time"]         = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
                            });
                    },
                    errorMsg =>
                    {
                        MaterialTaskTracker.MarkFailed(taskId, errorMsg);
                        GenerationNotifier.NotifyFailed("generate_material", taskId, capturedBackendTaskId, errorMsg,
                            new JObject { ["session_id"] = sessionId, ["generator_id"] = generatorId, ["prompt"] = combinedPrompt ?? "" });
                    });

                // 阶段2：异步轮询（跳过提交）
                var pipeline = new GenerationPipeline(host, ConfigType.Material, GenerationRequestOrigin.Agent, sessionId, "generate_material");
                string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(placeholderMatPath);
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId));

                TJLog.Log($"[GenerateMaterialTool] 轮询已启动，task_id={taskId}, backend_task_id={submitResult.BackendTaskId}, placeholder: {placeholderPath}, mat: {placeholderMatPath}");

                var result = new Dictionary<string, object>
                {
                    { "success",              true },
                    { "submission_success",   true },
                    { "message",
                        "Material generation started. " +
                        "STEP 1 (do now): Use `place_assets_in_scene` skill to apply placeholder_material_path to the scene. " +
                        "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                        "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn (~90s) " +
                        "containing ALL generation results (texture_path, material_path, preview_url, timing, etc.). " +
                        "*** POLLING IS STRICTLY FORBIDDEN — do NOT call query_material_status repeatedly. " +
                        "Only call query_material_status ONCE as a last-resort fallback if no notification arrives. ***" },
                    { "task_id",              taskId },
                    { "backend_task_id",      submitResult.BackendTaskId },
                    { "status",               "submitted" },
                    { "generator_id",         generatorId },
                    { "prompt",               combinedPrompt ?? "" },
                    { "placeholder_path",     placeholderPath },
                    { "placeholder_material_path", placeholderMatPath },
                    { "expected_texture_path",  placeholderPath },
                    { "expected_material_path", placeholderMatPath },
                    { "preview_url",          PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "estimated_wait_seconds", 90 },
                    { "notification_mode",    "bg_task_done" }
                };

                if (!string.IsNullOrEmpty(presetId)) result["preset_id"] = presetId;
                if (!string.IsNullOrEmpty(patternId)) result["pattern_id"] = patternId;
                if (!string.IsNullOrEmpty(styleId)) result["style_id"] = styleId;

                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateMaterialTool] Error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error generating material: {e.Message}" }
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

        [ExecuteCustomTool.CustomTool("query_material_status",
            "Query the status of a material generation task. Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "When completed, returns 'texture_path' (PNG) and 'material_path' (.mat) with asset paths in the project. " +
            "Status values: 'generating', 'completed', 'failed'. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QueryMaterialStatus(JObject parameters)
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

                var task = MaterialTaskTracker.GetTask(taskId);

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

                if (!string.IsNullOrEmpty(task.PresetId)) result["preset_id"] = task.PresetId;
                if (!string.IsNullOrEmpty(task.PatternId)) result["pattern_id"] = task.PatternId;
                if (!string.IsNullOrEmpty(task.StyleId)) result["style_id"] = task.StyleId;
                if (!string.IsNullOrEmpty(task.TexturePath)) result["texture_path"] = task.TexturePath;
                if (!string.IsNullOrEmpty(task.MaterialPath)) result["material_path"] = task.MaterialPath;
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
                    if (!string.IsNullOrEmpty(task.PlaceholderMaterialPath))
                        result["placeholder_material_path"] = task.PlaceholderMaterialPath;
                }

                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateMaterialTool] Query error: {e}");
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

        [ExecuteCustomTool.CustomTool("list_material_tasks", "List all active and recent material generation tasks")]
        public static object ListMaterialTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks = MaterialTaskTracker.GetAllTasks();
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

                    if (!string.IsNullOrEmpty(task.PresetId)) taskData["preset_id"] = task.PresetId;
                    if (!string.IsNullOrEmpty(task.PatternId)) taskData["pattern_id"] = task.PatternId;
                    if (!string.IsNullOrEmpty(task.StyleId)) taskData["style_id"] = task.StyleId;
                    if (!string.IsNullOrEmpty(task.TexturePath)) taskData["texture_path"] = task.TexturePath;
                    if (!string.IsNullOrEmpty(task.MaterialPath)) taskData["material_path"] = task.MaterialPath;
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
                TJLog.LogError($"[GenerateMaterialTool] List error: {e}");
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
        private static string BuildCombinedPrompt(
            MaterialTemplateOptionConfig presetOption,
            MaterialTemplateOptionConfig styleOption,
            string userPrompt)
        {
            var parts = new List<string>();

            if (presetOption != null && !string.IsNullOrEmpty(presetOption.prompt))
                parts.Add(presetOption.prompt);

            if (styleOption != null && !string.IsNullOrEmpty(styleOption.prompt))
                parts.Add(styleOption.prompt);

            if (!string.IsNullOrEmpty(userPrompt))
                parts.Add(userPrompt);

            return string.Join(", ", parts);
        }

        private static string CreatePlaceholderTexture(string outputPath)
        {
            string placeholderPath;
            if (!string.IsNullOrEmpty(outputPath))
            {
                string dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                placeholderPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.ChangeExtension(outputPath, ".png"));
            }
            else
            {
                if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                    AssetDatabase.CreateFolder("Assets", "TJGenerators");
                if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                    AssetDatabase.CreateFolder("Assets/TJGenerators", "History");
                string uniqueName = "Material_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                placeholderPath = AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/History/" + uniqueName);
            }

            // Create 1x1 gray placeholder PNG
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1f));
            tex.Apply();
            byte[] pngBytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            string absolutePath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", placeholderPath));
            string parentDir = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);
            File.WriteAllBytes(absolutePath, pngBytes);
            AssetDatabase.ImportAsset(placeholderPath, ImportAssetOptions.ForceUpdate);

            return placeholderPath;
        }

        private static string CreatePlaceholderMaterial(string texturePath)
        {
            string matPath = AssetDatabase.GenerateUniqueAssetPath(
                Path.ChangeExtension(texturePath, ".mat"));
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            var shader = TJMaterialShaderUtility.ResolveSurfaceLitShader()
                         ?? Shader.Find("Unlit/Texture");
            var mat = new Material(shader);
            if (texture != null)
                TJMaterialShaderUtility.AssignBaseColorTexture(mat, texture);
            AssetDatabase.CreateAsset(mat, matPath);
            AssetDatabase.SaveAssets();
            return matPath;
        }

        private static void ApplyMaterialParameters(DynamicGenerator generator, JObject parameters)
        {
            if (parameters["size"] != null)
                generator.SetParameter("size", parameters["size"].ToString());

            if (parameters["is_segmentation"] != null)
                generator.SetParameter("isSegmentation", parameters["is_segmentation"].ToObject<bool>());

            if (parameters["q_value"] != null)
                generator.SetParameter("qValue", parameters["q_value"].ToObject<int>());

            if (parameters["resize_width"] != null)
                generator.SetParameter("resizeWidth", parameters["resize_width"].ToObject<int>());
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>
    /// Automatically resumes interrupted generate_material tasks after domain reload.
    /// </summary>
    [InitializeOnLoad]
    public static class MaterialDomainReloadRecovery
    {
        static MaterialDomainReloadRecovery()
        {
            CustomToolDomainReloadRecovery.Schedule(ResumeInterruptedTasks);
        }

        private static void ResumeInterruptedTasks()
        {
            CustomToolDomainReloadRecovery.Resume(
                "GenerateMaterialTool",
                ConfigType.Material,
                t => t.toolName == "generate_material",
                () => MaterialTaskTracker.GetAllTasks(),
                (interrupted, config, generator) =>
                {
                    var trackerTask = MaterialTaskTracker.GetTaskByBackendId(interrupted.backendTaskId);
                    if (trackerTask != null)
                    {
                        CustomToolDomainReloadRecovery.MarkTrackerRecoveringIfNeeded(trackerTask.Status, () =>
                        {
                            MaterialTaskTracker.ApplyTaskUpdate(trackerTask, t => t.Status = "recovering");
                        });
                    }
                    else
                    {
                        string matPath = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);
                        string texPath = !string.IsNullOrEmpty(matPath)
                            ? Path.ChangeExtension(matPath, ".png")
                            : "";
                        trackerTask = MaterialTaskTracker.CreateRecoveredTask(
                            interrupted.backendTaskId, interrupted.prompt, texPath, matPath, interrupted.timestamp);
                    }

                    string placeholderPath = trackerTask.PlaceholderPath ?? "";
                    string placeholderMatPath = trackerTask.PlaceholderMaterialPath ?? "";
                    if (string.IsNullOrEmpty(placeholderMatPath))
                        placeholderMatPath = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);
                    if (string.IsNullOrEmpty(placeholderPath) && !string.IsNullOrEmpty(placeholderMatPath))
                        placeholderPath = Path.ChangeExtension(placeholderMatPath, ".png");

                    var presetOption = !string.IsNullOrEmpty(trackerTask.PresetId)
                        ? config.materialPresetSelector?.options?.FirstOrDefault(o => o.id == trackerTask.PresetId)
                        : null;

                    string sessionId = interrupted.sessionId ?? "";
                    string capturedBackendTaskId = interrupted.backendTaskId;
                    string taskId = trackerTask.TaskId;

                    var host = new MaterialPipelineHost(
                        placeholderPath,
                        placeholderMatPath,
                        presetOption,
                        sessionId,
                        (texPath, matPath, previewUrl) =>
                        {
                            MaterialTaskTracker.MarkCompleted(taskId, texPath, matPath, previewUrl);
                            var t = MaterialTaskTracker.GetTask(taskId);
                            GenerationNotifier.NotifyCompleted("generate_material", taskId, capturedBackendTaskId,
                                new JObject
                                {
                                    ["session_id"] = sessionId,
                                    ["generator_id"] = t?.GeneratorId ?? interrupted.modelVersion ?? "",
                                    ["prompt"] = t?.Prompt ?? interrupted.prompt ?? "",
                                    ["texture_path"] = texPath ?? "",
                                    ["material_path"] = matPath ?? "",
                                    ["preview_url"] = previewUrl ?? "",
                                    ["progress"] = 100,
                                    ["start_time"] = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                    ["end_time"] = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                    ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
                                });
                        },
                        errorMsg =>
                        {
                            MaterialTaskTracker.MarkFailed(taskId, errorMsg);
                            GenerationNotifier.NotifyFailed("generate_material", taskId, capturedBackendTaskId, errorMsg,
                                new JObject
                                {
                                    ["session_id"] = sessionId,
                                    ["generator_id"] = trackerTask.GeneratorId ?? interrupted.modelVersion ?? "",
                                    ["prompt"] = trackerTask.Prompt ?? interrupted.prompt ?? ""
                                });
                        });

                    CustomToolDomainReloadRecovery.StartPolling(
                        "GenerateMaterialTool", host, ConfigType.Material,
                        sessionId, "generate_material", generator, interrupted.backendTaskId);
                });
        }
    }

    /// <summary>
    /// IGenerationPipelineHost implementation for headless material generation via custom tools.
    /// Handles texture saving with Default import settings and creates a .mat Material asset.
    /// </summary>
    internal class MaterialPipelineHost : HeadlessPipelineHostBase, IMediaAssetPipelineHost
    {
        private readonly string _placeholderPath;
        private readonly TJGeneratorsAssetReference _materialRef;
        private readonly MaterialTemplateOptionConfig _presetOption;
        private readonly string _sessionId;
        private readonly Action<string, string, string> _onCompleted;
        private readonly Action<string> _onFailed;

        public MaterialPipelineHost(
            string placeholderPath,
            string placeholderMatPath,
            MaterialTemplateOptionConfig presetOption,
            string sessionId,
            Action<string, string, string> onCompleted,
            Action<string> onFailed)
        {
            _placeholderPath = placeholderPath;
            _materialRef = TJGeneratorsAssetReference.FromPath(placeholderMatPath);
            _presetOption = presetOption;
            _sessionId = sessionId ?? "";
            _onCompleted = onCompleted;
            _onFailed = onFailed;
        }

        protected override string DialogLogTag => "GenerateMaterialTool";
        protected override Action<string> DialogFailedCallback => errorMessage => _onFailed?.Invoke(errorMessage);

        public override TJGeneratorsAssetReference GetTargetAsset() => _materialRef;

        public void StartEditorCoroutine(IEnumerator coroutine)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(coroutine);
        }

        public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return null;

            // Return the placeholder path directly — pipeline overwrites the file bytes in-place.
            // Do NOT delete the placeholder: deleting would assign a new GUID on reimport,
            // breaking any Renderer.sharedMaterial references set up before generation completed.
            return _placeholderPath;
        }

        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return;

            TJLog.Log($"[GenerateMaterialTool] Texture saved: {savePath}");

            // Import texture as Default (not Sprite)
            var importer = AssetImporter.GetAtPath(savePath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.SaveAndReimport();
            }

            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));
            TJGeneratorsGenerationLabel.EnableSessionLabel(TJGeneratorsAssetReference.FromPath(savePath), _sessionId);

            // Update the placeholder .mat in-place instead of delete+recreate.
            // This preserves the material GUID so any Renderer.sharedMaterial references stay valid.
            string placeholderMatPath = Path.ChangeExtension(_placeholderPath, ".mat");
            string materialPath = UpdateOrCreateMaterialAsset(savePath, placeholderMatPath);

            _onCompleted?.Invoke(savePath, materialPath, generator.CurrentPreviewUrl);
        }

        private string UpdateOrCreateMaterialAsset(string texturePath, string placeholderMatPath)
        {
            try
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                if (texture == null)
                {
                    TJLog.LogWarning($"[GenerateMaterialTool] Cannot load texture at: {texturePath}");
                    return null;
                }

                // Try to update the placeholder material in-place (preserves GUID and Renderer references)
                var existingMat = AssetDatabase.LoadAssetAtPath<Material>(placeholderMatPath);
                if (existingMat != null)
                {
                    TJMaterialShaderUtility.EnsureCompatibleSurfaceShader(existingMat);
                    TJMaterialShaderUtility.AssignBaseColorTexture(existingMat, texture);
                    if (_presetOption != null)
                        TJMaterialShaderUtility.ApplySurfaceMaterialPreset(existingMat, _presetOption.id);
                    EditorUtility.SetDirty(existingMat);
                    AssetDatabase.SaveAssets();
                    TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(placeholderMatPath));
                    TJGeneratorsGenerationLabel.EnableSessionLabel(TJGeneratorsAssetReference.FromPath(placeholderMatPath), _sessionId);
                    TJLog.Log($"[GenerateMaterialTool] Material updated in-place: {placeholderMatPath}");
                    var matAsset = AssetDatabase.LoadAssetAtPath<Material>(placeholderMatPath);
                    if (matAsset != null) { Selection.activeObject = matAsset; EditorGUIUtility.PingObject(matAsset); }
                    return placeholderMatPath;
                }

                // Placeholder mat doesn't exist — create a new one
                string materialPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.ChangeExtension(texturePath, ".mat"));
                var shader = TJMaterialShaderUtility.ResolveSurfaceLitShader()
                             ?? Shader.Find("Unlit/Texture");
                var material = new Material(shader);
                TJMaterialShaderUtility.AssignBaseColorTexture(material, texture);
                if (_presetOption != null)
                    TJMaterialShaderUtility.ApplySurfaceMaterialPreset(material, _presetOption.id);
                AssetDatabase.CreateAsset(material, materialPath);
                AssetDatabase.SaveAssets();
                TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(materialPath));
                TJGeneratorsGenerationLabel.EnableSessionLabel(TJGeneratorsAssetReference.FromPath(materialPath), _sessionId);
                TJLog.Log($"[GenerateMaterialTool] Material asset created: {materialPath}");
                var newMatAsset = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (newMatAsset != null) { Selection.activeObject = newMatAsset; EditorGUIUtility.PingObject(newMatAsset); }
                return materialPath;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateMaterialTool] Error updating material asset: {e.Message}");
                return null;
            }
        }
    }
#endif
}
