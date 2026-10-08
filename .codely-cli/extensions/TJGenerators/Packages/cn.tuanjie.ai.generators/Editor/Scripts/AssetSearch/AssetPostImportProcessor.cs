#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using TJGenerators.Utils;
using UnityEditor;
using UnityEngine;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// ImportPackage 完成后的后处理：刷新 AssetDatabase、MoveAsset 到目标目录、写入 metadata、
    /// 更新 Tracker、打上 generation-session label。
    /// 由两处调用：
    /// <list type="number">
    ///   <item>script-free 路径，在 <see cref="AssetDownloadPipeline.ProcessImportQueue"/> 延迟一帧后同步调用；</item>
    ///   <item>含 .cs 路径，由 <see cref="AssetDomainReloadHook"/> 在 domain reload 完成后调用。</item>
    /// </list>
    /// </summary>
    public static class AssetPostImportProcessor
    {
        private const string LogTag = "[AssetPostImportProcessor]";

        /// <summary>
        /// 读取 PendingPostImport 中全部条目，逐条执行后处理。处理完成后全部从队列中移除。
        /// 处理过程中出现异常的条目会被标记为 Failed；不阻塞后续条目。
        /// </summary>
        public static void ProcessAll()
        {
            var pending = AssetImportQueue.GetPending();
            if (pending.Count == 0) return;

            foreach (var entry in pending)
            {
                try { ProcessOne(entry); }
                catch (Exception ex)
                {
                    TJLog.LogError($"{LogTag} PostImport processing failed for task {entry?.TaskId}: {ex}");
                    if (!string.IsNullOrEmpty(entry?.TaskId))
                        AssetDownloadTracker.MarkFailed(entry.TaskId, $"Post-import error: {ex.Message}");
                }
            }

            // 所有条目均已处理完毕（成功 / 失败 / 跳过），清空 PendingPostImport
            AssetImportQueue.SavePending(new List<AssetImportQueueEntry>());
        }

        private static void ProcessOne(AssetImportQueueEntry entry)
        {
            if (entry == null) return;

            string taskId     = entry.TaskId;
            string tempPath   = entry.TempPath;
            string origPrefab = entry.PrefabPath;
            string packageDir = entry.PackageDir;
            string sessionId  = entry.SessionId ?? "";

            // 拷贝一份、便于后续就地修改
            var importedFiles = new List<string>(entry.ImportedFiles ?? new List<string>());

            PathUtils.SafeDelete(tempPath);

            if (string.IsNullOrEmpty(origPrefab) || string.IsNullOrEmpty(packageDir))
            {
                AssetDownloadTracker.MarkFailed(taskId, "PendingPostImport: missing prefabPath or packageDir");
                return;
            }

            // 解析 prefab 实际路径：若 origPrefab 与包内 pathname 不完全一致，
            // 在 importedFiles 中按大小写/文件名 fallback；全失败才 MarkFailed。
            var (resolvedPrefab, resolveDetail) = ResolveActualPrefab(origPrefab, importedFiles);
            if (resolvedPrefab == null)
            {
                AssetDownloadTracker.MarkFailed(taskId, resolveDetail);
                return;
            }
            if (!string.Equals(resolvedPrefab, origPrefab, StringComparison.Ordinal))
            {
                TJLog.LogWarning(
                    $"{LogTag} prefab_path resolved via fallback ({resolveDetail}): orig={origPrefab}, resolved={resolvedPrefab}");
            }

            // 确保目标目录存在（Unity AssetDatabase 视角）
            PathUtils.EnsureAssetFolder(packageDir);

            string prefabFileName = Path.GetFileName(resolvedPrefab);
            string prefabDest     = packageDir + "/" + prefabFileName;

            // If the package already stored files at their final location (pathname entries under
            // packageDir), the prefab is already at prefabDest after ImportPackage — MoveAsset
            // source == destination would fail. Skip the move in that case.
            bool alreadyAtDest = string.Equals(
                resolvedPrefab.Replace('\\', '/'),
                prefabDest.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase);

            if (!alreadyAtDest)
            {
                string moveError = AssetDatabase.MoveAsset(resolvedPrefab, prefabDest);
                if (!string.IsNullOrEmpty(moveError))
                {
                    // Secondary fallback: MoveAsset can fail if the destination was already written
                    // by a concurrent import or a retry. If the file is there, treat as success.
                    if (!ExistsInAssetDatabase(prefabDest))
                    {
                        AssetDownloadTracker.MarkFailed(taskId, $"MoveAsset failed: {moveError}");
                        return;
                    }
                    TJLog.LogWarning(
                        $"{LogTag} MoveAsset failed ({moveError}) but prefab already exists at destination, treating as success: {prefabDest}");
                }
            }

            // imported_files: prefab 条目替换为 MoveAsset 后的路径，置于首位
            importedFiles.Remove(resolvedPrefab);
            importedFiles.Insert(0, prefabDest);
            AssetDownloadTracker.SetPrefabPath(taskId, prefabDest);

            // 写入 metadata JSON — 失败只记日志，不影响 task 状态（prefab 已成功移动）
            var task = AssetDownloadTracker.GetTask(taskId);
            if (task != null)
            {
                try
                {
                    AssetMetadataWriter.Write(task, prefabDest, importedFiles);
                }
                catch (Exception ex)
                {
                    TJLog.LogWarning(
                        $"{LogTag} Metadata write failed for task {taskId} (prefab move already succeeded): {ex.Message}");
                }
            }

            AssetDownloadTracker.MarkCompleted(taskId, importedFiles);

            if (!string.IsNullOrEmpty(sessionId))
            {
                try
                {
                    TJGeneratorsGenerationLabel.EnableSessionLabel(
                        TJGeneratorsAssetReference.FromPath(prefabDest), sessionId);
                }
                catch (Exception ex)
                {
                    TJLog.LogWarning($"{LogTag} EnableSessionLabel failed: {ex.Message}");
                }
            }

            if (entry.InstantiateInScene)
                InstantiatePrefabInScene(prefabDest);

            TJLog.Log($"{LogTag} Import completed: task_id={taskId}, prefab={prefabDest}, files={importedFiles.Count}");
        }

        /// <summary>
        /// 将指定 Prefab 实例化到当前活动场景，自动注册 Undo 并选中实例。
        /// 位置优先对齐到 SceneView 当前视角焦点；SceneView 不可用时落在世界原点。
        /// </summary>
        public static void InstantiatePrefabInScene(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath))
            {
                TJLog.LogWarning($"{LogTag} InstantiateInScene: prefabPath is empty");
                return;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                TJLog.LogWarning($"{LogTag} InstantiateInScene: prefab not found at {prefabPath}");
                return;
            }

            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null) return;

            // 对齐到 SceneView 焦点中心，方便用户直接看到
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
                instance.transform.position = sceneView.pivot;

            Undo.RegisterCreatedObjectUndo(instance, $"Place {instance.name} in Scene");
            Selection.activeObject = instance;

            TJLog.Log($"{LogTag} Instantiated '{instance.name}' in scene at {instance.transform.position}");
        }

        /// <summary>
        /// 根据包内 pathname 列表解析搜索结果中的 prefab 路径。供导入完成后与「同一 URL 已导入」分支复用。
        /// </summary>
        /// <param name="attemptDiskRepairImport">
        /// 为 true 时，在未注册到 AssetDatabase 的磁盘 .prefab 上尝试 ForceImport（仅导入流程使用）。
        /// </param>
        public static bool TryResolvePrefabFromImportList(
            string origPrefab,
            IList<string> importedFiles,
            out string resolvedAssetPath,
            bool attemptDiskRepairImport)
        {
            resolvedAssetPath = null;
            if (ExistsInAssetDatabase(origPrefab))
            {
                resolvedAssetPath = origPrefab;
                return true;
            }

            if (importedFiles == null || importedFiles.Count == 0)
                return false;

            string origFileName   = Path.GetFileName(origPrefab);
            string origNormalized = (origPrefab ?? "").Replace('\\', '/');

            foreach (var candidate in importedFiles)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (!candidate.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(candidate.Replace('\\', '/'), origNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    if (ExistsInAssetDatabase(candidate))
                    {
                        resolvedAssetPath = candidate;
                        return true;
                    }
                }
            }

            foreach (var candidate in importedFiles)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (!candidate.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(origFileName) &&
                    string.Equals(Path.GetFileName(candidate), origFileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (ExistsInAssetDatabase(candidate))
                    {
                        resolvedAssetPath = candidate;
                        return true;
                    }
                }
            }

            foreach (var candidate in importedFiles)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (!candidate.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (ExistsInAssetDatabase(candidate))
                {
                    resolvedAssetPath = candidate;
                    return true;
                }
            }

            if (!attemptDiskRepairImport)
                return false;

            foreach (var candidate in importedFiles)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (!candidate.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                string absPath = PathUtils.ToAbsoluteAssetPath(candidate);
                if (string.IsNullOrEmpty(absPath) || !File.Exists(absPath)) continue;
                AssetDatabase.ImportAsset(candidate,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                if (ExistsInAssetDatabase(candidate))
                {
                    resolvedAssetPath = candidate;
                    return true;
                }
            }

            return false;
        }

        // 在 ImportPackage 完成后定位 prefab 实际所在路径。
        // 先试 origPrefab（搜索结果给的路径）；不命中时按大小写忽略全路径、同名文件、首个 .prefab 逐级 fallback。
        // 返回 (resolvedPath, reason)；resolvedPath 为 null 表示所有 fallback 均失败。
        //
        // 关键点：不再对路径直接调用 AssetDatabase.ImportAsset(ForceUpdate) —— 在路径不存在时
        // Unity 会打 "'...' does not exist" 错误日志。改为开头做一次 SafeRefresh(ForceSynchronousImport)
        // 让 AssetDatabase 同步到 ImportPackage 刚写入的内容，再单用 AssetPathToGUID 判断 Unity 是否识别。
        private static (string resolved, string detail) ResolveActualPrefab(
            string origPrefab, List<string> importedFiles)
        {
            PathUtils.SafeRefresh(ImportAssetOptions.ForceSynchronousImport);

            if (TryResolvePrefabFromImportList(origPrefab, importedFiles, out var hit, attemptDiskRepairImport: true))
                return (hit, "resolved");

            int n = importedFiles?.Count ?? 0;
            return (null, $"Prefab not found at expected path after import: {origPrefab} (no .prefab resolved from {n} imported files)");
        }

        // Unity AssetDatabase 是否识别该路径为已导入资源。不调用 ImportAsset，避免路径不存在时产生
        // "'...' does not exist" 噪音日志（ImportAsset 对不存在路径会主动 LogError）。
        private static bool ExistsInAssetDatabase(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            return !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath));
        }
    }
}
#endif
