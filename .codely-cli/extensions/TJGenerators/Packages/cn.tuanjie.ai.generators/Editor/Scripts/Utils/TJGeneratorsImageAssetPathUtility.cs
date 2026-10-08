#if UNITY_EDITOR
using System;
using System.IO;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 图片占位符路径工具：生成结果常为 .jpg，但占位文件可能是 .png，与 <see cref="UnityEditor.AssetDatabase.GenerateUniqueAssetPath"/>
    /// 只检查单一扩展名不同，本工具跨所有常见图片扩展名检查同名占用，避免创建与已有同基名文件冲突的占位符。
    /// </summary>
    public static class TJGeneratorsImageAssetPathUtility
    {
        /// <summary>参考图/多视图等用户上传入口允许的文件扩展名（不含 webp 等）。</summary>
        private static readonly string[] k_ReferenceImageUploadExtensions =
        {
            ".jpg",
            ".jpeg",
            ".png",
        };

        /// <summary><see cref="UnityEditor.EditorUtility.OpenFilePanel"/> 扩展名过滤，与 <see cref="k_ReferenceImageUploadExtensions"/> 一致。</summary>
        public const string ReferenceImagePickFilter = "jpg,png,jpeg";

        private static readonly string[] k_ImageFileExtensions =
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".webp",
            ".tga",
            ".bmp",
            ".tif",
            ".tiff",
            ".exr",
            ".hdr",
            ".psd",
        };

        /// <summary>用户上传参考图/多视图槽位是否允许该路径（仅 png/jpg/jpeg）。</summary>
        public static bool IsSupportedReferenceImageUploadPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
                return false;

            for (int i = 0; i < k_ReferenceImageUploadExtensions.Length; i++)
            {
                if (ext.Equals(k_ReferenceImageUploadExtensions[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 在目标目录下生成唯一的图片占位路径：若同基名已存在任意常见图片扩展名，则递增序号（与 Unity「名称」「名称 1」「名称 2」规则一致）。
        /// 返回路径的扩展名与 <paramref name="preferredAssetPath"/> 一致。
        /// </summary>
        public static string GenerateUniqueImagePath(string preferredAssetPath)
        {
            if (string.IsNullOrEmpty(preferredAssetPath))
                preferredAssetPath = "Assets/New Image.jpg";

            return PathUtils.GenerateUniqueCrossExtensionPath(preferredAssetPath, k_ImageFileExtensions);
        }
    }
}
#endif
