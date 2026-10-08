# TJGenerators for Unity

TJGenerators for Unity 是一款强大的 AI 内容生成插件，集成团结 AI 平台的多模态生成能力，无缝嵌入 Unity 编辑器工作流。支持 3D 模型、天空盒、2D 精灵、表面材质、2D 动作/精灵表序列帧、背景音乐、音效、语音合成与声音克隆、图片放大、Game UI Kit、视频、3D 世界等多种游戏资产的 AI 生成，帮助开发者和创作者大幅提升内容创作效率。

## 功能特性

### 🎮 3D 模型生成

| 生成器 | 功能 | 特点 |
|--------|------|------|
| **Tripo 3D / Tripo P1** | 文生3D、图生3D、多视图生成3D | 默认 P1 模型，支持低面控制、PBR、网格分割；文生 3D 可选 `add_motion` 一步生成带动画角色 |
| **Rodin** | 文生3D、图生3D、多视图生成3D | 支持 Gen-2.5 五种层级（超低/低/中/高/极高），FBX 输出；可用 `quality_override` 自定义面数上限（优先于 quality 预设）；文生 3D 可选 `add_motion` |
| **混元3.1** | 文生3D、图生3D、多视图生成3D | 高精度生成，支持 PBR，输出 OBJ zip |
| **Tripo 纹理重生成** | 对已有 3D 模型重新生成贴图 / PBR | CustomTool `generate_tripo_texture_model`，需已有模型 task ID 或 model URL |

### ✂️ 图片工具

| 工具 | 功能 |
|------|------|
| **图片切割** | 对大图进行传统 CV 自动区域检测，预览并批量导出独立精灵（`AI/工具/图片切割`） |
| **图片分层** | 将一张输入图 AI 拆分为多张独立 RGBA 图层 PNG（Qwen 或 Seedream Pro）；编辑器图片窗口与 CustomTool `generate_image_layers` |
| **ESRGAN 图片放大** | Real-ESRGAN 超分（CustomTool `upscale_image`），支持 1x–8x、多种模型与可选人脸增强 |
| **Game UI Kit** | 默认两步工作流（CustomTool `generate_game_ui_kit`）：Seedream Pro 文生游戏 UI 截图（2848×1600）→ 图层拆分为底图 + 最多 16 层透明 PNG；`frontier` 品红底抠图拼版为旧路径；图层合并时可用 `slice_image` 兜底切割 |

### 🌌 天空盒生成

| 生成器 | 功能 |
|--------|------|
| **Rodin Skybox** | 文生天空盒、图生天空盒，支持高分辨率输出 |

### 🖼️ 图片生成

| 生成器 | 功能 | 特点 |
|--------|------|------|
| **Frontier Game Design** | 文生图、图生图 | 游戏向图片，支持 prompt 模板、多种画幅与 PNG/JPEG 输出 |
| **火山 SeeDream** | 文生图、图生图 | 通用图片生成，支持去背景等高级参数；prompt 最长 1024 字符 |
| **火山 SeeDream Pro** | 文生图、图生图 | 更高质量图片生成，支持多参考图与自动抠图等参数；prompt 最长 1024 字符 |
| **Frontier** | 文生图、图生图 | 风格化特效，支持多档分辨率与画幅 |
| **Frontier Lite** | 文生图、图生图 | 轻量风格化图片生成 |
| **图片分层（Qwen）** | 图生多图层 | 一张图片拆分为多张独立 RGBA 图层；需上传参考图，可设置图层数量（1–8） |
| **图片分层（Seedream Pro）** | 图生多图层 | 自动拆为 1 张底图 + 最多 16 个透明图层；prompt 可选，`num_layers` 忽略，支持 `size` 档位（1K / 1.5K / 2K / auto） |

### 🎨 2D 精灵生成

| 生成器 | 功能 | 特点 |
|--------|------|------|
| **Frontier Game Design** | 文生图、图生图 | 游戏向精灵，支持多种画幅与输出格式 |
| **火山 SeeDream** | 文生图、图生图 | 支持 31 种内容类型（武器、护甲、消耗品、UI图标等）、30 种艺术风格（像素、卡通、写实等）；prompt 最长 1024 字符 |
| **火山 SeeDream Pro** | 文生图、图生图 | 更高质量精灵生成，支持多参考图与自动抠图；prompt 最长 1024 字符 |
| **Frontier** | 文生图、图生图 | 风格化特效精灵 |
| **Frontier Lite** | 文生图、图生图 | 轻量风格化精灵 |

