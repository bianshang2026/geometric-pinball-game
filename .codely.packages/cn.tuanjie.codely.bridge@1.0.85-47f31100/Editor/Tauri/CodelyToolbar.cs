#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
#if UNITY_6000_3_OR_NEWER
using Cn.Tuanjie.Codely.Editor;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace UnityTcp.Editor.Windows
{
    public static class CodelyToolbarUnity6
    {
        private const string IconBase = "Packages/cn.tuanjie.codely.bridge/Editor/Bridge/Icons/";

        [MainToolbarElement("Tuanjie AI/Tuanjie AI Bridge", defaultDockPosition = MainToolbarDockPosition.Left)]
        public static MainToolbarElement CreateButton()
        {

            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(
                EditorGUIUtility.isProSkin
                    ? IconBase + "toolbar.png"
                    : IconBase + "toolbar_light_theme.png");
            var content = new MainToolbarContent(string.Empty, icon, "AI");
            return new MainToolbarButton(content, CodelyWindow.ToggleCodely);
        }
    }
}

#elif UNITY_2020_1_OR_NEWER

using System.Reflection;
using Cn.Tuanjie.Codely.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Windows
{
    [InitializeOnLoad]
    public static class CodelyToolbar
    {
        private const string ButtonName = "CodelyToolbarBtn";

        static CodelyToolbar()
        {
            if (EditorProcessGuard.ShouldSkipEditorUiAutoStart()) return;

            EditorApplication.delayCall += AttachToToolbar;
            EditorApplication.update += EnsureAttached;
        }

        private static void EnsureAttached()
        {
            var toolbarType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Toolbar");
            if (toolbarType == null) return;

            var toolbars = Resources.FindObjectsOfTypeAll(toolbarType);
            if (toolbars.Length == 0) return;

            var rootField = toolbarType.GetField("m_Root", BindingFlags.NonPublic | BindingFlags.Instance);
            if (rootField == null) return;

            var root = rootField.GetValue(toolbars[0]) as VisualElement;
            var leftZone = root?.Q("ToolbarZoneLeftAlign");
            if (leftZone == null) return;

            if (leftZone.Q(ButtonName) == null)
            {
                AttachToToolbar();
            }
        }

        private static void AttachToToolbar()
        {
            var toolbarType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Toolbar");
            if (toolbarType == null) return;

            var toolbars = Resources.FindObjectsOfTypeAll(toolbarType);
            if (toolbars.Length == 0)
            {
                EditorApplication.delayCall += AttachToToolbar;
                return;
            }

            var rootField = toolbarType.GetField("m_Root", BindingFlags.NonPublic | BindingFlags.Instance);
            if (rootField == null) return;

            var root = rootField.GetValue(toolbars[0]) as VisualElement;
            if (root == null)
            {
                EditorApplication.delayCall += AttachToToolbar;
                return;
            }

            var leftZone = root.Q("ToolbarZoneLeftAlign");
            if (leftZone == null)
            {
                EditorApplication.delayCall += AttachToToolbar;
                return;
            }

            if (leftZone.Q(ButtonName) != null) return;

            const string iconBase = "Packages/cn.tuanjie.codely.bridge/Editor/Bridge/Icons/";
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(
                EditorGUIUtility.isProSkin
                    ? iconBase + "toolbar.png"
                    : iconBase + "toolbar_light_theme.png");

            var button = new ToolbarButton(CodelyWindow.ToggleCodely)
            {
                name = ButtonName, 
                tooltip = "AI"
            };

            button.style.paddingTop = 2;
            button.style.paddingBottom = 2;
            button.style.paddingLeft = 6;
            button.style.paddingRight = 6;
            button.style.marginTop = 0;
            button.style.marginBottom = -1;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            button.style.flexDirection = FlexDirection.Row;

            if (icon != null)
            {
                var iconImage = new Image
                {
                    image = icon,
                    scaleMode = ScaleMode.ScaleToFit,
                    pickingMode = PickingMode.Ignore,
                };
                iconImage.style.width = 12;
                iconImage.style.height = 12;
                button.Add(iconImage);
            }

            var label = new Label("AI")
            {
                pickingMode = PickingMode.Ignore,
            };
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginLeft = icon != null ? 4 : 0;
            button.Add(label);

            leftZone.Add(button);

            leftZone.RegisterCallback<DetachFromPanelEvent>(_ =>
                EditorApplication.delayCall += AttachToToolbar);
        }
    }
}
#endif
#endif
