#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using TJGenerators.Config;
using TJGenerators.UI;
using TJGenerators.Utils;
using Unity.UniAsset.Manager.Editor.InternalBridge;
using Unity.EditorCoroutines.Editor;

namespace TJGenerators
{
    /// <summary>
    /// 纹理走势模板生成器 - 使用 seedream API 生成纹理走势模板图
    /// </summary>
    public class TJGeneratorsMaterialTemplateGenerator : EditorWindow
    {
        private const string TemplateDirectory = "Packages/cn.tuanjie.ai.generators/Editor/EditorTextures/TexturePatterns";
        private const int TemplateSize = 2048;  // 2048x2048 = 4194304 pixels (API requires at least 3686400 pixels)
        private const int MaxApiRetries = 5;  // API 调用最大重试次数
        private const int InitialRetryDelaySeconds = 10;  // 初始重试延迟（秒）
        private const int TemplateIntervalSeconds = 20;  // 每个模板之间的间隔（秒）
        private const int PollMaxRetries = 60;  // 轮询最大次数
        private const int PollIntervalSeconds = 3;  // 轮询间隔（秒）

        private List<MaterialTemplateOptionConfig> templates;
        private Dictionary<string, bool> templateStatus = new Dictionary<string, bool>();
        private Dictionary<string, string> templateErrors = new Dictionary<string, string>();
        private bool isGenerating = false;
        private int currentTemplateIndex = -1;
        private string currentStatus = "";
        private Vector2 scrollPosition;

        // 当前生成结果
        private string currentGenerateError = null;

#if TJGENERATORS_DEBUG
        /// <summary>由 <see cref="TJGeneratorsMenuItems.OpenMaterialTemplateGeneratorWindow"/> 或外部代码调用。</summary>
        public static void ShowWindow()
        {
            var window = GetWindow<TJGeneratorsMaterialTemplateGenerator>(TJGeneratorsL10n.L("TJGenerators 材质模板生成"));
            window.minSize = new Vector2(500, 600);
            window.Show();
        }
#endif

        private void OnEnable()
        {
            LoadTemplates();
        }

        private void LoadTemplates()
        {
            templates = new List<MaterialTemplateOptionConfig>();
            templateStatus.Clear();
            templateErrors.Clear();

            // 从配置中加载纹理走势模板
            var config = ConfigManager.GetGeneratorConfig(ConfigType.Material, "huoshan_seedream_material");
            if (config?.texturePatternSelector?.options != null)
            {
                foreach (var option in config.texturePatternSelector.options)
                {
                    templates.Add(option);
                    templateStatus[option.id] = File.Exists(GetAbsoluteTemplatePath(option.id));
                }
            }
        }

