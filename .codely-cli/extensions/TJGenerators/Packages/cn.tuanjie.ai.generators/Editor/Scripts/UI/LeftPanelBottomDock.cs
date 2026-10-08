#if UNITY_EDITOR
using System;
using UnityEngine;
using TJGenerators.Utils;

namespace TJGenerators.UI
{
    /// <summary>
    /// 左栏底部固定区：操作按钮（生成/搜索）与 <see cref="UserInfoBar"/>，绝对定位，不参与滚动。
    /// </summary>
    public static class LeftPanelBottomDock
    {
        public const float ActionButtonHeight = 30f;
        /// <summary>滚动参数区与底部生成按钮之间的垂直间距。</summary>
        public const float ActionButtonTopGap = 16f;

        const float PlayModeHintHeight = 14f;

        public readonly struct Layout
        {
            public readonly Rect buttonRect;

            public Layout(Rect buttonRect) => this.buttonRect = buttonRect;
        }

        /// <summary>按钮底边到提示文案的间距。</summary>
        static float PlayModeHintGap => CommonStyles.Space1;

        // 预留高度不含提示：Play 模式不抬按钮、不推用户栏，提示叠在中间空隙。
        static float ReservedHeight => ActionButtonHeight + UserInfoBar.Height;

        public static float GetScrollViewHeight(float windowHeight) =>
            windowHeight - ReservedHeight - ActionButtonTopGap;

        static Layout CalculateLayout(float windowHeight)
        {
            // 按钮与用户栏位置始终与 Edit 模式一致。
            float buttonBottom = windowHeight - UserInfoBar.Height;
            float buttonTop = buttonBottom - ActionButtonHeight;
            return new Layout(new Rect(
                CommonStyles.LeftContentPadding,
                buttonTop,
                CommonStyles.LeftComponentWidth,
                ActionButtonHeight));
        }

        /// <summary>
        /// 绘制底部 Dock：先执行 <paramref name="drawAction"/>，再绘制 <see cref="UserInfoBar"/>，
        /// 最后叠画 Play 模式提示（避免被用户栏背景盖住）。
        /// </summary>
        /// <param name="playModeHint">Play 模式提示文案；null 时使用生成场景的 <see cref="TJGeneratorsPlayModeGuard.ShortHint"/>。</param>
        public static void Draw(
            float windowHeight,
            float panelWidth,
            Action<Layout> drawAction,
            string email = null,
            string playModeHint = null)
        {
            Layout layout = CalculateLayout(windowHeight);
            drawAction?.Invoke(layout);

            UserInfoBar.Draw(windowHeight, panelWidth, email);

            if (!TJGeneratorsPlayModeGuard.IsActive)
                return;

            float buttonBottom = windowHeight - UserInfoBar.Height;
            var hintRect = new Rect(
                CommonStyles.LeftContentPadding,
                buttonBottom + PlayModeHintGap,
                CommonStyles.LeftComponentWidth,
                PlayModeHintHeight);
            if (GUI.Button(hintRect, playModeHint ?? TJGeneratorsPlayModeGuard.ShortHint, PlayModeHintLabelStyle))
                TJGeneratorsPlayModeGuard.ExitPlayMode();
        }

        static GUIStyle s_playModeHintLabelStyle;
        static GUIStyle PlayModeHintLabelStyle
        {
            get
            {
                if (s_playModeHintLabelStyle == null)
                {
                    s_playModeHintLabelStyle = new GUIStyle(CommonStyles.MiniRedLabelStyle)
                    {
                        alignment = TextAnchor.MiddleLeft,
                        clipping = TextClipping.Clip,
                        padding = new RectOffset(0, 0, 0, 0),
                        margin = new RectOffset(0, 0, 0, 0)
                    };
                }
                return s_playModeHintLabelStyle;
            }
        }
    }
}
#endif
