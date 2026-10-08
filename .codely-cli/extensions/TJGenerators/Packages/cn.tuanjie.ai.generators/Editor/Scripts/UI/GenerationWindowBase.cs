#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using TJGenerators.Config;
using TJGenerators.Generators;
using TJGenerators.Pipeline;
using TJGenerators.Utils;
using Unity.EditorCoroutines.Editor;

namespace TJGenerators.UI
{
    /// <summary>
    /// 各生成器窗口的抽象基类，提供用户信息加载、协程启动、任务恢复、OpenForAsset 等通用逻辑。
    /// 子类按需重写并实现 IGenerationPipelineHost。
    /// </summary>
    public abstract class GenerationWindowBase : EditorWindow
    {
        // ========== 用户信息 ==========
        // LastUserInfo 由 UserInfoHelper 维护，底部栏直接读取邮箱

        // ========== 生成器相关 ==========
        protected List<ModelGeneratorBase> _generators;
        protected int _currentGeneratorIndex;
        protected ModelGeneratorBase _currentGenerator;
        protected GenerationPipeline _pipeline;
        protected AIModelInfo currentSelectedModel;
        protected Dictionary<string, int> _baseGeneratorIndexById =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        protected Dictionary<string, ModelGeneratorBase> _baseGeneratorById =
            new Dictionary<string, ModelGeneratorBase>(StringComparer.OrdinalIgnoreCase);
        protected Dictionary<string, GeneratorConfig> _baseGeneratorConfigById =
            new Dictionary<string, GeneratorConfig>(StringComparer.OrdinalIgnoreCase);
        protected Dictionary<string, List<ParameterConfig>> _baseGeneratorParametersById =
            new Dictionary<string, List<ParameterConfig>>(StringComparer.OrdinalIgnoreCase);
        protected Dictionary<string, string> _baseModelVersionDisplayLabelIndex =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        protected Dictionary<string, ModelGeneratorBase> _baseGeneratorByModelVersionIndex =
            new Dictionary<string, ModelGeneratorBase>(StringComparer.OrdinalIgnoreCase);

        // ========== 历史记录相关 ==========
        protected List<TJGeneratorsGenerationHistoryItem> generationHistory =
            new List<TJGeneratorsGenerationHistoryItem>();
        protected int selectedHistoryIndex = -1;

        // ========== 生成状态 ==========
        protected bool isGenerating;
        protected string generationStatus = "";
        protected float generationProgress;

        /// <summary>
        /// 本窗口 Pipeline 上是否有进行中的生成（切换模型下拉选项后仍为 true，直至任务结束）。
        /// </summary>
        public bool IsPipelineBusy => _pipeline != null && _pipeline.IsPipelineBusy;

        // ========== 预览缓存（Sprite/Skybox/3DModel 窗口使用） ==========
        protected Dictionary<string, Texture2D> historyPreviewCache =
            new Dictionary<string, Texture2D>();
        protected Dictionary<string, Texture2D> urlPreviewCache =
            new Dictionary<string, Texture2D>();
        protected HashSet<string> urlPreviewLoading = new HashSet<string>();
        protected HashSet<string> urlPreviewFailed = new HashSet<string>();

        // ========== 预览加载队列（用于异步加载本地预览图，避免OnGUI中IO卡顿） ==========
        protected struct PreviewLoadRequest
        {
            public string cacheKey;
            public string filePath;
            public bool cacheToUrlPreview;
        }

        private Queue<PreviewLoadRequest> _previewLoadQueue = new Queue<PreviewLoadRequest>();
        private HashSet<string> _previewLoadPending = new HashSet<string>();
        private bool _isPreviewLoaderRunning;

        // ========== 布局相关 ==========
        protected Vector2 scrollPosition;
        protected Vector2 historyScrollPosition;
        protected float currentHistoryPanelWidth = 280f;
        protected bool isVerticalLayout;
        /// <summary>左侧内容区有效宽度；勿在字段初始化器中读 <see cref="CommonStyles"/>（会触发 EditorPrefs，SO 构造阶段非法）。各窗口在 OnGUI 开头赋值。</summary>
        protected float _effectiveLeftPanelWidth;

        // ========== 历史缩略图尺寸 ==========
        protected const float MinHistoryTileSize = 64f;
        protected const float MaxHistoryTileSize = 300f;
        protected const float DefaultHistoryTileSize = 120f;
        protected float currentHistoryTileSize = DefaultHistoryTileSize;
        protected const int HistoryTileBasePadding = 5;
        protected const int HistoryTileBaseMargin = 5;
        protected const float HistoryPreviewBaseInsetTotal = 8f;

        private float _cachedHistoryTileStyleScale = -1f;
        private GUIStyle _scaledHistoryTileStyle;
        private GUIStyle _scaledHistoryTileSelectedStyle;

        // ========== 高级设置 ==========
        protected bool showAdvancedSettings;

        /// <summary>
        /// 获取当前资产 GUID，用于任务恢复与历史加载。
        /// </summary>
        protected abstract string GetCurrentAssetGuid();

        /// <summary>
        /// 语言切换时刷新窗口。子类若重写 OnEnable/OnDisable，须调用 base。
        /// </summary>
        protected virtual void OnEnable()
        {
            // Domain reload 会清空不可序列化的生成器实例；若仍标记就绪则强制重新初始化并恢复模型选择。
            if (_windowContentReady && NeedsGeneratorRebootstrap())
                _windowContentReady = false;

            if (!_windowContentReady)
                BootstrapWindow();
            TJGeneratorsL10n.OnLanguageChanged += Repaint;
        }

        protected virtual void OnDisable()
        {
            TJGeneratorsL10n.OnLanguageChanged -= Repaint;
        }

        // ========== 窗口内容延迟初始化 ==========
        [NonSerialized] private bool _windowContentReady;
        [NonSerialized] private int _bootstrapRetryCount;
        private const int MaxBootstrapRetries = 5;

        /// <summary>
        /// Domain reload 后生成器列表或当前选择丢失时需要重新 Bootstrap。
        /// </summary>
        private bool NeedsGeneratorRebootstrap()
        {
            return _generators == null || _generators.Count == 0 || _currentGenerator == null;
        }

