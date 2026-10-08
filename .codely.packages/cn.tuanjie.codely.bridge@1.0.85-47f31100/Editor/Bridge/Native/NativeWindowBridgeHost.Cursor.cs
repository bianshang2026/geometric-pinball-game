using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Native
{
    internal static partial class NativeWindowBridgeHost
    {
        private static FieldInfo s_SceneViewLastCursorField;
        private static string s_LastSentEditorCursorStyle;
        private static string s_ActiveDragCursorStyle;
        private static Vector2 s_LastCursorProbePos;
        private static bool s_HasLastCursorProbePos;
        private const float kSplitGrabDist = 5f;

        private static void EnsureEditorCursorReflection()
        {
            if (s_SceneViewLastCursorField != null)
                return;

            s_SceneViewLastCursorField = typeof(SceneView).GetField(
                "s_LastCursor",
                BindingFlags.Static | BindingFlags.NonPublic);
        }

        private static void RememberCursorProbePosition(Vector2 mousePos)
        {
            if (mousePos.sqrMagnitude > 0.01f)
                s_LastCursorProbePos = mousePos;
            else if (s_HasLastMousePos)
                s_LastCursorProbePos = s_LastMousePos;
            else if (s_OffscreenTarget != null)
            {
                Rect pos = s_OffscreenTarget.position;
                s_LastCursorProbePos = new Vector2(pos.width * 0.5f, pos.height * 0.5f);
            }
            s_HasLastCursorProbePos = true;
        }

        private static Vector2 GetCursorProbePosition()
        {
            return s_HasLastCursorProbePos ? s_LastCursorProbePos : Vector2.zero;
        }

        private static void SendGuiEvent(object view, Event evt)
        {
            if (view == null || evt == null)
                return;

            MethodInfo sendEvent = view.GetType().GetMethod(
                "SendEvent",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Event) },
                null);
            sendEvent?.Invoke(view, new object[] { evt });
        }

        private static void RunCursorGuiCycle(Vector2 mousePos)
        {
            if (s_OffscreenTarget == null)
                return;

            object hostView = GetParentView(s_OffscreenTarget);
            if (hostView != null)
            {
                SendGuiEvent(hostView, new Event { type = EventType.Layout, mousePosition = mousePos });
                SendGuiEvent(hostView, new Event { type = EventType.Repaint, mousePosition = mousePos });
                SendGuiEvent(hostView, new Event { type = EventType.MouseMove, mousePosition = mousePos, delta = Vector2.zero });
            }

            if (s_OffscreenTarget is SceneView)
            {
                s_OffscreenTarget.SendEvent(new Event { type = EventType.Layout, mousePosition = mousePos });
                s_OffscreenTarget.SendEvent(new Event { type = EventType.Repaint, mousePosition = mousePos });
                s_OffscreenTarget.SendEvent(new Event { type = EventType.MouseMove, mousePosition = mousePos, delta = Vector2.zero });
            }
        }

        private static MouseCursor FindSplitCursorForHotControl(object view, int hotControl)
        {
            if (view == null || hotControl == 0)
                return MouseCursor.Arrow;

            Type viewType = view.GetType();
            if (viewType.Name == "SplitView")
            {
                FieldInfo controlIdField = viewType.GetField("controlID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo verticalField = viewType.GetField("vertical", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo childrenProp = viewType.GetProperty("children", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (controlIdField != null && verticalField != null &&
                    (int)controlIdField.GetValue(view) == hotControl)
                {
                    return (bool)verticalField.GetValue(view)
                        ? MouseCursor.SplitResizeUpDown
                        : MouseCursor.SplitResizeLeftRight;
                }

                if (childrenProp?.GetValue(view) is Array children)
                {
                    for (int i = 0; i < children.Length; i++)
                    {
                        MouseCursor nested = FindSplitCursorForHotControl(children.GetValue(i), hotControl);
                        if (nested != MouseCursor.Arrow)
                            return nested;
                    }
                }
                return MouseCursor.Arrow;
            }

            PropertyInfo childProp = viewType.GetProperty("children", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (childProp?.GetValue(view) is Array childViews)
            {
                for (int i = 0; i < childViews.Length; i++)
                {
                    MouseCursor nested = FindSplitCursorForHotControl(childViews.GetValue(i), hotControl);
                    if (nested != MouseCursor.Arrow)
                        return nested;
                }
            }
            return MouseCursor.Arrow;
        }

        /// <summary>
        /// Split resize cursors live on DockArea/SplitView, not SceneView.s_LastCursor.
        /// </summary>
        private static MouseCursor TryResolveSplitCursor(object hostView, Vector2 mousePosInView)
        {
            if (hostView == null)
                return MouseCursor.Arrow;

            int hotControl = GUIUtility.hotControl;
            if (hotControl != 0)
            {
                object hostWindow = s_CompositeActive
                    ? GetCompositeHostWindow()
                    : GetViewWindow(hostView);
                MouseCursor dragCursor = FindSplitCursorForHotControl(GetRootSplitView(hostWindow), hotControl);
                if (dragCursor == MouseCursor.Arrow)
                    dragCursor = FindSplitCursorForHotControl(hostView, hotControl);
                if (dragCursor != MouseCursor.Arrow)
                    return dragCursor;
            }

            PropertyInfo parentProp = hostView.GetType().GetProperty("parent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            PropertyInfo positionProp = hostView.GetType().GetProperty("position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            object view = hostView;
            for (int guard = 0; guard < 8 && view != null; guard++)
            {
                object splitParent = parentProp?.GetValue(view);
                if (splitParent == null || splitParent.GetType().Name != "SplitView")
                    break;

                Type splitType = splitParent.GetType();
                MethodInfo indexOfChild = splitType.GetMethod("IndexOfChild", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo verticalField = splitType.GetField("vertical", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo controlIdField = splitType.GetField("controlID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo childrenProp = splitType.GetProperty("children", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (indexOfChild == null || verticalField == null || childrenProp == null)
                    break;

                int splitControlId = controlIdField != null ? (int)controlIdField.GetValue(splitParent) : 0;
                if (hotControl != 0 && hotControl != splitControlId)
                {
                    view = splitParent;
                    continue;
                }

                int idx = (int)indexOfChild.Invoke(splitParent, new[] { view });
                bool vertical = (bool)verticalField.GetValue(splitParent);
                int childCount = (childrenProp.GetValue(splitParent) as Array)?.Length ?? 0;
                Rect position = positionProp != null ? (Rect)positionProp.GetValue(view) : Rect.zero;

                if (vertical)
                {
                    if (idx != 0 && mousePosInView.y <= kSplitGrabDist)
                        return MouseCursor.SplitResizeUpDown;
                    if (idx != childCount - 1 &&
                        mousePosInView.y >= position.height - kSplitGrabDist + 1f &&
                        mousePosInView.y <= position.height)
                        return MouseCursor.SplitResizeUpDown;
                }
                else
                {
                    if (idx != 0 && mousePosInView.x <= kSplitGrabDist)
                        return MouseCursor.SplitResizeLeftRight;
                    if (idx != childCount - 1 &&
                        mousePosInView.x >= position.width - kSplitGrabDist + 1f &&
                        mousePosInView.x <= position.width)
                        return MouseCursor.SplitResizeLeftRight;
                }

                view = splitParent;
            }

            return MouseCursor.Arrow;
        }

        private static MouseCursor ViewToolToMouseCursor(ViewTool tool)
        {
            switch (tool)
            {
                case ViewTool.Pan: return MouseCursor.Pan;
                case ViewTool.Orbit: return MouseCursor.Orbit;
                case ViewTool.FPS: return MouseCursor.FPS;
                case ViewTool.Zoom: return MouseCursor.Zoom;
                default: return MouseCursor.Arrow;
            }
        }

        private static bool IsEditorViewToolCursorContext()
        {
            if (s_OffscreenScenePanLocked)
                return true;

#if UNITY_2020_1_OR_NEWER
            return global::UnityEditor.Tools.viewToolActive;
#else
            return global::UnityEditor.Tools.current == Tool.View;
#endif
        }

        private static MouseCursor TryResolveSceneViewCursor()
        {
            // During drag, SceneView clutch tools (right=FPS, alt+left=Orbit, etc.)
            // update Tools.viewTool but s_LastCursor is stale because we skip the
            // Layout/Repaint probe cycle to preserve hotControl.
            if (s_RemoteMouseButtonDown && IsEditorViewToolCursorContext())
                return ViewToolToMouseCursor(global::UnityEditor.Tools.viewTool);

            EnsureEditorCursorReflection();
            if (s_SceneViewLastCursorField == null)
                return MouseCursor.Arrow;

            try
            {
                return (MouseCursor)s_SceneViewLastCursorField.GetValue(null);
            }
            catch (Exception ex)
            {
                LogVerbose($"[NWB-Cursor] Read SceneView.s_LastCursor failed: {ex.Message}");
                return MouseCursor.Arrow;
            }
        }

        private static string MouseCursorToStyleId(MouseCursor cursor)
        {
            switch (cursor)
            {
                case MouseCursor.Text: return "text";
                case MouseCursor.ResizeVertical:
                case MouseCursor.SplitResizeUpDown: return "ns-resize";
                case MouseCursor.ResizeHorizontal:
                case MouseCursor.SplitResizeLeftRight: return "ew-resize";
                case MouseCursor.Link: return "pointer";
                case MouseCursor.SlideArrow: return "col-resize";
                case MouseCursor.ResizeUpRight: return "nesw-resize";
                case MouseCursor.ResizeUpLeft: return "nwse-resize";
                case MouseCursor.MoveArrow: return "move";
                case MouseCursor.RotateArrow: return "rotate";
                case MouseCursor.ScaleArrow: return "scale";
                case MouseCursor.ArrowPlus: return "copy";
                case MouseCursor.ArrowMinus: return "not-allowed";
                case MouseCursor.Pan: return "pan";
                case MouseCursor.Orbit: return "orbit";
                case MouseCursor.Zoom: return "zoom";
                case MouseCursor.FPS: return "fps";
                case MouseCursor.CustomCursor: return "custom";
                default: return "default";
            }
        }

        private static MouseCursor TryResolveEditorMouseCursor()
        {
            if (s_OffscreenTarget != null)
            {
                object hostView = GetParentView(s_OffscreenTarget);
                if (hostView != null)
                {
                    MouseCursor splitCursor = TryResolveSplitCursor(hostView, GetCursorProbePosition());
                    if (splitCursor != MouseCursor.Arrow)
                        return splitCursor;
                }
            }

            if (s_OffscreenTarget is SceneView)
            {
                MouseCursor sceneCursor = TryResolveSceneViewCursor();
                if (sceneCursor != MouseCursor.Arrow)
                    return sceneCursor;
            }

            return MouseCursor.Arrow;
        }

        private static void TrySendDefaultEditorCursor()
        {
            if (!s_OffscreenActive)
                return;

            string styleId = "default";
            if (styleId == "default" && s_RemoteMouseButtonDown && !string.IsNullOrEmpty(s_ActiveDragCursorStyle))
                styleId = s_ActiveDragCursorStyle;

            if (styleId == s_LastSentEditorCursorStyle)
                return;

            s_LastSentEditorCursorStyle = styleId;
            SendDataChannelMessage("{\"type\":\"cursor_style\",\"cursor\":\"" + styleId + "\"}");
            LogVerbose($"[NWB-Cursor] -> {styleId} (outside hit)");
        }

        private static void TrySendEditorCursorStyle()
        {
            if (!s_OffscreenActive || s_OffscreenTarget == null)
                return;

            if (s_OffscreenTargetType != null &&
                s_OffscreenTargetType.Name == "GameView" &&
                global::UnityEngine.Cursor.lockState == CursorLockMode.Locked)
            {
                return;
            }

            string styleId = MouseCursorToStyleId(TryResolveEditorMouseCursor());
            if (styleId == "default" && s_RemoteMouseButtonDown && !string.IsNullOrEmpty(s_ActiveDragCursorStyle))
                styleId = s_ActiveDragCursorStyle;
            else if (styleId != "default")
                s_ActiveDragCursorStyle = styleId;

            if (styleId == s_LastSentEditorCursorStyle)
                return;

            s_LastSentEditorCursorStyle = styleId;
            SendDataChannelMessage("{\"type\":\"cursor_style\",\"cursor\":\"" + styleId + "\"}");
            LogVerbose($"[NWB-Cursor] -> {styleId}");
        }

        private static void LockEditorDragCursorFromHover()
        {
            if (!string.IsNullOrEmpty(s_LastSentEditorCursorStyle) &&
                s_LastSentEditorCursorStyle != "default")
            {
                s_ActiveDragCursorStyle = s_LastSentEditorCursorStyle;
            }
        }

        private static void UpdateEditorCursorFromInput(string type, Vector2 mousePos)
        {
            if (s_OffscreenTarget == null || s_OffscreenTargetType == null ||
                s_OffscreenTargetType.Name == "GameView")
            {
                return;
            }

            if (type == "mouseup")
                s_ActiveDragCursorStyle = null;

            RememberCursorProbePosition(mousePos);
            mousePos = GetCursorProbePosition();
            if (!s_RemoteMouseButtonDown)
                RunCursorGuiCycle(mousePos);
            TrySendEditorCursorStyle();
        }

        private static void ClearEditorCursorState(bool notifyFrontend = true)
        {
            s_LastSentEditorCursorStyle = null;
            s_ActiveDragCursorStyle = null;
            s_HasLastCursorProbePos = false;
            if (!notifyFrontend || !s_OffscreenActive)
                return;

            SendDataChannelMessage("{\"type\":\"cursor_style\",\"cursor\":\"default\"}");
        }
    }
}
