using System;
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
#endif

namespace UnityTcp.Editor.Tools
{
    public static class SpriteSequenceTaskTracker
    {
#if UNITY_EDITOR
        [Serializable]
        private class PersistedTask
        {
            public string taskId;
            public string generatorId;
            public string imagePath;
            public string animationType;
            public int    fps;
            public bool   loop;
            public string status;
            public int    progress;
            public string animationClipPath;
            public string folderPath;
            public string errorMessage;
            public long   startTimeTicks;
            public long   endTimeTicks;
            public string previewUrl;
            public string backendTaskId;
        }

        public class SpriteSequenceTaskInfo : IGenerationTaskInfo
        {
            public string TaskId { get; set; }
            public string GeneratorId { get; set; }
            public string ImagePath { get; set; }
            public string AnimationType { get; set; }
            public int Fps { get; set; }
            public bool Loop { get; set; }
            public string Status { get; set; }
            public int Progress { get; set; }
            public string AnimationClipPath { get; set; }
            public string FolderPath { get; set; }
            public string ErrorMessage { get; set; }
            public string PreviewUrl { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public string BackendTaskId { get; set; }
        }

        private static readonly GenerationTaskTrackerStore<SpriteSequenceTaskInfo, PersistedTask> Store =
            new GenerationTaskTrackerStore<SpriteSequenceTaskInfo, PersistedTask>(
                "TJGen_SpriteSeq", BuildPersisted, FromPersisted);

        internal static bool RemoveActiveTaskFromMemoryForTests(string taskId) =>
            Store.RemoveActiveTaskFromMemoryOnly(taskId);

        private static PersistedTask BuildPersisted(SpriteSequenceTaskInfo info) => new PersistedTask
        {
            taskId            = info.TaskId,
            generatorId       = info.GeneratorId,
            imagePath         = info.ImagePath ?? "",
            animationType     = info.AnimationType ?? "",
            fps               = info.Fps,
            loop              = info.Loop,
            status            = info.Status,
            progress          = info.Progress,
            animationClipPath = info.AnimationClipPath ?? "",
            folderPath        = info.FolderPath ?? "",
            errorMessage      = info.ErrorMessage ?? "",
            startTimeTicks    = info.StartTime.Ticks,
            endTimeTicks      = info.EndTime?.Ticks ?? 0,
            previewUrl        = info.PreviewUrl ?? "",
            backendTaskId     = info.BackendTaskId ?? ""
        };

        private static SpriteSequenceTaskInfo FromPersisted(PersistedTask p) => new SpriteSequenceTaskInfo
        {
            TaskId            = p.taskId,
            GeneratorId       = p.generatorId,
            ImagePath         = p.imagePath,
            AnimationType     = p.animationType,
            Fps               = p.fps,
            Loop              = p.loop,
            Status            = p.status,
            Progress          = p.progress,
            AnimationClipPath = p.animationClipPath,
            FolderPath        = p.folderPath,
            ErrorMessage      = p.errorMessage,
            PreviewUrl        = p.previewUrl,
            StartTime         = new DateTime(p.startTimeTicks),
            EndTime           = p.endTimeTicks > 0 ? (DateTime?)new DateTime(p.endTimeTicks) : null,
            BackendTaskId     = p.backendTaskId
        };

        internal static void ApplyTaskUpdate(SpriteSequenceTaskInfo task, Action<SpriteSequenceTaskInfo> mutate) =>
            Store.ApplyTaskUpdate(task, mutate);

