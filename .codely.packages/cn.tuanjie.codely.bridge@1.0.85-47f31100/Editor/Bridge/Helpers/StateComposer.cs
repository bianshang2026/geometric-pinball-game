using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
#if UNITY_2021_2_OR_NEWER
using UnityEditor.Build;
#endif
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Centralized state composition for Unity Editor state.
    /// State is always computed fresh from the live editor, and only when a
    /// client explicitly asks for it (manage_editor.get_state). There is no
    /// revision counter, dirty tracking, delta generation, or staleness
    /// notification.
    /// </summary>
    public static class StateComposer
    {
        // Console state tracking (shared with ReadConsole)
        private static int _consoleUnreadCount = 0;
        private static readonly List<object> _lastConsoleErrors = new List<object>();
        private static readonly object _consoleLock = new object();

        // Touched assets tracking
        private static readonly List<object> _touchedAssets = new List<object>();
        private static readonly object _assetsLock = new object();

        /// <summary>
        /// Builds a complete, up-to-date Unity state snapshot.
        /// </summary>
        public static object BuildFullState()
        {
            var state = new
            {
                editor = BuildEditorState(),
                project = BuildProjectState(),
                scene = BuildSceneState(),
                selection = BuildSelectionState(),
                console = BuildConsoleState(),
                assets = BuildAssetsState(),
                policy = BuildPolicyState()
            };

            return state;
        }

        /// <summary>
        /// Builds editor-specific state.
        /// </summary>
        public static object BuildEditorState()
        {
            var playMode = EditorApplication.isPlaying ? "playing" :
                          (EditorApplication.isPaused ? "paused" : "stopped");

            // Get focused window
            string focusedWindow = null;
            if (EditorWindow.focusedWindow != null)
            {
                focusedWindow = EditorWindow.focusedWindow.GetType().Name;
            }

            // Determine if operations require focus
            // This is a heuristic - some operations need the editor to be focused
            bool requiresFocusForOperations = DetermineIfFocusRequired();

            return new
            {
                playMode = playMode,
                focusedWindow = focusedWindow,
                requiresFocusForOperations = requiresFocusForOperations,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                lastCompilation = BuildLastCompilationState(),
                timeSinceStartup = (float)EditorApplication.timeSinceStartup
            };
        }

        /// <summary>
        /// Builds last compilation state.
        ///
        /// NOTE:
        /// - This is intentionally minimal and only reports whether Unity is
        ///   currently compiling ("started" vs "idle").
        /// - It is NOT a per-compilation snapshot and does NOT expose error/
        ///   warning counts for any specific pipeline.
        /// - For accurate diagnostics (including error/warning counts), callers
        ///   must use the start_compilation_pipeline response data and the
        ///   Unity console (read_console / unity_console).
        /// </summary>
        private static object BuildLastCompilationState()
        {
            var status = EditorApplication.isCompiling ? "started" : "idle";

            return new
            {
                status = status
            };
        }

        /// <summary>
        /// Determines if current operations require focus.
        /// </summary>
        private static bool DetermineIfFocusRequired()
        {
            // Heuristic: Some operations need focus, especially during Play mode
            // or when performing visual operations like scene manipulation
            if (EditorApplication.isPlaying || EditorApplication.isPaused)
            {
                return true;
            }

            // Check if SceneView needs focus for certain operations
            var sceneView = EditorWindow.focusedWindow as SceneView;
            if (sceneView != null)
            {
                return false; // Already focused
            }

            return false; // Default: focus not strictly required
        }

        /// <summary>
        /// Builds project-specific state.
        /// </summary>
        public static object BuildProjectState()
        {
            // Detect Render Pipeline
            string srp = "builtin";
            var currentRP = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (currentRP != null)
            {
                string rpName = currentRP.GetType().Name.ToLowerInvariant();
                if (rpName.Contains("urp") || rpName.Contains("universal"))
                {
                    srp = "urp";
                }
                else if (rpName.Contains("hdrp") || rpName.Contains("highdefinition"))
                {
                    srp = "hdrp";
                }
            }

            return new
            {
                srp = srp,
                defineSymbols = GetScriptingDefineSymbols(),
                packages = GetInstalledPackages(),
                dirty = false // Would track if project settings are modified
            };
        }

        private static string[] GetScriptingDefineSymbols()
        {
            // Get scripting define symbols for current build target
            var buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
#if UNITY_2021_2_OR_NEWER
            var symbols = PlayerSettings.GetScriptingDefineSymbols(
                NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup));
#else
            var symbols = PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup);
