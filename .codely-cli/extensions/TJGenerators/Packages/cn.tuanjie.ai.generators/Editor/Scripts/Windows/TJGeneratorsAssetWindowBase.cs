#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using TJGenerators.Config;
using TJGenerators.Generators;
using TJGenerators.Pipeline;
using TJGenerators.UI;
using TJGenerators.Utils;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;

namespace TJGenerators
{
    /// <summary>
    /// 绑定目标资产的 2D 生成窗口中间基类（Sprite / Material / Image / SpriteSequence）。
    /// 提供 OnGUI 骨架、目标资产头、生命周期与历史「在项目中显示」等共用逻辑。
    /// </summary>
    public abstract class TJGeneratorsAssetWindowBase : GenerationWindowBase,
        IGenerationPipelineHost, IGenerationTriggerHost, IMediaAssetPipelineHost
    {
        [SerializeField]
        protected TJGeneratorsAssetReference _targetAsset;

        [SerializeField]
        protected string textPrompt = "";

        // ========== 子类需提供的文案 / 钩子 ==========

        protected abstract string TargetHeaderLabel { get; }
        protected abstract string UnboundTargetLabel { get; }
        protected abstract string EmptyGeneratorsMessage { get; }
        protected abstract string HistoryApplyLabel { get; }
        protected abstract string PromptControlName { get; }

        protected abstract void RegisterInOpenWindows();
        protected abstract void UnregisterFromOpenWindows();

        /// <summary>模型选择器之后、底部生成区之外的左侧面板内容。</summary>
        protected abstract void DrawLeftPanelBody();

        protected abstract void DrawHistoryPanel(float panelWidth);

        protected abstract bool CanStartGeneration { get; }

        /// <summary>校验并配置生成器后启动 pipeline；基类已设置 isGenerating 等状态前由子类自行校验并 return。</summary>
        protected abstract void OnStartGeneration();

        protected abstract void ApplyHistoryToAsset(int index);

        public abstract string GetAssetSavePath(PipelineMediaType type, ModelGeneratorBase generator);
        public abstract void OnAssetSaved(PipelineMediaType type, string savePath, ModelGeneratorBase generator);

        // ========== 生命周期 ==========

        protected override void OnBootstrapWindowContent()
        {
            if (_targetAsset != null && !string.IsNullOrEmpty(_targetAsset.guid))
                RegisterInOpenWindows();

            InitializeGeneratorsFromConfig(WindowConfigType);
            OnRefreshWindowContent();
        }

        protected override void OnRefreshWindowContent()
        {
            generationHistory = LoadGenerationHistory();
            if (generationHistory != null && generationHistory.Count > 0 && selectedHistoryIndex < 0)
                selectedHistoryIndex = 0;
            CheckAndRecoverInterruptedTasks();
            Repaint();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            wantsMouseMove = true;
            EditorCoroutineUtility.StartCoroutineOwnerless(
                UserInfoHelper.GetUserInfoCoroutine(ConfigManager.GetUserInfoUrl(), OnUserInfoLoaded));
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            wantsMouseMove = false;
            UnregisterFromOpenWindows();
            ClearPreviewCaches();
            OnDisableClearSubclassResources();
        }

        /// <summary>子类清理参考图缩略图等运行时资源。</summary>
        protected virtual void OnDisableClearSubclassResources() { }

        protected override string GetCurrentAssetGuid() => _targetAsset?.guid ?? "";

        protected override void SetHistory(List<TJGeneratorsGenerationHistoryItem> history)
        {
            generationHistory = history;
        }

        protected virtual List<TJGeneratorsGenerationHistoryItem> LoadGenerationHistory()
        {
            return TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentAssetGuid());
        }

        protected override void OnGeneratorRestoredFromTask(ModelGeneratorBase generator)
        {
            base.OnGeneratorRestoredFromTask(generator);
            isGenerating = true;
            generationStatus = TJGeneratorsL10n.L("恢复中...");
        }

        // ========== OnGUI 骨架 ==========

        protected virtual void OnGUI()
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
            OnBeforeDrawChrome(splitLayout);

