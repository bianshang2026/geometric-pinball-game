#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using TJGenerators.Config;
using TJGenerators.UI;
using TJGenerators.Utils;
using UnityEditor;
using UnityEngine;
using Unity.EditorCoroutines.Editor;

namespace TJGenerators
{
    /// <summary>
    /// 模型选择器窗口
    /// </summary>
    public class TJGeneratorsModelSelectorWindow : EditorWindow
    {
        private static string AllTag => TJGeneratorsL10n.L("全部");
        private const string PreferenceKey = "TJGenerators.ModelSelector.Preferences";
        private static float CardMinWidth => 222f;
        private static float CardHeight => 224f;
        private static float CardImageHeight => 156f;
        private static float CardGap => 10f;
        // 分区（来自新版布局大分区设定）
        private static Vector2 SelectorWindowFixedSize =>
            new Vector2(1175f, 686f);
        private static float SidebarWidth => 196f;
        private static float RightPanelPaddingX => 14f;
        private static float RightSearchTop => 20f;
        private static float RightVendorButtonsTop => 79f;
        private static float RightSearchHeight => 44f;
        // 固定窗口 1160x686 下，为保证卡片约 222×224 且每排 4 个（间距 10），
        // 右侧可用宽度需满足：4*222 + 3*10 = 918。rightInner 宽度为 936，因此左右边距总和需为 18。
        // 这组边距同时用于搜索框、分类按钮、卡片区，确保它们左右对齐。
        private static float RightContentLeftPadding => 14f;
        private static float RightContentRightPadding => 4f;
        private static float RightSearchFixedWidth => 919f;
        private static float RightSearchFixedHeight => 38f;
        private static float RightVendorRowHeight => 36f;
        private static float RightStatusBarHeight => 44f;
        private static readonly Color RightStatusBarColor = new Color(44f / 255f, 44f / 255f, 44f / 255f, 1f); // #2C2C2C
        
        // 右侧 vendor 分类按钮：按 label 单独微调宽高（默认 0,0）
        // 说明：用于规避九宫格在某些目标尺寸区间触发的接缝；尽量只在个别按钮上微调。
        private static readonly Dictionary<string, Vector2> VendorButtonSizeDeltaByLabel =
            new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase)
            {
                // 你可以在这里对单个按钮精调，例如 new Vector2(+1f, 0f) 或 new Vector2(+2f, +1f)
                // { "Tripo", new Vector2(-1f, 0f) },
                // { "Tencent Hunyuan", new Vector2(1f, 0f) },
                // { "Rodin", new Vector2(1f, 0f) },
                // { "UniRig", new Vector2(1f, 0f) },
            };

        // Tab系统
        private const int TAB_MODEL = 0;
        private const int TAB_TYPE = 1;
        private const int TAB_STYLE = 2;

        // 回调
        private Action<AIModelInfo> onModelSelected;
        private Action<SelectorOptionConfig> onTypeSelected;
        private Action<SelectorOptionConfig> onStyleSelected;

        private IList<GeneratorConfig> _generatorList;

        private string _preferenceKey = PreferenceKey;
        private ConfigType? _configType;

        // 模型数据
        private List<AIModelInfo> allModels = new List<AIModelInfo>();
        private List<AIModelInfo> filteredModels = new List<AIModelInfo>();
        private AIModelInfo selectedModel;

        // 类型和风格数据
        private List<SelectorOptionConfig> typeOptions;
        private List<SelectorOptionConfig> styleOptions;
        private SelectorOptionConfig selectedType;
        private SelectorOptionConfig selectedStyle;
        private int currentTab = TAB_MODEL;

        // 类型/风格分类过滤
        private string selectedTypeCategory = AllTag;
        private string selectedStyleCategory = AllTag;

        // 标签过滤
        private List<string> functionTagOptions = new List<string>();
        private List<string> vendorTagOptions = new List<string>();
        private List<string> typeTagOptions = new List<string>();
        private List<string> styleTagOptions = new List<string>();
        private string selectedFunctionTag = AllTag;
        private string selectedVendorTag = AllTag;
        private string searchText = "";

        // UI状态
        private Vector2 scrollPosition;

        // 远程图标缓存（用于类型/风格选项的远程图片）
        private Dictionary<string, Texture2D> remoteIconCache = new Dictionary<string, Texture2D>();
        private HashSet<string> remoteIconLoading = new HashSet<string>();
        private HashSet<string> remoteIconFailed = new HashSet<string>();

        // 样式缓存
        private GUIStyle tagButtonStyle;
        private GUIStyle tagButtonSelectedStyle;
        private bool stylesInitialized;
        
        // 左侧分区按钮（新版样式）
        private GUIStyle _sidebarSegmentTextStyle;
        private Texture2D _sidebarBtnNormalTex;
        private Texture2D _sidebarBtnHoverTex;
        private Texture2D _sidebarBtnPressedTex;
        private static float SidebarButtonsLeft => 20f;
        private static float SidebarButtonsRight => 17f;
        private static float SidebarButtonsTop => 79f;
        private static float SidebarButtonWidth => 159f;
        private static float SidebarButtonHeight => 44f; // padding 10+10 + lineHeight 24
        private static float SidebarButtonGap => 10f;
        /// <summary>侧栏与厂商按钮九宫格目标边（设计 8px），随缩放取整。</summary>
        private static int NineSliceDestBorder8 => Mathf.Max(1, Mathf.Max(0, Mathf.RoundToInt(8f)));

        /// <summary>
        /// 通用卡片数据接口
        /// </summary>
        private interface ICardData
        {
            string Id { get; }
            string Name { get; }
            string Description { get; }
            Texture2D Icon { get; }
            bool IsPinned { get; }
            bool IsSelected { get; }
            string ExtraInfo { get; }
            bool CanTogglePin { get; }
            void OnPinToggled();
            void OnSelected();
        }

        /// <summary>
        /// 模型卡片数据适配器
        /// </summary>
        private class ModelCardData : ICardData
        {
            private readonly AIModelInfo model;
            private readonly AIModelInfo selectedModel;
            private readonly TJGeneratorsModelSelectorWindow window;

            public ModelCardData(AIModelInfo model, AIModelInfo selectedModel, TJGeneratorsModelSelectorWindow window)
            {
                this.model = model;
                this.selectedModel = selectedModel;
                this.window = window;
            }

