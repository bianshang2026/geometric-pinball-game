#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
using UnityEditor;
using UnityEngine;

using UnityTcp.Editor.Helpers;
namespace UnityTcp.Editor.Native
{
    /// <summary>
    /// A utility EditorWindow shown during offscreen streaming.
    /// Displays a message that the editor is hidden
    /// and provides a button to disconnect and restore the editor.
    ///
    /// The native window handle is excluded from hide operations in
    /// NativeWindowBridgeHost, so it remains visible while other Unity
    /// windows are hidden.
    ///
    /// Focus() calls triggered by popup creation target specific Unity
    /// ContainerWindows (not all windows), so this mask window is NOT
    /// brought to front by those calls.
    /// </summary>
    internal class StreamingMaskWindow : EditorWindow
    {
        private static StreamingMaskWindow s_Instance;
        private static bool s_ClosingFromCode;

        private GUIStyle _titleStyle;
        private GUIStyle _messageStyle;
        private GUIStyle _buttonStyle;
        private bool _stylesInitialized;

        private const float kWindowWidth = 380f;
        private const float kWindowHeight = 170f;

#if TUANJIE_1_OR_NEWER || TUANJIE_1
        private const string kEditorName = "Tuanjie Editor";
#else
        private const string kEditorName = "Unity Editor";
#endif

        internal static string WindowTitle => kEditorName + " - Streaming";

        internal static StreamingMaskWindow TryGetInstance() => s_Instance;

        internal static StreamingMaskWindow ShowMask()
        {
            if (s_Instance != null)
            {
                ApplyBottomRightPosition(s_Instance);
                s_Instance.ShowUtility();
                CodelyLogger.Log("[StreamingMask] Mask window re-shown");
                return s_Instance;
            }

            // After domain reload, s_Instance is null but a serialized
            // EditorWindow instance may still exist. Reuse it — Close()+create
            // races OnDestroy (Unity Close is often deferred) and the dying
            // window would clear the new s_Instance after a few reconnects.
            var existing = Resources.FindObjectsOfTypeAll<StreamingMaskWindow>();
            StreamingMaskWindow first = null;
            foreach (var w in existing)
            {
                if (w == null) continue;
                if (first == null)
                {
                    first = w;
                    continue;
                }
                // Extra domain-reload leftovers. Close is deferred, but
                // OnDestroy only clears s_Instance when s_Instance == this.
                try { w.Close(); }
                catch (System.Exception) { }
            }
            if (first != null)
            {
                s_Instance = first;
                first.titleContent = new GUIContent(WindowTitle);
                first.minSize = new Vector2(kWindowWidth, kWindowHeight);
                first.maxSize = new Vector2(kWindowWidth, kWindowHeight);
                ApplyBottomRightPosition(first);
                first.ShowUtility();
                CodelyLogger.Log("[StreamingMask] Mask window reused after domain reload");
                return first;
            }

            var win = CreateInstance<StreamingMaskWindow>();
            win.titleContent = new GUIContent(WindowTitle);
            win.minSize = new Vector2(kWindowWidth, kWindowHeight);
            win.maxSize = new Vector2(kWindowWidth, kWindowHeight);
            win.ShowUtility();
            ApplyBottomRightPosition(win);

            s_Instance = win;
            CodelyLogger.Log("[StreamingMask] Mask window shown");
            return win;
        }

        internal static void ApplyBottomRightPosition(EditorWindow win)
        {
            if (win == null) return;
            // Bottom-right of the primary screen, just above the taskbar.
            // Screen.currentResolution is physical pixels; EditorWindow.position is logical.
            const float kTaskbarHeight = 48f;
            const float kBottomMargin = 8f;
            float dpiScale = EditorGUIUtility.pixelsPerPoint;
            float logicalScreenW = Screen.currentResolution.width / dpiScale;
            float logicalScreenH = Screen.currentResolution.height / dpiScale;
            win.position = new Rect(
                logicalScreenW - kWindowWidth,
                logicalScreenH - kTaskbarHeight - kWindowHeight - kBottomMargin,
                kWindowWidth,
                kWindowHeight);
        }

        /// <summary>
        /// Programmatically close the mask window (called from StopOffscreenCapture).
        /// Sets a guard flag to prevent OnDestroy from triggering a recursive stop.
        /// </summary>
        internal static void HideMask()
        {
            s_ClosingFromCode = true;
            try
            {
                if (s_Instance != null)
                {
                    s_Instance.Close();
                }

                // Domain reload can clear s_Instance while the serialized
                // EditorWindow object still exists. Close any such zombies.
                foreach (var w in Resources.FindObjectsOfTypeAll<StreamingMaskWindow>())
                {
                    if (w != null && w != s_Instance)
                    {
                        w.Close();
                    }
                }
            }
            catch (System.Exception ex)
            {
                CodelyLogger.LogWarning($"[StreamingMask] HideMask close failed: {ex.Message}");
            }
            finally
            {
                s_ClosingFromCode = false;
                s_Instance = null;
            }
            CodelyLogger.Log("[StreamingMask] Mask window hidden");
        }

        internal static bool IsActive => s_Instance != null;

        private void OnGUI()
        {
            InitStyles();

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField(kEditorName, _titleStyle);

            EditorGUILayout.Space(8);

            EditorGUILayout.LabelField(
                "Editor is hidden while streaming via Codely Cowork.",
                _messageStyle);

            EditorGUILayout.Space(16);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(
                    "Disconnect & Restore",
                    _buttonStyle,
                    GUILayout.Width(200),
                    GUILayout.Height(32)))
            {
                EditorApplication.delayCall += () =>
                {
                    NativeWindowBridgeHost.StopOffscreenCapture();
                };
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
        }

        private void OnDestroy()
        {
            // Close() is often deferred. A dying leftover instance must not
            // clear a newer s_Instance or stop an active stream.
            if (s_Instance != this)
                return;

            s_Instance = null;
            if (s_ClosingFromCode)
                return;

            // User closed the mask window manually (e.g. clicked X).
            // Treat this as a disconnect request.
            if (NativeWindowBridgeHost.IsOffscreenActive)
            {
                CodelyLogger.Log("[StreamingMask] User closed mask window, stopping offscreen capture");
                EditorApplication.delayCall += () =>
                {
                    NativeWindowBridgeHost.StopOffscreenCapture();
                };
            }
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleCenter
            };

            _messageStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) }
            };

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(16, 16, 6, 6)
            };
        }
    }
}
#else
using UnityEditor;

namespace UnityTcp.Editor.Native
{
    /// <summary>
    /// No-op placeholder for non-Windows/macOS editor platforms.
    /// This keeps cross-platform compilation valid when shared code
    /// references StreamingMaskWindow in common paths.
    /// </summary>
    internal class StreamingMaskWindow : EditorWindow
    {
        internal static string WindowTitle => "Editor - Streaming";

        internal static StreamingMaskWindow TryGetInstance() => null;

        internal static StreamingMaskWindow ShowMask()
        {
            return null;
        }

        internal static void ApplyBottomRightPosition(EditorWindow win)
        {
        }

        internal static void HideMask()
        {
            // Intentionally empty on unsupported platforms.
        }

        internal static bool IsActive
        {
            get { return false; }
        }
    }
}
#endif
