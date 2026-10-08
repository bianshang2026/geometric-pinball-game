using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEditor;
using Unity.UniAsset.Manager.Editor.InternalBridge;
using TJGenerators.Utils;
using Unity.EditorCoroutines.Editor;

namespace TJGenerators.Config
{
    /// <summary>
    /// 统一配置入口。3D 与天空盒共用同一份 GeneratorConfig.json，仅通过 ConfigType 区分“当前窗口用哪份生成器列表”。
    /// </summary>
    public static class ConfigManager
    {
        private const string DefaultConfigFileName = "GeneratorConfig";
        private const string ConfigEndpoint = "config/generators";

        private static RemoteConfig _config;
        private static bool _isLoading;
        private static readonly List<(ConfigType type, Action<RemoteConfig> callback)> _pendingCallbacks = new List<(ConfigType type, Action<RemoteConfig> callback)>();

        /// <summary>
        /// 配置更新时触发，参数为 (ConfigType, 原始配置)。取生成器列表请用 GetGenerators(type)。
        /// </summary>
        public static event Action<ConfigType, RemoteConfig> OnConfigUpdated;

        private static RemoteConfig GetRawConfig()
        {
            if (_config != null) return _config;
            var loaded = LoadDefaultConfig();
            if (HasValidGenerators(loaded))
                _config = loaded;
            return loaded;
        }

        private static bool HasValidGenerators(RemoteConfig config)
        {
            if (config == null) return false;
            return (config.generators != null && config.generators.Count > 0)
                   || (config.skyboxGenerators != null && config.skyboxGenerators.Count > 0)
                   || (config.spriteGenerators != null && config.spriteGenerators.Count > 0)
                   || (config.spriteSequenceGenerators != null && config.spriteSequenceGenerators.Count > 0)
                   || (config.materialGenerators != null && config.materialGenerators.Count > 0)
                   || (config.musicGenerators != null && config.musicGenerators.Count > 0)
                   || (config.imageGenerators != null && config.imageGenerators.Count > 0)
                   || (config.referenceImageGenerators != null && config.referenceImageGenerators.Count > 0)
                   || (config.videoGenerators != null && config.videoGenerators.Count > 0);
        }

        /// <summary>
        /// 从原始配置中按 type 取对应列表：Generator -> generators，Skybox -> skyboxGenerators，Sprite -> spriteGenerators，Material -> materialGenerators。
        /// </summary>
        private static List<GeneratorConfig> GetListForType(RemoteConfig raw, ConfigType type)
        {
            if (raw == null) return null;
            switch (type)
            {
                case ConfigType.Skybox:          return raw.skyboxGenerators ?? raw.generators;
                case ConfigType.Sprite:          return raw.spriteGenerators ?? raw.generators;
                case ConfigType.SpriteSequence:  return raw.spriteSequenceGenerators ?? raw.generators;
                case ConfigType.Material:        return raw.materialGenerators ?? raw.generators;
                case ConfigType.Music:           return raw.musicGenerators ?? raw.generators;
                case ConfigType.Image:           return raw.imageGenerators ?? raw.generators;
                case ConfigType.ReferenceImage:  return null;
                case ConfigType.Video:           return raw.videoGenerators ?? raw.generators;
                case ConfigType.World:           return raw.worldGenerators ?? raw.generators;
                default:                         return raw.generators;
            }
        }

        /// <summary>
        /// 根据 type 直接从配置取启用的生成器列表：Generator -> generators，Skybox -> skyboxGenerators。
        /// </summary>
        public static List<GeneratorConfig> GetGenerators(ConfigType type)
        {
            var raw = GetRawConfig();
            var list = GetListForType(raw, type);
            if (list == null && (type == ConfigType.Image || type == ConfigType.ReferenceImage || type == ConfigType.Video))
                return new List<GeneratorConfig>();
            var enabled = list?.FindAll(g => g.enabled);
            if (enabled != null && enabled.Count > 0) return enabled;
            list = GetListForType(LoadDefaultConfig(), type);
            return list?.FindAll(g => g.enabled) ?? new List<GeneratorConfig>();
        }

        /// <summary>
        /// 获取指定生成器的配置（按类型取对应列表）
        /// </summary>
        public static GeneratorConfig GetGeneratorConfig(ConfigType type, string generatorId)
            => GetListForType(GetRawConfig(), type)?.Find(g => g.id == generatorId);

        /// <summary>
        /// 从包内 Editor/Config/GeneratorConfig.json 读取生成器配置（不受 Library 缓存影响）。
        /// </summary>
        public static GeneratorConfig GetPackageGeneratorConfig(ConfigType type, string generatorId)
        {
            if (string.IsNullOrEmpty(generatorId))
                return null;
            return GetListForType(LoadDefaultConfig(), type)?.Find(g => g.id == generatorId);
        }

