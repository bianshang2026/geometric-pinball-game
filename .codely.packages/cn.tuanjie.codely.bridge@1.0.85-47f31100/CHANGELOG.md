# Changelog

All notable changes to Codely Bridge will be documented in this file.

## [1.0.84] - 2026-09-14

### Added

- **Cowork 支持按项目暂停自动资源监听**（`b088ccb`、`b8c203e`、`1940db0`、`2bad5d6`、`73afad0`、`1b27c52`）：
  - 新增内部 `set` / `status` 动作并向所有连接的 Cowork 窗口广播状态；暂停状态保存在 `SessionState`，domain reload 后仍保持，关闭编辑器后恢复默认监听
  - 暂停时仅阻止编辑器聚焦触发的 AutoRefresh，工具主动发起的刷新与导入仍正常执行；恢复时在下一帧统一扫描并导入暂停期间的改动，同时兼容 Unity 6

## [1.0.83] - 2026-09-11

### Fixed

- **Bridge 更新检测改用原生 DLL 哈希，不再误判同版本 UPM 重注册**（`ce21879`、`fdf6a92`、`d9143fc`）：
  - UPM 在安装或移除其它包时会把 Bridge 重新列进 changedTo，此前即使版本与 DLL 均未变化也会卸载 socket 并弹重启提示；现在改为比较原生插件 MD5，字节相同则既不卸载也不提示
  - 首次记录只写入基线，离线替换 DLL 后打开编辑器不再误弹重启提示
  - DLL 哈希计算失败时输出警告日志，不再静默吞掉，便于排查漏检
- **macOS AI Marketplace 无法选择图片**（`d686fcc`）：
  - macOS WKWebView 需要宿主实现 `WKUIDelegate` 才会弹出文件选择框，此前页面上点击 `<input type="file">`（如生成页的“添加参考图”）没有任何反应；Windows WebView2 无此问题
  - 实现原生 `NSOpenPanel` 文件选择，主 frame 与嵌套 iframe 均生效，并同步更新 macOS `WebViewPlugin` 二进制

## [1.0.82] - 2026-09-09

### Added

- **`manage_asset.ensure_prefab_variant` 幂等创建 Prefab Variant**（`4b23d27`、`8067fbc`、`be40a4a`、`f7a6922`、`b41f636`、`3545673`）：
  - 传入 `path` + `sourcePrefab` 创建 Prefab Variant；目标已是同一 source 的 Variant 时直接返回成功（幂等），目标存在但不是 Variant 或基于其它 source 时报错
  - 拒绝 FBX/Model 源并提示先创建普通 Prefab；AssetDatabase 尚未导入 / 刚保存的目标按幂等重试处理，不再误报冲突；保存成功但读取资产信息失败时不误删已生成的 Variant
- **Bridge 包更新后弹窗提示重启团结编辑器**（`9afd0aa`、`1573e8a`）：原生插件与当前编辑器进程绑定，包更新需要完整重启而非 domain reload；更新后弹出 “Codely Bridge Updated” 对话框（Restart Now / Later），Restart Now 重开当前工程；pending 标记存于 SessionState（domain reload 后仍会提示），批量 / 自动化运行不弹窗

### Changed

- **`unity_asset` / `unity_shader` 动作面精简**（`2678ca9`、`1915c41`）：
  - `manage_asset` 移除 `ensure_has_meta`、`ensure_meta_integrity`、`create_folder`、`get_components`：组件列表并入 `get_info` 响应（含 Missing Script 槽位标记）；.meta 确保 / 完整性助手移除（refresh / import 本就会安全重建 .meta）；不再支持显式创建文件夹——`create` / `move` 会自动创建缺失的父目录
  - `manage_shader` 仅保留 `compile` 与 `preview`，移除与场景 / 材质工作流重复的 `detect_render_pipeline`、`ensure_material_shader_for_srp`
- **`manage_editor.refresh` 在 Play 模式下不再报错**（`cf83963`）：刷新前自动退出 Play 模式（Job 新增 ensure-edit-mode 步骤，已处于编辑模式时跳过），替代原先的 `refresh_blocked_in_play_mode` 错误
- **发布渠道切换为 CDN**（`e13099e`、`311e5cf`）：引入 `scripts/release-tools` 发布脚本与 `release_bridge` 手动流水线（打包、上传 TOS、校验 CDN、创建 release tag 并自动开 bump MR）；Packman 发布、兼容性注册与 1.0.0 别名退役

### Fixed

- **Windows 下输出路径含非 ASCII 字符时 MP4 录制失败**（`8faae3c`）：原生 MediaEncoder 在 Dispose 重写时间戳时对非 ASCII 路径报错（WindowsVideoMedia 0x8000ffff “Erase timestamps”）；编码先落到 `%TEMP%` 暂存文件，成功后再移动到目标路径
- **`manage_asset` 路径校验加固**（`5a5faa7`）：路径清洗不再盲信 `Assets/` 前缀，解析后强制结果留在项目 `Assets/` 内，越界直接报错而非探测 / 写到工程外

## [1.0.81-exp.1] - 2026-09-04

### Added

- **Game View 录制改用原生 MediaEncoder，并支持慢动作 encodeFps**（`691799c`）：
  - Windows / macOS 使用 Unity `MediaEncoder` 编码 H.264 MP4，不再依赖外部编码器
  - `start_game_view_recording` 新增 `encodeFps`（默认 2），低于采集 fps 时输出慢动作，方便低采样率模型看全每一帧

### Fixed

- **替换 meta 中的占位 GUID**（`b7a70ba`）：
  - 将 AI 生成的顺序占位 GUID 换成真实唯一值，避免导入时资源引用冲突

### Changed

- **端口注册与 handshake 行为调整**（`893e8f1`）：
  - 端口注册文件改到项目 `Temp/`；native heartbeat 不再写入 `stream_port`
- **更新各平台原生 TCP Bridge**（`893e8f1`、`a35e6d9`、`a2c1c5b`）：
  - 同步最新 Windows `NativeTcpBridge.dll`、macOS `libNativeTcpBridge.dylib`、Linux `libNativeTcpBridge.so`

## [1.0.80] - 2026-09-03

## [1.0.80-exp.1] - 2026-09-02

## [1.0.79-exp.2] - 2026-09-02

### Added

- **刷新资源数据库并返回最新诊断**（`94b2808`）：
  - `manage_editor.refresh` 同步刷新资源数据库，等待导入与编译结束后返回错误、警告和控制台信息；播放或暂停模式下会阻止刷新

## [1.0.79-exp.1] - 2026-08-31

### Added

- **显式 resume 动作**（`0cbeef3`）：
  - `manage_editor` 新增 `resume`，通过 `singleFrame` 选择单帧步进或持续播放；`pause` 支持 `targetPaused` 指定目标暂停状态，不再只能切换

### Changed

- **移除已废弃的 wait_for_upm**（`c8dddd3`）：
  - 删除 `wait_for_upm` 处理与对应 job，未知 action 统一走路由错误；`install_package` / `remove_package` 本身已等待完成
- **更新 Windows 原生 TCP Bridge**（`46040d2`）：
  - 同步最新 Windows `NativeTcpBridge.dll` 预编译产物

## [1.0.78] - 2026-08-28

### Changed

- **废弃 wait_for_bake**（`d0d0628`）：
  - `bake_lighting` / `bake_navmesh` 本身会等到完成，`wait_for_bake` 改为返回 `action_deprecated`，引导改用阻塞烘焙或 `wait_for_idle`
- **废弃 wait_for_upm，并处理传递依赖**（`99451ad`）：
  - `install_package` / `remove_package` 完成后才返回，`wait_for_upm` 改为返回 `action_deprecated`
  - 安装已作为传递依赖存在的包时返回 `already_present`；移除传递依赖时给出可操作错误

## [1.0.78-exp.1] - 2026-08-27

### Fixed

- **域重载期间保留待处理命令**（`8c1b74a`）：
  - 重载时暂停原生命令领取并恢复未完成租约，在新域的命令消费者与弹窗处理准备就绪后继续执行，避免排队命令丢失

## [1.0.77] - 2026-08-27

## [1.0.77-exp.1] - 2026-08-26

### Added

- **按模式拆分脚本执行**（`efe9e12`、`b8ef1c4`、`9071a49`、`8153fa6`）：
  - 新增 `exec_editor_script` / `exec_runtime_script`，由命令名选择编辑器或运行时模式；不再接受 `execution_mode`

### Changed

- **统一 Action 分发**（`e75ddbb`）：
  - 新增 `ActionRouter`，各工具改为字典路由，统一处理别名、默认 action 与未知 action 错误
- **精简 manage_asset**（`38c89de`）：
  - 删除批量资源操作，并重命名 action 别名
- **移除实验性 manage_ui_toolkit**（`c406b6d`、`6966515`）：
  - 先精简为 `ensure_panel_settings_asset`，随后整工具下线
- **Yamato 在 main 推送时跑测试**（`1fa666b`、`60465f9`）：
  - 推送到 main 时同样触发 bridge 测试，覆盖 merge commit 与直接推送

## [1.0.76] - 2026-08-21

### Added

- **Game View MP4 录制与原子化脚本验证**（`23102e7`、`6b3f359`、`ab3ff33`）：
  - 支持分段录制及 `execute_csharp_script.record_game_view`，提供 ffmpeg 编码、录制会话隔离、输出路径约束与生命周期清理
- **有状态 C# REPL 会话**（`18d05f1`、`74e4fcd`、`5654fae`）：
  - 支持跨调用变量、方法与类型复用，新增 `Repl.Id` / `Repl.ById`、`Repl.Vars` 及反射签名查询
- **自定义工具元数据与 Agent 附件**（`d3b9d53`、`bb24b5c`）：
  - 支持查询、刷新自定义工具注册信息，并允许 AgentInputNotifier 携带可选附件
- **Shader 变体编译与预览**（`89a3660`、`85e7c32`）：
  - 新增 `ShaderVariantApi` 与 `manage_shader` 的 `compile` / `preview`，按变体模式预热并渲染预览帧
- **Scene View 多视角截图**（`efb4fb0`）：
  - `capture_scene_view` 支持多方向拍摄；时间戳精确到毫秒，避免连拍文件名冲突
- **编译流水线内嵌控制台**（`6840981`）：
  - `start_compilation_pipeline` 完成后直接返回控制台诊断，无需再单独读取 console

### Changed

- **C# 脚本异步执行与会话安全**（`92d79ad`、`5ffc251`、`ed3b86b`）：
  - 支持 Task / IEnumerator 跨帧完成，REPL 默认改为关闭，并增加超时、取消及域重载操作保护
- **C# 执行结果与控制台输出整理**（`ae08d2f`、`56ca366`）：
  - 统一返回脚本结果并移除控制台消息内嵌堆栈，减少重复输出
- **精简 manage_shader**（`85e7c32`）：
  - 删除 shader CRUD，仅保留 `detect_render_pipeline` / `ensure_material_shader_for_srp` / `compile` / `preview`

### Fixed

- **C# 执行死锁、会话延续与结果格式化**（`c8dde2c`、`c387e95`、`3d73799`、`4198b41`）：
  - 修复阻塞调用、迭代器误判、REPL 自动修复延续及嵌套调用破坏会话状态等问题
- **串流窗口与复合布局恢复**（`d41da2f`、`b740d8f`、`24677d3`、`092fc22`）：
  - 修复遮罩窗口残留或消失，以及应用布局后未激活聚焦复合槽位的问题
- **原生插件平台配置**（`8dc1ff3`）：
  - 修正 Windows、Linux 与 macOS 原生插件的目标平台和 CPU 设置
- **Shader 编译错误过滤与预览防护**（`de261c1`）：
  - 过滤过期 shader 错误，编译/预览前校验，避免损坏变体提交 GPU compiler 导致卡住
- **Scene View 线框渲染**（`efb4fb0`）：
  - 非 Shaded 模式改走 `GL.wireframe`，修复 wireframe / shadedwireframe 被内部相机反射路径忽略的问题

## [1.0.76-exp.1] - 2026-08-18

### Added

- **Game View MP4 录制与原子化脚本验证**（`23102e7`、`6b3f359`、`ab3ff33`）：
  - 支持分段录制及 `execute_csharp_script.record_game_view`，提供 ffmpeg 编码、录制会话隔离、输出路径约束与生命周期清理
- **有状态 C# REPL 会话**（`18d05f1`、`74e4fcd`、`5654fae`）：
  - 支持跨调用变量、方法与类型复用，新增 `Repl.Id` / `Repl.ById`、`Repl.Vars` 及反射签名查询
- **自定义工具元数据与 Agent 附件**（`d3b9d53`、`bb24b5c`）：
  - 支持查询、刷新自定义工具注册信息，并允许 AgentInputNotifier 携带可选附件

### Changed

- **C# 脚本异步执行与会话安全**（`92d79ad`、`5ffc251`、`ed3b86b`）：
  - 支持 Task / IEnumerator 跨帧完成，REPL 默认改为关闭，并增加超时、取消及域重载操作保护
- **C# 执行结果与控制台输出整理**（`ae08d2f`、`56ca366`）：
  - 统一返回脚本结果并移除控制台消息内嵌堆栈，减少重复输出

### Fixed

- **C# 执行死锁、会话延续与结果格式化**（`c8dde2c`、`c387e95`、`3d73799`、`4198b41`）：
  - 修复阻塞调用、迭代器误判、REPL 自动修复延续及嵌套调用破坏会话状态等问题
- **串流窗口与复合布局恢复**（`d41da2f`、`b740d8f`、`24677d3`、`092fc22`）：
  - 修复遮罩窗口残留或消失，以及应用布局后未激活聚焦复合槽位的问题
- **原生插件平台配置**（`8dc1ff3`）：
  - 修正 Windows、Linux 与 macOS 原生插件的目标平台和 CPU 设置

## [1.0.75] - 2026-08-13

## [1.0.75-exp.1] - 2026-08-04

### Added

- **Scene View 所见即所得截图**（`40ae2a3`、`731b1c4`）：
  - 新增 `capture_scene_view`，支持按当前编辑器视角、渲染模式、时间序列及 GIF 捕获 Scene View
- **Yamato 代码覆盖率与发布流程指引**（`8b9eaf2`、`aa0a889`）：
  - 新增 EditMode 覆盖率采集、报告生成与归档任务，并补充 Bridge 发布及版本更新指引

### Changed

- **原生 TCP Bridge 与日志精简**（`969f2d6`）：
  - 更新 Windows、macOS、Linux 原生插件，移除逐命令 verbose 处理日志
- **UPM CI 打包与测试产物调整**（`aa0a889`、`c9aec68`）：
  - 统一传递完整 `upm-ci~` 产物并更新测试命令、产物名称；从 npm 包排除测试配置 meta 与内部发布文件

### Fixed

- **编辑模式 Game View 截图陈旧**（`2713352`、`24c7794`、`cb2ce01`）：
  - 捕获前强制刷新 Game View 与编辑器渲染循环，避免场景或 UI 变化后仍读取缓存帧

## [1.0.74] - 2026-08-01

## [1.0.74-exp.1] - 2026-08-01

### Added

- **编辑器消息注入 Agent 输入框**（`79d38a4`、`3a4ddd5`）：
  - 新增 `AgentInputNotifier`，广播 `input_from_editor` 事件，将编辑器侧消息推入 Agent 输入框
  - Codely 窗口未就绪时自动打开并排队；前端可通过 `manage_editor.drain_agent_input` 在页面加载完成后取队列
- **Yamato Windows 多版本 EditMode 测试与 CI 加固**（`dfd7bd4`、`2e0be74`、`272e8cf`、`6afd6aa`）：
  - 拆分 TUANJIE/UNITY 版本变量，迁移 Windows EditMode 到新镜像与物理机，增加 flaky 重试
  - 恢复 `Tests` 目录并修复 EditMode 失败；补充 `EditorAutomationGuard` meta 与 `TestRunnerOptions.json`

### Changed

- **精简协调面：移除 manage_workflow 与 console token**（`71cbcbf`、`e1f4dfc`）：
  - 删除 `manage_workflow`（保留 stub 指引改用 `manage_editor.start_compilation_pipeline`）
  - 取消增量 console token；`wait_for_idle` 改为等待 JobRegistry 全部完成且非编译/导入
  - `wait_for_compile` / `wait_for_stop` 等返回 `action_deprecated`；错误路径改用 `Response.Error`（`12dcaba`）
- **串流启动时隐藏迷你窗口**（`e2abe93`）

### Fixed

- **复合串流停止后黑屏僵尸窗与编辑器消失**（`762d22a`、`9cf0016`）：
  - 释放时销毁僵尸 HWND，并在 layout/stop 路径关闭空孤儿 ContainerWindow，避免误关主编辑器窗口
- **复合/远程串流纵向抖动与 resize 循环**（`42b9cbd`、`c2ae1f3`）：
  - 复合/远程模式跳过 `DeactivateUnityKeepWindows`（除非任务栏激活），并收紧 reclaim 告警噪音
- **停靠 GameView 压缩串流 UI**（`c04029f`）：
  - 串流前关闭停靠 GameView，每帧强制刷新 PanelSettings，停止后恢复停靠状态
- **GameView 播放区误点 PaneOptions**（`d1484ee`）：
  - Play 模式下跳过游戏区域的 PaneOptions 命中拦截
