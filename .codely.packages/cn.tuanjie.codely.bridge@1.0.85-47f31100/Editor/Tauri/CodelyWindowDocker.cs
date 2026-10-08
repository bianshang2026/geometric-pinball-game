#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
using System;
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEngine;

using UnityTcp.Editor.Helpers;
namespace Cn.Tuanjie.Codely.Editor
{
    /// <summary>
    /// Reflection helpers that wrap an EditorWindow in a fresh DockArea and splice it in
    /// as the leftmost child of the main editor's horizontal SplitView. Used so opening
    /// the Codely window does not require a destructive .wlt layout swap.
    /// </summary>
    internal static class CodelyWindowDocker
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags   = BindingFlags.Static   | BindingFlags.Public | BindingFlags.NonPublic;

        // ContainerWindow.m_ShowMode value for the main editor window. ShowMode.MainWindow == 4
        // in every Unity revision we ship against; see ContainerWindow.ShowMode in UnityCsReference.
        private const int ShowMode_MainWindow = 4;

        // width <= 0 means "auto": dock claims half of the main editor's working width.
        public static bool TryDockLeftmost(EditorWindow window, int width = 0)
        {
            if (window == null) return false;

            object newDockArea = null;
            try
            {
                var asm = typeof(EditorWindow).Assembly;
                var containerWindowType = asm.GetType("UnityEditor.ContainerWindow");
                var splitViewType       = asm.GetType("UnityEditor.SplitView");
                var dockAreaType        = asm.GetType("UnityEditor.DockArea");
                var viewType            = asm.GetType("UnityEditor.View");
                if (containerWindowType == null || splitViewType == null || dockAreaType == null || viewType == null)
                    return false;

                var mainContainer = FindMainContainerWindow(containerWindowType);
                if (mainContainer == null) return false;

                var rootViewProp = containerWindowType.GetProperty("rootView", InstanceFlags);
                var rootView = rootViewProp?.GetValue(mainContainer);
                if (rootView == null) return false;

                var mainSplit = FindOutermostHorizontalSplit(rootView, splitViewType, viewType);
                if (mainSplit == null) return false;

                var setPosition  = viewType.GetMethod("SetPosition", InstanceFlags, null, new[] { typeof(Rect) }, null);
                var positionProp = viewType.GetProperty("position", InstanceFlags);
                if (setPosition == null || positionProp == null) return false;

                var splitRect = (Rect)positionProp.GetValue(mainSplit);

                int effectiveWidth = width > 0
                    ? width
                    : Math.Max(100, (int)(splitRect.width));

                newDockArea = ScriptableObject.CreateInstance(dockAreaType);
                if (newDockArea == null) return false;

                if (!InvokeAddTab(dockAreaType, newDockArea, window))
                    return false;

                // Seed the new dock's width before splice so SplitView.SetupSplitState reads
                // effectiveWidth (not 0) when it rebuilds splitState from children's current rects.
                setPosition.Invoke(newDockArea, new object[] { new Rect(0, 0, effectiveWidth, splitRect.height) });

                var addChild = viewType.GetMethod("AddChild", InstanceFlags, null, new[] { viewType, typeof(int) }, null);
                if (addChild == null)
                    return false;
                addChild.Invoke(mainSplit, new[] { newDockArea, 0 });

                // SplitView nulled splitState in AddChild; re-applying its own rect triggers
                // SetupSplitState (seeds from the children's positions, including our seeded
                // width hint) followed by SetupRectsFromSplitState.
                setPosition.Invoke(mainSplit, new object[] { splitRect });

                // Final pass: clamp leftmost child to exactly `effectiveWidth` and absorb the
                // delta from existing siblings proportionally. Without this, Unity's
                // SplitterState can snap to even shares when total > parent width.
                EnforceLeftmostWidth(mainSplit, splitViewType, viewType, effectiveWidth, splitRect, setPosition);

                newDockArea = null;
                return true;
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[Codely] TryDockLeftmost failed: {e.Message}");
                return false;
            }
            finally
            {
                if (newDockArea is UnityEngine.Object orphan)
                    UnityEngine.Object.DestroyImmediate(orphan);
            }
        }

