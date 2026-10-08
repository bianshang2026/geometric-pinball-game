#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using TJGenerators;
using TJGenerators.Config;
using TJGenerators.Generators;
using TJGenerators.Utils;
using TJGenerators.PostProcessing;

namespace TJGenerators.Pipeline
{
    /// <summary>
    /// 纹理 / 序列帧 / 音频 / 视频资产的下载、写盘与 Host 回调（由 <see cref="GenerationPipeline"/> 委托）。
    /// </summary>
    internal sealed class GenerationMediaAssetHandlers
    {
        private const string LogTag = "[GenerationPipeline]";

        /// <summary>
        /// 由 <see cref="GenerationPipeline"/> 注入的收尾回调。
        /// </summary>
        internal sealed class Dependencies
        {
            public Action<ModelGeneratorBase, string> OnError;
            public Action<ModelGeneratorBase, string, string[], List<string>> OnComplete;
        }

        private readonly IGenerationPipelineHost _host;
        private readonly IMediaAssetPipelineHost _mediaHost;
        private readonly Dependencies _deps;
        private readonly string _historyDirectory;

        private string _audioSavePath;
        private string _videoSavePath;

        public GenerationMediaAssetHandlers(
            IGenerationPipelineHost host,
            Dependencies deps,
            string historyDirectory)
        {
            _host = host;
            _mediaHost = host as IMediaAssetPipelineHost;
            _deps = deps;
            _historyDirectory = historyDirectory?.TrimEnd('/', '\\') ?? "Assets/TJGenerators/History";
        }

        /// <summary>
        /// 初始化音频/视频输出的本地保存路径（不写入占位文件）。
        /// StartGeneration 与 StartFromSubmittedTask 共用。
        /// </summary>
        public void TryInitializeMediaSavePaths(ModelGeneratorBase generator)
        {
            string outputType = generator.GetOutputType();
            if (string.Equals(outputType, GenerationOutputTypes.Audio, StringComparison.OrdinalIgnoreCase))
                _audioSavePath = TryPrepareSavePath(_mediaHost?.GetAssetSavePath(PipelineMediaType.Audio, generator), "音频");
            else if (string.Equals(outputType, GenerationOutputTypes.Video, StringComparison.OrdinalIgnoreCase))
                _videoSavePath = TryPrepareSavePath(_mediaHost?.GetAssetSavePath(PipelineMediaType.Video, generator), "视频");
        }

        /// <summary>生成完成或失败后清空本次音视频保存路径。</summary>
        public void ClearMediaSavePaths()
        {
            _audioSavePath = null;
            _videoSavePath = null;
        }

        /// <summary>
        /// 处理非3D模型资产的下载和保存（天空盒、贴图、精灵图等）。支持单图或多图（如 image_urls 数组）。
        /// </summary>
        public IEnumerator HandleTextureAsset(
            ModelGeneratorBase generator,
            TJTaskStatusResponse response,
            IGenerationBackendTransport transport)
        {
            string[] downloadUrls = ResolveDownloadUrls(
                generator, response, TJGeneratorsL10n.L("未找到纹理资产下载URL"));
            if (downloadUrls == null)
                yield break;

            string firstSavePath = _mediaHost?.GetAssetSavePath(PipelineMediaType.Texture, generator);
            if (string.IsNullOrEmpty(firstSavePath))
            {
                _deps.OnError(generator, TJGeneratorsL10n.L("无法确定纹理资产保存路径"));
                yield break;
            }

            firstSavePath = firstSavePath.Replace('\\', '/');
            string dir = Path.GetDirectoryName(firstSavePath);
            string baseName = Path.GetFileNameWithoutExtension(firstSavePath);

            var savePaths = new List<string>();

            for (int i = 0; i < downloadUrls.Length; i++)
            {
                string url = downloadUrls[i];
                if (string.IsNullOrEmpty(url)) continue;

                TJLog.Log($"{LogTag} 开始下载纹理资产 [{i + 1}/{downloadUrls.Length}]: {url}");

                byte[] imageData = null;
                string downloadError = null;
                yield return DownloadBytesWithUiProgress(
                    transport, generator, url, i, downloadUrls.Length,
                    TJGeneratorsL10n.L("下载的纹理数据为空"),
                    bytes => imageData = bytes,
                    err => downloadError = err);

                if (!string.IsNullOrEmpty(downloadError))
                {
                    _deps.OnError(generator, downloadError);
                    yield break;
                }

                TJLog.Log($"{LogTag} 纹理下载完成 [{i + 1}/{downloadUrls.Length}], 大小: {imageData.Length} bytes");

                // 索引色 PNG（IHDR colorType=3）无真正 alpha；解码重编码为 RGBA32 再落盘。
                imageData = GeneratedTextureImportUtils.EnsureRgba32PngBytes(imageData);

                string savePath = i == 0
                    ? ResolveAndMigrateFirstTexturePath(firstSavePath, imageData, url)
                    : ResolveAdditionalTexturePath(dir, baseName, i, imageData, url);
                savePaths.Add(savePath);
                PipelineDownloadHelper.EnsureDirectoryForAssetPath(savePath);

                File.WriteAllBytes(PathUtils.ToAbsoluteAssetPath(savePath), imageData);
                PathUtils.ImportAssetAfterDiskWrite(savePath);

                if (i == 0)
                    _mediaHost?.OnAssetSaved(PipelineMediaType.Texture, savePath, generator);

                yield return null;
            }

            string actualModelPath = savePaths.Count > 0 ? savePaths[0] : firstSavePath;
            ApplyPreviewUrlToGenerator(generator, preferredPreviewUrl: null, downloadUrls, actualModelPath);
            _deps.OnComplete(generator, actualModelPath, downloadUrls, savePaths);
        }

