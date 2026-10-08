using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Codely.Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEditor;

#if UNITY_EDITOR
using TJGenerators;
using TJGenerators.Generators;
using TJGenerators.Config;
using TJGenerators.Pipeline;
using TJGenerators.PostProcessing;
using TJGenerators.Utils;
using Unity.EditorCoroutines.Editor;
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// CustomTools for generating game UI asset kits.
    /// generate_game_ui_kit: two async steps (Step 1 text→screenshot, Step 2 screenshot→layers/cutout).
    /// slice_image: synchronous CV connected-component slicing (fallback for merged layers or magenta sheets).
    /// </summary>
    public static class GenerateGameUiKitTool
    {
        private const string GeneratorId = "frontier-game-design"; // frontier 兼容路径
        private const string SeedreamProGeneratorId = "huoshan_seedream_pro_image";
        private const string SeedreamScreenshotSize = "2848x1600"; // 2K 16:9，须在 seedreamProAllowedSizes 白名单内
        private const string SeedreamLayerSize = "2K";
        private const string ScreenshotPromptSuffix = ", complete game UI screen design, full HUD layout, " +
            "health bars, mana bars, buttons, panels, inventory grid, skill icons, mini-map, " +
            "dialogue box, score display, clean professional game interface, " +
            "high quality 2D game UI design, detailed UI elements, consistent art style";
        private const string CutoutPrompt = "Using the reference screenshot, extract every UI element as individual isolated cutouts " +
            "and arrange them in a grid with clear spacing between each element. " +
            "Extract: buttons, panels, health/mana bars, icons, frames, borders, dividers, sliders, checkboxes, tab headers. " +
            "Keep any dynamic text labels (numbers, player names, scores) as separate editable elements, not baked into button art. " +
            "Preserve thin UI borders, decorative edges, and fine stroke details exactly. " +
            "Render everything on a perfectly flat pure magenta (#FF00FF) background. " +
            "No shadows, no gradients, no scenery, no reflections, no texture in background. " +
            "Each element must have clear margin around it for clean extraction.";
        // 与后端 mcp/game_ui_kit.go 的 gameUILayerPrompt 保持一致（含检测标记短语）。
        // 元素图层去动态文字（引擎运行时用 Text 组件渲染），动态文字独立成层（作位置/风格参考）。
        internal const string SeedreamLayerHint = "game UI kit layer decomposition: split this game UI screenshot into independent layers. " +
            "Put each UI element or coherent element group (buttons, panels, health/mana bars, skill icons, " +
            "item slots, frames, borders, dividers, sliders, tab headers) on its own transparent layer. " +
            "IMPORTANT: element art layers must be TEXT-FREE — remove all dynamic text (numbers, values, names, " +
            "scores, timers, counts) from button/panel/bar/icon art; leave those areas empty or with plain fill only. " +
            "Each dynamic text label goes on its own separate transparent text layer instead. " +
            "Static decorative engraved text that is part of the art style may stay.";

        /// <summary>provider + 步骤 → generator id（非 frontier 值一律落 seedream 默认）</summary>
        internal static string ResolveStepGeneratorId(string provider, int step)
        {
            if (string.Equals(provider, "frontier", StringComparison.OrdinalIgnoreCase))
                return GeneratorId;
            return step == 1 ? SeedreamProGeneratorId : GenerateImageLayersTool.SeedreamGeneratorId;
        }

        [ExecuteCustomTool.CustomTool("generate_game_ui_kit",
            "Generate a game UI asset kit in two async steps: " +
            "Step 1 (no screenshot_path): generates a game UI screenshot from text. " +
            "Step 2 (with screenshot_path): decomposes the screenshot into independent transparent PNG layers " +
            "(1 base image + up to 16 layers) — element art layers are TEXT-FREE (dynamic text goes on separate text layers, " +
            "use those only as position/style reference for runtime Text components) — ready to use directly as sprites, no chroma key needed. " +
            "Parameters: prompt (required), " +
            "screenshot_path (Step 2 only: local path of Step 1's output image_path), " +
            "provider (optional 'seedream_pro'|'frontier', default 'seedream_pro' — frontier keeps the legacy magenta cutout-sheet step 2; use the same provider for both steps), " +
            "size (optional '1K'|'1.5K'|'2K'|'auto', seedream step 2 only, default '2K'), " +
            "quality (optional, frontier only), output_format (optional, frontier only), " +
            "output_path (optional save path). " +
            "IMPORTANT: No placeholder is returned. A <bg_task_done> notification will arrive upon completion " +
            "(step 2 seedream carries layer_0_path/layers_folder/layer_count/layers_found/layer_paths; layer 0 is the base image, not an element). " +
            "If a layer still contains multiple merged elements, run slice_image on it with background_mode 'transparent'.")]
        public static object GenerateGameUiKit(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateGameUiKitTool] Generating game UI kit with parameters: {parameters}");

                string prompt = parameters["prompt"]?.ToString();
                string screenshotPath = parameters["screenshot_path"]?.ToString();
                string outputPath = parameters["output_path"]?.ToString();
                string sessionId = parameters["session_id"]?.ToString() ?? "";
                GenerationRequestOrigin.SetWorkspaceName(parameters["workspace_name"]?.ToString());
                string provider = parameters["provider"]?.ToString();

                int step = string.IsNullOrEmpty(screenshotPath) ? 1 : 2;
                bool isFrontier = string.Equals(provider, "frontier", StringComparison.OrdinalIgnoreCase);
                string generatorId = ResolveStepGeneratorId(provider, step);

                if (string.IsNullOrEmpty(prompt))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "'prompt' parameter is required" }
                    };
                }

                string effectivePrompt;
                if (step == 1)
                    effectivePrompt = prompt + ScreenshotPromptSuffix; // 两 provider 共用同一后缀（保持检测标记一致）
                else
                    effectivePrompt = isFrontier ? CutoutPrompt : SeedreamLayerHint;

                int maxLen = TJGeneratorsPromptLimits.GetMaxLength(generatorId);
                if (effectivePrompt.Length > maxLen)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error_code", "INVALID_PARAMS" },
                        { "message", $"Prompt length ({effectivePrompt.Length}) exceeds the {maxLen} character limit." }
                    };
                }

                // Load config（新装包模型可能不在运行时缓存，回退包内配置）
                var config = ConfigManager.GetGeneratorConfig(ConfigType.Image, generatorId);
                if (config == null)
                    config = ConfigManager.GetPackageGeneratorConfig(ConfigType.Image, generatorId);
                if (config == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Cannot find generator config for '{generatorId}'." }
                    };
                }

                var generator = new DynamicGenerator(config);
                generator.SetTextPrompt(effectivePrompt);
                generator.SetHistoryDisplayPrompt(prompt);

                if (step == 1)
                {
                    if (isFrontier)
                    {
                        generator.SetParameter("imageSize", "landscape_16_9");
                    }
                    else
                    {
                        // Seedream Pro 截图：2K 16:9；必须显式关闭自动抠图（配置默认 true 会把 HUD 背景抠掉）
                        generator.SetParameter("size", SeedreamScreenshotSize);
                        generator.SetParameter("isSegmentation", false);
                    }
                }
                else
                {
                    generator.SetImagePath(screenshotPath);
                    if (isFrontier)
                    {
                        generator.SetParameter("imageSize", "square_hd");
                    }
                    else
                    {
                        string size = GenerateImageLayersTool.ParseSeedreamSize(parameters["size"]);
                        generator.SetParameter("size", string.IsNullOrEmpty(size) ? SeedreamLayerSize : size);
                    }
                }

                // quality/output_format 仅 frontier 有效（seedream pro / 图层拆分无此参数）
                if (isFrontier)
                    ApplyGameUiKitParameters(generator, parameters);

                // Submit task
                var submitResult = TJGeneratorsGenerationService.SubmitTaskSync(generator, sessionId);
                if (!submitResult.Success)
                {
                    TJLog.LogError($"[GenerateGameUiKitTool] 任务提交失败 [{submitResult.ErrorCode}]: {submitResult.Message}");
                    return new Dictionary<string, object>
                    {
                        { "success",    false },
                        { "error_code", submitResult.ErrorCode },
                        { "message",    submitResult.Message }
                    };
                }

                TJLog.Log($"[GenerateGameUiKitTool] Step {step} ({generatorId}) 任务提交成功，backend_task_id={submitResult.BackendTaskId}");

                // Create placeholder texture
                string placeholderPath = CreatePlaceholderTexture(outputPath);

                // Register task + pipeline host
                string capturedBackendTaskId = submitResult.BackendTaskId;

                if (step == 2 && !isFrontier)
                {
                    // Seedream 图层拆分：多图下载（底图 + 各图层），复用 layers tracker/host 基建
                    int expectedLayerCount = GenerateImageLayersTool.SeedreamMaxLayerCount;
                    string taskId = ImageLayersTaskTracker.CreateTask(
                        generatorId, prompt, screenshotPath, expectedLayerCount, placeholderPath, capturedBackendTaskId);

                    var host = new ImageLayersPipelineHost(
                        placeholderPath,
                        sessionId,
                        expectedLayerCount,
                        taskId,
                        capturedBackendTaskId,
                        errorMsg =>
                        {
                            ImageLayersTaskTracker.MarkTaskFailed(taskId, errorMsg);
                            GenerationNotifier.NotifyFailed("generate_game_ui_kit", taskId, capturedBackendTaskId, errorMsg,
                                new JObject { ["session_id"] = sessionId, ["generator_id"] = generatorId, ["prompt"] = prompt ?? "", ["step"] = step });
                        },
                        toolName: "generate_game_ui_kit");

                    string historyAssetGuid = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(placeholderPath);
                    var pipeline = new GenerationPipeline(host, ConfigType.Image, GenerationRequestOrigin.Agent, sessionId, "generate_game_ui_kit");
                    EditorCoroutineUtility.StartCoroutineOwnerless(
                        pipeline.StartFromSubmittedTask(generator, historyAssetGuid, submitResult.BackendTaskId));

                    TJLog.Log($"[GenerateGameUiKitTool] Step 2 (layers) 轮询已启动，task_id={taskId}, backend_task_id={submitResult.BackendTaskId}");

                    return new Dictionary<string, object>
                    {
                        { "success", true },
                        { "submission_success", true },
                        { "message",
                            "Game UI kit Step 2 (Seedream layer decomposition) started. " +
                            "A <bg_task_done> notification will arrive with layer_0_path (BASE image, not an element), " +
                            "layers_folder, layer_count, layers_found and layer_paths. Element art layers (e.g. *_Art) are TEXT-FREE " +
                            "transparent cutouts, ready to use; dynamic text layers (e.g. *_Dynamic_Text) are only position/style " +
                            "reference for runtime Text components — do not ship them as sprites. " +
                            "If a layer still contains multiple merged elements, run slice_image on it with background_mode 'transparent'. " +
                            "*** POLLING IS STRICTLY FORBIDDEN. ***" },
                        { "task_id", taskId },
                        { "backend_task_id", submitResult.BackendTaskId },
                        { "status", "submitted" },
                        { "generator_id", generatorId },
                        { "provider", "seedream_pro" },
                        { "step", step },
                        { "prompt", prompt },
                        { "placeholder_path", placeholderPath },
                        { "estimated_wait_seconds", 90 },
                        { "notification_mode", "bg_task_done" },
                        { "preview_url", PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) }
                    };
                }

                // Step 1（两 provider）与 frontier Step 2：单图输出
                string singleTaskId = ImageTaskTracker.CreateTask(generatorId, prompt, screenshotPath, placeholderPath, capturedBackendTaskId);

                var host2 = new ImagePipelineHost(
                    placeholderPath,
                    sessionId,
                    (savedPath, previewUrl) =>
                    {
                        ImageTaskTracker.MarkTaskCompleted(singleTaskId, savedPath, previewUrl);
                        var t = ImageTaskTracker.GetTask(singleTaskId);
                        GenerationNotifier.NotifyCompleted("generate_game_ui_kit", singleTaskId, capturedBackendTaskId,
                            new JObject
                            {
                                ["session_id"]       = sessionId,
                                ["generator_id"]     = generatorId,
                                ["prompt"]           = prompt ?? "",
                                ["image_path"]       = savedPath,
                                ["preview_url"]      = previewUrl ?? "",
                                ["step"]             = step,
                                ["progress"]         = 100,
                                ["start_time"]       = t?.StartTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["end_time"]         = t?.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                                ["duration_seconds"] = (t != null && t.EndTime.HasValue) ? (int)(t.EndTime.Value - t.StartTime).TotalSeconds : 0
                            });
                    },
                    errorMsg =>
                    {
                        ImageTaskTracker.MarkTaskFailed(singleTaskId, errorMsg);
                        GenerationNotifier.NotifyFailed("generate_game_ui_kit", singleTaskId, capturedBackendTaskId, errorMsg,
                            new JObject { ["session_id"] = sessionId, ["generator_id"] = generatorId, ["prompt"] = prompt ?? "", ["step"] = step });
                    }
                );

                string historyAssetGuid2 = CustomToolHistoryBindings.HistoryGuidFromPlaceholderAssetPath(placeholderPath);
                var pipeline2 = new GenerationPipeline(host2, ConfigType.Image, GenerationRequestOrigin.Agent, sessionId, "generate_game_ui_kit");
                EditorCoroutineUtility.StartCoroutineOwnerless(
                    pipeline2.StartFromSubmittedTask(generator, historyAssetGuid2, submitResult.BackendTaskId));

                TJLog.Log($"[GenerateGameUiKitTool] Step {step} 轮询已启动，task_id={singleTaskId}, backend_task_id={submitResult.BackendTaskId}");

                return new Dictionary<string, object>
                {
                    { "success",            true },
                    { "submission_success", true },
                    { "message",
                        step == 1
                            ? "Game UI kit Step 1 (screenshot) started. " +
                              "After <bg_task_done>, submit Step 2 with screenshot_path = the returned image_path " +
                              "and the SAME provider. " +
                              "*** POLLING IS STRICTLY FORBIDDEN. ***"
                            : "Game UI kit Step 2 (cutout sheet) started. " +
                              "A <bg_task_done> notification will arrive with the final image_path. " +
                              "*** POLLING IS STRICTLY FORBIDDEN. ***" },
                    { "task_id",            singleTaskId },
                    { "backend_task_id",    submitResult.BackendTaskId },
                    { "status",             "submitted" },
                    { "generator_id",       generatorId },
                    { "provider",           isFrontier ? "frontier" : "seedream_pro" },
                    { "step",               step },
                    { "prompt",             prompt },
                    { "placeholder_path",   placeholderPath },
                    { "estimated_wait_seconds", step == 1 ? 60 : 60 },
                    { "notification_mode",  "bg_task_done" },
                    { "preview_url",        PreviewUrlHelper.BuildFixedPreviewUrl(submitResult.BackendTaskId) }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateGameUiKitTool] Error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error generating game UI kit: {e.Message}" }
                };
            }