        /// <summary>
        /// 在目标资产/模式确定后执行首次初始化。由 <see cref="OnEnable"/> 在首帧绘制前同步调用；
        /// OpenForAsset 须在 <see cref="EditorWindow.Show"/> 之前完成 setTarget，以便 Bootstrap 读到正确绑定。
        /// 对已就绪的窗口幂等（防止重复完整初始化）。
        /// Domain reload 后 AssetDatabase 可能尚未就绪导致生成器列表为空，此时通过 delayCall 重试。
        /// </summary>
        protected void BootstrapWindow()
        {
            if (_windowContentReady)
                return;
            OnBootstrapWindowContent();
            if (_generators == null || _generators.Count == 0)
            {
                if (_bootstrapRetryCount < MaxBootstrapRetries)
                {
                    _bootstrapRetryCount++;
                    _windowContentReady = false;
                    EditorApplication.delayCall += () =>
                    {
                        BootstrapWindow();
                        Repaint();
                    };
                    return;
                }
                TJLog.LogWarning($"{LogTag} 多次重试后仍未找到生成器配置，请检查 GeneratorConfig.json");
            }
            else if (_currentGenerator == null)
            {
                // 生成器已加载但当前模型丢失（domain reload），按偏好恢复选择。
                SelectDefaultGeneratorFromPreference(WindowConfigType);
            }
            _bootstrapRetryCount = 0;
            _windowContentReady = true;
        }

        /// <summary>首次初始化：生成器、历史、任务恢复等。子类重写。</summary>
        protected virtual void OnBootstrapWindowContent() { }

        /// <summary>域重载或窗口重新启用时刷新。子类重写。</summary>
        protected virtual void OnRefreshWindowContent() { }

        /// <summary>
        /// 设置历史记录列表，任务恢复后刷新时调用。
        /// </summary>
        protected abstract void SetHistory(List<TJGeneratorsGenerationHistoryItem> history);

        /// <summary>
        /// 检查并恢复中断的任务。通常在 <see cref="OnRefreshWindowContent"/> 中调用。
        /// </summary>
        protected void CheckAndRecoverInterruptedTasks()
        {
            TJGeneratorsTaskRecovery.CheckAndRecoverInterruptedTasks(
                this,
                GetCurrentAssetGuid,
                SetHistory,
                ResumeInterruptedTask,
                Repaint
            );
        }

        /// <summary>
        /// 在窗口标题栏右上角绘制「?」帮助图标，点击打开使用文档。
        /// Unity 会自动调用此方法（EditorWindow 约定）。
        /// </summary>
        private static GUIContent s_HelpButtonContent;
        protected virtual void ShowButton(Rect rect)
        {
            if (s_HelpButtonContent == null)
            {
                s_HelpButtonContent = EditorGUIUtility.IconContent("_Help");
                s_HelpButtonContent.tooltip = TJGeneratorsL10n.L("怎么用？查看使用文档");
            }

            if (GUI.Button(rect, s_HelpButtonContent, GUIStyle.none))
                TJGeneratorsDocs.OpenDocumentation();
        }

        /// <summary>
        /// 恢复单个中断的任务。默认实现使用 _generators 和 _pipeline，并设置 _currentGenerator。
        /// 子类可重写以添加额外逻辑（如设置 isGenerating、currentSelectedModel 等）。
        /// </summary>
        protected virtual void ResumeInterruptedTask(InterruptedTaskData task)
        {
            ResumeInterruptedTaskCore(
                task,
                _generators,
                _pipeline,
                OnGeneratorRestoredFromTask
            );
        }

        /// <summary>
        /// 当生成器从中断任务恢复后的回调。默认实现设置 _currentGenerator。
        /// 子类可重写以添加额外逻辑（如设置 isGenerating、currentSelectedModel 等）。
        /// </summary>
        protected virtual void OnGeneratorRestoredFromTask(ModelGeneratorBase generator)
        {
            _currentGenerator = generator;
        }

        /// <summary>
        /// 恢复任务的通用核心逻辑。处理任务恢复的完整流程。
        /// 通常不需要子类重写，优先重写 ResumeInterruptedTask 或 OnGeneratorRestoredFromTask。
        /// </summary>
        protected void ResumeInterruptedTaskCore(
            InterruptedTaskData task,
            List<ModelGeneratorBase> generators,
            GenerationPipeline pipeline,
            Action<ModelGeneratorBase> onGeneratorFound
        )
        {
            if (TJGeneratorsTaskRecovery.IsRecovering(task.backendTaskId))
                return;

            TJGeneratorsTaskRecovery.MarkAsRecovering(task.backendTaskId);
            TJLog.Log($"[TJGeneratorsTaskRecovery] 恢复任务: {task.backendTaskId}");

            var generator = generators?.Find(g => g.GeneratorId == task.modelVersion);
            if (generator != null)
            {
                generator.RestoreFromInterruptedTask(task);
                onGeneratorFound?.Invoke(generator);
                pipeline.RegisterActiveGenerator(generator);
                Repaint();
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline.PollTaskStatus(generator, task.backendTaskId)
                );
            }
            else
            {
                TJLog.LogWarning(
                    $"[TJGeneratorsTaskRecovery] 未找到对应的生成器: {task.modelVersion}"
                );
            }
        }

        /// <summary>
        /// 标准的用户信息加载回调（邮箱等已写入 <see cref="UserInfoHelper.LastUserInfo"/>）。
        /// </summary>
        protected void OnUserInfoLoaded(int? credits)
        {
            Repaint();
        }

        protected void DrawLeftPanelColumn(
            float leftPanelWidth,
            ref Vector2 scrollPosition,
            Action drawScrollContent)
        {
            LeftPanelLayout.DrawColumn(
                position.height,
                leftPanelWidth,
                ref scrollPosition,
                drawScrollContent);
        }

        /// <summary>底部 Dock 用户信息栏邮箱；返回 null 时使用 <see cref="UserInfoHelper.LastUserInfo"/>。</summary>
        protected virtual string GetStatusBarEmail() => null;

        /// <summary>
        /// 绝对绘制左栏底部：操作按钮 + <see cref="UserInfoBar"/>。
        /// </summary>
        protected void DrawLeftPanelBottomDock(
            float leftPanelWidth,
            Action<LeftPanelBottomDock.Layout> drawAction)
        {
            LeftPanelBottomDock.Draw(
                position.height,
                leftPanelWidth,
                drawAction,
                GetStatusBarEmail());
        }

        protected void DrawGeneratorActionButton(LeftPanelBottomDock.Layout layout)
        {
            if (this is IGenerationPipelineHost host)
                _currentGenerator?.DrawActionButton(host, layout);
        }

        /// <summary>
        /// 当前历史缩略图相对默认尺寸的缩放因子。
        /// </summary>
        protected float GetHistoryTileScale()
        {
            return Mathf.Clamp(currentHistoryTileSize / DefaultHistoryTileSize, 0.5f, 3f);
        }

