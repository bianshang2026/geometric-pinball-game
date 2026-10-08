# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.25] - 2026-09-08

### Added

- 生成请求增加 `X-Workspace-Name` 请求头，上报当前工作区名称

### Fixed

- FBX 贴图解压后先导入再做材质重映射，避免 `SearchAndRemapMaterials` 找不到纹理 GUID
- `AssetDatabase.Refresh` 在脚本编译期间延迟执行，避免刷新触发循环重编译；磁盘写入后改为按文件/目录定向导入

### Changed

- 移除 Unity 侧 `search_assets` / `download_asset` CustomTool 链路，改由后端 MCP 提供
- `cn.tuanjie.codely.bridge` 依赖从 1.0.69 升级到 1.0.76
- 天空盒文档移除已不支持的分辨率参数（后端仅支持 `high_res`）

## [1.0.24] - 2026-08-19

### Added

- 图片分层新增 Seedream Pro（`provider=seedream_pro`）：自动拆为 1 张底图 + 最多 16 个透明图层，prompt 可选，`num_layers` 忽略，支持 `size` 档位
- Rodin 3D 模型新增面数上限 `qualityOverride`（CustomTool `quality_override`，500–2000000），优先于 quality 预设，等价 Tripo 的 faceLimit

### Fixed

- 图片生成显式传入 `is_segmentation` 时不再被 `outputFormat` 覆盖
- 3D 模型绑定 Prefab 时按包围盒自动适配缩放，兼容 Unity 2019

### Changed

- Game UI Kit 默认改为 Seedream Pro：Step 1 生成 2848×1600 UI 截图，Step 2 图层拆分为底图 + 最多 16 层透明 PNG；`frontier` 品红底抠图拼版保留为旧路径
- 资产库下载前同步调用后台 `record-download` 落账（首次扣费 / 去重），失败不阻断下载

## [1.0.23] - 2026-08-14

### Fixed

- 视频窗口「参考图片（可选）」仍被拦截的问题：默认模式改为文生视频，提交时按是否上传参考图自动解析 mode；手动选图生视频但未传图时静默降级为文生视频（首帧/首尾帧模式仍校验必传图）

## [1.0.22] - 2026-08-13

### Added

- 新增图片分层生成器（`image-layering` / Qwen）：将一张输入图拆分为多张独立 RGBA 图层 PNG；编辑器图片窗口必选参考图，CustomTool `generate_image_layers` 支持 Domain Reload 任务恢复，并配套 agent / skill
- 视频生成支持参考图上传与动态模式：未上传参考图时文生视频，上传后自动切换参考图模式
- 新增 Tripo 纹理重生成扩展（`generate_tripo_texture_model`）：对已有 3D 模型重新生成贴图 / PBR
- 绑骨动画工具链重构：以 UniRig + HunyuanMotion 替代 Meshy，拆分为 `generate_rigged_model` / `generate_model_motion` / `generate_animated_character`；Tripo P1 / Rodin 文生 3D 支持 `add_motion` 一步生成带动画角色
- 生成失败或取消时自动清理 Prefab 内残留 Placeholder 子对象；新增 `AI/工具/清理占位 GameObject` 菜单批量扫描清理

### Fixed

- 图片生成 `outputFormat` 与自动抠图（`isSegmentation`）联动：选 JPEG 时关闭抠图，选 PNG 时开启；默认输出格式改为 JPEG
- Domain Reload 后生成器配置加载失败导致窗口模型列表为空的问题；增加文件系统直读配置与延迟重试
- FBX 导入完成后校验网格顶点数，避免空/损坏模型写入 `model_path`
- 修复 DynamicGenerator 纹理内存泄漏；多视图上传时保留已选的部分视图

### Changed

- 表面材质生成从精灵窗口拆分为独立 `TJGeneratorsMaterialWindow`；精灵、材质、图片、序列帧窗口共用 `TJGeneratorsAssetWindowBase` 基类
- 材质历史记录合并同名纹理条目，并支持从纹理历史反查材质路径

## [1.0.21] - 2026-08-07

### Added

