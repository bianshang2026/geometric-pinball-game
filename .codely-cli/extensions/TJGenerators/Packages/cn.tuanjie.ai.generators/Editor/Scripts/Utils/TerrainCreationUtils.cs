#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using TJGenerators.Utils;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 地形创建公共工具类，供 TJGeneratorsImageWindow 和 GenerateTerrainTool 共用。
    /// </summary>
    public static class TerrainCreationUtils
    {
        private const string LogTag = "[TerrainCreationUtils]";

        /// <summary>允许值: 33, 65, 129, 257, 513, 1025, 2049, 4097</summary>
        public static int SnapToAllowedHeightmapResolution(int approximate)
        {
            int[] allowed = { 33, 65, 129, 257, 513, 1025, 2049, 4097 };
            int lo = Mathf.Clamp(approximate, 33, 4097);
            foreach (int a in allowed)
            {
                if (a >= lo)
                    return a;
            }
            return 4097;
        }

        /// <summary>双线性重采样 + UV 内缩（避开 AI 图边缘异常像素）+ 外圈羽化（防竖墙）</summary>
        public static float[,] ConvertTextureToHeights(Texture2D texture, int targetResolution)
        {
            float[,] heights = new float[targetResolution, targetResolution];
            if (texture == null || targetResolution < 3)
                return heights;

            int w = texture.width;
            int h = texture.height;
            if (w < 2 || h < 2)
                return heights;

            // 内缩 1.5～4 像素或短边约 2%，减轻贴图最外一列在 R×R 重采样后竖起成墙
            float padPx = Mathf.Clamp(Mathf.Min(w, h) * 0.02f, 1.5f, 4f);
            padPx = Mathf.Min(padPx, (Mathf.Min(w, h) - 1f) * 0.45f);
            float uMin = padPx / w;
            float uMax = 1f - padPx / w;
            float vMin = padPx / h;
            float vMax = 1f - padPx / h;
            if (uMin >= uMax || vMin >= vMax)
            {
                uMin = 0.5f / w;
                uMax = 1f - 0.5f / w;
                vMin = 0.5f / h;
                vMax = 1f - 0.5f / h;
            }

            float denom = Mathf.Max(1f, targetResolution - 1f);
            for (int z = 0; z < targetResolution; z++)
            {
                for (int x = 0; x < targetResolution; x++)
                {
                    float tu = x / denom;
                    float tv = z / denom;
                    float u = Mathf.Lerp(uMin, uMax, tu);
                    float v = Mathf.Lerp(vMin, vMax, tv);
                    Color pixel = texture.GetPixelBilinear(u, v);
                    heights[z, x] = Mathf.Clamp01(pixel.grayscale);
                }
            }

            int featherRings = Mathf.Clamp(targetResolution / 64, 2, 10);
            FeatherHeightmapOuterRing(heights, targetResolution, featherRings);

            return heights;
        }

        /// <summary>最外若干环向内侧「稳定高度」插值，消除网格边界上整块竖直挤出。</summary>
        public static void FeatherHeightmapOuterRing(float[,] heights, int r, int feather)
        {
            if (r < 5 || feather < 1 || heights == null)
                return;

            feather = Mathf.Min(feather, Mathf.Max(1, (r - 1) / 2));
            int lo = feather;
            int hi = r - 1 - feather;
            if (lo > hi)
                return;

            var src = (float[,])heights.Clone();

            for (int z = 0; z < r; z++)
            {
                for (int x = 0; x < r; x++)
                {
                    int cx = Mathf.Clamp(x, lo, hi);
                    int cz = Mathf.Clamp(z, lo, hi);
                    float dist = Mathf.Min(x, z, r - 1 - x, r - 1 - z);
                    float t = Mathf.Clamp01(dist / (float)feather);
                    heights[z, x] = Mathf.Lerp(src[cz, cx], src[z, x], t);
                }
            }
        }

        /// <summary>
        /// 从已后处理的高度图 PNG 资产路径创建 TerrainData 并在当前场景中放置 Terrain GameObject。
        /// 返回 (terrainDataPath, terrainGoName)。
        /// </summary>
        public static (string terrainDataPath, string terrainGoName) CreateTerrainFromHeightmap(
            string heightmapAssetPath,
            string terrainGoName = "TJGenerators Terrain")
        {
            if (string.IsNullOrEmpty(heightmapAssetPath))
                throw new ArgumentException("heightmapAssetPath is empty");

            string absPng = PathUtils.ToAbsoluteAssetPath(heightmapAssetPath);
            if (!File.Exists(absPng))
                throw new FileNotFoundException(string.Format(TJGeneratorsL10n.L("高度图文件不存在: {0}"), absPng));

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(absPng);
            }
            catch (Exception e)
            {
                throw new IOException("Failed to read heightmap: " + e.Message, e);
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(bytes))
                    throw new InvalidOperationException(TJGeneratorsL10n.L("无法解码图片"));

                int w = tex.width;
                int h = tex.height;
                if (w <= 0 || h <= 0)
                    throw new InvalidOperationException(TJGeneratorsL10n.L("图片尺寸无效"));

                float terrainWidth = w;
                float terrainLength = h;
                float terrainMaxHeight = Mathf.Max(w, h) * 0.3f;

                int approxRes = Mathf.ClosestPowerOfTwo(Mathf.Max(w, h)) + 1;
                int heightmapResolution = SnapToAllowedHeightmapResolution(approxRes);

                // 必须先确定路径并调用 CreateAsset，再设置 heightmapResolution / SetHeights。
                // 否则 Unity 会在 new TerrainData() 后立即将其写为 TerrainData_{GUID}.asset
                // 临时文件，之后 CreateAsset 创建正式路径文件，UUID 文件就成了孤立垃圾文件。
                string hmDir = Path.GetDirectoryName(heightmapAssetPath.Replace('\\', '/'));
                string hmBase = Path.GetFileNameWithoutExtension(heightmapAssetPath);
                string terrainDataCandidate =
                    string.IsNullOrEmpty(hmDir)
                        ? hmBase + "_TerrainData.asset"
                        : hmDir + "/" + hmBase + "_TerrainData.asset";
                string terrainDataPath = AssetDatabase.GenerateUniqueAssetPath(terrainDataCandidate);

                var terrainData = new TerrainData();
                AssetDatabase.CreateAsset(terrainData, terrainDataPath);  // 先注册路径，避免 UUID 孤立文件

                terrainData.heightmapResolution = heightmapResolution;
                terrainData.size = new Vector3(terrainWidth, terrainMaxHeight, terrainLength);

                float[,] heights = ConvertTextureToHeights(tex, heightmapResolution);
                terrainData.SetHeights(0, 0, heights);

                EditorUtility.SetDirty(terrainData);
                AssetDatabase.SaveAssets();

                GameObject terrainGo = Terrain.CreateTerrainGameObject(terrainData);
                terrainGo.name = terrainGoName;

                Undo.RegisterCreatedObjectUndo(terrainGo, TJGeneratorsL10n.L("一键生成地形"));
                Selection.activeGameObject = terrainGo;
                EditorGUIUtility.PingObject(terrainGo);

                TJLog.Log($"{LogTag} 已创建地形: {terrainGo.name}, TerrainData: {terrainDataPath}");

                return (terrainDataPath, terrainGo.name);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>生成与历史原图同目录、带 _processed 后缀的唯一 PNG 资产路径。</summary>
        public static string GenerateProcessedHeightmapAssetPath(string originalAssetPath)
        {
            if (string.IsNullOrEmpty(originalAssetPath))
                return null;

            string dir = Path.GetDirectoryName(originalAssetPath.Replace('\\', '/'));
            string baseName = Path.GetFileNameWithoutExtension(originalAssetPath);
            string candidate =
                string.IsNullOrEmpty(dir)
                    ? baseName + "_processed.png"
                    : dir + "/" + baseName + "_processed.png";

            return AssetDatabase.GenerateUniqueAssetPath(candidate);
        }

        /// <summary>
        /// 完整的"后处理 → 创建 Terrain"流程：
        ///   1. 复制原图到 *_processed.png（避免污染历史文件）
        ///   2. 调用 TerrainHeightmapPostProcessor.TryPostProcessHeightmapImage
        ///   3. AssetDatabase.ImportAsset
        ///   4. CreateTerrainFromHeightmap
        /// 返回 (terrainDataPath, terrainGoName, processedAssetPath, error)；error 非 null 表示失败。
        /// </summary>
        public static (string terrainDataPath, string terrainGoName, string processedAssetPath, string error)
            PostProcessAndCreateTerrain(
                string originalHeightmapAssetPath,
                TerrainHeightmapPostProcessOptions options,
                string terrainGoName = "TJGenerators Terrain")
        {
            if (string.IsNullOrEmpty(originalHeightmapAssetPath))
                return (null, null, null, TJGeneratorsL10n.L("originalHeightmapAssetPath 为空"));

            string originalPath = originalHeightmapAssetPath.Replace('\\', '/');
            string absOriginal = PathUtils.ToAbsoluteAssetPath(originalPath);
            if (!File.Exists(absOriginal))
                return (null, null, null, string.Format(TJGeneratorsL10n.L("高度图文件不存在: {0}"), absOriginal));

            string processedAssetPath = GenerateProcessedHeightmapAssetPath(originalPath);
            if (string.IsNullOrEmpty(processedAssetPath))
                return (null, null, null, TJGeneratorsL10n.L("无法生成后处理文件路径"));

            string absProcessed = PathUtils.ToAbsoluteAssetPath(processedAssetPath);
            string processedDir = Path.GetDirectoryName(absProcessed);
            if (!string.IsNullOrEmpty(processedDir) && !Directory.Exists(processedDir))
                Directory.CreateDirectory(processedDir);

            try
            {
                File.Copy(absOriginal, absProcessed, true);
            }
            catch (Exception e)
            {
                return (null, null, null, string.Format(TJGeneratorsL10n.L("复制高度图失败: {0}"), e.Message));
            }

            if (!TerrainHeightmapPostProcessor.TryPostProcessHeightmapImage(absProcessed, out string hmErr, options))
                return (null, null, null, hmErr ?? TJGeneratorsL10n.L("后处理失败（未知错误）"));

            AssetDatabase.ImportAsset(processedAssetPath, ImportAssetOptions.ForceUpdate);

            try
            {
                var (terrainDataPath, goName) = CreateTerrainFromHeightmap(processedAssetPath, terrainGoName);
                return (terrainDataPath, goName, processedAssetPath, null);
            }
            catch (Exception e)
            {
                return (null, null, null, "Terrain creation failed: " + e.Message);
            }
        }
    }
}
#endif