            public string Id => model.Id;
            public string Name => model.Name;
            public string Description => model.Description;
            public Texture2D Icon => model.Icon;
            public bool IsPinned => model.IsPinned;
            public bool IsSelected => selectedModel != null && selectedModel.Id == model.Id;
            public string ExtraInfo => BuildTagSummary(model);
            public bool CanTogglePin => true;

            public void OnPinToggled()
            {
                model.IsPinned = !model.IsPinned;
                window.SavePreferences();
                window.ApplyFilters();
            }

            public void OnSelected()
            {
                window.selectedModel = model;
                model.LastUsed = DateTime.Now;
                window.SavePreferences();
                window.onModelSelected?.Invoke(model);
                window.Close();
            }
        }

        /// <summary>
        /// 类型/风格卡片数据适配器
        /// </summary>
        private class OptionCardData : ICardData
        {
            private readonly SelectorOptionConfig option;
            private readonly SelectorOptionConfig selectedOption;
            private readonly Action<SelectorOptionConfig> onSelected;
            private readonly TJGeneratorsModelSelectorWindow window;

            public OptionCardData(SelectorOptionConfig option, SelectorOptionConfig selectedOption, 
                Action<SelectorOptionConfig> onSelected, TJGeneratorsModelSelectorWindow window)
            {
                this.option = option;
                this.selectedOption = selectedOption;
                this.onSelected = onSelected;
                this.window = window;
            }

            public string Id => option.id;
            public string Name => TJGeneratorsL10n.L(option.name);
            public string Description => TJGeneratorsL10n.L(option.description ?? string.Empty);
            public Texture2D Icon => window.LoadOptionIcon(option);
            public bool IsPinned => option.pinned;
            public bool IsSelected => selectedOption != null && selectedOption.id == option.id;
            public string ExtraInfo => TJGeneratorsL10n.L(option.category);
            public bool CanTogglePin => false;

            public void OnPinToggled() { }

            public void OnSelected()
            {
                onSelected?.Invoke(option);
                window.Close();
            }
        }

        /// <summary>
        /// 清除窗口状态
        /// </summary>
        private void Clear()
        {
            onModelSelected = null;
            onTypeSelected = null;
            onStyleSelected = null;
            _generatorList = new List<GeneratorConfig>();
            typeOptions = null;
            styleOptions = null;
            _preferenceKey = PreferenceKey;
            currentTab = TAB_MODEL;
            selectedModel = null;
            selectedType = null;
            selectedStyle = null;

            searchText = "";
            selectedTypeCategory = AllTag;
            selectedStyleCategory = AllTag;
            selectedFunctionTag = AllTag;
            selectedVendorTag = AllTag;
        }

        /// <summary>
        /// 打开模型选择器窗口（按配置类型加载生成器列表，类型用于独立偏好键）
        /// </summary>
        public static void ShowWindow(AIModelInfo currentModel, Action<AIModelInfo> onSelected, ConfigType configType)
        {
            IList<GeneratorConfig> resolvedGeneratorList = ConfigManager.GetGenerators(configType);

            var window = GetWindow<TJGeneratorsModelSelectorWindow>(true, TJGeneratorsL10n.L("选择模型"), true);
            window.titleContent = new GUIContent(TJGeneratorsL10n.L("选择模型"));
            window.minSize = SelectorWindowFixedSize;
            window.maxSize = SelectorWindowFixedSize;
            
            window.Clear();
            window.onModelSelected = onSelected;
            window._generatorList = resolvedGeneratorList ?? new List<GeneratorConfig>();
            window._preferenceKey = PreferenceKey + "." + configType;
            window.currentTab = TAB_MODEL;
            
            window.InitializeOptions();
            window.selectedModel = window.ResolveSelectedModel(currentModel);
            window.ApplyFilters();
            window.ShowUtility();
        }

        /// <summary>
        /// 打开类型选择器窗口
        /// </summary>
        public static void ShowTypeSelector(
            List<SelectorOptionConfig> typeOptions,
            Action<SelectorOptionConfig> onTypeSelected,
            SelectorOptionConfig currentType = null)
        {
            var window = GetWindow<TJGeneratorsModelSelectorWindow>(true, TJGeneratorsL10n.L("选择类型"), true);
            window.titleContent = new GUIContent(TJGeneratorsL10n.L("选择类型"));
            window.minSize = SelectorWindowFixedSize;
            window.maxSize = SelectorWindowFixedSize;
            
            window.Clear();
            window.onTypeSelected = onTypeSelected;
            window.typeOptions = typeOptions ?? new List<SelectorOptionConfig>();
            window._preferenceKey = PreferenceKey + ".Sprite";
            window.currentTab = TAB_TYPE;
            
            window.selectedType = window.ResolveSelectedOption(window.typeOptions, currentType);
            window.InitializeOptions();
            window.ApplyFilters();
            window.ShowUtility();
        }

        /// <summary>
        /// 打开风格选择器窗口
        /// </summary>
        public static void ShowStyleSelector(
            List<SelectorOptionConfig> styleOptions,
            Action<SelectorOptionConfig> onStyleSelected,
            SelectorOptionConfig currentStyle = null)
        {
            var window = GetWindow<TJGeneratorsModelSelectorWindow>(true, TJGeneratorsL10n.L("选择风格"), true);
            window.titleContent = new GUIContent(TJGeneratorsL10n.L("选择风格"));
            window.minSize = SelectorWindowFixedSize;
            window.maxSize = SelectorWindowFixedSize;
            
            window.Clear();
            window.onStyleSelected = onStyleSelected;
            window.styleOptions = styleOptions ?? new List<SelectorOptionConfig>();
            window._preferenceKey = PreferenceKey + ".Sprite";
            window.currentTab = TAB_STYLE;
            
            window.selectedStyle = window.ResolveSelectedOption(window.styleOptions, currentStyle);
            window.InitializeOptions();
            window.ApplyFilters();
            window.ShowUtility();
        }