        private void OnGUI()
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), CommonStyles.WindowBackgroundColor);
            GUILayout.Space(10);
            GUILayout.Label(TJGeneratorsL10n.L("纹理走势模板生成器"), EditorStyles.boldLabel);
            GUILayout.Label(string.Format(TJGeneratorsL10n.L("模板目录: {0}"), TemplateDirectory), EditorStyles.miniLabel);
            GUILayout.Space(10);

            // 统计信息
            int generated = 0;
            foreach (var kvp in templateStatus)
                if (kvp.Value) generated++;

            GUILayout.Label(string.Format(TJGeneratorsL10n.L("已生成: {0} / {1}"), generated, templates.Count), EditorStyles.helpBox);
            GUILayout.Space(10);

            // 提示信息
            if (!isGenerating)
            {
                EditorGUILayout.HelpBox(TJGeneratorsL10n.L("注意：生成过程会顺序执行，每个模板之间间隔20秒以避免请求过于频繁。请耐心等待。"), MessageType.Info);
                GUILayout.Space(5);
            }

            bool playBlocked = TJGeneratorsPlayModeGuard.IsActive;
            if (playBlocked)
            {
                EditorGUILayout.HelpBox(TJGeneratorsPlayModeGuard.ShortHint, MessageType.Warning);
                GUILayout.Space(5);
            }

            // 生成按钮
            EditorGUI.BeginDisabledGroup(isGenerating || playBlocked);
            if (GUILayout.Button(TJGeneratorsL10n.L("生成所有缺失的模板"), GUILayout.Height(30)))
            {
                StartGenerateAll();
            }
            if (GUILayout.Button(TJGeneratorsL10n.L("重新生成所有模板"), GUILayout.Height(30)))
            {
                if (EditorUtility.DisplayDialog(TJGeneratorsL10n.L("确认"), TJGeneratorsL10n.L("确定要重新生成所有模板吗？这将覆盖已有的模板图。\n\n生成过程会顺序执行，请耐心等待。"), TJGeneratorsL10n.L("确定"), TJGeneratorsL10n.L("取消")))
                {
                    StartGenerateAll(true);
                }
            }
            EditorGUI.EndDisabledGroup();

            // 状态显示
            if (isGenerating)
            {
                GUILayout.Space(10);
                GUILayout.Label(string.Format(TJGeneratorsL10n.L("当前状态: {0}"), currentStatus), EditorStyles.helpBox);
                if (currentTemplateIndex >= 0 && currentTemplateIndex < templates.Count)
                {
                    var current = templates[currentTemplateIndex];
                    GUILayout.Label(string.Format(TJGeneratorsL10n.L("正在生成: {0}"), current.name), EditorStyles.boldLabel);
                }
            }

            GUILayout.Space(10);

            // 模板列表
            GUILayout.Label(TJGeneratorsL10n.L("模板列表:"), EditorStyles.boldLabel);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);
            foreach (var template in templates)
            {
                DrawTemplateItem(template);
            }
            GUILayout.EndScrollView();
        }

        private void DrawTemplateItem(MaterialTemplateOptionConfig template)
        {
            GUILayout.BeginHorizontal(EditorStyles.helpBox);
            
            // 状态图标
            bool exists = templateStatus.ContainsKey(template.id) && templateStatus[template.id];
            GUIContent statusIcon = exists ? EditorGUIUtility.IconContent("d_Toggle Icon") : EditorGUIUtility.IconContent("d_ToggleOff Icon");
            GUILayout.Label(statusIcon, GUILayout.Width(20));

            // 模板信息
            GUILayout.BeginVertical();
            GUILayout.Label($"{TJGeneratorsL10n.L(template.name)} ({TJGeneratorsL10n.L(template.category)})", EditorStyles.boldLabel);
            GUILayout.Label(TJGeneratorsL10n.L(template.description), EditorStyles.miniLabel);
            if (templateErrors.ContainsKey(template.id))
            {
                GUILayout.Label(string.Format(TJGeneratorsL10n.L("错误: {0}"), templateErrors[template.id]), CommonStyles.MiniRedLabelStyle);
            }
            GUILayout.EndVertical();

            // 单独生成按钮
            EditorGUI.BeginDisabledGroup(isGenerating || TJGeneratorsPlayModeGuard.IsActive);
            if (GUILayout.Button(exists ? TJGeneratorsL10n.L("重新生成") : TJGeneratorsL10n.L("生成"), GUILayout.Width(80)))
            {
                StartGenerateSingle(template);
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.EndHorizontal();
            GUILayout.Space(2);
        }

        private void StartGenerateAll(bool regenerateAll = false)
        {
            List<MaterialTemplateOptionConfig> toGenerate = new List<MaterialTemplateOptionConfig>();
            foreach (var template in templates)
            {
                if (regenerateAll || !templateStatus.ContainsKey(template.id) || !templateStatus[template.id])
                {
                    toGenerate.Add(template);
                }
            }

            if (toGenerate.Count == 0)
            {
                Debug.LogWarning("[MaterialTemplate] 所有模板已生成，无需重新生成。");
                return;
            }

            isGenerating = true;
            EditorCoroutineUtility.StartCoroutineOwnerless(GenerateTemplatesSequentially(toGenerate));
        }

        private void StartGenerateSingle(MaterialTemplateOptionConfig template)
        {
            isGenerating = true;
            EditorCoroutineUtility.StartCoroutineOwnerless(GenerateTemplatesSequentially(new List<MaterialTemplateOptionConfig> { template }));
        }

        /// <summary>
        /// 顺序生成模板（一个一个生成，避免并发问题）
        /// </summary>
        private IEnumerator GenerateTemplatesSequentially(List<MaterialTemplateOptionConfig> templatesToGenerate)
        {
            TJLog.Log($"[MaterialTemplate] ========== 开始顺序生成 {templatesToGenerate.Count} 个模板 ==========");

            int successCount = 0;
            int failCount = 0;

            for (int i = 0; i < templatesToGenerate.Count; i++)
            {
                currentTemplateIndex = templates.IndexOf(templatesToGenerate[i]);
                var template = templatesToGenerate[i];
                currentStatus = string.Format(TJGeneratorsL10n.L("生成中 ({0}/{1}) - {2}"), i + 1, templatesToGenerate.Count, template.name);

                TJLog.Log($"[MaterialTemplate] ========== [{i + 1}/{templatesToGenerate.Count}] 开始生成: {template.name} ({template.id}) ==========");

                // 重置结果
                currentGenerateError = null;

                yield return GenerateSingleTemplate(template);

                // 更新状态
                templateStatus[template.id] = File.Exists(GetAbsoluteTemplatePath(template.id));
                
                if (templateStatus[template.id])
                {
                    successCount++;
                    TJLog.Log($"[MaterialTemplate] [{i + 1}/{templatesToGenerate.Count}] ✓ 成功: {template.name}");
                }
                else
                {
                    failCount++;
                    string error = currentGenerateError
                        ?? (templateErrors.TryGetValue(template.id, out string templateError) ? templateError : TJGeneratorsL10n.L("未知错误"));
                    TJLog.LogWarning($"[MaterialTemplate] [{i + 1}/{templatesToGenerate.Count}] ✗ 失败: {template.name} - {error}");
                }

                Repaint();

                // 每个模板之间等待一段时间，避免请求过快
                if (i < templatesToGenerate.Count - 1)
                {
                    currentStatus = string.Format(TJGeneratorsL10n.L("等待 {0} 秒后继续..."), TemplateIntervalSeconds);
                    Repaint();
                    TJLog.Log($"[MaterialTemplate] 等待 {TemplateIntervalSeconds} 秒后继续下一个模板...");
                    yield return new EditorWaitForSeconds(TemplateIntervalSeconds);
                }
            }

            isGenerating = false;
            currentTemplateIndex = -1;
            currentStatus = TJGeneratorsL10n.L("完成");
            Repaint();

            Debug.Log(
                $"[MaterialTemplate] ========== 生成完成: 已生成 {successCount} 个材质模板，失败 {failCount} 个 =========="
            );
        }

        private IEnumerator GenerateSingleTemplate(MaterialTemplateOptionConfig template)
        {
            if (TJGeneratorsPlayModeGuard.IsActive)
            {
                currentGenerateError = TJGeneratorsPlayModeGuard.Message;
                templateErrors[template.id] = currentGenerateError;
                TJLog.LogError($"[MaterialTemplate] {template.id}: {TJGeneratorsPlayModeGuard.Message}");
                yield break;
            }

            string prompt = template.prompt;
            if (string.IsNullOrEmpty(prompt))
            {
                currentGenerateError = TJGeneratorsL10n.L("提示词为空");
                templateErrors[template.id] = currentGenerateError;
                TJLog.LogError($"[MaterialTemplate] {template.id}: 提示词为空");
                yield break;
            }

            // 获取认证令牌
            string authToken = UnityConnectSession.instance.GetAccessToken();
            if (string.IsNullOrEmpty(authToken))
            {
                currentGenerateError = TJGeneratorsL10n.L("无法获取认证令牌，请确保已登录 Unity");
                templateErrors[template.id] = currentGenerateError;
                TJLog.LogError($"[MaterialTemplate] {template.id}: 无法获取认证令牌");
                yield break;
            }

            // 构建请求（材质模板不需要抠图）
            var requestData = new MaterialTemplateRequest
            {
                prompt = prompt,
                size = $"{TemplateSize}x{TemplateSize}",
                responseFormat = "url",
                stream = false,
                watermark = false,
                sequentialImageGeneration = "",
                isSegmentation = false
            };

            string json = JsonUtility.ToJson(requestData);
            string url = ConfigManager.GetApiBaseUrl() + "task/huoshan-seedream-45";

            // 发送请求（带重试，指数退避）
            string taskId = null;
            string imageUrl = null;  // 同步模式下直接返回的图片URL
            bool apiSuccess = false;
            int lastResponseCode = 0;
            string lastError = "";

            for (int retry = 0; retry < MaxApiRetries; retry++)
            {
                // 计算等待时间（指数退避：10s, 20s, 40s, 80s, 160s）
                int waitTime = InitialRetryDelaySeconds * (int)Math.Pow(2, retry);
                
                if (retry > 0)
                {
                    TJLog.Log($"[MaterialTemplate] {template.id}: 第 {retry + 1} 次重试创建任务，等待 {waitTime} 秒...");
                    currentStatus = string.Format(TJGeneratorsL10n.L("重试中 ({0}/{1})，等待 {2} 秒..."), retry + 1, MaxApiRetries, waitTime);
                    Repaint();
                    yield return new EditorWaitForSeconds(waitTime);
                }

                bool needRetry = false;

                using (var request = new UnityWebRequest(url, "POST"))
                {
                    byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
                    request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("Authorization", $"Bearer {authToken}");
                    request.SetRequestHeader("orgId", UnityConnectSession.instance.GetOrgId());
                    request.SetRequestHeader("source", "codely");
                    request.SetRequestHeader(GenerationRequestOrigin.HeaderName, GenerationRequestOrigin.Ui);
                    var pkgVer = GenerationRequestOrigin.GetPackageVersion();
                    if (!string.IsNullOrEmpty(pkgVer))
                        request.SetRequestHeader(GenerationRequestOrigin.PackageVersionHeaderName, pkgVer);

                    TJLog.Log($"[MaterialTemplate] {template.id}: 发送创建任务请求 (尝试 {retry + 1}/{MaxApiRetries})");
                    yield return request.SendWebRequest();

                    lastResponseCode = (int)request.responseCode;

                    if (UnityWebRequestCompat.IsSuccess(request))
                    {
                        string responseText = request.downloadHandler.text;
                        TJLog.Log($"[MaterialTemplate] {template.id}: 创建任务响应: {responseText}");

                        try
                        {
                            // 尝试解析为同步响应（直接返回结果）
                            var syncResponse = JsonUtility.FromJson<MaterialTemplateSyncResponse>(responseText);
                            
                            // 检查是否是同步模式（status=completed 且有 image_urls）
                            if (syncResponse != null && syncResponse.status == "completed" && 
                                syncResponse.output?.data?.image_urls != null && 
                                syncResponse.output.data.image_urls.Length > 0)
                            {
                                // 同步模式：直接获取图片URL
                                imageUrl = syncResponse.output.data.image_urls[0];
                                apiSuccess = true;
                                TJLog.Log($"[MaterialTemplate] {template.id}: 同步模式，任务已完成, imageUrl={imageUrl}");
                            }
                            // 检查是否有 taskId（异步模式）
                            else if (syncResponse != null && !string.IsNullOrEmpty(syncResponse.taskId))
                            {
                                taskId = syncResponse.taskId;
                                apiSuccess = true;
                                TJLog.Log($"[MaterialTemplate] {template.id}: 异步模式，任务创建成功, taskId={taskId}");
                            }
                            // 尝试解析为异步响应（返回 task_id）
                            else
                            {
                                var asyncResponse = JsonUtility.FromJson<MaterialTemplateTaskResponse>(responseText);
                                if (asyncResponse != null && !string.IsNullOrEmpty(asyncResponse.task_id))
                                {
                                    taskId = asyncResponse.task_id;
                                    apiSuccess = true;
                                    TJLog.Log($"[MaterialTemplate] {template.id}: 异步模式(嵌套)，任务创建成功, taskId={taskId}");
                                }
                                else
                                {
                                    lastError = TJGeneratorsL10n.L("响应中无 task_id 且非同步完成状态");
                                    TJLog.LogWarning($"[MaterialTemplate] {template.id}: 响应中无 task_id 且非同步完成状态");
                                    needRetry = true;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            lastError = string.Format(TJGeneratorsL10n.L("解析响应失败: {0}"), e.Message);
                            TJLog.LogError($"[MaterialTemplate] {template.id}: 解析响应失败: {e.Message}");
                            needRetry = true;
                        }
                    }
                    else
                    {
                        string responseText = request.downloadHandler?.text ?? "";
                        lastError = request.error ?? TJGeneratorsL10n.L("未知错误");

                        if (lastResponseCode == 429)
                        {
                            lastError = TJGeneratorsL10n.L("请求过于频繁 (429)");
                            TJLog.LogWarning($"[MaterialTemplate] {template.id}: 429 Too Many Requests - 将自动重试");
                            needRetry = true;
                        }
                        else if (lastResponseCode == 500)
                        {
                            lastError = string.Format(TJGeneratorsL10n.L("服务器错误 (500): {0}"), responseText);
                            TJLog.LogError($"[MaterialTemplate] {template.id}: 500 Server Error - {responseText}");
                            needRetry = true;
                        }
                        else
                        {
                            lastError = string.Format(TJGeneratorsL10n.L("请求失败 ({0}): {1}"), lastResponseCode, lastError);
                            TJLog.LogError($"[MaterialTemplate] {template.id}: 请求失败 ({lastResponseCode}): {lastError}");
                            needRetry = true;
                        }
                    }
                }

                if (apiSuccess) break;
                
                if (!needRetry) break;
            }

            if (!apiSuccess)
            {
                currentGenerateError = lastError;
                templateErrors[template.id] = currentGenerateError;
                yield break;
            }

            // 如果是同步模式，直接下载图片
            if (!string.IsNullOrEmpty(imageUrl))
            {
                yield return DownloadAndSaveImage(imageUrl, template.id);
                yield break;
            }

            // 异步模式：轮询任务状态
            if (string.IsNullOrEmpty(taskId))
            {
                currentGenerateError = TJGeneratorsL10n.L("无法获取任务ID");
                templateErrors[template.id] = currentGenerateError;
                yield break;
            }

            // 轮询任务状态
            int pollRetry = 0;

            while (pollRetry < PollMaxRetries)
            {
                yield return new EditorWaitForSeconds(PollIntervalSeconds);

                using (var pollRequest = new UnityWebRequest(ConfigManager.GetApiBaseUrl() + $"task/{taskId}/id-status"))
                {
                    pollRequest.downloadHandler = new DownloadHandlerBuffer();
                    pollRequest.SetRequestHeader("Authorization", $"Bearer {authToken}");
                    pollRequest.SetRequestHeader("source", "codely");
                    pollRequest.SetRequestHeader(GenerationRequestOrigin.HeaderName, GenerationRequestOrigin.Ui);
                    var pkgVer = GenerationRequestOrigin.GetPackageVersion();
                    if (!string.IsNullOrEmpty(pkgVer))
                        pollRequest.SetRequestHeader(GenerationRequestOrigin.PackageVersionHeaderName, pkgVer);

                    yield return pollRequest.SendWebRequest();

                    if (UnityWebRequestCompat.IsSuccess(pollRequest))
                    {
                        try
                        {
                            var response = JsonUtility.FromJson<TJTaskStatusResponse>(pollRequest.downloadHandler.text);
                            if (response != null)
                            {
                                string status = response.status;
                                int progress = response.progress;

                                if (pollRetry % 5 == 0)  // 每 15 秒打印一次日志
                                {
                                    TJLog.Log($"[MaterialTemplate] {template.id}: 轮询状态={status}, 进度={progress}%");
                                }

                                if (status == "success" || status == "completed")
                                {
                                    // 获取图片URL
                                    if (response.output?.data?.result?.image_urls != null && 
                                        response.output.data.result.image_urls.Length > 0)
                                    {
                                        imageUrl = response.output.data.result.image_urls[0];
                                        TJLog.Log($"[MaterialTemplate] {template.id}: 任务完成, imageUrl={imageUrl}");
                                    }
                                    break;
                                }
                                else if (status == "failed" || status == "error")
                                {
                                    currentGenerateError = response.error ?? response.message ?? TJGeneratorsL10n.L("生成失败");
                                    TJLog.LogError($"[MaterialTemplate] {template.id}: 任务失败 - {currentGenerateError}");
                                    break;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            TJLog.LogWarning($"[MaterialTemplate] {template.id}: 轮询解析失败: {e.Message}");
                        }
                    }
                    else
                    {
                        TJLog.LogWarning($"[MaterialTemplate] {template.id}: 轮询请求失败: {pollRequest.error}");
                    }
                }

                pollRetry++;
            }

            if (string.IsNullOrEmpty(imageUrl))
            {
                currentGenerateError = currentGenerateError ?? TJGeneratorsL10n.L("生成超时");
                templateErrors[template.id] = currentGenerateError;
                yield break;
            }

            // 下载图片
            yield return DownloadAndSaveImage(imageUrl, template.id);
        }

        private IEnumerator DownloadAndSaveImage(string url, string templateId)
        {
            TJLog.Log($"[MaterialTemplate] {templateId}: 开始下载图片: {url}");

            using (var request = UnityWebRequestTexture.GetTexture(url))
            {
                yield return request.SendWebRequest();

                if (UnityWebRequestCompat.IsSuccess(request))
                {
                    Texture2D texture = ((DownloadHandlerTexture)request.downloadHandler).texture;
                    if (texture != null)
                    {
                        try
                        {
                            // 确保目录存在
                            string absoluteDir = Path.GetDirectoryName(GetAbsoluteTemplatePath(templateId));
                            if (!Directory.Exists(absoluteDir))
                            {
                                Directory.CreateDirectory(absoluteDir);
                            }

                            // 保存图片
                            string absolutePath = GetAbsoluteTemplatePath(templateId);
                            
                            byte[] pngData = texture.EncodeToPNG();
                            File.WriteAllBytes(absolutePath, pngData);

                            string assetPath = GetTemplateImagePath(templateId);
                            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                            TJLog.Log($"[MaterialTemplate] {templateId}: 图片保存成功: {assetPath}");
                            
                            templateErrors.Remove(templateId);
                        }
                        catch (Exception e)
                        {
                            currentGenerateError = string.Format(TJGeneratorsL10n.L("保存图片失败: {0}"), e.Message);
                            templateErrors[templateId] = currentGenerateError;
                            TJLog.LogError($"[MaterialTemplate] {templateId}: {currentGenerateError}");
                        }
                        finally
                        {
                            UnityEngine.Object.DestroyImmediate(texture);
                        }
                    }
                    else
                    {
                        currentGenerateError = TJGeneratorsL10n.L("下载的纹理为空");
                        templateErrors[templateId] = currentGenerateError;
                        TJLog.LogError($"[MaterialTemplate] {templateId}: 下载的纹理为空");
                    }
                }
                else
                {
                    currentGenerateError = string.Format(TJGeneratorsL10n.L("下载失败: {0}"), request.error);
                    templateErrors[templateId] = currentGenerateError;
                    TJLog.LogError($"[MaterialTemplate] {templateId}: 下载失败: {request.error}");
                }
            }
        }

        /// <summary>
        /// 获取包的根目录绝对路径
        /// </summary>
        public static string GetPackageRootPath()
        {
            // 方法1：通过已知文件路径查找（使用 package.json 作为锚点）
            string currentDir = Directory.GetCurrentDirectory();
            
            // 检查当前目录是否就是包目录
            if (File.Exists(Path.Combine(currentDir, "package.json")))
            {
                var json = File.ReadAllText(Path.Combine(currentDir, "package.json"));
                if (json.Contains("cn.tuanjie.ai.generators"))
                    return currentDir;
            }
            
            // 检查 Packages 子目录（当包作为本地包嵌入时）
            string packagesDir = Path.Combine(currentDir, "Packages");
            if (Directory.Exists(packagesDir))
            {
                // 检查 manifest.json 中引用的本地包
                string manifestPath = Path.Combine(currentDir, "Packages", "manifest.json");
                if (File.Exists(manifestPath))
                {
                    string manifest = File.ReadAllText(manifestPath);
                    // 查找本地包引用
                    var match = System.Text.RegularExpressions.Regex.Match(manifest, @"""cn\.tuanjie\.ai\.generators""\s*:\s*""file:([^""]+)""");
                    if (match.Success)
                    {
                        string relativePath = match.Groups[1].Value;
                        string packagePath = Path.GetFullPath(Path.Combine(currentDir, relativePath));
                        if (Directory.Exists(packagePath))
                            return packagePath;
                    }
                }
                
                // 检查 Packages/cn.tuanjie.ai.generators 目录
                string packageSubDir = Path.Combine(packagesDir, "cn.tuanjie.ai.generators");
                if (Directory.Exists(packageSubDir))
                    return packageSubDir;
            }
            
            // 回退：使用 Application.dataPath 的父目录
            return Path.GetDirectoryName(Application.dataPath);
        }

        /// <summary>
        /// 获取模板图片的绝对路径
        /// </summary>
        public static string GetAbsoluteTemplatePath(string templateId)
        {
            string packagePath = GetTemplateImagePath(templateId);
            // 将 Packages/cn.tuanjie.ai.generators/... 转换为绝对路径
            string relativePath = packagePath.Replace("Packages/cn.tuanjie.ai.generators/", "");
            return Path.Combine(GetPackageRootPath(), relativePath);
        }

        /// <summary>
        /// 获取模板图片路径（相对于 Packages）
        /// </summary>
        public static string GetTemplateImagePath(string templateId)
        {
            return $"{TemplateDirectory}/{templateId}.png";
        }

        /// <summary>
        /// 检查模板是否存在
        /// </summary>
        public static bool TemplateExists(string templateId)
        {
            string path = GetTemplateImagePath(templateId);
            string relativePath = path.Replace("Packages/cn.tuanjie.ai.generators/", "");
            string absolutePath = Path.Combine(GetPackageRootPath(), relativePath);
            return File.Exists(absolutePath);
        }
    }

    // ========== 请求/响应数据类 ==========

    [Serializable]
    public class MaterialTemplateRequest
    {
        public string prompt;
        public string size;
        public string responseFormat;
        public bool stream;
        public bool watermark;
        public string sequentialImageGeneration;
        public bool isSegmentation;
    }

    [Serializable]
    public class MaterialTemplateTaskResponse
    {
        public string task_id;
        public string status;
        public string message;
    }

    /// <summary>
    /// 同步响应结构（task/huoshan-seedream-45 直接返回结果）
    /// </summary>
    [Serializable]
    public class MaterialTemplateSyncResponse
    {
        public string id;
        public string taskId;  // 可能为空
        public string status;
        public MaterialTemplateSyncOutput output;
    }

    [Serializable]
    public class MaterialTemplateSyncOutput
    {
        public MaterialTemplateSyncOutputData data;
    }

    [Serializable]
    public class MaterialTemplateSyncOutputData
    {
        public string[] image_urls;
    }
}
#endif
