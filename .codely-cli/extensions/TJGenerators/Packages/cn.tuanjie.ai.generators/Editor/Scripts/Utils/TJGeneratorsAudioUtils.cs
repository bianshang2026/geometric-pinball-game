#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 音频资产工具方法：创建合法 WAV 占位文件并导入为 AudioClip。
    /// </summary>
    public static class TJGeneratorsAudioUtils
    {
        /// <summary>
        /// 在指定路径创建最短合法静音 WAV 并导入为 AudioClip，避免 Unity/FSBTool 将零长度或零采样视为无效。
        /// 生成时后端返回文生音频多为 MP4、音效多为 MP3 等，将保存到同基名、对应扩展名的文件。
        /// </summary>
        public static string CreateBlankAudioClip(string path)
        {
            path = Path.ChangeExtension(path, ".wav");
            string absolutePath = PathUtils.ToAbsoluteAssetPath(path);
            string directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            byte[] wavBytes = CreateShortestValidWav();
            File.WriteAllBytes(absolutePath, wavBytes);
            ImportAudioClip(path);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));
            return path;
        }

        /// <summary>
        /// 在指定路径写入 LAME 编码的最短静音 MP3 并导入为 AudioClip。
        /// 手写帧头 + 全零主数据不是合法 MPEG 码流，Unity 6 FSBTool 会报 Failed decoding audio clip。
        /// </summary>
        public static string CreateBlankMp3Clip(string path)
        {
            path = Path.ChangeExtension(path, ".mp3");
            string absolutePath = PathUtils.ToAbsoluteAssetPath(path);
            string directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllBytes(absolutePath, CreateShortestValidMp3());
            ImportAudioClip(path);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));
            return path;
        }

        /// <summary>
        /// LAME 编码的约 26ms 单声道静音（32 kbps）。需为真实编码器输出，FSBTool 才能解码。
        /// </summary>
        private static byte[] CreateShortestValidMp3()
        {
            // LAME 3.100, ~26ms mono silence @ 32 kbps (313 bytes)
            return Convert.FromBase64String(
                "//NAxAAAAANIAAAAAExBTUUzLjEwMFVVVVVVVVVVVVVMQU1FMy4xMDBVVVVVVVVV" +
                "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
                "VVVVVVVVVVX/80LEWwAAA0gAAAAAVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
                "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
                "VVVVVVVVVVVVVVVVVVVVVVX/80DEpAAAA0gAAAAAVVVVVVVVVVVVVVVVVVVVVVVV" +
                "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV" +
                "VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVQ==");
        }

        /// <summary>
        /// 生成最短合法 WAV：44 字节头 + 最少静音采样（若干 16-bit 0），满足 FSBTool 非零长度要求。
        /// </summary>
        private static byte[] CreateShortestValidWav()
        {
            const int sampleRate = 44100;
            const short numChannels = 1;
            const short bitsPerSample = 16;
            int byteRate = sampleRate * numChannels * (bitsPerSample / 8);
            short blockAlign = (short)(numChannels * (bitsPerSample / 8));
            const int numSamples = 256;
            int dataSize = numSamples * numChannels * (bitsPerSample / 8);
            int chunkSize = 36 + dataSize;
            int totalSize = 44 + dataSize;
            var buffer = new byte[totalSize];
            int offset = 0;
            void Write(byte[] src) { for (int i = 0; i < src.Length; i++) buffer[offset++] = src[i]; }
            void WriteLe(int value, int bytes)
            {
                for (int i = 0; i < bytes; i++) { buffer[offset++] = (byte)(value & 0xFF); value >>= 8; }
            }
            Write(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            WriteLe(chunkSize, 4);
            Write(new byte[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            Write(new byte[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            WriteLe(16, 4);
            WriteLe(1, 2);
            WriteLe(numChannels, 2);
            WriteLe(sampleRate, 4);
            WriteLe(byteRate, 4);
            WriteLe(blockAlign, 2);
            WriteLe(bitsPerSample, 2);
            Write(new byte[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            WriteLe(dataSize, 4);
            for (int i = 0; i < dataSize; i++)
                buffer[offset++] = 0;
            return buffer;
        }

        /// <summary>
        /// Unity 原生 AudioClip 导入不支持 AAC/MPEG-4 容器（.mp4/.m4a/.aac），需转码为 WAV。
        /// </summary>
        public static bool NeedsTranscodeForUnityImport(string extensionWithDot)
        {
            if (string.IsNullOrEmpty(extensionWithDot))
                return false;
            string ext = extensionWithDot.ToLowerInvariant();
            return ext == ".mp4" || ext == ".m4a" || ext == ".aac";
        }

        /// <summary>
        /// 从 AssetDatabase 加载 AudioClip，不触发导入或 Refresh。
        /// 若 <paramref name="assetPath"/> 无法作为 AudioClip 加载，会尝试同基名的 .wav 兄弟文件。
        /// </summary>
        public static AudioClip TryLoadAudioClip(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return null;

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
            if (clip != null)
                return clip;

            string wavSibling = Path.ChangeExtension(assetPath, ".wav");
            if (!string.Equals(wavSibling, assetPath, StringComparison.OrdinalIgnoreCase))
                return AssetDatabase.LoadAssetAtPath<AudioClip>(wavSibling);

            return null;
        }

        /// <summary>
        /// 对指定路径执行 ImportAsset 后加载 AudioClip。仅在已知磁盘文件已变更时调用。
        /// 若 ImportAsset 后仍无法加载（如 FSBTool 异步导入），才做一次全量 Refresh 兜底。
        /// </summary>
        private static AudioClip ImportAudioClip(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return null;

            PathUtils.ImportAssetAfterDiskWrite(assetPath);
            return TryLoadAudioClip(assetPath);
        }

        /// <summary>
        /// 确保音频资产可被 Unity 作为 AudioClip 导入。AAC/MP4 容器在无法直接导入时会尝试用 ffmpeg 转为 WAV。
        /// </summary>
        public static string EnsureUnityImportableAudioPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return assetPath;

            if (ImportAudioClip(assetPath) != null)
                return assetPath;

            if (!NeedsTranscodeForUnityImport(Path.GetExtension(assetPath)))
                return assetPath;

            string wavAssetPath = Path.ChangeExtension(assetPath, ".wav");
            string sourceAbsolute = PathUtils.ToAbsoluteAssetPath(assetPath);
            string wavAbsolute = PathUtils.ToAbsoluteAssetPath(wavAssetPath);
            if (!TryTranscodeToWav(sourceAbsolute, wavAbsolute, out string error))
            {
                TJLog.LogWarning(
                    $"[TJGeneratorsAudioUtils] 无法将 {assetPath} 转为 Unity 可导入的 WAV：{error}"
                );

                // ffmpeg 不可用时，若文件为 .mp4，将其重命名为 .m4a 以规避 Unity 将 .mp4
                // 误交给 VideoMedia 导入器处理（Windows 上触发 0xc00d36c4 错误）。
                // .m4a 会走 Unity 音频导入器，部分平台可直接加载，部分平台仍需 ffmpeg。
                if (string.Equals(Path.GetExtension(assetPath), ".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    string m4aAssetPath = Path.ChangeExtension(assetPath, ".m4a");
                    string mp4Absolute = PathUtils.ToAbsoluteAssetPath(assetPath);
                    string m4aAbsolute = PathUtils.ToAbsoluteAssetPath(m4aAssetPath);
                    if (File.Exists(mp4Absolute) && !File.Exists(m4aAbsolute))
                    {
                        File.Move(mp4Absolute, m4aAbsolute);
                        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null)
                            AssetDatabase.DeleteAsset(assetPath);
                        ImportAudioClip(m4aAssetPath);
                        TJLog.Log(
                            $"[TJGeneratorsAudioUtils] ffmpeg 不可用，已将 {assetPath} 重命名为 {m4aAssetPath}"
                            + " 以规避 Unity VideoMedia 导入器。"
                        );
                        return m4aAssetPath;
                    }
                }

                return assetPath;
            }

            if (!string.Equals(assetPath, wavAssetPath, StringComparison.OrdinalIgnoreCase)
                && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null)
            {
                AssetDatabase.DeleteAsset(assetPath);
            }

            ImportAudioClip(wavAssetPath);
            TJLog.Log($"[TJGeneratorsAudioUtils] 已将 {assetPath} 转码为 {wavAssetPath}");
            return wavAssetPath;
        }

        /// <summary>
        /// 使用系统 PATH 中的 ffmpeg 将音频转为 44.1kHz 16-bit PCM WAV。
        /// </summary>
        public static bool TryTranscodeToWav(
            string sourceAbsolutePath,
            string wavAbsolutePath,
            out string error
        )
        {
            error = null;
            if (string.IsNullOrEmpty(sourceAbsolutePath) || !File.Exists(sourceAbsolutePath))
            {
                error = TJGeneratorsL10n.L("源文件不存在");
                return false;
            }

            string wavDirectory = Path.GetDirectoryName(wavAbsolutePath);
            if (!string.IsNullOrEmpty(wavDirectory) && !Directory.Exists(wavDirectory))
                Directory.CreateDirectory(wavDirectory);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments =
                        $"-y -hide_banner -loglevel error -i \"{sourceAbsolutePath}\" "
                        + "-vn -acodec pcm_s16le -ar 44100 -ac 2 "
                        + $"\"{wavAbsolutePath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        error = TJGeneratorsL10n.L("无法启动 ffmpeg 进程");
                        return false;
                    }

                    // 在 WaitForExit 之前先启动异步 stderr 读取，避免管道缓冲区填满导致死锁：
                    // ffmpeg 向 stderr 写入超过 OS 缓冲区大小时，进程会阻塞在写入侧，
                    // 而主线程在 WaitForExit 处等待进程退出，形成互相等待的死锁。
                    var stderrTask = process.StandardError.ReadToEndAsync();

                    bool exited = process.WaitForExit(120_000);
                    if (!exited)
                    {
                        try { process.Kill(); }
                        catch { /* ignore */ }
                        error = TJGeneratorsL10n.L("ffmpeg 执行超时");
                        return false;
                    }

                    string stderr = stderrTask.GetAwaiter().GetResult();
                    if (process.ExitCode != 0 || !File.Exists(wavAbsolutePath))
                    {
                        error = string.IsNullOrWhiteSpace(stderr)
                            ? string.Format(TJGeneratorsL10n.L("ffmpeg 退出码 {0}"), process.ExitCode)
                            : stderr.Trim();
                        return false;
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }
}
#endif
