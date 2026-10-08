#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using TJGenerators.Config;
using TJGenerators.UI;
using TJGenerators.Utils;

namespace TJGenerators
{
    /// <summary>
    /// 选择纹理走势 - 预览窗口（新 UI 风格）
    /// </summary>
    public class TJGeneratorsTexturePatternSelectorWindow : EditorWindow
    {
        private static string AllTag => TJGeneratorsL10n.L("全部");

        private const float WindowWidth = 584f;
        private const float WindowHeight = 686f;

        private const float SearchTop = 20f;
        private const float ContentLeft = 20f;
        private const float ContentRight = 20f;
        private const float SearchWidth = 544f;
        private const float SearchHeight = 39f;

        private const float TagTop = 79f;
        private const float TagButtonHeight = 36f;
        private const float TagButtonGap = 10f;
        private const float TagButtonMinWidth = 65f;
        private const float TagButtonLongWidth = 135f;
        private const float TagRowBottomGap = 20f;

        private const float CardWidth = 138f;
        private const float CardHeight = 169f;
        private const float CardGap = 10f;
        private const float CardPadding = 10f;
        private const float CardImageWidth = 118f;
        private const float CardImageHeight = 101f;
        private const float CardImageShadowOffset = 2f;
        private const float CardNameTop = 123f;
        private const float CardNameHeight = 16f;
        private const float CardDescTop = 142f;
        private const float CardDescHeight = 20f;
        private const float CardsPerRow = 3f;

        private const float LabelToCardsGap = 10f;
        private const float SectionGap = 30f;
        private const float SectionLabelHeight = 20f;
        private const float ScrollViewMinHeight = 120f;
        private const float EmptyStateTopPadding = 20f;

        private const int TagLongLabelCharThreshold = 10;
        private const int TagNineSliceBorder = 32;
        private const int TagNineSliceCorner = 8;
        private const int CardNineSliceBorder = 16;
        private const int CardNineSliceCorner = 4;

        private static readonly Color TagTextUnselectedColor = new Color(216f / 255f, 216f / 255f, 216f / 255f, 1f);
        private static readonly Color DescTextColor = new Color(128f / 255f, 128f / 255f, 128f / 255f, 1f);
        private static readonly Color CardFallbackBackgroundColor = new Color(26f / 255f, 26f / 255f, 26f / 255f, 1f);
        private static readonly Color PreviewPlaceholderColor = new Color(0.2f, 0.2f, 0.2f, 1f);
        private static readonly Color PreviewShadowColor = new Color(0f, 0f, 0f, 0.25f);

        private List<MaterialTemplateOptionConfig> _templates;
        private Action<MaterialTemplateOptionConfig> _onSelected;
        private readonly Dictionary<string, Texture2D> _previewCache = new Dictionary<string, Texture2D>();
        private Vector2 _scrollPosition;
        private string _searchText = string.Empty;
        private string _selectedTag = AllTag;
        private string _currentSelectedId = string.Empty;

        private GUIStyle _tagTextStyle;
        private GUIStyle _sectionLabelStyle;
        private GUIStyle _nameStyle;
        private GUIStyle _descStyle;

        public static void ShowWindow(
            List<MaterialTemplateOptionConfig> templates,
            Action<MaterialTemplateOptionConfig> onSelected,
            string title = null,
            MaterialTemplateOptionConfig currentSelected = null)
        {
            var resolvedTitle = title ?? TJGeneratorsL10n.L("选择纹理走势");
            var window = GetWindow<TJGeneratorsTexturePatternSelectorWindow>(resolvedTitle);
            window.minSize = new Vector2(WindowWidth, WindowHeight);
            window.maxSize = new Vector2(WindowWidth, WindowHeight);
            window._templates = templates;
            window._onSelected = onSelected;
            window._currentSelectedId = currentSelected?.id ?? string.Empty;
            window.LoadPreviews();
            window.Show();
        }

        private void LoadPreviews()
        {
            _previewCache.Clear();
            if (_templates == null)
                return;

            foreach (var template in _templates)
            {
                if (template == null || string.IsNullOrEmpty(template.id))
                    continue;

                LoadPreview(template.id);
            }
        }

        private void LoadPreview(string templateId)
        {
            if (_previewCache.ContainsKey(templateId))
                return;

            _previewCache[templateId] = LoadTemplateTexture(templateId);
        }

        private static Texture2D LoadTemplateTexture(string templateId)
        {
            var assetPath = TJGeneratorsMaterialTemplateGenerator.GetTemplateImagePath(templateId);
            var texture = EditorGUIUtility.Load(assetPath) as Texture2D;
            if (texture != null)
                return texture;

            var absolutePath = TJGeneratorsMaterialTemplateGenerator.GetAbsoluteTemplatePath(templateId);
            if (!File.Exists(absolutePath))
                return null;

            texture = new Texture2D(2, 2);
            texture.LoadImage(File.ReadAllBytes(absolutePath));
            return texture;
        }

