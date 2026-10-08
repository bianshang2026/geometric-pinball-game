#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using TJGenerators.Config;
using TJGenerators.Generators;
using TJGenerators.Utils;
using Unity.UniAsset.Manager.Editor.InternalBridge;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace TJGenerators
{
    /// <summary>
    /// Headless AI reference-image generation for <see cref="AIReferenceImageWindow"/> and tests.
    /// </summary>
    public static class AIReferenceImageGeneration
    {
        private static readonly string[] MultiViewLabels = { "Front", "Left", "Back", "Right" };

        private const int ImageGenRequestTimeoutSeconds = 180;
        private const int ImageDownloadTimeoutSeconds = 120;
        private const int RequestBodyLogPreviewMaxChars = 12000;

        public static IEnumerator GenerateSingle(
            string prompt,
            Action<string> onSuccess,
            Action<string> onError,
            string generatorId = null,
            string requestOrigin = null)
        {
            if (TJGeneratorsPlayModeGuard.IsActive)
            {
                onError?.Invoke(TJGeneratorsPlayModeGuard.Message);
                yield break;
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                onError?.Invoke("prompt is required");
                yield break;
            }

            ImageGeneratorConfig config = ResolveConfig(generatorId);
            if (config == null)
            {
                onError?.Invoke("No enabled reference-image generator config (referenceImageGenerators).");
                yield break;
            }

            string token = UnityConnectSession.instance.GetAccessToken();
            if (string.IsNullOrEmpty(token))
            {
                onError?.Invoke("Not logged in — confirm editor / Hub login for access token.");
                yield break;
            }

            string origin = string.IsNullOrEmpty(requestOrigin)
                ? GenerationRequestOrigin.Agent
                : requestOrigin;

            string fullPrompt = (config.systemPrompts?.single ?? "") + prompt;
            bool done = false;
            bool failed = false;
            string failMessage = null;
            string resultPath = null;

            yield return CallImageGenApi(
                config,
                fullPrompt,
                null,
                null,
                origin,
                (path, tex, imageUrl) =>
                {
                    resultPath = path;
                    done = true;
                    if (tex != null)
                        UnityEngine.Object.DestroyImmediate(tex);
                },
                err =>
                {
                    failed = true;
                    failMessage = err;
                });

            if (failed)
            {
                onError?.Invoke(failMessage ?? "Image generation failed");
                yield break;
            }

            if (!done || string.IsNullOrEmpty(resultPath))
            {
                onError?.Invoke("Failed to generate single reference image");
                yield break;
            }

            onSuccess?.Invoke(resultPath);
        }

        public static IEnumerator GenerateMultiView(
            string prompt,
            int viewCount,
            Action<string[]> onSuccess,
            Action<string> onError,
            Action<int, int, string> onProgress = null,
            string generatorId = null,
            string requestOrigin = null)
        {
            if (TJGeneratorsPlayModeGuard.IsActive)
            {
                onError?.Invoke(TJGeneratorsPlayModeGuard.Message);
                yield break;
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                onError?.Invoke("prompt is required");
                yield break;
            }

            viewCount = Mathf.Clamp(viewCount, 1, 4);

            ImageGeneratorConfig config = ResolveConfig(generatorId);
            if (config == null)
            {
                onError?.Invoke("No enabled reference-image generator config (referenceImageGenerators).");
                yield break;
            }

            string token = UnityConnectSession.instance.GetAccessToken();
            if (string.IsNullOrEmpty(token))
            {
                onError?.Invoke("Not logged in — confirm editor / Hub login for access token.");
                yield break;
            }

            string origin = string.IsNullOrEmpty(requestOrigin)
                ? GenerationRequestOrigin.Agent
                : requestOrigin;

            var paths = new string[viewCount];
            var generatedImageUrls = new List<string>();
            string frontReferenceUrl = null;
            ImageGenPromptsConfig prompts = config.systemPrompts;
            bool failed = false;
            string failMessage = null;

            for (int i = 0; i < viewCount; i++)
            {
                string label = i < MultiViewLabels.Length ? MultiViewLabels[i] : $"view{i}";
                onProgress?.Invoke(i + 1, viewCount, label);

                string fullPrompt = GetMultiViewPromptPrefix(prompts, i) + prompt;

                if (!TryPrepareSideViewReferences(
                        config, i, frontReferenceUrl, generatedImageUrls, paths,
                        out string[] referenceUrls, out string[] referenceLocalPaths,
                        out string prepareError))
                {
                    onError?.Invoke(prepareError);
                    yield break;
                }

                bool done = false;
                string resultUrl = null;
                string resultPath = null;

                yield return CallImageGenApi(
                    config,
                    fullPrompt,
                    referenceUrls,
                    referenceLocalPaths,
                    origin,
                    (path, tex, imageUrl) =>
                    {
                        resultPath = path;
                        resultUrl = imageUrl;
                        done = true;
                        if (tex != null)
                            UnityEngine.Object.DestroyImmediate(tex);
                    },
                    err =>
                    {
                        failed = true;
                        failMessage = err;
                    });

                if (failed)
                {
                    onError?.Invoke(failMessage ?? "Image generation failed");
                    yield break;
                }

                if (!done || string.IsNullOrEmpty(resultPath))
                {
                    onError?.Invoke($"Failed to generate view '{label}'");
                    yield break;
                }

                if (string.IsNullOrEmpty(resultUrl))
                {
                    onError?.Invoke(i == 0
                        ? "Front view did not return an image URL; cannot align subsequent views."
                        : $"View '{label}' did not return an image URL; multi-view chain aborted.");
                    yield break;
                }

                paths[i] = resultPath;
                generatedImageUrls.Add(resultUrl);
                if (i == 0)
                    frontReferenceUrl = resultUrl;
            }

            onSuccess?.Invoke(paths);
        }

        private static ImageGeneratorConfig ResolveConfig(string generatorId)
        {
            if (!string.IsNullOrEmpty(generatorId))
            {
                var named = ConfigManager.GetReferenceImageGeneratorConfig(generatorId);
                if (named != null && named.enabled)
                    return named;
            }

            var list = ConfigManager.GetReferenceImageGenerators();
            return list != null && list.Count > 0 ? list[0] : null;
        }

        private static string GetMultiViewPromptPrefix(ImageGenPromptsConfig p, int i)
        {
            switch (i)
            {
                case 0: return p?.multiViewFront ?? "";
                case 1: return p?.multiViewLeft ?? "";
                case 2: return p?.multiViewBack ?? "";
                case 3: return p?.multiViewRight ?? "";
                default: return "";
            }
        }

        private static bool TryPrepareSideViewReferences(
            ImageGeneratorConfig config,
            int viewIndex,
            string frontReferenceUrl,
            List<string> generatedImageUrls,
            string[] slotPaths,
            out string[] referenceUrls,
            out string[] referenceLocalPaths,
            out string error)
        {
            referenceUrls = null;
            referenceLocalPaths = null;
            error = null;
            if (viewIndex <= 0)
                return true;

            bool repeatFrontAtEnd = viewIndex >= 2;
            referenceUrls = BuildMultiViewReferenceUrls(frontReferenceUrl, generatedImageUrls, repeatFrontAtEnd);
            referenceLocalPaths = BuildMultiViewReferenceLocalPaths(slotPaths, viewIndex, repeatFrontAtEnd);

            bool preferBase64 = !string.IsNullOrEmpty(config.request?.referenceImagesBase64Field);
            bool okLocal = referenceLocalPaths != null && referenceLocalPaths.Length > 0;
            bool okUrl = referenceUrls != null && referenceUrls.Length > 0;

            if ((preferBase64 && !okLocal && !okUrl) || (!preferBase64 && !okUrl))
            {
                error = "Missing reference images (local or URL) for side/back views.";
                return false;
            }

            return true;
        }

        private static void AppendRepeatedFrontIfNeeded(List<string> list, string front, bool repeatFrontAtEnd, bool allowAppend)
        {
            if (!repeatFrontAtEnd || !allowAppend || list.Count < 2 || list[list.Count - 1] == front)
                return;
            list.Add(front);
        }

        private static string[] BuildMultiViewReferenceUrls(
            string frontRemoteUrl,
            List<string> allPriorRemoteUrls,
            bool repeatFrontAtEnd)
        {
            if (allPriorRemoteUrls == null || allPriorRemoteUrls.Count == 0)
                return null;

            var list = new List<string>();
            if (!string.IsNullOrEmpty(frontRemoteUrl))
                list.Add(frontRemoteUrl);

            foreach (var url in allPriorRemoteUrls)
            {
                if (string.IsNullOrEmpty(url) || list.Contains(url))
                    continue;
                list.Add(url);
            }

            if (list.Count == 0)
                return null;

            AppendRepeatedFrontIfNeeded(list, frontRemoteUrl, repeatFrontAtEnd, !string.IsNullOrEmpty(frontRemoteUrl));
            return list.ToArray();
        }

        private static string[] BuildMultiViewReferenceLocalPaths(
            string[] slotPaths,
            int priorViewCount,
            bool repeatFrontAtEnd)
        {
            if (slotPaths == null || priorViewCount <= 0)
                return null;
            string front = slotPaths[0];
            if (string.IsNullOrEmpty(front) || !File.Exists(front))
                return null;

            var list = new List<string> { front };
            for (int j = 1; j < priorViewCount; j++)
            {
                if (j >= slotPaths.Length)
                    break;
                string p = slotPaths[j];
                if (string.IsNullOrEmpty(p) || !File.Exists(p))
                    continue;
                if (!list.Contains(p))
                    list.Add(p);
            }

            AppendRepeatedFrontIfNeeded(list, front, repeatFrontAtEnd, File.Exists(front));
            return list.Count > 0 ? list.ToArray() : null;
        }

        private static IEnumerator CallImageGenApi(
            ImageGeneratorConfig config,
            string prompt,
            string[] referenceImageUrls,
            string[] referenceImageLocalPaths,
            string requestOrigin,
            Action<string, Texture2D, string> onSuccess,
            Action<string> onFail)
        {
            string apiUrl = ConfigManager.GetApiBaseUrl() + config.endpoint;
            string json = BuildRequestJson(config, prompt, referenceImageUrls, referenceImageLocalPaths);
            TJLog.Log($"[AIReferenceImageGeneration] URL: {apiUrl}");
            if (json.Length > RequestBodyLogPreviewMaxChars)
                TJLog.Log($"[AIReferenceImageGeneration] body omitted (base64), length={json.Length}");
            else
                TJLog.Log($"[AIReferenceImageGeneration] body: {json}");

            byte[] postData = Encoding.UTF8.GetBytes(json);

            using (UnityWebRequest uwr = new UnityWebRequest(apiUrl, "POST"))
            {
                uwr.uploadHandler = new UploadHandlerRaw(postData);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                uwr.timeout = ImageGenRequestTimeoutSeconds;

                string token = UnityConnectSession.instance.GetAccessToken();
                if (!string.IsNullOrEmpty(token))
                    uwr.SetRequestHeader("Authorization", $"Bearer {token}");
                uwr.SetRequestHeader("orgId", UnityConnectSession.instance.GetOrgId());
                uwr.SetRequestHeader("source", ConfigManager.GetRequestSource());
                uwr.SetRequestHeader(
                    GenerationRequestOrigin.HeaderName,
                    string.IsNullOrEmpty(requestOrigin) ? GenerationRequestOrigin.Agent : requestOrigin);
                var pkgVer = GenerationRequestOrigin.GetPackageVersion();
                if (!string.IsNullOrEmpty(pkgVer))
                    uwr.SetRequestHeader(GenerationRequestOrigin.PackageVersionHeaderName, pkgVer);

                var op = uwr.SendWebRequest();
                float elapsed = 0f;
                foreach (object step in YieldUntilAsyncOperationDone(op, e => elapsed = e))
                    yield return step;

                if (UnityWebRequestCompat.IsSuccess(uwr))
                {
                    yield return HandleResponse(config, uwr.downloadHandler.text, onSuccess, onFail);
                }
                else
                {
                    LogImageGenRequestFailed(uwr, elapsed);
                    onFail?.Invoke(uwr.responseCode == 403
                        ? "Auth failed — confirm editor / Hub login."
                        : $"Request failed: {uwr.error}");
                }
            }
        }

        private static IEnumerable YieldUntilAsyncOperationDone(AsyncOperation op, Action<float> publishElapsed)
        {
            double t0 = EditorApplication.timeSinceStartup;
            while (op != null && !op.isDone)
                yield return null;
            publishElapsed?.Invoke((float)(EditorApplication.timeSinceStartup - t0));
        }

        private static void LogImageGenRequestFailed(UnityWebRequest uwr, float elapsed)
        {
            string suffix = uwr.responseCode == 504
                ? $"HTTP 504 (gateway/upstream timeout; Unity timeout={ImageGenRequestTimeoutSeconds}s), elapsed={elapsed:F1}s"
                : $"HTTP {uwr.responseCode}, elapsed={elapsed:F1}s, timeout={ImageGenRequestTimeoutSeconds}s";
            TJLog.LogError($"[AIReferenceImageGeneration] request failed: {uwr.error}, {suffix}");
            TJLog.LogError($"[AIReferenceImageGeneration] response: {uwr.downloadHandler?.text}");
        }

        private static string BuildRequestJson(
            ImageGeneratorConfig config,
            string prompt,
            string[] referenceImageUrls,
            string[] referenceImageLocalPaths)
        {
            string promptField = config.request?.promptField ?? "prompt";
            var o = new JObject { [promptField] = prompt };
            ParameterJsonWriter.ApplyFixedFields(o, config.request?.fixedFields);
            AppendReferenceImagesToRequest(o, config, referenceImageUrls, referenceImageLocalPaths);
            return o.ToString(Formatting.None);
        }

        private static void AppendReferenceImagesToRequest(
            JObject o,
            ImageGeneratorConfig config,
            string[] referenceImageUrls,
            string[] referenceImageLocalPaths)
        {
            if (TryAppendBase64ReferenceImages(o, config, referenceImageLocalPaths))
                return;
            if (referenceImageUrls == null || referenceImageUrls.Length == 0)
                return;

            string fieldName = config.request?.referenceImagesField ?? "imageUrls";
            var arr = new JArray();
            foreach (string url in referenceImageUrls)
                arr.Add(PathUtils.NormalizeRemoteUrl(url));
            o[fieldName] = arr;
        }

        private static bool TryAppendBase64ReferenceImages(
            JObject o, ImageGeneratorConfig config, string[] referenceImageLocalPaths)
        {
            if (string.IsNullOrEmpty(config.request?.referenceImagesBase64Field)
                || referenceImageLocalPaths == null
                || referenceImageLocalPaths.Length == 0)
                return false;

            var arr = new JArray();
            foreach (string p in referenceImageLocalPaths)
            {
                if (string.IsNullOrEmpty(p) || !File.Exists(p))
                    return false;
                arr.Add(Convert.ToBase64String(File.ReadAllBytes(p)));
            }
            o[config.request.referenceImagesBase64Field] = arr;
            return true;
        }

        private static IEnumerator HandleResponse(
            ImageGeneratorConfig config,
            string responseText,
            Action<string, Texture2D, string> onSuccess,
            Action<string> onFail)
        {
            var resp = config.response;
            string statusField = resp?.statusField ?? "status";
            string statusValue = ExtractJsonValue(responseText, statusField);
            bool ok = resp?.successValues != null
                ? resp.successValues.Contains(statusValue)
                : statusValue == "success" || statusValue == "completed";

            if (!ok)
            {
                string errorField = resp?.errorField ?? "error";
                string err = ExtractJsonValue(responseText, errorField);
                onFail?.Invoke(string.IsNullOrEmpty(err) ? "Unknown generation error" : err);
                yield break;
            }

            string imageUrlPath = resp?.imageUrlPath ?? "output.data.image_urls[0]";
            string imageUrl = PathUtils.NormalizeRemoteUrl(ExtractJsonValue(responseText, imageUrlPath));
            if (string.IsNullOrEmpty(imageUrl))
            {
                onFail?.Invoke($"Cannot extract image URL from path '{imageUrlPath}'");
                yield break;
            }

            yield return DownloadImage(imageUrl, onSuccess, onFail);
        }

        private static IEnumerator DownloadImage(
            string imageUrl,
            Action<string, Texture2D, string> onSuccess,
            Action<string> onFail)
        {
            imageUrl = PathUtils.NormalizeRemoteUrl(imageUrl);
            using (UnityWebRequest uwr = UnityWebRequestTexture.GetTexture(imageUrl))
            {
                uwr.timeout = ImageDownloadTimeoutSeconds;
                var op = uwr.SendWebRequest();
                float elapsed = 0f;
                foreach (object step in YieldUntilAsyncOperationDone(op, e => elapsed = e))
                    yield return step;

                if (!UnityWebRequestCompat.IsSuccess(uwr))
                {
                    TJLog.LogError(
                        $"[AIReferenceImageGeneration] image download failed: {uwr.error}, " +
                        $"HTTP {uwr.responseCode}, elapsed={elapsed:F1}s");
                    onFail?.Invoke(uwr.responseCode == 403
                        ? "Auth failed while downloading image."
                        : $"Image download failed: {uwr.error}");
                    yield break;
                }

                Texture2D texture = DownloadHandlerTexture.GetContent(uwr);
                string tempDir = Path.Combine(Application.temporaryCachePath, "AIImageGen");
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);
                string filePath = Path.Combine(tempDir,
                    $"aigen_{DateTime.Now:yyyyMMdd_HHmmss}_{UnityEngine.Random.Range(0, 9999)}.png");
                File.WriteAllBytes(filePath, texture.EncodeToPNG());
                TJLog.Log($"[AIReferenceImageGeneration] saved: {filePath}");
                onSuccess?.Invoke(filePath, texture, imageUrl);
            }
        }

        private static string ExtractJsonValue(string json, string path)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(path))
                return null;
            try
            {
                var token = JToken.Parse(json).SelectToken(path);
                if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                    return null;
                if (token is JValue jv)
                    return jv.Value == null ? null : jv.Type == JTokenType.String ? (string)jv.Value : jv.ToString();
                return token.ToString(Formatting.None);
            }
            catch (JsonReaderException)
            {
                return null;
            }
        }
    }
}
#endif
