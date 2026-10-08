using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// [EXPERIMENTAL] Handles baking operations (NavMesh, Lighting, etc.).
    /// Compatible with Unity 2022.3 LTS.
    /// </summary>
    public static class ManageBake
    {
        // Runtime check for AI Navigation package availability
        private static bool? _hasAINavigation = null;
        private static Type _navMeshSurfaceType = null;
        private static MethodInfo _buildNavMeshMethod = null;
        private static MethodInfo _updateNavMeshMethod = null;
        private static PropertyInfo _activeSurfacesProperty = null;
        private static Type _navMeshType = null;
        private static MethodInfo _calculateTriangulationMethod = null;
        private static MethodInfo _removeAllNavMeshDataMethod = null;

        /// <summary>
        /// Starts a bake-related <see cref="StepJob"/> on the current request; the response is held
        /// until the job finishes. Lighting bakes run for minutes, and any script edit in that
        /// window reloads the domain — which used to strand the bake's update callback and leave
        /// its job Pending forever.
        /// </summary>
        private static object RunToCompletion(StepJob job, JObject @params, int defaultTimeoutSeconds)
            => StepJobRunner.Start(
                CommandContext.RequestId, CommandContext.CommandType, job,
                @params["timeoutSeconds"]?.ToObject<int?>() ?? defaultTimeoutSeconds);

        /// <summary>
        /// True when a NavMesh has actually been produced. Checks <c>navMeshData</c> on the given
        /// surfaces (or on every active surface when <paramref name="surfaces"/> is null), then
        /// falls back to the global triangulation.
        /// </summary>
        internal static bool HasAnyNavMeshData(IList surfaces = null)
        {
            try
            {
                var navMeshDataProperty = _navMeshSurfaceType?.GetProperty("navMeshData");
                if (navMeshDataProperty != null)
                {
                    var toCheck = surfaces ?? _activeSurfacesProperty?.GetValue(null) as IList;
                    if (toCheck != null)
                    {
                        foreach (var surface in toCheck)
                        {
                            if (surface != null && navMeshDataProperty.GetValue(surface) != null)
                                return true;
                        }
                    }
                }

                if (_calculateTriangulationMethod != null)
                {
                    var triangulation = _calculateTriangulationMethod.Invoke(null, null);
                    var vertices = triangulation?.GetType()
                        .GetProperty("vertices")?.GetValue(triangulation) as Array;
                    return vertices != null && vertices.Length > 0;
                }
            }
            catch
            {
                // Reflection against an optional package — if we cannot tell, report no data
                // rather than claiming a bake produced something.
            }
            return false;
        }

        /// <summary>
        /// Runs a lighting bake to completion. <c>Lightmapping.BakeAsync</c> does not reload the domain
        /// itself, but a bake runs for minutes and any script edit in that window does — which used to
        /// strand the bake's update callback and leave its job Pending forever. Progress is read from
        /// <c>Lightmapping.isRunning</c>, editor-global state that is still true after a reload, so the
        /// job simply picks the wait back up.
        /// </summary>
        internal class BakeLightingJob : StepJob
        {
            public bool Started;

            /// <summary>
            /// How long to let the bake start before concluding it finished (or never began).
            /// Wall-clock rather than a frame count: Lightmapping.isRunning goes true only once
            /// Unity has finished preparing the scene, which is far more than a handful of frames
            /// on a real scene. Giving up early reports "baking completed" for a bake that never
            /// ran, against whatever lighting data happened to be on disk already.
            /// </summary>
            public double SettleSeconds = 30;

            /// <summary>
            /// Session-clock deadline for the detect phase, pushed forward for as long as the
            /// editor is updating so the window only counts down against an idle editor.
            /// </summary>
            public double SettleUntil;

            public bool SawRunning;

            protected override JobStep[] BuildSteps() => new[]
            {
                new JobStep("start-bake", () =>
                {
                    if (Started) return;
                    Started = true;
                    Lightmapping.BakeAsync();
                }),

                // Do not report "done" in the gap between requesting the bake and Unity starting it.
                new JobStep("detect-bake",
                    () =>
                    {
                        // The window does not count down while the editor is still refreshing —
                        // see SettleWindow.
                        SettleUntil = SettleWindow.Advance(
                            SettleUntil, EditorApplication.timeSinceStartup, SettleSeconds,
                            EditorApplication.isUpdating);
                        if (Lightmapping.isRunning) SawRunning = true;
                    },
                    () => SawRunning
                          || SettleWindow.Expired(SettleUntil, EditorApplication.timeSinceStartup,
                                                  EditorApplication.isUpdating)),

                new JobStep("await-bake", null, () => !Lightmapping.isRunning),

                new JobStep("report", () => Complete(Response.Success(
                    SawRunning
                        ? "[EXPERIMENTAL] Lighting baking completed"
                        : "[EXPERIMENTAL] No lighting bake was observed — Unity never started one. " +
                          "Any lighting data reported below predates this call.",
                    new Dictionary<string, object>
                    {
                        ["type"] = "lighting",
                        ["hasLightingData"] = Lightmapping.lightingDataAsset != null,
                        ["bake_observed"] = SawRunning,
                    }))),
            };
        }

        /// <summary>
        /// Waits out the asynchronous <c>UpdateNavMesh</c> operations started by
        /// <c>bake_navmesh</c>. The operations themselves cannot be persisted, so if a domain
        /// reload takes them the job falls back to waiting for the editor to settle and then
        /// reports on the NavMesh data that actually exists.
        /// </summary>
        internal class NavMeshBakeJob : StepJob
        {
            public int SurfaceCount;

            [JsonIgnore] public List<AsyncOperation> Operations;

            /// <summary>
            /// True once the operations have been lost to a domain reload. An idle editor says
            /// nothing about whether they finished, so from here the baked data is the only
            /// evidence there is.
            /// </summary>
            public bool OperationsLost;

            public override void OnRestored() => OperationsLost = true;

            protected override JobStep[] BuildSteps() => new[]
            {
                new JobStep("await-surfaces", null, () =>
                    Operations == null
                        ? !EditorApplication.isCompiling && !EditorApplication.isUpdating
                        : Operations.All(op => op == null || op.isDone)),

                new JobStep("report", Report),
            };

            internal void Report()
            {
                bool hasData = HasAnyNavMeshData();

                // Waiting for the editor to go idle only proves the editor is idle. Without the
                // operations to ask, unbaked surfaces mean the bake either never finished or
                // produced nothing, and "completed" would be a guess.
                if (OperationsLost && !hasData)
                {
                    Fail("[EXPERIMENTAL] NavMesh baking could not be confirmed: a domain reload " +
                         "took the bake operations and no NavMesh data is present. Check the " +
                         "NavMesh surfaces and re-bake if needed.");
                    return;
                }

                Complete(Response.Success(
                    "[EXPERIMENTAL] NavMesh baking completed",
                    new Dictionary<string, object>
                    {
                        ["type"] = "navmesh",
                        ["hasNavMeshData"] = hasData,
                        ["surfacesBaked"] = SurfaceCount,
                        // True when the outcome rests on the baked data alone, the bake operations
                        // themselves having been lost to a domain reload.
                        ["verified_after_reload"] = OperationsLost,
                    }));
            }
        }

        /// <summary>
        /// Reset the AI Navigation package cache. Call this after installing the package
        /// to force re-checking for available types.
        /// </summary>
        private static void ResetAINavigationCache()
        {
            _hasAINavigation = null;
            _navMeshSurfaceType = null;
            _buildNavMeshMethod = null;
            _updateNavMeshMethod = null;
            _activeSurfacesProperty = null;
            _navMeshType = null;
            _calculateTriangulationMethod = null;
            _removeAllNavMeshDataMethod = null;
        }

        private static bool HasAINavigation()
        {
            if (_hasAINavigation.HasValue)
                return _hasAINavigation.Value;

            try
            {
                // First, check if the package is installed via PackageManager
                bool packageInstalled = false;
                try
                {
#if UNITY_2021_2_OR_NEWER
                    // Use GetAllRegisteredPackages for Unity 2021.2+
                    var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                    packageInstalled = packages.Any(p => p.name == "com.unity.ai.navigation");
#else
                    // Fallback for older Unity versions
                    var listRequest = Client.List(true, false);
                    while (!listRequest.IsCompleted)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                    if (listRequest.Status == StatusCode.Success)
                    {
                        packageInstalled = listRequest.Result.Any(p => p.name == "com.unity.ai.navigation");
                    }
#endif
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"[ManageBake] Error checking package installation: {ex.Message}");
                    // Continue with type checking as fallback
                }

                // Try to find NavMeshSurface type (Unity.AI.Navigation namespace from com.unity.ai.navigation package)
                // Try multiple methods to find the type
                _navMeshSurfaceType = Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
                
                if (_navMeshSurfaceType == null)
                {
                    // Try with full assembly qualified name variations
                    _navMeshSurfaceType = Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
                }
                
                if (_navMeshSurfaceType == null)
                {
                    // Fallback: search in loaded assemblies by name first
                    System.Reflection.Assembly targetAssembly = null;
                    foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var assemblyName = assembly.GetName().Name;
                        if (assemblyName == "Unity.AI.Navigation" || assemblyName.Contains("Unity.AI.Navigation"))
                        {
                            targetAssembly = assembly;
                            break;
                        }
                    }
                    
                    if (targetAssembly != null)
                    {
                        _navMeshSurfaceType = targetAssembly.GetType("Unity.AI.Navigation.NavMeshSurface");
                    }
                }
                
                if (_navMeshSurfaceType == null)
                {
                    // Last resort: search all assemblies
                    foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _navMeshSurfaceType = assembly.GetType("Unity.AI.Navigation.NavMeshSurface");
                        if (_navMeshSurfaceType != null) break;
                    }
                }

                if (_navMeshSurfaceType != null)
                {
                    _buildNavMeshMethod = _navMeshSurfaceType.GetMethod("BuildNavMesh", BindingFlags.Public | BindingFlags.Instance);
                    _updateNavMeshMethod = _navMeshSurfaceType.GetMethod("UpdateNavMesh", BindingFlags.Public | BindingFlags.Instance);
                    _activeSurfacesProperty = _navMeshSurfaceType.GetProperty("activeSurfaces", BindingFlags.Public | BindingFlags.Static);
                }

                // Try to find NavMesh type (UnityEngine.AI namespace - still used by the package)
                _navMeshType = Type.GetType("UnityEngine.AI.NavMesh, UnityEngine.AIModule");
                if (_navMeshType == null)
                {
                    foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _navMeshType = assembly.GetType("UnityEngine.AI.NavMesh");
                        if (_navMeshType != null) break;
                    }
                }

                if (_navMeshType != null)
                {
                    _calculateTriangulationMethod = _navMeshType.GetMethod("CalculateTriangulation", BindingFlags.Public | BindingFlags.Static);
                    _removeAllNavMeshDataMethod = _navMeshType.GetMethod("RemoveAllNavMeshData", BindingFlags.Public | BindingFlags.Static);
                }

                // Check both package installation and required types/methods
                bool hasRequiredTypes = _navMeshSurfaceType != null && _buildNavMeshMethod != null && _navMeshType != null;

                // If package is installed but types are missing, check compilation status
                if (packageInstalled && !hasRequiredTypes)
                {
                    bool isCompiling = EditorApplication.isCompiling;
                    string compilationStatus = isCompiling ? "compiling" : "idle";
                    
                    // Collect diagnostic information
                    var loadedAssemblies = System.AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a => a.GetName().Name.Contains("AI") || a.GetName().Name.Contains("Navigation"))
                        .Select(a => a.GetName().Name)
                        .ToList();
                    
                    string diagnosticInfo = "";
                    if (loadedAssemblies.Count > 0)
                    {
                        diagnosticInfo = $" Found related assemblies: {string.Join(", ", loadedAssemblies)}.";
                    }
                    else
                    {
                        diagnosticInfo = " No AI/Navigation assemblies found in loaded assemblies.";
                    }
                    
                    string typeStatus = "";
                    if (_navMeshSurfaceType == null)
                    {
                        typeStatus += " NavMeshSurface type not found.";
                    }
                    else
                    {
                        typeStatus += $" NavMeshSurface found, but methods missing: BuildNavMesh={_buildNavMeshMethod != null}, UpdateNavMesh={_updateNavMeshMethod != null}, activeSurfaces={_activeSurfacesProperty != null}.";
                    }
                    
                    if (_navMeshType == null)
                    {
                        typeStatus += " NavMesh type not found.";
                    }
                    
                    CodelyLogger.LogWarning(
                        $"[ManageBake] com.unity.ai.navigation package is installed but required types/methods are not available. " +
                        $"Editor is currently {compilationStatus}.{diagnosticInfo}{typeStatus} " +
                        (isCompiling 
                            ? "Please wait for compilation to complete, then call 'unity_editor { \"action\": \"wait_for_idle\" }' before retrying."
                            : "The package may need to be reloaded. Try restarting Unity or wait a moment and retry.")
                    );
                }

                // Package installation check is primary, but we also need the types to be available
                // If package is installed but types are missing and we're not compiling, return false
                // If we're compiling, also return false (types won't be available until compilation completes)
                _hasAINavigation = packageInstalled && hasRequiredTypes && !EditorApplication.isCompiling;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[ManageBake] Error checking for AI Navigation package: {ex.Message}");
                _hasAINavigation = false;
            }

            return _hasAINavigation.Value;
        }

        private static readonly Dictionary<string, Func<JObject, object>> ActionHandlers =
            new Dictionary<string, Func<JObject, object>>
            {
                { "bake_navmesh", BakeNavMesh },
                { "bake_lighting", p => RunToCompletion(new BakeLightingJob(), p, 600) },
                { "wait_for_bake", WaitActionDeprecated },
                { "clear_navmesh", _ => ClearNavMesh() },
                { "clear_baked_data", _ => ClearBakedData() },
            };

        public static object HandleCommand(JObject @params)
            => ActionRouter.Route(@params, ActionHandlers);

        private static object WaitActionDeprecated(JObject @params)
        {
            string action = @params["action"]?.ToString()?.ToLower();
            return Response.ErrorWithCode(
                "action_deprecated",
                $"Action '{action}' is deprecated. There is no need to wait: bake_lighting and " +
                "bake_navmesh return only after the bake is done. To wait for the editor to " +
                "settle after external work, use 'unity_editor { \"action\": \"wait_for_idle\" }'.");
        }

        private static object BakeNavMesh(JObject @params)
        {
            try
            {
                // Reset cache and re-check if first check fails (in case package was just installed)
                if (!HasAINavigation())
                {
                    ResetAINavigationCache();
                    if (!HasAINavigation())
                    {
                        // Check if package is installed but types are not available
                        bool packageInstalled = false;
                        try
                        {
#if UNITY_2021_2_OR_NEWER
                            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                            packageInstalled = packages.Any(p => p.name == "com.unity.ai.navigation");
#else
                            var listRequest = Client.List(true, false);
                            while (!listRequest.IsCompleted)
                            {
                                System.Threading.Thread.Sleep(50);
                            }
                            if (listRequest.Status == StatusCode.Success)
                            {
                                packageInstalled = listRequest.Result.Any(p => p.name == "com.unity.ai.navigation");
                            }
#endif
                        }
                        catch { }
                        
                        bool isCompiling = EditorApplication.isCompiling;
                        
                        string errorMessage;
                        if (packageInstalled && isCompiling)
                        {
                            errorMessage = 
                                "[EXPERIMENTAL] NavMesh baking requires AI Navigation package types to be loaded. " +
                                "The package is installed but Unity is currently compiling. " +
                                "Please wait for compilation to complete by calling 'unity_editor { \"action\": \"wait_for_idle\" }', then retry.";
                        }
                        else if (packageInstalled)
                        {
                            errorMessage = 
                                "[EXPERIMENTAL] NavMesh baking requires AI Navigation package types to be loaded. " +
                                "The package 'com.unity.ai.navigation' is installed but required types are not available. " +
                                "This may happen if: (1) compilation is in progress, (2) the package needs to be reloaded, or (3) Unity needs to be restarted. " +
                                "Try: (1) Call 'unity_editor { \"action\": \"wait_for_idle\" }' to ensure compilation is complete, " +
                                "(2) Wait a few seconds and retry, or (3) Restart Unity.";
                        }
                        else
                        {
                            errorMessage = 
                                "[EXPERIMENTAL] NavMesh baking requires AI Navigation package. " +
                                "Install 'com.unity.ai.navigation' via Package Manager using: " +
                                "'unity_package { \"action\": \"install_package\", \"id_or_url\": \"com.unity.ai.navigation\" }', " +
                                "then wait for installation and compilation to complete using 'unity_editor { \"action\": \"wait_for_idle\" }'.";
                        }
                        
                        return Response.Error(errorMessage);
                    }
                }

                // Get all active NavMeshSurface components in the scene
                List<object> surfaces = new List<object>();
                if (_activeSurfacesProperty != null)
                {
                    var activeSurfaces = _activeSurfacesProperty.GetValue(null);
                    if (activeSurfaces is System.Collections.IList surfaceList)
                    {
                        foreach (var surface in surfaceList)
                        {
                            surfaces.Add(surface);
                        }
                    }
                }

                if (surfaces.Count == 0)
                {
                    // Fallback: find all NavMeshSurface components using Resources.FindObjectsOfTypeAll
                    if (_navMeshSurfaceType != null)
                    {
                        var allObjects = Resources.FindObjectsOfTypeAll(_navMeshSurfaceType);
                        foreach (var obj in allObjects)
                        {
                            if (obj != null)
                            {
                                surfaces.Add(obj);
                            }
                        }
                    }
                }

                if (surfaces.Count == 0)
                {
                    return Response.Error("[EXPERIMENTAL] No NavMeshSurface components found in the scene. Add a NavMeshSurface component to a GameObject to bake NavMesh.");
                }

                // Check if we should use async baking (UpdateNavMesh) or sync baking (BuildNavMesh)
                bool useAsync = @params["async"]?.ToObject<bool?>() ?? false;
                List<AsyncOperation> asyncOps = new List<AsyncOperation>();

                if (useAsync && _updateNavMeshMethod != null)
                {
                    // Use async UpdateNavMesh for each surface that has existing data
                    foreach (var surface in surfaces)
                    {
                        try
                        {
                            var navMeshDataProperty = _navMeshSurfaceType.GetProperty("navMeshData");
                            if (navMeshDataProperty != null)
                            {
                                var navMeshData = navMeshDataProperty.GetValue(surface);
                                if (navMeshData != null)
                                {
                                    var asyncOp = _updateNavMeshMethod.Invoke(surface, new object[] { navMeshData }) as AsyncOperation;
                                    if (asyncOp != null)
                                    {
                                        asyncOps.Add(asyncOp);
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // If UpdateNavMesh fails, fall back to BuildNavMesh
                        }
                    }
                }

                // If no async operations were started, use synchronous BuildNavMesh, which has
                // already finished by the time it returns.
                if (asyncOps.Count == 0)
                {
                    foreach (var surface in surfaces)
                    {
                        _buildNavMeshMethod?.Invoke(surface, null);
                    }

                    return Response.Success("[EXPERIMENTAL] NavMesh baking completed", new
                    {
                        type = "navmesh",
                        hasNavMeshData = HasAnyNavMeshData(surfaces),
                        surfacesBaked = surfaces.Count,
                    });
                }

                // Async surfaces: run to completion instead of answering "pending" and leaving the
                // client to poll a callback that would not survive an incidental domain reload.
                return RunToCompletion(
                    new NavMeshBakeJob { Operations = asyncOps, SurfaceCount = surfaces.Count },
                    @params, 600);
            }
            catch (Exception e)
            {
                return Response.Error($"[EXPERIMENTAL] Failed to start NavMesh baking: {e.Message}");
            }
        }

        private static object ClearNavMesh()
        {
            try
            {
                if (!HasAINavigation())
                {
                    return Response.Error("[EXPERIMENTAL] NavMesh operations require AI Navigation package.");
                }

                // Clear NavMesh using reflection - try RemoveAllNavMeshData first
                int clearedCount = 0;
                if (_removeAllNavMeshDataMethod != null)
                {
                    _removeAllNavMeshDataMethod.Invoke(null, null);
                    clearedCount++;
                }
                
                // Also clear all NavMeshSurface components
                if (_activeSurfacesProperty != null)
                {
                    var activeSurfaces = _activeSurfacesProperty.GetValue(null);
                    if (activeSurfaces is System.Collections.IList surfaceList)
                    {
                        var removeDataMethod = _navMeshSurfaceType.GetMethod("RemoveData", BindingFlags.Public | BindingFlags.Instance);
                        foreach (var surface in surfaceList)
                        {
                            try
                            {
                                removeDataMethod?.Invoke(surface, null);
                                clearedCount++;
                            }
                            catch { }
                        }
                    }
                }
                

                return Response.Success($"[EXPERIMENTAL] NavMesh data cleared ({clearedCount} surfaces).");
            }
            catch (Exception e)
            {
                return Response.Error($"[EXPERIMENTAL] Failed to clear NavMesh: {e.Message}");
            }
        }

        private static object ClearBakedData()
        {
            try
            {
                Lightmapping.Clear();

                return Response.Success("[EXPERIMENTAL] Baked lighting data cleared.");
            }
            catch (Exception e)
            {
                return Response.Error($"[EXPERIMENTAL] Failed to clear baked data: {e.Message}");
            }
        }
    }
}