#endif
            return string.IsNullOrEmpty(symbols) ?
                new string[0] :
                symbols.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string[] GetInstalledPackages()
        {
            // Simplified - in production would use PackageManager API
            return new string[0];
        }

        /// <summary>
        /// Builds scene-specific state.
        /// </summary>
        public static object BuildSceneState()
        {
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            return new
            {
                activeScenePath = activeScene.path,
                dirty = activeScene.isDirty,
                hasNavMeshData = HasNavMeshData(),
                hasLightingData = HasLightingData()
            };
        }

        private static bool HasNavMeshData()
        {
            // Check if current scene has NavMesh data using runtime reflection
            try
            {
                // First, try to check NavMeshSurface components (com.unity.ai.navigation package)
                Type navMeshSurfaceType = Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
                if (navMeshSurfaceType == null)
                {
                    // Fallback: search in loaded assemblies
                    foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        navMeshSurfaceType = assembly.GetType("Unity.AI.Navigation.NavMeshSurface");
                        if (navMeshSurfaceType != null) break;
                    }
                }

                if (navMeshSurfaceType != null)
                {
                    // Check NavMeshSurface components for navMeshData
                    var activeSurfacesProperty = navMeshSurfaceType.GetProperty("activeSurfaces", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (activeSurfacesProperty != null)
                    {
                        var activeSurfaces = activeSurfacesProperty.GetValue(null);
                        if (activeSurfaces is System.Collections.IList surfaceList && surfaceList.Count > 0)
                        {
                            var navMeshDataProperty = navMeshSurfaceType.GetProperty("navMeshData");
                            if (navMeshDataProperty != null)
                            {
                                foreach (var surface in surfaceList)
                                {
                                    if (surface != null)
                                    {
                                        var navMeshData = navMeshDataProperty.GetValue(surface);
                                        if (navMeshData != null)
                                        {
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Also check all NavMeshSurface components in the scene (including inactive)
                    var allSurfaces = Resources.FindObjectsOfTypeAll(navMeshSurfaceType);
                    if (allSurfaces != null && allSurfaces.Length > 0)
                    {
                        var navMeshDataProperty = navMeshSurfaceType.GetProperty("navMeshData");
                        if (navMeshDataProperty != null)
                        {
                            foreach (var surface in allSurfaces)
                            {
                                if (surface != null)
                                {
                                    var navMeshData = navMeshDataProperty.GetValue(surface);
                                    if (navMeshData != null)
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }

                // Fallback: Try to find NavMesh type using reflection (for built-in NavMesh)
                Type navMeshType = Type.GetType("UnityEngine.AI.NavMesh, UnityEngine.AIModule");
                if (navMeshType == null)
                {
                    // Fallback: search in loaded assemblies
                    foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        navMeshType = assembly.GetType("UnityEngine.AI.NavMesh");
                        if (navMeshType != null) break;
                    }
                }

                if (navMeshType == null)
                    return false;

                // Get CalculateTriangulation method
                MethodInfo calculateTriangulationMethod = navMeshType.GetMethod("CalculateTriangulation", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (calculateTriangulationMethod == null)
                    return false;

                // Call CalculateTriangulation using reflection
                var triangulation = calculateTriangulationMethod.Invoke(null, null);
                if (triangulation == null)
                    return false;

                // Get vertices property
                var verticesProperty = triangulation.GetType().GetProperty("vertices");
                if (verticesProperty == null)
                    return false;

                var vertices = verticesProperty.GetValue(triangulation) as Array;
                return vertices != null && vertices.Length > 0;
            }
            catch
            {
                // If any error occurs, assume no NavMesh data
                return false;
            }
        }

        private static bool HasLightingData()
        {
            // Check if current scene has baked lighting
            return Lightmapping.lightingDataAsset != null;
        }

        /// <summary>
        /// Builds selection state.
        /// </summary>
        public static object BuildSelectionState()
        {
            var activeObject = Selection.activeGameObject;
            object activeObjectInfo = null;

            if (activeObject != null)
            {
                activeObjectInfo = new
                {
                    id = activeObject.GetStableInstanceId(),
                    name = activeObject.name,
                    hierarchy_path = GetHierarchyPath(activeObject)
                };
            }

            return new
            {
                activeObject = activeObjectInfo
            };
        }

        private static string GetHierarchyPath(GameObject go)
        {
            if (go == null) return "";

            var path = go.name;
            var parent = go.transform.parent;

            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        /// <summary>
        /// Builds console state with real tracking data.
        /// </summary>
        public static object BuildConsoleState()
        {
            lock (_consoleLock)
            {
                return new
                {
                    unreadCount = _consoleUnreadCount,
                    lastErrors = _lastConsoleErrors.ToArray()
                };
            }
        }

        /// <summary>
        /// Updates console state tracking. Called by ReadConsole.
        /// </summary>
        public static void UpdateConsoleState(int unreadCount = 0, object[] lastErrors = null)
        {
            lock (_consoleLock)
            {
                _consoleUnreadCount = unreadCount;
                _lastConsoleErrors.Clear();
                if (lastErrors != null)
                {
                    _lastConsoleErrors.AddRange(lastErrors);
                }
            }
        }

        /// <summary>
        /// Builds assets state with tracked touched assets.
        /// </summary>
        public static object BuildAssetsState()
        {
            lock (_assetsLock)
            {
                return new
                {
                    touched = _touchedAssets.ToArray()
                };
            }
        }

        /// <summary>
        /// Adds a touched asset to tracking. Called by asset operations.
        /// </summary>
        public static void AddTouchedAsset(string path, bool imported = false, bool hasMeta = true)
        {
            lock (_assetsLock)
            {
                _touchedAssets.Add(new { path, imported, hasMeta });
                // Keep only last 100 entries
                while (_touchedAssets.Count > 100)
                {
                    _touchedAssets.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Clears touched assets list.
        /// </summary>
        public static void ClearTouchedAssets()
        {
            lock (_assetsLock)
            {
                _touchedAssets.Clear();
            }
        }

        /// <summary>
        /// Builds policy state.
        /// </summary>
        public static object BuildPolicyState()
        {
            return new
            {
                writeGuardInPlayMode = WriteGuard.GetPolicyString(),
                // UnityStateDirtyHook coalesces reimports onto EditorApplication.delayCall
                // (see ProcessPendingNotifications), so refresh is still batched.
                refreshMode = "debounced",
                consoleReadPolicy = "clear_then_read"
            };
        }
    }
}
