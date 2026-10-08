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
using Unity.EditorCoroutines.Editor;
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Tracks active static 3D model generation tasks (rodin / Rodin Gen-2.5 and tripo-p1 / Tripo P1).
    /// Uses SessionState to persist task info across script recompilation (domain reload).
    /// After domain reload, in-progress tasks are automatically resumed via TJGeneratorsTaskRecovery —
    /// the same mechanism used by the window UI.
    /// </summary>
    public static class StaticModelTaskTracker
    {
#if UNITY_EDITOR
        [Serializable]
        private class PersistedTask
        {
            public string   taskId;
            public string   backendTaskId;
            public string   prompt;
            public string   status;
            public int      progress;
            public string   prefabPath;
            public string   modelPath;
            public string   previewUrl;
            public string   errorMessage;
            public long     startTimeTicks;
            public long     endTimeTicks;
            public string   generatorType;
            public string   modelVersion;
            public string   imagePath;
            public string[] multiviewImagePaths;
            public string   sessionId;
        }

        public class StaticModelTaskInfo : IGenerationTaskInfo
        {
            public string    TaskId              { get; set; }
            public string    BackendTaskId       { get; set; }
            public string    Prompt              { get; set; }
            public string    Status              { get; set; }
            public int       Progress            { get; set; }
            public string    ModelPath           { get; set; }
            public string    PrefabPath          { get; set; }
            public string    PreviewUrl          { get; set; }
            public string    ErrorMessage        { get; set; }
            public DateTime  StartTime           { get; set; }
            public DateTime? EndTime             { get; set; }
            public string    GeneratorType       { get; set; }
            public string    ModelVersion        { get; set; }
            public string    ImagePath           { get; set; }
            public string[]  MultiviewImagePaths { get; set; }
            public string    SessionId           { get; set; }
        }

        private static readonly GenerationTaskTrackerStore<StaticModelTaskInfo, PersistedTask> Store =
            new GenerationTaskTrackerStore<StaticModelTaskInfo, PersistedTask>(
                "TJGen_3DModel", BuildPersisted, FromPersisted);

        private static PersistedTask BuildPersisted(StaticModelTaskInfo info) => new PersistedTask
        {
            taskId              = info.TaskId,
            backendTaskId       = info.BackendTaskId ?? "",
            prompt              = info.Prompt,
            status              = info.Status,
            progress            = info.Progress,
            prefabPath          = info.PrefabPath,
            modelPath           = info.ModelPath,
            previewUrl          = info.PreviewUrl ?? "",
            errorMessage        = info.ErrorMessage,
            startTimeTicks      = info.StartTime.Ticks,
            endTimeTicks        = info.EndTime?.Ticks ?? 0,
            generatorType       = info.GeneratorType ?? "",
            modelVersion        = info.ModelVersion ?? "",
            imagePath           = info.ImagePath ?? "",
            multiviewImagePaths = info.MultiviewImagePaths ?? new string[0],
            sessionId           = info.SessionId ?? ""
        };

        private static StaticModelTaskInfo FromPersisted(PersistedTask p) => new StaticModelTaskInfo
        {
            TaskId              = p.taskId,
            BackendTaskId       = p.backendTaskId,
            Prompt              = p.prompt,
            Status              = p.status,
            Progress            = p.progress,
            PrefabPath          = p.prefabPath,
            ModelPath           = p.modelPath,
            PreviewUrl          = p.previewUrl,
            ErrorMessage        = p.errorMessage,
            StartTime           = new DateTime(p.startTimeTicks),
            EndTime             = p.endTimeTicks > 0 ? (DateTime?)new DateTime(p.endTimeTicks) : null,
            GeneratorType       = p.generatorType,
            ModelVersion        = p.modelVersion,
            ImagePath           = p.imagePath,
            MultiviewImagePaths = p.multiviewImagePaths ?? new string[0],
            SessionId           = p.sessionId ?? ""
        };

        internal static void ApplyTaskUpdate(StaticModelTaskInfo task, Action<StaticModelTaskInfo> mutate) =>
            Store.ApplyTaskUpdate(task, mutate);

        public static string CreateTask(
            string prompt,
            string generatorType,
            TJGeneratorsTaskHandle handle,
            string prefabPath = null,
            string imagePath = null,
            string[] multiviewPaths = null,
            string modelVersion = null,
            string sessionId = "")
        {
            string prefix = generatorType == "tripo-p1"
                ? "tripo_model"
                : generatorType == "tripo-texture-model"
                    ? "texture_model"
                    : "static_model";
            string taskId = Store.AllocateTaskId(prefix);

            var taskInfo = new StaticModelTaskInfo
            {
                TaskId              = taskId,
                Prompt              = prompt ?? "",
                PrefabPath          = prefabPath ?? "",
                Status              = "initializing",
                Progress            = 0,
                StartTime           = DateTime.Now,
                GeneratorType       = generatorType ?? "",
                ModelVersion        = modelVersion ?? "",
                ImagePath           = imagePath ?? "",
                MultiviewImagePaths = multiviewPaths ?? new string[0],
                SessionId           = sessionId ?? ""
            };

            Store.RegisterTask(taskId, taskInfo);

            handle.OnCreated += (h) =>
            {
                Store.ApplyTaskUpdate(taskInfo, t =>
                {
                    t.BackendTaskId = h.BackendTaskId;
                    t.Status = "generating";
                });
            };
            handle.OnProgress += (h) =>
            {
                Store.ApplyTaskUpdate(taskInfo, t =>
                {
                    t.Status   = "generating";
                    t.Progress = h.Progress;
                    if (!string.IsNullOrEmpty(h.PreviewUrl))
                        t.PreviewUrl = h.PreviewUrl;
                });
            };
            handle.OnCompleted += (h) =>
            {
                Store.ApplyTaskUpdate(taskInfo, t =>
                {
                    t.Status     = "completed";
                    t.Progress   = 100;
                    t.ModelPath  = h.ModelPath;
                    t.PreviewUrl = h.PreviewUrl;
                    t.EndTime    = DateTime.Now;
                });
                GenerationNotifier.NotifyCompleted(
                    toolName: Generate3DModelTool.ResolveModelToolName(taskInfo.GeneratorType),
                    taskId:        taskInfo.TaskId,
                    backendTaskId: taskInfo.BackendTaskId,
                    extraData: new JObject
                    {
                        ["session_id"]       = sessionId,
                        ["generator_type"]   = taskInfo.GeneratorType ?? "",
                        ["prompt"]           = taskInfo.Prompt ?? "",
                        ["image_path"]       = taskInfo.ImagePath ?? "",
                        ["model_path"]       = h.ModelPath ?? "",
                        ["prefab_path"]      = taskInfo.PrefabPath ?? "",
                        ["preview_url"]      = h.PreviewUrl ?? "",
                        ["progress"]         = 100,
                        ["start_time"]       = taskInfo.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["end_time"]         = taskInfo.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                        ["duration_seconds"] = taskInfo.EndTime.HasValue ? (int)(taskInfo.EndTime.Value - taskInfo.StartTime).TotalSeconds : 0
                    });
            };
            handle.OnFailed += (h) =>
            {
                Store.ApplyTaskUpdate(taskInfo, t =>
                {
                    t.Status       = "failed";
                    t.ErrorMessage = h.ErrorMessage;
                    t.EndTime      = DateTime.Now;
                });
                GenerationNotifier.NotifyFailed(
                    toolName: Generate3DModelTool.ResolveModelToolName(taskInfo.GeneratorType),
                    taskId:        taskInfo.TaskId,
                    backendTaskId: taskInfo.BackendTaskId,
                    errorMessage:  h.ErrorMessage,
                    extraData: new JObject
                    {
                        ["session_id"]     = sessionId,
                        ["generator_type"] = taskInfo.GeneratorType ?? "",
                        ["prompt"]         = taskInfo.Prompt ?? ""
                    });
            };

            return taskId;
        }

        public static StaticModelTaskInfo GetTask(string taskId) => Store.GetTask(taskId);

        public static List<StaticModelTaskInfo> GetAllTasks() => Store.GetAllTasks();

        public static StaticModelTaskInfo GetTaskByBackendId(string backendTaskId) =>
            Store.GetTaskByBackendId(backendTaskId);

        public static StaticModelTaskInfo CreateRecoveredTask(
            string backendTaskId, string prompt, string prefabPath, long timestampMs)
        {
            return Store.CreateRecoveredTask(backendTaskId, () => new StaticModelTaskInfo
            {
                TaskId        = $"recovered_{backendTaskId}",
                BackendTaskId = backendTaskId,
                Prompt        = prompt ?? "",
                PrefabPath    = prefabPath ?? "",
                Status        = "recovering",
                Progress      = 0,
                StartTime     = timestampMs > 0
                                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).LocalDateTime
                                    : DateTime.Now
            });
        }

        public static void RemoveTask(string taskId) => Store.RemoveTask(taskId);

        public static void CleanupCompletedTasks() => Store.CleanupCompletedTasks();
