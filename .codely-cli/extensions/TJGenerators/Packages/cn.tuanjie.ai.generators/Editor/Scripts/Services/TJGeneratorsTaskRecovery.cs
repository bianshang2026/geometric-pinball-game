#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using TJGenerators.Utils;

namespace TJGenerators
{
    [Serializable]
    public class InterruptedTaskData
    {
        public string backendTaskId;      // 后端返回的任务ID
        public string localTaskId;        // 本地唯一ID（用于关联历史记录）
        public string prompt;
        public string imagePath;          // 图生模型时使用
        public string modelVersion;
        public bool isTextToModel;
        public long timestamp;
        public string sessionId;
        public string targetAssetGuid;
        public string status;
        public string toolName;

        public int faceLimit;
        public bool texture;
        public bool pbr;

        /// <summary>UI / CustomTool「添加动作」：主模型完成后走 UniRig + HunyuanMotion。</summary>
        public bool addMotion;
        /// <summary>混元 Motion 的 inputText；仅 addMotion 为 true 时有意义。</summary>
        public string motionDescription;

        /// <summary>Sprite sequence: animation_type (idle / frontRun / backRun).</summary>
        public string animationType;
        /// <summary>Sprite sequence: AnimationClip fps; 0 = unset.</summary>
        public int fps;
        /// <summary>Sprite sequence: whether loop was explicitly set on submit.</summary>
        public bool loopSpecified;
        /// <summary>Sprite sequence: AnimationClip loop (meaningful when loopSpecified).</summary>
        public bool loop;

        /// <summary>Image layering: requested layer count; 0 = unset.</summary>
        public int numLayers;
    }

    public static class TJGeneratorsTaskRecovery
    {
        private const string InterruptedTasksFilePath = "Library/AI.TJGenerators/InterruptedTasks.json";

        public static readonly string SessionId = Guid.NewGuid().ToString();

        private static readonly long SessionStartUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        private static List<InterruptedTaskData> s_Tasks;
        private static readonly HashSet<string> s_Recovering = new HashSet<string>();

#if TJGENERATORS_DEBUG
        /// <summary>
        /// 本地轮询中止代数：每次 RequestAbortAllLocalPolling 递增。
        /// 进行中的 PollTaskStatus 在循环里比对捕获的代数，不一致则停止继续请求。
        /// </summary>
        private static int s_localPollAbortEpoch;
#endif

        private static readonly HashSet<string> RecoverableTrackerStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "initializing",
            "generating",
            "running",
            "processing",
            "pending",
            "interrupted",
            "recovering"
        };

        static TJGeneratorsTaskRecovery() => Load();

        // ── Persistence ────────────────────────────────────────────────────

        private static void Load()
        {
            if (!File.Exists(InterruptedTasksFilePath))
            {
                s_Tasks = new List<InterruptedTaskData>();
                return;
            }

            try
            {
                var json = File.ReadAllText(InterruptedTasksFilePath);
                s_Tasks = JsonUtility.FromJson<TaskListWrapper>(json)?.tasks ?? new List<InterruptedTaskData>();

                int removed = s_Tasks.RemoveAll(t =>
                    t == null ||
                    string.IsNullOrEmpty(t.backendTaskId) ||
                    t.status == "failed" ||
                    t.status == "error" ||
                    t.status == "cancelled");

                if (removed > 0) Save();

                TJLog.Log($"[TJGeneratorsTaskRecovery] 加载了 {s_Tasks.Count} 个中断的任务");
            }
            catch (Exception ex)
            {
                TJLog.LogWarning($"[TJGeneratorsTaskRecovery] 加载中断任务失败: {ex.Message}");
                s_Tasks = new List<InterruptedTaskData>();
            }
        }