        private SelectorOptionConfig ResolveSelectedOption(List<SelectorOptionConfig> options, SelectorOptionConfig current)
        {
            if (current == null || options == null || options.Count == 0)
                return null;

            return options.FirstOrDefault(option =>
                option != null &&
                !string.IsNullOrEmpty(option.id) &&
                string.Equals(option.id, current.id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 获取默认优先模型（按配置类型：不同生成窗口传入不同 ConfigType，如 Generator、Skybox 等；置顶优先，其次最近使用）
        /// </summary>
        public static string GetPreferredModelId(IEnumerable<string> availableGeneratorIds, ConfigType configType)
        {
            List<string> availableIds = availableGeneratorIds?
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();

            string prefKey = PreferenceKey + "." + configType;
            IList<GeneratorConfig> list = ConfigManager.GetGenerators(configType);
            List<AIModelInfo> models = BuildModels(list, includeIcons: false, preferenceKey: prefKey);
            if (availableIds != null && availableIds.Count > 0)
            {
                HashSet<string> allowedIds = new HashSet<string>(availableIds);
                models = models.Where(model => allowedIds.Contains(model.Id)).ToList();
            }
            // 优先使用配置中显式指定的默认模型（如果存在且可用）
            try
            {
                string configuredDefault = ConfigManager.GetDefaultModelId(configType);
                if (!string.IsNullOrEmpty(configuredDefault))
                {
                    // 如果 availableIds 限制了可用集合，则确保默认模型在其中
                    if (availableIds == null || availableIds.Count == 0 || availableIds.Contains(configuredDefault))
                        return configuredDefault;
                    // 否则，如果 models 列表里包含该模型（可能来自其他来源），也接受
                    if (models.Any(m => string.Equals(m.Id, configuredDefault, StringComparison.OrdinalIgnoreCase)))
                        return configuredDefault;
                }
            }
            catch
            {
                // 忽略配置读取错误，回退到原有逻辑
            }

            AIModelInfo preferred = SortModels(models).FirstOrDefault();
            if (preferred != null)
            {
                return preferred.Id;
            }

            return availableIds != null && availableIds.Count > 0 ? availableIds[0] : null;
        }

        private void InitializeOptions()
        {
            allModels = BuildModels(_generatorList, includeIcons: true, preferenceKey: _preferenceKey);
            functionTagOptions = BuildTagOptions(allModels.SelectMany(GetFunctionTags));
            vendorTagOptions = BuildTagOptions(allModels.SelectMany(GetVendorTags));

            if (!functionTagOptions.Contains(selectedFunctionTag))
            {
                selectedFunctionTag = AllTag;
            }

            if (!vendorTagOptions.Contains(selectedVendorTag))
            {
                selectedVendorTag = AllTag;
            }

            if (typeOptions != null && typeOptions.Count > 0)
            {
                typeTagOptions = BuildTagOptions(typeOptions.SelectMany(o => new[] { o.category }));
            }

            if (styleOptions != null && styleOptions.Count > 0)
            {
                styleTagOptions = BuildTagOptions(styleOptions.SelectMany(o => new[] { o.category }));
            }
        }

        private AIModelInfo ResolveSelectedModel(AIModelInfo currentModel)
        {
            if (currentModel != null && !string.IsNullOrEmpty(currentModel.Id))
            {
                AIModelInfo matched = allModels.FirstOrDefault(model => model.Id == currentModel.Id);
                if (matched != null)
                {
                    return matched;
                }
            }

            return SortModels(allModels).FirstOrDefault();
        }

        private static List<AIModelInfo> BuildModels(IList<GeneratorConfig> generatorList, bool includeIcons, string preferenceKey = null)
        {
            List<AIModelInfo> models = new List<AIModelInfo>();
            string key = string.IsNullOrEmpty(preferenceKey) ? PreferenceKey : preferenceKey;
            Dictionary<string, TJGeneratorsModelPreferenceItem> preferenceLookup = LoadPreferenceLookup(key);

            if (generatorList == null || generatorList.Count == 0)
            {
                return models;
            }

            int order = 0;
            foreach (GeneratorConfig generator in generatorList)
            {
                if (generator == null || !generator.enabled)
                {
                    continue;
                }

                models.Add(CreateModelInfo(generator, order, preferenceLookup, includeIcons));
                order++;
            }

            return models;
        }

        private static AIModelInfo CreateModelInfo(
            GeneratorConfig generator,
            int configOrder,
            IReadOnlyDictionary<string, TJGeneratorsModelPreferenceItem> preferenceLookup,
            bool includeIcons)
        {
            ModelSelectorConfig selectorConfig = generator.modelSelector;
            string[] functionTags = NormalizeTags(selectorConfig?.functionTags)
                .Select(t => TJGeneratorsL10n.L(t)).ToArray();
            string[] vendorTags = NormalizeTags(selectorConfig?.vendorTags)
                .Select(t => TJGeneratorsL10n.L(t)).ToArray();

            bool isPinned = selectorConfig != null && selectorConfig.pinned;
            DateTime lastUsed = DateTime.MinValue;
            if (preferenceLookup != null && preferenceLookup.TryGetValue(generator.id, out TJGeneratorsModelPreferenceItem preference))
            {
                isPinned = preference.isPinned;
                if (preference.lastUsedTicks > 0)
                {
                    try
                    {
                        lastUsed = new DateTime(preference.lastUsedTicks, DateTimeKind.Local);
                    }
                    catch
                    {
                        lastUsed = DateTime.MinValue;
                    }
                }
            }

            return new AIModelInfo
            {
                Id = generator.id,
                Name = !string.IsNullOrEmpty(selectorConfig?.name)
                    ? TJGeneratorsL10n.L(selectorConfig.name)
                    : (TJGeneratorsL10n.L(generator.displayName ?? generator.id)),
                Description = TJGeneratorsL10n.L(selectorConfig?.description ?? string.Empty),
                FunctionTags = functionTags,
                VendorTags = vendorTags,
                Icon = includeIcons ? LoadModelIcon(generator.id, selectorConfig?.iconPath) : null,
                IsPinned = isPinned,
                LastUsed = lastUsed,
                ConfigOrder = configOrder
            };
        }

        private static Texture2D LoadModelIcon(string modelId, string configuredIconPath)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(configuredIconPath))
            {
                candidates.Add(configuredIconPath);
            }

            string normalizedId = (modelId ?? string.Empty).Replace(".", "_").Replace("-", "_");
            candidates.Add($"Packages/cn.tuanjie.ai.generators/Editor/EditorTextures/model_{normalizedId}.png");
            candidates.Add($"Assets/Editor/EditorTextures/model_{normalizedId}.png");
            candidates.Add($"Packages/cn.tuanjie.ai.generators/Editor/Resources/model_{normalizedId}.png");
            candidates.Add($"Assets/Editor/Resources/model_{normalizedId}.png");

            foreach (string path in candidates.Where(path => !string.IsNullOrEmpty(path)).Distinct())
            {
                Texture2D icon = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (icon != null)
                {
                    return icon;
                }
            }

            return null;
        }

        private static string[] NormalizeTags(List<string> tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return Array.Empty<string>();
            }

            return tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static Dictionary<string, TJGeneratorsModelPreferenceItem> LoadPreferenceLookup(string key)
        {
            Dictionary<string, TJGeneratorsModelPreferenceItem> lookup = new Dictionary<string, TJGeneratorsModelPreferenceItem>();
            string json = EditorPrefs.GetString(key ?? PreferenceKey, string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                return lookup;
            }

            try
            {
                TJGeneratorsModelPreferenceCollection collection = JsonUtility.FromJson<TJGeneratorsModelPreferenceCollection>(json);
                if (collection?.items == null)
                {
                    return lookup;
                }

                foreach (TJGeneratorsModelPreferenceItem item in collection.items)
                {
                    if (item != null && !string.IsNullOrEmpty(item.id))
                    {
                        lookup[item.id] = item;
                    }
                }
            }
            catch (Exception e)
            {
                TJLog.LogWarning($"[TJGeneratorsModelSelector] 读取模型偏好失败: {e.Message}");
            }

            return lookup;
        }

        private void SavePreferences()
        {
            TJGeneratorsModelPreferenceCollection collection = new TJGeneratorsModelPreferenceCollection();
            foreach (AIModelInfo model in allModels)
            {
                collection.items.Add(new TJGeneratorsModelPreferenceItem
                {
                    id = model.Id,
                    isPinned = model.IsPinned,
                    lastUsedTicks = model.LastUsed.Ticks
                });
            }

            EditorPrefs.SetString(_preferenceKey, JsonUtility.ToJson(collection));
        }

        private List<string> BuildTagOptions(IEnumerable<string> tags)
        {
            List<string> options = new List<string> { AllTag };
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string tag in tags ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                string normalizedTag = TJGeneratorsL10n.L(tag.Trim());
                if (seen.Add(normalizedTag))
                {
                    options.Add(normalizedTag);
                }
            }

            return options;
        }

