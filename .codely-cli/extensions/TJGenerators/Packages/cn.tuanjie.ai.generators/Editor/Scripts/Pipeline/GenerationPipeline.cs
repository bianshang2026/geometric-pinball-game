#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using TJGenerators;
using TJGenerators.Generators;
using TJGenerators.Config;
using TJGenerators.Utils;
using TJGenerators.PostProcessing;
using Unity.EditorCoroutines.Editor;

namespace TJGenerators.Pipeline
{
    /// <summary>
    /// 统一的生成流程管理 - 处理轮询、下载、Prefab绑定等通用逻辑。
    /// HTTP 传输由 <c>GenerationBackendTransportFactory</c> 与 <c>IGenerationBackendTransport</c> 实现。
    /// </summary>
    public class GenerationPipeline
    {
        private string API_BASE_URL => ConfigManager.GetApiBaseUrl();
        private int MAX_POLL_RETRIES => ConfigManager.GetPollMaxRetries();
        private float POLL_INTERVAL => ConfigManager.GetPollInterval();

        private const string SAVE_DIRECTORY = "Assets/TJGenerators/";
        private const string HISTORY_DIRECTORY = "Assets/TJGenerators/History/";

        /// <summary>
        /// 模型绑定到 Prefab 时的目标包裹尺寸（单位：米）。
        /// 以模型包围盒最长边为准做归一化，使不同源头生成的 3D 模型在场景中大小一致、不用手动改 scale。
        /// </summary>
        private const float DefaultModelTargetSize = 1f;

        private IGenerationPipelineHost _host;
        private TJGeneratorsTaskHandle _activeTaskHandle;
        private IGenerationBackendTransport _transport;

        /// <summary>
        /// 最近一次完成的任务响应，供 Host 在 OnAssetSaved 等回调中访问（如世界生成需要下载 spz/mesh）。
        /// </summary>
        public TJTaskStatusResponse LastCompletedResponse { get; private set; }

        /// <summary>
        /// 本次生成的来源标识（"ui" / "agent"），通过 fromMethod 头与 source 并列上报。
        /// 默认 agent；UI 窗口创建 Pipeline 时显式传入 ui。
        /// </summary>
        private readonly string _fromMethod;

        /// <summary>
        /// 当前 Agent 会话 ID，通过 X-Session-Id 头上报（可为空）。
        /// </summary>
        private readonly string _sessionId;

        /// <summary>
        /// CustomTool name that owns this pipeline (e.g. generate_sprite). Stamped onto InterruptedTaskData for domain-reload recovery.
        /// </summary>
        private readonly string _toolName;

        /// <summary>
        /// 当前占用本 Pipeline 的生成任务所属生成器（与 UI 中选中的模型实例可能不一致）。
        /// </summary>
        private ModelGeneratorBase _pipelineBusyGenerator;

        /// <summary>
        /// 当前任务的流水线/后处理配置，由任务入口点（StartGeneration 等）从 generator 取得后存储，
        /// 供后续私有方法直接访问，无需再通过 ModelGeneratorBase 虚方法绕一圈。
        /// </summary>
        private PipelineSettings _pipelineSettings = PipelineSettings.Default;

        private string _currentPreviewUrl;

        private readonly GenerationMediaAssetHandlers _mediaHandlers;

        /// <summary>「添加动作」后处理成功时，绑骨后的主模型 Unity 路径（供绑定 Prefab）。</summary>
        private string _postMotionRiggedPath;

        private static class TaskStatus
        {
            public const string Completed = "completed";
            public const string Failed = "failed";
            public const string Error = "error";
            public const string Cancelled = "cancelled";
        }

        private sealed class MotionSubTaskPollOutcome
        {
            public TJTaskStatusResponse Completed;
            public string Error;
        }

        public GenerationPipeline(
            IGenerationPipelineHost host,
            ConfigType configType,
            string fromMethod = GenerationRequestOrigin.Agent,
            string sessionId = "",
            string toolName = ""
        )
        {
            _host = host;
            _fromMethod = string.IsNullOrEmpty(fromMethod)
                ? GenerationRequestOrigin.Agent
                : fromMethod;
            _sessionId = sessionId ?? "";
            _toolName = toolName ?? "";
            _mediaHandlers = new GenerationMediaAssetHandlers(
                host,
                new GenerationMediaAssetHandlers.Dependencies
                {
                    OnError = (g, msg) => HandleError(g, msg),
                    OnComplete = (g, path, urls, paths) => CompleteGeneration(g, path, urls, paths),
                },
                HISTORY_DIRECTORY
            );
        }

        /// <summary>
        /// 是否有尚未结束的生成流程（轮询/下载等）。用于 UI 在切换模型下拉后仍保持「生成中」占用状态。
        /// </summary>
        public bool IsPipelineBusy => _pipelineBusyGenerator != null;

        /// <summary>
        /// 将当前进行中的任务绑定到指定生成器（正常启动与任务恢复时调用）。
        /// </summary>
        public void RegisterActiveGenerator(ModelGeneratorBase generator)
        {
            _pipelineBusyGenerator = generator;
        }

        private void EndGenerationState(ModelGeneratorBase generator)
        {
            generator.ResetState();
            if (_pipelineBusyGenerator == generator)
                _pipelineBusyGenerator = null;
        }

        private void EnsureTransport(ModelGeneratorBase generator)
        {
            if (_transport != null)
                return;
            _transport = GenerationBackendTransportFactory.Create(_fromMethod, _sessionId);
        }

        public IEnumerator StartGeneration(
            ModelGeneratorBase generator,
            string assetGuid,
            TJGeneratorsTaskHandle taskHandle = null
        )
        {
            _pipelineSettings = generator.GetPipelineSettings();
            _activeTaskHandle = taskHandle;

            if (TJGeneratorsPlayModeGuard.TryBlock(_host))
            {
                if (_activeTaskHandle != null)
                {
                    _activeTaskHandle.MarkFailed("PLAY_MODE", TJGeneratorsPlayModeGuard.Message);
                    _activeTaskHandle = null;
                }
                yield break;
            }

            if (!generator.ValidateInputs(out string errorMessage))
            {
                _host.ShowDialog(TJGeneratorsL10n.L("输入错误"), errorMessage);
                if (_activeTaskHandle != null)
                {
                    _activeTaskHandle.MarkFailed("invalid_input", errorMessage);
                    _activeTaskHandle = null;
                }
                yield break;
            }

            generator.CurrentGeneratingTaskId = TJGeneratorsHistoryManager.AddGeneratingPlaceholder(
                generator.GetPrompt(),
                generator.GetImagePath(),
                generator.GetModelVersion(),
                generator.IsTextToModel(),
                assetGuid,
                generator.GetHistoryDisplayPrompt(),
                _sessionId
            );

            if (_activeTaskHandle != null)
            {
                _activeTaskHandle.SetLocalTaskId(generator.CurrentGeneratingTaskId);
            }

            generator.IsRunning = true;
            RegisterActiveGenerator(generator);
            generator.ButtonText = TJGeneratorsL10n.L("上传中...");
            _host.RefreshHistory();
            _host.Repaint();

            _mediaHandlers.TryInitializeMediaSavePaths(generator);

            yield return SendGenerationRequest(generator, assetGuid);
        }

        /// <summary>
        /// 从已成功提交的后端任务ID开始轮询和下载，跳过 HTTP 提交阶段。
        /// 由 CustomTool 两阶段模式使用：外部同步提交后，由此方法接管剩余流程。
        /// </summary>
        public IEnumerator StartFromSubmittedTask(
            ModelGeneratorBase generator,
            string assetGuid,
            string backendTaskId,
            TJGeneratorsTaskHandle taskHandle = null
        )
        {
            _pipelineSettings = generator.GetPipelineSettings();
            _activeTaskHandle = taskHandle;

            if (TJGeneratorsPlayModeGuard.TryBlock(_host))
            {
                if (_activeTaskHandle != null)
                {
                    _activeTaskHandle.MarkFailed("PLAY_MODE", TJGeneratorsPlayModeGuard.Message);
                    _activeTaskHandle = null;
                }
                yield break;
            }

            generator.CurrentGeneratingTaskId = TJGeneratorsHistoryManager.AddGeneratingPlaceholder(
                generator.GetPrompt(),
                generator.GetImagePath(),
                generator.GetModelVersion(),
                generator.IsTextToModel(),
                assetGuid,
                generator.GetHistoryDisplayPrompt(),
                _sessionId
            );

            if (_activeTaskHandle != null)
                _activeTaskHandle.SetLocalTaskId(generator.CurrentGeneratingTaskId);

            generator.IsRunning = true;
            RegisterActiveGenerator(generator);
            generator.ButtonText = TJGeneratorsL10n.L("生成中...");
            _host.RefreshHistory();
            _host.Repaint();

            generator.CurrentBackendTaskId = backendTaskId;
            var taskData = generator.CreateInterruptedTaskData(backendTaskId, assetGuid);
            if (!string.IsNullOrEmpty(_toolName))
                taskData.toolName = _toolName;
            TJGeneratorsTaskRecovery.AddInterruptedTask(taskData);
            TJGeneratorsTaskRecovery.MarkAsRecovering(backendTaskId);

            if (_activeTaskHandle != null)
            {
                _activeTaskHandle.SetBackendTaskId(backendTaskId);
                _activeTaskHandle.SetStatus("pending");
                _activeTaskHandle.NotifyCreated();
            }

            TJLog.Log(
                $"[GenerationPipeline] StartFromSubmittedTask: 跳过提交，直接轮询 backendTaskId={backendTaskId}"
            );

            _mediaHandlers.TryInitializeMediaSavePaths(generator);

            EnsureTransport(generator);

            yield return PollTaskStatus(generator, backendTaskId);
        }