        public static string CreateTask(string generatorId, string imagePath, string animationType, int fps, bool loop, TJGeneratorsTaskHandle handle, string sessionId = "", string backendTaskId = "")
        {
            string taskId = Store.AllocateTaskId("sprite_sequence");
            var task = new SpriteSequenceTaskInfo
            {
                TaskId = taskId,
                GeneratorId = generatorId,
                ImagePath = imagePath ?? "",
                AnimationType = animationType ?? "idle",
                Fps = fps,
                Loop = loop,
                Status = "generating",
                Progress = 0,
                StartTime = DateTime.Now,
                BackendTaskId = backendTaskId
            };
            Store.RegisterTask(taskId, task);

            handle.OnProgress += (h) =>
            {
                Store.ApplyTaskUpdate(task, t =>
                {
                    if (h.Status != "completed" && h.Status != "failed")
                        t.Status = h.Status;
                    t.Progress = h.Progress;
                    if (!string.IsNullOrEmpty(h.PreviewUrl))
                        t.PreviewUrl = h.PreviewUrl;
                });
            };

            handle.OnCompleted += (h) =>
            {
                NotifyTaskCompleted(task, taskId, backendTaskId, h.ModelPath, h.PreviewUrl, sessionId);
            };

            handle.OnFailed += (h) =>
            {
                string generatorIdForNotify = task.GeneratorId ?? "";
                Store.ApplyTaskUpdate(task, t =>
                {
                    t.Status = "failed";
                    t.ErrorMessage = h.ErrorMessage;
                    t.EndTime = DateTime.Now;
                });

                GenerationNotifier.NotifyFailed("generate_sprite_sequence", taskId, backendTaskId, h.ErrorMessage,
                    new JObject { ["session_id"] = sessionId, ["generator_id"] = generatorIdForNotify });
            };

            return taskId;
        }

        public static SpriteSequenceTaskInfo GetTask(string taskId) => Store.GetTask(taskId);

        public static List<SpriteSequenceTaskInfo> GetAllTasks() => Store.GetAllTasks();

        public static SpriteSequenceTaskInfo GetTaskByBackendId(string backendTaskId) =>
            Store.GetTaskByBackendId(backendTaskId);

        public static SpriteSequenceTaskInfo CreateRecoveredTask(
            string backendTaskId,
            string generatorId,
            string imagePath,
            string animationType,
            int fps,
            bool loop,
            long timestampMs)
        {
            return Store.CreateRecoveredTask(backendTaskId, () =>
            {
                if (string.IsNullOrEmpty(animationType))
                {
                    TJLog.LogWarning("[GenerateSpriteSequenceTool] CreateRecoveredTask 收到空的 animation_type，回退 idle。");
                    animationType = "idle";
                }

                return new SpriteSequenceTaskInfo
                {
                    TaskId          = $"recovered_{backendTaskId}",
                    BackendTaskId   = backendTaskId,
                    GeneratorId     = generatorId ?? "",
                    ImagePath       = imagePath ?? "",
                    AnimationType   = animationType,
                    Fps             = fps > 0 ? fps : 12,
                    Loop            = loop,
                    Status          = "recovering",
                    Progress        = 0,
                    StartTime       = timestampMs > 0
                                        ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).LocalDateTime
                                        : DateTime.Now
                };
            });
        }

        internal static void MarkTaskCompleted(
            SpriteSequenceTaskInfo task, string clipPath, string previewUrl, string sessionId)
        {
            string folderPath = null;
            Store.ApplyTaskUpdate(task, t =>
            {
                t.Status = "completed";
                t.Progress = 100;
                t.AnimationClipPath = clipPath;
                t.FolderPath = string.IsNullOrEmpty(clipPath) ? "" : Path.GetDirectoryName(clipPath)?.Replace('\\', '/');
                t.PreviewUrl = previewUrl;
                t.EndTime = DateTime.Now;
                folderPath = t.FolderPath;
            });

            if (!string.IsNullOrEmpty(clipPath))
                TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(clipPath));

