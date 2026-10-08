#if UNITY_EDITOR
using System;
using System.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using TJGenerators;
using Unity.UniAsset.Manager.Editor.InternalBridge;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 用户信息请求通用逻辑
    /// </summary>
    public static class UserInfoHelper
    {
        public static UserInfoResponse LastUserInfo { get; private set; }

        /// <summary>
        /// 请求完整用户信息协程。成功时通过 onComplete 回传 UserInfoResponse，失败时传 null。
        /// </summary>
        public static IEnumerator GetUserInfoDetailCoroutine(string userInfoUrl, Action<UserInfoResponse> onComplete)
        {
            if (string.IsNullOrEmpty(userInfoUrl))
            {
                onComplete?.Invoke(null);
                yield break;
            }

            using (UnityWebRequest uwr = UnityWebRequest.Get(userInfoUrl))
            {
                uwr.downloadHandler = new DownloadHandlerBuffer();

                string token = UnityConnectSession.instance.GetAccessToken();
                if (!string.IsNullOrEmpty(token))
                    uwr.SetRequestHeader("Authorization", $"Bearer {token}");
                uwr.SetRequestHeader("orgId", UnityConnectSession.instance.GetOrgId());
                uwr.SetRequestHeader("source", "codely");

                yield return uwr.SendWebRequest();

                float timeout = 5000f;
                float timeElapsed = 0f;
                float interval = 0.5f;

                while (UnityWebRequestCompat.IsInProgress(uwr) && timeElapsed < timeout)
                {
                    double startWait = EditorApplication.timeSinceStartup;
                    while (EditorApplication.timeSinceStartup - startWait < interval)
                        yield return null;
                    timeElapsed += interval;
                }

                if (UnityWebRequestCompat.IsNotSuccess(uwr) || uwr.downloadHandler == null)
                {
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    string jsonResponse = uwr.downloadHandler.text;
                    UserInfoResponse userInfo = JsonUtility.FromJson<UserInfoResponse>(jsonResponse);
                    if (userInfo != null)
                        LastUserInfo = userInfo;
                    onComplete?.Invoke(userInfo);
                    yield break;
                }
                catch (Exception e)
                {
                    TJLog.LogError($"[UserInfoHelper] Error parsing user info response: {e.Message}");
                }

                onComplete?.Invoke(null);
            }
        }

        /// <summary>
        /// 请求用户信息协程。成功时通过 onComplete 回传当前积分（若有），失败或解析错误时传 null。
        /// 无论积分是否存在，成功解析时都会更新 <see cref="LastUserInfo"/>（含邮箱）。
        /// </summary>
        /// <param name="userInfoUrl">用户信息接口 URL </param>
        /// <param name="onComplete">回调：成功且含积分时为当前积分，否则为 null</param>
        /// <param name="logTag">日志前缀，用于区分调用方（如 "[TJGeneratorsSkybox] GetUserInfo"）</param>
        public static IEnumerator GetUserInfoCoroutine(string userInfoUrl, Action<int?> onComplete)
        {
            yield return GetUserInfoDetailCoroutine(
                userInfoUrl,
                userInfo =>
                {
                    if (userInfo != null && userInfo.credits != null)
                    {
                        onComplete?.Invoke(userInfo.credits.currentCredits);
                        return;
                    }
                    onComplete?.Invoke(null);
                });
        }
    }
}
#endif
