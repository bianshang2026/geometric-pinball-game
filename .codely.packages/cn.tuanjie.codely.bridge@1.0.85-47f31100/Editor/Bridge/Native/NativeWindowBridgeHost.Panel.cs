using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

using UnityTcp.Editor.Helpers;
namespace UnityTcp.Editor.Native
{
    // Panel protocol: extract complex UI panels (Grid Settings, Snap Increment, Draw Mode)
    // and render them interactively in the browser frontend.
    internal static partial class NativeWindowBridgeHost
    {
        // =================== Grid Settings Panel ===================
        // Reflection cache for SceneView.sceneViewGrids (SceneViewGrid).
        private static bool s_GridReflectionDone;
        private static PropertyInfo s_SceneViewGridsProp;
        private static PropertyInfo s_GridAxisProp;
        private static PropertyInfo s_GridOpacityProp;
        private static MethodInfo s_SetAllGridsPivotMethod;
        private static MethodInfo s_ResetPivotMethod;
        private static PropertyInfo s_GridSettingsSizeProp;
        private static System.Type s_GridRenderAxisType;

        private static void EnsureGridReflection()
        {
            if (s_GridReflectionDone) return;
            s_GridReflectionDone = true;
            try
            {
                var asm = typeof(EditorWindow).Assembly;
                s_SceneViewGridsProp = typeof(SceneView).GetProperty("sceneViewGrids",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                System.Type svgType = asm.GetType("UnityEditor.SceneViewGrid");
                if (svgType != null)
                {
                    s_GridAxisProp = svgType.GetProperty("gridAxis",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    s_GridOpacityProp = svgType.GetProperty("gridOpacity",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    s_SetAllGridsPivotMethod = svgType.GetMethod("SetAllGridsPivot",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                        null, new System.Type[] { typeof(Vector3) }, null);
                    s_ResetPivotMethod = svgType.GetMethod("ResetPivot",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    s_GridRenderAxisType = svgType.GetNestedType("GridRenderAxis",
                        BindingFlags.NonPublic | BindingFlags.Public);
                }

                System.Type gsType = asm.GetType("UnityEditor.GridSettings");
                if (gsType != null)
                    s_GridSettingsSizeProp = gsType.GetProperty("size",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                CodelyLogger.Log($"[NWB-GridPanel] Reflection OK: sceneViewGrids={s_SceneViewGridsProp != null}" +
                    $" gridAxis={s_GridAxisProp != null} gridOpacity={s_GridOpacityProp != null}" +
                    $" SetAllGridsPivot={s_SetAllGridsPivotMethod != null} ResetPivot={s_ResetPivotMethod != null}" +
                    $" GridSettings.size={s_GridSettingsSizeProp != null} GridRenderAxis={s_GridRenderAxisType != null}");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-GridPanel] Reflection init error: {ex.Message}");
            }
        }

        /// <summary>
        /// Extract Grid Settings panel data from the active SceneView and send
        /// a panel_show message to the browser frontend.
        /// </summary>
        private static bool TryExtractGridSettingsPanel(float relX, float relY)
        {
            EnsureGridReflection();
            if (s_SceneViewGridsProp == null || s_GridAxisProp == null || s_GridOpacityProp == null)
            {
                CodelyLogger.LogWarning("[NWB-GridPanel] Required reflection targets not available");
                return false;
            }

            try
            {
                SceneView sv = s_OffscreenTarget as SceneView;
                if (sv == null) sv = SceneView.lastActiveSceneView;
                if (sv == null)
                {
                    CodelyLogger.LogWarning("[NWB-GridPanel] No active SceneView found");
                    return false;
                }

                object grids = s_SceneViewGridsProp.GetValue(sv);
                if (grids == null)
                {
                    CodelyLogger.LogWarning("[NWB-GridPanel] sceneViewGrids is null");
                    return false;
                }

                object axisObj = s_GridAxisProp.GetValue(grids);
                int axisIndex = (int)axisObj;
                float opacity = (float)s_GridOpacityProp.GetValue(grids);

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "panel_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "grid_settings";

                var sb = new StringBuilder(1024);
                sb.Append("{\"type\":\"panel_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                AppendToolbarAnchorRectJson(sb);
                sb.Append(",\"title\":\"Grid Visual\",\"sourceType\":\"grid_settings\",\"controls\":[");

                sb.Append("{\"id\":\"gridPlane\",\"type\":\"button_strip\",\"label\":\"Grid Plane\",\"tooltip\":\"Choose which axis-aligned plane is visible in Scene view.\",");
                sb.Append("\"options\":[\"X\",\"Y\",\"Z\"],\"value\":");
                sb.Append(axisIndex);
                sb.Append("},");

                sb.Append("{\"id\":\"gridOpacity\",\"type\":\"slider\",\"label\":\"Opacity\",\"tooltip\":\"Adjust Scene grid opacity.\",");
                sb.Append("\"min\":0,\"max\":1,\"step\":0.01,\"value\":");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}", opacity);
                sb.Append("},");

                sb.Append("{\"id\":\"moveTo\",\"type\":\"button_row\",\"label\":\"Move To\",\"tooltip\":\"Move the Scene grid pivot.\",");
                sb.Append("\"buttons\":[");
                sb.Append("{\"id\":\"toHandle\",\"label\":\"To Handle\"},");
                sb.Append("{\"id\":\"toOrigin\",\"label\":\"To Origin\"}");
                sb.Append("]}");

                sb.Append("]}");

                string json = sb.ToString();
                bool sent = SendDataChannelMessage(json);
                s_FrontendPopupSent = sent;

                if (sent)
                    CodelyLogger.Log($"[NWB-GridPanel] Sent grid panel: id={s_CurrentFrontendPopupId} axis={axisIndex} opacity={opacity:F2}");
                else
                    CodelyLogger.LogWarning("[NWB-GridPanel] Failed to send grid panel via DataChannel");

                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-GridPanel] Error extracting grid settings: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static void HandleGridPanelChange(string json, string controlId)
        {
            float value = ExtractJsonFloat(json, "value");

            SceneView sv = s_OffscreenTarget as SceneView;
            if (sv == null) sv = SceneView.lastActiveSceneView;
            if (sv == null) return;

            object grids = s_SceneViewGridsProp?.GetValue(sv);
            if (grids == null) return;

            switch (controlId)
            {
                case "gridPlane":
                    if (s_GridAxisProp != null && s_GridRenderAxisType != null)
                    {
                        object enumVal = System.Enum.ToObject(s_GridRenderAxisType, (int)value);
                        s_GridAxisProp.SetValue(grids, enumVal);
                        CodelyLogger.Log($"[NWB-GridPanel] Set gridAxis to {enumVal}");
                    }
                    break;

                case "gridOpacity":
                    if (s_GridOpacityProp != null)
                    {
                        s_GridOpacityProp.SetValue(grids, value);
                        CodelyLogger.Log($"[NWB-GridPanel] Set gridOpacity to {value:F2}");
                    }
                    break;

                case "toHandle":
                    if (s_SetAllGridsPivotMethod != null)
                    {
                        Vector3 handlePos = UnityEditor.Tools.handlePosition;
                        Vector3 gridSize = Vector3.one;
                        if (s_GridSettingsSizeProp != null)
                            gridSize = (Vector3)s_GridSettingsSizeProp.GetValue(null);
                        Vector3 snapped = Snapping.Snap(handlePos, gridSize);
                        s_SetAllGridsPivotMethod.Invoke(grids, new object[] { snapped });
                        CodelyLogger.Log($"[NWB-GridPanel] SetAllGridsPivot({snapped})");
                    }
                    break;

                case "toOrigin":
                    if (s_ResetPivotMethod != null && s_GridRenderAxisType != null)
                    {
                        object allVal = System.Enum.ToObject(s_GridRenderAxisType, 3);
                        s_ResetPivotMethod.Invoke(grids, new object[] { allVal });
                        CodelyLogger.Log("[NWB-GridPanel] ResetPivot(All)");
                    }
                    break;

                default:
                    CodelyLogger.LogWarning($"[NWB-GridPanel] Unknown controlId: {controlId}");
                    break;
            }

            SceneView.RepaintAll();
        }

        // =================== Snap Increment Panel ===================

        /// <summary>
        /// Extract Snap Increment settings and send a panel_show message.
        /// Uses public EditorSnapSettings API — no reflection needed.
        /// </summary>
        private static bool TryExtractSnapIncrementPanel(float relX, float relY)
        {
            try
            {
                Vector3 moveVal = EditorSnapSettings.move;
                float rotateVal = EditorSnapSettings.rotate;
                float scaleVal = EditorSnapSettings.scale;

                bool linked = Mathf.Approximately(moveVal.x, moveVal.y) &&
                              Mathf.Approximately(moveVal.y, moveVal.z);

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "panel_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "snap_increment";

                var sb = new StringBuilder(512);
                sb.Append("{\"type\":\"panel_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                AppendToolbarAnchorRectJson(sb);
                sb.Append(",\"title\":\"Increment Snapping\",\"sourceType\":\"snap_increment\",\"controls\":[");

                sb.Append("{\"id\":\"snapMove\",\"type\":\"linked_vector3\",\"label\":\"Move\",\"tooltip\":\"Set move snap increments for X/Y/Z.\",");
                sb.Append("\"linked\":");
                sb.Append(linked ? "true" : "false");
                sb.Append(",\"value\":[");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    "{0},{1},{2}", moveVal.x, moveVal.y, moveVal.z);
                sb.Append("]},");

                sb.Append("{\"id\":\"snapRotate\",\"type\":\"float_field\",\"label\":\"Rotate\",\"tooltip\":\"Set rotate snap increment in degrees.\",\"value\":");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0}", rotateVal);
                sb.Append(",\"step\":1},");

                sb.Append("{\"id\":\"snapScale\",\"type\":\"float_field\",\"label\":\"Scale\",\"tooltip\":\"Set scale snap increment.\",\"value\":");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0}", scaleVal);
                sb.Append(",\"step\":0.01}");

                sb.Append("]}");

                string json = sb.ToString();
                bool sent = SendDataChannelMessage(json);
                s_FrontendPopupSent = sent;

                if (sent)
                    CodelyLogger.Log($"[NWB-SnapPanel] Sent snap increment panel: id={s_CurrentFrontendPopupId} move=({moveVal.x},{moveVal.y},{moveVal.z}) rotate={rotateVal} scale={scaleVal}");
                else
                    CodelyLogger.LogWarning("[NWB-SnapPanel] Failed to send snap increment panel via DataChannel");

                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-SnapPanel] Error extracting snap settings: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static void HandleSnapPanelChange(string json, string controlId)
        {
            switch (controlId)
            {
                case "snapMove":
                {
                    string valStr = ExtractJsonString(json, "value");
                    if (string.IsNullOrEmpty(valStr))
                    {
                        float x = ExtractJsonArrayFloat(json, "value", 0);
                        float y = ExtractJsonArrayFloat(json, "value", 1);
                        float z = ExtractJsonArrayFloat(json, "value", 2);
                        EditorSnapSettings.move = new Vector3(x, y, z);
                        CodelyLogger.Log($"[NWB-SnapPanel] Set move to ({x},{y},{z})");
                    }
                    break;
                }

                case "snapMove_link":
                    break;

                case "snapRotate":
                {
                    float value = ExtractJsonFloat(json, "value");
                    EditorSnapSettings.rotate = value;
                    CodelyLogger.Log($"[NWB-SnapPanel] Set rotate to {value}");
                    break;
                }

                case "snapScale":
                {
                    float value = ExtractJsonFloat(json, "value");
                    EditorSnapSettings.scale = value;
                    CodelyLogger.Log($"[NWB-SnapPanel] Set scale to {value}");
                    break;
                }

                default:
                    CodelyLogger.LogWarning($"[NWB-SnapPanel] Unknown controlId: {controlId}");
                    break;
            }

            if (s_OffscreenTarget != null) s_OffscreenTarget.Repaint();
        }

        // =================== Snap Settings (Grid Snapping) Panel ===================

        // Reflection cache: EditorSnapSettings.gridSize exists in Tuanjie engine
        // but not in standard Unity. Fall back to EditorSnapSettings.move.
        private static bool s_SnapGridSizeReflectionDone;
        private static PropertyInfo s_SnapGridSizeProp;

        private static Vector3 GetSnapGridSize()
        {
            if (!s_SnapGridSizeReflectionDone)
            {
                s_SnapGridSizeReflectionDone = true;
                s_SnapGridSizeProp = typeof(EditorSnapSettings).GetProperty("gridSize",
                    BindingFlags.Public | BindingFlags.Static);
            }
            if (s_SnapGridSizeProp != null)
                return (Vector3)s_SnapGridSizeProp.GetValue(null);
            return EditorSnapSettings.move;
        }

        private static void SetSnapGridSize(Vector3 value)
        {
            if (!s_SnapGridSizeReflectionDone)
            {
                s_SnapGridSizeReflectionDone = true;
                s_SnapGridSizeProp = typeof(EditorSnapSettings).GetProperty("gridSize",
                    BindingFlags.Public | BindingFlags.Static);
            }
            if (s_SnapGridSizeProp != null && s_SnapGridSizeProp.CanWrite)
            {
                s_SnapGridSizeProp.SetValue(null, value);
                return;
            }
            EditorSnapSettings.move = value;
        }

        /// <summary>
        /// Extract Grid Snapping settings and send a panel_show message.
        /// Uses EditorSnapSettings.gridSize (Tuanjie) or .move (standard Unity).
        /// </summary>
        private static bool TryExtractSnapSettingsPanel(float relX, float relY)
        {
            try
            {
                Vector3 gridSize = GetSnapGridSize();

                // Check if axes are linked.
                bool linked = Mathf.Approximately(gridSize.x, gridSize.y) &&
                              Mathf.Approximately(gridSize.y, gridSize.z);

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "panel_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "snap_settings";

                var sb = new StringBuilder(512);
                sb.Append("{\"type\":\"panel_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                AppendToolbarAnchorRectJson(sb);
                sb.Append(",\"title\":\"Grid Snapping\",\"sourceType\":\"snap_settings\",\"controls\":[");

                // Grid Size (linked X/Y/Z)
                sb.Append("{\"id\":\"gridSize\",\"type\":\"linked_vector3\",\"label\":\"Grid Size\",\"tooltip\":\"Set grid snapping size for X/Y/Z.\",");
                sb.Append("\"linked\":");
                sb.Append(linked ? "true" : "false");
                sb.Append(",\"value\":[");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    "{0},{1},{2}", gridSize.x, gridSize.y, gridSize.z);
                sb.Append("]},");

                // Align Selected (button row)
                sb.Append("{\"id\":\"alignSelected\",\"type\":\"button_row\",\"label\":\"Align Selected\",\"tooltip\":\"Snap selected objects to the grid.\",");
                sb.Append("\"buttons\":[");
                sb.Append("{\"id\":\"alignAll\",\"label\":\"All Axes\"},");
                sb.Append("{\"id\":\"alignX\",\"label\":\"X\"},");
                sb.Append("{\"id\":\"alignY\",\"label\":\"Y\"},");
                sb.Append("{\"id\":\"alignZ\",\"label\":\"Z\"}");
                sb.Append("]}");

                sb.Append("]}");

                string json = sb.ToString();
                bool sent = SendDataChannelMessage(json);
                s_FrontendPopupSent = sent;

                if (sent)
                    CodelyLogger.Log($"[NWB-SnapSettingsPanel] Sent grid snapping panel: id={s_CurrentFrontendPopupId} gridSize=({gridSize.x},{gridSize.y},{gridSize.z})");
                else
                    CodelyLogger.LogWarning("[NWB-SnapSettingsPanel] Failed to send grid snapping panel via DataChannel");

                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-SnapSettingsPanel] Error extracting snap settings: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static void HandleSnapSettingsPanelChange(string json, string controlId)
        {
            switch (controlId)
            {
                case "gridSize":
                {
                    float x = ExtractJsonArrayFloat(json, "value", 0);
                    float y = ExtractJsonArrayFloat(json, "value", 1);
                    float z = ExtractJsonArrayFloat(json, "value", 2);
                    SetSnapGridSize(new Vector3(x, y, z));
                    CodelyLogger.Log($"[NWB-SnapSettingsPanel] Set gridSize to ({x},{y},{z})");
                    break;
                }

                case "gridSize_link":
                    break;

                case "alignAll":
                case "alignX":
                case "alignY":
                case "alignZ":
                {
                    var selections = Selection.transforms;
                    if (selections != null && selections.Length > 0)
                    {
                        Undo.RecordObjects(selections, "Snap to Grid");
                        SnapAxis axis = SnapAxis.All;
                        if (controlId == "alignX") axis = SnapAxis.X;
                        else if (controlId == "alignY") axis = SnapAxis.Y;
                        else if (controlId == "alignZ") axis = SnapAxis.Z;
                        Handles.SnapToGrid(selections, axis);
                        CodelyLogger.Log($"[NWB-SnapSettingsPanel] SnapToGrid axis={axis}");
                    }
                    else
                    {
                        CodelyLogger.Log("[NWB-SnapSettingsPanel] No selection for SnapToGrid");
                    }
                    break;
                }

                default:
                    CodelyLogger.LogWarning($"[NWB-SnapSettingsPanel] Unknown controlId: {controlId}");
                    break;
            }

            if (s_OffscreenTarget != null) s_OffscreenTarget.Repaint();
        }

        private static void HandleSceneViewCameraPanelChange(string json, string controlId)
        {
            SceneView sv = s_OffscreenTarget as SceneView;
            if (sv == null) sv = SceneView.lastActiveSceneView;
            if (sv == null) return;

            object settings = sv.cameraSettings;
            System.Type settingsType = settings.GetType();

            bool SetMember(string name, object memberValue)
            {
                FieldInfo f = settingsType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    try { f.SetValue(settings, memberValue); return true; } catch { }
                }
                PropertyInfo p = settingsType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite)
                {
                    try { p.SetValue(settings, memberValue); return true; } catch { }
                }
                return false;
            }

            float value = ExtractJsonFloat(json, "value");
            bool boolValue = ExtractJsonBool(json, "value");
            bool dirty = false;

            switch (controlId)
            {
                case "cameraFov":
                    dirty = SetMember("fieldOfView", Mathf.Clamp(value, 4f, 120f));
                    break;
                case "dynamicClip":
                    dirty = SetMember("dynamicClip", boolValue || value >= 0.5f);
                    break;
                case "nearClip":
                    dirty = SetMember("nearClip", Mathf.Max(0.0001f, value));
                    break;
                case "farClip":
                    dirty = SetMember("farClip", Mathf.Max(0.001f, value));
                    break;
                case "occlusionCulling":
                    dirty = SetMember("occlusionCulling", boolValue || value >= 0.5f);
                    break;
                case "backfaceCulling":
                    dirty = SetMember("backfaceCulling", boolValue || value >= 0.5f);
                    break;
                case "cameraEasing":
                    dirty = SetMember("easingEnabled", boolValue || value >= 0.5f);
                    break;
                case "cameraAccel":
                    dirty = SetMember("accelerationEnabled", boolValue || value >= 0.5f);
                    break;
                case "cameraSpeed":
                    dirty = SetMember("speed", Mathf.Max(0.0001f, value));
                    break;
                case "speedMin":
                case "speedMax":
                {
                    float speedMin = 0.01f;
                    float speedMax = 2f;
                    FieldInfo minFld = settingsType.GetField("speedMin", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo maxFld = settingsType.GetField("speedMax", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (minFld != null && minFld.FieldType == typeof(float)) speedMin = (float)minFld.GetValue(settings);
                    if (maxFld != null && maxFld.FieldType == typeof(float)) speedMax = (float)maxFld.GetValue(settings);
                    if (controlId == "speedMin") speedMin = Mathf.Max(0.0001f, value);
                    else speedMax = Mathf.Max(0.0001f, value);
                    if (speedMax <= speedMin) speedMax = speedMin + 0.001f;

                    MethodInfo setMinMaxMethod = settingsType.GetMethod("SetSpeedMinMax",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new[] { typeof(float), typeof(float) }, null);
                    if (setMinMaxMethod != null)
                    {
                        try
                        {
                            setMinMaxMethod.Invoke(settings, new object[] { speedMin, speedMax });
                            dirty = true;
                        }
                        catch { }
                    }
                    if (!dirty)
                    {
                        bool setMin = SetMember("speedMin", speedMin);
                        bool setMax = SetMember("speedMax", speedMax);
                        dirty = setMin || setMax;
                    }
                    break;
                }
            }

            if (dirty)
            {
                PropertyInfo cameraSettingsProp = typeof(SceneView).GetProperty("cameraSettings",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                cameraSettingsProp?.SetValue(sv, settings);
                sv.Repaint();
            }
        }

        // ExtractJsonArrayFloat moved to NativeWindowBridgeHost.Json.cs

        // =================== Draw Mode Popup ===================

        private static string GetPopupWindowContentTypeName(object containerWindow)
        {
            try
            {
                object wc = GetPopupWindowContentObject(containerWindow);
                return wc?.GetType().Name ?? "";
            }
            catch { return ""; }
        }

        private static object GetPopupWindowContentObject(object containerWindow)
        {
            if (containerWindow == null) return null;
            System.Type cwType = containerWindow.GetType();
            PropertyInfo rootViewProp = cwType.GetProperty("rootView",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            object rootView = rootViewProp?.GetValue(containerWindow);
            if (rootView == null) return null;

            PropertyInfo avProp = rootView.GetType().GetProperty("actualView",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            object popupWindow = avProp?.GetValue(rootView);
            if (popupWindow == null) return null;

            FieldInfo wcField = popupWindow.GetType().GetField("m_WindowContent",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (wcField == null) return null;
            return wcField.GetValue(popupWindow);
        }

        private static bool TryExtractSceneFXPopup(float relX, float relY)
        {
            try
            {
                SceneView sv = s_OffscreenTarget as SceneView;
                if (sv == null) sv = SceneView.lastActiveSceneView;
                if (sv == null) return false;

                object state = sv.sceneViewState;
                if (state == null) return false;
                System.Type stateType = state.GetType();

                bool ReadBool(string name, bool fallback)
                {
                    PropertyInfo p = stateType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.PropertyType == typeof(bool))
                    {
                        try { return (bool)p.GetValue(state); } catch { return fallback; }
                    }
                    FieldInfo f = stateType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null && f.FieldType == typeof(bool))
                    {
                        try { return (bool)f.GetValue(state); } catch { return fallback; }
                    }
                    return fallback;
                }

                var items = new (string label, string key)[]
                {
                    ("Skybox", "showSkybox"),
                    ("Fog", "showFog"),
                    ("Flares", "showFlares"),
                    ("Always Refresh", "alwaysRefresh"),
                    ("Post Processing", "showImageEffects"),
                    ("Particle Systems", "showParticleSystems"),
                    ("Visual Effect Graphs", "showVisualEffectGraphs")
                };
                bool vfxActive = false;
                try
                {
                    System.Type vfxMgrType = Type.GetType("UnityEngine.VFX.VFXManager, UnityEngine.VFXModule");
                    PropertyInfo activateProp = vfxMgrType?.GetProperty("activateVFX", BindingFlags.Static | BindingFlags.Public);
                    if (activateProp != null && activateProp.PropertyType == typeof(bool))
                        vfxActive = (bool)activateProp.GetValue(null);
                }
                catch { }

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "scene_fx";
                s_LastFrontendPopupX = relX;
                s_LastFrontendPopupY = relY;
                s_CachedPopupItemPaths = new string[items.Length];

                var sb = new StringBuilder(2048);
                sb.Append("{\"type\":\"popup_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                sb.Append(",\"sourceType\":\"scene_fx\",\"items\":[");

                bool first = true;
                int validCount = 0;
                for (int i = 0; i < items.Length; i++)
                {
                    if (items[i].key == "showVisualEffectGraphs" && !vfxActive)
                        continue;
                    if (!first) sb.Append(',');
                    bool checkedVal = ReadBool(items[i].key, true);
                    string path = "scenefx|" + items[i].key;
                    s_CachedPopupItemPaths[validCount] = path;
                    sb.Append("{\"index\":");
                    sb.Append(validCount);
                    sb.Append(",\"label\":\"");
                    sb.Append(EscapeJsonString(items[i].label));
                    sb.Append("\",\"tooltip\":\"");
                    sb.Append(EscapeJsonString("Toggle " + items[i].label + " in Scene view"));
                    sb.Append("\",\"path\":\"");
                    sb.Append(path);
                    sb.Append("\",\"enabled\":true,\"checked\":");
                    sb.Append(checkedVal ? "true" : "false");
                    sb.Append(",\"separator\":false,\"keepOpen\":true}");
                    first = false;
                    validCount++;
                }

                sb.Append("]}");
                bool sent = SendDataChannelMessage(sb.ToString());
                s_FrontendPopupSent = sent;
                if (sent) CodelyLogger.Log($"[NWB-SceneFX] Sent popup: id={s_CurrentFrontendPopupId} items={validCount}");
                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-SceneFX] Error extracting popup: {ex.Message}");
                return false;
            }
        }

        private static bool HandleSceneFXToggle(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("scenefx|", StringComparison.Ordinal))
                return false;
            SceneView sv = s_OffscreenTarget as SceneView;
            if (sv == null) sv = SceneView.lastActiveSceneView;
            if (sv == null) return false;

            object state = sv.sceneViewState;
            if (state == null) return false;
            string key = path.Substring("scenefx|".Length);
            System.Type stateType = state.GetType();

            bool current = false;
            bool found = false;
            PropertyInfo p = stateType.GetProperty(key, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(bool) && p.CanRead && p.CanWrite)
            {
                current = (bool)p.GetValue(state);
                p.SetValue(state, !current);
                found = true;
            }
            else
            {
                FieldInfo f = stateType.GetField(key, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(bool))
                {
                    current = (bool)f.GetValue(state);
                    f.SetValue(state, !current);
                    found = true;
                }
            }

            if (!found) return false;
            sv.Repaint();
            return true;
        }

        private static bool TryExtractSceneViewCameraPanel(float relX, float relY)
        {
            try
            {
                SceneView sv = s_OffscreenTarget as SceneView;
                if (sv == null) sv = SceneView.lastActiveSceneView;
                if (sv == null) return false;

                object settings = sv.cameraSettings;
                System.Type settingsType = settings.GetType();
                float GetFloat(string name, float fallback)
                {
                    FieldInfo f = settingsType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null && f.FieldType == typeof(float))
                    {
                        try { return (float)f.GetValue(settings); } catch { return fallback; }
                    }
                    PropertyInfo p = settingsType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.PropertyType == typeof(float))
                    {
                        try { return (float)p.GetValue(settings); } catch { return fallback; }
                    }
                    return fallback;
                }
                bool GetBool(string name, bool fallback)
                {
                    FieldInfo f = settingsType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null && f.FieldType == typeof(bool))
                    {
                        try { return (bool)f.GetValue(settings); } catch { return fallback; }
                    }
                    PropertyInfo p = settingsType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.PropertyType == typeof(bool))
                    {
                        try { return (bool)p.GetValue(settings); } catch { return fallback; }
                    }
                    return fallback;
                }

                float speedMin = GetFloat("speedMin", 0.01f);
                float speedMax = GetFloat("speedMax", 2f);
                float speed = GetFloat("speed", speedMin);
                if (speedMax <= speedMin) speedMax = speedMin + 0.01f;
                speed = Mathf.Clamp(speed, speedMin, speedMax);

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "panel_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "scene_camera";
                s_LastFrontendPopupX = relX;
                s_LastFrontendPopupY = relY;

                var sb = new StringBuilder(3072);
                sb.Append("{\"type\":\"panel_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                sb.Append(",\"title\":\"Scene Camera\",\"sourceType\":\"scene_camera\",\"controls\":[");

                bool dynamicClip = GetBool("dynamicClip", true);
                bool isOrthographic = sv.orthographic;
                bool backfaceEnabled = true;
                try
                {
                    // Use reflection to stay compatible with editor versions without GPU-driven pipeline APIs.
                    System.Type graphicsSettingsType = typeof(UnityEngine.Rendering.GraphicsSettings);
                    PropertyInfo enableGdrpProp = graphicsSettingsType.GetProperty("enableGDRP",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (enableGdrpProp != null && enableGdrpProp.PropertyType == typeof(bool))
                        backfaceEnabled = (bool)enableGdrpProp.GetValue(null);
                }
                catch { }

                sb.Append("{\"id\":\"cameraFov\",\"type\":\"slider\",\"label\":\"Field of View\",\"tooltip\":\"The height of the camera's view angle. Measured in degrees vertically, or along the local Y axis.\",\"min\":4,\"max\":120,\"step\":0.1,\"disabled\":");
                sb.Append(isOrthographic ? "true" : "false");
                sb.Append(",\"value\":");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}", GetFloat("fieldOfView", 60f));
                sb.Append("},");

                sb.Append("{\"id\":\"dynamicClip\",\"type\":\"checkbox\",\"label\":\"Dynamic Clipping\",\"tooltip\":\"Check this to enable camera's near and far clipping planes to be calculated relative to the viewport size of the Scene.\",\"value\":");
                sb.Append(dynamicClip ? "true" : "false");
                sb.Append("},");

                sb.Append("{\"id\":\"sectionClipPlanes\",\"type\":\"section_header\",\"label\":\"Clipping Planes\"},");

                sb.Append("{\"id\":\"nearClip\",\"type\":\"float_field\",\"label\":\"Near\",\"tooltip\":\"Near clipping plane.\",\"subField\":true,\"disabled\":");
                sb.Append(dynamicClip ? "true" : "false");
                sb.Append(",\"value\":");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F4}", GetFloat("nearClip", 0.03f));
                sb.Append("},");

                sb.Append("{\"id\":\"farClip\",\"type\":\"float_field\",\"label\":\"Far\",\"tooltip\":\"Far clipping plane.\",\"subField\":true,\"disabled\":");
                sb.Append(dynamicClip ? "true" : "false");
                sb.Append(",\"value\":");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}", GetFloat("farClip", 10000f));
                sb.Append("},");

                sb.Append("{\"id\":\"occlusionCulling\",\"type\":\"checkbox\",\"label\":\"Occlusion Culling\",\"tooltip\":\"Check this to enable occlusion culling in the Scene view.\",\"value\":");
                sb.Append(GetBool("occlusionCulling", false) ? "true" : "false");
                sb.Append("},");

                sb.Append("{\"id\":\"backfaceCulling\",\"type\":\"checkbox\",\"label\":\"Backface Culling\",\"tooltip\":\"Check this to enable backface culling in the Scene View.\",\"disabled\":");
                sb.Append(backfaceEnabled ? "false" : "true");
                sb.Append(",\"value\":");
                sb.Append(GetBool("backfaceCulling", false) ? "true" : "false");
                sb.Append("},");

                sb.Append("{\"id\":\"sectionNavigation\",\"type\":\"section_header\",\"label\":\"Navigation\"},");

                sb.Append("{\"id\":\"cameraEasing\",\"type\":\"checkbox\",\"label\":\"Camera Easing\",\"tooltip\":\"Check this to enable camera movement easing.\",\"value\":");
                sb.Append(GetBool("easingEnabled", false) ? "true" : "false");
                sb.Append("},");

                sb.Append("{\"id\":\"cameraAccel\",\"type\":\"checkbox\",\"label\":\"Camera Acceleration\",\"tooltip\":\"Check this to enable acceleration when moving the camera.\",\"value\":");
                sb.Append(GetBool("accelerationEnabled", true) ? "true" : "false");
                sb.Append("},");

                sb.Append("{\"id\":\"cameraSpeed\",\"type\":\"slider\",\"label\":\"Camera Speed\",\"tooltip\":\"The current speed of the camera in the Scene view.\",\"min\":");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F4}", speedMin);
                sb.Append(",\"max\":");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F4}", speedMax);
                sb.Append(",\"step\":0.01,\"value\":");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F4}", speed);
                sb.Append("},");

                sb.Append("{\"id\":\"speedMin\",\"type\":\"float_field\",\"label\":\"Min\",\"tooltip\":\"The minimum speed of the camera in the Scene view.\",\"subField\":true,\"value\":");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F4}", speedMin);
                sb.Append("},");

                sb.Append("{\"id\":\"speedMax\",\"type\":\"float_field\",\"label\":\"Max\",\"tooltip\":\"The maximum speed of the camera in the Scene view.\",\"subField\":true,\"value\":");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:F4}", speedMax);
                sb.Append("}");

                sb.Append("]}");
                bool sent = SendDataChannelMessage(sb.ToString());
                s_FrontendPopupSent = sent;
                if (sent) CodelyLogger.Log($"[NWB-SceneCamera] Sent panel: id={s_CurrentFrontendPopupId}");
                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-SceneCamera] Error extracting panel: {ex.Message}");
                return false;
            }
        }

        private static bool s_DrawModeReflectionDone;
        private static object[] s_BuiltinCameraModes;
        private static FieldInfo s_CameraModeDrawMode;
        private static FieldInfo s_CameraModeName;
        private static FieldInfo s_CameraModeSection;
        private static MethodInfo s_IsCameraDrawModeSupportedMethod;

        private static void EnsureDrawModeReflection()
        {
            if (s_DrawModeReflectionDone) return;
            s_DrawModeReflectionDone = true;
            try
            {
                System.Type srmType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SceneRenderModeWindow");
                if (srmType == null) { CodelyLogger.LogWarning("[NWB-DrawMode] SceneRenderModeWindow type not found"); return; }

                System.Type stylesType = srmType.GetNestedType("Styles", BindingFlags.NonPublic | BindingFlags.Public);
                if (stylesType == null) { CodelyLogger.LogWarning("[NWB-DrawMode] Styles nested type not found"); return; }

                FieldInfo modesField = stylesType.GetField("sBuiltinCameraModes",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (modesField == null) { CodelyLogger.LogWarning("[NWB-DrawMode] sBuiltinCameraModes field not found"); return; }

                System.Array modesArray = modesField.GetValue(null) as System.Array;
                if (modesArray == null || modesArray.Length == 0) return;

                s_BuiltinCameraModes = new object[modesArray.Length];
                for (int i = 0; i < modesArray.Length; i++)
                    s_BuiltinCameraModes[i] = modesArray.GetValue(i);

                System.Type cmType = modesArray.GetValue(0).GetType();
                s_CameraModeDrawMode = cmType.GetField("drawMode", BindingFlags.Instance | BindingFlags.Public);
                s_CameraModeName = cmType.GetField("name", BindingFlags.Instance | BindingFlags.Public);
                s_CameraModeSection = cmType.GetField("section", BindingFlags.Instance | BindingFlags.Public);

                // SceneView.IsCameraDrawModeSupported is internal in the Tuanjie engine
                // (public in standard Unity), so resolve it via reflection.
                s_IsCameraDrawModeSupportedMethod = typeof(SceneView).GetMethod(
                    "IsCameraDrawModeSupported",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new System.Type[] { cmType }, null);

                CodelyLogger.Log($"[NWB-DrawMode] Reflection OK: modes={s_BuiltinCameraModes.Length} " +
                    $"drawMode={s_CameraModeDrawMode != null} name={s_CameraModeName != null} section={s_CameraModeSection != null} " +
                    $"isSupported={s_IsCameraDrawModeSupportedMethod != null}");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-DrawMode] Reflection init error: {ex.Message}");
            }
        }

        /// <summary>
        /// Calls <c>SceneView.IsCameraDrawModeSupported</c> via reflection (internal in
        /// the Tuanjie engine). Assumes the mode is supported when the API is unavailable,
        /// so modes are shown rather than silently hidden.
        /// </summary>
        private static bool IsCameraDrawModeSupportedReflected(SceneView sv, SceneView.CameraMode cameraMode)
        {
            if (s_IsCameraDrawModeSupportedMethod == null) return true;
            try { return (bool)s_IsCameraDrawModeSupportedMethod.Invoke(sv, new object[] { cameraMode }); }
            catch { return true; }
        }

        /// <summary>
        /// Extract draw mode (Shading Mode) popup items and send as popup_show.
        /// </summary>
        private static bool TryExtractDrawModePopup(float relX, float relY)
        {
            EnsureDrawModeReflection();
            if (s_BuiltinCameraModes == null || s_CameraModeName == null) return false;

            try
            {
                SceneView sv = s_OffscreenTarget as SceneView;
                if (sv == null) sv = SceneView.lastActiveSceneView;
                if (sv == null) return false;

                var currentMode = sv.cameraMode;

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "draw_mode";

                var sb = new StringBuilder(4096);
                sb.Append("{\"type\":\"popup_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                sb.Append(",\"items\":[");

                string lastSection = null;
                int validCount = 0;
                var pathList = new List<string>();

                for (int i = 0; i < s_BuiltinCameraModes.Length; i++)
                {
                    object cm = s_BuiltinCameraModes[i];
                    string name = s_CameraModeName.GetValue(cm) as string ?? "";
                    string section = s_CameraModeSection?.GetValue(cm) as string ?? "";
                    int drawModeInt = s_CameraModeDrawMode != null ? (int)s_CameraModeDrawMode.GetValue(cm) : -1;

                    // Construct a public CameraMode struct to call public SceneView methods.
                    var cameraMode = new SceneView.CameraMode
                    {
                        drawMode = (DrawCameraMode)drawModeInt,
                        name = name,
                        section = section
                    };

                    // Skip modes not supported by the current rendering pipeline.
                    if (!IsCameraDrawModeSupportedReflected(sv, cameraMode))
                        continue;

                    // Check if the mode is enabled (e.g. deferred modes on non-deferred path).
                    bool modeEnabled = sv.IsCameraDrawModeEnabled(cameraMode);

                    if (section != lastSection && !string.IsNullOrEmpty(section))
                    {
                        if (lastSection != null)
                        {
                            if (validCount > 0) sb.Append(',');
                            sb.Append("{\"separator\":true}");
                            validCount++;
                        }
                        if (validCount > 0) sb.Append(',');
                        sb.Append("{\"index\":-1,\"label\":\"");
                        sb.Append(EscapeJsonString(section));
                        sb.Append("\",\"path\":\"\",\"enabled\":false,\"checked\":false,\"separator\":false}");
                        validCount++;
                        lastSection = section;
                    }

                    bool isChecked = (DrawCameraMode)drawModeInt == currentMode.drawMode;
                    string pathKey = $"drawmode|{drawModeInt}|{name}";
                    pathList.Add(pathKey);

                    if (validCount > 0) sb.Append(',');
                    sb.Append("{\"index\":");
                    sb.Append(pathList.Count - 1);
                    sb.Append(",\"label\":\"");
                    sb.Append(EscapeJsonString(name));
                    sb.Append("\",\"path\":\"");
                    sb.Append(EscapeJsonString(pathKey));
                    sb.Append("\",\"enabled\":");
                    sb.Append(modeEnabled ? "true" : "false");
                    sb.Append(",\"checked\":");
                    sb.Append(isChecked ? "true" : "false");
                    sb.Append(",\"separator\":false}");
                    validCount++;
                }

                // Separator before Show Lightmap Resolution.
                if (validCount > 0) sb.Append(',');
                sb.Append("{\"separator\":true}");

                // Show Lightmap Resolution toggle — disabled when current mode < RealtimeCharting(12)
                // or the current mode is not enabled, matching Unity's SceneRenderModeWindow logic.
                bool showRes = GetLightmapShowResolution();
                bool lightmapToggleEnabled = (int)currentMode.drawMode >= 12 &&
                    sv.IsCameraDrawModeEnabled(currentMode);
                string resPath = "drawmode_lightmap_resolution";
                pathList.Add(resPath);
                sb.Append(",{\"index\":");
                sb.Append(pathList.Count - 1);
                sb.Append(",\"label\":\"Show Lightmap Resolution\",\"path\":\"");
                sb.Append(resPath);
                sb.Append("\",\"enabled\":");
                sb.Append(lightmapToggleEnabled ? "true" : "false");
                sb.Append(",\"checked\":");
                sb.Append(showRes ? "true" : "false");
                sb.Append(",\"separator\":false}");

                sb.Append("]}");

                s_CachedPopupItemPaths = pathList.ToArray();

                string json = sb.ToString();
                bool sent = SendDataChannelMessage(json);
                s_FrontendPopupSent = sent;

                if (sent)
                    CodelyLogger.Log($"[NWB-DrawMode] Sent draw mode popup: id={s_CurrentFrontendPopupId} items={pathList.Count}");
                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-DrawMode] Error extracting draw modes: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        // =================== Panel Protocol Dispatch ===================

        /// <summary>
        /// Handle a panel_change message from the browser. Dispatches to
        /// the appropriate handler based on the panel's sourceType.
        /// </summary>
        private static void HandlePanelChange(string json)
        {
            try
            {
                string panelId = ExtractJsonString(json, "id");
                string controlId = ExtractJsonString(json, "controlId");

                CodelyLogger.Log($"[NWB-Panel] panel_change: id={panelId} control={controlId} source={s_PopupSourceType}");

                if (panelId != s_CurrentFrontendPopupId)
                {
                    CodelyLogger.LogWarning($"[NWB-Panel] Mismatched panel ID: got {panelId}, expected {s_CurrentFrontendPopupId}");
                    return;
                }

                if (s_PopupSourceType == "grid_settings")
                    HandleGridPanelChange(json, controlId);
                else if (s_PopupSourceType == "snap_increment")
                    HandleSnapPanelChange(json, controlId);
                else if (s_PopupSourceType == "snap_settings")
                    HandleSnapSettingsPanelChange(json, controlId);
                else if (s_PopupSourceType == "scene_camera")
                    HandleSceneViewCameraPanelChange(json, controlId);
                else if (s_PopupSourceType == "gameview_size_add")
                    HandleGameViewSizeAddPanelChange(json, controlId);
                else
                    CodelyLogger.LogWarning($"[NWB-Panel] Unknown panel source: {s_PopupSourceType}");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Panel] Error handling panel_change: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // =================== GameView Size Add Panel ===================

        /// <summary>
        /// Send a panel_show message for the GameView "Add Resolution" form.
        /// Uses text_input, dropdown, int2_field, and button_row control types.
        /// </summary>
        private static void TryShowGameViewSizeAddPanel()
        {
            try
            {
                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "panel_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "gameview_size_add";

                var sb = new StringBuilder(1024);
                sb.Append("{\"type\":\"panel_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"title\":\"Add\",\"controls\":[");

                // Label text input.
                sb.Append("{\"type\":\"text_input\",\"id\":\"label\",\"label\":\"Label\",\"value\":\"\"},");

                // Type dropdown: 0=Aspect Ratio, 1=Fixed Resolution (default).
                sb.Append("{\"type\":\"dropdown\",\"id\":\"sizeType\",\"label\":\"Type\",");
                sb.Append("\"options\":[\"Aspect Ratio\",\"Fixed Resolution\"],\"value\":1},");

                // Width & Height integer inputs.
                sb.Append("{\"type\":\"int2_field\",\"id\":\"widthHeight\",\"label\":\"Width & Height\",");
                sb.Append("\"labels\":[\"X\",\"Y\"],\"value\":[10,10]},");

                // Cancel / OK buttons.
                sb.Append("{\"type\":\"button_row\",\"id\":\"actions\",\"label\":\"\",");
                sb.Append("\"buttons\":[{\"id\":\"cancel\",\"label\":\"Cancel\"},{\"id\":\"ok\",\"label\":\"OK\"}]}");

                sb.Append("]}");

                string json = sb.ToString();
                bool sent = SendDataChannelMessage(json);
                s_FrontendPopupSent = sent;

                if (sent)
                    CodelyLogger.Log($"[NWB-Panel] Sent GameViewSize add panel: id={s_CurrentFrontendPopupId}");
                else
                    CodelyLogger.LogWarning("[NWB-Panel] Failed to send GameViewSize add panel");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-Panel] Error showing add panel: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle panel_change messages from the GameView size add panel.
        /// Uses the cached IFlexibleMenuItemProvider.Create()/Add() pattern
        /// which is the same path FlexibleMenu uses internally.
        /// </summary>
        private static void HandleGameViewSizeAddPanelChange(string json, string controlId)
        {
            try
            {
                // Only act on the OK/Cancel button clicks.
                if (controlId != "actions") return;

                string buttonId = ExtractJsonString(json, "buttonId");
                if (buttonId == "cancel")
                {
                    CodelyLogger.Log("[NWB-Panel] GameViewSize add panel cancelled");
                    SendDataChannelMessage("{\"type\":\"panel_close\"}");
                    ClearFrontendPopupState();
                    return;
                }

                if (buttonId != "ok") return;

                // Extract form fields from the combined JSON payload.
                string label = ExtractJsonString(json, "formLabel");
                int sizeType = ExtractJsonInt(json, "formSizeType");
                int width = ExtractJsonInt(json, "formWidth");
                int height = ExtractJsonInt(json, "formHeight");

                CodelyLogger.Log($"[NWB-Panel] GameViewSize add: label={label} type={sizeType} w={width} h={height}");

                if (width <= 0 || height <= 0)
                {
                    CodelyLogger.LogWarning("[NWB-Panel] Invalid size: width and height must be > 0");
                    return;
                }

                // Use the cached IFlexibleMenuItemProvider from the popup session.
                // Create() returns a default GameViewSize; we then set properties and Add().
                object provider = s_CachedFlexibleMenuItemProvider;
                if (provider == null)
                {
                    CodelyLogger.LogWarning("[NWB-Panel] No cached FlexibleMenuItemProvider for add");
                    return;
                }

                System.Type provType = provider.GetType();
                MethodInfo createMethod = provType.GetMethod("Create",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (createMethod == null)
                {
                    CodelyLogger.LogWarning("[NWB-Panel] Create() method not found on provider");
                    return;
                }

                object newSize = createMethod.Invoke(provider, null);
                if (newSize == null)
                {
                    CodelyLogger.LogWarning("[NWB-Panel] Create() returned null");
                    return;
                }

                // Set properties: sizeType, width, height, baseText.
                System.Type sizeObjType = newSize.GetType();
                PropertyInfo stProp = sizeObjType.GetProperty("sizeType",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo wProp = sizeObjType.GetProperty("width",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo hProp = sizeObjType.GetProperty("height",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo btProp = sizeObjType.GetProperty("baseText",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (stProp != null)
                {
                    object enumVal = System.Enum.ToObject(stProp.PropertyType, sizeType);
                    stProp.SetValue(newSize, enumVal);
                }
                wProp?.SetValue(newSize, width);
                hProp?.SetValue(newSize, height);
                btProp?.SetValue(newSize, label ?? "");

                MethodInfo addMethod = provType.GetMethod("Add",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (addMethod == null)
                {
                    CodelyLogger.LogWarning("[NWB-Panel] Add() method not found on provider");
                    return;
                }

                int newIndex = (int)addMethod.Invoke(provider, new object[] { newSize });
                CodelyLogger.Log($"[NWB-Panel] Added GameViewSize at index {newIndex}: {label} ({width}x{height}) type={sizeType}");

                SendDataChannelMessage("{\"type\":\"panel_close\"}");
                ClearFrontendPopupState();

                if (s_OffscreenTarget != null) s_OffscreenTarget.Repaint();
            }
            catch (Exception ex)
            {
                string inner = ex.InnerException != null ? ex.InnerException.Message : "";
                CodelyLogger.LogWarning($"[NWB-Panel] Error adding GameViewSize: {ex.Message} inner={inner}\n{ex.StackTrace}");
            }
        }

        private static void HandlePanelClose(string json)
        {
            CodelyLogger.Log("[NWB-Panel] panel_close received");
            ClearFrontendPopupState();
        }
    }
}