#endif
    }

#if UNITY_EDITOR
    /// <summary>
    /// Automatically resumes interrupted rodin and tripo-p1 tasks after domain reload,
    /// using the same TJGeneratorsTaskRecovery mechanism as the window UI.
    /// </summary>
    [InitializeOnLoad]
    public static class StaticModelDomainReloadRecovery
    {
        static StaticModelDomainReloadRecovery()
        {
            CustomToolDomainReloadRecovery.Schedule(ResumeInterruptedTasks);
        }

        private static void ResumeInterruptedTasks()
        {
            CustomToolDomainReloadRecovery.Resume(
                "Generate3DModelTool",
                ConfigType.Generator,
                t => t.modelVersion == "rodin" || t.modelVersion == "tencent-generation" || t.modelVersion == "tripo-p1" || t.modelVersion == "tripo-texture-model",
                () => StaticModelTaskTracker.GetAllTasks(),
                (interrupted, _, generator) =>
                {
                    var trackerTask = StaticModelTaskTracker.GetTaskByBackendId(interrupted.backendTaskId);
                    if (trackerTask != null)
                    {
                        CustomToolDomainReloadRecovery.MarkTrackerRecoveringIfNeeded(trackerTask.Status, () =>
                        {
                            StaticModelTaskTracker.ApplyTaskUpdate(trackerTask, t => t.Status = "recovering");
                        });
                    }
                    else
                    {
                        string prefabPath = CustomToolDomainReloadRecovery.ResolveAssetPath(interrupted.targetAssetGuid);
                        trackerTask = StaticModelTaskTracker.CreateRecoveredTask(
                            interrupted.backendTaskId, interrupted.prompt, prefabPath, interrupted.timestamp);
                    }

                    var targetAsset = !string.IsNullOrEmpty(interrupted.targetAssetGuid)
                        ? TJGeneratorsAssetReference.FromGuid(interrupted.targetAssetGuid)
                        : null;

                    var host = new StaticModelRecoveryHost(targetAsset, interrupted.backendTaskId, generator);
                    string toolName = Generate3DModelTool.ResolveModelToolName(interrupted.modelVersion);
                    CustomToolDomainReloadRecovery.StartPolling(
                        "Generate3DModelTool", host, ConfigType.Generator,
                        interrupted.sessionId, toolName, generator, interrupted.backendTaskId);
                });
        }
    }

    /// <summary>
    /// Headless pipeline host for resuming static model tasks after domain reload.
    /// Updates StaticModelTaskTracker on completion/failure.
    /// </summary>
    internal class StaticModelRecoveryHost : HeadlessPipelineHostBase
    {
        private readonly TJGeneratorsAssetReference _targetAsset;
        private readonly string _backendTaskId;
        private readonly ModelGeneratorBase _generator;

        public StaticModelRecoveryHost(TJGeneratorsAssetReference targetAsset, string backendTaskId, ModelGeneratorBase generator)
        {
            _targetAsset   = targetAsset;
            _backendTaskId = backendTaskId;
            _generator     = generator;
        }

        protected override string DialogLogTag => "StaticModelRecovery";

        public override TJGeneratorsAssetReference GetTargetAsset() => _targetAsset;

        public override void OnGenerationCompleted(string modelPath)
        {
            if (!string.IsNullOrEmpty(modelPath))
                PathUtils.EnsureModelImporterReadable(modelPath);

            string prefabPath = _targetAsset?.GetPath();

            var tasksToUpdate = new List<StaticModelTaskTracker.StaticModelTaskInfo>();
            var byBackend = StaticModelTaskTracker.GetTaskByBackendId(_backendTaskId);
            if (byBackend != null) tasksToUpdate.Add(byBackend);

            if (!string.IsNullOrEmpty(prefabPath))
            {
                foreach (var t in StaticModelTaskTracker.GetAllTasks())
                {
                    if (!tasksToUpdate.Contains(t) &&
                        string.Equals(t.PrefabPath, prefabPath, StringComparison.OrdinalIgnoreCase) &&
                        (t.Status == "generating" || t.Status == "recovering" || t.Status == "initializing"))
                    {
                        tasksToUpdate.Add(t);
                    }
                }
            }

            foreach (var trackerTask in tasksToUpdate)
            {
                StaticModelTaskTracker.ApplyTaskUpdate(trackerTask, t =>
                {
                    t.Status    = "completed";
                    t.Progress  = 100;
                    t.ModelPath = modelPath;
                    t.EndTime   = DateTime.Now;
                });
            }

            TJLog.Log($"[Generate3DModelTool] Recovered task completed ({tasksToUpdate.Count} task(s) updated): {modelPath}");

            var notifyTask = byBackend;
            if (notifyTask != null)
                GenerationNotifier.NotifyCompleted(
                    toolName: Generate3DModelTool.ResolveModelToolName(notifyTask.GeneratorType),
                    taskId:        notifyTask.TaskId,
                    backendTaskId: _backendTaskId,
                    extraData: new JObject
                    {
                        ["session_id"]       = notifyTask.SessionId ?? "",
                        ["generator_type"]   = notifyTask.GeneratorType ?? "",
                        ["prompt"]           = notifyTask.Prompt ?? "",
                        ["image_path"]       = notifyTask.ImagePath ?? "",
                        ["model_path"]       = modelPath ?? "",
                        ["prefab_path"]      = notifyTask.PrefabPath ?? "",
                        ["preview_url"]      = notifyTask.PreviewUrl ?? "",
                        ["progress"]         = 100,
                        ["start_time"]       = notifyTask.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["end_time"]         = notifyTask.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                        ["duration_seconds"] = notifyTask.EndTime.HasValue ? (int)(notifyTask.EndTime.Value - notifyTask.StartTime).TotalSeconds : 0
                    });
            else
                TJLog.LogWarning($"[Generate3DModelTool] Recovery completed but notifyTask not found, backend_task_id={_backendTaskId}; notification will be lost.");
        }

        public override void ShowDialog(string title, string message)
        {
            base.ShowDialog(title, message);

            if (ErrorDialogUtils.IsErrorDialog(title))
            {
                var trackerTask = StaticModelTaskTracker.GetTaskByBackendId(_backendTaskId);
                if (trackerTask != null)
                {
                    var friendlyError = ErrorDialogUtils.ConvertToUserFriendlyError(title, message);
                    StaticModelTaskTracker.ApplyTaskUpdate(trackerTask, t =>
                    {
                        t.Status       = "failed";
                        t.ErrorMessage = friendlyError.TechnicalMessage;
                        t.EndTime      = DateTime.Now;
                    });
                    GenerationNotifier.NotifyFailed(
                        toolName: Generate3DModelTool.ResolveModelToolName(trackerTask.GeneratorType),
                        taskId:        trackerTask.TaskId,
                        backendTaskId: _backendTaskId,
                        errorMessage:  friendlyError.TechnicalMessage,
                        extraData: new JObject
                        {
                            ["session_id"]     = trackerTask.SessionId ?? "",
                            ["generator_type"] = trackerTask.GeneratorType ?? "",
                            ["prompt"]         = trackerTask.Prompt ?? ""
                        });
                }
            }
        }

        public override void Repaint()
        {
            if (_generator == null) return;
            var trackerTask = StaticModelTaskTracker.GetTaskByBackendId(_backendTaskId);
            if (trackerTask == null) return;
            bool isActive = trackerTask.Status == "recovering" || trackerTask.Status == "generating";
            if (!isActive) return;
            int progress = _generator.CurrentProgress;
            if (progress > trackerTask.Progress)
            {
                StaticModelTaskTracker.ApplyTaskUpdate(trackerTask, t =>
                {
                    t.Status   = "generating";
                    t.Progress = progress;
                });
            }
        }

    }
