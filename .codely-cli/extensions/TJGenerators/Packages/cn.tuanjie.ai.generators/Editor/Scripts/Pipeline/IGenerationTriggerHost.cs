#if UNITY_EDITOR
using TJGenerators.Generators;

namespace TJGenerators.Pipeline
{
    /// <summary>
    /// 由生成器 UI（如 <see cref="DynamicGenerator"/>）触发的窗口内生成入口。
    /// Headless CustomTool Host 无需实现。
    /// </summary>
    public interface IGenerationTriggerHost
    {
        void StartGeneration(ModelGeneratorBase generator);
    }
}
#endif
