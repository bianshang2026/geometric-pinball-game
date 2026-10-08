using System;
using System.Collections.Generic;

namespace TJGenerators.Config
{
    /// <summary>
    /// 选择器中的单个选项（用于 TypeSelector / StyleSelector 的 options 列表）。
    /// </summary>
    [Serializable]
    public class SelectorOptionConfig
    {
        public string id;           // 唯一标识符，如 "weapon_melee"
        public string name;         // 显示名称，如 "近战武器"
        public string description;  // 描述文字
        public string iconPath;     // 本地图片路径（可选，相对于 Packages 或 Assets）
        public string iconUrl;      // 远程图片 URL（可选）
        public string category;     // 分类标签（用于筛选）
        public string[] tags;       // 自定义标签
        public string prompt;       // 生成提示词（可选）
        public int order;           // 显示顺序（越小越靠前）
        public bool pinned = false; // 是否置顶
    }

    /// <summary>
    /// 类型选择器配置
    /// </summary>
    [Serializable]
    public class TypeSelectorConfig
    {
        public bool enabled = true;
        public string title = "资产类型";
        public string description = "选择要生成的游戏资产类型";
        public List<SelectorOptionConfig> options;
        public SelectorOptionConfig defaultOption;  // 可为null表示无默认选择
    }

    /// <summary>
    /// 风格选择器配置
    /// </summary>
    [Serializable]
    public class StyleSelectorConfig
    {
        public bool enabled = true;
        public string title = "艺术风格";
        public string description = "选择美术风格";
        public List<SelectorOptionConfig> options;
        public SelectorOptionConfig defaultOption;  // 可为null表示无默认选择
    }

    /// <summary>
    /// 材质模板选择器配置
    /// </summary>
    [Serializable]
    public class MaterialTemplateSelectorConfig
    {
        public bool enabled = true;
        public string title = "材质模板";
        public string description = "选择材质纹理模板";
        public List<MaterialTemplateOptionConfig> options;
    }

    /// <summary>
    /// 材质模板选项配置
    /// </summary>
    [Serializable]
    public class MaterialTemplateOptionConfig
    {
        public string id;           // 唯一标识符，如 "smooth_metal"
        public string name;         // 显示名称，如 "光滑金属"
        public string description;  // 描述文字
        public string category;     // 分类标签（金属、木材、石材等）
        public string prompt;       // 生成提示词
        public string iconPath;     // 本地图片路径（可选）
        public int order;           // 显示顺序（越小越靠前）
    }
}
