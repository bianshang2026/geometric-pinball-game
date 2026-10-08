#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Codely.Newtonsoft.Json;
using TJGenerators;
using TJGenerators.Config;
using TJGenerators.Utils;

namespace TJGenerators.Generators
{
    internal readonly struct DynamicTaskResponseContext
    {
        public DynamicTaskResponseContext(
            GeneratorConfig config,
            IReadOnlyDictionary<string, object> parameterValues,
            string generatorId,
            string currentInputMode = "text"
        )
        {
            Config = config;
            ParameterValues = parameterValues;
            GeneratorId = generatorId ?? "";
            CurrentInputMode = currentInputMode ?? "text";
        }

        public GeneratorConfig Config { get; }
        public IReadOnlyDictionary<string, object> ParameterValues { get; }
        public string GeneratorId { get; }
        public string CurrentInputMode { get; }
    }

    /// <summary>
    /// 动态生成器任务状态响应中的 URL / 文件名解析（原 DynamicGenerator 响应解析区域）。
    /// </summary>
    internal static class DynamicTaskResponseResolver
    {
        #region Path helpers

        #endregion

        #region Provider fallbacks

        private static string TryResolveRodinDownloadUrl(GeneratorConfig config, object result)
        {
            if (
                result == null
                || !DynamicRequestJsonBuilder.IsRodinGenerator(config)
            )
                return null;

            string url = PathUtils.GetUrlString(result, "base_basic_shaded");
            if (!string.IsNullOrEmpty(url))
            {
                TJLog.Log(
                    "[DynamicGenerator] GetDownloadUrl: Rodin 主路径为空，使用 'base_basic_shaded'"
                );
                return url;
            }

            url = PathUtils.GetUrlString(result, "base");
            if (!string.IsNullOrEmpty(url))
                TJLog.Log("[DynamicGenerator] GetDownloadUrl: Rodin 使用 'base' 作为兜底");
            return string.IsNullOrEmpty(url) ? null : url;
        }