- **Windows 串流遮罩抢焦点**（`872aff2`）：
  - Windows 推迟显示 streaming mask，直至用户经任务栏激活 Unity
- **Unity 6 兼容与 CI batchmode**（`3357aac`、`4ae138e`、`80480cf`）：
  - 实例 ID 改用 long、修正 `FindObjectsByType` 版本守卫；batchmode 下跳过 Bridge 自动启动
- **Win32 模态对话框扫描器 Mono 委托崩溃**（`3a4ddd5`）：
  - 主线程预创建枚举回调并复用长生命周期委托

## [1.0.73] - 2026-07-28

## [1.0.73-exp.1] - 2026-07-27

### Added

- **模态对话框检测与后台命令泵**（`a095256`、`0daf2b9`）：
  - 新增 `BackgroundCommandPump`，在主线程被原生模态对话框阻塞时仍可处理线程安全命令
  - 新增 `DialogWatcher` / `ModalDialogScanner`（Win32/macOS），检测模态对话框、分离在途 Job，并跨线程点击按钮
  - 新增 `manage_dialog` / `manage_job` 命令，以及 `JobRegistry` / `JobControl` 跟踪 attached/detached Job
- **New Input System 鼠标状态注入**（`206197c`）：
  - 离屏串流场景下通过反射 `QueueStateEvent` 注入 Mouse 位置与按键，修复 InputAction 绑定拖拽/点击失效

### Fixed

- **对话框/Job 后台处理评审修复**（`0daf2b9`）：
  - 原生 dequeue 串行化，避免超时 Stop 后残留 fetch 线程并发出队；仅在 Editor update 心跳停滞时升级扫描到的对话框；修复 DetachedJobs 旧字段加载后立刻过期
- **串流鼠标注入与恢复**（`31441c4`、`5acde62`、`4602051`、`26b445f`）：
  - 修正同批 mousedown/mouseup 的拖拽检测；串流结束后通过 domain reload / LegacyMouseBridge 恢复 InputSystem；恢复 raw input 注册并清理卡住的鼠标按键状态

## [1.0.72] - 2026-07-21

## [1.0.72-exp.1] - 2026-07-21

### Added

- **UI Toolkit 流式指针注入兜底**（`33b5fca`）：
  - 在 UI Toolkit 场景下补齐 streaming pointer injection fallback
- **支持 client DPR 以提升高 DPI 采集清晰度**（`32f786e`）：
  - 根据 client DPR 调整采集逻辑，提升高 DPI 捕获效果

### Changed

- **替换 AsyncOperationTracker 轮询：改为 StepJob 运行到完成**（`191e3f5`）：
  - 用 run-to-completion jobs 驱动流程，降低轮询带来的回归风险
- **简化 UI Toolkit 释放注入**（`4f2dd93`）：
  - 精简 release 注入逻辑，统一 streaming 注入路径
- **停止在每个工具响应中附带环境编辑器状态**（`82b58eb`）：
  - 减少无关状态传输，避免后续解析噪音
- **更新 mac 平台 native 库版本**（`184e95d`）：
  - 将 native lib 更新到 1.0.0 以匹配平台需求

### Fixed

- **修复 StepJob runner 回归并补齐覆盖率**（`7a14764`）：
  - 纠正 runner 行为并新增测试覆盖，避免回归
- **域重载后恢复离屏窗口位置**（`6b595ed`）：
  - 处理 off-screen window positions，在 domain reload 后回到可见状态
- **复合窗口桥接 DPI 缩放修正**（`b564df2`）：
  - 修复 composite window bridge 的 DPI scaling 计算
- **UI Toolkit 流式拖拽增量与兜底修复**（`6de5594`）：
  - 修复 streaming drag delta，并补齐 UI Toolkit fallback
- **修正 Letterboxed 视图下 GameView 输入坐标**（`7684f9b`）：
  - 在 letterboxed 场景下使用正确坐标映射
- **支持 plain VisualElements 上的 Clickable manipulators**（`0ea02d5`）：
  - 让 Clickable manipulators 能在普通 VisualElements 工作
- **修复 unity-script 依赖问题**（`5bb6aec`）：
  - 修复 unity-script 相关依赖，保证脚本可用

## [1.0.71] - 2026-07-17

## [1.0.71-exp.1] - 2026-07-17

### Added

- **Bridge 停止原因上报与无重载包更新后自动重启**（`863ed8d`、`859da3d`）：
  - 贯通 `manually_stopped` / `unity_quit` / `package_updating` / `package_removed` 等停止原因至握手文件；无脚本变更的包更新跳过重载时从 `Events.registeredPackages` 自动重启 Bridge；同步重建各平台 native 插件
- **声明式脚本无顶层语句检测测试**（`c4ce285`）：
  - 新增 `NoTopLevelStatementsTests`，覆盖拒绝路径、合法用例与嵌套语句场景

### Changed

- **Tauri 实例生命周期策略调整**（`c1ca350`）：
  - Unity 不再主动销毁 Tauri 实例，精简 `CodelyWindow` 相关逻辑

### Fixed

- **声明式脚本空跑与编译诊断**（`c4ce285`、`ea13615`）：
  - `execute_csharp_script` 在 emit 前拒绝仅含类型声明的脚本，避免无效果的成功返回；将检测移至编译成功后以返回真实 Roslyn 诊断，并移除易误导的 `SuggestEntryCall`

## [1.0.70-exp.3] - 2026-07-16

### Fixed

- **Unity 2019 原生桥接崩溃修复**（`7df5956`）：
  - 新增 ABI 版本握手与 handle 有效性探测，避免 domain reload 后误用无效句柄或 ABI 不匹配的原生库；同步更新各平台 native 插件
- **复合布局 GameView 鼠标坐标修正**（`c9385ce`、`a46c8cc`）：
  - composite 模式下 rawX/rawY 相对整帧而非 GameView 渲染纹理，导致点击与 uGUI 拖拽注入位置错误；改用 GameView 本地 gameRawX/gameRawY
- **New Input System 按键解析加固**（`d01d4d7`）：
  - `TryParseKey` 改用 `Enum.IsDefined` + `Enum.Parse`，无效键名返回 null 而非默认值

## [1.0.70-exp.2] - 2026-07-16

### Fixed

- **ReadConsole 绕过编辑器 UI 过滤**（`0e365c5`）：
  - 读取控制台时临时绕过 Collapse / Log·Warning·Error 级别开关与搜索过滤，避免被 Console 工具栏隐藏的日志读不到

## [1.0.70-exp.1] - 2026-07-16

### Added

- **编辑器循环 Task/协程 Runner**（`114ba85`、`5ab56bf`）：
  - 新增 `AsyncTaskRunner` / `CoroutineRunner`，支持 `execute_csharp_script` 调度异步 Task 与协程（含 `WaitFor*`），并将 `manage_input` / 截图流程迁至非阻塞 Job 模型
- **New Input System 与 uGUI 拖拽注入**（`f8ca500`、`ba331fa`）：
  - 通过反射 `QueueStateEvent` 注入键盘状态，补齐离屏/串流场景下 New Input System 输入
  - 支持虚拟摇杆等 drag-handler 的 uGUI OnDrag 注入，并抽取 GameView 坐标辅助方法
- **编译进度 DataChannel 转发**（`7783f79`、`4389cb3`）：
  - 将脚本编译进度转发至前端；domain reload 完成后发送 `domain_reload_complete`；Unity 2019 提供 CompilationPipeline 事件回退
- **异步退出 PlayMode 等待**（`b10d8cb`）：
  - 新增 `wait_for_stop`，支持异步等待 PlayMode 退出完成

### Changed

- **心跳写入下沉至原生 TCP Bridge**（`3a38433`、`0c04c2c`）：
  - `.com-unity-codely.json` 心跳改由 native 层维护，移除 `StatusHelper` 并精简 `PortManager`；同步更新各平台 native 库
- **observation-invalidated 归因与节流**（`c42f5f6`、`94b5f04`）：
  - 通知载荷补充 `source` / `pending`；5s 静默窗口内节流合并，避免会话期间通知洪水
- **窗口透明度刷新节流**（`e3d9612`）：
  - `ReapplyWindowTransparency` 限制为最多每 250ms 一次，降低离屏串流帧开销

### Fixed

- **脚本异步失败与协程清理**（`5ab56bf`、`7b738e4`）：
  - Task 失败以 tool error 返回；协程异常/超时处置 enumerator 栈以执行 `finally`；去重 log capture
- **Canvas 布局与输入反射加固**（`ba331fa`）：
  - SetFocus 后恢复 Canvas 渲染尺寸，避免 ScreenSpaceOverlay UI 偏移；加固 Input System 反射缓存与按键解析
- **observation 源归因与脚本拒绝语义**（`94b5f04`、`dbe5937`）：
  - dirty 源在 MarkDirty 时捕获，避免误归因到后续 flush 命令；`execution_mode` 不匹配时返回 Error 而非 Success
- **ReadConsole Unity 6 反射修复**（`38848d2`、`9f47466`）：
  - 修复最新 Unity 6 下控制台日志反射读取失败
- **Drill 窗口交互与完成态**（`59c686d`）：
  - 保持 drill 窗口控件与 completion 响应可用，并修复 cowork 缺失时的回退打开路径
- **EnumWindows 原生崩溃规避**（`208913e`）：
  - 改用 `GetWindow`/`GetDesktopWindow` 遍历 HWND，避免 shell extension 触发 `STATUS_DLL_INIT_FAILED` 原生崩溃

## [1.0.69-exp.1] - 2026-07-09

### Added

- **编辑器测试 CI 覆盖补齐**（`d375edb`）：
  - 新增 bridge EditMode tests 的 Yamato CI 流程，提升回归覆盖
- **Input System 依赖声明补齐**（`fd9b66a`）：
  - package 依赖中新增 Input System，为可选输入能力提供安装基础
- **脚本执行异常可观测性增强**（`ee42f50`）：
  - `CSharpScript.EvaluateAsync` 抛错时返回 stacktrace，便于定位运行期脚本问题

### Changed

- **CI 任务中包拷贝流程优化**（`dcd9b53`、`0255468`、`77e7670`）：
  - 调整 package copy 方式并改用显式 `PACKAGE_NAME`，提升流水线稳定性
- **输入系统集成策略调整**（`2d5f6c0`）：
  - 通过 `CODELY_INPUT_SYSTEM` 编译开关将 Input System 改为可选集成，降低无依赖场景接入成本

### Fixed

- **平台守卫与条件编译修复**（`89bb341`、`ddea6d7`）：
  - 为 `reuseDetachedTarget` 增加平台 guard；修正预处理分支与测试断言，避免跨平台分支误行为
- **Roslyn 引用与测试可见性恢复**（`0a9109a`）：
  - 恢复 Roslyn 引用和 `InternalsVisibleTo`，修复编辑器测试编译/运行失败

## [1.0.68] - 2026-07-09

### Added

- **Unity 6.5（6000.5）兼容性支持**（`ae78b49`、`98fa895`、`699dfc5`）：
  - 引入 EntityId 兼容层（`InstanceIdExtensions`），统一封装 Unity 6.5 中被弃用为错误的 int 实例 ID API（`GetInstanceID`、`InstanceIDToObject`、`Selection.activeInstanceID` 等），全链路改用 64 位 `long` 承载，确保 ID 在存储与传输中无损往返
  - 修复 Unity 6.5 下 bridge 无法安装的问题（AICODE-2127）
- **输入模拟与 GIF 捕获能力增强**（`f1348f6`、`b8f6f51`）：
  - 新增 `manage_input` 桥接命令，支持 PlayMode 下的输入模拟
  - 为 `ManageScreenshot` 新增增量式内存 GIF 捕获 API
- **复合布局重连能力**（`33a5f27`）：
  - 支持在 detached-hidden 重连场景下保留 composite 布局

### Changed

- **Composite 功能模块化与持久化重构**（`51a61ea`、`af73df6`、`f3d2e85`）：
  - 将 composite 相关功能拆分到独立文件，提升可维护性
  - 改进 composite 窗口在 domain reload 后的持久化，并补充 slot 管理的 verbose 日志
- **脚本编译性能优化**（`f3d7dbf`）：
  - `ExecuteCSharpScript` 的阻塞调用扫描复用 Roslyn 编译结果，降低重复编译开销
- **构建与发布流程调整**（`921fd35`、`af1ca8b`、`93b1010`、`58eb69b`）：
  - 非 canary 版本仅允许从 release 分支构建；实验版本仅上传至 TOS；从版本号直接推导 `IS_CANARY_RELEASE`；移除多余调试日志

### Fixed

- **Composite slot 稳定性修复**（`78bc94e`、`4daf696`）：
  - 校验尺寸并延迟 handle 重新获取，加固 composite remap 与 slot handle 重取逻辑
- **拖拽与输入交互修复**（`41c4f8b`、`b19e00b`、`f1bb14b`、`6966ea8`）：
  - 忽略低于阈值的细微拖拽抖动；修正 `manage_input` 的 UI 拖拽目标与协程释放；修复拖拽 popup 阻塞问题；为 Unity 2019 增加版本感知的 tab-drag 守卫
- **截图与离屏窗口修复**（`f59e040`、`0d4d08e`、`8f89316`）：
  - 加固 `ManageScreenshot.CaptureFrame` 的纹理缩放；修复离屏窗口处理的 fallback 与缩进问题；编辑器失焦时支持全速 PlayMode 与 GIF 捕获
- **脚本编译流程修复**（`c8dae67`、`c308966`）：
  - 脚本编译前自动保存 dirty 场景；修复 `RequestScriptCompilation` 重入问题

## [1.0.67] - 2026-07-03

### Added

- **拖拽与场景交互能力增强**（`abe18de`、`af8bfa0`、`dfc44e6`、`6363eac`）：
  - 新增将资源拖拽到场景进行交互的能力，补齐编辑器内拖拽工作流
  - 修复并优化 drag-and-drop 相关链路，提升稳定性与可用性

### Changed

- **原生桥接平台与事件追踪路径重构**（`47c12c0`、`af2aa43`、`6c1f17a`、`2012543`、`2de2568`）：
  - 为 Native Bridge 增加平台抽象层，并移除 Native Window Bridge 的 Linux 平台支持
  - `CodelyEventTracker` 改为以 HTTP invoke 为主，补充 pending metrics 的即时 flush 机制，提升生命周期事件上报可靠性
  - 增加原生日志回调的平台守卫，降低跨平台构建风险
- **截图与异步流程优化**（`e25b143`、`06f1317`）：
  - 优化 `PrintWindow` 捕获流程，引入 viewport cropping 提升截图效率
  - 让异步任务在 reload 后继续可追踪，并在缓存过期场景下自动重试编译

### Fixed

- **复合布局输入稳定性修复**（`1d4bca0`）：
  - 修复 composite layout 下键盘输入处理问题，降低输入丢失与交互异常风险

## [1.0.66] - 2026-06-30

### Added

- **Popup 菜单与标签页交互能力增强**（`d2378a0`、`934d4ff`、`8986725`、`d346e60`）：
  - 新增 `PopupMenu` 能力并重构 `NativeWindowBridgeHost` 相关路径，补齐左键下拉（`DisplayPopupMenu`）与标签页表头菜单触发链路
  - 新增标签页表头 context menu 能力与 composite 自动恢复机制，完善串流场景下标签页操作
  - 新增 `ProjectBrowser` 过滤器场景下的 `PopupList` 支持，覆盖更多编辑器原生弹出菜单类型
- **GUID 修复脚本与校验流程增强**（`99df5b2`、`99b8ba9`）：
  - 新增用于修复无效 Unity `.meta` GUID 的脚本
  - 改进 GUID 校验 hook 与脚本交互体验，降低 `.meta` GUID 异常导致的导入问题

### Changed

- **上下文菜单与指针事件路径优化**（`b604490`、`c139b04`）：
  - 优化上下文菜单日志与关闭（dismissal）流程，减少噪声并提升可观测性
  - 优化指针事件处理与 tooltip 开销，降低高频交互路径的性能负担
- **开发流程文档与仓库整洁度更新**（`3287efa`、`e70351b`）：
  - 新增 Git hooks 配置文档

### Fixed

- **弹窗定位与命中检测修复**（`2a78541`、`91ace8d`、`9094bf0`、`8664761`）：
  - 统一 composite 视图下的 popup 定位计算逻辑，修复弹窗位置偏移问题
  - 提升 PaneOptions 按钮 hit-test 精度，减少误判与误触发
  - 修复上下文菜单处理与原生 popup 关闭流程的稳定性问题

## [1.0.65] - 2026-06-29

### Added

- **TextMeshPro Essentials 自动导入**（`181108d`、`96c1627`、`07c7e49`）：
  - 新增 `TmpEssentialsAutoImporter`，在工具触发安装 `com.unity.textmeshpro` 后静默执行 `Window/TextMeshPro/Import TMP Essential Resources`，避免首次使用 TMP 时缺少 Essentials 资源
  - `execute_csharp_script` 在脚本包含 `TextMeshProUGUI` 时自动补齐 TMP Essentials；`manage_editor` 编译前检查也会识别 TMP UI 依赖并触发导入
  - `execute_menu_item` 执行 TMP Essentials 菜单项后同步调用导入流程，并通过 `Response.SuccessWithData` 返回更结构化的成功数据

