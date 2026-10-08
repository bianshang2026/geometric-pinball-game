using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Native
{
    internal static partial class NativeWindowBridgeHost
    {
        // DockArea.kTabHeight — duplicated here to avoid coupling to Unity internals.
        private const float kDockTabStripHeight = 19f;
        // HostView/DockArea Styles.genericMenuTopOffset default (--window-generic-menu-top-offset).
        private const float kGenericMenuTopOffset = 20f;
        private const float kPaneOptionsButtonHeight = 18f;
        private const float kPaneOptionsHitWidth = 52f;

        private static bool s_PaneOptionsMouseDownPending;
        private static bool s_PaneOptionsReflectionDone;
        private static Type s_HostViewType;
        private static MethodInfo s_HostViewAddDefaultItemsToMenu;
        private static MethodInfo s_HostViewAddWindowActionMenu;
        private static FieldInfo s_GenericMenuItemsField;
        private static Type s_GenericMenuItemType;
        private static FieldInfo s_MenuItemContentField;
        private static FieldInfo s_MenuItemSeparatorField;
        private static FieldInfo s_MenuItemOnField;
        private static FieldInfo s_MenuItemFuncField;
        private static FieldInfo s_MenuItemFunc2Field;
        private static FieldInfo s_MenuItemUserDataField;

        private struct CachedPaneOptionsEntry
        {
            public Delegate callback;
            public object userData;
            public bool enabled;
        }

        private static CachedPaneOptionsEntry[] s_CachedPaneOptionsEntries;

        private static void EnsurePaneOptionsReflection()
        {
            if (s_PaneOptionsReflectionDone) return;
            s_PaneOptionsReflectionDone = true;

            try
            {
                var editorAsm = typeof(EditorWindow).Assembly;
                s_HostViewType = editorAsm.GetType("UnityEditor.HostView");
                if (s_HostViewType != null)
                {
                    s_HostViewAddDefaultItemsToMenu = s_HostViewType.GetMethod(
                        "AddDefaultItemsToMenu",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    s_HostViewAddWindowActionMenu = s_HostViewType.GetMethod(
                        "AddWindowActionMenu",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                }

                s_GenericMenuItemsField = typeof(GenericMenu).GetField(
                    "m_MenuItems", BindingFlags.Instance | BindingFlags.NonPublic);
                if (s_GenericMenuItemsField != null)
                {
                    var sample = new GenericMenu();
                    IList list = s_GenericMenuItemsField.GetValue(sample) as IList;
                    if (list != null && list.Count == 0)
                    {
                        sample.AddItem(new GUIContent("x"), false, () => { });
                        list = s_GenericMenuItemsField.GetValue(sample) as IList;
                    }
                    if (list != null && list.Count > 0)
                        s_GenericMenuItemType = list[0].GetType();
                }

                if (s_GenericMenuItemType != null)
                {
                    const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    s_MenuItemContentField = s_GenericMenuItemType.GetField("content", f);
                    s_MenuItemSeparatorField = s_GenericMenuItemType.GetField("separator", f);
                    s_MenuItemOnField = s_GenericMenuItemType.GetField("on", f);
                    s_MenuItemFuncField = s_GenericMenuItemType.GetField("func", f);
                    s_MenuItemFunc2Field = s_GenericMenuItemType.GetField("func2", f);
                    s_MenuItemUserDataField = s_GenericMenuItemType.GetField("userData", f);
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-PaneOptions] Reflection init failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Strict DockArea tab-strip hit-test (tab header only).
        /// Keep this narrow so toolbar controls (e.g. GameView Gizmos) are not
        /// misclassified as tab-header clicks.
        /// </summary>
        private static bool IsInDockTitleBarRegion(float mouseY)
        {
            float tabStripH = Mathf.Max(s_TabBarOffsetY, kDockTabStripHeight);
            return mouseY >= -2f && mouseY <= tabStripH + 2f;
        }

        /// <summary>
        /// PaneOptions (⋮) button vertical band in DockArea header.
        /// Unity draws it around tabStrip + genericMenuTopOffset.
        /// </summary>
        private static bool IsInPaneOptionsVerticalBand(float mouseY)
        {
            float tabStripH = Mathf.Max(s_TabBarOffsetY, kDockTabStripHeight);
            float top = tabStripH + kGenericMenuTopOffset - 2f;
            float bottom = top + kPaneOptionsButtonHeight + 4f;
            return mouseY >= top && mouseY <= bottom;
        }

        private static bool IsPaneOptionsClick(float mouseX, float mouseY, float viewWidth)
        {
            // In Play mode, the game area starts below the toolbar (tab strip + toolbar).
            // The PaneOptions vertical band extends into this region, so game UI buttons
            // at the top-right (e.g. "Menu") can be mistaken for the ⋮ pane-options button
            // when the GameView is narrow (composite mode). Skip PaneOptions interception
            // for clicks that fall in the game play area.
            bool isGameViewPlayMode = s_OffscreenTargetType != null
                && s_OffscreenTargetType.Name == "GameView"
                && EditorApplication.isPlaying;
            if (isGameViewPlayMode && mouseY >= s_TabBarOffsetY + kGameViewToolbarHeight)
                return false;

            if (!IsInPaneOptionsVerticalBand(mouseY)) return false;
            if (viewWidth <= kPaneOptionsHitWidth + 4f) return false;
            return mouseX >= viewWidth - kPaneOptionsHitWidth;
        }

        /// <summary>
        /// Unity menu shortcuts appear as either "Label _%s" (GenericMenu / GUIContent)
        /// or "Label %F5" (ShortcutManager SequenceToMenuString, space-separated).
        /// </summary>
        private static void SplitUnityMenuText(string text, out string label, out string shortcut)
        {
            label = text ?? "";
            shortcut = "";
            if (string.IsNullOrEmpty(text)) return;

            int sep = text.LastIndexOf(" _", StringComparison.Ordinal);
            if (sep >= 0)
            {
                label = text.Substring(0, sep);
                shortcut = FormatUnityShortcutDisplay(text.Substring(sep + 2));
                return;
            }

            int space = text.LastIndexOf(' ');
            if (space < 0) return;

            string tail = text.Substring(space + 1);
            if (!LooksLikeUnityMenuShortcutTail(tail)) return;

            label = text.Substring(0, space);
            shortcut = FormatUnityShortcutDisplay(tail);
        }

        private static bool LooksLikeUnityMenuShortcutTail(string tail)
        {
            if (string.IsNullOrEmpty(tail) || tail.Length < 2) return false;

            int i = 0;
            while (i < tail.Length)
            {
                char c = tail[i];
                if (c == '%' || c == '#' || c == '&' || c == '^' || c == '_')
                {
                    i++;
                    continue;
                }
                break;
            }

            if (i >= tail.Length || i == 0) return false;

            for (int j = i; j < tail.Length; j++)
            {
                if (!char.IsLetterOrDigit(tail[j])) return false;
            }

            return true;
        }

        private static string FormatUnityShortcutDisplay(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            if (raw.Length > 1 && raw[0] == '_')
                raw = raw.Substring(1);

            var sb = new StringBuilder(raw.Length + 8);
            for (int i = 0; i < raw.Length; i++)
            {
                switch (raw[i])
                {
                    case '%': sb.Append("Ctrl+"); break;
                    case '#': sb.Append("Shift+"); break;
                    case '&': sb.Append("Alt+"); break;
                    case '^': sb.Append("Ctrl+"); break;
                    default: sb.Append(raw[i]); break;
                }
            }

            string result = sb.ToString();
            if (result.EndsWith("+", StringComparison.Ordinal))
                result = result.Substring(0, result.Length - 1);

            if (result.Length >= 2 && result[0] == 'f' && char.IsDigit(result[1]))
                result = result.ToUpperInvariant();

            return result;
        }

        private static void AppendPaneOptionsMenuItemJson(
            StringBuilder sb, int index, string text, string tooltip,
            bool enabled, bool isOn, bool isSep, ref int validCount)
        {
            SplitUnityMenuText(text, out string displayLabel, out string shortcut);

            if (validCount > 0) sb.Append(',');
            sb.Append("{\"index\":");
            sb.Append(index);
            sb.Append(",\"path\":\"");
            sb.Append(EscapeJsonString(text));
            sb.Append("\",\"label\":\"");
            sb.Append(EscapeJsonString(displayLabel));
            sb.Append("\",\"tooltip\":\"");
            sb.Append(EscapeJsonString(tooltip));
            sb.Append("\",\"enabled\":");
            sb.Append(enabled ? "true" : "false");
            sb.Append(",\"checked\":");
            sb.Append(isOn ? "true" : "false");
            sb.Append(",\"separator\":");
            sb.Append(isSep ? "true" : "false");
            if (!string.IsNullOrEmpty(shortcut))
            {
                sb.Append(",\"shortcut\":\"");
                sb.Append(EscapeJsonString(shortcut));
                sb.Append('"');
            }
            sb.Append('}');
            validCount++;
        }

        /// <summary>
        /// mousedown on ⋮: skip SendEvent so SceneView picking / DropdownButton
        /// hotControl are not activated before mouseup builds the frontend menu.
        /// </summary>
        private static bool TryBeginPaneOptionsClick(float mouseX, float mouseY, float viewWidth)
        {
            if (s_FrontendPopupSent || s_OffscreenTarget == null) return false;
            if (!IsPaneOptionsClick(mouseX, mouseY, viewWidth)) return false;

            s_PaneOptionsMouseDownPending = true;
            if (GUIUtility.hotControl != 0)
                GUIUtility.hotControl = 0;

            LogVerbose($"[NWB-PaneOptions] mousedown captured at ({mouseX:F0},{mouseY:F0})");
            return true;
        }

        /// <summary>
        /// Build the DockArea PaneOptions menu without GenericMenu.DropDown,
        /// send popup_show to the browser, cache callbacks for popup_select.
        /// </summary>
        private static bool TryInterceptPaneOptionsClick(float mouseX, float mouseY, float viewWidth, float ppp)
        {
            if (s_FrontendPopupSent || s_OffscreenTarget == null) return false;

            bool pendingUp = s_PaneOptionsMouseDownPending;
            if (!pendingUp && !IsPaneOptionsClick(mouseX, mouseY, viewWidth)) return false;

            EnsurePaneOptionsReflection();
            if (s_GenericMenuItemsField == null || s_GenericMenuItemType == null)
            {
                CodelyLogger.LogWarning("[NWB-PaneOptions] Reflection not ready, cannot intercept");
                return false;
            }

            try
            {
                object dockArea = GetParentView(s_OffscreenTarget);
                if (dockArea == null) return false;

                GenericMenu menu = BuildPaneOptionsGenericMenu(dockArea, s_OffscreenTarget);
                if (menu == null) return false;

                IList rawItems = s_GenericMenuItemsField.GetValue(menu) as IList;
                if (rawItems == null || rawItems.Count == 0) return false;

                float tabStripH = Mathf.Max(s_TabBarOffsetY, kDockTabStripHeight);
                ComputePopupPositionFromLocalClick(
                    mouseX,
                    mouseY,
                    tabStripH + kGenericMenuTopOffset + 2f,
                    2f,
                    out float popX,
                    out float popY,
                    explicitSourceView: dockArea,
                    explicitPpp: ppp);

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "pane_options";
                s_CachedPaneOptionsEntries = new CachedPaneOptionsEntry[rawItems.Count];

                var sb = new StringBuilder(2048);
                sb.Append("{\"type\":\"popup_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"sourceType\":\"pane_options\",\"x\":");
                sb.Append(Mathf.RoundToInt(popX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(popY));
                sb.Append(",\"items\":[");

                int validCount = 0;
                for (int i = 0; i < rawItems.Count; i++)
                {
                    object mi = rawItems[i];
                    if (mi == null) continue;

                    bool isSep = s_MenuItemSeparatorField != null && (bool)s_MenuItemSeparatorField.GetValue(mi);
                    bool isOn = s_MenuItemOnField != null && (bool)s_MenuItemOnField.GetValue(mi);
                    object content = s_MenuItemContentField?.GetValue(mi);
                    string text = "";
                    string tooltip = "";
                    if (content is GUIContent gc)
                    {
                        text = gc.text ?? "";
                        tooltip = gc.tooltip ?? "";
                    }

                    Delegate func = s_MenuItemFuncField?.GetValue(mi) as Delegate;
                    Delegate func2 = s_MenuItemFunc2Field?.GetValue(mi) as Delegate;
                    object userData = s_MenuItemUserDataField?.GetValue(mi);
                    bool enabled = func != null || func2 != null;

                    s_CachedPaneOptionsEntries[i] = new CachedPaneOptionsEntry
                    {
                        callback = func2 ?? func,
                        userData = userData,
                        enabled = enabled
                    };

                    AppendPaneOptionsMenuItemJson(sb, i, text, tooltip, enabled, isOn, isSep, ref validCount);
                }

                sb.Append("]}");

                if (validCount == 0) return false;

                if (GUIUtility.hotControl != 0)
                    GUIUtility.hotControl = 0;

                CancelNativeDelayedPopup(forceRemove: true);
                TryDismissWin32TrackPopupMenu();
                s_PendingNativePopupCloseFrames = Math.Max(s_PendingNativePopupCloseFrames, 60);
                s_SuppressTargetRepaintWhilePopupOpen = true;

                bool sent = SendDataChannelMessage(sb.ToString());
                s_FrontendPopupSent = sent;
                if (sent)
                {
                    s_PaneOptionsMouseDownPending = false;
                    CodelyLogger.Log($"[NWB-PaneOptions] Sent frontend menu: id={s_CurrentFrontendPopupId} items={validCount} target={s_OffscreenTargetType?.Name}");
                }
                else
                {
                    CodelyLogger.LogWarning("[NWB-PaneOptions] Failed to send popup_show via DataChannel");
                }

                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-PaneOptions] Intercept failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Right-click on tab header: build and send the same DockArea menu
        /// (Close Tab, Lock, Maximize, etc.) used by the ⋮ pane-options button.
        /// Called from TrySynthesizeAndForwardContextMenuPopup when the click
        /// falls in the tab strip region (y &lt;= s_TabBarOffsetY).
        /// </summary>
        private static bool TrySendTabHeaderContextMenu(float clickX, float clickY)
        {
            EnsurePaneOptionsReflection();
            if (s_OffscreenTarget == null || s_GenericMenuItemsField == null) return false;

            try
            {
                object dockArea = GetParentView(s_OffscreenTarget);
                if (dockArea == null) return false;

                GenericMenu menu = BuildPaneOptionsGenericMenu(dockArea, s_OffscreenTarget);
                if (menu == null) return false;

                IList rawItems = s_GenericMenuItemsField.GetValue(menu) as IList;
                if (rawItems == null || rawItems.Count == 0) return false;

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "pane_options";
                s_CachedPaneOptionsEntries = new CachedPaneOptionsEntry[rawItems.Count];

                var sb = new StringBuilder(2048);
                sb.Append("{\"type\":\"popup_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"sourceType\":\"pane_options\",\"x\":");
                float ppp = EditorGUIUtility.pixelsPerPoint;
                ComputePopupPositionFromLocalClick(
                    clickX,
                    clickY,
                    clickY + 2f,
                    2f,
                    out float popX,
                    out float popY,
                    explicitSourceView: dockArea,
                    explicitPpp: ppp);

                sb.Append(Mathf.RoundToInt(popX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(popY));
                sb.Append(",\"items\":[");

                int validCount = 0;
                for (int i = 0; i < rawItems.Count; i++)
                {
                    object mi = rawItems[i];
                    if (mi == null) continue;

                    bool isSep = s_MenuItemSeparatorField != null && (bool)s_MenuItemSeparatorField.GetValue(mi);
                    bool isOn = s_MenuItemOnField != null && (bool)s_MenuItemOnField.GetValue(mi);
                    object content = s_MenuItemContentField?.GetValue(mi);
                    string text = "";
                    string tooltip = "";
                    if (content is GUIContent gc)
                    {
                        text = gc.text ?? "";
                        tooltip = gc.tooltip ?? "";
                    }

                    Delegate func = s_MenuItemFuncField?.GetValue(mi) as Delegate;
                    Delegate func2 = s_MenuItemFunc2Field?.GetValue(mi) as Delegate;
                    object userData = s_MenuItemUserDataField?.GetValue(mi);
                    bool enabled = func != null || func2 != null;

                    s_CachedPaneOptionsEntries[i] = new CachedPaneOptionsEntry
                    {
                        callback = func2 ?? func,
                        userData = userData,
                        enabled = enabled
                    };

                    AppendPaneOptionsMenuItemJson(sb, i, text, tooltip, enabled, isOn, isSep, ref validCount);
                }

                sb.Append("]}");
                if (validCount == 0) return false;

                if (GUIUtility.hotControl != 0)
                    GUIUtility.hotControl = 0;

                CancelNativeDelayedPopup(forceRemove: true);
                TryDismissWin32TrackPopupMenu();
                s_PendingNativePopupCloseFrames = Math.Max(s_PendingNativePopupCloseFrames, 60);
                s_SuppressTargetRepaintWhilePopupOpen = true;

                bool sent = SendDataChannelMessage(sb.ToString());
                s_FrontendPopupSent = sent;
                if (sent)
                    CodelyLogger.Log($"[NWB-PaneOptions] Sent tab-header context menu: id={s_CurrentFrontendPopupId} items={validCount} target={s_OffscreenTargetType?.Name}");
                else
                    CodelyLogger.LogWarning("[NWB-PaneOptions] Failed to send tab-header context menu");

                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-PaneOptions] Tab header context menu failed: {ex.Message}");
                return false;
            }
        }

        private static GenericMenu BuildPaneOptionsGenericMenu(object dockArea, EditorWindow view)
        {
            var menu = new GenericMenu();

            if (view is IHasCustomMenu customMenu)
                customMenu.AddItemsToMenu(menu);

            if (s_HostViewAddDefaultItemsToMenu != null && dockArea != null)
                s_HostViewAddDefaultItemsToMenu.Invoke(dockArea, new object[] { menu, view });

            if (s_HostViewAddWindowActionMenu != null)
                s_HostViewAddWindowActionMenu.Invoke(null, new object[] { menu, view });

            return menu;
        }

        private static void HandlePaneOptionsPopupSelect(int index)
        {
            if (s_CachedPaneOptionsEntries == null ||
                index < 0 || index >= s_CachedPaneOptionsEntries.Length)
            {
                CodelyLogger.LogWarning($"[NWB-PaneOptions] Invalid select index={index}");
                return;
            }

            CachedPaneOptionsEntry entry = s_CachedPaneOptionsEntries[index];
            if (!entry.enabled || entry.callback == null)
            {
                LogVerbose($"[NWB-PaneOptions] Select ignored: index={index} enabled={entry.enabled}");
                return;
            }

            try
            {
                if (entry.callback is GenericMenu.MenuFunction2 mf2)
                    mf2(entry.userData);
                else if (entry.callback is GenericMenu.MenuFunction mf)
                    mf();

                LogVerbose($"[NWB-PaneOptions] Invoked callback for index={index}");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-PaneOptions] Callback error index={index}: {ex.Message}");
            }

            s_CachedPaneOptionsEntries = null;
        }
    }
}
