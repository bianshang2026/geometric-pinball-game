#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using TJGenerators.Config;
using TJGenerators.Generators;
using TJGenerators.Pipeline;
using TJGenerators.UI;
using TJGenerators.Utils;
using UnityEditor;
using UnityEngine;

namespace TJGenerators
{
    /// <summary>
    /// TJGenerators 图片生成窗口（文生图 / 图生图）。
    /// </summary>
    public class TJGeneratorsImageWindow : TJGeneratorsAssetWindowBase
    {
        // ========== 固定配置 ==========
        protected override ConfigType WindowConfigType => ConfigType.Image;
        protected override string LogTag => "[TJGeneratorsImage]";

        protected override string TargetHeaderLabel => TJGeneratorsL10n.L("目标图片");
        protected override string UnboundTargetLabel => TJGeneratorsL10n.L("未绑定（生成时自动创建）");
        protected override string EmptyGeneratorsMessage =>
            TJGeneratorsL10n.L("未找到可用的图片生成器，请检查配置");
        protected override string HistoryApplyLabel => TJGeneratorsL10n.L("应用到当前图片");
        protected override string PromptControlName => "image_prompt_input";

        private readonly List<string> referenceImagePaths = new List<string>();
        private readonly List<Texture2D> referenceUploadedImages = new List<Texture2D>();

        private static readonly Dictionary<string, TJGeneratorsImageWindow> s_openWindows =
            new Dictionary<string, TJGeneratorsImageWindow>();

        private Texture2D imagePreviewTexture;

        /// <summary>outputType 为 image 且配置启用时可选；prompt 经 DynamicRequestJsonBuilder.BuildEnhancedPrompt 拼为前缀</summary>
        private MaterialTemplateOptionConfig selectedPromptTemplate;

        private const string UnityTerrainHeightmapTemplateId = "unity_terrain_heightmap";

        /// <summary>Qwen 图片分层 generator id（numLayers 参数控制层数）</summary>
        public const string LayeringGeneratorId = "image-layering";

        /// <summary>Seedream 5.0 Pro 图片分层 generator id（自动分层，底图 + 最多 16 层）</summary>
        public const string SeedreamLayeringGeneratorId = "seedream-image-layering";

