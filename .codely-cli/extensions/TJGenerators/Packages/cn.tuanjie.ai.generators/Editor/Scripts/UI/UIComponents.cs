#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using TJGenerators.Config;
using TJGenerators.Generators;
using TJGenerators.Utils;

namespace TJGenerators.UI
{
    /// <summary>
    /// 各生成器窗口共用的可复用 UI 控件（参数绘制、模型选择器等），供主窗口、Skybox、Music、Sprite 及 DynamicGenerator 统一调用。
    /// </summary>
    public static class UIComponents
    {
        private static readonly Dictionary<string, int> _dropdownPendingSelections = new Dictionary<string, int>();
        private static readonly Dictionary<string, Vector2> _promptInputScrollPositions = new Dictionary<string, Vector2>();

        private const float PromptInputInnerPaddingX = 16f;
        private const float PromptInputInnerPaddingY = 14f;
        private const float PromptInputScrollBarWidth = 16f;
        private const float PromptInputContentHeightSlack = 4f;
        private const float PromptInputMinTextWidth = 40f;

        private static float PromptInputBoxHeight =>
            UploadImageComponents.ReferenceImageFrameHeight * 0.5f;

        private static bool s_ImguiLeftMouseHeld;

        /// <summary>
        /// IMGUI 语义下鼠标左键是否处于按下状态；不访问 <see cref="Input.GetMouseButton"/>，兼容 Player Settings「仅 Input System」。
        /// 使用前应调用 <see cref="SyncImguiLeftMouseHeldFromEvent"/>（或由内部在读取前同步）。
        /// </summary>
        public static bool ImguiLeftMouseHeld => s_ImguiLeftMouseHeld;

        public static void SyncImguiLeftMouseHeldFromEvent()
        {
            Event e = Event.current;
            if (e == null)
                return;
            switch (e.rawType)
            {
                case EventType.MouseDown:
                    if (e.button == 0)
                        s_ImguiLeftMouseHeld = true;
                    break;
                case EventType.MouseUp:
                    if (e.button == 0)
                        s_ImguiLeftMouseHeld = false;
                    break;
            }
        }

        /// <summary>占位符仅在“真·空”时绘制，避免 IME 组字阶段与实际输入重叠。</summary>
        private static bool ShouldShowEmptyTextPlaceholder(string currentText)
        {
            if (!string.IsNullOrEmpty(currentText))
                return false;
            if (!string.IsNullOrEmpty(Input.compositionString))
                return false;
            return true;
        }

        /// <summary>
        /// 为上一个 GUILayout 控件的矩形区域添加手型光标（用于 LinkStyle 按钮等）。
        /// 应在绘制完控件后立即调用。
        /// </summary>
        public static void AddLinkCursorToLastRect()
        {
            var lastRect = GUILayoutUtility.GetLastRect();
            EditorGUIUtility.AddCursorRect(lastRect, MouseCursor.Link);
        }

        /// <summary>
        /// 绘制链接风格按钮（GreenButtonStyle + 手型光标），点击时执行回调。
        /// </summary>
        /// <param name="text">按钮文案</param>
        /// <param name="onClick">点击时的回调，可为 null</param>
        public static void LinkButton(string text, Action onClick = null)
        {
            if (GUILayout.Button(text, CommonStyles.GreenButtonStyle))
                onClick?.Invoke();
            AddLinkCursorToLastRect();
        }

        /// <summary>
        /// 绘制带九宫格边框的选择按钮（文案 + 可选右侧图标），与「选择对象」按钮样式一致。
        /// </summary>
        /// <param name="rect">按钮区域</param>
        /// <param name="text">按钮文案，如「选择对象」「选择类型」</param>
        /// <param name="rightIcon">按钮文案右侧图标，传 null 则不绘制</param>
        /// <param name="frameTexture">九宫格边框贴图，默认使用目标选择器边框</param>
        /// <param name="rightIconSize">右侧图标绘制尺寸</param>
        /// <param name="onClick">点击回调</param>
        public static bool DrawFramedSelectButtonInRect(
            Rect rect,
            string text,
            Texture2D rightIcon = null,
            Texture2D frameTexture = null,
            float rightIconSize = CommonStyles.FramedSelectButtonIconSize,
            Action onClick = null)
        {
            const float edgePadding = 12f;
            const float textIconGap = 8f;
            const int frameSourceBorder = 36;
            const float frameReferenceHeight = 72f;

            if (frameTexture == null)
                frameTexture = CommonStyles.TargetSelectorRectTexture;
            if (frameTexture != null)
            {
                DrawNineSliceBackground(
                    rect,
                    frameTexture,
                    frameSourceBorder,
                    frameReferenceHeight);
            }

            bool clicked = GUI.Button(rect, GUIContent.none, GUIStyle.none);
            if (clicked)
                onClick?.Invoke();

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

            string label = text ?? string.Empty;
            Vector2 textSize = CommonStyles.SectionTitleStyle.CalcSize(new GUIContent(label));
            float centerY = rect.y + rect.height * 0.5f;
            float textX = rect.x + edgePadding;
            Rect textRect = new Rect(textX, centerY - textSize.y * 0.5f, textSize.x, textSize.y);
            GUI.Label(textRect, label, CommonStyles.SectionTitleStyle);

            if (rightIcon != null)
            {
                Rect iconRect = new Rect(
                    textRect.xMax + textIconGap,
                    centerY - rightIconSize * 0.5f,
                    rightIconSize,
                    rightIconSize);
                GUI.DrawTexture(iconRect, rightIcon, ScaleMode.ScaleToFit, true);
            }

            return clicked;
        }

        private static float MeasureFramedSelectButtonWidth(
            string text,
            Texture2D rightIcon = null,
            float rightIconSize = CommonStyles.FramedSelectButtonIconSize)
        {
            const float edgePadding = 12f;
            const float textIconGap = 8f;

            string label = text ?? string.Empty;
            Vector2 textSize = CommonStyles.SectionTitleStyle.CalcSize(new GUIContent(label));
            float iconPart = rightIcon != null ? textIconGap + rightIconSize : 0f;
            return edgePadding + textSize.x + iconPart + edgePadding;
        }

        /// <summary>
        /// 绘制一行选择器：标题 + 已选时显示名称与清除/更改按钮，未选时显示“选择XXX”按钮。
        /// 选择按钮统一为 GreenButtonStyle、手型光标；行前 0、行后 5。
        /// </summary>
        /// <param name="headerLabel">标题，如 "纹理走势："</param>
        /// <param name="selectedDisplayName">选中时显示的名称，无选中传 null</param>
        /// <param name="selectButtonText">未选中时按钮文案，如 "选择纹理"</param>
        /// <param name="onClear">点击“清除”时的回调</param>
        /// <param name="onChange">点击“更改”或“选择XXX”时的回调</param>
        public static void DrawSelectorRow(
            string headerLabel,
            string selectedDisplayName,
            string selectButtonText,
            Action onClear,
            Action onChange
        )
        {
            float spaceAfter = 5f;
            GUILayout.BeginHorizontal();
            if (!string.IsNullOrEmpty(headerLabel))
                GUILayout.Label(headerLabel, CommonStyles.SectionTitleStyle);

            if (!string.IsNullOrEmpty(selectedDisplayName))
            {
                GUILayout.Label(selectedDisplayName, CommonStyles.ContentStyle);
                GUILayout.FlexibleSpace();

                LinkButton(TJGeneratorsL10n.L("清除"), onClear);
                GUILayout.Space(10f);
                LinkButton(TJGeneratorsL10n.L("更改"), onChange);
            }
            else
            {
                GUILayout.FlexibleSpace();
                LinkButton(selectButtonText, onChange);
            }

            GUILayout.EndHorizontal();
            GUILayout.Space(spaceAfter);
        }

        public static void DrawSeparator()
        {
            GUILayout.Box(GUIContent.none, CommonStyles.SeparatorStyle, GUILayout.ExpandWidth(true));
        }

        /// <summary>
        /// 绘制 1px 高度分割线（不带上下间距），宽度随容器拉伸。
        /// </summary>
        public static void DrawGapLine()
        {
            GUILayout.Box(
                GUIContent.none,
                CommonStyles.GapLineStyle,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(1f));
        }