        private void EnsureStyles()
        {
            if (_tagTextStyle != null)
                return;

            _tagTextStyle = CreateTagTextStyle();
            _sectionLabelStyle = CreateSectionLabelStyle();
            _nameStyle = CreateCardNameStyle();
            _descStyle = CreateCardDescStyle();
        }

        private static GUIStyle CreateTagTextStyle()
        {
            return new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                font = CommonStyles.SourceHanSansRegularFont,
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                clipping = TextClipping.Clip,
                wordWrap = false
            };
        }

        private static GUIStyle CreateSectionLabelStyle()
        {
            var style = new GUIStyle(EditorStyles.label)
            {
                font = CommonStyles.SourceHanSansMediumFont ?? CommonStyles.SourceHanSansRegularFont,
                fontSize = 16,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft
            };
            style.normal.textColor = Color.white;
            return style;
        }

        private static GUIStyle CreateCardNameStyle()
        {
            var style = new GUIStyle(EditorStyles.label)
            {
                font = CommonStyles.SourceHanSansRegularFont,
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter
            };
            style.normal.textColor = Color.white;
            return style;
        }

        private static GUIStyle CreateCardDescStyle()
        {
            var style = new GUIStyle(EditorStyles.label)
            {
                font = CommonStyles.SourceHanSansRegularFont,
                fontSize = 10,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter
            };
            style.normal.textColor = DescTextColor;
            return style;
        }

        private void OnGUI()
        {
            UIComponents.SyncImguiLeftMouseHeldFromEvent();

            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(0f, 0f, position.width, position.height), CommonStyles.WindowBackgroundColor);

            EnsureStyles();