        /// <summary>
        /// 解析参考图上限：优先运行时/缓存配置，未配置时回退包内默认，最终默认 1。
        /// </summary>
        public static int ResolveMaxReferenceImages(ConfigType type, GeneratorConfig runtimeConfig)
        {
            string generatorId = runtimeConfig?.id;
            int runtimeMax = runtimeConfig?.uiLayout?.maxReferenceImages ?? 0;
            if (runtimeMax > 0)
                return runtimeMax;

            if (!string.IsNullOrEmpty(generatorId))
            {
                var packageConfig = GetPackageGeneratorConfig(type, generatorId);
                int packageMax = packageConfig?.uiLayout?.maxReferenceImages ?? 0;
                if (packageMax > 0)
                    return packageMax;
            }

            return 1;
        }

        /// <summary>
        /// 解析参考图上限（按 generatorId 在各类型列表中查找包内配置）。
        /// </summary>
        public static int ResolveMaxReferenceImages(GeneratorConfig runtimeConfig)
        {
            int runtimeMax = runtimeConfig?.uiLayout?.maxReferenceImages ?? 0;
            if (runtimeMax > 0)
                return runtimeMax;

            string generatorId = runtimeConfig?.id;
            if (!string.IsNullOrEmpty(generatorId))
            {
                var package = LoadDefaultConfig();
                foreach (ConfigType type in Enum.GetValues(typeof(ConfigType)))
                {
                    var packageConfig = GetListForType(package, type)?.Find(g => g.id == generatorId);
                    int packageMax = packageConfig?.uiLayout?.maxReferenceImages ?? 0;
                    if (packageMax > 0)
                        return packageMax;
                }
            }

            return 1;
        }


        /// <summary>
        /// 获取所有启用的参考图生成器配置（用于 AIReferenceImageWindow）
        /// </summary>
        public static List<ImageGeneratorConfig> GetReferenceImageGenerators()
        {
            var config = GetRawConfig();
            var generators = config?.referenceImageGenerators?.FindAll(g => g.enabled);
            if (generators != null && generators.Count > 0) return generators;
            var defaultConfig = LoadDefaultConfig();
            return defaultConfig?.referenceImageGenerators?.FindAll(g => g.enabled) ?? new List<ImageGeneratorConfig>();
        }

        /// <summary>
        /// 获取指定参考图生成器的配置
        /// </summary>
        public static ImageGeneratorConfig GetReferenceImageGeneratorConfig(string id)
            => GetRawConfig()?.referenceImageGenerators?.Find(g => g.id == id);


        // ---------- 以下为 globalEndpoints / pollConfig 等公用值，与 ConfigType 无关 ----------

        public static string GetApiBaseUrl()
        {
            var config = GetRawConfig();
#if TJGENERATORS_LOCAL_BACKEND
            if (!string.IsNullOrEmpty(config?.debugApiBaseUrl))
                return config.debugApiBaseUrl;
#endif
            return config?.apiBaseUrl ?? "https://ai-generator.tuanjie.cn/api/editor/";
        }

        /// <summary>
        /// 获取 Codely 资产搜索后端根地址（不含路径）。
        /// 用于资产库搜索/下载与换票接口 auth/exchange-with-unity-token。
        /// </summary>
        public static string GetCodelyBaseUrl()
        {
            return GetRawConfig()?.codelyBaseUrl ?? "https://codely.tuanjie.cn";
        }

        /// <summary>
        /// 获取配置中显式指定的默认模型 ID（如果有）。目前为顶层字段，不按 ConfigType 区分。
        /// </summary>
        public static string GetDefaultModelId(ConfigType type)
        {
            return GetRawConfig()?.defaultModelId;
        }

        public static string GetUserInfoUrl()
        {
            var config = GetRawConfig();
            string endpoint = config?.globalEndpoints?.userInfo ?? "user/me";
            return GetApiBaseUrl() + endpoint;
        }

        public static string GetPollStatusUrl(string taskId)
        {
            var config = GetRawConfig();
            string pattern = config?.globalEndpoints?.pollStatus ?? "task/{taskId}/id-status";
            return GetApiBaseUrl() + pattern.Replace("{taskId}", taskId);
        }

        public static float GetPollInterval()
            => GetRawConfig()?.pollConfig?.intervalSeconds ?? 8f;

        public static int GetPollMaxRetries()
            => GetRawConfig()?.pollConfig?.maxRetries ?? 360;

