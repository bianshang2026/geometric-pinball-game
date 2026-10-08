#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using TJGenerators;
using TJGenerators.Config;
using TJGenerators.Pipeline;
using TJGenerators.UI;
using TJGenerators.Utils;

namespace TJGenerators.Generators
{
    /// <summary>
    /// 动态生成器 - 完全由配置驱动，无需编写C#代码即可添加新模型
    /// </summary>
    public class DynamicGenerator : ModelGeneratorBase, IGeneratorParameterProvider
    {
        private readonly GeneratorConfig _config;
        private readonly PipelineSettings _pipelineSettings;
        private string _currentEndpointKey = "default";

        // 参数值存储
        private readonly Dictionary<string, object> _parameterValues = new Dictionary<string, object>();
        private readonly Dictionary<string, string> _extraRawJsonFields = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _dropdownIndices = new Dictionary<string, int>();

        // 输入数据
        private string _textPrompt = "";
        private string _historyDisplayPrompt = "";
        private SelectorOptionConfig _selectedType = null;
        private SelectorOptionConfig _selectedStyle = null;
        private MaterialTemplateOptionConfig _selectedPromptTemplate = null;
        private string _imagePath = "";
        private Texture2D _uploadedImage;

        private List<string> _imagePaths = new List<string>();

        // 多视图数据
        private List<string> _multiViewPaths = new List<string>();
        private List<Texture2D> _multiViewImages = new List<Texture2D>();
        private int _multiViewCount = 0;
        private int _multiViewMinRequired = 1;
        private string _currentInputMode = "text"; // text, image, multiview

        private string _primaryInputMode = "textOrImage";

        /// <summary>多视图底部四格说明文案。</summary>
        private static readonly string[] s_multiViewFooterLabels =
        {
            "正面 (必需)",
            "左侧",
            "背面",
            "右侧",
        };

        /// <summary>文件选择对话框标题用词（不含「必需」后缀）。</summary>
        private static readonly string[] s_multiViewPickerTitleLabels =
        {
            "正面",
            "左侧",
            "背面",
            "右侧",
        };

        // UI状态
        private bool _advancedFoldout = false;
        private bool _postProcessingFoldout = false;

        private bool _addMotionEnabled = false;

        private string _motionDescription = "";

        // 文件上传数据（用于UniRig等需要上传文件的生成器）
        private string _uploadedFilePath = "";
        private string _uploadedFileName = "";

        private string _uploadedModelAssetPath = "";

        private List<string> _projectMeshAssetPaths;
        private string[] _projectMeshPopupOptions;
        private int _selectedProjectMeshPopupIndex;

        /// <summary>
        /// 为 true 时下一次刷新会重新扫描 Assets 下的网格列表。
        /// 工程变更时置位，避免在 OnGUI 每帧执行 FindAssets 全库遍历导致内存暴涨/编辑器崩溃。
        /// </summary>
        private static bool s_forceProjectMeshRescan = true;

        static DynamicGenerator()
        {
            EditorApplication.projectChanged += () => s_forceProjectMeshRescan = true;
        }

        public DynamicGenerator(GeneratorConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _pipelineSettings = new PipelineSettings(_config);
            InitializeDefaultValues();
        }

        #region 公开API（供外部调用）

        public void SetTextPrompt(string prompt)
        {
            _textPrompt = prompt ?? "";
            // 仅在没有参考图/多视图输入时才回退到 text 模式，
            // 避免「图生+文字提示」场景下被错误地切回文生端点。
            bool hasMultiView = _multiViewCount > 0
                && _multiViewPaths != null
                && _multiViewPaths.Count > 0
                && !string.IsNullOrEmpty(_multiViewPaths[0]);
            if (!HasReferenceImageInput() && !hasMultiView)
            {
                _currentInputMode = "text";
            }
            UpdateEndpointForInputMode();
        }

        /// <summary>
        /// 设置历史列表展示的短标题（玩家原始输入）；未设置时从 <see cref="GetPrompt"/> 解析。
        /// </summary>
        public void SetHistoryDisplayPrompt(string userPrompt)
        {
            _historyDisplayPrompt = userPrompt ?? "";
        }

        /// <summary>
        /// 设置图片路径（单图模式），内部转为 SetImagePaths 以保证与多图逻辑一致。
        /// </summary>
        public void SetImagePath(string path)
        {
            SetImagePaths(path == null ? null : new[] { path });
        }

        /// <summary>
        /// 设置多图路径（如 Sprite 多图生组图/多图生图，最多 14 张）。null 或空则清空。
        /// </summary>
        public void SetImagePaths(IList<string> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                _imagePaths.Clear();
                _imagePath = "";
                DestroyUploadedImage();
                return;
            }

            DestroyUploadedImage();
            _imagePaths = new List<string>(paths.Count);
            for (int i = 0; i < paths.Count; i++)
            {
                string p = paths[i];
                if (!string.IsNullOrEmpty(p))
                    p = p.Replace('\\', '/');
                _imagePaths.Add(p ?? "");
            }
            _imagePath = _imagePaths[0] ?? "";
            _currentInputMode = "image";
            UpdateEndpointForInputMode();
            if (_imagePaths.Count == 1 && File.Exists(_imagePath))
            {
                _uploadedImage = new Texture2D(2, 2);
                _uploadedImage.LoadImage(File.ReadAllBytes(_imagePath));
            }
            else
            {
                DestroyUploadedImage();
            }
        }

        public void SetTypeSelection(SelectorOptionConfig type)
        {
            _selectedType = type;
        }

        public void SetStyleSelection(SelectorOptionConfig style)
        {
            _selectedStyle = style;
        }

        /// <summary>
        /// 设置文生图提示词模板（outputType 为 image 时，模板 prompt 会拼入 <see cref="DynamicRequestJsonBuilder.BuildEnhancedPrompt"/>）
        /// </summary>
        public void SetPromptTemplateSelection(MaterialTemplateOptionConfig template)
        {
            _selectedPromptTemplate = template;
        }

        /// <summary>当前模板 id（模板特定后处理）。</summary>
        public string GetSelectedPromptTemplateId()
        {
            return _selectedPromptTemplate?.id;
        }

        /// <summary>
        /// 设置多视图图片路径
        /// </summary>
        /// <param name="paths">图片路径数组，顺序为：正面、左侧、背面、右侧</param>
        public void SetMultiViewPaths(string[] paths)
        {
            if (paths == null || paths.Length == 0)
                return;

            DestroyMultiViewImages();
            _multiViewPaths = new List<string>(new string[4]);
            _multiViewImages = new List<Texture2D>(new Texture2D[4]);
            _multiViewCount = 4;
            _multiViewMinRequired = GetMultiViewMinRequired();

            for (int i = 0; i < Math.Min(paths.Length, 4); i++)
            {
                if (!string.IsNullOrEmpty(paths[i]) && File.Exists(paths[i]))
                {
                    _multiViewPaths[i] = paths[i];
                    _multiViewImages[i] = new Texture2D(2, 2);
                    _multiViewImages[i].LoadImage(File.ReadAllBytes(paths[i]));
                }
            }

            _currentInputMode = "multiview";
            UpdateEndpointForInputMode();
        }

