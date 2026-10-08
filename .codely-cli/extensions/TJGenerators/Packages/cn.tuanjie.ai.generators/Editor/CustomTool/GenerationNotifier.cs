using Codely.Newtonsoft.Json.Linq;

#if UNITY_EDITOR
using Codely.Newtonsoft.Json;
using TJGenerators.Utils;
using UnityEngine;
using UnityTcp.Editor;
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Sends generation-result push notifications to Codely CLI.
    /// Uses two complementary channels:
    ///   1. UnityTcpBridge.NotifyAll — TCP push when bridge ≥ 1.0.40 (CODELY_BRIDGE_HAS_NOTIFY_ALL)
    ///   2. File append — reliable fallback read by the CLI file watcher
    /// </summary>
    internal static class GenerationNotifier
    {
        public const string EventTypeResult = "generation_result";

        public static void NotifyCompleted(
            string toolName,
            string taskId,
            string backendTaskId,
            JObject extraData = null)
        {
#if UNITY_EDITOR
            if (!UnityTcpBridge.IsRunning) return;

            var payload = new JObject
            {
                ["tool_name"]       = toolName,
                ["task_id"]         = taskId,
                ["backend_task_id"] = backendTaskId ?? "",
                ["status"]          = "completed"
            };
            if (extraData != null)
                foreach (var p in extraData.Properties())
                    payload[p.Name] = p.Value;

#if CODELY_BRIDGE_HAS_NOTIFY_ALL
            UnityTcpBridge.NotifyAll(EventTypeResult, payload);
#endif
            WriteNotificationToFile(payload);
            TJLog.Log($"[GenerationNotifier] Notified completed: tool={toolName} task={taskId}");
#endif
        }

        public static void NotifyFailed(
            string toolName,
            string taskId,
            string backendTaskId,
            string errorMessage,
            JObject extraData = null)
        {
#if UNITY_EDITOR
            if (!UnityTcpBridge.IsRunning) return;

            var payload = new JObject
            {
                ["tool_name"]       = toolName,
                ["task_id"]         = taskId,
                ["backend_task_id"] = backendTaskId ?? "",
                ["status"]          = "failed",
                ["error"]           = errorMessage ?? "Unknown error"
            };
            if (extraData != null)
                foreach (var p in extraData.Properties())
                    payload[p.Name] = p.Value;

#if CODELY_BRIDGE_HAS_NOTIFY_ALL
            UnityTcpBridge.NotifyAll(EventTypeResult, payload);
#endif
            WriteNotificationToFile(payload);
            TJLog.Log($"[GenerationNotifier] Notified failed: tool={toolName} task={taskId} error={errorMessage}");
#endif
        }

        // Appends one JSON line to Library/AI.TJGenerators/async_generation_notifications.jsonl.
        // The CLI tails this file as a fallback when the TCP push doesn't reach the client.
        private static void WriteNotificationToFile(JObject payload)
        {
#if UNITY_EDITOR
            try
            {
                string libDir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, "..", "Library", "AI.TJGenerators"));
                System.IO.Directory.CreateDirectory(libDir);
                string filePath = System.IO.Path.Combine(libDir, "async_generation_notifications.jsonl");
                var entry = new JObject
                {
                    ["notification_type"] = EventTypeResult,
                    ["payload"]           = payload
                };
                System.IO.File.AppendAllText(filePath, JsonConvert.SerializeObject(entry) + "\n", System.Text.Encoding.UTF8);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[GenerationNotifier] WriteNotificationToFile failed: " + ex.Message);
            }
#endif
        }
    }
}
