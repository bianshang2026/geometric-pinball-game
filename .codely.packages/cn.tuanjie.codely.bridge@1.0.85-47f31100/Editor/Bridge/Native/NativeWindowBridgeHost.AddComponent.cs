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
        private static GameObject[] s_CachedAddComponentTargets;
        private static string s_AddComponentSearchFilter = "";
        private static float s_AddComponentPopupWidth;
        private static float s_AddComponentPopupHeight;

        private static bool s_AddComponentReflectionDone;
        private static Type s_AddComponentDataSourceType;
        private static Type s_ComponentDropdownItemType;
        private static Type s_AdvancedDropdownDataSourceType;
        private static Type s_AdvancedDropdownItemType;
        private static FieldInfo s_AddComponentWindowStateField;
        private static PropertyInfo s_AdvancedDropdownMainTreeProp;
        private static PropertyInfo s_AdvancedDropdownSearchTreeProp;
        private static PropertyInfo s_AdvancedDropdownItemChildrenProp;
        private static PropertyInfo s_AdvancedDropdownItemDisplayNameProp;
        private static PropertyInfo s_AdvancedDropdownItemHasChildrenProp;
        private static PropertyInfo s_ComponentDropdownMenuPathProp;
        private static MethodInfo s_AdvancedDropdownReloadDataMethod;
        private static MethodInfo s_AdvancedDropdownRebuildSearchMethod;
        private static MethodInfo s_ExecuteMenuItemOnGameObjectsMethod;

        private static void EnsureAddComponentReflection()
        {
            if (s_AddComponentReflectionDone) return;
            s_AddComponentReflectionDone = true;

            try
            {
                var editorAsm = typeof(EditorWindow).Assembly;
                s_AddComponentDataSourceType = editorAsm.GetType("UnityEditor.AddComponent.AddComponentDataSource");
                s_ComponentDropdownItemType = editorAsm.GetType("UnityEditor.AddComponent.ComponentDropdownItem");
                s_AdvancedDropdownDataSourceType = editorAsm.GetType("UnityEditor.IMGUI.Controls.AdvancedDropdownDataSource");
                s_AdvancedDropdownItemType = editorAsm.GetType("UnityEditor.IMGUI.Controls.AdvancedDropdownItem");

                Type addComponentWindowType = editorAsm.GetType("UnityEditor.AddComponent.AddComponentWindow");
                if (addComponentWindowType != null)
                {
                    s_AddComponentWindowStateField = addComponentWindowType.GetField(
                        "s_State",
                        BindingFlags.Static | BindingFlags.NonPublic);
                }

                if (s_AdvancedDropdownDataSourceType != null)
                {
                    s_AdvancedDropdownMainTreeProp = s_AdvancedDropdownDataSourceType.GetProperty(
                        "mainTree",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    s_AdvancedDropdownSearchTreeProp = s_AdvancedDropdownDataSourceType.GetProperty(
                        "searchTree",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    s_AdvancedDropdownReloadDataMethod = s_AdvancedDropdownDataSourceType.GetMethod(
                        "ReloadData",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    s_AdvancedDropdownRebuildSearchMethod = s_AdvancedDropdownDataSourceType.GetMethod(
                        "RebuildSearch",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (s_AdvancedDropdownItemType != null)
                {
                    s_AdvancedDropdownItemChildrenProp = s_AdvancedDropdownItemType.GetProperty(
                        "children",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    s_AdvancedDropdownItemHasChildrenProp = s_AdvancedDropdownItemType.GetProperty(
                        "hasChildren",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    for (Type t = s_AdvancedDropdownItemType; t != null && s_AdvancedDropdownItemDisplayNameProp == null; t = t.BaseType)
                    {
                        s_AdvancedDropdownItemDisplayNameProp = t.GetProperty(
                            "displayName",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                }

                if (s_ComponentDropdownItemType != null)
                {
                    s_ComponentDropdownMenuPathProp = s_ComponentDropdownItemType.GetProperty(
                        "menuPath",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                s_ExecuteMenuItemOnGameObjectsMethod = typeof(EditorApplication).GetMethod(
                    "ExecuteMenuItemOnGameObjects",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new[] { typeof(string), typeof(GameObject[]) },
                    null);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-AddComponent] Reflection init error: {ex.Message}");
            }
        }

        private static bool TryExtractAddComponentPopup(
            EditorWindow hostedWindow, float relX, float relY, float popW, float popH)
        {
            EnsureAddComponentReflection();
            if (hostedWindow == null) return false;

            try
            {
                FieldInfo targetsField = hostedWindow.GetType().GetField(
                    "m_GameObjects",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                s_CachedAddComponentTargets = targetsField?.GetValue(hostedWindow) as GameObject[];
                if (s_CachedAddComponentTargets == null || s_CachedAddComponentTargets.Length == 0)
                {
                    LogVerbose("[NWB-AddComponent] No target GameObjects on AddComponentWindow");
                    return false;
                }

                PropertyInfo searchProp = hostedWindow.GetType().GetProperty(
                    "searchString",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (searchProp != null && string.IsNullOrEmpty(s_AddComponentSearchFilter))
                {
                    s_AddComponentSearchFilter = searchProp.GetValue(hostedWindow) as string ?? "";
                }

                s_AddComponentPopupWidth = popW;
                s_AddComponentPopupHeight = popH;
                s_LastFrontendPopupX = relX;
                s_LastFrontendPopupY = relY;

                return TrySendAddComponentPopup(relX, relY, popW, popH);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-AddComponent] Extract error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static bool TrySendAddComponentPopup(float relX, float relY, float popW, float popH)
        {
            EnsureAddComponentReflection();
            if (s_CachedAddComponentTargets == null || s_CachedAddComponentTargets.Length == 0)
                return false;
            if (s_AddComponentDataSourceType == null ||
                s_AddComponentWindowStateField == null ||
                s_AdvancedDropdownMainTreeProp == null ||
                s_AdvancedDropdownReloadDataMethod == null)
            {
                CodelyLogger.LogWarning("[NWB-AddComponent] Required reflection types not found");
                return false;
            }

            try
            {
                object state = s_AddComponentWindowStateField.GetValue(null);
                if (state == null)
                    state = Activator.CreateInstance(s_AddComponentWindowStateField.FieldType);

                object dataSource = Activator.CreateInstance(
                    s_AddComponentDataSourceType,
                    state,
                    s_CachedAddComponentTargets);
                s_AdvancedDropdownReloadDataMethod.Invoke(dataSource, null);

                object mainTree = s_AdvancedDropdownMainTreeProp.GetValue(dataSource);
                object treeToRender = mainTree;

                if (!string.IsNullOrEmpty(s_AddComponentSearchFilter) &&
                    s_AdvancedDropdownRebuildSearchMethod != null &&
                    mainTree != null)
                {
                    s_AdvancedDropdownRebuildSearchMethod.Invoke(
                        dataSource,
                        new object[] { s_AddComponentSearchFilter, mainTree });
                    object searchTree = s_AdvancedDropdownSearchTreeProp?.GetValue(dataSource);
                    if (searchTree != null)
                        treeToRender = searchTree;
                }

                if (treeToRender == null)
                {
                    LogVerbose("[NWB-AddComponent] Component tree is null");
                    return false;
                }

                var pathList = new List<string>();
                int itemIndex = 0;
                int validCount = 0;

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "add_component";

                var sb = new StringBuilder(65536);
                sb.Append("{\"type\":\"popup_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"sourceType\":\"add_component\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                sb.Append(",\"width\":");
                sb.Append(Mathf.RoundToInt(Mathf.Max(popW, 200f)));
                sb.Append(",\"height\":");
                sb.Append(Mathf.RoundToInt(Mathf.Max(popH, 280f)));
                sb.Append(",\"searchText\":\"");
                sb.Append(EscapeJsonString(s_AddComponentSearchFilter ?? ""));
                sb.Append("\",\"items\":[");
                bool needComma = false;
                AppendAddComponentDropdownItems(
                    sb,
                    GetAdvancedDropdownChildren(treeToRender),
                    ref itemIndex,
                    ref validCount,
                    ref needComma,
                    pathList);
                sb.Append("]}");

                if (validCount <= 0)
                {
                    LogVerbose("[NWB-AddComponent] No selectable component items extracted");
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
                        LogVerbose($"[NWB-AddComponent] Sent chunked popup: bytes={payloadBytes} items={validCount}");
                }
                else
                {
                    sent = SendDataChannelMessage(payload);
                }

                s_FrontendPopupSent = sent;
                if (sent)
                {
                    CancelNativeDelayedPopup();
                    LogVerbose($"[NWB-AddComponent] Sent popup: id={s_CurrentFrontendPopupId} items={validCount} search='{s_AddComponentSearchFilter}'");
                }
                else
                {
                    CodelyLogger.LogWarning($"[NWB-AddComponent] Failed to send popup: id={s_CurrentFrontendPopupId}");
                }

                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-AddComponent] Send error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static IEnumerable GetAdvancedDropdownChildren(object item)
        {
            if (item == null || s_AdvancedDropdownItemChildrenProp == null)
                return new object[0];
            return s_AdvancedDropdownItemChildrenProp.GetValue(item) as IEnumerable ?? new object[0];
        }

        private static void AppendAddComponentDropdownItems(
            StringBuilder sb,
            IEnumerable items,
            ref int itemIndex,
            ref int validCount,
            ref bool needComma,
            List<string> pathList)
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
                    AppendAddComponentDropdownItems(
                        sb,
                        GetAdvancedDropdownChildren(item),
                        ref itemIndex,
                        ref childValid,
                        ref childComma,
                        pathList);
                    sb.Append("]}");
                    if (childValid > 0)
                        validCount++;
                    continue;
                }

                if (s_ComponentDropdownItemType == null ||
                    !s_ComponentDropdownItemType.IsInstanceOfType(item) ||
                    s_ComponentDropdownMenuPathProp == null)
                {
                    continue;
                }

                string menuPath = s_ComponentDropdownMenuPathProp.GetValue(item) as string;
                if (string.IsNullOrEmpty(menuPath))
                    continue;

                if (needComma) sb.Append(',');
                needComma = true;
                sb.Append("{\"index\":");
                sb.Append(itemIndex);
                sb.Append(",\"path\":\"");
                sb.Append(EscapeJsonString(menuPath));
                sb.Append("\",\"label\":\"");
                sb.Append(EscapeJsonString(label));
                sb.Append("\",\"enabled\":true,\"checked\":false,\"separator\":false}");
                pathList.Add(menuPath);
                itemIndex++;
                validCount++;
            }
        }

        private static string GetAdvancedDropdownDisplayName(object item)
        {
            if (item == null || s_AdvancedDropdownItemDisplayNameProp == null)
                return "";
            return s_AdvancedDropdownItemDisplayNameProp.GetValue(item) as string ?? "";
        }

        private static bool TryHandleAddComponentPopupSelect(string path)
        {
            if (string.IsNullOrEmpty(path) || s_CachedAddComponentTargets == null)
                return false;

            EnsureAddComponentReflection();
            if (s_ExecuteMenuItemOnGameObjectsMethod == null)
            {
                CodelyLogger.LogWarning("[NWB-AddComponent] ExecuteMenuItemOnGameObjects not found");
                return false;
            }

            try
            {
                bool ok = (bool)s_ExecuteMenuItemOnGameObjectsMethod.Invoke(
                    null,
                    new object[] { path, s_CachedAddComponentTargets });
                LogVerbose($"[NWB-AddComponent] ExecuteMenuItemOnGameObjects path='{path}' ok={ok}");
                return ok;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-AddComponent] Execute error: {ex.Message}");
                return false;
            }
        }

        private static void ClearAddComponentPopupState()
        {
            s_CachedAddComponentTargets = null;
            s_AddComponentSearchFilter = "";
            s_AddComponentPopupWidth = 0f;
            s_AddComponentPopupHeight = 0f;
        }
    }
}
