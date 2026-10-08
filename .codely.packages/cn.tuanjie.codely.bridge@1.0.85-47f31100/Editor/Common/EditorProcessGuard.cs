using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>Restricts editor auto-start hooks to the main Editor process.</summary>
    public static class EditorProcessGuard
    {
        public static bool ShouldSkipEditorAutoStart()
            => ShouldSkipEditorAutoStart(IsAssetImportWorkerProcess(), IsSecondaryEditorProcess());

        public static bool ShouldSkipEditorUiAutoStart()
            => ShouldSkipEditorUiAutoStart(
                IsAssetImportWorkerProcess(),
                IsSecondaryEditorProcess(),
                Application.isBatchMode,
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null);

        internal static bool ShouldSkipEditorAutoStart(
            bool isAssetImportWorker,
            bool isSecondaryEditor)
            => isAssetImportWorker || isSecondaryEditor;

        internal static bool ShouldSkipEditorUiAutoStart(
            bool isAssetImportWorker,
            bool isSecondaryEditor,
            bool isBatchMode,
            bool isNullGraphics)
            => ShouldSkipEditorAutoStart(isAssetImportWorker, isSecondaryEditor)
               || isBatchMode
               || isNullGraphics;

        private static bool IsAssetImportWorkerProcess()
        {
#if UNITY_2020_2_OR_NEWER
            return AssetDatabase.IsAssetImportWorkerProcess();
#elif UNITY_2019_3_OR_NEWER
            return UnityEditor.Experimental.AssetDatabaseExperimental.IsAssetImportWorkerProcess();
#else
            return false;
#endif
        }

        private static bool IsSecondaryEditorProcess()
        {
#if UNITY_2021_1_OR_NEWER
            return UnityEditor.MPE.ProcessService.level == UnityEditor.MPE.ProcessLevel.Secondary;
#elif UNITY_2020_2_OR_NEWER
            return UnityEditor.MPE.ProcessService.level == UnityEditor.MPE.ProcessLevel.Slave;
#elif UNITY_2020_1_OR_NEWER
            return Unity.MPE.ProcessService.level == Unity.MPE.ProcessLevel.UMP_SLAVE;
#else
            return false;
#endif
        }
    }
}