### Changed

- **遥测事件追踪增强**（`0ee3c00`、`6d29f7e`、`1644599`）：
  - 重构 `CodelyEventTracker` 与 IPC 消息处理，将欢迎页 / 面板生命周期事件统一到事件追踪入口
  - `codely_panel_opened` / `codely_panel_closed` 等面板事件接入生命周期追踪，并对需要用户标识的事件补充 user ID
  - `CodelyIpcManager` 补充连接状态回调，使事件在 IPC 连接变化时能更可靠地投递

## [1.0.64] - 2026-06-25

### Added

- **`manage_gameview` 工具 + 场景视图渲染模式 + GIF 截图**（`191c384`、`0240e83`）：
  - 新增 `manage_gameview` 工具，支持获取 / 设置 / 列举 Game View 分辨率；接入 `CommandRegistry` 与 `UnityTcpBridge`，并在 `StateDirtyPolicy` 中标记为非 dirty 动作，避免产生多余的 observation-invalidated 通知
  - `ManageScreenshot.capture_scene_view` 新增 `render_mode`（`shaded` / `wireframe` / `shadedwireframe`）；`shadedwireframe` 将 shaded pass 与双背景 wireframe 蒙版合成，做到与渲染管线无关
  - `capture_game_view` 新增 `scale` 参数（0.25–1.0，默认 0.5）
  - 新增 `as_gif`：录制 N 帧并编码为动画 GIF（`frameCount` / `fps` / `colorCount` / `startDelay`），覆盖 `game_view` / `scene_view` / `main_camera` / `specific_camera`；内置自包含的 median-cut + LZW GIF89a 编码器，逐帧延迟按实际抓帧间隔测量
  - `DrawLabelOnTexture` 将标签高度限制在图像高度的 1/5 并按宽度自适应
- **遥测事件追踪（经 IPC 上报 Tauri 后端）**（`d1c17ea`）：新增 `CodelyEventTracker`，从 Unity Bridge 经 IPC `trackEvent` 消息向 Tauri cowork APP 上报 6 个分析事件——`welcome_window_shown` / `welcome_window_closed`（`DrillWindow` OnEnable / OnDestroy）、`welcome_button_clicked`（`OnDrillCompleted`）、`codely_panel_opened`（`CodelyWindow` OnEnable）、`codely_panel_closed`（OnDisable，置于 IPC release 之前以保证可投递）、`codely_panel_input_submitted`（Tauri 侧经 `llm/streamChat` 处理）
  - `CodelyIpcManager` 缓冲 IPC 连接建立前发送的消息并在连接后 flush，避免事件静默丢失
  - `CodelyIpcServer` 的 `IpcMessageType` 新增 `TrackEvent`；`TauriUtils.GetEditorType()` 增强 tuanjie 检测（回退到 `m_EditorVersion` 的 `t` 后缀判断）
- **串流标签页 "more options" 弹出菜单**（`2a7566d`、`e0bcf09`）：新增 `NativeWindowBridgeHost.PaneOptions.cs`，为合成 / 串流模式下的 `DockArea` 标签页提供窗口选项（kebab / more action）按钮的弹出菜单提取与回调处理，使串流前端能正确触发标签页的更多操作
- **串流图标选择器（icon picker）**（`8a9dc08`、`2821905`）：新增 `NativeWindowBridgeHost.IconSelector.cs`，为串流场景提供简易的图标选择弹窗支持；并修复其在 Unity 2019 上的编译错误
- **合成模式新弹窗自动停靠到 DockArea**（`a593f90`）：增强多窗口合成（composite）串流——合成开始后自动检测并停靠新出现的 `EditorWindow`，覆盖经菜单项创建与右键 `Add Tab` 创建的窗口，确保它们被纳入合成串流捕获
  - 新增 `NativeWindowBridgeHost.PopupDock.cs`：`InterceptAndDockNewPopupWindows`（自动停靠新弹窗）、`TrackUntrackedCompositePanes`（检测原生新增标签页）、`InitCompositeWindowTracking`（快照基线窗口 ID 集合）
  - 基于反射的标签页移动，并对 Windows / macOS 分别处理；将弹窗检测与 `Add Tab` 右键菜单检测集成进 `ManageWindowBridge` 工作流

### Changed

- **更新 Windows 原生二进制**（`3acfb27`）：更新 `Plugins/Win` 下的 `NativeWindowBridge.dll` 与 `datachannel.dll` 至最新构建
- **新增脚本后编译前先 `AssetDatabase.Refresh()`**（`2660b00`）：经 `write_file` 或外部工具新建的脚本在触发编译前先执行 `AssetDatabase.Refresh()` 导入，确保新增脚本被纳入本次编译（`CompilationHelper`、`ManageEditor`）
- **暂时禁用 `SceneHierarchyNotifier` 与 `ConsoleLogChangedNotifier`**（`1f73a84`）：关闭这两个推送通知器

### Fixed

- **AICODE-1977 `execute_csharp_script` 非 UTF-8 脚本编码乱码**（`f723f56`）：修复脚本文件以本地 ANSI 代码页（如 zh-CN Windows 的 GBK/936）保存时，`File.ReadAllText` 默认按 UTF-8 解码导致中文标识符 / 字符串变成 '�' 并触发 Roslyn `CS1056` 的问题——新增 `ReadScriptFileSmart` 编码自动探测：优先识别 BOM（UTF-8 / UTF-16 LE / BE），无 BOM 时手动校验字节是否为合法 UTF-8（Mono 的 `UTF8Encoding.throwOnInvalidBytes` 不可靠，会静默替换为 '�' 而不抛异常），否则按结构校验回退到 GBK/936，再不行才用系统默认页并大声告警；脚本路径含 `U+FFFD` 时给出明确的"路径已在 TCP 层被非 UTF-8 解码、原始字节不可恢复"诊断提示，替代含糊的 not-found
- **`manage_gameobject` find 对缺失脚本组件抛 NRE**（`e60a32c`）：含缺失 / 损坏脚本引用（脚本被重命名 / 删除、编译错误、或非 UTF-8 脚本导入失败）的 GameObject，其 `GetComponents<Component>()` 会返回 null 项；`find` 动作序列化时在 `GetGameObjectData` 的 `componentNames` 中解引用这些 null，抛出以 "Internal error processing action 'find'" 暴露的 `NullReferenceException`，现已加 null 防护（`GameObjectSerializer`、`ManageGameObject`）

### Removed

- **移除 WebRTC 串流功能**（`1194445`）：移除基于 Unity RenderStreaming 包的 WebRTC 串流特性（已弃用），简化代码并减少依赖——删除 `ManageWebRtcStream` 工具及其 `.meta`、从 `CommandRegistry` 移除 `manage_webrtc_stream` 命令、从 `UnityTcpBridge` 移除 WebRTC 命令路由、从 `CodelyIpcServer` 移除 WebRTC IPC 消息类型与处理器；同步更新原生 `NativeWindowBridge.dll` / `datachannel.dll`

## [1.0.63] - 2026-06-22

### Added

- **合成窗口选择变更同步与 domain reload 句柄重获**（`f289ec5`）：大幅增强多窗口合成（composite）串流的捕获与状态同步
  - 新增选择变更监听与通知机制，selection 改变时主动驱动合成槽位重绘并向前端同步状态，确保 Inspector 等随选择刷新的窗口在串流端及时更新
  - domain reload 后重新获取各合成槽位（composite slot）的原生窗口句柄，避免脚本重编译后句柄失效导致黑帧 / 不刷新
  - 新增 `ForceRepaintImmediately`，对离屏窗口执行同步立即重绘，消除合成更新延迟
  - 新增 `NativeWindowBridgeHost.Json.cs` 共享 JSON 解析工具，捕获操作后恢复 GL 矩阵（避免抓帧改写渲染状态），并将 `EditorWindowNativeHandleHelper` 改为 `public` 以供合成槽位访问
  - 改进 `PrintWindow` 抓帧的 DPI 缩放与日志
- **串流标签页拖出自动重路由为 AddTab**（`54198be`）：新增 `NativeWindowBridgeHost.TabDrag.cs`，修复串流（合成 / 离屏）模式下把 Editor 标签页拖出停靠区时的问题——在串流画布中央释放标签页时 Unity 会新建一个无法被串流的浮动 `ContainerWindow`（`DropInfo.dropArea == null`）；改为经反射检测该情形并将拖放重路由为对光标下 `DockArea` 的 `AddTab`（从原 DockArea `RemoveTab` 后按末尾索引插入并选中、关闭残留的 `PaneDragTab`、复位拖拽状态与 `hotControl` 并重绘相关窗口）；落在停靠边缘 / 既有面板（`dropArea` 非空）的拖放仍走 Unity 原生 `SendEvent` 流程

### Changed

- **`ExecuteCSharpScript` 错误路径改为返回成功响应**（`0a2e598`）：`script` 参数缺失、脚本文件不存在 / 为空 / 读取失败 / 访问被拒，以及脚本执行抛异常等原先返回 `Response.Error` 的分支，统一改为返回 `Response.Success` 并仅携带提示文本；执行异常分支不再附带 `logs` 与 `exception` 详情

### Fixed

- **AICODE-1952 编辑器内嵌 cowork 卡 Loading**（`f9b792f`）：修复 macOS 内嵌 WebView（WKWebView）在 Tauri 后端尚未就绪 / 被重启替换时停在白屏的问题
  - 新增 `/api/tauri/status` HTTP 就绪门控（`_serverHttpReady`）：`_tauriServerPort` 在选定端口时即被赋值（远早于 Tauri 进程实际 bind），仅凭 `port > 0` 就导航会与服务端竞速并永久停在 connection-refused 白屏（WKWebView 不会自动重试失败的导航）；改为经后台轮询 `ProbeServerReadyAsync` 确认状态接口应答后再创建 / 重载 WebView
  - 后端被重启或重新 attach（经 project lock 或新启进程）时，从序列化句柄恢复的 WebView 绑定的是已失效 / 被替换的后端，页面停在白屏且 SPA 不再重跑加载握手；新增 `_pendingWebViewReloadAfterRelaunch`，在新服务 HTTP 就绪后重载内嵌 WebView 以重新 bootstrap UI（新服务可能复用同一端口，故经 `ResetServerReadyGate` 显式失效旧的就绪状态）
- **合成 GameView 在 Play 模式下水平压缩**（`6dccde1`、`75e1155`）：修复 Play 模式下合成 GameView 槽位（尤其鼠标点击后）出现水平压缩的渲染瑕疵——根因是 focus 事件期间 `Camera.aspect` 被破坏，以及 `PlayModeView` 优先级不当导致取到陈旧 render texture
  - 在 `SetFocus` 等原生 focus 事件后恢复相机 aspect，单窗口与合成两种模式均恢复宽高比
  - 合成已激活时不再重置帧计数，避免 warm-up 抢焦点；新增 Play 模式初始化检测与正确的 GameView 聚焦处理
  - 点击后强制连续 3 帧同步重绘以消除 backbuffer 延迟；合成模式下跳过原生鼠标按键消息以免扰动 backbuffer
  - 随后（`75e1155`）清理用于排查压缩 / 渲染问题的诊断日志与未使用的反射辅助方法（`GetGameViewDiagState`、`GetGameViewRenderTextureDiag`、`GameViewRenderTextureFieldResolved`、`WM_MOUSEMOVE` 常量），focus 操作日志降为 verbose，保留必要日志
- **`CompilationHelper` 编译错误 / 警告计数反射签名错误**（`f289ec5`）：内部 `LogEntries.GetCountsByType` 实际签名为 `(ref int errorCount, ref int warningCount, ref int logCount)` 三个 `ref int` 参数，而非单个 `int[3]` 数组；原代码传入数组无法被正确回填，导致错误 / 警告计数失效，进而影响编译自动修复判定。改为传入 `object[] { 0, 0, 0 }` 并在反射调用后回读 `args[0]` / `args[1]`
- **Unity 2019 标签页拖拽崩溃**（`f289ec5`）：跟踪标签栏鼠标位置以避免 Unity 2019 上 `DragTab` 崩溃，并为布局变化的标签页拖出检测增加跳帧处理

## [1.0.62] - 2026-06-18

### Fixed

- **Unity 2019 下串流光标相关代码编译错误**（`bae7482`）：修复串流光标 / 弹窗支持在 Unity 2019 上的编译错误（`NativeWindowBridgeHost.Cursor.cs`、`NativeWindowBridgeHost.AddComponent.cs`、`NativeWindowBridgeHost.ObjectSelector.cs`），并补齐相应 `.meta` 文件

## [1.0.61] - 2026-06-18

### Added

- **串流弹窗（popup）支持**（`4488ced`）：新增对 Editor 弹出式 UI 的串流支持，覆盖 `AddComponent`（`NativeWindowBridgeHost.AddComponent.cs`）、`AdvancedDropdown`（`NativeWindowBridgeHost.AdvancedDropdownPopup.cs`）与对象选择器 `ObjectSelector`（`NativeWindowBridgeHost.ObjectSelector.cs`），使这些原生弹窗能在远程串流场景下正确呈现与交互
- **串流光标样式支持**（`0ef377c`、`91c1193`）：新增 `NativeWindowBridgeHost.Cursor.cs`，将 Unity 编辑器光标样式同步到串流前端；并修复串流光标的相关问题
- **Canary（beta）通道版本注册**（`3c81426`）：`scripts/register-version.js` 与 `.gitlab-ci.yml` 增加将包注册到 canary（beta）通道的能力，便于灰度发布
- **Unity 2019 菜单 fallback 与 ConnectionTreeView 弹窗支持**（`876a399`）：在 `Menu.GetMenuItems` 不可用时，经反射使用 `Unsupported.GetSubmenus` / `GetSubmenusCommands` 提取菜单（`EnsureUnsupportedMenuReflection`、`SendDelayedPopupViaSubmenus`），兼容 Unity 2019；并新增对 `ConnectionTreeViewWindow`（Console / Profiler 连接目标选择器）弹出菜单的提取与回调处理，缓存原始弹窗索引以将前端选择映射回原生索引

### Changed

- **提取离屏 sentinel 处理逻辑为辅助方法**（`35be89d`）：将分散的离屏哨兵坐标（off-screen sentinel）判断与修复代码抽取为 `IsAtOffscreenSentinel` 与 `RepairOffscreenSentinel` 辅助方法，移除 `RestoreAllWindows` 中冗余的兜底代码并统一 POST-STOP 自动修复逻辑，降低重复、提升可维护性

### Fixed

- **窗口恢复后卡在离屏哨兵位置**（`e837798`）：修复 Unity 窗口在恢复后可能滞留在离屏哨兵坐标 `(-32000, -32000)` 而彻底消失的严重问题——当累积的恢复失败（尤其窗口因上次停止失败已处于哨兵位置）阻止其回到屏幕内时，新增安全兜底：保存时若窗口已在哨兵位置则赋予默认屏幕内坐标、主恢复后用兜底循环强制移回卡住的窗口、并在停止后校验中分离样式与坐标检查、补充恢复坐标的详细日志
- **Unity 2019 Gamma 色彩空间下截图黑屏**（`c9261b2`）：修复 Unity 2019 + Gamma 色彩空间下抓帧黑屏——`Present` 后 SwapChain 后台缓冲为空导致 `GrabPixels` 取到黑色像素；新增 `PrintWindow` fallback（`s_UseRenderTextureFallback`），经 DWM 合成抓取窗口内容，并在该模式下用 `HWND_BOTTOM` 定位以维持 DWM 合成
- **`ExecuteCSharpScript` 在 `Codely.Utility` 包出现时未更新引用**（`51fe455`）：修复 Codely.Utility 包后续加入时 `ExecuteCSharpScript` 未刷新程序集引用的问题

## [1.0.60] - 2026-06-15

### Fixed

- **AICODE-1862 Windows Unity6 内嵌 Tuanjie AI 卡 Loading**（`b6ca840`、`a168a99`）：重写 `NativeWindowHelper.GetHWND`，使其能在多显示器 / 多 DPI 缩放环境下可靠定位 Unity `EditorWindow` 背后的 Win32 HWND；此前纯绝对坐标匹配仅在主显示器有效，副屏上永远匹配不到导致内嵌面板一直卡在 "Loading"
  - 新增策略 1（首选）：经反射读取宿主 GUIView 的内部 `nativeHandle`（`EditorWindowNativeHandleHelper`），并校验其为本进程的 `UnityGUIViewWndClass` 窗口后直接使用，与坐标 / DPI 无关；Unity 构建缺少该属性或返回非 HWND 指针时安全降级到坐标搜索
  - 当反射得到的是本进程其他类窗口（如 ContainerWindow）时，将搜索范围收窄到该顶层窗口；其唯一 GUIView 子窗口直接采纳（浮动面板常见情形）
  - 坐标搜索对每个候选窗口在多种坐标假设下测试包含关系：(a) 旧逻辑物理像素 × `pixelsPerPoint`、(b) 候选物理矩形 ÷ 自身 `GetDpiForWindow` 缩放后按点比较、(d) 保留显示器物理原点的 monitor-anchored 映射、(c) container-relative 相对偏移比较（相对偏移不跨显示器，在任意 per-monitor 映射下都成立）；取最小匹配候选
  - 全部失败时按节流输出每个候选窗口的诊断转储，便于现场定位实际坐标映射