#endif

    /// <summary>
    /// CustomTool for generating static (non-animated) 3D models using Rodin Gen-2.5 (rodin).
    /// Produces an FBX model bound to a prefab. A Cube placeholder is shown in the scene
    /// during generation and automatically replaced when the model is ready.
    /// </summary>
    public static class Generate3DModelTool
    {
        private const string GeneratorId = "rodin";

        [ExecuteCustomTool.CustomTool("generate_3d_model_by_rodin",
            "Generate a 3D model from a text prompt and/or reference image using Rodin Gen-2.5 (Extreme-High tier by default). " +
            "Use this for high-precision 3D objects: hero assets, detailed props, PBR materials, architecture, etc. " +
            "For an animated HUMANOID from scratch (same as UI 添加动作): set add_motion=true and motion_description; " +
            "after the mesh lands the pipeline runs UniRig then HunyuanMotion automatically. Recommend ta_pose=true. " +
            "For an existing FBX/OBJ use generate_animated_character / generate_rigged_model / generate_model_motion instead. " +
            "Key parameters: " +
            "prompt (string, text description — required if image_path is not provided), " +
            "image_path (string, Unity asset path or absolute path to a reference image — required if prompt is not provided; can be combined with prompt), " +
            "prefab_output_path (string, optional, default auto-generated under Assets/TJGenerators/History/), " +
            "force_overwrite (bool, default false — set true to replace an existing prefab at the same path), " +
            "tier (string, default 'Gen-2.5-Extreme-High' — options: Gen-2.5-Extreme-Low/Low/Medium/High/Extreme-High), " +
            "quality (string, default 'medium' — options: extra-low/low/medium/high), " +
            "quality_override (int, optional custom target face count 500-2000000, takes precedence over quality — " +
            "set when the user wants a specific face budget, e.g. lightweight game props; Quad mode caps at 200000), " +
            "material (string, default 'PBR' — options: PBR/Shaded), " +
            "mesh_mode (string, default 'Quad' — options: Quad/Raw), " +
            "ta_pose (bool, default false — set true when add_motion so the mesh is easier to rig), " +
            "geometry_format (string, default 'fbx'), " +
            "add_motion (bool, default false — same as UI 添加动作: UniRig + HunyuanMotion after the model is ready), " +
            "motion_description (string, required when add_motion is true, e.g. 'a walking cycle'). " +
            "Generation takes 3–15 minutes. This call SYNCHRONOUSLY submits the task to the backend " +
            "before returning. " +
            "On success, instantiate the prefab immediately (it contains a Cube placeholder). " +
            "Call query_3d_model_status_by_rodin after 5 seconds to confirm the task is running, then poll every 10-15 seconds. " +
            "NOTE: If a domain reload (script compilation) occurs mid-generation, the task is automatically " +
            "resumed in the background — status will show 'recovering' until polling restarts.")]
        public static object Generate3DModel(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[Generate3DModelTool] Generating with parameters: {parameters}");

                string prompt           = parameters["prompt"]?.ToString();
                string imagePath        = parameters["image_path"]?.ToString();
                string prefabOutputPath = parameters["prefab_output_path"]?.ToString();
                bool   forceOverwrite   = parameters["force_overwrite"]?.ToObject<bool>() ?? false;
                string sessionId        = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());

                if (string.IsNullOrEmpty(prompt) && string.IsNullOrEmpty(imagePath))
                    return Fail("At least one of 'prompt' or 'image_path' is required.");

                if (!string.IsNullOrEmpty(prompt))
                {
                    int maxLen = TJGeneratorsPromptLimits.GetMaxLength(GeneratorId);
                    if (maxLen > 0 && prompt.Length > maxLen)
                        return Fail($"Prompt exceeds {maxLen} character limit (current: {prompt.Length} chars)");
                }

                string resolvedImagePath = null;
                if (!string.IsNullOrEmpty(imagePath))
                {
                    resolvedImagePath = ResolveImagePath(imagePath);
                    if (resolvedImagePath == null)
                        return Fail($"Image file not found: '{imagePath}'. Provide a valid Unity asset path (Assets/...) or absolute file path.");
                }

                if (string.IsNullOrEmpty(prefabOutputPath))
                {
                    prefabOutputPath = "Assets/TJGenerators/History/Model3D.prefab";
                    string defaultDir = Path.GetDirectoryName(prefabOutputPath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(defaultDir))
                        PathUtils.EnsureAssetFolder(defaultDir);
                    prefabOutputPath = AssetDatabase.GenerateUniqueAssetPath(prefabOutputPath);
                    if (string.IsNullOrEmpty(prefabOutputPath))
                        prefabOutputPath = "Assets/TJGenerators/History/Model3D.prefab";
                }
                else
                {
                    prefabOutputPath = Path.ChangeExtension(prefabOutputPath, ".prefab");

                    if (File.Exists(prefabOutputPath))
                    {
                        if (forceOverwrite)
                        {
                            AssetDatabase.DeleteAsset(prefabOutputPath);
                        }
                        else
                        {
                            string existingDir = Path.GetDirectoryName(prefabOutputPath)?.Replace('\\', '/');
                            if (!string.IsNullOrEmpty(existingDir))
                                PathUtils.EnsureAssetFolder(existingDir);
                            prefabOutputPath = AssetDatabase.GenerateUniqueAssetPath(prefabOutputPath);
                            if (string.IsNullOrEmpty(prefabOutputPath))
                                prefabOutputPath = Path.ChangeExtension(parameters["prefab_output_path"]?.ToString(), ".prefab");
                        }
                    }
                }

                var config = ConfigManager.GetGeneratorConfig(ConfigType.Generator, GeneratorId);
                if (config == null)
                    return Fail($"Cannot find generator config for '{GeneratorId}'. Ensure cn.tuanjie.ai.generators package is installed.");

                var generator = new DynamicGenerator(config);

                if (!string.IsNullOrEmpty(resolvedImagePath))
                {
                    generator.SetImagePath(resolvedImagePath);
                    if (!string.IsNullOrEmpty(prompt))
                        generator.SetTextPrompt(prompt);
                }
                else
                {
                    generator.SetTextPrompt(prompt);
                }

                ApplyParameters(generator, parameters);
                bool addMotion = TryApplyAddMotion(generator, parameters, out string motionErr);
                if (motionErr != null)
                    return Fail(motionErr);

                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[Generate3DModelTool] 任务提交失败 [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[Generate3DModelTool] 任务提交成功，backend_task_id={submitResult.BackendTaskId}");

                string createdPrefabPath = CreateBlankPrefab(prefabOutputPath);
                if (string.IsNullOrEmpty(createdPrefabPath))
                    return Fail($"Failed to create prefab at: {prefabOutputPath}");

                TJGeneratorsGenerationLabel.EnableSessionLabel(
                    TJGeneratorsAssetReference.FromPath(createdPrefabPath), sessionId);

                var context = new TJGeneratorsGenerationContext
                {
                    TargetAsset            = TJGeneratorsAssetReference.FromPath(createdPrefabPath),
                    AutoCreateTargetPrefab = false
                };

                var taskHandle = TJGeneratorsGenerationService.GenerateFromSubmittedTask(
                    generator, context, submitResult.BackendTaskId, sessionId);
                string taskId = StaticModelTaskTracker.CreateTask(
                    prompt ?? "", GeneratorId, taskHandle, createdPrefabPath, resolvedImagePath, sessionId: sessionId);

                TJLog.Log($"[Generate3DModelTool] 轮询已启动，task_id={taskId}, backend_task_id={submitResult.BackendTaskId}");

                var result = new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "task_id",            taskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       GeneratorId },
                    { "prompt",             prompt ?? "" },
                    { "image_path",         resolvedImagePath ?? "" },
                    { "prefab_output_path", createdPrefabPath },
                    { "add_motion",         addMotion },
                    { "message",
                        addMotion
                            ? "3D model + UniRig + HunyuanMotion started (same as UI 添加动作). " +
                              "STEP 1 (do now): Instantiate the prefab at prefab_output_path — it contains a Cube placeholder. " +
                              "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                              "STEP 3 (automatic): A <bg_task_done> notification will appear after the mesh, rig, and motion all finish " +
                              "(~12-20 min) with model_path / prefab_path / preview_url. Do NOT call generate_animated_character. " +
                              "*** POLLING IS STRICTLY FORBIDDEN — do NOT call query_3d_model_status_by_rodin repeatedly. " +
                              "Only call it ONCE as a last-resort fallback if no notification arrives. ***" +
                              "*** RE-SUBMISSION IS STRICTLY FORBIDDEN — do NOT call generate_3d_model_by_rodin " +
                              "again for the same model, regardless of outcome. Report errors and stop. ***"
                            : "3D model generation started. " +
                              "STEP 1 (do now): Instantiate the prefab at prefab_output_path — it contains a Cube placeholder. " +
                              "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                              "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn (~10 min) " +
                              "containing ALL generation results (model_path, prefab_path, preview_url, timing, etc.). " +
                              "*** POLLING IS STRICTLY FORBIDDEN — do NOT call query_3d_model_status_by_rodin repeatedly. " +
                              "Only call it ONCE as a last-resort fallback if no notification arrives. ***" +
                              "*** RE-SUBMISSION IS STRICTLY FORBIDDEN — do NOT call generate_3d_model_by_rodin " +
                              "again for the same model, regardless of outcome. Report errors and stop. ***" },
                    { "estimated_wait_seconds", addMotion ? 900 : 600 },
                    { "preview_url", PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "notification_mode",      "bg_task_done" }
                };
                if (addMotion)
                    result["motion_description"] = generator.GetMotionDescription();
                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[Generate3DModelTool] Error: {e}");
                return Fail($"Error: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

        [ExecuteCustomTool.CustomTool("query_3d_model_status_by_rodin",
            "Query the status of a static 3D model generation task (Rodin Gen-2.5). Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "Status values: 'initializing', 'generating', 'recovering', 'completed', 'failed', 'interrupted'. " +
            "'recovering' means Unity recompiled scripts (domain reload) and the task was automatically resumed. " +
            "'interrupted' means the backend task record was lost and recovery is not possible — re-generate. " +
            "When completed, returns model_path (the downloaded FBX asset path) and prefab_path. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QueryStatus(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                string taskId = parameters["task_id"]?.ToString();
                if (string.IsNullOrEmpty(taskId))
                    return Fail("'task_id' parameter is required");

                var task = StaticModelTaskTracker.GetTask(taskId);
                if (task == null)
                    return Fail($"Task '{taskId}' not found. It may have been cleaned up (tasks expire after 60 min) or Unity was fully restarted.");

                if ((task.Status == "generating" || task.Status == "recovering") && task.Progress >= 95)
                    TryUpdateFromHistory(task);

                var result = new Dictionary<string, object>
                {
                    { "success",    true },
                    { "task_id",    task.TaskId },
                    { "status",     task.Status },
                    { "progress",   task.Progress },
                    { "prompt",     task.Prompt },
                    { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
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

                if (task.Status == "interrupted")
                    result["hint"] = "Re-generate using generate_3d_model_by_rodin with force_overwrite=true and the same prefab_output_path.";

                if (task.Status == "recovering" && !string.IsNullOrEmpty(task.BackendTaskId))
                {
                    bool stillInRecovery = TJGeneratorsTaskRecovery.HasActiveRecovery(task.BackendTaskId);

                    if (!stillInRecovery)
                    {
                        TryUpdateFromHistory(task);
                        result["status"]   = task.Status;
                        result["progress"] = task.Progress;
                        if (!string.IsNullOrEmpty(task.ModelPath))    result["model_path"] = task.ModelPath;
                        result["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);
                        if (!string.IsNullOrEmpty(task.ErrorMessage)) result["error"]      = task.ErrorMessage;
                        if (task.EndTime.HasValue)
                        {
                            result["end_time"]         = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                            result["duration_seconds"] = (int)(task.EndTime.Value - task.StartTime).TotalSeconds;
                        }
                    }
                    else
                    {
                        result["hint"] = "Task was interrupted by a domain reload and is now being resumed automatically. Keep polling every 20-30 seconds.";
                    }
                }

                if (task.Status == "generating" && task.Progress >= 100)
                {
                    TryUpdateFromHistory(task);
                    result["status"]   = task.Status;
                    result["progress"] = task.Progress;
                    if (!string.IsNullOrEmpty(task.ModelPath))    result["model_path"] = task.ModelPath;
                    result["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);
                    if (!string.IsNullOrEmpty(task.ErrorMessage)) result["error"]      = task.ErrorMessage;
                    if (task.EndTime.HasValue)
                    {
                        result["end_time"]         = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                        result["duration_seconds"] = (int)(task.EndTime.Value - task.StartTime).TotalSeconds;
                    }
                }

                if (task.Status == "completed" && !string.IsNullOrEmpty(task.ModelPath))
                    result["result_summary"] = $"Generation completed. Model: {task.ModelPath}. Prefab: {task.PrefabPath ?? "N/A"}.";

                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[Generate3DModelTool] Query error: {e}");
                return Fail($"Error querying status: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

        [ExecuteCustomTool.CustomTool("list_3d_model_tasks_by_rodin",
            "List all active and recent static 3D model generation tasks (Rodin Gen-2.5). " +
            "Tasks survive script recompilation (domain reload) within the same Editor session — " +
            "in-progress tasks are automatically resumed and show status 'recovering'. " +
            "Tasks with status 'interrupted' lost their backend record and must be re-generated.")]
        public static object ListTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks = StaticModelTaskTracker.GetAllTasks()
                    .Where(t => t.GeneratorType == GeneratorId ||
                                (string.IsNullOrEmpty(t.GeneratorType) && !t.TaskId.StartsWith("tripo_model_")))
                    .ToList();
                var taskList = new List<Dictionary<string, object>>();

                foreach (var task in tasks)
                {
                    var d = new Dictionary<string, object>
                    {
                        { "task_id",    task.TaskId },
                        { "status",     task.Status },
                        { "progress",   task.Progress },
                        { "prompt",     task.Prompt },
                        { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                    };

                    if (!string.IsNullOrEmpty(task.PrefabPath))   d["prefab_path"] = task.PrefabPath;
                    if (!string.IsNullOrEmpty(task.ModelPath))    d["model_path"]  = task.ModelPath;
                    d["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);
                    if (!string.IsNullOrEmpty(task.ErrorMessage)) d["error"]       = task.ErrorMessage;
                    if (task.EndTime.HasValue) d["end_time"] = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");

                    if (task.Status == "completed" && !string.IsNullOrEmpty(task.ModelPath))
                        d["result_summary"] = $"Model: {task.ModelPath}. Prefab: {task.PrefabPath ?? "N/A"}.";

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
                TJLog.LogError($"[Generate3DModelTool] List error: {e}");
                return Fail($"Error listing tasks: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

#if UNITY_EDITOR
        private static void TryUpdateFromHistory(StaticModelTaskTracker.StaticModelTaskInfo task)
        {
            string guid = string.IsNullOrEmpty(task.PrefabPath) ? "" : AssetDatabase.AssetPathToGUID(task.PrefabPath);
            var history = !string.IsNullOrEmpty(guid)
                ? TJGeneratorsHistoryManager.LoadHistoryForAsset(guid)
                : new List<TJGeneratorsGenerationHistoryItem>();

            if (history == null || history.Count == 0)
                history = TJGeneratorsHistoryManager.LoadHistory();

            TJGeneratorsGenerationHistoryItem completedItem = null;
            long latestTimestamp = 0;
            string prompt = task.Prompt ?? "";
            long taskStartMs = new DateTimeOffset(task.StartTime).ToUnixTimeMilliseconds();
            long lowerBoundMs = taskStartMs - (10 * 60 * 1000);

            foreach (var h in history)
            {
                if (h == null || h.isGenerating || string.IsNullOrEmpty(h.modelPath))
                    continue;
                if (h.modelVersion != "rodin" && h.modelVersion != "tencent-generation" && h.modelVersion != "tripo-texture-model")
                    continue;

                bool guidMatch   = !string.IsNullOrEmpty(guid) && (h.assetGuid ?? "") == guid;
                bool promptMatch = !string.IsNullOrEmpty(prompt) &&
                                   string.Equals((h.prompt ?? "").Trim(), prompt.Trim(), StringComparison.OrdinalIgnoreCase);
                bool timeMatch   = h.timestamp >= lowerBoundMs;

                if (!(guidMatch || (promptMatch && timeMatch)))
                    continue;

                if (h.timestamp > latestTimestamp)
                {
                    completedItem   = h;
                    latestTimestamp = h.timestamp;
                }
            }

            if (completedItem != null)
            {
                StaticModelTaskTracker.ApplyTaskUpdate(task, t =>
                {
                    t.Status     = "completed";
                    t.Progress   = 100;
                    t.ModelPath  = completedItem.modelPath;
                    t.PreviewUrl = completedItem.previewImageUrl;
                    t.EndTime    = DateTimeOffset.FromUnixTimeMilliseconds(completedItem.timestamp).LocalDateTime;
                });
                TJLog.Log($"[Generate3DModelTool] Recovery task completed via history: {completedItem.modelPath}");
            }
            else if ((DateTime.Now - task.StartTime).TotalMinutes > 30)
            {
                StaticModelTaskTracker.ApplyTaskUpdate(task, t =>
                {
                    t.Status       = "failed";
                    t.ErrorMessage = "Recovery finished but no completed model was found in history. The generation may have failed.";
                    t.EndTime      = DateTime.Now;
                });
                TJLog.LogWarning($"[Generate3DModelTool] Recovery task timed out without result: {task.TaskId}");
            }
        }

        internal static string CreateBlankPrefab(string path)
        {
            path = Path.ChangeExtension(path, ".prefab").Replace("\\", "/");

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var root = new GameObject(Path.GetFileNameWithoutExtension(path));
            try
            {
                var placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
                placeholder.name = "Placeholder";
                placeholder.transform.SetParent(root.transform);
                placeholder.transform.localPosition = Vector3.zero;
                placeholder.transform.localRotation = Quaternion.identity;
                placeholder.transform.localScale    = Vector3.one;

                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));

            return path;
        }

        private static void ApplyParameters(DynamicGenerator generator, JObject parameters)
        {
            // Default to Extreme-High tier for high-precision generation
            string tier = parameters["tier"]?.ToString();
            if (string.IsNullOrEmpty(tier))
                tier = "Gen-2.5-Extreme-High";
            generator.SetParameter("tier", tier);

            if (parameters["quality"] != null)
                generator.SetParameter("quality", parameters["quality"].ToString());

            // Custom face count cap (overrides the quality preset). Default to 0 (= not sent) so the
            // high-precision default tier isn't silently capped; low tiers still get the backend auto-cap.
            if (parameters["quality_override"] != null)
                generator.SetParameter("qualityOverride", parameters["quality_override"].ToObject<int>());
            else
                generator.SetParameter("qualityOverride", 0);

            if (parameters["material"] != null)
                generator.SetParameter("material", parameters["material"].ToString());

            if (parameters["mesh_mode"] != null)
                generator.SetParameter("meshMode", parameters["mesh_mode"].ToString());

            if (parameters["ta_pose"] != null)
                generator.SetParameter("taPose", parameters["ta_pose"].ToObject<bool>());

            if (parameters["geometry_format"] != null)
                generator.SetParameter("geometryFormat", parameters["geometry_format"].ToString());
        }

        /// <summary>
        /// UI「添加动作」同款：主模型落地后走 UniRig + HunyuanMotion。
        /// </summary>
        /// <returns>true 表示已打开后处理；error 非空表示参数非法。</returns>
        internal static bool TryApplyAddMotion(DynamicGenerator generator, JObject parameters, out string error)
        {
            error = null;
            bool addMotion = parameters["add_motion"]?.ToObject<bool>() ?? false;
            if (!addMotion)
                return false;

            string motionDescription = parameters["motion_description"]?.ToString();
            if (string.IsNullOrWhiteSpace(motionDescription))
            {
                error = "add_motion is true but 'motion_description' is required (same as UI 添加动作). Example: 'a walking cycle'.";
                return false;
            }

            generator.SetAddMotion(true, motionDescription.Trim());
            return true;
        }

        internal static void ApplyParametersInternal(DynamicGenerator generator, JObject parameters)
        {
            ApplyParameters(generator, parameters);
        }

        internal static string ResolveImagePath(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return null;

            if (Path.IsPathRooted(imagePath))
                return File.Exists(imagePath) ? imagePath : null;

            if (imagePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                string absPath = Path.Combine(
                    Application.dataPath.Replace("Assets", ""),
                    imagePath).Replace("\\", "/");
                return File.Exists(absPath) ? absPath : null;
            }

            string projectRelative = Path.Combine(
                Application.dataPath.Replace("Assets", ""),
                imagePath).Replace("\\", "/");
            if (File.Exists(projectRelative)) return projectRelative;

            return null;
        }

        internal static Dictionary<string, object> Fail(string message)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "message", message }
            };
        }

        /// <summary>
        /// Resolves the CustomTool name for notification purposes based on generator type.
        /// </summary>
        internal static string ResolveModelToolName(string generatorType)
        {
            switch (generatorType)
            {
                case "tripo-p1":            return "generate_3d_model_by_tripo_p1";
                case "tripo-texture-model": return "generate_texture_model_by_tripo";
                default:                    return "generate_3d_model_by_rodin";
            }
        }
#endif
    }

    /// <summary>
    /// CustomTool for generating Tripo 3D models (Text/Image/Multiview) via TJGenerators pipeline.
    /// Default model_version is P1-20260311 (low-poly optimized).
    /// </summary>
    public static class GenerateTripoModelTool
    {
        private const string GeneratorId = "tripo-p1";
        private const string DefaultModelVersion = "P1-20260311";

        [ExecuteCustomTool.CustomTool(
            "generate_3d_model_by_tripo_p1",
            "Generate a 3D model using Tripo P1 (supports text-to-model, image-to-model, multiview-to-model). " +
            "Default model_version is P1-20260311 (low-poly optimized). " +
            "For an animated HUMANOID from scratch (same as UI 添加动作): set add_motion=true and motion_description; " +
            "after the mesh lands the pipeline runs UniRig then HunyuanMotion automatically. " +
            "For an existing FBX/OBJ use generate_animated_character / generate_rigged_model / generate_model_motion instead. " +
            "Parameters: prompt (string), image_path (string), multiview_image_paths (string[4] - [front,left,back,right]), " +
            "model_version (string), face_limit (int), texture (bool), pbr (bool), texture_seed (int), texture_quality (standard|detailed), " +
            "orientation (default|align_image - image/multiview only), " +
            "compress (string), export_uv (bool), with_mesh_segmentation (bool, default false — enable mesh segmentation), " +
            "add_motion (bool, default false — UniRig + HunyuanMotion after the model is ready), " +
            "motion_description (string, required when add_motion is true, e.g. 'a walking cycle'), " +
            "session_id (string, optional — adds Session_{id} label to the placeholder prefab for agent session grouping). " +
            "IMPORTANT: For P1-20260311, do NOT pass unsupported params (quad/smart_low_poly/generate_parts). " +
            "This call submits a backend task then starts polling & downloading asynchronously. Returns immediately with task_id and prefab_output_path."
        )]
        public static object GenerateTripo3DModel(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateTripoModelTool] Generating with parameters: {parameters}");

                string prompt           = parameters["prompt"]?.ToString();
                string imagePath        = parameters["image_path"]?.ToString();
                string prefabOutputPath = parameters["prefab_output_path"]?.ToString();
                bool   forceOverwrite   = parameters["force_overwrite"]?.ToObject<bool>() ?? false;
                string sessionId        = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());

                var mvToken = parameters["multiview_image_paths"];
                string[] multiviewPaths = null;
                if (mvToken != null && mvToken.Type == JTokenType.Array)
                    multiviewPaths = mvToken.ToObject<string[]>();

                bool hasText      = !string.IsNullOrEmpty(prompt);
                bool hasImage     = !string.IsNullOrEmpty(imagePath);
                bool hasMultiview = multiviewPaths != null && multiviewPaths.Length > 0;

                if (!hasText && !hasImage && !hasMultiview)
                    return Generate3DModelTool.Fail("At least one of 'prompt', 'image_path', or 'multiview_image_paths' is required.");

                if (hasText)
                {
                    int maxLen = TJGeneratorsPromptLimits.GetMaxLength(GeneratorId);
                    if (maxLen > 0 && prompt.Length > maxLen)
                        return Generate3DModelTool.Fail($"Prompt exceeds {maxLen} character limit (current: {prompt.Length} chars)");
                }

                string resolvedImagePath = null;
                if (hasImage)
                {
                    resolvedImagePath = Generate3DModelTool.ResolveImagePath(imagePath);
                    if (resolvedImagePath == null)
                        return Generate3DModelTool.Fail($"Image file not found: '{imagePath}'. Provide a valid Unity asset path (Assets/...) or absolute file path.");
                }

                string[] resolvedMv = null;
                if (hasMultiview)
                {
                    resolvedMv = new string[multiviewPaths.Length];
                    for (int i = 0; i < multiviewPaths.Length; i++)
                    {
                        if (string.IsNullOrEmpty(multiviewPaths[i])) { resolvedMv[i] = null; continue; }
                        string r = Generate3DModelTool.ResolveImagePath(multiviewPaths[i]);
                        if (r == null)
                            return Generate3DModelTool.Fail($"Multiview image file not found at index {i}: '{multiviewPaths[i]}'");
                        resolvedMv[i] = r;
                    }
                }

                if (string.IsNullOrEmpty(prefabOutputPath))
                {
                    prefabOutputPath = "Assets/TJGenerators/History/Tripo3D.prefab";
                    string defaultDir = Path.GetDirectoryName(prefabOutputPath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(defaultDir))
                        PathUtils.EnsureAssetFolder(defaultDir);
                    prefabOutputPath = AssetDatabase.GenerateUniqueAssetPath(prefabOutputPath);
                    if (string.IsNullOrEmpty(prefabOutputPath))
                        prefabOutputPath = "Assets/TJGenerators/History/Tripo3D.prefab";
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
                        string existingDir = Path.GetDirectoryName(prefabOutputPath)?.Replace('\\', '/');
                        if (!string.IsNullOrEmpty(existingDir))
                            PathUtils.EnsureAssetFolder(existingDir);
                        prefabOutputPath = AssetDatabase.GenerateUniqueAssetPath(prefabOutputPath);
                        if (string.IsNullOrEmpty(prefabOutputPath))
                            prefabOutputPath = Path.ChangeExtension(parameters["prefab_output_path"]?.ToString(), ".prefab");
                    }
                }

                var config = ConfigManager.GetGeneratorConfig(ConfigType.Generator, GeneratorId);
                if (config == null)
                    return Generate3DModelTool.Fail($"Cannot find generator config for '{GeneratorId}'. Ensure cn.tuanjie.ai.generators package is installed.");

                var generator = new DynamicGenerator(config);

                if (resolvedMv != null && resolvedMv.Length > 0)
                    generator.SetMultiViewPaths(resolvedMv);
                else if (!string.IsNullOrEmpty(resolvedImagePath))
                    generator.SetImagePath(resolvedImagePath);
                if (!string.IsNullOrEmpty(prompt))
                    generator.SetTextPrompt(prompt);

                ApplyTripoParameters(generator, parameters);

                if (parameters["orientation"] != null)
                    generator.SetParameter("orientation", parameters["orientation"].ToString());

                bool addMotion = Generate3DModelTool.TryApplyAddMotion(generator, parameters, out string motionErr);
                if (motionErr != null)
                    return Generate3DModelTool.Fail(motionErr);

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
                string taskId = StaticModelTaskTracker.CreateTask(
                    prompt ?? "", GeneratorId, taskHandle,
                    createdPrefabPath, resolvedImagePath, resolvedMv, modelVersion, sessionId);

                return new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "task_id",            taskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       GeneratorId },
                    { "model_version",      modelVersion },
                    { "prompt",             prompt ?? "" },
                    { "image_path",         resolvedImagePath ?? "" },
                    { "prefab_output_path", createdPrefabPath },
                    { "add_motion",         addMotion },
                    { "motion_description", addMotion ? generator.GetMotionDescription() : "" },
                    { "estimated_wait_seconds", addMotion ? 900 : 600 },
                    { "preview_url", PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "notification_mode",      "bg_task_done" },
                    { "message",
                        addMotion
                            ? "3D model + UniRig + HunyuanMotion started (same as UI 添加动作). " +
                              "STEP 1 (do now): Instantiate the prefab at prefab_output_path — it contains a Cube placeholder. " +
                              "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                              "STEP 3 (automatic): A <bg_task_done> notification will appear after the mesh, rig, and motion all finish " +
                              "(~12-20 min) with model_path / prefab_path / preview_url. Do NOT call generate_animated_character. " +
                              "*** POLLING IS STRICTLY FORBIDDEN — do NOT call query_3d_model_status_by_tripo_p1 repeatedly. " +
                              "Only call it ONCE as a last-resort fallback if no notification arrives. ***" +
                              "*** RE-SUBMISSION IS STRICTLY FORBIDDEN — do NOT call generate_3d_model_by_tripo_p1 " +
                              "again for the same model, regardless of outcome. Report errors and stop. ***"
                            : "3D model generation started. " +
                              "STEP 1 (do now): Instantiate the prefab at prefab_output_path — it contains a Cube placeholder. " +
                              "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                              "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn (~10 min) " +
                              "containing ALL generation results (model_path, prefab_path, preview_url, timing, etc.). " +
                              "*** POLLING IS STRICTLY FORBIDDEN — do NOT call query_3d_model_status_by_tripo_p1 repeatedly. " +
                              "Only call it ONCE as a last-resort fallback if no notification arrives. ***" +
                              "*** RE-SUBMISSION IS STRICTLY FORBIDDEN — do NOT call generate_3d_model_by_tripo_p1 " +
                              "again for the same model, regardless of outcome. Report errors and stop. ***" }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateTripoModelTool] Error: {e}");
                return Generate3DModelTool.Fail($"Error: {e.Message}");
            }
#else
            return new Dictionary<string, object> { { "success", false }, { "message", "This tool only works in Unity Editor." } };
#endif
        }

        [ExecuteCustomTool.CustomTool(
            "query_3d_model_status_by_tripo_p1",
            "Query the status of a Tripo 3D model generation task started by generate_3d_model_by_tripo_p1. Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "Returns: status, progress, prefab_path, model_path (when completed), preview_url (optional). " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden."
        )]
        public static object QueryTripoStatus(JObject parameters)
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

                if ((task.Status == "generating" || task.Status == "recovering") && task.Progress >= 95)
                    TryUpdateFromHistory(task);

                var result = new Dictionary<string, object>
                {
                    { "success",      true },
                    { "task_id",      task.TaskId },
                    { "status",       task.Status },
                    { "progress",     task.Progress },
                    { "backend_task_id", task.BackendTaskId ?? "" },
                    { "model_version",   task.ModelVersion ?? "" },
                    { "start_time",   task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
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
                TJLog.LogError($"[GenerateTripoModelTool] Query error: {e}");
                return Generate3DModelTool.Fail($"Error querying status: {e.Message}");
            }
#else
            return new Dictionary<string, object> { { "success", false }, { "message", "This tool only works in Unity Editor." } };
#endif
        }

        [ExecuteCustomTool.CustomTool(
            "list_3d_model_tasks_by_tripo_p1",
            "List all active and recent Tripo 3D model generation tasks in the current Unity Editor session."
        )]
        public static object ListTripoTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks = StaticModelTaskTracker.GetAllTasks()
                    .Where(t => t.GeneratorType == GeneratorId || t.TaskId.StartsWith("tripo_model_"))
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
                TJLog.LogError($"[GenerateTripoModelTool] List error: {e}");
                return Generate3DModelTool.Fail($"Error listing tasks: {e.Message}");
            }
#else
            return new Dictionary<string, object> { { "success", false }, { "message", "This tool only works in Unity Editor." } };
#endif
        }

