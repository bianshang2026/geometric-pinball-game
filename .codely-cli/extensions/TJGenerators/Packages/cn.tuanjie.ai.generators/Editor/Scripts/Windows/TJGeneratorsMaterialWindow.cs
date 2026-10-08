#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using TJGenerators.Config;
using TJGenerators.Generators;
using TJGenerators.Pipeline;
using TJGenerators.UI;
using TJGenerators.Utils;
using UnityEditor;
using UnityEngine;

namespace TJGenerators
{
    /// <summary>
    /// TJGenerators 材质生成窗口：通过图生图生成 Unity Material 素材。
    /// </summary>
    public class TJGeneratorsMaterialWindow : TJGeneratorsAssetWindowBase
    {
        protected override ConfigType WindowConfigType => ConfigType.Material;
        protected override string LogTag => "[TJGeneratorsMaterial]";

        protected override string TargetHeaderLabel => TJGeneratorsL10n.L("目标材质");
        protected override string UnboundTargetLabel => TJGeneratorsL10n.L("未绑定材质");
        protected override string EmptyGeneratorsMessage =>
            TJGeneratorsL10n.L("未找到可用的 Material 生成器，请检查 GeneratorConfig.json 中的 materialGenerators");
        protected override string HistoryApplyLabel => TJGeneratorsL10n.L("应用到当前材质");
        protected override string PromptControlName => "material_prompt_input";

        private static string MaterialStartBlockedMessage =>
            TJGeneratorsL10n.L("请先选择模型，并输入文本提示词或上传参考图片。");

        private static readonly Dictionary<string, TJGeneratorsMaterialWindow> s_openWindows =
            new Dictionary<string, TJGeneratorsMaterialWindow>();

        private string materialReferenceImagePath = "";
        private Texture2D materialReferenceImageThumb;
        private MaterialTemplateOptionConfig selectedTexturePattern;

        // ========== 静态入口 ==========

        public static void ShowWindow()
        {
            var rect = GetDefaultMainWindowRect();
            var window = GetWindowWithRect<TJGeneratorsMaterialWindow>(
                rect,
                utility: false,
                title: TJGeneratorsL10n.L("TJGenerators 材质生成"),
                focus: true
            );
            window.titleContent = new GUIContent(TJGeneratorsL10n.L("TJGenerators 材质生成"));
            FinalizeMainWindowShow(window, rect);
        }

        public static void OpenForMaterialAsset(string assetPath)
        {
            GenerationWindowBase.OpenForAsset(
                assetPath,
                s_openWindows,
                "[TJGeneratorsMaterial]",
                TJGeneratorsL10n.L("TJGenerators 材质 - {0}"),
                () => CreateInstance<TJGeneratorsMaterialWindow>(),
                (w, r) => w._targetAsset = r,
                ShowWindow);
        }

        /// <summary>
        /// 为材质生成产出的贴图打开窗口：优先绑回历史中的目标 Material（如 New Material.mat），
        /// 否则再用同名 .mat（不存在则创建）。
        /// </summary>
        public static void OpenForMaterialTextureAsset(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath))
                return;

            string matPath = TJGeneratorsHistoryManager.TryResolveMaterialPathForTexture(texturePath);
            if (string.IsNullOrEmpty(matPath))
            {
                string siblingMatPath = Path.ChangeExtension(texturePath, ".mat");
                matPath = AssetDatabase.LoadAssetAtPath<Material>(siblingMatPath) != null
                    ? siblingMatPath
                    : EnsureMaterialAssetAtPath(siblingMatPath, texturePath);
            }

