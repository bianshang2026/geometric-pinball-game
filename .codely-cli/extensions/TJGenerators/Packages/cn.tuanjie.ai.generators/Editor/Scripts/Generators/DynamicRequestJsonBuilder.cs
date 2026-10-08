#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using TJGenerators.Config;
using TJGenerators.Utils;
using UnityEngine;

namespace TJGenerators.Generators
{
    /// <summary>
    /// 从 <see cref="DynamicRequestBuildContext"/> 构建动态生成器的 JSON 请求体与增强提示词。
    /// </summary>
    internal static class DynamicRequestJsonBuilder
    {
        public static bool IsRodinGenerator(GeneratorConfig config)
        {
            return config != null
                && string.Equals(config.id, "rodin", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTencentGeneration(GeneratorConfig config)
        {
            return config != null
                && string.Equals(
                    config.id,
                    "tencent-generation",
                    StringComparison.OrdinalIgnoreCase
                );
        }

        /// <summary>
        /// Rodin 参考图：是否存在至少一张将写入请求体的本地可读文件（与 BuildRequestJson 打包逻辑一致）。
        /// </summary>
        public static bool RodinHasAnyValidReferenceImage(DynamicRequestBuildContext ctx)
        {
            if (ctx.ImagePaths != null && ctx.ImagePaths.Count > 0)
            {
                foreach (var p in ctx.ImagePaths)
                {
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                        return true;
                }
                return false;
            }
            return !string.IsNullOrEmpty(ctx.ImagePath) && File.Exists(ctx.ImagePath);
        }

        /// <summary>
        /// Rodin：有有效参考图且无有效增强提示词时为 fuse（纯图条件），否则 concat。
        /// </summary>
        public static string ComputeRodinConditionMode(DynamicRequestBuildContext ctx)
        {
            bool hasImage = RodinHasAnyValidReferenceImage(ctx);
            bool hasText = !string.IsNullOrWhiteSpace(BuildEnhancedPrompt(ctx));
            return hasImage && !hasText ? "fuse" : "concat";
        }

        /// <summary>
        /// 构建增强后的提示词，将类型和风格拼接到用户输入中
        /// </summary>
        public static string BuildEnhancedPrompt(DynamicRequestBuildContext ctx)
        {
            var config = ctx.Config;
            var parts = new List<string>();

            bool isMaterialMode = string.Equals(
                config.outputType,
                "material",
                StringComparison.OrdinalIgnoreCase
            );
            if (isMaterialMode)
            {
                parts.Add("seamless texture");
                parts.Add("tileable");
                parts.Add("PBR material");
                parts.Add("high quality texture");
                parts.Add("game ready");

                if (!string.IsNullOrEmpty(ctx.TextPrompt))
                    parts.Add(ctx.TextPrompt);

                return string.Join(", ", parts);
            }

            bool isSpriteMode = string.Equals(
                config.outputType,
                "sprite",
                StringComparison.OrdinalIgnoreCase
            );
            if (isSpriteMode)
            {
                parts.Add("game asset");
                parts.Add("2d game icon");
                parts.Add("single centered subject");
                parts.Add("solid pure white background");
                parts.Add("clean cutout ready");
                parts.Add("no shadows");
                parts.Add("no background elements");

                if (ctx.SelectedType != null && ctx.SelectedType.id != "none")
                {
                    parts.Add(ctx.SelectedType.name);
                    parts.Add("must be " + ctx.SelectedType.name);
                    parts.Add("strictly " + ctx.SelectedType.name + " type");
                }

                bool isSpecialViewStyle =
                    ctx.SelectedStyle != null && IsSpecialViewStyle(ctx.SelectedStyle.id);

                if (!isSpecialViewStyle)
                {
                    parts.Add("front view");
                    parts.Add("orthographic projection");
                    parts.Add("facing camera");
                    parts.Add("no rotation");
                    parts.Add("no perspective distortion");
                }

                if (ctx.SelectedStyle != null && ctx.SelectedStyle.id != "none")
                {
                    if (isSpecialViewStyle)
                        parts.Add(GetStyleEnglishName(ctx.SelectedStyle.id));
                    else
                        parts.Add(ctx.SelectedStyle.name + " style");
                }
            }

            if (
                ctx.SelectedPromptTemplate != null
                && !string.IsNullOrWhiteSpace(ctx.SelectedPromptTemplate.prompt)
            )
            {
                parts.Add(ctx.SelectedPromptTemplate.prompt.Trim());
            }

            if (!string.IsNullOrEmpty(ctx.TextPrompt))
                parts.Add(ctx.TextPrompt);

            return string.Join(", ", parts);
        }

        public static string BuildRequestJson(DynamicRequestBuildContext ctx)
        {
            var root = new JObject();
            var config = ctx.Config;
            var uiLayout = config.uiLayout ?? new UILayoutConfig();
            bool isMultiViewRequest =
                ctx.CurrentInputMode == "multiview" && ctx.MultiViewCount > 0;

            TJLog.Log(
                $"[DynamicRequestJsonBuilder] BuildRequestJson: inputMode={ctx.CurrentInputMode}, multiViewCount={ctx.MultiViewCount}"
            );

            bool isAudio = string.Equals(
                config.outputType,
                "audio",
                StringComparison.OrdinalIgnoreCase
            );
            if (isAudio && !string.IsNullOrEmpty(ctx.TextPrompt))
            {
                string audioTextFieldName = !string.IsNullOrEmpty(config.textInputFieldName)
                    ? config.textInputFieldName
                    : "text";
                root[audioTextFieldName] = ctx.TextPrompt;
            }
            else
            {
                if (!isMultiViewRequest)
                {
                    bool hasSingleOrMultiImage =
                        (!string.IsNullOrEmpty(ctx.ImagePath) && File.Exists(ctx.ImagePath))
                        || (ctx.ImagePaths != null && ctx.ImagePaths.Count > 0);
                    if (!IsTencentGeneration(config) || !hasSingleOrMultiImage)
                    {
                        string enhancedPrompt = BuildEnhancedPrompt(ctx);
                        if (!string.IsNullOrEmpty(enhancedPrompt))
                        {
                            string textFieldName = !string.IsNullOrEmpty(config.textInputFieldName)
                                ? config.textInputFieldName
                                : "prompt";
                            root[textFieldName] = enhancedPrompt;
                        }
                    }
                }
            }

            string imageKey = !string.IsNullOrEmpty(config.imageBase64FieldName)
                ? config.imageBase64FieldName
                : "imageBase64";
            if (!isMultiViewRequest && ctx.ImagePaths != null && ctx.ImagePaths.Count > 0)
            {
                var base64List = new List<string>();
                foreach (var p in ctx.ImagePaths)
                {
                    if (string.IsNullOrEmpty(p) || !File.Exists(p))
                        continue;
                    byte[] imageData = File.ReadAllBytes(p);
                    imageData = CompressImageIfNeeded(imageData, p);
                    string base64 = Convert.ToBase64String(imageData);
                    if (config.imageBase64WithPrefix)
                    {
                        string ext = Path.GetExtension(p).ToLower();
                        string mimeType = ext == ".png" ? "image/png" : "image/jpeg";
                        base64 = $"data:{mimeType};base64,{base64}";
                    }
                    base64List.Add(base64);
                }
                if (base64List.Count > 0)
                {
                    bool sendAsArray = ctx.ImagePaths.Count > 1 || config.imageBase64AsArray;
                    if (sendAsArray)
                    {
                        root[imageKey] = JArray.FromObject(base64List);
                    }
                    else
                    {
                        root[imageKey] = base64List[0];
                        string fileName = Path.GetFileName(ctx.ImagePath);
                        string ext = Path.GetExtension(ctx.ImagePath).ToLower();
                        string ct = ext == ".png" ? "image/png" : "image/jpeg";
                        root["imageName"] = fileName;
                        root["contentType"] = ct;
                    }
                }
            }
            else if (
                !isMultiViewRequest
                && !string.IsNullOrEmpty(ctx.ImagePath)
                && File.Exists(ctx.ImagePath)
            )
            {
                byte[] imageData = File.ReadAllBytes(ctx.ImagePath);
                imageData = CompressImageIfNeeded(imageData, ctx.ImagePath);
                string base64 = Convert.ToBase64String(imageData);
                string fileName = Path.GetFileName(ctx.ImagePath);
                string ext = Path.GetExtension(ctx.ImagePath).ToLower();
                string contentType = ext == ".png" ? "image/png" : "image/jpeg";

                if (config.imageBase64WithPrefix)
                    base64 = $"data:{contentType};base64,{base64}";

                if (config.imageBase64AsArray)
                    root[imageKey] = JArray.FromObject(new[] { base64 });
                else
                    root[imageKey] = base64;

                if (!config.imageBase64WithPrefix)
                {
                    root["imageName"] = fileName;
                    root["contentType"] = contentType;
                }
            }

            if (ctx.CurrentInputMode == "multiview" && ctx.MultiViewCount > 0)
            {
                var validPaths = new List<string>();
                var validIndices = new List<int>();
                for (int i = 0; i < ctx.MultiViewPaths.Count && i < 4; i++)
                {
                    if (!string.IsNullOrEmpty(ctx.MultiViewPaths[i]) && File.Exists(ctx.MultiViewPaths[i]))
                    {
                        validPaths.Add(ctx.MultiViewPaths[i]);
                        validIndices.Add(i);
                    }
                }

                TJLog.Log(
                    $"[DynamicRequestJsonBuilder][MultiView] BuildRequestJson: inputMode={ctx.CurrentInputMode}, "
                        + $"multiViewPathsCount={(ctx.MultiViewPaths == null ? 0 : ctx.MultiViewPaths.Count)}, "
                        + $"validCount={validPaths.Count}, validIndices={string.Join(",", validIndices.ToArray())}"
                );

                if (validPaths.Count > 0)
                {
                    if (IsTencentGeneration(config))
                    {
                        string frontPath = null;
                        for (int k = 0; k < validIndices.Count; k++)
                        {
                            if (validIndices[k] != 0)
                                continue;
                            frontPath = validPaths[k];
                            break;
                        }

                        if (!string.IsNullOrEmpty(frontPath) && File.Exists(frontPath))
                        {
                            byte[] frontBytes = File.ReadAllBytes(frontPath);
                            frontBytes = CompressImageIfNeeded(frontBytes, frontPath);
                            string frontB64 = Convert.ToBase64String(frontBytes);
                            string imageField =
                                !string.IsNullOrEmpty(config.imageBase64FieldName)
                                    ? config.imageBase64FieldName
                                    : "image";
                            root[imageField] = frontB64;

                            var mvArray = new JArray();
                            for (int k = 0; k < validIndices.Count; k++)
                            {
                                int slot = validIndices[k];
                                if (slot == 0)
                                    continue;
                                string viewType;
                                switch (slot)
                                {
                                    case 1: viewType = "left"; break;
                                    case 2: viewType = "back"; break;
                                    case 3: viewType = "right"; break;
                                    default: viewType = null; break;
                                }
                                if (viewType == null)
                                    continue;
                                string path = validPaths[k];
                                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                                    continue;
                                byte[] img = File.ReadAllBytes(path);
                                img = CompressImageIfNeeded(img, path);
                                string b64 = Convert.ToBase64String(img);
                                mvArray.Add(
                                    new JObject { ["viewType"] = viewType, ["viewImage"] = b64 }
                                );
                            }

                            if (mvArray.Count > 0)
                                root["multiViewImages"] = mvArray;
                        }
                    }
                    else if (config.imageBase64AsArray)
                    {
                        string multiImageKey = !string.IsNullOrEmpty(config.imageBase64FieldName)
                            ? config.imageBase64FieldName
                            : "files";

                        var base64List = new List<string>();
                        foreach (var path in validPaths)
                        {
                            byte[] imageData = File.ReadAllBytes(path);
                            imageData = CompressImageIfNeeded(imageData, path);
                            string base64 = Convert.ToBase64String(imageData);

                            if (config.imageBase64WithPrefix)
                            {
                                string ext = Path.GetExtension(path).ToLower();
                                string mimeType = ext == ".png" ? "image/png" : "image/jpeg";
                                base64 = $"data:{mimeType};base64,{base64}";
                            }
                            base64List.Add(base64);
                        }
                        root[multiImageKey] = JArray.FromObject(base64List);
                    }
                    else
                    {
                        string multiImageKey = !string.IsNullOrEmpty(config.imageBase64FieldName)
                            ? config.imageBase64FieldName
                            : "files";

                        var tripoViews = new JArray();
                        for (int i = 0; i < validPaths.Count; i++)
                        {
                            byte[] imageData = File.ReadAllBytes(validPaths[i]);
                            imageData = CompressImageIfNeeded(imageData, validPaths[i]);
                            string base64 = Convert.ToBase64String(imageData);

                            if (config.imageBase64WithPrefix)
                            {
                                string ext = Path.GetExtension(validPaths[i]).ToLower();
                                string mimeType = ext == ".png" ? "image/png" : "image/jpeg";
                                base64 = $"data:{mimeType};base64,{base64}";
                                tripoViews.Add(base64);
                            }
                            else
                            {
                                tripoViews.Add(new JObject { ["imageBase64"] = base64 });
                            }
                        }
                        root[multiImageKey] = tripoViews;
                    }
                }
            }

            if (IsRodinGenerator(config))
                root["conditionMode"] = ComputeRodinConditionMode(ctx);

            ParameterJsonWriter.Apply(
                root,
                config.parameters,
                ctx.ParameterValues,
                ctx.CurrentInputMode
            );
            ParameterJsonWriter.ApplyFixedFields(root, config.fixedFields);

            if (ctx.ExtraRawJsonFields.Count > 0)
            {
                foreach (var kv in ctx.ExtraRawJsonFields)
                {
                    if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value))
                        continue;
                    root[kv.Key] = JToken.Parse(kv.Value);
                }
            }

            string json = root.ToString(Formatting.None);

            string logJson = json;
            if (json.Length > 2000)
                logJson = json.Substring(0, 2000) + "... (truncated)";
            TJLog.Log($"[DynamicRequestJsonBuilder] BuildRequestJson 生成的JSON: {logJson}");

            return json;
        }