#else
            return new Dictionary<string, object>
            {
                { "success", false },
                { "message", "This tool only works in Unity Editor." }
            };
#endif
        }

        [ExecuteCustomTool.CustomTool("slice_image",
            "Slice a sprite sheet / cutout sheet into individual sprite PNGs using CV connected-component detection. " +
            "Automatically detects background (transparent or solid color like magenta), finds connected regions via 8-connected BFS, " +
            "applies feather + color decontamination to remove background fringe, and exports each element as a separate PNG. " +
            "Also used as the fallback for merged layer-decomposition layers (a layer PNG still containing multiple elements): use background_mode 'transparent'. " +
            "Parameters: image_path (required, local asset path), " +
            "background_mode (optional 'auto'|'transparent'|'solid_color', default 'auto' — for magenta cutout sheets use 'solid_color'), " +
            "color_tolerance (optional 0-100, default 15, higher = more pixels treated as background), " +
            "alpha_threshold (optional 0-1, default 0.1, used when background_mode is 'transparent'), " +
            "min_region_pixels (optional, default 100, regions smaller than this are ignored), " +
            "padding (optional, default 2, extra pixels around each sliced element), " +
            "set_as_sprite (optional, default true, auto-set TextureImporterType.Sprite on output). " +
            "Returns: sliced_count, output_directory, sliced_asset_paths array. " +
            "This is a synchronous operation — no task_id or polling needed.")]
        public static object SliceImage(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                TJLog.Log($"[GenerateGameUiKitTool] SliceImage parameters: {parameters}");

                string imagePath = parameters["image_path"]?.ToString();
                if (string.IsNullOrEmpty(imagePath))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "'image_path' parameter is required" }
                    };
                }

                // Load readable texture
                var readableTex = SpriteSequencePostProcess.LoadReadableTextureFromAssetPath(imagePath);
                if (readableTex == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", $"Failed to load readable texture from: {imagePath}. Ensure the file exists and is a valid image." }
                    };
                }

                try
                {
                    // Parse parameters
                    string bgModeStr = parameters["background_mode"]?.ToString() ?? "auto";
                    ImageSlicePostProcess.BackgroundMode bgMode;
                    switch (bgModeStr.ToLowerInvariant())
                    {
                        case "transparent":
                            bgMode = ImageSlicePostProcess.BackgroundMode.Transparent;
                            break;
                        case "solid_color":
                        case "solidcolor":
                            bgMode = ImageSlicePostProcess.BackgroundMode.SolidColor;
                            break;
                        default:
                            bgMode = ImageSlicePostProcess.BackgroundMode.Auto;
                            break;
                    }

                    float alphaThreshold = parameters["alpha_threshold"] != null
                        ? (float)parameters["alpha_threshold"].Value<double>()
                        : 0.1f;
                    float colorTolerance = parameters["color_tolerance"] != null
                        ? (float)parameters["color_tolerance"].Value<double>()
                        : 15f;
                    int minRegionPixels = parameters["min_region_pixels"]?.Value<int>() ?? 100;
                    int padding = parameters["padding"]?.Value<int>() ?? 2;
                    bool setAsSprite = parameters["set_as_sprite"]?.Value<bool>() ?? true;

                    TJLog.Log($"[GenerateGameUiKitTool] SliceImage: bgMode={bgMode}, alphaThreshold={alphaThreshold}, " +
                        $"colorTolerance={colorTolerance}, minRegionPixels={minRegionPixels}, padding={padding}, setAsSprite={setAsSprite}");

                    var result = ImageSlicePostProcess.Export(
                        readableTex,
                        imagePath,
                        bgMode,
                        alphaThreshold,
                        colorTolerance,
                        minRegionPixels,
                        padding,
                        setAsSprite);

                    if (result.ExportedCount == 0)
                    {
                        return new Dictionary<string, object>
                        {
                            { "success", false },
                            { "message", "No regions detected. Try adjusting background_mode, color_tolerance, or min_region_pixels." }
                        };
                    }

                    TJLog.Log($"[GenerateGameUiKitTool] SliceImage completed: {result.ExportedCount} sprites exported to {result.OutputDirectory}");

                    return new Dictionary<string, object>
                    {
                        { "success", true },
                        { "sliced_count", result.ExportedCount },
                        { "output_directory", result.OutputDirectory },
                        { "sliced_asset_paths", result.AssetPaths },
                        { "message", $"Successfully sliced {result.ExportedCount} sprite(s) into {result.OutputDirectory}" }
                    };
                }
                finally
                {
                    // Destroy the runtime texture if it's not an asset
                    if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(readableTex)))
                        UnityEngine.Object.DestroyImmediate(readableTex);
                }
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateGameUiKitTool] SliceImage error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error slicing image: {e.Message}" }
                };
            }
