#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    /// TJGenerators 世界生成窗口 - 使用 World Labs Marble 生成 3D 世界
    /// </summary>
    public class TJGeneratorsWorldWindow : GenerationWindowBase, IGenerationPipelineHost, IGenerationTriggerHost, IMediaAssetPipelineHost
    {
        // ========== 基类抽象属性实现 ==========
        protected override ConfigType WindowConfigType => ConfigType.World;
        protected override string LogTag => "[TJGeneratorsWorld]";

        // ========== 用户输入 ==========
        private string textPrompt = "";
        private string imagePath = "";
        private Texture2D uploadedImage;

        // ========== 目标资产 ==========
        [SerializeField]
        private TJGeneratorsAssetReference targetWorldAsset;
        private static Dictionary<string, TJGeneratorsWorldWindow> worldOpenWindows = new Dictionary<string, TJGeneratorsWorldWindow>();

        // ========== 世界预览 ==========
        private Texture2D worldPreviewTexture;

        // ========== 公开方法 ==========

        public static void ShowWindow()
        {
            var rect = GetDefaultMainWindowRect();
            var window = GetWindowWithRect<TJGeneratorsWorldWindow>(
                rect,
                utility: false,
                title: TJGeneratorsL10n.L("TJGenerators 世界生成"),
                focus: true
            );
            window.titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 世界生成"));
            FinalizeMainWindowShow(window, rect);
        }

        /// <summary>
        /// 为指定的世界资产打开窗口
        /// </summary>
        public static void OpenForAsset(string assetPath)
        {
            GenerationWindowBase.OpenForAsset(
                assetPath,
                worldOpenWindows,
                "[TJGeneratorsWorld]",
                TJGeneratorsL10n.L("TJGenerators 世界 - {0}"),
                () =>
                {
                    var window = CreateInstance<TJGeneratorsWorldWindow>();
                    return window;
                },
                (w, r) => w.targetWorldAsset = r,
                ShowWindow);
        }

        protected override void OnBootstrapWindowContent()
        {
            if (targetWorldAsset != null && !string.IsNullOrEmpty(targetWorldAsset.guid))
                worldOpenWindows[targetWorldAsset.guid] = this;

            InitializeGeneratorsFromConfig(ConfigType.World);
            OnRefreshWindowContent();
        }

        protected override void OnRefreshWindowContent()
        {
            if (targetWorldAsset == null || !targetWorldAsset.IsValid())
                EnsureTargetWorld();
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
            if (targetWorldAsset != null && !string.IsNullOrEmpty(targetWorldAsset.guid))
            {
                worldOpenWindows.Remove(targetWorldAsset.guid);
            }

            CleanupResources();
            ClearPreviewCaches();
        }

        // ========== 任务恢复 ==========

        protected override string GetCurrentAssetGuid() => GetCurrentWorldAssetGuid();

        protected override void SetHistory(List<TJGeneratorsGenerationHistoryItem> history) => generationHistory = history;

        protected override void OnGeneratorRestoredFromTask(ModelGeneratorBase generator)
        {
            base.OnGeneratorRestoredFromTask(generator);
            isGenerating = true;
            generationStatus = TJGeneratorsL10n.L("恢复中...");
        }

        /// <summary>
        /// 世界窗口优先选择 worldlabs 生成器
        /// </summary>
        protected override int GetFallbackGeneratorIndex()
        {
            if (TryGetGeneratorIndex("worldlabs", out int worldlabsIndex) && worldlabsIndex >= 0)
                return worldlabsIndex;
            return 0;
        }

        private void CleanupResources()
        {
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
                        TJGeneratorsL10n.L("目标世界"),
                        DrawTargetHeaderContentRect,
                        SelectTargetWorldAsset
                    );
                    GUILayout.Space(CommonStyles.Space2);
                    UIComponents.DrawModelSelector(
                        currentSelectedModel?.Name ?? _currentGenerator?.DisplayName ?? TJGeneratorsL10n.L("未选择"),
                        currentSelectedModel,
                        OnModelSelected,
                        ConfigType.World
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

            DrawHistoryPanel(currentHistoryPanelWidth);
            GUILayout.EndHorizontal();

            DrawLeftPanelBottomDock(splitLayout.LeftPanelWidth, DrawGenerationSection);
        }

        private void DrawTargetHeaderContentRect(Rect rect)
        {
            if (targetWorldAsset != null && targetWorldAsset.IsValid())
            {
                string worldName = Path.GetFileNameWithoutExtension(targetWorldAsset.GetPath());
                if (GUI.Button(rect, worldName, CommonStyles.TargetPrefabNameStyle))
                    SelectTargetWorldAsset();
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

        private void SelectTargetWorldAsset()
        {
            if (targetWorldAsset == null || !targetWorldAsset.IsValid())
                return;
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(targetWorldAsset.GetPath());
            if (obj != null)
            {
                EditorGUIUtility.PingObject(obj);
                Selection.activeObject = obj;
            }
        }

        private void DrawInputSection()
        {
            var genConfig = GetCurrentGeneratorConfig();
            textPrompt = DrawConfiguredTextPromptInput(textPrompt, "world_prompt_input", genConfig);

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

        // ========== 历史生成记录面板 ==========

        private void DrawHistoryPanel(float panelWidth)
        {
            DrawStandardHistoryPanel(panelWidth, new StandardHistoryPanelOptions
            {
                GetLargePreviewTexture = GetPreviewTextureForHistoryItem,
                DrawTilePreview = DrawWorldHistoryPreview,
                GetModelLabel = item => GetModelDisplayLabelFromIndex(item.modelVersion),
                ShowContextMenu = ShowHistoryContextMenu,
                DrawHistoryActions = DrawHistoryActions,
            });
        }

        /// <summary>
        /// 获取某条历史记录的预览纹理
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

        private void DrawWorldHistoryPreview(Rect rect, TJGeneratorsGenerationHistoryItem item)
        {
            if (item.isGenerating)
            {
                var fallbackStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 10, wordWrap = true };
                UIComponents.DrawLoadingSpinner(rect, fallbackStyle, Repaint);
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
                {
                    EnqueuePreviewLoad(item.modelPath, absPath, false);
                }
            }

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
                var localPreview = TryGetOrQueueWorldPreviewFromLocalCache(item.previewImageUrl);
                if (localPreview != null)
                {
                    GUI.DrawTexture(rect, localPreview, ScaleMode.ScaleToFit);
                    return;
                }
                if (!urlPreviewLoading.Contains(item.previewImageUrl) && !urlPreviewFailed.Contains(item.previewImageUrl))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(DownloadWorldPreviewImage(item.previewImageUrl));
                }
            }

            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f, 1f));
            var iconRect2 = new Rect(rect.x + rect.width / 4, rect.y + rect.height / 4, rect.width / 2, rect.height / 2);
            GUI.Label(iconRect2, EditorGUIUtility.IconContent("d_Texture2D Icon"));
        }

        private Texture2D TryGetOrQueueWorldPreviewFromLocalCache(string imageUrl)
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

        private IEnumerator DownloadWorldPreviewImage(string imageUrl)
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

        private void DrawHistoryActions()
        {
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            bool hasSelection = selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count;
            bool isGeneratingItem = hasSelection && generationHistory[selectedHistoryIndex].isGenerating;
            GUI.enabled = hasSelection && !isGeneratingItem;

            if (GUILayout.Button(TJGeneratorsL10n.L("应用到当前世界"), GUILayout.Height(25)))
            {
                ApplyHistoryToWorld(selectedHistoryIndex);
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
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("应用到当前世界")), false, () => ApplyHistoryToWorld(index));
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在项目中显示")), false, () => ShowHistoryInProject(index));
            menu.AddSeparator("");
            if (!string.IsNullOrEmpty(item.modelPath))
                menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在资源管理器中显示")), false, () => EditorUtility.RevealInFinder(item.modelPath));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("从历史记录中移除")), false, () =>
            {
                TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                generationHistory = TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentWorldAssetGuid());
                if (selectedHistoryIndex >= generationHistory.Count)
                    selectedHistoryIndex = Mathf.Max(0, generationHistory.Count - 1);
                Repaint();
            });
            menu.ShowAsContext();
        }

        private void ApplyHistoryToWorld(int index)
        {
            if (index < 0 || index >= generationHistory.Count) return;
            var item = generationHistory[index];

            if (string.IsNullOrEmpty(item.modelPath) || !File.Exists(PathUtils.ToAbsoluteAssetPath(item.modelPath)))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("该历史记录的纹理文件不存在，可能已被删除。"), LogTag);
                TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                generationHistory = TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentWorldAssetGuid());
                Repaint();
                return;
            }

            if (targetWorldAsset == null || !targetWorldAsset.IsValid())
            {
                Debug.LogWarning($"{LogTag} {TJGeneratorsL10n.L("请先绑定或创建目标世界资产。")}");
                return;
            }

            string targetPath = targetWorldAsset.GetPath();
            if (!targetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                targetPath = Path.ChangeExtension(targetPath, ".png");

            if (!EditorUtility.DisplayDialog(TJGeneratorsL10n.L("确认替换"), string.Format(TJGeneratorsL10n.L("确定要将选中的历史世界应用到 {0} 吗？"), Path.GetFileName(targetPath)), TJGeneratorsL10n.L("确定"), TJGeneratorsL10n.L("取消")))
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
                    importer.SaveAndReimport();
                }

                if (worldPreviewTexture != null) { DestroyImmediate(worldPreviewTexture); worldPreviewTexture = null; }
                string absPath = PathUtils.ToAbsoluteAssetPath(targetPath);
                if (File.Exists(absPath))
                {
                    byte[] bytes = File.ReadAllBytes(absPath);
                    var tex = new Texture2D(2, 2);
                    if (tex.LoadImage(bytes))
                        worldPreviewTexture = tex;
                    else
                        DestroyImmediate(tex);
                }
                TJLog.Log($"[TJGeneratorsWorld] 已将历史世界应用到 {targetPath}");
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

        private void EnsureTargetWorld()
        {
            if (targetWorldAsset != null && targetWorldAsset.IsValid())
                return;

            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
            {
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            }

            string worldPath = AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/New World.png");
            worldPath = CreateBlankWorldPreview(worldPath);

            if (string.IsNullOrEmpty(worldPath))
            {
                TJLog.LogError("[TJGeneratorsWorld] 无法创建世界资产");
                return;
            }

            targetWorldAsset = TJGeneratorsAssetReference.FromPath(worldPath);
            titleContent = new GUIContent(string.Format(TJGeneratorsL10n.L("TJGenerators 世界 - {0}"), Path.GetFileNameWithoutExtension(worldPath)));

            if (!string.IsNullOrEmpty(targetWorldAsset.guid))
            {
                worldOpenWindows[targetWorldAsset.guid] = this;
            }

            Repaint();
        }

        public static string CreateBlankWorldPreview(string path)
        {
            path = Path.ChangeExtension(path, ".png");
            string absolutePath = PathUtils.ToAbsoluteAssetPath(path);
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            int width = 1024;
            int height = 1024;

            Texture2D blankTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            Color[] defaultPixels = new Color[width * height];
            for (int i = 0; i < defaultPixels.Length; i++)
            {
                defaultPixels[i] = Color.black;
            }
            blankTexture.SetPixels(defaultPixels);
            blankTexture.Apply();

            byte[] pngData = blankTexture.EncodeToPNG();
            File.WriteAllBytes(absolutePath, pngData);
            UnityEngine.Object.DestroyImmediate(blankTexture);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            TextureImporter textureImporter = AssetImporter.GetAtPath(path) as TextureImporter;
            if (textureImporter != null)
            {
                textureImporter.textureType = TextureImporterType.Default;
                textureImporter.SaveAndReimport();
            }

            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));

            return path;
        }

        private void StartGeneration()
        {
            if (_currentGenerator == null)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("请先选择模型"), LogTag);
                return;
            }

            var genConfig = GetCurrentGeneratorConfig();
            var uiLayout = genConfig?.uiLayout ?? new UILayoutConfig();
            bool hasText = !string.IsNullOrWhiteSpace(textPrompt);
            bool hasImage = !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath);
            if (!hasText && !hasImage)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("请输入提示词或上传图片"), LogTag);
                return;
            }

            EnsureTargetWorld();

            isGenerating = true;
            generationStatus = TJGeneratorsL10n.L("准备中...");
            generationProgress = 0f;

            if (_currentGenerator is DynamicGenerator dynamicGen)
            {
                dynamicGen.SetTextPrompt(textPrompt);
                dynamicGen.SetImagePath(string.IsNullOrEmpty(imagePath) ? null : imagePath);
            }

            string assetGuid = targetWorldAsset?.guid ?? "";
            EditorCoroutineUtility.StartCoroutineOwnerless(_pipeline.StartGeneration(_currentGenerator, assetGuid));
        }

        // ========== IGenerationPipelineHost 实现 ==========

        public TJGeneratorsAssetReference GetTargetAsset()
        {
            return targetWorldAsset;
        }

        public void StartGeneration(ModelGeneratorBase generator)
        {
            if (generator == _currentGenerator)
                StartGeneration();
        }

        private string GetCurrentWorldAssetGuid()
        {
            return targetWorldAsset?.guid ?? "";
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
        /// 获取纹理资产的保存路径。每次生成使用唯一 .png 路径作为历史记录。
        /// </summary>
        public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return null;

            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                AssetDatabase.CreateFolder("Assets/TJGenerators", "History");
            string uniqueName = "World_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            return AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/History/" + uniqueName);
        }

        /// <summary>
        /// 纹理下载保存后的回调：复制到当前绑定资产并更新预览。
        /// </summary>
        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Texture) return;

            TJLog.Log($"[TJGeneratorsWorld] OnAssetSaved: {savePath}");

            TextureImporter textureImporter = AssetImporter.GetAtPath(savePath) as TextureImporter;
            if (textureImporter != null)
            {
                textureImporter.textureType = TextureImporterType.Default;
                textureImporter.SaveAndReimport();
            }
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));

            EnsureTargetWorld();
            string pathToShow = savePath;
            if (targetWorldAsset != null && targetWorldAsset.IsValid())
            {
                string targetPath = targetWorldAsset.GetPath();
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
                        targetImporter.SaveAndReimport();
                    }
                    pathToShow = targetPath;
                }
                catch (Exception e)
                {
                    TJLog.LogWarning($"[TJGeneratorsWorld] 复制到目标世界失败: {e.Message}");
                }
            }

            if (worldPreviewTexture != null)
            {
                DestroyImmediate(worldPreviewTexture);
                worldPreviewTexture = null;
            }
            string absoluteShowPath = PathUtils.ToAbsoluteAssetPath(pathToShow);
            if (File.Exists(absoluteShowPath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(absoluteShowPath);
                    var tex = new Texture2D(2, 2);
                    if (tex.LoadImage(bytes))
                        worldPreviewTexture = tex;
                    else
                        DestroyImmediate(tex);
                }
                catch (Exception e)
                {
                    TJLog.LogWarning($"[TJGeneratorsWorld] 加载预览图失败: {e.Message}");
                }
            }

            var worldTextureAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(pathToShow);
            if (worldTextureAsset != null)
            {
                Selection.activeObject = worldTextureAsset;
                EditorGUIUtility.PingObject(worldTextureAsset);
            }
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(pathToShow));

            generationStatus = TJGeneratorsL10n.L("完成");
            generationProgress = 1f;
            isGenerating = false;

            // Download spz + collider mesh and create scene objects
            EditorCoroutineUtility.StartCoroutineOwnerless(DownloadAndProcessWorldAssets());
        }

        // ========== 世界资产下载与场景创建 ==========

        private IEnumerator DownloadAndProcessWorldAssets()
        {
            var response = _pipeline?.LastCompletedResponse;
            if (response?.output?.data?.assets == null)
                yield break;

            var assets = response.output.data.assets;
            var spzUrls = assets.spzUrls;
            string meshUrl = assets.colliderMeshUrl;

            string outputFolder = "Assets/TJGenerators/World";
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            if (!AssetDatabase.IsValidFolder(outputFolder))
                AssetDatabase.CreateFolder("Assets/TJGenerators", "World");

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            // Download all spz variants (full_res used for scene, others saved for user)
            string fullResSpzPath = null;
            if (spzUrls != null)
            {
                var variants = new (string url, string label)[]
                {
                    (spzUrls.full_res, "full_res"),
                    (spzUrls._500k, "500k"),
                    (spzUrls._150k, "150k"),
                    (spzUrls._100k, "100k"),
                };

                foreach (var (url, label) in variants)
                {
                    if (string.IsNullOrEmpty(url))
                        continue;
                    string variantPath = $"{outputFolder}/World_{timestamp}_{label}.spz";
                    generationStatus = TJGeneratorsL10n.L("下载 Gaussian Splatting 数据 ({0})...", label);
                    Repaint();
                    yield return DownloadFile(url, variantPath);
                    if (label == "full_res")
                        fullResSpzPath = variantPath;
                }
            }

            // Download collider mesh
            string meshPath = $"{outputFolder}/World_{timestamp}_collider.glb";
            if (!string.IsNullOrEmpty(meshUrl))
            {
                generationStatus = TJGeneratorsL10n.L("下载 Collider Mesh...");
                Repaint();
                yield return DownloadFile(meshUrl, meshPath);
                AssetDatabase.ImportAsset(meshPath, ImportAssetOptions.ForceUpdate);
            }

            // Create GaussianSplat asset from full_res spz via reflection
            if (!string.IsNullOrEmpty(fullResSpzPath) && File.Exists(PathUtils.ToAbsoluteAssetPath(fullResSpzPath)))
            {
                generationStatus = TJGeneratorsL10n.L("创建 Gaussian Splat 资产...");
                Repaint();
                string createdAssetPath = TryCreateGaussianSplatAsset(fullResSpzPath, outputFolder, timestamp);
                if (!string.IsNullOrEmpty(createdAssetPath))
                {
                    CreateWorldGameObject(createdAssetPath, meshPath);
                }
                else
                {
                    TJLog.LogWarning("[TJGeneratorsWorld] GaussianSplat 资产创建失败，spz 文件已保存到: " + fullResSpzPath);
                }
            }
        }

        private IEnumerator DownloadFile(string url, string assetPath)
        {
            url = PathUtils.NormalizeRemoteUrl(url);
            string absolutePath = PathUtils.ToAbsoluteAssetPath(assetPath);
            string dir = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var uwr = UnityWebRequest.Get(url))
            {
                yield return uwr.SendWebRequest();
                if (UnityWebRequestCompat.IsSuccess(uwr))
                {
                    File.WriteAllBytes(absolutePath, uwr.downloadHandler.data);
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                    TJLog.Log($"[TJGeneratorsWorld] 下载完成: {assetPath} ({uwr.downloadedBytes} bytes)");
                }
                else
                {
                    TJLog.LogError($"[TJGeneratorsWorld] 下载失败: {url} - {uwr.error}");
                }
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        private static void SetPrivateField(object obj, string fieldName, object value)
        {
            var field = obj.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            field?.SetValue(obj, value);
        }

        private static object GetPrivateField(object obj, string fieldName)
        {
            var field = obj.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(obj);
        }

        /// <summary>
        /// 通过反射调用 GaussianSplatAssetCreator.CreateAsset() 从 spz 文件创建 GaussianSplat 资产。
        /// </summary>
        private string TryCreateGaussianSplatAsset(string spzPath, string outputFolder, string baseName)
        {
            try
            {
                Type creatorType = FindType("GaussianSplatting.Editor.GaussianSplatAssetCreator");
                if (creatorType == null)
                {
                    TJLog.LogWarning("[TJGeneratorsWorld] 未找到 GaussianSplatAssetCreator 类型，请确认 UnityGaussianSplatting 包已安装");
                    return null;
                }

                Type assetType = FindType("GaussianSplatting.Runtime.GaussianSplatAsset");
                if (assetType == null)
                {
                    TJLog.LogWarning("[TJGeneratorsWorld] 未找到 GaussianSplatAsset 类型");
                    return null;
                }

                Type vectorFormatType = FindType("GaussianSplatting.Runtime.GaussianSplatAsset+VectorFormat");
                Type colorFormatType = FindType("GaussianSplatting.Runtime.GaussianSplatAsset+ColorFormat");
                Type shFormatType = FindType("GaussianSplatting.Runtime.GaussianSplatAsset+SHFormat");

                // Create creator instance
                var creator = ScriptableObject.CreateInstance(creatorType);

                // Set fields for Medium quality
                string absSpzPath = PathUtils.ToAbsoluteAssetPath(spzPath);
                SetPrivateField(creator, "m_InputFile", absSpzPath);
                SetPrivateField(creator, "m_OutputFolder", outputFolder);
                SetPrivateField(creator, "m_ImportCameras", false);

                if (vectorFormatType != null)
                {
                    SetPrivateField(creator, "m_FormatPos", Enum.Parse(vectorFormatType, "Norm11"));
                    SetPrivateField(creator, "m_FormatScale", Enum.Parse(vectorFormatType, "Norm11"));
                }
                if (colorFormatType != null)
                    SetPrivateField(creator, "m_FormatColor", Enum.Parse(colorFormatType, "Norm8x4"));
                if (shFormatType != null)
                    SetPrivateField(creator, "m_FormatSH", Enum.Parse(shFormatType, "Norm6"));

                // Call CreateAsset() via reflection
                var createMethod = creatorType.GetMethod("CreateAsset",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (createMethod == null)
                {
                    TJLog.LogWarning("[TJGeneratorsWorld] 未找到 CreateAsset 方法");
                    return null;
                }

                TJLog.Log($"[TJGeneratorsWorld] 开始创建 GaussianSplat 资产: {spzPath}");
                try
                {
                    createMethod.Invoke(creator, null);
                }
                catch (System.Reflection.TargetInvocationException tie)
                {
                    var inner = tie.InnerException;
                    TJLog.LogError($"[TJGeneratorsWorld] GaussianSplatAssetCreator.CreateAsset 异常: {inner?.Message}\n{inner?.StackTrace}");
                    return null;
                }

                // Check for errors
                string errorMessage = GetPrivateField(creator, "m_ErrorMessage") as string;
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    TJLog.LogError($"[TJGeneratorsWorld] GaussianSplat 资产创建失败: {errorMessage}");
                    return null;
                }

                // GaussianSplatAssetCreator.CreateAsset() names the asset after the input file name
                // (via FilePickerControl.PathToDisplayString), so the expected path is based on the spz file name
                PathUtils.SafeRefresh(ImportAssetOptions.ForceUpdate);
                string spzFileName = Path.GetFileNameWithoutExtension(absSpzPath);
                string expectedAssetPath = $"{outputFolder}/{spzFileName}.asset";
                if (File.Exists(PathUtils.ToAbsoluteAssetPath(expectedAssetPath)))
                {
                    TJLog.Log($"[TJGeneratorsWorld] GaussianSplat 资产创建成功: {expectedAssetPath}");
                    return expectedAssetPath;
                }

                // Fallback: search for any .asset file created in the output folder matching the spz base name
                string[] foundAssets = AssetDatabase.FindAssets($"{spzFileName} t:Object", new[] { outputFolder });
                foreach (string guid in foundAssets)
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    if (p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    {
                        TJLog.Log($"[TJGeneratorsWorld] GaussianSplat 资产创建成功 (fallback search): {p}");
                        return p;
                    }
                }

                TJLog.LogWarning("[TJGeneratorsWorld] GaussianSplat 资产文件未找到");
                return null;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[TJGeneratorsWorld] 创建 GaussianSplat 资产异常: {e.Message}\n{e.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// 创建包含 GaussianSplatRenderer 的 GameObject 并放入场景。
        /// </summary>
        private void CreateWorldGameObject(string splatAssetPath, string colliderMeshPath)
        {
            try
            {
                Type assetType = FindType("GaussianSplatting.Runtime.GaussianSplatAsset");
                Type rendererType = FindType("GaussianSplatting.Runtime.GaussianSplatRenderer");
                if (assetType == null || rendererType == null)
                {
                    TJLog.LogWarning("[TJGeneratorsWorld] 未找到 GaussianSplatRenderer 类型");
                    return;
                }

                // Load the GaussianSplat asset
                var splatAsset = AssetDatabase.LoadAssetAtPath(splatAssetPath, assetType);
                if (splatAsset == null)
                {
                    TJLog.LogError($"[TJGeneratorsWorld] 无法加载 GaussianSplat 资产: {splatAssetPath}");
                    return;
                }

                // Create GameObject
                GameObject go = new GameObject($"AI World {DateTime.Now:yyyyMMdd_HHmmss}");
                Undo.RegisterCreatedObjectUndo(go, "Create AI World");

                // Add GaussianSplatRenderer component
                Component renderer = go.AddComponent(rendererType);

                // Find and assign shaders from the package
                AssignShadersToRenderer(renderer, rendererType);

                // Set the splat asset
                var assetField = rendererType.GetField("m_Asset", BindingFlags.Public | BindingFlags.Instance);
                assetField?.SetValue(renderer, splatAsset);

                // Try to load and add collider mesh
                if (!string.IsNullOrEmpty(colliderMeshPath) && File.Exists(PathUtils.ToAbsoluteAssetPath(colliderMeshPath)))
                {
                    TryAddColliderMesh(go, colliderMeshPath);
                }

                // Set transform (world models typically need -160° X rotation)
                go.transform.rotation = Quaternion.Euler(-160f, 0f, 0f);

                var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);

                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
                TJLog.Log($"[TJGeneratorsWorld] 世界 GameObject 已创建: {go.name}");
            }
            catch (Exception e)
            {
                TJLog.LogError($"[TJGeneratorsWorld] 创建世界 GameObject 异常: {e.Message}\n{e.StackTrace}");
            }
        }

        private void AssignShadersToRenderer(Component renderer, Type rendererType)
        {
            // Load shaders from the GaussianSplatting package using known paths
            const string shaderBasePath = "Packages/org.nesnausk.gaussian-splatting/Shaders";

            var shaderSplats = AssetDatabase.LoadAssetAtPath<Shader>($"{shaderBasePath}/RenderGaussianSplats.shader");
            var shaderComposite = AssetDatabase.LoadAssetAtPath<Shader>($"{shaderBasePath}/GaussianComposite.shader");
            var shaderDebugPoints = AssetDatabase.LoadAssetAtPath<Shader>($"{shaderBasePath}/GaussianDebugRenderPoints.shader");
            var shaderDebugBoxes = AssetDatabase.LoadAssetAtPath<Shader>($"{shaderBasePath}/GaussianDebugRenderBoxes.shader");
            var csSplatUtilities = AssetDatabase.LoadAssetAtPath<ComputeShader>($"{shaderBasePath}/SplatUtilities.compute");

            if (shaderSplats != null)
                SetPrivateField(renderer, "m_ShaderSplats", shaderSplats);
            else
                TJLog.LogWarning("[TJGeneratorsWorld] 未找到 RenderGaussianSplats.shader");

            if (shaderComposite != null)
                SetPrivateField(renderer, "m_ShaderComposite", shaderComposite);
            else
                TJLog.LogWarning("[TJGeneratorsWorld] 未找到 GaussianComposite.shader");

            if (shaderDebugPoints != null)
                SetPrivateField(renderer, "m_ShaderDebugPoints", shaderDebugPoints);
            else
                TJLog.LogWarning("[TJGeneratorsWorld] 未找到 GaussianDebugRenderPoints.shader");

            if (shaderDebugBoxes != null)
                SetPrivateField(renderer, "m_ShaderDebugBoxes", shaderDebugBoxes);
            else
                TJLog.LogWarning("[TJGeneratorsWorld] 未找到 GaussianDebugRenderBoxes.shader");

            if (csSplatUtilities != null)
                SetPrivateField(renderer, "m_CSSplatUtilities", csSplatUtilities);
            else
                TJLog.LogWarning("[TJGeneratorsWorld] 未找到 SplatUtilities.compute");
        }

        private void TryAddColliderMesh(GameObject go, string meshPath)
        {
            var loadedAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(meshPath);
            if (loadedAsset is Mesh mesh)
            {
                var meshFilter = go.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = mesh;
                var collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
                TJLog.Log("[TJGeneratorsWorld] Collider mesh 已添加");
            }
            else if (loadedAsset is GameObject glbRoot)
            {
                // GLB importer might create a prefab
                var meshFilter = glbRoot.GetComponentInChildren<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    var mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = meshFilter.sharedMesh;
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = meshFilter.sharedMesh;
                    TJLog.Log("[TJGeneratorsWorld] Collider mesh 已从 GLB prefab 添加");
                }
            }
            else
            {
                TJLog.LogWarning($"[TJGeneratorsWorld] Collider mesh 文件已保存但无法自动导入: {meshPath}\n如需使用，请安装 GLB 导入器（如 GLTFast）");
            }
        }
    }
}
#endif
