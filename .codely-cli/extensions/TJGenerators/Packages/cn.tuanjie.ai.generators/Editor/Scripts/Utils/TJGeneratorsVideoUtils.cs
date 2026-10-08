#if UNITY_EDITOR
using System;
using System.IO;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 视频资产工具：创建合法 MP4 占位（纯黑单帧）并导入为 VideoClip。
    /// </summary>
    public static class TJGeneratorsVideoUtils
    {
        private const string TemplateFileName = "TJGeneratorsBlankVideo.mp4";

        /// <summary>
        /// 在指定路径写入黑场占位 MP4 并导入。生成完成后会被实际视频覆盖。
        /// </summary>
        public static string CreateBlankVideoClip(string path)
        {
            path = Path.ChangeExtension(path, ".mp4");
            string absolutePath = PathUtils.ToAbsoluteAssetPath(path);
            string directory = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory))
                PathUtils.EnsureAssetFolder(directory);

            File.WriteAllBytes(absolutePath, GetTemplateBytes());
            ImportPlaceholder(path);
            return path;
        }

        /// <summary>
        /// 占位 MP4 是否为空或缺少 ftyp 头（旧版空文件占位）。
        /// </summary>
        public static bool NeedsPlaceholderRepair(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            string absolutePath = PathUtils.ToAbsoluteAssetPath(assetPath);
            if (!File.Exists(absolutePath))
                return true;

            try
            {
                var info = new FileInfo(absolutePath);
                if (info.Length < 32)
                    return true;

                using (var stream = File.OpenRead(absolutePath))
                {
                    var header = new byte[12];
                    if (stream.Read(header, 0, header.Length) < 8)
                        return true;

                    // MP4: size(4) + 'ftyp'(4)
                    return header[4] != (byte)'f'
                        || header[5] != (byte)'t'
                        || header[6] != (byte)'y'
                        || header[7] != (byte)'p';
                }
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 原地修复损坏/空的占位 MP4，保留 GUID。
        /// </summary>
        public static void RepairPlaceholderVideo(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            string absolutePath = PathUtils.ToAbsoluteAssetPath(assetPath);
            string directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory))
                PathUtils.EnsureAssetFolder(directory);

            File.WriteAllBytes(absolutePath, GetTemplateBytes());
            ImportPlaceholder(assetPath);
        }

        private static void ImportPlaceholder(string assetPath)
        {
            PathUtils.ImportAssetAfterDiskWrite(assetPath);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(assetPath));
        }

        private static byte[] GetTemplateBytes()
        {
            string absolute = ResolveTemplateAbsolutePath();
            if (!string.IsNullOrEmpty(absolute))
                return File.ReadAllBytes(absolute);

            return Convert.FromBase64String(ShortestBlackMp4Base64);
        }

        /// <summary>
        /// 模板放在 Editor/Resources~（Unity 不导入），避免包启动时 VideoClipImporter 报 Error while reading movie。
        /// </summary>
        private static string ResolveTemplateAbsolutePath()
        {
            string packageRoot = PathUtils.TryGetTjGeneratorsPackageRoot();
            if (string.IsNullOrEmpty(packageRoot))
                return null;

            string path = Path.Combine(packageRoot, "Editor", "Resources~", TemplateFileName);
            return File.Exists(path) ? path : null;
        }

        // 64x64 纯黑 H.264 单帧 MP4（约 1.5 KB），BT.709 色彩空间，由 FFmpeg 生成
        private const string ShortestBlackMp4Base64 =
            "AAAAIGZ0eXBpc29tAAACAGlzb21pc28yYXZjMW1wNDEAAAMdbW9vdgAAAGxtdmhkAAAAAAAAAAAAAAAAAAAD6AAAAMgAAQAAAQAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAkh0cmFrAAAAXHRraGQAAAADAAAAAAAAAAAAAAABAAAAAAAAAMgAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAAEAAAABAAAAAAAAkZWR0cwAAABxlbHN0AAAAAAAAAAEAAADIAAAAAAABAAAAAAHAbWRpYQAAACBtZGhkAAAAAAAAAAAAAAAAAAAoAAAACABVxAAAAAAALWhkbHIAAAAAAAAAAHZpZGUAAAAAAAAAAAAAAABWaWRlb0hhbmRsZXIAAAABa21pbmYAAAAUdm1oZAAAAAEAAAAAAAAAAAAAACRkaW5mAAAAHGRyZWYAAAAAAAAAAQAAAAx1cmwgAAAAAQAAAStzdGJsAAAAq3N0c2QAAAAAAAAAAQAAAJthdmMxAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAEAAQABIAAAASAAAAAAAAAABFUxhdmM2MS4xOS4xMDAgbGlieDI2NAAAAAAAAAAAAAAAGP//AAAAMWF2Y0MBQsAe/+EAGWdCwB7ZBCaagICAoAAAAwAgAAADAoHixckBAAVoy4PLIAAAABRidHJ0AAAAAAAAZ+gAAGfoAAAAGHN0dHMAAAAAAAAAAQAAAAIAAAQAAAAAFHN0c3MAAAAAAAAAAQAAAAEAAAAcc3RzYwAAAAAAAAABAAAAAQAAAAIAAAABAAAAHHN0c3oAAAAAAAAAAAAAAAIAAAKPAAAACgAAABRzdGNvAAAAAAAAAAEAAANNAAAAYXVkdGEAAABZbWV0YQAAAAAAAAAhaGRscgAAAAAAAAAAbWRpcmFwcGwAAAAAAAAAAAAAAAAsaWxzdAAAACSpdG9vAAAAHGRhdGEAAAABAAAAAExhdmY2MS43LjEwMAAAAAhmcmVlAAACoW1kYXQAAAJxBgX//23cRem95tlIt5Ys2CDZI+7veDI2NCAtIGNvcmUgMTY0IHIzMTkyIGMyNGUwNmMgLSBILjI2NC9NUEVHLTQgQVZDIGNvZGVjIC0gQ29weWxlZnQgMjAwMy0yMDI0IC0gaHR0cDovL3d3dy52aWRlb2xhbi5vcmcveDI2NC5odG1sIC0gb3B0aW9uczogY2FiYWM9MCByZWY9MyBkZWJsb2NrPTE6MDowIGFuYWx5c2U9MHgxOjB4MTExIG1lPWhleCBzdWJtZT03IHBzeT0xIHBzeV9yZD0xLjAwOjAuMDAgbWl4ZWRfcmVmPTEgbWVfcmFuZ2U9MTYgY2hyb21hX21lPTEgdHJlbGxpcz0xIDh4OGRjdD0wIGNxbT0wIGRlYWR6b25lPTIxLDExIGZhc3RfcHNraXA9MSBjaHJvbWFfcXBfb2Zmc2V0PS0yIHRocmVhZHM9MiBsb29rYWhlYWRfdGhyZWFkcz0xIHNsaWNlZF90aHJlYWRzPTAgbnI9MCBkZWNpbWF0ZT0xIGludGVybGFjZWQ9MCBibHVyYXlfY29tcGF0PTAgY29uc3RyYWluZWRfaW50cmE9MCBiZnJhbWVzPTAgd2VpZ2h0cD0wIGtleWludD0yNTAga2V5aW50X21pbj0xMCBzY2VuZWN1dD00MCBpbnRyYV9yZWZyZXNoPTAgcmNfbG9va2FoZWFkPTQwIHJjPWNyZiBtYnRyZWU9MSBjcmY9MjMuMCBxY29tcD0wLjYwIHFwbWluPTAgcXBtYXg9NjkgcXBzdGVwPTQgaXBfcmF0aW89MS40MCBhcT0xOjEuMDAAgAAAABZliIQO8mKAAL78nJyddddddddddddeAAAABkGaOBvhGA==";
    }
}
#endif
