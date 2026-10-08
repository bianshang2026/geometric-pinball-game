#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using TJGenerators.Utils;

namespace TJGenerators.PostProcessing
{
    /// <summary>
    /// 图片切割后处理：使用传统 CV 方法（连通域标记）将一张大图中的多个独立元素自动切割为小图。
    /// </summary>
    public static class ImageSlicePostProcess
    {
        // ========== 数据结构 ==========

        public enum BackgroundMode
        {
            Auto,
            Transparent,
            SolidColor
        }

        public struct SliceRegion
        {
            public int x;
            public int y;
            public int width;
            public int height;
            public int pixelCount;
        }

        public struct AnalyzeResult
        {
            public List<SliceRegion> regions;
            public Texture2D previewTexture;
        }

        public struct ExportResult
        {
            public string OutputDirectory;
            public List<string> AssetPaths;
            public int ExportedCount;
        }

        // ========== 公开 API ==========

        /// <summary>
        /// 分析图片，检测独立区域（用于预览）。返回区域列表和带标注的预览图。
        /// </summary>
        public static AnalyzeResult Analyze(
            Texture2D source,
            BackgroundMode bgMode,
            float alphaThreshold,
            float colorTolerance,
            int minRegionPixels,
            int padding)
        {
            if (source == null)
                return new AnalyzeResult { regions = new List<SliceRegion>(), previewTexture = null };

            Color[] pixels = source.GetPixels();
            int w = source.width;
            int h = source.height;

            BackgroundMode resolved = ResolveBackgroundMode(pixels, w, h, bgMode);
            bool[] mask = BuildForegroundMask(pixels, w, h, resolved, alphaThreshold, colorTolerance);
            List<SliceRegion> regions = FindConnectedRegions(mask, w, h, minRegionPixels);
            ApplyPadding(ref regions, w, h, padding);

            Texture2D preview = CreatePreviewTexture(source, regions);

            return new AnalyzeResult
            {
                regions = regions,
                previewTexture = preview
            };
        }

        /// <summary>
        /// 执行切割并导出为 PNG 资产。
        /// </summary>
        public static ExportResult Export(
            Texture2D source,
            string sourceAssetPath,
            BackgroundMode bgMode,
            float alphaThreshold,
            float colorTolerance,
            int minRegionPixels,
            int padding,
            bool setAsSprite)
        {
            if (source == null)
                return new ExportResult();

            Color[] pixels = source.GetPixels();
            int w = source.width;
            int h = source.height;

            BackgroundMode resolved = ResolveBackgroundMode(pixels, w, h, bgMode);
            bool[] mask = BuildForegroundMask(pixels, w, h, resolved, alphaThreshold, colorTolerance);
            List<SliceRegion> regions = FindConnectedRegions(mask, w, h, minRegionPixels);
            ApplyPadding(ref regions, w, h, padding);

            if (regions.Count == 0)
                return new ExportResult();

            // 估计背景色并做颜色去背景（消除边缘白灰边）
            Color bgColor = EstimateBackgroundColor(pixels, w, h, mask);
            Color[] maskedPixels = ApplyMaskToPixels(pixels, mask, w, h, bgColor);

            string outputDir = CreateOutputFolder(sourceAssetPath);
            var assetPaths = new List<string>();

            for (int i = 0; i < regions.Count; i++)
            {
                var region = regions[i];
                var sliceTex = CropRegion(maskedPixels, w, h, region);
                if (sliceTex == null)
                    continue;

                string fileName = $"slice_{i + 1:D3}.png";
                string assetPath = $"{outputDir}/{fileName}";
                File.WriteAllBytes(PathUtils.ToAbsoluteAssetPath(assetPath), sliceTex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(sliceTex);
                assetPaths.Add(assetPath);
            }

            PathUtils.ImportAssetsUnderFolderAfterDiskWrite(outputDir);

            if (setAsSprite)
            {
                foreach (var path in assetPaths)
                {
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer != null)
                    {
                        importer.textureType = TextureImporterType.Sprite;
                        importer.spriteImportMode = SpriteImportMode.Single;
                        importer.alphaIsTransparency = true;
                        importer.SaveAndReimport();
                    }
                }
            }

            return new ExportResult
            {
                OutputDirectory = outputDir,
                AssetPaths = assetPaths,
                ExportedCount = assetPaths.Count
            };
        }

        // ========== 背景检测 ==========

