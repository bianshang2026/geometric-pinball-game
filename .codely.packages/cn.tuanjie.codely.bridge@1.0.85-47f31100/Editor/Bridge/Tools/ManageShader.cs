using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Compile shader variants and preview.
    /// File create/read/update/delete is handled by exec_editor_script.
    /// </summary>
    public static class ManageShader
    {
        private static readonly Dictionary<string, Func<JObject, object>> ActionHandlers =
            new Dictionary<string, Func<JObject, object>>
            {
                { "compile", CompileShader },
                { "preview", PreviewShader },
            };

        /// <summary>
        /// Main handler for shader management actions.
        /// </summary>
        public static object HandleCommand(JObject @params)
            => ActionRouter.Route(@params, ActionHandlers);

        // --- Shader Compile Methods ---

        /// <summary>
        /// Compiles one or more shaders and returns structured error/warning messages.
        /// Import + variant warmup run as a <see cref="StepJob"/> so the editor main thread
        /// keeps pumping between frames — GPU shader compilation cannot complete if the
        /// main thread is blocked (ForceSynchronousImport / Thread.Sleep both deadlock).
        /// </summary>
        private static object CompileShader(JObject @params)
        {
            try
            {
                var pathsToken    = @params["paths"]           as JArray;
                var namesToken    = @params["shader_names"]    as JArray;
                var matPathsToken = @params["material_paths"]  as JArray;
                var keywordsToken = @params["keywords"]        as JArray;
                var passesToken   = @params["include_passes"]  as JArray;
                string variantMode    = @params["variant_mode"]?.ToString() ?? "common";
                int    timeoutSeconds = @params["timeout_seconds"]?.ToObject<int>() ?? 60;

                var inputPaths  = pathsToken?.Select(t => t.ToString()).ToList()  ?? new List<string>();
                var shaderNames = namesToken?.Select(t => t.ToString()).ToList()  ?? new List<string>();
                var matPaths    = matPathsToken?.Select(t => t.ToString()).ToList() ?? new List<string>();

                if (inputPaths.Count == 0 && shaderNames.Count == 0)
                    return Response.Error("compile requires at least one of 'paths' or 'shader_names'.");

                var shaderAssetPaths = new List<string>();

                foreach (var raw in inputPaths)
                {
                    string original = raw.Replace('\\', '/');
                    string p = original;
                    if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                        p = "Assets/" + p.TrimStart('/');

                    string ext = Path.GetExtension(p).ToLowerInvariant();
                    if (ext != ".shader" && ext != ".hlsl" && ext != ".cginc"
                        && ext != ".glsl" && ext != ".compute")
                    {
                        return Response.Error(
                            $"Unsupported file type '{ext}' in paths. " +
                            "Expected .shader, .hlsl, .cginc, .glsl, or .compute.");
                    }

                    string fullPath = Path.GetFullPath(Path.Combine(
                        Application.dataPath, "..", p)).Replace('\\', '/');
                    if (!File.Exists(fullPath))
                        return Response.Error($"File not found: '{original}'.");

                    if (ext == ".shader")
                    {
                        shaderAssetPaths.Add(p);
                    }
                    else
                    {
                        var found = FindShadersIncluding(p);
                        if (found.Count == 0)
                            return Response.Error(
                                $"No .shader assets found that #include '{p}'. " +
                                "Pass the parent .shader via 'paths' or use 'shader_names'.");
                        shaderAssetPaths.AddRange(found);
                    }
                }

                foreach (var shaderName in shaderNames)
                {
                    Shader s = Shader.Find(shaderName);
                    if (s == null)
                        return Response.Error(
                            $"Shader.Find(\"{shaderName}\") returned null. " +
                            "Verify the name matches the Shader \"Name/Path\" declaration in the file.");
                    string assetPath = AssetDatabase.GetAssetPath(s);
                    if (string.IsNullOrEmpty(assetPath))
                        return Response.Error($"Could not resolve asset path for shader '{shaderName}'.");
                    shaderAssetPaths.Add(assetPath);
                }

                shaderAssetPaths = shaderAssetPaths
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (shaderAssetPaths.Count == 0)
                    return Response.Error("No shader assets to compile.");

                var explicitKeywords = new List<List<string>>();
                if (keywordsToken != null)
                {
                    foreach (var combo in keywordsToken)
                    {
                        if (combo is JArray arr)
                            explicitKeywords.Add(arr.Select(k => k.ToString()).ToList());
                    }
                }

                var includePasses = passesToken?.Select(t => t.ToString()).ToList()
                    ?? new List<string>();

                return StepJobRunner.Start(
                    CommandContext.RequestId,
                    CommandContext.CommandType ?? "manage_shader",
                    new CompileShaderJob
                    {
                        AssetPaths        = shaderAssetPaths,
                        VariantMode       = variantMode,
                        MaterialPaths     = matPaths,
                        ExplicitKeywords  = explicitKeywords,
                        IncludePasses     = includePasses,
                    },
                    timeoutSeconds: timeoutSeconds);
            }
            catch (Exception e)
            {
                return Response.Error($"Shader compilation failed: {e.Message}");
            }
        }

        private const int MaxVariants = 32;

        /// <summary>
        /// Builds the list of shader variants to warm up based on variant_mode, material keywords,
        /// and optional explicit keyword arrays. Capped at MaxVariants to prevent explosion.
        /// </summary>
        private static List<ShaderVariantCollection.ShaderVariant> BuildVariantList(
            Shader shader,
            string variantMode,
            List<string> matPaths,
            List<List<string>> explicitKeywords,
            List<string> includePasses,
            out bool truncated)
        {
            truncated = false;
            var result = new List<ShaderVariantCollection.ShaderVariant>();

            // PassType is resolved through ShaderVariantApi so older Tuanjie
            // editors missing the type (or individual enum members) still compile.
            var passList = new List<object>();
            if (includePasses != null)
            {
                foreach (var p in includePasses)
                {
                    if (ShaderVariantApi.TryResolvePassType(p, out var pt)
                        && !passList.Contains(pt))
                        passList.Add(pt);
                }
            }
            if (passList.Count == 0)
            {
                foreach (var pt in ShaderVariantApi.DefaultPassTypes())
                    passList.Add(pt);
            }

            // Build keyword combinations.
            var keywordCombos = new List<string[]>();

            if (variantMode == "explicit")
            {
                if (explicitKeywords != null)
                {
                    foreach (var combo in explicitKeywords)
                    {
                        if (combo != null)
                            keywordCombos.Add(combo.ToArray());
                    }
                }
                // Ensure at least the no-keyword variant.
                if (keywordCombos.Count == 0)
                    keywordCombos.Add(new string[0]);
            }
            else
            {
                // Always include the no-keyword (default) variant.
                keywordCombos.Add(new string[0]);

                // Add keyword combos from materials (used / common both use this).
                foreach (var mp in matPaths ?? Enumerable.Empty<string>())
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
                    if (mat?.shaderKeywords?.Length > 0)
                    {
                        string[] kws = mat.shaderKeywords.OrderBy(k => k).ToArray();
                        string sig = string.Join(",", kws);
                        if (!keywordCombos.Any(c => string.Join(",", c.OrderBy(k => k)) == sig))
                            keywordCombos.Add(kws);
                    }
                }

                if (variantMode == "common")
                {
                    // Instancing variants.
                    keywordCombos.Add(new[] { "INSTANCING_ON" });
                    keywordCombos.Add(new[] { "INSTANCING_OFF" });

                    // Current SRP default keywords.
                    var currentRP = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                    if (currentRP != null)
                    {
                        string rpName = currentRP.GetType().FullName.ToLowerInvariant();
                        if (rpName.Contains("universal") || rpName.Contains("urp"))
                        {
                            keywordCombos.Add(new[] { "_MAIN_LIGHT_SHADOWS" });
                            keywordCombos.Add(new[] { "_ADDITIONAL_LIGHTS" });
                            keywordCombos.Add(new[] { "_MAIN_LIGHT_SHADOWS", "_ADDITIONAL_LIGHTS" });
                        }
                        else if (rpName.Contains("highdefinition") || rpName.Contains("hdrp"))
                        {
                            keywordCombos.Add(new[] { "SHADOW_LOW" });
                            keywordCombos.Add(new[] { "SHADOW_MEDIUM" });
                        }
                    }
                }
            }

            // Cross-product: passList × keywordCombos, capped at MaxVariants.
            foreach (var passType in passList)
            {
                foreach (var kwCombo in keywordCombos)
                {
                    if (result.Count >= MaxVariants)
                    {
                        truncated = true;
                        return result;
                    }
                    if (ShaderVariantApi.TryCreateVariant(shader, passType, kwCombo, out var variant))
                        result.Add(variant);
                }
            }

            // include_passes may resolve to a PassType the shader does not have
            // (e.g. "ForwardLit" -> ForwardBase on a Built-in Unlit / SRP shader).
            // Fall back to DefaultPassTypes so we still compile something.
            if (result.Count == 0 && includePasses != null && includePasses.Count > 0)
            {
                foreach (var fallbackPass in ShaderVariantApi.DefaultPassTypes())
                {
                    if (passList.Contains(fallbackPass)) continue;
                    foreach (var kwCombo in keywordCombos)
                    {
                        if (result.Count >= MaxVariants)
                        {
                            truncated = true;
                            return result;
                        }
                        if (ShaderVariantApi.TryCreateVariant(shader, fallbackPass, kwCombo, out var variant))
                            result.Add(variant);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Tries to read Tuanjie-extended fields (passName, shaderKeywords) from a ShaderMessage.
        /// Uses reflection for cross-version compatibility; omits the fields silently if absent.
        /// </summary>
        private static void TryAddExtendedMessageFields(ShaderMessage msg, Dictionary<string, object> entry)
        {
            try
            {
                Type t = msg.GetType();
                const BindingFlags bf = BindingFlags.Public | BindingFlags.Instance;

                // passName — Tuanjie extended field
                FieldInfo passField = t.GetField("passName", bf)
                    ?? t.GetField("PassName", bf);
                if (passField != null)
                {
                    object val = passField.GetValue(msg);
                    if (val != null && !string.IsNullOrEmpty(val.ToString()))
                        entry["pass"] = val.ToString();
                }

                // shaderKeywords — Tuanjie extended field
                FieldInfo kwField = t.GetField("shaderKeywords", bf)
                    ?? t.GetField("ShaderKeywords", bf);
                if (kwField != null)
                {
                    object val = kwField.GetValue(msg);
                    if (val is string[] arr && arr.Length > 0)
                        entry["keywords"] = arr;
                    else if (val is string s && !string.IsNullOrEmpty(s))
                        entry["keywords"] = s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }
            catch
            {
                // Ignore — extended fields not available in this editor version.
            }

            if (entry.ContainsKey("pass") && entry.ContainsKey("keywords"))
                return;

            if (TryParsePassAndKeywordsFromText(msg.message, out string parsedPass, out string[] parsedKws))
            {
                if (!entry.ContainsKey("pass") && !string.IsNullOrEmpty(parsedPass))
                    entry["pass"] = parsedPass;
                if (!entry.ContainsKey("keywords") && parsedKws != null && parsedKws.Length > 0)
                    entry["keywords"] = parsedKws;
            }
        }

        private static readonly Regex PassFromTextRegex = new Regex(
            @"Pass:\s*(?<pass>\w+)|pass\s+name\s+""(?<pass>[^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex KeywordsFromTextRegex = new Regex(
            @"(?:with|keywords?:)\s*(?<kws>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Extracts pass / keywords from Unity's embedded ShaderMessage text when
        /// Tuanjie-extended fields are not present on ShaderMessage.
        /// </summary>
        private static bool TryParsePassAndKeywordsFromText(
            string messageText, out string pass, out string[] keywords)
        {
            pass = null;
            keywords = null;
            if (string.IsNullOrEmpty(messageText))
                return false;

            var passMatch = PassFromTextRegex.Match(messageText);
            if (passMatch.Success)
                pass = passMatch.Groups["pass"].Value;

            var kwMatch = KeywordsFromTextRegex.Match(messageText);
            if (kwMatch.Success)
            {
                string raw = kwMatch.Groups["kws"].Value.Trim();
                if (raw.IndexOf("<no keywords>", StringComparison.OrdinalIgnoreCase) < 0
                    && !string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase))
                {
                    keywords = raw.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }

            return pass != null || keywords != null;
        }

        /// <summary>
        /// Creates a deduplication key from a message entry so that the same error reported
        /// both by GetShaderMessages and by the Console log is not counted twice.
        /// </summary>
        private static string MakeMessageSig(Dictionary<string, object> entry)
        {
            string file = entry.TryGetValue("file",    out var f) ? f?.ToString() ?? "" : "";
            string line = entry.TryGetValue("line",    out var l) ? l?.ToString() ?? "" : "";
            string msg  = entry.TryGetValue("message", out var m) ? m?.ToString() ?? "" : "";
            return $"{file}:{line}:{msg}";
        }

        // Patterns for shader error lines emitted to the Unity Console during WarmUp.
        // Unity does not expose variant-compile errors via ShaderUtil.GetShaderMessages;
        // they only appear in the log (Application.logMessageReceived).
        private static readonly Regex[] ShaderLogPatterns = new Regex[]
        {
            // "Shader error in 'NAME': MESSAGE at FILEPATH line N"  (most common)
            new Regex(
                @"Shader\s+(?:error|warning)\s+in\s+'[^']*':\s*(?<message>.+?)\s+at\s+(?<file>\S+)\s+line\s+(?<line>\d+)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // "... at FILEPATH(N)" or "... at FILEPATH line N" without leading 'Shader error'
            new Regex(
                @"(?<message>.+?)\s+at\s+(?<file>[^\s(]+)\((?<line>\d+)\)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        private static readonly Regex ShaderNameFromLogRegex = new Regex(
            @"Shader\s+(?:error|warning)\s+in\s+'([^']+)'",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool IsShaderRelatedLog(string logMsg)
        {
            return logMsg.IndexOf("shader",                StringComparison.OrdinalIgnoreCase) >= 0
                || logMsg.IndexOf("undeclared identifier", StringComparison.OrdinalIgnoreCase) >= 0
                || logMsg.IndexOf("unexpected token",      StringComparison.OrdinalIgnoreCase) >= 0
                || logMsg.IndexOf("cannot convert",        StringComparison.OrdinalIgnoreCase) >= 0
                || logMsg.IndexOf("implicit truncation",   StringComparison.OrdinalIgnoreCase) >= 0
                || logMsg.IndexOf("use of undeclared",     StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Attempts to parse a Unity Console log message emitted during shader variant compilation
        /// into a structured message entry. Returns null if the message is unrelated to shaders.
        /// </summary>
        private static Dictionary<string, object> TryParseShaderLogMessage(
            string logMsg, string fallbackAssetPath, bool isError)
        {
            if (!IsShaderRelatedLog(logMsg)) return null;

            string severity = isError ? "error" : "warning";

            foreach (var pattern in ShaderLogPatterns)
            {
                var m = pattern.Match(logMsg);
                if (!m.Success) continue;
                return new Dictionary<string, object>
                {
                    ["severity"] = severity,
                    ["shader"]   = fallbackAssetPath,
                    ["file"]     = m.Groups["file"].Value.Trim(),
                    ["line"]     = int.TryParse(m.Groups["line"].Value, out int ln) ? ln : 0,
                    ["message"]  = m.Groups["message"].Value.Trim(),
                    ["source"]   = "variant_compile",
                };
            }

            // Couldn't extract a location — include the raw message at shader root.
            return new Dictionary<string, object>
            {
                ["severity"] = severity,
                ["shader"]   = fallbackAssetPath,
                ["file"]     = fallbackAssetPath,
                ["line"]     = 0,
                ["message"]  = logMsg.Trim(),
                ["source"]   = "variant_compile",
            };
        }

        // --- Shader Preview Methods ---

        /// <summary>
        /// Renders one or more preview frames using PreviewRenderUtility (no Play Mode required).
        /// Returns base64-encoded PNG images and an is_error_pink flag per frame.
        /// Refuses to render if the shader has compilation errors to prevent GPU compiler hangs.
        /// </summary>
        private static object PreviewShader(JObject @params)
        {
            try
            {
                // Resolve shader_names (first entry) or material_path.
                var shaderNamesToken = @params["shader_names"] as JArray;
                string shaderName    = shaderNamesToken?.Count > 0 ? shaderNamesToken[0].ToString() : null;
                string matPath       = @params["material_path"]?.ToString();
                string meshType      = @params["mesh"]?.ToString() ?? "sphere";
                int resolution = Math.Max(64, Math.Min(1024,
                    @params["resolution"]?.ToObject<int>() ?? 256));

                // Parse frames array; default to a single frame at t=0.
                var frames = new List<(float t, JObject props)>();
                if (@params["frames"] is JArray framesArr && framesArr.Count > 0)
                {
                    foreach (var f in framesArr)
                    {
                        float t = f["t"]?.ToObject<float>() ?? 0f;
                        frames.Add((t, f["props"] as JObject));
                    }
                }
                else
                {
                    frames.Add((0f, null));
                }

                // Load or create a material.
                Material material = null;
                bool tempMaterial  = false;

                if (!string.IsNullOrEmpty(matPath))
                {
                    material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                    if (material == null)
                        return Response.Error($"Material not found at: {matPath}");
                }
                else if (!string.IsNullOrEmpty(shaderName))
                {
                    Shader shader = Shader.Find(shaderName);
                    if (shader == null)
                        return Response.Error($"Shader.Find(\"{shaderName}\") returned null.");
                    material    = new Material(shader);
                    tempMaterial = true;
                }
                else
                {
                    return Response.Error("preview requires 'shader_names' or 'material_path'.");
                }

                // Safety guard (Bug #3): refuse to render shaders with compilation errors.
                // Rendering a broken shader variant submits it to the GPU compiler and can
                // hang the entire Unity process when the compiler deadlocks.
                Shader previewShader = material.shader;
                if (previewShader != null)
                {
                    ShaderMessage[] safetyMsgs = ShaderUtil.GetShaderMessages(previewShader);
                    int errCount = 0;
                    foreach (var sm in safetyMsgs)
                    {
                        if (string.Equals(sm.severity.ToString(), "Error",
                                StringComparison.OrdinalIgnoreCase))
                            errCount++;
                    }
                    if (errCount > 0)
                        return Response.Error(
                            $"Cannot preview: the shader '{previewShader.name}' has {errCount} " +
                            "compilation error(s). Run unity_shader { \"action\": \"compile\" } " +
                            "first and fix all errors before previewing.");
                    if (!previewShader.isSupported)
                        return Response.Error(
                            $"Cannot preview: the shader '{previewShader.name}' is not supported " +
                            "on this GPU (not finished compiling, or failed to compile). " +
                            "Run unity_shader { \"action\": \"compile\" } first.");
                }

                Mesh previewMesh = GetPreviewMesh(meshType);
                var  pru         = new PreviewRenderUtility();
                var  resultFrames = new List<Dictionary<string, object>>();

                try
                {
                    // Basic camera setup.
                    pru.camera.transform.position  = new Vector3(0f, 0f, -3f);
                    pru.camera.transform.LookAt(Vector3.zero);
                    pru.camera.nearClipPlane = 0.01f;
                    pru.camera.farClipPlane  = 100f;
                    pru.camera.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);
                    pru.camera.clearFlags      = CameraClearFlags.SolidColor;

                    // Key light.
                    pru.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
                    pru.lights[0].intensity          = 1.4f;
                    if (pru.lights.Length > 1)
                    {
                        pru.lights[1].transform.rotation = Quaternion.Euler(-40f, -40f, 0f);
                        pru.lights[1].intensity          = 0.5f;
                    }

                    foreach (var (t, props) in frames)
                    {
                        // Unity ignores writes to the built-in _Time / _TimeParameters
                        // (and may overwrite them every frame). Preview animation is
                        // driven only by the opt-in _PreviewTime float.
                        material.SetFloat("_PreviewTime", t);

                        // Apply per-frame custom float/vector properties.
                        if (props != null)
                        {
                            foreach (var kv in props)
                            {
                                switch (kv.Value?.Type)
                                {
                                    case JTokenType.Float:
                                    case JTokenType.Integer:
                                        material.SetFloat(kv.Key, kv.Value.ToObject<float>());
                                        break;
                                    case JTokenType.Array:
                                        var arr = (JArray)kv.Value;
                                        if (arr.Count >= 4)
                                            material.SetVector(kv.Key, new Vector4(
                                                arr[0].ToObject<float>(), arr[1].ToObject<float>(),
                                                arr[2].ToObject<float>(), arr[3].ToObject<float>()));
                                        else if (arr.Count == 3)
                                            material.SetVector(kv.Key, new Vector4(
                                                arr[0].ToObject<float>(), arr[1].ToObject<float>(),
                                                arr[2].ToObject<float>(), 0f));
                                        break;
                                }
                            }
                        }

                        pru.BeginStaticPreview(new Rect(0, 0, resolution, resolution));
                        pru.DrawMesh(previewMesh, Matrix4x4.identity, material, 0);
                        pru.camera.Render();
                        Texture2D tex = pru.EndStaticPreview();

                        // Unity error shaders render as Color.magenta (1, 0, 1).
                        // Count across the whole image — a corner sample is all background.
                        Color[] pixels = tex.GetPixels();
                        int pink = 0;
                        foreach (var px in pixels)
                        {
                            if (px.r == 1f && px.g == 0f && px.b == 1f)
                                pink++;
                        }
                        bool isErrorPink = pixels.Length > 0
                            && (pink / (float)pixels.Length) >= 0.05f;

                        byte[] pngBytes = tex.EncodeToPNG();
                        UnityEngine.Object.DestroyImmediate(tex);

                        resultFrames.Add(new Dictionary<string, object>
                        {
                            ["t"]             = t,
                            ["image_base64"]  = Convert.ToBase64String(pngBytes),
                            ["is_error_pink"] = isErrorPink,
                        });
                    }
                }
                finally
                {
                    pru.Cleanup();
                    if (tempMaterial && material != null)
                        UnityEngine.Object.DestroyImmediate(material);
                }

                return Response.Success("Shader preview rendered successfully.", new Dictionary<string, object>
                {
                    ["success"]    = true,
                    ["frames"]     = resultFrames,
                    ["resolution"] = resolution,
                    ["mesh"]       = meshType,
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Shader preview failed: {e.Message}");
            }
        }

        /// <summary>Returns a built-in Unity mesh by type name.</summary>
        private static Mesh GetPreviewMesh(string meshType)
        {
            switch (meshType?.ToLowerInvariant())
            {
                case "quad":  return Resources.GetBuiltinResource<Mesh>("Quad.fbx");
                case "cube":  return Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                default:      return Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            }
        }

        /// <summary>
        /// Finds .shader assets that contain a #include directive referencing the given
        /// hlsl/include file (matched by base filename). Searches the file's own directory
        /// and its parent first for speed; falls back to a full-project scan only when those
        /// yield nothing.
        /// </summary>
        private static List<string> FindShadersIncluding(string hlslAssetPath)
        {
            string hlslFileName  = Path.GetFileName(hlslAssetPath);
            string hlslDir       = (Path.GetDirectoryName(hlslAssetPath) ?? "Assets").Replace('\\', '/');
            string hlslParentDir = (Path.GetDirectoryName(hlslDir)       ?? "Assets").Replace('\\', '/');

            // #include pattern: matches both "...file.hlsl" and <...file.hlsl>
            var includePattern = new Regex(
                @"#include\s+[""<][^""<>]*" + Regex.Escape(hlslFileName) + @"["">""]",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            // Search scopes: narrow first, full project as last resort
            var scopes = new[] { new[] { hlslDir }, new[] { hlslParentDir }, null };

            foreach (var scope in scopes)
            {
                string[] guids = scope != null
                    ? AssetDatabase.FindAssets("t:Shader", scope)
                    : AssetDatabase.FindAssets("t:Shader");

                var found = new List<string>();
                foreach (var guid in guids)
                {
                    string shaderPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(shaderPath)) continue;

                    try
                    {
                        string fullPath = Path.Combine(
                            Path.GetDirectoryName(Application.dataPath) ?? string.Empty,
                            shaderPath.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(fullPath)) continue;

                        string content = File.ReadAllText(fullPath);
                        if (includePattern.IsMatch(content))
                            found.Add(shaderPath);
                    }
                    catch
                    {
                        // Skip files that cannot be read
                    }
                }

                if (found.Count > 0) return found;
            }

            return new List<string>();
        }

        /// <summary>
        /// Frame-driven shader compile: ImportAsset(ForceUpdate) + variant warmup, waiting
        /// across editor frames for <c>!ShaderUtil.anythingCompiling</c> so the GPU compiler
        /// can deliver completion on the main-thread pump.
        /// </summary>
        private sealed class CompileShaderJob : StepJob
        {
            public List<string> AssetPaths = new List<string>();
            public string VariantMode = "common";
            public List<List<string>> ExplicitKeywords = new List<List<string>>();
            public List<string> IncludePasses = new List<string>();
            public List<string> MaterialPaths = new List<string>();
            public bool ImportStarted;
            public bool WarmupStarted;
            public int TotalVariants;
            public bool AnyTruncated;

            [JsonIgnore] private int _importIdle;
            [JsonIgnore] private int _warmupIdle;
            [JsonIgnore] private bool _warmupFinished;
            [JsonIgnore] private bool _listening;
            [JsonIgnore] private Application.LogCallback _logHandler;
            [JsonIgnore] private readonly List<Dictionary<string, object>> _consoleLogs
                = new List<Dictionary<string, object>>();
            [JsonIgnore] private readonly List<Dictionary<string, object>> _allMessages
                = new List<Dictionary<string, object>>();
            [JsonIgnore] private readonly HashSet<string> _seenSigs = new HashSet<string>();
            [JsonIgnore] private readonly HashSet<string> _allowedShaderNames
                = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            [JsonIgnore] private readonly List<ShaderVariantCollection> _collections
                = new List<ShaderVariantCollection>();
            [JsonIgnore] private int _totalErrors;
            [JsonIgnore] private int _totalWarnings;

            protected override JobStep[] BuildSteps() => new[]
            {
                new JobStep("setup-import", SetupImport, ImportSettled),
                new JobStep("read-and-warmup", ReadAndWarmup, WarmupSettled),
                new JobStep("collect", Collect),
            };

            public override void OnRestored()
            {
                // Domain reload drops the log subscription and JsonIgnore sets.
                // Rebuild the allow-list, then re-attach while import or warmup
                // is still in flight so Console errors are not lost.
                PopulateAllowedShaderNames();
                if (ImportStarted && !_warmupFinished)
                    AttachLog();
            }

            private void SetupImport()
            {
                if (ImportStarted) return;
                ImportStarted = true;
                // Shader compile errors are emitted to the Console during ImportAsset.
                // The handler must be attached before that call, not after.
                PopulateAllowedShaderNames();
                AttachLog();

                foreach (var assetPath in AssetPaths ?? Enumerable.Empty<string>())
                {
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                }
            }

            private bool ImportSettled()
            {
                if (ShaderUtil.anythingCompiling)
                {
                    _importIdle = 0;
                    return false;
                }
                // Stay at least one extra idle frame so a compile that starts on the next
                // pump is not mistaken for "already done".
                return ++_importIdle >= 2;
            }

            private void ReadAndWarmup()
            {
                if (!WarmupStarted)
                {
                    WarmupStarted = true;
                    KickoffWarmup();
                }
                else if (_collections.Count == 0 && !_warmupFinished)
                {
                    // Collections are not serialized; rebuild them after a domain reload.
                    TotalVariants = 0;
                    AnyTruncated = false;
                    KickoffWarmup();
                }

                ContinueWarmup();
            }

            private void KickoffWarmup()
            {
                foreach (var assetPath in AssetPaths ?? Enumerable.Empty<string>())
                {
                    try
                    {
                        var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
                        if (shader == null)
                        {
                            AddMessage(new Dictionary<string, object>
                            {
                                ["severity"] = "error",
                                ["shader"]   = assetPath,
                                ["file"]     = assetPath,
                                ["line"]     = 0,
                                ["message"]  = $"Failed to load shader asset after import: '{assetPath}'",
                            });
                            continue;
                        }

                        CollectShaderMessages(shader, assetPath);

                        var variantList = BuildVariantList(
                            shader, VariantMode, MaterialPaths,
                            ExplicitKeywords, IncludePasses, out bool truncated);
                        AnyTruncated |= truncated;
                        TotalVariants += variantList.Count;

                        if (variantList.Count == 0) continue;

                        var svc = new ShaderVariantCollection();
                        foreach (var v in variantList)
                        {
                            try { svc.Add(v); } catch { /* skip invalid pass/keyword combinations */ }
                        }
                        _collections.Add(svc);
                    }
                    catch (Exception e)
                    {
                        AddMessage(new Dictionary<string, object>
                        {
                            ["severity"] = "error",
                            ["shader"]   = assetPath,
                            ["file"]     = assetPath,
                            ["line"]     = 0,
                            ["message"]  = $"Exception during shader processing: {e.Message}",
                        });
                    }
                }
            }

            private void ContinueWarmup()
            {
                if (_warmupFinished) return;
                if (_collections.Count == 0)
                {
                    _warmupFinished = true;
                    DetachLog();
                    return;
                }

                bool allDone = true;
                foreach (var svc in _collections)
                {
                    try
                    {
                        if (!TryWarmUpProgressively(svc, 8))
                            allDone = false;
                    }
                    catch (Exception e)
                    {
                        string fallback = AssetPaths != null && AssetPaths.Count > 0
                            ? AssetPaths[0] : string.Empty;
                        AddMessage(new Dictionary<string, object>
                        {
                            ["severity"] = "error",
                            ["shader"]   = fallback,
                            ["file"]     = fallback,
                            ["line"]     = 0,
                            ["message"]  = $"Exception during variant warmup: {e.Message}",
                        });
                    }
                }
                _warmupFinished = allDone;
                if (_warmupFinished)
                    DetachLog();
            }

            private bool WarmupSettled()
            {
                if (!_warmupFinished || ShaderUtil.anythingCompiling)
                {
                    if (ShaderUtil.anythingCompiling) _warmupIdle = 0;
                    return false;
                }
                return ++_warmupIdle >= 2;
            }

            private void Collect()
            {
                try
                {
                    foreach (var assetPath in AssetPaths ?? Enumerable.Empty<string>())
                    {
                        try
                        {
                            var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
                            if (shader == null) continue;
                            CollectShaderMessages(shader, assetPath);
                        }
                        catch (Exception e)
                        {
                            AddMessage(new Dictionary<string, object>
                            {
                                ["severity"] = "error",
                                ["shader"]   = assetPath,
                                ["file"]     = assetPath,
                                ["line"]     = 0,
                                ["message"]  = $"Exception collecting shader messages: {e.Message}",
                            });
                        }
                    }

                    foreach (var entry in _consoleLogs)
                        AddMessage(entry);

                    string resultMsg = _totalErrors > 0
                        ? $"Shader compilation completed with {_totalErrors} error(s) and {_totalWarnings} warning(s)."
                        : _totalWarnings > 0
                            ? $"Shader compilation completed with {_totalWarnings} warning(s)."
                            : "Shader compilation completed successfully.";

                    // Top-level success means the compile job finished, not that
                    // the shader is error-free. Shader errors live in has_errors /
                    // error_count / messages so the CLI does not swallow the payload.
                    Complete(Response.Success(resultMsg, new Dictionary<string, object>
                    {
                        ["compiled"]           = true,
                        ["shaders_compiled"]   = AssetPaths?.Count ?? 0,
                        ["error_count"]        = _totalErrors,
                        ["warning_count"]      = _totalWarnings,
                        ["variants_compiled"]  = TotalVariants,
                        ["variants_truncated"] = AnyTruncated,
                        ["has_errors"]         = _totalErrors > 0,
                        ["messages"]           = _allMessages,
                    }));
                }
                catch (Exception e)
                {
                    Fail($"Shader compilation failed: {e.Message}");
                }
                finally
                {
                    DetachLog();
                    CleanupCollections();
                }
            }

            private void CollectShaderMessages(Shader shader, string assetPath)
            {
                ShaderMessage[] msgs = ShaderUtil.GetShaderMessages(shader);
                if (msgs == null || msgs.Length == 0) return;

                // ShaderUtil.GetShaderMessages may return stale entries from other
                // shaders. Keep only messages whose file belongs to this shader
                // (the shader itself + its hlsl/cginc/glsl deps).
                var ownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { assetPath };
                try
                {
                    foreach (var dep in AssetDatabase.GetDependencies(assetPath, recursive: false))
                    {
                        string ext = Path.GetExtension(dep).ToLowerInvariant();
                        if (ext == ".hlsl" || ext == ".cginc" || ext == ".glsl" || ext == ".shader")
                            ownFiles.Add(dep);
                    }
                }
                catch { /* dependency lookup is best-effort */ }

                foreach (var msg in msgs)
                {
                    string msgFile = (msg.file ?? string.Empty).Replace('\\', '/');
                    if (!string.IsNullOrEmpty(msgFile) && !ownFiles.Contains(msgFile))
                        continue;

                    // GetShaderMessages can return stale entries from other shaders
                    // (often with an empty file). Drop those by the name in the text.
                    var nameMatch = ShaderNameFromLogRegex.Match(msg.message ?? string.Empty);
                    if (nameMatch.Success
                        && !string.IsNullOrEmpty(shader.name)
                        && !string.Equals(
                            nameMatch.Groups[1].Value, shader.name, StringComparison.Ordinal))
                        continue;

                    bool isError = string.Equals(
                        msg.severity.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                    var entry = new Dictionary<string, object>
                    {
                        ["severity"] = isError ? "error" : "warning",
                        ["shader"]   = assetPath,
                        ["file"]     = string.IsNullOrEmpty(msg.file) ? assetPath : msg.file,
                        ["line"]     = msg.line,
                        ["message"]  = msg.message ?? string.Empty,
                    };
                    TryAddExtendedMessageFields(msg, entry);
                    AddMessage(entry);
                }
            }

            private void AddMessage(Dictionary<string, object> entry)
            {
                if (entry == null) return;
                if (!_seenSigs.Add(MakeMessageSig(entry))) return;
                _allMessages.Add(entry);
                bool isError = string.Equals(
                    entry["severity"]?.ToString(), "error", StringComparison.OrdinalIgnoreCase);
                if (isError) _totalErrors++;
                else         _totalWarnings++;
            }

            private void PopulateAllowedShaderNames()
            {
                _allowedShaderNames.Clear();
                foreach (var assetPath in AssetPaths ?? Enumerable.Empty<string>())
                {
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
                    if (shader != null && !string.IsNullOrEmpty(shader.name))
                        _allowedShaderNames.Add(shader.name);
                }
            }

            private bool IsAllowedShaderLog(string logMsg)
            {
                var m = ShaderNameFromLogRegex.Match(logMsg ?? string.Empty);
                if (!m.Success) return true;
                if (_allowedShaderNames.Count == 0) return true;
                return _allowedShaderNames.Contains(m.Groups[1].Value);
            }

            private void AttachLog()
            {
                if (_listening) return;
                _listening = true;
                _logHandler = (logMsg, stackTrace, logType) =>
                {
                    if (!_listening) return;
                    if (logType != LogType.Error && logType != LogType.Warning) return;
                    if (!IsAllowedShaderLog(logMsg)) return;
                    string fallback = AssetPaths != null && AssetPaths.Count > 0
                        ? AssetPaths[0] : string.Empty;
                    var parsed = TryParseShaderLogMessage(
                        logMsg, fallback, logType == LogType.Error);
                    if (parsed != null) _consoleLogs.Add(parsed);
                };
                Application.logMessageReceived += _logHandler;
            }

            private void DetachLog()
            {
                if (!_listening) return;
                _listening = false;
                if (_logHandler != null)
                    Application.logMessageReceived -= _logHandler;
                _logHandler = null;
            }

            private void CleanupCollections()
            {
                foreach (var svc in _collections)
                {
                    if (svc != null)
                        UnityEngine.Object.DestroyImmediate(svc);
                }
                _collections.Clear();
            }

            /// <summary>
            /// Warms a small batch of variants and returns true when the collection is fully
            /// warmed. Falls back to blocking WarmUp only when WarmUpProgressively is absent.
            /// </summary>
            private static bool TryWarmUpProgressively(ShaderVariantCollection svc, int count)
            {
                var method = typeof(ShaderVariantCollection).GetMethod(
                    "WarmUpProgressively",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(int) },
                    null);
                if (method != null)
                    return (bool)method.Invoke(svc, new object[] { count });

                svc.WarmUp();
                return true;
            }
        }
    }
} 