        public static float GetRequestTimeout()
            => GetRawConfig()?.pollConfig?.requestTimeoutSeconds ?? 30f;

        public static float GetDownloadTimeout()
            => GetRawConfig()?.pollConfig?.downloadTimeoutSeconds ?? 300f;

        public static float GetApiTimeout()
            => GetRawConfig()?.pollConfig?.apiTimeoutSeconds ?? 60f;

        public static string GetRequestSource()
            => GetRawConfig()?.requestHeaders?.source ?? "codely";

        /// <summary>
        /// 获取指定端点的完整 URL（按类型在对应生成器列表中查找）
        /// </summary>
        public static string GetEndpointUrl(ConfigType type, string generatorId, string endpointKey, string fallback)
        {
            var genConfig = GetGeneratorConfig(type, generatorId);
            string endpoint = genConfig?.GetEndpoint(endpointKey) ?? fallback;
            return GetApiBaseUrl() + endpoint;
        }

        /// <summary>
        /// 异步从服务端刷新配置（统一拉取 config/generators，合并到同一份配置后通知各视图）
        /// </summary>
        public static void RefreshConfigAsync(ConfigType type, Action<RemoteConfig> callback = null)
        {
            if (callback != null)
                _pendingCallbacks.Add((type, callback));
            if (_isLoading) return;
            _isLoading = true;
            EditorCoroutineUtility.StartCoroutineOwnerless(FetchConfigCoroutine());
        }

        private static IEnumerator FetchConfigCoroutine()
        {
            string apiBaseUrl = GetRawConfig()?.apiBaseUrl ?? "https://ai-generator.tuanjie.cn/api/editor/";
            string url = apiBaseUrl + ConfigEndpoint;

            using (var request = UnityWebRequest.Get(url))
            {
                request.SetRequestHeader("source", "codely");
                string token = UnityConnectSession.instance.GetAccessToken();
                if (!string.IsNullOrEmpty(token))
                    request.SetRequestHeader("Authorization", $"Bearer {token}");
                request.SetRequestHeader("orgId", UnityConnectSession.instance.GetOrgId());

                yield return request.SendWebRequest();

                if (UnityWebRequestCompat.IsSuccess(request))
                {
                    try
                    {
                        string responseText = request.downloadHandler.text;
                        if (!string.IsNullOrEmpty(responseText) && responseText.TrimStart().StartsWith("{"))
                        {
                            var newConfig = JsonUtility.FromJson<RemoteConfig>(responseText);
                            if (newConfig != null && HasValidGenerators(newConfig))
                            {
                                _config = newConfig;
                                OnConfigUpdated?.Invoke(ConfigType.Generator, GetRawConfig());
                                OnConfigUpdated?.Invoke(ConfigType.Skybox, GetRawConfig());
                                OnConfigUpdated?.Invoke(ConfigType.Sprite, GetRawConfig());
                                OnConfigUpdated?.Invoke(ConfigType.Material, GetRawConfig());
                                OnConfigUpdated?.Invoke(ConfigType.Music, GetRawConfig());
                                OnConfigUpdated?.Invoke(ConfigType.Image, GetRawConfig());
                                OnConfigUpdated?.Invoke(ConfigType.ReferenceImage, GetRawConfig());
                                OnConfigUpdated?.Invoke(ConfigType.Video, GetRawConfig());
                                OnConfigUpdated?.Invoke(ConfigType.World, GetRawConfig());
                                TJLog.Log($"从服务端加载配置成功");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        TJLog.Log($"服务端配置解析失败，使用本地配置: {e.Message}");
                    }
                }
            }

            _isLoading = false;
            var raw = GetRawConfig();
            foreach (var (t, cb) in _pendingCallbacks)
                cb?.Invoke(raw);
            _pendingCallbacks.Clear();
        }

        private static string[] GetPossiblePaths()
        {
            return new[]
            {
                $"Packages/cn.tuanjie.ai.generators/Editor/Config/{DefaultConfigFileName}.json",
                $"Assets/tuanjie-tripo/Editor/Config/{DefaultConfigFileName}.json",
                $"Assets/Editor/Config/{DefaultConfigFileName}.json"
            };
        }

        private static RemoteConfig LoadDefaultConfig()
        {
            // Domain reload 后 OnEnable 时 AssetDatabase 可能尚未就绪。
            // 先走文件系统直读，避免落到空 fallback 导致窗口模型变成「未选择」。
            var fileConfig = TryLoadConfigFromFileSystem();
            if (fileConfig != null)
                return fileConfig;

            string[] possiblePaths = GetPossiblePaths();
            foreach (string path in possiblePaths)
            {
                try
                {
                    var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                    if (textAsset != null)
                    {
                        var config = JsonUtility.FromJson<RemoteConfig>(textAsset.text);
                        if (config != null && HasValidGenerators(config))
                        {
                            TJLog.Log("从包内配置文件加载成功: " + path);
                            return config;
                        }
                    }
                }
                catch (Exception e) { TJLog.LogWarning("尝试加载 " + path + " 失败: " + e.Message); }
            }

            string[] guids = AssetDatabase.FindAssets(DefaultConfigFileName);
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (assetPath != null && assetPath.EndsWith(".json"))
                {
                    try
                    {
                        var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
                        if (textAsset != null)
                        {
                            var config = JsonUtility.FromJson<RemoteConfig>(textAsset.text);
                            if (config != null && HasValidGenerators(config))
                            {
                                TJLog.Log("从配置文件加载成功: " + assetPath);
                                return config;
                            }
                        }
                    }
                    catch (Exception e) { TJLog.LogWarning("加载 " + assetPath + " 失败: " + e.Message); }
                }
            }

            return LoadFallbackConfig();
        }

        /// <summary>
        /// 不依赖 AssetDatabase，从磁盘读取 GeneratorConfig.json（兼容 domain reload 早期时机）。
        /// </summary>
        private static RemoteConfig TryLoadConfigFromFileSystem()
        {
            foreach (string fullPath in EnumerateFilesystemConfigPaths())
            {
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
                    continue;
                try
                {
                    var config = JsonUtility.FromJson<RemoteConfig>(File.ReadAllText(fullPath));
                    if (config != null && HasValidGenerators(config))
                    {
                        TJLog.Log("从本地文件加载成功: " + fullPath);
                        return config;
                    }
                }
                catch (Exception e)
                {
                    TJLog.LogWarning("读取本地文件失败: " + fullPath + ", " + e.Message);
                }
            }
            return null;
        }

        private static List<string> EnumerateFilesystemConfigPaths()
        {
            var paths = new List<string>();
            string relativeConfig = Path.Combine("Editor", "Config", DefaultConfigFileName + ".json");

            try
            {
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ConfigManager).Assembly);
                if (packageInfo != null && !string.IsNullOrEmpty(packageInfo.resolvedPath))
                    paths.Add(Path.Combine(packageInfo.resolvedPath, relativeConfig));
            }
            catch (Exception e)
            {
                TJLog.LogWarning("解析包路径失败: " + e.Message);
            }

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrEmpty(projectRoot))
                return paths;

