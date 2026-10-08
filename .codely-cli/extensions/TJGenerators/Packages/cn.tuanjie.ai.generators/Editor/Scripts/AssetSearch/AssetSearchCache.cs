#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Codely.Newtonsoft.Json.Linq;
using TJGenerators.Utils;
using UnityEditor;
using UnityEngine;

namespace TJGenerators.AssetSearch
{
    [InitializeOnLoad]
    public static class AssetSearchCache
    {
        static AssetSearchCache()
        {
            EditorApplication.quitting += CleanupAll;
        }

        private static string CacheDir => Path.Combine(
            Path.GetDirectoryName(Application.dataPath),
            "Library", "AI.TJGenerators", "AssetSearch");

        /// <summary>
        /// 将搜索结果写入缓存文件，返回生成的 query_id（UUID）。
        /// </summary>
        public static string Write(List<AssetSearchCacheItem> items)
        {
            string queryId = Guid.NewGuid().ToString();
            EnsureDir();

            var itemsArray = new JArray();
            foreach (var item in items ?? new List<AssetSearchCacheItem>())
            {
                var kwArr = new JArray();
                foreach (var kw in item.Keywords ?? new List<string>())
                    kwArr.Add(kw);

                itemsArray.Add(new JObject
                {
                    ["query"]       = item.Query       ?? "",
                    ["asset_id"]    = item.AssetId     ?? "",
                    ["url"]         = item.Url         ?? "",
                    ["prefab_path"] = item.PrefabPath  ?? "",
                    ["name"]        = item.Name        ?? "",
                    ["category"]    = item.Category    ?? "",
                    ["source"]      = item.Source      ?? "",
                    ["score"]       = item.Score,
                    ["description"] = item.Description ?? "",
                    ["preview_url"] = item.PreviewUrl  ?? "",
                    ["prefab_meta"] = item.PrefabMeta  ?? "",
                    ["keywords"]    = kwArr,
                });
            }

            var cacheObj = new JObject
            {
                ["query_id"]       = queryId,
                ["created_at_utc"] = DateTime.UtcNow.ToString("o"),
                ["items"]          = itemsArray,
            };

            string path = Path.Combine(CacheDir, queryId + ".json");
            File.WriteAllText(path, cacheObj.ToString(), Encoding.UTF8);
            TJLog.Log($"[AssetSearchCache] Written query_id={queryId}, items={items?.Count ?? 0}");
            return queryId;
        }

        /// <summary>
        /// 读取缓存；找不到或解析失败返回 null。
        /// </summary>
        public static AssetSearchCacheFile Read(string queryId)
        {
            if (string.IsNullOrEmpty(queryId)) return null;
            string path = Path.Combine(CacheDir, queryId + ".json");
            if (!File.Exists(path)) return null;

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var obj = JObject.Parse(json);

                string createdRaw = obj["created_at_utc"]?.ToString();
                DateTime createdAt = createdRaw != null
                    ? DateTime.Parse(createdRaw, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    : DateTime.UtcNow;

                var file = new AssetSearchCacheFile
                {
                    QueryId      = obj["query_id"]?.ToString() ?? queryId,
                    CreatedAtUtc = createdAt,
                    Items        = new List<AssetSearchCacheItem>(),
                };

                var rawItems = obj["items"] as JArray;
                if (rawItems != null)
                {
                    foreach (var token in rawItems)
                    {
                        var it = token as JObject;
                        if (it == null) continue;

                        var kwList = new List<string>();
                        var kwArr  = it["keywords"] as JArray;
                        if (kwArr != null)
                            foreach (var kw in kwArr)
                                kwList.Add(kw.ToString());

                        file.Items.Add(new AssetSearchCacheItem
                        {
                            Query       = it["query"]?.ToString()                 ?? "",
                            AssetId     = it["asset_id"]?.ToString()              ?? "",
                            Url         = it["url"]?.ToString()                   ?? "",
                            PrefabPath  = it["prefab_path"]?.ToString()           ?? "",
                            Name        = it["name"]?.ToString()                  ?? "",
                            Category    = it["category"]?.ToString()              ?? "",
                            Source      = it["source"]?.ToString()                ?? "",
                            Score       = it["score"]?.ToObject<double>()         ?? 0.0,
                            Description = it["description"]?.ToString()           ?? "",
                            PreviewUrl  = it["preview_url"]?.ToString()           ?? "",
                            PrefabMeta  = it["prefab_meta"]?.ToString()           ?? "",
                            Keywords    = kwList,
                        });
                    }
                }

                return file;
            }
            catch (Exception ex)
            {
                TJLog.LogWarning($"[AssetSearchCache] Failed to read cache for query_id={queryId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 删除指定 query_id 对应的缓存文件（若存在）。
        /// </summary>
        public static void Delete(string queryId)
        {
            if (string.IsNullOrEmpty(queryId)) return;
            string path = Path.Combine(CacheDir, queryId + ".json");
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// 删除超过 maxAgeHours 的缓存文件；在每次资产库搜索时触发。
        /// </summary>
        public static void CleanupExpired(int maxAgeHours = 24)
        {
            if (!Directory.Exists(CacheDir)) return;
            DateTime cutoff = DateTime.UtcNow.AddHours(-maxAgeHours);
            foreach (string file in Directory.GetFiles(CacheDir, "*.json"))
            {
                try
                {
                    if (File.GetCreationTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { /* ignore individual file errors */ }
            }
        }

        /// <summary>
        /// 删除所有缓存文件；Editor 退出时自动调用。
        /// </summary>
        public static void CleanupAll()
        {
            if (!Directory.Exists(CacheDir)) return;
            foreach (string file in Directory.GetFiles(CacheDir, "*.json"))
            {
                try { File.Delete(file); }
                catch { /* ignore */ }
            }
        }

        private static void EnsureDir()
        {
            if (!Directory.Exists(CacheDir))
                Directory.CreateDirectory(CacheDir);
        }
    }
}
#endif