- Game UI Kit 工作流新增第三步 `slice_image`：CV 连通域检测自动切割 cutout sheet 为独立 Sprite PNG，支持背景模式、容差、最小区域等参数
- 视频生成新增多模态模式（`multimodal`）：支持参考视频（运镜/动作）、多参考图、音频参考；新增 `first_frame` / `first_last_frame` 模式与 `generate_audio` 参数
- 视频生成 UI 新增多模态参考视频上传入口（拖拽 + 文件选择）
- CustomTool 程序集诊断：Domain Reload 后检测 `UnityTcp.CustomTool` 是否加载，缺失时输出修复指引并尝试触发重编译
- Play Mode 保护提示可点击直接退出 Play Mode

### Fixed

- 生成精灵纹理导入时设置 `spritePixelsPerUnit = 100`，避免场景中缩放不一致
- 生成资产文件夹名跨平台 sanitize，避免 Linux 允许而 Windows 非法的字符导致工程无法打开
- 复用 DynamicGenerator 时清除残留多模态 JSON 字段，修复视频生成脏数据泄漏；路径解析统一委托 `PathUtils`
- 远程下载 URL 中的反斜杠统一规范为正斜杠，修复 UnityWebRequest 下载失败
- audio-generator 工具 schema 补充 `duration_seconds` / `output_format` / `play_on_awake`，修复 SFX 时长等参数被 agent 丢弃的问题
- 模型导入强制 `isReadable = true`，修复运行时模型不可见；补充 `.jpeg` 纹理支持与 `.webp` 兼容提示
- 参考图压缩前校验 PNG/JPEG 头并拒绝 8×8 占位纹理，避免无效图片被当作参考图上传
- 参考图超过 10MB 时自动压缩到 2048px + JPG 85% 质量后再上传
- Walk/Run 等动作动画导入默认启用 `loopTime`；一次性动作可通过 `loop` 参数控制
- TTS 省略或空白 `voice_id` 时使用内置默认预设音色，不再发送空值到后端
- 内容策略拦截时在错误信息中显示具体拦截原因

### Changed

- 视频生成 `duration` 范围从 1-10 改为 4-15 秒（对齐 Seedance 2.0 API）；`ratio` 新增 4:3 / 3:4 / 21:9 / adaptive 选项；分辨率描述修正为 480p / 720p
- 移除生成积分/消耗点数 UI 展示，`UserInfoBar` 仅显示邮箱
- Play Mode 提示文案统一精简为「请先退出 Play Mode」，并在生成失败信息中附带重试指引

## [1.0.20] - 2026-08-04

### Added

- 新增 Game UI Kit CustomTool（`generate_game_ui_kit`）：两步工作流先文生游戏 UI 截图，再生成品红底 UI 抠图拼版，并配套 skill / agent
- 新增 ESRGAN 图片放大 CustomTool（`upscale_image`）：Real-ESRGAN 支持 1x–8x 超分与多种模型/人脸增强选项，并配套 skill / agent
- 新增声音克隆 CustomTool（`voice_clone`）：从音频样本克隆音色并返回 `custom_voice_id`，可交给 TTS 使用，并配套 skill / agent
- AI 生成规划 agent 纳入 Game UI Kit、图片放大、声音克隆的分派与依赖规则

### Fixed

- 3D 模型预览改为相机 yaw/pitch 环绕旋转，修复模型转到背面时俯仰/偏航控制反向的问题

### Changed

- 音乐生成从火山文生音频（huoshan_music）切换到 Sonilo（sonilo-music）：参数从 duration(int 30-120)/version/enableInputRewrite 改为 durationSeconds(float 1-180)/outputFormat(wav|mp3)
- 音效生成从旧 fal 音效（sound-effect）切换到 Sonilo（sonilo-sfx）：移除 promptInfluence/loop 参数，outputFormat 改为 wav|mp3，时长范围从 1-22 扩展到 1-180 秒，支持中文 prompt
- output_format 只暴露 wav/mp3（Unity 可导入的格式），aac/flac 归一化为 wav；BGM 工具新增 prompt 长度校验（2000 字符上限）

## [1.0.19] - 2026-07-30

### Added

- 新增 Codely skill `unity-tjgenerators-version`，查询 Unity 实际编译加载的 TJGenerators UPM 包版本，避免读到扩展包或 History 目录中的过期副本
- 火山 SeeDream / SeeDream Pro（图片、精灵、材质）生成器补充 1024 字符 prompt 长度限制与提交前校验

