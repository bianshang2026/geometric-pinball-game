#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX
using System;
using System.Reflection;

// ReSharper disable once CheckNamespace
namespace Unity.InternalBridge
{
    /// <summary>
    /// Reflection-based wrapper around UnityEditor.Connect.UnityConnect (an internal type).
    /// Using reflection avoids depending on the magic
    /// `Unity.InternalAPIEditorBridge.&lt;contract&gt;` asmdef whose contract number changes
    /// between Unity major versions (001 in 2019, 010 in 2020, 016/017 in 2021+, ...).
    /// </summary>
    public static class UnityConnectSession
    {
        private static readonly Type s_Type;
        private static readonly object s_Instance;
        private static readonly MethodInfo s_GetAccessToken;
        private static readonly MethodInfo s_GetUserId;
        private static readonly MethodInfo s_GetUserName;
        private static readonly PropertyInfo s_LoggedIn;
        private static readonly MethodInfo s_RefreshAccessToken;
        private static readonly MethodInfo s_ShowLogin;

        static UnityConnectSession()
        {
            try
            {
                var editorAssembly = typeof(UnityEditor.Editor).Assembly;
                s_Type = editorAssembly.GetType("UnityEditor.Connect.UnityConnect");
                if (s_Type == null) return;

                var instanceProp = s_Type.GetProperty("instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (instanceProp == null) return;
                s_Instance = instanceProp.GetValue(null);
                if (s_Instance == null) return;

                const BindingFlags inst = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;
                s_GetAccessToken     = s_Type.GetMethod("GetAccessToken",     inst, null, Type.EmptyTypes, null);
                s_GetUserId          = s_Type.GetMethod("GetUserId",          inst, null, Type.EmptyTypes, null);
                s_GetUserName        = s_Type.GetMethod("GetUserName",        inst, null, Type.EmptyTypes, null);
                s_LoggedIn           = s_Type.GetProperty("loggedIn",         inst);
                s_RefreshAccessToken = s_Type.GetMethod("RefreshAccessToken", inst, null, new[] { typeof(Action<bool>) }, null);
                s_ShowLogin          = s_Type.GetMethod("ShowLogin",          inst, null, Type.EmptyTypes, null);
            }
            catch
            {
                // Reflection bind failure: leave fields null; methods will return safe defaults.
            }
        }

        private static string InvokeString(MethodInfo mi)
        {
            if (mi == null || s_Instance == null) return string.Empty;
            try { return mi.Invoke(s_Instance, null) as string ?? string.Empty; }
            catch { return string.Empty; }
        }

        public static string GetAccessToken() => InvokeString(s_GetAccessToken);
        public static string GetUserId()      => InvokeString(s_GetUserId);
        public static string GetUserName()    => InvokeString(s_GetUserName);

        public static bool IsLoggedIn()
        {
            if (s_LoggedIn == null || s_Instance == null) return false;
            try
            {
                var v = s_LoggedIn.GetValue(s_Instance);
                return v is bool b && b;
            }
            catch { return false; }
        }

        public static void RefreshAccessToken(Action<bool> onRefreshed)
        {
            if (s_RefreshAccessToken == null || s_Instance == null)
            {
                onRefreshed?.Invoke(false);
                return;
            }
            try { s_RefreshAccessToken.Invoke(s_Instance, new object[] { onRefreshed }); }
            catch { onRefreshed?.Invoke(false); }
        }

        public static void ShowLogin()
        {
            if (s_ShowLogin == null || s_Instance == null) return;
            try { s_ShowLogin.Invoke(s_Instance, null); }
            catch { /* ignore */ }
        }
    }
}
#endif
