#if UNITY_EDITOR
namespace TJGenerators.Pipeline
{
    /// <summary>
    /// 由 CustomTool 等 Host 实现：指定 3D 模型下载的固定 Assets 路径，跳过 History 槽位分配。
    /// </summary>
    public interface IModelDownloadPathProvider
    {
        /// <param name="resolvedSavePath">Pipeline 根据 URL 扩展名解析后的保存路径（可只取其扩展名）。</param>
        /// <returns>非空则直接写入该 Assets 相对路径；null/empty 则走默认 History 逻辑。</returns>
        string GetModelDownloadPath(string resolvedSavePath);
    }
}
#endif
