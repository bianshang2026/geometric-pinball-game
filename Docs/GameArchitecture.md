# 《几何弹球》抖音小游戏版 · 完整架构设计文档

> 版本 v2.0 ｜ 2026-09-19 ｜ 定位：**可直接开工的发布版施工图**，供逐 Phase 让 AI 实现。
> 基线：Phase 1-10 已实现（物理/八机关/连锁/构筑/地图/通用Boss+弱点弧/音频框架/引力球/局外存档/Android打包链）。
> 全文标注：**【已有】**＝现有代码可直接沿用；**【重构】**＝现有系统改造；**【新增】**＝待开发。
> 伴生文档：`Docs/AudioDesign.md`（音频系统完整设计，本文档只引用不重复）。

---

## 0. 现状 → 目标迁移总表（先读这张）

| # | 目标系统 | 现状 | 动作 |
|---|---|---|---|
| 1 | 核心物理/球/弹性/回收 | ✅ Phase 1 | 【已有】直接沿用 |
| 2 | 八种几何机关 | ✅ Phase 2-7（Block/Bomb/Triangle/Diamond/Mirror/Gravity±/OneWay/TimeGate/Portal） | 【已有】 |
| 3 | 触摸瞄准 | BallLauncher+TrajectoryPredictor（直读 Input） | 【重构】拆 InputManager/AimSystem/LaunchSystem 三层 |
| 4 | 战斗 UI | Hud（无 SafeArea） | 【重构】SafeArea + 底部重排 |
| 5 | Roguelite 地图 | RunMap 5 行无连线动画 | 【重构】8 层分支 + 节点状态 + 连线动画 + Run 存档 |
| 6 | 奖励/词缀 | BuildState 9 强化 + 3选1 | 【已有】+ 战报 RunStats【新增】 |
| 7 | 30 张地图 | Stage1-4 共 4 张 | 【新增】26 张 + RegionDef + 区域机制 |
| 8 | 5 Boss | 通用 Boss（护盾环+弱点弧） | 【新增】BossData + 5 行为控制器 |
| 9 | 局外大厅 | 无 | 【新增】MainMenu + 球库/构筑/图鉴/商店 |
| 10 | 存档 | MetaProgress（File.IO 落盘） | 【重构】IStorageService KV 抽象 + RunData/PlayerProfile |
| 11 | 性能（池化） | Debris/浮字/火花即用即建 | 【重构】5 类对象池 |
| 12 | 抖音接入 | 无 | 【新增】Stark SDK 构建管线（WebGL 后处理路径，见 §2.3） |
| 13 | 广告 | 无 | 【新增】AdService + 3 点位 |
| 14 | 发布 | Android APK 链已通 | 【已有】真机/包体验证流程 |

---

## 1. 游戏定位（不变量）

- 类型：几何弹球 × Roguelite × 物理弹射 × 构筑。
- 玩家职责：发射角度 / 路线规划 / 机关利用 / 球体构筑 / 地图抉择 / 连锁编排。
- 核心循环：瞄准→发射→弹射→连锁→击破核心→选强化→地图抉择→下一节点→Boss→Run 结算→局外成长→下一轮。
- 视觉：纯代码几何（ProcSprites 程序贴图 + NeonStyle），零外部美术资产。**这一条是包体与成本的最大优势，任何新功能不得引入贴图资产**（音频/字体除外）。

## 2. 平台目标：抖音小游戏

### 2.1 硬约束（架构级）

| 约束 | 设计对策 |
|---|---|
| 竖屏 9:16 为主 | 设计基准 1080×1920（CanvasScaler ScaleWithScreenSize，已有）；Camera 正交锁定 fieldHeight=16，宽度自适应露背景 |
| 异形屏（刘海/挖孔/手势条） | **SafeAreaRoot**【新增】：`Screen.safeArea` → RectTransform anchor，所有 UI 挂其下 |
| 触摸单手 | 瞄准=全屏拖动（无固定按钮区）；可点按钮全部贴边；右下 25% 区域为拇指盲区不放按钮 |
| 后台暂停/杀进程 | ILifecycleService.OnPause → **自动存 Run**（RunData 落盘）；恢复→继续挑战 |
| 无文件系统（WebGL 运行时） | 存档一律走 IStorageService KV，**禁止 File.IO**（现 MetaProgress 需迁移） |
| 包体 | 代码包=WASM；资产=程序生成贴图(0)+音频压缩(<8MB)+字体子集；BGM 可走流式/CDN |
| 无线程 | 禁 Task.Run/Thread（ToneSynth 主线程生成 ✓）；异步=协程 |
| 性能基线 | 主流机 60fps / 2019 年中端机 30fps 保底；预算见 §20 |

### 2.2 运行时平台矩阵

| 能力 | 编辑器/Win64/Android | 抖音小游戏 |
|---|---|---|
| IStorageService | PlayerPrefs 实现 | Stark KV 实现（内存缓存+防抖落盘） |
| IAdService | NoOp（始终成功） | 激励视频/插屏 |
| IShareService | 截图到本地 | 分享卡片 |
| ILifecycle | OnApplicationPause | 同（小游戏前后台切换会触发） |
| Input | 鼠标 | 触摸（WebGL Pointer 事件） |

### 2.3 抖音构建管线（已取证：团结 wiki「抖音Stark SDK版本兼容性与构建管线」）

