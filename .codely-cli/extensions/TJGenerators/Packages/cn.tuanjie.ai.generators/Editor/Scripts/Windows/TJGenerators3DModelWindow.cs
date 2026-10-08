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
using TJGenerators.Utils;
using TJGenerators.UI;
using Unity.UniAsset.Manager.Editor.InternalBridge;
using Unity.EditorCoroutines.Editor;

namespace TJGenerators
{
    /// <summary>
    /// TJGenerators 3D模型生成窗口 - 作为容器协调各种生成器
    /// </summary>
    public class TJGenerators3DModelWindow : GenerationWindowBase, IGenerationPipelineHost, IGenerationTriggerHost
    {
        private string _userEmail = "user123@unity.cn";
        // ========== 基类抽象属性实现 ==========
        protected override ConfigType WindowConfigType => ConfigType.Generator;
        protected override string LogTag => "[TJGenerators3DModel]";

        // ========== 资产绑定 ==========
        [SerializeField]
        private TJGeneratorsAssetReference targetAsset;

        /// <summary>3D 窗口内各生成器共享的文本提示词，切换模型时不各自保留不同内容。</summary>
        private string _sharedTextPrompt = string.Empty;

        internal string SharedTextPrompt
        {
            get => _sharedTextPrompt;
            set => _sharedTextPrompt = value ?? string.Empty;
        }

        private static Dictionary<string, TJGenerators3DModelWindow> openWindows = new Dictionary<string, TJGenerators3DModelWindow>();

        // ========== 场景实例缓存 ==========
        private List<GameObject> _cachedSceneInstances = new List<GameObject>();
        private double _lastSceneInstancesRefreshTime = -1;
        private const double SceneInstancesCacheTtl = 1.0;
        // ========== 3D预览 =========
        private Model3DPreview _modelPreview;

        // 本地预览图缓存目录
        private static string _previewCacheDirectory;

        private static string PreviewCacheDirectory
        {
            get
            {
                if (_previewCacheDirectory == null)
                {
                    _previewCacheDirectory = Path.Combine(Application.dataPath, "../Library/AI.TJGenerators/PreviewCache");
                }
                return _previewCacheDirectory;
            }
        }

        // ========== 公开方法 ==========
        
        public static void ShowWindow()
        {
            var rect = GetDefaultMainWindowRect();
            var window = GetWindowWithRect<TJGenerators3DModelWindow>(
                rect,
                utility: false,
                title: TJGeneratorsL10n.L("TJGenerators 3D模型"),
                focus: true
            );
            window.titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 3D模型"));
            FinalizeMainWindowShow(window, rect);
        }
        
        public static void OpenForAsset(string assetPath)
        {
            GenerationWindowBase.OpenForAsset(
                assetPath,
                openWindows,
                "[TJGenerators3DModel]",
                TJGeneratorsL10n.L("TJGenerators 3D模型 - {0}"),
                () =>
                {
                    var window = CreateInstance<TJGenerators3DModelWindow>();
                    return window;
                },
                (w, r) => w.targetAsset = r,
                ShowWindow);
        }

        public TJGeneratorsAssetReference GetTargetAsset() => targetAsset;
        
        // ========== 生命周期 ==========
        
        protected override void OnBootstrapWindowContent()
        {
            // Re-register after domain reload so OpenForAsset finds this window instead of creating a duplicate.
            if (targetAsset != null && !string.IsNullOrEmpty(targetAsset.guid))
                openWindows[targetAsset.guid] = this;

            EnsureModelPreview();
            InitializeGenerators();
            OnRefreshWindowContent();
        }

        protected override void OnRefreshWindowContent()
        {
            generationHistory = TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentAssetGuid());
            CheckAndRecoverInterruptedTasks();
            Repaint();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            // Bootstrap is skipped when already ready; still recreate preview after OnDisable disposed it.
            EnsureModelPreview();
            wantsMouseMove = true;

            EditorCoroutineUtility.StartCoroutineOwnerless(
                UserInfoHelper.GetUserInfoDetailCoroutine(
                    ConfigManager.GetUserInfoUrl(),
                    OnUserDetailInfoLoaded));
        }
        
        protected override void OnDisable()
        {
            base.OnDisable();
            wantsMouseMove = false;
            if (targetAsset != null && !string.IsNullOrEmpty(targetAsset.guid))
            {
                openWindows.Remove(targetAsset.guid);
            }

            // 取消订阅配置更新事件
            ConfigManager.OnConfigUpdated -= OnConfigUpdatedByType;

            _modelPreview?.Dispose();
            _modelPreview = null;
            ClearPreviewCaches();
        }

