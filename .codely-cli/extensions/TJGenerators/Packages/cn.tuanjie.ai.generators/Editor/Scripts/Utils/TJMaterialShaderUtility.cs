#if UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 按项目当前渲染管线选择可用的 Lit Shader，并统一主贴图 /光滑度等属性，
    /// 避免在 URP/HDRP 工程里仍使用内置 Standard 导致材质与预览呈洋红色。
    /// </summary>
    public static class TJMaterialShaderUtility
    {
        /// <summary>
        /// 解析用于表面材质（反照率 + 金属/光滑度）的 Lit Shader。
        /// </summary>
        public static Shader ResolveSurfaceLitShader()
        {
            var rp = GraphicsSettings.defaultRenderPipeline;
            if (rp == null)
            {
                var standard = Shader.Find("Standard");
                if (standard != null)
                    return standard;
                return Shader.Find("Unlit/Texture");
            }

            var typeName = rp.GetType().Name;
            if (typeName.IndexOf("Universal", System.StringComparison.Ordinal) >= 0)
            {
                var lit = Shader.Find("Universal Render Pipeline/Lit");
                if (lit != null)
                    return lit;
            }

            if (typeName.IndexOf("HDRender", System.StringComparison.Ordinal) >= 0)
            {
                var lit = Shader.Find("HDRP/Lit");
                if (lit != null)
                    return lit;
            }

            return Shader.Find("Universal Render Pipeline/Lit")
                   ?? Shader.Find("HDRP/Lit")
                   ?? Shader.Find("Standard")
                   ?? Shader.Find("Unlit/Texture");
        }

        /// <summary>
        /// 在 URP/HDRP 工程里，若材质仍使用内置 Standard（Inspector 会呈洋红色），则替换为当前管线的 Lit。
        /// </summary>
        public static void EnsureCompatibleSurfaceShader(Material material)
        {
            if (material == null || GraphicsSettings.defaultRenderPipeline == null)
                return;

            var shaderName = material.shader != null ? material.shader.name : "";
            if (string.IsNullOrEmpty(shaderName))
                return;

            var isBuiltInStandard = shaderName.Equals("Standard", StringComparison.Ordinal)
                                    || shaderName.StartsWith("Standard ", StringComparison.Ordinal);
            if (!isBuiltInStandard)
                return;

            var replacement = ResolveSurfaceLitShader();
            if (replacement != null && replacement != material.shader)
                material.shader = replacement;
        }

        /// <summary>
        /// 将主颜色贴图赋给当前 Shader 支持的槽位（URP/HDRP 的 BaseMap 或内置 MainTex）。
        /// </summary>
        public static void AssignBaseColorTexture(Material material, Texture2D texture)
        {
            if (material == null || texture == null)
                return;

            if (material.HasProperty("_BaseMap"))
                material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_BaseColorMap"))
                material.SetTexture("_BaseColorMap", texture);

            material.mainTexture = texture;
        }

        /// <summary>
        /// 金属度 + 光滑度：内置 Standard 用 _Glossiness，URP Lit 等用 _Smoothness。
        /// </summary>
        public static void SetMetallicAndSmoothness(Material material, float metallic, float smoothness)
        {
            if (material == null)
                return;

            if (material.HasProperty("_Metallic"))
                material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Glossiness"))
                material.SetFloat("_Glossiness", smoothness);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", smoothness);
        }

        /// <summary>
        /// 与表面材质窗口 / CustomTool 一致的一套预设（金属、玻璃透明队列等）。
        /// </summary>
        public static void ApplySurfaceMaterialPreset(Material material, string presetId)
        {
            if (material == null || string.IsNullOrEmpty(presetId))
                return;

            material.renderQueue = -1;

            switch (presetId)
            {
                case "metal":
                    SetMetallicAndSmoothness(material, 0.8f, 0.6f);
                    break;
                case "glass":
                    SetMetallicAndSmoothness(material, 0f, 0.9f);
                    material.renderQueue = 3000;
                    break;
                case "wood":
                case "fabric":
                case "leather":
                    SetMetallicAndSmoothness(material, 0f, 0.3f);
                    break;
                case "stone":
                case "concrete":
                case "brick":
                case "tile":
                    SetMetallicAndSmoothness(material, 0f, 0.1f);
                    break;
                case "ceramic":
                    SetMetallicAndSmoothness(material, 0f, 0.7f);
                    break;
                case "grass":
                case "sand":
                case "snow":
                    SetMetallicAndSmoothness(material, 0f, 0.2f);
                    break;
                default:
                    SetMetallicAndSmoothness(material, 0f, 0.5f);
                    break;
            }
        }
    }
}
#endif
