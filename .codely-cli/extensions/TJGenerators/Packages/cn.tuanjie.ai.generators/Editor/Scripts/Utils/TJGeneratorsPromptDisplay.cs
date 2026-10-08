#if UNITY_EDITOR
using System;
using TJGenerators.Utils;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 从历史记录或 API 用完整 prompt 中提取仅面向用户展示的短文案。
    /// </summary>
    public static class TJGeneratorsPromptDisplay
    {
        private const string UserSupplementMarker = "【用户补充】";
        private const string UserSupplementMarkerEn = "[User Addition]";
        private const string UserRequirementMarker = "用户需求：";
        private const string MultiRefOrderMarker = "【多参考图提交顺序";

        public static string ExtractUserFacingPrompt(string storedPrompt)
        {
            if (string.IsNullOrWhiteSpace(storedPrompt))
                return "";

            string s = storedPrompt.Trim();

            int idx = s.LastIndexOf(UserSupplementMarker, StringComparison.Ordinal);
            if (idx < 0)
                idx = s.LastIndexOf(UserSupplementMarkerEn, StringComparison.Ordinal);
            if (idx >= 0)
            {
                s = s.Substring(idx + UserSupplementMarker.Length).TrimStart('\r', '\n', ' ', '：', ':');
            }
            else
            {
                idx = s.LastIndexOf(UserRequirementMarker, StringComparison.Ordinal);
                if (idx >= 0)
                    s = s.Substring(idx + UserRequirementMarker.Length).Trim();
            }

            int hintIdx = s.IndexOf(MultiRefOrderMarker, StringComparison.Ordinal);
            if (hintIdx >= 0)
                s = s.Substring(0, hintIdx).TrimEnd();

            s = s.Trim();
            if (s.Length == 0)
                return "";

            // 无用户段标记的旧历史：勿把 instructions 当标题展示
            if (
                s.StartsWith("你是一名专业的", StringComparison.Ordinal)
                || s.StartsWith("通道约束：", StringComparison.Ordinal)
                || s.StartsWith("Channel constraint:", StringComparison.Ordinal)
            )
                return "";

            return s;
        }

        public static string FormatHistoryTileLabel(string userFacingPrompt, int maxChars = 20)
        {
            string text = string.IsNullOrWhiteSpace(userFacingPrompt) ? TJGeneratorsL10n.L("（无提示词）") : userFacingPrompt.Trim();
            if (maxChars > 0 && text.Length > maxChars)
                return text.Substring(0, maxChars - 3) + "...";
            return text;
        }
    }
}
#endif