### Fixed

- 绑骨动画 CustomTool 在 Domain Reload 恢复时正确持久化并传递 `sessionId`，并按 pipeline 类型动态选择通知工具名；任务提交结果增加 `submission_success` 字段

## [1.0.18] - 2026-07-29

### Fixed

- 修复 Unity 2019 下 2D 骨骼动画生成工具任务恢复逻辑中 lambda 变量遮蔽导致的编译报错

### Changed

- 音乐生成从火山文生音频（huoshan_music）切换到 Sonilo（sonilo-music）：参数从 duration(int 30-120)/version/enableInputRewrite 改为 durationSeconds(float 1-180)/outputFormat(wav|mp3)
- 音效生成从旧 fal 音效（sound-effect）切换到 Sonilo（sonilo-sfx）：移除 promptInfluence/loop 参数，outputFormat 从 fal 枚举改为 wav|mp3，时长范围从 1-22 扩展到 1-180 秒，支持中文 prompt
- output_format 只暴露 wav/mp3（Unity 可导入的格式），aac/flac 归一化为 wav
- BGM 工具新增 prompt 长度校验（2000 字符上限）

## [1.0.17] - 2026-07-29

### Added

- CustomTool（视频、特效视频、天空盒、地形、2D 序列帧）支持 Domain Reload 后自动恢复未完成任务，与图片/精灵等工具行为一致
- 精灵 CustomTool 生成占位纹理后自动配置为 Sprite 导入类型并启用 Alpha 透明

### Fixed

- CustomTool 同步任务提交改用 `UnityWebRequest` 阻塞实现，修复 Unity Mono 下 `HttpClient` 导致「Illegal byte sequence」、任务无法提交的问题
- 修复 `GenerationPipeline` 中因编码损坏的中文注释与字符串

### Changed

- 抽取通用 `GenerationTaskTrackerStore`，统一 13 个 CustomTool 的任务追踪、会话持久化与 Domain Reload 恢复逻辑
- 拆分 `IGenerationPipelineHost` 为生命周期、UI 触发与媒体资产三个接口，CustomTool 宿主统一继承 `HeadlessPipelineHostBase`
- 天空盒 `output_path` 提交前校验与规范化；音频/视频保存路径增加回退解析；音效输出扩展名统一规范为 mp3/wav

## [1.0.16] - 2026-07-24

### Changed

- 移除已弃用的混元 3D、混元智能减面、混元多视图转 3D 生成器及相关配置与 UI（保留混元 3.1）
- 生成相关 HTTP 请求统一附加 `orgId` 请求头；访问令牌优先读取 Codely CLI 凭证，再回退 UnityConnect

## [1.0.15] - 2026-07-22

### Fixed

- 修复生成纹理使用 indexed-color PNG 时 alpha 通道在 DXT5 压缩后丢失的问题，统一通过 `GeneratedTextureImportUtils` 解码为 RGBA32 并配置正确导入设置
- 修复音效生成占位符固定为 `.mp3` 导致非 MP3 格式无法覆盖占位资产、遗留陈旧文件的问题；规范化 `output_format` 的 fal 枚举与 wav/mp3 等别名

### Changed

- 配置数据模型拆分为 `Config/Models/` 下独立文件；生成历史与用户类型拆分；GenerationPipeline 媒体保存接口统一为 `GetAssetSavePath` / `OnAssetSaved(PipelineMediaType)`
- 移除 `PostProcessingConfig` 未使用的 `isHumanoid` 字段；`VisualSelectorOptionConfig` 重命名为 `SelectorOptionConfig`；纹理图案选择窗口重命名为 `TexturePatternSelectorWindow`

## [1.0.14] - 2026-07-21

### Added

- CustomTool（图片、精灵、材质、音频、音效、TTS）支持 Domain Reload 后自动恢复未完成任务，不再一律标记为永久中断

### Fixed

- 修复下载完成时 `RemoveInterruptedTask` 竞态，避免恢复中的任务被误清除

### Changed

