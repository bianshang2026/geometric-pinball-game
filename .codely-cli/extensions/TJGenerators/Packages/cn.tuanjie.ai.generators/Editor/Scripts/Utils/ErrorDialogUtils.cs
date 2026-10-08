#if UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.Networking;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 错误提示工具类：将错误输出到控制台（用户友好文案），不再弹窗。
    /// </summary>
    public static class ErrorDialogUtils
    {
        /// <summary>
        /// 友好错误消息结构
        /// </summary>
        public struct FriendlyErrorMessage
        {
            public string Title;
            public string Message;
            public string TechnicalMessage; // 存储原始技术消息用于日志
        }

        /// <summary>
        /// 在控制台输出用户友好的错误信息（不弹窗）。
        /// </summary>
        /// <param name="originalTitle">原始错误标题</param>
        /// <param name="originalMessage">原始错误消息</param>
        /// <param name="logPrefix">日志前缀（用于区分不同模块）</param>
        public static void ShowErrorDialog(string originalTitle, string originalMessage, string logPrefix = "TJGenerators")
        {
            var friendlyError = ConvertToUserFriendlyError(originalTitle, originalMessage);
            TJLog.LogError($"[{logPrefix}] {friendlyError.Title}: {friendlyError.Message}");
        }

        /// <summary>
        /// 在控制台输出用户友好的错误信息（不弹窗），并调用回调。
        /// </summary>
        /// <param name="originalTitle">原始错误标题</param>
        /// <param name="originalMessage">原始错误消息</param>
        /// <param name="onErrorCallback">错误回调函数</param>
        /// <param name="logPrefix">日志前缀</param>
        public static void ShowErrorDialog(string originalTitle, string originalMessage, Action<string> onErrorCallback, string logPrefix = "TJGenerators")
        {
            var friendlyError = ConvertToUserFriendlyError(originalTitle, originalMessage);
            TJLog.LogError($"[{logPrefix}] {friendlyError.Title}: {friendlyError.Message}");
            onErrorCallback?.Invoke(friendlyError.TechnicalMessage);
        }

        /// <summary>
        /// 将后端技术错误转换为用户友好的提示
        /// </summary>
        /// <param name="originalTitle">原始错误标题</param>
        /// <param name="originalMessage">原始错误消息</param>
        /// <returns>友好的错误消息</returns>
        public static FriendlyErrorMessage ConvertToUserFriendlyError(string originalTitle, string originalMessage)
        {
            var result = new FriendlyErrorMessage
            {
                Title = originalTitle,
                Message = originalMessage,
                TechnicalMessage = $"{originalTitle}: {originalMessage}"
            };

            // 绑骨/动画失败（姿态识别等）
            if ((originalMessage.Contains("422") && 
                (originalMessage.Contains("Pose estimation failed") || 
                 originalMessage.Contains("please provide a valid model"))) ||
                 originalMessage.Contains("step 3 rig failed") ||
                 originalMessage.Contains("rig failed"))
            {
                result.Title = TJGeneratorsL10n.L("动画绑定失败");
                result.Message = TJGeneratorsL10n.L("系统无法为您生成的模型绑定动画骨骼，这通常是因为您的提示词描述的不是一个角色。\n\n") +
                               TJGeneratorsL10n.L("❌ 无法制作动画：食物、车辆、建筑物、家具等物品\n") +
                               TJGeneratorsL10n.L("✅ 可以制作动画：人类、动物、机器人等角色\n\n") +
                               TJGeneratorsL10n.L("解决方案：请描述一个有头部、躯干、四肢的角色，而不是物品。");
                return result;
            }

            // 网络超时
            if (originalTitle.Contains("超时") || originalTitle.Contains("Timeout") || 
                originalMessage.Contains("timeout") || originalMessage.Contains("timed out"))
            {
                result.Title = TJGeneratorsL10n.L("网络超时");
                result.Message = TJGeneratorsL10n.L("生成请求超时，可能的原因：\n\n") +
                               TJGeneratorsL10n.L("• 网络连接不稳定\n") +
                               TJGeneratorsL10n.L("• 服务器负载过高\n") +
                               TJGeneratorsL10n.L("• 模型生成时间过长\n\n") +
                               TJGeneratorsL10n.L("建议稍后重试，或检查网络连接。");
                return result;
            }

            // 认证错误
            if (originalMessage.Contains("401") || originalMessage.Contains("unauthorized") ||
                originalMessage.Contains("authentication"))
            {
                result.Title = TJGeneratorsL10n.L("认证失败");
                result.Message = TJGeneratorsL10n.L("API认证失败，请检查：\n\n") +
                               TJGeneratorsL10n.L("• API密钥是否正确\n") +
                               TJGeneratorsL10n.L("• 账户是否有足够的额度\n") +
                               TJGeneratorsL10n.L("• 网络连接是否正常\n\n") +
                               TJGeneratorsL10n.L("请联系管理员检查配置。");
                return result;
            }

            // 模型生成失败
            if (originalMessage.Contains("500") || originalMessage.Contains("502") || 
                originalMessage.Contains("503") || originalMessage.Contains("504"))
            {
                result.Title = TJGeneratorsL10n.L("生成失败");
                result.Message = TJGeneratorsL10n.L("模型生成失败，请稍后重试。\n\n") +
                               TJGeneratorsL10n.L("如果问题持续存在，请联系技术支持。");
                return result;
            }

            // 通用错误处理 - 保持原始消息但添加更友好的标题
            if (originalTitle.Contains("错误") || originalTitle.Contains("Error"))
            {
                result.Title = TJGeneratorsL10n.L("生成失败");
                if (string.IsNullOrWhiteSpace(result.Message) || result.Message.Length < 10)
                {
                    result.Message = TJGeneratorsL10n.L("生成过程中出现错误，请重试。\n\n") +
                                   TJGeneratorsL10n.L("如果问题持续存在，请检查网络连接或联系技术支持。");
                }
            }

            return result;
        }

        /// <summary>
        /// 检查是否为错误对话框（用于判断是否需要标记任务失败）
        /// </summary>
        /// <param name="title">对话框标题</param>
        /// <returns>是否为错误对话框</returns>
        public static bool IsErrorDialog(string title)
        {
            return title.Contains("错误") || title.Contains("Error") || 
                   title.Contains("超时") || title.Contains("Timeout") ||
                   title.Contains("失败") || title.Contains("Failed");
        }

        /// <summary>
        /// UnityWebRequest 失败时的简短用户可读文案（含 403 登录提示）。
        /// 对 HTTP 错误码会附带截断后的响应体，便于定位后端返回的具体原因。
        /// </summary>
        public static string GetFriendlyErrorMessage(UnityWebRequest uwr, string defaultPrefix = null)
        {
            if (defaultPrefix == null)
                defaultPrefix = TJGeneratorsL10n.L("请求失败");

            if (uwr.responseCode == 403)
                return TJGeneratorsL10n.L("登录权限检查失败，请确认编辑器左上角或者Hub内已登录");

            string baseMsg = $"{defaultPrefix}: {uwr.error}";
            string body = uwr.downloadHandler?.text;
            if (string.IsNullOrEmpty(body))
                return baseMsg;

            body = body.Trim();
            if (body.Length == 0)
                return baseMsg;

            const int maxLen = 512;
            if (body.Length > maxLen)
                body = body.Substring(0, maxLen) + "…";

            return $"{baseMsg}\n{body}";
        }
    }
}
#endif