        private IEnumerator SendGenerationRequest(ModelGeneratorBase generator, string assetGuid)
        {
            string endpoint = generator.ApiEndpoint;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                HandleError(
                    generator,
                    $"No API endpoint configured for generator '{generator.GeneratorId}'."
                );
                yield break;
            }

            string url = API_BASE_URL + endpoint;
            TJLog.Log($"[GenerationPipeline] Building request payload...");
            var requestData = generator.BuildRequestData();
            TJLog.Log(
                $"[GenerationPipeline] 请求数据类型: {requestData?.GetType().Name ?? "null"}"
            );

            // 在发送HTTP请求之前保存占位任务记录（使用localTaskId作为backendTaskId占位符）
            // 防止domain reload发生在HTTP请求等待期间导致任务记录丢失
            var submittingTaskData = generator.CreateInterruptedTaskData(
                generator.CurrentGeneratingTaskId,
                assetGuid
            );
            submittingTaskData.status = "submitting";
            if (!string.IsNullOrEmpty(_toolName))
                submittingTaskData.toolName = _toolName;
            TJGeneratorsTaskRecovery.AddInterruptedTask(submittingTaskData);

            EnsureTransport(generator);
            TJTaskResponse response = null;
            string transportError = null;
#if TJGENERATORS_DEBUG
            int abortEpoch = TJGeneratorsTaskRecovery.GetLocalPollAbortEpoch();
#endif

            if (requestData is MultipartRequestData multipartData)
            {
                TJLog.Log($"[GenerationPipeline] 发送Multipart请求到: {url}");
                yield return _transport.CreateTaskMultipart(
                    url,
                    multipartData,
                    r => response = r,
                    e => transportError = e
                );
            }
            else
            {
                string jsonData;
                if (requestData is DynamicRequestData dynamicData)
                {
                    jsonData = dynamicData.JsonContent;
                }
                else
                {
                    jsonData = JsonUtility.ToJson(requestData);
                }

                TJLog.Log($"[GenerationPipeline] 发送请求到: {url}");
                TJLog.Log($"[GenerationPipeline] 请求体: {jsonData}");

                byte[] postData = System.Text.Encoding.UTF8.GetBytes(jsonData);
                yield return _transport.CreateTask(
                    url,
                    postData,
                    r => response = r,
                    e => transportError = e
                );
            }

#if TJGENERATORS_DEBUG
            if (TJGeneratorsTaskRecovery.WasLocalPollAborted(abortEpoch))
            {
                TJGeneratorsTaskRecovery.RemoveInterruptedTask(generator.CurrentGeneratingTaskId);
                string abortTaskId = !string.IsNullOrEmpty(response?.taskId)
                    ? response.taskId
                    : generator.CurrentGeneratingTaskId;
                HandleLocalPollAbort(generator, abortTaskId);
                yield break;
            }
#endif

            if (!string.IsNullOrEmpty(transportError))
            {
                TJGeneratorsTaskRecovery.RemoveInterruptedTask(generator.CurrentGeneratingTaskId);
                HandleError(generator, transportError);
                yield break;
            }

