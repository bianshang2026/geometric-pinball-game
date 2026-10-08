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
    /// <summary>
    /// 历史记录持久化管理
    /// </summary>
    public static class TJGeneratorsHistoryManager
    {
        private const string HistoryPrefsKey = "TJGeneratorsGenerationHistory";
        private const string LegacyHistoryPrefsKey = "ApexGenGenerationHistory"; // 旧键名，用于迁移
        private const int MaxHistoryCount = 100;
        private static bool hasMigrated = false;

        [Serializable]
        private class HistoryWrapper
        {
            public List<TJGeneratorsGenerationHistoryItem> items = new List<TJGeneratorsGenerationHistoryItem>();
        }

        /// <summary>
        /// 从旧键名迁移历史记录到新键名
        /// </summary>
        private static void MigrateFromLegacyKey()
        {
            if (hasMigrated) return;

            var newJson = EditorPrefs.GetString(HistoryPrefsKey, "");
            var legacyJson = EditorPrefs.GetString(LegacyHistoryPrefsKey, "");

            // 如果新键名没有数据，但旧键名有数据，则迁移
            if (string.IsNullOrEmpty(newJson) && !string.IsNullOrEmpty(legacyJson))
            {
                TJLog.Log("[TJGeneratorsHistoryManager] 正在从旧版本迁移历史记录...");
                EditorPrefs.SetString(HistoryPrefsKey, legacyJson);
                EditorPrefs.DeleteKey(LegacyHistoryPrefsKey);
                TJLog.Log("[TJGeneratorsHistoryManager] 历史记录迁移完成");
            }
            // 如果两边都有数据，删除旧的
            else if (!string.IsNullOrEmpty(legacyJson))
            {
                EditorPrefs.DeleteKey(LegacyHistoryPrefsKey);
            }

            hasMigrated = true;
        }

        private static List<TJGeneratorsGenerationHistoryItem> LoadAllHistory(bool filterMissingModelFiles = false)
        {
            MigrateFromLegacyKey(); // 确保迁移

            var json = EditorPrefs.GetString(HistoryPrefsKey, "");
            if (string.IsNullOrEmpty(json))
            {
                return new List<TJGeneratorsGenerationHistoryItem>();
            }

            try
            {
                var wrapper = JsonUtility.FromJson<HistoryWrapper>(json);
                if (wrapper?.items == null)
                    return new List<TJGeneratorsGenerationHistoryItem>();

                if (filterMissingModelFiles)
                {
                    wrapper.items.RemoveAll(item =>
                        !item.isGenerating
                        && !string.IsNullOrEmpty(item.modelPath)
                        && !DoesModelFileExist(item.modelPath));
                }

                return wrapper.items;
            }
            catch
            {
                return new List<TJGeneratorsGenerationHistoryItem>();
            }
        }

        private static bool DoesModelFileExist(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath))
                return false;

            if (modelPath.StartsWith("Assets/") || modelPath.StartsWith("Assets\\"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(modelPath);
                return asset != null;
            }

            return File.Exists(modelPath);
        }

        public static List<TJGeneratorsGenerationHistoryItem> LoadHistory()
        {
            return LoadAllHistory(filterMissingModelFiles: false);
        }

        public static List<TJGeneratorsGenerationHistoryItem> LoadHistoryForAsset(string assetGuid)
        {
            var allHistory = LoadAllHistory(filterMissingModelFiles: true);
            var targetGuid = assetGuid ?? "";
            var result = allHistory.Where(h =>
                (h.assetGuid ?? "") == targetGuid
                || (h.linkedAssetGuid ?? "") == targetGuid).ToList();

            if (!string.IsNullOrEmpty(targetGuid))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(targetGuid);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    string normalized = assetPath.Replace('\\', '/');
                    foreach (var h in allHistory)
                    {
                        if (string.IsNullOrEmpty(h.modelPath))
                            continue;
                        if (!string.Equals(h.modelPath.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (result.Any(r => r.taskId == h.taskId))
                            continue;
                        result.Add(h);
                    }
                }
            }

            result = result.OrderByDescending(h => h.timestamp).ToList();
            if (result.Count > 0)
                TJLog.Log($"[TJGeneratorsHistoryManager] LoadHistoryForAsset: assetGuid={targetGuid}, total={allHistory.Count}, filtered={result.Count}");
            return result;
        }

        /// <summary>
        /// 加载 Material 资产的生成历史（assetGuid 绑定 .mat；并合并同名贴图上的历史）。
        /// </summary>
        public static List<TJGeneratorsGenerationHistoryItem> LoadHistoryForMaterialAsset(string materialAssetPath)
        {
            string materialGuid = string.IsNullOrEmpty(materialAssetPath)
                ? ""
                : AssetDatabase.AssetPathToGUID(materialAssetPath);
            var result = LoadHistoryForAsset(materialGuid ?? "");

            // 从历史 PNG 打开时会立刻绑到同名 .mat；旧记录可能仍挂在贴图 GUID 上
            if (!string.IsNullOrEmpty(materialAssetPath))
            {
                string dir = Path.GetDirectoryName(materialAssetPath)?.Replace('\\', '/') ?? "";
                string baseName = Path.GetFileNameWithoutExtension(materialAssetPath);
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg" })
                {
                    string texPath = string.IsNullOrEmpty(dir) ? baseName + ext : $"{dir}/{baseName}{ext}";
                    string texGuid = AssetDatabase.AssetPathToGUID(texPath);
                    if (string.IsNullOrEmpty(texGuid))
                        continue;

                    foreach (var item in LoadHistoryForAsset(texGuid))
                    {
                        if (result.Any(r => r.taskId == item.taskId))
                            continue;
                        result.Add(item);
                    }
                }

                result = result.OrderByDescending(h => h.timestamp).ToList();
            }

            return result;
        }

        /// <summary>
        /// 材质生成完成时：Material 绑定 assetGuid，贴图 PNG 绑定 linkedAssetGuid。
        /// 仅在尚无有效材质 GUID 时，才回退到贴图旁同名 .mat。
        /// </summary>
        public static void TryLinkMaterialTextureHistoryItem(TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.modelPath))
                return;

            string texturePath = item.modelPath.Replace('\\', '/');
            string textureGuid = AssetDatabase.AssetPathToGUID(texturePath);
            if (string.IsNullOrEmpty(textureGuid))
                return;

            item.linkedAssetGuid = textureGuid;

            // 已有指向真实 Material 的 assetGuid（如 New Material.mat）时保留，勿改成贴图旁同名 .mat
            if (!string.IsNullOrEmpty(item.assetGuid)
                && !string.Equals(item.assetGuid, textureGuid, StringComparison.Ordinal))
            {
                string existingPath = AssetDatabase.GUIDToAssetPath(item.assetGuid);
                if (!string.IsNullOrEmpty(existingPath)
                    && existingPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
                    && AssetDatabase.LoadAssetAtPath<Material>(existingPath) != null)
                {
                    return;
                }
            }

            string matPath = Path.ChangeExtension(texturePath, ".mat");
            string matGuid = AssetDatabase.AssetPathToGUID(matPath);
            if (!string.IsNullOrEmpty(matGuid))
                item.assetGuid = matGuid;
        }

        /// <summary>
        /// 从材质历史贴图反查生成时绑定的目标 Material 路径（如 New Material.mat）。
        /// </summary>
        public static string TryResolveMaterialPathForTexture(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath))
                return null;

            string normalized = texturePath.Replace('\\', '/');
            string textureGuid = AssetDatabase.AssetPathToGUID(normalized);
            var allHistory = LoadAllHistory(filterMissingModelFiles: false);

            TJGeneratorsGenerationHistoryItem best = null;
            foreach (var h in allHistory)
            {
                bool matchByGuid = !string.IsNullOrEmpty(textureGuid)
                    && (string.Equals(h.linkedAssetGuid, textureGuid, StringComparison.Ordinal)
                        || string.Equals(h.assetGuid, textureGuid, StringComparison.Ordinal));
                bool matchByPath = !string.IsNullOrEmpty(h.modelPath)
                    && string.Equals(h.modelPath.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase);
                if (!matchByGuid && !matchByPath)
                    continue;

                if (string.IsNullOrEmpty(h.assetGuid)
                    || string.Equals(h.assetGuid, textureGuid, StringComparison.Ordinal))
                    continue;

                string matPath = AssetDatabase.GUIDToAssetPath(h.assetGuid);
                if (string.IsNullOrEmpty(matPath)
                    || !matPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
                    || AssetDatabase.LoadAssetAtPath<Material>(matPath) == null)
                    continue;

                if (best == null || h.timestamp > best.timestamp)
                    best = h;
            }

            return best == null ? null : AssetDatabase.GUIDToAssetPath(best.assetGuid);
        }

        public static void SaveHistory(List<TJGeneratorsGenerationHistoryItem> history)
        {
            while (history.Count > MaxHistoryCount)
            {
                history.RemoveAt(history.Count - 1);
            }

            var wrapper = new HistoryWrapper { items = history };
            var json = JsonUtility.ToJson(wrapper);
            EditorPrefs.SetString(HistoryPrefsKey, json);
        }

        public static string AddGeneratingPlaceholder(
            string prompt,
            string imagePath,
            string modelVersion,
            bool isTextToModel,
            string assetGuid,
            string userDisplayPrompt = null,
            string sessionId = ""
        )
        {
            var history = LoadHistory();
            var targetGuid = assetGuid ?? "";

            TJLog.Log($"[TJGeneratorsHistoryManager] AddGeneratingPlaceholder: assetGuid={targetGuid}, modelVersion={modelVersion}, isTextToModel={isTextToModel}");

            var existingPlaceholder = history.Find(h =>
                h.isGenerating &&
                (h.assetGuid ?? "") == targetGuid &&
                h.isTextToModel == isTextToModel &&
                (isTextToModel ? h.prompt == prompt : h.imagePath == imagePath));

            if (existingPlaceholder != null)
            {
                TJLog.Log($"[TJGeneratorsHistoryManager] 检测到重复的生成请求，复用现有占位符: {existingPlaceholder.taskId}");
                return existingPlaceholder.taskId;
            }

            var taskId = Guid.NewGuid().ToString();
            string trimmedUserDisplay = string.IsNullOrWhiteSpace(userDisplayPrompt)
                ? null
                : userDisplayPrompt.Trim();

            var item = new TJGeneratorsGenerationHistoryItem
            {
                modelPath = "",
                prompt = prompt,
                userPrompt = trimmedUserDisplay,
                imagePath = imagePath,
                timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                modelVersion = modelVersion,
                isTextToModel = isTextToModel,
                isGenerating = true,
                taskId = taskId,
                assetGuid = targetGuid,
                sessionId = sessionId ?? ""
            };

            history.Insert(0, item);
            SaveHistory(history);

            TJLog.Log($"[TJGeneratorsHistoryManager] 创建新占位符: taskId={taskId}, assetGuid={targetGuid}");

            return taskId;
        }

        public static void CompletePlaceholder(string taskId, string modelPath, string previewImageUrl = null, string promptTemplateId = null)
        {
            TJLog.Log($"[TJGeneratorsHistoryManager] CompletePlaceholder called: taskId={taskId}, modelPath={modelPath}, previewImageUrl={previewImageUrl ?? "null"}, promptTemplateId={promptTemplateId ?? "null"}");
            var history = LoadHistory();
            TJLog.Log($"[TJGeneratorsHistoryManager] Loaded {history.Count} history items");
            var item = history.Find(h => h.taskId == taskId);
            if (item != null)
            {
                TJLog.Log($"[TJGeneratorsHistoryManager] Found item: taskId={item.taskId}, isGenerating={item.isGenerating}, assetGuid={item.assetGuid}");
                if (!item.isGenerating)
                {
                    TJLog.Log($"[TJGeneratorsHistoryManager] 任务 {taskId} 已完成，跳过重复完成");
                    return;
                }

                item.modelPath = modelPath;
                item.isGenerating = false;
                item.timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                item.previewImageUrl = previewImageUrl;
                item.promptTemplateId = promptTemplateId;
                TryLinkMaterialTextureHistoryItem(item);
                SaveHistory(history);
                TJLog.Log($"[TJGeneratorsHistoryManager] 任务 {taskId} 已完成，modelPath={modelPath}, previewImageUrl={previewImageUrl ?? "null"}, promptTemplateId={promptTemplateId ?? "null"}");
            }
            else
            {
                TJLog.LogWarning($"[TJGeneratorsHistoryManager] 未找到taskId={taskId}的占位符");
            }
        }

        /// <summary>
        /// 多图完成：将占位符更新为第一张图，并插入其余张为独立历史条（一图一格）。
        /// </summary>
        public static void CompletePlaceholderMultiImage(string taskId, List<string> modelPaths, string[] imageUrls, string promptTemplateId = null)
        {
            if (modelPaths == null || modelPaths.Count == 0 || imageUrls == null || imageUrls.Length != modelPaths.Count)
            {
                TJLog.LogWarning("[TJGeneratorsHistoryManager] CompletePlaceholderMultiImage 参数无效");
                return;
            }

            var history = LoadHistory();
            int idx = history.FindIndex(h => h.taskId == taskId);
            if (idx < 0)
            {
                TJLog.LogWarning($"[TJGeneratorsHistoryManager] 未找到 taskId={taskId} 的占位符");
                return;
            }

            var placeholder = history[idx];
            if (!placeholder.isGenerating)
            {
                TJLog.Log("[TJGeneratorsHistoryManager] 任务已完成，跳过");
                return;
            }

            placeholder.modelPath = modelPaths[0];
            // Priority 2: result URL；Priority 3: 本地文件 URI（文件不存在时为 null）
            if (imageUrls != null && imageUrls.Length > 0 && !string.IsNullOrEmpty(imageUrls[0]))
            {
                placeholder.previewImageUrl = imageUrls[0];
            }
            else if (!string.IsNullOrEmpty(modelPaths[0]))
            {
                string fullPath0 = Path.GetFullPath(modelPaths[0]);
                placeholder.previewImageUrl = File.Exists(fullPath0)
                    ? "file://" + fullPath0.Replace('\\', '/')
                    : null;
            }
            placeholder.isGenerating = false;
            placeholder.timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            placeholder.promptTemplateId = promptTemplateId;

            for (int i = 1; i < modelPaths.Count; i++)
            {
                string itemPreviewUrl;
                if (imageUrls != null && i < imageUrls.Length && !string.IsNullOrEmpty(imageUrls[i]))
                {
                    itemPreviewUrl = imageUrls[i];
                }
                else if (!string.IsNullOrEmpty(modelPaths[i]))
                {
                    string fullPath = Path.GetFullPath(modelPaths[i]);
                    itemPreviewUrl = File.Exists(fullPath)
                        ? "file://" + fullPath.Replace('\\', '/')
                        : null;
                }
                else
                {
                    itemPreviewUrl = null;
                }

                history.Insert(idx + i, new TJGeneratorsGenerationHistoryItem
                {
                    modelPath = modelPaths[i],
                    previewImageUrl = itemPreviewUrl,
                    prompt = placeholder.prompt,
                    userPrompt = placeholder.userPrompt,
                    imagePath = placeholder.imagePath,
                    timestamp = placeholder.timestamp,
                    modelVersion = placeholder.modelVersion,
                    isTextToModel = placeholder.isTextToModel,
                    isGenerating = false,
                    taskId = "",
                    assetGuid = placeholder.assetGuid,
                    promptTemplateId = promptTemplateId,
                    sessionId = placeholder.sessionId
                });
            }

            SaveHistory(history);
            TJLog.Log($"[TJGeneratorsHistoryManager] 多图完成: {modelPaths.Count} 条历史");
        }

        public static void RemovePlaceholder(string taskId)
        {
            var history = LoadHistory();
            history.RemoveAll(h => h.taskId == taskId);
            SaveHistory(history);
        }

        public static void UpdatePlaceholderProgress(string taskId, int progress)
        {
            var history = LoadHistory();
            var item = history.Find(h => h.taskId == taskId && h.isGenerating);
            if (item != null)
            {
                item.progress = progress;
                SaveHistory(history);
            }
        }

        public static void RemoveFromHistory(string modelPath)
        {
            var history = LoadHistory();
            history.RemoveAll(h => h.modelPath == modelPath);
            SaveHistory(history);
        }

        /// <summary>
        /// 删除 EditorPrefs 中的全部生成历史（含旧版键名），无法撤销。
        /// </summary>
        public static void ClearAllHistory()
        {
            EditorPrefs.DeleteKey(HistoryPrefsKey);
            EditorPrefs.DeleteKey(LegacyHistoryPrefsKey);
            hasMigrated = false;
            TJLog.Log("[TJGeneratorsHistoryManager] 已清空所有生成历史记录");
        }