            DrawSearchBar();
            DrawTagRow();
            DrawGroupedCards();
        }

        private void DrawSearchBar()
        {
            GUILayout.Space(SearchTop);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(ContentLeft);

            var newSearch = UIComponents.DrawSearchTextField(
                _searchText,
                TJGeneratorsL10n.L("输入关键词搜索..."),
                GUILayout.Width(SearchWidth),
                GUILayout.MinWidth(SearchWidth),
                GUILayout.MaxWidth(SearchWidth),
                GUILayout.Height(SearchHeight));

            if (newSearch != _searchText)
            {
                _searchText = newSearch;
                Repaint();
            }

            GUILayout.Space(ContentRight);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTagRow()
        {
            var spacer = Mathf.Max(0f, TagTop - (SearchTop + SearchHeight));
            if (spacer > 0f)
                GUILayout.Space(spacer);

            var tags = BuildTags();
            if (tags.Count == 0)
                return;

            var rowRect = GUILayoutUtility.GetRect(position.width, TagButtonHeight, GUILayout.Height(TagButtonHeight));
            var textures = ResolveTagButtonTextures();
            var x = ContentLeft;
            var maxX = position.width - ContentRight;

            foreach (var tag in tags)
            {
                var buttonWidth = GetTagButtonWidth(tag);
                if (x + buttonWidth > maxX)
                    break;

                var buttonRect = new Rect(Mathf.Floor(x), Mathf.Floor(rowRect.y), Mathf.Floor(buttonWidth), TagButtonHeight);
                DrawTagButton(tag, buttonRect, textures);
                x += buttonWidth + TagButtonGap;
            }

            GUILayout.Space(TagRowBottomGap);
        }

        private static TagButtonTextures ResolveTagButtonTextures()
        {
            var greenStyle = CommonStyles.GenerateButtonSolidStyle;
            var greenNormal = greenStyle.normal.background;
            var greenHover = greenStyle.hover.background ?? greenNormal;
            var greenPressed = greenStyle.active.background ?? greenHover;

            return new TagButtonTextures(
                CommonStyles.BlackBtnNormal4xTexture,
                greenNormal,
                greenHover,
                greenPressed);
        }

        private void DrawTagButton(string tag, Rect buttonRect, TagButtonTextures textures)
        {
            var isSelected = string.Equals(_selectedTag, tag, StringComparison.OrdinalIgnoreCase);
            var isHover = buttonRect.Contains(Event.current.mousePosition);
            var isPressing = isHover && UIComponents.ImguiLeftMouseHeld;
            var background = textures.ResolveBackground(isSelected, isHover, isPressing);

            if (background != null)
                UIComponents.DrawNineSliceFixed(buttonRect, background, TagNineSliceBorder, TagNineSliceCorner);

            _tagTextStyle.normal.textColor = isSelected ? Color.white : TagTextUnselectedColor;
            GUI.Label(buttonRect, tag, _tagTextStyle);

            EditorGUIUtility.AddCursorRect(buttonRect, MouseCursor.Link);
            if (!GUI.Button(buttonRect, GUIContent.none, GUIStyle.none))
                return;

            _selectedTag = tag;
            _scrollPosition = Vector2.zero;
            Repaint();
            Event.current.Use();
        }

        private static float GetTagButtonWidth(string tag)
        {
            return tag.Length >= TagLongLabelCharThreshold ? TagButtonLongWidth : TagButtonMinWidth;
        }

        private List<string> BuildTags()
        {
            var tags = new List<string> { AllTag };
            if (_templates == null)
                return tags;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var template in _templates)
            {
                if (template == null || string.IsNullOrEmpty(template.category))
                    continue;

                var localizedCategory = LocalizeCategory(template.category);
                if (seen.Add(localizedCategory))
                    tags.Add(localizedCategory);
            }

            return tags;
        }

        private IEnumerable<MaterialTemplateOptionConfig> FilteredTemplates()
        {
            if (_templates == null)
                yield break;

            var keyword = (_searchText ?? string.Empty).Trim();
            var keywordLower = string.IsNullOrEmpty(keyword) ? null : keyword.ToLowerInvariant();

            foreach (var template in _templates)
            {
                if (template == null || !MatchesFilters(template, keywordLower))
                    continue;

                yield return template;
            }
        }

        private bool MatchesFilters(MaterialTemplateOptionConfig template, string keywordLower)
        {
            if (!MatchesSelectedTag(template))
                return false;

            if (keywordLower == null)
                return true;

            return ContainsIgnoreCase(TJGeneratorsL10n.L(template.name), keywordLower)
                || ContainsIgnoreCase(TJGeneratorsL10n.L(template.description), keywordLower)
                || ContainsIgnoreCase(LocalizeCategory(template.category), keywordLower)
                || ContainsIgnoreCase(template.id, keywordLower);
        }

        private bool MatchesSelectedTag(MaterialTemplateOptionConfig template)
        {
            return _selectedTag == AllTag
                || string.Equals(LocalizeCategory(template.category), _selectedTag, StringComparison.OrdinalIgnoreCase);
        }

        private static string LocalizeCategory(string category)
        {
            return string.IsNullOrEmpty(category)
                ? TJGeneratorsL10n.L("其他")
                : TJGeneratorsL10n.L(category);
        }

        private static bool ContainsIgnoreCase(string value, string keywordLower)
        {
            return !string.IsNullOrEmpty(value)
                && !string.IsNullOrEmpty(keywordLower)
                && value.ToLowerInvariant().Contains(keywordLower);
        }

        private void DrawGroupedCards()
        {
            var grouped = FilteredTemplates()
                .OrderBy(template => template.order)
                .ThenBy(template => template.name)
                .GroupBy(template => LocalizeCategory(template.category))
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _scrollPosition = GUILayout.BeginScrollView(
                _scrollPosition,
                false,
                true,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUILayout.Height(GetScrollViewHeight()));

            if (grouped.Count == 0)
            {
                DrawEmptyState();
                GUILayout.EndScrollView();
                return;
            }

            foreach (var group in grouped)
                DrawCategorySection(group.Key, group.ToList());

            GUILayout.EndScrollView();
        }

        private float GetScrollViewHeight()
        {
            var contentTop = TagTop + TagButtonHeight + TagRowBottomGap + EmptyStateTopPadding;
            return Mathf.Max(ScrollViewMinHeight, position.height - contentTop);
        }

        private static void DrawEmptyState()
        {
            GUILayout.Space(EmptyStateTopPadding);
            GUILayout.Label(TJGeneratorsL10n.L("当前分类下没有选项"), CommonStyles.SmallGreyCenterLabelStyle);
        }

        private void DrawCategorySection(string category, IReadOnlyList<MaterialTemplateOptionConfig> templates)
        {
            GUILayout.Space(2f);
            DrawSectionHeader(category);
            GUILayout.Space(LabelToCardsGap);
            DrawTemplateCardRows(templates);
            GUILayout.Space(SectionGap - CardGap);
        }

        private void DrawSectionHeader(string category)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(ContentLeft);
            GUILayout.Label(category, _sectionLabelStyle, GUILayout.Height(SectionLabelHeight));
            GUILayout.Space(ContentRight);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTemplateCardRows(IReadOnlyList<MaterialTemplateOptionConfig> templates)
        {
            var cardsPerRow = (int)CardsPerRow;
            var index = 0;

            while (index < templates.Count)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(ContentLeft);

                var rowCount = Mathf.Min(cardsPerRow, templates.Count - index);
                for (var column = 0; column < rowCount; column++)
                {
                    DrawTextureCard(templates[index++]);
                    if (column < rowCount - 1)
                        GUILayout.Space(CardGap);
                }

                GUILayout.FlexibleSpace();
                GUILayout.Space(ContentRight);
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(CardGap);
            }
        }

        private void DrawTextureCard(MaterialTemplateOptionConfig template)
        {
            var isSelected = IsTemplateSelected(template);
            var cardRect = GUILayoutUtility.GetRect(CardWidth, CardHeight, GUILayout.Width(CardWidth), GUILayout.Height(CardHeight));

            DrawCardBackground(cardRect, isSelected);
            DrawCardPreview(cardRect, template);
            DrawCardLabels(cardRect, template);
            HandleCardClick(cardRect, template);
        }

        private bool IsTemplateSelected(MaterialTemplateOptionConfig template)
        {
            return !string.IsNullOrEmpty(_currentSelectedId)
                && string.Equals(_currentSelectedId, template.id, StringComparison.OrdinalIgnoreCase);
        }

        private static void DrawCardBackground(Rect cardRect, bool isSelected)
        {
            var cardBackground = isSelected
                ? CommonStyles.ItemBoxChecked4xTexture
                : CommonStyles.ItemBoxNormal4xTexture;

            if (cardBackground != null)
                UIComponents.DrawNineSliceFixed(cardRect, cardBackground, CardNineSliceBorder, CardNineSliceCorner);
            else
                EditorGUI.DrawRect(cardRect, CardFallbackBackgroundColor);
        }

        private void DrawCardPreview(Rect cardRect, MaterialTemplateOptionConfig template)
        {
            var imageRect = new Rect(
                cardRect.x + CardPadding,
                cardRect.y + CardPadding,
                CardImageWidth,
                CardImageHeight);

            if (_previewCache.TryGetValue(template.id, out var preview) && preview != null)
            {
                var shadowRect = new Rect(imageRect.x, imageRect.y + CardImageShadowOffset, imageRect.width, imageRect.height);
                EditorGUI.DrawRect(shadowRect, PreviewShadowColor);
                GUI.DrawTexture(imageRect, preview, ScaleMode.ScaleToFit, true);
                return;
            }

            EditorGUI.DrawRect(imageRect, PreviewPlaceholderColor);
            GUI.Label(imageRect, TJGeneratorsL10n.L("无预览"), CommonStyles.SmallGreyCenterLabelStyle);
        }

        private void DrawCardLabels(Rect cardRect, MaterialTemplateOptionConfig template)
        {
            var contentWidth = cardRect.width - CardPadding * 2f;
            var nameRect = new Rect(cardRect.x + CardPadding, cardRect.y + CardNameTop, contentWidth, CardNameHeight);
            var descRect = new Rect(cardRect.x + CardPadding, cardRect.y + CardDescTop, contentWidth, CardDescHeight);

            GUI.Label(nameRect, TJGeneratorsL10n.L(template.name ?? string.Empty).ToUpperInvariant(), _nameStyle);
            GUI.Label(descRect, TJGeneratorsL10n.L(template.description ?? string.Empty).ToUpperInvariant(), _descStyle);
        }

        private void HandleCardClick(Rect cardRect, MaterialTemplateOptionConfig template)
        {
            EditorGUIUtility.AddCursorRect(cardRect, MouseCursor.Link);

            var currentEvent = Event.current;
            if (currentEvent.type != EventType.MouseDown
                || currentEvent.button != 0
                || !cardRect.Contains(currentEvent.mousePosition))
                return;

            _onSelected?.Invoke(template);
            currentEvent.Use();
            Close();
        }

        private readonly struct TagButtonTextures
        {
            private readonly Texture2D _blackNormal;
            private readonly Texture2D _greenNormal;
            private readonly Texture2D _greenHover;
            private readonly Texture2D _greenPressed;

            public TagButtonTextures(
                Texture2D blackNormal,
                Texture2D greenNormal,
                Texture2D greenHover,
                Texture2D greenPressed)
            {
                _blackNormal = blackNormal;
                _greenNormal = greenNormal;
                _greenHover = greenHover;
                _greenPressed = greenPressed;
            }

            public Texture2D ResolveBackground(bool isSelected, bool isHover, bool isPressing)
            {
                if (isPressing)
                    return _greenPressed;

                if (isHover)
                    return _greenHover;

                return isSelected ? _greenNormal : _blackNormal;
            }
        }
    }
}
#endif