- GenerationPipeline 拆分媒体处理、下载辅助、ZIP 解压与资产格式检测等协作类，并以常量统一输出类型与任务状态
- 绿幕视频、精灵序列、图片切片、绑骨模型等后处理逻辑统一至 `PostProcessing` 命名空间，消除多处重复实现

## [1.0.13] - 2026-07-17

### Added

- 新增 Unity Play 模式防护：生成、资产搜索、下载及场景放置入口在播放期间统一禁用并显示本地化提示，避免退出播放后资产丢失

### Fixed

- 视频 CustomTool 未传入 `mode` 时可根据参考图自动识别文生视频或图生视频模式
- 3D 模型生成器下载响应路径由错误的 `pbr_model` 修正为 `model`

### Changed

- 参考图生成逻辑抽取为可无界面复用的服务，空白 MP3 创建统一由音频工具处理

## [1.0.12] - 2026-07-14

### Added

- 视频：火山 Seedance 2 支持 Mini / 标准 / 快速模型选择，分辨率调整为 480p / 720p；新增阿里云 HappyHorse 1.1 文生/图生视频
- 图片与精灵：新增火山 SeeDream Pro、Frontier Lite 等生成器选项
- 生成历史写入 `sessionId`，新增 `list_session_assets` CustomTool，可按 Agent 会话列出已生成资产
- Editor 程序集补充 `System.IO.Compression` 引用，支持压缩包读写

### Fixed

- Domain Reload 后窗口重复创建、同会话历史占位误清理、模型预览丢失等生命周期问题
- 特效视频预览 URL 由 `image_url` 修正为 `last_frame_url`；3D 模型预览路径同步修正
- Unity 6000.5+ 下 `FindObjectsByType` 兼容；空白视频占位移至 `Resources~`，避免启动时 VideoClipImporter 报错
- 音乐/视频窗口刷新时不再自动创建孤儿占位资产

### Changed

- 统一生成窗口 Bootstrap / Refresh 生命周期与标准历史面板；2D 精灵表序列帧拆分为独立窗口
- CustomTool asmdef 改为按程序集名称引用
- `cn.tuanjie.codely.bridge` 升级至 1.0.69，`NotifyAll` 按 `CODELY_BRIDGE_HAS_NOTIFY_ALL` 条件编译
- 生成操作按钮布局与绘制逻辑优化

## [1.0.11] - 2026-07-07

### Added

- 世界生成：新增 `AI/生成/生成世界` 与 World Labs Marble 窗口，支持文生/图生 3D 世界；自动检测并安装 Unity Gaussian Splatting 包
- 特效视频：视频窗口新增特效视频（绿幕）工作流，下载后自动抠像生成 ChromaKey 材质，并可在场景中一键创建特效播放器
- 参考图上传校验：统一限制 png/jpg/jpeg 格式，不支持格式或损坏文件时给出友好错误提示
- 扩展资产内联：将原 `extension` 子模块直接纳入主仓库，包含各生成器 agent 配置与 skill 文档，无需 submodule 初始化
- Linux/macOS 打包：新增 `tools/pack.sh` 脚本，与现有 `pack.bat` 形成跨平台打包流程

### Fixed

- 兼容 Unity 2019.4：新增版本兼容工具类，替换 C# 8+ 语法，最低支持版本由 2020.3 降至 2019.4
- 空白视频占位素材 color primaries 设为 BT.709，消除 Windows Media Foundation 警告
- 补全视频、世界、材质模板、地形及特效视频相关英文翻译

### Changed

- 2D 序列帧菜单与命名统一为「2D 动作序列帧」「2D 精灵表序列帧」；生成菜单按 3D / 2D / 音频视频分组重排
- 背景音乐窗口移除语音角色选择 UI
- 清理未使用代码、纹理、IDE 配置与历史记录 API

## [1.0.10] - 2026-06-25

### Added

- 各生成窗口与 CustomTools 增加提示词长度校验与字符计数，与后端 binding 上限对齐，避免超长 prompt 导致 400 与任务记录缺失
- 响应映射新增 `downloadUrlPathMultiview`，多视图输入模式下可使用独立下载 URL 路径
- 编辑器启动时自动注册 `TuanjieAI` / `TuanjieAI_Frontier` 资产 Label
- `Assets/Create` 与 `GameObject` 菜单新增各类生成占位资产的快捷创建入口

