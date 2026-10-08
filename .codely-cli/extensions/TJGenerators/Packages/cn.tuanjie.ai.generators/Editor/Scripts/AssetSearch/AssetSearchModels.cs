#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// 资产搜索请求参数。
    /// </summary>
    public sealed class AssetSearchRequest
    {
        /// <summary>至少一个查询词；CustomTool 入口支持单 query 或批量 queries，此处统一为列表。</summary>
        public List<string> Queries { get; set; } = new List<string>();

        /// <summary>rerank_retrieve_top_k；小于 1 会被规范化为 1。</summary>
        public int RerankTopK { get; set; } = 5;

        /// <summary>search_retrieve_top_k；0 表示由 service 取 max(RerankTopK, 10)。</summary>
        public int SearchTopK { get; set; } = 0;

        /// <summary>filter_by_category；null/空列表则不在请求体中出现。</summary>
        public List<string> FilterByCategory { get; set; }
    }

    /// <summary>
    /// 单条资产搜索结果。
    /// </summary>
    public sealed class AssetSearchItem
    {
        public string AssetId { get; set; }
        public string PrefabPath { get; set; }
        public string Url { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Source { get; set; }
        public List<string> Keywords { get; set; }
        public double? Score { get; set; }
        public string Description { get; set; }
        public string PreviewUrl { get; set; }

        /// <summary>嵌套 <c>prefab_meta</c>（bounds、rotation 等），由 <c>AssetSearchService</c> 解析。</summary>
        public object PrefabMeta { get; set; }

        /// <summary>后端返回但此 POCO 未显式字段的条目原样透传（保留给 CustomTool/未来 UI）。</summary>
        public Dictionary<string, object> ExtraFields { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// 单个 query 对应的结果组。
    /// </summary>
    public sealed class AssetSearchGroup
    {
        public string Query { get; set; }
        public List<AssetSearchItem> Items { get; set; } = new List<AssetSearchItem>();
    }

    /// <summary>
    /// 搜索响应：按 query 分组的结果集合。
    /// </summary>
    public sealed class AssetSearchResponse
    {
        public List<AssetSearchGroup> Groups { get; set; } = new List<AssetSearchGroup>();
        public int TotalItemCount { get; set; }
    }

    /// <summary>
    /// 写入本地搜索缓存的单条记录。包含资产下载所需的全部字段。
    /// </summary>
    public sealed class AssetSearchCacheItem
    {
        public string Query       { get; set; }
        public string AssetId     { get; set; }
        public string Url         { get; set; }
        public string PrefabPath  { get; set; }
        public string Name        { get; set; }
        public string Category    { get; set; }
        public string Source      { get; set; }
        public double Score       { get; set; }
        public List<string> Keywords { get; set; }
        public string Description { get; set; }
        public string PreviewUrl  { get; set; }
        public string PrefabMeta  { get; set; }
    }

    /// <summary>
    /// 本地搜索缓存文件的顶层结构。存储路径：Library/AI.TJGenerators/AssetSearch/{query_id}.json。
    /// </summary>
    public sealed class AssetSearchCacheFile
    {
        public string QueryId        { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public List<AssetSearchCacheItem> Items { get; set; } = new List<AssetSearchCacheItem>();
    }
}
#endif
