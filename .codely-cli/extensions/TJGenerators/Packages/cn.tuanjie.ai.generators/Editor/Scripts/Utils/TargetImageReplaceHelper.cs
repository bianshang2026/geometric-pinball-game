#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 将源图片复制到绑定目标资产的通用逻辑（Image / SpriteSheet 等窗口共用）。
    /// 处理扩展名变化时的旧占位删除、纹理句柄释放与导入配置。
    /// </summary>
    public static class TargetImageReplaceHelper
    {
        public static void ReleaseTextureHandlesForTargetOverwrite(
            string targetAssetPath,
            ref Texture2D previewTexture,
            Dictionary<string, Texture2D> historyPreviewCache,
            Action<string> releaseExtraHandles = null)
        {
            if (string.IsNullOrEmpty(targetAssetPath))
                return;

            targetAssetPath = targetAssetPath.Replace('\\', '/');
            releaseExtraHandles?.Invoke(targetAssetPath);

            if (previewTexture != null)
            {
                string previewPath = AssetDatabase.GetAssetPath(previewTexture).Replace('\\', '/');
                if (string.Equals(previewPath, targetAssetPath, StringComparison.OrdinalIgnoreCase))
                    previewTexture = null;
            }

            if (historyPreviewCache == null || historyPreviewCache.Count == 0)
                return;

            var keysToRemove = new List<string>();
            foreach (var kv in historyPreviewCache)
            {
                if (string.Equals(kv.Key.Replace('\\', '/'), targetAssetPath, StringComparison.OrdinalIgnoreCase))
                {
                    keysToRemove.Add(kv.Key);
                    continue;
                }

                if (kv.Value == null)
                    continue;

                string cachedPath = AssetDatabase.GetAssetPath(kv.Value).Replace('\\', '/');
                if (string.Equals(cachedPath, targetAssetPath, StringComparison.OrdinalIgnoreCase))
                    keysToRemove.Add(kv.Key);
            }

            foreach (var key in keysToRemove)
                historyPreviewCache.Remove(key);
        }

        public static void ConfigureDefaultTexture(string assetPath)
        {
            GeneratedTextureImportUtils.ConfigureImportedTexture(
                assetPath, TextureImporterType.Default, alphaIsTransparency: true);
        }

        /// <summary>
        /// 将 <paramref name="sourceAssetPath"/> 复制到当前目标资产。
        /// 若扩展名变化则删除旧占位文件；<paramref name="onTargetExtensionChanged"/> 负责窗口注册等 UI 侧更新。
        /// </summary>
        public static bool ReplaceTargetImageFromSource(
            string sourceAssetPath,
            string okLogVerb,
            string logTag,
            ref TJGeneratorsAssetReference targetAsset,
            ref Texture2D previewTexture,
            Dictionary<string, Texture2D> historyPreviewCache,
            Func<string, TJGeneratorsAssetReference> ensureTargetAsset,
            Action<string> configureImportedTexture,
            Action<string, string> onTargetExtensionChanged,
            Action<string> releaseExtraHandles,
            out string errorMessage)
        {
            errorMessage = null;

            string ext = Path.GetExtension(sourceAssetPath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext))
                ext = ".png";

            targetAsset = ensureTargetAsset?.Invoke(ext);
            if (targetAsset == null || !targetAsset.IsValid())
            {
                errorMessage = TJGeneratorsL10n.L("目标图片无效");
                return false;
            }

            string oldTargetGuid = targetAsset.guid;
            string originalPath = targetAsset.GetPath();
            string targetPath = Path.ChangeExtension(originalPath, ext);
            string sourceAbsolute = PathUtils.ToAbsoluteAssetPath(sourceAssetPath);
            string targetAbsolute = PathUtils.ToAbsoluteAssetPath(targetPath);

            try
            {
                if (string.IsNullOrEmpty(sourceAbsolute) || !File.Exists(sourceAbsolute))
                {
                    errorMessage = TJGeneratorsL10n.L("源图片文件不存在");
                    return false;
                }

                string targetDir = Path.GetDirectoryName(targetAbsolute);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                ReleaseTextureHandlesForTargetOverwrite(
                    originalPath,
                    ref previewTexture,
                    historyPreviewCache,
                    releaseExtraHandles);

                File.Copy(sourceAbsolute, targetAbsolute, true);
                AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                configureImportedTexture?.Invoke(targetPath);

                TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(targetPath));
                TJLog.Log($"{logTag} {okLogVerb} {targetPath}");

                if (!string.Equals(originalPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    string originalAbsolute = PathUtils.ToAbsoluteAssetPath(originalPath);
                    if (File.Exists(originalAbsolute))
                    {
                        AssetDatabase.DeleteAsset(originalPath);
                        TJLog.Log($"{logTag} 已删除旧占位文件: {originalPath}");
                    }

                    targetAsset = TJGeneratorsAssetReference.FromPath(targetPath);
                    onTargetExtensionChanged?.Invoke(oldTargetGuid, targetPath);
                }

                var newTex = AssetDatabase.LoadAssetAtPath<Texture2D>(targetPath);
                if (newTex != null)
                {
                    previewTexture = newTex;
                    Selection.activeObject = newTex;
                    EditorGUIUtility.PingObject(newTex);
                }

                TJGeneratorsGenerationLabel.EnableLabel(targetAsset);
                return true;
            }
            catch (Exception e)
            {
                errorMessage = e.Message;
                TJLog.LogWarning($"{logTag} 复制到目标图片失败: {e.Message}");
                return false;
            }
        }
    }
}
#endif