- **AICODE-1768 `ensure_scene_saved` 对未命名场景会弹出文件系统对话框卡住 yolo 模式**（`add76ed`）：未命名（untitled）场景没有路径，`SaveScene` 会弹出阻塞编辑器线程的原生 "Save Scene" 对话框使自动化 / yolo 模式卡死；改为在 `path` 为空时快速失败并提示先用 `save` 动作指定 `name` 与 `path`，而非触发对话框
- **退出编辑器时停止 WebRTC 服务以防卡死**（`9c27eae`）：WebRTC 串流激活时关闭 Unity 编辑器会卡死——libdatachannel 的原生线程在 DLL 卸载后仍存活，Mono 线程管理器无法中止它们；在原生 DLL 卸载前于 `OnEditorQuitting` 调用 `NativeWindowBridgeHost.StopServer()`，先销毁 `PeerConnection`（其析构会 join libdatachannel 内部处理线程），`StopServer` 已在带超时的后台线程执行 `NWB_StopServer` 故不阻塞主线程
- **合成模式下 IMGUI 浮层交互改进**（`b6a6f2d`）：修复合成窗口模式下 IMGUI 浮层（如 SceneView 的 OrientationGizmo）无法正确接收鼠标事件的问题——为合成模式增加标签页切换以将事件派发到正确浮层、在 Layout 事件中传入真实鼠标位置以便浮层控件注册、经 `hotControl` 变化检测并跟踪浮层是否消费了 MouseDown（消费时跳过 picking 命令注入以免抢占交互）、在 domain reload 与工作区清理时重置浮层状态、并抑制弹出菜单移除期间的 Unity 内部报错

## [1.0.59] - 2026-06-12

### Added

- **合成窗口关闭标签页（Close Tab）检测与通知**（`6f2fd0b`）：`NativeWindowBridgeHost` 在执行 `Close Tab` 菜单动作前先快照该合成窗口涉及的窗口类型，关闭后检测被移除的合成槽位（composite slot），并经 DataChannel 主动通知前端，使前端能及时同步窗口状态

### Changed

- **限制 ContextClick 合成范围到标签页表头**（`6f2fd0b`）：合成的 `ContextClick` 事件仅在 `DockArea` 标签页表头区域内触发，避免误伤 Game 输入与 Scene 导航等其他区域的交互
- **原生状态跨 domain reload 保活**（`b2c5287`）：重构 assembly reload 处理，使原生 C++ 单例（HTTP 服务、WebRTC 会话、离屏渲染）在 domain reload 期间保持存活，消除端口重新绑定、避免 WebRTC 断连、并规避在主线程调用 `Stop()` 可能导致的死锁；reload 期间仅清理 C# 侧状态并将日志回调置空以防悬空委托调用，reload 后重新注册日志回调恢复日志
  - `OnBeforeAssemblyReload` 移除 `NWB_StopServer` / `NWB_StopCapture` 调用，reload 前先关闭浮动捕获窗口以避免遗留孤窗
  - 更新 Windows 原生二进制 `NativeWindowBridge.dll`、`datachannel.dll`
- **UI Toolkit 截图样式微调**（`7dc9dad`）：`ManageUIToolkit` 为根视觉元素增加背景色与居中对齐，放大标题字号并调整标题 Label 样式，改善视觉呈现

### Fixed

- **沙箱 iframe 下双击检测不可靠**（`857a7a2`）：在沙箱 iframe 或 `setPointerCapture` 重置浏览器内部计数器导致 `e.detail` 始终为 1 的场景下，原先仅依赖前端 `clickCount` 的判断会漏检双击；改为始终在 C# 侧运行计时 / 位置跟踪，并与前端值取 `MAX` 组合判定，提升双击检测稳健性；新增双击检测的 verbose 调试日志

## [1.0.57] - 2026-06-09
### Fixed
- Fix unity 2019 编译错误

## [1.0.56] - 2026-06-05

### Added

- **多窗口串流支持**（`0952ec1`）：`NativeWindowBridgeHost` 大幅扩展（约 +1000 行），支持同时串流多个 Editor 窗口；新增 `NWB_GetPendingCompositeRequest` P/Invoke，拉取待处理的合成（composite）串流请求并回传 fps / width / height；同步更新 Windows 原生二进制 `NativeWindowBridge.dll`、`datachannel.dll`、`libcrypto-3-x64.dll`、`libssl-3-x64.dll`
- **平台相关窗口句柄检测以支持键盘注入**（`3acdaa2`）：`NativeWindowBridgeHost` 在 `Show()` 后通过 before/after diff 捕获新建的原生窗口句柄——Windows 记录 `ContainerHwnd` / `GUIViewHwnd`，macOS 记录 `NSWindow` 指针——供 Play 模式键盘焦点操作使用（Windows `SetFocus` / `PostMessageW`，macOS `activateIgnoringOtherApps` / `makeKeyWindow`），确保串流交互时 `Input.GetKey` 正常工作；新增 `Menu.GetMenuItems` 反射缓存
- **GameView Play 模式鼠标处理与远程串流增强**（`74ac304`）：针对物理鼠标位于另一台机器的远程串流场景，全面改进 Play 模式 GameView 的鼠标事件处理，覆盖 uGUI 交互、Input Manager 集成与 cursor lock，跨 Windows / macOS
  - 新增 uGUI EventSystem 直接注入，绕过离屏 GameView 下 `StandaloneInputModule` 的限制，保证 UI 点击可靠
  - 经前端专用 `mousedelta` 消息注入远程鼠标增量，并新增 `connection_info` 消息区分远程 / 本地串流
  - Windows 经原生 Win32 鼠标按键注入支持 `Input.GetMouseButton`；macOS 经 CGEvent 鼠标注入支持 cursor-locked 模式下的 `Input.GetAxisRaw`
  - 新增 `BrowserBtnToUnityBtn` 按键映射、按住鼠标按键跟踪与断开时清理、`QueuePlayModeMouseEvent` 精确坐标排队、新连接建立时重发 cursor lock 状态
  - 更新 macOS 原生二进制 `libNativeWindowBridge.dylib` / `libdatachannel.dylib`

### Changed

- **Play 模式跳过窗口去激活并统一日志**（`74ac304`）：Play 模式下不再执行窗口去激活，以维持 Input Manager 焦点；相关 `Debug.Log` 调用统一改走 `CodelyLogger`
- **关闭原生窗口桥接的冗余日志**（`361e8a0`）：默认关闭 `NativeWindowBridgeHost` 的 verbose 日志输出

### Fixed

- **AICODE-1636 Bridge 状态没有实时切换**（`53f44f6`）：`NativeDllLoader` 的包变更回调原先仅处理 `args.changedTo`（更新场景），而卸载只出现在 `args.removed` 中而被忽略，导致卸载后原生 socket 仍保持打开、客户端永远收不到断开通知；新增对 `args.removed` 的处理，卸载时同步调用 `Unload()`
- **macOS 暂时禁用串流并改进平台检测**（`4ce6311`）：`IsStreamingSupportedOnCurrentPlatform` 在 macOS 改回返回 `false`，避免 macOS 用户使用尚未完善的串流功能；`GetStreamServerStatus` 新增平台检测，响应新增 `platformSupported` 与 `platform`（windows / macos / linux）字段，平台不支持时 `running` / `port` / `streamStatus` 返回空值

### Removed

- **移除 verbose 日志切换菜单项**（`f015771`）：删除 `Window/Codely/Toggle Verbose Log` 菜单项及 `ToggleVerboseLog()` 方法与关联的 verbose log 状态，简化界面

## [1.0.55] - 2026-06-03

### Added

- **`manage_window_bridge` / `manage_webrtc_stream` 标记为只读动作类别**（`fe6691f`）：在 `StateDirtyPolicy` 中将这两类工具注册为额外的只读（readonly）动作类别——`manage_window_bridge` 9 个动作、`manage_webrtc_stream` 5 个动作——使窗口管理与 WebRTC 串流操作不再触发多余的 dirty state delta，符合其本质上不改变工程状态的语义；新增对应单元测试

### Changed

- **集中式日志系统 CodelyLogger**（AICODE-1703）：新增 `Editor/Logging/CodelyLogger.cs`（独立 `Codely.Logging.asmdef`），作为全包唯一允许直接调用 `UnityEngine.Debug` 的入口，统一品牌前缀（`Codely Bridge`）并支持全局开关；移除旧的 `TcpLog`，56 个文件的日志调用统一改走 `CodelyLogger`
  - 经菜单 `AI/Logging/Enable Codely Logs` / `AI/Logging/Enable Verbose Logs` 或 `CodelyLogger.Enabled` 切换；Verbose 复用既有 `UnityTcp.DebugLogs` 偏好键以保持向后兼容
  - 开关值持久化（`EditorPrefs`）并缓存到 volatile 字段，使后台线程（IPC 循环、ThreadPool 回调）与 domain reload 期间的 `[InitializeOnLoad]` 静态构造函数也能安全读取——首次主线程访问时惰性加载，避免 off-thread 读 `EditorPrefs` 抛异常
- **公共接口统一加 Codely 命名空间前缀**（`23e97d4`）：将所有对外公开的类型前缀化到 `Codely` 命名空间，避免与宿主工程 / 其他包的类型名冲突（主要涉及 ScriptFix 系列与 `ExecuteCSharpScript` / `ManageScript`）；同步更新 `Codely.Roslyn.dll` 与 README
- **更新 macOS 原生桥接二进制**（`b8ffd44`）：更新 `libNativeTcpBridge.dylib`、`libNativeWindowBridge.dylib`、`libdatachannel.dylib` 至最新版本，纳入原生侧 bug 修复与性能改进

### Fixed

- **AICODE-1655 bridge 依赖冲突**：将分散的 Roslyn / `Microsoft.CodeAnalysis.*` / `System.*` 托管 DLL 合并打包为单个 `Codely.Roslyn.dll`，删除 `Microsoft.Bcl.*`、`Microsoft.CodeAnalysis.*`、`System.Buffers/Memory/Collections.Immutable/Reflection.Metadata/...` 等独立 DLL，避免与宿主工程或其他包携带的同名程序集冲突
- **AICODE-1416 内嵌模式下 Ctrl+L 快捷键会关闭 Codely**：标签页重新可见时（已初始化的 WebView）新增 `FocusEmbeddedInputOnBecameVisible`，延迟一帧把原生焦点显式送入内嵌内容（macOS `_webViewBridge.Focus()` / Windows `_webView2Host.MoveFocus()`），使页面输入框直接获得光标，不再因焦点停留在 Unity 侧而让 Ctrl+L 等快捷键命中编辑器并关闭 Codely
- **AICODE-1618 右键菜单 AI → Add to context 逻辑问题**：`Add to context` 不再强制打开 CodelyWindow；当无窗口打开导致共享 IPC 客户端未启动时，经新增的独立 IPC owner（`CodelyWindow:StaticContextSender`）启动客户端把排队的 context items 投递出去，投递完成后释放该 owner，使无窗口存活时共享客户端能正常停止
- **Windows domain reload 后窗口透明度恢复**（`cc6de44`）：修复 domain reload 清空静态状态但原生 HWND 仍保留被改样式 / 位置，导致窗口不可见或卡在屏幕外的问题——保存原始样式前先剥离 `WS_EX_TRANSPARENT`、跳过保存屏幕外哨兵位置、恢复可见时始终剥离 `WS_EX_TRANSPARENT`，并新增 `ShowWindow`（`SW_SHOWNOACTIVATE`）回退与停止后验证阶段的自动修复兜底

## [1.0.54] - 2026-05-28

### Fixed

- **macOS 串流 DPI 缩放错误**（`52b6148`）：`NativeWindowBridgeHost.DPIScale` 的预处理守卫由 `UNITY_EDITOR_WIN` 扩展为 `UNITY_EDITOR_WIN || UNITY_EDITOR_OSX`，使 macOS 也按 `EditorGUIUtility.pixelsPerPoint` 计算缩放，修复 Retina / HiDPI 显示器下 macOS 串流帧尺寸缩放错误的问题
- **串流期间 macOS Dock 图标处理**（`591c403`）：移除离屏 / Play 模式串流期间将 NSApp 切到 `Accessory` 激活策略（隐藏 Dock 图标）的逻辑，串流期间保留 Tuanjie / Unity 编辑器的 Dock 图标常驻；恢复可见时改为在 `activate` 前先 `unhide` 并设回 `Regular` 策略，避免遗留的 `hide:` / `Accessory` 状态导致编辑器窗口不可见

## [1.0.53] - 2026-05-27

### Added

- **`unity_menu` 的 `get_available_menus` 正式实现**（AICODE-1272）：原先返回空列表的占位实现改为真正枚举 Editor 菜单项
  - 主路径经反射调用内部 `UnityEditor.Menu.GetMenuItems(string, bool, bool)`，同时覆盖原生（C++ 定义）与脚本定义菜单项；该 API 在当前 Unity 版本不可用时回退到通过 `TypeCache` 扫描 `[MenuItem]` 特性方法
  - 内置顶层菜单（`File`/`Edit`/`Assets`/`GameObject`/`Component`/`Window`/`Help`/`Tools`/`Mobile`）作为基线种子，自定义顶层菜单（如 `AI`）动态补充
  - 支持可选参数：`menu_path`/`menuPath`（限定子树）、`filter`/`search`（大小写不敏感子串匹配）、`include_separators`/`includeSeparators`（默认 `false`）
  - 修复（`e1cdce7`）：`TypeCache` 结果改为无条件并入同一集合（去重），即使反射 API 仅返回部分子树或不可用，脚本定义菜单项也始终存在；`path` `PropertyInfo` 改为按条目运行时类型缓存（`_pathPropByType`），并新增快捷键尾串正则 `_shortcutTailPattern` 以正确剥离菜单热键标记
- **Device Simulator / HMI Simulator 截图支持**（AICODE-1533）：`ManageScreenshot.CaptureGameViewTexture` 不再只抓 Game View，而是枚举所有 `PlayModeView` 派生窗口（Game View、Device Simulator、HMI Simulator…）并逐一探测，返回首个非黑帧
  - 按基类名 `PlayModeView` 识别窗口（而非按已解析 `Type`），解决 Simulator 位于独立模块程序集（`UnityEditor.DeviceSimulatorModule` / `HMISimulatorModule`）导致 `Type.GetType(...,UnityEditor)` 返回 `null` 而被跳过的原 bug
  - 探测顺序：聚焦窗口优先，其次 Simulator，最后 Game View，使活跃的模拟帧优先于陈旧的 Game View RT；新增 `IsTextureBlack` 中心采样判黑，黑帧让位于下一来源，全黑时仍返回首帧
  - `GetGameViewRT` 沿类型层级遍历读取 RT 字段（`m_RenderTexture` / 基类 `PlayModeView.m_TargetTexture`）；Tuanjie 专属 HMI Simulator 渲染进 `DeviceView.PreviewTexture` 而不落到基类 RT，故在 `TUANJIE_1_0_OR_NEWER` 下通过 `FindPreviewTextureInVisualTree` 反射遍历可视树查找 `PreviewTexture`
- **串流遮罩窗口（Streaming Mask Window）**：新增 `StreamingMaskWindow`，在离屏 / Play 模式串流期间于 Windows 与 macOS 上叠加遮罩 UI，配合帧串流隐藏底层窗口内容
- **macOS 原生键盘输入注入**：Play 模式串流期间经 `CGEventCreateKeyboardEvent` 注入 OS 级键盘事件，新增浏览器键到 macOS 虚拟键码映射、按住键跟踪与超时去激活、本机物理按键状态轮询优化；新增 verbose 日志开关（菜单命令），高频日志改用 `LogVerbose`
- **Cowork 输入框自动 focus**（AICODE-1542）：WebView2 内嵌为 Unity GUIView 子窗口时，仅 `EditorWindow.Focus()` 只把 Win32 焦点给到父 GUIView，HTML 的 `autofocus` / `element.focus()` 不显示光标也收不到键盘输入；新增 `NB_MoveFocus` P/Invoke 与 `WebView2Host.MoveFocus`，首次导航完成后（仅本窗口可见时，且只做一次）把焦点显式送入内容，避免 SPA 后续路由切换抢焦点

### Changed

- **NWB 信令服务改用 OS 动态分配端口**（`361f647`）：原生窗口桥接的串流信令服务默认端口由固定值改为 `port=0`（OS 自动分配），避免同机多 Unity Editor 实例的端口冲突
  - `PortManager.PortConfig` 新增 `stream_port` 字段，`StatusHelper` 经 `NativeWindowBridgeHost.GetBoundPort()` 查询并在状态载荷（含 legacy payload）中上报实际绑定端口，供客户端运行时发现
  - `AutoStartStreamServer` 与 `start_stream_server` 默认走动态端口；端口回退逻辑调整为仅在显式指定固定端口（`port>0`）且 `allowPortFallback=true` 时生效，错误信息与日志同步改为动态端口语境
