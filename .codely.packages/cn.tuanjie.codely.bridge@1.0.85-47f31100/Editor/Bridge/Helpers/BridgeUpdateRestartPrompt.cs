using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Prompt to restart Unity after the Bridge native plugin changes.
    /// Pending lives in SessionState so a domain reload still shows the dialog.
    /// </summary>
    [InitializeOnLoad]
    internal static class BridgeUpdateRestartPrompt
    {
        internal const string PendingKey = "UnityTcp.BridgeRestartPending";
        internal const string LastDllHashKey = "UnityTcp.BridgeLastNativeDllHash";
        private const string PromptedKey = "UnityTcp.BridgeRestartPrompted";

        internal const string DialogTitle = "Codely Bridge Updated";
        internal const string DialogMessage =
            "The Codely Bridge package has been updated. Restart the Tuanjie Editor to load it.\n\n" +
            "Codely Bridge 已更新。请重启团结编辑器以加载新版本。";
        internal const string RestartButton = "Restart Now";
        internal const string LaterButton = "Later";

        internal static Func<string, string, string, string, bool> ShowDialog;
        internal static Action RestartEditor;
        internal static Func<bool> IsAutomated;

        static BridgeUpdateRestartPrompt()
        {
            if (EditorAutomationGuard.ShouldSkipBridgeAutoStart()) return;

            EditorApplication.delayCall += TryShowPendingPrompt;
        }

        internal static bool IsPending() => SessionState.GetBool(PendingKey, false);

        internal static string LastKnownNativeDllHash() =>
            SessionState.GetString(LastDllHashKey, null);

        internal static void RememberNativeDllFromFile(string path)
        {
            string hash = TryHashFile(path);
            if (string.IsNullOrEmpty(hash)) return;
            SessionState.SetString(LastDllHashKey, hash);
        }

        /// <summary>
        /// True when the incoming native plugin bytes differ from the
        /// currently loaded plugin (or, if nothing is remembered yet, from
        /// the changedFrom file). Missing changedFrom / unreadable files
        /// are not an update.
        /// </summary>
        internal static bool IsNativeDllContentChanged(string fromDllPath, string toDllPath)
        {
            if (string.IsNullOrEmpty(fromDllPath) || string.IsNullOrEmpty(toDllPath))
                return false;

            string toHash = TryHashFile(toDllPath);
            if (string.IsNullOrEmpty(toHash))
                return false;

            string known = LastKnownNativeDllHash();
            if (!string.IsNullOrEmpty(known))
                return known != toHash;

            string fromHash = TryHashFile(fromDllPath);
            if (string.IsNullOrEmpty(fromHash))
                return false;
            return fromHash != toHash;
        }

        internal static void MarkUpdated()
        {
            SessionState.SetBool(PendingKey, true);
            SessionState.SetBool(PromptedKey, false);
        }

        internal static void TryShowPendingPrompt()
        {
            if (ShowDialog == null &&
                (EditorApplication.isCompiling || EditorApplication.isUpdating))
            {
                EditorApplication.delayCall += TryShowPendingPrompt;
                return;
            }

            bool automated = (IsAutomated ?? EditorAutomationGuard.IsAutomatedEditorRun)();
            if (automated || !IsPending() || SessionState.GetBool(PromptedKey, false))
                return;

            SessionState.SetBool(PromptedKey, true);
            SessionState.SetBool(PendingKey, false);

            var show = ShowDialog ?? DefaultShowDialog;
            if (show(DialogTitle, DialogMessage, RestartButton, LaterButton))
                (RestartEditor ?? DefaultRestartEditor)();
        }

        internal static void ResetForTests()
        {
            SessionState.EraseBool(PendingKey);
            SessionState.EraseBool(PromptedKey);
            SessionState.EraseString(LastDllHashKey);
            ShowDialog = null;
            RestartEditor = null;
            IsAutomated = null;
        }

        private static string TryHashFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;
                using (var md5 = MD5.Create())
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] hash = md5.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning(
                    $"BridgeUpdateRestartPrompt: failed to hash '{path}': {ex.Message}");
                return null;
            }
        }

        private static bool DefaultShowDialog(string title, string message, string ok, string cancel)
            => EditorUtility.DisplayDialog(title, message, ok, cancel);

        private static void DefaultRestartEditor()
        {
            string projectPath = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(projectPath))
                projectPath = Directory.GetCurrentDirectory();
            EditorApplication.OpenProject(projectPath);
        }
    }
}
