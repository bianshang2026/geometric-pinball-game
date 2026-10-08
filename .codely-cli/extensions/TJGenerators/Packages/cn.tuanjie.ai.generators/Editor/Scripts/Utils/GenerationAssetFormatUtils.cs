#if UNITY_EDITOR
using System;
using System.IO;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 根据魔数 / URL / 配置推断生成资产的落盘扩展名（视频、音频、图片、3D）。
    /// </summary>
    public static class GenerationAssetFormatUtils
    {
        public static string GetVideoExtensionFromData(byte[] data, string url)
        {
            if (data != null && data.Length > 8)
            {
                if (data.Length > 11 && data[4] == 'f' && data[5] == 't' && data[6] == 'y' && data[7] == 'p')
                    return ".mp4";
            }

            if (!string.IsNullOrEmpty(url))
            {
                if (url.Contains(".mp4")) return ".mp4";
                if (url.Contains(".webm")) return ".webm";
                if (url.Contains(".mov")) return ".mov";
            }

            return ".mp4";
        }

        public static string GetAudioExtensionFromData(
            byte[] data,
            string url,
            string configuredAudioFormat
        )
        {
            string normalized = TJGeneratorsAudioAssetPathUtility.NormalizeImportedAudioFileExtension(
                configuredAudioFormat
            );
            string fallbackDot = "." + normalized;

            if (data == null || data.Length < 16)
                return FallbackAudioExtensionFromUrl(url, fallbackDot);

            if (
                data.Length >= 12
                && data[0] == 'R'
                && data[1] == 'I'
                && data[2] == 'F'
                && data[3] == 'F'
                && data[8] == 'W'
                && data[9] == 'A'
                && data[10] == 'V'
                && data[11] == 'E'
            )
                return ".wav";

            if (
                data.Length >= 4
                && data[0] == 'f'
                && data[1] == 'L'
                && data[2] == 'a'
                && data[3] == 'C'
            )
                return ".flac";

            if (
                data.Length >= 4
                && data[0] == 'O'
                && data[1] == 'g'
                && data[2] == 'g'
                && data[3] == 'S'
            )
                return ".ogg";

            if (data.Length >= 3 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
                return ".mp3";
            if ((data[0] & 0xFF) == 0xFF && (data[1] & 0xE0) == 0xE0)
                return ".mp3";

            if (
                data.Length > 11
                && data[4] == 'f'
                && data[5] == 't'
                && data[6] == 'y'
                && data[7] == 'p'
            )
                return ".mp4";

            if (
                data.Length >= 12
                && data[0] == 'F'
                && data[1] == 'O'
                && data[2] == 'R'
                && data[3] == 'M'
                && data[8] == 'A'
                && data[9] == 'I'
                && data[10] == 'F'
                && data[11] == 'F'
            )
                return ".aiff";

            return FallbackAudioExtensionFromUrl(url, fallbackDot);
        }

        private static string FallbackAudioExtensionFromUrl(string url, string fallbackDot)
        {
            if (string.IsNullOrEmpty(url))
                return fallbackDot;
            try
            {
                string pathPart = url;
                int q = pathPart.IndexOf('?');
                if (q >= 0)
                    pathPart = pathPart.Substring(0, q);
                string ext = Path.GetExtension(pathPart);
                if (string.IsNullOrEmpty(ext))
                    return fallbackDot;
                ext = ext.ToLowerInvariant();
                if (ext == ".mpeg")
                    return ".mp3";
                return ext;
            }
            catch
            {
                return fallbackDot;
            }
        }

        public static string GetExtensionFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return null;

            int queryIndex = url.IndexOf('?');
            if (queryIndex > 0)
                url = url.Substring(0, queryIndex);

            string[] extensions = { ".fbx", ".obj", ".zip" };
            foreach (var ext in extensions)
            {
                if (url.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return ext;
            }

            return null;
        }

        public static string GetImageExtensionFromData(byte[] imageData, string url)
        {
            if (imageData != null && imageData.Length >= 8)
            {
                if (imageData[0] == 0x89 && imageData[1] == 0x50 && imageData[2] == 0x4E && imageData[3] == 0x47)
                    return ".png";

                if (imageData[0] == 0xFF && imageData[1] == 0xD8 && imageData[2] == 0xFF)
                    return ".jpg";

                if (imageData[0] == 0x47 && imageData[1] == 0x49 && imageData[2] == 0x46 && imageData[3] == 0x38)
                    return ".gif";

                if (imageData[0] == 0x52 && imageData[1] == 0x49 && imageData[2] == 0x46 && imageData[3] == 0x46 &&
                    imageData.Length >= 12 && imageData[8] == 0x57 && imageData[9] == 0x45 && imageData[10] == 0x42 && imageData[11] == 0x50)
                    return ".webp";
            }

            if (!string.IsNullOrEmpty(url))
            {
                int queryIndex = url.IndexOf('?');
                if (queryIndex > 0)
                    url = url.Substring(0, queryIndex);

                string[] imageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
                foreach (var ext in imageExtensions)
                {
                    if (url.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        return ext == ".jpeg" ? ".jpg" : ext;
                }
            }

            return ".png";
        }
    }
}
#endif