        /// <summary>
        /// 处理 2D 序列帧：下载多帧图片，导入为 Sprite，并生成 AnimationClip。
        /// 历史记录以生成的 AnimationClip 路径作为 modelPath。
        /// </summary>
        public IEnumerator HandleSpriteSequenceAsset(
            ModelGeneratorBase generator,
            TJTaskStatusResponse response,
            IGenerationBackendTransport transport,
            string apiPreviewUrl = null)
        {
            string[] frameUrls = ResolveDownloadUrls(
                generator, response, TJGeneratorsL10n.L("未找到序列帧下载URL"));
            if (frameUrls == null)
                yield break;

            ReadSequenceParameters(generator, out int fps, out bool loop);

            string folderName = "Sequence_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string folderPath = _historyDirectory + "/" + folderName;
            EnsureHistoryFolder(folderPath);

            var frameSpritePaths = new List<string>();
            ReadRefImageMetrics(generator.GetImagePath(), out int refWidth, out float refPPU);

            string downloadError = null;
            yield return DownloadAndSaveFrameSprites(
                transport, generator, frameUrls, folderPath, refWidth, refPPU, frameSpritePaths,
                err => downloadError = err);

            if (!string.IsNullOrEmpty(downloadError))
                yield break;

            if (frameSpritePaths.Count == 0)
            {
                _deps.OnError(generator, TJGeneratorsL10n.L("未生成任何帧图片"));
                yield break;
            }

            string historyClipPath = AssetDatabase.GenerateUniqueAssetPath($"{folderPath}/{folderName}.anim");
            if (!SpriteSequencePostProcess.TryBuildAndSaveSpriteAnimationClip(
                    frameSpritePaths, historyClipPath, fps, loop,
                    out EditorCurveBinding binding, out ObjectReferenceKeyframe[] keys, out string clipError))
            {
                _deps.OnError(generator, TJGeneratorsL10n.L(clipError));
                yield break;
            }

            SyncTargetAnimationClip(fps, loop, binding, keys);
            ApplyPreviewUrlToGenerator(generator, apiPreviewUrl, frameUrls, historyClipPath);
            _deps.OnComplete(generator, historyClipPath, null, null);
        }

