using System;
using System.Runtime.InteropServices;

namespace UnityTcp.Editor.Native
{
    /// <summary>
    /// Low-level P/Invoke declarations for the NativeWindowBridge streaming plugin.
    /// Mirrors NativeWindowBridge.h C ABI exactly.
    /// </summary>
    internal static class NativeWindowBridgeAPI
    {
        // Keep the delegate type available cross-platform so managed references compile.
        // On unsupported platforms, NWB_SetLogCallback is a no-op and never reaches native code.
        internal delegate void NWB_LogCallbackDelegate(int level, string message);

#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
        private const string DllName = "NativeWindowBridge";

        [DllImport(DllName, EntryPoint = "NWB_StartServer")]
        private static extern int NWB_StartServer_Native(int httpPort);

        [DllImport(DllName, EntryPoint = "NWB_StopServer")]
        private static extern void NWB_StopServer_Native();

        [DllImport(DllName, EntryPoint = "NWB_IsRunning")]
        private static extern int NWB_IsRunning_Native();

        [DllImport(DllName, EntryPoint = "NWB_GetBoundPort")]
        private static extern int NWB_GetBoundPort_Native();

        [DllImport(DllName, EntryPoint = "NWB_UpdateWindowList", CharSet = CharSet.Ansi)]
        private static extern int NWB_UpdateWindowList_Native(string windowsJson);

        [DllImport(DllName, EntryPoint = "NWB_SetUnityPid")]
        private static extern void NWB_SetUnityPid_Native(int pid);

        [DllImport(DllName, EntryPoint = "NWB_GetWindowListJson")]
        private static extern int NWB_GetWindowListJson_Native(byte[] buffer, int bufferCapacity, out int outBytes);

        [DllImport(DllName, EntryPoint = "NWB_GetStreamStatusJson")]
        private static extern int NWB_GetStreamStatusJson_Native(byte[] buffer, int bufferCapacity, out int outBytes);

        // Offscreen rendering mode: C# pushes frames and polls input events.
        [DllImport(DllName, EntryPoint = "NWB_StartOffscreenCapture")]
        private static extern int NWB_StartOffscreenCapture_Native(int fps, int width, int height);

        /// <summary>Stops offscreen/OS capture on the native side (resets m_OffscreenMode).</summary>
        [DllImport(DllName, EntryPoint = "NWB_StopCapture")]
        private static extern void NWB_StopCapture_Native();

        [DllImport(DllName, EntryPoint = "NWB_PushFrame")]
        private static extern int NWB_PushFrame_Native(IntPtr bgraData, int width, int height, int stride);

        [DllImport(DllName, EntryPoint = "NWB_GetPendingInput")]
        private static extern int NWB_GetPendingInput_Native(byte[] buffer, int bufferSize);

        [DllImport(DllName, EntryPoint = "NWB_GetPendingOffscreenRequest")]
        private static extern int NWB_GetPendingOffscreenRequest_Native(
            byte[] windowTypeBuf, int bufSize,
            out int outFps, out int outWidth, out int outHeight, out float outDpr);

        [DllImport(DllName, EntryPoint = "NWB_GetPendingCompositeRequest")]
        private static extern int NWB_GetPendingCompositeRequest_Native(
            byte[] jsonBuf, int bufSize,
            out int outFps, out int outWidth, out int outHeight, out float outDpr);

        [DllImport(DllName, EntryPoint = "NWB_IsOffscreenActive")]
        private static extern int NWB_IsOffscreenActive_Native();

        // Check for pending resize request from frontend (via /stream/resize).
        // Returns 1 if pending (and clears it), 0 otherwise.
        [DllImport(DllName, EntryPoint = "NWB_GetPendingResize")]
        private static extern int NWB_GetPendingResize_Native(out int outWidth, out int outHeight, out float outDpr);

        // Query the active offscreen capture window type (survives domain reload).
        // Returns bytes written (excluding null), or 0 if no active offscreen.
        [DllImport(DllName, EntryPoint = "NWB_GetActiveOffscreenWindowType")]
        private static extern int NWB_GetActiveOffscreenWindowType_Native(byte[] buffer, int bufferSize);

        // Send a UTF-8 JSON message to the browser via the WebRTC DataChannel.
        [DllImport(DllName, EntryPoint = "NWB_SendDataChannelMessage")]
        private static extern int NWB_SendDataChannelMessage_Native(byte[] jsonUtf8, int length);