        private static BackgroundMode ResolveBackgroundMode(
            Color[] pixels, int w, int h, BackgroundMode mode)
        {
            if (mode != BackgroundMode.Auto)
                return mode;

            // 检测四角像素的 alpha：如果大量透明则为 Transparent，否则为 SolidColor
            int transparentCorners = 0;
            int cornerSamples = 0;
            int sampleSize = Mathf.Max(1, Mathf.Min(5, w / 10, h / 10));

            void SampleCorner(int sx, int sy)
            {
                for (int dy = 0; dy < sampleSize; dy++)
                {
                    for (int dx = 0; dx < sampleSize; dx++)
                    {
                        int px = Mathf.Min(sx + dx, w - 1);
                        int py = Mathf.Min(sy + dy, h - 1);
                        int idx = py * w + px;
                        if (idx < pixels.Length)
                        {
                            cornerSamples++;
                            if (pixels[idx].a < 0.5f)
                                transparentCorners++;
                        }
                    }
                }
            }

            SampleCorner(0, 0);                          // 左下
            SampleCorner(w - sampleSize, 0);             // 右下
            SampleCorner(0, h - sampleSize);             // 左上
            SampleCorner(w - sampleSize, h - sampleSize); // 右上

            if (cornerSamples > 0 && transparentCorners > cornerSamples / 2)
                return BackgroundMode.Transparent;

            return BackgroundMode.SolidColor;
        }

        // ========== 前景掩码 ==========

        private static bool[] BuildForegroundMask(
            Color[] pixels, int w, int h,
            BackgroundMode mode, float alphaThreshold, float colorTolerance)
        {
            var mask = new bool[w * h];

            if (mode == BackgroundMode.Transparent)
            {
                for (int i = 0; i < pixels.Length; i++)
                    mask[i] = pixels[i].a >= alphaThreshold;
            }
            else
            {
                // 纯色背景：取四角中位数作为背景色
                Color bgColor = EstimateBackgroundColor(pixels, w, h);
                float tol = colorTolerance / 100f;

                for (int i = 0; i < pixels.Length; i++)
                {
                    float dist = ColorDistance(pixels[i], bgColor);
                    mask[i] = dist > tol;
                }
            }

            return mask;
        }

        private static Color EstimateBackgroundColor(Color[] pixels, int w, int h)
        {
            return EstimateBackgroundColor(pixels, w, h, null);
        }