#else
            return new Dictionary<string, object>
            {
                { "success", false },
                { "message", "This tool only works in Unity Editor." }
            };
#endif
        }

        [ExecuteCustomTool.CustomTool("query_game_ui_kit_status",
            "Query the status of a game UI kit generation task. Use ONLY as a one-time fallback if no <bg_task_done> notification arrives. " +
            "When completed, returns 'image_path' (Step 1 / frontier Step 2) or 'layer_paths'/'layers_found'/'layers_folder' (seedream Step 2 layers). " +
            "Status values: 'generating', 'completed', 'failed'. " +
            "WARNING: Do NOT call this tool repeatedly. Polling is forbidden.")]
        public static object QueryGameUiKitStatus(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                string taskId = parameters["task_id"]?.ToString();
                if (string.IsNullOrEmpty(taskId))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "message", "'task_id' parameter is required" }
                    };
                }

                var task = ImageTaskTracker.GetTask(taskId);
                if (task == null)
                {
                    // seedream Step 2 任务在 layers tracker 里
                    var layersTask = ImageLayersTaskTracker.GetTask(taskId);
                    if (layersTask == null)
                    {
                        return new Dictionary<string, object>
                        {
                            { "success", false },
                            { "message", $"Task '{taskId}' not found. It may have been completed and cleaned up." }
                        };
                    }
                    return BuildLayersTaskResult(layersTask);
                }

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "task_id", task.TaskId },
                    { "generator_id", task.GeneratorId },
                    { "status", task.Status },
                    { "progress", task.Progress },
                    { "prompt", task.Prompt },
                    { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
                };

                if (!string.IsNullOrEmpty(task.ResultPath))
                    result["image_path"] = task.ResultPath;

                result["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);

                if (!string.IsNullOrEmpty(task.ErrorMessage))
                    result["error"] = task.ErrorMessage;

                if (task.EndTime.HasValue)
                {
                    result["end_time"] = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                    result["duration_seconds"] = (int)(task.EndTime.Value - task.StartTime).TotalSeconds;
                }

                if (task.Status == "generating")
                {
                    if (!string.IsNullOrEmpty(task.PlaceholderPath))
                        result["placeholder_path"] = task.PlaceholderPath;
                }

                return result;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateGameUiKitTool] Query error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error querying task status: {e.Message}" }
                };
            }
