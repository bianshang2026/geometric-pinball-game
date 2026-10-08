#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Codely.Newtonsoft.Json.Linq;
using TJGenerators.Config;
using TJGenerators.Utils;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// 资产搜索服务。对 Codely <c>/api/search/assets</c> 的 POCO 化封装：
    /// 调用方只需构造 <see cref="AssetSearchRequest"/>，拿到 <see cref="AssetSearchResponse"/>，
    /// 无需接触 JObject/JArray 或 HTTP 细节。CustomTool 与未来的 UI 均复用本服务。
    /// </summary>
    public static class AssetSearchService
    {
        private const string SearchEndpoint = "/api/search/assets";

        /// <summary>
        /// 同步执行一次搜索。抛出的异常分两类：
        /// <list type="bullet">
        ///   <item><see cref="InvalidOperationException"/> — 鉴权失败（消息带 <c>AUTH_REQUIRED:</c> 前缀，调用方原样呈现）；</item>
        ///   <item>其它（<c>HttpRequestException</c> 等） — 网络或响应异常，调用方按自身策略处理。</item>
        /// </list>
        /// </summary>
        public static AssetSearchResponse Search(AssetSearchRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Queries == null || request.Queries.Count == 0)
                throw new ArgumentException("At least one query is required", nameof(request));

            int rerankTopK = request.RerankTopK < 1 ? 1 : request.RerankTopK;
            int searchTopK = request.SearchTopK > 0 ? request.SearchTopK : Math.Max(rerankTopK, 10);

            string token = CodelyTokenProvider.GetToken();

            var body = new JObject
            {
                ["query"]                 = JArray.FromObject(request.Queries),
                ["rerank_retrieve_top_k"] = rerankTopK,
                ["search_retrieve_top_k"] = searchTopK,
            };
            if (request.FilterByCategory != null && request.FilterByCategory.Count > 0)
                body["filter_by_category"] = JArray.FromObject(request.FilterByCategory);

            string url = ConfigManager.GetCodelyBaseUrl().TrimEnd('/') + SearchEndpoint;
            string responseBody = CodelyHttpClient.PostJsonSync(url, body.ToString(), token);
            var data = JObject.Parse(responseBody);

            return ParseResponse(data, request.Queries);
        }

        // ---------- 响应解析 ----------

        private static AssetSearchResponse ParseResponse(JObject data, List<string> queries)
        {
            var result = new AssetSearchResponse();
            var rawGroups = data["results"] as JArray;
            if (rawGroups == null) return result;

            // 单 query：后端 results[0].items 为所有 items；保持向后兼容用 queries[0] 作为 group 名。
            // 多 query：results[i].query 由后端提供，直接用。
            foreach (var groupToken in rawGroups)
            {
                var groupObj = groupToken as JObject;
                if (groupObj == null) continue;

                string q = groupObj["query"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(q) && queries.Count == 1)
                    q = queries[0];
                if (string.IsNullOrEmpty(q)) continue;

                var items = FilterItems(groupObj["items"] as JArray);
                if (items.Count == 0) continue;

                result.Groups.Add(new AssetSearchGroup { Query = q, Items = items });
                result.TotalItemCount += items.Count;
            }

            return result;
        }

        private static List<AssetSearchItem> FilterItems(JArray rawItems)
        {
            var list = new List<AssetSearchItem>();
            if (rawItems == null) return list;

            foreach (var token in rawItems)
            {
                var obj = token as JObject;
                if (obj == null) continue;

                string assetId    = obj["asset_id"]?.ToString();
                string prefabPath = obj["prefab_path"]?.ToString();
                string url        = PathUtils.NormalizeRemoteUrl(obj["url"]?.ToString());
                if (string.IsNullOrWhiteSpace(assetId) ||
                    string.IsNullOrWhiteSpace(prefabPath) ||
                    string.IsNullOrWhiteSpace(url))
                    continue;

                var item = new AssetSearchItem
                {
                    AssetId     = assetId,
                    PrefabPath  = prefabPath,
                    Url         = url,
                    Name        = obj["name"]?.ToString(),
                    Category    = obj["category"]?.ToString(),
                    Source      = obj["source"]?.ToString(),
                    Description = obj["description"]?.ToString(),
                    PreviewUrl  = obj["preview_url"]?.ToString(),
                    Score       = obj["score"]?.ToObject<double?>(),
                    Keywords    = ParseKeywords(obj["keywords"]),
                    PrefabMeta  = obj["prefab_meta"] != null && obj["prefab_meta"].Type != JTokenType.Null
                        ? JTokenToPlainObject(obj["prefab_meta"])
                        : null,
                };

                foreach (var prop in obj.Properties())
                {
                    if (IsKnownField(prop.Name)) continue;
                    item.ExtraFields[prop.Name] = JTokenToPlainObject(prop.Value);
                }

                list.Add(item);
            }
            return list;
        }

        private static bool IsKnownField(string name)
        {
            switch (name)
            {
                case "asset_id":
                case "prefab_path":
                case "url":
                case "name":
                case "category":
                case "source":
                case "description":
                case "preview_url":
                case "score":
                case "keywords":
                case "prefab_meta":
                    return true;
                default:
                    return false;
            }
        }

        // 将 JToken 递归转为 POCO（Dictionary/List/基本类型），避免 Codely.Newtonsoft 的 JObject/JArray
        // 直接塞入 CustomTool 返回字典时跨 JSON 实现序列化失败。
        private static object JTokenToPlainObject(JToken token)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return null;
                case JTokenType.Integer: return token.ToObject<long>();
                case JTokenType.Float:   return token.ToObject<double>();
                case JTokenType.Boolean: return token.ToObject<bool>();
                case JTokenType.String:
                case JTokenType.Guid:
                case JTokenType.Uri:
                case JTokenType.TimeSpan:
                case JTokenType.Date:
                    return token.ToString();
                case JTokenType.Array:
                {
                    var list = new List<object>();
                    foreach (var child in (JArray)token)
                        list.Add(JTokenToPlainObject(child));
                    return list;
                }
                case JTokenType.Object:
                {
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in ((JObject)token).Properties())
                        dict[prop.Name] = JTokenToPlainObject(prop.Value);
                    return dict;
                }
                default:
                    return token.ToString();
            }
        }

        private static List<string> ParseKeywords(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token is JArray arr)
            {
                var list = new List<string>(arr.Count);
                foreach (var t in arr)
                {
                    var s = t?.ToString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
                return list;
            }
            var single = token.ToString();
            return string.IsNullOrEmpty(single) ? null : new List<string> { single };
        }
    }
}
#endif