        private const int MaxImageBytes = 10 * 1024 * 1024; // 10 MB
        private const int MaxImageDim = 2048; // 压缩目标边长上限

        /// <summary>
        /// 若 rawBytes 超过 10MB，将图片缩放到 MaxImageDim 以内并以 JPG 85 质量重编码。
        /// 返回压缩后的字节（原始字节若未超限则原样返回）。
        /// </summary>
        internal static byte[] CompressImageIfNeeded(byte[] rawBytes, string filePath)
        {
            if (rawBytes.Length <= MaxImageBytes)
                return rawBytes;

            var tex = new Texture2D(2, 2);
            if (!TryLoadImageForCompress(tex, rawBytes))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                Debug.LogWarning(
                    $"[TJGenerators] 参考图 {Path.GetFileName(filePath)} 超过 10MB 但无法解码，已跳过压缩。"
                );
                return rawBytes;
            }

            float scale = Mathf.Min((float)MaxImageDim / tex.width, (float)MaxImageDim / tex.height);
            int w = scale < 1f ? Mathf.Max(1, (int)(tex.width * scale)) : tex.width;
            int h = scale < 1f ? Mathf.Max(1, (int)(tex.height * scale)) : tex.height;

            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var scaled = new Texture2D(w, h, TextureFormat.RGBA32, false);
            scaled.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            scaled.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(tex);

