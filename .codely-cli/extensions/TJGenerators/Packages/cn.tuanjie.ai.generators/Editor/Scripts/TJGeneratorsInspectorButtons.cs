using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;
using TJGenerators.Utils;

namespace TJGenerators
{
    /// <summary>
    /// Inspector 标题栏「✦ AI 生成」按钮集成。
    /// </summary>
    [InitializeOnLoad]
    internal static class TJGeneratorsInspectorButtons
    {
        private static readonly List<Component> k_ComponentsCache = new List<Component>();
        private static GUIContent s_headerHelpContent;
        private static GUIStyle s_headerHelpStyle;

        static TJGeneratorsInspectorButtons()
        {
            Editor.finishedDefaultHeaderGUI += OnHeaderControlsGUI;
        }

        private static void DrawHeaderDocLink()
        {
            if (s_headerHelpContent == null)
                s_headerHelpContent = EditorGUIUtility.IconContent("_Help");
            s_headerHelpContent.tooltip = TJGeneratorsL10n.L("怎么用？查看使用文档");

            if (s_headerHelpStyle == null)
            {
                s_headerHelpStyle = new GUIStyle(GUIStyle.none)
                {
                    alignment = TextAnchor.MiddleCenter
                };
            }

            EditorGUILayout.BeginVertical(GUILayout.ExpandHeight(true));
            GUILayout.FlexibleSpace();
            GUILayout.Space(4f);

            if (GUILayout.Button(s_headerHelpContent, s_headerHelpStyle, GUILayout.Width(18f), GUILayout.Height(16f)))
                TJGeneratorsDocs.OpenDocumentation();

            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }

        private static void DrawAIGenerateHeaderButton(string tooltip, Action onClick)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            bool playBlocked = TJGeneratorsPlayModeGuard.IsActive;
            string tip = playBlocked ? TJGeneratorsPlayModeGuard.ShortHint : tooltip;
            using (new EditorGUI.DisabledScope(playBlocked))
            {
                if (GUILayout.Button(new GUIContent(TJGeneratorsL10n.L("✦ AI 生成"), tip)))
                    onClick();
            }

            DrawHeaderDocLink();
            EditorGUILayout.EndHorizontal();
        }

        private static void OnHeaderControlsGUI(Editor editor)
        {
            if (TryDrawTextureHeaderControls(editor)) return;
            if (TryDrawCubemapHeaderControls(editor)) return;
            if (TryDrawAudioHeaderControls(editor)) return;
            if (TryDrawVideoHeaderControls(editor)) return;
            if (TryDrawMaterialHeaderControls(editor)) return;
            if (TryDrawAnimationClipHeaderControls(editor)) return;
            if (TryDrawPrefabHeaderControls(editor)) return;

            TryDrawScenePrefabHeaderControls(editor);
        }

        private static bool TryGetTextureAssetPath(Editor editor, out string texturePath)
        {
            switch (editor.target)
            {
                case Texture2D texture:
                    texturePath = AssetDatabase.GetAssetPath(texture);
                    break;
                case Sprite sprite:
                    texturePath = AssetDatabase.GetAssetPath(sprite);
                    break;
                case TextureImporter textureImporter:
                    texturePath = textureImporter.assetPath;
                    break;
                default:
                    texturePath = null;
                    break;
            }

            return !string.IsNullOrEmpty(texturePath);
        }

