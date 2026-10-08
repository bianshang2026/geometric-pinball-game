#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// 下载任务的完整生命周期状态。原 status+phase 双字段合并为单一字段。
    /// </summary>
    public enum DownloadTaskStatus
    {
        Downloading,
        Importing,   // 文件已下载，Unity 正在导入 unitypackage
        Completed,
        Failed,
        Interrupted,
        Skipped,
    }

    /// <summary>
    /// 启动下载请求参数。
    /// </summary>
    public sealed class AssetDownloadRequest
    {
        public string AssetId { get; set; }
        public string Name { get; set; }
        public string PrefabPath { get; set; }
        public string Url { get; set; }
        public string Category { get; set; }
        public string Source { get; set; }
        public string Description { get; set; }
        public string PrefabMeta { get; set; }
        public string Query { get; set; }
        public float Score { get; set; }
        public string Keywords { get; set; }
        public string PreviewUrl { get; set; }
        public string SessionId { get; set; }
        /// <summary>下载完成后是否自动在当前场景中实例化 Prefab。</summary>
        public bool InstantiateInScene { get; set; }
    }

    /// <summary>
    /// StartDownload 的同步返回结果。
    /// Status=Downloading 表示协程已启动；Status=Skipped 表示目录已存在、直接返回。
    /// </summary>
    public sealed class StartDownloadResult
    {
        public string TaskId { get; set; }
        public DownloadTaskStatus Status { get; set; }
        public string PrefabPath { get; set; }
        public string Message { get; set; }

        /// <summary>本次下载是否首次（首次扣积分）；重复下载为 false。</summary>
        public bool IsFirstDownload { get; set; }

        /// <summary>本次实际扣除的积分；重复下载为 0。落账失败时可能为 0 且 BillingError 非空。</summary>
        public int CreditsCharged { get; set; }

        /// <summary>后台落账失败时的错误信息（best-effort，不阻断下载本身）。</summary>
        public string BillingError { get; set; }
    }

    /// <summary>
    /// 下载任务快照。既供 Tracker 内部存储，也作为 Service 对外暴露的类型。
    /// 原 DownloadTaskTracker.DownloadTaskInfo 搬迁至此，字段保持一致；Status/Phase 由字符串改为枚举。
    /// </summary>
    public sealed class DownloadTaskInfo
    {
        public string TaskId { get; set; }
        public string AssetId { get; set; }
        public string AssetName { get; set; }
        public string PackageDirPath { get; set; }
        public string PrefabPath { get; set; }
        public string MetadataPath { get; set; }
        public DownloadTaskStatus Status { get; set; }
        public string Category { get; set; }
        public string Source { get; set; }
        public string Description { get; set; }
        public string PrefabMeta { get; set; }
        public string Query { get; set; }
        public float Score { get; set; }
        public string Keywords { get; set; }
        public string PreviewUrl { get; set; }
        public string DownloadUrl { get; set; }
        public List<string> ImportedFiles { get; set; } = new List<string>();
        public string ErrorMessage { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }

        public int ProgressPercent
        {
            get
            {
                switch (Status)
                {
                    case DownloadTaskStatus.Downloading: return 0;
                    case DownloadTaskStatus.Importing:   return 50;
                    case DownloadTaskStatus.Completed:   return 100;
                    case DownloadTaskStatus.Skipped:     return 100;
                    default:                             return 0;
                }
            }
        }
    }

    /// <summary>
    /// 枚举 ↔ SessionState 字符串的转换助手。保持与重构前 SessionState 中已有记录的向后兼容。
    /// TODO(Step 8): 全部调用方迁入本 asmdef 后改回 internal。
    /// </summary>
    public static class DownloadTaskStatusMap
    {
        public static string ToSerialized(DownloadTaskStatus v)
        {
            switch (v)
            {
                case DownloadTaskStatus.Downloading: return "downloading";
                case DownloadTaskStatus.Importing:   return "importing";
                case DownloadTaskStatus.Completed:   return "completed";
                case DownloadTaskStatus.Failed:      return "failed";
                case DownloadTaskStatus.Interrupted: return "interrupted";
                case DownloadTaskStatus.Skipped:     return "skipped";
                default:                              return "";
            }
        }

        public static DownloadTaskStatus FromSerialized(string s)
        {
            switch (s)
            {
                case "downloading": return DownloadTaskStatus.Downloading;
                case "importing":   return DownloadTaskStatus.Importing;
                case "completed":   return DownloadTaskStatus.Completed;
                case "failed":      return DownloadTaskStatus.Failed;
                case "interrupted": return DownloadTaskStatus.Interrupted;
                case "skipped":     return DownloadTaskStatus.Skipped;
                default:            return DownloadTaskStatus.Downloading;
            }
        }
    }
}
#endif
