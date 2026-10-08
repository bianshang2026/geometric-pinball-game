using System;
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
        private const float kIconSelectorPopupWidth = 140f;
        private const float kIconSelectorPopupHeightWithLabels = 126f;
        private const float kIconSelectorPopupHeightNoLabels = 86f;
        private const int kIconSelectorLabelCount = 8;
        private const int kIconSelectorDotCount = 16;

        private const string kIconSelectorPathNone = "__none__";

        private static bool s_IconSelectorReflectionDone;
        private static Type s_IconSelectorType;
        private static FieldInfo s_IconSelectorTargetObjectField;
        private static FieldInfo s_IconSelectorTargetObjectListField;
        private static FieldInfo s_IconSelectorShowLabelIconsField;
        private static FieldInfo s_IconSelectorMultipleSelectedIconsField;
        private static MethodInfo s_IconSelectorCopyIconToImporterMethod;

        private static EditorWindow s_CachedIconSelectorWindow;
        private static UnityEngine.Object[] s_IconSelectorTargetObjects;
        private static bool s_IconSelectorSessionActive;
        private static bool s_IconSelectorShowLabelIcons;
        private static string s_IconSelectorSelectedPath = "";

        private static void EnsureIconSelectorReflection()
        {
            if (s_IconSelectorReflectionDone) return;
            s_IconSelectorReflectionDone = true;

            try
            {
                var editorAsm = typeof(EditorWindow).Assembly;
                s_IconSelectorType = editorAsm.GetType("UnityEditor.IconSelector");
                if (s_IconSelectorType == null)
                {
                    CodelyLogger.LogWarning("[NWB-IconSelector] IconSelector type not found");
                    return;
                }

                const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                const BindingFlags stat = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

                s_IconSelectorTargetObjectField = s_IconSelectorType.GetField("m_TargetObject", inst);
                s_IconSelectorTargetObjectListField = s_IconSelectorType.GetField("m_TargetObjectList", inst);
                s_IconSelectorShowLabelIconsField = s_IconSelectorType.GetField("m_ShowLabelIcons", inst);
                s_IconSelectorMultipleSelectedIconsField = s_IconSelectorType.GetField("m_MultipleSelectedIcons", inst);
                s_IconSelectorCopyIconToImporterMethod = s_IconSelectorType.GetMethod(
                    "CopyIconToImporter", stat, null, new[] { typeof(MonoScript) }, null);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-IconSelector] Reflection init failed: {ex.Message}");
            }
        }

        private static bool TryExtractIconSelectorPopup(
            EditorWindow hostedWindow, float relX, float relY, float popW, float popH)
        {
            EnsureIconSelectorReflection();
            if (hostedWindow == null || s_IconSelectorType == null)
                return false;

            try
            {
                s_CachedIconSelectorWindow = hostedWindow;
                s_IconSelectorTargetObjects = s_IconSelectorTargetObjectListField?.GetValue(hostedWindow) as UnityEngine.Object[];
                if (s_IconSelectorTargetObjects == null || s_IconSelectorTargetObjects.Length == 0)
                {
                    UnityEngine.Object single =
                        s_IconSelectorTargetObjectField?.GetValue(hostedWindow) as UnityEngine.Object;
                    if (single != null)
                        s_IconSelectorTargetObjects = new[] { single };
                }

                if (s_IconSelectorTargetObjects == null || s_IconSelectorTargetObjects.Length == 0)
                {
                    LogVerbose("[NWB-IconSelector] No target objects on IconSelector");
                    return false;
                }

                s_IconSelectorShowLabelIcons = s_IconSelectorShowLabelIconsField != null &&
                                               (bool)s_IconSelectorShowLabelIconsField.GetValue(hostedWindow);
                bool multiple = s_IconSelectorMultipleSelectedIconsField != null &&
                                (bool)s_IconSelectorMultipleSelectedIconsField.GetValue(hostedWindow);
                s_IconSelectorSelectedPath = multiple ? "" : ResolveIconSelectorSelectedPath(s_IconSelectorTargetObjects[0]);

                TryHideIconSelectorNativeWindow(hostedWindow);

                s_LastFrontendPopupX = relX;
                s_LastFrontendPopupY = relY;

                float height = s_IconSelectorShowLabelIcons
                    ? kIconSelectorPopupHeightWithLabels
                    : kIconSelectorPopupHeightNoLabels;
                float width = Mathf.Max(popW, kIconSelectorPopupWidth);
                height = Mathf.Max(popH, height);

                return TrySendIconSelectorPopup(relX, relY, width, height);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-IconSelector] Extract error: {ex.Message}");
                return false;
            }
        }

        private static void TryHideIconSelectorNativeWindow(EditorWindow window)
        {
            if (window == null) return;
            try
            {
                Rect pos = window.position;
                pos.x = -20000f;
                pos.y = -20000f;
                window.position = pos;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-IconSelector] Hide native window failed: {ex.Message}");
            }
        }

        private static bool TrySendIconSelectorPopup(float relX, float relY, float popW, float popH)
        {
            try
            {
                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "icon_selector";
                s_IconSelectorSessionActive = true;

                var sb = new StringBuilder(8192);
                sb.Append("{\"type\":\"popup_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"sourceType\":\"icon_selector\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                sb.Append(",\"width\":");
                sb.Append(Mathf.RoundToInt(popW));
                sb.Append(",\"height\":");
                sb.Append(Mathf.RoundToInt(popH));
                sb.Append(",\"title\":\"Select Icon\",\"showLabelIcons\":");
                sb.Append(s_IconSelectorShowLabelIcons ? "true" : "false");
                sb.Append(",\"selectedPath\":\"");
                sb.Append(EscapeJsonString(s_IconSelectorSelectedPath ?? ""));
                sb.Append("\"");

                if (s_IconSelectorShowLabelIcons)
                {
                    sb.Append(",\"labelIcons\":[");
                    AppendIconSelectorIconArray(sb, "label", kIconSelectorLabelCount, true);
                    sb.Append(']');
                }

                sb.Append(",\"dotIcons\":[");
                AppendIconSelectorIconArray(sb, "dot", kIconSelectorDotCount, false);
                sb.Append("]}");

                bool sent = SendDataChannelMessage(sb.ToString());
                if (sent)
                {
                    s_FrontendPopupSent = true;
                    LogVerbose("[NWB-IconSelector] Sent icon selector to frontend");
                }
                else
                {
                    ClearIconSelectorPopupState();
                }

                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-IconSelector] Send popup failed: {ex.Message}");
                ClearIconSelectorPopupState();
                return false;
            }
        }

        private static void AppendIconSelectorIconArray(StringBuilder sb, string kind, int count, bool labelIcon)
        {
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(',');
                string iconName = labelIcon
                    ? "sv_icon_name" + i
                    : "sv_icon_dot" + i + "_sml";
                string image = EncodeEditorIconDataUri(iconName);
                sb.Append("{\"path\":\"");
                sb.Append(kind);
                sb.Append(':');
                sb.Append(i);
                sb.Append("\",\"image\":\"");
                sb.Append(EscapeJsonString(image));
                sb.Append("\"}");
            }
        }

        private static string EncodeEditorIconDataUri(string iconName)
        {
            if (string.IsNullOrEmpty(iconName))
                return "";
            try
            {
                GUIContent content = EditorGUIUtility.IconContent(iconName);
                return TryEncodeAnnotationIconPngDataUri(content?.image as Texture2D);
            }
            catch
            {
                return "";
            }
        }

        private static MethodInfo s_GetIconForObjectMethod;
        private static MethodInfo s_SetIconForObjectMethod;

        private static Texture2D GetIconForObjectReflect(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            try
            {
                if (s_GetIconForObjectMethod == null)
                {
                    s_GetIconForObjectMethod = typeof(EditorGUIUtility).GetMethod(
                        "GetIconForObject",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        null,
                        new[] { typeof(UnityEngine.Object) },
                        null);
                }

                return s_GetIconForObjectMethod?.Invoke(null, new object[] { obj }) as Texture2D;
            }
            catch
            {
                return null;
            }
        }

        private static void SetIconForObjectReflect(UnityEngine.Object obj, Texture2D icon)
        {
            if (obj == null) return;
            try
            {
                if (s_SetIconForObjectMethod == null)
                {
                    s_SetIconForObjectMethod = typeof(EditorGUIUtility).GetMethod(
                        "SetIconForObject",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        null,
                        new[] { typeof(UnityEngine.Object), typeof(Texture2D) },
                        null);
                }

                s_SetIconForObjectMethod?.Invoke(null, new object[] { obj, icon });
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-IconSelector] SetIconForObject failed: {ex.Message}");
            }
        }

        private static string ResolveIconSelectorSelectedPath(UnityEngine.Object target)
        {
            Texture2D icon = GetIconForObjectReflect(target);
            if (icon == null)
                return kIconSelectorPathNone;

            if (s_IconSelectorShowLabelIcons)
            {
                for (int i = 0; i < kIconSelectorLabelCount; i++)
                {
                    if (IconMatchesName(icon, "sv_icon_name" + i) || IconMatchesName(icon, "sv_label_" + i))
                        return "label:" + i;
                }
            }

            for (int i = 0; i < kIconSelectorDotCount; i++)
            {
                if (IconMatchesName(icon, "sv_icon_dot" + i + "_sml") ||
                    IconMatchesName(icon, "sv_icon_dot" + i + "_pix16_gizmo"))
                {
                    return "dot:" + i;
                }
            }

            return "";
        }

        private static bool IconMatchesName(Texture2D icon, string iconName)
        {
            if (icon == null || string.IsNullOrEmpty(iconName))
                return false;
            GUIContent content = EditorGUIUtility.IconContent(iconName);
            return content?.image != null && ReferenceEquals(content.image, icon);
        }

        private static Texture2D ResolveLargeIconTexture(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            if (path == kIconSelectorPathNone)
                return null;

            if (path.StartsWith("label:", StringComparison.Ordinal))
            {
                if (int.TryParse(path.Substring("label:".Length), out int index))
                {
                    GUIContent content = EditorGUIUtility.IconContent("sv_label_" + index);
                    return content?.image as Texture2D;
                }
            }

            if (path.StartsWith("dot:", StringComparison.Ordinal))
            {
                if (int.TryParse(path.Substring("dot:".Length), out int index))
                {
                    GUIContent content = EditorGUIUtility.IconContent("sv_icon_dot" + index + "_pix16_gizmo");
                    return content?.image as Texture2D;
                }
            }

            return null;
        }

        private static void ApplyIconToIconSelectorTargets(Texture2D icon)
        {
            UnityEngine.Object[] targets = s_IconSelectorTargetObjects;
            if (targets == null || targets.Length == 0)
                return;

            Undo.RecordObjects(targets, "Set Icon On GameObject");
            foreach (UnityEngine.Object target in targets)
            {
                if (target == null) continue;
                SetIconForObjectReflect(target, icon);

                if (target is MonoScript monoScript && icon != null)
                    s_IconSelectorCopyIconToImporterMethod?.Invoke(null, new object[] { monoScript });
            }

            TryForceReloadInspectors();
            TryInvokeAnnotationWindowIconChanged();
        }

        private static void TryForceReloadInspectors()
        {
            try
            {
                MethodInfo method = typeof(EditorUtility).GetMethod(
                    "ForceReloadInspectors",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                method?.Invoke(null, null);
            }
            catch
            {
                // internal API; fall back to repaint-only below
            }

            InternalEditorUtility.RepaintAllViews();
        }

        private static void TryInvokeAnnotationWindowIconChanged()
        {
            try
            {
                Type annotationWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.AnnotationWindow");
                MethodInfo iconChanged = annotationWindowType?.GetMethod(
                    "IconChanged",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                iconChanged?.Invoke(null, null);
            }
            catch
            {
                // optional
            }
        }

        /// <summary>
        /// Returns true when the icon selector popup should stay open.
        /// </summary>
        private static bool TryHandleIconSelectorPopupSelect(string path)
        {
            if (!s_IconSelectorSessionActive)
                return false;

            if (path == kIconSelectorPathNone)
            {
                ApplyIconToIconSelectorTargets(null);
                s_IconSelectorSelectedPath = kIconSelectorPathNone;
                return true;
            }

            Texture2D icon = ResolveLargeIconTexture(path);
            if (icon == null && path != kIconSelectorPathNone)
            {
                CodelyLogger.LogWarning($"[NWB-IconSelector] Unknown icon path: {path}");
                return true;
            }

            ApplyIconToIconSelectorTargets(icon);
            s_IconSelectorSelectedPath = path;
            return true;
        }

        private static void CloseNativeIconSelectorWindow()
        {
            if (s_CachedIconSelectorWindow == null)
                return;

            try
            {
                s_CachedIconSelectorWindow.Close();
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-IconSelector] Close failed: {ex.Message}");
            }
        }

        private static void CancelIconSelectorSession()
        {
            CloseNativeIconSelectorWindow();
            ClearIconSelectorPopupState();
            if (s_OffscreenTarget != null)
            {
                s_OffscreenTarget.Repaint();
                InternalEditorUtility.RepaintAllViews();
            }
        }

        private static void ClearIconSelectorPopupState()
        {
            s_CachedIconSelectorWindow = null;
            s_IconSelectorTargetObjects = null;
            s_IconSelectorSessionActive = false;
            s_IconSelectorShowLabelIcons = false;
            s_IconSelectorSelectedPath = "";
        }
    }
}