        private void EnsureModelPreview()
        {
            if (_modelPreview == null)
                _modelPreview = new Model3DPreview();
        }
        
        private void InitializeGenerators()
        {
            InitializeGeneratorsFromConfig(ConfigType.Generator);
        }

        protected override void OnAfterGeneratorInitialize(ConfigType configType)
        {
            // 订阅配置更新事件
            ConfigManager.OnConfigUpdated -= OnConfigUpdatedByType;
            ConfigManager.OnConfigUpdated += OnConfigUpdatedByType;

            // 触发配置刷新
            ConfigManager.RefreshConfigAsync(ConfigType.Generator);
        }

        private void OnConfigUpdatedByType(ConfigType type, RemoteConfig config)
        {
            if (type != ConfigType.Generator) return;
            OnConfigUpdated(config);
        }

        private void OnConfigUpdated(RemoteConfig config)
        {
            // 配置更新时用 GetGenerators 重新加载生成器列表
            string currentId = _currentGenerator?.GeneratorId;

            _generators.Clear();
            var generators = ConfigManager.GetGenerators(ConfigType.Generator);
            if (generators != null)
            {
                foreach (var genConfig in generators)
                    _generators.Add(new DynamicGenerator(genConfig));
            }

            // 如果配置为空，显示提示
            if (_generators.Count == 0)
            {
                TJLog.LogWarning("配置更新后没有可用的生成器");
            }

            // 配置变化后重建索引（供默认选择/历史面板查找使用）
            RebuildGeneratorIndexes(ConfigType.Generator);

            // 尝试恢复之前选择的生成器
            bool restored = false;
            if (!string.IsNullOrEmpty(currentId))
            {
                if (TryGetGeneratorIndex(currentId, out var index) && index >= 0)
                {
                    SetCurrentGeneratorByIndex(index);
                    restored = true;
                }
            }

            if (!restored)
            {
                SelectDefaultGeneratorFromPreference(ConfigType.Generator);
            }

            if (_currentGenerator is DynamicGenerator dg)
                dg.SetTextPrompt(_sharedTextPrompt);

            Repaint();
        }

        // ========== 主UI绘制 ==========
        
