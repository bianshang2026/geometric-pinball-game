#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Codely.Newtonsoft.Json;
using TJGenerators.AssetSearch;
using TJGenerators.Config;
using TJGenerators.Pipeline;
using TJGenerators.PostProcessing;
using TJGenerators.UI;
using TJGenerators.Utils;
using Unity.UniAsset.Manager.Editor.InternalBridge;

namespace TJGenerators
{
    /// <summary>
    /// 图片切割窗口：选择一张大图，使用传统 CV 方法自动检测并切割出其中的独立元素。
    /// </summary>
    public class TJGeneratorsImageSliceWindow : EditorWindow
    {
        // ========== 状态 ==========
        private Texture2D _sourceTexture;
        private string _sourceAssetPath;
        private Texture2D _previewTexture;
        private List<ImageSlicePostProcess.SliceRegion> _detectedRegions = new List<ImageSlicePostProcess.SliceRegion>();
        private Vector2 _scrollPosition;
        private bool _isProcessing;

        // ========== 参数 ==========
        private int _bgModeIndex = 0;
        private float _alphaThreshold = 0.1f;
        private float _colorTolerance = 15f;
        private int _minRegionPixels = 100;
        private int _padding = 2;
        private bool _setAsSprite = true;

        private const string GuideUrlCn = "https://codely-docs.tuanjie.cn/ai-generation-tools/play-guide/#%E7%94%9F%E6%88%90-ui";
        private const string GuideUrlEn = "https://codely-docs.tuanjie.cn/en/ai-generation-tools/play-guide/#%E7%94%9F%E6%88%90-ui";
        private static string GuideUrl => TJGeneratorsL10n.IsEnglish ? GuideUrlEn : GuideUrlCn;

        private static string[] BgModeNames => new[]
        {
            TJGeneratorsL10n.L("自动检测"),
            TJGeneratorsL10n.L("透明背景"),
            TJGeneratorsL10n.L("纯色背景")
        };
        private static readonly ImageSlicePostProcess.BackgroundMode[] BgModeValues =
        {
            ImageSlicePostProcess.BackgroundMode.Auto,
            ImageSlicePostProcess.BackgroundMode.Transparent,
            ImageSlicePostProcess.BackgroundMode.SolidColor
        };

        private static GUIContent s_HelpButtonContent;

        private static readonly Dictionary<string, TJGeneratorsImageSliceWindow> s_OpenWindows =
            new Dictionary<string, TJGeneratorsImageSliceWindow>();

        public static void ShowWindow()
        {
            var window = GetWindow<TJGeneratorsImageSliceWindow>(TJGeneratorsL10n.L("图片切割"));
            window.minSize = new Vector2(420, 600);
            window.Show();
        }

        public static void OpenForAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                ShowWindow();
                return;
            }

            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (!string.IsNullOrEmpty(guid) && s_OpenWindows.TryGetValue(guid, out var existing) && existing != null)
            {
                existing.Focus();
                return;
            }

            var window = CreateInstance<TJGeneratorsImageSliceWindow>();
            window.titleContent = new GUIContent(string.Format(TJGeneratorsL10n.L("图片切割 - {0}"), Path.GetFileNameWithoutExtension(assetPath)));
            window.minSize = new Vector2(420, 600);
            window.SetSourceAsset(assetPath);
            window.Show();

