#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using Unity.InternalBridge;
using UnityEditor;
using UnityTcp.Editor.Helpers;
using UnityEngine;

namespace Cn.Tuanjie.Codely.Editor
{
    /// <summary>
    /// Analytics event tracker. Sends events from Unity to the Tauri backend via
    /// HTTP invoke (<c>unity/trackEvent</c>), which forwards them to the remote server.
    /// </summary>
    public static class CodelyEventTracker
    {
        public const string InvokeTrackEvent = "unity/trackEvent";

        public const string WelcomeWindowShown   = "welcome_window_shown";
        public const string WelcomeWindowClosed  = "welcome_window_closed";
        public const string WelcomeButtonClick   = "welcome_button_clicked";
        public const string CodelyPanelOpened    = "codely_panel_opened";
        public const string CodelyPanelClosed    = "codely_panel_closed";

        private static readonly HashSet<string> s_eventsRequiringUserId =
            new HashSet<string>
            {
                WelcomeWindowShown,
                WelcomeWindowClosed,
                WelcomeButtonClick,
                CodelyPanelOpened,
                CodelyPanelClosed,
            };

        private static readonly HttpClient s_httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        private static readonly Queue<string> s_pendingTrackPayloads =
            new Queue<string>();
        private const int MaxPendingTrackPayloads = 50;

        public static bool HasPending => s_pendingTrackPayloads.Count > 0;

        private static bool? s_isTuanjie;
        private static bool s_suppressNextPanelClose;

        /// <summary>
        /// Skip the next <c>codely_panel_closed</c> event. Use when closing only to
        /// immediately reopen (e.g. setup UI → main panel, force reload).
        /// </summary>
        public static void SuppressNextPanelClose()
        {
            s_suppressNextPanelClose = true;
        }

        /// <returns>True when the close event should be skipped.</returns>
        public static bool ConsumeSuppressNextPanelClose()
        {
            if (!s_suppressNextPanelClose)
            {
                return false;
            }

            s_suppressNextPanelClose = false;
            return true;
        }

        /// <summary>
        /// Flush track events queued before the Tauri HTTP port was available.
        /// </summary>
        public static void FlushPending(int tauriServerPort = -1)
        {
            if (!ShouldTrack())
            {
                s_pendingTrackPayloads.Clear();
                return;
            }

            while (s_pendingTrackPayloads.Count > 0)
            {
                string payload = s_pendingTrackPayloads.Peek();
                if (!SendTrackInvoke(payload, tauriServerPort, synchronous: false))
                {
                    break;
                }

                s_pendingTrackPayloads.Dequeue();
            }
        }