            byte[] result = scaled.EncodeToJPG(85);
            UnityEngine.Object.DestroyImmediate(scaled);

            Debug.LogWarning(
                $"[TJGenerators] 参考图 {Path.GetFileName(filePath)} "
                    + $"原大小 {rawBytes.Length / 1024f / 1024f:F1}MB 超过 10MB，"
                    + $"已自动缩放至 {w}×{h} 并压缩。"
            );
            return result;
        }

        /// <summary>
        /// Unity 2019/2020：非法数据时 LoadImage 仍可能返回 true，并填入 8×8 "?" 占位图。
        /// 先做 PNG/JPEG 头校验，再排除该占位尺寸。
        /// </summary>
        private static bool TryLoadImageForCompress(Texture2D tex, byte[] rawBytes)
        {
            if (!HasSupportedImageHeader(rawBytes))
                return false;
            if (!tex.LoadImage(rawBytes))
                return false;
            // 超限文件解码成 8×8 几乎一定是失败占位图，而非真实参考图
            if (tex.width == 8 && tex.height == 8)
                return false;
            return tex.width > 0 && tex.height > 0;
        }

        private static bool HasSupportedImageHeader(byte[] data)
        {
            if (data == null || data.Length < 3)
                return false;
            // JPEG
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return true;
            // PNG
            if (data.Length >= 4
                && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                return true;
            return false;
        }

        private static bool IsSpecialViewStyle(string styleId)
        {
            if (string.IsNullOrEmpty(styleId))
                return false;
            return styleId == "isometric" || styleId == "top_down" || styleId == "side_scroller";
        }

        private static string GetStyleEnglishName(string styleId)
        {
            switch (styleId)
            {
                case "isometric":     return "isometric view";
                case "top_down":      return "top down view";
                case "side_scroller": return "side scroller view";
                default:              return styleId;
            }
        }
    }
}

#endif
