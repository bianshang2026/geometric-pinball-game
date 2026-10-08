#if UNITY_EDITOR
using TJGenerators.Generators;

namespace TJGenerators.Pipeline
{
    /// <summary>
    /// 生成流水线媒体资产类型（纹理/音频/视频）。
    /// </summary>
    public enum PipelineMediaType
    {
        Texture,
        Audio,
        Video,
    }

    /// <summary>
    /// 生成流水线生命周期回调宿主（由 <see cref="GenerationPipeline"/> 驱动）。
    /// 媒体路径见 <see cref="IMediaAssetPipelineHost"/>；UI 触发生成见 <see cref="IGenerationTriggerHost"/>。
    /// </summary>
    public interface IGenerationPipelineHost
    {
        TJGeneratorsAssetReference GetTargetAsset();
        void RefreshHistory();

        /// <summary>
        /// 单次生成任务成功完成后的 Host 回调（选中历史项、刷新预览、更新 Tracker 等）。
        /// </summary>
        void OnGenerationCompleted(string assetPath);

        void RefreshUserInfo();
        void Repaint();
        void ShowDialog(string title, string message);
    }
}
#endif