### Fixed

- Rodin Gen-2.5 网格模式「三角形」选项 API 值由无效的 `Triangle` 修正为 `Raw`，修复选择该模式时的 400 错误
- `primaryParameterIds` 配置的参数现正确渲染在主区域，不再误折叠进高级设置
- 补全 ErrorDialog、搜索标签等本地化遗漏；修复字符串插值导致翻译匹配失效；统一 Volcengine 品牌名与 Text-to-Image 等术语
- 部分生成器 `responseMapping` 下载/预览 URL 路径修正

### Changed

- 菜单注册、占位资产创建与 Inspector 按钮拆分为独立模块（`TJGeneratorsMenuItems` / `TJGeneratorsAssetCreation` / `TJGeneratorsInspectorButtons`）
- 配置加载统一使用 `config/generators` 端点，移除冗余的类型分端点映射与未使用的生成器基类
- 图片切割窗口复用共享图片上传与操作按钮组件，并修复运行时纹理泄漏
- 移除 `com.unity.nuget.newtonsoft-json`、`com.unity.mathematics`、`com.unity.collections` 等未使用依赖，JSON 序列化改由 `cn.tuanjie.codely.bridge` 提供
- 扩展子模块更新：SKILL.md frontmatter YAML 格式修复

## [1.0.9] - 2026-06-18

### Added

- 表面材质支持仅用文本提示词生成，无需参考图；材质纹理 Inspector 新增「✦ AI 生成」入口
- AnimationClip 资源 Inspector 新增 2D 序列帧 AI 生成按钮
- 3D 模型窗口切换生成器时共享文本提示词，在多模型间保留输入
- 图片切割工具新增使用情况上报
- 分发包构建脚本 `tools/build-bundle.mjs`，支持组装 npm 包与 extension 并可选 zip 归档

### Fixed

- BGM 自动生成 AudioSource 时 Undo 无法正确回退的问题
- 混元 3.1 输出格式由 FBX 修正为 OBJ zip，与管线一致
- 材质与纹理 PNG 历史记录绑定，`.mat` 与其贴图共享历史
- 音频导入失败时提供更明确的错误提示；无法直接导入的格式可通过 ffmpeg 转码为 WAV
- ZIP 解压覆盖已有文件时的健壮性；低版本 C# 语言兼容性

### Changed

- 移除 GLB 格式转换管线及 `com.unity.cloud.gltfast` 依赖，3D 资产统一以 FBX/OBJ 为主；混元智能减面改为 OBJ 流程
- 移除独立 GLB 转换生成器与 IconGenerator 开发工具
- 生成器固定参数字段集中至 `fixedFields` 配置与 `ParameterJsonWriter.ApplyFixedFields`
- 磁盘写入后优先定向 `ImportAsset`，减少全量 `AssetDatabase.Refresh`
- 异步任务轮询间隔由 5 秒调整为 8 秒，`maxRetries` 调整为 360
- 扩展子模块更新：精灵 agent Mode C 加固（回合预算与 anti-poll）

## [1.0.8] - 2026-06-11

### Added

- 图片切割工具：新增 `AI/工具/图片切割` 菜单与 `ImageSliceService`，支持对大图进行传统 CV 自动区域检测、预览与批量导出精灵
- 多视图生成补全右侧（270°）视角 prompt，四向转面视角齐全
- 带骨骼动画管线增强：域重载后 Stage 2 自动恢复、`IModelDownloadPathProvider` 自定义模型下载路径、多级贴图回落恢复（rendered_image / .fbm / GLB 内嵌纹理）
- 扩展子模块更新：资产标签统一为 `TuanjieAI`，精灵生成 skill 文档同步

### Fixed

- 预览 URL 生成跳过 3D 模型文件，仅允许图片、音频、视频类型

### Changed

- Tripo 生成器配置迁移至 `tripo-p1`（P1-20260311），移除 P1 不支持的 style / convert 选项并对齐面数、`exportUv`、`compress` 参数
- `com.unity.cloud.gltfast` 依赖升级至 6.15.1；纹理加载改为 `UnityWebRequest` + `Texture2D.LoadImage`
- 精灵生成配置与 `GenerateSpriteTool` 同步更新

