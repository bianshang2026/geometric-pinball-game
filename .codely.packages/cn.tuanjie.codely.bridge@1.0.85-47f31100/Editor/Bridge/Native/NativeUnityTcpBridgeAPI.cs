using System;

namespace UnityTcp.Editor.Native
{
    // P/Invoke surface for the unified NativeTcpBridge (NTB_*) C ABI.
    // One TCP listener serves both inbound commands and outbound notifications.
    internal static class NativeUnityTcpBridgeAPI
    {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
        // Must equal NTB_ABI_VERSION in NativeTcpBridge.h. NativeDllLoader
        // reads the native value before calling anything else and refuses to
        // bind on a mismatch — see the ABI stability note in that header.
        //
        // The mismatch is not hypothetical bookkeeping: Unity 2019 runs the
        // previously compiled assembly once against a freshly updated native
        // plugin before it recompiles, so this exact check is what stands
        // between a version skew and a call with the wrong argument count.
        internal const int ExpectedAbiVersion = 2;

        // FROZEN — never change this signature; it is the handshake that lets
        // every other signature here change safely. See NativeTcpBridge.h.
        internal delegate int  GetAbiVersionDelegate();

        // heartbeatFilePathUtf8Z/projectPathUtf8Z are UTF-8, null-terminated
        // byte arrays (see NativeUnityTcpBridgeHost.Utf8Z) — not the raw .NET
        // string marshaler, to match this file's existing byte[]-buffer
        // convention. Idempotent: if the native singleton is already running
        // (e.g. reattaching after a domain reload), returns the existing
        // bound port without re-binding and without touching the heartbeat
        // writer, whose lifetime matches the TCP listener's exactly.
        //
        // Named "StartBridge", not "Start": the native entry point was renamed
        // when it gained the heartbeat parameters, so that assemblies compiled
        // against the old 2-arg NTB_Start fail to resolve it and shut down
        // cleanly instead of calling it with the wrong argument count. See the
        // ABI stability note in NativeTcpBridge.h before renaming or changing
        // the signature of anything here.
        internal delegate int  StartBridgeDelegate(int requestedPort, int maxFrameBytes, byte[] heartbeatFilePathUtf8Z, byte[] projectPathUtf8Z, int heartbeatIntervalMs, int streamPort);
        internal delegate void StopDelegate();
        // Added after ABI v1 shipped, deliberately without a version bump:
        // additive entry points can't cause the argument-count hazard the
        // guard exists for (a stale assembly never resolves a name it wasn't
        // compiled with). Resolved as optional — see ResolveAll — so callers
        // must null-check and fall back to NTB_Stop, though with managed and
        // native always shipping together it is only ever null on a dev-time
        // skew. reasonUtf8Z is UTF-8, null-terminated (see
        // NativeUnityTcpBridgeHost.Utf8Z); it lands in the heartbeat file's
        // "reason" field while stopped.
        internal delegate void StopWithReasonDelegate(byte[] reasonUtf8Z);
        internal delegate int  IsRunningDelegate();
        internal delegate int  GetBoundPortDelegate();
        internal delegate int  GetConnectionCountDelegate();
        internal delegate int  TryDequeueCommandDelegate(out ulong outRequestId, byte[] buffer, int bufferCapacity, out int outPayloadBytes);
        internal delegate int  TryBeginCommandDelegate(ulong requestId);
        internal delegate int  EnqueueResponseDelegate(ulong requestId, byte[] payload, int payloadBytes);
        internal delegate int  GetClientsJsonDelegate(byte[] buffer, int bufferCapacity, out int outBytes);
        internal delegate void PauseManagedCommandProcessingDelegate();
        internal delegate void ResumeManagedCommandProcessingDelegate();
        internal delegate int  NotifyAllDelegate(byte[] payload, int payloadBytes);
        // The only piece of native heartbeat state meant to change after
        // NTB_StartBridge — safe to call at any time.
        internal delegate void SetStreamPortDelegate(int streamPort);

        // Optional Windows busy-Editor proxy; unavailable UI must not block commands.
        internal delegate void SetEditorBusyProxyEnabledDelegate(int enabled);

        internal static GetAbiVersionDelegate                NTB_GetAbiVersion;
        internal static StartBridgeDelegate                  NTB_StartBridge;
        internal static StopDelegate                         NTB_Stop;
        internal static StopWithReasonDelegate               NTB_StopWithReason;
        internal static IsRunningDelegate                    NTB_IsRunning;
        internal static GetBoundPortDelegate                 NTB_GetBoundPort;
        internal static GetConnectionCountDelegate           NTB_GetConnectionCount;
        internal static TryDequeueCommandDelegate            NTB_TryDequeueCommand;
        internal static TryBeginCommandDelegate              NTB_TryBeginCommand;
        internal static EnqueueResponseDelegate              NTB_EnqueueResponse;
        internal static GetClientsJsonDelegate               NTB_GetClientsJson;
        internal static PauseManagedCommandProcessingDelegate  NTB_PauseManagedCommandProcessing;
        internal static ResumeManagedCommandProcessingDelegate NTB_ResumeManagedCommandProcessing;
        internal static NotifyAllDelegate                       NTB_NotifyAll;
        internal static SetStreamPortDelegate                   NTB_SetStreamPort;
        internal static SetEditorBusyProxyEnabledDelegate       NTB_SetEditorBusyProxyEnabled;
#endif
    }
}
