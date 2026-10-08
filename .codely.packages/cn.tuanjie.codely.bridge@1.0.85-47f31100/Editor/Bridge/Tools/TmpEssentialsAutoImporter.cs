using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Silently imports the "TMP Essential Resources" after the bridge installs
    /// TextMesh Pro via a tool call, so the interactive "TMP Importer" window never
    /// blocks automated workflows (e.g. install_package_and_validate installing
    /// com.unity.textmeshpro).
    ///
    /// This does NOT run on ordinary editor loads. It only acts when
    /// <see cref="ScheduleImport"/> has been called by <c>ManagePackage</c> after a
    /// tool-initiated TMP install. The pending flag lives in <see cref="SessionState"/>
    /// so it survives the post-install domain reload but not an editor restart.
    /// </summary>
    [InitializeOnLoad]
    public static class TmpEssentialsAutoImporter
    {
        private const string PendingKey = "Codely.TmpEssentials.Pending";
        private const int MaxAttempts = 30; // ~30 editor ticks waiting for TMP to compile/load

        private static int _attempts;

        static TmpEssentialsAutoImporter()
        {
            if (EditorAutomationGuard.ShouldSkipBridgeAutoStart()) return;

            // Only re-arm across the post-install domain reload if a tool install
            // requested it. No-op for every other editor load.
            if (SessionState.GetBool(PendingKey, false))
                EditorApplication.delayCall += TryImport;
        }

        /// <summary>
        /// Called by <c>ManagePackage</c> right after a tool-initiated install of
        /// com.unity.textmeshpro succeeds. Marks the import pending (so it survives
        /// the domain reload UPM triggers) and attempts it on the next editor tick.
        /// </summary>
        public static void ScheduleImport()
        {
            SessionState.SetBool(PendingKey, true);
            _attempts = 0;
            EditorApplication.delayCall += TryImport;
        }

        private static void TryImport()
        {
            try
            {
                // The TMP editor assembly only exists after the post-install
                // recompile. If it isn't loaded yet, retry on a later tick.
                Type importerType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("TMPro.TMP_PackageResourceImporter"))
                    .FirstOrDefault(t => t != null);
                if (importerType == null)
                {
                    if (++_attempts <= MaxAttempts)
                    {
                        EditorApplication.delayCall += TryImport;
                    }
                    else
                    {
                        SessionState.EraseBool(PendingKey);
                        CodelyLogger.LogWarning("[TmpEssentialsAutoImporter] TMP assembly never loaded; giving up.");
                    }
                    return;
                }

                // Essentials already present -> nothing to import; just make sure no
                // window lingers. This is the exact check TMP itself uses (see
                // TMP_PackageResourceImporter.OnGUI).
                if (File.Exists("Assets/TextMesh Pro/Resources/TMP Settings.asset"))
                {
                    CloseImporterWindowIfOpen();
                    SessionState.EraseBool(PendingKey);
                    return;
                }

                // TMPro.TMP_PackageResourceImporter.ImportResources is a PUBLIC STATIC
                // method (verified in com.unity.textmeshpro 3.0.10):
                //   public static void ImportResources(bool importEssentials,
                //                                      bool importExamples,
                //                                      bool interactive)
                // interactive:false => imports the "TMP Essential Resources"
                // unitypackage silently, with no dialog. (Older TMP exposed a
                // 2-arg / instance variant, so we stay tolerant of both.)
                MethodInfo method = importerType.GetMethod(
                    "ImportResources",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (method == null)
                {
                    SessionState.EraseBool(PendingKey);
                    CodelyLogger.LogWarning("[TmpEssentialsAutoImporter] TMP_PackageResourceImporter.ImportResources not found.");
                    return;
                }

                object[] args = method.GetParameters().Length >= 3
                    ? new object[] { true, false, false }  // essentials, examples, interactive
                    : new object[] { true, false };
                object target = method.IsStatic
                    ? null
                    : Activator.CreateInstance(importerType, nonPublic: true);
                method.Invoke(target, args);

                CloseImporterWindowIfOpen();
                SessionState.EraseBool(PendingKey);
                CodelyLogger.Log("[TmpEssentialsAutoImporter] Imported TMP essential resources silently.");
            }
            catch (Exception e)
            {
                SessionState.EraseBool(PendingKey);
                CodelyLogger.LogWarning($"[TmpEssentialsAutoImporter] Auto-import failed: {e.Message}");
            }
        }

        /// <summary>
        /// Safety net: if TMP's own InitializeOnLoad popped the importer window
        /// before our silent import, close it so it can't block.
        /// </summary>
        private static void CloseImporterWindowIfOpen()
        {
            Type windowType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("TMPro.TMP_PackageResourceImporterWindow"))
                .FirstOrDefault(t => t != null);
            if (windowType == null)
                return;

            foreach (EditorWindow w in Resources.FindObjectsOfTypeAll(windowType).OfType<EditorWindow>())
                w.Close();
        }
    }
}
