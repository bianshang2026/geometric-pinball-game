#if UNITY_EDITOR
using System;
using System.IO;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using TJGenerators.Utils;

namespace TJGenerators.Config
{
    /// <summary>
    /// 加载 2D 精灵表序列帧指令模板（SpriteSheetSequenceProfiles.json）。
    /// 同时支持 AssetDatabase、包磁盘路径与 FindAssets 兜底，避免仅依赖固定 Packages 虚拟路径或 TextAsset 类型。
    /// </summary>
    public static class SpriteSheetSequenceProfileConfigLoader
    {
        private static readonly string[] VirtualCandidates =
        {
            "Packages/cn.tuanjie.ai.generators/Editor/Config/SpriteSheetSequenceProfiles.json",
            "Assets/codelyGenerator/Editor/Config/SpriteSheetSequenceProfiles.json",
            "Assets/TJGenerators/Config/SpriteSheetSequenceProfiles.json",
        };

        /// <summary>
        /// 尝试加载配置文件并解析为 JObject。
        /// </summary>
        /// <param name="root">解析后的 JSON 根对象</param>
        /// <param name="resolvedPath">用于日志/调试的路径描述（虚拟路径或磁盘绝对路径）</param>
        public static bool TryLoad(out JObject root, out string resolvedPath)
        {
            root = null;
            resolvedPath = null;

            foreach (var candidate in VirtualCandidates)
            {
                var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(candidate);
                if (textAsset == null || string.IsNullOrEmpty(textAsset.text))
                    continue;
                if (!TryParse(textAsset.text, candidate, out root, out var err))
                {
                    TJLog.LogWarning($"[SpriteSheetSequenceProfileConfigLoader] {err}");
                    continue;
                }

                resolvedPath = candidate;
                return true;
            }

            string pkgRoot = PathUtils.TryGetTjGeneratorsPackageRoot();
            if (!string.IsNullOrEmpty(pkgRoot))
            {
                string diskPath = Path.Combine(pkgRoot, "Editor", "Config", "SpriteSheetSequenceProfiles.json");
                if (File.Exists(diskPath))
                {
                    string text;
                    try
                    {
                        text = File.ReadAllText(diskPath);
                    }
                    catch (Exception e)
                    {
                        TJLog.LogWarning($"[SpriteSheetSequenceProfileConfigLoader] Read failed '{diskPath}': {e.Message}");
                        text = null;
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        if (TryParse(text, diskPath, out root, out string parseErr))
                        {
                            resolvedPath = diskPath;
                            return true;
                        }

                        if (!string.IsNullOrEmpty(parseErr))
                            TJLog.LogWarning($"[SpriteSheetSequenceProfileConfigLoader] {parseErr}");
                    }
                }
            }

            var guids = AssetDatabase.FindAssets("SpriteSheetSequenceProfiles");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                string jsonText = null;

                var ta = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (ta != null && !string.IsNullOrEmpty(ta.text))
                    jsonText = ta.text;
                else
                {
                    try
                    {
                        string abs = PathUtils.ToAbsoluteAssetPath(path);
                        if (File.Exists(abs))
                            jsonText = File.ReadAllText(abs);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                if (string.IsNullOrEmpty(jsonText))
                    continue;

                if (!TryParse(jsonText, path, out root, out var err))
                {
                    TJLog.LogWarning($"[SpriteSheetSequenceProfileConfigLoader] {err}");
                    continue;
                }

                resolvedPath = path;
                return true;
            }

            return false;
        }

        private static bool TryParse(string json, string pathForLog, out JObject root, out string error)
        {
            root = null;
            error = null;
            try
            {
                root = JObject.Parse(json);
                return true;
            }
            catch (Exception e)
            {
                error = $"Parse failed for '{pathForLog}': {e.Message}";
                return false;
            }
        }
    }
}
#endif
