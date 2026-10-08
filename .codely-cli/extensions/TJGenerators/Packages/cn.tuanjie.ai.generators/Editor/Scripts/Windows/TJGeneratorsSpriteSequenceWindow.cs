#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TJGenerators.Config;
using TJGenerators.Generators;
using TJGenerators.Pipeline;
using TJGenerators.UI;
using TJGenerators.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using Unity.EditorCoroutines.Editor;

namespace TJGenerators
{
    /// <summary>
    /// 2D 序列帧（动作）生成窗口：输入动作描述（必填）+ 参考图（可选），输出多帧 Sprite + AnimationClip。
    /// </summary>
    public class TJGeneratorsSpriteSequenceWindow : TJGeneratorsAssetWindowBase
    {
        protected override ConfigType WindowConfigType => ConfigType.SpriteSequence;
        protected override string LogTag => "[TJGeneratorsSpriteSequence]";

        protected override string TargetHeaderLabel => TJGeneratorsL10n.L("目标动画");
        protected override string UnboundTargetLabel => TJGeneratorsL10n.L("未绑定（生成到历史）");
        protected override string EmptyGeneratorsMessage =>
            TJGeneratorsL10n.L("未找到可用的 2D 序列帧生成器，请检查 GeneratorConfig.json 中的 spriteSequenceGenerators");
        protected override string HistoryApplyLabel => TJGeneratorsL10n.L("应用到当前动画");
        protected override string PromptControlName => "sprite_sequence_prompt_input";

        private string referenceImagePath = "";
        private Texture2D referenceImageThumb;

        private static readonly Dictionary<string, TJGeneratorsSpriteSequenceWindow> s_openWindows =
            new Dictionary<string, TJGeneratorsSpriteSequenceWindow>();

        // ========== 静态入口 ==========

        public static void ShowWindow()
        {
            var rect = GetDefaultMainWindowRect();
            var window = GetWindowWithRect<TJGeneratorsSpriteSequenceWindow>(
                rect,
                utility: false,
                title: TJGeneratorsL10n.L("TJGenerators 2D动作序列帧"),
                focus: true
            );
            window.titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 2D动作序列帧"));
            FinalizeMainWindowShow(window, rect);
        }