        /// <summary>
        /// 绘制一行选择器：左侧标题 + 右侧带边框选择按钮（同一行，如「内容类型」+「选择类型」）。
        /// </summary>
        /// <param name="leftLabel">左侧标题，如「内容类型」</param>
        /// <param name="selectButtonLabel">右侧按钮文案，如「选择类型」</param>
        /// <param name="selectButtonIcon">未选中时右侧按钮图标，传 null 则不绘制</param>
        /// <param name="onSelect">点击选择按钮时的回调</param>
        /// <param name="selectedDisplayName">已选中时显示在右侧按钮上的名称；非空时图标切换为 edit</param>
        public static void DrawSelectionRow(
            string leftLabel,
            string selectButtonLabel,
            Texture2D selectButtonIcon,
            Action onSelect,
            string selectedDisplayName = null)
        {
            const float rowHeight = 34f;
            const float selectButtonHeight = 28f;
            const float labelToButtonGap = 16f;

            bool hasSelection = !string.IsNullOrEmpty(selectedDisplayName);
            string buttonLabel = hasSelection ? selectedDisplayName : selectButtonLabel;
            Texture2D buttonIcon = hasSelection ? CommonStyles.EditIconTexture : selectButtonIcon;
            float iconSize = hasSelection
                ? CommonStyles.EditIconSize
                : CommonStyles.KeyboardArrowRightIconSize;

            float selectButtonWidth = hasSelection
                ? MeasureSelectedValueWidth(buttonLabel, buttonIcon, iconSize)
                : MeasureFramedSelectButtonWidth(buttonLabel, buttonIcon, iconSize);
            Rect rowRect = GUILayoutUtility.GetRect(0f, rowHeight, GUILayout.ExpandWidth(true), GUILayout.Height(rowHeight));

            Rect selectButtonRect = new Rect(
                rowRect.xMax - selectButtonWidth,
                rowRect.y + (rowHeight - selectButtonHeight) * 0.5f,
                selectButtonWidth,
                selectButtonHeight);
            Rect labelRect = new Rect(
                rowRect.x,
                rowRect.y,
                Mathf.Max(0f, selectButtonRect.xMin - rowRect.x - labelToButtonGap),
                rowHeight);

            GUI.Label(labelRect, leftLabel ?? string.Empty, CommonStyles.SectionTitleStyle);
            if (hasSelection)
            {
                DrawSelectedValueInRect(
                    new Rect(rowRect.xMax - selectButtonWidth, rowRect.y, selectButtonWidth, rowHeight),
                    buttonLabel,
                    buttonIcon,
                    iconSize,
                    onSelect);
            }
            else
            {
                DrawFramedSelectButtonInRect(
                    selectButtonRect,
                    buttonLabel,
                    buttonIcon,
                    rightIconSize: iconSize,
                    onClick: onSelect);
            }
        }

        private static float MeasureSelectedValueWidth(string text, Texture2D rightIcon, float rightIconSize)
        {
            const float textIconGap = 8f;

            string label = text ?? string.Empty;
            Vector2 textSize = CommonStyles.SectionTitleStyle.CalcSize(new GUIContent(label));
            float iconPart = rightIcon != null ? textIconGap + rightIconSize : 0f;
            return textSize.x + iconPart;
        }

        private static void DrawSelectedValueInRect(
            Rect rect,
            string text,
            Texture2D rightIcon,
            float rightIconSize,
            Action onClick)
        {
            const float textIconGap = 8f;

            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
                onClick?.Invoke();
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

            string label = text ?? string.Empty;
            Vector2 textSize = CommonStyles.SectionTitleStyle.CalcSize(new GUIContent(label));
            float iconPart = rightIcon != null ? textIconGap + rightIconSize : 0f;
            float contentWidth = textSize.x + iconPart;
            float startX = rect.xMax - contentWidth;
            float centerY = rect.y + rect.height * 0.5f;

            Rect textRect = new Rect(startX, centerY - textSize.y * 0.5f, textSize.x, textSize.y);
            GUI.Label(textRect, label, CommonStyles.SectionTitleStyle);

            if (rightIcon != null)
            {
                Rect iconRect = new Rect(
                    textRect.xMax + textIconGap,
                    centerY - rightIconSize * 0.5f,
                    rightIconSize,
                    rightIconSize);
                GUI.DrawTexture(iconRect, rightIcon, ScaleMode.ScaleToFit, true);
            }
        }

        /// <summary>
        /// 绘制选择区块头：标题行 + 左侧已选内容 + 右侧带边框选择按钮（与「选择对象」布局一致）。
        /// </summary>
        /// <param name="title">区块标题，如「目标精灵」「内容类型」</param>
        /// <param name="drawContent">在左侧区域绘制当前选中内容</param>
        /// <param name="selectButtonLabel">右侧按钮文案，如「选择对象」「选择类型」</param>
        /// <param name="selectButtonIcon">右侧按钮图标，传 null 则不绘制</param>
        /// <param name="onSelect">点击选择按钮时的回调</param>
        public static void DrawSelectionHeaderComposite(
            string title,
            Action<Rect> drawContent,
            string selectButtonLabel,
            Texture2D selectButtonIcon,
            Action onSelect)
        {
            const float contentRowHeight = 34f;
            const float selectButtonHeight = 28f;
            const float contentToButtonGap = 16f;

            float selectButtonWidth = MeasureFramedSelectButtonWidth(selectButtonLabel, selectButtonIcon);
            float titleHeight = CommonStyles.TargetPrefabHeaderStyle.CalcSize(new GUIContent(title)).y;
            float totalHeight = titleHeight + contentRowHeight;

            Rect containerRect = GUILayoutUtility.GetRect(0f, totalHeight, GUILayout.ExpandWidth(true), GUILayout.Height(totalHeight));
            Rect titleRect = new Rect(containerRect.x, containerRect.y, containerRect.width, titleHeight);
            GUI.Label(titleRect, title, CommonStyles.TargetPrefabHeaderStyle);

            Rect contentRowRect = new Rect(containerRect.x, containerRect.y + titleHeight, containerRect.width, contentRowHeight);
            Rect selectButtonRect = new Rect(
                contentRowRect.xMax - selectButtonWidth,
                contentRowRect.y + (contentRowHeight - selectButtonHeight) * 0.5f,
                selectButtonWidth,
                selectButtonHeight);
            Rect contentRect = new Rect(
                contentRowRect.x,
                contentRowRect.y,
                Mathf.Max(0f, selectButtonRect.xMin - contentRowRect.x - contentToButtonGap),
                contentRowHeight);

            drawContent?.Invoke(contentRect);
            DrawFramedSelectButtonInRect(selectButtonRect, selectButtonLabel, selectButtonIcon, onClick: onSelect);
        }

        /// <summary>
        /// 绘制目标资源头：标题行 + 资源名称与「选择对象」按钮行。
        /// </summary>
        /// <param name="title">区块标题，如「目标预制体」</param>
        /// <param name="drawTargetContent">在名称区域绘制当前目标资源内容</param>
        /// <param name="onSelectTarget">点击「选择对象」时的回调</param>
        public static void DrawTargetHeaderComposite(
            string title,
            Action<Rect> drawTargetContent,
            Action onSelectTarget)
        {
            DrawSelectionHeaderComposite(
                title,
                drawTargetContent,
                TJGeneratorsL10n.L("选择对象"),
                CommonStyles.TargetIconTexture,
                onSelectTarget);
        }

        /// <summary>
        /// 绘制模型区块标题（与「参考图片」等区块标题样式一致）。
        /// </summary>
        public static void DrawModelSectionTitle()
        {
            GUILayout.BeginHorizontal();
            DrawSectionTitle(TJGeneratorsL10n.L("模型"), uppercase: false);
            GUILayout.EndHorizontal();
            GUILayout.Space(CommonStyles.Space2);
        }

        /// <summary>
        /// 绘制固定模型行：与 <see cref="DrawModelSelector"/> 同款样式，但不可点击切换。
        /// </summary>
        public static void DrawFixedModelSelector(
            string currentModelName,
            AIModelInfo currentSelectedModel)
        {
            DrawModelSectionTitle();
            DrawModelSelectorRow(
                currentModelName,
                currentSelectedModel,
                interactive: false,
                onModelSelected: null,
                configType: default);
        }

        /// <summary>
        /// 绘制模型切换行：模型图标 + 名称 + 下拉箭头，点击打开模型选择器窗口。
        /// </summary>
        /// <param name="currentModelName">显示的当前模型名称（如 "未选择" 或具体模型名）</param>
        /// <param name="currentSelectedModel">当前选中的模型信息，传给选择器窗口</param>
        /// <param name="onModelSelected">用户在选择器中确认选择时的回调</param>
        /// <param name="configType">配置类型，决定选择器加载哪类生成器列表</param>
        public static void DrawModelSelector(
            string currentModelName,
            AIModelInfo currentSelectedModel,
            Action<AIModelInfo> onModelSelected,
            ConfigType configType)
        {
            DrawModelSectionTitle();
            DrawModelSelectorRow(
                currentModelName,
                currentSelectedModel,
                interactive: true,
                onModelSelected,
                configType);
        }