**路径：WebGL 构建 + 字节跳动 Stark SDK 后处理**（Tuanjie 1.9.3 ≥ SDK 要求的 TUANJIE_1_5_OR_NEWER ✓）：
1. `BuildPipeline.BuildPlayer` 产出 **webgl.data / webgl.wasm / webgl.framework.js**（必须此命名，非默认 Build.*）；
2. Stark SDK 做模板替换与格式转换 → 抖音小游戏包；
3. **已知坑**：Stark SDK 的 `com.bytedance.ttsdk-editor.asmdef` 引用两个错误 GUID，需修正为 ttsdk.ref 与 TTLitJson 的正确 GUID；与微信 SDK 共存时 `WX_SyncFunction_tnnt` 未定义符号 → linker 参数 `-s ERROR_ON_UNDEFINED_SYMBOLS=0`。
→ P12 里程碑门：以上 4 项先行验证，产出"抖音开发者工具可打开的项目"。

### 2.4 平台抽象（游戏代码的边界）

```
Gameplay 层（Ball/Battle/Roguelite/UI）   ← 禁止 using 任何平台 SDK
    ↓ 仅依赖
Platform 接口层 IPlatformService 族
    ↓ 实现（按 #if 平台 define 切换）
Unity 实现（编辑器/Win/Android） ｜ 小游戏实现（Stark KV/广告/分享）
```
asmdef 边界：`Assets/_Game/Platform/` 独立程序集 + `defineConstraints` 隔离 SDK 引用；业务程序集仅引用接口。

```csharp
public interface IStorageService { void Set(string key, string json); string Get(string key, string def = null); void Flush(); }
public interface IAdService     { bool IsReady(AdSlot slot); void Show(AdSlot slot, Action<bool> onDone); }
public enum AdSlot { Revive, DoubleReward, ShopRefresh }
public interface IShareService   { void ShareScreenshot(Action<bool> onDone); }
public interface ILifecycleService { event Action OnPaused; event Action OnResumed; }
public static class PlatformManager { /* Init 时注册实现；业务用 PlatformManager.Storage 等取用 */ }
```

---

## 3. 产品结构与页面流（§三）

```
【局外大厅 MainMenu】
   ├─（无进行中 Run）→ 开始挑战 → 生成 RunMap → 【地图 MapScreen】
   ├─（有中断 Run）  → 继续挑战 → 读 RunData → 【地图】
   └─ 球库 / 构筑 / 图鉴 / 商店（Panel）
【地图】→ 点战斗节点 → 【Battle（动态生成）】
   → 击破核心 → 【VictoryReport 战报】 → 【3选1 强化】 → 【地图】（✓ 现有流程已是"不自动下一关"）
   → …层数推进… → 【Boss 节点】 → 【RunResult 结算】（星尘转化+图鉴+纪录）→ 【大厅】
失败（球耗尽）→ 【失败浮层】→ 重玩本节点 / 放弃 Run → 【地图】/【RunResult·失败】→ 【大厅】
```
现状差异：Bootstrap 现在直接 EnterBattle；改为 `MainMenu.Open()`。Hud.RunOver 的"再来一 Run"改为"回大厅"。其余流程与现状一致（Phase 6 已实现战斗→结算→选强化→回地图 ✓）。

## 4. 局外大厅 UI（§四）

### 4.1 布局（1080×1920 基准，px）

| 区 | 位置/尺寸 | 内容 |
|---|---|---|
| TopBar | SafeArea 顶部，高 140 | 左：Logo「几何弹球」；右：⚙设置（64×64） |
| 中央核心 | 居中 y≈+180 | 大型几何核心动画：六边形轮廓+双旋转环+呼吸光晕（复用 CoreBlock 视觉配方），下方两行小字：`最高层数 ×N` `最高星尘 ×N` |
| 开始按钮 | 居中 y≈-260，620×150 | 【开始挑战】/【继续挑战】（有中断 Run 时），按下 1→0.94→1（120ms） |
| BottomNav | SafeArea 底部，高 200，4 等分 | 球库 / 构筑 / 图鉴 / 商店（图标+文字，间色区分） |
| 资源角标 | 右上（设置左侧） | 星尘数量 |

### 4.2 Canvas 层级

```
Canvas(Scaler 1080×1920, match=1)
└─ SafeAreaRoot(SafeArea 组件驱动 anchor)
   ├─ TopBar
   ├─ MainCoreAnim
   ├─ StartButton
   ├─ ResourceBadge
   └─ BottomNav{ BallBtn, BuildBtn, CodexBtn, ShopBtn }
```
### 4.3 切换与动画
- 页面切换：UIManager PageStack（Push/Pop/Replace），统一 200ms alpha 过渡 + 新页 Scale 0.96→1（OutQuad）；
- 点击反馈：全局 ButtonHook（按下缩 0.94 + SFX_UI_Click，Phase 9C 接入）；
- 大厅→战斗：无需过场（同场景，Stage 动态生成 + 短黑闪 250ms）。

## 5. 球库系统（§五）

### 5.1 球种=玩法表（6 球种；穿透球为新增）

| 球种 | 玩法定位 | 机制 | 数值基线 | 解锁（星尘） | 状态 |
|---|---|---|---|---|---|
| 普通球 | 基础弹射 | 标准弹性 | 速12/伤1 | 初始 | 【已有】 |
| 分裂球 | 球海 | 每次碰撞 1→2（冷却0.12s，上限6球） | 速12/伤1 | 10 | 【已有】 |
| 爆炸球 | 面伤开路 | 首撞范围爆炸(r1.4/伤2) | 速12/伤1 | 14 | 【已有】 |
| **穿透球** | 直线洞穿 | 每次发射可穿透 2 次（复用 `_pierceLeft` 管线），视觉=细长光针 | 速13/伤1 | 18 | 【新增】BallKind.Pierce |
| 棱镜球 | 镜面多路线 | 撞镜面 1→3 分光(±28°/±55°) | 速12/伤1 | 16 | 【已有】 |
| 引力球 | 弧线塑向 | 向最近可破坏体持续受力 | 速11/伤1 | 20 | 【已有】 |