### 🧱 表面材质生成

| 生成器 | 功能 |
|--------|------|
| **火山 SeeDream 表面材质** | 文生/图生表面材质，支持仅用文本提示词或参考图生成，含 15 种材质类型（PBR金属、木材、石材、布料、玻璃等）；prompt 最长 1024 字符 |

### 🎵 音频生成

| 生成器 | 功能 |
|--------|------|
| **Sonilo 音乐生成** | 文生背景音乐，支持 1-180 秒时长，WAV/MP3 输出 |
| **Sonilo 音效生成** | 文生音效（SFX），支持中英文，1-180 秒，WAV/MP3 输出 |
| **MiniMax 语音合成** | 文生语音（TTS），支持预设语音角色与自定义 Voice ID；省略 `voice_id` 时使用内置默认音色 |
| **声音克隆** | 从音频样本克隆音色（CustomTool `voice_clone`），返回 `custom_voice_id` 可交给 TTS 使用 |

### 🎬 视频生成

| 生成器 | 功能 | 特点 |
|--------|------|------|
| **Seedance 2** | 文生/图生/多模态视频 | 火山 Seedance 2，支持 `first_frame` / `first_last_frame` / `multimodal`（参考视频 + 多参考图 + 音频参考）；可选参考图上传，未上传时文生视频、上传后自动切换参考图模式；可选 Mini / 标准 / 快速模型，4-15 秒，分辨率 480p / 720p |
| **HappyHorse 1.1** | 文生/图生视频 | 阿里云 HappyHorse，支持首帧图生视频 |
| **特效视频（生图+生视频）** | 文生/图生特效视频 | 绿幕输出，自动抠像生成 ChromaKey 材质，可一键在场景中创建特效播放器 |

### 🌍 世界生成

| 生成器 | 功能 | 特点 |
|--------|------|------|
| **World Labs Marble** | 文生/图生 3D 世界 | 依赖 Unity Gaussian Splatting 包，首次使用时会自动检测并安装 |

### 🎬 2D 序列帧生成

| 入口 | 生成器 | 功能 |
|------|--------|------|
| **2D 动作序列帧** | **2D 序列帧** | 图生角色动作序列帧（待机、向前跑、向后跑等），输出多帧 Sprite + AnimationClip |
| **2D 精灵表序列帧** | **Frontier Game Design** | 生成 4×4 整图精灵表，自动绿幕抠图、网格切割并导出 AnimationClip；支持 prompt 模板与参考图 |

### ⚙️ 架构特性

- **配置驱动架构**：所有生成器通过 JSON 配置文件定义，添加新生成器无需编写 C# 代码
- **公开 C# API**：支持在编辑器脚本中调用生成功能
- **任务恢复机制**：编辑器意外关闭后自动恢复进行中的任务；CustomTool（图片、图片分层、精灵、材质、音频、视频、特效视频、天空盒、地形、2D 序列帧、绑骨动画、图片放大、Game UI Kit 等）在 Domain Reload 后亦可自动恢复未完成生成；绑骨动画任务会持久化 `sessionId` 以便按会话恢复与通知
- **Play 模式保护**：Unity 播放期间禁用生成、资产搜索、下载及场景放置操作，避免退出播放后生成内容被丢弃；提示文案可点击直接退出 Play Mode
- **Placeholder 自动清理**：生成失败或取消时自动移除 Prefab 内残留 Placeholder 子对象；`AI/工具/清理占位 GameObject` 可批量扫描项目 Prefab 清理
- **历史记录管理**：按资产隔离历史记录，支持快速复用与在 Project 中定位；写入 `sessionId` 便于按 Agent 会话分组，可通过 `list_session_assets` CustomTool 查询
- **资产 Label 自动注册**：编辑器启动时自动写入 `TuanjieAI` 标签；精灵表序列帧资产额外写入 `TuanjieAI_Frontier`（代码常量 `SpriteSheetLabel`），便于 Project 搜索与 Inspector 路由
- **使用文档入口**：窗口标题栏帮助、`AI/✦ 玩转 AI 生成` 菜单，以及 Inspector「✦ AI 生成」按钮均链至官方使用指南
- **Inspector 快捷入口**：AnimationClip、材质纹理、Prefab 等资产 Inspector 标题栏可直接打开对应 AI 生成窗口（见 `TJGeneratorsInspectorButtons.cs`）
- **菜单与资产创建分离**：菜单项仅负责注册入口；占位资产创建逻辑集中在 `TJGeneratorsAssetCreation.cs`

