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
    /// TJGenerators Sprite 生成窗口 - 使用 huoshan_seedream 等生成器生成 Sprite 贴图。
    /// </summary>
    public class TJGeneratorsSpriteWindow : TJGeneratorsAssetWindowBase
    {
        protected override ConfigType WindowConfigType => ConfigType.Sprite;
        protected override string LogTag => "[TJGeneratorsSprite]";

        protected override string TargetHeaderLabel => TJGeneratorsL10n.L("目标精灵");
        protected override string UnboundTargetLabel => TJGeneratorsL10n.L("未绑定精灵");
        protected override string EmptyGeneratorsMessage =>
            TJGeneratorsL10n.L("未找到可用的 Sprite 生成器，请检查 GeneratorConfig.json 中的 spriteGenerators");
        protected override string HistoryApplyLabel => TJGeneratorsL10n.L("应用到当前精灵");
        protected override string PromptControlName => "sprite_prompt_input";

        private static readonly Dictionary<string, TJGeneratorsSpriteWindow> s_openWindows =
            new Dictionary<string, TJGeneratorsSpriteWindow>();

        private readonly List<string> referenceImagePaths = new List<string>();
        private readonly List<Texture2D> referenceUploadedImages = new List<Texture2D>();
        private SelectorOptionConfig selectedType;
        private SelectorOptionConfig selectedStyle;

        // ========== 静态入口 ==========

        public static void ShowWindow()
        {
            var rect = GetDefaultMainWindowRect();
            var window = GetWindowWithRect<TJGeneratorsSpriteWindow>(
                rect,
                utility: false,
                title: TJGeneratorsL10n.L("TJGenerators 精灵生成"),
                focus: true
            );
            window.titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 精灵生成"));
            FinalizeMainWindowShow(window, rect);
        }

        public static void OpenForAsset(string assetPath)
        {
            GenerationWindowBase.OpenForAsset(
                assetPath,
                s_openWindows,
                "[TJGeneratorsSprite]",
                TJGeneratorsL10n.L("TJGenerators 精灵 - {0}"),
                () => CreateInstance<TJGeneratorsSpriteWindow>(),
                (w, r) => w._targetAsset = r,
                ShowWindow);
        }

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
            foreach (var tex in referenceUploadedImages)
            {
                if (tex != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(tex)))
                    DestroyImmediate(tex);
            }
            referenceUploadedImages.Clear();
            referenceImagePaths.Clear();
        }

        protected override void ResetInputStateAfterModelChange()
        {
            var config = GetCurrentGeneratorConfig();
            ResetTextPromptIfHidden(config, ref textPrompt);
            ClearReferenceImagesWhenUploadHidden(config, referenceImagePaths, referenceUploadedImages);
        }

        protected override void OnModelSelected(AIModelInfo model)
        {
            OnModelSelectedBase(model);
            UploadImageComponents.TrimReferenceImagesToMax(
                referenceImagePaths,
                referenceUploadedImages,
                GetMaxReferenceImages());
        }

        // ========== UI ==========

        protected override void DrawLeftPanelBody()
        {
            DrawInputSection();
            DrawTypeAndStyleSelectors();
            var genConfig = GetCurrentGeneratorConfig();
            if (ShouldShowImageUpload(genConfig))
            {
                GUILayout.Space(CommonStyles.Space3);
                DrawImageUploadArea();
                GUILayout.Space(CommonStyles.Space2);
            }
            DrawConfigurationSection();
            GUILayout.Space(CommonStyles.Space3);
        }

        protected override bool CanStartGeneration => !string.IsNullOrWhiteSpace(textPrompt);

        private void DrawTypeAndStyleSelectors()
        {
            if (_currentGenerator == null)
                return;
            var genConfig = GetCurrentGeneratorConfig();
            if (genConfig == null)
                return;

            var typeSelectorConfig = (genConfig.typeSelector?.options != null ? genConfig.typeSelector : null)
                                     ?? ConfigManager.GetSpriteTypeSelector();
            var styleSelectorConfig = (genConfig.styleSelector?.options != null ? genConfig.styleSelector : null)
                                      ?? ConfigManager.GetSpriteStyleSelector();
            bool hasTypeSelector = typeSelectorConfig != null && typeSelectorConfig.enabled;
            bool hasStyleSelector = styleSelectorConfig != null && styleSelectorConfig.enabled;

            if (!hasTypeSelector && !hasStyleSelector)
                return;

            if (hasTypeSelector)
            {
                UIComponents.DrawSelectionRow(
                    TJGeneratorsL10n.L("内容类型"),
                    TJGeneratorsL10n.L("选择类型"),
                    CommonStyles.DropBoxRightArrow4xTexture,
                    ShowTypeSelector,
                    selectedType != null ? TJGeneratorsL10n.L(selectedType.name) : null);
                GUILayout.Space(CommonStyles.Space2);
                if (hasStyleSelector)
                {
                    UIComponents.DrawGapLine();
                    GUILayout.Space(CommonStyles.Space2);
                }
            }

            if (hasStyleSelector)
            {
                UIComponents.DrawSelectionRow(
                    TJGeneratorsL10n.L("艺术风格"),
                    TJGeneratorsL10n.L("选择风格"),
                    CommonStyles.DropBoxRightArrow4xTexture,
                    ShowStyleSelector,
                    selectedStyle != null ? TJGeneratorsL10n.L(selectedStyle.name) : null);
                GUILayout.Space(CommonStyles.Space2);
                UIComponents.DrawGapLine();
                GUILayout.Space(CommonStyles.Space2);
            }
        }

        private void ShowTypeSelector()
        {
            var genConfig = GetCurrentGeneratorConfig();
            var typeSelector = (genConfig?.typeSelector?.options != null ? genConfig.typeSelector : null)
                               ?? ConfigManager.GetSpriteTypeSelector();
            if (typeSelector?.options == null)
            {
                TJLog.LogError($"{LogTag} 类型选择器配置为空");
                return;
            }

            TJGeneratorsModelSelectorWindow.ShowTypeSelector(
                typeSelector.options,
                OnTypeSelected,
                selectedType
            );
        }

        private void ShowStyleSelector()
        {
            var genConfig = GetCurrentGeneratorConfig();
            var styleSelector = (genConfig?.styleSelector?.options != null ? genConfig.styleSelector : null)
                                ?? ConfigManager.GetSpriteStyleSelector();
            if (styleSelector?.options == null)
            {
                TJLog.LogError($"{LogTag} 风格选择器配置为空");
                return;
            }

            TJGeneratorsModelSelectorWindow.ShowStyleSelector(
                styleSelector.options,
                OnStyleSelected,
                selectedStyle
            );
        }

        private void OnTypeSelected(SelectorOptionConfig type)
        {
            if (type?.id == "none")
            {
                selectedType = null;
                TJLog.Log($"{LogTag} 用户选择不使用特定类型");
            }
            else
            {
                selectedType = type;
                TJLog.Log($"{LogTag} 选择类型: {type?.name}");
            }

            if (_currentGenerator is DynamicGenerator dynamicGen)
                dynamicGen.SetTypeSelection(selectedType);

            Repaint();
        }

        private void OnStyleSelected(SelectorOptionConfig style)
        {
            if (style?.id == "none")
            {
                selectedStyle = null;
                TJLog.Log($"{LogTag} 用户选择不使用特定风格");
            }
            else
            {
                selectedStyle = style;
                TJLog.Log($"{LogTag} 选择风格: {style?.name}");
            }

            if (_currentGenerator is DynamicGenerator dynamicGen)
                dynamicGen.SetStyleSelection(selectedStyle);

            Repaint();
        }

        private void DrawImageUploadArea()
        {
            DrawConfiguredReferenceImageUpload(
                referenceImagePaths,
                referenceUploadedImages,
                "sprite_reference_upload");
        }

        // ========== 历史 ==========

        protected override void DrawHistoryPanel(float panelWidth)
        {
            DrawStandardHistoryPanel(panelWidth, new StandardHistoryPanelOptions
            {
                AddPanelTopSpacing = true,
                GetLargePreviewTexture = GetPreviewTextureForHistoryItem,
                DrawTilePreview = DrawSpriteHistoryPreview,
                GetModelLabel = item => GetModelDisplayLabelFromIndex(item.modelVersion),
                ShowContextMenu = ShowHistoryContextMenu,
                DrawHistoryActions = () => DrawDefaultHistoryActions(showTileSizeSlider: true),
            });
        }

        private Texture2D GetPreviewTextureForHistoryItem(TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null || item.isGenerating)
                return null;
            if (!string.IsNullOrEmpty(item.modelPath))
            {
                if (historyPreviewCache.TryGetValue(item.modelPath, out var cached) && cached != null)
                    return cached;
                var assetTex = AssetDatabase.LoadAssetAtPath<Texture2D>(item.modelPath);
                if (assetTex != null)
                {
                    historyPreviewCache[item.modelPath] = assetTex;
                    return assetTex;
                }
                string absPath = PathUtils.ToAbsoluteAssetPath(item.modelPath);
                if (File.Exists(absPath))
                    EnqueuePreviewLoad(item.modelPath, absPath, false);
            }
            if (!string.IsNullOrEmpty(item.previewImageUrl)
                && urlPreviewCache.TryGetValue(item.previewImageUrl, out var urlTex)
                && urlTex != null)
                return urlTex;
            if (!item.isTextToModel
                && !string.IsNullOrEmpty(item.imagePath)
                && historyPreviewCache.TryGetValue(item.imagePath, out var up)
                && up != null)
                return up;
            return null;
        }

        private void DrawSpriteHistoryPreview(Rect rect, TJGeneratorsGenerationHistoryItem item)
        {
            if (item.isGenerating)
            {
                UIComponents.DrawLoadingSpinner(rect, CommonStyles.SmallGreyLabelStyle, Repaint);
                return;
            }
            if (!string.IsNullOrEmpty(item.modelPath))
            {
                if (historyPreviewCache.TryGetValue(item.modelPath, out var cached) && cached != null)
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
                    EnqueuePreviewLoad(item.modelPath, absPath, false);
            }
            if (!item.isTextToModel
                && !string.IsNullOrEmpty(item.imagePath)
                && historyPreviewCache.TryGetValue(item.imagePath, out var up)
                && up != null)
            {
                GUI.DrawTexture(rect, up, ScaleMode.ScaleToFit);
                return;
            }
            if (item.isTextToModel
                && !string.IsNullOrEmpty(item.previewImageUrl)
                && urlPreviewCache.TryGetValue(item.previewImageUrl, out var urlTex)
                && urlTex != null)
            {
                GUI.DrawTexture(rect, urlTex, ScaleMode.ScaleToFit);
                return;
            }
            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f, 1f));
            GUI.Label(
                new Rect(rect.x + rect.width / 4, rect.y + rect.height / 4, rect.width / 2, rect.height / 2),
                EditorGUIUtility.IconContent("d_Texture2D Icon"));
        }

        private void ShowHistoryContextMenu(int index)
        {
            var item = generationHistory[index];
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(HistoryApplyLabel), false, () => ApplyHistoryToAsset(index));
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在项目中显示")), false, () => ShowHistoryInProject(index));
            menu.AddSeparator("");
            if (!string.IsNullOrEmpty(item.modelPath))
                menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在资源管理器中显示")), false, () => EditorUtility.RevealInFinder(item.modelPath));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("从历史记录中移除")), false, () =>
            {
                TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                generationHistory = LoadGenerationHistory();
                if (selectedHistoryIndex >= generationHistory.Count)
                    selectedHistoryIndex = Mathf.Max(0, generationHistory.Count - 1);
                Repaint();
            });
            menu.ShowAsContext();
        }

        protected override void ApplyHistoryToAsset(int index)
        {
            if (index < 0 || index >= generationHistory.Count)
                return;
            var item = generationHistory[index];
            if (string.IsNullOrEmpty(item.modelPath) || !File.Exists(PathUtils.ToAbsoluteAssetPath(item.modelPath)))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("该历史记录的纹理文件不存在。"), LogTag);
                return;
            }

            if (_targetAsset == null || !_targetAsset.IsValid())
            {
                Debug.LogWarning($"{LogTag} {TJGeneratorsL10n.L("请先绑定或创建目标精灵资产。")}");
                return;
            }
            string targetPath = _targetAsset.GetPath();
            if (!targetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                targetPath = Path.ChangeExtension(targetPath, ".png");
            if (!EditorUtility.DisplayDialog(
                    TJGeneratorsL10n.L("确认替换"),
                    string.Format(TJGeneratorsL10n.L("确定要将选中的历史应用到 {0} 吗？"), Path.GetFileName(targetPath)),
                    TJGeneratorsL10n.L("确定"),
                    TJGeneratorsL10n.L("取消")))
                return;
            try
            {
                File.Copy(PathUtils.ToAbsoluteAssetPath(item.modelPath), PathUtils.ToAbsoluteAssetPath(targetPath), true);
                AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                var importer = AssetImporter.GetAtPath(targetPath) as TextureImporter;
                if (importer != null)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.SaveAndReimport();
                }
                TJLog.Log($"[TJGeneratorsSprite] 已将历史应用到 {targetPath}");
            }
            catch (Exception e)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("应用失败: ") + e.Message, LogTag);
            }
            Repaint();
        }

        // ========== 生成 ==========

        protected override void OnStartGeneration()
        {
            if (_currentGenerator == null || string.IsNullOrEmpty(textPrompt))
            {
                ErrorDialogUtils.ShowErrorDialog(
                    TJGeneratorsL10n.L("错误"),
                    TJGeneratorsL10n.L("请先选择模型并输入文本提示词"),
                    LogTag);
                return;
            }

            EnsureTargetSprite();
            MarkGenerationStarted();
            if (_currentGenerator is DynamicGenerator dynamicGen)
            {
                dynamicGen.SetTextPrompt(textPrompt);
                dynamicGen.SetImagePaths(
                    referenceImagePaths != null && referenceImagePaths.Count > 0
                        ? referenceImagePaths
                        : null);
            }
            StartPipelineForCurrentGenerator();
        }

        private void EnsureTargetSprite()
        {
            if (_targetAsset != null && _targetAsset.IsValid())
                return;
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            string spritePath = AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/New Sprite.png");
            spritePath = CreateBlankSprite(spritePath);
            if (string.IsNullOrEmpty(spritePath))
            {
                TJLog.LogError("[TJGeneratorsSprite] 无法创建精灵");
                return;
            }
            _targetAsset = TJGeneratorsAssetReference.FromPath(spritePath);
            titleContent = new GUIContent(string.Format(
                TJGeneratorsL10n.L("TJGenerators 精灵 - {0}"),
                Path.GetFileNameWithoutExtension(spritePath)));
            RegisterInOpenWindows();
            Repaint();
        }

        /// <summary>
        /// 在指定路径创建空白 PNG 并导入为 Sprite。
        /// </summary>
        public static string CreateBlankSprite(string path)
        {
            path = Path.ChangeExtension(path, ".png");
            string absolutePath = PathUtils.ToAbsoluteAssetPath(path);
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            int size = 4;
            var blank = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color(0, 0, 0, 0);
            blank.SetPixels(pixels);
            blank.Apply();
            File.WriteAllBytes(absolutePath, blank.EncodeToPNG());
            DestroyImmediate(blank);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.SaveAndReimport();
            }
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));
            return path;
        }

        public override string GetAssetSavePath(PipelineMediaType type, ModelGeneratorBase generator)
        {
            if (type != PipelineMediaType.Texture)
                return null;
            return BuildHistoryTexturePath("Sprite_");
        }

        public override void OnAssetSaved(PipelineMediaType type, string savePath, ModelGeneratorBase generator)
        {
            if (type != PipelineMediaType.Texture)
                return;

            TJLog.Log($"{LogTag} OnAssetSaved: {savePath}");
            GeneratedTextureImportUtils.ConfigureImportedTexture(
                savePath, TextureImporterType.Sprite, alphaIsTransparency: true);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));

            string pathToShow = savePath;
            EnsureTargetSprite();
            if (_targetAsset != null && _targetAsset.IsValid())
            {
                string targetPath = _targetAsset.GetPath();
                if (!targetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    targetPath = Path.ChangeExtension(targetPath, ".png");
                try
                {
                    File.Copy(PathUtils.ToAbsoluteAssetPath(savePath), PathUtils.ToAbsoluteAssetPath(targetPath), true);
                    AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                    GeneratedTextureImportUtils.ConfigureImportedTexture(
                        targetPath, TextureImporterType.Sprite, alphaIsTransparency: true);
                    pathToShow = targetPath;
                }
                catch (Exception e)
                {
                    TJLog.LogWarning($"[TJGeneratorsSprite] 复制到目标失败: {e.Message}");
                }
            }

            var textureAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(pathToShow);
            if (textureAsset != null)
            {
                Selection.activeObject = textureAsset;
                EditorGUIUtility.PingObject(textureAsset);
            }
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(pathToShow));

            MarkGenerationCompleted();
        }
    }
}
#endif
