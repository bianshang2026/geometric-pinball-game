using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Native
{
    // Popup window interception — auto-dock to composite DockArea.
    // Detects new EditorWindows created after composite streaming starts
    // (e.g. via menu items or context-menu "Add Tab") and automatically
    // docks them as tabs in the existing composite DockArea.
    internal static partial class NativeWindowBridgeHost
    {
        // Countdown frames for scanning untracked panes after native "Add Tab".
        // Set to >0 when a trigger is detected; decremented each frame until 0.
        // Used by composite pane tracking loops on supported streaming platforms.
#pragma warning disable CS0414
        private static int s_CompositeTrackPanesCountdown;
#pragma warning restore CS0414

        // Baseline set of EditorWindow InstanceIDs captured when composite
        // streaming starts. Windows not in this set are considered "new".
        private static HashSet<long> s_CompositeKnownWindowIds = new HashSet<long>();

        /// <summary>
        /// Detect EditorWindows in the composite ContainerWindow that are NOT
        /// tracked in s_CompositeSlots (e.g. tabs added via native "Add Tab"
        /// context menu) and create slots for them so the capture loop can
        /// capture their content. Called each frame from CaptureAndPushCompositeFrame.
        /// </summary>
        private static void TrackUntrackedCompositePanes()
        {
            if (!s_CompositeActive || s_CompositeSlots.Count == 0) return;

            // Collect tracked InstanceIDs
            var trackedIds = new HashSet<long>();
            foreach (var slot in s_CompositeSlots.Values)
                if (slot?.Window != null)
                    trackedIds.Add(slot.Window.GetStableInstanceId());

            object compositeHostWindow = GetCompositeHostWindow();
            if (compositeHostWindow == null) return;

            // Get NormalizedRect from first slot (new tab shares the same rect)
            Rect defaultRect = new Rect(0, 0, 1, 1);
            foreach (var slot in s_CompositeSlots.Values)
            {
                if (slot != null) { defaultRect = slot.NormalizedRect; break; }
            }

            bool anyAdded = false;
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (w == null || trackedIds.Contains(w.GetStableInstanceId())) continue;

                object parentView = GetParentView(w);
                if (parentView == null) continue;
                object windowHost = GetViewWindow(parentView);
                if (windowHost == null || !ReferenceEquals(windowHost, compositeHostWindow))
                    continue;

                string typeName = w.GetType().FullName ?? w.GetType().Name;
                if (typeName == "UnityTcp.Editor.Native.StreamingMaskWindow") continue;

                string slotId = "native-" + typeName.Replace(".", "-").ToLowerInvariant()
                    + "-" + w.GetStableInstanceId();
                var newSlot = new CompositeCaptureSlot
                {
                    SlotId = slotId,
                    WindowTypeName = typeName,
                    WindowType = w.GetType(),
                    Window = w,
                    OwnsWindow = false,
                    CloseWindowOnRelease = true,
                    NormalizedRect = defaultRect
                };
#if UNITY_EDITOR_WIN
                newSlot.ContainerHwnd = Cn.Tuanjie.Codely.Editor.NativeWindowHelper.GetHWND(w);
                IntPtr reflectedGUIView = Cn.Tuanjie.Codely.Editor.EditorWindowNativeHandleHelper
                    .GetGUIViewHandle(w);
                if (reflectedGUIView != IntPtr.Zero)
                    newSlot.GUIViewHwnd = reflectedGUIView;
                // Remove WS_EX_LAYERED to prevent DoPaint transparent mode
                if (newSlot.ContainerHwnd != IntPtr.Zero)
                {
                    int style = GetWindowLongW(newSlot.ContainerHwnd, GWL_EXSTYLE);
                    if ((style & WS_EX_LAYERED) != 0)
                        SetWindowLongW(newSlot.ContainerHwnd, GWL_EXSTYLE, style & ~WS_EX_LAYERED);
                }
#endif
#if UNITY_EDITOR_OSX
                foreach (var existSlot in s_CompositeSlots.Values)
                {
                    if (existSlot?.Window != null && existSlot.NSWindow != IntPtr.Zero)
                    { newSlot.NSWindow = existSlot.NSWindow; break; }
                }
#endif
                s_CompositeSlots[slotId] = newSlot;
                w.autoRepaintOnSceneChange = true;
                ForceDoubleLayoutRepaint(w);
                w.Repaint();
                anyAdded = true;
                CodelyLogger.Log($"[NWB-Composite] Auto-tracked native tab: {typeName} (id={w.GetStableInstanceId()})");
            }

            if (anyAdded)
                EditorApplication.QueuePlayerLoopUpdate();
        }

        /// <summary>
        /// Snapshot all current EditorWindow InstanceIDs so new windows created
        /// AFTER this call can be detected by InterceptAndDockNewPopupWindows.
        /// Called once when composite streaming first starts.
        /// </summary>
        private static void InitCompositeWindowTracking()
        {
            s_CompositeKnownWindowIds.Clear();
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (w != null)
                    s_CompositeKnownWindowIds.Add(w.GetStableInstanceId());
            }
            CodelyLogger.Log($"[NWB-Composite] InitCompositeWindowTracking: baseline={s_CompositeKnownWindowIds.Count} windows");
        }

        /// <summary>
        /// Detect EditorWindows that appeared AFTER composite streaming started
        /// and automatically dock them as tabs in the composite DockArea.
        /// Called from ManageWindowBridge when the window list changes.
        /// Returns true if any windows were docked (caller should re-push the
        /// window list so the /windows endpoint reflects the new docked state).
        /// The frontend learns about the new tab via editor_windows_changed →
        /// refreshDynamicWindows, so no separate DataChannel notification is needed.
        /// </summary>
        internal static bool InterceptAndDockNewPopupWindows()
        {
            if (!s_CompositeActive || s_CompositeSlots.Count == 0)
                return false;

            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var currentIds = new HashSet<long>();
            var newWindows = new System.Collections.Generic.List<EditorWindow>();

            foreach (var w in allWindows)
            {
                if (w == null) continue;
                long id = w.GetStableInstanceId();
                currentIds.Add(id);
                if (!s_CompositeKnownWindowIds.Contains(id))
                    newWindows.Add(w);
            }

            // Update known set so closed-then-reopened windows are still tracked
            s_CompositeKnownWindowIds = currentIds;

            if (newWindows.Count == 0) return false;

            // Collect composite slot InstanceIDs for fast lookup
            var compositeInstanceIds = new HashSet<long>();
            foreach (var slot in s_CompositeSlots.Values)
            {
                if (slot?.Window != null)
                    compositeInstanceIds.Add(slot.Window.GetStableInstanceId());
            }

            object compositeDockArea = FindExistingCompositeDockArea();
            if (compositeDockArea == null)
            {
                CodelyLogger.Log("[NWB-PopupDock] InterceptPopup: no composite DockArea found, skipping");
                return false;
            }

            // Get the composite's ContainerWindow for comparison
            object compositeHostWindow = GetCompositeHostWindow();
            bool anyDocked = false;

            foreach (var w in newWindows)
            {
                string typeName = w.GetType().FullName ?? w.GetType().Name;
                string title = w.titleContent?.text ?? "";

                if (compositeInstanceIds.Contains(w.GetStableInstanceId()))
                {
                    LogVerbose($"[NWB-PopupDock] Skip already-tracked: {typeName} \"{title}\"");
                    continue;
                }

                // Skip streaming infrastructure windows
                if (typeName == "UnityTcp.Editor.Native.StreamingMaskWindow")
                    continue;
                if (title.EndsWith(" - Streaming", StringComparison.Ordinal))
                    continue;

                // Skip windows that have no parent (not yet fully initialized)
                object parentView = GetParentView(w);
                if (parentView == null)
                {
                    LogVerbose($"[NWB-PopupDock] Skip no-parent: {typeName} \"{title}\"");
                    continue;
                }

                // Skip windows already in the composite ContainerWindow
                if (compositeHostWindow != null)
                {
                    object windowHost = GetViewWindow(parentView);
                    if (windowHost != null && ReferenceEquals(windowHost, compositeHostWindow))
                    {
                        LogVerbose($"[NWB-PopupDock] Skip already-in-composite: {typeName} \"{title}\"");
                        continue;
                    }
                }

                // Dock this popup into the composite DockArea
                if (TryDockWindowToCompositeDockArea(w, compositeDockArea, typeName, title))
                {
                    anyDocked = true;
                    CodelyLogger.Log($"[NWB-PopupDock] Auto-docked popup as tab: {typeName} \"{title}\"");
                }
                else
                {
                    CodelyLogger.LogWarning($"[NWB-PopupDock] Failed to dock: {typeName} \"{title}\"");
                }
            }

            // Schedule pane tracking so newly-docked windows get a proper
            // CompositeCaptureSlot (required for capture after drag-out).
            if (anyDocked)
                s_CompositeTrackPanesCountdown = 3;

            return anyDocked;
        }

        /// <summary>
        /// Move an EditorWindow from its current DockArea into the composite
        /// DockArea using RemoveTab + AddTab (same proven pattern as TabDrag).
        /// The old DockArea / ContainerWindow is cleaned up automatically
        /// when killIfEmpty=true.
        /// </summary>
        private static bool TryDockWindowToCompositeDockArea(
            EditorWindow window, object targetDockArea, string typeName, string title)
        {
            if (window == null || targetDockArea == null) return false;

            try
            {
                EnsureTabDragReflection();

                object sourceDockArea = GetParentView(window);
                if (sourceDockArea == null || sourceDockArea.GetType().Name != "DockArea")
                {
                    LogVerbose($"[NWB-Composite] InterceptPopup: {typeName} has no source DockArea, skipping");
                    return false;
                }

                // Remove from old DockArea (killIfEmpty=true closes empty container)
                if (s_DockRemoveTabMethod != null)
                {
                    bool killIfEmpty = !ReferenceEquals(sourceDockArea, targetDockArea);
                    s_DockRemoveTabMethod.Invoke(sourceDockArea, new object[] { window, killIfEmpty, false });
                }
                else
                {
                    LogVerbose("[NWB-Composite] InterceptPopup: RemoveTab method unavailable, trying direct AddTab");
                }

                // Add as tab to composite DockArea
                if (s_DockAddTabIndexedMethod != null)
                {
                    int insertIndex = GetDockPaneCount(targetDockArea);
                    s_DockAddTabIndexedMethod.Invoke(targetDockArea, new object[] { insertIndex, window, false });
                }
                else
                {
                    // Fallback: use the simpler AddTab(EditorWindow, bool) overload
                    System.Type dockAreaType = targetDockArea.GetType();
                    MethodInfo addTab = dockAreaType.GetMethod("AddTab",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new System.Type[] { typeof(EditorWindow), typeof(bool) },
                        null);
                    if (addTab == null)
                    {
                        CodelyLogger.LogWarning("[NWB-Composite] InterceptPopup: no AddTab method found");
                        return false;
                    }
                    addTab.Invoke(targetDockArea, new object[] { window, false });
                }

                // Enable autoRepaintOnSceneChange for proper capture rendering
                window.autoRepaintOnSceneChange = true;

                // Initialize GPU buffers for capture
                ForceDoubleLayoutRepaint(window);
                window.Repaint();
                EditorApplication.QueuePlayerLoopUpdate();

                // Repaint composite windows to reflect new tab bar
                foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
                    slot?.Window?.Repaint();

                return true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning(
                    $"[NWB-Composite] InterceptPopup: failed to dock {typeName}: " +
                    (ex.InnerException?.Message ?? ex.Message));
                return false;
            }
        }
    }
}
