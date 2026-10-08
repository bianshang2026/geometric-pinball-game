#if UNITY_EDITOR
using UnityEngine.Networking;

namespace TJGenerators.Utils
{
    /// <summary>
    /// UnityWebRequest 版本兼容层。
    /// Unity 2020.2+ 使用 <see cref="UnityWebRequest.result"/> / <see cref="UnityWebRequest.Result"/>；
    /// 更早版本回退到 isDone / isNetworkError / isHttpError。
    /// 调用方统一走本类，无需在各处写 #if。
    /// </summary>
    public static class UnityWebRequestCompat
    {
        public static bool IsInProgress(UnityWebRequest request)
        {
            if (request == null) return false;
#if UNITY_2020_2_OR_NEWER
            return request.result == UnityWebRequest.Result.InProgress;
#else
            return !request.isDone;
#endif
        }

        public static bool IsSuccess(UnityWebRequest request)
        {
            if (request == null) return false;
#if UNITY_2020_2_OR_NEWER
            return request.result == UnityWebRequest.Result.Success;
#else
            return request.isDone && !request.isNetworkError && !request.isHttpError;
#endif
        }

        /// <summary>
        /// 请求未成功完成（含超时后仍为 InProgress、网络/协议错误等）。
        /// 轮询超时后应使用此方法。
        /// </summary>
        public static bool IsNotSuccess(UnityWebRequest request) => !IsSuccess(request);
    }
}
#endif
