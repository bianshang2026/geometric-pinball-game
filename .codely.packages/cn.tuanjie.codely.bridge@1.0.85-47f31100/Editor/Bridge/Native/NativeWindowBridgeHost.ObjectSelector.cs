using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Native
{
    internal static partial class NativeWindowBridgeHost
    {
        private static object s_CachedObjectSelector;
        private static int s_CachedObjectSelectorControlId;
        private static int s_CachedObjectSelectorSelectedInstanceId;
        private static string s_ObjectSelectorSearchFilter = "";
        private static float s_ObjectSelectorPopupWidth;
        private static float s_ObjectSelectorPopupHeight;
        private static bool s_ObjectSelectorInterceptRetryScheduled;

        private static bool s_ObjectSelectorReflectionDone;
        private static Type s_ObjectSelectorType;
        private static PropertyInfo s_ObjectSelectorGetProp;
        private static FieldInfo s_ObjectSelectorModalUndoGroupField;
        private static FieldInfo s_ObjectSelectorDelegateViewField;
        private static FieldInfo s_ObjectSelectorListAreaField;
        private static FieldInfo s_ObjectSelectorTreeSearchField;
        private static FieldInfo s_ObjectSelectorIsShowingAssetsField;
        private static FieldInfo s_ObjectSelectorAllowSceneObjectsField;
        private static FieldInfo s_ObjectSelectorLastSelectedIdField;
        private static FieldInfo s_ObjectSelectorControlIdField;
        private static PropertyInfo s_ObjectSelectorSearchFilterProp;
        private static MethodInfo s_ObjectSelectorFilterSettingsChangedMethod;
        private static MethodInfo s_ObjectSelectorNotifySelectionChangedMethod;
        private static MethodInfo s_ObjectSelectorCancelMethod;
        private static MethodInfo s_ObjectSelectorInitIfNeededMethod;
        private static MethodInfo s_ObjectSelectorSendEventMethod;
        private static FieldInfo s_ObjectSelectorEditedPropertyField;
        private static FieldInfo s_ObjectListAreaLocalAssetsField;
        private static MethodInfo s_ObjectSelectorListAreaItemSelectedCallbackMethod;
        private static object s_CachedObjectSelectorDelegateView;
        private static bool s_ObjectSelectorSessionActive;
        private static bool s_CachedObjectSelectorShowingAssets = true;
        private static bool s_CachedObjectSelectorAllowScene;
        private static string s_CachedObjectSelectorPropertyPath;
        private static long[] s_CachedObjectSelectorTargetInstanceIds;
        private static long s_CachedObjectBeingEditedId;
        private static string[] s_CachedObjectSelectorRequiredTypes;
        private static FieldInfo s_ObjectSelectorObjectBeingEditedField;
        private static FieldInfo s_ObjectSelectorRequiredTypesField;
        private static FieldInfo s_ObjectSelectorOnUpdatedCallbackField;
        private static FieldInfo s_ObjectSelectorOriginalSelectionField;
        private static long s_CachedObjectSelectorOriginalSelectionId;
        private static readonly Dictionary<int, string> s_CachedObjectSelectorInstanceLabels =
            new Dictionary<int, string>();

        private const string kObjectSelectorUpdatedCommand = "ObjectSelectorUpdated";
        private const string kObjectSelectorClosedCommand = "ObjectSelectorClosed";

        private static void EnsureObjectSelectorReflection()
        {
            if (s_ObjectSelectorReflectionDone) return;
            s_ObjectSelectorReflectionDone = true;

            try
            {
                var editorAsm = typeof(EditorWindow).Assembly;
                s_ObjectSelectorType = editorAsm.GetType("UnityEditor.ObjectSelector");
                if (s_ObjectSelectorType == null)
                {
                    CodelyLogger.LogWarning("[NWB-ObjectSelector] ObjectSelector type not found");
                    return;
                }

                s_ObjectSelectorGetProp = s_ObjectSelectorType.GetProperty(
                    "get",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

                s_ObjectSelectorModalUndoGroupField = s_ObjectSelectorType.GetField("m_ModalUndoGroup", inst);
                s_ObjectSelectorDelegateViewField = s_ObjectSelectorType.GetField("m_DelegateView", inst);
                s_ObjectSelectorListAreaField = s_ObjectSelectorType.GetField("m_ListArea", inst);
                s_ObjectSelectorTreeSearchField = s_ObjectSelectorType.GetField("m_ObjectTreeWithSearch", inst);
                s_ObjectSelectorIsShowingAssetsField = s_ObjectSelectorType.GetField("m_IsShowingAssets", inst);
                s_ObjectSelectorAllowSceneObjectsField = s_ObjectSelectorType.GetField("m_AllowSceneObjects", inst);
                s_ObjectSelectorLastSelectedIdField = s_ObjectSelectorType.GetField("m_LastSelectedInstanceId", inst);
                s_ObjectSelectorControlIdField = s_ObjectSelectorType.GetField("objectSelectorID", inst);
                s_ObjectSelectorSearchFilterProp = s_ObjectSelectorType.GetProperty("searchFilter", inst);
                s_ObjectSelectorFilterSettingsChangedMethod = s_ObjectSelectorType.GetMethod(
                    "FilterSettingsChanged", inst);
                s_ObjectSelectorNotifySelectionChangedMethod = s_ObjectSelectorType.GetMethod(
                    "NotifySelectionChanged", inst);
                s_ObjectSelectorCancelMethod = s_ObjectSelectorType.GetMethod("Cancel", inst);
                s_ObjectSelectorInitIfNeededMethod = s_ObjectSelectorType.GetMethod("InitIfNeeded", inst);
                s_ObjectSelectorEditedPropertyField = s_ObjectSelectorType.GetField("m_EditedProperty", inst);
                s_ObjectSelectorSendEventMethod = s_ObjectSelectorType.GetMethod(
                    "SendEvent", inst, null, new[] { typeof(string), typeof(bool) }, null);
                s_ObjectSelectorListAreaItemSelectedCallbackMethod = s_ObjectSelectorType.GetMethod(
                    "ListAreaItemSelectedCallback", inst);
                s_ObjectSelectorObjectBeingEditedField = s_ObjectSelectorType.GetField(
                    "m_ObjectBeingEdited", inst);
                s_ObjectSelectorRequiredTypesField = s_ObjectSelectorType.GetField(
                    "m_RequiredTypes", inst);
                s_ObjectSelectorOnUpdatedCallbackField = s_ObjectSelectorType.GetField(
                    "m_OnObjectSelectorUpdated", inst);
                s_ObjectSelectorOriginalSelectionField = s_ObjectSelectorType.GetField(
                    "m_OriginalSelection", inst);

                Type objectListAreaType = editorAsm.GetType("UnityEditor.ObjectListArea");
                if (objectListAreaType != null)
                {
                    s_ObjectListAreaLocalAssetsField = objectListAreaType.GetField(
                        "m_LocalAssets", BindingFlags.Instance | BindingFlags.NonPublic);
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjectSelector] Reflection init error: {ex.Message}");
            }
        }

        private static object GetLiveObjectSelector()
        {
            EnsureObjectSelectorReflection();
            if (s_ObjectSelectorGetProp == null) return null;
            return s_ObjectSelectorGetProp.GetValue(null);
        }

        private static bool IsObjectSelectorOpen(object selector)
        {
            if (selector == null || s_ObjectSelectorModalUndoGroupField == null)
                return false;
            return (int)s_ObjectSelectorModalUndoGroupField.GetValue(selector) >= 0;
        }

        private static bool IsObjectSelectorFromOffscreenTarget(object selector)
        {
            if (selector == null || s_OffscreenTarget == null || s_ObjectSelectorDelegateViewField == null)
                return false;

            object delegateView = s_ObjectSelectorDelegateViewField.GetValue(selector);
            if (delegateView == null) return false;

            FieldInfo parentField = typeof(EditorWindow).GetField(
                "m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
            object targetView = parentField?.GetValue(s_OffscreenTarget);
            return targetView != null && ReferenceEquals(delegateView, targetView);
        }

        private static bool TryImmediateObjectSelectorIntercept(float ppp)
        {
            if (s_FrontendPopupSent) return true;

            try
            {
                object selector = GetLiveObjectSelector();
                if (!IsObjectSelectorOpen(selector) || !IsObjectSelectorFromOffscreenTarget(selector))
                    return false;

                if (IsObjectSelectorUsingTreeView(selector))
                {
                    LogVerbose("[NWB-ObjectSelector] Tree-view selector not supported yet");
                    TryHideObjectSelectorNativeWindow(selector);
                    return false;
                }

                Rect targetScreenPos = GetOffscreenTargetScreenRect();
                Rect selectorPos = GetEditorWindowScreenRect(selector as EditorWindow);
                if (selectorPos.width <= 0 || selectorPos.height <= 0)
                    return false;

                float relX = (selectorPos.x - targetScreenPos.x) * ppp;
                float relY = (selectorPos.y - targetScreenPos.y) * ppp;
                float popW = selectorPos.width * ppp;
                float popH = selectorPos.height * ppp;

                s_CachedObjectSelector = selector;
                if (s_ObjectSelectorDelegateViewField != null)
                    s_CachedObjectSelectorDelegateView = s_ObjectSelectorDelegateViewField.GetValue(selector);
                if (s_ObjectSelectorControlIdField != null)
                    s_CachedObjectSelectorControlId = (int)s_ObjectSelectorControlIdField.GetValue(selector);
                if (s_ObjectSelectorLastSelectedIdField != null)
                    s_CachedObjectSelectorSelectedInstanceId =
                        (int)s_ObjectSelectorLastSelectedIdField.GetValue(selector);

                if (string.IsNullOrEmpty(s_ObjectSelectorSearchFilter) &&
                    s_ObjectSelectorSearchFilterProp != null)
                {
                    s_ObjectSelectorSearchFilter =
                        s_ObjectSelectorSearchFilterProp.GetValue(selector) as string ?? "";
                }

                s_ObjectSelectorPopupWidth = popW;
                s_ObjectSelectorPopupHeight = popH;
                s_LastFrontendPopupX = relX;
                s_LastFrontendPopupY = relY;
                CacheObjectSelectorSessionState(selector);

                bool sent = TrySendObjectSelectorPopup(relX, relY, popW, popH);
                if (sent)
                {
                    // Keep the native ObjectSelector session alive (AuxWindow is not
                    // composited into the offscreen stream). Moving/hiding it can end
                    // the modal session before popup_select arrives.
                    LogVerbose("[NWB-ObjectSelector] Sent object picker to frontend");
                }
                else if (!s_ObjectSelectorInterceptRetryScheduled)
                {
                    LogVerbose("[NWB-ObjectSelector] Extraction failed; canceling object picker session");
                    CancelObjectSelectorSession();
                    ClearObjectSelectorPopupState();
                }

                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjectSelector] Intercept error: {ex.Message}");
                return false;
            }
        }

        private static bool IsObjectSelectorUsingTreeView(object selector)
        {
            if (selector == null || s_ObjectSelectorTreeSearchField == null)
                return false;

            object tree = s_ObjectSelectorTreeSearchField.GetValue(selector);
            if (tree == null) return false;

            MethodInfo isInitialized = tree.GetType().GetMethod(
                "IsInitialized",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return isInitialized != null && (bool)isInitialized.Invoke(tree, null);
        }

        private static Rect GetOffscreenTargetScreenRect()
        {
            Rect targetScreenPos = Rect.zero;
            if (s_OffscreenTarget == null) return targetScreenPos;

            FieldInfo parentField = typeof(EditorWindow).GetField(
                "m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
            object targetView = parentField?.GetValue(s_OffscreenTarget);
            if (targetView == null) return targetScreenPos;

            Type viewType = targetView.GetType();
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
            return targetScreenPos;
        }

        private static Rect GetEditorWindowScreenRect(EditorWindow window)
        {
            if (window == null) return Rect.zero;

            FieldInfo parentField = typeof(EditorWindow).GetField(
                "m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
            object parentView = parentField?.GetValue(window);
            if (parentView == null)
                return window.position;

            PropertyInfo windowProp = parentView.GetType().GetProperty(
                "window", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object containerWindow = windowProp?.GetValue(parentView);
            if (containerWindow == null)
                return window.position;

            PropertyInfo positionProp = containerWindow.GetType().GetProperty(
                "position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (positionProp == null)
                return window.position;
            return (Rect)positionProp.GetValue(containerWindow);
        }

        private static void TryHideObjectSelectorNativeWindow(object selector)
        {
            var window = selector as EditorWindow;
            if (window == null) return;

            try
            {
                // Keep the selector session alive but move it off-screen. Minimize can
                // leave list/search state stale on some platforms.
                Rect pos = window.position;
                pos.x = -20000f;
                pos.y = -20000f;
                window.position = pos;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjectSelector] Hide native window failed: {ex.Message}");
            }
        }

        private static void EnsureObjectSelectorListReady(object selector)
        {
            s_ObjectSelectorInitIfNeededMethod?.Invoke(selector, null);
        }

        private static void RestoreObjectSelectorSessionFields(object selector)
        {
            if (selector == null)
                return;

            if (s_CachedObjectBeingEditedId != 0 && s_ObjectSelectorObjectBeingEditedField != null)
            {
                UnityEngine.Object objectBeingEdited =
                    InstanceIdExtensions.InstanceIdToObject(s_CachedObjectBeingEditedId);
                if (objectBeingEdited != null)
                    s_ObjectSelectorObjectBeingEditedField.SetValue(selector, objectBeingEdited);
            }

            if (s_CachedObjectSelectorRequiredTypes != null &&
                s_ObjectSelectorRequiredTypesField != null)
            {
                s_ObjectSelectorRequiredTypesField.SetValue(
                    selector,
                    s_CachedObjectSelectorRequiredTypes);
            }
        }

        private static bool ApplyObjectSelectorTabAndFilter(
            object selector,
            bool showAssets,
            string searchFilter = null)
        {
            if (selector == null || s_ObjectSelectorIsShowingAssetsField == null)
                return false;

            EnsureObjectSelectorReflection();
            EnsureObjectSelectorListReady(selector);
            RestoreObjectSelectorSessionFields(selector);

            if (searchFilter != null)
            {
                s_ObjectSelectorSearchFilter = searchFilter;
                s_ObjectSelectorSearchFilterProp?.SetValue(selector, searchFilter);
            }
            else if (s_ObjectSelectorSearchFilterProp != null)
            {
                s_ObjectSelectorSearchFilter =
                    s_ObjectSelectorSearchFilterProp.GetValue(selector) as string ??
                    s_ObjectSelectorSearchFilter ??
                    "";
            }

            s_ObjectSelectorIsShowingAssetsField.SetValue(selector, showAssets);
            s_CachedObjectSelectorShowingAssets = showAssets;
            s_ObjectSelectorFilterSettingsChangedMethod?.Invoke(selector, null);
            return true;
        }

        private static void SendObjectSelectorCommand(object selector, string commandName)
        {
            if (selector == null || string.IsNullOrEmpty(commandName))
                return;

            try
            {
                if (s_ObjectSelectorSendEventMethod != null)
                {
                    s_ObjectSelectorSendEventMethod.Invoke(selector, new object[] { commandName, false });
                    return;
                }

                object delegateView = s_CachedObjectSelectorDelegateView;
                if (delegateView == null && s_ObjectSelectorDelegateViewField != null)
                    delegateView = s_ObjectSelectorDelegateViewField.GetValue(selector);

                Event evt = EditorGUIUtility.CommandEvent(commandName);
                if (delegateView != null)
                {
                    MethodInfo sendEvent = delegateView.GetType().GetMethod(
                        "SendEvent",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(Event) },
                        null);
                    sendEvent?.Invoke(delegateView, new object[] { evt });
                }

                if (s_OffscreenTarget != null)
                    s_OffscreenTarget.SendEvent(evt);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjectSelector] Send command '{commandName}' failed: {ex.Message}");
            }
        }

        private static void CacheObjectSelectorSessionState(object selector)
        {
            s_ObjectSelectorSessionActive = true;
            s_CachedObjectSelectorPropertyPath = null;
            s_CachedObjectSelectorTargetInstanceIds = null;
            s_CachedObjectBeingEditedId = 0;
            s_CachedObjectSelectorOriginalSelectionId = 0;

            if (selector != null)
            {
                if (s_ObjectSelectorAllowSceneObjectsField != null)
                    s_CachedObjectSelectorAllowScene =
                        (bool)s_ObjectSelectorAllowSceneObjectsField.GetValue(selector);
                if (s_ObjectSelectorIsShowingAssetsField != null)
                    s_CachedObjectSelectorShowingAssets =
                        (bool)s_ObjectSelectorIsShowingAssetsField.GetValue(selector);

                if (s_ObjectSelectorObjectBeingEditedField != null)
                {
                    var objectBeingEdited =
                        s_ObjectSelectorObjectBeingEditedField.GetValue(selector) as UnityEngine.Object;
                    s_CachedObjectBeingEditedId =
                        objectBeingEdited != null ? objectBeingEdited.GetStableInstanceId() : 0;
                }

                if (s_ObjectSelectorOriginalSelectionField != null)
                {
                    var originalSelection =
                        s_ObjectSelectorOriginalSelectionField.GetValue(selector) as UnityEngine.Object;
                    s_CachedObjectSelectorOriginalSelectionId =
                        originalSelection != null ? originalSelection.GetStableInstanceId() : 0;
                }

                if (s_ObjectSelectorRequiredTypesField != null)
                {
                    s_CachedObjectSelectorRequiredTypes =
                        s_ObjectSelectorRequiredTypesField.GetValue(selector) as string[];
                }

                if (s_ObjectSelectorEditedPropertyField != null)
                {
                    var editedProperty =
                        s_ObjectSelectorEditedPropertyField.GetValue(selector) as SerializedProperty;
                    if (editedProperty != null && !string.IsNullOrEmpty(editedProperty.propertyPath))
                    {
                        s_CachedObjectSelectorPropertyPath = editedProperty.propertyPath;
                        UnityEngine.Object[] targets = editedProperty.serializedObject.targetObjects;
                        if (targets != null && targets.Length > 0)
                        {
                            s_CachedObjectSelectorTargetInstanceIds = new long[targets.Length];
                            for (int i = 0; i < targets.Length; i++)
                                s_CachedObjectSelectorTargetInstanceIds[i] = targets[i].GetStableInstanceId();
                        }
                    }
                }
            }

            EnsureObjectSelectorApplyTargetsFromInspector();

            if ((s_CachedObjectSelectorTargetInstanceIds == null ||
                 s_CachedObjectSelectorTargetInstanceIds.Length == 0) &&
                s_CachedObjectBeingEditedId != 0)
            {
                s_CachedObjectSelectorTargetInstanceIds = new[] { s_CachedObjectBeingEditedId };
            }

            LogVerbose(
                $"[NWB-ObjectSelector] Cached session targets={FormatTargetIds()} edited={s_CachedObjectBeingEditedId} original={s_CachedObjectSelectorOriginalSelectionId} path={s_CachedObjectSelectorPropertyPath ?? "null"} types={FormatRequiredTypes()}");
        }

        private static string FormatTargetIds()
        {
            if (s_CachedObjectSelectorTargetInstanceIds == null ||
                s_CachedObjectSelectorTargetInstanceIds.Length == 0)
            {
                return "[]";
            }

            return "[" + string.Join(",", s_CachedObjectSelectorTargetInstanceIds) + "]";
        }

        private static string FormatRequiredTypes()
        {
            if (s_CachedObjectSelectorRequiredTypes == null ||
                s_CachedObjectSelectorRequiredTypes.Length == 0)
            {
                return "[]";
            }

            return "[" + string.Join(",", s_CachedObjectSelectorRequiredTypes) + "]";
        }

        private static long[] GetOffscreenInspectorTargetInstanceIds()
        {
            var ids = new List<long>();

            if (s_OffscreenTarget != null)
            {
                try
                {
                    FieldInfo trackerField = s_OffscreenTarget.GetType().GetField(
                        "m_Tracker",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    object tracker = trackerField?.GetValue(s_OffscreenTarget);
                    AppendEditorTargetInstanceIds(ids, tracker);
                }
                catch (Exception ex)
                {
                    LogVerbose($"[NWB-ObjectSelector] Inspector tracker read failed: {ex.Message}");
                }
            }

            if (ids.Count == 0)
            {
                try
                {
                    AppendEditorTargetInstanceIds(ids, ActiveEditorTracker.sharedTracker);
                }
                catch (Exception ex)
                {
                    LogVerbose($"[NWB-ObjectSelector] sharedTracker read failed: {ex.Message}");
                }
            }

            UnityEngine.Object activeObject = Selection.activeObject;
            if (activeObject != null)
                AppendUniqueTargetId(ids, activeObject.GetStableInstanceId());

            return ids.ToArray();
        }

        private static void AppendEditorTargetInstanceIds(List<long> ids, object tracker)
        {
            if (tracker == null)
                return;

            PropertyInfo activeEditorsProp = tracker.GetType().GetProperty(
                "activeEditors",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var activeEditors = activeEditorsProp?.GetValue(tracker) as UnityEditor.Editor[];
            if (activeEditors == null)
                return;

            foreach (UnityEditor.Editor editor in activeEditors)
            {
                if (editor == null)
                    continue;

                if (editor.target != null)
                    AppendUniqueTargetId(ids, editor.target.GetStableInstanceId());

                UnityEngine.Object[] targets = editor.targets;
                if (targets == null)
                    continue;

                foreach (UnityEngine.Object target in targets)
                {
                    if (target != null)
                        AppendUniqueTargetId(ids, target.GetStableInstanceId());
                }
            }
        }

        private static void AppendUniqueTargetId(List<long> ids, long instanceId)
        {
            if (instanceId == 0 || ids.Contains(instanceId))
                return;

            ids.Add(instanceId);
        }

        private static void EnsureObjectSelectorApplyTargetsFromInspector()
        {
            long[] inspectorIds = GetOffscreenInspectorTargetInstanceIds();
            if (inspectorIds.Length == 0)
                return;

            if (s_CachedObjectSelectorTargetInstanceIds == null ||
                s_CachedObjectSelectorTargetInstanceIds.Length == 0)
            {
                s_CachedObjectSelectorTargetInstanceIds = inspectorIds;
            }
            else
            {
                var merged = new List<long>(s_CachedObjectSelectorTargetInstanceIds);
                foreach (long id in inspectorIds)
                    AppendUniqueTargetId(merged, id);
                s_CachedObjectSelectorTargetInstanceIds = merged.ToArray();
            }

            if (s_CachedObjectBeingEditedId == 0)
                s_CachedObjectBeingEditedId = inspectorIds[0];
        }

        private static UnityEngine.Object ResolveBuiltinMeshByLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
                return null;

            string resourceName = null;
            switch (label)
            {
                case "Plane": resourceName = "New-Plane.fbx"; break;
                case "Cube": resourceName = "Cube.fbx"; break;
                case "Capsule": resourceName = "New-Capsule.fbx"; break;
                case "Cylinder": resourceName = "New-Cylinder.fbx"; break;
                case "Sphere": resourceName = "New-Sphere.fbx"; break;
                case "Quad": resourceName = "Quad.fbx"; break;
            }

            if (resourceName == null)
                return null;

            return Resources.GetBuiltinResource(typeof(Mesh), resourceName);
        }

        private static UnityEngine.Object ResolveObjectSelectorInstance(int instanceId)
        {
            if (instanceId == 0)
                return null;

            UnityEngine.Object obj = InstanceIdExtensions.ObjectFromInstanceId(instanceId);
            if (obj != null)
                return obj;

            obj = InstanceIdExtensions.InstanceIdToObject(instanceId);
            if (obj != null)
                return obj;

            if (s_CachedObjectSelectorInstanceLabels.TryGetValue(instanceId, out string label))
                return ResolveBuiltinMeshByLabel(label);

            return null;
        }

        private static Type ResolveObjectSelectorRequiredType()
        {
            if (s_CachedObjectSelectorRequiredTypes != null)
            {
                foreach (string typeName in s_CachedObjectSelectorRequiredTypes)
                {
                    if (string.IsNullOrEmpty(typeName))
                        continue;

                    Type type = Type.GetType("UnityEngine." + typeName + ", UnityEngine.CoreModule");
                    if (type == null)
                        type = Type.GetType("UnityEngine." + typeName + ", UnityEngine");
                    if (type != null)
                        return type;
                }
            }

            return typeof(Mesh);
        }

        private static bool TryAssignObjectSelectorProperty(
            SerializedObject serializedObject,
            SerializedProperty property,
            UnityEngine.Object selectedObject,
            Type requiredType)
        {
            if (!SerializedPropertyAcceptsObject(property, requiredType))
                return false;

            serializedObject.Update();
            property.objectReferenceValue = selectedObject;
            serializedObject.ApplyModifiedProperties();
            return true;
        }

        private static bool TryApplyMatchingMeshPropertyOnTarget(
            SerializedObject serializedObject,
            UnityEngine.Object selectedObject,
            Type requiredType,
            bool restrictToOriginalSelection)
        {
            string[] preferredPaths = { "m_Mesh", "m_sharedMesh", "sharedMesh", "mesh" };
            string originalMatchPath = null;
            string firstMatchPath = null;

            foreach (string preferredPath in preferredPaths)
            {
                SerializedProperty preferredProperty = serializedObject.FindProperty(preferredPath);
                if (!SerializedPropertyAcceptsObject(preferredProperty, requiredType))
                    continue;

                if (firstMatchPath == null)
                    firstMatchPath = preferredPath;
                if (restrictToOriginalSelection &&
                    s_CachedObjectSelectorOriginalSelectionId != 0 &&
                    preferredProperty.objectReferenceValue != null &&
                    preferredProperty.objectReferenceValue.GetStableInstanceId() ==
                    s_CachedObjectSelectorOriginalSelectionId)
                {
                    originalMatchPath = preferredPath;
                }
            }

            if (originalMatchPath == null)
            {
                SerializedProperty iterator = serializedObject.GetIterator();
                if (iterator.NextVisible(true))
                {
                    do
                    {
                        if (!SerializedPropertyAcceptsObject(iterator, requiredType))
                            continue;

                        string propertyPath = iterator.propertyPath;
                        if (firstMatchPath == null)
                            firstMatchPath = propertyPath;
                        if (restrictToOriginalSelection &&
                            s_CachedObjectSelectorOriginalSelectionId != 0 &&
                            iterator.objectReferenceValue != null &&
                            iterator.objectReferenceValue.GetStableInstanceId() ==
                            s_CachedObjectSelectorOriginalSelectionId)
                        {
                            originalMatchPath = propertyPath;
                            break;
                        }
                    }
                    while (iterator.NextVisible(false));
                }
            }

            string chosenPath = originalMatchPath ?? firstMatchPath;
            if (string.IsNullOrEmpty(chosenPath))
                return false;

            SerializedProperty chosenProperty = serializedObject.FindProperty(chosenPath);
            return TryAssignObjectSelectorProperty(
                serializedObject, chosenProperty, selectedObject, requiredType);
        }

        private static bool SerializedPropertyAcceptsObject(SerializedProperty property, Type requiredType)
        {
            if (property == null || property.propertyType != SerializedPropertyType.ObjectReference)
                return false;

            if (requiredType == null || requiredType == typeof(UnityEngine.Object))
                return true;

            string typeName = property.type;
            if (string.IsNullOrEmpty(typeName))
                return true;

            if (typeName.IndexOf(requiredType.Name, StringComparison.Ordinal) >= 0)
                return true;

            return requiredType == typeof(Mesh) &&
                   typeName.IndexOf("Mesh", StringComparison.Ordinal) >= 0;
        }

        private static IEnumerable<long> OrderTargetIdsForMeshApply(long[] targetIds)
        {
            var seen = new HashSet<long>();
            if (targetIds == null)
                yield break;

            foreach (long targetId in targetIds)
            {
                UnityEngine.Object obj = InstanceIdExtensions.ObjectFromInstanceId(targetId);
                if (obj == null)
                    obj = InstanceIdExtensions.InstanceIdToObject(targetId);
                if (obj is MeshFilter || obj is MeshCollider)
                {
                    if (seen.Add(targetId))
                        yield return targetId;
                }
            }

            if (s_CachedObjectBeingEditedId != 0 && seen.Add(s_CachedObjectBeingEditedId))
                yield return s_CachedObjectBeingEditedId;

            foreach (long targetId in targetIds)
            {
                if (seen.Add(targetId))
                    yield return targetId;
            }
        }

        private static IEnumerable<UnityEngine.Object> EnumerateObjectSelectorApplyTargets(
            UnityEngine.Object target,
            Type requiredType)
        {
            if (target == null)
                yield break;

            yield return target;

            if (target is GameObject gameObject)
            {
                if (requiredType == typeof(Mesh) || requiredType == typeof(UnityEngine.Object))
                {
                    MeshFilter meshFilter = gameObject.GetComponent<MeshFilter>();
                    if (meshFilter != null)
                        yield return meshFilter;

                    MeshCollider meshCollider = gameObject.GetComponent<MeshCollider>();
                    if (meshCollider != null)
                        yield return meshCollider;
                }

                if (typeof(Component).IsAssignableFrom(requiredType) &&
                    requiredType != typeof(Mesh))
                {
                    Component component = gameObject.GetComponent(requiredType);
                    if (component != null)
                        yield return component;
                }
            }
        }

        private static bool TryApplyObjectSelectorPropertyScan(UnityEngine.Object selectedObject)
        {
            if (selectedObject == null)
                return false;

            long[] targetIds = s_CachedObjectSelectorTargetInstanceIds;
            if ((targetIds == null || targetIds.Length == 0) && s_CachedObjectBeingEditedId != 0)
                targetIds = new[] { s_CachedObjectBeingEditedId };
            if (targetIds == null || targetIds.Length == 0)
                return false;

            Type requiredType = ResolveObjectSelectorRequiredType();
            bool applied = false;
            bool restrictToOriginalSelection = s_CachedObjectSelectorOriginalSelectionId != 0;

            foreach (long targetId in OrderTargetIdsForMeshApply(targetIds))
            {
                UnityEngine.Object rootTarget = InstanceIdExtensions.ObjectFromInstanceId(targetId);
                if (rootTarget == null)
                    rootTarget = InstanceIdExtensions.InstanceIdToObject(targetId);
                if (rootTarget == null)
                    continue;

                foreach (UnityEngine.Object target in EnumerateObjectSelectorApplyTargets(
                             rootTarget, requiredType))
                {
                    SerializedObject serializedObject = new SerializedObject(target);

                    if (!string.IsNullOrEmpty(s_CachedObjectSelectorPropertyPath))
                    {
                        SerializedProperty cachedProperty =
                            serializedObject.FindProperty(s_CachedObjectSelectorPropertyPath);
                        if (cachedProperty != null &&
                            TryAssignObjectSelectorProperty(
                                serializedObject, cachedProperty, selectedObject, requiredType))
                        {
                            applied = true;
                            break;
                        }
                    }

                    if (TryApplyMatchingMeshPropertyOnTarget(
                            serializedObject,
                            selectedObject,
                            requiredType,
                            restrictToOriginalSelection))
                    {
                        applied = true;
                        break;
                    }
                }

                if (applied)
                    break;
            }

            return applied;
        }

        private static bool TryInvokeObjectSelectorUpdatedCallback(UnityEngine.Object selectedObject)
        {
            object selector = s_CachedObjectSelector ?? GetLiveObjectSelector();
            if (selector == null || s_ObjectSelectorOnUpdatedCallbackField == null)
                return false;

            var onUpdated = s_ObjectSelectorOnUpdatedCallbackField.GetValue(selector) as Delegate;
            if (onUpdated == null)
                return false;

            onUpdated.DynamicInvoke(selectedObject);
            return true;
        }

        private static void PrimeObjectSelectorSingletonForCommand(int instanceId)
        {
            object selector = GetLiveObjectSelector();
            if (selector == null)
                return;

            if (s_ObjectSelectorLastSelectedIdField != null)
                s_ObjectSelectorLastSelectedIdField.SetValue(selector, instanceId);
            if (s_ObjectSelectorControlIdField != null && s_CachedObjectSelectorControlId != 0)
                s_ObjectSelectorControlIdField.SetValue(selector, s_CachedObjectSelectorControlId);
        }

        private static void SendInspectorObjectSelectorCommand(string commandName)
        {
            if (s_OffscreenTarget == null || s_CachedObjectSelectorControlId == 0)
                return;

            GUIUtility.keyboardControl = s_CachedObjectSelectorControlId;
            s_OffscreenTarget.SendEvent(EditorGUIUtility.CommandEvent(commandName));
        }

        private static bool TrySendObjectSelectorPopup(
            float relX,
            float relY,
            float popW,
            float popH,
            bool allowEmptyItems = false,
            bool preservePopupId = false)
        {
            EnsureObjectSelectorReflection();
            object selector = s_CachedObjectSelector ?? GetLiveObjectSelector();
            bool selectorOpen = selector != null && IsObjectSelectorOpen(selector);
            if (selector == null || (!selectorOpen && !s_ObjectSelectorSessionActive))
            {
                CodelyLogger.LogWarning("[NWB-ObjectSelector] No ObjectSelector session to send");
                return false;
            }

            try
            {
                EnsureObjectSelectorListReady(selector);
                if (selectorOpen)
                    CacheObjectSelectorSessionState(selector);
                else
                    RestoreObjectSelectorSessionFields(selector);

                var items = CollectObjectSelectorVisibleItems(selector);
                if (items.Count == 0 && !allowEmptyItems)
                {
                    if (!s_ObjectSelectorInterceptRetryScheduled)
                    {
                        s_ObjectSelectorInterceptRetryScheduled = true;
                        EditorApplication.delayCall += () =>
                        {
                            s_ObjectSelectorInterceptRetryScheduled = false;
                            if (s_FrontendPopupSent)
                                return;
                            object retrySelector = s_CachedObjectSelector ?? GetLiveObjectSelector();
                            if (retrySelector == null ||
                                (!IsObjectSelectorOpen(retrySelector) &&
                                 !s_ObjectSelectorSessionActive))
                                return;
                            if (IsObjectSelectorOpen(retrySelector))
                                TryImmediateObjectSelectorIntercept(EditorGUIUtility.pixelsPerPoint);
                            else
                                TrySendObjectSelectorPopup(
                                    s_LastFrontendPopupX,
                                    s_LastFrontendPopupY,
                                    s_ObjectSelectorPopupWidth,
                                    s_ObjectSelectorPopupHeight,
                                    allowEmptyItems: true,
                                    preservePopupId: true);
                        };
                    }
                    LogVerbose("[NWB-ObjectSelector] No visible picker items yet; retry scheduled");
                    return false;
                }

                bool allowScene = s_CachedObjectSelectorAllowScene;
                bool showingAssets = s_CachedObjectSelectorShowingAssets;
                if (selectorOpen)
                {
                    if (s_ObjectSelectorAllowSceneObjectsField != null)
                        allowScene = (bool)s_ObjectSelectorAllowSceneObjectsField.GetValue(selector);
                    if (s_ObjectSelectorIsShowingAssetsField != null)
                        showingAssets = (bool)s_ObjectSelectorIsShowingAssetsField.GetValue(selector);
                }

                int selectedId = s_CachedObjectSelectorSelectedInstanceId;
                if (selectorOpen && s_ObjectSelectorLastSelectedIdField != null)
                    selectedId = (int)s_ObjectSelectorLastSelectedIdField.GetValue(selector);

                var pathList = new List<string>();
                int itemIndex = 0;

                if (!preservePopupId || string.IsNullOrEmpty(s_CurrentFrontendPopupId))
                {
                    s_FrontendPopupIdCounter++;
                    s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;
                }
                s_PopupSourceType = "object_selector";

                var sb = new StringBuilder(65536);
                sb.Append("{\"type\":\"popup_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"sourceType\":\"object_selector\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                sb.Append(",\"width\":");
                sb.Append(Mathf.RoundToInt(Mathf.Max(popW, 280f)));
                sb.Append(",\"height\":");
                sb.Append(Mathf.RoundToInt(Mathf.Max(popH, 360f)));
                sb.Append(",\"searchText\":\"");
                sb.Append(EscapeJsonString(s_ObjectSelectorSearchFilter ?? ""));
                sb.Append('"');

                if (allowScene)
                {
                    sb.Append(",\"tabs\":[");
                    sb.Append("{\"id\":\"assets\",\"label\":\"Assets\",\"active\":");
                    sb.Append(showingAssets ? "true" : "false");
                    sb.Append("},{\"id\":\"scene\",\"label\":\"Scene\",\"active\":");
                    sb.Append(showingAssets ? "false" : "true");
                    sb.Append("}]");
                }

                sb.Append(",\"items\":[");
                bool needComma = false;
                foreach (var entry in items)
                {
                    if (needComma) sb.Append(',');
                    needComma = true;
                    sb.Append("{\"index\":");
                    sb.Append(itemIndex);
                    sb.Append(",\"path\":\"");
                    sb.Append(entry.instanceId);
                    sb.Append("\",\"label\":\"");
                    sb.Append(EscapeJsonString(entry.label));
                    sb.Append("\",\"enabled\":true,\"checked\":");
                    sb.Append(entry.instanceId == selectedId ? "true" : "false");
                    sb.Append(",\"separator\":false}");
                    pathList.Add(entry.instanceId.ToString());
                    itemIndex++;
                }
                sb.Append("]}");

                s_CachedPopupItemPaths = pathList.ToArray();
                s_CachedObjectSelectorInstanceLabels.Clear();
                foreach (var entry in items)
                    s_CachedObjectSelectorInstanceLabels[entry.instanceId] = entry.label;
                string payload = sb.ToString();
                int payloadBytes = Encoding.UTF8.GetByteCount(payload);

                bool sent;
                if (payloadBytes > kMaxDataChannelPayloadBytes)
                {
                    sent = SendPopupPayloadChunked(s_CurrentFrontendPopupId, payload, payloadBytes);
                    if (sent)
                        LogVerbose($"[NWB-ObjectSelector] Sent chunked popup: bytes={payloadBytes} items={items.Count}");
                }
                else
                {
                    sent = SendDataChannelMessage(payload);
                }

                s_FrontendPopupSent = sent;
                if (sent)
                {
                    CancelNativeDelayedPopup();
                    LogVerbose($"[NWB-ObjectSelector] Sent popup: id={s_CurrentFrontendPopupId} items={items.Count}");
                }
                else
                {
                    CodelyLogger.LogWarning($"[NWB-ObjectSelector] Failed to send popup: id={s_CurrentFrontendPopupId}");
                }

                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjectSelector] Send error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private struct ObjectSelectorVisibleItem
        {
            public readonly string label;
            public readonly int instanceId;

            public ObjectSelectorVisibleItem(string label, int instanceId)
            {
                this.label = label;
                this.instanceId = instanceId;
            }
        }

        private static List<ObjectSelectorVisibleItem> CollectObjectSelectorVisibleItems(object selector)
        {
            var results = new List<ObjectSelectorVisibleItem>();
            if (selector == null || s_ObjectSelectorListAreaField == null ||
                s_ObjectListAreaLocalAssetsField == null)
            {
                return results;
            }

            object listArea = s_ObjectSelectorListAreaField.GetValue(selector);
            if (listArea == null) return results;

            object localGroup = s_ObjectListAreaLocalAssetsField.GetValue(listArea);
            if (localGroup == null) return results;

            MethodInfo getVisibleItems = localGroup.GetType().GetMethod(
                "GetVisibleNameAndInstanceIDs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (getVisibleItems == null) return results;

            var visible = getVisibleItems.Invoke(localGroup, null) as IList;
            if (visible == null) return results;

            foreach (object pairObj in visible)
            {
                if (pairObj == null) continue;
                Type pairType = pairObj.GetType();
                PropertyInfo keyProp = pairType.GetProperty("Key");
                PropertyInfo valueProp = pairType.GetProperty("Value");
                if (keyProp == null || valueProp == null) continue;

                string label = keyProp.GetValue(pairObj) as string ?? "";
                int instanceId = (int)valueProp.GetValue(pairObj);
                if (string.IsNullOrEmpty(label) && instanceId == 0)
                    label = "None";
                if (string.IsNullOrEmpty(label))
                    continue;

                results.Add(new ObjectSelectorVisibleItem(label, instanceId));
            }

            return results;
        }

        private static bool TryHandleObjectSelectorPopupSelect(string path)
        {
            if (string.IsNullOrEmpty(path) || !int.TryParse(path, out int instanceId))
                return false;

            object selector = s_CachedObjectSelector ?? GetLiveObjectSelector();
            bool selectorOpen = selector != null && IsObjectSelectorOpen(selector);
            if (!s_ObjectSelectorSessionActive && !selectorOpen)
            {
                CodelyLogger.LogWarning("[NWB-ObjectSelector] Select failed: ObjectSelector session inactive");
                return false;
            }

            EnsureObjectSelectorReflection();
            try
            {
                EnsureObjectSelectorApplyTargetsFromInspector();

                UnityEngine.Object selectedObject = ResolveObjectSelectorInstance(instanceId);
                if (selectedObject == null && instanceId != 0)
                {
                    CodelyLogger.LogWarning(
                        $"[NWB-ObjectSelector] Could not resolve instanceId={instanceId}");
                }

                bool appliedToProperty = TryApplyObjectSelectorPropertyScan(selectedObject);
                if (!appliedToProperty)
                    appliedToProperty = TryInvokeObjectSelectorUpdatedCallback(selectedObject);

                PrimeObjectSelectorSingletonForCommand(instanceId);

                if (selectorOpen)
                {
                    EnsureObjectSelectorListReady(selector);

                    if (s_ObjectSelectorLastSelectedIdField != null)
                        s_ObjectSelectorLastSelectedIdField.SetValue(selector, instanceId);

                    if (s_CachedObjectSelectorControlId != 0)
                        GUIUtility.keyboardControl = s_CachedObjectSelectorControlId;
                    if (s_ObjectSelectorControlIdField != null && s_CachedObjectSelectorControlId != 0)
                        s_ObjectSelectorControlIdField.SetValue(selector, s_CachedObjectSelectorControlId);

                    TryUpdateObjectSelectorListSelection(selector, instanceId);

                    if (s_ObjectSelectorListAreaItemSelectedCallbackMethod != null)
                        s_ObjectSelectorListAreaItemSelectedCallbackMethod.Invoke(selector, new object[] { false });
                    else if (s_ObjectSelectorNotifySelectionChangedMethod != null)
                        s_ObjectSelectorNotifySelectionChangedMethod.Invoke(selector, new object[] { false });
                    else
                        SendObjectSelectorCommand(selector, kObjectSelectorUpdatedCommand);

                    SendInspectorObjectSelectorCommand(kObjectSelectorUpdatedCommand);

                    if (selector is EditorWindow window)
                        window.Close();
                }
                else
                {
                    SendInspectorObjectSelectorCommand(kObjectSelectorUpdatedCommand);
                    SendInspectorObjectSelectorCommand(kObjectSelectorClosedCommand);
                    EditorApplication.delayCall += () =>
                    {
                        PrimeObjectSelectorSingletonForCommand(instanceId);
                        SendInspectorObjectSelectorCommand(kObjectSelectorUpdatedCommand);
                        if (s_OffscreenTarget != null)
                        {
                            s_OffscreenTarget.Repaint();
                            InternalEditorUtility.RepaintAllViews();
                        }
                    };
                    if (!appliedToProperty)
                    {
                        CodelyLogger.LogWarning(
                            $"[NWB-ObjectSelector] Select fallback: selector closed, instanceId={instanceId} resolved={(selectedObject != null)}");
                    }
                }

                s_ObjectSelectorSessionActive = false;
                LogVerbose(
                    $"[NWB-ObjectSelector] Applied selection instanceId={instanceId} direct={appliedToProperty} open={selectorOpen} targets={FormatTargetIds()} path={s_CachedObjectSelectorPropertyPath ?? "null"}");
                return appliedToProperty;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjectSelector] Select error: {ex.Message}");
                return false;
            }
        }

        private static void TryHandleObjectSelectorPopupSearch(string filter)
        {
            if (!s_ObjectSelectorSessionActive)
                return;

            object selector = s_CachedObjectSelector ?? GetLiveObjectSelector();
            if (selector == null)
                return;

            ApplyObjectSelectorTabAndFilter(
                selector,
                s_CachedObjectSelectorShowingAssets,
                filter ?? "");

            EditorApplication.delayCall += ResendObjectSelectorPopupAfterFilter;
        }

        private static void ResendObjectSelectorPopupAfterFilter()
        {
            if (s_PopupSourceType != "object_selector")
                return;

            s_FrontendPopupSent = false;
            TrySendObjectSelectorPopup(
                s_LastFrontendPopupX,
                s_LastFrontendPopupY,
                s_ObjectSelectorPopupWidth,
                s_ObjectSelectorPopupHeight,
                allowEmptyItems: true,
                preservePopupId: true);
        }

        private static void TryUpdateObjectSelectorListSelection(object selector, int instanceId)
        {
            if (selector == null || s_ObjectSelectorListAreaField == null)
                return;

            object listArea = s_ObjectSelectorListAreaField.GetValue(selector);
            if (listArea == null)
                return;

            MethodInfo initSelection = listArea.GetType().GetMethod(
                "InitSelection",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(int[]) },
                null);
            initSelection?.Invoke(listArea, new object[] { new[] { instanceId } });
        }

        private static void TryHandleObjectSelectorPopupTab(string tabId)
        {
            if (!s_ObjectSelectorSessionActive)
            {
                CodelyLogger.LogWarning("[NWB-ObjectSelector] Tab switch failed: ObjectSelector session inactive");
                return;
            }

            object selector = s_CachedObjectSelector ?? GetLiveObjectSelector();
            if (selector == null)
            {
                CodelyLogger.LogWarning("[NWB-ObjectSelector] Tab switch failed: ObjectSelector not found");
                return;
            }

            bool showAssets = tabId != "scene";
            if (!ApplyObjectSelectorTabAndFilter(selector, showAssets))
            {
                CodelyLogger.LogWarning("[NWB-ObjectSelector] Tab switch failed: ObjectSelector not ready");
                return;
            }

            if (selector is EditorWindow window)
                window.Repaint();

            s_FrontendPopupSent = false;
            TrySendObjectSelectorPopup(
                s_LastFrontendPopupX,
                s_LastFrontendPopupY,
                s_ObjectSelectorPopupWidth,
                s_ObjectSelectorPopupHeight,
                allowEmptyItems: true,
                preservePopupId: true);
            LogVerbose($"[NWB-ObjectSelector] Switched tab to {(showAssets ? "Assets" : "Scene")}");
        }

        private static void CancelObjectSelectorSession()
        {
            object selector = s_CachedObjectSelector ?? GetLiveObjectSelector();
            if (selector == null || !IsObjectSelectorOpen(selector))
                return;

            EnsureObjectSelectorReflection();
            try
            {
                s_ObjectSelectorCancelMethod?.Invoke(selector, null);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ObjectSelector] Cancel error: {ex.Message}");
                if (selector is EditorWindow window)
                {
                    try { window.Close(); } catch { /* best effort */ }
                }
            }
        }

        private static void ClearObjectSelectorPopupState()
        {
            s_CachedObjectSelector = null;
            s_CachedObjectSelectorDelegateView = null;
            s_CachedObjectSelectorControlId = 0;
            s_CachedObjectSelectorSelectedInstanceId = 0;
            s_ObjectSelectorSearchFilter = "";
            s_ObjectSelectorPopupWidth = 0f;
            s_ObjectSelectorPopupHeight = 0f;
            s_ObjectSelectorInterceptRetryScheduled = false;
            s_ObjectSelectorSessionActive = false;
            s_CachedObjectSelectorShowingAssets = true;
            s_CachedObjectSelectorAllowScene = false;
            s_CachedObjectSelectorPropertyPath = null;
            s_CachedObjectSelectorTargetInstanceIds = null;
            s_CachedObjectBeingEditedId = 0;
            s_CachedObjectSelectorOriginalSelectionId = 0;
            s_CachedObjectSelectorRequiredTypes = null;
            s_CachedObjectSelectorInstanceLabels.Clear();
        }
    }
}
