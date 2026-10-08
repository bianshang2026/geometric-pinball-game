#if UNITY_EDITOR
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// Codely 后端同步 POST 客户端。<see cref="PostJsonSync"/> 是 <c>AssetSearchService</c> 与
    /// <c>CodelyTokenProvider</c> 共享的底层 HTTP 原语，签名稳定；token 为空时不附 Authorization 头。
    /// </summary>
    public static class CodelyHttpClient
    {
        // 复用单例避免 socket 耗尽；生命周期与 Editor 进程一致。
        // 不使用 DefaultRequestHeaders（每次请求可能有不同 token，避免跨请求污染）。
        private static readonly HttpClient _client = new HttpClient();

        /// <summary>
        /// 同步 POST JSON；token 非空时添加 <c>Authorization: Bearer {token}</c> 头。
        /// 非 2xx 响应抛 <see cref="HttpRequestException"/>，消息格式 <c>HTTP {code}: {body}</c>。
        /// 超时通过 CancellationTokenSource 控制，不改 _client.Timeout（会影响并发）。
        /// </summary>
        public static string PostJsonSync(string url, string jsonBody, string token, int timeoutSeconds = 20)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(token))
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    HttpResponseMessage response;
                    try
                    {
                        response = _client.SendAsync(req, cts.Token).Result;
                    }
                    catch (AggregateException ae) when (ae.InnerException is TaskCanceledException)
                    {
                        throw new HttpRequestException($"HTTP request timed out after {timeoutSeconds}s: {url}");
                    }

                    using (response)
                    {
                        string body = response.Content.ReadAsStringAsync().Result;
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
                        return body;
                    }
                }
            }
        }
    }
}
#endif
