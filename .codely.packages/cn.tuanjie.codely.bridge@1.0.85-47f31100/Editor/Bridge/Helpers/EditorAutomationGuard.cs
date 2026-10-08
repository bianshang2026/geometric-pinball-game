using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Restricts bridge auto-start to the main Editor process and supported automation runs.
    /// Set <see cref="AllowBridgeInBatchEnvVar"/> to opt in when integration tests need the bridge.
    /// </summary>
    internal static class EditorAutomationGuard
    {
        internal const string AllowBridgeInBatchEnvVar = "UNITY_TCP_ALLOW_BATCH";

        internal static bool IsAutomatedEditorRun()
            => Application.isBatchMode
               || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        internal static bool ShouldSkipBridgeAutoStart()
            => EditorProcessGuard.ShouldSkipEditorAutoStart()
               || (IsAutomatedEditorRun()
                   && string.IsNullOrWhiteSpace(
                       Environment.GetEnvironmentVariable(AllowBridgeInBatchEnvVar)));

        internal static bool ShouldSkipBridgeAutoStart(
            bool isAssetImportWorker,
            bool isSecondaryEditor,
            bool isAutomatedEditorRun,
            bool allowBridgeInBatch)
            => isAssetImportWorker
               || isSecondaryEditor
               || (isAutomatedEditorRun && !allowBridgeInBatch);
    }
}
