using System;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Notifier
{
    /// <summary>
    /// Broadcasts the current Editor selection to notifier clients whenever it
    /// changes. The emitted payload mirrors the structure returned by
    /// <see cref="UnityTcp.Editor.Tools.ManageEditor"/>'s get_selection action.
    /// </summary>
    [InitializeOnLoad]
    public static class SelectionChangedNotifier
    {
        private const string EventType = "selection_changed";

        private static bool _flushScheduled;

        static SelectionChangedNotifier()
        {
            if (EditorAutomationGuard.ShouldSkipBridgeAutoStart()) return;

            Selection.selectionChanged += OnSelectionChanged;
        }

        private static void OnSelectionChanged() => ScheduleFlush();

        private static void ScheduleFlush()
        {
            if (_flushScheduled) return;
            _flushScheduled = true;
            EditorApplication.delayCall += Flush;
        }

        private static void Flush()
        {
            _flushScheduled = false;
            try
            {
                if (!UnityTcpBridge.IsRunning) return;

                JObject payload = BuildSelectionPayload();
                if (payload == null) return;

                UnityTcpBridge.NotifyAll(EventType, payload);
            }
            catch (Exception e)
            {
                try { CodelyLogger.LogError($"[SelectionChangedNotifier] flush failed: {e.Message}"); } catch { }
            }
        }

        private static JObject BuildSelectionPayload() => SelectionPayloadBuilder.Build();

    }
}