            if (!string.IsNullOrEmpty(guid))
                s_OpenWindows[guid] = window;
        }

        // ========== 生命周期 ==========

        private void OnEnable()
        {
            _bgModeIndex = EditorPrefs.GetInt("TJGen_ImageSlice_BgMode", 0);
            _alphaThreshold = EditorPrefs.GetFloat("TJGen_ImageSlice_AlphaThreshold", 0.1f);
            _colorTolerance = EditorPrefs.GetFloat("TJGen_ImageSlice_ColorTolerance", 15f);
            _minRegionPixels = EditorPrefs.GetInt("TJGen_ImageSlice_MinRegion", 100);
            _padding = EditorPrefs.GetInt("TJGen_ImageSlice_Padding", 2);
            _setAsSprite = EditorPrefs.GetBool("TJGen_ImageSlice_SetAsSprite", true);
        }

        private void OnDestroy()
        {
            ClearPreview();
            DestroyRuntimeSourceTexture();
            foreach (var kvp in s_OpenWindows)
            {
                if (kvp.Value == this)
                {
                    s_OpenWindows.Remove(kvp.Key);
                    break;
                }
            }
        }

        /// <summary>
        /// 标题栏右上角「?」帮助图标，点击打开使用文档（与其他生成窗口一致）。
        /// </summary>
        private void ShowButton(Rect rect)
        {
            if (s_HelpButtonContent == null)
            {
                s_HelpButtonContent = EditorGUIUtility.IconContent("_Help");
                s_HelpButtonContent.tooltip = TJGeneratorsL10n.L("怎么用？查看使用文档");
            }

            if (GUI.Button(rect, s_HelpButtonContent, GUIStyle.none))
                Application.OpenURL(GuideUrl);
        }

        // ========== GUI ==========

        private void OnGUI()
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), CommonStyles.WindowBackgroundColor);

            float contentWidth = position.width - CommonStyles.LeftContentPadding * 2f;

            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition);
            GUILayout.BeginHorizontal();
            GUILayout.Space(CommonStyles.LeftContentPadding);
            GUILayout.BeginVertical();

            GUILayout.Space(CommonStyles.Space2);
            DrawSourceImageSection();
            GUILayout.Space(CommonStyles.Space2);
            DrawGuideLink();
            GUILayout.Space(CommonStyles.Space2);
            DrawParameterSection(contentWidth);
            GUILayout.Space(CommonStyles.Space2);
            DrawPreviewSection(contentWidth);
            GUILayout.Space(CommonStyles.Space2);
            DrawActionSection(contentWidth);
            GUILayout.Space(CommonStyles.Space2);

            GUILayout.EndVertical();
            GUILayout.Space(CommonStyles.LeftContentPadding);
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
        }

        // ---------- 图片选择区 ----------

        private void DrawSourceImageSection()
        {
            UIComponents.DrawSectionTitle(TJGeneratorsL10n.L("选择图片"), uppercase: false);
            GUILayout.Space(CommonStyles.Space2);

            UploadImageComponents.DrawLargeImageUpload(
                ref _sourceAssetPath,
                ref _sourceTexture,
                onAIGenClicked: null,
                repaint: Repaint,
                onUserChanged: ClearPreview,
                onPickDone: (path, tex) => SetSourceAsset(path, tex));

            if (!string.IsNullOrEmpty(_sourceAssetPath))
            {
                GUILayout.Space(2f);
                GUILayout.Label(_sourceAssetPath, CommonStyles.SmallGreyLeftLabelStyle);
            }
        }

        // ---------- 使用指南链接 ----------

        private void DrawGuideLink()
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(TJGeneratorsL10n.L("✦ UI切图使用指南"), CommonStyles.LinkStyle))
            {
                Application.OpenURL(GuideUrl);
            }
            UIComponents.AddLinkCursorToLastRect();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // ---------- 参数区 ----------

        private void DrawParameterSection(float contentWidth)
        {
            UIComponents.DrawSectionTitle(TJGeneratorsL10n.L("参数设置"), uppercase: false);
            GUILayout.Space(CommonStyles.Space2);

            // 背景模式
            int newBgMode = EditorGUILayout.Popup(TJGeneratorsL10n.L("背景模式"), _bgModeIndex, BgModeNames);
            if (newBgMode != _bgModeIndex)
            {
                _bgModeIndex = newBgMode;
                EditorPrefs.SetInt("TJGen_ImageSlice_BgMode", _bgModeIndex);
                InvalidateAnalysis();
            }

            // Alpha 阈值（透明背景模式）
            if (_bgModeIndex == 0 || _bgModeIndex == 1)
            {
                float newAlpha = EditorGUILayout.Slider(TJGeneratorsL10n.L("Alpha 阈值"), _alphaThreshold, 0f, 1f);
                if (Mathf.Abs(newAlpha - _alphaThreshold) > 0.001f)
                {
                    _alphaThreshold = newAlpha;
                    EditorPrefs.SetFloat("TJGen_ImageSlice_AlphaThreshold", _alphaThreshold);
                    InvalidateAnalysis();
                }
            }

            // 颜色容差（纯色背景模式）
            if (_bgModeIndex == 0 || _bgModeIndex == 2)
            {
                float newTol = EditorGUILayout.Slider(TJGeneratorsL10n.L("颜色容差"), _colorTolerance, 0f, 100f);
                if (Mathf.Abs(newTol - _colorTolerance) > 0.01f)
                {
                    _colorTolerance = newTol;
                    EditorPrefs.SetFloat("TJGen_ImageSlice_ColorTolerance", _colorTolerance);
                    InvalidateAnalysis();
                }
            }

            // 最小区域像素数
            int newMin = EditorGUILayout.IntField(TJGeneratorsL10n.L("最小区域(像素)"), _minRegionPixels);
            if (newMin != _minRegionPixels)
            {
                _minRegionPixels = Mathf.Max(1, newMin);
                EditorPrefs.SetInt("TJGen_ImageSlice_MinRegion", _minRegionPixels);
                InvalidateAnalysis();
            }

            // 留白
            int newPad = EditorGUILayout.IntField(TJGeneratorsL10n.L("留白(像素)"), _padding);
            if (newPad != _padding)
            {
                _padding = Mathf.Max(0, newPad);
                EditorPrefs.SetInt("TJGen_ImageSlice_Padding", _padding);
                InvalidateAnalysis();
            }

            // 设为 Sprite
            bool newSprite = EditorGUILayout.Toggle(TJGeneratorsL10n.L("自动设为 Sprite"), _setAsSprite);
            if (newSprite != _setAsSprite)
            {
                _setAsSprite = newSprite;
                EditorPrefs.SetBool("TJGen_ImageSlice_SetAsSprite", _setAsSprite);
            }
        }

        // ---------- 预览区 ----------

        private void DrawPreviewSection(float contentWidth)
        {
            const float maxPreviewHeight = 400f;

            UIComponents.DrawSectionTitle(TJGeneratorsL10n.L("预览"), uppercase: false);
            GUILayout.Space(CommonStyles.Space2);

            if (UIComponents.DrawGenerateButton(
                    _isProcessing ? TJGeneratorsL10n.L("正在分析...") : TJGeneratorsL10n.L("检测区域"),
                    _sourceTexture != null && !_isProcessing,
                    _isProcessing,
                    GUILayout.ExpandWidth(true),
                    GUILayout.Height(LeftPanelBottomDock.ActionButtonHeight)))
            {
                RunAnalysis();
            }

            GUILayout.Space(CommonStyles.Space2);
            if (_detectedRegions.Count > 0)
            {
                GUILayout.Label(
                    string.Format(TJGeneratorsL10n.L("检测到 {0} 个区域"), _detectedRegions.Count),
                    CommonStyles.SectionTitleStyle);
            }
            else if (_isProcessing)
            {
                GUILayout.Label(TJGeneratorsL10n.L("正在分析..."), CommonStyles.HintLabelStyle);
            }
            else
            {
                GUILayout.Label(GUIContent.none, CommonStyles.HintLabelStyle, GUILayout.Height(18f));
            }

            Rect previewRect = GUILayoutUtility.GetRect(
                contentWidth,
                maxPreviewHeight,
                GUILayout.ExpandWidth(true));
            if (_previewTexture != null)
                GUI.DrawTexture(previewRect, _previewTexture, ScaleMode.ScaleToFit, true);
        }

        // ---------- 操作区 ----------

        private void DrawActionSection(float contentWidth)
        {
            UIComponents.DrawSeparator();
            GUILayout.Space(CommonStyles.Space2);

            string btnText = _detectedRegions.Count > 0
                ? string.Format(TJGeneratorsL10n.L("切割并导出 ({0} 张)"), _detectedRegions.Count)
                : TJGeneratorsL10n.L("切割并导出");
            if (UIComponents.DrawGenerateButton(
                    _isProcessing ? TJGeneratorsL10n.L("正在导出...") : btnText,
                    _detectedRegions.Count > 0 && !_isProcessing,
                    _isProcessing,
                    GUILayout.ExpandWidth(true),
                    GUILayout.Height(LeftPanelBottomDock.ActionButtonHeight)))
            {
                RunExport();
            }
        }

        // ========== 逻辑 ==========

        private void SetSourceAsset(string path, Texture2D preloadedTexture = null)
        {
            ClearPreview();
            DestroyRuntimeSourceTexture();

            if (string.IsNullOrEmpty(path))
            {
                _sourceAssetPath = null;
                _sourceTexture = null;
                Repaint();
                return;
            }

            string assetsRelative = PathUtils.AbsolutePathToAssetsRelative(path);
            if (assetsRelative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                _sourceAssetPath = assetsRelative;
                _sourceTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetsRelative);
                if (preloadedTexture != null && preloadedTexture != _sourceTexture)
                    DestroyImmediate(preloadedTexture);
            }
            else
            {
                _sourceAssetPath = Path.GetFullPath(path).Replace('\\', '/');
                _sourceTexture = preloadedTexture
                    ?? SpriteSequencePostProcess.LoadReadableTextureFromAssetPath(_sourceAssetPath);
            }

            Repaint();
        }

        private void DestroyRuntimeSourceTexture()
        {
            if (_sourceTexture != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(_sourceTexture)))
                DestroyImmediate(_sourceTexture);
        }

        private void ClearPreview()
        {
            if (_previewTexture != null)
            {
                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(_previewTexture)))
                    DestroyImmediate(_previewTexture);
                _previewTexture = null;
            }
            _detectedRegions.Clear();
        }

        private void InvalidateAnalysis()
        {
            if (_detectedRegions.Count > 0 || _previewTexture != null)
            {
                ClearPreview();
                Repaint();
            }
        }

        private void RunAnalysis()
        {
            if (_sourceTexture == null)
                return;

            _isProcessing = true;
            Repaint();

            try
            {
                var readableTex = SpriteSequencePostProcess.LoadReadableTextureFromAssetPath(_sourceAssetPath);
                if (readableTex == null)
                {
                    EditorUtility.DisplayDialog(TJGeneratorsL10n.L("图片切割"), TJGeneratorsL10n.L("无法读取图片像素，请确保图片不是压缩格式。"), TJGeneratorsL10n.L("确定"));
                    _isProcessing = false;
                    return;
                }

                var result = ImageSlicePostProcess.Analyze(
                    readableTex,
                    BgModeValues[_bgModeIndex],
                    _alphaThreshold,
                    _colorTolerance,
                    _minRegionPixels,
                    _padding);

                ClearPreview();
                _detectedRegions = result.regions;
                _previewTexture = result.previewTexture;

                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(readableTex)))
                    DestroyImmediate(readableTex);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog(TJGeneratorsL10n.L("图片切割"), string.Format(TJGeneratorsL10n.L("分析失败：{0}"), e.Message), TJGeneratorsL10n.L("确定"));
            }
            finally
            {
                _isProcessing = false;
                Repaint();
            }
        }

        private void RunExport()
        {
            if (_sourceTexture == null)
                return;

            _isProcessing = true;
            Repaint();

            try
            {
                var readableTex = SpriteSequencePostProcess.LoadReadableTextureFromAssetPath(_sourceAssetPath);
                if (readableTex == null)
                {
                    EditorUtility.DisplayDialog(TJGeneratorsL10n.L("图片切割"), TJGeneratorsL10n.L("无法读取图片像素。"), TJGeneratorsL10n.L("确定"));
                    _isProcessing = false;
                    return;
                }

                var result = ImageSlicePostProcess.Export(
                    readableTex,
                    _sourceAssetPath,
                    BgModeValues[_bgModeIndex],
                    _alphaThreshold,
                    _colorTolerance,
                    _minRegionPixels,
                    _padding,
                    _setAsSprite);

                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(readableTex)))
                    DestroyImmediate(readableTex);

                if (result.ExportedCount > 0)
                {
                    ReportImageSliceUsage(_sourceAssetPath, result.ExportedCount, BgModeNames[_bgModeIndex]);

                    var folderObj = AssetDatabase.LoadAssetAtPath<DefaultAsset>(result.OutputDirectory);
                    if (folderObj != null)
                    {
                        Selection.activeObject = folderObj;
                        EditorGUIUtility.PingObject(folderObj);
                    }

                    EditorUtility.DisplayDialog(TJGeneratorsL10n.L("图片切割"),
                        string.Format(TJGeneratorsL10n.L("成功切割并导出 {0} 张图片\n\n输出目录：{1}"), result.ExportedCount, result.OutputDirectory),
                        TJGeneratorsL10n.L("确定"));
                }
                else
                {
                    EditorUtility.DisplayDialog(TJGeneratorsL10n.L("图片切割"), TJGeneratorsL10n.L("未检测到可切割的区域，请调整参数后重试。"), TJGeneratorsL10n.L("确定"));
                }
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog(TJGeneratorsL10n.L("图片切割"), string.Format(TJGeneratorsL10n.L("导出失败：{0}"), e.Message), TJGeneratorsL10n.L("确定"));
            }
            finally
            {
                _isProcessing = false;
                Repaint();
            }
        }

        /// <summary>
        /// 向后端上报图片切割使用记录，用于任务统计。fire-and-forget，失败静默。
        /// </summary>
        private static void ReportImageSliceUsage(string sourceAssetPath, int sliceCount, string backgroundMode)
        {
            // 在主线程先取 token，避免后台线程访问 UnityConnectSession
            string token = UnityConnectSession.instance.GetAccessToken();
            if (string.IsNullOrEmpty(token))
            {
                try { token = CodelyTokenProvider.GetToken(); } catch { token = ""; }
            }
            if (string.IsNullOrEmpty(token))
                return;

            string url = ConfigManager.GetApiBaseUrl() + "task/image-slice";
            var body = new Dictionary<string, object>
            {
                ["sourceAssetPath"] = sourceAssetPath ?? "",
                ["sliceCount"] = sliceCount,
                ["backgroundMode"] = backgroundMode ?? ""
            };
            string jsonBody = JsonConvert.SerializeObject(body);

            Task.Run(() =>
            {
                try
                {
                    CodelyHttpClient.PostJsonSync(url, jsonBody, token, timeoutSeconds: 10);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ImageSlice] 上报使用记录失败: {e.Message}");
                }
            });
        }
    }
}
#endif