        private static void EnforceLeftmostWidth(
            object split, Type splitViewType, Type viewType,
            int targetWidth, Rect splitRect, MethodInfo setPosition)
        {
            var splitStateField = splitViewType.GetField("splitState", InstanceFlags);
            var splitState = splitStateField?.GetValue(split);
            if (splitState == null) return;

            var realSizesField = splitState.GetType().GetField("realSizes", InstanceFlags);
            var sizes = realSizesField?.GetValue(splitState) as int[];
            if (sizes == null || sizes.Length < 2) return;

            int parentWidth = (int)splitRect.width;
            if (parentWidth <= 0) return;

            // Reserve at least 200px total for the rest of the docks so we never starve them.
            int minRest = Math.Max(200, 100 * (sizes.Length - 1));
            int clamped = Math.Min(targetWidth, Math.Max(100, parentWidth - minRest));

            int oldRest = 0;
            for (int i = 1; i < sizes.Length; i++) oldRest += sizes[i];

            int newRest = parentWidth - clamped;
            sizes[0] = clamped;

            if (oldRest > 0)
            {
                int accum = 0;
                for (int i = 1; i < sizes.Length - 1; i++)
                {
                    int scaled = (int)((float)sizes[i] / oldRest * newRest);
                    sizes[i] = Math.Max(100, scaled);
                    accum += sizes[i];
                }
                sizes[sizes.Length - 1] = Math.Max(100, newRest - accum);
            }
            else
            {
                int each = newRest / (sizes.Length - 1);
                for (int i = 1; i < sizes.Length; i++) sizes[i] = each;
            }

            setPosition.Invoke(split, new object[] { splitRect });
        }

        private static object FindMainContainerWindow(Type containerWindowType)
        {
            var windowsProp = containerWindowType.GetProperty("windows", StaticFlags);
            var all = windowsProp?.GetValue(null) as IEnumerable;
            if (all == null) return null;

            var showModeField = containerWindowType.GetField("m_ShowMode", InstanceFlags);
            if (showModeField == null) return null;

            foreach (var cw in all)
            {
                if (Convert.ToInt32(showModeField.GetValue(cw)) == ShowMode_MainWindow)
                    return cw;
            }
            return null;
        }

        private static object FindOutermostHorizontalSplit(object view, Type splitViewType, Type viewType)
        {
            var allChildrenProp = viewType.GetProperty("allChildren", InstanceFlags);
            var children = allChildrenProp?.GetValue(view) as Array;
            if (children == null) return null;

            foreach (var child in children)
            {
                if (splitViewType.IsInstanceOfType(child) && !IsVertical(child, splitViewType))
                    return child;
            }

            foreach (var child in children)
            {
                if (viewType.IsInstanceOfType(child))
                {
                    var nested = FindOutermostHorizontalSplit(child, splitViewType, viewType);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        private static bool IsVertical(object splitView, Type splitViewType)
        {
            var verticalField = splitViewType.GetField("vertical", InstanceFlags);
            if (verticalField == null) return false;
            var value = verticalField.GetValue(splitView);
            return value is bool b && b;
        }

        private static bool InvokeAddTab(Type dockAreaType, object dockArea, EditorWindow window)
        {
            var oneArg = dockAreaType.GetMethod("AddTab", InstanceFlags, null, new[] { typeof(EditorWindow) }, null);
            if (oneArg != null) { oneArg.Invoke(dockArea, new object[] { window }); return true; }

            var oneArgBool = dockAreaType.GetMethod("AddTab", InstanceFlags, null, new[] { typeof(EditorWindow), typeof(bool) }, null);
            if (oneArgBool != null) { oneArgBool.Invoke(dockArea, new object[] { window, true }); return true; }

            var indexedBool = dockAreaType.GetMethod("AddTab", InstanceFlags, null, new[] { typeof(int), typeof(EditorWindow), typeof(bool) }, null);
            if (indexedBool != null) { indexedBool.Invoke(dockArea, new object[] { 0, window, true }); return true; }

            var indexed = dockAreaType.GetMethod("AddTab", InstanceFlags, null, new[] { typeof(int), typeof(EditorWindow) }, null);
            if (indexed != null) { indexed.Invoke(dockArea, new object[] { 0, window }); return true; }

            return false;
        }
    }
}
#endif