            if (!string.IsNullOrEmpty(folderPath))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folderPath }))
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(assetPath));
                    TJGeneratorsGenerationLabel.EnableSessionLabel(
                        TJGeneratorsAssetReference.FromPath(assetPath), sessionId);
                }
            }

            if (!string.IsNullOrEmpty(clipPath))
                TJGeneratorsGenerationLabel.EnableSessionLabel(
                    TJGeneratorsAssetReference.FromPath(clipPath), sessionId);
        }

        internal static SpriteSequenceTaskInfo SelectPrimaryTaskForNotification(
            IList<SpriteSequenceTaskInfo> tasks, string preferredTaskId = null)
        {
            if (tasks == null || tasks.Count == 0) return null;

            if (!string.IsNullOrEmpty(preferredTaskId))
            {
                var preferred = tasks.FirstOrDefault(t => t.TaskId == preferredTaskId);
                if (preferred != null) return preferred;
            }

            var original = tasks.FirstOrDefault(t =>
                t.TaskId.StartsWith("sprite_sequence_", StringComparison.OrdinalIgnoreCase));
            if (original != null) return original;

            return tasks[0];
        }

        internal static int CountFrameSprites(string folderPath)
        {
            return string.IsNullOrEmpty(folderPath)
                ? 0
                : AssetDatabase.FindAssets("t:Sprite", new[] { folderPath }).Length;
        }

        internal static Dictionary<string, object> BuildTaskStatusDictionary(SpriteSequenceTaskInfo task)
        {
            var result = new Dictionary<string, object>
            {
                { "task_id", task.TaskId },
                { "generator_id", task.GeneratorId ?? "" },
                { "status", task.Status },
                { "progress", task.Progress },
                { "image_path", task.ImagePath ?? "" },
                { "animation_type", task.AnimationType ?? "" },
                { "fps", task.Fps },
                { "loop", task.Loop },
                { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") },
                { "preview_url", PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId) },
            };

            if (!string.IsNullOrEmpty(task.BackendTaskId))
                result["backend_task_id"] = task.BackendTaskId;

            if (!string.IsNullOrEmpty(task.AnimationClipPath))
                result["animation_clip_path"] = task.AnimationClipPath;
            if (!string.IsNullOrEmpty(task.FolderPath))
                result["folder_path"] = task.FolderPath;

            if (string.Equals(task.Status, "completed", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(task.FolderPath))
            {
                result["frame_count"] = CountFrameSprites(task.FolderPath);
            }

            if (!string.IsNullOrEmpty(task.ErrorMessage))
                result["error"] = task.ErrorMessage;

            if (task.EndTime.HasValue)
            {
                result["end_time"] = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                result["duration_seconds"] = (int)(task.EndTime.Value - task.StartTime).TotalSeconds;
            }

            return result;
        }

        internal static void SendCompletionNotification(
            SpriteSequenceTaskInfo task, string taskId, string backendTaskId, string previewUrl, string sessionId)
        {
            int frameCount = CountFrameSprites(task.FolderPath);

            GenerationNotifier.NotifyCompleted("generate_sprite_sequence", taskId, backendTaskId,
                new JObject
                {
                    ["session_id"]          = sessionId,
                    ["generator_id"]        = task.GeneratorId ?? "",
                    ["folder_path"]         = task.FolderPath ?? "",
                    ["animation_clip_path"] = task.AnimationClipPath ?? "",
                    ["frame_count"]         = frameCount,
                    ["preview_url"]         = previewUrl ?? "",
                    ["progress"]            = 100,
                    ["start_time"]          = task.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["end_time"]            = task.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                    ["duration_seconds"]    = task.EndTime.HasValue ? (int)(task.EndTime.Value - task.StartTime).TotalSeconds : 0
                });
        }

        internal static void NotifyTaskCompleted(
            SpriteSequenceTaskInfo task, string taskId, string backendTaskId, string clipPath, string previewUrl, string sessionId)
        {
            MarkTaskCompleted(task, clipPath, previewUrl, sessionId);
            SendCompletionNotification(task, taskId, backendTaskId, previewUrl, sessionId);
        }

        public static void RemoveTask(string taskId) => Store.RemoveTask(taskId);

        public static void CleanupCompletedTasks() => Store.CleanupCompletedTasks();
#endif
    }

    public static class GenerateSpriteSequenceTool
    {
        [ExecuteCustomTool.CustomTool("generate_sprite_sequence",
            "Generate a 2D sprite sequence animation (multiple Sprite frames + AnimationClip) from a character reference image using AI. " +
            "Output is saved to Assets/TJGenerators/History/Sequence_xxx/ as individual Sprite PNGs and a .anim AnimationClip asset. " +
            "Parameters: " +
            "image_path (REQUIRED — absolute path or Assets-relative path of the character reference image), " +
            "generator_id (optional, default 'sprite_sequence_v1'), " +
            "animation_type (optional animation action; valid values: 'idle' (idle), 'frontRun' (run forward), 'backRun' (run backward); default 'idle'), " +
            "fps (optional frames per second for the AnimationClip, integer 1-60, default 12), " +
            "loop (optional bool, whether the AnimationClip loops, default true). " +
            "NOTE: image_path is mandatory — the API only accepts image input for sprite sequence generation. " +
            "IMPORTANT ASYNC WORKFLOW: " +
            "(1) Call this tool to start generation; note task_id and end your response turn immediately. " +
            "(2) *** POLLING IS STRICTLY FORBIDDEN. NEVER call `query_sprite_sequence_status` in a loop or repeatedly. *** " +
            "    Only call `query_sprite_sequence_status` ONCE as a last-resort fallback if no <bg_task_done> notification arrives after ~90 seconds. " +
            "(3) A <bg_task_done> notification will arrive automatically in your next turn when generation finishes (~1-3 minutes). " +
            "    The notification payload contains ALL result fields (folder_path, animation_clip_path, frame_count, backend_task_id, preview_url, timing, etc.). " +
            "    If you receive the notification, the task is done — do NOT call `query_sprite_sequence_status` under any circumstances.")]
        public static object GenerateSpriteSequence(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateSpriteSequenceTool] Generating sprite sequence with parameters: {parameters}");

                string generatorId = parameters["generator_id"]?.ToString() ?? "sprite_sequence_v1";
                string imagePath = parameters["image_path"]?.ToString();
                string animationType = parameters["animation_type"]?.ToString() ?? "idle";
                string sessionId = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());
                int fps = 12;
                bool loop = true;

                if (parameters["fps"] != null)
                {
                    if (!int.TryParse(parameters["fps"].ToString(), out fps))
                    {
                        TJLog.LogWarning($"[GenerateSpriteSequenceTool] Invalid fps value '{parameters["fps"]}', using default: 12");
                        fps = 12;
                    }
                    fps = Mathf.Clamp(fps, 1, 60);
                }

                if (parameters["loop"] != null)
                {
                    if (!bool.TryParse(parameters["loop"].ToString(), out loop))
                    {
                        TJLog.LogWarning($"[GenerateSpriteSequenceTool] Invalid loop value '{parameters["loop"]}', using default: true");
                        loop = true;
                    }
                }

                if (string.IsNullOrEmpty(imagePath))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "'image_path' is required for sprite sequence generation. Provide the path to a character reference image." }
                    };
                }

                // Validate animation_type
                var validAnimTypes = new HashSet<string> { "idle", "frontRun", "backRun" };
                if (!validAnimTypes.Contains(animationType))
                {
                    TJLog.LogWarning($"[GenerateSpriteSequenceTool] Unknown animation_type '{animationType}', falling back to 'idle'. Valid values: idle, frontRun, backRun.");
                    animationType = "idle";
                }

                // Load sprite sequence generator config
                var config = ConfigManager.GetGeneratorConfig(ConfigType.SpriteSequence, generatorId);
                if (config == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Cannot find sprite sequence generator config for '{generatorId}'. Valid value: 'sprite_sequence_v1'." }
                    };
                }

                // Create generator and set inputs
                var generator = new DynamicGenerator(config);
                generator.SetImagePath(imagePath);
                generator.SetParameter("animation_type", animationType);
                generator.SetParameter("fps", fps);
                generator.SetParameter("loop", loop);

                // 阶段1：同步提交任务到后端，立即获取 backendTaskId 或失败原因
                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[GenerateSpriteSequenceTool] 任务提交失败 [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[GenerateSpriteSequenceTool] 任务提交成功，backend_task_id={submitResult.BackendTaskId}");

                // 阶段2：异步轮询（跳过提交）
                var context = new TJGeneratorsGenerationContext
                {
                    TargetAsset = null,
                    AutoCreateTargetPrefab = false
                };
                var handle = TJGeneratorsGenerationService.GenerateFromSubmittedTask(
                    generator, context, submitResult.BackendTaskId, sessionId, "generate_sprite_sequence");

                // Create tracked task; subscribes to handle events internally for progress updates
                string taskId = SpriteSequenceTaskTracker.CreateTask(generatorId, imagePath, animationType, fps, loop, handle, sessionId, submitResult.BackendTaskId);

                TJLog.Log($"[GenerateSpriteSequenceTool] 轮询已启动，task_id={taskId}, backend_task_id={submitResult.BackendTaskId}");

                return new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "message",
                        "Sprite sequence generation started. " +
                        "STEP 1 (do now): Note the task_id for later retrieval. " +
                        "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                        "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn (~90s) " +
                        "containing ALL generation results (folder_path, animation_clip_path, frame_count, timing, etc.). " +
                        "*** POLLING IS STRICTLY FORBIDDEN — do NOT call query_sprite_sequence_status repeatedly. " +
                        "Only call query_sprite_sequence_status ONCE as a one-time fallback if no notification arrives. ***" },
                    { "task_id",            taskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       generatorId },
                    { "image_path",         imagePath },
                    { "animation_type",     animationType },
                    { "fps",                fps },
                    { "loop",               loop },
                    { "preview_url",        PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "estimated_wait_seconds", 90 },
                    { "notification_mode",  "bg_task_done" }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateSpriteSequenceTool] Error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error generating sprite sequence: {e.Message}" }
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

        [ExecuteCustomTool.CustomTool("query_sprite_sequence_status",
            "Query the status of a sprite sequence generation task. Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "When completed, returns the same result fields as the bg_task_done notification: folder_path, animation_clip_path, frame_count, backend_task_id, preview_url, timing fields, etc. " +
            "Status values: 'generating', 'recovering', 'completed', 'failed', 'interrupted'. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QuerySpriteSequenceStatus(JObject parameters)
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

                var task = SpriteSequenceTaskTracker.GetTask(taskId);

                if (task == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Task '{taskId}' not found. It may have been completed and cleaned up." }
                    };
                }

                var result = SpriteSequenceTaskTracker.BuildTaskStatusDictionary(task);
                result["success"] = true;
                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateSpriteSequenceTool] Query error: {e}");
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

        [ExecuteCustomTool.CustomTool("list_sprite_sequence_tasks", "List all active and recent sprite sequence generation tasks")]
        public static object ListSpriteSequenceTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks = SpriteSequenceTaskTracker.GetAllTasks();
                var taskList = new List<Dictionary<string, object>>();

                foreach (var task in tasks)
                {
                    taskList.Add(SpriteSequenceTaskTracker.BuildTaskStatusDictionary(task));
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
                TJLog.LogError($"[GenerateSpriteSequenceTool] List error: {e}");
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
    }

