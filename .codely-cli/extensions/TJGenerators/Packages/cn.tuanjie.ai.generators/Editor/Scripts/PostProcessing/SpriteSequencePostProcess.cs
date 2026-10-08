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
    public static class SpriteSequencePostProcess
    {
        public const float DefaultChromaTolerance = 0.16f;
        public const float DefaultChromaFeather = 0.04f;

        public struct SliceResult
        {
            public string OutputDirectory;
            public List<string> SpriteAssetPaths;
            public string AnimationClipPath;
            public int ExportedCount;
        }

        /// <summary>
        /// 绿幕抠图后切片并生成 AnimationClip。调用方负责任务状态 / UI 回写。
        /// </summary>
        public static SliceResult CutoutAndSlice(
            string assetPath,
            float tolerance,
            float feather,
            int cols,
            int rows,
            float fps,
            bool loop,
            bool writeCutoutBackToAsset = false)
        {
            Texture2D src = null;
            Texture2D cutout = null;
            try
            {
                src = LoadReadableTextureFromAssetPath(assetPath);
                if (src == null)
                    throw new InvalidOperationException("Failed to read image for cutout/slice.");

                cutout = BuildGreenScreenCutoutTexture(src, tolerance, feather);
                if (cutout == null)
                    throw new InvalidOperationException("Green-screen cutout failed.");

                if (writeCutoutBackToAsset && !string.IsNullOrEmpty(assetPath))
                {
                    string absolutePath = PathUtils.ToAbsoluteAssetPath(assetPath);
                    File.WriteAllBytes(absolutePath, cutout.EncodeToPNG());
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                }

                return SliceTextureToSpritesAndAnimation(cutout, assetPath, cols, rows, fps, loop);
            }
            finally
            {
                if (src != null) UnityEngine.Object.DestroyImmediate(src);
                if (cutout != null) UnityEngine.Object.DestroyImmediate(cutout);
            }
        }

        public static Texture2D LoadReadableTextureFromAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return null;
            string abs = PathUtils.ToAbsoluteAssetPath(assetPath);
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs))
                return null;
            byte[] bytes = File.ReadAllBytes(abs);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return null;
            }
            return tex;
        }

        public static Texture2D BuildGreenScreenCutoutTexture(Texture2D src, float tolerance, float feather)
        {
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            Color[] pixels = src.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                Color.RGBToHSV(c, out float h, out float s, out float v);

                float hueDist = Mathf.Abs(h - (1f / 3f));
                hueDist = Mathf.Min(hueDist, 1f - hueDist);
                float hueGate = Mathf.Clamp01(1f - hueDist / Mathf.Lerp(0.22f, 0.08f, Mathf.Clamp01(tolerance * 2f)));
                float satGate = Mathf.Clamp01((s - 0.12f) / 0.35f);
                float lumGate = Mathf.Clamp01((v - 0.08f) / 0.25f);
                float dominance = Mathf.Clamp01((c.g - Mathf.Max(c.r, c.b) - 0.01f) / Mathf.Max(0.02f, tolerance));
                float similarity = 1f - Vector3.Distance(new Vector3(c.r, c.g, c.b), new Vector3(0f, 1f, 0f)) / 1.73205f;
                similarity = Mathf.Clamp01(similarity);

                float key = hueGate * satGate * lumGate * dominance * similarity;
                float soften = Mathf.Max(0.001f, feather * 2f + 0.015f);
                key = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((key - 0.08f) / soften));

                c.a *= (1f - key);
                if (c.a < 0.001f)
                {
                    c.a = 0f;
                }
                else
                {
                    float maxRb = Mathf.Max(c.r, c.b);
                    float despill = key * 0.7f * Mathf.Clamp01(1f - c.a);
                    c.g = Mathf.Lerp(c.g, maxRb, despill);
                }
                pixels[i] = c;
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        public static SliceResult SliceTextureToSpritesAndAnimation(
            Texture2D sourceTexture,
            string sourceAssetPath,
            int cols,
            int rows,
            float fps,
            bool loop
        )
        {
            cols = Mathf.Max(1, cols);
            rows = Mathf.Max(1, rows);
            int frameW = sourceTexture.width / cols;
            int frameH = sourceTexture.height / rows;
            if (frameW <= 0 || frameH <= 0)
                throw new InvalidOperationException("Slice rows/columns exceed image dimensions.");

            string outputDir = CreateSpriteSliceOutputFolder(sourceAssetPath);
            var spriteAssetPaths = new List<string>();
            int exported = 0;

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int x = c * frameW;
                    int y = sourceTexture.height - (r + 1) * frameH;
                    Color[] pixels = sourceTexture.GetPixels(x, y, frameW, frameH);
                    var frameTex = new Texture2D(frameW, frameH, TextureFormat.RGBA32, false);
                    frameTex.SetPixels(pixels);
                    frameTex.Apply();

                    string assetPath = $"{outputDir}/frame_r{r + 1:D2}_c{c + 1:D2}.png";
                    File.WriteAllBytes(PathUtils.ToAbsoluteAssetPath(assetPath), frameTex.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(frameTex);

                    // 先批量写入文件，统一在循环外 Refresh + ImportAsset，
                    // 避免循环内逐帧 SaveAndReimport 触发异步重导入导致后续 LoadAssetAtPath 拿到 null。
                    spriteAssetPaths.Add(assetPath);
                    exported++;
                }
            }

            // 定向导入文件夹下所有帧 PNG，避免全量 Refresh
            PathUtils.ImportAssetsUnderFolderAfterDiskWrite(outputDir);

            // 统一设置每帧的 TextureImporter（Sprite 类型、透明通道）
            for (int i = 0; i < spriteAssetPaths.Count; i++)
            {
                var importer = AssetImporter.GetAtPath(spriteAssetPaths[i]) as TextureImporter;
                if (importer != null)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.alphaIsTransparency = true;
                    importer.SaveAndReimport();
                }
            }

            // 确保所有帧导入完成后再创建 AnimationClip
            PathUtils.ImportAssetsUnderFolderAfterDiskWrite(outputDir);
            string clipPath = CreateSpriteSequenceAnimationClip(outputDir, spriteAssetPaths, fps, loop);

            return new SliceResult
            {
                OutputDirectory = outputDir,
                SpriteAssetPaths = spriteAssetPaths,
                AnimationClipPath = clipPath,
                ExportedCount = exported
            };
        }

        private static string CreateSpriteSliceOutputFolder(string sourceAssetPath)
        {
            // 与其他生成器行为对齐：切割导出统一落在 History 下，
            // 避免因 sourceAssetPath 在 TJGenerators 根目录而导致导出散落到根目录。
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                AssetDatabase.CreateFolder("Assets/TJGenerators", "History");

            string sourceName = Path.GetFileNameWithoutExtension(sourceAssetPath);
            if (string.IsNullOrEmpty(sourceName))
                sourceName = "Image";
            string folderName = $"{sourceName}_slices_{DateTime.Now:yyyyMMdd_HHmmss}";
            string baseFolder = $"Assets/TJGenerators/History/{folderName}";
            string unique = AssetDatabase.GenerateUniqueAssetPath(baseFolder);
            EnsureAssetFolder(unique);
            return unique;
        }

        private static void EnsureAssetFolder(string folderPath)
        {
            string normalized = folderPath.Replace("\\", "/").TrimEnd('/');
            string[] parts = normalized.Split('/');
            if (parts.Length == 0)
                return;
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string CreateSpriteSequenceAnimationClip(
            string outputDir,
            List<string> spriteAssetPaths,
            float fps,
            bool loop
        )
        {
            if (string.IsNullOrEmpty(outputDir) || spriteAssetPaths == null || spriteAssetPaths.Count == 0)
                return null;

            string clipPath = AssetDatabase.GenerateUniqueAssetPath($"{outputDir}/sprite_sequence.anim");
            if (!TryBuildAndSaveSpriteAnimationClip(
                    spriteAssetPaths, clipPath, fps, loop,
                    out _, out _, out string error))
            {
                if (!string.IsNullOrEmpty(error))
                    TJLog.LogWarning($"[SpriteSequencePostProcess] {error}");
                return null;
            }
            return clipPath;
        }

        /// <summary>
        /// SpriteRenderer.m_Sprite 曲线绑定（序列帧 AnimationClip 共用）。
        /// </summary>
        public static EditorCurveBinding CreateSpriteRendererBinding()
        {
            return new EditorCurveBinding
            {
                type = typeof(SpriteRenderer),
                path = "",
                propertyName = "m_Sprite"
            };
        }

        /// <summary>
        /// 由 Sprite 列表与帧率生成 ObjectReference 关键帧。
        /// </summary>
        public static ObjectReferenceKeyframe[] BuildSpriteKeyframes(IList<Sprite> sprites, float fps)
        {
            float safeFps = Mathf.Max(1f, fps);
            var keys = new ObjectReferenceKeyframe[sprites.Count];
            for (int i = 0; i < sprites.Count; i++)
            {
                keys[i] = new ObjectReferenceKeyframe
                {
                    time = i / safeFps,
                    value = sprites[i]
                };
            }
            return keys;
        }

        /// <summary>
        /// 写入 Sprite 曲线并设置 loopTime（部分 Unity 版本可能不支持 settings）。
        /// </summary>
        public static void ApplySpriteCurveToClip(
            AnimationClip clip,
            EditorCurveBinding binding,
            ObjectReferenceKeyframe[] keys,
            bool loop)
        {
            if (clip == null) return;
            AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);

            try
            {
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = loop;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
            }
            catch
            {
                // 某些 Unity 版本下 AnimationClipSettings 可能不可用，忽略 loop 设置
            }
        }

        /// <summary>
        /// 先创建空 AnimationClip 再写曲线。若先写曲线再 CreateAsset，
        /// ImportAsset 会从磁盘反序列化覆盖内存中的 ObjectReferenceCurve，导致动画为空。
        /// </summary>
        public static bool TryBuildAndSaveSpriteAnimationClip(
            IList<string> spriteAssetPaths,
            string clipPath,
            float fps,
            bool loop,
            out EditorCurveBinding binding,
            out ObjectReferenceKeyframe[] keys,
            out string error)
        {
            binding = default;
            keys = null;
            error = null;

            var sprites = LoadSpritesFromAssetPaths(spriteAssetPaths);
            if (sprites.Count == 0)
            {
                error = "导入帧图片失败（未加载到 Sprite）";
                return false;
            }
            if (sprites.Count < spriteAssetPaths.Count)
            {
                TJLog.LogWarning(
                    $"[SpriteSequencePostProcess] 部分Sprite加载失败：成功{sprites.Count}/{spriteAssetPaths.Count}");
            }

            float safeFps = Mathf.Max(1f, fps);
            var clip = new AnimationClip { frameRate = safeFps };
            AssetDatabase.CreateAsset(clip, clipPath);
            AssetDatabase.ImportAsset(clipPath, ImportAssetOptions.ForceUpdate);

            clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
            {
                error = "创建 AnimationClip 失败";
                return false;
            }

            binding = CreateSpriteRendererBinding();
            keys = BuildSpriteKeyframes(sprites, safeFps);
            ApplySpriteCurveToClip(clip, binding, keys, loop);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            return true;
        }

        public static List<Sprite> LoadSpritesFromAssetPaths(IList<string> spriteAssetPaths)
        {
            var sprites = new List<Sprite>();
            if (spriteAssetPaths == null)
                return sprites;

            for (int i = 0; i < spriteAssetPaths.Count; i++)
            {
                var sp = AssetDatabase.LoadAssetAtPath<Sprite>(spriteAssetPaths[i]);
                if (sp == null)
                {
                    var allAssets = AssetDatabase.LoadAllAssetsAtPath(spriteAssetPaths[i]);
                    if (allAssets != null)
                    {
                        foreach (var asset in allAssets)
                        {
                            if (asset is Sprite fallbackSprite)
                            {
                                sp = fallbackSprite;
                                break;
                            }
                        }
                    }
                }
                if (sp != null)
                    sprites.Add(sp);
            }

            if (sprites.Count == 0 && spriteAssetPaths.Count > 0)
            {
                TJLog.LogWarning(
                    $"[SpriteSequencePostProcess] LoadAssetAtPath<Sprite> 全部返回 null，路径数={spriteAssetPaths.Count}，首路径={spriteAssetPaths.FirstOrDefault()}");
            }
            return sprites;
        }
    }
}
#endif
