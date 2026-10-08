using System;
using System.Collections.Generic;

namespace TJGenerators.Config
{
    /// <summary>
    /// 单个生成器的配置
    /// </summary>
    [Serializable]
    public class GeneratorConfig
    {
        public string id;
        public string displayName;
        public bool enabled = true;
        public string outputType = GenerationOutputTypes.Model;
        public string audioFormat = "wav";   // "wav" | "mp3" | "mp4" … — only relevant when outputType == audio
        public List<EndpointConfig> endpoints;
        public List<ParameterConfig> parameters;
        public PostProcessingConfig postProcessing;
        public ResponseMappingConfig responseMapping;
        public UILayoutConfig uiLayout;  // UI布局配置
        public ModelSelectorConfig modelSelector;
        public TypeSelectorConfig typeSelector;      // 类型选择器配置（Sprite用）
        public StyleSelectorConfig styleSelector;    // 风格选择器配置（Sprite用）
        public MaterialTemplateSelectorConfig materialPresetSelector;  // 材质预设选择器配置（Material用）
        public MaterialTemplateSelectorConfig texturePatternSelector;  // 纹理走势选择器配置（Material用）
        public MaterialTemplateSelectorConfig materialStyleSelector;  // 风格状态选择器配置（Material用）
        public MaterialTemplateSelectorConfig promptTemplateSelector;
        public string imageBase64FieldName; // 有的模型期望配置为"image"，而不是"imageBase64"
        public bool imageBase64AsArray = false; // 是否将imageBase64作为数组发送
        public bool imageBase64WithPrefix = false; // 是否添加 data:image/xxx;base64, 前缀
        public string imageUrlsFieldName; // 图生图时参考图 URL 数组的字段名，如 "imageUrls"
        public string textInputFieldName; // 文本输入字段名，默认为"prompt"，混元Motion等使用"inputText"
        /// <summary>写死在请求体中的字段（不在面板展示），与 referenceImageGenerators.request.fixedFields 同结构。</summary>
        public List<ImageGenFixedField> fixedFields;

        public string GetEndpoint(string key)
        {
            if (endpoints == null) return null;
            var ep = endpoints.Find(e => e.key == key);
            return ep?.value;
        }
    }

    [Serializable]
    public class EndpointConfig
    {
        public string key;
        public string value;
    }
}
