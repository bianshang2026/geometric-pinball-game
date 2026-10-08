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
using TJGenerators.PostProcessing;
using TJGenerators.Utils;
using Unity.EditorCoroutines.Editor;
#endif

namespace UnityTcp.Editor.Tools
{
    // ─────────────────────────────────────────────────────────────────────────────
    // RiggedAnimationTaskTracker  —  three tools share one tracker
    // ─────────────────────────────────────────────────────────────────────────────
    public static class RiggedAnimationTaskTracker
    {
#if UNITY_EDITOR
        [Serializable]
        private class PersistedTask
        {
            public string taskId;
            public string sessionId;
            public string pipelineType;
            public string status;
            public string substatus;
            public int    progress;

            public string sourceModelPath;
            public string riggedModelPath;
            public string motionDescription;

            public string motionFbxPath;
            public string controllerPath;
            public string prefabPath;

            public string backendRigTaskId;
            public string backendMotionTaskId;

            public float  actionDuration;
            public float  cfgStrength;
            public string randomSeedList;
            public bool   loopTime;

            public string errorMessage;
            public long   startTimeTicks;
            public long   endTimeTicks;
        }

        public class RiggedAnimationTaskInfo : IGenerationTaskInfo
        {
            public string    TaskId              { get; set; }
            public string    SessionId           { get; set; }
            public string    PipelineType        { get; set; }
            public string    Status              { get; set; }
            public string    Substatus           { get; set; }
            public int       Progress            { get; set; }

            public string    SourceModelPath     { get; set; }
            public string    RiggedModelPath     { get; set; }
            public string    MotionDescription   { get; set; }

            public string    MotionFbxPath       { get; set; }
            public string    ControllerPath      { get; set; }
            public string    PrefabPath          { get; set; }

            public string    BackendRigTaskId    { get; set; }
            public string    BackendMotionTaskId { get; set; }

            public float     ActionDuration      { get; set; }
            public float     CfgStrength         { get; set; }
            public string    RandomSeedList      { get; set; }
            public bool      LoopTime            { get; set; } = true;

            public string    ErrorMessage        { get; set; }
            public DateTime  StartTime           { get; set; }
            public DateTime? EndTime             { get; set; }

            public string BackendTaskId
            {
                get => !string.IsNullOrEmpty(BackendRigTaskId) ? BackendRigTaskId : BackendMotionTaskId;
                set
                {
                    if (!string.IsNullOrEmpty(BackendRigTaskId))
                        BackendMotionTaskId = value;
                    else
                        BackendRigTaskId = value;
                }
            }

            public string PreviewUrl { get; set; }
        }

        private static readonly GenerationTaskTrackerStore<RiggedAnimationTaskInfo, PersistedTask> Store =
            new GenerationTaskTrackerStore<RiggedAnimationTaskInfo, PersistedTask>(
                "TJGen_RigAnim",
                BuildPersisted,
                FromPersisted,
                getBackendTaskId: t => t.BackendTaskId,
                matchesBackendTaskId: (t, id) =>
                    (!string.IsNullOrEmpty(id) &&
                     (t.BackendRigTaskId == id || t.BackendMotionTaskId == id)),
                reconcileAfterRestore: ReconcileRiggedAfterRestore);

        private static PersistedTask BuildPersisted(RiggedAnimationTaskInfo info) => new PersistedTask
        {
            taskId              = info.TaskId              ?? "",
            sessionId           = info.SessionId           ?? "",
            pipelineType        = info.PipelineType        ?? "",
            status              = info.Status              ?? "",
            substatus           = info.Substatus           ?? "",
            progress            = info.Progress,
            sourceModelPath     = info.SourceModelPath     ?? "",
            riggedModelPath     = info.RiggedModelPath     ?? "",
            motionDescription   = info.MotionDescription   ?? "",
            motionFbxPath       = info.MotionFbxPath       ?? "",
            controllerPath      = info.ControllerPath      ?? "",
            prefabPath          = info.PrefabPath          ?? "",
            backendRigTaskId    = info.BackendRigTaskId    ?? "",
            backendMotionTaskId = info.BackendMotionTaskId ?? "",
            actionDuration      = info.ActionDuration,
            cfgStrength         = info.CfgStrength,
            randomSeedList      = info.RandomSeedList      ?? "0",
            loopTime            = info.LoopTime,
            errorMessage        = info.ErrorMessage        ?? "",
            startTimeTicks      = info.StartTime.Ticks,
            endTimeTicks        = info.EndTime?.Ticks ?? 0
        };

        private static RiggedAnimationTaskInfo FromPersisted(PersistedTask p) => new RiggedAnimationTaskInfo
        {
            TaskId              = p.taskId,
            SessionId           = p.sessionId,
            PipelineType        = p.pipelineType,
            Status              = p.status,
            Substatus           = p.substatus,
            Progress            = p.progress,
            SourceModelPath     = p.sourceModelPath,
            RiggedModelPath     = p.riggedModelPath,
            MotionDescription   = p.motionDescription,
            MotionFbxPath       = p.motionFbxPath,
            ControllerPath      = p.controllerPath,
            PrefabPath          = p.prefabPath,
            BackendRigTaskId    = p.backendRigTaskId,
            BackendMotionTaskId = p.backendMotionTaskId,
            ActionDuration      = p.actionDuration,
            CfgStrength         = p.cfgStrength,
            RandomSeedList      = p.randomSeedList,
            LoopTime            = p.loopTime,
            ErrorMessage        = p.errorMessage,
            StartTime           = new DateTime(p.startTimeTicks),
            EndTime             = p.endTimeTicks > 0 ? (DateTime?)new DateTime(p.endTimeTicks) : null
        };

        private static void ReconcileRiggedAfterRestore(RiggedAnimationTaskInfo info, Action save)
        {
            bool isActive = info.Status == "initializing"     || info.Status == "rigging"          ||
                            info.Status == "rigging_complete"  || info.Status == "generating_motion" ||
                            info.Status == "recovering"        || info.Status == "pending";
            if (!isActive) return;

            bool canRecover =
                TJGeneratorsTaskRecovery.HasActiveRecovery(info.BackendRigTaskId) ||
                TJGeneratorsTaskRecovery.HasActiveRecovery(info.BackendMotionTaskId);

            bool pendingMotionStage = info.PipelineType == "rig_and_motion"
                && info.Status == "rigging_complete"
                && string.IsNullOrEmpty(info.BackendMotionTaskId)
                && !string.IsNullOrEmpty(info.RiggedModelPath)
                && File.Exists(PathUtils.ToAbsoluteAssetPath(info.RiggedModelPath));

            if (canRecover)
                info.Status = "recovering";
            else if (!pendingMotionStage)
            {
                info.Status = "interrupted";
                info.ErrorMessage = TJGeneratorsL10n.L("生成因域重载中断且后端任务记录已丢失，请重新生成。");
                info.EndTime = DateTime.Now;
            }
            save?.Invoke();
        }

