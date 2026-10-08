#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using Codely.Newtonsoft.Json.Linq;
using TJGenerators;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 从任务状态响应中解析图片下载 URL（fal 生图、SeeDream 等）。
    /// 兼容 JsonUtility 反序列化不完整及 imageUrls / image_urls / images[].url 等多种结构。
    /// 返回的 URL 均经 <see cref="PathUtils.NormalizeUrlArray"/> / <see cref="PathUtils.NormalizeRemoteUrl"/> 规范化反斜杠。
    /// </summary>
    public static class TaskStatusOutputUrlHelper
    {
        private static readonly string[] ImageUrlPathCandidates =
        {
            "imageUrls",
            "image_urls",
        };

        /// <summary>
        /// 当 JsonUtility 未填充 <see cref="TJTaskOutputData.imageUrls"/> 时，用 Newtonsoft 从原始 JSON 补全。
        /// </summary>
        public static void PatchImageUrlsFromTaskJson(string taskJson, TJTaskStatusResponse response)
        {
            if (string.IsNullOrEmpty(taskJson) || response == null)
                return;

            if (TryGetImageDownloadUrls(response, null) != null)
                return;

            try
            {
                var root = JObject.Parse(taskJson);
                var urls = CollectUrlsFromToken(
                    root["output"]?["data"]
                );
                if (urls == null || urls.Count == 0)
                    return;

                if (response.output == null)
                    response.output = new TJTaskOutput();
                if (response.output.data == null)
                    response.output.data = new TJTaskOutputData();
                response.output.data.imageUrls = urls.ToArray();
            }
            catch
            {
                // ignored — 保持原响应，由上层报错
            }
        }

        /// <summary>
        /// 解析图片下载 URL 列表；<paramref name="preferredPath"/> 为配置中的 downloadUrlPath（可空）。
        /// </summary>
        public static string[] TryGetImageDownloadUrls(
            TJTaskStatusResponse response,
            string preferredPath
        )
        {
            if (response?.output?.data == null)
                return null;

            var data = response.output.data;

            if (data.imageUrls != null && data.imageUrls.Length > 0)
                return PathUtils.NormalizeUrlArray(data.imageUrls);

            if (data.result?.image_urls != null && data.result.image_urls.Length > 0)
                return PathUtils.NormalizeUrlArray(data.result.image_urls);

            if (data.result != null)
            {
                string fromResultUrls = PathUtils.GetUrlString(data.result, "imageUrls");
                if (!string.IsNullOrEmpty(fromResultUrls))
                    return new[] { fromResultUrls };

                string[] fromResultArray = TryPathAsStringArray(data.result, "imageUrls");
                if (fromResultArray != null && fromResultArray.Length > 0)
                    return fromResultArray;
            }

            var pathsToTry = BuildPathCandidates(preferredPath);
            for (int i = 0; i < pathsToTry.Count; i++)
            {
                string path = pathsToTry[i];
                if (data.result != null)
                {
                    string[] fromResult = TryPathAsStringArray(data.result, path);
                    if (fromResult != null && fromResult.Length > 0)
                        return fromResult;
                }

                string[] fromData = TryPathAsStringArray(data, path);
                if (fromData != null && fromData.Length > 0)
                    return fromData;

                string single = PathUtils.GetUrlString(data, path);
                if (!string.IsNullOrEmpty(single))
                    return new[] { single };
            }

            return null;
        }

        private static List<string> BuildPathCandidates(string preferredPath)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(preferredPath))
                list.Add(preferredPath);
            for (int i = 0; i < ImageUrlPathCandidates.Length; i++)
            {
                string p = ImageUrlPathCandidates[i];
                if (!list.Contains(p))
                    list.Add(p);
            }
            return list;
        }

        private static string[] TryPathAsStringArray(object container, string path)
        {
            object raw = PathUtils.GetRaw(container, path);
            if (raw == null)
                return null;

            if (raw is string s && !string.IsNullOrEmpty(s))
                return PathUtils.NormalizeUrlArray(new[] { s });

            if (raw is Array arr && arr.Length > 0)
            {
                var urls = new string[arr.Length];
                for (int i = 0; i < arr.Length; i++)
                    urls[i] = arr.GetValue(i)?.ToString();
                return PathUtils.NormalizeUrlArray(urls);
            }

            return null;
        }

        private static List<string> CollectUrlsFromToken(JToken dataToken)
        {
            if (dataToken == null || dataToken.Type == JTokenType.Null)
                return null;

            var urls = new List<string>();

            foreach (string key in ImageUrlPathCandidates)
            {
                AppendUrlStrings(urls, dataToken[key]);
            }

            AppendUrlStrings(urls, dataToken["result"]?["imageUrls"]);
            AppendUrlStrings(urls, dataToken["result"]?["image_urls"]);
            AppendUrlsFromImagesArray(urls, dataToken["images"]);
            AppendUrlsFromImagesArray(urls, dataToken["result"]?["images"]);

            return urls.Count > 0 ? urls : null;
        }

        private static void AppendUrlStrings(List<string> urls, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return;

            if (token.Type == JTokenType.String)
            {
                string s = token.ToString();
                if (!string.IsNullOrEmpty(s))
                    urls.Add(PathUtils.NormalizeRemoteUrl(s));
                return;
            }

            if (token is JArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    string s = arr[i]?.Type == JTokenType.String
                        ? arr[i].ToString()
                        : arr[i]?["url"]?.ToString();
                    if (!string.IsNullOrEmpty(s))
                        urls.Add(PathUtils.NormalizeRemoteUrl(s));
                }
            }
        }

        private static void AppendUrlsFromImagesArray(List<string> urls, JToken imagesToken)
        {
            if (!(imagesToken is JArray images))
                return;

            for (int i = 0; i < images.Count; i++)
            {
                string u = images[i]?["url"]?.ToString();
                if (!string.IsNullOrEmpty(u))
                    urls.Add(PathUtils.NormalizeRemoteUrl(u));
            }
        }
    }
}
#endif
