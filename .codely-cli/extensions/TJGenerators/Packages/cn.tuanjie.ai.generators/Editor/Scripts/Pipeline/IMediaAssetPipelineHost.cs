#if UNITY_EDITOR
using TJGenerators.Generators;

namespace TJGenerators.Pipeline
{
    /// <summary>
    /// 纹理/音频/视频等媒体资产的路径与保存后回调（由 <see cref="GenerationMediaAssetHandlers"/> 使用）。
    /// 不处理此类媒体的 Host 无需实现。
    /// </summary>
    public interface IMediaAssetPipelineHost
    {
        /// <summary>
        /// 获取指定类型媒体资产的保存路径。
        /// 返回 null 表示该 Host 不处理此类媒体。
        /// </summary>
        string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator);

        /// <summary>
        /// 指定类型媒体资产下载保存后的回调（Import Settings、历史刷新、打标签等）。
        /// </summary>
        void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator);
    }
}
#endif
