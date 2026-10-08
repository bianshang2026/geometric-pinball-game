#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using TJGenerators.Config;
using TJGenerators.UI;
using TJGenerators.Utils;

namespace TJGenerators
{
    /// <summary>
    /// AI 参考图生成窗口（UI）。生成逻辑见 <see cref="AIReferenceImageGeneration"/>。
    /// </summary>
    public class AIReferenceImageWindow : EditorWindow
    {
        private const float MinPreviewWindowHeight = 450f;
        private const float MinMultiPreviewWindowWidth = 500f;

        private string _prompt = "";
        private bool _isGenerating;
        private string _statusMessage = "";
        private bool _showPreview;
        private EditorCoroutine _generationCoroutine;

        private List<ImageGeneratorConfig> _generators;
        private string[] _generatorNames;
        private int _selectedGeneratorIndex;

        private Action<string, Texture2D> _onSingleImageGenerated;
        private Texture2D _previewTexture;
        private string _previewFilePath;

        private bool _isMultiViewMode;
        private Action<string[], Texture2D[]> _onMultiViewGenerated;
        private Texture2D[] _multiViewTextures;
        private string[] _multiViewFilePaths;
        private int _multiViewTargetCount = 4;

        private ImageGeneratorConfig SelectedConfig =>
            _generators != null && _selectedGeneratorIndex < _generators.Count
                ? _generators[_selectedGeneratorIndex]
                : null;

        private static string StatusNoGeneratorConfig =>
            $"<color=#ff6666>{TJGeneratorsL10n.L("没有可用的图片生成器配置")}</color>";

        private static string StatusAuthFailed =>
            $"<color=#ff6666>{TJGeneratorsL10n.L("登录权限检查失败，请确认编辑器左上角或者Hub内已登录")}</color>";

        // ── Public entry ─────────────────────────────────────────────────────

        public static void Show(Action<string, Texture2D> onImageGenerated)
        {
            var w = CreateConfiguredWindow(multiView: false);
            w._onSingleImageGenerated = onImageGenerated;
            w.ShowUtility();
        }

        /// <summary>多视图：正面→左侧→背面→右侧；张数由 <paramref name="multiViewMinRequired"/> 决定（1–4）。</summary>
        public static void ShowMultiView(Action<string[], Texture2D[]> onMultiViewGenerated, int multiViewMinRequired)
        {
            var w = CreateConfiguredWindow(multiView: true, multiViewMinRequired);
            w._onMultiViewGenerated = onMultiViewGenerated;
            w.ShowUtility();
        }

        // ── Lifecycle ────────────────────────────────────────────────────────

