#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using Codely.Newtonsoft.Json.Linq;
using TJGenerators.Utils;
using UnityEditor;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// 下载完成后写入 metadata JSON 到资产目录。
    /// </summary>
    public static class AssetMetadataWriter
    {
        /// <summary>
        /// 将 <paramref name="taskInfo"/> 的元信息与 <paramref name="importedFiles"/> 序列化为 JSON，
        /// 写入 <paramref name="taskInfo"/>.MetadataPath 指向的资源路径。
        /// MetadataPath 为空或父目录创建失败时静默跳过。
        /// </summary>
        public static void Write(DownloadTaskInfo taskInfo, string prefabDest, List<string> importedFiles)
        {
            if (taskInfo == null) return;

            string metadataAssetPath = taskInfo.MetadataPath;
            if (string.IsNullOrEmpty(metadataAssetPath)) return;

            var keywordsArr = ParseKeywordsToArray(taskInfo.Keywords);

            JToken prefabMetaToken = null;
            if (!string.IsNullOrWhiteSpace(taskInfo.PrefabMeta))
            {
                try { prefabMetaToken = JToken.Parse(taskInfo.PrefabMeta); }
                catch { /* 无效 JSON，跳过 */ }
            }

            var metadata = new JObject
            {
                ["asset_id"]       = taskInfo.AssetId     ?? "",
                ["name"]           = taskInfo.AssetName   ?? "",
                ["prefab_path"]    = prefabDest,
                ["query"]          = taskInfo.Query       ?? "",
                ["score"]          = taskInfo.Score,
                ["category"]       = taskInfo.Category    ?? "",
                ["source"]         = taskInfo.Source      ?? "",
                ["description"]    = taskInfo.Description ?? "",
                ["preview_url"]    = taskInfo.PreviewUrl  ?? "",
                ["download_url"]   = taskInfo.DownloadUrl ?? "",
                ["keywords"]       = keywordsArr,
                ["prefab_meta"]    = prefabMetaToken ?? JValue.CreateNull(),
                ["imported_files"] = JArray.FromObject(importedFiles ?? new List<string>()),
            };

            string absMetadataPath = PathUtils.ToAbsoluteAssetPath(metadataAssetPath);
            string parentDir = Path.GetDirectoryName(absMetadataPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            File.WriteAllText(absMetadataPath, metadata.ToString(), Encoding.UTF8);
            AssetDatabase.ImportAsset(metadataAssetPath);
        }

        /// <summary>
        /// 将搜索结果中的 keywords 原始字符串解析为 JArray。
        /// 支持 JSON 数组字符串（首选）与单个字符串（降级兜底）。
        /// </summary>
        public static JArray ParseKeywordsToArray(string keywordsRaw)
        {
            if (string.IsNullOrWhiteSpace(keywordsRaw)) return new JArray();
            try
            {
                return JArray.Parse(keywordsRaw);
            }
            catch
            {
                return new JArray(keywordsRaw);
            }
        }
    }
}
#endif