        private static void DrawModelSelectorRow(
            string currentModelName,
            AIModelInfo currentSelectedModel,
            bool interactive,
            Action<AIModelInfo> onModelSelected,
            ConfigType configType)
        {
            const float iconSize = 20f;
            const float iconTextGap = 12f;
            const float actionIconHeight = 8f;
            const float contentPaddingX = 16f;
            const float rowHeight = 40f;

            Rect buttonRect = GUILayoutUtility.GetRect(
                GUIContent.none,
                CommonStyles.ModelSelectButtonStyle,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(rowHeight));

            DrawNineSliceBackground(
                buttonRect,
                CommonStyles.ModelSelectButtonStyle.normal.background,
                CommonStyles.ModelSelectButtonStyle.border.left,
                CommonStyles.ModelSelectButtonStyle.normal.background != null ? CommonStyles.ModelSelectButtonStyle.normal.background.height : rowHeight);

            if (interactive)
            {
                if (GUI.Button(buttonRect, GUIContent.none, GUIStyle.none))
                {
                    TJGenerators.TJGeneratorsModelSelectorWindow.ShowWindow(
                        currentSelectedModel,
                        onModelSelected,
                        configType);
                }
                EditorGUIUtility.AddCursorRect(buttonRect, MouseCursor.Link);
            }

            float leftX = buttonRect.x + contentPaddingX;
            float centerY = buttonRect.y + buttonRect.height * 0.5f;

            Rect iconRect = new Rect(leftX, centerY - iconSize * 0.5f, iconSize, iconSize);
            Texture2D modelIcon = currentSelectedModel?.Icon;
            if (modelIcon != null)
                GUI.DrawTexture(iconRect, modelIcon, ScaleMode.ScaleToFit, true);
            else
                DrawModelIconPlaceholder(iconRect, currentSelectedModel?.Name ?? currentModelName);

            float nameX = leftX + iconSize + iconTextGap;
            float nameRight = interactive
                ? buttonRect.xMax - contentPaddingX - actionIconHeight
                : buttonRect.xMax - contentPaddingX;
            Rect nameRect = new Rect(
                nameX,
                buttonRect.y,
                Mathf.Max(80f, nameRight - nameX),
                buttonRect.height);
            GUI.Label(nameRect, currentModelName ?? TJGeneratorsL10n.L("未选择"), CommonStyles.ModelSelectNameStyle);

            if (!interactive)
                return;

            Texture2D actionIcon = CommonStyles.SyncAltTexture;
            if (actionIcon != null)
            {
                float actionIconWidth = actionIconHeight * actionIcon.width / Mathf.Max(1f, actionIcon.height);
                Rect actionIconRect = new Rect(
                    buttonRect.xMax - contentPaddingX - actionIconWidth,
                    centerY - actionIconHeight * 0.5f,
                    actionIconWidth,
                    actionIconHeight);
                GUI.DrawTexture(actionIconRect, actionIcon, ScaleMode.ScaleToFit, true);
            }
        }

        /// <summary>
        /// 从模型名称提取最多两个大写字母，用于无图标时的占位显示。
        /// </summary>
        public static string GetModelDisplayInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "AI";

            string[] words = name
                .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(word => !string.IsNullOrWhiteSpace(word))
                .ToArray();

            if (words.Length >= 2)
            {
                string initials = string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
                if (!string.IsNullOrEmpty(initials))
                    return initials;
            }

            string compact = new string(name.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            if (compact.Length <= 2)
                return compact.ToUpperInvariant();

            return compact.Substring(0, 2).ToUpperInvariant();
        }

        /// <summary>
        /// 绘制模型图标占位：圆形底 + 两个大写字母（与模型选择器卡片一致）。
        /// </summary>
        public static void DrawModelIconPlaceholder(Rect rect, string modelName)
        {
            string initials = GetModelDisplayInitials(modelName);
            Vector2 center = rect.center;
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f;

            Handles.BeginGUI();
            Handles.color = new Color(51f / 255f, 51f / 255f, 51f / 255f, 1f);
            Handles.DrawSolidDisc(center, Vector3.forward, radius);
            Handles.color = CommonStyles.LayoutSeparatorColor;
            Handles.DrawWireDisc(center, Vector3.forward, Mathf.Max(0f, radius - 0.5f));
            Handles.EndGUI();

            int fontSize = Mathf.Clamp(Mathf.RoundToInt(rect.height * 0.42f), 7, 30);
            var labelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = fontSize,
                normal = { textColor = CommonStyles.FontContentColor },
                font = CommonStyles.SourceHanSansMediumFont != null
                    ? CommonStyles.SourceHanSansMediumFont
                    : CommonStyles.SourceHanSansRegularFont
            };
            GUI.Label(rect, initials, labelStyle);
        }

        /// <summary>
        /// 绘制“文本提示词输入区”：左侧标题 + 单行输入框 + 占位提示，统一使用 CommonStyles.TextFieldStyle/PlaceholderStyle。
        /// </summary>
        /// <param name="title">左侧标题，如“文本提示词”</param>
        /// <param name="placeholder">输入为空时显示的占位文案</param>
        /// <param name="value">当前输入值</param>
        /// <returns>用户输入后的新字符串</returns>
        public static string DrawTextField(string title, string placeholder, string value)
        {
            GUILayout.BeginHorizontal();
            DrawSectionTitle(title ?? TJGeneratorsL10n.L("文本提示词"), uppercase: false);
            GUILayout.EndHorizontal();

            GUILayout.Space(CommonStyles.Space2);

            return DrawTextInputOnly(placeholder, value);
        }

        private static string DrawTextInputOnly(string placeholder, string value)
        {

            GUILayout.BeginHorizontal();
            Rect textFieldRect = GUILayoutUtility.GetRect(
                GUIContent.none,
                CommonStyles.TextFieldStyle,
                GUILayout.ExpandWidth(true)
            );
            string newValue = EditorGUI.TextField(
                textFieldRect,
                value ?? "",
                CommonStyles.TextFieldStyle
            );
            if (ShouldShowEmptyTextPlaceholder(newValue))
                EditorGUI.LabelField(
                    textFieldRect,
                    placeholder ?? "",
                    CommonStyles.PlaceholderStyle
                );
            GUILayout.EndHorizontal();

            return newValue;
        }

        /// <summary>
        /// 绘制多行文本提示框（input_box_large 背景，固定高度约为参考图框的一半，超出时框内滚动）。
        /// </summary>
        public static string DrawPromptInputBox(string value, string placeholder, string controlName = "prompt_input_box", int maxLength = 0)
        {
            string text = value ?? string.Empty;
            if (maxLength > 0 && text.Length > maxLength)
                text = text.Substring(0, maxLength);

            Rect boxRect = LayoutPromptInputBox();
            DrawPromptInputBoxBackground(boxRect);

            Rect innerRect = GetPromptInputInnerRect(boxRect);
            float contentHeight = MeasurePromptInputContentHeight(text, placeholder, innerRect);

            string result = DrawScrollablePromptText(innerRect, text, placeholder, controlName, contentHeight);

            if (maxLength > 0)
                DrawCharCounter(boxRect, result.Length, maxLength);

            return result;
        }

        private static void DrawCharCounter(Rect boxRect, int current, int max)
        {
            string counter = $"{current}/{max}";
            float counterWidth = 60f;
            float counterHeight = 16f;
            var counterRect = new Rect(
                boxRect.xMax - counterWidth - 6f,
                boxRect.yMax - counterHeight - 2f,
                counterWidth,
                counterHeight);
            var oldAlign = GUI.skin.label.alignment;
            var oldFontSize = GUI.skin.label.fontSize;
            GUI.skin.label.alignment = TextAnchor.MiddleRight;
            GUI.skin.label.fontSize = 10;
            var color = current >= max ? new Color(1f, 0.4f, 0.4f) : new Color(0.6f, 0.6f, 0.6f);
            var oldColor = GUI.color;
            GUI.color = color;
            GUI.Label(counterRect, counter);
            GUI.color = oldColor;
            GUI.skin.label.alignment = oldAlign;
            GUI.skin.label.fontSize = oldFontSize;
        }

        private static Rect LayoutPromptInputBox()
        {
            return GUILayoutUtility.GetRect(
                GUIContent.none,
                CommonStyles.PromptInputNormalStyle,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(PromptInputBoxHeight));
        }

        private static void DrawPromptInputBoxBackground(Rect boxRect)
        {
            GUIStyle style = CommonStyles.PromptInputNormalStyle;

            Texture2D background = style.normal.background;
            float referenceHeight = background != null ? background.height : PromptInputBoxHeight;
            int destBorder = Mathf.Max(
                1,
                Mathf.RoundToInt(style.border.left * (PromptInputBoxHeight / Mathf.Max(1f, referenceHeight))));

            DrawNineSliceBackground(
                boxRect,
                background,
                style.border.left,
                referenceHeight,
                fixedDestBorder: destBorder);
        }

