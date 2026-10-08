#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using TJGenerators.Utils;

namespace TJGenerators.UI
{
    /// <summary>
    /// 左侧面板底部用户信息栏：邮箱。
    /// </summary>
    public static class UserInfoBar
    {
        private const float BarHeight = 56f;
        private const float ContentRowHeight = 40f;
        private const float HorizontalPadding = 16f;
        private const string DefaultEmailPlaceholder = "user123@unity.cn";

        private static GUIStyle s_emailRowStyle;

        public static float Height => BarHeight;

        /// <summary>
        /// 绘制用户信息栏。email 为 null 时使用 <see cref="UserInfoHelper.LastUserInfo"/>。
        /// </summary>
        public static void Draw(
            float windowHeight,
            float leftPanelWidth,
            string email = null)
        {
            Rect barRect = GetBarRect(windowHeight, leftPanelWidth);
            EditorGUI.DrawRect(barRect, CommonStyles.WindowBackgroundColor);

            Rect rowRect = GetContentRowRect(barRect);
            string emailText = ResolveEmail(email);
            float emailWidth = Mathf.Max(1f, leftPanelWidth - HorizontalPadding * 2f);
            var emailRect = new Rect(HorizontalPadding, rowRect.y, emailWidth, rowRect.height);
            GUI.Label(emailRect, emailText, GetEmailRowStyle());
        }

        private static Rect GetBarRect(float windowHeight, float panelWidth) =>
            new Rect(0f, windowHeight - BarHeight, panelWidth, BarHeight);

        private static Rect GetContentRowRect(Rect barRect)
        {
            float rowY = barRect.y + (BarHeight - ContentRowHeight) * 0.5f;
            return new Rect(barRect.x, rowY, barRect.width, ContentRowHeight);
        }

        private static string ResolveEmail(string email)
        {
            if (!string.IsNullOrEmpty(email))
                return email;

            string userEmail = UserInfoHelper.LastUserInfo?.email;
            return string.IsNullOrEmpty(userEmail) ? DefaultEmailPlaceholder : userEmail;
        }

        private static GUIStyle GetEmailRowStyle()
        {
            if (s_emailRowStyle == null)
            {
                s_emailRowStyle = new GUIStyle(CommonStyles.ProfileEmailStyle)
                {
                    alignment = TextAnchor.MiddleLeft
                };
            }
            return s_emailRowStyle;
        }
    }
}
#endif
