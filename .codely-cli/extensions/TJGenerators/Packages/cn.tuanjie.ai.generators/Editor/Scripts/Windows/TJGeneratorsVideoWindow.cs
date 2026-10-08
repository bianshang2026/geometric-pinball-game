#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using UnityEngine.Networking;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;
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
    /// TJGenerators 视频生成窗口 - 文生视频 / 图生视频
    /// </summary>
    public class TJGeneratorsVideoWindow : GenerationWindowBase, IGenerationPipelineHost, IGenerationTriggerHost, IMediaAssetPipelineHost
    {
        // ========== 基类抽象属性实现 ==========
        protected override ConfigType WindowConfigType => ConfigType.Video;
        protected override string LogTag => "[TJGeneratorsVideo]";

        // ========== 窗口特定字段 ==========
        [SerializeField]
        private string textPrompt = "";

        [SerializeField]
        private TJGeneratorsAssetReference targetVideoAsset;

        private readonly List<string> referenceImagePaths = new List<string>();
        private readonly List<Texture2D> referenceUploadedImages = new List<Texture2D>();

        private string referenceVideoPath = "";

        private double _lastProgressRepaintTime;

        private static readonly Dictionary<string, TJGeneratorsVideoWindow> s_videoOpenWindows =
            new Dictionary<string, TJGeneratorsVideoWindow>();

        // ========== 静态入口 ==========

        public static void ShowWindow()
        {
            var rect = GetDefaultMainWindowRect();
            var window = GetWindowWithRect<TJGeneratorsVideoWindow>(
                rect,
                utility: false,
                title: TJGeneratorsL10n.L("TJGenerators 视频生成"),
                focus: true
            );
            window.titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 视频生成"));
            FinalizeMainWindowShow(window, rect);
        }

        /// <summary>
        /// 从指定资产路径打开视频生成窗口。
        /// </summary>
        public static void OpenForAsset(string assetPath)
        {
            GenerationWindowBase.OpenForAsset(
                assetPath,
                s_videoOpenWindows,
                "[TJGeneratorsVideo]",
                TJGeneratorsL10n.L("TJGenerators 视频 - {0}"),
                () =>
                {
                    var window = CreateInstance<TJGeneratorsVideoWindow>();
                    return window;
                },
                (w, r) => w.targetVideoAsset = r,
                ShowWindow);
        }

        protected override void OnBootstrapWindowContent()
        {
            if (targetVideoAsset != null && !string.IsNullOrEmpty(targetVideoAsset.guid))
                s_videoOpenWindows[targetVideoAsset.guid] = this;

            InitializeGeneratorsFromConfig(ConfigType.Video);
            OnRefreshWindowContent();
        }

        protected override void OnRefreshWindowContent()
        {
            RefreshHistory();
            CheckAndRecoverInterruptedTasks();
        }

        // ========== 生命周期 ==========

        protected override void OnEnable()
        {
            base.OnEnable();
            wantsMouseMove = true;

            EditorCoroutineUtility.StartCoroutineOwnerless(
                UserInfoHelper.GetUserInfoCoroutine(
                    ConfigManager.GetUserInfoUrl(),
                    OnUserInfoLoaded
                )
            );
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            wantsMouseMove = false;

            if (targetVideoAsset != null && !string.IsNullOrEmpty(targetVideoAsset.guid))
            {
                s_videoOpenWindows.Remove(targetVideoAsset.guid);
            }

            ClearPreviewCaches();

            foreach (var tex in referenceUploadedImages)
            {
                if (tex != null)
                    DestroyImmediate(tex);
            }
            referenceUploadedImages.Clear();
            referenceImagePaths.Clear();
        }

        // ========== 任务恢复 ==========

        protected override string GetCurrentAssetGuid() => GetCurrentVideoAssetGuid();

        protected override void SetHistory(List<TJGeneratorsGenerationHistoryItem> history) =>
            generationHistory = history;

        protected override void OnGeneratorRestoredFromTask(ModelGeneratorBase generator)
        {
            base.OnGeneratorRestoredFromTask(generator);
            isGenerating = true;
            generationStatus = TJGeneratorsL10n.L("恢复中...");
            Repaint();
        }

        // ========== UI ==========

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
                EditorGUI.DrawRect(
                    new Rect(0, 0, position.width, position.height),
                    CommonStyles.WindowBackgroundColor
                );
                EditorGUILayout.HelpBox(TJGeneratorsL10n.L("未找到可用的视频生成器，请检查 GeneratorConfig.json 中的 videoGenerators"), MessageType.Error);
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
                        TJGeneratorsL10n.L("目标视频"),
                        DrawTargetHeaderContentRect,
                        SelectTargetVideoAsset
                    );
                    GUILayout.Space(CommonStyles.Space2);

                    UIComponents.DrawModelSelector(
                        currentSelectedModel?.Name ?? _currentGenerator?.DisplayName ?? TJGeneratorsL10n.L("未选择"),
                        currentSelectedModel,
                        OnModelSelected,
                        ConfigType.Video
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
            if (targetVideoAsset != null && targetVideoAsset.IsValid())
            {
                string videoName = Path.GetFileNameWithoutExtension(targetVideoAsset.GetPath());
                if (GUI.Button(rect, videoName, CommonStyles.TargetPrefabNameStyle))
                    SelectTargetVideoAsset();
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            }
            else
            {
                GUI.Label(rect, TJGeneratorsL10n.L("未绑定（生成时自动创建）"), CommonStyles.ContentStyle);
            }
        }

        private void SelectTargetVideoAsset()
        {
            if (targetVideoAsset == null || !targetVideoAsset.IsValid())
                return;
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(targetVideoAsset.GetPath());
            if (obj != null)
            {
                EditorGUIUtility.PingObject(obj);
                Selection.activeObject = obj;
            }
        }

        private void OnModelSelected(AIModelInfo model) => OnModelSelectedBase(model);

        protected override void ResetInputStateAfterModelChange()
        {
            var config = GetCurrentGeneratorConfig();
            ResetTextPromptIfHidden(config, ref textPrompt);
            ClearReferenceImagesWhenUploadHidden(config, referenceImagePaths, referenceUploadedImages);
        }

        private void DrawInputSection()
        {
            var genConfig = GetCurrentGeneratorConfig();
            textPrompt = DrawConfiguredTextPromptInput(textPrompt, "video_prompt_input", genConfig);

            // Read current mode parameter value
            string currentMode = GetCurrentModeValue();

            // Show video upload slot for multimodal mode
            if (currentMode == "multimodal")
            {
                GUILayout.Space(CommonStyles.Space3);
                GUILayout.Label(TJGeneratorsL10n.L("参考视频（运镜/动作）"), CommonStyles.HeaderStyle);
                EditorGUI.BeginChangeCheck();
                var newVideoPath = EditorGUILayout.TextField("", referenceVideoPath);
                if (EditorGUI.EndChangeCheck())
                    referenceVideoPath = newVideoPath;

                Rect dropRect = GUILayoutUtility.GetLastRect();
                HandleVideoDragAndDrop(dropRect);

                if (GUILayout.Button(TJGeneratorsL10n.L("选择视频文件"), GUILayout.Height(22)))
                {
                    string selected = EditorUtility.OpenFilePanel(
                        TJGeneratorsL10n.L("选择参考视频"),
                        "Assets",
                        "mp4");
                    if (!string.IsNullOrEmpty(selected))
                    {
                        string relPath = PathUtils.TryGetAssetsRelativePathFromAbsolute(selected);
                        referenceVideoPath = !string.IsNullOrEmpty(relPath) ? relPath : selected;
                        Repaint();
                    }
                }
            }

            if (ShouldShowImageUpload(genConfig))
            {
                GUILayout.Space(CommonStyles.Space3);
                DrawConfiguredReferenceImageUpload(
                    referenceImagePaths,
                    referenceUploadedImages,
                    "video_reference_upload");
            }
        }

        private string GetCurrentModeValue()
        {
            if (_currentGenerator is DynamicGenerator dynamicGen)
            {
                var modeParam = dynamicGen.GetParameter("mode");
                if (modeParam != null) return modeParam.ToString();
            }
            return null;
        }

        private void HandleVideoDragAndDrop(Rect dropRect)
        {
            Event evt = Event.current;
            if (evt == null) return;

            if (evt.type == EventType.DragUpdated && dropRect.Contains(evt.mousePosition))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                evt.Use();
            }
            else if (evt.type == EventType.DragPerform && dropRect.Contains(evt.mousePosition))
            {
                DragAndDrop.AcceptDrag();
                if (DragAndDrop.paths != null && DragAndDrop.paths.Length > 0)
                {
                    string path = DragAndDrop.paths[0];
                    if (!string.IsNullOrEmpty(path) &&
                        (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)))
                    {
                        referenceVideoPath = path;
                        Repaint();
                    }
                }
                evt.Use();
            }
        }

        private void DrawConfigurationSection()
        {
            var provider = _currentGenerator as IGeneratorParameterProvider;

            // Filter out mode parameter (handled by input mode)
            var allParams = GetCurrentGeneratorParameters();
            showAdvancedSettings = DrawConfiguredAdvancedSettingsFoldout(
                showAdvancedSettings,
                provider,
                allParams
            );
        }

        private void DrawGenerationSection(LeftPanelBottomDock.Layout layout)
        {
            string currentMode = GetCurrentModeValue();
            bool canGenerate;
            if (currentMode == "multimodal")
                canGenerate = !string.IsNullOrWhiteSpace(textPrompt) || !string.IsNullOrEmpty(referenceVideoPath);
            else
                canGenerate = !string.IsNullOrWhiteSpace(textPrompt);
            UIComponents.DrawGenerationSectionAt(
                layout,
                isGenerating,
                generationProgress,
                generationStatus,
                canGenerate,
                StartGeneration,
                null,
                () =>
                {
                    double t = EditorApplication.timeSinceStartup;
                    if (t - _lastProgressRepaintTime > 0.1)
                    {
                        _lastProgressRepaintTime = t;
                        Repaint();
                    }
                });
        }

        private void DrawHistoryPanel(float panelWidth)
        {
            float historyPanelHeight = isVerticalLayout ? position.height * 0.45f : position.height;

            UIComponents.BeginHistoryPanel(panelWidth, historyPanelHeight, isVerticalLayout);

            float previewBlockHeight = DrawHistoryThumbnailPreviewBlock(panelWidth, historyPanelHeight);

            float scrollHeight = Mathf.Max(0f, historyPanelHeight - previewBlockHeight - 100f);
            historyScrollPosition = GUILayout.BeginScrollView(
                historyScrollPosition,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUILayout.Height(scrollHeight));

            if (generationHistory.Count == 0)
                UIComponents.DrawHistoryEmptyState();
            else
                DrawHistoryGrid();

            GUILayout.EndScrollView();

            DrawHistoryActions();

            UIComponents.EndHistoryPanel();
        }

        private void DrawHistoryGrid()
        {
            for (int i = 0; i < generationHistory.Count; i++)
            {
                if (i > 0)
                    GUILayout.Space(6f);

                var item = generationHistory[i];
                bool isSelected = selectedHistoryIndex == i;

                GUILayout.BeginHorizontal(GUIStyle.none, GUILayout.Height(50));

                Rect rowRect = GUILayoutUtility.GetRect(0, 50, GUILayout.ExpandWidth(true));

                if (Event.current.type == EventType.Repaint)
                {
                    Color bgColor = isSelected
                        ? CommonStyles.ThemeDarkGreenColor
                        : new Color(0.15f, 0.15f, 0.15f, 1f);
                    EditorGUI.DrawRect(rowRect, bgColor);
                }

                Rect labelRect = new Rect(rowRect.x + 8, rowRect.y + 5, rowRect.width - 16, 20);
                string displayName = item.isGenerating ? TJGeneratorsL10n.L("生成中...") : item.GetDisplayName();
                GUI.Label(labelRect, displayName, CommonStyles.HistoryLabelStyle);

                Rect subLabelRect = new Rect(rowRect.x + 8, rowRect.y + 25, rowRect.width - 16, 18);
                GUI.Label(subLabelRect, GetModelDisplayLabelFromIndex(item.modelVersion), CommonStyles.SmallGreyCenterLabelStyle);

                if (item.isGenerating)
                {
                    Repaint();
                }
                else if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
                {
                    selectedHistoryIndex = i;
                    Event.current.Use();
                    Repaint();
                }
                else if (Event.current.type == EventType.ContextClick && rowRect.Contains(Event.current.mousePosition))
                {
                    ShowHistoryContextMenu(i);
                    Event.current.Use();
                }

                GUILayout.EndHorizontal();
            }
        }

        private float DrawHistoryThumbnailPreviewBlock(float panelWidth, float historyPanelHeight)
        {
            if (selectedHistoryIndex < 0 || selectedHistoryIndex >= generationHistory.Count)
                return 0f;

            var selectedItem = generationHistory[selectedHistoryIndex];
            bool showPreview =
                selectedItem.isGenerating
                || !string.IsNullOrEmpty(selectedItem.modelPath)
                || !string.IsNullOrEmpty(selectedItem.previewImageUrl);
            if (!showPreview)
                return 0f;

            float maxPreviewWidth = Mathf.Max(0f, CommonStyles.HistoryPanelInnerWidth(panelWidth));
            if (maxPreviewWidth <= 0f || historyPanelHeight <= 0f)
                return 0f;

            float previewHeight = isVerticalLayout
                ? Mathf.Max(0f, historyPanelHeight * 0.5f)
                : Mathf.Min(maxPreviewWidth, 200f);
            float previewWidth = Mathf.Min(maxPreviewWidth, previewHeight);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var previewRect = GUILayoutUtility.GetRect(previewWidth, previewHeight);
            DrawVideoHistoryThumbnail(previewRect, selectedItem);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(5f);
            return previewHeight + 5f;
        }

        private void DrawVideoHistoryThumbnail(Rect rect, TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null || item.isGenerating)
            {
                UIComponents.DrawLoadingSpinner(rect, CommonStyles.SmallGreyLabelStyle, Repaint);
                return;
            }

            var tex = GetPreviewTextureForHistoryItem(item);
            if (tex != null)
            {
                GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit);
                return;
            }

            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f, 1f));
        }

        private Texture2D GetPreviewTextureForHistoryItem(TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null || item.isGenerating)
                return null;

            if (!string.IsNullOrEmpty(item.previewImageUrl))
            {
                if (
                    urlPreviewCache.TryGetValue(item.previewImageUrl, out var urlTex)
                    && urlTex != null
                )
                    return urlTex;

                string previewUrl = item.previewImageUrl;
                if (previewUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    string localPath = previewUrl.Substring("file://".Length).Replace('\\', '/');
                    string assetPath = PathUtils.TryGetAssetsRelativePathFromAbsolute(localPath)
                        ?? localPath;
                    if (
                        !string.IsNullOrEmpty(assetPath)
                        && historyPreviewCache.TryGetValue(assetPath, out var fileCached)
                        && fileCached != null
                    )
                        return fileCached;

                    var clipFromFile = AssetDatabase.LoadAssetAtPath<UnityEngine.Video.VideoClip>(assetPath);
                    var fileThumb = TryGetVideoClipThumbnail(clipFromFile, assetPath);
                    if (fileThumb != null)
                        return fileThumb;
                }
                else if (
                    previewUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || previewUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                )
                {
                    if (
                        !urlPreviewLoading.Contains(previewUrl)
                        && !urlPreviewFailed.Contains(previewUrl)
                    )
                    {
                        EditorCoroutineUtility.StartCoroutineOwnerless(
                            DownloadPreviewImage(previewUrl)
                        );
                    }
                }
            }

            if (!string.IsNullOrEmpty(item.modelPath))
            {
                if (
                    historyPreviewCache.TryGetValue(item.modelPath, out var cached)
                    && cached != null
                )
                    return cached;

                var clip = AssetDatabase.LoadAssetAtPath<UnityEngine.Video.VideoClip>(item.modelPath);
                return TryGetVideoClipThumbnail(clip, item.modelPath);
            }

            return null;
        }

        private Texture2D TryGetVideoClipThumbnail(UnityEngine.Video.VideoClip clip, string cacheKey)
        {
            if (clip == null || string.IsNullOrEmpty(cacheKey))
                return null;

            var preview =
                AssetPreview.GetAssetPreview(clip) as Texture2D
                ?? AssetPreview.GetMiniThumbnail(clip) as Texture2D;
            if (preview != null)
            {
                historyPreviewCache[cacheKey] = preview;
                return preview;
            }

#if UNITY_6000_5_OR_NEWER
            if (AssetPreview.IsLoadingAssetPreview(clip.GetEntityId()))
#else
            if (AssetPreview.IsLoadingAssetPreview(clip.GetInstanceID()))
#endif
                EditorApplication.delayCall += Repaint;

            return null;
        }

        private void InvalidateVideoPreviewCache(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            historyPreviewCache.Remove(assetPath);
        }

        private IEnumerator DownloadPreviewImage(string imageUrl)
        {
            urlPreviewLoading.Add(imageUrl);
            using (var uwr = UnityWebRequestTexture.GetTexture(imageUrl))
            {
                yield return uwr.SendWebRequest();
                if (UnityWebRequestCompat.IsSuccess(uwr))
                {
                    var tex = DownloadHandlerTexture.GetContent(uwr);
                    if (tex != null)
                    {
                        urlPreviewCache[imageUrl] = tex;
                        Repaint();
                    }
                }
                else
                {
                    urlPreviewFailed.Add(imageUrl);
                }
            }

            urlPreviewLoading.Remove(imageUrl);
        }

        private void ShowHistoryContextMenu(int index)
        {
            if (index < 0 || index >= generationHistory.Count)
                return;
            var item = generationHistory[index];
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在项目中显示")), false, () => ShowHistoryInProject(index));
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
                    generationHistory = TJGeneratorsHistoryManager.LoadHistoryForAsset(
                        GetCurrentVideoAssetGuid()
                    );
                    if (generationHistory.Count == 0)
                        selectedHistoryIndex = -1;
                    else if (selectedHistoryIndex >= generationHistory.Count)
                        selectedHistoryIndex = Mathf.Max(0, generationHistory.Count - 1);
                    Repaint();
                }
            );

            menu.ShowAsContext();
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

        private void DrawHistoryActions()
        {
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();

            bool hasSelection = selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count;
            bool isGenerating = hasSelection && generationHistory[selectedHistoryIndex].isGenerating;
            GUI.enabled = hasSelection && !isGenerating
                && targetVideoAsset != null && targetVideoAsset.IsValid();
            if (GUILayout.Button(TJGeneratorsL10n.L("应用到当前视频"), GUILayout.Height(25)))
                ApplyHistoryToVideo(selectedHistoryIndex);

            GUI.enabled = hasSelection && !isGenerating;
            if (GUILayout.Button(TJGeneratorsL10n.L("在项目中显示"), GUILayout.Height(25)))
                ShowHistoryInProject(selectedHistoryIndex);

            // 特效视频：显示"创建特效播放器"按钮
            if (hasSelection && !isGenerating
                && generationHistory[selectedHistoryIndex].modelVersion == "effect_video_wf"
                && !string.IsNullOrEmpty(generationHistory[selectedHistoryIndex].modelPath))
            {
                if (GUILayout.Button(TJGeneratorsL10n.L("创建特效播放器"), GUILayout.Height(25)))
                    GreenScreenVideoPostProcess.SetupEffectVideoInScene(generationHistory[selectedHistoryIndex].modelPath);
            }

            GUI.enabled = true;

            GUILayout.EndHorizontal();
            GUILayout.Space(CommonStyles.Space2);
        }

        private void ApplyHistoryToVideo(int index)
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
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("视频文件不存在，可能已被删除。"), LogTag);
                if (!string.IsNullOrEmpty(item.modelPath))
                    TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                generationHistory = TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentVideoAssetGuid());
                Repaint();
                return;
            }

            if (targetVideoAsset == null || !targetVideoAsset.IsValid())
            {
                Debug.LogWarning($"{LogTag} {TJGeneratorsL10n.L("请先创建或选择目标视频资产。")}");
                return;
            }

            string targetPath = targetVideoAsset.GetPath();
            if (!EditorUtility.DisplayDialog(
                TJGeneratorsL10n.L("确认替换"),
                string.Format(TJGeneratorsL10n.L("确定要将选中的历史应用到 {0} 吗？"), Path.GetFileName(targetPath)),
                TJGeneratorsL10n.L("确定"), TJGeneratorsL10n.L("取消")))
            {
                return;
            }

            try
            {
                string srcAbsolute = PathUtils.ToAbsoluteAssetPath(item.modelPath);
                string dstAbsolute = PathUtils.ToAbsoluteAssetPath(targetPath);
                string targetExt = Path.GetExtension(targetPath).ToLowerInvariant();
                string sourceExt = Path.GetExtension(item.modelPath).ToLowerInvariant();

                // If extensions differ, update target path
                if (!string.Equals(targetExt, sourceExt, StringComparison.OrdinalIgnoreCase))
                {
                    targetPath = Path.ChangeExtension(targetPath, sourceExt);
                    dstAbsolute = PathUtils.ToAbsoluteAssetPath(targetPath);

                    // Delete old file
                    string oldPath = targetVideoAsset.GetPath();
                    string oldAbsolute = PathUtils.ToAbsoluteAssetPath(oldPath);
                    if (File.Exists(oldAbsolute) && !string.Equals(oldAbsolute, dstAbsolute, StringComparison.OrdinalIgnoreCase))
                    {
                        AssetDatabase.DeleteAsset(oldPath);
                    }

                    targetVideoAsset = TJGeneratorsAssetReference.FromPath(targetPath);
                    titleContent = new GUIContent(string.Format(TJGeneratorsL10n.L("TJGenerators 视频 - {0}"), Path.GetFileNameWithoutExtension(targetPath)));
                    string newGuid = targetVideoAsset.guid;
                    if (!string.IsNullOrEmpty(newGuid))
                        s_videoOpenWindows[newGuid] = this;
                }

                string targetDir = Path.GetDirectoryName(dstAbsolute);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                File.Copy(srcAbsolute, dstAbsolute, true);
                AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);

                TJGeneratorsGenerationLabel.EnableLabel(targetVideoAsset);
                TJLog.Log($"[TJGeneratorsVideo] 已将历史视频应用到 {targetPath}");
            }
            catch (Exception e)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("应用失败: ") + e.Message, LogTag);
            }

            Repaint();
        }

        // ========== 生成 ==========

        private void StartGeneration()
        {
            if (_currentGenerator == null)
                return;

            if (string.IsNullOrWhiteSpace(textPrompt))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("请输入文本提示词。"), LogTag);
                return;
            }

            bool hasImage = referenceImagePaths.Count > 0;

            EnsureTargetVideo();
            if (!HasValidTargetVideoAsset())
            {
                ErrorDialogUtils.ShowErrorDialog(
                    TJGeneratorsL10n.L("错误"),
                    TJGeneratorsL10n.L("无法创建或绑定目标视频占位资产，请确认 Project 中选中的目录可写。"),
                    LogTag
                );
                return;
            }

            string promptToSend = textPrompt.Trim();
            if (_currentGenerator is DynamicGenerator dynamicGen)
            {
                // Clear leftover multimodal fields (e.g. videos) from a previous run on the reused generator.
                dynamicGen.ClearExtraRawJsonFields();

                dynamicGen.SetTextPrompt(promptToSend);
                dynamicGen.SetImagePaths(hasImage ? referenceImagePaths : null);

                // Determine mode based on inputs and dropdown (reference image is optional)
                string selectedMode = GetCurrentModeValue();
                bool hasReferenceVideo = !string.IsNullOrEmpty(referenceVideoPath);
                string mode = VideoModeResolver.Resolve(selectedMode, hasImage, hasReferenceVideo);

                // Validate image requirements for image-dependent modes (after soft fallbacks)
                if (mode == "first_frame" && referenceImagePaths.Count < 1)
                {
                    ErrorDialogUtils.ShowErrorDialog(
                        TJGeneratorsL10n.L("错误"),
                        TJGeneratorsL10n.L("首帧生视频需要上传 1 张图片。"),
                        LogTag);
                    return;
                }
                if (mode == "first_last_frame" && referenceImagePaths.Count < 2)
                {
                    ErrorDialogUtils.ShowErrorDialog(
                        TJGeneratorsL10n.L("错误"),
                        TJGeneratorsL10n.L("首尾帧生视频需要上传 2 张图片（首帧 + 尾帧）。"),
                        LogTag);
                    return;
                }

                if (mode == "multimodal")
                {
                    // Upload video to TOS
                    string absVideoPath = PathUtils.ToAbsoluteAssetPath(referenceVideoPath);
                    if (!File.Exists(absVideoPath))
                    {
                        ErrorDialogUtils.ShowErrorDialog(
                            TJGeneratorsL10n.L("错误"),
                            TJGeneratorsL10n.L("参考视频文件不存在: ") + referenceVideoPath,
                            LogTag);
                        return;
                    }

                    string videoTosUrl = UploadVideoToTOS(absVideoPath);
                    if (string.IsNullOrEmpty(videoTosUrl))
                    {
                        ErrorDialogUtils.ShowErrorDialog(
                            TJGeneratorsL10n.L("错误"),
                            TJGeneratorsL10n.L("参考视频上传失败"),
                            LogTag);
                        return;
                    }

                    dynamicGen.SetExtraRawJsonField("videos", "[" + JsonConvert.SerializeObject(videoTosUrl) + "]");
                    dynamicGen.SetParameter("mode", "multimodal");
                }
                else
                {
                    // first_frame, first_last_frame, reference_image, text_to_video
                    // Images are already set above via SetImagePaths
                    dynamicGen.SetParameter("mode", mode);
                }
            }

            isGenerating = true;
            generationStatus = TJGeneratorsL10n.L("准备中...");
            generationProgress = 0f;

            // 已提交生成：清空输入框，便于连续发起下一条任务（提示词已写入 generator / 历史占位）
            textPrompt = string.Empty;
            GUI.FocusControl(null);

            string assetGuid = targetVideoAsset?.guid ?? "";
            EditorCoroutineUtility.StartCoroutineOwnerless(
                _pipeline.StartGeneration(_currentGenerator, assetGuid)
            );
            Repaint();
        }

        // ========== IGenerationPipelineHost ==========

        public TJGeneratorsAssetReference GetTargetAsset() => targetVideoAsset;

        public void StartGeneration(ModelGeneratorBase generator)
        {
            if (generator == _currentGenerator)
                StartGeneration();
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

        public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Video) return null;

            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                AssetDatabase.CreateFolder("Assets/TJGenerators", "History");

            string uniqueName = "Video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4";
            return AssetDatabase.GenerateUniqueAssetPath(
                "Assets/TJGenerators/History/" + uniqueName
            );
        }

        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Video) return;

            TJLog.Log($"[TJGeneratorsVideo] OnAssetSaved: {savePath}");
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));
            InvalidateVideoPreviewCache(savePath);

            // Copy to target asset if bound
            if (targetVideoAsset != null && targetVideoAsset.IsValid())
            {
                string targetPath = targetVideoAsset.GetPath();
                try
                {
                    string sourceExt = Path.GetExtension(savePath).ToLowerInvariant();
                    string targetExt = Path.GetExtension(targetPath).ToLowerInvariant();

                    // Update target path extension if needed
                    if (!string.Equals(sourceExt, targetExt, StringComparison.OrdinalIgnoreCase))
                    {
                        string newPath = Path.ChangeExtension(targetPath, sourceExt);
                        string oldAbsolute = PathUtils.ToAbsoluteAssetPath(targetPath);
                        if (File.Exists(oldAbsolute))
                            AssetDatabase.DeleteAsset(targetPath);

                        targetPath = newPath;
                        targetVideoAsset = TJGeneratorsAssetReference.FromPath(targetPath);
                        titleContent = new GUIContent(string.Format(TJGeneratorsL10n.L("TJGenerators 视频 - {0}"), Path.GetFileNameWithoutExtension(targetPath)));
                        string newGuid = targetVideoAsset.guid;
                        if (!string.IsNullOrEmpty(newGuid))
                            s_videoOpenWindows[newGuid] = this;
                    }

                    string srcAbsolute = PathUtils.ToAbsoluteAssetPath(savePath);
                    string dstAbsolute = PathUtils.ToAbsoluteAssetPath(targetPath);
                    string targetDir = Path.GetDirectoryName(dstAbsolute);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        Directory.CreateDirectory(targetDir);
                    File.Copy(srcAbsolute, dstAbsolute, true);
                    AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                    InvalidateVideoPreviewCache(targetPath);
                    TJGeneratorsGenerationLabel.EnableLabel(targetVideoAsset);
                    PingTargetVideoInProject(targetPath);
                    TJLog.Log($"[TJGeneratorsVideo] 视频已复制到目标路径: {targetPath}");
                }
                catch (Exception e)
                {
                    TJLog.LogWarning($"[TJGeneratorsVideo] 复制到目标视频失败: {e.Message}");
                }
            }
            else if (!string.IsNullOrEmpty(savePath))
            {
                PingTargetVideoInProject(savePath);
            }

            // 特效视频工作流：自动创建 ChromaKey 材质
            TryCreateChromaKeyMaterial(savePath, generator);

            generationStatus = TJGeneratorsL10n.L("完成");
            generationProgress = 1f;
            isGenerating = false;
            RefreshHistory();
            Repaint();
        }

        /// <summary>
        /// 特效视频工作流（effect_video_wf）：视频下载后自动创建 ChromaKey 材质并搭建场景播放器。
        /// 非 effect_video_wf 生成器不受影响。
        /// </summary>
        private void TryCreateChromaKeyMaterial(string videoPath, ModelGeneratorBase generator)
        {
            if (string.IsNullOrEmpty(videoPath))
                return;

            var currentGen = generator ?? _currentGenerator;
            if (currentGen == null || currentGen.GeneratorId != "effect_video_wf")
                return;

            try
            {
                var result = GreenScreenVideoPostProcess.EnsureChromaKeyMaterial(videoPath);
                if (result.Success)
                {
                    TJLog.Log($"[TJGeneratorsVideo] ChromaKey material created: {result.MaterialPath}");
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(result.MaterialPath);
                    if (mat != null)
                        EditorGUIUtility.PingObject(mat);

                    GreenScreenVideoPostProcess.SetupEffectVideoInScene(videoPath, result.MaterialPath);
                }
                else
                {
                    TJLog.LogWarning($"[TJGeneratorsVideo] ChromaKey material creation failed: {result.Error}");
                }
            }
            catch (Exception e)
            {
                TJLog.LogWarning($"[TJGeneratorsVideo] ChromaKey post-process error: {e.Message}");
            }
        }

        // ========== 辅助方法 ==========

        private string GetCurrentVideoAssetGuid() => targetVideoAsset?.guid ?? "";

        private bool HasValidTargetVideoAsset() =>
            targetVideoAsset != null && targetVideoAsset.IsValid();

        /// <summary>
        /// 无绑定目标时创建占位视频（ShowWindow 回退或生成前兜底）。
        /// </summary>
        private void EnsureTargetVideo()
        {
            if (HasValidTargetVideoAsset())
            {
                string path = targetVideoAsset.GetPath();
                if (TJGeneratorsVideoUtils.NeedsPlaceholderRepair(path))
                    TJGeneratorsVideoUtils.RepairPlaceholderVideo(path);
                return;
            }

            string folder = PathUtils.GetProjectBrowserInsertionFolderAssetPath();
            string videoPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/New Video.mp4");
            videoPath = CreateBlankVideo(videoPath);
            if (string.IsNullOrEmpty(videoPath))
            {
                TJLog.LogError("[TJGeneratorsVideo] 无法创建视频占位资产");
                return;
            }

            targetVideoAsset = TJGeneratorsAssetReference.FromPath(videoPath);
            titleContent = new GUIContent(
                string.Format(TJGeneratorsL10n.L("TJGenerators 视频 - {0}"), Path.GetFileNameWithoutExtension(videoPath))
            );

            if (!string.IsNullOrEmpty(targetVideoAsset.guid))
                s_videoOpenWindows[targetVideoAsset.guid] = this;

            TJGeneratorsGenerationLabel.EnableLabel(targetVideoAsset);
            Repaint();
        }

        private string UploadVideoToTOS(string absVideoPath)
        {
            string uploadUrl = ConfigManager.GetApiBaseUrl() + "upload/video";
            var multipart = new MultipartRequestData
            {
                FilePath = absVideoPath,
                FileName = Path.GetFileName(absVideoPath),
                FileFieldName = "video"
            };
            var httpResult = GenerationBackendSyncSubmit.PostMultipart(
                uploadUrl, multipart, GenerationRequestOrigin.Agent, "", 120f);
            if (!httpResult.IsSuccess)
            {
                TJLog.LogError("[TJGeneratorsVideo] Video upload failed: " + httpResult.Error);
                return null;
            }
            try
            {
                var resp = JObject.Parse(httpResult.Body);
                return resp["url"]?.ToString();
            }
            catch (Exception e)
            {
                TJLog.LogError("[TJGeneratorsVideo] Parse upload response failed: " + e.Message);
                return null;
            }
        }

        private void PingTargetVideoInProject(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            EditorApplication.delayCall += () =>
            {
                if (this == null)
                    return;

                var asset =
                    AssetDatabase.LoadAssetAtPath<UnityEngine.Video.VideoClip>(assetPath)
                    ?? AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset == null)
                    return;

                EditorUtility.FocusProjectWindow();
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            };
        }

        /// <summary>
        /// 创建黑场 mp4 占位文件，生成完成后会被实际视频覆盖。
        /// </summary>
        public static string CreateBlankVideo(string path) =>
            TJGeneratorsVideoUtils.CreateBlankVideoClip(path);
    }
}
#endif