            if (_generators == null || _generators.Count == 0)
            {
                EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), CommonStyles.WindowBackgroundColor);
                EditorGUILayout.HelpBox(EmptyGeneratorsMessage, MessageType.Error);
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
                        TargetHeaderLabel,
                        DrawTargetHeaderContentRect,
                        SelectTargetAsset
                    );
                    GUILayout.Space(CommonStyles.Space2);
                    UIComponents.DrawModelSelector(
                        currentSelectedModel?.Name ?? _currentGenerator?.DisplayName ?? TJGeneratorsL10n.L("未选择"),
                        currentSelectedModel,
                        OnModelSelected,
                        WindowConfigType
                    );
                    GUILayout.Space(CommonStyles.Space3);

                    DrawLeftPanelBody();

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

        protected virtual void OnBeforeDrawChrome(UIComponents.FixedSplitLayoutParams splitLayout) { }

        protected virtual void DrawTargetHeaderContentRect(Rect rect)
        {
            if (_targetAsset != null && _targetAsset.IsValid())
            {
                string name = Path.GetFileNameWithoutExtension(_targetAsset.GetPath());
                if (GUI.Button(rect, name, CommonStyles.TargetPrefabNameStyle))
                    SelectTargetAsset();
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            }
            else
            {
                GUI.Label(rect, UnboundTargetLabel, CommonStyles.ContentStyle);
            }
        }

        protected virtual void SelectTargetAsset()
        {
            if (_targetAsset == null || !_targetAsset.IsValid())
                return;
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(_targetAsset.GetPath());
            if (obj != null)
            {
                EditorGUIUtility.PingObject(obj);
                Selection.activeObject = obj;
            }
        }

        protected virtual void OnModelSelected(AIModelInfo model) => OnModelSelectedBase(model);

        protected virtual void DrawInputSection()
        {
            var genConfig = GetCurrentGeneratorConfig();
            textPrompt = DrawConfiguredTextPromptInput(textPrompt, PromptControlName, genConfig);
            if (ShouldShowTextInput(genConfig))
                GUILayout.Space(CommonStyles.Space3);
        }

        protected virtual void DrawConfigurationSection()
        {
            var provider = _currentGenerator as IGeneratorParameterProvider;
            showAdvancedSettings = DrawConfiguredAdvancedSettingsFoldout(
                showAdvancedSettings,
                provider,
                GetCurrentGeneratorParameters());
        }

        protected virtual void DrawGenerationSection(LeftPanelBottomDock.Layout layout)
        {
            UIComponents.DrawGenerationSectionAt(
                layout,
                isGenerating,
                generationProgress,
                generationStatus,
                CanStartGeneration,
                StartGeneration,
                null,
                Repaint);
        }

        // ========== 历史 ==========

        protected virtual void DrawDefaultHistoryActions(bool showTileSizeSlider = false)
        {
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            bool hasSelection = selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count;
            bool itemGenerating = hasSelection && generationHistory[selectedHistoryIndex].isGenerating;
            GUI.enabled = hasSelection && !itemGenerating;
            if (GUILayout.Button(HistoryApplyLabel, GUILayout.Height(25)))
                ApplyHistoryToAsset(selectedHistoryIndex);
            if (GUILayout.Button(TJGeneratorsL10n.L("在项目中显示"), GUILayout.Height(25)))
                ShowHistoryInProject(selectedHistoryIndex);
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            if (showTileSizeSlider)
            {
                GUILayout.BeginVertical();
                GUILayout.Space(6);
                currentHistoryTileSize = GUILayout.HorizontalSlider(
                    currentHistoryTileSize, MinHistoryTileSize, MaxHistoryTileSize, GUILayout.Width(60f));
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
        }

        protected virtual void ShowHistoryInProject(int index)
        {
            if (index < 0 || index >= generationHistory.Count)
                return;

            var item = generationHistory[index];
            string assetPath = !string.IsNullOrEmpty(item.modelPath) ? item.modelPath : item.imagePath;
            var asset = !string.IsNullOrEmpty(assetPath)
                ? AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath)
                : null;
            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);
                Selection.activeObject = asset;
            }
        }

        protected virtual void SelectHistoryItemByAssetPath(string assetPath)
        {
            if (generationHistory == null || string.IsNullOrEmpty(assetPath))
                return;
            int index = generationHistory.FindIndex(x => x.modelPath == assetPath || x.imagePath == assetPath);
            if (index >= 0)
                selectedHistoryIndex = index;
        }

        // ========== 生成 / Host ==========

        protected void StartGeneration()
        {
            OnStartGeneration();
        }

        public TJGeneratorsAssetReference GetTargetAsset() => _targetAsset;

        public void StartGeneration(ModelGeneratorBase generator)
        {
            if (generator == _currentGenerator)
                StartGeneration();
        }

        public virtual void OnGenerationCompleted(string assetPath)
        {
            SelectHistoryItemByAssetPath(assetPath);
        }

        protected static void EnsureTjGeneratorsHistoryFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                AssetDatabase.CreateFolder("Assets/TJGenerators", "History");
        }

        protected static string BuildHistoryTexturePath(string prefix, string extension = ".png")
        {
            EnsureTjGeneratorsHistoryFolder();
            if (!extension.StartsWith(".", StringComparison.Ordinal))
                extension = "." + extension;
            string uniqueName = prefix + DateTime.Now.ToString("yyyyMMdd_HHmmss") + extension;
            return AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/History/" + uniqueName);
        }

        protected void MarkGenerationStarted()
        {
            isGenerating = true;
            generationStatus = TJGeneratorsL10n.L("准备中...");
            generationProgress = 0f;
        }

        protected void MarkGenerationCompleted()
        {
            generationStatus = TJGeneratorsL10n.L("完成");
            generationProgress = 1f;
            isGenerating = false;
        }

        protected void StartPipelineForCurrentGenerator()
        {
            string assetGuid = _targetAsset?.guid ?? "";
            EditorCoroutineUtility.StartCoroutineOwnerless(_pipeline.StartGeneration(_currentGenerator, assetGuid));
        }
    }
}
#endif
