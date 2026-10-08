using System;
using System.Collections.Generic;
using System.Linq;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityTcp.Editor.Helpers;
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
using Cn.Tuanjie.Codely.Editor;
#endif

namespace UnityTcp.Editor.Notifier
{
    /// <summary>
    /// Pushes messages from the Editor/engine into the input field of the
    /// embedded AI agent (CodelyWindow) as an "input_from_editor" event:
    /// user-typed messages, engine errors, stack traces, or any other info the
    /// agent should react to. Distinguish the kind of message via the
    /// <c>source</c>/<c>type</c> arguments.
    ///
    /// If no CodelyWindow is open, one is opened and the message is queued
    /// instead of broadcast; the frontend drains the queue via manage_editor
    /// action "drain_agent_input" once its page is fully loaded, so cold-start
    /// messages are not lost. With a window already open, the message is
    /// broadcast directly.
    ///
    /// Payload shape: <c>{ text, source, type, autoSubmit, timestamp, attachments? }</c>.
    /// </summary>
    public static class AgentInputNotifier
    {
        private const string EventType = "input_from_editor";
        private const int MaxPendingMessages = 100;

        private static readonly object _gate = new object();
        private static readonly List<JObject> _pending = new List<JObject>();

        /// <summary>
        /// Sends <paramref name="text"/> to the embedded agent's input field.
        /// Must be called from the main thread (may open an EditorWindow).
        /// </summary>
        /// <param name="text">Message to place into the agent input field.</param>
        /// <param name="source">Origin of the message; defaults to "editor_window".</param>
        /// <param name="type">Kind of message; defaults to "user_message".</param>
        /// <param name="autoSubmit">True to have the frontend submit the message immediately instead of just appending it.</param>
        /// <param name="timestamp">Time the message was created; defaults to now (UTC).</param>
        /// <param name="attachments">Optional file paths the frontend attaches (@) to the message; may be null.</param>
        /// <returns>
        /// True if broadcast to at least one client; false if invalid or queued
        /// for the frontend to drain (no CodelyWindow was open yet).
        /// </returns>
        public static bool Send(
            string text,
            string source = "editor_window",
            string type = "user_message",
            bool autoSubmit = false,
            DateTime? timestamp = null,
            IReadOnlyList<string> attachments = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            try
            {
                var payload = new JObject
                {
                    ["text"] = text,
                    ["source"] = source,
                    ["type"] = type,
                    ["autoSubmit"] = autoSubmit,
                    ["timestamp"] = (timestamp ?? DateTime.UtcNow).ToString("o"),
                };

                var attachmentPaths = attachments
                    ?.Select(p => p?.Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();
                if (attachmentPaths != null && attachmentPaths.Count > 0)
                    payload["attachments"] = JArray.FromObject(attachmentPaths);

#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
                // The CodelyWindow's presence is the one reliable signal here:
                // connection state can lie (e.g. a standalone app client makes
                // NotifyAll report delivered while the embedded page is still
                // loading). No window → open one, queue the message, and let
                // the frontend drain the queue once its page is fully loaded.
                if (!EditorWindow.HasOpenInstances<CodelyWindow>())
                {
                    CodelyWindow.ShowWindow();
                    QueuePending(payload);
                    return false;
                }
#endif

                return UnityTcpBridge.IsRunning
                    && UnityTcpBridge.NotifyAll(EventType, payload);
            }
            catch (Exception e)
            {
                try { CodelyLogger.LogError($"[AgentInputNotifier] send failed: {e.Message}"); } catch { }
                return false;
            }
        }

        private static void QueuePending(JObject payload)
        {
            lock (_gate)
            {
                if (_pending.Count >= MaxPendingMessages)
                    _pending.RemoveAt(0); // drop the oldest
                _pending.Add(payload);
            }
        }

        /// <summary>
        /// Returns all queued (undelivered) messages and clears the queue.
        /// Called by manage_editor's "drain_agent_input" action on behalf of
        /// the frontend once its page is fully loaded.
        /// </summary>
        public static List<JObject> DrainPending()
        {
            lock (_gate)
            {
                var items = new List<JObject>(_pending);
                _pending.Clear();
                return items;
            }
        }
    }
}
