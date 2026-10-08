#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 中英文本地化系统：通过 L() 方法将中文字符串映射为英文，默认中文。
    /// 语言偏好存储在 EditorPrefs 中，切换时触发 OnLanguageChanged 事件。
    /// </summary>
    public static class TJGeneratorsL10n
    {
        public enum Language
        {
            Chinese = 0,
            English = 1
        }

        private const string EditorPrefsKey = "TJGenerators_Language";
        private const string LogTag = "[TJGeneratorsL10n]";

        private static Language _current;
        private static bool _languageLoaded;
        private static Dictionary<string, string> _translations;
        private static bool _translationsInitialized;

        /// <summary>
        /// 当前语言。首次访问时从 EditorPrefs 读取，默认为中文。
        /// </summary>
        public static Language Current
        {
            get
            {
                EnsureLanguageLoaded();
                return _current;
            }
        }

        /// <summary>当前是否为中文模式。</summary>
        public static bool IsChinese
        {
            get
            {
                EnsureLanguageLoaded();
                return _current == Language.Chinese;
            }
        }

        /// <summary>当前是否为英文模式。</summary>
        public static bool IsEnglish
        {
            get
            {
                EnsureLanguageLoaded();
                return _current == Language.English;
            }
        }

        /// <summary>
        /// 语言切换事件。窗口在 OnEnable 中订阅、OnDisable 中取消订阅，
        /// 收到事件后调用 Repaint() 刷新界面。
        /// </summary>
        public static event Action OnLanguageChanged;

        private static void EnsureLanguageLoaded()
        {
            if (_languageLoaded)
                return;
            _languageLoaded = true;
#if TUANJIE_1 || TUANJIE_2
            _current = (Language)EditorPrefs.GetInt(EditorPrefsKey, (int)Language.Chinese);
#else
            _current = (Language)EditorPrefs.GetInt(EditorPrefsKey, (int)Language.English);
#endif
            EditorApplication.quitting += () =>
            {
                EditorPrefs.SetInt(EditorPrefsKey, (int)_current);
            };
        }

        private static void EnsureInitialized()
        {
            if (!_translationsInitialized)
            {
                EnsureTranslations();
                _translationsInitialized = true;
            }
        }

        /// <summary>
        /// 设置当前语言并触发 OnLanguageChanged 事件。
        /// </summary>
        public static void SetLanguage(Language lang)
        {
            EnsureLanguageLoaded();
            _current = lang;
            EditorPrefs.SetInt(EditorPrefsKey, (int)lang);
            OnLanguageChanged?.Invoke();
            // 刷新菜单项的显示状态（验证函数会根据新语言返回不同的值）
            EditorApplication.delayCall += () => EditorApplication.RepaintHierarchyWindow();
            EditorApplication.delayCall += () => EditorApplication.RepaintProjectWindow();
        }

        /// <summary>
        /// 在中文模式返回原文；在英文模式查翻译表返回英文，未命中则返回原文。
        /// </summary>
        public static string L(string chinese)
        {
            if (string.IsNullOrEmpty(chinese))
                return chinese;

            EnsureLanguageLoaded();

            if (_current == Language.Chinese)
                return chinese;

            EnsureInitialized();

            if (_translations != null && _translations.TryGetValue(chinese, out var en))
                return en;

            return chinese;
        }

        /// <summary>
        /// 格式化翻译：先翻译模板再插值。
        /// 例如 L("检测到 {0} 个区域", count) 在英文模式下返回 "Detected {count} areas"
        /// </summary>
        public static string L(string template, params object[] args)
        {
            string resolved = L(template);
            return string.Format(resolved, args);
        }

        // ====================================================================
        //  翻译表
        // ====================================================================

        private static void EnsureTranslations()
        {
            if (_translations != null)
                return;

            _translations = new Dictionary<string, string>(StringComparer.Ordinal);

            // ---- 菜单项（tooltip 等，MenuItem 路径本身不翻译） ----
            _translations["使用 TJGenerators AI 生成3D模型"] = "Use TJGenerators AI to Generate 3D Model";
            _translations["使用 TJGenerators AI 生成天空盒"] = "Use TJGenerators AI to Generate Skybox";
            _translations["使用 TJGenerators AI 生成表面材质"] = "Use TJGenerators AI to Generate Material";
            _translations["使用 TJGenerators AI 生成2D精灵"] = "Use TJGenerators AI to Generate 2D Sprite";
            _translations["使用 TJGenerators AI 生成图片"] = "Use TJGenerators AI to Generate Image";
            _translations["使用 TJGenerators AI 生成2D精灵表序列帧"] = "Use TJGenerators AI to Generate 2D Spritesheet Sequence";
            _translations["使用 TJGenerators AI 生成音频"] = "Use TJGenerators AI to Generate Audio";
            _translations["使用 TJGenerators AI 生成2D动作序列帧"] = "Use TJGenerators AI to Generate 2D Action Sequence";
            _translations["使用 TJGenerators AI 生成3D模型到对应预制体"] = "Use TJGenerators AI to Generate 3D Model to Prefab";
            _translations["编辑该 TJGenerators 视频并查看历史记录"] = "Edit TJGenerators Video & View History";
            _translations["怎么用？查看使用文档"] = "How to use? View docs";

            // ---- Inspector / Header 按钮 ----
            _translations["✦ AI 生成"] = "✦ AI Generate";

            // ---- 通用 UI ----
            _translations["生成"] = "Generate";
            _translations["生成中..."] = "Generating...";
            _translations["生成模型"] = "Generate Model";
            _translations["生成中"] = "Generating";
            _translations["请先退出 Play Mode。\n\nAI 生成只能在编辑模式下使用，Play 模式下的资产变更会在退出后丢失。"] =
                "Exit Play Mode first.\n\nAI generation is only available in Edit mode. Asset changes made in Play mode are discarded on exit.";
            _translations["请先退出 Play Mode 再生成"] =
                "Exit Play Mode to generate";
            _translations["请先退出 Play Mode 再搜索"] =
                "Exit Play Mode to search";
            _translations["请先退出 Play Mode 再下载或放入场景"] =
                "Exit Play Mode to download or place in scene";
            _translations["请先退出 Play Mode 再搜索、下载或放入场景"] =
                "Exit Play Mode to search, download, or place in scene";
            _translations["文本提示词"] = "Prompt";
            _translations["在此处输入文本提示..."] = "Enter prompt here...";
            _translations["参考图片（可选）"] = "Ref Image (Opt.)";
            _translations["参考图片"] = "Ref Image";
            _translations["输入图片（必填）"] = "Input Image (required)";
            _translations["高级设置"] = "Advanced";
            _translations["未知模型"] = "Unknown Model";
            _translations["选择图片"] = "Select Image";
            _translations["用AI生成"] = "AI Generate";
            _translations["选择对象"] = "Select Object";
            _translations["选择类型"] = "Select Type";
            _translations["选择风格"] = "Select Style";
            _translations["全部"] = "All";
            _translations["更改"] = "Change";
            _translations["清除"] = "Clear";
            _translations["确认"] = "Confirm";
            _translations["取消"] = "Cancel";
            _translations["确定"] = "OK";
            _translations["提示"] = "Notice";
            _translations["请求失败"] = "Request Failed";

            // ---- 历史 ----
            _translations["暂无历史记录"] = "No History";
            _translations["第一次来？看看怎么用 →"] = "New here? See guide →";

            // ---- 资产库搜索 ----
            _translations["查询（每行一个关键词）"] = "Query (one keyword per line)";
            _translations["请输入查询关键词（每行一个）"] = "Enter keywords (one per line)";
            _translations["分类过滤（可选）"] = "Category Filter (Opt.)";
            _translations["打开 Unity 登录…"] = "Open Unity Login…";
            _translations["搜索中…"] = "Searching…";
            _translations["搜索"] = "Search";
            _translations["结果"] = "Results";
            _translations["无匹配结果。"] = "No results.";
            _translations["搜索结果将显示在这里"] = "Results will appear here";
            _translations["输入关键词并点击搜索按钮，将为您查找相关的3D资产和动画资源"] = "Enter keywords and click search to find 3D assets";
            _translations["描述详情"] = "Description";
            _translations["放入场景"] = "Place in Scene";
            _translations["已在项目中"] = "In Project";
            _translations["仅下载到项目"] = "Download Only";
            _translations["下载并放入场景"] = "Download & Place";
            _translations["资源已在项目中，将当前条目对应的 Prefab 放入场景"] = "Asset already in project, place the Prefab in scene";
            _translations["与当前条目共用同一资源包，无需重复下载"] = "Shares the same package, no need to re-download";

            // ---- 右键菜单 ----
            _translations["应用到当前预制体"] = "Apply to Prefab";
            _translations["在项目中显示模型"] = "Show in Project";
            _translations["在项目中显示"] = "Show in Project";
            _translations["在项目中显示音频"] = "Show in Project";
            _translations["在资源管理器中显示"] = "Show in Explorer";
            _translations["从历史记录中移除"] = "Remove from History";
            _translations["聚焦到预制体"] = "Focus Prefab";
            _translations["场景中无实例"] = "No Scene Instances";
            _translations["应用到当前图片"] = "Apply to Image";
            _translations["应用到当前天空盒"] = "Apply to Skybox";
            _translations["应用到当前音频"] = "Apply to Audio";
            _translations["应用到当前动画"] = "Apply to Animation";
            _translations["应用到当前视频"] = "Apply to Video";
            _translations["应用到场景天空盒"] = "Apply to Scene Skybox";
            _translations["保存为材质"] = "Save as Material";
            _translations["一键生成地形"] = "Generate Terrain";

            // ---- 图片窗口 ----
            _translations["地形高度图（生成后）"] = "Terrain Heightmap";
            _translations["输出最低"] = "Output Min";
            _translations["输出最高"] = "Output Max";
            _translations["地形最凹处对应高度图灰度下限"] = "Lower heightmap gray value for terrain's lowest point";
            _translations["地形最高处对应高度图灰度上限"] = "Upper heightmap gray value for terrain's highest point";
            _translations["导入本地图片…"] = "Import Image…";
            _translations["使用当前选中图片"] = "Use Selected Image";
            _translations["执行抠图并预览"] = "Cutout & Preview";
            _translations["恢复原图预览"] = "Restore Preview";
            _translations["切割并导出为 Sprite"] = "Slice & Export Sprite";
            _translations["更换图片"] = "Change Image";

            // ---- 图片切割窗口 ----
            _translations["检测区域"] = "Detect Areas";
            _translations["正在分析..."] = "Analyzing...";

            // ---- 天空盒窗口 ----
            _translations["天空盒预览"] = "Skybox Preview";

            // ---- 参考图窗口 ----
            _translations["模型:"] = "Model:";
            _translations["多视图生成"] = "Multi-View Generation";
            _translations["多视图生成结果预览"] = "Multi-View Result Preview";
            _translations["生成结果预览"] = "Result Preview";

            // ---- DynamicGenerator ----
            _translations["刷新模型列表"] = "Refresh Models";
            _translations["已选择文件:"] = "Selected File:";

            // ---- 模型选择器 ----
            _translations["选择模型"] = "Select Model";
            _translations["没有可用的选项"] = "No options available";
            _translations["没有图片？"] = "No image?";
            _translations["当前分类下没有选项"] = "No options in category";

            // ---- 材质模板 ----
            _translations["纹理走势模板生成器"] = "Texture Pattern Generator";
            _translations["模板列表:"] = "Template List:";
            _translations["生成所有缺失的模板"] = "Generate Missing Templates";
            _translations["重新生成所有模板"] = "Regenerate All";
            _translations["重新生成"] = "Regenerate";
            _translations["搜索："] = "Search:";
            _translations["选择选项"] = "Select Option";
            _translations["选择纹理走势"] = "Select Texture Pattern";

            // ---- Config 模型默认值 ----
            _translations["上传模型文件"] = "Upload Model File";
            _translations["上传多角度图片生成3D模型，正面必需，至少2张图片"] = "Upload multi-angle images to generate 3D model. Front view required, min 2 images.";
            _translations["选择要减面的OBJ文件"] = "Select OBJ to Decimate";            _translations["支持 FBX、OBJ 格式的3D模型文件"] = "Supports FBX, OBJ 3D model files";
            _translations["资产类型"] = "Asset Type";
            _translations["选择要生成的游戏资产类型"] = "Select asset type to generate";
            _translations["艺术风格"] = "Art Style";
            _translations["选择美术风格"] = "Select Art Style";
            _translations["材质模板"] = "Material Template";
            _translations["选择材质纹理模板"] = "Select Material Texture Template";

            // ---- Test Runner ----
            _translations["测试参数"] = "Test Parameters";
            _translations["图片路径"] = "Image Path";
            _translations["选择..."] = "Browse...";
            _translations["使用选中预制体"] = "Use Selected Prefab";
            _translations["创建并选择测试预制体"] = "Create & Select Test Prefab";
            _translations["运行测试"] = "Run Test";
            _translations["状态"] = "Status";
            _translations["进度"] = "Progress";
            _translations["任务 ID"] = "Task ID";
            _translations["本地 ID"] = "Local ID";
            _translations["模型路径"] = "Model Path";

            // ---- UI 切图 ----
            _translations["✦ UI切图使用指南"] = "✦ UI Slice Guide";

            // ---- 错误消息 ----
            _translations["动画绑定失败"] = "Rig Failed";
            _translations["系统无法为您生成的模型绑定动画骨骼，这通常是因为您的提示词描述的不是一个角色。\n\n❌ 无法制作动画：食物、车辆、建筑物、家具等物品\n✅ 可以制作动画：人类、动物、机器人等角色\n\n解决方案：请描述一个有头部、躯干、四肢的角色，而不是物品。"] =
                "The system cannot rig the generated model. This is usually because your prompt does not describe a character.\n\n❌ Cannot animate: food, vehicles, buildings, furniture\n✅ Can animate: humans, animals, robots\n\nSolution: Describe a character with head, torso, and limbs.";
            _translations["网络超时"] = "Network Timeout";
            _translations["生成请求超时，可能的原因：\n\n• 网络连接不稳定\n• 服务器负载过高\n• 模型生成时间过长\n\n建议稍后重试，或检查网络连接。"] =
                "Generation request timed out. Possible causes:\n\n• Unstable network\n• Server overload\n• Model generation too slow\n\nPlease retry later or check your network.";
            _translations["认证失败"] = "Auth Failed";
            _translations["API认证失败，请检查：\n\n• API密钥是否正确\n• 账户是否有足够的额度\n• 网络连接是否正常\n\n请联系管理员检查配置。"] =
                "API authentication failed. Check:\n\n• API key is correct\n• Account has sufficient credits\n• Network is connected\n\nPlease contact your administrator.";
            _translations["生成失败"] = "Generation Failed";
            _translations["模型生成失败，请稍后重试。\n\n如果问题持续存在，请联系技术支持。"] =
                "Generation failed, please retry.\n\nIf the issue persists, contact support.";
            _translations["生成过程中出现错误，请重试。\n\n如果问题持续存在，请检查网络连接或联系技术支持。"] =
                "An error occurred during generation.\n\nIf the issue persists, check your network or contact support.";
            _translations["登录权限检查失败，请确认编辑器左上角或者Hub内已登录"] =
                "Login check failed. Please sign in to the Editor or Hub.";

            // ---- DisplayDialog ----
            _translations["图片切割"] = "Image Slice";
            _translations["无法读取图片像素，请确保图片不是压缩格式。"] = "Cannot read image pixels. Ensure the image is not compressed.";
            _translations["无法读取图片像素。"] = "Cannot read image pixels.";
            _translations["无法读取图片文件。"] = "Cannot read the image file.";
            _translations["不支持的图片格式"] = "Unsupported Image Format";
            _translations["仅支持 png、jpg、jpeg 格式的图片。"] = "Only png, jpg, and jpeg images are supported.";
            _translations["未检测到可切割的区域，请调整参数后重试。"] = "No areas detected for slicing. Adjust parameters and retry.";
            _translations["确认替换"] = "Confirm Replace";
            _translations["清空历史记录"] = "Clear History";
            _translations["确定要清空所有 TJGenerators 生成历史记录吗？此操作不可撤销。"] = "Are you sure you want to clear all TJGenerators history? This cannot be undone.";
            _translations["清除轮询任务记录"] = "Clear Polling Task Records";
            _translations["确定要清除所有轮询中的生成任务记录吗？将清空本地中断任务与「生成中」占位符，并停止本地继续轮询。后端暂无取消接口，服务器上的任务可能仍会继续运行。"] =
                "Clear all in-progress polling task records? This removes local interrupted tasks and generating placeholders, and stops further local polling. There is no backend cancel API, so server-side jobs may keep running.";
            _translations["场景中没有该预制体的实例。"] = "No instances of this prefab in the scene.";
            _translations["请先选择一个目标预制体。"] = "Please select a target prefab first.";

            // ---- 上传相关 ----

            // ---- Debug 菜单 ----
            _translations["Token 为空，请先登录 Unity 账号。"] = "Token is empty. Please sign in to Unity first.";
            _translations["Access Token 为空，请确认编辑器已登录 Unity 账号"] = "Access Token is empty. Please sign in to Unity.";
            _translations["Token 已复制到剪贴板。"] = "Token copied to clipboard.";
            _translations["所有配置缓存已清除。请关闭并重新打开 TJGenerators 窗口以加载最新配置。"] = "All config cache cleared. Please reopen TJGenerators windows to load the latest config.";

            // ---- 多视图 ----
            _translations["将按顺序自动生成："] = "Will auto-generate in order: ";
            _translations["（共"] = "(total ";
            _translations["张）"] = " images)";

            // ---- 场景实例 ----
            _translations["场景中的实例 ("] = "Scene Instances (";
            _translations[")"] = ")";
            _translations["聚焦:"] = "Focus:";

            // ---- 已生成 ----
            _translations["已生成: "] = "Generated: ";
            _translations[" / "] = " / ";
            _translations["错误:"] = "Error:";

            // ---- 窗口标题 ----
            _translations["TJGenerators 参考图生成 (多视图)"] = "TJGenerators Reference Image (Multi-View)";
            _translations["TJGenerators 参考图生成"] = "TJGenerators Reference Image";
            _translations["TJGenerators 3D模型"] = "TJGenerators 3D Model";
            _translations["TJGenerators 图片生成"] = "TJGenerators Image";
            _translations["TJGenerators 音频生成"] = "TJGenerators Audio";
            _translations["TJGenerators 天空盒生成"] = "TJGenerators Skybox";
            _translations["TJGenerators 2D动作序列帧"] = "TJGenerators 2D Action Sequence";
            _translations["TJGenerators 精灵生成"] = "TJGenerators Sprite";
            _translations["TJGenerators 材质生成"] = "TJGenerators Material";
            _translations["TJGenerators 视频生成"] = "TJGenerators Video";
            _translations["Tuanjie AI 资产库搜索"] = "Tuanjie AI Asset Search";

            // ---- 窗口标题格式后缀 ----
            _translations[" - "] = " - ";

            // ---- DynamicGenerator 补充 ----
            _translations["生成模式"] = "Generation Mode";
            _translations["文生与图生"] = "Text & Image";
            _translations["后处理"] = "Post Process";
            _translations["添加动作"] = "Add Motion";
            _translations["动作描述"] = "Motion Description";
            _translations["输入动作，如：walk、run、jump..."] = "Enter motion, e.g.: walk, run, jump...";
            _translations["文本提示词（可选）"] = "Prompt (Opt.)";
            _translations["生成参考图"] = "Generate Reference Image";
            _translations["绑骨模型"] = "Rigged Model";
            _translations["从当前工程的 Assets 中选择 FBX / OBJ。"] = "Select FBX/OBJ from Assets";
            _translations["Assets 内未找到 FBX / OBJ，请将模型导入工程后刷新本窗口。"] = "No FBX/OBJ found in Assets. Import models and refresh.";
            _translations["支持 FBX、OBJ 格式的 3D 模型文件"] = "Supports FBX, OBJ 3D model files";
            _translations["正面图片是必需的，且文件必须存在。"] = "Front image is required and must exist.";
            _translations["请上传多视图图片"] = "Please upload multi-view images";
            _translations["请上传输入图片。"] = "Please upload an input image.";
            _translations["选择{0}图片"] = "Select {0} Image";
            _translations["至少需要{0}张图片进行多视图生成"] = "At least {0} images required for multi-view";
            _translations["多视图至少需要{0}张图片"] = "Multi-view needs at least {0} images";
            _translations["已勾选添加动作，请输入动作描述"] = "Motion is enabled, please enter motion description";
            _translations["请选择要绑骨的模型文件"] = "Please select a model file for rigging";
            _translations["选择的文件不存在"] = "Selected file does not exist";
            _translations["请输入提示词"] = "Please enter a prompt";
            _translations["请输入提示词或上传图片"] = "Please enter a prompt or upload an image";
            _translations["请输入提示词或上传有效的参考图片"] = "Please enter a prompt or upload a valid reference image";

            // ---- Sprite 窗口补充 ----
            _translations["目标材质"] = "Target Material";
            _translations["目标精灵"] = "Target Sprite";
            _translations["目标天空盒"] = "Target Skybox";
            _translations["目标音频"] = "Target Audio";
            _translations["未选择"] = "Not Selected";
            _translations["未绑定（生成后自动创建材质）"] = "Unassigned (auto-create after generation)";
            _translations["未绑定（生成时自动创建）"] = "Unassigned (auto-create on generation)";
            _translations["内容类型"] = "Content Type";
            _translations["材质预设："] = "Material Preset:";
            _translations["选择预设"] = "Select Preset";
            _translations["纹理走势"] = "Texture Pattern";
            _translations["选择模板"] = "Select Template";
            _translations["风格状态："] = "Style State:";
            _translations["选择材质预设"] = "Select Material Preset";
            _translations["选择风格状态"] = "Select Style State";
            _translations["材质预览"] = "Material Preview";
            _translations["精灵预览"] = "Sprite Preview";
            _translations["尚未生成\r\n生成后预览将在此处显示"] = "Not yet generated\nPreview will appear here after generation";
            _translations["尚未生成天空盒\n生成后预览将在此处显示"] = "Skybox not yet generated\nPreview will appear here after generation";
            _translations["应用到当前材质"] = "Apply to Material";
            _translations["应用到当前精灵"] = "Apply to Sprite";

            // ---- Skybox 窗口补充 ----
            _translations["错误"] = "Error";
            _translations["当前宿主不支持触发生成。"] = "The current host does not support triggering generation.";
            _translations["该历史记录的纹理文件不存在，可能已被删除。"] = "The texture file for this history record does not exist.";
            _translations["请先绑定或创建目标天空盒资产。"] = "Please bind or create a target skybox asset first.";
            _translations["确定要将选中的历史天空盒应用到 {0} 吗？"] = "Apply selected skybox to {0}?";
            _translations["应用失败: "] = "Apply failed: ";
            _translations["请先选择模型并输入文本提示词"] = "Please select a model and enter a prompt";
            _translations["请先生成天空盒"] = "Please generate a skybox first";
            _translations["保存天空盒材质"] = "Save Skybox Material";
            _translations["选择保存位置"] = "Select Save Location";

            // ---- Music 窗口补充 ----
            _translations["未找到可用的文生音频生成器，请检查 GeneratorConfig.json 中的 musicGenerators"] = "No audio generator found. Check musicGenerators in config.";
            _translations["音频文件不存在，可能已被删除。"] = "Audio file does not exist.";
            _translations["请先创建或选择目标音频资产。"] = "Please create or select a target audio asset first.";
            _translations["确定将选中的音频应用到当前目标「{0}」吗？"] = "Apply selected audio to target \"{0}\"?";
            _translations["已将历史音频应用到"] = "Applied historical audio to";
            _translations["应用失败（详见控制台）。"] = "Apply failed (see console).";
            _translations["应用失败"] = "Apply failed";
            _translations["目标音频无效"] = "Invalid target audio";
            _translations["源音频文件不存在"] = "Source audio file does not exist";

            // ---- Image 窗口补充 ----
            _translations["在右侧历史记录中选中对应 PNG 后，应用后处理并创建场景地形。"] = "Select the PNG in history, then apply post-processing and create terrain.";
            _translations["后处理：Median 3x3 去尖刺（散点离群点）"] = "Post: Median 3x3 Despike";
            _translations["后处理：高斯模糊平滑"] = "Post: Gaussian Blur";
            _translations["高度重映射（类似 Terrain Tools · Height Remap）"] = "Height Remap (like Terrain Tools)";
            _translations["百分位拉伸（去掉极暗/极亮离群点再起有效对比）"] = "Percentile Stretch";
            _translations["低端截断"] = "Low Cutoff";
            _translations["低于该百分位的亮度视作海平面一端，类似压低海底噪声"] = "Brightness below this percentile treated as sea level baseline, similar to suppressing low terrain noise";
            _translations["高端截断"] = "High Cutoff";
            _translations["高于该百分位的亮度视作山顶一端"] = "Brightness above this percentile treated as mountain peak";
            _translations["高度曲线 Gamma"] = "Height Curve Gamma";
            _translations["1 = 线性；小于 1 中间调抬高（更陡）；大于 1 更平（更多平原）"] = "1=linear; <1 steeper; >1 flatter";
            _translations["输出垂直范围（归一化高度映射到 [最低, 最高]）"] = "Output vertical range";
            _translations["确定将选中的图片应用到当前目标「{0}」吗？"] = "Apply selected image to target \"{0}\"?";
            _translations["确定要将历史模型应用到 {0} 吗？"] = "Apply history model to {0}?";
            _translations["确定要将选中的历史应用到 {0} 吗？"] = "Apply selected history to {0}?";

            // ---- Image 切割补充 ----
            _translations["图片切割 - {0}"] = "Image Slice - {0}";
            _translations["检测到 {0} 个区域"] = "Detected {0} areas";

            // ---- 窗口标题格式 ----
            _translations["TJGenerators 3D模型 - {0}"] = "TJGenerators 3D Model - {0}";
            _translations["TJGenerators 3D模型 -"] = "TJGenerators 3D Model -";
            _translations["TJGenerators 图片 - {0}"] = "TJGenerators Image - {0}";
            _translations["TJGenerators 音频 - {0}"] = "TJGenerators Audio - {0}";
            _translations["TJGenerators 天空盒 - {0}"] = "TJGenerators Skybox - {0}";
            _translations["TJGenerators 2D动作序列帧 - {0}"] = "TJGenerators 2D Action Sequence - {0}";
            _translations["TJGenerators 序列帧"] = "TJGenerators Sequence";
            _translations["TJGenerators 精灵 - {0}"] = "TJGenerators Sprite - {0}";
            _translations["TJGenerators 材质 - {0}"] = "TJGenerators Material - {0}";
            _translations["TJGenerators 视频 - {0}"] = "TJGenerators Video - {0}";

            // ---- TestRunner 补充 ----
            _translations["生成器"] = "Generator";
            _translations["提示词"] = "Prompt";
            _translations["优先使用图片"] = "Prefer Image";
            _translations["目标预制体（可选）"] = "Target Prefab (Opt.)";
            _translations["不指定目标预制体时，会自动创建并绑定新的预制体。"] = "If no prefab is specified, a new one will be auto-created.";

            // ---- 资产搜索补充 ----
            _translations["点数："] = "Credits: ";
            _translations["点数：--"] = "Credits: --";
            _translations["暂无预览"] = "No Preview";
            _translations["加载中…"] = "Loading…";
            _translations["请至少输入一行关键词。"] = "Enter at least one keyword.";
            _translations["暂无描述"] = "No Description";
            _translations["结果（共 {0} 条）"] = "Results ({0} total)";

            // ---- MaterialTemplate 补充 ----
            _translations["确定要重新生成所有模板吗？这将覆盖已有的模板图。\n\n生成过程会顺序执行，请耐心等待。"] = "Regenerate all templates? This will overwrite existing ones.\n\nGeneration runs sequentially, please wait.";

            // ---- ImageSlice 补充 ----
            _translations["背景模式"] = "BG Mode";
            _translations["Alpha 阈值"] = "Alpha Threshold";
            _translations["颜色容差"] = "Color Tolerance";
            _translations["最小区域(像素)"] = "Min Area (px)";
            _translations["留白(像素)"] = "Padding (px)";
            _translations["自动设为 Sprite"] = "Auto Set as Sprite";

            // ---- HelpBox 补充 ----
            _translations["未找到可用的图片生成器，请检查配置"] = "No image generator found. Check config.";
            _translations["未找到可用的生成器，请检查配置"] = "No generator found. Check config.";
            _translations["未找到可用的 2D 序列帧生成器，请检查 GeneratorConfig.json 中的 spriteSequenceGenerators"] = "No sprite sequence generator found. Check spriteSequenceGenerators in config.";
            _translations["未找到可用的视频生成器，请检查 GeneratorConfig.json 中的 videoGenerators"] = "No video generator found. Check videoGenerators in config.";
            _translations["注意：生成过程会顺序执行，每个模板之间间隔20秒以避免请求过于频繁。请耐心等待。"] = "Note: Generation runs sequentially with 20s intervals. Please wait.";

            // ---- 额外补充 ----
            _translations["资产库"] = "Asset Library";
            _translations["正在搜索…"] = "Searching…";
            _translations["选择要导入的图片"] = "Select Image to Import";
            _translations["成功切割并导出 {0} 张图片\n\n输出目录：{1}"] = "Successfully sliced and exported {0} images\n\nOutput: {1}";
            _translations["导出失败"] = "Export Failed";

            // ---- 状态字符串（多窗口通用）----
            _translations["恢复中..."] = "Restoring...";
            _translations["准备中..."] = "Preparing...";
            _translations["完成"] = "Done";
            _translations["转换中..."] = "Converting...";
            _translations["生成中 {0}%"] = "Generating {0}%";

            // ---- 通用标签 ----
            _translations["目标预制体"] = "Target Prefab";
            _translations["目标图片"] = "Target Image";
            _translations["目标动画"] = "Target Animation";
            _translations["目标视频"] = "Target Video";
            _translations["无预览"] = "No Preview";
            _translations["其他"] = "Other";
            _translations["自定义"] = "Custom";
            _translations["参数设置"] = "Parameters";
            _translations["预览"] = "Preview";
            _translations["语音角色"] = "Voice Character";
            _translations["无波形预览"] = "No Waveform Preview";
            _translations["抠图与切割"] = "Cutout & Slice";
            _translations["未绑定（生成到历史）"] = "Unassigned (output to history only)";
            _translations["输入关键词搜索..."] = "Search by keyword...";
            _translations["生成图片"] = "Generate Image";
            _translations["使用此图片"] = "Use This Image";
            _translations["在右侧预览区选中历史图片后，可抠图、切割并导出 Sprite 动画；也可直接导入本地图片。"] = "Select a history image on the right to cutout, slice and export Sprite animation; or import a local image.";
            _translations["（手动导入图片）"] = "(Manual Import)";

            // ---- 弹窗/错误消息 ----
            _translations["导入失败"] = "Import Failed";
            _translations["图片文件不存在，无法加入历史。"] = "Image file does not exist, cannot add to history.";
            _translations["该历史记录的图片文件不存在。"] = "The image file for this history record does not exist.";
            _translations["无法读取选中的历史图片。"] = "Cannot read the selected history image.";
            _translations["模型文件不存在，可能已被删除。"] = "Model file does not exist, may have been deleted.";
            _translations["动画文件不存在，可能已被删除。"] = "Animation file does not exist, may have been deleted.";
            _translations["视频文件不存在，可能已被删除。"] = "Video file does not exist, may have been deleted.";
            _translations["请输入文本提示词。"] = "Please enter a text prompt.";
            _translations["未选择可用的生成模型。"] = "No available generation model selected.";
            _translations["地形生成失败"] = "Terrain Generation Failed";
            _translations["无法生成地形"] = "Cannot Generate Terrain";
            _translations["请选择由「Unity 地形高度图」模板生成且已完成的 PNG 历史记录。"] = "Please select a completed PNG from the \"Unity Terrain Heightmap\" template.";
            _translations["已将历史图片应用到"] = "Applied history image to";
            _translations["已生成图片并复制到"] = "Image generated and copied to";
            _translations["目标图片无效"] = "Invalid target image";
            _translations["源图片文件不存在"] = "Source image file does not exist";
            _translations["缺少序列帧指令配置"] = "Missing Sequence Instructions";
            _translations["请先选择模型"] = "Please select a model first";
            _translations["请先在历史中选中由本模板生成的已完成 PNG。"] = "Please select a completed PNG generated by this template in history.";
            _translations["未找到可处理的图片：请先在右侧历史中选一张，或点击上方「导入本地图片 / 使用当前选中图片」。"] = "No image to process: select one from history on the right, or click \"Import Image / Use Selected Image\" above.";
            _translations["无法创建或绑定目标视频占位资产，请确认 Project 中选中的目录可写。"] = "Cannot create or bind target video asset. Ensure the selected Project directory is writable.";
            _translations["应用失败: {0}"] = "Apply failed: {0}";

            // ---- 带占位符的模板 ----
            _translations["场景({0})"] = "Scene({0})";
            _translations["导出失败：{0}"] = "Export Failed: {0}";
            _translations["分析失败：{0}"] = "Analysis failed: {0}";
            _translations["切割并导出 ({0} 张)"] = "Slice & Export ({0} imgs)";
            _translations["点数：{0}"] = "Credits: {0}";
            _translations["场景中的实例 ({0})"] = "Scene Instances ({0})";
            _translations["聚焦: {0}"] = "Focus: {0}";
            _translations["应用失败：{0}"] = "Apply failed: {0}";
            _translations["共{0}个模型 ｜已收藏{1}个"] = "{0} models | {1} favorites";
            _translations["共{0}个选项"] = "{0} options";

            // ---- 图片切割 ----
            _translations["自动检测"] = "Auto Detect";
            _translations["透明背景"] = "Transparent BG";
            _translations["纯色背景"] = "Solid Color BG";
            _translations["点击选择图片或将图片拖入此区域"] = "Click to select or drag image here";

            // ---- 图片窗口补充 ----
            _translations["TJGenerators 2D精灵表序列帧"] = "TJGenerators 2D Spritesheet Sequence";
            _translations["提示词模板不可用"] = "Prompt Template Unavailable";
            _translations["当前模型未配置提示词模板选项（options 为空）"] = "Current model has no prompt template options configured.";
            _translations["选择提示词"] = "Select Prompt";
            _translations["提示词模板"] = "Prompt Template";
            _translations["模糊强度 (σ)"] = "Blur Strength (σ)";
            _translations["绿幕容差"] = "Chroma Tolerance";
            _translations["边缘羽化"] = "Edge Feather";
            _translations["切割列数"] = "Slice Columns";
            _translations["切割行数"] = "Slice Rows";
            _translations["动画 FPS"] = "Animation FPS";
            _translations["动画循环 (Loop)"] = "Loop";
            _translations["预览图中的红色线条为切割线。"] = "Red lines in the preview indicate slice boundaries.";
            _translations["未找到或无法读取 SpriteSheetSequenceProfiles.json。\n\n请确认包内含 Editor/Config/SpriteSheetSequenceProfiles.json，或通过 Package Manager 正确安装 cn.tuanjie.ai.generators。"] = "Cannot find or read SpriteSheetSequenceProfiles.json.\n\nEnsure the package contains Editor/Config/SpriteSheetSequenceProfiles.json.";
            _translations["序列帧模板不可用：请检查 SpriteSheetSequenceProfiles.json 中的 profiles 与 defaultProfileId 是否有效。"] = "Sequence template unavailable: check profiles and defaultProfileId in SpriteSheetSequenceProfiles.json.";
            _translations["无法根据当前模板构建序列帧指令包（frontier_sequence_envelope），请检查配置文件。"] = "Cannot build sequence instruction package from the current template. Check config.";
            _translations["当前 profile 的 instructions 为空或缺失，请编辑 SpriteSheetSequenceProfiles.json 填写完整指令。"] = "Current profile's instructions are empty or missing. Edit SpriteSheetSequenceProfiles.json.";
            _translations["仅支持绑定 .jpg / .jpeg / .png 的图片资产。\r\n\r\n建议先创建「生成图片」新资产。"] = "Only supports .jpg / .jpeg / .png image assets.\n\nCreate a new \"Generate Image\" asset first.";
            _translations["仅支持绑定 .jpg / .jpeg / .png 的图片资产。\r\n\r\n建议先通过菜单创建新图片资产。"] = "Only supports .jpg / .jpeg / .png image assets.\n\nCreate a new image asset via menu first.";

            // ---- Sprite 窗口补充 ----
            _translations["请输入文本提示词，或选择纹理走势 / 上传材质模板图片"] = "Enter a prompt, or select a texture pattern / upload a material template image";
            _translations["请先选择模型，并输入文本提示词或选择纹理走势 / 上传材质模板图片。"] = "Please select a model and enter a prompt or select a texture pattern / upload a material template image.";
            _translations["请先选择模型，并输入文本提示词或上传参考图片。"] = "Please select a model and enter a prompt or upload a reference image.";
            _translations["选择参考图片"] = "Select Reference Image";
            _translations["未找到可用的 Material 生成器，请检查 GeneratorConfig.json 中的 materialGenerators"] = "No material generator found. Check materialGenerators in config.";
            _translations["未找到可用的 Sprite 生成器，请检查 GeneratorConfig.json 中的 spriteGenerators"] = "No sprite generator found. Check spriteGenerators in config.";
            _translations["纹理图片不存在"] = "Texture Image Not Found";
            _translations["请先创建或选择目标动画资产。"] = "Please create or select a target animation asset first.";
            _translations["请先绑定或创建目标精灵资产。"] = "Please bind or create a target sprite asset first.";

            // ---- 3D 模型窗口补充 ----
            _translations["TJGenerators 材质模板生成"] = "TJGenerators Material Template Generator";
            _translations["模板目录: {0}"] = "Template Dir: {0}";
            _translations["当前状态: {0}"] = "Status: {0}";
            _translations["正在生成: {0}"] = "Generating: {0}";

            // ---- 参考图窗口补充 ----
            _translations["正面"] = "Front";
            _translations["左侧"] = "Left";
            _translations["背面"] = "Back";
            _translations["右侧"] = "Right";
            _translations["没有可用的图片生成器配置"] = "No available image generator configuration";
            _translations["(无可用模型)"] = "(No model available)";
            _translations["输入提示词生成多视图参考图"] = "Enter a prompt to generate multi-view reference images";
            _translations["输入提示词生成参考图"] = "Enter a prompt to generate a reference image";
            _translations["正在生成图片，请稍候..."] = "Generating image, please wait...";
            _translations["描述你想要生成的音乐风格、情绪或场景..."] = "Describe the music style, mood or scene to generate...";

            // ---- 资产搜索补充 ----
            _translations["已导出 {0} 张 Sprite。\n路径：{1}"] = "Exported {0} Sprites.\nPath: {1}";
            _translations["已导出 {0} 张 Sprite，并创建动画文件。\nSprite路径：{1}\n动画路径：{2}"] = "Exported {0} Sprites and created animation.\nSprite path: {1}\nAnimation path: {2}";

            // ---- AIReferenceImageWindow ----
            _translations["将按顺序自动生成：{0}（共{1}张）"] = "Will auto-generate in order: {0} ({1} images)";
            _translations["正在生成多视图图片 ({0}/{1}) - {2}..."] = "Generating multi-view ({0}/{1}) - {2}...";
            _translations["请求失败: {0}"] = "Request failed: {0}";
            _translations["图片下载失败: {0}"] = "Image download failed: {0}";
            _translations["生成失败: {0}"] = "Generation failed: {0}";
            _translations["生成失败: 无法从响应中提取图片URL"] = "Generation failed: cannot extract image URL from response";
            _translations["未知错误"] = "Unknown error";
            _translations["缺少可用的参考图（本地文件或图片 URL），无法继续生成侧视/背视。请确认正面已生成成功且接口返回了地址。"] =
                "Missing reference image (local file or URL). Cannot continue side/back views. Ensure the front view succeeded and the API returned a URL.";
            _translations["正面图未返回可用的图片 URL，无法与后续视角对齐，已中止。"] =
                "Front view did not return a valid image URL. Cannot align subsequent views. Aborted.";
            _translations["当前视角未返回图片 URL，多视图链已中止。"] =
                "Current view did not return an image URL. Multi-view chain aborted.";

            // ---- MaterialTemplate 状态补充 ----
            _translations["等待 {0} 秒后继续..."] = "Waiting {0}s before continuing...";
            _translations["提示词为空"] = "Prompt is empty";
            _translations["无法获取认证令牌，请确保已登录 Unity"] = "Cannot get auth token. Please sign in to Unity.";
            _translations["无法获取任务ID"] = "Cannot get task ID";
            _translations["生成超时"] = "Generation timed out";
            _translations["下载的纹理为空"] = "Downloaded texture is empty";
            _translations["响应中无 task_id 且非同步完成状态"] = "No task_id in response and not a synchronous completion";
            _translations["保存图片失败: {0}"] = "Save image failed: {0}";
            _translations["下载失败: {0}"] = "Download failed: {0}";

            // ---- TerrainHeightmapPostProcessor ----
            _translations["文件不存在"] = "File does not exist";
            _translations["无法解码图片"] = "Cannot decode image";
            _translations["图片尺寸无效"] = "Invalid image dimensions";
            _translations["解析响应失败: {0}"] = "Parse response failed: {0}";
            _translations["请求过于频繁 (429)"] = "Too many requests (429)";
            _translations["服务器错误 (500): {0}"] = "Server error (500): {0}";
            _translations["请求失败 ({0}): {1}"] = "Request failed ({0}): {1}";
            _translations["生成中 ({0}/{1}) - {2}"] = "Generating ({0}/{1}) - {2}";
            _translations["重试中 ({0}/{1})，等待 {2} 秒..."] = "Retrying ({0}/{1}), waiting {2}s...";
            _translations["纹理走势 '{0}' 的图片尚未生成。\r\n\r\n请通过菜单 'AI/开发/生成纹理走势模板图' 生成纹理图片。"] = "Texture pattern '{0}' image not yet generated.\n\nGenerate via menu 'AI/Dev/Generate Texture Pattern Templates'.";
            _translations["拖拽旋转 | 滚轮缩放 | 双击重置"] = "Drag to rotate | Scroll to zoom | Double-click to reset";
            _translations["点击上传图片"] = "Click to upload image";
            _translations["点击上传图片（支持JPG、PNG格式）"] = "Click to upload (JPG, PNG supported)";

            // ---- Pipeline / Service 补充 ----
            _translations["上传中..."] = "Uploading...";
            _translations["下载中..."] = "Downloading...";
            _translations["提交绑骨任务..."] = "Submitting rig task...";
            _translations["下载绑骨模型..."] = "Downloading rigged model...";
            _translations["提交动作生成..."] = "Submitting motion generation...";
            _translations["下载动作模型..."] = "Downloading motion model...";
            _translations["创建动画控制器..."] = "Creating animator...";
            _translations["获取认证token失败: {0}"] = "Failed to get auth token: {0}";
            _translations["未登录，请通过Unity编辑器左上角或Unity Hub登录后重试"] = "Not signed in. Please sign in via the Editor or Hub.";
            _translations["认证失败，请重新登录Unity账号"] = "Auth failed. Please re-sign in to Unity.";
            _translations["请求频率过高，请稍后重试"] = "Rate limit exceeded. Please retry later.";
            _translations["无法将 {0} 导入为 AudioClip。"] = "Cannot import {0} as AudioClip.";
            _translations["下载中 ({0}/{1})..."] = "Downloading ({0}/{1})...";
            _translations["任务已取消: {0}"] = "Task cancelled: {0}";
            _translations["任务已取消"] = "Task cancelled";
            _translations["任务失败: {0}"] = "Task failed: {0}";
            // Domain reload / 任务中断错误
            _translations["生成因域重载中断且后端任务记录已丢失，请重新生成。"] =
                "Generation was interrupted (domain reload) and the backend task record was lost. Please re-generate.";
            _translations["生成器 '{0}' 未配置 API endpoint"] = "Generator '{0}' has no API endpoint configured";
            _translations["服务器响应格式异常: {0}"] = "Invalid server response: {0}";
            _translations["提交失败 (HTTP {0}): {1}"] = "Submit failed (HTTP {0}): {1}";
            _translations["网络请求失败: {0}"] = "Network request failed: {0}";
            _translations["提交时发生意外错误: {0}"] = "Unexpected error during submission: {0}";
            _translations["请求参数错误: {0}"] = "Invalid request params: {0}";
            _translations["等待中..."] = "Pending...";
            _translations["处理中..."] = "Processing...";
            _translations["已完成"] = "Completed";
            _translations["失败"] = "Failed";
            _translations["认证token为空，请确保已登录Unity"] = "Auth token is empty. Please sign in to Unity.";
            _translations["模型"] = "Model";
            _translations["请求超时，任务可能仍在后端运行。重新打开窗口可继续等待。"] = "Request timed out. The task may still be running on the backend. Reopen the window to continue waiting.";
            _translations["绑骨"] = "Rigging";
            _translations["动作生成"] = "Motion Generation";
            _translations["输入错误"] = "Input Error";
            _translations["响应数据无效"] = "Invalid response data";
            _translations["生成内容可能涉及敏感信息，请修改后重试"] = "Generated content may contain sensitive info. Please modify and retry.";
            _translations["拦截原因"] = "Rejection reason";
            _translations["未找到模型下载URL"] = "Model download URL not found";
            _translations["未找到纹理资产下载URL"] = "Texture asset download URL not found";
            _translations["无法确定纹理资产保存路径"] = "Cannot determine texture asset save path";
            _translations["'output_path' 不能为空。"] = "'output_path' is empty.";
            _translations["'output_path' 必须是 Assets 相对路径（Assets/...），或位于工程 Assets 目录下的绝对路径。收到：'{0}'"] =
                "'output_path' must be an Assets-relative path (Assets/...) or an absolute path under the project Assets folder. Got: '{0}'";
            _translations["'output_path' 必须解析到工程 Assets 目录下。收到：'{0}'"] =
                "'output_path' must resolve under the project Assets folder. Got: '{0}'";
            _translations["'output_path' 无法转换为 Assets 相对路径。收到：'{0}'"] =
                "'output_path' could not be converted to an Assets-relative path. Got: '{0}'";
            _translations["下载的纹理数据为空"] = "Downloaded texture data is empty";
            _translations["未找到序列帧下载URL"] = "Sequence frame download URL not found";
            _translations["下载的帧数据为空"] = "Downloaded frame data is empty";
            _translations["未生成任何帧图片"] = "No frame images generated";
            _translations["导入帧图片失败（未加载到 Sprite）"] = "Failed to import frame images (Sprite not loaded)";
            _translations["创建 AnimationClip 失败"] = "Failed to create AnimationClip";
            _translations["未找到音频下载URL"] = "Audio download URL not found";
            _translations["无法确定音频保存路径（占位未创建）"] = "Cannot determine audio save path (placeholder not created)";
            _translations["下载的音频数据为空"] = "Downloaded audio data is empty";
            _translations["未找到视频下载URL"] = "Video download URL not found";
            _translations["无法确定视频保存路径"] = "Cannot determine video save path";
            _translations["下载的视频数据为空"] = "Downloaded video data is empty";
            _translations["解压ZIP文件失败或未找到模型文件"] = "Failed to extract ZIP or model file not found";
            _translations["无效响应"] = "Invalid response";
            _translations["轮询超时"] = "Polling timed out";
            _translations["轮询超时，任务可能仍在后端运行。重新打开窗口可继续等待。"] = "Polling timed out. Task may still be running. Reopen window to continue.";
            _translations["超时"] = "Timeout";
            _translations["动画绑定失败：您的提示词描述的可能不是一个角色。请确保描述的是有身体结构的角色（如人类、动物、机器人），而不是物品（如食物、车辆、建筑）。"] = "Rigging failed: your prompt may not describe a character. Ensure it describes a character with body structure (human, animal, robot), not an object (food, vehicle, building).";
            _translations["请求参数错误：请检查您的输入是否符合要求，特别是提示词内容和格式。"] = "Invalid request params: check your input, especially prompt content and format.";
            _translations["请求频率过高：API调用次数超出限制，请稍后重试。"] = "Rate limit exceeded. Please retry later.";
            _translations["认证失败：API密钥可能无效或账户配额不足，请检查配置。"] = "Auth failed: API key may be invalid or account quota insufficient. Check config.";
            _translations["— 选择项目内模型 —"] = "— Select Project Model —";
            _translations["下载失败"] = "Download Failed";
            _translations["下载音频失败"] = "Audio Download Failed";
            _translations["下载视频失败"] = "Video Download Failed";
            _translations["下载模型失败"] = "Model Download Failed";
            _translations["源文件不存在"] = "Source file does not exist";
            _translations["无法启动 ffmpeg 进程"] = "Cannot start ffmpeg process";
            _translations["支持png/jpg/jpeg，文件大小最大不超过10M，分辨率最低要求128*128，最高限制4096*4096"] = "Supports png/jpg/jpeg, max 10M, min 128x128, max 4096x4096";
            _translations["最多可选择 {0} 张参考图片"] = "Max {0} reference images allowed";

            // ---- GeneratorConfig.json label/tooltip ----
            _translations["0 - Idle 待机"] = "0 - Idle";
            _translations["0 表示不裁剪"] = "0 = no crop";
            _translations["1 - Walking 行走"] = "1 - Walking";
            _translations["105 - Triple Combo 三连击"] = "105 - Triple Combo";
            _translations["125 - Spell Cast 施法"] = "125 - Spell Cast";
            _translations["156 - Stand Dodge 闪避"] = "156 - Dodge";
            _translations["16 - Run Fast 快跑"] = "16 - Fast Run";
            _translations["207 - Roundhouse Kick 回旋踢"] = "207 - Roundhouse Kick";
            _translations["22 - Funny Dance 搞笑舞"] = "22 - Funny Dance";
            _translations["224 - Archery Shot 射箭"] = "224 - Archery Shot";
            _translations["242 - Charged Slash 蓄力劈"] = "242 - Charged Slash";
            _translations["4 - Attack 攻击"] = "4 - Attack";
            _translations["452 - Backflip 后空翻"] = "452 - Backflip";
            _translations["466 - Jump 跳跃"] = "466 - Jump";
            _translations["509 - Sprint 冲刺"] = "509 - Sprint";
            _translations["568 - Swim 游泳"] = "568 - Swim";
            _translations["7 - Be Hit Fly Up 击飞"] = "7 - Knockback";
            _translations["8 - Dead 死亡"] = "8 - Dead";
            _translations["92 - Double Combo 双连击"] = "92 - Double Combo";
            _translations["96 - Kung Fu Punch 功夫拳"] = "96 - Kung Fu Punch";
            _translations["A/T姿势"] = "A/T Pose";
            _translations["AI模型"] = "AI Model";
            _translations["CFG强度"] = "CFG Strength";
            _translations["Normal（带纹理几何模型）"] = "Normal (Textured Geometry)";
            _translations["P1 建议范围：48~20000"] = "P1 recommended: 48~20000";
            _translations["P1 支持：geometry。空表示默认(meshopt)。"] = "P1 supports: geometry. Empty = default (meshopt).";
            _translations["PBR 材质"] = "PBR Material";
            _translations["PBR材质"] = "PBR Material";
            _translations["PBR纹理"] = "PBR Texture";
            _translations["rig v2.5 仅支持以下预设动画"] = "rig v2.5 only supports the following preset animations";
            _translations["SeeDream 要求单图总像素约 368 万～1040 万"] = "SeeDream requires ~3.68M~10.4M pixels per image";
            _translations["T/A姿势"] = "T/A Pose";
            _translations["v3.1 (最新)"] = "v3.1 (Latest)";
            _translations["v5.0 有效范围 [30,120]。优先级：Text 中指定 > 本参数"] = "v5.0 range [30,120]. Priority: Text > this param";
            _translations["芭比"] = "Barbie";
            _translations["绑骨版本"] = "Rig Version";
            _translations["标准"] = "Standard";
            _translations["材质类型"] = "Material Type";
            _translations["裁剪宽度（像素）"] = "Crop Width (px)";
            _translations["超低"] = "Ultra Low";
            _translations["待机"] = "Idle";
            _translations["待机 (Idle)"] = "Idle";
            _translations["导出UV"] = "Export UV";
            _translations["导出带几何体"] = "Export with Geometry";
            _translations["动画动作"] = "Animation Action";
            _translations["动画类型"] = "Animation Type";
            _translations["动作时长（秒）"] = "Action Duration (s)";
            _translations["逗号分隔的种子列表，用于生成多个变体"] = "Comma-separated seed list for multiple variants";
            _translations["毒液"] = "Venom";
            _translations["对称模式"] = "Symmetry Mode";
            _translations["对齐图像"] = "Align to Image";
            _translations["返回末帧"] = "Return Last Frame";
            _translations["方向"] = "Orientation";
            _translations["高分辨率"] = "High Resolution";
            _translations["烘焙动画"] = "Bake Animation";
            _translations["极高"] = "Ultra High";
            _translations["将动画烘焙到模型中"] = "Bake animation into model";
            _translations["角色高度(米)"] = "Character Height (m)";
            _translations["角色高度，单位为米"] = "Character height in meters";
            _translations["精细"] = "Fine";
            _translations["卡通人物"] = "Cartoon Character";
            _translations["开启"] = "On";
            _translations["控制生成结果的多样性和质量"] = "Controls diversity and quality of results";
            _translations["控制是否执行UV展开。默认true；关闭可明显提速并减小模型体积。"] = "Enable UV unwrap. Default true; disable to speed up and reduce file size.";
            _translations["抠图后裁剪宽度（像素），0 表示不裁剪，等比例缩放"] = "Crop width after cutout (px). 0 = no crop, proportional scale.";
            _translations["抠图后压缩质量"] = "Compression quality after cutout";
            _translations["抠图后压缩质量，100 不压缩，值越小压缩越厉害"] = "Compression after cutout. 100 = no compression, lower = more compression";
            _translations["宽高比"] = "Aspect Ratio";
            _translations["面数"] = "Face Count";
            _translations["面数限制"] = "Face Limit";
            _translations["模型版本"] = "Model Version";
            _translations["模型层级"] = "Model LOD";
            _translations["模型分割"] = "Model Segmentation";
            _translations["目标多边形数量，范围100-300000"] = "Target polygon count, range 100-300000";
            _translations["目标面数"] = "Target Face Count";
            _translations["目标面数，范围100-300000"] = "Target face count, range 100-300000";
            _translations["内容审核"] = "Content Moderation";
            _translations["攀爬 (Climb)"] = "Climb";
            _translations["跑步 (Run)"] = "Run";
            _translations["劈砍 (Slash)"] = "Slash";
            _translations["启用Remesh"] = "Enable Remesh";
            _translations["启用模型分割"] = "Enable model segmentation";
            _translations["启用输入重写"] = "Enable Input Rewrite";
            _translations["启用四边网格模型输出"] = "Enable quad mesh output";
            _translations["潜水 (Dive)"] = "Dive";
            _translations["三角形"] = "Triangles";
            _translations["设置方向=对齐图像以自动旋转模型以对齐原始图像"] = "Set Orientation=Align to Image to auto-rotate model to match original image";
            _translations["射击 (Shoot)"] = "Shoot";
            _translations["生成 AnimationClip 的帧率"] = "AnimationClip frame rate";
            _translations["生成PBR贴图"] = "Generate PBR Maps";
            _translations["生成T/A姿势的模型"] = "Generate T/A-pose model";
            _translations["生成的 AnimationClip 是否循环"] = "Whether AnimationClip loops";
            _translations["生成动作的时长，单位为秒"] = "Action duration in seconds";
            _translations["生成分辨率（0.5K/1K/2K/4K）"] = "Resolution (0.5K/1K/2K/4K)";
            _translations["生成高分辨率天空盒"] = "Generate high-res skybox";
            _translations["生成宽高比（auto 或指定比例）"] = "Aspect ratio (auto or specified)";
            _translations["生成任务类型：Normal可生成带纹理的几何模型"] = "Task type: Normal can generate textured geometry model";
            _translations["生成图片尺寸"] = "Image size";
            _translations["生成图片格式"] = "Image format";
            _translations["生成种子，0表示随机"] = "Seed, 0 = random";
            _translations["圣诞"] = "Christmas";
            _translations["时长(秒)"] = "Duration (s)";
            _translations["视频分辨率"] = "Video resolution";
            _translations["视频宽高比"] = "Video aspect ratio";
            _translations["视频时长（秒）"] = "Video duration (s)";
            _translations["是否返回视频最后一帧作为预览"] = "Return last frame as preview";
            _translations["是否生成A/T姿势"] = "Generate A/T pose";
            _translations["是否生成可循环音效"] = "Generate loopable audio";
            _translations["是否使用模型改写输入 prompt"] = "Use model to rewrite input prompt";
            _translations["是否自动抠图去除背景"] = "Auto cutout (remove background)";
            _translations["受伤 (Hurt)"] = "Hurt";
            _translations["输出格式"] = "Output Format";
            _translations["图层数量"] = "Layer Count";
            _translations["生成的图层数量（1–8），默认 4"] = "Number of layers to generate (1–8), default 4";
            _translations["图层文件格式"] = "Layer file format";
            _translations["输出中是否包含几何体"] = "Include geometry in output";
            _translations["四边网格"] = "Quad Mesh";
            _translations["四边形"] = "Quads";
            _translations["随机种子"] = "Random Seed";
            _translations["随机种子，0表示随机"] = "Random seed, 0 = random";
            _translations["随机种子列表"] = "Seed List";
            _translations["拓扑结构"] = "Topology";
            _translations["拓扑类型"] = "Topology Type";
            _translations["腾讯接口要求 FaceCount 在 3000～500000（越大越精细、越慢）"] = "Tencent API: FaceCount 3000~500000 (higher = finer, slower)";
            _translations["提示词对生成结果的影响程度"] = "Prompt influence on results";
            _translations["提示词影响度"] = "Prompt Influence";
            _translations["跳跃 (Jump)"] = "Jump";
            _translations["图生视频"] = "Image-to-Video";
            _translations["图生视频：基于参考图片生成视频；文生视频：基于文本描述生成视频。上传参考图片时自动切换为图生视频。"] = "Image-to-Video: generate from reference image; Text-to-Video: from text. Auto-switches when image is uploaded.";
            _translations["网格模式"] = "Mesh Mode";
            _translations["文生视频"] = "Text-to-Video";
            _translations["纹理提示词"] = "Texture Prompt";
            _translations["纹理提示词（最多600字符）"] = "Texture prompt (max 600 chars)";
            _translations["纹理压缩"] = "Tex Compression";
            _translations["纹理质量"] = "Texture Quality";
            _translations["向后跑"] = "Run Backward";
            _translations["向前跑"] = "Run Forward";
            _translations["行走 (Walk)"] = "Walk";
            _translations["循环播放"] = "Loop";
            _translations["压缩质量"] = "Compress Quality";
            _translations["音效时长（秒）"] = "Audio Duration (s)";
            _translations["用于绑骨的姿势，T-Pose更适合动画"] = "Pose for rigging. T-Pose is better for animation.";
            _translations["预设动画"] = "Preset Animation";
            _translations["粘土"] = "Clay";
            _translations["着色材质"] = "Shaded Material";
            _translations["帧率"] = "Frame Rate";
            _translations["蒸汽朋克"] = "Steampunk";
            _translations["质量"] = "Quality";
            _translations["中（推荐）"] = "Medium (Recommended)";
            _translations["中文男声 - 绅士"] = "Chinese Male - Refined";
            _translations["中文男声 - 幽默长者"] = "Chinese Male - Jovial Senior";
            _translations["中文女声 - 可爱精灵"] = "Chinese Female - Cute Elf";
            _translations["中文女声 - 温暖闺蜜"] = "Chinese Female - Warm Companion";
            _translations["转身 (Turn)"] = "Turn";
            _translations["坠落 (Fall)"] = "Fall";
            _translations["姿势模式"] = "Pose Mode";
            _translations["自动抠图（去除背景）"] = "Auto Cutout (Remove BG)";
            _translations["纹理"] = "Texture";
            _translations["默认"] = "Default";
            _translations["高"] = "High";
            _translations["低"] = "Low";
            _translations["中"] = "Medium";
            _translations["风格"] = "Style";
            _translations["分辨率"] = "Resolution";
            _translations["循环"] = "Loop";
            _translations["自动"] = "Auto";
            _translations["关闭"] = "Off";
            _translations["生成纹理"] = "Generate Texture";
            _translations["图片尺寸"] = "Image Size";

            // ---- GeneratorConfig.json textInputPlaceholder ----
            _translations["描述你想要生成的3D模型..."] = "Describe the 3D model you want to generate...";
            _translations["描述你想要生成的带动画的3D角色（最多600字符）..."] = "Describe the animated 3D character (max 600 chars)...";
            _translations["描述你想要生成的带动画的3D人物角色（将自动追加T-Pose）..."] = "Describe the animated 3D character (T-Pose will be auto-appended)...";
            _translations["输入动作描述，如：walk、run、jump..."] = "Enter motion description, e.g.: walk, run, jump...";
            _translations["描述你想要生成的游戏图片..."] = "Describe the game image you want to generate...";
            _translations["描述你想要生成的图片..."] = "Describe the image you want to generate...";
            _translations["描述图片内容，辅助图层分离..."] = "Describe the image to guide layer separation...";
            _translations["描述你想要生成的天空盒场景..."] = "Describe the skybox scene you want to generate...";
            _translations["描述你想要生成的游戏精灵图标..."] = "Describe the game sprite icon you want to generate...";
            _translations["描述材质效果..."] = "Describe the material effect...";
            _translations["用英文描述你想要生成的音效，例如：雷声、爆炸、鸟鸣..."] = "Describe the sound effect in English, e.g.: thunder, explosion, birdsong...";
            _translations["输入要合成语音的文本内容..."] = "Enter text content for speech synthesis...";
            _translations["描述你想要生成的视频内容..."] = "Describe the video content you want to generate...";
            _translations["输入不完整"] = "Incomplete Input";

            // ---- GeneratorConfig.json name/description ----
            _translations["16位机时代像素风格"] = "16-Bit Pixel Style";
            _translations["16位像素"] = "16-Bit Pixel";
            _translations["8位机时代像素风格"] = "8-Bit Pixel Style";
            _translations["8位像素"] = "8-Bit Pixel";
            _translations["MiniMax 语音合成"] = "MiniMax TTS";
            _translations["NPC头像"] = "NPC Avatar";
            _translations["P1模型+绑骨+动画，输出FBX"] = "P1 Model+Rig+Anim, Output FBX";
            _translations["Q版可爱"] = "Chibi";
            _translations["Q版可爱风格"] = "Chibi Style";
            _translations["Tripo P1 动画模型"] = "Tripo P1 Animated Model";
            _translations["UI按钮、交互控件"] = "UI Buttons, Controls";
            _translations["Unity 地形高度图"] = "Unity Terrain Heightmap";
            _translations["按钮图标"] = "Button Icon";
            _translations["半写实"] = "Semi-Realistic";
            _translations["半写实风格"] = "Semi-Realistic Style";
            _translations["被动技能"] = "Passive Skill";
            _translations["被动技能效果图标"] = "Passive Skill Icon";
            _translations["边框装饰"] = "Border Decor";
            _translations["编织"] = "Woven";
            _translations["编织纹理"] = "Woven Texture";
            _translations["扁平化设计风格"] = "Flat Design Style";
            _translations["扁平设计"] = "Flat Design";
            _translations["波浪"] = "Wave";
            _translations["波浪形纹理"] = "Wave Texture";
            _translations["玻璃"] = "Glass";
            _translations["玻璃材质"] = "Glass Material";
            _translations["布料"] = "Fabric";
            _translations["采集工具"] = "Gathering Tools";
            _translations["彩色玻璃"] = "Stained Glass";
            _translations["彩色玻璃风格"] = "Stained Glass Style";
            _translations["草地"] = "Grass";
            _translations["草地材质"] = "Grass Material";
            _translations["潮湿"] = "Wet";
            _translations["宠物"] = "Pet";
            _translations["垂直方向的条纹纹理"] = "Vertical Lines";
            _translations["垂直条纹"] = "Vertical Stripes";
            _translations["锤子、熔炉、工作台等"] = "Hammer, Furnace, Workbench";
            _translations["瓷砖"] = "Tile";
            _translations["瓷砖材质"] = "Tile Material";
            _translations["等轴测"] = "Isometric";
            _translations["等轴测视角"] = "Isometric View";
            _translations["低多边形"] = "Low Poly";
            _translations["低多边形风格"] = "Low Poly Style";
            _translations["敌人、怪物头像"] = "Enemy, Monster Avatar";
            _translations["雕像、旗帜、地毯等"] = "Statue, Flag, Carpet";
            _translations["对角线"] = "Diagonal";
            _translations["对角线方向的纹理"] = "Diagonal Lines";
            _translations["盾牌"] = "Shield";
            _translations["法力药水、魔法卷轴等"] = "Mana Potion, Scroll";
            _translations["法杖、魔杖、魔导书等"] = "Staff, Wand, Grimoire";
            _translations["非玩家角色头像"] = "NPC Avatar";
            _translations["风化"] = "Weathered";
            _translations["蜂窝"] = "Honeycomb";
            _translations["俯视视角"] = "Top-Down View";
            _translations["概念设计"] = "Concept Design";
            _translations["干净、无磨损"] = "Clean, Pristine";
            _translations["高度图，顶视图"] = "Heightmap, Top View";
            _translations["镐、镰刀、钓竿等"] = "Pickaxe, Sickle, Rod";
            _translations["哥特"] = "Gothic";
            _translations["哥特风格"] = "Gothic Style";
            _translations["各类防护盾牌"] = "Shields";
            _translations["弓、弩、枪械等远程武器"] = "Bow, Crossbow, Gun";
            _translations["功能入口、系统图标"] = "Feature Entry, System Icon";
            _translations["功能图标"] = "Function Icon";
            _translations["怪物图标"] = "Monster Icon";
            _translations["关键物品"] = "Key Items";
            _translations["横版卷轴"] = "Side-Scroller";
            _translations["横版卷轴视角"] = "Side-Scroll View";
            _translations["混凝土"] = "Concrete";
            _translations["混凝土材质"] = "Concrete Material";
            _translations["火山 SeeDream"] = "Volcengine SeeDream";
            _translations["火山 SeeDream 表面材质"] = "Volcengine SeeDream Material";
            _translations["Seedance 2"] = "Seedance 2";
            _translations["Sonilo 音乐生成"] = "Sonilo Music Generation";
            _translations["Sonilo 音效生成"] = "Sonilo Sound Effect Generation";
            _translations["极简主义"] = "Minimalism";
            _translations["极简主义风格"] = "Minimalist Style";
            _translations["家具"] = "Furniture";
            _translations["减益特效"] = "Debuff Effect";
            _translations["减益状态效果图标"] = "Debuff Icon";
            _translations["剑、斧、锤等近战武器"] = "Sword, Axe, Hammer";
            _translations["交叉网格"] = "Crosshatch";
            _translations["交叉网格纹理"] = "Crosshatch Texture";
            _translations["角色动作序列帧生成（图生序列帧）"] = "Sprite Sheet Generation";
            _translations["戒指、项链、手镯等"] = "Ring, Necklace, Bracelet";
            _translations["金属"] = "Metal";
            _translations["金属质感材质"] = "Metal Material";
            _translations["近战武器"] = "Melee Weapons";
            _translations["经典像素艺术风格"] = "Classic Pixel Art";
            _translations["精金、龙鳞、秘银等"] = "Mithril, Dragon Scale, Orichalcum";
            _translations["均匀平滑"] = "Smooth";
            _translations["科幻"] = "Sci-Fi";
            _translations["科幻风格"] = "Sci-Fi Style";
            _translations["可爱"] = "Cute";
            _translations["可爱风格"] = "Cute Style";
            _translations["可释放的主动技能图标"] = "Active Skill Icon";
            _translations["可选前缀，会与下方文本提示词合并后发送"] = "Optional Prefix";
            _translations["恐怖"] = "Horror";
            _translations["恐怖风格"] = "Horror Style";
            _translations["快速选择常用材质类型"] = "Quick Material Select";
            _translations["矿石、草药、木材等"] = "Ore, Herb, Wood";
            _translations["狼、鹰、精灵等伙伴"] = "Wolf, Eagle, Spirit";
            _translations["裂纹"] = "Cracks";
            _translations["裂纹纹理"] = "Crack Texture";
            _translations["鳞片"] = "Scales";
            _translations["鳞片状纹理"] = "Scale Texture";
            _translations["六边形蜂窝纹理"] = "Honeycomb Pattern";
            _translations["马、龙、飞毯等载具"] = "Horse, Dragon, Carpet";
            _translations["美式卡通"] = "Cartoon";
            _translations["美式卡通风格"] = "Cartoon Style";
            _translations["面板边框、UI装饰"] = "Panel Border, UI Decor";
            _translations["魔法恢复"] = "Mana Restore";
            _translations["魔法武器"] = "Magic Weapon";
            _translations["木头"] = "Wood";
            _translations["木纹材质"] = "Wood Material";
            _translations["皮革"] = "Leather";
            _translations["皮革材质"] = "Leather Material";
            _translations["普通材料"] = "Common Materials";
            _translations["奇幻"] = "Fantasy";
            _translations["奇幻风格"] = "Fantasy Style";
            _translations["强化药剂、buff卷轴等"] = "Boost Potion, Buff Scroll";
            _translations["任务关键道具、剧情物品"] = "Quest Key Items";
            _translations["任务物品"] = "Quest Items";
            _translations["任务相关收集品"] = "Quest Collectibles";
            _translations["日式动漫"] = "Anime";
            _translations["日式动漫风格"] = "Anime Style";
            _translations["赛博朋克"] = "Cyberpunk";
            _translations["赛博朋克风格"] = "Cyberpunk Style";
            _translations["赛璐珞"] = "Cel Shading";
            _translations["赛璐珞着色风格"] = "Cel Shading Style";
            _translations["沙地"] = "Sand";
            _translations["沙地材质"] = "Sand Material";
            _translations["身体装备"] = "Body Armor";
            _translations["生命恢复"] = "Health Restore";
            _translations["石头"] = "Stone";
            _translations["石头材质"] = "Stone Material";
            _translations["史诗材料"] = "Epic Materials";
            _translations["矢量艺术"] = "Vector Art";
            _translations["矢量艺术风格"] = "Vector Art Style";
            _translations["饰品"] = "Accessory";
            _translations["手绘"] = "Hand Drawn";
            _translations["手绘风格"] = "Hand Drawn Style";
            _translations["水彩画"] = "Watercolor";
            _translations["水彩画风格"] = "Watercolor Style";
            _translations["水平方向的条纹纹理"] = "Horizontal Lines";
            _translations["水平条纹"] = "Horizontal Stripes";
            _translations["素描草图"] = "Sketch";
            _translations["素描草图风格"] = "Sketch Style";
            _translations["随机噪点纹理"] = "Noise Texture";
            _translations["陶瓷"] = "Ceramic";
            _translations["陶瓷材质"] = "Ceramic Material";
            _translations["头部装备"] = "Head Gear";
            _translations["头盔、帽子、面具等"] = "Helmet, Hat, Mask";
            _translations["透明感、易抠图"] = "Transparent, Easy cutout";
            _translations["玩家角色头像"] = "Player Avatar";
            _translations["文生视频、图生视频"] = "Text-to-Video / Image-to-Video";
            _translations["文生图、图生图"] = "Text-to-Image / Image-to-Image";
            _translations["图片分层"] = "Image Layering";
            _translations["一张图片 → 多个独立 RGBA 图层"] = "One image → multiple independent RGBA layers";
            _translations["Seedream 图片分层"] = "Seedream Image Layering";
            _translations["一张图片 → 底图 + 最多 16 个透明图层（自动分层）"] = "One image → base + up to 16 transparent layers (auto)";
            _translations["输入图片（必填，仅 1 张）"] = "Input Image (required, exactly 1)";
            _translations["拆分提示词（可选）"] = "Split Prompt (optional)";
            _translations["留空自动拆分全部主体；也可指定要拆分的元素..."] = "Leave empty to auto-split all main elements; or specify elements to split...";
            _translations["分辨率档位"] = "Resolution Tier";
            _translations["输出分辨率档位；auto 跟随输入图尺寸（1K/1.5K/2K）"] = "Output resolution tier; auto follows the input image size (1K/1.5K/2K)";
            _translations["文生音频、背景音乐"] = "Text-to-Audio / BGM";
            _translations["文生音效"] = "Text to SFX";
            _translations["文生语音、TTS"] = "Text-to-Speech / TTS";
            _translations["无方向性的平滑表面"] = "Smooth Surface";
            _translations["稀有材料"] = "Rare Materials";
            _translations["线稿"] = "Line Art";
            _translations["线稿风格"] = "Line Art Style";
            _translations["像素艺术"] = "Pixel Art";
            _translations["写实风格"] = "Realistic";
            _translations["写实光影"] = "Realistic Lighting";
            _translations["胸甲、长袍、外衣等"] = "Chest, Robe, Coat";
            _translations["序列帧动画"] = "Sprite Animation";
            _translations["选择材质的风格和状态"] = "Material Style/State";
            _translations["选择要生成的游戏内容类型"] = "Game Content Type";
            _translations["雪地"] = "Snow";
            _translations["雪地材质"] = "Snow Material";
            _translations["椅子、桌子、床铺等"] = "Chair, Table, Bed";
            _translations["音效生成"] = "SFX Generation";
            _translations["英雄头像"] = "Hero Avatar";
            _translations["油画"] = "Oil Painting";
            _translations["油画风格"] = "Oil Painting Style";
            _translations["游戏概念图"] = "Game Concept Art";
            _translations["游戏图标"] = "Game Icon";
            _translations["有划痕、磨损"] = "Scratched, Worn";
            _translations["有水渍、湿润"] = "Wet, Damp";
            _translations["有污渍、灰尘"] = "Dirty, Dusty";
            _translations["远程武器"] = "Ranged Weapons";
            _translations["远古遗物、神之泪等"] = "Ancient Artifact, Divine Tear";
            _translations["脏污"] = "Dirty";
            _translations["噪点粗糙"] = "Noisy, Rough";
            _translations["增益道具"] = "Buff Item";
            _translations["增益特效"] = "Buff Effect";
            _translations["增益状态效果图标"] = "Buff Icon";
            _translations["崭新"] = "Brand New";
            _translations["照片级"] = "Photorealistic";
            _translations["照片级写实风格"] = "Photorealistic Style";
            _translations["蒸汽朋克风格"] = "Steampunk Style";
            _translations["织物材质"] = "Fabric Material";
            _translations["制造工具"] = "Crafting Tools";
            _translations["治疗药水、食物等"] = "Healing Potion, Food";
            _translations["中世纪"] = "Medieval";
            _translations["中世纪风格"] = "Medieval Style";
            _translations["主动技能"] = "Active Skill";
            _translations["砖块"] = "Brick";
            _translations["砖块排列"] = "Brick Layout";
            _translations["砖块排列纹理"] = "Brick Pattern";
            _translations["砖墙材质"] = "Brick Material";
            _translations["装饰物"] = "Decor";
            _translations["自然风化效果"] = "Natural Age";
            _translations["坐骑"] = "Mount";
            _translations["做旧"] = "Aged";
            _translations["正面 (必需)"] = "Front (Required)";
            _translations["后端返回 AAC/MP4 时需安装 ffmpeg 并加入 PATH 以自动转 WAV。"] = "Backend returned AAC/MP4. Install ffmpeg and add to PATH for auto WAV conversion.";

            // ---- 补充遗漏的翻译 ----
            _translations["清空"] = "Clear";
            _translations["模型生成失败，请稍后重试。"] = "Model generation failed, please retry.";
            _translations["高度图文件不存在: {0}"] = "Heightmap file not found: {0}";
            _translations["无法生成后处理文件路径"] = "Cannot generate post-processing file path";
            _translations["复制高度图失败: {0}"] = "Failed to copy heightmap: {0}";
            _translations["后处理失败（未知错误）"] = "Post-processing failed (unknown error)";
            _translations["模型: "] = "Model: ";
            _translations["音频已复制到目标路径"] = "Audio copied to target path";
            _translations["该历史记录的纹理文件不存在。"] = "The texture file for this history record does not exist.";
            _translations["生成 BGM AudioSource"] = "Create BGM AudioSource";
            _translations["TJGenerators 生成测试"] = "TJGenerators Generation Test";
            _translations["已创建任务: {0}"] = "Task created: {0}";

            // ---- 补充遗漏的窗口翻译 ----
            _translations["请等待该条生成完成后再应用。"] = "Please wait for the current generation to complete before applying.";
            _translations["当前未绑定目标动画资产，无法应用。"] = "No target animation asset bound, cannot apply.";
            _translations["已将历史记录应用到当前动画。"] = "Applied history to current animation.";
            _translations["没有找到启用的参考图生成器配置"] = "No enabled reference image generator config found";
            _translations["配置更新后没有可用的生成器"] = "No generators available after config update";
            _translations["无法创建 Prefab"] = "Cannot create Prefab";
            _translations["请先创建或选择目标视频资产。"] = "Please create or select a target video asset first.";
            _translations["请先绑定或创建目标图片资产。"] = "Please bind or create a target image asset first.";
            _translations["已导出 {0} 张 Sprite。\\n路径：{1}"] = "Exported {0} Sprites.\\nPath: {1}";
            _translations["已导出 {0} 张 Sprite，并创建动画文件。\\nSprite路径：{1}\\n动画路径：{2}"] = "Exported {0} Sprites and created animation.\\nSprite path: {1}\\nAnimation path: {2}";

            // ---- GeneratorConfig.json displayName 补充 ----
            _translations["混元3.1"] = "Hy 3.1";
            _translations["混元Motion"] = "Hy Motion";
            _translations["UniRig 绑骨"] = "UniRig";
            _translations["（无提示词）"] = "(No Prompt)";
            _translations["asset_id："] = "asset_id:";

            // ---- functionTags / vendorTags ----
            _translations["文生3D"] = "Text-to-3D";
            _translations["图生3D"] = "Image-to-3D";
            _translations["多视图生成3D"] = "Multi-View-to-3D";
            _translations["文生3D带动画"] = "Text-to-3D with Animation";
            _translations["图生3D带动画"] = "Image-to-3D with Animation";
            _translations["文生图"] = "Text-to-Image";
            _translations["图生图"] = "Image-to-Image";
            _translations["文生天空盒"] = "Text-to-Skybox";
            _translations["图生天空盒"] = "Image-to-Skybox";
            _translations["图生表面材质"] = "Image-to-Material";
            _translations["文生音频"] = "Text-to-Audio";
            _translations["音效"] = "SFX";
            _translations["语音合成"] = "TTS";
            _translations["2D序列帧"] = "2D Sequence";
            _translations["动作"] = "Motion";
            _translations["优化"] = "Optimize";
            _translations["AI生成"] = "AI Generate";
            _translations["火山"] = "Volcengine";

            // ---- Sprite 类型/风格 category ----
            _translations["武器"] = "Weapons";
            _translations["护甲"] = "Armor";
            _translations["消耗品"] = "Consumables";
            _translations["材料"] = "Materials";
            _translations["特殊"] = "Special";
            _translations["工具"] = "Tools";
            _translations["角色"] = "Characters";
            _translations["场景"] = "Scene";
            _translations["技能"] = "Skills";
            _translations["效果"] = "Effects";
            _translations["像素"] = "Pixel";
            _translations["卡通"] = "Cartoon";
            _translations["写实"] = "Realistic";
            _translations["现代"] = "Modern";
            _translations["绘画"] = "Painting";
            _translations["渲染"] = "Rendering";
            _translations["视角"] = "View";
            _translations["题材"] = "Theme";
            _translations["艺术"] = "Art";
            _translations["游戏"] = "Game";
            _translations["地形"] = "Terrain";

            // ---- 材质预设/纹理走势/风格状态 category ----
            _translations["基础"] = "Basic";
            _translations["建筑"] = "Architecture";
            _translations["透明"] = "Transparent";
            _translations["自然"] = "Nature";
            _translations["条纹"] = "Stripes";
            _translations["网格"] = "Mesh";
            _translations["曲线"] = "Curves";
            _translations["随机"] = "Random";
            _translations["几何"] = "Geometry";
            _translations["有机"] = "Organic";
            _translations["破损"] = "Damaged";
            _translations["织物"] = "Fabric";

            // ---- title 字段（无冒号版本）----
            _translations["材质预设"] = "Material Preset";
            _translations["风格状态"] = "Style State";

            // ---- 其他缺失的 config 字段 ----
            _translations["纹理的走向"] = "Texture Pattern";
            _translations["选择纹理的走向和图案"] = "Select Texture Pattern";
            _translations["语音文本"] = "Speech Text";
            _translations["角色图片"] = "Character Image";
            _translations["上传多角度图片生成3D模型，正面必需，至少4张图片"] = "Upload multi-angle images to generate 3D model. Front view required, min 4 images.";
            _translations["请在工程 Assets 内下拉选择 FBX / OBJ；所选文件将上传至绑骨服务"] = "Select FBX/OBJ from Assets; the file will be uploaded to the rigging service.";
            _translations["4×4 序列帧（16 帧）"] = "4×4 Sequence (16 Frames)";

            // ---- ErrorDialogUtils 分段消息补充 ----
            _translations["系统无法为您生成的模型绑定动画骨骼，这通常是因为您的提示词描述的不是一个角色。\n\n"] = "The system cannot rig the generated model. This is usually because your prompt does not describe a character.\n\n";
            _translations["❌ 无法制作动画：食物、车辆、建筑物、家具等物品\n"] = "❌ Cannot animate: food, vehicles, buildings, furniture\n";
            _translations["✅ 可以制作动画：人类、动物、机器人等角色\n\n"] = "✅ Can animate: humans, animals, robots\n\n";
            _translations["解决方案：请描述一个有头部、躯干、四肢的角色，而不是物品。"] = "Solution: Describe a character with head, torso, and limbs, not an object.";
            _translations["生成请求超时，可能的原因：\n\n"] = "Generation request timed out. Possible causes:\n\n";
            _translations["• 网络连接不稳定\n"] = "• Unstable network\n";
            _translations["• 服务器负载过高\n"] = "• Server overload\n";
            _translations["• 模型生成时间过长\n\n"] = "• Model generation taking too long\n\n";
            _translations["建议稍后重试，或检查网络连接。"] = "Please retry later or check your network.";
            _translations["API认证失败，请检查：\n\n"] = "API authentication failed. Check:\n\n";
            _translations["• API密钥是否正确\n"] = "• API key is correct\n";
            _translations["• 账户是否有足够的额度\n"] = "• Account has sufficient credits\n";
            _translations["• 网络连接是否正常\n\n"] = "• Network is connected\n\n";
            _translations["请联系管理员检查配置。"] = "Please contact your administrator.";
            _translations["模型生成失败，请稍后重试。\n\n"] = "Model generation failed, please retry.\n\n";
            _translations["如果问题持续存在，请联系技术支持。"] = "If the issue persists, contact support.";
            _translations["生成过程中出现错误，请重试。\n\n"] = "An error occurred during generation.\n\n";
            _translations["如果问题持续存在，请检查网络连接或联系技术支持。"] = "If the issue persists, check your network or contact support.";

            // ---- 代码中遗漏的翻译 ----
            _translations["ffmpeg 执行超时"] = "ffmpeg timed out";
            _translations["ffmpeg 退出码 {0}"] = "ffmpeg exit code {0}";
            _translations["已生成: {0} / {1}"] = "Generated: {0} / {1}";
            _translations["错误: {0}"] = "Error: {0}";
            _translations["正在生成多视图图片 (1/{0})..."] = "Generating multi-view images (1/{0})...";
            _translations["文本提示词（仅支持英文描述）"] = "Prompt (English only)";
            _translations["例如：a cute banana mascot, studio lighting, high detail"] = "e.g.: a cute banana mascot, studio lighting, high detail";
            _translations["例如：a fantasy sword icon, studio lighting, high detail"] = "e.g.: a fantasy sword icon, studio lighting, high detail";

            // ---- tags 数组搜索标签翻译 ----
            // spriteTypeSelector tags
            _translations["战斗"] = "Combat";
            _translations["魔法"] = "Magic";
            _translations["防御"] = "Defense";
            _translations["增益"] = "Buff";
            _translations["恢复"] = "Recovery";
            _translations["制作"] = "Crafting";
            _translations["稀有"] = "Rare";
            _translations["史诗"] = "Epic";
            _translations["剧情"] = "Story";
            _translations["任务"] = "Quest";
            _translations["采集"] = "Gathering";
            _translations["移动"] = "Movement";
            _translations["伙伴"] = "Companion";
            _translations["装饰"] = "Decor";
            _translations["界面"] = "UI";
            _translations["能力"] = "Ability";
            _translations["敌人"] = "Enemy";
            // spriteStyleSelector tags
            _translations["复古"] = "Retro";
            _translations["经典"] = "Classic";
            _translations["精致"] = "Refined";
            _translations["活泼"] = "Lively";
            _translations["动漫"] = "Anime";
            _translations["真实"] = "Realistic";
            _translations["简约"] = "Minimalist";
            _translations["清晰"] = "Clean";
            _translations["柔和"] = "Soft";
            _translations["粗略"] = "Rough";
            _translations["简洁"] = "Simple";
            _translations["动画"] = "Animation";
            _translations["未来"] = "Future";
            _translations["冒险"] = "Adventure";
            _translations["科技"] = "Tech";
            _translations["太空"] = "Space";
            _translations["黑暗"] = "Dark";
            _translations["温馨"] = "Cozy";
            _translations["古典"] = "Classical";
            _translations["历史"] = "Historical";
            _translations["宗教"] = "Religious";

            // ---- 视频生成补充 ----
            _translations["创建特效播放器"] = "Create Effect Player";
            _translations["特效视频（生图+生视频）"] = "Effect Video (Image + Video)";
            _translations["特效视频"] = "Effect Video";
            _translations["输入特效描述，自动生成原画+绿幕特效视频"] = "Enter an effect description to auto-generate key art and a green-screen effect video";
            _translations["特效"] = "VFX";
            _translations["绿幕视频"] = "Green-Screen Video";
            _translations["特效描述"] = "Effect Description";
            _translations["描述你想要的特效，例如：fire explosion, magic glow, lightning strike...（绿幕背景自动添加）"] = "Describe the effect you want, e.g.: fire explosion, magic glow, lightning strike... (green-screen background added automatically)";
            _translations["视频时长(秒)"] = "Video duration (s)";
            _translations["特效视频时长（4-15秒）"] = "Effect video duration (4-15 seconds)";
            _translations["视频比例"] = "Aspect ratio";

            // ---- 世界生成补充 ----
            _translations["TJGenerators 世界生成"] = "TJGenerators World";
            _translations["TJGenerators 世界 - {0}"] = "TJGenerators World - {0}";
            _translations["目标世界"] = "Target World";
            _translations["应用到当前世界"] = "Apply to World";
            _translations["请先绑定或创建目标世界资产。"] = "Please bind or create a target world asset first.";
            _translations["确定要将选中的历史世界应用到 {0} 吗？"] = "Apply selected world to {0}?";
            _translations["下载 Collider Mesh..."] = "Download Collider Mesh...";
            _translations["创建 Gaussian Splat 资产..."] = "Create Gaussian Splat Asset...";
            _translations["下载 Gaussian Splatting 数据 ({0})..."] = "Download Gaussian Splatting data ({0})...";

            // ---- UnityGaussianSplattingPackage 补充 ----
            _translations["安装依赖包"] = "Install Dependency";
            _translations["世界生成功能需要安装 UnityGaussianSplatting 依赖包。\n\n来源：https://github.com/Liuweixian/UnityGaussianSplatting.git\n子目录：package\n\n如果网络无法访问 GitHub，可选择「使用代理安装」。"] = "World generation requires the UnityGaussianSplatting package.\n\nSource: https://github.com/Liuweixian/UnityGaussianSplatting.git\nSubdirectory: package\n\nIf you cannot access GitHub, choose \"Install via Proxy\".";
            _translations["直接安装"] = "Install Directly";
            _translations["使用代理安装"] = "Install via Proxy";
            _translations["正在安装 UnityGaussianSplatting...\n这可能需要几分钟，请耐心等待。"] = "Installing UnityGaussianSplatting...\nThis may take a few minutes, please wait.";
            _translations["安装失败"] = "Installation Failed";
            _translations["UnityGaussianSplatting 安装失败：{0}\n\n你也可以手动克隆仓库后通过 Package Manager 从本地磁盘安装：\n1. git clone https://github.com/Liuweixian/UnityGaussianSplatting.git\n2. Window → Package Manager → + → Add package from disk\n3. 选择克隆下来的 package 目录中的 package.json"] = "UnityGaussianSplatting installation failed: {0}\n\nYou can also manually clone the repo and install via Package Manager:\n1. git clone https://github.com/Liuweixian/UnityGaussianSplatting.git\n2. Window → Package Manager → + → Add package from disk\n3. Select package.json in the cloned package directory";

            // ---- ImageSliceWindow 补充 ----
            _translations["切割并导出"] = "Slice & Export";
            _translations["正在导出..."] = "Exporting...";

            // ---- TerrainCreationUtils 补充 ----
            _translations["originalHeightmapAssetPath 为空"] = "originalHeightmapAssetPath is empty";
        }
    }
}
#endif
