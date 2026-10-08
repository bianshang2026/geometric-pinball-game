using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

using UnityTcp.Editor.Helpers;
namespace Cn.Tuanjie.Codely.Editor
{
    [InitializeOnLoad]
    internal static class CodelyLayoutInstaller
    {
        private const string LayoutFileName    = "Tuanjie AI Mode.wlt";
        private const string OldLayoutFileName = "Codely Mode.wlt";
        private const string PackageLayoutAssetPath = "Packages/cn.tuanjie.codely.bridge/Editor/Layout/" + LayoutFileName;

        static CodelyLayoutInstaller()
        {
            if (EditorProcessGuard.ShouldSkipEditorUiAutoStart()) return;

            EditorApplication.delayCall += InstallCodelyLayout;
        }

        /// <summary>
        /// Removes the legacy "Codely Mode.wlt" entry from the user's layouts directory
        /// so it does not show up alongside the new "Tuanjie AI Mode" in the Window > Layouts menu.
        /// </summary>
        private static void RemoveLegacyLayout(string layoutsDir)
        {
            if (string.IsNullOrEmpty(layoutsDir)) return;
            var legacy = Path.Combine(layoutsDir, OldLayoutFileName);
            if (!File.Exists(legacy)) return;
            try { File.Delete(legacy); }
            catch (Exception e) { CodelyLogger.LogWarning($"[Codely] Could not remove legacy layout '{legacy}': {e.Message}"); }
        }

        private static void InstallCodelyLayout()
        {
            var assetPath = ResolveLayoutAssetPath();
            if (string.IsNullOrEmpty(assetPath))
            {
                CodelyLogger.LogWarning($"[Codely] Could not resolve {LayoutFileName} (check package path or asset import).");
                return;
            }

            var srcFull = ToAbsoluteProjectPath(assetPath);
            if (string.IsNullOrEmpty(srcFull) || !File.Exists(srcFull))
            {
                CodelyLogger.LogWarning($"[Codely] {LayoutFileName} not found on disk for asset path \"{assetPath}\" (resolved: {srcFull ?? "(null)"}).");
                return;
            }

            var layoutsDir = GetLayoutsModePreferencesPath();
            if (string.IsNullOrEmpty(layoutsDir))
            {
                CodelyLogger.LogWarning("[Codely] Could not get editor layouts path.");
                return;
            }

            if (!Directory.Exists(layoutsDir))
            {
                CodelyLogger.LogWarning($"[Codely] Layouts directory does not exist: {layoutsDir}");
                return;
            }

            var dstFull = Path.Combine(layoutsDir, LayoutFileName);
            try
            {
                File.Copy(srcFull, dstFull, true);
                RemoveLegacyLayout(layoutsDir);
                ReloadWindowLayoutMenu();

                // Only auto-switch when the Editor is still sitting on the legacy "Codely Mode"
                // layout — otherwise reloading would wipe window customisations on every
                // domain reload.
                if (IsLayoutActive(LegacyLayoutNames[0]) || IsLayoutActive(LegacyLayoutNames[1]))
                {
                    ApplyCodelyLayout();

                }
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[Codely] Could not install Tuanjie AI Layout: {e.Message}");
            }
        }

        // Legacy values of `m_LastLoadedLayoutName` to migrate from (no .wlt extension).
        private static readonly string[] LegacyLayoutNames = { "Codely Mode", "Codely" };

