using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    public static class ServerPathResolver
    {
        /// <summary>
        /// Attempts to locate the package root directory for cn.tuanjie.codely.bridge.
        /// Returns true if found and sets packagePath to the package root folder.
        /// </summary>
        public static bool TryFindPackageRoot(out string packagePath, bool warnOnLegacyPackageId = true)
        {
            // Resolve via local package info (no network). Fall back to Client.List on older editors.
            try
            {
#if UNITY_2021_2_OR_NEWER
                // Primary: the package that owns this assembly
                var owner = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ServerPathResolver).Assembly);
                if (owner != null)
                {
                    if (TryResolvePackage(owner, out packagePath, warnOnLegacyPackageId))
                    {
                        return true;
                    }
                }

                // Secondary: scan all registered packages locally
                foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (TryResolvePackage(p, out packagePath, warnOnLegacyPackageId))
                    {
                        return true;
                    }
                }
#else
                // Older Unity versions: use Package Manager Client.List as a fallback
                var list = UnityEditor.PackageManager.Client.List();
                while (!list.IsCompleted) { }
                if (list.Status == UnityEditor.PackageManager.StatusCode.Success)
                {
                    foreach (var pkg in list.Result)
                    {
                        if (TryResolvePackage(pkg, out packagePath, warnOnLegacyPackageId))
                        {
                            return true;
                        }
                    }
                }
#endif
            }
            catch { /* ignore */ }

            packagePath = null;
            return false;
        }

        private static bool TryResolvePackage(UnityEditor.PackageManager.PackageInfo p, out string packagePath, bool warnOnLegacyPackageId)
        {
            const string CurrentId = "cn.tuanjie.codely.bridge";

            packagePath = null;
            if (p == null || p.name != CurrentId)
            {
                return false;
            }

            packagePath = p.resolvedPath;
            return !string.IsNullOrEmpty(packagePath) && Directory.Exists(packagePath);
        }
    }
}