        /// <summary>
        /// 处理文生音频：下载到开始时创建的占位路径，覆盖后重新导入，应用到同一 AudioClip。
        /// </summary>
        public IEnumerator HandleAudioAsset(ModelGeneratorBase generator, TJTaskStatusResponse response)
        {
            string url = generator.GetDownloadUrl(response);
            if (string.IsNullOrEmpty(url))
            {
                _deps.OnError(generator, TJGeneratorsL10n.L("未找到音频下载URL"));
                yield break;
            }
            string savePath = ResolveMediaSavePath(_audioSavePath, PipelineMediaType.Audio, generator, "音频");
            if (string.IsNullOrEmpty(savePath))
            {
                _deps.OnError(generator, TJGeneratorsL10n.L("无法确定音频保存路径（占位未创建）"));
                yield break;
            }

            generator.ButtonText = TJGeneratorsL10n.L("下载中...");
            _host.Repaint();
            TJLog.Log($"{LogTag} 开始下载音频: {url} -> {savePath}");

            string finalPath = null;
            string detectedExtWithDot = null;
            string mediaError = null;
            yield return DownloadResolveExtAndWrite(
                url,
                savePath,
                120f,
                TJGeneratorsL10n.L("下载音频失败"),
                TJGeneratorsL10n.L("下载的音频数据为空"),
                (data, path) =>
                {
                    string configuredAudioFormat = generator.GetPipelineSettings()?.AudioFormat ?? "wav";
                    detectedExtWithDot = GenerationAssetFormatUtils.GetAudioExtensionFromData(data, url, configuredAudioFormat);
                    string resolved = Path.ChangeExtension(path, detectedExtWithDot);
                    TJLog.Log(
                        $"{LogTag} 音频扩展名（魔数/URL/配置）: {detectedExtWithDot}, 保存路径: {resolved}"
                    );
                    return resolved;
                },
                path => finalPath = path,
                err => mediaError = err);

            if (!string.IsNullOrEmpty(mediaError))
            {
                _deps.OnError(generator, mediaError);
                yield break;
            }

            finalPath = TJGeneratorsAudioUtils.EnsureUnityImportableAudioPath(finalPath);

            if (TJGeneratorsAudioUtils.TryLoadAudioClip(finalPath) == null)
            {
                string importError = string.Format(TJGeneratorsL10n.L("无法将 {0} 导入为 AudioClip。"), finalPath);
                if (TJGeneratorsAudioUtils.NeedsTranscodeForUnityImport(detectedExtWithDot)
                    || TJGeneratorsAudioUtils.NeedsTranscodeForUnityImport(Path.GetExtension(finalPath)))
                {
                    importError += TJGeneratorsL10n.L("后端返回 AAC/MP4 时需安装 ffmpeg 并加入 PATH 以自动转 WAV。");
                }
                _deps.OnError(generator, importError);
                yield break;
            }

            ApplyPreviewUrlToGenerator(generator, preferredPreviewUrl: null, new[] { url }, finalPath);
            _mediaHost?.OnAssetSaved(PipelineMediaType.Audio, finalPath, generator);
            _deps.OnComplete(generator, finalPath, null, null);
        }

        /// <summary>
        /// 处理视频资产：下载到保存路径，导入后通知 Host。
        /// </summary>
        public IEnumerator HandleVideoAsset(
            ModelGeneratorBase generator,
            TJTaskStatusResponse response,
            string apiPreviewUrl = null)
        {
            string url = generator.GetDownloadUrl(response);
            if (string.IsNullOrEmpty(url))
            {
                _deps.OnError(generator, TJGeneratorsL10n.L("未找到视频下载URL"));
                yield break;
            }
            string savePath = ResolveMediaSavePath(_videoSavePath, PipelineMediaType.Video, generator, "视频");
            if (string.IsNullOrEmpty(savePath))
            {
                _deps.OnError(generator, TJGeneratorsL10n.L("无法确定视频保存路径"));
                yield break;
            }

            generator.ButtonText = TJGeneratorsL10n.L("下载中...");
            _host.Repaint();
            TJLog.Log($"{LogTag} 开始下载视频: {url} -> {savePath}");

            string finalPath = null;
            string mediaError = null;
            yield return DownloadResolveExtAndWrite(
                url,
                savePath,
                ConfigManager.GetDownloadTimeout(),
                TJGeneratorsL10n.L("下载视频失败"),
                TJGeneratorsL10n.L("下载的视频数据为空"),
                (data, path) =>
                {
                    string actualExtension = GenerationAssetFormatUtils.GetVideoExtensionFromData(data, url);
                    string resolved = Path.ChangeExtension(path, actualExtension);
                    TJLog.Log($"{LogTag} 视频格式检测: {actualExtension}, 保存路径: {resolved}");
                    return resolved;
                },
                path => finalPath = path,
                err => mediaError = err);

            if (!string.IsNullOrEmpty(mediaError))
            {
                _deps.OnError(generator, mediaError);
                yield break;
            }

            PathUtils.ImportAssetAfterDiskWrite(finalPath);

            ApplyPreviewUrlToGenerator(generator, apiPreviewUrl, null, finalPath);
            _mediaHost?.OnAssetSaved(PipelineMediaType.Video, finalPath, generator);
            _deps.OnComplete(generator, finalPath, null, null);
        }

