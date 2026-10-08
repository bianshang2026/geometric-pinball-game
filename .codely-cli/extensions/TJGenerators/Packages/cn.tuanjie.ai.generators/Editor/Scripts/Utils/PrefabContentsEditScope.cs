#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace TJGenerators.Utils
{
    /// <summary>
    /// Prefab 内容编辑作用域。
    /// Unity 2020.3+ 且 <paramref name="saveOnDispose"/> 为 true 时使用
    /// <see cref="PrefabUtility.EditPrefabContentsScope"/>；
    /// 其余情况使用 LoadPrefabContents，Dispose 时按 <paramref name="saveOnDispose"/> 决定是否写回。
    /// </summary>
    public sealed class PrefabContentsEditScope : IDisposable
    {
        public GameObject prefabContentsRoot { get; private set; }

        private readonly string _prefabAssetPath;
        private readonly bool _saveOnDispose;
        private bool _disposed;

#if UNITY_2020_3_OR_NEWER
        private PrefabUtility.EditPrefabContentsScope _editScope;
#endif

        public PrefabContentsEditScope(string prefabAssetPath, bool saveOnDispose = true)
        {
            _saveOnDispose = saveOnDispose;
            _prefabAssetPath = prefabAssetPath.Replace("\\", "/");

#if UNITY_2020_3_OR_NEWER
            if (saveOnDispose)
            {
                _editScope = new PrefabUtility.EditPrefabContentsScope(_prefabAssetPath);
                prefabContentsRoot = _editScope.prefabContentsRoot;
                return;
            }
#endif
            prefabContentsRoot = PrefabUtility.LoadPrefabContents(_prefabAssetPath);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

#if UNITY_2020_3_OR_NEWER
            if (_saveOnDispose)
            {
                _editScope.Dispose();
                prefabContentsRoot = null;
                _disposed = true;
                return;
            }
#endif

            if (prefabContentsRoot != null)
            {
                if (_saveOnDispose)
                    PrefabUtility.SaveAsPrefabAsset(prefabContentsRoot, _prefabAssetPath);
                PrefabUtility.UnloadPrefabContents(prefabContentsRoot);
                prefabContentsRoot = null;
            }

            _disposed = true;
        }
    }
}
#endif