- **DrillWindow 单实例 attach 流程**（AICODE-1507）：Walkthrough 启动改为 `EnsureTauriProcess`——先经 `~/.codely/app.lock.meta.json` 发现运行中的单实例 `cowork` APP，校验 `/api/tauri/status` 的 `single_instance` 后 POST `/api/tauri/attach-embed-workspace` 注册当前工作区并走正常 lock-file attach，仅在无运行实例时才新启进程；HTTP 与 lock 轮询置于后台线程，避免阻塞主线程
  - `OnGUI` backstop 改以 IPC `server_ready` URL 的端口为准（覆盖启动时乐观缓存的陈旧端口），杜绝把 WebView2 指向死端口（`ERR_CONNECTION_REFUSED`）
  - `GetWebViewUrl` 追加 `workspaceDir` / `theme` / `mode=embedded` 查询参数，使 Tauri 后端能为这个无窗口标签的内嵌 WebView 注入 `window.__CODELY_WORKSPACE_DIR__` 并正确路由 drill 相关 invoke
- **重绘 / 重新隐藏逻辑改用基于帧的 ticker**（`126ba3e`）：将嵌套 `delayCall` 回调替换为 `PlayModeRepaintTick` / `ReloadRehideTick` 帧计数器（`s_PendingPlayModeRepaints` / `s_PendingReloadRehidePasses`），使 GameView 重绘与 domain reload 后窗口重新隐藏分散到不同 editor 帧、时序更可控
- **打包 macOS 原生桥接依赖**（`f5fb3a9`）：随包附带 `libcrypto.3.dylib`、`libssl.3.dylib`，并更新 `libNativeWindowBridge.dylib` / `libdatachannel.dylib`，解决传递依赖缺失
- **更新各平台原生二进制**：Windows `NativeWindowBridge.dll` / `datachannel.dll` / `NativeBrowser.dll` / `NativeIpcClient.dll` / `NativeTcpBridge.dll`，macOS `libNativeWindowBridge.dylib` / `libNativeTcpBridge.dylib`，Linux `libNativeTcpBridge.so`

### Fixed

- **AICODE-1508 Walk Through 最后一步点击无响应**：内嵌 WebView 缺少工作区路由信息时后端以 "Missing workspace routing info" 拒绝全部 invoke，`drill/drillCompleted` 永远到不了处理器、Unity 收不到 `drillCompleted` IPC；经 `GetWebViewUrl` 注入 `workspaceDir` 修复
- **AICODE-1507 Walk Through 页面无法访问 / 打开时把 Tuanjie Cowork 拉到前台**：经单实例 attach 流程复用运行中的 APP，不再新启会被 `tauri_plugin_single_instance` 转发后立即退出（端口从未绑定）的多余进程
- **Windows Play 模式下幽灵移动与遮罩窗口闪烁**（`c8951ff`）：`SetFocus` 触发前台窗口回收打断 `WM_KEYUP`、Input Manager 残留按键状态；新增前台回收宽限期与按住键跟踪，在 mouseup 时 flush 陈旧状态，离屏去激活时重置按键跟踪
- **Windows Play 模式按住键卡顿**（`9c700fb`）：初始按键重复延迟（~500ms）超过宽限期（0.15s）导致遮罩窗口过早回收焦点；前台回收跳过逻辑扩展为在整个按住期间检查 `s_WinHeldKeys`
- **退出 Play 模式时帧停顿与 domain reload 恢复**（`45e6298`）：Play 模式状态切换后强制立即重绘 GameView 并安排多次重绘覆盖 Unity 短暂渲染停顿；domain reload teardown 前先通知浏览器以缩短重连延迟
- **assembly reload 期间残留浮动捕获窗口**（`fb53c07`）：自动创建的浮动捕获窗口因 close 被推迟到 `delayCall`，会先被 `RestoreAllWindowsVisible` 恢复可见而永久残留；改为 reload 前经 `SessionState` 跟踪并同步关闭，reload 后再按保存的 instance ID 检测并清理遗留窗口，macOS `RestoreAllWindowsVisible` 排除正在关闭的 NSWindow

## [1.0.52] - 2026-05-21

### Fixed

- **Unity 2019 / 标准 Unity 编译失败**：1.0.50–1.0.51 引入的 WebRTC 串流 / 窗口桥接代码无法在 Unity 2019.4 与标准 Unity（非团结引擎）上编译，本次统一修复
  - C# 8.0 switch 表达式改写为传统 switch 语句（`ManageWebRtcStream`、`ManageWindowBridge`），并将目标类型 `new()` 还原为显式构造函数调用（`new object()` / `new RenderStreamingSession()`）
  - `EditorWindow.docked` 在团结引擎中为 internal（标准 Unity 2020.1+ 为 public）：新增 `NativeWindowBridgeHost.IsWindowDocked` 反射助手，`ManageWindowBridge` 的 `list_windows` 等改为经其读取，不可用时返回 `false`
  - `EditorSnapSettings.gridSize` 仅团结引擎存在：`NativeWindowBridgeHost.Panel` 新增 `GetSnapGridSize` / `SetSnapGridSize` 反射访问，标准 Unity 下回退到 `EditorSnapSettings.move`
  - `SceneView.IsCameraDrawModeSupported`、`EditorGUIUtility.GetIconForObject` 在团结引擎中为 internal：改为反射调用（`IsCameraDrawModeSupportedReflected`），API 不可用时默认视为支持，避免静默隐藏绘制模式
  - Windows / macOS 原生 DLL 静态字段加 `#if UNITY_EDITOR_WIN` / `#if UNITY_EDITOR_OSX` 平台预处理守卫
  - .NET Standard 2.0 兼容：`string.Contains(char)` 改为 `Contains(string)`（`ManageGameObject`、`ReadConsole`），`Contains(string, StringComparison)` 改为 `IndexOf(..., StringComparison) >= 0`（`CodelyIpcServer`）
- **批处理 / 无头模式下自动弹窗导致测试失败**：新增 `TauriUtils.IsAutomatedEditorRun`（`Application.isBatchMode` 或 `SystemInfo.graphicsDeviceType == Null`）；`CodelyWindow.TryAutoOpenAfterInstall`、`DrillWindow.AutoOpenOnLoadIfDrillIncomplete` 与 `OpenWalkThrough` 在该环境下不再创建窗口，避免触发原生 `SUCCEEDED(hr)` 断言把本可通过的 CI / QA 测试整体判为 FAIL

## [1.0.51] - 2026-05-20

### Added

- **macOS 原生依赖预加载**：`NativeWindowBridgeHost` 新增 `EnsureMacDylibLoaded`，在首次探测原生可用性前通过 `dlopen`（`RTLD_NOW | RTLD_GLOBAL`）主动加载 `libdatachannel.dylib` 与 `libNativeWindowBridge.dylib`，解决部分打包流程产生的裸名称（bare-name）链接无法解析传递依赖的问题
  - `EnumerateMacNativeBridgeFolders` 先经 `AssetDatabase.FindAssets` 定位已导入的 dylib 资源目录，再回退到 `Packages/cn.tuanjie.codely.bridge/Plugins/macOS` 与 `Library/PackageCache` 下的包目录
  - 预加载结果（成功路径或 `dlerror` 详情）记录到 `macNativeDylibLoadDetail`，并在 `DllNotFoundException` / `EntryPointNotFoundException` 时追加到可用性原因中，便于排查
- **流服务自动启动重试与失败检测**：`ManageWindowBridge.AutoStartStreamServer` 新增可配置的重试机制（`AutoStartRetryDelayMs` 2000ms、`AutoStartRetryMaxAttempts` 30 次），插件尚未就绪时按节流策略（首次及每 5 次）打印进度日志，超过上限后停止重试并提示可手动调用 `start_stream_server`
  - 新增 `IsNonRetryablePluginFailure`，识别架构不匹配（`wrong architecture` / `incompatible architecture` / `no suitable image found` 等）等不可恢复失败，直接报错并停止重试，避免无意义的日志噪音

### Changed

- **macOS 暂时禁用原生流服务**：`IsStreamingSupportedOnCurrentPlatform` 在 macOS 平台改为返回 `false`（与 Linux 一致），在 macOS 原生插件 / 运行时路径完善前保持硬禁用
- **平台相关日志与错误信息细化**：Linux / macOS 分别打印各自的禁用提示；`AutoStartStreamServer` 失败及 `start_stream_server` / offscreen 入口的错误信息改为附带 `GetAvailabilityReason()` 详情，并去除指向特定平台的过时措辞
- **更新 macOS 原生二进制**：同步发布更新后的 `libNativeWindowBridge.dylib` 与 `libdatachannel.dylib`（通用 / x86_64 构建）

## [1.0.50] - 2026-05-20

### Added

- **WebRTC 串流与窗口管理桥接**：新增完整的 Editor 窗口 WebRTC 串流基础设施，支持通过浏览器远程查看与交互 Unity Editor 窗口
  - 新增 `ManageWebRtcStream` 与 `ManageWindowBridge` 两个工具，并配套 `NativeWindowBridgeHost` / `NativeWindowBridgeAPI` P/Invoke 绑定层
  - 随包附带跨平台原生插件：Windows `NativeWindowBridge.dll`、`datachannel.dll`、`libcrypto-3-x64.dll`、`libssl-3-x64.dll`；macOS `libNativeWindowBridge.dylib`、`libdatachannel.dylib`
  - 支持窗口枚举、聚焦与原生句柄解析；鼠标 / 键盘事件注入；离屏（offscreen）渲染模式与帧串流
  - WebRTC 会话生命周期经 IPC（`CodelyIpcServer`）管理，串流服务端口被占用时支持端口回退（port fallback）启动
  - Unity 与原生平台之间的坐标系转换，并通过反射访问 Unity 内部 Editor API
  - 交互式面板提取：Grid Settings、Snap Increments、Grid Snapping（`NativeWindowBridgeHost.Panel.cs`）
- **原生日志回调与 assembly reload 加固**：`NativeWindowBridgeHost` 新增 `NWB_SetLogCallback` 委托 / P/Invoke，将原生 C++ 日志接入 Unity Console，并在服务初始化前注册回调以捕获全部 C++ 日志
  - 新增 `OnBeforeAssemblyReload` 处理：domain reload 前停止服务并安全置空日志回调，避免悬空函数指针导致崩溃
  - 新增 `ForceResetNativeOffscreenState`（restore 失败时清理原生状态）与 `NWB_StopCapture` P/Invoke（干净退出 offscreen 模式）
  - 找不到 `NWB_SetLogCallback` 导出时打印 `Debug.LogWarning`，替代原先的静默跳过，便于排查原生日志缺失
- **macOS 原生键盘事件注入**：更新 `libNativeWindowBridge.dylib`，接入 CoreGraphics / CoreFoundation 的 `CGEventCreateKeyboardEvent` / `CGEventSetFlags`，使 Play 模式 GameView 串流期间可向 Unity 运行时 Input 系统派发 OS 级键盘事件

### Changed

- **DPI 缩放逻辑集中到 `DPIScale`**：`NativeWindowBridgeHost` 新增静态 `DPIScale` 属性，统一 Windows / macOS 平台相关的缩放计算，替换散落在 capture 尺寸、GUIView 尺寸提取、resize 目标比较中的重复条件分支
- **截图未指定尺寸时自动降采样并下调默认分辨率**：`ManageScreenshot` 在调用方未提供 width/height 时，将每张截图按最长边降采样至 256px（保持宽高比）；同时下调各截图入口的默认回退分辨率（Scene View `960×540` → `480×270`、asset `1024` → `480×270`、UI Toolkit 改用 `Math.Min(..., 480/270)`），避免输出超大图
- **更新 Windows 原生二进制**：同步发布更新后的 `NativeWindowBridge.dll` 与 `datachannel.dll`

### Fixed

- **ERD-4273 启动失败**：更新 Windows `NativeBrowser.dll`、`NativeIpcClient.dll`、`NativeTcpBridge.dll`（并补充 `NativeBrowser/CMakeLists.txt`），修复启动失败问题
- **Windows HiDPI 离屏截图被放大**：修复 Windows HiDPI 显示器下 offscreen 窗口渲染内容被放大 / 缩放错误的问题——前端 CSS 像素与 Unity 逻辑点 1:1 对应，但 GPU backbuffer 工作在物理像素分辨率，Windows 需缩放到物理像素、macOS 由 backing scale 内部处理；`TryGetCurrentGUIViewSize` 在 Windows 改为返回物理像素，并移除错误的 `pixelsPerPoint` 除法
- **离屏串流结束后相机宽高比错乱**：串流期间相机被手动设为浮窗尺寸，docked GameView 恢复时 Unity 的 aspect 重置逻辑会跳过 manual 模式相机，导致后续渲染水平压缩；`StopOffscreenCapture` 新增 `ResetAspect()` 恢复 auto 模式，新增 `RestoreGameCameraAspects()` 在串流事件期间修复 aspect 损坏，并缓存 `m_TargetTexture` FieldInfo 避免反复反射
- **Editor 无焦点启动时 WebView2 初始化卡住**：编辑器无焦点启动时 `EditorApplication.update` / `delayCall` 被节流到接近停摆，导致 `server_ready → OnTauriServerReady → TryInitializeWebView2` 链路卡在 "Loading..." 占位
  - `CodelyIpcManager` 用 volatile 静态缓存最新 `server_ready` URL，主线程无需等待 delayCall 即可读取
  - 从 IPC 后台线程对每个 `UnityGUIViewWndClass` 子窗口 `InvalidateRect`，借 WM_PAINT 唤醒 Unity 消息循环、强制走一次 OnGUI
  - `CodelyWindow` / `DrillWindow` 新增 OnGUI backstop，在 paint 期间读取缓存 URL 直接驱动 `TryInitializeWebView2`
  - 移除主线程冗余 delayCall 与基于 Sleep 的阻塞式 HWND 轮询；`Ctrl+L` 无选中时恢复（Show/Focus）已有窗口而非将其关闭
- **Windows 包升级时原生崩溃**：domain reload 前对 WebView2 BrowserInstances 做 Windows 平台同步关闭（`NB_ShutdownAll`，5000ms 超时），在 `NativeBrowser.dll` 卸载前完成全部原生资源清理；清空 C# 句柄引用避免重复清理，并清理 `DrillWindow` 的 WebView2 引用
- **移除过时的 signaling URL 构建并加强 JSON 转义**：移除依赖已不存在的 `CodelyIpcManager.LastServerUrl` 的 `BuildSignalingUrlFromTauri`，RenderStreaming 会话改为要求显式 `signaling_url` 参数；`EscapeJson` 补充 Unicode 控制字符（U+0000–U+001F，含退格 / 换页）转义，避免 JSON 解析错误
- **AICODE-1474 force reload 与点击 AI 后 layout 错乱**：新增 `CodelyWindowDocker`，修复 force reload 及点击 AI 之后窗口 layout 不正确的问题
- **AICODE-1492 Get Started 最后一步按钮无响应**：修复 Walkthrough 中 Get Started 最后按钮点击无反应的问题（`CodelyWindow` / `CodelyWindowDocker`）

## [1.0.49] - 2026-05-15

### Added

- **Editor 加载时若 drill 未完成则自动弹出 Walkthrough**：`DrillWindow` 新增 `[InitializeOnLoadMethod] AutoOpenOnLoadIfDrillIncomplete`，在编辑器启动 / domain reload 时通过 `EditorApplication.delayCall` 延迟到首个 idle tick，再读取 `~/.codely/data.json` 的 `drillCompleted` 字段；为 `false` 时自动调用 `OpenWalkThrough()` 让首次接入的用户无需手动点 `AI/Open Walkthrough` 菜单
  - 使用 `SessionState` key `Codely_DrillAutoOpened` 作为 per-session 守卫，确保同一个 Editor 会话内的多次 domain reload（脚本重编译）不会反复弹窗
  - 跳过条件：`TauriUtils.GetCodelyRunnable()` 为空（未安装 cowork runnable）、已存在 `DrillWindow` 实例（`HasOpenInstances<DrillWindow>()`）、或 drill 已完成

### Changed

- **`IsDrillCompleted` 从 `CodelyWindow` 提升到 `TauriUtils`**：原先位于 `CodelyWindow` 的私有静态方法 `IsDrillCompleted` 迁移到 `TauriUtils.IsDrillCompleted`（`internal`），`CodelyWindow.OpenCodelyWindow` 与新的 `DrillWindow.AutoOpenOnLoadIfDrillIncomplete` 共享同一份 `~/.codely/data.json` 读取实现，避免后续两份代码出现行为漂移

## [1.0.48] - 2026-05-15

### Changed

- **顶层菜单从 `Window/Tuanjie AI/` 改为 `AI/`**：将 1.0.46 引入的 `Window/Tuanjie AI/*` 菜单组整体上移到主菜单栏顶层 `AI/`，并去除 `Tuanjie` 前缀，使条目在菜单栏一级即可访问
  - `Window/Tuanjie AI/Open Assistant` → `AI/Open Assistant`（保留 `Ctrl+L`）
  - `Window/Tuanjie AI/Open CLI` → `AI/Open CLI`
  - `Window/Tuanjie AI/Open Walkthrough` → `AI/Open Walkthrough`
  - `Window/Tuanjie AI/Check Connections` → `AI/Check Connections`
  - `Window/Tuanjie AI/Force Reload` → `AI/Force Reload`
  - 右键菜单同步：`Assets/Tuanjie AI/Add to context` → `Assets/AI/Add to context`，`GameObject/Tuanjie AI/Add to context` → `GameObject/AI/Add to context`
