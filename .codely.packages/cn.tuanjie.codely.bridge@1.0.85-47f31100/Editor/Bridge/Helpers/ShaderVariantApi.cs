using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Compatibility surface for <c>UnityEngine.Rendering.PassType</c> and
    /// <see cref="ShaderVariantCollection.ShaderVariant"/> construction.
    ///
    /// Tuanjie / Unity editors disagree on which assembly hosts PassType and
    /// which enum members exist (e.g. ScriptableRenderPipeline*). Referencing
    /// the type at compile time produces CS0246 on some editors, so every
    /// lookup and constructor call goes through reflection. Callers speak
    /// string pass names; missing types or members are skipped, not compiled
    /// against.
    /// </summary>
    public static class ShaderVariantApi
    {
        static readonly Type PassTypeType = ResolvePassTypeType();
        static readonly ConstructorInfo VariantCtor = ResolveVariantCtor();

        static readonly Dictionary<string, string> Aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["forward"] = "ForwardBase",
                ["forwardlit"] = "ForwardBase",
                ["forwardbase"] = "ForwardBase",
                ["forwardadd"] = "ForwardAdd",
                ["shadowcaster"] = "ShadowCaster",
                ["deferred"] = "Deferred",
                ["srp"] = "ScriptableRenderPipeline",
                ["scriptablerp"] = "ScriptableRenderPipeline",
                ["defaultunlit"] = "ScriptableRenderPipelineDefaultUnlit",
            };

        static readonly string[] DefaultPassNames =
        {
            "Normal",
            "ShadowCaster",
            "ScriptableRenderPipeline",
        };

        /// <summary>True when this editor exposes a PassType enum we can resolve.</summary>
        public static bool IsAvailable => PassTypeType != null && PassTypeType.IsEnum;

        public static IReadOnlyList<object> DefaultPassTypes()
        {
            var result = new List<object>();
            if (!IsAvailable)
                return result;

            foreach (var name in DefaultPassNames)
            {
                if (TryParseEnum(name, out var value) && !result.Contains(value))
                    result.Add(value);
            }

            if (result.Count == 0 && TryParseEnum("Normal", out var normal))
                result.Add(normal);

            return result;
        }

        /// <summary>
        /// Maps a human pass name (or alias such as "forwardlit") to a boxed
        /// PassType. Unknown names fall back to Normal when that member exists.
        /// Returns false when PassType itself is missing on this editor.
        /// </summary>
        public static bool TryResolvePassType(string name, out object passType)
        {
            passType = null;
            if (!IsAvailable)
                return false;

            if (!string.IsNullOrEmpty(name))
            {
                if (Aliases.TryGetValue(name, out var canonical)
                    && TryParseEnum(canonical, out passType))
                    return true;

                if (TryParseEnum(name, out passType))
                    return true;
            }

            return TryParseEnum("Normal", out passType);
        }

        public static bool TryCreateVariant(
            Shader shader,
            object passType,
            string[] keywords,
            out ShaderVariantCollection.ShaderVariant variant)
        {
            variant = default;
            if (shader == null || passType == null || VariantCtor == null)
                return false;
            if (PassTypeType != null && !PassTypeType.IsInstanceOfType(passType))
                return false;

            try
            {
                object boxed = InvokeVariantCtor(shader, passType, keywords ?? new string[0]);
                if (boxed == null)
                    return false;
                variant = (ShaderVariantCollection.ShaderVariant)boxed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static object InvokeVariantCtor(Shader shader, object passType, string[] keywords)
        {
            var ps = VariantCtor.GetParameters();
            if (ps.Length == 2)
                return VariantCtor.Invoke(new[] { shader, passType });
            if (ps.Length >= 3)
                return VariantCtor.Invoke(new object[] { shader, passType, keywords });
            return null;
        }

        static bool TryParseEnum(string name, out object value)
        {
            value = null;
            if (PassTypeType == null || string.IsNullOrEmpty(name))
                return false;

            try
            {
                value = Enum.Parse(PassTypeType, name, ignoreCase: true);
                return value != null && Enum.IsDefined(PassTypeType, value);
            }
            catch
            {
                return false;
            }
        }

        static Type ResolvePassTypeType()
        {
            string[] candidates =
            {
                "UnityEngine.Rendering.PassType, UnityEngine.CoreModule",
                "UnityEngine.Rendering.PassType, UnityEngine",
                "UnityEngine.Rendering.PassType, UnityEngine.RenderingModule",
            };
            foreach (var id in candidates)
            {
                var t = Type.GetType(id);
                if (t != null && t.IsEnum)
                    return t;
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType("UnityEngine.Rendering.PassType"); }
                catch { }
                if (t != null && t.IsEnum)
                    return t;
            }

            return null;
        }

        static ConstructorInfo ResolveVariantCtor()
        {
            if (PassTypeType == null)
                return null;

            foreach (var ctor in typeof(ShaderVariantCollection.ShaderVariant).GetConstructors())
            {
                var ps = ctor.GetParameters();
                if (ps.Length >= 2
                    && ps[0].ParameterType == typeof(Shader)
                    && ps[1].ParameterType == PassTypeType)
                    return ctor;
            }

            return null;
        }
    }
}