        private static bool IsImageDownloadPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            return string.Equals(path, "imageUrls", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "image_urls", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveDownloadUrlPath(DynamicTaskResponseContext ctx)
        {
            var mapping = ctx.Config.responseMapping;
            if (
                string.Equals(ctx.CurrentInputMode, "multiview", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(mapping?.downloadUrlPathMultiview)
            )
                return mapping.downloadUrlPathMultiview;

            return mapping?.downloadUrlPath ?? "pbr_model";
        }

        #endregion

        public static string GetDownloadUrl(
            DynamicTaskResponseContext ctx,
            TJTaskStatusResponse response
        )
        {
            if (response?.output?.data == null)
            {
                TJLog.Log($"[DynamicGenerator] GetDownloadUrl: output.data 为空");
                return null;
            }

            string defaultPath = ResolveDownloadUrlPath(ctx);
            TJLog.Log($"[DynamicGenerator] GetDownloadUrl: 使用路径 '{defaultPath}'");

            if (IsImageDownloadPath(defaultPath))
            {
                string[] imageUrls = TaskStatusOutputUrlHelper.TryGetImageDownloadUrls(
                    response,
                    defaultPath
                );
                if (imageUrls != null && imageUrls.Length > 0)
                    return imageUrls[0];
            }

            if (
                defaultPath == "audio_url"
                && !string.IsNullOrEmpty(response.output.data.audio_url)
            )
                return PathUtils.NormalizeRemoteUrl(response.output.data.audio_url);
            if (
                defaultPath == "audioUrl"
                && !string.IsNullOrEmpty(response.output.data.audioUrl)
            )
                return PathUtils.NormalizeRemoteUrl(response.output.data.audioUrl);

            if (defaultPath.StartsWith("resultFiles", StringComparison.Ordinal))
            {
                string urlFromData = PathUtils.GetUrlString(response.output.data, defaultPath);
                if (!string.IsNullOrEmpty(urlFromData))
                    return urlFromData;
            }

            if (response.output.data.result == null)
            {
                string flatUrl = PathUtils.GetUrlString(response.output.data, defaultPath);
                if (!string.IsNullOrEmpty(flatUrl))
                    return flatUrl;

                TJLog.Log(
                    $"[DynamicGenerator] GetDownloadUrl: output.data.result 为空, output.data 内容: {JsonConvert.SerializeObject(response.output.data, Formatting.Indented)}"
                );
                return null;
            }

            string url = PathUtils.GetUrlString(response.output.data.result, defaultPath);

            object result = response.output.data.result;

            if (string.IsNullOrEmpty(url))
                url = TryResolveRodinDownloadUrl(ctx.Config, result);

            if (string.IsNullOrEmpty(url))
            {
                TJLog.Log(
                    $"[DynamicGenerator] GetDownloadUrl: 路径 '{defaultPath}' 未找到URL, result 内容: {JsonConvert.SerializeObject(response.output.data.result, Formatting.Indented)}"
                );
            }

            return url;
        }

        public static string[] GetDownloadUrls(
            DynamicTaskResponseContext ctx,
            TJTaskStatusResponse response
        )
        {
            if (response?.output?.data == null)
                return null;

            string defaultPath = ResolveDownloadUrlPath(ctx);

            if (IsImageDownloadPath(defaultPath))
            {
                string[] imageUrls = TaskStatusOutputUrlHelper.TryGetImageDownloadUrls(
                    response,
                    defaultPath
                );
                if (imageUrls != null && imageUrls.Length > 0)
                    return imageUrls;
            }

            object raw = null;
            if (response.output.data.result != null)
            {
                raw = PathUtils.GetRaw(response.output.data.result, defaultPath);
            }

            if (raw == null)
            {
                raw = PathUtils.GetRaw(response.output.data, defaultPath);
            }

            if (raw is Array arr && arr.Length > 0)
            {
                var urls = new string[arr.Length];
                for (int i = 0; i < arr.Length; i++)
                    urls[i] = arr.GetValue(i)?.ToString();
                return PathUtils.NormalizeUrlArray(urls);
            }

            if (raw is string singleStr && !string.IsNullOrEmpty(singleStr))
                return PathUtils.NormalizeUrlArray(new[] { singleStr });
            return null;
        }

        public static string GetPreviewImageUrl(
            DynamicTaskResponseContext ctx,
            TJTaskStatusResponse response
        )
        {
            if (response?.output?.data == null)
                return null;

            string path = ctx.Config.responseMapping?.previewUrlPath ?? "generated_image";

            if (path != null && path.StartsWith("resultFiles", StringComparison.Ordinal))
            {
                string urlFromData = PathUtils.GetUrlString(response.output.data, path);
                if (!string.IsNullOrEmpty(urlFromData))
                    return urlFromData;
            }

            if (path != null && path.StartsWith("assets.", StringComparison.Ordinal))
            {
                string urlFromAssets = PathUtils.GetUrlString(response.output.data, path);
                if (!string.IsNullOrEmpty(urlFromAssets))
                    return urlFromAssets;
            }

            if (response.output.data.result == null)
                return null;

            return PathUtils.GetUrlString(response.output.data.result, path);
        }

        public static string GetRenderedImageUrl(
            DynamicTaskResponseContext ctx,
            TJTaskStatusResponse response
        )
        {
            if (response?.output?.data?.result == null)
                return null;

            string path = ctx.Config.responseMapping?.renderedImagePath;
            if (string.IsNullOrEmpty(path))
                return null;

            return PathUtils.GetUrlString(response.output.data.result, path);
        }

        public static string GetModelFileName(DynamicTaskResponseContext ctx)
        {
            string ext = "fbx";
            if (
                ctx.ParameterValues != null
                && ctx.ParameterValues.TryGetValue("geometryFormat", out object geoFormat)
            )
            {
                ext = geoFormat?.ToString()?.ToLower() ?? "fbx";
            }
            return $"{ctx.Config.id}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}";
        }
    }
}
#endif
