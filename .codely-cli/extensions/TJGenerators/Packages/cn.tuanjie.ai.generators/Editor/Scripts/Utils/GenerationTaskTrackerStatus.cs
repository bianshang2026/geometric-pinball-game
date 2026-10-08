#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace TJGenerators.Utils
{
    public static class GenerationTaskTrackerStatus
    {
        private static readonly HashSet<string> SessionRestoreReconcileStatuses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "initializing",
                "generating",
                "recovering",
                "running",
                "processing",
                "pending",
            };

        public static bool ReconcileAfterDomainReload(
            IGenerationTaskInfo info,
            Action save,
            Func<IGenerationTaskInfo, bool> hasActiveRecovery = null)
        {
            if (info == null) return false;
            if (string.IsNullOrEmpty(info.Status)
                || !SessionRestoreReconcileStatuses.Contains(info.Status))
                return false;

            bool canRecover = hasActiveRecovery != null
                ? hasActiveRecovery(info)
                : TJGeneratorsTaskRecovery.HasActiveRecovery(info.BackendTaskId);

            if (canRecover)
            {
                info.Status = "recovering";
            }
            else
            {
                info.Status = "interrupted";
                info.ErrorMessage = TJGeneratorsL10n.L("生成因域重载中断且后端任务记录已丢失，请重新生成。");
                info.EndTime = DateTime.Now;
            }

            save?.Invoke();
            return true;
        }
    }
}
#endif
