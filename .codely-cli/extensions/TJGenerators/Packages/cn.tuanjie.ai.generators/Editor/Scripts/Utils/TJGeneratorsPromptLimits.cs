#if UNITY_EDITOR
namespace TJGenerators.Utils
{
    /// <summary>
    /// 集中管理各生成器的 prompt 最大字符数，与后端 binding max 对齐。
    /// 返回 0 表示不限制。
    /// </summary>
    public static class TJGeneratorsPromptLimits
    {
        /// <summary>
        /// 根据 generatorId 返回 prompt 最大字符数。
        /// </summary>
        public static int GetMaxLength(string generatorId)
        {
            switch (generatorId)
            {
                // fal.go backend binding max
                case "frontier-game-design": return 4000;
                case "frontier-effect":      return 2000;
                case "sonilo-sfx":           return 500;
                case "sonilo-music":         return 2000;
                case "minimax-tts":          return 10000;
                // client-only limits (backend has no binding max but documents a recommended cap)
                case "tencent-generation":   return 1000;
                case "rodin":                return 1000;
                case "tripo-p1":             return 1024;
                case "huoshan_seedream":           return 1024;
                case "huoshan_seedream_image":     return 1024;
                case "huoshan_seedream_pro":       return 1024;
                case "huoshan_seedream_pro_image": return 1024;
                case "huoshan_seedream_material":  return 1024;
                case "image-layering":             return 1024;
                case "seedream-image-layering":    return 1024;
                default:                     return 0;
            }
        }
    }
}
#endif