### 5.2 装备与切换
- **首发球种**＝球库中"装备"的球种（写入 PlayerProfile.ballLoadout，进战斗为 IdleBall 初始 Data）；
- 战斗内左下"球种切换"按钮保留（existing Hud），循环可用球种——球库装备决定起始位。

### 5.3 球库页面
- 2×3 卡片网格（每卡 480×420）：球图形（代码绘制预览）+名称+一句玩法描述；
- 状态：已装备（青框）/ 已解锁（可点装备）/ 未解锁（灰 + 星尘价格 + 解锁按钮）；
- 点装备 → 卡片描边动画 + UI_Confirm。

## 6. 构筑系统（§六）：局外 vs 局内

| 维度 | 局外构筑（永久） | 局内强化（Run 内） |
|---|---|---|
| 载体 | MetaProgress 解锁（星尘购买） | BuildState 词缀堆叠（3选1/商店/宝箱） |
| 内容 | 星图罗盘(开局水晶+2×5层)/备用弹体(库存+1)/预载构筑(开局随机强化)/**球种解锁**/未来：商店折扣、开局双发 | 9 种现有 UpgradeDef（加速/穿透/自动分裂/爆裂/余震/球数/伤害/爆程/连锁伤） |
| 原则 | **只解锁"新玩法"，不做数值碾压**（每项都改变开局决策而非伤害） | 数值+机制混合，Run 内闭环 |
| 展示 | 构筑页＝解锁总览（已购列表+价格）；局内构筑在战斗 HUD 显示堆叠数，图鉴页可看明细 | |

## 7. Roguelite 地图系统 v2（§七/§九/§十六）

### 7.1 层数计算（一 Run = 8 个访问节点）

```
L0        L1        L2        L3        L4        L5        L6        L7
起点⚔ → [⚔/$/?] → [⚔/○/?] → [⚔/♛≥1] → [?/○/$] → [⚔/♛≥1] → [$+○必含] → BOSS
 1-3节点  1-3      1-3       2-3       1-3      2-3       2         1
```
- 访问节点数 = 1+5+1+1 = **8**（8~10 目标区间内）；战斗类约 5 场，单场 90~150s → **一 Run 12~18 分钟**；
- 分支保证：L1-L5 每层 ≥2 节点且全部可达；每层访问 1 节点，其余保持锁定——**保住抉择感**；
- 抖音碎片化对策：Run 可中断续玩（RunData 落盘），失败/放弃只损失本 Run。

### 7.2 生成算法（RunMap v2 重构）
- 每层节点数：`2 + Rng.Next(0,2)`（L6/L7 固定）；
- 类型按 **LayerConfig**（数据驱动）：该层允许类型+权重（如 L3：Battle40/Elite60）；
- 连边：每节点连下行 1-2 个（现有边算法复用）+ 补边保证下行无死节点（已有）；
- **RngService**：`System.Random(hash(runSeed, layer, index))`——整张图可由 runSeed 复现。

### 7.3 节点状态机与视觉

| 状态 | 含义 | 视觉 |
|---|---|---|
| Locked | 未解锁 | 灰暗 40% 透明度，不可点 |
| Available | 当前可选（邻接已完成的下一层） | 亮青 + 呼吸描边 |
| Current | 所在节点 | 白色满亮 + 外圈 |
| Completed | 已通过 | ✓ 角标 + 降饱和 |
| Failed | 战斗失败但选择放弃前保留 | 红色描边（可重试或绕行则回到 Available） |
| Boss | L7 | 红色六边形大图标 |

### 7.4 地图动画
- 节点完成：Scale 1→1.2→1（180ms）→ ✓ 角标淡入；
- **连接线**【新增，现无】：节点间线段（UI Image 拉伸），完成节点→邻边依次 Alpha 0→1（250ms，间隔 60ms stagger）；
- 新层解锁：该层节点从 40% 透明度呼吸进入可点态；
- 8 层竖排超一屏 → 地图层拖动滚动 + 当前层自动居中。

### 7.5 Run 存档（RunData）
进出地图/战斗节点/奖励选择时增量保存；大厅显示"继续挑战"。字段见 §19。

## 8. 节点类型定义（§八，6 类，现有已实现）

| 类型 | 图标 | 颜色 | 功能 | 概率(默认) | 奖励 | 风险 | 路线价值 |
|---|---|---|---|---|---|---|---|
| 战斗⚔ | 剑形三角 | 青 | 标准战斗节点 | 42% | 水晶2+强化3选1 | 球库存 | 主进度 |
| 精英♛ | 大菱形 | 橙 | HP×2 全场强化 | 18%（L3/L5 保证≥1） | 水晶4+**强化双选** | 高（库存消耗大） | 词缀速成 |
| 宝箱○ | 方框 | 黄 | 免费随机词缀 | 10% | 词缀×1 | 无 | 白赚，偏路诱饵 |
| 商店$ | 圆环 | 蓝 | 2水晶/词缀 | 16% | 按需补强 | 水晶经济 | 调节阀 |
| 事件? | 问号 | 紫 | 献祭球库存↔强化 / +3水晶 | 14% | 自选 | 或损库存 | 风险决策 |
| BOSS | 六边形 | 红 | 区域 Boss 战 | L7 固定 | 星尘+通关 | 全清 | 终局 |

