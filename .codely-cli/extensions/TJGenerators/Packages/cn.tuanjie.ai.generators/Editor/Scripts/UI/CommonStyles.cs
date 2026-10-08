#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace TJGenerators.UI
{
    /// <summary>
    /// 各生成器窗口共用的样式与纹理（headerStyle、buttonStyle、textFieldStyle 等），
    /// 供各生成器窗口统一使用，避免重复定义与视觉不一致。
    /// </summary>
    public static class CommonStyles
    {
        private const string TextureBasePath = "Packages/cn.tuanjie.ai.generators/Editor/EditorTextures/";
        private const string TextToModelInputPath = "TextToModelInput.png";
        private const string ImageUploadPath = "ImageUpload.png";
        private const string PreviewImageDefaultPath = "PreviewImageDefault.png";
        private const string SeparationLinePath = "SeparationLine.png";
        private const string SearchInputBox4xPath = "Frames/search_input_box_4x.png";
        private const string SearchIcon4xPath = "Icons/search_icon_4x.png";
        private const string GapLinePath = "Frames/GapLine.png";
        private const string GreenBtnNormal4xPath = "Button/green_btn_normal_4x.png";
        private const string GreenBtnHover4xPath = "Button/green_btn_hover_4x.png";
        private const string GreenBtnPressed4xPath = "Button/green_btn_pressed_4x.png";
        private const string BlackBtnNormal4xPath = "Button/black_btn_normal_4x.png";
        private const string GreyBtnDisablePath = "Button/grey_btn_disable.png";
        private const string GreenButtonPath = "GreenButton.png";
        private const string MutiViewBoxPath = "MutiViewBox.png";
        private const string MutiViewSelectedBoxPath = "MutiViewSelectedBox.png";
        private const string CheckboxOffNormalPath = "Checkbox/CheckboxOffNormal.png";
        private const string CheckboxOffHoverPath = "Checkbox/CheckboxOffHover.png";
        private const string CheckboxOnNormalPath = "Checkbox/CheckboxOnNormal.png";
        private const string CheckboxOnHoverPath = "Checkbox/CheckboxOnHover.png";
        private const string TargetIconPath = "Icons/TargetIcon.png";
        private const string TargetSelectorRectPath = "Icons/target_selector_rect.png";
        private const string ModelSelectorRectPath = "Frames/model_selector_rect.png";
        private const string SyncAltPath = "Icons/sync_alt.png";
        private const string UploadFrameNormal2xPath = "Frames/upload_frame_normal_2x.png";
        private const string UploadFrameHover2xPath = "Frames/upload_frame_hover_2x.png";
        private const string UploadFrameUploaded2xPath = "Frames/upload_frame_uploaded_2x.png";
        private const string InputBoxNormal2xPath = "Frames/input_box_normal_2x.png";
        private const string InputBoxLargePath = "Frames/input_box_large.png";
        private const string UploadImageIconPath = "Icons/UploadImageIcon.png";
        private const string ArrowGreen4xPath = "Icons/arrow_green_4x.png";
        private const string DropDownFrame2xPath = "Frames/drop_down_frame_2x.png";
        private const string DropDownFrameSelected2xPath = "Frames/drop_down_frame_selected_2x.png";
        private const string DropBoxArrow4xPath = "Icons/drop_box_arrow_4x.png";
        private const string DropBoxRightArrow4xPath = "Icons/keyboard_arrow_right.png";
        private const string EditIconPath = "Icons/edit.png";
        private const string CloseIconPath = "Icons/close_icon.png";
        private const string SettingInputBox2xPath = "Frames/setting_Input_box_2x.png";
        private const string ItemBoxNormal4xPath = "Frames/item_box_normal_4x.png";
        private const string ItemBoxChecked4xPath = "Frames/item_box_checked_4x.png";
        private const string FavoriteIconNormal4xPath = "Icons/favorite_icon_normal_4x.png";
        private const string FavoriteIconChecked4xPath = "Icons/favorite_icon_checked_4x.png";
        private const string TileTexturePath = "TileTexture.png";
        private const string TileTextureSelectedPath = "TileTextureSelected.png";
        private const string FontBasePath = "Packages/cn.tuanjie.ai.generators/Editor/Fonts/";
        private const string SourceHanSansRegularFontName = "SourceHanSansSC-Regular.otf";
        private const string SourceHanSansMediumFontName = "SourceHanSansSC-Medium.otf";

        /// <summary>窗口背景色 #222222</summary>
        public static readonly Color WindowBackgroundColor = new Color(34f / 255f, 34f / 255f, 34f / 255f, 1f);
        /// <summary>空区域背景色 #161616</summary>
        public static readonly Color EmptyAreaBackgroundColor = new Color(22f / 255f, 22f / 255f, 22f / 255f, 1f);
        /// <summary>布局分割线颜色 #333333</summary>
        public static readonly Color LayoutSeparatorColor = new Color(51f / 255f, 51f / 255f, 51f / 255f, 1f);
        /// <summary>字体颜色 #333333</summary>
        public static readonly Color FontGrayColor = new Color(156f / 255f, 163f / 255f, 175f / 255f, 1f);
        /// <summary>内容颜色 #D1D5DB</summary>
        public static readonly Color FontContentColor = new Color(209f / 255f, 213f / 255f, 219f / 255f, 1f);
        /// <summary>提示文字颜色 #6A717F</summary>
        public static readonly Color FontHintColor = new Color(106f / 255f, 113f / 255f, 127f / 255f, 1f);
        /// <summary>主题浅绿色 #0FC596</summary>
        public static readonly Color ThemeLightGreenColor = new Color(15f / 255f, 197f / 255f, 150f / 255f, 1f);
        /// <summary>主题主色 #01A77F</summary>
        public static readonly Color ThemeGreenColor = new Color(1f / 255f, 167f / 255f, 127f / 255f, 1f);
        /// <summary>主题深色 #006F4F</summary>
        public static readonly Color ThemeDarkGreenColor = new Color(0f / 255f, 111f / 255f, 79f / 255f, 1f);
        /// <summary>生成按钮禁用底图九宫格源边界（grey_btn_disable.png）。</summary>
        public const int GenerateButtonDisabledSourceBorder = 64;
        /// <summary>生成按钮禁用底图九宫格参考高度（grey_btn_disable.png 高度 396）。</summary>
        public const float GenerateButtonDisabledReferenceHeight = 396f;
        /// <summary>警告色 #FFA500</summary>
        public static readonly Color ThemeOrangeColor = new Color(1f, 0.7f, 0.3f, 1f);

        /// <summary>带边框选择按钮默认右侧图标尺寸。</summary>
        public const float FramedSelectButtonIconSize = 12f;
        /// <summary>keyboard_arrow_right 图标尺寸。</summary>
        public const float KeyboardArrowRightIconSize = 16f;
        /// <summary>edit 图标尺寸。</summary>
        public const float EditIconSize = 12f;
        /// <summary>灰色 #888888</summary>
        public static readonly Color ThemeGreyColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        /// <summary>生成按钮文字字号（“生成”“生成中...”）。</summary>
        public static int GenerateButtonFontSize => 16;
        /// <summary>生成按钮文字字重 </summary>
        public static FontStyle GenerateButtonFontStyle = FontStyle.Bold;
        /// <summary>清除按钮符号（图片/多视图槽位）</summary>
        public static readonly string ClearButtonSymbol = "×";
        /// <summary>加号占位符号（空槽位）</summary>
        public static readonly string PlusSymbol = "+";
        /// <summary>行之间的垂直间距（像素）</summary>
        public static float LineSpacing => 20f;
        /// <summary>统一间距节奏 1x </summary>
        public static float Space1 => 4f;
        /// <summary>统一间距节奏 2x </summary>
        public static float Space2 => 8f;
        /// <summary>统一间距节奏 4x </summary>
        public static float Space3 => 16f;

        /// <summary>主生成窗口（Sprite / Sequence / Skybox / 3D / Music）的最小尺寸</summary>
        public static Vector2 MainWindowMinSize => new Vector2(320f, 800f);

        // ========== 两列自适应布局 ==========

        /// <summary>历史面板最小宽度</summary>
        public static float MinHistoryPanelWidth => 400f;
        /// <summary>左侧面板固定宽度</summary>
        public static float LeftPanelFixedWidth => 400f;
        /// <summary>左侧内容区内边距</summary>
        public static float LeftContentPadding => 16f;
        /// <summary>左侧组件内容宽度（<see cref="LeftPanelFixedWidth"/> − 2×<see cref="LeftContentPadding"/>）</summary>
        public static float LeftComponentWidth => LeftPanelFixedWidth - LeftContentPadding * 2f;
        /// <summary>切换为垂直布局的窗口宽度阈值</summary>
        public static float LayoutSwitchThreshold => 520f;
        /// <summary>外边距</summary>
        public static float OuterMargin => 10f;
        /// <summary>设置面板左右内边距</summary>
        public static int PanelPadding => Mathf.Max(0, Mathf.RoundToInt(30f));

        /// <summary>
        /// ScrollView 内纵向滚动条占用的横向宽度（条贴在视口右侧）。固定常量在高分屏或不同 Unity 皮肤下容易偏小，导致最右列被压住。
        /// </summary>
        public static float ScrollViewVerticalScrollbarReserveForLayout
        {
            get
            {
                var vs = GUI.skin.verticalScrollbar;
                float fw = (vs != null && vs.fixedWidth > 0.5f) ? vs.fixedWidth : 15f;
                int marginH = vs != null ? (vs.margin.left + vs.margin.right) : 4;
                return Mathf.Max(26f, fw + marginH + 8f);
            }
        }

        /// <summary>
        /// 历史栏外层宽度减去左右 <see cref="PanelPadding"/>（预览等在 ScrollView 上方、不受滚动条侵占宽度的区域）。
        /// </summary>
        public static float HistoryPanelInnerWidth(float panelOuterWidth)
        {
            float w = panelOuterWidth - PanelPadding * 2f;
            return Mathf.Max(120f, w);
        }

        /// <summary>
        /// 历史栏内 ScrollView 子内容排版宽度（再扣纵向滚动条占位；网格必须与之一致）。
        /// </summary>
        public static float HistoryScrollViewLayoutWidth(float panelOuterWidth)
        {
            float w = panelOuterWidth - PanelPadding * 2f - ScrollViewVerticalScrollbarReserveForLayout;
            return Mathf.Max(120f, w);
        }

        private static GUIStyle _historyPanelStyleHorizontal;
        private static GUIStyle _historyPanelStyleVertical;
        private static GUIStyle _windowContentStyle;

        /// <summary>
        /// 独立窗口内容容器样式（带四周内边距）
        /// </summary>
        public static GUIStyle WindowContentStyle
        {
            get
            {
                if (_windowContentStyle == null)
                {
                    _windowContentStyle = new GUIStyle
                    {
                        padding = new RectOffset(PanelPadding, PanelPadding, Mathf.Max(0, Mathf.RoundToInt(30f)), PanelPadding)
                    };
                }
                return _windowContentStyle;
            }
        }

        /// <summary>
        /// 历史面板容器样式（根据布局模式设置间距）
        /// </summary>
        public static GUIStyle GetHistoryPanelStyle(bool isVerticalLayout)
        {
            if (isVerticalLayout)
            {
                if (_historyPanelStyleVertical == null)
                {
                    _historyPanelStyleVertical = new GUIStyle
                    {
                        padding = new RectOffset(PanelPadding, PanelPadding, Mathf.Max(0, Mathf.RoundToInt(30f)), 0)
                    };
                }
                return _historyPanelStyleVertical;
            }
            else
            {
                if (_historyPanelStyleHorizontal == null)
                {
                    _historyPanelStyleHorizontal = new GUIStyle
                    {
                        padding = new RectOffset(PanelPadding, PanelPadding, Mathf.Max(0, Mathf.RoundToInt(30f)), 0)
                    };
                }
                return _historyPanelStyleHorizontal;
            }
        }

        private static Texture2D _textToModelInputTexture;
        private static Texture2D _imageUploadTexture;
        private static Texture2D _previewImageDefaultTexture;
        private static Texture2D _separationLineTexture;
        private static Texture2D _searchInputBox4xTexture;
        private static Texture2D _searchIcon4xTexture;
        private static Texture2D _gapLineTexture;
        private static Texture2D _tileTexture;
        private static Texture2D _tileTextureSelectedTexture;
        private static Texture2D _buttonNormalTexture;
        private static Texture2D _buttonHoverTexture;
        private static Texture2D _buttonActiveTexture;
        private static Texture2D _mutiViewBoxTexture;
        private static Texture2D _mutiViewSelectedBoxTexture;
        private static Texture2D _checkboxOffNormalTexture;
        private static Texture2D _checkboxOffHoverTexture;
        private static Texture2D _checkboxOnNormalTexture;
        private static Texture2D _checkboxOnHoverTexture;
        private static Texture2D _targetIconTexture;
        private static Texture2D _targetSelectorRectTexture;
        private static Texture2D _modelSelectorRectTexture;
        private static Texture2D _syncAltTexture;
        private static Texture2D _editIconTexture;
        private static Texture2D _closeIconTexture;
        private static Texture2D _uploadFrameNormal2xTexture;
        private static Texture2D _uploadFrameHover2xTexture;
        private static Texture2D _uploadFrameUploaded2xTexture;
        private static Texture2D _inputBoxNormal2xTexture;
        private static Texture2D _inputBoxLargeTexture;
        private static Texture2D _uploadImageIconTexture;
        private static Texture2D _arrowGreen4xTexture;
        private static Texture2D _dropDownFrame2xTexture;
        private static Texture2D _dropDownFrameSelected2xTexture;
        private static Texture2D _dropBoxArrow4xTexture;
        private static Texture2D _dropBoxRightArrow4xTexture;
        private static Texture2D _settingInputBox2xTexture;
        private static Texture2D _itemBoxNormal4xTexture;
        private static Texture2D _itemBoxChecked4xTexture;
        private static Texture2D _favoriteIconNormal4xTexture;
        private static Texture2D _favoriteIconChecked4xTexture;

        private static GUIStyle _textStyle;
        private static GUIStyle _headerStyle;
        private static GUIStyle _modelNameStyle;
        private static GUIStyle _contentStyle;
        private static GUIStyle _linkStyle;
        private static GUIStyle _greenButtonStyle;
        private static GUIStyle _buttonStyle;
        private static GUIStyle _textFieldStyle;
        private static GUIStyle _searchTextFieldStyle;
        private static GUIStyle _placeholderStyle;
        private static GUIStyle _imageUploadAreaStyle;
        private static GUIStyle _separatorStyle;
        private static GUIStyle _gapLineStyle;
        private static GUIStyle _statusStyle;
        private static GUIStyle _historyTileStyle;
        private static GUIStyle _historyTileSelectedStyle;
        private static GUIStyle _historyLabelStyle;
        private static GUIStyle _generateButtonSolidStyle;
        private static GUIStyle _generateButtonBusyStyle;
        private static GUIStyle _generateButtonDisabledStyle;
        private static GUIStyle _generateButtonLabelStyle;
        private static GUIStyle _generateButtonLabelDisabledStyle;
        private static GUIStyle _centeredGreyMiniLabelStyleSmall;
        private static GUIStyle _centeredGreyLabelStyle;
        private static GUIStyle _smallGreyCenterLabelStyle;
        private static GUIStyle _smallGreyLeftLabelStyle;
        private static GUIStyle _SmallGreenLeftLabelStyle;
        private static GUIStyle _smallGreyLabelStyle;
        private static GUIStyle _hintLabelStyle;
        private static GUIStyle _clearButtonStyle;
        private static GUIStyle _plusStyle;
        private static GUIStyle _helpBoxStyle;
        private static GUIStyle _miniRedLabelStyle;
        private static GUIStyle _pinIconStyle;
        private static GUIStyle _sectionTitleStyle;
        private static GUIStyle _targetPrefabHeaderStyle;
        private static GUIStyle _targetPrefabNameStyle;
        private static GUIStyle _targetObjectButtonLabelStyle;
        private static GUIStyle _checkboxBoxStyle;
        private static GUIStyle _checkboxRowLabelStyle;
        private static GUIStyle _modelSelectButtonStyle;
        private static GUIStyle _modelSelectNameStyle;
        private static GUIStyle _uploadFrameNormalStyle;
        private static GUIStyle _uploadFrameHoverStyle;
        private static GUIStyle _uploadFrameUploadedStyle;
        private static GUIStyle _uploadTitleStyle;
        private static GUIStyle _uploadHintStyle;
        private static GUIStyle _uploadNoImageStyle;
        private static GUIStyle _uploadAIGenLinkStyle;
        private static GUIStyle _promptInputNormalStyle;
        private static GUIStyle _promptInputTextStyle;
        private static GUIStyle _promptInputPlaceholderStyle;
        private static GUIStyle _dropDownTriggerStyle;
        private static GUIStyle _dropDownPanelStyle;
        private static GUIStyle _dropDownRowTextStyle;
        private static GUIStyle _advancedFoldoutTitleStyle;
        private static GUIStyle _advancedInputTextStyle;
        private static GUIStyle _profileEmailStyle;
        private static Texture2D _greenBtnPressed4xTexture;
        private static Texture2D _greenBtnNormal4xTexture;
        private static Texture2D _greenBtnHover4xTexture;
        private static Texture2D _blackBtnNormal4xTexture;
        private static Texture2D _greyBtnDisableTexture;
        private static Texture2D _greenButtonTexture;
        private static Font _sourceHanSansRegularFont;
        private static Font _sourceHanSansMediumFont;

        public static Texture2D CreateSolidColorTexture(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private static Texture2D LoadEditorTexture(string relativePath)
        {
            return EditorGUIUtility.Load(TextureBasePath + relativePath) as Texture2D;
        }

        private static void EnsureTextures()
        {
            if (_textToModelInputTexture != null) return;
            _textToModelInputTexture = LoadEditorTexture(TextToModelInputPath);
            _imageUploadTexture = LoadEditorTexture(ImageUploadPath);
            _previewImageDefaultTexture = LoadEditorTexture(PreviewImageDefaultPath);
            _separationLineTexture = LoadEditorTexture(SeparationLinePath);
            _searchInputBox4xTexture = LoadEditorTexture(SearchInputBox4xPath);
            _searchIcon4xTexture = LoadEditorTexture(SearchIcon4xPath);
            _gapLineTexture = LoadEditorTexture(GapLinePath);
            _greenBtnNormal4xTexture = LoadEditorTexture(GreenBtnNormal4xPath);
            _greenBtnHover4xTexture = LoadEditorTexture(GreenBtnHover4xPath);
            _greenBtnPressed4xTexture = LoadEditorTexture(GreenBtnPressed4xPath);
            _blackBtnNormal4xTexture = LoadEditorTexture(BlackBtnNormal4xPath);
            _greyBtnDisableTexture = LoadEditorTexture(GreyBtnDisablePath);
            _greenButtonTexture = LoadEditorTexture(GreenButtonPath);
            _mutiViewBoxTexture = LoadEditorTexture(MutiViewBoxPath);
            _mutiViewSelectedBoxTexture = LoadEditorTexture(MutiViewSelectedBoxPath);
            _checkboxOffNormalTexture = LoadEditorTexture(CheckboxOffNormalPath);
            _checkboxOffHoverTexture = LoadEditorTexture(CheckboxOffHoverPath);
            _checkboxOnNormalTexture = LoadEditorTexture(CheckboxOnNormalPath);
            _checkboxOnHoverTexture = LoadEditorTexture(CheckboxOnHoverPath);
            _targetIconTexture = LoadEditorTexture(TargetIconPath);
            _targetSelectorRectTexture = LoadEditorTexture(TargetSelectorRectPath);
            _modelSelectorRectTexture = LoadEditorTexture(ModelSelectorRectPath);
            _syncAltTexture = LoadEditorTexture(SyncAltPath);
            _editIconTexture = LoadEditorTexture(EditIconPath);
            _closeIconTexture = LoadEditorTexture(CloseIconPath);
            _uploadFrameNormal2xTexture = LoadEditorTexture(UploadFrameNormal2xPath);
            _uploadFrameHover2xTexture = LoadEditorTexture(UploadFrameHover2xPath);
            _uploadFrameUploaded2xTexture = LoadEditorTexture(UploadFrameUploaded2xPath);
            _inputBoxNormal2xTexture = LoadEditorTexture(InputBoxNormal2xPath);
            _inputBoxLargeTexture = LoadEditorTexture(InputBoxLargePath);
            _uploadImageIconTexture = LoadEditorTexture(UploadImageIconPath);
            _arrowGreen4xTexture = LoadEditorTexture(ArrowGreen4xPath);
            _dropDownFrame2xTexture = LoadEditorTexture(DropDownFrame2xPath);
            _dropDownFrameSelected2xTexture = LoadEditorTexture(DropDownFrameSelected2xPath);
            _dropBoxArrow4xTexture = LoadEditorTexture(DropBoxArrow4xPath);
            _dropBoxRightArrow4xTexture = LoadEditorTexture(DropBoxRightArrow4xPath);
            _settingInputBox2xTexture = LoadEditorTexture(SettingInputBox2xPath);
            _itemBoxNormal4xTexture = LoadEditorTexture(ItemBoxNormal4xPath);
            _itemBoxChecked4xTexture = LoadEditorTexture(ItemBoxChecked4xPath);
            _favoriteIconNormal4xTexture = LoadEditorTexture(FavoriteIconNormal4xPath);
            _favoriteIconChecked4xTexture = LoadEditorTexture(FavoriteIconChecked4xPath);

            _tileTexture = LoadEditorTexture(TileTexturePath);
            _tileTextureSelectedTexture = LoadEditorTexture(TileTextureSelectedPath);
            _buttonNormalTexture = CreateSolidColorTexture(new Color(0.4f, 0.4f, 0.4f, 1f));
            _buttonHoverTexture = CreateSolidColorTexture(ThemeGreyColor);
            _buttonActiveTexture = CreateSolidColorTexture(new Color(0.35f, 0.35f, 0.35f, 1f));

            // 中文字体：思源黑体 Regular / Medium
            _sourceHanSansRegularFont = AssetDatabase.LoadAssetAtPath<Font>(FontBasePath + SourceHanSansRegularFontName);
            if (_sourceHanSansRegularFont == null)
                _sourceHanSansRegularFont = AssetDatabase.LoadAssetAtPath<Font>("Assets/Editor/Fonts/" + SourceHanSansRegularFontName);
            _sourceHanSansMediumFont = AssetDatabase.LoadAssetAtPath<Font>(FontBasePath + SourceHanSansMediumFontName);
            if (_sourceHanSansMediumFont == null)
                _sourceHanSansMediumFont = AssetDatabase.LoadAssetAtPath<Font>("Assets/Editor/Fonts/" + SourceHanSansMediumFontName);
        }

        private static void EnsureStyles()
        {
            EnsureTextures();
            if (_buttonStyle != null)
                return;

            _textStyle = new GUIStyle
            {
                font = _sourceHanSansRegularFont,
                fontSize = 12,
                fontStyle = FontStyle.Normal,
            };

            _buttonStyle = new GUIStyle(_textStyle)
            {
                fixedHeight = 30f,
                alignment = TextAnchor.MiddleCenter,
            };
            _buttonStyle.normal.background = _buttonNormalTexture;
            _buttonStyle.hover.background = _buttonHoverTexture;
            _buttonStyle.active.background = _buttonActiveTexture;
            _buttonStyle.focused.background = _buttonNormalTexture;
            _buttonStyle.normal.textColor = Color.white;
            _buttonStyle.hover.textColor = Color.white;
            _buttonStyle.active.textColor = Color.white;
            _buttonStyle.focused.textColor = Color.white;

            _generateButtonSolidStyle = new GUIStyle(_buttonStyle)
            {
                fixedHeight = LeftPanelBottomDock.ActionButtonHeight,
                imagePosition = ImagePosition.ImageOnly,
                fontSize = GenerateButtonFontSize,
                fontStyle = GenerateButtonFontStyle,
                font = _sourceHanSansMediumFont != null ? _sourceHanSansMediumFont : _sourceHanSansRegularFont,
                margin = new RectOffset(0, 0, Mathf.Max(0, Mathf.RoundToInt(12f)), Mathf.Max(0, Mathf.RoundToInt(12f))),
                padding = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(33f)), Mathf.Max(0, Mathf.RoundToInt(33f)), Mathf.Max(0, Mathf.RoundToInt(8f)), Mathf.Max(0, Mathf.RoundToInt(8f))),
                border = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(32f)), Mathf.Max(0, Mathf.RoundToInt(32f)), Mathf.Max(0, Mathf.RoundToInt(32f)), Mathf.Max(0, Mathf.RoundToInt(32f))),
                alignment = TextAnchor.MiddleCenter
            };
            _generateButtonSolidStyle.normal.background = _greenBtnNormal4xTexture;
            _generateButtonSolidStyle.hover.background = _greenBtnHover4xTexture;
            _generateButtonSolidStyle.active.background = _greenBtnPressed4xTexture;
            _generateButtonSolidStyle.focused.background = _greenBtnNormal4xTexture;
            _generateButtonSolidStyle.normal.textColor = Color.white;
            _generateButtonSolidStyle.hover.textColor = Color.white;
            _generateButtonSolidStyle.active.textColor = Color.white;
            _generateButtonSolidStyle.focused.textColor = Color.white;

            _generateButtonBusyStyle = new GUIStyle(_generateButtonSolidStyle);
            _generateButtonBusyStyle.normal.background = _greenBtnPressed4xTexture;
            _generateButtonBusyStyle.hover.background = _greenBtnPressed4xTexture;
            _generateButtonBusyStyle.active.background = _greenBtnPressed4xTexture;
            _generateButtonBusyStyle.focused.background = _greenBtnPressed4xTexture;

            _generateButtonDisabledStyle = new GUIStyle(_generateButtonSolidStyle);
            _generateButtonDisabledStyle.normal.background = _greyBtnDisableTexture;
            _generateButtonDisabledStyle.hover.background = _greyBtnDisableTexture;
            _generateButtonDisabledStyle.active.background = _greyBtnDisableTexture;
            _generateButtonDisabledStyle.focused.background = _greyBtnDisableTexture;

            _generateButtonLabelStyle = new GUIStyle(_textStyle)
            {
                font = _sourceHanSansMediumFont != null ? _sourceHanSansMediumFont : _sourceHanSansRegularFont,
                fontSize = GenerateButtonFontSize,
                fontStyle = GenerateButtonFontStyle,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };
            _generateButtonLabelStyle.normal.textColor = Color.white;
            _generateButtonLabelStyle.hover.textColor = Color.white;
            _generateButtonLabelStyle.active.textColor = Color.white;
            _generateButtonLabelStyle.focused.textColor = Color.white;

            var generateButtonDisabledTextColor = new Color(1f, 1f, 1f, 0.25f);
            _generateButtonLabelDisabledStyle = new GUIStyle(_generateButtonLabelStyle);
            _generateButtonLabelDisabledStyle.normal.textColor = generateButtonDisabledTextColor;
            _generateButtonLabelDisabledStyle.hover.textColor = generateButtonDisabledTextColor;
            _generateButtonLabelDisabledStyle.active.textColor = generateButtonDisabledTextColor;
            _generateButtonLabelDisabledStyle.focused.textColor = generateButtonDisabledTextColor;

            _textFieldStyle = new GUIStyle(_textStyle)
            {
                fixedHeight = 35f,
                normal = { background = _textToModelInputTexture, textColor = Color.white },
                padding = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(5f)), Mathf.Max(0, Mathf.RoundToInt(5f))),
                margin = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
            };
            _textFieldStyle.hover.textColor = Color.white;
            _textFieldStyle.active.textColor = Color.white;
            _textFieldStyle.focused.textColor = Color.white;

            _searchTextFieldStyle = new GUIStyle(_textFieldStyle)
            {
                fixedHeight = 44f,
                normal = { background = _searchInputBox4xTexture, textColor = Color.white },
                border = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(16f)), Mathf.Max(0, Mathf.RoundToInt(16f)), Mathf.Max(0, Mathf.RoundToInt(16f)), Mathf.Max(0, Mathf.RoundToInt(16f))),
                padding = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(10f))),
            };

            _placeholderStyle = new GUIStyle(_textStyle)
            {
                normal = { textColor = Color.gray },
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(10f))),
            };

            _imageUploadAreaStyle = new GUIStyle
            {
                normal = { background = _imageUploadTexture },
                padding = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(8f)), Mathf.Max(0, Mathf.RoundToInt(8f))),
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 82f,
                font = _sourceHanSansRegularFont,
            };

            _headerStyle = new GUIStyle(_textStyle)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = FontGrayColor },
            };

            _modelNameStyle = new GUIStyle(_textStyle)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = FontContentColor },
            };

            _contentStyle = new GUIStyle(_textStyle)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = FontContentColor },
            };

            _linkStyle = new GUIStyle(_textStyle)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = ThemeGreenColor}
            };

            _greenButtonStyle = new GUIStyle
            {
                font = _sourceHanSansRegularFont,
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(10f)), 0, 0),
                normal = { textColor = ThemeGreenColor, background = _greenButtonTexture },
            };

            _separatorStyle = new GUIStyle
            {
                fixedHeight = Mathf.Max(1f, 1f),
                margin = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(20f)), Mathf.Max(0, Mathf.RoundToInt(20f)), Mathf.Max(0, Mathf.RoundToInt(20f)), Mathf.Max(0, Mathf.RoundToInt(20f)))
            };
            _separatorStyle.normal.background = _separationLineTexture;

            _gapLineStyle = new GUIStyle
            {
                fixedHeight = 1f,
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                stretchWidth = true
            };
            _gapLineStyle.normal.background = _gapLineTexture;

            _statusStyle = new GUIStyle
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.8f, 0.8f, 0.8f, 1f) },
                padding = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(5f)), Mathf.Max(0, Mathf.RoundToInt(5f)), Mathf.Max(0, Mathf.RoundToInt(2f)), Mathf.Max(0, Mathf.RoundToInt(2f))),
                font = _sourceHanSansRegularFont
            };

            _historyTileStyle = new GUIStyle
            {
                alignment = TextAnchor.MiddleCenter,
                font = _sourceHanSansRegularFont,
                normal = { background = _tileTexture }
            };

            _historyTileSelectedStyle = new GUIStyle(_historyTileStyle)
            {
                normal = { background = _tileTextureSelectedTexture }
            };

            _centeredGreyMiniLabelStyleSmall = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                font = _sourceHanSansRegularFont,
                fontSize = 10
            };
            _centeredGreyLabelStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                font = _sourceHanSansRegularFont,
                fontSize = 12
            };

            _smallGreyCenterLabelStyle = new GUIStyle
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = ThemeGreyColor },
                fontSize = 9,
                font = _sourceHanSansRegularFont
            };
            _smallGreyLeftLabelStyle = new GUIStyle
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
                normal = { textColor = ThemeGreyColor },
                fontSize = 10,
                font = _sourceHanSansRegularFont
            };
            _SmallGreenLeftLabelStyle = new GUIStyle
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
                normal = { textColor = ThemeLightGreenColor },
                fontSize = 9,
                font = _sourceHanSansRegularFont
            };
            _hintLabelStyle = new GUIStyle(_textStyle)
            {
                normal = { textColor = FontHintColor },
                fontSize = 9,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
            };
            // 不居中的小号灰字（思源黑体 Regular），用于列表内左对齐提示等
            _smallGreyLabelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = ThemeGreyColor },
                fontSize = 10,
                font = _sourceHanSansRegularFont
            };
            _clearButtonStyle = new GUIStyle()
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            _plusStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 22,
                normal = { textColor = ThemeGreyColor }
            };
            _helpBoxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                fontSize = 10,
                wordWrap = true
            };
            _miniRedLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                font = _sourceHanSansRegularFont,
                normal = { textColor = Color.red }
            };
            _historyLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                fontSize = 10,
                normal = { textColor = Color.white },
                hover = { textColor = Color.white },
                font = _sourceHanSansRegularFont
            };
            _pinIconStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter
            };

            _sectionTitleStyle = new GUIStyle(_textStyle)
            {
                font =  _sourceHanSansRegularFont,
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft
            };
            _sectionTitleStyle.normal.textColor = Color.white;
            _sectionTitleStyle.hover.textColor = Color.white;
            _sectionTitleStyle.active.textColor = Color.white;
            _sectionTitleStyle.focused.textColor = Color.white;

            _advancedFoldoutTitleStyle = new GUIStyle(_sectionTitleStyle)
            {
                clipping = TextClipping.Clip
            };

            _advancedInputTextStyle = new GUIStyle(_textStyle)
            {
                font = _sourceHanSansRegularFont,
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                padding = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(10f)), 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0)
            };
            _advancedInputTextStyle.normal.textColor = Color.white;
            _advancedInputTextStyle.hover.textColor = Color.white;
            _advancedInputTextStyle.active.textColor = Color.white;
            _advancedInputTextStyle.focused.textColor = Color.white;

            _profileEmailStyle = new GUIStyle(_textStyle)
            {
                font = _sourceHanSansMediumFont != null ? _sourceHanSansMediumFont : _sourceHanSansRegularFont,
                fontSize = 11,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.UpperLeft,
                clipping = TextClipping.Clip
            };
            _profileEmailStyle.normal.textColor = new Color(1f, 1f, 1f, 0.45f);

            _targetPrefabHeaderStyle = new GUIStyle(_textStyle)
            {
                fontStyle = FontStyle.Normal,
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft
            };
            _targetPrefabHeaderStyle.normal.textColor = new Color(1f, 1f, 1f, 0.25f);
            _targetPrefabHeaderStyle.hover.textColor = _targetPrefabHeaderStyle.normal.textColor;
            _targetPrefabHeaderStyle.active.textColor = _targetPrefabHeaderStyle.normal.textColor;
            _targetPrefabHeaderStyle.focused.textColor = _targetPrefabHeaderStyle.normal.textColor;

            _targetPrefabNameStyle = new GUIStyle(_textStyle)
            {
                fontStyle = FontStyle.Normal,
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft
            };
            _targetPrefabNameStyle.normal.textColor = Color.white;
            _targetPrefabNameStyle.hover.textColor = Color.white;
            _targetPrefabNameStyle.active.textColor = Color.white;
            _targetPrefabNameStyle.focused.textColor = Color.white;

            _targetObjectButtonLabelStyle = new GUIStyle(_textStyle)
            {
                font = _sourceHanSansRegularFont,
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };
            var targetLabelColor = new Color(1f, 1f, 1f, 0.6f);
            _targetObjectButtonLabelStyle.normal.textColor = targetLabelColor;
            _targetObjectButtonLabelStyle.hover.textColor = targetLabelColor;
            _targetObjectButtonLabelStyle.active.textColor = targetLabelColor;
            _targetObjectButtonLabelStyle.focused.textColor = targetLabelColor;
            _targetObjectButtonLabelStyle.onNormal.textColor = targetLabelColor;
            _targetObjectButtonLabelStyle.onHover.textColor = targetLabelColor;
            _targetObjectButtonLabelStyle.onActive.textColor = targetLabelColor;
            _targetObjectButtonLabelStyle.onFocused.textColor = targetLabelColor;

            float checkboxDesignSize = 30f;
            _checkboxBoxStyle = new GUIStyle
            {
                stretchWidth = false,
                stretchHeight = false,
                fixedWidth = checkboxDesignSize,
                fixedHeight = checkboxDesignSize,
                margin = new RectOffset(0, Mathf.Max(0, Mathf.RoundToInt(6f)), Mathf.Max(0, Mathf.RoundToInt(2f)), Mathf.Max(0, Mathf.RoundToInt(2f))),
                padding = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0),
                overflow = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip
            };
            _checkboxBoxStyle.normal.background = _checkboxOffNormalTexture;
            _checkboxBoxStyle.hover.background = _checkboxOffHoverTexture != null ? _checkboxOffHoverTexture : _checkboxOffNormalTexture;
            _checkboxBoxStyle.active.background = _checkboxOffHoverTexture != null ? _checkboxOffHoverTexture : _checkboxOffNormalTexture;
            _checkboxBoxStyle.focused.background = _checkboxOffHoverTexture != null ? _checkboxOffHoverTexture : _checkboxOffNormalTexture;
            _checkboxBoxStyle.onNormal.background = _checkboxOnNormalTexture != null ? _checkboxOnNormalTexture : _checkboxOffNormalTexture;
            _checkboxBoxStyle.onHover.background = _checkboxOnHoverTexture != null ? _checkboxOnHoverTexture : _checkboxOnNormalTexture;
            _checkboxBoxStyle.onActive.background = _checkboxOnHoverTexture != null ? _checkboxOnHoverTexture : _checkboxOnNormalTexture;
            _checkboxBoxStyle.onFocused.background = _checkboxOnHoverTexture != null ? _checkboxOnHoverTexture : _checkboxOnNormalTexture;

            _checkboxRowLabelStyle = new GUIStyle(_textStyle)
            {
                alignment = TextAnchor.MiddleLeft,
                margin = new RectOffset(0, 0, Mathf.Max(0, Mathf.RoundToInt(2f)), Mathf.Max(0, Mathf.RoundToInt(2f))),
                stretchWidth = true
            };
            _checkboxRowLabelStyle.normal.textColor = FontContentColor;
            _checkboxRowLabelStyle.hover.textColor = FontContentColor;
            _checkboxRowLabelStyle.active.textColor = FontContentColor;
            _checkboxRowLabelStyle.focused.textColor = FontContentColor;
            _checkboxRowLabelStyle.onNormal.textColor = FontContentColor;
            _checkboxRowLabelStyle.onHover.textColor = FontContentColor;
            _checkboxRowLabelStyle.onActive.textColor = FontContentColor;
            _checkboxRowLabelStyle.onFocused.textColor = FontContentColor;

            _modelSelectButtonStyle = new GUIStyle
            {
                fixedHeight = 40f,
                padding = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(16f)), Mathf.Max(0, Mathf.RoundToInt(16f)), Mathf.Max(0, Mathf.RoundToInt(10f)), Mathf.Max(0, Mathf.RoundToInt(10f))),
                margin = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleLeft,
                border = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(60f)), Mathf.Max(0, Mathf.RoundToInt(60f)), Mathf.Max(0, Mathf.RoundToInt(60f)), Mathf.Max(0, Mathf.RoundToInt(60f)))
            };
            _modelSelectButtonStyle.normal.background = _modelSelectorRectTexture;
            _modelSelectButtonStyle.hover.background = _modelSelectorRectTexture;
            _modelSelectButtonStyle.active.background = _modelSelectorRectTexture;
            _modelSelectButtonStyle.focused.background = _modelSelectorRectTexture;

            _modelSelectNameStyle = new GUIStyle(_textStyle)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };
            _modelSelectNameStyle.normal.textColor = Color.white;
            _modelSelectNameStyle.hover.textColor = Color.white;
            _modelSelectNameStyle.active.textColor = Color.white;
            _modelSelectNameStyle.focused.textColor = Color.white;

            _uploadFrameNormalStyle = new GUIStyle
            {
                fixedHeight = 180f,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(4f)), Mathf.Max(0, Mathf.RoundToInt(4f)), Mathf.Max(0, Mathf.RoundToInt(4f)), Mathf.Max(0, Mathf.RoundToInt(4f))),
                stretchWidth = true
            };
            _uploadFrameNormalStyle.normal.background = _uploadFrameNormal2xTexture;
            _uploadFrameNormalStyle.hover.background = _uploadFrameNormal2xTexture;
            _uploadFrameNormalStyle.active.background = _uploadFrameNormal2xTexture;
            _uploadFrameNormalStyle.focused.background = _uploadFrameNormal2xTexture;

            _uploadFrameHoverStyle = new GUIStyle(_uploadFrameNormalStyle);
            _uploadFrameHoverStyle.normal.background = _uploadFrameHover2xTexture != null ? _uploadFrameHover2xTexture : _uploadFrameNormal2xTexture;
            _uploadFrameHoverStyle.hover.background = _uploadFrameHoverStyle.normal.background;
            _uploadFrameHoverStyle.active.background = _uploadFrameHoverStyle.normal.background;
            _uploadFrameHoverStyle.focused.background = _uploadFrameHoverStyle.normal.background;

            _uploadFrameUploadedStyle = new GUIStyle(_uploadFrameNormalStyle);
            _uploadFrameUploadedStyle.normal.background = _uploadFrameUploaded2xTexture != null ? _uploadFrameUploaded2xTexture : _uploadFrameNormal2xTexture;
            _uploadFrameUploadedStyle.hover.background = _uploadFrameUploadedStyle.normal.background;
            _uploadFrameUploadedStyle.active.background = _uploadFrameUploadedStyle.normal.background;
            _uploadFrameUploadedStyle.focused.background = _uploadFrameUploadedStyle.normal.background;

            _uploadTitleStyle = new GUIStyle(_textStyle)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter
            };
            var uploadTitleColor = new Color(200f / 255f, 204f / 255f, 210f / 255f, 1f);
            _uploadTitleStyle.normal.textColor = uploadTitleColor;
            _uploadTitleStyle.hover.textColor = uploadTitleColor;
            _uploadTitleStyle.active.textColor = uploadTitleColor;
            _uploadTitleStyle.focused.textColor = uploadTitleColor;

            _uploadHintStyle = new GUIStyle(_textStyle)
            {
                fontSize = 12,
                alignment = TextAnchor.UpperCenter,
                wordWrap = true
            };
            var uploadHintColor = new Color(102f / 255f, 102f / 255f, 102f / 255f, 1f);
            _uploadHintStyle.normal.textColor = uploadHintColor;
            _uploadHintStyle.hover.textColor = uploadHintColor;
            _uploadHintStyle.active.textColor = uploadHintColor;
            _uploadHintStyle.focused.textColor = uploadHintColor;

            _uploadNoImageStyle = new GUIStyle(_textStyle)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleRight
            };
            _uploadNoImageStyle.normal.textColor = uploadHintColor;
            _uploadNoImageStyle.hover.textColor = uploadHintColor;
            _uploadNoImageStyle.active.textColor = uploadHintColor;
            _uploadNoImageStyle.focused.textColor = uploadHintColor;

            _uploadAIGenLinkStyle = new GUIStyle(_textStyle)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft
            };
            _uploadAIGenLinkStyle.normal.textColor = ThemeGreenColor;
            _uploadAIGenLinkStyle.hover.textColor = ThemeGreenColor;
            _uploadAIGenLinkStyle.active.textColor = ThemeGreenColor;
            _uploadAIGenLinkStyle.focused.textColor = ThemeGreenColor;

            const int inputBoxNormalRefHeight = 60;
            const int inputBoxNormalBorder = 14;
            Texture2D promptInputBg = _inputBoxLargeTexture != null ? _inputBoxLargeTexture : _inputBoxNormal2xTexture;
            int promptInputBorder = promptInputBg != null
                ? Mathf.Max(
                    inputBoxNormalBorder,
                    Mathf.RoundToInt(inputBoxNormalBorder * (promptInputBg.height / (float)inputBoxNormalRefHeight)))
                : inputBoxNormalBorder;

            _promptInputNormalStyle = new GUIStyle
            {
                fixedHeight = 0,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(
                    Mathf.Max(0, promptInputBorder),
                    Mathf.Max(0, promptInputBorder),
                    Mathf.Max(0, promptInputBorder),
                    Mathf.Max(0, promptInputBorder)),
                stretchWidth = true,
                stretchHeight = true
            };
            _promptInputNormalStyle.normal.background = promptInputBg;
            _promptInputNormalStyle.hover.background = promptInputBg;
            _promptInputNormalStyle.active.background = promptInputBg;
            _promptInputNormalStyle.focused.background = promptInputBg;

            _promptInputTextStyle = new GUIStyle(_textStyle)
            {
                font = _sourceHanSansMediumFont != null ? _sourceHanSansMediumFont : _sourceHanSansRegularFont,
                fontSize = 12,
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 0, 0, 0)
            };
            _promptInputTextStyle.normal.textColor = Color.white;
            _promptInputTextStyle.hover.textColor = Color.white;
            _promptInputTextStyle.active.textColor = Color.white;
            _promptInputTextStyle.focused.textColor = Color.white;

            _promptInputPlaceholderStyle = new GUIStyle(_promptInputTextStyle);
            var promptPlaceholderColor = new Color(102f / 255f, 102f / 255f, 102f / 255f, 1f);
            _promptInputPlaceholderStyle.normal.textColor = promptPlaceholderColor;
            _promptInputPlaceholderStyle.hover.textColor = promptPlaceholderColor;
            _promptInputPlaceholderStyle.active.textColor = promptPlaceholderColor;
            _promptInputPlaceholderStyle.focused.textColor = promptPlaceholderColor;

            _dropDownTriggerStyle = new GUIStyle
            {
                fixedHeight = 30f,
                padding = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(12f)), Mathf.Max(0, Mathf.RoundToInt(12f)), Mathf.Max(0, Mathf.RoundToInt(8f)), Mathf.Max(0, Mathf.RoundToInt(8f))),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(Mathf.Max(0, Mathf.RoundToInt(8f)), Mathf.Max(0, Mathf.RoundToInt(8f)), Mathf.Max(0, Mathf.RoundToInt(8f)), Mathf.Max(0, Mathf.RoundToInt(8f))),
                stretchWidth = true
            };
            _dropDownTriggerStyle.normal.background = _dropDownFrame2xTexture;
            _dropDownTriggerStyle.hover.background = _dropDownFrame2xTexture;
            _dropDownTriggerStyle.active.background = _dropDownFrame2xTexture;
            _dropDownTriggerStyle.focused.background = _dropDownFrame2xTexture;

            _dropDownPanelStyle = new GUIStyle
            {
                padding = new RectOffset(0, 0, Mathf.Max(0, Mathf.RoundToInt(4f)), Mathf.Max(0, Mathf.RoundToInt(4f))),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0),
                stretchWidth = true,
                stretchHeight = true
            };
            _dropDownPanelStyle.normal.background = null;
            _dropDownPanelStyle.hover.background = null;
            _dropDownPanelStyle.active.background = null;
            _dropDownPanelStyle.focused.background = null;

            _dropDownRowTextStyle = new GUIStyle(_textStyle)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };
            _dropDownRowTextStyle.normal.textColor = Color.white;
            _dropDownRowTextStyle.hover.textColor = Color.white;
            _dropDownRowTextStyle.active.textColor = Color.white;
            _dropDownRowTextStyle.focused.textColor = Color.white;
        }

        /// <summary>
        /// 绘制「自定义方框 + 独立文字」的勾选行。
        /// </summary>
        public static bool DrawCheckboxRow(string label, bool value)
        {
            GUILayout.BeginHorizontal();
            var box = CheckboxBoxStyle;
            float rowH = Mathf.Max(box.fixedHeight, EditorGUIUtility.singleLineHeight);
            bool newValue = GUILayout.Toggle(
                value,
                GUIContent.none,
                box,
                GUILayout.Width(box.fixedWidth),
                GUILayout.Height(rowH));
            GUILayout.Label(label, CheckboxRowLabelStyle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(rowH));
            GUILayout.EndHorizontal();
            return newValue;
        }

        public static GUIStyle HeaderStyle
        {
            get { EnsureStyles(); return _headerStyle; }
        }

        public static GUIStyle ModelNameStyle
        {
            get { EnsureStyles(); return _modelNameStyle; }
        }

        public static GUIStyle ContentStyle
        {
            get { EnsureStyles(); return _contentStyle; }
        }

        public static GUIStyle LinkStyle
        {
            get { EnsureStyles(); return _linkStyle; }
        }

        public static GUIStyle GreenButtonStyle
        {
            get { EnsureStyles(); return _greenButtonStyle; }
        }

        public static GUIStyle ButtonStyle
        {
            get { EnsureStyles(); return _buttonStyle; }
        }

        public static GUIStyle TextFieldStyle
        {
            get { EnsureStyles(); return _textFieldStyle; }
        }

        /// <summary>搜索框专用（较矮的输入框，fixedHeight 22）</summary>
        public static GUIStyle SearchTextFieldStyle
        {
            get { EnsureStyles(); return _searchTextFieldStyle; }
        }

        public static GUIStyle PlaceholderStyle
        {
            get { EnsureStyles(); return _placeholderStyle; }
        }

        public static GUIStyle ImageUploadAreaStyle
        {
            get { EnsureStyles(); return _imageUploadAreaStyle; }
        }

        public static Texture2D PreviewImageDefaultTexture
        {
            get { EnsureTextures(); return _previewImageDefaultTexture; }
        }

        public static GUIStyle SeparatorStyle
        {
            get { EnsureStyles(); return _separatorStyle; }
        }

        public static GUIStyle GapLineStyle
        {
            get { EnsureStyles(); return _gapLineStyle; }
        }

        public static GUIStyle StatusStyle
        {
            get { EnsureStyles(); return _statusStyle; }
        }

        public static GUIStyle HistoryTileStyle
        {
            get { EnsureStyles(); return _historyTileStyle; }
        }

        public static GUIStyle HistoryTileSelectedStyle
        {
            get { EnsureStyles(); return _historyTileSelectedStyle; }
        }

        public static GUIStyle HistoryLabelStyle
        {
            get { EnsureStyles();  return _historyLabelStyle; }
        }

        public static GUIStyle GenerateButtonSolidStyle
        {
            get { EnsureStyles(); return _generateButtonSolidStyle; }
        }

        public static GUIStyle GenerateButtonBusyStyle
        {
            get { EnsureStyles(); return _generateButtonBusyStyle; }
        }

        public static GUIStyle GenerateButtonDisabledStyle
        {
            get { EnsureStyles(); return _generateButtonDisabledStyle; }
        }

        public static GUIStyle GenerateButtonLabelStyle
        {
            get { EnsureStyles(); return _generateButtonLabelStyle; }
        }

        public static GUIStyle GenerateButtonLabelDisabledStyle
        {
            get { EnsureStyles(); return _generateButtonLabelDisabledStyle; }
        }

        public static GUIStyle CenteredGreyMiniLabelStyleSmall
        {
            get { EnsureStyles(); return _centeredGreyMiniLabelStyleSmall; }
        }

        public static GUIStyle CenteredGreyLabelStyle
        {
            get { EnsureStyles(); return _centeredGreyLabelStyle; }
        }

        public static GUIStyle SmallGreyCenterLabelStyle
        {
            get { EnsureStyles(); return _smallGreyCenterLabelStyle; }
        }

        public static GUIStyle SmallGreyLeftLabelStyle
        {
            get { EnsureStyles(); return _smallGreyLeftLabelStyle; }
        }

        public static GUIStyle SmallGreenLeftLabelStyle
        {
            get { EnsureStyles(); return _SmallGreenLeftLabelStyle; }
        }

        public static GUIStyle SmallGreyLabelStyle
        {
            get { EnsureStyles(); return _smallGreyLabelStyle; }
        }

        public static GUIStyle HintLabelStyle
        {
            get { EnsureStyles(); return _hintLabelStyle; }
        }

        public static GUIStyle ClearButtonStyle
        {
            get { EnsureStyles(); return _clearButtonStyle; }
        }

        public static GUIStyle PlusStyle
        {
            get { EnsureStyles(); return _plusStyle; }
        }

        public static GUIStyle HelpBoxStyle
        {
            get { EnsureStyles(); return _helpBoxStyle; }
        }

        public static GUIStyle MiniRedLabelStyle
        {
            get { EnsureStyles(); return _miniRedLabelStyle; }
        }

        public static GUIStyle PinIconStyle
        {
            get { EnsureStyles(); return _pinIconStyle; }
        }

        public static GUIStyle SectionTitleStyle
        {
            get { EnsureStyles(); return _sectionTitleStyle; }
        }

        public static GUIStyle TargetPrefabHeaderStyle
        {
            get { EnsureStyles(); return _targetPrefabHeaderStyle; }
        }

        public static GUIStyle TargetPrefabNameStyle
        {
            get { EnsureStyles(); return _targetPrefabNameStyle; }
        }

        public static GUIStyle TargetObjectButtonLabelStyle
        {
            get { EnsureStyles(); return _targetObjectButtonLabelStyle; }
        }

        public static GUIStyle CheckboxBoxStyle
        {
            get { EnsureStyles(); return _checkboxBoxStyle; }
        }

        public static GUIStyle CheckboxRowLabelStyle
        {
            get { EnsureStyles(); return _checkboxRowLabelStyle; }
        }

        public static GUIStyle ModelSelectButtonStyle
        {
            get { EnsureStyles(); return _modelSelectButtonStyle; }
        }

        public static GUIStyle ModelSelectNameStyle
        {
            get { EnsureStyles(); return _modelSelectNameStyle; }
        }

        public static GUIStyle UploadFrameNormalStyle
        {
            get { EnsureStyles(); return _uploadFrameNormalStyle; }
        }

        public static GUIStyle UploadFrameHoverStyle
        {
            get { EnsureStyles(); return _uploadFrameHoverStyle; }
        }

        public static GUIStyle UploadFrameUploadedStyle
        {
            get { EnsureStyles(); return _uploadFrameUploadedStyle; }
        }

        public static GUIStyle UploadTitleStyle
        {
            get { EnsureStyles(); return _uploadTitleStyle; }
        }

        public static GUIStyle UploadHintStyle
        {
            get { EnsureStyles(); return _uploadHintStyle; }
        }

        public static GUIStyle UploadNoImageStyle
        {
            get { EnsureStyles(); return _uploadNoImageStyle; }
        }

        public static GUIStyle UploadAIGenLinkStyle
        {
            get { EnsureStyles(); return _uploadAIGenLinkStyle; }
        }

        public static GUIStyle PromptInputNormalStyle
        {
            get { EnsureStyles(); return _promptInputNormalStyle; }
        }

        public static GUIStyle PromptInputTextStyle
        {
            get { EnsureStyles(); return _promptInputTextStyle; }
        }

        /// <summary>多行提示词输入框占位文案（灰色），勿用于 <see cref="EditorGUI.TextField"/> / <see cref="EditorGUI.TextArea"/> 的实际输入。</summary>
        public static GUIStyle PromptInputPlaceholderStyle
        {
            get { EnsureStyles(); return _promptInputPlaceholderStyle; }
        }

        public static GUIStyle DropDownTriggerStyle
        {
            get { EnsureStyles(); return _dropDownTriggerStyle; }
        }

        public static GUIStyle DropDownRowTextStyle
        {
            get { EnsureStyles(); return _dropDownRowTextStyle; }
        }

        public static GUIStyle DropDownPanelStyle
        {
            get { EnsureStyles(); return _dropDownPanelStyle; }
        }

        public static Texture2D BlackBtnNormal4xTexture
        {
            get { EnsureTextures(); return _blackBtnNormal4xTexture; }
        }

        public static Texture2D SearchIcon4xTexture
        {
            get { EnsureTextures(); return _searchIcon4xTexture; }
        }

        public static Texture2D MutiViewBoxTexture
        {
            get { EnsureTextures(); return _mutiViewBoxTexture; }
        }

        public static Texture2D MutiViewSelectedBoxTexture
        {
            get { EnsureTextures(); return _mutiViewSelectedBoxTexture; }
        }

        public static Texture2D TargetIconTexture
        {
            get { EnsureTextures(); return _targetIconTexture; }
        }

        public static Texture2D TargetSelectorRectTexture
        {
            get { EnsureTextures(); return _targetSelectorRectTexture; }
        }

        public static Texture2D SyncAltTexture
        {
            get { EnsureTextures(); return _syncAltTexture; }
        }

        public static Texture2D EditIconTexture
        {
            get { EnsureTextures(); return _editIconTexture; }
        }

        public static Texture2D CloseIconTexture
        {
            get { EnsureTextures(); return _closeIconTexture; }
        }

        public static Texture2D UploadImageIconTexture
        {
            get { EnsureTextures(); return _uploadImageIconTexture; }
        }

        public static Texture2D ArrowGreen4xTexture
        {
            get { EnsureTextures(); return _arrowGreen4xTexture; }
        }

        public static Texture2D DropDownFrameSelected2xTexture
        {
            get { EnsureTextures(); return _dropDownFrameSelected2xTexture; }
        }

        public static Texture2D DropBoxArrow4xTexture
        {
            get { EnsureTextures(); return _dropBoxArrow4xTexture; }
        }

        public static Texture2D DropBoxRightArrow4xTexture
        {
            get { EnsureTextures(); return _dropBoxRightArrow4xTexture; }
        }

        public static Texture2D SettingInputBox2xTexture
        {
            get { EnsureTextures(); return _settingInputBox2xTexture; }
        }

        public static Texture2D ItemBoxNormal4xTexture
        {
            get { EnsureTextures(); return _itemBoxNormal4xTexture; }
        }

        public static Texture2D ItemBoxChecked4xTexture
        {
            get { EnsureTextures(); return _itemBoxChecked4xTexture; }
        }

        public static Texture2D FavoriteIconNormal4xTexture
        {
            get { EnsureTextures(); return _favoriteIconNormal4xTexture; }
        }

        public static Texture2D FavoriteIconChecked4xTexture
        {
            get { EnsureTextures(); return _favoriteIconChecked4xTexture; }
        }

        public static Font SourceHanSansRegularFont
        {
            get { EnsureTextures(); return _sourceHanSansRegularFont; }
        }

        public static GUIStyle AdvancedFoldoutTitleStyle
        {
            get { EnsureStyles(); return _advancedFoldoutTitleStyle; }
        }

        public static GUIStyle AdvancedInputTextStyle
        {
            get { EnsureStyles(); return _advancedInputTextStyle; }
        }

        public static GUIStyle ProfileEmailStyle
        {
            get { EnsureStyles(); return _profileEmailStyle; }
        }

        public static Font SourceHanSansMediumFont
        {
            get { EnsureTextures(); return _sourceHanSansMediumFont; }
        }
    }
}
#endif