        internal static void ApplyTaskUpdate(RiggedAnimationTaskInfo task, Action<RiggedAnimationTaskInfo> mutate) =>
            Store.ApplyTaskUpdate(task, mutate);

        internal static void AddTask(RiggedAnimationTaskInfo task)
        {
            if (task == null || string.IsNullOrEmpty(task.TaskId)) return;
            Store.RegisterTask(task.TaskId, task);
        }

        public static RiggedAnimationTaskInfo GetTask(string taskId) => Store.GetTask(taskId);

        public static List<RiggedAnimationTaskInfo> GetAllTasks() => Store.GetAllTasks();

        public static RiggedAnimationTaskInfo GetTaskByRigBackendId(string backendId)
        {
            if (string.IsNullOrEmpty(backendId)) return null;
            return Store.Find(t => t.BackendRigTaskId == backendId);
        }

        public static RiggedAnimationTaskInfo GetTaskByMotionBackendId(string backendId)
        {
            if (string.IsNullOrEmpty(backendId)) return null;
            return Store.Find(t => t.BackendMotionTaskId == backendId);
        }

        public static RiggedAnimationTaskInfo CreateRecoveredTask(
            string backendId, string pipelineType, string sourceModelPath, string prefabPath, long timestampMs)
        {
            return Store.CreateRecoveredTask(backendId, () => new RiggedAnimationTaskInfo
            {
                TaskId              = $"recovered_{backendId}",
                PipelineType        = pipelineType ?? "rig_only",
                BackendRigTaskId    = pipelineType == "motion_only" ? "" : backendId,
                BackendMotionTaskId = pipelineType == "motion_only" ? backendId : "",
                SourceModelPath     = sourceModelPath ?? "",
                PrefabPath          = prefabPath ?? "",
                Status              = "recovering",
                Progress            = 0,
                StartTime           = timestampMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).LocalDateTime
                    : DateTime.Now
            });
        }

        public static void RemoveTask(string taskId) => Store.RemoveTask(taskId);
#endif
    }

