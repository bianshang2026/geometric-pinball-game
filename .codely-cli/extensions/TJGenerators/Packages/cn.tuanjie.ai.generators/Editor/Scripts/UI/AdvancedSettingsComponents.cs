#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using TJGenerators.Config;
using TJGenerators.Generators;
using TJGenerators.Utils;

namespace TJGenerators.UI
{
    /// <summary>
    /// 「高级设置」折叠区及行内控件（bool / 文本 / 下拉 / 数值等），供各生成器窗口与 DynamicGenerator 使用。
    /// </summary>
    public static class AdvancedSettingsComponents
    {
        /// <summary>高级参数行统一高度（与折叠内控件对齐）。</summary>
        public static float AdvancedSettingsRowHeight => 33f;

        /// <summary>高级参数行右侧外边距。</summary>
        public static float AdvancedSettingsRowRightPadding => 16f;

        /// <summary>高级参数行右侧控件列宽度。</summary>
        public static float AdvancedSettingsRowControlWidth => 226f;

        /// <summary>
        /// 绘制与「高级设置」同款的折叠标题行（标题 + 箭头，点击切换展开）。
        /// </summary>
        public static bool DrawSettingsFoldoutHeader(bool expanded, string foldoutLabel, bool uppercaseLabel = true)
        {
            const float rowHeight = 20f;
            const float arrowSize = CommonStyles.KeyboardArrowRightIconSize;
            const float arrowCenterGapToLabelRight = 12f;

            Rect rowRect = GUILayoutUtility.GetRect(
                GUIContent.none,
                GUIStyle.none,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(rowHeight));

            string labelText = foldoutLabel ?? string.Empty;
            if (uppercaseLabel)
                labelText = labelText.ToUpperInvariant();
            GUI.Label(rowRect, labelText, CommonStyles.AdvancedFoldoutTitleStyle);

            Vector2 labelSize = CommonStyles.AdvancedFoldoutTitleStyle.CalcSize(new GUIContent(labelText));
            float labelRight = rowRect.x + labelSize.x;
            float arrowCenterX = labelRight + arrowCenterGapToLabelRight;

            var arrowTex = CommonStyles.DropBoxRightArrow4xTexture;
            if (arrowTex != null)
            {
                Rect arrowRect = new Rect(
                    arrowCenterX - arrowSize * 0.5f,
                    rowRect.y + (rowHeight - arrowSize) * 0.5f,
                    arrowSize,
                    arrowSize);

                Matrix4x4 old = GUI.matrix;
                float angle = expanded ? 90f : 0f;
                GUIUtility.RotateAroundPivot(angle, arrowRect.center);
                GUI.DrawTexture(arrowRect, arrowTex, ScaleMode.ScaleToFit, true);
                GUI.matrix = old;
            }

            EditorGUIUtility.AddCursorRect(rowRect, MouseCursor.Link);
            bool newExpanded = expanded;
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && rowRect.Contains(Event.current.mousePosition))
            {
                newExpanded = !expanded;
                Event.current.Use();
            }

            return newExpanded;
        }

        /// <summary>
        /// 绘制可折叠设置区：标题行 + 展开时执行 <paramref name="drawExpandedContent"/>。
        /// </summary>
        public static bool DrawSettingsFoldout(
            bool expanded,
            string foldoutLabel,
            Action drawExpandedContent,
            bool uppercaseLabel = true
        )
        {
            bool newExpanded = DrawSettingsFoldoutHeader(expanded, foldoutLabel, uppercaseLabel);
            if (newExpanded && drawExpandedContent != null)
            {
                GUILayout.Space(CommonStyles.Space2);
                drawExpandedContent();
            }

            return newExpanded;
        }