        public void OnGUI()
        {
            if (Event.current.type == EventType.MouseMove)
                Repaint();

            // 固定左右分栏：左侧固定 500，右侧自适应；避免两侧互相挤压。
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

                    var sceneInstances = GetCachedSceneInstances();
                    string sceneButtonLabel = sceneInstances.Count > 0
                        ? string.Format(TJGeneratorsL10n.L("场景({0})"), sceneInstances.Count)
                        : TJGeneratorsL10n.L("选择对象");
                    UIComponents.DrawSelectionHeaderComposite(
                        TJGeneratorsL10n.L("目标预制体"),
                        DrawTargetHeaderContentRect,
                        sceneButtonLabel,
                        CommonStyles.TargetIconTexture,
                        SelectOrShowSceneInstance
                    );
                    GUILayout.Space(CommonStyles.Space2);
                    UIComponents.DrawModelSelector(
                        _currentGenerator?.DisplayName ?? TJGeneratorsL10n.L("未选择"),
                        currentSelectedModel,
                        OnModelSelected,
                        ConfigType.Generator);
                    GUILayout.Space(CommonStyles.Space3);

                    if (_currentGenerator != null)
                        _currentGenerator.DrawParametersUI(this);

                    GUILayout.EndVertical();
                    GUILayout.Space(CommonStyles.LeftContentPadding);
                    GUILayout.EndHorizontal();
                    GUILayout.Space(CommonStyles.LeftContentPadding);
                });

            GUILayout.Space(splitLayout.GapWidth);

            // 历史记录面板
            DrawHistoryPanel(currentHistoryPanelWidth);
            GUILayout.EndHorizontal();

            DrawLeftPanelBottomDock(splitLayout.LeftPanelWidth, DrawGeneratorActionButton);
        }

        protected override string GetStatusBarEmail()
        {
            return string.IsNullOrEmpty(_userEmail) ? "user123@unity.cn" : _userEmail;
        }

        private void OnUserDetailInfoLoaded(UserInfoResponse userInfo)
        {
            if (userInfo == null)
                return;

            _userEmail = string.IsNullOrEmpty(userInfo.email) ? "--" : userInfo.email;
            Repaint();
        }

        private void DrawTargetHeaderContentRect(Rect rect)
        {
            if (targetAsset != null && targetAsset.IsValid())
            {
                string prefabPath = targetAsset.GetPath();
                string prefabName = Path.GetFileNameWithoutExtension(prefabPath);
                if (GUI.Button(rect, prefabName, CommonStyles.TargetPrefabNameStyle))
                    SelectTargetPrefabAsset();
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

                if (Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    if (prefab != null)
                        ShowPrefabContextMenu(prefab);
                    Event.current.Use();
                }
            }
            else
            {
                GUI.Label(rect, TJGeneratorsL10n.L("未绑定（生成时自动创建）"), CommonStyles.ContentStyle);
            }
        }

        private void SelectTargetPrefabAsset()
        {
            if (targetAsset == null || !targetAsset.IsValid())
                return;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(targetAsset.GetPath());
            if (prefab != null)
            {
                EditorGUIUtility.PingObject(prefab);
                Selection.activeObject = prefab;
            }
        }

        private void SelectOrShowSceneInstance()
        {
            var instances = GetCachedSceneInstances();
            if (instances.Count == 0)
            {
                EditorUtility.DisplayDialog(TJGeneratorsL10n.L("提示"), TJGeneratorsL10n.L("场景中没有该预制体的实例。"), TJGeneratorsL10n.L("确定"));
            }
            else if (instances.Count == 1)
            {
                EditorGUIUtility.PingObject(instances[0]);
                Selection.activeGameObject = instances[0];
                SceneView.lastActiveSceneView?.FrameSelected();
            }
            else
            {
                ShowSceneInstancesMenu(instances);
            }
        }

        // ========== 生成器选择器 ==========

        private void OnModelSelected(AIModelInfo model)
        {
            if (_currentGenerator is DynamicGenerator currentDg)
                _sharedTextPrompt = currentDg.GetPrompt() ?? string.Empty;
            OnModelSelectedBase(model);
        }

        protected override void ResetInputStateAfterModelChange()
        {
            ResetTextPromptIfHidden(GetCurrentGeneratorConfig(), ref _sharedTextPrompt);
            if (_currentGenerator is DynamicGenerator dg)
                dg.SetTextPrompt(_sharedTextPrompt);
        }

        // ========== 生成任务启动 ==========
        
        public void StartGeneration(ModelGeneratorBase generator)
        {
            EnsureTargetAsset();
            EditorCoroutineUtility.StartCoroutineOwnerless(_pipeline.StartGeneration(generator, GetCurrentAssetGuid()));
        }
        
        // ========== 历史记录面板 ==========
        
        private void DrawHistoryPanel(float panelWidth)
        {
            float historyPanelHeight = isVerticalLayout ? position.height * 0.45f : position.height;
            
            UIComponents.BeginHistoryPanel(panelWidth, historyPanelHeight, isVerticalLayout);
            
            // 3D预览区域
            bool shouldShowPreview = false;
            if (selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count)
            {
                var selectedItem = generationHistory[selectedHistoryIndex];
                if (!selectedItem.isGenerating && !string.IsNullOrEmpty(selectedItem.modelPath))
                {
                    shouldShowPreview = true;
                    Vector3 previewRotation = new Vector3(0f, 0f, 0f);
                    if (!string.IsNullOrEmpty(selectedItem.modelVersion) && _generators != null)
                    {
                        if (TryGetGeneratorByModelVersion(selectedItem.modelVersion, out var gen) && gen != null)
                        {
                            var settings = gen.GetPipelineSettings();
                            if (settings != null)
                                previewRotation = settings.GetModelRotation();
                        }
                    }
                    EnsureModelPreview();
                    _modelPreview.Draw(selectedItem.modelPath, CommonStyles.HistoryPanelInnerWidth(currentHistoryPanelWidth), previewRotation, Repaint);
                    GUILayout.Space(12);
                }
            }
            
            // 历史记录滚动区域（预留：标题栏~30 + 底部按钮~45 + 边距~25 = 100）
            float previewHeight = shouldShowPreview ? 200f : 0f;
            float bottomMargin = shouldShowPreview ? 115f : 100f;
            float scrollHeight = historyPanelHeight - bottomMargin - previewHeight;
            historyScrollPosition = GUILayout.BeginScrollView(
                historyScrollPosition,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUILayout.Height(scrollHeight));
            
            if (generationHistory.Count == 0)
            {
                UIComponents.DrawHistoryEmptyState();
            }
            else
            {
                DrawHistoryGrid();
            }
            
            GUILayout.EndScrollView();
            
            // 底部操作按钮
            DrawHistoryActions();
            UIComponents.EndHistoryPanel();
        }
        
        private void DrawHistoryGrid()
        {
            float historyContent = CommonStyles.HistoryScrollViewLayoutWidth(currentHistoryPanelWidth);
            float scale = GetHistoryTileScale();
            float margin = Mathf.Max(4f, Mathf.Round(HistoryTileBaseMargin * scale));
            int itemsPerRow = ComputeHistoryItemsPerRow(historyContent, currentHistoryTileSize);
            float tileWidth = Mathf.Max(1f, (historyContent - itemsPerRow * 2f * margin) / itemsPerRow);
            // 根据缩略图大小动态调整标签区域高度
            float labelHeight = currentHistoryTileSize >= 100f ? 40f : 32f;
            float tileHeight = tileWidth + labelHeight;

            for (int i = 0; i < generationHistory.Count; i += itemsPerRow)
            {
                GUILayout.BeginHorizontal();
                
                for (int j = 0; j < itemsPerRow && (i + j) < generationHistory.Count; j++)
                {
                    int index = i + j;
                    var item = generationHistory[index];
                    bool isSelected = (selectedHistoryIndex == index);
                    
                    GUILayout.BeginVertical(GetScaledHistoryTileStyle(isSelected),
                        GUILayout.Width(tileWidth), GUILayout.Height(tileHeight));
                    
                    float previewSize = GetScaledHistoryPreviewSize(tileWidth);
                    Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize);
                    DrawModelPreview(previewRect, item);
                    
                    if (!item.isGenerating && Event.current.type == EventType.MouseDown && previewRect.Contains(Event.current.mousePosition))
                    {
                        selectedHistoryIndex = index;
                        Event.current.Use();
                        Repaint();
                    }
                    
                    if (!item.isGenerating && Event.current.type == EventType.ContextClick && previewRect.Contains(Event.current.mousePosition))
                    {
                        ShowHistoryContextMenu(index);
                        Event.current.Use();
                    }
                    
                    string historyText = GetHistoryTilePromptText(item);
                    historyText = TruncateToSingleLine(historyText, CommonStyles.HistoryLabelStyle, tileWidth - 10f);
                    GUILayout.Label(historyText, CommonStyles.HistoryLabelStyle);
                    
                    // 显示所用的模型名称
                    string modelLabel = GetModelDisplayLabel(item.modelVersion);
                    GUILayout.Label(modelLabel, CommonStyles.SmallGreyCenterLabelStyle);
                    
                    GUILayout.EndVertical();
                }
                
                GUILayout.EndHorizontal();
            }
        }

        private static string GetHistoryTilePromptText(TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null) return string.Empty;

            if (item.isGenerating)
            {
                if (item.progress >= 100)
                    return TJGeneratorsL10n.L("转换中...");
                return item.progress > 0 ? string.Format(TJGeneratorsL10n.L("生成中 {0}%"), item.progress) : TJGeneratorsL10n.L("生成中...");
            }

            if (!string.IsNullOrEmpty(item.prompt))
                return item.prompt;

            if (!string.IsNullOrEmpty(item.modelPath))
                return Path.GetFileNameWithoutExtension(item.modelPath);

            return string.Empty;
        }

        private static string TruncateToSingleLine(string text, GUIStyle style, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (style == null) return text;

            // IMGUI 里避免出现换行符导致的二次排版。
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrEmpty(text)) return string.Empty;

            float safeWidth = Mathf.Max(10f, maxWidth);
            safeWidth -= (style != null ? style.padding.left + style.padding.right : 0f);
            safeWidth = Mathf.Max(10f, safeWidth);

            var singleLineStyle = new GUIStyle(style)
            {
                wordWrap = false
            };

            if (singleLineStyle.CalcSize(new GUIContent(text)).x <= safeWidth)
                return text;

            const string ellipsis = "...";

            // 如果原文本已经是省略形式，去掉尾部的 "..."，避免叠加成 "......"
            string working = text;
            if (working.EndsWith(ellipsis, StringComparison.Ordinal) && working.Length > ellipsis.Length)
                working = working.Substring(0, working.Length - ellipsis.Length);

            int lo = 0;
            int hi = working.Length;

            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                string candidate = working.Substring(0, mid) + ellipsis;
                if (singleLineStyle.CalcSize(new GUIContent(candidate)).x <= safeWidth)
                    lo = mid;
                else
                    hi = mid - 1;
            }

            if (lo <= 0)
                return ellipsis;

            return working.Substring(0, lo) + ellipsis;
        }
        
        /// <summary>
        /// 获取模型版本的友好显示名称
        /// </summary>
        private string GetModelDisplayLabel(string modelVersion)
        {
            if (string.IsNullOrEmpty(modelVersion))
                return TJGeneratorsL10n.L("未知模型");

            // 热路径：使用预建字典索引，避免每帧调用 ConfigManager / 遍历 parameters
            if (_baseModelVersionDisplayLabelIndex.TryGetValue(modelVersion, out var displayName) &&
                !string.IsNullOrEmpty(displayName))
            {
                return TJGeneratorsL10n.L(displayName);
            }

            // 回退到旧的硬编码逻辑
            // Tripo 模型版本
            if (modelVersion.StartsWith("v") && modelVersion.Contains("-"))
            {
                var parts = modelVersion.Split('-');
                return $"Tripo {parts[0]}";
            }

            // Rodin 模型
            if (modelVersion.StartsWith("Rodin"))
            {
                return modelVersion;
            }

            return modelVersion;
        }
        
        private void DrawModelPreview(Rect rect, TJGeneratorsGenerationHistoryItem item)
        {
            if (item.isGenerating)
            {
                var iconRect = new Rect(rect.x + rect.width / 4, rect.y + rect.height / 4, rect.width / 2, rect.height / 2);
                
                var spinIcon = EditorGUIUtility.IconContent("Loading");
                if (spinIcon != null && spinIcon.image != null)
                {
                    float angle = (float)(EditorApplication.timeSinceStartup * 180) % 360;
                    Matrix4x4 matrixBackup = GUI.matrix;
                    GUIUtility.RotateAroundPivot(angle, iconRect.center);
                    GUI.DrawTexture(iconRect, spinIcon.image, ScaleMode.ScaleToFit);
                    GUI.matrix = matrixBackup;
                }
                else
                {
                    GUI.Label(rect, TJGeneratorsL10n.L("生成中..."), CommonStyles.SmallGreyCenterLabelStyle);
                }
                
                Repaint();
                return;
            }

            // 对于图生模型和多图生模型，优先使用上传的图片作为预览
            if (!item.isTextToModel && !string.IsNullOrEmpty(item.imagePath))
            {
                // 检查内存缓存
                if (historyPreviewCache.TryGetValue(item.imagePath, out var uploadedPreview) && uploadedPreview != null)
                {
                    GUI.DrawTexture(rect, uploadedPreview, ScaleMode.ScaleToFit);
                    return;
                }

                // 从本地文件加载
                if (File.Exists(item.imagePath))
                {
                    EnqueuePreviewLoad(item.imagePath, item.imagePath, false);
                }
            }

            // 文本生成模型使用URL预览图（如果有的话）
            if (item.isTextToModel && !string.IsNullOrEmpty(item.previewImageUrl))
            {
                // 先检查内存缓存
                if (urlPreviewCache.TryGetValue(item.previewImageUrl, out var urlPreview) && urlPreview != null)
                {
                    GUI.DrawTexture(rect, urlPreview, ScaleMode.ScaleToFit);
                    return;
                }
                
                // 再检查本地文件缓存
                var localPreview = TryGetOrQueueUrlPreviewFromLocalCache(item.previewImageUrl);
                if (localPreview != null)
                {
                    GUI.DrawTexture(rect, localPreview, ScaleMode.ScaleToFit);
                    return;
                }
                
                // 都没有则下载（仅当未在下载中且未失败过）
                if (!urlPreviewLoading.Contains(item.previewImageUrl) && !urlPreviewFailed.Contains(item.previewImageUrl))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(DownloadPreviewImage(item.previewImageUrl));
                }
            }
            
            // 回退到本地资产预览
            if (!string.IsNullOrEmpty(item.modelPath) &&
                (!historyPreviewCache.TryGetValue(item.modelPath, out var preview) || preview == null))
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(item.modelPath);
                if (asset != null)
                {
                    preview = AssetPreview.GetAssetPreview(asset);
                    if (preview != null)
                    {
                        historyPreviewCache[item.modelPath] = preview;
                    }
                }
            }
            
            if (!string.IsNullOrEmpty(item.modelPath) && historyPreviewCache.TryGetValue(item.modelPath, out var cachedPreview) && cachedPreview != null)
            {
                GUI.DrawTexture(rect, cachedPreview, ScaleMode.ScaleToFit);
            }
            else
            {
                EditorGUI.DrawRect(rect, new Color(0.0f, 0.2f, 0.2f, 0f));
                var iconRect = new Rect(rect.x + rect.width / 4, rect.y + rect.height / 4, rect.width / 2, rect.height / 2);
                GUI.Label(iconRect, EditorGUIUtility.IconContent("d_Prefab Icon"));
            }
        }

        private Texture2D TryGetOrQueueUrlPreviewFromLocalCache(string imageUrl)
        {
            string localPath = GetLocalCachePath(imageUrl);
            if (!File.Exists(localPath))
                return null;

            EnqueuePreviewLoad(imageUrl, localPath, true);
            if (urlPreviewCache.TryGetValue(imageUrl, out var cached) && cached != null)
                return cached;
            return null;
        }
        
        /// <summary>
        /// 获取URL对应的本地缓存文件路径
        /// </summary>
        private string GetLocalCachePath(string imageUrl)
        {
            // 使用URL的hash作为文件名，保留原始扩展名
            string hash = imageUrl.GetHashCode().ToString("X8");
            string extension = Path.GetExtension(new Uri(imageUrl).LocalPath);
            if (string.IsNullOrEmpty(extension))
                extension = ".png";
            return Path.Combine(PreviewCacheDirectory, $"{hash}{extension}");
        }
        
        /// <summary>
        /// 保存预览图到本地缓存
        /// </summary>
        private void SavePreviewToLocalCache(string imageUrl, byte[] imageData)
        {
            try
            {
                if (!Directory.Exists(PreviewCacheDirectory))
                {
                    Directory.CreateDirectory(PreviewCacheDirectory);
                }
                
                string localPath = GetLocalCachePath(imageUrl);
                File.WriteAllBytes(localPath, imageData);
            }
            catch (Exception e)
            {
                TJLog.LogWarning($"保存本地缓存失败: {e.Message}");
            }
        }
        
        /// <summary>
        /// 下载预览图URL并缓存到本地
        /// </summary>
        private IEnumerator DownloadPreviewImage(string imageUrl)
        {
            if (string.IsNullOrEmpty(imageUrl) || urlPreviewLoading.Contains(imageUrl))
                yield break;
            
            urlPreviewLoading.Add(imageUrl);
            TJLog.Log($"开始下载预览图: {imageUrl}");
            
            // 使用普通请求下载原始字节数据
            using (UnityWebRequest uwr = UnityWebRequest.Get(imageUrl))
            {
                uwr.downloadHandler = new DownloadHandlerBuffer();
                
                yield return uwr.SendWebRequest();
                
                // 等待请求完成
                float timeout = 30f;
                float timeElapsed = 0f;
                float interval = 0.5f;
                
                while (UnityWebRequestCompat.IsInProgress(uwr) && timeElapsed < timeout)
                {
                    double startWait = EditorApplication.timeSinceStartup;
                    while (EditorApplication.timeSinceStartup - startWait < interval)
                    {
                        yield return null;
                    }
                    timeElapsed += interval;
                }
                
                if (UnityWebRequestCompat.IsSuccess(uwr))
                {
                    byte[] imageData = uwr.downloadHandler.data;
                    if (imageData != null && imageData.Length > 0)
                    {
                        // 使用LoadImage解码图片
                        var texture = new Texture2D(2, 2);
                        if (texture.LoadImage(imageData))
                        {
                            // 保存到本地缓存
                            SavePreviewToLocalCache(imageUrl, imageData);
                            
                            urlPreviewCache[imageUrl] = texture;
                            TJLog.Log($"预览图下载成功并缓存: {imageUrl}");
                            Repaint();
                        }
                        else
                        {
                            TJLog.LogWarning($"预览图解码失败: {imageUrl}");
                            urlPreviewFailed.Add(imageUrl);
                            UnityEngine.Object.DestroyImmediate(texture);
                        }
                    }
                    else
                    {
                        TJLog.LogWarning($"预览图数据为空: {imageUrl}");
                        urlPreviewFailed.Add(imageUrl);
                    }
                }
                else
                {
                    TJLog.LogWarning($"预览图下载失败: {imageUrl}, 错误: {uwr.error}");
                    urlPreviewFailed.Add(imageUrl);
                }
            }
            
            urlPreviewLoading.Remove(imageUrl);
        }
        
        private void DrawHistoryActions()
        {
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            
            bool hasSelection = selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count;
            bool isGenerating = hasSelection && generationHistory[selectedHistoryIndex].isGenerating;
            
            GUI.enabled = hasSelection && !isGenerating;
            
            if (GUILayout.Button(TJGeneratorsL10n.L("应用到当前预制体"), GUILayout.Height(25)))
            {
                ApplyHistoryToPrefab(selectedHistoryIndex);
            }

            if (GUILayout.Button(TJGeneratorsL10n.L("在项目中显示模型"), GUILayout.Height(25)))
            {
                ShowHistoryInProject(selectedHistoryIndex);
            }
            
            GUI.enabled = true;
            
            // 缩略图大小调节滑动条（推到最右边，垂直居中对齐）
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical();
            GUILayout.Space(6); // 上边距，使滑条与25px高的按钮垂直居中
            currentHistoryTileSize = GUILayout.HorizontalSlider(currentHistoryTileSize, MinHistoryTileSize, MaxHistoryTileSize, GUILayout.Width(60f));
            GUILayout.EndVertical();
            
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
        }
        
        private void ShowHistoryContextMenu(int index)
        {
            var item = generationHistory[index];
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("应用到当前预制体")), false, () => ApplyHistoryToPrefab(index));
            
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在项目中显示模型")), false, () => ShowHistoryInProject(index));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在资源管理器中显示")), false, () => EditorUtility.RevealInFinder(item.modelPath));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("从历史记录中移除")), false, () =>
            {
                TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                generationHistory = TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentAssetGuid());
                if (selectedHistoryIndex >= generationHistory.Count)
                {
                    selectedHistoryIndex = generationHistory.Count - 1;
                }
                Repaint();
            });
            
            menu.ShowAsContext();
        }
        
        private void ApplyHistoryToPrefab(int index)
        {
            if (index < 0 || index >= generationHistory.Count) return;
            
            var item = generationHistory[index];
            
            if (!File.Exists(item.modelPath))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("模型文件不存在，可能已被删除。"), LogTag);
                TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                generationHistory = TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentAssetGuid());
                Repaint();
                return;
            }
            
            if (targetAsset == null || !targetAsset.IsValid())
            {
                EditorUtility.DisplayDialog(TJGeneratorsL10n.L("提示"), TJGeneratorsL10n.L("请先选择一个目标预制体。"), TJGeneratorsL10n.L("确定"));
                return;
            }
            
            if (!EditorUtility.DisplayDialog(TJGeneratorsL10n.L("确认替换"),
                string.Format(TJGeneratorsL10n.L("确定要将历史模型应用到 {0} 吗？"), Path.GetFileName(targetAsset.GetPath())), TJGeneratorsL10n.L("确定"), TJGeneratorsL10n.L("取消")))
            {
                return;
            }
            
            // 根据modelVersion确定scale和rotation
            float scale = GetScaleForModelVersion(item.modelVersion);
            Vector3 rotation = GetRotationForModelVersion(item.modelVersion);
            _pipeline.BindModelToPrefab(item.modelPath, scale, rotation);
            OnGenerationCompleted(item.modelPath);
            
            TJLog.Log($"已将历史模型 {item.modelPath} 应用到 {targetAsset.GetPath()} (scale={scale}, rotation={rotation})");
        }

        private float GetScaleForModelVersion(string modelVersion)
        {
            if (string.IsNullOrEmpty(modelVersion))
                return 1f;

            // 从配置读取缩放，根据modelVersion映射到generatorId
            string generatorId = GetGeneratorIdFromModelVersion(modelVersion);
            return ConfigOptionsLoader.LoadModelScale(generatorId, 1f);
        }

        private Vector3 GetRotationForModelVersion(string modelVersion)
        {
            if (string.IsNullOrEmpty(modelVersion))
                return new Vector3(0f, 0f, 0f);

            // 从配置读取旋转，根据modelVersion映射到generatorId
            string generatorId = GetGeneratorIdFromModelVersion(modelVersion);
            return ConfigOptionsLoader.LoadModelRotation(generatorId, new Vector3(0f, 0f, 0f));
        }

        private string GetGeneratorIdFromModelVersion(string modelVersion)
        {
            switch (modelVersion.ToLower())
            {
                case "rodin":                        return "rodin";
                case "tencent-generation":           return "tencent-generation";
                case "混元3.1":                       return "tencent-generation";
                case "tripo-p1-animation":           return "tripo-p1-animation";
                default:                             return "tripo";  // 默认使用tripo配置
            }
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
        
        protected override string GetCurrentAssetGuid()
        {
            return targetAsset?.guid ?? "";
        }

        protected override void SetHistory(List<TJGeneratorsGenerationHistoryItem> history)
        {
            generationHistory = history;
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

            GameObject loadedModel = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (loadedModel != null)
            {
                TJLog.Log($"模型已加载: {assetPath}");
                
                if (targetAsset != null && targetAsset.IsValid())
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(targetAsset.GetPath());
                    if (prefab != null)
                    {
                        EditorGUIUtility.PingObject(prefab);
                    }
                }
            }
            else
            {
                TJLog.LogError("Failed to load model at path: " + assetPath);
            }
            
            Repaint();
        }
        
        // 3D 模型窗口不处理纹理/音频/视频媒体路径

        // ========== 场景实例查找 ==========

        private List<GameObject> GetCachedSceneInstances()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastSceneInstancesRefreshTime > SceneInstancesCacheTtl)
            {
                _cachedSceneInstances = targetAsset != null && targetAsset.IsValid()
                    ? FindPrefabInstancesInScene(targetAsset.GetPath())
                    : new List<GameObject>();
                _lastSceneInstancesRefreshTime = now;
            }

            return _cachedSceneInstances;
        }

        private List<GameObject> FindPrefabInstancesInScene(string prefabPath)
        {
            var instances = new List<GameObject>();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return instances;

            var allObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var root in allObjects)
            {
                FindPrefabInstancesRecursive(root, prefab, instances);
            }
            return instances;
        }

        private void FindPrefabInstancesRecursive(GameObject obj, GameObject prefab, List<GameObject> results)
        {
            var prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(obj);
            if (prefabSource == prefab)
            {
                results.Add(obj);
            }

            foreach (Transform child in obj.transform)
            {
                FindPrefabInstancesRecursive(child.gameObject, prefab, results);
            }
        }

        private void ShowPrefabContextMenu(GameObject prefab)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("聚焦到预制体")), false, () =>
            {
                EditorGUIUtility.PingObject(prefab);
                Selection.activeObject = prefab;
            });

            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            var sceneInstances = FindPrefabInstancesInScene(prefabPath);

            if (sceneInstances.Count > 0)
            {
                menu.AddSeparator("");
                menu.AddDisabledItem(new GUIContent(string.Format(TJGeneratorsL10n.L("场景中的实例 ({0})"), sceneInstances.Count)));

                foreach (var instance in sceneInstances)
                {
                    var instanceRef = instance; // 闭包捕获
                    var path = GetGameObjectPath(instance);
                    menu.AddItem(new GUIContent(string.Format(TJGeneratorsL10n.L("聚焦: {0}"), path)), false, () =>
                    {
                        EditorGUIUtility.PingObject(instanceRef);
                        Selection.activeGameObject = instanceRef;
                        SceneView.lastActiveSceneView?.FrameSelected();
                    });
                }
            }
            else
            {
                menu.AddSeparator("");
                menu.AddDisabledItem(new GUIContent(TJGeneratorsL10n.L("场景中无实例")));
            }

            menu.ShowAsContext();
        }

        private void ShowSceneInstancesMenu(List<GameObject> instances)
        {
            var menu = new GenericMenu();
            foreach (var instance in instances)
            {
                var instanceRef = instance;
                var path = GetGameObjectPath(instance);
                menu.AddItem(new GUIContent(path), false, () =>
                {
                    EditorGUIUtility.PingObject(instanceRef);
                    Selection.activeGameObject = instanceRef;
                    SceneView.lastActiveSceneView?.FrameSelected();
                });
            }
            menu.ShowAsContext();
        }

        private string GetGameObjectPath(GameObject obj)
        {
            var path = obj.name;
            var parent = obj.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        // ========== 目标资产管理 ==========
        
        private void EnsureTargetAsset()
        {
            if (targetAsset != null && targetAsset.IsValid())
                return;

            string previousGuid = targetAsset?.guid ?? "";
            
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
            {
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            }
            
            string prefabPath = AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/New Mesh.prefab");
            prefabPath = CreatePrefabWithPlaceholder(prefabPath);
            
            if (string.IsNullOrEmpty(prefabPath))
            {
                TJLog.LogError("无法创建 Prefab");
                return;
            }
            
            targetAsset = TJGeneratorsAssetReference.FromPath(prefabPath);
            titleContent = new GUIContent(string.Format(TJGeneratorsL10n.L("TJGenerators 3D模型 - {0}"), Path.GetFileNameWithoutExtension(prefabPath)));
            
            if (!string.IsNullOrEmpty(targetAsset.guid))
            {
                openWindows[targetAsset.guid] = this;
                if (string.IsNullOrEmpty(previousGuid))
                    TJGeneratorsHistoryManager.RewriteAssetGuid("", targetAsset.guid,
                        item => string.IsNullOrEmpty(item.modelPath)
                             || item.modelPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
            }
            
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab != null)
            {
                var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance != null)
                {
                    Undo.RegisterCreatedObjectUndo(instance, $"Create {instance.name}");
                    Selection.activeObject = instance;
                }
            }

            RefreshHistory();
            Repaint();
        }
        
        private static string CreatePrefabWithPlaceholder(string path)
        {
            path = Path.ChangeExtension(path, ".prefab");
            
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            var rootGameObject = new GameObject("Generated Mesh");
            try
            {
                var placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
                placeholder.name = "Placeholder";
                placeholder.transform.SetParent(rootGameObject.transform);
                placeholder.transform.localPosition = Vector3.zero;
                placeholder.transform.localRotation = Quaternion.identity;
                placeholder.transform.localScale = Vector3.one;
                
                PrefabUtility.SaveAsPrefabAsset(rootGameObject, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rootGameObject);
            }
            
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));
            
            return path;
        }
    }
}
#endif
