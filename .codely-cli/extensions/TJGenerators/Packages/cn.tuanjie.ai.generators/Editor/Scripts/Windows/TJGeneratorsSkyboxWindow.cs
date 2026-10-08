#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using TJGenerators.Generators;
using TJGenerators.Pipeline;
using TJGenerators.Config;
using TJGenerators.UI;
using TJGenerators.Utils;
using Unity.UniAsset.Manager.Editor.InternalBridge;
using Unity.EditorCoroutines.Editor;

namespace TJGenerators
{
    /// <summary>
    /// TJGenerators 天空盒生成窗口 - 使用生成器模型生成天空盒
    /// </summary>
    public class TJGeneratorsSkyboxWindow : GenerationWindowBase, IGenerationPipelineHost, IGenerationTriggerHost, IMediaAssetPipelineHost
    {
        // ========== 基类抽象属性实现 ==========
        protected override ConfigType WindowConfigType => ConfigType.Skybox;
        protected override string LogTag => "[TJGeneratorsSkybox]";

        // ========== 用户输入 ==========
        private string textPrompt = "";
        private string imagePath = "";
        private Texture2D uploadedImage;

        // ========== 目标资产 ==========
        [SerializeField]
        private TJGeneratorsAssetReference targetSkyboxAsset;
        private static Dictionary<string, TJGeneratorsSkyboxWindow> skyboxOpenWindows = new Dictionary<string, TJGeneratorsSkyboxWindow>();

        // ========== 天空盒预览 ==========
        private Material skyboxPreviewMaterial;
        private Cubemap generatedSkybox;
        private Texture2D skyboxPreviewTexture;

        // ========== 公开方法 ==========
        
        public static void ShowWindow()
        {
            var rect = GetDefaultMainWindowRect();
            var window = GetWindowWithRect<TJGeneratorsSkyboxWindow>(
                rect,
                utility: false,
                title: TJGeneratorsL10n.L("TJGenerators 天空盒生成"),
                focus: true
            );
            window.titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 天空盒生成"));
            FinalizeMainWindowShow(window, rect);
        }
        
        /// <summary>
        /// 为指定的天空盒资产打开窗口
        /// </summary>
        public static void OpenForAsset(string assetPath)
        {
            GenerationWindowBase.OpenForAsset(
                assetPath,
                skyboxOpenWindows,
                "[TJGeneratorsSkybox]",
                TJGeneratorsL10n.L("TJGenerators 天空盒 - {0}"),
                () =>
                {
                    var window = CreateInstance<TJGeneratorsSkyboxWindow>();
                    return window;
                },
                (w, r) => w.targetSkyboxAsset = r,
                ShowWindow);
        }

        protected override void OnBootstrapWindowContent()
        {
            if (targetSkyboxAsset != null && !string.IsNullOrEmpty(targetSkyboxAsset.guid))
                skyboxOpenWindows[targetSkyboxAsset.guid] = this;

            InitializeGeneratorsFromConfig(ConfigType.Skybox);
            OnRefreshWindowContent();
        }

        protected override void OnRefreshWindowContent()
        {
            if (targetSkyboxAsset == null || !targetSkyboxAsset.IsValid())
                EnsureTargetSkybox();
            RefreshHistory();
            CheckAndRecoverInterruptedTasks();
        }
        
        // ========== 生命周期 ==========
        
        protected override void OnEnable()
        {
            base.OnEnable();
            wantsMouseMove = true;

            EditorCoroutineUtility.StartCoroutineOwnerless(UserInfoHelper.GetUserInfoCoroutine(ConfigManager.GetUserInfoUrl(), OnUserInfoLoaded));
        }
        
        protected override void OnDisable()
        {
            base.OnDisable();
            wantsMouseMove = false;
            if (targetSkyboxAsset != null && !string.IsNullOrEmpty(targetSkyboxAsset.guid))
            {
                skyboxOpenWindows.Remove(targetSkyboxAsset.guid);
            }
            
            CleanupResources();
            ClearPreviewCaches();
        }

        // ========== 任务恢复 ==========

        protected override string GetCurrentAssetGuid() => GetCurrentSkyboxAssetGuid();

        protected override void SetHistory(List<TJGeneratorsGenerationHistoryItem> history) => generationHistory = history;

