#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Codely.Newtonsoft.Json.Linq;
using TJGenerators.Utils;
using UnityEditor;
using UnityEngine;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// 资产下载的对外 facade。CustomTool 与未来 UI 共享此入口。
    /// <see cref="StartDownload"/> 同步返回 <see cref="StartDownloadResult"/>；<see cref="TaskUpdated"/>
    /// 在每次任务状态变更（合并到 <c>EditorApplication.delayCall</c> 下一拍）时派发。
    /// </summary>
    public static class AssetDownloadService
    {
        /// <summary>默认资产落地目录；与原 CustomTool 协议保持一致。</summary>
        public const string DefaultDestBase = "Assets/TJGenerators/DownloadedAssets";

        /// <summary>
        /// 任意下载任务状态变更时触发（合并到 EditorApplication.delayCall 下一拍派发）。
        /// UI 可订阅以实时刷新进度条；CustomTool 继续走轮询，不订阅此事件。
        /// </summary>
        public static event Action<DownloadTaskInfo> TaskUpdated;

        /// <summary>
        /// Tracker 在状态变更派发时调用。对外不可见（internal）——CustomTool/UI 只应订阅 <see cref="TaskUpdated"/>。
        /// </summary>
        internal static void RaiseTaskUpdated(DownloadTaskInfo info)
        {
            var handler = TaskUpdated;
            if (handler == null || info == null) return;
            try { handler.Invoke(info); }
            catch (Exception ex)
            {
                TJLog.LogWarning($"[AssetDownloadService] TaskUpdated handler threw: {ex.Message}");
            }
        }

        /// <summary>
        /// 查询任务当前状态；找不到返回 null。
        /// </summary>
        public static DownloadTaskInfo GetTask(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return null;
            return AssetDownloadTracker.GetTask(taskId);
        }

        /// <summary>
        /// 枚举所有活跃 + SessionState 中的任务（UI 显示历史用）。
        /// </summary>
        public static IReadOnlyList<DownloadTaskInfo> GetAllTasks()
        {
            return AssetDownloadTracker.GetAllTasks();
        }

        /// <summary>
        /// 判定该搜索条目对应的资源是否已在工程中（当前 asset 目录已导入，或同一 unitypackage URL 已导入过）。
        /// 成功时 <paramref name="resolvedPrefabPath"/> 为可用于 <see cref="AssetPostImportProcessor.InstantiatePrefabInScene"/> 的路径。
        /// </summary>
        public static bool TryGetImportedPrefabPath(
            string downloadUrl,
            string assetId,
            string searchPrefabPath,
            out string resolvedPrefabPath)
        {
            resolvedPrefabPath = null;
            if (string.IsNullOrWhiteSpace(searchPrefabPath)) return false;

            // 优先按包 URL 命中 metadata（含完整 imported_files），可正确解析同包内不同动作 Prefab。
            if (!string.IsNullOrWhiteSpace(downloadUrl)
                && TryResolvePrefabFromDiskMetadata(downloadUrl, searchPrefabPath, out resolvedPrefabPath)
                && !string.IsNullOrEmpty(resolvedPrefabPath))
                return true;

            string packageDir    = DefaultDestBase + "/" + assetId;
            string absPackageDir = PathUtils.ToAbsoluteAssetPath(packageDir);
            string byFolder      = TryFindExistingPrefab(absPackageDir, packageDir, searchPrefabPath);
            if (byFolder != null)
            {
                resolvedPrefabPath = byFolder;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 扫描已下载资产目录下的 metadata，查找与 <paramref name="downloadUrl"/> 完全一致的
        /// <c>download_url</c>（新写入字段）；命中后用 <c>imported_files</c> 解析本条目的 prefab。
        /// 注：签名 URL 在重新搜索后可能变化，此时无法与历史 metadata 匹配，属预期限制。
        /// </summary>
        static bool TryResolvePrefabFromDiskMetadata(string downloadUrl, string searchPrefabPath, out string resolved)
        {
            resolved = null;
            if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(searchPrefabPath)) return false;

            string absRoot = PathUtils.ToAbsoluteAssetPath(DefaultDestBase);
            if (string.IsNullOrEmpty(absRoot) || !Directory.Exists(absRoot)) return false;

            string[] metaFiles;
            try { metaFiles = Directory.GetFiles(absRoot, "*_metadata.json", SearchOption.AllDirectories); }
            catch { return false; }

            foreach (var absMeta in metaFiles)
            {
                JObject jo;
                try
                {
                    jo = JObject.Parse(File.ReadAllText(absMeta));
                }
                catch { continue; }

                string stored = jo["download_url"]?.ToString();
                if (string.IsNullOrEmpty(stored) || !string.Equals(stored, downloadUrl, StringComparison.Ordinal))
                    continue;

                var imported = new List<string>();
                var arr = jo["imported_files"] as JArray;
                if (arr != null)
                {
                    foreach (var t in arr)
                    {
                        var s = t?.ToString();
                        if (!string.IsNullOrEmpty(s)) imported.Add(s.Replace('\\', '/'));
                    }
                }

                if (imported.Count == 0) continue;

                if (AssetPostImportProcessor.TryResolvePrefabFromImportList(
                        searchPrefabPath, imported, out var hit, attemptDiskRepairImport: false)
                    && !string.IsNullOrEmpty(hit))
                {
                    resolved = hit;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 启动一次资产下载。目标目录若已存在则直接返回 <see cref="DownloadTaskStatus.Skipped"/>，
        /// 不启动协程；否则同步创建 Tracker 任务 + 异步协程下载并返回 <see cref="DownloadTaskStatus.Downloading"/>。
        /// 必填字段：<see cref="AssetDownloadRequest.AssetId"/>、<see cref="AssetDownloadRequest.Name"/>、
        /// <see cref="AssetDownloadRequest.Url"/>、<see cref="AssetDownloadRequest.PrefabPath"/>。
        /// </summary>
        public static StartDownloadResult StartDownload(AssetDownloadRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.AssetId))    throw new ArgumentException("AssetId is required",    nameof(request));
            if (string.IsNullOrWhiteSpace(request.Name))       throw new ArgumentException("Name is required",       nameof(request));
            if (string.IsNullOrWhiteSpace(request.Url))        throw new ArgumentException("Url is required",        nameof(request));
            if (string.IsNullOrWhiteSpace(request.PrefabPath)) throw new ArgumentException("PrefabPath is required", nameof(request));

            // Unity 禁止在 Play 模式下 ImportPackage；下载到项目与放入场景均不可用。
            if (TJGeneratorsPlayModeGuard.IsActive)
            {
                return new StartDownloadResult
                {
                    Status  = DownloadTaskStatus.Failed,
                    Message = TJGeneratorsPlayModeGuard.DownloadOrPlaceShortHint,
                };
            }

            // asset_id 即 slug，直接用作目录名
            string packageDir   = DefaultDestBase + "/" + request.AssetId;
            string metadataPath = packageDir + "/" + request.AssetId + "_metadata.json";
            string tempPath     = Path.Combine(Application.temporaryCachePath, request.AssetId + ".unitypackage");

            string absPackageDir = Utils.PathUtils.ToAbsoluteAssetPath(packageDir);

            // 已存在于工程：本 asset 目录已有 prefab，或同一 unitypackage URL 已由其它条目导入（animation 多动作）。
            if (TryGetImportedPrefabPath(request.Url, request.AssetId, request.PrefabPath, out string existingResolved)
                && !string.IsNullOrEmpty(existingResolved))
            {
                string skipTaskId = AssetDownloadTracker.CreateTask(
                    request.AssetId, request.Name, packageDir, request.PrefabPath, metadataPath,
                    request.Category ?? "", request.Source ?? "", request.Description ?? "",
                    request.PrefabMeta ?? "", request.Query ?? "", request.Score, request.Keywords ?? "",
                    request.PreviewUrl ?? "", request.Url ?? "");
                AssetDownloadTracker.MarkSkipped(skipTaskId, existingResolved);
                var restored = AssetDownloadTracker.GetTask(skipTaskId);
                TJLog.Log(
                    $"[AssetDownloadService] Download skipped (already in project): task_id={skipTaskId}, prefab={existingResolved}, unitypackage_url={request.Url}");

                if (request.InstantiateInScene)
                {
                    string prefabToInstantiate = restored?.PrefabPath ?? existingResolved;
                    EditorApplication.delayCall += () =>
                        AssetPostImportProcessor.InstantiatePrefabInScene(prefabToInstantiate);
                }

                return new StartDownloadResult
                {
                    TaskId     = skipTaskId,
                    Status     = DownloadTaskStatus.Skipped,
                    PrefabPath = restored?.PrefabPath ?? existingResolved,
                    Message    = "Asset already imported; skipped download.",
                };
            }

            if (Directory.Exists(absPackageDir))
            {
                // 目录存在但无 .prefab —— 上次失败残留，清掉防止 ImportPackage/MoveAsset 冲突
                TJLog.LogWarning($"[AssetDownloadService] Orphan package dir found without .prefab, cleaning up: {packageDir}");
                AssetDatabase.DeleteAsset(packageDir);
            }

            string taskId = AssetDownloadTracker.CreateTask(
                request.AssetId, request.Name, packageDir, request.PrefabPath, metadataPath,
                request.Category ?? "", request.Source ?? "", request.Description ?? "",
                request.PrefabMeta ?? "", request.Query ?? "", request.Score, request.Keywords ?? "",
                request.PreviewUrl ?? "", request.Url ?? "");

            // 后台落账：直连 Codely 的下载不经后台缓存，这里把资产元数据报到后台用于
            // 首次扣费 + 任务记录 + 去重（best-effort，失败不阻断下载）。
            var billing = AssetDownloadBilling.Record(request);

            AssetDownloadPipeline.StartDownload(
                taskId, request.Url, request.PrefabPath, packageDir, tempPath,
                request.SessionId ?? "", request.InstantiateInScene);

            TJLog.Log(
                $"[AssetDownloadService] Download started: task_id={taskId}, name={request.Name}, asset_id={request.AssetId}, dir={packageDir}, unitypackage_url={request.Url}");

            return new StartDownloadResult
            {
                TaskId          = taskId,
                Status          = DownloadTaskStatus.Downloading,
                Message         = "Download started. Poll GetTask(taskId) to track progress.",
                IsFirstDownload = billing.IsFirstDownload,
                CreditsCharged  = billing.CreditsCharged,
                BillingError    = billing.HasError ? billing.Error : null,
            };
        }

        // 检测目录是否存在并含 .prefab；优先同名匹配（Path.GetFileName(originalPrefabPath)），
        // 未命中则取目录下首个 .prefab，都没有返回 null。返回值为项目相对路径。
        private static string TryFindExistingPrefab(string absPackageDir, string packageDir, string originalPrefabPath)
        {
            if (!Directory.Exists(absPackageDir)) return null;

            string[] prefabFiles;
            try { prefabFiles = Directory.GetFiles(absPackageDir, "*.prefab", SearchOption.TopDirectoryOnly); }
            catch { return null; }
            if (prefabFiles.Length == 0) return null;

            string preferredFileName = string.IsNullOrEmpty(originalPrefabPath)
                ? null : Path.GetFileName(originalPrefabPath);
            string hit = null;
            foreach (var f in prefabFiles)
            {
                if (preferredFileName != null &&
                    string.Equals(Path.GetFileName(f), preferredFileName, StringComparison.OrdinalIgnoreCase))
                {
                    hit = f;
                    break;
                }
            }
            if (hit == null) hit = prefabFiles[0];

            return packageDir + "/" + Path.GetFileName(hit);
        }
    }
}
#endif
