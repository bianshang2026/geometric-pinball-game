using System;
using UnityEditor;
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Centralized logger for the Codely Bridge package.
    ///
    /// All package logging is routed through here so it can be globally
    /// enabled/disabled and consistently branded. This is the ONLY place in the
    /// package that is allowed to call <see cref="UnityEngine.Debug"/> directly;
    /// every other type should log through CodelyLogger.
    ///
    /// Toggle logging via the menu (AI/Logging/Enable Codely Logs) or
    /// programmatically through <see cref="Enabled"/>.
    /// </summary>
    public static class CodelyLogger
    {
        private const string Prefix = "<b><color=#2EA3FF>Codely Bridge</color></b>:";

        // Master on/off switch. Persisted so the choice survives domain reloads.
        private const string EnabledKey = "Codely.Logging.Enabled";

        // Verbose/diagnostic toggle. Reuses the pre-existing key so the legacy
        // "UnityTcp.DebugLogs" preference keeps working unchanged.
        private const string VerboseKey = "UnityTcp.DebugLogs";

        private const string EnabledMenu = "AI/Logging/Enable Codely Logs";
        private const string VerboseMenu = "AI/Logging/Enable Verbose Logs";

        // Cached copies of the persisted prefs. Logs are emitted both from background
        // threads (IPC loops, ThreadPool callbacks) and from [InitializeOnLoad] static
        // constructors during domain reload — in neither case can we rely on EditorPrefs
        // being readable: off the main thread it throws, and constructor ordering across
        // assemblies is undefined, so a separate init method may not have run yet.
        //
        // Instead we lazily read the prefs on first access (EnsureLoaded). The first
        // access happens on the main thread (the bridge's static constructor), where
        // EditorPrefs works; the result is cached in volatile fields that any thread can
        // then read. InitializeOnLoadMethod is kept only as an early best-effort warm-up.
        private static volatile bool s_enabled = false;
        private static volatile bool s_verbose = false;
        private static volatile bool s_loaded = false;

        [InitializeOnLoadMethod]
        private static void Init() => EnsureLoaded();

        private static void EnsureLoaded()
        {
            if (s_loaded) return;
            try
            {
                // Throws if called off the main thread; we keep defaults and retry on
                // the next access until a main-thread caller succeeds.
                s_enabled = EditorPrefs.GetBool(EnabledKey, false);
                s_verbose = EditorPrefs.GetBool(VerboseKey, false);
                s_loaded = true;
            }
            catch { /* not on the main thread yet */ }
        }

        /// <summary>
        /// Master switch. When false, no package log is emitted regardless of
        /// severity. Defaults to false — logging is opt-in.
        /// </summary>
        public static bool Enabled
        {
            get { EnsureLoaded(); return s_enabled; }
            set
            {
                s_enabled = value;
                s_loaded = true;
                try { EditorPrefs.SetBool(EnabledKey, value); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Verbose/diagnostic logging. Gates <see cref="Verbose"/>; has no effect
        /// unless <see cref="Enabled"/> is also true.
        /// </summary>
        public static bool VerboseEnabled
        {
            get { EnsureLoaded(); return s_verbose; }
            set
            {
                s_verbose = value;
                s_loaded = true;
                try { EditorPrefs.SetBool(VerboseKey, value); } catch { /* ignore */ }
            }
        }

        /// <summary>Informational log. Suppressed when logging is disabled.</summary>
        public static void Log(object message)
        {
            if (!Enabled) return;
            Debug.Log($"{Prefix} {message}");
        }

        /// <summary>Warning log. Suppressed when logging is disabled.</summary>
        public static void LogWarning(object message)
        {
            if (!Enabled) return;
            Debug.LogWarning($"<color=#cc7a00>{Prefix} {message}</color>");
        }

        /// <summary>Error log. Suppressed when logging is disabled.</summary>
        public static void LogError(object message)
        {
            if (!Enabled) return;
            Debug.LogError($"<color=#cc3333>{Prefix} {message}</color>");
        }

        /// <summary>Exception log. Suppressed when logging is disabled.</summary>
        public static void LogException(Exception exception)
        {
            if (!Enabled) return;
            Debug.LogException(exception);
        }

        /// <summary>
        /// Diagnostic log. Only emitted when both <see cref="Enabled"/> and
        /// <see cref="VerboseEnabled"/> are true. Use for high-frequency or
        /// detailed traces that should be off by default.
        /// </summary>
        public static void Verbose(object message)
        {
            if (!Enabled || !VerboseEnabled) return;
            Debug.Log($"{Prefix} {message}");
        }

        // ---- Editor menu toggles ------------------------------------------------

        [MenuItem(EnabledMenu, priority = 1000)]
        private static void ToggleEnabled() => Enabled = !Enabled;

        [MenuItem(EnabledMenu, true)]
        private static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(EnabledMenu, Enabled);
            return true;
        }

        [MenuItem(VerboseMenu, priority = 1001)]
        private static void ToggleVerbose() => VerboseEnabled = !VerboseEnabled;

        [MenuItem(VerboseMenu, true)]
        private static bool ToggleVerboseValidate()
        {
            Menu.SetChecked(VerboseMenu, VerboseEnabled);
            return Enabled;
        }
    }
}