        private static bool IsRasterImagePath(string path)
        {
            return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryDrawTextureHeaderControls(Editor editor)
        {
            if (!TryGetTextureAssetPath(editor, out var texturePath))
                return false;

            var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null)
                return false;

            if (importer.textureType == TextureImporterType.Sprite)
            {
                var spriteAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(texturePath);
                if (!TJGeneratorsGenerationLabel.HasSpriteSheetLabel(spriteAsset))
                {
                    DrawAIGenerateHeaderButton(
                        TJGeneratorsL10n.L("使用 TJGenerators AI 生成2D精灵"),
                        () => TJGeneratorsSpriteWindow.OpenForAsset(texturePath));
                }

                return true;
            }

            if (importer.textureType == TextureImporterType.Default
                && importer.textureShape != TextureImporterShape.TextureCube
                && IsRasterImagePath(texturePath))
            {
                if (IsMaterialGenerationTexture(texturePath))
                {
                    DrawAIGenerateHeaderButton(
                        TJGeneratorsL10n.L("使用 TJGenerators AI 生成表面材质"),
                        () => TJGeneratorsMaterialWindow.OpenForMaterialTextureAsset(texturePath));
                }
                else
                {
                    var imageAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(texturePath);
                    if (TJGeneratorsGenerationLabel.HasSpriteSheetLabel(imageAsset))
                    {
                        DrawAIGenerateHeaderButton(
                            TJGeneratorsL10n.L("使用 TJGenerators AI 生成2D精灵表序列帧"),
                            () => TJGeneratorsSpriteSheetSequenceWindow.OpenForAsset(texturePath));
                    }
                    else
                    {
                        DrawAIGenerateHeaderButton(
                            TJGeneratorsL10n.L("使用 TJGenerators AI 生成图片"),
                            () => TJGeneratorsImageWindow.OpenForAsset(texturePath));
                    }
                }

                return true;
            }

            if (importer.textureShape == TextureImporterShape.TextureCube)
            {
                DrawAIGenerateHeaderButton(
                    TJGeneratorsL10n.L("使用 TJGenerators AI 生成天空盒"),
                    () => TJGeneratorsSkyboxWindow.OpenForAsset(texturePath));
                return true;
            }

            return false;
        }

        private static bool TryDrawCubemapHeaderControls(Editor editor)
        {
            if (!(editor.target is Cubemap cubemap))
                return false;

            string path = AssetDatabase.GetAssetPath(cubemap);
            if (!string.IsNullOrEmpty(path))
            {
                DrawAIGenerateHeaderButton(
                    TJGeneratorsL10n.L("使用 TJGenerators AI 生成天空盒"),
                    () => TJGeneratorsSkyboxWindow.OpenForAsset(path));
            }

            return true;
        }

        private static bool TryGetAudioAssetPath(Editor editor, out string audioPath)
        {
            switch (editor.target)
            {
                case UnityEngine.AudioClip audioClip:
                    audioPath = AssetDatabase.GetAssetPath(audioClip);
                    break;
                case UnityEditor.AudioImporter audioImporter:
                    audioPath = audioImporter.assetPath;
                    break;
                default:
                    audioPath = null;
                    break;
            }

            return !string.IsNullOrEmpty(audioPath);
        }

        private static bool TryDrawAudioHeaderControls(Editor editor)
        {
            if (!TryGetAudioAssetPath(editor, out var audioPath))
                return false;

            if (AssetImporter.GetAtPath(audioPath) is UnityEditor.AudioImporter)
            {
                DrawAIGenerateHeaderButton(
                    TJGeneratorsL10n.L("使用 TJGenerators AI 生成音频"),
                    () => TJGeneratorsMusicWindow.OpenForAsset(audioPath));
            }

            return true;
        }

        private static bool TryGetVideoAssetPath(Editor editor, out string videoPath)
        {
            switch (editor.target)
            {
                case VideoClip videoClip:
                    videoPath = AssetDatabase.GetAssetPath(videoClip);
                    break;
                case AssetImporter assetImporter when IsVideoAssetPath(assetImporter.assetPath):
                    videoPath = assetImporter.assetPath;
                    break;
                default:
                    videoPath = null;
                    break;
            }

            return !string.IsNullOrEmpty(videoPath);
        }

        private static bool TryDrawVideoHeaderControls(Editor editor)
        {
            if (!TryGetVideoAssetPath(editor, out var videoPath))
                return false;

            var videoAsset = AssetDatabase.LoadMainAssetAtPath(videoPath);
            if (videoAsset != null && TJGeneratorsGenerationLabel.HasLabel(videoAsset))
            {
                DrawAIGenerateHeaderButton(
                    TJGeneratorsL10n.L("编辑该 TJGenerators 视频并查看历史记录"),
                    () => TJGeneratorsVideoWindow.OpenForAsset(videoPath));
            }

            return true;
        }

        private static bool TryDrawMaterialHeaderControls(Editor editor)
        {
            if (!(editor.target is Material material))
                return false;

            string path = AssetDatabase.GetAssetPath(material);
            if (!string.IsNullOrEmpty(path))
            {
                DrawAIGenerateHeaderButton(
                    TJGeneratorsL10n.L("使用 TJGenerators AI 生成表面材质"),
                    () => TJGeneratorsMaterialWindow.OpenForMaterialAsset(path));
            }

            return true;
        }

