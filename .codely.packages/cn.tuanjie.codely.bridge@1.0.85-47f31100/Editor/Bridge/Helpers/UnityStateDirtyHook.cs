using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Hook mechanism for external tools to notify Unity when they've modified files
    /// that may affect Unity's state (scripts, assets, scenes, etc.).
    /// This is NOT exposed as a tool to LLM - it's an internal notification system.
    /// </summary>
    public static class UnityStateDirtyHook
    {
        /// <summary>
        /// File change types that can affect Unity state.
        /// </summary>
        public enum FileChangeType
        {
            ScriptModified,      // .cs files modified
            AssetModified,       // Asset files (.prefab, .mat, .asset, etc.) modified
            SceneModified,       // .unity scene files modified
            ShaderModified,      // .shader files modified
            ConfigModified,      // Project settings, package.json, etc. modified
            UIModified,          // .uxml, .uss files modified
            Unknown              // Other file types
        }

        private static readonly Queue<DirtyNotification> _pendingNotifications = new Queue<DirtyNotification>();
        private static readonly object _notificationLock = new object();
        private static bool _refreshScheduled = false;

        /// <summary>
        /// Notification record for dirty file changes.
        /// </summary>
        public class DirtyNotification
        {
            public DateTime Timestamp { get; set; }
            public FileChangeType ChangeType { get; set; }
            public string FilePath { get; set; }
            public string ToolName { get; set; }
            public bool RequiresReimport { get; set; }
            public bool RequiresCompilation { get; set; }
        }

        /// <summary>
        /// Called by external agentic tools (edit, write, etc.) to notify Unity of file changes.
        /// This is the main entry point for the hook system.
        /// </summary>
        /// <param name="filePath">Path to the file that was modified</param>
        /// <param name="toolName">Name of the tool that made the change (for logging)</param>
        public static void NotifyFileChanged(string filePath, string toolName = "unknown")
        {
            if (string.IsNullOrEmpty(filePath))
                return;

            try
            {
                // Normalize path
                filePath = filePath.Replace('\\', '/');

                // Determine change type and required actions
                var changeType = DetermineChangeType(filePath);
                bool requiresReimport = ShouldReimport(filePath, changeType);
                bool requiresCompilation = RequiresCompilation(filePath, changeType);

                var notification = new DirtyNotification
                {
                    Timestamp = DateTime.UtcNow,
                    ChangeType = changeType,
                    FilePath = filePath,
                    ToolName = toolName,
                    RequiresReimport = requiresReimport,
                    RequiresCompilation = requiresCompilation
                };

                lock (_notificationLock)
                {
                    _pendingNotifications.Enqueue(notification);
                    
                    // Schedule refresh on next editor update
                    if (!_refreshScheduled)
                    {
                        _refreshScheduled = true;
                        EditorApplication.delayCall += ProcessPendingNotifications;
                    }
                }

                CodelyLogger.Log($"[UnityStateDirtyHook] Notified: {changeType} - {filePath} (from {toolName})");
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[UnityStateDirtyHook] Failed to process notification for {filePath}: {e.Message}");
            }
        }

        /// <summary>
        /// Process all pending dirty notifications and trigger appropriate Unity actions.
        /// </summary>
        private static void ProcessPendingNotifications()
        {
            List<DirtyNotification> toProcess;
            
            lock (_notificationLock)
            {
                if (_pendingNotifications.Count == 0)
                {
                    _refreshScheduled = false;
                    return;
                }
                
                toProcess = new List<DirtyNotification>(_pendingNotifications);
                _pendingNotifications.Clear();
                _refreshScheduled = false;
            }

            // Group by action type
            var needsReimport = toProcess.Where(n => n.RequiresReimport).Select(n => n.FilePath).Distinct().ToList();
            var needsCompilation = toProcess.Any(n => n.RequiresCompilation);

            // Process reimports
            if (needsReimport.Count > 0)
            {
                CodelyLogger.Log($"[UnityStateDirtyHook] Reimporting {needsReimport.Count} assets...");
                foreach (var path in needsReimport)
                {
                    // Convert to Unity-relative path if needed
                    string unityPath = ConvertToUnityPath(path);
                    if (!string.IsNullOrEmpty(unityPath))
                    {
                        AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceUpdate);
                    }
                }
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }

            // Log summary
            var summary = new
            {
                processed = toProcess.Count,
                reimported = needsReimport.Count,
                needsCompilation = needsCompilation,
                byType = toProcess.GroupBy(n => n.ChangeType).ToDictionary(g => g.Key.ToString(), g => g.Count())
            };

            CodelyLogger.Log($"[UnityStateDirtyHook] Processed notifications: {Codely.Newtonsoft.Json.JsonConvert.SerializeObject(summary)}");

            // If compilation is needed, it will happen automatically via Unity's asset pipeline
            if (needsCompilation)
            {
                CodelyLogger.Log("[UnityStateDirtyHook] Script changes detected - Unity will trigger compilation automatically");
            }
        }

        /// <summary>
        /// Determine the type of change based on file extension.
        /// </summary>
        private static FileChangeType DetermineChangeType(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            
            switch (ext)
            {
                case ".cs":
                    return FileChangeType.ScriptModified;
                case ".unity":
                case ".scene":
                    return FileChangeType.SceneModified;
                case ".shader":
                case ".shadergraph":
                case ".shadersubgraph":
                    return FileChangeType.ShaderModified;
                case ".prefab":
                case ".mat":
                case ".asset":
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".psd":
                case ".fbx":
                case ".obj":
                case ".mp3":
                case ".wav":
                case ".anim":
                case ".controller":
                    return FileChangeType.AssetModified;
                case ".uxml":
                case ".uss":
                    return FileChangeType.UIModified;
                case ".json":
                case ".asmdef":
                case ".asmref":
                    return FileChangeType.ConfigModified;
                default:
                    return FileChangeType.Unknown;
            }
        }

        /// <summary>
        /// Determine if a file change requires reimporting in Unity.
        /// </summary>
        private static bool ShouldReimport(string filePath, FileChangeType changeType)
        {
            // Check if file is in Assets/ folder
            if (!IsInAssetsFolder(filePath))
                return false;

            switch (changeType)
            {
                case FileChangeType.ScriptModified:
                case FileChangeType.ShaderModified:
                case FileChangeType.AssetModified:
                case FileChangeType.SceneModified:
                case FileChangeType.UIModified:
                    return true;
                case FileChangeType.ConfigModified:
                    return filePath.Contains("package.json") || filePath.Contains(".asmdef");
                default:
                    return false;
            }
        }

        /// <summary>
        /// Determine if a file change requires script compilation.
        /// </summary>
        private static bool RequiresCompilation(string filePath, FileChangeType changeType)
        {
            return changeType == FileChangeType.ScriptModified && IsInAssetsFolder(filePath);
        }

        /// <summary>
        /// Check if a file path is within the Unity Assets folder.
        /// </summary>
        private static bool IsInAssetsFolder(string filePath)
        {
            string normalizedPath = filePath.Replace('\\', '/');
            return normalizedPath.Contains("/Assets/") || normalizedPath.StartsWith("Assets/");
        }

        /// <summary>
        /// Convert an absolute or relative path to Unity-relative path (Assets/...).
        /// </summary>
        private static string ConvertToUnityPath(string filePath)
        {
            string normalized = filePath.Replace('\\', '/');
            
            // Already Unity-relative
            if (normalized.StartsWith("Assets/"))
                return normalized;

            // Extract Assets/... portion
            int assetsIndex = normalized.IndexOf("/Assets/");
            if (assetsIndex >= 0)
                return normalized.Substring(assetsIndex + 1); // Skip the leading /

            // Check if it's relative to project root
            string projectRoot = Application.dataPath.Replace("/Assets", "").Replace('\\', '/');
            if (normalized.StartsWith(projectRoot))
            {
                string relativePath = normalized.Substring(projectRoot.Length).TrimStart('/');
                if (relativePath.StartsWith("Assets/"))
                    return relativePath;
            }

            return null;
        }

        /// <summary>
        /// Get statistics about recent dirty notifications (for debugging).
        /// </summary>
        public static object GetStatistics()
        {
            lock (_notificationLock)
            {
                return new
                {
                    pending = _pendingNotifications.Count,
                    refreshScheduled = _refreshScheduled
                };
            }
        }

        /// <summary>
        /// Clear all pending notifications (for testing/debugging).
        /// </summary>
        public static void ClearPendingNotifications()
        {
            lock (_notificationLock)
            {
                _pendingNotifications.Clear();
                _refreshScheduled = false;
            }
            CodelyLogger.Log("[UnityStateDirtyHook] Cleared all pending notifications");
        }
    }
}