        // Seconds since the last browser heartbeat arrived via DataChannel.
        // Tracked in C++ (WebRTC thread), accurate even when C# main thread
        // is blocked. Returns -1 if no heartbeat has been received yet.
        [DllImport(DllName, EntryPoint = "NWB_GetSecondsSinceLastHeartbeat")]
        private static extern float NWB_GetSecondsSinceLastHeartbeat_Native();

        [DllImport(DllName, EntryPoint = "NWB_SetLogCallback")]
        private static extern void NWB_SetLogCallback_Native(NWB_LogCallbackDelegate callback);
#endif

        // Public managed surface. On unsupported platforms this is intentionally
        // implemented as no-op/default so shared code compiles and does not call native.
        internal static int NWB_StartServer(int httpPort)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_StartServer_Native(httpPort);
#else
            return 0;
#endif
        }

        internal static void NWB_StopServer()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            NWB_StopServer_Native();
#endif
        }

        internal static int NWB_IsRunning()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_IsRunning_Native();
#else
            return 0;
#endif
        }

        internal static int NWB_GetBoundPort()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_GetBoundPort_Native();
#else
            return 0;
#endif
        }

        internal static int NWB_UpdateWindowList(string windowsJson)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_UpdateWindowList_Native(windowsJson);
#else
            return 0;
#endif
        }

        internal static void NWB_SetUnityPid(int pid)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            NWB_SetUnityPid_Native(pid);
#endif
        }

        internal static int NWB_GetWindowListJson(byte[] buffer, int bufferCapacity, out int outBytes)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_GetWindowListJson_Native(buffer, bufferCapacity, out outBytes);
#else
            outBytes = 0;
            return 0;
#endif
        }

        internal static int NWB_GetStreamStatusJson(byte[] buffer, int bufferCapacity, out int outBytes)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_GetStreamStatusJson_Native(buffer, bufferCapacity, out outBytes);
#else
            outBytes = 0;
            return 0;
#endif
        }

        internal static int NWB_StartOffscreenCapture(int fps, int width, int height)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_StartOffscreenCapture_Native(fps, width, height);
#else
            return 0;
#endif
        }

        internal static void NWB_StopCapture()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            NWB_StopCapture_Native();
#endif
        }

        internal static int NWB_PushFrame(IntPtr bgraData, int width, int height, int stride)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_PushFrame_Native(bgraData, width, height, stride);
#else
            return 0;
#endif
        }

        internal static int NWB_GetPendingInput(byte[] buffer, int bufferSize)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_GetPendingInput_Native(buffer, bufferSize);
#else
            return 0;
#endif
        }

        internal static int NWB_GetPendingOffscreenRequest(
            byte[] windowTypeBuf, int bufSize, out int outFps, out int outWidth, out int outHeight, out float outDpr)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_GetPendingOffscreenRequest_Native(windowTypeBuf, bufSize, out outFps, out outWidth, out outHeight, out outDpr);
#else
            outFps = 0;
            outWidth = 0;
            outHeight = 0;
            outDpr = 0f;
            return 0;
#endif
        }

        internal static int NWB_GetPendingCompositeRequest(
            byte[] jsonBuf, int bufSize, out int outFps, out int outWidth, out int outHeight, out float outDpr)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_GetPendingCompositeRequest_Native(jsonBuf, bufSize, out outFps, out outWidth, out outHeight, out outDpr);
#else
            outFps = 0;
            outWidth = 0;
            outHeight = 0;
            outDpr = 0f;
            return 0;
#endif
        }

        internal static int NWB_IsOffscreenActive()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_IsOffscreenActive_Native();
#else
            return 0;
#endif
        }

        internal static int NWB_GetPendingResize(out int outWidth, out int outHeight, out float outDpr)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_GetPendingResize_Native(out outWidth, out outHeight, out outDpr);
#else
            outWidth = 0;
            outHeight = 0;
            outDpr = 0f;
            return 0;
#endif
        }

        internal static int NWB_GetActiveOffscreenWindowType(byte[] buffer, int bufferSize)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_GetActiveOffscreenWindowType_Native(buffer, bufferSize);
#else
            return 0;
#endif
        }

        internal static int NWB_SendDataChannelMessage(byte[] jsonUtf8, int length)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_SendDataChannelMessage_Native(jsonUtf8, length);
#else
            return 0;
#endif
        }

        internal static float NWB_GetSecondsSinceLastHeartbeat()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            return NWB_GetSecondsSinceLastHeartbeat_Native();
#else
            return -1f;
#endif
        }

        internal static void NWB_SetLogCallback(NWB_LogCallbackDelegate callback)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            NWB_SetLogCallback_Native(callback);
#endif
        }
    }
}
