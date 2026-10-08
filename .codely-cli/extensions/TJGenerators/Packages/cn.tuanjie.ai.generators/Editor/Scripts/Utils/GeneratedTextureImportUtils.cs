#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 生成贴图落盘/导入辅助：将索引色 PNG 展开为真正的 RGBA32，并锁定导入格式避免 DXT 压缩破坏 alpha。
    /// </summary>
    public static class GeneratedTextureImportUtils
    {
        /// <summary>
        /// 若 <paramref name="imageData"/> 为 PNG，则解码后重编码为 RGBA32 PNG（消除调色板/索引色）；
        /// 非 PNG 原样返回。
        /// </summary>
        public static byte[] EnsureRgba32PngBytes(byte[] imageData)
        {
            if (imageData == null || imageData.Length < 8)
                return imageData;

            if (!IsPng(imageData))
                return imageData;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(imageData))
                    return imageData;

                byte[] rgbaPng = tex.EncodeToPNG();
                return rgbaPng != null && rgbaPng.Length > 0 ? rgbaPng : imageData;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// 配置生成贴图导入器：启用 alpha 透明，并将默认/Standalone 平台锁定为未压缩 RGBA32。
        /// </summary>
        public static void ConfigureImportedTexture(
            string assetPath,
            TextureImporterType textureType,
            bool alphaIsTransparency = true)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return;

            importer.textureType = textureType;
            if (textureType == TextureImporterType.Sprite)
            {
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 100f;
            }

            importer.alphaIsTransparency = alphaIsTransparency;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.crunchedCompression = false;

            var defaults = importer.GetDefaultPlatformTextureSettings();
            defaults.format = TextureImporterFormat.RGBA32;
            defaults.textureCompression = TextureImporterCompression.Uncompressed;
            defaults.crunchedCompression = false;
            importer.SetPlatformTextureSettings(defaults);

            // Editor / Standalone 常覆盖为 DXT5；显式锁定，避免索引色 PNG 经块压缩后 alpha 异常。
            var standalone = importer.GetPlatformTextureSettings("Standalone");
            standalone.name = "Standalone";
            standalone.overridden = true;
            standalone.format = TextureImporterFormat.RGBA32;
            standalone.textureCompression = TextureImporterCompression.Uncompressed;
            standalone.crunchedCompression = false;
            if (standalone.maxTextureSize <= 0)
                standalone.maxTextureSize = importer.maxTextureSize > 0 ? importer.maxTextureSize : 2048;
            importer.SetPlatformTextureSettings(standalone);

            importer.SaveAndReimport();
        }

        /// <summary>
        /// 收集多图分层产物：<paramref name="firstPath"/>（第 0 层）+ 同目录
        /// <c>{basename}_1.ext</c> … <c>{basename}_{N-1}.ext</c>。
        /// </summary>
        public static List<string> CollectIndexedSiblingPaths(string firstPath, int expectedCount)
        {
            var paths = new List<string>();
            if (string.IsNullOrEmpty(firstPath))
                return paths;

            firstPath = firstPath.Replace('\\', '/');
            paths.Add(firstPath);

            string dir = Path.GetDirectoryName(firstPath)?.Replace('\\', '/');
            string baseName = Path.GetFileNameWithoutExtension(firstPath);
            string ext = Path.GetExtension(firstPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName))
                return paths;

            int max = Math.Max(expectedCount, 1);
            for (int i = 1; i < max; i++)
            {
                string candidate = $"{dir}/{baseName}_{i}{ext}";
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(candidate) != null
                    || File.Exists(PathUtils.ToAbsoluteAssetPath(candidate)))
                {
                    paths.Add(candidate);
                    continue;
                }

                // UniqueAssetPath may have appended " 1" etc.; try loose search.
                string[] guids = AssetDatabase.FindAssets(baseName + "_" + i, new[] { dir });
                bool found = false;
                foreach (string guid in guids)
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid)?.Replace('\\', '/');
                    if (string.IsNullOrEmpty(p)) continue;
                    string fn = Path.GetFileNameWithoutExtension(p);
                    if (MatchesIndexedSiblingFileName(fn, baseName, i))
                    {
                        paths.Add(p);
                        found = true;
                        break;
                    }
                }

                if (!found)
                    break;
            }

            return paths;
        }

        /// <summary>
        /// 将分层产物配置为 Default + RGBA32 + alpha（跳过已配置过的第 0 层亦可重复调用）。
        /// </summary>
        public static void ConfigureLayerTextures(
            IList<string> layerPaths,
            TextureImporterType textureType = TextureImporterType.Default,
            bool alphaIsTransparency = true)
        {
            if (layerPaths == null || layerPaths.Count == 0)
                return;

            for (int i = 0; i < layerPaths.Count; i++)
            {
                string path = layerPaths[i];
                if (string.IsNullOrEmpty(path))
                    continue;
                ConfigureImportedTexture(path, textureType, alphaIsTransparency);
            }
        }

        /// <summary>
        /// Matches <c>{baseName}_{index}</c> or UniqueAssetPath variants like <c>{baseName}_{index} 1</c>.
        /// Avoids treating <c>_10</c> as a match for index <c>1</c>.
        /// </summary>
        internal static bool MatchesIndexedSiblingFileName(string fileNameWithoutExt, string baseName, int index)
        {
            if (string.IsNullOrEmpty(fileNameWithoutExt) || string.IsNullOrEmpty(baseName) || index < 0)
                return false;

            string prefix = baseName + "_" + index;
            if (!fileNameWithoutExt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            if (fileNameWithoutExt.Length == prefix.Length)
                return true;

            return fileNameWithoutExt[prefix.Length] == ' ';
        }

        private static bool IsPng(byte[] data)
        {
            return data[0] == 0x89
                && data[1] == 0x50
                && data[2] == 0x4E
                && data[3] == 0x47;
        }
    }
}
#endif
