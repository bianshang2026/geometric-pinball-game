#if UNITY_EDITOR
using System;
using System.Collections;
using UnityEngine;
using TJGenerators;
using TJGenerators.Pipeline;
using TJGenerators.UI;
using TJGenerators.Utils;

namespace TJGenerators.Generators
{
    /// <summary>
    /// 模型生成器基类 - 定义所有生成器必须实现的接口
    /// </summary>
    public abstract class ModelGeneratorBase
    {
        // ========== 显示信息 ==========
        
        /// <summary>
        /// 生成器显示名称（如 "Tripo 3D"、"Rodin"）
        /// </summary>
        public abstract string DisplayName { get; }
        
        /// <summary>
        /// 生成器唯一标识符（如 "tripo"、"rodin"）
        /// </summary>
        public abstract string GeneratorId { get; }
        
        /// <summary>
        /// API端点路径（如 "task/tripo-text-to-model"）
        /// </summary>
        public abstract string ApiEndpoint { get; }
        
        // ========== 状态管理 ==========
        
        /// <summary>
        /// 是否正在运行生成任务
        /// </summary>
        public bool IsRunning { get; set; }
        
        /// <summary>
        /// 按钮显示文字
        /// </summary>
        public string ButtonText { get; set; } = TJGeneratorsL10n.L("生成");
        
        /// <summary>
        /// 当前生成任务的本地ID（用于关联历史记录占位符）
        /// </summary>
        public string CurrentGeneratingTaskId { get; set; }
        
        /// <summary>
        /// 当前后端任务ID（用于任务恢复）
        /// </summary>
        public string CurrentBackendTaskId { get; set; }

        /// <summary>
        /// 当前任务进度（0-100），由 UpdateButtonStatus 更新，供恢复流程同步进度用
        /// </summary>
        public int CurrentProgress { get; protected set; }

        /// <summary>
        /// 当前任务的有效预览 URL（含 fallback 计算结果）。
        /// 由 GenerationPipeline 在调用 OnAssetSaved 之前写入，
        /// 供 host 回调传递给各 TaskTracker。任务完成后由 CompleteGeneration 清空。
        /// </summary>
        public string CurrentPreviewUrl { get; set; }

        // ========== UI绘制 ==========
        
        /// <summary>
        /// 绘制参数UI面板 - 每个子类独立实现自己的参数面板
        /// </summary>
        /// <param name="context">宿主窗口（Repaint、启动生成等），由各窗口实现</param>
        public abstract void DrawParametersUI(IGenerationPipelineHost context);

        /// <summary>绘制固定在左栏底部的操作按钮（生成/搜索等）。默认无操作。</summary>
        public virtual void DrawActionButton(IGenerationPipelineHost context, LeftPanelBottomDock.Layout layout) { }
        
        // ========== 参数验证 ==========
        
        /// <summary>
        /// 验证输入参数是否有效
        /// </summary>
        /// <param name="errorMessage">如果验证失败，返回错误信息</param>
        /// <returns>参数是否有效</returns>
        public abstract bool ValidateInputs(out string errorMessage);
        
        // ========== API请求构建 ==========
        
        /// <summary>
        /// 构建API请求数据
        /// </summary>
        /// <returns>请求数据对象（将被序列化为JSON）</returns>
        public abstract object BuildRequestData();
        
        // ========== 响应解析 ==========
        
        /// <summary>
        /// 从任务状态响应中提取下载URL
        /// </summary>
        /// <param name="response">任务状态响应</param>
        /// <returns>模型下载URL</returns>
        public abstract string GetDownloadUrl(TJTaskStatusResponse response);

        /// <summary>
        /// 从任务状态响应中提取多个下载 URL（如 image_urls 数组）。若不存在或仅单条则返回 null。
        /// </summary>
        public virtual string[] GetDownloadUrls(TJTaskStatusResponse response) => null;
        
        /// <summary>
        /// 从任务状态响应中提取预览图URL（用于历史记录显示）
        /// </summary>
        /// <param name="response">任务状态响应</param>
        /// <returns>预览图URL，如果没有则返回null</returns>
        public virtual string GetPreviewImageUrl(TJTaskStatusResponse response) => null;
        
        /// <summary>
        /// 从任务状态响应中提取渲染贴图URL（用于FBX主贴图）
        /// </summary>
        /// <param name="response">任务状态响应</param>
        /// <returns>渲染贴图URL，如果没有则返回null</returns>
        public virtual string GetRenderedImageUrl(TJTaskStatusResponse response) => null;
        
        /// <summary>
        /// 获取生成的模型文件名
        /// </summary>
        /// <returns>文件名（包含扩展名）</returns>
        public abstract string GetModelFileName();
        