#else
            return new Dictionary<string, object>
            {
                { "success", false },
                { "message", "This tool only works in Unity Editor." }
            };
#endif
        }

        [ExecuteCustomTool.CustomTool("list_game_ui_kit_tasks", "List all active and recent game UI kit generation tasks")]
        public static object ListGameUiKitTasks(JObject parameters)
        {
#if UNITY_EDITOR
            try
            {
                // 单图步（Step 1 两 provider / frontier Step 2）在 ImageTaskTracker；
                // seedream Step 2（图层拆分）在 ImageLayersTaskTracker。两个 store 都要查。
                // 注意：两个 tracker 分别与 generate_image / generate_image_layers 共享，
                // 无法按工具名区分，只能按 generator id 过滤。
                ImageTaskTracker.CleanupCompletedTasks();
                ImageLayersTaskTracker.CleanupCompletedTasks();
                var taskList = new List<Dictionary<string, object>>();

                foreach (var task in ImageTaskTracker.GetAllTasks())
                {
                    if (task.GeneratorId != GeneratorId && task.GeneratorId != SeedreamProGeneratorId)
                        continue;
                    taskList.Add(BuildSingleImageTaskData(task));
                }

                foreach (var task in ImageLayersTaskTracker.GetAllTasks())
                {
                    if (task.GeneratorId != GenerateImageLayersTool.SeedreamGeneratorId)
                        continue;
                    taskList.Add(BuildLayersTaskData(task));
                }

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "count", taskList.Count },
                    { "tasks", taskList },
                    { "note", "game_ui_kit tasks share trackers with generate_image (frontier/seedream_pro screenshot steps) " +
                              "and generate_image_layers (seedream layer step). Tasks are not separately tagged by tool name." }
                };
            }
            catch (Exception e)
            {
                TJLog.LogError($"[GenerateGameUiKitTool] List error: {e}");
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "message", $"Error listing tasks: {e.Message}" }
                };
            }
