using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
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
    /// CustomTool for cloning a voice from an audio sample (fal.ai minimax/voice-clone).
    /// Uploads the local audio to TOS, then submits an async voice-clone task.
    /// Output is a custom_voice_id string (not a file asset).
    /// Uses a dedicated VoiceCloneTaskTracker and a custom polling coroutine
    /// (standard pipeline expects a downloadable file, but voice-clone output is a string).
    /// </summary>
    public static class GenerateVoiceCloneTool
    {
        private const string GeneratorId = "voice-clone";

        [ExecuteCustomTool.CustomTool("voice_clone",
            "Clone a voice from an audio sample. " +
            "Returns a custom_voice_id that can be passed to generate_tts as the voice_id parameter. " +
            "Parameters: audio_path (local audio file path, required — mp3/wav/m4a, 10s-5min, <20MB). " +
            "IMPORTANT: No placeholder is returned. A <bg_task_done> notification will arrive upon completion " +
            "containing 'custom_voice_id'.")]
        public static object VoiceClone(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateVoiceCloneTool] Cloning voice with parameters: {parameters}");

                string audioPath = parameters["audio_path"]?.ToString();
                string sessionId = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());

                if (string.IsNullOrEmpty(audioPath))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "'audio_path' parameter is required" }
                    };
                }

                // Resolve local path
                string absPath = ResolveAudioPath(audioPath);
                if (string.IsNullOrEmpty(absPath) || !File.Exists(absPath))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "FILE_NOT_FOUND" },
                        { "message", $"Audio file not found: {audioPath}" }
                    };
                }

                // Load voice-clone generator config
                var config = ConfigManager.GetGeneratorConfig(ConfigType.Music, GeneratorId);
                if (config == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Cannot find generator config for '{GeneratorId}'." }
                    };
                }

                var generator = new DynamicGenerator(config);

                // Upload audio to TOS and get CDN URL
                string cdnUrl = UploadAudioToTOS(absPath);
                if (string.IsNullOrEmpty(cdnUrl))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "UPLOAD_FAILED" },
                        { "message", "Failed to upload audio to TOS" }
                    };
                }

                // Set audioUrl parameter
                generator.SetParameter("audioUrl", cdnUrl);

                // Submit task
                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[GenerateVoiceCloneTool] 任务提交失败 [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[GenerateVoiceCloneTool] 任务提交成功，backend_task_id={submitResult.BackendTaskId}");

                // Create tracked task
                string capturedBackendTaskId = submitResult.BackendTaskId;
                string taskId = VoiceCloneTaskTracker.CreateTask(audioPath, capturedBackendTaskId);

                // Start custom polling coroutine (voice-clone output is a string, not a file)
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    PollVoiceCloneTask(taskId, capturedBackendTaskId, audioPath, sessionId));

                TJLog.Log($"[GenerateVoiceCloneTool] 轮询已启动，task_id={taskId}, backend_task_id={submitResult.BackendTaskId}");

                return new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "message",
                        "Voice clone started. " +
                        "STEP 1 (do now): END THIS RESPONSE TURN immediately. " +
                        "STEP 2 (automatic): A <bg_task_done> notification will appear in your next turn (~30s) " +
                        "containing 'custom_voice_id'. " +
                        "*** POLLING IS STRICTLY FORBIDDEN. Only call query_voice_clone_status ONCE as a last-resort fallback. ***" },
                    { "task_id",            taskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       GeneratorId },
                    { "audio_path",         audioPath },
                    { "estimated_wait_seconds", 30 },
                    { "notification_mode",  "bg_task_done" }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateVoiceCloneTool] Error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error cloning voice: {e.Message}" }
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

        [ExecuteCustomTool.CustomTool("query_voice_clone_status",
            "Query the status of a voice clone task. Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "When completed, returns 'custom_voice_id'. " +
            "Status values: 'generating', 'completed', 'failed'. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QueryVoiceCloneStatus(JObject parameters)
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

                var task = VoiceCloneTaskTracker.GetTask(taskId);
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
                    { "generator_id", GeneratorId },
                    { "status", task.Status },
                    { "progress", task.Progress },
                    { "audio_path", task.AudioPath ?? "" },
                    { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                };

                if (!string.IsNullOrEmpty(task.CustomVoiceId))
                    result["custom_voice_id"] = task.CustomVoiceId;

                if (!string.IsNullOrEmpty(task.PreviewUrl))
                    result["preview_url"] = task.PreviewUrl;
                else
                    result["preview_url"] = PreviewUrlHelper.BuildFixedPreviewUrl(task.BackendTaskId);

                if (!string.IsNullOrEmpty(task.ErrorMessage))
                    result["error"] = task.ErrorMessage;

                if (task.EndTime.HasValue)
                {
                    result["end_time"] = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                    result["duration_seconds"] = (int)(task.EndTime.Value - task.StartTime).TotalSeconds;
                }

                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateVoiceCloneTool] Query error: {e}");
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

        [ExecuteCustomTool.CustomTool("list_voice_clone_tasks", "List all active and recent voice clone tasks")]
        public static object ListVoiceCloneTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var allTasks = VoiceCloneTaskTracker.GetAllTasks();
                var taskList = new List<Dictionary<string, object>>();

                foreach (var task in allTasks)
                {
                    var taskData = new Dictionary<string, object>
                    {
                        { "task_id", task.TaskId },
                        { "generator_id", GeneratorId },
                        { "status", task.Status },
                        { "progress", task.Progress },
                        { "audio_path", task.AudioPath ?? "" },
                        { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                    };

                    if (!string.IsNullOrEmpty(task.CustomVoiceId))
                        taskData["custom_voice_id"] = task.CustomVoiceId;

                    taskData["preview_url"] = !string.IsNullOrEmpty(task.PreviewUrl)
                        ? task.PreviewUrl
                        : PreviewUrlHelper.BuildFixedPreviewUrl(task.BackendTaskId);

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
                TJLog.LogError($"[GenerateVoiceCloneTool] List error: {e}");
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
        /// Custom polling coroutine for voice-clone tasks.
        /// Polls the standard task status endpoint and extracts customVoiceId from the response.
        /// </summary>
        private static IEnumerator PollVoiceCloneTask(string taskId, string backendTaskId, string audioPath, string sessionId)
        {
            string statusUrl = ConfigManager.GetPollStatusUrl(backendTaskId);
            int maxRetries = ConfigManager.GetPollMaxRetries();
            float pollInterval = ConfigManager.GetPollInterval();
            int retryCount = 0;

            while (retryCount < maxRetries)
            {
                retryCount++;
                TJLog.Log($"[GenerateVoiceCloneTool] 轮询 {retryCount}/{maxRetries}");

                string rawJson = null;
                string pollError = null;

                using (UnityWebRequest uwr = UnityWebRequest.Get(statusUrl))
                {
                    string token = UnityConnectSession.instance.GetAccessToken();
                    if (!string.IsNullOrEmpty(token))
                        uwr.SetRequestHeader("Authorization", $"Bearer {token}");
                    uwr.SetRequestHeader("orgId", UnityConnectSession.instance.GetOrgId());
                    uwr.SetRequestHeader("source", "codely");

                    yield return uwr.SendWebRequest();

                    if (UnityWebRequestCompat.IsSuccess(uwr))
                    {
                        rawJson = uwr.downloadHandler.text;
                    }
                    else
                    {
                        pollError = uwr.error;
                    }
                }

                if (!string.IsNullOrEmpty(pollError) || string.IsNullOrEmpty(rawJson))
                {
                    if (retryCount >= maxRetries)
                    {
                        VoiceCloneTaskTracker.MarkFailed(taskId, $"Polling failed: {pollError}");
                        GenerationNotifier.NotifyFailed("voice_clone", taskId, backendTaskId,
                            $"Polling failed after {retryCount} retries: {pollError}",
                            new JObject { ["session_id"] = sessionId, ["generator_id"] = GeneratorId, ["audio_path"] = audioPath ?? "" });
                        yield break;
                    }
                    yield return new EditorWaitForSeconds(pollInterval);
                    continue;
                }

                // Parse response
                JObject responseJson = null;
                try { responseJson = JObject.Parse(rawJson); }
                catch { /* parse failed, handled below */ }

                if (responseJson == null)
                {
                    if (retryCount >= maxRetries)
                    {
                        VoiceCloneTaskTracker.MarkFailed(taskId, "Failed to parse status response");
                        GenerationNotifier.NotifyFailed("voice_clone", taskId, backendTaskId,
                            "Failed to parse status response",
                            new JObject { ["session_id"] = sessionId, ["generator_id"] = GeneratorId, ["audio_path"] = audioPath ?? "" });
                        yield break;
                    }
                    yield return new EditorWaitForSeconds(pollInterval);
                    continue;
                }

                string status = responseJson["status"]?.ToString() ?? "";
                int progress = responseJson["progress"]?.ToObject<int>() ?? 0;

                TJLog.Log($"[GenerateVoiceCloneTool] 任务状态: {status}, 进度: {progress}");

                if (status == "completed")
                {
                    // Extract customVoiceId and previewAudioUrl from output.data
                    string customVoiceId = responseJson["output"]?["data"]?["customVoiceId"]?.ToString() ?? "";
                    string previewAudioUrl = responseJson["output"]?["data"]?["previewAudioUrl"]?.ToString() ?? "";

                    if (string.IsNullOrEmpty(customVoiceId))
                    {
                        TJLog.LogWarning("[GenerateVoiceCloneTool] customVoiceId not found in response, checking alternative paths");
                        // Try nested result path
                        customVoiceId = responseJson["output"]?["data"]?["result"]?["customVoiceId"]?.ToString() ?? "";
                    }

                    VoiceCloneTaskTracker.MarkCompleted(taskId, customVoiceId, previewAudioUrl);
                    var t = VoiceCloneTaskTracker.GetTask(taskId);

                    GenerationNotifier.NotifyCompleted("voice_clone", taskId, backendTaskId,
                        new JObject
                        {
                            ["session_id"]       = sessionId,
                            ["generator_id"]     = GeneratorId,
                            ["custom_voice_id"]  = customVoiceId ?? "",
                            ["preview_url"]      = previewAudioUrl ?? "",
                            ["audio_path"]       = audioPath ?? "",
                            ["progress"]         = 100,
                            ["start_time"]       = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                            ["end_time"]         = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                            ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
                        });
                    yield break;
                }
                else if (status == "failed" || status == "error" || status == "cancelled")
                {
                    string error = responseJson["error"]?.ToString() ?? responseJson["message"]?.ToString() ?? $"Task {status}";
                    VoiceCloneTaskTracker.MarkFailed(taskId, error);
                    GenerationNotifier.NotifyFailed("voice_clone", taskId, backendTaskId, error,
                        new JObject { ["session_id"] = sessionId, ["generator_id"] = GeneratorId, ["audio_path"] = audioPath ?? "" });
                    yield break;
                }

                // Still generating, wait and retry
                yield return new EditorWaitForSeconds(pollInterval);
            }

            // Timeout
            VoiceCloneTaskTracker.MarkFailed(taskId, "Polling timeout");
            GenerationNotifier.NotifyFailed("voice_clone", taskId, backendTaskId,
                $"Polling timeout after {maxRetries} retries",
                new JObject { ["session_id"] = sessionId, ["generator_id"] = GeneratorId, ["audio_path"] = audioPath ?? "" });
        }

        private static string ResolveAudioPath(string audioPath)
        {
            if (string.IsNullOrEmpty(audioPath))
                return null;
            if (Path.IsPathRooted(audioPath))
                return File.Exists(audioPath) ? audioPath : null;
            if (audioPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                string absPath = Path.Combine(Application.dataPath.Replace("Assets", ""), audioPath).Replace("\\", "/");
                return File.Exists(absPath) ? absPath : null;
            }
            string fallback = Path.Combine(Application.dataPath, audioPath).Replace("\\", "/");
            return File.Exists(fallback) ? fallback : null;
        }

        private static string UploadAudioToTOS(string absPath)
        {
            string url = ConfigManager.GetApiBaseUrl() + "upload/audio";
            string token = UnityConnectSession.instance.GetAccessToken();
            if (string.IsNullOrEmpty(token))
            {
                TJLog.LogError("[GenerateVoiceCloneTool] Not logged in");
                return null;
            }

            try
            {
                byte[] fileBytes = File.ReadAllBytes(absPath);
                string fileName = Path.GetFileName(absPath);
                string boundary = "----TJGenBoundary" + DateTime.Now.Ticks;

                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(120);
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
                    client.DefaultRequestHeaders.Add("orgId", UnityConnectSession.instance.GetOrgId());
                    client.DefaultRequestHeaders.Add("source", "codely");

                    var form = new System.Net.Http.MultipartFormDataContent(boundary);
                    var fileContent = new System.Net.Http.ByteArrayContent(fileBytes);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                        GetAudioMimeType(absPath));
                    form.Add(fileContent, "audio", fileName);

                    var response = client.PostAsync(url, form).Result;
                    string body = response.Content.ReadAsStringAsync().Result;

                    if (!response.IsSuccessStatusCode)
                    {
                        TJLog.LogError($"[GenerateVoiceCloneTool] Upload failed: {response.StatusCode} {body}");
                        return null;
                    }

                    var json = JObject.Parse(body);
                    return PathUtils.NormalizeRemoteUrl(json["url"]?.ToString());
                }
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateVoiceCloneTool] Upload error: {e}");
                return null;
            }
        }

        private static string GetAudioMimeType(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant();
            switch (ext)
            {
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".m4a": return "audio/mp4";
                case ".aac": return "audio/aac";
                case ".ogg": return "audio/ogg";
                default:     return "application/octet-stream";
            }
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>
    /// Tracks voice clone tasks. Output is a custom_voice_id string, not a file asset.
    /// </summary>
    public static class VoiceCloneTaskTracker
    {
        private static readonly Dictionary<string, VoiceCloneTaskInfo> _activeTasks = new Dictionary<string, VoiceCloneTaskInfo>();
        private static int _taskIdCounter = 0;

        private const string SessionKeyIds = "TJGen_VoiceClone_Ids";
        private const string SessionKeyFmt = "TJGen_VoiceClone_{0}";

        [Serializable]
        private class PersistedTask
        {
            public string taskId;
            public string audioPath;
            public string status;
            public int progress;
            public string customVoiceId;
            public string previewUrl;
            public string errorMessage;
            public string backendTaskId;
            public long startTimeTicks;
            public long endTimeTicks;
        }

        public class VoiceCloneTaskInfo
        {
            public string TaskId { get; set; }
            public string AudioPath { get; set; }
            public string Status { get; set; }
            public int Progress { get; set; }
            public string CustomVoiceId { get; set; }
            public string PreviewUrl { get; set; }
            public string ErrorMessage { get; set; }
            public string BackendTaskId { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
        }

        public static string CreateTask(string audioPath, string backendTaskId)
        {
            string taskId = $"voice_clone_{++_taskIdCounter}_{DateTime.Now.Ticks}";
            var task = new VoiceCloneTaskInfo
            {
                TaskId = taskId,
                AudioPath = audioPath ?? "",
                BackendTaskId = backendTaskId ?? "",
                Status = "generating",
                StartTime = DateTime.Now
            };
            _activeTasks[taskId] = task;
            SaveToSession(task);
            return taskId;
        }

        public static void MarkCompleted(string taskId, string customVoiceId, string previewUrl)
        {
            if (_activeTasks.TryGetValue(taskId, out var task))
            {
                task.Status = "completed";
                task.Progress = 100;
                task.CustomVoiceId = customVoiceId;
                task.PreviewUrl = previewUrl;
                task.EndTime = DateTime.Now;
                SaveToSession(task);
            }
        }

        public static void MarkFailed(string taskId, string errorMessage)
        {
            if (_activeTasks.TryGetValue(taskId, out var task))
            {
                task.Status = "failed";
                task.ErrorMessage = errorMessage;
                task.EndTime = DateTime.Now;
                SaveToSession(task);
            }
        }

        public static VoiceCloneTaskInfo GetTask(string taskId)
        {
            if (_activeTasks.TryGetValue(taskId, out var task)) return task;
            return TryRestoreFromSession(taskId);
        }

        public static List<VoiceCloneTaskInfo> GetAllTasks()
        {
            string ids = SessionState.GetString(SessionKeyIds, "");
            if (!string.IsNullOrEmpty(ids))
            {
                foreach (var id in ids.Split('|'))
                {
                    if (!string.IsNullOrEmpty(id) && !_activeTasks.ContainsKey(id))
                        TryRestoreFromSession(id);
                }
            }
            return new List<VoiceCloneTaskInfo>(_activeTasks.Values);
        }

        private static void SaveToSession(VoiceCloneTaskInfo info)
        {
            var p = new PersistedTask
            {
                taskId = info.TaskId,
                audioPath = info.AudioPath ?? "",
                status = info.Status,
                progress = info.Progress,
                customVoiceId = info.CustomVoiceId ?? "",
                previewUrl = info.PreviewUrl ?? "",
                errorMessage = info.ErrorMessage ?? "",
                backendTaskId = info.BackendTaskId ?? "",
                startTimeTicks = info.StartTime.Ticks,
                endTimeTicks = info.EndTime?.Ticks ?? 0
            };
            SessionState.SetString(string.Format(SessionKeyFmt, info.TaskId), JsonUtility.ToJson(p));
            string ids = SessionState.GetString(SessionKeyIds, "");
            if (!ids.Contains(info.TaskId))
                SessionState.SetString(SessionKeyIds, string.IsNullOrEmpty(ids) ? info.TaskId : ids + "|" + info.TaskId);
        }

        private static VoiceCloneTaskInfo TryRestoreFromSession(string taskId)
        {
            string json = SessionState.GetString(string.Format(SessionKeyFmt, taskId), "");
            if (string.IsNullOrEmpty(json)) return null;
            PersistedTask p;
            try { p = JsonUtility.FromJson<PersistedTask>(json); }
            catch { return null; }

            var info = new VoiceCloneTaskInfo
            {
                TaskId = p.taskId,
                AudioPath = p.audioPath,
                Status = p.status,
                Progress = p.progress,
                CustomVoiceId = p.customVoiceId,
                PreviewUrl = p.previewUrl,
                ErrorMessage = p.errorMessage,
                BackendTaskId = p.backendTaskId,
                StartTime = new DateTime(p.startTimeTicks),
                EndTime = p.endTimeTicks > 0 ? (DateTime?)new DateTime(p.endTimeTicks) : null
            };

            if (info.Status == "generating" || info.Status == "recovering")
            {
                info.Status = "interrupted";
                info.ErrorMessage = TJGeneratorsL10n.L("生成因域重载中断，请重新生成。");
                info.EndTime = DateTime.Now;
                SaveToSession(info);
            }

            _activeTasks[taskId] = info;
            return info;
        }
    }
#endif
}