        private static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(InterruptedTasksFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(InterruptedTasksFilePath, JsonUtility.ToJson(new TaskListWrapper { tasks = s_Tasks }, true));
            }
            catch (Exception ex)
            {
                TJLog.LogError($"[TJGeneratorsTaskRecovery] 保存中断任务失败: {ex.Message}");
            }
        }

        [Serializable]
        private class TaskListWrapper
        {
            public List<InterruptedTaskData> tasks = new List<InterruptedTaskData>();
        }

        // ── CRUD ───────────────────────────────────────────────────────────

        public static void AddInterruptedTask(InterruptedTaskData data)
        {
            if (s_Tasks == null) s_Tasks = new List<InterruptedTaskData>();

            var idx = s_Tasks.FindIndex(t => t.backendTaskId == data.backendTaskId);
            if (idx >= 0) s_Tasks[idx] = data;
            else s_Tasks.Add(data);

            Save();
            TJLog.Log($"[TJGeneratorsTaskRecovery] 添加中断任务: {data.backendTaskId}");
        }

        /// <returns>true 表示成功移除；false 表示任务不存在（已被其他协程移除）</returns>
        /// <param name="clearRecovering">
        /// When false, keeps the in-memory recovering flag so LoadFromSession does not treat the
        /// task as permanently interrupted during the RemoveInterruptedTask → MarkCompleted window
        /// (download still in progress). Caller must ClearRecovering when the host callback finishes,
        /// or rely on a later RemoveInterruptedTask(clearRecovering: true).
        /// </param>
        public static bool RemoveInterruptedTask(string backendTaskId, bool clearRecovering = true)
        {
            if (s_Tasks == null) return false;

            var removed = s_Tasks.RemoveAll(t => t.backendTaskId == backendTaskId);
            if (removed > 0)
            {
                Save();
                TJLog.Log($"[TJGeneratorsTaskRecovery] 移除中断任务: {backendTaskId}");
            }

            if (clearRecovering)
                s_Recovering.Remove(backendTaskId);
            return removed > 0;
        }

        public static void UpdateTaskStatus(string backendTaskId, string status)
        {
            var task = s_Tasks?.Find(t => t.backendTaskId == backendTaskId);
            if (task == null) return;
            task.status = status;
            Save();
        }

        // ── Queries (public) ───────────────────────────────────────────────

        public static List<InterruptedTaskData> GetAllInterruptedTasks()
        {
            return s_Tasks?.ToList() ?? new List<InterruptedTaskData>();
        }

        public static bool IsRecovering(string backendTaskId) => s_Recovering.Contains(backendTaskId);

        public static void MarkAsRecovering(string backendTaskId) => s_Recovering.Add(backendTaskId);

        public static void ClearRecovering(string backendTaskId) => s_Recovering.Remove(backendTaskId);

#if TJGENERATORS_DEBUG
        /// <summary>
        /// 清空本地中断/轮询恢复任务列表（InterruptedTasks.json）及 in-memory recovering 标记，
        /// 并递增本地轮询中止代数，使已启动的 PollTaskStatus 在下一轮循环停止继续请求。
        /// 不会向后端发送取消请求。仅 DEBUG 构建可用。
        /// </summary>
        /// <returns>清除前记录的任务数量。</returns>
        public static int ClearAllInterruptedTasks()
        {
            int count = s_Tasks?.Count ?? 0;
            s_Tasks = new List<InterruptedTaskData>();
            s_Recovering.Clear();
            RequestAbortAllLocalPolling();
            Save();
            TJLog.Log($"[TJGeneratorsTaskRecovery] 已清空全部中断/轮询任务记录（共 {count} 条）");
            return count;
        }

        /// <summary>
        /// 请求停止所有本地轮询协程（不碰 InterruptedTasks 列表本身）。仅 DEBUG 构建可用。
        /// </summary>
        public static void RequestAbortAllLocalPolling()
        {
            unchecked { s_localPollAbortEpoch++; }
            TJLog.Log($"[TJGeneratorsTaskRecovery] 已请求中止本地轮询（epoch={s_localPollAbortEpoch}）");
        }

        /// <summary>供 PollTaskStatus 在启动时捕获，之后用 <see cref="WasLocalPollAborted"/> 检测。</summary>
        public static int GetLocalPollAbortEpoch() => s_localPollAbortEpoch;

        /// <summary>若自 <paramref name="capturedEpoch"/> 起已请求过本地中止，返回 true。</summary>
        public static bool WasLocalPollAborted(int capturedEpoch) =>
            capturedEpoch != s_localPollAbortEpoch;