## 安装要求

- Unity 版本：**2019.4 或更高版本**

## 安装步骤

### 方式一：通过 Packages 文件夹安装

1. 找到 Unity 项目目录中的 **`Packages`** 文件夹
2. 将 **`cn.tuanjie.ai.generators`** 包放入 **`Packages`** 文件夹

### 方式二：验证安装

安装完成后，可以通过以下方式验证：

- 检查 **Packages** 文件夹中是否存在 **Editor** 目录
- 打开菜单 **`AI`**，确认插件已正确加载

## 使用指南

### 菜单入口

| 菜单项 | 功能 |
|--------|------|
| `AI/生成/生成3D模型` | 打开 3D 模型生成窗口 |
| `AI/生成/生成天空盒` | 打开天空盒生成窗口 |
| `AI/生成/生成表面材质` | 打开表面材质生成窗口 |
| `AI/生成/生成精灵` | 打开 2D 精灵生成窗口 |
| `AI/生成/生成图片` | 打开图片生成窗口 |
| `AI/生成/生成2D动作序列帧` | 打开 2D 动作序列帧生成窗口（图生 idle / 跑等动作） |
| `AI/生成/生成2D精灵表序列帧` | 打开 2D 精灵表序列帧生成窗口（整图切割为序列帧） |
| `AI/生成/生成音频` | 打开背景音乐生成窗口 |
| `AI/生成/生成视频` | 打开视频生成窗口（含特效视频绿幕工作流） |
| `AI/生成/生成世界` | 打开 3D 世界生成窗口（World Labs Marble） |
| `AI/工具/图片切割` | 打开 **图片切割** 窗口，自动检测并导出大图中的独立元素 |
| `AI/工具/清理占位 GameObject` | 扫描项目内全部 Prefab，删除生成流程残留的 Placeholder 子对象 |
| `AI/搜索资产库` | 打开 **资产库搜索** 编辑器窗口 |
| `AI/✦ 玩转 AI 生成` | 在浏览器中打开 AI 生成工具使用文档 |
| `AI/搜索生成的资产` | 聚焦 Project，并按 AI 生成标签过滤搜索 |

上述 **`AI/开发`** 菜单项（运行测试、清缓存、一键清空历史、**一键清除所有轮询任务记录**、模板/图标工具等）需在定义 **`TJGENERATORS_DEBUG`** 后才显示。

#### Assets / GameObject 快捷创建

| 菜单路径 | 功能 |
|----------|------|
| `Assets/Create/3D/生成3D模型` | 创建 3D 模型生成占位资产 |
| `Assets/Create/3D/生成天空盒` | 创建天空盒生成占位资产 |
| `Assets/Create/3D/生成表面材质` | 创建表面材质生成占位资产 |
| `Assets/Create/3D/生成世界` | 创建 3D 世界生成占位资产 |
| `GameObject/3D Object/生成3D模型` | 在场景中挂接 3D 模型生成流程 |
| `GameObject/3D Object/生成天空盒` | 在场景中挂接天空盒生成流程 |
| `GameObject/3D Object/生成表面材质` | 在场景中挂接表面材质生成流程 |
| `Assets/Create/2D/生成2D精灵` | 创建 2D 精灵生成占位资产 |
| `Assets/Create/2D/生成图片` | 创建图片生成占位资产 |
| `GameObject/2D Object/生成2D精灵` | 在场景中挂接 2D 精灵生成流程 |
| `Assets/Create/2D/生成2D动作序列帧` | 创建 2D 动作序列帧占位资产 |
| `Assets/Create/Audio/生成音频` | 创建音频生成占位资产 |

