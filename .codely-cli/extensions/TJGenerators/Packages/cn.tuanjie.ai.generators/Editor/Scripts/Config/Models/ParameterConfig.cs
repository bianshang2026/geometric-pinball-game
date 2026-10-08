using System;
using System.Collections.Generic;

namespace TJGenerators.Config
{
    /// <summary>
    /// 参数配置
    /// </summary>
    [Serializable]
    public class ParameterConfig
    {
        public string id;
        public string type;
        public string label;
        public string tooltip;
        public List<OptionConfig> options;
        public string defaultValue;
        public float min;
        public float max;
        public string dependsOn;
        public string dependsValue;
        public string apiFieldName;  // 默认字段名
        public string apiFieldNameImage;  // 图片模式字段名（可选）
        public string apiFieldNameMultiview;  // 多视图模式字段名（可选）
        public string valueType;
        public bool allowCustom;  // 为 true 时下拉框支持切换到自定义文本输入模式

        /// <summary>
        /// 根据输入模式获取API字段名
        /// </summary>
        public string GetApiFieldName(string inputMode)
        {
            switch (inputMode)
            {
                case "image":
                    return !string.IsNullOrEmpty(apiFieldNameImage) ? apiFieldNameImage : apiFieldName ?? id;
                case "multiview":
                    return !string.IsNullOrEmpty(apiFieldNameMultiview) ? apiFieldNameMultiview : apiFieldName ?? id;
                default:
                    return apiFieldName ?? id;
            }
        }
    }

    [Serializable]
    public class OptionConfig
    {
        public string value;
        public string label;
        public string description;
    }
}
