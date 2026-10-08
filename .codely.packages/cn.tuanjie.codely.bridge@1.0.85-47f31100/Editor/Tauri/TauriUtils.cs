#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

using UnityTcp.Editor.Helpers;
namespace Cn.Tuanjie.Codely.Editor
{
    /// <summary>
    /// Shared static utilities used by both <see cref="CodelyWindow"/> and <see cref="DrillWindow"/>.
    /// Centralises SessionState keys, port management, lock-file resolution, and executable discovery.
    /// </summary>
    internal static class TauriUtils
    {
        #region SessionState Keys &amp; Port Constants

        /// <summary>SessionState key for the running Tauri process PID.</summary>
        internal const string SESSION_STATE_TAURI_PID_KEY  = "Codely_TauriPID";

        /// <summary>SessionState key for the running Tauri HTTP server port.</summary>
        internal const string SESSION_STATE_TAURI_PORT_KEY = "Codely_TauriPort";

        internal const int DEFAULT_TAURI_PORT  = 31415;
        internal const int MAX_PORT_ATTEMPTS   = 100;

        internal const string TAURI_ICONS_BASE_PATH = "Packages/cn.tuanjie.codely.bridge/Editor/Tauri/Icons/";

        #endregion

        #region Automation / Headless Detection

        /// <summary>
        /// True when this is not the main interactive Editor — a worker/secondary process, batch
        /// mode, or a null graphics device (CI / QA automation harnesses). Auto-open windows stay
        /// closed in these environments: creating an <see cref="UnityEditor.EditorWindow"/> forces native window +
        /// graphics-surface creation, which fails with a <c>SUCCEEDED(hr)</c> native assertion that
        /// flips otherwise-passing automated test runs to an overall FAIL.
        /// </summary>
        internal static bool IsAutomatedEditorRun()
            => EditorProcessGuard.ShouldSkipEditorUiAutoStart();

        #endregion

        #region Workspace &amp; Lock-File Utilities