- **主工具栏按钮文案改为 `AI`**：`CodelyToolbar` 中 `MainToolbarContent` text、`ToolbarButton.tooltip` 与 `Label` 由 `Tuanjie AI` 改为 `AI`，与新的顶层菜单命名保持一致
- **`Open CLI` 未找到 CLI 时的提示更新**：`CodelyWindow.OpenCodelyTerminal` 中 `Tuanjie AI CLI Not Found` 弹窗的提示改为指引设置 `CODELY_HOME`（包含 `codely[.exe]`）或 `CODELY_APP_HOME`（包含 `cli/bin/<platform>/codely[.exe]`），替换原先 `CODELY_CLI_PATH` 的过时描述
- **`CODELY_APP_HOME` 查询走统一 `ReadOsEnvVar` 助手**：`TauriUtils.GetCodelyRunnable` 删除内联的 Windows 注册表 / macOS 登录 shell 查询代码，改为通过共享的 `ReadOsEnvVar("CODELY_APP_HOME")` 每次从 OS 级存储读取，行为与 1.0.46 等价但实现统一，便于其它读取项复用

### Fixed

- **macOS `Open CLI` 路径含空格时启动失败**：`CodelyWindow.OpenCodelyTerminal` 在调用 `osascript` 时不再直接把 `cliPath` 拼进 `do script "..."` 字符串，而是先做 AppleScript 级 escape（先 `\` 后 `"`）再用 `quoted form of` 由 AppleScript 在运行时做 shell quote；包含空格的安装路径（如 `/Applications/Codely Cowork.app/Contents/Resources/cli/...`）现在能作为单一参数完整传入 Terminal 子 shell，不再被空格截断

### Documentation

- **Tests/Coverage 文档对齐 bridge 实现**：
  - 新增 `execute_csharp_script_full_coverage.md`（约 500 行），覆盖 ExecuteCSharpScript 自动修复、`script_path`、内部日志抑制等用例
  - 重写 `unity_script_full_coverage.md`（+1191 行）覆盖 ScriptFix 全套修复器与 edit revision 行为
  - 修正 `execute_custom_tool_full_coverage.md` 断言与 lookup 用例，与当前 `ExecuteCustomTool` 注册/反射路径一致
  - `unity_workflow_full_coverage.md` 补充 batch + 工作流场景；`README.md` 更新索引

## [1.0.47] - 2026-05-15

### Changed

- **UI 文案统一改为 "Tuanjie AI"**：将编辑器内残留的 "Codely" 用户可见字符串整体替换为 "Tuanjie AI"，与顶层菜单 `Window/Tuanjie AI` 改名保持一致；底层命名空间、包名、`CODELY_APP_HOME` 等环境变量与 IPC 协议保持不变，仅修改可见文案与菜单/资源命名
  - `TcpBridgeControlWindow`：窗口标题 `Codely Bridge` → `Tuanjie AI Bridge`，就绪 / 未连接提示文案同步替换为 `Tuanjie AI bridge is ready` / `Connect to Tuanjie AI Agent` / `Start Tuanjie AI Bridge to connect to Tuanjie AI Agents.`
  - `CodelyToolbar`：主工具栏元素路径 `Codely AI/Codely Bridge` → `Tuanjie AI/Tuanjie AI Bridge`，按钮 tooltip / Label 由 `Codely` 改为 `Tuanjie AI`
  - `CodelyWindow`：窗口标题、`Loading Codely...` 占位、`Updating Codely` 更新页、Setup 引导页的 logo / 主副标题、`Codely CLI Not Found` 与终端打开失败弹窗等全部改为 `Tuanjie AI`；右键菜单 `Assets/Codely AI/Add to context` → `Assets/Tuanjie AI/Add to context`，`GameObject/Codely AI/Add to context` → `GameObject/Tuanjie AI/Add to context`
  - `DrillWindow`：Walkthrough 窗口标题 `Codely WalkThrough` → `Tuanjie AI WalkThrough`
- **布局文件改名 `Codely Mode.wlt` → `Tuanjie AI Mode.wlt`**：随包附带的 Window Layout 资源同步更名，避免 `Window > Layouts` 菜单中同时出现新旧两份 Codely 布局

### Fixed

- **`CodelyLayoutInstaller` 兼容老布局迁移**：
  - 安装新 `Tuanjie AI Mode.wlt` 时同步从用户 layouts 目录删除遗留的 `Codely Mode.wlt`，确保菜单中不再出现旧条目
  - 新增 `IsLegacyLayoutActive` 检测：通过反射定位 `UnityEditor.WindowLayout.GetCurrentLayoutPath()`（不可用时回退到扫描 `layoutsPreferencesPath` 下最新的 `CurrentLayout*.dwlt`），解析 `m_LastLoadedLayoutName` 是否为 `Codely Mode` / `Codely`；仅当用户仍停留在旧布局时才自动切换到 `Tuanjie AI Mode`，避免每次 domain reload 都重置用户的窗口自定义

## [1.0.46] - 2026-05-15

### Added

- **Welcome 流程结束后自动切换 Codely Layout**：`DrillWindow.OnDrillCompleted` 在打开 `CodelyWindow` 前先调用新增的 `CodelyLayoutInstaller.ApplyCodelyLayout()`，把 Editor 加载到随包附带的 Codely `.wlt` 布局
  - 新增 `ApplyCodelyLayout`：若用户 layouts 目录缺少布局文件则先执行 `InstallCodelyLayout` 重新落盘，再通过反射调用 `UnityEditor.WindowLayout.TryLoadWindowLayout` / `LoadWindowLayout` 加载
  - 在 layout swap 前先 `Close()` DrillWindow，确保 IPC release / WebView teardown 同步走完，避免布局切换将其他窗口一并销毁时遗留资源

### Changed

- **顶层菜单移入 `Window/Tuanjie AI`**：原 `Codely AI/*` 菜单整组迁出主菜单栏，挂到 `Window` 下并改名 `Tuanjie AI`，同时重命名条目以去除 "Codely" 字样
  - `Open Codely` → `Open Assistant`（保留 `Ctrl+L` 快捷键）
  - `Open Codely (Terminal)` → `Open CLI`
  - `Open Codely Walkthrough` → `Open Walkthrough`
  - `Open Codely Bridge` → `Check Connections`
  - `Force Reload Codely` → `Force Reload`
- **默认安装目录优先 Tuanjie Cowork**：`GetCodelyRunnable` 在 `CODELY_APP_HOME` 未设置时，先尝试 `%LOCALAPPDATA%\programs\Tuanjie Cowork`（macOS：`/Applications/Tuanjie Cowork.app/Contents/MacOS`），仅当该路径下不存在 `cowork[.exe]` 时回退至 `Codely Cowork`
- **`CODELY_APP_HOME` 每次从最新环境读取**：移除进程启动时缓存的 `Environment.GetEnvironmentVariable("CODELY_APP_HOME")`，改为每次解析时从 Windows 注册表（`HKCU/HKLM Environment`）或 macOS 登录 shell 读取，避免安装器更新环境变量后必须重启 Editor 才能生效

### Fixed

- **`CODELY_APP_HOME` 指向无效目录时不再硬失败**：`GetCodelyRunnable` 在读取到 `CODELY_APP_HOME` 后，若该目录下不存在 `cowork[.exe]`，会打印 warning 并清空 `home`，让默认 `Tuanjie Cowork` / `Codely Cowork` 回退逻辑继续生效，而不是把无效路径一直传到最末的 `File.Exists` 错误

## [1.0.45] - 2026-05-15

### Fixed

- **`capture_ui_toolkit` 空白截图**：重写 `CaptureUIToolkitCoroutine` 中 PanelSettings / UIDocument 初始化与渲染驱动逻辑，解决在 HiDPI 显示器与全新 Editor 会话下 UI Toolkit 截图输出为空白或 0×0 的问题
  - `PanelSettings.scaleMode` 改为 `ConstantPixelSize` 并将 `scale`、`referenceDpi`、`fallbackDpi` 锁定到 1×/96 dpi，避免 HiDPI 下 panel 解析尺寸缩半或为 0
  - `clearDepthStencil` 置为 `true`，清理上一次重试残留深度，防止后续 UI quad 被深度剔除
  - 临时 `__CodelyTempUIDocument__` GameObject 改用 `HideFlags.DontSave`，使 runtime panel 系统能正确注册其离屏 panel
  - root `VisualElement` 显式钉到 RenderTexture 尺寸，避免首次 layout tick 之前为 0×0
  - 新增 `GetDefaultRuntimeTheme` 回退，当 UI Builder 主题映射不可用时仍使用默认 runtime 主题渲染
  - 新增 `TryForceUIDocumentRepaint`：通过反射调用 panel 的 `Repaint(Event)` / `Render()` 同步刷新 UIR draws，移除原先依赖编辑器前台 + player loop 的实现
  - 移除 `FocusUnityEditor` 及配套 Win32 `SetForegroundWindow` / `AttachThreadInput` P/Invoke 与 macOS `osascript` 调用，截图不再抢占用户当前前台窗口

### Changed

- **`Plugins\Codely.Newtonsoft.Json.dll` Auto Reference 打开**：将 `isExplicitlyReferenced` 由 1 改为 0，使用户程序集无需显式 reference 即可使用 bundled Newtonsoft.Json

## [1.0.44] - 2026-05-14

### Added

- **Unity 2019.4 支持**：将包最低支持版本从 Unity 2021.3 下调至 Unity 2019.4，并打平 Mono / C# 7.3 兼容性差异
  - 重写跨 Bridge / Tauri 编辑器代码中的 C# 8+ 构造（switch expressions、range indexers、`using var`）为 7.3 兼容形式
  - 随包附带 Unity 2019 缺失的 .NET Standard 2.0 facade DLL：`System.Memory`、`System.Buffers`、`System.Numerics.Vectors`、`System.Threading.Tasks.Extensions`、`Microsoft.Bcl.*`、`System.Text.Encoding.CodePages`
  - Windows 下将 `CodelyIpcServer` 内的 `System.IO.Pipes.NamedPipeClientStream` 替换为原生 `NIC_*` 单例（`NativeIpcClient.dll`），规避 Mono 5.x 命名管道取消挂起与程序集重载线程泄漏
  - `NativeWindowHelper` 改为基于位置的 GUIView 匹配 + 缓存的静态 `EnumWindowsProc` 委托，避免 Mono `delegate_hash_table` 每次分配委托导致的崩溃
  - 移除未使用的 `BinaryFrameHelper` 与 `InternalAPIEditorBridge.017` asmdef
- **`ConsoleLogChangedNotifier`**：新增 NTB_* 推送通知器，监听 Unity Console 日志变化并向已订阅客户端广播
- **`ManageEditor` focus window 前置**：聚焦 Codely 窗口前先将 Unity 主进程带到前台（Windows `AttachThreadInput` + `SetForegroundWindow`、macOS `osascript` 激活 PID），避免被其他应用遮挡
- **`ExecuteCSharpScript`**
  - 新增 `description` 参数，用于在调用侧标注脚本意图
  - 抑制工具自身内部跟踪日志，避免污染脚本执行期间捕获的 Console 输出
- **macOS WebView**：新增 `WebViewPlugin.bundle` 骨架，Tauri/WebView 多平台共享同一组接口

### Changed

- **Tauri 单实例进程管理重构**：在 Codely 跨工作区切换、关闭窗口等场景下保留单一 Tauri 进程，避免 UI 拆解与重复启动
  - `CodelyWindow` / `TauriUtils` 增加 single-instance attach 逻辑与全局 app meta path 检索
  - 工作区目录获取统一走 `GetWorkspaceDirectory`，standalone window state 处理更新以与 attach 流程对齐
  - Tauri 应用启动改为 async 入口，提升窗口打开响应性
  - 关闭窗口时清理遗留 Tauri 进程；切换 workspace 时不再误杀仍在使用的 Tauri 实例
  - 强化进程终止与 IPC 通道在单实例模式下的清理顺序
- **拖拽 UI 重写（`CodelyWindow`）**：重写 drag & drop 渲染与命中测试路径（+623 行），修正拖拽视觉与上下文添加流程
- **macOS 主题同步**：IPC 连接建立时与 Tauri 同步深色/浅色主题，主题变更时主动下发颜色到 Tauri
- **场景视图截图默认分辨率**：`take_scene_screenshot` 在未提供 width/height 时回退到 1920×1080，避免输出空尺寸
- **更新插件图标**
- **更新 `Plugins\Win\NativeIpcClient.dll` 与 `NativeBrowser.dll` 元数据**

### Fixed

- **WebView 与编辑器 Tab 重叠**：调整 `CodelyWindow` 中 WebView 的布局，避免覆盖编辑器窗口标签栏
- **Unity 2019 macOS WebView 错误**：修复 macOS + Unity 2019 下 WebView 初始化与崩溃问题
- **Unity 2020.3.15 兼容性**：修复 `ManageUIToolkit` 与 `CodelyIpcServer` 在 2020.3.15 上的编译/运行错误
- **Windows IL2CPP 构建报错**：修正 Win 原生插件（`NativeBrowser.dll`、`NativeIpcClient.dll`、相关 cpp/h）的 `.meta` 平台设置，使 IL2CPP 构建不再误纳编辑器原生插件
- **DLL 平台 meta 配置**：修正随包附带的 facade DLL（`System.Buffers`、`System.Memory`、`System.Numerics.Vectors`、`System.Text.Encoding.CodePages`、`System.Threading.Tasks.Extensions`）以及 `NativeBrowser.dll` / `NativeIpcClient.dll` 的 import settings，消除 dll 重复加载/平台冲突报错
- **ScriptFix `SyntaxTree` 歧义**：在 `ScriptFixProvider` 与各 `Fix*` 类中显式限定 `Microsoft.CodeAnalysis.SyntaxTree`，解决与其他程序集类型同名导致的编译歧义
- 移除遗留调试日志

## [1.0.43] - 2026-05-07

- 版本号占位（package.json 已升至 1.0.43，但未独立发布；变更内容合并至 1.0.44）

## [1.0.42] - 2026-04-29

### Fixed

- **Codely Window 显示问题**：修复 Codely Setup Window Icon 显示问题
## [1.0.41] - 2026-04-29

### Fixed

- **GameObject 序列化崩溃**：新增 `Matrix4x4Converter` 与 `TransformHandleConverter`，避免 Newtonsoft 反射序列化 `Matrix4x4`（`inverse`/`transpose` 递归与 `rotation` 触发 `ValidTRS` 断言导致 StackOverflow）以及 `UnityEngine.TransformHandle`（默认值触发 `AssertHandleIsValid`）时的崩溃
- **`get_components` 按 instanceID 查找失败**：`FindObjectsInternal` 现将数字字符串与 `searchMethod == "default"` 视为按 ID 查找，避免传入整型 instance ID（如 `49552`）时回退到按名称匹配
- **U6 打开 Codely 时点击 AI 按钮自动输入 `@Readme`**（AICODE-1326）
- **`OnServerReady` 端口校验**：在服务就绪回调中新增端口有效性校验
- **WebView 稳定性与资源共享**：改善 WebView 多窗口场景下的资源共享与稳定性

### Changed

- **Unity Bridge UI 调整**（AICODE-1317）
- 更新插件图标

### Removed

- 移除未使用文件

## [1.0.40] - 2026-04-22

### Added

- **统一命令与通知 TCP 通道**：将原有 `UnityStateServer`（USS_*）与 `NativeTcpBridge`（NTB_*）整合为单一 NTB_* ABI，单条 TCP 监听同时承载入站命令与出站推送通知；`CLIENT_VERSION>=2` 客户端可订阅推送
- **Unity 状态推送通知**：新增 `SceneHierarchyNotifier` 与 `SelectionChangedNotifier`，通过 debounce 合并高频事件，载荷与 `ManageScene` / `ManageEditor` 响应保持一致
- **`UnityTcpBridge.NotifyAll(eventType, payload)` 公共 API**：暴露统一的推送入口
- **`ManageEditor.GetSelection` 增强**：响应额外返回 `activeAsset`、`assetPaths` 以及每个 GameObject 的层级路径
- **Tauri Embed 模式检测**：通过 `/api/tauri/status` 端点读取远端 detach/embed 状态，附加进程时正确判定模式；新增 `TryParseJsonEmbedModeField` 解析与同步调试日志

### Changed

