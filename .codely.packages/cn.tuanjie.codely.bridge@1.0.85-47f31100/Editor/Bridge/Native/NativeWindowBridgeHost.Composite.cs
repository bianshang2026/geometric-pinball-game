using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Native
{
    internal static partial class NativeWindowBridgeHost
    {
        private const string kCompositeSlotMappingKey = "NWB_CompositeSlotMapping";
        private const string kCompositePrePlayTabKey = "NWB_CompositePrePlayTab";
        private const string kCompositeRestoreJsonKey = "NWB_CompositeRestoreJson";
        private const string kCompositeRestoreFpsKey = "NWB_CompositeRestoreFps";
        private const string kDefaultCompositeLayoutPreset = "unity-default-layout";
        private const string kSavedCompositeLayoutPreset = "codely-saved-layout";

        // Saved when StartCompositeOffscreenCapture succeeds so domain reload
        // can auto-resume without waiting for the browser to re-send the layout.
        private static string s_LastCompositeRequestJson;

        private enum CompositeLayoutApplyMode
        {
            Auto = 0,
            AppendSingle = 1,
            StrictReplace = 2,
            PreserveExisting = 3,
            ReplaceManagedKeepNative = 4
        }

        [Serializable]
        private sealed class CompositeStreamRequest
        {
            public int fps = 30;
            public int width;
            public int height;
            public string layoutPreset;
            public string applyMode;
            public string focusedSlotId;
            public CompositeSlotRequest[] slots;
        }

        [Serializable]
        private sealed class CompositeSlotRequest
        {
            public string slotId;
            public string windowType;
            public string title;
            public CompositeRect rect;
        }

        [Serializable]
        private sealed class CompositeRect
        {
            public float x;
            public float y;
            public float w;
            public float h;
        }

        [Serializable]
        private sealed class CompositeSlotMappingEntry
        {
            public string slotId;
            public long instanceId;
            public string windowType;
            public CompositeRect rect;
        }

        [Serializable]
        private sealed class CompositeSlotMappingStore
        {
            public CompositeSlotMappingEntry[] entries;
        }

        private sealed class CompositeCaptureSlot
        {
            public string SlotId;
            public string WindowTypeName;
            public EditorWindow Window;
            public System.Type WindowType;
            public RenderTexture SourceRT;
            public Rect NormalizedRect;
            public bool OwnsWindow;
            public bool CloseWindowOnRelease;
#if UNITY_EDITOR_WIN
            // Win32 HWNDs for keyboard injection (SetFocus + PostMessageW).
            // Detected after Show() using the before/after HWND diff technique.
            public IntPtr ContainerHwnd;
            public IntPtr GUIViewHwnd;
#endif
#if UNITY_EDITOR_OSX
            // NSWindow pointer for keyboard injection (activateIgnoringOtherApps + makeKeyWindow).
            public IntPtr NSWindow;
#endif
        }

        private sealed class CompositeLayoutNode
        {
            public CompositeSlotRequest Slot;
            public CompositeLayoutNode First;
            public CompositeLayoutNode Second;
            public string Edge;
        }

        private sealed class CompositeLayoutOperation
        {
            public string Preset;
            public string HierarchySlotId;
            public string GameSlotId;
            public string SceneSlotId;
            public string ProjectSlotId;
            public string ConsoleSlotId;
            public string InspectorSlotId;
            public int Step;
            public int WaitFrames;
            public int Attempts;
        }

        private static bool s_CompositeActive;
        private static readonly Dictionary<string, CompositeCaptureSlot> s_CompositeSlots =
            new Dictionary<string, CompositeCaptureSlot>();
        private static RenderTexture s_CompositeRT;
        private static RenderTexture s_CompositePaneDragRT;
        private static Texture2D s_CompositeReadbackTex;
        private static int s_CompositeLogicalWidth;
        private static int s_CompositeLogicalHeight;
        private static Rect s_CompositeFrameBoundsLogical = Rect.zero;
        private static Vector2 s_CompositeFrameOffsetPixels = Vector2.zero;
        private static Vector2 s_CompositeFrameScalePixels = Vector2.one;
        private static bool s_CompositeWorkspaceReflowPending;
#pragma warning disable CS0414
        private static int s_CompositeLastViewCount;
#pragma warning restore CS0414
        private static int s_CompositeSkipFrames;
        private static CompositeLayoutOperation s_CompositeLayoutOperation;
#pragma warning disable CS0414
        private static bool s_CompositePlayModeInitDone;
        private static int s_CompositeForceRepaintFrames;
#pragma warning restore CS0414
        private static object s_CompositeMouseCaptureView;
        private static CompositeCaptureSlot s_CompositeMouseCaptureSlot;
        private static EditorWindow s_CompositeLastTabTarget;

        /// <summary>
        /// Save composite slot mappings so surviving windows can be rediscovered
        /// after domain reload without closing them.
        /// </summary>
        private static void SaveCompositeSlotMapping()
        {
            if (s_CompositeSlots == null || s_CompositeSlots.Count == 0)
            {
                SessionState.EraseString(kCompositeSlotMappingKey);
                return;
            }

            var entries = new List<CompositeSlotMappingEntry>();
            foreach (var kvp in s_CompositeSlots)
            {
                var slot = kvp.Value;
                if (slot?.Window == null) continue;
                entries.Add(new CompositeSlotMappingEntry
                {
                    slotId = kvp.Key,
                    instanceId = slot.Window.GetStableInstanceId(),
                    windowType = slot.WindowTypeName ?? string.Empty,
                    rect = new CompositeRect
                    {
                        x = slot.NormalizedRect.x,
                        y = slot.NormalizedRect.y,
                        w = slot.NormalizedRect.width,
                        h = slot.NormalizedRect.height
                    }
                });
            }
            if (entries.Count == 0)
            {
                SessionState.EraseString(kCompositeSlotMappingKey);
                return;
            }

            string json = JsonUtility.ToJson(new CompositeSlotMappingStore { entries = entries.ToArray() });
            SessionState.SetString(kCompositeSlotMappingKey, json);
            LogVerbose($"[NWB-Composite] Saved slot mapping ({entries.Count} slots) before reload");
        }

        /// <summary>
        /// After domain reload, rediscover surviving EditorWindows from the
        /// saved mapping and pre-populate s_CompositeSlots.
        /// </summary>
        private static void PrePopulateCompositeSlots()
        {
            string json = SessionState.GetString(kCompositeSlotMappingKey, "");
            SessionState.EraseString(kCompositeSlotMappingKey);
            if (string.IsNullOrEmpty(json))
                return;

            CompositeSlotMappingStore store = null;
            try
            {
                store = JsonUtility.FromJson<CompositeSlotMappingStore>(json);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Composite] Failed to parse slot mapping: {ex.Message}");
                return;
            }
            if (store?.entries == null || store.entries.Length == 0)
                return;

            int restored = 0;
            foreach (var entry in store.entries)
            {
                if (entry == null) continue;
                string slotId = entry.slotId;
                string typeName = entry.windowType;
                long instanceId = entry.instanceId;
                if (string.IsNullOrEmpty(slotId) || instanceId == 0)
                    continue;

                var obj = InstanceIdExtensions.InstanceIdToObject(instanceId);
                var window = obj as EditorWindow;
                if (window == null)
                {
                    LogVerbose($"[NWB-Composite] Slot '{slotId}' window id={instanceId} did not survive reload");
                    continue;
                }

                var slot = new CompositeCaptureSlot
                {
                    SlotId = slotId,
                    WindowTypeName = typeName,
                    WindowType = window.GetType(),
                    Window = window,
                    NormalizedRect = ToUnityRect(entry.rect),
                    OwnsWindow = true,
                    CloseWindowOnRelease = false
                };
                s_CompositeSlots[slotId] = slot;
                restored++;
            }
            if (restored > 0)
                LogVerbose($"[NWB-Composite] Pre-populated {restored} slots from surviving windows");
        }

        private static Rect ToUnityRect(CompositeRect rect)
        {
            if (rect == null)
                return new Rect(0f, 0f, 1f, 1f);
            return new Rect(
                rect.x,
                rect.y,
                Mathf.Max(0.01f, rect.w),
                Mathf.Max(0.01f, rect.h));
        }

        /// <summary>
        /// Find an existing DockArea from already-created composite slots.
        /// Used by CreateCompositeSlot to dock new views immediately.
        /// </summary>
        private static object FindExistingCompositeDockArea()
        {
            foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
            {
                if (slot?.Window == null) continue;
                object view = GetParentView(slot.Window);
                if (view != null && view.GetType().Name == "DockArea")
                    return view;
            }
            return null;
        }

        private static bool StartCompositeOffscreenCapture(string requestJson, int fps, int width, int height,
            bool nativeAlreadyStarted = false,
            CompositeLayoutApplyMode applyMode = CompositeLayoutApplyMode.Auto)
        {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
            if (!IsAvailable()) return false;

            try
            {
                CompositeStreamRequest request = JsonUtility.FromJson<CompositeStreamRequest>(requestJson);
                if (request == null || request.slots == null || request.slots.Length == 0)
                {
                    CodelyLogger.LogError("[NWB-Composite] Empty composite layout request");
                    return false;
                }

                int logicalW = Mathf.Max((width > 0 ? width : request.width), 320);
                int logicalH = Mathf.Max((height > 0 ? height : request.height), 240);
                fps = Mathf.Max(1, fps > 0 ? fps : request.fps);
                CompositeLayoutApplyMode resolvedApplyMode = ResolveCompositeLayoutApplyMode(request, applyMode);
                bool samePresetDetachedReconnect = ShouldPreserveCompositeLayoutForSamePreset(request);
                if (samePresetDetachedReconnect)
                {
                    bool canKeepPreserve = TryNormalizeSlotIdsForPresetReuse(request) &&
                                           TryEnsurePresetLayoutConsistencyWithoutRebuild(request);
                    if (canKeepPreserve)
                    {
                        resolvedApplyMode = CompositeLayoutApplyMode.PreserveExisting;
                        LogVerbose($"[NWB-Composite] Same preset detached reconnect, preserve existing layout: preset={request.layoutPreset} slots={s_CompositeSlots.Count}");
                    }
                    else
                    {
                        // Rect consistency check failed and in-place tree convergence
                        // could not be completed. Fall back to normal request-driven
                        // rebuild path (strict/replace modes) for recovery.
                        samePresetDetachedReconnect = false;
                        resolvedApplyMode = ResolveCompositeLayoutApplyMode(request, applyMode);
                        LogVerbose($"[NWB-Composite] Same preset detached reconnect fallback to rebuild: preset={request.layoutPreset} mode={resolvedApplyMode}");
                    }
                }
                LogVerbose($"[NWB-Composite] Apply mode: {resolvedApplyMode}, requested slots={request.slots.Length}, current slots={s_CompositeSlots.Count}, focusedSlotId={request.focusedSlotId}");

                // Switch cleanly from single-window offscreen to composite mode.
                if (s_OffscreenActive && !s_CompositeActive && s_OffscreenTarget != null)
                {
                    if (s_OffscreenAutoFloatingWindow)
                    {
                        try { s_OffscreenTarget.Close(); }
                        catch (Exception ex) { CodelyLogger.LogWarning($"[NWB-Composite] Close old single target failed: {ex.Message}"); }
                    }
                    s_OffscreenTarget = null;
                    s_OffscreenTargetType = null;
                    s_OffscreenAutoFloatingWindow = false;
                    s_OffscreenFloatingWindowInstanceId = 0;
                }

                EnsureCompositeRenderTargets(logicalW, logicalH);

                // Close existing docked GameView(s) BEFORE ApplyCompositeLayout creates
                // the floating slot windows. The docked GameView's size info
                // (PanelSettings.GetGameViewResolution) interferes with the floating
                // GameView's PanelSettings.m_TargetRect if still present during Show().
                // Skip in PreserveExisting mode (domain reload resume) — the floating
                // GameView already survived the reload.
                if (!s_ClosedDockedGameViewForStreaming && resolvedApplyMode != CompositeLayoutApplyMode.PreserveExisting)
                {
                    bool hasGameViewSlot = false;
                    if (request.slots != null)
                    {
                        foreach (var slotReq in request.slots)
                        {
                            if (slotReq != null && !string.IsNullOrEmpty(slotReq.windowType)
                                && slotReq.windowType.Contains("GameView"))
                            {
                                hasGameViewSlot = true;
                                break;
                            }
                        }
                    }
                    if (hasGameViewSlot)
                        CloseExistingGameViewsForStreaming(null);
                }

                ApplyCompositeLayout(request, logicalW, logicalH, resolvedApplyMode);
                if (resolvedApplyMode != CompositeLayoutApplyMode.PreserveExisting)
                    TryActivateCompositeSlot(request.focusedSlotId);

                int physW = (Mathf.RoundToInt(logicalW * DPIScale) + 1) & ~1;
                int physH = (Mathf.RoundToInt(logicalH * DPIScale) + 1) & ~1;

                if (!nativeAlreadyStarted)
                {
                    int nativeResult = NativeWindowBridgeAPI.NWB_StartOffscreenCapture(fps, physW, physH);
                    if (nativeResult != 1)
                    {
                        CodelyLogger.LogError("[NWB-Composite] Native StartOffscreenCapture failed");
                        return false;
                    }
                }

                bool wasAlreadyComposite = s_CompositeActive;
                s_CompositeActive = true;
                s_OffscreenActive = true;
                s_UseRenderTextureFallback =
                    (PlayerSettings.colorSpace == ColorSpace.Gamma);
                if (s_UseRenderTextureFallback)
                    CodelyLogger.Log("[NWB-Diag] Gamma color space detected — using PrintWindow fallback (no AuxBackBufferManager)");
                s_OffscreenFps = fps;
                if (!wasAlreadyComposite)
                {
                    s_OffscreenFrameCount = 0;
                    s_CompositeLastViewCount = 0;
                }
                s_CompositeSkipFrames = 0;
                s_OffscreenTarget = null;
                s_OffscreenTargetType = null;
                s_NextCaptureTime = 0;
                s_HasFrontendHeartbeat = false;
                s_LastFrontendHeartbeatTime = EditorApplication.timeSinceStartup;
                RegisterOffscreenLoop();
                RegisterSelectionChangeListener();

                MakeAllWindowsTransparent();
                DeactivateUnityKeepWindows();
#if UNITY_EDITOR_OSX
                ShowStreamingMaskWindow();
#endif
#if UNITY_EDITOR_WIN
                DeferStreamingMaskWindow();
#endif
                InitCompositeWindowTracking();

                // Cache the request JSON for domain reload auto-resume.
                s_LastCompositeRequestJson = requestJson;

                CodelyLogger.Log($"[NWB-Composite] Started slots={s_CompositeSlots.Count} canvas={logicalW}x{logicalH} phys={physW}x{physH} @{fps}fps nativeSkipped={nativeAlreadyStarted}");
                return true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"[NWB-Composite] Start failed: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
#else
            return false;
#endif
        }

        private static void EnsureCompositeRenderTargets(int logicalW, int logicalH)
        {
            logicalW = Mathf.Max((logicalW + 1) & ~1, 2);
            logicalH = Mathf.Max((logicalH + 1) & ~1, 2);
            int physW = (Mathf.RoundToInt(logicalW * DPIScale) + 1) & ~1;
            int physH = (Mathf.RoundToInt(logicalH * DPIScale) + 1) & ~1;

#if UNITY_EDITOR_WIN
            var rtReadWrite = RenderTextureReadWrite.Linear;
            bool texLinear = true;
#else
            var rtReadWrite = RenderTextureReadWrite.Default;
            bool texLinear = false;
#endif
            if (s_CompositeRT == null || s_CompositeRT.width != physW || s_CompositeRT.height != physH)
            {
                if (s_CompositeRT != null)
                {
                    s_CompositeRT.Release();
                    UnityEngine.Object.DestroyImmediate(s_CompositeRT);
                }
                s_CompositeRT = new RenderTexture(physW, physH, 0, RenderTextureFormat.BGRA32, rtReadWrite)
                {
                    name = "__NWB_CompositeRT__",
                    useMipMap = false,
                    autoGenerateMips = false
                };
                s_CompositeRT.Create();
            }

            if (s_CompositeReadbackTex == null || s_CompositeReadbackTex.width != physW || s_CompositeReadbackTex.height != physH)
            {
                if (s_CompositeReadbackTex != null)
                    UnityEngine.Object.DestroyImmediate(s_CompositeReadbackTex);
                s_CompositeReadbackTex = new Texture2D(physW, physH, TextureFormat.BGRA32, false, texLinear);
            }

            s_CompositeLogicalWidth = logicalW;
            s_CompositeLogicalHeight = logicalH;
        }

        private static CompositeLayoutApplyMode ResolveCompositeLayoutApplyMode(
            CompositeStreamRequest request, CompositeLayoutApplyMode requestedMode)
        {
            if (requestedMode != CompositeLayoutApplyMode.Auto)
                return requestedMode;

            string requested = request?.applyMode ?? string.Empty;
            if (string.Equals(requested, "append_single", StringComparison.OrdinalIgnoreCase))
                return CompositeLayoutApplyMode.AppendSingle;
            if (string.Equals(requested, "strict_replace", StringComparison.OrdinalIgnoreCase))
                return CompositeLayoutApplyMode.StrictReplace;
            if (string.Equals(requested, "preserve_existing", StringComparison.OrdinalIgnoreCase))
                return CompositeLayoutApplyMode.PreserveExisting;
            if (string.Equals(requested, "replace_managed_keep_native", StringComparison.OrdinalIgnoreCase))
                return CompositeLayoutApplyMode.ReplaceManagedKeepNative;

            int validCount = 0;
            if (request?.slots != null)
            {
                foreach (CompositeSlotRequest slot in request.slots)
                {
                    if (slot != null && !string.IsNullOrEmpty(slot.windowType))
                        validCount++;
                }
            }

            // UX rule:
            // - Single-slot request means incremental add/update; keep existing slots.
            // - Multi-slot request means strict layout snapshot; replace by request.
            return validCount <= 1
                ? CompositeLayoutApplyMode.AppendSingle
                : CompositeLayoutApplyMode.StrictReplace;
        }

        private static string GetLastCompositeLayoutPreset()
        {
            if (string.IsNullOrEmpty(s_LastCompositeRequestJson))
                return string.Empty;
            try
            {
                CompositeStreamRequest last = JsonUtility.FromJson<CompositeStreamRequest>(s_LastCompositeRequestJson);
                return last?.layoutPreset ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool ShouldPreserveCompositeLayoutForSamePreset(CompositeStreamRequest request)
        {
            if (!s_NativeOffscreenDisconnectedAwaitingRestore)
                return false;
            if (s_CompositeSlots == null || s_CompositeSlots.Count == 0)
                return false;

            string preset = request?.layoutPreset ?? string.Empty;
            if (string.IsNullOrEmpty(preset))
                return false;
            if (!string.Equals(preset, kDefaultCompositeLayoutPreset, StringComparison.Ordinal) &&
                !string.Equals(preset, kSavedCompositeLayoutPreset, StringComparison.Ordinal))
                return false;

            string lastPreset = GetLastCompositeLayoutPreset();
            if (!string.Equals(lastPreset, preset, StringComparison.Ordinal))
                return false;

            // Preserve only when current live slots can satisfy the full incoming
            // layout request. If user closed a view (e.g. default 6-slot -> 5-slot),
            // we must rebuild missing slots instead of preserving the incomplete state.
            return HasRequiredRequestedWindowTypesForPreserve(request);
        }

        private static bool HasRequiredRequestedWindowTypesForPreserve(CompositeStreamRequest request)
        {
            if (request?.slots == null || request.slots.Length == 0)
                return false;

            var required = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (CompositeSlotRequest slot in request.slots)
            {
                string type = slot?.windowType;
                if (string.IsNullOrEmpty(type))
                    continue;
                if (!required.TryGetValue(type, out int count))
                    count = 0;
                required[type] = count + 1;
            }
            if (required.Count == 0)
                return false;

            var available = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
            {
                if (slot?.Window == null)
                    continue;
                string type = slot.WindowTypeName;
                if (string.IsNullOrEmpty(type))
                    continue;
                if (!available.TryGetValue(type, out int count))
                    count = 0;
                available[type] = count + 1;
            }

            foreach (var kvp in required)
            {
                if (!available.TryGetValue(kvp.Key, out int got) || got < kvp.Value)
                    return false;
            }
            return true;
        }

        private static bool TryNormalizeSlotIdsForPresetReuse(CompositeStreamRequest request)
        {
            if (!s_NativeOffscreenDisconnectedAwaitingRestore || request?.slots == null)
                return true;

            var usedExistingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (CompositeSlotRequest req in request.slots)
            {
                string incomingId = string.IsNullOrEmpty(req?.slotId) ? req?.windowType : req.slotId;
                if (string.IsNullOrEmpty(incomingId) || req == null)
                    continue;
                if (s_CompositeSlots.ContainsKey(incomingId))
                {
                    usedExistingIds.Add(incomingId);
                    continue;
                }

                string remapFromId = null;
                foreach (var kvp in s_CompositeSlots)
                {
                    if (usedExistingIds.Contains(kvp.Key))
                        continue;
                    CompositeCaptureSlot candidate = kvp.Value;
                    if (candidate?.Window == null)
                        continue;
                    if (!string.Equals(candidate.WindowTypeName, req.windowType, StringComparison.Ordinal))
                        continue;
                    remapFromId = kvp.Key;
                    break;
                }
                if (string.IsNullOrEmpty(remapFromId))
                    continue;

                CompositeCaptureSlot slot = s_CompositeSlots[remapFromId];
                s_CompositeSlots.Remove(remapFromId);
                slot.SlotId = incomingId;
                s_CompositeSlots[incomingId] = slot;
                usedExistingIds.Add(incomingId);
                LogVerbose($"[NWB-Composite] Normalize preset reuse slotId: '{remapFromId}' -> '{incomingId}' ({req.windowType})");
            }
            return true;
        }

        private static bool TryEnsurePresetLayoutConsistencyWithoutRebuild(CompositeStreamRequest request)
        {
            if (!TryBuildPresetTargetSlotsForConsistency(request, out List<CompositeSlotRequest> targetSlots))
                return false;

            if (VerifySavedCompositeLayoutRects(targetSlots, 0.03f, out float rectError))
            {
                LogVerbose($"[NWB-Composite] Preset layout rects already aligned (maxError={rectError:F4})");
                return true;
            }

            LogVerbose($"[NWB-Composite] Preset layout rect mismatch (maxError={rectError:F4}), trying in-place tree convergence");
            if (!TryApplySavedCompositeLayoutTree(targetSlots))
                return false;
            CloseOrphanEmptyContainerWindows();

            bool alignedAfter = VerifySavedCompositeLayoutRects(targetSlots, 0.03f, out float afterError);
            if (!alignedAfter)
                CodelyLogger.LogWarning($"[NWB-Composite] In-place preset tree convergence did not reach tolerance (maxError={afterError:F4})");
            else
                LogVerbose($"[NWB-Composite] In-place preset tree convergence succeeded (maxError={afterError:F4})");
            return alignedAfter;
        }

        private static bool TryBuildPresetTargetSlotsForConsistency(
            CompositeStreamRequest request,
            out List<CompositeSlotRequest> targetSlots)
        {
            targetSlots = null;
            string preset = request?.layoutPreset ?? string.Empty;
            if (string.IsNullOrEmpty(preset))
                return false;

            if (string.Equals(preset, kSavedCompositeLayoutPreset, StringComparison.Ordinal))
            {
                targetSlots = GetValidCompositeLayoutSlots(request);
                return targetSlots != null && targetSlots.Count > 0;
            }

            if (!string.Equals(preset, kDefaultCompositeLayoutPreset, StringComparison.Ordinal))
                return false;

            if (!TryResolveDefaultLayoutSlotIds(request,
                out string hierarchySlotId,
                out string sceneSlotId,
                out string gameSlotId,
                out string projectSlotId,
                out string consoleSlotId,
                out string inspectorSlotId))
                return false;

            targetSlots = BuildDefaultCompositeLayoutSlots(
                request,
                hierarchySlotId,
                sceneSlotId,
                gameSlotId,
                projectSlotId,
                consoleSlotId,
                inspectorSlotId);
            return targetSlots != null && targetSlots.Count > 0;
        }

        private static bool IsNativeTrackedCompositeSlot(string slotId)
        {
            return !string.IsNullOrEmpty(slotId) &&
                   slotId.StartsWith("native-", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// In detached-hidden state, frontend can re-generate slotId on each restart
        /// (for example using timestamp suffixes). Rebind the incoming slotId to an
        /// existing slot with the same window type so we can reuse the old floating
        /// window instead of creating a new one.
        /// </summary>
        private static CompositeCaptureSlot TryRemapDetachedCompositeSlot(string incomingSlotId,
            string windowTypeName, HashSet<string> wanted, int logicalW, int logicalH, CompositeRect requestedRect)
        {
            if (!s_NativeOffscreenDisconnectedAwaitingRestore || string.IsNullOrEmpty(windowTypeName))
                return null;

            string reusableSlotId = null;
            CompositeCaptureSlot reusableSlot = null;
            foreach (var kvp in s_CompositeSlots)
            {
                if (wanted.Contains(kvp.Key))
                    continue;

                CompositeCaptureSlot candidate = kvp.Value;
                if (candidate?.Window == null)
                    continue;
                if (!string.Equals(candidate.WindowTypeName, windowTypeName, StringComparison.Ordinal))
                    continue;
                if (!IsDetachedRemapSizeCompatible(candidate, logicalW, logicalH, requestedRect, out string reason))
                {
                    LogVerbose($"[NWB-Composite] Skip detached remap candidate '{kvp.Key}' ({windowTypeName}): {reason}");
                    continue;
                }

                reusableSlotId = kvp.Key;
                reusableSlot = candidate;
                break;
            }

            if (reusableSlot == null)
                return null;

            if (!string.Equals(reusableSlotId, incomingSlotId, StringComparison.Ordinal))
            {
                s_CompositeSlots.Remove(reusableSlotId);
                reusableSlot.SlotId = incomingSlotId;
                s_CompositeSlots[incomingSlotId] = reusableSlot;
                LogVerbose($"[NWB-Composite] Reuse detached slot by windowType: '{reusableSlotId}' -> '{incomingSlotId}' ({windowTypeName})");
            }
            else
            {
                LogVerbose($"[NWB-Composite] Reuse detached slot: '{incomingSlotId}' ({windowTypeName})");
            }

            return reusableSlot;
        }

        private static bool IsDetachedRemapSizeCompatible(
            CompositeCaptureSlot candidate,
            int logicalW,
            int logicalH,
            CompositeRect requestedRect,
            out string reason)
        {
            reason = string.Empty;
            if (candidate?.Window == null)
            {
                reason = "candidate window is null";
                return false;
            }

            Rect nr = ClampCompositeRect(requestedRect);
            float expectedLogicalW = Mathf.Max(logicalW * nr.width, 1f);
            float expectedLogicalH = Mathf.Max(logicalH * nr.height, 1f);

            object parentView = GetParentView(candidate.Window);
            if (parentView == null)
            {
                reason = "parent view is missing";
                return false;
            }

            Rect viewRect = GetViewScreenPosition(parentView);
            if (viewRect.width <= 1f || viewRect.height <= 1f)
            {
                reason = $"invalid view size {viewRect.width:F1}x{viewRect.height:F1}";
                return false;
            }

            float expectedW = Mathf.Max(expectedLogicalW * LocalPP, 1f);
            float expectedH = Mathf.Max(expectedLogicalH * LocalPP, 1f);
            float actualW = Mathf.Max(viewRect.width * LocalPP, 1f);
            float actualH = Mathf.Max(viewRect.height * LocalPP, 1f);
            float widthTolerance = Mathf.Max(96f, expectedW * 0.25f);
            float heightTolerance = Mathf.Max(96f, expectedH * 0.25f);

            bool widthOk = Mathf.Abs(actualW - expectedW) <= widthTolerance;
            bool heightOk = Mathf.Abs(actualH - expectedH) <= heightTolerance;
            if (widthOk && heightOk)
                return true;

            reason = $"size mismatch actual={actualW:F0}x{actualH:F0} expected={expectedW:F0}x{expectedH:F0}";
            return false;
        }

        private static void ApplyCompositeLayout(CompositeStreamRequest request, int logicalW, int logicalH,
            CompositeLayoutApplyMode applyMode)
        {
            bool preserveExisting = applyMode == CompositeLayoutApplyMode.PreserveExisting;
            bool strictReplace = applyMode == CompositeLayoutApplyMode.StrictReplace;
            bool replaceManagedKeepNative = applyMode == CompositeLayoutApplyMode.ReplaceManagedKeepNative;
            bool forcePresetLayout = string.Equals(request?.layoutPreset, kDefaultCompositeLayoutPreset, StringComparison.Ordinal) ||
                                     string.Equals(request?.layoutPreset, kSavedCompositeLayoutPreset, StringComparison.Ordinal);
            // Only reuse detached slot ids for the single-slot reconnect path.
            // Multi-slot (especially default/saved preset) should rebuild against
            // the incoming layout snapshot to avoid stale docking topology.
            bool allowDetachedSlotRemap = s_NativeOffscreenDisconnectedAwaitingRestore &&
                                          request?.slots != null &&
                                          request.slots.Length == 1;
            if (preserveExisting)
            {
                // Preserve mode keeps existing docking topology and windows; re-acquire
                // handles for all alive slots even when incoming slotIds changed.
                foreach (CompositeCaptureSlot existing in s_CompositeSlots.Values)
                {
                    if (existing?.Window != null)
                        ReacquireCompositeSlotHandles(existing);
                }
            }
            var wanted = new HashSet<string>();
            foreach (CompositeSlotRequest slotRequest in request.slots)
            {
                if (slotRequest == null || string.IsNullOrEmpty(slotRequest.windowType))
                    continue;

                string slotId = string.IsNullOrEmpty(slotRequest.slotId)
                    ? slotRequest.windowType
                    : slotRequest.slotId;
                wanted.Add(slotId);

                if (s_CompositeSlots.TryGetValue(slotId, out CompositeCaptureSlot slot) &&
                    slot.Window == null)
                {
                    ReleaseCompositeSlot(slot);
                    s_CompositeSlots.Remove(slotId);
                    slot = null;
                }

                if (preserveExisting)
                {
                    // During domain-reload resume, keep current windows/rects unchanged.
                    // Only re-acquire native handles for slots that already exist.
                    if (slot != null)
                        ReacquireCompositeSlotHandles(slot);
                    continue;
                }

                if (slot == null)
                {
                    if (allowDetachedSlotRemap)
                        slot = TryRemapDetachedCompositeSlot(slotId, slotRequest.windowType, wanted, logicalW, logicalH, slotRequest.rect);
                }

                if (slot == null)
                {
                    slot = CreateCompositeSlot(slotId, slotRequest.windowType, logicalW, logicalH, slotRequest.rect);
                    if (slot == null) continue;
                    s_CompositeSlots[slotId] = slot;
                }
                else
                {
                    slot.WindowTypeName = slotRequest.windowType;
                    ReacquireCompositeSlotHandles(slot);
                    ResizeCompositeSlot(slot, logicalW, logicalH, slotRequest.rect);
                }

                slot.NormalizedRect = ClampCompositeRect(slotRequest.rect);
                slot.Window?.Repaint();
            }

            if (strictReplace || replaceManagedKeepNative)
            {
                var removeIds = new List<string>();
                foreach (string id in s_CompositeSlots.Keys)
                {
                    if (!wanted.Contains(id))
                    {
                        if (replaceManagedKeepNative && IsNativeTrackedCompositeSlot(id))
                        {
                            LogVerbose($"[NWB-Composite] Keep native slot '{id}' in ReplaceManagedKeepNative");
                            continue;
                        }
                        removeIds.Add(id);
                    }
                }
                foreach (string id in removeIds)
                {
                    LogVerbose($"[NWB-Composite] Remove slot '{id}' in mode={applyMode}");
                    ReleaseCompositeSlot(s_CompositeSlots[id]);
                    s_CompositeSlots.Remove(id);
                }
            }

            if (!preserveExisting)
            {
                ResizeCompositeWorkspaceToCanvas();
                // Preset layout (default/saved) must always converge to the
                // preset geometry, even when the request uses a non-strict
                // applyMode such as ReplaceManagedKeepNative.
                if (strictReplace || forcePresetLayout)
                    ScheduleCompositeLayoutPreset(request);
            }
        }

        private static CompositeCaptureSlot CreateCompositeSlot(string slotId, string windowTypeName, int logicalW, int logicalH, CompositeRect rect)
        {
            System.Type windowType = ResolveEditorWindowType(windowTypeName);
            if (windowType == null)
            {
                CodelyLogger.LogError($"[NWB-Composite] Window type not found: {windowTypeName}");
                return null;
            }

            EditorWindow reference = null;
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (w != null && w.GetType() == windowType)
                {
                    reference = w;
                    break;
                }
            }
            bool shouldReuseExistingWindow = reference != null && ShouldReuseExistingCompositeWindow(windowTypeName);
            // Do NOT call GetWindow for types we will create floating copies of (e.g. GameView).
            // GetWindow creates a docked window whose size info (PanelSettings.
            // GetGameViewResolution) interferes with the floating copy's texture config.
            if (reference == null && !shouldReuseExistingWindow)
            {
                if (ShouldReuseExistingCompositeWindow(windowTypeName))
                    reference = EditorWindow.GetWindow(windowType, false, null, false);
            }

            EditorWindow floating = shouldReuseExistingWindow
                ? reference
                : ScriptableObject.CreateInstance(windowType) as EditorWindow;
            if (floating == null)
            {
                CodelyLogger.LogError($"[NWB-Composite] Failed to create floating window: {windowTypeName}");
                return null;
            }

            Rect nr = ClampCompositeRect(rect);
            float slotW = Mathf.Max(logicalW * DpiScaleFactor, 320f * DpiScaleFactor);
            float slotH = Mathf.Max(logicalH * DpiScaleFactor - s_TabBarOffsetY, 240f * DpiScaleFactor);
            Rect refPos = reference != null ? reference.position : new Rect(100, 100, slotW, slotH);

            // When composite is already active, existing reference windows may
            // be hidden off-screen at (-32000, -32000) physical pixels.
            // ContainerWindow.position uses DPI-scaled logical coordinates, so
            // at 200% DPI the position appears as ~(-16000, -16000).
            // Use -10000 as a safe universal off-screen threshold.
            bool refOffScreen = (refPos.x <= -10000f || refPos.y <= -10000f);
            if (refOffScreen)
                refPos = new Rect(100, 100, slotW, slotH);

            // Add subsequent slots as tabs in the existing DockArea. If a
            // layout preset is active, an EditorApplication.update operation
            // later splits those tabs after Unity has finished layout/repaint.
            object existingDockArea = FindExistingCompositeDockArea();
            bool addedDirectly = false;
            bool shouldAddAsTab = existingDockArea != null && !shouldReuseExistingWindow;
#if UNITY_EDITOR_WIN
            HashSet<IntPtr> beforeShowHwnds = null;
#endif
#if UNITY_EDITOR_OSX
            HashSet<IntPtr> beforeShowNSWindows = null;
#endif

            if (shouldAddAsTab)
            {
                try
                {
                    System.Type dockAreaType = existingDockArea.GetType();
                    MethodInfo addTab = dockAreaType.GetMethod("AddTab",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new System.Type[] { typeof(EditorWindow), typeof(bool) },
                        null);
                    if (addTab != null)
                    {
                        addTab.Invoke(existingDockArea, new object[] { floating, false });
                        addedDirectly = true;
                        LogVerbose($"[NWB-Composite] AddTab {windowTypeName} directly to existing DockArea (default tab mode)");
                    }
                }
                catch (Exception ex)
                {
                    string innerMsg = ex.InnerException?.Message ?? ex.Message;
                    CodelyLogger.LogWarning($"[NWB-Composite] Direct AddTab failed for {windowTypeName}: {innerMsg}, falling back to Show()");
                }
            }

            if (!addedDirectly)
            {
#if UNITY_EDITOR_WIN
                beforeShowHwnds = new HashSet<IntPtr>(GetAllUnityHwnds());
#endif
#if UNITY_EDITOR_OSX
                beforeShowNSWindows = new HashSet<IntPtr>(GetAllNSWindows());
#endif
                if (!shouldReuseExistingWindow)
                {
                    floating.position = new Rect(refPos.x + 30f, refPos.y + 30f, slotW, slotH);
                    floating.Show();
                    floating.position = new Rect(refPos.x + 30f, refPos.y + 30f, slotW, slotH);
                }
                // Force initial IMGUI Layout+Repaint for the first standalone window.
                // Initializes GPU AuxBackBuffer and IMGUI state so the DWM
                // redirect surface has renderable content for PrintWindow capture.
                ForceDoubleLayoutRepaint(floating);
                floating.Repaint();
            }

            // Enable autoRepaintOnSceneChange for all composite windows (tabbed
            // or independent). This ensures RepaintViews(true) resets
            // m_CanBlitPaint=false, forcing full DoPaint renders.
            floating.autoRepaintOnSceneChange = true;

            var slot = new CompositeCaptureSlot
            {
                SlotId = slotId,
                WindowTypeName = windowTypeName,
                WindowType = windowType,
                Window = floating,
                OwnsWindow = !shouldReuseExistingWindow,
                CloseWindowOnRelease = shouldReuseExistingWindow,
                NormalizedRect = nr
            };
#if UNITY_EDITOR_WIN
            if (shouldReuseExistingWindow || addedDirectly)
            {
                // Window shares ContainerWindow with existing DockArea
                slot.ContainerHwnd = Cn.Tuanjie.Codely.Editor.NativeWindowHelper.GetHWND(floating);
                if (slot.ContainerHwnd != IntPtr.Zero)
                {
                    IntPtr child = GetWindow(slot.ContainerHwnd, GW_CHILD);
                    if (child != IntPtr.Zero)
                        slot.GUIViewHwnd = child;
                    LogVerbose($"[NWB-Composite] {(addedDirectly ? "Tabbed" : "Reused")} slot '{slotId}' HWND=0x{slot.ContainerHwnd.ToInt64():X}, GUIView=0x{(child != IntPtr.Zero ? child.ToInt64() : 0):X}");
                }
            }
            else
            {
                var afterShowHwnds = GetAllUnityHwnds();
                foreach (var h in afterShowHwnds)
                {
                    if (!beforeShowHwnds.Contains(h))
                    {
                        slot.ContainerHwnd = h;
                        IntPtr child = GetWindow(h, GW_CHILD);
                        if (child != IntPtr.Zero)
                            slot.GUIViewHwnd = child;
                        LogVerbose($"[NWB-Composite] Slot '{slotId}' HWND=0x{h.ToInt64():X}, GUIView=0x{(child != IntPtr.Zero ? child.ToInt64() : 0):X}");
                        break;
                    }
                }
            }

            // Get the actual GUIView HWND via EditorWindow.m_Parent.nativeHandle.
            // GetWindow(GW_CHILD) on a ContainerWindow returns RootView, not the
            // DockArea. The reflected native handle gives the correct DockArea HWND
            // which has its own D3D swap chain that PrintWindow can capture.
            IntPtr reflectedGUIView = Cn.Tuanjie.Codely.Editor.EditorWindowNativeHandleHelper.GetGUIViewHandle(floating);
            if (reflectedGUIView != IntPtr.Zero)
                slot.GUIViewHwnd = reflectedGUIView;

            LogVerbose($"[NWB-Composite] Slot '{slotId}' ContainerHwnd=0x{slot.ContainerHwnd.ToInt64():X}, GUIViewHwnd=0x{slot.GUIViewHwnd.ToInt64():X} reflected=0x{reflectedGUIView.ToInt64():X}");

            // Remove WS_EX_LAYERED from both ContainerWindow and GUIView.
            // DoPaint checks WS_EX_LAYERED and sets m_Transparent=true which
            // causes OnWindowEndRender(presentContent=false), preventing the
            // DWM redirect surface from being updated. PrintWindow then
            // captures black. Remove from both to ensure proper rendering.
            if (slot.ContainerHwnd != IntPtr.Zero)
            {
                int style = GetWindowLongW(slot.ContainerHwnd, GWL_EXSTYLE);
                if ((style & WS_EX_LAYERED) != 0)
                    SetWindowLongW(slot.ContainerHwnd, GWL_EXSTYLE, style & ~WS_EX_LAYERED);
            }
            if (slot.GUIViewHwnd != IntPtr.Zero && slot.GUIViewHwnd != slot.ContainerHwnd)
            {
                int gvStyle = GetWindowLongW(slot.GUIViewHwnd, GWL_EXSTYLE);
                if ((gvStyle & WS_EX_LAYERED) != 0)
                    SetWindowLongW(slot.GUIViewHwnd, GWL_EXSTYLE, gvStyle & ~WS_EX_LAYERED);
            }
#endif
#if UNITY_EDITOR_OSX
            if (addedDirectly)
            {
                // Share NSWindow from existing slot
                foreach (CompositeCaptureSlot existSlot in s_CompositeSlots.Values)
                {
                    if (existSlot?.Window != null && existSlot.NSWindow != IntPtr.Zero)
                    {
                        slot.NSWindow = existSlot.NSWindow;
                        break;
                    }
                }
                LogVerbose($"[NWB-Composite] Tabbed slot '{slotId}' NSWindow={slot.NSWindow}");
            }
            else if (!shouldReuseExistingWindow)
            {
                var afterShowNSWindows = GetAllNSWindows();
                foreach (var nsw in afterShowNSWindows)
                {
                    if (!beforeShowNSWindows.Contains(nsw))
                    {
                        slot.NSWindow = nsw;
                        LogVerbose($"[NWB-Composite] Slot '{slotId}' NSWindow={nsw}");
                        break;
                    }
                }
            }
#endif
            ResizeCompositeSlot(slot, logicalW, logicalH, rect);

            // Initialize the window's content for current selection.
            // InvokeSelectionChangeOnWindow rebuilds the ActiveEditorTracker
            // (loads Inspector editors for the selected object).
            if (floating != null && Selection.activeObject != null)
            {
                try { InvokeSelectionChangeOnWindow(floating); } catch (Exception) { }
            }

            // For tabbed windows: force initial Layout+Repaint AFTER editors
            // are loaded by InvokeSelectionChangeOnWindow. This ensures DoPaint
            // takes the full-render path and writes correct content into the
            // AuxBackBuffer. Without this, the GUIView's m_CanBlitPaint is true
            // (set by the first slot's SendEvent render) and subsequent
            // ForceRepaintImmediately calls blit stale content from AuxBackBuffer.
            if (addedDirectly)
            {
                ForceDoubleLayoutRepaint(floating);
                floating.Repaint();
            }

            EditorApplication.QueuePlayerLoopUpdate();

            return slot;
        }

        private static void ScheduleCompositeLayoutPreset(CompositeStreamRequest request)
        {
            if (request == null)
            {
                CancelCompositeLayoutOperation();
                return;
            }

            if (request.layoutPreset == kSavedCompositeLayoutPreset)
            {
                ScheduleSavedCompositeLayout(request);
                return;
            }

            if (request.layoutPreset != kDefaultCompositeLayoutPreset)
            {
                CancelCompositeLayoutOperation();
                return;
            }

            if (!TryResolveDefaultLayoutSlotIds(request,
                out string hierarchySlotId,
                out string sceneSlotId,
                out string gameSlotId,
                out string projectSlotId,
                out string consoleSlotId,
                out string inspectorSlotId))
                return;

            List<CompositeSlotRequest> defaultSlots = BuildDefaultCompositeLayoutSlots(
                request,
                hierarchySlotId,
                sceneSlotId,
                gameSlotId,
                projectSlotId,
                consoleSlotId,
                inspectorSlotId);
            float defaultLayoutRectError;
            if (IsDefaultCompositeLayoutAchieved(hierarchySlotId, sceneSlotId, gameSlotId, projectSlotId, consoleSlotId, inspectorSlotId) &&
                VerifySavedCompositeLayoutRects(defaultSlots, 0.03f, out defaultLayoutRectError))
                return;

            // NOTE:
            // The direct SplitView-tree rebuild path is intentionally disabled for
            // default layout on Windows. In practice this path can produce invalid
            // DockArea state after domain reload (HostView.GetExtraButtonsWidth NRE),
            // which then breaks downstream keyboard/input dispatch in Play mode.
            //
            // We fall back to the existing step-by-step layout operation, which uses
            // Unity's normal docking flow and is more stable for multi-view streams.
            LogVerbose("[NWB-Composite] Skip direct tree rebuild for default layout; using scheduled dock operations");

            if (IsDefaultCompositeLayoutAchieved(hierarchySlotId, sceneSlotId, gameSlotId, projectSlotId, consoleSlotId, inspectorSlotId))
                return;

            if (s_CompositeLayoutOperation != null &&
                s_CompositeLayoutOperation.Preset == request.layoutPreset &&
                s_CompositeLayoutOperation.HierarchySlotId == hierarchySlotId &&
                s_CompositeLayoutOperation.GameSlotId == gameSlotId &&
                s_CompositeLayoutOperation.SceneSlotId == sceneSlotId &&
                s_CompositeLayoutOperation.ProjectSlotId == projectSlotId &&
                s_CompositeLayoutOperation.ConsoleSlotId == consoleSlotId &&
                s_CompositeLayoutOperation.InspectorSlotId == inspectorSlotId)
            {
                return;
            }

            s_CompositeLayoutOperation = new CompositeLayoutOperation
            {
                Preset = request.layoutPreset,
                HierarchySlotId = hierarchySlotId,
                GameSlotId = gameSlotId,
                SceneSlotId = sceneSlotId,
                ProjectSlotId = projectSlotId,
                ConsoleSlotId = consoleSlotId,
                InspectorSlotId = inspectorSlotId,
                Step = 0,
                WaitFrames = 3,
                Attempts = 0
            };
            EditorApplication.update -= CompositeLayoutOperationTick;
            EditorApplication.update += CompositeLayoutOperationTick;
            LogVerbose("[NWB-Composite] Scheduled default layout operation");
        }

        private static void ScheduleSavedCompositeLayout(CompositeStreamRequest request)
        {
            List<CompositeSlotRequest> savedSlots = GetValidCompositeLayoutSlots(request);
            TryApplySavedCompositeLayoutTree(savedSlots);
            // After moving EditorWindows from floating ContainerWindows to the
            // main editor's DockArea, the original floating ContainerWindows are
            // left empty (no panes). Close them to prevent zombie black windows.
            CloseOrphanEmptyContainerWindows();
            CancelCompositeLayoutOperation();
        }

        private static List<CompositeSlotRequest> GetValidCompositeLayoutSlots(CompositeStreamRequest request)
        {
            var slots = new List<CompositeSlotRequest>();
            if (request?.slots == null) return slots;

            foreach (CompositeSlotRequest slot in request.slots)
            {
                if (slot == null || string.IsNullOrEmpty(slot.slotId) || string.IsNullOrEmpty(slot.windowType))
                    continue;
                slots.Add(slot);
            }

            return slots;
        }

        private static List<List<CompositeSlotRequest>> GroupCompositeSlotsByRect(List<CompositeSlotRequest> slots)
        {
            var groups = new List<List<CompositeSlotRequest>>();
            if (slots == null) return groups;

            foreach (CompositeSlotRequest slot in slots)
            {
                bool added = false;
                foreach (var group in groups)
                {
                    if (group.Count > 0 && AreCompositeRectsEquivalent(slot.rect, group[0].rect))
                    {
                        group.Add(slot);
                        added = true;
                        break;
                    }
                }
                if (!added)
                    groups.Add(new List<CompositeSlotRequest> { slot });
            }

            return groups;
        }

        private static bool AreCompositeRectsEquivalent(CompositeRect a, CompositeRect b)
        {
            Rect ar = ClampCompositeRect(a);
            Rect br = ClampCompositeRect(b);
            const float eps = 0.01f;
            return Mathf.Abs(ar.x - br.x) <= eps &&
                   Mathf.Abs(ar.y - br.y) <= eps &&
                   Mathf.Abs(ar.width - br.width) <= eps &&
                   Mathf.Abs(ar.height - br.height) <= eps;
        }

        private static CompositeLayoutNode BuildCompositeLayoutNode(List<CompositeSlotRequest> slots)
        {
            if (slots == null || slots.Count == 0) return null;
            if (slots.Count == 1) return new CompositeLayoutNode { Slot = slots[0] };

            if (TrySplitCompositeLayoutSlots(slots, true, out List<CompositeSlotRequest> first, out List<CompositeSlotRequest> second) ||
                TrySplitCompositeLayoutSlots(slots, false, out first, out second))
            {
                return new CompositeLayoutNode
                {
                    First = BuildCompositeLayoutNode(first),
                    Second = BuildCompositeLayoutNode(second),
                    Edge = IsVerticalSplit(first, second) ? "right" : "bottom"
                };
            }

            slots.Sort((a, b) =>
            {
                Rect ar = ClampCompositeRect(a.rect);
                Rect br = ClampCompositeRect(b.rect);
                int cmp = ar.x.CompareTo(br.x);
                return cmp != 0 ? cmp : ar.y.CompareTo(br.y);
            });
            return new CompositeLayoutNode
            {
                First = new CompositeLayoutNode { Slot = slots[0] },
                Second = BuildCompositeLayoutNode(slots.GetRange(1, slots.Count - 1)),
                Edge = "right"
            };
        }

        private static bool TrySplitCompositeLayoutSlots(
            List<CompositeSlotRequest> slots,
            bool vertical,
            out List<CompositeSlotRequest> first,
            out List<CompositeSlotRequest> second)
        {
            first = null;
            second = null;
            const float eps = 0.01f;
            var cuts = new List<float>();
            foreach (CompositeSlotRequest slot in slots)
            {
                Rect r = ClampCompositeRect(slot.rect);
                cuts.Add(vertical ? r.xMin : r.yMin);
                cuts.Add(vertical ? r.xMax : r.yMax);
            }
            cuts.Sort();

            foreach (float cut in cuts)
            {
                var a = new List<CompositeSlotRequest>();
                var b = new List<CompositeSlotRequest>();
                bool crosses = false;
                foreach (CompositeSlotRequest slot in slots)
                {
                    Rect r = ClampCompositeRect(slot.rect);
                    float min = vertical ? r.xMin : r.yMin;
                    float max = vertical ? r.xMax : r.yMax;
                    if (max <= cut + eps)
                        a.Add(slot);
                    else if (min >= cut - eps)
                        b.Add(slot);
                    else
                    {
                        crosses = true;
                        break;
                    }
                }
                if (!crosses && a.Count > 0 && b.Count > 0)
                {
                    first = a;
                    second = b;
                    return true;
                }
            }

            return false;
        }

        private static bool IsVerticalSplit(List<CompositeSlotRequest> first, List<CompositeSlotRequest> second)
        {
            float firstMaxX = float.MinValue;
            float secondMinX = float.MaxValue;
            foreach (CompositeSlotRequest slot in first)
                firstMaxX = Mathf.Max(firstMaxX, ClampCompositeRect(slot.rect).xMax);
            foreach (CompositeSlotRequest slot in second)
                secondMinX = Mathf.Min(secondMinX, ClampCompositeRect(slot.rect).xMin);
            return firstMaxX <= secondMinX + 0.01f;
        }

        private static bool TryApplySavedCompositeLayoutTree(List<CompositeSlotRequest> savedSlots)
        {
            if (savedSlots == null || savedSlots.Count == 0) return false;

            bool displayFrozen = false;
            try
            {
                var groups = GroupCompositeSlotsByRect(savedSlots);
                var layoutSlots = new List<CompositeSlotRequest>();
                foreach (var group in groups)
                {
                    if (group.Count > 0) layoutSlots.Add(group[0]);
                }
                if (layoutSlots.Count == 0) return false;

                CompositeLayoutNode rootNode = BuildCompositeLayoutNode(layoutSlots);
                if (rootNode == null) return false;

                object hostWindow = GetCompositeHostWindow();
                if (hostWindow == null) return false;

                Rect rootScreen = GetCompositeRootRectForTree(hostWindow);
                if (rootScreen.width <= 1f || rootScreen.height <= 1f) return false;

                SetContainerWindowFreezeDisplay(true);
                displayFrozen = true;

                var groupsByAnchor = new Dictionary<string, List<CompositeSlotRequest>>();
                foreach (var group in groups)
                {
                    if (group.Count == 0 || string.IsNullOrEmpty(group[0].slotId)) continue;
                    groupsByAnchor[group[0].slotId] = group;
                }

                Rect fullBounds = new Rect(0f, 0f, 1f, 1f);
                object rootView = BuildSavedCompositeViewTree(rootNode, groupsByAnchor, fullBounds, rootScreen.width, rootScreen.height);
                if (rootView == null) return false;

                SetViewPosition(rootView, new Rect(0f, 0f, rootScreen.width, rootScreen.height));
                SetContainerWindowRootView(hostWindow, rootView);
                ReflowCompositeSplitRecursive(rootView);
                RepaintCompositeViewRecursive(rootView);
                InvokeContainerWindowDisplayAllViews(hostWindow);
                EditorApplication.QueuePlayerLoopUpdate();
                LogVerbose("[NWB-Composite] Applied saved layout by rebuilding SplitView tree");
                return true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Composite] Direct saved layout tree rebuild failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (displayFrozen)
                    SetContainerWindowFreezeDisplay(false);
            }
        }

        private static object BuildSavedCompositeViewTree(
            CompositeLayoutNode node,
            Dictionary<string, List<CompositeSlotRequest>> groupsByAnchor,
            Rect parentBounds,
            float rootWidth,
            float rootHeight)
        {
            if (node == null) return null;

            Rect nodeBounds = GetCompositeLayoutNodeBounds(node);
            Rect localRect = NormalizedRectToLocalRect(nodeBounds, parentBounds, rootWidth, rootHeight);

            if (node.Slot != null)
            {
                if (!groupsByAnchor.TryGetValue(node.Slot.slotId, out List<CompositeSlotRequest> group))
                    group = new List<CompositeSlotRequest> { node.Slot };

                object dockArea = CreateUnityEditorView("UnityEditor.DockArea");
                if (dockArea == null) return null;
                SetViewPosition(dockArea, localRect);

                int movedCount = 0;
                var movedSlots = new List<CompositeCaptureSlot>();
                foreach (CompositeSlotRequest slotRequest in group)
                {
                    if (!TryGetCompositeSlot(slotRequest.slotId, out CompositeCaptureSlot slot) || slot?.Window == null)
                        continue;
                    if (!MoveCompositeWindowToDockArea(slot.Window, dockArea))
                        return null;
                    movedSlots.Add(slot);
                    movedCount++;
                }
                if (movedCount == 0) return null;

                SelectCompositeSlotTabForGroup(group);
                RepaintCompositeViewRecursive(dockArea);
                // Reacquire after dockArea repaint to avoid sampling transient window handles.
                foreach (CompositeCaptureSlot movedSlot in movedSlots)
                    ReacquireCompositeSlotHandles(movedSlot);
                return dockArea;
            }

            object splitView = CreateUnityEditorView("UnityEditor.SplitView");
            if (splitView == null) return null;
            SetSplitViewVertical(splitView, string.Equals(node.Edge, "bottom", StringComparison.OrdinalIgnoreCase));
            SetViewPosition(splitView, localRect);

            object first = BuildSavedCompositeViewTree(node.First, groupsByAnchor, nodeBounds, rootWidth, rootHeight);
            object second = BuildSavedCompositeViewTree(node.Second, groupsByAnchor, nodeBounds, rootWidth, rootHeight);
            if (first == null || second == null) return null;

            if (!AddViewChild(splitView, first) || !AddViewChild(splitView, second))
                return null;
            return splitView;
        }

        private static Rect GetCompositeLayoutNodeBounds(CompositeLayoutNode node)
        {
            if (node == null) return new Rect(0f, 0f, 1f, 1f);
            if (node.Slot != null) return ClampCompositeRect(node.Slot.rect);
            Rect first = GetCompositeLayoutNodeBounds(node.First);
            Rect second = GetCompositeLayoutNodeBounds(node.Second);
            return Rect.MinMaxRect(
                Mathf.Min(first.xMin, second.xMin),
                Mathf.Min(first.yMin, second.yMin),
                Mathf.Max(first.xMax, second.xMax),
                Mathf.Max(first.yMax, second.yMax));
        }

        private static Rect NormalizedRectToLocalRect(Rect bounds, Rect parentBounds, float rootWidth, float rootHeight)
        {
            return new Rect(
                (bounds.xMin - parentBounds.xMin) * rootWidth,
                (bounds.yMin - parentBounds.yMin) * rootHeight,
                Mathf.Max(bounds.width * rootWidth, 1f),
                Mathf.Max(bounds.height * rootHeight, 1f));
        }

        private static object CreateUnityEditorView(string typeName)
        {
            System.Type type = ResolveUnityEditorType(typeName);
            return type != null ? ScriptableObject.CreateInstance(type) : null;
        }

        private static System.Type ResolveUnityEditorType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            System.Type type = System.Type.GetType(typeName) ??
                               System.Type.GetType(typeName + ",UnityEditor");
            if (type != null) return type;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType(typeName);
                if (type != null) return type;
            }
            return null;
        }

        private static Rect GetCompositeRootRectForTree(object hostWindow)
        {
            object rootSplit = GetRootSplitView(hostWindow);
            Rect rootRect = GetViewScreenPosition(rootSplit);
            if (rootRect.width > 1f && rootRect.height > 1f)
                return rootRect;

            PropertyInfo positionProp = hostWindow?.GetType().GetProperty("position",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (positionProp != null)
            {
                Rect pos = (Rect)positionProp.GetValue(hostWindow);
                return new Rect(0f, 0f, Mathf.Max(pos.width, 320f), Mathf.Max(pos.height, 240f));
            }
            return new Rect(0f, 0f, Mathf.Max(s_CompositeLogicalWidth, 320), Mathf.Max(s_CompositeLogicalHeight, 240));
        }

        private static void SetSplitViewVertical(object splitView, bool vertical)
        {
            FieldInfo verticalField = splitView?.GetType().GetField("vertical",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            verticalField?.SetValue(splitView, vertical);
        }

        private static bool AddViewChild(object parent, object child)
        {
            if (parent == null || child == null) return false;
            foreach (MethodInfo method in parent.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "AddChild") continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 1)
                {
                    method.Invoke(parent, new object[] { child });
                    return true;
                }
            }
            return false;
        }

        private static bool MoveCompositeWindowToDockArea(EditorWindow window, object targetDockArea)
        {
            if (window == null || targetDockArea == null) return false;
            try
            {
                // Capture the source ContainerWindow BEFORE moving the EditorWindow.
                // After RemoveTab, the source DockArea may become empty, and we need
                // to close the orphaned ContainerWindow to prevent zombie black windows.
                object sourceDockArea = GetParentView(window);
                object sourceContainerWindow = sourceDockArea != null ? GetViewWindow(sourceDockArea) : null;

                if (sourceDockArea != null && sourceDockArea.GetType().Name == "DockArea")
                {
                    MethodInfo removeTab = sourceDockArea.GetType().GetMethod("RemoveTab",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new System.Type[] { typeof(EditorWindow), typeof(bool), typeof(bool) },
                        null);
                    removeTab?.Invoke(sourceDockArea, new object[] { window, false, false });
                }

                MethodInfo addTab = targetDockArea.GetType().GetMethod("AddTab",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(EditorWindow), typeof(bool) },
                    null);
                if (addTab != null)
                    addTab.Invoke(targetDockArea, new object[] { window, false });
                else
                {
                    addTab = targetDockArea.GetType().GetMethod("AddTab",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new System.Type[] { typeof(EditorWindow) },
                        null);
                    if (addTab == null) return false;
                    addTab.Invoke(targetDockArea, new object[] { window });
                }

                // After moving, close the source ContainerWindow if it's now empty.
                // This unifies behavior with "添加视图" path where EditorWindow.Close()
                // on the last tab destroys the ContainerWindow naturally.
                if (sourceContainerWindow != null)
                {
                    try
                    {
                        // Check if source container is different from target container
                        object targetContainerWindow = GetViewWindow(targetDockArea);
                        if (targetContainerWindow != null && !object.ReferenceEquals(sourceContainerWindow, targetContainerWindow))
                        {
                            // Source and target are different ContainerWindows.
                            // Check if source ContainerWindow is now empty (no panes).
                            if (!ViewTreeHasDockPanes(
                                    sourceContainerWindow.GetType().GetField("m_RootView",
                                        BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sourceContainerWindow)))
                            {
                                MethodInfo closeMethod = sourceContainerWindow.GetType().GetMethod("Close",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                closeMethod?.Invoke(sourceContainerWindow, null);
                                LogVerbose("[NWB-Composite] Closed empty source ContainerWindow after MoveCompositeWindowToDockArea");
                            }
                        }
                    }
                    catch (Exception) { }
                }

                return true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Composite] Move window to rebuilt DockArea failed: {ex.Message}");
                return false;
            }
        }

        private static void SelectCompositeSlotTabForGroup(List<CompositeSlotRequest> group)
        {
            if (group == null || group.Count == 0) return;
            foreach (CompositeSlotRequest slotRequest in group)
            {
                if (TryGetCompositeSlot(slotRequest.slotId, out CompositeCaptureSlot slot))
                {
                    SelectCompositeSlotTab(slot);
                    return;
                }
            }
        }

        private static void SetContainerWindowRootView(object hostWindow, object rootView)
        {
            PropertyInfo rootViewProp = hostWindow?.GetType().GetProperty("rootView",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            rootViewProp?.SetValue(hostWindow, rootView);
        }

        private static void InvokeContainerWindowDisplayAllViews(object hostWindow)
        {
            MethodInfo displayAllViews = hostWindow?.GetType().GetMethod("DisplayAllViews",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            displayAllViews?.Invoke(hostWindow, null);
        }

        private static void SetContainerWindowFreezeDisplay(bool freeze)
        {
            System.Type containerWindowType = ResolveUnityEditorType("UnityEditor.ContainerWindow");
            MethodInfo setFreezeDisplay = containerWindowType?.GetMethod("SetFreezeDisplay",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            setFreezeDisplay?.Invoke(null, new object[] { freeze });
        }

        private static bool TryResolveDefaultLayoutSlotIds(
            CompositeStreamRequest request,
            out string hierarchySlotId,
            out string sceneSlotId,
            out string gameSlotId,
            out string projectSlotId,
            out string consoleSlotId,
            out string inspectorSlotId)
        {
            hierarchySlotId = null;
            sceneSlotId = null;
            gameSlotId = null;
            projectSlotId = null;
            consoleSlotId = null;
            inspectorSlotId = null;
            if (request?.slots == null) return false;

            foreach (CompositeSlotRequest slot in request.slots)
            {
                if (slot == null || string.IsNullOrEmpty(slot.slotId) || string.IsNullOrEmpty(slot.windowType))
                    continue;
                if (slot.windowType.Contains("SceneHierarchyWindow"))
                    hierarchySlotId = slot.slotId;
                else if (slot.windowType.Contains("SceneView"))
                    sceneSlotId = slot.slotId;
                else if (slot.windowType.Contains("GameView"))
                    gameSlotId = slot.slotId;
                else if (slot.windowType.Contains("ProjectBrowser"))
                    projectSlotId = slot.slotId;
                else if (slot.windowType.Contains("ConsoleWindow"))
                    consoleSlotId = slot.slotId;
                else if (slot.windowType.Contains("InspectorWindow"))
                    inspectorSlotId = slot.slotId;
            }

            return !string.IsNullOrEmpty(hierarchySlotId) &&
                   !string.IsNullOrEmpty(sceneSlotId) &&
                   !string.IsNullOrEmpty(gameSlotId) &&
                   !string.IsNullOrEmpty(projectSlotId) &&
                   !string.IsNullOrEmpty(consoleSlotId) &&
                   !string.IsNullOrEmpty(inspectorSlotId);
        }

        private static List<CompositeSlotRequest> BuildDefaultCompositeLayoutSlots(
            CompositeStreamRequest request,
            string hierarchySlotId,
            string sceneSlotId,
            string gameSlotId,
            string projectSlotId,
            string consoleSlotId,
            string inspectorSlotId)
        {
            var slotsById = new Dictionary<string, CompositeSlotRequest>();
            if (request?.slots != null)
            {
                foreach (CompositeSlotRequest slot in request.slots)
                {
                    if (slot == null || string.IsNullOrEmpty(slot.slotId)) continue;
                    slotsById[slot.slotId] = slot;
                }
            }

            const float rootWidth = 1206f;
            const float rootHeight = 665f;
            float leftWidth = 921f / rootWidth;
            float hierarchyWidth = 228f / rootWidth;
            float sceneWidth = 693f / rootWidth;
            float topHeight = 394f / rootHeight;
            float bottomHeight = 271f / rootHeight;
            float inspectorX = 921f / rootWidth;
            float inspectorWidth = 285f / rootWidth;

            var slots = new List<CompositeSlotRequest>();
            AddDefaultCompositeLayoutSlot(slots, slotsById, hierarchySlotId, 0f, 0f, hierarchyWidth, topHeight);
            AddDefaultCompositeLayoutSlot(slots, slotsById, sceneSlotId, hierarchyWidth, 0f, sceneWidth, topHeight);
            AddDefaultCompositeLayoutSlot(slots, slotsById, gameSlotId, hierarchyWidth, 0f, sceneWidth, topHeight);
            AddDefaultCompositeLayoutSlot(slots, slotsById, projectSlotId, 0f, topHeight, leftWidth, bottomHeight);
            AddDefaultCompositeLayoutSlot(slots, slotsById, consoleSlotId, 0f, topHeight, leftWidth, bottomHeight);
            AddDefaultCompositeLayoutSlot(slots, slotsById, inspectorSlotId, inspectorX, 0f, inspectorWidth, 1f);
            return slots;
        }

        private static void AddDefaultCompositeLayoutSlot(
            List<CompositeSlotRequest> slots,
            Dictionary<string, CompositeSlotRequest> slotsById,
            string slotId,
            float x,
            float y,
            float w,
            float h)
        {
            if (string.IsNullOrEmpty(slotId) || !slotsById.TryGetValue(slotId, out CompositeSlotRequest source))
                return;

            slots.Add(new CompositeSlotRequest
            {
                slotId = source.slotId,
                windowType = source.windowType,
                rect = new CompositeRect
                {
                    x = x,
                    y = y,
                    w = w,
                    h = h
                }
            });
        }

        private static void CompositeLayoutOperationTick()
        {
            CompositeLayoutOperation op = s_CompositeLayoutOperation;
            if (op == null)
            {
                EditorApplication.update -= CompositeLayoutOperationTick;
                return;
            }

            if (!s_CompositeActive)
            {
                CancelCompositeLayoutOperation();
                return;
            }

            if (op.WaitFrames > 0)
            {
                op.WaitFrames--;
                RepaintCompositeLayoutOperationSlots(op);
                return;
            }

            if (!TryGetCompositeSlot(op.HierarchySlotId, out CompositeCaptureSlot hierarchySlot) ||
                !TryGetCompositeSlot(op.SceneSlotId, out CompositeCaptureSlot sceneSlot) ||
                !TryGetCompositeSlot(op.GameSlotId, out CompositeCaptureSlot gameSlot) ||
                !TryGetCompositeSlot(op.ProjectSlotId, out CompositeCaptureSlot projectSlot) ||
                !TryGetCompositeSlot(op.ConsoleSlotId, out CompositeCaptureSlot consoleSlot) ||
                !TryGetCompositeSlot(op.InspectorSlotId, out CompositeCaptureSlot inspectorSlot))
            {
                RetryCompositeLayoutOperation("waiting for default layout slots");
                return;
            }

            switch (op.Step)
            {
                case 0:
                    ReacquireCompositeSlotHandles(hierarchySlot);
                    ReacquireCompositeSlotHandles(sceneSlot);
                    ReacquireCompositeSlotHandles(gameSlot);
                    ReacquireCompositeSlotHandles(projectSlot);
                    ReacquireCompositeSlotHandles(consoleSlot);
                    ReacquireCompositeSlotHandles(inspectorSlot);
                    SelectCompositeSlotTab(sceneSlot);
                    op.Step = 1;
                    op.Attempts = 0;
                    op.WaitFrames = 2;
                    return;
                case 1:
                    if (!AreCompositeSlotsInDifferentDockAreas(sceneSlot, inspectorSlot))
                    {
                        if (!TryDockCompositeSlotViaSplitView(inspectorSlot, op.SceneSlotId, "right"))
                        {
                            RetryCompositeLayoutOperation("docking Inspector to the right");
                            return;
                        }
                        ReacquireCompositeSlotHandles(inspectorSlot);
                    }
                    op.Step = 2;
                    op.Attempts = 0;
                    op.WaitFrames = 3;
                    return;
                case 2:
                    if (!AreCompositeSlotsInDifferentDockAreas(sceneSlot, projectSlot))
                    {
                        if (!TryDockCompositeSlotViaSplitView(projectSlot, op.SceneSlotId, "bottom"))
                        {
                            RetryCompositeLayoutOperation("docking Project to the bottom");
                            return;
                        }
                        ReacquireCompositeSlotHandles(projectSlot);
                    }
                    op.Step = 3;
                    op.Attempts = 0;
                    op.WaitFrames = 3;
                    return;
                case 3:
                    if (!AreCompositeSlotsInSameDockArea(projectSlot, consoleSlot))
                    {
                        if (!TryMoveCompositeSlotAsTab(consoleSlot, op.ProjectSlotId))
                        {
                            RetryCompositeLayoutOperation("tabbing Console with Project");
                            return;
                        }
                        ReacquireCompositeSlotHandles(consoleSlot);
                    }
                    SelectCompositeSlotTab(projectSlot);
                    op.Step = 4;
                    op.Attempts = 0;
                    op.WaitFrames = 2;
                    return;
                case 4:
                    if (!AreCompositeSlotsInDifferentDockAreas(hierarchySlot, sceneSlot))
                    {
                        if (!TryDockCompositeSlotViaSplitView(hierarchySlot, op.SceneSlotId, "left"))
                        {
                            RetryCompositeLayoutOperation("docking Hierarchy to the left");
                            return;
                        }
                        ReacquireCompositeSlotHandles(hierarchySlot);
                    }
                    SelectCompositeSlotTab(hierarchySlot);
                    SelectCompositeSlotTab(sceneSlot);
                    op.Step = 5;
                    op.Attempts = 0;
                    op.WaitFrames = 3;
                    return;
                default:
                    SelectCompositeSlotTab(hierarchySlot);
                    SelectCompositeSlotTab(sceneSlot);
                    SelectCompositeSlotTab(projectSlot);
                    if (IsDefaultCompositeLayoutAchieved(op.HierarchySlotId, op.SceneSlotId, op.GameSlotId, op.ProjectSlotId, op.ConsoleSlotId, op.InspectorSlotId))
                    {
                        LogVerbose("[NWB-Composite] Default layout operation completed");
                        CancelCompositeLayoutOperation();
                    }
                    else
                    {
                        op.Step = 1;
                        RetryCompositeLayoutOperation("verifying default layout");
                    }
                    return;
            }
        }

        private static bool TryGetCompositeSlot(string slotId, out CompositeCaptureSlot slot)
        {
            slot = null;
            return !string.IsNullOrEmpty(slotId) &&
                   s_CompositeSlots.TryGetValue(slotId, out slot) &&
                   slot?.Window != null;
        }

        private static void TryActivateCompositeSlot(string slotId)
        {
            if (!TryGetCompositeSlot(slotId, out CompositeCaptureSlot slot) || slot.Window == null)
                return;
            if (SelectCompositeSlotTab(slot))
            {
                LogVerbose($"[NWB-Composite] Activated reserved slot '{slotId}' ({slot.WindowTypeName})");
            } else {
                CodelyLogger.LogWarning($"[NWB-Composite] Activate slot '{slotId}' failed");
            }
        }

        private static CompositeCaptureSlot FindCompositeSlotByWindow(EditorWindow window)
        {
            if (window == null || s_CompositeSlots == null) return null;
            foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
            {
                if (slot?.Window == window)
                    return slot;
            }
            return null;
        }

        private static void RepaintCompositeLayoutOperationSlots(CompositeLayoutOperation op)
        {
            if (op == null) return;
            if (TryGetCompositeSlot(op.HierarchySlotId, out CompositeCaptureSlot hierarchySlot))
                hierarchySlot.Window.Repaint();
            if (TryGetCompositeSlot(op.SceneSlotId, out CompositeCaptureSlot sceneSlot))
                sceneSlot.Window.Repaint();
            if (TryGetCompositeSlot(op.GameSlotId, out CompositeCaptureSlot gameSlot))
                gameSlot.Window.Repaint();
            if (TryGetCompositeSlot(op.ProjectSlotId, out CompositeCaptureSlot projectSlot))
                projectSlot.Window.Repaint();
            if (TryGetCompositeSlot(op.ConsoleSlotId, out CompositeCaptureSlot consoleSlot))
                consoleSlot.Window.Repaint();
            if (TryGetCompositeSlot(op.InspectorSlotId, out CompositeCaptureSlot inspectorSlot))
                inspectorSlot.Window.Repaint();
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private static void RetryCompositeLayoutOperation(string reason)
        {
            CompositeLayoutOperation op = s_CompositeLayoutOperation;
            if (op == null) return;
            op.Attempts++;
            if (op.Attempts > 12)
            {
                if (string.Equals(op.Preset, kDefaultCompositeLayoutPreset, StringComparison.Ordinal))
                {
                    if (TryApplyDefaultLayoutTreeFallback(op))
                    {
                        LogVerbose($"[NWB-Composite] Default layout fallback tree rebuild succeeded after '{reason}'");
                        CancelCompositeLayoutOperation();
                        return;
                    }
                }
                CodelyLogger.LogWarning($"[NWB-Composite] Default layout operation stopped while {reason}");
                CancelCompositeLayoutOperation();
                return;
            }
            op.WaitFrames = 2;
            RepaintCompositeLayoutOperationSlots(op);
        }

        private static bool TryApplyDefaultLayoutTreeFallback(CompositeLayoutOperation op)
        {
            if (op == null) return false;
            if (string.IsNullOrEmpty(op.HierarchySlotId) ||
                string.IsNullOrEmpty(op.SceneSlotId) ||
                string.IsNullOrEmpty(op.GameSlotId) ||
                string.IsNullOrEmpty(op.ProjectSlotId) ||
                string.IsNullOrEmpty(op.ConsoleSlotId) ||
                string.IsNullOrEmpty(op.InspectorSlotId))
                return false;

            var request = new CompositeStreamRequest();
            var requestSlots = new List<CompositeSlotRequest>();
            TryAppendCurrentSlotForFallback(requestSlots, op.HierarchySlotId);
            TryAppendCurrentSlotForFallback(requestSlots, op.SceneSlotId);
            TryAppendCurrentSlotForFallback(requestSlots, op.GameSlotId);
            TryAppendCurrentSlotForFallback(requestSlots, op.ProjectSlotId);
            TryAppendCurrentSlotForFallback(requestSlots, op.ConsoleSlotId);
            TryAppendCurrentSlotForFallback(requestSlots, op.InspectorSlotId);
            request.slots = requestSlots.ToArray();
            if (request.slots.Length < 6)
                return false;

            List<CompositeSlotRequest> defaultSlots = BuildDefaultCompositeLayoutSlots(
                request,
                op.HierarchySlotId,
                op.SceneSlotId,
                op.GameSlotId,
                op.ProjectSlotId,
                op.ConsoleSlotId,
                op.InspectorSlotId);
            if (defaultSlots == null || defaultSlots.Count == 0)
                return false;

            if (!TryApplySavedCompositeLayoutTree(defaultSlots))
                return false;
            CloseOrphanEmptyContainerWindows();
            return true;
        }

        private static void TryAppendCurrentSlotForFallback(List<CompositeSlotRequest> slots, string slotId)
        {
            if (slots == null || string.IsNullOrEmpty(slotId))
                return;
            if (!s_CompositeSlots.TryGetValue(slotId, out CompositeCaptureSlot slot))
                return;
            if (slot?.Window == null)
                return;

            slots.Add(new CompositeSlotRequest
            {
                slotId = slotId,
                windowType = slot.WindowTypeName ?? slot.Window.GetType().FullName
            });
        }

        private static void CancelCompositeLayoutOperation()
        {
            s_CompositeLayoutOperation = null;
            EditorApplication.update -= CompositeLayoutOperationTick;
        }

        private static bool IsDefaultCompositeLayoutAchieved(
            string hierarchySlotId,
            string sceneSlotId,
            string gameSlotId,
            string projectSlotId,
            string consoleSlotId,
            string inspectorSlotId)
        {
            if (!TryGetCompositeSlot(hierarchySlotId, out CompositeCaptureSlot hierarchySlot) ||
                !TryGetCompositeSlot(sceneSlotId, out CompositeCaptureSlot sceneSlot) ||
                !TryGetCompositeSlot(gameSlotId, out CompositeCaptureSlot gameSlot) ||
                !TryGetCompositeSlot(projectSlotId, out CompositeCaptureSlot projectSlot) ||
                !TryGetCompositeSlot(consoleSlotId, out CompositeCaptureSlot consoleSlot) ||
                !TryGetCompositeSlot(inspectorSlotId, out CompositeCaptureSlot inspectorSlot))
                return false;

            object hierarchyDock = GetParentView(hierarchySlot.Window);
            object sceneDock = GetParentView(sceneSlot.Window);
            object gameDock = GetParentView(gameSlot.Window);
            object projectDock = GetParentView(projectSlot.Window);
            object consoleDock = GetParentView(consoleSlot.Window);
            object inspectorDock = GetParentView(inspectorSlot.Window);
            return hierarchyDock != null &&
                   sceneDock != null &&
                   gameDock != null &&
                   projectDock != null &&
                   consoleDock != null &&
                   inspectorDock != null &&
                   object.ReferenceEquals(sceneDock, gameDock) &&
                   object.ReferenceEquals(projectDock, consoleDock) &&
                   !object.ReferenceEquals(hierarchyDock, sceneDock) &&
                   !object.ReferenceEquals(sceneDock, projectDock) &&
                   !object.ReferenceEquals(projectDock, inspectorDock) &&
                   !object.ReferenceEquals(sceneDock, inspectorDock) &&
                   !object.ReferenceEquals(hierarchyDock, inspectorDock);
        }

        private static bool AreCompositeSlotsInDifferentDockAreas(CompositeCaptureSlot first, CompositeCaptureSlot second)
        {
            object firstDock = first?.Window != null ? GetParentView(first.Window) : null;
            object secondDock = second?.Window != null ? GetParentView(second.Window) : null;
            return firstDock != null && secondDock != null && !object.ReferenceEquals(firstDock, secondDock);
        }

        private static bool AreCompositeSlotsInSameDockArea(CompositeCaptureSlot first, CompositeCaptureSlot second)
        {
            object firstDock = first?.Window != null ? GetParentView(first.Window) : null;
            object secondDock = second?.Window != null ? GetParentView(second.Window) : null;
            return firstDock != null && secondDock != null && object.ReferenceEquals(firstDock, secondDock);
        }

        private static bool TryMoveCompositeSlotAsTab(CompositeCaptureSlot slot, string targetSlotId)
        {
            if (slot?.Window == null || string.IsNullOrEmpty(targetSlotId))
                return false;
            if (!s_CompositeSlots.TryGetValue(targetSlotId, out CompositeCaptureSlot targetSlot) || targetSlot?.Window == null)
                return false;

            try
            {
                object sourceDockArea = GetParentView(slot.Window);
                object targetDockArea = GetParentView(targetSlot.Window);
                if (sourceDockArea == null || targetDockArea == null)
                    return false;
                if (object.ReferenceEquals(sourceDockArea, targetDockArea))
                    return true;

                MethodInfo removeTab = sourceDockArea.GetType().GetMethod("RemoveTab",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(EditorWindow), typeof(bool), typeof(bool) },
                    null);
                MethodInfo addTab = targetDockArea.GetType().GetMethod("AddTab",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(EditorWindow), typeof(bool) },
                    null);
                if (removeTab == null || addTab == null)
                    return false;

                removeTab.Invoke(sourceDockArea, new object[] { slot.Window, true, false });
                addTab.Invoke(targetDockArea, new object[] { slot.Window, false });
                SelectCompositeSlotTab(targetSlot);
                return true;
            }
            catch (Exception ex)
            {
                string innerMsg = ex.InnerException?.Message ?? ex.Message;
                CodelyLogger.LogWarning($"[NWB-Composite] Move slot '{slot.SlotId}' as tab failed: {innerMsg}");
                return false;
            }
        }

        private static bool SelectCompositeSlotTab(CompositeCaptureSlot slot)
        {
            if (slot?.Window == null) return false;
            try
            {
                object dockArea = GetParentView(slot.Window);
                if (dockArea == null) return false;
                FieldInfo panesField = dockArea.GetType().GetField("m_Panes",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object panesObj = panesField?.GetValue(dockArea);
                if (!(panesObj is System.Collections.IList panes)) return false;
                int index = panes.IndexOf(slot.Window);
                if (index < 0) return false;
                PropertyInfo selectedProp = dockArea.GetType().GetProperty("selected",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                selectedProp?.SetValue(dockArea, index);
                MethodInfo updateTitle = dockArea.GetType().GetMethod("UpdateWindowTitle",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new System.Type[] { typeof(EditorWindow) },
                    null);
                updateTitle?.Invoke(dockArea, new object[] { slot.Window });
                MethodInfo repaint = dockArea.GetType().GetMethod("Repaint",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                repaint?.Invoke(dockArea, null);
                ForceDoubleLayoutRepaint(slot.Window);
                slot.Window.Repaint();
                return true;
            }
            catch (Exception ex)
            {
                string innerMsg = ex.InnerException?.Message ?? ex.Message;
                CodelyLogger.LogWarning($"[NWB-Composite] Select slot tab failed for '{slot.SlotId}': {innerMsg}");
                return false;
            }
        }

        private static bool TryDockCompositeSlotViaSplitView(CompositeCaptureSlot slot, string targetSlotId, string dockEdge)
        {
            if (slot?.Window == null || string.IsNullOrEmpty(targetSlotId) || string.IsNullOrEmpty(dockEdge))
                return false;
            if (!s_CompositeSlots.TryGetValue(targetSlotId, out CompositeCaptureSlot targetSlot) || targetSlot?.Window == null)
                return false;

            object dockAreaForDragReset = null;
            try
            {
                object targetDockArea = GetParentView(targetSlot.Window);
                object sourceDockArea = GetParentView(slot.Window);
                if (targetDockArea == null || sourceDockArea == null) return false;
                dockAreaForDragReset = sourceDockArea;

                Rect targetRect = GetViewScreenPosition(targetDockArea);
                if (targetRect.width <= 1f || targetRect.height <= 1f) return false;

                Vector2 screenPos;
                switch (dockEdge)
                {
                    case "left":
                        screenPos = new Vector2(targetRect.xMin + 2f, targetRect.center.y);
                        break;
                    case "right":
                        screenPos = new Vector2(targetRect.xMax - 2f, targetRect.center.y);
                        break;
                    case "bottom":
                        screenPos = new Vector2(targetRect.center.x, targetRect.yMax - 2f);
                        break;
                    default:
                        CodelyLogger.LogWarning($"[NWB-Composite] Unsupported dock edge '{dockEdge}' for slot '{slot.SlotId}'");
                        return false;
                }

                FieldInfo originalDragSourceField = sourceDockArea.GetType().GetField("s_OriginalDragSource",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                originalDragSourceField?.SetValue(null, sourceDockArea);
                FieldInfo dragPaneField = sourceDockArea.GetType().GetField("s_DragPane",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                dragPaneField?.SetValue(null, slot.Window);
                FieldInfo ignoreDockingField = sourceDockArea.GetType().GetField("s_IgnoreDockingForView",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                FieldInfo panesField = sourceDockArea.GetType().GetField("m_Panes",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                int paneCount = panesField?.GetValue(sourceDockArea) is System.Collections.ICollection panes
                    ? panes.Count
                    : 0;
                ignoreDockingField?.SetValue(null, paneCount == 1 ? sourceDockArea : null);

                object hostWindow = GetViewWindow(targetDockArea);
                object rootSplit = GetRootSplitView(hostWindow);
                if (rootSplit == null) return false;

                MethodInfo dragOver = rootSplit.GetType().GetMethod("DragOver",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new System.Type[] { typeof(EditorWindow), typeof(Vector2) },
                    null);
                object dropInfo = dragOver?.Invoke(rootSplit, new object[] { slot.Window, screenPos });
                if (dropInfo == null)
                {
                    CodelyLogger.LogWarning($"[NWB-Composite] DragOver returned null for slot '{slot.SlotId}' edge={dockEdge}");
                    return false;
                }

                MethodInfo performDrop = rootSplit.GetType().GetMethod("PerformDrop",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new System.Type[] { typeof(EditorWindow), dropInfo.GetType(), typeof(Vector2) },
                    null);
                if (performDrop == null) return false;

                object result = performDrop.Invoke(rootSplit, new object[] { slot.Window, dropInfo, screenPos });
                bool ok = result is bool b && b;
                if (ok)
                {
                    SelectCompositeSlotTab(slot);
                    LogVerbose($"[NWB-Composite] Docked slot '{slot.SlotId}' to '{targetSlotId}' edge={dockEdge} via SplitView.PerformDrop");
                }
                return ok;
            }
            catch (Exception ex)
            {
                string innerMsg = ex.InnerException?.Message ?? ex.Message;
                CodelyLogger.LogWarning($"[NWB-Composite] SplitView docking failed for slot '{slot.SlotId}': {innerMsg}");
                return false;
            }
            finally
            {
                ResetCompositeDockAreaDragState(dockAreaForDragReset, slot);
            }
        }

        private static void ResetCompositeDockAreaDragState(object dockArea, CompositeCaptureSlot slot)
        {
            if (dockArea == null) return;

            try
            {
                System.Type dockAreaType = dockArea.GetType();
                dockAreaType.GetField("s_DragPane",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                dockAreaType.GetField("s_DropInfo",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                dockAreaType.GetField("s_PlaceholderPos",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, -1);
                dockAreaType.GetField("s_IsDragging",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, false);
                dockAreaType.GetField("s_OriginalDragSource",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                dockAreaType.GetField("s_IgnoreDockingForView",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);

                object newDockArea = slot?.Window != null ? GetParentView(slot.Window) : dockArea;
                MethodInfo repaint = newDockArea?.GetType().GetMethod("Repaint",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                repaint?.Invoke(newDockArea, null);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Composite] DockArea drag state reset failed for '{slot?.SlotId}': {ex.Message}");
            }
        }

        private static bool VerifySavedCompositeLayoutRects(
            List<CompositeSlotRequest> savedSlots,
            float tolerance,
            out float maxError)
        {
            maxError = 0f;
            if (savedSlots == null || savedSlots.Count == 0) return false;
            object rootSplit = GetRootSplitView(GetCompositeHostWindow());
            if (rootSplit == null) return false;
            Rect rootScreen = GetViewScreenPosition(rootSplit);
            if (rootScreen.width <= 1f || rootScreen.height <= 1f) return false;

            foreach (CompositeSlotRequest savedSlot in savedSlots)
            {
                if (!TryGetNormalizedCompositeSlotRect(savedSlot, rootScreen, out Rect current))
                    return false;
                Rect expected = ClampCompositeRect(savedSlot.rect);
                maxError = Mathf.Max(maxError,
                    Mathf.Abs(current.x - expected.x),
                    Mathf.Abs(current.y - expected.y),
                    Mathf.Abs(current.width - expected.width),
                    Mathf.Abs(current.height - expected.height));
            }

            return maxError <= tolerance;
        }

        private static bool TryGetNormalizedCompositeSlotRect(
            CompositeSlotRequest savedSlot,
            Rect rootScreen,
            out Rect normalized)
        {
            normalized = Rect.zero;
            if (savedSlot == null || string.IsNullOrEmpty(savedSlot.slotId))
                return false;
            if (!TryGetCompositeSlot(savedSlot.slotId, out CompositeCaptureSlot slot) || slot?.Window == null)
                return false;
            object view = GetParentView(slot.Window);
            if (view == null) return false;
            Rect viewRect = GetViewScreenPosition(view);
            if (viewRect.width <= 1f || viewRect.height <= 1f) return false;
            normalized = NormalizeCompositeViewRect(viewRect, rootScreen);
            return true;
        }

        private static IEnumerable<object> GetViewChildren(object view)
        {
            if (view == null) yield break;
            PropertyInfo childrenProp = view.GetType().GetProperty("children",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object childrenObj = childrenProp?.GetValue(view);
            if (!(childrenObj is System.Collections.IEnumerable children)) yield break;

            foreach (object child in children)
            {
                if (child != null) yield return child;
            }
        }

        private static void SetViewPosition(object view, Rect position)
        {
            if (view == null) return;
            PropertyInfo positionProp = view.GetType().GetProperty("position",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            positionProp?.SetValue(view, position);
        }

        private static void ReflowCompositeSplitRecursive(object view)
        {
            if (view == null) return;
            if (view.GetType().Name == "SplitView")
            {
                MethodInfo reflow = view.GetType().GetMethod("Reflow",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                reflow?.Invoke(view, null);
            }

            foreach (object child in GetViewChildren(view))
                ReflowCompositeSplitRecursive(child);
        }

        private static void RepaintCompositeViewRecursive(object view)
        {
            if (view == null) return;
            MethodInfo repaint = view.GetType().GetMethod("Repaint",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            repaint?.Invoke(view, null);

            foreach (object child in GetViewChildren(view))
                RepaintCompositeViewRecursive(child);
        }

        private static void ReleaseCompositeState()
        {
            CancelCompositeLayoutOperation();

            // Release each slot's SourceRT and close its EditorWindow.
            // EditorWindow.Close() removes the tab from the DockArea and, if it's
            // the last tab, closes the ContainerWindow through Unity's normal
            // destruction path (WM_CLOSE → DestroyWindow), which properly cleans
            // up D3D11 swap chains and RenderTextureMap entries.
            //
            // We do NOT call ContainerWindow.Close() directly via reflection.
            // That was introduced in commit 762d22a4 to prevent zombie HWNDs, but
            // it could destroy the MAIN editor's ContainerWindow when the slot's
            // EditorWindow was parented there — causing the editor to disappear.
            // Wrap in try-catch so one slot's failure doesn't block the rest.
            s_IsReleasingComposite = true;
            foreach (var slot in s_CompositeSlots.Values)
            {
                try
                {
                    ReleaseCompositeSlot(slot);
                }
                catch (Exception ex)
                {
                    CodelyLogger.LogError($"[NWB-Composite] ReleaseCompositeSlot failed for '{slot?.SlotId}': {ex.Message}");
                }
            }
            s_CompositeSlots.Clear();
            s_IsReleasingComposite = false;

            // Release global RenderTextures.
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
    }
}
