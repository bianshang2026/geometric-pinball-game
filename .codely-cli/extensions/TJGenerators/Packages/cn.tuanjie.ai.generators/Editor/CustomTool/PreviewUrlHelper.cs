#if UNITY_EDITOR
using TJGenerators;
using TJGenerators.Config;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Builds stable preview-image URLs that resolve via the backend API
    /// (/api/external/task/preview-image?taskid=xxx).
    /// These URLs are permanent as long as the task record exists in MongoDB,
    /// unlike the temporary CDN URLs returned by AI providers.
    /// </summary>
    internal static class PreviewUrlHelper
    {
        /// <summary>
        /// Construct a fixed preview-image URL from a backendTaskId.
        /// Returns null if backendTaskId is null/empty.
        /// </summary>
        public static string BuildFixedPreviewUrl(string backendTaskId)
        {
            if (string.IsNullOrEmpty(backendTaskId))
                return null;

            string apiBase = ConfigManager.GetApiBaseUrl();       // e.g. https://ai.tuanjie.cn/api/editor/
            string externalBase = apiBase.Replace("/api/editor/", "/api/external/");
            return $"{externalBase}task/preview-image?taskid={backendTaskId}";
        }

        /// <summary>
        /// Return the best available preview URL:
        ///   1. If a real (CDN) preview URL is available, use it (lower latency).
        ///   2. Otherwise fall back to the fixed backend API URL.
        /// </summary>
        public static string GetPreviewUrl(string realPreviewUrl, string backendTaskId)
        {
            if (!string.IsNullOrEmpty(realPreviewUrl))
                return realPreviewUrl;
            return BuildFixedPreviewUrl(backendTaskId);
        }
    }
}
#endif