        private static Rect GetPromptInputInnerRect(Rect boxRect)
        {
            return new Rect(
                boxRect.x + PromptInputInnerPaddingX,
                boxRect.y + PromptInputInnerPaddingY,
                Mathf.Max(1f, boxRect.width - PromptInputInnerPaddingX * 2f),
                Mathf.Max(1f, boxRect.height - PromptInputInnerPaddingY * 2f));
        }

        private static float MeasurePromptInputContentHeight(string text, string placeholder, Rect innerRect)
        {
            float textWidth = GetPromptInputTextWidth(innerRect);
            string sample = string.IsNullOrEmpty(text) ? (placeholder ?? string.Empty) : text;
            float measured = CommonStyles.PromptInputTextStyle.CalcHeight(new GUIContent(sample), textWidth);
            return Mathf.Max(innerRect.height, measured + PromptInputContentHeightSlack);
        }

        private static float GetPromptInputTextWidth(Rect innerRect) =>
            Mathf.Max(PromptInputMinTextWidth, innerRect.width - PromptInputScrollBarWidth);

        private static Vector2 GetPromptInputScrollPosition(string controlName) =>
            _promptInputScrollPositions.TryGetValue(controlName, out Vector2 scrollPos)
                ? scrollPos
                : Vector2.zero;

        private static string DrawScrollablePromptText(
            Rect innerRect,
            string text,
            string placeholder,
            string controlName,
            float contentHeight)
        {
            float textWidth = GetPromptInputTextWidth(innerRect);
            Vector2 scrollPos = GetPromptInputScrollPosition(controlName);

            scrollPos = GUI.BeginScrollView(
                innerRect,
                scrollPos,
                new Rect(0f, 0f, textWidth, contentHeight),
                alwaysShowHorizontal: false,
                alwaysShowVertical: false);

            GUI.SetNextControlName(controlName);
            Rect textRect = new Rect(0f, 0f, textWidth, contentHeight);
            string newValue = EditorGUI.TextArea(textRect, text, CommonStyles.PromptInputTextStyle);

            if (ShouldShowEmptyTextPlaceholder(newValue) && !string.IsNullOrEmpty(placeholder))
                GUI.Label(textRect, placeholder, CommonStyles.PromptInputPlaceholderStyle);

            GUI.EndScrollView();
            _promptInputScrollPositions[controlName] = scrollPos;
            return newValue;
        }

        /// <summary>
        /// 绘制统一小标题组件。
        /// </summary>
        public static void DrawSectionTitle(string title, bool uppercase = true)
        {
            string text = title ?? string.Empty;
            if (uppercase)
                text = text.ToUpperInvariant();
            GUILayout.Label(text, CommonStyles.SectionTitleStyle);
        }

        /// <summary>
        /// 绘制参考图片区块标题，右侧显示当前数量/上限（如 0/1、2/14）。
        /// </summary>
        /// <param name="drawBeforeCount">可选：在数量标签左侧绘制额外控件（如「生成参考图」链接）。</param>
        public static void DrawReferenceImageSectionTitle(
            string title,
            int currentCount,
            int maxCount,
            Action drawBeforeCount = null)
        {
            currentCount = Mathf.Max(0, currentCount);
            maxCount = Mathf.Max(1, maxCount);
            if (currentCount > maxCount)
                currentCount = maxCount;

            GUILayout.BeginHorizontal();
            DrawSectionTitle(title ?? TJGeneratorsL10n.L("参考图片（可选）"), uppercase: false);
            GUILayout.FlexibleSpace();
            if (drawBeforeCount != null)
            {
                drawBeforeCount();
                GUILayout.Space(CommonStyles.Space2);
            }
            GUILayout.Label($"{currentCount}/{maxCount}", CommonStyles.TargetObjectButtonLabelStyle);
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 绘制样式化下拉框（触发器 + 独立弹出层）。
        /// </summary>
        public static int DrawStyledDropdown(
            string dropdownId,
            int selectedIndex,
            IList<string> options,
            int separatorBeforeIndex = -1,
            float panelTopGap = 4f,
            float hoverInset = 2f,
            float dropdownWidth = -1f)
        {
            float resolvedWidth = dropdownWidth < 0f ? CommonStyles.LeftComponentWidth : dropdownWidth;
            float triggerHeight = 30f;
            float textPaddingX = 12f;
            float arrowSize = 14f;
            float rightPadding = 12f;
            float scaledPanelTopGap = panelTopGap;
            float scaledHoverInset = hoverInset;

            if (options == null || options.Count == 0)
                return selectedIndex;

            if (!string.IsNullOrEmpty(dropdownId) && _dropdownPendingSelections.TryGetValue(dropdownId, out var pending))
            {
                selectedIndex = Mathf.Clamp(pending, 0, options.Count - 1);
                _dropdownPendingSelections.Remove(dropdownId);
            }

            selectedIndex = Mathf.Clamp(selectedIndex, 0, options.Count - 1);

            Rect triggerRect = resolvedWidth > 0f
                ? GUILayoutUtility.GetRect(
                    GUIContent.none,
                    CommonStyles.DropDownTriggerStyle,
                    GUILayout.Width(resolvedWidth),
                    GUILayout.MinWidth(resolvedWidth),
                    GUILayout.MaxWidth(resolvedWidth),
                    GUILayout.Height(triggerHeight))
                : GUILayoutUtility.GetRect(
                    GUIContent.none,
                    CommonStyles.DropDownTriggerStyle,
                    GUILayout.ExpandWidth(true),
                    GUILayout.Height(triggerHeight));

            DrawNineSliceBackground(
                triggerRect,
                CommonStyles.DropDownTriggerStyle.normal.background,
                CommonStyles.DropDownTriggerStyle.border.left,
                CommonStyles.DropDownTriggerStyle.normal.background != null ? CommonStyles.DropDownTriggerStyle.normal.background.height : triggerHeight);

            if (GUI.Button(triggerRect, GUIContent.none, GUIStyle.none))
            {
                // 先清除键盘焦点，确保当前正在编辑的 DelayedIntField / DelayedFloatField
                // 能在本帧提交其待定值，避免点击下拉框后数值回退。
                GUI.FocusControl(null);
                Rect popupAnchor = new Rect(triggerRect.x, triggerRect.y + scaledPanelTopGap, triggerRect.width, triggerRect.height);
                PopupWindow.Show(
                    popupAnchor,
                    new StyledDropdownPopup(
                        dropdownId,
                        options,
                        selectedIndex,
                        separatorBeforeIndex,
                        triggerRect.width,
                        Mathf.Max(0f, scaledHoverInset),
                        OnDropdownPopupSelected));
                Event.current.Use();
            }

            Rect textRect = new Rect(
                triggerRect.x + textPaddingX,
                triggerRect.y,
                Mathf.Max(20f, triggerRect.width - textPaddingX - rightPadding - arrowSize - 8f),
                triggerRect.height);
            GUI.Label(textRect, options[selectedIndex], CommonStyles.DropDownRowTextStyle);

            var arrow = CommonStyles.DropBoxArrow4xTexture;
            if (arrow != null)
            {
                Rect arrowRect = new Rect(
                    triggerRect.xMax - rightPadding - arrowSize,
                    triggerRect.y + (triggerRect.height - arrowSize) * 0.5f,
                    arrowSize,
                    arrowSize);
                GUI.DrawTexture(arrowRect, arrow, ScaleMode.ScaleToFit, true);
            }

            return selectedIndex;
        }

        private static void OnDropdownPopupSelected(string dropdownId, int selectedIndex)
        {
            if (string.IsNullOrEmpty(dropdownId))
                return;
            _dropdownPendingSelections[dropdownId] = selectedIndex;
        }

        private sealed class StyledDropdownPopup : PopupWindowContent
        {
            private readonly string _dropdownId;
            private readonly IList<string> _options;
            private readonly int _selectedIndex;
            private readonly int _separatorBeforeIndex;
            private readonly float _width;
            private readonly float _hoverInset;
            private readonly Action<string, int> _onSelected;

            public StyledDropdownPopup(
                string dropdownId,
                IList<string> options,
                int selectedIndex,
                int separatorBeforeIndex,
                float width,
                float hoverInset,
                Action<string, int> onSelected)
            {
                _dropdownId = dropdownId;
                _options = options;
                _selectedIndex = selectedIndex;
                _separatorBeforeIndex = separatorBeforeIndex;
                _width = Mathf.Max(120f, width);
                _hoverInset = Mathf.Max(0f, hoverInset);
                _onSelected = onSelected;
            }

            public override void OnOpen()
            {
                if (editorWindow != null)
                    editorWindow.wantsMouseMove = true;
            }

            public override Vector2 GetWindowSize()
            {
                float rowHeight = 30f;
                float panelPaddingY = 4f;
                float h = panelPaddingY * 2f + rowHeight * _options.Count;
                return new Vector2(_width, h);
            }

            public override void OnGUI(Rect rect)
            {
                float rowHeight = 30f;
                float panelPaddingY = 4f;
                float textPaddingX = 12f;

                if (Event.current.type == EventType.MouseMove && editorWindow != null)
                    editorWindow.Repaint();

                EditorGUI.DrawRect(rect, CommonStyles.WindowBackgroundColor);
                GUI.Box(rect, GUIContent.none, CommonStyles.DropDownPanelStyle);

                float currentY = rect.y + panelPaddingY;
                for (int i = 0; i < _options.Count; i++)
                {
                    Rect rowRect = new Rect(rect.x, currentY, rect.width, rowHeight);
                    bool isHover = rowRect.Contains(Event.current.mousePosition);
                    if (isHover || i == _selectedIndex)
                    {
                        Rect hoverRect = new Rect(
                            rowRect.x + _hoverInset,
                            rowRect.y,
                            Mathf.Max(0f, rowRect.width - _hoverInset * 2f),
                            rowRect.height);

                        if (CommonStyles.DropDownFrameSelected2xTexture != null)
                            GUI.DrawTexture(hoverRect, CommonStyles.DropDownFrameSelected2xTexture, ScaleMode.StretchToFill, true);
                        else
                            EditorGUI.DrawRect(hoverRect, CommonStyles.ThemeGreenColor);
                    }

                    Rect rowTextRect = new Rect(rowRect.x + textPaddingX, rowRect.y, rowRect.width - textPaddingX * 2f, rowRect.height);
                    GUI.Label(rowTextRect, _options[i], CommonStyles.DropDownRowTextStyle);

                    if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && rowRect.Contains(Event.current.mousePosition))
                    {
                        _onSelected?.Invoke(_dropdownId, i);
                        editorWindow.Close();
                        Event.current.Use();
                        return;
                    }

                    currentY += rowHeight;
                }
            }
        }

