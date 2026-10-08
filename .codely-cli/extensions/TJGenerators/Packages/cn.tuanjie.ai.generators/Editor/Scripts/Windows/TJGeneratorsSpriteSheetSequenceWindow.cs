#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Unity.EditorCoroutines.Editor;
using TJGenerators.Config;
using TJGenerators.Generators;
using TJGenerators.Pipeline;
using TJGenerators.PostProcessing;
using TJGenerators.UI;
using TJGenerators.Utils;

namespace TJGenerators
{
    /// <summary>
    /// TJGenerators 2D 精灵表序列帧生成窗口：生成 1:1 spritesheet → 绿幕抠图 → 网格切片 → Sprites + AnimationClip。
    /// </summary>
    public class TJGeneratorsSpriteSheetSequenceWindow : GenerationWindowBase, IGenerationPipelineHost, IGenerationTriggerHost, IMediaAssetPipelineHost
    {
        private const string GeneratorId = "frontier-game-design";

        private const float HistoryBottomMargin = 90f;
        private const float PreviewToScrollSpacing = 12f;
        private const float HistoryLargeTileThreshold = 100f;
        private const float HistoryLargeLabelHeight = 40f;
        private const float HistorySmallLabelHeight = 32f;
        private const float ButtonHeightSmall = 22f;
        private const float ButtonHeightMedium = 24f;
        private const float ButtonHeightStandard = 25f;
        private const float ButtonHeightLarge = 28f;
        private const float PlaceholderIconOffsetScale = 0.25f;
        private const float PlaceholderIconSizeScale = 0.5f;
        private const float HistoryActionsTopSpacing = 5f;
        private const float HistoryActionsBottomSpacing = 10f;
        private const float HistoryZoomSliderWidth = 90f;
        private const float HistoryZoomSliderTopSpacing = 6f;
        private static readonly Color HistoryPlaceholderBackgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);