        protected override void OnGeneratorRestoredFromTask(ModelGeneratorBase generator)
        {
            base.OnGeneratorRestoredFromTask(generator);
            isGenerating = true;
            generationStatus = TJGeneratorsL10n.L("恢复中...");
        }

        /// <summary>
        /// Skybox 窗口优先选择 rodin 生成器
        /// </summary>
        protected override int GetFallbackGeneratorIndex()
        {
            // 如果没有偏好，优先选择 rodin
            if (TryGetGeneratorIndex("rodin", out int rodinIndex) && rodinIndex >= 0)
                return rodinIndex;
            return 0;
        }

        private void CleanupResources()
        {
            if (skyboxPreviewMaterial != null)
            {
                DestroyImmediate(skyboxPreviewMaterial);
                skyboxPreviewMaterial = null;
            }
            
            if (uploadedImage != null)
            {
                DestroyImmediate(uploadedImage);
                uploadedImage = null;
            }
        }
        
        // ========== UI绘制 ==========
        
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
            isVerticalLayout = false;
            currentHistoryPanelWidth = splitLayout.RightPanelWidth;
            _effectiveLeftPanelWidth = CommonStyles.LeftComponentWidth;
            
            if (_generators == null || _generators.Count == 0)
            {
                EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), CommonStyles.WindowBackgroundColor);
                EditorGUILayout.HelpBox(TJGeneratorsL10n.L("未找到可用的生成器，请检查配置"), MessageType.Error);
                return;
            }
            
            UIComponents.DrawAdaptiveLayoutBackground(
                new Rect(0, 0, position.width, position.height),
                false,
                splitLayout.LeftPanelWidth,
                position.height);

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
                        TJGeneratorsL10n.L("目标天空盒"),
                        DrawTargetHeaderContentRect,
                        SelectTargetSkyboxAsset
                    );
                    GUILayout.Space(CommonStyles.Space2);
                    UIComponents.DrawModelSelector(
                        currentSelectedModel?.Name ?? _currentGenerator?.DisplayName ?? TJGeneratorsL10n.L("未选择"),
                        currentSelectedModel,
                        OnModelSelected,
                        ConfigType.Skybox
                    );
                    GUILayout.Space(CommonStyles.Space3);
                    DrawInputSection();
                    GUILayout.Space(CommonStyles.Space3);
                    DrawConfigurationSection();
                    GUILayout.Space(CommonStyles.Space3);

                    GUILayout.EndVertical();
                    GUILayout.Space(CommonStyles.LeftContentPadding);
                    GUILayout.EndHorizontal();
                    GUILayout.Space(CommonStyles.LeftContentPadding);
                });

            GUILayout.Space(splitLayout.GapWidth);

            // 右侧：历史生成记录
            DrawHistoryPanel(currentHistoryPanelWidth);
            GUILayout.EndHorizontal();

            DrawLeftPanelBottomDock(splitLayout.LeftPanelWidth, DrawGenerationSection);
        }

        private void DrawTargetHeaderContentRect(Rect rect)
        {
            if (targetSkyboxAsset != null && targetSkyboxAsset.IsValid())
            {
                string skyboxName = Path.GetFileNameWithoutExtension(targetSkyboxAsset.GetPath());
                if (GUI.Button(rect, skyboxName, CommonStyles.TargetPrefabNameStyle))
                    SelectTargetSkyboxAsset();
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            }
            else
            {
                GUI.Label(rect, TJGeneratorsL10n.L("未绑定（生成时自动创建）"), CommonStyles.ContentStyle);
            }
        }
        
        private void OnModelSelected(AIModelInfo model)
        {
            OnModelSelectedBase(model);
        }

        protected override void ResetInputStateAfterModelChange()
        {
            var config = GetCurrentGeneratorConfig();
            ResetTextPromptIfHidden(config, ref textPrompt);
            ClearSingleReferenceImageWhenUploadHidden(config, ref imagePath, ref uploadedImage);
        }

        private void SelectTargetSkyboxAsset()
        {
            if (targetSkyboxAsset == null || !targetSkyboxAsset.IsValid())
                return;
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(targetSkyboxAsset.GetPath());
            if (obj != null)
            {
                EditorGUIUtility.PingObject(obj);
                Selection.activeObject = obj;
            }
        }

        private void DrawInputSection()
        {
            var genConfig = GetCurrentGeneratorConfig();
            textPrompt = DrawConfiguredTextPromptInput(textPrompt, "skybox_prompt_input", genConfig);

            if (ShouldShowImageUpload(genConfig))
            {
                GUILayout.Space(CommonStyles.Space3);
                var uiLayout = genConfig?.uiLayout;
                UIComponents.DrawReferenceImageSectionTitle(
                    ResolveImageUploadLabel(uiLayout),
                    string.IsNullOrEmpty(imagePath) ? 0 : 1,
                    1);
                GUILayout.Space(CommonStyles.Space2);
                UploadImageComponents.DrawLargeImageUpload(
                    ref imagePath,
                    ref uploadedImage,
                    null,
                    Repaint,
                    onPickDone: (path, tex) =>
                    {
                        imagePath = path;
                        uploadedImage = tex;
                    });
            }
        }
        
        private void DrawConfigurationSection()
        {
            var provider = _currentGenerator as IGeneratorParameterProvider;
            showAdvancedSettings = DrawConfiguredAdvancedSettingsFoldout(
                showAdvancedSettings,
                provider,
                GetCurrentGeneratorParameters());
        }
        
        private void DrawGenerationSection(LeftPanelBottomDock.Layout layout)
        {
            UIComponents.DrawGenerationSectionAt(
                layout,
                isGenerating,
                generationProgress,
                generationStatus,
                !string.IsNullOrWhiteSpace(textPrompt),
                StartGeneration,
                null,
                Repaint);
        }
        
        private void DrawSkyboxPreview()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(TJGeneratorsL10n.L("天空盒预览"), CommonStyles.HeaderStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            // 若在历史记录中选中了某条，主预览区显示该条历史的天空盒
            Texture2D previewToShow = null;
            if (selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count)
            {
                var selectedItem = generationHistory[selectedHistoryIndex];
                if (!selectedItem.isGenerating)
                    previewToShow = GetPreviewTextureForHistoryItem(selectedItem);
            }
            if (previewToShow == null)
                previewToShow = skyboxPreviewTexture;

            if (previewToShow != null || (skyboxPreviewMaterial != null && generatedSkybox != null))
            {
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical();

                // 单张 PNG 纹理预览（不再做六面展开）；宽度不超过左侧面板，避免两列时与历史重叠
                float availableWidth = Mathf.Max(300, Mathf.Min(position.width - 60, _effectiveLeftPanelWidth - 60));
                float previewHeight = Mathf.Min(availableWidth * 0.5f, 200f);
                Rect previewRect = GUILayoutUtility.GetRect(availableWidth, previewHeight);
                EditorGUI.DrawRect(previewRect, new Color(0.15f, 0.15f, 0.15f, 1f));
                if (previewToShow != null)
                    GUI.DrawTexture(previewRect, previewToShow, ScaleMode.ScaleToFit);

                GUILayout.EndVertical();
                GUILayout.EndHorizontal();

                GUILayout.Space(10);

                GUILayout.BeginHorizontal();

                if (GUILayout.Button(TJGeneratorsL10n.L("应用到场景天空盒"), CommonStyles.ButtonStyle, GUILayout.Height(30f)))
                {
                    ApplyToSceneSkybox();
                }

                GUILayout.Space(10);

                if (GUILayout.Button(TJGeneratorsL10n.L("保存为材质"), CommonStyles.ButtonStyle, GUILayout.Height(30f)))
                {
                    SaveSkyboxMaterial();
                }

                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.BeginHorizontal();

                Rect emptyRect = GUILayoutUtility.GetRect(0, 80, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(emptyRect, CommonStyles.EmptyAreaBackgroundColor);

                GUI.Label(emptyRect, TJGeneratorsL10n.L("尚未生成天空盒\n生成后预览将在此处显示"), CommonStyles.CenteredGreyLabelStyle);

                GUILayout.EndHorizontal();
            }
        }
        
        // ========== 历史生成记录面板 ==========
        
        private void DrawHistoryPanel(float panelWidth)
        {
            DrawStandardHistoryPanel(panelWidth, new StandardHistoryPanelOptions
            {
                GetLargePreviewTexture = GetPreviewTextureForHistoryItem,
                DrawTilePreview = DrawSkyboxHistoryPreview,
                GetModelLabel = item => GetSkyboxModelDisplayLabel(item.modelVersion),
                ShowContextMenu = ShowHistoryContextMenu,
                DrawHistoryActions = DrawHistoryActions,
            });
        }
        
        /// <summary>
        /// 获取某条历史记录的预览纹理，供主预览区或缩略图使用（会写入 historyPreviewCache / urlPreviewCache）。
        /// </summary>
        private Texture2D GetPreviewTextureForHistoryItem(TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null || item.isGenerating) return null;

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
                var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(item.modelPath);
                if (cubemap != null)
                {
                    var preview = AssetPreview.GetAssetPreview(cubemap);
                    if (preview != null)
                    {
                        historyPreviewCache[item.modelPath] = preview;
                        return preview;
                    }
                }
                string absPath = PathUtils.ToAbsoluteAssetPath(item.modelPath);
                if (File.Exists(absPath))
                {
                    EnqueuePreviewLoad(item.modelPath, absPath, false);
                }
            }

            if (!item.isTextToModel && !string.IsNullOrEmpty(item.imagePath) &&
                historyPreviewCache.TryGetValue(item.imagePath, out var uploadedPreview) && uploadedPreview != null)
                return uploadedPreview;

            if (item.isTextToModel && !string.IsNullOrEmpty(item.previewImageUrl) &&
                urlPreviewCache.TryGetValue(item.previewImageUrl, out var urlPreview) && urlPreview != null)
                return urlPreview;

            return null;
        }

        private void DrawSkyboxHistoryPreview(Rect rect, TJGeneratorsGenerationHistoryItem item)
        {
            if (item.isGenerating)
            {
                var fallbackStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 10, wordWrap = true };
                UIComponents.DrawLoadingSpinner(rect, fallbackStyle, Repaint);
                return;
            }
            
            // 历史记录以 .png 为准：优先用 modelPath（每条历史对应的唯一 PNG）
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
                var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(item.modelPath);
                if (cubemap != null)
                {
                    var preview = AssetPreview.GetAssetPreview(cubemap);
                    if (preview != null)
                    {
                        historyPreviewCache[item.modelPath] = preview;
                        GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit);
                        return;
                    }
                }
                string absPath = PathUtils.ToAbsoluteAssetPath(item.modelPath);
                if (File.Exists(absPath))
                {
                    EnqueuePreviewLoad(item.modelPath, absPath, false);
                }
            }
            
            // 回退：上传图或 URL 预览图
            if (!item.isTextToModel && !string.IsNullOrEmpty(item.imagePath))
            {
                if (historyPreviewCache.TryGetValue(item.imagePath, out var uploadedPreview) && uploadedPreview != null)
                {
                    GUI.DrawTexture(rect, uploadedPreview, ScaleMode.ScaleToFit);
                    return;
                }
                if (File.Exists(item.imagePath))
                {
                    EnqueuePreviewLoad(item.imagePath, item.imagePath, false);
                }
            }
            if (item.isTextToModel && !string.IsNullOrEmpty(item.previewImageUrl))
            {
                if (urlPreviewCache.TryGetValue(item.previewImageUrl, out var urlPreview) && urlPreview != null)
                {
                    GUI.DrawTexture(rect, urlPreview, ScaleMode.ScaleToFit);
                    return;
                }
                var localPreview = TryGetOrQueueSkyboxPreviewFromLocalCache(item.previewImageUrl);
                if (localPreview != null)
                {
                    GUI.DrawTexture(rect, localPreview, ScaleMode.ScaleToFit);
                    return;
                }
                if (!urlPreviewLoading.Contains(item.previewImageUrl) && !urlPreviewFailed.Contains(item.previewImageUrl))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(DownloadSkyboxPreviewImage(item.previewImageUrl));
                }
            }
            
            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f, 1f));
            var iconRect2 = new Rect(rect.x + rect.width / 4, rect.y + rect.height / 4, rect.width / 2, rect.height / 2);
            GUI.Label(iconRect2, EditorGUIUtility.IconContent("d_Texture2D Icon"));
        }
        
        private Texture2D TryGetOrQueueSkyboxPreviewFromLocalCache(string imageUrl)
        {
            string cacheDir = Path.Combine(Application.dataPath, "../Library/AI.TJGenerators/PreviewCache");
            string hash = imageUrl.GetHashCode().ToString("X8");
            string path = Path.Combine(cacheDir, hash + ".png");
            if (!File.Exists(path))
                return null;

            EnqueuePreviewLoad(imageUrl, path, true);
            if (urlPreviewCache.TryGetValue(imageUrl, out var cached) && cached != null)
                return cached;
            return null;
        }

        private IEnumerator DownloadSkyboxPreviewImage(string imageUrl)
        {
            urlPreviewLoading.Add(imageUrl);
            using (var uwr = UnityWebRequestTexture.GetTexture(imageUrl))
            {
                yield return uwr.SendWebRequest();
                if (UnityWebRequestCompat.IsSuccess(uwr) && uwr.downloadHandler.data != null)
                {
                    var tex = DownloadHandlerTexture.GetContent(uwr);
                    if (tex != null)
                    {
                        urlPreviewCache[imageUrl] = tex;
                        string cacheDir = Path.Combine(Application.dataPath, "../Library/AI.TJGenerators/PreviewCache");
                        if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
                        string hash = imageUrl.GetHashCode().ToString("X8");
                        File.WriteAllBytes(Path.Combine(cacheDir, hash + ".png"), tex.EncodeToPNG());
                    }
                }
                else
                {
                    urlPreviewFailed.Add(imageUrl);
                }
            }
            urlPreviewLoading.Remove(imageUrl);
            Repaint();
        }
        
        private string GetSkyboxModelDisplayLabel(string modelVersion)
        {
            return GetModelDisplayLabelFromIndex(modelVersion);
        }
        
        private void DrawHistoryActions()
        {
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            bool hasSelection = selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count;
            bool isGenerating = hasSelection && generationHistory[selectedHistoryIndex].isGenerating;
            GUI.enabled = hasSelection && !isGenerating;
            
            if (GUILayout.Button(TJGeneratorsL10n.L("应用到当前天空盒"), GUILayout.Height(25)))
            {
                ApplyHistoryToSkybox(selectedHistoryIndex);
            }
            if (GUILayout.Button(TJGeneratorsL10n.L("在项目中显示"), GUILayout.Height(25)))
            {
                ShowHistoryInProject(selectedHistoryIndex);
            }
            GUI.enabled = true;
            
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical();
            GUILayout.Space(6);
            currentHistoryTileSize = GUILayout.HorizontalSlider(currentHistoryTileSize, MinHistoryTileSize, MaxHistoryTileSize, GUILayout.Width(60f));
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
        }
        
        private void ShowHistoryContextMenu(int index)
        {
            var item = generationHistory[index];
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("应用到当前天空盒")), false, () => ApplyHistoryToSkybox(index));
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在项目中显示")), false, () => ShowHistoryInProject(index));
            menu.AddSeparator("");
            if (!string.IsNullOrEmpty(item.modelPath))
                menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在资源管理器中显示")), false, () => EditorUtility.RevealInFinder(item.modelPath));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("从历史记录中移除")), false, () =>
            {
                TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                generationHistory = TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentSkyboxAssetGuid());
                if (selectedHistoryIndex >= generationHistory.Count)
                    selectedHistoryIndex = Mathf.Max(0, generationHistory.Count - 1);
                Repaint();
            });
            menu.ShowAsContext();
        }
        
        private void ApplyHistoryToSkybox(int index)
        {
            if (index < 0 || index >= generationHistory.Count) return;
            var item = generationHistory[index];
            
            if (string.IsNullOrEmpty(item.modelPath) || !File.Exists(PathUtils.ToAbsoluteAssetPath(item.modelPath)))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("该历史记录的纹理文件不存在，可能已被删除。"), LogTag);
                TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                generationHistory = TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentSkyboxAssetGuid());
                Repaint();
                return;
            }
            
            if (targetSkyboxAsset == null || !targetSkyboxAsset.IsValid())
            {
                Debug.LogWarning($"{LogTag} {TJGeneratorsL10n.L("请先绑定或创建目标天空盒资产。")}");
                return;
            }
            
            string targetPath = targetSkyboxAsset.GetPath();
            if (!targetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                targetPath = Path.ChangeExtension(targetPath, ".png");
            
            if (!EditorUtility.DisplayDialog(TJGeneratorsL10n.L("确认替换"), string.Format(TJGeneratorsL10n.L("确定要将选中的历史天空盒应用到 {0} 吗？"), Path.GetFileName(targetPath)), TJGeneratorsL10n.L("确定"), TJGeneratorsL10n.L("取消")))
                return;
            
            try
            {
                string srcAbsolute = PathUtils.ToAbsoluteAssetPath(item.modelPath);
                string dstAbsolute = PathUtils.ToAbsoluteAssetPath(targetPath);
                File.Copy(srcAbsolute, dstAbsolute, true);
                AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                
                TextureImporter importer = AssetImporter.GetAtPath(targetPath) as TextureImporter;
                if (importer != null)
                {
                    importer.textureType = TextureImporterType.Default;
                    importer.textureShape = TextureImporterShape.TextureCube;
                    importer.SaveAndReimport();
                }
                
                if (skyboxPreviewTexture != null) { DestroyImmediate(skyboxPreviewTexture); skyboxPreviewTexture = null; }
                string absPath = PathUtils.ToAbsoluteAssetPath(targetPath);
                if (File.Exists(absPath))
                {
                    byte[] bytes = File.ReadAllBytes(absPath);
                    var tex = new Texture2D(2, 2);
                    if (tex.LoadImage(bytes))
                        skyboxPreviewTexture = tex;
                    else
                        DestroyImmediate(tex);
                }
                generatedSkybox = AssetDatabase.LoadAssetAtPath<Cubemap>(targetPath);
                TJLog.Log($"[TJGeneratorsSkybox] 已将历史天空盒应用到 {targetPath}");
            }
            catch (Exception e)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("应用失败: ") + e.Message, LogTag);
            }
            
            Repaint();
        }
        
        private void ShowHistoryInProject(int index)
        {
            if (index < 0 || index >= generationHistory.Count) return;
            var item = generationHistory[index];
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.modelPath);
            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);
                Selection.activeObject = asset;
            }
        }
        
        // ========== 辅助方法 ==========
        
        private void EnsureTargetSkybox()
        {
            if (targetSkyboxAsset != null && targetSkyboxAsset.IsValid())
                return;
            
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
            {
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            }
            
            string skyboxPath = AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/New Skybox.png");
            skyboxPath = CreateBlankSkybox(skyboxPath);
            
            if (string.IsNullOrEmpty(skyboxPath))
            {
                TJLog.LogError("[TJGeneratorsSkybox] 无法创建天空盒");
                return;
            }
            
            targetSkyboxAsset = TJGeneratorsAssetReference.FromPath(skyboxPath);
            titleContent = new GUIContent(string.Format(TJGeneratorsL10n.L("TJGenerators 天空盒 - {0}"), Path.GetFileNameWithoutExtension(skyboxPath)));
            
            if (!string.IsNullOrEmpty(targetSkyboxAsset.guid))
            {
                skyboxOpenWindows[targetSkyboxAsset.guid] = this;
            }
            
            Repaint();
        }
        
        /// <summary>
        /// 将项目内相对路径 (Assets/...) 转为磁盘绝对路径，用于 File/Directory，避免在 package 目录下创建文件。
        /// </summary>

        public static string CreateBlankSkybox(string path)
        {
            // 使用 .png 扩展名，Unity 会将其识别为纹理资产
            path = Path.ChangeExtension(path, ".png");
            string absolutePath = PathUtils.ToAbsoluteAssetPath(path);
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            // 创建一个横向排列的天空盒纹理（6个面水平排列）
            // 格式：6:1 宽高比，每个面是正方形
            int faceSize = 1024;
            int width = faceSize * 6; // 6个面横向排列
            int height = faceSize;
            
            // 创建空白纹理（黑色）
            Texture2D blankTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            Color[] defaultPixels = new Color[width * height];
            for (int i = 0; i < defaultPixels.Length; i++)
            {
                defaultPixels[i] = Color.black;
            }
            blankTexture.SetPixels(defaultPixels);
            blankTexture.Apply();
            
            // 保存为 PNG 文件（用绝对路径避免写到 package 目录）
            byte[] pngData = blankTexture.EncodeToPNG();
            File.WriteAllBytes(absolutePath, pngData);
            UnityEngine.Object.DestroyImmediate(blankTexture);
            
            // 导入资产（Unity会自动刷新）
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            
            // 设置导入设置为天空盒（TextureCube）
            TextureImporter textureImporter = AssetImporter.GetAtPath(path) as TextureImporter;
            if (textureImporter != null)
            {
                textureImporter.textureType = TextureImporterType.Default;
                textureImporter.textureShape = TextureImporterShape.TextureCube;
                textureImporter.SaveAndReimport();
            }
            else
            {
                TJLog.LogWarning($"[TJGeneratorsSkybox] 无法获取 TextureImporter for {path}");
            }

            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));
            
            return path;
        }
        
        private void StartGeneration()
        {
            if (_currentGenerator == null || string.IsNullOrWhiteSpace(textPrompt))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("请先选择模型并输入文本提示词"), LogTag);
                return;
            }
            
            // 确保目标天空盒资产存在
            EnsureTargetSkybox();
            
            isGenerating = true;
            generationStatus = TJGeneratorsL10n.L("准备中...");
            generationProgress = 0f;
            
            if (_currentGenerator is DynamicGenerator dynamicGen)
            {
                dynamicGen.SetTextPrompt(textPrompt);
                dynamicGen.SetImagePath(string.IsNullOrEmpty(imagePath) ? null : imagePath);
            }
            
            // 委托给 GenerationPipeline 处理（与主窗口共享同一套请求/轮询/下载流程）
            string assetGuid = targetSkyboxAsset?.guid ?? "";
            EditorCoroutineUtility.StartCoroutineOwnerless(_pipeline.StartGeneration(_currentGenerator, assetGuid));
        }
        
        // ========== IGenerationPipelineHost 实现 ==========
        
        public TJGeneratorsAssetReference GetTargetAsset()
        {
            return targetSkyboxAsset;
        }

        public void StartGeneration(ModelGeneratorBase generator)
        {
            if (generator == _currentGenerator)
                StartGeneration();
        }

        private string GetCurrentSkyboxAssetGuid()
        {
            return targetSkyboxAsset?.guid ?? "";
        }
        
        public void OnGenerationCompleted(string assetPath)
        {
            if (generationHistory != null && !string.IsNullOrEmpty(assetPath))
            {
                int index = generationHistory.FindIndex(x => x.modelPath == assetPath || x.imagePath == assetPath);
                if (index >= 0)
                {
                    selectedHistoryIndex = index;
                }
            }
        }


        /// <summary>
        /// 获取纹理资产的保存路径。每次生成使用唯一 .png 路径作为历史记录，便于每条历史对应不同图片。
        /// </summary>
        public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return null;

            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                AssetDatabase.CreateFolder("Assets/TJGenerators", "History");
            string uniqueName = "Skybox_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            return AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/History/" + uniqueName);
        }
        
        /// <summary>
        /// 纹理下载保存后的回调：savePath 为本次生成的唯一 .png（历史记录用）；同时复制到当前绑定资产并更新预览。
        /// </summary>
        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return;

            TJLog.Log($"[TJGeneratorsSkybox] OnAssetSaved: {savePath}");

            // 设置为天空盒（TextureCube）
            TextureImporter textureImporter = AssetImporter.GetAtPath(savePath) as TextureImporter;
            if (textureImporter != null)
            {
                textureImporter.textureType = TextureImporterType.Default;
                textureImporter.textureShape = TextureImporterShape.TextureCube;
                textureImporter.SaveAndReimport();
            }
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));

            // 确保有绑定的目标天空盒，并复制本次生成到目标路径，作为“当前”天空盒
            EnsureTargetSkybox();
            string pathToShow = savePath;
            if (targetSkyboxAsset != null && targetSkyboxAsset.IsValid())
            {
                string targetPath = targetSkyboxAsset.GetPath();
                if (!targetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    targetPath = Path.ChangeExtension(targetPath, ".png");
                try
                {
                    string srcAbsolute = PathUtils.ToAbsoluteAssetPath(savePath);
                    string dstAbsolute = PathUtils.ToAbsoluteAssetPath(targetPath);
                    File.Copy(srcAbsolute, dstAbsolute, true);
                    AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                    TextureImporter targetImporter = AssetImporter.GetAtPath(targetPath) as TextureImporter;
                    if (targetImporter != null)
                    {
                        targetImporter.textureType = TextureImporterType.Default;
                        targetImporter.textureShape = TextureImporterShape.TextureCube;
                        targetImporter.SaveAndReimport();
                    }
                    pathToShow = targetPath;
                }
                catch (Exception e)
                {
                    TJLog.LogWarning($"[TJGeneratorsSkybox] 复制到目标天空盒失败: {e.Message}");
                }
            }

            // 从最终展示路径加载预览
            if (skyboxPreviewTexture != null)
            {
                DestroyImmediate(skyboxPreviewTexture);
                skyboxPreviewTexture = null;
            }
            string absoluteShowPath = PathUtils.ToAbsoluteAssetPath(pathToShow);
            if (File.Exists(absoluteShowPath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(absoluteShowPath);
                    var tex = new Texture2D(2, 2);
                    if (tex.LoadImage(bytes))
                        skyboxPreviewTexture = tex;
                    else
                        DestroyImmediate(tex);
                }
                catch (Exception e)
                {
                    TJLog.LogWarning($"[TJGeneratorsSkybox] 加载预览图失败: {e.Message}");
                }
            }

            Cubemap loadedSkyboxCubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(pathToShow);
            if (loadedSkyboxCubemap != null)
            {
                generatedSkybox = loadedSkyboxCubemap;
                skyboxPreviewMaterial = new Material(Shader.Find("Skybox/Cubemap"));
                skyboxPreviewMaterial.SetTexture("_Tex", loadedSkyboxCubemap);
                TJLog.Log($"[TJGeneratorsSkybox] Skybox 保存成功: {savePath}，当前天空盒: {pathToShow}");
            }

            var skyboxTextureAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(pathToShow);
            if (skyboxTextureAsset != null)
            {
                Selection.activeObject = skyboxTextureAsset;
                EditorGUIUtility.PingObject(skyboxTextureAsset);
            }
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(pathToShow));

            // 自动应用到场景天空盒（使用材质副本，避免关闭窗口时 CleanupResources 销毁预览材质连带清空场景天空盒）
            if (skyboxPreviewMaterial != null && skyboxPreviewMaterial.GetTexture("_Tex") != null)
            {
                ApplySkyboxMaterialToSceneFromPreviewSource(skyboxPreviewMaterial);
                TJLog.Log("[TJGeneratorsSkybox] 天空盒已自动应用到场景");
            }

            generationStatus = TJGeneratorsL10n.L("完成");
            generationProgress = 1f;
            isGenerating = false;
        }

        private void ApplyToSceneSkybox()
        {
            Material materialToApply = null;

            if (skyboxPreviewMaterial != null && skyboxPreviewMaterial.GetTexture("_Tex") != null)
            {
                materialToApply = skyboxPreviewMaterial;
            }
            else if (selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count)
            {
                var item = generationHistory[selectedHistoryIndex];
                if (!item.isGenerating && !string.IsNullOrEmpty(item.modelPath))
                {
                    var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(item.modelPath);
                    if (cubemap != null)
                    {
                        var mat = new Material(Shader.Find("Skybox/Cubemap"));
                        mat.SetTexture("_Tex", cubemap);
                        materialToApply = mat;
                    }
                }
            }

            if (materialToApply == null)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("请先生成天空盒"), LogTag);
                return;
            }

            bool usedPreviewMaterial = skyboxPreviewMaterial != null && ReferenceEquals(materialToApply, skyboxPreviewMaterial);
            if (usedPreviewMaterial)
                ApplySkyboxMaterialToSceneFromPreviewSource(skyboxPreviewMaterial);
            else
            {
                RenderSettings.skybox = materialToApply;
                EditorUtility.SetDirty(RenderSettings.skybox);
            }
            TJLog.Log("[TJGeneratorsSkybox] 天空盒已应用到场景");
        }

        /// <summary>
        /// 从预览材质复制一份赋给 RenderSettings.skybox；关闭面板时会销毁预览材质，不能与场景共用同一实例。
        /// </summary>
        private static void ApplySkyboxMaterialToSceneFromPreviewSource(Material previewMaterial)
        {
            if (previewMaterial == null || previewMaterial.GetTexture("_Tex") == null)
                return;
            var instance = new Material(previewMaterial);
            RenderSettings.skybox = instance;
            EditorUtility.SetDirty(instance);
        }
        
        private void SaveSkyboxMaterial()
        {
            if (skyboxPreviewMaterial == null)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("请先生成天空盒"), LogTag);
                return;
            }
            
            string savePath = EditorUtility.SaveFilePanelInProject(
                TJGeneratorsL10n.L("保存天空盒材质"),
                "SkyboxMaterial",
                "mat",
                TJGeneratorsL10n.L("选择保存位置"));
            
            if (!string.IsNullOrEmpty(savePath))
            {
                AssetDatabase.CreateAsset(skyboxPreviewMaterial, savePath);
                AssetDatabase.SaveAssets();
                TJLog.Log($"[TJGeneratorsSkybox] 天空盒材质已保存: {savePath}");
            }
        }
    }
}
#endif
