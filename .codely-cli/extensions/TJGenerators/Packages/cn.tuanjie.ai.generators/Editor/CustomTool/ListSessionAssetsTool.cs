using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Codely.Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEditor;

#if UNITY_EDITOR
using TJGenerators;
using TJGenerators.Utils;
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Lists all assets generated within a specific agent session.
    /// Queries local History (EditorPrefs) by sessionId — returns both completed
    /// and in-progress items, with local file paths where available.
    /// </summary>
    public static class ListSessionAssetsTool
    {
        [ExecuteCustomTool.CustomTool("list_session_assets",
            "List all assets (3D models, images, audio, video, sprites, materials, etc.) generated within " +
            "a specific agent session. Returns local file paths for completed assets and status for " +
            "in-progress assets. " +
            "Parameters: session_id (string, required — the session ID used when calling generate_* tools). " +
            "Only assets generated with a matching session_id will be returned. " +
            "Assets generated before session_id support was added or on a different machine will not appear.")]
        public static object ListSessionAssets(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                string sessionId = parameters["session_id"]?.ToString();
                if (string.IsNullOrEmpty(sessionId))
                    return Fail("'session_id' parameter is required");

                var history = TJGeneratorsHistoryManager.LoadHistory();
                var sessionItems = history
                    .Where(h => !string.IsNullOrEmpty(h.sessionId) && h.sessionId == sessionId)
                    .OrderByDescending(h => h.timestamp)
                    .ToList();

                var assets = new List<Dictionary<string, object>>();

                foreach (var item in sessionItems)
                {
                    var entry = new Dictionary<string, object>
                    {
                        { "task_id",        item.taskId ?? "" },
                        { "status",         item.isGenerating ? "generating" : "completed" },
                        { "progress",       item.progress },
                        { "prompt",         item.GetUserFacingPrompt() ?? "" },
                        { "model_version",  item.modelVersion ?? "" },
                        { "is_text_to_model", item.isTextToModel },
                        { "created_at",     item.GetTimeString() }
                    };

                    if (!string.IsNullOrEmpty(item.modelPath))
                    {
                        entry["model_path"] = item.modelPath;

                        // Check if file actually exists on disk
                        bool fileExists = false;
                        try
                        {
                            if (item.modelPath.StartsWith("Assets/") || item.modelPath.StartsWith("Assets\\"))
                                fileExists = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.modelPath) != null;
                            else
                                fileExists = File.Exists(item.modelPath);
                        }
                        catch { }
                        entry["file_exists"] = fileExists;
                    }

                    if (!string.IsNullOrEmpty(item.previewImageUrl))
                        entry["preview_url"] = item.previewImageUrl;

                    if (!string.IsNullOrEmpty(item.imagePath))
                        entry["image_path"] = item.imagePath;

                    if (!string.IsNullOrEmpty(item.assetGuid))
                    {
                        entry["asset_guid"] = item.assetGuid;
                        string assetPath = AssetDatabase.GUIDToAssetPath(item.assetGuid);
                        if (!string.IsNullOrEmpty(assetPath))
                            entry["asset_path"] = assetPath;
                    }

                    if (!string.IsNullOrEmpty(item.promptTemplateId))
                        entry["prompt_template_id"] = item.promptTemplateId;

                    assets.Add(entry);
                }

                return new Dictionary<string, object>
                {
                    { "success",     true },
                    { "session_id",  sessionId },
                    { "total",       assets.Count },
                    { "completed",   assets.Count(a => (string)a["status"] == "completed") },
                    { "in_progress", assets.Count(a => (string)a["status"] == "generating") },
                    { "assets",      assets }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[ListSessionAssetsTool] Error: {e}");
                return Fail($"Error listing session assets: {e.Message}");
            }
#else
            return Fail("This tool only works in Unity Editor.");
#endif
        }

        private static Dictionary<string, object> Fail(string message)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "message", message }
            };
        }
    }
}
