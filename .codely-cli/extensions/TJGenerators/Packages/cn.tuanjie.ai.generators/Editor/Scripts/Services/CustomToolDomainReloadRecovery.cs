#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using Unity.EditorCoroutines.Editor;
using TJGenerators.Config;
using TJGenerators.Generators;
using TJGenerators.Pipeline;
using TJGenerators.Utils;

namespace TJGenerators
{
    /// <summary>
    /// Shared domain-reload resume loop for CustomTool recoveries.
    /// Tool-specific tracker/host wiring stays in each tool via <see cref="Resume"/>.
    /// </summary>
    public static class CustomToolDomainReloadRecovery
    {
        /// <summary>
        /// Double delayCall so recovery runs after EditorWindow OnEnable delayCall.
        /// </summary>
        public static void Schedule(Action resume)
        {
            if (resume == null) return;
            EditorApplication.delayCall += () => EditorApplication.delayCall += () => resume();
        }

        public static string ResolveAssetPath(string assetGuid)
        {
            return !string.IsNullOrEmpty(assetGuid)
                ? AssetDatabase.GUIDToAssetPath(assetGuid)
                : "";
        }

        /// <summary>
        /// Mark an existing tracker task as recovering when its status is still recoverable.
        /// </summary>
        public static void MarkTrackerRecoveringIfNeeded(string status, Action setRecoveringAndSave)
        {
            if (setRecoveringAndSave == null) return;
            if (TJGeneratorsTaskRecovery.IsRecoverableTrackerStatus(status))
                setRecoveringAndSave();
        }

        /// <summary>
        /// Filter interrupted tasks, load configs, mark recovering, restore generators, then
        /// delegate tool-specific host/pipeline start to <paramref name="resumeOne"/>.
        /// </summary>
        public static void Resume(
            string logTag,
            ConfigType configType,
            Func<InterruptedTaskData, bool> matchesTool,
            Action loadTrackers,
            Action<InterruptedTaskData, GeneratorConfig, DynamicGenerator> resumeOne)
        {
            if (matchesTool == null || resumeOne == null) return;

            var managedTasks = TJGeneratorsTaskRecovery.GetAllInterruptedTasks()
                .Where(t => matchesTool(t) && !TJGeneratorsTaskRecovery.IsRecovering(t.backendTaskId))
                .ToList();

            if (managedTasks.Count == 0) return;

            TJLog.Log($"[{logTag}] Resuming {managedTasks.Count} interrupted task(s) after domain reload.");

            loadTrackers?.Invoke();

            foreach (var interrupted in managedTasks)
            {
                var config = ConfigManager.GetGeneratorConfig(configType, interrupted.modelVersion);
                if (config == null)
                {
                    TJLog.LogWarning($"[{logTag}] Cannot find config '{interrupted.modelVersion}' for task recovery. Skipping (record kept for next reload).");
                    continue;
                }

                TJGeneratorsTaskRecovery.MarkAsRecovering(interrupted.backendTaskId);

                var generator = new DynamicGenerator(config);
                generator.RestoreFromInterruptedTask(interrupted);

                resumeOne(interrupted, config, generator);
            }
        }

        public static void StartPolling(
            string logTag,
            IGenerationPipelineHost host,
            ConfigType configType,
            string sessionId,
            string toolName,
            DynamicGenerator generator,
            string backendTaskId)
        {
            var pipeline = new GenerationPipeline(
                host, configType, GenerationRequestOrigin.Agent, sessionId ?? "", toolName ?? "");
            if (string.IsNullOrEmpty(toolName))
                TJLog.Log($"[{logTag}] Resuming backend task: {backendTaskId}");
            else
                TJLog.Log($"[{logTag}] Resuming backend task ({toolName}): {backendTaskId}");
            EditorCoroutineUtility.StartCoroutineOwnerless(
                pipeline.PollTaskStatus(generator, backendTaskId));
        }
    }
}
#endif
