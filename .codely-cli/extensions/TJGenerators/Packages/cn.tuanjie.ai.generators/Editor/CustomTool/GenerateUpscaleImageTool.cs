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
using Unity.UniAsset.Manager.Editor.InternalBridge;
using Unity.EditorCoroutines.Editor;
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// CustomTool for upscaling images using AI super-resolution (Real-ESRGAN via fal.ai).
    /// Uploads the local image to TOS, then submits an async upscale task.
    /// Output is a PNG/JPEG saved to Assets/TJGenerators/History/.
    /// Domain-reload recovery is handled by ImageDomainReloadRecovery in GenerateImageTool.cs
    /// (shares ImageTaskTracker and ImagePipelineHost).
    /// </summary>
    public static class GenerateUpscaleImageTool
    {
        private const string GeneratorId = "esrgan";

        [ExecuteCustomTool.CustomTool("upscale_image",
            "Upscale an image using AI super-resolution (Real-ESRGAN). " +
            "Supports 1x-8x upscaling with model variants for photo, anime/2D game art, and denoising. " +
            "Output is a PNG/JPEG saved to Assets/TJGenerators/History/. " +
            "Parameters: image_path (local image path, required), " +
            "scale (1-8, default 4), " +
            "model (optional: RealESRGAN_x4plus|RealESRGAN_x2plus|RealESRGAN_x4plus_anime_6B|RealESRGAN_x4_v3|RealESRGAN_x4_wdn_v3|RealESRGAN_x4_anime_v3; auto-select if omitted), " +
            "face_enhance (optional bool, default false), " +
            "output_format (optional 'png'|'jpeg', default 'png'), " +
            "output_path (optional save path). " +
            "IMPORTANT: No placeholder is returned. A <bg_task_done> notification will arrive upon completion.")]
        public static object UpscaleImage(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateUpscaleImageTool] Upscaling image with parameters: {parameters}");

                string imagePath = parameters["image_path"]?.ToString();
                string outputPath = parameters["output_path"]?.ToString();
                string sessionId = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());

                if (string.IsNullOrEmpty(imagePath))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "'image_path' parameter is required" }
                    };
                }

                // Resolve local path
                string absPath = ResolveImagePath(imagePath);
                if (string.IsNullOrEmpty(absPath) || !File.Exists(absPath))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "FILE_NOT_FOUND" },
                        { "message", $"Image file not found: {imagePath}" }
                    };
                }

                // Load esrgan generator config
                var config = ConfigManager.GetGeneratorConfig(ConfigType.Image, GeneratorId);
                if (config == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Cannot find generator config for '{GeneratorId}'. Ensure the TJGenerators package is installed and the Editor has finished compiling." }
                    };
                }

                var generator = new DynamicGenerator(config);

                // Upload image to TOS and get CDN URL
                string cdnUrl = UploadFileToTOS(absPath, "image");
                if (string.IsNullOrEmpty(cdnUrl))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "UPLOAD_FAILED" },
                        { "message", "Failed to upload image to TOS" }
                    };
                }

                // Set imageUrl parameter (backend expects a CDN URL, not base64)
                generator.SetParameter("imageUrl", cdnUrl);

                // Apply optional parameters
                ApplyUpscaleParameters(generator, parameters);

                // Submit task
                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[GenerateUpscaleImageTool] 任务提交失败 [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[GenerateUpscaleImageTool] 任务提交成功，backend_task_id={submitResult.BackendTaskId}");

                // Create placeholder texture
                string placeholderPath = CreatePlaceholderTexture(outputPath);

                // Register task
                string capturedBackendTaskId = submitResult.BackendTaskId;
                int scale = parameters["scale"] != null ? parameters["scale"].ToObject<int>() : 4;
                string model = parameters["model"]?.ToString() ?? "";
                string taskId = ImageTaskTracker.CreateTask(GeneratorId, "", imagePath, placeholderPath, capturedBackendTaskId);

                // Create pipeline host
                var host = new ImagePipelineHost(
                    placeholderPath,
                    sessionId,
                    (savedPath, previewUrl) =>
                    {
                        ImageTaskTracker.MarkTaskCompleted(taskId, savedPath, previewUrl);
                        var t = ImageTaskTracker.GetTask(taskId);
                        GenerationNotifier.NotifyCompleted("upscale_image", taskId, capturedBackendTaskId,
                            new JObject
                            {
                                ["session_id"]       = sessionId,
                                ["generator_id"]     = GeneratorId,
                                ["image_path"]       = savedPath,
                                ["preview_url"]      = previewUrl ?? "",
                                ["scale"]            = scale,
                                ["model"]            = model,
                                ["progress"]         = 100,
                                ["start_time"]       = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["end_time"]         = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
                            });
                    },
                    errorMsg =>
                    {
                        ImageTaskTracker.MarkTaskFailed(taskId, errorMsg);
                        GenerationNotifier.NotifyFailed("upscale_image", taskId, capturedBackendTaskId, errorMsg,
                            new JObject { ["session_id"] = sessionId, ["generator_id"] = GeneratorId });
                    }
                );

                string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(placeholderPath);
                var pipeline = new GenerationPipeline(host, ConfigType.Image, GenerationRequestOrigin.Agent, sessionId, "upscale_image");
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId));

                TJLog.Log($"[GenerateUpscaleImageTool] 轮询已启动，task_id={taskId}, backend_task_id={submitResult.BackendTaskId}");

                return new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "message",
                        "Image upscale started. " +
                        "STEP 1 (do now): END THIS RESPONSE TURN immediately. " +
                        "STEP 2 (automatic): A <bg_task_done> notification will appear in your next turn (~30s) " +
                        "containing ALL results (image_path, preview_url, timing, etc.). " +
                        "*** POLLING IS STRICTLY FORBIDDEN. Only call query_upscale_image_status ONCE as a last-resort fallback. ***" },
                    { "task_id",            taskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       GeneratorId },
                    { "image_path",         imagePath },
                    { "scale",              scale },
                    { "model",              model },
                    { "estimated_wait_seconds", 30 },
                    { "notification_mode",  "bg_task_done" },
                    { "preview_url",        PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateUpscaleImageTool] Error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error upscaling image: {e.Message}" }
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

        [ExecuteCustomTool.CustomTool("query_upscale_image_status",
            "Query the status of an image upscale task. Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "When completed, returns 'image_path' with the upscaled image asset path. " +
            "Status values: 'generating', 'completed', 'failed'. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QueryUpscaleImageStatus(JObject parameters)
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

                var task = ImageTaskTracker.GetTask(taskId);
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
                    { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                };

                if (!string.IsNullOrEmpty(task.ResultPath))
                    result["image_path"] = task.ResultPath;

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
                TJLog.LogError($"[GenerateUpscaleImageTool] Query error: {e}");
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

        [ExecuteCustomTool.CustomTool("list_upscale_image_tasks", "List all active and recent image upscale tasks")]
        public static object ListUpscaleImageTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var allTasks = ImageTaskTracker.GetAllTasks();
                var taskList = new List<Dictionary<string, object>>();

                foreach (var task in allTasks)
                {
                    if (task.GeneratorId != GeneratorId)
                        continue;

                    var taskData = new Dictionary<string, object>
                    {
                        { "task_id", task.TaskId },
                        { "generator_id", task.GeneratorId },
                        { "status", task.Status },
                        { "progress", task.Progress },
                        { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                    };

                    if (!string.IsNullOrEmpty(task.ResultPath))
                        taskData["image_path"] = task.ResultPath;

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
                TJLog.LogError($"[GenerateUpscaleImageTool] List error: {e}");
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
        private static string ResolveImagePath(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath))
                return null;
            if (Path.IsPathRooted(imagePath))
                return File.Exists(imagePath) ? imagePath : null;
            if (imagePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                string absPath = Path.Combine(Application.dataPath.Replace("Assets", ""), imagePath).Replace("\\", "/");
                return File.Exists(absPath) ? absPath : null;
            }
            string fallback = Path.Combine(Application.dataPath, imagePath).Replace("\\", "/");
            return File.Exists(fallback) ? fallback : null;
        }

        private static string UploadFileToTOS(string absPath, string fileType)
        {
            string uploadEndpoint = fileType == "image" ? "upload/image" : "upload/audio";
            string fieldName = fileType == "image" ? "image" : "audio";
            string url = ConfigManager.GetApiBaseUrl() + uploadEndpoint;

            string token = UnityConnectSession.instance.GetAccessToken();
            if (string.IsNullOrEmpty(token))
            {
                TJLog.LogError("[GenerateUpscaleImageTool] Not logged in");
                return null;
            }

            try
            {
                byte[] fileBytes = File.ReadAllBytes(absPath);
                string fileName = Path.GetFileName(absPath);
                string boundary = "----TJGenBoundary" + DateTime.Now.Ticks;
                string contentType = "multipart/form-data; boundary=" + boundary;

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(60);
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
                    client.DefaultRequestHeaders.Add("orgId", UnityConnectSession.instance.GetOrgId());
                    client.DefaultRequestHeaders.Add("source", "codely");

                    var form = new System.Net.Http.MultipartFormDataContent(boundary);
                    var fileContent = new System.Net.Http.ByteArrayContent(fileBytes);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                        GetMimeType(absPath));
                    form.Add(fileContent, fieldName, fileName);

                    var response = client.PostAsync(url, form).Result;
                    string body = response.Content.ReadAsStringAsync().Result;

                    if (!response.IsSuccessStatusCode)
                    {
                        TJLog.LogError($"[GenerateUpscaleImageTool] Upload failed: {response.StatusCode} {body}");
                        return null;
                    }

                    var json = JObject.Parse(body);
                    return PathUtils.NormalizeRemoteUrl(json["url"]?.ToString());
                }
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateUpscaleImageTool] Upload error: {e}");
                return null;
            }
        }

        private static string GetMimeType(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant();
            switch (ext)
            {
                case ".png":  return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif":  return "image/gif";
                case ".mp3":  return "audio/mpeg";
                case ".wav":  return "audio/wav";
                case ".m4a":  return "audio/mp4";
                default:      return "application/octet-stream";
            }
        }

        private static void ApplyUpscaleParameters(DynamicGenerator generator, JObject parameters)
        {
            if (parameters["scale"] != null)
                generator.SetParameter("scale", parameters["scale"].ToObject<int>());

            if (parameters["model"] != null)
                generator.SetParameter("model", parameters["model"].ToString());

            if (parameters["face_enhance"] != null)
                generator.SetParameter("face", parameters["face_enhance"].ToObject<bool>());

            if (parameters["output_format"] != null)
                generator.SetParameter("outputFormat", parameters["output_format"].ToString());
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
                string uniqueName = "Upscale_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
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

        internal static void ApplyUpscaleParametersInternal(DynamicGenerator generator, JObject parameters)
            => ApplyUpscaleParameters(generator, parameters);
#endif
    }
}