        public static string DrawSearchTextField(string value, string placeholder, params GUILayoutOption[] options)
        {
            float height = 44f;
            float padding = 10f;
            float iconSize = 18f;
            float iconGap = 10f;

            Rect rect = GUILayoutUtility.GetRect(
                GUIContent.none,
                CommonStyles.SearchTextFieldStyle,
                options != null && options.Length > 0
                    ? options
                    : new[] { GUILayout.ExpandWidth(true), GUILayout.Height(height) });

            // 背景（4x 九宫格，radius=4 -> sourceBorder=16）
            var bgTex = CommonStyles.SearchTextFieldStyle.normal.background;
            if (bgTex != null)
                DrawNineSliceScaled(rect, bgTex, 16, bgTex.height);

            // 图标
            var searchIcon = CommonStyles.SearchIcon4xTexture;
            float iconX = rect.x + padding;
            if (searchIcon != null)
            {
                Rect iconRect = new Rect(iconX, rect.y + (rect.height - iconSize) * 0.5f, iconSize, iconSize);
                GUI.DrawTexture(iconRect, searchIcon, ScaleMode.ScaleToFit, true);
            }

            // 输入区域
            float textX = rect.x + padding + iconSize + iconGap;
            Rect textRect = new Rect(
                textX,
                rect.y + 1f,
                Mathf.Max(1f, rect.xMax - padding - textX),
                rect.height - 2f);

            EditorGUIUtility.AddCursorRect(textRect, MouseCursor.Text);

            var textStyle = new GUIStyle(GUIStyle.none)
            {
                font = CommonStyles.SourceHanSansMediumFont != null ? CommonStyles.SourceHanSansMediumFont : CommonStyles.SourceHanSansRegularFont,
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                wordWrap = false,
                padding = new RectOffset(0, 0, 0, 0)
            };
            textStyle.normal.textColor = Color.white;
            textStyle.hover.textColor = Color.white;
            textStyle.active.textColor = Color.white;
            textStyle.focused.textColor = Color.white;

            string current = value ?? string.Empty;
            string newValue = EditorGUI.TextField(textRect, current, textStyle);

            if (ShouldShowEmptyTextPlaceholder(newValue) && !string.IsNullOrEmpty(placeholder))
            {
                var placeholderStyle = new GUIStyle(textStyle);
                placeholderStyle.normal.textColor = new Color(102f / 255f, 102f / 255f, 102f / 255f, 1f);
                GUI.Label(textRect, placeholder, placeholderStyle);
            }

            return newValue;
        }

        /// <summary>
        /// 在指定矩形内绘制生成按钮。
        /// </summary>
        public static bool DrawGenerateButtonAt(
            Rect buttonRect,
            string text,
            bool enabled,
            bool isBusy = false)
        {
            ResolveGenerateButtonVisuals(enabled, isBusy, out GUIStyle buttonStyle, out GUIStyle labelStyle, out bool isDisabled);

            GUI.enabled = enabled;
            bool clicked = enabled && GUI.Button(buttonRect, GUIContent.none, GUIStyle.none);
            GUI.enabled = true;
            DrawGenerateButtonNineSliceBackground(buttonRect, buttonStyle, enabled, isDisabled);

            string title = string.IsNullOrEmpty(text) ? TJGeneratorsL10n.L("生成模型") : text;
            Vector2 titleSize = labelStyle.CalcSize(new GUIContent(title));
            float midY = buttonRect.center.y;
            float labelX = buttonRect.x + Mathf.Max(0f, (buttonRect.width - titleSize.x) * 0.5f);
            GUI.Label(new Rect(labelX, midY - titleSize.y * 0.5f, titleSize.x, titleSize.y), title, labelStyle);
            return clicked;
        }

        /// <summary>
        /// 绘制生成按钮。
        /// </summary>
        public static bool DrawGenerateButton(
            string text,
            bool enabled,
            bool isBusy = false,
            params GUILayoutOption[] options)
        {
            ResolveGenerateButtonVisuals(enabled, isBusy, out GUIStyle btnStyle, out _, out _);
            GUI.enabled = enabled;
            Rect buttonRect = GUILayoutUtility.GetRect(
                GUIContent.none,
                btnStyle,
                options != null && options.Length > 0 ? options : new[] { GUILayout.ExpandWidth(true), GUILayout.Height(LeftPanelBottomDock.ActionButtonHeight) });
            GUI.enabled = true;
            return DrawGenerateButtonAt(buttonRect, text, enabled, isBusy);
        }

        /// <summary>
        /// 在指定矩形内绘制带前置图标的绿色主操作按钮（生成、搜索等）。
        /// </summary>
        public static bool DrawGenerateButtonWithIconAt(
            Rect buttonRect,
            string text,
            Texture2D icon,
            bool enabled,
            float iconSize = 16f,
            float iconTextGap = 6f,
            bool isBusy = false)
        {
            ResolveGenerateButtonVisuals(enabled, isBusy, out GUIStyle buttonStyle, out GUIStyle labelStyle, out bool isDisabled);

            GUI.enabled = enabled;
            bool clicked = enabled && GUI.Button(buttonRect, GUIContent.none, GUIStyle.none);
            GUI.enabled = true;
            DrawGenerateButtonNineSliceBackground(buttonRect, buttonStyle, enabled, isDisabled);

            string safeText = string.IsNullOrEmpty(text) ? TJGeneratorsL10n.L("生成") : text;
            Vector2 textSize = labelStyle.CalcSize(new GUIContent(safeText));
            float safeIconSize = Mathf.Max(0f, iconSize);
            bool hasIcon = safeIconSize > 0f && icon != null;
            float iconStride = hasIcon ? safeIconSize + Mathf.Max(0f, iconTextGap) : 0f;
            float x = buttonRect.x + Mathf.Max(0f, (buttonRect.width - iconStride - textSize.x) * 0.5f);
            float midY = buttonRect.center.y;

            if (hasIcon)
            {
                GUI.color = isDisabled ? new Color(1f, 1f, 1f, 0.25f) : GUI.color;
                GUI.DrawTexture(new Rect(x, midY - safeIconSize * 0.5f, safeIconSize, safeIconSize), icon, ScaleMode.ScaleToFit, true);
                GUI.color = Color.white;
                x += iconStride;
            }

            GUI.Label(new Rect(x, midY - textSize.y * 0.5f, textSize.x, textSize.y), safeText, labelStyle);
            return clicked;
        }