#if UNITY_EDITOR
        private static bool IsP1(string modelVersion)
        {
            return !string.IsNullOrEmpty(modelVersion)
                && modelVersion.StartsWith("P1-", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyTripoParameters(DynamicGenerator generator, JObject parameters)
        {
            string modelVersion = parameters["model_version"]?.ToString();
            if (string.IsNullOrEmpty(modelVersion))
                modelVersion = DefaultModelVersion;

            generator.SetParameter("modelVersion", modelVersion);

            if (parameters["face_limit"] != null)
                generator.SetParameter("faceLimit", parameters["face_limit"].ToObject<int>());
            if (parameters["texture"] != null)
                generator.SetParameter("texture", parameters["texture"].ToObject<bool>());
            if (parameters["pbr"] != null)
                generator.SetParameter("pbr", parameters["pbr"].ToObject<bool>());
            if (parameters["texture_seed"] != null)
                generator.SetParameter("textureSeed", parameters["texture_seed"].ToObject<int>());
            if (parameters["texture_quality"] != null)
                generator.SetParameter("textureQuality", parameters["texture_quality"].ToString());
            if (parameters["compress"] != null)
                generator.SetParameter("compress", parameters["compress"].ToString());
            if (parameters["export_uv"] != null)
                generator.SetParameter("exportUv", parameters["export_uv"].ToObject<bool>());
            if (parameters["with_mesh_segmentation"] != null)
                generator.SetParameter("withMeshSegmentation", parameters["with_mesh_segmentation"].ToObject<bool>());

            bool p1 = IsP1(modelVersion);
            if (!p1)
            {
                if (parameters["style"] != null)
                    generator.SetParameter("style", parameters["style"].ToString());
                if (parameters["quad"] != null)
                    generator.SetParameter("quad", parameters["quad"].ToObject<bool>());
                if (parameters["smart_low_poly"] != null)
                    generator.SetParameter("smartLowPoly", parameters["smart_low_poly"].ToObject<bool>());
                if (parameters["generate_parts"] != null)
                    generator.SetParameter("generateParts", parameters["generate_parts"].ToObject<bool>());
            }
        }

        internal static void ApplyTripoParametersInternal(DynamicGenerator generator, JObject parameters)
        {
            ApplyTripoParameters(generator, parameters);
        }

        private static void TryUpdateFromHistory(StaticModelTaskTracker.StaticModelTaskInfo task)
        {
            if (task == null) return;

            string guid = string.IsNullOrEmpty(task.PrefabPath)
                ? ""
                : AssetDatabase.AssetPathToGUID(task.PrefabPath);

            var history = !string.IsNullOrEmpty(guid)
                ? TJGeneratorsHistoryManager.LoadHistoryForAsset(guid)
                : new List<TJGeneratorsGenerationHistoryItem>();

            if (history == null || history.Count == 0)
                history = TJGeneratorsHistoryManager.LoadHistory();

            TJGeneratorsGenerationHistoryItem candidate = null;
            long latestTimestamp = 0;
            string prompt = task.Prompt ?? "";
            long taskStartMs = new DateTimeOffset(task.StartTime).ToUnixTimeMilliseconds();
            long lowerBoundMs = taskStartMs - (10 * 60 * 1000);

            foreach (var h in history)
            {
                if (h == null || h.isGenerating || string.IsNullOrEmpty(h.modelPath))
                    continue;
                if (h.modelVersion != GeneratorId && h.modelVersion != "tripo")
                    continue;

                bool guidMatch   = !string.IsNullOrEmpty(guid) && (h.assetGuid ?? "") == guid;
                bool promptMatch = !string.IsNullOrEmpty(prompt) &&
                                   string.Equals((h.prompt ?? "").Trim(), prompt.Trim(), StringComparison.OrdinalIgnoreCase);
                bool timeMatch   = h.timestamp >= lowerBoundMs;

                if (!(guidMatch || (promptMatch && timeMatch)))
                    continue;

                if (h.timestamp > latestTimestamp)
                {
                    candidate       = h;
                    latestTimestamp = h.timestamp;
                }
            }

            if (candidate != null)
            {
                StaticModelTaskTracker.ApplyTaskUpdate(task, t =>
                {
                    t.Status     = "completed";
                    t.Progress   = 100;
                    t.ModelPath  = candidate.modelPath;
                    t.PreviewUrl = candidate.previewImageUrl;
                    t.EndTime    = DateTimeOffset.FromUnixTimeMilliseconds(candidate.timestamp).LocalDateTime;
                });
            }
        }
#endif
    }
}