        /// <summary>
        /// 根据当前缩放返回历史 tile 样式（含同步缩放的 padding/margin）。
        /// </summary>
        protected GUIStyle GetScaledHistoryTileStyle(bool isSelected)
        {
            float scale = GetHistoryTileScale();
            if (_scaledHistoryTileStyle == null || _scaledHistoryTileSelectedStyle == null || !Mathf.Approximately(scale, _cachedHistoryTileStyleScale))
            {
                int scaledPadding = Mathf.Max(Mathf.Max(0, Mathf.RoundToInt(4f)), Mathf.RoundToInt(HistoryTileBasePadding * scale));
                int scaledMargin = Mathf.Max(Mathf.Max(0, Mathf.RoundToInt(4f)), Mathf.RoundToInt(HistoryTileBaseMargin * scale));

                _scaledHistoryTileStyle = new GUIStyle(CommonStyles.HistoryTileStyle)
                {
                    padding = new RectOffset(scaledPadding, scaledPadding, scaledPadding, scaledPadding),
                    margin = new RectOffset(scaledMargin, scaledMargin, scaledMargin, scaledMargin)
                };
                _scaledHistoryTileSelectedStyle = new GUIStyle(CommonStyles.HistoryTileSelectedStyle)
                {
                    padding = new RectOffset(scaledPadding, scaledPadding, scaledPadding, scaledPadding),
                    margin = new RectOffset(scaledMargin, scaledMargin, scaledMargin, scaledMargin)
                };
                _cachedHistoryTileStyleScale = scale;
            }

            return isSelected ? _scaledHistoryTileSelectedStyle : _scaledHistoryTileStyle;
        }

        /// <summary>
        /// 单个历史 tile 在横向实际占用的布局宽度：<see cref="GUILayout.Width"/> 外加样式左右 margin（与 <see cref="GetScaledHistoryTileStyle"/> 一致）。
        /// </summary>
        protected float GetHistoryTileHorizontalStride(float tileOuterWidth)
        {
            float scale = GetHistoryTileScale();
            float margin = Mathf.Max(4f, Mathf.Round(HistoryTileBaseMargin * scale));
            return tileOuterWidth + 2f * margin;
        }

        /// <summary>
        /// 根据可用内容宽度计算每行历史条目数量，避免因忽略 tile margin 导致横向溢出与滚动条。
        /// </summary>
        protected int ComputeHistoryItemsPerRow(float contentWidth, float tileOuterWidth)
        {
            float stride = GetHistoryTileHorizontalStride(tileOuterWidth);
            return Mathf.Max(1, Mathf.FloorToInt(Mathf.Max(1f, contentWidth) / stride));
        }

        /// <summary>
        /// 获取历史缩略图预览区域尺寸（含按缩放同步的 inset）。
        /// </summary>
        protected float GetScaledHistoryPreviewSize(float tileWidth)
        {
            float inset = HistoryPreviewBaseInsetTotal * GetHistoryTileScale();
            return Mathf.Max(1f, tileWidth - inset);
        }

        /// <summary>
        /// 标准历史面板配置：网格缩略图 + 可选大预览 + 底部操作区。
        /// </summary>
        protected sealed class StandardHistoryPanelOptions
        {
            public Action<Rect, TJGeneratorsGenerationHistoryItem> DrawTilePreview;
            public Func<TJGeneratorsGenerationHistoryItem, string> GetModelLabel;
            public Action<int> ShowContextMenu;
            public Action DrawHistoryActions;

            public Func<TJGeneratorsGenerationHistoryItem, string> GetPrimaryLabel;
            public Func<TJGeneratorsGenerationHistoryItem, Texture2D> GetLargePreviewTexture;
            public Func<float, float, float> DrawLargePreviewBlock;
            public Action OnBeforeDraw;
            public Action<int, int> OnBeforeSelectHistoryItem;
            public float BottomMargin = 100f;
            public float ScrollTopSpacing;
            public bool AddPanelTopSpacing;
            public float? HistoryContentWidth;
            public Func<float, float> GetLabelHeight;
        }

        /// <summary>
        /// 校正 <see cref="selectedHistoryIndex"/>，避免列表为空或越界。
        /// </summary>
        protected void EnsureHistorySelectionIndex()
        {
            if (generationHistory == null)
                generationHistory = new List<TJGeneratorsGenerationHistoryItem>();

            if (generationHistory.Count > 0)
                selectedHistoryIndex = Mathf.Clamp(selectedHistoryIndex, 0, generationHistory.Count - 1);
            else
                selectedHistoryIndex = -1;
        }

        /// <summary>
        /// 绘制标准历史面板：大预览区、网格缩略图、底部操作按钮。
        /// </summary>
        protected void DrawStandardHistoryPanel(float panelWidth, StandardHistoryPanelOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            float historyPanelHeight = isVerticalLayout ? position.height * 0.45f : position.height;
            EnsureHistorySelectionIndex();
            options.OnBeforeDraw?.Invoke();

            UIComponents.BeginHistoryPanel(panelWidth, historyPanelHeight, isVerticalLayout);
            if (options.AddPanelTopSpacing)
                GUILayout.Space(5);

            float previewBlockHeight = 0f;
            if (options.DrawLargePreviewBlock != null)
                previewBlockHeight = options.DrawLargePreviewBlock(panelWidth, historyPanelHeight);
            else if (options.GetLargePreviewTexture != null)
                previewBlockHeight = DrawDefaultHistoryLargePreview(panelWidth, historyPanelHeight, options.GetLargePreviewTexture);

            if (options.ScrollTopSpacing > 0f)
                GUILayout.Space(options.ScrollTopSpacing);

            float scrollHeight = historyPanelHeight - previewBlockHeight - options.ScrollTopSpacing - options.BottomMargin;
            historyScrollPosition = GUILayout.BeginScrollView(
                historyScrollPosition,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUILayout.Height(scrollHeight));

            if (generationHistory.Count == 0)
                UIComponents.DrawHistoryEmptyState();
            else
                DrawStandardHistoryGrid(options);

            GUILayout.EndScrollView();
            options.DrawHistoryActions?.Invoke();
            UIComponents.EndHistoryPanel();
        }

        protected float DrawDefaultHistoryLargePreview(
            float panelWidth,
            float historyPanelHeight,
            Func<TJGeneratorsGenerationHistoryItem, Texture2D> getPreviewTexture)
        {
            Texture2D historyPreviewTex = null;
            bool showHistoryPreview = false;
            if (selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count)
            {
                var selectedItem = generationHistory[selectedHistoryIndex];
                if (!selectedItem.isGenerating)
                {
                    showHistoryPreview = true;
                    historyPreviewTex = getPreviewTexture?.Invoke(selectedItem);
                }
            }

            return UIComponents.DrawHistoryTexturePreview(
                historyPreviewTex,
                showHistoryPreview,
                isVerticalLayout,
                panelWidth,
                historyPanelHeight);
        }

