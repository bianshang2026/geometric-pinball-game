#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace TJGenerators.Utils
{
    /// <summary>
    /// Object 查找 API 兼容层。
    /// Unity 6000.5+：FindObjectsByType 的 SortMode 重载已废弃，且单参数重载仍可能绑定到带默认 SortMode 的旧签名，
    /// 因此多对象查找改为 Resources.FindObjectsOfTypeAll + 场景过滤；单对象仍用 FindAnyObjectByType。
    /// Unity 2022.3–6000.4 使用 FindAnyObjectByType / FindObjectsOfType；
    /// Unity 2020.1–2022.1 使用 FindObjectsOfType / FindObjectOfType；
    /// Unity 2019 含 inactive 查找同样走 Resources 过滤。
    /// </summary>
    public static class UnityObjectCompat
    {
        public static T FindObjectOfType<T>() where T : Object
        {
#if UNITY_2022_3_OR_NEWER
            return Object.FindAnyObjectByType<T>(FindObjectsInactive.Exclude);
#elif UNITY_2022_2_OR_NEWER
            return Object.FindFirstObjectByType<T>(FindObjectsInactive.Exclude);
#else
            return Object.FindObjectOfType<T>();
#endif
        }

        public static T[] FindObjectsOfType<T>() where T : Object
        {
#if UNITY_6000_5_OR_NEWER
            return FindSceneObjectsOfType<T>(includeInactive: false);
#elif UNITY_2020_1_OR_NEWER
            return Object.FindObjectsOfType<T>(false);
#else
            return Object.FindObjectsOfType<T>();
#endif
        }

        public static T[] FindObjectsOfTypeIncludingInactive<T>() where T : Object
        {
#if UNITY_6000_5_OR_NEWER
            return FindSceneObjectsOfType<T>(includeInactive: true);
#elif UNITY_2020_1_OR_NEWER
            return Object.FindObjectsOfType<T>(true);
#else
            return FindSceneObjectsOfType<T>(includeInactive: true);
#endif
        }

#if UNITY_6000_5_OR_NEWER || !UNITY_2020_1_OR_NEWER
        private static T[] FindSceneObjectsOfType<T>(bool includeInactive) where T : Object
        {
            var results = new List<T>();
            foreach (var obj in Resources.FindObjectsOfTypeAll<T>())
            {
                if (obj == null)
                    continue;

                var component = obj as Component;
                if (component == null || !component.gameObject.scene.IsValid())
                    continue;

                if (!includeInactive && !component.gameObject.activeInHierarchy)
                    continue;

                results.Add(obj);
            }
            return results.ToArray();
        }
#endif
    }
}
#endif