        private static IEnumerable<string> GetFunctionTags(AIModelInfo model)
        {
            return model?.FunctionTags ?? Array.Empty<string>();
        }

        private static IEnumerable<string> GetVendorTags(AIModelInfo model)
        {
            return model?.VendorTags ?? Array.Empty<string>();
        }

        private void ApplyFilters()
        {
            string keyword = (searchText ?? string.Empty).Trim();

            IEnumerable<AIModelInfo> query = allModels.Where(model =>
                MatchesTag(model.FunctionTags, selectedFunctionTag) &&
                MatchesTag(model.VendorTags, selectedVendorTag) &&
                MatchesSearch(model, keyword));

            filteredModels = SortModelsForDisplay(query).ToList();
        }

        private static bool MatchesTag(IEnumerable<string> tags, string selectedTag)
        {
            if (string.IsNullOrEmpty(selectedTag) || selectedTag == AllTag)
            {
                return true;
            }

            return (tags ?? Array.Empty<string>())
                .Any(tag => string.Equals(tag, selectedTag, StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesSearch(AIModelInfo model, string keyword)
        {
            if (string.IsNullOrEmpty(keyword))
            {
                return true;
            }

            return ContainsIgnoreCase(model.Name, keyword) ||
                   ContainsIgnoreCase(model.Description, keyword) ||
                   (model.FunctionTags != null && model.FunctionTags.Any(tag => ContainsIgnoreCase(tag, keyword))) ||
                   (model.VendorTags != null && model.VendorTags.Any(tag => ContainsIgnoreCase(tag, keyword)));
        }

        private static bool ContainsIgnoreCase(string text, string keyword)
        {
            return !string.IsNullOrEmpty(text) &&
                   text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesOptionSearch(SelectorOptionConfig option, string keyword)
        {
            if (string.IsNullOrEmpty(keyword))
            {
                return true;
            }

            if (option == null)
            {
                return false;
            }

            return ContainsIgnoreCase(TJGeneratorsL10n.L(option.name), keyword) ||
                   ContainsIgnoreCase(TJGeneratorsL10n.L(option.description), keyword) ||
                   ContainsIgnoreCase(TJGeneratorsL10n.L(option.category), keyword) ||
                   ContainsIgnoreCase(option.id, keyword) ||
                   (option.tags != null && option.tags.Any(tag => ContainsIgnoreCase(TJGeneratorsL10n.L(tag), keyword)));
        }

        private static IOrderedEnumerable<AIModelInfo> SortModels(IEnumerable<AIModelInfo> models)
        {
            return models
                .OrderByDescending(model => model.IsPinned)
                .ThenByDescending(model => model.LastUsed)
                .ThenBy(model => model.ConfigOrder)
                .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase);
        }

        private static IOrderedEnumerable<AIModelInfo> SortModelsForDisplay(IEnumerable<AIModelInfo> models)
        {
            // For card rendering we keep a stable order to avoid the selected model
            // jumping to the top just because LastUsed was updated.
            return models
                .OrderByDescending(model => model.IsPinned)
                .ThenBy(model => model.ConfigOrder)
                .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase);
        }

        private void InitializeStyles()
        {
            if (stylesInitialized) return;

            tagButtonStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(10, 10, 3, 3),
                margin = new RectOffset(2, 2, 2, 2),
                normal = { 
                    background = CommonStyles.CreateSolidColorTexture(new Color(0.24f, 0.24f, 0.24f, 0.75f)),
                    textColor = new Color(0.78f, 0.78f, 0.78f)
                }
            };

            tagButtonSelectedStyle = new GUIStyle(tagButtonStyle)
            {
                normal = { 
                    background = CommonStyles.CreateSolidColorTexture(new Color(0.25f, 0.53f, 0.82f, 0.85f)),
                    textColor = Color.white
                }
            };

            // 左侧分区按钮：选中=normal贴图；hover/pressed=对应贴图；未选中且非hover时透明
            _sidebarBtnNormalTex = CommonStyles.GenerateButtonSolidStyle.normal.background;
            _sidebarBtnHoverTex = CommonStyles.GenerateButtonSolidStyle.hover.background != null
                ? CommonStyles.GenerateButtonSolidStyle.hover.background
                : _sidebarBtnNormalTex;
            _sidebarBtnPressedTex = CommonStyles.GenerateButtonSolidStyle.active.background != null
                ? CommonStyles.GenerateButtonSolidStyle.active.background
                : _sidebarBtnHoverTex;

            _sidebarSegmentTextStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                font = CommonStyles.SourceHanSansMediumFont != null ? CommonStyles.SourceHanSansMediumFont : CommonStyles.SourceHanSansRegularFont,
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                clipping = TextClipping.Clip,
                wordWrap = false
            };
            _sidebarSegmentTextStyle.normal.textColor = Color.white;
            _sidebarSegmentTextStyle.hover.textColor = Color.white;
            _sidebarSegmentTextStyle.active.textColor = Color.white;
            _sidebarSegmentTextStyle.focused.textColor = Color.white;

            stylesInitialized = true;
        }

