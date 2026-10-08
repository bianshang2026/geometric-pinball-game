#if UNITY_EDITOR
using System;
using TJGenerators.Utils;

namespace TJGenerators.Pipeline
{
    /// <summary>
    /// Headless / CustomTool 用 Pipeline Host 基类。
    /// 提供 <see cref="RefreshHistory"/>、<see cref="RefreshUserInfo"/>、<see cref="ShowDialog"/> 等默认实现。
    /// </summary>
    public abstract class HeadlessPipelineHostBase : IGenerationPipelineHost
    {
        protected virtual string DialogLogTag => "TJGenerators";

        /// <summary>
        /// 非空时，<see cref="ShowDialog"/> 在错误对话框确认后回调（常用于 CustomTool 的 onFailed）。
        /// </summary>
        protected virtual Action<string> DialogFailedCallback => null;

        public abstract TJGeneratorsAssetReference GetTargetAsset();

        public virtual void RefreshHistory() { }

        public virtual void OnGenerationCompleted(string assetPath) { }

        public virtual void RefreshUserInfo() { }

        public virtual void Repaint() { }

        public virtual void ShowDialog(string title, string message)
        {
            var callback = DialogFailedCallback;
            if (callback != null)
                ErrorDialogUtils.ShowErrorDialog(title, message, callback, DialogLogTag);
            else
                ErrorDialogUtils.ShowErrorDialog(title, message, DialogLogTag);
        }
    }
}
#endif
