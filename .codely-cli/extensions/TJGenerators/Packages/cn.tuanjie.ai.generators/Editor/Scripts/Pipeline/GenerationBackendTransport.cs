#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using TJGenerators;
using TJGenerators.AssetSearch;
using TJGenerators.Generators;
using TJGenerators.Utils;
using Unity.UniAsset.Manager.Editor.InternalBridge;

namespace TJGenerators.Pipeline
{
    /// <summary>
    /// 创建生成管线使用的后端传输实现（真实 HTTP）。
    /// </summary>
    internal static class GenerationBackendTransportFactory
    {
        /// <param name="fromMethod">请求来源标识（"ui" / "agent"），通过 fromMethod 头上报。</param>
        /// <param name="sessionId">Agent 会话 ID，通过 X-Session-Id 头上报（可为空）。</param>
        public static IGenerationBackendTransport Create(string fromMethod = GenerationRequestOrigin.Agent, string sessionId = "")
            => new ProductionBackendTransport(fromMethod, sessionId);
    }

    /// <summary>
    /// CustomTool 同步提交：与 <see cref="ProductionBackendTransport"/> 相同的 UnityWebRequest + 无 BOM UTF-8 body，
    /// 避免 Unity Mono 下 <c>System.Net.Http.HttpClient</c> 的 Illegal byte sequence 缺陷。
    /// </summary>
    public static class GenerationBackendSyncSubmit
    {
        public const float DefaultTimeoutSeconds = 30f;

        public struct HttpResult
        {
            public bool TimedOut;
            public long ResponseCode;
            public string Body;
            public string Error;
            public bool IsSuccess;
        }

        public static HttpResult PostJson(
            string url,
            byte[] postData,
            string fromMethod = GenerationRequestOrigin.Agent,
            string sessionId = "",
            float timeoutSeconds = DefaultTimeoutSeconds)
        {
            using (UnityWebRequest uwr = new UnityWebRequest(url, "POST"))
            {
                uwr.uploadHandler = new UploadHandlerRaw(postData);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                uwr.timeout = Mathf.CeilToInt(timeoutSeconds);
                ProductionBackendTransport.ApplyAuthHeaders(uwr, fromMethod, sessionId);
                return SendBlocking(uwr, timeoutSeconds);
            }
        }

        public static HttpResult PostMultipart(
            string url,
            MultipartRequestData multipartData,
            string fromMethod = GenerationRequestOrigin.Agent,
            string sessionId = "",
            float timeoutSeconds = DefaultTimeoutSeconds)
        {
            string boundary;
            byte[] postData = ProductionBackendTransport.BuildMultipartBody(multipartData, out boundary);
            using (UnityWebRequest uwr = new UnityWebRequest(url, "POST"))
            {
                uwr.uploadHandler = new UploadHandlerRaw(postData);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", $"multipart/form-data; boundary={boundary}");
                uwr.timeout = Mathf.CeilToInt(timeoutSeconds);
                ProductionBackendTransport.ApplyAuthHeaders(uwr, fromMethod, sessionId);
                return SendBlocking(uwr, timeoutSeconds);
            }
        }

        /// <summary>
        /// 阻塞等待 UnityWebRequest。CustomTool 在主线程同步返回结果时使用；
        /// native 层异步完成请求，无需 yield 编辑器 player loop。
        /// 注意：Thread.Sleep 期间 EditorApplication.timeSinceStartup 不推进，超时用墙钟时间。
        /// </summary>
        private static HttpResult SendBlocking(UnityWebRequest uwr, float timeoutSeconds)
        {
            UnityWebRequestAsyncOperation op = uwr.SendWebRequest();
            var start = DateTime.UtcNow;
            while (op != null && !op.isDone)
            {
                if ((DateTime.UtcNow - start).TotalSeconds > timeoutSeconds)
                {
                    uwr.Abort();
                    return new HttpResult
                    {
                        TimedOut = true,
                        ResponseCode = uwr.responseCode,
                        Body = uwr.downloadHandler != null ? uwr.downloadHandler.text : "",
                        Error = TJGeneratorsL10n.L("请求超时"),
                        IsSuccess = false
                    };
                }
                Thread.Sleep(10);
            }

            return new HttpResult
            {
                TimedOut = false,
                ResponseCode = uwr.responseCode,
                Body = uwr.downloadHandler != null ? uwr.downloadHandler.text : "",
                Error = uwr.error,
                IsSuccess = UnityWebRequestCompat.IsSuccess(uwr)
            };
        }
    }

