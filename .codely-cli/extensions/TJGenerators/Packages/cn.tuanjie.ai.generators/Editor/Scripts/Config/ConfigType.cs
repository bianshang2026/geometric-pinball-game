namespace TJGenerators.Config
{
    /// <summary>
    /// 配置类型枚举
    /// </summary>
    public enum ConfigType
    {
        /// <summary>
        /// 3D 模型生成配置（用于 Generation 窗口）
        /// </summary>
        Generator,

        /// <summary>
        /// 天空盒生成配置（用于天空盒窗口）
        /// </summary>
        Skybox,

        /// <summary>
        /// Sprite 生成配置（用于 Sprite 窗口）
        /// </summary>
        Sprite,

        /// <summary>
        /// 2D 序列帧（动作）生成配置（用于序列帧窗口）
        /// </summary>
        SpriteSequence,

        /// <summary>
        /// Material 生成配置（用于 Material 窗口）
        /// </summary>
        Material,

        /// <summary>
        /// 文生音频生成配置（用于文生音频窗口）
        /// </summary>
        Music,

        /// <summary>
        /// 通用图片生成配置（用于「生成图片」窗口，对应 GeneratorConfig.json 的 imageGenerators）
        /// </summary>
        Image,

        /// <summary>
        /// 参考图生成配置（用于「AI参考图生成」窗口，对应 GeneratorConfig.json 的 referenceImageGenerators）
        /// </summary>
        ReferenceImage,

        /// <summary>
        /// 视频生成配置（用于「生成视频」窗口，对应 GeneratorConfig.json 的 videoGenerators）
        /// </summary>
        Video,

        /// <summary>
        /// 世界生成配置（用于世界生成窗口，对应 GeneratorConfig.json 的 worldGenerators）
        /// </summary>
        World
    }
}
