#if UNITY_EDITOR
using System;
using Codely.Newtonsoft.Json.Linq;
using TJGenerators.Config;
using Unity.UniAsset.Manager.Editor.InternalBridge;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// Codely access_token 换票与缓存入口。UI / CustomTool 只接触 <see cref="GetToken"/> 与 <see cref="Invalidate"/>。
    /// 鉴权失败时抛 <see cref="InvalidOperationException"/>，消息以 <c>AUTH_REQUIRED:</c> 前缀开头，调用方原样呈现给用户。
    /// </summary>
    public static class CodelyTokenProvider
    {
        private const string TokenExchangeEndpoint = "/auth/exchange-with-unity-token";

        private static string _codelyAccessToken;
        private static DateTime _codelyTokenExpiry = DateTime.MinValue;

        /// <summary>
        /// 获取已换票的 Codely access_token，供外部直接读取缓存（如 UnityConnectSession.GetAccessToken 的 fallback）。
        /// 未换票或缓存过期时返回 null。
        /// </summary>
        internal static string CachedAccessToken
        {
            get
            {
                if (!string.IsNullOrEmpty(_codelyAccessToken) && DateTime.Now < _codelyTokenExpiry)
                    return _codelyAccessToken;
                return null;
            }
        }

        /// <summary>
        /// 获取有效的 Codely access_token。缓存未过期直接复用，过期时用 Unity token 重新换票。
        /// Unity Connect token 不可用时 fallback 读取 ~/.codely-cli/oauth_creds.json 中的 JWT token。
        /// </summary>
        /// <exception cref="InvalidOperationException">AUTH_REQUIRED:* 前缀，调用方原样向用户呈现。</exception>
        public static string GetToken()
        {
            if (!string.IsNullOrEmpty(_codelyAccessToken) && DateTime.Now < _codelyTokenExpiry)
                return _codelyAccessToken;
            return ExchangeUnityToken();
        }

        /// <summary>
        /// 强制清空 token 缓存；UI 提供"重新登录"按钮时调用，下次 <see cref="GetToken"/> 会重新换票。
        /// </summary>
        public static void Invalidate()
        {
            _codelyAccessToken = null;
            _codelyTokenExpiry = DateTime.MinValue;
        }

        private static string ExchangeUnityToken()
        {
            string unityToken = UnityConnectSession.instance.GetRawAccessToken();
            string unityUserId = UnityConnectSession.instance.GetUserId();

            // 原始 Unity Connect token 可用时，走换票流程；换票失败则 fallback
            if (!string.IsNullOrEmpty(unityToken))
            {
                try
                {
                    return DoTokenExchange(unityToken, unityUserId);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[CodelyTokenProvider] Unity token exchange failed: {ex.Message}, trying fallback JWT token...");
                }
            }

            // Fallback: 读取 ~/.codely-cli/oauth_creds.json 中的 JWT token
            string fallbackToken = UnityConnectSession.ReadFallbackToken();
            if (!string.IsNullOrEmpty(fallbackToken))
            {
#if TJGENERATORS_DEBUG
                UnityEngine.Debug.Log($"[CodelyTokenProvider] Using fallback JWT token from ~/.codely-cli/oauth_creds.json, token: {fallbackToken}");
                UnityEngine.Debug.Log($"[CodelyTokenProvider] Authorization header: Bearer {fallbackToken}");
#endif
                _codelyAccessToken = fallbackToken;
                _codelyTokenExpiry = DateTime.MaxValue;
                return _codelyAccessToken;
            }

            throw new InvalidOperationException(
                "AUTH_REQUIRED: Unity token is empty and no fallback token found in ~/.codely-cli/oauth_creds.json, please sign in to Unity Editor or run 'codely login'");
        }

        private static string DoTokenExchange(string unityToken, string unityUserId)
        {
            var body = new JObject
            {
                ["unity_access_token"] = unityToken,
                ["unity_user_id"] = unityUserId ?? ""
            };
            string url = ConfigManager.GetCodelyBaseUrl().TrimEnd('/') + TokenExchangeEndpoint;

            string response;
            try
            {
                response = CodelyHttpClient.PostJsonSync(url, body.ToString(), token: "", timeoutSeconds: 10);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"AUTH_REQUIRED: Codely token exchange failed (network error: {ex.Message}). Please sign in to Unity Editor again.");
            }

            JObject data;
            try { data = JObject.Parse(response); }
            catch
            {
                throw new InvalidOperationException(
                    "AUTH_REQUIRED: Codely token exchange returned invalid response. Unity token may be expired, please sign in again.");
            }

            string accessToken = data["access_token"]?.ToString();
            if (string.IsNullOrEmpty(accessToken))
                throw new InvalidOperationException(
                    "AUTH_REQUIRED: Exchange response missing access_token field");

            int expiresIn = data["expires_in"]?.ToObject<int>() ?? 3600;
            _codelyAccessToken = accessToken;
            _codelyTokenExpiry = DateTime.Now.AddSeconds(expiresIn - 60);
            return _codelyAccessToken;
        }
    }
}
#endif