        /// <summary>
        /// 高级设置折叠行内的布尔勾选（标签左、复选框右）。
        /// </summary>
        public static bool DrawAdvancedStyleBoolRow(string label, bool value, string tooltip = null)
        {
            Rect rowRect = GUILayoutUtility.GetRect(
                0f,
                AdvancedSettingsRowHeight,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(AdvancedSettingsRowHeight));
            GetAdvancedRowRects(rowRect, out Rect labelRect, out Rect controlRect, 30f);
            GUI.Label(labelRect, new GUIContent(label ?? string.Empty, tooltip ?? string.Empty), CommonStyles.TargetObjectButtonLabelStyle);
            float boxSize = Mathf.Min(30f, Mathf.Min(controlRect.width, controlRect.height));
            Rect boxRect = new Rect(
                controlRect.x,
                controlRect.y + (controlRect.height - boxSize) * 0.5f,
                boxSize,
                boxSize);
            bool newValue = GUI.Toggle(boxRect, value, GUIContent.none, CommonStyles.CheckboxBoxStyle);
            // Toggle 使用 FocusType.Passive，点击不会自动清除 keyboardControl，
            // 需手动清焦，使数字字段的待定编辑在下一帧提交。
            if (newValue != value)
                GUI.FocusControl(null);
            return newValue;
        }

        /// <summary>
        /// 高级设置折叠行内的单行文本输入（标签左、输入框右）。
        /// </summary>
        public static string DrawAdvancedStyleTextRow(string label, string value, string tooltip = null)
        {
            Rect rowRect = GUILayoutUtility.GetRect(
                0f,
                AdvancedSettingsRowHeight,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(AdvancedSettingsRowHeight));
            GetAdvancedRowRects(rowRect, out Rect labelRect, out Rect controlRect, AdvancedSettingsRowHeight);
            GUI.Label(labelRect, new GUIContent(label ?? string.Empty, tooltip ?? string.Empty), CommonStyles.TargetObjectButtonLabelStyle);
            return DrawAdvancedInputTextField(controlRect, value, delayed: false);
        }

        /// <summary>
        /// 高级设置同款整数行（标签左、输入框右）。
        /// </summary>
        public static int DrawAdvancedStyleIntRow(string label, int value, string controlName = null, string tooltip = null)
        {
            Rect rowRect = GUILayoutUtility.GetRect(
                0f,
                AdvancedSettingsRowHeight,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(AdvancedSettingsRowHeight));
            GetAdvancedRowRects(rowRect, out Rect labelRect, out Rect controlRect, 30f);
            GUI.Label(labelRect, new GUIContent(label ?? string.Empty, tooltip ?? string.Empty), CommonStyles.TargetObjectButtonLabelStyle);
            string resolvedName = controlName ?? "advanced_int_" + (label ?? "field");
            return DrawAdvancedDelayedIntField(controlRect, value, resolvedName);
        }

        /// <summary>
        /// 高级设置同款浮点滑条行（标签左、滑条右）。
        /// </summary>
        public static float DrawAdvancedStyleSliderRow(string label, float value, float min, float max, string tooltip = null)
        {
            Rect rowRect = GUILayoutUtility.GetRect(
                0f,
                AdvancedSettingsRowHeight,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(AdvancedSettingsRowHeight));
            GetAdvancedRowRects(rowRect, out Rect labelRect, out Rect controlRect, AdvancedSettingsRowHeight);
            GUI.Label(labelRect, new GUIContent(label ?? string.Empty, tooltip ?? string.Empty), CommonStyles.TargetObjectButtonLabelStyle);
            return EditorGUI.Slider(controlRect, value, min, max);
        }

        /// <summary>
        /// 绘制高级设置折叠区：Foldout「高级设置」+ 展开时按配置绘制各高级参数。
        /// </summary>
        public static bool DrawAdvancedSettingsFoldout(
            bool expanded,
            IGeneratorParameterProvider provider,
            List<ParameterConfig> parameters,
            string foldoutLabel = null
        )
        {
            bool hasParams = parameters != null && parameters.Count > 0;
            if (!hasParams)
                return false;

            return DrawSettingsFoldout(
                expanded,
                foldoutLabel ?? TJGeneratorsL10n.L("高级设置"),
                () => DrawAdvancedSettingsParameters(provider, parameters),
                uppercaseLabel: true
            );
        }

        private static void DrawAdvancedSettingsParameters(
            IGeneratorParameterProvider provider,
            List<ParameterConfig> parameters
        )
        {
            if (provider == null || parameters == null)
                return;

            bool drewAny = false;
            foreach (var param in parameters)
            {
                if (param == null)
                    continue;
                if (!string.IsNullOrEmpty(param.dependsOn))
                {
                    var depVal = provider.GetParameter(param.dependsOn);
                    if (depVal == null || depVal.ToString() != param.dependsValue)
                        continue;
                }

                if (drewAny)
                    GUILayout.Space(CommonStyles.Space2);
                DrawAdvancedParameter(provider, param);
                drewAny = true;
            }
        }