        private void DrawStandardHistoryGrid(StandardHistoryPanelOptions options)
        {
            float tileWidth = currentHistoryTileSize;
            float labelHeight = options.GetLabelHeight != null
                ? options.GetLabelHeight(tileWidth)
                : (currentHistoryTileSize >= 100f ? 40f : 32f);
            float tileHeight = tileWidth + labelHeight;
            float contentWidth = options.HistoryContentWidth
                ?? CommonStyles.HistoryScrollViewLayoutWidth(currentHistoryPanelWidth);
            int itemsPerRow = ComputeHistoryItemsPerRow(contentWidth, tileWidth);

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
                        GUILayout.Height(tileHeight));

                    float previewSize = GetScaledHistoryPreviewSize(tileWidth);
                    Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize);
                    options.DrawTilePreview?.Invoke(previewRect, item);

                    if (!item.isGenerating
                        && Event.current.type == EventType.MouseDown
                        && previewRect.Contains(Event.current.mousePosition))
                    {
                        int oldIndex = selectedHistoryIndex;
                        options.OnBeforeSelectHistoryItem?.Invoke(index, oldIndex);
                        selectedHistoryIndex = index;
                        Event.current.Use();
                        Repaint();
                    }

                    if (!item.isGenerating
                        && options.ShowContextMenu != null
                        && Event.current.type == EventType.ContextClick
                        && previewRect.Contains(Event.current.mousePosition))
                    {
                        options.ShowContextMenu(index);
                        Event.current.Use();
                    }

                    string primaryLabel = options.GetPrimaryLabel != null
                        ? options.GetPrimaryLabel(item)
                        : item.GetDisplayName();
                    GUILayout.Label(primaryLabel, CommonStyles.HistoryLabelStyle);

                    if (options.GetModelLabel != null)
                        GUILayout.Label(options.GetModelLabel(item), CommonStyles.SmallGreyCenterLabelStyle);

                    GUILayout.EndVertical();
                }
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>
        /// 初始化生成器的模板方法：根据 ConfigType 自动加载生成器列表、重建索引、选择默认生成器、创建 Pipeline。
        /// 子类可通过重写 OnBeforeGeneratorInitialize、OnAfterGeneratorInitialize 来插入自定义逻辑。
        /// </summary>
        /// <param name="configType">配置类型（Generator、Sprite、Music、Skybox、Material、SpriteSequence）</param>
        protected void InitializeGeneratorsFromConfig(ConfigType configType)
        {
            OnBeforeGeneratorInitialize(configType);

            _generators = new List<ModelGeneratorBase>();

            // 根据配置类型获取生成器列表
            var generatorConfigs = ConfigManager.GetGenerators(configType);
            if (generatorConfigs == null || generatorConfigs.Count == 0)
            {
                TJLog.LogWarning($"{LogTag} 未找到生成器配置，ConfigType: {configType}");
                OnAfterGeneratorInitialize(configType);
                return;
            }

            // 创建 DynamicGenerator 实例
            foreach (var genConfig in generatorConfigs)
            {
                _generators.Add(new DynamicGenerator(genConfig));
            }

            // 重建生成器索引
            RebuildGeneratorIndexes(configType);

            // 选择默认生成器
            SelectDefaultGeneratorFromPreference(configType);

            // 创建 Pipeline（子类必须实现 IGenerationPipelineHost）
            // UI 窗口发起的生成统一标记为 ui 来源（fromMethod 头）
            _pipeline = new GenerationPipeline((IGenerationPipelineHost)this, configType, GenerationRequestOrigin.Ui);

            TJLog.Log($"{LogTag} 初始化 {_generators.Count} 个生成器，当前: {_currentGenerator?.DisplayName}");

            OnAfterGeneratorInitialize(configType);
        }

        /// <summary>
        /// 根据偏好设置选择默认生成器。子类可重写以自定义默认选择逻辑（如指定回退生成器 ID）。
        /// </summary>
        protected virtual void SelectDefaultGeneratorFromPreference(ConfigType configType)
        {
            if (_generators == null || _generators.Count == 0)
            {
                _currentGenerator = null;
                _currentGeneratorIndex = 0;
                currentSelectedModel = null;
                return;
            }

            // 尝试从偏好设置加载
            string preferredId = TJGeneratorsModelSelectorWindow.GetPreferredModelId(
                _generators.Select(g => g.GeneratorId), 
                configType);

            int preferredIndex = -1;
            if (!string.IsNullOrEmpty(preferredId))
                TryGetGeneratorIndex(preferredId, out preferredIndex);

            // 子类可重写提供回退逻辑（如 Skybox 优先选择 "rodin"）
            if (preferredIndex < 0)
                preferredIndex = GetFallbackGeneratorIndex();

            if (preferredIndex < 0)
                preferredIndex = 0;

            SetCurrentGeneratorByIndex(preferredIndex);
        }

        /// <summary>
        /// 获取回退生成器索引。子类可重写以指定特定的回退生成器 ID（如 "rodin"）。
        /// </summary>
        protected virtual int GetFallbackGeneratorIndex()
        {
            return 0;
        }

        /// <summary>
        /// 设置当前生成器（按索引）。
        /// </summary>
        protected void SetCurrentGeneratorByIndex(int index)
        {
            if (_generators == null || _generators.Count == 0)
            {
                _currentGenerator = null;
                _currentGeneratorIndex = 0;
                currentSelectedModel = null;
                return;
            }

            _currentGeneratorIndex = Mathf.Clamp(index, 0, _generators.Count - 1);
            _currentGenerator = _generators[_currentGeneratorIndex];

            if (_currentGenerator != null)
            {
                currentSelectedModel = BuildModelInfoFromGenerator(_currentGenerator);
            }
        }

        /// <summary>
        /// 在生成器初始化前调用，子类可重写以插入自定义逻辑。
        /// </summary>
        protected virtual void OnBeforeGeneratorInitialize(ConfigType configType) { }

        /// <summary>
        /// 在生成器初始化后调用，子类可重写以插入自定义逻辑（如订阅配置更新事件）。
        /// </summary>
        protected virtual void OnAfterGeneratorInitialize(ConfigType configType) { }

        /// <summary>
        /// 获取窗口的配置类型，用于 BuildModelInfoFromGenerator 等方法。
        /// 子类需实现此属性。
        /// </summary>
        protected abstract ConfigType WindowConfigType { get; }

        /// <summary>
        /// 获取窗口的日志标签，如 "[TJGeneratorsSprite]"。
        /// </summary>
        protected abstract string LogTag { get; }

        /// <summary>
        /// 根据生成器构建模型信息（用于模型选择器显示）。
        /// 子类可重写此方法以支持多配置类型（如 Sprite 窗口的 Sprite/Material 模式）。
        /// </summary>
        protected virtual AIModelInfo BuildModelInfoFromGenerator(ModelGeneratorBase generator)
        {
            if (generator == null)
                return null;
            var genConfig = ConfigManager.GetGeneratorConfig(
                WindowConfigType,
                generator.GeneratorId
            );
            if (genConfig == null)
                return null;
            var selectorConfig = genConfig.modelSelector;
            return new AIModelInfo
            {
                Id = generator.GeneratorId,
                Name = !string.IsNullOrEmpty(selectorConfig?.name)
                    ? TJGeneratorsL10n.L(selectorConfig.name)
                    : (generator.DisplayName ?? generator.GeneratorId),
                Description = TJGeneratorsL10n.L(selectorConfig?.description ?? string.Empty),
                FunctionTags = selectorConfig?.functionTags?.Select(t => TJGeneratorsL10n.L(t)).ToArray() ?? Array.Empty<string>(),
                VendorTags = selectorConfig?.vendorTags?.Select(t => TJGeneratorsL10n.L(t)).ToArray() ?? Array.Empty<string>(),
                Icon = LoadModelIconForWindow(generator.GeneratorId, selectorConfig?.iconPath),
            };
        }

        /// <summary>
        /// 为窗口初始化阶段恢复当前模型图标（与模型选择器候选路径规则保持一致）。
        /// </summary>
        private static Texture2D LoadModelIconForWindow(string modelId, string configuredIconPath)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(configuredIconPath))
                candidates.Add(configuredIconPath);

            string normalizedId = (modelId ?? string.Empty).Replace(".", "_").Replace("-", "_");
            candidates.Add($"Packages/cn.tuanjie.ai.generators/Editor/EditorTextures/model_{normalizedId}.png");
            candidates.Add($"Assets/Editor/EditorTextures/model_{normalizedId}.png");
            candidates.Add($"Packages/cn.tuanjie.ai.generators/Editor/Resources/model_{normalizedId}.png");
            candidates.Add($"Assets/Editor/Resources/model_{normalizedId}.png");

            foreach (var path in candidates.Where(path => !string.IsNullOrEmpty(path)).Distinct())
            {
                var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (icon != null)
                    return icon;
            }

            return null;
        }

        /// <summary>
        /// 刷新历史记录，并保留当前的选中状态（如果该记录仍然存在）
        /// </summary>
        public virtual void RefreshHistory()
        {
            string oldSelectedPath = null;
            string oldSelectedTaskId = null;
            if (generationHistory != null && selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count)
            {
                var item = generationHistory[selectedHistoryIndex];
                oldSelectedPath = item.modelPath ?? item.imagePath;
                oldSelectedTaskId = item.taskId;
            }

            generationHistory = TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentAssetGuid());

            if (generationHistory.Count > 0)
            {
                int newIndex = -1;
                if (!string.IsNullOrEmpty(oldSelectedPath) || !string.IsNullOrEmpty(oldSelectedTaskId))
                {
                    newIndex = generationHistory.FindIndex(x => 
                        (!string.IsNullOrEmpty(oldSelectedPath) && (x.modelPath == oldSelectedPath || x.imagePath == oldSelectedPath)) ||
                        (!string.IsNullOrEmpty(oldSelectedTaskId) && x.taskId == oldSelectedTaskId));
                }

                if (newIndex >= 0)
                {
                    selectedHistoryIndex = newIndex;
                }
                else
                {
                    selectedHistoryIndex = 0;
                }
            }
            else
            {
                selectedHistoryIndex = -1;
            }
            
            Repaint();
        }

        /// <summary>
        /// 刷新用户信息。子类可直接使用或重写。
        /// </summary>
        public virtual void RefreshUserInfo()
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(
                UserInfoHelper.GetUserInfoCoroutine(
                    ConfigManager.GetUserInfoUrl(),
                    OnUserInfoLoaded
                )
            );
        }

        /// <summary>
        /// 显示对话框。默认实现会重置 isGenerating 状态并设置 generationStatus。
        /// </summary>
        public virtual void ShowDialog(string title, string message)
        {
            isGenerating = false;
            var friendlyError = ErrorDialogUtils.ConvertToUserFriendlyError(title, message);
            generationStatus = friendlyError.Message;
            
            // 使用统一的错误对话框工具
            ErrorDialogUtils.ShowErrorDialog(title, message, "GenerationWindow");
        }

        /// <summary>
        /// 模型选择回调的通用实现。根据选中的模型 ID 更新 _currentGeneratorIndex 和 _currentGenerator。
        /// 子类可重写以添加额外逻辑。
        /// </summary>
        protected virtual void OnModelSelectedBase(AIModelInfo model)
        {
            if (model == null || _generators == null)
                return;

            if (_baseGeneratorIndexById.TryGetValue(model.Id, out var indexed) &&
                indexed >= 0 && indexed < _generators.Count)
            {
                _currentGeneratorIndex = indexed;
                _currentGenerator = _generators[indexed];
                currentSelectedModel = model;
                ResetInputStateAfterModelChange();
                Repaint();
                return;
            }

            for (int i = 0; i < _generators.Count; i++)
            {
                if (_generators[i].GeneratorId != model.Id)
                    continue;

                _currentGeneratorIndex = i;
                _currentGenerator = _generators[i];
                currentSelectedModel = model;
                ResetInputStateAfterModelChange();
                Repaint();
                return;
            }
        }

        /// <summary>
        /// 切换生成器后按当前 <see cref="GeneratorConfig.uiLayout"/> 清理不应保留的输入。
        /// 子类重写以重置窗口内的 prompt、参考图等字段。
        /// </summary>
        protected virtual void ResetInputStateAfterModelChange()
        {
        }

        protected static void ResetTextPromptIfHidden(GeneratorConfig config, ref string prompt)
        {
            if (!ShouldShowTextInput(config))
                prompt = string.Empty;
        }

        /// <summary>
        /// 重建生成器相关索引：GeneratorId/index/config/parameters，以及历史 modelVersion 的显示名索引。
        /// 子类在初始化/切换模式后调用。
        /// </summary>
        protected void RebuildGeneratorIndexes(ConfigType configType)
        {
            _baseGeneratorIndexById.Clear();
            _baseGeneratorById.Clear();
            _baseGeneratorConfigById.Clear();
            _baseGeneratorParametersById.Clear();
            _baseModelVersionDisplayLabelIndex.Clear();
            _baseGeneratorByModelVersionIndex.Clear();

            if (_generators != null)
            {
                for (int i = 0; i < _generators.Count; i++)
                {
                    var gen = _generators[i];
                    if (gen == null || string.IsNullOrEmpty(gen.GeneratorId))
                        continue;

                    _baseGeneratorIndexById[gen.GeneratorId] = i;
                    _baseGeneratorById[gen.GeneratorId] = gen;
                    _baseModelVersionDisplayLabelIndex[gen.GeneratorId] = gen.DisplayName ?? gen.GeneratorId;

                    // 兼容：历史记录中的 modelVersion 可能直接存的是 GeneratorId
                    if (!_baseGeneratorByModelVersionIndex.ContainsKey(gen.GeneratorId))
                        _baseGeneratorByModelVersionIndex[gen.GeneratorId] = gen;
                }
            }

            var configs = ConfigManager.GetGenerators(configType);
            if (configs == null)
                return;

            foreach (var genConfig in configs)
            {
                if (genConfig == null || string.IsNullOrEmpty(genConfig.id))
                    continue;

                _baseGeneratorConfigById[genConfig.id] = genConfig;
                _baseGeneratorParametersById[genConfig.id] = genConfig.parameters;
                if (!_baseModelVersionDisplayLabelIndex.ContainsKey(genConfig.id))
                    _baseModelVersionDisplayLabelIndex[genConfig.id] = genConfig.displayName ?? genConfig.id;

                if (genConfig.parameters == null)
                    continue;

                foreach (var param in genConfig.parameters)
                {
                    if (param == null || param.id != "modelVersion" || param.options == null)
                        continue;

                    foreach (var opt in param.options)
                    {
                        if (opt == null || string.IsNullOrEmpty(opt.value))
                            continue;
                        if (!_baseModelVersionDisplayLabelIndex.ContainsKey(opt.value))
                            _baseModelVersionDisplayLabelIndex[opt.value] = genConfig.displayName ?? genConfig.id;

                        // 同一个 modelVersion 可能被多个参数定义覆盖；这里按第一个命中的生成器实例保留
                        if (!_baseGeneratorByModelVersionIndex.ContainsKey(opt.value) &&
                            _baseGeneratorById.TryGetValue(genConfig.id, out var genInstance) &&
                            genInstance != null)
                        {
                            _baseGeneratorByModelVersionIndex[opt.value] = genInstance;
                        }
                    }
                }
            }
        }

        protected bool TryGetGeneratorIndex(string generatorId, out int index)
            => _baseGeneratorIndexById.TryGetValue(generatorId ?? string.Empty, out index);

        protected GeneratorConfig GetGeneratorConfigFromIndex(string generatorId)
        {
            if (string.IsNullOrEmpty(generatorId))
                return null;
            _baseGeneratorConfigById.TryGetValue(generatorId, out var config);
            return config;
        }

        protected GeneratorConfig GetCurrentGeneratorConfig()
            => _currentGenerator == null ? null : GetGeneratorConfigFromIndex(_currentGenerator.GeneratorId);

        protected static string ResolveTextInputLabel(UILayoutConfig uiLayout)
            => TJGeneratorsL10n.L(string.IsNullOrEmpty(uiLayout?.textInputLabel) ? "文本提示词" : uiLayout.textInputLabel);

        protected static string ResolveTextInputPlaceholder(UILayoutConfig uiLayout)
            => TJGeneratorsL10n.L(string.IsNullOrEmpty(uiLayout?.textInputPlaceholder)
                ? "在此处输入文本提示..."
                : uiLayout.textInputPlaceholder);

        protected static string ResolveImageUploadLabel(UILayoutConfig uiLayout)
            => TJGeneratorsL10n.L(string.IsNullOrEmpty(uiLayout?.imageUploadLabel) ? "参考图片（可选）" : uiLayout.imageUploadLabel);

        protected static string ResolveAdvancedLabel(UILayoutConfig uiLayout)
            => TJGeneratorsL10n.L(string.IsNullOrEmpty(uiLayout?.advancedLabel) ? "高级设置" : uiLayout.advancedLabel);

        protected static bool ShouldShowTextInput(GeneratorConfig config)
            => config?.uiLayout == null || config.uiLayout.showTextInput;

        protected static bool ShouldShowImageUpload(GeneratorConfig config)
            => config?.uiLayout == null || config.uiLayout.showImageUpload;

        /// <summary>
        /// 当前生成器禁用参考图上传时，清空列表中的参考图。
        /// </summary>
        protected void ClearReferenceImagesWhenUploadHidden(
            GeneratorConfig config,
            List<string> referenceImagePaths,
            List<Texture2D> referenceUploadedImages)
        {
            if (ShouldShowImageUpload(config))
                return;
            if (referenceImagePaths == null || referenceUploadedImages == null)
                return;
            if (referenceImagePaths.Count == 0 && referenceUploadedImages.Count == 0)
                return;

            UploadImageComponents.ClearReferenceImages(referenceImagePaths, referenceUploadedImages);
        }

        /// <summary>
        /// 当前生成器禁用参考图上传时，清空单张参考图。
        /// </summary>
        protected void ClearSingleReferenceImageWhenUploadHidden(
            GeneratorConfig config,
            ref string imagePath,
            ref Texture2D uploadedImage)
        {
            if (ShouldShowImageUpload(config))
                return;
            if (string.IsNullOrEmpty(imagePath) && uploadedImage == null)
                return;

            UploadImageComponents.ClearSingleReferenceImage(ref imagePath, ref uploadedImage);
        }

        /// <summary>
        /// 按当前生成器 uiLayout 绘制文本提示词输入区；showTextInput 为 false 时不绘制并返回空字符串。
        /// </summary>
        protected string DrawConfiguredTextPromptInput(
            string prompt,
            string controlName,
            GeneratorConfig config = null)
        {
            if (config == null)
                config = GetCurrentGeneratorConfig();
            if (!ShouldShowTextInput(config))
                return string.Empty;

            var uiLayout = config?.uiLayout;
            UIComponents.DrawSectionTitle(ResolveTextInputLabel(uiLayout), uppercase: false);
            GUILayout.Space(CommonStyles.Space2);
            return UIComponents.DrawPromptInputBox(
                prompt,
                ResolveTextInputPlaceholder(uiLayout),
                controlName,
                GetPromptMaxLength());
        }

        /// <summary>
        /// 根据当前生成器 ID 返回 prompt 最大字符数（与后端 binding max 对齐）。
        /// 返回 0 表示不限制。
        /// </summary>
        protected virtual int GetPromptMaxLength()
        {
            return TJGeneratorsPromptLimits.GetMaxLength(_currentGenerator?.GeneratorId);
        }

        protected bool DrawConfiguredAdvancedSettingsFoldout(
            bool expanded,
            IGeneratorParameterProvider provider,
            List<ParameterConfig> parameters,
            GeneratorConfig config = null)
        {
            if (config == null)
                config = GetCurrentGeneratorConfig();
            return UIComponents.DrawAdvancedSettingsFoldout(
                expanded,
                provider,
                parameters,
                ResolveAdvancedLabel(config?.uiLayout));
        }

        protected int GetMaxReferenceImages(GeneratorConfig genConfig = null)
        {
            if (genConfig == null)
                genConfig = GetCurrentGeneratorConfig();
            return ConfigManager.ResolveMaxReferenceImages(WindowConfigType, genConfig);
        }

        protected void DrawConfiguredReferenceImageUpload(
            List<string> referenceImagePaths,
            List<Texture2D> referenceUploadedImages,
            string scrollStateKey)
        {
            if (referenceImagePaths == null || referenceUploadedImages == null)
                return;

            var genConfig = GetCurrentGeneratorConfig();
            if (!ShouldShowImageUpload(genConfig))
                return;
            int maxReferenceImages = GetMaxReferenceImages(genConfig);
            UploadImageComponents.TrimReferenceImagesToMax(
                referenceImagePaths,
                referenceUploadedImages,
                maxReferenceImages);

            string title = ResolveImageUploadLabel(genConfig?.uiLayout);

            UIComponents.DrawReferenceImageSectionTitle(
                title,
                referenceImagePaths.Count,
                maxReferenceImages);
            GUILayout.Space(CommonStyles.Space2);

            Action onAIGenClicked = () => ShowAIReferenceImagePicker(
                referenceImagePaths,
                referenceUploadedImages,
                maxReferenceImages);

            if (maxReferenceImages <= 1)
            {
                string singlePath = referenceImagePaths.Count > 0 ? referenceImagePaths[0] : "";
                Texture2D singleTex = referenceUploadedImages.Count > 0 ? referenceUploadedImages[0] : null;
                UploadImageComponents.DrawLargeImageUpload(
                    ref singlePath,
                    ref singleTex,
                    onAIGenClicked,
                    Repaint,
                    onPickDone: (path, tex) =>
                        UploadImageComponents.ApplySingleReferencePickResult(
                            referenceImagePaths, referenceUploadedImages, path, tex));

                referenceImagePaths.Clear();
                referenceUploadedImages.Clear();
                if (!string.IsNullOrEmpty(singlePath))
                {
                    referenceImagePaths.Add(singlePath);
                    if (singleTex != null)
                        referenceUploadedImages.Add(singleTex);
                }
                return;
            }

            UploadImageComponents.DrawLargeMultiImageUpload(
                referenceImagePaths,
                referenceUploadedImages,
                maxReferenceImages,
                Repaint,
                scrollStateKey,
                onAIGenClicked);
        }

        protected void ShowAIReferenceImagePicker(
            List<string> referenceImagePaths,
            List<Texture2D> referenceUploadedImages,
            int maxReferenceImages)
        {
            global::TJGenerators.AIReferenceImageWindow.Show((path, texture) =>
            {
                UploadImageComponents.TryAddReferenceImageFromAiResult(
                    referenceImagePaths,
                    referenceUploadedImages,
                    maxReferenceImages,
                    path,
                    texture,
                    Repaint);
            });
        }

        protected List<ParameterConfig> GetCurrentGeneratorParameters()
        {
            if (_currentGenerator == null)
                return null;
            _baseGeneratorParametersById.TryGetValue(_currentGenerator.GeneratorId, out var parameters);
            return parameters;
        }

        protected string GetModelDisplayLabelFromIndex(string modelVersion, string unknown = "未知模型")
        {
            if (string.IsNullOrEmpty(modelVersion))
                return TJGeneratorsL10n.L(unknown);
            if (_baseModelVersionDisplayLabelIndex.TryGetValue(modelVersion, out var displayName) &&
                !string.IsNullOrEmpty(displayName))
            {
                return TJGeneratorsL10n.L(displayName);
            }
            return TJGeneratorsL10n.L(modelVersion);
        }

        protected bool TryGetGeneratorByModelVersion(string modelVersion, out ModelGeneratorBase generator)
        {
            return _baseGeneratorByModelVersionIndex.TryGetValue(modelVersion ?? string.Empty, out generator);
        }

        private void EnsurePreviewCachesAllocated()
        {
            if (historyPreviewCache == null)
                historyPreviewCache = new Dictionary<string, Texture2D>();
            if (urlPreviewCache == null)
                urlPreviewCache = new Dictionary<string, Texture2D>();
            if (urlPreviewLoading == null)
                urlPreviewLoading = new HashSet<string>();
            if (urlPreviewFailed == null)
                urlPreviewFailed = new HashSet<string>();
            if (_previewLoadQueue == null)
                _previewLoadQueue = new Queue<PreviewLoadRequest>();
            if (_previewLoadPending == null)
                _previewLoadPending = new HashSet<string>();
        }

        /// <summary>
        /// 清理预览缓存。子类可在 OnDisable 或需要刷新时调用。
        /// </summary>
        protected virtual void ClearPreviewCaches()
        {
            EnsurePreviewCachesAllocated();

            // 注意：这些缓存里可能存的是运行时创建的 Texture2D（通过 new Texture2D + LoadImage）。
            // Unity 对运行时纹理不会被 AssetDatabase 管理，因此需要在清理时显式 DestroyImmediate，避免内存/显存持续增长。
            foreach (var kv in historyPreviewCache)
            {
                var texture = kv.Value;
                if (texture == null)
                    continue;

                // 若没有对应的 AssetDatabase 路径，视为运行时纹理；否则保留（交给 Unity/AssetDatabase 生命周期管理）。
                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(texture)))
                {
                    DestroyImmediate(texture);
                }
            }

            foreach (var kv in urlPreviewCache)
            {
                var texture = kv.Value;
                if (texture == null)
                    continue;

                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(texture)))
                {
                    DestroyImmediate(texture);
                }
            }

            // DestroyImmediate 之后再 Clear，避免先丢引用导致无法正确销毁。
            historyPreviewCache.Clear();
            urlPreviewCache.Clear();
            urlPreviewLoading.Clear();
            urlPreviewFailed.Clear();

            // 清理预览加载队列
            _previewLoadQueue.Clear();
            _previewLoadPending.Clear();
            _isPreviewLoaderRunning = false;
        }

        /// <summary>
        /// 将预览图加载请求加入队列，异步加载以避免在OnGUI中执行IO操作导致卡顿。
        /// </summary>
        /// <param name="cacheKey">缓存键（用于存储到字典）</param>
        /// <param name="filePath">文件的绝对路径</param>
        /// <param name="cacheToUrlPreview">true: 存入urlPreviewCache; false: 存入historyPreviewCache</param>
        protected void EnqueuePreviewLoad(string cacheKey, string filePath, bool cacheToUrlPreview)
        {
            if (string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(filePath))
                return;

            EnsurePreviewCachesAllocated();

            if (_previewLoadPending.Contains(cacheKey))
                return;

            _previewLoadPending.Add(cacheKey);
            _previewLoadQueue.Enqueue(new PreviewLoadRequest
            {
                cacheKey = cacheKey,
                filePath = filePath,
                cacheToUrlPreview = cacheToUrlPreview
            });

            if (!_isPreviewLoaderRunning)
            {
                _isPreviewLoaderRunning = true;
                EditorCoroutineUtility.StartCoroutineOwnerless(ProcessPreviewLoadQueue());
            }
        }

        /// <summary>
        /// 处理预览加载队列的协程，逐个加载文件并创建纹理。
        /// </summary>
        private IEnumerator ProcessPreviewLoadQueue()
        {
            EnsurePreviewCachesAllocated();
            while (_previewLoadQueue.Count > 0)
            {
                var request = _previewLoadQueue.Dequeue();
                byte[] imageData = null;

                try
                {
                    if (File.Exists(request.filePath))
                    {
                        // 将 IO 移出 OnGUI，避免 UI 热路径卡顿。
                        imageData = File.ReadAllBytes(request.filePath);
                    }
                }
                catch (Exception e)
                {
                    TJLog.LogWarning($"{LogTag} 加载历史预览失败: {request.filePath}, 错误: {e.Message}");
                }
                finally
                {
                    _previewLoadPending.Remove(request.cacheKey);
                }

                if (imageData != null && imageData.Length > 0)
                {
                    yield return null;

                    var texture = new Texture2D(2, 2);
                    if (texture.LoadImage(imageData))
                    {
                        if (request.cacheToUrlPreview)
                            urlPreviewCache[request.cacheKey] = texture;
                        else
                            historyPreviewCache[request.cacheKey] = texture;
                        Repaint();
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                }

                yield return null;
            }

            _isPreviewLoaderRunning = false;
        }

        /// <summary>
        /// 通用的 OpenForAsset 模板：校验路径、查重、创建窗口、设置资产、加入字典。
        /// </summary>
        /// <param name="assetPath">资产路径，为空时调用 onEmptyPath</param>
        /// <param name="openWindows">已打开窗口字典</param>
        /// <param name="logTag">日志标签</param>
        /// <param name="titleFormat">标题格式，如 "文生音频 - {0}"，{0} 为文件名</param>
        /// <param name="getOrCreateWindow">获取或创建窗口实例</param>
        /// <param name="setTarget">设置目标资产到窗口</param>
        /// <param name="onEmptyPath">assetPath 为空时的回调（如 ShowWindow）</param>
        public static void OpenForAsset<TWindow>(
            string assetPath,
            Dictionary<string, TWindow> openWindows,
            string logTag,
            string titleFormat,
            Func<TWindow> getOrCreateWindow,
            Action<TWindow, TJGeneratorsAssetReference> setTarget,
            Action onEmptyPath = null
        )
            where TWindow : GenerationWindowBase
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                onEmptyPath?.Invoke();
                return;
            }

            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                TJLog.LogError($"{logTag} 无效的资产路径: {assetPath}");
                return;
            }

            if (openWindows.TryGetValue(guid, out var existing) && existing != null)
            {
                existing.Focus();
                return;
            }

            var window = getOrCreateWindow();
            var assetRef = TJGeneratorsAssetReference.FromPath(assetPath);
            setTarget(window, assetRef);
            // CreateInstance triggers OnEnable/Bootstrap before setTarget runs, so the
            // target asset is null at that point and history loads empty. Reload now that
            // the target is assigned.
            window.RefreshHistory();
            window.titleContent = new GUIContent(
                string.Format(titleFormat, Path.GetFileNameWithoutExtension(assetPath))
            );
            SetDefaultWindowSize(window);
            window.Show();
            openWindows[guid] = window;
        }

        /// <summary>
        /// 在 Show 后再次应用已知矩形，避免首帧 OnGUI 改 minSize 导致窗口偏移。
        /// 不增加堆叠偏移计数（矩形已由 GetDefaultMainWindowRect 计算）。
        /// </summary>
        protected static void FinalizeMainWindowShow<TWindow>(TWindow window, Rect rect)
            where TWindow : EditorWindow
        {
            ScheduleWindowPositionReapply(window, rect);
        }

        private static void ScheduleWindowPositionReapply(EditorWindow window, Rect rect)
        {
            window.position = rect;
            Rect capturedPos = rect;
            EditorApplication.delayCall += () =>
            {
                if (window != null)
                    window.position = capturedPos;
            };
        }

        protected static Rect GetDefaultMainWindowRect()
        {
            return UIComponents.GetDefaultMainWindowRect();
        }

        /// <summary>
        /// 设置默认窗口尺寸，并居中显示；每新开一个主窗口在中心基础上叠加 (24, 24) 偏移。
        /// 使用 delayCall 在下一帧再次应用，避免 Unity 在 Show() 后覆盖 position。
        /// </summary>
        protected static void SetDefaultWindowSize(EditorWindow window)
        {
            const float designStackOffset = 24f;

            var pos = window.position;
            if (pos.width < CommonStyles.LayoutSwitchThreshold)
                pos.width = UIComponents.DefaultMainWindowWidth;
            if (pos.height < UIComponents.DefaultMainWindowHeight)
                pos.height = UIComponents.DefaultMainWindowHeight;

            Rect rect = UIComponents.GetDefaultMainWindowRect(pos.width, pos.height, designStackOffset);
            ScheduleWindowPositionReapply(window, rect);
        }
    }
}
#endif
