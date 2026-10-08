#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using TJGenerators.Utils;
using UnityEditor;
using UnityEngine;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// 下载任务状态存储：内存字典 + SessionState 持久化，支持 domain reload 后恢复。
    /// 与原 <c>UnityTcp.Editor.Tools.DownloadTaskTracker</c> 行为一致；SessionState 键名与字符串值完全保留。
    /// Status / Phase 的外部 API 改为枚举，对内序列化时与旧字符串一一对应。
    /// </summary>
    public static class AssetDownloadTracker
    {
        private static readonly Dictionary<string, DownloadTaskInfo> _activeTasks =
            new Dictionary<string, DownloadTaskInfo>();
        private static int _taskIdCounter = 0;

        private const string SessionKeyIds = "TJGen_Download_Ids";
        private const string SessionKeyFmt = "TJGen_Download_{0}";

        // 仅跟踪当前进程内活跃的下载协程。domain reload 后静态字段重置为 0，
        // 此时所有先前的下载协程已被 Unity 杀死，不再计入 in-flight。
        private static int _inFlight = 0;

        // ---------- 事件派发（合并到 EditorApplication.delayCall 下一拍） ----------

        private static readonly HashSet<string> _dirtyTaskIds = new HashSet<string>();
        private static bool _dispatchScheduled;

        [Serializable]
        private class PersistedTask
        {
            public string taskId;
            public string assetId;
            public string prefabPath;
            public string assetName;
            public string packageDirPath;
            public string metadataPath;
            public string status;
            public string phase;             // 保留字段用于读取旧版 SessionState，写入时留空
            public string category;
            public string source;
            public string description;
            public string prefabMetaJson;   // serialized JObject → string
            public string query;
            public float  score;
            public string keywords;
            public string previewUrl;
            public string downloadUrl;
            public string importedFilesJson; // JSON array of strings
            public string errorMessage;
            public long   startTimeTicks;
            public long   endTimeTicks;
        }

        [Serializable]
        private class StringList { public List<string> items = new List<string>(); }

        internal static void SaveToSession(DownloadTaskInfo info)
        {
            var importedList = new StringList { items = info.ImportedFiles ?? new List<string>() };
            var p = new PersistedTask
            {
                taskId             = info.TaskId,
                assetId            = info.AssetId         ?? "",
                prefabPath         = info.PrefabPath      ?? "",
                assetName          = info.AssetName       ?? "",
                packageDirPath     = info.PackageDirPath  ?? "",
                metadataPath       = info.MetadataPath    ?? "",
                status             = DownloadTaskStatusMap.ToSerialized(info.Status),
                phase              = "",
                category           = info.Category        ?? "",
                source             = info.Source          ?? "",
                description        = info.Description     ?? "",
                prefabMetaJson     = info.PrefabMeta      ?? "",
                query              = info.Query           ?? "",
                score              = info.Score,
                keywords           = info.Keywords        ?? "",
                previewUrl         = info.PreviewUrl      ?? "",
                downloadUrl        = info.DownloadUrl     ?? "",
                importedFilesJson  = JsonUtility.ToJson(importedList),
                errorMessage       = info.ErrorMessage    ?? "",
                startTimeTicks     = info.StartTime.Ticks,
                endTimeTicks       = info.EndTime?.Ticks ?? 0,
            };

            SessionState.SetString(string.Format(SessionKeyFmt, info.TaskId), JsonUtility.ToJson(p));
            string ids = SessionState.GetString(SessionKeyIds, "");
            bool alreadyListed = false;
            if (!string.IsNullOrEmpty(ids))
            {
                foreach (var existing in ids.Split('|'))
                {
                    if (existing == info.TaskId) { alreadyListed = true; break; }
                }
            }
            if (!alreadyListed)
            {
                SessionState.SetString(SessionKeyIds,
                    string.IsNullOrEmpty(ids) ? info.TaskId : ids + "|" + info.TaskId);
            }

            ScheduleDispatch(info.TaskId);
            TryPruneTerminatedTasks();
        }

        // 终态任务清理：保留最近 TerminatedRetainLimit 条；总量超过 TerminatedPruneTrigger 才实际跑，分摊成本。
        // 非终态（Downloading/Importing）任务永远保留。
        private const int TerminatedRetainLimit  = 50;
        private const int TerminatedPruneTrigger = 100;

        private static void TryPruneTerminatedTasks()
        {
            string ids = SessionState.GetString(SessionKeyIds, "");
            if (string.IsNullOrEmpty(ids)) return;

            var idList = ids.Split('|');
            if (idList.Length < TerminatedPruneTrigger) return;

            foreach (var id in idList)
                if (!string.IsNullOrEmpty(id) && !_activeTasks.ContainsKey(id))
                    TryRestoreFromSession(id);

            var terminatedStatuses = new HashSet<DownloadTaskStatus>
            {
                DownloadTaskStatus.Completed,
                DownloadTaskStatus.Failed,
                DownloadTaskStatus.Interrupted,
                DownloadTaskStatus.Skipped,
            };

            var terminated = new List<DownloadTaskInfo>();
            foreach (var kv in _activeTasks)
                if (terminatedStatuses.Contains(kv.Value.Status))
                    terminated.Add(kv.Value);

            if (terminated.Count <= TerminatedRetainLimit) return;

            terminated.Sort((a, b) =>
            {
                var ea = a.EndTime ?? a.StartTime;
                var eb = b.EndTime ?? b.StartTime;
                return ea.CompareTo(eb);
            });

            int removeCount = terminated.Count - TerminatedRetainLimit;
            for (int i = 0; i < removeCount; i++)
                RemoveTask(terminated[i].TaskId);

            TJLog.Log($"[AssetDownloadTracker] Pruned {removeCount} terminated tasks, retained {TerminatedRetainLimit}.");
        }

        private static void ScheduleDispatch(string taskId)
        {
            _dirtyTaskIds.Add(taskId);
            if (_dispatchScheduled) return;
            _dispatchScheduled = true;
            EditorApplication.delayCall += DispatchUpdates;
        }

        private static void DispatchUpdates()
        {
            _dispatchScheduled = false;
            if (_dirtyTaskIds.Count == 0) return;
            var ids = new List<string>(_dirtyTaskIds);
            _dirtyTaskIds.Clear();
            foreach (var id in ids)
            {
                if (_activeTasks.TryGetValue(id, out var t))
                    AssetDownloadService.RaiseTaskUpdated(t);
            }
        }

        private static DownloadTaskInfo TryRestoreFromSession(string taskId)
        {
            string json = SessionState.GetString(string.Format(SessionKeyFmt, taskId), "");
            if (string.IsNullOrEmpty(json)) return null;

            PersistedTask p;
            try { p = JsonUtility.FromJson<PersistedTask>(json); }
            catch { return null; }

            var imported = new List<string>();
            try
            {
                var dl = JsonUtility.FromJson<StringList>(p.importedFilesJson);
                if (dl?.items != null) imported = dl.items;
            }
            catch { /* ignore */ }

            var status = DownloadTaskStatusMap.FromSerialized(p.status);
            // 旧版 SessionState 中 status="downloading" 可能对应两种子状态（由 phase 区分），
            // 向后兼容：phase="importing" → 恢复为新的 Importing 状态
            if (status == DownloadTaskStatus.Downloading && p.phase == "importing")
                status = DownloadTaskStatus.Importing;

            var info = new DownloadTaskInfo
            {
                TaskId         = p.taskId,
                AssetId        = p.assetId,
                PrefabPath     = p.prefabPath,
                AssetName      = p.assetName,
                PackageDirPath = p.packageDirPath,
                MetadataPath   = p.metadataPath,
                Status         = status,
                Category       = p.category,
                Source         = p.source,
                Description    = p.description,
                PrefabMeta     = p.prefabMetaJson,
                Query          = p.query,
                Score          = p.score,
                Keywords       = p.keywords,
                PreviewUrl     = p.previewUrl,
                DownloadUrl    = p.downloadUrl,
                ImportedFiles  = imported,
                ErrorMessage   = p.errorMessage,
                StartTime      = p.startTimeTicks > 0 ? new DateTime(p.startTimeTicks) : DateTime.Now,
                EndTime        = p.endTimeTicks > 0 ? (DateTime?)new DateTime(p.endTimeTicks) : null,
            };

            // 中断恢复：
            // - Downloading：协程已被 domain reload 杀死，标记为 Interrupted
            // - Importing：由 PendingPostImportQueue + [InitializeOnLoadMethod] 恢复，保留原状态让 ProcessAll 处理
            if (info.Status == DownloadTaskStatus.Downloading)
            {
                info.Status       = DownloadTaskStatus.Interrupted;
                info.ErrorMessage = "Download was interrupted (domain reload). Please re-download.";
                info.EndTime      = DateTime.Now;
                _activeTasks[taskId] = info;
                SaveToSession(info);
                return info;
            }

            _activeTasks[taskId] = info;
            return info;
        }

        public static string CreateTask(
            string assetId,
            string assetName,
            string packageDirPath,
            string prefabPath,
            string metadataPath,
            string category,
            string source,
            string description,
            string prefabMeta,
            string query,
            float  score,
            string keywords,
            string previewUrl = "",
            string downloadUrl = "")
        {
            string taskId = $"download_{++_taskIdCounter}_{DateTime.Now.Ticks}";
            var task = new DownloadTaskInfo
            {
                TaskId         = taskId,
                AssetId        = assetId,
                AssetName      = assetName,
                PackageDirPath = packageDirPath,
                PrefabPath     = prefabPath,      // 初始为搜索结果原始路径；MoveAsset 后由 SetPrefabPath 更新
                MetadataPath   = metadataPath,
                Category       = category,
                Source         = source,
                Description    = description,
                PrefabMeta     = prefabMeta,
                Query          = query,
                Score          = score,
                Keywords       = keywords,
                PreviewUrl     = previewUrl,
                DownloadUrl    = downloadUrl ?? "",
                Status         = DownloadTaskStatus.Downloading,
                StartTime      = DateTime.Now,
            };
            _activeTasks[taskId] = task;
            SaveToSession(task);
            return taskId;
        }

        public static void SetStatus(string taskId, DownloadTaskStatus status)
        {
            if (!_activeTasks.TryGetValue(taskId, out var task)) return;
            task.Status = status;
            SaveToSession(task);
        }

        public static void SetPrefabPath(string taskId, string newPrefabPath)
        {
            if (!_activeTasks.TryGetValue(taskId, out var task)) return;
            task.PrefabPath = newPrefabPath;
            SaveToSession(task);
        }

        public static void MarkCompleted(string taskId, List<string> importedFiles)
        {
            if (!_activeTasks.TryGetValue(taskId, out var task)) return;
            task.Status        = DownloadTaskStatus.Completed;
            task.ImportedFiles = importedFiles ?? new List<string>();
            task.EndTime       = DateTime.Now;
            SaveToSession(task);
        }

        public static void MarkFailed(string taskId, string errorMessage)
        {
            if (!_activeTasks.TryGetValue(taskId, out var task)) return;
            task.Status       = DownloadTaskStatus.Failed;
            task.ErrorMessage = errorMessage;
            task.EndTime      = DateTime.Now;
            SaveToSession(task);
        }

        /// <summary>
        /// 标记任务为 skipped。必须由调用方传入真实存在的 prefab 路径（事先验证过文件存在），
        /// 避免 Tracker 凭 PackageDir + 文件名拼出"假"路径导致 AI 调用 unity_gameobject 失败。
        /// 为兼容旧签名，actualPrefabPath 为空时保留 CreateTask 时写入的原始 PrefabPath 作为兜底。
        /// </summary>
        public static void MarkSkipped(string taskId, string actualPrefabPath)
        {
            if (!_activeTasks.TryGetValue(taskId, out var task)) return;
            if (!string.IsNullOrEmpty(actualPrefabPath))
                task.PrefabPath = actualPrefabPath;
            task.Status  = DownloadTaskStatus.Skipped;
            task.EndTime = DateTime.Now;
            SaveToSession(task);
        }

        // ---------- in-flight 计数（仅当前进程） ----------

        public static void IncrementInFlight() { _inFlight++; }

        public static void DecrementInFlight() { if (_inFlight > 0) _inFlight--; }

        public static int GetInFlight() => _inFlight;

        // ---------- 查询 ----------

        public static DownloadTaskInfo GetTask(string taskId)
        {
            if (_activeTasks.TryGetValue(taskId, out var task)) return task;
            return TryRestoreFromSession(taskId);
        }

        public static List<DownloadTaskInfo> GetAllTasks()
        {
            string ids = SessionState.GetString(SessionKeyIds, "");
            if (!string.IsNullOrEmpty(ids))
            {
                foreach (var id in ids.Split('|'))
                    if (!string.IsNullOrEmpty(id) && !_activeTasks.ContainsKey(id))
                        TryRestoreFromSession(id);
            }
            return new List<DownloadTaskInfo>(_activeTasks.Values);
        }

        public static void RemoveTask(string taskId)
        {
            _activeTasks.Remove(taskId);
            SessionState.EraseString(string.Format(SessionKeyFmt, taskId));
            string ids  = SessionState.GetString(SessionKeyIds, "");
            var list    = new List<string>(ids.Split('|'));
            list.Remove(taskId);
            SessionState.SetString(SessionKeyIds, string.Join("|", list));
        }
    }
}
#endif