## 9. 30 张战斗地图规划（§十/§十一，Layout Data 驱动）

> 现有 StageData 结构完全够用（entries[]+corePos+coreHp+ballStock），30 张=30 个 ScriptableObject 数据资产 + StageLibrary 索引。区域化新增 RegionDef。

### 9.1 五区域设定

| 区域 | 名称 | 核心机制 | 教学主轴 |
|---|---|---|---|
| R1 | 矩阵实验场 | 方块阵/三角边规则/菱形加速 | 基础弹道、瞄准精度、加速时机 |
| R2 | 镜面回廊 | 镜面、单向墙、旋转三角 | 反射角计算、时序、入口选择 |
| R3 | 混沌裂隙 | 传送门、炸弹链、时间门 | 路线预编排、连锁链、相位等待 |
| R4 | 引力穹界 | 引力/斥力场、力场组合 | 弧线弹道、场内塑向、轨道甩击 |
| R5 | 核心圣殿 | 全机关高密度混合 | 综合考试、连锁最大化 |

### 9.2 Stage01–30 明细（难度 1-5；布局为示意俯视，▲上为核心区）

| # | 名称 | 主机制 | 次机制 | 布局示意 | 难 | 教学目标 | 适合 Build | 连锁潜力 |
|---|---|---|---|---|---|---|---|---|
| 01 | 初识矩阵 | 方块 | — | ▲ ◎ ▏3列短墙 | 1 | 拖动瞄准→直击核心 | 普通 | 低 |
| 02 | 边之规则 | 三角(慢边) | 方块 | ▲△×2 护卫 | 1 | 慢边=可控减速入轨 | 普通 | 低 |
| 03 | 加速走廊 | 菱形×2 | 方块 | ◆ ▲ ◆ 竖排 | 1 | 两次加速越障 | 普通/加速 | 中 |
| 04 | 护墙之后 | 方块弧墙 | 三角 | ◎ 外弧 6 块 | 2 | 折射绕后 | 普通 | 中 |
| 05 | 双三角夹击 | 三角(快+慢) | 菱形 | 左快右慢斜置 | 2 | 分清边色规则 | 加速 | 中 |
| 06 | 实验场 Boss 关 | 方块阵 | 加速 | 稀疏阵+◆ | 2 | 第一次弱点弧 Boss(B1 试用) | 任意 | 中 |
| 07 | 第一面镜 | 镜面×1 | 方块 | 斜镜 45° | 2 | 入射角=反射角 | 任意 | 中 |
| 08 | 镜梯 | 镜面×3 阶梯 | — | 阶梯斜置 | 2 | 连续反射链 | 棱镜 | 高 |
| 09 | 单向之门 | 单向墙×2 | 方块 | 十字布防 | 2 | 正反两用思维 | 普通 | 中 |
| 10 | 旋转哨卫 | 旋转三角×2 | 镜面 | 上下对转 | 3 | 时序预判 | 棱镜/加速 | 高 |
| 11 | 回廊迷影 | 镜面×4 环形 | 单向 | 环形布防 | 3 | 封闭内反弹规划 | 棱镜 | 高 |
| 12 | 回廊 Boss 关 | 镜面阵 | 旋转三角 | 放射阵 | 3 | B2 镜面装甲教学 | 棱镜/爆炸 | 高 |
| 13 | 裂隙入口 | 传送门×1对 | 方块 | 侧路传送 | 3 | 传送保速矢量 | 普通 | 中 |
| 14 | 双门快递 | 传送门×2对 | 加速 | 双通道 | 3 | 传送链加速 | 加速 | 高 |
| 15 | 炸弹阵 I | 炸弹×3 | 方块 | 哑铃串联 | 3 | 一点起爆链 | 爆炸 | 极高 |
| 16 | 相位之门 | 时间门×2 同相 | 炸弹 | 门后藏爆 | 3 | 相位等待 | 任意 | 高 |
| 17 | 交错相位 | 时间门×2 异相 | 传送 | 0.6s 错相 | 4 | 交替时窗 | 任意 | 中 |
| 18 | 裂隙 Boss 关 | 炸弹阵+传送 | 时间门 | 环爆阵 | 4 | B3 孕核清屏链 | 爆炸/分裂 | 极高 |
| 19 | 引力初现 | 引力场×1 | 方块 | 单场塑弧 | 3 | 弧线过弯 | 引力 | 中 |
| 20 | 推拉之间 | 引+斥各1 | 镜面 | 对置场 | 4 | 场边界弹弓 | 引力/加速 | 高 |
| 21 | 轨道加速环 | 引力场+菱形环 | — | 圆桌布局 | 4 | 绕核甩击 | 引力 | 高 |
| 22 | 黑洞窄巷 | 强引力 | 单向 | 巷道窄缝 | 4 | 借力穿缝 | 引力/棱镜 | 中 |
| 23 | 双星系统 | 引力×2 | 传送 | 双场交叠 | 4 | 场间弹道规划 | 引力 | 高 |
| 24 | 穹界 Boss 关 | 引力+炸弹 | 镜面 | 环+爆点 | 5 | B4 相位弱点教学 | 引力/爆炸 | 极高 |
| 25 | 圣殿前厅 | 全机关稀疏混合 | — | 抽样混布 | 4 | 综合辨识 | 任意 | 中 |
| 26 | 精锐走廊 | 混合+精英块 | 加速 | 直道考验 | 5 | 高压精度 | 加速/分裂 | 高 |
| 27 | 连锁大教堂 | 炸弹×5+三角 | 传送 | 立体爆网 | 5 | 全屏连锁编排 | 爆炸/分裂 | 极高 |
| 28 | 镜与引力 | 镜×3+引力 | 时间门 | 反射+塑向 | 5 | 复合轨迹计算 | 棱镜/引力 | 高 |
| 29 | 门后之王 | 时间门墙+单向 | 炸弹 | 节奏关 | 5 | 0.6s 决策窗口 | 任意 | 高 |
| 30 | 终焉圣殿 | 全机关满配 | — | 密集终考 | 5 | Run 最终综合试炼+B5 | 全 Build | 极高 |

