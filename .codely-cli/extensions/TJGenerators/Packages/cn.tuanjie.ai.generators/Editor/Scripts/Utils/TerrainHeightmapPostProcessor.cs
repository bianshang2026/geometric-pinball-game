#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace TJGenerators.Utils
{
    /// <summary>地形高度图 PNG 后处理选项。</summary>
    public struct TerrainHeightmapPostProcessOptions
    {
        // ── 去噪 ──────────────────────────────────────────────────────────────
        /// <summary>Median 3×3 去除"散点尖刺/离群点"（建议第一步开启）。</summary>
        public bool median3x3;

        // ── 平滑 ──────────────────────────────────────────────────────────────
        /// <summary>
        /// 双边滤波：平滑噪声的同时保护地形边缘/悬崖，适合大多数场景。
        /// 与 gaussianBlur 互斥；双边优先级更高。
        /// </summary>
        public bool bilateralFilter;

        /// <summary>双边滤波空间 σ（像素距离权重，建议 1.5～4.0）。</summary>
        public float bilateralSigmaSpace;

        /// <summary>双边滤波色彩 σ（高度差权重，建议 0.05～0.25；越小越保边）。</summary>
        public float bilateralSigmaColor;

        /// <summary>普通高斯模糊（不保边，当 bilateralFilter=false 时生效）。</summary>
        public bool gaussianBlur;

        /// <summary>高斯标准差 σ，越大越平滑（建议 0.8～2.5）。</summary>
        public float gaussianSigma;

        // ── 地形侵蚀 ─────────────────────────────────────────────────────────
        /// <summary>
        /// 热力侵蚀：将超过休止角的坡度搬移物质，使 AI 生成的不自然坡度趋于真实。
        /// 建议在平滑之后执行，迭代 5～30 次。
        /// </summary>
        public bool thermalErosion;

        /// <summary>热力侵蚀迭代次数（5～30；越大地形越圆润）。</summary>
        public int thermalErosionIterations;

        /// <summary>休止角（归一化高度差阈值，建议 0.01～0.08；越小削坡越积极）。</summary>
        public float thermalErosionTalus;

        // ── 归一化 ────────────────────────────────────────────────────────────
        /// <summary>
        /// 百分位归一化（推荐替代纯 min-max）。
        /// 忽略顶/底百分位的极值，使整体对比度不被孤立离群点压扁。
        /// </summary>
        public bool percentileNormalization;

        /// <summary>低端截断百分位（0.0～0.05，建议 0.01）。</summary>
        public float percentileLow;

        /// <summary>高端截断百分位（0.95～1.0，建议 0.99）。</summary>
        public float percentileHigh;

        // ── 高度曲线 ─────────────────────────────────────────────────────────
        /// <summary>
        /// 对归一化后的高度做 Gamma 校正（pow(h, gamma)）。
        /// 1.0 = 不变；&lt;1.0 提升中间调（更多山地）；&gt;1.0 压低中间调（更多平原）。
        /// </summary>
        public float heightGamma;

        // ── 输出重映射（近似 Terrain Tools Height Remap 的垂直范围）────────────
        /// <summary>
        /// 在 Gamma 之后，将线性高度 t∈[0,1] 映射到 [remapOutputMin, remapOutputMax]。
        /// 例如 0.1～0.9 可抬高整体海面并压低山顶，避免贴满 0～1。
        /// </summary>
        public float remapOutputMin;

        /// <summary>输出上限，须大于 remapOutputMin（内部会钳制）。</summary>
        public float remapOutputMax;

        // ── 快速预设 ──────────────────────────────────────────────────────────
        /// <summary>返回适合大多数 AI 生成高度图的默认选项。</summary>
        public static TerrainHeightmapPostProcessOptions Default => new TerrainHeightmapPostProcessOptions
        {
            median3x3             = true,
            bilateralFilter       = true,
            bilateralSigmaSpace   = 2.5f,
            bilateralSigmaColor   = 0.12f,
            gaussianBlur          = false,
            gaussianSigma         = 1.2f,
            thermalErosion        = true,
            thermalErosionIterations = 15,
            thermalErosionTalus   = 0.04f,
            percentileNormalization = true,
            percentileLow         = 0.01f,
            percentileHigh        = 0.99f,
            heightGamma           = 1.0f,
            remapOutputMin        = 0f,
            remapOutputMax        = 1f,
        };
    }

    /// <summary>
    /// 将文生图得到的"高度图"转为灰度并做后处理，覆盖写回同一路径（输出为 PNG）。
    /// 处理流程：RGB→灰度 → Median去刺 → 双边/高斯平滑 → 热力侵蚀 → 百分位/min-max 归一化 → Gamma → 输出范围重映射（Height Remap）。
    /// </summary>
    public static class TerrainHeightmapPostProcessor
    {
        // ═══════════════════════════════════════════════════════════════════════
        //  异步拆分 API（供 apply_terrain_heightmap 后台线程方案使用）
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [主线程] 解码 PNG → 提取灰度 float[]。不含任何耗时滤波运算，调用后可立即返回给 agent。
        /// </summary>
        public static bool TryExtractPixelData(
            string absolutePath,
            out float[] luminance,
            out int width,
            out int height,
            out string errorMessage)
        {
            luminance = null; width = 0; height = 0; errorMessage = null;
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
            {
                errorMessage = TJGeneratorsL10n.L("文件不存在");
                return false;
            }

            byte[] bytes;
            try { bytes = File.ReadAllBytes(absolutePath); }
            catch (Exception e) { errorMessage = e.Message; return false; }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(bytes)) { errorMessage = TJGeneratorsL10n.L("无法解码图片"); return false; }
                width = tex.width; height = tex.height;
                if (width <= 0 || height <= 0) { errorMessage = TJGeneratorsL10n.L("图片尺寸无效"); return false; }
                Color[] pixels = tex.GetPixels();
                int n = pixels.Length;
                luminance = new float[n];
                for (int i = 0; i < n; i++)
                {
                    Color c = pixels[i];
                    luminance[i] = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
                    if (luminance[i] < 0f) luminance[i] = 0f;
                    else if (luminance[i] > 1f) luminance[i] = 1f;
                }
                return true;
            }
            catch (Exception e) { errorMessage = e.Message; return false; }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        /// <summary>
        /// [后台线程安全] 对 luminance[] 原地执行所有滤波、归一化、Gamma、重映射。
        /// 不含任何 Unity API 调用，可在 Task.Run / Thread 中执行。
        /// </summary>
        public static void ApplyFilters(
            float[] luminance,
            int w, int h,
            TerrainHeightmapPostProcessOptions options)
        {
            if (options.median3x3 && w >= 3 && h >= 3)
                ApplyMedianFilter3x3(luminance, w, h);

            if (options.bilateralFilter && options.bilateralSigmaSpace > 1e-4f)
                ApplyBilateralFilter(luminance, w, h, options.bilateralSigmaSpace, options.bilateralSigmaColor);
            else if (options.gaussianBlur && options.gaussianSigma > 1e-4f)
                ApplySeparableGaussianBlur(luminance, w, h, options.gaussianSigma);

            if (options.thermalErosion && options.thermalErosionIterations > 0)
                ApplyThermalErosion(luminance, w, h, options.thermalErosionIterations, options.thermalErosionTalus);

            // 归一化 + Gamma + 重映射（结果仍写回 luminance[]）
            int n = luminance.Length;
            float normMin, normMax;
            if (options.percentileNormalization)
            {
                float pLow  = options.percentileLow  < 0f ? 0f : options.percentileLow  > 1f ? 1f : options.percentileLow;
                float pHigh = options.percentileHigh < 0f ? 0f : options.percentileHigh > 1f ? 1f : options.percentileHigh;
                if (pHigh <= pLow) pHigh = pLow + 0.01f > 1f ? 1f : pLow + 0.01f;
                (normMin, normMax) = ComputePercentiles(luminance, pLow, pHigh);
            }
            else
            {
                normMin = float.MaxValue; normMax = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    if (luminance[i] < normMin) normMin = luminance[i];
                    if (luminance[i] > normMax) normMax = luminance[i];
                }
            }

            float range = normMax - normMin;
            float gamma = options.heightGamma > 1e-4f ? options.heightGamma : 1.0f;
            bool applyGamma = gamma < 0.9999f || gamma > 1.0001f;
            float rOutMin = options.remapOutputMin < 0f ? 0f : options.remapOutputMin > 1f ? 1f : options.remapOutputMin;
            float rOutMax = options.remapOutputMax < 0f ? 0f : options.remapOutputMax > 1f ? 1f : options.remapOutputMax;
            if (rOutMax < rOutMin + 1e-5f) rOutMax = rOutMin + 0.01f > 1f ? 1f : rOutMin + 0.01f;

            for (int i = 0; i < n; i++)
            {
                float t = range > 1e-5f ? (luminance[i] - normMin) / range : 0.5f;
                if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                if (applyGamma)
                    t = (float)System.Math.Pow(t, gamma);
                luminance[i] = rOutMin + (rOutMax - rOutMin) * t;
            }
        }

        /// <summary>
        /// [主线程] 将后处理完毕的 luminance[] 编码为 PNG 并覆盖写入指定路径。
        /// </summary>
        public static bool TryEncodeAndSave(
            string absolutePath,
            float[] luminance,
            int w, int h,
            out string errorMessage)
        {
            errorMessage = null;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                int n = luminance.Length;
                var outColors = new Color[n];
                for (int i = 0; i < n; i++)
                {
                    float t = luminance[i];
                    outColors[i] = new Color(t, t, t, 1f);
                }
                tex.SetPixels(outColors);
                tex.Apply(false, false);
                File.WriteAllBytes(absolutePath, tex.EncodeToPNG());
                return true;
            }
            catch (Exception e) { errorMessage = e.Message; return false; }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  同步主入口（供 TJGeneratorsImageWindow 等现有同步调用方使用）
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 主入口：解码图片 → 后处理 → 覆盖写入原路径（输出为 PNG）。
        /// </summary>
        public static bool TryPostProcessHeightmapImage(
            string absolutePath,
            out string errorMessage,
            TerrainHeightmapPostProcessOptions options = default
        )
        {
            errorMessage = null;
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
            {
                errorMessage = TJGeneratorsL10n.L("文件不存在");
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(absolutePath);
            }
            catch (Exception e)
            {
                errorMessage = e.Message;
                return false;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(bytes))
                {
                    errorMessage = TJGeneratorsL10n.L("无法解码图片");
                    return false;
                }

                int w = tex.width;
                int h = tex.height;
                if (w <= 0 || h <= 0)
                {
                    errorMessage = TJGeneratorsL10n.L("图片尺寸无效");
                    return false;
                }

                Color[] pixels = tex.GetPixels();
                int n = pixels.Length;
                var luminance = new float[n];

                for (int i = 0; i < n; i++)
                {
                    Color c = pixels[i];
                    luminance[i] = Mathf.Clamp01(0.299f * c.r + 0.587f * c.g + 0.114f * c.b);
                }

                // ── Step 1: Median 去孤立尖刺（在其他操作之前） ──────────────
                if (options.median3x3 && w >= 3 && h >= 3)
                    ApplyMedianFilter3x3(luminance, w, h);

                // ── Step 2: 平滑（双边优先；不保边场景可退回高斯） ──────────
                if (options.bilateralFilter && options.bilateralSigmaSpace > 1e-4f)
                    ApplyBilateralFilter(luminance, w, h, options.bilateralSigmaSpace, options.bilateralSigmaColor);
                else if (options.gaussianBlur && options.gaussianSigma > 1e-4f)
                    ApplySeparableGaussianBlur(luminance, w, h, options.gaussianSigma);

                // ── Step 3: 热力侵蚀（使坡度自然化） ────────────────────────
                if (options.thermalErosion && options.thermalErosionIterations > 0)
                    ApplyThermalErosion(luminance, w, h, options.thermalErosionIterations, options.thermalErosionTalus);

                // ── Step 4: 归一化（百分位 or min-max） ──────────────────────
                float normMin, normMax;
                if (options.percentileNormalization)
                {
                    float pLow  = Mathf.Clamp01(options.percentileLow);
                    float pHigh = Mathf.Clamp01(options.percentileHigh);
                    if (pHigh <= pLow) pHigh = Mathf.Min(1f, pLow + 0.01f);
                    (normMin, normMax) = ComputePercentiles(luminance, pLow, pHigh);
                }
                else
                {
                    normMin = float.MaxValue;
                    normMax = float.MinValue;
                    for (int i = 0; i < n; i++)
                    {
                        if (luminance[i] < normMin) normMin = luminance[i];
                        if (luminance[i] > normMax) normMax = luminance[i];
                    }
                }

                float range = normMax - normMin;
                float gamma = options.heightGamma > 1e-4f ? options.heightGamma : 1.0f;
                bool applyGamma = Mathf.Abs(gamma - 1f) > 1e-4f;

                float rOutMin = Mathf.Clamp01(options.remapOutputMin);
                float rOutMax = Mathf.Clamp01(options.remapOutputMax);
                if (rOutMax < rOutMin + 1e-5f)
                    rOutMax = Mathf.Min(1f, rOutMin + 0.01f);

                var outColors = new Color[n];
                for (int i = 0; i < n; i++)
                {
                    float t = range > 1e-5f ? (luminance[i] - normMin) / range : 0.5f;
                    t = Mathf.Clamp01(t);
                    if (applyGamma)
                        t = Mathf.Pow(t, gamma);
                    t = Mathf.Lerp(rOutMin, rOutMax, t);
                    outColors[i] = new Color(t, t, t, 1f);
                }

                UnityEngine.Object.DestroyImmediate(tex);
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.SetPixels(outColors);
                tex.Apply(false, false);

                File.WriteAllBytes(absolutePath, tex.EncodeToPNG());
                return true;
            }
            catch (Exception e)
            {
                errorMessage = e.Message;
                return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  双边滤波
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 双边滤波：同时考虑空间距离权重和高度差权重，平滑平坦区域同时保护陡坡/悬崖边缘。
        /// 时间复杂度 O(n·r²)，r = ceil(sigmaSpace*2)。
        /// </summary>
        private static void ApplyBilateralFilter(float[] data, int w, int h, float sigmaSpace, float sigmaColor)
        {
            sigmaSpace = Mathf.Clamp(sigmaSpace, 0.5f, 10f);
            sigmaColor = Mathf.Clamp(sigmaColor, 0.01f, 1f);

            int r = Mathf.Min(12, Mathf.Max(1, Mathf.CeilToInt(sigmaSpace * 2f)));
            float twoSigmaSpaceSq = 2f * sigmaSpace * sigmaSpace;
            float twoSigmaColorSq = 2f * sigmaColor * sigmaColor;

            int n = w * h;
            var result = new float[n];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float center = data[y * w + x];
                    float weightSum = 0f;
                    float valueSum  = 0f;

                    int yMin = Mathf.Max(0, y - r);
                    int yMax = Mathf.Min(h - 1, y + r);
                    int xMin = Mathf.Max(0, x - r);
                    int xMax = Mathf.Min(w - 1, x + r);

                    for (int sy = yMin; sy <= yMax; sy++)
                    {
                        int dyy = sy - y;
                        for (int sx = xMin; sx <= xMax; sx++)
                        {
                            int dxx = sx - x;
                            float neighbor = data[sy * w + sx];
                            float colorDiff = center - neighbor;

                            float wSpatial = (dxx * dxx + dyy * dyy) / twoSigmaSpaceSq;
                            float wColor   = (colorDiff * colorDiff)  / twoSigmaColorSq;
                            float weight   = Mathf.Exp(-(wSpatial + wColor));

                            weightSum += weight;
                            valueSum  += weight * neighbor;
                        }
                    }

                    result[y * w + x] = weightSum > 1e-10f ? valueSum / weightSum : center;
                }
            }

            Array.Copy(result, data, n);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  热力侵蚀（Thermal Erosion）
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 简单热力侵蚀：对超过休止角（talus）的坡度搬移物质，使地形趋向自然坡度分布。
        /// 使用双缓冲避免同帧写入顺序影响结果。
        /// </summary>
        private static void ApplyThermalErosion(float[] data, int w, int h, int iterations, float talus)
        {
            talus = Mathf.Clamp(talus, 1e-4f, 1f);
            int n = w * h;

            // 4 方向邻域
            int[] dx = { 0, 0, -1, 1 };
            int[] dy = { -1, 1,  0, 0 };
            var delta = new float[n];

            for (int iter = 0; iter < iterations; iter++)
            {
                Array.Clear(delta, 0, n);

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float h0 = data[y * w + x];
                        int idx0 = y * w + x;

                        for (int d = 0; d < 4; d++)
                        {
                            int nx = x + dx[d];
                            int ny = y + dy[d];
                            if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;

                            int idx1 = ny * w + nx;
                            float diff = h0 - data[idx1];
                            if (diff > talus)
                            {
                                // 仅搬移超出休止角的部分的一半（保守策略，防止过度削坡）
                                float move = (diff - talus) * 0.25f;
                                delta[idx0] -= move;
                                delta[idx1] += move;
                            }
                        }
                    }
                }

                for (int i = 0; i < n; i++)
                    data[i] = Mathf.Clamp01(data[i] + delta[i]);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  百分位归一化
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 计算 <paramref name="data"/> 的 pLow / pHigh 百分位值，用于鲁棒的对比度拉伸。
        /// </summary>
        private static (float low, float high) ComputePercentiles(float[] data, float pLow, float pHigh)
        {
            int n = data.Length;
            var sorted = new float[n];
            Array.Copy(data, sorted, n);
            Array.Sort(sorted);

            int iLow  = Mathf.Clamp(Mathf.FloorToInt(pLow  * (n - 1)), 0, n - 1);
            int iHigh = Mathf.Clamp(Mathf.CeilToInt (pHigh * (n - 1)), 0, n - 1);
            return (sorted[iLow], sorted[iHigh]);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  高斯模糊（保留用于特殊场景）
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>可分离高斯模糊（不保边），原地修改 <paramref name="data"/>。</summary>
        private static void ApplySeparableGaussianBlur(float[] data, int w, int h, float sigma)
        {
            sigma = Mathf.Clamp(sigma, 0.1f, 8f);
            int maxR = Mathf.Min(31, Mathf.Max(1, Mathf.Min(w, h) / 2));
            int r = Mathf.Min(maxR, Mathf.Max(1, Mathf.CeilToInt(sigma * 3f)));
            int kernelSize = 2 * r + 1;
            var kernel = new float[kernelSize];
            float sumK = 0f;
            float twoSigmaSq = 2f * sigma * sigma;
            for (int i = 0; i < kernelSize; i++)
            {
                int d = i - r;
                float v = Mathf.Exp(-(d * d) / twoSigmaSq);
                kernel[i] = v;
                sumK += v;
            }

            for (int i = 0; i < kernelSize; i++)
                kernel[i] /= sumK;

            int n = w * h;
            var temp = new float[n];

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float acc = 0f;
                    for (int ki = 0; ki < kernelSize; ki++)
                    {
                        int sx = Mathf.Clamp(x + ki - r, 0, w - 1);
                        acc += kernel[ki] * data[row + sx];
                    }

                    temp[row + x] = acc;
                }
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float acc = 0f;
                    for (int ki = 0; ki < kernelSize; ki++)
                    {
                        int sy = Mathf.Clamp(y + ki - r, 0, h - 1);
                        acc += kernel[ki] * temp[sy * w + x];
                    }

                    data[y * w + x] = acc;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Median 3×3（去孤立尖刺）
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Median 3×3 去尖刺，使用 8-bit 量化提升性能。
        /// </summary>
        private static void ApplyMedianFilter3x3(float[] data, int w, int h)
        {
            int n = w * h;
            var q = new byte[n];
            for (int i = 0; i < n; i++)
                q[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(data[i] * 255f), 0, 255);

            var temp = new byte[n];
            var neighborhood = new byte[9];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = 0;
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int sy  = Mathf.Clamp(y + oy, 0, h - 1);
                        int row = sy * w;
                        for (int ox = -1; ox <= 1; ox++)
                            neighborhood[idx++] = q[row + Mathf.Clamp(x + ox, 0, w - 1)];
                    }

                    InsertionSort9(neighborhood);
                    temp[y * w + x] = neighborhood[4];
                }
            }

            for (int i = 0; i < n; i++)
                data[i] = temp[i] / 255f;
        }

        private static void InsertionSort9(byte[] arr)
        {
            for (int i = 1; i < 9; i++)
            {
                byte key = arr[i];
                int j = i - 1;
                while (j >= 0 && arr[j] > key)
                {
                    arr[j + 1] = arr[j];
                    j--;
                }

                arr[j + 1] = key;
            }
        }
    }
}
#endif
