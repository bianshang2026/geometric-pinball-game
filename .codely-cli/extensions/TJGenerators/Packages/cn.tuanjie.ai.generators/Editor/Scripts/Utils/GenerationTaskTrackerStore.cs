#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TJGenerators.Utils
{
    public sealed class GenerationTaskTrackerStore<TInfo, TPersisted>
        where TInfo : class, IGenerationTaskInfo
        where TPersisted : class
    {
        private readonly string _sessionKeyIds;
        private readonly string _sessionKeyFmt;
        private readonly Func<TInfo, TPersisted> _toPersisted;
        private readonly Func<TPersisted, TInfo> _fromPersisted;
        private readonly Func<TInfo, string> _getBackendTaskId;
        private readonly Func<TInfo, string, bool> _matchesBackendTaskId;
        private readonly Func<TInfo, bool> _hasActiveRecovery;
        private readonly Action<TInfo, Action> _reconcileAfterRestore;

        private readonly Dictionary<string, TInfo> _activeTasks = new Dictionary<string, TInfo>();
        private int _taskIdCounter;

        public GenerationTaskTrackerStore(
            string sessionKeyPrefix,
            Func<TInfo, TPersisted> toPersisted,
            Func<TPersisted, TInfo> fromPersisted,
            Func<TInfo, string> getBackendTaskId = null,
            Func<TInfo, string, bool> matchesBackendTaskId = null,
            Func<TInfo, bool> hasActiveRecovery = null,
            Action<TInfo, Action> reconcileAfterRestore = null)
        {
            if (string.IsNullOrEmpty(sessionKeyPrefix))
                throw new ArgumentException("sessionKeyPrefix is required", nameof(sessionKeyPrefix));
            _sessionKeyIds = sessionKeyPrefix + "_Ids";
            _sessionKeyFmt = sessionKeyPrefix + "_{0}";
            _toPersisted = toPersisted ?? throw new ArgumentNullException(nameof(toPersisted));
            _fromPersisted = fromPersisted ?? throw new ArgumentNullException(nameof(fromPersisted));
            _getBackendTaskId = getBackendTaskId ?? (t => t.BackendTaskId);
            _matchesBackendTaskId = matchesBackendTaskId
                ?? ((t, backendId) =>
                    !string.IsNullOrEmpty(backendId)
                    && string.Equals(_getBackendTaskId(t), backendId, StringComparison.Ordinal));
            _hasActiveRecovery = hasActiveRecovery
                ?? (t => TJGeneratorsTaskRecovery.HasActiveRecovery(_getBackendTaskId(t)));
            _reconcileAfterRestore = reconcileAfterRestore;
        }

        internal Dictionary<string, TInfo> ActiveTasksForTests => _activeTasks;

        public string AllocateTaskId(string prefix)
        {
            return $"{prefix}_{++_taskIdCounter}_{DateTime.Now.Ticks}";
        }

        public void RegisterTask(string taskId, TInfo info)
        {
            if (info == null || string.IsNullOrEmpty(taskId)) return;

            info.TaskId = taskId;
            _activeTasks[taskId] = info;
            WriteSessionState(_toPersisted(info), taskId);
        }

        public void SaveToSession(TInfo info)
        {
            if (info == null) return;
            WriteSessionState(_toPersisted(info), info.TaskId);
        }

        public void ApplyTaskUpdate(TInfo task, Action<TInfo> mutate)
        {
            if (task == null || mutate == null) return;

            mutate(task);
            WriteSessionState(_toPersisted(task), task.TaskId);
        }

        public TInfo GetTask(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return null;
            if (_activeTasks.TryGetValue(taskId, out var task)) return task;
            return TryRestoreFromSession(taskId);
        }

        public List<TInfo> GetAllTasks()
        {
            RestoreAllSessionTasks();
            return new List<TInfo>(_activeTasks.Values);
        }

        public TInfo GetTaskByBackendId(string backendTaskId)
        {
            if (string.IsNullOrEmpty(backendTaskId)) return null;

            var cached = _activeTasks.Values.FirstOrDefault(t => _matchesBackendTaskId(t, backendTaskId));
            if (cached != null) return cached;

            RestoreAllSessionTasks();
            return _activeTasks.Values.FirstOrDefault(t => _matchesBackendTaskId(t, backendTaskId));
        }

        public TInfo Find(Predicate<TInfo> predicate)
        {
            if (predicate == null) return null;
            RestoreAllSessionTasks();
            return _activeTasks.Values.FirstOrDefault(t => predicate(t));
        }

        public TInfo CreateRecoveredTask(string backendTaskId, Func<TInfo> factory)
        {
            if (string.IsNullOrEmpty(backendTaskId) || factory == null) return null;

            var existing = _activeTasks.Values.FirstOrDefault(t => _matchesBackendTaskId(t, backendTaskId));
            if (existing != null) return existing;

            RestoreAllSessionTasks();
            existing = _activeTasks.Values.FirstOrDefault(t => _matchesBackendTaskId(t, backendTaskId));
            if (existing != null) return existing;

            var info = factory();
            if (info == null) return null;

            if (string.IsNullOrEmpty(info.TaskId))
                info.TaskId = $"recovered_{backendTaskId}";
            info.BackendTaskId = string.IsNullOrEmpty(info.BackendTaskId) ? backendTaskId : info.BackendTaskId;
            if (string.IsNullOrEmpty(info.Status))
                info.Status = "recovering";

            _activeTasks[info.TaskId] = info;
            WriteSessionState(_toPersisted(info), info.TaskId);
            return info;
        }

        public void RemoveTask(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return;
            _activeTasks.Remove(taskId);
            EraseSessionTask(taskId);
        }

        public bool RemoveActiveTaskFromMemoryOnly(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return false;
            return _activeTasks.Remove(taskId);
        }

        public void CleanupCompletedTasks(double retainMinutes = 60)
        {
            RestoreAllSessionTasks();

            var toRemove = new List<string>();
            foreach (var kvp in _activeTasks)
            {
                string status = kvp.Value.Status;
                bool terminal = status == "completed" || status == "failed" || status == "interrupted";
                if (terminal &&
                    kvp.Value.EndTime.HasValue &&
                    (DateTime.Now - kvp.Value.EndTime.Value).TotalMinutes > retainMinutes)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (string taskId in toRemove)
                RemoveTask(taskId);
        }

        private List<string> LoadSessionTaskIds()
        {
            string ids = SessionState.GetString(_sessionKeyIds, "");
            if (string.IsNullOrEmpty(ids)) return new List<string>();

            var list = new List<string>();
            foreach (var id in ids.Split('|'))
            {
                if (!string.IsNullOrEmpty(id) && !list.Contains(id))
                    list.Add(id);
            }
            return list;
        }

        private void SaveSessionTaskIds(List<string> ids)
        {
            SessionState.SetString(_sessionKeyIds, ids == null || ids.Count == 0 ? "" : string.Join("|", ids));
        }

        private void AddSessionTaskId(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return;
            var list = LoadSessionTaskIds();
            if (list.Contains(taskId)) return;
            list.Add(taskId);
            SaveSessionTaskIds(list);
        }

        private void RemoveSessionTaskId(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return;
            var list = LoadSessionTaskIds();
            if (!list.Remove(taskId)) return;
            SaveSessionTaskIds(list);
        }

        private void WriteSessionState(TPersisted persisted, string taskId)
        {
            SessionState.SetString(string.Format(_sessionKeyFmt, taskId), JsonUtility.ToJson(persisted));
            AddSessionTaskId(taskId);
        }

        private void EraseSessionTask(string taskId)
        {
            SessionState.EraseString(string.Format(_sessionKeyFmt, taskId));
            RemoveSessionTaskId(taskId);
        }

        private void RestoreAllSessionTasks()
        {
            foreach (var id in LoadSessionTaskIds())
            {
                if (!_activeTasks.ContainsKey(id))
                    TryRestoreFromSession(id);
            }
        }

        private TInfo TryRestoreFromSession(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return null;

            string json = SessionState.GetString(string.Format(_sessionKeyFmt, taskId), "");
            if (string.IsNullOrEmpty(json))
            {
                EraseSessionTask(taskId);
                return null;
            }

            TPersisted p;
            try { p = JsonUtility.FromJson<TPersisted>(json); }
            catch
            {
                EraseSessionTask(taskId);
                return null;
            }
            if (p == null)
            {
                EraseSessionTask(taskId);
                return null;
            }

            var info = _fromPersisted(p);
            if (!TryNormalizeAndValidateRestored(taskId, info))
            {
                EraseSessionTask(taskId);
                return null;
            }

            if (_reconcileAfterRestore != null)
                _reconcileAfterRestore(info, () => WriteSessionState(_toPersisted(info), info.TaskId));
            else
            {
                GenerationTaskTrackerStatus.ReconcileAfterDomainReload(
                    info,
                    () => WriteSessionState(_toPersisted(info), info.TaskId),
                    t => _hasActiveRecovery((TInfo)t));
            }

            _activeTasks[taskId] = info;
            return info;
        }

        private static bool TryNormalizeAndValidateRestored(string sessionTaskId, TInfo info)
        {
            if (info == null) return false;

            if (string.IsNullOrEmpty(info.TaskId))
                info.TaskId = sessionTaskId;

            if (string.IsNullOrEmpty(info.TaskId))
                return false;

            if (!string.Equals(info.TaskId, sessionTaskId, StringComparison.Ordinal))
                info.TaskId = sessionTaskId;

            return !string.IsNullOrEmpty(info.Status);
        }
    }
}
#endif
