using System;
using System.Collections.Generic;

namespace TJGenerators.Config
{
    /// <summary>
    /// 图片生成器配置
    /// </summary>
    [Serializable]
    public class ImageGeneratorConfig
    {
        public string id;
        public string displayName;
        public bool enabled = true;
        public string endpoint;
        public ImageGenRequestConfig request;
        public ImageGenResponseConfig response;
        public ImageGenPromptsConfig systemPrompts;
    }

    [Serializable]
    public class ImageGenRequestConfig
    {
        public string promptField = "prompt";
        public List<ImageGenFixedField> fixedFields;
        /// <summary>参照图使用可访问的 URL 时写入的 JSON 字段名（火山 SeeDream 为 imageUrls，不是 imagesUrl）。</summary>
        public string referenceImagesField = "imagesUrl";
        /// <summary>
        /// 若配置（非空），多视图链式生成时优先将已下载的本地 PNG 以 base64 数组写入该字段（与 huoshan_seedream的 images 一致），
        /// 避免仅支持站内 URL 的接口收不到外链参考图。
        /// </summary>
        public string referenceImagesBase64Field;
    }

    [Serializable]
    public class ImageGenFixedField
    {
        public string key;
        public string value;
        public string type = "string";  // "string", "bool", "int", "float"
    }

    [Serializable]
    public class ImageGenResponseConfig
    {
        public string statusField = "status";
        public List<string> successValues;
        public string imageUrlPath;    // dot-separated path, e.g. "output.data.image_urls[0]"
        public string errorField = "error";
    }

    [Serializable]
    public class ImageGenPromptsConfig
    {
        public string single;
        public string multiViewFront;
        public string multiViewLeft;
        public string multiViewBack;
        public string multiViewRight;
    }
}