> 每张图只写 entries[]（kind/variant/pos/rot/len/hp）——一张图 ≈ 8~14 行数据，30 张 ≈ 400 行资产数据；由 StageLibrary 按区域+难度索引。

## 10. 地图随机化：StageData × StageModifier × RunSeed（§十二）

```
Run 开始 → runSeed = RngService.NewSeed()
进战斗节点(stageIdx) → subRng = System.Random(hash(runSeed, nodeIndex, stageIdx))
  → 从区域 Modifier 池抽 0~2 个 ModifierData（确定性）
  → StageBuilder.Build(StageData + Modifier 应用) → 该 Run 的"版本 A/B/C"
```
| ModifierData 字段 | 范围 | 作用面 |
|---|---|---|
| hpScale | 1.0~1.4 | 全可破坏 HP |
| speedScale | 1.0~1.3 | 三角/门旋转速度 |
| mechBonus | +0~2 | 同类机关补量（从模板池复制） |
| bombBonus | +0~3 | 补炸弹 |
| coreShield | +0~6 | 核心外随机方位补护盾块 |
| ballSpeedScale | 0.9~1.15 | 本关球速 |
| specialId | 可空 | 区域特殊（如"全图单向反转""引力增强"） |
- 精英/Boss 现有硬编码修饰（hp×2/×3）**迁入** ModifierData 管线【重构】；
- 同一 runSeed 完全可复现（测试/回放/未来"挑战码分享"）。

## 11. 战斗地图 UI（§十三，竖屏落地版）

```
┌──────────────────────────┐
│ ‖暂停   R3·混沌裂隙  L4/8 ‖ ← TopBar(SafeArea内,高120)：左=暂停/退出，中=区域·层，右=设置
│        CHAIN ×12          │ ← 连锁（仅≥2显示，已有）
│ ◎ CORE  ▓▓ HP数字        │ ← 核心HP贴核心本体（已有浮字数字），不占UI
│      （战斗区 100%宽×78%）│ ← 瞄准=全屏拖动，UI零遮挡
│                          │
│  ●×12  强化×3  ◇×6       │ ← BottomBar(高110)：左=球库存+强化+水晶合并一行
│          [球种:引力球]    │    右=球种切换按钮(左下,拇指可达)
└──────────────────────────┘
```
- 单手：全部按钮贴左下拇指弧区；右下 25% 留空；
- 手指遮挡：瞄准起点=手指按下处（全屏），发射点固定底部；预测线绘制在手指上方可见（预测器已有）；
- Hud 现有元素迁移：stock/stage/upgrade/crystal 四个散落标签→合并两行；BallTypeBtn 移至 BottomBar 右端。

## 12. 触摸输入架构（§十四）

```
Mouse/Touch → InputManager(统一 PointerEvent{phase,pos,delta}) → AimSystem → LaunchSystem
```
| 项 | 值 | 说明 |
|---|---|---|
| 按下 | 全屏任意点 | 记录起点，不要求按在球上 |
| 拖动→方向 | 拖动矢量决定发射角（保持现有 BallLauncher 手感，验收=行为对齐） | Gameplay 禁止直接读 Input.mousePosition |
| 最小拖动 | 30px（低于=取消，现有 cancelDragDist 对齐） | 防误触 |
| 最大拖动 | 无限（方向制，非蓄力） | 简单 |
| 角度限制 | 仰角 ≥10°（现有 minAimElevationDeg） | 防水平死弹 |
| 吸附 | 无 | 保持自由瞄准 |
| 预测轨迹 | 3 次反弹 + 点距 0.4（现有） | 拖动实时更新 |
| 实现 | 【重构】BallLauncher 拆三层；TrajectoryPredictor 保留 | 接口：IAimSource（可被回放/测试替身） |

## 13. 战斗结束流程（§十五）

现有流程已符合"结算→3选1→回地图"（不自动下一关 ✓）。补强：
- **VictoryReport**【新增】：`VICTORY / 最大连锁 ×N / 最大单次伤害 N / 获得水晶 +N（→星尘 +M 预览）` + 3 选1 卡（复用现有 RewardPanel 风格）→【继续】→ 地图；
- 数据来源 **RunStats**【新增】：ChainSystem 最大值钩子、单发最大伤害（ExplosionSystem/BallCollision 聚合）、水晶流水。

## 14. 地图返回机制（§十六）
见 §7.3/§7.4（状态机+连线动画）。现有"可点=邻接"逻辑保留，升级为状态驱动渲染。

## 15. Boss 设计（§十七，5 区域 5 Boss，机制型而非血条型）