#if UNITY_EDITOR
    internal static class SpriteSequenceRecoverySupport
    {
        private static readonly HashSet<string> ValidAnimationTypes = new HashSet<string>
        {
            "idle", "frontRun", "backRun"
        };

        internal static void ApplyGeneratorParameters(
            DynamicGenerator generator, SpriteSequenceTaskTracker.SpriteSequenceTaskInfo trackerTask, InterruptedTaskData interrupted)
        {
            string imagePath = !string.IsNullOrEmpty(trackerTask?.ImagePath) ? trackerTask.ImagePath : interrupted?.imagePath;
            if (!string.IsNullOrEmpty(imagePath))
                generator.SetImagePath(imagePath);

            generator.SetParameter("animation_type", ResolveAnimationType(trackerTask, interrupted));
            generator.SetParameter("fps", ResolveFps(trackerTask, interrupted));
            generator.SetParameter("loop", ResolveLoop(trackerTask, interrupted));
        }

        internal static void BackfillTrackerFromInterrupted(
            SpriteSequenceTaskTracker.SpriteSequenceTaskInfo trackerTask, InterruptedTaskData interrupted)
        {
            if (trackerTask == null || interrupted == null) return;

            bool needsAnimation = string.IsNullOrEmpty(trackerTask.AnimationType) && !string.IsNullOrEmpty(interrupted.animationType);
            bool needsFps = trackerTask.Fps <= 0 && interrupted.fps > 0;
            if (!needsAnimation && !needsFps) return;

            SpriteSequenceTaskTracker.ApplyTaskUpdate(trackerTask, t =>
            {
                if (needsAnimation)
                    t.AnimationType = NormalizeAnimationType(interrupted.animationType);
                if (needsFps)
                    t.Fps = interrupted.fps;
            });
        }

        internal static string ResolveAnimationType(
            SpriteSequenceTaskTracker.SpriteSequenceTaskInfo trackerTask, InterruptedTaskData interrupted)
        {
            if (!string.IsNullOrEmpty(trackerTask?.AnimationType))
                return NormalizeAnimationType(trackerTask.AnimationType);

            if (!string.IsNullOrEmpty(interrupted?.animationType))
                return NormalizeAnimationType(interrupted.animationType);

            TJLog.LogWarning("[GenerateSpriteSequenceTool] 恢复任务缺少 animation_type（Session 与 InterruptedTasks 均无），回退 idle。");
            return "idle";
        }

        internal static int ResolveFps(
            SpriteSequenceTaskTracker.SpriteSequenceTaskInfo trackerTask, InterruptedTaskData interrupted)
        {
            if (trackerTask != null && trackerTask.Fps > 0)
                return trackerTask.Fps;

            if (interrupted != null && interrupted.fps > 0)
                return interrupted.fps;

            TJLog.LogWarning("[GenerateSpriteSequenceTool] 恢复任务缺少 fps（Session 与 InterruptedTasks 均无），回退 12。");
            return 12;
        }

        internal static bool ResolveLoop(
            SpriteSequenceTaskTracker.SpriteSequenceTaskInfo trackerTask, InterruptedTaskData interrupted)
        {
            if (trackerTask != null)
                return trackerTask.Loop;

            if (interrupted != null && interrupted.loopSpecified)
                return interrupted.loop;

            TJLog.LogWarning("[GenerateSpriteSequenceTool] 恢复任务缺少 loop（Session 与 InterruptedTasks 均无），回退 true。");
            return true;
        }

        private static string NormalizeAnimationType(string animationType)
        {
            if (ValidAnimationTypes.Contains(animationType))
                return animationType;

            TJLog.LogWarning(
                $"[GenerateSpriteSequenceTool] 未知 animation_type '{animationType}'，回退 idle。有效值: idle, frontRun, backRun。");
            return "idle";
        }
    }

    [InitializeOnLoad]
    public static class SpriteSequenceDomainReloadRecovery
    {
        static SpriteSequenceDomainReloadRecovery()
        {
            CustomToolDomainReloadRecovery.Schedule(ResumeInterruptedTasks);
        }

        private static void ResumeInterruptedTasks()
        {
            CustomToolDomainReloadRecovery.Resume(
                "GenerateSpriteSequenceTool",
                ConfigType.SpriteSequence,
                t => t.toolName == "generate_sprite_sequence",
                () => SpriteSequenceTaskTracker.GetAllTasks(),
                (interrupted, _, generator) =>
                {
                    var trackerTask = SpriteSequenceTaskTracker.GetTaskByBackendId(interrupted.backendTaskId);
                    if (trackerTask != null)
                    {
                        SpriteSequenceRecoverySupport.BackfillTrackerFromInterrupted(trackerTask, interrupted);
                        CustomToolDomainReloadRecovery.MarkTrackerRecoveringIfNeeded(trackerTask.Status, () =>
                        {
                            SpriteSequenceTaskTracker.ApplyTaskUpdate(trackerTask, t => t.Status = "recovering");
                        });
                    }
                    else
                    {
                        trackerTask = SpriteSequenceTaskTracker.CreateRecoveredTask(
                            interrupted.backendTaskId,
                            interrupted.modelVersion,
                            interrupted.imagePath,
                            SpriteSequenceRecoverySupport.ResolveAnimationType(null, interrupted),
                            SpriteSequenceRecoverySupport.ResolveFps(null, interrupted),
                            SpriteSequenceRecoverySupport.ResolveLoop(null, interrupted),
                            interrupted.timestamp);
                    }

                    SpriteSequenceRecoverySupport.ApplyGeneratorParameters(generator, trackerTask, interrupted);

                    string sessionId = interrupted.sessionId ?? "";
                    string capturedBackendTaskId = interrupted.backendTaskId;
                    string taskId = trackerTask.TaskId;

                    var host = new SpriteSequenceRecoveryHost(
                        capturedBackendTaskId, sessionId, taskId, generator);
                    CustomToolDomainReloadRecovery.StartPolling(
                        "GenerateSpriteSequenceTool", host, ConfigType.SpriteSequence,
                        sessionId, "generate_sprite_sequence", generator, interrupted.backendTaskId);
                });
        }
    }

    internal class SpriteSequenceRecoveryHost : HeadlessPipelineHostBase
    {
        private readonly string _backendTaskId;
        private readonly string _sessionId;
        private readonly string _taskId;
        private readonly ModelGeneratorBase _generator;

        public SpriteSequenceRecoveryHost(string backendTaskId, string sessionId, string taskId, ModelGeneratorBase generator)
        {
            _backendTaskId = backendTaskId;
            _sessionId     = sessionId ?? "";
            _taskId        = taskId;
            _generator     = generator;
        }

        protected override string DialogLogTag => "SpriteSequenceRecovery";

        public override TJGeneratorsAssetReference GetTargetAsset() => null;

        public override void Repaint()
        {
            if (_generator == null) return;

            var trackerTask = SpriteSequenceTaskTracker.GetTaskByBackendId(_backendTaskId);
            if (trackerTask == null || !TJGeneratorsTaskRecovery.IsRecoverableTrackerStatus(trackerTask.Status)) return;

            int progress = _generator.CurrentProgress;
            string previewUrl = _generator.CurrentPreviewUrl;
            if (progress <= trackerTask.Progress && string.IsNullOrEmpty(previewUrl)) return;

            SpriteSequenceTaskTracker.ApplyTaskUpdate(trackerTask, t =>
            {
                if (progress > t.Progress)
                {
                    t.Status = "generating";
                    t.Progress = progress;
                }

                if (!string.IsNullOrEmpty(previewUrl))
                    t.PreviewUrl = previewUrl;
            });
        }

        public override void ShowDialog(string title, string message)
        {
            base.ShowDialog(title, message);

            if (!ErrorDialogUtils.IsErrorDialog(title)) return;

            var trackerTask = SpriteSequenceTaskTracker.GetTaskByBackendId(_backendTaskId);
            if (trackerTask == null) return;

            var friendlyError = ErrorDialogUtils.ConvertToUserFriendlyError(title, message);
            SpriteSequenceTaskTracker.ApplyTaskUpdate(trackerTask, t =>
            {
                t.Status = "failed";
                t.ErrorMessage = friendlyError.TechnicalMessage;
                t.EndTime = DateTime.Now;
            });

            GenerationNotifier.NotifyFailed(
                "generate_sprite_sequence",
                _taskId,
                _backendTaskId,
                friendlyError.TechnicalMessage,
                new JObject
                {
                    ["session_id"]   = _sessionId,
                    ["generator_id"] = trackerTask.GeneratorId ?? ""
                });
        }

        public override void OnGenerationCompleted(string assetPath)
        {
            var tasksToUpdate = new List<SpriteSequenceTaskTracker.SpriteSequenceTaskInfo>();
            var byBackend = SpriteSequenceTaskTracker.GetTaskByBackendId(_backendTaskId);
            if (byBackend != null) tasksToUpdate.Add(byBackend);

            foreach (var t in SpriteSequenceTaskTracker.GetAllTasks())
            {
                if (!tasksToUpdate.Contains(t) &&
                    t.BackendTaskId == _backendTaskId &&
                    TJGeneratorsTaskRecovery.IsRecoverableTrackerStatus(t.Status))
                {
                    tasksToUpdate.Add(t);
                }
            }

            string previewUrl = _generator?.CurrentPreviewUrl;
            foreach (var trackerTask in tasksToUpdate)
            {
                SpriteSequenceTaskTracker.MarkTaskCompleted(
                    trackerTask, assetPath, previewUrl, _sessionId);
            }

            var notifyTask = SpriteSequenceTaskTracker.SelectPrimaryTaskForNotification(tasksToUpdate, _taskId);
            if (notifyTask != null)
            {
                SpriteSequenceTaskTracker.SendCompletionNotification(
                    notifyTask, notifyTask.TaskId, _backendTaskId, previewUrl, _sessionId);
            }
        }
    }
#endif
}
