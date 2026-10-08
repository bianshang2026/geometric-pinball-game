#if UNITY_EDITOR
using System;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 音频占位符路径：文生音频（如火山）常为 .mp4，音效为 .mp3 等，与占位 .wav 不同扩展名但同名。
    /// <see cref="UnityEditor.AssetDatabase.GenerateUniqueAssetPath"/> 只检查单一扩展名，会误判「基名未占用」而重复命名。
    /// </summary>
    public static class TJGeneratorsAudioAssetPathUtility
    {
        private static readonly string[] k_AudioFileExtensions =
        {
            ".wav",
            ".mp4",
            ".m4a",
            ".mp3",
            ".ogg",
            ".aac",
            ".flac",
            ".aif",
            ".aiff",
            ".wma",
        };

        /// <summary>
        /// 在目标目录下生成唯一的音频路径：若同基名已存在任意常见音频扩展名，则递增序号（与 Unity「名称」「名称 1」「名称 2」规则一致）。
        /// 返回路径的扩展名与 <paramref name="preferredAssetPath"/> 一致。
        /// </summary>
        public static string GenerateUniqueAudioPath(string preferredAssetPath)
        {
            if (string.IsNullOrEmpty(preferredAssetPath))
                preferredAssetPath = "Assets/New AudioClip.wav";

            return PathUtils.GenerateUniqueCrossExtensionPath(
                preferredAssetPath.Replace('\\', '/'),
                k_AudioFileExtensions
            );
        }

        /// <summary>
        /// 在目标目录下生成唯一的 .wav 占位路径：若同基名已存在任意常见音频扩展名，则递增序号（与 Unity「名称」「名称 1」「名称 2」规则一致）。
        /// </summary>
        public static string GenerateUniquePlaceholderWavPath(string preferredAssetPath)
        {
            if (string.IsNullOrEmpty(preferredAssetPath))
                preferredAssetPath = "Assets/New AudioClip.wav";

            string stem = System.IO.Path.GetFileNameWithoutExtension(preferredAssetPath);
            if (string.IsNullOrEmpty(stem))
                stem = "New AudioClip";
            string directory = System.IO.Path.GetDirectoryName(preferredAssetPath.Replace('\\', '/'));
            if (string.IsNullOrEmpty(directory))
                directory = "Assets";

            return GenerateUniqueAudioPath($"{directory}/{stem}.wav");
        }

        /// <summary>
        /// 将配置/API 侧的音频格式规范为落盘扩展名（不含路径点），与后端 URL 扩展名保持一致。
        /// 支持 plain 扩展名（mp3/wav）以及 fal.ai sound-effect 枚举（mp3_44100_128 / pcm_44100）。
        /// </summary>
        public static string NormalizeImportedAudioFileExtension(string audioFormat)
        {
            if (string.IsNullOrWhiteSpace(audioFormat))
                return "wav";

            string fmt = audioFormat.Trim().TrimStart('.').ToLowerInvariant();

            // fal.ai enums are codec_sampleRate[_bitrate] — map to a real file extension.
            if (fmt.StartsWith("mp3", StringComparison.Ordinal))
                return "mp3";
            if (fmt.StartsWith("pcm", StringComparison.Ordinal)
                || fmt.StartsWith("ulaw", StringComparison.Ordinal)
                || fmt.StartsWith("alaw", StringComparison.Ordinal))
                return "wav";
            if (fmt.StartsWith("opus", StringComparison.Ordinal))
                return "ogg";

            return fmt;
        }
    }
}
#endif