        private void OnGUI()
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), CommonStyles.WindowBackgroundColor);
            GUILayout.BeginVertical(CommonStyles.WindowContentStyle);
            if (_showPreview)
                DrawPreviewUI();
            else
                DrawInputUI();
            GUILayout.EndVertical();
        }

        private void OnDestroy()
        {
            StopGenerationCoroutine();
            DestroyPreviewTextures();
        }

        // ── UI ───────────────────────────────────────────────────────────────

        private void DrawInputUI()
        {
            GUILayout.Label(
                _isMultiViewMode
                    ? TJGeneratorsL10n.L("输入提示词生成多视图参考图")
                    : TJGeneratorsL10n.L("输入提示词生成参考图"),
                CommonStyles.HeaderStyle);

            if (_isMultiViewMode)
            {
                int k = Mathf.Min(_multiViewTargetCount, 4);
                string orderText = string.Join("、", Enumerable.Range(0, k).Select(MultiViewLabel));
                GUILayout.Label(
                    string.Format(TJGeneratorsL10n.L("将按顺序自动生成：{0}（共{1}张）"), orderText, _multiViewTargetCount),
                    CommonStyles.SmallGreyLabelStyle);
            }

            GUILayout.Space(CommonStyles.Space2);
            DrawModelPickerIfNeeded();

            GUI.enabled = !_isGenerating;
            _prompt = EditorGUILayout.TextField(_prompt, CommonStyles.TextFieldStyle);
            GUI.enabled = true;
            GUILayout.Space(CommonStyles.Space2);

            bool playBlocked = TJGeneratorsPlayModeGuard.IsActive;
            bool canGenerate = !_isGenerating && !string.IsNullOrEmpty(_prompt) && SelectedConfig != null && !playBlocked;
            if (UIComponents.DrawGenerateButton(
                    _isGenerating ? TJGeneratorsL10n.L("生成中...") : TJGeneratorsL10n.L("生成图片"),
                    canGenerate,
                    _isGenerating,
                    GUILayout.Height(LeftPanelBottomDock.ActionButtonHeight)))
            {
                StartGeneration();
            }
            if (playBlocked)
            {
                GUILayout.Space(CommonStyles.Space1);
                GUILayout.Label(TJGeneratorsPlayModeGuard.ShortHint, CommonStyles.MiniRedLabelStyle);
            }

            DrawStatusFooter(CommonStyles.Space2);
        }

        private void DrawModelPickerIfNeeded()
        {
            if (_generators == null || _generators.Count <= 1)
                return;

            GUILayout.BeginHorizontal();
            GUILayout.Label(TJGeneratorsL10n.L("模型:"), CommonStyles.HeaderStyle);
            GUI.enabled = !_isGenerating;
            _selectedGeneratorIndex = EditorGUILayout.Popup(_selectedGeneratorIndex, _generatorNames);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(CommonStyles.Space2);
        }

        private void DrawPreviewUI()
        {
            GUILayout.Label(
                _isMultiViewMode
                    ? TJGeneratorsL10n.L("多视图生成结果预览")
                    : TJGeneratorsL10n.L("生成结果预览"),
                CommonStyles.HeaderStyle);
            GUILayout.Space(5);

            if (_isMultiViewMode)
                DrawMultiViewTiles();
            else
                DrawSinglePreview();

            GUILayout.Space(10);
            DrawConfirmButtons();
        }

        private void DrawSinglePreview()
        {
            if (_previewTexture == null)
                return;

            float maxSize = Mathf.Max(Mathf.Min(position.width - 20, position.height - 120), 100f);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            Rect r = GUILayoutUtility.GetRect(maxSize, maxSize);
            GUI.DrawTexture(r, _previewTexture, ScaleMode.ScaleToFit);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawMultiViewTiles()
        {
            int n = _multiViewTargetCount;
            float tileSize = Mathf.Max(
                Mathf.Min((position.width - 60) / Mathf.Max(1, n), position.height - 140),
                80f);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            for (int i = 0; i < n; i++)
            {
                GUILayout.BeginVertical();
                Rect r = GUILayoutUtility.GetRect(tileSize, tileSize);
                if (_multiViewTextures != null && _multiViewTextures[i] != null)
                    GUI.DrawTexture(r, _multiViewTextures[i], ScaleMode.ScaleToFit);
                else
                    EditorGUI.DrawRect(r, new Color(0.2f, 0.2f, 0.2f));
                GUILayout.Label(MultiViewLabel(i), CommonStyles.SmallGreyCenterLabelStyle, GUILayout.Width(tileSize));
                GUILayout.EndVertical();
                if (i < n - 1)
                    GUILayout.Space(10);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawConfirmButtons()
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUI.enabled = !_isGenerating;

            if (GUILayout.Button(TJGeneratorsL10n.L("使用此图片"), CommonStyles.ButtonStyle, GUILayout.Width(130)))
                ScheduleUseImageAndClose();

            if (GUILayout.Button(TJGeneratorsL10n.L("重新生成"), CommonStyles.ButtonStyle, GUILayout.Width(130)))
                ResetPreviewForRegeneration();

            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            DrawStatusFooter(5f);
        }

        private void DrawStatusFooter(float spacingBefore)
        {
            if (string.IsNullOrEmpty(_statusMessage))
                return;
            GUILayout.Space(spacingBefore);
            GUILayout.Label(_statusMessage, CommonStyles.StatusStyle);
        }

        // ── Generation (UI shell → service) ──────────────────────────────────

        private void StartGeneration()
        {
            var config = SelectedConfig;
            if (config == null)
            {
                _statusMessage = StatusNoGeneratorConfig;
                Repaint();
                return;
            }

            _isGenerating = true;
            _statusMessage = _isMultiViewMode
                ? string.Format(TJGeneratorsL10n.L("正在生成多视图图片 (1/{0})..."), _multiViewTargetCount)
                : TJGeneratorsL10n.L("正在生成图片，请稍候...");
            Repaint();

            StopGenerationCoroutine();
            // Bound to this window so close/destroy stops the coroutine (no ownerless leak).
            _generationCoroutine = EditorCoroutineUtility.StartCoroutine(
                _isMultiViewMode
                    ? RunMultiViewGeneration(config.id)
                    : RunSingleGeneration(config.id),
                this);
        }

        private void StopGenerationCoroutine()
        {
            if (_generationCoroutine == null)
                return;
            EditorCoroutineUtility.StopCoroutine(_generationCoroutine);
            _generationCoroutine = null;
        }

        private IEnumerator RunSingleGeneration(string generatorId)
        {
            string path = null;
            string error = null;

            yield return AIReferenceImageGeneration.GenerateSingle(
                _prompt,
                onSuccess: p => path = p,
                onError: err => error = err,
                generatorId: generatorId,
                requestOrigin: GenerationRequestOrigin.Ui);

            if (this == null)
                yield break;

            if (!TryAcceptResult(error, path != null ? new[] { path } : null, expectedCount: 1))
                yield break;

            DestroyPreviewTextures();
            _previewFilePath = path;
            _previewTexture = LoadTextureFromPath(path);
            EnterPreviewMode(multiViewLayout: false);
        }

        private IEnumerator RunMultiViewGeneration(string generatorId)
        {
            string[] paths = null;
            string error = null;

            yield return AIReferenceImageGeneration.GenerateMultiView(
                _prompt,
                _multiViewTargetCount,
                onSuccess: p => paths = p,
                onError: err => error = err,
                onProgress: (cur, total, _) =>
                {
                    if (this == null)
                        return;
                    _statusMessage = string.Format(
                        TJGeneratorsL10n.L("正在生成多视图图片 ({0}/{1}) - {2}..."),
                        cur, total, MultiViewLabel(cur - 1));
                    Repaint();
                },
                generatorId: generatorId,
                requestOrigin: GenerationRequestOrigin.Ui);

            if (this == null)
                yield break;

            if (!TryAcceptResult(error, paths, _multiViewTargetCount))
                yield break;

            DestroyPreviewTextures();
            _multiViewFilePaths = new string[_multiViewTargetCount];
            _multiViewTextures = new Texture2D[_multiViewTargetCount];
            for (int i = 0; i < paths.Length; i++)
            {
                _multiViewFilePaths[i] = paths[i];
                _multiViewTextures[i] = LoadTextureFromPath(paths[i]);
            }

            EnterPreviewMode(multiViewLayout: true);
        }

        private bool TryAcceptResult(string error, string[] paths, int expectedCount)
        {
            if (!string.IsNullOrEmpty(error))
            {
                FailGeneration(MapServiceError(error));
                return false;
            }

            if (paths == null || paths.Length != expectedCount || paths.Any(p => string.IsNullOrEmpty(p) || !File.Exists(p)))
            {
                FailGeneration(RichError(TJGeneratorsL10n.L("生成失败: 无法从响应中提取图片URL")));
                return false;
            }

            return true;
        }

        private void EnterPreviewMode(bool multiViewLayout)
        {
            _generationCoroutine = null;
            _showPreview = true;
            _isGenerating = false;
            _statusMessage = "";
            EnsurePreviewWindowSize(multiViewLayout);
            Repaint();
        }

        private void FailGeneration(string statusMessage)
        {
            _generationCoroutine = null;
            _isGenerating = false;
            _statusMessage = statusMessage ?? "";
            Repaint();
        }

        // ── Confirm / reset ──────────────────────────────────────────────────

        private void ScheduleUseImageAndClose()
        {
            // Transfer texture ownership to the host callback; do not Destroy on close.
            if (_isMultiViewMode)
            {
                string[] paths = _multiViewFilePaths;
                Texture2D[] textures = _multiViewTextures;
                Action<string[], Texture2D[]> cb = _onMultiViewGenerated;
                _multiViewFilePaths = null;
                _multiViewTextures = null;
                EditorApplication.delayCall += () => cb?.Invoke(paths, textures);
            }
            else
            {
                string path = _previewFilePath;
                Texture2D tex = _previewTexture;
                Action<string, Texture2D> cb = _onSingleImageGenerated;
                _previewFilePath = null;
                _previewTexture = null;
                EditorApplication.delayCall += () => cb?.Invoke(path, tex);
            }

            EditorApplication.delayCall += Close;
        }

        private void ResetPreviewForRegeneration()
        {
            _showPreview = false;
            _statusMessage = "";
            DestroyPreviewTextures();
            _previewFilePath = null;
            if (_isMultiViewMode)
            {
                _multiViewTextures = new Texture2D[_multiViewTargetCount];
                _multiViewFilePaths = new string[_multiViewTargetCount];
            }
            Repaint();
        }

        // ── Setup / helpers ──────────────────────────────────────────────────

        private static AIReferenceImageWindow CreateConfiguredWindow(bool multiView, int multiViewMinRequired = 4)
        {
            var w = CreateInstance<AIReferenceImageWindow>();
            w._isMultiViewMode = multiView;
            if (multiView)
            {
                int n = Mathf.Clamp(multiViewMinRequired, 1, 4);
                w._multiViewTargetCount = n;
                w._multiViewTextures = new Texture2D[n];
                w._multiViewFilePaths = new string[n];
                w.titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 参考图生成 (多视图)"));
                w.minSize = new Vector2(500f, 200f);
            }
            else
            {
                w.titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 参考图生成"));
                w.minSize = new Vector2(420f, 200f);
            }

            w.LoadGenerators();
            return w;
        }

        private void LoadGenerators()
        {
            _generators = ConfigManager.GetReferenceImageGenerators();
            if (_generators == null || _generators.Count == 0)
            {
                TJLog.LogWarning("[AI参考图生成] 没有找到启用的参考图生成器配置");
                _generators = new List<ImageGeneratorConfig>();
                _generatorNames = new[] { TJGeneratorsL10n.L("(无可用模型)") };
                return;
            }

            _generatorNames = new string[_generators.Count];
            for (int i = 0; i < _generators.Count; i++)
                _generatorNames[i] = TJGeneratorsL10n.L(_generators[i].displayName);
            _selectedGeneratorIndex = 0;
        }

        private void EnsurePreviewWindowSize(bool multiViewLayout)
        {
            var pos = position;
            bool changed = false;
            if (pos.height < MinPreviewWindowHeight)
            {
                pos.height = MinPreviewWindowHeight;
                changed = true;
            }

            if (multiViewLayout && pos.width < MinMultiPreviewWindowWidth)
            {
                pos.width = MinMultiPreviewWindowWidth;
                changed = true;
            }

            if (changed)
                position = pos;
        }

        private static string MultiViewLabel(int index)
        {
            switch (index)
            {
                case 0: return TJGeneratorsL10n.L("正面");
                case 1: return TJGeneratorsL10n.L("左侧");
                case 2: return TJGeneratorsL10n.L("背面");
                case 3: return TJGeneratorsL10n.L("右侧");
                default: return (index + 1).ToString();
            }
        }

        private string MapServiceError(string error)
        {
            if (string.IsNullOrEmpty(error))
                return RichError(TJGeneratorsL10n.L("未知错误"));

            if (error.IndexOf("Not logged in", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("Auth failed", StringComparison.OrdinalIgnoreCase) >= 0)
                return StatusAuthFailed;

            if (error.IndexOf("No enabled reference-image", StringComparison.OrdinalIgnoreCase) >= 0)
                return StatusNoGeneratorConfig;

            if (error.IndexOf("Missing reference images", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RichError(TJGeneratorsL10n.L(
                    "缺少可用的参考图（本地文件或图片 URL），无法继续生成侧视/背视。请确认正面已生成成功且接口返回了地址。"));
            }

            if (error.IndexOf("Front view did not return", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RichError(TJGeneratorsL10n.L(
                    "正面图未返回可用的图片 URL，无法与后续视角对齐，已中止。"));
            }

            if (error.IndexOf("did not return an image URL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RichError(TJGeneratorsL10n.L("当前视角未返回图片 URL，多视图链已中止。"));
            }

            // Service keeps English messages for headless callers; map back for the UI.
            if (TryMapPrefixedServiceError(error, "Request failed:", "请求失败: {0}", out string requestFailed))
                return RichError(requestFailed);
            if (TryMapPrefixedServiceError(error, "Image download failed:", "图片下载失败: {0}", out string downloadFailed))
                return RichError(downloadFailed);

            return RichError(string.Format(TJGeneratorsL10n.L("生成失败: {0}"), error));
        }

        private static bool TryMapPrefixedServiceError(
            string error, string englishPrefix, string l10nFormatKey, out string localized)
        {
            localized = null;
            if (!error.StartsWith(englishPrefix, StringComparison.OrdinalIgnoreCase))
                return false;
            string detail = error.Substring(englishPrefix.Length).Trim();
            if (string.IsNullOrEmpty(detail))
                detail = TJGeneratorsL10n.L("未知错误");
            localized = string.Format(TJGeneratorsL10n.L(l10nFormatKey), detail);
            return true;
        }

        private static string RichError(string message) => $"<color=#ff6666>{message}</color>";

        private static Texture2D LoadTextureFromPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            var tex = new Texture2D(2, 2);
            if (!tex.LoadImage(File.ReadAllBytes(path)))
            {
                DestroyImmediate(tex);
                return null;
            }

            return tex;
        }

        private void DestroyPreviewTextures()
        {
            if (_previewTexture != null)
            {
                DestroyImmediate(_previewTexture);
                _previewTexture = null;
            }

            if (_multiViewTextures == null)
                return;

            for (int i = 0; i < _multiViewTextures.Length; i++)
            {
                if (_multiViewTextures[i] == null)
                    continue;
                DestroyImmediate(_multiViewTextures[i]);
                _multiViewTextures[i] = null;
            }
        }
    }
}
#endif