        /// <summary>
        /// Returns <c>true</c> when Unity's active layout snapshot records
        /// <c>m_LastLoadedLayoutName</c> as one of the legacy Codely names. The snapshot
        /// lives at <c>WindowLayout.GetCurrentLayoutPath()</c>, which resolves to
        /// <c>&lt;UserPrefs&gt;\Layouts\CurrentLayout-&lt;mode&gt;.dwlt</c> (or
        /// <c>CurrentMaximizeLayout.dwlt</c>) — NOT under the project's Library/.
        /// </summary>
        private static bool IsLayoutActive(string layoutName)
        {
            try
            {
                var dwltPath = GetCurrentLayoutDwltPath();
                if (string.IsNullOrEmpty(dwltPath) || !File.Exists(dwltPath))
                    return false;

                using (var reader = new StreamReader(dwltPath))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var trimmed = line.TrimStart();
                        if (!trimmed.StartsWith("m_LastLoadedLayoutName:", StringComparison.Ordinal))
                            continue;
                        var colon = trimmed.IndexOf(':');
                        if (colon < 0) continue;
                        var name = trimmed.Substring(colon + 1).Trim();
                        if (string.Equals(name, layoutName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[Codely] IsLayoutActive failed: {e.Message}");
            }
            return false;
        }

        /// <summary>
        /// Resolves the absolute path of Unity's currently-active layout snapshot
        /// (<c>CurrentLayout-&lt;mode&gt;.dwlt</c>). Prefers
        /// <c>UnityEditor.WindowLayout.GetCurrentLayoutPath()</c>; falls back to scanning
        /// <c>WindowLayout.layoutsPreferencesPath</c> (the user-prefs Layouts root) for the
        /// freshest matching dwlt so it works on Unity versions that lack the method.
        /// </summary>
        private static string GetCurrentLayoutDwltPath()
        {
            var windowLayoutType = typeof(EditorWindow).Assembly.GetType("UnityEditor.WindowLayout");
            if (windowLayoutType == null) return null;

            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

            // 1) Direct API when present (Unity 2020.2+).
            var method = windowLayoutType.GetMethod("GetCurrentLayoutPath", flags, null, Type.EmptyTypes, null);
            if (method != null)
            {
                try
                {
                    var path = method.Invoke(null, null) as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        return path;
                }
                catch { /* fall through to manual probe */ }
            }

            // 2) Scan the user-prefs Layouts root for the freshest CurrentLayout*.dwlt.
            var layoutsRootProp = windowLayoutType.GetProperty("layoutsPreferencesPath", flags);
            var layoutsRoot     = layoutsRootProp?.GetValue(null) as string;
            if (string.IsNullOrEmpty(layoutsRoot) || !Directory.Exists(layoutsRoot))
                return null;

            string freshest   = null;
            DateTime newest   = DateTime.MinValue;
            foreach (var p in Directory.GetFiles(layoutsRoot, "CurrentLayout*.dwlt", SearchOption.TopDirectoryOnly))
            {
                var m = File.GetLastWriteTimeUtc(p);
                if (m > newest) { newest = m; freshest = p; }
            }
            return freshest;
        }

        internal static void RemoveInstalledLayout()
        {
            var layoutsDir = GetLayoutsModePreferencesPath();
            if (string.IsNullOrEmpty(layoutsDir))
                return;

            var dstFull = Path.Combine(layoutsDir, LayoutFileName);
            if (!File.Exists(dstFull))
                return;

            try
            {
                File.Delete(dstFull);
                ReloadWindowLayoutMenu();
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[Codely] Could not remove installed layout: {e.Message}");
            }
        }

        /// <summary>
        /// Switches the Editor to the Codely layout. Re-installs the .wlt from the package
        /// if it's missing from the user's layouts directory. Returns true on success.
        /// Note: loading a layout destroys all current EditorWindows.
        /// </summary>
        internal static bool ApplyCodelyLayout()
        {
            if (IsLayoutActive("Tuanjie AI Mode") || IsLayoutActive("Tuanjie AI"))
            {
                return true;
            }

            var layoutsDir = GetLayoutsModePreferencesPath();
            if (string.IsNullOrEmpty(layoutsDir))
            {
                CodelyLogger.LogWarning("[Codely] ApplyCodelyLayout: could not resolve layouts directory.");
                return false;
            }

            var dstFull = Path.Combine(layoutsDir, LayoutFileName);
            if (!File.Exists(dstFull))
            {
                InstallCodelyLayout();
                if (!File.Exists(dstFull))
                {
                    CodelyLogger.LogWarning($"[Codely] ApplyCodelyLayout: layout file missing after install attempt: {dstFull}");
                    return false;
                }
            }

            try
            {
                return LoadWindowLayoutFile(dstFull);
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[Codely] ApplyCodelyLayout failed: {e.Message}");
                return false;
            }
        }

        private static bool LoadWindowLayoutFile(string fullPath)
        {
            var windowLayoutType = typeof(EditorWindow).Assembly.GetType("UnityEditor.WindowLayout");
            if (windowLayoutType == null)
                return false;

            var signature = new[] { typeof(string), typeof(bool) };
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            // Prefer TryLoadWindowLayout when present (newer Unity); fall back to LoadWindowLayout.
            var method = windowLayoutType.GetMethod("TryLoadWindowLayout", flags, null, signature, null)
                      ?? windowLayoutType.GetMethod("LoadWindowLayout",    flags, null, signature, null);

            if (method == null)
            {
                CodelyLogger.LogWarning("[Codely] LoadWindowLayoutFile: UnityEditor.WindowLayout.LoadWindowLayout not found.");
                return false;
            }

            var result = method.Invoke(null, new object[] { fullPath, false });
            return result is bool ok ? ok : true;
        }

        private static string ResolveLayoutAssetPath()
        {
            if (File.Exists(ToAbsoluteProjectPath(PackageLayoutAssetPath)))
                return PackageLayoutAssetPath;

            foreach (var guid in AssetDatabase.FindAssets(LayoutFileName))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("/" + LayoutFileName, StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("\\" + LayoutFileName, StringComparison.OrdinalIgnoreCase))
                    return path;
            }

            return null;
        }

        private static string ToAbsoluteProjectPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return null;

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                return null;

            return Path.GetFullPath(Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string GetLayoutsModePreferencesPath()
        {
            var windowLayoutType = typeof(EditorWindow).Assembly.GetType("UnityEditor.WindowLayout");
            if (windowLayoutType == null)
                return null;

            var property = windowLayoutType.GetProperty(
                "layoutsModePreferencesPath",
                BindingFlags.Static | BindingFlags.NonPublic);

            return property?.GetValue(null) as string;
        }

        private static void ReloadWindowLayoutMenu()
        {
            var windowLayoutType = typeof(EditorWindow).Assembly.GetType("UnityEditor.WindowLayout");
            if (windowLayoutType == null)
                return;

            var method = windowLayoutType.GetMethod(
                "ReloadWindowLayoutMenu",
                BindingFlags.Static | BindingFlags.NonPublic);
            method?.Invoke(null, null);

            var updateMenusMethod = typeof(EditorUtility).GetMethod(
                "Internal_UpdateAllMenus",
                BindingFlags.Static | BindingFlags.NonPublic);
            updateMenusMethod?.Invoke(null, null);
        }
    }
}