            OpenForMaterialAsset(matPath);
        }

        /// <summary>
        /// 在 candidatePath 复用或创建 Material；可选用来源贴图初始化 BaseColor。
        /// </summary>
        private static string EnsureMaterialAssetAtPath(string candidatePath, string sourceTexturePath = null)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(candidatePath) != null)
                return candidatePath;

            string materialPath = AssetDatabase.GenerateUniqueAssetPath(candidatePath);
            Shader shader = TJMaterialShaderUtility.ResolveSurfaceLitShader()
                            ?? Shader.Find("Unlit/Texture");
            var material = new Material(shader);
            if (!string.IsNullOrEmpty(sourceTexturePath))
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(sourceTexturePath);
                if (tex != null)
                    TJMaterialShaderUtility.AssignBaseColorTexture(material, tex);
            }

            AssetDatabase.CreateAsset(material, materialPath);
            AssetDatabase.SaveAssets();
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(materialPath));
            return materialPath;
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
            ClearMaterialReferenceImage();
        }

        public override void RefreshHistory()
        {
            string oldSelectedPath = null;
            string oldSelectedTaskId = null;
            if (generationHistory != null && selectedHistoryIndex >= 0 && selectedHistoryIndex < generationHistory.Count)
            {
                var item = generationHistory[selectedHistoryIndex];
                oldSelectedPath = item.modelPath ?? item.imagePath;
                oldSelectedTaskId = item.taskId;
            }

            generationHistory = LoadGenerationHistory();

            if (generationHistory.Count > 0)
            {
                int newIndex = -1;
                if (!string.IsNullOrEmpty(oldSelectedPath) || !string.IsNullOrEmpty(oldSelectedTaskId))
                {
                    newIndex = generationHistory.FindIndex(x =>
                        (!string.IsNullOrEmpty(oldSelectedPath) && (x.modelPath == oldSelectedPath || x.imagePath == oldSelectedPath)) ||
                        (!string.IsNullOrEmpty(oldSelectedTaskId) && x.taskId == oldSelectedTaskId));
                }

                selectedHistoryIndex = newIndex >= 0 ? newIndex : 0;
            }
            else
            {
                selectedHistoryIndex = -1;
            }

            Repaint();
        }

        protected override List<TJGeneratorsGenerationHistoryItem> LoadGenerationHistory()
        {
            if (_targetAsset != null && _targetAsset.IsValid())
            {
                string path = _targetAsset.GetPath();
                if (path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                    return TJGeneratorsHistoryManager.LoadHistoryForMaterialAsset(path);
                return TJGeneratorsHistoryManager.LoadHistoryForAsset(_targetAsset.guid);
            }

            return TJGeneratorsHistoryManager.LoadHistoryForAsset(GetCurrentAssetGuid());
        }

        protected override void ResetInputStateAfterModelChange()
        {
            var config = GetCurrentGeneratorConfig();
            ResetTextPromptIfHidden(config, ref textPrompt);
        }

        // ========== UI ==========

        protected override void DrawLeftPanelBody()
        {
            DrawInputSection();
            DrawReferenceImageSection();
            GUILayout.Space(CommonStyles.Space3);
            UIComponents.DrawGapLine();
            GUILayout.Space(CommonStyles.Space3);
            DrawConfigurationSection();
            GUILayout.Space(CommonStyles.Space3);
        }

        protected override bool CanStartGeneration => HasMaterialGenerationInput();

        private void DrawReferenceImageSection()
        {
            if (_currentGenerator == null)
                return;
            var genConfig = GetCurrentGeneratorConfig();
            if (genConfig == null || genConfig.texturePatternSelector == null || !genConfig.texturePatternSelector.enabled)
                return;

            var patterns = genConfig.texturePatternSelector.options;
            if (patterns == null || patterns.Count == 0)
                return;

            if (selectedTexturePattern != null && string.IsNullOrEmpty(materialReferenceImagePath))
                selectedTexturePattern = null;

            GUILayout.Space(CommonStyles.Space2);
            UIComponents.DrawSectionTitle(TJGeneratorsL10n.L("参考图片（可选）"), uppercase: false);
            GUILayout.Space(CommonStyles.Space2);
            UploadImageComponents.DrawLargeImageUpload(
                ref materialReferenceImagePath,
                ref materialReferenceImageThumb,
                ShowTexturePatternSelector,
                Repaint,
                () => { selectedTexturePattern = null; },
                TJGeneratorsL10n.L("选择模板"),
                onPickDone: (path, tex) =>
                {
                    materialReferenceImagePath = path;
                    materialReferenceImageThumb = tex;
                });
            GUILayout.Space(5);
        }

        private void ShowTexturePatternSelector()
        {
            if (_currentGenerator == null)
                return;
            var genConfig = GetCurrentGeneratorConfig();
            if (genConfig?.texturePatternSelector?.options == null)
            {
                TJLog.LogError("[TJGeneratorsMaterial] 纹理走势选择器配置为空");
                return;
            }

            TJGeneratorsTexturePatternSelectorWindow.ShowWindow(
                genConfig.texturePatternSelector.options,
                OnTexturePatternSelected,
                TJGeneratorsL10n.L("选择参考图片"),
                selectedTexturePattern
            );
        }

        private void OnTexturePatternSelected(MaterialTemplateOptionConfig pattern)
        {
            if (pattern == null)
            {
                selectedTexturePattern = null;
                TJLog.Log("[TJGeneratorsMaterial] 用户取消选择参考图片模板");
                ClearMaterialReferenceImage();
            }
            else
            {
                TJLog.Log($"[TJGeneratorsMaterial] 选择参考图片模板: {pattern.name}");

                string absolutePath = TJGeneratorsMaterialTemplateGenerator.GetAbsoluteTemplatePath(pattern.id);

                if (File.Exists(absolutePath))
                {
                    selectedTexturePattern = pattern;
                    if (materialReferenceImageThumb != null)
                    {
                        if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(materialReferenceImageThumb)))
                            DestroyImmediate(materialReferenceImageThumb);
                        materialReferenceImageThumb = null;
                    }

                    materialReferenceImagePath = absolutePath;
                    var tex2 = new Texture2D(2, 2);
                    if (tex2.LoadImage(File.ReadAllBytes(absolutePath)))
                        materialReferenceImageThumb = tex2;
                    else
                        DestroyImmediate(tex2);

                    TJLog.Log($"[TJGeneratorsMaterial] 已加载纹理图片: {pattern.id}");
                }
                else
                {
                    ErrorDialogUtils.ShowErrorDialog(
                        TJGeneratorsL10n.L("纹理图片不存在"),
                        TJGeneratorsL10n.L("纹理走势 '{0}' 的图片尚未生成。\r\n\r\n请通过菜单 'AI/开发/生成纹理走势模板图' 生成纹理图片。", pattern.name),
                        LogTag
                    );
                }
            }

            Repaint();
        }

        private void ClearMaterialReferenceImage()
        {
            materialReferenceImagePath = "";
            if (materialReferenceImageThumb != null)
            {
                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(materialReferenceImageThumb)))
                    DestroyImmediate(materialReferenceImageThumb);
                materialReferenceImageThumb = null;
            }
        }

        // ========== 历史 ==========

        protected override void DrawHistoryPanel(float panelWidth)
        {
            DrawStandardHistoryPanel(panelWidth, new StandardHistoryPanelOptions
            {
                AddPanelTopSpacing = true,
                GetLargePreviewTexture = GetPreviewTextureForHistoryItem,
                DrawTilePreview = DrawMaterialHistoryPreview,
                GetModelLabel = item => GetModelDisplayLabelFromIndex(item.modelVersion),
                ShowContextMenu = ShowHistoryContextMenu,
                DrawHistoryActions = () => DrawDefaultHistoryActions(showTileSizeSlider: true),
            });
        }

        private Texture2D GetPreviewTextureForHistoryItem(TJGeneratorsGenerationHistoryItem item)
        {
            if (item == null || item.isGenerating)
                return null;
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
                    EnqueuePreviewLoad(item.modelPath, absPath, false);
            }
            if (!string.IsNullOrEmpty(item.previewImageUrl)
                && urlPreviewCache.TryGetValue(item.previewImageUrl, out var urlTex)
                && urlTex != null)
                return urlTex;
            if (!item.isTextToModel
                && !string.IsNullOrEmpty(item.imagePath)
                && historyPreviewCache.TryGetValue(item.imagePath, out var up)
                && up != null)
                return up;
            return null;
        }

        private void DrawMaterialHistoryPreview(Rect rect, TJGeneratorsGenerationHistoryItem item)
        {
            if (item.isGenerating)
            {
                UIComponents.DrawLoadingSpinner(rect, CommonStyles.SmallGreyLabelStyle, Repaint);
                return;
            }
            Texture2D preview = GetPreviewTextureForHistoryItem(item);
            if (preview != null)
            {
                GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit);
                return;
            }
            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f, 1f));
            GUI.Label(
                new Rect(rect.x + rect.width / 4, rect.y + rect.height / 4, rect.width / 2, rect.height / 2),
                EditorGUIUtility.IconContent("d_Texture2D Icon"));
        }

        private void ShowHistoryContextMenu(int index)
        {
            var item = generationHistory[index];
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(HistoryApplyLabel), false, () => ApplyHistoryToAsset(index));
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在项目中显示")), false, () => ShowHistoryInProject(index));
            menu.AddSeparator("");
            if (!string.IsNullOrEmpty(item.modelPath))
                menu.AddItem(new GUIContent(TJGeneratorsL10n.L("在资源管理器中显示")), false, () => EditorUtility.RevealInFinder(item.modelPath));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(TJGeneratorsL10n.L("从历史记录中移除")), false, () =>
            {
                TJGeneratorsHistoryManager.RemoveFromHistory(item.modelPath);
                generationHistory = LoadGenerationHistory();
                if (selectedHistoryIndex >= generationHistory.Count)
                    selectedHistoryIndex = Mathf.Max(0, generationHistory.Count - 1);
                Repaint();
            });
            menu.ShowAsContext();
        }

        protected override void ApplyHistoryToAsset(int index)
        {
            if (index < 0 || index >= generationHistory.Count)
                return;
            var item = generationHistory[index];
            if (string.IsNullOrEmpty(item.modelPath) || !File.Exists(PathUtils.ToAbsoluteAssetPath(item.modelPath)))
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), TJGeneratorsL10n.L("该历史记录的纹理文件不存在。"), LogTag);
                return;
            }

            if (_targetAsset != null && _targetAsset.IsValid())
            {
                string materialPath = _targetAsset.GetPath();
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material != null)
                {
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(item.modelPath);
                    if (texture != null)
                    {
                        material.mainTexture = texture;
                        EditorUtility.SetDirty(material);
                        AssetDatabase.SaveAssets();
                        Selection.activeObject = material;
                        EditorGUIUtility.PingObject(material);
                        TJLog.Log($"[TJGeneratorsMaterial] 已将历史纹理应用到 {materialPath}");
                    }
                }
                else
                {
                    TJLog.LogWarning($"[TJGeneratorsMaterial] 绑定的资产不是 Material: {materialPath}");
                }
            }
            else
            {
                CreateMaterialAsset(item.modelPath, item.modelPath);
            }
            Repaint();
        }

        // ========== 生成 ==========

        private bool HasMaterialGenerationInput()
        {
            return !string.IsNullOrWhiteSpace(textPrompt)
                || !string.IsNullOrEmpty(materialReferenceImagePath);
        }

        protected override void OnStartGeneration()
        {
            if (_currentGenerator == null || !HasMaterialGenerationInput())
            {
                ErrorDialogUtils.ShowErrorDialog(TJGeneratorsL10n.L("错误"), MaterialStartBlockedMessage, LogTag);
                return;
            }

            EnsureTargetMaterial();
            MarkGenerationStarted();
            if (_currentGenerator is DynamicGenerator dynamicGen)
            {
                dynamicGen.SetTextPrompt(textPrompt);
                dynamicGen.SetImagePaths(
                    string.IsNullOrEmpty(materialReferenceImagePath)
                        ? null
                        : new List<string> { materialReferenceImagePath });
            }
            StartPipelineForCurrentGenerator();
        }

        private void EnsureTargetMaterial()
        {
            if (_targetAsset != null && _targetAsset.IsValid())
            {
                string path = _targetAsset.GetPath();
                if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
                    return;

                // Bound to a texture: prefer history-linked Material, else sibling .mat
                string resolved = TJGeneratorsHistoryManager.TryResolveMaterialPathForTexture(path);
                BindToOrCreateMaterial(
                    !string.IsNullOrEmpty(resolved) ? resolved : Path.ChangeExtension(path, ".mat"),
                    path);
                return;
            }

            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");

            BindToOrCreateMaterial("Assets/TJGenerators/New Material.mat");
        }

        /// <summary>
        /// Rebind window to an existing Material at candidatePath, or create one there.
        /// Unregisters the previous open-windows key before rebinding to avoid GUID leaks.
        /// </summary>
        private void BindToOrCreateMaterial(string candidatePath, string sourceTexturePath = null)
        {
            UnregisterFromOpenWindows();

            string materialPath = EnsureMaterialAssetAtPath(candidatePath, sourceTexturePath);

            _targetAsset = TJGeneratorsAssetReference.FromPath(materialPath);
            titleContent = new GUIContent(string.Format(
                TJGeneratorsL10n.L("TJGenerators 材质 - {0}"),
                Path.GetFileNameWithoutExtension(materialPath)));
            RegisterInOpenWindows();
            TJGeneratorsGenerationLabel.EnableLabel(_targetAsset);
            Repaint();
        }

        public override string GetAssetSavePath(PipelineMediaType type, ModelGeneratorBase generator)
        {
            if (type != PipelineMediaType.Texture)
                return null;
            return BuildHistoryTexturePath("Material_");
        }

        public override void OnAssetSaved(PipelineMediaType type, string savePath, ModelGeneratorBase generator)
        {
            if (type != PipelineMediaType.Texture)
                return;

            TJLog.Log($"{LogTag} OnAssetSaved: {savePath}");
            GeneratedTextureImportUtils.ConfigureImportedTexture(
                savePath, TextureImporterType.Default, alphaIsTransparency: true);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(savePath));

            ApplyTextureToMaterial(savePath);
            MarkGenerationCompleted();
        }

        private void ApplyTextureToMaterial(string texturePath)
        {
            try
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                if (texture == null)
                {
                    TJLog.LogWarning($"[TJGeneratorsMaterial] 无法加载纹理: {texturePath}");
                    return;
                }

                Material material = null;
                string materialPath = null;

                if (_targetAsset != null && _targetAsset.IsValid())
                {
                    materialPath = _targetAsset.GetPath();
                    material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                }

                if (material != null)
                {
                    TJMaterialShaderUtility.EnsureCompatibleSurfaceShader(material);
                    TJMaterialShaderUtility.AssignBaseColorTexture(material, texture);
                    EditorUtility.SetDirty(material);
                    AssetDatabase.SaveAssets();
                    TJLog.Log($"[TJGeneratorsMaterial] 已更新 Material 纹理: {materialPath}");

                    Selection.activeObject = material;
                    EditorGUIUtility.PingObject(material);
                }
                else
                {
                    CreateMaterialAsset(texturePath, texturePath);
                }
            }
            catch (Exception e)
            {
                TJLog.LogError($"[TJGeneratorsMaterial] 应用纹理到材质失败: {e.Message}");
            }
        }

        private void CreateMaterialAsset(string texturePath, string textureToShowPath)
        {
            try
            {
                string materialPath = Path.ChangeExtension(texturePath, ".mat");
                materialPath = AssetDatabase.GenerateUniqueAssetPath(materialPath);

                Shader shader = TJMaterialShaderUtility.ResolveSurfaceLitShader();
                if (shader == null)
                {
                    TJLog.LogError("[TJGeneratorsMaterial] 无法解析表面材质 Lit Shader，请检查渲染管线与 Shader 是否可用。");
                    return;
                }

                Material material = new Material(shader);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(textureToShowPath);
                TJMaterialShaderUtility.AssignBaseColorTexture(material, tex);

                AssetDatabase.CreateAsset(material, materialPath);
                AssetDatabase.SaveAssets();

                TJLog.Log($"[TJGeneratorsMaterial] Material 创建成功: {materialPath}");

                _targetAsset = TJGeneratorsAssetReference.FromPath(materialPath);
                RegisterInOpenWindows();
                titleContent = new GUIContent(string.Format(
                    TJGeneratorsL10n.L("TJGenerators 材质 - {0}"),
                    Path.GetFileNameWithoutExtension(materialPath)));

                var materialAsset = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (materialAsset != null)
                {
                    Selection.activeObject = materialAsset;
                    EditorGUIUtility.PingObject(materialAsset);
                }
            }
            catch (Exception e)
            {
                TJLog.LogError($"[TJGeneratorsMaterial] 创建 Material 失败: {e.Message}");
            }
        }
    }
}
#endif