        internal static void DrawAdvancedParameter(IGeneratorParameterProvider provider, ParameterConfig param)
        {
            if (provider == null || param == null)
                return;

            switch (param.type)
            {
                case "dropdown":
                    DrawAdvancedDropdown(provider, param);
                    break;
                case "int":
                    DrawAdvancedIntField(provider, param);
                    break;
                case "float":
                    DrawAdvancedFloatField(provider, param);
                    break;
                case "bool":
                    DrawAdvancedBoolField(provider, param);
                    break;
                case "json":
                    DrawAdvancedJsonField(provider, param);
                    break;
                case "string":
                default:
                    DrawAdvancedStringField(provider, param);
                    break;
            }
        }

        private static void GetAdvancedRowRects(Rect rowRect, out Rect labelRect, out Rect controlRect, float controlHeight)
        {
            float controlX = rowRect.xMax - AdvancedSettingsRowRightPadding - AdvancedSettingsRowControlWidth;
            float controlY = rowRect.y + (rowRect.height - controlHeight) * 0.5f;
            controlRect = new Rect(controlX, controlY, AdvancedSettingsRowControlWidth, controlHeight);
            labelRect = new Rect(rowRect.x, rowRect.y, Mathf.Max(1f, controlX - rowRect.x), rowRect.height);
        }

        private static void DrawAdvancedDropdown(IGeneratorParameterProvider provider, ParameterConfig param)
        {
            if (param.options == null || param.options.Count == 0)
                return;

            object currentVal = provider.GetParameter(param.id);
            int index = 0;
            if (currentVal != null)
            {
                string valStr = currentVal.ToString();
                for (int i = 0; i < param.options.Count; i++)
                {
                    if (param.options[i].value == valStr)
                    {
                        index = i;
                        break;
                    }
                }
            }

            string[] labels = new string[param.options.Count];
            for (int i = 0; i < param.options.Count; i++)
                labels[i] = TJGeneratorsL10n.L(param.options[i].label);

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(TJGeneratorsL10n.L(param.label ?? string.Empty), TJGeneratorsL10n.L(param.tooltip ?? string.Empty)), CommonStyles.TargetObjectButtonLabelStyle, GUILayout.Height(AdvancedSettingsRowHeight));
            GUILayout.FlexibleSpace();
            int newIndex = UIComponents.DrawStyledDropdown(
                "advanced_" + (param.id ?? "dropdown"),
                index,
                labels,
                separatorBeforeIndex: -1,
                panelTopGap: 4f,
                hoverInset: 2f,
                dropdownWidth: AdvancedSettingsRowControlWidth);
            GUILayout.Space(AdvancedSettingsRowRightPadding);
            GUILayout.EndHorizontal();