        private static void ResolveGenerateButtonVisuals(
            bool enabled,
            bool isBusy,
            out GUIStyle buttonStyle,
            out GUIStyle labelStyle,
            out bool isDisabled)
        {
            isDisabled = !enabled && !isBusy;
            if (isBusy)
                buttonStyle = CommonStyles.GenerateButtonBusyStyle;
            else if (enabled)
                buttonStyle = CommonStyles.GenerateButtonSolidStyle;
            else
                buttonStyle = CommonStyles.GenerateButtonDisabledStyle;

            labelStyle = isDisabled
                ? CommonStyles.GenerateButtonLabelDisabledStyle
                : CommonStyles.GenerateButtonLabelStyle;
        }

        private static void DrawGenerateButtonNineSliceBackground(Rect rect, GUIStyle style, bool enabled, bool isDisabled)
        {
            Texture2D tex = ResolveGenerateButtonTexture(style, rect, enabled, isDisabled);
            if (tex == null)
                return;

            if (isDisabled)
            {
                DrawNineSliceScaled(
                    rect,
                    tex,
                    CommonStyles.GenerateButtonDisabledSourceBorder,
                    CommonStyles.GenerateButtonDisabledReferenceHeight);
                return;
            }

            const int sourceBorder = 32;
            const float referenceHeight = 160f;
            DrawNineSliceScaled(rect, tex, sourceBorder, referenceHeight);
        }

        private static Texture2D ResolveGenerateButtonTexture(GUIStyle style, Rect rect, bool enabled, bool isDisabled)
        {
            SyncImguiLeftMouseHeldFromEvent();

            Texture2D normal = style.normal.background;
            Texture2D hover = style.hover.background != null ? style.hover.background : normal;
            Texture2D active = style.active.background != null ? style.active.background : hover;
            if (isDisabled || !enabled)
                return normal != null ? normal : active;

            bool isHover = rect.Contains(Event.current.mousePosition);
            bool isPressing = isHover && ImguiLeftMouseHeld;
            if (isPressing)
                return active != null ? active : hover;
            if (isHover)
                return hover != null ? hover : normal;
            return normal;
        }

        /// <summary>
        /// 九宫格各块之间的默认重叠（约 1px），减轻舍入导致的接缝线。
        /// </summary>
        private static float DefaultNineSliceOverlapPixels => Mathf.Max(0.5f, 1f);

        /// <summary>
        /// overlapPx：负数表示使用 <see cref="DefaultNineSliceOverlapPixels"/>；0 表示块与块之间不重叠。
        /// </summary>
        private static float ResolveNineSliceOverlap(float overlapPx)
        {
            if (overlapPx < 0f)
                return DefaultNineSliceOverlapPixels;
            return Mathf.Max(0f, overlapPx);
        }