### 快速开始

1. 通过菜单打开对应的生成窗口
2. 选择生成器（如 Tripo P1、Rodin、混元3.1 等）
3. 输入文本提示词或上传参考图片（部分生成器会显示字符计数与上限提示；**3D 模型窗口内切换生成器时，文本提示词在各模型间共享**；切到不需要文本输入的生成器时会自动清空）
4. 调整参数（可选）
5. 点击 **生成** 按钮开始生成
6. 生成完成后，资产自动保存到 `Assets/TJGenerators/` 目录

### 3D 模型格式说明

插件使用 Unity 原生导入 **FBX / OBJ**（及混元 3.1 输出的 **OBJ zip**），**不再依赖** `com.unity.cloud.gltfast`。本地绑骨等工具在工程内选择模型时支持 **FBX / OBJ**。

### 文生3D模型

1. 在文本输入框中输入模型描述，如"一把木椅"、"科幻风格的机器人"
2. 选择模型版本、风格、纹理质量等参数
3. 点击 **生成** 按钮

### 图生3D模型

1. 点击图片上传区域，选择参考图片
2. 可选：输入补充描述
3. 点击 **生成** 按钮

### 多视图生成3D模型

1. 展开 **多视图生成** 区域
2. 上传正面、左侧、背面、右侧四张图片
3. 点击 **生成** 按钮

## C# API 使用

### 基础用法

```csharp
using TJGenerators.Config;
using TJGenerators;
using TJGenerators.Generators;

// 1. 获取生成器配置（按 ConfigType + generatorId，3D 模型使用 ConfigType.Generator）
var config = ConfigManager.GetGeneratorConfig(ConfigType.Generator, "tripo");
var generator = new DynamicGenerator(config);

// 2. 设置输入
generator.SetTextPrompt("一把木椅");
// 或
generator.SetImagePath("/path/to/image.png");
// 或
generator.SetMultiViewPaths(new[] { "front.png", "left.png", "back.png", "right.png" });

// 3. 设置参数（可选）
generator.SetParameter("style", "object:clay");
generator.SetParameter("textureQuality", "detailed");

// 4. 启动生成
var context = TJGeneratorsGenerationContext.ForAssetPath("Assets/MyPrefab.prefab");
var handle = TJGeneratorsGenerationService.Generate(generator, context);

// 5. 处理事件
handle.OnCreated += h => Debug.Log($"任务创建: {h.BackendTaskId}");
handle.OnProgress += h => Debug.Log($"进度: {h.Progress}%");
handle.OnCompleted += h => Debug.Log($"完成: {h.ModelPath}");
handle.OnFailed += h => Debug.LogError($"失败: {h.ErrorMessage}");
```

### 为现有资产生成

```csharp
// 为现有 Prefab 生成新模型
var handle = TJGeneratorsGenerationService.Generate(generator, "Assets/Characters/Hero.prefab");

// 通过 GUID 生成
var handle = TJGeneratorsGenerationService.GenerateForGuid(generator, assetGuid);
```

## 配置驱动架构

TJGenerators 采用配置驱动架构，所有生成器通过 `Editor/Config/GeneratorConfig.json` 定义。

### 添加新生成器

在配置文件中添加：

```json
{
  "id": "new-model",
  "displayName": "新模型生成器",
  "enabled": true,
  "modelSelector": {
    "description": "模型描述",
    "functionTags": ["文生3D"],
    "vendorTags": ["厂商"]
  },
  "endpoints": [
    { "key": "text", "value": "task/new-model-text" },
    { "key": "image", "value": "task/new-model-image" }
  ],
  "uiLayout": {
    "showTextInput": true,
    "showImageUpload": true,
    "textInputLabel": "文本提示词",
    "primaryParameterIds": ["quality"]
  },
  "parameters": [
    {
      "id": "quality",
      "type": "dropdown",
      "label": "质量",
      "apiFieldName": "quality",
      "options": [
        { "value": "low", "label": "低" },
        { "value": "high", "label": "高" }
      ],
      "defaultValue": "high"
    }
  ],
  "responseMapping": {
    "downloadUrlPath": "model_url",
    "downloadUrlPathMultiview": "model_url_multiview",
    "previewUrlPath": "preview_image"
  }
}
```