- **GameObject 路径语义统一**：`ManageEditor.GetGameObjectPath` 返回层级路径而非场景路径，与 `SelectionChangedNotifier` 对齐，使 `get_selection` 响应与 `selection_changed` 通知中的 `path` 字段含义一致
- **场景层级树构建迭代化**：`ManageScene` / `SceneHierarchyNotifier` 的递归 DFS 重写为两阶段迭代实现，避免深层级时托管栈溢出
- **`SceneHierarchyNotifier` 防抖**：从 per-frame `delayCall` 合并改为基于 `EditorApplication.update` 的 250 ms idle-debounce，事件爆发时仅产生单条通知
- **原生桥接绑定流程**：`NativeUnityTcpBridgeHost.StartOrAttach` 在调用 `NTB_Start` 前先探测 `IsRunning` / `GetBoundPort`，域重载后能可靠复用既有原生监听
- **`NativeDllLoader.Load` 句柄校验**：通过 `GetModuleHandleExW`（Windows）/ `dlopen(RTLD_NOLOAD)`（macOS/Linux）做 path-based 探测，消除包升级后 `SessionState` 句柄复用导致的 double-free 风险
- **拆解顺序修正**：新增 `NativeDllLoader.OnUnload` 事件，`UnityTcpBridge` 与 `UnityStateServer` 在原生句柄释放前先自行停止，替代 `Unload()` 中的直接调用
- **反射加载容错**：`ICommandExecutor.Register` 捕获 `ReflectionTypeLoadException`，部分类型加载失败时仍可注册其余可用类型，避免整体扫描被静默丢弃
- **`UssConfig` 缓存**：内存缓存配置，避免心跳写入时反复读取磁盘
- **进程环境清理**：启动 Tauri 前调用 `TauriUtils.SanitizePkgEnvironment` 从 `ProcessStartInfo` 中移除 `PKG_EXECPATH`、`PKG_INVOKE_NODEJS`、`PKG_INVOKE_ENTRYPOINT` 等 pkg bootstrap 变量，防止 cowork/core 启动时入口探测失败
- **DLL 加载与进程绑定**：补充错误检查路径，加载或绑定失败时及时返回
- **更新 Win/Linux/macOS 原生插件二进制**：同步发布 `NativeTcpBridge.dll`、`libNativeTcpBridge.so`、`libNativeTcpBridge.dylib`

### Fixed

- **iOS 打包失败（AICODE-1278）**：修复安装 codelybridge 后打 iOS 应用报错

### Removed

- 旧版 `Editor/Bridge/Server` 目录（`UnityStateServer`、executor 抽象、`SceneHierarchyBuilder`、`UssConfigHelper`、schema 文档），由统一 NTB 通道及新通知器替代

## [1.0.39] - 2026-04-16

### Added

- **FixMissingAssemblyReference 支持 CS0234 命名空间修复**：当 CS0234 错误因使用了错误命名空间前缀（如 `UnityEngine.SerializedObject` 实际位于 `UnityEditor`）时，自动重写源码将错误前缀替换为正确命名空间
- **FixMissingAssemblyReference 扩展至 CS0103**：将 `FixMissingAssemblyReference` 支持的诊断 ID 扩展为 `CS0246`、`CS0234`、`CS0103`，提升未限定符号的自动修复覆盖范围
- **新增 FixMissingAssemblyReference 测试套件**：新增 226 行覆盖测试（`FixMissingAssemblyReferenceTests.cs`）及 115 行 `FixMissingImportsTests.cs` 补充用例

### Changed

- **包升级处理优化**：改善 `CodelyWindow` 与 `DrillWindow` 在 Unity 包升级场景下的窗口生命周期处理，提升升级过程中的稳定性

## [1.0.38] - 2026-04-15

### Added

- **Native TCP Bridge**：将托管 C# TCP 服务器替换为原生插件架构，新增 Windows、macOS、Linux 三平台原生插件（`NativeTcpBridge.dll` / `libNativeTcpBridge.dylib` / `libNativeTcpBridge.so`）
- **NativeTcpBridgeHost**：新增 P/Invoke 绑定层，支持跨程序集重载后保持原生服务器状态；新增 `GetConnectedClients` 原生快照 API
- **NativeLibLoader**：动态加载原生 DLL，支持包更新后无需重启即可热替换
- **FixUnqualifiedUnityStaticMethod**：新增 ScriptFix 自动修复器，处理 CS0103（未限定的 Unity 静态方法调用），通过诊断 span 精确定位节点

### Changed

- **TCP 架构重构**：移除托管 `TcpListener` 及客户端循环代码，网络 I/O 移至原生层，减少 Editor 主线程阻塞；移除端口发现逻辑，改用操作系统自动分配端口
- **CompileAndAutoFix 重构**：用 `CSharpScript.Create()` 替代 `WrapScript`/`ExtractScript` 方案，自动修复编译检查与实际执行使用同一脚本引擎；新增回归保护，若修复后错误数增加则自动回滚；简化 `FixMissingBrace`，新增正则守卫跳过锯齿数组与命名元组数组的 CS1513
- **asmdef 重命名**：将内部桥接 asmdef 重命名为 `Unity.InternalAPIEditorBridge.017`，修复与 `com.unity.localization` 的冲突（AICODE-1121）
- **`NativeTcpBridge` 仅限 Editor**：将原生桥接代码限制为仅在 Editor 环境编译
- **`.npmignore` 更新**：排除构建产物

### Fixed

- **右键"加到 Codely"上下文未添加**：修复在未打开 Codely 窗口时使用右键菜单，内容不会被添加到上下文的问题（AICODE-1258）
- **Welcome 页面未显示**：修复 Tuanjie 和 Unity Editor 中未出现 Welcome 页面的问题（AICODE-1232）
- **Linux 原生库加载**：修复 Linux 下 DLL 加载器问题并更新 `.so` 文件
- **macOS 原生插件**：更新 macOS `libNativeTcpBridge.dylib`

## [1.0.37] - 2026-04-10

### Fixed
- fix: 打开Plugins\Codely.Newtonsoft.Json.dll auto reference

## [1.0.36] - 2026-04-09

### Added

- **ExecuteCSharpScript 自动修复**：新增 ScriptFix 框架，包含 `FixMissingImports`、`FixMissingSemicolon`、`FixMissingBrace`、`FixMissingParenthesis`、`FixMissingSquareBracket`、`FixMissingAssemblyReference`、`FixAmbiguousReference` 等修复器，编译失败时自动尝试修复并重试
- **ExecuteCSharpScript 支持 `script_path`**：新增 `script_path` 参数，可直接传入脚本文件路径；`script` 与 `script_path` 二选一
- **Assets 右键菜单"加到 Codely"**：启用 `Assets/Codely/Add to context` 右键菜单，将选中资源/GameObject 添加为 Codely 上下文（AICODE-1096）
- **Codely Layout**：新增 Codely 专属布局入口

### Changed

- **更新加载 UI 重构**：采用纯色 `#2d2d2d` 背景；内容区域扩展至 480px；标题字号升至 22px Bold；状态文本与页脚文本分行并用分割线隔开；旋转 spinner 改用 `Handles.DrawSolidDisc` 渲染
- **包名校验修正**：`OnPackagesRegistered` / `OnPackagesRemoved` 中将 `cn.tuanjie.codely` 更正为 `cn.tuanjie.codely.bridge`
- **安装后窗口打开方式**：改为调用 `ToggleCodely()` 优先显示引导页，Setup 完成后同样使用 `ToggleCodely()` 打开窗口
- **菜单栏结构**：`Open Codely Bridge` 菜单项从 `Window/Codely/` 移至顶层 `Codely/`（AICODE-1123）
- **DLL 重命名**：将 `Codely.Microsoft.CodeAnalysis.*` 系列 DLL 统一去除 `Codely.` 前缀，修复 Burst 编译报错
- **ReadConsole 优化**：新增最大 1000 条目限制、完善堆栈信息处理（AICODE-1117）
- **共享 IPC 基础设施**：提取可复用的 IPC 消息处理与工具类到独立模块
- **Codely Logo 及图标更新**：更新应用图标与标题栏图标

### Fixed

- **连接前进程存活检查**：尝试连接前先验证目标进程是否仍在运行，避免无效连接
- **Bridge 移除时窗口行为**：修复在 Codely 对话打开状态下移除 Bridge 不会自动关闭 Codely 窗口、顶部面板显示异常的问题（AICODE-1131）
- **TCP 服务器停止时端口状态**：服务器停止后将 `currentUnityPort` 重置为 `-1`（无效状态），避免残留端口被误用
- **Meta GUID 修复**：修正若干 `.meta` 文件的 GUID

## [1.0.35] - 2026-04-01

### Added

- **Tauri 自动更新**：新增 `updateRestart` IPC；更新重启期间显示加载 UI，重连后恢复窗口；`OnConnectionChanged` 经 `EditorApplication.delayCall` 派发；30 秒超时兜底
- **`manage_screenshot`**：支持 `capture_asset`、`capture_ui_toolkit`；部分命令支持协程执行；`UnityTcpBridge` 相应配合

### Changed

- **拖拽上下文 IPC**：`dropItems` 更名为 `addContexts`（Unity 内拖拽资源/GameObject 作为上下文）
- **启动 Tauri**：简化 Unity 登录态逻辑，直接使用当前会话 token，移除 `RefreshAccessToken` 与超时回退
- **工程锁与 Tauri 进程**：抽取可复用的 lock 解析与校验；支持就地更新后刷新进程身份
- **可执行文件与文案**：`codely-app` 更名为 `cowork`（Windows/macOS 路径与日志）

### Fixed

- **安装路径**：未配置 `CODELY_APP_HOME` 时回退到默认安装目录（Windows：`%LOCALAPPDATA%\programs\Codely Cowork`；macOS：`/Applications/Codely Cowork.app/Contents/MacOS`）
- **关闭标签页**：主线程立即 `Kill` Tauri 进程，避免新窗口误复用旧进程导致竞态（AICODE-1054）
- **切换工程关闭窗口**：`preserveTauri` 为 true 时不终止 Tauri 进程
- **macOS 粘贴**：聚焦嵌入 WKWebView（`MacOSWebViewBridge` / `WebViewPlugin`）

## [1.0.34] - 2026-03-26
### Fixed
- fix: 修复mac os 文件拖拽问题
## [1.0.33] - 2026-03-25
### Fixed
- fix: close window if project switched
- fix: codely bridge mac 上经常重连报错
## [1.0.32] - 2026-03-24
### Fixed

- 修复 `editor codely` 无法通过拖拽添加文件的问题

## [1.0.31] - 2026-03-23

### Changed

- **`take_scene_screenshot` multi-view capture**: Replace single scene-camera capture with a 4-view composite; a temporary camera mirrors all scene camera settings and shoots from ISO, TOP, FRONT, and RIGHT angles, then stitches the four renders into a 2×2 grid image (`StitchTextures2x2`)
- **Scene screenshot labels**: Draw white uppercase view-name labels (ISO / TOP / FRONT / RIGHT) with a darkened background at the top-left corner of each quadrant using a built-in 5×7 bitmap font rendered at 12× scale (`DrawLabelOnTexture`)

## [1.0.30] - 2026-03-23

### Added

- **IPC `focus_window` message**: Add `focusWindow` IPC message type and handler to bring the Unity Editor window to the foreground; on Windows uses `AttachThreadInput` + `SetForegroundWindow` to bypass foreground-switch restrictions; on macOS uses `osascript` to activate the process by PID

### Fixed

- **`execute-csharp-script` Assembly-CSharp.dll lock**: Switch Roslyn assembly references from in-process `Assembly` objects to `MetadataReference.CreateFromFile`; shadow-copy `Assembly-CSharp` and `Assembly-CSharp-Editor` to a versioned temp directory (`CodelyScriptRefs`) so the originals are never locked during script compilation; automatically clean up stale shadow copies on each run

## [1.0.29] - 2026-03-20

### Added

- **WebView persistence across domain reloads**: Keep webview alive by storing the native browser window handle (`_serializedWebView2Handle`) and validity flag as serialized fields; add set-callback methods to re-bind C# callbacks after assembly reload (`NativeBrowserAPI`, `WebView2Host`)

### Changed

- **CodelyToolbar**: Rename `TcpBridgeToolbar` -> `CodelyToolbar` and wire toolbar button to `CodelyWindow.FocusOrOpenWindow` instead of `TcpBridgeControlWindow.ShowWindow`

### Fixed

- Fix `Editor/Bridge/Icons/app.png.meta` GUID and remove stale Server platform texture settings
- Skip `MeshFilter.mesh` access in `GameObjectSerializer` to avoid instantiating a new mesh and causing unintended side effects
- Remove unused function in `CodelyWindow`
- Exclude Tauri-specific code on Linux with platform compile guards

## [1.0.28] - 2026-03-17

### Added

- **WebView persistence across domain reloads**: Keep webview alive by storing the native browser window handle (`_serializedWebView2Handle`) and validity flag as serialized fields; add set-callback methods to re-bind C# callbacks after assembly reload (`NativeBrowserAPI`, `WebView2Host`)

### Changed

- **CodelyToolbar**: Rename `TcpBridgeToolbar` → `CodelyToolbar` and wire toolbar button to `CodelyWindow.FocusOrOpenWindow` instead of `TcpBridgeControlWindow.ShowWindow`

### Fixed

- Fix `Editor/Bridge/Icons/app.png.meta` GUID and remove stale Server platform texture settings
- Skip `MeshFilter.mesh` access in `GameObjectSerializer` to avoid instantiating a new mesh and causing unintended side effects
- Remove unused function in `CodelyWindow`
- Exclude Tauri-specific code on Linux with platform compile guards

## [1.0.27] - 2026-03-13

### Added

- Add meta files: `Editor\Bridge\fonts\NotoSans-Regular.ttf.meta`, `Editor\Bridge\Icons\setup_download.png.meta`, `Editor\Bridge\Icons\setup_main.png.meta`

## [1.0.26] - 2026-03-13

### Added

- **Download Codely page** and new menu items
- Setup window assets: `setup_download.png`, `setup_main.png` (Editor/Bridge/Icons)
- Noto Sans Regular font for setup UI (Editor/Bridge/fonts/NotoSans-Regular.ttf)

### Changed

- CodelyWindow and DrillWindow updates for download/setup flow and new menu entries
- TcpBridgeControlWindow minor update

## [1.0.25] - 2026-03-10

### Added

- **Codely setup flow**
  - Add setup window (CodelySetupWindow) to guide users when Codely is not installed; includes download link and "重新检测" (re-detect) button
  - Resolve `CODELY_HOME` without Unity restart: read from Windows registry (user/system) and macOS login shell when process env is not set
- **Drill / Tauri server management**
  - Add detach/attach mode and replace previous drill mode
  - Introduce `DrillWindow` for Tauri server management
  - Add drill walkthrough check in CodelyWindow and `DrillCompleted` event

### Changed

- Rename **codely-tauri** to **codely-app** (executable and references)
- Use **CODELY_APP_HOME** instead of **CODELY_HOME** as the app home environment variable
- Optimize `GetCodelyRunnable` logic
- Update UI to show multiple connection status for the same platform

### Fixed

- Try to dock Codely window to any existing editor window when embedding fails
- Fix `Editor/Tauri/CodelyWindow.cs.meta` GUID issue

## [1.0.24] - 2026-03-03
### Changed

- Meged Codely and Codely Bridge into one package
- Update cicd pipeline to build package into dll
- Add `AI` toolbar button to open Codely Window 


## [1.0.23] - 2026-02-25

### Changed

- **UI**
Codely bridge 页面改版

## [1.0.22] - 2026-02-11

### Enhanced

- **ExecuteCSharpScript**
  - Added Unity.InputSystem assembly support for C# script execution
  - Scripts can now access Input System types and APIs when the package is installed

## [1.0.21] - 2026-02-06

### Changed

- **Batch Operations Refactoring**
  - Split generic `batch` action into two distinct operations: `create_batch` for write-only deterministic sequences and `edit_batch` for search-then-write edits
  - Added `HandleCreateBatch` method for write-only deterministic batch operations
  - Added `HandleEditBatch` method for search-then-write batch operations with captureAs support
  - Improved code clarity and prevented mixed read/write batch states
  - Maintained parity with TypeScript client schema
  - Added backward compatibility aliases for snake_case to camelCase parameters
  - Updated ValidActions list to include new batch operation types
  - Updated writeActions array to include new batch operations for state validation

### Enhanced

- **ManageAsset**
  - Enhanced batch operation handling with clearer separation of concerns
  - Improved parameter naming consistency with backward compatibility support

- **ManageGameObject**
  - Enhanced batch operation handling with clearer separation of concerns
  - Improved parameter naming consistency with backward compatibility support

### Test Coverage

- Updated `unity_asset_full_coverage.md` to reflect new batch operations
- Updated `unity_gameobject_full_coverage.md` to reflect new batch operations
- Updated `unity_workflow_full_coverage.md` with refined batch operation workflows
- Updated `Tests/Coverage/README.md` documentation


## [1.0.20] - 2026-02-05

### Fixed

- **TCP Port Management on macOS**
  - Disabled ReuseAddress socket option on macOS in PortManager.cs


## [1.0.19] - 2026-02-04

### Fixed

- **TCP Port Management on macOS**
  - Disabled ReuseAddress socket option on macOS to prevent multiple Unity instances from listening on the same port
  - Ensures proper port exclusivity across Unity Editor instances


## [1.0.18] - 2026-02-04

### Fixed

- **TCP Connection Reliability**
  - Add LingerState to test listener to send RST on close (same as actual listener)
  - Increase immediate retry attempts from 3 to 5
  - Increase retry sleep time from 75ms to 150ms
  - Extend wait time on Windows from 100ms to 500ms to allow TCP port full release


## [1.0.17] - 2026-02-03

### Fixed

- **Revert IPV6 Loopback Support**


## [1.0.16] - 2026-02-03

### Added

