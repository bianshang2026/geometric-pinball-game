using UnityEngine;

namespace TJGenerators.Config
{
    /// <summary>
    /// 配置选项加载器 - 供现有 Generator 使用
    /// </summary>
    public static class ConfigOptionsLoader
    {
        /// <summary>
        /// 从配置加载模型缩放
        /// </summary>
        public static float LoadModelScale(string generatorId, float fallback = 1f)
        {
            var genConfig = ConfigManager.GetGeneratorConfig(ConfigType.Generator, generatorId);
            return genConfig?.postProcessing?.modelScale ?? fallback;
        }

        /// <summary>
        /// 从配置加载模型旋转
        /// </summary>
        public static Vector3 LoadModelRotation(string generatorId, Vector3 fallback = default)
        {
            var genConfig = ConfigManager.GetGeneratorConfig(ConfigType.Generator, generatorId);
            var rotation = genConfig?.postProcessing?.rotation;
            if (rotation != null)
            {
                return rotation.ToVector3();
            }
            return fallback == default ? new Vector3(0f, 0f, 0f) : fallback;
        }
    }
}
