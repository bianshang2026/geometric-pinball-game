#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using TJGenerators.Config;
using TJGenerators.UI;
using TJGenerators.Utils;

namespace TJGenerators
{
    /// <summary>
    /// 材质选项选择器窗口 - 网格布局显示所有可用的选项（材质预设/纹理走势）
    /// </summary>
    public class TJGeneratorsMaterialTemplateSelectorWindow : EditorWindow
    {
        private List<MaterialTemplateOptionConfig> templates;
        private Dictionary<string, Texture2D> previewCache = new Dictionary<string, Texture2D>();
        private Action<MaterialTemplateOptionConfig> onSelected;
        private Vector2 scrollPosition;
        private string selectedCategory = null;
        private string searchText = "";
        private MaterialTemplateOptionConfig selectedTemplate = null;
        private string windowTitle = null;
        private bool showPreviewThumbnails = true;

        private GUIStyle cardStyle;
        private GUIStyle selectedCardStyle;
        private GUIStyle categoryNameStyle;
        private GUIStyle templateNameStyle;
        private GUIStyle templateDescStyle;
        private GUIStyle listRowStyle;
        private GUIStyle listRowSelectedStyle;
        private GUIStyle listNameStyle;
        private GUIStyle listDescStyle;

        private const float CardSize = 120f;
        private const float CardSpacing = 10f;

        public static void ShowWindow(
            List<MaterialTemplateOptionConfig> templates,
            Action<MaterialTemplateOptionConfig> onSelected,
            string title = null,
            bool showPreviewThumbnails = true)
        {
            var resolvedTitle = title ?? TJGeneratorsL10n.L("选择选项");
            var window = GetWindow<TJGeneratorsMaterialTemplateSelectorWindow>(resolvedTitle);
            window.minSize = showPreviewThumbnails ? new Vector2(500, 600) : new Vector2(420, 360);
            window.templates = templates;
            window.onSelected = onSelected;
            window.windowTitle = resolvedTitle;
            window.showPreviewThumbnails = showPreviewThumbnails;
            window.previewCache.Clear();
            if (showPreviewThumbnails)
                window.LoadPreviews();
            window.Show();
        }

        private void LoadPreviews()
        {
            previewCache.Clear();
            if (templates == null) return;

            foreach (var template in templates)
            {
                LoadPreview(template.id);
            }
        }

        private void LoadPreview(string templateId)
        {
            if (previewCache.ContainsKey(templateId)) return;

            string templatePath = TJGeneratorsMaterialTemplateGenerator.GetTemplateImagePath(templateId);
            Texture2D tex = null;

            // 尝试从 AssetDatabase 加载
            tex = EditorGUIUtility.Load(templatePath) as Texture2D;

            // 尝试从文件系统加载（使用正确的路径解析）
            if (tex == null)
            {
                string absolutePath = TJGeneratorsMaterialTemplateGenerator.GetAbsoluteTemplatePath(templateId);
                if (File.Exists(absolutePath))
                {
                    tex = new Texture2D(2, 2);
                    tex.LoadImage(File.ReadAllBytes(absolutePath));
                }
            }

            previewCache[templateId] = tex;
        }

