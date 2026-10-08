using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Native
{
    internal static partial class NativeWindowBridgeHost
    {
        private enum AdvancedDropdownHandlerKind
        {
            StatelessIndex,
            Multiselect,
            MaterialShader,
        }

        private static object s_CachedAdvancedDropdownDataSource;
        private static AdvancedDropdownHandlerKind s_AdvancedDropdownHandlerKind;
        private static MaterialEditor s_CachedAdvancedDropdownMaterialEditor;
        private static string s_AdvancedDropdownSearchFilter = "";
        private static float s_AdvancedDropdownPopupWidth;
        private static float s_AdvancedDropdownPopupHeight;

        private static bool s_AdvancedDropdownWindowReflectionDone;
        private static PropertyInfo s_AdvancedDropdownWindowDataSourceProp;
        private static PropertyInfo s_AdvancedDropdownWindowSearchProp;
        private static PropertyInfo s_AdvancedDropdownItemElementIndexProp;
        private static PropertyInfo s_AdvancedDropdownItemIdProp;
        private static PropertyInfo s_AdvancedDropdownDataSourceSelectedIdsProp;
        private static PropertyInfo s_ShaderDropdownItemFullNameProp;
        private static FieldInfo s_MaterialEditorsField;
        private static FieldInfo s_MaterialEditorShaderField;
        private static FieldInfo s_ShaderDropdownCurrentShaderField;
        private static MethodInfo s_MaterialEditorOnSelectedShaderPopupMethod;
        private static MethodInfo s_MultiselectUpdateSelectedIdMethod;
        private static Type s_StatelessAdvancedDropdownType;
        private static FieldInfo s_StatelessAdvancedDropdownResultField;
        private static FieldInfo s_StatelessAdvancedDropdownWindowClosedField;
        private static FieldInfo s_StatelessAdvancedDropdownShouldReturnValueField;
        private static FieldInfo s_StatelessAdvancedDropdownParentWindowField;

        private static void EnsureAdvancedDropdownWindowReflection()
        {
            if (s_AdvancedDropdownWindowReflectionDone) return;
            s_AdvancedDropdownWindowReflectionDone = true;

            try
            {
                EnsureAddComponentReflection();

                var editorAsm = typeof(EditorWindow).Assembly;
                Type advancedDropdownWindowType = editorAsm.GetType(
                    "UnityEditor.IMGUI.Controls.AdvancedDropdownWindow");
                if (advancedDropdownWindowType != null)
                {
                    s_AdvancedDropdownWindowDataSourceProp = advancedDropdownWindowType.GetProperty(
                        "dataSource",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    s_AdvancedDropdownWindowSearchProp = advancedDropdownWindowType.GetProperty(
                        "searchString",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (s_AdvancedDropdownItemType != null)
                {
                    s_AdvancedDropdownItemElementIndexProp = s_AdvancedDropdownItemType.GetProperty(
                        "elementIndex",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    s_AdvancedDropdownItemIdProp = s_AdvancedDropdownItemType.GetProperty(
                        "id",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (s_AdvancedDropdownDataSourceType != null)
                {
                    s_AdvancedDropdownDataSourceSelectedIdsProp = s_AdvancedDropdownDataSourceType.GetProperty(
                        "selectedIDs",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                s_StatelessAdvancedDropdownType = editorAsm.GetType("UnityEditor.StatelessAdvancedDropdown");
                if (s_StatelessAdvancedDropdownType != null)
                {
                    const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                    s_StatelessAdvancedDropdownResultField = s_StatelessAdvancedDropdownType.GetField("m_Result", staticFlags);
                    s_StatelessAdvancedDropdownWindowClosedField = s_StatelessAdvancedDropdownType.GetField("m_WindowClosed", staticFlags);
                    s_StatelessAdvancedDropdownShouldReturnValueField = s_StatelessAdvancedDropdownType.GetField("m_ShouldReturnValue", staticFlags);
                    s_StatelessAdvancedDropdownParentWindowField = s_StatelessAdvancedDropdownType.GetField("s_ParentWindow", staticFlags);
                }

                Type multiselectDataSourceType = editorAsm.GetType(
                    "UnityEditor.IMGUI.Controls.MultiselectDataSource");
                if (multiselectDataSourceType != null)
                {
                    s_MultiselectUpdateSelectedIdMethod = multiselectDataSourceType.GetMethod(
                        "UpdateSelectedId",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                foreach (Type nested in typeof(MaterialEditor).GetNestedTypes(
                             BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (nested.Name == "ShaderDropdownItem")
                    {
                        s_ShaderDropdownItemFullNameProp = nested.GetProperty(
                            "fullName",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        break;
                    }
                }

                foreach (Type nested in typeof(MaterialEditor).GetNestedTypes(
                             BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (nested.Name != "ShaderSelectionDropdown") continue;
                    foreach (Type inner in nested.GetNestedTypes(
                                 BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (inner.Name == "ShaderDropdownDataSource")
                        {
                            s_ShaderDropdownCurrentShaderField = inner.GetField(
                                "m_CurrentShader",
                                BindingFlags.Instance | BindingFlags.NonPublic);
                            break;
                        }
                    }
                    break;
                }

                s_MaterialEditorsField = typeof(MaterialEditor).GetField(
                    "s_MaterialEditors",
                    BindingFlags.Static | BindingFlags.NonPublic);
                s_MaterialEditorShaderField = typeof(MaterialEditor).GetField(
                    "m_Shader",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                s_MaterialEditorOnSelectedShaderPopupMethod = typeof(MaterialEditor).GetMethod(
                    "OnSelectedShaderPopup",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-AdvancedDropdown] Reflection init error: {ex.Message}");
            }
        }

        private static bool TryExtractAdvancedDropdownPopup(
            EditorWindow hostedWindow, float relX, float relY, float popW, float popH)
        {
            EnsureAdvancedDropdownWindowReflection();
            if (hostedWindow == null || s_AdvancedDropdownWindowDataSourceProp == null)
                return false;

            try
            {
                object dataSource = s_AdvancedDropdownWindowDataSourceProp.GetValue(hostedWindow);
                if (dataSource == null)
                    return false;

                string dataSourceName = dataSource.GetType().Name;
                AdvancedDropdownHandlerKind kind;
                switch (dataSourceName)
                {
                    case "ShaderDropdownDataSource":
                        kind = AdvancedDropdownHandlerKind.MaterialShader;
                        s_CachedAdvancedDropdownMaterialEditor = FindMaterialEditorForShaderDropdown(dataSource);
                        if (s_CachedAdvancedDropdownMaterialEditor == null)
                        {
                            LogVerbose("[NWB-AdvancedDropdown] No MaterialEditor for shader dropdown");
                            return false;
                        }
                        break;
                    case "SimpleDataSource":
                    case "MultiLevelDataSource":
                        kind = AdvancedDropdownHandlerKind.StatelessIndex;
                        s_CachedAdvancedDropdownMaterialEditor = null;
                        break;
                    case "MultiselectDataSource":
                        kind = AdvancedDropdownHandlerKind.Multiselect;
                        s_CachedAdvancedDropdownMaterialEditor = null;
                        break;
                    default:
                        LogVerbose($"[NWB-AdvancedDropdown] Unsupported dataSource: {dataSourceName}");
                        return false;
                }

                s_CachedAdvancedDropdownDataSource = dataSource;
                s_AdvancedDropdownHandlerKind = kind;

                if (string.IsNullOrEmpty(s_AdvancedDropdownSearchFilter) &&
                    s_AdvancedDropdownWindowSearchProp != null)
                {
                    s_AdvancedDropdownSearchFilter =
                        s_AdvancedDropdownWindowSearchProp.GetValue(hostedWindow) as string ?? "";
                }

                s_AdvancedDropdownPopupWidth = popW;
                s_AdvancedDropdownPopupHeight = popH;
                s_LastFrontendPopupX = relX;
                s_LastFrontendPopupY = relY;

                LogVerbose($"[NWB-AdvancedDropdown] Extracting {dataSourceName} ({kind})");
                return TrySendAdvancedDropdownPopup(relX, relY, popW, popH);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-AdvancedDropdown] Extract error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static MaterialEditor FindMaterialEditorForShaderDropdown(object dataSource)
        {
            if (s_MaterialEditorsField == null)
                return null;

            Shader currentShader = null;
            if (s_ShaderDropdownCurrentShaderField != null)
                currentShader = s_ShaderDropdownCurrentShaderField.GetValue(dataSource) as Shader;

            var editors = s_MaterialEditorsField.GetValue(null) as IList;
            if (editors == null || editors.Count == 0)
                return null;

            MaterialEditor inspectorMatch = null;
            MaterialEditor shaderMatch = null;

            foreach (object editorObj in editors)
            {
                var materialEditor = editorObj as MaterialEditor;
                if (materialEditor == null)
                    continue;

                if (s_OffscreenTarget != null &&
                    IsMaterialEditorOnTargetInspector(materialEditor))
                {
                    inspectorMatch = materialEditor;
                }

                if (s_MaterialEditorShaderField != null && currentShader != null)
                {
                    var editorShader = s_MaterialEditorShaderField.GetValue(materialEditor) as Shader;
                    if (editorShader == currentShader)
                        shaderMatch = materialEditor;
                }
            }

            return inspectorMatch ?? shaderMatch ?? (editors[0] as MaterialEditor);
        }

        private static bool IsMaterialEditorOnTargetInspector(MaterialEditor materialEditor)
        {
            if (s_OffscreenTarget == null)
                return false;

            try
            {
                var trackerField = s_OffscreenTarget.GetType().GetField(
                    "m_Tracker",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                object tracker = trackerField?.GetValue(s_OffscreenTarget);
                if (tracker == null)
                    return false;

                PropertyInfo activeEditorsProp = tracker.GetType().GetProperty(
                    "activeEditors",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var activeEditors = activeEditorsProp?.GetValue(tracker) as UnityEditor.Editor[];
                if (activeEditors == null)
                    return false;

                foreach (UnityEditor.Editor editor in activeEditors)
                {
                    if (editor == materialEditor)
                        return true;
                }
            }
            catch
            {
                // Best-effort inspector association.
            }

            return false;
        }

        private static bool TrySendAdvancedDropdownPopup(float relX, float relY, float popW, float popH)
        {
            EnsureAdvancedDropdownWindowReflection();
            if (s_CachedAdvancedDropdownDataSource == null || s_AdvancedDropdownMainTreeProp == null)
            {
                CodelyLogger.LogWarning("[NWB-AdvancedDropdown] Missing cached dropdown state");
                return false;
            }

            try
            {
                object mainTree = s_AdvancedDropdownMainTreeProp.GetValue(s_CachedAdvancedDropdownDataSource);
                object treeToRender = mainTree;

                if (!string.IsNullOrEmpty(s_AdvancedDropdownSearchFilter) &&
                    s_AdvancedDropdownRebuildSearchMethod != null &&
                    mainTree != null)
                {
                    s_AdvancedDropdownRebuildSearchMethod.Invoke(
                        s_CachedAdvancedDropdownDataSource,
                        new object[] { s_AdvancedDropdownSearchFilter, mainTree });
                    object searchTree = s_AdvancedDropdownSearchTreeProp?.GetValue(s_CachedAdvancedDropdownDataSource);
                    if (searchTree != null)
                        treeToRender = searchTree;
                }

                if (treeToRender == null)
                {
                    LogVerbose("[NWB-AdvancedDropdown] Dropdown tree is null");
                    return false;
                }

                var pathList = new List<string>();
                int itemIndex = 0;
                int validCount = 0;
                var selectedIds = GetAdvancedDropdownSelectedIds(s_CachedAdvancedDropdownDataSource);

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "advanced_dropdown";

                var sb = new StringBuilder(65536);
                sb.Append("{\"type\":\"popup_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"sourceType\":\"advanced_dropdown\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                sb.Append(",\"width\":");
                sb.Append(Mathf.RoundToInt(Mathf.Max(popW, 200f)));
                sb.Append(",\"height\":");
                sb.Append(Mathf.RoundToInt(Mathf.Max(popH, 280f)));
                sb.Append(",\"searchText\":\"");
                sb.Append(EscapeJsonString(s_AdvancedDropdownSearchFilter ?? ""));
                sb.Append("\",\"items\":[");
                bool needComma = false;
                AppendAdvancedDropdownItems(
                    sb,
                    GetAdvancedDropdownChildren(treeToRender),
                    ref itemIndex,
                    ref validCount,
                    ref needComma,
                    pathList,
                    selectedIds);
                sb.Append("]}");

                if (validCount <= 0)
                {
                    LogVerbose("[NWB-AdvancedDropdown] No selectable dropdown items extracted");
                    return false;
                }

                s_CachedPopupItemPaths = pathList.ToArray();
                string payload = sb.ToString();
                int payloadBytes = Encoding.UTF8.GetByteCount(payload);

                bool sent;
                if (payloadBytes > kMaxDataChannelPayloadBytes)
                {
                    sent = SendPopupPayloadChunked(s_CurrentFrontendPopupId, payload, payloadBytes);
                    if (sent)
                        LogVerbose($"[NWB-AdvancedDropdown] Sent chunked popup: bytes={payloadBytes} items={validCount}");
                }
                else
                {
                    sent = SendDataChannelMessage(payload);
                }

                s_FrontendPopupSent = sent;
                if (sent)
                {
                    CancelNativeDelayedPopup();
                    LogVerbose($"[NWB-AdvancedDropdown] Sent popup: id={s_CurrentFrontendPopupId} kind={s_AdvancedDropdownHandlerKind} items={validCount}");
                }
                else
                {
                    CodelyLogger.LogWarning($"[NWB-AdvancedDropdown] Failed to send popup: id={s_CurrentFrontendPopupId}");
                }

                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-AdvancedDropdown] Send error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static HashSet<int> GetAdvancedDropdownSelectedIds(object dataSource)
        {
            var selected = new HashSet<int>();
            if (dataSource == null || s_AdvancedDropdownDataSourceSelectedIdsProp == null)
                return selected;

            var selectedIds = s_AdvancedDropdownDataSourceSelectedIdsProp.GetValue(dataSource) as IList;
            if (selectedIds == null)
                return selected;

            foreach (object idObj in selectedIds)
            {
                if (idObj is int id)
                    selected.Add(id);
            }

            return selected;
        }

        private static int GetAdvancedDropdownItemId(object item)
        {
            if (item == null || s_AdvancedDropdownItemIdProp == null)
                return 0;
            return (int)s_AdvancedDropdownItemIdProp.GetValue(item);
        }

        private static int GetAdvancedDropdownElementIndex(object item)
        {
            if (item == null || s_AdvancedDropdownItemElementIndexProp == null)
                return -1;
            return (int)s_AdvancedDropdownItemElementIndexProp.GetValue(item);
        }

        private static void AppendAdvancedDropdownItems(
            StringBuilder sb,
            IEnumerable items,
            ref int itemIndex,
            ref int validCount,
            ref bool needComma,
            List<string> pathList,
            HashSet<int> selectedIds)
        {
            if (items == null) return;

            foreach (object item in items)
            {
                if (item == null) continue;
                if (item.GetType().Name == "SeparatorDropdownItem")
                    continue;

                string label = GetAdvancedDropdownDisplayName(item);
                if (string.IsNullOrEmpty(label))
                    continue;

                bool hasChildren = s_AdvancedDropdownItemHasChildrenProp != null &&
                                   (bool)s_AdvancedDropdownItemHasChildrenProp.GetValue(item);

                if (hasChildren)
                {
                    if (needComma) sb.Append(',');
                    needComma = true;
                    sb.Append("{\"label\":\"");
                    sb.Append(EscapeJsonString(label));
                    sb.Append("\",\"enabled\":true,\"checked\":false,\"separator\":false,\"submenu\":true,\"children\":[");
                    bool childComma = false;
                    int childValid = 0;
                    AppendAdvancedDropdownItems(
                        sb,
                        GetAdvancedDropdownChildren(item),
                        ref itemIndex,
                        ref childValid,
                        ref childComma,
                        pathList,
                        selectedIds);
                    sb.Append("]}");
                    if (childValid > 0)
                        validCount++;
                    continue;
                }

                string path = null;
                if (item.GetType().Name == "ShaderDropdownItem" &&
                    s_ShaderDropdownItemFullNameProp != null)
                {
                    path = s_ShaderDropdownItemFullNameProp.GetValue(item) as string;
                }
                else
                {
                    int elementIndex = GetAdvancedDropdownElementIndex(item);
                    if (elementIndex >= 0)
                        path = elementIndex.ToString();
                }

                if (string.IsNullOrEmpty(path))
                    continue;

                bool isChecked = selectedIds != null && selectedIds.Contains(GetAdvancedDropdownItemId(item));

                if (needComma) sb.Append(',');
                needComma = true;
                sb.Append("{\"index\":");
                sb.Append(itemIndex);
                sb.Append(",\"path\":\"");
                sb.Append(EscapeJsonString(path));
                sb.Append("\",\"label\":\"");
                sb.Append(EscapeJsonString(label));
                sb.Append("\",\"enabled\":true,\"checked\":");
                sb.Append(isChecked ? "true" : "false");
                sb.Append(",\"separator\":false}");
                pathList.Add(path);
                itemIndex++;
                validCount++;
            }
        }

        /// <summary>
        /// Returns true when the frontend popup should stay open (multiselect).
        /// </summary>
        private static bool TryHandleAdvancedDropdownPopupSelect(string path)
        {
            if (string.IsNullOrEmpty(path) || s_CachedAdvancedDropdownDataSource == null)
                return false;

            EnsureAdvancedDropdownWindowReflection();

            try
            {
                switch (s_AdvancedDropdownHandlerKind)
                {
                    case AdvancedDropdownHandlerKind.MaterialShader:
                        TryHandleMaterialShaderDropdownSelect(path);
                        return false;
                    case AdvancedDropdownHandlerKind.Multiselect:
                        TryHandleMultiselectDropdownSelect(path);
                        return true;
                    default:
                        TryHandleStatelessIndexDropdownSelect(path);
                        return false;
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-AdvancedDropdown] Select error: {ex.Message}");
                return false;
            }
        }

        private static bool TryHandleMaterialShaderDropdownSelect(string path)
        {
            if (s_CachedAdvancedDropdownMaterialEditor == null ||
                s_MaterialEditorOnSelectedShaderPopupMethod == null)
            {
                CodelyLogger.LogWarning("[NWB-AdvancedDropdown] Material shader select handler unavailable");
                return false;
            }

            s_MaterialEditorOnSelectedShaderPopupMethod.Invoke(
                s_CachedAdvancedDropdownMaterialEditor,
                new object[] { path });
            LogVerbose($"[NWB-AdvancedDropdown] Applied shader: '{path}'");
            return true;
        }

        private static bool TryHandleStatelessIndexDropdownSelect(string path)
        {
            if (!int.TryParse(path, out int selectedIndex))
                return false;

            if (s_StatelessAdvancedDropdownResultField == null ||
                s_StatelessAdvancedDropdownWindowClosedField == null)
            {
                CodelyLogger.LogWarning("[NWB-AdvancedDropdown] StatelessAdvancedDropdown fields not found");
                return false;
            }

            s_StatelessAdvancedDropdownResultField.SetValue(null, selectedIndex);
            s_StatelessAdvancedDropdownWindowClosedField.SetValue(null, true);
            RepaintStatelessAdvancedDropdownParent();
            LogVerbose($"[NWB-AdvancedDropdown] Applied index selection: {selectedIndex}");
            return true;
        }

        private static bool TryHandleMultiselectDropdownSelect(string path)
        {
            if (!int.TryParse(path, out int selectedIndex) ||
                s_MultiselectUpdateSelectedIdMethod == null)
            {
                return false;
            }

            object item = FindAdvancedDropdownLeafByElementIndex(
                s_AdvancedDropdownMainTreeProp.GetValue(s_CachedAdvancedDropdownDataSource),
                selectedIndex);
            if (item == null)
            {
                CodelyLogger.LogWarning($"[NWB-AdvancedDropdown] Multiselect item not found: index={selectedIndex}");
                return true;
            }

            s_MultiselectUpdateSelectedIdMethod.Invoke(s_CachedAdvancedDropdownDataSource, new[] { item });

            if (s_StatelessAdvancedDropdownShouldReturnValueField != null)
                s_StatelessAdvancedDropdownShouldReturnValueField.SetValue(null, true);

            RepaintStatelessAdvancedDropdownParent();

            s_FrontendPopupSent = false;
            TrySendAdvancedDropdownPopup(
                s_LastFrontendPopupX,
                s_LastFrontendPopupY,
                s_AdvancedDropdownPopupWidth,
                s_AdvancedDropdownPopupHeight);
            LogVerbose($"[NWB-AdvancedDropdown] Applied multiselect index: {selectedIndex}");
            return true;
        }

        private static object FindAdvancedDropdownLeafByElementIndex(object node, int elementIndex)
        {
            if (node == null)
                return null;

            foreach (object child in GetAdvancedDropdownChildren(node))
            {
                if (child == null || child.GetType().Name == "SeparatorDropdownItem")
                    continue;

                bool hasChildren = s_AdvancedDropdownItemHasChildrenProp != null &&
                                   (bool)s_AdvancedDropdownItemHasChildrenProp.GetValue(child);
                if (hasChildren)
                {
                    object found = FindAdvancedDropdownLeafByElementIndex(child, elementIndex);
                    if (found != null)
                        return found;
                    continue;
                }

                if (GetAdvancedDropdownElementIndex(child) == elementIndex)
                    return child;
            }

            return null;
        }

        private static void RepaintStatelessAdvancedDropdownParent()
        {
            EditorWindow parent = null;
            if (s_StatelessAdvancedDropdownParentWindowField != null)
                parent = s_StatelessAdvancedDropdownParentWindowField.GetValue(null) as EditorWindow;

            parent?.Repaint();
            s_OffscreenTarget?.Repaint();
        }

        private static void ClearAdvancedDropdownPopupState()
        {
            s_CachedAdvancedDropdownDataSource = null;
            s_CachedAdvancedDropdownMaterialEditor = null;
            s_AdvancedDropdownSearchFilter = "";
            s_AdvancedDropdownPopupWidth = 0f;
            s_AdvancedDropdownPopupHeight = 0f;
        }
    }
}
