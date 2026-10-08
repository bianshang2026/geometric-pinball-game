#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using Codely.Newtonsoft.Json.Linq;
using TJGenerators.Config;

namespace TJGenerators.Generators
{
    /// <summary>
    /// 将 <see cref="ParameterConfig"/> 与用户取值写入请求 <see cref="JObject"/>。
    /// 支持 <c>apiFieldName</c> 中的点记法（如 <c>voiceSetting.voiceId</c>），
    /// 自动创建中间嵌套对象。
    /// </summary>
    internal static class ParameterJsonWriter
    {
        public static void Apply(
            JObject root,
            IReadOnlyList<ParameterConfig> parameters,
            IReadOnlyDictionary<string, object> values,
            string inputMode
        )
        {
            if (parameters == null || root == null || values == null)
                return;

            foreach (var param in parameters)
            {
                if (!values.TryGetValue(param.id, out object value))
                    continue;

                // 与 UI 一致：dependsOn 条件不满足时跳过该参数
                if (!string.IsNullOrEmpty(param.dependsOn))
                {
                    if (!values.TryGetValue(param.dependsOn, out object depVal)
                        || depVal?.ToString() != param.dependsValue)
                        continue;
                }

                string fieldName = param.GetApiFieldName(inputMode);

                switch (param.type)
                {
                    case "int":
                        // int 值为 0 时视为"未填写"，跳过写入（与 default 类型的空字符串一致），
                        // 避免向后端发送 seed=0 这类"固定种子 0"的非预期行为
                        if (Convert.ToInt32(value) == 0)
                            break;
                        SetNestedValue(root, fieldName, Convert.ToInt32(value));
                        break;
                    case "float":
                        SetNestedValue(root, fieldName, Convert.ToSingle(value));
                        break;
                    case "bool":
                        SetNestedValue(root, fieldName, Convert.ToBoolean(value));
                        break;
                    case "dropdown":
                        string strVal = value?.ToString() ?? "";
                        // 空字符串视为"未填写"，跳过写入，
                        // 避免向后端发送 textureAlignment="" 这类 omitempty 字段导致 400
                        if (string.IsNullOrEmpty(strVal))
                            break;
                        if (param.valueType == "string")
                            SetNestedValue(root, fieldName, strVal);
                        else if (int.TryParse(strVal, out int intVal))
                            SetNestedValue(root, fieldName, intVal);
                        else if (float.TryParse(strVal, out float floatVal))
                            SetNestedValue(root, fieldName, floatVal);
                        else
                            SetNestedValue(root, fieldName, strVal);
                        break;
                    case "json":
                        string jsonVal = value?.ToString()?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(jsonVal))
                            SetNestedValue(root, fieldName, JToken.Parse(jsonVal));
                        break;
                    default:
                        string defaultVal = value?.ToString() ?? "";
                        // 空字符串视为"未填写"，跳过写入，避免覆盖同 apiFieldName 的前序参数
                        if (!string.IsNullOrEmpty(defaultVal))
                            SetNestedValue(root, fieldName, defaultVal);
                        break;
                }
            }
        }

        public static void ApplyFixedFields(JObject root, IReadOnlyList<ImageGenFixedField> fixedFields)
        {
            if (root == null || fixedFields == null)
                return;

            foreach (ImageGenFixedField field in fixedFields)
            {
                if (string.IsNullOrEmpty(field?.key))
                    continue;

                switch (field.type)
                {
                    case "bool":
                        SetNestedValue(
                            root,
                            field.key,
                            string.Equals(field.value, "true", StringComparison.OrdinalIgnoreCase)
                                || field.value == "1"
                        );
                        break;
                    case "int":
                        if (int.TryParse(
                                field.value,
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out int iv))
                            SetNestedValue(root, field.key, iv);
                        break;
                    case "float":
                        if (double.TryParse(
                                field.value,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out double dv))
                            SetNestedValue(root, field.key, dv);
                        break;
                    default:
                        SetNestedValue(root, field.key, field.value ?? "");
                        break;
                }
            }
        }

        /// <summary>
        /// 设置可能包含点记法的嵌套值（如 "voiceSetting.voiceId"）。
        /// 若 fieldName 不含点，等效于 root[fieldName] = value。
        /// </summary>
        private static void SetNestedValue(JObject root, string fieldName, JToken value)
        {
            if (string.IsNullOrEmpty(fieldName))
                return;

            int dotIndex = fieldName.IndexOf('.');
            if (dotIndex < 0)
            {
                root[fieldName] = value;
                return;
            }

            // 逐级创建嵌套 JObject
            JObject current = root;
            string remaining = fieldName;
            while (remaining != null)
            {
                dotIndex = remaining.IndexOf('.');
                if (dotIndex < 0)
                {
                    current[remaining] = value;
                    break;
                }

                string segment = remaining.Substring(0, dotIndex);
                remaining = remaining.Substring(dotIndex + 1);

                if (current[segment] is JObject nested)
                {
                    current = nested;
                }
                else
                {
                    var newObj = new JObject();
                    current[segment] = newObj;
                    current = newObj;
                }
            }
        }
    }
}

#endif
