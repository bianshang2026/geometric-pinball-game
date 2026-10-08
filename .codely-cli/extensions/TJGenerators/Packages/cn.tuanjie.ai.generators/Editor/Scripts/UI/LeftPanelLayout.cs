#if UNITY_EDITOR
using System;
using UnityEngine;

namespace TJGenerators.UI
{
    /// <summary>
    /// 生成器左栏可滚动参数区。底部操作按钮与 <see cref="UserInfoBar"/> 由 <see cref="LeftPanelBottomDock"/> 绝对绘制。
    /// </summary>
    public static class LeftPanelLayout
    {
        public static float GetScrollViewHeight(float windowHeight) =>
            LeftPanelBottomDock.GetScrollViewHeight(windowHeight);

        /// <summary>
        /// 左栏滚动参数区（高度已为底部 Dock 预留空间）。
        /// </summary>
        public static void DrawColumn(
            float windowHeight,
            float leftPanelWidth,
            ref Vector2 scrollPosition,
            Action drawScrollContent)
        {
            float scrollHeight = GetScrollViewHeight(windowHeight);

            GUILayout.BeginVertical(
                GUILayout.Width(leftPanelWidth),
                GUILayout.MinWidth(leftPanelWidth),
                GUILayout.MaxWidth(leftPanelWidth));

            scrollPosition = GUILayout.BeginScrollView(
                scrollPosition,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUILayout.Height(scrollHeight),
                GUILayout.Width(leftPanelWidth),
                GUILayout.MaxWidth(leftPanelWidth));
            drawScrollContent?.Invoke();
            GUILayout.EndScrollView();

            GUILayout.EndVertical();
        }
    }
}
#endif
