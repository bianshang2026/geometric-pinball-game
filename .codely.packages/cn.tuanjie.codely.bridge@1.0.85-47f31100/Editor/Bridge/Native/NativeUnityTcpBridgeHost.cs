using System;
using System.Collections.Generic;
using System.Text;
using Codely.Newtonsoft.Json;
using UnityEngine;

using UnityTcp.Editor.Helpers;
namespace UnityTcp.Editor.Native
{
    // Managed wrapper around the unified NativeTcpBridge (NTB_*) C ABI.
    // One TCP listener serves both inbound commands and outbound notifications;
    // clients that advertise CLIENT_VERSION>=2 are eligible for notifications.
    internal static class NativeUnityTcpBridgeHost
    {
        private const int InboundBufferSize = 8 * 1024 * 1024;
        private const int ClientSnapshotBufferSize = 512 * 1024;

        private static readonly byte[] InboundBuffer = new byte[InboundBufferSize];
        private static readonly byte[] ClientSnapshotBuffer = new byte[ClientSnapshotBufferSize];

        private sealed class ClientSnapshot
        {
            public List<ClientSnapshotEntry> clients { get; set; }
        }

        private sealed class ClientSnapshotEntry
        {
            public ulong id { get; set; }
            public string endpoint { get; set; }
            public string platform { get; set; }
            public int version { get; set; }
        }

        // ---- Start / Stop ---------------------------------------------- //

        // heartbeatFilePath/projectPath configure the handshake file written
        // on a fresh bind (see NTB_StartBridge's doc comment). These only
        // take effect on a true fresh start, not a reattach.
        // heartbeatIntervalMs is accepted for ABI compatibility and ignored.
        public static bool StartOrAttach(
            int requestedPort, int maxFrameBytes,
            string heartbeatFilePath, string projectPath, int heartbeatIntervalMs, int streamPort,
            out int boundPort)
        {
            boundPort = 0;
            if (!NativeDllLoader.IsLoaded && !NativeDllLoader.Load())
            {
                CodelyLogger.LogError("NativeTcpBridge: failed to load native library. Ensure the plugin is installed correctly.");
                return false;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                EditorBusyProxy.Refresh();
                // After a domain reload the managed side restarts but the native server
                // remains bound to its existing port. Probe first so we attach to the
                // existing listener instead of relying solely on NTB_StartBridge's idempotency.
                if (IsRunning())
                {
                    int existingPort = GetBoundPort();
                    if (existingPort > 0)
                    {
                        boundPort = existingPort;
                        return true;
                    }
                }

                int startResult = NativeUnityTcpBridgeAPI.NTB_StartBridge(
                    requestedPort, maxFrameBytes,
                    Utf8Z(heartbeatFilePath), Utf8Z(projectPath), heartbeatIntervalMs, streamPort);
                if (startResult <= 0) return false;
                boundPort = startResult;
                return true;
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"NativeTcpBridge start failed: {ex.Message}");
                return false;
            }
        }

