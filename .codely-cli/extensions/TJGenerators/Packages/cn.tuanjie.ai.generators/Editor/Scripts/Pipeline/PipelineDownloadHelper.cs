#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using TJGenerators.Utils;

namespace TJGenerators.Pipeline
{
    /// <summary>
    /// 管线内统一的下载与本地路径辅助（UWR 等待、建目录、<c>file://</c> 预览回退）。
    /// </summary>
    internal static class PipelineDownloadHelper
    {
        /// <summary>
        /// 在 <paramref name="uwr"/> 仍处于进行状态时忙等，直到完成或超时。
        /// 使用精确的 timeSinceStartup 计时，每 0.5 s 检查一次。
        /// </summary>
        public static IEnumerator WaitForWebRequest(UnityWebRequest uwr, float timeout)
        {
            float timeElapsed = 0f;
            const float interval = 0.5f;
            while (UnityWebRequestCompat.IsInProgress(uwr) && timeElapsed < timeout)
            {
                double startWait = EditorApplication.timeSinceStartup;
                while (EditorApplication.timeSinceStartup - startWait < interval)
                    yield return null;
                timeElapsed += interval;
            }
        }

        /// <summary>
        /// 确保资产路径对应的磁盘目录存在（写文件前调用）。
        /// </summary>
        public static void EnsureDirectoryForAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            string absoluteSavePath = PathUtils.ToAbsoluteAssetPath(assetPath);
            string directory = Path.GetDirectoryName(absoluteSavePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }

        /// <summary>
        /// 当远程预览 URL 不可用时，回退为本地文件的 <c>file://</c> URL。
        /// </summary>
        public static string ResolveLocalFilePreviewUrl(string assetOrAbsolutePath)
        {
            if (string.IsNullOrEmpty(assetOrAbsolutePath)) return null;
            string fullPath = Path.GetFullPath(assetOrAbsolutePath);
            if (File.Exists(fullPath))
                return "file://" + fullPath.Replace('\\', '/');
            return null;
        }

        /// <summary>
        /// 用 UnityWebRequest 下载 URL 到本地资产路径（写盘，不 Import）。
        /// 成功时 <paramref name="onSuccess"/> 收到字节；失败时 <paramref name="onError"/> 收到错误信息。
        /// </summary>
        /// <remarks>入口调用 <see cref="PathUtils.NormalizeRemoteUrl"/>，仅替换 URL 反斜杠，不做 Uri 完整规范化。</remarks>
        public static IEnumerator DownloadUrlToFile(
            string url,
            string savePath,
            float timeout,
            Action<byte[]> onSuccess = null,
            Action<string> onError = null)
        {
            url = PathUtils.NormalizeRemoteUrl(url);
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(savePath))
            {
                onError?.Invoke("Invalid download url or save path");
                yield break;
            }

            EnsureDirectoryForAssetPath(savePath);

            using (UnityWebRequest uwr = UnityWebRequest.Get(url))
            {
                uwr.downloadHandler = new DownloadHandlerBuffer();
                yield return uwr.SendWebRequest();
                yield return WaitForWebRequest(uwr, timeout);

                if (UnityWebRequestCompat.IsSuccess(uwr) && uwr.downloadHandler?.data != null)
                {
                    byte[] data = uwr.downloadHandler.data;
                    File.WriteAllBytes(PathUtils.ToAbsoluteAssetPath(savePath), data);
                    onSuccess?.Invoke(data);
                }
                else
                {
                    onError?.Invoke(uwr.error ?? "Download failed");
                }
            }
        }

        /// <summary>
        /// 下载 URL 为字节（不写盘）。供音频/视频在改扩展名后再写盘使用。
        /// 成功时回调收到的字节可能为空，由调用方校验。
        /// </summary>
        /// <remarks>入口调用 <see cref="PathUtils.NormalizeRemoteUrl"/>，仅替换 URL 反斜杠，不做 Uri 完整规范化。</remarks>
        public static IEnumerator DownloadUrlToBytes(
            string url,
            float timeout,
            Action<byte[]> onSuccess,
            Action<string> onError,
            string friendlyFailMessage = null)
        {
            url = PathUtils.NormalizeRemoteUrl(url);
            using (UnityWebRequest uwr = UnityWebRequest.Get(url))
            {
                uwr.downloadHandler = new DownloadHandlerBuffer();
                yield return uwr.SendWebRequest();
                yield return WaitForWebRequest(uwr, timeout);

                if (UnityWebRequestCompat.IsNotSuccess(uwr))
                {
                    string msg = string.IsNullOrEmpty(friendlyFailMessage)
                        ? (uwr.error ?? "Download failed")
                        : ErrorDialogUtils.GetFriendlyErrorMessage(uwr, friendlyFailMessage);
                    onError?.Invoke(msg);
                    yield break;
                }

                onSuccess?.Invoke(uwr.downloadHandler?.data);
            }
        }
    }
}
#endif