        // --- URL / 路径解析 ---

        private static string TryPrepareSavePath(string savePath, string label)
        {
            if (string.IsNullOrEmpty(savePath))
                return null;

            try
            {
                PipelineDownloadHelper.EnsureDirectoryForAssetPath(savePath);
                return savePath;
            }
            catch (Exception e)
            {
                TJLog.LogWarning($"{LogTag} 准备{label}保存路径失败: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Prefer cached path from <see cref="TryInitializeMediaSavePaths"/>; fall back to host placeholder
        /// (same pattern as <see cref="HandleTextureAsset"/>).
        /// </summary>
        private string ResolveMediaSavePath(
            string cachedPath, PipelineMediaType mediaType, ModelGeneratorBase generator, string label)
        {
            if (!string.IsNullOrEmpty(cachedPath))
                return cachedPath;

            return TryPrepareSavePath(_mediaHost?.GetAssetSavePath(mediaType, generator), label);
        }

        /// <summary>
        /// 解析下载 URL 列表：优先 GetDownloadUrls，回退 GetDownloadUrl。
        /// 找不到时调用 OnError 并返回 null。
        /// </summary>
        private string[] ResolveDownloadUrls(
            ModelGeneratorBase generator,
            TJTaskStatusResponse response,
            string missingUrlError)
        {
            string[] downloadUrls = generator.GetDownloadUrls(response);
            if (downloadUrls != null && downloadUrls.Length > 0)
                return downloadUrls;

            string singleUrl = generator.GetDownloadUrl(response);
            if (string.IsNullOrEmpty(singleUrl))
            {
                _deps.OnError(generator, missingUrlError);
                return null;
            }
            return new[] { singleUrl };
        }

        /// <summary>
        /// 主图：扩展名与字节格式一致；若与占位路径不同则删除占位并生成唯一路径。
        /// </summary>
        private static string ResolveAndMigrateFirstTexturePath(
            string firstSavePath, byte[] imageData, string url)
        {
            string actualExtension = GenerationAssetFormatUtils.GetImageExtensionFromData(imageData, url);
            string pathWithActualExt =
                Path.ChangeExtension(firstSavePath, actualExtension).Replace('\\', '/');

            if (string.Equals(pathWithActualExt, firstSavePath, StringComparison.OrdinalIgnoreCase))
                return firstSavePath;

            string absPlaceholder = PathUtils.ToAbsoluteAssetPath(firstSavePath);
            if (File.Exists(absPlaceholder))
            {
                if (AssetDatabase.LoadMainAssetAtPath(firstSavePath) != null)
                    AssetDatabase.DeleteAsset(firstSavePath);
                else
                    File.Delete(absPlaceholder);
            }

            return AssetDatabase.GenerateUniqueAssetPath(pathWithActualExt);
        }

        private static string ResolveAdditionalTexturePath(
            string dir, string baseName, int index, byte[] imageData, string url)
        {
            string actualExtension = GenerationAssetFormatUtils.GetImageExtensionFromData(imageData, url);
            string savePath = Path.Combine(dir, baseName + "_" + index);
            savePath = Path.ChangeExtension(savePath, actualExtension).Replace('\\', '/');
            return AssetDatabase.GenerateUniqueAssetPath(savePath);
        }

        // --- 下载写盘 ---

        /// <summary>
        /// transport 下载字节，并更新 UI 进度文案。失败时 onError 收到本地化消息。
        /// </summary>
        private IEnumerator DownloadBytesWithUiProgress(
            IGenerationBackendTransport transport,
            ModelGeneratorBase generator,
            string url,
            int index,
            int total,
            string emptyDataMessage,
            Action<byte[]> onSuccess,
            Action<string> onError)
        {
            generator.ButtonText = total > 1
                ? TJGeneratorsL10n.L("下载中 ({0}/{1})...", index + 1, total)
                : TJGeneratorsL10n.L("下载中...");
            _host.Repaint();

            byte[] data = null;
            string downloadError = null;
            yield return transport.DownloadBytes(url, bytes => data = bytes, err => downloadError = err);

            if (!string.IsNullOrEmpty(downloadError))
            {
                onError(downloadError);
                yield break;
            }
            if (data == null || data.Length == 0)
            {
                onError(emptyDataMessage);
                yield break;
            }

            onSuccess(data);
        }

        /// <summary>
        /// 音视频共用：下载字节 → 按内容改扩展名 → 写盘。
        /// </summary>
        private static IEnumerator DownloadResolveExtAndWrite(
            string url,
            string savePath,
            float timeout,
            string downloadFailMessage,
            string emptyDataMessage,
            Func<byte[], string, string> resolvePathWithExtension,
            Action<string> onSaved,
            Action<string> onError)
        {
            byte[] data = null;
            string downloadError = null;
            yield return PipelineDownloadHelper.DownloadUrlToBytes(
                url,
                timeout,
                bytes => data = bytes,
                err => downloadError = err,
                downloadFailMessage);

            if (!string.IsNullOrEmpty(downloadError))
            {
                onError(downloadError);
                yield break;
            }
            if (data == null || data.Length == 0)
            {
                onError(emptyDataMessage);
                yield break;
            }

            string finalPath = resolvePathWithExtension(data, savePath);
            PipelineDownloadHelper.EnsureDirectoryForAssetPath(finalPath);
            File.WriteAllBytes(PathUtils.ToAbsoluteAssetPath(finalPath), data);
            onSaved(finalPath);
        }

        private IEnumerator DownloadAndSaveFrameSprites(
            IGenerationBackendTransport transport,
            ModelGeneratorBase generator,
            string[] frameUrls,
            string folderPath,
            int refWidth,
            float refPPU,
            List<string> frameSpritePaths,
            Action<string> onError)
        {
            for (int i = 0; i < frameUrls.Length; i++)
            {
                string url = frameUrls[i];
                if (string.IsNullOrEmpty(url)) continue;

                byte[] imageData = null;
                string downloadError = null;
                yield return DownloadBytesWithUiProgress(
                    transport, generator, url, i, frameUrls.Length,
                    TJGeneratorsL10n.L("下载的帧数据为空"),
                    bytes => imageData = bytes,
                    err => downloadError = err);

                if (!string.IsNullOrEmpty(downloadError))
                {
                    _deps.OnError(generator, downloadError);
                    onError(downloadError);
                    yield break;
                }

                imageData = GeneratedTextureImportUtils.EnsureRgba32PngBytes(imageData);

                string ext = GenerationAssetFormatUtils.GetImageExtensionFromData(imageData, url);
                string frameAssetPath = $"{folderPath}/frame_{i + 1:0000}.{ext.TrimStart('.')}";
                frameAssetPath = AssetDatabase.GenerateUniqueAssetPath(frameAssetPath);
                File.WriteAllBytes(PathUtils.ToAbsoluteAssetPath(frameAssetPath), imageData);
                PathUtils.ImportAssetAfterDiskWrite(frameAssetPath);

                ConfigureFrameSpriteImporter(frameAssetPath, imageData, refWidth, refPPU);
                TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(frameAssetPath));

                frameSpritePaths.Add(frameAssetPath);
                yield return null;
            }
        }

        // --- 序列帧 / Clip ---

        private static void ReadSequenceParameters(
            ModelGeneratorBase generator, out int fps, out bool loop)
        {
            fps = 12;
            loop = true;

            if (generator is IGeneratorParameterProvider paramProvider)
            {
                if (paramProvider.GetParameter("fps") is int fpsInt) fps = fpsInt;
                else if (int.TryParse(paramProvider.GetParameter("fps")?.ToString(), out int fpsParsed))
                    fps = fpsParsed;

                if (paramProvider.GetParameter("loop") is bool loopBool) loop = loopBool;
                else if (bool.TryParse(paramProvider.GetParameter("loop")?.ToString(), out bool loopParsed))
                    loop = loopParsed;
            }

            fps = Mathf.Clamp(fps, 1, 60);
        }

        private void EnsureHistoryFolder(string folderPath)
        {
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                AssetDatabase.CreateFolder("Assets", "TJGenerators");

            string historyParent = Path.GetDirectoryName(_historyDirectory)?.Replace('\\', '/');
            string historyLeaf = Path.GetFileName(_historyDirectory);
            if (!string.IsNullOrEmpty(historyParent)
                && !string.IsNullOrEmpty(historyLeaf)
                && !AssetDatabase.IsValidFolder(_historyDirectory))
            {
                AssetDatabase.CreateFolder(historyParent, historyLeaf);
            }

            string absFolder = PathUtils.ToAbsoluteAssetPath(folderPath);
            if (!Directory.Exists(absFolder))
                Directory.CreateDirectory(absFolder);
        }

        private static void ReadRefImageMetrics(
            string refImagePath, out int refWidth, out float refPPU)
        {
            refWidth = 0;
            refPPU = 100f;

            if (string.IsNullOrEmpty(refImagePath))
                return;

            try
            {
                string absRefPath = PathUtils.ToAbsoluteAssetPath(refImagePath);
                if (File.Exists(absRefPath))
                {
                    var refTex = new Texture2D(2, 2);
                    if (refTex.LoadImage(File.ReadAllBytes(absRefPath)))
                        refWidth = refTex.width;
                    UnityEngine.Object.DestroyImmediate(refTex);
                }
                var refImporter = AssetImporter.GetAtPath(refImagePath) as TextureImporter;
                if (refImporter != null)
                    refPPU = refImporter.spritePixelsPerUnit;
            }
            catch
            {
                // 读取失败则使用默认值
            }
        }

        private static void ConfigureFrameSpriteImporter(
            string frameAssetPath, byte[] imageData, int refWidth, float refPPU)
        {
            // 先按 Sprite + RGBA32 锁定导入，再按需覆盖 PPU
            GeneratedTextureImportUtils.ConfigureImportedTexture(
                frameAssetPath, TextureImporterType.Sprite, alphaIsTransparency: true);

            if (refWidth <= 0)
                return;

            var importer = AssetImporter.GetAtPath(frameAssetPath) as TextureImporter;
            if (importer == null)
                return;

            var frameTex = new Texture2D(2, 2);
            try
            {
                if (frameTex.LoadImage(imageData) && frameTex.width > 0)
                {
                    importer.spritePixelsPerUnit = refPPU * frameTex.width / refWidth;
                    importer.SaveAndReimport();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(frameTex);
            }
        }

        private void SyncTargetAnimationClip(
            int fps, bool loop, EditorCurveBinding binding, ObjectReferenceKeyframe[] keys)
        {
            // 若绑定了目标 AnimationClip，则同步更新它（保留历史 clip 独立路径）
            var targetAsset = _host.GetTargetAsset();
            if (targetAsset == null || !targetAsset.IsValid())
                return;

            string targetPath = targetAsset.GetPath();
            var targetClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath);
            if (targetClip == null)
                return;

            targetClip.frameRate = fps;
            SpriteSequencePostProcess.ApplySpriteCurveToClip(targetClip, binding, keys, loop);
            EditorUtility.SetDirty(targetClip);
            AssetDatabase.SaveAssets();
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(targetPath));
        }

        // --- 预览 ---

        private static void ApplyPreviewUrlToGenerator(
            ModelGeneratorBase generator,
            string preferredPreviewUrl,
            string[] fallbackUrls,
            string localPath)
        {
            string ep = preferredPreviewUrl;

            if (string.IsNullOrEmpty(ep) && fallbackUrls != null && fallbackUrls.Length > 0)
                ep = fallbackUrls[0];

            if (string.IsNullOrEmpty(ep))
                ep = PipelineDownloadHelper.ResolveLocalFilePreviewUrl(localPath);

            generator.CurrentPreviewUrl = ep;
        }
    }
}
#endif
