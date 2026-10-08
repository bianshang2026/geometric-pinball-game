#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Unity.EditorCoroutines.Editor;
using TJGenerators.Utils;
using UnityEditor;
using UnityEngine.Networking;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// 下载 → 导入 → 后处理的核心协程与调度入口。原 CustomTool 侧内联实现
    /// 搬迁至此（该工具已移除，资产库搜索/下载改由后端提供）；对外只公开 <see cref="Run"/> 与
    /// <see cref="ProcessImportQueue"/>。
    /// </summary>
    public static class AssetDownloadPipeline
    {
        public const int    DownloadTimeoutSeconds = 300; // 5 分钟整体超时（单次尝试）
        public const int    MaxDownloadRetries     = 3;   // 失败后最多重试次数
        public const double RetryDelaySeconds      = 1.0; // 相邻两次重试之间等待时长（秒）
        private const string LogTag = "[AssetDownloadPipeline]";

        /// <summary>
        /// 启动下载协程（IncrementInFlight + StartCoroutineOwnerless）。
        /// 任何退出路径均须 DecrementInFlight，保持计数配对——在协程内部已处理。
        /// </summary>
        public static void StartDownload(
            string taskId,
            string url,
            string prefabPath,
            string packageDir,
            string tempPath,
            string sessionId = "",
            bool   instantiateInScene = false)
        {
            AssetDownloadTracker.IncrementInFlight();
            EditorCoroutineUtility.StartCoroutineOwnerless(
                Run(taskId, url, prefabPath, packageDir, tempPath, sessionId ?? "", instantiateInScene));
        }

        /// <summary>
        /// 核心下载协程：UnityWebRequest 下载 → 解析包文件列表 → 加入导入队列。
        /// 入口对 URL 做反斜杠规范化（<see cref="PathUtils.NormalizeRemoteUrl"/>），不做 Uri 完整规范化。
        /// </summary>
        public static IEnumerator Run(
            string taskId,
            string url,
            string prefabPath,
            string packageDir,
            string tempPath,
            string sessionId,
            bool   instantiateInScene = false)
        {
            // 清理上次中断可能遗留的临时文件（DownloadHandlerFile 虽会覆盖写入，但显式清理保证幂等）
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }

            url = PathUtils.NormalizeRemoteUrl(url);

            // 仅将 URL 中的反斜杠替换为正斜杠（见 NormalizeRemoteUrl）；不做 Uri.AbsoluteUri 等完整规范化，
            // 否则会对已编码字符二次处理（如 %2F → %252F）或重解析路径段，导致签名校验失败。
            bool   downloadOk = false;
            string finalError = null;

            for (int attempt = 0; attempt <= MaxDownloadRetries; attempt++)
            {
                if (attempt > 0)
                {
                    TJLog.LogWarning($"{LogTag} 下载重试 {attempt}/{MaxDownloadRetries}，taskId={taskId}");
                    var retryWait = DateTime.Now;
                    while ((DateTime.Now - retryWait).TotalSeconds < RetryDelaySeconds)
                        yield return null;
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
                }

                var req = UnityWebRequest.Get(url);
                req.downloadHandler = new DownloadHandlerFile(tempPath);
                // 不设 req.timeout（保持默认 0），避免 ImportPackage 阻塞主线程期间误触发 native 超时

                req.SendWebRequest();
                var startTime = DateTime.Now;
                bool timedOut = false;

                while (!req.isDone)
                {
                    if ((DateTime.Now - startTime).TotalSeconds > DownloadTimeoutSeconds)
                    {
                        req.Abort();
                        timedOut = true;
                        break;
                    }
                    yield return null;
                }

                if (timedOut)
                {
                    req.Dispose();
                    PathUtils.SafeDelete(tempPath);
                    finalError = $"TIMEOUT: download exceeded {DownloadTimeoutSeconds}s";
                    TJLog.LogWarning($"{LogTag} 下载超时（第 {attempt + 1} 次），taskId={taskId} url={url}");
                    continue;
                }

                if (UnityWebRequestCompat.IsSuccess(req))
                {
                    req.Dispose();
                    downloadOk = true;
                    break;
                }

                // 下载失败：记录诊断信息
                long   httpCode  = req.responseCode;
                string httpError = req.error ?? "Unknown download error";
                req.Dispose();
                PathUtils.SafeDelete(tempPath);

                if (httpCode == 400)
                {
                    // 签名 URL 已过期，重试无意义
                    finalError = "URL_EXPIRED: Download URL returned HTTP 400 (signed URL likely expired). " +
                                 "Re-run the asset library search to get a fresh URL, then retry the download.";
                    TJLog.LogError(
                        $"{LogTag} 下载失败（HTTP 400，URL 已过期，不再重试）" +
                        $" taskId={taskId} httpCode={httpCode} error={httpError} url={url}");
                    break;
                }

                finalError = $"HTTP {httpCode}: {httpError}";
                if (attempt < MaxDownloadRetries)
                    TJLog.LogWarning(
                        $"{LogTag} 下载失败（第 {attempt + 1} 次，将重试）" +
                        $" taskId={taskId} httpCode={httpCode} error={httpError} url={url}");
            }

            if (!downloadOk)
            {
                TJLog.LogError(
                    $"{LogTag} 下载最终失败（已重试 {MaxDownloadRetries} 次）" +
                    $" taskId={taskId} url={url} error={finalError}");
                AssetDownloadTracker.MarkFailed(taskId, finalError);
                AssetDownloadTracker.DecrementInFlight();
                yield break;
            }

            // 扫描文件列表 + 检测 .cs
            System.Collections.Generic.List<string> importedFiles;
            try { importedFiles = UnityPackageInspector.GetPackageFileList(tempPath); }
            catch (Exception ex)
            {
                AssetDownloadTracker.MarkFailed(taskId, $"PARSE_ERROR: {ex.Message}");
                PathUtils.SafeDelete(tempPath);
                AssetDownloadTracker.DecrementInFlight();
                yield break;
            }
            bool hasScripts = WillTriggerDomainReload(importedFiles);

            // 加入导入队列并触发处理
            AssetImportQueue.Enqueue(new AssetImportQueueEntry
            {
                TaskId             = taskId,
                TempPath           = tempPath,
                PrefabPath         = prefabPath,
                PackageDir         = packageDir,
                ImportedFiles      = importedFiles,
                HasScripts         = hasScripts,
                SessionId          = sessionId ?? "",
                InstantiateInScene = instantiateInScene,
            });
            AssetDownloadTracker.DecrementInFlight();
            ProcessImportQueue();
        }

        /// <summary>
        /// 从 ImportQueue 取队首条目，检查不变量（含 .cs 需等 in-flight == 0）；
        /// 移至 PendingPostImportQueue；调用 ImportPackage；订阅 <see cref="AssetDatabase.importPackageCompleted"/>
        /// 等事件，在 Unity 确认导入彻底完成后才触发后处理。
        /// <para>关键点：<c>AssetDatabase.ImportPackage(..., interactive: false)</c> 在 Unity 内部是异步的——
        /// 文件按批写入磁盘并登记到 AssetDatabase，跨多帧完成。因此不能仅用 <c>EditorApplication.delayCall</c>
        /// （下一帧就触发）来判定"导入结束"；对文件较多的包（如 Kenney 86 文件），会出现 ResolveActualPrefab
        /// 时 <c>AssetPathToGUID</c> 全部返回空、报 "no .prefab resolved" 的假阴性。</para>
        /// <para>domain reload（含 .cs 包）发生在 importPackageCompleted 之后，不影响正常路径；
        /// 若 delayCall 被 reload 吞掉，<see cref="AssetDomainReloadHook"/> 会重新从
        /// PendingPostImportQueue 拾回条目，作为兜底。</para>
        /// </summary>
        public static void ProcessImportQueue()
        {
            var entry = AssetImportQueue.PeekImportQueue();
            if (entry == null) return;

            // Unity 禁止 Play 模式下 ImportPackage：保留队首，退出 Play 后再导入。
            if (TJGeneratorsPlayModeGuard.IsActive)
            {
                EnsureResumeImportAfterPlayMode();
                TJLog.Log($"{LogTag} Play 模式中，推迟 ImportPackage，task={entry.TaskId}");
                return;
            }

            if (entry.HasScripts && AssetDownloadTracker.GetInFlight() > 0)
            {
                // 等待所有下载完成后再次触发
                return;
            }

            // 移至 PendingPostImportQueue（SessionState 持久化），确保 domain reload 后可恢复
            AssetImportQueue.DropImportQueueHead();
            AssetImportQueue.PushPending(entry);

            string taskId   = entry.TaskId;
            string tempPath = entry.TempPath;
            AssetDownloadTracker.SetStatus(taskId, DownloadTaskStatus.Importing);

            // ── 过滤 Packages/ 条目 ────────────────────────────────────────────────
            // 部分 .unitypackage 会将依赖的 URP 等 Package Manager 包一同打包，
            // 导入时会覆盖项目中已有的版本（如将 URP 17.0.3 替换为 git hash 版），
            // 造成 Shader 编译失败与材质变紫。在调用 ImportPackage 前流式剔除这些条目。
            string importPath    = tempPath;
            string filteredPath  = null;
            try
            {
                filteredPath = UnityPackageInspector.CreateFilteredPackage(
                    tempPath,
                    pathname =>
                        pathname.StartsWith("Packages/",       StringComparison.OrdinalIgnoreCase) ||
                        pathname.StartsWith("ProjectSettings/PackageManagerSettings",
                                            StringComparison.OrdinalIgnoreCase));

                if (filteredPath != null)
                {
                    importPath = filteredPath;
                    TJLog.LogWarning(
                        $"{LogTag} 包含 Packages/ 系统条目，已生成过滤版本用于导入，task={taskId}");
                }
            }
            catch (Exception ex)
            {
                TJLog.LogWarning(
                    $"{LogTag} 过滤包失败，将使用原始文件导入（{ex.Message}），task={taskId}");
                PathUtils.SafeDelete(filteredPath);
                filteredPath = null;
                importPath   = tempPath;
            }
            // ──────────────────────────────────────────────────────────────────────

            // 不依赖 importPackageCompleted 传入的 packageName 做匹配：
            // Unity 传入的名称来自 .unitypackage 内部元数据（由包作者导出时设定），与我们临时文件名无关。
            // 第三方包（如 Kenney）的内部名完全不同，导致名字匹配失败、回调被忽略、任务永远卡在 importing。
            // 改为：检查该 taskId 是否仍在 PendingPostImport 队列中——在则处理，不在则已被其他回调处理过。
            AssetDatabase.ImportPackageCallback       onCompleted = null;
            AssetDatabase.ImportPackageFailedCallback onFailed    = null;
            AssetDatabase.ImportPackageCallback       onCancelled = null;

            void Unsubscribe()
            {
                if (onCompleted != null) AssetDatabase.importPackageCompleted -= onCompleted;
                if (onFailed    != null) AssetDatabase.importPackageFailed    -= onFailed;
                if (onCancelled != null) AssetDatabase.importPackageCancelled -= onCancelled;
            }

            onCompleted = (pkgName) =>
            {
                if (!AssetImportQueue.IsPending(taskId)) return;
                Unsubscribe();
                // 再延后一帧，让 AssetDatabase 最终化。
                EditorApplication.delayCall += () =>
                {
                    PathUtils.SafeDelete(tempPath);
                    PathUtils.SafeDelete(filteredPath);
                    AssetPostImportProcessor.ProcessAll();
                    EditorApplication.delayCall += ProcessImportQueue;
                };
            };

            onFailed = (pkgName, errMsg) =>
            {
                if (!AssetImportQueue.IsPending(taskId)) return;
                Unsubscribe();
                // 不立即 MarkFailed：Unity 对某些非致命错误（如 Prefab Variant 缺少父 Prefab）也会触发
                // importPackageFailed，但文件已物理写入磁盘。先尝试后处理恢复；若 ProcessOne 仍找不到
                // 可用 prefab，会在内部 MarkFailed，行为与直接失败一致。
                TJLog.LogWarning(
                    $"{LogTag} importPackageFailed task={taskId}, err={errMsg}; attempting post-import recovery");
                EditorApplication.delayCall += () =>
                {
                    PathUtils.SafeDelete(tempPath);
                    PathUtils.SafeDelete(filteredPath);
                    AssetPostImportProcessor.ProcessAll();
                    EditorApplication.delayCall += ProcessImportQueue;
                };
            };

            onCancelled = (pkgName) =>
            {
                if (!AssetImportQueue.IsPending(taskId)) return;
                Unsubscribe();
                TJLog.LogWarning($"{LogTag} importPackageCancelled task={taskId}");
                AssetDownloadTracker.MarkFailed(taskId, "ImportPackage was cancelled");
                PathUtils.SafeDelete(tempPath);
                PathUtils.SafeDelete(filteredPath);
                AssetImportQueue.RemovePending(taskId);
                EditorApplication.delayCall += ProcessImportQueue;
            };

            // 订阅顺序：务必在 ImportPackage 之前完成订阅，避免同步回调遗漏。
            AssetDatabase.importPackageCompleted += onCompleted;
            AssetDatabase.importPackageFailed    += onFailed;
            AssetDatabase.importPackageCancelled += onCancelled;

            try
            {
                AssetDatabase.ImportPackage(importPath, false);
            }
            catch (Exception ex)
            {
                Unsubscribe();
                TJLog.LogError($"{LogTag} ImportPackage threw for task {taskId}: {ex.Message}");
                AssetDownloadTracker.MarkFailed(taskId, $"ImportPackage failed: {ex.Message}");
                PathUtils.SafeDelete(tempPath);
                PathUtils.SafeDelete(filteredPath);
                AssetImportQueue.RemovePending(taskId);
                EditorApplication.delayCall += ProcessImportQueue;
            }

            // 统一由 importPackageCompleted / Failed / Cancelled 驱动后续流程——
            // 对含 .cs 包，完成回调在 domain reload 之前触发，ProcessAll 正常跑完即可；
            // 若 delayCall 被 reload 吞掉，AssetDomainReloadHook 会兜底拾回 PendingPostImportQueue。
        }

        // 判断 unitypackage 是否会触发 Domain Reload。除 .cs 外，.dll / .asmdef / .asmref / .rsp 同样会触发。
        // 字段名在 SessionState 中仍叫 hasScripts，保持向后兼容。
        private static bool WillTriggerDomainReload(System.Collections.Generic.List<string> importedFiles)
        {
            if (importedFiles == null) return false;
            foreach (var p in importedFiles)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (p.EndsWith(".cs",      StringComparison.OrdinalIgnoreCase)) return true;
                if (p.EndsWith(".dll",     StringComparison.OrdinalIgnoreCase)) return true;
                if (p.EndsWith(".asmdef",  StringComparison.OrdinalIgnoreCase)) return true;
                if (p.EndsWith(".asmref",  StringComparison.OrdinalIgnoreCase)) return true;
                if (p.EndsWith(".rsp",     StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static bool s_resumeImportHooked;

        static void EnsureResumeImportAfterPlayMode()
        {
            if (s_resumeImportHooked)
                return;
            s_resumeImportHooked = true;
            EditorApplication.playModeStateChanged += OnPlayModeStateChangedForImport;
        }

        static void OnPlayModeStateChangedForImport(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode)
                return;
            s_resumeImportHooked = false;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChangedForImport;
            EditorApplication.delayCall += ProcessImportQueue;
        }
    }

    /// <summary>
    /// Domain Reload 后自动恢复挂起的后处理 + 触发下一个导入。
    /// </summary>
    internal static class AssetDomainReloadHook
    {
        [InitializeOnLoadMethod]
        private static void OnDomainReload()
        {
            // 1. 完成被 domain reload 中断的遗留后处理
            EditorApplication.delayCall += () =>
            {
                AssetPostImportProcessor.ProcessAll();
                // 2. 延迟触发下一个导入（需等 AssetDatabase 就绪）
                EditorApplication.delayCall += AssetDownloadPipeline.ProcessImportQueue;
            };
        }
    }
}
#endif
