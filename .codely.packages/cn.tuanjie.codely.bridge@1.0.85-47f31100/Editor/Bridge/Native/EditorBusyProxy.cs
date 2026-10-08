namespace UnityTcp.Editor.Native
{
    // Managed code only enables the feature; native window probes decide when to show.
    internal static class EditorBusyProxy
    {
        internal const bool BusyWindowEnabled = true;

        internal static void Refresh()
        {
#if UNITY_EDITOR_WIN
            try { NativeUnityTcpBridgeAPI.NTB_SetEditorBusyProxyEnabled?.Invoke(BusyWindowEnabled ? 1 : 0); }
            catch { /* Optional UI must not interrupt bridge startup. */ }
#endif
        }
    }
}