| Boss | 区域 | 形态/机制 | 玩法改变 | 复用/新增 |
|---|---|---|---|---|
| B1 旋转六边堡 | R1 | 护盾环 12 块 × 45°/s 旋转 + 弱点弧 60°/s（弧内×2/弧外×0.2） | 学会掐弧窗口 | 【已有】护盾环+弱点弧；仅加环旋转 |
| B2 镜面三棱核 | R2 | 三片 120° 间隔旋转镜面装甲（镜面反射球），缺口 60° 露核心；核心带弱点弧 | 玩家必须计算反射角把球送进缺口 | MirrorLine×3【新增BossController】，弱点弧已有 |
| B3 混沌孕核 | R3 | 每 7s 生成 2 方块+1 炸弹（场上限 14）形成活体屏障；核心本体脆 | 唯一解法=炸弹连锁开路 | 【新增】SpawnController（复用 GeometryBlock/BombBlock） |
| B4 黑洞引力核 | R4 | 自带×1.5 引力场把球拉入轨道；相位弱点弧 80°/s（弧外×0.1） | 利用轨道甩击+相位窗口 | GravityField+弱点弧【新增组合】 |
| B5 终焉几何核 | R5 | 三阶段：P1 旋转护盾环→P2 弱点弧+双时间门封锁→P3 狂暴（引力+炸弹生成+弧 100°/s），每阶段独立 HP 段 | 综合考试 | 全复用+PhaseController【新增】 |

- BossData（ScriptableObject）：phases[]（hp、shieldRing{count,radius,rotSpeed}、weakArc{angle,speed,crit,armor}、gravity{strength,radius}、spawner{period,pool}、timeGates[]）；
- Boss 解锁：击败 B_k → 区域 k+1 进入 Run 随机池（决策点 §25-1）。

## 16. 经济系统（§十八）

| 资源 | 归属 | 获得 | Run 结束后 |
|---|---|---|---|
| 水晶 | Run 内 | 战斗节点+2/事件+3 | **清空**，按 2:1 转化为星尘预览（仅展示） |
| 星尘 | 局外 | 节点通关(3/5/10)+Run 通关+8+水晶 2:1 转化 | **保留**（MetaProgress 已有） |
| 词缀/强化 | Run 内 | 3选1/商店/宝箱/事件 | **清空**（BuildState.Reset 已有） |
| 球库存 | Run 内 | 关卡配置+局外加成 | **清空** |
| 永久解锁 | 局外 | 星尘购买 | **保留**（MetaProgress 已有） |
| 图鉴条目 | 局外 | 首见机关/球/Boss 自动解锁 | **保留**【新增 CodexEntry】 |
| 纪录 | 局外 | 最高层数/最高星尘单 Run/Boss 击杀数 | **保留**（PlayerProfile） |

## 17. 代码架构（§十九，目录 + 类职责）

```
Assets/_Game/
├─ Scripts/
│  ├─ Core/        GameBootstrap【已有·改尾部流程】 GameConfig【已有】 GameEvents【已有】
│  │               ChainSystem【已有】 NeonStyle【已有】 ProcSprites【已有】 RngService【新增】
│  ├─ Ball/        Ball【已有+Pierce】 BallManager【已有+池化】 BallData【已有】
│  │               BallLauncher【重构→InputManager】 TrajectoryPredictor【已有】
│  ├─ Input/       InputManager【新增】 AimSystem【重构】 LaunchSystem【重构】
│  ├─ Geometry/    GeometryEntity+9机关类【已有】
│  ├─ Stage/       StageManager【已有】 StageBuilder【已有】 StageData【已有+region】
│  │               StageLibrary【新增】 StageModifier【新增】 ModifierData【新增】 RegionDef【新增】
│  ├─ Boss/        BossData【新增】 BossController【新增】 BossPhaseRunner【新增】
│  │               CoreBlock【已有+弱点弧】
│  ├─ Roguelite/   RunMap【重构v2】 MapNode【已有+状态】 LayerConfig【新增】
│  │               MetaProgress【已有→挂 IStorage】 RunData【新增】 RunManager【新增】
│  ├─ Build/       BuildState【已有】 UpgradeDef【已有】
│  ├─ UI/          Hud【重构】 MainMenuScreen【新增】 MapScreen【重构】 BallLibraryScreen【新增】
│  │               BuildScreen【新增】 CodexScreen【新增】 MetaShopScreen【新增】
│  │               UIManager(PageStack)【新增】 SafeAreaRoot【新增】 UIAnim【新增】 VictoryReportPanel【新增】
│  ├─ Visual/      EffectManager【重构+池化】 FloatingText【已有→池化】 BackgroundGrid【已有】
│  ├─ Audio/       AudioManager【已有】 ToneSynth【已有】
│  ├─ Pool/        PoolSystem【新增】（Ball/Debris/FloatingText/Spark/Ring 五池）
│  ├─ Stats/       RunStats【新增】
│  ├─ Save/        SaveService【新增=IStorageService 封装】 PlayerProfile【新增=MetaProgress 扩展】
│  ├─ Platform/    PlatformManager【新增】 IPlatformService 族【新增】
│  │               UnityStorageService【新增】 UnityLifecycleService【新增】
│  │               (P12: StarkStorageService/StarkAdService/StarkShareService)
│  └─ Editor/      MobileBuild【已有】 StageAuthorTool【新增：30图数据生成校验】
├─ Data/Resources/ GameConfig/4×BallData+GravityBall+PierceBall【新】/Stage1-30/Region1-5/
│                  Boss1-5/UpgradeDef×9/MetaUnlockDef/LayerConfig/CodexIndex【新】
└─ Scenes/         Main.scene（唯一场景）
```
> 本项目**零 Prefab**：一切 GameObject/组件/视觉运行时构建。上文"PREFAB"概念在本项目=代码构建函数（如 `CoreBlock.Build()`），新系统延续此约定。