        /// <summary>
        /// 从指定 AnimationClip 资产路径打开窗口；生成时将写入该资产或与之关联历史。
        /// </summary>
        public static void OpenForAsset(string assetPath)
        {
            GenerationWindowBase.OpenForAsset(
                assetPath,
                s_openWindows,
                "[TJGeneratorsSpriteSequence]",
                TJGeneratorsL10n.L("TJGenerators 2D动作序列帧 - {0}"),
                () =>
                {
                    var window = CreateInstance<TJGeneratorsSpriteSequenceWindow>();
                    return window;
                },
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
            if (referenceImageThumb != null)
            {
                DestroyImmediate(referenceImageThumb);
                referenceImageThumb = null;
            }
        }

        protected override void OnGeneratorRestoredFromTask(ModelGeneratorBase generator)
        {
            base.OnGeneratorRestoredFromTask(generator);
            currentSelectedModel = BuildModelInfoFromGenerator(generator);
        }

        protected override void ResetInputStateAfterModelChange()
        {
            var config = GetCurrentGeneratorConfig();
            ResetTextPromptIfHidden(config, ref textPrompt);
            ClearSingleReferenceImageWhenUploadHidden(config, ref referenceImagePath, ref referenceImageThumb);
        }

        // ========== UI ==========

        protected override void DrawLeftPanelBody()
        {
            DrawInputSection();
            GUILayout.Space(CommonStyles.Space3);
            DrawConfigurationSection();
            GUILayout.Space(CommonStyles.Space3);
        }

        protected override bool CanStartGeneration =>
            _currentGenerator != null && !string.IsNullOrEmpty(referenceImagePath);

        protected override void DrawInputSection()
        {
            var genConfig = GetCurrentGeneratorConfig();
            textPrompt = DrawConfiguredTextPromptInput(textPrompt, PromptControlName, genConfig);

            if (ShouldShowImageUpload(genConfig))
            {
                if (ShouldShowTextInput(genConfig))
                    GUILayout.Space(CommonStyles.Space3);
                var uiLayout = genConfig?.uiLayout;
                UIComponents.DrawReferenceImageSectionTitle(
                    ResolveImageUploadLabel(uiLayout),
                    string.IsNullOrEmpty(referenceImagePath) ? 0 : 1,
                    1);
                GUILayout.Space(CommonStyles.Space2);
                UploadImageComponents.DrawLargeImageUpload(
                    ref referenceImagePath,
                    ref referenceImageThumb,
                    null,
                    Repaint,
                    onPickDone: (path, tex) =>
                    {
                        referenceImagePath = path;
                        referenceImageThumb = tex;
                    });
            }
        }

        // ========== 历史记录 ==========

        protected override void DrawHistoryPanel(float panelWidth)
        {
            DrawStandardHistoryPanel(panelWidth, new StandardHistoryPanelOptions
            {
                GetLargePreviewTexture = GetPreviewTextureForHistoryItem,
                DrawTilePreview = DrawSpriteSequenceHistoryPreview,
                GetModelLabel = item => item.GetTimeString(),
                DrawHistoryActions = () => DrawDefaultHistoryActions(),
            });
        }

        private void DrawSpriteSequenceHistoryPreview(Rect rect, TJGeneratorsGenerationHistoryItem item)
        {
            if (item.isGenerating)
            {
                UIComponents.DrawLoadingSpinner(rect, null, Repaint);
                return;
            }

            Texture2D preview = GetPreviewTextureForHistoryItem(item);
            if (preview != null)
                GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit);
            else
                EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f, 1f));
        }

        private Texture2D GetPreviewTextureForHistoryItem(TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null || item.isGenerating) return null;

            // 优先使用 URL 预览图（内存缓存）
            if (!string.IsNullOrEmpty(item.previewImageUrl))
            {
                if (urlPreviewCache.TryGetValue(item.previewImageUrl, out var cachedTex) && cachedTex != null)
                    return cachedTex;

                // 检查本地文件缓存
                var localTex = LoadPreviewFromLocalCache(item.previewImageUrl);
                if (localTex != null)
                {
                    urlPreviewCache[item.previewImageUrl] = localTex;
                    return localTex;
                }

                // 触发下载（异步，下次 Repaint 时显示）
                if (!urlPreviewLoading.Contains(item.previewImageUrl) && !urlPreviewFailed.Contains(item.previewImageUrl))
                    EditorCoroutineUtility.StartCoroutineOwnerless(DownloadPreviewImage(item.previewImageUrl));
            }

            // Fallback：从本地 AnimationClip 取第一帧 Sprite
            if (!string.IsNullOrEmpty(item.modelPath))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(item.modelPath);
                if (clip != null)
                {
                    try
                    {
                        var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                        foreach (var binding in bindings)
                        {
                            if (binding.propertyName != null && binding.propertyName.Contains("m_Sprite"))
                            {
                                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                                var sprite = keys != null && keys.Length > 0 ? keys[0].value as Sprite : null;
                                if (sprite != null)
                                    return AssetPreview.GetAssetPreview(sprite) ?? AssetPreview.GetMiniThumbnail(sprite) as Texture2D;
                            }
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        private Texture2D LoadPreviewFromLocalCache(string imageUrl)
        {
            string cacheDir = Path.Combine(Application.dataPath, "../Library/AI.TJGenerators/PreviewCache");
            string hash = imageUrl.GetHashCode().ToString("X8");
            string path = Path.Combine(cacheDir, hash + ".png");
            if (!File.Exists(path)) return null;
            try
            {
                var tex = new Texture2D(2, 2);
                if (tex.LoadImage(File.ReadAllBytes(path)))
                    return tex;
                DestroyImmediate(tex);
            }
            catch { }
            return null;
        }

        private IEnumerator DownloadPreviewImage(string imageUrl)
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

        // ========== 生成逻辑 ==========

        protected override void OnStartGeneration()
        {
            if (_currentGenerator == null)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("请先选择模型"), LogTag);
                return;
            }

            MarkGenerationStarted();

            if (_currentGenerator is DynamicGenerator dynamicGen)
            {
                dynamicGen.SetImagePath(!string.IsNullOrEmpty(referenceImagePath) ? referenceImagePath : null);
            }

            StartPipelineForCurrentGenerator();
        }

        protected override void ApplyHistoryToAsset(int index)
        {
            if (index < 0 || index >= generationHistory.Count) return;
            var item = generationHistory[index];
            if (item.isGenerating)
            {
                Debug.LogWarning($"{LogTag} {TJGeneratorsL10n.L("请等待该条生成完成后再应用。")}");
                return;
            }
            if (string.IsNullOrEmpty(item.modelPath) || !File.Exists(item.modelPath))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("动画文件不存在，可能已被删除。"), LogTag);
                TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                generationHistory = TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentAssetGuid());
                Repaint();
                return;
            }
            if (_targetAsset == null || !_targetAsset.IsValid())
            {
                Debug.LogWarning($"{LogTag} {TJGeneratorsL10n.L("当前未绑定目标动画资产，无法应用。")}");
                return;
            }

            string targetPath = _targetAsset.GetPath();
            try
            {
                File.Copy(item.modelPath, targetPath, true);
                AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath);
                if (clip != null)
                {
                    Selection.activeObject = clip;
                    EditorGUIUtility.PingObject(clip);
                }
                Debug.Log($"{LogTag} {TJGeneratorsL10n.L("已将历史记录应用到当前动画。")}");
            }
            catch (Exception ex)
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("应用失败: {0}", ex.Message), LogTag);
            }
        }

        // ========== IMediaAssetPipelineHost ==========

        public override void OnGenerationCompleted(string assetPath)
        {
            base.OnGenerationCompleted(assetPath);
            MarkGenerationCompleted();

            if (!string.IsNullOrEmpty(assetPath))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                if (clip != null)
                {
                    Selection.activeObject = clip;
                    EditorGUIUtility.PingObject(clip);
                }
            }
        }

        public override string GetAssetSavePath(PipelineMediaType type, ModelGeneratorBase generator)
        {
            if (type != PipelineMediaType.Texture) return null;
            return BuildHistoryTexturePath("SpriteSequence_");
        }

        public override void OnAssetSaved(PipelineMediaType type, string savePath, ModelGeneratorBase generator)
        {
            if (type != PipelineMediaType.Texture) return;

            var importer = AssetImporter.GetAtPath(savePath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.SaveAndReimport();
            }
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));
        }
    }
}
#endif