        /// <summary>
        /// 将矩形对齐到物理像素网格，减轻 <see cref="GUI.DrawTextureWithTexCoords"/> 在子像素坐标下
        /// 左/上缘与右/下缘采样不对称（常见表现为边框或虚线「上左细、右下粗」）。
        /// </summary>
        private static Rect SnapRectToPixelGrid(Rect r)
        {
            float ppp = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
            float Snap(float v) => Mathf.Round(v * ppp) / ppp;
            float xMin = Snap(r.xMin);
            float yMin = Snap(r.yMin);
            float xMax = Snap(r.xMax);
            float yMax = Snap(r.yMax);
            float minSpan = 1f / ppp;
            if (xMax - xMin < minSpan)
                xMax = xMin + minSpan;
            if (yMax - yMin < minSpan)
                yMax = yMin + minSpan;
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        /// <summary>
        /// 统一九宫格绘制入口：
        /// - sourceBorder: 源贴图切片边界（像素）
        /// - fixedDestBorder > 0 时使用固定目标边界；否则按 referenceHeight 等比缩放
        /// - overlapPx: 负数（默认）为 <see cref="DefaultNineSliceOverlapPixels"/> 填充重叠；0 为关闭
        /// </summary>
        public static void DrawNineSlice(
            Rect targetRect,
            Texture2D texture,
            int sourceBorder,
            float referenceHeight,
            int fixedDestBorder = -1,
            float overlapPx = -1f)
        {
            if (texture == null)
                return;

            targetRect = SnapRectToPixelGrid(targetRect);

            int destBorder = fixedDestBorder > 0
                ? fixedDestBorder
                // 缩放时用 Floor，避免 8.8 -> 9 这类上舍入导致切线不连续出现固定竖缝
                : Mathf.Max(1, Mathf.FloorToInt(sourceBorder * (targetRect.height / Mathf.Max(1f, referenceHeight))));
            DrawNineSliceRemap(targetRect, texture, sourceBorder, destBorder, ResolveNineSliceOverlap(overlapPx));
        }

        /// <summary>
        /// 便捷重载：按 referenceHeight 自动缩放目标边界。
        /// </summary>
        public static void DrawNineSliceScaled(
            Rect targetRect,
            Texture2D texture,
            int sourceBorder,
            float referenceHeight,
            float overlapPx = -1f)
        {
            DrawNineSlice(targetRect, texture, sourceBorder, referenceHeight, -1, overlapPx);
        }

        /// <summary>
        /// 便捷重载：使用固定目标边界。
        /// </summary>
        public static void DrawNineSliceFixed(
            Rect targetRect,
            Texture2D texture,
            int sourceBorder,
            int fixedDestBorder,
            float overlapPx = -1f)
        {
            DrawNineSlice(targetRect, texture, sourceBorder, Mathf.Max(1f, targetRect.height), fixedDestBorder, overlapPx);
        }

        private static void DrawNineSliceRemap(Rect targetRect, Texture2D texture, int sourceBorder, int destBorder, float overlapPx)
        {
            int src = Mathf.Clamp(sourceBorder, 1, Mathf.Min(texture.width / 2 - 1, texture.height / 2 - 1));
            float dst = Mathf.Clamp(destBorder, 1f, Mathf.Min((targetRect.width - 1f) * 0.5f, (targetRect.height - 1f) * 0.5f));
            float overlap = Mathf.Max(0f, overlapPx);
            if (overlap > 0f && dst >= 1f)
            {
                // 大角区时若重叠仍取 ~1px，边/中心会过多侵入角区，虚线边框内侧易被盖住（上/左边更细），
                // 圆弧过渡也易被邻片采样“折线化”。随 dst 缩小允许的最大重叠。
                float capByCorner = Mathf.Clamp(2.0f / dst, 0.1f, 1f);
                overlap = Mathf.Min(overlap, capByCorner);
            }

            // 用“宽高拆三段 + xMax/yMax 串联”保证 patch 之间无缝拼接（避免 1px 缝/线）。
            float leftW = dst;
            float rightW = dst;
            float midW = Mathf.Max(0f, targetRect.width - leftW - rightW);
            float topH = dst;
            float bottomH = dst;
            float midH = Mathf.Max(0f, targetRect.height - topH - bottomH);

            Rect r00 = new Rect(targetRect.x, targetRect.y, leftW, topH);
            Rect r10 = new Rect(r00.xMax - overlap, targetRect.y, midW + overlap * 2f, topH);
            Rect r20 = new Rect(r10.xMax - overlap, targetRect.y, rightW + overlap, topH);

            Rect r01 = new Rect(targetRect.x, r00.yMax - overlap, leftW, midH + overlap * 2f);
            Rect r11 = new Rect(r01.xMax - overlap, r10.yMax - overlap, midW + overlap * 2f, midH + overlap * 2f);
            Rect r21 = new Rect(r11.xMax - overlap, r20.yMax - overlap, rightW + overlap, midH + overlap * 2f);

            Rect r02 = new Rect(targetRect.x, r01.yMax - overlap, leftW, bottomH + overlap);
            Rect r12 = new Rect(r02.xMax - overlap, r11.yMax - overlap, midW + overlap * 2f, bottomH + overlap);
            Rect r22 = new Rect(r12.xMax - overlap, r21.yMax - overlap, rightW + overlap, bottomH + overlap);

            // 绘制顺序：先中区，再四边，最后四角。
            // - 若中区在边条之后画，r11 会叠在 r10/r01 向内的重叠带上，易吃掉贴图里靠内侧的虚线/描边（常见为顶、左边更细）。
            // - 边条仍会叠在角块矩形上；四角最后绘制，盖住侵入的直边采样，保留圆角柔和过渡。
            DrawPatchPx(r11, texture, src, src, texture.width - src * 2, texture.height - src * 2, insetLeft: false, insetRight: false, insetTop: false, insetBottom: false);
            DrawPatchPx(r10, texture, src, 0, texture.width - src * 2, src, insetLeft: false, insetRight: false, insetTop: true, insetBottom: false);
            DrawPatchPx(r12, texture, src, texture.height - src, texture.width - src * 2, src, insetLeft: false, insetRight: false, insetTop: false, insetBottom: true);
            DrawPatchPx(r01, texture, 0, src, src, texture.height - src * 2, insetLeft: true, insetRight: false, insetTop: false, insetBottom: false);
            DrawPatchPx(r21, texture, texture.width - src, src, src, texture.height - src * 2, insetLeft: false, insetRight: true, insetTop: false, insetBottom: false);

            DrawPatchPx(r00, texture, 0, 0, src, src, insetLeft: true, insetRight: false, insetTop: true, insetBottom: false);
            DrawPatchPx(r20, texture, texture.width - src, 0, src, src, insetLeft: false, insetRight: true, insetTop: true, insetBottom: false);
            DrawPatchPx(r02, texture, 0, texture.height - src, src, src, insetLeft: true, insetRight: false, insetTop: false, insetBottom: true);
            DrawPatchPx(r22, texture, texture.width - src, texture.height - src, src, src, insetLeft: false, insetRight: true, insetTop: false, insetBottom: true);
        }

        private static void DrawPatchPx(
            Rect targetRect,
            Texture2D texture,
            int sx,
            int sy,
            int sw,
            int sh,
            bool insetLeft,
            bool insetRight,
            bool insetTop,
            bool insetBottom)
        {
            if (targetRect.width <= 0f || targetRect.height <= 0f || sw <= 0 || sh <= 0)
                return;

            float texW = texture.width;
            float texH = texture.height;
            float insetX = 0.5f / texW;
            float insetY = 0.5f / texH;

            float uMin = (sx / texW) + (insetLeft ? insetX : 0f);
            float uMax = ((sx + sw) / texW) - (insetRight ? insetX : 0f);
            // 注意：Unity 的 UV v 轴从下到上；sy 是从贴图顶部开始的像素坐标
            float vMin = 1f - ((sy + sh) / texH) + (insetBottom ? insetY : 0f);
            float vMax = 1f - (sy / texH) - (insetTop ? insetY : 0f);
            Rect uvRect = Rect.MinMaxRect(uMin, vMin, uMax, vMax);
            GUI.DrawTextureWithTexCoords(targetRect, texture, uvRect, true);
        }

        private static void DrawNineSliceBackground(
            Rect rect,
            Texture2D texture,
            int sourceBorder,
            float referenceHeight,
            int fixedDestBorder = -1,
            float overlapPx = -1f)
        {
            DrawNineSlice(rect, texture, Mathf.Max(1, sourceBorder), Mathf.Max(1f, referenceHeight), fixedDestBorder, overlapPx);
        }

        /// <summary>
        /// 在左栏底部 Dock 布局内绘制生成按钮。
        /// </summary>
        public static void DrawGenerationSectionAt(
            LeftPanelBottomDock.Layout layout,
            bool isGenerating,
            float progress,
            string status,
            bool canGenerate,
            Action onGenerate,
            Action drawExtraBetweenButtonAndProgress = null,
            Action repaint = null,
            string idleButtonLabel = null)
        {
            bool playBlocked = TJGeneratorsPlayModeGuard.IsActive;
            bool canClick = !isGenerating && canGenerate && !playBlocked;
            string idle = string.IsNullOrEmpty(idleButtonLabel) ? TJGeneratorsL10n.L("生成") : idleButtonLabel;
            string buttonText = isGenerating ? TJGeneratorsL10n.L("生成中...") : idle;
            bool clicked = DrawGenerateButtonAt(
                layout.buttonRect,
                buttonText,
                canClick,
                isGenerating);

            if (clicked)
            {
                GUI.FocusControl(null);
                Action callback = onGenerate;
                EditorApplication.delayCall += () =>
                {
                    EditorApplication.delayCall += () => callback?.Invoke();
                };
            }

            drawExtraBetweenButtonAndProgress?.Invoke();
        }

        /// <summary>软引导链接样式：基于 <see cref="CommonStyles.LinkStyle"/> 但不加粗。</summary>
        private static GUIStyle s_softLinkStyle;
        private static GUIStyle SoftLinkStyle
        {
            get
            {
                if (s_softLinkStyle == null)
                {
                    s_softLinkStyle = new GUIStyle(CommonStyles.LinkStyle)
                    {
                        fontStyle = FontStyle.Normal
                    };
                }
                return s_softLinkStyle;
            }
        }

        /// <summary>
        /// 绘制历史记录为空时的占位提示，并附带「怎么用？」软引导链接，点击打开使用文档。
        /// </summary>
        public static void DrawHistoryEmptyState()
        {
            GUILayout.Label(TJGeneratorsL10n.L("暂无历史记录"), CommonStyles.CenteredGreyLabelStyle);
            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(TJGeneratorsL10n.L("第一次来？看看怎么用 →"), SoftLinkStyle))
                TJGeneratorsDocs.OpenDocumentation();
            AddLinkCursorToLastRect();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// 在指定矩形内绘制“生成中”旋转加载图标，用于历史项等占位。
        /// </summary>
        /// <param name="rect">绘制区域</param>
        /// <param name="fallbackStyle">图标不可用时使用的文字样式，默认 CommonStyles.SmallGreyCenterLabelStyle</param>
        /// <param name="repaint">可选，用于驱动动画的刷新回调</param>
        public static void DrawLoadingSpinner(
            Rect rect,
            GUIStyle fallbackStyle = null,
            Action repaint = null
        )
        {
            if (Event.current.type == EventType.Repaint)
            {
                var spinIcon = EditorGUIUtility.IconContent("Loading");
                if (spinIcon != null && spinIcon.image != null)
                {
                    var iconRect = new Rect(
                        rect.x + rect.width / 4,
                        rect.y + rect.height / 4,
                        rect.width / 2,
                        rect.height / 2
                    );
                    float angle = (float)(EditorApplication.timeSinceStartup * 180) % 360f;
                    var matrixBackup = GUI.matrix;
                    GUIUtility.RotateAroundPivot(angle, iconRect.center);
                    GUI.DrawTexture(iconRect, spinIcon.image, ScaleMode.ScaleToFit);
                    GUI.matrix = matrixBackup;
                }
                else
                {
                    var style = fallbackStyle ?? CommonStyles.SmallGreyCenterLabelStyle;
                    GUI.Label(rect, TJGeneratorsL10n.L("生成中..."), style);
                }
            }
            repaint?.Invoke();
        }

        /// <summary>
        /// 绘制矩形边框（上、下、左、右四条边）。
        /// </summary>
        /// <param name="rect">目标矩形</param>
        /// <param name="color">边框颜色</param>
        /// <param name="thickness">边框厚度，默认 1</param>
        public static void DrawRectOutline(Rect rect, Color color, float thickness = 1f)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            EditorGUI.DrawRect(
                new Rect(rect.x, rect.yMax - thickness, rect.width, thickness),
                color
            );
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            EditorGUI.DrawRect(
                new Rect(rect.xMax - thickness, rect.y, thickness, rect.height),
                color
            );
        }

        public static bool DrawAdvancedStyleBoolRow(string label, bool value)
            => AdvancedSettingsComponents.DrawAdvancedStyleBoolRow(label, value);

        public static string DrawAdvancedStyleTextRow(string label, string value)
            => AdvancedSettingsComponents.DrawAdvancedStyleTextRow(label, value);

        public static int DrawAdvancedStyleIntRow(string label, int value, string controlName = null)
            => AdvancedSettingsComponents.DrawAdvancedStyleIntRow(label, value, controlName);

        public static float DrawAdvancedStyleSliderRow(string label, float value, float min, float max)
            => AdvancedSettingsComponents.DrawAdvancedStyleSliderRow(label, value, min, max);

        public static bool DrawSettingsFoldout(
            bool expanded,
            string foldoutLabel,
            Action drawExpandedContent,
            bool uppercaseLabel = true)
            => AdvancedSettingsComponents.DrawSettingsFoldout(expanded, foldoutLabel, drawExpandedContent, uppercaseLabel);

        public static bool DrawAdvancedSettingsFoldout(
            bool expanded,
            IGeneratorParameterProvider provider,
            List<ParameterConfig> parameters,
            string foldoutLabel = null)
            => AdvancedSettingsComponents.DrawAdvancedSettingsFoldout(expanded, provider, parameters, foldoutLabel ?? TJGeneratorsL10n.L("高级设置"));

        /// <summary>主生成窗口默认打开宽度。</summary>
        public const float DefaultMainWindowWidth = 1000f;
        /// <summary>主生成窗口默认打开高度。</summary>
        public const float DefaultMainWindowHeight = 900f;