        // ========== 输出类型 ==========
        
        /// <summary>
        /// 获取输出类型，见 <see cref="TJGenerators.Config.GenerationOutputTypes"/>。
        /// 该值同时用于 GenerationPipeline 内部分流和外部（如 TJGeneratorsGenerationService）确定 configType。
        /// </summary>
        public virtual string GetOutputType() => TJGenerators.Config.GenerationOutputTypes.Model;

        // ========== 后处理与流水线配置 ==========

        /// <summary>
        /// 获取与流水线和后处理相关的配置（缩放、转换等）。
        /// 由 <see cref="GenerationPipeline"/> 在任务执行期间使用，不应在生成器内部直接调用。
        /// </summary>
        public virtual PipelineSettings GetPipelineSettings()
            => PipelineSettings.Default;

        /// <summary>
        /// 是否在主模型生成完成后执行「绑骨 + 混元 Motion」后处理（由 DynamicGenerator UI 勾选驱动）。
        /// </summary>
        public virtual bool GetAddMotionEnabled() => false;

        /// <summary>
        /// 后处理动作描述，将提交至混元 Motion 的 inputText。
        /// </summary>
        public virtual string GetMotionDescription() => "";

        // ========== 任务恢复 ==========

        /// <summary>
        /// 创建中断任务数据（用于任务恢复）
        /// </summary>
        /// <param name="backendTaskId">后端任务ID</param>
        /// <param name="targetAssetGuid">目标资产GUID</param>
        /// <returns>中断任务数据</returns>
        public abstract InterruptedTaskData CreateInterruptedTaskData(string backendTaskId, string targetAssetGuid);
        
        /// <summary>
        /// 从中断任务数据恢复生成器状态
        /// </summary>
        /// <param name="taskData">中断任务数据</param>
        public virtual void RestoreFromInterruptedTask(InterruptedTaskData taskData)
        {
            CurrentGeneratingTaskId = taskData.localTaskId;
            CurrentBackendTaskId = taskData.backendTaskId;
            IsRunning = true;
            ButtonText = TJGeneratorsL10n.L("恢复中...");
        }
        
        // ========== 历史记录 ==========
        
        /// <summary>
        /// 获取当前提示词（发送 API / 写入任务）
        /// </summary>
        public abstract string GetPrompt();

        /// <summary>
        /// 历史缩略图标题用：仅玩家输入，不含 instructions 等系统拼接内容。
        /// </summary>
        public virtual string GetHistoryDisplayPrompt() => GetPrompt();
        
        /// <summary>
        /// 获取当前图片路径（用于历史记录）
        /// </summary>
        /// <returns>图片路径</returns>
        public abstract string GetImagePath();
        
        /// <summary>
        /// 获取模型版本标识（用于历史记录）
        /// </summary>
        /// <returns>模型版本字符串</returns>
        public abstract string GetModelVersion();
        
        /// <summary>
        /// 是否是文本生成模式（用于历史记录）
        /// </summary>
        /// <returns>true为文本生成，false为图片生成</returns>
        public abstract bool IsTextToModel();
        
        // ========== 状态重置 ==========
        
        /// <summary>
        /// 重置生成器状态（任务完成或失败后调用）
        /// </summary>
        public virtual void ResetState()
        {
            IsRunning = false;
            ButtonText = TJGeneratorsL10n.L("生成");
            CurrentGeneratingTaskId = null;
            CurrentBackendTaskId = null;
            CurrentProgress = 0;
        }
        
        /// <summary>
        /// 更新按钮状态文字
        /// </summary>
        /// <param name="status">任务状态</param>
        /// <param name="progress">进度百分比（0-100）</param>
        public virtual void UpdateButtonStatus(string status, int progress = 0)
        {
            CurrentProgress = progress;
            if (progress > 0 && (status == "running" || status == "processing"))
            {
                ButtonText = progress >= 100 ? TJGeneratorsL10n.L("转换中...") : string.Format(TJGeneratorsL10n.L("生成中 {0}%"), progress);
            }
            else
            {
                switch (status)
                {
                    case "pending":    ButtonText = TJGeneratorsL10n.L("等待中..."); break;
                    case "processing": ButtonText = TJGeneratorsL10n.L("处理中..."); break;
                    case "running":    ButtonText = TJGeneratorsL10n.L("生成中..."); break;
                    case "completed":  ButtonText = TJGeneratorsL10n.L("已完成"); break;
                    case "failed":     ButtonText = TJGeneratorsL10n.L("失败"); break;
                    case "error":      ButtonText = TJGeneratorsL10n.L("错误"); break;
                    default:           ButtonText = TJGeneratorsL10n.L("处理中..."); break;
                }
            }
        }
    }
}
#endif
