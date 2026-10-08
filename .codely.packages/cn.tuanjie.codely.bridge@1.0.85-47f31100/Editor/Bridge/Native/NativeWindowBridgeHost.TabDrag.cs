using System;
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Native
{
    internal static partial class NativeWindowBridgeHost
    {
        private static Vector2 s_LastInputScreenPoint;
        private static bool s_TabDragReflectionDone;
        private static Type s_DockAreaType;
        private static FieldInfo s_DockIsDraggingField;
        private static FieldInfo s_DockDragPaneField;
        private static FieldInfo s_DockDropInfoField;
        private static FieldInfo s_DockOriginalDragSourceField;
        private static FieldInfo s_DockPanesField;
        private static MethodInfo s_DockResetDragVarsMethod;
        private static MethodInfo s_DockRemoveTabMethod;
        private static MethodInfo s_DockAddTabIndexedMethod;
        private static PropertyInfo s_DockSelectedProperty;
        private static Type s_DropInfoType;
        private static FieldInfo s_DropInfoDropAreaField;
        private static Type s_PaneDragTabType;
        private static PropertyInfo s_PaneDragTabGetProperty;
        private static MethodInfo s_PaneDragTabCloseMethod;

        private static void EnsureTabDragReflection()
        {
            if (s_TabDragReflectionDone) return;
            s_TabDragReflectionDone = true;

            try
            {
                var editorAsm = typeof(EditorWindow).Assembly;
                s_DockAreaType = editorAsm.GetType("UnityEditor.DockArea");
                s_DropInfoType = editorAsm.GetType("UnityEditor.DropInfo");
                s_PaneDragTabType = editorAsm.GetType("UnityEditor.PaneDragTab");

                if (s_DockAreaType != null)
                {
                    const BindingFlags f = BindingFlags.Static | BindingFlags.NonPublic;
                    s_DockIsDraggingField = s_DockAreaType.GetField("s_IsDragging", f);
                    s_DockDragPaneField = s_DockAreaType.GetField("s_DragPane", f);
                    s_DockDropInfoField = s_DockAreaType.GetField("s_DropInfo", f);
                    s_DockOriginalDragSourceField = s_DockAreaType.GetField("s_OriginalDragSource", f);
                    s_DockPanesField = s_DockAreaType.GetField("m_Panes", BindingFlags.Instance | BindingFlags.NonPublic);
                    s_DockResetDragVarsMethod = s_DockAreaType.GetMethod("ResetDragVars", f);
                    s_DockRemoveTabMethod = s_DockAreaType.GetMethod(
                        "RemoveTab",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new[] { typeof(EditorWindow), typeof(bool), typeof(bool) },
                        null);
                    s_DockAddTabIndexedMethod = s_DockAreaType.GetMethod(
                        "AddTab",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        new[] { typeof(int), typeof(EditorWindow), typeof(bool) },
                        null);
                    s_DockSelectedProperty = s_DockAreaType.GetProperty(
                        "selected", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (s_DropInfoType != null)
                    s_DropInfoDropAreaField = s_DropInfoType.GetField("dropArea", BindingFlags.Instance | BindingFlags.Public);

                if (s_PaneDragTabType != null)
                {
                    s_PaneDragTabGetProperty = s_PaneDragTabType.GetProperty(
                        "get", BindingFlags.Static | BindingFlags.Public);
                    s_PaneDragTabCloseMethod = s_PaneDragTabType.GetMethod(
                        "Close", BindingFlags.Instance | BindingFlags.Public);
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-TabDrag] Reflection init failed: {ex.Message}");
            }
        }

        private static void UpdateLastInputScreenPoint(float rawX, float rawY, float ppp, Vector2 localMousePos)
        {
            if (s_CompositeActive)
            {
                s_LastInputScreenPoint = new Vector2(
                    s_CompositeFrameBoundsLogical.xMin +
                        (rawX - s_CompositeFrameOffsetPixels.x) /
                        Mathf.Max(s_CompositeFrameScalePixels.x * ppp, 0.001f),
                    s_CompositeFrameBoundsLogical.yMin +
                        (rawY - s_CompositeFrameOffsetPixels.y) /
                        Mathf.Max(s_CompositeFrameScalePixels.y * ppp, 0.001f));
                return;
            }

            object parentView = s_OffscreenTarget != null ? GetParentView(s_OffscreenTarget) : null;
            if (parentView != null)
            {
                Rect viewRect = GetViewScreenPosition(parentView);
                s_LastInputScreenPoint = new Vector2(viewRect.xMin + localMousePos.x, viewRect.yMin + localMousePos.y);
            }
            else
            {
                s_LastInputScreenPoint = localMousePos;
            }
        }

        private static bool IsStreamingTabDragPendingFloatingWindow()
        {
            if (!s_CompositeActive && !s_OffscreenActive) return false;

            EnsureTabDragReflection();
            if (s_DockIsDraggingField == null || !(bool)s_DockIsDraggingField.GetValue(null))
                return false;

            object dropInfo = s_DockDropInfoField?.GetValue(null);
            if (dropInfo != null && s_DropInfoDropAreaField != null)
            {
                object dropArea = s_DropInfoDropAreaField.GetValue(dropInfo);
                if (dropArea != null)
                    return false;
            }

            return s_DockDragPaneField?.GetValue(null) is EditorWindow;
        }

        private static object FindDockAreaAtScreenPoint(Vector2 screenPoint)
        {
            object bestDock = null;
            float bestArea = float.MaxValue;

            if (s_CompositeActive)
            {
                foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
                {
                    if (slot?.Window == null) continue;
                    object dock = GetParentView(slot.Window);
                    if (dock == null || dock.GetType().Name != "DockArea") continue;

                    Rect rect = GetViewScreenPosition(dock);
                    if (!rect.Contains(screenPoint)) continue;

                    float area = rect.width * rect.height;
                    if (area < bestArea)
                    {
                        bestArea = area;
                        bestDock = dock;
                    }
                }

                if (bestDock != null) return bestDock;
                return FindExistingCompositeDockArea();
            }

            if (s_OffscreenTarget != null)
            {
                object dock = GetParentView(s_OffscreenTarget);
                if (dock != null && dock.GetType().Name == "DockArea")
                    return dock;
            }

            return null;
        }

        private static int GetDockPaneCount(object dockArea)
        {
            if (dockArea == null || s_DockPanesField == null) return 0;
            IList panes = s_DockPanesField.GetValue(dockArea) as IList;
            return panes?.Count ?? 0;
        }

        /// <summary>
        /// When a tab is released over the streaming canvas center, Unity would
        /// create a new floating ContainerWindow (DropInfo.dropArea == null).
        /// Re-route that drop as AddTab on the DockArea under the cursor instead.
        /// Edge/pane drops (dropArea set) still go through SendEvent normally.
        /// </summary>
        private static bool TryCompleteStreamingTabDragAsAddTab(Vector2 screenMousePos)
        {
            if (!IsStreamingTabDragPendingFloatingWindow()) return false;

            EnsureTabDragReflection();
            if (s_DockRemoveTabMethod == null || s_DockAddTabIndexedMethod == null)
                return false;

            var dragPane = s_DockDragPaneField?.GetValue(null) as EditorWindow;
            var originalSource = s_DockOriginalDragSourceField?.GetValue(null);
            if (dragPane == null || originalSource == null) return false;

            object targetDock = FindDockAreaAtScreenPoint(screenMousePos);
            if (targetDock == null)
            {
                LogVerbose("[NWB-TabDrag] No target DockArea for AddTab fallback");
                return false;
            }

            try
            {
                bool killIfEmpty = !ReferenceEquals(originalSource, targetDock);
                s_DockRemoveTabMethod.Invoke(originalSource, new object[] { dragPane, killIfEmpty, false });

                int insertIndex = GetDockPaneCount(targetDock);
                s_DockAddTabIndexedMethod.Invoke(targetDock, new object[] { insertIndex, dragPane, false });

                if (s_DockSelectedProperty != null && s_DockSelectedProperty.CanWrite)
                    s_DockSelectedProperty.SetValue(targetDock, insertIndex);

                CloseActivePaneDragTab();
                s_DockResetDragVarsMethod?.Invoke(null, null);
                if (GUIUtility.hotControl != 0)
                    GUIUtility.hotControl = 0;

                dragPane.Repaint();
                if (targetDock is ScriptableObject dockSo)
                {
                    MethodInfo repaint = dockSo.GetType().GetMethod(
                        "Repaint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null);
                    repaint?.Invoke(dockSo, null);
                }

                if (s_CompositeActive)
                {
                    foreach (CompositeCaptureSlot slot in s_CompositeSlots.Values)
                        slot?.Window?.Repaint();
                }
                else
                {
                    s_OffscreenTarget?.Repaint();
                }

                CodelyLogger.Log(
                    $"[NWB-TabDrag] AddTab fallback: {dragPane.GetType().Name} -> DockArea at ({screenMousePos.x:F0},{screenMousePos.y:F0})");
                return true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-TabDrag] AddTab fallback failed: {ex.Message}");
                return false;
            }
        }

        private static void CloseActivePaneDragTab()
        {
            if (s_PaneDragTabGetProperty == null || s_PaneDragTabCloseMethod == null) return;
            object paneDrag = s_PaneDragTabGetProperty.GetValue(null, null);
            if (paneDrag != null)
                s_PaneDragTabCloseMethod.Invoke(paneDrag, null);
        }
    }
}