关键类职责（新增部分）：RunManager=Run 生命周期(开始/中断恢复/结束转化)；UIManager=Panel 栈与过渡；StageLibrary=按 区域×层数 抽 StageData；StageModifier=确定性修饰；PoolSystem=通用对象池(List<Pooled>，Get/Release，零运行时 Instantiate)；RunStats=战报聚合；PlayerProfile=星尘/解锁/图鉴/纪录/设置的统一存档门面（向后兼容现有 MetaProgress 语义）。

## 18. 数据结构字段定义（§二十）

| 数据 | 字段（新增加粗） |
|---|---|
| StageData | id, stageName, **regionId, difficulty**, entries[]{kind,variant,position,rotationDeg,length,hp}, corePos, coreHp, ballStock, **bossId(可空)** |
| RegionDef | id, name, themeDesc, stageIds[], bossId, modifierIds[] |
| ModifierData | id, name, desc, hpScale, speedScale, mechBonus, bombBonus, coreShield, ballSpeedScale, specialId |
| BossData | id, name, coreHp, coreScale, phases[]{hp, shieldRing{count,radius,rotSpeed}, weakArc{angle,speed,crit,armor}, gravity{strength,radius}, spawner{period,max}, gates[]} |
| BallData | ballName, kind, speed, damage, radius, splitAngleDeg, core/glow/trailColor, **unlockCost, codexDesc** |
| UpgradeDef | title, desc, maxStacks, 效果字段【已有】 |
| MetaUnlockDef | id,title,desc,cost,repeatable,maxStacks【已有】 |
| RunData | runSeed, layer, nodeIds[], currentNodeId, clearedIds[], crystals, buildStacks[], stockBonus, loadoutBallId, stats{maxChain,maxHit} |
| PlayerProfile | stardust, unlocks[], loadoutBallId, best{layer,stardust}, codex[], bossKills[], settings{master,bgm,sfx,ui} |
| LayerConfig | layer, allowedTypes[]{type,weight}, nodeCount{min,max}, guarantees[] |
| CodexEntry | id, category(机关/球/Boss), title, desc, unlockCondition |

## 19. 性能预算与池化（§二十二）

| 项 | 预算 | 现状 | 动作 |
|---|---|---|---|
| 对象池 | Ball×8 / Debris×40 / 浮字×20 / 火花×60 / 冲击环×8 | 全部即用即建 | 【重构】PoolSystem，战斗中零 Instantiate/Destroy |
| 球并发 | ≤6（现有 maxConcurrentBalls ✓） | 已达标 | — |
| 音效并发 | ≤14 声部（AudioManager v2 ✓） | 已达标 | — |
| SpriteRenderer 活动 | ≤160 | 峰值~80 | 预算内 |
| DrawCall | ≤120 | 程序贴图共享材质，约 40-60 | 预算内 |
| 粒子 | ≤150 | 火花走池 | 火花池上限即预算 |
| GC/帧 | ≈0 | 每帧有 Instantiate | 池化后达标 |
| 目标帧率 | 主流 60 / 低端 30（Application.targetFrameRate=60 已有） | — | 真机压测 |
| WASM 包体 | 代码包为主；音频压缩后 <8MB；字体子集 | 音频 51 文件未压缩 | P11 音频压缩/瘦身 |
| 启动 | 冷启动到大厅 <5s | 单场景轻量 | P14 实测 |

## 20. UI 跳转与场景结构（§二十三/§二十四）

**结论：单 Main.scene + 全 Panel 化**（与现状一致，判断合理并维持）。
- UIManager PageStack：`MainMenu ⇄ {BallLibrary, Build, Codex, MetaShop}`；`MainMenu → MapScreen → Battle(Hud) → VictoryReport → MapScreen → … → RunResultPanel → MainMenu`；
- 战斗区与地图共场景：进战斗=关 Panel+BuildStage；出战斗=开 Panel+清 Stage（已有能力）；
- 每个页面一个 Panel 类 + 一个 Build() 函数，禁新 Scene。

## 21. 动画规范（§二十五）

时间 token：S=0.12s（按压/微反馈） M=0.18s（pop/受击） L=0.30s（过场/连线）；缓动统一 OutQuad。
| 场景 | 动画 | 实现 |
|---|---|---|
| 按钮按下 | Scale 1→0.94→1 | UIAnim.Pop（S） |
| 节点完成 | 1→1.2→1 + ✓淡入 | UIAnim.Pop（M） |
| 连线点亮 | Alpha 0→1 stagger | UIAnim.Fade（L） |
| 核心受击 | Scale punch 0.08 | EffectManager.ScalePunch【已有】 |
| Boss 出现 | 旋转+Scale 0→1+屏震 | EffectManager.Shake+自定义【新增】 |
| 碎裂/火花/拖尾/震屏 | — | EffectManager 全套【已有】 |

## 22. 音频（§二十六）
完整方案见 `Docs/AudioDesign.md`（C 大调五声音阶、机关音高身份 □C △D ◆E ◯G ◇A ◎C↑、Combo 分层编曲、动态音乐、限声）——与本次需求完全对齐，不重复设计。9A 框架已上线；小游戏侧补充：音频压缩采样率、BGM 流式、解码并发预算在 P11 落实。