        /// <summary>
        /// 估计背景色。如果提供 mask，仅从背景像素采样，更精确。
        /// </summary>
        private static Color EstimateBackgroundColor(Color[] pixels, int w, int h, bool[] mask)
        {
            // 采样四角和边缘像素，取中位数作为背景色
            int sampleCount = 0;
            float sumR = 0, sumG = 0, sumB = 0, sumA = 0;

            // 有掩码时：从背景像素（mask=false）采样，更精确
            if (mask != null)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (mask[i]) continue;
                    sumR += pixels[i].r;
                    sumG += pixels[i].g;
                    sumB += pixels[i].b;
                    sumA += pixels[i].a;
                    sampleCount++;
                }
            }
            else
            {
                // 无掩码时：从边缘像素采样
                int edgeWidth = Mathf.Max(1, Mathf.Min(3, w / 20));
                int edgeHeight = Mathf.Max(1, Mathf.Min(3, h / 20));

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        bool isEdge = x < edgeWidth || x >= w - edgeWidth
                                   || y < edgeHeight || y >= h - edgeHeight;
                        if (!isEdge) continue;

                        int idx = y * w + x;
                        if (idx >= pixels.Length) continue;

                        sumR += pixels[idx].r;
                        sumG += pixels[idx].g;
                        sumB += pixels[idx].b;
                        sumA += pixels[idx].a;
                        sampleCount++;
                    }
                }
            }

            if (sampleCount == 0)
                return Color.white;

            return new Color(sumR / sampleCount, sumG / sampleCount, sumB / sampleCount, sumA / sampleCount);
        }

        private static float ColorDistance(Color a, Color b)
        {
            float dr = a.r - b.r;
            float dg = a.g - b.g;
            float db = a.b - b.b;
            return Mathf.Sqrt(dr * dr + dg * dg + db * db);
        }

        // ========== 连通域标记（8-connected BFS）==========

        private static readonly int[] s_dx = { -1, -1, -1, 0, 0, 1, 1, 1 };
        private static readonly int[] s_dy = { -1, 0, 1, -1, 1, -1, 0, 1 };

        private static List<SliceRegion> FindConnectedRegions(bool[] mask, int w, int h, int minPixels)
        {
            var labels = new int[w * h];
            int labelCount = 0;
            var regions = new List<SliceRegion>();

            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i] || labels[i] != 0)
                    continue;

                labelCount++;
                var region = BFS(mask, labels, w, h, i, labelCount);
                if (region.pixelCount >= minPixels)
                    regions.Add(region);
            }

            // 按 y 再按 x 排序（从上到下、从左到右），方便用户阅读
            regions.Sort((a, b) =>
            {
                int cmp = b.y.CompareTo(a.y); // Unity 纹理 y 从下往上，翻转排序
                if (cmp != 0) return cmp;
                return a.x.CompareTo(b.x);
            });

            return regions;
        }

        private static SliceRegion BFS(bool[] mask, int[] labels, int w, int h, int startIdx, int label)
        {
            var queue = new Queue<int>();
            queue.Enqueue(startIdx);
            labels[startIdx] = label;

            int minX = w, minY = h, maxX = 0, maxY = 0;
            int count = 0;

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int cx = idx % w;
                int cy = idx / w;
                count++;

                if (cx < minX) minX = cx;
                if (cx > maxX) maxX = cx;
                if (cy < minY) minY = cy;
                if (cy > maxY) maxY = cy;

                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + s_dx[d];
                    int ny = cy + s_dy[d];
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h)
                        continue;
                    int nIdx = ny * w + nx;
                    if (!mask[nIdx] || labels[nIdx] != 0)
                        continue;
                    labels[nIdx] = label;
                    queue.Enqueue(nIdx);
                }
            }

            return new SliceRegion
            {
                x = minX,
                y = minY,
                width = maxX - minX + 1,
                height = maxY - minY + 1,
                pixelCount = count
            };
        }

        // ========== Padding ==========

        private static void ApplyPadding(ref List<SliceRegion> regions, int w, int h, int padding)
        {
            for (int i = 0; i < regions.Count; i++)
            {
                var r = regions[i];
                r.x = Mathf.Max(0, r.x - padding);
                r.y = Mathf.Max(0, r.y - padding);
                r.width = Mathf.Min(w - r.x, r.width + 2 * padding);
                r.height = Mathf.Min(h - r.y, r.height + 2 * padding);
                regions[i] = r;
            }
        }

        // ========== 掩码应用 ==========

        private const int FeatherRadius = 2;

        /// <summary>
        /// 将前景掩码应用到像素数组：对掩码做羽化后乘以 alpha，
        /// 并从边缘半透明像素中扣除背景色贡献（color decontamination），消除白灰边。
        /// </summary>
        private static Color[] ApplyMaskToPixels(Color[] pixels, bool[] mask, int w, int h, Color bgColor)
        {
            float[] softMask = FeatherMask(mask, w, h, FeatherRadius);

            var result = new Color[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                float m = softMask[i];
                float a = pixels[i].a * m;

                // 颜色去背景：从 RGB 中扣除背景色按 (1-m) 比例的混合
                // original_rgb ≈ fg_rgb * m + bg_rgb * (1-m)
                // => fg_rgb ≈ (original_rgb - bg_rgb * (1-m)) / m
                float r = pixels[i].r;
                float g = pixels[i].g;
                float b = pixels[i].b;

                if (m > 0.01f)
                {
                    float invM = 1f / m;
                    r = (r - bgColor.r * (1f - m)) * invM;
                    g = (g - bgColor.g * (1f - m)) * invM;
                    b = (b - bgColor.b * (1f - m)) * invM;
                }

                result[i] = new Color(
                    Mathf.Clamp01(r),
                    Mathf.Clamp01(g),
                    Mathf.Clamp01(b),
                    Mathf.Clamp01(a));
            }
            return result;
        }

        /// <summary>
        /// 对二值掩码做 box blur 产生软边缘。
        /// 半径 1 ≈ 2px 渐变，半径 2 ≈ 4px 渐变。
        /// 先水平后垂直，两趟 separable blur。
        /// </summary>
        private static float[] FeatherMask(bool[] mask, int w, int h, int radius)
        {
            int len = mask.Length;
            var src = new float[len];
            for (int i = 0; i < len; i++)
                src[i] = mask[i] ? 1f : 0f;

            var tmp = new float[len];
            var dst = new float[len];

            int kernelSize = radius * 2 + 1;

            // 水平趟
            for (int y = 0; y < h; y++)
            {
                float sum = 0f;
                // 初始化窗口 [0, kernelSize)
                for (int kx = 0; kx <= Mathf.Min(radius, w - 1); kx++)
                    sum += src[y * w + kx];

                for (int x = 0; x < w; x++)
                {
                    int addX = x + radius;
                    int remX = x - radius - 1;
                    if (addX < w) sum += src[y * w + addX];
                    if (remX >= 0) sum -= src[y * w + remX];
                    tmp[y * w + x] = sum / kernelSize;
                }
            }

            // 垂直趟
            for (int x = 0; x < w; x++)
            {
                float sum = 0f;
                for (int ky = 0; ky <= Mathf.Min(radius, h - 1); ky++)
                    sum += tmp[ky * w + x];

                for (int y = 0; y < h; y++)
                {
                    int addY = y + radius;
                    int remY = y - radius - 1;
                    if (addY < h) sum += tmp[addY * w + x];
                    if (remY >= 0) sum -= tmp[remY * w + x];
                    dst[y * w + x] = sum / kernelSize;
                }
            }

            return dst;
        }

        // ========== 裁剪 ==========

        private static Texture2D CropRegion(Color[] pixels, int w, int h, SliceRegion region)
        {
            int rw = Mathf.Min(region.width, w - region.x);
            int rh = Mathf.Min(region.height, h - region.y);
            if (rw <= 0 || rh <= 0)
                return null;

            var tex = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
            var cropPixels = new Color[rw * rh];

            for (int dy = 0; dy < rh; dy++)
            {
                int srcRow = (region.y + dy) * w + region.x;
                int dstRow = dy * rw;
                for (int dx = 0; dx < rw; dx++)
                {
                    cropPixels[dstRow + dx] = pixels[srcRow + dx];
                }
            }

            tex.SetPixels(cropPixels);
            tex.Apply();
            return tex;
        }

        // ========== 预览 ==========

        private static Texture2D CreatePreviewTexture(Texture2D source, List<SliceRegion> regions)
        {
            var preview = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            preview.SetPixels(source.GetPixels());

            // 为每个区域绘制边框
            var colors = GetRegionColors(regions.Count);
            for (int i = 0; i < regions.Count; i++)
            {
                DrawRegionRect(preview, regions[i], colors[i]);
            }

            preview.Apply();
            return preview;
        }

        private static Color[] GetRegionColors(int count)
        {
            // 高对比度颜色列表，便于在深色/浅色图上都清晰可见
            var palette = new Color[]
            {
                new Color(0f, 1f, 0.53f, 1f),    // #00FF87 绿
                new Color(1f, 0.27f, 0f, 1f),    // #FF4500 橙红
                new Color(0f, 0.75f, 1f, 1f),    // #00BFFF 天蓝
                new Color(1f, 0.84f, 0f, 1f),    // #FFD700 金
                new Color(0.8f, 0.2f, 1f, 1f),   // #CC33FF 紫
                new Color(1f, 0.41f, 0.71f, 1f),  // #FF69B4 粉
                new Color(0.5f, 1f, 0f, 1f),     // #80FF00 黄绿
                new Color(1f, 1f, 0f, 1f),        // #FFFF00 黄
            };

            var result = new Color[count];
            for (int i = 0; i < count; i++)
                result[i] = palette[i % palette.Length];
            return result;
        }

        private static void DrawRegionRect(Texture2D tex, SliceRegion region, Color color)
        {
            int thickness = Mathf.Max(2, Mathf.Min(3, tex.width / 200));

            for (int t = 0; t < thickness; t++)
            {
                // 上边
                DrawHorizontalLine(tex, region.x, region.y + t, region.width, color);
                // 下边
                DrawHorizontalLine(tex, region.x, region.y + region.height - 1 - t, region.width, color);
                // 左边
                DrawVerticalLine(tex, region.x + t, region.y, region.height, color);
                // 右边
                DrawVerticalLine(tex, region.x + region.width - 1 - t, region.y, region.height, color);
            }
        }

        private static void DrawHorizontalLine(Texture2D tex, int x, int y, int length, Color color)
        {
            if (y < 0 || y >= tex.height) return;
            for (int i = 0; i < length; i++)
            {
                int px = x + i;
                if (px >= 0 && px < tex.width)
                    tex.SetPixel(px, y, color);
            }
        }

        private static void DrawVerticalLine(Texture2D tex, int x, int y, int length, Color color)
        {
            if (x < 0 || x >= tex.width) return;
            for (int i = 0; i < length; i++)
            {
                int py = y + i;
                if (py >= 0 && py < tex.height)
                    tex.SetPixel(x, py, color);
            }
        }

        // ========== 输出文件夹 ==========

        private static string CreateOutputFolder(string sourceAssetPath)
        {
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                AssetDatabase.CreateFolder("Assets/TJGenerators", "History");

            string sourceName = Path.GetFileNameWithoutExtension(sourceAssetPath);
            if (string.IsNullOrEmpty(sourceName))
                sourceName = "Image";
            string folderName = $"{sourceName}_sliced_{DateTime.Now:yyyyMMdd_HHmmss}";
            string baseFolder = $"Assets/TJGenerators/History/{folderName}";
            string unique = AssetDatabase.GenerateUniqueAssetPath(baseFolder);
            EnsureAssetFolder(unique);
            return unique;
        }

        private static void EnsureAssetFolder(string folderPath)
        {
            string normalized = folderPath.Replace("\\", "/").TrimEnd('/');
            string[] parts = normalized.Split('/');
            if (parts.Length == 0) return;
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
#endif