        private void OnGUI()
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), CommonStyles.WindowBackgroundColor);
            InitializeStyles();

            // 搜索栏
            DrawSearchBar();

            // 分类筛选
            DrawCategoryFilter();

            if (showPreviewThumbnails)
                DrawTemplateGrid();
            else
                DrawTemplateList();

            // 底部按钮
            DrawBottomButtons();
        }

        private void InitializeStyles()
        {
            if (cardStyle != null) return;

            cardStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(5, 5, 5, 5),
                margin = new RectOffset(5, 5, 5, 5),
                alignment = TextAnchor.MiddleCenter
            };

            selectedCardStyle = new GUIStyle(cardStyle);
            selectedCardStyle.normal.background = CreateColorTexture(new Color(0.2f, 0.5f, 0.3f, 1f));

            categoryNameStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                margin = new RectOffset(10, 10, 10, 5)
            };

            templateNameStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };

            templateDescStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 9,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };

            listRowStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(12, 12, 8, 8),
                margin = new RectOffset(10, 10, 2, 4),
                alignment = TextAnchor.MiddleLeft
            };

            listRowSelectedStyle = new GUIStyle(listRowStyle);
            listRowSelectedStyle.normal.background = CreateColorTexture(CommonStyles.ThemeDarkGreenColor);

            listNameStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true
            };

            listDescStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                margin = new RectOffset(0, 0, 2, 0)
            };
        }

        private Texture2D CreateColorTexture(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        private void DrawSearchBar()
        {
            GUILayout.Space(CommonStyles.Space2);
            GUILayout.BeginHorizontal();
            GUILayout.Space(CommonStyles.Space2);
            GUILayout.Label(TJGeneratorsL10n.L("搜索："), GUILayout.Width(50));
            searchText = GUILayout.TextField(searchText, GUILayout.Height(20));
            if (GUILayout.Button("×", GUILayout.Width(20), GUILayout.Height(20)))
            {
                searchText = "";
                GUI.FocusControl(null);
            }
            GUILayout.Space(10);
            GUILayout.EndHorizontal();
        }

        private void DrawCategoryFilter()
        {
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUILayout.Space(10);

            // 获取所有分类
            var categories = new List<string> { TJGeneratorsL10n.L("全部") };
            if (templates != null)
            {
                foreach (var t in templates)
                {
                    if (!string.IsNullOrEmpty(t.category))
                    {
                        string cat = TJGeneratorsL10n.L(t.category);
                        if (!categories.Contains(cat))
                            categories.Add(cat);
                    }
                }
            }

            // 分类按钮
            foreach (var cat in categories)
            {
                bool isSelected = selectedCategory == cat;
                var style = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 11
                };
                if (isSelected)
                {
                    GUI.color = new Color(0.3f, 0.6f, 0.4f, 1f);
                }
                if (GUILayout.Button(TJGeneratorsL10n.L(cat), style, GUILayout.Height(24)))
                {
                    selectedCategory = cat;
                }
                GUI.color = Color.white;
            }

            GUILayout.FlexibleSpace();
            GUILayout.Space(10);
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
        }

        private Dictionary<string, List<MaterialTemplateOptionConfig>> BuildFilteredGroupedTemplates()
        {
            var filteredTemplates = new List<MaterialTemplateOptionConfig>();
            if (templates != null)
            {
                foreach (var t in templates)
                {
                    if (selectedCategory != TJGeneratorsL10n.L("全部") && TJGeneratorsL10n.L(t.category) != selectedCategory)
                        continue;

                    if (!string.IsNullOrEmpty(searchText))
                    {
                        string searchLower = searchText.ToLower();
                        string nameLower = (TJGeneratorsL10n.L(t.name) ?? "").ToLower();
                        string descLower = (TJGeneratorsL10n.L(t.description) ?? "").ToLower();
                        string catLower = (TJGeneratorsL10n.L(t.category) ?? "").ToLower();

                        if (!(nameLower.Contains(searchLower) ||
                              descLower.Contains(searchLower) ||
                              catLower.Contains(searchLower)))
                            continue;
                    }

                    filteredTemplates.Add(t);
                }
            }

            var groupedTemplates = new Dictionary<string, List<MaterialTemplateOptionConfig>>();
            foreach (var t in filteredTemplates)
            {
                string cat = TJGeneratorsL10n.L(t.category ?? "其他");
                if (!groupedTemplates.ContainsKey(cat))
                    groupedTemplates[cat] = new List<MaterialTemplateOptionConfig>();
                groupedTemplates[cat].Add(t);
            }

            foreach (var kvp in groupedTemplates)
                kvp.Value.Sort((a, b) => a.order.CompareTo(b.order));

            return groupedTemplates;
        }

        private void DrawTemplateGrid()
        {
            var groupedTemplates = BuildFilteredGroupedTemplates();

            // 滚动区域
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            foreach (var kvp in groupedTemplates)
            {
                // 分类标题
                GUILayout.Label(TJGeneratorsL10n.L(kvp.Key), categoryNameStyle);

                // 模板网格
                int perRow = Mathf.Max(1, (int)((position.width - 40) / (CardSize + CardSpacing)));
                var templateList = kvp.Value;

                for (int i = 0; i < templateList.Count; i += perRow)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(10);

                    for (int j = 0; j < perRow && (i + j) < templateList.Count; j++)
                    {
                        DrawTemplateCard(templateList[i + j], i + j);
                    }

                    GUILayout.FlexibleSpace();
                    GUILayout.Space(10);
                    GUILayout.EndHorizontal();
                }

                GUILayout.Space(10);
            }

            GUILayout.EndScrollView();
        }

        private void DrawTemplateList()
        {
            var groupedTemplates = BuildFilteredGroupedTemplates();

            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            foreach (var kvp in groupedTemplates)
            {
                GUILayout.Label(TJGeneratorsL10n.L(kvp.Key), categoryNameStyle);

                foreach (var template in kvp.Value)
                {
                    bool isSelected = selectedTemplate != null && selectedTemplate.id == template.id;
                    GUILayout.BeginHorizontal(isSelected ? listRowSelectedStyle : listRowStyle);
                    GUILayout.BeginVertical();
                    GUILayout.Label(TJGeneratorsL10n.L(template.name ?? ""), listNameStyle);
                    if (!string.IsNullOrEmpty(template.description))
                        GUILayout.Label(TJGeneratorsL10n.L(template.description), listDescStyle);
                    GUILayout.EndVertical();
                    GUILayout.EndHorizontal();

                    if (Event.current.type == EventType.MouseDown &&
                        GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                    {
                        if (Event.current.clickCount == 2)
                            ConfirmSelection(template);
                        else
                            selectedTemplate = template;
                        Event.current.Use();
                        Repaint();
                    }
                }

                GUILayout.Space(6);
            }

            GUILayout.EndScrollView();
        }

        private void DrawTemplateCard(MaterialTemplateOptionConfig template, int index)
        {
            bool isSelected = selectedTemplate != null && selectedTemplate.id == template.id;
            var style = isSelected ? selectedCardStyle : cardStyle;

            GUILayout.BeginVertical(style, GUILayout.Width(CardSize), GUILayout.Height(CardSize + 30));

            // 预览图
            Rect previewRect = GUILayoutUtility.GetRect(CardSize - 10, CardSize - 10);
            if (previewCache.TryGetValue(template.id, out var tex) && tex != null)
            {
                GUI.DrawTexture(previewRect, tex, ScaleMode.ScaleToFit);
            }
            else
            {
                EditorGUI.DrawRect(previewRect, new Color(0.2f, 0.2f, 0.2f, 1f));
                GUI.Label(previewRect, TJGeneratorsL10n.L("无预览"), CommonStyles.SmallGreyCenterLabelStyle);
            }

            // 名称
            GUILayout.Label(TJGeneratorsL10n.L(template.name), templateNameStyle, GUILayout.Height(16));

            // 描述
            GUILayout.Label(TJGeneratorsL10n.L(template.description), templateDescStyle, GUILayout.Height(14));

            GUILayout.EndVertical();

            // 点击选择
            if (Event.current.type == EventType.MouseDown && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
            {
                if (Event.current.clickCount == 2)
                {
                    // 双击确认
                    ConfirmSelection(template);
                }
                else
                {
                    selectedTemplate = template;
                }
                Event.current.Use();
                Repaint();
            }
        }

        private void DrawBottomButtons()
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button(TJGeneratorsL10n.L("取消"), GUILayout.Width(80), GUILayout.Height(28)))
            {
                Close();
            }

            GUI.enabled = selectedTemplate != null;
            if (GUILayout.Button(TJGeneratorsL10n.L("确定"), GUILayout.Width(80), GUILayout.Height(28)))
            {
                if (selectedTemplate != null)
                {
                    ConfirmSelection(selectedTemplate);
                }
            }
            GUI.enabled = true;

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
        }

        private void ConfirmSelection(MaterialTemplateOptionConfig template)
        {
            onSelected?.Invoke(template);
            Close();
        }
    }
}
#endif
