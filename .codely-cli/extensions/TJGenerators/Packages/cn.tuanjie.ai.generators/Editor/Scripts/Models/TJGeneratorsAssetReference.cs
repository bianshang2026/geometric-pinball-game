using System;
using UnityEditor;

namespace TJGenerators
{
    /// <summary>
    /// 简化的 GUID-based 资产引用，用于追踪生成目标资产
    /// </summary>
    [Serializable]
    public class TJGeneratorsAssetReference
    {
        public string guid = string.Empty;

        /// <summary>
        /// 获取资产路径
        /// </summary>
        public string GetPath()
        {
            if (string.IsNullOrEmpty(guid))
                return string.Empty;
            return AssetDatabase.GUIDToAssetPath(guid);
        }

        /// <summary>
        /// 检查引用是否有效
        /// </summary>
        public bool IsValid()
        {
            if (string.IsNullOrEmpty(guid))
                return false;
            var path = GetPath();
            return !string.IsNullOrEmpty(path);
        }

        /// <summary>
        /// 从资产路径创建引用
        /// </summary>
        public static TJGeneratorsAssetReference FromPath(string path)
        {
            return new TJGeneratorsAssetReference
            {
                guid = AssetDatabase.AssetPathToGUID(path)
            };
        }

        /// <summary>
        /// 从 GUID 创建引用
        /// </summary>
        public static TJGeneratorsAssetReference FromGuid(string guid)
        {
            return new TJGeneratorsAssetReference { guid = guid };
        }
    }
}
