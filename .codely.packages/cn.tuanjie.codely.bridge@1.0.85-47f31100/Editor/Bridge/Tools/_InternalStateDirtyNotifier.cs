using System;
using System.Collections.Generic;
using Codely.Newtonsoft.Json.Linq;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// INTERNAL TOOL - NOT EXPOSED TO LLM.
    /// Receives notifications from external agentic tools when they modify files
    /// that may affect Unity's state.
    /// 
    /// This tool should only be called by the agent execution layer,
    /// not directly by LLM tool invocations.
    /// </summary>
    public static class _InternalStateDirtyNotifier
    {
        /// <summary>
        /// Handle dirty state notification from external tools.
        /// </summary>
        private static readonly Dictionary<string, Func<JObject, object>> ActionHandlers =
            new Dictionary<string, Func<JObject, object>>
            {
                { "notify_file_changed", NotifyFileChanged },
                { "notify_batch_changes", NotifyBatchChanges },
                { "get_statistics", _ => GetStatistics() },
            };

        public static object HandleCommand(JObject @params)
            => ActionRouter.Route(@params, ActionHandlers);

        /// <summary>
        /// Notify Unity that a single file was changed by an external tool.
        /// </summary>
        private static object NotifyFileChanged(JObject @params)
        {
            try
            {
                string filePath = @params["file_path"]?.ToString();
                string toolName = @params["tool_name"]?.ToString() ?? "unknown";

                if (string.IsNullOrEmpty(filePath))
                    return Response.Error("[INTERNAL] 'file_path' parameter required");

                UnityStateDirtyHook.NotifyFileChanged(filePath, toolName);

                return Response.Success(
                    $"[INTERNAL] File change notification recorded: {filePath}",
                    new { filePath = filePath, toolName = toolName }
                );
            }
            catch (Exception e)
            {
                return Response.Error($"[INTERNAL] Failed to notify file change: {e.Message}");
            }
        }

        /// <summary>
        /// Notify Unity of multiple file changes in a batch (more efficient).
        /// </summary>
        private static object NotifyBatchChanges(JObject @params)
        {
            try
            {
                var filesArray = @params["files"] as JArray;
                string toolName = @params["tool_name"]?.ToString() ?? "unknown";

                if (filesArray == null || filesArray.Count == 0)
                    return Response.Error("[INTERNAL] 'files' array parameter required");

                int notified = 0;
                foreach (var fileToken in filesArray)
                {
                    string filePath = fileToken.ToString();
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        UnityStateDirtyHook.NotifyFileChanged(filePath, toolName);
                        notified++;
                    }
                }

                return Response.Success(
                    $"[INTERNAL] Batch notification recorded: {notified} files",
                    new { count = notified, toolName = toolName }
                );
            }
            catch (Exception e)
            {
                return Response.Error($"[INTERNAL] Failed to notify batch changes: {e.Message}");
            }
        }

        /// <summary>
        /// Get statistics about dirty notifications (for debugging).
        /// </summary>
        private static object GetStatistics()
        {
            try
            {
                var stats = UnityStateDirtyHook.GetStatistics();
                return Response.Success("[INTERNAL] Dirty hook statistics", stats);
            }
            catch (Exception e)
            {
                return Response.Error($"[INTERNAL] Failed to get statistics: {e.Message}");
            }
        }
    }
}