保存后，通过菜单 **`AI/开发/清除配置缓存并重新加载`** 清除缓存即可生效（需启用开发宏 `TJGENERATORS_DEBUG`）。

### 支持的参数类型

| 类型 | 说明 |
|------|------|
| `dropdown` | 下拉选择框 |
| `int` | 整数输入框（支持 min/max） |
| `float` | 浮点数输入框（支持 min/max） |
| `bool` | 复选框 |
| `string` | 文本输入框 |

## 目录结构

```
Editor/
├── Config/
│   ├── GeneratorConfig.json                 # 生成器配置文件
│   └── SpriteSheetSequenceProfiles.json     # 2D 精灵表序列帧指令模板（网格规格、instructions、knowledge_refs）
├── CustomTool/                     # CustomTools（含特效视频等）
├── EditorTextures/                 # UI 图标和纹理
└── Scripts/
    ├── TJGeneratorsMenuItems.cs                 # 菜单入口（薄层，仅 [MenuItem] 注册）
    ├── TJGeneratorsAssetCreation.cs             # 占位资产创建与打开生成窗口
    ├── TJGeneratorsInspectorButtons.cs   # Inspector 标题栏「✦ AI 生成」集成
    ├── TJGeneratorsGenerationTestRunner.cs     # 编辑器内测试工具
    ├── TJGeneratorsGenerationLabel.cs          # 生成资产标签（Project 窗口）
    ├── Services/                         # 核心服务与会话
    │   ├── TJGeneratorsGenerationService.cs # 公开 C# API
    │   ├── TJGeneratorsHistoryManager.cs    # 生成历史管理
    │   ├── TJGeneratorsTaskRecovery.cs     # 任务恢复
    │   ├── CustomToolDomainReloadRecovery.cs # CustomTool Domain Reload 恢复
    │   └── UnityConnectSession.cs           # Unity/Codely 会话
    ├── Config/
    │   ├── ConfigManager.cs                  # 配置管理器
    │   ├── ConfigOptionsLoader.cs            # 配置选项加载
    │   ├── Models/                           # 配置数据模型（按类型拆分）
    │   │   ├── GeneratorConfig.cs
    │   │   ├── ParameterConfig.cs
    │   │   ├── PostProcessingConfig.cs
    │   │   └── VisualSelectorConfigs.cs
    │   ├── ConfigType.cs                     # 配置类型枚举
    │   ├── SpriteSheetSequenceImageOrderHint.cs    # 2D 精灵表序列帧参考图顺序提示
    │   └── SpriteSheetSequenceProfileConfigLoader.cs # 2D 精灵表序列帧模板加载
    ├── Models/                          # 共享类型与任务响应数据模型
    │   ├── TJGeneratorsGenerationHistoryItem.cs  # 生成历史条目
    │   ├── UserInfoModels.cs                     # 用户信息模型
    │   ├── TJTaskResponseModels.cs            # 任务响应数据模型
    │   └── TJGeneratorsAssetReference.cs           # 资产引用封装
    ├── Windows/                         # EditorWindow 主界面（各生成入口）
    │   ├── AIReferenceImageWindow.cs
    │   ├── TJGenerators3DModelWindow.cs
    │   ├── TJGeneratorsAssetWindowBase.cs # 资产绑定 2D 生成窗口基类（精灵/材质/图片/序列帧）
    │   ├── TJGeneratorsImageSliceWindow.cs
    │   ├── TJGeneratorsImageWindow.cs
    │   ├── TJGeneratorsMaterialWindow.cs
    │   ├── TJGeneratorsSpriteSheetSequenceWindow.cs # 2D 精灵表序列帧（整图切割）
    │   ├── TJGeneratorsMaterialTemplateGenerator.cs
    │   ├── TJGeneratorsMaterialTemplateSelectorWindow.cs
    │   ├── TJGeneratorsModelSelectorWindow.cs
    │   ├── TJGeneratorsMusicWindow.cs
    │   ├── TJGeneratorsSkyboxWindow.cs
    │   ├── TJGeneratorsSpriteSequenceWindow.cs
    │   ├── TJGeneratorsSpriteWindow.cs
    │   ├── TJGeneratorsTexturePatternSelectorWindow.cs
    │   ├── TJGeneratorsVideoWindow.cs
    │   └── TJGeneratorsWorldWindow.cs
    ├── UI/
    │   ├── GenerationWindowBase.cs      # 生成窗口基类
    │   ├── UIComponents.cs              # 通用 UI 组件
    │   ├── CommonStyles.cs              # 通用样式
    │   └── Model3DPreview.cs            # 3D 预览
    ├── Generators/
    │   ├── ModelGeneratorBase.cs              # 生成器基类
    │   ├── DynamicGenerator.cs                # 配置驱动生成器（UI、校验、请求）
    │   ├── DynamicTaskResponseResolver.cs    # 任务响应 URL / 文件名解析
    │   ├── DynamicRequestJsonBuilder.cs       # 动态请求 JSON 构建
    │   ├── ParameterJsonWriter.cs            # 参数写入 JSON 辅助
    │   ├── DynamicRequestModels.cs           # 请求/构建上下文模型
    │   └── IGeneratorParameterProvider.cs    # 参数提供者接口
    ├── Pipeline/
    │   ├── GenerationPipeline.cs               # 统一生成流程
    │   ├── GenerationBackendTransport.cs        # 后端 HTTP 传输
    │   ├── GenerationMediaAssetHandlers.cs      # 非 3D 媒体下载与写盘
    │   ├── IGenerationPipelineHost.cs          # 管线宿主接口（生命周期 / UI 触发 / 媒体资产）
    │   ├── HeadlessPipelineHostBase.cs         # CustomTool 等无界面宿主基类
    │   ├── GenerationBackendSyncSubmit.cs      # CustomTool 同步任务提交（UnityWebRequest）
    │   ├── PipelineSettings.cs                # 管线设置
    │   ├── PipelineApiModels.cs               # 管线 API 模型
    │   └── PipelineDownloadHelper.cs          # 下载与本地路径辅助
    ├── PostProcessing/
    │   ├── GreenScreenVideoPostProcess.cs     # 绿幕视频抠像材质
    │   ├── ImageSlicePostProcess.cs           # 图片切割分析/导出
    │   ├── RiggedModelPostProcess.cs          # 绑骨模型后处理
    │   └── SpriteSequencePostProcess.cs       # 序列帧后处理
    ├── AssetSearch/                          # AI 资产生命周期与 Project 搜索
    └── Utils/                                # 通用工具（路径、图片、地形、提示词长度限制等）
        ├── GenerationTaskTrackerStore.cs     # CustomTool 任务追踪与会话持久化（通用）
        ├── GenerationAssetFormatUtils.cs     # 资产格式检测（魔数 / URL）
        ├── GeneratedTextureImportUtils.cs  # 生成纹理 RGBA32 导入配置
        ├── ZipExtractor.cs                   # ZIP 解压
        └── TJGeneratorsPromptLimits.cs       # 各生成器 prompt 最大字符数
```

## 依赖

| 包 | 版本 | 用途 |
|----|------|------|
| `cn.tuanjie.codely.bridge` | 1.0.76 | Codely 桥接（提供 `Codely.Newtonsoft.Json`） |
| `com.unity.modules.jsonserialize` | 1.0.0 | `JsonUtility` 序列化 |
| `com.unity.modules.unitywebrequest` | 1.0.0 | HTTP 请求与资源下载 |
| `com.unity.modules.video` | 1.0.0 | 视频播放与特效视频工作流 |
| `com.unity.editorcoroutines` | 1.0.1 | 编辑器协程 |

> **说明**：早期版本曾依赖 `com.unity.cloud.gltfast` 导入 GLB；当前版本已移除该依赖，3D 资产以 FBX / OBJ 为主。

## 许可证

本项目采用 Unity 包形式分发，使用前请确保已获得相应授权。

---

享受使用 TJGenerators for Unity 进行游戏内容创作！🎉
