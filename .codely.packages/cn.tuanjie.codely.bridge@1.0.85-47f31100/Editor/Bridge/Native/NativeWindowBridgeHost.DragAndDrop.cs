using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Native
{
    internal static partial class NativeWindowBridgeHost
    {
        private static int s_DragAndDropTestingLogCount;
        private static int s_ProjectSelectionSnapshotLogCount;
        private static int s_BlockedNativeProjectDragLogCount;
        private static int s_NativeDragCancelWatchdogVersion;
        private static int s_SyntheticDragThresholdLogCount;
        private static bool s_RemoteNativeDragAndDropActive;
        private static EditorWindow s_RemoteNativeDragSourceWindow;
        private static UnityEngine.Object[] s_ProjectAssetDragObjects;
        private static string[] s_ProjectAssetDragPaths;
        private static bool s_SyntheticDragGestureTracking;
        private static Vector2 s_SyntheticDragStartPosition;
        private static string s_SyntheticDragWindowTypeName;

        // Ignore tiny pointer jitter so click interactions (e.g. hierarchy foldout)
        // are not misclassified as drag gestures.
        private const float kSyntheticDragStartThreshold = 3f;
        private const float kSyntheticDragStartThresholdSqr =
            kSyntheticDragStartThreshold * kSyntheticDragStartThreshold;

        private static bool ShouldStartSyntheticProjectAssetDragForInput(string inputType, string windowTypeName)
        {
            return inputType == "mousedrag" &&
                (windowTypeName == "ProjectBrowser" || windowTypeName == "UnityEditor.ProjectBrowser");
        }

        private static bool ShouldStartSyntheticHierarchyGameObjectDragForInput(string inputType, string windowTypeName)
        {
            return inputType == "mousedrag" &&
                (windowTypeName == "SceneHierarchyWindow" || windowTypeName == "UnityEditor.SceneHierarchyWindow");
        }

        private static bool ShouldCaptureProjectAssetSelectionForInput(string inputType, string windowTypeName)
        {
            return inputType == "mousedown" &&
                (windowTypeName == "ProjectBrowser" || windowTypeName == "UnityEditor.ProjectBrowser");
        }

        private static bool ShouldBlockNativeEditorDragStart(string inputType, string windowTypeName)
        {
            return ShouldStartSyntheticProjectAssetDragForInput(inputType, windowTypeName) ||
                ShouldStartSyntheticHierarchyGameObjectDragForInput(inputType, windowTypeName);
        }

        private static bool ShouldForwardNativeDragAndDropEvent(string inputType)
        {
            return inputType == "mousedrag" || inputType == "mouseup";
        }

        private static bool ShouldUseCompositeMouseCaptureForInput(string inputType)
        {
            return !s_RemoteNativeDragAndDropActive || !ShouldForwardNativeDragAndDropEvent(inputType);
        }

        private static bool ShouldTrackSyntheticDragWindowType(string windowTypeName)
        {
            return windowTypeName == "ProjectBrowser" ||
                windowTypeName == "UnityEditor.ProjectBrowser" ||
                windowTypeName == "SceneHierarchyWindow" ||
                windowTypeName == "UnityEditor.SceneHierarchyWindow";
        }

        private static void UpdateSyntheticDragGestureState(
            EditorWindow window,
            string inputType,
            Vector2 mousePosition)
        {
            string windowTypeName = window?.GetType().FullName ?? window?.GetType().Name;
            bool trackableWindow = !string.IsNullOrEmpty(windowTypeName) &&
                ShouldTrackSyntheticDragWindowType(windowTypeName);

            if (inputType == "mousedown")
            {
                if (trackableWindow)
                {
                    s_SyntheticDragGestureTracking = true;
                    s_SyntheticDragStartPosition = mousePosition;
                    s_SyntheticDragWindowTypeName = windowTypeName;
                }
                else
                {
                    ClearSyntheticDragGestureState();
                }
                return;
            }

            if (inputType == "mouseup")
            {
                ClearSyntheticDragGestureState();
            }
        }

        private static bool HasSyntheticDragThresholdMet(
            EditorWindow window,
            string inputType,
            Vector2 mousePosition)
        {
            if (window == null || inputType != "mousedrag")
                return false;

            string windowTypeName = window.GetType().FullName ?? window.GetType().Name;
            if (!s_SyntheticDragGestureTracking ||
                string.IsNullOrEmpty(s_SyntheticDragWindowTypeName) ||
                !string.Equals(s_SyntheticDragWindowTypeName, windowTypeName, StringComparison.Ordinal))
            {
                return false;
            }

            float sqrDistance = (mousePosition - s_SyntheticDragStartPosition).sqrMagnitude;
            bool reachedThreshold = sqrDistance >= kSyntheticDragStartThresholdSqr;
            if (!reachedThreshold)
            {
                s_SyntheticDragThresholdLogCount++;
                if (s_SyntheticDragThresholdLogCount <= 10 || s_SyntheticDragThresholdLogCount % 100 == 0)
                {
                    LogVerbose(
                        $"[NWB-DnD] Ignore tiny drag before threshold: " +
                        $"win={windowTypeName} dist={Mathf.Sqrt(sqrDistance):F2}px " +
                        $"threshold={kSyntheticDragStartThreshold:F1}px");
                }
            }
            return reachedThreshold;
        }

        private static void ClearSyntheticDragGestureState()
        {
            s_SyntheticDragGestureTracking = false;
            s_SyntheticDragStartPosition = Vector2.zero;
            s_SyntheticDragWindowTypeName = null;
        }

        private static bool TryStartSyntheticProjectAssetDrag(
            EditorWindow window,
            string inputType,
            Vector2 mousePosition)
        {
            if (window == null) return false;
            if (s_RemoteNativeDragAndDropActive && !HasActiveUnityDragAndDropData())
                FinishRemoteNativeDragAndDrop();
            if (s_RemoteNativeDragAndDropActive) return false;
            if (!ShouldStartSyntheticProjectAssetDragForInput(inputType, window.GetType().FullName ?? window.GetType().Name))
                return false;
            if (!HasSyntheticDragThresholdMet(window, inputType, mousePosition))
                return false;

            try
            {
                var refs = new System.Collections.Generic.List<UnityEngine.Object>();
                var paths = new System.Collections.Generic.List<string>();
                AddCachedProjectAssetDragObjects(refs, paths);
                if (refs.Count == 0)
                {
                    foreach (UnityEngine.Object obj in Selection.objects)
                    {
                        if (obj == null) continue;
                        string path = AssetDatabase.GetAssetPath(obj);
                        if (string.IsNullOrEmpty(path)) continue;
                        refs.Add(obj);
                        paths.Add(path);
                    }
                }

                if (refs.Count == 0)
                    return false;

                BeginRemoteNativeDragAndDrop(
                    window,
                    refs.ToArray(),
                    paths.ToArray(),
                    DragAndDropVisualMode.Generic,
                    $"Started synthetic Project asset drag: count={refs.Count}");
                return true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-DnD] Failed to start synthetic Project asset drag: {ex.Message}");
                return false;
            }
        }

        private static bool TryStartSyntheticHierarchyGameObjectDrag(
            EditorWindow window,
            string inputType,
            Vector2 mousePosition)
        {
            if (window == null) return false;
            if (s_RemoteNativeDragAndDropActive && !HasActiveUnityDragAndDropData())
                FinishRemoteNativeDragAndDrop();
            if (s_RemoteNativeDragAndDropActive) return false;
            if (!ShouldStartSyntheticHierarchyGameObjectDragForInput(inputType, window.GetType().FullName ?? window.GetType().Name))
                return false;
            if (!HasSyntheticDragThresholdMet(window, inputType, mousePosition))
                return false;

            try
            {
                var refs = new System.Collections.Generic.List<UnityEngine.Object>();
                AddHierarchyDragGameObjects(window, refs);
                if (refs.Count == 0)
                {
                    foreach (UnityEngine.Object obj in Selection.objects)
                    {
                        if (obj is GameObject)
                            refs.Add(obj);
                    }
                }

                if (refs.Count == 0)
                    return false;

                BeginRemoteNativeDragAndDrop(
                    window,
                    refs.ToArray(),
                    new string[0],
                    DragAndDropVisualMode.Move,
                    $"Started synthetic Hierarchy GameObject drag: count={refs.Count}");
                return true;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-DnD] Failed to start synthetic Hierarchy GameObject drag: {ex.Message}");
                return false;
            }
        }

        private static void BeginRemoteNativeDragAndDrop(
            EditorWindow sourceWindow,
            UnityEngine.Object[] objectReferences,
            string[] paths,
            DragAndDropVisualMode visualMode,
            string logMessage)
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = objectReferences;
            DragAndDrop.paths = paths;
            DragAndDrop.visualMode = visualMode;
            ClearRemoteDragControls();
            s_CompositeMouseCaptureView = null;
            s_CompositeMouseCaptureSlot = null;
            s_RemoteNativeDragAndDropActive = true;
            s_RemoteNativeDragSourceWindow = sourceWindow;
            ClearSyntheticDragGestureState();
            s_DragAndDropTestingLogCount++;
            if (s_DragAndDropTestingLogCount <= 10 || s_DragAndDropTestingLogCount % 100 == 0)
                LogVerbose($"[NWB-DnD] {logMessage}");
        }

        private static bool TryBlockNativeEditorDragStart(
            EditorWindow window,
            string inputType,
            Vector2 mousePosition)
        {
            if (window == null) return false;
            if (!ShouldBlockNativeEditorDragStart(inputType, window.GetType().FullName ?? window.GetType().Name))
                return false;
            if (!HasSyntheticDragThresholdMet(window, inputType, mousePosition))
                return false;

            ClearRemoteDragControls();
            s_CompositeMouseCaptureView = null;
            s_CompositeMouseCaptureSlot = null;
            ClearSourceDragSelection(window);
            ScheduleNativeDragDropCancelWatchdog();
            s_BlockedNativeProjectDragLogCount++;
            if (s_BlockedNativeProjectDragLogCount <= 10 || s_BlockedNativeProjectDragLogCount % 100 == 0)
                LogVerbose("[NWB-DnD] Blocked native editor DragAndDrop.StartDrag fallback");
            return true;
        }

        private static bool TryForwardRemoteNativeDragAndDropEvent(
            EditorWindow target,
            string inputType,
            Vector2 mousePosition,
            EventModifiers modifiers)
        {
            if (!s_RemoteNativeDragAndDropActive) return false;
            if (target == null || !ShouldForwardNativeDragAndDropEvent(inputType)) return false;
            if (!HasActiveUnityDragAndDropData())
            {
                FinishRemoteNativeDragAndDrop();
                return false;
            }

            Event dragUpdated = new Event
            {
                type = EventType.DragUpdated,
                mousePosition = mousePosition,
                modifiers = modifiers
            };
            target.SendEvent(dragUpdated);

            if (inputType == "mouseup")
            {
                try
                {
                    ScheduleRemoteDragModalCancelWatchdog();
                    Event dragPerform = new Event
                    {
                        type = EventType.DragPerform,
                        mousePosition = mousePosition,
                        modifiers = modifiers
                    };
                    target.SendEvent(dragPerform);
                }
                finally
                {
                    FinishRemoteNativeDragAndDrop();
                }
            }

            return true;
        }

        private static void FinishRemoteNativeDragAndDrop()
        {
            s_RemoteNativeDragAndDropActive = false;
            s_NativeDragCancelWatchdogVersion++;
            ClearSyntheticDragGestureState();
            ClearSourceDragSelection(s_RemoteNativeDragSourceWindow);
            s_RemoteNativeDragSourceWindow = null;
            ClearProjectAssetDragSnapshot();
            DragAndDrop.PrepareStartDrag();
        }

        private static void ClearRemoteDragControls()
        {
            GUIUtility.hotControl = 0;
            DragAndDrop.activeControlID = 0;
        }

        private static void ClearSourceDragSelection(EditorWindow window)
        {
            ClearProjectBrowserListAreaDragSelection(window);
            ClearSceneHierarchyDragSelection(window);
        }

        private static bool HasActiveUnityDragAndDropData()
        {
            bool hasObjects = DragAndDrop.objectReferences != null &&
                DragAndDrop.objectReferences.Length > 0;
            bool hasPaths = DragAndDrop.paths != null &&
                DragAndDrop.paths.Length > 0;
            return hasObjects || hasPaths;
        }

        private static void CaptureProjectAssetSelectionAfterMouseDown(EditorWindow window, string inputType)
        {
            if (window == null) return;
            string windowTypeName = window.GetType().FullName ?? window.GetType().Name;
            if (!ShouldCaptureProjectAssetSelectionForInput(inputType, windowTypeName))
            {
                if (inputType == "mousedown")
                    ClearProjectAssetDragSnapshot();
                return;
            }

            try
            {
                var ids = new System.Collections.Generic.List<int>();
                AddProjectBrowserListAreaDragSelection(window, ids);
                AddProjectBrowserListAreaSelection(window, ids);
                AddProjectBrowserTreeStateSelection(window, "m_AssetTreeState", ids);
                AddProjectBrowserTreeStateSelection(window, "m_FolderTreeState", ids);
                SetProjectAssetDragSnapshot(ids);
            }
            catch (Exception ex)
            {
                ClearProjectAssetDragSnapshot();
                if (s_ProjectSelectionSnapshotLogCount <= 5)
                    CodelyLogger.LogWarning($"[NWB-DnD] Failed to capture Project selection snapshot: {ex.Message}");
            }
        }

        private static void AddProjectBrowserListAreaSelection(
            EditorWindow window,
            System.Collections.Generic.List<int> ids)
        {
            object listArea = GetMemberValue(window, "ListArea") ?? GetMemberValue(window, "m_ListArea");
            if (listArea == null) return;

            var getSelection = listArea.GetType().GetMethod(
                "GetSelection",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (getSelection?.Invoke(listArea, null) is int[] selection)
                AddUniqueIds(ids, selection);
        }

        private static void AddProjectBrowserListAreaDragSelection(
            EditorWindow window,
            System.Collections.Generic.List<int> ids)
        {
            object listArea = GetMemberValue(window, "ListArea") ?? GetMemberValue(window, "m_ListArea");
            object localAssets = GetMemberValue(listArea, "m_LocalAssets");
            object dragSelection = GetMemberValue(localAssets, "m_DragSelection");
            if (dragSelection is IEnumerable enumerable)
                AddUniqueIds(ids, enumerable);
        }

        private static void ClearProjectBrowserListAreaDragSelection(EditorWindow window)
        {
            if (window == null) return;
            object listArea = GetMemberValue(window, "ListArea") ?? GetMemberValue(window, "m_ListArea");
            object localAssets = GetMemberValue(listArea, "m_LocalAssets");
            object dragSelection = GetMemberValue(localAssets, "m_DragSelection");
            ClearListLikeObject(dragSelection);
        }

        private static void AddHierarchyDragGameObjects(
            EditorWindow window,
            System.Collections.Generic.List<UnityEngine.Object> refs)
        {
            object treeView = GetSceneHierarchyTreeView(window);
            object dragSelection = GetMemberValue(treeView, "m_DragSelection");
            object selected = InvokeMember(dragSelection, "Get");
            if (selected is IEnumerable enumerable)
            {
                foreach (object value in enumerable)
                {
                    if (!(value is int id)) continue;
                    UnityEngine.Object obj = InstanceIdExtensions.InstanceIdToObject(id);
                    if (obj is GameObject && !refs.Contains(obj))
                        refs.Add(obj);
                }
            }
        }

        private static void ClearSceneHierarchyDragSelection(EditorWindow window)
        {
            object treeView = GetSceneHierarchyTreeView(window);
            object dragSelection = GetMemberValue(treeView, "m_DragSelection");
            ClearListLikeObject(dragSelection);
        }

        private static object GetSceneHierarchyTreeView(EditorWindow window)
        {
            object sceneHierarchy = GetMemberValue(window, "sceneHierarchy") ?? GetMemberValue(window, "m_SceneHierarchy");
            return GetMemberValue(sceneHierarchy, "treeView") ?? GetMemberValue(sceneHierarchy, "m_TreeView");
        }

        private static void AddProjectBrowserTreeStateSelection(
            EditorWindow window,
            string fieldName,
            System.Collections.Generic.List<int> ids)
        {
            object state = GetMemberValue(window, fieldName);
            object selected = GetMemberValue(state, "selectedIDs") ?? GetMemberValue(state, "m_SelectedIDs");
            if (selected is IEnumerable enumerable)
            {
                foreach (object value in enumerable)
                {
                    if (value is int id && !ids.Contains(id))
                        ids.Add(id);
                }
            }
        }

        private static object GetMemberValue(object target, string name)
        {
            if (target == null) return null;
            var type = target.GetType();
            var flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            var property = type.GetProperty(name, flags);
            if (property != null)
                return property.GetValue(target);
            var field = type.GetField(name, flags);
            return field?.GetValue(target);
        }

        private static object InvokeMember(object target, string name)
        {
            if (target == null) return null;
            var method = target.GetType().GetMethod(
                name,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            return method?.Invoke(target, null);
        }

        private static void AddUniqueIds(System.Collections.Generic.List<int> ids, int[] values)
        {
            if (values == null) return;
            foreach (int id in values)
            {
                if (id != 0 && !ids.Contains(id))
                    ids.Add(id);
            }
        }

        private static void AddUniqueIds(System.Collections.Generic.List<int> ids, IEnumerable values)
        {
            if (values == null) return;
            foreach (object value in values)
            {
                if (value is int id && id != 0 && !ids.Contains(id))
                    ids.Add(id);
            }
        }

        private static bool ClearListLikeObject(object value)
        {
            if (value == null) return false;
            var clear = value.GetType().GetMethod(
                "Clear",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (clear == null) return false;
            clear.Invoke(value, null);
            return true;
        }

        private static void SetProjectAssetDragSnapshot(System.Collections.Generic.List<int> ids)
        {
            var refs = new System.Collections.Generic.List<UnityEngine.Object>();
            var paths = new System.Collections.Generic.List<string>();
            foreach (int id in ids)
            {
                string path = InstanceIdExtensions.AssetPathFromInstanceId(id);
                if (string.IsNullOrEmpty(path)) continue;
                UnityEngine.Object obj = InstanceIdExtensions.InstanceIdToObject(id);
                if (obj == null)
                    obj = AssetDatabase.LoadMainAssetAtPath(path);
                if (obj == null) continue;
                refs.Add(obj);
                paths.Add(path);
            }

            s_ProjectAssetDragObjects = refs.Count > 0 ? refs.ToArray() : null;
            s_ProjectAssetDragPaths = paths.Count > 0 ? paths.ToArray() : null;
            s_ProjectSelectionSnapshotLogCount++;
            if (s_ProjectSelectionSnapshotLogCount <= 10 || s_ProjectSelectionSnapshotLogCount % 100 == 0)
                LogVerbose($"[NWB-DnD] Project selection snapshot: ids={ids.Count} assets={refs.Count}");
        }

        private static void AddCachedProjectAssetDragObjects(
            System.Collections.Generic.List<UnityEngine.Object> refs,
            System.Collections.Generic.List<string> paths)
        {
            if (s_ProjectAssetDragObjects == null || s_ProjectAssetDragPaths == null)
                return;
            int count = Math.Min(s_ProjectAssetDragObjects.Length, s_ProjectAssetDragPaths.Length);
            for (int i = 0; i < count; i++)
            {
                UnityEngine.Object obj = s_ProjectAssetDragObjects[i];
                string path = s_ProjectAssetDragPaths[i];
                if (obj == null || string.IsNullOrEmpty(path)) continue;
                refs.Add(obj);
                paths.Add(path);
            }
        }

        private static void ClearProjectAssetDragSnapshot()
        {
            s_ProjectAssetDragObjects = null;
            s_ProjectAssetDragPaths = null;
        }

        private static void ScheduleNativeDragDropCancelWatchdog()
        {
#if UNITY_EDITOR_WIN
            var hwnds = GetAllUnityHwnds();
            if (hwnds == null || hwnds.Count == 0)
                return;

            int version = ++s_NativeDragCancelWatchdogVersion;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                const int vkEscape = 0x1B;
                for (int i = 0; i < 20; i++)
                {
                    if (version != s_NativeDragCancelWatchdogVersion)
                        return;

                    foreach (IntPtr hwnd in hwnds)
                    {
                        if (hwnd == IntPtr.Zero) continue;
                        PostMessageW(hwnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                        PostMessageW(hwnd, WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
                        PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)vkEscape, IntPtr.Zero);
                        PostMessageW(hwnd, WM_KEYUP, (IntPtr)vkEscape, IntPtr.Zero);
                    }

                    Thread.Sleep(50);
                }
            });
#endif
        }

        private static void ScheduleRemoteDragModalCancelWatchdog()
        {
#if UNITY_EDITOR_WIN
            var baseline = new HashSet<IntPtr>(GetAllUnityHwnds());
            foreach (var hwnd in s_TransparentHwnds)
                baseline.Add(hwnd);
            if (s_MaskWindowHwnd != IntPtr.Zero)
                baseline.Add(s_MaskWindowHwnd);
            if (s_OffscreenTargetContainerHwnd != IntPtr.Zero)
                baseline.Add(s_OffscreenTargetContainerHwnd);

            int version = ++s_NativeDragCancelWatchdogVersion;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                const int maxAttempts = 160; // ~4s at 25ms; enough for modal creation during DragPerform.
                const int sleepMs = 25;
                const uint vkEscape = 0x1B;
                const uint wmClose = 0x0010;

                for (int i = 0; i < maxAttempts; i++)
                {
                    if (version != s_NativeDragCancelWatchdogVersion)
                        return;

                    IntPtr modalHwnd = FindNewRemoteDragBlockingModal(baseline);
                    if (modalHwnd != IntPtr.Zero)
                    {
                        string className = GetHwndClassName(modalHwnd);
                        string title = GetHwndTitle(modalHwnd);
                        PostMessageW(modalHwnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                        PostMessageW(modalHwnd, WM_KEYDOWN, (IntPtr)vkEscape, IntPtr.Zero);
                        PostMessageW(modalHwnd, WM_KEYUP, (IntPtr)vkEscape, IntPtr.Zero);
                        PostMessageW(modalHwnd, wmClose, IntPtr.Zero, IntPtr.Zero);
                        CodelyLogger.Log(
                            $"[NWB-DnD] Auto-canceled blocking modal after remote drag: " +
                            $"hwnd=0x{modalHwnd.ToInt64():X} class={className} title=\"{title}\"");
                        return;
                    }

                    Thread.Sleep(sleepMs);
                }
            });
#endif
        }

