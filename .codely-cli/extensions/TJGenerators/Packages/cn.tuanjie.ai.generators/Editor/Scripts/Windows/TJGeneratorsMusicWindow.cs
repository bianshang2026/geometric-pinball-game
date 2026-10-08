#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
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
    /// TJGenerators 文生音频窗口 - 使用 sonilo-music 等生成器生成音频
    /// </summary>
    public class TJGeneratorsMusicWindow : GenerationWindowBase, IGenerationPipelineHost, IGenerationTriggerHost, IMediaAssetPipelineHost
    {
        // ========== 基类抽象属性实现 ==========
        protected override ConfigType WindowConfigType => ConfigType.Music;
        protected override string LogTag => "[TJGeneratorsMusic]";

        // ========== 窗口特定字段 ==========
        private string textPrompt = "";
        private int _previewPlayingHistoryIndex = -1;
        private double _lastProgressRepaintTime;
        private readonly Dictionary<string, UnityEngine.AudioClip> _clipCache = new Dictionary<string, UnityEngine.AudioClip>();
        private readonly Dictionary<string, float[]> _waveformCache = new Dictionary<string, float[]>();
        private const int WaveformCacheBars = 256;

        [SerializeField]
        private TJGeneratorsAssetReference targetAudioAsset;

        private static System.Collections.Generic.Dictionary<string, TJGeneratorsMusicWindow> s_musicOpenWindows = new System.Collections.Generic.Dictionary<string, TJGeneratorsMusicWindow>();

        public static void ShowWindow()
        {
            var rect = GetDefaultMainWindowRect();
            var window = GetWindowWithRect<TJGeneratorsMusicWindow>(
                rect,
                utility: false,
                title: TJGeneratorsL10n.L("TJGenerators 音频生成"),
                focus: true
            );
            window.titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 音频生成"));
            FinalizeMainWindowShow(window, rect);
        }

        /// <summary>
        /// 从指定音频资产路径打开文生音频窗口（如通过 Assets/Create 创建后打开）。
        /// 生成时将把结果写入该资产路径。
        /// </summary>
        public static void OpenForAsset(string assetPath)
        {
            GenerationWindowBase.OpenForAsset(
                assetPath,
                s_musicOpenWindows,
                "[TJGeneratorsMusic]",
                TJGeneratorsL10n.L("TJGenerators 音频 - {0}"),
                () =>
                {
                    var window = CreateInstance<TJGeneratorsMusicWindow>();
                    return window;
                },
                (w, r) => w.targetAudioAsset = r,
                ShowWindow);
        }

        protected override void OnBootstrapWindowContent()
        {
            if (targetAudioAsset != null && !string.IsNullOrEmpty(targetAudioAsset.guid))
                s_musicOpenWindows[targetAudioAsset.guid] = this;

            InitializeGeneratorsFromConfig(ConfigType.Music);
            OnRefreshWindowContent();
        }

        protected override void OnRefreshWindowContent()
        {
            RefreshHistory();
            CheckAndRecoverInterruptedTasks();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            wantsMouseMove = false;
            EditorApplication.update -= OnPreviewUpdate;

            if (_previewPlayingHistoryIndex >= 0)
            {
                StopPreviewClip();
                _previewPlayingHistoryIndex = -1;
            }

            // 只释放引用：AudioClip 由 AssetDatabase 管理，不在这里 DestroyImmediate
            _clipCache.Clear();
            _waveformCache.Clear();

            if (targetAudioAsset != null && !string.IsNullOrEmpty(targetAudioAsset.guid))
                s_musicOpenWindows.Remove(targetAudioAsset.guid);
        }

        private void OnPreviewUpdate()
        {
            if (_previewPlayingHistoryIndex < 0) return;
            if (!IsPreviewClipPlaying())
            {
                StopPreviewClip();
                _previewPlayingHistoryIndex = -1;
                EditorApplication.update -= OnPreviewUpdate;
            }
            Repaint();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            wantsMouseMove = true;

            EditorCoroutineUtility.StartCoroutineOwnerless(UserInfoHelper.GetUserInfoCoroutine(ConfigManager.GetUserInfoUrl(), OnUserInfoLoaded));
        }

        private string GetCurrentMusicAssetGuid() => targetAudioAsset?.guid ?? "";

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
                EditorGUILayout.HelpBox(TJGeneratorsL10n.L("未找到可用的文生音频生成器，请检查 GeneratorConfig.json 中的 musicGenerators"), MessageType.Error);
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
                        TJGeneratorsL10n.L("目标音频"),
                        DrawTargetHeaderContentRect,
                        SelectTargetAudioAsset
                    );
                    GUILayout.Space(CommonStyles.Space2);
                    UIComponents.DrawModelSelector(
                        currentSelectedModel?.Name ?? _currentGenerator?.DisplayName ?? TJGeneratorsL10n.L("未选择"),
                        currentSelectedModel,
                        OnModelSelected,
                        ConfigType.Music
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
            if (HasValidTargetAudioAsset())
            {
                string name = Path.GetFileNameWithoutExtension(targetAudioAsset.GetPath());
                if (GUI.Button(rect, name, CommonStyles.TargetPrefabNameStyle))
                    SelectTargetAudioAsset();
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            }
            else
            {
                GUI.Label(rect, TJGeneratorsL10n.L("未绑定（生成时自动创建）"), CommonStyles.ContentStyle);
            }
        }

        private void SelectTargetAudioAsset()
        {
            if (!HasValidTargetAudioAsset())
                return;
            var clip = AssetDatabase.LoadAssetAtPath<UnityEngine.AudioClip>(targetAudioAsset.GetPath());
            if (clip != null)
            {
                EditorGUIUtility.PingObject(clip);
                Selection.activeObject = clip;
            }
        }

        private void OnModelSelected(AIModelInfo model) => OnModelSelectedBase(model);

        protected override void ResetInputStateAfterModelChange()
        {
            var config = GetCurrentGeneratorConfig();
            ResetTextPromptIfHidden(config, ref textPrompt);
        }

        private void DrawInputSection()
        {
            UIComponents.DrawSectionTitle(TJGeneratorsL10n.L("文本提示词"), uppercase: false);
            GUILayout.Space(CommonStyles.Space2);
            int maxLength = GetPromptMaxLength();
            textPrompt = UIComponents.DrawPromptInputBox(
                textPrompt,
                TJGeneratorsL10n.L("描述你想要生成的音乐风格、情绪或场景..."),
                "music_prompt_input",
                maxLength
            );
        }

        private void DrawConfigurationSection()
        {
            var provider = _currentGenerator as IGeneratorParameterProvider;
            List<ParameterConfig> parameters = GetCurrentGeneratorParameters();

            showAdvancedSettings = DrawConfiguredAdvancedSettingsFoldout(
                showAdvancedSettings,
                provider,
                parameters);
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

        private bool HasValidHistorySelection()
        {
            return selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count;
        }

        private bool HasValidTargetAudioAsset()
        {
            return targetAudioAsset != null && targetAudioAsset.IsValid();
        }

        private void DrawHistoryPanel(float panelWidth)
        {
            float historyPanelHeight = isVerticalLayout ? position.height * 0.45f : position.height;

            UIComponents.BeginHistoryPanel(panelWidth, historyPanelHeight, isVerticalLayout);

            bool shouldShowPreview = false;
            if (HasValidHistorySelection())
            {
                var selectedItem = generationHistory[selectedHistoryIndex];
                if (!selectedItem.isGenerating && !string.IsNullOrEmpty(selectedItem.modelPath))
                {
                    var clip = GetOrLoadClip(selectedItem.modelPath);
                    if (clip != null)
                    {
                        shouldShowPreview = true;
                        float maxPreviewWidth = CommonStyles.HistoryPanelInnerWidth(panelWidth);
                        float previewWidth = Mathf.Min(maxPreviewWidth, 300f);
                        float previewHeight = 60f;
                        var previewRect = GUILayoutUtility.GetRect(previewWidth, previewHeight);
                        DrawWaveformPreview(previewRect, clip, selectedItem.modelPath);
                        if (_previewPlayingHistoryIndex == selectedHistoryIndex && clip.length > 0f)
                        {
                            float pos = GetPreviewClipPosition();
                            float progress = pos >= 0f ? Mathf.Clamp01(pos / clip.length) : 0f;
                            float x = previewRect.x + progress * previewRect.width;
                            EditorGUI.DrawRect(new Rect(x - 1f, previewRect.y, 2f, previewRect.height), Color.white);
                        }
                        GUILayout.Space(5);
                    }
                }
            }

            float previewHeight2 = shouldShowPreview ? 70f : 0f;
            float scrollHeight = historyPanelHeight - previewHeight2 - 100f;
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
                for (int i = 0; i < generationHistory.Count; i++)
                {
                    if (i > 0)
                        GUILayout.Space(6f);
                    var item = generationHistory[i];
                    bool isSelected = selectedHistoryIndex == i;
                    GUILayout.BeginHorizontal(GUIStyle.none, GUILayout.Height(60));
                    if (item.isGenerating)
                    {
                        Rect rowRect = GUILayoutUtility.GetRect(0, 60);
                        if (Event.current.type == EventType.Repaint)
                        {
                            EditorGUI.DrawRect(rowRect, new Color(0.1f, 0.1f, 0.1f, 1f));
                            float iconH = 28f;
                            var iconRect = new Rect(rowRect.x + rowRect.width / 4, rowRect.y + (rowRect.height - iconH) / 2f, rowRect.width / 2, iconH);
                            var spinIcon = EditorGUIUtility.IconContent("Loading");
                            if (spinIcon != null && spinIcon.image != null)
                            {
                                float angle = (float)(EditorApplication.timeSinceStartup * 180) % 360f;
                                Matrix4x4 matrixBackup = GUI.matrix;
                                GUIUtility.RotateAroundPivot(angle, iconRect.center);
                                GUI.DrawTexture(iconRect, spinIcon.image, ScaleMode.ScaleToFit);
                                GUI.matrix = matrixBackup;
                            }
                            else
                            {
                                GUI.Label(rowRect, TJGeneratorsL10n.L("生成中..."), CommonStyles.CenteredGreyMiniLabelStyleSmall);
                            }
                        }
                        Repaint();
                    }
                    else
                    {
                        var clip = GetOrLoadClip(item.modelPath);
                        Rect playRect = GUILayoutUtility.GetRect(24, 60, GUILayout.Width(28));
                        Rect previewRect = GUILayoutUtility.GetRect(0, 60, GUILayout.ExpandWidth(true));
                        if (Event.current.type == EventType.Repaint)
                            DrawWaveformPreview(previewRect, clip, item.modelPath);
                        if (Event.current.type == EventType.Repaint && _previewPlayingHistoryIndex == i && clip != null && clip.length > 0f)
                        {
                            float position = GetPreviewClipPosition();
                            float progress = position >= 0f ? Mathf.Clamp01(position / clip.length) : 0f;
                            float x = previewRect.x + progress * previewRect.width;
                            float lineW = 2f;
                            EditorGUI.DrawRect(new Rect(x - lineW * 0.5f, previewRect.y, lineW, previewRect.height), Color.white);
                        }
                        string tooltipText = (string.IsNullOrEmpty(item.prompt) ? "" : item.prompt + "\n") + TJGeneratorsL10n.L("模型: ") + GetMusicModelDisplayLabel(item.modelVersion);
                        GUI.Label(previewRect, new GUIContent("", tooltipText));
                        if (Event.current.type == EventType.Repaint && isSelected)
                        {
                            UIComponents.DrawRectOutline(previewRect, CommonStyles.ThemeDarkGreenColor, 2f);
                        }
                        GUIContent playIcon = EditorGUIUtility.IconContent(_previewPlayingHistoryIndex == i ? "PauseButton" : "PlayButton");
                        if (playIcon == null) playIcon = new GUIContent(_previewPlayingHistoryIndex == i ? "■" : "▶");
                        if (GUI.Button(playRect, playIcon))
                        {
                            if (_previewPlayingHistoryIndex == i)
                            {
                                StopPreviewClip();
                                _previewPlayingHistoryIndex = -1;
                                EditorApplication.update -= OnPreviewUpdate;
                            }
                            else
                            {
                                StopPreviewClip();
                                EditorApplication.update -= OnPreviewUpdate;
                                if (clip != null)
                                {
                                    PlayPreviewClip(clip);
                                    _previewPlayingHistoryIndex = i;
                                    EditorApplication.update += OnPreviewUpdate;
                                }
                            }
                            Event.current.Use();
                            Repaint();
                        }
                        if (Event.current.type == EventType.MouseDown && previewRect.Contains(Event.current.mousePosition))
                        {
                            selectedHistoryIndex = i;
                            Event.current.Use();
                            Repaint();
                        }
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();
            DrawHistoryActions();
            UIComponents.EndHistoryPanel();
        }

        private UnityEngine.AudioClip GetOrLoadClip(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath)) return null;
            if (_clipCache.TryGetValue(modelPath, out var cached)) return cached;
            var clip = AssetDatabase.LoadAssetAtPath<UnityEngine.AudioClip>(modelPath);
            if (clip != null) _clipCache[modelPath] = clip;
            return clip;
        }

        /// <summary>
        /// 绘制纯波形预览（深灰底、浅橙色波形）。波形数据按 modelPath 缓存，避免每帧 GetData。
        /// </summary>
        private void DrawWaveformPreview(Rect rect, UnityEngine.AudioClip clip, string modelPath)
        {
            EditorGUI.DrawRect(rect, new Color(0.11f, 0.11f, 0.11f, 1f));
            if (clip == null) return;
            try
            {
                if (!_waveformCache.TryGetValue(modelPath, out float[] bars))
                {
                    int totalSamples = clip.samples * clip.channels;
                    if (totalSamples <= 0) return;
                    float[] samples = new float[totalSamples];
                    clip.GetData(samples, 0);
                    int pack = Mathf.Max(1, totalSamples / WaveformCacheBars);
                    bars = new float[WaveformCacheBars];
                    for (int x = 0; x < WaveformCacheBars; x++)
                    {
                        int start = x * pack;
                        int end = Mathf.Min(start + pack, totalSamples);
                        float maxA = 0f;
                        for (int i = start; i < end; i++)
                            maxA = Mathf.Max(maxA, Mathf.Abs(samples[i]));
                        bars[x] = maxA;
                    }
                    _waveformCache[modelPath] = bars;
                }
                int w = Mathf.Max(1, (int)rect.width);
                float centerY = rect.y + rect.height * 0.5f;
                float halfH = rect.height * 0.4f;
                int nBars = bars.Length;
                for (int x = 0; x < w; x++)
                {
                    int idx = (int)((long)x * nBars / w) % nBars;
                    float maxA = bars[idx];
                    float barH = maxA * halfH;
                    if (barH < 1f) barH = 1f;
                    Rect bar = new Rect(rect.x + x, centerY - barH * 0.5f, 1f, barH);
                    EditorGUI.DrawRect(bar, CommonStyles.ThemeOrangeColor);
                }
            }
            catch
            {
                GUI.Label(rect, TJGeneratorsL10n.L("无波形预览"), CommonStyles.SmallGreyCenterLabelStyle);
            }
        }

        private string GetMusicModelDisplayLabel(string modelVersion)
        {
            return GetModelDisplayLabelFromIndex(modelVersion);
        }

        private static void PlayPreviewClip(UnityEngine.AudioClip clip)
        {
            if (clip == null) return;
            try
            {
                var asm = typeof(UnityEditor.AssetDatabase).Assembly;
                var audioUtil = asm.GetType("UnityEditor.AudioUtil");
                if (audioUtil == null) return;
                // Unity: PlayPreviewClip(AudioClip clip, int startSample = 0, bool loop = false)
                var method = audioUtil.GetMethod("PlayPreviewClip", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(UnityEngine.AudioClip), typeof(int), typeof(bool) }, null);
                if (method != null)
                    method.Invoke(null, new object[] { clip, 0, false });
                else
                {
                    method = audioUtil.GetMethod("PlayPreviewClip", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(UnityEngine.AudioClip), typeof(int) }, null);
                    if (method != null)
                        method.Invoke(null, new object[] { clip, 0 });
                    else
                    {
                        method = audioUtil.GetMethod("PlayPreviewClip", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(UnityEngine.AudioClip) }, null);
                        if (method != null)
                            method.Invoke(null, new object[] { clip });
                    }
                }
            }
            catch (Exception ex)
            {
                TJLog.LogWarning("[TJGeneratorsMusic] PlayPreviewClip: " + ex.Message);
            }
        }

        private static void StopPreviewClip()
        {
            try
            {
                var asm = typeof(UnityEditor.AssetDatabase).Assembly;
                var audioUtil = asm.GetType("UnityEditor.AudioUtil");
                if (audioUtil == null) return;
                var method = audioUtil.GetMethod("StopAllPreviewClips", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null)
                    method.Invoke(null, null);
            }
            catch (Exception ex)
            {
                TJLog.LogWarning("[TJGeneratorsMusic] StopPreviewClip: " + ex.Message);
            }
        }

        /// <summary>反射调用 AudioUtil.IsPreviewClipPlaying()，无则返回 false。</summary>
        private static bool IsPreviewClipPlaying()
        {
            try
            {
                var asm = typeof(UnityEditor.AssetDatabase).Assembly;
                var audioUtil = asm.GetType("UnityEditor.AudioUtil");
                if (audioUtil == null) return false;
                var method = audioUtil.GetMethod("IsPreviewClipPlaying", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null && method.Invoke(null, null) is bool playing)
                    return playing;
            }
            catch { }
            return false;
        }

        /// <summary>反射调用 AudioUtil.GetPreviewClipPosition()（秒），无则返回 -1f。</summary>
        private static float GetPreviewClipPosition()
        {
            try
            {
                var asm = typeof(UnityEditor.AssetDatabase).Assembly;
                var audioUtil = asm.GetType("UnityEditor.AudioUtil");
                if (audioUtil == null) return -1f;
                var method = audioUtil.GetMethod("GetPreviewClipPosition", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method != null && method.Invoke(null, null) is float pos)
                    return pos;
            }
            catch { }
            return -1f;
        }

        private void DrawHistoryActions()
        {
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            bool hasSelection = HasValidHistorySelection();
            bool isGenerating = hasSelection && generationHistory[selectedHistoryIndex].isGenerating;
            GUI.enabled = hasSelection && !isGenerating && HasValidTargetAudioAsset();
            if (GUILayout.Button(TJGeneratorsL10n.L("应用到当前音频"), GUILayout.Height(25)))
                ApplyHistoryToCurrentAudio(selectedHistoryIndex);
            GUI.enabled = hasSelection && !isGenerating;
            if (GUILayout.Button(TJGeneratorsL10n.L("在项目中显示音频"), GUILayout.Height(25)))
                ShowHistoryInProject(selectedHistoryIndex);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
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

        private void ApplyHistoryToCurrentAudio(int index)
        {
            if (index < 0 || index >= generationHistory.Count) return;
            var item = generationHistory[index];
            if (item.isGenerating)
            {
                Debug.LogWarning($"{LogTag} {TJGeneratorsL10n.L("请等待该条生成完成后再应用。")}");
                return;
            }
            string histAbsEarly = PathUtils.ToAbsoluteAssetPath(item.modelPath);
            if (
                string.IsNullOrEmpty(item.modelPath)
                || string.IsNullOrEmpty(histAbsEarly)
                || !File.Exists(histAbsEarly)
            )
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("音频文件不存在，可能已被删除。"), LogTag);
                TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                generationHistory = TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentMusicAssetGuid());
                Repaint();
                return;
            }
            if (!HasValidTargetAudioAsset())
            {
                Debug.LogWarning($"{LogTag} {TJGeneratorsL10n.L("请先创建或选择目标音频资产。")}");
                return;
            }
            string ext =
                string.IsNullOrEmpty(item.modelPath)
                    ? ".mp3"
                    : Path.GetExtension(item.modelPath);
            if (string.IsNullOrEmpty(ext))
                ext = ".mp3";
            string targetPathForDialog = Path.ChangeExtension(targetAudioAsset.GetPath(), ext);
            if (
                !EditorUtility.DisplayDialog(
                    TJGeneratorsL10n.L("确认替换"),
                    TJGeneratorsL10n.L("确定将选中的音频应用到当前目标「{0}」吗？", Path.GetFileNameWithoutExtension(targetPathForDialog)),
                    TJGeneratorsL10n.L("确定"),
                    TJGeneratorsL10n.L("取消"))
            )
                return;
            string err;
            if (!ReplaceTargetClipFromProjectSource(item.modelPath, TJGeneratorsL10n.L("已将历史音频应用到"), out err))
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), string.IsNullOrEmpty(err) ? TJGeneratorsL10n.L("应用失败（详见控制台）。") : TJGeneratorsL10n.L("应用失败: {0}", err), LogTag);
            else
            {
                RefreshHistory();
                Repaint();
            }
        }

        /// <summary>
        /// 将工程内源音频复制到当前目标资产；若扩展名变化则删除旧文件并更新 GUID / 场景中 AudioSource 引用，
        /// 与生成完成回调 <see cref="OnAssetSaved"/> 保持一致，避免同基名下残留 .wav 与 .mp3 两个文件。
        /// </summary>
        private bool ReplaceTargetClipFromProjectSource(string sourceAssetPath, string okLogVerb, out string errorMessage)
        {
            errorMessage = null;
            if (!HasValidTargetAudioAsset())
            {
                errorMessage = TJGeneratorsL10n.L("目标音频无效");
                return false;
            }

            string ext = Path.GetExtension(sourceAssetPath);
            if (string.IsNullOrEmpty(ext))
                ext = ".wav";

            string oldTargetGuid = targetAudioAsset.guid;
            string originalPath = targetAudioAsset.GetPath();
            string targetPath = Path.ChangeExtension(originalPath, ext);
            string sourceAbsolute = PathUtils.ToAbsoluteAssetPath(sourceAssetPath);
            string targetAbsolute = PathUtils.ToAbsoluteAssetPath(targetPath);

            var oldTargetClip = AssetDatabase.LoadAssetAtPath<AudioClip>(originalPath);

            try
            {
                if (string.IsNullOrEmpty(sourceAbsolute) || !File.Exists(sourceAbsolute))
                {
                    errorMessage = TJGeneratorsL10n.L("源音频文件不存在");
                    return false;
                }

                string targetDir = Path.GetDirectoryName(targetAbsolute);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);
                File.Copy(sourceAbsolute, targetAbsolute, true);
                AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                TJGeneratorsGenerationLabel.EnableLabel(targetAudioAsset);
                TJLog.Log($"[TJGeneratorsMusic] {okLogVerb} {targetPath}");

                if (!string.Equals(originalPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    string originalAbsolute = PathUtils.ToAbsoluteAssetPath(originalPath);
                    if (File.Exists(originalAbsolute))
                    {
                        AssetDatabase.DeleteAsset(originalPath);
                        TJLog.Log($"[TJGeneratorsMusic] 已删除旧占位文件: {originalPath}");
                    }

                    if (!string.IsNullOrEmpty(oldTargetGuid))
                        s_musicOpenWindows.Remove(oldTargetGuid);

                    targetAudioAsset = TJGeneratorsAssetReference.FromPath(targetPath);
                    titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 音频 - {0}", Path.GetFileNameWithoutExtension(targetPath)));
                    string newGuid = targetAudioAsset.guid;
                    if (!string.IsNullOrEmpty(newGuid))
                    {
                        s_musicOpenWindows[newGuid] = this;
                        TJGeneratorsHistoryManager.RewriteAssetGuid(oldTargetGuid, newGuid);
                    }
                }

                var newTargetClip = AssetDatabase.LoadAssetAtPath<AudioClip>(targetPath);
                if (newTargetClip != null)
                {
                    try
                    {
                        bool foundExistingSource = false;
                        foreach (var source in UnityObjectCompat.FindObjectsOfType<AudioSource>())
                        {
                            if (source.clip == null) continue;
                            bool matchByRef =
                                oldTargetClip != null && source.clip == oldTargetClip;
                            bool matchByPath = string.Equals(
                                AssetDatabase.GetAssetPath(source.clip),
                                originalPath,
                                StringComparison.OrdinalIgnoreCase);
                            if (matchByRef || matchByPath)
                            {
                                source.clip = newTargetClip;
                                source.playOnAwake = true;
                                EditorUtility.SetDirty(source);
                                TJLog.Log(
                                    $"[TJGeneratorsMusic] Updated AudioSource on '{source.gameObject.name}'.");
                                foundExistingSource = true;
                            }
                        }

                        if (!foundExistingSource)
                        {
                            var go = GameObject.Find("BGMPlayer");
                            bool isNew = go == null;
                            if (isNew)
                            {
                                go = new GameObject("BGMPlayer");
                                Undo.RegisterCreatedObjectUndo(go, "TJGenerators BGM Player");
                            }

                            var bgmSource = go.GetComponent<AudioSource>();
                            if (bgmSource == null)
                                bgmSource = Undo.AddComponent<AudioSource>(go);

                            Undo.RecordObject(bgmSource, "TJGenerators BGM Clip");
                            bgmSource.clip = newTargetClip;
                            bgmSource.loop = true;
                            bgmSource.spatialBlend = 0f;
                            bgmSource.playOnAwake = true;
                            EditorUtility.SetDirty(bgmSource);
                            EditorUtility.SetDirty(go);
                            TJLog.Log($"[TJGeneratorsMusic] Auto-created/wired BGMPlayer in active scene.");
                        }

                        UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
                    }
                    catch (Exception wireEx)
                    {
                        TJLog.LogWarning(
                            $"[TJGeneratorsMusic] 场景中更新音频引用跳过（资产已成功替换）: {wireEx.Message}");
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                errorMessage = e.Message;
                TJLog.LogWarning($"[TJGeneratorsMusic] 复制到当前音频失败: {e.Message}");
                return false;
            }
        }

        // ========== 任务恢复 ==========

        protected override string GetCurrentAssetGuid() => GetCurrentMusicAssetGuid();

        protected override void SetHistory(List<TJGeneratorsGenerationHistoryItem> history) => generationHistory = history;

        protected override void OnGeneratorRestoredFromTask(ModelGeneratorBase generator)
        {
            base.OnGeneratorRestoredFromTask(generator);
            isGenerating = true;
            generationStatus = TJGeneratorsL10n.L("恢复中...");
        }

        private void EnsureTargetMusic()
        {
            if (HasValidTargetAudioAsset()) return;
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            string audioPath = TJGeneratorsAudioAssetPathUtility.GenerateUniquePlaceholderWavPath(
                "Assets/TJGenerators/New AudioClip.wav");
            audioPath = TJGeneratorsAudioUtils.CreateBlankAudioClip(audioPath);
            if (string.IsNullOrEmpty(audioPath))
            {
                TJLog.LogError("[TJGeneratorsMusic] 无法创建音频占位资产");
                return;
            }
            targetAudioAsset = TJGeneratorsAssetReference.FromPath(audioPath);
            titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 音频 - {0}", Path.GetFileNameWithoutExtension(audioPath)));
            if (!string.IsNullOrEmpty(targetAudioAsset.guid))
                s_musicOpenWindows[targetAudioAsset.guid] = this;
            Repaint();
        }

        private void StartGeneration()
        {
            if (_currentGenerator == null || string.IsNullOrWhiteSpace(textPrompt)) return;
            EnsureTargetMusic();
            if (!HasValidTargetAudioAsset()) return;
            if (_currentGenerator is DynamicGenerator dg)
                dg.SetTextPrompt(textPrompt);
            isGenerating = true;
            generationStatus = TJGeneratorsL10n.L("准备中...");
            generationProgress = 0f;
            string assetGuid = targetAudioAsset?.guid ?? "";
            EditorCoroutineUtility.StartCoroutineOwnerless(_pipeline.StartGeneration(_currentGenerator, assetGuid));
        }

        public TJGeneratorsAssetReference GetTargetAsset() => targetAudioAsset;

        public void StartGeneration(ModelGeneratorBase generator)
        {
            if (generator == _currentGenerator)
                StartGeneration();
        }

        public override void RefreshHistory()
        {
            base.RefreshHistory();
            _clipCache.Clear();
            _waveformCache.Clear();
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
            if (_type != PipelineMediaType.Audio) return null;

            // 始终保存到唯一 History 路径，使每条历史记录有独立 modelPath，避免所有波形显示同一文件
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                AssetDatabase.CreateFolder("Assets/TJGenerators", "History");
            string ext =
                (generator is DynamicGenerator dg)
                    ? "." + TJGeneratorsAudioAssetPathUtility.NormalizeImportedAudioFileExtension(dg.AudioFormat)
                    : ".wav";
            string uniqueName = "Music_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ext;
            return TJGeneratorsAudioAssetPathUtility.GenerateUniqueAudioPath(
                "Assets/TJGenerators/History/" + uniqueName);
        }

        public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator)
        {
            if (_type != PipelineMediaType.Audio) return;

            TJLog.Log($"[TJGeneratorsMusic] OnAssetSaved: {savePath}");
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));
            if (HasValidTargetAudioAsset())
            {
                // 使用 savePath 的扩展名复制到当前目标（与 ReplaceTargetClipFromProjectSource）
                ReplaceTargetClipFromProjectSource(savePath, TJGeneratorsL10n.L("音频已复制到目标路径"), out _);
            }
            generationStatus = TJGeneratorsL10n.L("完成");
            generationProgress = 1f;
            isGenerating = false;
            RefreshHistory();
            Repaint();
        }

    }
}
#endif
