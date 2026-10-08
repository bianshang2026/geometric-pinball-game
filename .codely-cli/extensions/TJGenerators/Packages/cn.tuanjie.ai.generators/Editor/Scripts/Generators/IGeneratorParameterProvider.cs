#if UNITY_EDITOR
namespace TJGenerators.Generators
{
    /// <summary>
    /// 按参数 id 读写键值，供 UIComponents 绘制配置驱动的参数 UI，
    /// 与 DynamicGenerator 解耦，便于其他生成器复用 Drawer。
    /// </summary>
    public interface IGeneratorParameterProvider
    {
        object GetParameter(string parameterId);
        void SetParameter(string parameterId, object value);
    }
}
#endif