- **C# Script Execution**
  - New `ExecuteCSharpScript` tool for executing arbitrary C# code at runtime using Microsoft.CodeAnalysis.CSharp.Scripting (Roslyn compiler services)
  - Captures and returns Unity console logs during script execution
  - Supports custom using directives and assembly references
  - Enables dynamic C# code execution without requiring editor restart or recompilation
  - Added bundled Roslyn assemblies: `Codely.Microsoft.CodeAnalysis.dll`, `Codely.Microsoft.CodeAnalysis.CSharp.dll`, `Codely.Microsoft.CodeAnalysis.Scripting.dll`, `Codely.Microsoft.CodeAnalysis.CSharp.Scripting.dll`
  - Added supporting assemblies: `Codely.System.Collections.Immutable.dll`, `Codely.System.Reflection.Metadata.dll`, `Codely.System.Runtime.CompilerServices.Unsafe.dll`

### Fixed

- **UnityTcpBridge**
  - Reverted accidental handshake string change that broke package functionality (changed from incorrect 'WELCOME UNITY-TCP 1 FRAMING=1' back to correct 'WELCOME Codely-Bridge 1 FRAMING=1')
- **Add IPV6 Loopback Support**

### Changed

- **Branding & Menu Structure**
  - Renamed "Unity TCP" to "Codely Bridge" across logging and port management
  - Simplified menu structure: removed redundant menu items and kept only "Window/Codely Bridge/Control Window"
  - Updated menu organization for better user experience

- **CI/CD**
  - Updated register_version job rules in CI pipeline

## [1.0.15] - 2026-01-29

### Changed

- **ManageScreenshot**
  - Simplified screenshot API: removed redundant `capture_game_view` action; its behavior is now covered by the unified `capture` action
  - `capture` action now consistently uses GameView reflection to capture what the user sees in both edit and play modes
  - Removed redundant `FlipTextureVertically` call and related comments for clearer, more maintainable code

- **Package Publishing**
  - Updated `.npmignore` to include `Tests/` directory in the published npm package so consumers can run and extend the test suite

### Fixed

- **Screenshot Capture**
  - Fixed vertical flip of textures captured from RenderTexture so screenshots match on-screen orientation

### Test Coverage

- Updated `unity_screenshot_full_coverage.md` and `Tests/Coverage/README.md` to reflect the simplified screenshot API and current test structure

## [1.0.14] - 2026-01-22

### Changed

- **Dependency Management**
  - Swapped to bundled `Codely.Newtonsoft.Json.dll` in `Plugins/` directory instead of external `Newtonsoft.Json` package
  - Removed external `Newtonsoft.Json` dependency from `package.json`
  - Updated all code references to use bundled Newtonsoft.Json assembly

- **Architecture**
  - Moved runtime implementation code from `Runtime/` to `Editor/` scope to align with Unity usage patterns

### Added

- **Package Publishing**
  - Added `.npmignore` file to exclude unwanted files from npm publication
  - Excludes CI/CD configuration, build artifacts, Git metadata, IDE files, and test directories
  - Ensures only essential code and documentation are published to npm registry

- **CI/CD Pipeline**
  - Added automated npm pack and TOS (Tencent Object Storage) upload steps to deployment pipelines
  - Added backup version (1.0.0) upload for both staging and production environments
  - Improved deployment reliability with fallback version availability

## [1.0.13] - 2026-01-20

### Fixed

- **Test Assembly Dependencies**
  - Fixed missing `Newtonsoft.Json.dll` reference in `UnityTcp.Editor.Tests.asmdef`
  - Added `com.unity.ext.nunit` package dependency to `package.json` for proper NUnit framework support
  - Ensures test assembly can properly reference required dependencies for compilation

## [1.0.12] - 2026-01-19

### Added

- **ManageGameObject**
  - Added `list_children` action for listing GameObject children with configurable depth
  - Support for three result modes: `auto` (default), `inline`, and `file`
  - Automatic fallback to file output when hierarchy exceeds `maxInlineItems` threshold (default: 200)
  - Depth-limited traversal with `depth` parameter (default: 1 for direct children)
  - `includeInactive` parameter to control whether inactive GameObjects are included
  - Iterative tree building to avoid stack overflow on deep hierarchies
  - JSON streaming to file for large results to prevent memory issues
  - New helper methods: `CountDescendantsUpToDepth`, `BuildChildrenTreeIterative`, `WriteChildrenTreeIterative`

### Enhanced

- **ManageScene**
  - Improved large scene hierarchy handling (>500 GameObjects)
  - Returns shallow root-only tree with hints instead of error when scene is too large
  - Changed `CountGameObjectsRecursive` to use iterative traversal (stack-based) to avoid stack overflow on deep hierarchies
  - Better user guidance for drilling down into large hierarchies incrementally

- **Coverage Tools**
  - Added `CodelyUnityCoverageTools` class for E2E test utilities
  - New `codely.generate_large_hierarchy` custom tool for quickly generating test hierarchies
  - Configurable generation parameters: root name, child/grandchild prefixes, and counts

### Test Coverage

- Added `unity_large_hierarchy_e2e_coverage.md` with 152 lines of E2E test scenarios for large hierarchy handling
- Updated `unity_gameobject_full_coverage.md` with `list_children` action coverage
- Updated `unity_scene_full_coverage.md` with improved large scene handling coverage

## [1.0.11] - 2026-01-13

### Added

- **Tuanjie Editor Scene File Support**
  - Added support for `.scene` file extension used by Tuanjie Editor
  - Implemented extension-aware scene path handling in `ManageScene`
  - Support both `.unity` (Unity Editor) and `.scene` (Tuanjie Editor) extensions based on editor type

### Enhanced

- **UnityStateDirtyHook**
  - Added `.scene` extension detection for scene file change tracking
  - Ensures proper state tracking for both Unity and Tuanjie editor scene files

- **Documentation**
  - Improved documentation for scene file extension handling

## [1.0.10] - 2026-01-12

### Fixed

- **ManageGameObject**
  - Updated default behavior for `searchInactive` parameter from `false` to `true`
  - Ensures inactive GameObjects are included in search results unless explicitly specified otherwise

- **UnityEngineObjectConverter**
  - Added support for `{"find":"...", "method":"..."}` reference format in object deserialization
  - Fixed deserialization errors when encountering find instruction format used by MCP tools for dynamic GameObject lookups
  - Implemented delegate pattern to call `ManageGameObject.FindObjectByInstruction` from Runtime assembly without direct Editor assembly reference

## [1.0.9] - 2026-01-05

### Changed

- **Unity Project Metadata**
  - Updated Unity project metadata GUIDs across all .meta files
  - Refreshed GUIDs in Editor, Runtime, and Tests directories

## [1.0.8] - 2025-12-16

### Enhanced

- **Compilation Tracking**
  - Improved compilation error/warning count tracking with nullable integers
  - Changed `CompilationHelper.GetCompilationErrors()` and `GetCompilationWarnings()` to return `int?` instead of `int`
  - Updated `GetCompilationSummary()` to only include known values in result (removes misleading default 0 values)
  - Enhanced compilation result handling in `ManageEditor` to properly process nullable values
  - Improved type handling for compilation result payloads (supports both dictionary and anonymous object formats)
  - Added clarifying comments explaining why returning 0 for unknown counts is problematic
  - Better distinction between "0 errors/warnings" (validated) vs "unknown count" (not yet validated)

## [1.0.7] - 2025-12-15

### Changed

- **Package Renaming**
  - Renamed package from `com.unity.codely` to `cn.tuanjie.codely.bridge`
  - Updated all internal references and documentation to reflect new package name

- **Branding Update**
  - Renamed all menu item paths and labels in the Unity Editor

## [1.0.6] - 2025-12-10

### Added

- **Compilation Pipeline**
  - New `pipeline_kind: "compile"` field for structured hints to downstream tools
  - `requires_console_validation: true` flag to guide compilation validation
  - Comprehensive integration test documentation for compilation pipeline policy
  - Enhanced play mode state synchronization with `playMode` field in responses

### Enhanced

- **StateComposer**
  - Simplified state reporting to focus on essential information (compiling vs idle)
  - Minimized state complexity with clear documentation directing users to specific diagnostic tools

- **ManageAsset**
  - Improved `AssetExists` method with ghost asset detection
  - New `BuildAssetNotFoundResponse` for better error messaging about desync issues
  - Enhanced asset validation and error handling

- **Test Coverage**
  - Added `unity_compile_pipeline_integration.md` with 152 lines of comprehensive test scenarios
  - Updated `unity_editor_full_coverage.md` with compilation pipeline requirements

## [1.0.5] - 2025-12-04

### Added

- **Test Coverage**
  - Added comprehensive test coverage for `ExecuteCustomTool` functionality
  - UI Toolkit tools test coverage documentation and validation

### Enhanced

- **ManageUIToolkit**
  - Enhanced `link_uss_to_uxml` action with GUID support
  - Added `ResolveAssetPath` helper method for flexible path/GUID resolution
  - Improved parameter validation for mixed path/GUID usage

- **ManageShader**
  - Enhanced `ensure_material_shader_for_srp` action with `material_guid` parameter support
  - Improved parameter handling for material identification via path or GUID
  - Better error messages for missing material parameters

- **CodelyUnityValidationTools**
  - Added nested field path support in `validate_response`
  - Support for dot-notation field paths (e.g., "state.project.srp", "project.srp")
  - Automatic recursive field search when direct path fails
  - Enhanced field validation with path tracking and debugging

- **ManageBake**
  - Refactored NavMesh operations to use runtime reflection
  - Improved AI Navigation package detection and type resolution
  - Better compatibility with optional AI Navigation package installation
  - Enhanced error handling for missing package scenarios

## [1.0.4] - 2025-12-03

### Added

- **Validation Tools Framework**
  - New `CodelyUnityValidationTools`: 15+ validation helpers for automated testing
  - `codely.validate_play_mode`: Validate current editor PlayMode state
  - `codely.validate_active_tool`: Validate current active editor tool
  - `codely.validate_not_compiling`: Ensure editor is not compiling
  - `codely.validate_tag_and_layer_exist`: Verify Tag/Layer existence
  - `codely.validate_window_open`: Check editor window state
  - `codely.validate_console_contains`: Validate console messages with filter
  - `codely.validate_console_count`: Verify console message counts
  - `codely.validate_active_scene`: Validate active scene properties
  - `codely.validate_scene_dirty`: Check scene dirty state
  - `codely.validate_hierarchy_root_count`: Verify hierarchy root object count
  - `codely.validate_gameobject_exists`: Check GameObject existence
  - `codely.validate_response`: Generic response validation

- **Compilation Pipeline**
  - `CompilationHelper`: New helper class for compilation status checking and error tracking
  - `start_compilation_pipeline` action in ManageEditor for standardized compile workflow
  - Block compilation during play mode to prevent editor errors

- **Test Coverage Documentation**
  - Complete test coverage specs for all Unity tools
  - `unity_editor_full_coverage.md`: 24 actions coverage
  - `unity_console_full_coverage.md`: Console operations coverage
  - `unity_scene_full_coverage.md`: Scene management coverage
  - `unity_gameobject_full_coverage.md`: GameObject operations coverage
  - `unity_asset_full_coverage.md`: Asset management coverage
  - `unity_script_full_coverage.md`: Script management coverage
  - `unity_shader_full_coverage.md`: Shader operations coverage
  - `unity_package_full_coverage.md`: Package manager coverage
  - `unity_menu_full_coverage.md`: Menu execution coverage

### Enhanced

- **State Management**
  - State delta tracking added to async operation responses
  - Internal state publish tracking for write operations
  - Enhanced console state tracking with `since_token` filtering

- **ManageEditor**
  - Idempotent `ensure_tag` and `ensure_layer` operations
  - Extended with compilation pipeline integration

- **ManageAsset**
  - Enhanced robustness with better error handling

- **ManageGameObject**
  - Improved serialization with `GameObjectSerializer` enhancements

- **ReadConsole**
  - Enhanced filtering with `since_token` support for incremental reads

- **ExecuteCustomTool**
  - Improved tool registry with better parameter validation

### Fixed

- Improved Unity version compatibility across various tools

## [1.0.3] - 2025-12-01

### Added

- **State Management System**
  - `AsyncOperationTracker`: Comprehensive async operation management with progress tracking and cancellation support
  - `StateComposer`: Full Unity state composition including scene, project, packages, and shaders
  - `UnityStateDirtyHook`: Automatic tracking of Unity Editor state changes (hierarchy, project, selection, console)
  - `WriteGuard`: Thread-safe write operation protection with main thread enforcement
  - New `get_current_state` endpoint for retrieving complete Unity state snapshots

- **New Unity Tools**
  - `ManageBake`: Light baking controls (start, cancel, clear, status queries)
  - `ManagePackage`: Package manager operations with version pinning support (package@version syntax)
  - `ManageUIToolkit`: UI Toolkit template instantiation with automatic USS/C# generation

- **Custom Tool Execution Framework**
  - `ExecuteCustomTool`: Reflection-based tool discovery and execution via `[CustomTool]` attribute
  - Automatic tool registry with parameter validation and error handling
  - Support for custom tools without modifying CommandRegistry

- **Enhanced Existing Tools**
  - `ManageEditor`: Extended with state-aware operations and full state retrieval
  - `ManageGameObject`: Added find, query, parent/child operations, and component management
  - `ManageAsset`: New asset import, export, and metadata operations
  - `ManageScene`: Enhanced with scene creation and multi-scene management
  - `ManageShader`: Expanded with shader compilation, variant queries, and global property management
  - `ReadConsole`: Added scope-based console clearing and entry filtering

### Enhanced

- **Response Helpers**: New state-aware methods (`SuccessWithDelta`, `SuccessWithState`, `Conflict`) for better change tracking
- **CompilationHelper**: Improved compilation workflow handling with better async integration
- **Test Coverage**: Added unit tests for `AsyncOperationTracker`, `StateComposer`, and `WriteGuard`

## [1.0.2] - 2025-11-11

### Fixed

- **Unity Version Compatibility**: Added conditional compilation in `ManageGameObject.cs`
  - Uses `FindObjectsByType` with `FindObjectsInactive` enum for Unity 2022.2+
  - Falls back to `FindObjectsOfType` for Unity 2021.3 and earlier
  - Resolves CS0246 error: `FindObjectsInactive` type not found on Unity 2021
  - Maintains backward compatibility across Unity versions

## [1.0.1] - 2025-11-07

### Fixed

- 🐛 **Fixed build compilation error**
  - Corrected assembly definition configuration for `UnityTcp.Editor.asmdef`
  - Changed from platform exclusion list to explicit Editor platform inclusion
  - Ensures Editor assembly only compiles in Unity Editor, not in game builds
  - Resolves compilation errors during game packaging for all platforms

## [1.0.0] - 2024-12-19

### Major Refactoring

- 🔄 **Complete removal of MCP (Model Context Protocol) logic**
  - Removed all MCP-specific components, tools, and protocol handling
  - Eliminated MCP server integration and HTTP server components
  - Removed MCP client models, configuration systems, and UI windows

### New TCP-Focused Architecture

- 🚀 **Pure TCP Socket Implementation**
  - New `UnityTcpBridge` class for TCP server management
  - Basic echo server implementation as starting point
  - Async/await patterns for non-blocking operations
  - Multi-client connection support with proper resource management

### Core TCP Features

- **Port Management**
  - Automatic port discovery and allocation
  - Project-specific port persistence
  - Smart port conflict resolution
  - Cross-platform compatibility

- **Connection Handling**
  - TCP listener with automatic client acceptance
  - Configurable socket options (keep-alive, timeouts)
  - Graceful connection cleanup on shutdown
  - Unity lifecycle integration (assembly reload, editor quit)

### Updated Components

- **Renamed Assemblies**: `UnityTcp.*` → `UnityTcp.*`
- **Updated Namespaces**: All classes moved to `UnityTcp.Editor.*` namespace
- **Simplified Helpers**: Kept only TCP-relevant utilities (PortManager, TcpLog)
- **Package Rebranding**: Updated from "Unity MCP" to "Unity TCP Bridge"

### Removed Components

- All MCP protocol handling and message processing
- MCP tool implementations (ManageScript, ManageAsset, etc.)
- MCP UI windows and editor integrations
- HTTP server and MCP server management
- Telemetry and MCP-specific logging
- Configuration builders and MCP client models

### Technical Details

- **Architecture**: Direct TCP socket server with customizable protocol handling
- **Performance**: Lightweight implementation focused on TCP networking
- **Compatibility**: Unity 2021.3+ with Newtonsoft.Json dependency
- **Protocol**: Basic TCP with welcome handshake (easily customizable)

### Migration Guide

This is a breaking change that removes all MCP functionality:

1. **Previous MCP Users**: This package no longer provides MCP integration
2. **TCP Socket Users**: Replace any `UnityTcpBridge` references with `UnityTcpBridge`
3. **Custom Protocols**: Implement your protocol logic in `HandleClientAsync` method
4. **Port Management**: Use `PortManager` for dynamic port allocation needs

### Development Notes

- Codebase reduced by ~80% by removing MCP complexity
- Focus shifted to providing a clean TCP socket foundation
- Easy to extend for custom networking protocols
- Maintains Unity Editor integration for automatic lifecycle management

## Previous Versions

Previous versions (1.x.x) included MCP (Model Context Protocol) integration which has been completely removed in this version.