        /// <summary>是否为图片分层类 generator（多张 RGBA PNG 输出，走 ImageLayers_ 占位与兄弟图层导入）</summary>
        public static bool IsLayeringGenerator(string generatorId)
        {
            if (string.IsNullOrEmpty(generatorId)) return false;
            return string.Equals(generatorId, LayeringGeneratorId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(generatorId, SeedreamLayeringGeneratorId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>是否为 Seedream 自动分层 generator（无 numLayers 参数，层数由模型决定）</summary>
        public static bool IsSeedreamLayeringGenerator(string generatorId)
        {
            return string.Equals(generatorId, SeedreamLayeringGeneratorId, StringComparison.OrdinalIgnoreCase);
        }

        [SerializeField]
        private bool terrainHeightmapGaussianBlur = true;

        [SerializeField]
        private bool terrainHeightmapMedian3x3 = true;

        [SerializeField]
        [Range(0.5f, 3f)]
        private float terrainHeightmapBlurSigma = 1.2f;

        [SerializeField]
        private bool terrainHeightmapRemapFoldout = true;

        [SerializeField]
        private bool terrainHeightmapPercentileNormalize = true;

        [SerializeField]
        [Range(0f, 0.2f)]
        private float terrainHeightmapPercentileLow = 0.05f;

        [SerializeField]
        [Range(0.8f, 1f)]
        private float terrainHeightmapPercentileHigh = 0.95f;

        [SerializeField]
        [Range(0.35f, 2.5f)]
        private float terrainHeightmapHeightGamma = 1f;

        [SerializeField]
        [Range(0f, 1f)]
        private float terrainHeightmapRemapOutMin = 0.02f;

        [SerializeField]
        [Range(0f, 1f)]
        private float terrainHeightmapRemapOutMax = 0.98f;

        // ========== 静态入口 ==========
        public static void ShowWindow()
        {
            var rect = GetDefaultMainWindowRect();
            var window = GetWindowWithRect<TJGeneratorsImageWindow>(
                rect,
                utility: false,
                title: TJGeneratorsL10n.L("TJGenerators 图片生成"),
                focus: true
            );
            window.titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 图片生成"));
            FinalizeMainWindowShow(window, rect);
        }

        public static void OpenForAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                ShowWindow();
                return;
            }

            if (!IsSupportedImageAssetPath(assetPath))
            {
                ErrorDialogUtils.ShowErrorDialog(
                    TJGeneratorsL10n.L("TJGenerators 图片生成"),
                    TJGeneratorsL10n.L("仅支持绑定 .jpg / .jpeg / .png 的图片资产。\r\n\r\n建议先创建「生成图片」新资产。"),
                    "[TJGeneratorsImage]"
                );
                return;
            }

            GenerationWindowBase.OpenForAsset(
                assetPath,
                s_openWindows,
                "[TJGeneratorsImage]",
                TJGeneratorsL10n.L("TJGenerators 图片 - {0}"),
                () =>
                {
                    var window = CreateInstance<TJGeneratorsImageWindow>();
                    return window;
                },
                (w, r) => w._targetAsset = r,
                ShowWindow);
        }

        private static bool IsSupportedImageAssetPath(string assetPath) =>
            assetPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || assetPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

        // ========== 生命周期钩子 ==========

        protected override void RegisterInOpenWindows()
        {
            if (_targetAsset != null && !string.IsNullOrEmpty(_targetAsset.guid))
                s_openWindows[_targetAsset.guid] = this;
        }

        protected override void UnregisterFromOpenWindows()
        {
            if (_targetAsset != null && !string.IsNullOrEmpty(_targetAsset.guid))
                s_openWindows.Remove(_targetAsset.guid);
        }

        protected override void OnDisableClearSubclassResources()
        {
            imagePreviewTexture = null;

            foreach (var tex in referenceUploadedImages)
            {
                if (tex != null)
                    DestroyImmediate(tex);
            }
            referenceUploadedImages.Clear();
            referenceImagePaths.Clear();
        }

        protected override void OnBeforeDrawChrome(UIComponents.FixedSplitLayoutParams _)
        {
            maxSize = new Vector2(10000f, 10000f);
        }

        protected override void ResetInputStateAfterModelChange()
        {
            var config = GetCurrentGeneratorConfig();
            ResetTextPromptIfHidden(config, ref textPrompt);
            ClearReferenceImagesWhenUploadHidden(config, referenceImagePaths, referenceUploadedImages);
        }

        protected override void OnModelSelectedBase(AIModelInfo model)
        {
            base.OnModelSelectedBase(model);
            selectedPromptTemplate = null;
            if (_currentGenerator is DynamicGenerator dg)
                dg.SetPromptTemplateSelection(null);
            UploadImageComponents.TrimReferenceImagesToMax(
                referenceImagePaths,
                referenceUploadedImages,
                GetMaxReferenceImages());
        }

        // ========== UI ==========

        protected override void DrawLeftPanelBody()
        {
            DrawInputSection();
            GUILayout.Space(CommonStyles.Space3);
            DrawConfigurationSection();
            GUILayout.Space(CommonStyles.Space3);
            DrawTerrainHeightmapAfterGenerationSection();
            GUILayout.Space(CommonStyles.Space3);
        }

        protected override bool CanStartGeneration
        {
            get
            {
                if (_currentGenerator == null || string.IsNullOrWhiteSpace(textPrompt))
                    return false;
                var layout = GetCurrentGeneratorConfig()?.uiLayout;
                if (layout != null && layout.imageUploadRequired && referenceImagePaths.Count == 0)
                    return false;
                return true;
            }
        }

        private void ShowPromptTemplateSelectorWindow()
        {
            var cfg = GetCurrentGeneratorConfig();
            if (cfg?.promptTemplateSelector?.options == null || cfg.promptTemplateSelector.options.Count == 0)
            {
                ErrorDialogUtils.ShowErrorDialog(
                    TJGeneratorsL10n.L("提示词模板不可用"),
                    TJGeneratorsL10n.L("当前模型未配置提示词模板选项（options 为空）"),
                    LogTag
                );
                return;
            }

            TJGeneratorsMaterialTemplateSelectorWindow.ShowWindow(
                cfg.promptTemplateSelector.options,
                OnPromptTemplateSelected,
                string.IsNullOrEmpty(cfg.promptTemplateSelector.title)
                    ? TJGeneratorsL10n.L("选择提示词")
                    : TJGeneratorsL10n.L(cfg.promptTemplateSelector.title),
                showPreviewThumbnails: false
            );
        }

        private void OnPromptTemplateSelected(MaterialTemplateOptionConfig template)
        {
            selectedPromptTemplate = template;

            if (_currentGenerator is DynamicGenerator dg)
                dg.SetPromptTemplateSelection(selectedPromptTemplate);

            Repaint();
        }

        private void DrawPromptTemplateSelector()
        {
            var cfg = GetCurrentGeneratorConfig();
            if (cfg?.promptTemplateSelector == null
                || !cfg.promptTemplateSelector.enabled
                || cfg.promptTemplateSelector.options == null
                || cfg.promptTemplateSelector.options.Count == 0)
            {
                return;
            }

            string title = string.IsNullOrEmpty(cfg.promptTemplateSelector.title)
                ? TJGeneratorsL10n.L("提示词模板")
                : TJGeneratorsL10n.L(cfg.promptTemplateSelector.title);

            UIComponents.DrawSelectionRow(
                title,
                TJGeneratorsL10n.L("选择提示词"),
                CommonStyles.DropBoxRightArrow4xTexture,
                ShowPromptTemplateSelectorWindow,
                TJGeneratorsL10n.L(selectedPromptTemplate?.name));

            GUILayout.Space(CommonStyles.Space3);
        }

        protected override void DrawInputSection()
        {
            DrawPromptTemplateSelector();

            var genConfig = GetCurrentGeneratorConfig();
            textPrompt = DrawConfiguredTextPromptInput(textPrompt, PromptControlName, genConfig);

            if (ShouldShowImageUpload(genConfig))
            {
                GUILayout.Space(CommonStyles.Space3);
                DrawReferenceImagesSection();
            }
        }

        private void DrawReferenceImagesSection()
        {
            DrawConfiguredReferenceImageUpload(
                referenceImagePaths,
                referenceUploadedImages,
                "image_reference_upload");
        }

        protected override void DrawConfigurationSection()
        {
            var provider = _currentGenerator as IGeneratorParameterProvider;

            var allParams = GetCurrentGeneratorParameters();
            List<ParameterConfig> filteredParams = null;
            if (allParams != null && allParams.Count > 0)
            {
                filteredParams = new List<ParameterConfig>(allParams.Count);
                for (int i = 0; i < allParams.Count; i++)
                {
                    var p = allParams[i];
                    if (p == null || string.IsNullOrEmpty(p.id))
                        continue;

                    if (p.id == "isSegmentation" || p.id == "qValue" || p.id == "resizeWidth")
                        continue;

                    filteredParams.Add(p);
                }
            }

            showAdvancedSettings = DrawConfiguredAdvancedSettingsFoldout(
                showAdvancedSettings,
                provider,
                filteredParams
            );
        }

        private bool IsUnityTerrainHeightmapTemplateSelected()
        {
            return string.Equals(
                selectedPromptTemplate?.id,
                UnityTerrainHeightmapTemplateId,
                StringComparison.OrdinalIgnoreCase
            );
        }

        /// <summary>地形模板：后处理选项与「一键生成地形」位于生成按钮下方，顺序为「生成 → 后处理设置 → 建地形」。</summary>
        private void DrawTerrainHeightmapAfterGenerationSection()
        {
            if (!IsUnityTerrainHeightmapTemplateSelected())
                return;

            GUILayout.Label(TJGeneratorsL10n.L("地形高度图（生成后）"), CommonStyles.HeaderStyle);
            GUILayout.Space(6);

            GUILayout.Label(
                TJGeneratorsL10n.L("在右侧历史记录中选中对应 PNG 后，应用后处理并创建场景地形。"),
                CommonStyles.SmallGreyLabelStyle
            );
            GUILayout.Space(8);

            terrainHeightmapMedian3x3 = EditorGUILayout.ToggleLeft(
                TJGeneratorsL10n.L("后处理：Median 3x3 去尖刺（散点离群点）"),
                terrainHeightmapMedian3x3
            );

            GUILayout.Space(4);
            terrainHeightmapGaussianBlur = EditorGUILayout.ToggleLeft(
                TJGeneratorsL10n.L("后处理：高斯模糊平滑"),
                terrainHeightmapGaussianBlur
            );
            if (terrainHeightmapGaussianBlur)
            {
                EditorGUI.indentLevel++;
                terrainHeightmapBlurSigma = EditorGUILayout.Slider(
                    TJGeneratorsL10n.L("模糊强度 (σ)"),
                    terrainHeightmapBlurSigma,
                    0.5f,
                    3f
                );
                EditorGUI.indentLevel--;
            }

            GUILayout.Space(8);
            terrainHeightmapRemapFoldout = EditorGUILayout.Foldout(
                terrainHeightmapRemapFoldout,
                TJGeneratorsL10n.L("高度重映射（类似 Terrain Tools · Height Remap）"),
                true
            );
            if (terrainHeightmapRemapFoldout)
            {
                EditorGUI.indentLevel++;
                terrainHeightmapPercentileNormalize = EditorGUILayout.ToggleLeft(
                    TJGeneratorsL10n.L("百分位拉伸（去掉极暗/极亮离群点再起有效对比）"),
                    terrainHeightmapPercentileNormalize
                );
                EditorGUI.BeginDisabledGroup(!terrainHeightmapPercentileNormalize);
                terrainHeightmapPercentileLow = EditorGUILayout.Slider(
                    new GUIContent(
                        TJGeneratorsL10n.L("低端截断"),
                        TJGeneratorsL10n.L("低于该百分位的亮度视作海平面一端，类似压低海底噪声")
                    ),
                    terrainHeightmapPercentileLow,
                    0f,
                    0.2f
                );
                terrainHeightmapPercentileHigh = EditorGUILayout.Slider(
                    new GUIContent(
                        TJGeneratorsL10n.L("高端截断"),
                        TJGeneratorsL10n.L("高于该百分位的亮度视作山顶一端")
                    ),
                    terrainHeightmapPercentileHigh,
                    0.8f,
                    1f
                );
                EditorGUI.EndDisabledGroup();
                if (terrainHeightmapPercentileHigh <= terrainHeightmapPercentileLow)
                    terrainHeightmapPercentileHigh =
                        Mathf.Min(1f, terrainHeightmapPercentileLow + 0.02f);

                terrainHeightmapHeightGamma = EditorGUILayout.Slider(
                    new GUIContent(
                        TJGeneratorsL10n.L("高度曲线 Gamma"),
                        TJGeneratorsL10n.L("1 = 线性；小于 1 中间调抬高（更陡）；大于 1 更平（更多平原）")
                    ),
                    terrainHeightmapHeightGamma,
                    0.35f,
                    2.5f
                );

                EditorGUILayout.LabelField(
                    TJGeneratorsL10n.L("输出垂直范围（归一化高度映射到 [最低, 最高]）"),
                    CommonStyles.SmallGreyLabelStyle
                );
                terrainHeightmapRemapOutMin = EditorGUILayout.Slider(
                    new GUIContent(TJGeneratorsL10n.L("输出最低"), TJGeneratorsL10n.L("地形最凹处对应高度图灰度下限")),
                    terrainHeightmapRemapOutMin,
                    0f,
                    1f
                );
                terrainHeightmapRemapOutMax = EditorGUILayout.Slider(
                    new GUIContent(TJGeneratorsL10n.L("输出最高"), TJGeneratorsL10n.L("地形最高处对应高度图灰度上限")),
                    terrainHeightmapRemapOutMax,
                    0f,
                    1f
                );
                if (terrainHeightmapRemapOutMax <= terrainHeightmapRemapOutMin)
                    terrainHeightmapRemapOutMax =
                        Mathf.Min(1f, terrainHeightmapRemapOutMin + 0.02f);

                EditorGUI.indentLevel--;
            }

            GUILayout.Space(10);

            var selectedHistoryItem =
                selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count
                    ? generationHistory[selectedHistoryIndex]
                    : null;
            bool canTerrain =
                CanGenerateTerrainFromHistoryItem(selectedHistoryItem);
            EditorGUI.BeginDisabledGroup(!canTerrain);
            if (GUILayout.Button(TJGeneratorsL10n.L("一键生成地形"), GUILayout.Height(28)))
                GenerateTerrainFromHeightmap(selectedHistoryIndex);
            EditorGUI.EndDisabledGroup();

            if (!canTerrain)
            {
                GUILayout.Space(4);
                GUILayout.Label(
                    TJGeneratorsL10n.L("请先在历史中选中由本模板生成的已完成 PNG。"),
                    CommonStyles.SmallGreyLabelStyle
                );
            }
        }

        protected override void DrawHistoryPanel(float panelWidth)
        {
            DrawStandardHistoryPanel(panelWidth, new StandardHistoryPanelOptions
            {
                DrawLargePreviewBlock = DrawImageHistoryLargePreview,
                ScrollTopSpacing = 12f,
                BottomMargin = 90f,
                HistoryContentWidth = CommonStyles.HistoryScrollViewLayoutWidth(panelWidth),
                DrawTilePreview = DrawImageHistoryPreview,
                GetPrimaryLabel = GetHistoryUserPromptLabel,
                GetModelLabel = item => GetModelDisplayLabelFromIndex(item.modelVersion),
                ShowContextMenu = ShowHistoryContextMenu,
                DrawHistoryActions = () => DrawDefaultHistoryActions(),
            });
        }

        private float DrawImageHistoryLargePreview(float panelWidth, float historyPanelHeight)
        {
            Texture2D historyPreviewTex = null;
            bool showHistoryPreview = false;
            if (selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count)
            {
                var selectedItem = generationHistory[selectedHistoryIndex];
                if (!selectedItem.isGenerating)
                {
                    showHistoryPreview = true;
                    historyPreviewTex = GetPreviewTextureForHistoryItem(selectedItem);
                }
            }

            if (historyPreviewTex == null)
                historyPreviewTex = imagePreviewTexture;

            return UIComponents.DrawHistoryTexturePreview(
                historyPreviewTex,
                showHistoryPreview || historyPreviewTex != null,
                isVerticalLayout,
                panelWidth,
                historyPanelHeight);
        }

        private void DrawImageHistoryPreview(Rect rect, TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null || item.isGenerating)
            {
                UIComponents.DrawLoadingSpinner(rect, CommonStyles.SmallGreyLabelStyle, Repaint);
                return;
            }

            if (!string.IsNullOrEmpty(item.modelPath))
            {
                if (
                    historyPreviewCache.TryGetValue(item.modelPath, out var cached)
                    && cached != null
                )
                {
                    GUI.DrawTexture(rect, cached, ScaleMode.ScaleToFit);
                    return;
                }

                var assetTex = AssetDatabase.LoadAssetAtPath<Texture2D>(item.modelPath);
                if (assetTex != null)
                {
                    historyPreviewCache[item.modelPath] = assetTex;
                    GUI.DrawTexture(rect, assetTex, ScaleMode.ScaleToFit);
                    return;
                }

                string absPath = PathUtils.ToAbsoluteAssetPath(item.modelPath);
                if (File.Exists(absPath))
                {
                    // 异步加载本地预览图到缓存，避免OnGUI卡顿
                    EnqueuePreviewLoad(item.modelPath, absPath, false);
                }
            }

            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f, 1f));
            var iconRect = new Rect(
                rect.x + rect.width / 4,
                rect.y + rect.height / 4,
                rect.width / 2,
                rect.height / 2
            );
            GUI.Label(iconRect, EditorGUIUtility.IconContent("d_Texture2D Icon"));
        }

        private Texture2D GetPreviewTextureForHistoryItem(TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null || item.isGenerating)
                return null;

            if (!string.IsNullOrEmpty(item.modelPath))
            {
                if (
                    historyPreviewCache.TryGetValue(item.modelPath, out var cached)
                    && cached != null
                )
                    return cached;

                var assetTex = AssetDatabase.LoadAssetAtPath<Texture2D>(item.modelPath);
                if (assetTex != null)
                {
                    historyPreviewCache[item.modelPath] = assetTex;
                    return assetTex;
                }

                string absPath = PathUtils.ToAbsoluteAssetPath(item.modelPath);
                if (File.Exists(absPath))
                {
                    EnqueuePreviewLoad(item.modelPath, absPath, false);
                }
            }

            // 可选：如果历史项已经有 URL 预览缓存，也可以复用
            if (
                item.isTextToModel
                && !string.IsNullOrEmpty(item.previewImageUrl)
                && urlPreviewCache.TryGetValue(item.previewImageUrl, out var urlTex)
                && urlTex != null
            )
            {
                return urlTex;
            }

            return null;
        }

        private static string GetHistoryUserPromptLabel(TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null)
                return "";
            return TJGenerators.Utils.TJGeneratorsPromptDisplay.FormatHistoryTileLabel(item.GetUserFacingPrompt());
        }

        private void ShowHistoryContextMenu(int index)
        {
            if (index < 0 || index >= generationHistory.Count)
                return;
            var item = generationHistory[index];
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent(HistoryApplyLabel), false, () => ApplyHistoryToAsset(index));
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在项目中显示")), false, () => ShowHistoryInProject(index));

            if (CanGenerateTerrainFromHistoryItem(item))
                menu.AddItem(
                    new GUIContent(TJGeneratorsL10n.L("一键生成地形")),
                    false,
                    () => GenerateTerrainFromHeightmap(index)
                );

            menu.AddSeparator("");

            if (!string.IsNullOrEmpty(item.modelPath))
                menu.AddItem(
                    new GUIContent(TJGeneratorsL10n.L("在资源管理器中显示")),
                    false,
                    () => EditorUtility.RevealInFinder(item.modelPath)
                );

            menu.AddSeparator("");

            menu.AddItem(
                new GUIContent(TJGeneratorsL10n.L("从历史记录中移除")),
                false,
                () =>
                {
                    TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                    RefreshHistory();
                    if (generationHistory.Count == 0)
                        selectedHistoryIndex = -1;
                    else if (selectedHistoryIndex >= generationHistory.Count)
                        selectedHistoryIndex = Mathf.Max(0, generationHistory.Count - 1);
                    Repaint();
                }
            );

            menu.ShowAsContext();
        }

        protected override void ApplyHistoryToAsset(int index)
        {
            if (index < 0 || index >= generationHistory.Count)
                return;
            var item = generationHistory[index];

            if (item.isGenerating)
            {
                Debug.LogWarning($"{LogTag} {TJGeneratorsL10n.L("请等待该条生成完成后再应用。")}");
                return;
            }

            if (
                string.IsNullOrEmpty(item.modelPath)
                || !File.Exists(PathUtils.ToAbsoluteAssetPath(item.modelPath))
            )
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("该历史记录的图片文件不存在。"), LogTag);
                if (!string.IsNullOrEmpty(item.modelPath))
                    TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                RefreshHistory();
                Repaint();
                return;
            }

            if (_targetAsset == null || !_targetAsset.IsValid())
            {
                Debug.LogWarning($"{LogTag} {TJGeneratorsL10n.L("请先绑定或创建目标图片资产。")}");
                return;
            }

            string srcExt = string.IsNullOrEmpty(item.modelPath) ? ".png" : Path.GetExtension(item.modelPath);
            if (string.IsNullOrEmpty(srcExt)) srcExt = ".png";
            string targetPathForDialog = Path.ChangeExtension(_targetAsset.GetPath(), srcExt);
            if (
                !EditorUtility.DisplayDialog(
                    TJGeneratorsL10n.L("确认替换"),
                    string.Format(TJGeneratorsL10n.L("确定将选中的图片应用到当前目标「{0}」吗？"), Path.GetFileNameWithoutExtension(targetPathForDialog)),
                    TJGeneratorsL10n.L("确定"),
                    TJGeneratorsL10n.L("取消")
                )
            )
            {
                return;
            }

            if (!ReplaceTargetImageFromSource(item.modelPath, TJGeneratorsL10n.L("已将历史图片应用到"), out string err))
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), string.IsNullOrEmpty(err) ? TJGeneratorsL10n.L("应用失败（详见控制台）。") : string.Format(TJGeneratorsL10n.L("应用失败: {0}"), err), LogTag);
            else
                RefreshHistory();

            Repaint();
        }

        /// <summary>
        /// 将源图片复制到当前目标资产；若扩展名变化则删除旧占位文件并更新 GUID / 历史记录，
        /// 与生成完成回调 <see cref="OnAssetSaved"/> 保持一致，避免同基名下残留 .jpg 与 .png 两个文件。
        /// </summary>
        private bool ReplaceTargetImageFromSource(string sourceAssetPath, string okLogVerb, out string errorMessage)
        {
            return TargetImageReplaceHelper.ReplaceTargetImageFromSource(
                sourceAssetPath,
                okLogVerb,
                LogTag,
                ref _targetAsset,
                ref imagePreviewTexture,
                historyPreviewCache,
                ext =>
                {
                    EnsureTargetImage(ext);
                    return _targetAsset;
                },
                TargetImageReplaceHelper.ConfigureDefaultTexture,
                OnTargetImageExtensionChanged,
                releaseExtraHandles: null,
                out errorMessage);
        }

        private void OnTargetImageExtensionChanged(string oldTargetGuid, string newTargetPath)
        {
            if (!string.IsNullOrEmpty(oldTargetGuid))
                s_openWindows.Remove(oldTargetGuid);

            titleContent = new GUIContent(
                string.Format(TJGeneratorsL10n.L("TJGenerators 图片 - {0}"), Path.GetFileNameWithoutExtension(newTargetPath)));

            string newGuid = _targetAsset.guid;
            if (!string.IsNullOrEmpty(newGuid))
            {
                s_openWindows[newGuid] = this;
                TJGeneratorsHistoryManager.RewriteAssetGuid(oldTargetGuid, newGuid);
            }
        }

        // ========== 生成 ==========
        protected override void OnStartGeneration()
        {
            if (string.IsNullOrWhiteSpace(textPrompt))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("请输入文本提示词。"), LogTag);
                return;
            }

            bool hasImage = referenceImagePaths.Count > 0;
            var layout = GetCurrentGeneratorConfig()?.uiLayout;
            if (layout != null && layout.imageUploadRequired && !hasImage)
            {
                ErrorDialogUtils.ShowErrorDialog(
                    TJGeneratorsL10n.L("错误"),
                    TJGeneratorsL10n.L("请上传输入图片。"),
                    LogTag);
                return;
            }

            if (_currentGenerator == null)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("未选择可用的生成模型。"), LogTag);
                return;
            }

            EnsureTargetImage();

            MarkGenerationStarted();

            if (_currentGenerator is DynamicGenerator dynamicGen)
            {
                string fmt = dynamicGen.GetParameter("outputFormat")?.ToString() ?? "";
                if (string.Equals(fmt, "jpeg", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fmt, "jpg", StringComparison.OrdinalIgnoreCase))
                    dynamicGen.SetParameter("isSegmentation", false);
                else if (string.Equals(fmt, "png", StringComparison.OrdinalIgnoreCase))
                    dynamicGen.SetParameter("isSegmentation", true);

                dynamicGen.ClearExtraRawJsonFields();

                string finalPrompt = textPrompt.Trim();
                dynamicGen.SetPromptTemplateSelection(selectedPromptTemplate);
                dynamicGen.SetTextPrompt(finalPrompt);
                dynamicGen.SetHistoryDisplayPrompt(textPrompt.Trim());
                dynamicGen.SetImagePaths(hasImage ? referenceImagePaths : null);
            }

            StartPipelineForCurrentGenerator();
        }

        public override string GetAssetSavePath(PipelineMediaType type, ModelGeneratorBase generator)
        {
            if (type != PipelineMediaType.Texture) return null;
            // 图片分层类 generator 输出多张 RGBA PNG；其余模型仍按 jpeg 占位，pipeline 会按实际格式迁移扩展名
            bool isLayering = generator != null && IsLayeringGenerator(generator.GeneratorId);
            return BuildHistoryTexturePath(isLayering ? "ImageLayers_" : "Image_", isLayering ? ".png" : ".jpg");
        }

        public override void OnAssetSaved(PipelineMediaType type, string savePath, ModelGeneratorBase generator)
        {
            if (type != PipelineMediaType.Texture) return;

            // 地形高度图后处理改为「一键生成地形」时执行，此处仅保留后端原图

            // 设置 history 文件本身的导入器（RGBA32 + alpha，避免索引色 PNG 被压成 DXT5）
            GeneratedTextureImportUtils.ConfigureImportedTexture(
                savePath, TextureImporterType.Default, alphaIsTransparency: true);

            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));

            // 同步更新绑定资产：扩展名变化时自动删除旧占位并重写历史 GUID，避免残留同名异扩展名文件
            if (!ReplaceTargetImageFromSource(savePath, TJGeneratorsL10n.L("已生成图片并复制到"), out string replaceErr))
                TJLog.LogWarning($"{LogTag} 同步目标图片失败: {replaceErr}");

            // 多图层时其余图仍在下载；完成态挪到 OnGenerationCompleted
        }

        public override void OnGenerationCompleted(string assetPath)
        {
            base.OnGenerationCompleted(assetPath);

            // 图片分层：配置其余层 RGBA/标签（第 0 层已在 OnAssetSaved 处理）
            bool isLayering = _currentGenerator != null && IsLayeringGenerator(_currentGenerator.GeneratorId);
            if (isLayering && !string.IsNullOrEmpty(assetPath))
            {
                int expected = 4;
                if (_currentGenerator is DynamicGenerator dg
                    && dg.GetParameter("numLayers") != null
                    && int.TryParse(dg.GetParameter("numLayers").ToString(), out int parsed)
                    && parsed > 0)
                {
                    expected = parsed;
                }
                else if (_currentGenerator != null && IsSeedreamLayeringGenerator(_currentGenerator.GeneratorId))
                {
                    // Seedream 自动分层：底图 + 最多 16 层，无 numLayers 参数；
                    // 给上限 17，CollectIndexedSiblingPaths 遇到缺口会自动停止
                    expected = 17;
                }

                var layerPaths = GeneratedTextureImportUtils.CollectIndexedSiblingPaths(assetPath, expected);
                for (int i = 1; i < layerPaths.Count; i++)
                {
                    string path = layerPaths[i];
                    GeneratedTextureImportUtils.ConfigureImportedTexture(
                        path, TextureImporterType.Default, alphaIsTransparency: true);
                    TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));
                }
            }

            MarkGenerationCompleted();
        }

        /// <summary>
        /// 允许一键生成：已完成、本地 PNG 存在；且（历史里保存了地形模板 id，或当前窗口正选中地形高度图模板）。
        /// 避免仅依赖 <see cref="TJGeneratorsGenerationHistoryItem.promptTemplateId"/>（旧历史或序列化前记录为空时按钮长期灰色）。
        /// </summary>
        private bool CanGenerateTerrainFromHistoryItem(TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null || item.isGenerating || string.IsNullOrEmpty(item.modelPath))
                return false;
            if (!item.modelPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!File.Exists(PathUtils.ToAbsoluteAssetPath(item.modelPath)))
                return false;

            if (
                string.Equals(
                    item.promptTemplateId,
                    UnityTerrainHeightmapTemplateId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return true;

            return IsUnityTerrainHeightmapTemplateSelected();
        }

        /// <summary>
        /// 复制历史中的原始高度图 → 后处理写入单独 PNG → 按 PNG 宽高设置 Terrain 世界尺寸并创建场景地形。
        /// </summary>
        private void GenerateTerrainFromHeightmap(int historyIndex)
        {
            if (historyIndex < 0 || historyIndex >= generationHistory.Count)
                return;

            var item = generationHistory[historyIndex];
            if (!CanGenerateTerrainFromHistoryItem(item))
            {
                ErrorDialogUtils.ShowErrorDialog(
                    TJGeneratorsL10n.L("无法生成地形"),
                    TJGeneratorsL10n.L("请选择由「Unity 地形高度图」模板生成且已完成的 PNG 历史记录。"),
                    LogTag
                );
                return;
            }

            var hmOpts = new TerrainHeightmapPostProcessOptions
            {
                median3x3 = terrainHeightmapMedian3x3,
                gaussianBlur = terrainHeightmapGaussianBlur,
                gaussianSigma = terrainHeightmapBlurSigma,
                percentileNormalization = terrainHeightmapPercentileNormalize,
                percentileLow = terrainHeightmapPercentileLow,
                percentileHigh = terrainHeightmapPercentileHigh,
                heightGamma = terrainHeightmapHeightGamma,
                remapOutputMin = terrainHeightmapRemapOutMin,
                remapOutputMax = Mathf.Max(
                    terrainHeightmapRemapOutMax,
                    terrainHeightmapRemapOutMin + 0.01f
                ),
            };

            var (_, _, _, error) = TerrainCreationUtils.PostProcessAndCreateTerrain(
                item.modelPath, hmOpts);

            if (!string.IsNullOrEmpty(error))
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("地形生成失败"), error, LogTag);

            Repaint();
        }

        // ========== 辅助方法 ==========
        private void EnsureTargetImage()
        {
            // 初始化阶段：只在未绑定/无效时创建占位图，不强制改动用户已绑定的扩展名。
            if (_targetAsset != null && _targetAsset.IsValid())
                return;

            EnsureTargetImage(".jpg");
        }

        private void EnsureTargetImage(string desiredExt)
        {
            desiredExt = (desiredExt ?? ".jpg").Trim();
            if (!desiredExt.StartsWith("."))
                desiredExt = "." + desiredExt;
            desiredExt = desiredExt.ToLowerInvariant();

            // 目标已有效时直接使用（无论扩展名是否与 desiredExt 一致）；
            // 后续 ReplaceTargetImageFromSource 会在保存实际结果时处理扩展名变化。
            if (_targetAsset != null && _targetAsset.IsValid())
                return;

            string folder = PathUtils.GetProjectBrowserInsertionFolderAssetPath();
            // 跨所有常见图片扩展名检查同基名占用，避免与同名但不同后缀的文件冲突（如已有 New Image.png 时不创建 New Image.jpg）。
            string path = TJGeneratorsImageAssetPathUtility.GenerateUniqueImagePath(
                $"{folder}/New Image{desiredExt}"
            );
            path = CreateBlankImage(path);

            if (string.IsNullOrEmpty(path))
            {
                TJLog.LogError($"{LogTag} 无法创建图片资产");
                return;
            }

            _targetAsset = TJGeneratorsAssetReference.FromPath(path);
            titleContent = new GUIContent(
                string.Format(TJGeneratorsL10n.L("TJGenerators 图片 - {0}"), Path.GetFileNameWithoutExtension(path))
            );

            RegisterInOpenWindows();

            Repaint();
        }

        /// <summary>
        /// 创建空白图片资产（根据扩展名创建 JPG/PNG）。
        /// </summary>
        public static string CreateBlankImage(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png")
            {
                path = Path.ChangeExtension(path, ".jpg");
                ext = ".jpg";
            }

            string absolutePath = PathUtils.ToAbsoluteAssetPath(path);

            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var blank =
                ext == ".png"
                    ? new Texture2D(4, 4, TextureFormat.RGBA32, false)
                    : new Texture2D(4, 4, TextureFormat.RGB24, false);
            var pixels = new Color[16];
            // 与「生成精灵」占位一致：PNG 全透明；JPG 无 alpha 时用与历史缩略图占位相近的深灰，避免一开始整片发白。
            Color fill = ext == ".png" ? Color.clear : new Color(0.2f, 0.2f, 0.2f);
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = fill;
            blank.SetPixels(pixels);
            blank.Apply();

            if (ext == ".png")
            {
                File.WriteAllBytes(absolutePath, blank.EncodeToPNG());
            }
            else
            {
                File.WriteAllBytes(absolutePath, blank.EncodeToJPG(75));
            }
            DestroyImmediate(blank);

            // 导入并设置类型
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.SaveAndReimport();
            }

            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));
            return path;
        }
    }
}
#endif