        private void OnGUI()
        {
            UIComponents.SyncImguiLeftMouseHeldFromEvent();

            if (Event.current.type == EventType.Repaint)
            {
                // 外层背景（#1E1E1E）
                EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), new Color(30f / 255f, 30f / 255f, 30f / 255f, 1f));
                // 内容区背景（#222222）
                EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), CommonStyles.WindowBackgroundColor);
            }
            
            InitializeStyles();

            Rect sidebarRect = new Rect(0f, 0f, SidebarWidth, position.height);
            Rect rightRect = new Rect(SidebarWidth, 0f, Mathf.Max(0f, position.width - SidebarWidth), position.height);
            Rect rightInnerRect = new Rect(
                rightRect.x + RightPanelPaddingX,
                rightRect.y,
                Mathf.Max(0f, rightRect.width - RightPanelPaddingX * 2f),
                rightRect.height);

            if (Event.current.type == EventType.Repaint)
            {
                // 左侧选择区底色（#2E2E2E）
                EditorGUI.DrawRect(sidebarRect, new Color(46f / 255f, 46f / 255f, 46f / 255f, 1f));
            }

            // 左侧栏
            GUILayout.BeginArea(sidebarRect);
            switch (currentTab)
            {
                case TAB_MODEL:
                    DrawFunctionTagSidebar();
                    break;
                case TAB_TYPE:
                    selectedTypeCategory = DrawTypeStyleSidebar(typeOptions, selectedTypeCategory);
                    break;
                case TAB_STYLE:
                    selectedStyleCategory = DrawTypeStyleSidebar(styleOptions, selectedStyleCategory);
                    break;
                default:
                    EditorGUILayout.BeginVertical(GUILayout.Width(SidebarWidth));
                    EditorGUILayout.EndVertical();
                    break;
            }
            GUILayout.EndArea();

            // 右侧内容区
            GUILayout.BeginArea(rightInnerRect);
            DrawTopSearchBar();
            // 让右侧分类按钮顶部与左侧按钮顶部对齐（79px）
            // 搜索框 top=20、高=44 => bottom=64，需要补 15px
            // float spacer = Mathf.Max(0f, RightVendorButtonsTop - (RightSearchTop + RightSearchHeight));
            float spacer = 20f;
            if (spacer > 0f) GUILayout.Space(spacer);
            float contentUsedHeight = RightSearchTop + RightSearchFixedHeight + spacer;

            if (currentTab == TAB_MODEL)
            {
                DrawVendorTagsRow();
                // 分类按钮区域与下方卡片区域的间距：10（按钮上下边缘空白）+10（间隔）=20
                GUILayout.Space(20f);
                contentUsedHeight += RightVendorRowHeight + 20f;
            }

            float cardsViewHeight = Mathf.Max(120f, rightInnerRect.height - contentUsedHeight - RightStatusBarHeight);

            switch (currentTab)
            {
                case TAB_MODEL:
                    DrawCardsGrid(items: filteredModels, drawCardAction: DrawModelCard, viewHeight: cardsViewHeight);
                    break;
                case TAB_TYPE:
                    DrawTypeStyleCards(typeOptions, ref selectedType, onTypeSelected, selectedTypeCategory, cardsViewHeight);
                    break;
                case TAB_STYLE:
                    DrawTypeStyleCards(styleOptions, ref selectedStyle, onStyleSelected, selectedStyleCategory, cardsViewHeight);
                    break;
                default:
                    GUILayout.Label(TJGeneratorsL10n.L("没有可用的选项"), CommonStyles.SmallGreyLabelStyle);
                    break;
            }
            GUILayout.EndArea();

            // 状态栏横向铺满整个右侧面板（不受右侧内容区内边距影响）
            DrawRightBottomStatusBar(new Rect(
                rightRect.x,
                position.height - RightStatusBarHeight,
                rightRect.width,
                RightStatusBarHeight));
        }

        private string DrawTypeStyleSidebar(List<SelectorOptionConfig> options, string selectedCategory)
        {
            string newSelectedCategory = selectedCategory;
            var categories = new List<string> { AllTag };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var opt in options)
            {
                if (!string.IsNullOrEmpty(opt.category) && seen.Add(opt.category))
                    categories.Add(TJGeneratorsL10n.L(opt.category));
            }
            
            DrawSidebarSegmentButtons(
                categories,
                selectedCategory,
                picked =>
                {
                    newSelectedCategory = picked;
                    Repaint();
                });
            return newSelectedCategory;
        }

        private void DrawTypeStyleCards(List<SelectorOptionConfig> options, ref SelectorOptionConfig selected, Action<SelectorOptionConfig> onSelected, string selectedCategory, float viewHeight)
        {
            if (options == null || options.Count == 0)
            {
                DrawCardsGrid<SelectorOptionConfig>(
                    items: new List<SelectorOptionConfig>(),
                    drawCardAction: null,
                    viewHeight: viewHeight
                );
                return;
            }

            string keyword = (searchText ?? string.Empty).Trim();

            var filteredOptions = options
                .Where(o =>
                    (selectedCategory == AllTag || o.category == selectedCategory) &&
                    MatchesOptionSearch(o, keyword))
                .OrderBy(o => o.order).ThenBy(o => o.name).ToList();

            var currentSelected = selected;

            DrawCardsGrid(
                items: filteredOptions,
                drawCardAction: (option, width) => DrawTypeStyleCard(option, width, currentSelected, (picked) =>
                {
                    currentSelected = picked;
                    onSelected?.Invoke(picked);
                }),
                viewHeight: viewHeight
            );

            selected = currentSelected;
        }

        private void DrawTypeStyleCard(SelectorOptionConfig option, float cardWidth, SelectorOptionConfig selected, Action<SelectorOptionConfig> onSelected)
        {
            var cardData = new OptionCardData(option, selected, onSelected, this);
            DrawCard(cardData, cardWidth);
        }

        /// <summary>
        /// 加载选项图标：优先本地 -> 远程 URL -> 返回 null（由调用方 fallback）
        /// </summary>
        private Texture2D LoadOptionIcon(SelectorOptionConfig option)
        {
            if (!string.IsNullOrEmpty(option.iconPath))
            {
                var localIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(option.iconPath);
                if (localIcon != null)
                {
                    return localIcon;
                }
            }

            if (!string.IsNullOrEmpty(option.iconUrl))
            {
                if (remoteIconCache.TryGetValue(option.iconUrl, out var cachedIcon) && cachedIcon != null)
                {
                    return cachedIcon;
                }

                if (!remoteIconLoading.Contains(option.iconUrl) && !remoteIconFailed.Contains(option.iconUrl))
                {
                    StartDownloadRemoteIcon(option.iconUrl);
                }
            }

            return null;
        }

        /// <summary>
        /// 开始下载远程图标
        /// </summary>
        private void StartDownloadRemoteIcon(string url)
        {
            remoteIconLoading.Add(url);
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                EditorCoroutineUtility.StartCoroutineOwnerless(DownloadRemoteIconCoroutine(url));
            };
        }

        /// <summary>
        /// 下载远程图标协程
        /// </summary>
        private System.Collections.IEnumerator DownloadRemoteIconCoroutine(string url)
        {
            using (var request = UnityEngine.Networking.UnityWebRequestTexture.GetTexture(url))
            {
                request.timeout = 30;
                yield return request.SendWebRequest();

                if (UnityWebRequestCompat.IsSuccess(request))
                {
                    var texture = ((UnityEngine.Networking.DownloadHandlerTexture)request.downloadHandler).texture;
                    if (texture != null)
                    {
                        remoteIconCache[url] = texture;
                        Repaint();
                    }
                    else
                    {
                        remoteIconFailed.Add(url);
                    }
                }
                else
                {
                    TJLog.LogWarning($"[TJGeneratorsModelSelector] 下载图标失败: {url}, 错误: {request.error}");
                    remoteIconFailed.Add(url);
                }
            }

            remoteIconLoading.Remove(url);
        }

        private void DrawFunctionTagSidebar()
        {
            DrawSidebarSegmentButtons(
                functionTagOptions,
                selectedFunctionTag,
                picked =>
                {
                    selectedFunctionTag = picked;
                    ApplyFilters();
                    Repaint();
                });
        }

        private void DrawSidebarSegmentButtons(
            IList<string> labels,
            string selectedLabel,
            Action<string> onPicked)
        {
            if (labels == null || labels.Count == 0)
                return;

            // 位置与尺寸：左20、右17、上79；单个按钮宽159、高44、间距10
            float availableW = Mathf.Max(0f, SidebarWidth - SidebarButtonsLeft - SidebarButtonsRight);
            float w = Mathf.Min(SidebarButtonWidth, availableW);

            for (int i = 0; i < labels.Count; i++)
            {
                string label = labels[i] ?? string.Empty;
                Rect rect = new Rect(
                    SidebarButtonsLeft,
                    SidebarButtonsTop + i * (SidebarButtonHeight + SidebarButtonGap),
                    w,
                    SidebarButtonHeight);

                bool isSelected = string.Equals(selectedLabel, label, StringComparison.OrdinalIgnoreCase);
                bool isHover = rect.Contains(Event.current.mousePosition);

                // 背景：选中用 normal；未选中 hover/pressed 用对应贴图；未选中 idle 透明
                Texture2D bg = null;
                if (isSelected)
                {
                    bg = _sidebarBtnNormalTex;
                }
                else if (isHover)
                {
                    bg = UIComponents.ImguiLeftMouseHeld ? _sidebarBtnPressedTex : _sidebarBtnHoverTex;
                }

                if (bg != null)
                {
                    // 使用统一九宫格（sourceBorder=32, referenceHeight=160），目标边界会按高度缩放
                    // 分区按钮高度 44，更接近 40 的 1/4 目标尺寸；固定 destBorder=8 可避免 round 造成的切线缝
                    UIComponents.DrawNineSliceFixed(rect, bg, 32, NineSliceDestBorder8);
                }

                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
                if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                {
                    onPicked?.Invoke(label);
                    Event.current.Use();
                }

                GUI.Label(rect, label, _sidebarSegmentTextStyle);
            }
        }

        private void DrawTopSearchBar()
        {
            GUILayout.Space(RightSearchTop);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(RightContentLeftPadding);
            string newSearch = UIComponents.DrawSearchTextField(
                searchText,
                TJGeneratorsL10n.L("输入关键词搜索..."),
                GUILayout.Width(RightSearchFixedWidth),
                GUILayout.MinWidth(RightSearchFixedWidth),
                GUILayout.MaxWidth(RightSearchFixedWidth),
                GUILayout.Height(RightSearchFixedHeight));
            if (newSearch != searchText)
            {
                searchText = newSearch;
                ApplyFilters();
            }
            GUILayout.Space(RightContentRightPadding);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawVendorTagsRow()
        {
            if (vendorTagOptions == null || vendorTagOptions.Count == 0)
                return;

            float gap = 10f;
            float baseButtonH = 36f; // padding 10+10 + lineHeight 16
            float minW = 65f;
            float longW = 135f;

            var selectedTextColor = Color.white;
            var unselectedTextColor = new Color(216f / 255f, 216f / 255f, 216f / 255f, 1f);
            var textStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                font = CommonStyles.SourceHanSansRegularFont,
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                clipping = TextClipping.Clip,
                wordWrap = false
            };

            Texture2D black = CommonStyles.BlackBtnNormal4xTexture;
            Texture2D greenN = CommonStyles.GenerateButtonSolidStyle.normal.background;
            Texture2D greenH = CommonStyles.GenerateButtonSolidStyle.hover.background ?? greenN;
            Texture2D greenP = CommonStyles.GenerateButtonSolidStyle.active.background ?? greenH;

            // 这排按钮改为手动排版 + 像素对齐，避免“改一个宽度导致后续按钮坐标漂移，从而踩到接缝区间”
            float rowH = baseButtonH;
            Rect rowRect = GUILayoutUtility.GetRect(0f, rowH, GUILayout.ExpandWidth(true), GUILayout.Height(rowH));
            float ppp = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
            float Snap(float v) => Mathf.Floor(v * ppp) / ppp;

            float x = Snap(rowRect.x + RightContentLeftPadding);
            float y = Snap(rowRect.y);
            float maxX = Snap(rowRect.xMax - RightContentRightPadding);

            foreach (string tag in vendorTagOptions)
            {
                string label = tag ?? string.Empty;
                bool isSelected = string.Equals(selectedVendorTag, label, StringComparison.OrdinalIgnoreCase);
                Vector2 size = GetVendorButtonSize(label, minW, longW, baseButtonH);
                float w = Snap(size.x);
                float buttonH = Snap(size.y);

                // 如果放不下就停止绘制（避免挤压导致 GUILayout 产生不可预期的小数坐标）
                if (x + w > maxX)
                    break;

                Rect rect = new Rect(x, y, w, buttonH);
                bool isHover = rect.Contains(Event.current.mousePosition);
                bool isPressing = isHover && UIComponents.ImguiLeftMouseHeld;

                if (isSelected)
                {
                    var bg = isPressing ? greenP : (isHover ? greenH : greenN);
                    if (bg != null)
                        UIComponents.DrawNineSliceFixed(rect, bg, 32, NineSliceDestBorder8);
                    textStyle.normal.textColor = selectedTextColor;
                }
                else
                {
                    // 与左侧一致：未选中时 hover/pressed 也使用绿色状态贴图；idle 保持黑色底
                    var bg = isPressing ? greenP : (isHover ? greenH : black);
                    if (bg != null)
                    {
                        // 同步 border：黑/绿按钮同尺寸，采用相同九宫格参数
                        UIComponents.DrawNineSliceFixed(rect, bg, 32, NineSliceDestBorder8);
                    }
                    textStyle.normal.textColor = unselectedTextColor;
                }

                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
                if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                {
                    selectedVendorTag = label;
                    ApplyFilters();
                    Repaint();
                    Event.current.Use();
                }

                GUI.Label(rect, label, textStyle);
                x = Snap(x + w + gap);
            }
        }

        private static Vector2 GetVendorButtonSize(string label, float minW, float longW, float baseH)
        {
            float w = (label != null && label.Length >= 10) ? longW : minW;
            float h = baseH;

            if (!string.IsNullOrEmpty(label) && VendorButtonSizeDeltaByLabel.TryGetValue(label, out var delta))
            {
                w += delta.x;
                h += delta.y;
            }

            // 安全夹紧，避免出现负尺寸
            w = Mathf.Max(1f, w);
            h = Mathf.Max(1f, h);
            return new Vector2(w, h);
        }


        private void DrawTagButton(string tag, bool isFunctionTag)
        {
            bool isSelected = string.Equals(isFunctionTag ? selectedFunctionTag : selectedVendorTag, tag, StringComparison.OrdinalIgnoreCase);
            if (GUILayout.Button(tag, isSelected ? tagButtonSelectedStyle : tagButtonStyle, GUILayout.Height(22f)))
            {
                if (isFunctionTag)
                    selectedFunctionTag = tag;
                else
                    selectedVendorTag = tag;
                ApplyFilters();
            }
        }

        /// <summary>
        /// 通用的卡片网格绘制方法（使用自动布局）
        /// </summary>
        private void DrawCardsGrid<T>(List<T> items, Action<T, float> drawCardAction, float viewHeight)
        {
            scrollPosition = GUILayout.BeginScrollView(
                scrollPosition,
                false,
                true,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUILayout.Height(viewHeight));

            if (items == null || items.Count == 0)
            {
                GUILayout.Label(TJGeneratorsL10n.L("当前分类下没有选项"), CommonStyles.SmallGreyCenterLabelStyle);
                GUILayout.EndScrollView();
                return;
            }

            // 卡片尺寸严格固定，不随窗口拉伸变化；左右与搜索框/分类按钮对齐（左20，右25）
            float availableWidth = Mathf.Max(
                CardMinWidth,
                position.width - SidebarWidth - RightPanelPaddingX * 2f - RightContentLeftPadding - RightContentRightPadding);
            int cardsPerRow = Mathf.Max(1, Mathf.FloorToInt((availableWidth + CardGap) / (CardMinWidth + CardGap)));

            int index = 0;
            while (index < items.Count)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(RightContentLeftPadding);
                int rowCount = Mathf.Min(cardsPerRow, items.Count - index);
                for (int i = 0; i < rowCount; i++)
                {
                    drawCardAction?.Invoke(items[index++], CardMinWidth);
                    if (i < rowCount - 1)
                        GUILayout.Space(CardGap);
                }
                GUILayout.FlexibleSpace();
                GUILayout.Space(RightContentRightPadding);
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(CardGap);
            }

            GUILayout.EndScrollView();
        }

        private void DrawRightBottomStatusBar(Rect rect)
        {
            EditorGUI.DrawRect(rect, RightStatusBarColor);

            int totalCount = 0;
            int pinnedCount = 0;
            if (currentTab == TAB_MODEL)
            {
                totalCount = filteredModels?.Count ?? 0;
                pinnedCount = filteredModels?.Count(model => model != null && model.IsPinned) ?? 0;
            }
            else if (currentTab == TAB_TYPE)
            {
                totalCount = GetFilteredOptionCount(typeOptions, selectedTypeCategory);
            }
            else if (currentTab == TAB_STYLE)
            {
                totalCount = GetFilteredOptionCount(styleOptions, selectedStyleCategory);
            }

            string text = currentTab == TAB_MODEL
                ? TJGeneratorsL10n.L("共{0}个模型 ｜已收藏{1}个", totalCount, pinnedCount)
                : TJGeneratorsL10n.L("共{0}个选项", totalCount);

            var style = new GUIStyle(EditorStyles.label)
            {
                font = CommonStyles.SourceHanSansMediumFont != null ? CommonStyles.SourceHanSansMediumFont : CommonStyles.SourceHanSansRegularFont,
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft
            };
            style.normal.textColor = new Color(128f / 255f, 128f / 255f, 128f / 255f, 1f);

            float padX = 14f;
            float padW = 28f;
            Rect textRect = new Rect(rect.x + padX, rect.y, Mathf.Max(1f, rect.width - padW), rect.height);
            GUI.Label(textRect, text.ToUpperInvariant(), style);
        }

        private int GetFilteredOptionCount(List<SelectorOptionConfig> options, string selectedCategory)
        {
            if (options == null || options.Count == 0)
                return 0;
            string keyword = (searchText ?? string.Empty).Trim();
            return options.Count(o =>
                o != null &&
                (selectedCategory == AllTag || o.category == selectedCategory) &&
                MatchesOptionSearch(o, keyword));
        }

        /// <summary>
        /// 多行自动换行后限制在最多 <paramref name="maxLines"/> 行高度内，超出则在末尾加「...」。
        /// </summary>
        private static string ClampWrappedTextWithEllipsis(string raw, GUIStyle style, float width, int maxLines)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;
            string text = raw.ToUpperInvariant();
            float lineH = style.lineHeight;
            if (lineH < 0.01f)
                lineH = Mathf.Max(8f, style.fontSize * 1.2f);
            float maxH = lineH * maxLines + 0.51f;
            if (style.CalcHeight(new GUIContent(text), width) <= maxH)
                return text;
            const string suffix = "...";
            int lo = 0;
            int hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                string candidate = text.Substring(0, mid) + suffix;
                float h = style.CalcHeight(new GUIContent(candidate), width);
                if (h <= maxH)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            if (lo <= 0)
                return suffix;
            return text.Substring(0, lo).TrimEnd() + suffix;
        }

        /// <summary>
        /// 通用的卡片绘制方法（使用自动布局）
        /// </summary>
        private void DrawCard(ICardData cardData, float cardWidth)
        {
            bool isSelected = cardData.IsSelected;
            Rect cardRect = GUILayoutUtility.GetRect(cardWidth, CardHeight, GUILayout.Width(cardWidth), GUILayout.Height(CardHeight));

            float padX = 14f;
            float padW = 28f;
            float favInsetRight = 34f;
            float favTop = 7f;
            float favW = 17.5f;
            float favH = 16.64f;
            float iconW = 78f;
            float iconH = 67f;
            float iconTop = 42f;
            float iconShadowDy = 2f;
            float titleTop = 120f;
            float titleLineH = 20f;
            float descTop = 148f;
            float tagGap = 4f;
            float tagLineH = 20f;
            int cardNineDestBorder = Mathf.Max(1, Mathf.Max(0, Mathf.RoundToInt(4f)));

            Texture2D cardBg = isSelected ? CommonStyles.ItemBoxChecked4xTexture : CommonStyles.ItemBoxNormal4xTexture;
            if (cardBg != null)
                UIComponents.DrawNineSliceFixed(cardRect, cardBg, 16, cardNineDestBorder);
            else
                EditorGUI.DrawRect(cardRect, new Color(26f / 255f, 26f / 255f, 26f / 255f, 1f));

            // 收藏图标（右上）
            Rect favRect = new Rect(cardRect.xMax - favInsetRight, cardRect.y + favTop, favW, favH);
            bool showFav = cardData.IsPinned || cardData.CanTogglePin;
            if (showFav)
            {
                Texture2D favTex = cardData.IsPinned ? CommonStyles.FavoriteIconChecked4xTexture : CommonStyles.FavoriteIconNormal4xTexture;
                if (favTex != null)
                    GUI.DrawTexture(favRect, favTex, ScaleMode.ScaleToFit, true);
                else
                    GUI.Label(favRect, cardData.IsPinned ? "\u2605" : "\u2606", CommonStyles.PinIconStyle);

                if (cardData.CanTogglePin && GUI.Button(favRect, GUIContent.none, GUIStyle.none))
                {
                    cardData.OnPinToggled();
                    Event.current.Use();
                    return;
                }
            }

            // 中部模型图标（约 78×67）
            Texture2D icon = cardData.Icon;
            Rect iconRect = new Rect(cardRect.x + (cardRect.width - iconW) * 0.5f, cardRect.y + iconTop, iconW, iconH);
            if (icon != null)
            {
                Rect shadowRect = new Rect(iconRect.x, iconRect.y + iconShadowDy, iconRect.width, iconRect.height);
                EditorGUI.DrawRect(shadowRect, new Color(0f, 0f, 0f, 0.25f));
                GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit, true);
            }
            else
            {
                UIComponents.DrawModelIconPlaceholder(iconRect, cardData.Name);
            }

            // 文本样式（按新设计）
            var titleStyle = new GUIStyle(CommonStyles.ModelNameStyle)
            {
                fontSize = 14,
                font = CommonStyles.SourceHanSansMediumFont != null ? CommonStyles.SourceHanSansMediumFont : CommonStyles.SourceHanSansRegularFont,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft
            };
            titleStyle.normal.textColor = Color.white;

            var descStyle = new GUIStyle(CommonStyles.SmallGreyLeftLabelStyle)
            {
                fontSize = 10,
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                clipping = TextClipping.Clip
            };
            descStyle.normal.textColor = new Color(128f / 255f, 128f / 255f, 128f / 255f, 1f);

            float descLineH = descStyle.lineHeight > 0.01f ? descStyle.lineHeight : Mathf.Max(8f, descStyle.fontSize * 1.2f);
            float descBlockH = descLineH * 2f;
            float tagTop = descTop + descBlockH + tagGap;

            var tagStyle = new GUIStyle(CommonStyles.SmallGreenLeftLabelStyle)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleLeft
            };

            float textW = Mathf.Max(1f, cardRect.width - padW);
            Rect titleRect = new Rect(cardRect.x + padX, cardRect.y + titleTop, textW, titleLineH);
            Rect descRect = new Rect(cardRect.x + padX, cardRect.y + descTop, textW, descBlockH);
            Rect tagRect = new Rect(cardRect.x + padX, cardRect.y + tagTop, textW, tagLineH);

            GUI.Label(titleRect, (cardData.Name ?? string.Empty).ToUpperInvariant(), titleStyle);
            string descShown = ClampWrappedTextWithEllipsis(cardData.Description ?? string.Empty, descStyle, textW, 2);
            GUI.Label(descRect, descShown, descStyle);
            if (!string.IsNullOrEmpty(cardData.ExtraInfo))
                GUI.Label(tagRect, cardData.ExtraInfo.ToUpperInvariant(), tagStyle);

            // 卡片点击选择（排除收藏图标区域）
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && cardRect.Contains(Event.current.mousePosition))
            {
                if (!favRect.Contains(Event.current.mousePosition))
                {
                    cardData.OnSelected();
                    Event.current.Use();
                }
            }
        }

        private void DrawModelCard(AIModelInfo model, float cardWidth)
        {
            var cardData = new ModelCardData(model, selectedModel, this);
            DrawCard(cardData, cardWidth);
        }

        private static string BuildTagSummary(AIModelInfo model)
        {
            List<string> tags = (model.FunctionTags ?? Array.Empty<string>())
                .Concat(model.VendorTags ?? Array.Empty<string>())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => TJGeneratorsL10n.L(tag.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (tags.Count == 0)
            {
                return string.Empty;
            }

            if (tags.Count > 3)
            {
                return string.Join(" / ", tags.Take(3)) + " ...";
            }

            return string.Join(" / ", tags);
        }
    }
}
#endif