        // reason, when given, ends up in the heartbeat file's "reason" field
        // for as long as the bridge stays stopped (e.g. "manually_stopped",
        // "unity_quit", "package_updating"), so clients can tell a deliberate
        // shutdown from an unexpected one. Null keeps the generic "stopped".
        public static void StopServer(string reason = null)
        {
            if (!NativeDllLoader.IsLoaded) return;

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                // NTB_StopWithReason is optional (older DLLs predate it and
                // ship the same ABI version) — fall back to plain NTB_Stop.
                if (!string.IsNullOrEmpty(reason) && NativeUnityTcpBridgeAPI.NTB_StopWithReason != null)
                    NativeUnityTcpBridgeAPI.NTB_StopWithReason(Utf8Z(reason));
                else
                    NativeUnityTcpBridgeAPI.NTB_Stop();
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge stop failed: {ex.Message}");
            }
        }

        // ---- Query methods --------------------------------------------- //

        public static bool IsRunning()
        {
            if (!NativeDllLoader.IsLoaded)
            {
                return false;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                return NativeUnityTcpBridgeAPI.NTB_IsRunning() == 1;
#else
                return false;
#endif
            }
            catch { return false; }
        }

        public static int GetBoundPort()
        {
            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return 0;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                return NativeUnityTcpBridgeAPI.NTB_GetBoundPort?.Invoke() ?? 0;
#else
                return 0;
#endif
            }
            catch { return 0; }
        }

        public static int GetConnectionCount()
        {
            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return 0;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                return Math.Max(0, NativeUnityTcpBridgeAPI.NTB_GetConnectionCount());
#else
                return 0;
#endif
            }
            catch { return 0; }
        }

        // ---- Command queue --------------------------------------------- //

        public static bool TryDequeueCommand(out ulong requestId, out string commandText)
        {
            requestId   = 0;
            commandText = null;

            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return false;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                if (NativeUnityTcpBridgeAPI.NTB_TryDequeueCommand(
                    out requestId,
                    InboundBuffer,
                    InboundBuffer.Length,
                    out int payloadBytes) != 1)
                {
                    return false;
                }

                if (payloadBytes <= 0 || payloadBytes > InboundBuffer.Length) return false;

                commandText = Encoding.UTF8.GetString(InboundBuffer, 0, payloadBytes);
                return !string.IsNullOrEmpty(commandText);
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge dequeue failed: {ex.Message}");
                return false;
            }
        }

        public static bool TryBeginCommand(ulong requestId)
        {
            if (!NativeDllLoader.IsLoaded || requestId == 0) return false;
            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                return NativeUnityTcpBridgeAPI.NTB_TryBeginCommand(requestId) == 1;
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge begin command failed: {ex.Message}");
                return false;
            }
        }

        public static bool EnqueueResponse(ulong requestId, string responseJson)
        {
            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return false;
            }

            if (requestId == 0 || string.IsNullOrEmpty(responseJson)) return false;

            try
            {
                byte[] payload = Encoding.UTF8.GetBytes(responseJson);
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                return NativeUnityTcpBridgeAPI.NTB_EnqueueResponse(requestId, payload, payload.Length) == 1;
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge enqueue response failed: {ex.Message}");
                return false;
            }
        }

        // ---- Managed command lifecycle -------------------------------- //

        public static void PauseManagedCommandProcessing()
        {
            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                NativeUnityTcpBridgeAPI.NTB_PauseManagedCommandProcessing?.Invoke();
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge PauseManagedCommandProcessing failed: {ex.Message}");
            }
        }

        public static void ResumeManagedCommandProcessing()
        {
            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                NativeUnityTcpBridgeAPI.NTB_ResumeManagedCommandProcessing?.Invoke();
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge ResumeManagedCommandProcessing failed: {ex.Message}");
            }
        }

        // ---- Notify ---------------------------------------------------- //

        public static bool NotifyAll(string payloadJson)
        {
            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started.");
                return false;
            }

            if (string.IsNullOrEmpty(payloadJson)) return false;

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(payloadJson);
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                return NativeUnityTcpBridgeAPI.NTB_NotifyAll(bytes, bytes.Length) == 1;
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge NotifyAll failed: {ex.Message}");
                return false;
            }
        }

        // ---- Connected clients ----------------------------------------- //

        public static Dictionary<string, string> GetConnectedClients()
        {
            var result = new Dictionary<string, string>();

            if (!NativeDllLoader.IsLoaded)
            {
                CodelyLogger.LogError("NativeTcpBridge is not started. Call Start Bridge first.");
                return result;
            }

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                if (NativeUnityTcpBridgeAPI.NTB_GetClientsJson(
                    ClientSnapshotBuffer,
                    ClientSnapshotBuffer.Length,
                    out int bytes) != 1)
                {
                    return result;
                }

                if (bytes <= 0 || bytes > ClientSnapshotBuffer.Length) return result;

                string json = Encoding.UTF8.GetString(ClientSnapshotBuffer, 0, bytes);
                var snapshot = JsonConvert.DeserializeObject<ClientSnapshot>(json);
                if (snapshot?.clients == null) return result;

                foreach (var client in snapshot.clients)
                {
                    if (string.IsNullOrEmpty(client?.endpoint)) continue;
                    result[client.endpoint] = string.IsNullOrEmpty(client.platform) ? "unknown" : client.platform;
                }
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge get clients failed: {ex.Message}");
            }

            return result;
        }

        // ---- Heartbeat --------------------------------------------------- //

        // Forwards the NWB (WebRTC/stream) signaling port to native. Kept
        // for ABI compatibility; the handshake file no longer surfaces
        // stream_port.
        public static void SetStreamPort(int streamPort)
        {
            if (!NativeDllLoader.IsLoaded) return;

            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
                NativeUnityTcpBridgeAPI.NTB_SetStreamPort?.Invoke(streamPort);
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"NativeTcpBridge SetStreamPort failed: {ex.Message}");
            }
        }

        // UTF-8, null-terminated — matches the byte[] convention this P/Invoke
        // surface uses elsewhere, rather than relying on string marshaling
        // attributes that vary across Mono/IL2CPP versions.
        private static byte[] Utf8Z(string s) => Encoding.UTF8.GetBytes((s ?? string.Empty) + "\0");
    }
}