            foreach (string assetPath in GetPossiblePaths())
                paths.Add(Path.GetFullPath(Path.Combine(projectRoot, assetPath)));

            return paths;
        }

        private static RemoteConfig LoadFallbackConfig()
        {
            TJLog.LogWarning("使用硬编码的最小配置");
            return new RemoteConfig
            {
                version = "1.0",
                apiBaseUrl = "https://ai-generator.tuanjie.cn/api/editor/",
                codelyBaseUrl = "https://codely.tuanjie.cn",
                pollConfig = new PollConfig
                {
                    maxRetries = 360,
                    intervalSeconds = 8f,
                    requestTimeoutSeconds = 30f,
                    downloadTimeoutSeconds = 300f,
                    apiTimeoutSeconds = 60f
                },
                globalEndpoints = new GlobalEndpointsConfig { userInfo = "user/me", pollStatus = "task/{taskId}/id-status" },
                requestHeaders = new RequestHeadersConfig { source = "codely" },
                generators = new List<GeneratorConfig>(),
                skyboxGenerators = new List<GeneratorConfig>(),
                spriteGenerators = new List<GeneratorConfig>(),
                spriteSequenceGenerators = new List<GeneratorConfig>(),
                materialGenerators = new List<GeneratorConfig>(),
                musicGenerators = new List<GeneratorConfig>(),
                imageGenerators = new List<GeneratorConfig>(),
                referenceImageGenerators = new List<ImageGeneratorConfig>(),
                videoGenerators = new List<GeneratorConfig>(),
                worldGenerators = new List<GeneratorConfig>()
            };
        }

        /// <summary>
        /// 获取 Sprite 窗口共享的类型选择器配置
        /// </summary>
        public static TypeSelectorConfig GetSpriteTypeSelector()
            => GetRawConfig()?.spriteTypeSelector;

        /// <summary>
        /// 获取 Sprite 窗口共享的风格选择器配置
        /// </summary>
        public static StyleSelectorConfig GetSpriteStyleSelector()
            => GetRawConfig()?.spriteStyleSelector;

        /// <summary>
        /// 清除配置缓存
        /// </summary>
        public static void ClearCache()
        {
            _config = null;
        }
    }
}
