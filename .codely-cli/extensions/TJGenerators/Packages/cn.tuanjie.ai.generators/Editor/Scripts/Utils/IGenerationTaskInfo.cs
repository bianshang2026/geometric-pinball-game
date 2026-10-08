#if UNITY_EDITOR
using System;

namespace TJGenerators.Utils
{
    public interface IGenerationTaskInfo
    {
        string TaskId { get; set; }
        string Status { get; set; }
        int Progress { get; set; }
        string BackendTaskId { get; set; }
        DateTime StartTime { get; set; }
        DateTime? EndTime { get; set; }
        string ErrorMessage { get; set; }
        string PreviewUrl { get; set; }
    }
}
#endif