    /// <summary>
    /// 生成管线与团结后端之间的 HTTP 协程式传输抽象。
    /// </summary>
    internal interface IGenerationBackendTransport
    {
        IEnumerator CreateTask(string url, byte[] postData, Action<TJTaskResponse> onSuccess, Action<string> onError);
        IEnumerator CreateTaskMultipart(string url, MultipartRequestData multipartData, Action<TJTaskResponse> onSuccess, Action<string> onError);
        IEnumerator PollStatus(string taskId, string url, Action<TJTaskStatusResponse> onSuccess, Action<string> onError);
        /// <summary>下载远程 URL 字节；入口对 URL 做反斜杠规范化（<see cref="PathUtils.NormalizeRemoteUrl"/>）。</summary>
        IEnumerator DownloadBytes(string url, Action<byte[]> onSuccess, Action<string> onError);
    }

    internal sealed class ProductionBackendTransport : IGenerationBackendTransport
    {
        private readonly string _fromMethod;
        private readonly string _sessionId;

        public ProductionBackendTransport(string fromMethod = GenerationRequestOrigin.Agent, string sessionId = "")
        {
            _fromMethod = string.IsNullOrEmpty(fromMethod) ? GenerationRequestOrigin.Agent : fromMethod;
            _sessionId = sessionId ?? "";
        }

        internal static void ApplyAuthHeaders(UnityWebRequest uwr, string fromMethod, string sessionId)
        {
            string token = UnityConnectSession.instance.GetAccessToken();
            uwr.SetRequestHeader("Authorization", $"Bearer {token}");
            uwr.SetRequestHeader("orgId", UnityConnectSession.instance.GetOrgId());
            uwr.SetRequestHeader("source", "codely");
            uwr.SetRequestHeader(
                GenerationRequestOrigin.HeaderName,
                string.IsNullOrEmpty(fromMethod) ? GenerationRequestOrigin.Agent : fromMethod);
            string packageVersion = GenerationRequestOrigin.GetPackageVersion();
            if (!string.IsNullOrEmpty(packageVersion))
                uwr.SetRequestHeader(GenerationRequestOrigin.PackageVersionHeaderName, packageVersion);
            if (!string.IsNullOrEmpty(sessionId))
                uwr.SetRequestHeader(GenerationRequestOrigin.SessionIdHeaderName, sessionId);
            string workspaceName = GenerationRequestOrigin.CurrentWorkspaceName;
            if (!string.IsNullOrEmpty(workspaceName))
                uwr.SetRequestHeader(GenerationRequestOrigin.WorkspaceNameHeaderName, workspaceName);
        }

        internal static byte[] BuildMultipartBody(MultipartRequestData multipartData, out string boundary)
        {
            boundary = "----WebKitFormBoundary" + DateTime.Now.Ticks.ToString("x");
            byte[] boundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\n");
            byte[] endBoundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");

            using (var memoryStream = new MemoryStream())
            {
                if (multipartData.AdditionalFields != null)
                {
                    foreach (var field in multipartData.AdditionalFields)
                    {
                        string fieldHeader = $"Content-Disposition: form-data; name=\"{field.Key}\"\r\n\r\n{field.Value}";
                        byte[] fieldBytes = Encoding.UTF8.GetBytes(fieldHeader);
                        memoryStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                        memoryStream.Write(fieldBytes, 0, fieldBytes.Length);
                    }
                }

                if (!string.IsNullOrEmpty(multipartData.FilePath) && File.Exists(multipartData.FilePath))
                {
                    string fileName = multipartData.FileName ?? Path.GetFileName(multipartData.FilePath);
                    string fileHeader =
                        $"Content-Disposition: form-data; name=\"{multipartData.FileFieldName}\"; filename=\"{fileName}\"\r\nContent-Type: application/octet-stream\r\n\r\n";
                    byte[] fileHeaderBytes = Encoding.UTF8.GetBytes(fileHeader);
                    memoryStream.Write(boundaryBytes, 0, boundaryBytes.Length);
                    memoryStream.Write(fileHeaderBytes, 0, fileHeaderBytes.Length);

                    byte[] fileBytes = File.ReadAllBytes(multipartData.FilePath);
                    memoryStream.Write(fileBytes, 0, fileBytes.Length);
                }

                memoryStream.Write(endBoundaryBytes, 0, endBoundaryBytes.Length);
                return memoryStream.ToArray();
            }
        }