            if (newIndex != index)
                provider.SetParameter(param.id, param.options[newIndex].value);
        }

        private static void DrawAdvancedIntField(IGeneratorParameterProvider provider, ParameterConfig param)
        {
            int value = 0;
            if (provider.GetParameter(param.id) != null)
                int.TryParse(provider.GetParameter(param.id).ToString(), out value);

            Rect rowRect = GUILayoutUtility.GetRect(0f, AdvancedSettingsRowHeight, GUILayout.ExpandWidth(true), GUILayout.Height(AdvancedSettingsRowHeight));
            GetAdvancedRowRects(rowRect, out var labelRect, out var controlRect, 30f);
            GUI.Label(labelRect, new GUIContent(TJGeneratorsL10n.L(param.label ?? string.Empty), TJGeneratorsL10n.L(param.tooltip ?? string.Empty)), CommonStyles.TargetObjectButtonLabelStyle);
            string controlName = "advanced_int_" + (param.id ?? "field");
            int newValue = DrawAdvancedDelayedIntField(controlRect, value, controlName);
            if (param.min != 0 || param.max != 0)
                newValue = (int)Mathf.Clamp((float)newValue, param.min, param.max);
            if (newValue != value)
                provider.SetParameter(param.id, newValue);
        }

        private static void DrawAdvancedFloatField(IGeneratorParameterProvider provider, ParameterConfig param)
        {
            float value = 0f;
            if (provider.GetParameter(param.id) != null)
                float.TryParse(provider.GetParameter(param.id).ToString(), out value);

            Rect rowRect = GUILayoutUtility.GetRect(0f, AdvancedSettingsRowHeight, GUILayout.ExpandWidth(true), GUILayout.Height(AdvancedSettingsRowHeight));
            GetAdvancedRowRects(rowRect, out var labelRect, out var controlRect, 30f);
            GUI.Label(labelRect, new GUIContent(TJGeneratorsL10n.L(param.label ?? string.Empty), TJGeneratorsL10n.L(param.tooltip ?? string.Empty)), CommonStyles.TargetObjectButtonLabelStyle);
            string controlName = "advanced_float_" + (param.id ?? "field");
            float newValue = DrawAdvancedDelayedFloatField(controlRect, value, controlName);
            if (param.min != 0 || param.max != 0)
                newValue = Mathf.Clamp(newValue, param.min, param.max);
            if (Math.Abs(newValue - value) > 1e-6f)
                provider.SetParameter(param.id, newValue);
        }

        private static void DrawAdvancedBoolField(IGeneratorParameterProvider provider, ParameterConfig param)
        {
            bool value = false;
            if (provider.GetParameter(param.id) is bool vb)
                value = vb;
            else if (bool.TryParse(provider.GetParameter(param.id)?.ToString(), out bool parsed))
                value = parsed;

            bool newValue = DrawAdvancedStyleBoolRow(TJGeneratorsL10n.L(param.label), value, TJGeneratorsL10n.L(param.tooltip));
            if (newValue != value)
                provider.SetParameter(param.id, newValue);
        }

        private static void DrawAdvancedStringField(IGeneratorParameterProvider provider, ParameterConfig param)
        {
            string value = provider.GetParameter(param.id)?.ToString() ?? "";
            Rect rowRect = GUILayoutUtility.GetRect(0f, AdvancedSettingsRowHeight, GUILayout.ExpandWidth(true), GUILayout.Height(AdvancedSettingsRowHeight));
            GetAdvancedRowRects(rowRect, out var labelRect, out var controlRect, 30f);
            GUI.Label(labelRect, new GUIContent(TJGeneratorsL10n.L(param.label ?? string.Empty), TJGeneratorsL10n.L(param.tooltip ?? string.Empty)), CommonStyles.TargetObjectButtonLabelStyle);
            string newValue = DrawAdvancedInputTextField(controlRect, value, delayed: false);
            if (newValue != value)
                provider.SetParameter(param.id, newValue);
        }

        private static void DrawAdvancedInputBackground(Rect rect)
        {
            var bgTex = CommonStyles.SettingInputBox2xTexture;
            if (bgTex != null)
                UIComponents.DrawNineSliceFixed(rect, bgTex, 8, 4);
            else
                EditorGUI.DrawRect(rect, new Color(34f / 255f, 34f / 255f, 34f / 255f, 1f));
        }

        // 用静态字典自行维护待提交状态，不依赖 Unity 内部 RecycledTextEditor。
        // PopupWindow / Toggle 等非文本控件操作时，RecycledTextEditor 可能被提前清空，
        // 导致 DelayedIntField 无法提交。字典方案完全绕过这一限制。
        private static readonly Dictionary<string, string> _pendingIntEdits = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _pendingFloatEdits = new Dictionary<string, string>();

        private static int DrawAdvancedDelayedIntField(Rect rect, int value, string controlName)
        {
            DrawAdvancedInputBackground(rect);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Text);
            if (!string.IsNullOrEmpty(controlName))
                GUI.SetNextControlName(controlName);

            bool hasFocus = !string.IsNullOrEmpty(controlName) &&
                            GUI.GetNameOfFocusedControl() == controlName;

            if (!hasFocus)
            {
                if (!string.IsNullOrEmpty(controlName) &&
                    _pendingIntEdits.TryGetValue(controlName, out string pending))
                {
                    _pendingIntEdits.Remove(controlName);
                    if (int.TryParse(pending, out int parsed))
                        value = parsed;
                }
                EditorGUI.TextField(rect, value.ToString(), CommonStyles.AdvancedInputTextStyle);
                return value;
            }

            if (!_pendingIntEdits.ContainsKey(controlName))
                _pendingIntEdits[controlName] = value.ToString();

            // 过滤非整数字符
            if (Event.current.type == EventType.KeyDown)
            {
                char c = Event.current.character;
                if (c != '\0' && !char.IsDigit(c) && c != '-' && !char.IsControl(c))
                    Event.current.character = '\0';
            }

            string newText = EditorGUI.TextField(rect, _pendingIntEdits[controlName], CommonStyles.AdvancedInputTextStyle);
            _pendingIntEdits[controlName] = newText;

            if (Event.current.type == EventType.KeyDown &&
                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                _pendingIntEdits.Remove(controlName);
                if (int.TryParse(newText, out int parsedEnter))
                {
                    GUI.FocusControl(null);
                    Event.current.Use();
                    return parsedEnter;
                }
            }

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                _pendingIntEdits.Remove(controlName);
                GUI.FocusControl(null);
            }

            return value;
        }

        private static float DrawAdvancedDelayedFloatField(Rect rect, float value, string controlName)
        {
            DrawAdvancedInputBackground(rect);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Text);
            if (!string.IsNullOrEmpty(controlName))
                GUI.SetNextControlName(controlName);

            bool hasFocus = !string.IsNullOrEmpty(controlName) &&
                            GUI.GetNameOfFocusedControl() == controlName;

            if (!hasFocus)
            {
                if (!string.IsNullOrEmpty(controlName) &&
                    _pendingFloatEdits.TryGetValue(controlName, out string pending))
                {
                    _pendingFloatEdits.Remove(controlName);
                    if (float.TryParse(pending,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out float parsed))
                        value = parsed;
                }
                EditorGUI.TextField(rect,
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    CommonStyles.AdvancedInputTextStyle);
                return value;
            }

            if (!_pendingFloatEdits.ContainsKey(controlName))
                _pendingFloatEdits[controlName] =
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // 过滤非浮点字符
            if (Event.current.type == EventType.KeyDown)
            {
                char c = Event.current.character;
                if (c != '\0' && !char.IsDigit(c) && c != '-' && c != '.' &&
                    c != 'e' && c != 'E' && !char.IsControl(c))
                    Event.current.character = '\0';
            }

            string newText = EditorGUI.TextField(rect, _pendingFloatEdits[controlName], CommonStyles.AdvancedInputTextStyle);
            _pendingFloatEdits[controlName] = newText;

            if (Event.current.type == EventType.KeyDown &&
                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                _pendingFloatEdits.Remove(controlName);
                if (float.TryParse(newText,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float parsedEnter))
                {
                    GUI.FocusControl(null);
                    Event.current.Use();
                    return parsedEnter;
                }
            }

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                _pendingFloatEdits.Remove(controlName);
                GUI.FocusControl(null);
            }

            return value;
        }

        private static string DrawAdvancedInputTextField(Rect rect, string value, bool delayed, string controlName = null)
        {
            DrawAdvancedInputBackground(rect);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Text);
            if (!string.IsNullOrEmpty(controlName))
                GUI.SetNextControlName(controlName);
            if (delayed)
                return EditorGUI.DelayedTextField(rect, value ?? string.Empty, CommonStyles.AdvancedInputTextStyle);
            return EditorGUI.TextField(rect, value ?? string.Empty, CommonStyles.AdvancedInputTextStyle);
        }

        private static void DrawAdvancedJsonField(IGeneratorParameterProvider provider, ParameterConfig param)
        {
            string value = provider.GetParameter(param.id)?.ToString() ?? "";
            Rect rowRect = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true), GUILayout.Height(20f));
            GUI.Label(rowRect, new GUIContent(TJGeneratorsL10n.L(param.label ?? string.Empty), TJGeneratorsL10n.L(param.tooltip ?? string.Empty)), CommonStyles.TargetObjectButtonLabelStyle);
            GUILayout.Space(CommonStyles.Space2);
            string newValue = EditorGUILayout.TextArea(value, GUILayout.MinHeight(48f));
            if (newValue != value)
                provider.SetParameter(param.id, newValue);
        }
    }
}
#endif