        /// <summary>
        /// 以编程方式（非 UI 选择）设置文件上传路径，供 CustomTool 在提交 UniRig 任务时使用。
        /// </summary>
        public void SetFileUploadPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            _uploadedModelAssetPath = assetPath;
            _uploadedFilePath       = PathUtils.ToAbsoluteAssetPath(assetPath);
            _uploadedFileName       = Path.GetFileName(assetPath);
        }

        /// <summary>UI 或 CustomTool 上传的源模型路径（Assets/...），供绑骨后恢复贴图使用。</summary>
        public string GetUploadedModelAssetPath() => _uploadedModelAssetPath;

        public void SetParameter(string parameterId, object value)
        {
            if (string.IsNullOrEmpty(parameterId))
                return;

            string prev = _parameterValues.TryGetValue(parameterId, out var old) ? old?.ToString() : null;
            string next = value?.ToString();
            if (string.Equals(prev, next, StringComparison.Ordinal))
                return;

            _parameterValues[parameterId] = value;

            // 如果是下拉选项，同步更新索引
            var param = _config.parameters?.Find(p => p.id == parameterId);
            if (param?.options != null)
            {
                for (int i = 0; i < param.options.Count; i++)
                {
                    if (param.options[i].value == next)
                    {
                        _dropdownIndices[parameterId] = i;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 设置额外的 JSON 字段（原样拼接到请求体中，value 需为合法 JSON 片段）。
        /// 用于扩展配置外字段，例如 custom gems 结构中的 instructions / knowledge_refs。
        /// </summary>
        public void SetExtraRawJsonField(string fieldName, string rawJsonValue)
        {
            if (string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(rawJsonValue))
                return;

            _extraRawJsonFields[fieldName] = rawJsonValue;
        }

        /// <summary>
        /// 清理所有额外 JSON 字段。
        /// </summary>
        public void ClearExtraRawJsonFields()
        {
            _extraRawJsonFields.Clear();
        }

        public object GetParameter(string parameterId)
        {
            return _parameterValues.TryGetValue(parameterId, out var value) ? value : null;
        }

        #endregion

        #region 基本信息（从配置读取）

        public override string DisplayName => TJGeneratorsL10n.L(_config.displayName ?? _config.id);
        public override string GeneratorId => _config.id;
        public List<ParameterConfig> Parameters => _config.parameters;
        public override string ApiEndpoint
        {
            get
            {
                string endpoint = _config.GetEndpoint(_currentEndpointKey);
                if (!string.IsNullOrEmpty(endpoint))
                    return endpoint;

                endpoint = _config.GetEndpoint(GetEndpointKeyForInputMode(_currentInputMode));
                if (!string.IsNullOrEmpty(endpoint))
                    return endpoint;

                endpoint = _config.GetEndpoint("default");
                if (!string.IsNullOrEmpty(endpoint))
                    return endpoint;

                if (_config.endpoints != null && _config.endpoints.Count > 0)
                    return _config.endpoints[0].value ?? "";

                return "";
            }
        }

        #endregion

        #region 初始化默认值

        private void InitializeDefaultValues()
        {
            if (_config.parameters == null)
                return;

            foreach (var param in _config.parameters)
            {
                if (param.options != null && param.options.Count > 0)
                {
                    // 下拉选项：找到默认值的索引
                    int defaultIndex = 0;
                    if (!string.IsNullOrEmpty(param.defaultValue))
                    {
                        for (int i = 0; i < param.options.Count; i++)
                        {
                            if (param.options[i].value == param.defaultValue)
                            {
                                defaultIndex = i;
                                break;
                            }
                        }
                    }
                    _dropdownIndices[param.id] = defaultIndex;
                    _parameterValues[param.id] = param.options[defaultIndex].value;
                }
                else
                {
                    // 其他类型：使用默认值
                    _parameterValues[param.id] = ParseDefaultValue(param);
                }
            }
        }

        private object ParseDefaultValue(ParameterConfig param)
        {
            if (string.IsNullOrEmpty(param.defaultValue))
            {
                switch (param.type)
                {
                    case "int":   return 0;
                    case "float": return 0f;
                    case "bool":  return false;
                    case "string": return "";
                    default:      return "";
                }
            }

            switch (param.type)
            {
                case "int":
                    return int.TryParse(param.defaultValue, out int i) ? i : 0;
                case "float":
                    return float.TryParse(param.defaultValue, out float f) ? f : 0f;
                case "bool":
                    return param.defaultValue.ToLower() == "true";
                default:
                    return param.defaultValue;
            }
        }

        #endregion

        #region UI绘制

        private static (bool isMultiViewOnly, bool isDualMode) ResolveUILayoutModes(
            UILayoutConfig uiLayout)
        {
            bool isMultiViewOnly =
                uiLayout.showMultiView && !uiLayout.showTextInput && !uiLayout.showImageUpload;
            bool isDualMode =
                (uiLayout.showTextInput || uiLayout.showImageUpload) && uiLayout.showMultiView;
            return (isMultiViewOnly, isDualMode);
        }

        public override void DrawParametersUI(IGenerationPipelineHost context)
        {
            bool is3DMainWindow = context is TJGenerators3DModelWindow;

            var uiLayout = _config.uiLayout ?? new UILayoutConfig();

            var (isMultiViewOnly, isDualMode) = ResolveUILayoutModes(uiLayout);

            if (uiLayout.showFileUpload)
            {
                DrawFileUpload(context);
            }

            if (isDualMode)
            {
                GUILayout.BeginHorizontal();
                UIComponents.DrawSectionTitle(TJGeneratorsL10n.L("生成模式"), uppercase: false);
                GUILayout.EndHorizontal();
                GUILayout.Space(CommonStyles.Space2);
                float rowHeight = 22f;
                int selectedIndex = _primaryInputMode == "multiview" ? 1 : 0;
                string[] modeOptions = new string[]
                {
                    TJGeneratorsL10n.L("文生与图生"),
                    TJGeneratorsL10n.L("多视图生成"),
                };
                int newIndex;
                if (is3DMainWindow)
                {
                    newIndex = UIComponents.DrawStyledDropdown(
                        "3d_model_primary_input_mode_dropdown",
                        selectedIndex,
                        modeOptions,
                        separatorBeforeIndex: -1,
                        panelTopGap: 4f,
                        hoverInset: 2f);
                }
                else
                {
                    Rect dropdownRect = EditorGUILayout.GetControlRect(false, rowHeight);
                    var guiOptions = new GUIContent[] { new GUIContent(modeOptions[0]), new GUIContent(modeOptions[1]) };
                    newIndex = EditorGUI.Popup(dropdownRect, selectedIndex, guiOptions);
                }
                _primaryInputMode = newIndex == 1 ? "multiview" : "textOrImage";
                GUILayout.Space(CommonStyles.Space3);

                if (_primaryInputMode == "textOrImage")
                {
                    DrawTextAndImageInputs(context, uiLayout, CommonStyles.Space3, CommonStyles.Space2);
                }
                else
                {
                    EnsureMultiViewInit();
                    DrawMultiViewHeaderRow(context, uiLayout, CommonStyles.Space3);
                    DrawMultiViewArea(context);
                }
            }
            else if (isMultiViewOnly)
            {
                EnsureMultiViewInit();
                DrawMultiViewHeaderRow(context, uiLayout, CommonStyles.Space3);
                DrawMultiViewArea(context);
            }
            else
            {
                // 仅文生/图生
                DrawTextAndImageInputs(context, uiLayout, CommonStyles.Space3, CommonStyles.Space2);
            }

            // 主参数（折叠区外）
            var primaryParams = GetPrimaryParameters(uiLayout);
            if (primaryParams != null && primaryParams.Count > 0)
            {
                if (is3DMainWindow) GUILayout.Space(CommonStyles.Space3);
                bool drewAny = false;
                foreach (var param in primaryParams)
                {
                    if (param == null)
                        continue;
                    if (!string.IsNullOrEmpty(param.dependsOn))
                    {
                        var depVal = GetParameter(param.dependsOn);
                        if (depVal == null || depVal.ToString() != param.dependsValue)
                            continue;
                    }
                    if (drewAny)
                        GUILayout.Space(CommonStyles.Space2);
                    AdvancedSettingsComponents.DrawAdvancedParameter(this, param);
                    drewAny = true;
                }
            }

            if (is3DMainWindow) GUILayout.Space(CommonStyles.Space3);
            // 高级参数（折叠）
            var advancedParams = GetAdvancedParameters(uiLayout);
            if (advancedParams != null && advancedParams.Count > 0)
            {
                if (!is3DMainWindow)
                    GUILayout.Space(CommonStyles.Space3);
                _advancedFoldout = UIComponents.DrawAdvancedSettingsFoldout(
                    _advancedFoldout,
                    this,
                    advancedParams,
                    TJGeneratorsL10n.L(uiLayout.advancedLabel ?? "高级设置")
                );
            }

            if (ShouldShowEnableMotionUi())
            {
                if (is3DMainWindow) GUILayout.Space(CommonStyles.Space3);
                UIComponents.DrawGapLine();
                if (is3DMainWindow) GUILayout.Space(CommonStyles.Space3);
                if (!is3DMainWindow)
                    GUILayout.Space(CommonStyles.Space3);

                _postProcessingFoldout = UIComponents.DrawSettingsFoldout(
                    _postProcessingFoldout,
                    TJGeneratorsL10n.L("后处理"),
                    () => DrawPostProcessingExpandedContent(is3DMainWindow),
                    uppercaseLabel: false
                );
            }
        }

        public override void DrawActionButton(IGenerationPipelineHost context, LeftPanelBottomDock.Layout layout)
        {
            var uiLayout = _config.uiLayout ?? new UILayoutConfig();
            DrawTopGenerateButton(context, uiLayout, layout);
        }

        private bool ShouldShowEnableMotionUi() => _pipelineSettings.ShouldShowEnableMotionUi();

        private List<ParameterConfig> GetPrimaryParameters(UILayoutConfig uiLayout)
        {
            if (_config.parameters == null || _config.parameters.Count == 0)
                return null;
            if (uiLayout?.primaryParameterIds == null || uiLayout.primaryParameterIds.Count == 0)
                return null;
            var ids = new HashSet<string>(uiLayout.primaryParameterIds);
            var list = new List<ParameterConfig>();
            foreach (var p in _config.parameters)
            {
                if (p != null && ids.Contains(p.id))
                    list.Add(p);
            }
            return list;
        }

        private List<ParameterConfig> GetAdvancedParameters(UILayoutConfig uiLayout)
        {
            if (_config.parameters == null || _config.parameters.Count == 0)
                return null;
            if (uiLayout?.primaryParameterIds == null || uiLayout.primaryParameterIds.Count == 0)
                return _config.parameters;
            var ids = new HashSet<string>(uiLayout.primaryParameterIds);
            var list = new List<ParameterConfig>();
            foreach (var p in _config.parameters)
            {
                if (p != null && !ids.Contains(p.id))
                    list.Add(p);
            }
            return list;
        }

        private void DrawPostProcessingExpandedContent(bool is3DMainWindow)
        {
            if (is3DMainWindow)
            {
                _addMotionEnabled = UIComponents.DrawAdvancedStyleBoolRow(TJGeneratorsL10n.L("添加动作"), _addMotionEnabled);
                if (_addMotionEnabled)
                {
                    GUILayout.Space(CommonStyles.Space2);
                    _motionDescription = UIComponents.DrawAdvancedStyleTextRow(
                        TJGeneratorsL10n.L("动作描述"),
                        _motionDescription ?? string.Empty
                    );
                }
                return;
            }

            GUILayout.BeginHorizontal();
            _addMotionEnabled = EditorGUILayout.Toggle(TJGeneratorsL10n.L("添加动作"), _addMotionEnabled);
            GUILayout.EndHorizontal();

            if (_addMotionEnabled)
            {
                GUILayout.Space(CommonStyles.LineSpacing);
                _motionDescription = UIComponents.DrawTextField(
                    TJGeneratorsL10n.L("动作描述"),
                    TJGeneratorsL10n.L("输入动作，如：walk、run、jump..."),
                    _motionDescription
                );
            }
        }

        private void DrawTextAndImageInputs(
            IGenerationPipelineHost context,
            UILayoutConfig uiLayout,
            float sectionSpacing,
            float titleToControlSpacing
        )
        {
            bool is3DMainWindow = context is TJGenerators3DModelWindow;

            if (is3DMainWindow)
            {
                if (uiLayout.showTextInput)
                {
                    var window = (TJGenerators3DModelWindow)context;
                    string prompt = window.SharedTextPrompt;
                    UIComponents.DrawSectionTitle(TJGeneratorsL10n.L(uiLayout.textInputLabel ?? "文本提示词（可选）"), uppercase: false);
                    GUILayout.Space(titleToControlSpacing);
                    prompt = UIComponents.DrawPromptInputBox(
                        prompt,
                        TJGeneratorsL10n.L(uiLayout.textInputPlaceholder ?? "在此处输入文本提示..."),
                        "generator_main_prompt_input");
                    window.SharedTextPrompt = prompt;
                    _textPrompt = prompt;
                }

                if (uiLayout.showImageUpload)
                {
                    if (uiLayout.showTextInput)
                        GUILayout.Space(sectionSpacing);

                    DrawImageUploadSection(
                        context,
                        uiLayout,
                        titleToControlSpacing,
                        sectionSpacing,
                        useShowcaseStyle: true,
                        addTrailingSectionSpacing: false
                    );
                }
                return;
            }

            if (uiLayout.showTextInput)
            {
                _textPrompt = UIComponents.DrawTextField(
                    TJGeneratorsL10n.L(uiLayout.textInputLabel ?? "文本提示词（可选）"),
                    TJGeneratorsL10n.L(uiLayout.textInputPlaceholder ?? "在此处输入文本提示..."),
                    _textPrompt
                );
                GUILayout.Space(CommonStyles.LineSpacing);
            }

            if (uiLayout.showImageUpload)
            {
                DrawImageUploadSection(
                    context,
                    uiLayout,
                    titleToControlSpacing,
                    sectionSpacing,
                    useShowcaseStyle: false,
                    addTrailingSectionSpacing: true
                );
            }
        }

        private void DrawImageUploadSection(
            IGenerationPipelineHost context,
            UILayoutConfig uiLayout,
            float titleToControlSpacing,
            float sectionSpacing,
            bool useShowcaseStyle,
            bool addTrailingSectionSpacing)
        {
            string title = uiLayout?.imageUploadLabel ?? "参考图片";
            int maxReferenceImages = ConfigManager.ResolveMaxReferenceImages(_config);
            int imageCount = string.IsNullOrEmpty(_imagePath) ? 0 : 1;
            if (_imagePaths != null && _imagePaths.Count > 0)
                imageCount = _imagePaths.Count;
            UIComponents.DrawReferenceImageSectionTitle(
                TJGeneratorsL10n.L(title),
                imageCount,
                maxReferenceImages,
                useShowcaseStyle
                    ? (Action)null
                    : () =>
                    {
                        UIComponents.LinkButton(
                            TJGeneratorsL10n.L("生成参考图"),
                            () =>
                            {
                                AIReferenceImageWindow.Show(
                                    (path, texture) =>
                                    {
                                        _imagePath = path;
                                        _uploadedImage = texture;
                                        context.Repaint();
                                    }
                                );
                            }
                        );
                    });
            GUILayout.Space(titleToControlSpacing);

            if (useShowcaseStyle)
            {
                UploadImageComponents.DrawLargeImageUpload(
                    ref _imagePath,
                    ref _uploadedImage,
                    () =>
                    {
                        AIReferenceImageWindow.Show(
                            (path, texture) =>
                            {
                                _imagePath = path;
                                _uploadedImage = texture;
                                _currentInputMode = string.IsNullOrEmpty(path) ? "text" : "image";
                                UpdateEndpointForInputMode();
                                context.Repaint();
                            }
                        );
                    },
                    context.Repaint,
                    onUserChanged: () =>
                    {
                        _currentInputMode = string.IsNullOrEmpty(_imagePath) ? "text" : "image";
                        UpdateEndpointForInputMode();
                    },
                    onPickDone: (path, tex) =>
                    {
                        _imagePath = path;
                        _uploadedImage = tex;
                    });
            }
            else
            {
                UploadImageComponents.DrawSingleImageUpload(
                    ref _imagePath,
                    ref _uploadedImage,
                    context.Repaint,
                    onUserChanged: () =>
                    {
                        _currentInputMode = string.IsNullOrEmpty(_imagePath) ? "text" : "image";
                        UpdateEndpointForInputMode();
                    },
                    onPickDone: (path, tex) =>
                    {
                        _imagePath = path;
                        _uploadedImage = tex;
                    }
                );
            }
            if (addTrailingSectionSpacing)
                GUILayout.Space(sectionSpacing);
        }

        private void DrawTopGenerateButton(
            IGenerationPipelineHost context,
            UILayoutConfig uiLayout,
            LeftPanelBottomDock.Layout layout)
        {
            bool windowPipelineBusy = context is GenerationWindowBase gwb && gwb.IsPipelineBusy;
            bool busy = IsRunning || windowPipelineBusy;
            bool canGenerate = !busy;
            if (uiLayout.showFileUpload)
            {
                canGenerate =
                    canGenerate && !string.IsNullOrEmpty(_uploadedFilePath);
            }
            else if (canGenerate)
            {
                canGenerate = ValidateInputs(out _);
            }

            // 与独立窗口共用 DrawGenerationSectionAt：统一延迟输入框提交（DelayCall）。
            // 生成中仅切换按钮文案，进度在历史项等处展示。
            UIComponents.DrawGenerationSectionAt(
                layout,
                busy,
                0f,
                "",
                canGenerate,
                () => HandleTopGenerateClicked(context, uiLayout),
                null,
                busy ? (Action)context.Repaint : null);
        }

        private void HandleTopGenerateClicked(IGenerationPipelineHost context, UILayoutConfig uiLayout)
        {
            if (ValidateInputs(out string error))
            {
                if (uiLayout.showFileUpload)
                {
                    _currentEndpointKey = "default";
                }
                else
                {
                    var (isMultiViewOnly, isDualMode) = ResolveUILayoutModes(uiLayout);

                    if (isMultiViewOnly || (isDualMode && _primaryInputMode == "multiview"))
                    {
                        _currentInputMode = "multiview";
                    }
                    else
                    {
                        if (HasReferenceImageInput())
                            _currentInputMode = "image";
                        else
                            _currentInputMode = "text";
                    }
                    UpdateEndpointForInputMode();
                }
                if (context is IGenerationTriggerHost triggerHost)
                    triggerHost.StartGeneration(this);
                else
                    ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("当前宿主不支持触发生成。"), "[DynamicGenerator]");
            }
            else
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("输入不完整"), error, "[DynamicGenerator]");
            }
        }

        private void EnsureMultiViewInit()
        {
            if (_multiViewPaths == null)
                _multiViewPaths = new List<string>();
            if (_multiViewImages == null)
                _multiViewImages = new List<Texture2D>();

            // Pad to four slots without discarding partial uploads (Count 1–3 must be preserved).
            while (_multiViewPaths.Count < 4)
                _multiViewPaths.Add(null);
            while (_multiViewImages.Count < 4)
                _multiViewImages.Add(null);

            _multiViewCount = 4;
            _multiViewMinRequired = GetMultiViewMinRequired();
        }

        private int GetMultiViewMinRequired()
        {
            return _config?.uiLayout?.multiViewMinRequired ?? 2;
        }

        private void DrawMultiViewHeaderRow(
            IGenerationPipelineHost context,
            UILayoutConfig uiLayout,
            float foldoutSpacing
        )
        {
            GUILayout.Space(foldoutSpacing);
            GUILayout.BeginHorizontal();
            UIComponents.DrawSectionTitle(
                TJGeneratorsL10n.L(uiLayout.multiViewLabel ?? "多视图生成"),
                uppercase: false);
            GUILayout.FlexibleSpace();
            UIComponents.LinkButton(
                TJGeneratorsL10n.L("生成参考图"),
                () =>
                {
                    AIReferenceImageWindow.ShowMultiView(
                        (paths, textures) =>
                        {
                            TJLog.Log(
                                $"[DynamicGenerator][MultiView] reference callback: "
                                    + $"pathsLen={(paths == null ? -1 : paths.Length)}, "
                                    + $"texturesLen={(textures == null ? -1 : textures.Length)}"
                            );

                            EnsureMultiViewInit();

                            int count = Math.Min(4, paths == null ? 0 : paths.Length);
                            for (int i = 0; i < count; i++)
                            {
                                string p = paths[i];
                                bool fileExists = !string.IsNullOrEmpty(p) && File.Exists(p);

                                Texture2D tex = null;
                                if (textures != null && i < textures.Length)
                                    tex = textures[i];

                                TJLog.Log(
                                    $"[DynamicGenerator][MultiView] slot[{i}]: "
                                        + $"pathEmpty={string.IsNullOrEmpty(p)}, fileExists={fileExists}, texNull={tex == null}"
                                );

                                if (!string.IsNullOrEmpty(p) && fileExists)
                                {
                                    _multiViewPaths[i] = p;

                                    if (tex == null)
                                    {
                                        try
                                        {
                                            tex = new Texture2D(2, 2);
                                            tex.LoadImage(File.ReadAllBytes(p));
                                        }
                                        catch (Exception e)
                                        {
                                            TJLog.LogWarning(
                                                $"[DynamicGenerator][MultiView] slot[{i}] reload texture failed: {e.Message}"
                                            );
                                        }
                                    }

                                    _multiViewImages[i] = tex;

                                    TJLog.Log(
                                        $"[DynamicGenerator][MultiView] slot[{i}] after set: imageNull={_multiViewImages[i] == null}"
                                    );
                                }
                            }
                            context.Repaint();
                        },
                        GetMultiViewMinRequired()
                    );
                }
            );
            GUILayout.EndHorizontal();
        }

        private void DrawFileUpload(IGenerationPipelineHost context)
        {
            var uiLayout = _config.uiLayout ?? new UILayoutConfig();

            GUILayout.BeginVertical();
            UIComponents.DrawSectionTitle(TJGeneratorsL10n.L(uiLayout.fileUploadLabel ?? "绑骨模型"), uppercase: false);
            GUILayout.EndVertical();

            GUILayout.Space(CommonStyles.Space2);

            GUILayout.BeginVertical();
            GUILayout.Label(
                TJGeneratorsL10n.L("从当前工程的 Assets 中选择 FBX / OBJ。"),
                CommonStyles.SmallGreyLabelStyle
            );
            GUILayout.EndVertical();

            GUILayout.Space(CommonStyles.Space2);

            RefreshProjectMeshModelsForUpload();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(TJGeneratorsL10n.L("刷新模型列表"), GUILayout.Width(100f)))
            {
                s_forceProjectMeshRescan = true;
                context.Repaint();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();

            if (_projectMeshAssetPaths == null || _projectMeshAssetPaths.Count == 0)
            {
                GUILayout.BeginVertical();
                GUILayout.Label(
                    TJGeneratorsL10n.L("Assets 内未找到 FBX / OBJ，请将模型导入工程后刷新本窗口。"),
                    CommonStyles.HelpBoxStyle
                );
                GUILayout.EndVertical();
            }
            else
            {
                GUILayout.BeginVertical();
                EditorGUI.BeginChangeCheck();
                _selectedProjectMeshPopupIndex = EditorGUILayout.Popup(
                    _selectedProjectMeshPopupIndex,
                    _projectMeshPopupOptions
                );
                if (EditorGUI.EndChangeCheck())
                {
                    ApplyProjectMeshFromPopup(_selectedProjectMeshPopupIndex);
                    context.Repaint();
                }
                GUILayout.EndVertical();
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(CommonStyles.Space2);

            if (!string.IsNullOrEmpty(_uploadedFilePath))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(TJGeneratorsL10n.L("已选择文件:"), CommonStyles.SmallGreyLabelStyle);
                GUILayout.Label(_uploadedFileName, CommonStyles.ContentStyle);
                if (GUILayout.Button("×", GUILayout.Width(20f)))
                {
                    ClearFileUploadSelection();
                    context.Repaint();
                }
                GUILayout.EndHorizontal();
            }

            GameObject preview =
                !string.IsNullOrEmpty(_uploadedModelAssetPath)
                    ? AssetDatabase.LoadAssetAtPath<GameObject>(_uploadedModelAssetPath)
                    : null;
            if (preview != null)
            {
                GUILayout.Space(CommonStyles.Space2);
                GUILayout.BeginHorizontal();
                EditorGUILayout.ObjectField(preview, typeof(GameObject), false, GUILayout.Height(40f));
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(CommonStyles.Space2);
            GUILayout.BeginHorizontal();
            GUILayout.Label(
                TJGeneratorsL10n.L(uiLayout.fileUploadHint ?? "支持 FBX、OBJ 格式的 3D 模型文件"),
                CommonStyles.HelpBoxStyle
            );
            GUILayout.EndHorizontal();
        }

        private void ClearFileUploadSelection()
        {
            _uploadedFilePath = "";
            _uploadedFileName = "";
            _uploadedModelAssetPath = "";
            _selectedProjectMeshPopupIndex = 0;
        }

        private void ApplyProjectMeshFromPopup(int popupIndex)
        {
            if (popupIndex <= 0)
            {
                ClearFileUploadSelection();
                return;
            }

            int assetIdx = popupIndex - 1;
            if (
                _projectMeshAssetPaths == null
                || assetIdx < 0
                || assetIdx >= _projectMeshAssetPaths.Count
            )
            {
                ClearFileUploadSelection();
                return;
            }

            string assetPath = _projectMeshAssetPaths[assetIdx];
            _uploadedModelAssetPath = assetPath;
            _uploadedFilePath = PathUtils.ToAbsoluteAssetPath(assetPath);
            _uploadedFileName = Path.GetFileName(assetPath);
        }

        private void RefreshProjectMeshModelsForUpload()
        {
            if (
                _projectMeshAssetPaths != null
                && _projectMeshPopupOptions != null
                && !s_forceProjectMeshRescan
            )
                return;

            s_forceProjectMeshRescan = false;
            _projectMeshAssetPaths = PathUtils.FindMeshModelAssetPathsInAssets();
            int n = _projectMeshAssetPaths.Count;
            _projectMeshPopupOptions = new string[n + 1];
            _projectMeshPopupOptions[0] = TJGeneratorsL10n.L("— 选择项目内模型 —");
            for (int i = 0; i < n; i++)
                _projectMeshPopupOptions[i + 1] = _projectMeshAssetPaths[i];

            SyncProjectMeshPopupIndexFromUploadState();
        }

        private void SyncProjectMeshPopupIndexFromUploadState()
        {
            if (_projectMeshAssetPaths == null || _projectMeshAssetPaths.Count == 0)
            {
                _selectedProjectMeshPopupIndex = 0;
                return;
            }

            if (string.IsNullOrEmpty(_uploadedFilePath) || !File.Exists(_uploadedFilePath))
            {
                _selectedProjectMeshPopupIndex = 0;
                return;
            }

            string rel =
                !string.IsNullOrEmpty(_uploadedModelAssetPath)
                    ? _uploadedModelAssetPath
                    : PathUtils.TryGetAssetsRelativePathFromAbsolute(_uploadedFilePath);

            if (string.IsNullOrEmpty(rel))
            {
                _selectedProjectMeshPopupIndex = 0;
                return;
            }

            int idx = _projectMeshAssetPaths.FindIndex(p =>
                string.Equals(p, rel, StringComparison.OrdinalIgnoreCase)
            );
            _selectedProjectMeshPopupIndex = idx >= 0 ? idx + 1 : 0;
        }

        private void DrawMultiViewArea(IGenerationPipelineHost context)
        {
            float labelControlSpacing = 6f;
            GUILayout.Space(CommonStyles.Space2);
            var uiLayout = _config.uiLayout ?? new UILayoutConfig();
            GUILayout.Label(
                TJGeneratorsL10n.L(uiLayout.multiViewHint),
                CommonStyles.SmallGreyLeftLabelStyle,
                GUILayout.MaxWidth(CommonStyles.LeftComponentWidth),
                GUILayout.ExpandWidth(true));

            GUILayout.Space(CommonStyles.Space2);

            // 4个图片上传槽位 - 居中排列（单层 Horizontal + FlexibleSpace 避免嵌套导致 EndLayoutGroup 错位）
            float multiViewBoxSize = 70f;
            float multiViewSpacing = 15f;

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            for (int i = 0; i < 4; i++)
            {
                DrawMultiViewSlot(context, i, multiViewBoxSize);
                if (i < 3)
                    GUILayout.Space(multiViewSpacing);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // 标签
            GUILayout.Space(labelControlSpacing);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            for (int i = 0; i < 4; i++)
            {
                GUILayout.Label(
                    TJGeneratorsL10n.L(s_multiViewFooterLabels[i]),
                    CommonStyles.SmallGreyCenterLabelStyle,
                    GUILayout.Width(multiViewBoxSize)
                );
                if (i < 3)
                    GUILayout.Space(multiViewSpacing);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawMultiViewSlot(IGenerationPipelineHost context, int index, float boxSize)
        {
            // 确保列表足够大
            while (_multiViewPaths.Count <= index)
                _multiViewPaths.Add(null);
            while (_multiViewImages.Count <= index)
                _multiViewImages.Add(null);

            GUILayout.BeginVertical(GUILayout.Width(boxSize));

            Rect boxRect = GUILayoutUtility.GetRect(boxSize, boxSize);

            // 绘制背景框
            var bgTexture =
                index == 0 ? CommonStyles.MutiViewSelectedBoxTexture : CommonStyles.MutiViewBoxTexture;
            GUI.DrawTexture(boxRect, bgTexture, ScaleMode.StretchToFill);

            // 计算清除按钮位置
            Rect clearRect = Rect.zero;
            if (_multiViewImages[index] != null)
            {
                float clearBtnSize = 16f;
                clearRect = new Rect(
                    boxRect.x + boxRect.width - clearBtnSize - 2f,
                    boxRect.y + 2f,
                    clearBtnSize,
                    clearBtnSize
                );
            }

            // 处理点击事件
            Event evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0)
            {
                if (clearRect != Rect.zero && clearRect.Contains(evt.mousePosition))
                {
                    _multiViewPaths[index] = null;
                    _multiViewImages[index] = null;
                    evt.Use();
                    context.Repaint();
                }
                else if (boxRect.Contains(evt.mousePosition))
                {
                    evt.Use();
                    int slotIndex = index;
                    EditorApplication.delayCall += () =>
                    {
                        while (_multiViewPaths.Count <= slotIndex)
                            _multiViewPaths.Add(null);
                        while (_multiViewImages.Count <= slotIndex)
                            _multiViewImages.Add(null);
                        string path = EditorUtility.OpenFilePanel(
                            TJGeneratorsL10n.L("选择{0}图片", TJGeneratorsL10n.L(s_multiViewPickerTitleLabels[slotIndex])),
                            "",
                            TJGeneratorsImageAssetPathUtility.ReferenceImagePickFilter
                        );
                        if (string.IsNullOrEmpty(path))
                            return;

                        if (!TJGeneratorsImageAssetPathUtility.IsSupportedReferenceImageUploadPath(path))
                        {
                            ErrorDialogUtils.ShowErrorDialog(
                                TJGeneratorsL10n.L("不支持的图片格式"),
                                TJGeneratorsL10n.L("仅支持 png、jpg、jpeg 格式的图片。"),
                                "[DynamicGenerator]");
                            return;
                        }

                        var tex = new Texture2D(2, 2);
                        if (!tex.LoadImage(File.ReadAllBytes(path)))
                        {
                            UnityEngine.Object.DestroyImmediate(tex);
                            ErrorDialogUtils.ShowErrorDialog(
                                TJGeneratorsL10n.L("错误"),
                                TJGeneratorsL10n.L("无法读取图片文件。"),
                                "[DynamicGenerator]");
                            return;
                        }

                        _multiViewPaths[slotIndex] = path;
                        _multiViewImages[slotIndex] = tex;
                        context.Repaint();
                    };
                }
            }

            // 绘制图片预览或加号
            if (_multiViewImages[index] != null)
            {
                float padding = 3f;
                Rect previewRect = new Rect(
                    boxRect.x + padding,
                    boxRect.y + padding,
                    boxRect.width - padding * 2,
                    boxRect.height - padding * 2
                );
                GUI.DrawTexture(previewRect, _multiViewImages[index], ScaleMode.ScaleToFit);

                GUI.Label(clearRect, CommonStyles.ClearButtonSymbol, CommonStyles.ClearButtonStyle);
            }
            else
            {
                GUI.Label(boxRect, CommonStyles.PlusSymbol, CommonStyles.PlusStyle);
            }

            GUILayout.EndVertical();
        }

        #endregion

        #region 端点与输入模式

        private static void DestroyTextureIfNeeded(Texture2D texture)
        {
            if (texture != null)
                UnityEngine.Object.DestroyImmediate(texture);
        }

        private void DestroyUploadedImage()
        {
            DestroyTextureIfNeeded(_uploadedImage);
            _uploadedImage = null;
        }

        private void DestroyMultiViewImages()
        {
            if (_multiViewImages == null)
                return;

            for (int i = 0; i < _multiViewImages.Count; i++)
                DestroyTextureIfNeeded(_multiViewImages[i]);
        }

        private void UpdateEndpointForInputMode()
        {
            // Update endpoint key based on current input mode
            _currentEndpointKey = GetEndpointKeyForInputMode(_currentInputMode);
        }

        private static string GetEndpointKeyForInputMode(string inputMode)
        {
            switch (inputMode)
            {
                case "multiview": return "multiview";
                case "image":     return "image";
                default:          return "text";
            }
        }

        #endregion

        #region 验证

        private bool TryValidateMultiViewInputs(out string errorMessage)
        {
            if (_multiViewCount <= 0 || _multiViewPaths == null)
            {
                errorMessage =
                    TJGeneratorsL10n.L("至少需要{0}张图片进行多视图生成", GetMultiViewMinRequired());
                return false;
            }
            if (
                _multiViewPaths.Count == 0
                || string.IsNullOrEmpty(_multiViewPaths[0])
                || !File.Exists(_multiViewPaths[0])
            )
            {
                errorMessage = TJGeneratorsL10n.L("正面图片是必需的，且文件必须存在。");
                return false;
            }
            int uploadedCount = 0;
            for (int i = 0; i < _multiViewPaths.Count; i++)
            {
                if (
                    !string.IsNullOrEmpty(_multiViewPaths[i])
                    && File.Exists(_multiViewPaths[i])
                )
                    uploadedCount++;
            }
            if (uploadedCount < _multiViewMinRequired)
            {
                errorMessage =
                    uploadedCount > 0
                        ? TJGeneratorsL10n.L("多视图至少需要{0}张图片", _multiViewMinRequired)
                        : TJGeneratorsL10n.L("请上传多视图图片");
                return false;
            }
            errorMessage = null;
            return true;
        }

        /// <summary>
        /// 是否有参考图输入（路径已填或多图列表非空）。与 <see cref="IsTextToModel"/> 的语义不同，勿混用。
        /// </summary>
        private bool HasReferenceImageInput() =>
            !string.IsNullOrEmpty(_imagePath)
            || (_imagePaths != null && _imagePaths.Count > 0);

        /// <summary>
        /// 文生图/图生图等：后端要求必须有用户输入的提示词，不能只传参考图或仅依赖模板前缀。
        /// </summary>
        private bool OutputTypeRequiresUserTextPrompt()
        {
            string ot = _config?.outputType;
            return string.Equals(ot, "image", StringComparison.OrdinalIgnoreCase);
        }

        public override bool ValidateInputs(out string errorMessage)
        {
            var uiLayout = _config.uiLayout ?? new UILayoutConfig();
            if (uiLayout.showMultiView)
                _multiViewMinRequired = GetMultiViewMinRequired();

            if (
                ShouldShowEnableMotionUi()
                && _addMotionEnabled
                && string.IsNullOrWhiteSpace(_motionDescription)
            )
            {
                errorMessage = TJGeneratorsL10n.L("已勾选添加动作，请输入动作描述");
                return false;
            }

            // 文件上传模式
            if (uiLayout.showFileUpload)
            {
                if (string.IsNullOrEmpty(_uploadedFilePath))
                {
                    errorMessage = TJGeneratorsL10n.L("请选择要绑骨的模型文件");
                    return false;
                }

                if (!File.Exists(_uploadedFilePath))
                {
                    errorMessage = TJGeneratorsL10n.L("选择的文件不存在");
                    return false;
                }

                errorMessage = null;
                return true;
            }

            var (isMultiViewOnly, isDualMode) = ResolveUILayoutModes(uiLayout);

            // 双模式：仅校验当前选中模式的输入
            if (isDualMode)
            {
                if (_primaryInputMode == "multiview")
                    return TryValidateMultiViewInputs(out errorMessage);
                // textOrImage：仅校验文生/图生
                bool hasText = !string.IsNullOrWhiteSpace(_textPrompt);
                bool hasImage = HasReferenceImageInput();
                if (OutputTypeRequiresUserTextPrompt())
                {
                    if (!hasText)
                    {
                        errorMessage = TJGeneratorsL10n.L("请输入提示词");
                        return false;
                    }
                }
                else if (!hasText && !hasImage)
                {
                    errorMessage = TJGeneratorsL10n.L("请输入提示词或上传图片");
                    return false;
                }
                errorMessage = null;
                return true;
            }

            // 仅多视图：只校验多视图
            if (isMultiViewOnly)
                return TryValidateMultiViewInputs(out errorMessage);

            // Rodin：须存在有效提示词或磁盘上可读的参考图（与自动 conditionMode 一致；fuse 仅在有图无文时）
            if (DynamicRequestJsonBuilder.IsRodinGenerator(_config))
            {
                var requestCtx = CreateRequestBuildContext();
                bool hasText = !string.IsNullOrWhiteSpace(
                    DynamicRequestJsonBuilder.BuildEnhancedPrompt(requestCtx)
                );
                bool hasValidImage = DynamicRequestJsonBuilder.RodinHasAnyValidReferenceImage(
                    requestCtx
                );
                if (!hasText && !hasValidImage)
                {
                    errorMessage = TJGeneratorsL10n.L("请输入提示词或上传有效的参考图片");
                    return false;
                }
                errorMessage = null;
                return true;
            }

            // 仅文生/图生：校验文本或图片
            bool hasTextCheck = !string.IsNullOrWhiteSpace(_textPrompt);
            bool hasImageCheck = HasReferenceImageInput();
            bool hasMultiView = false;
            if (_multiViewCount > 0)
            {
                int uploadedCount = 0;
                for (int i = 0; i < _multiViewPaths.Count; i++)
                {
                    if (!string.IsNullOrEmpty(_multiViewPaths[i]))
                        uploadedCount++;
                }
                if (uploadedCount >= _multiViewMinRequired)
                    hasMultiView = true;
                else if (uploadedCount > 0)
                {
                    errorMessage = TJGeneratorsL10n.L("多视图至少需要{0}张图片", _multiViewMinRequired);
                    return false;
                }
            }

            if (OutputTypeRequiresUserTextPrompt())
            {
                if (!hasTextCheck)
                {
                    errorMessage = TJGeneratorsL10n.L("请输入提示词");
                    return false;
                }
            }
            else if (!hasTextCheck && !hasImageCheck && !hasMultiView)
            {
                errorMessage = TJGeneratorsL10n.L("请输入提示词或上传图片");
                return false;
            }

            errorMessage = null;
            return true;
        }

        #endregion

        #region API请求构建（动态JSON）

        private DynamicRequestBuildContext CreateRequestBuildContext()
        {
            return new DynamicRequestBuildContext(
                _config,
                _textPrompt,
                _selectedType,
                _selectedStyle,
                _selectedPromptTemplate,
                _imagePath,
                _imagePaths,
                _multiViewPaths,
                _multiViewCount,
                _currentInputMode,
                _parameterValues,
                _extraRawJsonFields
            );
        }

        public override object BuildRequestData()
        {
            var uiLayout = _config.uiLayout ?? new UILayoutConfig();

            // 文件上传模式：返回MultipartRequestData
            if (uiLayout.showFileUpload && !string.IsNullOrEmpty(_uploadedFilePath))
            {
                return new MultipartRequestData
                {
                    FilePath = _uploadedFilePath,
                    FileName = _uploadedFileName,
                    FileFieldName = "file",
                    AdditionalFields = null,
                };
            }

            return new DynamicRequestData
            {
                JsonContent = DynamicRequestJsonBuilder.BuildRequestJson(
                    CreateRequestBuildContext()
                ),
            };
        }

        #endregion

        #region 响应解析（委托给 DynamicTaskResponseResolver）

        private DynamicTaskResponseContext CreateResponseContext()
        {
            return new DynamicTaskResponseContext(
                _config,
                _parameterValues,
                GeneratorId,
                _currentInputMode
            );
        }

        public override string GetDownloadUrl(TJTaskStatusResponse response) =>
            DynamicTaskResponseResolver.GetDownloadUrl(CreateResponseContext(), response);

        public override string[] GetDownloadUrls(TJTaskStatusResponse response) =>
            DynamicTaskResponseResolver.GetDownloadUrls(CreateResponseContext(), response);

        public override string GetPreviewImageUrl(TJTaskStatusResponse response) =>
            DynamicTaskResponseResolver.GetPreviewImageUrl(CreateResponseContext(), response);

        public override string GetRenderedImageUrl(TJTaskStatusResponse response) =>
            DynamicTaskResponseResolver.GetRenderedImageUrl(CreateResponseContext(), response);

        public override string GetModelFileName() =>
            DynamicTaskResponseResolver.GetModelFileName(CreateResponseContext());

        #endregion

        #region 输出类型、Motion UI 状态与流水线配置

        public override string GetOutputType() => _pipelineSettings.GetOutputType();

        public string AudioFormat
        {
            get
            {
                // Prefer the user-requested output_format (fal enum, e.g. "mp3_44100_128" / "pcm_44100")
                // over the config default. The pipeline uses this to detect the download extension from
                // magic bytes, so it must match what the backend actually returns — otherwise the
                // placeholder (.mp3) and the final asset (.wav) diverge and the placeholder goes stale.
                if (_parameterValues.TryGetValue("outputFormat", out var value) && value != null)
                {
                    string fmt = value.ToString().Trim();
                    if (!string.IsNullOrEmpty(fmt))
                        return fmt;
                }
                return _pipelineSettings.AudioFormat;
            }
        }

        /// <summary>
        /// CustomTool / 任务恢复：打开与 UI「添加动作」相同的后处理（UniRig + HunyuanMotion）。
        /// </summary>
        public void SetAddMotion(bool enabled, string description = "")
        {
            _addMotionEnabled = enabled;
            _motionDescription = description ?? "";
        }

        public override bool GetAddMotionEnabled() => _addMotionEnabled;

        public override string GetMotionDescription() => _motionDescription ?? "";

        public override PipelineSettings GetPipelineSettings() => _pipelineSettings;

        #endregion

        #region 任务恢复

        public override InterruptedTaskData CreateInterruptedTaskData(
            string backendTaskId,
            string targetAssetGuid
        )
        {
            var data = new InterruptedTaskData
            {
                backendTaskId = backendTaskId,
                localTaskId = CurrentGeneratingTaskId,
                prompt = _textPrompt,
                imagePath = GetImagePath(),
                modelVersion = _config.id,
                isTextToModel = IsTextToModel(),
                timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                sessionId = TJGeneratorsTaskRecovery.SessionId,
                targetAssetGuid = targetAssetGuid ?? "",
                status = "pending",
            };

            var animationType = GetParameter("animation_type")?.ToString();
            if (!string.IsNullOrEmpty(animationType))
                data.animationType = animationType;

            if (GetParameter("fps") != null && int.TryParse(GetParameter("fps").ToString(), out int fps) && fps > 0)
                data.fps = fps;

            if (GetParameter("loop") != null)
            {
                data.loopSpecified = true;
                if (GetParameter("loop") is bool loopBool)
                    data.loop = loopBool;
                else if (bool.TryParse(GetParameter("loop").ToString(), out bool loopParsed))
                    data.loop = loopParsed;
            }

            if (GetParameter("numLayers") != null
                && int.TryParse(GetParameter("numLayers").ToString(), out int numLayers)
                && numLayers > 0)
            {
                data.numLayers = numLayers;
            }

            data.addMotion = _addMotionEnabled;
            data.motionDescription = _motionDescription ?? "";

            return data;
        }

        public override void RestoreFromInterruptedTask(InterruptedTaskData taskData)
        {
            base.RestoreFromInterruptedTask(taskData);
            if (taskData == null) return;

            if (!string.IsNullOrEmpty(taskData.prompt))
                SetTextPrompt(taskData.prompt);

            if (!string.IsNullOrEmpty(taskData.imagePath))
                SetImagePath(taskData.imagePath);

            if (!string.IsNullOrEmpty(taskData.animationType))
                SetParameter("animation_type", taskData.animationType);

            if (taskData.fps > 0)
                SetParameter("fps", taskData.fps);

            if (taskData.loopSpecified)
                SetParameter("loop", taskData.loop);

            if (taskData.numLayers > 0)
                SetParameter("numLayers", taskData.numLayers);

            if (taskData.addMotion)
                SetAddMotion(true, taskData.motionDescription);
        }

        #endregion

        #region 历史记录

        public override string GetPrompt() => _textPrompt;

        public override string GetHistoryDisplayPrompt()
        {
            if (!string.IsNullOrWhiteSpace(_historyDisplayPrompt))
                return _historyDisplayPrompt.Trim();
            return TJGenerators.Utils.TJGeneratorsPromptDisplay.ExtractUserFacingPrompt(_textPrompt);
        }

        public override string GetImagePath() =>
            (_imagePaths != null && _imagePaths.Count > 0) ? _imagePaths[0] : _imagePath;

        public override string GetModelVersion() => _config.id;

        public override bool IsTextToModel() =>
            (_imagePaths == null || _imagePaths.Count == 0) && string.IsNullOrEmpty(_imagePath);

        #endregion
    }
}
#endif
