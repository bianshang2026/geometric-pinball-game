#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using Unity.UniAsset.Manager.Editor.InternalBridge;
using TJGenerators.Generators;
using TJGenerators.Pipeline;
using TJGenerators.Config;
using TJGenerators.Utils;
using UnityEditor;
using UnityEngine;
using Unity.EditorCoroutines.Editor;

namespace TJGenerators
{
    public sealed class TJGeneratorsGenerationContext
    {
        public TJGeneratorsAssetReference TargetAsset { get; set; }
        public bool AutoCreateTargetPrefab { get; set; } = true;

        public static TJGeneratorsGenerationContext ForAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return new TJGeneratorsGenerationContext();

            return new TJGeneratorsGenerationContext
            {
                TargetAsset = TJGeneratorsAssetReference.FromPath(assetPath)
            };
        }

        public static TJGeneratorsGenerationContext ForAssetGuid(string assetGuid)
        {
            if (string.IsNullOrEmpty(assetGuid))
                return new TJGeneratorsGenerationContext();

            return new TJGeneratorsGenerationContext
            {
                TargetAsset = TJGeneratorsAssetReference.FromGuid(assetGuid)
            };
        }
    }

    public sealed class TJGeneratorsSubmitResult
    {
        public bool   Success;
        public string BackendTaskId;
        public string ErrorCode;   // AUTH_REQUIRED / INVALID_PARAMS / RATE_LIMITED / SERVER_ERROR / NETWORK_ERROR / CONFIG_ERROR
        public string Message;     // 用户可读的中文错误描述
    }

    public sealed class TJGeneratorsGenerationResult
    {
        public string ModelPath { get; internal set; }
        public string PreviewUrl { get; internal set; }
        public string BackendTaskId { get; internal set; }
        public string LocalTaskId { get; internal set; }
    }

    public sealed class TJGeneratorsTaskHandle
    {
        public string LocalTaskId { get; private set; }
        public string BackendTaskId { get; private set; }
        public string Status { get; private set; }
        public int Progress { get; private set; }
        public string ModelPath { get; private set; }
        public string PreviewUrl { get; private set; }
        public string ErrorMessage { get; private set; }
        public TJGeneratorsGenerationResult Result { get; private set; }
        public bool IsCompleted { get; private set; }
        public bool IsFailed { get; private set; }

        public event Action<TJGeneratorsTaskHandle> OnCreated;
        public event Action<TJGeneratorsTaskHandle> OnProgress;
        public event Action<TJGeneratorsTaskHandle> OnCompleted;
        public event Action<TJGeneratorsTaskHandle> OnFailed;

        internal void SetLocalTaskId(string taskId)
        {
            LocalTaskId = taskId;
        }

        internal void SetBackendTaskId(string taskId)
        {
            BackendTaskId = taskId;
        }

        internal void SetStatus(string status)
        {
            Status = status;
        }

        internal void UpdateProgress(string status, int progress)
        {
            Status = status;
            Progress = progress;
            InvokeSafely(OnProgress);
        }

        internal void SetPreviewUrl(string previewUrl)
        {
            PreviewUrl = previewUrl;
        }

        internal void NotifyCreated()
        {
            InvokeSafely(OnCreated);
        }

        internal void MarkCompleted(string modelPath, string previewUrl)
        {
            Status = "completed";
            Progress = 100;
            ModelPath = modelPath;
            PreviewUrl = previewUrl;
            IsCompleted = true;
            IsFailed = false;
            Result = new TJGeneratorsGenerationResult
            {
                ModelPath = modelPath,
                PreviewUrl = previewUrl,
                BackendTaskId = BackendTaskId,
                LocalTaskId = LocalTaskId
            };
            InvokeSafely(OnCompleted);
        }

        internal void MarkFailed(string status, string message)
        {
            Status = status;
            ErrorMessage = message;
            IsFailed = true;
            IsCompleted = false;
            InvokeSafely(OnFailed);
        }

        private void InvokeSafely(Action<TJGeneratorsTaskHandle> callback)
        {
            if (callback == null)
                return;

            foreach (Action<TJGeneratorsTaskHandle> handler in callback.GetInvocationList())
            {
                try
                {
                    handler(this);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }
    }

    public static class TJGeneratorsGenerationService
    {
        public static TJGeneratorsTaskHandle Generate(ModelGeneratorBase generator, TJGeneratorsGenerationContext context = null, string sessionId = "")
        {
            if (generator == null)
                throw new ArgumentNullException(nameof(generator));

            var resolvedContext = context ?? new TJGeneratorsGenerationContext();
            var taskHandle = new TJGeneratorsTaskHandle();
            var targetAsset = ResolveTargetAsset(resolvedContext, generator.GetOutputType());
            var host = new HeadlessGenerationHost(targetAsset);
            var outputType = generator.GetOutputType() ?? "";
            ConfigType configType;
            switch (outputType)
            {
                case GenerationOutputTypes.Sprite:          configType = ConfigType.Sprite; break;
                case GenerationOutputTypes.SpriteSequence:  configType = ConfigType.SpriteSequence; break;
                case GenerationOutputTypes.Material:        configType = ConfigType.Material; break;
                case GenerationOutputTypes.Audio:           configType = ConfigType.Music; break;
                case GenerationOutputTypes.Model:           configType = ConfigType.Generator; break;
                default:                                    configType = ConfigType.Skybox; break;
            }
            var pipeline = new GenerationPipeline(host, configType, GenerationRequestOrigin.Agent, sessionId);
            var assetGuid = targetAsset?.guid ?? "";

            EditorCoroutineUtility.StartCoroutineOwnerless(pipeline.StartGeneration(generator, assetGuid, taskHandle));
            return taskHandle;
        }

        public static TJGeneratorsTaskHandle Generate(ModelGeneratorBase generator, string targetAssetPath)
        {
            return Generate(generator, TJGeneratorsGenerationContext.ForAssetPath(targetAssetPath), "");
        }

        public static TJGeneratorsTaskHandle GenerateForGuid(ModelGeneratorBase generator, string targetAssetGuid)
        {
            return Generate(generator, TJGeneratorsGenerationContext.ForAssetGuid(targetAssetGuid), "");
        }

        /// <summary>
        /// 从已在后端成功创建的任务ID开始，启动异步轮询和下载，跳过 HTTP 提交阶段。
        /// 配合 CustomTool 两阶段模式使用：外部同步提交获得 backendTaskId 后调用此方法。
        /// </summary>
        public static TJGeneratorsTaskHandle GenerateFromSubmittedTask(
            ModelGeneratorBase generator,
            TJGeneratorsGenerationContext context,
            string backendTaskId,
            string sessionId = "",
            string toolName = "")
        {
            if (generator == null)
                throw new ArgumentNullException(nameof(generator));
            if (string.IsNullOrEmpty(backendTaskId))
                throw new ArgumentException("backendTaskId cannot be null or empty", nameof(backendTaskId));

            var resolvedContext = context ?? new TJGeneratorsGenerationContext();
            var taskHandle  = new TJGeneratorsTaskHandle();
            var targetAsset = ResolveTargetAsset(resolvedContext, generator.GetOutputType());
            var host        = new HeadlessGenerationHost(targetAsset);
            var outputType  = generator.GetOutputType() ?? "";
            ConfigType configType;
            switch (outputType)
            {
                case GenerationOutputTypes.Sprite:          configType = ConfigType.Sprite; break;
                case GenerationOutputTypes.SpriteSequence:  configType = ConfigType.SpriteSequence; break;
                case GenerationOutputTypes.Material:        configType = ConfigType.Material; break;
                case GenerationOutputTypes.Audio:           configType = ConfigType.Music; break;
                case GenerationOutputTypes.Model:           configType = ConfigType.Generator; break;
                default:                                    configType = ConfigType.Skybox; break;
            }
            var pipeline  = new GenerationPipeline(host, configType, GenerationRequestOrigin.Agent, sessionId, toolName ?? "");
            var assetGuid = targetAsset?.guid ?? "";

            EditorCoroutineUtility.StartCoroutineOwnerless(
                pipeline.StartFromSubmittedTask(generator, assetGuid, backendTaskId, taskHandle));
            return taskHandle;
        }

        /// <summary>
        /// 同步提交生成任务到后端（约 1-3 秒），立即返回 backendTaskId 或失败原因。
        /// 使用与 <see cref="GenerationPipeline"/> 相同的 UnityWebRequest 路径（阻塞等待），
        /// 避免 Unity Mono 下 HttpClient 的 Illegal byte sequence 缺陷；编辑器会短暂冻结。
        /// 所有 CustomTool 应调用此方法替代 pipeline.StartGeneration() 的提交阶段。
        /// </summary>
        public static TJGeneratorsSubmitResult SubmitTaskSync(ModelGeneratorBase generator, string sessionId = "")
        {
            if (TJGeneratorsPlayModeGuard.IsActive)
            {
                return new TJGeneratorsSubmitResult
                {
                    Success = false,
                    ErrorCode = "PLAY_MODE",
                    Message = TJGeneratorsPlayModeGuard.Message + "\n\nPlease exit Play Mode manually, then retry."
                };
            }

            // 1. 鉴权检查
            string token = UnityConnectSession.instance.GetAccessToken();
            if (string.IsNullOrEmpty(token))
                return new TJGeneratorsSubmitResult {
                    Success = false, ErrorCode = "AUTH_REQUIRED",
                    Message = TJGeneratorsL10n.L("未登录，请通过Unity编辑器左上角或Unity Hub登录后重试")
                };

            // 2. 构建 URL
            string endpoint = generator.ApiEndpoint;
            if (string.IsNullOrWhiteSpace(endpoint))
                return new TJGeneratorsSubmitResult {
                    Success = false, ErrorCode = "CONFIG_ERROR",
                    Message = string.Format(TJGeneratorsL10n.L("生成器 '{0}' 未配置 API endpoint"), generator.GeneratorId)
                };
            string url = ConfigManager.GetApiBaseUrl() + endpoint;

            // 3. 构建请求体 & 同步发送（与 GenerationPipeline / ProductionBackendTransport 同路径）
            var requestData = generator.BuildRequestData();
            try
            {
                GenerationBackendSyncSubmit.HttpResult http;
                if (requestData is MultipartRequestData multipart)
                {
                    http = GenerationBackendSyncSubmit.PostMultipart(
                        url, multipart, GenerationRequestOrigin.Agent, sessionId);
                }
                else
                {
                    string jsonData = requestData is DynamicRequestData d
                        ? d.JsonContent : JsonUtility.ToJson(requestData);
                    // Encoding.UTF8.GetBytes 不含 BOM，与 pipeline CreateTask 一致
                    byte[] postData = Encoding.UTF8.GetBytes(jsonData ?? "");
                    http = GenerationBackendSyncSubmit.PostJson(
                        url, postData, GenerationRequestOrigin.Agent, sessionId);
                }

                if (http.TimedOut)
                {
                    return new TJGeneratorsSubmitResult {
                        Success = false, ErrorCode = "NETWORK_ERROR",
                        Message = string.Format(TJGeneratorsL10n.L("网络请求失败: {0}"), http.Error)
                    };
                }

                string body = http.Body ?? "";
                if (http.IsSuccess)
                {
                    var resp = JsonUtility.FromJson<TJTaskResponse>(body);
                    if (resp != null && !string.IsNullOrEmpty(resp.taskId))
                        return new TJGeneratorsSubmitResult { Success = true, BackendTaskId = resp.taskId };
                    return new TJGeneratorsSubmitResult {
                        Success = false, ErrorCode = "INVALID_RESPONSE",
                        Message = string.Format(TJGeneratorsL10n.L("服务器响应格式异常: {0}"), body)
                    };
                }

                // responseCode==0：传输层/网络错误（非 HTTP 状态）
                if (http.ResponseCode == 0)
                {
                    return new TJGeneratorsSubmitResult {
                        Success = false, ErrorCode = "NETWORK_ERROR",
                        Message = string.Format(TJGeneratorsL10n.L("网络请求失败: {0}"),
                            string.IsNullOrEmpty(http.Error) ? body : http.Error)
                    };
                }

                switch ((int)http.ResponseCode)
                {
                    case 403:
                        return new TJGeneratorsSubmitResult { Success = false, ErrorCode = "AUTH_REQUIRED",
                            Message = TJGeneratorsL10n.L("登录权限检查失败，请确认编辑器左上角或者Hub内已登录") };
                    case 401:
                        return new TJGeneratorsSubmitResult { Success = false, ErrorCode = "AUTH_REQUIRED",
                            Message = TJGeneratorsL10n.L("认证失败，请重新登录Unity账号") };
                    case 422:
                        return new TJGeneratorsSubmitResult { Success = false, ErrorCode = "INVALID_PARAMS",
                            Message = string.Format(TJGeneratorsL10n.L("请求参数错误: {0}"), body) };
                    case 429:
                        return new TJGeneratorsSubmitResult { Success = false, ErrorCode = "RATE_LIMITED",
                            Message = TJGeneratorsL10n.L("请求频率过高，请稍后重试") };
                    default:
                        return new TJGeneratorsSubmitResult { Success = false, ErrorCode = "SERVER_ERROR",
                            Message = string.Format(TJGeneratorsL10n.L("提交失败 (HTTP {0}): {1}"),
                                (int)http.ResponseCode, body) };
                }
            }
            catch (Exception e)
            {
                return new TJGeneratorsSubmitResult {
                    Success = false, ErrorCode = "UNEXPECTED_ERROR",
                    Message = string.Format(TJGeneratorsL10n.L("提交时发生意外错误: {0}"), e.Message)
                };
            }
        }

        private static TJGeneratorsAssetReference ResolveTargetAsset(TJGeneratorsGenerationContext context, string outputType)
        {
            if (context.TargetAsset != null && context.TargetAsset.IsValid())
            {
                return context.TargetAsset;
            }

            if (!context.AutoCreateTargetPrefab)
            {
                return context.TargetAsset;
            }

            // 根据输出类型创建不同的目标资产
            switch (outputType)
            {
                case GenerationOutputTypes.Material:
                    var materialPath = CreateMaterialAsset("Assets/TJGenerators/New Material.mat");
                    return string.IsNullOrEmpty(materialPath) ? context.TargetAsset : TJGeneratorsAssetReference.FromPath(materialPath);

                case GenerationOutputTypes.Sprite:
                    var spritePath = CreateSpriteAsset("Assets/TJGenerators/New Sprite.png");
                    return string.IsNullOrEmpty(spritePath) ? context.TargetAsset : TJGeneratorsAssetReference.FromPath(spritePath);

                case GenerationOutputTypes.SpriteSequence:
                    var clipPath = CreateAnimationClipAsset("Assets/TJGenerators/New Sprite Sequence.anim");
                    return string.IsNullOrEmpty(clipPath) ? context.TargetAsset : TJGeneratorsAssetReference.FromPath(clipPath);

                default:
                    var prefabPath = CreatePrefabWithPlaceholder("Assets/TJGenerators/New Mesh.prefab");
                    return string.IsNullOrEmpty(prefabPath) ? context.TargetAsset : TJGeneratorsAssetReference.FromPath(prefabPath);
            }
        }

        private static string CreateMaterialAsset(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            path = AssetDatabase.GenerateUniqueAssetPath(path);

            var surfaceShader = TJMaterialShaderUtility.ResolveSurfaceLitShader()
                                ?? Shader.Find("Unlit/Texture");
            Material material = new Material(surfaceShader);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));

            return path;
        }

        private static string CreateSpriteAsset(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            path = AssetDatabase.GenerateUniqueAssetPath(path);

            // 创建空白纹理
            Texture2D texture = new Texture2D(2, 2);
            byte[] bytes = texture.EncodeToPNG();
            File.WriteAllBytes(path, bytes);
            UnityEngine.Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.SaveAndReimport();
            }
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));

            return path;
        }

        private static string CreateAnimationClipAsset(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            path = AssetDatabase.GenerateUniqueAssetPath(path);

            var clip = new AnimationClip();
            AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));

            return path;
        }
        
        private static string CreatePrefabWithPlaceholder(string path)
        {
            path = Path.ChangeExtension(path, ".prefab");

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            path = AssetDatabase.GenerateUniqueAssetPath(path);

            var rootGameObject = new GameObject("Generated Mesh");
            try
            {
                var placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
                placeholder.name = "Placeholder";
                placeholder.transform.SetParent(rootGameObject.transform);
                placeholder.transform.localPosition = Vector3.zero;
                placeholder.transform.localRotation = Quaternion.identity;
                placeholder.transform.localScale = Vector3.one;

                PrefabUtility.SaveAsPrefabAsset(rootGameObject, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rootGameObject);
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));

            return path;
        }

        private sealed class HeadlessGenerationHost : HeadlessPipelineHostBase, IMediaAssetPipelineHost
        {
            private readonly TJGeneratorsAssetReference _targetAsset;

            public HeadlessGenerationHost(TJGeneratorsAssetReference targetAsset)
            {
                _targetAsset = targetAsset;
            }

            protected override string DialogLogTag => "TJGenerators";

            public override TJGeneratorsAssetReference GetTargetAsset()
            {
                return _targetAsset;
            }

            public string GetAssetSavePath(PipelineMediaType _type, ModelGeneratorBase generator)
            {
                if (_type != PipelineMediaType.Audio) return null;

                // Create unique path in History folder for audio files
                if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                    UnityEditor.AssetDatabase.CreateFolder("Assets", "TJGenerators");
                if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                    UnityEditor.AssetDatabase.CreateFolder("Assets/TJGenerators", "History");
                string ext =
                    (generator is DynamicGenerator dg)
                        ? "." + TJGeneratorsAudioAssetPathUtility.NormalizeImportedAudioFileExtension(dg.AudioFormat)
                        : ".wav";
                string uniqueName = "Music_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ext;
                return TJGenerators.Utils.TJGeneratorsAudioAssetPathUtility.GenerateUniqueAudioPath(
                    "Assets/TJGenerators/History/" + uniqueName);
            }

            public void OnAssetSaved(PipelineMediaType _type, string savePath, ModelGeneratorBase generator) { }
        }
    }
}
#endif
