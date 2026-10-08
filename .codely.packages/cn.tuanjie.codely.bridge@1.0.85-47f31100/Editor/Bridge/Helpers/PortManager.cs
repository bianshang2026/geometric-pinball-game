using System.IO;
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    // Resolves the handshake file path. Content (unity_port, heartbeat, reason,
    // ...) is owned entirely by the native heartbeat writer — see
    // NativeUnityTcpBridgeHost.StartOrAttach.
    public static class PortManager
    {
        private const string RegistryFileName = ".com-unity-codely.json";

        private static string GetRegistryDirectory()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string tempDir = Path.Combine(projectRoot, "Temp");
            Directory.CreateDirectory(tempDir);
            return tempDir;
        }

        public static string GetRegistryFilePath() =>
            Path.Combine(GetRegistryDirectory(), RegistryFileName);
    }
}
