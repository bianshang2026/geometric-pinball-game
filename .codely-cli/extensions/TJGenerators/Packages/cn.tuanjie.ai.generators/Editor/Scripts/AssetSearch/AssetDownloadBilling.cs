#if UNITY_EDITOR
using Codely.Newtonsoft.Json.Linq;
using TJGenerators.Config;
using Unity.UniAsset.Manager.Editor.InternalBridge;
using TJGenerators.Utils;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// 资产下载落账结果。IsFirstDownload 表示该 asset_id 对本用户是否首次下载；
    /// CreditsCharged 为本次实际扣的积分（重复下载为 0）。失败时 HasError 为 true,
    /// Error 携带可展示信息，调用方应视为 best-effort（不阻断下载本身）。
    /// </summary>
    public sealed class AssetDownloadBillingResult
    {
        public bool   IsFirstDownload { get; set; }
        public int    CreditsCharged  { get; set; }
        public bool   HasError        { get; set; }
        public string Error           { get; set; }
    }

    /// <summary>
    /// Unity 扩展直连 Codely 搜索/下载时的后台落账客户端。
    /// 搜索与下载均直连 Codely（不经后台缓存），因此后台无法像 MCP 那样从缓存命中
    /// query_id + item 来计算首次扣费。这里在真实下载启动前，把资产元数据同步报到
    /// 后台 <c>/api/editor/assets/record-download</c>，后台据此完成首次扣费 + 任务记录
    /// + AssetLibDownloadRecord upsert（重复下载免费，与 MCP 同一套计费逻辑）。
    /// 落账失败不阻断下载，仅记录日志并在 srcLiveResult 中携带错误信息。
    /// </summary>
    public static class AssetDownloadBilling
    {
        private const string RecordEndpoint = "assets/record-download";

        public static AssetDownloadBillingResult Record(AssetDownloadRequest request)
        {
            var result = new AssetDownloadBillingResult();
            if (request == null || string.IsNullOrWhiteSpace(request.AssetId))
            {
                result.HasError = true;
                result.Error    = "Record skipped: missing asset_id";
                return result;
            }

            var body = new JObject
            {
                ["asset_id"]     = request.AssetId ?? "",
                ["name"]         = request.Name     ?? request.AssetId,
                ["prefab_path"]  = request.PrefabPath ?? "",
                ["category"]     = request.Category ?? "",
                ["source"]       = request.Source   ?? "",
                ["query"]        = request.Query    ?? "",
                ["download_url"] = request.Url      ?? "",
            };

            string token = string.Empty;
            try
            {
                token = UnityConnectSession.instance.GetAccessToken();
            }
            catch (System.Exception ex)
            {
                result.HasError = true;
                result.Error    = $"asset billing auth unavailable: {ex.Message}";
                TJLog.LogWarning($"[AssetDownloadBilling] {result.Error}");
                return result;
            }
            if (string.IsNullOrEmpty(token))
            {
                result.HasError = true;
                result.Error    = "asset billing skipped: no access token";
                TJLog.LogWarning($"[AssetDownloadBilling] {result.Error}");
                return result;
            }

            string url = ConfigManager.GetApiBaseUrl().TrimEnd('/') + "/" + RecordEndpoint;

            string response;
            try
            {
                response = CodelyHttpClient.PostJsonSync(url, body.ToString(), token, timeoutSeconds: 15);
            }
            catch (System.Exception ex)
            {
                result.HasError = true;
                result.Error    = $"asset billing failed: {ex.Message}";
                TJLog.LogWarning($"[AssetDownloadBilling] {result.Error} (asset_id={request.AssetId})");
                return result;
            }

            try
            {
                var data = JObject.Parse(response);
                result.IsFirstDownload = data["is_first_download"]?.ToObject<bool?>() ?? false;
                result.CreditsCharged  = data["credits_charged"]?.ToObject<int?>() ?? 0;
                TJLog.Log($"[AssetDownloadBilling] recorded: asset_id={request.AssetId}, first={result.IsFirstDownload}, credits={result.CreditsCharged}");
            }
            catch (System.Exception ex)
            {
                result.HasError = true;
                result.Error    = $"asset billing response parse failed: {ex.Message}";
                TJLog.LogWarning($"[AssetDownloadBilling] {result.Error}");
            }
            return result;
        }
    }
}
#endif