## 23. MVP 开发阶段（§二十七，14 阶段映射 + 完成标准）

| Phase | 状态 | 交付 | 脚本/数据 | 完成标准 |
|---|---|---|---|---|
| P1 核心物理 | ✅ 已有 | Ball/RB/回收 | — | T1-T8 已过 |
| P2 基础机关 | ✅ 已有 | 9 机关 | — | 已过 |
| P3 触摸瞄准 | 🔧 重构 | InputManager/AimSystem/LaunchSystem 三层 | Input/*.cs | 手感回归=与现 BallLauncher 逐参数对齐；Win 鼠标+真机触摸各过一遍 |
| P4 战斗 UI | 🔧 重构 | SafeArea+TopBar/BottomBar 重排 | SafeAreaRoot/UIAnim/Hud v2 | 异形模拟器无遮挡；按钮拇指可达；轨迹区零 UI |
| P5 地图 v2 | 🔧 重构 | 8 层+状态+连线+滚动+Run 存档 | RunMap v2/LayerConfig/RunData/RunManager | 层数8固定；断点续玩（杀进程恢复同 Run）；连线动画可测（状态断言） |
| P6 奖励/战报 | 🔧 扩展 | VictoryReport+RunStats | Stats/RunStats, VictoryReportPanel | 战报数字与统计一致；流程仍不自动进关 |
| P7 30 张图 | 🆕 新增 | 30 StageData+5 区域+StageLibrary+穿透球 | Stage5-30.asset, RegionDef×5, BallKind.Pierce, StageLibrary | 30 图全部可构建无报错；区域机制分布符合 §9 表 |
| P8 5 Boss | 🆕 新增 | BossData×5+行为控制器 | Boss/*.cs, BossData×5 | 每个 Boss 机制可测（B1 弧/B2 镜装甲/B3 生成上限/B4 相位/B5 三阶段）；纯 HP 增加≠过测 |
| P9 局外大厅 | 🆕 新增 | 大厅+四页+UIManager | MainMenu/BallLibrary/Build/Codex/MetaShop/UIManager | 大厅→球库→装备→开战全链路；页面栈无泄漏 |
| P10 存档 | 🔧 重构 | IStorageService+PlayerProfile 迁移 | Platform/Save/*.cs | MetaProgress 语义不变；Win/Android 落 PlayerPrefs；小游戏实现留 P12 |
| P11 性能 | 🆕 新增 | 五池+预算压测 | PoolSystem, EffectManager 池化 | 战斗 5 分钟 0 Instantiate/Destroy（统计断言）；低端真机 30fps |
| P12 抖音接入 | 🆕 新增 | WebGL+Stark 管线跑通 | StarkStorage/Ad/Share + 构建脚本 | 开发者工具可运行；§2.3 四坑全过；KV 存档生效 |
| P13 广告 | 🆕 新增 | 3 点位（复活/双倍星尘/商店刷新） | AdService 接线 | NoOp 下全流程可玩；真机广告回调正确 |
| P14 发布测试 | 🆕 新增 | 包体/启动/提审 | — | 冷启动<5s；包体达标；过抖音审核清单 |

（Android APK 链已就绪作为 P14 前的真机验证载体——移动端打包工具已交付：菜单 GeoBreaker/移动端/*。）

## 24. 需求覆盖对照（用户 29 条 → 本文档）

| 需求 | 章节 | 需求 | 章节 |
|---|---|---|---|
| 一 核心定位 | §1 | 十六 地图返回机制 | §7.3-7.4/§14 |
| 二 平台约束/抽象 | §2 | 十七 Boss | §15 |
| 三 产品结构 | §3 | 十八 经济 | §16 |
| 四 大厅 UI | §4 | 十九 代码架构 | §17 |
| 五 球库 | §5 | 二十 数据驱动 | §18 |
| 六 构筑 | §6 | 二十一 平台抽象 | §2.4 |
| 七 地图系统 | §7 | 二十二 性能 | §19 |
| 八 节点类型 | §8 | 二十三 页面跳转 | §20 |
| 九 层数计算 | §7.1 | 二十四 场景结构 | §20 |
| 十/十一 30 图 | §9 | 二十五 动画 | §21 |
| 十二 随机化 | §10 | 二十六 音频 | §22 |
| 十三 战斗 UI | §11 | 二十七 MVP | §23 |
| 十四 触摸 | §12 | 二十八 先设计后代码 | 本文档 |
| 十五 战斗结束 | §13 | 二十九 最终目标 | §1/§23 |
| — | — | 二十九目标清单 | ✓ 全部覆盖 |

## 25. 设计决策点（开工前请你拍板）

1. **Boss 池规则**：击败 B_k 解锁区域 k+1 入随机池（推荐）？还是按 Run 轮换固定顺序？
2. **穿透球**：按 §5 设计为独立球种（每发 2 次穿透），保留局内"穿透强化"升级（两者叠加）？
3. **大厅首版范围**：P9 一次做全四页（球库/构筑/图鉴/商店），还是先大厅+球库两页（图鉴/商店随 P9.5 补）？
4. **货币命名**：星尘保留（已有存档语义）还是统一改"金币"（需迁移一次旧档）？
5. **Run 长度**：8 节点（推荐，§7.1）还是更短的 6 节点快速 Run（抖音碎片化，可做成后置选项）？
6. **失败节点**：球耗尽=可原地重试本节点（推荐）还是直接判 Run 失败回大厅？
7. **P3 手感基线**：InputManager 重构期间锁定现版 BallLauncher 手感为验收基准（推荐）？
