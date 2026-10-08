#if UNITY_EDITOR
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;

namespace TJGenerators.Utils
{
    /// <summary>
    /// SessionState + JArray 的通用读写封装。解析失败或键不存在时返回空数组。
    /// 与具体业务无关，任何需要在 Editor 会话期间持久化 JSON 数组的场景均可复用。
    /// </summary>
    public static class SessionJsonStore
    {
        /// <summary>读取 SessionState 中指定键的 JArray；键不存在或解析失败返回空 JArray。</summary>
        public static JArray LoadJsonArray(string sessionKey)
        {
            string raw = SessionState.GetString(sessionKey, "");
            if (string.IsNullOrEmpty(raw)) return new JArray();
            try { return JArray.Parse(raw); }
            catch { return new JArray(); }
        }

        /// <summary>将 JArray 写入 SessionState 指定键；入参为 null 时写入 "[]"。</summary>
        public static void SaveJsonArray(string sessionKey, JArray arr)
        {
            SessionState.SetString(sessionKey, arr != null ? arr.ToString() : "[]");
        }
    }
}
#endif