        /// <summary>
        /// 主编辑器窗口内居中矩形；连续打开多个 Codely 主窗口时在中心基础上叠加偏移（与生成器窗口一致）。
        /// </summary>
        private static int _mainWindowOpenCountForDefaultRect;

        public static Rect GetDefaultMainWindowRect()
        {
            return GetDefaultMainWindowRect(
                DefaultMainWindowWidth,
                DefaultMainWindowHeight,
                24f,
                20);
        }

        /// <summary>
        /// 主编辑器窗口内居中矩形；连续打开多个 Codely 主窗口时在中心基础上叠加偏移（与生成器窗口一致）。
        /// <paramref name="width"/>、<paramref name="height"/>、<paramref name="stackOffset"/> 为最终布局像素，不再二次缩放。
        /// 尺寸超过主编辑器窗口时会先钳制，避免窗口顶到上沿后仍保持过高而“顶天立地”。
        /// </summary>
        public static Rect GetDefaultMainWindowRect(float width, float height, float stackOffset, int wrapCount = 20)
        {
            Rect mainRect = EditorGUIUtilityCompat.GetMainWindowPosition();

            width = Mathf.Min(width, mainRect.width);
            height = Mathf.Min(height, mainRect.height);

            float offset = stackOffset * _mainWindowOpenCountForDefaultRect;
            var rect = new Rect(
                mainRect.x + (mainRect.width - width) * 0.5f + offset,
                mainRect.y + (mainRect.height - height) * 0.5f + offset,
                width,
                height);

            float maxX = Mathf.Max(mainRect.x, mainRect.xMax - rect.width);
            float maxY = Mathf.Max(mainRect.y, mainRect.yMax - rect.height);
            rect.x = Mathf.Clamp(rect.x, mainRect.x, maxX);
            rect.y = Mathf.Clamp(rect.y, mainRect.y, maxY);

            _mainWindowOpenCountForDefaultRect =
                (_mainWindowOpenCountForDefaultRect + 1) % Mathf.Max(1, wrapCount);
            return rect;
        }

        /// <summary>
        /// 固定左右分栏布局的计算结果：
        /// - 左侧宽度固定
        /// - 右侧宽度自适应并受最小宽度保护
        /// </summary>
        public struct FixedSplitLayoutParams
        {
            public float LeftPanelWidth;
            public float RightPanelWidth;
            public float GapWidth;
            public float WindowMinWidth;
            public float WindowMinHeight;
        }

        /// <summary>
        /// 计算固定左右分栏布局参数。
        /// 左侧固定宽度，右侧根据窗口宽度拉伸，并保证不小于 minRightPanelWidth。
        /// </summary>
        public static FixedSplitLayoutParams CalculateFixedSplitLayout(
            float windowWidth,
            float minWindowHeight,
            float leftPanelFixedWidth,
            float minRightPanelWidth,
            float gapWidth)
        {
            float safeLeft = Mathf.Max(1f, leftPanelFixedWidth);
            float safeRightMin = Mathf.Max(1f, minRightPanelWidth);
            float safeGap = Mathf.Max(0f, gapWidth);
            float windowMinWidth = safeLeft + safeGap + safeRightMin;
            float rightWidth = Mathf.Max(safeRightMin, windowWidth - safeLeft - safeGap);
            return new FixedSplitLayoutParams
            {
                LeftPanelWidth = safeLeft,
                RightPanelWidth = rightWidth,
                GapWidth = safeGap,
                WindowMinWidth = windowMinWidth,
                WindowMinHeight = Mathf.Max(1f, minWindowHeight)
            };
        }

        /// <summary>
        /// 开始历史面板（右侧面板），自动处理顶部间距和内边距
        /// </summary>
        /// <param name="panelWidth">面板宽度</param>
        /// <param name="panelHeight">面板高度</param>
        /// <param name="isVerticalLayout">是否为垂直布局</param>
        public static void BeginHistoryPanel(float panelWidth, float panelHeight, bool isVerticalLayout)
        {
            GUILayout.BeginVertical(GUILayout.Width(panelWidth), GUILayout.MinWidth(panelWidth), GUILayout.Height(panelHeight));
            GUILayout.BeginVertical(CommonStyles.GetHistoryPanelStyle(isVerticalLayout));
        }

        /// <summary>
        /// 结束历史面板（右侧面板）
        /// </summary>
        public static void EndHistoryPanel()
        {
            GUILayout.EndVertical();
            GUILayout.EndVertical();
        }

        /// <summary>
        /// 在历史面板顶部绘制选中条目的纹理预览。
        /// - 单列（垂直布局）时，高度固定为历史面板高度的一半；
        /// - 双列（水平布局）时，高度最多 200，且不超过可用宽度。
        /// 返回整个预览区（含下方间距）的高度，供调用方计算滚动区域高度。
        /// </summary>
        public static float DrawHistoryTexturePreview(
            Texture2D previewTex,
            bool showPreviewArea,
            bool isVerticalLayout,
            float panelWidth,
            float historyPanelHeight)
        {
            if (!showPreviewArea)
                return 0f;

            float maxPreviewWidth = Mathf.Max(0f, CommonStyles.HistoryPanelInnerWidth(panelWidth));
            if (maxPreviewWidth <= 0f || historyPanelHeight <= 0f)
                return 0f;

            float previewHeight = isVerticalLayout
                ? Mathf.Max(0f, historyPanelHeight * 0.5f)
                : Mathf.Min(maxPreviewWidth, 200f);

            float previewWidth = Mathf.Min(maxPreviewWidth, previewHeight);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var previewRect = GUILayoutUtility.GetRect(previewWidth, previewHeight);
            if (previewTex != null && Event.current.type == EventType.Repaint)
                GUI.DrawTexture(previewRect, previewTex, ScaleMode.ScaleToFit);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(5f);

            return previewHeight + 5f;
        }

        /// <summary>
        /// 绘制两列自适应布局的背景色（含分割线）
        /// </summary>
        /// <param name="windowRect">窗口矩形区域</param>
        /// <param name="isVerticalLayout">是否为垂直布局</param>
        /// <param name="leftPanelWidth">左侧面板宽度（水平布局时使用）</param>
        /// <param name="settingsPanelHeight">设置面板高度（垂直布局时使用）</param>
        public static void DrawAdaptiveLayoutBackground(
            Rect windowRect,
            bool isVerticalLayout,
            float leftPanelWidth,
            float settingsPanelHeight)
        {
            const float separatorThickness = 1f;

            if (isVerticalLayout)
            {
                Rect topSeparatorRect = new Rect(0, 0, windowRect.width, separatorThickness);
                Rect topRect = new Rect(0, separatorThickness, windowRect.width, settingsPanelHeight - separatorThickness);
                Rect middleSeparatorRect = new Rect(0, settingsPanelHeight, windowRect.width, separatorThickness);
                Rect bottomRect = new Rect(0, settingsPanelHeight + separatorThickness, windowRect.width, windowRect.height - settingsPanelHeight - separatorThickness);
                EditorGUI.DrawRect(topSeparatorRect, CommonStyles.LayoutSeparatorColor);
                EditorGUI.DrawRect(topRect, CommonStyles.WindowBackgroundColor);
                EditorGUI.DrawRect(middleSeparatorRect, CommonStyles.LayoutSeparatorColor);
                EditorGUI.DrawRect(bottomRect, CommonStyles.EmptyAreaBackgroundColor);
            }
            else
            {
                Rect leftTopSeparatorRect = new Rect(0, 0, leftPanelWidth, separatorThickness);
                Rect leftRect = new Rect(0, separatorThickness, leftPanelWidth, windowRect.height - separatorThickness);
                Rect middleSeparatorRect = new Rect(leftPanelWidth, 0, separatorThickness, windowRect.height);
                Rect rightTopSeparatorRect = new Rect(leftPanelWidth + separatorThickness, 0, windowRect.width - leftPanelWidth - separatorThickness, separatorThickness);
                Rect rightRect = new Rect(leftPanelWidth + separatorThickness, separatorThickness, windowRect.width - leftPanelWidth - separatorThickness, windowRect.height - separatorThickness);
                EditorGUI.DrawRect(leftTopSeparatorRect, CommonStyles.LayoutSeparatorColor);
                EditorGUI.DrawRect(leftRect, CommonStyles.WindowBackgroundColor);
                EditorGUI.DrawRect(middleSeparatorRect, CommonStyles.LayoutSeparatorColor);
                EditorGUI.DrawRect(rightTopSeparatorRect, CommonStyles.LayoutSeparatorColor);
                EditorGUI.DrawRect(rightRect, CommonStyles.EmptyAreaBackgroundColor);
            }
        }
    }
}
#endif
