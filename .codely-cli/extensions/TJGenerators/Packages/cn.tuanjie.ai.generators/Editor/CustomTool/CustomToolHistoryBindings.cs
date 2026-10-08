using UnityEditor;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// CustomTool 的两阶段提交会调用 <see cref="TJGenerators.Pipeline.GenerationPipeline.StartFromSubmittedTask"/>。
    /// 若 assetGuid 为空，<see cref="TJGenerators.TJGeneratorsHistoryManager.LoadHistoryForAsset"/> 无法把历史挂到
    /// 用户选中的占位资源上。此处将占位 Asset 路径解析为 GUID，与手动 UI 中「当前目标资源」一致。
    /// </summary>
    internal static class CustomToolHistoryBindings
    {
        public static string HistoryGuidFromPlaceholderAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return "";

            string g = AssetDatabase.AssetPathToGUID(assetPath);
            return string.IsNullOrEmpty(g) ? "" : g;
        }
    }
}
