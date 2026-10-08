using System;
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
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// CustomTool for re-texturing existing 3D models using Tripo texture-model API.
    /// Accepts either a prior task ID (original_model_task_id) or a direct model URL.
    /// Produces a re-textured model bound to a placeholder prefab (same pattern as other model tools).
    /// </summary>
    public static class GenerateTripoTextureModelTool
    {
        private const string GeneratorId = "tripo-texture-model";
        private const string DefaultModelVersion = "v2.5-20250123";

        [ExecuteCustomTool.CustomTool(
            "generate_texture_model_by_tripo",
            "Re-texture an existing 3D model using Tripo texture-model API. " +
            "Requires either original_model_task_id (from a prior generate_3d_model_by_tripo_p1 / generate_3d_model_by_rodin task) " +
            "or url (direct model file URL). " +
            "Parameters: " +
            "original_model_task_id (string — prior generation task ID), " +
            "url (string — direct model URL, alternative to original_model_task_id), " +
            "texture_prompt_text (string — text description for texture generation), " +
            "texture_prompt_image (string — image path or URL to guide texture), " +
            "texture_prompt_style_image (string — style image path or URL), " +
            "model_version (string, default 'v2.5-20250123' — options: v2.5-20250123 / v3.0-20250812), " +
            "texture (bool, default true), pbr (bool, default true), bake (bool, default false), " +
            "with_fbx (bool, default true), texture_seed (int, optional), " +
            "texture_quality (string, default 'standard' — options: standard / detailed), " +
            "texture_alignment (string, optional — options: original_image / geometry), " +
            "compress (string, optional — options: geometry), " +
            "prefab_output_path (string, optional, default auto-generated), " +
            "force_overwrite (bool, default false), " +
            "session_id (string, optional). " +
            "This call submits a backend task then starts polling asynchronously. Returns immediately with task_id and prefab_output_path."
        )]
        public static object GenerateTextureModel(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateTripoTextureModelTool] Generating with parameters: {parameters}");

                string originalModelTaskId = parameters["original_model_task_id"]?.ToString();
                string url                 = parameters["url"]?.ToString();
                string prefabOutputPath    = parameters["prefab_output_path"]?.ToString();
                bool   forceOverwrite      = parameters["force_overwrite"]?.ToObject<bool>() ?? false;
                string sessionId           = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());

                if (string.IsNullOrEmpty(originalModelTaskId) && string.IsNullOrEmpty(url))
                    return Generate3DModelTool.Fail("At least one of 'original_model_task_id' or 'url' is required.");

                // --- Resolve prefab output path ---
                if (string.IsNullOrEmpty(prefabOutputPath))
                {
                    prefabOutputPath = "Assets/TJGenerators/History/TextureModel.prefab";
                    string defaultDir = Path.GetDirectoryName(prefabOutputPath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(defaultDir))
                        PathUtils.EnsureAssetFolder(defaultDir);
                    prefabOutputPath = AssetDatabase.GenerateUniqueAssetPath(prefabOutputPath);
                    if (string.IsNullOrEmpty(prefabOutputPath))
                        prefabOutputPath = "Assets/TJGenerators/History/TextureModel.prefab";
                }
                else
                {
                    prefabOutputPath = Path.ChangeExtension(prefabOutputPath, ".prefab");
                }

                if (File.Exists(prefabOutputPath))
                {
                    if (forceOverwrite)
                    {
                        AssetDatabase.DeleteAsset(prefabOutputPath);
                    }
                    else
                    {
                        prefabOutputPath = AssetDatabase.GenerateUniqueAssetPath(prefabOutputPath);
                    }
                }

                // --- Load generator config ---
                var config = ConfigManager.GetGeneratorConfig(ConfigType.Generator, GeneratorId);
                if (config == null)
                    return Generate3DModelTool.Fail($"Cannot find generator config for '{GeneratorId}'. Ensure cn.tuanjie.ai.generators package is installed.");

                var generator = new DynamicGenerator(config);

                if (!string.IsNullOrEmpty(originalModelTaskId))
                    generator.SetExtraRawJsonField("originalModelTaskId", JsonConvert.ToString(originalModelTaskId));
                else if (!string.IsNullOrEmpty(url))
                    generator.SetExtraRawJsonField("originalModelTaskId", JsonConvert.ToString(url));

                // --- Texture prompt fields ---
                string texturePromptText = parameters["texture_prompt_text"]?.ToString();
                if (!string.IsNullOrEmpty(texturePromptText))
                    generator.SetExtraRawJsonField("texturePromptText", JsonConvert.ToString(texturePromptText));

                string texturePromptImage = parameters["texture_prompt_image"]?.ToString();
                if (!string.IsNullOrEmpty(texturePromptImage))
                {
                    string imageData = ResolveImageField(texturePromptImage);
                    generator.SetExtraRawJsonField("texturePromptImage", JsonConvert.ToString(imageData));
                }

                string texturePromptStyleImage = parameters["texture_prompt_style_image"]?.ToString();
                if (!string.IsNullOrEmpty(texturePromptStyleImage))
                {
                    string styleData = ResolveImageField(texturePromptStyleImage);
                    generator.SetExtraRawJsonField("texturePromptStyleImage", JsonConvert.ToString(styleData));
                }

                // --- Standard parameters ---
                ApplyTextureModelParameters(generator, parameters);

                // --- Submit ---
                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[GenerateTripoTextureModelTool] Task submitted, backend_task_id={submitResult.BackendTaskId}");

                // --- Create placeholder prefab ---
                string createdPrefabPath = Generate3DModelTool.CreateBlankPrefab(prefabOutputPath);
                if (string.IsNullOrEmpty(createdPrefabPath))
                    return Generate3DModelTool.Fail($"Failed to create prefab at: {prefabOutputPath}");

                TJGeneratorsGenerationLabel.EnableSessionLabel(
                    TJGeneratorsAssetReference.FromPath(createdPrefabPath), sessionId);

                var context = new TJGeneratorsGenerationContext
                {
                    TargetAsset            = TJGeneratorsAssetReference.FromPath(createdPrefabPath),
                    AutoCreateTargetPrefab = false
                };

                var taskHandle = TJGeneratorsGenerationService.GenerateFromSubmittedTask(
                    generator, context, submitResult.BackendTaskId, sessionId);

                string modelVersion = parameters["model_version"]?.ToString() ?? DefaultModelVersion;
                string displayPrompt = !string.IsNullOrEmpty(texturePromptText)
                    ? texturePromptText
                    : !string.IsNullOrEmpty(originalModelTaskId)
                        ? $"Re-texture task {originalModelTaskId}"
                        : $"Re-texture URL {url}";

                string taskId = StaticModelTaskTracker.CreateTask(
                    displayPrompt, GeneratorId, taskHandle,
                    createdPrefabPath, null, null, modelVersion, sessionId);

                return new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "task_id",            taskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       GeneratorId },
                    { "model_version",      modelVersion },
                    { "prefab_output_path", createdPrefabPath },
                    { "original_model_task_id", originalModelTaskId ?? "" },
                    { "url",                    url ?? "" },
                    { "estimated_wait_seconds", 600 },
                    { "preview_url", PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "notification_mode",      "bg_task_done" },
                    { "message",
                        "Texture model generation started. " +
                        "STEP 1 (do now): Instantiate the prefab at prefab_output_path — it contains a Cube placeholder. " +
                        "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                        "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn " +
                        "containing ALL generation results (model_path, prefab_path, preview_url, timing, etc.). " +
                        "*** POLLING IS STRICTLY FORBIDDEN — do NOT call query_texture_model_status_by_tripo repeatedly. " +
                        "Only call it ONCE as a last-resort fallback if no notification arrives. ***" +
                        "*** RE-SUBMISSION IS STRICTLY FORBIDDEN — do NOT call generate_texture_model_by_tripo " +
                        "again for the same model, regardless of outcome. Report errors and stop. ***" }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateTripoTextureModelTool] Error: {e}");
                return Generate3DModelTool.Fail($"Error: {e.Message}");
            }