## [1.0.7] - 2026-06-05

### Added

- 音频生成新增 MiniMax 语音合成（TTS），支持预设语音角色与自定义 Voice ID
- 图片与 2D 精灵生成新增 Frontier Game Design、Frontier 风格化特效模型；Frontier 序列帧窗口默认切换为 Frontier Game Design
- Tripo P1 设为默认 3D 生成器；新增 `defaultModelId` 配置字段，模型选择窗口按配置自动选中默认模型
- Tripo P1 新增 `exportUv`、`compress` 参数，面数范围扩展至 48–20000 并附带 tooltip 说明
- Tripo 3D 升级至 v3.1，支持网格分割（`withMeshSegmentation`）参数
- 多图大尺寸上传组件；参考图数量上限改由生成器配置 `maxReferenceImages` 驱动
- 生成窗口标题栏帮助按钮、空历史「怎么用？」引导，以及菜单 `AI/✦ 玩转 AI 生成` 统一打开使用文档
- Inspector 中各类资产生成入口改为「✦ AI 生成」，并附带文档帮助链接
- 历史记录面板新增「在 Project 中显示」；Meshy 动画 `actionId` 下拉选择与 tooltip 说明
- 高级数值输入框过滤与焦点管理；视频资产生成流程增强（有效占位 MP4、缩略图预览、Inspector 入口）
- Unity Connect 令牌为空时，自动回退读取 `~/.codely-cli/oauth_creds.json` 中的 JWT
- 扩展子模块更新：skill 文档与参数说明（含分割、Frontier 模型、异步模板等）

### Fixed

- 在 OnGUI 中同步打开图片选择对话框导致的重入崩溃与 GUILayout 状态错误
- 预制体重新生成后场景实例 Transform 被重置的问题
- 高级设置中 int/float 字段在编辑后数值回退；生成进行中历史记录操作按钮误触
- 存在参考图或多视图时 `SetTextPrompt` 错误重置输入模式
- Tripo P1 预览 URL 路径；移除 WebP 上传支持与 jpg 提示文案笔误
- Meshy 图生 3D 固定 `shouldRemesh` 为 false；移除不支持的音频编码选项

### Changed

- 生成窗口 UI 整体改版：统一样式与间距、紧凑上传区、`uiLayout` 驱动文本/图片/高级设置渲染
- `ConfigManager` API 统一为 `GetGeneratorConfig(ConfigType, id)`；切换生成器时自动重置输入状态
- 上传与高级设置 UI 拆分为独立组件；图片预览提取为 `ImagePreview` 组件
- 生成按钮统一延迟提交 `DelayedTextField`；后处理与序列帧工具逻辑收拢至 Service/Utils
- 图片/音频资产命名支持跨扩展名唯一路径，避免同名不同后缀冲突
- 3D 生成器默认顺序调整：Tripo P1 为主生成器，Tripo v3.1 降为次级选项；混元 3D 移除内置 FBX 转换流程及相关 UI
- 异步任务轮询 `maxRetries` 由 180 提升至 720，最长等待时间由 15 分钟延长至 1 小时

## [1.0.6] - 2026-05-15

### Added

- 各类生成工具由轮询改为基于 `bg_task_done` 的推送通知
- 资源搜索下载流程改为异步通知模式
- 表面材质模板选择器支持紧凑列表视图

### Fixed

- URP 下 3D 预览材质显示为粉色的问题
- 腾讯系接口面数字段与平台约束对齐
- 图片生成尺寸文案更新；移除质量下拉与 WebP 输出选项
- 图片生成在未填写用户文本提示时拦截并优化错误提示
- 天空盒预览应用到场景时使用材质副本，避免意外改动共享材质
- 生成历史与占位资源绑定及音频工具抽取相关行为
- Frontier 游戏设计图生成器 `numImages` 与 Frontier 2D 序列输出目录
- 条件编译下遗漏的清除全部生成历史逻辑
- 额度刷新与部分输入框配色
- 下载日志补充 `unitypackage_url`

### Changed

