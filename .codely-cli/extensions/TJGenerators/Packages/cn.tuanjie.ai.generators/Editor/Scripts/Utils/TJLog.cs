using System.Text.RegularExpressions;
using UnityEngine;

namespace TJGenerators.Utils
{
    /// <summary>
    /// TJGenerators 日志封装类
    /// - Log/LogWarning: 仅在定义 TJGENERATORS_DEBUG 时输出
    /// - LogError: 始终输出
    /// </summary>
    public static class TJLog
    {
        private const string Tag = "[TJGenerators]";
        private const int MaxFieldValueLength = 100;

        /// <summary>
        /// 普通日志，仅开发模式输出
        /// </summary>
        [System.Diagnostics.Conditional("TJGENERATORS_DEBUG")]
        public static void Log(string message)
        {
            Debug.Log($"{Tag} {message}");
        }

        /// <summary>
        /// 警告日志，仅开发模式输出
        /// </summary>
        [System.Diagnostics.Conditional("TJGENERATORS_DEBUG")]
        public static void LogWarning(string message)
        {
            Debug.LogWarning($"{Tag} {message}");
        }

        /// <summary>
        /// 错误日志，始终输出
        /// </summary>
        public static void LogError(string message)
        {
            Debug.LogError($"{Tag} {message}");
        }

        /// <summary>
        /// 缩略 JSON 字符串中的长字段值（如 base64 图片数据）
        /// </summary>
        public static string TruncateJsonFields(string json, int maxLength = MaxFieldValueLength)
        {
            if (string.IsNullOrEmpty(json))
                return json;

            // 匹配 "key": "value" 格式，缩略过长的 value
            return Regex.Replace(json, @"""([^""]+)""\s*:\s*""([^""]{0,10})([^""]*)""", match =>
            {
                string key = match.Groups[1].Value;
                string valueStart = match.Groups[2].Value;
                string valueRest = match.Groups[3].Value;

                if (valueRest.Length > 0)
                {
                    // 值过长，缩略显示
                    return $"\"{key}\": \"{valueStart}...[truncated {valueRest.Length} chars]\"";
                }
                return match.Value;
            });
        }
    }
}
