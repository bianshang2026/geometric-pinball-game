#if UNITY_EDITOR
using System;
using System.IO;
using TJGenerators.Utils;

namespace TJGenerators
{
    /// <summary>
    /// 历史生成记录项
    /// </summary>
    [Serializable]
    public class TJGeneratorsGenerationHistoryItem
    {
        public string modelPath;
        public string prompt;
        /// <summary>
        /// 仅用于历史列表/缩略图标题展示的玩家输入，不含 instructions 等系统文案。
        /// </summary>
        public string userPrompt;
        public string imagePath;
        public long timestamp;
        public string modelVersion;
        public bool isTextToModel;
        public bool isGenerating;
        public string taskId;
        public string assetGuid;
        /// <summary>
        /// 关联资产 GUID（材质生成时通常为贴图 PNG，与 assetGuid 的 Material 成对绑定）。
        /// </summary>
        public string linkedAssetGuid;
        public int progress;

        /// <summary>
        /// 预览图URL（来自API返回的rendered_image等字段）
        /// </summary>
        public string previewImageUrl;

        /// <summary>
        /// Prompt模板ID（用于识别特定类型的生成任务，如地形高度图）
        /// </summary>
        public string promptTemplateId;

        /// <summary>
        /// Agent 会话 ID，用于按 session 分组查询。
        /// </summary>
        public string sessionId;

        public string GetUserFacingPrompt()
        {
            if (!string.IsNullOrWhiteSpace(userPrompt))
                return userPrompt.Trim();
            return TJGeneratorsPromptDisplay.ExtractUserFacingPrompt(prompt);
        }

        public string GetDisplayName()
        {
            if (isGenerating)
            {
                if (progress >= 100)
                    return TJGeneratorsL10n.L("转换中...");
                return progress > 0 ? TJGeneratorsL10n.L("生成中 {0}%", progress) : TJGeneratorsL10n.L("生成中...");
            }
            string facing = GetUserFacingPrompt();
            if (!string.IsNullOrEmpty(facing))
                return TJGeneratorsPromptDisplay.FormatHistoryTileLabel(facing);
            if (!string.IsNullOrEmpty(prompt))
                return TJGeneratorsPromptDisplay.FormatHistoryTileLabel(
                    TJGeneratorsPromptDisplay.ExtractUserFacingPrompt(prompt)
                );
            return Path.GetFileNameWithoutExtension(modelPath);
        }

        public string GetTimeString()
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime;
            return dt.ToString("MM/dd HH:mm");
        }
    }
}
#endif
