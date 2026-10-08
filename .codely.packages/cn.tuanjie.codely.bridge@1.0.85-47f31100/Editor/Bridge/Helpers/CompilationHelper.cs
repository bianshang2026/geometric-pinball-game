using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Helper class for Unity compilation status checking and error tracking
    /// </summary>
    public static class CompilationHelper
    {
        // Track last known compilation error/warning counts
        // IMPORTANT: Keep these nullable. Returning 0 when counts are unknown is misleading
        // (it can be interpreted as "validated: no errors/warnings").
        private static int? _lastErrorCount = null;
        private static int? _lastWarningCount = null;

        /// <summary>
        /// Helper to check compilation status across Unity versions
        /// </summary>
        public static bool IsCompiling()
        {
            if (EditorApplication.isCompiling)
            {
                return true;
            }
            try
            {
                System.Type pipeline = System.Type.GetType("UnityEditor.Compilation.CompilationPipeline, UnityEditor");
                var prop = pipeline?.GetProperty("isCompiling", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (prop != null)
                {
                    return (bool)prop.GetValue(null);
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Gets the count of compilation errors from the console.
        /// This is an approximation based on console log entries.
        /// </summary>
        public static int? GetCompilationErrors()
        {
            try
            {
                // Try to get error count from LogEntries (internal API)
                var logEntriesType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntriesType != null)
                {
                    var getCountMethod = logEntriesType.GetMethod(
                        "GetCount",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    );
                    
                    // Get count with error filter (mode = 1 for errors)
                    var getCountByTypeMethod = logEntriesType.GetMethod(
                        "GetCountsByType",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    );

                    if (getCountByTypeMethod != null)
                    {
                        // GetCountsByType(ref int errorCount, ref int warningCount, ref int logCount)
                        object[] args = new object[] { 0, 0, 0 };
                        getCountByTypeMethod.Invoke(null, args);
                        _lastErrorCount = (int)args[0]; // Errors
                        return _lastErrorCount;
                    }
                }
            }
            catch (System.Exception e)
            {
                CodelyLogger.LogWarning($"[CompilationHelper] Failed to get error count: {e.Message}");
            }

            return _lastErrorCount;
        }

        /// <summary>
        /// Gets the count of compilation warnings from the console.
        /// This is an approximation based on console log entries.
        /// </summary>
        public static int? GetCompilationWarnings()
        {
            try
            {
                // Try to get warning count from LogEntries (internal API)
                var logEntriesType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntriesType != null)
                {
                    var getCountByTypeMethod = logEntriesType.GetMethod(
                        "GetCountsByType",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    );

                    if (getCountByTypeMethod != null)
                    {
                        // GetCountsByType(ref int errorCount, ref int warningCount, ref int logCount)
                        object[] args = new object[] { 0, 0, 0 };
                        getCountByTypeMethod.Invoke(null, args);
                        _lastWarningCount = (int)args[1]; // Warnings
                        return _lastWarningCount;
                    }
                }
            }
            catch (System.Exception e)
            {
                CodelyLogger.LogWarning($"[CompilationHelper] Failed to get warning count: {e.Message}");
            }

            return _lastWarningCount;
        }

        /// <summary>
        /// Resets tracked error/warning counts.
        /// Should be called before starting a new compilation.
        /// </summary>
        public static void ResetCounts()
        {
            _lastErrorCount = null;
            _lastWarningCount = null;
        }

        /// <summary>
        /// Requests a script compilation after auto-saving any dirty scenes.
        /// This is the single entry point that should be used instead of
        /// <c>CompilationPipeline.RequestScriptCompilation()</c> everywhere in
        /// the bridge, to prevent Unity's native "Save scene?" modal dialog
        /// from blocking the main thread during domain reload.
        /// </summary>
        public static void RequestScriptCompilationSafe()
        {
            EnsureScenesSavedBeforeReload();
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
        }

        /// <summary>
        /// Auto-saves any dirty scenes before a domain reload (triggered by
        /// RequestScriptCompilation) to prevent Unity's native "Save scene?"
        /// modal dialog from blocking the main thread.
        ///
        /// - Dirty **named** scenes (have a path) are saved silently to their
        ///   existing file location.
        /// - Dirty **unnamed** scenes (no path) are saved to
        ///   "Assets/Temp/CodelyAutosave/UnnamedScene_{timestamp}.unity" so
        ///   the dialog is never triggered. The autosave path is logged.
        /// </summary>
        internal static void EnsureScenesSavedBeforeReload()
        {
            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isDirty)
                    continue;

                if (!string.IsNullOrEmpty(scene.path))
                {
                    // Named scene — save silently to its existing path
                    bool ok = EditorSceneManager.SaveScene(scene);
                    CodelyLogger.Log(
                        $"[CompilationHelper] Auto-saved dirty scene '{scene.name}' → '{scene.path}' (ok={ok}) before compile");
                }
                else
                {
                    // Unnamed scene — save to a temp autosave path to avoid the
                    // blocking "Save As" file dialog that hangs the editor
                    string dir = "Assets/Temp/CodelyAutosave";
                    string sceneName = !string.IsNullOrEmpty(scene.name) ? scene.name : "Untitled";
                    string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string relPath = $"{dir}/{sceneName}_{timestamp}.unity";

                    // SaveScene fails if the target folder does not exist yet — create it
                    // first. Directory.CreateDirectory is a no-op if it already exists, and
                    // "Assets/..." resolves against Unity's project-root working directory.
                    System.IO.Directory.CreateDirectory(dir);

                    bool ok = EditorSceneManager.SaveScene(scene, relPath);
                    if (ok)
                    {
                        CodelyLogger.LogWarning(
                            $"[CompilationHelper] Unnamed dirty scene auto-saved to '{relPath}' before compile. " +
                            "Move or delete this file if not needed.");
                    }
                    else
                    {
                        CodelyLogger.LogError(
                            $"[CompilationHelper] Failed to auto-save unnamed dirty scene before compile. " +
                            "Domain reload may trigger a blocking dialog.");
                    }
                }
            }
        }
        
        /// <summary>
        /// Starts a standard compilation pipeline:
        /// 1. Clears console so a post-compile read returns only this build's output
        /// 2. Requests compilation
        ///
        /// This is the recommended pattern after any script modification.
        /// </summary>
        /// <param name="clearCache">
        /// When true, attempts to clear the script assembly cache before compiling so that
        /// stale intermediate build artefacts are discarded.  Uses
        /// <c>RequestScriptCompilationOptions.CleanBuildCache</c> (Unity 2019.3+) via
        /// reflection; falls back to a normal recompile when that API is unavailable.
        /// </param>
        public static object StartCompilationPipeline(bool clearCache = false)
        {
            try
            {
                // Do not allow script compilation while in Play/Paused mode; recompiling during
                // play mode is unsafe and Unity cannot cleanly reload the domain.
                if (EditorApplication.isPlaying || EditorApplication.isPaused)
                {
                    return Response.Error(
                        "compile_blocked_in_play_mode",
                        new
                        {
                            code = "compile_blocked_in_play_mode",
                            message = "Compilation is not allowed while the editor is in Play/Paused mode. Stop Play mode (unity_editor.stop) before requesting compilation.",
                            playMode = EditorApplication.isPlaying ? "playing" : "paused"
                        }
                    );
                }

                // Step 1: Clear console so a post-compile read returns only this build's output
                UnityTcp.Editor.Tools.ReadConsole.HandleCommand(new Codely.Newtonsoft.Json.Linq.JObject
                {
                    ["action"] = "clear"
                });

                // Step 2: Reset error counts
                ResetCounts();
                
                string jobMessage = clearCache
                    ? "Script compilation pipeline started (clean cache)"
                    : "Script compilation pipeline started";

                // Auto-save dirty scenes before requesting compilation to prevent
                // Unity's "Save scene?" modal dialog from blocking the main thread.
                // Also covers the cache-clear reflection path below.
                EnsureScenesSavedBeforeReload();

                // Step 4: Detect externally-written/modified files before compiling.
                // Files created via raw file writes (not the bridge's script tools, which call
                // AssetDatabase.ImportAsset directly) are only picked up by Unity's AUTOMATIC
                // refresh, which is gated on the editor having focus. When the editor runs
                // unfocused/headless (agent-driven), those new .cs never import and therefore
                // never compile. An explicit Refresh() forces detection + import regardless of
                // focus (and regardless of the "Auto Refresh" preference), so RequestScriptCompilation
                // then has the new scripts to compile.
                AssetDatabase.Refresh();

                // Step 5: Request compilation (optionally with cache clear).
                //
                // AssetDatabase.Refresh() is synchronous and, when it detects new/modified .cs
                // files on disk, it starts script compilation (Bee) ITSELF. Issuing our own
                // RequestScriptCompilation() on top of an already-running compile re-enters the
                // pipeline and races Bee — the request can be dropped or corrupt the in-flight
                // build. So only force a compile when Refresh() did NOT already start one.
                //
                // The explicit request is still required in the other path: bridge script tools
                // import via AssetDatabase.ImportAsset directly, so there is no on-disk change for
                // Refresh() to detect and it will not trigger a compile on its own.
                bool cacheClearedViaFlag = false;
                bool alreadyCompiling = IsCompiling();
                if (!alreadyCompiling)
                {
                    if (clearCache)
                    {
                        // Try RequestScriptCompilationOptions.CleanBuildCache (Unity 2019.3+) via
                        // reflection so this code remains compatible with older Unity versions.
                        try
                        {
                            var optionsType = System.Type.GetType(
                                "UnityEditor.Compilation.RequestScriptCompilationOptions, UnityEditor");
                            if (optionsType != null)
                            {
                                object cleanCacheValue = System.Enum.Parse(optionsType, "CleanBuildCache");
                                var requestWithOptions = typeof(UnityEditor.Compilation.CompilationPipeline).GetMethod(
                                    "RequestScriptCompilation",
                                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                                    null,
                                    new[] { optionsType },
                                    null);
                                if (requestWithOptions != null)
                                {
                                    requestWithOptions.Invoke(null, new[] { cleanCacheValue });
                                    cacheClearedViaFlag = true;
                                }
                            }
                        }
                        catch (System.Exception ex)
                        {
                            CodelyLogger.LogWarning($"[CompilationHelper] CleanBuildCache not available on this Unity version: {ex.Message}");
                        }
                    }

                    if (!cacheClearedViaFlag)
                    {
                        RequestScriptCompilationSafe();
                    }
                }

                // Step 6: Report what the caller needs to finish the job. This response is
                // internal — CompilePipelineJob consumes it and then waits for the compile — so it
                // carries no operation id; the tracker job above exists only to drive
                // OnCompilationFinished and the editor-state snapshot.
                var response = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["success"] = true,
                    ["message"] = jobMessage,
                    ["pipeline_kind"] = "compile",
                };
                if (clearCache) response["cache_cleared"] = cacheClearedViaFlag;

                return response;
            }
            catch (System.Exception e)
            {
                CodelyLogger.LogError($"[CompilationHelper] StartCompilationPipeline failed: {e}");
                return Response.Error($"Failed to start compilation pipeline: {e.Message}");
            }
        }
        
        /// <summary>
        /// Gets a summary of the last compilation result.
        /// </summary>
        public static object GetCompilationSummary()
        {
            var errors = GetCompilationErrors();
            var warnings = GetCompilationWarnings();

            // Only include fields that are actually known; returning 0 is misleading.
            var result = new System.Collections.Generic.Dictionary<string, object>
            {
                ["isCompiling"] = IsCompiling()
            };

            if (errors.HasValue) result["errors"] = errors.Value;
            if (warnings.HasValue) result["warnings"] = warnings.Value;
            if (errors.HasValue) result["success"] = errors.Value == 0;

            return result;
        }
    }
}
