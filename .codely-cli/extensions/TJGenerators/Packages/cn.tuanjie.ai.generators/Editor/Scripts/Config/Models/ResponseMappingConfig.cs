using System;

namespace TJGenerators.Config
{
    /// <summary>
    /// API响应字段映射
    /// </summary>
    [Serializable]
    public class ResponseMappingConfig
    {
        public string downloadUrlPath;
        public string downloadUrlPathMultiview;
        public string previewUrlPath;
        public string renderedImagePath;  // 渲染贴图URL路径（用于FBX主贴图）
        public string taskIdPath;
        public string progressPath;
        public string statusPath;
    }
}
