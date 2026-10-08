#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using TJGenerators.Config;

namespace TJGenerators.Generators
{
    /// <summary>
    /// 构建动态 JSON 请求时的只读快照（<see cref="DynamicRequestJsonBuilder"/>）。
    /// </summary>
    public sealed class DynamicRequestBuildContext
    {
        public GeneratorConfig Config { get; }
        public string TextPrompt { get; }
        public SelectorOptionConfig SelectedType { get; }
        public SelectorOptionConfig SelectedStyle { get; }
        public MaterialTemplateOptionConfig SelectedPromptTemplate { get; }
        public string ImagePath { get; }
        public IReadOnlyList<string> ImagePaths { get; }
        public IReadOnlyList<string> MultiViewPaths { get; }
        public int MultiViewCount { get; }
        public string CurrentInputMode { get; }
        public IReadOnlyDictionary<string, object> ParameterValues { get; }
        public IReadOnlyDictionary<string, string> ExtraRawJsonFields { get; }

        public DynamicRequestBuildContext(
            GeneratorConfig config,
            string textPrompt,
            SelectorOptionConfig selectedType,
            SelectorOptionConfig selectedStyle,
            MaterialTemplateOptionConfig selectedPromptTemplate,
            string imagePath,
            IReadOnlyList<string> imagePaths,
            IReadOnlyList<string> multiViewPaths,
            int multiViewCount,
            string currentInputMode,
            IReadOnlyDictionary<string, object> parameterValues,
            IReadOnlyDictionary<string, string> extraRawJsonFields
        )
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            TextPrompt = textPrompt ?? "";
            SelectedType = selectedType;
            SelectedStyle = selectedStyle;
            SelectedPromptTemplate = selectedPromptTemplate;
            ImagePath = imagePath ?? "";
            ImagePaths = imagePaths ?? Array.Empty<string>();
            MultiViewPaths = multiViewPaths ?? Array.Empty<string>();
            MultiViewCount = multiViewCount;
            CurrentInputMode = currentInputMode ?? "text";
            ParameterValues =
                parameterValues ?? new Dictionary<string, object>(StringComparer.Ordinal);
            ExtraRawJsonFields =
                extraRawJsonFields ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 动态请求数据包装
    /// </summary>
    [Serializable]
    public class DynamicRequestData
    {
        public string JsonContent;
    }

    /// <summary>
    /// Multipart文件上传请求数据
    /// </summary>
    public class MultipartRequestData
    {
        public string FilePath;
        public string FileName;
        public string FileFieldName = "file";
        public Dictionary<string, string> AdditionalFields;
    }
}
#endif