        private static bool TryDrawAnimationClipHeaderControls(Editor editor)
        {
            if (!(editor.target is AnimationClip animationClip))
                return false;

            string clipPath = AssetDatabase.GetAssetPath(animationClip);
            if (!string.IsNullOrEmpty(clipPath)
                && clipPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            {
                var clipAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(clipPath);
                if (!TJGeneratorsGenerationLabel.HasSpriteSheetLabel(clipAsset))
                {
                    DrawAIGenerateHeaderButton(
                        TJGeneratorsL10n.L("使用 TJGenerators AI 生成2D动作序列帧"),
                        () => TJGeneratorsSpriteSequenceWindow.OpenForAsset(clipPath));
                }
            }

            return true;
        }

        private static bool TryDrawPrefabHeaderControls(Editor editor)
        {
            if (!EditorUtility.IsPersistent(editor.target))
                return false;

            if (!OnAssetGenerationValidation(editor.targets))
                return true;

            DrawAIGenerateHeaderButton(
                TJGeneratorsL10n.L("使用 TJGenerators AI 生成3D模型"),
                () => OnAssetGenerationRequest(editor.targets));

            return true;
        }

        private static void TryDrawScenePrefabHeaderControls(Editor editor)
        {
            if (!(editor.target is GameObject sceneObject) || !OnScenePrefabInstanceValidation(sceneObject))
                return;

            DrawAIGenerateHeaderButton(
                TJGeneratorsL10n.L("使用 TJGenerators AI 生成3D模型到对应预制体"),
                () => OnScenePrefabInstanceGenerationRequest(sceneObject));
        }

        private static bool IsVideoAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase);
        }

        private static bool OnScenePrefabInstanceValidation(GameObject sceneObject)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(sceneObject))
                return false;

            var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(sceneObject);
            if (prefabRoot == null)
                return false;

            if (prefabRoot != sceneObject)
                return false;

            var prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(prefabRoot);
            if (prefabAsset == null)
                return false;

            string prefabPath = AssetDatabase.GetAssetPath(prefabAsset);
            if (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".prefab"))
                return false;

            if (TJGeneratorsGenerationLabel.HasLabel(prefabAsset))
                return true;

            if (IsEmptyGameObject(prefabAsset))
                return true;

            return false;
        }

        private static void OnScenePrefabInstanceGenerationRequest(GameObject sceneObject)
        {
            var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(sceneObject);
            if (prefabRoot == null)
                return;

            var prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(prefabRoot);
            if (prefabAsset == null)
                return;

            string prefabPath = AssetDatabase.GetAssetPath(prefabAsset);
            if (!string.IsNullOrEmpty(prefabPath) && prefabPath.EndsWith(".prefab"))
                TJGenerators3DModelWindow.OpenForAsset(prefabPath);
        }

        private static bool OnAssetGenerationValidation(IEnumerable<UnityEngine.Object> objects)
        {
            foreach (var obj in objects)
            {
                if (AssetDatabase.IsOpenForEdit(obj) && TryGetValidPrefabPath(obj, out _))
                {
                    if (TJGeneratorsGenerationLabel.HasLabel(obj))
                        return true;

                    if (obj is GameObject gameObject && IsEmptyGameObject(gameObject))
                        return true;
                }
            }
            return false;
        }

        private static void OnAssetGenerationRequest(IEnumerable<UnityEngine.Object> objects)
        {
            foreach (var obj in objects)
            {
                if (TryGetValidPrefabPath(obj, out var validPath))
                    TJGenerators3DModelWindow.OpenForAsset(validPath);
            }
        }

        private static bool TryGetValidPrefabPath(UnityEngine.Object obj, out string path)
        {
            path = null;

            if (obj is GameObject)
                path = AssetDatabase.GetAssetPath(obj);

            if (string.IsNullOrEmpty(path))
                path = AssetDatabase.GetAssetPath(obj);

            if (!string.IsNullOrEmpty(path) && path.EndsWith(".prefab"))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                return prefab != null;
            }

            return false;
        }

        private static bool IsEmptyGameObject(GameObject gameObject)
        {
            gameObject.GetComponents(k_ComponentsCache);
            return k_ComponentsCache.Count == 1 && k_ComponentsCache[0] is Transform;
        }

        private static bool IsMaterialGenerationTexture(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath))
                return false;

            string normalized = texturePath.Replace('\\', '/');
            string matPath = Path.ChangeExtension(normalized, ".mat");
            if (AssetDatabase.LoadAssetAtPath<Material>(matPath) != null)
                return true;

            return normalized.IndexOf("/TJGenerators/History/Material_", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