        public IEnumerator CreateTask(string url, byte[] postData, Action<TJTaskResponse> onSuccess, Action<string> onError)
        {
            using (UnityWebRequest uwr = new UnityWebRequest(url, "POST"))
            {
                uwr.uploadHandler = new UploadHandlerRaw(postData);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                ApplyAuthHeaders(uwr, _fromMethod, _sessionId);

#if TJGENERATORS_DEBUG
                string requestBody = Encoding.UTF8.GetString(postData);
                string logBody = System.Text.RegularExpressions.Regex.Replace(
                    requestBody,
                    @"""imageBase64""\s*:\s*(?:""[^""]*""|\[[^\]]*\])",
                    "\"imageBase64\":\"(omitted)\"");
                TJLog.Log($"[Transport] POST {url}\n[Transport] 请求体: {logBody}");
#endif

                yield return uwr.SendWebRequest();

                float timeout = 60f;
                float timeElapsed = 0f;
                float interval = 0.5f;

                while (UnityWebRequestCompat.IsInProgress(uwr) && timeElapsed < timeout)
                {
                    double startWait = EditorApplication.timeSinceStartup;
                    while (EditorApplication.timeSinceStartup - startWait < interval)
                        yield return null;
                    timeElapsed += interval;
                }

                if (UnityWebRequestCompat.IsNotSuccess(uwr))
                {
                    onError?.Invoke(ErrorDialogUtils.GetFriendlyErrorMessage(uwr));
                    yield break;
                }

                try
                {
                    string jsonResponse = uwr.downloadHandler.text;
#if TJGENERATORS_DEBUG
                    TJLog.Log($"[Transport] POST {url} 响应: {jsonResponse}");
#else
                    TJLog.Log($"[GenerationPipeline] 响应: {jsonResponse}");
#endif
                    TJTaskResponse response = JsonUtility.FromJson<TJTaskResponse>(jsonResponse);
                    onSuccess?.Invoke(response);
                }
                catch (Exception e)
                {
                    onError?.Invoke(string.Format(TJGeneratorsL10n.L("解析响应失败: {0}"), e.Message));
                }
            }
        }

        public IEnumerator CreateTaskMultipart(string url, MultipartRequestData multipartData, Action<TJTaskResponse> onSuccess, Action<string> onError)
        {
            string boundary;
            byte[] postData = BuildMultipartBody(multipartData, out boundary);

            using (UnityWebRequest uwr = new UnityWebRequest(url, "POST"))
            {
                uwr.uploadHandler = new UploadHandlerRaw(postData);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", $"multipart/form-data; boundary={boundary}");
                ApplyAuthHeaders(uwr, _fromMethod, _sessionId);

                TJLog.Log($"[Transport] POST Multipart {url}");

                yield return uwr.SendWebRequest();

                float timeout = 60f;
                float timeElapsed = 0f;
                float interval = 0.5f;

                while (UnityWebRequestCompat.IsInProgress(uwr) && timeElapsed < timeout)
                {
                    double startWait = EditorApplication.timeSinceStartup;
                    while (EditorApplication.timeSinceStartup - startWait < interval)
                        yield return null;
                    timeElapsed += interval;
                }

                if (UnityWebRequestCompat.IsNotSuccess(uwr))
                {
                    onError?.Invoke(ErrorDialogUtils.GetFriendlyErrorMessage(uwr));
                    yield break;
                }

                try
                {
                    string jsonResponse = uwr.downloadHandler.text;
                    TJLog.Log($"[GenerationPipeline] Multipart响应: {jsonResponse}");
                    TJTaskResponse response = JsonUtility.FromJson<TJTaskResponse>(jsonResponse);
                    onSuccess?.Invoke(response);
                }
                catch (Exception e)
                {
                    onError?.Invoke(string.Format(TJGeneratorsL10n.L("解析响应失败: {0}"), e.Message));
                }
            }
        }

        public IEnumerator PollStatus(string taskId, string url, Action<TJTaskStatusResponse> onSuccess, Action<string> onError)
        {
            string token = null;
            try
            {
                token = UnityConnectSession.instance.GetAccessToken();
            }
            catch (Exception ex)
            {
                onError?.Invoke(string.Format(TJGeneratorsL10n.L("获取认证token失败: {0}"), ex.Message));
                yield break;
            }
            if (string.IsNullOrEmpty(token))
            {
                try
                {
                    token = CodelyTokenProvider.GetToken();
                }
                catch (Exception reAuthEx)
                {
                    onError?.Invoke(string.Format(TJGeneratorsL10n.L("获取认证token失败: {0}"), reAuthEx.Message));
                    yield break;
                }
            }

            UnityWebRequest uwr = UnityWebRequest.Get(url);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            ApplyAuthHeaders(uwr, _fromMethod, _sessionId);

#if TJGENERATORS_DEBUG
            TJLog.Log($"[Transport] GET {url}");
#endif

            yield return uwr.SendWebRequest();

            float requestTimeout = 30f;
            float requestElapsed = 0f;
            while (!uwr.isDone && requestElapsed < requestTimeout)
            {
                requestElapsed += Time.deltaTime;
                yield return null;
            }

            if (!uwr.isDone)
            {
                uwr.Abort();
                uwr.Dispose();
                onError?.Invoke(TJGeneratorsL10n.L("请求超时，任务可能仍在后端运行。重新打开窗口可继续等待。"));
                yield break;
            }

            if (UnityWebRequestCompat.IsNotSuccess(uwr))
            {
                string msg = ErrorDialogUtils.GetFriendlyErrorMessage(uwr);
                uwr.Dispose();
                onError?.Invoke(msg);
                yield break;
            }

            string jsonResponse = uwr.downloadHandler.text;
            uwr.Dispose();
            try
            {
#if TJGENERATORS_DEBUG
                TJLog.Log($"[Transport] GET {url} 响应: {jsonResponse}");
#else
                TJLog.Log($"[GenerationPipeline] 状态响应: {jsonResponse}");
#endif
                var response = JsonUtility.FromJson<TJTaskStatusResponse>(jsonResponse);
                TaskStatusOutputUrlHelper.PatchImageUrlsFromTaskJson(jsonResponse, response);
                onSuccess?.Invoke(response);
            }
            catch (Exception e)
            {
                onError?.Invoke(string.Format(TJGeneratorsL10n.L("解析响应失败: {0}"), e.Message));
            }
        }

        public IEnumerator DownloadBytes(string url, Action<byte[]> onSuccess, Action<string> onError)
        {
            url = PathUtils.NormalizeRemoteUrl(url);
            using (UnityWebRequest uwr = UnityWebRequest.Get(url))
            {
                uwr.downloadHandler = new DownloadHandlerBuffer();
                yield return uwr.SendWebRequest();

                float timeout = 120f;
                float timeElapsed = 0f;
                float interval = 0.5f;

                while (UnityWebRequestCompat.IsInProgress(uwr) && timeElapsed < timeout)
                {
                    double startWait = EditorApplication.timeSinceStartup;
                    while (EditorApplication.timeSinceStartup - startWait < interval)
                        yield return null;
                    timeElapsed += interval;
                }

                if (UnityWebRequestCompat.IsNotSuccess(uwr))
                {
                    onError?.Invoke(ErrorDialogUtils.GetFriendlyErrorMessage(uwr, TJGeneratorsL10n.L("下载失败")));
                    yield break;
                }

                onSuccess?.Invoke(uwr.downloadHandler.data);
            }
        }
    }
}
#endif