        private static readonly HashSet<string> ExcludedParameterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "isSegmentation", "qValue", "resizeWidth",
            "aspectRatio", "aspect_ratio", "resolution", "imageSize"
        };

        protected override ConfigType WindowConfigType => ConfigType.Image;
        protected override string LogTag => "[TJGeneratorsSpriteSheetSequence]";

        [SerializeField]
        private string textPrompt = "";

        [SerializeField]
        private TJGeneratorsAssetReference targetImageAsset;

        private readonly List<string> referenceImagePaths = new List<string>();
        private readonly List<Texture2D> referenceUploadedImages = new List<Texture2D>();

        private static readonly Dictionary<string, TJGeneratorsSpriteSheetSequenceWindow> s_openWindows =
            new Dictionary<string, TJGeneratorsSpriteSheetSequenceWindow>();

        private Texture2D imagePreviewTexture;

        private JObject spriteSheetProfilesRoot;
        [SerializeField] private string spriteSheetProfileId;

        [SerializeField] private int spriteSliceColumns = 4;
        [SerializeField] private int spriteSliceRows = 4;
        [SerializeField] [Range(1f, 60f)] private float spriteSliceFps = 12f;
        [SerializeField] private bool spriteSliceLoop = true;
        [SerializeField] private bool showSpriteCutoutSettings;
        [SerializeField] [Range(0.05f, 0.35f)] private float chromaKeyTolerance = SpriteSequencePostProcess.DefaultChromaTolerance;
        [SerializeField] [Range(0f, 0.3f)] private float chromaFeather = SpriteSequencePostProcess.DefaultChromaFeather;
        private Texture2D processedPreviewTexture;
        private string processedPreviewSourcePath;
        private bool processedPreviewValid;
        [SerializeField]
        private ImagePreview historyMainPreview = new ImagePreview();

        public static void ShowWindow()
        {
            string title = TJGeneratorsL10n.L("TJGenerators 2D精灵表序列帧");
            var rect = GetDefaultMainWindowRect();
            var window = GetWindowWithRect<TJGeneratorsSpriteSheetSequenceWindow>(
                rect,
                utility: false,
                title: title,
                focus: true
            );
            window.titleContent = new GUIContent(title);
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
                    TJGeneratorsL10n.L("TJGenerators 2D精灵表序列帧"),
                    TJGeneratorsL10n.L("仅支持绑定 .jpg / .jpeg / .png 的图片资产。\r\n\r\n建议先通过菜单创建新图片资产。"),
                    "[TJGeneratorsSpriteSheetSequence]"
                );
                return;
            }

            EnsureSpriteSheetLabelForPath(assetPath);

            GenerationWindowBase.OpenForAsset(
                assetPath,
                s_openWindows,
                "[TJGeneratorsSpriteSheetSequence]",
                TJGeneratorsL10n.L("TJGenerators 2D精灵表序列帧 - {0}"),
                () =>
                {
                    var window = CreateInstance<TJGeneratorsSpriteSheetSequenceWindow>();
                    return window;
                },
                (w, r) => w.targetImageAsset = r,
                ShowWindow);
        }

        private static bool IsSupportedImageAssetPath(string assetPath) =>
            assetPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || assetPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

        private static void EnsureSpriteSheetLabelForPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            TJGeneratorsGenerationLabel.EnableSpriteSheetLabel(asset);
        }

        protected override void OnBootstrapWindowContent()
        {
            if (targetImageAsset != null && !string.IsNullOrEmpty(targetImageAsset.guid))
                s_openWindows[targetImageAsset.guid] = this;

            InitializeGeneratorsFromConfig(ConfigType.Image);
            ApplyGeneratorFilter();
            ReloadSpriteSheetProfiles();
            OnRefreshWindowContent();
            Repaint();
        }

        protected override void OnRefreshWindowContent()
        {
            ApplyGeneratorFilter();
            ReloadSpriteSheetProfiles();
            RefreshHistory();
            CheckAndRecoverInterruptedTasks();
            Repaint();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            wantsMouseMove = true;

            EditorCoroutineUtility.StartCoroutineOwnerless(
                UserInfoHelper.GetUserInfoCoroutine(ConfigManager.GetUserInfoUrl(), OnUserInfoLoaded)
            );
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            wantsMouseMove = false;
            if (targetImageAsset != null && !string.IsNullOrEmpty(targetImageAsset.guid))
                s_openWindows.Remove(targetImageAsset.guid);

            imagePreviewTexture = null;
            ClearPreviewCaches();
            ResetProcessedPreview();

            foreach (var tex in referenceUploadedImages)
            {
                if (tex != null)
                    DestroyImmediate(tex);
            }
            referenceUploadedImages.Clear();
            referenceImagePaths.Clear();
        }

        protected override string GetCurrentAssetGuid() => targetImageAsset?.guid ?? "";

        protected override void SetHistory(List<TJGeneratorsGenerationHistoryItem> history) =>
            generationHistory = history;

        protected override void OnGeneratorRestoredFromTask(ModelGeneratorBase generator)
        {
            base.OnGeneratorRestoredFromTask(generator);
            isGenerating = true;
            generationStatus = TJGeneratorsL10n.L("恢复中...");
            Repaint();
        }

        private void OnGUI()
        {
            if (Event.current.type == EventType.MouseMove)
                Repaint();

            var splitLayout = UIComponents.CalculateFixedSplitLayout(
                position.width,
                CommonStyles.MainWindowMinSize.y,
                CommonStyles.LeftPanelFixedWidth,
                CommonStyles.MinHistoryPanelWidth,
                CommonStyles.OuterMargin);
            minSize = new Vector2(splitLayout.WindowMinWidth, splitLayout.WindowMinHeight);
            maxSize = new Vector2(10000f, 10000f);
            isVerticalLayout = false;
            currentHistoryPanelWidth = splitLayout.RightPanelWidth;
            _effectiveLeftPanelWidth = CommonStyles.LeftComponentWidth;

            if (_generators == null || _generators.Count == 0)
            {
                EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), CommonStyles.WindowBackgroundColor);
                EditorGUILayout.HelpBox(TJGeneratorsL10n.L("未找到可用的图片生成器，请检查配置"), MessageType.Error);
                return;
            }

            UIComponents.DrawAdaptiveLayoutBackground(
                new Rect(0, 0, position.width, position.height),
                false,
                splitLayout.LeftPanelWidth,
                position.height
            );

            GUILayout.BeginHorizontal();
            DrawLeftPanelColumn(
                splitLayout.LeftPanelWidth,
                ref scrollPosition,
                () =>
                {
                    GUILayout.Space(CommonStyles.LeftContentPadding);
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(CommonStyles.LeftContentPadding);
                    GUILayout.BeginVertical(
                        GUILayout.Width(CommonStyles.LeftComponentWidth),
                        GUILayout.MinWidth(CommonStyles.LeftComponentWidth),
                        GUILayout.MaxWidth(CommonStyles.LeftComponentWidth));

                    UIComponents.DrawTargetHeaderComposite(
                        TJGeneratorsL10n.L("目标图片"),
                        DrawTargetHeaderContentRect,
                        SelectTargetImageAsset
                    );
                    GUILayout.Space(CommonStyles.Space2);

                    UIComponents.DrawFixedModelSelector(
                        currentSelectedModel?.Name ?? _currentGenerator?.DisplayName ?? "Frontier",
                        currentSelectedModel);

                    GUILayout.Space(CommonStyles.Space3);
                    DrawInputSection();
                    GUILayout.Space(CommonStyles.Space3);
                    DrawConfigurationSection();
                    GUILayout.Space(CommonStyles.Space3);
                    DrawCutoutAfterGenerationSection();
                    GUILayout.Space(CommonStyles.Space3);

                    GUILayout.EndVertical();
                    GUILayout.Space(CommonStyles.LeftContentPadding);
                    GUILayout.EndHorizontal();
                    GUILayout.Space(CommonStyles.LeftContentPadding);
                });

            GUILayout.Space(splitLayout.GapWidth);
            DrawHistoryPanel(currentHistoryPanelWidth);
            GUILayout.EndHorizontal();

            DrawLeftPanelBottomDock(splitLayout.LeftPanelWidth, DrawGenerationSection);
        }

        private void DrawTargetHeaderContentRect(Rect rect)
        {
            if (targetImageAsset != null && targetImageAsset.IsValid())
            {
                string imageName = Path.GetFileNameWithoutExtension(targetImageAsset.GetPath());
                if (GUI.Button(rect, imageName, CommonStyles.TargetPrefabNameStyle))
                    SelectTargetImageAsset();
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            }
            else
            {
                GUI.Label(rect, TJGeneratorsL10n.L("未绑定（生成时自动创建）"), CommonStyles.ContentStyle);
            }
        }

        private void SelectTargetImageAsset()
        {
            if (targetImageAsset == null || !targetImageAsset.IsValid())
                return;
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(targetImageAsset.GetPath());
            if (tex != null)
            {
                EditorGUIUtility.PingObject(tex);
                Selection.activeObject = tex;
            }
        }

        private void DrawInputSection()
        {
            var genConfig = GetCurrentGeneratorConfig();
            textPrompt = DrawConfiguredTextPromptInput(textPrompt, "sprite_sheet_prompt_input", genConfig);

            if (ShouldShowImageUpload(genConfig))
            {
                GUILayout.Space(CommonStyles.Space3);
                DrawConfiguredReferenceImageUpload(
                    referenceImagePaths,
                    referenceUploadedImages,
                    "sprite_sheet_reference_upload");
            }
        }

        private void DrawConfigurationSection()
        {
            var provider = _currentGenerator as IGeneratorParameterProvider;
            var allParams = GetCurrentGeneratorParameters();
            List<ParameterConfig> filteredParams = null;
            if (allParams != null && allParams.Count > 0)
            {
                filteredParams = new List<ParameterConfig>(allParams.Count);
                for (int i = 0; i < allParams.Count; i++)
                {
                    var parameter = allParams[i];
                    if (parameter == null || string.IsNullOrEmpty(parameter.id))
                        continue;
                    if (ExcludedParameterIds.Contains(parameter.id))
                        continue;

                    filteredParams.Add(parameter);
                }
            }

            showAdvancedSettings = DrawConfiguredAdvancedSettingsFoldout(
                showAdvancedSettings,
                provider,
                filteredParams
            );
        }

        private void DrawGenerationSection(LeftPanelBottomDock.Layout layout)
        {
            bool canGenerate = _currentGenerator != null && !string.IsNullOrWhiteSpace(textPrompt);
            UIComponents.DrawGenerationSectionAt(
                layout,
                isGenerating,
                generationProgress,
                generationStatus,
                canGenerate,
                StartGeneration,
                null,
                Repaint
            );
        }

        private void DrawCutoutAfterGenerationSection()
        {
            showSpriteCutoutSettings = UIComponents.DrawSettingsFoldout(
                showSpriteCutoutSettings,
                TJGeneratorsL10n.L("抠图与切割"),
                DrawSpriteCutoutAndSliceContent,
                uppercaseLabel: true);
        }

        private void DrawHistoryPanel(float panelWidth)
        {
            float historyPanelHeight = isVerticalLayout ? position.height * 0.45f : position.height;
            float historyPanelInner = CommonStyles.HistoryPanelInnerWidth(panelWidth);
            float historyScrollInner = CommonStyles.HistoryScrollViewLayoutWidth(panelWidth);
            EnsureHistorySelectionAndFallback();

            UIComponents.BeginHistoryPanel(panelWidth, historyPanelHeight, isVerticalLayout);

            Texture2D historyPreviewTex = null;
            if (selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count)
            {
                var selectedItem = generationHistory[selectedHistoryIndex];
                if (!selectedItem.isGenerating)
                    historyPreviewTex = GetPreviewTextureForHistoryItem(selectedItem);
            }

            if (historyPreviewTex == null)
                historyPreviewTex = imagePreviewTexture;

            string activeSourcePath = GetActivePreviewSourcePath()?.Replace('\\', '/');
            historyPreviewTex = GetEffectivePreviewTexture(historyPreviewTex, activeSourcePath);

            float previewBlockHeight = historyMainPreview.Draw(
                historyPreviewTex,
                historyPanelInner,
                position.height,
                isVerticalLayout,
                Repaint,
                !string.IsNullOrEmpty(GetActivePreviewSourcePath())
                    ? (Action<Rect, Rect>)((drawRect, texCoords) => ImagePreview.DrawSliceGridOverlay(
                        drawRect,
                        Mathf.Max(1, spriteSliceColumns),
                        Mathf.Max(1, spriteSliceRows),
                        texCoords))
                    : null
            );
            GUILayout.Space(PreviewToScrollSpacing);

            float scrollHeight = historyPanelHeight - previewBlockHeight - PreviewToScrollSpacing - HistoryBottomMargin;
            historyScrollPosition = GUILayout.BeginScrollView(
                historyScrollPosition,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUILayout.Height(scrollHeight));

            if (generationHistory.Count == 0)
                UIComponents.DrawHistoryEmptyState();
            else
                DrawHistoryGrid(historyScrollInner);

            GUILayout.EndScrollView();
            DrawHistoryActions();
            UIComponents.EndHistoryPanel();
        }

        private void EnsureHistorySelectionAndFallback()
        {
            if (generationHistory == null)
                generationHistory = new List<TJGeneratorsGenerationHistoryItem>();

            if (generationHistory.Count > 0)
                selectedHistoryIndex = Mathf.Clamp(selectedHistoryIndex, 0, generationHistory.Count - 1);
            else
                selectedHistoryIndex = -1;
        }

        private Texture2D GetEffectivePreviewTexture(Texture2D historyPreviewTex, string selectedHistoryPath)
        {
            if (!processedPreviewValid || processedPreviewTexture == null)
                return historyPreviewTex;

            if (string.IsNullOrEmpty(selectedHistoryPath) || !string.Equals(selectedHistoryPath, processedPreviewSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                ResetProcessedPreview();
                return historyPreviewTex;
            }

            return processedPreviewTexture;
        }

        private void DrawHistoryGrid(float historyContentWidth)
        {
            float tileWidth = currentHistoryTileSize;
            float labelHeight = currentHistoryTileSize >= HistoryLargeTileThreshold
                ? HistoryLargeLabelHeight
                : HistorySmallLabelHeight;
            float tileHeight = tileWidth + labelHeight;
            int itemsPerRow = ComputeHistoryItemsPerRow(historyContentWidth, tileWidth);

            for (int i = 0; i < generationHistory.Count; i += itemsPerRow)
            {
                GUILayout.BeginHorizontal();
                for (int j = 0; j < itemsPerRow && (i + j) < generationHistory.Count; j++)
                {
                    int index = i + j;
                    var item = generationHistory[index];
                    bool isSelected = selectedHistoryIndex == index;

                    GUILayout.BeginVertical(
                        GetScaledHistoryTileStyle(isSelected),
                        GUILayout.Width(tileWidth),
                        GUILayout.Height(tileHeight)
                    );

                    float previewSize = GetScaledHistoryPreviewSize(tileWidth);
                    Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize);
                    DrawImageHistoryPreview(previewRect, item);

                    if (!item.isGenerating
                        && Event.current.type == EventType.MouseDown
                        && previewRect.Contains(Event.current.mousePosition))
                    {
                        if (selectedHistoryIndex != index)
                            ResetProcessedPreview();
                        selectedHistoryIndex = index;
                        Event.current.Use();
                        Repaint();
                    }

                    if (!item.isGenerating
                        && Event.current.type == EventType.ContextClick
                        && previewRect.Contains(Event.current.mousePosition))
                    {
                        ShowHistoryContextMenu(index);
                        Event.current.Use();
                    }

                    GUILayout.Label(GetHistoryUserPromptLabel(item), CommonStyles.HistoryLabelStyle);
                    GUILayout.Label(GetModelDisplayLabelFromIndex(item.modelVersion), CommonStyles.SmallGreyCenterLabelStyle);
                    GUILayout.EndVertical();
                }
                GUILayout.EndHorizontal();
            }
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
                var cachedTexture = TryGetOrEnqueueCachedTexture(item.modelPath);
                if (cachedTexture != null)
                {
                    GUI.DrawTexture(rect, cachedTexture, ScaleMode.ScaleToFit);
                    return;
                }
            }

            EditorGUI.DrawRect(rect, HistoryPlaceholderBackgroundColor);
            var iconRect = new Rect(
                rect.x + rect.width * PlaceholderIconOffsetScale,
                rect.y + rect.height * PlaceholderIconOffsetScale,
                rect.width * PlaceholderIconSizeScale,
                rect.height * PlaceholderIconSizeScale);
            GUI.Label(iconRect, EditorGUIUtility.IconContent("d_Texture2D Icon"));
        }

        private Texture2D GetPreviewTextureForHistoryItem(TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null || item.isGenerating)
                return null;

            if (!string.IsNullOrEmpty(item.modelPath))
            {
                var cachedTexture = TryGetOrEnqueueCachedTexture(item.modelPath);
                if (cachedTexture != null)
                    return cachedTexture;
            }

            if (item.isTextToModel
                && !string.IsNullOrEmpty(item.previewImageUrl)
                && urlPreviewCache.TryGetValue(item.previewImageUrl, out var urlTex)
                && urlTex != null)
            {
                return urlTex;
            }

            return null;
        }

        private Texture2D TryGetOrEnqueueCachedTexture(string assetPath)
        {
            if (historyPreviewCache.TryGetValue(assetPath, out var cached) && cached != null)
                return cached;

            var assetTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (assetTexture != null)
                return historyPreviewCache[assetPath] = assetTexture;

            string absolutePath = PathUtils.ToAbsoluteAssetPath(assetPath);
            if (File.Exists(absolutePath))
                EnqueuePreviewLoad(assetPath, absolutePath, false);

            return null;
        }

        private void DrawHistoryActions()
        {
            GUILayout.Space(HistoryActionsTopSpacing);
            GUILayout.BeginHorizontal();

            bool hasSelection = selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count;
            bool itemGenerating = hasSelection && generationHistory[selectedHistoryIndex].isGenerating;
            GUI.enabled = hasSelection && !itemGenerating;

            if (GUILayout.Button(TJGeneratorsL10n.L("应用到当前图片"), GUILayout.Height(ButtonHeightStandard)))
                ApplyHistoryToImage(selectedHistoryIndex);

            if (GUILayout.Button(TJGeneratorsL10n.L("在项目中显示"), GUILayout.Height(ButtonHeightStandard)))
                ShowHistoryInProject(selectedHistoryIndex);

            GUI.enabled = true;

            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical();
            GUILayout.Space(HistoryZoomSliderTopSpacing);
            historyMainPreview.DrawZoomSlider(HistoryZoomSliderWidth);
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.Space(HistoryActionsBottomSpacing);
        }

        private static string GetHistoryUserPromptLabel(TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null)
                return "";
            return TJGeneratorsPromptDisplay.FormatHistoryTileLabel(item.GetUserFacingPrompt());
        }

        private void DrawSpriteCutoutAndSliceContent()
        {
            string activeSourcePath = GetActivePreviewSourcePath();

            GUILayout.Label(
                TJGeneratorsL10n.L("在右侧预览区选中历史图片后，可抠图、切割并导出 Sprite 动画；也可直接导入本地图片。"),
                CommonStyles.HelpBoxStyle,
                GUILayout.MaxWidth(CommonStyles.LeftComponentWidth));
            GUILayout.Space(CommonStyles.Space2);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(TJGeneratorsL10n.L("导入本地图片…"), GUILayout.Height(ButtonHeightSmall), GUILayout.ExpandWidth(true)))
                ImportLocalImageToHistory();
            GUILayout.Space(CommonStyles.Space2);
            if (GUILayout.Button(TJGeneratorsL10n.L("使用当前选中图片"), GUILayout.Height(ButtonHeightSmall), GUILayout.ExpandWidth(true)))
                AddSelectedProjectTextureToHistory();
            GUILayout.EndHorizontal();

            GUILayout.Space(CommonStyles.Space2);

            if (string.IsNullOrEmpty(activeSourcePath))
            {
                GUILayout.Label(
                    TJGeneratorsL10n.L("未找到可处理的图片：请先在右侧历史中选一张，或点击上方「导入本地图片 / 使用当前选中图片」。"),
                    CommonStyles.HelpBoxStyle,
                    GUILayout.MaxWidth(CommonStyles.LeftComponentWidth));
                return;
            }

            chromaKeyTolerance = UIComponents.DrawAdvancedStyleSliderRow(
                TJGeneratorsL10n.L("绿幕容差"), chromaKeyTolerance, 0.05f, 0.35f);
            GUILayout.Space(CommonStyles.Space2);
            chromaFeather = UIComponents.DrawAdvancedStyleSliderRow(
                TJGeneratorsL10n.L("边缘羽化"), chromaFeather, 0f, 0.3f);

            GUILayout.Space(CommonStyles.Space2);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(TJGeneratorsL10n.L("执行抠图并预览"), GUILayout.Height(ButtonHeightMedium), GUILayout.ExpandWidth(true)))
                ApplyGreenScreenCutoutPreview();
            GUILayout.Space(CommonStyles.Space2);
            if (GUILayout.Button(TJGeneratorsL10n.L("恢复原图预览"), GUILayout.Height(ButtonHeightMedium), GUILayout.ExpandWidth(true)))
            {
                ResetProcessedPreview();
                Repaint();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(CommonStyles.Space2);
            spriteSliceColumns = Mathf.Max(1, UIComponents.DrawAdvancedStyleIntRow(TJGeneratorsL10n.L("切割列数"), spriteSliceColumns, "sprite_slice_columns"));
            GUILayout.Space(CommonStyles.Space2);
            spriteSliceRows = Mathf.Max(1, UIComponents.DrawAdvancedStyleIntRow(TJGeneratorsL10n.L("切割行数"), spriteSliceRows, "sprite_slice_rows"));
            GUILayout.Space(CommonStyles.Space2);
            spriteSliceFps = UIComponents.DrawAdvancedStyleSliderRow(TJGeneratorsL10n.L("动画 FPS"), spriteSliceFps, 1f, 60f);
            GUILayout.Space(CommonStyles.Space2);
            spriteSliceLoop = UIComponents.DrawAdvancedStyleBoolRow(TJGeneratorsL10n.L("动画循环 (Loop)"), spriteSliceLoop);
            GUILayout.Space(CommonStyles.Space2);
            GUILayout.Label(
                TJGeneratorsL10n.L("预览图中的红色线条为切割线。"),
                CommonStyles.HelpBoxStyle,
                GUILayout.MaxWidth(CommonStyles.LeftComponentWidth));

            GUILayout.Space(CommonStyles.Space2);
            if (GUILayout.Button(TJGeneratorsL10n.L("切割并导出为 Sprite"), GUILayout.Height(ButtonHeightLarge), GUILayout.ExpandWidth(true)))
                SliceSelectedHistoryToSprites();
        }

        private void ImportLocalImageToHistory()
        {
            string file = EditorUtility.OpenFilePanel(TJGeneratorsL10n.L("选择要导入的图片"), "", "png,jpg,jpeg");
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
                return;

            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/Imported"))
                AssetDatabase.CreateFolder("Assets/TJGenerators", "Imported");

            string ext = Path.GetExtension(file);
            if (string.IsNullOrEmpty(ext))
                ext = ".png";
            ext = ext.ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
                ext = ".png";

            string baseName = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(baseName))
                baseName = "ImportedImage";

            string dstAssetPath = AssetDatabase.GenerateUniqueAssetPath($"Assets/TJGenerators/Imported/{baseName}{ext}");
            string dstAbs = PathUtils.ToAbsoluteAssetPath(dstAssetPath);
            try
            {
                string dir = Path.GetDirectoryName(dstAbs);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.Copy(file, dstAbs, true);
            }
            catch (Exception e)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("导入失败"), e.Message, LogTag);
                return;
            }

            AssetDatabase.ImportAsset(dstAssetPath, ImportAssetOptions.ForceUpdate);
            ConfigureAndReimportTexture(dstAssetPath, alphaIsTransparency: true);

            AddImageAssetPathToHistoryAndSelect(dstAssetPath);
            Repaint();
        }

        private void AddSelectedProjectTextureToHistory()
        {
            var tex = Selection.activeObject as Texture2D;
            if (tex == null)
            {
                Debug.LogWarning($"{LogTag} 请先在 Project 里选中一张 Texture2D 图片（png/jpg）。");
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogWarning($"{LogTag} 无法获取选中图片的路径。");
                return;
            }

            assetPath = assetPath.Replace('\\', '/');
            if (!assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                && !assetPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                && !assetPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning($"{LogTag} 当前仅支持 png/jpg/jpeg。");
                return;
            }

            AddImageAssetPathToHistoryAndSelect(assetPath);
            Repaint();
        }

        private void AddImageAssetPathToHistoryAndSelect(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            assetPath = assetPath.Replace('\\', '/');
            if (!File.Exists(PathUtils.ToAbsoluteAssetPath(assetPath)))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("图片文件不存在，无法加入历史。"), LogTag);
                return;
            }

            EnsureHistorySelectionAndFallback();

            for (int i = 0; i < generationHistory.Count; i++)
            {
                var historyItem = generationHistory[i];
                if (historyItem != null && !string.IsNullOrEmpty(historyItem.modelPath)
                    && string.Equals(historyItem.modelPath.Replace('\\', '/'), assetPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (selectedHistoryIndex != i)
                        ResetProcessedPreview();
                    selectedHistoryIndex = i;
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                    if (obj != null)
                    {
                        Selection.activeObject = obj;
                        EditorGUIUtility.PingObject(obj);
                    }
                    return;
                }
            }

            generationHistory.Insert(0, new TJGeneratorsGenerationHistoryItem
            {
                modelPath = assetPath,
                isGenerating = false,
                prompt = TJGeneratorsL10n.L("（手动导入图片）"),
                modelVersion = "manual_import"
            });
            ResetProcessedPreview();
            selectedHistoryIndex = 0;

            var assetObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (assetObj != null)
            {
                Selection.activeObject = assetObj;
                EditorGUIUtility.PingObject(assetObj);
            }
        }

        private void ShowHistoryContextMenu(int index)
        {
            if (index < 0 || index >= generationHistory.Count)
                return;
            var item = generationHistory[index];
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("应用到当前图片")), false, () => ApplyHistoryToImage(index));
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在项目中显示")), false, () => ShowHistoryInProject(index));

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

        private void ApplyHistoryToImage(int index)
        {
            if (index < 0 || index >= generationHistory.Count)
                return;
            var item = generationHistory[index];

            if (item.isGenerating)
            {
                Debug.LogWarning($"{LogTag} {TJGeneratorsL10n.L("请等待该条生成完成后再应用。")}");
                return;
            }

            if (string.IsNullOrEmpty(item.modelPath) || !File.Exists(PathUtils.ToAbsoluteAssetPath(item.modelPath)))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("该历史记录的图片文件不存在。"), LogTag);
                if (!string.IsNullOrEmpty(item.modelPath))
                    TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                RefreshHistory();
                Repaint();
                return;
            }

            if (targetImageAsset == null || !targetImageAsset.IsValid())
            {
                Debug.LogWarning($"{LogTag} {TJGeneratorsL10n.L("请先绑定或创建目标图片资产。")}");
                return;
            }

            string srcExt = string.IsNullOrEmpty(item.modelPath) ? ".png" : Path.GetExtension(item.modelPath);
            if (string.IsNullOrEmpty(srcExt)) srcExt = ".png";
            string targetPathForDialog = Path.ChangeExtension(targetImageAsset.GetPath(), srcExt);
            if (!EditorUtility.DisplayDialog(
                    TJGeneratorsL10n.L("确认替换"),
                    string.Format(TJGeneratorsL10n.L("确定将选中的图片应用到当前目标「{0}」吗？"), Path.GetFileNameWithoutExtension(targetPathForDialog)),
                    TJGeneratorsL10n.L("确定"),
                    TJGeneratorsL10n.L("取消")))
            {
                return;
            }

            if (!ReplaceTargetImageFromSource(item.modelPath, TJGeneratorsL10n.L("已将历史图片应用到"), out string err))
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), string.IsNullOrEmpty(err) ? TJGeneratorsL10n.L("应用失败（详见控制台）。") : string.Format(TJGeneratorsL10n.L("应用失败: {0}"), err), LogTag);
            else
                RefreshHistory();

            Repaint();
        }

        private bool ReplaceTargetImageFromSource(string sourceAssetPath, string logSuccessVerb, out string errorMessage)
        {
            return TargetImageReplaceHelper.ReplaceTargetImageFromSource(
                sourceAssetPath,
                logSuccessVerb,
                LogTag,
                ref targetImageAsset,
                ref imagePreviewTexture,
                historyPreviewCache,
                ext =>
                {
                    EnsureTargetImage(ext);
                    return targetImageAsset;
                },
                path => ConfigureAndReimportTexture(path),
                OnTargetImageExtensionChanged,
                ReleaseProcessedPreviewIfTargetPath,
                out errorMessage);
        }

        private void ReleaseProcessedPreviewIfTargetPath(string targetAssetPath)
        {
            if (processedPreviewValid
                && !string.IsNullOrEmpty(processedPreviewSourcePath)
                && string.Equals(processedPreviewSourcePath.Replace('\\', '/'), targetAssetPath, StringComparison.OrdinalIgnoreCase))
            {
                ResetProcessedPreview();
            }
        }

        private void OnTargetImageExtensionChanged(string oldTargetGuid, string newTargetPath)
        {
            if (!string.IsNullOrEmpty(oldTargetGuid))
                s_openWindows.Remove(oldTargetGuid);

            titleContent = new GUIContent(
                string.Format(
                    TJGeneratorsL10n.L("TJGenerators 2D精灵表序列帧 - {0}"),
                    Path.GetFileNameWithoutExtension(newTargetPath)));

            string newGuid = targetImageAsset.guid;
            if (!string.IsNullOrEmpty(newGuid))
            {
                s_openWindows[newGuid] = this;
                TJGeneratorsHistoryManager.RewriteAssetGuid(oldTargetGuid, newGuid);
            }
        }

        private void ShowHistoryInProject(int index)
        {
            if (index < 0 || index >= generationHistory.Count)
                return;

            var item = generationHistory[index];
            if (string.IsNullOrEmpty(item.modelPath))
                return;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.modelPath);
            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);
                Selection.activeObject = asset;
            }
        }

        private void ApplyGreenScreenCutoutPreview()
        {
            string sourcePath = GetActivePreviewSourcePath();
            if (string.IsNullOrEmpty(sourcePath))
            {
                Debug.LogWarning($"{LogTag} 未找到可处理的预览图，请先生成或选中一张历史图片。");
                return;
            }

            Texture2D src = SpriteSequencePostProcess.LoadReadableTextureFromAssetPath(sourcePath);
            if (src == null)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("无法读取选中的历史图片。"), LogTag);
                return;
            }

            try
            {
                Texture2D cutout = SpriteSequencePostProcess.BuildGreenScreenCutoutTexture(src, chromaKeyTolerance, chromaFeather);
                ReplaceProcessedPreview(cutout, sourcePath);
                Repaint();
            }
            finally
            {
                DestroyImmediate(src);
            }
        }

        private void SliceSelectedHistoryToSprites()
        {
            string sourcePath = GetActivePreviewSourcePath();
            if (string.IsNullOrEmpty(sourcePath))
            {
                Debug.LogWarning($"{LogTag} 未找到可切割的图片，请先生成或选中一张历史图片。");
                return;
            }

            Texture2D sourceTexture = processedPreviewValid && processedPreviewTexture != null && string.Equals(processedPreviewSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)
                ? processedPreviewTexture
                : SpriteSequencePostProcess.LoadReadableTextureFromAssetPath(sourcePath);

            if (sourceTexture == null)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("无法读取选中的历史图片。"), LogTag);
                return;
            }

            bool shouldDestroyLoaded = sourceTexture != processedPreviewTexture;
            try
            {
                var sliceResult = SpriteSequencePostProcess.SliceTextureToSpritesAndAnimation(
                    sourceTexture,
                    sourcePath,
                    spriteSliceColumns,
                    spriteSliceRows,
                    spriteSliceFps,
                    spriteSliceLoop
                );
                string outputDir = sliceResult.OutputDirectory;
                int exported = sliceResult.ExportedCount;
                string clipPath = sliceResult.AnimationClipPath;
                string msg = string.IsNullOrEmpty(clipPath)
                    ? TJGeneratorsL10n.L("已导出 {0} 张 Sprite。\n路径：{1}", exported, outputDir)
                    : TJGeneratorsL10n.L("已导出 {0} 张 Sprite，并创建动画文件。\nSprite路径：{1}\n动画路径：{2}", exported, outputDir, clipPath);
                Debug.Log($"{LogTag} 切割完成：\n{msg}");

                LabelAndPingSliceResult(sliceResult);
            }
            finally
            {
                if (shouldDestroyLoaded)
                    DestroyImmediate(sourceTexture);
            }
        }

        private string GetSelectedHistoryModelPath()
        {
            if (selectedHistoryIndex < 0 || selectedHistoryIndex >= generationHistory.Count)
                return null;
            var item = generationHistory[selectedHistoryIndex];
            if (item == null || string.IsNullOrEmpty(item.modelPath))
                return null;
            return item.modelPath.Replace('\\', '/');
        }

        private string GetActivePreviewSourcePath()
        {
            string historyPath = GetSelectedHistoryModelPath();
            if (!string.IsNullOrEmpty(historyPath))
                return historyPath;

            if (targetImageAsset != null && targetImageAsset.IsValid())
            {
                string assetPath = targetImageAsset.GetPath();
                if (!string.IsNullOrEmpty(assetPath))
                    return assetPath.Replace('\\', '/');
            }

            return null;
        }

        private void ReplaceProcessedPreview(Texture2D newTexture, string sourcePath)
        {
            ResetProcessedPreview();
            processedPreviewTexture = newTexture;
            processedPreviewSourcePath = sourcePath;
            processedPreviewValid = processedPreviewTexture != null;
        }

        private void ResetProcessedPreview()
        {
            if (processedPreviewTexture != null)
                DestroyImmediate(processedPreviewTexture);
            processedPreviewTexture = null;
            processedPreviewSourcePath = null;
            processedPreviewValid = false;
        }

        private bool TryValidateInstructionsForGeneration(out string failureMessage)
        {
            failureMessage = null;

            ReloadSpriteSheetProfiles();
            if (spriteSheetProfilesRoot == null)
            {
                failureMessage = TJGeneratorsL10n.L("未找到或无法读取 SpriteSheetSequenceProfiles.json。\n\n请确认包内含 Editor/Config/SpriteSheetSequenceProfiles.json，或通过 Package Manager 正确安装 cn.tuanjie.ai.generators。");
                return false;
            }

            if (string.IsNullOrEmpty(spriteSheetProfileId))
            {
                failureMessage = TJGeneratorsL10n.L("序列帧模板不可用：请检查 SpriteSheetSequenceProfiles.json 中的 profiles 与 defaultProfileId 是否有效。");
                return false;
            }

            string envelopeRaw = BuildEnvelopeRawFromSelectedProfile(referenceImagePaths);
            if (string.IsNullOrEmpty(envelopeRaw))
            {
                failureMessage = TJGeneratorsL10n.L("无法根据当前模板构建序列帧指令包（frontier_sequence_envelope），请检查配置文件。");
                return false;
            }

            string instructions = ExtractInstructionsFromEnvelopeRaw(envelopeRaw);
            if (string.IsNullOrWhiteSpace(instructions))
            {
                failureMessage = TJGeneratorsL10n.L("当前 profile 的 instructions 为空或缺失，请编辑 SpriteSheetSequenceProfiles.json 填写完整指令。");
                return false;
            }

            return true;
        }

        private void StartGeneration()
        {
            if (string.IsNullOrWhiteSpace(textPrompt))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("请输入文本提示词。"), LogTag);
                return;
            }

            ReloadSpriteSheetProfiles();
            if (!TryValidateInstructionsForGeneration(out string validationFailureMessage))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("缺少序列帧指令配置"), validationFailureMessage, LogTag);
                return;
            }

            var (effectiveReferencePaths, userRefCount) = BuildEffectiveReferenceImagePathsWithUserCount(referenceImagePaths);

            if (_currentGenerator == null)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("未选择可用的生成模型。"), LogTag);
                return;
            }

            EnsureTargetImage();

            isGenerating = true;
            generationStatus = TJGeneratorsL10n.L("准备中...");
            generationProgress = 0f;

            if (_currentGenerator is DynamicGenerator dynamicGen)
            {
                dynamicGen.SetParameter("isSegmentation", false);
                dynamicGen.ClearExtraRawJsonFields();
                dynamicGen.SetParameter("imageSize", "square_hd");
                dynamicGen.SetParameter("outputFormat", "png");
                dynamicGen.SetPromptTemplateSelection(null);

                string finalPrompt = textPrompt.Trim();
                string envelopeRaw = BuildEnvelopeRawFromSelectedProfile(referenceImagePaths);
                if (!string.IsNullOrEmpty(envelopeRaw))
                {
                    dynamicGen.SetExtraRawJsonField("frontier_sequence_envelope", envelopeRaw);
                    string instructions = ExtractInstructionsFromEnvelopeRaw(envelopeRaw);
                    if (!string.IsNullOrEmpty(instructions))
                        finalPrompt = BuildPromptWithInstructionsFallback(finalPrompt, instructions);

                    if (effectiveReferencePaths.Count > 0)
                        finalPrompt = SpriteSheetSequenceImageOrderHint.AppendToPrompt(
                            finalPrompt,
                            effectiveReferencePaths.Count,
                            userRefCount
                        );
                }

                dynamicGen.SetTextPrompt(finalPrompt);
                dynamicGen.SetHistoryDisplayPrompt(textPrompt.Trim());
                dynamicGen.SetImagePaths(effectiveReferencePaths.Count > 0 ? effectiveReferencePaths : null);
            }

            string assetGuid = targetImageAsset?.guid ?? "";
            EditorCoroutineUtility.StartCoroutineOwnerless(
                _pipeline.StartGeneration(_currentGenerator, assetGuid)
            );
        }

        public TJGeneratorsAssetReference GetTargetAsset() => targetImageAsset;

        public void StartGeneration(ModelGeneratorBase generator)
        {
            if (generator == _currentGenerator)
                StartGeneration();
        }

        public void OnGenerationCompleted(string assetPath)
        {
            if (generationHistory != null && !string.IsNullOrEmpty(assetPath))
            {
                int index = generationHistory.FindIndex(x => x.imagePath == assetPath || x.modelPath == assetPath);
                if (index >= 0)
                    selectedHistoryIndex = index;
            }
        }

        public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return null;

            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                AssetDatabase.CreateFolder("Assets/TJGenerators", "History");

            string uniqueName = "SpriteSheet_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            return AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/History/" + uniqueName);
        }

        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return;

            ConfigureAndReimportTexture(savePath);

            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));

            if (!ReplaceTargetImageFromSource(savePath, TJGeneratorsL10n.L("已生成图片并复制到"), out string replaceErr))
                TJLog.LogWarning($"{LogTag} 同步目标图片失败: {replaceErr}");

            generationStatus = TJGeneratorsL10n.L("完成");
            generationProgress = 1f;
            isGenerating = false;

            var spriteSheetAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(savePath);
            TJGeneratorsGenerationLabel.EnableSpriteSheetLabel(spriteSheetAsset);
            if (targetImageAsset != null && targetImageAsset.IsValid())
            {
                var targetAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(targetImageAsset.GetPath());
                TJGeneratorsGenerationLabel.EnableSpriteSheetLabel(targetAsset);
            }
            AutoPostProcess(savePath);
        }

        private void AutoPostProcess(string sourceAssetPath)
        {
            try
            {
                var sliceResult = SpriteSequencePostProcess.CutoutAndSlice(
                    sourceAssetPath,
                    chromaKeyTolerance,
                    chromaFeather,
                    spriteSliceColumns,
                    spriteSliceRows,
                    spriteSliceFps,
                    spriteSliceLoop,
                    writeCutoutBackToAsset: true);

                ConfigureAndReimportTexture(sourceAssetPath, alphaIsTransparency: true);

                Texture2D cutoutForSync = SpriteSequencePostProcess.LoadReadableTextureFromAssetPath(sourceAssetPath);
                if (cutoutForSync != null)
                {
                    SyncCutoutToTargetImage(cutoutForSync);
                    DestroyImmediate(cutoutForSync);
                }

                LabelAndPingSliceResult(sliceResult);

                processedPreviewTexture = SpriteSequencePostProcess.LoadReadableTextureFromAssetPath(sourceAssetPath);
                if (processedPreviewTexture != null)
                {
                    processedPreviewSourcePath = sourceAssetPath;
                    processedPreviewValid = true;
                }

                TJLog.Log($"{LogTag} 自动后处理完成：{sliceResult.ExportedCount} 帧，AnimationClip={sliceResult.AnimationClipPath}");
            }
            catch (Exception e)
            {
                TJLog.LogWarning($"{LogTag} 自动后处理异常: {e.Message}");
            }
        }

        private void SyncCutoutToTargetImage(Texture2D cutoutTex)
        {
            if (targetImageAsset == null || !targetImageAsset.IsValid())
                return;

            string targetPath = targetImageAsset.GetPath();
            if (string.IsNullOrEmpty(targetPath))
                return;

            string targetAbsolute = PathUtils.ToAbsoluteAssetPath(targetPath);
            File.WriteAllBytes(targetAbsolute, cutoutTex.EncodeToPNG());
            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
            ConfigureAndReimportTexture(targetPath, alphaIsTransparency: true);
        }

        private void LabelAndPingSliceResult(SpriteSequencePostProcess.SliceResult sliceResult)
        {
            if (sliceResult.SpriteAssetPaths != null)
            {
                foreach (var spritePath in sliceResult.SpriteAssetPaths)
                {
                    var spriteAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(spritePath);
                    TJGeneratorsGenerationLabel.EnableSpriteSheetLabel(spriteAsset);
                }
            }

            if (!string.IsNullOrEmpty(sliceResult.AnimationClipPath))
            {
                var clipObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(sliceResult.AnimationClipPath);
                TJGeneratorsGenerationLabel.EnableSpriteSheetLabel(clipObj);
            }

            if (!string.IsNullOrEmpty(sliceResult.AnimationClipPath))
            {
                var clipAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(sliceResult.AnimationClipPath);
                if (clipAsset != null)
                {
                    EditorGUIUtility.PingObject(clipAsset);
                    Selection.activeObject = clipAsset;
                }
            }
            else if (!string.IsNullOrEmpty(sliceResult.OutputDirectory))
            {
                var dirAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(sliceResult.OutputDirectory);
                if (dirAsset != null)
                    EditorGUIUtility.PingObject(dirAsset);
            }
        }

        private void ApplyGeneratorFilter()
        {
            if (_generators == null || _generators.Count == 0)
                return;

            var filtered = new List<ModelGeneratorBase>();
            for (int i = 0; i < _generators.Count; i++)
            {
                var g = _generators[i];
                if (string.Equals(g.GeneratorId, GeneratorId, StringComparison.OrdinalIgnoreCase))
                    filtered.Add(g);
            }
            if (filtered.Count == 0)
                return;

            _generators = filtered;
            _currentGeneratorIndex = 0;
            _currentGenerator = _generators[0];
            currentSelectedModel = BuildModelInfoFromGenerator(_currentGenerator);
        }

        private void ReloadSpriteSheetProfiles()
        {
            spriteSheetProfilesRoot = null;

            try
            {
                if (!SpriteSheetSequenceProfileConfigLoader.TryLoad(out spriteSheetProfilesRoot, out _)
                    || spriteSheetProfilesRoot == null)
                    return;

                var profiles = spriteSheetProfilesRoot["profiles"] as JArray;
                if (profiles == null || profiles.Count == 0)
                    return;

                string defaultId = spriteSheetProfilesRoot["defaultProfileId"]?.ToString();
                string targetId = !string.IsNullOrEmpty(spriteSheetProfileId) ? spriteSheetProfileId : defaultId;
                spriteSheetProfileId = ResolveProfileId(profiles, targetId);

                ApplySliceSettingsFromSelectedProfile();
            }
            catch (Exception e)
            {
                TJLog.LogWarning($"{LogTag} 读取序列帧模板配置失败: {e.Message}");
            }
        }

        private static string ResolveProfileId(JArray profiles, string preferredId)
        {
            if (!string.IsNullOrEmpty(preferredId))
            {
                foreach (var token in profiles)
                {
                    if (!(token is JObject item))
                        continue;
                    string id = item["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id)
                        && string.Equals(id, preferredId, StringComparison.OrdinalIgnoreCase))
                        return id;
                }
            }

            foreach (var token in profiles)
            {
                if (!(token is JObject item))
                    continue;
                string id = item["id"]?.ToString();
                if (!string.IsNullOrEmpty(id))
                    return id;
            }

            return null;
        }

        private void ApplySliceSettingsFromSelectedProfile()
        {
            var profileObj = FindSelectedProfileObject();
            if (profileObj == null)
                return;

            JToken colsToken = profileObj["sliceColumns"];
            JToken rowsToken = profileObj["sliceRows"];
            if (colsToken != null && int.TryParse(colsToken.ToString(), out int cols) && cols > 0)
                spriteSliceColumns = cols;
            if (rowsToken != null && int.TryParse(rowsToken.ToString(), out int rows) && rows > 0)
                spriteSliceRows = rows;
        }

        private JObject FindSelectedProfileObject()
        {
            if (spriteSheetProfilesRoot == null || string.IsNullOrEmpty(spriteSheetProfileId))
                return null;
            var profiles = spriteSheetProfilesRoot["profiles"] as JArray;
            if (profiles == null)
                return null;
            foreach (var token in profiles)
            {
                if (!(token is JObject item))
                    continue;
                if (string.Equals(item["id"]?.ToString(), spriteSheetProfileId, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return null;
        }

        private string BuildEnvelopeRawFromSelectedProfile(List<string> userReferenceImagePaths)
        {
            var profile = FindSelectedProfileObject();
            if (profile == null)
                return null;

            var envelope = new JObject
            {
                ["instructions"] = profile["instructions"]?.ToString() ?? "",
                ["knowledge_refs"] = profile["knowledge_refs"] is JArray refs
                    ? (JArray)refs.DeepClone()
                    : new JArray(),
                ["reference_channel_policy"] = new JObject
                {
                    ["user_reference_channel"] = "imageUrls",
                    ["knowledge_reference_channel"] = "frontier_sequence_envelope.knowledge_refs",
                    ["identity_priority"] = "user_reference_first",
                    ["knowledge_usage"] = "layout_alignment_only"
                },
                ["user_reference_refs"] = BuildUserReferenceRefs(userReferenceImagePaths)
            };
            return envelope.ToString();
        }

        private (List<string> paths, int userImageCount) BuildEffectiveReferenceImagePathsWithUserCount(
            List<string> userReferenceImagePaths)
        {
            var merged = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int userImageCount = 0;

            if (userReferenceImagePaths != null)
            {
                for (int i = 0; i < userReferenceImagePaths.Count; i++)
                {
                    string normalizedPath = NormalizeToAbsoluteImagePath(userReferenceImagePaths[i]);
                    if (string.IsNullOrEmpty(normalizedPath) || !File.Exists(normalizedPath) || !seen.Add(normalizedPath))
                        continue;
                    merged.Add(normalizedPath);
                    userImageCount++;
                }
            }

            var knowledgePaths = GetKnowledgeLocalImagePathsFromSelectedProfile();
            for (int i = 0; i < knowledgePaths.Count; i++)
            {
                string normalizedPath = NormalizeToAbsoluteImagePath(knowledgePaths[i]);
                if (string.IsNullOrEmpty(normalizedPath) || !File.Exists(normalizedPath) || !seen.Add(normalizedPath))
                    continue;
                merged.Add(normalizedPath);
            }

            return (merged, userImageCount);
        }

        private List<string> GetKnowledgeLocalImagePathsFromSelectedProfile()
        {
            var paths = new List<string>();
            var profile = FindSelectedProfileObject();
            if (!(profile?["knowledge_refs"] is JArray refs) || refs.Count == 0)
                return paths;

            foreach (var token in refs)
            {
                if (!(token is JObject item))
                    continue;
                string localPath = item["local_path"]?.ToString();
                if (string.IsNullOrEmpty(localPath))
                    localPath = item["image_path"]?.ToString();
                if (string.IsNullOrEmpty(localPath))
                    localPath = item["path"]?.ToString();
                if (!string.IsNullOrEmpty(localPath))
                    paths.Add(localPath);
            }

            return paths;
        }

        private static void ConfigureAndReimportTexture(string assetPath, bool alphaIsTransparency = false)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return;

            importer.textureType = TextureImporterType.Default;
            importer.alphaIsTransparency = alphaIsTransparency;
            importer.SaveAndReimport();
        }

        private static string NormalizeToAbsoluteImagePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            if (Path.IsPathRooted(path))
                return path;
            return PathUtils.ToAbsoluteAssetPath(path.Replace("\\", "/"));
        }

        private static JArray BuildUserReferenceRefs(List<string> userReferenceImagePaths)
        {
            var result = new JArray();
            if (userReferenceImagePaths == null || userReferenceImagePaths.Count == 0)
                return result;

            for (int i = 0; i < userReferenceImagePaths.Count; i++)
            {
                string path = userReferenceImagePaths[i];
                if (string.IsNullOrEmpty(path))
                    continue;

                result.Add(new JObject
                {
                    ["index"] = i,
                    ["source"] = "user_upload",
                    ["role"] = "identity_primary",
                    ["path"] = path,
                    ["name"] = Path.GetFileName(path)
                });
            }

            return result;
        }

        private static string ExtractInstructionsFromEnvelopeRaw(string envelopeRaw)
        {
            if (string.IsNullOrEmpty(envelopeRaw))
                return null;
            try
            {
                var obj = JObject.Parse(envelopeRaw);
                return obj["instructions"]?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string BuildPromptWithInstructionsFallback(string prompt, string instructions)
        {
            if (string.IsNullOrWhiteSpace(instructions))
                return prompt ?? "";
            if (string.IsNullOrWhiteSpace(prompt))
                return instructions;
            return instructions + "\n\n【用户补充】\n" + prompt.Trim();
        }

        protected override void ResetInputStateAfterModelChange()
        {
            var config = GetCurrentGeneratorConfig();
            ResetTextPromptIfHidden(config, ref textPrompt);
            ClearReferenceImagesWhenUploadHidden(config, referenceImagePaths, referenceUploadedImages);
        }

        private void EnsureTargetImage()
        {
            if (targetImageAsset != null && targetImageAsset.IsValid())
                return;
            EnsureTargetImage(".png");
        }

        private void EnsureTargetImage(string desiredExt)
        {
            desiredExt = (desiredExt ?? ".png").Trim();
            if (!desiredExt.StartsWith("."))
                desiredExt = "." + desiredExt;
            desiredExt = desiredExt.ToLowerInvariant();

            if (targetImageAsset != null && targetImageAsset.IsValid())
                return;

            string folder = PathUtils.GetProjectBrowserInsertionFolderAssetPath();
            string path = TJGeneratorsImageAssetPathUtility.GenerateUniqueImagePath($"{folder}/New Image{desiredExt}");
            path = TJGeneratorsImageWindow.CreateBlankImage(path);

            if (string.IsNullOrEmpty(path))
            {
                TJLog.LogError($"{LogTag} 无法创建图片资产");
                return;
            }

            EnsureSpriteSheetLabelForPath(path);
            targetImageAsset = TJGeneratorsAssetReference.FromPath(path);
            titleContent = new GUIContent(
                string.Format(TJGeneratorsL10n.L("TJGenerators 2D精灵表序列帧 - {0}"), Path.GetFileNameWithoutExtension(path))
            );

            if (!string.IsNullOrEmpty(targetImageAsset.guid))
                s_openWindows[targetImageAsset.guid] = this;

            Repaint();
        }
    }
}
#endif