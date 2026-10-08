#if UNITY_EDITOR
namespace TJGenerators.Config
{
    /// <summary>
    /// <see cref="GeneratorConfig.outputType"/> 与配置 JSON 共用的输出类型常量。
    /// </summary>
    public static class GenerationOutputTypes
    {
        public const string Model = "model";
        public const string RiggedModel = "rigged-model";
        public const string Texture = "texture";
        public const string Cubemap = "cubemap";
        public const string Sprite = "sprite";
        public const string SpriteSequence = "sprite_sequence";
        public const string Material = "material";
        public const string Audio = "audio";
        public const string Image = "image";
        public const string Video = "video";
    }
}
#endif