#if UNITY_EDITOR_WIN
        private static IntPtr FindNewRemoteDragBlockingModal(HashSet<IntPtr> baseline)
        {
            IntPtr result = IntPtr.Zero;
            uint currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

            IntPtr hwnd = GetWindow(GetDesktopWindow(), GW_CHILD);
            int safety = 0;
            while (hwnd != IntPtr.Zero && safety < 4096)
            {
                if (!baseline.Contains(hwnd) && hwnd != s_MaskWindowHwnd)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == currentPid && IsWindowVisible(hwnd))
                    {
                        string className = GetHwndClassName(hwnd);
                        if (IsRemoteDragBlockingModalClass(hwnd, className))
                        {
                            result = hwnd;
                            break;
                        }
                    }
                }
                hwnd = GetWindow(hwnd, GW_HWNDNEXT);
                safety++;
            }

            return result;
        }

        private static bool IsRemoteDragBlockingModalClass(IntPtr hwnd, string className)
        {
            if (string.IsNullOrEmpty(className) || className == "#32768")
                return false;
            if (className == "#32770")
                return true;

            string title = GetHwndTitle(hwnd);
            if (string.IsNullOrEmpty(title) || title.EndsWith(" - Streaming", StringComparison.Ordinal))
                return false;

            IntPtr owner = GetWindow(hwnd, GW_OWNER);
            bool ownedUnityWindow = owner != IntPtr.Zero &&
                className.IndexOf("Unity", StringComparison.OrdinalIgnoreCase) >= 0;
            bool containerLikeDialog = className.IndexOf("Container", StringComparison.OrdinalIgnoreCase) >= 0 &&
                className.IndexOf("Unity", StringComparison.OrdinalIgnoreCase) >= 0;
            return ownedUnityWindow || containerLikeDialog;
        }

        private static string GetHwndClassName(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(256);
            return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        private static string GetHwndTitle(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(256);
            return GetWindowTextW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        private const uint GW_OWNER = 4;
#endif
    }
}