        /// <summary>Returns the Unity project root directory (one level above Application.dataPath).</summary>
        internal static string GetWorkspaceDirectory()
            => Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));

        /// <summary>
        /// Returns the per-project lock directory: <c>~/.codely/projects/&lt;sha256_16hex&gt;</c>.
        /// The hash algorithm matches the Rust <c>lock_dir_for_workspace</c> function:
        /// SHA256 of the project path with forward slashes, first 16 hex chars.
        /// </summary>
        internal static string GetLockDirForWorkspace(string workspaceDir)
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home)) return null;

                // Forward-slash normalisation is required so the hash is identical across platforms.
                string projectPath = Path.GetFullPath(workspaceDir).Replace('\\', '/');
                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(projectPath));
                    string hashHex   = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16).ToLowerInvariant();
                    return Path.Combine(home, ".codely", "projects", hashHex);
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely: GetLockDirForWorkspace failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns the global app meta file path: <c>~/.codely/app.lock.meta.json</c>.
        /// Matches the Rust <c>write_global_app_meta</c> location. The file is
        /// written by any running single-instance APP so external processes
        /// can discover the APP's HTTP port.
        /// </summary>
        internal static string GetGlobalAppMetaPath()
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home)) return null;
                return Path.Combine(home, ".codely", "app.lock.meta.json");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely: GetGlobalAppMetaPath failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads <c>~/.codely/data.json</c> and returns whether the drill walkthrough
        /// has been completed. Missing file or unparseable JSON returns <c>false</c>
        /// (treat as not-yet-completed so the walkthrough can open); read errors return
        /// <c>true</c> so we fall back to the main window instead of looping on a broken file.
        /// </summary>
        internal static bool IsDrillCompleted()
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string dataPath = Path.Combine(home, ".codely", "data.json");
                if (!File.Exists(dataPath))
                    return false;
                string json = File.ReadAllText(dataPath);
                var match = Regex.Match(json, "\"drillCompleted\"\\s*:\\s*(true|false)");
                if (match.Success)
                    {
                        return match.Groups[1].Value == "true";
                    }

                return false;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely: Failed to read drill status from data.json: {ex.Message}");
                return true;
            }
        }

        #endregion

        #region Executable Discovery

        /// <summary>
        /// Resolves the <c>cowork</c> / <c>cowork.exe</c> runnable path.
        /// Reads <c>CODELY_APP_HOME</c> from the process environment or OS-level store, then
        /// falls back to the default installation location. Returns <c>null</c> if not found.
        /// </summary>
        internal static string GetCodelyRunnable()
        {
            // Read CODELY_APP_HOME from the OS-level store (registry / login shell) every call
            // so the Editor picks up installer updates without a restart.
            string home = ReadOsEnvVar("CODELY_APP_HOME");

#if UNITY_EDITOR_WIN
            if (!string.IsNullOrEmpty(home) && !File.Exists(Path.Combine(home, "cowork.exe")))
            {
                CodelyLogger.LogWarning($"Codely: CODELY_APP_HOME='{home}' does not contain cowork.exe, falling back to default install location.");
                home = null;
            }
            if (string.IsNullOrEmpty(home))
            {
                string tuanjieHome = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\programs\Tuanjie Cowork");
                string codelyHome  = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\programs\Codely Cowork");
                home = File.Exists(Path.Combine(tuanjieHome, "cowork.exe")) ? tuanjieHome : codelyHome;
            }

            string exePath = Path.Combine(home, "cowork.exe");
#elif UNITY_EDITOR_OSX
            if (!string.IsNullOrEmpty(home) && !File.Exists(Path.Combine(home, "cowork")))
            {
                CodelyLogger.LogWarning($"Codely: CODELY_APP_HOME='{home}' does not contain cowork, falling back to default install location.");
                home = null;
            }
            if (string.IsNullOrEmpty(home))
            {
                const string tuanjieHome = "/Applications/Tuanjie Cowork.app/Contents/MacOS";
                const string codelyHome  = "/Applications/Codely Cowork.app/Contents/MacOS";
                home = File.Exists(Path.Combine(tuanjieHome, "cowork")) ? tuanjieHome : codelyHome;
            }

            string exePath = Path.Combine(home, "cowork");
#else
            if (string.IsNullOrEmpty(home)) return null;
            string exePath = Path.Combine(home, "cowork");
#endif

            if (!File.Exists(exePath))
            {
                CodelyLogger.LogError($"Codely: cowork runnable not found at '{exePath}'. Make sure cowork is installed.");
                return null;
            }
            return Path.GetFullPath(exePath);
        }

        /// <summary>
        /// Resolves the <c>codely</c> / <c>codely.exe</c> CLI path. Mirrors
        /// <see cref="GetCodelyRunnable"/>'s OS-level env-var reading so installer updates take
        /// effect without restarting the Editor:
        /// <list type="number">
        /// <item><c>CODELY_HOME</c>: look directly for <c>codely[.exe]</c> at that root.</item>
        /// <item><c>CODELY_APP_HOME</c>: look under <c>cli\bin\win32-x64</c> (Windows) or
        ///       <c>cli/bin/darwin-arm64</c> / <c>cli/bin/darwin-x64</c> (macOS).</item>
        /// <item>Fallback to the default Tuanjie Cowork / Codely Cowork install dir with the
        ///       same cli subpath.</item>
        /// </list>
        /// Returns <c>null</c> if none of these resolve.
        /// </summary>
        internal static string GetCodelyCliRunnable()
        {
#if UNITY_EDITOR_WIN
            const string cliExe        = "codely.exe";
            string[]     cliSubPaths   = { @"cli\bin\win32-x64" };
            string[]     fallbackRoots =
            {
                Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\programs\Tuanjie Cowork"),
                Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\programs\Codely Cowork"),
            };
#elif UNITY_EDITOR_OSX
            const string cliExe        = "codely";
            // Apple Silicon first, then x64 — try both since the Editor's process arch is not
            // always a reliable indicator of which CLI binary the user has installed.
            string[]     cliSubPaths   = { "cli/bin/darwin-arm64", "cli/bin/darwin-x64" };
            string[]     fallbackRoots =
            {
                "/Applications/Tuanjie Cowork.app/Contents/MacOS",
                "/Applications/Codely Cowork.app/Contents/MacOS",
            };
#else
            return null;
#endif

            // 1) CODELY_HOME — direct codely[.exe] at the root.
            string codelyHome = ReadOsEnvVar("CODELY_HOME");
            if (!string.IsNullOrEmpty(codelyHome))
            {
                string direct = Path.Combine(codelyHome, cliExe);
                if (File.Exists(direct)) return Path.GetFullPath(direct);
                CodelyLogger.LogWarning($"Codely: CODELY_HOME='{codelyHome}' does not contain {cliExe}, falling back to CODELY_APP_HOME.");
            }

            // 2) CODELY_APP_HOME + cli subpath.
            string appHome = ReadOsEnvVar("CODELY_APP_HOME");
            if (!string.IsNullOrEmpty(appHome))
            {
                string found = TryFindCli(appHome, cliSubPaths, cliExe);
                if (!string.IsNullOrEmpty(found)) return found;
                CodelyLogger.LogWarning($"Codely: CODELY_APP_HOME='{appHome}' does not contain {cliExe} under the expected cli/bin path, falling back to default install location.");
            }

            // 3) Fallback: default install locations (Tuanjie Cowork preferred, then Codely Cowork).
            foreach (var root in fallbackRoots)
            {
                string found = TryFindCli(root, cliSubPaths, cliExe);
                if (!string.IsNullOrEmpty(found)) return found;
            }

            CodelyLogger.LogError("Codely: codely CLI runnable not found. Make sure Codely is installed.");
            return null;
        }

        private static string TryFindCli(string root, string[] subPaths, string exeName)
        {
            if (string.IsNullOrEmpty(root)) return null;
            foreach (var sub in subPaths)
            {
                string candidate = Path.Combine(root, sub, exeName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            return null;
        }

        /// <summary>
        /// Reads an environment variable from the OS-level store (Windows registry HKCU/HKLM
        /// Environment, or macOS login shell), bypassing the Unity Editor process's cached env.
        /// Returns <c>null</c> if not set or unavailable. Windows values are
        /// <see cref="Environment.ExpandEnvironmentVariables"/>-expanded before return.
        /// </summary>
        private static string ReadOsEnvVar(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
#if UNITY_EDITOR_WIN
            var registryLocations = new (Microsoft.Win32.RegistryKey root, string subKey)[]
            {
                (Microsoft.Win32.Registry.CurrentUser,  @"Environment"),
                (Microsoft.Win32.Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
            };
            foreach (var (root, subKey) in registryLocations)
            {
                try
                {
                    using (var key = root.OpenSubKey(subKey, writable: false))
                    {
                        string value = key?.GetValue(name, null,
                            Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                        if (!string.IsNullOrEmpty(value))
                            return Environment.ExpandEnvironmentVariables(value);
                    }
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"Codely: Failed to read {name} from registry ({subKey}): {ex.Message}");
                }
            }
            return null;
#elif UNITY_EDITOR_OSX
            try
            {
                string shell = Environment.GetEnvironmentVariable("SHELL");
                if (string.IsNullOrEmpty(shell)) shell = "/bin/zsh";
                for (int pass = 0; pass < 2; pass++)
                {
                    string flags = pass == 0 ? "-l" : "-i -l";
                    var psi = new ProcessStartInfo(shell, $"{flags} -c \"echo ${name}\"")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                    };
                    using (var proc = Process.Start(psi))
                    {
                        string value = proc?.StandardOutput.ReadLine()?.Trim();
                        proc?.WaitForExit(3000);
                        if (!string.IsNullOrEmpty(value)) return value;
                    }
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"Codely: Failed to read {name} from login shell: {ex.Message}");
            }
            return null;
#else
            return null;
#endif
        }

        /// <summary>
        /// Returns <c>"tuanjie"</c> if the project uses the Tuanjie engine, otherwise <c>"unity"</c>.
        /// </summary>
        internal static string GetEditorType()
        {
            try
            {
                string versionTxt = Path.GetFullPath(Path.Combine(
                    UnityEngine.Application.dataPath, "..", "ProjectSettings", "ProjectVersion.txt"));
                if (!File.Exists(versionTxt))
                    return "unity";
                string content = File.ReadAllText(versionTxt);
                // m_TuanjieEditorVersion is the primary signal; m_EditorVersion with a
                // 't' suffix (e.g. 2022.3.62t1) is the fallback for Tuanjie projects
                // that don't yet write the dedicated field.
                if (content.Contains("m_TuanjieEditorVersion"))
                    return "tuanjie";
                var match = Regex.Match(content, @"m_EditorVersion:\s*\d+\.\d+\.\d+(\w+)");
                if (match.Success && match.Groups[1].Value.Contains("t"))
                    return "tuanjie";
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Codely: Error reading ProjectVersion.txt: {ex.Message}");
            }
            return "unity";
        }

        #endregion

        #region Port Utilities

        internal static bool IsPortAvailable(int port)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch { return false; }
        }

        internal static int FindAvailablePort()
        {
            for (int port = DEFAULT_TAURI_PORT; port < DEFAULT_TAURI_PORT + MAX_PORT_ATTEMPTS; port++)
            {
                if (IsPortAvailable(port)) return port;
            }
            return -1;
        }

        /// <summary>
        /// Removes inherited pkg bootstrap variables before launching cowork/Tauri.
        /// If these variables leak from the editor process into cowork/core, core
        /// startup can fail inside the editor because pkg mis-detects its entrypoint.
        /// </summary>
        internal static void SanitizePkgEnvironment(ProcessStartInfo startInfo)
        {
            if (startInfo == null || startInfo.UseShellExecute)
            {
                return;
            }

            startInfo.EnvironmentVariables.Remove("PKG_EXECPATH");
            startInfo.EnvironmentVariables.Remove("PKG_INVOKE_NODEJS");
            startInfo.EnvironmentVariables.Remove("PKG_INVOKE_ENTRYPOINT");
        }

        #endregion

        #region File &amp; JSON Utilities

        /// <summary>
        /// Reads a text file with retry on <see cref="IOException"/>.
        /// Returns <c>null</c> on permanent failure or if the file does not exist.
        /// </summary>
        internal static string TryReadFileWithRetry(string filePath, int retryCount, int retryDelayMs)
        {
            for (int attempt = 1; attempt <= retryCount; attempt++)
            {
                try
                {
                    if (!File.Exists(filePath)) return null;
                    return File.ReadAllText(filePath);
                }
                catch (IOException ex)
                {
                    if (attempt >= retryCount)
                    {
                        CodelyLogger.LogWarning($"Codely: Failed to read file after {retryCount} attempts. path={filePath}, error={ex.Message}");
                        return null;
                    }
                    Thread.Sleep(retryDelayMs);
                }
                catch (UnauthorizedAccessException ex)
                {
                    CodelyLogger.LogWarning($"Codely: No permission to read file. path={filePath}, error={ex.Message}");
                    return null;
                }
                catch { return null; }
            }
            return null;
        }

        /// <summary>
        /// Extracts an integer field from a simple flat JSON string using regex.
        /// Returns <c>-1</c> if the field is absent or cannot be parsed.
        /// </summary>
        internal static int ExtractIntFromJson(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(fieldName)) return -1;
            var match = Regex.Match(json, $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*(\\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out int v) ? v : -1;
        }

        #endregion
    }
}
#endif