            if (response != null && !string.IsNullOrEmpty(response.taskId))
            {
                TJLog.Log($"[GenerationPipeline] 任务ID: {response.taskId}");

                TJGeneratorsTaskRecovery.RemoveInterruptedTask(generator.CurrentGeneratingTaskId);
                var taskData = generator.CreateInterruptedTaskData(response.taskId, assetGuid);
                if (!string.IsNullOrEmpty(_toolName))
                    taskData.toolName = _toolName;
                TJGeneratorsTaskRecovery.AddInterruptedTask(taskData);
                generator.CurrentBackendTaskId = response.taskId;

                // Mark as actively recovering so TaskRecoveryHelper won't start a duplicate pipeline
                // if the 3D model window opens (OnEnable) while this task is still being polled.
                TJGeneratorsTaskRecovery.MarkAsRecovering(response.taskId);

                if (_activeTaskHandle != null)
                {
                    _activeTaskHandle.SetBackendTaskId(response.taskId);
                    _activeTaskHandle.SetStatus(
                        string.IsNullOrEmpty(response.status) ? "pending" : response.status
                    );
                    _activeTaskHandle.NotifyCreated();
                }

                generator.ButtonText = TJGeneratorsL10n.L("生成中...");
                _host.Repaint();
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    PollTaskStatus(generator, response.taskId)
                );
            }
            else
            {
                TJGeneratorsTaskRecovery.RemoveInterruptedTask(generator.CurrentGeneratingTaskId);
                HandleError(generator, TJGeneratorsL10n.L("响应数据无效"));
            }
        }

        public IEnumerator PollTaskStatus(ModelGeneratorBase generator, string taskId)
        {
            _pipelineSettings = generator.GetPipelineSettings();
            EnsureTransport(generator);
            _mediaHandlers.TryInitializeMediaSavePaths(generator);
            string url = ConfigManager.GetPollStatusUrl(taskId);
#if TJGENERATORS_DEBUG
            int abortEpoch = TJGeneratorsTaskRecovery.GetLocalPollAbortEpoch();
#endif
            bool taskCompleted = false;
            int retryCount = 0;

            while (!taskCompleted && retryCount < MAX_POLL_RETRIES)
            {
#if TJGENERATORS_DEBUG
                if (TJGeneratorsTaskRecovery.WasLocalPollAborted(abortEpoch))
                {
                    HandleLocalPollAbort(generator, taskId);
                    yield break;
                }
#endif

                retryCount++;
                TJLog.Log($"[GenerationPipeline] 轮询 {retryCount}/{MAX_POLL_RETRIES}");

                TJTaskStatusResponse response = null;
                string transportError = null;
                yield return _transport.PollStatus(
                    taskId,
                    url,
                    r => response = r,
                    e => transportError = e
                );

#if TJGENERATORS_DEBUG
                if (TJGeneratorsTaskRecovery.WasLocalPollAborted(abortEpoch))
                {
                    HandleLocalPollAbort(generator, taskId);
                    yield break;
                }
#endif

                if (!string.IsNullOrEmpty(transportError))
                {
                    if (retryCount >= MAX_POLL_RETRIES)
                    {
                        HandleError(generator, transportError);
                        yield break;
                    }
#if TJGENERATORS_DEBUG
                    yield return WaitSeconds(POLL_INTERVAL, abortEpoch);
#else
                    yield return WaitSeconds(POLL_INTERVAL);
#endif
                    continue;
                }

                if (response != null)
                {
                    TJLog.Log(
                        $"[GenerationPipeline] 任务状态: {response.status}, 进度: {response.progress}"
                    );

                    generator.UpdateButtonStatus(response.status, response.progress);
                    UpdateHistoryProgress(generator, response.progress);
                    if (_activeTaskHandle != null)
                    {
                        // 轮询时提前提取预览图URL（后端可能在generating阶段就返回）
                        if (string.IsNullOrEmpty(_activeTaskHandle.PreviewUrl))
                        {
                            string previewUrl = generator.GetPreviewImageUrl(response);
                            if (!string.IsNullOrEmpty(previewUrl))
                                _activeTaskHandle.SetPreviewUrl(previewUrl);
                        }
                        _activeTaskHandle.UpdateProgress(response.status, response.progress);
                    }
                    _host.Repaint();

                    if (response.status == TaskStatus.Completed)
                    {
                        // 原子移除：只有成功移除任务记录的协程才执行下载，防止多个 PollTaskStatus
                        // 协程（因 domain reload 重复恢复导致）同时触发重复下载。
                        // clearRecovering: false — 下载完成前保留 in-memory recovering，避免
                        // LoadFromSession 在 RemoveInterruptedTask 与 MarkCompleted 之间误标 interrupted。
                        bool shouldDownload = true;
                        string completedBackendTaskId = generator.CurrentBackendTaskId;
                        if (!string.IsNullOrEmpty(completedBackendTaskId))
                        {
                            shouldDownload = TJGeneratorsTaskRecovery.RemoveInterruptedTask(
                                completedBackendTaskId,
                                clearRecovering: false
                            );
                            if (!shouldDownload)
                                TJLog.Log(
                                    $"[GenerationPipeline] 任务 {completedBackendTaskId} 已被其他协程处理，跳过重复下载。"
                                );
                        }
                        if (shouldDownload)
                        {
                            TJLog.Log("[GenerationPipeline] 任务完成，开始下载...");
                            yield return CompleteTask(generator, response);
                            if (!string.IsNullOrEmpty(completedBackendTaskId))
                                TJGeneratorsTaskRecovery.ClearRecovering(completedBackendTaskId);
                        }
                        taskCompleted = true;
                    }
                    else if (
                        response.status == TaskStatus.Failed
                        || response.status == TaskStatus.Error
                        || response.status == TaskStatus.Cancelled
                    )
                    {
                        string detail = !string.IsNullOrEmpty(response.error)
                            ? response.error
                            : (!string.IsNullOrEmpty(response.message) ? response.message : null);

                        if (response.errorCode == "content_moderation")
                        {
                            string baseMsg = TJGeneratorsL10n.L(
                                "生成内容可能涉及敏感信息，请修改后重试"
                            );
                            string fullMsg = string.IsNullOrEmpty(detail)
                                ? baseMsg
                                : $"{baseMsg}\n{TJGeneratorsL10n.L("拦截原因")}: {detail}";
                            HandleError(
                                generator,
                                fullMsg,
                                response.status == TaskStatus.Cancelled
                                    ? TaskStatus.Cancelled
                                    : TaskStatus.Error
                            );
                            taskCompleted = true;
                        }
                        else
                        {
                            string enhancedError = EnhanceErrorMessage(detail, generator);
                            string msg;
                            if (response.status == TaskStatus.Cancelled)
                                msg = !string.IsNullOrEmpty(detail)
                                    ? string.Format(TJGeneratorsL10n.L("任务已取消: {0}"), detail)
                                    : TJGeneratorsL10n.L("任务已取消");
                            else
                                msg = !string.IsNullOrEmpty(enhancedError)
                                    ? enhancedError
                                    : (
                                        !string.IsNullOrEmpty(detail)
                                            ? string.Format(
                                                TJGeneratorsL10n.L("任务失败: {0}"),
                                                detail
                                            )
                                            : string.Format(
                                                TJGeneratorsL10n.L("任务失败: {0}"),
                                                response.status
                                            )
                                    );

                            HandleError(
                                generator,
                                msg,
                                response.status == TaskStatus.Cancelled
                                    ? TaskStatus.Cancelled
                                    : TaskStatus.Error
                            );
                            taskCompleted = true;
                        }
                    }
                }
                else
                {
                    HandleError(generator, TJGeneratorsL10n.L("响应数据无效"));
                    taskCompleted = true;
                }

                if (!taskCompleted && retryCount < MAX_POLL_RETRIES)
                {
#if TJGENERATORS_DEBUG
                    yield return WaitSeconds(POLL_INTERVAL, abortEpoch);
#else
                    yield return WaitSeconds(POLL_INTERVAL);
#endif
                }
            }

            if (!taskCompleted && retryCount >= MAX_POLL_RETRIES)
            {
                HandlePollingTimeout(
                    generator,
                    TJGeneratorsL10n.L("轮询超时，任务可能仍在后端运行。重新打开窗口可继续等待。")
                );
            }
        }

        private IEnumerator CompleteTask(
            ModelGeneratorBase generator,
            TJTaskStatusResponse response
        )
        {
            string previewImageUrl = generator.GetPreviewImageUrl(response);
            if (!string.IsNullOrEmpty(previewImageUrl))
            {
                TJLog.Log($"[GenerationPipeline] 获取到预览图URL: {previewImageUrl}");
            }

            _currentPreviewUrl = previewImageUrl;

            if (_activeTaskHandle != null)
            {
                _activeTaskHandle.SetPreviewUrl(previewImageUrl);
            }

            LastCompletedResponse = response;
            string outputType = generator.GetOutputType();
            if (
                string.Equals(
                    outputType,
                    GenerationOutputTypes.Audio,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                yield return _mediaHandlers.HandleAudioAsset(generator, response);
                yield break;
            }
            if (
                string.Equals(
                    outputType,
                    GenerationOutputTypes.Video,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                yield return _mediaHandlers.HandleVideoAsset(
                    generator,
                    response,
                    _currentPreviewUrl
                );
                yield break;
            }
            if (
                string.Equals(
                    outputType,
                    GenerationOutputTypes.SpriteSequence,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                EnsureTransport(generator);
                yield return _mediaHandlers.HandleSpriteSequenceAsset(
                    generator,
                    response,
                    _transport,
                    _currentPreviewUrl
                );
                yield break;
            }
            if (
                outputType != GenerationOutputTypes.Model
                && !string.Equals(
                    outputType,
                    GenerationOutputTypes.RiggedModel,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                EnsureTransport(generator);
                yield return _mediaHandlers.HandleTextureAsset(generator, response, _transport);
                yield break;
            }

            generator.ButtonText = TJGeneratorsL10n.L("下载中...");
            _host.Repaint();

            string modelUrl = generator.GetDownloadUrl(response);

            string renderedImageUrl = generator.GetRenderedImageUrl(response);
            bool isFBX =
                !string.IsNullOrEmpty(modelUrl)
                && modelUrl.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(modelUrl))
            {
                string fileName = generator.GetModelFileName();
                string actualExtension = GenerationAssetFormatUtils.GetExtensionFromUrl(modelUrl);
                if (!string.IsNullOrEmpty(actualExtension))
                {
                    string baseName = Path.GetFileNameWithoutExtension(fileName);
                    fileName = baseName + actualExtension;
                }
                string savePath = GetModelSavePath(fileName);

                TJLog.Log($"[GenerationPipeline] 开始下载: {modelUrl}");

                yield return DownloadModel(
                    generator,
                    modelUrl,
                    savePath,
                    isFBX,
                    renderedImageUrl
                );
            }
            else
            {
                // 始终走 HandleError：在 _activeTaskHandle 为 null 时仍须 RemovePlaceholder/RefreshHistory，否则会留下一直转圈的历史项
                HandleError(generator, TJGeneratorsL10n.L("未找到模型下载URL"));
            }
        }

        /// <summary>
        /// 下载模型文件。当 isFBX 且 renderedImageUrl 非空时，会下载 webp 贴图并应用到 FBX 材质。
        /// </summary>
        private IEnumerator DownloadModel(
            ModelGeneratorBase generator,
            string modelUrl,
            string savePath,
            bool isFBX = false,
            string renderedImageUrl = null
        )
        {
            string uniquePath = ResolveModelDownloadPath(savePath, modelUrl);
            bool isZipFile =
                savePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    GenerationAssetFormatUtils.GetExtensionFromUrl(modelUrl),
                    ".zip",
                    StringComparison.OrdinalIgnoreCase
                );

            byte[] modelData = null;
            string downloadError = null;
            yield return PipelineDownloadHelper.DownloadUrlToFile(
                modelUrl,
                uniquePath,
                ConfigManager.GetDownloadTimeout(),
                onSuccess: data => modelData = data,
                onError: err => downloadError = err
            );

            if (!string.IsNullOrEmpty(downloadError) || modelData == null || modelData.Length == 0)
            {
                TJLog.LogError($"[GenerationPipeline] 下载失败: {downloadError}");
                HandleError(
                    generator,
                    !string.IsNullOrEmpty(downloadError)
                        ? downloadError
                        : TJGeneratorsL10n.L("下载模型失败")
                );
                yield break;
            }

            string finalModelPath = uniquePath;

            if (isZipFile)
            {
                finalModelPath = ZipExtractor.ExtractZipAndGetModelPath(
                    modelData,
                    uniquePath
                );
                if (string.IsNullOrEmpty(finalModelPath))
                {
                    HandleError(
                        generator,
                        TJGeneratorsL10n.L("解压ZIP文件失败或未找到模型文件")
                    );
                    yield break;
                }
            }
            else
            {
                PathUtils.ImportAssetAfterDiskWrite(finalModelPath);
            }

            string renderedTexturePath = null;
            if (!string.IsNullOrEmpty(renderedImageUrl))
            {
                string modelDir = Path.GetDirectoryName(finalModelPath);
                string renderedBase = Path.GetFileNameWithoutExtension(finalModelPath);
                string renderedFileName = $"{renderedBase}_render.webp";
                renderedTexturePath = Path.Combine(modelDir, renderedFileName)
                    .Replace("\\", "/");
                yield return DownloadRenderedImage(renderedImageUrl, renderedTexturePath);
            }

            if (isFBX)
            {
                ModelPostProcessing(finalModelPath, renderedTexturePath);
                PathUtils.ImportAssetAfterDiskWrite(finalModelPath);

                if (!ValidateImportedFbxMesh(finalModelPath, out string meshReason))
                {
                    TJLog.LogError(
                        $"[GenerationPipeline] FBX 网格校验失败（{meshReason}）: {finalModelPath}"
                    );
                    HandleError(
                        generator,
                        TJGeneratorsL10n.L("模型文件损坏（顶点数为0），请重试生成")
                    );
                    yield break;
                }
            }

            if (finalModelPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            {
                ObjModelPostProcessing(finalModelPath);
                PathUtils.ImportAssetAfterDiskWrite(finalModelPath);
            }

            if (
                string.Equals(
                    generator.GetOutputType(),
                    GenerationOutputTypes.RiggedModel,
                    StringComparison.OrdinalIgnoreCase
                ) && finalModelPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
            )
            {
                string sourceModelPath = generator is DynamicGenerator dg
                    ? dg.GetUploadedModelAssetPath()
                    : null;
                RiggedModelPostProcess.FinalizeRiggedImport(
                    finalModelPath,
                    sourceModelPath,
                    renderedTexturePath
                );
            }

            // 混元 Motion 等：动画面片在单一主 FBX 内时，从主 FBX 建单状态自循环控制器
            if (
                _pipelineSettings.GetPostProcessingSingleClipLoopAnimatorController()
                && finalModelPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
                && !generator.GetAddMotionEnabled()
            )
            {
                string modelDir =
                    Path.GetDirectoryName(finalModelPath)?.Replace("\\", "/") ?? "";
                string baseName = Path.GetFileNameWithoutExtension(finalModelPath);
                RiggedModelPostProcess.CreateSingleClipLoopAnimatorControllerFromMotionClip(
                    modelDir,
                    baseName,
                    finalModelPath
                );
            }

            string modelPathForBind = finalModelPath;
            if (generator.GetAddMotionEnabled())
            {
                _postMotionRiggedPath = null;
                yield return RunMotionPostProcessing(
                    generator,
                    finalModelPath,
                    generator.GetMotionDescription(),
                    renderedTexturePath
                );
                if (!string.IsNullOrEmpty(_postMotionRiggedPath))
                    modelPathForBind = _postMotionRiggedPath;
            }

            // 绑定到Prefab：UniRig + 混元 Motion 后的 FBX 姿态/尺度已由管线决定，勿再套后处理里的 modelScale/rotation。
            // 静态模型按包围盒自适应归一化到目标尺寸，避免模型过小（一个点）需要手动改 scale。
            bool addMotion = generator.GetAddMotionEnabled();
            float bindScale = addMotion ? 1f : _pipelineSettings.GetModelScale();
            Vector3 bindRotation = addMotion
                ? Vector3.zero
                : _pipelineSettings.GetModelRotation();
            BindModelToPrefab(modelPathForBind, bindScale, bindRotation, autoFitToTargetScale: !addMotion);

            CompleteGeneration(generator, modelPathForBind);
        }

        /// <summary>
        /// 轮询子任务（UniRig / 混元 Motion）直到完成或失败，不触发主任务的 HandleError。
        /// </summary>
        private IEnumerator PollSimpleTaskUntilComplete(
            ModelGeneratorBase generator,
            string taskId,
            string phaseLabel,
            MotionSubTaskPollOutcome outcome
        )
        {
            outcome.Completed = null;
            outcome.Error = null;
            EnsureTransport(generator);
            string pollUrl = ConfigManager.GetPollStatusUrl(taskId);
#if TJGENERATORS_DEBUG
            int abortEpoch = TJGeneratorsTaskRecovery.GetLocalPollAbortEpoch();
#endif

            for (int retry = 0; retry < MAX_POLL_RETRIES; retry++)
            {
#if TJGENERATORS_DEBUG
                if (TJGeneratorsTaskRecovery.WasLocalPollAborted(abortEpoch))
                {
                    outcome.Error = TJGeneratorsL10n.L("任务已取消");
                    yield break;
                }
#endif

                TJTaskStatusResponse resp = null;
                string transportError = null;
                yield return _transport.PollStatus(
                    taskId,
                    pollUrl,
                    r => resp = r,
                    e => transportError = e
                );

#if TJGENERATORS_DEBUG
                if (TJGeneratorsTaskRecovery.WasLocalPollAborted(abortEpoch))
                {
                    outcome.Error = TJGeneratorsL10n.L("任务已取消");
                    yield break;
                }
#endif

                if (!string.IsNullOrEmpty(transportError))
                {
#if TJGENERATORS_DEBUG
                    yield return WaitSeconds(POLL_INTERVAL, abortEpoch);
#else
                    yield return WaitSeconds(POLL_INTERVAL);
#endif
                    continue;
                }

                if (resp == null)
                {
                    outcome.Error = TJGeneratorsL10n.L("无效响应");
                    yield break;
                }

                generator.ButtonText = $"{phaseLabel} {resp.status}...";
                _host.Repaint();

                if (resp.status == TaskStatus.Completed)
                {
                    outcome.Completed = resp;
                    yield break;
                }

                if (
                    resp.status == TaskStatus.Failed
                    || resp.status == TaskStatus.Error
                    || resp.status == TaskStatus.Cancelled
                )
                {
                    outcome.Error =
                        resp.status == TaskStatus.Cancelled
                            ? (
                                !string.IsNullOrEmpty(resp.error)
                                    ? resp.error
                                    : TJGeneratorsL10n.L("任务已取消")
                            )
                            : (
                                !string.IsNullOrEmpty(resp.error)
                                    ? resp.error
                                    : (
                                        !string.IsNullOrEmpty(resp.message)
                                            ? resp.message
                                            : resp.status
                                    )
                            );
                    yield break;
                }

#if TJGENERATORS_DEBUG
                yield return WaitSeconds(POLL_INTERVAL, abortEpoch);
#else
                yield return WaitSeconds(POLL_INTERVAL);
#endif
            }

            outcome.Error = TJGeneratorsL10n.L("轮询超时");
        }

        private static string GetMappedDownloadUrl(
            TJTaskStatusResponse response,
            GeneratorConfig cfg
        )
        {
            if (response?.output?.data?.result == null || cfg?.responseMapping == null)
                return null;
            string path = cfg.responseMapping.downloadUrlPath;
            if (string.IsNullOrEmpty(path))
                path = "model";
            return PathUtils.GetUrlString(response.output.data.result, path);
        }

        /// <summary>
        /// 主模型落地后：上传至 UniRig 绑骨，再请求混元 Motion，将动作剪辑绑定到绑骨 FBX。
        /// </summary>
        /// <param name="renderedTexturePath">主流程 rendered_image；仅在无法从进 UniRig 前的模型复用材质时作为回退。</param>
        private IEnumerator RunMotionPostProcessing(
            ModelGeneratorBase generator,
            string extractedModelPath,
            string motionDescription,
            string renderedTexturePath = null
        )
        {
            _postMotionRiggedPath = null;
            if (string.IsNullOrEmpty(extractedModelPath))
                yield break;

            string absMesh = PathUtils.ToAbsoluteAssetPath(extractedModelPath);
            if (!File.Exists(absMesh))
            {
                TJLog.LogWarning(
                    $"[GenerationPipeline] 后处理动作：模型文件不存在: {extractedModelPath}"
                );
                yield break;
            }

            EnsureTransport(generator);

            var unirigCfg = ConfigManager.GetGeneratorConfig(ConfigType.Generator, "unirig");
            var motionCfg = ConfigManager.GetGeneratorConfig(
                ConfigType.Generator,
                "hunyuan-motion"
            );
            if (unirigCfg == null || motionCfg == null)
            {
                TJLog.LogWarning(
                    "[GenerationPipeline] 后处理动作：未找到 unirig 或 hunyuan-motion 配置，跳过后处理"
                );
                yield break;
            }

            string modelDir = Path.GetDirectoryName(extractedModelPath)?.Replace("\\", "/") ?? "";
            string baseName = Path.GetFileNameWithoutExtension(extractedModelPath);

            string unirigEndpoint = unirigCfg.GetEndpoint("default");
            if (string.IsNullOrEmpty(unirigEndpoint))
            {
                TJLog.LogWarning("[GenerationPipeline] 后处理动作：UniRig 端点未配置");
                yield break;
            }

            string unirigUrl = API_BASE_URL + unirigEndpoint;
            var multipart = new MultipartRequestData
            {
                FilePath = absMesh,
                FileName = Path.GetFileName(absMesh) ?? "model.fbx",
                FileFieldName = "file",
                AdditionalFields = null,
            };

            TJTaskResponse createResp = null;
            string createErr = null;
            generator.ButtonText = TJGeneratorsL10n.L("提交绑骨任务...");
            _host.Repaint();
            yield return _transport.CreateTaskMultipart(
                unirigUrl,
                multipart,
                r => createResp = r,
                e => createErr = e
            );

            if (
                !string.IsNullOrEmpty(createErr)
                || createResp == null
                || string.IsNullOrEmpty(createResp.taskId)
            )
            {
                TJLog.LogWarning(
                    $"[GenerationPipeline] 后处理动作：UniRig 提交失败: {createErr ?? "无 taskId"}"
                );
                yield break;
            }

            var unirigOutcome = new MotionSubTaskPollOutcome();
            yield return PollSimpleTaskUntilComplete(
                generator,
                createResp.taskId,
                TJGeneratorsL10n.L("绑骨"),
                unirigOutcome
            );
            if (unirigOutcome.Completed == null)
            {
                TJLog.LogWarning(
                    $"[GenerationPipeline] 后处理动作：绑骨未完成: {unirigOutcome.Error ?? "未知错误"}"
                );
                yield break;
            }

            string riggedUrl = GetMappedDownloadUrl(unirigOutcome.Completed, unirigCfg);
            if (string.IsNullOrEmpty(riggedUrl))
            {
                TJLog.LogWarning("[GenerationPipeline] 后处理动作：绑骨响应中无模型 URL");
                yield break;
            }

            string riggedExt = GenerationAssetFormatUtils.GetExtensionFromUrl(riggedUrl) ?? ".fbx";
            string riggedSavePath = Path.Combine(modelDir, baseName + "_rigged" + riggedExt)
                .Replace("\\", "/");
            generator.ButtonText = TJGeneratorsL10n.L("下载绑骨模型...");
            _host.Repaint();
            yield return DownloadFile(riggedUrl, riggedSavePath);

            if (!File.Exists(PathUtils.ToAbsoluteAssetPath(riggedSavePath)))
            {
                TJLog.LogWarning("[GenerationPipeline] 后处理动作：绑骨模型下载失败");
                yield break;
            }

            if (riggedSavePath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                RiggedModelPostProcess.FinalizeRiggedImport(
                    riggedSavePath,
                    extractedModelPath,
                    renderedTexturePath
                );
            }
            else
            {
                PathUtils.ImportAssetAfterDiskWrite(riggedSavePath);
                RiggedModelPostProcess.ApplyTexturesFromSourceToRiggedModel(
                    extractedModelPath,
                    riggedSavePath,
                    renderedTexturePath
                );
            }

            _postMotionRiggedPath = riggedSavePath;

            if (string.IsNullOrWhiteSpace(motionDescription))
            {
                TJLog.Log("[GenerationPipeline] 后处理动作：无动作描述，仅完成绑骨");
                yield break;
            }

            string motionEndpoint = motionCfg.GetEndpoint("default");
            if (string.IsNullOrEmpty(motionEndpoint))
            {
                TJLog.LogWarning("[GenerationPipeline] 后处理动作：混元 Motion 端点未配置");
                yield break;
            }

            string motionUrl = API_BASE_URL + motionEndpoint;
            var motionPayload = new HyMotionPostPayload
            {
                inputText = motionDescription.Trim(),
                actionDuration = 5f,
                cfgStrength = 5f,
                randomSeedList = "0",
            };
            string motionJson = JsonUtility.ToJson(motionPayload);
            byte[] motionBytes = Encoding.UTF8.GetBytes(motionJson);

            TJTaskResponse motionCreate = null;
            string motionCreateErr = null;
            generator.ButtonText = TJGeneratorsL10n.L("提交动作生成...");
            _host.Repaint();
            yield return _transport.CreateTask(
                motionUrl,
                motionBytes,
                r => motionCreate = r,
                e => motionCreateErr = e
            );

            if (
                !string.IsNullOrEmpty(motionCreateErr)
                || motionCreate == null
                || string.IsNullOrEmpty(motionCreate.taskId)
            )
            {
                TJLog.LogWarning(
                    $"[GenerationPipeline] 后处理动作：混元 Motion 提交失败: {motionCreateErr ?? "无 taskId"}"
                );
                yield break;
            }

            var motionOutcome = new MotionSubTaskPollOutcome();
            yield return PollSimpleTaskUntilComplete(
                generator,
                motionCreate.taskId,
                TJGeneratorsL10n.L("动作生成"),
                motionOutcome
            );
            if (motionOutcome.Completed == null)
            {
                TJLog.LogWarning(
                    $"[GenerationPipeline] 后处理动作：动作任务未完成: {motionOutcome.Error ?? "未知"}"
                );
                yield break;
            }

            string motionFbxUrl = GetMappedDownloadUrl(motionOutcome.Completed, motionCfg);
            if (string.IsNullOrEmpty(motionFbxUrl))
            {
                TJLog.LogWarning("[GenerationPipeline] 后处理动作：混元 Motion 响应中无下载 URL");
                yield break;
            }

            string motionExt =
                GenerationAssetFormatUtils.GetExtensionFromUrl(motionFbxUrl) ?? ".fbx";
            string motionSavePath = Path.Combine(modelDir, baseName + "_motion" + motionExt)
                .Replace("\\", "/");
            generator.ButtonText = TJGeneratorsL10n.L("下载动作模型...");
            _host.Repaint();
            yield return DownloadFile(motionFbxUrl, motionSavePath);

            if (!File.Exists(PathUtils.ToAbsoluteAssetPath(motionSavePath)))
            {
                TJLog.LogWarning("[GenerationPipeline] 后处理动作：动作文件下载失败");
                yield break;
            }

            if (motionSavePath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                RiggedModelPostProcess.SetupAnimationImport(motionSavePath);

            PathUtils.ImportAssetAfterDiskWrite(motionSavePath);

            string riggedBaseName = Path.GetFileNameWithoutExtension(riggedSavePath);
            generator.ButtonText = TJGeneratorsL10n.L("创建动画控制器...");
            _host.Repaint();
            RiggedModelPostProcess.CreateSingleClipLoopAnimatorControllerFromMotionClip(
                modelDir,
                riggedBaseName,
                motionSavePath
            );
        }

        private IEnumerator DownloadFile(string url, string savePath)
        {
            string directory = Path.GetDirectoryName(savePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory))
                PathUtils.EnsureAssetFolder(directory);

            string downloadError = null;
            yield return PipelineDownloadHelper.DownloadUrlToFile(
                url,
                savePath,
                120f,
                onError: err => downloadError = err
            );

            if (string.IsNullOrEmpty(downloadError))
            {
                PathUtils.ImportAssetAfterDiskWrite(savePath);
                TJLog.Log($"[GenerationPipeline] 文件下载完成: {savePath}");
            }
            else
            {
                TJLog.LogWarning(
                    $"[GenerationPipeline] 文件下载失败: {url}, error: {downloadError}"
                );
            }
        }

        /// <summary>
        /// 下载 Tripo rendered_image (webp) 到模型目录，供 FBX 材质使用。
        /// </summary>
        private IEnumerator DownloadRenderedImage(string imageUrl, string unityRelativePath)
        {
            byte[] imageData = null;
            string downloadError = null;
            yield return PipelineDownloadHelper.DownloadUrlToFile(
                imageUrl,
                unityRelativePath,
                60f,
                onSuccess: data => imageData = data,
                onError: err => downloadError = err
            );

            if (string.IsNullOrEmpty(downloadError) && imageData != null && imageData.Length > 0)
            {
                TJLog.Log(
                    $"[GenerationPipeline] rendered_image 已下载: {unityRelativePath}, size={imageData.Length}"
                );
                PathUtils.ImportAssetAfterDiskWrite(unityRelativePath);
            }
            else
            {
                TJLog.LogWarning(
                    $"[GenerationPipeline] rendered_image 下载失败: {imageUrl}, error={downloadError}"
                );
            }
        }

        private void ObjModelPostProcessing(string assetPath)
        {
            ModelImporter modelImporter = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (modelImporter != null)
            {
                string directoryPath = Path.GetDirectoryName(assetPath)?.Replace("\\", "/");
                string absoluteDirectoryPath = PathUtils.ToAbsoluteAssetPath(directoryPath);

                modelImporter.importNormals = ModelImporterNormals.Calculate;
                modelImporter.normalCalculationMode =
                    ModelImporterNormalCalculationMode.AreaAndAngleWeighted;
                modelImporter.importBlendShapes = true;
                modelImporter.importTangents = ModelImporterTangents.CalculateMikk;
                modelImporter.isReadable = true;
                modelImporter.SearchAndRemapMaterials(
                    ModelImporterMaterialName.BasedOnTextureName,
                    ModelImporterMaterialSearch.Local
                );
                modelImporter.SaveAndReimport();

                foreach (string filePath in Directory.GetFiles(absoluteDirectoryPath))
                {
                    string extension = Path.GetExtension(filePath).ToLower();
                    if (extension == ".png" || extension == ".jpg" || extension == ".jpeg")
                    {
                        string fileName = Path.GetFileName(filePath).ToLower();
                        string unityPath = PathUtils.AbsolutePathToAssetsRelative(filePath);

                        if (
                            fileName.Contains("normal")
                            || fileName.Contains("_n.")
                            || fileName.Contains("_norm")
                        )
                        {
                            TextureImporter textureImporter =
                                AssetImporter.GetAtPath(unityPath) as TextureImporter;
                            if (textureImporter != null)
                            {
                                textureImporter.textureType = TextureImporterType.NormalMap;
                                textureImporter.SaveAndReimport();
                            }
                        }
                    }
                }

                TJLog.Log(
                    $"[GenerationPipeline] OBJ模型导入设置已配置: 法线计算={modelImporter.importNormals}, 切线计算={modelImporter.importTangents}"
                );
                TJLog.Log($"[GenerationPipeline] OBJ模型后处理完成: {assetPath}");
            }
        }

        /// <summary>
        /// 将生成的模型绑定到目标 Prefab。
        /// 仅替换名为 "GeneratedModel" 或 "Placeholder" 的子对象，保留其他子对象和根组件。
        /// </summary>
        public void BindModelToPrefab(
            string modelPath,
            float scale = 1f,
            Vector3 rotation = default
        )
        {
            BindModelToPrefab(modelPath, scale, rotation, autoFitToTargetScale: true);
        }

        /// <summary>
        /// 将生成的模型绑定到目标 Prefab，并按包围盒把模型自适应归一化到目标尺寸，
        /// 避免不同生成器产出模型的原始单位差异导致绑定后模型在场景中过小（表现为一个点）。
        /// 仅目标尺寸大于 0 且可计算到有效包围盒时才做归一化；此时忽略传入的 scale。
        /// </summary>
        public void BindModelToPrefab(
            string modelPath,
            float scale = 1f,
            Vector3 rotation = default,
            float targetSize = DefaultModelTargetSize,
            bool autoFitToTargetScale = false
        )
        {
            var targetAsset = _host.GetTargetAsset();
            if (targetAsset == null || !targetAsset.IsValid())
                return;

            string prefabPath = targetAsset.GetPath();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                TJLog.LogError($"[GenerationPipeline] 无法加载目标Prefab: {prefabPath}");
                return;
            }

            var modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (modelPrefab == null)
            {
                TJLog.LogError($"[GenerationPipeline] 无法加载生成的模型: {modelPath}");
                return;
            }

            // 修改 Prefab 结构（增删子节点）会导致 Unity 在传播变更时丢失场景实例的 position
            // override，使实例位置重置回原点。提前保存所有场景实例的 local transform，
            // 在 SafeRefresh() 之后恢复，避免用户手动调整的位置被覆盖。
            var savedInstanceTransforms = CollectSceneInstanceLocalTransforms(prefab);

            // Use prefabPath directly — GetPrefabAssetPathOfNearestInstanceRoot only works on scene
            // instances, not on prefab assets loaded via LoadAssetAtPath (returns "" for assets).
            string prefabAssetPath = prefabPath.Replace("\\", "/");
            using (var editScope = new PrefabContentsEditScope(prefabAssetPath))
            {
                var prefabRoot = editScope.prefabContentsRoot;

                for (int i = prefabRoot.transform.childCount - 1; i >= 0; i--)
                {
                    var child = prefabRoot.transform.GetChild(i).gameObject;
                    if (child.name == "GeneratedModel" || child.name == "Placeholder")
                    {
                        UnityEngine.Object.DestroyImmediate(child);
                    }
                }

                var modelInstance =
                    PrefabUtility.InstantiatePrefab(modelPrefab, prefabRoot.transform)
                    as GameObject;
                if (modelInstance != null)
                {
                    modelInstance.name = "GeneratedModel";
                    modelInstance.transform.localPosition = Vector3.zero;
                    modelInstance.transform.localRotation = Quaternion.Euler(rotation);
                    modelInstance.transform.localScale = new Vector3(scale, scale, scale);

                    // 模型如果小到几乎看不见（一个点），按包围盒自适应放大到目标尺寸。
                    if (autoFitToTargetScale && targetSize > 0f)
                    {
                        float normalized = ComputeAutoFitScale(modelPrefab, targetSize);
                        if (normalized > 0f)
                            modelInstance.transform.localScale = new Vector3(normalized, normalized, normalized);
                    }

                    ApplyDefaultMaterialIfMissing(modelInstance);
                }

                Avatar bindAvatar = null;
                if (modelInstance != null)
                {
                    var modelAnimator = modelInstance.GetComponent<Animator>();
                    if (modelAnimator != null)
                    {
                        bindAvatar = modelAnimator.avatar;
                        UnityEngine.Object.DestroyImmediate(modelAnimator);
                    }
                }

                string modelDir2 = Path.GetDirectoryName(modelPath)?.Replace("\\", "/") ?? "";
                string modelBaseName = Path.GetFileNameWithoutExtension(modelPath);
                string ctrlPath = Path.Combine(modelDir2, modelBaseName + "_Controller.controller")
                    .Replace("\\", "/");
                var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ctrlPath);

                var animator = prefabRoot.GetComponent<Animator>();
                if (ctrl != null || bindAvatar != null)
                {
                    if (animator == null)
                        animator = prefabRoot.AddComponent<Animator>();
                    if (ctrl != null)
                        animator.runtimeAnimatorController = ctrl;
                    if (bindAvatar != null)
                        animator.avatar = bindAvatar;
                }
                else
                {
                    if (animator != null)
                    {
                        TJLog.Log("[GenerationPipeline] 新建静态模型，清理根节点遗留的 Animator");
                        UnityEngine.Object.DestroyImmediate(animator);
                    }
                }
            }

            AssetDatabase.SaveAssets();
            PathUtils.SafeRefresh();

            RestoreSceneInstanceLocalTransforms(savedInstanceTransforms);

            TJLog.Log($"[GenerationPipeline] 模型已绑定到Prefab: {prefabPath}");
        }

        /// <summary>
        /// 按模型所有渲染器包围盒的最长边计算归一化 scale，使其最长边恰好等于 targetSize。
        /// 返回 targetSize / longestEdge。无可渲染网格、或包围盒无法计算/退化为 0 时返回 0（表示不做归一化）。
        /// </summary>
        private float ComputeAutoFitScale(GameObject modelPrefab, float targetSize)
        {
            var renderers = modelPrefab.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return 0f;

            var bounds = new Bounds();
            bool hasBounds = false;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (!hasBounds)
                {
                    bounds = r.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }

            if (!hasBounds)
                return 0f;

            Vector3 size = bounds.size;
            float longest = Mathf.Max(Mathf.Max(size.x, size.y), size.z);
            if (longest <= Mathf.Epsilon)
                return 0f;

            return targetSize / longest;
        }

        /// <summary>
        /// 失败/取消时从目标 Prefab 中删除名为 "Placeholder" 的子对象，保留根与其他子节点。
        /// </summary>
        private void RemovePlaceholderFromPrefab()
        {
            var targetAsset = _host.GetTargetAsset();
            if (targetAsset == null || !targetAsset.IsValid())
                return;

            string prefabPath = targetAsset.GetPath().Replace("\\", "/");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return;

            var savedTransforms = CollectSceneInstanceLocalTransforms(prefab);
            bool removed = false;

            using (var editScope = new PrefabContentsEditScope(prefabPath))
            {
                var prefabRoot = editScope.prefabContentsRoot;
                for (int i = prefabRoot.transform.childCount - 1; i >= 0; i--)
                {
                    var child = prefabRoot.transform.GetChild(i).gameObject;
                    if (child.name == "Placeholder")
                    {
                        UnityEngine.Object.DestroyImmediate(child);
                        removed = true;
                    }
                }
            }

            if (!removed)
                return;

            AssetDatabase.SaveAssets();
            PathUtils.SafeRefresh();
            RestoreSceneInstanceLocalTransforms(savedTransforms);
            TJLog.Log("[GenerationPipeline] 已移除占位 GameObject");
        }

        private struct InstanceTransformSnapshot
        {
            public Transform Transform;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public Vector3 LocalScale;
        }

        /// <summary>
        /// 收集活动场景中所有属于指定 Prefab 的顶层实例的 local transform。
        /// </summary>
        private static List<InstanceTransformSnapshot> CollectSceneInstanceLocalTransforms(
            GameObject prefabAsset
        )
        {
            var result = new List<InstanceTransformSnapshot>();
            if (prefabAsset == null)
                return result;

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!scene.isLoaded)
                return result;

            foreach (var root in scene.GetRootGameObjects())
                CollectPrefabInstancesRecursive(root, prefabAsset, result);

            return result;
        }

        private static void CollectPrefabInstancesRecursive(
            GameObject obj,
            GameObject prefabAsset,
            List<InstanceTransformSnapshot> result
        )
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(obj);
            if (source == prefabAsset)
            {
                result.Add(
                    new InstanceTransformSnapshot
                    {
                        Transform = obj.transform,
                        LocalPosition = obj.transform.localPosition,
                        LocalRotation = obj.transform.localRotation,
                        LocalScale = obj.transform.localScale,
                    }
                );
                return;
            }

            foreach (Transform child in obj.transform)
                CollectPrefabInstancesRecursive(child.gameObject, prefabAsset, result);
        }

        /// <summary>
        /// 将场景实例的 local transform 恢复到快照中保存的值（配合 Undo 使操作可撤销）。
        /// </summary>
        private static void RestoreSceneInstanceLocalTransforms(
            List<InstanceTransformSnapshot> snapshots
        )
        {
            foreach (var snap in snapshots)
            {
                if (snap.Transform == null)
                    continue;
                Undo.RecordObject(snap.Transform, "Restore Instance Transform After Generation");
                snap.Transform.localPosition = snap.LocalPosition;
                snap.Transform.localRotation = snap.LocalRotation;
                snap.Transform.localScale = snap.LocalScale;
            }
        }

        /// <summary>
        /// 获取或创建默认白色材质，用于给缺失或错误着色器的模型补材质。
        /// </summary>
        private static Material GetOrCreateDefaultMaterial()
        {
            const string defaultMatPath = "Assets/TJGenerators/DefaultWhite.mat";

            var existing = AssetDatabase.LoadAssetAtPath<Material>(defaultMatPath);
            if (existing != null)
            {
                // 确保已经存在的默认材质真的是“白色”，防止之前被误调成紫色等颜色。
                bool changed = false;
                if (existing.HasProperty("_BaseColor"))
                {
                    existing.SetColor("_BaseColor", Color.white);
                    changed = true;
                }
                if (existing.HasProperty("_Color"))
                {
                    existing.SetColor("_Color", Color.white);
                    changed = true;
                }

                if (changed)
                {
                    EditorUtility.SetDirty(existing);
                    AssetDatabase.SaveAssets();
                }

                return existing;
            }

            const string folderParent = "Assets";
            const string folderName = "TJGenerators";
            if (!AssetDatabase.IsValidFolder($"{folderParent}/{folderName}"))
            {
                AssetDatabase.CreateFolder(folderParent, folderName);
            }

            var shader = TJMaterialShaderUtility.ResolveSurfaceLitShader();

            if (shader == null)
            {
                return null;
            }

            var mat = new Material(shader);

            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", Color.white);
            }
            if (mat.HasProperty("_Color"))
            {
                mat.SetColor("_Color", Color.white);
            }

            AssetDatabase.CreateAsset(mat, defaultMatPath);
            AssetDatabase.SaveAssets();

            return mat;
        }

        /// <summary>
        /// 将缺失或错误着色器的材质替换为默认材质，作用于实际生成的模型实例。
        /// </summary>
        private static void ApplyDefaultMaterialIfMissing(GameObject root)
        {
            if (root == null)
                return;

            var defaultMat = GetOrCreateDefaultMaterial();
            if (defaultMat == null)
            {
                return;
            }

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null || renderer.sharedMaterials == null)
                    continue;

                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    bool missingShader =
                        mat == null
                        || mat.shader == null
                        || mat.shader.name == "Hidden/InternalErrorShader";
                    if (missingShader)
                    {
                        mats[i] = defaultMat;
                        changed = true;
                    }
                }

                if (changed)
                {
                    renderer.sharedMaterials = mats;
                }
            }
        }

        /// <summary>
        /// 完成生成任务。多图时 savePaths 与 imageUrls 数量一致，会拆成多条历史（一图一格）。
        /// </summary>
        private void CompleteGeneration(
            ModelGeneratorBase generator,
            string modelPath,
            string[] imageUrls = null,
            List<string> savePaths = null
        )
        {
            if (!string.IsNullOrEmpty(generator.CurrentBackendTaskId))
            {
                TJGeneratorsTaskRecovery.RemoveInterruptedTask(generator.CurrentBackendTaskId);
            }

            // 图片/音频/序列帧类型：effectivePreviewUrl 已由各 Handle* 方法写入 generator
            // 3D 模型/带动画角色类型：generator.CurrentPreviewUrl 为 null，在此处计算
            string effectivePreviewUrl = generator.CurrentPreviewUrl;

            if (string.IsNullOrEmpty(effectivePreviewUrl))
            {
                effectivePreviewUrl = _currentPreviewUrl;

                if (string.IsNullOrEmpty(effectivePreviewUrl) && _activeTaskHandle != null)
                    effectivePreviewUrl = _activeTaskHandle.PreviewUrl;

                // 本地文件 URI 仅限图片/音频/视频；3D 模型文件不能作为预览图
                if (string.IsNullOrEmpty(effectivePreviewUrl) && !string.IsNullOrEmpty(modelPath))
                {
                    bool isPreviewable =
                        modelPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        || modelPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        || modelPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                        || modelPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                        || modelPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                        || modelPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                        || modelPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                        || modelPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
                    if (isPreviewable)
                    {
                        string fullPath = PathUtils.ToAbsoluteAssetPath(modelPath);
                        if (File.Exists(fullPath))
                            effectivePreviewUrl = "file://" + fullPath.Replace('\\', '/');
                    }
                }
            }

            _currentPreviewUrl = null;
            generator.CurrentPreviewUrl = null;

            if (!string.IsNullOrEmpty(generator.CurrentGeneratingTaskId))
            {
                string promptTemplateId = null;
                if (generator is DynamicGenerator dgPrompt)
                    promptTemplateId = dgPrompt.GetSelectedPromptTemplateId();

                if (
                    savePaths != null
                    && savePaths.Count > 1
                    && imageUrls != null
                    && imageUrls.Length == savePaths.Count
                )
                {
                    TJGeneratorsHistoryManager.CompletePlaceholderMultiImage(
                        generator.CurrentGeneratingTaskId,
                        savePaths,
                        imageUrls,
                        promptTemplateId
                    );
                }
                else
                {
                    TJGeneratorsHistoryManager.CompletePlaceholder(
                        generator.CurrentGeneratingTaskId,
                        modelPath,
                        effectivePreviewUrl,
                        promptTemplateId: promptTemplateId
                    );
                }
            }

            _mediaHandlers.ClearMediaSavePaths();

            EndGenerationState(generator);

            if (_activeTaskHandle != null)
            {
                _activeTaskHandle.MarkCompleted(modelPath, effectivePreviewUrl);
                _activeTaskHandle = null;
            }

            _host.RefreshHistory();
            _host.OnGenerationCompleted(modelPath);
            _host.Repaint();

            _host.RefreshUserInfo();

            TJLog.Log($"[GenerationPipeline] 生成完成: {modelPath}");
        }

        private string EnhanceErrorMessage(string originalError, ModelGeneratorBase generator)
        {
            if (string.IsNullOrEmpty(originalError))
                return null;

            if (generator != null && generator.GetModelVersion().Contains("animation"))
            {
                if (
                    originalError.Contains("step 3 rig failed")
                    || (
                        originalError.Contains("422")
                        && originalError.Contains("Pose estimation failed")
                    )
                )
                {
                    return TJGeneratorsL10n.L(
                        "动画绑定失败：您的提示词描述的可能不是一个角色。请确保描述的是有身体结构的角色（如人类、动物、机器人），而不是物品（如食物、车辆、建筑）。"
                    );
                }
            }

            if (originalError.Contains("422"))
            {
                return TJGeneratorsL10n.L(
                    "请求参数错误：请检查您的输入是否符合要求，特别是提示词内容和格式。"
                );
            }

            if (originalError.Contains("429"))
            {
                return TJGeneratorsL10n.L("请求频率过高：API调用次数超出限制，请稍后重试。");
            }

            if (originalError.Contains("401"))
            {
                return TJGeneratorsL10n.L("认证失败：API密钥可能无效或账户配额不足，请检查配置。");
            }

            if (originalError.Contains("500") || originalError.Contains("503"))
            {
                return TJGeneratorsL10n.L("模型生成失败，请稍后重试。");
            }

            return null;
        }

        public void HandleError(
            ModelGeneratorBase generator,
            string message,
            string status = "error"
        )
        {
            TJLog.LogError($"[GenerationPipeline] {message}");
            _host.ShowDialog(TJGeneratorsL10n.L("错误"), message);

            if (_activeTaskHandle != null)
            {
                _activeTaskHandle.MarkFailed(status, message);
                _activeTaskHandle = null;
            }

            if (!string.IsNullOrEmpty(generator.CurrentBackendTaskId))
            {
                TJGeneratorsTaskRecovery.RemoveInterruptedTask(generator.CurrentBackendTaskId);
            }

            if (!string.IsNullOrEmpty(generator.CurrentGeneratingTaskId))
            {
                TJGeneratorsHistoryManager.RemovePlaceholder(generator.CurrentGeneratingTaskId);
            }

            RemovePlaceholderFromPrefab();
            EndGenerationState(generator);
            _host.RefreshHistory();
            _host.Repaint();
        }

