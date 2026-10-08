#if UNITY_EDITOR
using TJGenerators.Pipeline;
using UnityEditor;
using UnityEngine;

namespace TJGenerators.Utils
{
    /// <summary>
    /// Blocks AI generation while the Editor is in Play mode (or entering it).
    /// Play-mode scene/asset changes are discarded on exit, so generation must run in Edit mode.
    /// </summary>
    public static class TJGeneratorsPlayModeGuard
    {
        public static bool IsActive => EditorApplication.isPlayingOrWillChangePlaymode;

        public static string Message =>
            TJGeneratorsL10n.L(
                "请先退出 Play Mode。\n\nAI 生成只能在编辑模式下使用，Play 模式下的资产变更会在退出后丢失。");

        public static string ShortHint =>
            TJGeneratorsL10n.L("请先退出 Play Mode 再生成");

        public static string SearchShortHint =>
            TJGeneratorsL10n.L("请先退出 Play Mode 再搜索");

        /// <summary>
        /// Unity 在 Play 模式下禁止 <c>AssetDatabase.ImportPackage</c>，下载到项目与放入场景均不可用。
        /// </summary>
        public static string DownloadOrPlaceShortHint =>
            TJGeneratorsL10n.L("请先退出 Play Mode 再下载或放入场景");

        /// <summary>
        /// 资产搜索窗口综合提示：搜索、下载、放置均受限，统一用一条文案。
        /// </summary>
        public static string AssetSearchAllBlockedHint =>
            TJGeneratorsL10n.L("请先退出 Play Mode 再搜索、下载或放入场景");

        /// <summary>退出 Play 模式。</summary>
        public static void ExitPlayMode() => EditorApplication.isPlaying = false;

        /// <summary>
        /// If play mode is active, logs a warning to the console and returns true (caller should abort).
        /// </summary>
        public static bool TryBlock(IGenerationPipelineHost host)
        {
            if (!IsActive)
                return false;

            host?.ShowDialog(TJGeneratorsL10n.L("提示"), Message);
            return true;
        }
    }
}
#endif