- 生成管线与自定义工具整体改进；MP4 音频统一规范为 M4A 并整理音频资产处理
- JSON 序列化迁移至 `Codely.Newtonsoft.Json` 命名空间
- 品牌与 Unity 菜单：团结 AI / Tuanjie AI 展示与路径重组（含菜单自 `Window/Tuanjie AI` 调整为 `AI`）
- UI 缩放与共用样式拆分、整合（含天空盒 `SetImagePath` 等调用简化）

## [1.0.5] - 2026-05-11

### Added

- 新增带骨骼动作的 3D 模型生成：`generate_rigged_animated_model` 自定义工具及配套 Codely skill
- 背景音乐生成结果可在编辑器 Play 模式下自动开始播放（并支持 `play_on_awake` 开关）
- 资源库搜索结果改为卡片式布局与新版界面
- 若工程里已导入过同一下载包（按网址识别），不再重复下载

### Fixed

- 会话里保存的搜索结果条数在合并缓存时不再被截断
- 带动画的 FBX：先等资源库刷新完毕再写导入选项，Animator 默认状态机更稳妥
- 勾选动作相关生成时，不再强行套用模型的缩放与旋转，避免姿态异常
- 各窗口文本框统一用编辑器专用输入，减少焦点与 IME 等问题
- 图片参考窗口偶现文件占用与历史记录错乱

### Changed

- 生成历史存盘方式调整，管线里的请求数据结构单独整理
- 原先手写拼 JSON 的逻辑改为 Newtonsoft 解析与生成
- 左侧栏等共用间距与样式常量集中管理，脚本目录按职责重新归类
- 资源搜索里已在本工程中的条目可直接「置入场景」，不必再点下载
- 上述「已导入」判断会读落地后的元数据，并与下载任务状态一致

## [1.0.4] - 2026-04-30

### Added

- 资源库搜索窗口：场景置入、GIF 动图预览
- Domain 重载与 Play 模式下持久化搜索结果
- 3D 生成、贴图与模型选择等窗口新版 UI（输入框、图片上传、下拉等）

### Fixed

- TaskRecovery：加载时清理失败/异常任务
- 资源搜索预览占位文案；移除演示窗口并修正相关警告
- 历史面板等区域用 IMGUI 跟踪鼠标，替代 `Input.GetMouseButton`，避免编辑器下交互异常
- 图片生成器、按钮九宫格切片、额度检查等 UI 问题

### Changed

- 资源搜索与多类生成窗口界面整体改版
- 窗口矩形与布局逻辑集中到 UIComponents；历史面板布局计算集中维护
- 进一步将弹窗提示改为控制台输出；搜索参数与窗口初始化简化
- 内联整合 TaskRecovery 辅助逻辑

## [1.0.3] - 2026-04-24

### Added

- 视频 / Seedance 与序列相关生成与窗口
- 资源搜索迁 Codely 并整体优化下载与筛选
- 地形高度图自定义工具
- UniRig + 混元动作后处理
- 多视图图生及无水印、游戏设计图等生成选项
- 模型项目内选资源上传与 Tripo 变体支持

### Fixed

- 导入、下载、预览与 API 配置（分辨率、字段名、时序与错误处理等）
- 图片与序列、Rodin/材质与渲染管线相关若干问题

### Changed

- 主菜单「AI生成」→「Codely AI」
- 部分错误由弹窗改为控制台
- 去除 Burst 与部分弃用选项
- 3D 工具合并/重命名、上传与动捕选项整理
- 子模块与扩展更新
- npm 包剔除无关内容

## [1.0.2] - 2026-04-10

### Added

- Tripo P1：会话 `session_id` 支持，以及对应 UI 与自定义工具流程。
- Tripo 生成器：`base_model` 字段支持。
- 自高度图一键生成地形。

### Fixed

- 音频保存路径问题；按生成器驱动的音频格式处理。
- 序列精灵资源在完成时正确打上 `TJGeneratorsAIGenerated` 标签。
- IME 输入时占位符重叠问题。
- 为请求补充 `DefaultRequestHeaders` 的 `Accept`。
- 自定义程序集启用 `overrideReferences` 相关修正。

### Changed

- `session_id` 已接入各自定义生成与下载工具。

## [1.0.1] - 先前版本

- 基础 AI 资产生成功能与依赖（Codely Bridge、GLTFast、Newtonsoft.Json 等）。