#else
            return new Dictionary<string, object>
            {
                { "success", false },
                { "message", "This tool only works in Unity Editor." }
            };
#endif
        }

#if UNITY_EDITOR
        /// <summary>layers tracker 任务的查询结果（seedream Step 2）</summary>
        private static Dictionary<string, object> BuildLayersTaskResult(ImageLayersTaskTracker.ImageLayersTaskInfo task)
        {
            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "task_id", task.TaskId },
                { "generator_id", task.GeneratorId },
                { "status", task.Status },
                { "progress", task.Progress },
                { "prompt", task.Prompt },
                { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
            };

            if (!string.IsNullOrEmpty(task.Layer0Path))
            {
                // 兼容字段：底图路径（注意 layer 0 是底图，不是元素精灵）
                result["image_path"] = task.Layer0Path;
                result["layer_0_path"] = task.Layer0Path;
            }
            if (!string.IsNullOrEmpty(task.LayersFolder))
                result["layers_folder"] = task.LayersFolder;
            result["layer_count"] = task.LayerCount;

            result["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);

            if (!string.IsNullOrEmpty(task.ErrorMessage))
                result["error"] = task.ErrorMessage;

            if (task.EndTime.HasValue)
            {
                result["end_time"] = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");
                result["duration_seconds"] = (int)(task.EndTime.Value - task.StartTime).TotalSeconds;
            }

            if (task.Status == "generating" || task.Status == "recovering")
            {
                if (!string.IsNullOrEmpty(task.PlaceholderPath))
                    result["placeholder_path"] = task.PlaceholderPath;
            }

            if (task.Status == "completed" && !string.IsNullOrEmpty(task.Layer0Path))
            {
                var layerPaths = GenerateImageLayersTool.CollectLayerPaths(task.Layer0Path, task.LayerCount);
                result["layer_paths"] = layerPaths;
                result["layers_found"] = layerPaths.Count;
            }

            return result;
        }

        private static Dictionary<string, object> BuildSingleImageTaskData(ImageTaskTracker.ImageTaskInfo task)
        {
            var taskData = new Dictionary<string, object>
            {
                { "task_id", task.TaskId },
                { "generator_id", task.GeneratorId },
                { "status", task.Status },
                { "progress", task.Progress },
                { "prompt", task.Prompt },
                { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
            };

            if (!string.IsNullOrEmpty(task.ResultPath))
                taskData["image_path"] = task.ResultPath;

            taskData["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);

            if (!string.IsNullOrEmpty(task.ErrorMessage))
                taskData["error"] = task.ErrorMessage;

            if (task.EndTime.HasValue)
                taskData["end_time"] = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");

            return taskData;
        }

        private static Dictionary<string, object> BuildLayersTaskData(ImageLayersTaskTracker.ImageLayersTaskInfo task)
        {
            var taskData = new Dictionary<string, object>
            {
                { "task_id", task.TaskId },
                { "generator_id", task.GeneratorId },
                { "status", task.Status },
                { "progress", task.Progress },
                { "prompt", task.Prompt },
                { "layer_count", task.LayerCount },
                { "start_time", task.StartTime.ToString("yyyy-MM-dd HH:mm:ss") }
            };

            if (!string.IsNullOrEmpty(task.Layer0Path))
                taskData["layer_0_path"] = task.Layer0Path;
            if (!string.IsNullOrEmpty(task.LayersFolder))
                taskData["layers_folder"] = task.LayersFolder;

            taskData["preview_url"] = PreviewUrlHelper.GetPreviewUrl(task.PreviewUrl, task.BackendTaskId);

            if (!string.IsNullOrEmpty(task.ErrorMessage))
                taskData["error"] = task.ErrorMessage;

            if (task.EndTime.HasValue)
                taskData["end_time"] = task.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss");

            return taskData;
        }

        private static void ApplyGameUiKitParameters(DynamicGenerator generator, JObject parameters)
        {
            // quality is a fixedField in frontier-game-design config (defaults to "low").
            // Use SetExtraRawJsonField to override it AFTER ApplyFixedFields runs,
            // because ExtraRawJsonFields are applied last in BuildRequestJson.
            if (parameters["quality"] != null)
            {
                string quality = parameters["quality"].ToString();
                generator.SetExtraRawJsonField("quality", $"\"{quality}\"");
            }

            if (parameters["output_format"] != null)
                generator.SetParameter("outputFormat", parameters["output_format"].ToString());
        }

        private static string CreatePlaceholderTexture(string outputPath)
        {
            string placeholderPath;
            if (!string.IsNullOrEmpty(outputPath))
            {
                string dir = Path.GetDirectoryName(outputPath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir))
                    EnsureAssetDatabaseFolder(dir);
                placeholderPath = AssetDatabase.GenerateUniqueAssetPath(
                    Path.ChangeExtension(outputPath, ".png"));
            }
            else
            {
                if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
                    AssetDatabase.CreateFolder("Assets", "TJGenerators");
                if (!AssetDatabase.IsValidFolder("Assets/TJGenerators/History"))
                    AssetDatabase.CreateFolder("Assets/TJGenerators", "History");
                string uniqueName = "GameUIKit_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                placeholderPath = AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/History/" + uniqueName);
            }

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1f));
            tex.Apply();
            byte[] pngBytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            string absolutePath = PathUtils.ToAbsoluteAssetPath(placeholderPath);
            File.WriteAllBytes(absolutePath, pngBytes);
            PathUtils.ImportAssetAfterDiskWrite(placeholderPath);

            return placeholderPath;
        }

        private static void EnsureAssetDatabaseFolder(string folderPath)
        {
            folderPath = folderPath.Replace('\\', '/').TrimEnd('/');
            string[] parts = folderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        internal static void ApplyGameUiKitParametersInternal(DynamicGenerator generator, JObject parameters)
            => ApplyGameUiKitParameters(generator, parameters);
#endif
    }
}
