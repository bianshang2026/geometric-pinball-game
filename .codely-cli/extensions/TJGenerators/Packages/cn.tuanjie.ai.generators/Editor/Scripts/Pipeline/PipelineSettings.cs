#if UNITY_EDITOR
using System;
using TJGenerators.Config;
using UnityEngine;

namespace TJGenerators.Pipeline
{
    /// <summary>
    /// 从 <see cref="GeneratorConfig"/> 读取生成流水线相关设置（输出类型与后处理等）。
    /// </summary>
    public sealed class PipelineSettings
    {
        private static readonly PipelineSettings _default = new PipelineSettings(null);

        /// <summary>当没有具体配置时使用的默认实例（所有属性均返回默认值）。</summary>
        public static PipelineSettings Default => _default;

        private readonly GeneratorConfig _config;

        public PipelineSettings(GeneratorConfig config)
        {
            _config = config;
        }

        public string GetOutputType()
        {
            return !string.IsNullOrEmpty(_config?.outputType) ? _config.outputType : GenerationOutputTypes.Model;
        }

        /// <summary>
        /// 音频在 API/配置中的格式（如 "wav"、"mp3"、"mp4"）。仅 outputType == audio 时有意义。
        /// </summary>
        public string AudioFormat =>
            !string.IsNullOrEmpty(_config?.audioFormat) ? _config.audioFormat : "wav";

        public bool IsThreeDModelOutputType()
        {
            string ot = _config?.outputType;
            if (string.IsNullOrEmpty(ot))
                return true;
            return string.Equals(ot, GenerationOutputTypes.Model, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ot, GenerationOutputTypes.RiggedModel, StringComparison.OrdinalIgnoreCase);
        }

        public bool ShouldShowEnableMotionUi() =>
            IsThreeDModelOutputType() && _config?.postProcessing?.enableMotion == true;

        public float GetModelScale()
        {
            return _config?.postProcessing?.modelScale ?? 1f;
        }

        public Vector3 GetModelRotation()
        {
            var rotation = _config?.postProcessing?.rotation;
            if (rotation != null)
                return rotation.ToVector3();
            return new Vector3(0f, 0f, 0f);
        }

        public bool GetPostProcessingSingleClipLoopAnimatorController()
        {
            return _config?.postProcessing?.singleClipLoopAnimatorController == true;
        }
    }
}
#endif
