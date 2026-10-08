#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using TJGenerators.Utils;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// unitypackage 只读解析与过滤：流式扫描 GZip+Tar 结构，提取 {guid}/pathname 条目中的文件路径，
    /// 并支持按路径前缀生成过滤版本的包文件（用于导入前去除 Packages/ 等系统级条目）。
    /// </summary>
    public static class UnityPackageInspector
    {
        private const string LogTag = "[UnityPackageInspector]";

        /// <summary>
        /// 读取 unitypackage 中所有 Unity 资源路径列表（即每条目 pathname 内容）。
        /// </summary>
        public static List<string> GetPackageFileList(string filePath)
        {
            var paths = new List<string>();
            using (var fileStream = File.OpenRead(filePath))
            using (var gzip = new GZipStream(fileStream, CompressionMode.Decompress))
            {
                var header = new byte[512];

                while (true)
                {
                    if (ReadFull(gzip, header, 512) < 512) break;
                    if (IsZeroBlock(header)) break;

                    string name = ReadNullTerminatedString(header, 0, 100);
                    long size = ParseOctal(header, 124, 12);
                    long dataBlocks = (size + 511) / 512;

                    var parts = name.TrimEnd('/').Split('/');
                    if (parts.Length == 2 && parts[1] == "pathname" && size > 0 && size < 4096)
                    {
                        var buf = new byte[dataBlocks * 512];
                        ReadFull(gzip, buf, (int)(dataBlocks * 512));
                        string assetPath = Encoding.UTF8.GetString(buf, 0, (int)size).Trim();
                        if (!string.IsNullOrEmpty(assetPath))
                            paths.Add(assetPath);
                    }
                    else
                    {
                        SkipBytes(gzip, dataBlocks * 512);
                    }
                }
            }
            return paths;
        }

        /// <summary>
        /// 读取 unitypackage 中所有 GUID → 资源路径的映射（Pass 1，用于 CreateFilteredPackage）。
        /// </summary>
        public static Dictionary<string, string> GetGuidPathnames(string filePath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var fs = File.OpenRead(filePath))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            {
                var header = new byte[512];

                while (ReadFull(gz, header, 512) == 512)
                {
                    if (IsZeroBlock(header)) break;
                    string name  = ReadNullTerminatedString(header, 0, 100);
                    long   size  = ParseOctal(header, 124, 12);
                    long   blocks = (size + 511) / 512;

                    int    slash      = name.IndexOf('/');
                    bool   isPathname = slash > 0 &&
                                        string.Equals(name.Substring(slash + 1).TrimEnd('/'),
                                                      "pathname", StringComparison.Ordinal);

                    if (isPathname && size > 0 && size < 4096)
                    {
                        string guid = name.Substring(0, slash);
                        var    buf  = new byte[blocks * 512];
                        ReadFull(gz, buf, (int)(blocks * 512));
                        string assetPath = Encoding.UTF8.GetString(buf, 0, (int)size).Trim();
                        if (!string.IsNullOrEmpty(assetPath))
                            result[guid] = assetPath;
                    }
                    else
                    {
                        SkipBytes(gz, blocks * 512);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 生成过滤后的 .unitypackage 文件，排除 <paramref name="shouldExclude"/> 返回 true 的路径
        /// 所对应的全部 GUID 条目（pathname / asset / asset.meta / preview.png 等一并去除）。
        /// <para>实现：两次流式扫描（Pass 1 收集待排除 GUID；Pass 2 流式复制剩余条目），无需全量内存缓冲。</para>
        /// </summary>
        /// <returns>过滤后的临时文件路径；若无需过滤则返回 <c>null</c>。调用方负责删除该文件。</returns>
        public static string CreateFilteredPackage(string sourcePath, Func<string, bool> shouldExclude)
        {
            // Pass 1: 收集需要排除的 GUID
            var guidPathnames = GetGuidPathnames(sourcePath);
            var excludeGuids  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedPaths  = new List<string>();

            foreach (var kv in guidPathnames)
            {
                if (shouldExclude(kv.Value))
                {
                    excludeGuids.Add(kv.Key);
                    skippedPaths.Add(kv.Value);
                }
            }

            if (excludeGuids.Count == 0)
                return null;

            TJLog.LogWarning(
                $"{LogTag} 检测到 {excludeGuids.Count} 个需过滤的包条目，将在导入前移除：\n" +
                string.Join("\n", skippedPaths));

            // Pass 2: 流式复制，跳过被排除 GUID 的全部 tar 块
            string filteredPath = sourcePath + ".filtered.unitypackage";
            using (var srcFs = File.OpenRead(sourcePath))
            using (var srcGz = new GZipStream(srcFs, CompressionMode.Decompress))
            using (var dstFs = File.Create(filteredPath))
            using (var dstGz = new GZipStream(dstFs, CompressionMode.Compress))
            {
                var header = new byte[512];
                while (ReadFull(srcGz, header, 512) == 512)
                {
                    if (IsZeroBlock(header))
                    {
                        // 写入 end-of-archive（两个零块）
                        dstGz.Write(header, 0, 512);
                        dstGz.Write(header, 0, 512);
                        break;
                    }

                    string name   = ReadNullTerminatedString(header, 0, 100);
                    long   size   = ParseOctal(header, 124, 12);
                    long   blocks = (size + 511) / 512;

                    int    slash = name.IndexOf('/');
                    string guid  = slash > 0 ? name.Substring(0, slash) : null;

                    if (guid != null && excludeGuids.Contains(guid))
                    {
                        SkipBytes(srcGz, blocks * 512);
                    }
                    else
                    {
                        dstGz.Write(header, 0, 512);
                        CopyExact(srcGz, dstGz, blocks * 512);
                    }
                }
            }

            return filteredPath;
        }

        private static int ReadFull(Stream s, byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        private static bool IsZeroBlock(byte[] block)
        {
            for (int i = 0; i < block.Length; i++)
                if (block[i] != 0) return false;
            return true;
        }

        private static string ReadNullTerminatedString(byte[] buf, int offset, int length)
        {
            int end = offset;
            while (end < offset + length && buf[end] != 0) end++;
            return Encoding.UTF8.GetString(buf, offset, end - offset);
        }

        private static long ParseOctal(byte[] header, int offset, int length)
        {
            string raw = Encoding.ASCII.GetString(header, offset, length);
            string trimmed = raw.Trim('\0', ' ');
            return string.IsNullOrEmpty(trimmed) ? 0L : Convert.ToInt64(trimmed, 8);
        }

        private static void SkipBytes(Stream s, long count)
        {
            var buf = new byte[4096];
            while (count > 0)
            {
                int toRead = (int)Math.Min(count, buf.Length);
                int n = s.Read(buf, 0, toRead);
                if (n <= 0) break;
                count -= n;
            }
        }

        private static void CopyExact(Stream src, Stream dst, long byteCount)
        {
            var buf       = new byte[65536];
            long remaining = byteCount;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buf.Length);
                int n      = src.Read(buf, 0, toRead);
                if (n <= 0) break;
                dst.Write(buf, 0, n);
                remaining -= n;
            }
        }
    }
}
#endif