#else
            return new Dictionary<string, object> { { "success", false }, { "message", "This tool only works in Unity Editor." } };
#endif
        }

        [ExecuteCustomTool.CustomTool(
            "query_texture_model_status_by_tripo",
            "Query the status of a texture model re-texture task started by generate_texture_model_by_tripo. " +
            "Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "Returns: status, progress, prefab_path, model_path (when completed), preview_url (optional). " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden."
        )]
        public static object QueryTextureModelStatus(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                string taskId = parameters["task_id"]?.ToString();
                if (string.IsNullOrEmpty(taskId))
                    return Generate3DModelTool.Fail("'task_id' parameter is required");

                var task = StaticModelTaskTracker.GetTask(taskId);
                if (task == null)
                    return Generate3DModelTool.Fail($"Task '{taskId}' not found. It may have been cleaned up or Unity was fully restarted.");

                var result = new Dictionary<string, object>
                {
                    { "success",        true },
                    { "task_id",        task.TaskId },
                    { "status",         task.Status },
                    { "progress",       task.Progress },
                    { "backend_task_id", task.BackendTaskId ?? "" },
                    { "model_version",   task.ModelVersion ?? "" },
                    { "start_time",     task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                };

                if (!string.IsNullOrEmpty(task.PrefabPath))   result["prefab_path"] = task.PrefabPath;
                if (!string.IsNullOrEmpty(task.ModelPath))    result["model_path"]  = task.ModelPath;
                result["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);
                if (!string.IsNullOrEmpty(task.ErrorMessage)) result["error"]       = task.ErrorMessage;

                if (task.EndTime.HasValue)
                {
                    result["end_time"]         = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                    result["duration_seconds"] = (int)(task.EndTime.Value - task.StartTime).TotalSeconds;
                }

                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateTripoTextureModelTool] Query error: {e}");
                return Generate3DModelTool.Fail($"Error querying status: {e.Message}");
            }
#else
            return new Dictionary<string, object> { { "success", false }, { "message", "This tool only works in Unity Editor." } };
#endif
        }

        [ExecuteCustomTool.CustomTool(
            "list_texture_model_tasks_by_tripo",
            "List all active and recent Tripo texture model re-texture tasks in the current Unity Editor session."
        )]
        public static object ListTextureModelTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks = StaticModelTaskTracker.GetAllTasks()
                    .Where(t => t.GeneratorType == GeneratorId || t.TaskId.StartsWith("texture_model_"))
                    .OrderByDescending(t => t.StartTime)
                    .ToList();
                var taskList = new List<Dictionary<string, object>>();

                foreach (var t in tasks)
                {
                    var d = new Dictionary<string, object>
                    {
                        { "task_id",         t.TaskId },
                        { "status",          t.Status },
                        { "progress",        t.Progress },
                        { "backend_task_id", t.BackendTaskId ?? "" },
                        { "model_version",   t.ModelVersion ?? "" },
                        { "start_time",      t.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                    };
                    if (!string.IsNullOrEmpty(t.PrefabPath))   d["prefab_path"] = t.PrefabPath;
                    if (!string.IsNullOrEmpty(t.ModelPath))    d["model_path"]  = t.ModelPath;
                    d["preview_url"] = PreviewUrlHelper.GetPreviewUrl(t.PreviewUrl, t.BackendTaskId);
                    if (!string.IsNullOrEmpty(t.ErrorMessage)) d["error"]       = t.ErrorMessage;
                    if (t.EndTime.HasValue) d["end_time"] = t.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                    taskList.Add(d);
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
                TJLog.LogError($"[GenerateTripoTextureModelTool] List error: {e}");
                return Generate3DModelTool.Fail($"Error listing tasks: {e.Message}");
            }
#else
            return new Dictionary<string, object> { { "success", false }, { "message", "This tool only works in Unity Editor." } };
#endif
        }

