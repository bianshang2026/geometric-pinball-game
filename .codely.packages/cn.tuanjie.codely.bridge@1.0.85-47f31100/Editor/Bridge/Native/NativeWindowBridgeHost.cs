using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

using UnityTcp.Editor.Helpers;
namespace UnityTcp.Editor.Native
{
    /// <summary>
    /// High-level managed host for NativeWindowBridge.
    /// Handles availability probing, domain reload safety (start-or-attach),
    /// and UTF-8 buffer management for native JSON exchange.
    /// Follows the same pattern as NativeTcpBridgeHost.
    /// </summary>
    internal static partial class NativeWindowBridgeHost
    {
        // Verbose logging toggle for streaming-related diagnostics.
        // When true, all [NWB-*] logs are printed. When false, only
        // errors and critical state changes are printed.
        // Toggle at runtime via menu: Codely > Streaming > Toggle Verbose Log
        internal static bool s_OffscreenVerboseLog = true;

        /// <summary>
        /// Log only when verbose mode is enabled. Use for high-frequency
        /// or detailed diagnostic messages. Errors and critical state
        /// changes should use CodelyLogger.Log/LogWarning/LogError directly.
        /// </summary>
        private static void LogVerbose(string msg)
        {
            if (s_OffscreenVerboseLog)
                CodelyLogger.Log(msg);
        }

        private const int JsonBufferSize = 512 * 1024;

        private static readonly byte[] JsonBuffer = new byte[JsonBufferSize];

        private static bool availabilityChecked;
        private static bool available;
        private static string availabilityReason = "Native availability not checked yet.";
#if UNITY_EDITOR_WIN
        private static bool windowsDllSearchPathConfigured;
        private static bool windowsNativeDllLoaded;
#endif
#if UNITY_EDITOR_OSX
        private static bool macNativeDylibLoadAttempted;
        private static bool macNativeDylibLoaded;
        private static string macNativeDylibLoadDetail = "Not attempted.";
#endif

        // pollingRegistered is non-serialized; resets to false on domain reload.
        // This is intentional: EditorApplication.update subscriptions are also
        // cleared on reload, so we must re-register.
        private static bool pollingRegistered;

        // Called after domain reload to re-register polling if server is still running.
        [UnityEditor.InitializeOnLoadMethod]
        private static void OnDomainReload()
        {
            if (EditorAutomationGuard.ShouldSkipBridgeAutoStart()) return;

            EnsureReloadCleanupRegistered();

            // Listen for play mode changes to force-repaint GameView immediately
            // after exiting play mode, preventing a multi-second frame stall.
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            // Subscribe to Unity Progress API so we can forward compilation
            // status descriptions to the cowork frontend via DataChannel.
            RegisterCompilationProgressTracker();

            // Delay check to avoid calling into native too early.
            EditorApplication.delayCall += () =>
            {
                if (IsAvailable() && IsRunning())
                {
                    // Re-register log callback (nulled in OnBeforeAssemblyReload
                    // to prevent dangling C# delegate during reload).
                    RegisterNativeLogCallback();
                    RegisterOffscreenPolling();
                    TryRestoreOffscreenAfterReload();
                }
                // Notify browser that domain reload is complete so it can
                // exit the domain_reloading overlay immediately.
                SendDataChannelMessage("{\"type\":\"domain_reload_complete\"}");
                CodelyLogger.Log("[NWB-DC] Sent: {\"type\":\"domain_reload_complete\"}");
                // Defensive self-heal: if Unity was left in accessory policy
                // after an abnormal stream shutdown/reload, restore normal app visibility.
                EnsureMacEditorVisibilitySelfHeal();
            };
        }

        /// <summary>
        /// During play mode transitions Unity may internally re-layout, re-show, or
        /// re-activate windows, breaking the offscreen hiding state.  Re-apply
        /// transparency and deactivation on EVERY state change (including
        /// ExitingEditMode and ExitingPlayMode) to prevent the editor from
        /// flashing visible or staying visible after Stop.
        /// </summary>
        // Remaining extra repaints after play-mode transition. Decremented each
        // editor update frame so repaints spread across distinct frames instead of
        // batching in a single tick (EditorApplication.delayCall can coalesce).
        private static int s_PendingPlayModeRepaints;

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
#if UNITY_EDITOR_WIN
            // Even when offscreen is not active, we may need to restore InputSystem
            // device state after a prior streaming session disabled devices via
            // OnFocusChanged(false). When the user enters Play mode without streaming,
            // Mouse.current may still have DisabledWhileInBackground flag set.
            if (!s_OffscreenActive && state == PlayModeStateChange.EnteredPlayMode)
            {
                EditorApplication.delayCall += () =>
                {
                    if (!EditorApplication.isPlaying) return;
                };
            }
#endif
            if (!s_OffscreenActive)
            {
#if UNITY_EDITOR_WIN
                // If a domain reload was requested while in Play mode (via
                // RecoverInputSystemAfterStreaming), execute it now that we've
                // exited Play mode.
                if (state == PlayModeStateChange.EnteredEditMode &&
                    SessionState.GetBool(s_PendingReloadAfterPlayKey, false))
                {
                    SessionState.SetBool(s_PendingReloadAfterPlayKey, false);
                    CodelyLogger.Log("[NWB-NewInputSystem] Executing pending domain reload after Play mode exit");
                    EditorApplication.delayCall += () => EditorUtility.RequestScriptReload();
                }
#endif
                return;
            }

#if UNITY_EDITOR_WIN
            s_WinPlayKeyLastFocusTime = 0;
            s_WinHeldKeys.Clear();
            ClearInputSystemMouseState();
#endif
#if UNITY_EDITOR_OSX
            s_MacPlayKeyboardActive = false;
            s_MacPlayKeyLastTime = 0;
            s_MacHeldKeys.Clear();
            s_MacHeldMouseButtons.Clear();
            s_MacKeyReinjectionFrames = 0;
#endif
            // Immediately re-hide on all transitions to prevent flash.
            ReapplyWindowTransparency();
            DeactivateUnityKeepWindows();

            // Composite mode: mirror Tuanjie's native behavior of auto-
            // activating GameView on Play and restoring previous tab on Stop.
            // Tuanjie does this via EditorApplicationLayout.InitPlaymodeLayout
            // → WindowLayout.ShowAppropriateViewOnEnterExitPlaymode → Focus().
            // Use SessionState because domain reload happens between
            // ExitingEditMode and EnteredPlayMode, wiping static fields.
            if (s_CompositeActive && s_CompositeSlots != null && s_CompositeSlots.Count > 1)
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                {
                    string activeType = GetCompositeActiveWindowTypeName();
                    if (!string.IsNullOrEmpty(activeType))
                    {
                        SessionState.SetString(kCompositePrePlayTabKey, activeType);
                        LogVerbose($"[NWB-Composite] Saved pre-play active tab: {activeType}");
                    }
                    ActivateCompositeGameViewTab();
                }
                else if (state == PlayModeStateChange.EnteredPlayMode)
                {
                    ActivateCompositeGameViewTab();
                }
                else if (state == PlayModeStateChange.EnteredEditMode)
                {
                    string savedType = SessionState.GetString(kCompositePrePlayTabKey, "");
                    SessionState.EraseString(kCompositePrePlayTabKey);
                    if (!string.IsNullOrEmpty(savedType))
                        RestoreCompositeTabByTypeName(savedType);
                }
            }

            if (state == PlayModeStateChange.EnteredEditMode ||
                state == PlayModeStateChange.EnteredPlayMode)
            {
                if (s_OffscreenTarget != null)
                {
                    s_OffscreenTarget.Repaint();
                    s_PendingPlayModeRepaints = 3;
                    EditorApplication.update += PlayModeRepaintTick;
                }
            }
        }

        private static void PlayModeRepaintTick()
        {
            if (s_PendingPlayModeRepaints <= 0 || !s_OffscreenActive || s_OffscreenTarget == null)
            {
                s_PendingPlayModeRepaints = 0;
                EditorApplication.update -= PlayModeRepaintTick;
                return;
            }
            s_PendingPlayModeRepaints--;
            s_OffscreenTarget.Repaint();
        }

        /// <summary>
        /// Called BEFORE assembly reload. The native C++ singleton (HTTP server,
        /// WebRTC session, encoder) survives domain reload because it lives in
        /// the unmanaged DLL address space. All HTTP handler callbacks are pure
        /// C++ lambdas capturing the native singleton — no C# delegates involved.
        /// So we keep the server running and only clean up C# side state.
        /// The only C# callback is the log callback, which must be nulled to
        /// prevent the native thread from calling a dangling managed delegate.
        /// </summary>
        private const string kOrphanedFloatingWindowKey = "NWB_OrphanedFloatingWindowId";
        private const string kWindowRectsSaveKey = "NWB_SavedWindowRects";
        private const string kClientDprKey = "NWB_ClientDPR";

        private static void OnBeforeAssemblyReload()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            try
            {
                CleanupBeforeAssemblyReload();
            }
            finally
            {
                // Window cleanup must not prevent releasing the native callback.
                // Clear native first; keep the delegate rooted if that call fails.
                try
                {
                    NativeWindowBridgeAPI.NWB_SetLogCallback(null);
                    s_NativeLogCallbackRegistered = false;
                    s_NativeLogCallback = null;
                }
                catch (Exception) { }
            }
#endif
        }

        private static void CleanupBeforeAssemblyReload()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            // Notify browser that domain reload is imminent
            SendDataChannelMessage("{\"type\":\"domain_reload\"}");
            CodelyLogger.Log("[NWB-DC] Sent: {\"type\":\"domain_reload\"}");

            // Unsubscribe from Progress events — compilation is done,
            // domain reload is starting. The domain_reload message
            // above already notifies the frontend to show the reload overlay.
            UnregisterCompilationProgressTracker();

            // Close floating capture window (single-view mode only)
            if (s_OffscreenAutoFloatingWindow)
            {
                if (s_OffscreenFloatingWindowInstanceId != 0)
                    SessionState.SetString(kOrphanedFloatingWindowKey, s_OffscreenFloatingWindowInstanceId.ToString());

                if (s_OffscreenTarget != null)
                {
                    try { s_OffscreenTarget.Close(); }
                    catch (Exception) { }
                }
            }

            // Composite mode: save slot mapping, request JSON, and release RTs,
            // but keep EditorWindows alive. DockArea serializes m_Panes/m_Selected,
            // so the active tab survives domain reload automatically.
            if (s_CompositeActive)
            {
                SaveCompositeSlotMapping();
                ReleaseCompositeRenderTargets();

                // Save composite request so TryRestoreOffscreenAfterReload can
                // auto-resume without waiting for the browser to re-send layout.
                if (!string.IsNullOrEmpty(s_LastCompositeRequestJson))
                {
                    SessionState.SetString(kCompositeRestoreJsonKey, s_LastCompositeRequestJson);
                    SessionState.SetInt(kCompositeRestoreFpsKey, s_OffscreenFps);
                }

                CodelyLogger.Log("[NWB] Released composite RTs before assembly reload (windows preserved)");
            }
            else if (s_CompositeRT != null || s_CompositePaneDragRT != null || s_CompositeReadbackTex != null)
            {
                ReleaseCompositeRenderTargets();
            }

            // Persist client DPR so capture scaling survives domain reload.
            SessionState.SetFloat(kClientDprKey, s_ClientDPR);

            // Persist closed-docked-GameView flag so restoration survives domain reload.
            SessionState.SetBool(kClosedDockedGameViewKey, s_ClosedDockedGameViewForStreaming);
            // Sibling type name is already saved in CloseExistingGameViewsForStreaming.

            // Drop popup/context-menu state without notifying the browser — the
            // data channel is about to be torn down and stale MenuController refs
            // must not survive into the next domain.
            ClearFrontendPopupState(notifyFrontend: false);

            // Reset C# tracking flags; will be restored after reload via
            // TryRestoreOffscreenAfterReload / PollOffscreenRequests.
            s_OffscreenActive = false;
            s_OffscreenLoopRegistered = false;

            // NOTE: Do NOT call NWB_StopServer or NWB_StopCapture here.
            // The native singleton keeps the HTTP server, WebRTC session,
            // and offscreen mode alive across domain reload. Benefits:
            //   - Port stays the same (no re-bind)
            //   - WebRTC connection persists (browser doesn't disconnect)
            //   - No deadlock risk (Stop() is never called on main thread)
            //   - TryRestoreOffscreenAfterReload detects active offscreen
            //     and auto-resumes frame pushing after reload completes
#endif
        }

        // Remaining re-hide passes after domain reload restore. Decremented each
        // editor update frame so passes spread across distinct frames.
        private static int s_PendingReloadRehidePasses;

        private static void ReloadRehideTick()
        {
            if (s_PendingReloadRehidePasses <= 0 || !s_OffscreenActive)
            {
                s_PendingReloadRehidePasses = 0;
                EditorApplication.update -= ReloadRehideTick;
                return;
            }
            s_PendingReloadRehidePasses--;
#if UNITY_EDITOR_WIN
            ReapplyWindowTransparency();
            DeactivateUnityKeepWindows();
#elif UNITY_EDITOR_OSX
            ReapplyWindowTransparency();
            DeactivateUnityKeepWindows();
#endif
        }

        // =====================================================================
        // Compilation progress tracking: subscribe to UnityEditor.Progress
        // events and forward compilation status to the cowork frontend via
        // DataChannel so the browser overlay can display live build info.
        // UnityEditor.Progress API is only available in Unity 2020+.
        // =====================================================================

        private static bool s_CompilationProgressRegistered;

#if UNITY_2020_2_OR_NEWER
        private static void RegisterCompilationProgressTracker()
        {
            if (s_CompilationProgressRegistered) return;
            s_CompilationProgressRegistered = true;
            Progress.added += OnCompilationProgressAdded;
            Progress.updated += OnCompilationProgressUpdated;
            Progress.removed += OnCompilationProgressRemoved;
            // Scan existing items in case compilation already started
            foreach (var item in Progress.EnumerateItems())
            {
                if (item.name == "Compiling Scripts" && item.running)
                {
                    CodelyLogger.Log($"[NWB-DC] Progress found existing: name=\"{item.name}\" description=\"{item.description}\"");
                    SendCompilationProgressMessage(item.description);
                }
            }
        }

        private static void UnregisterCompilationProgressTracker()
        {
            if (!s_CompilationProgressRegistered) return;
            s_CompilationProgressRegistered = false;
            Progress.added -= OnCompilationProgressAdded;
            Progress.updated -= OnCompilationProgressUpdated;
            Progress.removed -= OnCompilationProgressRemoved;
        }

        private static void OnCompilationProgressAdded(Progress.Item[] items)
        {
            foreach (var item in items)
            {
                if (item.name == "Compiling Scripts" && item.running)
                {
                    CodelyLogger.Log($"[NWB-DC] Progress.added: name=\"{item.name}\" description=\"{item.description}\"");
                    SendCompilationProgressMessage(item.description);
                    return;
                }
            }
        }

        private static void OnCompilationProgressUpdated(Progress.Item[] items)
        {
            foreach (var item in items)
            {
                if (item.name == "Compiling Scripts" && item.running)
                {
                    CodelyLogger.Log($"[NWB-DC] Progress.updated: name=\"{item.name}\" description=\"{item.description}\" progress={item.progress}");
                    SendCompilationProgressMessage(item.description);
                    return;
                }
            }
        }

        private static void OnCompilationProgressRemoved(Progress.Item[] items)
        {
            foreach (var item in items)
            {
                if (item.name == "Compiling Scripts")
                {
                    CodelyLogger.Log($"[NWB-DC] Progress.removed: name=\"{item.name}\" status={item.status}");
                    // Check for actual compilation errors via console log entries.
                    // Progress API always reports Succeeded even on compile failure.
                    int? errors = CompilationHelper.GetCompilationErrors();
                    bool hasErrors = errors.HasValue && errors.Value > 0;
                    string phase = hasErrors ? "failed" : "compile_done";
                    string json = "{\"type\":\"compilation_progress\",\"phase\":\"" + phase + "\"";
                    if (hasErrors)
                        json += ",\"errors\":" + errors.Value;
                    json += "}";
                    try { SendDataChannelMessage(json); } catch (Exception) { }
                    CodelyLogger.Log($"[NWB-DC] Sent: {json}");
                    return;
                }
            }
        }

        private static void SendCompilationProgressMessage(string description)
        {
            if (string.IsNullOrEmpty(description)) description = "";
            string json = "{\"type\":\"compilation_progress\",\"phase\":\"compiling\",\"description\":\""
                + EscapeJsonString(description) + "\"}";
            try { SendDataChannelMessage(json); } catch (Exception) { }
            CodelyLogger.Log($"[NWB-DC] Sent: {json}");
        }
#else // UNITY_2020_2_OR_NEWER
        // Unity 2019 fallback: no Progress API, use CompilationPipeline events
        private static void RegisterCompilationProgressTracker()
        {
            if (s_CompilationProgressRegistered) return;
            s_CompilationProgressRegistered = true;
            UnityEditor.Compilation.CompilationPipeline.compilationStarted += OnCompilationStarted_2019;
            UnityEditor.Compilation.CompilationPipeline.compilationFinished += OnCompilationFinished_2019;
        }

        private static void UnregisterCompilationProgressTracker()
        {
            if (!s_CompilationProgressRegistered) return;
            s_CompilationProgressRegistered = false;
            UnityEditor.Compilation.CompilationPipeline.compilationStarted -= OnCompilationStarted_2019;
            UnityEditor.Compilation.CompilationPipeline.compilationFinished -= OnCompilationFinished_2019;
        }

        private static void OnCompilationStarted_2019(object obj)
        {
            CodelyLogger.Log("[NWB-DC] CompilationPipeline.compilationStarted");
            string json = "{\"type\":\"compilation_progress\",\"phase\":\"compiling\",\"description\":\"\"}";
            try { SendDataChannelMessage(json); } catch (Exception) { }
            CodelyLogger.Log($"[NWB-DC] Sent: {json}");
        }

        private static void OnCompilationFinished_2019(object obj)
        {
            CodelyLogger.Log("[NWB-DC] CompilationPipeline.compilationFinished");
            int? errors = CompilationHelper.GetCompilationErrors();
            bool hasErrors = errors.HasValue && errors.Value > 0;
            string phase = hasErrors ? "failed" : "compile_done";
            string json = "{\"type\":\"compilation_progress\",\"phase\":\"" + phase + "\"";
            if (hasErrors)
                json += ",\"errors\":" + errors.Value;
            json += "}";
            try { SendDataChannelMessage(json); } catch (Exception) { }
            CodelyLogger.Log($"[NWB-DC] Sent: {json}");
        }

        private static void SendCompilationProgressMessage(string description)
        {
            // No-op on Unity 2019 — no Progress API to provide descriptions
        }
#endif

        /// <summary>
        /// Check if the C++ side was running an offscreen capture before domain reload.
        /// If so, auto-resume pushing frames so the browser stream recovers seamlessly.
        /// </summary>
        private static void TryRestoreOffscreenAfterReload()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            // Safety net: close any orphaned floating capture window that
            // survived the domain reload. OnBeforeAssemblyReload should
            // have closed it, but if that failed (exception, forced
            // reload, etc.), the saved instance ID lets us find it.
            long orphanId = long.TryParse(
                SessionState.GetString(kOrphanedFloatingWindowKey, "0"), out long parsedOrphanId)
                ? parsedOrphanId : 0L;
            if (orphanId != 0)
            {
                SessionState.SetString(kOrphanedFloatingWindowKey, "0");
                foreach (var win in Resources.FindObjectsOfTypeAll<EditorWindow>())
                {
                    if (win != null && win.GetStableInstanceId() == orphanId)
                    {
                        try
                        {
                            win.Close();
                            CodelyLogger.Log("[NWB] Closed orphaned floating capture window after reload");
                        }
                        catch (Exception) { }
                        break;
                    }
                }
            }

            try
            {
                // Restore client DPR so capture scaling continues after reload.
                s_ClientDPR = SessionState.GetFloat(kClientDprKey, 0f);
                SessionState.EraseFloat(kClientDprKey);

                // Restore closed-docked-GameView flag so StopOffscreenCapture
                // knows to reopen the GameView after streaming ends.
                s_ClosedDockedGameViewForStreaming = SessionState.GetBool(kClosedDockedGameViewKey, false);

                byte[] buf = new byte[256];
                int len = NativeWindowBridgeAPI.NWB_GetActiveOffscreenWindowType(buf, buf.Length);
                if (len <= 0) return;

                string windowType = System.Text.Encoding.UTF8.GetString(buf, 0, len);
                if (string.IsNullOrEmpty(windowType)) return;
                if (windowType == "__composite__")
                {
                    // Rediscover EditorWindows that survived domain reload
                    // via Unity serialization. Pre-populating s_CompositeSlots
                    // lets ApplyCompositeLayout reuse them instead of creating
                    // new windows (avoiding AddTab and tab-order issues).
                    PrePopulateCompositeSlots();

                    // Auto-resume composite streaming using saved request JSON.
                    // Without this, a deadlock occurs: C# waits for the browser
                    // to re-send the layout, while the browser's stall detector
                    // sees C++ heartbeats alive and never calls tryResumeOffscreen.
                    string savedJson = SessionState.GetString(kCompositeRestoreJsonKey, "");
                    int savedFps = SessionState.GetInt(kCompositeRestoreFpsKey, 30);
                    SessionState.EraseString(kCompositeRestoreJsonKey);
                    SessionState.EraseInt(kCompositeRestoreFpsKey);

                    if (!string.IsNullOrEmpty(savedJson))
                    {
                        CodelyLogger.Log("[NWB] Domain reload detected active composite offscreen; auto-resuming with saved layout...");
                        bool startOk = StartCompositeOffscreenCapture(savedJson, savedFps, 0, 0,
                            nativeAlreadyStarted: true, applyMode: CompositeLayoutApplyMode.PreserveExisting);
                        if (startOk)
                        {
                            CodelyLogger.Log("[NWB] Composite offscreen auto-resumed after domain reload");
                            s_PendingReloadRehidePasses = 5;
                            EditorApplication.update += ReloadRehideTick;
                        }
                        else
                        {
                            CodelyLogger.LogWarning("[NWB] Composite auto-resume failed, resetting native state");
                            ForceResetNativeOffscreenState();
                        }
                    }
                    else
                    {
                        CodelyLogger.Log("[NWB] Domain reload detected active composite offscreen; no saved layout, waiting for browser refresh.");
                    }
                    return;
                }

                // Only resume if C# is not already running offscreen (shouldn't be after reload).
                if (s_OffscreenActive) return;

                CodelyLogger.Log($"[NWB] Domain reload detected active offscreen: {windowType}, auto-resuming...");

                // Native offscreen mode is already active in C++ (it survived reload),
                // so pass nativeAlreadyStarted=true to skip re-initializing native side.
                bool ok = StartOffscreenCapture(windowType, fps: 30, width: 0, height: 0,
                                                nativeAlreadyStarted: true);
                if (ok)
                {
                    CodelyLogger.Log($"[NWB] Offscreen capture auto-resumed for {windowType} after domain reload");

                    // Play-mode entry can re-layout windows after MakeAllWindowsTransparent
                    // runs inside StartOffscreenCapture. Schedule extra re-hide passes over
                    // the next few frames to catch any late window position changes.
                    s_PendingReloadRehidePasses = 5;
                    EditorApplication.update += ReloadRehideTick;
                }
                else
                {
                    // C# restore failed but native may still think offscreen is active — reset
                    // native state so the browser can start a fresh stream instead of hanging.
                    CodelyLogger.LogWarning($"[NWB] Failed to auto-resume offscreen capture for {windowType}, resetting native offscreen");
                    ForceResetNativeOffscreenState();
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB] TryRestoreOffscreenAfterReload error: {ex.Message}");
                ForceResetNativeOffscreenState();
            }
#endif
        }

        public static bool IsSupportedPlatform =>
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            true;
#else
            false;
#endif

        public static string GetAvailabilityReason() => availabilityReason;

        /// <summary>
        /// Probe whether the native plugin is loadable. Cached after first success.
        /// </summary>
        public static bool IsAvailable(bool forceRefresh = false)
        {
            if (!IsSupportedPlatform)
            {
                availabilityChecked = true;
                available = false;
                availabilityReason = "Platform not supported by NativeWindowBridge.";
                return false;
            }

            if (availabilityChecked && (!forceRefresh || available))
            {
                return available;
            }

            availabilityChecked = true;
            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
#if UNITY_EDITOR_WIN
                EnsureWindowsDllSearchPath();
#elif UNITY_EDITOR_OSX
                EnsureMacDylibLoaded();
#endif
                _ = NativeWindowBridgeAPI.NWB_IsRunning();
                available = true;
                availabilityReason = "NativeWindowBridge library loaded successfully.";
#endif
            }
            catch (DllNotFoundException ex)
            {
                available = false;
                availabilityReason = $"DllNotFoundException: {ex.Message}";
#if UNITY_EDITOR_OSX
                if (macNativeDylibLoadAttempted)
                {
                    availabilityReason += $" | mac preload: {macNativeDylibLoadDetail}";
                }
#endif
            }
            catch (EntryPointNotFoundException ex)
            {
                available = false;
                availabilityReason = $"EntryPointNotFoundException: {ex.Message}";
#if UNITY_EDITOR_OSX
                if (macNativeDylibLoadAttempted)
                {
                    availabilityReason += $" | mac preload: {macNativeDylibLoadDetail}";
                }
#endif
            }
            catch (Exception ex)
            {
                available = false;
                availabilityReason = $"Unexpected: {ex.GetType().Name}: {ex.Message}";
                CodelyLogger.LogWarning($"[NWB] Availability check failed: {availabilityReason}");
            }
            return available;
        }

#if UNITY_EDITOR_WIN
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        /// <summary>
        /// Ensure Windows can resolve transitive native dependencies (e.g. datachannel.dll)
        /// when loading NativeWindowBridge.dll from the Unity package Plugins folder.
        /// After domain reload Mono P/Invoke will load a new DLL instance from the new
        /// package path. This is acceptable because OnBeforeAssemblyReload stops the old
        /// instance's server, making the new instance the sole authority.
        /// SetDllDirectory must still point to the correct folder so that transitive
        /// dependencies (datachannel.dll, libssl, libcrypto) can be resolved.
        /// </summary>
        private static void EnsureWindowsDllSearchPath()
        {
            if (windowsDllSearchPathConfigured)
            {
                return;
            }

            try
            {
                foreach (string folder in EnumerateNativeBridgeFolders())
                {
                    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                    {
                        continue;
                    }

                    string nativeBridgePath = Path.Combine(folder, "NativeWindowBridge.dll");
                    if (!File.Exists(nativeBridgePath))
                    {
                        continue;
                    }

                    string dependencyPath = Path.Combine(folder, "datachannel.dll");
                    if (!File.Exists(dependencyPath))
                    {
                        CodelyLogger.LogWarning($"[NWB] datachannel.dll not found next to NativeWindowBridge.dll: {dependencyPath}");
                    }

                    if (SetDllDirectory(folder))
                    {
                        windowsDllSearchPathConfigured = true;
                        CodelyLogger.Log($"[NWB] SetDllDirectory configured: {folder}");
                        EnsureWindowsNativeDllLoaded(nativeBridgePath, dependencyPath);
                        if (windowsNativeDllLoaded)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB] EnsureWindowsDllSearchPath failed: {ex.Message}");
            }

            CodelyLogger.LogWarning("[NWB] Could not configure Windows DLL search path for NativeWindowBridge.dll.");
        }

        private static void EnsureWindowsNativeDllLoaded(string nativeBridgePath, string dependencyPath)
        {
            if (windowsNativeDllLoaded)
            {
                return;
            }

            // Load dependency first to avoid transitive resolution failure.
            if (!string.IsNullOrEmpty(dependencyPath) && File.Exists(dependencyPath))
            {
                IntPtr dep = LoadLibrary(dependencyPath);
                if (dep == IntPtr.Zero)
                {
                    int depErr = Marshal.GetLastWin32Error();
                    CodelyLogger.LogWarning($"[NWB] LoadLibrary(datachannel.dll) failed. path={dependencyPath}, win32={depErr}");
                }
            }

            IntPtr lib = LoadLibrary(nativeBridgePath);
            if (lib == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                CodelyLogger.LogWarning($"[NWB] LoadLibrary(NativeWindowBridge.dll) failed. path={nativeBridgePath}, win32={err}");
                return;
            }

            windowsNativeDllLoaded = true;
            CodelyLogger.Log($"[NWB] LoadLibrary succeeded: {nativeBridgePath}");
        }

        private static IEnumerable<string> EnumerateNativeBridgeFolders()
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Find imported DLL assets via AssetDatabase.
            string[] guids = AssetDatabase.FindAssets("NativeWindowBridge t:DefaultAsset");
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith("NativeWindowBridge.dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string fullPath = Path.GetFullPath(assetPath);
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    folders.Add(dir);
                }
            }

            // Fallback: common package locations in the current Unity project.
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            folders.Add(Path.Combine(projectRoot, "Packages", "cn.tuanjie.codely.bridge", "Plugins", "Win"));

            string packageCacheRoot = Path.Combine(projectRoot, "Library", "PackageCache");
            if (Directory.Exists(packageCacheRoot))
            {
                foreach (string dir in Directory.GetDirectories(packageCacheRoot, "cn.tuanjie.codely.bridge*"))
                {
                    folders.Add(Path.Combine(dir, "Plugins", "Win"));
                }
            }

            return folders;
        }
#endif

#if UNITY_EDITOR_OSX
        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern IntPtr dlopen(string path, int mode);

        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern IntPtr dlerror();

        private const int RtldNow = 2;
        private const int RtldGlobal = 8;

        /// <summary>
        /// Ensure macOS can resolve transitive dylib dependencies on Intel/Apple Silicon.
        /// Some packaging flows produce bare-name linkage (e.g. libdatachannel.dylib),
        /// so we proactively dlopen dependencies from known package folders.
        /// </summary>
        private static void EnsureMacDylibLoaded()
        {
            if (macNativeDylibLoaded)
            {
                return;
            }

            macNativeDylibLoadAttempted = true;

            foreach (string folder in EnumerateMacNativeBridgeFolders())
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    continue;
                }

                string dependencyPath = Path.Combine(folder, "libdatachannel.dylib");
                string nativeBridgePath = Path.Combine(folder, "libNativeWindowBridge.dylib");
                if (!File.Exists(nativeBridgePath))
                {
                    continue;
                }

                if (!TryDlopenMac(dependencyPath, out string depError))
                {
                    CodelyLogger.LogWarning($"[NWB] macOS pre-load dependency failed: {depError}");
                }

                if (TryDlopenMac(nativeBridgePath, out string bridgeError))
                {
                    macNativeDylibLoaded = true;
                    macNativeDylibLoadDetail = $"Loaded from {nativeBridgePath}";
                    return;
                }

                macNativeDylibLoadDetail = bridgeError;
                CodelyLogger.LogWarning($"[NWB] macOS pre-load NativeWindowBridge failed: {bridgeError}");
            }

            if (!macNativeDylibLoaded)
            {
                CodelyLogger.LogWarning($"[NWB] macOS pre-load exhausted all candidate folders without success. detail={macNativeDylibLoadDetail}");
            }
        }

        private static bool TryDlopenMac(string path, out string error)
        {
            if (string.IsNullOrEmpty(path))
            {
                error = "Path is empty.";
                return false;
            }

            if (!File.Exists(path))
            {
                error = $"File not found: {path}";
                return false;
            }

            IntPtr handle = dlopen(path, RtldNow | RtldGlobal);
            if (handle != IntPtr.Zero)
            {
                error = string.Empty;
                return true;
            }

            string dlError = ReadMacDlError();
            error = $"dlopen failed: path={path}, detail={dlError}";
            return false;
        }

        private static string ReadMacDlError()
        {
            IntPtr ptr = dlerror();
            if (ptr == IntPtr.Zero)
            {
                return "unknown";
            }

            string msg = Marshal.PtrToStringAnsi(ptr);
            return string.IsNullOrEmpty(msg) ? "unknown" : msg;
        }

        private static IEnumerable<string> EnumerateMacNativeBridgeFolders()
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Find imported dylib assets via AssetDatabase.
            string[] guids = AssetDatabase.FindAssets("libNativeWindowBridge t:DefaultAsset");
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (
                    string.IsNullOrEmpty(assetPath)
                    || !assetPath.EndsWith("libNativeWindowBridge.dylib", StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                string fullPath = Path.GetFullPath(assetPath);
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    folders.Add(dir);
                }
            }

            // Fallback: common package locations in the current Unity project.
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            folders.Add(Path.Combine(projectRoot, "Packages", "cn.tuanjie.codely.bridge", "Plugins", "macOS"));

            string packageCacheRoot = Path.Combine(projectRoot, "Library", "PackageCache");
            if (Directory.Exists(packageCacheRoot))
            {
                foreach (string dir in Directory.GetDirectories(packageCacheRoot, "cn.tuanjie.codely.bridge*"))
                {
                    folders.Add(Path.Combine(dir, "Plugins", "macOS"));
                }
            }

            return folders;
        }
#endif

        public static bool IsRunning()
        {
            if (!IsAvailable()) return false;
            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
                return NativeWindowBridgeAPI.NWB_IsRunning() == 1;
#else
                return false;
#endif
            }
            catch { return false; }
        }

        /// <summary>
        /// Start or re-attach to the native HTTP streaming server.
        /// Idempotent: if already running, returns the existing bound port.
        /// </summary>
        public static bool StartOrAttach(int requestedPort, out int boundPort)
        {
            boundPort = 0;
            // Force-refresh availability here so a previous transient load failure
            // does not keep the stream server permanently unavailable.
            if (!IsAvailable(forceRefresh: true)) return false;
            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
                // Register log callback BEFORE any other native call so we capture
                // all C++ log output from this point forward.
                RegisterNativeLogCallback();

                int result = NativeWindowBridgeAPI.NWB_StartServer(requestedPort);
                if (result <= 0)
                {
                    availabilityReason = $"NWB_StartServer returned {result} for requested port {requestedPort}.";
                    return false;
                }
                boundPort = NativeWindowBridgeAPI.NWB_GetBoundPort();
                if (boundPort <= 0) boundPort = result;

                // Always register polling when the server is up. OnDomainReload may run
                // before AutoStartStreamServer, leaving polling unregistered until restart.
                RegisterOffscreenPolling();

                return true;
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                availabilityReason = $"StartOrAttach exception: {ex.GetType().Name}: {ex.Message}";
                CodelyLogger.LogError($"[NWB] Start failed: {ex.Message}");
                return false;
            }
        }

        // Static reference prevents the delegate from being garbage-collected while
        // the native DLL holds a pointer to it.
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
        private static NativeWindowBridgeAPI.NWB_LogCallbackDelegate s_NativeLogCallback;
        private static bool s_NativeLogCallbackRegistered;
#endif

        private static void RegisterNativeLogCallback()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            // Install cleanup before handing any delegate to native, including
            // explicit starts where OnDomainReload was skipped by the guard.
            EnsureReloadCleanupRegistered();
            if (s_NativeLogCallbackRegistered) return;
            try
            {
                s_NativeLogCallback = (level, message) =>
                {
                    if (level >= 2) CodelyLogger.LogError(message);
                    else if (level == 1) CodelyLogger.LogWarning(message);
                    else CodelyLogger.Log(message);
                };
                NativeWindowBridgeAPI.NWB_SetLogCallback(s_NativeLogCallback);
                s_NativeLogCallbackRegistered = true;
            }
            catch (EntryPointNotFoundException)
            {
                CodelyLogger.LogWarning("[NWB] NWB_SetLogCallback not found in native DLL — native logs will not appear in Unity Console");
            }
#endif
        }

        private static void EnsureReloadCleanupRegistered()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        /// <summary>
        /// Register EditorApplication.update polling for offscreen capture requests.
        /// </summary>
        private static void RegisterOffscreenPolling()
        {
            if (pollingRegistered) return;
            pollingRegistered = true;
            EditorApplication.update += PollOffscreenRequests;
            CodelyLogger.Log("[NWB] Offscreen polling registered on EditorApplication.update.");
        }

        /// <summary>
        /// Polled every editor frame. Checks if native has a pending offscreen start request.
        /// Runs on Unity main thread so all EditorWindow APIs are safe to call.
        /// </summary>
        private static readonly byte[] s_OffscreenWindowTypeBuf = new byte[256];
        private static readonly byte[] s_CompositeRequestBuf = new byte[512 * 1024];

        private static void PollOffscreenRequests()
        {
            if (!IsAvailable()) return;

#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            try
            {
                // Check for pending offscreen start request.
                // Always consume pending requests even when s_OffscreenActive is true.
                // When the user switches between Game View and Scene View, the native
                // server stores the new target type as a pending request. If we only
                // check when !s_OffscreenActive, the C# side never learns about the
                // new type and keeps pushing frames from the old window.
                int hasPending = NativeWindowBridgeAPI.NWB_GetPendingOffscreenRequest(
                    s_OffscreenWindowTypeBuf, s_OffscreenWindowTypeBuf.Length,
                    out int offFps, out int offW, out int offH, out float offDpr);

                if (hasPending == 1)
                {
                    int len = Array.IndexOf(s_OffscreenWindowTypeBuf, (byte)0);
                    if (len < 0) len = s_OffscreenWindowTypeBuf.Length;
                    string windowType = Encoding.UTF8.GetString(s_OffscreenWindowTypeBuf, 0, len);

                    if (offDpr > 0f) s_ClientDPR = offDpr;

                    if (s_OffscreenActive)
                    {
                        string currentType = s_OffscreenTarget != null ? s_OffscreenTarget.GetType().FullName : "(null)";
                        CodelyLogger.Log($"[NWB] Offscreen switch request while active: {currentType} → {windowType} fps={offFps} w={offW} h={offH}");
                    }
                    else
                    {
                        CodelyLogger.Log($"[NWB] Offscreen request: type={windowType} fps={offFps} w={offW} h={offH}");
                    }

                    // Always call NWB_StartOffscreenCapture from C# to ensure the
                    // native side activates offscreen mode. The HTTP handler only
                    // stores the pending request; activation requires this explicit call.
                    bool ok = StartOffscreenCapture(windowType, offFps, offW, offH, nativeAlreadyStarted: false);
                    if (!ok)
                    {
                        CodelyLogger.LogError($"[NWB] StartOffscreenCapture failed for type={windowType} fps={offFps} w={offW} h={offH}");
                    }
                }

                int hasCompositePending = NativeWindowBridgeAPI.NWB_GetPendingCompositeRequest(
                    s_CompositeRequestBuf, s_CompositeRequestBuf.Length,
                    out int compositeFps, out int compositeW, out int compositeH, out float compositeDpr);

                if (hasCompositePending == 1)
                {
                    if (compositeDpr > 0f) s_ClientDPR = compositeDpr;
                    int len = Array.IndexOf(s_CompositeRequestBuf, (byte)0);
                    if (len < 0) len = s_CompositeRequestBuf.Length;
                    string json = Encoding.UTF8.GetString(s_CompositeRequestBuf, 0, len);
                    bool ok = StartCompositeOffscreenCapture(json, compositeFps, compositeW, compositeH);
                    if (!ok)
                    {
                        CodelyLogger.LogError($"[NWB] StartCompositeOffscreenCapture failed fps={compositeFps} w={compositeW} h={compositeH}");
                    }
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB] PollOffscreenRequests error: {ex.Message}\n{ex.StackTrace}");
            }
#endif
        }

        public static void StopServer()
        {
            // Always clean up C# offscreen state first so window transparency
            // (WS_EX_TRANSPARENT) and input interception are restored even if
            // the native call below throws or the poll loop hasn't run yet.
            if (s_OffscreenActive)
            {
                StopOffscreenCapture();
            }

            if (!IsAvailable()) return;
            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
                NativeWindowBridgeAPI.NWB_StopServer();
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB] Stop failed: {ex.Message}");
            }
        }

        public static int GetBoundPort()
        {
            if (!IsAvailable()) return 0;
            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
                return NativeWindowBridgeAPI.NWB_GetBoundPort();
#else
                return 0;
#endif
            }
            catch { return 0; }
        }

        /// <summary>
        /// Tell the native side the Unity Editor's OS PID so it can enumerate
        /// OS-native windows (CGWindowID on macOS) for that process.
        /// </summary>
        public static void SetUnityPid(int pid)
        {
            if (!IsAvailable()) return;
            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
                NativeWindowBridgeAPI.NWB_SetUnityPid(pid);
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB] SetUnityPid failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Push the current EditorWindow list JSON into native as metadata.
        /// The /windows endpoint merges this with OS-native window enumeration.
        /// </summary>
        public static bool UpdateWindowList(string windowsJson)
        {
            if (!IsAvailable() || string.IsNullOrEmpty(windowsJson)) return false;
            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
                return NativeWindowBridgeAPI.NWB_UpdateWindowList(windowsJson) == 1;
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB] UpdateWindowList failed: {ex.Message}");
                return false;
            }
        }

        public static string GetWindowListJson()
        {
            if (!IsAvailable()) return "[]";
            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
                if (NativeWindowBridgeAPI.NWB_GetWindowListJson(
                    JsonBuffer, JsonBuffer.Length, out int bytes) == 1 &&
                    bytes > 0 && bytes <= JsonBuffer.Length)
                {
                    return Encoding.UTF8.GetString(JsonBuffer, 0, bytes);
                }
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB] GetWindowListJson failed: {ex.Message}");
            }
            return "[]";
        }

        public static string GetStreamStatusJson()
        {
            if (!IsAvailable()) return "{\"capturing\":false}";
            try
            {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
                if (NativeWindowBridgeAPI.NWB_GetStreamStatusJson(
                    JsonBuffer, JsonBuffer.Length, out int bytes) == 1 &&
                    bytes > 0 && bytes <= JsonBuffer.Length)
                {
                    return Encoding.UTF8.GetString(JsonBuffer, 0, bytes);
                }
#endif
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB] GetStreamStatusJson failed: {ex.Message}");
            }
            return "{\"capturing\":false}";
        }

        // =====================================================================
        // Offscreen rendering: capture RenderTexture from EditorWindow, push
        // BGRA pixels to native encoder, and poll input events from DataChannel.
        // =====================================================================

        private static bool s_OffscreenActive;
        internal static bool IsOffscreenActive => s_OffscreenActive;
        private static bool s_OffscreenLoopRegistered;
        // Re-entrancy guard: prevents concurrent/nested StopOffscreenCapture calls
        // from heartbeat-timeout, mask disconnect, StopServer, and tab-close paths.
        private static bool s_IsStoppingOffscreen;
        // Set to true during ReleaseCompositeState to tell ReleaseCompositeSlot
        // to close ContainerWindows (not during ApplyCompositeLayout slot replacement).
        private static bool s_IsReleasingComposite;
        // Saved Application.runInBackground value before streaming.
        // Streaming requires runInBackground=true so the player loop
        // keeps running when the editor loses OS focus (user switches
        // to the Codely streaming app).
        private static bool s_SavedRunInBackground;
        private static bool s_RunInBackgroundOverridden;
        private static EditorWindow s_OffscreenTarget;
        private static System.Type s_OffscreenTargetType;
        // If true, we created a floating capture window from a docked target
        // so we can resize it independently for streaming quality.
        private static bool s_OffscreenAutoFloatingWindow;
        private static EditorWindow s_OffscreenOriginalDockedTarget;
        // True when we closed the editor's docked GameView(s) during streaming to
        // prevent their size info from interfering with the floating streaming view.
        private static bool s_ClosedDockedGameViewForStreaming;
        private const string kClosedDockedGameViewKey = "NWB_ClosedDockedGameView";
        private const string kClosedDockedGameViewSiblingKey = "NWB_ClosedDockedGameViewSibling";
        // Keep applying frontend resize target for a few frames until GUIView catches up.
        private static bool s_OffscreenResizeTargetActive;
        private static int s_OffscreenResizeTargetWidth;
        private static int s_OffscreenResizeTargetHeight;
        private static int s_OffscreenResizeRetryFrames;
        private const int kOffscreenResizeRetryMaxFrames = 20;
        private static RenderTexture s_CaptureRT;
        private static Texture2D s_ReadbackTex;

        // Diagnostic: sample captured pixels to detect black-frame issues
        // private static long s_GrabPixelsDiagCounter;
        // private static Texture2D s_DiagTex;

        // When GrabPixels consistently returns RGBA=(0,0,0,0), the SwapChain
        // back-buffer is empty (typical on Unity 2019 Gamma color space where
        // no AuxBackBufferManager intermediate RT exists, so DoPaint→Present
        // clears the SwapChain before GrabPixels reads). Once detected, switch
        // to the GameView's internal m_RenderTexture which survives Present.
        //
        // Detection: prefer PlayerSettings.colorSpace == Gamma (instant, no
        // wasted frames), fall back to pixel-sampling if needed.
        private static bool s_UseRenderTextureFallback;
        // Unity 2019 (especially Gamma + PrintWindow path) has known tab-drag
        // instability in DockArea.DragTab. Keep a narrow safety guard only for
        // that version branch, while allowing tab-drag split on newer versions.
        private static bool s_LoggedUnity2019TabDragGuard;
        private static bool IsUnity2019Editor =>
            Application.unityVersion.StartsWith("2019.", StringComparison.Ordinal);

        // PrintWindow API cached resources for Unity 2019 Gamma fallback.
        // Bypasses GrabPixels entirely by using DWM composition capture.
        private static IntPtr s_PwDC;
        private static IntPtr s_PwBitmap;
        private static IntPtr s_PwOldBmp;
        private static IntPtr s_PwBits;
        private static int s_PwDibW, s_PwDibH;
        private static Texture2D s_PwTex;
        private static byte[] s_PwBuffer;

        // This field is actively used on Win/macOS streaming paths, but Linux
        // builds compile out those paths and can report CS0414 noise.
#pragma warning disable CS0414
        private static int s_OffscreenFps = 30;
#pragma warning restore CS0414
        private static double s_NextCaptureTime;

        private static int s_OffscreenFrameCount;
        private static readonly byte[] s_InputBuffer = new byte[4096];
        private static bool s_DockAreaTabActivated;
        // Saved DockArea state for restoration on stop.
        private static object s_DockAreaInstance;
        private static int s_OriginalDockAreaSelectedIndex = -1;
        // Saved EditorWindow.position at streaming start for black-screen recovery.
        private static Rect s_OriginalTargetPosition;
        // Height difference between GUIView and EditorWindow (tab bar + border).
        // Used for RT size calculation and GrabPixels sourceRect sizing.
        // NOT used for coordinate adjustment since SendEvent uses GUIView coords.
        private static float s_TabBarOffsetY;

        /// <summary>
        /// Client devicePixelRatio received from the browser. 0 means not provided
        /// (use local pixelsPerPoint only). Stored across domain reload via SessionState.
        /// </summary>
        private static float s_ClientDPR;

        /// <summary>
        /// Local pixelsPerPoint (min 1). On Windows this is the OS DPI scaling
        /// factor; on macOS Metal handles backing scale internally so returns 1.
        /// </summary>
        private static float LocalPP =>
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            Mathf.Max(EditorGUIUtility.pixelsPerPoint, 1f);
#else
            1f;
#endif

        /// <summary>
        /// Effective DPI scale: max(clientDPR, LocalPP). When the remote client
        /// has a higher DPR than the local machine, capture at clientDPR so the
        /// browser receives enough pixels for a sharp image.
        /// </summary>
        private static float DPIScale =>
            s_ClientDPR > 0 ? Mathf.Max(s_ClientDPR, LocalPP) : LocalPP;

        /// <summary>
        /// Scale factor to convert frontend CSS pixels to Unity window logical points.
        /// When DPIScale > LocalPP, the window must be larger in logical points
        /// so the backbuffer (= windowLogical × LocalPP) matches the capture RT
        /// (= CSS_size × DPIScale).
        /// </summary>
        private static float DpiScaleFactor => DPIScale / LocalPP;

        // During Windows offscreen GameView streaming, force-disable "Low Resolution"
        // to avoid blurry output after toolbar/preset interactions.
#if UNITY_EDITOR_WIN
        private static bool s_GameViewLowResOriginalCaptured;
        private static bool s_GameViewLowResOriginalValue;
        private static bool s_GameViewLowResForcedOff;
#endif
        private static long s_OffscreenFloatingWindowInstanceId;
#if UNITY_EDITOR_WIN
        private static int s_OffscreenForegroundHideDiagCount;
#endif
        // ── macOS P/Invoke: direct Objective-C runtime calls ──
        // Strategy: make all Unity windows transparent (alpha=0) with
        // setIgnoresMouseEvents:YES so they remain on-screen but invisible.
        // This avoids [NSApp hide:] which causes windowDidResignKey →
        // GUIUtility.hotControl=0 (breaking drags) and lockFocusIfCanDraw=NO
        // (preventing backing store updates for GrabPixels).
        //
        // SceneView.HandleClickAndDragToFocus() calls Focus() on every
        // mouseDown on macOS → GUIView::Focus() → [NSApp activateIgnoringOtherApps:YES].
        // With transparent windows, this activation is invisible to the user,
        // and mouse events pass through to the browser via ignoresMouseEvents.
#if UNITY_EDITOR_OSX
        [DllImport("libobjc.dylib", EntryPoint = "objc_getClass")]
        private static extern IntPtr objc_getClass(string name);

        [DllImport("libobjc.dylib", EntryPoint = "sel_registerName")]
        private static extern IntPtr sel_registerName(string name);

        [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_RetPtr(IntPtr receiver, IntPtr selector);

        [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector);

        [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_void_double(IntPtr receiver, IntPtr selector, double arg);

        [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_void_bool(IntPtr receiver, IntPtr selector, bool arg);

        // Return an id (pointer) from a message with one IntPtr argument.
        // Used for [NSArray objectAtIndex:].
        [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_RetPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

        private static IntPtr s_NSApp = IntPtr.Zero;
        private static IntPtr s_HideSel = IntPtr.Zero;
        private static IntPtr s_UnhideSel = IntPtr.Zero;
        private static IntPtr s_SetAlphaValSel = IntPtr.Zero;
        private static IntPtr s_SetIgnoresMouseSel = IntPtr.Zero;
        private static IntPtr s_SetActivationPolicySel = IntPtr.Zero;
        private static IntPtr s_ActivationPolicySel = IntPtr.Zero;
        private static IntPtr s_WindowsSel = IntPtr.Zero;
        private static IntPtr s_CountSel = IntPtr.Zero;
        private static IntPtr s_ObjectAtIndexSel = IntPtr.Zero;
        private static IntPtr s_ActivateIgnoringSel = IntPtr.Zero;
        private static IntPtr s_MakeKeyWindowSel = IntPtr.Zero;
        private static bool s_ObjCRefsInitialized;
        private const long kNSApplicationActivationPolicyRegular = 0;

        // Cached real NSWindow pointers obtained from [NSApp windows].
        private static readonly List<IntPtr> s_TransparentNSWindows = new List<IntPtr>();
        // NSWindow of the streaming mask window, excluded from alpha/ignore-mouse hiding.
        private static IntPtr s_MacMaskNSWindow = IntPtr.Zero;

        // NSWindow of the offscreen capture floating window. Used on macOS
        // to call [nsWindow makeKeyWindow] before keyboard injection so
        // GUIView.hasFocus returns true for GameView Play mode input.
        private static IntPtr s_OffscreenTargetNSWindow = IntPtr.Zero;

        // CGEvent P/Invoke for injecting native keyboard events on macOS.
        // This is the Mac equivalent of Win32 PostMessageW — events go through
        // the native input pipeline and update the Input Manager key state.
        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, bool keyDown);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern void CGEventPost(uint tap, IntPtr cgEvent);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern void CGEventSetFlags(IntPtr cgEvent, ulong flags);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern void CFRelease(IntPtr cf);

        // CGEvent mouse P/Invoke — Mac equivalent of Win32
        // PostNativeMouseButtonMessage. Used to inject mouse button
        // presses and movement so Input.GetMouseButton / Input.GetAxis
        // work in Play mode during streaming.
        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint { public double x, y; }

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern IntPtr CGEventCreateMouseEvent(
            IntPtr source, uint mouseType, CGPoint mouseCursorPosition, uint mouseButton);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern void CGEventSetIntegerValueField(IntPtr evt, uint field, long value);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern void CGEventSetDoubleValueField(IntPtr evt, uint field, double value);

        // CGEvent mouse type constants
        private const uint kCGEventLeftMouseDown = 1;
        private const uint kCGEventLeftMouseUp = 2;
        private const uint kCGEventRightMouseDown = 3;
        private const uint kCGEventRightMouseUp = 4;
        private const uint kCGEventMouseMoved = 5;
        private const uint kCGEventLeftMouseDragged = 6;
        private const uint kCGEventRightMouseDragged = 7;
        private const uint kCGEventOtherMouseDown = 25;
        private const uint kCGEventOtherMouseUp = 26;
        private const uint kCGEventOtherMouseDragged = 27;

        // CGEvent field IDs for mouse delta (Apple CGEventTypes.h values)
        private const uint kCGMouseEventDeltaX = 4;
        private const uint kCGMouseEventDeltaY = 5;

        private const uint kCGSessionEventTap = 1;

        // Mouse buttons currently held via PostNativeCGMouseButtonEvent.
        // Flushed on stream disconnect to avoid ghost-held button states.
        private static readonly HashSet<int> s_MacHeldMouseButtons = new HashSet<int>();

        // Query physical key state at the hardware level. Used as an
        // optimization for LOCAL Mac: while a physical key is held,
        // skip deactivation so the native Input Manager keeps the key
        // pressed. Returns false on remote Mac (no physical keyboard),
        // which gracefully falls back to the DataChannel timeout path.
        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern bool CGEventSourceKeyState(int stateID, ushort keycode);
        private const int kCGEventSourceStateCombinedSessionState = 0;

        // Whether Unity is currently activated for Play mode keyboard input.
        // While true, CheckMacPlayKeyboardTimeout re-injects held keys and
        // manages deactivation via a DataChannel-based timeout.
        private static bool s_MacPlayKeyboardActive = false;

        // Browser key names currently held (from DataChannel keydown/keyup).
        // Primary key tracking — works on both local and remote Mac.
        private static readonly HashSet<string> s_MacHeldKeys = new HashSet<string>();

        // Timestamp of the last Play mode key event from DataChannel.
        // Deactivation occurs when no DataChannel events arrive for
        // kMacPlayKeyTimeoutSec AND no physical keys are held.
        private static double s_MacPlayKeyLastTime = 0;
        private const double kMacPlayKeyTimeoutSec = 0.3;

        // Frame counter for re-injecting CGEvent keydowns after activation.
        // macOS activateIgnoringOtherApps is asynchronous — CGEvents posted
        // before activation completes are dispatched to the wrong app. By
        // re-injecting for a few frames, we guarantee at least one CGEvent
        // arrives after activation settles.
        private static int s_MacKeyReinjectionFrames = 0;
        private const int kMacKeyReinjectionMaxFrames = 5;

        private static void EnsureObjCRefs()
        {
            if (s_ObjCRefsInitialized) return;
            s_ObjCRefsInitialized = true;
            try
            {
                IntPtr nsAppClass = objc_getClass("NSApplication");
                IntPtr sharedSel = sel_registerName("sharedApplication");
                s_NSApp = objc_msgSend_RetPtr(nsAppClass, sharedSel);
                s_HideSel = sel_registerName("hide:");
                s_UnhideSel = sel_registerName("unhideWithoutActivation");
                s_SetAlphaValSel = sel_registerName("setAlphaValue:");
                s_SetIgnoresMouseSel = sel_registerName("setIgnoresMouseEvents:");
                s_SetActivationPolicySel = sel_registerName("setActivationPolicy:");
                s_ActivationPolicySel = sel_registerName("activationPolicy");
                s_WindowsSel = sel_registerName("windows");
                s_CountSel = sel_registerName("count");
                s_ObjectAtIndexSel = sel_registerName("objectAtIndex:");
                s_ActivateIgnoringSel = sel_registerName("activateIgnoringOtherApps:");
                s_MakeKeyWindowSel = sel_registerName("makeKeyWindow");
                CodelyLogger.Log($"[NWB-ObjC] Initialized: NSApp={s_NSApp}");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjC] Init failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Map browser KeyboardEvent.key names to macOS virtual key codes.
        /// Returns 0xFFFF if the key is not mapped.
        /// </summary>
        private static ushort BrowserKeyToMacVK(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0xFFFF;

            if (key.Length == 1)
            {
                char c = char.ToLower(key[0]);
                switch (c)
                {
                    case 'a': return 0x00; case 's': return 0x01;
                    case 'd': return 0x02; case 'f': return 0x03;
                    case 'h': return 0x04; case 'g': return 0x05;
                    case 'z': return 0x06; case 'x': return 0x07;
                    case 'c': return 0x08; case 'v': return 0x09;
                    case 'b': return 0x0B; case 'q': return 0x0C;
                    case 'w': return 0x0D; case 'e': return 0x0E;
                    case 'r': return 0x0F; case 'y': return 0x10;
                    case 't': return 0x11; case 'o': return 0x1F;
                    case 'u': return 0x20; case 'i': return 0x22;
                    case 'p': return 0x23; case 'l': return 0x25;
                    case 'j': return 0x26; case 'k': return 0x28;
                    case 'n': return 0x2D; case 'm': return 0x2E;
                    case '1': return 0x12; case '2': return 0x13;
                    case '3': return 0x14; case '4': return 0x15;
                    case '5': return 0x17; case '6': return 0x16;
                    case '7': return 0x1A; case '8': return 0x1C;
                    case '9': return 0x19; case '0': return 0x1D;
                    case ' ': return 0x31; case '-': return 0x1B;
                    case '=': return 0x18; case '[': return 0x21;
                    case ']': return 0x1E; case ';': return 0x29;
                    case '\'': return 0x27; case ',': return 0x2B;
                    case '.': return 0x2F; case '/': return 0x2C;
                    case '\\': return 0x2A; case '`': return 0x32;
                }
            }

            switch (key)
            {
                case "Enter": case "Return": return 0x24;
                case "Tab": return 0x30;
                case "Backspace": return 0x33;
                case "Escape": return 0x35;
                case "Delete": return 0x75;
                case "ArrowUp": return 0x7E;
                case "ArrowDown": return 0x7D;
                case "ArrowLeft": return 0x7B;
                case "ArrowRight": return 0x7C;
                case "Home": return 0x73;
                case "End": return 0x77;
                case "PageUp": return 0x74;
                case "PageDown": return 0x79;
                case "Shift": return 0x38;
                case "Control": return 0x3B;
                case "Alt": return 0x3A;
                case "Meta": return 0x37;
                case "CapsLock": return 0x39;
                case "F1": return 0x7A; case "F2": return 0x78;
                case "F3": return 0x63; case "F4": return 0x76;
                case "F5": return 0x60; case "F6": return 0x61;
                case "F7": return 0x62; case "F8": return 0x64;
                case "F9": return 0x65; case "F10": return 0x6D;
                case "F11": return 0x67; case "F12": return 0x6F;
            }
            return 0xFFFF;
        }

        /// <summary>
        /// Post a native CGEvent keyboard event into the macOS session event tap.
        /// This is the Mac equivalent of PostNativeKeyMessage (Win32 PostMessageW).
        /// The event travels through NSApp's native event pipeline and updates
        /// the Input Manager key state, making Input.GetKey work in Play mode.
        /// </summary>
        private static void PostNativeCGKeyEvent(bool isDown, string browserKey)
        {
            ushort macVK = BrowserKeyToMacVK(browserKey);
            if (macVK == 0xFFFF) return;

            IntPtr cgEvent = IntPtr.Zero;
            try
            {
                cgEvent = CGEventCreateKeyboardEvent(IntPtr.Zero, macVK, isDown);
                if (cgEvent == IntPtr.Zero) return;
                CGEventPost(kCGSessionEventTap, cgEvent);
            }
            finally
            {
                if (cgEvent != IntPtr.Zero)
                    CFRelease(cgEvent);
            }
        }

        /// <summary>
        /// Post a native CGEvent mouse button event (down/up) into the macOS
        /// session event tap. Mac equivalent of Win32 PostNativeMouseButtonMessage.
        /// Makes Input.GetMouseButton(0/1/2) return true in Play mode.
        ///
        /// The position uses the offscreen window center so the event lands
        /// inside the GameView. Works for both local and remote streaming.
        /// </summary>
        private static void PostNativeCGMouseButtonEvent(bool isDown, int button)
        {
            uint eventType;
            uint cgButton;
            switch (button)
            {
                case 0: eventType = isDown ? kCGEventLeftMouseDown : kCGEventLeftMouseUp;
                        cgButton = 0; break;
                case 2: eventType = isDown ? kCGEventRightMouseDown : kCGEventRightMouseUp;
                        cgButton = 1; break;
                case 1: eventType = isDown ? kCGEventOtherMouseDown : kCGEventOtherMouseUp;
                        cgButton = 2; break;
                default: return;
            }

            if (isDown)
                s_MacHeldMouseButtons.Add(button);
            else
                s_MacHeldMouseButtons.Remove(button);

            // Use offscreen window center for the CGEvent position so the
            // event targets the GameView. CG coordinates: origin top-left
            // of primary display. macOS screen height = primary display height.
            CGPoint pos = GetOffscreenWindowCGCenter();

            IntPtr cgEvt = IntPtr.Zero;
            try
            {
                cgEvt = CGEventCreateMouseEvent(IntPtr.Zero, eventType, pos, cgButton);
                if (cgEvt == IntPtr.Zero) return;
                CGEventPost(kCGSessionEventTap, cgEvt);
            }
            finally
            {
                if (cgEvt != IntPtr.Zero) CFRelease(cgEvt);
            }

            LogVerbose($"[NWB-MacMouseBtn] CGEvent 0x{eventType:X} down={isDown}" +
                $" btn={button} cgBtn={cgButton} pos=({pos.x:F0},{pos.y:F0})" +
                $" held=[{string.Join(",", s_MacHeldMouseButtons)}]");
        }

        /// <summary>
        /// Post a native CGEvent mouse move/drag event with delta values.
        /// This feeds Input.GetAxisRaw("Mouse X") / Input.GetAxisRaw("Mouse Y")
        /// through the macOS native event pipeline.
        ///
        /// On Windows, Input.GetAxis delta comes from WM_INPUT (RIDEV_INPUTSINK)
        /// which only works locally. On macOS, CGEvent delta works for BOTH
        /// local and remote streaming because we inject the delta directly.
        /// </summary>
        private static void PostNativeCGMouseMoveEvent(
            float deltaX, float deltaY, int heldButton)
        {
            // Choose the correct event type based on which button is held.
            uint eventType;
            uint cgButton = 0;
            if (s_MacHeldMouseButtons.Contains(0))
            {
                eventType = kCGEventLeftMouseDragged;
                cgButton = 0;
            }
            else if (s_MacHeldMouseButtons.Contains(2))
            {
                eventType = kCGEventRightMouseDragged;
                cgButton = 1;
            }
            else if (s_MacHeldMouseButtons.Contains(1))
            {
                eventType = kCGEventOtherMouseDragged;
                cgButton = 2;
            }
            else
            {
                eventType = kCGEventMouseMoved;
            }

            CGPoint pos = GetOffscreenWindowCGCenter();
            IntPtr cgEvt = IntPtr.Zero;
            try
            {
                cgEvt = CGEventCreateMouseEvent(IntPtr.Zero, eventType, pos, cgButton);
                if (cgEvt == IntPtr.Zero) return;

                // Set delta fields so Input.GetAxisRaw("Mouse X/Y") picks
                // up the movement through the native input pipeline.
                CGEventSetIntegerValueField(cgEvt, kCGMouseEventDeltaX, (long)deltaX);
                CGEventSetIntegerValueField(cgEvt, kCGMouseEventDeltaY, (long)deltaY);

                CGEventPost(kCGSessionEventTap, cgEvt);
            }
            finally
            {
                if (cgEvt != IntPtr.Zero) CFRelease(cgEvt);
            }
        }

        /// <summary>
        /// Compute the CG display-coordinate center of the offscreen window.
        /// CG coordinates: origin at top-left of primary display.
        /// EditorWindow.position: origin at top-left of primary display
        /// (same as CG on macOS, already in points).
        /// </summary>
        private static CGPoint GetOffscreenWindowCGCenter()
        {
            if (s_OffscreenTarget != null)
            {
                Rect winPos = s_OffscreenTarget.position;
                return new CGPoint
                {
                    x = winPos.x + winPos.width * 0.5,
                    y = winPos.y + winPos.height * 0.5
                };
            }
            // Fallback: use a central point
            return new CGPoint { x = 100, y = 100 };
        }

        /// <summary>
        /// Per-frame Play mode keyboard management.
        /// Called from EnsureWindowsStayHiddenDuringOffscreen every frame.
        ///
        /// Three responsibilities:
        /// 1. Re-inject CGEvent keydowns during the grace period after
        ///    activation so the async activateIgnoringOtherApps has time
        ///    to settle before CGEvents are dispatched.
        /// 2. Poll CGEventSourceKeyState for physically held keys (LOCAL
        ///    Mac optimization). While any key is physically held, keep
        ///    Unity active so the native Input Manager maintains the
        ///    pressed state for continuous movement.
        /// 3. Deactivate Unity when no physical keys are held AND no
        ///    DataChannel events have arrived for kMacPlayKeyTimeoutSec.
        ///    On remote Mac, CGEventSourceKeyState always returns false,
        ///    so the timeout is the sole deactivation trigger.
        /// </summary>
        private static void CheckMacPlayKeyboardTimeout()
        {
            if (!s_MacPlayKeyboardActive) return;

            // Re-inject held keys during the grace period so at least one
            // CGEvent arrives after macOS activation has settled.
            if (s_MacKeyReinjectionFrames > 0 && s_MacHeldKeys.Count > 0)
            {
                s_MacKeyReinjectionFrames--;
                EnsureObjCRefs();
                if (s_NSApp != IntPtr.Zero && s_OffscreenTargetNSWindow != IntPtr.Zero)
                {
                    objc_msgSend_void_bool(s_NSApp, s_ActivateIgnoringSel, true);
                    objc_msgSend_void(s_OffscreenTargetNSWindow, s_MakeKeyWindowSel);
                }
                foreach (var key in s_MacHeldKeys)
                    PostNativeCGKeyEvent(true, key);
            }

            // LOCAL optimization: poll physical key state. If any tracked
            // key is still physically held, keep Unity active and refresh
            // the timestamp so the timeout doesn't fire. On remote Mac
            // (no physical keyboard), this always returns false and the
            // code falls through to the timeout check below.
            if (s_MacHeldKeys.Count > 0)
            {
                bool anyPhysicallyHeld = false;
                foreach (var key in s_MacHeldKeys)
                {
                    ushort vk = BrowserKeyToMacVK(key);
                    if (vk != 0xFFFF && CGEventSourceKeyState(
                            kCGEventSourceStateCombinedSessionState, vk))
                    {
                        anyPhysicallyHeld = true;
                        break;
                    }
                }
                if (anyPhysicallyHeld)
                {
                    s_MacPlayKeyLastTime = EditorApplication.timeSinceStartup;
                    return;
                }
            }

            // Deactivate when no physical keys are held AND no DataChannel
            // events have arrived for the timeout period. The browser
            // regains keyboard focus and immediately fires keydown if the
            // user is still pressing a key → re-activate.
            double elapsed = EditorApplication.timeSinceStartup - s_MacPlayKeyLastTime;
            if (elapsed > kMacPlayKeyTimeoutSec)
            {
                foreach (var key in s_MacHeldKeys)
                    PostNativeCGKeyEvent(false, key);
                s_MacHeldKeys.Clear();

                s_MacPlayKeyboardActive = false;
                s_MacKeyReinjectionFrames = 0;
                DeactivateUnityKeepWindows();
                ReapplyWindowTransparency();
            }
        }
#endif

        // Count hide/transparency calls for throttled logging.
        // Diagnostic counter used by hide/deactivate routines on supported platforms.
#pragma warning disable CS0414
        private static int s_HideCallCount;
#pragma warning restore CS0414
        // Track whether a remote mouse button is currently pressed.
        // Used to avoid deactivation during drag operations.
        private static bool s_RemoteMouseButtonDown;
        // True when native capture has ended but the user has not clicked
        // "Disconnect & Restore" yet. In this state Unity stays hidden and
        // floating windows are preserved for an explicit restore action.
        private static bool s_NativeOffscreenDisconnectedAwaitingRestore;

        /// <summary>
        /// Collect all real NSWindow pointers via [NSApp windows].
        /// This is safe because NSApp.windows returns actual Objective-C
        /// NSWindow objects, unlike ContainerWindow.m_WindowPtr which
        /// is a C++ ContainerWindow* and must NOT receive ObjC messages.
        /// </summary>
        private static List<IntPtr> GetAllNSWindows(bool includeMaskWindow = false)
        {
            var result = new List<IntPtr>();
#if UNITY_EDITOR_OSX
            EnsureObjCRefs();
            if (s_NSApp == IntPtr.Zero || s_WindowsSel == IntPtr.Zero) return result;

            try
            {
                IntPtr windowsArray = objc_msgSend_RetPtr(s_NSApp, s_WindowsSel);
                if (windowsArray == IntPtr.Zero) return result;

                IntPtr countRaw = objc_msgSend_RetPtr(windowsArray, s_CountSel);
                int count = (int)(long)countRaw;

                for (int i = 0; i < count; i++)
                {
                    IntPtr nsWin = objc_msgSend_RetPtr_IntPtr(
                        windowsArray, s_ObjectAtIndexSel, (IntPtr)i);
                    if (nsWin != IntPtr.Zero && (includeMaskWindow || nsWin != s_MacMaskNSWindow))
                        result.Add(nsWin);
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjC] GetAllNSWindows failed: {ex.Message}");
            }
#endif
            return result;
        }

        /// <summary>
        /// Make all NSWindows transparent (alpha=0) with mouse events
        /// passing through. Windows remain on-screen so lockFocusIfCanDraw=YES
        /// and the Metal backing store keeps updating for GrabPixels capture.
        /// </summary>
        private static void MakeAllWindowsTransparent()
        {
#if UNITY_EDITOR_OSX
            EnsureObjCRefs();
            if (s_SetAlphaValSel == IntPtr.Zero) return;

            s_TransparentNSWindows.Clear();
            var allWindows = GetAllNSWindows();

            foreach (var nsWin in allWindows)
            {
                objc_msgSend_void_double(nsWin, s_SetAlphaValSel, 0.0);
                objc_msgSend_void_bool(nsWin, s_SetIgnoresMouseSel, true);
                s_TransparentNSWindows.Add(nsWin);
            }
            LogVerbose($"[NWB-ObjC] Made {allWindows.Count} NSWindows transparent (via [NSApp windows])");
#elif UNITY_EDITOR_WIN
            try
            {
                s_TransparentHwnds.Clear();
                s_OriginalExStyles.Clear();
                s_OriginalWindowRects.Clear();
                var hwnds = GetAllUnityHwnds();
                foreach (var hwnd in hwnds)
                {
                    int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);

                    // DOMAIN-RELOAD SAFETY: After domain reload, C# statics
                    // are cleared but the HWND may already have WS_EX_TRANSPARENT
                    // from the previous session. Strip it before saving so that
                    // RestoreAllWindowsVisible can properly undo the modification.
                    int cleanStyle = exStyle & ~WS_EX_TRANSPARENT;
                    s_OriginalExStyles[hwnd] = cleanStyle;

                    // IMPORTANT: Do NOT use WS_EX_LAYERED. The Tuanjie C++
                    // GUIView::DoPaint checks the ContainerWindow HWND for
                    // WS_EX_LAYERED and sets m_Transparent=true (one-way latch
                    // with no reset path). m_Transparent=true causes
                    // SwapChain::Present() to be skipped in EndRendering(),
                    // permanently freezing the window's visual output.
                    SetWindowLongW(hwnd, GWL_EXSTYLE,
                        exStyle | WS_EX_TRANSPARENT);

                    if (s_UseRenderTextureFallback)
                    {
                        // PrintWindow mode: keep windows in-place so DWM
                        // continues to composite their content. Push behind
                        // the streaming mask window via HWND_BOTTOM.
                        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    }
                    else
                    {
                        // Normal mode: save original position then move
                        // off-screen to hide visually. GrabPixels still
                        // works on off-screen windows.
                        GetWindowRect(hwnd, out RECT origRect);
                        bool alreadyAtSentinel = IsAtOffscreenSentinel(origRect);
                        if (!alreadyAtSentinel)
                        {
                            s_OriginalWindowRects[hwnd] = origRect;
                        }
                        else
                        {
                            // Window already at sentinel — likely after domain reload
                            // cleared s_OriginalWindowRects. Try SessionState first,
                            // then fall back to the monitor work area starting at (0,0).
                            RestoreWindowRectsFromSessionState();
                            if (!s_OriginalWindowRects.ContainsKey(hwnd))
                            {
                                var fallback = GetFallbackRestoreRect(hwnd, origRect);
                                s_OriginalWindowRects[hwnd] = fallback;
                                CodelyLogger.LogWarning($"[NWB-Win] Window hwnd=0x{hwnd:X} at sentinel, no saved rect in SessionState, origRect=({origRect.Left},{origRect.Top},{origRect.Right},{origRect.Bottom}) w={origRect.Right-origRect.Left} h={origRect.Bottom-origRect.Top}, using fallback ({fallback.Left},{fallback.Top},{fallback.Right},{fallback.Bottom})");
                            }
                        }
                        int w = origRect.Right - origRect.Left;
                        int h = origRect.Bottom - origRect.Top;
                        SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, w, h,
                            SWP_NOACTIVATE | SWP_NOZORDER);
                    }

                    s_TransparentHwnds.Add(hwnd);
                }
                // After all windows have been processed (both real saves and
                // sentinel restores), persist the complete s_OriginalWindowRects
                // to SessionState. This ensures that windows saved earlier in
                // the loop are available when later windows are at sentinel.
                SaveWindowRectsToSessionState();
                CodelyLogger.Log($"[NWB-Win] Made {hwnds.Count} HWNDs hidden (mode={( s_UseRenderTextureFallback ? "PrintWindow/HWND_BOTTOM" : "off-screen" )})");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Win] MakeAllWindowsTransparent failed: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// Re-apply alpha=0 + ignoresMouseEvents=YES on all NSWindows.
        /// Fetches a fresh list via [NSApp windows] so newly created
        /// windows (e.g. popup ContainerWindows) are also made transparent.
        /// Called after Focus() which may order windows front.
        /// </summary>
        private static void ReapplyWindowTransparency()
        {
#if UNITY_EDITOR_OSX
            if (s_SetAlphaValSel == IntPtr.Zero) return;
            var allWindows = GetAllNSWindows();
            s_TransparentNSWindows.Clear();
            foreach (var nsWin in allWindows)
            {
                objc_msgSend_void_double(nsWin, s_SetAlphaValSel, 0.0);
                objc_msgSend_void_bool(nsWin, s_SetIgnoresMouseSel, true);
                s_TransparentNSWindows.Add(nsWin);
            }
#elif UNITY_EDITOR_WIN
            try
            {
                // Re-enumerate to catch newly created windows (e.g. popup ContainerWindows).
                var hwnds = GetAllUnityHwnds();
                s_TransparentHwnds.Clear();
                bool detachedHidden = s_NativeOffscreenDisconnectedAwaitingRestore;
                foreach (var hwnd in hwnds)
                {
                    if (!s_OriginalExStyles.ContainsKey(hwnd))
                    {
                        // Strip WS_EX_TRANSPARENT before saving — if a new window
                        // appeared after a domain reload it might already carry our
                        // transparent flag from ReapplyWindowTransparency.
                        int rawStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
                        s_OriginalExStyles[hwnd] = rawStyle & ~WS_EX_TRANSPARENT;
                    }
                    int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
                    SetWindowLongW(hwnd, GWL_EXSTYLE,
                        exStyle | WS_EX_TRANSPARENT);

                    bool useInPlaceFallback = s_UseRenderTextureFallback && !detachedHidden;
                    if (useInPlaceFallback)
                    {
                        // PrintWindow mode: HWND_BOTTOM keeps windows behind
                        // the streaming mask while DWM keeps compositing.
                        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    }
                    else
                    {
                        // Normal off-screen mode: re-enforce (-32000,-32000)
                        // position if Unity moved the window back on-screen.
                        if (!s_OriginalWindowRects.ContainsKey(hwnd))
                        {
                            GetWindowRect(hwnd, out RECT r);
                            bool atSentinel = (r.Left <= -30000 && r.Top <= -30000);
                            if (!atSentinel)
                                s_OriginalWindowRects[hwnd] = r;
                        }
                        GetWindowRect(hwnd, out RECT liveRect);
                        int w2 = Mathf.Max(liveRect.Right - liveRect.Left, 1);
                        int h2 = Mathf.Max(liveRect.Bottom - liveRect.Top, 1);
                        bool alreadyOffscreen = (liveRect.Left == -32000 && liveRect.Top == -32000);
                        if (!alreadyOffscreen)
                        {
                            SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, w2, h2,
                                SWP_NOACTIVATE | SWP_NOZORDER);
                        }
                    }

                    s_TransparentHwnds.Add(hwnd);
                }
                s_LastWindowTransparencyReapplyTime = EditorApplication.timeSinceStartup;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Win] ReapplyWindowTransparency failed: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// macOS defensive recovery on domain reload.
        /// If a previous offscreen session left NSApp in Accessory activation policy
        /// (e.g. abnormal Cowork exit), the Dock icon disappears. This restores it
        /// by forcing Regular policy and restoring window visibility. Only takes
        /// action when policy is actually wrong — avoids disturbing macOS window
        /// management when everything is already normal.
        /// </summary>
        private static void EnsureMacEditorVisibilitySelfHeal()
        {
#if UNITY_EDITOR_OSX
            if (s_OffscreenTarget != null) return;
            EnsureObjCRefs();
            if (s_NSApp == IntPtr.Zero) return;
            try
            {
                long currentPolicy = s_ActivationPolicySel != IntPtr.Zero
                    ? (long)objc_msgSend_RetPtr(s_NSApp, s_ActivationPolicySel)
                    : -1;

                if (s_SetActivationPolicySel != IntPtr.Zero)
                    objc_msgSend_void_IntPtr(s_NSApp, s_SetActivationPolicySel,
                        (IntPtr)kNSApplicationActivationPolicyRegular);
                // RestoreAllWindowsVisible: alpha=1, ignoresMouseEvents=false,
                // setActivationPolicy:Regular, activateIgnoringOtherApps:YES.
                RestoreAllWindowsVisible();
                CodelyLogger.Log($"[NWB-ObjC] Self-heal: policy was {currentPolicy}, called RestoreAllWindowsVisible.");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjC] Self-heal failed: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// Restore all NSWindows to full visibility.
        /// Uses [NSApp windows] for a fresh list in case windows were
        /// created or destroyed since MakeAllWindowsTransparent was called.
        /// </summary>
        private static void RestoreAllWindowsVisible(IntPtr excludeNSWindow = default)
        {
#if UNITY_EDITOR_OSX
            EnsureObjCRefs();
            if (s_SetAlphaValSel == IntPtr.Zero) return;

            var allWindows = GetAllNSWindows();
            int skipped = 0;
            foreach (var nsWin in allWindows)
            {
                // Skip NSWindows that are being closed. On macOS
                // [nsWindow close] doesn't immediately remove the
                // pointer from [NSApp windows], so we must not
                // restore alpha on a window we just closed.
                if (excludeNSWindow != IntPtr.Zero && nsWin == excludeNSWindow)
                {
                    skipped++;
                    continue;
                }
                objc_msgSend_void_double(nsWin, s_SetAlphaValSel, 1.0);
                objc_msgSend_void_bool(nsWin, s_SetIgnoresMouseSel, false);
            }

            // Restore Regular activation policy and unhide before activation so
            // a previous hide:/Accessory state cannot leave the editor invisible.
            if (s_NSApp != IntPtr.Zero && s_SetActivationPolicySel != IntPtr.Zero)
            {
                objc_msgSend_void_IntPtr(s_NSApp, s_SetActivationPolicySel,
                    (IntPtr)kNSApplicationActivationPolicyRegular);
            }
            if (s_NSApp != IntPtr.Zero && s_UnhideSel != IntPtr.Zero)
                objc_msgSend_void(s_NSApp, s_UnhideSel);
            if (s_NSApp != IntPtr.Zero && s_ActivateIgnoringSel != IntPtr.Zero)
            {
                objc_msgSend_void_bool(s_NSApp, s_ActivateIgnoringSel, true);
            }

            CodelyLogger.Log($"[NWB-ObjC] Restored {allWindows.Count - skipped} NSWindows to visible (Regular + activate){(skipped > 0 ? $", skipped {skipped} closing" : "")}");
            s_TransparentNSWindows.Clear();
#elif UNITY_EDITOR_WIN
            try
            {
                var visibleHwnds = GetAllUnityHwnds();
                var hwndsSet = new HashSet<IntPtr>(visibleHwnds);
                foreach (var trackedHwnd in s_OriginalExStyles.Keys)
                {
                    if (!hwndsSet.Contains(trackedHwnd) && IsWindow(trackedHwnd)
                        && trackedHwnd != s_MaskWindowHwnd)
                        hwndsSet.Add(trackedHwnd);
                }

                var hwnds = new List<IntPtr>(hwndsSet);

                // Restore original extended styles (remove WS_EX_TRANSPARENT).
                // ALWAYS strip WS_EX_TRANSPARENT as a safety net — even if the
                // saved "original" style contains it (e.g. domain reload
                // corrupted the saved state), Unity editor windows should
                // never have WS_EX_TRANSPARENT in normal operation.
                foreach (var hwnd in hwnds)
                {
                    int curStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
                    int restoreStyle;
                    if (s_OriginalExStyles.TryGetValue(hwnd, out int origStyle))
                    {
                        restoreStyle = origStyle & ~WS_EX_TRANSPARENT;
                    }
                    else
                    {
                        restoreStyle = curStyle & ~WS_EX_TRANSPARENT;
                    }
                    SetWindowLongW(hwnd, GWL_EXSTYLE, restoreStyle);
                    int afterStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
                    LogVerbose($"[NWB-Win] Restore hwnd=0x{hwnd:X}: before=0x{curStyle:X8} orig=0x{(s_OriginalExStyles.ContainsKey(hwnd) ? s_OriginalExStyles[hwnd] : -1):X8} target=0x{restoreStyle:X8} after=0x{afterStyle:X8} transparent={(afterStyle & WS_EX_TRANSPARENT) != 0}");
                }

                // Bring windows back: restore Z-order and position.
                // SWP_FRAMECHANGED ensures DWM refreshes the hit-test
                // region after removing WS_EX_TRANSPARENT.
                foreach (var hwnd in hwnds)
                {
                    ShowWindow(hwnd, SW_SHOW);
                    if (s_UseRenderTextureFallback)
                    {
                        // PrintWindow mode: windows stayed in-place,
                        // only need Z-order + visibility restore.
                        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
                    }
                    else if (s_OriginalWindowRects.TryGetValue(hwnd, out RECT r))
                    {
                        // Off-screen mode: move back to saved position.
                        bool savedIsOffscreen = IsAtOffscreenSentinel(r);
                        if (savedIsOffscreen)
                        {
                            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
                                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
                        }
                        else
                        {
                            int w = r.Right - r.Left;
                            int h = r.Bottom - r.Top;
                            SetWindowPos(hwnd, HWND_TOP, r.Left, r.Top, w, h,
                                SWP_SHOWWINDOW | SWP_FRAMECHANGED);
                        }
                        LogVerbose($"[NWB-Win] Restore pos hwnd=0x{hwnd:X}: saved=({r.Left},{r.Top},{r.Right},{r.Bottom}) savedIsOffscreen={savedIsOffscreen}");
                    }
                    else
                    {
                        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
                        LogVerbose($"[NWB-Win] Restore pos hwnd=0x{hwnd:X}: no saved rect, used SWP_NOMOVE");
                    }
                }

                // Force a full Win32 redraw so the restored windows display
                // their current content immediately.
                foreach (var hwnd in hwnds)
                {
                    RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                        RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_ERASE | RDW_FRAME);
                }

                // Activate the first Unity HWND (main editor window) so it
                // receives keyboard/mouse input focus immediately.
                if (hwnds.Count > 0)
                {
                    ForceForegroundWindow(hwnds[0]);
                }

                CodelyLogger.Log($"[NWB-Win] Restored {hwnds.Count} HWNDs to visible + Z-order + focus");

                s_TransparentHwnds.Clear();
                s_OriginalExStyles.Clear();
                s_OriginalWindowRects.Clear();
                // Clear SessionState — streaming has ended, window positions
                // are no longer needed for domain reload recovery.
                SessionState.EraseString(kWindowRectsSaveKey);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Win] RestoreAllWindowsVisible failed: {ex.Message}");
            }
#endif
        }

#if UNITY_EDITOR_WIN
        /// <summary>
        /// Returns true when the RECT sits at the off-screen sentinel position
        /// (-32000, -32000 physical) used by <see cref="MakeAllWindowsTransparent"/>
        /// to hide windows during streaming.
        /// </summary>
        private static bool IsAtOffscreenSentinel(RECT rect)
        {
            return rect.Left <= -30000 || rect.Top <= -30000;
        }

        /// <summary>
        /// If <paramref name="hwnd"/> is stuck at the off-screen sentinel,
        /// force it to an on-screen position using the monitor work area.
        /// </summary>
        /// <returns>true when a repair was performed.</returns>
        private static bool RepairOffscreenSentinel(IntPtr hwnd, string logContext)
        {
            GetWindowRect(hwnd, out RECT rect);
            if (!IsAtOffscreenSentinel(rect)) return false;

            var fb = GetFallbackRestoreRect(hwnd, rect);
            ShowWindow(hwnd, SW_SHOW);
            SetWindowPos(hwnd, HWND_TOP, fb.Left, fb.Top, fb.Right - fb.Left, fb.Bottom - fb.Top,
                SWP_SHOWWINDOW | SWP_FRAMECHANGED);
            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_ERASE | RDW_FRAME);
            CodelyLogger.LogWarning(
                $"[NWB-Win] {logContext}: hwnd=0x{hwnd:X} at ({rect.Left},{rect.Top}), forced to ({fb.Left},{fb.Top},{fb.Right - fb.Left},{fb.Bottom - fb.Top})");
            return true;
        }

        /// <summary>
        /// Get a reasonable on-screen fallback rect for a window whose
        /// original position is unknown (e.g. lost during domain reload).
        /// Uses the monitor's work area (taskbar-excluded) that the HWND
        /// is nearest to. Falls back to (0,0,1920,1080) if Win32 APIs fail.
        /// </summary>
        private static RECT GetFallbackRestoreRect(IntPtr hwnd, RECT currentRect)
        {
            try
            {
                IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (hMon != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(hMon, ref mi))
                        return mi.rcWork;
                }
            }
            catch { }
            return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        }

        /// <summary>
        /// <summary>
        /// Persist s_OriginalWindowRects to SessionState.
        /// Called once from MakeAllWindowsTransparent when windows are still
        /// on-screen (real positions). All rects at this point are real
        /// (non-sentinel, non-fallback), so no filtering is needed.
        /// </summary>
        private static void SaveWindowRectsToSessionState()
        {
            if (s_OriginalWindowRects == null || s_OriginalWindowRects.Count == 0)
            {
                // Don't erase — keep existing data for domain reload recovery.
                return;
            }
            var sb = new StringBuilder();
            foreach (var kvp in s_OriginalWindowRects)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(kvp.Key.ToInt64());
                sb.Append(',');
                sb.Append(kvp.Value.Left);
                sb.Append(',');
                sb.Append(kvp.Value.Top);
                sb.Append(',');
                sb.Append(kvp.Value.Right);
                sb.Append(',');
                sb.Append(kvp.Value.Bottom);
            }
            SessionState.SetString(kWindowRectsSaveKey, sb.ToString());
        }

        /// <summary>
        /// Restore s_OriginalWindowRects from SessionState after domain reload.
        /// </summary>
        private static void RestoreWindowRectsFromSessionState()
        {
            string saved = SessionState.GetString(kWindowRectsSaveKey, "");
            if (string.IsNullOrEmpty(saved)) return;
            int count = 0;
            foreach (var entry in saved.Split(';'))
            {
                var parts = entry.Split(',');
                if (parts.Length != 5) continue;
                if (!long.TryParse(parts[0], out long hwndLong)) continue;
                IntPtr hwnd = new IntPtr(hwndLong);
                if (!IsWindow(hwnd)) continue;
                if (!int.TryParse(parts[1], out int l) ||
                    !int.TryParse(parts[2], out int t) ||
                    !int.TryParse(parts[3], out int r) ||
                    !int.TryParse(parts[4], out int b)) continue;
                s_OriginalWindowRects[hwnd] = new RECT { Left = l, Top = t, Right = r, Bottom = b };
                count++;
            }
        }
#endif

#if UNITY_EDITOR_OSX
        /// <summary>
        /// Show the streaming mask window after hiding all Unity editor windows.
        /// Detects the mask NSWindow by diffing [NSApp windows] before and after
        /// creation, then excludes it from subsequent transparency passes.
        /// </summary>
        private static void ShowStreamingMaskWindow()
        {
            try
            {
                var beforeWindows = new HashSet<IntPtr>(GetAllNSWindows(includeMaskWindow: true));
                foreach (var nsWin in s_TransparentNSWindows) beforeWindows.Add(nsWin);

                StreamingMaskWindow.ShowMask();

                s_MacMaskNSWindow = IntPtr.Zero;
                var afterWindows = GetAllNSWindows(includeMaskWindow: true);
                foreach (var nsWin in afterWindows)
                {
                    if (!beforeWindows.Contains(nsWin))
                    {
                        s_MacMaskNSWindow = nsWin;
                        break;
                    }
                }

                if (s_MacMaskNSWindow != IntPtr.Zero)
                {
                    LogVerbose($"[NWB-ObjC] Streaming mask window shown, nsWindow={s_MacMaskNSWindow}");
                }
                else
                {
                    CodelyLogger.LogWarning("[NWB-ObjC] Streaming mask window shown but NSWindow not detected");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjC] ShowStreamingMaskWindow failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Hide the streaming mask window and clear the tracked NSWindow.
        /// </summary>
        private static void HideStreamingMaskWindow()
        {
            try
            {
                StreamingMaskWindow.HideMask();
                s_MacMaskNSWindow = IntPtr.Zero;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjC] HideStreamingMaskWindow failed: {ex.Message}");
            }
        }
#endif

        /// <summary>
        /// Force a window to the foreground, bypassing the Win32 restriction
        /// that only the foreground process can call SetForegroundWindow.
        /// Uses AttachThreadInput to merge input queues with the current
        /// foreground window's thread, then calls SetForegroundWindow.
        /// </summary>
        private static void ForceForegroundWindow(IntPtr targetHwnd)
        {
#if UNITY_EDITOR_WIN
            try
            {
                IntPtr fgHwnd = GetForegroundWindow();
                if (fgHwnd == targetHwnd)
                {
                    LogVerbose("[NWB-Win] Target is already foreground");
                    return;
                }

                uint curThreadId = GetCurrentThreadId();
                uint fgThreadId = GetWindowThreadProcessId(fgHwnd, out _);

                bool attached = false;
                if (curThreadId != fgThreadId)
                {
                    attached = AttachThreadInput(curThreadId, fgThreadId, true);
                }

                BringWindowToTop(targetHwnd);
                ShowWindow(targetHwnd, SW_SHOW);
                bool result = SetForegroundWindow(targetHwnd);
                LogVerbose($"[NWB-Win] ForceForegroundWindow: attached={attached} result={result} hwnd=0x{targetHwnd:X}");

                if (attached)
                {
                    AttachThreadInput(curThreadId, fgThreadId, false);
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Win] ForceForegroundWindow failed: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// Deactivate Unity without permanently hiding windows.
        /// Sequence: [NSApp hide:] (deactivates, hides windows) →
        /// [NSApp unhideWithoutActivation] (re-shows windows without activating).
        /// Windows come back at alpha=0 (preserved), so they remain invisible.
        /// The previously active app (browser) regains activation.
        /// </summary>
        private static void DeactivateUnityKeepWindows()
        {
#if UNITY_EDITOR_OSX
            EnsureObjCRefs();
            if (s_NSApp == IntPtr.Zero) return;
            try
            {
                // hide: deactivates Unity, orders all windows out.
                objc_msgSend_void_IntPtr(s_NSApp, s_HideSel, IntPtr.Zero);
                // unhideWithoutActivation: re-shows windows without activating.
                // Windows retain their alpha=0 setting from MakeAllWindowsTransparent.
                objc_msgSend_void(s_NSApp, s_UnhideSel);

                s_HideCallCount++;
                if (s_HideCallCount <= 3 || s_HideCallCount % 200 == 0)
                    LogVerbose($"[NWB-ObjC] deactivate+unhide #{s_HideCallCount}");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjC] DeactivateUnityKeepWindows failed: {ex.Message}");
            }
#elif UNITY_EDITOR_WIN
            try
            {
                // Re-apply transparency (covers newly created popup windows) then
                // push all Unity windows behind other windows so the browser stays
                // in front. Unlike SW_MINIMIZE, this keeps windows renderable so
                // GrabPixels continues to work.
                ReapplyWindowTransparency();
                foreach (var hwnd in s_TransparentHwnds)
                {
                    SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                s_HideCallCount++;
                if (s_HideCallCount <= 3 || s_HideCallCount % 200 == 0)
                    LogVerbose($"[NWB-Win] deactivate+push-back #{s_HideCallCount}");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Win] DeactivateUnityKeepWindows failed: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// Schedule a delayed deactivation for the next editor frame.
        /// Used after closing native popup windows, as the popup's
        /// ContainerWindow.Close() can trigger additional window ordering
        /// that re-activates Unity after our synchronous deactivation.
        /// </summary>
        private static void RequestDelayedDeactivation()
        {
            if (!s_OffscreenActive) return;
            EditorApplication.delayCall += () =>
            {
                if (!s_OffscreenActive) return;
                ReapplyWindowTransparency();
                DeactivateUnityKeepWindows();
            };
        }

        /// <summary>
        /// While offscreen streaming is active, keep Unity hidden even if
        /// the OS or internal code re-activates/re-shows windows (e.g. play
        /// mode transitions, taskbar clicks).
        /// </summary>
        private static void EnsureWindowsStayHiddenDuringOffscreen()
        {
#if UNITY_EDITOR_WIN
            if (!s_OffscreenActive) return;
            try
            {
                // Snapshot FG before re-hiding: taskbar restore moves an
                // off-screen HWND on-screen, and ReapplyWindowTransparency
                // would shove it back to -32000 before we can detect the click.
                IntPtr fgBeforeReapply = GetForegroundWindow();
                bool restoredFromOffscreen = false;
                if (fgBeforeReapply != IntPtr.Zero
                    && fgBeforeReapply != s_MaskWindowHwnd
                    && !s_UseRenderTextureFallback
                    && s_TransparentHwnds.Contains(fgBeforeReapply))
                {
                    GetWindowRect(fgBeforeReapply, out RECT fgRect);
                    restoredFromOffscreen = fgRect.Left > -30000 && fgRect.Top > -30000;
                }

                // Re-apply WS_EX_TRANSPARENT + hiding strategy on all HWNDs.
                // PrintWindow mode uses HWND_BOTTOM in-place; normal mode
                // uses off-screen (-32000,-32000). EditorWindow.position
                // setter or Unity internal layout may change styles/Z-order.
                double now = EditorApplication.timeSinceStartup;
                if (now - s_LastWindowTransparencyReapplyTime >= kWindowTransparencyReapplyIntervalSec)
                {
                    s_LastWindowTransparencyReapplyTime = now;
                    ReapplyWindowTransparency();
                }

                IntPtr fg = fgBeforeReapply;
                if (fg != IntPtr.Zero)
                {
                    // Skip if the foreground window is the streaming mask window —
                    // it is intentionally visible and should not be pushed back.
                    if (fg == s_MaskWindowHwnd)
                        return;

                    // Skip foreground reclaim while Play mode keys are held
                    // OR within the grace period after the last SetFocus.
                    //
                    // s_WinHeldKeys tracks DataChannel keydown/keyup state. It
                    // stays non-empty for the entire key hold — including the
                    // initial key-repeat delay (~500ms) that the short grace
                    // period cannot cover.  The grace period (0.15s) handles
                    // the brief SetFocus effect AFTER all keys are released.
                    //
                    // Without this, the initial key-repeat delay causes a
                    // stutter: grace expires → DeactivateUnityKeepWindows +
                    // SetForegroundWindow(mask) → hasFocus=false → movement
                    // stops until the first key repeat arrives.
                        if (s_WinHeldKeys.Count > 0)
                            return;
                        double focusElapsed = EditorApplication.timeSinceStartup - s_WinPlayKeyLastFocusTime;
                        if (focusElapsed < kWinPlayKeyFocusGraceSec)
                            return;

                    GetWindowThreadProcessId(fg, out uint fgPid);
                    uint curPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                    double nowSec = EditorApplication.timeSinceStartup;
                    if (fgPid != 0 && fgPid != curPid)
                    {
                        // Another app holds FG (browser / Codely). Remember how long.
                        if (s_OtherProcessFgSince < 0)
                            s_OtherProcessFgSince = nowSec;
                    }
                    else if (fgPid == curPid)
                    {
                        // Only treat as taskbar click if another process held FG long enough.
                        // Stream-start Unity FG churn never satisfies this, so the mask
                        // stays hidden until the user deliberately activates Tuanjie.
                        bool taskbarActivate = s_OtherProcessFgSince >= 0
                            && (nowSec - s_OtherProcessFgSince) >= kTaskbarActivateMinOtherFgSec;
                        if (!taskbarActivate && restoredFromOffscreen)
                            taskbarActivate = true;
                        s_OtherProcessFgSince = -1;

                        s_OffscreenForegroundHideDiagCount++;
                        // In composite streaming mode, the floating ContainerWindow
                        // is resized larger than its original docked size. Any
                        // DeactivateUnityKeepWindows() → SetWindowPos(HWND_BOTTOM)
                        // sends WM_SIZE which resets ContainerWindow.position back
                        // to the original (smaller) size — the position setter
                        // doesn't stick for offscreen windows at (-32000,-32000).
                        // This causes a per-frame resize loop → Screen dimensions
                        // oscillate → Camera.aspect oscillates → vertical shaking.
                        // Skip DeactivateUnityKeepWindows() entirely in composite mode
                        // unless the user explicitly clicked the taskbar.
                        bool skipDeactivate = (s_CompositeActive && !taskbarActivate)
                            || (s_IsRemoteConnection && !taskbarActivate);

                        if (!skipDeactivate)
                        {
                            if (s_OffscreenForegroundHideDiagCount <= 20 || s_OffscreenForegroundHideDiagCount % 120 == 0)
                            {
                                CodelyLogger.LogWarning($"[NWB-Win] Foreground reclaimed by Unity during offscreen, forcing hidden again #{s_OffscreenForegroundHideDiagCount} (taskbarActivate={taskbarActivate})");
                            }
                            DeactivateUnityKeepWindows();
                        }
                        else if (s_OffscreenForegroundHideDiagCount == 1)
                        {
                            LogVerbose($"[NWB-Win] Foreground reclaimed by Unity during offscreen, skipping deactivate (composite={s_CompositeActive} remote={s_IsRemoteConnection} taskbarActivate={taskbarActivate})");
                        }

                        if (taskbarActivate)
                            EnsureStreamingMaskShownForUser();
                    }
                }
            }
            catch (Exception ex)
            {
                if (s_OffscreenForegroundHideDiagCount <= 5 || s_OffscreenForegroundHideDiagCount % 120 == 0)
                {
                    CodelyLogger.LogWarning($"[NWB-Win] EnsureWindowsStayHiddenDuringOffscreen failed: {ex.Message}");
                }
            }
#elif UNITY_EDITOR_OSX
            if (!s_OffscreenActive) return;
            // Check whether Play mode keyboard activation should time out.
            CheckMacPlayKeyboardTimeout();
            // Re-apply alpha=0 + ignoresMouseEvents on all NSWindows every
            // frame.  Play mode transitions or internal Unity code may
            // re-show or re-activate windows; this ensures they stay hidden.
            ReapplyWindowTransparency();
#endif
        }

#if UNITY_EDITOR_WIN
        [DllImport("user32.dll")]
        private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);


        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate,
            IntPtr hrgnUpdate, uint flags);
        // RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_ERASE | RDW_FRAME
        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_UPDATENOW = 0x0100;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_FRAME = 0x0400;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        private const uint GW_CHILD = 5;
        private const uint GW_HWNDNEXT = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        private const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll")]
        private static extern bool EndMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc, ref BITMAPINFOHEADER lpbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        private const uint PW_RENDERFULLCONTENT = 2;

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_CANCELMODE = 0x001F;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_MBUTTONDOWN = 0x0207;
        private const uint WM_MBUTTONUP = 0x0208;
        private const uint MK_LBUTTON = 0x0001;
        private const uint MK_RBUTTON = 0x0002;
        private const uint MK_MBUTTON = 0x0010;
        private const uint MAPVK_VK_TO_VSC = 0;

        // SendInput for injecting synthetic mouse movement when RIDEV_INPUTSINK
        // doesn't provide hardware delta (remote streaming scenario).
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
            // Pad to sizeof(INPUT)=40 on x64. KEYBDINPUT and HARDWAREINPUT
            // are smaller, but union size = max member size = MOUSEINPUT(32).
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001;


        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        // SendMessage is synchronous: it directly invokes the WndProc on
        // the calling thread and returns only after the WndProc finishes.
        // This lets us update the InputManager's mouse button state (which
        // happens inside WndProc for WM_RBUTTONDOWN) and IMMEDIATELY call
        // ReleaseCapture() before any message pump processing occurs.
        // PostMessage was asynchronous, leaving SetCapture active across
        // frames, causing editor freezes.
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // RegisterRawInputDevices — re-register mouse raw input with
        // RIDEV_INPUTSINK so the offscreen GameView receives WM_INPUT
        // even when the Unity Editor is not the foreground process.
        // Without this, Input.GetAxisRaw("Mouse X/Y") returns 0 because
        // the delta is sourced exclusively from WM_INPUT / RawInput, not
        // from WM_MOUSEMOVE (Tuanjie InputManager architecture).
        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(
            RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_REMOVE = 0x00000001;
        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(ref RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr lpRect);

        private const int SW_SHOW = 5;
        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 8;

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOZORDER = 0x0004;
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        // Cached list of Unity editor HWNDs that were made transparent.
        private static readonly List<IntPtr> s_TransparentHwnds = new List<IntPtr>();
        // Original WS_EX styles per HWND for proper restoration.
        private static readonly Dictionary<IntPtr, int> s_OriginalExStyles = new Dictionary<IntPtr, int>();
        // Original window positions saved before moving off-screen.
        // Used by the normal (non-PrintWindow) hiding strategy to restore
        // windows to their original positions when streaming stops.
        private static readonly Dictionary<IntPtr, RECT> s_OriginalWindowRects = new Dictionary<IntPtr, RECT>();

        // HWND of the streaming mask window, excluded from hide/push operations.
        private static IntPtr s_MaskWindowHwnd = IntPtr.Zero;

        // Foreground belonged to another process starting at this time (-1 = Unity/unknown).
        // Used so we only show the mask on a real taskbar activate (other app → Unity),
        // not on stream-start Unity FG churn.
        private static double s_OtherProcessFgSince = -1;
        private const double kTaskbarActivateMinOtherFgSec = 0.4;

        // Throttle the hidden-state refresh so we do not enumerate HWNDs on
        // every editor frame while offscreen streaming is active.
        private static double s_LastWindowTransparencyReapplyTime = 0;
        private const double kWindowTransparencyReapplyIntervalSec = 0.25;

        // HWND of the offscreen capture floating ContainerWindow and its
        // GUIView child. Used by PostNativeKeyMessage and SetFocus to route
        // keyboard events to the correct GameView for Input.GetKey support.
        private static IntPtr s_OffscreenTargetContainerHwnd = IntPtr.Zero;
        private static IntPtr s_OffscreenTargetGUIViewHwnd = IntPtr.Zero;

        // Timestamp of the last Play mode SetFocus call on Windows.
        // SetFocus briefly makes the HWND's parent the foreground window,
        // which triggers EnsureWindowsStayHiddenDuringOffscreen to reclaim
        // and show the mask window. During this grace period the foreground
        // check is skipped so the mask doesn't keep flashing.
        private static double s_WinPlayKeyLastFocusTime = 0;
        private const double kWinPlayKeyFocusGraceSec = 0.15;
        // Throttle SetFocus during key auto-repeat to reduce movement jitter.
        private const double kWinPlayKeyRepeatRefocusSec = 0.25;

        // Browser key names currently held on Windows (from DataChannel
        // keydown/keyup). Used to flush WM_KEYUP on mouseup so the Input
        // Manager does not retain stale pressed states (ghost movement).
        private static readonly HashSet<string> s_WinHeldKeys = new HashSet<string>();

        // Mouse buttons currently held via PostNativeMouseButtonMessage.
        // Flushed on stream disconnect to avoid ghost-held button states.
        private static readonly HashSet<int> s_WinHeldMouseButtons = new HashSet<int>();

        // Set after posting WM_xBUTTONDOWN; cleared by OffscreenUpdateLoop
        // calling ReleaseCapture to undo SetMouseCapture that the WndProc
        // applies in response, preventing potential frame stalls.
        private static bool s_NeedReleaseMouseCapture = false;

        // Tracks whether RIDEV_INPUTSINK is currently registered for the
        // GameView HWND and the last registration time (for throttling).
        private static bool s_RawInputSinkRegistered = false;
        private static double s_RawInputSinkLastRegTime = 0;
        private static IntPtr s_RawInputSinkHwnd = IntPtr.Zero;

        /// <summary>
        /// Register the GameView HWND for mouse raw input with RIDEV_INPUTSINK.
        /// This allows the offscreen GameView to receive WM_INPUT messages even
        /// when the Unity Editor is not the foreground process. Without this,
        /// Input.GetAxisRaw("Mouse X/Y") returns 0 because delta is sourced
        /// exclusively from WM_INPUT/RawInput (not from WM_MOUSEMOVE).
        /// Tuanjie's InputSetWindow (called on WM_SETFOCUS) re-registers without
        /// RIDEV_INPUTSINK, so we must periodically re-register.
        /// </summary>
        private static void EnsureRawInputSinkRegistered()
        {
            IntPtr hwnd = s_OffscreenTargetGUIViewHwnd != IntPtr.Zero
                ? s_OffscreenTargetGUIViewHwnd
                : s_OffscreenTargetContainerHwnd;
            if (hwnd == IntPtr.Zero) return;

            double now = EditorApplication.timeSinceStartup;
            // Re-register every 0.5s to counteract Tuanjie's own RegisterRawInputDevices
            // (triggered by SetFocus → WM_SETFOCUS → InputSetWindow → ToggleFullscreen)
            if (s_RawInputSinkRegistered && s_RawInputSinkHwnd == hwnd
                && (now - s_RawInputSinkLastRegTime) < 0.5)
                return;

            var rid = new RAWINPUTDEVICE[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = HID_USAGE_PAGE_GENERIC,
                    usUsage = HID_USAGE_GENERIC_MOUSE,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = hwnd
                }
            };
            bool ok = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            if (ok)
            {
                if (!s_RawInputSinkRegistered)
                    LogVerbose($"[NWB-RawInput] Registered RIDEV_INPUTSINK for hwnd=0x{hwnd.ToInt64():X}");
                s_RawInputSinkRegistered = true;
                s_RawInputSinkHwnd = hwnd;
                s_RawInputSinkLastRegTime = now;
            }
            else if (!s_RawInputSinkRegistered)
            {
                CodelyLogger.LogWarning($"[NWB-RawInput] Failed to register RIDEV_INPUTSINK for hwnd=0x{hwnd.ToInt64():X}");
            }
        }

        // Enumerate top-level windows belonging to the current process using
        // GetWindow instead of EnumWindows. EnumWindows internally invokes
        // callbacks that can load third-party shell extension DLLs (e.g.
        // BaiduNetdisk YunShellExt, OneDrive FileSyncShell, Adobe CoreSync)
        // whose DllMain may fail, causing STATUS_DLL_INIT_FAILED (0xC0000142)
        // — a native crash that C# try-catch cannot intercept.
        // GetWindow walks the window manager's internal linked list without
        // triggering any shell extension loading.
        private static List<IntPtr> GetAllUnityHwnds()
        {
            var result = new List<IntPtr>();
            uint currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

            IntPtr hwnd = GetWindow(GetDesktopWindow(), GW_CHILD);
            int safety = 0;
            while (hwnd != IntPtr.Zero && safety < 4096)
            {
                try
                {
                    if (IsWindow(hwnd) && IsWindowVisible(hwnd))
                    {
                        GetWindowThreadProcessId(hwnd, out uint pid);
                        if (pid == currentPid && hwnd != s_MaskWindowHwnd)
                            result.Add(hwnd);
                    }
                }
                catch
                {
                    // Keep iteration resilient even if a single HWND becomes invalid.
                }
                hwnd = GetWindow(hwnd, GW_HWNDNEXT);
                safety++;
            }

            return result;
        }

        /// <summary>
        /// Defer creating the streaming mask until the user activates Unity via the
        /// taskbar (see EnsureStreamingMaskShownForUser). Showing it at stream start
        /// steals Host mouse focus and skews GameView Play PathD.
        /// </summary>
        private static void DeferStreamingMaskWindow()
        {
            s_MaskWindowHwnd = IntPtr.Zero;
            s_OtherProcessFgSince = -1;
            CodelyLogger.Log("[NWB-Win] Streaming mask deferred until taskbar activate (bottom-right when shown)");
        }

        /// <summary>
        /// Create/show the mask at bottom-right and bring it to the foreground.
        /// Called when the user clicks the Editor taskbar button during offscreen streaming.
        /// </summary>
        private static void EnsureStreamingMaskShownForUser()
        {
            try
            {
                StreamingMaskWindow.ShowMask();
                IntPtr hwnd = ResolveStreamingMaskContainerHwnd();
                if (hwnd == IntPtr.Zero)
                {
                    // ShowUtility may bind the native handle on the next editor tick.
                    EditorApplication.delayCall += () =>
                    {
                        if (!s_OffscreenActive) return;
                        IntPtr delayed = ResolveStreamingMaskContainerHwnd();
                        if (delayed == IntPtr.Zero)
                        {
                            CodelyLogger.LogWarning("[NWB-Win] EnsureStreamingMaskShownForUser: HWND not detected");
                            return;
                        }
                        s_MaskWindowHwnd = delayed;
                        BringStreamingMaskToFront(delayed);
                    };
                    return;
                }

                s_MaskWindowHwnd = hwnd;
                BringStreamingMaskToFront(hwnd);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Win] EnsureStreamingMaskShownForUser failed: {ex.Message}");
            }
        }

        private static IntPtr ResolveStreamingMaskContainerHwnd()
        {
            var win = StreamingMaskWindow.TryGetInstance();
            if (win == null) return IntPtr.Zero;

            IntPtr handle = Cn.Tuanjie.Codely.Editor.EditorWindowNativeHandleHelper.GetGUIViewHandle(win);
            if (handle == IntPtr.Zero || !IsWindow(handle))
                handle = FindWindowW(null, StreamingMaskWindow.WindowTitle);
            if (handle == IntPtr.Zero || !IsWindow(handle))
                return IntPtr.Zero;

            IntPtr root = GetAncestor(handle, GA_ROOT);
            return root != IntPtr.Zero ? root : handle;
        }

        private static void BringStreamingMaskToFront(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;

            s_TransparentHwnds.Remove(hwnd);
            int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
            SetWindowLongW(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_TRANSPARENT);

            StreamingMaskWindow.ApplyBottomRightPosition(StreamingMaskWindow.TryGetInstance());
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
            LogVerbose($"[NWB-Win] Streaming mask shown for user, hwnd=0x{hwnd.ToInt64():X}");
        }

        /// <summary>
        /// Hide the streaming mask window and clear the tracked HWND.
        /// </summary>
        private static void HideStreamingMaskWindow()
        {
            try
            {
                StreamingMaskWindow.HideMask();
                s_MaskWindowHwnd = IntPtr.Zero;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Win] HideStreamingMaskWindow failed: {ex.Message}");
            }
        }

#endif


        /// <summary>
        /// Ensure the target EditorWindow is the active tab in its DockArea.
        /// When multiple windows share a tab group (e.g. GameView + SceneView),
        /// GrabPixels and SendEvent only work on the currently selected tab.
        /// This uses reflection to set the DockArea.selected index without
        /// calling Focus() which would bring Unity to the foreground.
        /// </summary>
        private static void EnsureActiveTabInDockArea(EditorWindow window)
        {
            if (window == null || s_DockAreaTabActivated) return;

            try
            {
                FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (parentField == null) return;

                object dockArea = parentField.GetValue(window);
                if (dockArea == null) return;

                System.Type dockAreaType = dockArea.GetType();

                // If not a DockArea (e.g. standalone window), compute offset from
                // the GUIView and mark as done.
                if (dockAreaType.Name != "DockArea")
                {
                    ComputeTabBarOffset(window, dockArea, dockAreaType);
                    s_DockAreaTabActivated = true;
                    return;
                }

                // Get the list of panes (EditorWindows) in this DockArea.
                FieldInfo panesField = dockAreaType.GetField("m_Panes",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (panesField == null) return;

                var panes = panesField.GetValue(dockArea) as System.Collections.IList;
                if (panes == null || panes.Count <= 1)
                {
                    // Even with a single tab, DockArea still has a tab bar that offsets
                    // the content area. Must compute the offset before returning.
                    ComputeTabBarOffset(window, dockArea, dockAreaType);
                    s_DockAreaTabActivated = true;
                    return;
                }

                int targetIndex = -1;
                for (int i = 0; i < panes.Count; i++)
                {
                    if ((panes[i] as UnityEngine.Object) == window) { targetIndex = i; break; }
                }
                if (targetIndex < 0) return;

                // Get current selected index and save it for restoration on stop.
                PropertyInfo selectedProp = dockAreaType.GetProperty("selected",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (selectedProp == null)
                {
                    FieldInfo selectedField = dockAreaType.GetField("m_Selected",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (selectedField != null)
                    {
                        int cur = (int)selectedField.GetValue(dockArea);
                        if (cur != targetIndex)
                        {
                            s_DockAreaInstance = dockArea;
                            s_OriginalDockAreaSelectedIndex = cur;
                            selectedField.SetValue(dockArea, targetIndex);
                            LogVerbose($"[NWB-Offscreen] Activated tab {targetIndex} in DockArea (field), saved original={cur}");
                        }
                    }
                }
                else
                {
                    int cur = (int)selectedProp.GetValue(dockArea);
                    if (cur != targetIndex)
                    {
                        s_DockAreaInstance = dockArea;
                        s_OriginalDockAreaSelectedIndex = cur;
                        selectedProp.SetValue(dockArea, targetIndex);
                        LogVerbose($"[NWB-Offscreen] Activated tab {targetIndex} in DockArea (prop), saved original={cur}");
                    }
                }

                // Calculate Y offset: the tab bar height between GUIView top and
                // EditorWindow content top. GrabPixels captures from GUIView top,
                // but SendEvent mousePosition is relative to EditorWindow content.
                ComputeTabBarOffset(window, dockArea, dockAreaType);

                s_DockAreaTabActivated = true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] EnsureActiveTab failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore the DockArea selected tab if we changed it during offscreen capture,
        /// then ensure the target window has proper focus for user interaction.
        /// </summary>
        private static void RestoreDockAreaTab()
        {
            EditorWindow target = s_OffscreenTarget;

            // If we changed the DockArea tab, restore the original selection.
            if (s_DockAreaInstance != null && s_OriginalDockAreaSelectedIndex >= 0)
            {
                try
                {
                    var dockAreaType = s_DockAreaInstance.GetType();
                    PropertyInfo selectedProp = dockAreaType.GetProperty("selected",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (selectedProp != null)
                    {
                        selectedProp.SetValue(s_DockAreaInstance, s_OriginalDockAreaSelectedIndex);
                        LogVerbose($"[NWB-Offscreen] Restored DockArea tab to original index={s_OriginalDockAreaSelectedIndex}");
                    }
                    else
                    {
                        FieldInfo selectedField = dockAreaType.GetField("m_Selected",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        if (selectedField != null)
                        {
                            selectedField.SetValue(s_DockAreaInstance, s_OriginalDockAreaSelectedIndex);
                            LogVerbose($"[NWB-Offscreen] Restored DockArea tab to original index={s_OriginalDockAreaSelectedIndex} (field)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"[NWB-Offscreen] RestoreDockAreaTab failed: {ex.Message}");
                }
            }
            else
            {
                LogVerbose("[NWB-Offscreen] No DockArea tab change to restore");
            }

            s_DockAreaInstance = null;
            s_OriginalDockAreaSelectedIndex = -1;
        }

        /// <summary>
        /// Release UIElements pointer and mouse capture on the target window's panel.
        /// During streaming, SendEvent injection passes through ProcessRetainedMode which
        /// lets UIElements process pointer events. If a MouseDown sets pointer capture on
        /// a VisualElement but the corresponding MouseUp fails to release it (e.g. because
        /// the stream was stopped mid-interaction), PointerCaptureDispatchingStrategy will
        /// intercept ALL subsequent OS mouse events with stopDispatch=true and
        /// propagateToIMGUI=false, completely blocking IMGUI toolbar interaction.
        /// </summary>
        private static void ReleaseUIElementsCapture(EditorWindow target)
        {
            try
            {
                // Method 1: Call GUIUtility.releaseCapture delegate which triggers
                // PointerCaptureHelper.ReleaseEditorMouseCapture() on the editor dispatcher.
                var releaseCaptureField = typeof(GUIUtility).GetField("releaseCapture",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (releaseCaptureField != null)
                {
                    var releaseAction = releaseCaptureField.GetValue(null) as Action;
                    if (releaseAction != null)
                    {
                        releaseAction.Invoke();
                        LogVerbose("[NWB-Offscreen] Released UIElements editor mouse capture via GUIUtility.releaseCapture");
                    }
                }

                // Method 2: Also release capture on the target window's panel directly.
                // This covers cases where the panel-local capture differs from the
                // editor-global dispatcher's capture state.
                if (target != null && target.rootVisualElement != null)
                {
                    var panel = target.rootVisualElement.panel;
                    if (panel != null)
                    {
                        // IPanel.ReleasePointer is an extension method in PointerCaptureHelper.
                        // Use reflection: panel.dispatcher.pointerState.ReleasePointer(0)
                        // PointerId.mousePointerId == 0
                        var dispatcherProp = panel.GetType().GetProperty("dispatcher",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        object dispatcher = dispatcherProp?.GetValue(panel);
                        if (dispatcher != null)
                        {
                            var pointerStateProp = dispatcher.GetType().GetProperty("pointerState",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            object pointerState = pointerStateProp?.GetValue(dispatcher);
                            if (pointerState != null)
                            {
                                // Check current capturing element for diagnostics
                                var getCaptMethod = pointerState.GetType().GetMethod("GetCapturingElement",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                object capElement = getCaptMethod?.Invoke(pointerState, new object[] { 0 });

                                if (capElement != null)
                                {
                                    LogVerbose($"[NWB-Offscreen] Panel has pointer capture on: {capElement.GetType().Name} — releasing");
                                }

                                // ReleasePointer(int pointerId)
                                var releaseMethod = pointerState.GetType().GetMethod("ReleasePointer",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                    null, new System.Type[] { typeof(int) }, null);
                                releaseMethod?.Invoke(pointerState, new object[] { 0 });

                                // ProcessPointerCapture to commit the release
                                var processMethod = pointerState.GetType().GetMethod("ProcessPointerCapture",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                    null, new System.Type[] { typeof(int) }, null);
                                processMethod?.Invoke(pointerState, new object[] { 0 });

                                if (capElement != null)
                                {
                                    LogVerbose("[NWB-Offscreen] Panel pointer capture released and processed");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] ReleaseUIElementsCapture failed: {ex.Message}");
            }
        }


        /// <summary>
        /// Repair GUIView native properties that may block input after offscreen streaming.
        /// SendEvent injection can leave disableInputEvents=true or mouseRayInvisible=true,
        /// which prevents the DockArea from receiving any mouse events.
        /// </summary>
        private static void RepairGUIViewNativeState(EditorWindow target)
        {
            if (target == null) return;
            try
            {
                FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (parentField == null) return;
                object guiView = parentField.GetValue(target);
                if (guiView == null) return;

                var gvType = guiView.GetType();

                var disableInputProp = gvType.GetProperty("disableInputEvents",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (disableInputProp != null)
                {
                    bool val = (bool)disableInputProp.GetValue(guiView);
                    if (val)
                    {
                        disableInputProp.SetValue(guiView, false);
                        LogVerbose("[NWB-Offscreen] FIXED: disableInputEvents was true, reset to false");
                    }
                }

                var mouseRayInvisProp = gvType.GetProperty("mouseRayInvisible",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mouseRayInvisProp != null)
                {
                    bool val = (bool)mouseRayInvisProp.GetValue(guiView);
                    if (val)
                    {
                        mouseRayInvisProp.SetValue(guiView, false);
                        LogVerbose("[NWB-Offscreen] FIXED: mouseRayInvisible was true, reset to false");
                    }
                }

                var clearKbdMethod = gvType.GetMethod("ClearKeyboardControl",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (clearKbdMethod != null)
                    clearKbdMethod.Invoke(guiView, null);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] RepairGUIViewNativeState failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore visual rendering state of the target EditorWindow after
        /// offscreen streaming.
        ///
        /// Root cause: during streaming, the GUIView's IMGUI state can become
        /// stale — toolbar toggle icons stop updating visually even though the
        /// underlying values toggle correctly (confirmed by PostStop diagnostic
        /// logs showing audioMasterMute correctly flipping on each click).
        ///
        /// The issue is that after stop, the GUIView's native rendering pipeline
        /// does not trigger a full on-screen redraw. Simply calling Repaint()
        /// schedules an async repaint, but RepaintImmediately() forces a
        /// synchronous full redraw through the native pipeline (including the
        /// platform buffer swap). We also install a short-lived EditorApplication
        /// .update callback that forces Repaint every frame for a few frames, to
        /// ensure any deferred state (GUILayout caches, hover state, etc.) gets
        /// fully flushed to screen.
        /// </summary>
        private static int s_RepairRepaintFramesLeft;
        private static EditorWindow s_RepairTarget;

        /// <summary>
        /// Fix black-screen caused by EditorWindow.m_Pos being displaced to
        /// extreme off-screen coordinates during offscreen streaming. When the
        /// HWND is moved off-screen for hiding, Unity's ContainerWindow may
        /// internally shift m_Pos far off-screen (e.g. -31741,-31970). After
        /// restoring the HWND, m_Pos remains stale and the DockArea renders
        /// nothing for this EditorWindow, causing the "black screen" effect.
        /// </summary>
        private static void RepairWindowPosition(EditorWindow target, Rect savedPos)
        {
            if (target == null) return;
            try
            {
                Rect curPos = target.position;
                // Detect abnormal position: x or y far off-screen (< -10000)
                if (curPos.x < -10000 || curPos.y < -10000)
                {
                    // Try to recover a sane position from the GUIView (parent)
                    // which tracks the actual native window rect.
                    FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    object guiView = parentField?.GetValue(target);
                    Rect fixedPos = curPos;

                    if (guiView != null)
                    {
                        PropertyInfo posProp = null;
                        var tt = guiView.GetType();
                        while (tt != null && posProp == null)
                        {
                            posProp = tt.GetProperty("position",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            tt = tt.BaseType;
                        }
                        if (posProp != null)
                        {
                            Rect gvPos = (Rect)posProp.GetValue(guiView);
                            // EditorWindow.position is relative to screen;
                            // GUIView.position is relative to the ContainerWindow.
                            // Use the GUIView rect as a base — the EditorWindow
                            // sits inside the GUIView at a small offset (tab bar).
                            fixedPos = new Rect(gvPos.x + 1, gvPos.y + 19,
                                curPos.width, curPos.height);
                        }
                    }

                    // If we saved a valid position before stop, prefer that.
                    if (savedPos.x > -10000 && savedPos.y > -10000 &&
                        savedPos.width > 0 && savedPos.height > 0)
                    {
                        fixedPos = savedPos;
                    }

                    LogVerbose($"[NWB-Repair] Fixing abnormal window position: " +
                        $"was=({curPos.x},{curPos.y}) fixed=({fixedPos.x},{fixedPos.y}) " +
                        $"size={curPos.width}x{curPos.height}");

                    // Set m_Pos directly to avoid EditorWindow.position setter
                    // side-effects (undocking, etc.)
                    var mPosField = typeof(EditorWindow).GetField("m_Pos",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (mPosField != null)
                        mPosField.SetValue(target, fixedPos);
                    else
                        target.position = fixedPos;

                    target.Repaint();
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Repair] RepairWindowPosition failed: {ex.Message}");
            }
        }

        private static void RepairVisualState(EditorWindow target)
        {
            if (target == null) return;
            try
            {
                FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (parentField == null) return;
                object guiView = parentField.GetValue(target);
                if (guiView == null) return;

                var gvType = guiView.GetType();

                // Diagnostic: dump critical IMGUI/GUIView state BEFORE repair.
                bool hasFocus = false;
                try
                {
                    PropertyInfo hasFocusProp = null;
                    var tt = gvType;
                    while (tt != null && hasFocusProp == null)
                    {
                        hasFocusProp = tt.GetProperty("hasFocus",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        tt = tt.BaseType;
                    }
                    if (hasFocusProp != null)
                        hasFocus = (bool)hasFocusProp.GetValue(guiView);
                }
                catch (Exception) { }

                Rect rootBound = Rect.zero;
                try
                {
                    if (target.rootVisualElement != null)
                        rootBound = target.rootVisualElement.worldBound;
                }
                catch (Exception) { }

                PropertyInfo posProp = null;
                Rect gvPos = Rect.zero;
                try
                {
                    var tt = gvType;
                    while (tt != null && posProp == null)
                    {
                        posProp = tt.GetProperty("position",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        tt = tt.BaseType;
                    }
                    if (posProp != null)
                        gvPos = (Rect)posProp.GetValue(guiView);
                }
                catch (Exception) { }

                LogVerbose($"[NWB-Repair] PRE-STATE: hotControl={GUIUtility.hotControl} " +
                    $"kbControl={GUIUtility.keyboardControl} hasFocus={hasFocus} " +
                    $"focusedWin={EditorWindow.focusedWindow?.GetType().Name ?? "null"} " +
                    $"rootVE.worldBound={rootBound} gvPos={gvPos} " +
                    $"targetPos={target.position} targetType={target.GetType().Name}");

                // CRITICAL: Clear hotControl. If streaming left a non-zero
                // hotControl, GetEventTypeForControl() converts all mouse events
                // to Ignore for any control whose id != hotControl — completely
                // blocking toolbar toggle/button interaction.
                if (GUIUtility.hotControl != 0)
                {
                    LogVerbose($"[NWB-Repair] Clearing residual hotControl={GUIUtility.hotControl}");
                    GUIUtility.hotControl = 0;
                }

                // Force a synchronous full redraw through the native pipeline.
                MethodInfo repaintImmediate = null;
                var t = gvType;
                while (t != null && repaintImmediate == null)
                {
                    repaintImmediate = t.GetMethod("RepaintImmediately",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, System.Type.EmptyTypes, null);
                    t = t.BaseType;
                }

                if (repaintImmediate != null)
                {
                    try { repaintImmediate.Invoke(guiView, null); }
                    catch (Exception) { }
                }

                // Also trigger a deferred Repaint for safety.
                target.Repaint();

                // Install a short-lived update callback that forces Repaint every
                // frame for several frames. This ensures toolbar toggle icons,
                // hover effects, and GUILayout caches are fully refreshed even if
                // the first RepaintImmediately runs before all state is committed.
                s_RepairTarget = target;
                s_RepairRepaintFramesLeft = 30;
                EditorApplication.update -= RepairRepaintTick;
                EditorApplication.update += RepairRepaintTick;

                LogVerbose("[NWB-Offscreen] RepairVisualState: RepaintImmediately + 30-frame repaint installed");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] RepairVisualState failed: {ex.Message}");
            }
        }

        private static void RepairRepaintTick()
        {
            if (s_RepairTarget == null || s_RepairRepaintFramesLeft <= 0)
            {
                EditorApplication.update -= RepairRepaintTick;
                s_RepairTarget = null;
                return;
            }
            s_RepairRepaintFramesLeft--;

            // Request a deferred repaint on the target window and its parent
            // GUIView (DockArea) to refresh both content and tab bar.
            s_RepairTarget.Repaint();
            try
            {
                FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                object guiView = parentField?.GetValue(s_RepairTarget);
                if (guiView != null)
                {
                    MethodInfo repaintMethod = guiView.GetType().GetMethod("Repaint",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, System.Type.EmptyTypes, null);
                    repaintMethod?.Invoke(guiView, null);
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Compute the pixel offset between the top of GUIView (what GrabPixels captures)
        /// and the top of EditorWindow content area (what SendEvent uses).
        /// This is typically the DockArea tab bar height (~18-21 pixels).
        /// </summary>
        private static void ComputeTabBarOffset(EditorWindow window, object dockArea, System.Type dockAreaType)
        {
            s_TabBarOffsetY = 0f;
            try
            {
                float borderTop = -1f;
                float screenDiff = -1f;

                // Method 1: Use DockArea.borderSize.top (only the tab strip itself).
                PropertyInfo borderProp = dockAreaType.GetProperty("borderSize",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (borderProp != null)
                {
                    object border = borderProp.GetValue(dockArea);
                    if (border != null)
                    {
                        PropertyInfo topProp = border.GetType().GetProperty("top");
                        if (topProp != null)
                            borderTop = (float)(int)topProp.GetValue(border);
                    }
                }

                // Method 2 (preferred): The difference in height between GUIView
                // (which includes tab bar) and EditorWindow (content only) gives
                // the exact pixel offset. This is robust regardless of coordinate origin.
                PropertyInfo viewPosProp = dockAreaType.GetProperty("screenPosition",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (viewPosProp != null)
                {
                    Rect viewPos = (Rect)viewPosProp.GetValue(dockArea);
                    Rect winPos = window.position;
                    float heightDiff = viewPos.height - winPos.height;
                    float yDiff = winPos.y - viewPos.y;
                    screenDiff = heightDiff;
                    LogVerbose($"[NWB-Offscreen] Tab offset debug: borderSize.top={borderTop} " +
                              $"viewPos=({viewPos.x},{viewPos.y},{viewPos.width},{viewPos.height}) " +
                              $"winPos=({winPos.x},{winPos.y},{winPos.width},{winPos.height}) " +
                              $"heightDiff={heightDiff} yDiff={yDiff}");
                }

                // Prefer the height diff if valid, otherwise fall back to borderSize.
                if (screenDiff > 0 && screenDiff < 100)
                {
                    s_TabBarOffsetY = screenDiff;
                    LogVerbose($"[NWB-Offscreen] Tab bar offset (height diff): {s_TabBarOffsetY}px");
                }
                else if (borderTop >= 0)
                {
                    s_TabBarOffsetY = borderTop;
                    LogVerbose($"[NWB-Offscreen] Tab bar offset (borderSize.top): {s_TabBarOffsetY}px");
                }
                else
                {
                    s_TabBarOffsetY = 19f;
                    LogVerbose($"[NWB-Offscreen] Tab bar offset (default): {s_TabBarOffsetY}px");
                }
            }
            catch (Exception ex)
            {
                s_TabBarOffsetY = 19f;
                CodelyLogger.LogWarning($"[NWB-Offscreen] ComputeTabBarOffset failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Start offscreen capture for the given EditorWindow type (e.g. "UnityEditor.GameView").
        /// The window does not need to be visible or undocked.
        /// When nativeAlreadyStarted=true (browser HTTP path), native offscreen mode is already active.
        /// When nativeAlreadyStarted=false (CLI path), this method activates native offscreen mode.
        /// </summary>
        public static bool StartOffscreenCapture(string windowTypeName, int fps = 30, int width = 0, int height = 0,
                                                  bool nativeAlreadyStarted = false)
        {
            if (!IsAvailable()) return false;

            // If already in offscreen mode (tab switch), clean up the old target
            // without restoring window visibility — keeps windows hidden throughout.
            bool isSwitching = s_OffscreenTarget != null;
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            bool reuseDetachedTarget = false;
#endif
            if (isSwitching)
            {
                if (s_NativeOffscreenDisconnectedAwaitingRestore &&
                    s_OffscreenTarget != null &&
                    string.Equals(s_OffscreenTarget.GetType().FullName, windowTypeName, StringComparison.Ordinal))
                {
                    // Reconnect path: keep the existing floating target alive so
                    // stream restart does not destroy/recreate the capture window.
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
                    reuseDetachedTarget = true;
#endif
                    LogVerbose($"[NWB-Offscreen] Reusing detached target: {windowTypeName}");
                }
                else
                {
                    LogVerbose($"[NWB-Offscreen] Switching target: {s_OffscreenTarget.GetType().FullName} → {windowTypeName}");
                    if (s_OffscreenAutoFloatingWindow)
                    {
                        try { s_OffscreenTarget.Close(); }
                        catch (Exception ex) { CodelyLogger.LogWarning($"[NWB-Offscreen] Close old floating failed: {ex.Message}"); }
                    }
                    RestoreKeyFocusAfterOffscreen();
                    s_OffscreenTarget = null;
                    s_OffscreenAutoFloatingWindow = false;
                    s_OffscreenOriginalDockedTarget = null;
                    s_OffscreenFloatingWindowInstanceId = 0;
                }
            }

            // Ensure no stale popup/panel state from a previous capture session.
            ClearFrontendPopupState();

#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            bool hidEditorWindowsForOffscreen = false;
            try
            {
                // Resolve the target EditorWindow type.
                // First try standard resolution, then search all loaded assemblies
                // (needed for third-party plugin window types).
                System.Type windowType = System.Type.GetType(windowTypeName);
                if (windowType == null)
                {
                    windowType = System.Type.GetType(windowTypeName + ",UnityEditor");
                }
                if (windowType == null)
                {
                    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        windowType = asm.GetType(windowTypeName);
                        if (windowType != null) break;
                    }
                }
                if (windowType == null)
                {
                    // Fallback: try matching by FullName in already-open EditorWindows.
                    foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
                    {
                        if (w != null && w.GetType().FullName == windowTypeName)
                        {
                            windowType = w.GetType();
                            break;
                        }
                    }
                    if (windowType == null)
                    {
                        CodelyLogger.LogError($"[NWB-Offscreen] Window type not found: {windowTypeName}");
                        return false;
                    }
                }

                EditorWindow target = null;
                foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
                {
                    if (w != null && w.GetType() == windowType)
                    {
                        target = w;
                        break;
                    }
                }

                if (reuseDetachedTarget &&
                    s_OffscreenTarget != null &&
                    s_OffscreenTarget.GetType() == windowType)
                {
                    target = s_OffscreenTarget;
                }

                if (target == null)
                {
                    // Try to create one if not found (e.g. GameView may not be open).
                    target = EditorWindow.GetWindow(windowType, false, null, false);
                }

                if (target == null)
                {
                    CodelyLogger.LogError($"[NWB-Offscreen] Could not find/create window: {windowTypeName}");
                    return false;
                }

                // Always create a dedicated floating capture window so:
                // 1) We never disturb the user's existing window layout (docked or floating).
                // 2) Resize requests can actually change source resolution.
                // 3) Stop always closes our window, leaving the user's windows intact.
                if (!reuseDetachedTarget)
                {
                    s_OffscreenAutoFloatingWindow = false;
                    s_OffscreenOriginalDockedTarget = null;
                    s_OffscreenFloatingWindowInstanceId = 0;
                    try
                    {
                        EditorWindow referenceTarget = target;

                        // Save reference target's position and docked state BEFORE
                        // closing it, because CloseExistingGameViewsForStreaming
                        // will destroy the docked GameView instance.
                        Rect savedRefPos = referenceTarget.position;
                        bool refWasDocked = IsWindowDocked(referenceTarget);

                        // Close existing docked GameView(s) BEFORE creating the floating
                        // window. The docked GameView's size info (PanelSettings.
                        // GetGameViewResolution) interferes with the floating view,
                        // causing UI compression if still present during creation.
                        if (windowType.Name == "GameView" && !s_ClosedDockedGameViewForStreaming)
                        {
                            CloseExistingGameViewsForStreaming(null);
                        }

                        EditorWindow floating = ScriptableObject.CreateInstance(windowType) as EditorWindow;
                        if (floating == null)
                        {
                            CodelyLogger.LogError("[NWB-Offscreen] Failed to create dedicated floating capture window");
                            return false;
                        }

                        // Frontend sends CSS pixels which map 1:1 to Unity logical
                        // points (DIPs). Do NOT divide by pixelsPerPoint — that
                        // shrinks the window on HiDPI displays (e.g. Windows 200%),
                        // making captured content appear zoomed/magnified.
                        Rect pos = savedRefPos;
                        float desiredW = Mathf.Max(width > 0 ? (float)width * DpiScaleFactor : pos.width, 320f * DpiScaleFactor);
                        float desiredH = Mathf.Max(height > 0 ? ((float)height - s_TabBarOffsetY) * DpiScaleFactor : pos.height, 240f * DpiScaleFactor);
                        Rect desiredRect = new Rect(pos.x + 20f, pos.y + 20f, desiredW, desiredH);
                        floating.position = desiredRect;
#if UNITY_EDITOR_WIN
                        // Snapshot HWNDs before Show() so we can detect the new
                        // ContainerWindow HWND created for this floating window.
                        var beforeShowHwnds = new HashSet<IntPtr>(GetAllUnityHwnds());
#endif
#if UNITY_EDITOR_OSX
                        // Snapshot NSWindow list before Show() so we can detect
                        // the new NSWindow created for the floating window.
                        var beforeShowNSWindows = new HashSet<IntPtr>(GetAllNSWindows());
#endif
                        floating.Show();
                        // Re-apply position AFTER Show() because Unity's window
                        // management may override the pre-Show position with saved
                        // layout values. This ensures the floating window starts at
                        // the requested capture resolution immediately.
                        floating.position = desiredRect;
                        floating.Focus();
#if UNITY_EDITOR_WIN
                        // Detect the newly created ContainerWindow HWND and its
                        // GUIView child for keyboard input injection targeting.
                        s_OffscreenTargetContainerHwnd = IntPtr.Zero;
                        s_OffscreenTargetGUIViewHwnd = IntPtr.Zero;
                        var afterShowHwnds = GetAllUnityHwnds();
                        foreach (var h in afterShowHwnds)
                        {
                            if (!beforeShowHwnds.Contains(h))
                            {
                                s_OffscreenTargetContainerHwnd = h;
                                IntPtr child = GetWindow(h, GW_CHILD);
                                if (child != IntPtr.Zero)
                                    s_OffscreenTargetGUIViewHwnd = child;
                                LogVerbose($"[NWB-Win] Captured floating window HWND=0x{h.ToInt64():X}, GUIView child=0x{(child != IntPtr.Zero ? child.ToInt64() : 0):X}");
                                break;
                            }
                        }
#endif
#if UNITY_EDITOR_OSX
                        // Detect the newly created NSWindow for keyboard focus
                        // injection (makeKeyWindow) during GameView Play mode.
                        s_OffscreenTargetNSWindow = IntPtr.Zero;
                        var afterShowNSWindows = GetAllNSWindows();
                        foreach (var nsw in afterShowNSWindows)
                        {
                            if (!beforeShowNSWindows.Contains(nsw))
                            {
                                s_OffscreenTargetNSWindow = nsw;
                                LogVerbose($"[NWB-ObjC] Captured floating window NSWindow={nsw}");
                                break;
                            }
                        }
#endif
                        target = floating;
                        s_OffscreenAutoFloatingWindow = true;
                        s_OffscreenOriginalDockedTarget = referenceTarget;
                        s_OffscreenFloatingWindowInstanceId = floating.GetStableInstanceId();
                        Rect actualPos = floating.position;
                        CodelyLogger.Log($"[NWB-Offscreen] Created dedicated floating capture window (ref was {(refWasDocked ? "docked" : "floating")}) " +
                                  $"desired=({desiredRect.width:F0}x{desiredRect.height:F0}) actual=({actualPos.width:F0}x{actualPos.height:F0})");
                    }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogError($"[NWB-Offscreen] Failed to create floating capture window: {ex.Message}");
                        return false;
                    }
                }
                else
                {
                    LogVerbose($"[NWB-Offscreen] Reuse existing floating capture window instanceId={target.GetStableInstanceId()}");
                }

                // Ensure the window has rendered at least once so the RT is populated.
                target.Repaint();

                s_OffscreenTarget = target;
                s_OffscreenTargetType = windowType;
                s_OffscreenFps = Mathf.Max(1, fps);
                s_OriginalTargetPosition = target.position;
                s_DockAreaTabActivated = false;
                s_DockAreaInstance = null;
                s_OriginalDockAreaSelectedIndex = -1;
                s_TabBarOffsetY = 0f;
                s_OffscreenFrameCount = 0;
                s_NextCaptureTime = 0;
                s_OffscreenResizeTargetActive = false;
                s_OffscreenResizeTargetWidth = 0;
                s_OffscreenResizeTargetHeight = 0;
                s_OffscreenResizeRetryFrames = 0;
#if UNITY_EDITOR_WIN
                s_GameViewLowResOriginalCaptured = false;
                s_GameViewLowResOriginalValue = false;
                s_GameViewLowResForcedOff = false;
                s_OffscreenForegroundHideDiagCount = 0;
                s_OtherProcessFgSince = -1;
#endif

                // Compute tab bar offset early so RT size includes the full GUIView.
                EnsureActiveTabInDockArea(target);
                TryPrepareGameViewLowResolutionForOffscreen(target);

                // Determine resolution: use GUIView.screenPosition (includes tab bar)
                // to get the full pixel size that GrabPixels will capture.
                if (width <= 0 || height <= 0)
                {
                    try
                    {
                        FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        if (parentField != null)
                        {
                            object guiView = parentField.GetValue(target);
                            if (guiView != null)
                            {
                                PropertyInfo screenPosProp = guiView.GetType().GetProperty("screenPosition",
                                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (screenPosProp != null)
                                {
                                    Rect screenPos = (Rect)screenPosProp.GetValue(guiView);
                                    width = Mathf.Max((int)screenPos.width, 320);
                                    height = Mathf.Max((int)screenPos.height, 240);
                                }
                            }
                        }
                    }
                    catch (Exception) { /* fallback below */ }

                    if (width <= 0 || height <= 0)
                    {
                        Rect pos = target.position;
                        width = Mathf.Max((int)pos.width, 320);
                        height = Mathf.Max((int)(pos.height + s_TabBarOffsetY), 240);
                    }
                }

                // Round capture dimensions to even values so that H.264/VP8
                // encoders do not need to pad, which avoids a subtle frame-size
                // mismatch between what Unity captures and what the browser decodes.
                width = (width + 1) & ~1;
                height = (height + 1) & ~1;

                // On Windows, scale to physical pixels for HiDPI displays. GrabPixels
                // operates on the GPU backbuffer which is at physical resolution.
                // Without this, at 150% DPI (ppp=1.5) only the top-left ~67% of
                // content is captured, making the stream appear zoomed/magnified.
                // On macOS, Metal handles backing scale internally so use logical size.
                int physWidth = (Mathf.RoundToInt(width * DPIScale) + 1) & ~1;
                int physHeight = (Mathf.RoundToInt(height * DPIScale) + 1) & ~1;

                // Create capture resources sized to the full GUIView (including tab bar).
                if (s_CaptureRT != null) { s_CaptureRT.Release(); UnityEngine.Object.DestroyImmediate(s_CaptureRT); }
                if (s_ReadbackTex != null) UnityEngine.Object.DestroyImmediate(s_ReadbackTex);

                // Platform-specific color space for capture RT and readback texture.
                //
                // Windows (D3D11): GrabPixels writes sRGB-encoded content into the RT.
                // Using an sRGB-aware RT would cause the GPU to apply an extra
                // linear→sRGB curve on write, double-encoding gamma (washed-out colors).
                // Linear RT avoids this by treating writes as raw bytes.
                //
                // macOS (Metal): GrabPixels already handles color space correctly.
                // Using Linear RT strips the sRGB encoding, making colors too dark.
                // Default (sRGB) RT preserves correct gamma on Mac.
#if UNITY_EDITOR_WIN
                var rtReadWrite = RenderTextureReadWrite.Linear;
                bool texLinear = true;
#else
                var rtReadWrite = RenderTextureReadWrite.Default;
                bool texLinear = false;
#endif
                s_CaptureRT = new RenderTexture(physWidth, physHeight, 0, RenderTextureFormat.BGRA32, rtReadWrite)
                {
                    name = "__NWB_OffscreenRT__",
                    useMipMap = false,
                    autoGenerateMips = false
                };
                s_CaptureRT.Create();

                s_ReadbackTex = new Texture2D(physWidth, physHeight, TextureFormat.BGRA32, false, texLinear);

                target.Repaint();

                // Activate native offscreen mode. The HTTP handler only stores
                // a pending request; NWB_StartOffscreenCapture must be called
                // to actually enable frame reception in the C++ encoder.
                // When nativeAlreadyStarted=true (domain reload restore), skip
                // this because the C++ singleton already has offscreen active.
                if (!nativeAlreadyStarted)
                {
                    int result = NativeWindowBridgeAPI.NWB_StartOffscreenCapture(fps, physWidth, physHeight);
                    if (result != 1)
                    {
                        CodelyLogger.LogError("[NWB-Offscreen] Native StartOffscreenCapture failed");
                        return false;
                    }
                }

                s_OffscreenActive = true;
                s_NativeOffscreenDisconnectedAwaitingRestore = false;

                // Predict whether GrabPixels will fail: in Gamma color
                // space, Unity has no AuxBackBufferManager intermediate RT,
                // so DoPaint→Present clears the SwapChain before
                // GrabPixels can read it. Enable fallback immediately to
                // avoid 3 frames of black.
                s_UseRenderTextureFallback =
                    (PlayerSettings.colorSpace == ColorSpace.Gamma);
                if (s_UseRenderTextureFallback)
                    CodelyLogger.Log("[NWB-Diag] Gamma color space detected — using m_RenderTexture fallback (no AuxBackBufferManager)");

                s_HasFrontendHeartbeat = false;
                s_LastFrontendHeartbeatTime = EditorApplication.timeSinceStartup;
                RegisterOffscreenLoop();

                // Minimize non-target utility windows so that macOS popup
                // window activation doesn't make them visible on the desktop.
                MinimizeNonTargetUtilityWindows();

#if UNITY_EDITOR_WIN
                // Force Layout+Repaint cycles at the correct capture resolution
                // BEFORE hiding windows. A Repaint-only SendEvent does NOT trigger
                // GameView.ConfigureTargetTexture() — that happens in the Layout
                // pass. Without Layout, m_TargetTexture keeps its old size from the
                // docked view, and game cameras render at the wrong aspect ratio
                // (e.g. 1411x505 instead of 1092x810), causing horizontal
                // compression of buildings.
                //
                // Two full Layout+Repaint cycles are needed:
                //   Pass 1: Layout recalculates targetRenderSize and calls
                //           ConfigureTargetTexture to recreate m_TargetTexture at
                //           the new size. Repaint renders — but the game player
                //           loop may still use stale Screen.width/height values.
                //   Pass 2: Layout is a no-op (size unchanged). Repaint renders
                //           with the correctly sized m_TargetTexture and updated
                //           Screen dimensions. This produces the first correctly
                //           proportioned frame.
                try
                {
                    // Ensure window is at the correct capture size before DoPaint.
                    // width/height are CSS pixels = Unity logical points (DIPs).
                    float wantW = Mathf.Max((float)width * DpiScaleFactor, 320f * DpiScaleFactor);
                    float wantH = Mathf.Max((float)height * DpiScaleFactor - s_TabBarOffsetY, 240f * DpiScaleFactor);
                    Rect curPos = target.position;
                    if (Mathf.Abs(curPos.width - wantW) > 2 || Mathf.Abs(curPos.height - wantH) > 2)
                    {
                        target.position = new Rect(curPos.x, curPos.y, wantW, wantH);
                        LogVerbose($"[NWB-Offscreen] Pre-DoPaint resize: was ({curPos.width:F0}x{curPos.height:F0}), now ({wantW:F0}x{wantH:F0})");
                    }

                    ForceDoubleLayoutRepaint(target);
                    LogVerbose($"[NWB-Offscreen] Forced initial Layout+Repaint x2 at capture resolution ({width}x{height})");
                }
                catch (Exception) { }
#endif

                // When switching targets, windows are already hidden — skip re-hiding
                // to avoid a visible flash (hide → unhide → re-hide cycle).
                if (!isSwitching)
                {
                    MakeAllWindowsTransparent();
                    hidEditorWindowsForOffscreen = true;
                    DeactivateUnityKeepWindows();
#if UNITY_EDITOR_OSX
                    ShowStreamingMaskWindow();
#endif
#if UNITY_EDITOR_WIN
                    DeferStreamingMaskWindow();
#endif
                }
                else
                {
                    hidEditorWindowsForOffscreen = true;
                }

                // Force runInBackground so the player loop keeps running
                // when the user switches to the Codely streaming app and
                // the editor loses OS focus. Without this, Time.frameCount
                // freezes and game logic (Update, coroutines, scene loads)
                // never executes.
                if (!s_RunInBackgroundOverridden)
                {
                    s_SavedRunInBackground = Application.runInBackground;
                    Application.runInBackground = true;
                    s_RunInBackgroundOverridden = true;
                }

                float ppp = EditorGUIUtility.pixelsPerPoint;
                Rect winPos = target.position;
                CodelyLogger.Log($"[NWB-Offscreen] Started: {windowTypeName} RT={physWidth}x{physHeight} logical={width}x{height} @{fps}fps " +
                          $"winPos=({winPos.x},{winPos.y},{winPos.width},{winPos.height}) " +
                          $"tabOff={s_TabBarOffsetY} ppp={ppp} " +
                          $"runInBG={Application.runInBackground} (was {s_SavedRunInBackground})");
                return true;
            }
            catch (Exception ex)
            {
                // Fail-safe cleanup: if start fails after visibility changes,
                // restore Unity UI state immediately.
                if (hidEditorWindowsForOffscreen)
                {
#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
                    HideStreamingMaskWindow();
#endif
                    RestoreAllWindowsVisible();
                }
                CodelyLogger.LogError($"[NWB-Offscreen] Start failed: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
#else
            return false;
#endif
        }

        private static bool ShouldReuseExistingCompositeWindow(string windowTypeName)
        {
            switch (windowTypeName)
            {
                case "UnityEditor.GameView":
                case "UnityEditor.SceneView":
                case "UnityEditor.InspectorWindow":
                case "UnityEditor.ProjectBrowser":
                case "UnityEditor.ConsoleWindow":
                case "UnityEditor.SceneHierarchyWindow":
                    return false;
                default:
                    return true;
            }
        }

        private static System.Type ResolveEditorWindowType(string windowTypeName)
        {
            if (string.IsNullOrEmpty(windowTypeName)) return null;
            System.Type windowType = System.Type.GetType(windowTypeName) ??
                                     System.Type.GetType(windowTypeName + ",UnityEditor");
            if (windowType != null) return windowType;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                windowType = asm.GetType(windowTypeName);
                if (windowType != null) return windowType;
            }
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (w != null && w.GetType().FullName == windowTypeName)
                    return w.GetType();
            }
            return null;
        }

        private static Rect ClampCompositeRect(CompositeRect rect)
        {
            if (rect == null)
                return new Rect(0f, 0f, 1f, 1f);
            float x = Mathf.Clamp01(rect.x);
            float y = Mathf.Clamp01(rect.y);
            float w = Mathf.Clamp(rect.w, 0.05f, 1f);
            float h = Mathf.Clamp(rect.h, 0.05f, 1f);
            if (x + w > 1f) w = 1f - x;
            if (y + h > 1f) h = 1f - y;
            return new Rect(x, y, Mathf.Max(w, 0.05f), Mathf.Max(h, 0.05f));
        }

        /// <summary>
        /// Re-acquire platform handles (HWND/NSWindow) for a composite slot
        /// whose EditorWindow survived domain reload. After reload C# statics
        /// are wiped but the Win32/Cocoa windows still exist.
        /// </summary>
        private static void ReacquireCompositeSlotHandles(CompositeCaptureSlot slot)
        {
            if (slot == null || slot.Window == null) return;
#if UNITY_EDITOR_WIN
            IntPtr previousContainer = slot.ContainerHwnd;
            IntPtr previousGUIView = slot.GUIViewHwnd;
            bool containerChanged = false;
            bool guiViewChanged = false;

            // Always re-query the current ContainerWindow HWND from the live EditorWindow.
            // Dock/undock/re-tab operations can migrate the window to a different
            // ContainerWindow while the slot keeps an old cached HWND.
            IntPtr latestContainer = Cn.Tuanjie.Codely.Editor.NativeWindowHelper.GetHWND(slot.Window);
            if (latestContainer != IntPtr.Zero)
            {
                bool currentInvalid = slot.ContainerHwnd == IntPtr.Zero || !IsWindow(slot.ContainerHwnd);
                bool candidateChanged = slot.ContainerHwnd != latestContainer;
                bool allowUpdate = currentInvalid || !candidateChanged || IsContainerHwndCompatibleWithCompositeSlot(slot, latestContainer);

                if (!allowUpdate)
                {
                    LogVerbose($"[NWB-Composite] Skip suspicious handle switch for slot '{slot.SlotId}': " +
                        $"0x{slot.ContainerHwnd.ToInt64():X} -> 0x{latestContainer.ToInt64():X}");
                }
                else if (slot.ContainerHwnd == IntPtr.Zero || slot.ContainerHwnd != latestContainer || !IsWindow(slot.ContainerHwnd))
                {
                    slot.ContainerHwnd = latestContainer;
                    containerChanged = slot.ContainerHwnd != previousContainer;

                    IntPtr child = GetWindow(latestContainer, GW_CHILD);
                    if (child != IntPtr.Zero)
                    {
                        slot.GUIViewHwnd = child;
                        guiViewChanged = slot.GUIViewHwnd != previousGUIView;
                    }
                }
            }
            else if (slot.ContainerHwnd != IntPtr.Zero && !IsWindow(slot.ContainerHwnd))
            {
                slot.ContainerHwnd = IntPtr.Zero;
                containerChanged = slot.ContainerHwnd != previousContainer;
            }

            if (slot.GUIViewHwnd == IntPtr.Zero || !IsWindow(slot.GUIViewHwnd) || containerChanged)
            {
                IntPtr reflected = Cn.Tuanjie.Codely.Editor.EditorWindowNativeHandleHelper.GetGUIViewHandle(slot.Window);
                if (reflected != IntPtr.Zero)
                {
                    slot.GUIViewHwnd = reflected;
                    guiViewChanged = slot.GUIViewHwnd != previousGUIView;
                }
            }
            if (slot.ContainerHwnd != IntPtr.Zero)
            {
                int style = GetWindowLongW(slot.ContainerHwnd, GWL_EXSTYLE);
                if ((style & WS_EX_LAYERED) != 0)
                    SetWindowLongW(slot.ContainerHwnd, GWL_EXSTYLE, style & ~WS_EX_LAYERED);
            }
            if (containerChanged || guiViewChanged)
            {
                LogVerbose($"[NWB-Composite] Refreshed slot '{slot.SlotId}' handles: " +
                    $"container 0x{previousContainer.ToInt64():X} -> 0x{slot.ContainerHwnd.ToInt64():X}, " +
                    $"guiView 0x{previousGUIView.ToInt64():X} -> 0x{slot.GUIViewHwnd.ToInt64():X}");
            }
#endif
#if UNITY_EDITOR_OSX
            if (slot.NSWindow == IntPtr.Zero)
            {
                foreach (CompositeCaptureSlot other in s_CompositeSlots.Values)
                {
                    if (other != null && other != slot && other.NSWindow != IntPtr.Zero)
                    {
                        slot.NSWindow = other.NSWindow;
                        break;
                    }
                }
            }
#endif
        }

#if UNITY_EDITOR_WIN
        /// <summary>
        /// Validate candidate container HWND against the slot's live parent view size.
        /// This prevents transient layout states from rebinding a slot to an unrelated
        /// narrow container (which later causes PrintWindow crop/stretch artifacts).
        /// </summary>
        private static bool IsContainerHwndCompatibleWithCompositeSlot(CompositeCaptureSlot slot, IntPtr candidateContainerHwnd)
        {
            if (slot == null || slot.Window == null || candidateContainerHwnd == IntPtr.Zero)
                return false;
            if (!GetClientRect(candidateContainerHwnd, out RECT clientRect))
                return true;

            object parentView = GetParentView(slot.Window);
            if (parentView == null)
                return true;

            Rect viewRect = GetViewScreenPosition(parentView);
            if (viewRect.width <= 1f || viewRect.height <= 1f)
                return true;

            float expectedW = Mathf.Max(viewRect.width * LocalPP, 1f);
            float expectedH = Mathf.Max(viewRect.height * LocalPP, 1f);
            float actualW = Mathf.Max(clientRect.Right, 1f);
            float actualH = Mathf.Max(clientRect.Bottom, 1f);

            float widthDelta = Mathf.Abs(actualW - expectedW);
            float heightDelta = Mathf.Abs(actualH - expectedH);
            float widthTolerance = Mathf.Max(96f, expectedW * 0.25f);
            float heightTolerance = Mathf.Max(96f, expectedH * 0.25f);

            return widthDelta <= widthTolerance && heightDelta <= heightTolerance;
        }
#endif

        /// <summary>
        /// Get the type name of the currently active window in the composite
        /// DockArea. Used to save the active tab before Play mode.
        /// </summary>
        private static string GetCompositeActiveWindowTypeName()
        {
            foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
            {
                if (slot?.Window == null) continue;
                try
                {
                    FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (parentField == null) break;
                    object dockArea = parentField.GetValue(slot.Window);
                    if (dockArea == null || dockArea.GetType().Name != "DockArea") break;
                    System.Type dockAreaType = dockArea.GetType();

                    PropertyInfo selectedProp = dockAreaType.GetProperty("selected",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    FieldInfo panesField = dockAreaType.GetField("m_Panes",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (selectedProp == null || panesField == null) break;

                    int sel = (int)selectedProp.GetValue(dockArea);
                    var panes = panesField.GetValue(dockArea) as System.Collections.IList;
                    if (panes == null || sel < 0 || sel >= panes.Count) break;

                    var activeWin = panes[sel] as EditorWindow;
                    if (activeWin != null)
                        return activeWin.GetType().FullName;
                }
                catch (Exception) { }
                break;
            }
            return null;
        }

        /// <summary>
        /// Activate the GameView (PlayModeView) tab in the composite DockArea.
        /// Mirrors Tuanjie's InitPlaymodeLayout → playModeView.Focus() behavior.
        /// </summary>
        private static void ActivateCompositeGameViewTab()
        {
            foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
            {
                if (slot?.Window == null) continue;
                string typeName = slot.Window.GetType().FullName;
                if (typeName == "UnityEditor.GameView"
                    || typeof(EditorWindow).IsAssignableFrom(slot.Window.GetType())
                       && slot.Window.GetType().Name == "GameView")
                {
                    slot.Window.ShowTab();
                    LogVerbose($"[NWB-Composite] Activated GameView tab on Play mode enter");
                    return;
                }
            }
        }

        /// <summary>
        /// Restore a composite tab by its type name. Used when exiting Play
        /// mode to switch back to the window the user had active before Play.
        /// Mirrors Tuanjie's TryGetLastFocusedWindowInSameDock → ShowTab().
        /// </summary>
        private static void RestoreCompositeTabByTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return;
            foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
            {
                if (slot?.Window == null) continue;
                if (slot.Window.GetType().FullName == typeName)
                {
                    slot.Window.ShowTab();
                    LogVerbose($"[NWB-Composite] Restored pre-play tab '{typeName}' on Edit mode enter");
                    return;
                }
            }
        }

        /// <summary>
        /// Send two rounds of Layout+Repaint to an EditorWindow so it
        /// initializes its IMGUI state and GPU AuxBackBuffer. Pass 1
        /// triggers ConfigureTargetTexture; pass 2 renders with the
        /// correctly sized m_TargetTexture. Silently catches exceptions
        /// from internal IMGUI assertions in off-screen windows.
        /// </summary>
        private static void ForceDoubleLayoutRepaint(EditorWindow window)
        {
            if (window == null) return;
            try
            {
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
            }
            catch (Exception) { }
        }

        private static void ResizeCompositeSlot(CompositeCaptureSlot slot, int logicalW, int logicalH, CompositeRect rect)
        {
            if (slot == null || slot.Window == null) return;
            Rect nr = ClampCompositeRect(rect);
            float slotW = Mathf.Max(logicalW * DpiScaleFactor, 320f * DpiScaleFactor);
            float slotH = Mathf.Max(logicalH * DpiScaleFactor - s_TabBarOffsetY, 240f * DpiScaleFactor);
            Rect pos = slot.Window.position;
            object currentHostWindow = GetCompositeHostWindow();
            object slotHostWindow = GetViewWindow(GetParentView(slot.Window));
            bool isDockedInCompositeWorkspace = currentHostWindow != null && object.ReferenceEquals(currentHostWindow, slotHostWindow);
            if (!isDockedInCompositeWorkspace && (Mathf.Abs(pos.width - slotW) > 2f || Mathf.Abs(pos.height - slotH) > 2f))
                slot.Window.position = new Rect(pos.x, pos.y, slotW, slotH);

            int physW = (Mathf.RoundToInt(slotW * LocalPP) + 1) & ~1;
            int physH = (Mathf.RoundToInt((slotH + s_TabBarOffsetY) * LocalPP) + 1) & ~1;
#if UNITY_EDITOR_WIN
            var rtReadWrite = RenderTextureReadWrite.Linear;
#else
            var rtReadWrite = RenderTextureReadWrite.Default;
#endif
            if (slot.SourceRT == null || slot.SourceRT.width != physW || slot.SourceRT.height != physH)
            {
                if (slot.SourceRT != null)
                {
                    slot.SourceRT.Release();
                    UnityEngine.Object.DestroyImmediate(slot.SourceRT);
                }
                slot.SourceRT = new RenderTexture(physW, physH, 0, RenderTextureFormat.BGRA32, rtReadWrite)
                {
                    name = "__NWB_CompositeSlotRT__",
                    useMipMap = false,
                    autoGenerateMips = false
                };
                slot.SourceRT.Create();
            }
            slot.NormalizedRect = nr;
        }

        private static void ReleaseCompositeSlot(CompositeCaptureSlot slot)
        {
            if (slot == null) return;
            if (slot.SourceRT != null)
            {
                if (RenderTexture.active == slot.SourceRT)
                    RenderTexture.active = null;
                slot.SourceRT.Release();
                UnityEngine.Object.DestroyImmediate(slot.SourceRT);
                slot.SourceRT = null;
            }
            if ((slot.OwnsWindow || slot.CloseWindowOnRelease) && slot.Window != null)
            {
                if (slot.OwnsWindow && s_IsReleasingComposite)
                {
                    // During ReleaseCompositeState (stop streaming), close the entire
                    // ContainerWindow to ensure the Win32 HWND is destroyed. This
                    // unifies behavior across all layout paths — EditorWindow.Close()
                    // alone may leave the ContainerWindow's HWND alive as a zombie
                    // when the DockArea's parent structure doesn't trigger KillIfEmpty.
                    // During ApplyCompositeLayout (slot replacement), s_IsReleasingComposite
                    // is false, so only EditorWindow.Close() is used.
                    try
                    {
                        object parentView = GetParentView(slot.Window);
                        object containerWindow = parentView != null ? GetViewWindow(parentView) : null;
                        if (containerWindow != null)
                        {
                            MethodInfo closeMethod = containerWindow.GetType().GetMethod("Close",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (closeMethod != null)
                                closeMethod.Invoke(containerWindow, null);
                            else
                                slot.Window.Close();
                        }
                        else
                        {
                            slot.Window.Close();
                        }
                    }
                    catch (Exception)
                    {
                        try { slot.Window.Close(); } catch (Exception) { }
                    }
                }
                else
                {
                    try { slot.Window.Close(); }
                    catch (Exception) { }
                }
            }
            slot.Window = null;
        }

        private static object GetParentView(EditorWindow window)
        {
            if (window == null) return null;
            FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return parentField?.GetValue(window);
        }

        private static object GetViewWindow(object view)
        {
            if (view == null) return null;
            PropertyInfo windowProp = view.GetType().GetProperty("window",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return windowProp?.GetValue(view);
        }

        private static Rect GetViewScreenPosition(object view)
        {
            if (view == null) return Rect.zero;
            PropertyInfo screenPosProp = view.GetType().GetProperty("screenPosition",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (screenPosProp == null) return Rect.zero;
            return (Rect)screenPosProp.GetValue(view);
        }

        private static string JsonFloat(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static Rect NormalizeCompositeViewRect(Rect viewRect, Rect bounds)
        {
            if (bounds.width <= 1f || bounds.height <= 1f)
                return new Rect(0f, 0f, 1f, 1f);

            return new Rect(
                Mathf.Clamp01((viewRect.xMin - bounds.xMin) / bounds.width),
                Mathf.Clamp01((viewRect.yMin - bounds.yMin) / bounds.height),
                Mathf.Clamp01(viewRect.width / bounds.width),
                Mathf.Clamp01(viewRect.height / bounds.height));
        }

        private static void SendCompositeLayoutSnapshot()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            if (!s_CompositeActive || s_CompositeSlots == null || s_CompositeSlots.Count == 0)
            {
                SendDataChannelMessage("{\"type\":\"composite_layout_snapshot\",\"slots\":[]}");
                return;
            }

            try
            {
                var slotViews = new Dictionary<CompositeCaptureSlot, object>();
                var visitedViews = new HashSet<object>();
                Rect bounds = Rect.zero;
                bool hasBounds = false;

                object rootSplit = GetRootSplitView(GetCompositeHostWindow());
                if (rootSplit != null)
                {
                    Rect rootRect = GetViewScreenPosition(rootSplit);
                    if (rootRect.width > 1f && rootRect.height > 1f)
                    {
                        bounds = rootRect;
                        hasBounds = true;
                    }
                }

                foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
                {
                    if (slot?.Window == null) continue;
                    object view = GetParentView(slot.Window);
                    if (view == null) continue;
                    slotViews[slot] = view;

                    if (!visitedViews.Add(view)) continue;
                    Rect viewRect = GetViewScreenPosition(view);
                    if (viewRect.width <= 1f || viewRect.height <= 1f) continue;
                    bounds = hasBounds ? Rect.MinMaxRect(
                        Mathf.Min(bounds.xMin, viewRect.xMin),
                        Mathf.Min(bounds.yMin, viewRect.yMin),
                        Mathf.Max(bounds.xMax, viewRect.xMax),
                        Mathf.Max(bounds.yMax, viewRect.yMax)) : viewRect;
                    hasBounds = true;
                }

                if (!hasBounds)
                {
                    SendDataChannelMessage("{\"type\":\"composite_layout_snapshot\",\"slots\":[]}");
                    return;
                }

                var sb = new StringBuilder();
                sb.Append("{\"type\":\"composite_layout_snapshot\",\"slots\":[");
                bool first = true;
                foreach (var pair in slotViews)
                {
                    CompositeCaptureSlot slot = pair.Key;
                    object view = pair.Value;
                    Rect viewRect = GetViewScreenPosition(view);
                    if (viewRect.width <= 1f || viewRect.height <= 1f) continue;
                    Rect nr = NormalizeCompositeViewRect(viewRect, bounds);
                    bool isActive = IsCompositeSlotActiveInDockArea(slot);

                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"slotId\":\"").Append(EscapeJsonString(slot.SlotId ?? "")).Append("\"");
                    sb.Append(",\"windowType\":\"").Append(EscapeJsonString(slot.WindowTypeName ?? slot.Window.GetType().FullName)).Append("\"");
                    sb.Append(",\"rect\":{");
                    sb.Append("\"x\":").Append(JsonFloat(nr.x));
                    sb.Append(",\"y\":").Append(JsonFloat(nr.y));
                    sb.Append(",\"w\":").Append(JsonFloat(nr.width));
                    sb.Append(",\"h\":").Append(JsonFloat(nr.height));
                    sb.Append("}");
                    if (isActive) sb.Append(",\"active\":true");
                    sb.Append("}");
                }
                sb.Append("]}");
                SendDataChannelMessage(sb.ToString());
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Composite] Failed to send layout snapshot: {ex.Message}");
                SendDataChannelMessage("{\"type\":\"composite_layout_snapshot\",\"slots\":[]}");
            }
#endif
        }

        private static bool IsCompositeSlotActiveInDockArea(CompositeCaptureSlot slot)
        {
            if (slot?.Window == null) return false;
            try
            {
                object dockArea = GetParentView(slot.Window);
                if (dockArea == null || dockArea.GetType().Name != "DockArea")
                    return true;

                FieldInfo panesField = dockArea.GetType().GetField("m_Panes",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object panesObj = panesField?.GetValue(dockArea);
                if (!(panesObj is System.Collections.IList panes)) return false;
                int index = panes.IndexOf(slot.Window);
                if (index < 0) return false;
                if (panes.Count == 1) return true;

                int selected = -1;
                PropertyInfo selectedProp = dockArea.GetType().GetProperty("selected",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (selectedProp != null)
                {
                    selected = (int)selectedProp.GetValue(dockArea);
                }
                else
                {
                    FieldInfo selectedField = dockArea.GetType().GetField("m_Selected",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (selectedField != null)
                        selected = (int)selectedField.GetValue(dockArea);
                }

                return selected == index;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static EditorWindow GetActualViewFromHostView(object hostView)
        {
            if (hostView == null) return null;
            PropertyInfo actualProp = hostView.GetType().GetProperty("actualView",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (actualProp != null)
                return actualProp.GetValue(hostView) as EditorWindow;

            FieldInfo actualField = hostView.GetType().GetField("m_ActualView",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return actualField?.GetValue(hostView) as EditorWindow;
        }

        private static object GetCompositeHostWindow()
        {
            foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
            {
                if (slot?.Window == null) continue;
                object hostWindow = GetViewWindow(GetParentView(slot.Window));
                if (hostWindow != null) return hostWindow;
            }
            return null;
        }

        private static object GetRootSplitView(object hostWindow)
        {
            if (hostWindow == null) return null;
            PropertyInfo rootSplitProp = hostWindow.GetType().GetProperty("rootSplitView",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return rootSplitProp?.GetValue(hostWindow);
        }

        private static void ResizeCompositeWorkspaceToCanvas()
        {
            if (s_CompositeLogicalWidth <= 0 || s_CompositeLogicalHeight <= 0)
                return;

            // Find the single ContainerWindow from any valid slot.
            object hostWindow = null;
            foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
            {
                if (slot?.Window == null) continue;
                object parentView = GetParentView(slot.Window);
                if (parentView == null) continue;
                hostWindow = GetViewWindow(parentView);
                if (hostWindow != null) break;
            }
            if (hostWindow == null) return;

            try
            {
                PropertyInfo positionProp = hostWindow.GetType().GetProperty("position",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (positionProp == null) return;

                Rect pos = (Rect)positionProp.GetValue(hostWindow);
                float targetW = Mathf.Max(s_CompositeLogicalWidth * DpiScaleFactor, 320f * DpiScaleFactor);
                float targetH = Mathf.Max(s_CompositeLogicalHeight * DpiScaleFactor, 240f * DpiScaleFactor);
                bool resized = false;
                if (Mathf.Abs(pos.width - targetW) > 2f || Mathf.Abs(pos.height - targetH) > 2f)
                {
                    positionProp.SetValue(hostWindow, new Rect(pos.x, pos.y, targetW, targetH));

                    // Re-read position after set. For offscreen windows at (-32000,-32000),
                    // the position setter may not stick — Unity internally clamps or rejects
                    // the resize. In that case, skip Reflow to avoid layout thrashing on
                    // the wrong (old) size, which causes visual shaking.
                    Rect afterSet = (Rect)positionProp.GetValue(hostWindow);
                    resized = Mathf.Abs(afterSet.width - targetW) <= 2f && Mathf.Abs(afterSet.height - targetH) <= 2f;
                }

                object rootSplit = GetRootSplitView(hostWindow);
                if (resized)
                    s_CompositeWorkspaceReflowPending = true;

                // Always allow Reflow — even during mousedown. Skipping Reflow
                // during mousedown causes ContainerWindow size to diverge from
                // RootSplitView layout, creating a size mismatch between
                // sourceRT (actual view) and composite RT (canvas target).
                if (rootSplit != null && s_CompositeWorkspaceReflowPending)
                {
                    PropertyInfo viewPositionProp = rootSplit.GetType().GetProperty("position",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    viewPositionProp?.SetValue(rootSplit, new Rect(0, 0, targetW, targetH));
                    rootSplit.GetType().GetMethod("Reflow", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.Invoke(rootSplit, null);
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Composite] Resize workspace failed: {ex.Message}");
            }
            s_CompositeWorkspaceReflowPending = false;
        }

        /// <summary>
        /// Release only RenderTextures and C# references, keeping
        /// EditorWindows alive so they survive domain reload via
        /// Unity serialization. DockArea.m_Selected is [SerializeField]
        /// and preserves the active tab automatically.
        /// </summary>
        private static void ReleaseCompositeRenderTargets()
        {
            CancelCompositeLayoutOperation();
            // Release per-slot RenderTextures but NOT the windows
            foreach (var slot in s_CompositeSlots.Values)
            {
                if (slot == null) continue;
                if (slot.SourceRT != null)
                {
                    if (RenderTexture.active == slot.SourceRT)
                        RenderTexture.active = null;
                    try
                    {
                        slot.SourceRT.Release();
                        UnityEngine.Object.DestroyImmediate(slot.SourceRT);
                    }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogError($"[NWB-Composite] ReleaseCompositeRenderTargets: slot '{slot.SlotId}' RT release failed: {ex.Message}");
                    }
                    slot.SourceRT = null;
                }
            }

            if (RenderTexture.active == s_CompositeRT)
                RenderTexture.active = null;
            if (s_CompositeRT != null)
            {
                s_CompositeRT.Release();
                UnityEngine.Object.DestroyImmediate(s_CompositeRT);
                s_CompositeRT = null;
            }
            if (RenderTexture.active == s_CompositePaneDragRT)
                RenderTexture.active = null;
            if (s_CompositePaneDragRT != null)
            {
                s_CompositePaneDragRT.Release();
                UnityEngine.Object.DestroyImmediate(s_CompositePaneDragRT);
                s_CompositePaneDragRT = null;
            }
            if (s_CompositeReadbackTex != null)
            {
                UnityEngine.Object.DestroyImmediate(s_CompositeReadbackTex);
                s_CompositeReadbackTex = null;
            }

            s_CompositeActive = false;
            s_CompositeLogicalWidth = 0;
            s_CompositeLogicalHeight = 0;
            s_CompositeFrameBoundsLogical = Rect.zero;
            s_CompositeFrameOffsetPixels = Vector2.zero;
            s_CompositeFrameScalePixels = Vector2.one;
            s_CompositeWorkspaceReflowPending = false;
            s_CompositePlayModeInitDone = false;
            s_CompositeMouseCaptureView = null;
            s_CompositeMouseCaptureSlot = null;
            s_CompositeLastTabTarget = null;
            s_OffscreenTarget = null;
            s_OffscreenTargetType = null;
            s_CaptureRT = null;
        }

        /// <summary>
        /// Minimize all ContainerWindows with showMode=2 (Utility) that are
        /// not hosting the current offscreen target. On macOS, when a native
        /// showMode=1 popup is briefly created (even if closed immediately),
        /// the OS may activate the Unity application, making all non-minimized
        /// windows visible. Minimizing utility windows prevents this.
        /// </summary>
        private static void MinimizeNonTargetUtilityWindows()
        {
            try
            {
                System.Type cwType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ContainerWindow");
                if (cwType == null) return;

                PropertyInfo showModeProp = cwType.GetProperty("showMode",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                MethodInfo minimizeMethod = cwType.GetMethod("Minimize",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (showModeProp == null || minimizeMethod == null) return;

                // Find the ContainerWindow hosting the offscreen target so
                // we can skip it.
                object targetCW = null;
                if (s_OffscreenTarget != null)
                {
                    FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    object parentView = parentField?.GetValue(s_OffscreenTarget);
                    if (parentView != null)
                    {
                        PropertyInfo windowProp = parentView.GetType().GetProperty("window",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        targetCW = windowProp?.GetValue(parentView);
                    }
                }

                var allContainers = Resources.FindObjectsOfTypeAll(cwType);
                foreach (var cw in allContainers)
                {
                    if (cw == null || object.ReferenceEquals(cw, targetCW)) continue;
                    int showMode = (int)showModeProp.GetValue(cw);
                    // Minimize Utility(2), Tooltip(6), ModalUtility(7) windows.
                    if (showMode == 2 || showMode == 6 || showMode == 7)
                    {
                        minimizeMethod.Invoke(cw, null);
                        LogVerbose($"[NWB-Offscreen] Minimized utility ContainerWindow: showMode={showMode}");
                    }
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] MinimizeNonTargetUtilityWindows failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset native offscreen mode without running full C# window/UI cleanup.
        /// Used when domain-reload restore fails and native/C# state is out of sync.
        /// </summary>
        private static void ForceResetNativeOffscreenState()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            try
            {
                if (NativeWindowBridgeAPI.NWB_IsOffscreenActive() == 1)
                {
                    NativeWindowBridgeAPI.NWB_StopCapture();
                    CodelyLogger.Log("[NWB] ForceResetNativeOffscreenState: native offscreen cleared");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB] ForceResetNativeOffscreenState error: {ex.Message}");
            }
#endif
#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
            HideStreamingMaskWindow();
#endif
            s_OffscreenActive = false;
            s_NativeOffscreenDisconnectedAwaitingRestore = false;
        }

        private static bool ViewTreeHasDockPanes(object view)
        {
            if (view == null) return false;

            try
            {
                if (view.GetType().Name == "DockArea")
                {
                    FieldInfo panesField = view.GetType().GetField("m_Panes",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    object panesObj = panesField?.GetValue(view);
                    if (panesObj is System.Collections.IList panes && panes.Count > 0)
                    {
                        for (int i = 0; i < panes.Count; i++)
                        {
                            if (panes[i] is EditorWindow)
                                return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore reflection failures and continue recursive scan.
            }

            foreach (object child in GetViewChildren(view))
            {
                if (ViewTreeHasDockPanes(child))
                    return true;
            }

            return false;
        }

        private static void CloseOrphanEmptyContainerWindows()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            try
            {
                System.Type cwType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ContainerWindow");
                if (cwType == null) return;

                PropertyInfo showModeProp = cwType.GetProperty("showMode",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo rootViewField = cwType.GetField("m_RootView",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo closeMethod = cwType.GetMethod("Close",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (showModeProp == null || rootViewField == null || closeMethod == null)
                    return;

                int closedCount = 0;
                object[] allContainers = Resources.FindObjectsOfTypeAll(cwType);
                foreach (object cw in allContainers)
                {
                    if (cw == null) continue;

                    int showMode;
                    try { showMode = (int)showModeProp.GetValue(cw); }
                    catch { continue; }

                    // Keep main/utility/popup containers untouched.
                    // We only cleanup normal aux containers that became empty.
                    if (showMode != 0) continue;

                    object rootView = rootViewField.GetValue(cw);
                    bool hasPanes = ViewTreeHasDockPanes(rootView);
                    if (hasPanes) continue;

                    try
                    {
                        closeMethod.Invoke(cw, null);
                        closedCount++;
                    }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogWarning($"[NWB-Offscreen] Failed to close orphan ContainerWindow: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }

                if (closedCount > 0)
                    CodelyLogger.Log($"[NWB-Offscreen] Closed {closedCount} orphan empty ContainerWindow(s) before restore");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] CloseOrphanEmptyContainerWindows failed: {ex.Message}");
            }
#endif
        }

        public static void StopOffscreenCapture()
        {
            if (s_IsStoppingOffscreen)
                return;
            s_IsStoppingOffscreen = true;

            try
            {

            s_NativeOffscreenDisconnectedAwaitingRestore = false;
            // Notify the browser to stop streaming BEFORE tearing down native
            // capture. This sets capturing=false in the browser so it does NOT
            // auto-reconnect when the WebRTC connection drops.
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            try { SendDataChannelMessage("{\"type\":\"stream_stop\"}"); }
            catch (Exception) { }
#endif

            // Stop native offscreen mode AFTER the stream_stop message so the
            // data channel has a chance to flush before the WebRTC connection
            // is torn down.
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            if (s_OffscreenActive || NativeWindowBridgeAPI.NWB_IsOffscreenActive() == 1)
            {
                try { NativeWindowBridgeAPI.NWB_StopCapture(); }
                catch (Exception ex) { CodelyLogger.LogWarning($"[NWB] NWB_StopCapture failed: {ex.Message}"); }
            }
#endif

            // Restore the original runInBackground setting now that
            // streaming has stopped and the editor can resume its normal
            // background behavior.
            if (s_RunInBackgroundOverridden)
            {
                Application.runInBackground = s_SavedRunInBackground;
                s_RunInBackgroundOverridden = false;
                CodelyLogger.Log($"[NWB-Offscreen] Restored runInBackground={s_SavedRunInBackground}");
            }

            TryRestoreGameViewLowResolutionAfterOffscreen(s_OffscreenTarget);
#if UNITY_EDITOR_WIN
            // CRITICAL: During streaming, RestoreGameCameraAspects() explicitly
            // sets Camera.aspect on all game cameras, which switches them to
            // manual mode (m_ImplicitAspect=false in native Camera). When the
            // docked GameView resumes rendering, OnWindowSizeHasChanged →
            // WindowSizeHasChanged skips ResetAspect() because m_ImplicitAspect
            // is false, leaving cameras stuck at the floating window's aspect
            // ratio. Call ResetAspect() on all cameras to restore auto mode.
            try
            {
                foreach (Camera cam in Camera.allCameras)
                {
                    if (cam.targetTexture != null) continue;
                    cam.ResetAspect();
                }
            }
            catch (Exception) { }
#endif
            // Clear any lingering popup/panel state so it doesn't block
            // input events when a different window is captured next.
            ClearFrontendPopupState();
            ClearEditorCursorState(notifyFrontend: false);
            UnregisterSelectionChangeListener();

            // Check if a docked GameView exists BEFORE ReleaseCompositeState()
            // clears s_CompositeSlots and s_CompositeActive. We need to know
            // if the user's original docked GameView was closed during streaming
            // so we can restore it. The s_ClosedDockedGameViewForStreaming flag
            // may be stale (false) after PreserveExisting domain reloads, so
            // also check if any non-composite GameView exists.
            bool wasComposite = s_CompositeActive;
            bool closedDockedGameView = s_ClosedDockedGameViewForStreaming;
            if (!closedDockedGameView && wasComposite)
            {
                var gvTypeCheck = ResolveEditorWindowType("UnityEditor.GameView");
                if (gvTypeCheck != null)
                {
                    var compositeSlotIds = new HashSet<long>();
                    if (s_CompositeSlots != null)
                    {
                        foreach (var slot in s_CompositeSlots.Values)
                        {
                            if (slot?.Window != null)
                                compositeSlotIds.Add(slot.Window.GetStableInstanceId());
                        }
                    }

                    bool anyNonCompositeGV = false;
                    foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
                    {
                        if (w == null || w.GetType() != gvTypeCheck) continue;
                        if (compositeSlotIds.Contains(w.GetStableInstanceId())) continue;
                        anyNonCompositeGV = true;
                        break;
                    }
                    if (!anyNonCompositeGV)
                    {
                        closedDockedGameView = true;
                        CodelyLogger.Log("[NWB-Offscreen] RestoreDockedGameView: no non-composite GameView found, forcing restore");
                    }
                }
            }

            if (wasComposite)
            {
#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
                // Close the streaming mask window BEFORE ReleaseCompositeState.
                // ReleaseCompositeState closes ContainerWindows, which can trigger
                // StreamingMaskWindow.OnDestroy. If the mask is still "alive" at
                // that point, OnDestroy sees s_ClosingFromCode=false and
                // IsOffscreenActive=true, scheduling a duplicate StopOffscreenCapture.
                HideStreamingMaskWindow();
#endif
                ReleaseCompositeState();
            }
            else
            {
#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
                HideStreamingMaskWindow();
#endif
            }

            // Some Unity internal docking transitions can leave behind a normal
            // ContainerWindow whose DockArea has no panes (empty black window).
            // Close these orphans before Win32 visibility/style restoration.
            CloseOrphanEmptyContainerWindows();

            // CRITICAL: Restore the original key-focus delegate BEFORE
            // clearing s_OriginalKeyFocusFunc. Without this, GUIUtility
            // keeps the () => true override, causing all IMGUI controls
            // to think they have keyboard focus and breaking mouse input.
            RestoreKeyFocusAfterOffscreen();
            LogVerbose($"[NWB-Offscreen] Restored key focus delegate on stop (wasSaved={s_OriginalKeyFocusSaved} origWasNull={s_OriginalKeyFocusFunc == null})");

            // Clear IMGUI global state that may have been left over from
            // SendEvent injection during streaming. A non-zero hotControl
            // causes GetEventTypeForControl() to convert all mouse events
            // to Ignore for controls whose id != hotControl — completely
            // blocking toolbar toggle/button interaction after stop.
            if (GUIUtility.hotControl != 0)
            {
                LogVerbose($"[NWB-Offscreen] Clearing residual hotControl={GUIUtility.hotControl} on stop");
                GUIUtility.hotControl = 0;
            }
            if (GUIUtility.keyboardControl != 0)
            {
                LogVerbose($"[NWB-Offscreen] Clearing residual keyboardControl={GUIUtility.keyboardControl} on stop");
                GUIUtility.keyboardControl = 0;
            }

            // Log window position at stop time for black-screen diagnostics.
            if (s_OffscreenTarget != null)
            {
                Rect stopPos = s_OffscreenTarget.position;
                CodelyLogger.Log($"[NWB-Offscreen] Stop: targetPos=({stopPos.x},{stopPos.y},{stopPos.width},{stopPos.height})" +
                    $" savedPos=({s_OriginalTargetPosition.x},{s_OriginalTargetPosition.y})");
            }

            // CRITICAL FIX: Release UIElements pointer/mouse capture.
            // During streaming, SendEvent injection flows through both UIElements
            // (ProcessRetainedMode) and IMGUI. If a MouseDown was injected but the
            // matching MouseUp didn't properly release UIElements capture, the panel's
            // PointerCaptureDispatchingStrategy will intercept ALL subsequent OS mouse
            // events (stopDispatch=true, propagateToIMGUI=false), preventing IMGUI
            // DoGUI from ever executing for mouse events after stop.
            ReleaseUIElementsCapture(s_OffscreenTarget);

            // If we changed the DockArea tab, restore the original selection.
            RestoreDockAreaTab();

            // Repair GUIView native properties that may have been corrupted
            // by SendEvent injection during offscreen streaming.
            RepairGUIViewNativeState(s_OffscreenTarget);

            // Close the auto-created floating capture window synchronously
            // before restoring editor visibility. Deferring this to delayCall
            // can make RestoreAllWindowsVisible briefly show the floating
            // window, or leave it behind if repair code throws first.
            bool hadAutoFloating = s_OffscreenAutoFloatingWindow;
            EditorWindow savedOriginalDocked = s_OffscreenOriginalDockedTarget;
            EditorWindow savedTarget = s_OffscreenTarget;
            Rect savedTargetPos = s_OriginalTargetPosition;

#if UNITY_EDITOR_OSX
            IntPtr closingNSWindow = hadAutoFloating ? s_OffscreenTargetNSWindow : IntPtr.Zero;
#endif

            if (hadAutoFloating)
            {
                try
                {
                    EditorWindow closeTarget = s_OffscreenTarget;
                    long floatingInstanceId = s_OffscreenFloatingWindowInstanceId;
                    if (closeTarget == null && floatingInstanceId != 0)
                    {
                        foreach (var win in Resources.FindObjectsOfTypeAll<EditorWindow>())
                        {
                            if (win != null && win.GetStableInstanceId() == floatingInstanceId)
                            {
                                closeTarget = win;
                                break;
                            }
                        }
                    }

                    if (closeTarget != null)
                    {
                        closeTarget.Close();
                        CodelyLogger.Log("[NWB-Offscreen] Closed auto-created floating capture window");
                    }
                    else
                    {
                        CodelyLogger.LogWarning("[NWB-Offscreen] Auto-created floating window close skipped: target already gone");
                    }
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"[NWB-Offscreen] Failed to close auto-created floating window: {ex.Message}");
                }
            }

            // Restore all windows to full visibility. On macOS, exclude the
            // closing floating window's NSWindow if it is still present in
            // [NSApp windows] during the same runloop pass.
            //
            // Start a time-limited background #32768 monitor BEFORE restoring
            // windows. After RestoreAllWindowsVisible, the next C++ DoRepaint
            // cycle calls ShowDelayedContextMenu. If gDelayedContextMenu is
            // set, TrackPopupMenuEx blocks the main thread. This monitor
            // dismisses it from a background thread.
#if UNITY_EDITOR_WIN
            ScheduleTimeLimitedPopupDismiss(5000);
#endif
#if UNITY_EDITOR_OSX
            RestoreAllWindowsVisible(closingNSWindow);
#else
            RestoreAllWindowsVisible();
#endif

            long savedFloatingInstanceId = hadAutoFloating ? s_OffscreenFloatingWindowInstanceId : 0;
            EditorApplication.delayCall += () =>
            {
                if (hadAutoFloating)
                {
                    if (savedFloatingInstanceId != 0)
                    {
                        foreach (var win in Resources.FindObjectsOfTypeAll<EditorWindow>())
                        {
                            if (win != null && win.GetStableInstanceId() == savedFloatingInstanceId)
                            {
                                try
                                {
                                    win.Close();
                                    CodelyLogger.LogWarning("[NWB-Offscreen] Safety net: re-closed surviving floating capture window");
                                }
                                catch (Exception) { }
                                break;
                            }
                        }
                    }

                    if (savedOriginalDocked != null)
                    {
                        try
                        {
                            savedOriginalDocked.Focus();
                            RepairVisualState(savedOriginalDocked);
                        }
                        catch (Exception) { }
                    }
                }
                else
                {
                    EditorWindow liveTarget = savedTarget;
                    if (liveTarget != null)
                    {
                        // Fix black-screen: detect if EditorWindow.position was displaced
                        // to an extreme off-screen location during streaming.
                        RepairWindowPosition(liveTarget, savedTargetPos);

                        liveTarget.Focus();

                        // Verify focus was actually granted. If another window still holds
                        // focus (e.g. ProjectBrowser), retry after a short delay.
                        if (EditorWindow.focusedWindow != liveTarget)
                        {
                            LogVerbose($"[NWB-Repair] Focus() did not take effect, focusedWin={EditorWindow.focusedWindow?.GetType().Name ?? "null"}, retrying...");
                            EditorApplication.delayCall += () =>
                            {
                                if (liveTarget == null) return;
                                liveTarget.Focus();
                                liveTarget.Repaint();
                            };
                        }

                        RepairVisualState(liveTarget);
                    }
                }

                // Restore docked GameView if we closed it during streaming.
                if (closedDockedGameView)
                    RestoreDockedGameViewAfterStreaming();
            };

            s_OffscreenActive = false;
            s_OffscreenTarget = null;
            s_OffscreenTargetType = null;
            s_OffscreenFrameCount = 0;
            s_CompositeLastViewCount = 0;
            s_InputEventCount = 0;
            s_ActionEventCount = 0;
            s_DockAreaTabActivated = false;
            s_KeyFocusDelegateResolved = false;
            s_OriginalKeyFocusFunc = null;
            s_OriginalKeyFocusSaved = false;
            s_TextEditorFieldsResolved = false;
            s_SearchFieldTextField = null;
            s_IsRemoteConnection = false;
            s_RemoteDeltaLogCount = 0;
#if UNITY_EDITOR_WIN
            s_OffscreenTargetContainerHwnd = IntPtr.Zero;
            s_OffscreenTargetGUIViewHwnd = IntPtr.Zero;
            s_WinPlayKeyLastFocusTime = 0;
            s_WinHeldKeys.Clear();
            ClearInputSystemMouseState();
            s_RawInputSinkRegistered = false;
            s_RawInputSinkHwnd = IntPtr.Zero;
            // Re-register raw input WITHOUT RIDEV_INPUTSINK, matching Tuanjie's default
            // registration (dwFlags=0). RIDEV_REMOVE was previously used but it permanently
            // destroys the raw input registration for the application —
            // RegisterRawInputDevices replaces ALL prior registrations for the same usage
            // page+usage, so RIDEV_REMOVE wipes Tuanjie's default registration too.
            // Tuanjie only re-registers on WM_SETFOCUS (InputSetWindow), which may not
            // fire after streaming stops, leaving the app with NO raw input registration.
            // Using dwFlags=0 with hwndTarget=NULL restores the default behavior safely.
            {
                var reRegRid = new RAWINPUTDEVICE[]
                {
                    new RAWINPUTDEVICE
                    {
                        usUsagePage = HID_USAGE_PAGE_GENERIC,
                        usUsage = HID_USAGE_GENERIC_MOUSE,
                        dwFlags = 0,
                        hwndTarget = IntPtr.Zero
                    }
                };
                RegisterRawInputDevices(reRegRid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                LogVerbose("[NWB-RawInput] Re-registered raw input WITHOUT RIDEV_INPUTSINK (dwFlags=0)");
            }
            // Restore InputSystem device state. During streaming, OnFocusChanged(false)
            // disabled the Mouse device (DisabledWhileInBackground) and may have cleared
            // native hooks. This forces OnFocusChanged(true) to re-enable devices.
            // Recover InputSystem: domain reload if in Edit mode (fully resets native
            // state), or LegacyMouseBridge + deferred reload if in Play mode.
            RecoverInputSystemAfterStreaming();
            ReleasePrintWindowDIB();
            if (s_PwTex != null) { UnityEngine.Object.DestroyImmediate(s_PwTex); s_PwTex = null; }
            s_PwBuffer = null;
            s_CursorPosBeforeLockSaved = false;
            if (s_CursorClipActive)
            {
                ClipCursor(IntPtr.Zero);
                s_CursorClipActive = false;
            }
            s_CursorTrackingValid = false;
#endif
#if UNITY_EDITOR_OSX
            s_OffscreenTargetNSWindow = IntPtr.Zero;
            s_MacPlayKeyboardActive = false;
            s_MacPlayKeyLastTime = 0;
            s_MacHeldKeys.Clear();
            s_MacHeldMouseButtons.Clear();
            s_MacKeyReinjectionFrames = 0;
#endif
            s_SetSearchMethod = null;
            s_SetSearchMethodResolved = false;
            s_AnnotationSearchFilter = "";
            s_TabBarOffsetY = 0f;
            s_HideCallCount = 0;
            s_RemoteMouseButtonDown = false;
            s_RemoteNativeDragAndDropActive = false;
            s_LastMousePos = Vector2.zero;
            s_HasLastMousePos = false;
            s_NativeOffscreenDisconnectedAwaitingRestore = false;
            s_OffscreenScenePanLocked = false;
            s_ClientDPR = 0f;
            s_OverlayConsumedMouseDown = false;
            s_HasFrontendHeartbeat = false;
            s_LastFrontendHeartbeatTime = 0;
            s_GameViewStylesReflectionDone = false;
            s_GameViewStylesType = null;
            s_GameViewToolbarTooltipLogged = false;
            s_GameViewToolbarLayoutDetected = false;
            s_GameViewToolbarDiagCount = 0;
            s_OffscreenAutoFloatingWindow = false;
            s_OffscreenOriginalDockedTarget = null;
            s_ClosedDockedGameViewForStreaming = false;
            s_OffscreenResizeTargetActive = false;
            s_OffscreenResizeTargetWidth = 0;
            s_OffscreenResizeTargetHeight = 0;
            s_OffscreenResizeRetryFrames = 0;
#if UNITY_EDITOR_WIN
            s_GameViewLowResOriginalCaptured = false;
            s_GameViewLowResOriginalValue = false;
            s_GameViewLowResForcedOff = false;
            s_CachedTargetTextureField = null;
            s_CachedTargetTextureFieldResolved = false;
#endif
            s_OffscreenFloatingWindowInstanceId = 0;
#if UNITY_EDITOR_WIN
            s_OffscreenForegroundHideDiagCount = 0;
            s_OtherProcessFgSince = -1;
#endif

            // Clear RenderTexture.active before releasing to avoid Unity warning.
            if (RenderTexture.active == s_CaptureRT)
                RenderTexture.active = null;
            if (s_CaptureRT != null) { s_CaptureRT.Release(); UnityEngine.Object.DestroyImmediate(s_CaptureRT); s_CaptureRT = null; }
            if (s_ReadbackTex != null) { UnityEngine.Object.DestroyImmediate(s_ReadbackTex); s_ReadbackTex = null; }

            CodelyLogger.Log("[NWB-Offscreen] Stopped — visual repair scheduled via delayCall");

#if UNITY_EDITOR_WIN
            // Final verification and auto-repair: check all Unity HWNDs have
            // clean styles AND are not stuck off-screen.
            // Style check: domain reload may corrupt saved state.
            // Position check: cumulative restore failures can leave HWNDs
            // at the -32000 sentinel even when style was properly restored.
            try
            {
                var verifyHwnds = GetAllUnityHwnds();
                foreach (var hwnd in verifyHwnds)
                {
                    int style = GetWindowLongW(hwnd, GWL_EXSTYLE);
                    bool stillLayered = (style & WS_EX_LAYERED) != 0;
                    bool stillTransparent = (style & WS_EX_TRANSPARENT) != 0;
                    if (stillLayered || stillTransparent)
                    {
                        CodelyLogger.LogWarning($"[NWB-Win] POST-STOP WARNING: hwnd=0x{hwnd:X} still has layered={stillLayered} transparent={stillTransparent} style=0x{style:X8}, auto-repairing...");
                        int cleanedStyle = style & ~WS_EX_TRANSPARENT & ~WS_EX_LAYERED;
                        SetWindowLongW(hwnd, GWL_EXSTYLE, cleanedStyle);
                        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                            RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_ERASE | RDW_FRAME);
                    }

                    RepairOffscreenSentinel(hwnd, "POST-STOP AUTO-REPAIR");
                }
            }
            catch (Exception) { }
#endif

            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"[NWB-Stop] StopOffscreenCapture failed: {ex.Message}");
            }
            finally
            {
                s_IsStoppingOffscreen = false;
            }
        }

        /// <summary>
        /// Disconnect native/WebRTC offscreen capture but keep Unity hidden and keep
        /// current floating views alive. This is used by browser "stop streaming"
        /// semantics where restore must be user-triggered from the mask UI.
        /// </summary>
        public static void DisconnectOffscreenKeepHidden()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            if (!s_OffscreenActive)
                return;

            try
            {
                if (NativeWindowBridgeAPI.NWB_IsOffscreenActive() == 1)
                    NativeWindowBridgeAPI.NWB_StopCapture();
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] DisconnectOffscreenKeepHidden stop native failed: {ex.Message}");
            }

            s_NativeOffscreenDisconnectedAwaitingRestore = true;
            EnsureWindowsStayHiddenDuringOffscreen();
            CodelyLogger.Log("[NWB-Offscreen] Disconnected capture, editor stays hidden until Disconnect & Restore");
#endif
        }

        private static void RegisterOffscreenLoop()
        {
            if (s_OffscreenLoopRegistered) return;
            s_OffscreenLoopRegistered = true;
            EditorApplication.update += OffscreenUpdateLoop;
        }

        /// <summary>
        /// EditorApplication.update callback: captures frames and polls input.
        /// </summary>
        private static void OffscreenUpdateLoop()
        {
            if (!s_OffscreenActive) return;

#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            try
            {
                // Detect if native side stopped offscreen mode (e.g. browser clicked Stop).
                if (NativeWindowBridgeAPI.NWB_IsOffscreenActive() == 0)
                {
                    if (!s_NativeOffscreenDisconnectedAwaitingRestore)
                    {
                        s_NativeOffscreenDisconnectedAwaitingRestore = true;
                        CodelyLogger.Log("[NWB-Offscreen] Native offscreen mode ended, keeping editor hidden until user clicks Disconnect & Restore");
                    }
                    // Keep reapplying hidden state while waiting for explicit restore.
                    EnsureWindowsStayHiddenDuringOffscreen();
                    return;
                }
                if (s_NativeOffscreenDisconnectedAwaitingRestore)
                {
                    s_NativeOffscreenDisconnectedAwaitingRestore = false;
                    CodelyLogger.Log("[NWB-Offscreen] Native offscreen mode resumed from detached-hidden state");
                }

                // If the floating capture window was closed externally (for example,
                // cowork window closed without sending Stop), terminate offscreen
                // capture immediately instead of re-binding to an arbitrary window.
                if (!s_CompositeActive && s_OffscreenTarget == null)
                {
                    CodelyLogger.LogWarning("[NWB-Offscreen] Target window closed externally, stopping offscreen capture");
                    StopOffscreenCapture();
                    return;
                }

                // Keep Unity hidden if it gets re-activated from taskbar while
                // offscreen streaming is still running.
                EnsureWindowsStayHiddenDuringOffscreen();

#if UNITY_EDITOR_WIN
                if (s_NeedReleaseMouseCapture)
                {
                    try { ReleaseCapture(); } catch (Exception) { }
                    s_NeedReleaseMouseCapture = false;
                }

#endif

                // --- Dynamic resize from frontend ---
                if (NativeWindowBridgeAPI.NWB_GetPendingResize(out int newW, out int newH, out float newDpr) == 1)
                {
                    if (newDpr > 0f) s_ClientDPR = newDpr;
                    if (s_CompositeActive)
                    {
                        EnsureCompositeRenderTargets(newW, newH);
                        CodelyLogger.Log($"[NWB-Composite] Resize requested: {newW}x{newH}");
                    }
                    else
                    {
                        // Round UP to even dimensions. H.264 MFT and VP8 require
                        // even width/height (NV12/I420 chroma subsampling). Odd
                        // values cause the encoder to pad +1, producing a slightly
                        // larger frame that the browser then stretches into the
                        // container, introducing subtle distortion.
                        s_OffscreenResizeTargetWidth = Mathf.Max((newW + 1) & ~1, 2);
                        s_OffscreenResizeTargetHeight = Mathf.Max((newH + 1) & ~1, 2);
                        s_OffscreenResizeRetryFrames = kOffscreenResizeRetryMaxFrames;
                        s_OffscreenResizeTargetActive = true;
                        if (newW != s_OffscreenResizeTargetWidth || newH != s_OffscreenResizeTargetHeight)
                            CodelyLogger.Log($"[NWB-Offscreen] Resize requested: {newW}x{newH} -> aligned {s_OffscreenResizeTargetWidth}x{s_OffscreenResizeTargetHeight} (even)");
                        else
                            CodelyLogger.Log($"[NWB-Offscreen] Resize requested: {s_OffscreenResizeTargetWidth}x{s_OffscreenResizeTargetHeight}");
                    }
                }

                if (!s_CompositeActive && s_OffscreenResizeTargetActive)
                {
                    ApplyPendingOffscreenResizeTarget();
                    // EditorWindow.position setter moves the HWND back on-screen.
                    // Immediately re-hide to prevent WM_WINDOWPOSCHANGED cascade
                    // when the first mouse event later moves it back to -32000,-32000.
                    ReapplyWindowTransparency();
                }
                else if (!s_CompositeActive)
                {
                    // Guard against unexpected size drift after toolbar/canvas clicks:
                    // if Unity shrinks the floating GameView, re-activate resize settling.
                    EnsureOffscreenSizeStability();
                    // Apply immediately in the same update tick to avoid one-frame
                    // visual mismatch (flash) between GUIView and capture RT.
                    if (s_OffscreenResizeTargetActive)
                    {
                        ApplyPendingOffscreenResizeTarget();
                        ReapplyWindowTransparency();
                    }
                }

                // --- Frame capture ---
                double now = EditorApplication.timeSinceStartup;
                double interval = 1.0 / s_OffscreenFps;
                if (now >= s_NextCaptureTime)
                {
                    s_NextCaptureTime = now + interval;
                    CaptureAndPushFrame();
                }

                // --- Input polling ---
                PollAndInjectInput();

#if UNITY_EDITOR_WIN
                // --- Raw input sink registration ---
                // Ensure the offscreen GameView HWND receives WM_INPUT in background.
                // Must be re-registered periodically because SetFocus (called during
                // keyboard input injection) triggers WM_SETFOCUS → InputSetWindow →
                // RegisterRawInputDevices without RIDEV_INPUTSINK, overwriting ours.
                if (EditorApplication.isPlaying && !EditorApplication.isPaused)
                {
                    // Keep RIDEV_INPUTSINK registered so both local hardware
                    // WM_INPUT and remote SendInput-generated WM_INPUT reach
                    // the offscreen GameView. Remote vs local is determined
                    // by the frontend's ICE candidate-pair report.
                    EnsureRawInputSinkRegistered();
                }
#endif

#if UNITY_EDITOR_WIN
                // Track OS cursor position every frame while NOT in Locked
                // mode. This must run BEFORE the lock state detection below.
                // On the frame the game enters Locked, Tuanjie has already
                // called SetCursorPos (clamped to 0,0) before this callback.
                // By using the tracked value from the previous frame, we get
                // the real pre-lock position instead of (0,0).
                if (EditorApplication.isPlaying && !EditorApplication.isPaused
                    && global::UnityEngine.Cursor.lockState != CursorLockMode.Locked)
                {
                    if (GetCursorPos(out POINT trackPt))
                    {
                        s_CursorTrackingPosX = trackPt.X;
                        s_CursorTrackingPosY = trackPt.Y;
                        s_CursorTrackingValid = true;
                    }
                }
#endif

                // --- Cursor lock/visible state forwarding ---
                // When the game changes Cursor.lockState or Cursor.visible,
                // notify the browser so it can hide/show the CSS cursor.
                // Both properties are tracked independently because some
                // games change visibility without changing lock state
                // (e.g., pressing ESC may only set Cursor.visible=true).
                if (EditorApplication.isPlaying && !EditorApplication.isPaused)
                {
                    int curLock = (int)global::UnityEngine.Cursor.lockState;
                    bool cursorVisible = global::UnityEngine.Cursor.visible;
                    bool visChanged = (cursorVisible != s_LastCursorVisible);
                    if (curLock != s_LastCursorLockState || visChanged)
                    {
                        int prevLock = s_LastCursorLockState;
                        s_LastCursorLockState = curLock;
                        s_LastCursorVisible = cursorVisible;
                        bool locked = (global::UnityEngine.Cursor.lockState == CursorLockMode.Locked);
                        bool prevVisible = !visChanged ? cursorVisible : !cursorVisible;
#if UNITY_EDITOR_WIN
                        // Save cursor position when entering Locked mode.
                        // Uses the continuously tracked position from the
                        // previous frame — GetCursorPos() here would return
                        // (0,0) because Tuanjie already called SetCursorPos
                        // to the offscreen center earlier this frame.
                        if (locked && prevLock != (int)CursorLockMode.Locked)
                        {
                            if (s_CursorTrackingValid)
                            {
                                s_CursorPosBeforeLockX = s_CursorTrackingPosX;
                                s_CursorPosBeforeLockY = s_CursorTrackingPosY;
                                s_CursorPosBeforeLockSaved = true;
                                LogVerbose($"[NWB-CursorLock] Saved tracked cursor pos ({s_CursorTrackingPosX},{s_CursorTrackingPosY}) before lock");
                            }
                        }
                        // Restore OS cursor position when leaving Locked mode.
                        if (!locked && prevLock == (int)CursorLockMode.Locked)
                        {
                            if (s_CursorClipActive)
                            {
                                ClipCursor(IntPtr.Zero);
                                s_CursorClipActive = false;
                            }
                            if (s_CursorPosBeforeLockSaved)
                            {
                                SetCursorPos(s_CursorPosBeforeLockX, s_CursorPosBeforeLockY);
                                LogVerbose($"[NWB-CursorLock] Restored cursor pos ({s_CursorPosBeforeLockX},{s_CursorPosBeforeLockY}) after unlock, clip released");
                                s_CursorPosBeforeLockSaved = false;
                            }
                            // Flush any stuck held keys when leaving Locked mode.
                            // The browser may fail to send keyup events if Pointer
                            // Lock was rejected (sandbox) and focus was lost.
                            if (s_WinHeldKeys.Count > 0 || s_InputSystemHeldMouseButtons.Count > 0)
                            {
                                LogVerbose($"[NWB-CursorLock] Flushing {s_WinHeldKeys.Count} stuck keys on unlock: [{string.Join(",", s_WinHeldKeys)}]");
                                foreach (var key in s_WinHeldKeys)
                                    PostNativeKeyMessage(false, key);
                                s_WinHeldKeys.Clear();
                                ClearInputSystemMouseState();
                            }
                        }
                        // When visible transitions true while still Locked (e.g.
                        // ESC opens a menu), clip the OS cursor to the CENTER
                        // of the offscreen GameView window. This achieves:
                        // 1. No jitter — Tuanjie's SetCursorPos targets the same
                        //    center, so ClipCursor clamping is a no-op.
                        // 2. Correct Input.mousePosition — the OS cursor is inside
                        //    the GameView, so GetCursorPos maps to game center.
                        // 3. Clicks work — game's pause menu expects cursor at
                        //    center (Locked mode), Input.GetMouseButtonDown(0) +
                        //    position at center matches the game's expectations.
                        // The user's pre-lock position is restored when leaving.
                        if (locked && visChanged && cursorVisible && !prevVisible)
                        {
                            IntPtr gvHwnd = s_OffscreenTargetGUIViewHwnd != IntPtr.Zero
                                ? s_OffscreenTargetGUIViewHwnd
                                : s_OffscreenTargetContainerHwnd;
                            if (gvHwnd != IntPtr.Zero && GetWindowRect(gvHwnd, out RECT gvRect))
                            {
                                int cx = (gvRect.Left + gvRect.Right) / 2;
                                int cy = (gvRect.Top + gvRect.Bottom) / 2;
                                SetCursorPos(cx, cy);
                                RECT clipRect = new RECT {
                                    Left = cx, Top = cy,
                                    Right = cx + 1, Bottom = cy + 1
                                };
                                ClipCursor(ref clipRect);
                                s_CursorClipActive = true;
                                LogVerbose($"[NWB-CursorLock] ClipCursor at GV center ({cx},{cy}) gvRect=({gvRect.Left},{gvRect.Top},{gvRect.Right},{gvRect.Bottom})");
                            }
                        }
                        // Release clip when cursor becomes hidden again or
                        // when leaving Locked mode entirely.
                        if (s_CursorClipActive && (!locked || !cursorVisible))
                        {
                            ClipCursor(IntPtr.Zero);
                            s_CursorClipActive = false;
                            LogVerbose("[NWB-CursorLock] ClipCursor released");
                        }
#endif
                        SendDataChannelMessage("{\"type\":\"cursor_lock\",\"locked\":" +
                            (locked ? "true" : "false") + ",\"visible\":" +
                            (cursorVisible ? "true" : "false") + "}");
                        var curState = UnityEngine.Cursor.lockState;
                        LogVerbose($"[NWB-CursorLock] state={curState} visible={cursorVisible} -> frontend");
                    }
                }

#if UNITY_EDITOR_WIN
                // ClipCursor (set above) handles the Locked+visible state.
                // No per-frame SetCursorPos needed — ClipCursor constrains
                // Tuanjie's SetCursorPos to our 1px rect without jitter.
#endif

                // --- Post-input popup cleanup ---
                // Input polling may have triggered a mousedown that caused a
                // delayed popup (DoPopup). The popup ContainerWindow may now
                // exist but was not yet present during the pre-capture scan.
                // Running ScanAndCloseNativePopups again ensures the native
                // popup is closed in the same frame, preventing it from being
                // rendered in the next video frame alongside the HTML overlay.
                ScanAndCloseNativePopups();
                // Dismiss #32768 Win32 popups every frame during the popup
                // close window. TrackPopupMenuEx popups are separate from
                // ContainerWindows and need explicit Win32 API dismissal.
                if (s_FrontendPopupSent || s_PendingNativePopupCloseFrames > 0)
                    TryDismissWin32TrackPopupMenu();
                // --- Frontend heartbeat watchdog ---
                // Query C++ for the true time since last browser heartbeat.
                // C++ tracks heartbeat arrivals on the WebRTC thread, so the
                // value is accurate even when the C# main thread was blocked
                // (e.g. compilation, asset import, modal dialogs). This
                // prevents false timeouts that would stop capture unnecessarily.
                // Wrapped in try-catch: older DLLs without this API fall back
                // to the C#-side timestamp set by InjectInputEvent.
                float nativeSecSinceHb = -1f;
                try
                {
                    nativeSecSinceHb = NativeWindowBridgeAPI.NWB_GetSecondsSinceLastHeartbeat();
                }
                catch (EntryPointNotFoundException)
                {
                    // Old DLL without heartbeat API — use C#-side tracking
                    if (s_HasFrontendHeartbeat)
                        nativeSecSinceHb = (float)(EditorApplication.timeSinceStartup - s_LastFrontendHeartbeatTime);
                }
                if (nativeSecSinceHb >= 0)
                {
                    s_HasFrontendHeartbeat = true;
                    if (nativeSecSinceHb > kFrontendHeartbeatTimeoutSec)
                    {
                        CodelyLogger.LogWarning($"[NWB-Offscreen] Frontend heartbeat timeout (C++ reports {nativeSecSinceHb:F1}s > {kFrontendHeartbeatTimeoutSec}s), frame#{s_OffscreenFrameCount}, stopping capture");
                        StopOffscreenCapture();
                        return;
                    }
                    if (nativeSecSinceHb > kFrontendHeartbeatTimeoutSec * 0.5 && s_OffscreenFrameCount % 30 == 0)
                    {
                        CodelyLogger.LogWarning($"[NWB-Heartbeat] Stale: C++ reports {nativeSecSinceHb:F1}s since last heartbeat (timeout={kFrontendHeartbeatTimeoutSec}s)");
                    }
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] Loop error: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// Get the current GUIView size in PHYSICAL pixels (accounting for DPI scaling).
        /// On Windows, GUIView.screenPosition returns logical points but GrabPixels
        /// operates on the physical-pixel GPU backbuffer. Multiply by pixelsPerPoint
        /// to get the correct capture dimensions.
        /// On macOS, the Metal GrabPixels path handles backing scale factor internally,
        /// so we return the logical size as-is (macOS streaming works correctly without
        /// this adjustment — verified on Retina displays).
        /// </summary>
        private static bool TryGetCurrentGUIViewSize(out int guiW, out int guiH)
        {
            guiW = 0;
            guiH = 0;
            if (s_OffscreenTarget == null) return false;
            try
            {
                FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                object guiView = parentField != null ? parentField.GetValue(s_OffscreenTarget) : null;
                if (guiView == null) return false;

                PropertyInfo screenPosProp = guiView.GetType().GetProperty("screenPosition",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (screenPosProp == null) return false;

                Rect screenPos = (Rect)screenPosProp.GetValue(guiView);
                // Windows: screenPosition is logical points; GrabPixels uses physical backbuffer.
                // macOS: DPIScale is 1 — Metal handles backing scale internally.
                guiW = Mathf.Max(Mathf.RoundToInt(screenPos.width * LocalPP), 1);
                guiH = Mathf.Max(Mathf.RoundToInt(screenPos.height * LocalPP), 1);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureOffscreenSizeStability()
        {
            if (s_CaptureRT == null || s_OffscreenTarget == null) return;
            if (s_OffscreenResizeTargetWidth <= 0 || s_OffscreenResizeTargetHeight <= 0) return;

            if (!TryGetCurrentGUIViewSize(out int guiW, out int guiH)) return;

            bool rtMatchesGui = s_CaptureRT.width == guiW && s_CaptureRT.height == guiH;
            // TryGetCurrentGUIViewSize returns physical pixels (via LocalPP).
            // Resize target is in CSS pixels; window is set to CSS × DpiScaleFactor logical points.
            // Convergence: guiW (physical) should equal CSS × DPIScale.
            int expectedPhysW = Mathf.RoundToInt(s_OffscreenResizeTargetWidth * DPIScale);
            int expectedPhysH = Mathf.RoundToInt(s_OffscreenResizeTargetHeight * DPIScale);
            bool guiMatchesTarget = Mathf.Abs(guiW - expectedPhysW) <= 2
                                 && Mathf.Abs(guiH - expectedPhysH) <= 2;
            if (rtMatchesGui && guiMatchesTarget) return;

            // If Unity window got unexpectedly shrunk (commonly after focus/click),
            // restart resize settling so the capture window returns to requested size.
            s_OffscreenResizeTargetActive = true;
            s_OffscreenResizeRetryFrames = Mathf.Max(s_OffscreenResizeRetryFrames, 8);
        }

        /// <summary>
        /// Ensure capture RT/readback texture always matches the latest GUIView size.
        /// This prevents one-frame top-left shrink artifacts when GUIView size changes
        /// in the same tick due to toolbar/menu interactions.
        /// </summary>
        private static bool EnsureCaptureBuffersMatchGUIView(out int guiW, out int guiH)
        {
            guiW = 0;
            guiH = 0;
            if (s_CaptureRT == null) return false;
            if (!TryGetCurrentGUIViewSize(out guiW, out guiH)) return false;

            bool changed = false;
            if (s_CaptureRT.width != guiW || s_CaptureRT.height != guiH)
            {
                s_CaptureRT.Release();
                s_CaptureRT.width = guiW;
                s_CaptureRT.height = guiH;
                s_CaptureRT.Create();
                changed = true;
            }

            if (s_ReadbackTex == null || s_ReadbackTex.width != guiW || s_ReadbackTex.height != guiH)
            {
                if (s_ReadbackTex != null)
                {
                    UnityEngine.Object.DestroyImmediate(s_ReadbackTex);
                }
#if UNITY_EDITOR_WIN
                s_ReadbackTex = new Texture2D(guiW, guiH, TextureFormat.BGRA32, false, true);
#else
                s_ReadbackTex = new Texture2D(guiW, guiH, TextureFormat.BGRA32, false, false);
#endif
                changed = true;
            }

            if (changed && (s_OffscreenFrameCount <= 20 || s_OffscreenFrameCount % 300 == 0))
            {
                LogVerbose($"[NWB-Offscreen] Capture buffer sync: gui={guiW}x{guiH}, target={s_OffscreenResizeTargetWidth}x{s_OffscreenResizeTargetHeight}");
            }
            return true;
        }

        private static void ApplyPendingOffscreenResizeTarget()
        {
            if (!s_OffscreenResizeTargetActive) return;
            if (s_OffscreenTarget == null)
            {
                s_OffscreenResizeTargetActive = false;
                return;
            }

            int requestedW = Mathf.Max(s_OffscreenResizeTargetWidth, 1);
            int requestedH = Mathf.Max(s_OffscreenResizeTargetHeight, 1);
            int alignedW = requestedW;
            int alignedH = requestedH;
            bool converged = false;

            try
            {
                // requestedW/H are CSS pixels = Unity logical points (DIPs).
                // Set window.position directly without dividing by pixelsPerPoint.
                Rect pos = s_OffscreenTarget.position;
                float targetW = Mathf.Max((float)requestedW * DpiScaleFactor, 320f * DpiScaleFactor);
                float targetH = Mathf.Max((float)requestedH * DpiScaleFactor - s_TabBarOffsetY, 240f * DpiScaleFactor);
                s_OffscreenTarget.position = new Rect(pos.x, pos.y, targetW, targetH);
                s_OffscreenTarget.Repaint();

                if (TryGetCurrentGUIViewSize(out int guiW, out int guiH))
                {
                    alignedW = guiW;
                    alignedH = guiH;
                    // TryGetCurrentGUIViewSize returns physical pixels (via LocalPP).
                    // Convergence: guiW should equal CSS × DPIScale.
                    int expectedPhysW = Mathf.RoundToInt(requestedW * DPIScale);
                    int expectedPhysH = Mathf.RoundToInt(requestedH * DPIScale);
                    converged = Mathf.Abs(guiW - expectedPhysW) <= 2 && Mathf.Abs(guiH - expectedPhysH) <= 2;
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] Failed to apply resize target: {ex.Message}");
            }

            if (alignedW > 0 && alignedH > 0 && s_CaptureRT != null &&
                (s_CaptureRT.width != alignedW || s_CaptureRT.height != alignedH))
            {
                s_CaptureRT.Release();
                s_CaptureRT.width = alignedW;
                s_CaptureRT.height = alignedH;
                s_CaptureRT.Create();

                if (s_ReadbackTex != null)
                {
                    UnityEngine.Object.DestroyImmediate(s_ReadbackTex);
                }
#if UNITY_EDITOR_WIN
                s_ReadbackTex = new Texture2D(alignedW, alignedH, TextureFormat.BGRA32, false, true);
#else
                s_ReadbackTex = new Texture2D(alignedW, alignedH, TextureFormat.BGRA32, false, false);
#endif
                LogVerbose($"[NWB-Offscreen] Resize applied: requested={requestedW}x{requestedH}, aligned={alignedW}x{alignedH}");
            }

            if (converged || s_OffscreenResizeRetryFrames <= 0)
            {
                if (!converged)
                {
                    CodelyLogger.LogWarning($"[NWB-Offscreen] Resize settle timeout: requested={requestedW}x{requestedH}, finalAligned={alignedW}x{alignedH}");
                }
                s_OffscreenResizeTargetActive = false;
                s_OffscreenResizeRetryFrames = 0;

                // Force Layout+Repaint so ConfigureTargetTexture() recreates
                // m_TargetTexture at the new size and cameras render with the
                // correct aspect ratio. Layout is required — a Repaint-only
                // SendEvent does not trigger ConfigureTargetTexture.
#if UNITY_EDITOR_WIN
                ForceDoubleLayoutRepaint(s_OffscreenTarget);
#endif
            }
            else
            {
                s_OffscreenResizeRetryFrames--;
            }
        }

        private static void TryPrepareGameViewLowResolutionForOffscreen(EditorWindow target)
        {
#if UNITY_EDITOR_WIN
            if (target == null || target.GetType().Name != "GameView") return;
            try
            {
                PropertyInfo prop = target.GetType().GetProperty("lowResolutionForAspectRatios",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null || !prop.CanRead || !prop.CanWrite || prop.PropertyType != typeof(bool))
                    return;

                bool current = (bool)prop.GetValue(target);
                s_GameViewLowResOriginalCaptured = true;
                s_GameViewLowResOriginalValue = current;
                if (current)
                {
                    prop.SetValue(target, false);
                    s_GameViewLowResForcedOff = true;
                    LogVerbose("[NWB-Offscreen] Temporarily disabled GameView lowResolutionForAspectRatios on Windows for stream clarity");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] Failed to adjust GameView low-resolution setting: {ex.Message}");
            }
#endif
        }

        private static void TryRestoreGameViewLowResolutionAfterOffscreen(EditorWindow target)
        {
#if UNITY_EDITOR_WIN
            if (!s_GameViewLowResOriginalCaptured || target == null || target.GetType().Name != "GameView")
                return;
            try
            {
                PropertyInfo prop = target.GetType().GetProperty("lowResolutionForAspectRatios",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
                {
                    prop.SetValue(target, s_GameViewLowResOriginalValue);
                    if (s_GameViewLowResForcedOff)
                    {
                        LogVerbose($"[NWB-Offscreen] Restored GameView lowResolutionForAspectRatios = {s_GameViewLowResOriginalValue}");
                    }
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] Failed to restore GameView low-resolution setting: {ex.Message}");
            }
#endif
        }

#if UNITY_EDITOR_WIN
        // Cached FieldInfo for m_TargetTexture to avoid repeated reflection lookups
        // in RestoreGameCameraAspects which is called on every input event.
        private static FieldInfo s_CachedTargetTextureField;
        private static bool s_CachedTargetTextureFieldResolved;

        /// <summary>
        /// Restore all game camera aspect ratios to match the offscreen capture
        /// target texture. Internal_SendEvent(MouseDown/MouseUp) calls native
        /// UpdateScreenManager → OnWindowSizeHasChanged → Camera::WindowSizeHasChanged
        /// → ResetAspect, which re-computes Camera.aspect from GetDisplayViewSize(0)
        /// (the ORIGINAL docked GameView dimensions, e.g. 1126x514) because
        /// GUIView::GetCurrent() is not yet set to the GameView during
        /// UpdateScreenManager. This corrupts Camera.main.aspect from the correct
        /// targetTex ratio (e.g. 922/900=1.024) to the docked ratio (1126/514=2.191),
        /// causing horizontal compression in subsequent renders.
        /// </summary>
        private static void RestoreGameCameraAspects()
        {
            // Determine the correct aspect ratio from the streaming GameView's
            // render texture. Works for both single-window and composite modes.
            RenderTexture targetTex = null;

            if (s_OffscreenTarget != null && s_OffscreenTargetType != null
                && s_OffscreenTargetType.Name == "GameView")
            {
                // Single-window mode: use s_OffscreenTarget
                if (!s_CachedTargetTextureFieldResolved)
                {
                    s_CachedTargetTextureField = s_OffscreenTargetType.GetField("m_TargetTexture",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                    if (s_CachedTargetTextureField == null)
                        s_CachedTargetTextureField = s_OffscreenTargetType.BaseType?.GetField("m_TargetTexture",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                    s_CachedTargetTextureFieldResolved = true;
                }
                if (s_CachedTargetTextureField != null)
                    targetTex = s_CachedTargetTextureField.GetValue(s_OffscreenTarget) as RenderTexture;
            }
            else if (s_CompositeSlots != null && s_CompositeSlots.Count > 0)
            {
                // Composite mode: find the GameView slot's m_RenderTexture.
                // SendEvent triggers UpdateScreenManager → OnWindowSizeHasChanged
                // which corrupts Camera.aspect to the docked GameView's ratio.
                foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
                {
                    if (slot?.Window == null) continue;
                    if (slot.WindowTypeName == null || !slot.WindowTypeName.Contains("GameView")) continue;

                    if (!s_CachedTargetTextureFieldResolved)
                    {
                        var gvType = slot.Window.GetType();
                        s_CachedTargetTextureField = gvType.GetField("m_TargetTexture",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                        if (s_CachedTargetTextureField == null)
                            s_CachedTargetTextureField = gvType.BaseType?.GetField("m_TargetTexture",
                                BindingFlags.Instance | BindingFlags.NonPublic);
                        s_CachedTargetTextureFieldResolved = true;
                    }
                    if (s_CachedTargetTextureField != null)
                        targetTex = s_CachedTargetTextureField.GetValue(slot.Window) as RenderTexture;
                    break;
                }
            }
            else
            {
                return;
            }

            try
            {
                if (targetTex == null || !targetTex.IsCreated())
                {
                    // Fallback: use Screen dimensions (set by SetMainPlayModeViewSize)
                    if (Screen.width <= 0 || Screen.height <= 0) return;
                    float correctAspect = (float)Screen.width / Screen.height;
                    foreach (Camera cam in Camera.allCameras)
                    {
                        if (cam.targetTexture != null) continue;
                        float delta = Mathf.Abs(cam.aspect - correctAspect);
                        if (delta > 0.01f)
                            cam.aspect = correctAspect;
                    }
                    return;
                }

                float aspect = (float)targetTex.width / targetTex.height;

                foreach (Camera cam in Camera.allCameras)
                {
                    if (cam.targetTexture != null) continue;
                    float delta = Mathf.Abs(cam.aspect - aspect);
                    if (delta > 0.01f)
                    {
                        cam.aspect = aspect;
                    }
                }
            }
            catch (Exception) { }
        }
#endif

        // Cached MethodInfo for GameView.SetMainPlayModeViewSize to avoid repeated reflection.
        private static MethodInfo s_CachedSetMainPlayModeViewSize;
        private static MethodInfo s_CachedGetTargetRenderSize;
        private static bool s_CachedPlayModeViewSizeMethodsResolved;

        /// <summary>
        /// Restore Canvas renderingDisplaySize after SetFocus corrupts it.
        ///
        /// SetFocus → UpdateScreenManager → OnWindowSizeHasChanged corrupts
        /// Screen.width/Height and Canvas.renderingDisplaySize to the ORIGINAL
        /// docked GameView dimensions (e.g. 1259x606) instead of the streaming
        /// render texture dimensions (e.g. 2282x1936). This causes
        /// ScreenSpaceOverlay Canvas elements to shift position in the
        /// streamed video because the Canvas only covers a sub-region of the
        /// render texture.
        ///
        /// Calling GameView.SetMainPlayModeViewSize with the correct target
        /// render size forces Screen.width/Height and Canvas.renderingDisplaySize
        /// to match the render texture, keeping UI elements centered.
        /// </summary>
        private static void RestoreCanvasLayout()
        {
            if (s_OffscreenTarget == null || s_OffscreenTargetType == null
                || s_OffscreenTargetType.Name != "GameView") return;

            try
            {
                if (!s_CachedPlayModeViewSizeMethodsResolved)
                {
                    s_CachedGetTargetRenderSize = s_OffscreenTargetType.GetMethod("get_targetRenderSize",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                    s_CachedSetMainPlayModeViewSize = s_OffscreenTargetType.GetMethod("SetMainPlayModeViewSize",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                    s_CachedPlayModeViewSizeMethodsResolved = true;
                }

                if (s_CachedGetTargetRenderSize == null || s_CachedSetMainPlayModeViewSize == null)
                    return;

                Vector2 targetSize = (Vector2)s_CachedGetTargetRenderSize.Invoke(s_OffscreenTarget, null);

                // Only call if Screen dimensions don't match the target render size.
                if (Mathf.Abs(Screen.width - targetSize.x) > 1f ||
                    Mathf.Abs(Screen.height - targetSize.y) > 1f)
                {
                    s_CachedSetMainPlayModeViewSize.Invoke(s_OffscreenTarget, new object[] { targetSize });
                }
            }
            catch (Exception) { }
        }

        private static bool TryCaptureGUIViewObject(object guiView, RenderTexture targetRT)
        {
            if (guiView == null || targetRT == null) return false;

            try
            {
                System.Type guiViewType = guiView.GetType();
                MethodInfo repaintMethod = guiViewType.GetMethod("Repaint",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                repaintMethod?.Invoke(guiView, null);

                MethodInfo grabPixels = guiViewType.GetMethod("GrabPixels",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(RenderTexture), typeof(Rect) },
                    null);
                if (grabPixels == null) return false;

                Rect screenPos = GetViewScreenPosition(guiView);
                float grabW = Mathf.Max(screenPos.width * LocalPP, 1f);
                float grabH = Mathf.Max(screenPos.height * LocalPP, 1f);
                grabPixels.Invoke(guiView, new object[] { targetRT, new Rect(0, 0, grabW, grabH) });
                return true;
            }
            catch (Exception ex)
            {
                if (s_OffscreenFrameCount <= 20 || s_OffscreenFrameCount % 300 == 0)
                    CodelyLogger.LogWarning($"[NWB-Composite] GUIView overlay capture failed: {ex.Message}");
                return false;
            }
        }

        private static bool TryGetActivePaneDragTab(out object paneDragView, out Rect screenRect)
        {
            paneDragView = null;
            screenRect = Rect.zero;

            try
            {
                System.Type paneDragType = typeof(EditorWindow).Assembly.GetType("UnityEditor.PaneDragTab");
                if (paneDragType == null) return false;

                UnityEngine.Object[] panes = Resources.FindObjectsOfTypeAll(paneDragType);
                if (panes == null || panes.Length == 0) return false;

                FieldInfo windowField = paneDragType.GetField("m_Window",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                FieldInfo targetRectField = paneDragType.GetField("m_TargetRect",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                foreach (UnityEngine.Object pane in panes)
                {
                    if (pane == null) continue;

                    UnityEngine.Object containerWindow = windowField?.GetValue(pane) as UnityEngine.Object;
                    if (containerWindow == null) continue;

                    Rect rect = targetRectField != null
                        ? (Rect)targetRectField.GetValue(pane)
                        : GetViewScreenPosition(pane);
                    if (rect.width <= 1f || rect.height <= 1f)
                        rect = GetViewScreenPosition(pane);
                    if (rect.width <= 1f || rect.height <= 1f) continue;

                    paneDragView = pane;
                    screenRect = rect;
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (s_OffscreenFrameCount <= 20 || s_OffscreenFrameCount % 300 == 0)
                    CodelyLogger.LogWarning($"[NWB-Composite] PaneDragTab lookup failed: {ex.Message}");
            }

            return false;
        }

        private static void EnsureCompositePaneDragRT(int width, int height)
        {
            width = Mathf.Max((width + 1) & ~1, 2);
            height = Mathf.Max((height + 1) & ~1, 2);
#if UNITY_EDITOR_WIN
            var rtReadWrite = RenderTextureReadWrite.Linear;
#else
            var rtReadWrite = RenderTextureReadWrite.Default;
#endif
            if (s_CompositePaneDragRT == null ||
                s_CompositePaneDragRT.width != width ||
                s_CompositePaneDragRT.height != height)
            {
                if (s_CompositePaneDragRT != null)
                {
                    s_CompositePaneDragRT.Release();
                    UnityEngine.Object.DestroyImmediate(s_CompositePaneDragRT);
                }
                s_CompositePaneDragRT = new RenderTexture(width, height, 0, RenderTextureFormat.BGRA32, rtReadWrite)
                {
                    name = "__NWB_CompositePaneDragRT__",
                    useMipMap = false,
                    autoGenerateMips = false
                };
                s_CompositePaneDragRT.Create();
            }
        }

        private static void DrawPaneDragTabOverlay(Rect compositeBounds, Vector2 scalePixels)
        {
            if (!TryGetActivePaneDragTab(out object paneDragView, out Rect paneRect))
                return;

            try
            {
                int overlayW = Mathf.Max((Mathf.RoundToInt(paneRect.width * LocalPP) + 1) & ~1, 2);
                int overlayH = Mathf.Max((Mathf.RoundToInt(paneRect.height * LocalPP) + 1) & ~1, 2);
                EnsureCompositePaneDragRT(overlayW, overlayH);
                if (!TryCaptureGUIViewObject(paneDragView, s_CompositePaneDragRT))
                    return;

                int destW = Mathf.RoundToInt(paneRect.width * LocalPP * scalePixels.x);
                int destH = Mathf.RoundToInt(paneRect.height * LocalPP * scalePixels.y);
                int destX = Mathf.RoundToInt((paneRect.xMin - compositeBounds.xMin) * LocalPP * scalePixels.x);
                int destY = s_CompositeRT.height
                    - Mathf.RoundToInt((paneRect.yMin - compositeBounds.yMin) * LocalPP * scalePixels.y)
                    - destH;
                Rect dest = new Rect(destX, destY, destW, destH);

                RenderTexture.active = s_CompositeRT;
                Graphics.DrawTexture(dest, s_CompositePaneDragRT);
            }
            catch (Exception ex)
            {
                if (s_OffscreenFrameCount <= 20 || s_OffscreenFrameCount % 300 == 0)
                    CodelyLogger.LogWarning($"[NWB-Composite] PaneDragTab overlay draw failed: {ex.Message}");
            }
        }

        private static bool TryHitCompositeView(Vector2 screenPoint, out object hitView, out CompositeCaptureSlot hitSlot)
        {
            hitView = null;
            hitSlot = null;
            float bestArea = float.MaxValue;
            var visitedViews = new HashSet<object>();

            foreach (CompositeCaptureSlot candidate in s_CompositeSlots.Values)
            {
                if (candidate?.Window == null) continue;
                object view = GetParentView(candidate.Window);
                if (view == null || !visitedViews.Add(view)) continue;

                Rect viewRect = GetViewScreenPosition(view);
                if (!viewRect.Contains(screenPoint)) continue;

                float area = Mathf.Max(viewRect.width * viewRect.height, 1f);
                if (area < bestArea)
                {
                    bestArea = area;
                    hitView = view;
                    hitSlot = candidate;
                }
            }

            return hitView != null;
        }

        private static void CaptureAndPushFrame()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            if (s_CompositeActive)
            {
                CaptureAndPushCompositeFrame();
                return;
            }

            if (s_CaptureRT == null || s_ReadbackTex == null) return;

            if (s_OffscreenTarget == null) return;

            // Ensure the target is the active tab in its DockArea so GrabPixels
            // captures the correct window when multiple windows share a tab group.
            EnsureActiveTabInDockArea(s_OffscreenTarget);

            // Close native popup windows BEFORE frame capture so they don't appear
            // in the video alongside the HTML overlay popup (which is sent separately
            // via DataChannel popup_show). Without this ordering, both the rasterized
            // IMGUI popup AND the HTML overlay popup would be visible simultaneously.
            ScanAndCloseNativePopups();

            bool captured = false;
            bool isGameViewTarget = s_OffscreenTargetType != null && s_OffscreenTargetType.Name == "GameView";

#if UNITY_EDITOR_WIN
            // Ensure camera aspects match the target texture before capture.
            // Async Repaint → DoPaint → UpdateScreenManager → OnWindowSizeHasChanged
            // can corrupt camera aspect between frames.
            if (isGameViewTarget)
                RestoreGameCameraAspects();
#endif
            // Sync capture buffers to current GUIView just before capture, so GrabPixels
            // never writes a smaller GUI image into a stale larger RT.
            bool hasGuiSize = EnsureCaptureBuffersMatchGUIView(out int guiWNow, out int guiHNow);

            // If GameView temporarily shrank away from the requested target (common around
            // toolbar popup interactions), skip this frame to avoid pushing a wrong-sized
            // transient frame. Resize settling will restore target in subsequent ticks.
            // guiWNow/guiHNow are physical pixels (via LocalPP).
            // Convergence: guiWNow should equal CSS × DPIScale.
            if (isGameViewTarget && hasGuiSize &&
                s_OffscreenResizeTargetWidth > 0 && s_OffscreenResizeTargetHeight > 0)
            {
                int expectedPhysW = Mathf.RoundToInt(s_OffscreenResizeTargetWidth * DPIScale);
                int expectedPhysH = Mathf.RoundToInt(s_OffscreenResizeTargetHeight * DPIScale);
                if (Mathf.Abs(guiWNow - expectedPhysW) > 2 || Mathf.Abs(guiHNow - expectedPhysH) > 2)
                {
                    s_OffscreenResizeTargetActive = true;
                    s_OffscreenResizeRetryFrames = Mathf.Max(s_OffscreenResizeRetryFrames, 10);
                    if (s_OffscreenFrameCount <= 20 || s_OffscreenFrameCount % 300 == 0)
                    {
                        CodelyLogger.LogWarning($"[NWB-Offscreen] Skip transient GameView frame: gui={guiWNow}x{guiHNow}, target={s_OffscreenResizeTargetWidth}x{s_OffscreenResizeTargetHeight}");
                    }
                    s_OffscreenTarget.Repaint();
                    return;
                }
            }

            // Use GrabPixels for ALL window types (GameView, SceneView, Inspector, etc.)
            // to capture the full window content including toolbars and UI chrome.
            // Strategy 1 (RenderTexture) and Strategy 2 (SceneView camera) only capture
            // the render area without toolbars, causing inconsistency with coordinate mapping.
#if UNITY_EDITOR_WIN
            // On Unity 2019 Gamma (no AuxBackBufferManager), GrabPixels reads
            // zeroed-out SwapChain back buffers after Present. Use PrintWindow
            // (DWM composition capture) to bypass this entirely.
            if (s_UseRenderTextureFallback && s_OffscreenTargetContainerHwnd != IntPtr.Zero)
                captured = TryCaptureViaPrintWindow(s_OffscreenTargetContainerHwnd, s_CaptureRT);
#endif
            if (!captured)
                captured = TryCaptureViaGUIView(s_OffscreenTarget);

            // Fallback: Try internal RenderTexture (for windows where GrabPixels fails).
            if (!captured && !isGameViewTarget)
            {
                RenderTexture sourceRT = TryGetWindowRenderTexture(s_OffscreenTarget);
                if (sourceRT != null)
                {
                    Graphics.Blit(sourceRT, s_CaptureRT);
                    captured = true;
                    if (s_OffscreenFrameCount <= 20 || s_OffscreenFrameCount % 300 == 0)
                    {
                        LogVerbose($"[NWB-Offscreen] Capture fallback used: internal RT at frame#{s_OffscreenFrameCount}");
                    }
                }
            }
            else if (!captured && isGameViewTarget && (s_OffscreenFrameCount <= 20 || s_OffscreenFrameCount % 300 == 0))
            {
                // GameView fallback path captures render area without IMGUI toolbar/menu
                // and can cause one-frame flash with slightly different layout.
                LogVerbose($"[NWB-Offscreen] Skip GameView fallback at frame#{s_OffscreenFrameCount} (preserve toolbar-consistent frames)");
            }

            // Fallback: SceneView camera render.
            if (!captured && s_OffscreenTarget is SceneView sceneView)
            {
                Camera cam = sceneView.camera;
                if (cam != null)
                {
                    var prev = cam.targetTexture;
                    cam.targetTexture = s_CaptureRT;
                    cam.Render();
                    cam.targetTexture = prev;
                    captured = true;
                }
            }

            // Strategy 4: Force a Repaint and retry on next frame.
            if (!captured)
            {
                s_OffscreenTarget.Repaint();
                return;
            }

            // GPU -> CPU readback.
            int w = s_CaptureRT.width;
            int h = s_CaptureRT.height;

            if (s_ReadbackTex.width != w || s_ReadbackTex.height != h)
            {
                UnityEngine.Object.DestroyImmediate(s_ReadbackTex);
#if UNITY_EDITOR_WIN
                s_ReadbackTex = new Texture2D(w, h, TextureFormat.BGRA32, false, true);
#else
                s_ReadbackTex = new Texture2D(w, h, TextureFormat.BGRA32, false, false);
#endif
            }

            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = s_CaptureRT;
            s_ReadbackTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            s_ReadbackTex.Apply(false);
            RenderTexture.active = prevActive;

            NativeArray<byte> raw = s_ReadbackTex.GetRawTextureData<byte>();

            double pushStart = EditorApplication.timeSinceStartup;
            unsafe
            {
                NativeWindowBridgeAPI.NWB_PushFrame(
                    (IntPtr)raw.GetUnsafeReadOnlyPtr(),
                    w, h, w * 4);
            }
            double pushMs = (EditorApplication.timeSinceStartup - pushStart) * 1000.0;
            // Warn when PushFrame blocks the main thread for too long.
            // This usually means the native encoder (MFT) is stalling.
            if (pushMs > 100.0)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] PushFrame took {pushMs:F1}ms (SLOW - encoder may be stalling)");
            }

            s_OffscreenFrameCount++;
            if (s_OffscreenFrameCount == 1)
            {
                LogVerbose($"[NWB-Offscreen] Frame #1: {w}x{h} stride={w * 4} fmt={s_ReadbackTex.format} rt={s_CaptureRT.format} sRGB={s_CaptureRT.sRGB}");
            }

            // Queue an async repaint so GameView.OnGUI runs on the next editor
            // tick. Without this, the player loop never advances because
            // GrabPixels only reads back the last rendered content and does NOT
            // trigger a new render pass. The repaint → OnGUI → player loop step
            // cycle keeps the game running at the capture framerate while
            // off-screen.  Repaint() is cheap (sets a dirty flag, coalesced by
            // the editor) and does NOT cause the re-entry issues that
            // RepaintImmediately() would.
            if (isGameViewTarget && EditorApplication.isPlaying)
            {
                s_OffscreenTarget.Repaint();
            }
#endif
        }

        private static void CaptureAndPushCompositeFrame()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            if (s_CompositeSlots.Count == 0)
                return;

            // Skip the first 2 frames to allow renders to propagate through
            // the DWM/GPU pipeline.
            // Frame 0: render propagates to AuxBackBuffer
            // Frame 1: Present propagates to DWM redirect surface
            // Frame 2+: PrintWindow captures correct content
            //
            // Use warm-up frame 1 to Focus() the GameView slot so it becomes
            // the "main" PlayModeView (s_LastFocused).  Without this, the
            // floating GameView's RenderView() may be skipped in Play mode
            // (NeedToPerformRendering consumed by the original docked GameView)
            // and m_RenderTexture stays stale → horizontal compression.
            if (s_OffscreenFrameCount < 2)
            {
                if (s_OffscreenFrameCount == 1)
                    FocusAndRepaintCompositeGameView();
                s_OffscreenFrameCount++;
                return;
            }

            if (s_PendingNativePopupCloseFrames > 0 || s_FrontendPopupSent)
                DismissAllNativePopups();

            // Scan for natively-added tabs only when triggered (e.g. after
            // right-click "Add Tab"). Avoids per-frame FindObjectsOfTypeAll.
            if (s_CompositeTrackPanesCountdown > 0)
            {
                TrackUntrackedCompositePanes();
                s_CompositeTrackPanesCountdown--;
            }

            ResizeCompositeWorkspaceToCanvas();

            // Detect Play mode entry: Focus GameView so it becomes the main
            // PlayModeView with correct render target priority.
            if (EditorApplication.isPlaying && !s_CompositePlayModeInitDone)
            {
                FocusAndRepaintCompositeGameView();
                s_CompositePlayModeInitDone = true;
            }
            // Detect Play→Edit transition (Stop pressed, no domain reload):
            // Repaint all slots to update their render state for Edit mode.
            // Do NOT call FocusAndRepaintCompositeGameView() here — that would
            // Focus(GameView) and steal the active tab back from the pre-play
            // tab (e.g. SceneView) that was just restored in OnPlayModeStateChanged.
            if (!EditorApplication.isPlaying)
            {
                if (s_CompositePlayModeInitDone)
                {
                    foreach (CompositeCaptureSlot s in s_CompositeSlots.Values)
                    {
                        if (s?.Window == null) continue;
#if UNITY_EDITOR_WIN
                        if (s.WindowTypeName?.Contains("GameView") == true)
                            RestoreGameCameraAspects();
#endif
                        ForceRepaintImmediately(s.Window);
                    }
                }
                s_CompositePlayModeInitDone = false;
            }

            // Build a deduplicated view→slot map. Multiple tabs sharing one
            // DockArea map to the same parent view; we capture the DockArea
            // once and it shows whichever tab is currently active.
            // Views may be in different DockAreas if the user dragged a tab
            // to split the window — this is normal and should not be "fixed".
            var viewSlots = new Dictionary<object, CompositeCaptureSlot>();
            foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
            {
                if (slot?.Window == null) continue;
                object view = GetParentView(slot.Window);
                if (view == null) continue;
                if (viewSlots.ContainsKey(view)) continue;
                viewSlots[view] = slot;
            }

            // Detect tab drag-out: when viewSlots.Count increases, a tab was
            // dragged to a new DockArea (SplitView). Skip 2 frames to let
            // the layout change propagate before capturing.
            int currentViewCount = viewSlots.Count;
            if (currentViewCount > s_CompositeLastViewCount && s_CompositeLastViewCount > 0)
            {
                LogVerbose($"[NWB-Composite] Tab drag-out detected: dockAreas {s_CompositeLastViewCount} -> {currentViewCount}");
                s_CompositeLastViewCount = currentViewCount;
                s_CompositeSkipFrames = 2;
                s_OffscreenFrameCount++;
                return;
            }
            s_CompositeLastViewCount = currentViewCount;

            // Skip frames after drag-out to let window resize propagate.
            if (s_CompositeSkipFrames > 0)
            {
                s_CompositeSkipFrames--;
                s_OffscreenFrameCount++;
                return;
            }

            Rect bounds = Rect.zero;
            bool hasBounds = false;
            object rootSplit = GetRootSplitView(GetCompositeHostWindow());
            if (rootSplit != null)
            {
                Rect rootRect = GetViewScreenPosition(rootSplit);
                if (rootRect.width > 1 && rootRect.height > 1)
                {
                    bounds = rootRect;
                    hasBounds = true;
                }
            }

            foreach (object view in viewSlots.Keys)
            {
                Rect sp = GetViewScreenPosition(view);
                if (sp.width <= 1 || sp.height <= 1) continue;
                bounds = hasBounds ? Rect.MinMaxRect(
                    Mathf.Min(bounds.xMin, sp.xMin),
                    Mathf.Min(bounds.yMin, sp.yMin),
                    Mathf.Max(bounds.xMax, sp.xMax),
                    Mathf.Max(bounds.yMax, sp.yMax)) : sp;
                hasBounds = true;
            }

            if (!hasBounds) return;
            s_CompositeFrameBoundsLogical = bounds;

            if (s_OffscreenFrameCount <= 4 || s_OffscreenFrameCount % 600 == 0)
                LogVerbose($"[NWB-Composite] Frame#{s_OffscreenFrameCount} slots={s_CompositeSlots.Count} dockAreas={viewSlots.Count}");

            int targetLogicalW = s_CompositeLogicalWidth > 0 ? s_CompositeLogicalWidth : Mathf.RoundToInt(bounds.width);
            int targetLogicalH = s_CompositeLogicalHeight > 0 ? s_CompositeLogicalHeight : Mathf.RoundToInt(bounds.height);
            EnsureCompositeRenderTargets(targetLogicalW, targetLogicalH);
            if (s_CompositeRT == null || s_CompositeReadbackTex == null) return;

            float boundsPixelW = Mathf.Max(bounds.width * LocalPP, 1f);
            float boundsPixelH = Mathf.Max(bounds.height * LocalPP, 1f);
            Vector2 scalePixels = new Vector2(s_CompositeRT.width / boundsPixelW, s_CompositeRT.height / boundsPixelH);
            if (float.IsNaN(scalePixels.x) || float.IsInfinity(scalePixels.x) || scalePixels.x <= 0f) scalePixels.x = 1f;
            if (float.IsNaN(scalePixels.y) || float.IsInfinity(scalePixels.y) || scalePixels.y <= 0f) scalePixels.y = 1f;
            s_CompositeFrameScalePixels = scalePixels;
            s_CompositeFrameOffsetPixels = Vector2.zero;

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = s_CompositeRT;
            GL.Clear(true, true, Color.black);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, s_CompositeRT.width, s_CompositeRT.height, 0);

            foreach (var pair in viewSlots)
            {
                object view = pair.Key;
                CompositeCaptureSlot slot = pair.Value;
                if (slot == null || slot.Window == null)
                    continue;

                try
                {
                    EditorWindow activeWindow = GetActualViewFromHostView(view) ?? slot.Window;

                    Rect viewRect = GetViewScreenPosition(view);
                    int sourceW = Mathf.Max((Mathf.RoundToInt(viewRect.width * LocalPP) + 1) & ~1, 2);
                    int sourceH = Mathf.Max((Mathf.RoundToInt(viewRect.height * LocalPP) + 1) & ~1, 2);

                    if (s_OffscreenFrameCount <= 4 || s_OffscreenFrameCount % 600 == 0)
                        LogVerbose($"[NWB-Composite] Capture frame#{s_OffscreenFrameCount} '{slot.WindowTypeName}' sourceRT={sourceW}x{sourceH} isPlaying={EditorApplication.isPlaying}");

#if UNITY_EDITOR_WIN
                    var rtReadWrite = RenderTextureReadWrite.Linear;
#else
                    var rtReadWrite = RenderTextureReadWrite.Default;
#endif
                    if (slot.SourceRT == null || slot.SourceRT.width != sourceW || slot.SourceRT.height != sourceH)
                    {
                        if (slot.SourceRT != null)
                        {
                            slot.SourceRT.Release();
                            UnityEngine.Object.DestroyImmediate(slot.SourceRT);
                        }
                        slot.SourceRT = new RenderTexture(sourceW, sourceH, 0, RenderTextureFormat.BGRA32, rtReadWrite)
                        {
                            name = "__NWB_CompositeViewRT__",
                            useMipMap = false,
                            autoGenerateMips = false
                        };
                        slot.SourceRT.Create();
                    }

#if UNITY_EDITOR_WIN
                    // Determine which HWND to use for PrintWindow capture.
                    // Gamma mode (Unity 2019): GrabPixels returns black due to
                    // SwapChain present semantics — PrintWindow with ContainerHwnd
                    // (valid Win32 HWND) is the only reliable capture path.
                    // Linear mode (Tuanjie): GUIViewHwnd is a C++ object pointer
                    // (not a valid HWND); PrintWindow fails, but GrabPixels
                    // fallback works correctly via TryCaptureViaGUIView.
                    IntPtr printWindowHwnd;
                    if (s_UseRenderTextureFallback)
                        printWindowHwnd = slot.ContainerHwnd;
                    else
                        printWindowHwnd = slot.GUIViewHwnd != IntPtr.Zero
                            ? slot.GUIViewHwnd : slot.ContainerHwnd;
                    bool usePrintWindow = printWindowHwnd != IntPtr.Zero;

#endif

                    if (!ShouldBlockNativeContextMenuRepaint())
                    {
                        bool isGameViewSlot = slot.WindowTypeName?.Contains("GameView") == true;
#if UNITY_EDITOR_WIN
                        // Fix Camera.aspect BEFORE any repaint. SetFocus during
                        // mousedown corrupts Camera.aspect to the docked GameView's
                        // ratio. The corruption persists until explicitly corrected.
                        // Must run in BOTH Play and Edit (Stop) modes.
                        if (isGameViewSlot)
                        {
                            RestoreGameCameraAspects();
                            ForceApplyPanelSettings();
                        }
#endif
                        if (!EditorApplication.isPlaying)
                        {
                            if (activeWindow != s_OffscreenTarget)
                                NotifySelectionChangeIfNeeded(activeWindow);
                            ForceRepaintImmediately(activeWindow);
                        }
                        else if (isGameViewSlot && s_CompositeForceRepaintFrames > 0)
                        {
                            // After a click, use synchronous repaint for a few frames
                            // to ensure the backbuffer is rendered with correct
                            // Camera.aspect BEFORE GrabPixels captures it.
                            // Async Repaint would leave a 1-frame lag where GrabPixels
                            // reads the old (corrupted) backbuffer content.
                            ForceRepaintImmediately(activeWindow);
#if UNITY_EDITOR_WIN
                            RestoreGameCameraAspects();
#endif
                            s_CompositeForceRepaintFrames--;
                        }
                        else
                        {
                            activeWindow.Repaint();
                        }
                    }

                    RenderTexture savedCaptureRT = s_CaptureRT;
                    bool captured = false;
                    try
                    {
                        s_CaptureRT = slot.SourceRT;
#if UNITY_EDITOR_WIN
                        if (usePrintWindow)
                        {
                            captured = TryCaptureViaPrintWindow(printWindowHwnd, s_CaptureRT, viewRect, true);
                            if (s_OffscreenFrameCount <= 2 || s_OffscreenFrameCount % 600 == 0)
                                LogVerbose($"[NWB-Composite] PrintWindow capture for '{slot.WindowTypeName}' frame#{s_OffscreenFrameCount}: ok={captured} hwnd=0x{printWindowHwnd.ToInt64():X}");
                        }

#endif
                        if (!captured)
                            captured = TryCaptureViaGUIView(activeWindow);
                    }
                    finally
                    {
                        s_CaptureRT = savedCaptureRT;
                    }

                    if (!captured)
                    {
                        if (s_OffscreenFrameCount <= 20 || s_OffscreenFrameCount % 300 == 0)
                            CodelyLogger.LogWarning($"[NWB-Composite] Slot capture returned false ({slot.SlotId}, {slot.WindowTypeName})");
                        continue;
                    }

                    int destW = Mathf.RoundToInt(viewRect.width * LocalPP * scalePixels.x);
                    int destH = Mathf.RoundToInt(viewRect.height * LocalPP * scalePixels.y);
                    int destX = Mathf.RoundToInt((viewRect.xMin - bounds.xMin) * LocalPP * scalePixels.x);
                    int destY = s_CompositeRT.height
                        - Mathf.RoundToInt((viewRect.yMin - bounds.yMin) * LocalPP * scalePixels.y)
                        - destH;
                    Rect dest = new Rect(destX, destY, destW, destH);

                    // RepaintImmediately / TryCaptureViaGUIView may change
                    // RenderTexture.active and GL matrix. Restore both before
                    // drawing the slot texture into the composite frame.
                    RenderTexture.active = s_CompositeRT;
                    GL.LoadPixelMatrix(0, s_CompositeRT.width, s_CompositeRT.height, 0);
                    Graphics.DrawTexture(dest, slot.SourceRT);
                }
                catch (Exception ex)
                {
                    if (s_OffscreenFrameCount <= 20 || s_OffscreenFrameCount % 300 == 0)
                        CodelyLogger.LogWarning($"[NWB-Composite] Slot capture failed ({slot.SlotId}): {ex.Message}");
                }
            }

            DrawPaneDragTabOverlay(bounds, scalePixels);

            GL.PopMatrix();
            RenderTexture.active = s_CompositeRT;

            int w = s_CompositeRT.width;
            int h = s_CompositeRT.height;
            if (s_CompositeReadbackTex.width != w || s_CompositeReadbackTex.height != h)
            {
                UnityEngine.Object.DestroyImmediate(s_CompositeReadbackTex);
#if UNITY_EDITOR_WIN
                s_CompositeReadbackTex = new Texture2D(w, h, TextureFormat.BGRA32, false, true);
#else
                s_CompositeReadbackTex = new Texture2D(w, h, TextureFormat.BGRA32, false, false);
#endif
            }

            s_CompositeReadbackTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            s_CompositeReadbackTex.Apply(false);
            RenderTexture.active = previousActive;

            NativeArray<byte> raw = s_CompositeReadbackTex.GetRawTextureData<byte>();
            unsafe
            {
                NativeWindowBridgeAPI.NWB_PushFrame(
                    (IntPtr)raw.GetUnsafeReadOnlyPtr(),
                    w, h, w * 4);
            }

            s_OffscreenFrameCount++;
            if (s_OffscreenFrameCount == 1)
            {
                LogVerbose($"[NWB-Composite] Frame #1: {w}x{h} slots={s_CompositeSlots.Count}");
            }
#endif
        }

        /// <summary>
        /// Track selection state for composite capture. When Selection changes
        /// between frames, non-input-target windows (e.g. Inspector) need to be
        /// explicitly notified because Unity's deferred selectionChanged callback
        /// hasn't fired yet at capture time. Without this, the Inspector's internal
        /// Editor[] array remains stale and OnGUI draws old content even though
        /// RepaintImmediately executes successfully.
        /// </summary>
        private static UnityEngine.Object s_LastCapturedSelection;
        private static int s_NotifySelectionChangeLogCount;

        private static void NotifySelectionChangeIfNeeded(EditorWindow window)
        {
            if (window == null) return;
            var curSel = Selection.activeObject;
            if (curSel == s_LastCapturedSelection) return;

            // Selection changed since last capture - use InvokeSelectionChangeOnWindow
            // which handles both OnSelectionChange and OnSelectionChanged method names.
            s_LastCapturedSelection = curSel;
            if (InvokeSelectionChangeOnWindow(window))
            {
                window.Repaint();
                // Trigger RepaintAll(true) so PlayerLoopController resets
                // m_CanBlitPaint=false → next DoPaint does full render with
                // the freshly rebuilt UI.
                EditorApplication.QueuePlayerLoopUpdate();
                s_NotifySelectionChangeLogCount++;
                if (s_NotifySelectionChangeLogCount <= 10 || s_NotifySelectionChangeLogCount % 300 == 0)
                    LogVerbose($"[NWB-Selection] Notified {window.GetType().Name} " +
                        $"sel='{(curSel != null ? curSel.name : "(null)")}' (#{s_NotifySelectionChangeLogCount})");
            }
        }

        /// <summary>
        /// Called from PollAndInjectInput immediately after a mouseup event
        /// confirms that Selection changed (selBeforeUp != selAfterUp).
        /// Iterates all composite slots EXCEPT the current input target and
        /// forces OnSelectionChange() on each, ensuring their internal
        /// Editor[]/Tracker state rebuilds for the next capture frame.
        /// This is more reliable than detecting selection change in the capture
        /// loop because capture runs BEFORE input injection each frame.
        /// </summary>
        private static void NotifyCompositeNonTargetWindows(UnityEngine.Object newSel)
        {
            if (!s_CompositeActive) return;
            int notified = 0;
            int skippedNull = 0;
            int slotTotal = s_CompositeSlots.Count;
            string targetName = s_OffscreenTarget != null ? s_OffscreenTarget.GetType().Name : "(null)";

            // Notify all composite windows of selection change. Different
            // window types use different method names in Tuanjie:
            //   SceneHierarchyWindow: OnSelectionChange()  (Unity message)
            //   InspectorWindow:      OnSelectionChanged() (private callback)
            // Try both names, fall back to RebuildContentsContainers().
            foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
            {
                if (slot == null) continue;
                if (slot.Window == null)
                {
                    skippedNull++;
                    continue;
                }
                try
                {
                    if (InvokeSelectionChangeOnWindow(slot.Window))
                    {
                        notified++;
                        slot.Window.Repaint();
                    }
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"[NWB-Selection] NotifyComposite slot failed on {slot.WindowTypeName}: {ex.Message}");
                }
            }

            s_LastCapturedSelection = newSel;
            s_NotifySelectionChangeLogCount++;
            if (s_NotifySelectionChangeLogCount <= 20 || s_NotifySelectionChangeLogCount % 100 == 0)
            {
                CodelyLogger.Log($"[NWB-Selection] NotifyCompositeNonTargetWindows: sel='{(newSel != null ? newSel.name : "(null)")}' " +
                    $"notified={notified} skippedNull={skippedNull} target='{targetName}' slots={slotTotal} (#{s_NotifySelectionChangeLogCount})");
            }

            // Trigger RepaintAll(true) via PlayerLoopController to reset
            // m_CanBlitPaint=false on all autoRepaint views. This forces
            // DoPaint to take the full render path (instead of blit) on the
            // next frame, ensuring the AuxBackBuffer is updated with fresh
            // content after the selection change handler rebuilt the UI.
            if (notified > 0)
                EditorApplication.QueuePlayerLoopUpdate();
        }

        /// <summary>
        /// Invoke the selection-change handler on a window via reflection.
        /// For Inspector/PropertyEditor windows, forces the ActiveEditorTracker
        /// to rebuild first so that tracker.activeEditors contains editors for
        /// the new selection. Without this, RebuildContentsContainers reads stale
        /// editors because the tracker updates lazily.
        /// </summary>
        private static int s_InvokeSelectionLogCount;
        private static bool InvokeSelectionChangeOnWindow(EditorWindow window)
        {
            if (window == null) return false;
            System.Type wType = window.GetType();
            string wName = wType.Name;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // For Inspector/PropertyEditor: force tracker rebuild BEFORE
            // OnSelectionChanged, so tracker.activeEditors has new editors.
            bool isPropertyEditor = typeof(EditorWindow).Assembly
                .GetType("UnityEditor.PropertyEditor")?.IsAssignableFrom(wType) ?? false;
            if (isPropertyEditor)
            {
                try
                {
                    FieldInfo trackerField = wType.GetField("m_Tracker", flags);
                    if (trackerField == null)
                    {
                        // Walk base types to find the field
                        System.Type baseT = wType.BaseType;
                        while (baseT != null && trackerField == null)
                        {
                            trackerField = baseT.GetField("m_Tracker", flags);
                            baseT = baseT.BaseType;
                        }
                    }
                    if (trackerField != null)
                    {
                        object tracker = trackerField.GetValue(window);
                        if (tracker != null)
                        {
                            MethodInfo forceRebuild = tracker.GetType().GetMethod("ForceRebuild", flags);
                            if (forceRebuild != null)
                            {
                                forceRebuild.Invoke(tracker, null);
                                s_InvokeSelectionLogCount++;
                                if (s_InvokeSelectionLogCount <= 20 || s_InvokeSelectionLogCount % 100 == 0)
                                    CodelyLogger.Log($"[NWB-Selection] tracker.ForceRebuild OK for {wName} (#{s_InvokeSelectionLogCount})");
                            }
                            else if (s_InvokeSelectionLogCount <= 5)
                                CodelyLogger.LogWarning($"[NWB-Selection] ForceRebuild method NOT FOUND on tracker for {wName}");
                        }
                        else if (s_InvokeSelectionLogCount <= 5)
                            CodelyLogger.LogWarning($"[NWB-Selection] m_Tracker is null for {wName}");
                    }
                    else if (s_InvokeSelectionLogCount <= 5)
                        CodelyLogger.LogWarning($"[NWB-Selection] m_Tracker field NOT FOUND for {wName}");
                }
                catch (Exception ex)
                {
                    if (s_InvokeSelectionLogCount <= 5)
                        CodelyLogger.LogWarning($"[NWB-Selection] tracker.ForceRebuild failed for {wName}: {ex.Message}");
                }
            }

            // Try 1: OnSelectionChange (SceneHierarchyWindow and other standard windows)
            MethodInfo m = wType.GetMethod("OnSelectionChange", flags);
            if (m != null)
            {
                m.Invoke(window, null);
                if (s_InvokeSelectionLogCount <= 20)
                    CodelyLogger.Log($"[NWB-Selection] Invoked OnSelectionChange on {wName}");
                return true;
            }

            // Try 2: OnSelectionChanged (InspectorWindow in Tuanjie uses this name)
            m = wType.GetMethod("OnSelectionChanged", flags);
            if (m != null)
            {
                m.Invoke(window, null);
                if (s_InvokeSelectionLogCount <= 20)
                    CodelyLogger.Log($"[NWB-Selection] Invoked OnSelectionChanged on {wName}");
                return true;
            }

            // Try 3: RebuildContentsContainers (PropertyEditor base class)
            m = wType.GetMethod("RebuildContentsContainers", flags);
            if (m != null)
            {
                m.Invoke(window, null);
                if (s_InvokeSelectionLogCount <= 20)
                    CodelyLogger.Log($"[NWB-Selection] Invoked RebuildContentsContainers on {wName}");
                return true;
            }

            if (s_InvokeSelectionLogCount <= 20)
                CodelyLogger.LogWarning($"[NWB-Selection] No selection-change method found on {wName}");
            return false;
        }

        /// <summary>
        /// Force a synchronous repaint on an offscreen EditorWindow by calling
        /// GUIView.RepaintImmediately() via reflection. This ensures that windows
        /// hidden off-screen in Edit Mode actually execute their OnGUI and update
        /// their GPU backbuffer before GrabPixels reads it.
        /// Falls back to EditorWindow.Repaint() if reflection fails.
        /// </summary>
        private static int s_ForceRepaintLogCount;
        private static int s_ForceRepaintInspectorCount;
        private static void ForceRepaintImmediately(EditorWindow window)
        {
            if (window == null) return;
            try
            {
                if (!s_RepaintImmediatelyReflectionDone)
                {
                    s_RepaintImmediatelyReflectionDone = true;
                    var gvType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView");
                    if (gvType != null)
                    {
                        var t = gvType;
                        while (t != null && s_GUIViewRepaintImmediatelyMethod == null)
                        {
                            s_GUIViewRepaintImmediatelyMethod = t.GetMethod("RepaintImmediately",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null, System.Type.EmptyTypes, null);
                            t = t.BaseType;
                        }
                    }
                    LogVerbose($"[NWB-Repaint] Reflection init: GUIView.RepaintImmediately {(s_GUIViewRepaintImmediatelyMethod != null ? "FOUND" : "NOT FOUND")}");
                }

                if (s_GUIViewRepaintImmediatelyMethod != null)
                {
                    FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    object guiView = parentField?.GetValue(window);
                    if (guiView != null)
                    {
                        s_GUIViewRepaintImmediatelyMethod.Invoke(guiView, null);
                        s_ForceRepaintLogCount++;
                        bool isInspector = window.GetType().Name.Contains("Inspector");
                        if (isInspector) s_ForceRepaintInspectorCount++;
                        if (s_ForceRepaintLogCount <= 5 || s_ForceRepaintLogCount % 600 == 0
                            || (isInspector && s_ForceRepaintInspectorCount <= 5))
                            LogVerbose($"[NWB-Repaint] ForceRepaintImmediately OK: {window.GetType().Name} (#{s_ForceRepaintLogCount} insp#{s_ForceRepaintInspectorCount})");
                        return;
                    }
                    else
                    {
                        if (s_ForceRepaintLogCount <= 10)
                            LogVerbose($"[NWB-Repaint] ForceRepaintImmediately FAILED: m_Parent is null for {window.GetType().Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (s_ForceRepaintLogCount <= 5)
                    CodelyLogger.LogWarning($"[NWB-Repaint] ForceRepaintImmediately exception: {ex.Message}");
            }
            window.Repaint();
        }

        /// <summary>
        /// Focus the composite GameView slot and force a synchronous repaint
        /// on all slots.
        ///
        /// ROOT CAUSE OF COMPRESSION: In Play mode, each GameView's OnGUI
        /// only calls RenderView() when NeedToPerformRendering() is true.
        /// This flag is consumed by whichever GameView renders first (the
        /// "main" PlayModeView). If the floating streaming GameView is NOT
        /// the main view (s_LastFocused points to the original docked one),
        /// its RenderView() is skipped → m_RenderTexture stays stale →
        /// GrabPixels captures old/wrong-aspect content → compression.
        ///
        /// Focus() sets s_LastFocused = this, SetMainPlayModeViewSize, and
        /// Display.activeEditorGameViewTarget, making the floating GameView
        /// the primary render target for the PlayerLoop. This ensures its
        /// RenderView() runs and m_RenderTexture has fresh content.
        /// </summary>
        private static void FocusAndRepaintCompositeGameView()
        {
            CompositeCaptureSlot gameViewSlot = null;
            foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
            {
                if (slot?.Window == null) continue;
                bool isGameView = slot.WindowTypeName != null
                    && slot.WindowTypeName.Contains("GameView");
                if (isGameView)
                {
                    gameViewSlot = slot;
                    try
                    {
                        slot.Window.Focus();
#if UNITY_EDITOR_WIN
                        // Fix aspect BEFORE repaint so the synchronous render
                        // uses correct Camera.aspect. Focus() may have corrupted
                        // it via SetFocus → UpdateScreenManager chain.
                        RestoreGameCameraAspects();
#endif
                        ForceRepaintImmediately(slot.Window);
#if UNITY_EDITOR_WIN
                        // Fix again: ForceRepaintImmediately → DoPaint →
                        // UpdateScreenManager may re-corrupt Camera.aspect.
                        RestoreGameCameraAspects();
#endif
                    }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogWarning($"[NWB-Focus] GameView Focus failed: {ex.Message}");
                    }
                }
                else
                {
                    ForceRepaintImmediately(slot.Window);
                }
            }
            LogVerbose($"[NWB-Focus] FocusAndRepaintCompositeGameView done, gameViewFocused={gameViewSlot != null}");
        }


        // =====================================================================
        // Selection change listener: when Selection changes during composite
        // streaming in Edit Mode, force the Inspector to re-render by sending
        // it synthetic Layout+Repaint events (same mechanism that keeps the
        // Hierarchy backbuffer fresh via SendEvent during input injection).
        // =====================================================================
        private static bool s_SelectionListenerRegistered;
        private static UnityEngine.Object s_LastKnownSelection;
        private static int s_SelectionChangeCount;

        private static void RegisterSelectionChangeListener()
        {
            if (s_SelectionListenerRegistered) return;
            s_SelectionListenerRegistered = true;
            s_LastKnownSelection = Selection.activeObject;
            s_SelectionChangeCount = 0;
            Selection.selectionChanged += OnSelectionChangedComposite;
            LogVerbose($"[NWB-Selection] Listener registered. Current selection: {(Selection.activeObject != null ? Selection.activeObject.name : "(null)")}");
        }

        private static void UnregisterSelectionChangeListener()
        {
            if (!s_SelectionListenerRegistered) return;
            s_SelectionListenerRegistered = false;
            Selection.selectionChanged -= OnSelectionChangedComposite;
        }

        private static void OnSelectionChangedComposite()
        {
            var cur = Selection.activeObject;
            s_SelectionChangeCount++;
            s_LastKnownSelection = cur;
        }

        // =====================================================================
        // Frontend popup UI replacement: intercept GenericMenu popup creation,
        // serialize menu data to JSON, and send to browser via DataChannel.
        // Browser renders the popup in HTML/CSS and sends selection back.
        // =====================================================================

        // Tracks whether we already sent popup data to the frontend for the
        // current popup. Cleared when popup closes. Prevents duplicate sends.
        private static bool s_FrontendPopupSent;
        // When > 0, ScanAndCloseNativePopups will force-close the first
        // showMode==1 ContainerWindow it finds, regardless of isRemoteTriggered.
        // Decremented each frame. Set by TryInterceptDelayedPopup when a popup
        // is sent to the frontend but the native ContainerWindow doesn't exist yet.
        private static int s_PendingNativePopupCloseFrames;
        // Auto-incrementing popup ID for matching select/close messages.
        private static int s_FrontendPopupIdCounter;
        // Current popup ID string (e.g. "popup_1").
        private static string s_CurrentFrontendPopupId;
        // Last popup anchor in Unity pixel coordinates.
        private static float s_LastFrontendPopupX;
        private static float s_LastFrontendPopupY;
        // Last hover-tooltip text sent to browser.
        private static string s_LastFrontendTooltipText = "";

        // Tracks the last Cursor.lockState and Cursor.visible sent to the
        // frontend so we only send a DataChannel message when either changes.
        // -1 means "not yet sent" so the initial state is always forwarded.
        // Cursor sync state for DataChannel messaging; Linux builds can treat
        // these as write-only when streaming branches are compiled out.
#pragma warning disable CS0414
        private static int s_LastCursorLockState = -1;
        private static bool s_LastCursorVisible = true;
#pragma warning restore CS0414
#if UNITY_EDITOR_WIN
        // Continuously tracked OS cursor position while NOT in Locked mode.
        // Updated every frame in EditorApplication.update. When Locked mode
        // is entered, Tuanjie has already called SetCursorPos (moving cursor
        // to offscreen center, clamped to 0,0) BEFORE our callback runs.
        // Using the tracked position from the previous frame avoids saving (0,0).
        private static int s_CursorTrackingPosX;
        private static int s_CursorTrackingPosY;
        private static bool s_CursorTrackingValid;
        // Cursor position to restore when leaving Locked or becoming visible.
        private static int s_CursorPosBeforeLockX;
        private static int s_CursorPosBeforeLockY;
        private static bool s_CursorPosBeforeLockSaved;
        // When Locked+visible (e.g. ESC menu), Tuanjie still calls SetCursorPos
        // each frame to the offscreen center, clamped to (0,0). We use
        // ClipCursor to confine the OS cursor to a 1-pixel rect at the
        // saved position — this eliminates jitter from competing SetCursorPos
        // calls. Cleared when lockState leaves Locked or visible becomes false.
        private static bool s_CursorClipActive;
#endif
        // Search keyword used by the full Annotation popup.
        private static string s_AnnotationSearchFilter = "";
        // Reflection cache for EditorGUI.PopupCallbackInfo — used to detect
        // popup requests that the native platform cannot display (offscreen).
        private static System.Type s_PopupCallbackInfoType;
        private static FieldInfo s_PopupCallbackInfoInstanceField;
        private static FieldInfo s_PopupCallbackInfoControlIDField;
        private static FieldInfo s_PopupCallbackInfoSelectedIndexField;
        private static FieldInfo s_PopupCallbackInfoSourceViewField;
        private static bool s_PopupCallbackInfoReflectionDone;
        // Reflection cache for GUIView.RepaintImmediately (used in composite Edit Mode).
        private static MethodInfo s_GUIViewRepaintImmediatelyMethod;
        private static bool s_RepaintImmediatelyReflectionDone;
        // Reflection cache for backend tooltip querying.
        private static bool s_TooltipReflectionDone;
        private static FieldInfo s_EditorWindowParentField;
        private static System.Type s_GUIViewType;
        private static PropertyInfo s_GUIViewMouseOverViewProp;
        private static PropertyInfo s_GUIViewScreenPositionProp;
        private static PropertyInfo s_GUIViewWindowBackendProp;
        private static MethodInfo s_WindowBackendGetTooltipMethod;
        private static bool s_HasLoggedMissingBackendTooltip;
        // GameView toolbar tooltip: direct GUIContent reflection cache.
        // IMGUI tooltip state (m_MouseTooltip) is cleared by OnGUIState.EndOnGUI()
        // after each repaint, so GUI.tooltip / GUI.mouseTooltip are always empty
        // when read from managed code outside OnGUI.  We must read GUIContent
        // tooltip strings directly via reflection.
        private static bool s_GameViewStylesReflectionDone;
        private static System.Type s_GameViewStylesType;
        private static bool s_GameViewToolbarTooltipLogged;
        // Cached results for GameView toolbar left-side layout detection.
        private static bool s_GameViewToolbarLayoutDetected;
        private static float s_GameViewScaleRegionLeft;
        private static float s_GameViewScaleRegionRight;

        /// <summary>
        /// Send a UTF-8 JSON message to the browser via the native DataChannel.
        /// </summary>
        private static bool SendDataChannelMessage(string json)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            byte[] utf8 = Encoding.UTF8.GetBytes(json);
            return NativeWindowBridgeAPI.NWB_SendDataChannelMessage(utf8, utf8.Length) == 1;
#else
            return false;
#endif
        }

        internal static bool NotifyEditorWindowsChanged()
        {
            return SendDataChannelMessage("{\"type\":\"editor_windows_changed\"}");
        }

        // Reflection cache for AnnotationUtility (lazy-initialized).
        private static bool s_AnnotationReflectionDone;
        private static System.Type s_AnnotationUtilType;
        private static MethodInfo s_GetAnnotationsMethod;
        private static MethodInfo s_GetRecentlyChangedAnnotationsMethod;
        private static MethodInfo s_GetAnnotationMethod;
        private static MethodInfo s_SetGizmoEnabledMethod;
        private static MethodInfo s_SetIconEnabledMethod;
        private static PropertyInfo s_AnnoUse3dGizmosProp;
        private static PropertyInfo s_AnnoFadeGizmosProp;
        private static PropertyInfo s_AnnoShowSelectionOutlineProp;
        private static PropertyInfo s_AnnoShowSelectionWireProp;
        private static PropertyInfo s_AnnoIconSizeProp;
        private static PropertyInfo s_AnnoFadeGizmoSizeProp;
        private static PropertyInfo s_AnnoWindowTerrainWarnProp;

        // Reflection cache for LightmapVisualization (internal type, lazy-initialized).
        private static bool s_LightmapVisReflectionDone;
        private static System.Type s_LightmapVisType;
        private static PropertyInfo s_ShowResolutionProp;

        /// <summary>
        /// Initialize reflection cache for UnityEditor.LightmapVisualization (internal).
        /// </summary>
        private static void EnsureLightmapVisReflection()
        {
            if (s_LightmapVisReflectionDone) return;
            s_LightmapVisReflectionDone = true;
            try
            {
                s_LightmapVisType = typeof(EditorWindow).Assembly.GetType("UnityEditor.LightmapVisualization");
                if (s_LightmapVisType == null)
                {
                    CodelyLogger.LogWarning("[NWB-LightmapVis] LightmapVisualization type not found");
                    return;
                }
                s_ShowResolutionProp = s_LightmapVisType.GetProperty("showResolution",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (s_ShowResolutionProp == null)
                    CodelyLogger.LogWarning("[NWB-LightmapVis] showResolution property not found");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-LightmapVis] Reflection init error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get LightmapVisualization.showResolution via reflection. Returns false if unavailable.
        /// </summary>
        private static bool GetLightmapShowResolution()
        {
            EnsureLightmapVisReflection();
            if (s_ShowResolutionProp == null) return false;
            try { return (bool)s_ShowResolutionProp.GetValue(null); }
            catch { return false; }
        }

        /// <summary>
        /// Set LightmapVisualization.showResolution via reflection.
        /// </summary>
        private static void SetLightmapShowResolution(bool value)
        {
            EnsureLightmapVisReflection();
            if (s_ShowResolutionProp == null) return;
            try { s_ShowResolutionProp.SetValue(null, value); }
            catch (Exception ex) { CodelyLogger.LogWarning($"[NWB-LightmapVis] Set showResolution failed: {ex.Message}"); }
        }

        /// <summary>
        /// Initialize reflection cache for UnityEditor.AnnotationUtility.
        /// </summary>
        private static void EnsureAnnotationReflection()
        {
            if (s_AnnotationReflectionDone) return;
            s_AnnotationReflectionDone = true;
            try
            {
                s_AnnotationUtilType = typeof(EditorWindow).Assembly.GetType("UnityEditor.AnnotationUtility");
                if (s_AnnotationUtilType == null)
                {
                    CodelyLogger.LogWarning("[NWB-Annotation] AnnotationUtility type not found");
                    return;
                }
                s_GetAnnotationsMethod = s_AnnotationUtilType.GetMethod("GetAnnotations",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                s_GetRecentlyChangedAnnotationsMethod = s_AnnotationUtilType.GetMethod("GetRecentlyChangedAnnotations",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                s_GetAnnotationMethod = s_AnnotationUtilType.GetMethod("GetAnnotation",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new System.Type[] { typeof(int), typeof(string) }, null);
                s_SetGizmoEnabledMethod = s_AnnotationUtilType.GetMethod("SetGizmoEnabled",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new System.Type[] { typeof(int), typeof(string), typeof(int), typeof(bool) }, null);
                s_SetIconEnabledMethod = s_AnnotationUtilType.GetMethod("SetIconEnabled",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new System.Type[] { typeof(int), typeof(string), typeof(int) }, null);
                s_AnnoUse3dGizmosProp = s_AnnotationUtilType.GetProperty("use3dGizmos",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                s_AnnoFadeGizmosProp = s_AnnotationUtilType.GetProperty("fadeGizmos",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                s_AnnoShowSelectionOutlineProp = s_AnnotationUtilType.GetProperty("showSelectionOutline",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                s_AnnoShowSelectionWireProp = s_AnnotationUtilType.GetProperty("showSelectionWire",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                s_AnnoIconSizeProp = s_AnnotationUtilType.GetProperty("iconSize",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                s_AnnoFadeGizmoSizeProp = s_AnnotationUtilType.GetProperty("fadeGizmoSize",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                System.Type annotationWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.AnnotationWindow");
                s_AnnoWindowTerrainWarnProp = annotationWindowType?.GetProperty("ShowTerrainDebugWarnings",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                LogVerbose($"[NWB-Annotation] Reflection OK: GetAnnotations={s_GetAnnotationsMethod != null} GetAnnotation={s_GetAnnotationMethod != null} SetGizmoEnabled={s_SetGizmoEnabledMethod != null} SetIconEnabled={s_SetIconEnabledMethod != null}");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Annotation] Reflection init error: {ex.Message}");
            }
        }

        private static bool GetStaticBool(PropertyInfo prop, bool fallback = false)
        {
            if (prop == null) return fallback;
            try { return (bool)prop.GetValue(null); } catch { return fallback; }
        }

        private static float GetStaticFloat(PropertyInfo prop, float fallback = 0f)
        {
            if (prop == null) return fallback;
            try { return (float)prop.GetValue(null); } catch { return fallback; }
        }

        private static void SetStaticBool(PropertyInfo prop, bool value)
        {
            if (prop == null) return;
            try { prop.SetValue(null, value); } catch { }
        }

        private static void SetStaticFloat(PropertyInfo prop, float value)
        {
            if (prop == null) return;
            try { prop.SetValue(null, value); } catch { }
        }

        // Match UnityEditor.AnnotationWindow icon-size slider mapping.
        private const float kAnnoIconExponentStart = -3.0f;
        private const float kAnnoIconExponentRange = 3.0f;
        private const int kAnnotationIconEncodeMaxSize = 14;

        private static float ConvertTexelWorldSizeTo01(float texelWorldSize)
        {
            if (texelWorldSize == -1.0f) return 1.0f;
            if (texelWorldSize == 0.0f) return 0.0f;
            return (Mathf.Log10(texelWorldSize) - kAnnoIconExponentStart) / kAnnoIconExponentRange;
        }

        private static float Convert01ToTexelWorldSize(float value01)
        {
            if (value01 <= 0.0f) return 0.0f;
            return Mathf.Pow(10.0f, kAnnoIconExponentStart + kAnnoIconExponentRange * value01);
        }

        private static string TryEncodeAnnotationIconPngDataUri(Texture2D src)
        {
            if (src == null || src.width <= 0 || src.height <= 0) return "";
            try
            {
                // Always encode a compact icon size so all rows can keep iconImage
                // while keeping popup payload under DataChannel limits.
                RenderTexture prev = RenderTexture.active;
                RenderTexture tmp = null;
                Texture2D copy = null;
                try
                {
                    int targetW = Mathf.Max(1, Mathf.Min(src.width, kAnnotationIconEncodeMaxSize));
                    int targetH = Mathf.Max(1, Mathf.Min(src.height, kAnnotationIconEncodeMaxSize));
                    tmp = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(src, tmp);
                    RenderTexture.active = tmp;
                    copy = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
                    copy.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
                    copy.Apply(false, false);
                    byte[] png = copy.EncodeToPNG();
                    if (png != null && png.Length > 0)
                        return "data:image/png;base64," + Convert.ToBase64String(png);
                }
                finally
                {
                    RenderTexture.active = prev;
                    if (tmp != null) RenderTexture.ReleaseTemporary(tmp);
                    if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
                }
            }
            catch { }
            return "";
        }

        private static MethodInfo s_CachedApplyPanelSettings;
        private static bool s_ApplyPanelSettingsResolved;

        /// <summary>
        /// Forces PanelSettings.ApplyPanelSettings() on all PanelSettings assets.
        /// This refreshes m_TargetRect from GetDisplayRect(), which may have changed
        /// after the docked GameView was closed (deferred Close()) and the floating
        /// GameView became the only PlayModeView. Without this, m_TargetRect retains
        /// the stale docked GV size, causing UI Toolkit to lay out at the wrong
        /// resolution → horizontal compression.
        /// </summary>
        private static void ForceApplyPanelSettings()
        {
            try
            {
                var psType = System.Type.GetType("UnityEngine.UIElements.PanelSettings, UnityEngine.UIElementsModule")
                    ?? System.Type.GetType("UnityEngine.UIElements.PanelSettings, UnityEngine");
                if (psType == null) return;

                if (!s_ApplyPanelSettingsResolved)
                {
                    s_CachedApplyPanelSettings = psType.GetMethod("ApplyPanelSettings",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    s_ApplyPanelSettingsResolved = true;
                }
                if (s_CachedApplyPanelSettings == null) return;

                var allPS = Resources.FindObjectsOfTypeAll(psType);
                if (allPS == null) return;

                foreach (var psObj in allPS)
                {
                    if (psObj == null) continue;
                    s_CachedApplyPanelSettings.Invoke(psObj, null);
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Closes all existing GameView windows except those whose instance ID is in
        /// <paramref name="excludeInstanceIds"/>. This prevents the docked GameView's
        /// size info (PanelSettings.GetGameViewResolution) from interfering with the
        /// floating streaming view, which causes UI compression/distortion.
        /// Before closing, saves a sibling window type name (another tab in the same
        /// DockArea) to SessionState so <see cref="RestoreDockedGameViewAfterStreaming"/>
        /// can dock the restored GameView back into the same tab group.
        /// Sets <see cref="s_ClosedDockedGameViewForStreaming"/> if any were closed.
        /// </summary>
        private static void CloseExistingGameViewsForStreaming(HashSet<long> excludeInstanceIds)
        {
            System.Type gameViewType = ResolveEditorWindowType("UnityEditor.GameView");
            if (gameViewType == null) return;

            if (excludeInstanceIds == null)
                excludeInstanceIds = new HashSet<long>();

            var toClose = new List<EditorWindow>();
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (w == null || w.GetType() != gameViewType) continue;
                if (excludeInstanceIds.Contains(w.GetStableInstanceId())) continue;
                toClose.Add(w);
            }

            if (toClose.Count == 0) return;

            // Before closing, find a sibling window in the same DockArea so we can
            // restore the GameView to the same dock position later.
            string siblingTypeName = null;
            foreach (var gv in toClose)
            {
                object dockArea = GetParentView(gv);
                if (dockArea == null || dockArea.GetType().Name != "DockArea") continue;

                FieldInfo panesField = dockArea.GetType().GetField("m_Panes",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (panesField == null) continue;
                var panes = panesField.GetValue(dockArea) as System.Collections.IList;
                if (panes == null) continue;

                foreach (var pane in panes)
                {
                    if (pane is EditorWindow sibling && sibling != gv && sibling.GetType() != gameViewType)
                    {
                        siblingTypeName = sibling.GetType().FullName;
                        break;
                    }
                }
                if (siblingTypeName != null) break;
            }

            if (siblingTypeName != null)
                SessionState.SetString(kClosedDockedGameViewSiblingKey, siblingTypeName);

            int closedCount = 0;
            foreach (var w in toClose)
            {
                try { w.Close(); closedCount++; }
                catch (Exception ex) { CodelyLogger.LogWarning($"[NWB-Offscreen] Failed to close docked GameView: {ex.Message}"); }
            }

            s_ClosedDockedGameViewForStreaming = true;
            SessionState.SetBool(kClosedDockedGameViewKey, true);
            CodelyLogger.Log($"[NWB-Offscreen] Closed {closedCount} docked GameView(s) to prevent size interference with floating streaming view (sibling={siblingTypeName ?? "none"})");
        }

        /// <summary>
        /// Reopens a docked GameView window after streaming stops, if we previously
        /// closed it via <see cref="CloseExistingGameViewsForStreaming"/>. Uses AddTab
        /// on an existing DockArea to dock the GameView back instead of creating a
        /// floating window.
        /// </summary>
        private static void RestoreDockedGameViewAfterStreaming()
        {
            SessionState.EraseBool(kClosedDockedGameViewKey);

            System.Type gameViewType = ResolveEditorWindowType("UnityEditor.GameView");
            if (gameViewType == null) return;

            // Check if a GameView already exists (e.g. user reopened it manually).
            bool exists = false;
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (w != null && w.GetType() == gameViewType) { exists = true; break; }
            }
            if (exists)
            {
                SessionState.EraseString(kClosedDockedGameViewSiblingKey);
                return;
            }

            // Find a DockArea to dock the GameView into.
            // Priority: saved sibling → SceneView → Inspector → any docked window.
            object targetDockArea = null;
            string savedSibling = SessionState.GetString(kClosedDockedGameViewSiblingKey, "");
            SessionState.EraseString(kClosedDockedGameViewSiblingKey);

            var dockCandidates = new List<string>();
            if (!string.IsNullOrEmpty(savedSibling))
                dockCandidates.Add(savedSibling);
            dockCandidates.Add("UnityEditor.SceneView");
            dockCandidates.Add("UnityEditor.InspectorWindow");
            dockCandidates.Add("UnityEditor.ConsoleWindow");
            dockCandidates.Add("UnityEditor.ProjectBrowser");

            foreach (string candidateTypeName in dockCandidates)
            {
                System.Type candidateType = ResolveEditorWindowType(candidateTypeName);
                if (candidateType == null) continue;
                foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
                {
                    if (w == null || w.GetType() != candidateType) continue;
                    object da = GetParentView(w);
                    if (da != null && da.GetType().Name == "DockArea")
                    {
                        targetDockArea = da;
                        break;
                    }
                }
                if (targetDockArea != null) break;
            }

            try
            {
                EditorWindow gameView = ScriptableObject.CreateInstance(gameViewType) as EditorWindow;
                if (gameView == null)
                {
                    CodelyLogger.LogWarning("[NWB-Offscreen] Failed to create GameView for restore");
                    return;
                }

                if (targetDockArea != null)
                {
                    MethodInfo addTab = targetDockArea.GetType().GetMethod("AddTab",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new System.Type[] { typeof(EditorWindow), typeof(bool) },
                        null);
                    if (addTab != null)
                    {
                        addTab.Invoke(targetDockArea, new object[] { gameView, true });
                        CodelyLogger.Log("[NWB-Offscreen] Restored docked GameView via AddTab after streaming");
                        return;
                    }
                }

                // Fallback: use GetWindow which may create a floating window,
                // but at least the GameView will be available.
                gameView.Close();
                EditorWindow.GetWindow(gameViewType, false, null, true);
                CodelyLogger.Log("[NWB-Offscreen] Restored GameView via GetWindow fallback after streaming");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] Failed to restore docked GameView: {ex.Message}");
            }
        }

        private static PropertyInfo s_DockedProp;
        private static bool s_DockedPropResolved;
        /// <summary>
        /// Reads <c>EditorWindow.docked</c> via reflection. In the Tuanjie engine this
        /// property is internal (unlike standard Unity 2020.1+, where it is public), so
        /// direct member access fails to compile. Returns false when unavailable.
        /// </summary>
        internal static bool IsWindowDocked(EditorWindow window)
        {
            if (window == null) return false;
            if (!s_DockedPropResolved)
            {
                s_DockedPropResolved = true;
                try
                {
                    s_DockedProp = typeof(EditorWindow).GetProperty(
                        "docked",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                catch { s_DockedProp = null; }
            }
            if (s_DockedProp != null && s_DockedProp.PropertyType == typeof(bool))
            {
                try { return (bool)s_DockedProp.GetValue(window, null); }
                catch { /* ignore */ }
            }
            return false;
        }

        private static string ResolveAnnotationIconDataUri(int classID, string scriptClass, bool hasIcon)
        {
            Texture2D iconTex = null;
            try
            {
                if (!string.IsNullOrEmpty(scriptClass))
                {
                    // EditorGUIUtility.GetScript is internal; use reflection.
                    var getScriptMethod = typeof(EditorGUIUtility).GetMethod("GetScript",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        null, new System.Type[] { typeof(string) }, null);
                    if (getScriptMethod != null)
                    {
                        UnityEngine.Object script = getScriptMethod.Invoke(null, new object[] { scriptClass }) as UnityEngine.Object;
                        if (script != null)
                        {
                            // EditorGUIUtility.GetIconForObject is internal; use reflection.
                            var getIconMethod = typeof(EditorGUIUtility).GetMethod("GetIconForObject",
                                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                                null, new System.Type[] { typeof(UnityEngine.Object) }, null);
                            if (getIconMethod != null)
                                iconTex = getIconMethod.Invoke(null, new object[] { script }) as Texture2D;
                        }
                    }
                }
                if (iconTex == null && hasIcon)
                {
                    // AssetPreview.GetMiniTypeThumbnailFromClassID is internal; use reflection.
                    var getThumbMethod = typeof(AssetPreview).GetMethod("GetMiniTypeThumbnailFromClassID",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        null, new System.Type[] { typeof(int) }, null);
                    if (getThumbMethod != null)
                        iconTex = getThumbMethod.Invoke(null, new object[] { classID }) as Texture2D;
                }
            }
            catch { }
            return TryEncodeAnnotationIconPngDataUri(iconTex);
        }

        /// <summary>
        /// Extract full AnnotationWindow popup data including top toggles,
        /// search text, and both gizmo/icon states.
        /// </summary>
        private static bool TryExtractAnnotationWindowPopupFull(float relX, float relY)
        {
            EnsureAnnotationReflection();
            if (s_GetAnnotationsMethod == null) return false;

            try
            {
                System.Array annotations = s_GetAnnotationsMethod.Invoke(null, null) as System.Array;
                if (annotations == null) return false;

                const int kHasIcon = 1;
                const int kHasGizmo = 2;
                const int kIsDisabled = 4;
                const int maxShowRecent = 5;

                FieldInfo flagsFld = null;
                FieldInfo classIDFld = null;
                FieldInfo scriptClassFld = null;
                FieldInfo gizmoEnabledFld = null;
                FieldInfo iconEnabledFld = null;
                if (annotations.Length > 0)
                {
                    System.Type annType = annotations.GetValue(0).GetType();
                    flagsFld = annType.GetField("flags", BindingFlags.Instance | BindingFlags.Public);
                    classIDFld = annType.GetField("classID", BindingFlags.Instance | BindingFlags.Public);
                    scriptClassFld = annType.GetField("scriptClass", BindingFlags.Instance | BindingFlags.Public);
                    gizmoEnabledFld = annType.GetField("gizmoEnabled", BindingFlags.Instance | BindingFlags.Public);
                    iconEnabledFld = annType.GetField("iconEnabled", BindingFlags.Instance | BindingFlags.Public);
                    if (flagsFld == null || classIDFld == null || scriptClassFld == null || gizmoEnabledFld == null || iconEnabledFld == null)
                        return false;
                }

                // Resolve built-in type names through UnityType lookup.
                System.Type unityTypeClass = typeof(EditorWindow).Assembly.GetType("UnityEditor.UnityType");
                MethodInfo findTypeMethod = unityTypeClass?.GetMethod("FindTypeByPersistentTypeID",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new System.Type[] { typeof(int) }, null);

                Func<object, string> buildKey = (ann) =>
                {
                    int classID = (int)classIDFld.GetValue(ann);
                    string scriptClass = scriptClassFld.GetValue(ann) as string ?? "";
                    return $"{classID}|{scriptClass}";
                };

                var byKey = new Dictionary<string, object>();
                var recentKeys = new List<string>();
                var scriptKeys = new List<string>();
                var builtinKeys = new List<string>();

                for (int i = 0; i < annotations.Length; i++)
                {
                    object ann = annotations.GetValue(i);
                    string key = buildKey(ann);
                    if (!byKey.ContainsKey(key))
                        byKey.Add(key, ann);

                    string scriptClass = scriptClassFld.GetValue(ann) as string ?? "";
                    if (string.IsNullOrEmpty(scriptClass)) builtinKeys.Add(key);
                    else scriptKeys.Add(key);
                }

                if (s_GetRecentlyChangedAnnotationsMethod != null)
                {
                    System.Array recents = s_GetRecentlyChangedAnnotationsMethod.Invoke(null, null) as System.Array;
                    if (recents != null)
                    {
                        for (int i = 0; i < recents.Length && recentKeys.Count < maxShowRecent; i++)
                        {
                            string key = buildKey(recents.GetValue(i));
                            if (byKey.ContainsKey(key) && !recentKeys.Contains(key))
                                recentKeys.Add(key);
                        }
                    }
                }

                scriptKeys.Sort(StringComparer.OrdinalIgnoreCase);
                builtinKeys.Sort(StringComparer.OrdinalIgnoreCase);

                string normalizedFilter = (s_AnnotationSearchFilter ?? "").Trim();
                bool hasFilter = !string.IsNullOrEmpty(normalizedFilter);

                string ResolveName(object ann)
                {
                    int classID = (int)classIDFld.GetValue(ann);
                    string scriptClass = scriptClassFld.GetValue(ann) as string ?? "";
                    if (!string.IsNullOrEmpty(scriptClass)) return scriptClass;
                    if (findTypeMethod != null)
                    {
                        try
                        {
                            object utype = findTypeMethod.Invoke(null, new object[] { classID });
                            PropertyInfo nameProp = utype?.GetType().GetProperty("name", BindingFlags.Instance | BindingFlags.Public);
                            string n = nameProp?.GetValue(utype) as string;
                            if (!string.IsNullOrEmpty(n)) return n;
                        }
                        catch { }
                    }
                    return $"ClassID_{classID}";
                }

                bool MatchFilter(object ann)
                {
                    if (!hasFilter) return true;
                    string n = ResolveName(ann);
                    return n.IndexOf(normalizedFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                }

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "annotation_full";
                s_LastFrontendPopupX = relX;
                s_LastFrontendPopupY = relY;

                bool BuildAnnotationPayload(bool includeIconImage, out string payloadResult, out int payloadBytesResult, out string[] cachedPaths)
                {
                    var pathList = new List<string>();
                    var sb = new StringBuilder(8192);
                    sb.Append("{\"type\":\"popup_show\",\"id\":\"");
                    sb.Append(s_CurrentFrontendPopupId);
                    sb.Append("\",\"x\":");
                    sb.Append(Mathf.RoundToInt(relX));
                    sb.Append(",\"y\":");
                    sb.Append(Mathf.RoundToInt(relY));
                    sb.Append(",\"sourceType\":\"annotation_full\"");
                    sb.Append(",\"showColumnHeaders\":true");
                    sb.Append(",\"searchText\":\"");
                    sb.Append(EscapeJsonString(s_AnnotationSearchFilter ?? ""));
                    sb.Append("\"");

                    // Top toggles.
                    sb.Append(",\"headerToggles\":[");
                    sb.Append("{\"key\":\"use3dGizmos\",\"label\":\"3D Icons\",\"tooltip\":\"Show/Hide Icon\",\"checked\":");
                    sb.Append(GetStaticBool(s_AnnoUse3dGizmosProp, true) ? "true" : "false");
                    sb.Append(",\"enabled\":true},");
                    sb.Append("{\"key\":\"fadeGizmos\",\"label\":\"Fade Gizmos\",\"tooltip\":\"Fade out and stop rendering gizmos that are small on screen\",\"checked\":");
                    sb.Append(GetStaticBool(s_AnnoFadeGizmosProp, false) ? "true" : "false");
                    sb.Append(",\"enabled\":true},");
                    sb.Append("{\"key\":\"showSelectionOutline\",\"label\":\"Selection Outline\",\"tooltip\":\"Toggle Selection Outline\",\"checked\":");
                    sb.Append(GetStaticBool(s_AnnoShowSelectionOutlineProp, true) ? "true" : "false");
                    sb.Append(",\"enabled\":true},");
                    sb.Append("{\"key\":\"showSelectionWire\",\"label\":\"Selection Wire\",\"tooltip\":\"Toggle Selection Wire\",\"checked\":");
                    sb.Append(GetStaticBool(s_AnnoShowSelectionWireProp, true) ? "true" : "false");
                    sb.Append(",\"enabled\":true},");
                    sb.Append("{\"key\":\"showTerrainDebugWarnings\",\"label\":\"Terrain Debug Warnings\",\"tooltip\":\"Show Terrain Debug Warnings\",\"checked\":");
                    sb.Append(GetStaticBool(s_AnnoWindowTerrainWarnProp, true) ? "true" : "false");
                    sb.Append(",\"enabled\":true}");
                    sb.Append("]");

                    // Top sliders.
                    float iconSizeTexel = GetStaticFloat(s_AnnoIconSizeProp, 0.01f);
                    float iconSize01 = Mathf.Clamp01(ConvertTexelWorldSizeTo01(iconSizeTexel));
                    sb.Append(",\"headerSliders\":[");
                    sb.Append("{\"key\":\"iconSize\",\"label\":\"3D Icon Size\",\"tooltip\":\"Show/Hide Icon\",\"value\":");
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F4}", iconSize01);
                    sb.Append(",\"min\":0,\"max\":1,\"step\":0.001,\"showValue\":false},");
                    sb.Append("{\"key\":\"fadeGizmoSize\",\"label\":\"Fade Size\",\"tooltip\":\"Fade out and stop rendering gizmos that are small on screen\",\"value\":");
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}", GetStaticFloat(s_AnnoFadeGizmoSizeProp, 5f));
                    sb.Append(",\"min\":2,\"max\":10,\"step\":0.1,\"showValue\":false}");
                    sb.Append("]");

                    sb.Append(",\"items\":[");
                    bool firstItem = true;
                    int index = 0;

                    void AppendSection(string section, List<string> keys)
                    {
                        bool hasAny = false;
                        for (int i = 0; i < keys.Count; i++)
                        {
                            object ann = byKey[keys[i]];
                            if (MatchFilter(ann)) { hasAny = true; break; }
                        }
                        if (!hasAny) return;

                        if (!firstItem) sb.Append(',');
                        sb.Append("{\"index\":-1,\"label\":\"");
                        sb.Append(EscapeJsonString(section));
                        sb.Append("\",\"path\":\"\",\"enabled\":false,\"checked\":false,\"separator\":false}");
                        firstItem = false;

                        for (int i = 0; i < keys.Count; i++)
                        {
                            object ann = byKey[keys[i]];
                            if (!MatchFilter(ann)) continue;

                            int flags = (int)flagsFld.GetValue(ann);
                            int classID = (int)classIDFld.GetValue(ann);
                            string scriptClass = scriptClassFld.GetValue(ann) as string ?? "";
                            bool hasGizmo = (flags & kHasGizmo) != 0;
                            bool hasIconFlag = (flags & kHasIcon) != 0;
                            // Match GizmoInfo.thumb: script entries can still have an icon even
                            // when kHasIcon is not set in annotation flags.
                            bool hasIcon = !string.IsNullOrEmpty(scriptClass) || hasIconFlag;
                            bool disabled = (flags & kIsDisabled) != 0;
                            bool gizmoChecked = (int)gizmoEnabledFld.GetValue(ann) > 0;
                            bool iconChecked = (int)iconEnabledFld.GetValue(ann) > 0;
                            string safeScript = string.IsNullOrEmpty(scriptClass) ? "-" : scriptClass;
                            string gizmoPath = $"annotation|{classID}|{safeScript}";
                            string iconPath = $"annotation_icon|{classID}|{safeScript}";
                            string iconImage = "";
                            if (includeIconImage && hasIcon)
                            {
                                iconImage = ResolveAnnotationIconDataUri(classID, scriptClass, hasIconFlag);
                            }
                            pathList.Add(gizmoPath);

                            if (!firstItem) sb.Append(',');
                            sb.Append("{\"index\":");
                            sb.Append(index);
                            sb.Append(",\"label\":\"");
                            string resolvedName = ResolveName(ann);
                            sb.Append(EscapeJsonString(resolvedName));
                            sb.Append("\",\"path\":\"");
                            sb.Append(EscapeJsonString(gizmoPath));
                            sb.Append("\",\"enabled\":true,\"checked\":");
                            sb.Append(gizmoChecked ? "true" : "false");
                            sb.Append(",\"separator\":false,\"keepOpen\":true");
                            sb.Append(",\"hasGizmo\":");
                            sb.Append(hasGizmo ? "true" : "false");
                            sb.Append(",\"hasIcon\":");
                            sb.Append(hasIcon ? "true" : "false");
                            sb.Append(",\"disabled\":");
                            sb.Append(disabled ? "true" : "false");
                            sb.Append(",\"gizmoChecked\":");
                            sb.Append(gizmoChecked ? "true" : "false");
                            sb.Append(",\"iconChecked\":");
                            sb.Append(iconChecked ? "true" : "false");
                            sb.Append(",\"iconPath\":\"");
                            sb.Append(EscapeJsonString(iconPath));
                            sb.Append("\",\"iconImage\":\"");
                            sb.Append(EscapeJsonString(iconImage));
                            sb.Append("\"}");
                            firstItem = false;
                            index++;
                        }
                    }

                    AppendSection("Recently Changed", recentKeys);
                    AppendSection("Scripts", scriptKeys);
                    AppendSection("Built-in Components", builtinKeys);

                    sb.Append("]}");
                    payloadResult = sb.ToString();
                    payloadBytesResult = Encoding.UTF8.GetByteCount(payloadResult);
                    cachedPaths = pathList.ToArray();
                    return true;
                }

                BuildAnnotationPayload(true, out string payload, out int payloadBytes, out string[] popupPaths);
                s_CachedPopupItemPaths = popupPaths;

                bool sent;
                if (payloadBytes > kMaxDataChannelPayloadBytes)
                {
                    sent = SendPopupPayloadChunked(s_CurrentFrontendPopupId, payload, payloadBytes);
                    if (!sent)
                    {
                        CodelyLogger.LogWarning($"[NWB-Annotation] Chunked popup send failed: id={s_CurrentFrontendPopupId} bytes={payloadBytes}");
                    }
                    else
                    {
                        LogVerbose($"[NWB-Annotation] Sent chunked popup with icons: id={s_CurrentFrontendPopupId} bytes={payloadBytes}");
                    }
                }
                else
                {
                    sent = SendDataChannelMessage(payload);
                }
                s_FrontendPopupSent = sent;
                if (sent)
                {
                    CancelNativeDelayedPopup();
                    LogVerbose($"[NWB-Annotation] Sent full annotation popup: id={s_CurrentFrontendPopupId} filter='{s_AnnotationSearchFilter}' bytes={payloadBytes}");
                }
                else
                    CodelyLogger.LogWarning($"[NWB-Annotation] Failed to send annotation popup: id={s_CurrentFrontendPopupId} bytes={payloadBytes}");
                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Annotation] Error extracting full popup: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        // Panel protocol code (Grid, Snap, DrawMode) is in NativeWindowBridgeHost.Panel.cs

        /// <summary>
        /// Extract popup data from ConnectionTreeViewWindow (Console/Profiler
        /// connection target selector). Items are ConnectionDropDownItem objects
        /// stored in the connectionItems field, each with DisplayName,
        /// m_Connected (Func<bool>), m_OnSelected (Action), and m_Disabled.
        /// </summary>
        private static bool TryExtractConnectionTreeViewPopup(
            object containerWindow, float relX, float relY, float popW, float popH)
        {
            try
            {
                // Navigate to PopupWindow.m_WindowContent (ConnectionTreeViewWindow)
                var rootViewProp = containerWindow.GetType().GetProperty("rootView",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (rootViewProp == null) return false;
                object rootView = rootViewProp.GetValue(containerWindow);
                if (rootView == null) return false;

                var actualViewProp = rootView.GetType().GetProperty("actualView",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (actualViewProp == null) return false;
                object popupWindow = actualViewProp.GetValue(rootView);
                if (popupWindow == null) return false;

                FieldInfo wcField = popupWindow.GetType().GetField("m_WindowContent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (wcField == null) return false;
                object windowContent = wcField.GetValue(popupWindow);
                if (windowContent == null) return false;

                // Get connectionItems (List<ConnectionDropDownItem>)
                FieldInfo itemsField = windowContent.GetType().GetField("connectionItems",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (itemsField == null)
                {
                    LogVerbose("[NWB-ConnectionPopup] connectionItems field not found");
                    return false;
                }
                System.Collections.IList items = itemsField.GetValue(windowContent) as System.Collections.IList;
                if (items == null || items.Count == 0)
                {
                    LogVerbose("[NWB-ConnectionPopup] No connection items");
                    return false;
                }

                // Build popup JSON and cache callbacks
                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;

                var callbacks = new System.Action[items.Count];
                var sb = new StringBuilder(2048);
                sb.Append("{\"type\":\"popup_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                sb.Append(",\"width\":");
                sb.Append(Mathf.RoundToInt(popW));
                sb.Append(",\"height\":");
                sb.Append(Mathf.RoundToInt(popH));
                sb.Append(",\"items\":[");

                // Reflection fields for ConnectionDropDownItem
                System.Type itemType = items[0].GetType();
                FieldInfo connectedField = null;
                FieldInfo onSelectedField = null;
                FieldInfo disabledField = null;
                PropertyInfo displayNameProp = null;

                for (System.Type t = itemType; t != null; t = t.BaseType)
                {
                    if (connectedField == null)
                        connectedField = t.GetField("m_Connected",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (onSelectedField == null)
                        onSelectedField = t.GetField("m_OnSelected",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (disabledField == null)
                        disabledField = t.GetField("m_Disabled",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (displayNameProp == null)
                        displayNameProp = t.GetProperty("displayName",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }

                int validCount = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    object item = items[i];
                    if (item == null) continue;

                    string label = displayNameProp != null
                        ? displayNameProp.GetValue(item) as string ?? ""
                        : "";
                    // Skip group headers (items with children but no onSelected)
                    if (string.IsNullOrEmpty(label)) continue;

                    bool isConnected = false;
                    bool isDisabled = false;
                    System.Action onSelected = null;

                    try
                    {
                        if (connectedField != null)
                        {
                            var connFunc = connectedField.GetValue(item) as System.Func<bool>;
                            if (connFunc != null) isConnected = connFunc();
                        }
                        if (disabledField != null)
                            isDisabled = (bool)disabledField.GetValue(item);
                        if (onSelectedField != null)
                            onSelected = onSelectedField.GetValue(item) as System.Action;
                    }
                    catch { }

                    callbacks[i] = onSelected;

                    if (validCount > 0) sb.Append(',');
                    sb.Append("{\"index\":");
                    sb.Append(i);
                    sb.Append(",\"path\":\"\",\"label\":\"");
                    sb.Append(EscapeJsonString(label));
                    sb.Append("\",\"tooltip\":\"\",\"enabled\":");
                    sb.Append(!isDisabled ? "true" : "false");
                    sb.Append(",\"checked\":");
                    sb.Append(isConnected ? "true" : "false");
                    sb.Append(",\"separator\":false}");
                    validCount++;
                }

                sb.Append("]}");

                if (validCount == 0)
                {
                    LogVerbose("[NWB-ConnectionPopup] No valid items after filtering");
                    return false;
                }

                string json = sb.ToString();
                bool sent = SendDataChannelMessage(json);
                s_FrontendPopupSent = sent;
                if (sent)
                {
                    s_PopupSourceType = "connection_tree";
                    s_CachedConnectionCallbacks = callbacks;
                    s_SuppressTargetRepaintWhilePopupOpen = true;
                    LogVerbose($"[NWB-ConnectionPopup] Sent popup: id={s_CurrentFrontendPopupId} items={validCount}");
                }
                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ConnectionPopup] Extraction error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Try to extract menu data from a FlexibleMenu inside a PopupWindow.
        /// FlexibleMenu uses IFlexibleMenuItemProvider for its items, and
        /// GameViewSizeMenu is the specific subclass for resolution dropdown.
        /// </summary>
        private static bool TryExtractFlexibleMenuPopup(object containerWindow, float relX, float relY, float popW, float popH)
        {
            try
            {
                // Walk: ContainerWindow -> rootView -> actualView (HostView) -> hosted window (PopupWindow)
                System.Type cwType = containerWindow.GetType();
                PropertyInfo rootViewProp = cwType.GetProperty("rootView",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (rootViewProp == null) return false;

                object rootView = rootViewProp.GetValue(containerWindow);
                if (rootView == null) return false;

                // Find actualView (the EditorWindow hosted by the HostView).
                PropertyInfo actualViewProp = rootView.GetType().GetProperty("actualView",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (actualViewProp == null) return false;

                object popupWindow = actualViewProp.GetValue(rootView);
                if (popupWindow == null) return false;

                LogVerbose($"[NWB-FrontendPopup] PopupWindow type: {popupWindow.GetType().FullName}");

                // Get m_WindowContent (PopupWindowContent)
                FieldInfo wcField = popupWindow.GetType().GetField("m_WindowContent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (wcField == null)
                {
                    LogVerbose("[NWB-FrontendPopup] m_WindowContent field not found");
                    return false;
                }

                object windowContent = wcField.GetValue(popupWindow);
                if (windowContent == null)
                {
                    LogVerbose("[NWB-FrontendPopup] m_WindowContent is null");
                    return false;
                }

                LogVerbose($"[NWB-FrontendPopup] WindowContent type: {windowContent.GetType().FullName}");

                // Check if it's a FlexibleMenu or its subclass.
                System.Type contentType = windowContent.GetType();
                FieldInfo itemProviderField = null;
                FieldInfo callbackField = null;
                FieldInfo sepIndicesField = null;

                // Walk up type hierarchy to find FlexibleMenu fields.
                for (System.Type t = contentType; t != null; t = t.BaseType)
                {
                    if (itemProviderField == null)
                        itemProviderField = t.GetField("m_ItemProvider",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (callbackField == null)
                        callbackField = t.GetField("m_ItemClickedCallback",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (sepIndicesField == null)
                        sepIndicesField = t.GetField("m_SeperatorIndices",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }

                if (itemProviderField == null)
                {
                    // ProjectBrowser search filter popup uses UnityEditor.PopupList.
                    if (contentType.FullName == "UnityEditor.PopupList" || contentType.Name == "PopupList")
                    {
                        LogVerbose("[NWB-FrontendPopup] Detected PopupList, using PopupList extraction path");
                        return TryExtractPopupListFromWindowContent(windowContent, relX, relY, popW, popH);
                    }
                    LogVerbose("[NWB-FrontendPopup] Not a FlexibleMenu (no m_ItemProvider field)");
                    return false;
                }

                object itemProvider = itemProviderField.GetValue(windowContent);
                if (itemProvider == null)
                {
                    LogVerbose("[NWB-FrontendPopup] m_ItemProvider is null");
                    return false;
                }

                // Get selected index via property.
                PropertyInfo selIdxProp = contentType.GetProperty("selectedIndex",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                int selectedIdx = selIdxProp != null ? (int)selIdxProp.GetValue(windowContent) : -1;

                // Get separator indices.
                int[] separatorIndices = sepIndicesField?.GetValue(windowContent) as int[] ?? new int[0];

                // Use IFlexibleMenuItemProvider interface methods.
                System.Type providerType = itemProvider.GetType();
                MethodInfo countMethod = providerType.GetMethod("Count",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo getNameMethod = providerType.GetMethod("GetName",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new System.Type[] { typeof(int) }, null);
                MethodInfo getItemMethod = providerType.GetMethod("GetItem",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new System.Type[] { typeof(int) }, null);

                if (countMethod == null || getNameMethod == null)
                {
                    LogVerbose("[NWB-FrontendPopup] IFlexibleMenuItemProvider methods not found");
                    return false;
                }

                int count = (int)countMethod.Invoke(itemProvider, null);
                if (count <= 0)
                {
                    LogVerbose("[NWB-FrontendPopup] FlexibleMenu has 0 items");
                    return false;
                }

                // Cache for callback execution.
                s_CachedFlexibleMenu = windowContent;
                s_CachedFlexibleMenuItemProvider = itemProvider;
                if (callbackField != null)
                    s_CachedFlexibleMenuCallback = callbackField.GetValue(windowContent) as System.Delegate;

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "flexible";
                s_CachedPopupItemPaths = new string[count];

                var sepSet = new System.Collections.Generic.HashSet<int>(separatorIndices);

                // Detect GameViewSizeMenu extra toggles (Low Resolution, VSync).
                bool isGameViewSizeMenu = contentType.FullName == "UnityEditor.GameViewSizeMenu";
                bool hasLowResToggle = false;
                bool lowResChecked = false;
                bool lowResEnabled = true;
                bool hasVSyncToggle = false;
                bool vSyncChecked = false;
                bool hasAddButton = false;
                object cachedGameView = null;

                if (isGameViewSizeMenu)
                {
                    // Read m_GameView (IGameViewSizeMenuUser) via reflection.
                    FieldInfo gameViewField = contentType.GetField("m_GameView",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (gameViewField != null)
                    {
                        cachedGameView = gameViewField.GetValue(windowContent);
                        if (cachedGameView != null)
                        {
                            System.Type gvType = cachedGameView.GetType();

                            // Low Resolution Aspect Ratios toggle
                            PropertyInfo lowResProp = gvType.GetProperty("lowResolutionForAspectRatios",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            PropertyInfo forceResProp = gvType.GetProperty("forceLowResolutionAspectRatios",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (lowResProp != null)
                            {
                                hasLowResToggle = true;
                                bool forceVal = forceResProp != null && (bool)forceResProp.GetValue(cachedGameView);
                                lowResChecked = forceVal || (bool)lowResProp.GetValue(cachedGameView);
                                lowResEnabled = !forceVal;
                            }

                            // VSync toggle — only visible on Metal/Vulkan/D3D11/D3D12/OpenGLCore.
                            PropertyInfo vSyncProp = gvType.GetProperty("vSyncEnabled",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (vSyncProp != null)
                            {
                                var gfxType = SystemInfo.graphicsDeviceType;
                                hasVSyncToggle = gfxType == UnityEngine.Rendering.GraphicsDeviceType.Metal ||
                                    gfxType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan ||
                                    gfxType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 ||
                                    gfxType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12 ||
                                    gfxType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore;
                                if (hasVSyncToggle)
                                    vSyncChecked = (bool)vSyncProp.GetValue(cachedGameView);
                            }
                        }
                    }

                    // Check for "+" add button (m_ShowAddNewPresetItem in FlexibleMenu).
                    FieldInfo modifyUIField = null;
                    for (System.Type t = contentType; t != null; t = t.BaseType)
                    {
                        if (modifyUIField == null)
                            modifyUIField = t.GetField("m_ModifyItemUI",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    }
                    hasAddButton = modifyUIField != null && modifyUIField.GetValue(windowContent) != null;
                }

                // Cache the IGameViewSizeMenuUser for handling toggle actions.
                s_CachedGameViewSizeMenuUser = cachedGameView;

                var sb = new StringBuilder(2048);
                sb.Append("{\"type\":\"popup_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                sb.Append(",\"width\":");
                sb.Append(Mathf.RoundToInt(popW));
                sb.Append(",\"height\":");
                sb.Append(Mathf.RoundToInt(popH));

                // Extra header toggles for GameViewSizeMenu.
                if (isGameViewSizeMenu)
                {
                    sb.Append(",\"headerToggles\":[");
                    bool firstToggle = true;
                    if (hasLowResToggle)
                    {
                        sb.Append("{\"key\":\"lowRes\",\"label\":\"Low Resolution Aspect Ratios\",\"checked\":");
                        sb.Append(lowResChecked ? "true" : "false");
                        sb.Append(",\"enabled\":");
                        sb.Append(lowResEnabled ? "true" : "false");
                        sb.Append('}');
                        firstToggle = false;
                    }
                    if (hasVSyncToggle)
                    {
                        if (!firstToggle) sb.Append(',');
                        sb.Append("{\"key\":\"vSync\",\"label\":\"VSync (Game view only)\",\"checked\":");
                        sb.Append(vSyncChecked ? "true" : "false");
                        sb.Append(",\"enabled\":true}");
                    }
                    sb.Append(']');
                    sb.Append(",\"showAddButton\":");
                    sb.Append(hasAddButton ? "true" : "false");
                }

                sb.Append(",\"items\":[");

                int validCount = 0;
                for (int i = 0; i < count; i++)
                {
                    string name = "";
                    try { name = (string)getNameMethod.Invoke(itemProvider, new object[] { i }); } catch { }
                    string itemTooltip = "";
                    if (getItemMethod != null)
                    {
                        try
                        {
                            object itemObj = getItemMethod.Invoke(itemProvider, new object[] { i });
                            if (itemObj != null)
                            {
                                PropertyInfo tpProp = itemObj.GetType().GetProperty("tooltip",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                itemTooltip = tpProp?.GetValue(itemObj) as string ?? "";
                                if (string.IsNullOrEmpty(itemTooltip))
                                {
                                    PropertyInfo cntProp = itemObj.GetType().GetProperty("content",
                                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    object gc = cntProp?.GetValue(itemObj);
                                    if (gc != null)
                                    {
                                        PropertyInfo gct = gc.GetType().GetProperty("tooltip",
                                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        itemTooltip = gct?.GetValue(gc) as string ?? "";
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    s_CachedPopupItemPaths[i] = name;

                    bool isChecked = (i == selectedIdx);

                    if (validCount > 0) sb.Append(',');
                    sb.Append("{\"index\":");
                    sb.Append(i);
                    sb.Append(",\"path\":\"");
                    sb.Append(EscapeJsonString(name));
                    sb.Append("\",\"label\":\"");
                    sb.Append(EscapeJsonString(name));
                    sb.Append("\",\"tooltip\":\"");
                    sb.Append(EscapeJsonString(itemTooltip));
                    sb.Append("\",\"enabled\":true");
                    sb.Append(",\"checked\":");
                    sb.Append(isChecked ? "true" : "false");
                    sb.Append(",\"separator\":false");
                    sb.Append('}');
                    validCount++;

                    if (sepSet.Contains(i))
                    {
                        sb.Append(",{\"index\":-1,\"path\":\"\",\"label\":\"\",\"enabled\":false,\"checked\":false,\"separator\":true}");
                        validCount++;
                    }
                }

                sb.Append("]}");

                string json = sb.ToString();
                bool sent = SendDataChannelMessage(json);
                s_FrontendPopupSent = sent;

                if (sent)
                {
                    CancelNativeDelayedPopup();
                    LogVerbose($"[NWB-FrontendPopup] Sent FlexibleMenu popup: id={s_CurrentFrontendPopupId} items={count} selected={selectedIdx} pos=({relX:F0},{relY:F0}) isGameViewSizeMenu={isGameViewSizeMenu} lowRes={hasLowResToggle}:{lowResChecked} vSync={hasVSyncToggle}:{vSyncChecked} addBtn={hasAddButton}");
                }
                else
                    CodelyLogger.LogWarning("[NWB-FrontendPopup] Failed to send FlexibleMenu popup via DataChannel");

                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-FrontendPopup] FlexibleMenu extraction error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Escape special characters for JSON string values.
        /// </summary>
        private static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Handle a popup_select message from the browser. Execute the selected
        /// menu item via EditorApplication.ExecuteMenuItem using the full path
        /// stored in MenuController during popup creation.
        /// </summary>
        private static void HandleFrontendPopupSelect(string json)
        {
            try
            {
                string popupId = ExtractJsonString(json, "id");
                int index = ExtractJsonInt(json, "index");
                string path = ExtractJsonString(json, "path");

                if (s_PopupSourceType == "object_selector" &&
                    string.IsNullOrEmpty(path) &&
                    index >= 0 &&
                    s_CachedPopupItemPaths != null &&
                    index < s_CachedPopupItemPaths.Length)
                {
                    path = s_CachedPopupItemPaths[index];
                }

                LogVerbose($"[NWB-FrontendPopup] popup_select: id={popupId} index={index} path={path} source={s_PopupSourceType ?? "null"}");

                if (popupId != s_CurrentFrontendPopupId &&
                    !(s_PopupSourceType == "object_selector" && s_ObjectSelectorSessionActive) &&
                    !(s_PopupSourceType == "icon_selector" && s_IconSelectorSessionActive))
                {
                    CodelyLogger.LogWarning($"[NWB-FrontendPopup] Mismatched popup ID: got {popupId}, expected {s_CurrentFrontendPopupId}");
                    return;
                }

                // Handle delayed popup first — it uses index-based callback,
                // not menu path execution.
                if (s_PopupSourceType != null && s_PopupSourceType.StartsWith("delayedpopup"))
                {
                    // When using Unsupported.GetSubmenus fallback, the frontend
                    // index may differ from the original popup index. Map back
                    // via cached command indices.
                    int origIndex = index;
                    if (s_CachedPopupOriginalIndices != null &&
                        index >= 0 && index < s_CachedPopupOriginalIndices.Length)
                    {
                        origIndex = s_CachedPopupOriginalIndices[index];
                    }
                    LogVerbose($"[NWB-FrontendPopup] Executing delayed popup selection index={index} origIndex={origIndex}");
                    HandleDelayedPopupSelect(origIndex);
                    ClearFrontendPopupState();
                    return;
                }

                // Handle UITK toolbar dropdown selections. These popups
                // were built manually from C# property state, so we set the
                // corresponding property directly instead of ExecuteMenuItem.
                if (s_PopupSourceType == "uitk_pivotMode")
                {
                    if (path == "Center") UnityEditor.Tools.pivotMode = UnityEditor.PivotMode.Center;
                    else if (path == "Pivot") UnityEditor.Tools.pivotMode = UnityEditor.PivotMode.Pivot;
                    LogVerbose($"[NWB-UITK] Set pivotMode to: {path}");
                    if (s_OffscreenTarget != null) s_OffscreenTarget.Repaint();
                    ClearFrontendPopupState();
                    return;
                }
                if (s_PopupSourceType == "uitk_pivotRotation")
                {
                    if (path == "Global") UnityEditor.Tools.pivotRotation = UnityEditor.PivotRotation.Global;
                    else if (path == "Local") UnityEditor.Tools.pivotRotation = UnityEditor.PivotRotation.Local;
                    LogVerbose($"[NWB-UITK] Set pivotRotation to: {path}");
                    if (s_OffscreenTarget != null) s_OffscreenTarget.Repaint();
                    ClearFrontendPopupState();
                    return;
                }

                if (s_PopupSourceType == "pane_options")
                {
                    bool isCloseTab = path != null && path.Contains("Close Tab");

                    HandlePaneOptionsPopupSelect(index);
                    s_SuppressTargetRepaintWhilePopupOpen = false;
                    ClearFrontendPopupState();

                    // After Close Tab, check if all composite windows are gone.
                    if (isCloseTab && s_CompositeActive && s_CompositeSlots != null)
                    {
                        bool anyAlive = false;
                        foreach (var kv in s_CompositeSlots)
                        {
                            if (kv.Value?.Window != null) { anyAlive = true; break; }
                        }
                        if (!anyAlive)
                        {
                            CodelyLogger.Log("[NWB-Composite] All composite tabs closed via pane_options, stopping offscreen capture");
                            StopOffscreenCapture();
                            return;
                        }
                    }

                    if (s_OffscreenTarget != null)
                        s_OffscreenTarget.Repaint();
                    return;
                }

                if (s_PopupSourceType == "add_component")
                {
                    TryHandleAddComponentPopupSelect(path);
                    ClearFrontendPopupState();
                    if (s_OffscreenTarget != null)
                        s_OffscreenTarget.Repaint();
                    return;
                }

                if (s_PopupSourceType == "icon_selector")
                {
                    bool keepOpen = TryHandleIconSelectorPopupSelect(path);
                    if (!keepOpen)
                        ClearFrontendPopupState();
                    else if (s_OffscreenTarget != null)
                    {
                        s_OffscreenTarget.Repaint();
                        InternalEditorUtility.RepaintAllViews();
                    }
                    return;
                }

                if (s_PopupSourceType == "object_selector")
                {
                    TryHandleObjectSelectorPopupSelect(path);
                    ClearFrontendPopupState();
                    if (s_OffscreenTarget != null)
                    {
                        s_OffscreenTarget.Repaint();
                        InternalEditorUtility.RepaintAllViews();
                    }
                    return;
                }

                if (s_PopupSourceType == "advanced_dropdown")
                {
                    bool keepOpen = TryHandleAdvancedDropdownPopupSelect(path);
                    if (!keepOpen)
                        ClearFrontendPopupState();
                    if (s_OffscreenTarget != null)
                        s_OffscreenTarget.Repaint();
                    return;
                }

                if (s_PopupSourceType == "popup_list")
                {
                    HandlePopupListSelect(index, path);
                    s_SuppressTargetRepaintWhilePopupOpen = false;
                    ClearFrontendPopupState();
                    if (s_OffscreenTarget != null)
                        s_OffscreenTarget.Repaint();
                    return;
                }

                // Handle AnnotationWindow (Gizmos) popup selections.
                // Path is encoded as "annotation|classID|scriptClass".
                // Toggle the gizmo enabled state and re-send the updated popup.
                if (s_PopupSourceType == "annotation_full")
                {
                    try
                    {
                        EnsureAnnotationReflection();
                        if (s_SetGizmoEnabledMethod != null && path != null && path.StartsWith("annotation|"))
                        {
                            string[] parts = path.Split('|');
                            if (parts.Length >= 3)
                            {
                                int classID = int.Parse(parts[1]);
                                string scriptClass = parts[2] == "-" ? "" : parts[2];

                                // Read current gizmo state to toggle it
                                int curEnabled = 0;
                                if (s_GetAnnotationMethod != null)
                                {
                                    object ann = s_GetAnnotationMethod.Invoke(null, new object[] { classID, scriptClass });
                                    if (ann != null)
                                    {
                                        FieldInfo geFld = ann.GetType().GetField("gizmoEnabled",
                                            BindingFlags.Instance | BindingFlags.Public);
                                        if (geFld != null)
                                            curEnabled = (int)geFld.GetValue(ann);
                                    }
                                }
                                int newEnabled = curEnabled > 0 ? 0 : 1;
                                s_SetGizmoEnabledMethod.Invoke(null,
                                    new object[] { classID, scriptClass, newEnabled, true });
                                LogVerbose($"[NWB-Annotation] Toggled gizmo: classID={classID} scriptClass={scriptClass} {curEnabled}->{newEnabled}");

                                if (s_OffscreenTarget != null) s_OffscreenTarget.Repaint();
                            }
                        }
                        else if (s_SetIconEnabledMethod != null && path != null && path.StartsWith("annotation_icon|"))
                        {
                            string[] parts = path.Split('|');
                            if (parts.Length >= 3)
                            {
                                int classID = int.Parse(parts[1]);
                                string scriptClass = parts[2] == "-" ? "" : parts[2];
                                int curEnabled = 0;
                                if (s_GetAnnotationMethod != null)
                                {
                                    object ann = s_GetAnnotationMethod.Invoke(null, new object[] { classID, scriptClass });
                                    if (ann != null)
                                    {
                                        FieldInfo ieFld = ann.GetType().GetField("iconEnabled",
                                            BindingFlags.Instance | BindingFlags.Public);
                                        if (ieFld != null)
                                            curEnabled = (int)ieFld.GetValue(ann);
                                    }
                                }
                                int newEnabled = curEnabled > 0 ? 0 : 1;
                                s_SetIconEnabledMethod.Invoke(null, new object[] { classID, scriptClass, newEnabled });
                                LogVerbose($"[NWB-Annotation] Toggled icon: classID={classID} scriptClass={scriptClass} {curEnabled}->{newEnabled}");
                                SceneView.RepaintAll();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogWarning($"[NWB-Annotation] Error toggling gizmo: {ex.Message}");
                    }
                    // Keep current popup order stable while open. Unity updates
                    // "Recently Changed" ordering after close/reopen; frontend
                    // applies icon/gizmo visual state locally.
                    return;
                }

                // Handle draw mode popup selections.
                // Path is encoded as "drawmode|drawModeInt|name".
                if (s_PopupSourceType == "draw_mode")
                {
                    try
                    {
                        if (path == "drawmode_lightmap_resolution")
                        {
                            // Toggle Show Lightmap Resolution.
                            bool curRes = GetLightmapShowResolution();
                            SetLightmapShowResolution(!curRes);
                            LogVerbose($"[NWB-DrawMode] Toggled showResolution to {!curRes}");
                            SceneView.RepaintAll();
                        }
                        else if (path != null && path.StartsWith("drawmode|"))
                        {
                            string[] parts = path.Split('|');
                            if (parts.Length >= 2 && int.TryParse(parts[1], out int drawModeInt))
                            {
                                SceneView sv = s_OffscreenTarget as SceneView;
                                if (sv == null) sv = SceneView.lastActiveSceneView;
                                if (sv != null)
                                {
                                    // Find the matching CameraMode from builtins.
                                    EnsureDrawModeReflection();
                                    if (s_BuiltinCameraModes != null && s_CameraModeDrawMode != null)
                                    {
                                        foreach (var cm in s_BuiltinCameraModes)
                                        {
                                            if ((int)s_CameraModeDrawMode.GetValue(cm) == drawModeInt)
                                            {
                                                // SceneView.cameraMode is a public struct.
                                                string modeName = s_CameraModeName?.GetValue(cm) as string ?? "";
                                                string modeSection = s_CameraModeSection?.GetValue(cm) as string ?? "";
                                                sv.cameraMode = new SceneView.CameraMode
                                                {
                                                    drawMode = (DrawCameraMode)drawModeInt,
                                                    name = modeName,
                                                    section = modeSection
                                                };
                                                LogVerbose($"[NWB-DrawMode] Set cameraMode to {modeName} ({drawModeInt})");
                                                sv.Repaint();
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogWarning($"[NWB-DrawMode] Error setting draw mode: {ex.Message}");
                    }
                    ClearFrontendPopupState();
                    return;
                }

                // Handle "+" add button in GameViewSizeMenu — show the Add panel.
                if (path == "__add__" && s_PopupSourceType == "flexible")
                {
                    LogVerbose("[NWB-FrontendPopup] Add button clicked in FlexibleMenu, showing add panel");
                    TryShowGameViewSizeAddPanel();
                    return;
                }

                // Handle SceneFXWindow toggle rows.
                if (s_PopupSourceType == "scene_fx")
                {
                    if (HandleSceneFXToggle(path))
                    {
                        s_FrontendPopupSent = false;
                        TryExtractSceneFXPopup(s_LastFrontendPopupX, s_LastFrontendPopupY);
                    }
                    else
                    {
                        ClearFrontendPopupState();
                    }
                    return;
                }

                // Handle ConnectionTreeViewWindow selection (Console/Profiler target).
                if (s_PopupSourceType == "connection_tree")
                {
                    if (s_CachedConnectionCallbacks != null &&
                        index >= 0 && index < s_CachedConnectionCallbacks.Length &&
                        s_CachedConnectionCallbacks[index] != null)
                    {
                        try
                        {
                            s_CachedConnectionCallbacks[index].Invoke();
                            LogVerbose($"[NWB-ConnectionPopup] Invoked callback for index={index}");
                        }
                        catch (Exception cbEx)
                        {
                            CodelyLogger.LogWarning($"[NWB-ConnectionPopup] Callback error: {cbEx.Message}");
                        }
                    }
                    else
                    {
                        CodelyLogger.LogWarning($"[NWB-ConnectionPopup] No callback for index={index}");
                    }
                    ClearFrontendPopupState();
                    return;
                }

                // Determine the full menu path to execute.
                // Prefer the path sent back by the frontend (Cloud Editor pattern: path is the
                // canonical callback identifier, not index). Fall back to the server-side
                // cached array when path is absent or empty.
                string menuPath = null;
                if (!string.IsNullOrEmpty(path) && path != "__add__")
                {
                    menuPath = path;
                    LogVerbose($"[NWB-FrontendPopup] Using path from frontend: {menuPath}");
                }
                else if (s_CachedPopupItemPaths != null && index >= 0 && index < s_CachedPopupItemPaths.Length)
                {
                    menuPath = s_CachedPopupItemPaths[index];
                    LogVerbose($"[NWB-FrontendPopup] Using cached path for index={index}: {menuPath}");
                }

                if (string.IsNullOrEmpty(menuPath))
                {
                    CodelyLogger.LogWarning($"[NWB-FrontendPopup] Empty menu path for index={index}");
                    ClearFrontendPopupState();
                    return;
                }

                if (s_PopupSourceType == "flexible")
                {
                    // FlexibleMenu: invoke the cached callback directly.
                    LogVerbose($"[NWB-FrontendPopup] Executing FlexibleMenu item index={index}");
                    if (s_CachedFlexibleMenuCallback != null)
                    {
                        // FlexibleMenu callback signature: Action<int, object>(index, item)
                        // Get item from provider.
                        object item = null;
                        if (s_CachedFlexibleMenuItemProvider != null)
                        {
                            MethodInfo getItemMethod = s_CachedFlexibleMenuItemProvider.GetType().GetMethod("GetItem",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null, new System.Type[] { typeof(int) }, null);
                            if (getItemMethod != null)
                                item = getItemMethod.Invoke(s_CachedFlexibleMenuItemProvider, new object[] { index });
                        }
                        try
                        {
                            s_CachedFlexibleMenuCallback.DynamicInvoke(index, item);
                            LogVerbose($"[NWB-FrontendPopup] FlexibleMenu callback invoked for index={index}");
                        }
                        catch (Exception cbEx)
                        {
                            CodelyLogger.LogWarning($"[NWB-FrontendPopup] FlexibleMenu callback error: {cbEx.Message}");
                        }
                    }
                    else
                    {
                        CodelyLogger.LogWarning("[NWB-FrontendPopup] No FlexibleMenu callback cached");
                    }
                }
                else
                {
                    // Detect "Close Tab" from DockArea context menu. Before
                    // execution, snapshot composite window types so we can
                    // identify which was closed and prevent re-creation.
                    bool isCloseTab = menuPath != null && menuPath.Contains("Close Tab");
                    HashSet<string> preCloseTypes = null;
                    if (isCloseTab && s_CompositeActive)
                    {
                        preCloseTypes = new HashSet<string>();
                        foreach (var kv in s_CompositeSlots)
                        {
                            if (kv.Value?.Window != null)
                                preCloseTypes.Add(kv.Value.WindowTypeName);
                        }
                    }

                    LogVerbose($"[NWB-FrontendPopup] Executing menu item: {menuPath}");
                    TryExecuteForwardedMenuItem(menuPath);

                    // After execution, detect which window type was closed and
                    // notify the frontend via DataChannel so it can remove the
                    // tab from its state before sending the next layout.
                    if (isCloseTab && s_CompositeActive && preCloseTypes != null)
                    {
                        int remainingWindows = 0;
                        foreach (var kv in s_CompositeSlots)
                        {
                            if (kv.Value?.Window == null && preCloseTypes.Contains(kv.Value?.WindowTypeName ?? ""))
                            {
                                string closedType = kv.Value.WindowTypeName;
                                CodelyLogger.Log($"[NWB-Composite] User closed tab: {closedType}");
                                SendDataChannelMessage("{\"type\":\"composite_slot_removed\",\"windowType\":\"" + closedType + "\"}");
                            }
                            if (kv.Value?.Window != null)
                                remainingWindows++;
                        }

                        // All composite windows closed — stop streaming entirely.
                        if (remainingWindows == 0)
                        {
                            CodelyLogger.Log("[NWB-Composite] All composite tabs closed by user, stopping offscreen capture");
                            StopOffscreenCapture();
                            return;
                        }
                    }

                    // Trigger deferred scan for natively-added tabs after "Add Tab"
                    if (s_CompositeActive && menuPath != null && menuPath.Contains("Add Tab"))
                    {
                        s_CompositeTrackPanesCountdown = 3;
                        LogVerbose("[NWB-Composite] Add Tab detected, scheduling pane tracking scan");
                    }
                }

                s_SuppressTargetRepaintWhilePopupOpen = false;
                ClearFrontendPopupState();
                if (s_OffscreenTarget != null)
                    s_OffscreenTarget.Repaint();
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-FrontendPopup] Error handling popup_select: {ex.Message}\n{ex.StackTrace}");
                ClearFrontendPopupState();
            }
        }

        /// <summary>
        /// Handle a popup_toggle message from the browser (user toggled a header
        /// toggle like "Low Resolution" or "VSync" in GameViewSizeMenu).
        /// </summary>
        private static void HandleFrontendPopupToggle(string json)
        {
            try
            {
                string key = ExtractJsonString(json, "key");
                bool value = ExtractJsonBool(json, "value");
                LogVerbose($"[NWB-FrontendPopup] popup_toggle: key={key} value={value}");

                if (s_PopupSourceType == "annotation_full")
                {
                    EnsureAnnotationReflection();
                    if (key == "use3dGizmos") SetStaticBool(s_AnnoUse3dGizmosProp, value);
                    else if (key == "fadeGizmos") SetStaticBool(s_AnnoFadeGizmosProp, value);
                    else if (key == "showSelectionOutline") SetStaticBool(s_AnnoShowSelectionOutlineProp, value);
                    else if (key == "showSelectionWire") SetStaticBool(s_AnnoShowSelectionWireProp, value);
                    else if (key == "showTerrainDebugWarnings") SetStaticBool(s_AnnoWindowTerrainWarnProp, value);
                    SceneView.RepaintAll();
                    s_FrontendPopupSent = false;
                    TryExtractAnnotationWindowPopupFull(s_LastFrontendPopupX, s_LastFrontendPopupY);
                    return;
                }

                if (s_CachedGameViewSizeMenuUser == null)
                {
                    CodelyLogger.LogWarning("[NWB-FrontendPopup] No cached GameViewSizeMenuUser for toggle");
                    return;
                }

                System.Type gvType = s_CachedGameViewSizeMenuUser.GetType();
                if (key == "lowRes")
                {
                    PropertyInfo prop = gvType.GetProperty("lowResolutionForAspectRatios",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop != null && prop.CanWrite)
                    {
                        prop.SetValue(s_CachedGameViewSizeMenuUser, value);
                        LogVerbose($"[NWB-FrontendPopup] Set lowResolutionForAspectRatios = {value}");
                    }
                }
                else if (key == "vSync")
                {
                    PropertyInfo prop = gvType.GetProperty("vSyncEnabled",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop != null && prop.CanWrite)
                    {
                        prop.SetValue(s_CachedGameViewSizeMenuUser, value);
                        LogVerbose($"[NWB-FrontendPopup] Set vSyncEnabled = {value}");
                    }
                }

                if (s_OffscreenTarget != null)
                    s_OffscreenTarget.Repaint();
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-FrontendPopup] Error handling popup_toggle: {ex.Message}");
            }
        }

        private static void HandleFrontendPopupSlider(string json)
        {
            try
            {
                if (s_PopupSourceType != "annotation_full") return;
                string key = ExtractJsonString(json, "key");
                float value = ExtractJsonFloat(json, "value");
                EnsureAnnotationReflection();
                if (key == "iconSize")
                    SetStaticFloat(s_AnnoIconSizeProp, Convert01ToTexelWorldSize(Mathf.Clamp01(value)));
                else if (key == "fadeGizmoSize")
                    SetStaticFloat(s_AnnoFadeGizmoSizeProp, Mathf.Clamp(value, 2f, 10f));
                SceneView.RepaintAll();
                s_FrontendPopupSent = false;
                TryExtractAnnotationWindowPopupFull(s_LastFrontendPopupX, s_LastFrontendPopupY);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-FrontendPopup] Error handling popup_slider: {ex.Message}");
            }
        }

        private static void HandleFrontendPopupSearch(string json)
        {
            try
            {
                if (s_PopupSourceType == "add_component")
                {
                    s_AddComponentSearchFilter = ExtractJsonString(json, "value") ?? "";
                    s_FrontendPopupSent = false;
                    TrySendAddComponentPopup(
                        s_LastFrontendPopupX,
                        s_LastFrontendPopupY,
                        s_AddComponentPopupWidth,
                        s_AddComponentPopupHeight);
                    return;
                }

                if (s_PopupSourceType == "object_selector")
                {
                    TryHandleObjectSelectorPopupSearch(ExtractJsonString(json, "value") ?? "");
                    return;
                }

                if (s_PopupSourceType == "advanced_dropdown")
                {
                    s_AdvancedDropdownSearchFilter = ExtractJsonString(json, "value") ?? "";
                    s_FrontendPopupSent = false;
                    TrySendAdvancedDropdownPopup(
                        s_LastFrontendPopupX,
                        s_LastFrontendPopupY,
                        s_AdvancedDropdownPopupWidth,
                        s_AdvancedDropdownPopupHeight);
                    return;
                }

                if (s_PopupSourceType != "annotation_full") return;
                s_AnnotationSearchFilter = ExtractJsonString(json, "value") ?? "";
                s_FrontendPopupSent = false;
                TryExtractAnnotationWindowPopupFull(s_LastFrontendPopupX, s_LastFrontendPopupY);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-FrontendPopup] Error handling popup_search: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle a popup_close message from the browser (user clicked outside popup).
        /// </summary>
        private static void HandleFrontendPopupClose(string json)
        {
            LogVerbose("[NWB-FrontendPopup] popup_close received");

            // For delayed popups, clear PopupCallbackInfo.instance so that
            // the pending callback is not accidentally consumed later.
            if (s_PopupSourceType != null && s_PopupSourceType.StartsWith("delayedpopup"))
            {
                try
                {
                    EnsurePopupCallbackInfoReflection();
                    if (s_PopupCallbackInfoInstanceField != null)
                    {
                        s_PopupCallbackInfoInstanceField.SetValue(null, null);
                        LogVerbose("[NWB-DelayedPopup] Cleared PopupCallbackInfo.instance on close");
                    }
                    s_CachedDelayedPopupCallbackInfo = null;
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"[NWB-DelayedPopup] Error clearing instance: {ex.Message}");
                }
            }

            TryDismissWin32TrackPopupMenu();
            if (s_PopupSourceType == "object_selector")
                CancelObjectSelectorSession();
            if (s_PopupSourceType == "icon_selector")
                CancelIconSelectorSession();
            ClearFrontendPopupState();
        }

        private static void HandleFrontendPopupTab(string json)
        {
            try
            {
                if (s_PopupSourceType != "object_selector") return;

                string popupId = ExtractJsonString(json, "id");
                if (!string.IsNullOrEmpty(popupId) &&
                    popupId != s_CurrentFrontendPopupId &&
                    !s_ObjectSelectorSessionActive)
                {
                    CodelyLogger.LogWarning(
                        $"[NWB-FrontendPopup] Mismatched popup_tab ID: got {popupId}, expected {s_CurrentFrontendPopupId}");
                    return;
                }

                string tabId = ExtractJsonString(json, "tab");
                if (string.IsNullOrEmpty(tabId)) return;
                TryHandleObjectSelectorPopupTab(tabId);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-FrontendPopup] Error handling popup_tab: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear all frontend popup tracking state.
        /// </summary>
        private static void ClearFrontendPopupState(bool notifyFrontend = true)
        {
            s_FrontendPopupSent = false;
            s_CurrentFrontendPopupId = null;
            s_LastFrontendTooltipText = "";
            s_LastCursorLockState = -1;
            s_CachedPopupItemPaths = null;
            s_CachedPopupOriginalIndices = null;
            s_CachedFlexibleMenu = null;
            s_CachedFlexibleMenuItemProvider = null;
            s_CachedFlexibleMenuCallback = null;
            s_CachedPopupListSelectDelegate = null;
            s_CachedPopupListItems = null;
            s_CachedConnectionCallbacks = null;
            s_CachedPaneOptionsEntries = null;
            s_PaneOptionsMouseDownPending = false;
            s_CachedGameViewSizeMenuUser = null;
            s_CachedDelayedPopupCallbackInfo = null;
            s_PopupSourceType = null;
            s_SuppressTargetRepaintWhilePopupOpen = false;
            ClearContextMenuForwardState();
            s_HasToolbarAnchorRectPx = false;
            s_LastToolbarAnchorRectPx = Rect.zero;
            s_LastMenuControllerSignature = null;
            ClearAddComponentPopupState();
            ClearAdvancedDropdownPopupState();
            ClearObjectSelectorPopupState();
            ConsumeDelayedContextMenu();
            CancelNativeDelayedPopup(forceRemove: true);
            TryDismissWin32TrackPopupMenu();
            // NOTE: Do NOT clear s_PendingNativePopupCloseFrames here.
            // The native popup ContainerWindow may not have been created yet
            // when the frontend sends popup_close. The forceClose mechanism
            // must continue scanning for the native popup across frames.

            if (!notifyFrontend)
            {
                return;
            }

            // Notify the frontend to close any open popup or panel.
            SendDataChannelMessage("{\"type\":\"popup_hide\"}");
            SendDataChannelMessage("{\"type\":\"panel_hide\"}");
            SendDataChannelMessage("{\"type\":\"tooltip_hide\"}");
        }

        private static void EnsureTooltipReflection()
        {
            if (s_TooltipReflectionDone) return;
            s_TooltipReflectionDone = true;
            try
            {
                s_EditorWindowParentField = typeof(EditorWindow).GetField("m_Parent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                s_GUIViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView");
                s_GUIViewMouseOverViewProp = s_GUIViewType?.GetProperty("mouseOverView",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                s_GUIViewScreenPositionProp = s_GUIViewType?.GetProperty("screenPosition",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                s_GUIViewWindowBackendProp = s_GUIViewType?.GetProperty("windowBackend",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Tooltip] Reflection init error: {ex.Message}");
            }
        }

        // NOTE: TryQueryMouseTooltipFromIMGUI was removed because OnGUIState.EndOnGUI()
        // (Tuanjie native C++ GUIState.cpp:238) always calls m_MouseTooltip.reset()
        // at the end of each OnGUI cycle. This means GUI.tooltip / GUI.mouseTooltip
        // are ALWAYS empty when read from managed code outside OnGUI.  The native
        // TooltipManager captures tooltip via m_ExpectsSetTooltip during ForceRepaint,
        // but that flag is inaccessible from managed code.
        // GameView toolbar tooltips now use TryGetGameViewToolbarTooltip (direct
        // GUIContent reflection + coordinate geometry).

        private static bool TryQueryTooltipAtMouse(Vector2 mousePos, out string tooltipText)
        {
            tooltipText = "";
            try
            {
                EnsureTooltipReflection();
                if (s_OffscreenTarget == null || s_EditorWindowParentField == null || s_GUIViewWindowBackendProp == null)
                    return false;

                object parentView = s_EditorWindowParentField.GetValue(s_OffscreenTarget);
                if (parentView == null) return false;
                object queryView = parentView;
                Vector2 queryMousePos = mousePos;

                // Match Unity native Tooltip.SetTooltip path:
                // use GUIView.mouseOverView when available.
                if (s_GUIViewMouseOverViewProp != null)
                {
                    object mouseOverView = s_GUIViewMouseOverViewProp.GetValue(null, null);
                    if (mouseOverView != null)
                    {
                        queryView = mouseOverView;

                        if (s_GUIViewScreenPositionProp != null)
                        {
                            try
                            {
                                Rect parentScreen = (Rect)s_GUIViewScreenPositionProp.GetValue(parentView, null);
                                Rect queryScreen = (Rect)s_GUIViewScreenPositionProp.GetValue(queryView, null);
                                Vector2 mouseScreen = parentScreen.position + mousePos;
                                queryMousePos = mouseScreen - queryScreen.position;
                            }
                            catch { queryMousePos = mousePos; }
                        }
                    }
                }

                object backend = s_GUIViewWindowBackendProp.GetValue(queryView);
                if (backend == null) return false;

                if (s_WindowBackendGetTooltipMethod == null ||
                    s_WindowBackendGetTooltipMethod.DeclaringType == null ||
                    !s_WindowBackendGetTooltipMethod.DeclaringType.IsAssignableFrom(backend.GetType()))
                {
                    s_WindowBackendGetTooltipMethod = null;
                    MethodInfo[] methods = backend.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int i = 0; i < methods.Length; i++)
                    {
                        MethodInfo m = methods[i];
                        if (!string.Equals(m.Name, "GetTooltip", StringComparison.Ordinal)) continue;
                        var ps = m.GetParameters();
                        if (ps.Length != 3) continue;
                        if (ps[0].ParameterType != typeof(Vector2)) continue;
                        if (ps[1].ParameterType != typeof(string).MakeByRefType()) continue;
                        if (ps[2].ParameterType != typeof(Rect).MakeByRefType()) continue;
                        s_WindowBackendGetTooltipMethod = m;
                        break;
                    }
                }
                if (s_WindowBackendGetTooltipMethod == null)
                {
                    if (!s_HasLoggedMissingBackendTooltip)
                    {
                        s_HasLoggedMissingBackendTooltip = true;
                        CodelyLogger.LogWarning($"[NWB-Tooltip] GetTooltip method not found on backend type {backend.GetType().FullName}");
                    }
                    return TryQueryUITKTooltipAtMouse(mousePos, out tooltipText);
                }

                object[] args = new object[] { queryMousePos, "", Rect.zero };
                bool hasTooltip = (bool)s_WindowBackendGetTooltipMethod.Invoke(backend, args);
                string tip = args[1] as string ?? "";
                if (hasTooltip && !string.IsNullOrEmpty(tip))
                {
                    tooltipText = tip;
                    return true;
                }

                return TryQueryUITKTooltipAtMouse(mousePos, out tooltipText);
            }
            catch
            {
                return TryQueryUITKTooltipAtMouse(mousePos, out tooltipText);
            }
        }

        private static bool TryQueryUITKTooltipAtMouse(Vector2 mousePos, out string tooltipText)
        {
            tooltipText = "";
            try
            {
                if (s_OffscreenTarget == null) return false;
                var root = s_OffscreenTarget.rootVisualElement;
                if (root == null || root.panel == null) return false;

                // rootVisualElement uses EditorWindow-local coords which start
                // below the DockArea tab bar. Convert from GUIView coords.
                Vector2 localPos = new Vector2(mousePos.x,
                    mousePos.y - s_TabBarOffsetY);

                var picked = root.panel.Pick(localPos);
                while (picked != null)
                {
                    if (!string.IsNullOrEmpty(picked.tooltip))
                    {
                        tooltipText = picked.tooltip;
                        return true;
                    }
                    picked = picked.parent;
                }
            }
            catch
            {
                // Ignore UITK tooltip query failures and fallback to no-tooltip state.
            }
            return false;
        }

        /// <summary>
        /// For GameView IMGUI toolbar, read GUIContent.tooltip strings directly via
        /// reflection and match by mouse position geometry.
        /// Background: OnGUIState.EndOnGUI() (native C++) calls m_MouseTooltip.reset()
        /// at the end of every OnGUI cycle, so GUI.tooltip / GUI.mouseTooltip are
        /// guaranteed to be empty when read from managed code outside OnGUI. The native
        /// TooltipManager works around this by capturing the tooltip text during
        /// ForceRepaint via m_ExpectsSetTooltip, but we cannot access that flag from
        /// managed code. Therefore, we read the GUIContent tooltip strings directly.
        ///
        /// Toolbar layout (GameView.DoToolbarGUI, right-to-left):
        ///   [Gizmos DropDownToggle] [Stats] [Shortcuts] [Mute]
        ///   FlexibleSpace
        ///   ... EnterPlayModeBehavior / FrameDebugger / RenderDoc ...
        ///   FlexibleSpace
        ///   Scale label + slider + value
        ///   Size popup (160px)
        ///   (optional) Display popup / Type popup
        /// </summary>
        private static int s_GameViewToolbarDiagCount;
        private static bool TryGetGameViewToolbarTooltip(Vector2 videoPosInGUIView, out string tooltipText)
        {
            tooltipText = "";
            if (s_OffscreenTarget == null) return false;
            if (s_OffscreenTargetType == null || s_OffscreenTargetType.Name != "GameView") return false;

            float localY = videoPosInGUIView.y - s_TabBarOffsetY;
            float localX = videoPosInGUIView.x;

            // GameView toolbar occupies the top ~20px of the content area.
            if (localY < -2f || localY > 22f) return false;

            if (!s_GameViewStylesReflectionDone)
            {
                s_GameViewStylesReflectionDone = true;
                try
                {
                    s_GameViewStylesType = s_OffscreenTargetType.GetNestedType("Styles",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (s_GameViewStylesType != null)
                        LogVerbose("[NWB-Tooltip] GameView.Styles reflection OK");
                    else
                        CodelyLogger.LogWarning("[NWB-Tooltip] GameView.Styles type not found");
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"[NWB-Tooltip] GameView.Styles reflection: {ex.Message}");
                }
            }
            if (s_GameViewStylesType == null) return false;

            try
            {
                float winW = s_OffscreenTarget.position.width;

                // ── Right-side button group (right-to-left) ──
                float rightEdge = winW;

                // Gizmos uses DropDownToggle which adds a small arrow (~12px).
                GUIContent gizmosC = ReadStylesGUIContent("gizmosContent");
                float gizmosW = CalcDropDownToggleWidth(gizmosC, 70f);
                float gizmosLeft = rightEdge - gizmosW;

                GUIContent statsC = ReadStylesGUIContent("statsContent");
                float statsW = CalcToolbarButtonWidth(statsC, 40f);
                float statsLeft = gizmosLeft - statsW;

                GUIContent shortcutsC = ReadStylesGUIContent("shortcutsOnContent")
                                     ?? ReadStylesGUIContent("shortcutsOffContent");
                float shortcutsW = CalcToolbarButtonWidth(shortcutsC, 28f);
                float shortcutsLeft = statsLeft - shortcutsW;

                GUIContent muteC = ReadStylesGUIContent("muteOnContent")
                                ?? ReadStylesGUIContent("muteOffContent");
                float muteW = CalcToolbarButtonWidth(muteC, 28f);
                float muteLeft = shortcutsLeft - muteW;

                // ── Scale region (dynamically calculated) ──
                GUIContent scaleC = ReadStylesGUIContent("zoomSliderContent");
                DetectGameViewToolbarLayout(scaleC);
                float scaleRegionLeft = s_GameViewScaleRegionLeft;
                float scaleRegionRight = s_GameViewScaleRegionRight;

                // Diagnostic logging for first few toolbar hover events.
                s_GameViewToolbarDiagCount++;
                if (s_GameViewToolbarDiagCount <= 20 || s_GameViewToolbarDiagCount % 200 == 0)
                {
                    LogVerbose($"[NWB-GV-Toolbar] #{s_GameViewToolbarDiagCount} " +
                        $"localXY=({localX:F0},{localY:F0}) winW={winW:F0} " +
                        $"gizmos=[{gizmosLeft:F0}..{rightEdge:F0}]w={gizmosW:F0} " +
                        $"stats=[{statsLeft:F0}..{gizmosLeft:F0}]w={statsW:F0} " +
                        $"shortcuts=[{shortcutsLeft:F0}..{statsLeft:F0}]w={shortcutsW:F0} " +
                        $"mute=[{muteLeft:F0}..{shortcutsLeft:F0}]w={muteW:F0} " +
                        $"scale=[{scaleRegionLeft:F0}..{scaleRegionRight:F0}]");
                }

                // ── Hit test from right to left ──
                if (localX >= gizmosLeft)
                    return TryReturnContentTooltip(gizmosC, out tooltipText);
                if (localX >= statsLeft)
                    return TryReturnContentTooltip(statsC, out tooltipText);
                if (localX >= shortcutsLeft)
                    return TryReturnContentTooltip(shortcutsC, out tooltipText);
                if (localX >= muteLeft)
                    return TryReturnContentTooltip(muteC, out tooltipText);

                // Scale region (left portion of toolbar).
                if (localX >= scaleRegionLeft && localX <= scaleRegionRight)
                    return TryReturnContentTooltip(scaleC, out tooltipText);

                return false;
            }
            catch (Exception ex)
            {
                if (!s_GameViewToolbarTooltipLogged)
                {
                    s_GameViewToolbarTooltipLogged = true;
                    CodelyLogger.LogWarning($"[NWB-Tooltip] GameView toolbar tooltip error: {ex.Message}");
                }
                return false;
            }
        }

        private static GUIContent ReadStylesGUIContent(string fieldName)
        {
            if (s_GameViewStylesType == null) return null;
            var fi = s_GameViewStylesType.GetField(fieldName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return fi?.GetValue(null) as GUIContent;
        }

        private static float CalcToolbarButtonWidth(GUIContent content, float fallback)
        {
            if (content == null) return fallback;
            try
            {
                var style = EditorStyles.toolbarButton;
                if (style != null)
                    return style.CalcSize(content).x + 4f;
            }
            catch { }
            return fallback;
        }

        /// <summary>
        /// Estimate width of a DropDownToggle button (EditorStyles.toolbarDropDownToggleRight).
        /// Adds extra space for the dropdown arrow icon (~12px).
        /// </summary>
        private static float CalcDropDownToggleWidth(GUIContent content, float fallback)
        {
            if (content == null) return fallback;
            try
            {
                var style = EditorStyles.toolbarButton;
                if (style != null)
                    return style.CalcSize(content).x + 16f; // +16 for dropdown arrow + padding
            }
            catch { }
            return fallback;
        }

        /// <summary>
        /// Detect the GameView toolbar left-side layout to precisely locate the
        /// Scale (zoom slider) region. The toolbar left section can include:
        ///   [Type popup 90px + Space 6px]  (if GetAvailableWindowTypes().Count > 1)
        ///   [Display popup 80px]            (if ModuleManager.ShouldShowMultiDisplayOption())
        ///   [Resolution popup 160px]        (always)
        ///   [Scale label + slider + value]  (always)
        ///
        /// We detect which optional items are present via reflection, then compute
        /// the precise X range for the Scale area.
        /// </summary>
        private static void DetectGameViewToolbarLayout(GUIContent scaleContent)
        {
            if (s_GameViewToolbarLayoutDetected) return;
            s_GameViewToolbarLayoutDetected = true;

            float leftOffset = 4f; // toolbar left padding (EditorStyles.toolbar)
            try
            {
                // Check if type popup is shown: PlayModeView.GetAvailableWindowTypes().Count > 1
                var playModeViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.PlayModeView");
                if (playModeViewType != null)
                {
                    var getTypes = playModeViewType.GetMethod("GetAvailableWindowTypes",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (getTypes != null)
                    {
                        var dict = getTypes.Invoke(null, null) as System.Collections.IDictionary;
                        if (dict != null && dict.Count > 1)
                            leftOffset += 96f; // Width(90) + Space(~6)
                    }
                }

                // Check if multi-display popup is shown
                var moduleManagerType = typeof(EditorWindow).Assembly.GetType("UnityEditor.Modules.ModuleManager");
                if (moduleManagerType != null)
                {
                    var shouldShow = moduleManagerType.GetMethod("ShouldShowMultiDisplayOption",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (shouldShow != null && (bool)shouldShow.Invoke(null, null))
                        leftOffset += 80f; // Width(80)
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-GV-Toolbar] Layout detection error: {ex.Message}");
            }

            // Resolution popup is always present.
            leftOffset += 160f; // Width(160f)

            s_GameViewScaleRegionLeft = leftOffset;

            // Scale area: "Scale" label + slider(max 150px) + value label(40px)
            float scaleLabelW = 36f; // "Scale" text width estimate
            if (scaleContent != null)
            {
                try
                {
                    var labelStyle = EditorStyles.label;
                    if (labelStyle != null)
                        scaleLabelW = labelStyle.CalcSize(scaleContent).x;
                }
                catch { }
            }
            float scaleAreaW = scaleLabelW + 150f + 40f; // label + max slider + value
            s_GameViewScaleRegionRight = s_GameViewScaleRegionLeft + scaleAreaW;

            LogVerbose($"[NWB-GV-Toolbar] Layout detected: scaleRegion=[{s_GameViewScaleRegionLeft:F0}..{s_GameViewScaleRegionRight:F0}] " +
                $"leftOffset={leftOffset:F0} scaleLabelW={scaleLabelW:F0}");
        }

        private static bool TryReturnContentTooltip(GUIContent content, out string tooltipText)
        {
            tooltipText = "";
            if (content == null || string.IsNullOrEmpty(content.tooltip))
                return false;
            tooltipText = content.tooltip;
            return true;
        }

        private static void SendFrontendTooltip(string text)
        {
            string normalized = text ?? "";
            if (normalized == s_LastFrontendTooltipText) return;
            s_LastFrontendTooltipText = normalized;
            if (string.IsNullOrEmpty(normalized))
            {
                SendDataChannelMessage("{\"type\":\"tooltip_hide\"}");
            }
            else
            {
                SendDataChannelMessage("{\"type\":\"tooltip_show\",\"text\":\"" + EscapeJsonString(normalized) + "\"}");
            }
        }



        /// <summary>
        /// Immediately scan all ContainerWindows for a newly-created popup
        /// (showMode==1) right after injecting a mousedown event. If found,
        /// extract menu data, send it to the frontend via DataChannel, and
        /// close the native popup in the same frame — before macOS has a
        /// chance to un-hide the Unity application.
        /// </summary>
        private static void TryImmediatePopupIntercept(float ppp)
        {
            try
            {
                System.Type cwType = typeof(UnityEditor.Editor).Assembly
                    .GetType("UnityEditor.ContainerWindow");
                if (cwType == null) return;

                PropertyInfo showModeProp = cwType.GetProperty("showMode",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                PropertyInfo positionProp = cwType.GetProperty("position",
                    BindingFlags.Instance | BindingFlags.Public);
                if (showModeProp == null || positionProp == null) return;

                // Get the target window's screen-space rect for coordinate
                // mapping in TryExtractAndSendPopupToFrontend.
                // m_Parent is a FIELD (not property) on EditorWindow,
                // and screenPosition lives on the GUIView base class.
                Rect targetScreenPos = Rect.zero;
                if (s_OffscreenTarget != null)
                {
                    FieldInfo parentField = typeof(EditorWindow).GetField(
                        "m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (parentField != null)
                    {
                        object targetView = parentField.GetValue(s_OffscreenTarget);
                        if (targetView != null)
                        {
                            System.Type viewType = targetView.GetType();
                            while (viewType != null && viewType.Name != "GUIView" && viewType.Name != "View")
                                viewType = viewType.BaseType;

                            PropertyInfo screenPosProp = (viewType ?? targetView.GetType()).GetProperty(
                                "screenPosition",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            if (screenPosProp == null)
                                screenPosProp = targetView.GetType().GetProperty(
                                    "screenPosition",
                                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                            if (screenPosProp != null)
                                targetScreenPos = (Rect)screenPosProp.GetValue(targetView);
                        }
                    }
                }
                // targetScreenPos logging removed — called on every mouse event, too noisy.

                var allContainers = Resources.FindObjectsOfTypeAll(cwType);
                foreach (var cw in allContainers)
                {
                    if (cw == null) continue;
                    int showMode = (int)showModeProp.GetValue(cw);
                    if (showMode != 1) continue;

                    Rect cwPos = (Rect)positionProp.GetValue(cw);
                    if (cwPos.width <= 0 || cwPos.height <= 0) continue;

                    LogVerbose($"[NWB-PopupIntercept] Immediate popup detected: pos=({cwPos.x:F0},{cwPos.y:F0},{cwPos.width:F0},{cwPos.height:F0})");

                    bool sent = TryExtractAndSendPopupToFrontend(
                        cw, showMode, cwPos, targetScreenPos, ppp);

                    // Always close native showMode=1 popup immediately to
                    // prevent macOS from bringing the parent window to the
                    // foreground (NSWindow activation on child window creation).
                    TryClosePopupWindow(cw);

                    if (sent)
                        LogVerbose("[NWB-PopupIntercept] Popup sent to frontend and native window closed in same frame");
                    else
                        LogVerbose("[NWB-PopupIntercept] Extraction failed but native popup closed to prevent foreground activation");
                    break;
                }

                if (!s_FrontendPopupSent)
                    TryImmediateObjectSelectorIntercept(ppp);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-PopupIntercept] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize reflection cache for EditorGUI.PopupCallbackInfo.
        /// </summary>
        private static void EnsurePopupCallbackInfoReflection()
        {
            if (s_PopupCallbackInfoReflectionDone) return;
            s_PopupCallbackInfoReflectionDone = true;

            try
            {
                s_PopupCallbackInfoType = typeof(EditorGUI).GetNestedType(
                    "PopupCallbackInfo",
                    BindingFlags.NonPublic | BindingFlags.Public);
                if (s_PopupCallbackInfoType == null)
                {
                    CodelyLogger.LogWarning("[NWB-DelayedPopup] PopupCallbackInfo type not found");
                    return;
                }

                s_PopupCallbackInfoInstanceField = s_PopupCallbackInfoType.GetField(
                    "instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                s_PopupCallbackInfoControlIDField = s_PopupCallbackInfoType.GetField(
                    "m_ControlID", BindingFlags.Instance | BindingFlags.NonPublic);
                s_PopupCallbackInfoSelectedIndexField = s_PopupCallbackInfoType.GetField(
                    "m_SelectedIndex", BindingFlags.Instance | BindingFlags.NonPublic);
                s_PopupCallbackInfoSourceViewField = s_PopupCallbackInfoType.GetField(
                    "m_SourceView", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-DelayedPopup] Reflection init error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the current PopupCallbackInfo.instance (may be null).
        /// </summary>
        private static object GetPopupCallbackInfoInstance()
        {
            EnsurePopupCallbackInfoReflection();
            if (s_PopupCallbackInfoInstanceField == null) return null;
            return s_PopupCallbackInfoInstanceField.GetValue(null);
        }

        /// <summary>
        /// <summary>
        /// After SendEvent(MouseDown), detect whether EditorGUI.DoPopup created
        /// a new PopupCallbackInfo. If so, read the actual popup items that
        /// DisplayCustomMenu queued in C++ gDelayedContextMenu via
        /// Menu.GetMenuItems and send them to the browser frontend.
        /// This is completely generic — works for ANY DoPopup-based popup
        /// regardless of window type or toolbar layout.
        /// </summary>
        private static bool TryInterceptDelayedPopup(
            object prevCallbackInstance, float clickX, float clickY, float ppp)
        {
            if (s_FrontendPopupSent) return false;
            if (s_OffscreenTarget == null) return false;

            EnsurePopupCallbackInfoReflection();
            if (s_PopupCallbackInfoInstanceField == null) return false;

            object curInstance = s_PopupCallbackInfoInstanceField.GetValue(null);

            if (curInstance == null) return false;
            if (curInstance == prevCallbackInstance) return false;

            LogVerbose("[NWB-DelayedPopup] Detected new PopupCallbackInfo.instance after mousedown");

            try
            {
                object popupSourceView = null;
                try
                {
                    popupSourceView = s_PopupCallbackInfoSourceViewField?.GetValue(curInstance);
                }
                catch (Exception) { }
                popupSourceView = ResolvePopupSourceView(popupSourceView);

                ComputePopupPositionFromLocalClick(
                    clickX, clickY, s_TabBarOffsetY + 20f, 4f,
                    out float clickPopX, out float clickPopY,
                    explicitSourceView: popupSourceView,
                    explicitPpp: ppp);
                // Delayed IMGUI popups (EditorGUI.DoPopup) do not consistently
                // expose a stable trigger rect in PopupCallbackInfo across
                // Tuanjie versions. Use click-based frame coordinates only.
                s_HasToolbarAnchorRectPx = false;
                s_LastToolbarAnchorRectPx = Rect.zero;

                // Read the exact items that DoPopup → DisplayCustomMenu queued
                // in MenuController under kPopupMenuPath.
                System.Type menuType = typeof(EditorWindow).Assembly.GetType("UnityEditor.Menu");
                if (menuType == null)
                {
                    CodelyLogger.LogWarning("[NWB-DelayedPopup] UnityEditor.Menu type not found");
                    return false;
                }

                MethodInfo getMenuItemsMethod = menuType.GetMethod("GetMenuItems",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(string), typeof(bool), typeof(bool) },
                    null);
                if (getMenuItemsMethod == null)
                {
                    // Unity 2019 lacks Menu.GetMenuItems. Use the public API
                    // Unsupported.GetSubmenus to read items that DoPopup →
                    // DisplayCustomContextPopupMenu registered in MenuController.
                    // This is generic and works for ALL DoPopup-based menus.
                    LogVerbose("[NWB-DelayedPopup] Menu.GetMenuItems not found, using Unsupported.GetSubmenus fallback");

                    float fallbackPopX = clickPopX;
                    float fallbackPopY = clickPopY;
                    bool fallbackSent = SendDelayedPopupViaSubmenus(
                        fallbackPopX, fallbackPopY, ppp);
                    if (fallbackSent)
                    {
                        s_PopupSourceType = "delayedpopup";
                        s_CachedDelayedPopupCallbackInfo = curInstance;
                        try
                        {
                            s_PopupCallbackInfoInstanceField.SetValue(null, null);
                            LogVerbose("[NWB-DelayedPopup] Cleared PopupCallbackInfo.instance");
                        }
                        catch (Exception clearEx)
                        {
                            CodelyLogger.LogWarning($"[NWB-DelayedPopup] Failed to clear instance: {clearEx.Message}");
                        }
                        CancelNativeDelayedPopup();
                        s_SuppressTargetRepaintWhilePopupOpen = true;
                        TryDismissWin32TrackPopupMenu();
                        s_PendingNativePopupCloseFrames = 60;
                    }
                    return fallbackSent;
                }

                System.Array menuItems = getMenuItemsMethod.Invoke(null,
                    new object[] { kPopupMenuPath, true, false }) as System.Array;

                if (menuItems == null || menuItems.Length == 0)
                {
                    LogVerbose("[NWB-DelayedPopup] No MenuController items found for delayed popup");
                    return false;
                }

                LogVerbose($"[NWB-DelayedPopup] Read {menuItems.Length} items from MenuController");

                // Use frame pixel coordinates so frontend unityPixelToDOM aligns
                // correctly in both single-view and composite layouts.
                float popX = clickPopX;
                float popY = clickPopY;

                bool sent = SendGenericMenuPopup(
                    menuItems, menuType, popX, popY, 0, 0,
                    hierarchicalContextMenu: true);
                if (sent)
                {
                    // Override source type: delayed popups use index-based callback
                    // via PopupCallbackInfo.SetEnumValueDelegate, not ExecuteMenuItem.
                    s_PopupSourceType = "delayedpopup";
                    // Keep a strong reference before clearing static instance.
                    s_CachedDelayedPopupCallbackInfo = curInstance;

                    // Clear PopupCallbackInfo.instance to prevent the native popup
                    // from being created during the next IMGUI repaint cycle.
                    // We have already captured the menu items and sent them to the
                    // browser as an HTML overlay — the native popup is not needed.
                    try
                    {
                        s_PopupCallbackInfoInstanceField.SetValue(null, null);
                        LogVerbose("[NWB-DelayedPopup] Cleared PopupCallbackInfo.instance to suppress native popup");
                    }
                    catch (Exception clearEx)
                    {
                        CodelyLogger.LogWarning($"[NWB-DelayedPopup] Failed to clear instance: {clearEx.Message}");
                    }

                    // DisplayPopupMenu (Project Assets/) queues gDelayedContextMenu.
                    // Selection uses cached PopupCallbackInfo, so we can remove the
                    // temporary MenuController items to block TrackPopupMenuEx.
                    CancelNativeDelayedPopup();
                    s_SuppressTargetRepaintWhilePopupOpen = true;
                    TryDismissWin32TrackPopupMenu();

                    // The native popup ContainerWindow (showMode==1) may still
                    // appear if C++ already queued it. Tell ScanAndCloseNativePopups
                    // to force-close the first popup it finds within the next frames.
                    s_PendingNativePopupCloseFrames = 60;
                    LogVerbose($"[NWB-DelayedPopup] Sent delayed popup from MenuController: id={s_CurrentFrontendPopupId} items={menuItems.Length} pendingCloseFrames=60");
                }
                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-DelayedPopup] Error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Compute a lightweight signature of MenuController's current items
        /// under kPopupMenuPath. Format: "count|firstItemPath".
        /// Used to detect changes caused by GenericMenu.DropDown.
        /// </summary>
        private static string GetMenuControllerSignature()
        {
            try
            {
                System.Type menuType = typeof(EditorWindow).Assembly.GetType("UnityEditor.Menu");
                if (menuType == null) return "0|";

                MethodInfo gmi = menuType.GetMethod("GetMenuItems",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(string), typeof(bool), typeof(bool) },
                    null);
                if (gmi != null)
                {
                    System.Array items = gmi.Invoke(null,
                        new object[] { kPopupMenuPath, true, false }) as System.Array;
                    if (items == null || items.Length == 0) return "0|";

                    string fp = "";
                    var pp = items.GetValue(0).GetType().GetProperty("path",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pp != null) fp = pp.GetValue(items.GetValue(0)) as string ?? "";
                    return $"{items.Length}|{fp}";
                }

                // Fallback: Unsupported.GetSubmenus (Unity 2019)
                EnsureUnsupportedMenuReflection();
                if (s_UnsupportedGetSubmenusMethod != null)
                {
                    var subs = s_UnsupportedGetSubmenusMethod.Invoke(null,
                        new object[] { kPopupMenuPath }) as string[];
                    if (subs == null || subs.Length == 0) return "0|";
                    return $"{subs.Length}|{subs[0]}";
                }
                return "0|";
            }
            catch { return "0|"; }
        }

        /// <summary>
        /// Detect GenericMenu.DropDown popups that wrote to MenuController but
        /// did not create a PopupCallbackInfo or a native popup ContainerWindow
        /// (both suppressed in offscreen mode). Compare the current
        /// MenuController signature against the pre-mousedown snapshot; if
        /// different, the mousedown triggered a GenericMenu and we send
        /// the items to the browser frontend.
        /// </summary>
        private static bool TryInterceptGenericMenuPopup(
            string prevSignature, float clickX, float clickY, float ppp)
        {
            if (s_FrontendPopupSent) return false;

            try
            {
                string curSig = GetMenuControllerSignature();
                if (curSig == prevSignature || curSig == "0|") return false;

                LogVerbose($"[NWB-GenericMenu] MenuController changed: {prevSignature} → {curSig}");
                return TryForwardMenuControllerPopup(clickX, clickY, forceResend: false);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-GenericMenu] Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Read MenuController items under kPopupMenuPath and forward them to
        /// the browser overlay. Also cancels the native delayed context menu
        /// (gDelayedContextMenu) so TrackPopupMenuEx does not appear on the
        /// local desktop during streaming.
        /// </summary>
        private static bool TryForwardMenuControllerPopup(float clickX, float clickY, bool forceResend)
        {
            if (s_FrontendPopupSent) return false;

            try
            {
                System.Type menuType = typeof(EditorWindow).Assembly.GetType("UnityEditor.Menu");
                if (menuType == null) return false;

                MethodInfo getMenuItemsMethod = menuType.GetMethod("GetMenuItems",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(string), typeof(bool), typeof(bool) },
                    null);

                string curSig = GetMenuControllerSignature();

                if (getMenuItemsMethod != null)
                {
                    System.Array menuItems = getMenuItemsMethod.Invoke(null,
                        new object[] { kPopupMenuPath, true, false }) as System.Array;
                    if (menuItems == null || menuItems.Length == 0)
                    {
                        LogVerbose("[NWB-ContextMenu] No MenuController items to forward");
                        return false;
                    }

                    if (!forceResend && curSig == s_LastMenuControllerSignature)
                    {
                        LogVerbose($"[NWB-ContextMenu] Menu unchanged (sig={curSig}), skip resend");
                        return false;
                    }

                    float ppp = EditorGUIUtility.pixelsPerPoint;
                    ComputePopupPositionFromLocalClick(
                        clickX, clickY, s_TabBarOffsetY + 4f, 4f,
                        out float popX, out float popY,
                        explicitPpp: ppp);

                    string prevSig = s_LastMenuControllerSignature;
                    if (forceResend)
                        s_LastMenuControllerSignature = null;

                    bool sent = SendGenericMenuPopup(
                        menuItems, menuType, popX, popY, 0, 0,
                        hierarchicalContextMenu: true);
                    if (sent)
                    {
                        s_PendingNativePopupCloseFrames = Math.Max(
                            s_PendingNativePopupCloseFrames, 60);

                        // Immediately close any native ContainerWindow(sm=2)
                        // that GenericMenu.ShowAsContext created, so it does
                        // not appear alongside the frontend popup.
                        DismissAllNativePopups();

                        // Drain gDelayedContextMenu: ShowAsContext sets it,
                        // and any subsequent Layout/Repaint would trigger
                        // TrackPopupMenuEx, blocking the main thread. Send
                        // a few protected Layout events to exhaust it.
                        // Menu items stay in MenuController for
                        // ExecuteMenuItem on popup_select.
                        for (int d = 0; d < 4; d++)
                        {
                            ScheduleNativePopupAutoDismiss();
                            try
                            {
                                s_OffscreenTarget.SendEvent(
                                    new Event { type = EventType.Layout });
                            }
                            catch (Exception) { }
                            finally { CancelNativePopupAutoDismiss(); }
                            TryDismissWin32TrackPopupMenu();
                        }
                        ScanAndCloseNativePopups();

                        LogVerbose($"[NWB-ContextMenu] Forwarded popup: id={s_CurrentFrontendPopupId} items={menuItems.Length} sig={curSig}");
                    }
                    else
                    {
                        // Restore previous signature so the caller can retry
                        // with the same items. Do NOT remove MenuController
                        // entries here — the caller (TrySynthesizeAndForward…)
                        // will clean up after all retries are exhausted.
                        s_LastMenuControllerSignature = prevSig;
                        CodelyLogger.LogWarning("[NWB-ContextMenu] Failed to send popup_show via DataChannel (items retained for retry)");
                    }
                    return sent;
                }

                // Fallback: Unsupported.GetSubmenus (Unity 2019)
                if (!forceResend && curSig == s_LastMenuControllerSignature)
                {
                    LogVerbose($"[NWB-ContextMenu] Menu unchanged (sig={curSig}), skip resend");
                    return false;
                }

                float fbPpp = EditorGUIUtility.pixelsPerPoint;
                ComputePopupPositionFromLocalClick(
                    clickX, clickY, s_TabBarOffsetY + 4f, 4f,
                    out float fbPopX, out float fbPopY,
                    explicitPpp: fbPpp);
                bool fbSent = SendDelayedPopupViaSubmenus(fbPopX, fbPopY, fbPpp);
                if (fbSent)
                {
                    s_PopupSourceType = "generic_context";
                    s_PendingNativePopupCloseFrames = 60;
                    LogVerbose($"[NWB-ContextMenu] Forwarded popup via Unsupported.GetSubmenus fallback: id={s_CurrentFrontendPopupId}");
                }
                else
                {
                    CancelNativeDelayedPopup();
                    CodelyLogger.LogWarning("[NWB-ContextMenu] Fallback failed; cancelled native delayed menu");
                }
                return fbSent;
            }
            catch (Exception ex)
            {
                CancelNativeDelayedPopup();
                CodelyLogger.LogWarning($"[NWB-ContextMenu] Error forwarding menu: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Path 4: Detect clicks on UITK EditorToolbarDropdown buttons.
        /// These buttons (e.g. PivotModeDropdown, PivotRotationDropdown in
        /// SceneView) use GenericMenu.DropDown which calls native
        /// DisplayCustomContextPopupMenu — this fails silently in offscreen
        /// mode and does NOT write to MenuController, so Paths 1-3 miss them.
        /// We use panel.Pick() to hit-test the UITK tree and, for known
        /// dropdowns, build the popup data manually from C# property state.
        /// </summary>
        private static bool TryInterceptUITKToolbarClick(float x, float y, float ppp)
        {
            if (s_FrontendPopupSent || s_OffscreenTarget == null) return false;

            try
            {
                var root = s_OffscreenTarget.rootVisualElement;
                if (root == null || root.panel == null) return false;

                // Hit-test the UITK panel at the GUIView coordinate.
                var picked = root.panel.Pick(new Vector2(x, y));
                if (picked == null) return false;

                // Walk up the ancestor chain to find an EditorToolbarDropdown.
                string dropdownTypeName = null;
                var el = picked;
                while (el != null)
                {
                    string typeName = el.GetType().Name;
                    if (typeName == "PivotModeDropdown" ||
                        typeName == "PivotRotationDropdown")
                    {
                        dropdownTypeName = typeName;
                        break;
                    }
                    // Also check for unknown dropdowns that extend EditorToolbarDropdown.
                    var bt = el.GetType().BaseType;
                    while (bt != null)
                    {
                        if (bt.Name == "EditorToolbarDropdown")
                        {
                            dropdownTypeName = el.GetType().Name;
                            break;
                        }
                        bt = bt.BaseType;
                    }
                    if (dropdownTypeName != null) break;
                    el = el.parent;
                }

                if (dropdownTypeName == null) return false;

                LogVerbose($"[NWB-UITK] Detected toolbar dropdown click: {dropdownTypeName} at ({x:F0},{y:F0})");
                return BuildAndSendUITKPopup(dropdownTypeName, x, y);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-UITK] Error in TryInterceptUITKToolbarClick: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Build popup JSON for a known UITK toolbar dropdown and send it to
        /// the browser frontend. Returns true if a popup was sent.
        /// </summary>
        private static bool BuildAndSendUITKPopup(string dropdownTypeName, float x, float y)
        {
            float popX = x;
            float toolbarBottom = s_TabBarOffsetY + 20f;
            float popY = Mathf.Max(toolbarBottom, y + 4f);

            s_FrontendPopupIdCounter++;
            s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;

            var sb = new System.Text.StringBuilder(1024);
            sb.Append("{\"type\":\"popup_show\",\"id\":\"");
            sb.Append(s_CurrentFrontendPopupId);
            sb.Append("\",\"x\":");
            sb.Append(popX.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"y\":");
            sb.Append(popY.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendToolbarAnchorRectJson(sb);
            sb.Append(",\"items\":[");

            if (dropdownTypeName == "PivotModeDropdown")
            {
                bool isCenter = UnityEditor.Tools.pivotMode == UnityEditor.PivotMode.Center;
                sb.Append("{\"index\":0,\"label\":\"Center\",\"path\":\"Center\",\"enabled\":true,\"checked\":");
                sb.Append(isCenter ? "true" : "false");
                sb.Append("},{\"index\":1,\"label\":\"Pivot\",\"path\":\"Pivot\",\"enabled\":true,\"checked\":");
                sb.Append(!isCenter ? "true" : "false");
                sb.Append("}");
                s_PopupSourceType = "uitk_pivotMode";
            }
            else if (dropdownTypeName == "PivotRotationDropdown")
            {
                bool isGlobal = UnityEditor.Tools.pivotRotation == UnityEditor.PivotRotation.Global;
                sb.Append("{\"index\":0,\"label\":\"Global\",\"path\":\"Global\",\"enabled\":true,\"checked\":");
                sb.Append(isGlobal ? "true" : "false");
                sb.Append("},{\"index\":1,\"label\":\"Local\",\"path\":\"Local\",\"enabled\":true,\"checked\":");
                sb.Append(!isGlobal ? "true" : "false");
                sb.Append("}");
                s_PopupSourceType = "uitk_pivotRotation";
            }
            else
            {
                LogVerbose($"[NWB-UITK] Unknown dropdown type: {dropdownTypeName}, skipping");
                return false;
            }

            sb.Append("]}");

            s_CachedPopupItemPaths = null;
            s_FrontendPopupSent = true;
            CancelNativeDelayedPopup();

            SendDataChannelMessage(sb.ToString());
            LogVerbose($"[NWB-UITK] Sent UITK popup: id={s_CurrentFrontendPopupId} type={s_PopupSourceType}");
            return true;
        }

        /// <summary>
        /// Handle frontend selection for a delayed popup (popup that was
        /// intercepted because the native platform could not display it).
        /// Simulates the callback that the native popup menu would have
        /// triggered by setting PopupCallbackInfo.m_SelectedIndex and
        /// sending the PopupMenuChanged command event.
        /// </summary>
        private static void HandleDelayedPopupSelect(int index)
        {
            try
            {
                EnsurePopupCallbackInfoReflection();
                object instance = GetPopupCallbackInfoInstance();
                if (instance == null)
                {
                    // Fallback to cached callback object. This is expected when
                    // TryInterceptDelayedPopup already cleared the static instance.
                    instance = s_CachedDelayedPopupCallbackInfo;
                    if (instance == null)
                    {
                        CodelyLogger.LogWarning("[NWB-DelayedPopup] PopupCallbackInfo.instance and cache are null on select");
                        return;
                    }
                    LogVerbose("[NWB-DelayedPopup] Using cached PopupCallbackInfo for select");
                }

                // Restore static instance so PopupMenuChanged can consume
                // m_SelectedIndex through Unity's EditorGUI.DoPopup internals.
                if (s_PopupCallbackInfoInstanceField != null)
                {
                    s_PopupCallbackInfoInstanceField.SetValue(null, instance);
                }

                // Set m_SelectedIndex
                if (s_PopupCallbackInfoSelectedIndexField != null)
                    s_PopupCallbackInfoSelectedIndexField.SetValue(instance, index);

                // Send PopupMenuChanged command event to m_SourceView
                if (s_PopupCallbackInfoSourceViewField != null)
                {
                    object sourceView = s_PopupCallbackInfoSourceViewField.GetValue(instance);
                    if (sourceView != null)
                    {
                        // sourceView is a GUIView, call SendEvent with a command event
                        MethodInfo sendEventMethod = sourceView.GetType().GetMethod(
                            "SendEvent", BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic, null,
                            new System.Type[] { typeof(Event) }, null);
                        if (sendEventMethod != null)
                        {
                            Event cmdEvt = EditorGUIUtility.CommandEvent("PopupMenuChanged");
                            sendEventMethod.Invoke(sourceView, new object[] { cmdEvt });
                            LogVerbose($"[NWB-DelayedPopup] Sent PopupMenuChanged to sourceView, index={index}");
                        }
                    }
                    else
                    {
                        CodelyLogger.LogWarning("[NWB-DelayedPopup] sourceView is null, trying s_OffscreenTarget");
                        // Fallback: send to offscreen target
                        if (s_OffscreenTarget != null)
                        {
                            Event cmdEvt = EditorGUIUtility.CommandEvent("PopupMenuChanged");
                            s_OffscreenTarget.SendEvent(cmdEvt);
                        }
                    }
                }

                if (s_OffscreenTarget != null)
                    s_OffscreenTarget.Repaint();

                s_CachedDelayedPopupCallbackInfo = null;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-DelayedPopup] HandleSelect error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Try to close a popup ContainerWindow to prevent macOS NSApplication unhide.
        /// Only re-hides Unity if the popup was triggered by a recent remote (browser)
        /// mousedown, preventing local user popups from minimizing Unity.
        /// </summary>
        private static void TryClosePopupWindow(object containerWindow)
        {
            try
            {
                MethodInfo closeMethod = containerWindow.GetType().GetMethod("Close",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, System.Type.EmptyTypes, null);
                if (closeMethod != null)
                {
                    closeMethod.Invoke(containerWindow, null);
                    LogVerbose("[NWB-FrontendPopup] Closed native popup window");

                    // Only hide Unity if the popup was caused by a recent remote
                    // (browser) mousedown. This prevents local SceneView toolbar
                    // clicks from minimizing the Unity application.
                    double elapsed = EditorApplication.timeSinceStartup - s_LastRemoteMousedownTime;
                    bool isRemoteTriggered = elapsed < kRemotePopupGracePeriod;

                    if (isRemoteTriggered)
                    {
                        ReapplyWindowTransparency();
                        // Skip DeactivateUnityKeepWindows for remote connections:
                        // SetWindowPos(HWND_BOTTOM) sends WM_SIZE which corrupts
                        // Screen dimensions → Camera.aspect oscillation → shaking.
                        if (!s_IsRemoteConnection)
                            RequestDelayedDeactivation();
                    }
                    else
                    {
                        LogVerbose($"[NWB-FrontendPopup] Skipping Unity hide — popup appears local (elapsed={elapsed:F2}s)");
                    }
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-FrontendPopup] Failed to close popup window: {ex.Message}");
            }
        }

        private static object FindStreamingMaskContainerWindow(FieldInfo parentField)
        {
            try
            {
                var maskInstances = Resources.FindObjectsOfTypeAll<StreamingMaskWindow>();
                foreach (var mask in maskInstances)
                {
                    if (mask == null) continue;
                    object maskParentView = parentField?.GetValue(mask);
                    if (maskParentView == null) continue;
                    PropertyInfo windowProp = maskParentView.GetType().GetProperty("window",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    object host = windowProp?.GetValue(maskParentView);
                    if (host != null) return host;
                }
            }
            catch (Exception)
            {
                // Fall through to title/size matching.
            }

            return null;
        }

        private static bool IsStreamingMaskContainerWindow(
            object containerWindow, object knownMaskContainer, PropertyInfo positionProp)
        {
            if (containerWindow == null) return false;
            if (knownMaskContainer != null
                && object.ReferenceEquals(containerWindow, knownMaskContainer))
                return true;

            try
            {
                PropertyInfo titleProp = containerWindow.GetType().GetProperty("title",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                string title = titleProp?.GetValue(containerWindow) as string;
                if (!string.IsNullOrEmpty(title)
                    && title.IndexOf("Streaming", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            catch (Exception)
            {
                // Title lookup is best-effort.
            }

            // ShowUtility may not have wired m_Parent yet. Don't close a brand-new
            // 380x170 utility while a mask instance exists.
            if (StreamingMaskWindow.IsActive && positionProp != null)
            {
                try
                {
                    Rect pos = (Rect)positionProp.GetValue(containerWindow);
                    if (pos.width >= 360f && pos.width <= 420f
                        && pos.height >= 150f && pos.height <= 230f)
                        return true;
                }
                catch (Exception)
                {
                    // Size lookup is best-effort.
                }
            }

            return false;
        }

        /// <summary>
        /// Scan for showMode==1 (PopupMenu) ContainerWindows that appeared
        /// asynchronously (not caught by TryImmediatePopupIntercept).
        /// Extract menu data and close native popups immediately.
        /// Pixel compositing has been removed; all popups use frontend HTML.
        /// </summary>
        private static void ScanAndCloseNativePopups()
        {
            if (s_CaptureRT == null || s_OffscreenTarget == null) return;

            try
            {
                FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (parentField == null) return;

                object targetView = parentField.GetValue(s_OffscreenTarget);
                if (targetView == null) return;

                System.Type guiViewBaseType = targetView.GetType();
                while (guiViewBaseType != null && guiViewBaseType.Name != "GUIView" && guiViewBaseType.Name != "View")
                    guiViewBaseType = guiViewBaseType.BaseType;

                PropertyInfo screenPosProp = (guiViewBaseType ?? targetView.GetType()).GetProperty("screenPosition",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (screenPosProp == null)
                    screenPosProp = targetView.GetType().GetProperty("screenPosition",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (screenPosProp == null) return;

                Rect targetScreenPos = (Rect)screenPosProp.GetValue(targetView);
                float ppp = EditorGUIUtility.pixelsPerPoint;

                System.Type containerWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ContainerWindow");
                if (containerWindowType == null) return;

                var allContainers = Resources.FindObjectsOfTypeAll(containerWindowType);
                if (allContainers == null || allContainers.Length == 0) return;

                PropertyInfo showModeProp = containerWindowType.GetProperty("showMode",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                PropertyInfo positionProp = containerWindowType.GetProperty("position",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (showModeProp == null || positionProp == null) return;

                // Identify the StreamingMaskWindow's ContainerWindow so we
                // can skip it when closing sm==2 containers. ShowUtility()
                // creates an sm==2 container for the mask; closing it would
                // destroy the mask and stop the stream.
                object maskContainerWindow = FindStreamingMaskContainerWindow(parentField);

                // Diagnostic log at low frequency.
                bool shouldLogScan = (s_OffscreenFrameCount <= 3)
                                     || (s_OffscreenFrameCount % 500 == 1);
                if (shouldLogScan)
                {
                    int containerCount = 0;
                    foreach (var cwDbg in allContainers)
                    {
                        if (cwDbg == null) continue;
                        int sm = (int)showModeProp.GetValue(cwDbg);
                        if (sm == 0 || sm == 4) continue;
                        containerCount++;
                    }
                    // Only log when popups are actually found (avoid per-frame spam).
                    if (containerCount == 0) { /* no popup — silent */ }
                }

                // Only intercept popups triggered by remote (browser) input.
                // Local popups (opened by the user directly in Unity) should
                // be left alone so the user can interact with them normally.
                double elapsed = EditorApplication.timeSinceStartup - s_LastRemoteMousedownTime;
                bool isRemoteTriggered = elapsed < kRemotePopupGracePeriod;

                // TryInterceptDelayedPopup may have sent a popup to the
                // frontend before the native ContainerWindow was created.
                // In that case, force-close any popup found within the grace
                // window to prevent the native popup from appearing in video.
                bool forceClose = s_PendingNativePopupCloseFrames > 0;
                if (forceClose)
                {
                    s_PendingNativePopupCloseFrames--;
                    // Log ALL containers with their showModes during the
                    // force-close window to diagnose which type the popup is.
                    if (s_PendingNativePopupCloseFrames % 10 == 0)
                    {
                        var sb = new System.Text.StringBuilder("[NWB-Popup-Scan] forceClose containers: ");
                        foreach (var cwDbg in allContainers)
                        {
                            if (cwDbg == null) continue;
                            int sm = (int)showModeProp.GetValue(cwDbg);
                            Rect pos = (Rect)positionProp.GetValue(cwDbg);
                            sb.Append($"sm={sm}({pos.width:F0}x{pos.height:F0}) ");
                        }
                        sb.Append($"remain={s_PendingNativePopupCloseFrames}");
                        CodelyLogger.Log(sb.ToString());
                    }
                }

                foreach (var cw in allContainers)
                {
                    if (cw == null) continue;

                    int showMode = (int)showModeProp.GetValue(cw);
                    // Handle PopupMenu (1) always, and Utility/DropDown (2) during
                    // forceClose or remote-triggered input. GenericMenu.ShowAsContext
                    // (used by ConsoleWindow, etc.) creates showMode==2 containers
                    // that must be closed after forwarding to frontend.
                    // IMPORTANT: skip the StreamingMaskWindow's sm==2 container —
                    // closing it would destroy the mask and stop the stream.
                    if (showMode == 1)
                    { /* always process */ }
                    else if (showMode == 2 && (forceClose || isRemoteTriggered))
                    {
                        if (IsStreamingMaskContainerWindow(cw, maskContainerWindow, positionProp))
                            continue;
                    }
                    else
                        continue;

                    Rect cwPos = (Rect)positionProp.GetValue(cw);
                    if (cwPos.width <= 0 || cwPos.height <= 0) continue;

                    if (!isRemoteTriggered && !forceClose)
                    {
                        // Local popup — do not close, do not extract.
                        continue;
                    }

                    if (forceClose)
                    {
                        LogVerbose($"[NWB-Popup-Scan] Force-closing deferred native popup (frames left={s_PendingNativePopupCloseFrames})");
                    }

                    if (!s_FrontendPopupSent)
                    {
                        TryExtractAndSendPopupToFrontend(
                            cw, showMode, cwPos, targetScreenPos, ppp);
                    }
                    else
                    {
                        LogVerbose($"[NWB-Popup-Scan] Native popup found but popup already sent to frontend — closing without re-sending. pos=({cwPos.x:F0},{cwPos.y:F0},{cwPos.width:F0},{cwPos.height:F0})");
                    }
                    TryClosePopupWindow(cw);
                }

                if (!s_FrontendPopupSent && isRemoteTriggered)
                {
                    TryImmediateObjectSelectorIntercept(ppp);
                }
            }
            catch (Exception ex)
            {
                if (s_OffscreenFrameCount % 300 == 1)
                    CodelyLogger.LogWarning($"[NWB-Offscreen] ScanAndCloseNativePopups error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Poll all pending input events from native and inject via EditorWindow.SendEvent().
        /// </summary>
        private static void PollAndInjectInput()
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            // Process deferred mouseup from a previous batch (if any).
            // This ensures the game's Update() had at least one frame to
            // see IsDown==true between pointerdown and pointerup.
            if (s_DeferredMouseUpJson != null)
            {
                InjectInputEvent(s_DeferredMouseUpJson);
                s_DeferredMouseUpJson = null;
            }

            // Process up to 32 events per frame to avoid stalling.
            // Coalesce high-frequency pointer-move events so we keep input
            // responsiveness under heavy drag while avoiding event-backlog jitter.
            string pendingMoveJson = null;
            for (int i = 0; i < 32; i++)
            {
                int bytes = NativeWindowBridgeAPI.NWB_GetPendingInput(s_InputBuffer, s_InputBuffer.Length);
                if (bytes <= 0) break;

                string json = Encoding.UTF8.GetString(s_InputBuffer, 0, bytes);
                string type = ExtractJsonString(json, "type");
                bool isMove = type == "mousemove" || type == "mousedrag";
                if (isMove)
                {
                    // Keep only the latest move in this batch.
                    pendingMoveJson = json;
                    continue;
                }

                // Preserve ordering: flush latest move before action events.
                if (pendingMoveJson != null)
                {
                    InjectInputEvent(pendingMoveJson);
                    pendingMoveJson = null;
                }

                // Defer mouseup to the next frame so the game's Update()/Tick()
                // runs between the last mousedrag position update and the button
                // release. Without this, InputSystem.OnUpdate may process both the
                // mousedrag (position update) and mouseup (button release) in the
                // same update, causing touchPress.canceled to fire before Tick()
                // sets IsDragging=true, so the drag is never detected.
                if (type == "mouseup")
                {
                    // Flush any pending mousedrag position first so the game sees
                    // the final drag position before the button release next frame.
                    if (pendingMoveJson != null)
                    {
                        InjectInputEvent(pendingMoveJson);
                        pendingMoveJson = null;
                    }
                    s_DeferredMouseUpJson = json;
                    continue;
                }

                InjectInputEvent(json);
            }

            if (pendingMoveJson != null)
            {
                InjectInputEvent(pendingMoveJson);
            }
#endif
        }

        private static int s_InputEventCount;
        // Deferred mouseup: when mousedown and mouseup arrive in the same
        // PollAndInjectInput batch, the mouseup is deferred to the next frame
        // so the game's Update() gets a chance to see IsDown==true.
        private static string s_DeferredMouseUpJson;
        // Separate counter for action events (mousedown/mouseup/keydown/keyup) so
        // diagnostic logs are not drowned out by high-frequency mousemove events.
        private static int s_ActionEventCount;

        // Direct uGUI click/drag injection state.
        // Bypasses StandaloneInputModule which relies on Input.mousePosition
        // (incorrect for off-screen GameView since GetCursorPos returns OS cursor).
        private static GameObject s_StreamingPressedTarget;
        private static PointerEventData s_StreamingPointerData;
        // True when the pressed target is a drag handler (IBeginDragHandler/IDragHandler)
        // rather than a click handler. Determines whether Release sends
        // IEndDragHandler+IPointerUpHandler or IPointerUpHandler+IPointerClickHandler.
        private static bool s_StreamingIsDragTarget;
        // Last screen position used for drag delta calculation.
        private static Vector2 s_StreamingLastDragScreenPos;

        // UI Toolkit (UIDocument) direct injection state.
        // Used when EventSystem.RaycastAll finds no uGUI hits —
        // falls back to VisualElement.Pick for UI Toolkit elements.
        private static VisualElement s_StreamingUITKTarget;
        private static bool s_StreamingIsUITKTarget;

        // Timestamp of last remote (browser) mousedown injection.
        // Used to distinguish browser-triggered popups from local user popups.
        // Only hide Unity when a popup appears within this grace period after
        // a remote mousedown, preventing local popup clicks from minimizing Unity.
        private static double s_LastRemoteMousedownTime;
        private const double kRemotePopupGracePeriod = 0.5;
        // Double-click detection state for injected mouse events.
        // Unity IMGUI uses Event.clickCount to distinguish single/double clicks
        // (e.g. Project window requires clickCount==2 to open folders).
        // Native events get clickCount set by OS, but SendEvent-injected events
        // need it set explicitly. We track timing/position here as a fallback
        // when the frontend does not supply a clickCount value.
        private static double s_LastClickTime;
        private static Vector2 s_LastClickPos;
        private static int s_LastClickButton = -1;
        private static int s_CurrentClickCount;
        private const double kDoubleClickMaxDelay = 0.5; // 500ms, matches typical OS setting
        private const float kDoubleClickMaxDist = 5f;    // max pixel drift between clicks

        // Track latest mouse position so synthetic MouseDrag events can carry
        // a meaningful delta vector (native input pipeline usually computes this).
        private static Vector2 s_LastMousePos;
        private static bool s_HasLastMousePos;
        // SceneView motion-tool lock state for offscreen drag emulation.
        private static bool s_OffscreenScenePanLocked;
        // True when an IMGUI overlay (e.g. OrientationGizmo) consumed
        // the MouseDown event and set hotControl. Prevents mousedrag
        // fallback from stealing the overlay's interaction.
        private static bool s_OverlayConsumedMouseDown;
        // True when the last mousedown landed in the DockArea tab bar
        // region. Subsequent mousedrag events skip SendEvent to prevent
        // DockArea.DragTab → ContainerWindow.GetOrderedWindowList crash
        // caused by dangling PPtr in Unity 2019.
        private static bool s_MouseDownInTabBar;
        // Frontend heartbeat watchdog: helps recover if browser is closed
        // without sending explicit stop.
        private static bool s_HasFrontendHeartbeat;
        private static double s_LastFrontendHeartbeatTime;
        private const double kFrontendHeartbeatTimeoutSec = 8.0;
        // Cross-platform connection state from frontend. The "remote" flag
        // is still useful on macOS/Linux for event-path decisions even though
        // native SendInput fallback only exists on Windows.
        private static bool s_IsRemoteConnection;
        // Keep remote-delta logging bounded on all platforms.
        private static int s_RemoteDeltaLogCount;
        /// <summary>
        /// Parse input JSON from browser and inject as Unity Event into the target window.
        /// JSON format: {"type":"mousemove|mousedown|mouseup|wheel|keydown|keyup","x":...,"y":...,...}
        /// </summary>
        private static void InjectInputEvent(string json)
        {
            if (!s_CompositeActive && s_OffscreenTarget == null) return;

            try
            {
                string type = ExtractJsonString(json, "type");

                if (type == "heartbeat")
                {
                    // C++ already tracks the heartbeat timestamp and sends
                    // heartbeat_ack back to the browser. C# still updates
                    // s_LastFrontendHeartbeatTime as a fallback for older DLLs
                    // without NWB_GetSecondsSinceLastHeartbeat.
                    bool wasFirst = !s_HasFrontendHeartbeat;
                    s_HasFrontendHeartbeat = true;
                    s_LastFrontendHeartbeatTime = EditorApplication.timeSinceStartup;
                    if (wasFirst)
                    {
                        LogVerbose("[NWB-Heartbeat] First heartbeat received (C++ tracks timing)");
                        var lockState = global::UnityEngine.Cursor.lockState;
                        bool locked = lockState == CursorLockMode.Locked;
                        bool vis = global::UnityEngine.Cursor.visible;
                        SendDataChannelMessage("{\"type\":\"cursor_lock\",\"locked\":" +
                            (locked ? "true" : "false") + ",\"visible\":" +
                            (vis ? "true" : "false") + "}");
                        LogVerbose($"[NWB-Heartbeat] Re-sent cursor_lock locked={locked} visible={vis}");
                        s_LastSentEditorCursorStyle = null;
                        TrySendEditorCursorStyle();
                    }
                    return;
                }

                // Frontend reports connection type after DataChannel opens.
                // remote=true when the API URL goes through Tauri proxy tunnel,
                // meaning the physical mouse is on a different machine.
                if (type == "connection_info")
                {
                    bool wasRemote = s_IsRemoteConnection;
                    s_IsRemoteConnection = json.Contains("\"remote\":true")
                                        || json.Contains("\"remote\": true");
                    s_RemoteDeltaLogCount = 0;
                    if (s_IsRemoteConnection != wasRemote)
                        LogVerbose($"[NWB-Connection] remote={s_IsRemoteConnection} (proxy-url detection)");
                    return;
                }

                if (type == "request_composite_layout")
                {
                    SendCompositeLayoutSnapshot();
                    return;
                }

                // Handle frontend popup messages before regular input processing.
                if (type == "popup_select")
                {
                    HandleFrontendPopupSelect(json);
                    return;
                }
                if (type == "popup_close")
                {
                    HandleFrontendPopupClose(json);
                    return;
                }
                if (type == "popup_toggle")
                {
                    HandleFrontendPopupToggle(json);
                    return;
                }
                if (type == "popup_slider")
                {
                    HandleFrontendPopupSlider(json);
                    return;
                }
                if (type == "popup_search")
                {
                    HandleFrontendPopupSearch(json);
                    return;
                }
                if (type == "popup_tab")
                {
                    HandleFrontendPopupTab(json);
                    return;
                }
                if (type == "panel_change")
                {
                    HandlePanelChange(json);
                    return;
                }
                if (type == "panel_close")
                {
                    HandlePanelClose(json);
                    return;
                }
                // Direct delta injection from frontend (remote fallback when
                // Pointer Lock is unavailable in nested iframe contexts).
                // The frontend sends movementX/Y from pointermove events
                // during right-click drag while gameCursorLocked.
                if (type == "mousedelta")
                {
                    if (s_IsRemoteConnection
                        && global::UnityEngine.Cursor.lockState == CursorLockMode.Locked)
                    {
                        float fdx = ExtractJsonFloat(json, "dx");
                        float fdy = ExtractJsonFloat(json, "dy");
                        if (fdx * fdx + fdy * fdy > 0.01f)
                        {
#if UNITY_EDITOR_WIN
                            // When Cursor.lockState=Locked, the engine (and our
                            // bridge) call ClipCursor to confine the OS cursor to
                            // a tiny rect at the GameView center. This prevents
                            // SendInput from actually moving the cursor, so the
                            // engine's position-based delta calculation reads 0.
                            // Temporarily release the clip before injecting the
                            // movement; the engine will re-clip on the next frame
                            // AFTER it has read the position delta.
                            ClipCursor(IntPtr.Zero);

                            s_RawInputSinkLastRegTime = 0;
                            EnsureRawInputSinkRegistered();
                            int dx = (int)fdx;
                            int dy = (int)fdy;
                            var inputs = new INPUT[]
                            {
                                new INPUT
                                {
                                    type = INPUT_MOUSE,
                                    mi = new MOUSEINPUT
                                    {
                                        dx = dx,
                                        dy = dy,
                                        dwFlags = MOUSEEVENTF_MOVE
                                    }
                                }
                            };
                            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
#endif
                            // Also queue an IMGUI event with the delta for any
                            // code paths that read Event.delta directly.
                            Vector2 delta = new Vector2(fdx, fdy);
                            float posX = s_HasLastMousePos ? s_LastMousePos.x * LocalPP : 0f;
                            float posY = s_HasLastMousePos ? s_LastMousePos.y * LocalPP : 0f;
                            QueuePlayModeMouseEvent(
                                EventType.MouseMove, posX, posY,
                                -1, EventModifiers.None, delta);

                            if (s_RemoteDeltaLogCount < 10)
                            {
                                // Use fdx/fdy here so non-Windows builds do not
                                // reference Windows-only local variables.
                                LogVerbose($"[NWB-RemoteDelta] SendInput({fdx:F0},{fdy:F0})+QueuePlayMode pos=({posX},{posY})");
                                s_RemoteDeltaLogCount++;
                            }
                        }
                    }
                    return;
                }
                float rawX = ExtractJsonFloat(json, "x");
                float rawY = ExtractJsonFloat(json, "y");
                int button = ExtractJsonInt(json, "button");

                // Compute click count for double-click detection.
                // Always run C#-side timing/position tracking because browser
                // e.detail may stay at 1 in sandboxed iframes or when
                // setPointerCapture resets the internal click counter.
                // Use MAX(frontend value, C# timing detection) for robustness.
                int frontendClickCount = ExtractJsonInt(json, "clickCount");
                int clickCount = 1;
                if (type == "mousedown")
                {
                    double now = EditorApplication.timeSinceStartup;
                    Vector2 pos = new Vector2(rawX, rawY);
                    if (button == s_LastClickButton
                        && (now - s_LastClickTime) < kDoubleClickMaxDelay
                        && Vector2.Distance(pos, s_LastClickPos) < kDoubleClickMaxDist)
                    {
                        s_CurrentClickCount++;
                    }
                    else
                    {
                        s_CurrentClickCount = 1;
                    }
                    // Use the larger of frontend-supplied and C#-detected count.
                    clickCount = Math.Max(frontendClickCount, s_CurrentClickCount);
                    if (s_CurrentClickCount >= 2)
                        LogVerbose($"[NWB-DblClick] Detected clickCount={clickCount} (frontend={frontendClickCount} csharp={s_CurrentClickCount} dt={(now - s_LastClickTime)*1000:F0}ms dist={Vector2.Distance(pos, s_LastClickPos):F1}px)");
                    s_LastClickTime = now;
                    s_LastClickPos = pos;
                    s_LastClickButton = button;
                }
                else if (type == "mouseup")
                {
                    // Carry the same clickCount as the preceding mousedown.
                    clickCount = s_CurrentClickCount;
                }

                // Browser sends pixel coordinates based on videoWidth/videoHeight.
                // On HiDPI displays, convert pixels to points (1 point = ppp pixels).
                // GrabPixels captures the full GUIView, and EditorWindow.SendEvent uses
                // the same coordinate origin as GUIView (verified: yDiff=0 in logs),
                // so NO tab bar offset subtraction is needed.
                float ppp = EditorGUIUtility.pixelsPerPoint;
                float x = rawX / ppp;
                float y = rawY / ppp;
                bool isMouseDownEvent = type == "mousedown";
                bool isMouseUpEvent = type == "mouseup";
                bool isMouseDragEvent = type == "mousedrag";
                bool isKeyboardInputEvent = type == "keydown" || type == "keyup";
                bool hasCompositeMouseCapture = s_CompositeMouseCaptureView != null &&
                    ShouldUseCompositeMouseCaptureForInput(type);

                // Clamp to GUIView bounds (point units) — use RT size / ppp.
                float maxW = s_CaptureRT != null ? (s_CaptureRT.width / ppp) : (s_OffscreenTarget != null ? s_OffscreenTarget.position.width : s_CompositeFrameBoundsLogical.width);
                float maxH = s_CaptureRT != null ? (s_CaptureRT.height / ppp) : (s_OffscreenTarget != null ? s_OffscreenTarget.position.height : s_CompositeFrameBoundsLogical.height);
                if (s_CompositeActive)
                {
                    Vector2 screenPoint = new Vector2(
                        s_CompositeFrameBoundsLogical.xMin + (rawX - s_CompositeFrameOffsetPixels.x) / Mathf.Max(s_CompositeFrameScalePixels.x * ppp, 0.001f),
                        s_CompositeFrameBoundsLogical.yMin + (rawY - s_CompositeFrameOffsetPixels.y) / Mathf.Max(s_CompositeFrameScalePixels.y * ppp, 0.001f));
                    object hitView = null;
                    CompositeCaptureSlot hitSlot = null;

                    // Key events do not carry meaningful pointer coordinates in
                    // composite mode (often rawX/rawY == 0). Routing keydown/keyup
                    // by hit-testing (0,0) wrongly redirects focus to whichever slot
                    // occupies top-left. Reuse the last explicit target selected by
                    // mouse interaction so:
                    // - click GameView -> WASD drives game
                    // - click other view -> WASD does not drive game
                    if (isKeyboardInputEvent && s_OffscreenTarget != null)
                    {
                        hitView = GetParentView(s_OffscreenTarget);
                        hitSlot = FindCompositeSlotByWindow(s_OffscreenTarget);
                    }
                    else if (hasCompositeMouseCapture)
                    {
                        hitView = s_CompositeMouseCaptureView;
                        hitSlot = s_CompositeMouseCaptureSlot;
                    }

                    if (!isKeyboardInputEvent && (hitView == null || isMouseDownEvent))
                    {
                        hitView = null;
                        hitSlot = null;
                        TryHitCompositeView(screenPoint, out hitView, out hitSlot);
                    }

                    if (hitView == null && s_OffscreenTarget == null)
                    {
                        foreach (CompositeCaptureSlot candidate in s_CompositeSlots.Values)
                        {
                            if (candidate?.Window == null) continue;
                            hitView = GetParentView(candidate.Window);
                            hitSlot = candidate;
                            break;
                        }
                    }

                    if (hitView != null)
                    {
                        Rect viewRect = GetViewScreenPosition(hitView);
                        x = screenPoint.x - viewRect.xMin;
                        y = screenPoint.y - viewRect.yMin;
                        maxW = viewRect.width;
                        maxH = viewRect.height;
                    }

                    EditorWindow activeWindow = GetActualViewFromHostView(hitView) ?? hitSlot?.Window;
                    if (activeWindow == null)
                    {
                        if (type == "mousemove" || type == "mousedrag")
                            TrySendDefaultEditorCursor();
                        return;
                    }
                    s_OffscreenTarget = activeWindow;
                    s_OffscreenTargetType = activeWindow.GetType();
                    s_CaptureRT = hitSlot?.SourceRT;
#if UNITY_EDITOR_WIN
                    // Update global HWNDs from the resolved slot so that
                    // keyboard injection (SetFocus + PostMessageW) targets
                    // the correct GUIView for Input.GetKey in Play mode.
                    //
                    // IMPORTANT: slot.GUIViewHwnd holds the reflected DockArea
                    // pointer (from EditorWindowNativeHandleHelper.GetGUIViewHandle)
                    // which is required by PrintWindow to capture the correct
                    // D3D swap chain. However, on some TuanJie builds this is a
                    // C++ object pointer (>32-bit), not a valid Win32 HWND.
                    // SetFocus/PostMessageW require a real HWND. When the reflected
                    // pointer fails IsWindow(), fall back to the first child of
                    // ContainerHwnd — a GUIView whose GUIViewWndProc will route
                    // WM_KEYDOWN to the global Input Manager for Input.GetKey.
                    if (hitSlot != null)
                    {
                        s_OffscreenTargetContainerHwnd = hitSlot.ContainerHwnd;
                        IntPtr inputHwnd = hitSlot.GUIViewHwnd;
                        if (inputHwnd != IntPtr.Zero && !IsWindow(inputHwnd))
                        {
                            IntPtr child = GetWindow(hitSlot.ContainerHwnd, GW_CHILD);
                            inputHwnd = child != IntPtr.Zero ? child : hitSlot.ContainerHwnd;
                            // LogVerbose($"[NWB-Composite] GUIViewHwnd 0x{hitSlot.GUIViewHwnd.ToInt64():X} is a C++ pointer; using child HWND 0x{inputHwnd.ToInt64():X} for keyboard injection");
                        }
                        s_OffscreenTargetGUIViewHwnd = inputHwnd;
                    }
#endif
#if UNITY_EDITOR_OSX
                    if (hitSlot != null && hitSlot.NSWindow != IntPtr.Zero)
                        s_OffscreenTargetNSWindow = hitSlot.NSWindow;
#endif

                    if (isMouseDownEvent)
                    {
                        s_CompositeMouseCaptureView = hitView;
                        s_CompositeMouseCaptureSlot = hitSlot;
                        hasCompositeMouseCapture = hitView != null;
                    }
                }
                if (!(s_CompositeActive && hasCompositeMouseCapture && (isMouseDragEvent || isMouseUpEvent)))
                {
                    x = Mathf.Clamp(x, 0f, maxW);
                    y = Mathf.Clamp(y, 0f, maxH);
                }
                Vector2 mousePos = new Vector2(x, y);
                Vector2 mouseDelta = s_HasLastMousePos ? (mousePos - s_LastMousePos) : Vector2.zero;
                bool isSceneViewTarget = s_OffscreenTargetType != null && s_OffscreenTargetType.Name == "SceneView";
                bool isGameViewTarget = s_OffscreenTargetType != null && s_OffscreenTargetType.Name == "GameView";
                // GameView in Play mode needs a different keyboard input path:
                // Input.GetKey reads from the native Input Manager, not IMGUI events.
                bool isGamePlayMode = isGameViewTarget && EditorApplication.isPlaying && !EditorApplication.isPaused;
                Vector2 activeMousePos = mousePos;
                Vector2 activeMouseDelta = mouseDelta;

                // In composite mode, rawX/rawY are relative to the composite
                // frame (all panels), not to the GameView's own render texture.
                // Convert local logical coordinates (x, y) to GameView-local raw
                // pixel coordinates so that InjectStreamingUIPress and
                // QueuePlayModeMouseEvent receive the correct position.
                // x,y are IMGUI points (top-left origin, Y increases downward).
                // Convert to physical pixels matching single-window rawX/rawY.
                float gameRawX = rawX;
                float gameRawY = rawY;
                if (s_CompositeActive)
                {
                    gameRawX = x * ppp;
                    gameRawY = y * ppp;
                }
                // Keep tab-drag screen-point tracking mouse-only. Key events often
                // carry (0,0), which would pollute the cached screen point.
                if (IsMouseEvent(type))
                    UpdateLastInputScreenPoint(rawX, rawY, ppp, mousePos);

                // Parse modifier flags from browser.
                EventModifiers mods = EventModifiers.None;
                if (ExtractJsonBool(json, "shift")) mods |= EventModifiers.Shift;
                if (ExtractJsonBool(json, "ctrl"))  mods |= EventModifiers.Control;
                if (ExtractJsonBool(json, "alt"))   mods |= EventModifiers.Alt;
                if (ExtractJsonBool(json, "meta"))  mods |= EventModifiers.Command;

                Event evt = null;
                string keyForCommand = null;

                switch (type)
                {
                    case "mousemove":
                        evt = new Event { type = EventType.MouseMove, mousePosition = activeMousePos, delta = activeMouseDelta, modifiers = mods };
                        break;
                    case "mousedown":
                        evt = new Event { type = EventType.MouseDown, mousePosition = activeMousePos, button = BrowserBtnToUnityBtn(button), clickCount = clickCount, modifiers = mods };
                        break;
                    case "mouseup":
                        evt = new Event { type = EventType.MouseUp, mousePosition = activeMousePos, button = BrowserBtnToUnityBtn(button), clickCount = clickCount, modifiers = mods };
                        break;
                    case "mousedrag":
                        evt = new Event { type = EventType.MouseDrag, mousePosition = activeMousePos, delta = activeMouseDelta, button = BrowserBtnToUnityBtn(button), modifiers = mods };
                        break;
                    case "wheel":
                        float deltaX = ExtractJsonFloat(json, "deltaX");
                        float deltaY = ExtractJsonFloat(json, "deltaY");
                        // Browser wheel delta is in CSS pixels (often +/-120 per notch),
                        // while Unity SceneView expects a much smaller delta scale.
                        // Normalize large mouse-wheel deltas to keep zoom speed close to
                        // native TJ/Unity behavior while preserving small trackpad deltas.
                        if (Mathf.Abs(deltaX) > 10f) deltaX *= 0.025f;
                        if (Mathf.Abs(deltaY) > 10f) deltaY *= 0.025f;
                        evt = new Event
                        {
                            type = EventType.ScrollWheel,
                            mousePosition = mousePos,
                            delta = new Vector2(deltaX, deltaY),
                            modifiers = mods
                        };
                        break;
                    case "keydown":
                    {
                        string key = ExtractJsonString(json, "key");
                        keyForCommand = key;
                        KeyCode kc = ParseKeyCode(key);
                        char ch = (!string.IsNullOrEmpty(key) && key.Length == 1) ? key[0] : '\0';
                        // Map special keys: set keyCode and appropriate character.
                        // CRITICAL: Unity's TextEditor.HandleKeyEvent uses s_KeyEditOps
                        // dictionary keyed by Event (compared via Event.Equals which checks
                        // keyCode + modifiers). Event.KeyboardEvent("backspace") sets
                        // EventModifiers.FunctionKey on Backspace/Delete/arrows/etc.
                        // Without FunctionKey, the dictionary lookup fails silently.
                        if (key == "Enter" || key == "Return") { kc = KeyCode.Return; ch = '\n'; }
                        else if (key == "Tab") { kc = KeyCode.Tab; ch = '\t'; }
                        else if (key == "Backspace") { ch = '\0'; mods |= EventModifiers.FunctionKey; }
                        else if (key == "Delete") { ch = '\0'; mods |= EventModifiers.FunctionKey; }
                        else if (key == "Escape") { ch = '\0'; }
                        else if (key == "ArrowLeft" || key == "ArrowRight"
                              || key == "ArrowUp" || key == "ArrowDown"
                              || key == "Home" || key == "End"
                              || key == "PageUp" || key == "PageDown")
                        { ch = '\0'; mods |= EventModifiers.FunctionKey; }
                        else if (key != null && key.Length > 1 && key.StartsWith("F")
                              && int.TryParse(key.Substring(1), out _))
                        { mods |= EventModifiers.FunctionKey; }
                        evt = new Event { type = EventType.KeyDown, keyCode = kc, character = ch, modifiers = mods };
                        break;
                    }
                    case "keyup":
                    {
                        string key = ExtractJsonString(json, "key");
                        keyForCommand = key;
                        KeyCode kc = ParseKeyCode(key);
                        char ch = (!string.IsNullOrEmpty(key) && key.Length == 1) ? key[0] : '\0';
                        if (key == "Backspace" || key == "Delete"
                            || key == "ArrowLeft" || key == "ArrowRight"
                            || key == "ArrowUp" || key == "ArrowDown"
                            || key == "Home" || key == "End"
                            || key == "PageUp" || key == "PageDown")
                        { mods |= EventModifiers.FunctionKey; }
                        else if (key != null && key.Length > 1 && key.StartsWith("F")
                              && int.TryParse(key.Substring(1), out _))
                        { mods |= EventModifiers.FunctionKey; }
                        evt = new Event { type = EventType.KeyUp, keyCode = kc, character = ch, modifiers = mods };
                        break;
                    }
                }

                if (evt != null)
                {
                    if (type == "mousedown" && !isGamePlayMode && button == 0)
                        LockEditorDragCursorFromHover();

                    // Ensure the target window is the active tab before sending events.
                    EnsureActiveTabInDockArea(s_OffscreenTarget);

                    // In composite mode, switch the DockArea tab to match the
                    // current target. Multiple slots share one DockArea, and
                    // Internal_SendEvent only dispatches through the selected
                    // tab's UIElements tree. Without this switch, overlays
                    // (e.g. SceneView OrientationGizmo) never see the event.
                    EnsureCompositeTabForTarget();

                    // GUIView.hasFocus is a native property that returns false when Unity
                    // is not the macOS key window.  Two mechanisms ensure Editor IMGUI
                    // controls (SearchField, EditorGUI.TextField) work in offscreen mode:
                    // Override s_HasCurrentWindowKeyFocusFunc delegate so
                    // HasKeyboardFocus() returns true → BeginEditing() is called.
                    //
                    // SKIP this override for GameView Play mode keyboard AND mouse events:
                    // the override + editingTextField=true trick tells IMGUI a text field
                    // is focused, which prevents the GameView from forwarding keyboard
                    // events to the runtime Input system (Input.GetKey/GetAxis).
                    // For mouse events, the override can interfere with GameView's
                    // internal focus checks for forwarding mouse state to the runtime
                    // Input Manager (Input.mousePosition, Input.GetMouseButton).
                    // In Play mode, we use native SetFocus instead to ensure hasFocus
                    // returns true without IMGUI side effects.
                    bool isKeyEvent = (type == "keydown" || type == "keyup");
                    bool isMouseInputEvent = (type == "mousedown" || type == "mouseup"
                        || type == "mousemove" || type == "mousedrag");
                    if (!(isGamePlayMode && (isKeyEvent || isMouseInputEvent)))
                        OverrideKeyFocusForOffscreen();

                    // Only set editingTextField for keyboard events when NOT handling
                    // a SceneView shortcut. Shortcuts like Q/W/E/R/T/Y should
                    // not activate text editing mode.
                    bool shortcutHandled = false;
                    if (type == "keydown")
                    {
                        shortcutHandled = TryHandleSceneViewShortcut(keyForCommand, mods, isSceneViewTarget);
                    }

                    // Skip editingTextField for GameView Play mode — setting this flag
                    // prevents the GameView from forwarding keyboard events to the
                    // runtime Input system that Input.GetKey reads from.
                    if (isKeyEvent && !shortcutHandled && !isGamePlayMode)
                    {
                        // Modifier-only keys (Alt, Shift, Control, Meta) are not text
                        // input and should not enable text-editing mode. Doing so would
                        // cause FallbackHandleKeyDown to be called for modifier presses,
                        // corrupting internal text editor state.
                        bool isModifierOnly = keyForCommand == "Alt" || keyForCommand == "Shift"
                                           || keyForCommand == "Control" || keyForCommand == "Meta";
                        if (!isModifierOnly)
                            EditorGUIUtility.editingTextField = true;
                    }

                    bool sentToPopup = false;
                    bool viaEditorWindow = false;

                    // When a frontend popup has been sent but the browser
                    // hasn't closed it yet, swallow mouse events so that
                    // stale mousedown/mouseup arriving while the popup
                    // travels over DataChannel won't trigger unrelated
                    // Unity actions (e.g. clicking Gizmos while the
                    // Play Focused popup is still being delivered).
                    if (!sentToPopup && s_FrontendPopupSent && IsMouseEvent(type))
                    {
                        if (s_InputEventCount <= 30 || s_ActionEventCount <= 10)
                            LogVerbose($"[NWB-PopupGuard] Swallowed {type} ({x:F0},{y:F0}) — frontend popup active");
                        // When a mousedown is swallowed (user clicked "elsewhere" in
                        // the stream), also close any native popup that escaped our
                        // initial cleanup. In streaming, injected events don't reach
                        // the Win32 popup's message loop, so the popup would persist
                        // until actively closed.
                        if (type == "mousedown")
                        {
                            DismissAllNativePopups();
                            // Clear context menu forward state so the
                            // corresponding mouseup won't trigger
                            // TrySynthesizeAndForwardContextMenuPopup.
                            // ConsoleWindow never received this mousedown,
                            // so m_RightClickedDelayFrame was not set and
                            // ContextClick would produce zero items.
                            if (IsBrowserRightClickButton(button))
                                ClearContextMenuForwardState();
                        }
                        // Still repaint and count the event below, just
                        // don't forward it to the EditorWindow.
                        viaEditorWindow = false;
                    }
                    else if (shortcutHandled)
                    {
                        // Shortcut already handled; repaint to reflect change.
                        viaEditorWindow = true;
                    }
                    else
                    {
                        // Keep synthetic drag detection state in sync with the latest
                        // mouse gesture so tiny click jitter is not treated as drag.
                        UpdateSyntheticDragGestureState(s_OffscreenTarget, type, activeMousePos);
                    }

                    if (!viaEditorWindow && TryStartSyntheticProjectAssetDrag(s_OffscreenTarget, type, activeMousePos))
                    {
                        viaEditorWindow = true;
                    }
                    else if (!viaEditorWindow && TryStartSyntheticHierarchyGameObjectDrag(s_OffscreenTarget, type, activeMousePos))
                    {
                        viaEditorWindow = true;
                    }
                    else if (!viaEditorWindow && TryBlockNativeEditorDragStart(s_OffscreenTarget, type, activeMousePos))
                    {
                        viaEditorWindow = true;
                    }
                    else if (!viaEditorWindow && TryForwardRemoteNativeDragAndDropEvent(s_OffscreenTarget, type, activeMousePos, mods))
                    {
                        viaEditorWindow = true;
                    }
                    if (!viaEditorWindow && !sentToPopup)
                    {
                        if (type == "keydown")
                        {
                            if (isGamePlayMode)
                            {
                                // GameView Play mode: send a single KeyDown event without
                                // the two-phase text editing dispatch. editingTextField and
                                // key focus override are not active, so the GameView can
                                // forward the event to the runtime Input system.
#if UNITY_EDITOR_WIN
                                // GUIView.hasFocus is a native C++ property that returns
                                // false when the window is offscreen. GameView.OnGUI checks
                                // hasFocus before forwarding keyboard events to the runtime
                                // Input Manager. SetFocus gives thread-level keyboard focus
                                // to the GUIView child HWND, making hasFocus return true so
                                // the forwarding succeeds and Input.GetKey sees the keys.
                                IntPtr focusTarget = s_OffscreenTargetGUIViewHwnd != IntPtr.Zero
                                    ? s_OffscreenTargetGUIViewHwnd
                                    : s_OffscreenTargetContainerHwnd;
                                if (focusTarget != IntPtr.Zero)
                                {
                                    bool keyAlreadyHeld = !string.IsNullOrEmpty(keyForCommand) &&
                                        s_WinHeldKeys.Contains(keyForCommand);
                                    double nowFocus = EditorApplication.timeSinceStartup;
                                    bool shouldRefocus = !keyAlreadyHeld ||
                                        (nowFocus - s_WinPlayKeyLastFocusTime) > kWinPlayKeyRepeatRefocusSec;
                                    if (shouldRefocus)
                                    {
                                        SetFocus(focusTarget);
                                        s_WinPlayKeyLastFocusTime = nowFocus;
                                        RestoreCanvasLayout();
                                        // SetFocus triggers WM_SETFOCUS → InputSetWindow which
                                        // overwrites our RIDEV_INPUTSINK registration. Re-register.
                                        s_RawInputSinkLastRegTime = 0;
                                        EnsureRawInputSinkRegistered();
                                    }
                                }
#elif UNITY_EDITOR_OSX
                                // On macOS, ALWAYS activate + makeKeyWindow on every
                                // keydown, mirroring Windows' SetFocus-on-every-keydown
                                // pattern. The old approach only activated on the first
                                // keydown (guarded by s_MacPlayKeyboardActive), but the
                                // mouseup handler could set that flag without actually
                                // activating Unity, causing subsequent keydowns to skip
                                // activation and lose CGEvents to the wrong app.
                                if (s_OffscreenTargetNSWindow != IntPtr.Zero)
                                {
                                    EnsureObjCRefs();
                                    // Flush stale held keys before activating for the
                                    // new key. The browser doesn't send keyup when it
                                    // loses focus (Unity activation steals focus), so
                                    // s_MacHeldKeys may contain keys from a previous
                                    // press that are no longer physically held. Sending
                                    // CGEvent(keyup) now clears the Input Manager state
                                    // while Unity is still becoming active, preventing
                                    // ghost movement and reversed direction.
                                    if (s_MacHeldKeys.Count > 0)
                                    {
                                        foreach (var stale in s_MacHeldKeys)
                                        {
                                            if (stale != keyForCommand)
                                                PostNativeCGKeyEvent(false, stale);
                                        }
                                        if (string.IsNullOrEmpty(keyForCommand))
                                            s_MacHeldKeys.Clear();
                                        else
                                        {
                                            s_MacHeldKeys.Clear();
                                            s_MacHeldKeys.Add(keyForCommand);
                                        }
                                    }
                                    objc_msgSend_void_bool(s_NSApp, s_ActivateIgnoringSel, true);
                                    objc_msgSend_void(s_OffscreenTargetNSWindow, s_MakeKeyWindowSel);
                                    s_MacPlayKeyboardActive = true;
                                    s_MacKeyReinjectionFrames = kMacKeyReinjectionMaxFrames;
                                }
#endif
                                s_OffscreenTarget.SendEvent(new Event(evt));
#if UNITY_EDITOR_WIN
                                // Also inject via Win32 PostMessage so that the native
                                // input pipeline receives WM_KEYDOWN for Input.GetKey.
                                PostNativeKeyMessage(true, keyForCommand);
                                s_WinHeldKeys.Add(keyForCommand);
                                // Also inject into the new Input System (activeInputHandler=2)
                                // which processes keyboard via WM_INPUT, not WM_KEYDOWN.
                                // WM_INPUT is blocked by isFocused check when offscreen,
                                // so we use QueueStateEvent to bypass it entirely.
                                QueueInputSystemKeyboardState();
#elif UNITY_EDITOR_OSX
                                // Post CGEvent so the native Input Manager receives the
                                // keydown through the macOS event pipeline.
                                PostNativeCGKeyEvent(true, keyForCommand);
                                s_MacHeldKeys.Add(keyForCommand);
                                s_MacPlayKeyLastTime = EditorApplication.timeSinceStartup;
#endif
                                if (s_ActionEventCount <= 10)
                                {
#if UNITY_EDITOR_WIN
                                    LogVerbose($"[NWB-GameInput] KeyDown: kc={evt.keyCode} mod={evt.modifiers} (Play mode, native inject, focus=0x{focusTarget.ToInt64():X})");
#elif UNITY_EDITOR_OSX
                                    LogVerbose($"[NWB-GameInput] KeyDown: kc={evt.keyCode} mod={evt.modifiers} (Play mode, held={s_MacHeldKeys.Count} reinject={s_MacKeyReinjectionFrames} nsw={s_OffscreenTargetNSWindow})");
#else
                                    LogVerbose($"[NWB-GameInput] KeyDown: kc={evt.keyCode} mod={evt.modifiers} (Play mode)");
#endif
                                }
                            }
                            else
                            {
                                // Unity's EventUtility.KeyDown sends TWO events for each keydown:
                                //   1) KeyDown with keyCode set (for HandleKeyEvent / keyCode dispatch)
                                //   2) KeyDown with character set, keyCode=None (for text insertion)
                                // This two-phase dispatch is required for special keys like Backspace
                                // to be handled by TextEditor.HandleKeyEvent's key-action dictionary.
                                // CRITICAL: FunctionKey modifier must be set for Backspace/Delete/arrows
                                // to match the Event dictionary keys created by Event.KeyboardEvent().
                                Event phase1 = new Event { type = EventType.KeyDown, keyCode = evt.keyCode, modifiers = evt.modifiers };
                                bool used1 = s_OffscreenTarget.SendEvent(phase1);

                                char phase2Char = evt.character != '\0' ? evt.character : (char)evt.keyCode;
                                Event phase2 = new Event { type = EventType.KeyDown, keyCode = KeyCode.None, character = phase2Char, modifiers = evt.modifiers };
                                bool used2 = s_OffscreenTarget.SendEvent(phase2);

                                if (s_ActionEventCount <= 10)
                                    LogVerbose($"[NWB-Input-Diag] KeyDown 2-phase: kc={evt.keyCode} ch={(int)phase2Char} mod={evt.modifiers} used1={used1} used2={used2} kbCtrl={GUIUtility.keyboardControl} editTF={EditorGUIUtility.editingTextField}");

                                // On macOS when Unity is hidden, GUIView.hasFocus (native) is
                                // always false, which blocks IsEditingControl() inside DoTextField.
                                // If neither phase consumed the event, fall back to direct
                                // TextEditor manipulation.
                                if (!used1 && !used2 && EditorGUIUtility.editingTextField)
                                {
                                    FallbackHandleKeyDown(phase1, phase2Char);
                                }
                            }
                            viaEditorWindow = true;
                        }
                        else if (type == "mousedown")
                        {
                            // Track the time of remote mousedown so that popup
                            // hiding logic can distinguish browser-triggered
                            // popups from local user popups.
                            s_LastRemoteMousedownTime = EditorApplication.timeSinceStartup;
                            // Track whether mousedown is in the tab bar region so
                            // subsequent mousedrag events can skip SendEvent and
                            // avoid DockArea.DragTab → ContainerWindow crash.
                            s_MouseDownInTabBar = IsInDockTitleBarRegion(activeMousePos.y);

                            // Set context menu forward expectation for:
                            // 1. Non-GameView/SceneView targets (always)
                            // 2. GameView/SceneView when click is in tab header area
                            bool isTabHeaderClick = IsInDockTitleBarRegion(activeMousePos.y);
                            if (IsBrowserRightClickButton(button) &&
                                (ShouldSynthesizeContextClickForTarget() || isTabHeaderClick))
                                s_ExpectContextMenuForward = true;

                            // Scene picking can fail if IMGUI still thinks a text field
                            // is being edited (stale keyboardControl/editingTextField from
                            // previous popup/search interactions). Clear it before SceneView
                            // hit-testing so Move tool can select objects and show handles.
                            if (isSceneViewTarget && EditorGUIUtility.editingTextField)
                            {
                                EditorGUIUtility.editingTextField = false;
                                if (GUIUtility.keyboardControl != 0)
                                    GUIUtility.keyboardControl = 0;
                                if (s_ActionEventCount <= 10)
                                    LogVerbose("[NWB-ScenePick] Cleared stale text-edit focus before SceneView mousedown");
                            }

                            // If a frontend popup is already active, skip
                            // the mousedown to avoid unintended actions.
                            if (s_FrontendPopupSent)
                            {
                                if (s_ActionEventCount <= 10)
                                    LogVerbose($"[NWB-PopupGuard] Skipped mousedown ({x:F0},{y:F0}) — frontend popup active");
                                viaEditorWindow = false;
                            }
                            else if (button == 0 &&
                                TryBeginPaneOptionsClick(activeMousePos.x, activeMousePos.y, maxW))
                            {
                                viaEditorWindow = true;
                            }
                            else if (isGamePlayMode
                                && IsInGamePlayArea(gameRawY, ppp))
                            {
                                // GameView Play mode (game area only): dual injection.
                                // Clicks in the toolbar area (rawY < threshold) fall
                                // through to the normal SendEvent path so IMGUI
                                // toolbar popups (Game, Display, etc.) still work.
                                //
                                // Path A: QueueGameViewInputEvent queues IMGUI events
                                // for the player loop (MouseDown/Up only).
                                //
                                // Path B: Direct uGUI EventSystem injection — raycast
                                // at screen position and execute IPointerDown/Up/Click
                                // handlers, bypassing StandaloneInputModule's reliance
                                // on Input.mousePosition (incorrect when off-screen).

#if UNITY_EDITOR_WIN
                                IntPtr focusMouse = s_OffscreenTargetGUIViewHwnd != IntPtr.Zero
                                    ? s_OffscreenTargetGUIViewHwnd
                                    : s_OffscreenTargetContainerHwnd;
                                if (focusMouse != IntPtr.Zero)
                                {
                                    SetFocus(focusMouse);
                                    // SetFocus triggers native WM_SETFOCUS processing which
                                    // can corrupt Camera.aspect via the docked GameView's
                                    // UpdateScreenManager → OnWindowSizeHasChanged chain.
                                    // Restore immediately after SetFocus returns.
                                    RestoreGameCameraAspects();
                                    // SetFocus also corrupts Canvas.renderingDisplaySize
                                    // (Screen.width/Height) to the original docked GameView
                                    // dimensions, causing ScreenSpaceOverlay UI to shift.
                                    RestoreCanvasLayout();
                                    s_WinPlayKeyLastFocusTime = EditorApplication.timeSinceStartup;
                                    s_RawInputSinkLastRegTime = 0;
                                    EnsureRawInputSinkRegistered();
                                }
#endif
                                // Path A: queue IMGUI event
                                QueuePlayModeMouseEvent(EventType.MouseDown, gameRawX, gameRawY, button, mods);

                                // Path B: direct uGUI click injection
                                InjectStreamingUIPress(gameRawX, gameRawY, button);

#if UNITY_EDITOR_WIN
                                // Path C: native Win32 mouse button injection so
                                // the native Input Manager updates Input.GetMouseButton /
                                // Input.GetKey(KeyCode.Mouse0/Mouse1/Mouse2).
                                // SendMessage + ReleaseCapture undoes SetMouseCapture
                                // that WM_*BUTTONDOWN triggers.
                                // rawX/rawY MUST be passed so the position falls inside
                                // m_GameViewClippedRect in GUIViewWndProc.
                                //
                                // SKIP in composite mode: the native WM_LBUTTONDOWN
                                // triggers ProcessInputMessage on the off-screen GUIView,
                                // which causes a native OnGUI cycle that disrupts the
                                // GameView's backbuffer state (horizontal compression).
                                // QueueGameViewInputEvent (QUEUED+UI path) already handles
                                // Input.GetMouseButton via the managed input pipeline.
                                if (s_CompositeSlots == null || s_CompositeSlots.Count == 0)
                                    PostNativeMouseButtonMessage(true, button, (int)rawX, (int)rawY);

                                // Path D (new Input System): inject mouse position + button state
                                // into UnityEngine.InputSystem so InputAction-based drag/touch
                                // detection (bound to <Mouse>/position and <Mouse>/leftButton) works.
                                // Without this, Mouse.current.position stays at (0,0) and
                                // Mouse.current.leftButton never updates in offscreen streaming mode.
                                {
                                    float pppD = EditorGUIUtility.pixelsPerPoint;
                                    float sx = RawPixelToScreenX(gameRawX);
                                    float sy = RawPixelToScreenY(gameRawY, pppD);
                                    QueueInputSystemMouseState(new Vector2(sx, sy), button, true);
                                }
#elif UNITY_EDITOR_OSX
                                // Path C (macOS): native CGEvent mouse button injection.
                                // ONLY when cursor is locked (FPS mode).
                                // CGEventCreateMouseEvent teleports the real macOS cursor
                                // to the offscreen window center. In unlocked mode this
                                // causes the cursor to leave the browser iframe, preventing
                                // mouseup delivery and breaking the click sequence.
                                // In unlocked mode, InjectStreamingUIPress (Path B) is
                                // sufficient for UI button clicks.
                                if (global::UnityEngine.Cursor.lockState == CursorLockMode.Locked)
                                {
                                    EnsureObjCRefs();
                                    if (s_OffscreenTargetNSWindow != IntPtr.Zero)
                                    {
                                        objc_msgSend_void_bool(s_NSApp, s_ActivateIgnoringSel, true);
                                        objc_msgSend_void(s_OffscreenTargetNSWindow, s_MakeKeyWindowSel);
                                    }
                                    PostNativeCGMouseButtonEvent(true, button);
                                }
#endif

                                // Allow cursor lock/hide when clicking in the game
                                // area — replicates GameView.AllowCursorLockAndHide.
                                Unsupported.SetAllowCursorHide(true);
                                try { SetAllowCursorLockReflection(true); }
                                catch (Exception) { /* best effort */ }

                                // Force synchronous repaint for 3 frames after click
                                // to eliminate the 1-frame backbuffer lag compression.
                                if (s_CompositeSlots != null && s_CompositeSlots.Count > 0)
                                    s_CompositeForceRepaintFrames = 3;

                                if (s_ActionEventCount <= 10)
                                {
                                    float pppDbg = EditorGUIUtility.pixelsPerPoint;
                                    float gpY = gameRawY - GetGameViewOffsetPixels(pppDbg);
                                    LogVerbose($"[NWB-PlayMouse] mousedown QUEUED+UI: " +
                                        $"rawPx=({rawX},{rawY}) gameRaw=({gameRawX:F0},{gameRawY:F0}) gamePx=({gameRawX:F0},{gpY:F0}) " +
                                        $"btn={button} Screen=({Screen.width},{Screen.height}) " +
                                        $"ppp={pppDbg} tabOff={s_TabBarOffsetY}");
                                }
                                viaEditorWindow = true;
                            }
                            else
                            {
                                // Capture toolbar trigger geometry at click time so
                                // popup/panel alignment can use exact left boundary.
                                TryCaptureToolbarAnchorRect(x, y, ppp);

                                // Warm-up state before mousedown: send MouseMove +
                                // Layout so IMGUI state is current for the click
                                // handler. Do NOT send Repaint here — on Windows,
                                // SendEvent(Repaint) calls ForceRepaint() → DoPaint()
                                // → Present(), which can activate the Unity window
                                // (moving it from -32000 back on-screen) and trigger
                                // a GFX context/backbuffer disruption that causes
                                // persistent horizontal compression of game content.
                                // Layout alone is sufficient for IMGUI hit-testing.
                                try
                                {
                                    Event warmMove = new Event
                                    {
                                        type = EventType.MouseMove,
                                        mousePosition = activeMousePos,
                                        delta = activeMouseDelta,
                                        modifiers = mods
                                    };
                                    s_OffscreenTarget.SendEvent(warmMove);
                                    // Layout event must carry the real mouse position so
                                    // that IMGUI overlays (e.g. SceneOrientationGizmo)
                                    // register their Handles.Button controls via
                                    // HandleUtility.AddControl. The gizmo skips handle
                                    // registration when the mouse is outside gizmoRect —
                                    // a default (0,0) Layout event causes that check to
                                    // fail, leaving nearestControl=0 for the MouseDown.
                                    s_OffscreenTarget.SendEvent(new Event { type = EventType.Layout, mousePosition = activeMousePos });
#if UNITY_EDITOR_WIN
                                    // Fix: warm-up events also call UpdateScreenManager
                                    // which corrupts Camera.aspect via OnWindowSizeHasChanged
                                    if (isGameViewTarget)
                                        RestoreGameCameraAspects();
#endif
                                }
                                catch (Exception) { }

                                // Snapshot state before SendEvent so we can detect
                                // what the mousedown handler created.
                                object prevPopupCbInstance = GetPopupCallbackInfoInstance();
                                string prevMCSig = GetMenuControllerSignature();

                                int kbBefore = GUIUtility.keyboardControl;
                                bool etfBefore = EditorGUIUtility.editingTextField;
                                int hotBefore = GUIUtility.hotControl;
                                Event mdEvt = new Event(evt);

#if UNITY_EDITOR_WIN
                                // Right-click mousedown may trigger GenericMenu.ShowAsContext()
                                // → TrackPopupMenuEx INSIDE the same SendEvent call.
                                // Left-click on DropdownButton targets (ProjectBrowser "+",
                                // SceneHierarchyWindow "Create") calls DisplayPopupMenu which
                                // only QUEUES gDelayedContextMenu in C++ — TrackPopupMenuEx
                                // fires on the NEXT GUIView::OnInputEvent (Repaint/Layout).
                                // Right-click blocks the main thread; pre-activate auto-dismiss
                                // for that case only. DisplayPopup is handled post-mousedown.
                                string displayPrefixDown = null;
                                if (s_OffscreenTargetType != null && !IsBrowserRightClickButton(button))
                                {
                                    switch (s_OffscreenTargetType.Name)
                                    {
                                        case "ProjectBrowser": displayPrefixDown = "Assets/Create"; break;
                                        case "SceneHierarchyWindow": displayPrefixDown = "GameObject"; break;
                                    }
                                }
                                bool isDisplayPopupTargetDown = !string.IsNullOrEmpty(displayPrefixDown);
                                // Auto-dismiss during mousedown only for right-click
                                // (which calls TrackPopupMenuEx synchronously).
                                // DisplayPopup targets are handled separately below.
                                bool mousedownAutoDismiss = s_ExpectContextMenuForward
                                    && IsBrowserRightClickButton(button);
                                long mdSendEventStart = 0;
                                if (mousedownAutoDismiss)
                                {
                                    mdSendEventStart = System.Diagnostics.Stopwatch.GetTimestamp();
                                    LogVerbose($"[NWB-ContextMenu] Pre-mousedown: autoDismiss=true target={s_OffscreenTargetType?.Name} prevMCSig={prevMCSig}");
                                    ScheduleNativePopupAutoDismiss();
                                }
#endif
                                bool consumed = s_OffscreenTarget.SendEvent(mdEvt);
                                CaptureProjectAssetSelectionAfterMouseDown(s_OffscreenTarget, type);
#if UNITY_EDITOR_WIN
                                if (mousedownAutoDismiss)
                                {
                                    CancelNativePopupAutoDismiss();
                                    double mdSendEventMs = (System.Diagnostics.Stopwatch.GetTimestamp() - mdSendEventStart)
                                        / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
                                    string postSig = GetMenuControllerSignature();
                                    LogVerbose($"[NWB-ContextMenu] Post-mousedown SendEvent: elapsed={mdSendEventMs:F1}ms consumed={consumed} postMCSig={postSig} prevMCSig={prevMCSig}");
                                    if (mdSendEventMs > 200)
                                        CodelyLogger.LogWarning($"[NWB-ContextMenu] SendEvent(MouseDown) BLOCKED for {mdSendEventMs:F0}ms — TrackPopupMenuEx was likely active");
                                }

                                // Fix: SendEvent(MouseDown) calls native UpdateScreenManager
                                // which resets Camera.aspect to the original docked GameView's
                                // Display 0 dimensions. Restore correct aspect immediately.
                                if (isGameViewTarget)
                                    RestoreGameCameraAspects();

                                // After auto-dismiss broke TrackPopupMenuEx, the menu items
                                // remain in MenuController. Read and forward them to the
                                // frontend now so the user sees the popup immediately.
                                if (mousedownAutoDismiss && !s_FrontendPopupSent)
                                {
                                    string postMCSig = GetMenuControllerSignature();
                                    if (postMCSig != prevMCSig && postMCSig != "0|")
                                    {
                                        LogVerbose($"[NWB-ContextMenu] Mousedown created popup: sig {prevMCSig} → {postMCSig}");
                                        s_SuppressTargetRepaintWhilePopupOpen = true;
                                        TryForwardMenuControllerPopup(x, y, forceResend: true);
                                        if (s_FrontendPopupSent)
                                        {
                                            TryDismissWin32TrackPopupMenu();
                                            s_PendingNativePopupCloseFrames = Math.Max(
                                                s_PendingNativePopupCloseFrames, 60);
                                            ClearContextMenuForwardState();
                                        }
                                    }
                                    else
                                    {
                                        LogVerbose($"[NWB-ContextMenu] Mousedown: no new popup after SendEvent (postMCSig={postMCSig} == prevMCSig? {postMCSig == prevMCSig})");
                                    }
                                }

                                // Path 5: DisplayPopupMenu triggered by left-click
                                // mousedown. EditorGUI.DropdownButton fires on MouseDown
                                // and calls DisplayPopupMenu which only QUEUES
                                // gDelayedContextMenu in C++. The actual TrackPopupMenuEx
                                // fires on the NEXT GUIView::OnInputEvent (Repaint/Layout).
                                // Proactively trigger ShowDelayedContextMenu by sending a
                                // protected Layout event, dismiss the native popup, and
                                // forward menu items to the frontend.
                                if (isDisplayPopupTargetDown && !s_FrontendPopupSent)
                                {
                                    bool popupFound = ProbeAndDismissDelayedContextMenu();
                                    LogVerbose($"[NWB-DisplayPopup] Post-mousedown probe: popupFound={popupFound} prefix='{displayPrefixDown}'");
                                    if (popupFound)
                                    {
                                        LogVerbose($"[NWB-DisplayPopup] Delayed popup dismissed, forwarding '{displayPrefixDown}' menu");
                                        s_SuppressTargetRepaintWhilePopupOpen = true;
                                        TryForwardDisplayPopupMenu(x, y, displayPrefixDown);
                                        if (!s_FrontendPopupSent)
                                            s_SuppressTargetRepaintWhilePopupOpen = false;
                                    }
                                }
#endif
                                // If SendEvent(MouseDown) was consumed AND changed
                                // hotControl, an IMGUI overlay (e.g. OrientationGizmo)
                                // already handled the click via Handles.Button. Do NOT
                                // inject picking commands or activate motion tools —
                                // those would clear the overlay's hotControl and steal
                                // the interaction, causing the overlay click to fail
                                // silently on the subsequent MouseUp.
                                bool overlayHandledClick = consumed && GUIUtility.hotControl != 0
                                    && GUIUtility.hotControl != hotBefore;
                                s_OverlayConsumedMouseDown = overlayHandledClick;

                                // SceneView click selection uses RectSelection shortcuts to
                                // dispatch "SceneViewPickingEventCommand". In offscreen mode
                                // focusedWindow can be null, so shortcut contexts may not fire.
                                // Dispatch the same command directly after MouseDown.
                                if (isSceneViewTarget && button == 0 && (mods & EventModifiers.Alt) == 0
                                    && !overlayHandledClick
                                    && !IsInDockTitleBarRegion(activeMousePos.y))
                                {
                                    TryDispatchSceneViewPickingCommand(activeMousePos);
                                }

                                // Activate SceneView motion tool (FPS/Orbit/Pan/Zoom)
                                // for right-click, middle-click, or Alt+click. In offscreen
                                // mode, ShortcutManager ClutchShortcut contexts don't fire,
                                // so we manually set the view tool and hotControl here.
                                // Skip if the click is in the DockArea tab header —
                                // tab header right-clicks should show the DockArea
                                // context menu, not activate FPS/Orbit/Zoom.
                                bool isInTabHeader = IsInDockTitleBarRegion(activeMousePos.y);
                                if (isSceneViewTarget && !overlayHandledClick && !isInTabHeader)
                                {
                                    // Convert browser button to Unity button before SceneView tool
                                    // resolution: browser 2=right → Unity 1=right, browser 1=middle → Unity 2=middle.
                                    TryActivateSceneViewMotionTool(BrowserBtnToUnityBtn(button), mods, activeMousePos.x, activeMousePos.y);
                                }

                                // Path 1: Scan for popup ContainerWindow created
                                // by this mousedown (showMode=1 — AnnotationWindow,
                                // PopupWindow, FlexibleMenu, etc.).
                                if (!s_FrontendPopupSent)
                                    TryImmediatePopupIntercept(ppp);

                                // Path 2: DoPopup queued a delayed popup
                                // (PopupCallbackInfo.instance was set, but native
                                // popup failed in offscreen mode).
                                if (!s_FrontendPopupSent)
                                {
                                    TryInterceptDelayedPopup(prevPopupCbInstance, x, y, ppp);
                                }

                                // Path 3: GenericMenu.DropDown wrote to MenuController
                                // but no PopupCallbackInfo was created and no native
                                // popup window appeared (offscreen suppression).
                                // Detect by comparing MenuController signature.
                                if (!s_FrontendPopupSent)
                                {
                                    TryInterceptGenericMenuPopup(prevMCSig, x, y, ppp);
                                }

                                viaEditorWindow = true;
                            }
                        }
                        else if (type == "mouseup")
                        {
                            s_MouseDownInTabBar = false;

                            if (s_FrontendPopupSent)
                            {
                                if (s_ActionEventCount <= 10)
                                    LogVerbose($"[NWB-PopupGuard] Skipped mouseup ({x:F0},{y:F0}) — frontend popup active");
                                viaEditorWindow = false;
                                s_PaneOptionsMouseDownPending = false;
                            }
                            else if (button == 0 &&
                                (s_PaneOptionsMouseDownPending ||
                                 IsPaneOptionsClick(activeMousePos.x, activeMousePos.y, maxW)))
                            {
                                if (TryInterceptPaneOptionsClick(activeMousePos.x, activeMousePos.y, maxW, ppp))
                                {
                                    // PaneOptions (⋮): menu sent to browser without GenericMenu.DropDown.
                                    viaEditorWindow = true;
                                }
                                else
                                {
                                    // Do not SendEvent — PopupGenericMenu would open native menu.
                                    s_PaneOptionsMouseDownPending = false;
                                    CodelyLogger.LogWarning(
                                        $"[NWB-PaneOptions] mouseup intercept failed at ({activeMousePos.x:F0},{activeMousePos.y:F0})");
                                    viaEditorWindow = true;
                                }
                            }
                            else if (isGamePlayMode
                                && IsInGamePlayArea(gameRawY, ppp))
                            {
                                // GameView Play mode (game area only): IMGUI queue
                                // + direct uGUI release. Toolbar clicks fall through.
#if UNITY_EDITOR_WIN
                                IntPtr focusMouse = s_OffscreenTargetGUIViewHwnd != IntPtr.Zero
                                    ? s_OffscreenTargetGUIViewHwnd
                                    : s_OffscreenTargetContainerHwnd;
                                if (focusMouse != IntPtr.Zero)
                                {
                                    SetFocus(focusMouse);
                                    RestoreGameCameraAspects();
                                    RestoreCanvasLayout();
                                    s_WinPlayKeyLastFocusTime = EditorApplication.timeSinceStartup;
                                    s_RawInputSinkLastRegTime = 0;
                                    EnsureRawInputSinkRegistered();
                                }
#endif
                                // Path A: queue IMGUI event
                                QueuePlayModeMouseEvent(EventType.MouseUp, gameRawX, gameRawY, button, mods);

                                // Path B: complete the uGUI press→click sequence
                                InjectStreamingUIRelease(gameRawX, gameRawY, button);

#if UNITY_EDITOR_WIN
                                // Path C: native Win32 mouse button release
                                // (mirrors mousedown Path C).
                                if (s_CompositeSlots == null || s_CompositeSlots.Count == 0)
                                    PostNativeMouseButtonMessage(false, button, (int)rawX, (int)rawY);

                                // Path D (new Input System): release mouse button in InputSystem.
                                {
                                    float pppD = EditorGUIUtility.pixelsPerPoint;
                                    float sx = RawPixelToScreenX(gameRawX);
                                    float sy = RawPixelToScreenY(gameRawY, pppD);
                                    QueueInputSystemMouseState(new Vector2(sx, sy), button, false);
                                }
#elif UNITY_EDITOR_OSX
                                // Path C (macOS): native CGEvent mouse button release
                                // (mirrors mousedown Path C). Only in locked mode.
                                if (global::UnityEngine.Cursor.lockState == CursorLockMode.Locked)
                                {
                                    PostNativeCGMouseButtonEvent(false, button);
                                }
#endif

                                if (s_ActionEventCount <= 10)
                                {
                                    float pppDbg = EditorGUIUtility.pixelsPerPoint;
                                    float gpY = gameRawY - GetGameViewOffsetPixels(pppDbg);
                                    LogVerbose($"[NWB-PlayMouse] mouseup QUEUED+UI: " +
                                        $"rawPx=({rawX},{rawY}) gameRaw=({gameRawX:F0},{gameRawY:F0}) gamePx=({gameRawX:F0},{gpY:F0}) " +
                                        $"btn={button}");
                                }
                                viaEditorWindow = true;
                            }
                            else if (button == 0 &&
                                TryCompleteStreamingTabDragAsAddTab(s_LastInputScreenPoint))
                            {
                                viaEditorWindow = true;
                            }
                            else
                            {
                                // Snapshot pre-mouseup state for popup detection.
                                // UITK toolbar dropdown buttons (Grid, Snap, etc.)
                                // fire GenericMenu.DropDown on mouseup, so Paths 2/3
                                // must also run after mouseup — not just mousedown.
                                object prevPopupCbUp = GetPopupCallbackInfoInstance();
                                string prevMCSigUp = GetMenuControllerSignature();

                                // Right-click mouseup: the implicit Layout inside
                                // SendEvent(mouseup) triggers ConsoleWindow's
                                // m_RightClickedDelayFrame→0 → GenericMenu.ShowAsContext
                                // → gDelayedContextMenu SET. Protect with auto-dismiss
                                // so any resulting TrackPopupMenuEx returns immediately.
                                bool mouseupAutoDismiss = s_ExpectContextMenuForward
                                    && IsBrowserRightClickButton(button);
                                if (mouseupAutoDismiss)
                                    ScheduleNativePopupAutoDismiss();

                                int hotBeforeUp = GUIUtility.hotControl;
                                UnityEngine.Object selBeforeUp = Selection.activeObject;
                                Event sendEvt = new Event(evt);
                                bool consumedUp = s_OffscreenTarget.SendEvent(sendEvt);
                                UnityEngine.Object selAfterUp = Selection.activeObject;

                                if (mouseupAutoDismiss)
                                    CancelNativePopupAutoDismiss();

                                // Immediately notify all non-input-target composite
                                // windows (e.g. Inspector) when selection changes.
                                // This must happen HERE (in PollAndInjectInput) rather
                                // than in CaptureAndPushCompositeFrame because capture
                                // runs BEFORE input injection each frame, creating a
                                // one-frame lag that prevents NotifySelectionChangeIfNeeded
                                // in the capture loop from working reliably.
                                if (selBeforeUp != selAfterUp && s_CompositeActive)
                                {
                                    NotifyCompositeNonTargetWindows(selAfterUp);
                                }

#if UNITY_EDITOR_WIN
                                // Fix: same aspect corruption as mousedown
                                if (isGameViewTarget)
                                    RestoreGameCameraAspects();
#endif
                                // Only synthesize ContextClick when the corresponding
                                // mousedown was actually forwarded to the EditorWindow
                                // (s_ExpectContextMenuForward is set during mousedown).
                                // If the mousedown was swallowed by PopupGuard,
                                // ConsoleWindow's m_RightClickedDelayFrame was never set,
                                // so ContextClick would produce zero items and the FAILED
                                // path would needlessly start a 15-second popup guard.
                                if (IsBrowserRightClickButton(button) && s_ExpectContextMenuForward)
                                    TrySynthesizeAndForwardContextMenuPopup(
                                        x, y, mods, activeMousePos, activeMouseDelta);

                                if (!s_FrontendPopupSent)
                                    TryImmediatePopupIntercept(ppp);

                                // Path 2 after mouseup: delayed popup from DoPopup.
                                if (!s_FrontendPopupSent)
                                    TryInterceptDelayedPopup(prevPopupCbUp, x, y, ppp);

                                // Path 3 after mouseup: GenericMenu.DropDown wrote
                                // to MenuController (e.g. Grid/Snap toolbar buttons
                                // and Project/Hierarchy right-click menus).
                                if (!s_FrontendPopupSent)
                                    TryInterceptGenericMenuPopup(prevMCSigUp, x, y, ppp);

                                // Path 4: UITK toolbar dropdown buttons (e.g.
                                // Center/Pivot, Global/Local in SceneView).
                                if (!s_FrontendPopupSent)
                                    TryInterceptUITKToolbarClick(x, y, ppp);

                                bool isTabHeaderRightClick = IsBrowserRightClickButton(button) &&
                                    s_TabBarOffsetY > 0 && activeMousePos.y <= s_TabBarOffsetY;
                                if (IsBrowserRightClickButton(button) &&
                                    (ShouldSynthesizeContextClickForTarget() || isTabHeaderRightClick))
                                {
                                    if (!s_FrontendPopupSent)
                                    {
                                        CancelNativeDelayedPopup();
                                        s_SuppressTargetRepaintWhilePopupOpen = false;
                                        CodelyLogger.LogWarning($"[NWB-ContextMenu] No frontend popup after context click on {s_OffscreenTargetType?.Name}");
                                    }
                                    ClearContextMenuForwardState();
                                }

                                viaEditorWindow = true;
                            }
                        }
                        else
                        {
                            // If SceneView motion tool wasn't activated on mousedown
                            // (e.g., toolbar click detected later), retry on first drag.
                            // Skip if an overlay already consumed the mousedown —
                            // the overlay is handling the drag interaction.
                            if (type == "mousedrag" && isSceneViewTarget && !s_OffscreenScenePanLocked
                                && !s_OverlayConsumedMouseDown
                                && !(s_TabBarOffsetY > 0 && activeMousePos.y < s_TabBarOffsetY))
                            {
                                TryActivateSceneViewMotionTool(BrowserBtnToUnityBtn(button), mods, activeMousePos.x, activeMousePos.y);
                            }

                            // GameView Play mode: for mousemove/mousedrag, bypass
                            // SendEvent and use QueueGameViewInputEvent directly.
                            // SendEvent→GameView.OnGUI also calls QueueGameViewInputEvent
                            // but with potentially wrong coordinates from the offscreen
                            // coordinate space, causing double/conflicting events.
                            bool skipSendEvent = isGamePlayMode
                                && (type == "mousemove" || type == "mousedrag");

                            // Version-aware tab-drag guard:
                            // - Unity 2019 + Gamma fallback has historical DragTab
                            //   instability/crash risk. Keep the guard there.
                            // - Newer versions keep MouseDrag enabled so users can
                            //   split tabs to right/bottom via native docking.
                            if (!skipSendEvent && type == "mousedrag"
                                && s_MouseDownInTabBar
                                && s_UseRenderTextureFallback
                                && IsUnity2019Editor)
                            {
                                skipSendEvent = true;
                                if (!s_LoggedUnity2019TabDragGuard)
                                {
                                    s_LoggedUnity2019TabDragGuard = true;
                                    CodelyLogger.Log("[NWB-Composite] Unity 2019 tab-drag guard active in Gamma fallback");
                                }
                            }
                            bool used = false;
                            if (!skipSendEvent)
                            {
                                Event sendEvt = new Event(evt);
                                used = s_OffscreenTarget.SendEvent(sendEvt);
                            }

#if UNITY_EDITOR_WIN
                            // Fix: every SendEvent calls UpdateScreenManager which can
                            // corrupt camera aspect via OnWindowSizeHasChanged
                            if (isGameViewTarget && !skipSendEvent)
                                RestoreGameCameraAspects();

                            // GameView Play mode: set focus to the offscreen GUIView
                            // and post native WM_KEYUP so the Input Manager releases
                            // the key state for Input.GetKey.
                            if (isGamePlayMode && type == "keyup")
                            {
                                IntPtr focusTargetUp = s_OffscreenTargetGUIViewHwnd != IntPtr.Zero
                                    ? s_OffscreenTargetGUIViewHwnd
                                    : s_OffscreenTargetContainerHwnd;
                                if (focusTargetUp != IntPtr.Zero)
                                {
                                    SetFocus(focusTargetUp);
                                    s_WinPlayKeyLastFocusTime = EditorApplication.timeSinceStartup;
                                    s_RawInputSinkLastRegTime = 0;
                                    EnsureRawInputSinkRegistered();
                                }
                                PostNativeKeyMessage(false, keyForCommand);
                                s_WinHeldKeys.Remove(keyForCommand);
                                // Also update the new Input System with the released key state.
                                QueueInputSystemKeyboardState();
                            }

                            // GameView Play mode: queue mousemove/drag for IMGUI.
                            // For LOCAL streaming, RIDEV_INPUTSINK delivers hardware
                            // WM_INPUT → Input.GetAxisRaw works directly.
                            // For REMOTE streaming (detected via ICE candidate-pair),
                            // hardware mouse is on a different machine so we inject
                            // delta via SendInput + RIDEV_INPUTSINK.
                            if (isGamePlayMode && (type == "mousemove" || type == "mousedrag"))
                            {
                                EventType moveType = (type == "mousedrag")
                                    ? EventType.MouseDrag : EventType.MouseMove;
                                QueuePlayModeMouseEvent(moveType, gameRawX, gameRawY, button, mods, activeMouseDelta);

                                // Path B (uGUI): inject OnDrag for virtual joysticks
                                // and other IDragHandler-based UI controls. Only fires
                                // when a drag target was grabbed on mousedown.
                                if (type == "mousedrag")
                                    InjectStreamingUIDrag(gameRawX, gameRawY, button);

                                // Path D (new Input System): update Mouse.current.position so
                                // InputAction-based drag detection (touchPosition.ReadValue)
                                // tracks the cursor during move/drag. Button state is preserved
                                // from the last mousedown/mouseup (browserButton=-1 = no change).
                                {
                                    float pppD = EditorGUIUtility.pixelsPerPoint;
                                    float sx = RawPixelToScreenX(gameRawX);
                                    float sy = RawPixelToScreenY(gameRawY, pppD);
                                    QueueInputSystemMouseState(new Vector2(sx, sy), -1, false);
                                }

                                // Remote delta injection is now handled by the
                                // dedicated "mousedelta" message from the frontend,
                                // which provides raw movementX/Y from the browser.
                                // This avoids Y-axis confusion between IMGUI coords
                                // (bottom-left origin) and browser screen coords
                                // (top-left origin, matching SendInput convention).
                            }
#elif UNITY_EDITOR_OSX
                            // Post CGEvent keyup and remove from the DataChannel-based
                            // held set. This path is reached when the browser has focus
                            // (key tap or key release after deactivation timeout).
                            if (isGamePlayMode && type == "keyup")
                            {
                                PostNativeCGKeyEvent(false, keyForCommand);
                                s_MacHeldKeys.Remove(keyForCommand);
                                s_MacPlayKeyLastTime = EditorApplication.timeSinceStartup;
                            }

                            // GameView Play mode: queue mousemove/drag for IMGUI so
                            // the game runtime receives mouse position updates via
                            // EditorGUIUtility.QueueGameViewInputEvent.
                            if (isGamePlayMode && (type == "mousemove" || type == "mousedrag"))
                            {
                                EventType moveType = (type == "mousedrag")
                                    ? EventType.MouseDrag : EventType.MouseMove;
                                QueuePlayModeMouseEvent(moveType, gameRawX, gameRawY, button, mods, activeMouseDelta);

                                // Path B (uGUI): inject OnDrag for virtual joysticks
                                if (type == "mousedrag")
                                    InjectStreamingUIDrag(gameRawX, gameRawY, button);

                                // Inject native CGEvent mouse movement ONLY when
                                // cursor is locked (FPS-style camera rotation).
                                // Input.GetAxisRaw("Mouse X/Y") needs native delta
                                // in locked mode. In unlocked mode, QueuePlayModeMouseEvent
                                // above is sufficient for Input.mousePosition.
                                // CRITICAL: posting CGEventMouseMoved in unlocked mode
                                // teleports the real macOS cursor to the offscreen window
                                // center, causing the mouse to "leave" the browser iframe
                                // and preventing all further interaction.
                                if (activeMouseDelta.sqrMagnitude > 0.01f
                                    && global::UnityEngine.Cursor.lockState == CursorLockMode.Locked)
                                {
                                    PostNativeCGMouseMoveEvent(
                                        activeMouseDelta.x, activeMouseDelta.y, button);
                                }
                            }
#endif
                            viaEditorWindow = true;
                        }
                    }

                    // Query backend tooltip after mouse move events so SceneView/GameView
                    // toolbar hints (and other backend-provided tooltips) are forwarded
                    // to the browser in near real-time.
                    if (type == "mousemove" || type == "mousedrag")
                    {
                        // During SceneView FPS/orbit/pan drag, tooltip probing adds
                        // reflection/layout cost but provides no user value.
                        bool skipTooltipProbe = isSceneViewTarget && s_OffscreenScenePanLocked;
                        if (!skipTooltipProbe)
                        {
                            if (TryQueryTooltipAtMouse(activeMousePos, out string hoverTip))
                            {
                                SendFrontendTooltip(hoverTip);
                            }
                            else if (isGameViewTarget && TryGetGameViewToolbarTooltip(activeMousePos, out string gameViewTip))
                            {
                                // GameView IMGUI tooltips cannot be read via GUI.tooltip
                                // because OnGUIState.EndOnGUI() clears m_MouseTooltip.
                                // Use direct GUIContent reflection + geometry matching.
                                SendFrontendTooltip(gameViewTip);
                            }
                            else
                            {
                                SendFrontendTooltip("");
                            }
                        }

                        UpdateEditorCursorFromInput(type, activeMousePos);
                    }
                    else if (type == "keydown" && !isGamePlayMode)
                    {
                        UpdateEditorCursorFromInput(type, activeMousePos);
                    }
                    else if (type == "mousedown" || type == "mouseup" || type == "wheel")
                    {
                        SendFrontendTooltip("");
                    }

                    if (type == "mousedown") s_RemoteMouseButtonDown = true;

                    // For mousedown/mouseup, force a synchronous Repaint event
                    // while focus override is still active. Some IMGUI controls
                    // (e.g. EditorGUI.Popup → GenericMenu.DropDown) only create
                    // the popup ContainerWindow during a Repaint pass, not during
                    // the initial MouseDown SendEvent. Without this, the popup
                    // never gets created in offscreen mode because the async
                    // Repaint() runs after RestoreKeyFocusAfterOffscreen removes
                    // the hasFocus override.
                    //
                    // IMPORTANT: Skip this for GameView clicks in the game area
                    // (below toolbar). On Windows, SendEvent(Repaint) triggers
                    // ForceRepaint→DoPaint→Present which activates the Unity
                    // window, causing a persistent foreground reclamation cascade.
                    // GameView's game area doesn't produce IMGUI popups so the
                    // Repaint is unnecessary. Only toolbar clicks (y < toolbar
                    // height) need the popup creation path.
                    bool isTabHeaderMouseUp = type == "mouseup" && IsBrowserRightClickButton(button) &&
                        IsInDockTitleBarRegion(activeMousePos.y);
                    bool isContextMenuMouseUp = type == "mouseup" && IsBrowserRightClickButton(button) &&
                        (ShouldSynthesizeContextClickForTarget() || isTabHeaderMouseUp);
                    if ((type == "mousedown" || type == "mouseup")
                        && !ShouldBlockNativeContextMenuRepaint()
                        && !isContextMenuMouseUp)
                    {
                        bool isGameAreaClick = isGameViewTarget && activeMousePos.y > s_TabBarOffsetY + 21;
#if UNITY_EDITOR_WIN
                        if (!isGameAreaClick)
#endif
                        {
                            // Force a synchronous Layout+Repaint cycle so IMGUI
                            // controls that create popups during Repaint (not
                            // MouseDown) can produce the ContainerWindow now.
                            try
                            {
                                Event layoutEvt = new Event { type = EventType.Layout, mousePosition = activeMousePos };
                                s_OffscreenTarget.SendEvent(layoutEvt);
                                Event repaintEvt = new Event { type = EventType.Repaint, mousePosition = activeMousePos };
                                s_OffscreenTarget.SendEvent(repaintEvt);
#if UNITY_EDITOR_WIN
                                // Fix: synchronous Repaint → DoPaint → UpdateScreenManager
                                // also corrupts camera aspect
                                if (isGameViewTarget)
                                    RestoreGameCameraAspects();
#endif
                                if (!isGamePlayMode)
                                    UpdateEditorCursorFromInput(type, activeMousePos);
                            }
                            catch (Exception) { /* Repaint may throw in some contexts */ }
                        }
                        if (!s_FrontendPopupSent)
                            TryImmediatePopupIntercept(ppp);
                    }

                    // Restore original key-focus delegate and key window state before
                    // async Repaint to avoid side effects on other windows.
                    RestoreKeyFocusAfterOffscreen();

                    if (!ShouldBlockNativeContextMenuRepaint())
                        s_OffscreenTarget.Repaint();

                    // Track mouse button state for drag-aware deactivation.
                    if (type == "mouseup")
                    {
                        s_RemoteMouseButtonDown = false;
                        s_CompositeMouseCaptureView = null;
                        s_CompositeMouseCaptureSlot = null;
                        s_OverlayConsumedMouseDown = false;
                        if (isSceneViewTarget && s_OffscreenScenePanLocked)
                            TryCompleteSceneViewPanLock();
                        else if (s_OffscreenScenePanLocked)
                        {
                            // Pan was locked but current target is not SceneView —
                            // this indicates a composite slot mismatch.
                            s_OffscreenScenePanLocked = false;
                            if (GUIUtility.hotControl != 0) GUIUtility.hotControl = 0;
                        }
                        if (!isGamePlayMode)
                            UpdateEditorCursorFromInput(type, activeMousePos);
                    }

                    // Window transparency strategy:
                    // All windows are alpha=0 + ignoresMouseEvents=YES (set in
                    // StartOffscreenCapture). Focus() during mousedown activates
                    // Unity and orders windows front, but they stay transparent.
                    // Re-apply transparency after every mouse event in case some
                    // code path reset it. On Windows, mouseup deactivates Unity
                    // so the local browser regains activation. On macOS, doing
                    // that makes the next remote click become an app-activation
                    // click, so keep Unity active between remote mouse actions.
                    if (type == "mousedown" || type == "mouseup" || type == "mousedrag")
                    {
                        ReapplyWindowTransparency();
                    }
                    if (type == "mouseup" && !s_RemoteMouseButtonDown)
                    {
#if UNITY_EDITOR_WIN
                        // Flush all held key states before deactivating.
                        // If the mask-to-front cycle (SetForegroundWindow in
                        // EnsureWindowsStayHiddenDuringOffscreen) interrupts
                        // WM_KEYUP processing, the Input Manager retains
                        // stale pressed states causing ghost movement.
                        if (s_WinHeldKeys.Count > 0)
                        {
                            foreach (var key in s_WinHeldKeys)
                                PostNativeKeyMessage(false, key);
                            s_WinHeldKeys.Clear();
                        }
                        // Flush held mouse buttons to avoid ghost-held state.
                        // Copy to array first because PostNativeMouseButtonMessage
                        // mutates s_WinHeldMouseButtons during the loop.
                        if (s_WinHeldMouseButtons.Count > 0)
                        {
                            var heldCopy = new int[s_WinHeldMouseButtons.Count];
                            s_WinHeldMouseButtons.CopyTo(heldCopy);
                            foreach (var btn in heldCopy)
                                PostNativeMouseButtonMessage(false, btn);
                            s_WinHeldMouseButtons.Clear();
                            ClearInputSystemMouseState();
                        }
                        // Skip deactivation for GameView Play mode: the game runtime
                        // needs the window to retain focus so the Input Manager can
                        // process queued mouse events (Input.mousePosition,
                        // Input.GetMouseButton) during the next game Update frame.
                        // Without focus, the GameView won't forward mouse state to
                        // the runtime and uGUI EventSystem clicks won't register.
                        //
                        // Also skip for remote connections: without a local browser
                        // to hold the foreground, DeactivateUnityKeepWindows() sends
                        // WM_ACTIVATE(WA_INACTIVE) via SetWindowPos, disrupting IMGUI
                        // state for the next mousedown (e.g. tree view expansion).
                        if (!isGamePlayMode && !s_IsRemoteConnection)
                            DeactivateUnityKeepWindows();
#elif UNITY_EDITOR_OSX
                        // Flush all held key states while Unity is still the
                        // active app so they reach Unity's Input Manager.
                        if (s_MacHeldKeys.Count > 0)
                        {
                            foreach (var key in s_MacHeldKeys)
                                PostNativeCGKeyEvent(false, key);
                            s_MacHeldKeys.Clear();
                        }
                        // Flush held mouse buttons to avoid ghost-held state.
                        if (s_MacHeldMouseButtons.Count > 0)
                        {
                            var heldCopy = new int[s_MacHeldMouseButtons.Count];
                            s_MacHeldMouseButtons.CopyTo(heldCopy);
                            foreach (var btn in heldCopy)
                                PostNativeCGMouseButtonEvent(false, btn);
                            s_MacHeldMouseButtons.Clear();
                        }
                        s_MacPlayKeyboardActive = false;
                        s_MacKeyReinjectionFrames = 0;
                        // Keep Unity active after mouse interactions. Otherwise
                        // macOS returns focus to the previously active app after
                        // every mouseup, and the next remote click is swallowed as
                        // a window activation click instead of reaching Unity.
#endif
                    }

                    s_InputEventCount++;
                    bool isAction = (type != "mousemove" && type != "mousedrag");
                    if (isAction) s_ActionEventCount++;
                    if (IsMouseEvent(type))
                    {
                        s_LastMousePos = activeMousePos;
                        s_HasLastMousePos = true;
                    }
                    if (s_InputEventCount <= 10 || (isAction && s_ActionEventCount <= 10))
                    {
                        string extra = "";
                        if (type == "mousedown" || type == "mouseup")
                            extra = $" rawPx=({rawX},{rawY}) pt=({x:F1},{y:F1}) maxPt=({maxW:F0},{maxH:F0}) ppp={ppp} clickCount={clickCount}(fe={frontendClickCount}) kbCtrl={GUIUtility.keyboardControl} editTF={EditorGUIUtility.editingTextField}";
                        else if (type == "keydown" || type == "keyup")
                            extra = $" key='{evt.keyCode}' ch={(int)evt.character}('{(evt.character > 31 ? evt.character : '?')}') mod={evt.modifiers} kbCtrl={GUIUtility.keyboardControl} editTF={EditorGUIUtility.editingTextField} target={s_OffscreenTargetType?.Name ?? "(null)"} playGame={isGamePlayMode} comp={s_CompositeActive}";
                        string target = sentToPopup ? " -> popup" : (viaEditorWindow ? " -> window" : "");
                        LogVerbose($"[NWB-Offscreen] Input #{s_InputEventCount}: {type} ({x:F1},{y:F1}) btn={button}{extra}{target}");
                    }
                }
            }
            catch (Exception ex)
            {
                RestoreKeyFocusAfterOffscreen();
                CodelyLogger.LogWarning($"[NWB-Offscreen] InjectInput error: {ex.Message}");
            }
        }

        private static bool IsMouseEvent(string type)
        {
            return type == "mousedown" || type == "mouseup" || type == "mousemove"
                || type == "mousedrag" || type == "wheel";
        }

        /// <summary>
        /// Return true when a right-button mouseup should be followed by a
        /// synthetic ContextClick. GameView and SceneView reserve right-click
        /// for play input and camera tools; other editor windows (Project,
        /// Hierarchy, Inspector, etc.) use ContextClick for context menus.
        /// </summary>
        private static bool ShouldSynthesizeContextClickForTarget()
        {
            if (s_OffscreenTargetType == null) return false;
            string targetName = s_OffscreenTargetType.Name;
            return targetName != "GameView" && targetName != "SceneView";
        }

        /// <summary>
        /// Browser MouseEvent.button: 0=left, 1=middle, 2=right.
        /// Context menus are triggered by right-click (button 2), not middle-click.
        /// </summary>
        private static bool IsBrowserRightClickButton(int browserButton)
        {
            return browserButton == 2;
        }

        /// <summary>
        /// Determine the SceneView motion tool based on mouse button and modifiers.
        /// Returns ViewTool.None if no view tool should be activated (e.g., left-click
        /// without modifiers selects objects, not the view tool).
        /// </summary>
        private static ViewTool DetermineSceneViewMotionTool(int unityButton, EventModifiers mods, bool is2D, bool isRotationLocked)
        {
            bool alt = (mods & EventModifiers.Alt) != 0;
            bool actionKey = (mods & EventModifiers.Command) != 0 || (mods & EventModifiers.Control) != 0;

            if (unityButton == 1) // Right mouse button
            {
                if (alt)
                    return ViewTool.Zoom;
                if (is2D)
                    return ViewTool.Pan;
                return ViewTool.FPS;
            }
            if (unityButton == 2) // Middle mouse button
            {
                return ViewTool.Pan;
            }
            if (unityButton == 0 && alt) // Alt + Left click
            {
                if (is2D)
                    return ViewTool.Pan;
                if (actionKey)
                    return ViewTool.Pan;
                if (isRotationLocked)
                    return ViewTool.Pan;
                return ViewTool.Orbit;
            }
            if (unityButton == 0 && UnityEditor.Tools.current == Tool.View)
            {
                return ViewTool.Pan;
            }
            return ViewTool.None;
        }

        /// <summary>
        /// Activate a SceneView motion tool (FPS/Pan/Orbit/Zoom) in offscreen mode.
        /// In offscreen mode, ShortcutManager's ClutchShortcut contexts do not activate
        /// because focusedWindow/viewportsUnderMouse are not guaranteed. This helper
        /// manually emulates the shortcut path by setting Tools.viewTool/s_LockedViewTool
        /// and dispatching SetSceneViewMotionHotControlEventCommand.
        /// </summary>
        // Check whether a VisualElement or any of its ancestors belongs to
        // the SceneView UITK toolbar (buttons, dropdowns, toggles, etc.).
        // Used to avoid activating SceneView motion tools when the user
        // clicks on toolbar UI rather than the scene viewport.
        private static bool IsUITKToolbarElement(UnityEngine.UIElements.VisualElement el)
        {
            while (el != null)
            {
                string tn = el.GetType().Name;
                if (tn.Contains("Toolbar") || tn.Contains("ToolStrip"))
                    return true;
                // Also check base types for EditorToolbarDropdown / Toggle / Button
                var bt = el.GetType().BaseType;
                while (bt != null && bt != typeof(object))
                {
                    string bn = bt.Name;
                    if (bn.Contains("Toolbar"))
                        return true;
                    bt = bt.BaseType;
                }
                el = el.parent;
            }
            return false;
        }

        // Prefer invoking SceneView's native motion-tool pipeline so internal
        // zoom state (m_StartZoom/m_ZoomSpeed/m_TotalMotion) is initialized exactly
        // like a real ClutchShortcut path.
        private static bool TryStartSceneViewMotionViaReflection(SceneView sceneView, ViewTool targetTool)
        {
            if (sceneView == null)
                return false;
            try
            {
                FieldInfo motionField = typeof(SceneView).GetField(
                    "m_SceneViewMotion",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (motionField == null)
                    return false;
                object motionObj = motionField.GetValue(sceneView);
                if (motionObj == null)
                    return false;
                MethodInfo startMethod = motionObj.GetType().GetMethod(
                    "StartSceneViewMotionTool",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (startMethod == null)
                    return false;
                startMethod.Invoke(motionObj, new object[] { targetTool, sceneView });
                return true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-SceneViewTool] reflection start failed: {ex.Message}");
                return false;
            }
        }

        // Mirror SceneView's normal release path to restore EditorGUIUtility
        // mouse jumping and locked tool state safely.
        private static bool TryCompleteSceneViewMotionViaReflection(SceneView sceneView)
        {
            if (sceneView == null)
                return false;
            try
            {
                FieldInfo motionField = typeof(SceneView).GetField(
                    "m_SceneViewMotion",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (motionField == null)
                    return false;
                object motionObj = motionField.GetValue(sceneView);
                if (motionObj == null)
                    return false;
                MethodInfo completeMethod = motionObj.GetType().GetMethod(
                    "CompleteSceneViewMotionTool",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (completeMethod == null)
                    return false;
                completeMethod.Invoke(motionObj, null);
                return true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-SceneViewTool] reflection complete failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// In composite mode, ensure the DockArea's selected tab matches
        /// s_OffscreenTarget. Internal_SendEvent dispatches through the
        /// DockArea's currently-selected EditorWindow's visual tree —
        /// overlays (e.g. SceneOrientationGizmo) live in that tree and
        /// only receive events when their window is the active tab.
        /// Without this, clicks on SceneView overlays are silently
        /// swallowed by the GameView tab if it happens to be selected.
        /// </summary>
        private static void EnsureCompositeTabForTarget()
        {
            if (!s_CompositeActive) return;
            if (s_OffscreenTarget == null) return;

            try
            {
                FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (parentField == null) return;

                object dockArea = parentField.GetValue(s_OffscreenTarget);
                if (dockArea == null) return;

                System.Type dockAreaType = dockArea.GetType();
                if (dockAreaType.Name != "DockArea")
                {
                    s_CompositeLastTabTarget = s_OffscreenTarget;
                    return;
                }

                FieldInfo panesField = dockAreaType.GetField("m_Panes",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (panesField == null) return;

                var panes = panesField.GetValue(dockArea) as System.Collections.IList;
                if (panes == null || panes.Count <= 1)
                {
                    s_CompositeLastTabTarget = s_OffscreenTarget;
                    return;
                }

                int targetIndex = -1;
                for (int i = 0; i < panes.Count; i++)
                {
                    if ((panes[i] as UnityEngine.Object) == s_OffscreenTarget)
                    { targetIndex = i; break; }
                }
                if (targetIndex < 0)
                {
                    s_CompositeLastTabTarget = s_OffscreenTarget;
                    return;
                }

                PropertyInfo selectedProp = dockAreaType.GetProperty("selected",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (selectedProp != null)
                {
                    int cur = (int)selectedProp.GetValue(dockArea);
                    if (cur != targetIndex)
                    {
                        selectedProp.SetValue(dockArea, targetIndex);
                        LogVerbose($"[NWB-CompositeTab] Switched DockArea tab {cur}→{targetIndex} for {s_OffscreenTargetType?.Name}");
                    }
                }
                else
                {
                    FieldInfo selectedField = dockAreaType.GetField("m_Selected",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (selectedField != null)
                    {
                        int cur = (int)selectedField.GetValue(dockArea);
                        if (cur != targetIndex)
                        {
                            selectedField.SetValue(dockArea, targetIndex);
                            LogVerbose($"[NWB-CompositeTab] Switched DockArea tab {cur}→{targetIndex} for {s_OffscreenTargetType?.Name} (field)");
                        }
                    }
                }

                s_CompositeLastTabTarget = s_OffscreenTarget;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-CompositeTab] Tab switch failed: {ex.Message}");
            }
        }

        // Cached reflection for SceneView.lastActiveSceneView backing field.
        // The property has no public setter, so we set the internal field directly.
        private static FieldInfo s_LastActiveSceneViewField;
        private static bool s_LastActiveSceneViewFieldResolved;

        /// <summary>
        /// Ensure SceneView.lastActiveSceneView points to the given SceneView.
        /// In composite mode the target switches via hit-test without calling
        /// SceneView.OnFocus(), so lastActiveSceneView can remain null or stale.
        /// Unity's SceneViewMotion.HandleMouseDrag checks lastActiveSceneView
        /// internally — if it doesn't match, FPS/Orbit rotation is silently
        /// skipped while Pan still works (Pan bypasses the check).
        /// </summary>
        private static void EnsureLastActiveSceneView(SceneView sv)
        {
            if (sv == null) return;
            if (SceneView.lastActiveSceneView == sv) return;

            if (!s_LastActiveSceneViewFieldResolved)
            {
                s_LastActiveSceneViewFieldResolved = true;
                s_LastActiveSceneViewField = typeof(SceneView).GetField(
                    "s_LastActiveSceneView",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (s_LastActiveSceneViewField == null)
                {
                    // Try alternative field name used in some Tuanjie versions
                    s_LastActiveSceneViewField = typeof(SceneView).GetField(
                        "m_LastActiveSceneView",
                        BindingFlags.Static | BindingFlags.NonPublic);
                }
            }

            if (s_LastActiveSceneViewField != null)
            {
                s_LastActiveSceneViewField.SetValue(null, sv);
                LogVerbose($"[NWB-SceneViewTool] Set lastActiveSceneView={sv.GetStableInstanceId()} via reflection");
            }
            else
            {
                // Fallback: call Focus() which triggers OnFocus → lastActiveSceneView = this.
                // This may briefly flash the native window but
                // EnsureWindowsStayHiddenDuringOffscreen will re-hide it.
                sv.Focus();
                LogVerbose($"[NWB-SceneViewTool] Set lastActiveSceneView via Focus() fallback");
            }
        }

        private static void TryActivateSceneViewMotionTool(int button, EventModifiers mods,
            float mouseX = 0f, float mouseY = 0f)
        {
            if (s_OffscreenTarget == null || s_OffscreenTargetType == null || s_OffscreenTargetType.Name != "SceneView")
            {
                if (s_ActionEventCount <= 20)
                    LogVerbose($"[NWB-SceneViewTool] skip activate — target={s_OffscreenTargetType?.Name ?? "null"} btn={button}");
                return;
            }
            // In composite mode, hotControl from a previous SceneView motion
            // session may linger because CompleteSceneViewMotionTool does not
            // always reset it (the intermediate SendEvent+Repaint cycle can
            // re-acquire it). Force-clear stale hotControl so the next
            // right-click can activate FPS mode again.
            if (GUIUtility.hotControl != 0)
            {
                if (s_OffscreenScenePanLocked)
                {
                    return;
                }
                // Not pan-locked but hotControl is stale from previous motion — clear it
                GUIUtility.hotControl = 0;
            }
            if (s_OffscreenScenePanLocked) return;

            bool is2D = false;
            bool isRotationLocked = false;
            SceneView sv = s_OffscreenTarget as SceneView;
            try
            {
                if (sv != null)
                {
                    is2D = sv.in2DMode;
                    isRotationLocked = sv.isRotationLocked;
                }
            }
            catch (Exception) { }

            ViewTool targetTool = DetermineSceneViewMotionTool(button, mods, is2D, isRotationLocked);
            if (targetTool == ViewTool.None) return;

            // When the motion tool is triggered by plain left-click (no Alt)
            // because Tools.current == Tool.View, verify the click is NOT on
            // an interactive UITK toolbar element. Toolbar buttons like
            // Grid/Snap need the raw mousedown to fire their GenericMenu popup.
            if (button == 0 && (mods & EventModifiers.Alt) == 0)
            {
                try
                {
                    var root = s_OffscreenTarget.rootVisualElement;
                    if (root != null && root.panel != null)
                    {
                        var picked = root.panel.Pick(new Vector2(mouseX, mouseY));
                        if (picked != null && picked != root && IsUITKToolbarElement(picked))
                        {
                            if (s_ActionEventCount <= 10)
                                LogVerbose($"[NWB-SceneViewTool] Skip motion tool — UITK toolbar hit " +
                                          $"'{picked.GetType().Name}' at ({mouseX:F0},{mouseY:F0})");
                            return;
                        }
                    }
                }
                catch (Exception) { }
            }

            try
            {
                // In composite mode, SceneView.lastActiveSceneView may not
                // be set because the target was resolved via hit-test, not
                // through the normal Focus/OnFocus path. This must be fixed
                // BEFORE calling StartSceneViewMotionTool, because the
                // internal SceneViewMotion checks lastActiveSceneView when
                // processing FPS/Orbit drag — without a match, rotation is
                // silently ignored while pan still works.
                if (s_CompositeActive && sv != null)
                {
                    EnsureLastActiveSceneView(sv);
                }

                bool started = TryStartSceneViewMotionViaReflection(sv, targetTool);
                if (!started)
                {
                    // Fallback path for engine versions where reflection signatures differ.
                    FieldInfo lockedField = typeof(UnityEditor.Tools).GetField(
                        "s_LockedViewTool",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    if (lockedField != null)
                        lockedField.SetValue(null, targetTool);
                    UnityEditor.Tools.viewTool = targetTool;
                    s_OffscreenTarget.SendEvent(EditorGUIUtility.CommandEvent("SetSceneViewMotionHotControlEventCommand"));
                }
                s_OffscreenScenePanLocked = true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-SceneViewTool] activate failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Release the temporary SceneView motion tool lock at mouseup.
        /// </summary>
        private static void TryCompleteSceneViewPanLock()
        {
            try
            {
                int hotCtrlBefore = GUIUtility.hotControl;
                SceneView sv = s_OffscreenTarget as SceneView;
                bool completed = TryCompleteSceneViewMotionViaReflection(sv);
                if (!completed)
                {
                    if (GUIUtility.hotControl != 0)
                        GUIUtility.hotControl = 0;

                    FieldInfo lockedField = typeof(UnityEditor.Tools).GetField(
                        "s_LockedViewTool",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    if (lockedField != null)
                        lockedField.SetValue(null, ViewTool.None);
                    UnityEditor.Tools.viewTool = ViewTool.Pan;
                }
                // Always ensure hotControl is reset after completing a SceneView
                // motion tool. In composite mode the intermediate SendEvent+Repaint
                // cycle between mouseup and this call can re-acquire hotControl,
                // leaving it non-zero even after CompleteSceneViewMotionTool.
                // A stale hotControl blocks subsequent TryActivateSceneViewMotionTool
                // calls, making FPS/Pan/Orbit unusable after the first use.
                if (GUIUtility.hotControl != 0)
                {
                    LogVerbose($"[NWB-SceneViewTool] force-clearing hotControl={GUIUtility.hotControl} after motion completion (was {hotCtrlBefore} before)");
                    GUIUtility.hotControl = 0;
                }
                s_OffscreenScenePanLocked = false;
                if (s_ActionEventCount <= 20)
                {
                    string camInfo = "";
                    try
                    {
                        if (sv != null)
                        {
                            var cam = sv.camera;
                            camInfo = $" pivot={sv.pivot} size={sv.size:F2} rot={sv.rotation.eulerAngles}" +
                                      (cam != null ? $" camPos={cam.transform.position} fov={cam.fieldOfView:F1}" : "");
                        }
                    }
                    catch (Exception) { }
                    LogVerbose($"[NWB-SceneViewTool] released hotCtrlBefore={hotCtrlBefore} hotCtrlAfter={GUIUtility.hotControl} via={(completed ? "SceneViewMotion.Complete" : "fallback")}{camInfo}");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-SceneViewTool] release failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Dispatch SceneView frame command using the same fallback order as
        /// Tuanjie SceneView.FrameSelectedMenuItem:
        /// 1) current offscreen target
        /// 2) last active SceneView
        /// 3) direct SceneView.FrameSelected call
        /// </summary>
        private static bool TryDispatchSceneViewFrameCommand(bool lockView)
        {
            string command = lockView ? "FrameSelectedWithLock" : "FrameSelected";
            var sv = s_OffscreenTarget as SceneView;
            if (sv == null) return false;

            bool handled = false;
            try
            {
                handled = sv.SendEvent(EditorGUIUtility.CommandEvent(command));
                if (handled)
                {
                    sv.Focus();
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Shortcut] SendEvent {command} failed: {ex.Message}");
            }

            if (!handled && SceneView.lastActiveSceneView != null && SceneView.lastActiveSceneView != sv)
            {
                try
                {
                    handled = SceneView.lastActiveSceneView.SendEvent(EditorGUIUtility.CommandEvent(command));
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"[NWB-Shortcut] lastActiveSceneView {command} failed: {ex.Message}");
                }
            }

            if (!handled)
            {
                try
                {
                    MethodInfo frameMethod = typeof(SceneView).GetMethod(
                        "FrameSelected",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new[] { typeof(bool) },
                        null);
                    if (frameMethod != null)
                    {
                        object r = frameMethod.Invoke(sv, new object[] { lockView });
                        if (r is bool b) handled = b;
                    }
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"[NWB-Shortcut] Direct FrameSelected({lockView}) failed: {ex.Message}");
                }
            }

            sv.Repaint();
            return handled;
        }

        /// <summary>
        /// Handle keyboard shortcuts for SceneView that normally go through ShortcutManager.
        /// In offscreen mode, SendEvent bypasses the global ShortcutManager pipeline, so
        /// tool selection (Q/W/E/R/T/Y), pivot toggles (Z/X), frame selected (F), and
        /// delete must be dispatched manually.
        /// </summary>
        private static bool TryHandleSceneViewShortcut(string key, EventModifiers mods, bool isSceneView)
        {
            if (!isSceneView) return false;
            if (EditorGUIUtility.editingTextField) return false;

            bool noMods = (mods & ~EventModifiers.FunctionKey) == EventModifiers.None;
            bool shiftOnly = (mods & ~EventModifiers.FunctionKey) == EventModifiers.Shift;

            if (key == null || key.Length != 1) return false;
            char c = char.ToLowerInvariant(key[0]);

            if (noMods)
            {
                switch (c)
                {
                    case 'q':
                        UnityEditor.Tools.current = Tool.View;
                        LogVerbose("[NWB-Shortcut] Tool → View (Q)");
                        return true;
                    case 'w':
                        UnityEditor.Tools.current = Tool.Move;
                        LogVerbose("[NWB-Shortcut] Tool → Move (W)");
                        return true;
                    case 'e':
                        UnityEditor.Tools.current = Tool.Rotate;
                        LogVerbose("[NWB-Shortcut] Tool → Rotate (E)");
                        return true;
                    case 'r':
                        UnityEditor.Tools.current = Tool.Scale;
                        LogVerbose("[NWB-Shortcut] Tool → Scale (R)");
                        return true;
                    case 't':
                        UnityEditor.Tools.current = Tool.Rect;
                        LogVerbose("[NWB-Shortcut] Tool → Rect (T)");
                        return true;
                    case 'y':
                        UnityEditor.Tools.current = Tool.Transform;
                        LogVerbose("[NWB-Shortcut] Tool → Transform (Y)");
                        return true;
                    case 'z':
                        UnityEditor.Tools.pivotMode = (UnityEditor.Tools.pivotMode == PivotMode.Center)
                            ? PivotMode.Pivot : PivotMode.Center;
                        LogVerbose($"[NWB-Shortcut] Pivot → {UnityEditor.Tools.pivotMode} (Z)");
                        return true;
                    case 'x':
                        UnityEditor.Tools.pivotRotation = (UnityEditor.Tools.pivotRotation == PivotRotation.Global)
                            ? PivotRotation.Local : PivotRotation.Global;
                        LogVerbose($"[NWB-Shortcut] Rotation → {UnityEditor.Tools.pivotRotation} (X)");
                        return true;
                    case 'f':
                        TryDispatchSceneViewFrameCommand(lockView: false);
                        LogVerbose("[NWB-Shortcut] FrameSelected (F)");
                        return true;
                    case '2':
                    {
                        var sv = s_OffscreenTarget as SceneView;
                        if (sv != null)
                        {
                            sv.in2DMode = !sv.in2DMode;
                            LogVerbose($"[NWB-Shortcut] 2D Mode → {sv.in2DMode} (2)");
                        }
                        return true;
                    }
                }
            }
            if (shiftOnly && c == 'f')
            {
                TryDispatchSceneViewFrameCommand(lockView: true);
                LogVerbose("[NWB-Shortcut] FrameSelectedWithLock (Shift+F)");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Inject SceneView's internal picking command to select hovered objects.
        /// This mirrors RectSelection.DelayPicking() in Tuanjie source and bypasses
        /// shortcut-context dependence on focusedWindow.
        /// </summary>
        private static void TryDispatchSceneViewPickingCommand(Vector2 mousePos)
        {
            if (s_OffscreenTarget == null || s_OffscreenTargetType == null || s_OffscreenTargetType.Name != "SceneView")
                return;
            try
            {
                // Ensure nearestControl for RectSelection is up to date in the same frame.
                var layoutEvt = new Event { type = EventType.Layout, mousePosition = mousePos };
                var moveEvt = new Event { type = EventType.MouseMove, mousePosition = mousePos, delta = Vector2.zero };
                s_OffscreenTarget.SendEvent(layoutEvt);
                s_OffscreenTarget.SendEvent(moveEvt);

                var pickCmd = EditorGUIUtility.CommandEvent("SceneViewPickingEventCommand");
                pickCmd.mousePosition = mousePos;
                s_OffscreenTarget.SendEvent(pickCmd);

                if (s_ActionEventCount <= 10)
                {
                    LogVerbose($"[NWB-ScenePick] Injected SceneViewPickingEventCommand at ({mousePos.x:F1},{mousePos.y:F1}) selCount={Selection.objects?.Length ?? 0}");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ScenePick] Inject command failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Find instance method on the current type or any base type, including
        /// non-public members declared on base classes.
        /// </summary>
        private static MethodInfo FindInstanceMethodOnTypeHierarchy(System.Type type, string name, System.Type[] paramTypes)
        {
            for (System.Type t = type; t != null; t = t.BaseType)
            {
                MethodInfo mi = t.GetMethod(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly,
                    null,
                    paramTypes,
                    null);
                if (mi != null) return mi;
            }
            return null;
        }

        // Cached reflection accessor for the key-focus delegate on GUIUtility.
        private static bool s_KeyFocusDelegateResolved;
        private static FieldInfo s_KeyFocusDelegateField;
        private static Func<bool> s_OriginalKeyFocusFunc;
        // Tracks whether we saved the original delegate (needed because the
        // original may be null, and we must restore null — not skip restoration).
        private static bool s_OriginalKeyFocusSaved;

        // Cached reflection accessors for fallback text editing when
        // GUIView.hasFocus is false (offscreen mode on macOS).
        private static bool s_TextEditorFieldsResolved;
        private static FieldInfo s_RecycledEditorField;   // EditorGUI.s_RecycledEditor
        private static FieldInfo s_ActuallyEditingField;  // RecycledTextEditor.s_ActuallyEditing
        private static FieldInfo s_OriginalTextField;     // EditorGUI.s_OriginalText
        // Cached field for syncing search text back to the EditorWindow so that
        // the next OnGUI/Repaint displays the correct value even when
        // IsEditingControl returns false (native hasFocus always false in offscreen).
        private static FieldInfo s_SearchFieldTextField;  // target window's search text field

        /// <summary>
        /// Override GUIUtility.s_HasCurrentWindowKeyFocusFunc so that all IMGUI
        /// controls (EditorGUI.DoTextField, SearchField, etc.) believe the current
        /// window has key focus.  This avoids calling GUIView.Focus() which would
        /// bring Unity to the foreground on macOS.
        ///
        /// Background: GUIView.hasFocus is a native property (IsViewFocused →
        /// GetKeyGUIView() == this).  On macOS, GetKeyGUIView() walks
        /// NSApp.keyWindow.firstResponder, which is NULL when Unity is hidden.
        /// EditorGUIUtility registers HasCurrentWindowKeyFocus (which calls
        /// GUIView.current.hasFocus) as GUIUtility.s_HasCurrentWindowKeyFocusFunc.
        /// EditorGUI.HasKeyboardFocus and RecycledTextEditor.IsEditingControl both
        /// go through this delegate — so overriding it to return true lets all
        /// Editor text fields work without OS-level focus.
        /// </summary>
        private static void OverrideKeyFocusForOffscreen()
        {
            try
            {
                if (!s_KeyFocusDelegateResolved)
                {
                    s_KeyFocusDelegateResolved = true;
                    s_KeyFocusDelegateField = typeof(GUIUtility).GetField(
                        "s_HasCurrentWindowKeyFocusFunc",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    if (s_KeyFocusDelegateField != null)
                        LogVerbose("[NWB-Offscreen] Resolved GUIUtility.s_HasCurrentWindowKeyFocusFunc");
                    else
                        CodelyLogger.LogWarning("[NWB-Offscreen] Could not find s_HasCurrentWindowKeyFocusFunc");
                }

                if (s_KeyFocusDelegateField != null)
                {
                    // Only save the original delegate the FIRST time.
                    // Subsequent calls already replaced the field with our
                    // () => true override; re-reading would lose the real original.
                    // Note: the original may be null, so use a separate flag.
                    if (!s_OriginalKeyFocusSaved)
                    {
                        s_OriginalKeyFocusFunc = (Func<bool>)s_KeyFocusDelegateField.GetValue(null);
                        s_OriginalKeyFocusSaved = true;
                    }
                    s_KeyFocusDelegateField.SetValue(null, (Func<bool>)(() => true));
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] OverrideKeyFocus failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Fallback text editing for when SendEvent cannot consume keyboard events
        /// because GUIView.hasFocus (native) returns false.
        ///
        /// On macOS, when Unity is hidden, GetKeyGUIView() returns NULL and
        /// GUIView.hasFocus is always false.  This makes RecycledTextEditor.
        /// IsEditingControl() return false even though BeginEditing() was called
        /// (by our delegate override), so DoTextField never calls HandleKeyEvent
        /// or Insert.
        ///
        /// This method directly operates on the RecycledTextEditor instance to
        /// process keyboard input, mirroring what DoTextField's KeyDown branch does.
        /// </summary>
        private static void FallbackHandleKeyDown(Event evt, char charToInsert)
        {
            try
            {
                if (!s_TextEditorFieldsResolved)
                {
                    s_TextEditorFieldsResolved = true;
                    var editorGuiType = typeof(EditorGUI);

                    s_RecycledEditorField = editorGuiType.GetField("s_RecycledEditor",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                    s_OriginalTextField = editorGuiType.GetField("s_OriginalText",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                    if (s_RecycledEditorField != null)
                    {
                        System.Type rteType = s_RecycledEditorField.FieldType;
                        s_ActuallyEditingField = rteType.GetField("s_ActuallyEditing",
                            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    }

                    LogVerbose($"[NWB-Offscreen] FallbackTextEditor resolved: editor={s_RecycledEditorField != null} editing={s_ActuallyEditingField != null} origText={s_OriginalTextField != null}");
                }

                if (s_RecycledEditorField == null) return;
                var editor = s_RecycledEditorField.GetValue(null) as TextEditor;
                if (editor == null) return;

                bool isEditing = s_ActuallyEditingField != null &&
                                 (bool)s_ActuallyEditingField.GetValue(null);
                if (!isEditing) return;
                if (editor.controlID != GUIUtility.keyboardControl) return;

                // Phase 1: let TextEditor handle special keys (Backspace, arrows, etc.)
                bool handled = editor.HandleKeyEvent(evt);
                if (handled)
                {
                    if (s_ActionEventCount <= 10)
                        LogVerbose($"[NWB-Input-Diag] Fallback HandleKeyEvent consumed: kc={evt.keyCode}");
                }

                // Phase 2: insert printable character (only if Phase 1 did not handle)
                if (!handled && charToInsert > 31 && charToInsert != 127)
                {
                    editor.Insert(charToInsert);
                    handled = true;
                    if (s_ActionEventCount <= 10)
                        LogVerbose($"[NWB-Input-Diag] Fallback Insert: ch={(int)charToInsert}('{charToInsert}') text='{editor.text}'");
                }

                if (handled)
                {
                    // Sync s_OriginalText so next BeginEditing won't revert our edit.
                    if (s_OriginalTextField != null)
                        s_OriginalTextField.SetValue(null, editor.text);
                    // Sync edited text back to the EditorWindow's search field so that
                    // the next OnGUI/Repaint displays it (Repaint uses the text parameter
                    // passed to DoTextField, not editor.text, when IsEditingControl is false).
                    SyncEditorTextToWindow(editor.text);
                }
            }
            catch (Exception ex)
            {
                if (s_ActionEventCount <= 5)
                    CodelyLogger.LogWarning($"[NWB-Offscreen] FallbackHandleKeyDown failed: {ex.Message}");
            }
        }

        // Cached MethodInfo for SetSearch(string) on the target EditorWindow.
        // ProjectBrowser.SetSearch is public; other window types may use a
        // private search text field as fallback.
        private static MethodInfo s_SetSearchMethod;
        private static bool s_SetSearchMethodResolved;

        /// <summary>
        /// After fallback editing changes the RecycledTextEditor's text, sync
        /// the value back into the EditorWindow so the search results update.
        ///
        /// For ProjectBrowser this calls SetSearch(string) which parses the
        /// text into SearchFilter, refreshes the list area, and repaints.
        /// For other windows we fall back to setting the string field directly.
        /// </summary>
        private static void SyncEditorTextToWindow(string newText)
        {
            if (s_OffscreenTarget == null) return;
            try
            {
                System.Type winType = s_OffscreenTarget.GetType();

                // Prefer SetSearch(string) which triggers full search pipeline
                if (!s_SetSearchMethodResolved)
                {
                    s_SetSearchMethodResolved = true;
                    s_SetSearchMethod = winType.GetMethod("SetSearch",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new[] { typeof(string) }, null);
                    if (s_SetSearchMethod != null)
                        LogVerbose($"[NWB-Offscreen] Resolved SetSearch method on {winType.Name}");
                }

                if (s_SetSearchMethod != null)
                {
                    s_SetSearchMethod.Invoke(s_OffscreenTarget, new object[] { newText });
                    return;
                }

                // Fallback: set the raw text field for windows without SetSearch
                if (s_SearchFieldTextField == null)
                {
                    string[] candidates = { "m_SearchFieldText", "m_SearchText", "m_SearchString", "m_Filter" };
                    foreach (string name in candidates)
                    {
                        FieldInfo fi = winType.GetField(name,
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (fi != null && fi.FieldType == typeof(string))
                        {
                            s_SearchFieldTextField = fi;
                            LogVerbose($"[NWB-Offscreen] Resolved search text field: {winType.Name}.{name}");
                            break;
                        }
                    }
                }

                if (s_SearchFieldTextField != null)
                {
                    s_SearchFieldTextField.SetValue(s_OffscreenTarget, newText);
                }
            }
            catch (Exception ex)
            {
                if (s_ActionEventCount <= 3)
                    CodelyLogger.LogWarning($"[NWB-Offscreen] SyncEditorTextToWindow failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore the original s_HasCurrentWindowKeyFocusFunc delegate.
        /// </summary>
        private static void RestoreKeyFocusAfterOffscreen()
        {
            try
            {
                // Restore even if original was null — the key is that we
                // remove our () => true override from the delegate field.
                if (s_KeyFocusDelegateField != null && s_OriginalKeyFocusSaved)
                    s_KeyFocusDelegateField.SetValue(null, s_OriginalKeyFocusFunc);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Offscreen] RestoreKeyFocus failed: {ex.Message}");
            }
        }

        // ---- PrintWindow capture: bypasses GrabPixels entirely ----

#if UNITY_EDITOR_WIN
        /// <summary>
        /// Allocate or resize the GDI DIB Section used by PrintWindow.
        /// Top-down 32-bit BGRA bitmap stored in shared memory (s_PwBits).
        /// </summary>
        private static void EnsurePrintWindowDIB(int w, int h)
        {
            if (s_PwDC != IntPtr.Zero && s_PwDibW == w && s_PwDibH == h)
                return;
            ReleasePrintWindowDIB();

            s_PwDC = CreateCompatibleDC(IntPtr.Zero);
            var bmi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                biWidth = w,
                biHeight = -h, // negative → top-down scanline order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };
            s_PwBitmap = CreateDIBSection(s_PwDC, ref bmi, 0,
                out s_PwBits, IntPtr.Zero, 0);
            if (s_PwBitmap != IntPtr.Zero)
                s_PwOldBmp = SelectObject(s_PwDC, s_PwBitmap);
            s_PwDibW = w;
            s_PwDibH = h;
        }

        private static void ReleasePrintWindowDIB()
        {
            if (s_PwOldBmp != IntPtr.Zero && s_PwDC != IntPtr.Zero)
                SelectObject(s_PwDC, s_PwOldBmp);
            if (s_PwBitmap != IntPtr.Zero)
                DeleteObject(s_PwBitmap);
            if (s_PwDC != IntPtr.Zero)
                DeleteDC(s_PwDC);
            s_PwDC = IntPtr.Zero;
            s_PwBitmap = IntPtr.Zero;
            s_PwOldBmp = IntPtr.Zero;
            s_PwBits = IntPtr.Zero;
            s_PwDibW = 0;
            s_PwDibH = 0;
        }

        /// <summary>
        /// Capture a window's visual content via Win32 PrintWindow + DWM composition.
        /// Completely bypasses Unity's GrabPixels, solving both the GameView magnification
        /// and non-GameView black screen issues in Unity 2019 Gamma mode.
        ///
        /// Flow: PrintWindow(PW_RENDERFULLCONTENT) → DWM bitmap → crop to client area
        ///       → Texture2D.LoadRawTextureData → Graphics.Blit → captureRT.
        /// </summary>
        private static int s_PrintWindowLogCount;

        private static bool TryCaptureViaPrintWindow(IntPtr containerHwnd, RenderTexture captureRT)
        {
            return TryCaptureViaPrintWindow(containerHwnd, captureRT, Rect.zero, false);
        }

        private static bool TryCaptureViaPrintWindow(IntPtr containerHwnd, RenderTexture captureRT, Rect viewScreenRect, bool useViewRectCrop)
        {
            if (containerHwnd == IntPtr.Zero || captureRT == null) return false;

            if (!GetWindowRect(containerHwnd, out RECT winRect)) return false;
            int winW = winRect.Right - winRect.Left;
            int winH = winRect.Bottom - winRect.Top;
            if (winW <= 0 || winH <= 0) return false;

            // Temporarily strip WS_EX_LAYERED so DWM can capture
            // the D3D11 SwapChain content. Layered windows are normally
            // expected to supply their own bitmap via UpdateLayeredWindow,
            // which Unity never calls, so DWM has no content to capture.
            int exStyle = GetWindowLongW(containerHwnd, GWL_EXSTYLE);
            bool wasLayered = (exStyle & WS_EX_LAYERED) != 0;
            if (wasLayered)
                SetWindowLongW(containerHwnd, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED);

            EnsurePrintWindowDIB(winW, winH);
            bool ok = s_PwBits != IntPtr.Zero &&
                      PrintWindow(containerHwnd, s_PwDC, PW_RENDERFULLCONTENT);

            // Restore layered style immediately
            if (wasLayered)
                SetWindowLongW(containerHwnd, GWL_EXSTYLE, exStyle);

            if (!ok) return false;

            // Calculate client area offset within the full window bitmap
            if (!GetClientRect(containerHwnd, out RECT clientRect)) return false;
            POINT clientOrigin = new POINT { X = 0, Y = 0 };
            ClientToScreen(containerHwnd, ref clientOrigin);

            int borderL = clientOrigin.X - winRect.Left;
            int borderT = clientOrigin.Y - winRect.Top;
            int clientW = clientRect.Right;
            int clientH = clientRect.Bottom;

            // Target dimensions must match captureRT.
            int dstW = captureRT.width;
            int dstH = captureRT.height;
            int srcX = 0;
            int srcY = 0;
            int srcW = clientW;
            int srcH = clientH;

            if (useViewRectCrop)
            {
                // Crop PrintWindow to the slot's view rect in client coordinates.
                // This avoids copying the top-left of the whole container for
                // every slot when multiple DockAreas share one ContainerWindow.
                int viewX = Mathf.RoundToInt(viewScreenRect.xMin * LocalPP);
                int viewY = Mathf.RoundToInt(viewScreenRect.yMin * LocalPP);
                int viewW = Mathf.RoundToInt(viewScreenRect.width * LocalPP);
                int viewH = Mathf.RoundToInt(viewScreenRect.height * LocalPP);
                srcX = viewX - clientOrigin.X;
                srcY = viewY - clientOrigin.Y;
                srcW = viewW;
                srcH = viewH;

                // Clamp to client bounds.
                if (srcX < 0)
                {
                    srcW += srcX;
                    srcX = 0;
                }
                if (srcY < 0)
                {
                    srcH += srcY;
                    srcY = 0;
                }
                if (srcX + srcW > clientW)
                    srcW = clientW - srcX;
                if (srcY + srcH > clientH)
                    srcH = clientH - srcY;
            }

            int copyW = Mathf.Min(srcW, dstW);
            int copyH = Mathf.Min(srcH, dstH);
            if (copyW <= 0 || copyH <= 0) return false;

            s_PrintWindowLogCount++;
            if (s_PrintWindowLogCount <= 3 || s_PrintWindowLogCount % 600 == 0)
            {
                LogVerbose($"[NWB-PrintWin] #{s_PrintWindowLogCount} winRect={winW}x{winH} client={clientW}x{clientH} " +
                    $"border=({borderL},{borderT}) src=({srcX},{srcY},{srcW}x{srcH}) dst={dstW}x{dstH} copy={copyW}x{copyH} layered={wasLayered} ppp={LocalPP} dpi={DPIScale} useViewRect={useViewRectCrop}");
            }

            // When DPI scaling causes the PrintWindow capture size (client area)
            // to differ from captureRT dimensions, use the actual capture dimensions
            // for the intermediate texture and let Graphics.Blit scale to captureRT.
            int texW = copyW;
            int texH = copyH;

            int dataSize = texW * texH * 4;
            if (s_PwBuffer == null || s_PwBuffer.Length < dataSize)
                s_PwBuffer = new byte[dataSize];

            // Crop client area rows from the full-window DIB.
            // DIB is top-down BGRA, so row y starts at offset y*stride.
            int srcStride = winW * 4;
            int texStride = texW * 4;
            int rowBytes = texW * 4;
            for (int y = 0; y < texH; y++)
            {
                Marshal.Copy(
                    s_PwBits + (borderT + srcY + y) * srcStride + (borderL + srcX) * 4,
                    s_PwBuffer, y * texStride, rowBytes);
            }

            // Fix alpha: PrintWindow may leave alpha at 0 for opaque pixels
            // (GDI convention). Force alpha to 255 so the texture is opaque.
            for (int i = 3; i < texH * texStride; i += 4)
                s_PwBuffer[i] = 255;

            if (s_PwTex == null || s_PwTex.width != texW || s_PwTex.height != texH)
            {
                if (s_PwTex != null) UnityEngine.Object.DestroyImmediate(s_PwTex);
                s_PwTex = new Texture2D(texW, texH, TextureFormat.BGRA32, false);
            }
            s_PwTex.LoadRawTextureData(s_PwBuffer);
            s_PwTex.Apply(false);
            Graphics.Blit(s_PwTex, captureRT);

            return true;
        }
#endif

        /// <summary>
        /// Capture IMGUI windows (Inspector, Hierarchy, Console, Project, etc.)
        /// by forcing a repaint into a temporary RenderTexture using GUIView internals.
        /// Works for any EditorWindow that renders via IMGUI.
        /// </summary>
        private static bool TryCaptureViaGUIView(EditorWindow window)
        {
            if (window == null || s_CaptureRT == null) return false;

            try
            {
                // Access m_Parent (GUIView) which Unity uses internally for all EditorWindows.
                System.Type editorWindowType = typeof(EditorWindow);
                FieldInfo parentField = editorWindowType.GetField("m_Parent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (parentField == null) return false;

                object guiView = parentField.GetValue(window);
                if (guiView == null) return false;

                // Try to get the cached background texture from GUIView.
                System.Type guiViewType = guiView.GetType();

                // Approach A: Use GrabPixels to capture the full GUIView content including
                // DockArea tab bar and any toolbar. The captured image covers the entire
                // GUIView area. Mouse coordinates must be offset by s_TabBarOffsetY in
                // InjectInputEvent to map from video coords to EditorWindow content coords.
                MethodInfo grabPixels = guiViewType.GetMethod("GrabPixels",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(RenderTexture), typeof(Rect) },
                    null);
                if (grabPixels != null)
                {
                    // GrabPixels operates on the GPU backbuffer. On Windows, the
                    // backbuffer is at physical pixel resolution but screenPosition
                    // returns logical points — multiply by pixelsPerPoint to capture
                    // the full content. On macOS, Metal handles backing scale factor
                    // internally so logical points are used directly.
                    PropertyInfo screenPosProp = guiViewType.GetProperty("screenPosition",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    float grabW, grabH;
                    if (screenPosProp != null)
                    {
                        Rect screenPos = (Rect)screenPosProp.GetValue(guiView);
                        grabW = screenPos.width * LocalPP;
                        grabH = screenPos.height * LocalPP;
                    }
                    else
                    {
                        Rect pos = window.position;
                        grabW = pos.width * LocalPP;
                        grabH = (pos.height + s_TabBarOffsetY) * LocalPP;
                    }
                    // Guard against zero-size rects (window not yet laid out
                    // during composite layout transitions). GrabPixels with a
                    // 0×0 rect crashes in AuxBackBufferManager::OnWindowBeginRender.
                    if (grabW < 1f || grabH < 1f)
                        return false;
                    Rect sourceRect = new Rect(0, 0, grabW, grabH);

                    grabPixels.Invoke(guiView, new object[] { s_CaptureRT, sourceRect });
                    return true;
                }

                // Approach B: Try accessing the cached RT directly.
                string[] rtCandidates = {
                    "m_BackingScaleFactorRT", "m_BackTexture", "m_TargetTexture"
                };
                foreach (string name in rtCandidates)
                {
                    FieldInfo fi = guiViewType.GetField(name,
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (fi != null && typeof(RenderTexture).IsAssignableFrom(fi.FieldType))
                    {
                        var rt = fi.GetValue(guiView) as RenderTexture;
                        if (rt != null && rt.IsCreated())
                        {
                            Graphics.Blit(rt, s_CaptureRT);
                            return true;
                        }
                    }

                    PropertyInfo pi = guiViewType.GetProperty(name,
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (pi != null && typeof(RenderTexture).IsAssignableFrom(pi.PropertyType))
                    {
                        var rt = pi.GetValue(guiView) as RenderTexture;
                        if (rt != null && rt.IsCreated())
                        {
                            Graphics.Blit(rt, s_CaptureRT);
                            return true;
                        }
                    }
                }

                // Approach C: Repaint and capture via temporary BeginPaint/EndPaint.
                // This forces a draw cycle and creates an accessible render target.
                MethodInfo repaintImmediately = guiViewType.GetMethod("RepaintImmediately",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (repaintImmediately != null)
                {
                    repaintImmediately.Invoke(guiView, null);

                    // After RepaintImmediately, try getting the RT again.
                    foreach (string name in rtCandidates)
                    {
                        FieldInfo fi = guiViewType.GetField(name,
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        if (fi != null && typeof(RenderTexture).IsAssignableFrom(fi.FieldType))
                        {
                            var rt = fi.GetValue(guiView) as RenderTexture;
                            if (rt != null && rt.IsCreated())
                            {
                                Graphics.Blit(rt, s_CaptureRT);
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (s_OffscreenFrameCount <= 5)
                    CodelyLogger.LogWarning($"[NWB-Offscreen] GUIView capture failed: {ex.Message}");
            }

            return false;
        }

        // --- Reflection helper: get RenderTexture from EditorWindow ---
        private static RenderTexture TryGetWindowRenderTexture(EditorWindow window)
        {
            if (window == null) return null;
            string[] candidates = { "targetTexture", "m_TargetTexture", "m_RenderTexture" };
            System.Type t = window.GetType();
            foreach (string candidate in candidates)
            {
                var pi = t.GetProperty(candidate, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi != null && typeof(RenderTexture).IsAssignableFrom(pi.PropertyType))
                {
                    var rt = pi.GetValue(window) as RenderTexture;
                    if (rt != null) return rt;
                }

                var fi = t.GetField(candidate, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi != null && typeof(RenderTexture).IsAssignableFrom(fi.FieldType))
                {
                    var rt = fi.GetValue(window) as RenderTexture;
                    if (rt != null) return rt;
                }
            }
            return null;
        }

        private static KeyCode ParseKeyCode(string key)
        {
            if (string.IsNullOrEmpty(key)) return KeyCode.None;

            // Browser key names → Unity KeyCode mapping for special keys.
            switch (key)
            {
                case "ArrowUp":    return KeyCode.UpArrow;
                case "ArrowDown":  return KeyCode.DownArrow;
                case "ArrowLeft":  return KeyCode.LeftArrow;
                case "ArrowRight": return KeyCode.RightArrow;
                case "Enter":      return KeyCode.Return;
                case "Backspace":  return KeyCode.Backspace;
                case "Delete":     return KeyCode.Delete;
                case "Escape":     return KeyCode.Escape;
                case "Tab":        return KeyCode.Tab;
                case "Home":       return KeyCode.Home;
                case "End":        return KeyCode.End;
                case "PageUp":     return KeyCode.PageUp;
                case "PageDown":   return KeyCode.PageDown;
                case "Insert":     return KeyCode.Insert;
                case " ":          return KeyCode.Space;
                case "F1":  return KeyCode.F1;   case "F2":  return KeyCode.F2;
                case "F3":  return KeyCode.F3;   case "F4":  return KeyCode.F4;
                case "F5":  return KeyCode.F5;   case "F6":  return KeyCode.F6;
                case "F7":  return KeyCode.F7;   case "F8":  return KeyCode.F8;
                case "F9":  return KeyCode.F9;   case "F10": return KeyCode.F10;
                case "F11": return KeyCode.F11;  case "F12": return KeyCode.F12;
                case "Shift": case "Control": case "Alt": case "Meta":
                    return KeyCode.None;
            }

            if (key.Length == 1)
            {
                char c = char.ToUpperInvariant(key[0]);
                if (c >= 'A' && c <= 'Z')
                    return (KeyCode)System.Enum.Parse(typeof(KeyCode), c.ToString());
                if (c >= '0' && c <= '9')
                    return (KeyCode)System.Enum.Parse(typeof(KeyCode), "Alpha" + c);
            }
            if (System.Enum.TryParse(key, true, out KeyCode code))
                return code;
            return KeyCode.None;
        }

#if UNITY_EDITOR_WIN
        /// <summary>
        /// Post a Win32 WM_KEYDOWN/WM_KEYUP message to simulate native keyboard input
        /// for the runtime Input system (Input.GetKey). EditorWindow.SendEvent only
        /// dispatches IMGUI events and does not update the Input Manager key state.
        /// Posting to any Unity HWND feeds into the main thread's message loop where
        /// the native input processing picks up the key state change.
        /// </summary>
        private static void PostNativeKeyMessage(bool isDown, string browserKey)
        {
            ushort vk = BrowserKeyToVK(browserKey);
            if (vk == 0) return;

            // Prefer the GameView's own GUIView or ContainerWindow HWND so
            // that the WM_KEYDOWN is processed by the correct WndProc and
            // updates the Input Manager key state. Fall back to the first
            // transparent HWND (main editor window) if no target is cached.
            IntPtr targetHwnd = s_OffscreenTargetGUIViewHwnd != IntPtr.Zero
                ? s_OffscreenTargetGUIViewHwnd
                : (s_OffscreenTargetContainerHwnd != IntPtr.Zero
                    ? s_OffscreenTargetContainerHwnd
                    : (s_TransparentHwnds.Count > 0 ? s_TransparentHwnds[0] : IntPtr.Zero));
            if (targetHwnd == IntPtr.Zero) return;

            uint scanCode = MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
            uint msg = isDown ? WM_KEYDOWN : WM_KEYUP;

            // Build lParam: repeat=1, scan code in bits 16-23.
            // For keyup: set previous-state (bit 30) and transition (bit 31).
            uint lParam = 1u | (scanCode << 16);
            if (!isDown)
                lParam |= (1u << 30) | (1u << 31);

            PostMessageW(targetHwnd, msg, (IntPtr)vk, (IntPtr)lParam);
        }

        /// <summary>
        /// Send a synchronous Win32 mouse button message to the GameView HWND
        /// to update the native InputManager's mouse button state.
        ///
        /// CRITICAL: the lParam MUST contain a client-area position that falls
        /// inside the GameView content rect. GUIView::GUIViewWndProc checks:
        ///   m_GameViewClippedRect.Contains(DPIScaleHelper.ConvertFromNative(lParam))
        /// and only calls ProcessInputMessage (which sets InputManager mouse state)
        /// when the position is inside the game area. Passing (1,1) lands in the
        /// toolbar, causing ProcessInputMessage to be skipped entirely.
        ///
        /// SendMessage is SYNCHRONOUS — the WndProc executes within our call stack.
        /// We immediately call ReleaseCapture() after to undo SetCapture.
        /// </summary>
        private static void PostNativeMouseButtonMessage(bool isDown, int button,
            int clientX = 0, int clientY = 0)
        {
            IntPtr hwnd = s_OffscreenTargetGUIViewHwnd != IntPtr.Zero
                ? s_OffscreenTargetGUIViewHwnd
                : s_OffscreenTargetContainerHwnd;
            if (hwnd == IntPtr.Zero) return;

            uint msg;
            switch (button)
            {
                case 0: msg = isDown ? WM_LBUTTONDOWN : WM_LBUTTONUP; break;
                case 1: msg = isDown ? WM_MBUTTONDOWN : WM_MBUTTONUP; break;
                case 2: msg = isDown ? WM_RBUTTONDOWN : WM_RBUTTONUP; break;
                default: return;
            }

            // Update held set BEFORE SendMessage so wParam reflects the
            // complete set of currently pressed buttons (including the one
            // being pressed/released right now).
            if (isDown)
                s_WinHeldMouseButtons.Add(button);
            else
                s_WinHeldMouseButtons.Remove(button);

            // Build wParam with all held-button flags, matching what
            // Windows sends natively for concurrent button presses.
            int wp = 0;
            foreach (int btn in s_WinHeldMouseButtons)
            {
                if (btn == 0) wp |= (int)MK_LBUTTON;
                else if (btn == 2) wp |= (int)MK_RBUTTON;
                else if (btn == 1) wp |= (int)MK_MBUTTON;
            }

            // MAKELPARAM(x, y) — position MUST be inside GameView content rect
            // so GUIViewWndProc allows ProcessInputMessage to run.
            IntPtr lParam = (IntPtr)((clientY << 16) | (clientX & 0xFFFF));

            SendMessage(hwnd, msg, (IntPtr)wp, lParam);

            if (isDown)
                ReleaseCapture();

            bool unityMB1 = false, unityMB2 = false;
            if (EditorApplication.isPlaying)
            {
                try
                {
                    unityMB1 = Input.GetMouseButton(1);
                    unityMB2 = Input.GetMouseButton(2);
                }
                catch (Exception) { }
            }

            LogVerbose($"[NWB-MouseBtn] SendMessage 0x{msg:X4}" +
                $" hwnd=0x{hwnd.ToInt64():X} down={isDown} btn={button}" +
                $" pos=({clientX},{clientY})" +
                $" Unity.MB(1)={unityMB1} Unity.MB(2)={unityMB2}" +
                $" held=[{string.Join(",", s_WinHeldMouseButtons)}]");
        }
#endif

        // Cached reflection accessor for Unsupported.SetAllowCursorLock(bool, DisallowCursorLockReasons).
        private static MethodInfo s_SetAllowCursorLockMethod;
        private static object s_CursorLockReasonOther;

        /// <summary>
        /// Call Unsupported.SetAllowCursorLock via reflection (internal API).
        /// Replicates GameView.AllowCursorLockAndHide cursor-lock part.
        /// </summary>
        private static void SetAllowCursorLockReflection(bool allow)
        {
            if (s_SetAllowCursorLockMethod == null)
            {
                s_SetAllowCursorLockMethod = typeof(Unsupported).GetMethod(
                    "SetAllowCursorLock",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                // Convert int 2 to the internal DisallowCursorLockReasons enum type.
                Type reasonsType = typeof(Unsupported).GetNestedType(
                    "DisallowCursorLockReasons",
                    BindingFlags.NonPublic);
                if (reasonsType != null)
                    s_CursorLockReasonOther = Enum.ToObject(reasonsType, 2);
            }
            if (s_SetAllowCursorLockMethod != null && s_CursorLockReasonOther != null)
                s_SetAllowCursorLockMethod.Invoke(null, new object[] { allow, s_CursorLockReasonOther });
        }

        // Keep GameView toolbar height in one place so all input-coordinate
        // conversions stay consistent when Unity internal constants change.
        private const float kGameViewToolbarHeight = 21f;

        // Convert tab bar + toolbar logical offset to raw render-target pixels.
        private static float GetGameViewOffsetPixels(float pixelsPerPoint)
        {
            return (s_TabBarOffsetY + kGameViewToolbarHeight) * pixelsPerPoint;
        }

        // Check whether a raw Y coordinate is inside the playable game area.
        private static bool IsInGamePlayArea(float rawPixelY, float pixelsPerPoint)
        {
            return rawPixelY >= GetGameViewOffsetPixels(pixelsPerPoint);
        }

        // When the GameView uses a fixed aspect ratio (e.g. 16:9) or a fixed
        // resolution (e.g. QHD 2560x1440), the game content is letterboxed
        // AND potentially scaled within the content area. Screen.width/height
        // reflects the game render-target size, not the content area size.
        //
        // This method computes the letterbox offset and scale factor needed
        // to convert raw render-target pixels (what GrabPixels captures) to
        // game Screen coordinates (what uGUI RaycastAll and
        // QueueGameViewInputEvent expect).
        //
        // For Free Aspect: offset=0, scale=1 (content area == render target).
        // For AspectRatio (16:9): offset>0, scale=1 (render target fits 1:1
        //   in one dimension, letterboxed in the other).
        // For FixedResolution (QHD): offset>0, scale!=1 (render target is
        //   scaled down to fit within the content area).
        private static void GetGameViewLetterbox(
            out float leftPx, out float topPx,
            out float scaleX, out float scaleY)
        {
            leftPx = 0f; topPx = 0f;
            scaleX = 1f; scaleY = 1f;
            if (s_OffscreenTarget == null) return;
            if (!TryGetCurrentGUIViewSize(out int guiW, out int guiH)) return;
            float ppp = EditorGUIUtility.pixelsPerPoint;
            float chromeOffsetPx = GetGameViewOffsetPixels(ppp);
            // Content area = full GUIView minus chrome (tab bar + toolbar).
            float contentW = guiW;
            float contentH = guiH - chromeOffsetPx;
            // Game render-target size.
            float sw = Screen.width;
            float sh = Screen.height;
            if (sw <= 0 || sh <= 0) return;
            float gameAspect = sw / sh;
            float contentAspect = contentW / contentH;
            float displayW, displayH;
            if (gameAspect > contentAspect)
            {
                // Game is wider: fit by width, letterbox top/bottom.
                displayW = contentW;
                displayH = contentW / gameAspect;
            }
            else
            {
                // Game is taller: fit by height, letterbox left/right.
                displayH = contentH;
                displayW = contentH * gameAspect;
            }
            leftPx = Mathf.Max(0f, (contentW - displayW) * 0.5f);
            topPx = Mathf.Max(0f, (contentH - displayH) * 0.5f);
            // Scale from display pixels to Screen/render-target pixels.
            scaleX = sw / displayW;
            scaleY = sh / displayH;
        }

        // Cached letterbox values (updated per input event to stay fresh).
        private static float s_LetterboxLeftPx;
        private static float s_LetterboxTopPx;
        private static float s_LetterboxScaleX = 1f;
        private static float s_LetterboxScaleY = 1f;

        // Update cached letterbox offset and scale. Called once per input
        // event before coordinate conversion.
        private static void UpdateLetterboxCache()
        {
            GetGameViewLetterbox(
                out s_LetterboxLeftPx, out s_LetterboxTopPx,
                out s_LetterboxScaleX, out s_LetterboxScaleY);
        }

        // Convert raw RT X (top-left origin) to game screen X (bottom-left origin).
        private static float RawPixelToScreenX(float rawPixelX)
        {
            return (rawPixelX - s_LetterboxLeftPx) * s_LetterboxScaleX;
        }

        // Convert raw RT Y (top-left origin) to game screen Y (bottom-left origin).
        private static float RawPixelToScreenY(float rawPixelY, float pixelsPerPoint)
        {
#if UNITY_EDITOR_WIN
            float contentY = rawPixelY - GetGameViewOffsetPixels(pixelsPerPoint) - s_LetterboxTopPx;
            return Screen.height - contentY * s_LetterboxScaleY;
#else
            float contentY = rawPixelY - s_LetterboxTopPx;
            return Screen.height - contentY * s_LetterboxScaleY;
#endif
        }

        /// <summary>
        /// Directly inject a pointer-down into the uGUI EventSystem by
        /// raycasting at the given raw-pixel position and executing
        /// IPointerDownHandler on the topmost hit.
        ///
        /// This bypasses StandaloneInputModule, which relies on
        /// Input.mousePosition (derived from GetCursorPos — incorrect
        /// when the GameView is off-screen).
        /// </summary>
        private static void InjectStreamingUIPress(float rawPixelX, float rawPixelY, int button)
        {
            if (button != 0) return;
            try
            {
                float ppp = EditorGUIUtility.pixelsPerPoint;
                UpdateLetterboxCache();
                var es = EventSystem.current;
                if (es == null || !es.isActiveAndEnabled)
                {
                    LogVerbose($"[NWB-StreamUI] Press SKIP: EventSystem " +
                        $"{(es == null ? "is NULL" : "disabled")}");
                    return;
                }

                float screenX = RawPixelToScreenX(rawPixelX);
                float screenY = RawPixelToScreenY(rawPixelY, ppp);

                var ped = new PointerEventData(es)
                {
                    position = new Vector2(screenX, screenY),
                    button = PointerEventData.InputButton.Left
                };

                var results = new List<RaycastResult>();
                es.RaycastAll(ped, results);

                if (results.Count > 0)
                {
                    // Search all raycast hits for an IPointerClickHandler.
                    // The topmost hit may be a non-interactive child (e.g. a
                    // Text/Label element) whose ancestor chain does not include
                    // the Button. Fall back to deeper hits that do have one.
                    GameObject hitTarget = null;
                    GameObject handler = null;
                    for (int i = 0; i < results.Count; i++)
                    {
                        var candidate = results[i].gameObject;
                        var h = ExecuteEvents.GetEventHandler<IPointerClickHandler>(candidate);
                        if (h != null)
                        {
                            hitTarget = candidate;
                            handler = h;
                            ped.pointerCurrentRaycast = results[i];
                            ped.pointerPressRaycast = results[i];
                            break;
                        }
                    }

                    if (handler != null)
                    {
                        s_StreamingPressedTarget = handler;
                        s_StreamingPointerData = ped;
                        s_StreamingIsDragTarget = false;
                        ped.pointerPress = handler;
                        ExecuteEvents.Execute(handler, ped, ExecuteEvents.pointerDownHandler);

                        LogVerbose($"[NWB-StreamUI] Press hit={hitTarget.name} " +
                            $"handler={handler.name} screenPos=({screenX:F0},{screenY:F0})");
                    }
                    else
                    {
                        // No IPointerClickHandler found — search for drag handlers
                        // (IBeginDragHandler or IDragHandler). Virtual joysticks and
                        // other drag-based UI controls implement these instead of
                        // IPointerClickHandler. Try IBeginDragHandler first (standard
                        // drag flow), then IDragHandler as fallback.
                        for (int i = 0; i < results.Count; i++)
                        {
                            var candidate = results[i].gameObject;
                            var dragH = ExecuteEvents.GetEventHandler<IBeginDragHandler>(candidate);
                            if (dragH == null)
                                dragH = ExecuteEvents.GetEventHandler<IDragHandler>(candidate);
                            if (dragH != null)
                            {
                                hitTarget = candidate;
                                handler = dragH;
                                ped.pointerCurrentRaycast = results[i];
                                ped.pointerPressRaycast = results[i];
                                break;
                            }
                        }

                        if (handler != null)
                        {
                            s_StreamingPressedTarget = handler;
                            s_StreamingPointerData = ped;
                            s_StreamingIsDragTarget = true;
                            s_StreamingLastDragScreenPos = new Vector2(screenX, screenY);
                            ped.pointerPress = handler;
                            ExecuteEvents.Execute(handler, ped, ExecuteEvents.pointerDownHandler);
                            ExecuteEvents.Execute(handler, ped, ExecuteEvents.beginDragHandler);

                            LogVerbose($"[NWB-StreamUI] Press(DRAG) hit={hitTarget.name} " +
                                $"handler={handler.name} screenPos=({screenX:F0},{screenY:F0})");
                        }
                        else
                        {
                            // Fallback to UI Toolkit when uGUI has hits but no actionable handlers.
                            InjectStreamingUIToolkitPress(screenX, screenY);
                            LogVerbose($"[NWB-StreamUI] Press: {results.Count} hits at " +
                                $"screenPos=({screenX:F0},{screenY:F0}) but no click/drag handler found");
                        }
                    }
                }
                else
                {
                    // Fallback to UI Toolkit hit-testing when uGUI raycast has no hit.
                    InjectStreamingUIToolkitPress(screenX, screenY);
                    LogVerbose($"[NWB-StreamUI] Press NO HIT " +
                        $"screenPos=({screenX:F0},{screenY:F0}) " +
                        $"Screen=({Screen.width},{Screen.height}) " +
                        $"letterbox=({s_LetterboxLeftPx:F0},{s_LetterboxTopPx:F0}) " +
                        $"scale=({s_LetterboxScaleX:F3},{s_LetterboxScaleY:F3}) " +
                        $"rawPx=({rawPixelX:F0},{rawPixelY:F0})");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-StreamUI] Press error: {ex.Message}");
            }
        }

        /// <summary>
        /// Inject an OnDrag event to the target grabbed by InjectStreamingUIPress.
        /// Called during mousedrag while a drag handler is active.
        /// Updates PointerEventData position/delta and dispatches dragHandler.
        /// </summary>
        private static void InjectStreamingUIDrag(float rawPixelX, float rawPixelY, int button)
        {
            if (button != 0) return;
            if (!s_StreamingIsDragTarget) return;
            if (s_StreamingPressedTarget == null || s_StreamingPointerData == null) return;

            try
            {
                float ppp = EditorGUIUtility.pixelsPerPoint;
                float screenX = RawPixelToScreenX(rawPixelX);
                float screenY = RawPixelToScreenY(rawPixelY, ppp);
                var newScreenPos = new Vector2(screenX, screenY);
                s_StreamingPointerData.delta = newScreenPos - s_StreamingLastDragScreenPos;
                s_StreamingPointerData.position = newScreenPos;

                ExecuteEvents.Execute(s_StreamingPressedTarget,
                    s_StreamingPointerData, ExecuteEvents.dragHandler);
                // Advance drag baseline so delta stays per-event, not press-to-current.
                s_StreamingLastDragScreenPos = newScreenPos;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-StreamUI] Drag error: {ex.Message}");
            }
        }

        /// <summary>
        /// Complete the pointer interaction started by InjectStreamingUIPress:
        /// - For click targets: send IPointerUpHandler + IPointerClickHandler.
        /// - For drag targets: send IEndDragHandler + IPointerUpHandler.
        /// </summary>
        private static void InjectStreamingUIRelease(float rawPixelX, float rawPixelY, int button)
        {
            if (button != 0) return;

            // Handle UI Toolkit (UIDocument) targets separately.
            if (s_StreamingIsUITKTarget)
            {
                try
                {
                    InjectStreamingUIToolkitRelease();
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogWarning($"[NWB-StreamUI-UITK] Release error: {ex.Message}");
                }
                finally
                {
                    s_StreamingIsUITKTarget = false;
                    s_StreamingUITKTarget = null;
                }
                return;
            }

            if (s_StreamingPressedTarget == null || s_StreamingPointerData == null) return;

            try
            {
                if (s_StreamingIsDragTarget)
                {
                    float ppp = EditorGUIUtility.pixelsPerPoint;
                    float screenX = RawPixelToScreenX(rawPixelX);
                    float screenY = RawPixelToScreenY(rawPixelY, ppp);
                    var newScreenPos = new Vector2(screenX, screenY);
                    s_StreamingPointerData.delta = newScreenPos - s_StreamingLastDragScreenPos;
                    s_StreamingPointerData.position = newScreenPos;

                    ExecuteEvents.Execute(s_StreamingPressedTarget,
                        s_StreamingPointerData, ExecuteEvents.endDragHandler);
                    ExecuteEvents.Execute(s_StreamingPressedTarget,
                        s_StreamingPointerData, ExecuteEvents.pointerUpHandler);

                    LogVerbose($"[NWB-StreamUI] Release+EndDrag " +
                        $"handler={s_StreamingPressedTarget.name}");
                }
                else
                {
                    ExecuteEvents.Execute(s_StreamingPressedTarget,
                        s_StreamingPointerData, ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.Execute(s_StreamingPressedTarget,
                        s_StreamingPointerData, ExecuteEvents.pointerClickHandler);

                    LogVerbose($"[NWB-StreamUI] Release+Click " +
                        $"handler={s_StreamingPressedTarget.name}");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-StreamUI] Release error: {ex.Message}");
            }
            finally
            {
                s_StreamingPressedTarget = null;
                s_StreamingPointerData = null;
                s_StreamingIsDragTarget = false;
            }
        }

        /// <summary>
        /// Inject a pointer-down into a UI Toolkit (UIDocument) panel.
        /// Finds all active UIDocuments, picks the VisualElement at the
        /// given screen position, and stores it for later release.
        /// Used as a fallback when EventSystem.RaycastAll (uGUI) returns
        /// no hits — i.e. the game UI uses UI Toolkit, not Canvas/Button.
        /// </summary>
#if UNITY_2021_2_OR_NEWER
        private static void InjectStreamingUIToolkitPress(float screenX, float screenY)
        {
            try
            {
#if UNITY_6000_5_OR_NEWER
                var docs = UnityEngine.Object.FindObjectsByType<UIDocument>();
#elif UNITY_2022_2_OR_NEWER
                var docs = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
#else
                var docs = UnityEngine.Object.FindObjectsOfType<UIDocument>();
#endif
                if (docs == null || docs.Length == 0)
                {
                    LogVerbose("[NWB-StreamUI-UITK] Press SKIP: no UIDocument found");
                    return;
                }

                foreach (var doc in docs)
                {
                    if (doc == null || doc.rootVisualElement == null)
                        continue;

                    var root = doc.rootVisualElement;

                    // Convert screen coordinates (bottom-left origin, Y up)
                    // to panel coordinates (top-left origin, Y down).
                    // Scale by panel size vs screen size to handle PanelSettings
                    // that use ScaleWithScreenSize with a reference resolution.
                    float panelW = root.layout.width;
                    float panelH = root.layout.height;
                    float panelX = screenX * panelW / Screen.width;
                    float panelY = (Screen.height - screenY) * panelH / Screen.height;
                    var panelPos = new Vector2(panelX, panelY);

                    var target = root.panel.Pick(panelPos);
                    if (target == null || target == root)
                        continue;

                    s_StreamingUITKTarget = target;
                    s_StreamingIsUITKTarget = true;

                    LogVerbose($"[NWB-StreamUI-UITK] Press hit={target.name} " +
                        $"panelPos=({panelX:F0},{panelY:F0}) " +
                        $"panelSize=({panelW:F0}x{panelH:F0}) " +
                        $"screenSize=({Screen.width}x{Screen.height})");
                    return;
                }

                LogVerbose($"[NWB-StreamUI-UITK] Press NO HIT " +
                    $"screenPos=({screenX:F0},{screenY:F0}) " +
                    $"Screen=({Screen.width},{Screen.height})");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-StreamUI-UITK] Press error: {ex.Message}");
            }
        }
#else
        private static void InjectStreamingUIToolkitPress(float screenX, float screenY)
        {
            // UIDocument is not available before Unity 2021.2.
        }
#endif

        /// <summary>
        /// Complete the UI Toolkit pointer interaction started by
        /// InjectStreamingUIToolkitPress. Walks up the hierarchy from
        /// the picked element to find a Button with a Clickable
        /// manipulator, then invokes the clicked delegate via reflection.
        /// This is necessary because:
        /// 1. PointerDownEvent/PointerUpEvent properties have internal setters
        /// 2. MouseDownEvent is not dispatched correctly via SendEvent
        /// 3. The Clickable manipulator's activation filter requires
        ///    IMouseEvent, which PointerDownEvent does not implement
        /// </summary>
        private static void InjectStreamingUIToolkitRelease()
        {
            if (s_StreamingUITKTarget == null) return;

            try
            {
                var target = s_StreamingUITKTarget;

                // Walk up the hierarchy to find a Clickable manipulator.
                // Games may attach Clickable to plain VisualElements (not just
                // Button) via AddManipulator(new Clickable(...)).
                Clickable clickable = null;
                VisualElement clickableOwner = null;
                var el = target;
                while (el != null)
                {
                    // Fast path: Button elements expose clickable directly
                    if (el is Button btn && btn.clickable != null)
                    {
                        clickable = btn.clickable;
                        clickableOwner = el;
                        break;
                    }

                    // Slow path: use reflection to check manipulators list
                    // for any Clickable manipulator on a plain VisualElement
                    var found = TryGetClickableFromElement(el);
                    if (found != null)
                    {
                        clickable = found;
                        clickableOwner = el;
                        break;
                    }
                    el = el.parent;
                }

                if (clickable != null)
                {
                    // Use SimulateSingleClick to fire the clicked callback.
                    // This is the most reliable approach because:
                    // 1. It handles both 'clicked' and 'clickedWithEventInfo' delegates
                    // 2. It works for both Button.clickable and plain VisualElement + Clickable
                    // 3. It avoids issues with MouseDownEvent not being dispatched via SendEvent
                    var simMethod = typeof(Clickable).GetMethod("SimulateSingleClick",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    if (simMethod != null)
                    {
                        simMethod.Invoke(clickable, new object[] { null, 0 });
                    }
                    else
                    {
                        // Fallback: invoke clicked delegate directly via reflection
                        var clickedField = typeof(Clickable).GetField("clicked",
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance);
                        if (clickedField != null)
                        {
                            var del = clickedField.GetValue(clickable) as System.Delegate;
                            if (del != null)
                            {
                                foreach (var d in del.GetInvocationList())
                                {
                                    try { d.DynamicInvoke(); }
                                    catch (Exception ex)
                                    {
                                        CodelyLogger.LogWarning(
                                            $"[NWB-StreamUI-UITK] clicked handler error: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }

                    LogVerbose($"[NWB-StreamUI-UITK] Release+Click " +
                        $"owner={clickableOwner.name} picked={target.name}");
                }
                else
                {
                    LogVerbose($"[NWB-StreamUI-UITK] Release: no Clickable found " +
                        $"in hierarchy of {target.name}");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-StreamUI-UITK] Release error: {ex.Message}");
            }
        }

        /// <summary>
        /// Use reflection to check if a VisualElement has a Clickable
        /// manipulator attached via AddManipulator. The manipulators are
        /// not stored in a direct list on VisualElement; instead, the
        /// Clickable registers event callbacks (OnMouseDown, OnMouseUp)
        /// on the target element. We access these callbacks through the
        /// CallbackEventHandler.m_CallbackRegistry field and inspect
        /// each callback's delegate Target to find a Clickable instance.
        /// </summary>
        private static Clickable TryGetClickableFromElement(VisualElement el)
        {
            try
            {
                // Access CallbackEventHandler.m_CallbackRegistry (base class of VisualElement)
                var registryField = typeof(CallbackEventHandler).GetField("m_CallbackRegistry",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (registryField == null) return null;

                var registry = registryField.GetValue(el);
                if (registry == null) return null;

                // Access EventCallbackRegistry.m_Callbacks (EventCallbackList)
                var cbsField = registry.GetType().GetField("m_Callbacks",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (cbsField == null) return null;

                var cbs = cbsField.GetValue(registry);
                if (cbs == null) return null;

                // Access EventCallbackList.m_List (List<EventCallbackFunctorBase>)
                var listField = cbs.GetType().GetField("m_List",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (listField == null) return null;

                var list = listField.GetValue(cbs) as System.Collections.IList;
                if (list == null) return null;

                // Iterate a copy to avoid collection-modified errors
                var count = list.Count;
                for (int i = 0; i < count; i++)
                {
                    var functor = list[i];
                    if (functor == null) continue;

                    // Access EventCallbackFunctor.m_Callback (the delegate)
                    var cbField = functor.GetType().GetField("m_Callback",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public);
                    if (cbField == null) continue;

                    var del = cbField.GetValue(functor) as System.Delegate;
                    if (del?.Target is Clickable c)
                        return c;
                }
            }
            catch (Exception ex)
            {
                LogVerbose($"[NWB-StreamUI-UITK] TryGetClickableFromElement error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Directly queue a mouse event for the game runtime via
        /// EditorGUIUtility.QueueGameViewInputEvent, bypassing GameView.OnGUI.
        ///
        /// GameView.OnGUI computes game-pixel coordinates as:
        ///   gameMousePosition = (editorMousePos + gameMouseOffset) * gameMouseScale
        /// where gameMouseOffset = -viewInWindow.position - targetInView.position
        /// (viewInWindow.position = chrome offset; targetInView.position = letterbox
        /// offset when using a fixed aspect ratio) and
        /// gameMouseScale = pixelsPerPoint / m_ZoomArea.scale.y.
        ///
        /// For our offscreen setup, the raw RT pixels include the tab bar,
        /// GameView toolbar, and letterbox bars. Subtracting all offsets and
        /// applying the letterbox scale factor gives coordinates relative to
        /// the game rendering area — exactly what the native
        /// QueueGameViewInputEvent expects.
        /// </summary>
        private static void QueuePlayModeMouseEvent(
            EventType eventType, float rawPixelX, float rawPixelY,
            int btn, EventModifiers mod, Vector2 delta = default)
        {
            float pppLocal = EditorGUIUtility.pixelsPerPoint;
            float offsetPixels = GetGameViewOffsetPixels(pppLocal);
            // Subtract chrome offset AND letterbox offset, then apply scale
            // to convert from display pixels to game render-target pixels.
            UpdateLetterboxCache();
            float gamePixelX = (rawPixelX - s_LetterboxLeftPx) * s_LetterboxScaleX;
            float gamePixelY = (rawPixelY - offsetPixels - s_LetterboxTopPx) * s_LetterboxScaleY;

            Event gameEvt = new Event
            {
                type = eventType,
                mousePosition = new Vector2(gamePixelX, gamePixelY),
                button = BrowserBtnToUnityBtn(btn),
                modifiers = mod,
                delta = delta,
                displayIndex = 0
            };
            EditorGUIUtility.QueueGameViewInputEvent(gameEvt);
        }

        /// <summary>
        /// Remap browser MouseEvent.button to Unity Event.button.
        /// Browser: 0=left, 1=middle, 2=right
        /// Unity:   0=left, 1=right,  2=middle
        /// Middle and right are swapped between the two systems.
        /// </summary>
        private static int BrowserBtnToUnityBtn(int browserBtn)
        {
            switch (browserBtn)
            {
                case 1: return 2; // browser middle → Unity middle
                case 2: return 1; // browser right  → Unity right
                default: return browserBtn;
            }
        }

#if UNITY_EDITOR_WIN
        /// <summary>
        /// Map browser KeyboardEvent.key names to Win32 Virtual Key codes.
        /// Returns 0 if the key is not mapped.
        /// </summary>
        private static ushort BrowserKeyToVK(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;

            if (key.Length == 1)
            {
                char c = key[0];
                if (c >= 'a' && c <= 'z') return (ushort)(c - 'a' + 0x41);
                if (c >= 'A' && c <= 'Z') return (ushort)c;
                if (c >= '0' && c <= '9') return (ushort)c;
                if (c == ' ') return 0x20; // VK_SPACE
                if (c == '-') return 0xBD; // VK_OEM_MINUS
                if (c == '=') return 0xBB; // VK_OEM_PLUS
                if (c == '[') return 0xDB; // VK_OEM_4
                if (c == ']') return 0xDD; // VK_OEM_6
                if (c == ';') return 0xBA; // VK_OEM_1
                if (c == ',') return 0xBC; // VK_OEM_COMMA
                if (c == '.') return 0xBE; // VK_OEM_PERIOD
                if (c == '/') return 0xBF; // VK_OEM_2
                if (c == '`') return 0xC0; // VK_OEM_3
                if (c == '\\') return 0xDC; // VK_OEM_5
                if (c == '\'') return 0xDE; // VK_OEM_7
            }

            switch (key)
            {
                case "Enter": case "Return": return 0x0D;
                case "Escape": return 0x1B;
                case "Tab": return 0x09;
                case "Backspace": return 0x08;
                case "Delete": return 0x2E;
                case "Insert": return 0x2D;
                case "Home": return 0x24;
                case "End": return 0x23;
                case "PageUp": return 0x21;
                case "PageDown": return 0x22;
                case "ArrowUp": return 0x26;
                case "ArrowDown": return 0x28;
                case "ArrowLeft": return 0x25;
                case "ArrowRight": return 0x27;
                case "Shift": return 0x10;
                case "Control": return 0x11;
                case "Alt": return 0x12;
                case "Meta": return 0x5B; // VK_LWIN
                case "CapsLock": return 0x14;
                case "Backquote": return 0xC0; // VK_OEM_3
                case "Backslash": return 0xDC; // VK_OEM_5
                case "Quote": return 0xDE; // VK_OEM_7
                case "F1": return 0x70; case "F2": return 0x71;
                case "F3": return 0x72; case "F4": return 0x73;
                case "F5": return 0x74; case "F6": return 0x75;
                case "F7": return 0x76; case "F8": return 0x77;
                case "F9": return 0x78; case "F10": return 0x79;
                case "F11": return 0x7A; case "F12": return 0x7B;
                default: return 0;
            }
        }
#endif
    }
}
