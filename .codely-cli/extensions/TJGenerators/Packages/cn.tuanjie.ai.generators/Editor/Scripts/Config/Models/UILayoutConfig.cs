using System;
using System.Collections.Generic;

namespace TJGenerators.Config
{
    /// <summary>
    /// UI布局配置
    /// </summary>
    [Serializable]
    public class UILayoutConfig
    {
        public bool showTextInput = true;
        public bool showImageUpload = true;
        public bool showMultiView = false;
        public bool showFileUpload = false;   // 是否显示文件上传组件（用于UniRig等）
        public bool advancedFoldout = true;   // 高级设置是否默认折叠
        public string textInputLabel = "文本提示词";
        public string textInputPlaceholder = "在此处输入文本提示...";
        public string imageUploadLabel = "参考图片";
        public string multiViewLabel = "多视图生成";
        public string multiViewHint = "上传多角度图片生成3D模型，正面必需，至少2张图片";  // 多视图提示
        public string advancedLabel = "高级设置";
        public List<string> primaryParameterIds;  // 在主区域（折叠区外）显示的参数ID
        public string fileUploadLabel = "上传模型文件";  // 文件上传标签
        public string fileUploadHint = "支持 FBX、OBJ 格式的3D模型文件";  // 文件上传提示
        /// <summary>参考图上传上限；0 表示未配置（回退包内默认配置），1 为单图，大于 1 为多图。</summary>
        public int maxReferenceImages = 0;
        /// <summary>多视图模式最少需要上传的图片数</summary>
        public int multiViewMinRequired = 2;
        /// <summary>true 时图片上传为必填项，CanStartGeneration 检查非空</summary>
        public bool imageUploadRequired = false;
    }
}