#endif

        /// <summary>
        /// True if the backend task is still tracked for recovery: either persisted in
        /// InterruptedTasks.json, or still marked recovering in-memory (e.g. download in progress
        /// after the persisted record was removed to claim exclusive completion).
        /// </summary>
        public static bool HasActiveRecovery(string backendTaskId)
        {
            if (string.IsNullOrEmpty(backendTaskId)) return false;
            if (IsRecovering(backendTaskId)) return true;
            return s_Tasks != null && s_Tasks.Exists(t => t.backendTaskId == backendTaskId);
        }

        /// <summary>
        /// Whether a CustomTool tracker task should be resumed as recovering after domain reload.
        /// </summary>
        public static bool IsRecoverableTrackerStatus(string status)
        {
            return !string.IsNullOrEmpty(status) && RecoverableTrackerStatuses.Contains(status);
        }

        // ── Window recovery entry point ────────────────────────────────────

        /// <param name="host">宿主窗口，用于 null 检查</param>
        /// <param name="getAssetGuid">获取当前资产 GUID</param>
        /// <param name="setHistory">设置历史记录列表（加载后回调）</param>
        /// <param name="resumeTask">恢复单个任务的回调（查找 generator、设置状态、启动轮询）</param>
        /// <param name="onRepaint">刷新界面回调</param>
        public static void CheckAndRecoverInterruptedTasks(
            EditorWindow host,
            Func<string> getAssetGuid,
            Action<List<TJGeneratorsGenerationHistoryItem>> setHistory,
            Action<InterruptedTaskData> resumeTask,
            Action onRepaint)
        {
            EditorApplication.delayCall += () =>
            {
                if (host == null) return;

                var assetGuid = getAssetGuid();
                var history = TJGeneratorsHistoryManager.LoadHistoryForAsset(assetGuid);
                var generatingItems = history.FindAll(h => h.isGenerating);

                if (generatingItems.Count > 0)
                {
                    // history 中已有 isGenerating 占位符，说明是同 session 内 domain reload 导致的中断，直接恢复无需弹窗
                    var interruptedTasks = GetAllInterruptedTasksForAsset(assetGuid);
                    if (interruptedTasks.Count > 0)
                    {
                        RecoverAfterDomainReload(interruptedTasks, assetGuid, resumeTask, setHistory, onRepaint);
                    }
                    else
                    {
                        var orphansFromPreviousSession = generatingItems.FindAll(
                            h => h.timestamp < SessionStartUnixMs);
                        if (orphansFromPreviousSession.Count > 0)
                            CleanupOrphanedPlaceholders(orphansFromPreviousSession, assetGuid, setHistory, onRepaint);
                        else
                        {
                            setHistory(history);
                            onRepaint?.Invoke();
                        }
                    }
                }
                else
                {
                    // 无占位符说明任务在 history 写入前就中断，或来自上一个 session，需要用户确认
                    RecoverCrossSession(assetGuid, resumeTask);
                }
            };
        }

        // ── Private recovery helpers ───────────────────────────────────────

        private static void RecoverAfterDomainReload(
            List<InterruptedTaskData> tasks,
            string assetGuid,
            Action<InterruptedTaskData> resumeTask,
            Action<List<TJGeneratorsGenerationHistoryItem>> setHistory,
            Action onRepaint)
        {
            TJLog.Log($"[TJGeneratorsTaskRecovery] 检测到 {tasks.Count} 个中断的任务，重新恢复轮询...");
            foreach (var task in tasks)
            {
                if (task.status == "submitting")
                {
                    if (task.sessionId == SessionId)
                    {
                        TJLog.Log($"[TJGeneratorsTaskRecovery] 任务 {task.localTaskId} 仍在当前 session 提交中，跳过清理。");
                        continue;
                    }

                    TJLog.Log($"[TJGeneratorsTaskRecovery] 任务 {task.localTaskId} 在提交阶段被中断（跨 domain reload），清理占位符");
                    RemoveInterruptedTask(task.backendTaskId);
                    if (!string.IsNullOrEmpty(task.localTaskId))
                        TJGeneratorsHistoryManager.RemovePlaceholder(task.localTaskId);
                    continue;
                }

                // MarkAsRecovering 由 StartGeneration 在 pipeline 启动时设置，避免对同一任务启动两条 pipeline
                if (IsRecovering(task.backendTaskId))
                {
                    TJLog.Log($"[TJGeneratorsTaskRecovery] 任务 {task.backendTaskId} 已在轮询中，跳过重复恢复。");
                    continue;
                }

                resumeTask(task);
            }

            setHistory(TJGeneratorsHistoryManager.LoadHistoryForAsset(assetGuid));
            onRepaint?.Invoke();
        }

        private static void CleanupOrphanedPlaceholders(
            List<TJGeneratorsGenerationHistoryItem> generatingItems,
            string assetGuid,
            Action<List<TJGeneratorsGenerationHistoryItem>> setHistory,
            Action onRepaint)
        {
            TJLog.Log($"[TJGeneratorsTaskRecovery] 发现 {generatingItems.Count} 个孤立的生成中占位符（无对应中断任务），清理...");
            foreach (var item in generatingItems)
            {
                TJLog.Log($"[TJGeneratorsTaskRecovery] 清理孤立占位符: taskId={item.taskId}");
                TJGeneratorsHistoryManager.RemovePlaceholder(item.taskId);
            }
            setHistory(TJGeneratorsHistoryManager.LoadHistoryForAsset(assetGuid));
            onRepaint?.Invoke();
        }

        private static void RecoverCrossSession(string assetGuid, Action<InterruptedTaskData> resumeTask)
        {
            var recoverableTasks = GetRecoverableTasksForAsset(assetGuid);
            if (recoverableTasks.Count == 0) return;

            // 不弹窗，自动恢复轮询。若需清理任务请使用历史记录面板。
            Debug.LogWarning(
                $"[TJGeneratorsTaskRecovery] 检测到 {recoverableTasks.Count} 个未完成的生成任务（可能在上次关闭编辑器时中断），自动恢复轮询。"
            );
            foreach (var task in recoverableTasks)
                resumeTask(task);
        }

        private static List<InterruptedTaskData> GetAllInterruptedTasksForAsset(string assetGuid)
        {
            return s_Tasks?
                .Where(t => t.targetAssetGuid == (assetGuid ?? ""))
                .ToList() ?? new List<InterruptedTaskData>();
        }

        private static List<InterruptedTaskData> GetRecoverableTasksForAsset(string assetGuid)
        {
            return s_Tasks?
                .Where(t => !s_Recovering.Contains(t.backendTaskId))
                .Where(t => t.targetAssetGuid == (assetGuid ?? ""))
                .ToList() ?? new List<InterruptedTaskData>();
        }
    }
}
#endif
