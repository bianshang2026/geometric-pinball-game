#if UNITY_EDITOR && TJGENERATORS_DEBUG
using UnityEditor;
using TJGenerators.Utils;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// 仅在启用 <c>TJGENERATORS_DEBUG</c> 时注册的诊断订阅者，帮助验证
    /// <see cref="AssetDownloadService.TaskUpdated"/> 事件是否被正确派发。
    /// 生产构建中不参与编译。
    /// </summary>
    internal static class AssetDownloadEventDiagnostics
    {
        [InitializeOnLoadMethod]
        private static void Register()
        {
            AssetDownloadService.TaskUpdated += OnTaskUpdated;
        }

        private static void OnTaskUpdated(DownloadTaskInfo info)
        {
            if (info == null) return;
            TJLog.Log($"[AssetDownloadEventDiagnostics] TaskUpdated: task_id={info.TaskId}, status={info.Status}");
        }
    }
}
#endif
