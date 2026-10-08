#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace TJGenerators.Utils
{
    /// <summary>
    /// EditorGUIUtility 版本兼容层。
    /// Unity 2021.2+ 使用 <see cref="EditorGUIUtility.GetMainWindowPosition"/>；
    /// 更早版本回退到屏幕分辨率区域。
    /// </summary>
    internal static class EditorGUIUtilityCompat
    {
        public static Rect GetMainWindowPosition()
        {
#if UNITY_2021_2_OR_NEWER
            return EditorGUIUtility.GetMainWindowPosition();
#else
            var resolution = Screen.currentResolution;
            return new Rect(0f, 0f, resolution.width, resolution.height);
#endif
        }
    }
}
#endif