        public static bool Track(string eventType, string data = null, int tauriServerPort = -1)
        {
            if (!ShouldTrack())
            {
                return false;
            }

            try
            {
                FlushPending(tauriServerPort);

                string userId = s_eventsRequiringUserId.Contains(eventType)
                    ? UnityConnectSession.GetUserId()
                    : null;
                string payload = BuildPayload(eventType, data, userId);

                if (SendTrackInvoke(payload, tauriServerPort, synchronous: false))
                {
                    CodelyLogger.Log($"Codely EventTracker: {eventType}");
                    return true;
                }

                if (s_pendingTrackPayloads.Count < MaxPendingTrackPayloads)
                {
                    s_pendingTrackPayloads.Enqueue(payload);
                    CodelyLogger.LogWarning(
                        $"Codely EventTracker: {eventType} buffered (Tauri port not ready)");
                }
                else
                {
                    CodelyLogger.LogWarning(
                        $"Codely EventTracker: {eventType} dropped (pending queue full)");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely EventTracker error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Track a lifecycle close event via HTTP invoke. Uses a synchronous request so
        /// the event is sent before the Editor window tears down Tauri connectivity.
        /// </summary>
        public static void TryTrackClose(
            string eventType,
            int tauriServerPort,
            ref bool alreadySent,
            bool synchronous = true)
        {
            if (alreadySent || !ShouldTrack())
            {
                return;
            }

            try
            {
                FlushPending(tauriServerPort);

                string userId = s_eventsRequiringUserId.Contains(eventType)
                    ? UnityConnectSession.GetUserId()
                    : null;
                string payload = BuildPayload(eventType, data: null, userId);

                if (SendTrackInvoke(payload, tauriServerPort, synchronous))
                {
                    alreadySent = true;
                    CodelyLogger.Log($"Codely EventTracker: {eventType}");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely EventTracker error: {ex.Message}");
            }
        }

        private static bool ShouldTrack()
        {
            if (!s_isTuanjie.HasValue)
            {
                string editorType = TauriUtils.GetEditorType();
                s_isTuanjie = editorType == "tuanjie";
            }

            return s_isTuanjie.Value;
        }

        private static bool SendTrackInvoke(
            string trackPayloadJson,
            int tauriServerPort,
            bool synchronous)
        {
            return SendHttpInvoke(
                InvokeTrackEvent,
                tauriServerPort,
                synchronous,
                trackPayloadJson);
        }

        private static bool SendHttpInvoke(
            string messageType,
            int tauriServerPort,
            bool synchronous,
            string dataJson = null)
        {
            int port = ResolveTauriPort(tauriServerPort);
            if (port <= 0)
            {
                return false;
            }

            try
            {
                string workspaceDir = TauriUtils.GetWorkspaceDirectory();
                string editorType   = TauriUtils.GetEditorType();
                string messageId    = Guid.NewGuid().ToString();
                string payload      = BuildInvokePayload(
                    messageType,
                    messageId,
                    workspaceDir,
                    editorType,
                    dataJson);
                string url = $"http://127.0.0.1:{port}/api/tauri/invoke";
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                AddInvokeRoutingHeaders(request, workspaceDir, editorType);

                if (synchronous)
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    {
                        using (var response = s_httpClient.SendAsync(request, cts.Token).GetAwaiter().GetResult())
                        {
                            return response.IsSuccessStatusCode;
                        }
                    }
                }

                _ = s_httpClient.SendAsync(request);
                return true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely EventTracker: {messageType} invoke failed: {ex.Message}");
                return false;
            }
        }

        private static int ResolveTauriPort(int tauriServerPort)
        {
            if (tauriServerPort > 0)
            {
                return tauriServerPort;
            }

            return SessionState.GetInt(TauriUtils.SESSION_STATE_TAURI_PORT_KEY, -1);
        }

        private static void AddInvokeRoutingHeaders(
            HttpRequestMessage request,
            string workspaceDir,
            string editorType)
        {
            if (!string.IsNullOrEmpty(workspaceDir))
            {
                request.Headers.TryAddWithoutValidation(
                    "X-Workspace-Dir",
                    Uri.EscapeDataString(workspaceDir));
            }

            if (!string.IsNullOrEmpty(editorType))
            {
                request.Headers.TryAddWithoutValidation("X-Editor-Type", editorType);
            }
        }

        private static string BuildInvokePayload(
            string messageType,
            string messageId,
            string workspaceDir,
            string editorType,
            string dataJson = null)
        {
            string typeEscaped   = EscapeJson(messageType);
            string idEscaped     = EscapeJson(messageId);
            string dirEscaped    = EscapeJson(workspaceDir ?? string.Empty);
            string editorEscaped = EscapeJson(editorType ?? "unity");
            string dataField     = string.IsNullOrEmpty(dataJson) ? "null" : dataJson;
            return $"{{\"message\":{{\"messageType\":\"{typeEscaped}\",\"messageId\":\"{idEscaped}\",\"data\":{dataField}}},\"workspaceDir\":\"{dirEscaped}\",\"editorType\":\"{editorEscaped}\"}}";
        }

        private static string BuildPayload(string eventType, string data, string userId = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\"event_type\":\"").Append(EscapeJson(eventType)).Append("\"");
            sb.Append(",\"timestamp\":\"").Append(DateTimeOffset.UtcNow.ToString("o")).Append("\"");
            if (!string.IsNullOrEmpty(userId))
            {
                sb.Append(",\"user_id\":\"").Append(EscapeJson(userId)).Append("\"");
            }
            if (!string.IsNullOrEmpty(data))
            {
                sb.Append(",\"data\":\"").Append(EscapeJson(data)).Append("\"");
            }
            sb.Append("}");
            return sb.ToString();
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
#endif