#if TJGENERATORS_DEBUG
        /// <summary>
        /// 仅移除历史中的「生成中」占位符（isGenerating），保留已完成记录。仅 DEBUG 构建可用。
        /// </summary>
        /// <returns>移除的条数。</returns>
        public static int ClearAllGeneratingPlaceholders()
        {
            var history = LoadHistory();
            int removed = history.RemoveAll(h => h != null && h.isGenerating);
            if (removed > 0)
                SaveHistory(history);
            TJLog.Log($"[TJGeneratorsHistoryManager] 已清除 {removed} 条生成中占位符");
            return removed;
        }
#endif

        /// <summary>
        /// 目标资源从 .wav 占位变为 .mp4 / .mp3 等时 GUID 会变，将已有关联的历史条目的 assetGuid 迁移到新 GUID，避免面板历史丢失。
        /// </summary>
        /// <param name="filter">可选过滤谓词，仅当返回 true 时才迁移该条目。传 null 表示迁移所有匹配条目。</param>
        public static void RewriteAssetGuid(string oldGuid, string newGuid,
            Predicate<TJGeneratorsGenerationHistoryItem> filter = null)
        {
            // oldGuid may be empty string: migrates items with assetGuid=="" to a newly bound asset.
            if (string.IsNullOrEmpty(newGuid)
                || string.Equals(oldGuid ?? "", newGuid, StringComparison.Ordinal))
                return;

            var history = LoadAllHistory();
            bool changed = false;
            foreach (var h in history)
            {
                if (!string.Equals(h.assetGuid ?? "", oldGuid, StringComparison.Ordinal))
                    continue;
                if (filter != null && !filter(h))
                    continue;
                h.assetGuid = newGuid;
                changed = true;
            }

            if (changed)
            {
                SaveHistory(history);
                TJLog.Log($"[TJGeneratorsHistoryManager] 已将历史记录 assetGuid 自 {oldGuid} 迁移至 {newGuid}");
            }
        }
    }
}
#endif