#if UNITY_EDITOR
        private static void ApplyTextureModelParameters(DynamicGenerator generator, JObject parameters)
        {
            string modelVersion = parameters["model_version"]?.ToString();
            if (string.IsNullOrEmpty(modelVersion))
                modelVersion = DefaultModelVersion;
            generator.SetParameter("modelVersion", modelVersion);

            if (parameters["texture"] != null)
                generator.SetParameter("texture", parameters["texture"].ToObject<bool>());
            if (parameters["pbr"] != null)
                generator.SetParameter("pbr", parameters["pbr"].ToObject<bool>());
            if (parameters["bake"] != null)
                generator.SetParameter("bake", parameters["bake"].ToObject<bool>());
            if (parameters["with_fbx"] != null)
                generator.SetParameter("withFbx", parameters["with_fbx"].ToObject<bool>());
            if (parameters["texture_seed"] != null)
                generator.SetParameter("textureSeed", parameters["texture_seed"].ToObject<int>());
            if (parameters["texture_quality"] != null)
                generator.SetParameter("textureQuality", parameters["texture_quality"].ToString());
            if (parameters["texture_alignment"] != null)
                generator.SetParameter("textureAlignment", parameters["texture_alignment"].ToString());
            if (parameters["compress"] != null)
                generator.SetParameter("compress", parameters["compress"].ToString());
        }

        /// <summary>
        /// Resolves an image field value to either a URL or a base64 data URI.
        /// If the value is already a URL (http/https), returns it as-is.
        /// If the value is a local file path, reads the file and encodes as base64 with data URI prefix.
        /// </summary>
        private static string ResolveImageField(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            // Already a URL — pass through
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return value;

            // Already a data URI — pass through
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return value;

            // Local file path — encode as base64
            string resolvedPath = Generate3DModelTool.ResolveImagePath(value);
            if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
            {
                byte[] imageData = File.ReadAllBytes(resolvedPath);
                string ext = Path.GetExtension(resolvedPath).ToLower();
                string mimeType = ext == ".png" ? "image/png" : "image/jpeg";
                return $"data:{mimeType};base64,{Convert.ToBase64String(imageData)}";
            }

            // Fallback: return as-is (might be a relative URL or other format)
            return value;
        }
#endif
    }
}