#if UNITY_EDITOR
    // ─────────────────────────────────────────────────────────────────────────────
    // Domain Reload Recovery
    // ─────────────────────────────────────────────────────────────────────────────
    [InitializeOnLoad]
    public static class RiggedAnimationDomainReloadRecovery
    {
        static RiggedAnimationDomainReloadRecovery() =>
            CustomToolDomainReloadRecovery.Schedule(ResumeInterruptedTasks);

        private static void ResumeInterruptedTasks()
        {
            var allInterrupted = TJGeneratorsTaskRecovery.GetAllInterruptedTasks();

            // ── unirig tasks (Stage 1) ────────────────────────────────────────
            foreach (var t in allInterrupted.Where(
                t => t.modelVersion == "unirig" && !TJGeneratorsTaskRecovery.IsRecovering(t.backendTaskId)))
            {
                var tracker = RiggedAnimationTaskTracker.GetTaskByRigBackendId(t.backendTaskId);
                if (tracker == null) continue; // belongs to UI window, skip

                TJGeneratorsTaskRecovery.MarkAsRecovering(t.backendTaskId);
                RiggedAnimationTaskTracker.ApplyTaskUpdate(tracker, task => task.Status = "recovering");

                var cfg = ConfigManager.GetGeneratorConfig(ConfigType.Generator, "unirig");
                if (cfg == null) continue;

                var gen = new DynamicGenerator(cfg);
                gen.RestoreFromInterruptedTask(t);
                gen.SetFileUploadPath(tracker.SourceModelPath);

                var host     = new RigModelPipelineHost(
                    tracker, tracker.SourceModelPath, tracker.RiggedModelPath, gen, tracker.SessionId);
                var pipeline = new GenerationPipeline(
                    host, ConfigType.Generator, GenerationRequestOrigin.Agent, tracker.SessionId);
                TJLog.Log($"[RiggedAnimationDomainReloadRecovery] Resuming unirig task: {t.backendTaskId}");
                EditorCoroutineUtility.StartCoroutineOwnerless(pipeline.PollTaskStatus(gen, t.backendTaskId));
            }

            // ── hunyuan-motion tasks (Stage 2) ────────────────────────────────
            foreach (var t in allInterrupted.Where(
                t => t.modelVersion == "hunyuan-motion" && !TJGeneratorsTaskRecovery.IsRecovering(t.backendTaskId)))
            {
                var tracker = RiggedAnimationTaskTracker.GetTaskByMotionBackendId(t.backendTaskId);
                if (tracker == null) continue; // belongs to GenerationPipeline post-processing, skip

                TJGeneratorsTaskRecovery.MarkAsRecovering(t.backendTaskId);
                RiggedAnimationTaskTracker.ApplyTaskUpdate(tracker, task => task.Status = "recovering");

                var motionCfg = ConfigManager.GetGeneratorConfig(ConfigType.Generator, "hunyuan-motion");
                if (motionCfg == null) continue;

                var motionGen = new DynamicGenerator(motionCfg);
                motionGen.RestoreFromInterruptedTask(t);

                string motionSave = BuildMotionSavePath(tracker.RiggedModelPath);
                var motionHost    = new ModelMotionPipelineHost(
                    tracker, motionSave, motionGen, tracker.SessionId);
                var pipeline      = new GenerationPipeline(
                    motionHost, ConfigType.Generator, GenerationRequestOrigin.Agent, tracker.SessionId);
                TJLog.Log($"[RiggedAnimationDomainReloadRecovery] Resuming hunyuan-motion task: {t.backendTaskId}");
                EditorCoroutineUtility.StartCoroutineOwnerless(pipeline.PollTaskStatus(motionGen, t.backendTaskId));
            }

            // ── rig_and_motion Stage 2 尚未提交（rigging_complete）────────────────
            foreach (var tracker in RiggedAnimationTaskTracker.GetAllTasks().Where(t =>
                t.PipelineType == "rig_and_motion"
                && t.Status == "rigging_complete"
                && string.IsNullOrEmpty(t.BackendMotionTaskId)))
            {
                if (string.IsNullOrEmpty(tracker.RiggedModelPath)) continue;
                if (!File.Exists(PathUtils.ToAbsoluteAssetPath(tracker.RiggedModelPath))) continue;
                if (string.IsNullOrWhiteSpace(tracker.MotionDescription)) continue;

                TJLog.Log(
                    $"[RiggedAnimationDomainReloadRecovery] 续跑 rig_and_motion Stage 2: task={tracker.TaskId}"
                );
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    RiggedAnimationStage2Helper.LaunchMotionStage(tracker, sessionId: ""));
            }
        }

        internal static string BuildMotionSavePath(string riggedModelPath)
        {
            if (string.IsNullOrEmpty(riggedModelPath)) return "";
            string dir      = Path.GetDirectoryName(riggedModelPath)?.Replace("\\", "/") ?? "";
            string baseName = Path.GetFileNameWithoutExtension(riggedModelPath);
            if (baseName.EndsWith("_rigged", StringComparison.OrdinalIgnoreCase))
                baseName = baseName.Substring(0, baseName.Length - "_rigged".Length);
            return Path.Combine(dir, baseName + "_motion.fbx").Replace("\\", "/");
        }
    }

    /// <summary>
    /// rig_and_motion Stage 2（混元 Motion）提交与轮询；供 RigModelPipelineHost 与域重载恢复共用。
    /// </summary>
    internal static class RiggedAnimationStage2Helper
    {
        internal static IEnumerator LaunchMotionStage(
            RiggedAnimationTaskTracker.RiggedAnimationTaskInfo task,
            string sessionId = "")
        {
            if (task == null) yield break;

            if (string.IsNullOrEmpty(sessionId))
                sessionId = task.SessionId ?? "";

            var motionCfg = ConfigManager.GetGeneratorConfig(ConfigType.Generator, "hunyuan-motion");
            if (motionCfg == null)
            {
                MarkMotionFailed(task, "Cannot find hunyuan-motion config.", sessionId);
                yield break;
            }

            var motionGen = new DynamicGenerator(motionCfg);
            motionGen.SetTextPrompt(task.MotionDescription);
            motionGen.SetParameter("actionDuration", task.ActionDuration);
            motionGen.SetParameter("cfgStrength",    task.CfgStrength);
            motionGen.SetParameter("randomSeedList", task.RandomSeedList ?? "0");

            var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(motionGen, sessionId);
            if (!submitResult.Success)
            {
                MarkMotionFailed(task, submitResult.Message, sessionId);
                yield break;
            }

            RiggedAnimationTaskTracker.ApplyTaskUpdate(task, t =>
            {
                t.BackendMotionTaskId = submitResult.BackendTaskId;
                t.Status              = "generating_motion";
            });

            string motionSavePath = RiggedAnimationDomainReloadRecovery.BuildMotionSavePath(task.RiggedModelPath);
            var motionHost        = new ModelMotionPipelineHost(task, motionSavePath, motionGen, sessionId);
            var pipeline          = new GenerationPipeline(motionHost, ConfigType.Generator, GenerationRequestOrigin.Agent, sessionId);

            string motionHistoryGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(
                !string.IsNullOrEmpty(task.PrefabPath) ? task.PrefabPath : task.RiggedModelPath);

            EditorCoroutineUtility.StartCoroutineOwnerless(
                pipeline.StartFromSubmittedTask(motionGen, motionHistoryGuid, submitResult.BackendTaskId, null));

            yield return null;
        }

        internal static void MarkMotionFailed(
            RiggedAnimationTaskTracker.RiggedAnimationTaskInfo task,
            string message,
            string sessionId = "")
        {
            if (string.IsNullOrEmpty(sessionId))
                sessionId = task?.SessionId ?? "";

            RiggedAnimationTaskTracker.ApplyTaskUpdate(task, t =>
            {
                t.Status       = "rigging_complete_motion_failed";
                t.ErrorMessage = message;
                t.EndTime      = DateTime.Now;
            });
            GenerationNotifier.NotifyFailed("generate_animated_character", task.TaskId, task.BackendRigTaskId, message,
                new JObject
                {
                    ["session_id"]    = sessionId,
                    ["pipeline_type"] = "rig_and_motion"
                });
            TJLog.LogError($"[RiggedAnimationStage2Helper] Motion stage failed: {message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // RigModelPipelineHost  —  Stage 1 host (UniRig rigging)
    // ─────────────────────────────────────────────────────────────────────────────
    internal class RigModelPipelineHost : HeadlessPipelineHostBase, IModelDownloadPathProvider
    {
        private readonly RiggedAnimationTaskTracker.RiggedAnimationTaskInfo _task;
        private readonly string _sourceModelPath;
        private readonly string _expectedRiggedPath;
        private readonly ModelGeneratorBase _generator;
        private readonly string _sessionId;

        internal RigModelPipelineHost(
            RiggedAnimationTaskTracker.RiggedAnimationTaskInfo task,
            string sourceModelPath,
            string expectedRiggedPath,
            ModelGeneratorBase generator,
            string sessionId = "")
        {
            _task               = task;
            _sourceModelPath    = sourceModelPath;
            _expectedRiggedPath = expectedRiggedPath;
            _generator          = generator;
            _sessionId          = !string.IsNullOrEmpty(sessionId) ? sessionId : task?.SessionId ?? "";
        }

        public override TJGeneratorsAssetReference GetTargetAsset()
        {
            if (string.IsNullOrEmpty(_task?.PrefabPath)) return null;
            return TJGeneratorsAssetReference.FromPath(_task.PrefabPath);
        }

        public string GetModelDownloadPath(string resolvedSavePath)
        {
            if (string.IsNullOrEmpty(_expectedRiggedPath))
                return null;
            string ext = Path.GetExtension(resolvedSavePath);
            if (string.IsNullOrEmpty(ext))
                ext = ".fbx";
            return Path.ChangeExtension(_expectedRiggedPath, ext)?.Replace("\\", "/");
        }

        public override void Repaint()
        {
            if (_generator == null || _task == null) return;
            bool isActive = _task.Status == "rigging" || _task.Status == "recovering";
            if (!isActive) return;
            int raw      = _generator.CurrentProgress;
            int progress = _task.PipelineType == "rig_and_motion" ? raw / 2 : raw;
            if (progress > _task.Progress)
            {
                RiggedAnimationTaskTracker.ApplyTaskUpdate(_task, t =>
                {
                    t.Status   = "rigging";
                    t.Progress = progress;
                });
            }
        }

        public override void OnGenerationCompleted(string riggedPath)
        {
            if (_task == null) return;

            // Configure Humanoid, fix bone mapping, restore textures from source
            RiggedModelPostProcess.FinalizeRiggedImport(riggedPath, _sourceModelPath);

            // Assign Animator + Avatar to prefab (no controller yet for rig_only)
            if (!string.IsNullOrEmpty(_task.PrefabPath))
                ReplaceAnimatedCharacterModelTool.AssignAnimatorControllerIfMissing(_task.PrefabPath, riggedPath);

            // 5. Update tracker
            if (_task.PipelineType == "rig_only")
            {
                RiggedAnimationTaskTracker.ApplyTaskUpdate(_task, t =>
                {
                    t.RiggedModelPath = riggedPath;
                    t.Status   = "completed";
                    t.Progress = 100;
                    t.EndTime  = DateTime.Now;
                });
                GenerationNotifier.NotifyCompleted("generate_rigged_model", _task.TaskId, _task.BackendRigTaskId,
                    new JObject
                    {
                        ["session_id"]        = _sessionId,
                        ["pipeline_type"]     = "rig_only",
                        ["source_model_path"] = _task.SourceModelPath ?? "",
                        ["rigged_model_path"] = riggedPath ?? "",
                        ["prefab_path"]       = _task.PrefabPath ?? "",
                        ["progress"]          = 100,
                        ["start_time"]        = _task.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["end_time"]          = _task.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                        ["duration_seconds"]  = _task.EndTime.HasValue ? (int)(_task.EndTime.Value - _task.StartTime).TotalSeconds : 0
                    });
                TJLog.Log($"[RigModelPipelineHost] rig_only 完成: {riggedPath}");
            }
            else if (_task.PipelineType == "rig_and_motion")
            {
                RiggedAnimationTaskTracker.ApplyTaskUpdate(_task, t =>
                {
                    t.RiggedModelPath = riggedPath;
                    t.Status   = "rigging_complete";
                    t.Progress = 50;
                });
                TJLog.Log($"[RigModelPipelineHost] rig_and_motion Stage 1 完成，启动 Stage 2: {riggedPath}");
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    RiggedAnimationStage2Helper.LaunchMotionStage(_task, _sessionId));
            }
        }

        protected override string DialogLogTag => "RigModelPipelineHost";

        public override void ShowDialog(string title, string message)
        {
            base.ShowDialog(title, message);
            if (ErrorDialogUtils.IsErrorDialog(title) && _task != null)
            {
                var friendly = ErrorDialogUtils.ConvertToUserFriendlyError(title, message);
                RiggedAnimationTaskTracker.ApplyTaskUpdate(_task, t =>
                {
                    t.Status       = "failed";
                    t.ErrorMessage = friendly.TechnicalMessage;
                    t.EndTime      = DateTime.Now;
                });
                string failedTool = _task.PipelineType == "rig_only" ? "generate_rigged_model" : "generate_animated_character";
                GenerationNotifier.NotifyFailed(failedTool, _task.TaskId, _task.BackendRigTaskId, friendly.TechnicalMessage,
                    new JObject
                    {
                        ["session_id"]    = _sessionId,
                        ["pipeline_type"] = _task.PipelineType ?? ""
                    });
            }
        }

    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ModelMotionPipelineHost  —  Stage 2 host (HunyuanMotion)
    // ─────────────────────────────────────────────────────────────────────────────
    internal class ModelMotionPipelineHost : HeadlessPipelineHostBase, IModelDownloadPathProvider
    {
        private readonly RiggedAnimationTaskTracker.RiggedAnimationTaskInfo _task;
        private readonly string _motionSavePath;
        private readonly ModelGeneratorBase _generator;
        private readonly string _sessionId;

        internal ModelMotionPipelineHost(
            RiggedAnimationTaskTracker.RiggedAnimationTaskInfo task,
            string motionSavePath,
            ModelGeneratorBase generator = null,
            string sessionId = "")
        {
            _task           = task;
            _motionSavePath = motionSavePath;
            _generator      = generator;
            _sessionId      = !string.IsNullOrEmpty(sessionId) ? sessionId : task?.SessionId ?? "";
        }

        private string GetNotificationToolName()
        {
            return _task?.PipelineType == "motion_only"
                ? "generate_model_motion"
                : "generate_animated_character";
        }

        // Returning null prevents GenerationPipeline.BindModelToPrefab from replacing the prefab
        // with the motion FBX — the prefab should keep the rigged model.
        public override TJGeneratorsAssetReference GetTargetAsset() => null;

        public string GetModelDownloadPath(string resolvedSavePath)
        {
            if (string.IsNullOrEmpty(_motionSavePath))
                return null;
            string ext = Path.GetExtension(resolvedSavePath);
            if (string.IsNullOrEmpty(ext))
                ext = ".fbx";
            return Path.ChangeExtension(_motionSavePath, ext)?.Replace("\\", "/");
        }

        public override void Repaint()
        {
            if (_task == null || _generator == null) return;
            bool isActive = _task.Status == "generating_motion" || _task.Status == "recovering";
            if (!isActive) return;
            int raw      = _generator.CurrentProgress;
            int progress = _task.PipelineType == "rig_and_motion" ? 50 + raw / 2 : raw;
            if (progress > _task.Progress)
            {
                RiggedAnimationTaskTracker.ApplyTaskUpdate(_task, t => t.Progress = progress);
            }
        }

        public override void OnGenerationCompleted(string motionFbxPath)
        {
            if (_task == null) return;

            // 1. Configure as Humanoid animation import
            RiggedModelPostProcess.SetupAnimationImport(motionFbxPath, _task?.LoopTime ?? true);

            // 2. Reimport so animation clips are extractable
            PathUtils.SafeRefresh();

            // 3. Create single-clip loop AnimatorController named after rigged model
            string riggedDir      = string.IsNullOrEmpty(_task.RiggedModelPath) ? "" :
                                    (Path.GetDirectoryName(_task.RiggedModelPath)?.Replace("\\", "/") ?? "");
            string riggedBaseName = string.IsNullOrEmpty(_task.RiggedModelPath) ? "" :
                                    Path.GetFileNameWithoutExtension(_task.RiggedModelPath);
            string controllerPath = null;
            if (!string.IsNullOrEmpty(riggedDir) && !string.IsNullOrEmpty(riggedBaseName))
            {
                controllerPath = RiggedModelPostProcess.CreateSingleClipLoopAnimatorControllerFromMotionClip(
                    riggedDir, riggedBaseName, motionFbxPath);
            }

            // 4. Assign controller + avatar to prefab so animation loops in Play Mode
            if (!string.IsNullOrEmpty(_task.PrefabPath) && !string.IsNullOrEmpty(_task.RiggedModelPath))
                ReplaceAnimatedCharacterModelTool.AssignAnimatorControllerIfMissing(
                    _task.PrefabPath, _task.RiggedModelPath);

            // 5. Update tracker
            RiggedAnimationTaskTracker.ApplyTaskUpdate(_task, t =>
            {
                t.MotionFbxPath  = motionFbxPath;
                t.ControllerPath = controllerPath ?? "";
                t.Status         = "completed";
                t.Progress       = 100;
                t.EndTime        = DateTime.Now;
            });
            GenerationNotifier.NotifyCompleted(GetNotificationToolName(), _task.TaskId, _task.BackendMotionTaskId,
                new JObject
                {
                    ["session_id"]        = _sessionId,
                    ["pipeline_type"]     = _task.PipelineType ?? "",
                    ["source_model_path"] = _task.SourceModelPath ?? "",
                    ["rigged_model_path"] = _task.RiggedModelPath ?? "",
                    ["motion_fbx_path"]   = motionFbxPath ?? "",
                    ["controller_path"]   = controllerPath ?? "",
                    ["prefab_path"]       = _task.PrefabPath ?? "",
                    ["progress"]          = 100,
                    ["start_time"]        = _task.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["end_time"]          = _task.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                    ["duration_seconds"]  = _task.EndTime.HasValue ? (int)(_task.EndTime.Value - _task.StartTime).TotalSeconds : 0
                });

            TJLog.Log($"[ModelMotionPipelineHost] Motion 完成: {motionFbxPath}, controller={controllerPath}");
        }

        protected override string DialogLogTag => "ModelMotionPipelineHost";

        public override void ShowDialog(string title, string message)
        {
            base.ShowDialog(title, message);
            if (ErrorDialogUtils.IsErrorDialog(title) && _task != null)
            {
                var friendly = ErrorDialogUtils.ConvertToUserFriendlyError(title, message);
                RiggedAnimationTaskTracker.ApplyTaskUpdate(_task, t =>
                {
                    t.Status = _task.PipelineType == "rig_and_motion"
                        ? "rigging_complete_motion_failed"
                        : "failed";
                    t.ErrorMessage = friendly.TechnicalMessage;
                    t.EndTime      = DateTime.Now;
                });
                GenerationNotifier.NotifyFailed(GetNotificationToolName(), _task.TaskId, _task.BackendMotionTaskId,
                    friendly.TechnicalMessage,
                    new JObject
                    {
                        ["session_id"]    = _sessionId,
                        ["pipeline_type"] = _task.PipelineType ?? ""
                    });
            }
        }
    }
#endif

    // ─────────────────────────────────────────────────────────────────────────────
    // Tool A: generate_rigged_model
    // ─────────────────────────────────────────────────────────────────────────────
    public static class GenerateRiggedModelTool
    {
        private static int _taskIdCounter = 0;

        [ExecuteCustomTool.CustomTool("generate_rigged_model",
            "Rig an existing 3D model (FBX/OBJ) into a Humanoid skeleton using UniRig AI. " +
            "Output: a rigged Humanoid FBX + a Capsule placeholder Prefab with Animator (T-Pose, no animation). " +
            "Use this when you only need rigging/skinning without motion animation. " +
            "For rigging + motion in one step use generate_animated_character instead. " +
            "Parameters: source_model_path (required, path to FBX/OBJ in Assets), " +
            "prefab_output_path (optional, defaults to History/), " +
            "force_overwrite (bool, default false). " +
            "Takes ~1-3 minutes. Poll with query_rigged_model_status after 5 seconds.")]
        public static object GenerateRiggedModel(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                string sourceModelPath  = parameters["source_model_path"]?.ToString();
                string prefabOutputPath = parameters["prefab_output_path"]?.ToString();
                bool   forceOverwrite   = parameters["force_overwrite"]?.ToObject<bool>() ?? false;
                string sessionId        = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());

                if (string.IsNullOrEmpty(sourceModelPath))
                    return Fail("'source_model_path' parameter is required");

                if (!File.Exists(PathUtils.ToAbsoluteAssetPath(sourceModelPath)))
                    return Fail($"Source model not found: {sourceModelPath}");

                prefabOutputPath = RiggedAnimatedModelHelpers.ResolvePrefabPath(
                    prefabOutputPath, sourceModelPath, "RiggedModel", forceOverwrite);
                if (prefabOutputPath == null)
                    return Fail("Failed to resolve prefab output path");

                var config = ConfigManager.GetGeneratorConfig(ConfigType.Generator, "unirig");
                if (config == null)
                    return Fail("Cannot find 'unirig' generator config.");

                var generator = new DynamicGenerator(config);
                generator.SetFileUploadPath(sourceModelPath);

                string srcDir      = Path.GetDirectoryName(sourceModelPath)?.Replace("\\", "/") ?? "";
                string srcBase     = Path.GetFileNameWithoutExtension(sourceModelPath);
                string expectedRig = Path.Combine(srcDir, srcBase + "_rigged.fbx").Replace("\\", "/");

                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                    return new Dictionary<string, object>
                    {
                        { "success", false }, { "error_code", submitResult.ErrorCode },
                        { "message", submitResult.Message }
                    };

                string createdPrefabPath = ReplaceAnimatedCharacterModelTool.CreateBlankPrefab(prefabOutputPath);
                if (string.IsNullOrEmpty(createdPrefabPath))
                    return Fail($"Failed to create prefab at: {prefabOutputPath}");

                TJGeneratorsGenerationLabel.EnableSessionLabel(
                    TJGeneratorsAssetReference.FromPath(createdPrefabPath), sessionId);

                string taskId = $"rig_only_{++_taskIdCounter}_{DateTime.Now.Ticks}";
                var task = new RiggedAnimationTaskTracker.RiggedAnimationTaskInfo
                {
                    TaskId           = taskId,
                    SessionId        = sessionId,
                    PipelineType     = "rig_only",
                    Status           = "rigging",
                    Progress         = 0,
                    SourceModelPath  = sourceModelPath,
                    RiggedModelPath  = expectedRig,
                    PrefabPath       = createdPrefabPath,
                    BackendRigTaskId = submitResult.BackendTaskId,
                    StartTime        = DateTime.Now
                };
                RiggedAnimationTaskTracker.AddTask(task);

                var host     = new RigModelPipelineHost(task, sourceModelPath, expectedRig, generator, sessionId);
                var pipeline = new GenerationPipeline(host, ConfigType.Generator, GenerationRequestOrigin.Agent, sessionId);
                string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(createdPrefabPath);
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId, null));

                TJLog.Log($"[GenerateRiggedModelTool] 任务已提交 task_id={taskId}, backend={submitResult.BackendTaskId}");

                return new Dictionary<string, object>
                {
                    { "success",              true },
                    { "submission_success",   true },
                    { "task_id",              taskId },
                    { "backend_task_id",      submitResult.BackendTaskId },
                    { "status",               "rigging" },
                    { "generator_id",         "unirig" },
                    { "source_model_path",    sourceModelPath },
                    { "prefab_output_path",   createdPrefabPath },
                    { "expected_rigged_path", expectedRig },
                    { "estimated_wait_seconds", 120 },
                    { "notification_mode",    "bg_task_done" },
                    { "preview_url",          PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "message",
                        "Rigging started. A Capsule placeholder prefab is created immediately. " +
                        "STEP 1 (do now): Instantiate the prefab. " +
                        "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                        "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn (~2 min) " +
                        "containing ALL results (rigged_model_path, prefab_path, timing, etc.). " +
                        "*** POLLING IS STRICTLY FORBIDDEN — only call query_rigged_model_status ONCE as a last-resort fallback. ***" }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateRiggedModelTool] Error: {e}");
                return Fail($"Error: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

        [ExecuteCustomTool.CustomTool("query_rigged_model_status",
            "Query the status of a rigged model generation task (generate_rigged_model). Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "Status: 'initializing', 'rigging' (0-100%), 'completed', 'failed', 'recovering', 'interrupted'. " +
            "When completed: rigged_model_path contains the Humanoid FBX, prefab_path the placeholder Prefab. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QueryStatus(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                string taskId = parameters["task_id"]?.ToString();
                if (string.IsNullOrEmpty(taskId))
                    return Fail("'task_id' is required");

                var task = RiggedAnimationTaskTracker.GetTask(taskId);
                if (task == null)
                    return Fail($"Task '{taskId}' not found.");

                return RiggedAnimatedModelHelpers.BuildStatusResult(task);
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateRiggedModelTool] Query error: {e}");
                return Fail($"Error: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

        [ExecuteCustomTool.CustomTool("list_rigged_model_tasks",
            "List all active and recent rigged model generation tasks (generate_rigged_model).")]
        public static object ListTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks    = RiggedAnimationTaskTracker.GetAllTasks()
                                   .Where(t => t.PipelineType == "rig_only").ToList();
                var taskList = tasks.Select(t => RiggedAnimatedModelHelpers.BuildStatusResult(t)).ToList();
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "tasks",   taskList },
                    { "count",   taskList.Count }
                };
            }
            catch (Exception e)
            {
                return Fail($"Error: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

        private static Dictionary<string, object> Fail(string message) =>
            new Dictionary<string, object> { { "success", false }, { "message", message } };
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Tool B: generate_model_motion
    // ─────────────────────────────────────────────────────────────────────────────
    public static class GenerateModelMotionTool
    {
        private static int _taskIdCounter = 0;

        [ExecuteCustomTool.CustomTool("generate_model_motion",
            "Generate motion animation for an already-rigged Humanoid FBX model using HunyuanMotion. " +
            "Output: motion FBX + AnimatorController that auto-loops in Play Mode. " +
            "Use when you already have a Humanoid FBX and only need motion animation. " +
            "For rigging + motion from a raw model use generate_animated_character instead. " +
            "Parameters: rigged_model_path (required, path to Humanoid FBX in Assets), " +
            "motion_description (required, e.g. 'a backflip'), " +
            "target_prefab_path (optional, prefab to assign controller+avatar to), " +
            "action_duration (float, seconds, default 5), " +
            "cfg_strength (float, default 5), " +
            "random_seed (int, 0=server random, default 0), " +
            "loop (optional bool, default true; set false for one-shot non-looping motions). " +
            "Takes ~1-2 minutes. Poll with query_model_motion_status after 5 seconds.")]
        public static object GenerateModelMotion(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                string riggedModelPath   = parameters["rigged_model_path"]?.ToString();
                string motionDescription = parameters["motion_description"]?.ToString();
                string targetPrefabPath  = parameters["target_prefab_path"]?.ToString();
                float  actionDuration    = parameters["action_duration"]?.ToObject<float>() ?? 5f;
                float  cfgStrength       = parameters["cfg_strength"]?.ToObject<float>()    ?? 5f;
                int    seed              = parameters["random_seed"]?.ToObject<int>()        ?? 0;
                bool   loopTime          = parameters["loop"]?.ToObject<bool>()             ?? true;
                string sessionId         = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());

                if (string.IsNullOrEmpty(riggedModelPath))
                    return Fail("'rigged_model_path' is required");
                if (string.IsNullOrEmpty(motionDescription))
                    return Fail("'motion_description' is required");
                if (!File.Exists(PathUtils.ToAbsoluteAssetPath(riggedModelPath)))
                    return Fail($"Rigged model not found: {riggedModelPath}");

                string randomSeedList = seed.ToString();

                var config = ConfigManager.GetGeneratorConfig(ConfigType.Generator, "hunyuan-motion");
                if (config == null)
                    return Fail("Cannot find 'hunyuan-motion' generator config.");

                var generator = new DynamicGenerator(config);
                generator.SetTextPrompt(motionDescription);
                generator.SetParameter("actionDuration", actionDuration);
                generator.SetParameter("cfgStrength",    cfgStrength);
                generator.SetParameter("randomSeedList", randomSeedList);

                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                    return new Dictionary<string, object>
                    {
                        { "success", false }, { "error_code", submitResult.ErrorCode },
                        { "message", submitResult.Message }
                    };

                string taskId = $"motion_only_{++_taskIdCounter}_{DateTime.Now.Ticks}";
                var task = new RiggedAnimationTaskTracker.RiggedAnimationTaskInfo
                {
                    TaskId              = taskId,
                    SessionId           = sessionId,
                    PipelineType        = "motion_only",
                    Status              = "generating_motion",
                    Progress            = 0,
                    RiggedModelPath     = riggedModelPath,
                    PrefabPath          = targetPrefabPath ?? "",
                    MotionDescription   = motionDescription,
                    BackendMotionTaskId = submitResult.BackendTaskId,
                    ActionDuration      = actionDuration,
                    CfgStrength         = cfgStrength,
                    RandomSeedList      = randomSeedList,
                    LoopTime            = loopTime,
                    StartTime           = DateTime.Now
                };
                RiggedAnimationTaskTracker.AddTask(task);

                string motionSavePath = RiggedAnimationDomainReloadRecovery.BuildMotionSavePath(riggedModelPath);
                var host     = new ModelMotionPipelineHost(task, motionSavePath, generator, sessionId);
                var pipeline = new GenerationPipeline(host, ConfigType.Generator, GenerationRequestOrigin.Agent, sessionId);
                string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(
                    !string.IsNullOrEmpty(targetPrefabPath) ? targetPrefabPath : riggedModelPath);
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId, null));

                TJLog.Log($"[GenerateModelMotionTool] 任务已提交 task_id={taskId}, backend={submitResult.BackendTaskId}");

                return new Dictionary<string, object>
                {
                    { "success",                              true },
                    { "submission_success",                   true },
                    { "task_id",                              taskId },
                    { "backend_task_id",                      submitResult.BackendTaskId },
                    { "status",                               "generating_motion" },
                    { "generator_id",                         "hunyuan-motion" },
                    { "rigged_model_path",                    riggedModelPath },
                    { "motion_description",                   motionDescription },
                    { "estimated_wait_seconds", 90 },
                    { "notification_mode",    "bg_task_done" },
                    { "preview_url",          PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "message",
                        "Motion generation started. " +
                        "STEP 1 (do now): Note the task_id. " +
                        "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                        "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn (~90s) " +
                        "containing ALL results (motion_fbx_path, controller_path, timing, etc.). " +
                        "*** POLLING IS STRICTLY FORBIDDEN — only call query_model_motion_status ONCE as a last-resort fallback. ***" }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateModelMotionTool] Error: {e}");
                return Fail($"Error: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

        [ExecuteCustomTool.CustomTool("query_model_motion_status",
            "Query the status of a model motion generation task (generate_model_motion). " +
            "Status: 'generating_motion' (0-100%), 'completed', 'failed', 'recovering', 'interrupted'. " +
            "When completed: motion_fbx_path, controller_path, rigged_model_path are returned. " +
            "Enter Play Mode to see the animation loop automatically.")]
        public static object QueryStatus(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                string taskId = parameters["task_id"]?.ToString();
                if (string.IsNullOrEmpty(taskId))
                    return Fail("'task_id' is required");

                var task = RiggedAnimationTaskTracker.GetTask(taskId);
                if (task == null)
                    return Fail($"Task '{taskId}' not found.");

                return RiggedAnimatedModelHelpers.BuildStatusResult(task);
            }
            catch (Exception e)
            {
                return Fail($"Error: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

        [ExecuteCustomTool.CustomTool("list_model_motion_tasks",
            "List all active and recent motion generation tasks (generate_model_motion).")]
        public static object ListTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks    = RiggedAnimationTaskTracker.GetAllTasks()
                                   .Where(t => t.PipelineType == "motion_only").ToList();
                var taskList = tasks.Select(t => RiggedAnimatedModelHelpers.BuildStatusResult(t)).ToList();
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "tasks",   taskList },
                    { "count",   taskList.Count }
                };
            }
            catch (Exception e)
            {
                return Fail($"Error: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

        private static Dictionary<string, object> Fail(string message) =>
            new Dictionary<string, object> { { "success", false }, { "message", message } };
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Tool C: generate_animated_character  (UniRig + HunyuanMotion)
    // ─────────────────────────────────────────────────────────────────────────────
    public static class GenerateAnimatedCharacterTool
    {
        private static int _taskIdCounter = 0;

        [ExecuteCustomTool.CustomTool("generate_animated_character",
            "Rig an existing 3D model AND generate motion animation in one shot: " +
            "Stage 1 uses UniRig AI to rig the model into a Humanoid skeleton; " +
            "Stage 2 uses HunyuanMotion to generate the requested motion. " +
            "Requires source_model_path (existing FBX/OBJ). This tool does NOT accept a text-to-3D prompt, action_id, topology, or other Meshy-style params. " +
            "To create a character from scratch (same as UI 添加动作): call generate_3d_model_by_tripo_p1 or generate_3d_model_by_rodin with add_motion=true and motion_description. " +
            "For rigging only use generate_rigged_model. For motion on an already-rigged model use generate_model_motion. " +
            "Parameters: source_model_path (required), motion_description (required, e.g. 'a running cycle'), " +
            "prefab_output_path (optional), force_overwrite (bool, default false), " +
            "action_duration (float, seconds, default 5), cfg_strength (float, default 5), " +
            "random_seed (int, 0=server random, default 0), " +
            "loop (optional bool, default true; set false for one-shot non-looping motions). " +
            "Takes ~2-5 minutes total. After submit, END THE TURN and wait for <bg_task_done>; " +
            "use query_animated_character_status only once as a last-resort fallback.")]
        public static object GenerateCharacter(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                string sourceModelPath   = parameters["source_model_path"]?.ToString();
                string motionDescription = parameters["motion_description"]?.ToString();
                string prefabOutputPath  = parameters["prefab_output_path"]?.ToString();
                bool   forceOverwrite    = parameters["force_overwrite"]?.ToObject<bool>() ?? false;
                float  actionDuration    = parameters["action_duration"]?.ToObject<float>() ?? 5f;
                float  cfgStrength       = parameters["cfg_strength"]?.ToObject<float>()    ?? 5f;
                int    seed              = parameters["random_seed"]?.ToObject<int>()        ?? 0;
                bool   loopTime          = parameters["loop"]?.ToObject<bool>()             ?? true;
                string sessionId         = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());

                if (string.IsNullOrEmpty(sourceModelPath))
                {
                    bool looksLikeFromScratch =
                        !string.IsNullOrEmpty(parameters["prompt"]?.ToString()) ||
                        parameters["action_id"] != null ||
                        parameters["topology"] != null ||
                        parameters["pose_mode"] != null;
                    if (looksLikeFromScratch)
                        return Fail(
                            "'source_model_path' is required. From scratch, do NOT call this tool with a prompt or Meshy params (action_id/topology/pose_mode). " +
                            "Call generate_3d_model_by_tripo_p1 (default) or generate_3d_model_by_rodin with add_motion=true and motion_description " +
                            "(same as UI: 3D model → UniRig → HunyuanMotion).");
                    return Fail("'source_model_path' is required");
                }
                if (string.IsNullOrEmpty(motionDescription))
                    return Fail("'motion_description' is required");
                if (!File.Exists(PathUtils.ToAbsoluteAssetPath(sourceModelPath)))
                    return Fail($"Source model not found: {sourceModelPath}");

                prefabOutputPath = RiggedAnimatedModelHelpers.ResolvePrefabPath(
                    prefabOutputPath, sourceModelPath, "RiggedAnimatedModel", forceOverwrite);
                if (prefabOutputPath == null)
                    return Fail("Failed to resolve prefab output path");

                string randomSeedList = seed.ToString();

                var config = ConfigManager.GetGeneratorConfig(ConfigType.Generator, "unirig");
                if (config == null)
                    return Fail("Cannot find 'unirig' generator config.");

                var generator = new DynamicGenerator(config);
                generator.SetFileUploadPath(sourceModelPath);

                string srcDir      = Path.GetDirectoryName(sourceModelPath)?.Replace("\\", "/") ?? "";
                string srcBase     = Path.GetFileNameWithoutExtension(sourceModelPath);
                string expectedRig = Path.Combine(srcDir, srcBase + "_rigged.fbx").Replace("\\", "/");

                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                    return new Dictionary<string, object>
                    {
                        { "success", false }, { "error_code", submitResult.ErrorCode },
                        { "message", submitResult.Message }
                    };

                string createdPrefabPath = ReplaceAnimatedCharacterModelTool.CreateBlankPrefab(prefabOutputPath);
                if (string.IsNullOrEmpty(createdPrefabPath))
                    return Fail($"Failed to create prefab at: {prefabOutputPath}");

                TJGeneratorsGenerationLabel.EnableSessionLabel(
                    TJGeneratorsAssetReference.FromPath(createdPrefabPath), sessionId);

                string taskId = $"rig_and_motion_{++_taskIdCounter}_{DateTime.Now.Ticks}";
                var task = new RiggedAnimationTaskTracker.RiggedAnimationTaskInfo
                {
                    TaskId            = taskId,
                    SessionId         = sessionId,
                    PipelineType      = "rig_and_motion",
                    Status            = "rigging",
                    Progress          = 0,
                    SourceModelPath   = sourceModelPath,
                    RiggedModelPath   = expectedRig,
                    PrefabPath        = createdPrefabPath,
                    MotionDescription = motionDescription,
                    BackendRigTaskId  = submitResult.BackendTaskId,
                    ActionDuration    = actionDuration,
                    CfgStrength       = cfgStrength,
                    RandomSeedList    = randomSeedList,
                    LoopTime          = loopTime,
                    StartTime         = DateTime.Now
                };
                RiggedAnimationTaskTracker.AddTask(task);

                var host     = new RigModelPipelineHost(task, sourceModelPath, expectedRig, generator, sessionId);
                var pipeline = new GenerationPipeline(host, ConfigType.Generator, GenerationRequestOrigin.Agent, sessionId);
                string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(createdPrefabPath);
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId, null));

                TJLog.Log($"[GenerateAnimatedCharacterTool] 任务已提交 task_id={taskId}, backend={submitResult.BackendTaskId}");

                return new Dictionary<string, object>
                {
                    { "success",              true },
                    { "submission_success",   true },
                    { "task_id",              taskId },
                    { "backend_task_id",      submitResult.BackendTaskId },
                    { "status",               "rigging" },
                    { "generator_id",         "unirig" },
                    { "source_model_path",    sourceModelPath },
                    { "motion_description",   motionDescription },
                    { "prefab_output_path",   createdPrefabPath },
                    { "expected_rigged_path", expectedRig },
                    { "estimated_wait_seconds", 300 },
                    { "notification_mode",    "bg_task_done" },
                    { "preview_url",          PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) },
                    { "message",
                        "Rig+motion generation started (Stage 1: rigging, Stage 2: motion launches automatically). " +
                        "STEP 1 (do now): Instantiate the prefab at prefab_output_path. " +
                        "STEP 2 (critical): END THIS RESPONSE TURN immediately. " +
                        "STEP 3 (automatic): A <bg_task_done> notification will appear in your next turn (~5 min) " +
                        "containing ALL results (rigged_model_path, motion_fbx_path, controller_path, timing, etc.). " +
                        "*** POLLING IS STRICTLY FORBIDDEN — only call query_animated_character_status ONCE as a last-resort fallback. ***" }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateAnimatedCharacterTool] Error: {e}");
                return Fail($"Error: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

        [ExecuteCustomTool.CustomTool("query_animated_character_status",
            "Query the status of a rig+motion generation task (generate_animated_character). Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "Status progression: 'rigging' (0-50%) → 'rigging_complete' → 'generating_motion' (50-100%) → 'completed'. " +
            "Failure modes: 'failed' (rigging failed), 'rigging_complete_motion_failed' (rigging ok but motion failed). " +
            "When completed: rigged_model_path, motion_fbx_path, controller_path, prefab_path all returned. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QueryStatus(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                string taskId = parameters["task_id"]?.ToString();
                if (string.IsNullOrEmpty(taskId))
                    return Fail("'task_id' is required");

                var task = RiggedAnimationTaskTracker.GetTask(taskId);
                if (task == null)
                    return Fail($"Task '{taskId}' not found.");

                return RiggedAnimatedModelHelpers.BuildStatusResult(task);
            }
            catch (Exception e)
            {
                return Fail($"Error: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

        [ExecuteCustomTool.CustomTool("list_animated_character_tasks",
            "List all active and recent rig+motion generation tasks (generate_animated_character).")]
        public static object ListTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                var tasks    = RiggedAnimationTaskTracker.GetAllTasks()
                                   .Where(t => t.PipelineType == "rig_and_motion").ToList();
                var taskList = tasks.Select(t => RiggedAnimatedModelHelpers.BuildStatusResult(t)).ToList();
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "tasks",   taskList },
                    { "count",   taskList.Count }
                };
            }
            catch (Exception e)
            {
                return Fail($"Error: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

        private static Dictionary<string, object> Fail(string message) =>
            new Dictionary<string, object> { { "success", false }, { "message", message } };
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────────────────────────────
#if UNITY_EDITOR
    internal static class RiggedAnimatedModelHelpers
    {
        internal static string ResolvePrefabPath(
            string prefabOutputPath, string sourceModelPath, string fallbackPrefix, bool forceOverwrite)
        {
            if (string.IsNullOrEmpty(prefabOutputPath))
            {
                string baseName = Path.GetFileNameWithoutExtension(sourceModelPath ?? "Model");
                prefabOutputPath = $"Assets/TJGenerators/History/{fallbackPrefix}_{baseName}.prefab";
                string dir = Path.GetDirectoryName(prefabOutputPath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir))
                    PathUtils.EnsureAssetFolder(dir);
                prefabOutputPath = AssetDatabase.GenerateUniqueAssetPath(prefabOutputPath);
                if (string.IsNullOrEmpty(prefabOutputPath))
                    prefabOutputPath = $"Assets/TJGenerators/History/{fallbackPrefix}_{baseName}.prefab";
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
                        string dir = Path.GetDirectoryName(prefabOutputPath)?.Replace('\\', '/');
                        if (!string.IsNullOrEmpty(dir))
                            PathUtils.EnsureAssetFolder(dir);
                        prefabOutputPath = AssetDatabase.GenerateUniqueAssetPath(prefabOutputPath);
                    }
                }
            }
            return prefabOutputPath;
        }

        internal static Dictionary<string, object> BuildStatusResult(
            RiggedAnimationTaskTracker.RiggedAnimationTaskInfo task)
        {
            var result = new Dictionary<string, object>
            {
                { "success",       true },
                { "task_id",       task.TaskId },
                { "pipeline_type", task.PipelineType },
                { "status",        task.Status },
                { "progress",      task.Progress },
                { "start_time",    task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
            };

            if (!string.IsNullOrEmpty(task.PrefabPath))        result["prefab_path"]       = task.PrefabPath;
            if (!string.IsNullOrEmpty(task.SourceModelPath))   result["source_model_path"] = task.SourceModelPath;
            if (!string.IsNullOrEmpty(task.RiggedModelPath))   result["rigged_model_path"] = task.RiggedModelPath;
            if (!string.IsNullOrEmpty(task.MotionFbxPath))     result["motion_fbx_path"]   = task.MotionFbxPath;
            if (!string.IsNullOrEmpty(task.ControllerPath))    result["controller_path"]   = task.ControllerPath;
            if (!string.IsNullOrEmpty(task.MotionDescription)) result["motion_description"] = task.MotionDescription;
            if (!string.IsNullOrEmpty(task.ErrorMessage))      result["error"]             = task.ErrorMessage;

            if (task.EndTime.HasValue)
            {
                result["end_time"]         = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                result["duration_seconds"] = (int)(task.EndTime.Value - task.StartTime).TotalSeconds;
            }


            if (task.Status == "interrupted")
            {
                if (task.PipelineType == "motion_only")
                {
                    result["hint"] = "Re-generate using the same parameters.";
                }
                else if (task.PipelineType == "rig_and_motion")
                {
                    bool riggedExists = !string.IsNullOrEmpty(task.RiggedModelPath) &&
                                        File.Exists(PathUtils.ToAbsoluteAssetPath(task.RiggedModelPath));
                    if (riggedExists)
                    {
                        result["rigged_stage_completed"] = true;
                        result["hint"] = "Stage 1 (rigging) completed — '" + task.RiggedModelPath + "' exists. " +
                                         "Call generate_model_motion with this path to skip re-rigging; " +
                                         "or re-generate the full pipeline with force_overwrite=true.";
                    }
                    else
                    {
                        result["hint"] = "Re-generate using the same parameters with force_overwrite=true.";
                    }
                }
                else // rig_only
                {
                    result["hint"] = "Re-generate using the same parameters with force_overwrite=true.";
                }
            }

            if (task.Status == "completed")
                result["result_summary"] = BuildResultSummary(task);

            return result;
        }

        private static string BuildResultSummary(RiggedAnimationTaskTracker.RiggedAnimationTaskInfo task)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(task.RiggedModelPath)) parts.Add("rigged Humanoid FBX");
            if (!string.IsNullOrEmpty(task.MotionFbxPath))   parts.Add("motion FBX");
            if (!string.IsNullOrEmpty(task.ControllerPath))  parts.Add("AnimatorController (auto-loops in Play Mode)");
            if (!string.IsNullOrEmpty(task.PrefabPath))      parts.Add("Prefab with Animator");
            return $"Generation completed: {string.Join(", ", parts)}.";
        }
    }
#endif
}