#if TJGENERATORS_DEBUG
        /// <summary>
        /// 开发菜单等触发的本地轮询中止：不弹错误框、不请求后端 cancel。仅 DEBUG 构建可用。
        /// </summary>
        private void HandleLocalPollAbort(ModelGeneratorBase generator, string taskId)
        {
            TJLog.Log($"[GenerationPipeline] 本地已中止轮询: {taskId}");

            if (_activeTaskHandle != null)
            {
                _activeTaskHandle.MarkFailed("cancelled", TJGeneratorsL10n.L("任务已取消"));
                _activeTaskHandle = null;
            }

            if (!string.IsNullOrEmpty(generator.CurrentBackendTaskId))
                TJGeneratorsTaskRecovery.RemoveInterruptedTask(generator.CurrentBackendTaskId);

            if (!string.IsNullOrEmpty(generator.CurrentGeneratingTaskId))
                TJGeneratorsHistoryManager.RemovePlaceholder(generator.CurrentGeneratingTaskId);

            RemovePlaceholderFromPrefab();
            EndGenerationState(generator);
            _host.RefreshHistory();
            _host.Repaint();
        }
#endif
        /// <summary>
        /// 处理轮询超时（不移除任务记录，允许重连）
        /// </summary>
        private void HandlePollingTimeout(ModelGeneratorBase generator, string message)
        {
            TJLog.LogError($"[GenerationPipeline] {message}");
            _host.ShowDialog(TJGeneratorsL10n.L("超时"), message);

            if (_activeTaskHandle != null)
            {
                _activeTaskHandle.MarkFailed("polling_timeout", message);
                _activeTaskHandle = null;
            }

            if (!string.IsNullOrEmpty(generator.CurrentBackendTaskId))
            {
                TJGeneratorsTaskRecovery.UpdateTaskStatus(
                    generator.CurrentBackendTaskId,
                    "polling_timeout"
                );
            }

            EndGenerationState(generator);
            _host.Repaint();
        }

        private void UpdateHistoryProgress(ModelGeneratorBase generator, int progress)
        {
            if (progress > 0 && !string.IsNullOrEmpty(generator.CurrentGeneratingTaskId))
            {
                TJGeneratorsHistoryManager.UpdatePlaceholderProgress(
                    generator.CurrentGeneratingTaskId,
                    progress
                );
                _host.RefreshHistory();
            }
        }

        /// <summary>
        /// 对下载 URL 做 MD5 取前 16 个十六进制字符 + 扩展名作为槽内模型文件名；URL 为空时用随机 GUID，扩展名优先 URL、否则沿用分组路径。
        /// </summary>
        private string BuildDownloadModelAssetFileName(string modelUrl, string groupingPath)
        {
            string ext = GenerationAssetFormatUtils.GetExtensionFromUrl(modelUrl);
            if (string.IsNullOrEmpty(ext))
                ext = Path.GetExtension(groupingPath);
            if (string.IsNullOrEmpty(ext))
                ext = ".fbx";

            string source = string.IsNullOrEmpty(modelUrl)
                ? Guid.NewGuid().ToString("N")
                : modelUrl;
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(source));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString() + ext.ToLowerInvariant();
            }
        }

        private string ResolveModelDownloadPath(string savePath, string modelUrl)
        {
            if (_host is IModelDownloadPathProvider pathProvider)
            {
                string directPath = pathProvider.GetModelDownloadPath(savePath);
                if (!string.IsNullOrEmpty(directPath))
                    return PrepareDirectModelDownloadPath(directPath);
            }

            return GetUniqueFilePath(savePath, BuildDownloadModelAssetFileName(modelUrl, savePath));
        }

        private static string PrepareDirectModelDownloadPath(string assetPath)
        {
            assetPath = assetPath?.Replace("\\", "/");
            if (string.IsNullOrEmpty(assetPath))
                return assetPath;

            string dir = Path.GetDirectoryName(assetPath)?.Replace("\\", "/");
            if (!string.IsNullOrEmpty(dir))
            {
                PathUtils.EnsureAssetFolder(dir);
                string absDir = PathUtils.ToAbsoluteAssetPath(dir);
                if (!string.IsNullOrEmpty(absDir) && !Directory.Exists(absDir))
                    Directory.CreateDirectory(absDir);
            }

            return assetPath;
        }

        private string GetModelSavePath(string fileName)
        {
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
            {
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            }

            var targetAsset = _host.GetTargetAsset();
            if (targetAsset != null && targetAsset.IsValid())
            {
                string prefabName = Path.GetFileNameWithoutExtension(targetAsset.GetPath());
                string ext = Path.GetExtension(fileName);
                return Path.Combine(SAVE_DIRECTORY, $"{prefabName}{ext}");
            }
            return Path.Combine(SAVE_DIRECTORY, fileName);
        }

        /// <summary>
        /// 在 <c>Assets/TJGenerators/History/{分组名}/01/</c> 下分配序号子目录。
        /// 分组目录名来自 <paramref name="groupingPath"/> 的文件基名（如 Prefab「new mesh 1」）；槽内模型文件名优先用 <paramref name="diskFileNameFromUrl"/>（通常为 URL 的 MD5 短名 + 扩展名）。
        /// </summary>
        private string GetUniqueFilePath(string groupingPath, string diskFileNameFromUrl = null)
        {
            string fileExtension = Path.GetExtension(groupingPath);
            string fileName = !string.IsNullOrEmpty(diskFileNameFromUrl)
                ? diskFileNameFromUrl
                : Path.GetFileName(groupingPath);
            if (string.IsNullOrEmpty(fileName))
                fileName = "Model" + (string.IsNullOrEmpty(fileExtension) ? ".fbx" : fileExtension);

            string baseLabel = Path.GetFileNameWithoutExtension(groupingPath);
            if (string.IsNullOrEmpty(baseLabel))
                baseLabel = "Model";
            string groupFolderName = PathUtils.SanitizeAssetFolderName(baseLabel);

            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            string historyRoot = HISTORY_DIRECTORY.TrimEnd('/', '\\');
            if (!AssetDatabase.IsValidFolder(historyRoot))
                AssetDatabase.CreateFolder("Assets/TJGenerators", "History");

            string groupPath = $"{historyRoot}/{groupFolderName}".Replace("\\", "/");

            if (!AssetDatabase.IsValidFolder(groupPath))
            {
                AssetDatabase.CreateFolder(historyRoot, groupFolderName);
            }

            if (!AssetDatabase.IsValidFolder(groupPath))
            {
                TJLog.LogError($"[GenerationPipeline] 无法创建 History 分组目录: {groupPath}");
                groupFolderName = $"Model_{Guid.NewGuid():N}";
                groupPath = $"{historyRoot}/{groupFolderName}".Replace("\\", "/");
                AssetDatabase.CreateFolder(historyRoot, groupFolderName);
            }

            bool SlotFolderIsUnused(string slot)
            {
                string folderAssetPath = $"{groupPath}/{slot}";
                if (AssetDatabase.IsValidFolder(folderAssetPath))
                    return false;
                string absSlot = PathUtils.ToAbsoluteAssetPath(folderAssetPath);
                return absSlot == null || !Directory.Exists(absSlot);
            }

            for (int index = 1; index < 10000; index++)
            {
                string slot = index.ToString("D2");
                if (!SlotFolderIsUnused(slot))
                    continue;

                AssetDatabase.CreateFolder(groupPath, slot);
                if (!AssetDatabase.IsValidFolder($"{groupPath}/{slot}"))
                    continue;

                string candidate = $"{groupPath}/{slot}/{fileName}".Replace("\\", "/");
                return AssetDatabase.GenerateUniqueAssetPath(candidate);
            }

            string fallbackSlot = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
            AssetDatabase.CreateFolder(groupPath, fallbackSlot);
            string fallbackPath = $"{groupPath}/{fallbackSlot}/{fileName}".Replace("\\", "/");
            TJLog.LogWarning(
                $"[GenerationPipeline] History 序号子目录已满，改用时间戳: {fallbackPath}"
            );
            return AssetDatabase.GenerateUniqueAssetPath(fallbackPath);
        }

        /// <summary>
        /// 将 Tripo rendered_image 等贴图应用到已导入模型资源下所有 Renderer 材质（主贴图 / URP _BaseMap / _MainTex）。
        /// 用于主 FBX 后处理；绑骨后仅在无法从源模型复用材质时作为回退。
        /// </summary>
        private void ApplyRenderedTextureToImportedModel(
            string assetPath,
            string renderedTexturePath
        )
        {
            if (string.IsNullOrEmpty(renderedTexturePath))
                return;

            Texture2D renderedTex = AssetDatabase.LoadAssetAtPath<Texture2D>(renderedTexturePath);
            if (renderedTex == null)
            {
                TJLog.LogWarning(
                    $"[GenerationPipeline] 无法加载 rendered 贴图: {renderedTexturePath}"
                );
                return;
            }

            var modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (modelPrefab == null)
                return;

            var renderers = modelPrefab.GetComponentsInChildren<Renderer>();
            int appliedCount = 0;
            foreach (var rend in renderers)
            {
                if (rend.sharedMaterials == null)
                    continue;
                foreach (var mat in rend.sharedMaterials)
                {
                    if (mat == null)
                        continue;
                    mat.mainTexture = renderedTex;
                    if (mat.HasProperty("_BaseMap"))
                        mat.SetTexture("_BaseMap", renderedTex);
                    if (mat.HasProperty("_MainTex"))
                        mat.SetTexture("_MainTex", renderedTex);
                    EditorUtility.SetDirty(mat);
                    appliedCount++;
                }
            }

            AssetDatabase.SaveAssets();
            TJLog.Log(
                $"[GenerationPipeline] 已将 rendered_image 贴图应用到 {appliedCount} 个材质: {renderedTexturePath} -> {assetPath}"
            );
        }

        /// <summary>
        /// 模型后处理（提取纹理、设置法线贴图等）。renderedTexturePath 为 Tripo rendered_image (webp) 的 Unity 相对路径时，会将其设为所有材质的主贴图。
        /// </summary>
        private void ModelPostProcessing(
            string assetPath,
            string renderedTexturePath = null
        )
        {
            ModelImporter modelImporter = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (modelImporter != null)
            {
                string parentDir = Path.GetDirectoryName(assetPath)?.Replace("\\", "/") ?? "";
                string safeBase = PathUtils.SanitizeAssetFolderName(
                    Path.GetFileNameWithoutExtension(assetPath)
                );
                // 每个 FBX 单独子目录，避免 Tripo 等固定贴图名（如 tripo_model_basecolor）在同一父目录下互相覆盖。
                string extractDirRelative = string.IsNullOrEmpty(parentDir)
                    ? $"{safeBase}.fbm"
                    : $"{parentDir}/{safeBase}.fbm";

                string absExtract = PathUtils.ToAbsoluteAssetPath(extractDirRelative);
                if (!string.IsNullOrEmpty(absExtract))
                    Directory.CreateDirectory(absExtract);

                modelImporter.ExtractTextures(extractDirRelative);

                // 提取出的贴图是新生成文件（非下载），不走 DownloadFile 的定向导入；
                // 这里先整目录导入，确保后续 SearchAndRemapMaterials 能拿到贴图 GUID
                PathUtils.ImportAssetsUnderFolderAfterDiskWrite(extractDirRelative);

                // 在 Refresh 之前，先单独导入法线贴图并设置类型，避免 NormalMap settings 弹窗
                if (!string.IsNullOrEmpty(absExtract) && Directory.Exists(absExtract))
                {
                    foreach (string filePath in Directory.GetFiles(absExtract))
                    {
                        string ext = Path.GetExtension(filePath).ToLowerInvariant();
                        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
                            continue;

                        string fileName = Path.GetFileName(filePath);
                        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                        bool treatAsNormalMap =
                            fileName.StartsWith("Normal", StringComparison.OrdinalIgnoreCase)
                            || nameWithoutExt.EndsWith(
                                "_normal",
                                StringComparison.OrdinalIgnoreCase
                            );
                        if (!treatAsNormalMap)
                            continue;

                        string unityPath = extractDirRelative.TrimEnd('/') + "/" + fileName;
                        AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceUpdate);
                        TextureImporter ti = AssetImporter.GetAtPath(unityPath) as TextureImporter;
                        if (ti != null)
                        {
                            ti.textureType = TextureImporterType.NormalMap;
                            ti.SaveAndReimport();
                        }
                    }
                }

                modelImporter.isReadable = true;
                modelImporter.SearchAndRemapMaterials(
                    ModelImporterMaterialName.BasedOnTextureName,
                    ModelImporterMaterialSearch.Local
                );
                modelImporter.SaveAndReimport();

                ApplyRenderedTextureToImportedModel(assetPath, renderedTexturePath);
            }
        }

        /// <summary>
        /// 校验已导入 FBX 是否含有效网格（顶点数 &gt; 0）。
        /// 损坏或空网格资产应在 CompleteGeneration 之前失败，避免 Agent 写入坏 model_path。
        /// </summary>
        private static bool ValidateImportedFbxMesh(string assetPath, out string reason)
        {
            reason = null;
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go == null)
            {
                reason = "无法加载 FBX GameObject";
                return false;
            }

            var meshFilters = go.GetComponentsInChildren<MeshFilter>(true);
            var skinnedRenderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            int totalVertices = 0;
            foreach (var mf in meshFilters)
            {
                if (mf.sharedMesh != null)
                    totalVertices += mf.sharedMesh.vertexCount;
            }

            foreach (var smr in skinnedRenderers)
            {
                if (smr.sharedMesh != null)
                    totalVertices += smr.sharedMesh.vertexCount;
            }

            if (totalVertices == 0)
            {
                int meshCount = meshFilters.Length + skinnedRenderers.Length;
                reason =
                    meshCount == 0
                        ? "FBX 不含任何网格"
                        : $"FBX 所有网格顶点数为 0（共 {meshCount} 个网格）";
                return false;
            }

            return true;
        }

        private IEnumerator WaitSeconds(float seconds
#if TJGENERATORS_DEBUG
            , int abortEpoch = -1
#endif
        )
        {
            double startTime = EditorApplication.timeSinceStartup;
            while (EditorApplication.timeSinceStartup - startTime < seconds)
            {
#if TJGENERATORS_DEBUG
                if (abortEpoch >= 0 && TJGeneratorsTaskRecovery.WasLocalPollAborted(abortEpoch))
                    yield break;
#endif
                yield return null;
            }
        }
    }
}
#endif
