# 《几何弹球》完整音频系统设计文档（Phase 9 蓝图）

> 版本 v1.0 ｜ 2026-09-19 ｜ 基于 Phase 1-8 已完成代码现状撰写
> 定位：可直接进入 Unity（Tuanjie 1.9.3 / Unity 2022.3 内核）制作阶段的施工图。
> 核心命题：**"玩家不是在听游戏音效，而是在用弹球演奏这局游戏。"**

---

## 0. 现状盘点（Phase 8 → Phase 9）

### 0.1 已有资产（全部保留复用）

| 现有文件（Assets/_Game/Data/Resources/Audio/） | 迁移目标 |
|---|---|
| SFX_HitBlock.wav | → SFX_Block_Hit_v1（方块撞击变体 1） |
| SFX_HitCore.wav | → SFX_Core_Hit_v1（核心受击变体 1） |
| SFX_HitMech.wav | → 保留为兜底通用机关音（新系统按类型分发后基本不再触发） |
| SFX_Bounce.wav | → SFX_Ball_Bounce_v1（撞墙变体 1） |
| SFX_Explode.wav | → SFX_Explode_Low_v1（爆炸第 1 层） |
| SFX_Launch.wav | → SFX_Ball_Launch_v1 |
| SFX_Recycle.wav | → SFX_Ball_Return_v1 |
| SFX_Reward.wav | → SFX_UI_Confirm_v1 |
| SFX_Portal.wav | → SFX_Portal_Exit_v1 |
| BGM_Field.wav | → BGM_Ambient（探索层 stem） |

### 0.2 Phase 8 已实现 / Phase 9 缺失

| 已有 | 缺失（本文档补齐） |
|---|---|
| 3 源轮换 PlayOneShot | 总线（Bus）体系、优先级、限声、同类冷却 |
| 音量按球速缩放 | 完整球速→Pitch/Volume/亮度三参数映射 |
| pitch ±5% 抖动 | 音符级系统：机关音高身份、Combo 音阶 |
| 单条 BGM 循环 | 动态分层音乐 + Ducking + 状态机 |
| 碰撞按"可破坏/不可破坏"二分 | 按机关类型分发的**形状声音语言** |
| — | 核心警戒层、Boss 频段阵营、胜负仪式音、UI 音组 |

---

## 1. 音频风格总纲

**风格关键词**：Minimal · Geometric · Futuristic · Clean · Reactive
**禁止**：管弦、拟真枪炮、卡通弹跳、长混响、人声、旋律抢戏的 BGM。

### 1.1 频率规划（声部席位表——所有资产禁止越席抢频）

| 频段 | 席位 | 归属 |
|---|---|---|
| 30–120 Hz | Sub 席 | Boss、核心 DOOM、爆炸第 1 层、Combo ≥7 的 Bass 层 |
| 120–500 Hz | 低中席 | 方块 tik/tak、护盾 clank、力场低鸣 |
| 500 Hz–2 kHz | 中席 | 机关电子音、UI blip、Bossa 警报 |
| 2–8 kHz | 亮席 | 三角/镜面/核心泛音 TING、Combo 音符、Arp |
| 8 kHz+ | Air 席 | Shimmer、穿透 whoosh、胜利高频释放 |

### 1.2 调性策略（本作音频的地基）

全局统一 **C 大调五声音阶（C D E G A）**：
- 五声音阶任意组合不产生小二度冲突 → **任意路线、任意时序的机关音叠加永远和谐**，"弹球演奏"才有音乐合法性；
- 击打层（AI 采样）不要求准确音高，**音高身份由程序化合成层（ToneSynth）保证准调**（AI 无法保证音准，这是采用混合合成的根本原因）；
- BGM 全部 stem 限定 C 小调 / 110 BPM（与五声音阶同音族，暗色基调）。

### 1.3 响度基准

| 总线 | 相对电平 |
|---|---|
| Master | 1.0（峰值预留 -1dB 余量） |
| SFX 击打类 | 0.55–0.75 |
| 机关/Combo 音符层 | 0.40–0.55 |
| UI | 0.40 |
| BGM stem（叠加后） | 0.22–0.32（永远低于 SFX，见 §14 Ducking） |
| 静默窗（核心破坏） | Master ×0.12 |

---

## 2. 四大支柱总览（本文档的骨架）

| 支柱 | 解决什么 | 章节 |
|---|---|---|
| ① 形状声音语言 | 每种几何体有固定音色 + 固定音高身份，听声辨物、辨路线 | §10 §11 |
| ② Combo 音乐化 | 连锁等级 = 旋律上行 + 编曲逐层加厚 | §12 |
| ③ 球速表情 | 速度决定音高/音量/亮度，"tik→tink→TING!" | §8 §9 |
| ④ 动态音乐 + Ducking | 状态编曲 + 碰撞密度自动压 BGM | §13 §14 |

辅以：优先级限声（§6）、程序化合成（§7）、核心/Boss/胜负仪式音（§15–17）。

---

## 3. 系统架构

```
GameBootstrap
 └─ AudioManager (v2)  ← 唯一音频入口（GameObject 自持，无场景资产）
     ├─ AudioBus × 6   Master → BGM / Ambient / SFX_Ball / SFX_Geom / SFX_Mech / SFX_Boom / UI
     │                   （代码总线，等效 AudioMixer，见 §5）
     ├─ VoicePool        14 个轮换 AudioSource + 优先级抢占（§6）
     ├─ ToneSynth         运行时程序化合成：五声音符 ×3 音色 + 旋律（§7）
     ├─ SampleBank        Resources/Audio/ 下 AI 采样 wav（§18）
     ├─ MusicDirector     3 stem 增益状态机 + Ducking（§13 §14）
     ├─ CoreWarnState     核心 HP 警戒脉冲状态机（§15）
     └─ 订阅 GameEvents：OnBallCollision / OnChainChanged / OnExplosion / OnVictory /
        OnBallLaunched / OnBallRecycled / OnAllBallsConsumed
```

**原则**：所有 GameObject 一律不持有 AudioSource（球/机关只调 AudioManager 静态入口），符合 Phase 8 既定架构。

---

## 4. AudioManager v2 核心设计（代码骨架）

```csharp
namespace GeoBreaker
{
    /// <summary>音频定义：一条 SFX = 变体组 + 总线 + 优先级 + 限声参数。</summary>
    public class SfxDef
    {
        public string id;               // "Block_Hit"
        public AudioClip[] variants;   // AI 采样变体（≥1）
        public string bus;              // "Geom" / "Ball" / "Mech" / "Boom" / "UI"
        public int priority;            // §6 优先级表
        public int voiceLimit = 3;      // 同类最大并发
        public float cooldown = 0.04f;  // 同类最小触发间隔（秒）
        public float vol = 0.65f;       // 基准音量
        public float pitch = 1f;        // 基准 pitch（运行时再叠随机/球速）
        public bool speedScaled = false;// 是否叠加球速映射（§8）
    }

    public class AudioManager : MonoBehaviour
    {
        public static AudioManager I { get; private set; }

        // ---- 总线（§5）----
        readonly Dictionary<string, AudioBus> _buses = new Dictionary<string, AudioBus>();

        // ---- 限声（§6）----
        readonly Dictionary<string, float> _lastPlay = new Dictionary<string, float>();
        readonly Dictionary<string, int> _liveVoices = new Dictionary<string, int>();
        readonly List<Voice> _pool = new List<Voice>();     // 14 个 AudioSource
        readonly Queue<float> _playTimes = new Queue<float>(); // Ducking 密度计

        // ---- 程序化音符（§7）----
        Dictionary<string, AudioClip> _tones;

        void Init() { /* 建 6 总线 + 14 声部池 + ToneSynth 缓存 + 订阅 GameEvents + MusicDirector 起播 */ }

        /// <summary>统一播放管线：冷却 → 限声 → 抢占 → 随机化 → 球速映射。</summary>
        public static void Play(string id, float volScale = 1f, float pitchScale = 1f,
                                Ball ball = null) { /* 见 §6 伪码 */ }
    }
}
```

播放管线（顺序即规则）：

```
Play(id, …)
  1. def = _defs[id]; now = Time.time
  2. 冷却：if (now - _lastPlay[id] < def.cooldown) → 丢弃（不排队，丢弃就是防堆叠）
  3. 限声：if (_liveVoices[id] >= def.voiceLimit) → 挑池中"同类且最旧"的声部截断复用（盖旧不叠新）
  4. 全局：空闲声部 < 2 时 → 抢占池中优先级最低且 < 本条优先级的声部；
     抢不到（全池优先级更高）→ 丢弃
  5. 随机化：clip = variants.Random(); pitch = def.pitch × Rand(0.95,1.05) × pitchScale;
     vol = def.vol × Rand(0.85,1.0) × volScale
  6. 球速映射（def.speedScaled）：k = InverseLerp(minSpeed,maxSpeed, ball 速度)（§8 公式）
  7. PlayOneShot；记录 _lastPlay / _liveVoices / _playTimes（供 Ducking）
```

---

## 5. 总线架构（代码实现的 AudioMixer 等价物）

### 5.1 结构

```
Master (增益 1.0, 持 AudioLowPassFilter —— 核心爆炸时整体压暗用)
 ├─ BGM      （BGM_Beat / BGM_Arp 两 stem，LPF 可用）
 ├─ Ambient  （BGM_Ambient stem、力场循环等氛围层）
 ├─ SFX_Ball （发射/反弹/回收/分裂/穿透）
 ├─ SFX_Geom （方块/三角/镜面击打）
 ├─ SFX_Mech （传送门/力场/时间门/菱形）
 ├─ SFX_Boom （炸弹三层爆炸、核心破坏）
 └─ UI       （按钮/选卡/购买）
```

**为什么不用 .mixer 资产**：① Tuanjie/Unity 运行时无法创建 AudioMixer 资产（EditorOnly API），与本项目"纯代码生成"原则冲突；② 本作动态音乐只需求"增益 + 低通"两种总线处理，AudioSource.volume + AudioLowPassFilter 完全等效；③ 留有升级路径（§5.3）。

### 5.2 代码总线实现

```csharp
public class AudioBus
{
    public string name;
    public float gain = 1f;          // 用户设置 × 状态增益
    public readonly List<AudioSource> sources = new List<AudioSource>();
    public void Apply() { foreach (var s in sources) s.volume = Base(s) * gain; }
}
// Bus = 一组 AudioSource 的归属登记（池内每个声部挂到对应 Bus 的 GameObject 下），
// 每帧仅当 gain 变化时 Apply——零成本。
```

### 5.3 音量设置 API（落 PlayerPrefs）

```csharp
public static void SetMasterVolume(float v) // 0..1，存 "GB_Audio_Master"
public static void SetBgmVolume(float v)     // "GB_Audio_BGM"（BGM+Ambient 两总线）
public static void SetSfxVolume(float v)     // "GB_Audio_SFX"（Ball/Geom/Mech/Boom 四总线）
public static void SetUiVolume(float v)      // "GB_Audio_UI"
```
- Init 时从 PlayerPrefs 读回；UI 面板（后续在地图界面加设置页）只调这四个 API。
- 内部用 `Mathf.Log10(v)*20 + 20` 折半程曲线近似人耳响度（0.5 → 约 -6dB）。

### 5.4 升级路径（可选，非 MVP）

若未来需要混响/侧链压缩：写一次性 EditorUtility 生成 `.mixer` 资产（Groups 与 §5.1 一一对应），把各 Bus 的 AudioSource.outputAudioMixerGroup 指过去即可，业务代码零改动——因为所有播放都走 AudioManager。

---

## 6. 优先级 · 限声 · 防重复（三张表即全部规则）

### 6.1 优先级表（整数，越大越不可被抢占）

| 类别 | priority | voiceLimit | cooldown |
|---|---|---|---|
| CoreDestroy（三层爆炸+泛音） | 100 | 1 | — |
| Boss Event（Intro/Doom） | 90 | 2 | 0.6 |
| Bomb Explosion（三層） | 80 | 2 | 0.12 |
| Special Mechanism（Portal/Gravity/Eject/TimeGate） | 60 | 3 | 0.10 |
| Core Hit | 50 | 3 | 0.06 |
| Combo 音符 | 40 | 4 | 0.05 |
| Normal Collision（Block/Bounce/Triangle/Mirror/Diamond） | 20 | 4 | 0.03 |
| UI | 10 | 3 | 0.05 |

**池规模**：全局 14 个声部（移动端安全上限 ≤20 个 AudioSource，实测 14 池 + 3 stem + 2 力场循环 + 1 rush 循环 ≈ 20）。
**同帧去重**：同 id 同一帧第二次触发直接丢弃（cooldown 的最小粒度兜底）。
**饱和闸门**：滑动 0.5s 窗口内 PlayOneShot 次数 >40 时，priority ≤20 的请求全部丢弃（50 球齐撞场景保底）。

### 6.2 防重复三件套（全部叠加生效）

| 机制 | 参数 | 效果 |
|---|---|---|
| Pitch Randomization | ×0.95–1.05 | 消除机关枪效应 |
| Volume Randomization | ×0.85–1.0 | 消除电平恒定的机械感 |
| 变体池 | 每类 2–6 个 AI 变体 | 消除音色重复（§18） |

---

## 7. ToneSynth 程序化合成器（"球=乐器"的音准引擎）

### 7.1 为什么需要它

| 需求 | AI 采样 | ToneSynth |
|---|---|---|
| 复杂质感（爆炸/传送门/whoosh） | ✔ | ✘ |
| **准调音符**（C4=261.63Hz） | 无法保证 | ✔ 精确 |
| 运行时任意变调/移调 | 受采样质量制约 | ✔ 实时 |
| 资产数量 | 每音一文件 | **零文件** |

→ 结论：**击打质感用 AI 采样，音高身份用 ToneSynth**，两者叠出"又脆又准"的几何音。

### 7.2 五声音阶频率表（ToneSynth 内置）

```
度数:  C4     D4     E4     G4     A4     C5     D5     E5     G5     A5     C6
Hz :  261.63  293.66  329.63  392.00  440.00  523.25  587.33  659.26  783.99  880.00  1046.50
idx : 0      1      2      3      4      5      6      7      8      9      10
```

### 7.3 音色配方（AudioClip.Create 运行时生成，Init 一次性缓存）

```csharp
static AudioClip Crystal(float f, float dur = 0.18f)  // TING：sin(f)+0.42sin(2f)+0.18sin(3f)，快攻5ms+指数衰减
static AudioClip Sub(float f, float dur = 0.30f)      // DOOM：sin(f)+0.15sin(2f)，慢衰减
static AudioClip Blip(float f, float dur = 0.07f)    // BEEP：纯 sin + 3 采样点瞬态，极快衰减
static AudioClip Pad(float f, float dur = 0.40f)     // 层垫：sin+0.3sin(2f)，20ms 攻+线性释
// 示例（44100Hz 单声道，生成后 SetData，Dictionary<string,AudioClip> 缓存按 "Crystal_261.63" 键取）
```

### 7.4 生成清单（全部零文件，Init 期 ~35 条，内存 <2MB）

| 用途 | 配方 | 数量 |
|---|---|---|
| Combo 11 音 ×Crystal | §12 | 11 |
| Combo 11 音 ×Sub（低八度，Bass 层用） | §12 | 11 |
| Combo 11 音 ×Pad（五度层用，f×1.5） | §12 | 11 |
| 机关音高身份（C4/D4/E4/G4/A4/C5/D5 ×Crystal/Blip） | §11 | 7 |
| 分裂双音/菱形阶梯/胜利琶音/失败下行 | §9–17 | 按需临时生成（同缓存机制） |

---

## 8. 球速→声音映射（速度表情系统）

```
k = InverseLerp(minSpeed=8, maxSpeed=24, |v|)          // 0..1（GameConfig 驱动，不写死）
pitchMul = 0.92 + 0.36k        // 低速略降防闷 → 高速 +4.5 半音（TING!）
volMul   = 0.45 + 0.55k        // 低速轻 → 高速满
亮度:     k ≥ 0.75 换 HighSpeed 变体组（更亮的 AI 采样替代普通变体）
拟声参考: k<0.35 "tik" | 0.35–0.75 "tink" | >0.75 "TING!"
```

- **实现**：`Play(id, ball)` 且 `def.speedScaled` 时自动套用；预测线瞄准时也可用同曲线预听（后续 polish，非 MVP）。
- **全局 Speed Rush 层**：一个循环 shimmer 源（Ambient 总线），volume = (全场最快存活球 k)²×0.30 —— 高速期持续"有风声"，与碰撞 TING 形成连续/离散两个速度听觉维度。
- **穿透 whoosh 密度**：穿透球连续穿块的冷却 = Lerp(0.12, 0.05, k)，速度越快 whoosh 越密（§9 P-2）。

---

## 9. 球类音效规格表（AI 资产 A 组）

> Prompt 统一前缀 `SFXP = "Short futuristic geometric game sound effect, "` 统一后缀 `, dry, no music, no voice, minimal reverb.`（下表 Prompt 列省略前后缀）

| # | 文件名 | 时长 | Prompt（补中段） | pitch | vol | 变体 | 触发条件（Hook） |
|---|---|---|---|---|---|---|---|
| A1 | SFX_Ball_Launch | 0.25s | very short airy whoosh followed by a bright pluck, energetic release | 1.0 | 0.60 | 2 | `Ball.ForceLaunch()`（v1=现 SFX_Launch） |
| A2 | SFX_Ball_Bounce_v1..6 | 0.06–0.12s | soft wall bounce thud, neutral, subtle, low fatigue, 6 distinct variants | 1.0 | 0.40 | 6 | 撞不可破坏几何体/墙（事件分发，speedScaled） |
| A3 | SFX_Ball_HighSpeed | 0.12s | bright crisp metallic ping, high energy, clean transient | 1.05 | 0.62 | 2 | k≥0.75 时的撞墙替代层 |
| A4 | SFX_Ball_Return | 0.40s | soft absorb sound, gentle descending synth slide, warm | 0.95 | 0.50 | 2 | `BallManager.Recycle()`（v1=现 SFX_Recycle） |
| A5 | SFX_Ball_Split | 0.30s | glassy sparkle shimmer, two-note sparkle feel | 1.0 | 0.55 | 1 | `BallManager.SpawnChild()` 成功时（旋律音由 ToneSynth 叠加，见 §9.1） |
| A6 | SFX_Ball_Pierce_v1..2 | 0.15s | airy shimmer whoosh, light, passing through sound | 1.0 | 0.50 | 2 | `Ball.OnCollisionEnter2D` 穿透分支 + 单向墙通过分支（§19 H 表） |

### 9.1 分裂的音乐化（1→2 = 1 音→2 音）

分裂质感（A5 shimmer）之外，叠加 ToneSynth **双音**：`PlayTone(idx)` + `PlayTone(idx+2)`（大三度，如 C→E）。
**阶梯规则**：同一球 4s 内连续分裂，根音沿五声上移（C→E→G→C6 后封顶），4s 无分裂回落 C5 —— 连续分裂自然奏出上行琶音。
棱镜球 `PrismBurst` 同样走此管线（1→3 时播放 root+2 与 root+4 两层，更华丽）。

---

## 10. 机关声音语言规格表（AI 资产 B/C 组）+ 音高身份

> 每次命中 = **AI 击打采样（质感）+ ToneSynth 音高身份音（音准，vol×0.22）**，身份音也走 §6 限声。

### 10.1 击打质感层（AI 资产 B 组：Geometry 总线，priority 20）

| # | 文件名 | 时长 | Prompt | pitch | vol | 变体 | 触发 |
|---|---|---|---|---|---|---|---|
| B1 | SFX_Block_Hit_v1..5 | 0.08–0.15s | clean short hard click, dry impact, arcade tik tak clack, 5 variants | 1.0 | 0.62 | 5 | 球击 GeometryBlock（v1=现 SFX_HitBlock，speedScaled） |
| B2 | SFX_Block_Shatter_v1..2 | 0.20s | small geometric shatter, glass crack debris, short | 1.0 | 0.50 | 2 | 方块 hp≤0 死亡时叠加 |
| B3 | SFX_Triangle_Reflect_v1..3 | 0.15–0.2s | crystalline metallic reflection, bright transient TING, clean tail | 1.05 | 0.70 | 3 | 球击 TriangleReflector（speedScaled） |
| B4 | SFX_Diamond_Accel_v1..2 | 0.30s | rising pitch synth ding, energetic but clean acceleration | 1.0 | 0.65 | 2 | DiamondAccelerator.OnBallCollide 加速生效帧 |
| B5 | SFX_Mirror_Ting_v1..3 | 0.15s | glass crystal reflection, very crisp, high pitched ping | 1.10 | 0.65 | 3 | 球击 MirrorLine |

### 10.2 特殊机关层（AI 资产 C 组：Mechanism/Boom 总线，priority 60/80）

| # | 文件名 | 时长 | Prompt | pitch | vol | 变体 | 触发 |
|---|---|---|---|---|---|---|---|
| C1 | SFX_Portal_Enter_v1..2 | 0.40s | spatial teleport entrance, futuristic electronic whoosh, subtle low frequency, being sucked in | 0.95 | 0.60 | 2 | `PortalPair.Teleport()` 入口端 |
| C2 | SFX_Portal_Exit_v1..2 | 0.35s | crystal pling exit, bright teleport arrival, high clean tone | 1.05 | 0.60 | 2 | 同上出口端，延迟 80ms 播（v1=现 SFX_Portal） |
| C3 | SFX_Gravity_Enter | 0.50s | low frequency suction swoop, deep pull, subsonic feel | 1.0 | 0.55 | 1 | 球进入引力场（§10.4 钩子） |
| C4 | SFX_Gravity_Loop | 1.0s | deep low hum loop, seamless, sub bass drone | 1.0 | 0.30 | 1 | 引力场内循环（pitch 随距离升高，§10.4） |
| C5 | SFX_Gravity_Absorb | 0.25s | low short impact thump, deep hit | 1.0 | 0.60 | 1 | 球抵达 minDist 吸附点 |
| C6 | SFX_Repulse_Pressure | 0.8s | low tension pressure hum loop, unsettling | 1.0 | 0.25 | 1 | 斥力场内循环 |
| C7 | SFX_Repulse_Eject | 0.20s | strong pop thump, energetic ejection | 1.0 | 0.65 | 1 | 球被斥力弹出边界 |
| C8 | SFX_OneWay_Pass | 0.20s | airy swish, passing through, light | 1.0 | 0.45 | 1 | 单向墙正向通过（A6 变体亦可复用，独立 1 条更清晰） |
| C9 | SFX_TimeGate_Open | 0.15s | soft chime, gate opening, gentle | 1.0 | 0.45 | 1 | `TimeGate.Update` open 翻转 |
| C10 | SFX_TimeGate_Close | 0.12s | low muted click, gate closing | 1.0 | 0.40 | 1 | 同上 close 翻转 |
| C11 | SFX_Bomb_Beep | 0.08s | short electronic warning beep, precise single tone | 1.0 | 0.50 | 1 | 炸弹接近警报（§10.3，pitch 强制 A4） |
| C12 | SFX_Explode_Low | 0.80s | deep low frequency boom, subsonic impact, no realistic explosion | 1.0 | 0.85 | 1 | 爆炸第 1 层（v1=现 SFX_Explode） |
| C13 | SFX_Explode_Shatter | 0.50s | geometric debris shatter, crystalline crackle burst | 1.0 | 0.60 | 1 | 爆炸第 2 层 |
| C14 | SFX_Explode_Energy | 0.40s | high frequency energy release shimmer, bright sparkle | 1.0 | 0.55 | 1 | 爆炸第 3 层 |

### 10.3 炸弹接近警报（beep 节奏化，新增 8 行代码）

`BombBlock` 新增 Update 轮询（0.05s 节流）：

```csharp
// BombBlock 新增字段：float _beepT; const float WarnR = 1.15f; // = 爆炸半径×0.72
void Update()
{
    if (!Alive || Time.time < _beepT) return;
    float nearest = 99f;
    foreach (var b in Physics2D.OverlapCircleAll(transform.position, WarnR))
    {
        var ball = b.GetComponent<Ball>();
        if (ball != null && ball.IsLive)
            nearest = Mathf.Min(nearest, Vector2.Distance(transform.position, ball.transform.position));
    }
    if (nearest < WarnR)
    {
        float closeness = 1f - nearest / WarnR;                  // 0..1
        _beepT = Time.time + Mathf.Lerp(0.42f, 0.13f, closeness);  // beep→beep beep→beep-beep-beep
        AudioManager.PlayBombBeep(closeness);                    // vol=0.4+0.4c，pitch 锚定 A4
    }
    else _beepT = Time.time + 0.1f;
}
```
爆炸触发 = `ExplosionSystem`（现有 `OnExplosion` 事件）→ 三层齐发（C12+C13+C14，每层 ±4% pitch 抖动错开瞬态）。

### 10.4 引力/斥力场音频（进入→渐强→吸附三段）

`GravityField.FixedUpdate`（已遍历球）追加每球状态跟踪：

```csharp
readonly Dictionary<int, bool> _inside = new Dictionary<int, bool>();
// FixedUpdate 球循环内：
bool inField = dist <= _radius && dist >= _cfg.gravityFieldMinDist;
_inside.TryGetValue(ball.GetInstanceID(), out bool was);
if (inField && !was) AudioManager.PlayFieldEnter(polarity);            // C3 / (斥力)C6 起
if (inField) AudioManager.NotifyFieldProximity(this, dist);            // 循环源 pitch = Lerp(0.9, 1.5, closeness)
if (!inField && was) AudioManager.PlayFieldExit(polarity);             // C7 或自然淡出
if (inField && dist <= _cfg.gravityFieldMinDist * 1.15f && polarity > 0)
    AudioManager.PlayFieldAbsorb();                                    // C5（0.4s 自身冷却）
```
力场循环源预算：每个力场 1 个 loop AudioSource（一关 ≤4 个力场，达标，§6 池外单列）。

---

## 11. 几何→音符映射（"路线=旋律"系统）

### 11.1 音高身份表（形状的固定音名）

| 形状 | 身份音（ToneSynth） | 度数 idx | 配方 |
|---|---|---|---|
| □ Block | C4 261.63 | 0 | Crystal 短音 |
| △ Triangle | D4 293.66 | 1 | Crystal（比 □ 高 2 度，方向改变更醒目） |
| ◆ Diamond | E4 329.63 | 2 | Crystal，且连续撞击沿 idx+1 阶梯（E→G→A→C5，3s 断档回落） |
| ◯ Portal | 入 G3 / 出 G5 | 3 | 入=Sub(G3 196Hz)，出=Crystal(G5) —— WHOOM→PLING 同族异度 |
| ◇ Bomb | A4 440 | 4 | Blip（警报 beep 同锚 A4，爆炸尾部 Energy 层叠 A5 shimmer） |
| ◎ Core | C5 523.25 | 5 | Crystal 泛音层（叠在 AI 低频 DOOM 之上） |
| ╱ Mirror | D5 587.33 | 6 | Crystal 高八度三角音；1s 内连续反射每次 +30 音分（ting-ting-ting 升调） |

### 11.2 路线=旋律（设计验证）

```
路线A：□ → △ → ◆ → ◎   听感 = C4 · D4 · E4 · C5↑  （级进上行，优雅收束）
路线B：□ → ◆ → ◯ → ◎   听感 = C4 · E4 · G4→G5 · C5↑（跳进开阔，传送门炫技）
```
- **和谐性保证**：全部身份音都在五声音阶内（§1.2），任意顺序任意叠加无刺耳音程；
- **身份音音量** 0.22×（比击打层低 1/3）——"听得见的旋律，不抢戏的伴奏"；
- 与 §12 Combo 音符同调同表：碰撞旋律与连锁旋律构成同一首曲子的两个声部。

---

## 12. Combo 音阶系统（连锁 = 音乐化）

### 12.1 触发

`AudioManager` 订阅现有 `GameEvents.OnChainChanged(int level)`（ChainSystem 已在每次可破坏命中/爆炸时广播，窗口 1.6s，零改动）。

### 12.2 音符映射（11 音循环 + 八度抬升封顶）

```
idx   = (level - 1) % 11
octave = 1 + Min(1, (level - 1) / 11)          // level 12+ 抬到 ×2（C5..C7），封顶防刺
freq  = Pentatonic[idx] * octave
```
Chain 1–6 → C D E G A C↑ D↑；Chain 7+ 继续 E↑ G↑ A↑ C6↑；Chain 12+ 整体高八度。

### 12.3 编曲分层（"越来越丰富，而不是越来越吵"）

| Chain | 叠加层 | 实现 |
|---|---|---|
| 1–3 | 纯 Crystal 音符 | vol 0.50 |
| 4–6 | + 五度 Pad（freq×1.5） | vol 0.22，dur 0.3s |
| 7–10 | + 低八度 Sub（freq÷2） | vol 0.30 —— Bass 进场 |
| 10+ | + 快速琶音 sparkle（root→+2→+4 度，30ms 间隔 Blip） | vol 0.18 |
| 20+ | 每 5 级 + Shimmer sting；MusicDirector 进入 MaxChain 态（§13） | — |

- 层内音量随级微升：总增益 ×(1 + Min(0.2, level×0.02))，**封顶 +20%**；
- Combo 占用声部 ≤2/次（音符层 + 一个伴奏层共享同一声部抢占判定）；
- 断链（level=0）：**静默处理**——不播负向音，BGM 状态 4s 后自然回落（败不惩罚耳朵）。

---

## 13. 动态音乐系统（MusicDirector）

### 13.1 Stem 资产（BGM 组，generate_music，60s 循环）

| 文件 | Prompt | BPM/调 | 用途 |
|---|---|---|---|
| BGM_Ambient | Minimal futuristic ambient electronic loop, slow evolving synth pad, sparse, clean, dark calm, seamless loop | 自由/C 小调 | 探索/瞄准基底（=现 BGM_Field 升级重生成或沿用） |
| BGM_Beat | Minimal futuristic electronic beat loop, deep bass pulse, crisp minimal hi-hats, geometric game soundtrack, no lead melody, seamless loop | 110/C 小调 | 战斗层 |
| BGM_Arp | Bright synth arpeggio loop, C minor pentatonic, steady 16th notes, energetic but clean, futuristic geometric, seamless loop | 110/C 小调 | 高连锁层 |
| BGM_Boss | Dark heavy electronic loop, deep sub bass, industrial metallic hits, tense boss fight energy, seamless loop | 100/C 小调 | Boss 替换 Beat 层 |

### 13.2 状态机（增益表，全部 1.2s 平滑过渡）

| 状态 | Ambient | Beat | Arp | 进入条件 |
|---|---|---|---|---|
| Map/Menu | 0.80 | 0 | 0 | 地图界面（MapScreen 活动） |
| Aiming（关卡就绪无活球） | 0.75 | 0.15 | 0 | StageManager 构建后、球全部回收 |
| Battle | 0.55 | 0.70 | 0 | 任一存活球 |
| HighChain | 0.55 | 0.80 | 0.60 | Chain ≥7（回落延迟 4s） |
| MaxChain | 0.50 | 0.90 | 0.90 | Chain ≥20（回落延迟 4s） |
| Boss | 0.50 | 换 BGM_Boss 0.75 | 0 | EnterBattle(Boss/Elite) |
| Victory | 全 stem 1s 淡出 → 静默窗 → 亮 Pad swell | | | OnVictory（§15.3） |
| Defeat | pitch→0.92 + 2s 内淡至 0.3 → Defeat 下行音 | | | OnAllBallsConsumed（§17） |

### 13.3 风险与备选方案

**风险**：AI 生成的多 stem 无法样本级对齐，叠加时可能相位/节拍漂移。
**对策（按优先级）**：
1. 三个战斗 stem 用同一 BPM/调性 prompt 生成，叠加时 Beat/Arp 增益 ≤0.9，漂移在低音量下可接受；
2. 若试听仍不可接受 → **方案 B**：只保留 Ambient + 单条战斗曲，状态切换用交叉淡入淡出（两源交叉，绝对安全），Arp/Boss 层用 LPF(900Hz)+pitch(0.93) 调制战斗曲替代；
3. 长期方案：BGM 三 stem 由 ToneSynth 直接程序化生成节奏轨（零对齐问题，Phase 9E 后可选研究项）。

---

## 14. Ducking 系统（碰撞优先）

### 14.1 密度 Ducking（自动，无状态机）

```
每次 PlayOneShot → _playTimes.Enqueue(now)；
Update：清出 0.5s 外旧项 → n = 窗口内播放次数
n ≤ 8   → BGM 增益 ×1.00
n ≤ 15  → ×0.85
n ≤ 25  → ×0.70
n > 25  → ×0.55          // 大连锁时 BGM 自动让路
0.3s lerp 平滑；恢复同曲线（无快慢不对称）
```

### 14.2 事件 Ducking

| 事件 | 动作 | 恢复 |
|---|---|---|
| 核心破坏（§15.3） | Master → 0.12（80ms 快降），45s 无——0.45s 静默窗 | 0.3s 升回 + 播 Victory |
| Boss Intro | BGM 全 stem ×0.5（1.2s），让位低频 swell | Intro 完自动 |
| 连锁 ≥10 瞬间 | BGM ×0.8 一次（0.5s） | 自动 |

**SFX 永远不被 Duck**——被压的只有 BGM/Ambient（这就是"SFX 优先级高于 BGM"的实现）。

---

## 15. 核心音频设计 ◎

### 15.1 Core Hit（每次受击）

`CoreBlock.TakeDamage` 新增直调：`AudioManager.PlayCoreHit(ratio)`（CoreBlock.Build 里记录 `_maxHp = hpValue`）。

```
质感层：SFX_Core_Hit_v1/v2（AI：deep metallic energy pulse, low frequency thump, resonant, 0.25s）×2 变体
音高层：C5 Crystal 泛音（§11.1）
音高曲线：pitch = Lerp(1.00, 0.82, 1 - hpRatio)   // 血越少每次 DOOM 越沉
音量曲线：vol = 0.65 + 0.2×(1-hpRatio)             // 越接近击破越重
```

### 15.2 Core Warning（警戒层状态机，AudioManager.Update 驱动）

| HP | 状态 | 脉冲内容 |
|---|---|---|
| >50% | 0 无 | — |
| ≤50% | 1 | 低频脉冲 Sub(C3 130.8Hz) 每 2.2s，vol 0.15 |
| ≤25% | 2 | + 高频警报 Blip(A4) 每 1.2s；脉冲加速到 1.2s |
| ≤10% | 3 Critical | 脉冲 0.6s；Blip 双音失谐（440+466Hz 同时，1s 周期）；BGM_Beat 叠加 pitch 0.97 |

状态切换瞬间各播一次状态提示音（状态 2 进场 = 尖锐警报双 blip；状态 3 进场 = 低鸣 + 屏幕感 sub swell 0.8s）。
实现：`_coreWarnState` 由 `PlayCoreHit(ratio)` 顺带更新；Update 内按间隔累加器触发脉冲（音符走 ToneSynth，零资产）。

### 15.3 Core Destroy（整局最强声音事件，三层 + 静默 + 胜利）

`OnVictory` 订阅内启动协程（优先级 100，占 3 声部）：

```
t=0.00s  层1 BOOM    SFX_Core_Destroy_Boom（1.0s：massive low frequency boom, deep subsonic impact）
         层2 SHATTER SFX_Core_Destroy_Shatter（0.8s：huge geometric shatter, crystal debris cascade）
         层3 TING!  SFX_Core_Destroy_Ting（1.5s：long bright crystal energy release, high shimmer sustain）
         同时 Master → 0.12（爆炸自身走 SFX_Boom 总线在 duck 前已起播，保持满响）
t=0.45s  静默窗收束（Master 仍 0.12——那 0.3~0.5s 的"突然安静"是设计的一部分，必须保留）
t=0.50s  Master 0.3s 升回；播 Victory（§17.1）
t=1.1s   MusicDirector → Victory 状态（亮 Pad swell）
```

---

## 16. Boss 音频设计（频段阵营）

**总规则：Boss = 低频阵营（30–250Hz），玩家 = 高频阵营（800Hz–6kHz），两个阵营在频谱上不打架，玩家永远听得清自己的"演奏"。**

| 事件 | 声音 | Hook |
|---|---|---|
| Boss 出现 | SFX_Boss_Intro（1.2s：deep low frequency swell, slow metallic geometric unfolding, massive dark energy rise, 1.2 seconds）+ 护盾环 10 连 Crystal 快速琶音（C5 起每 40ms 一音上行，几何展开感） | `StageManager.EnterBattle` node.type∈{Boss,Elite} |
| 玩家球击中 Boss 核心 | 走 §15.1 但 pitch ×0.85 + 叠 SFX_Boss_Doom（0.4s：extra deep sub hit, dark low boom）——比普通核心更沉 | CoreBlock（scale>1 时标志位） |
| 玩家球击中护盾 | SFX_Shield_Clank_v1/v2（0.12s：metallic shield impact clank, mid frequency, armor hit ×2）——中频金属，与方块脆击明确区分 | `CoreBlock.TakeDamage` 的 `LiveShields>0` 分支（现有代码位置） |
| Boss 核心受击（我方） | 保持高频 Crystal TING 不变——阵营对比靠 Boss 自身低频实现 | — |

---

## 17. Victory / Defeat / UI 音频

### 17.1 Victory（胜利 = 玩家旋律的终曲）

程序化琶音（ToneSynth，§15.3 时序的 t=0.5s 处）：
`C5 → E5 → G5 → C6` 每 90ms 一音（Crystal，vol 0.55），尾音 C6 同时叠 Pad(C5) 长音 0.8s + Shimmer sting（AI：bright victory shimmer sparkle, uplifting, clean, 0.5s）。
**BGM 不切新曲**——胜利琶音本身就是玩家弹出来的最后一个 Combo（音乐叙事闭环）。

### 17.2 Defeat（失败 = 下行终止式）

`OnAllBallsConsumed`（AudioManager 订阅）：
- ToneSynth 下行三音 `E4 → D4 → C4`（每 220ms，Sub+Crystal 混合，vol 0.5）；
- 低鸣垫底 SFX_Defeat_Sting（0.8s：low descending dark synth swell, somber, soft, minimal）；
- BGM pitch→0.92 且 2s 内 Beat/Arp 淡出——地图返回前保持暗淡。

### 17.3 UI 音组（UI 总线，priority 10）

| 文件 | 时长 | Prompt | 触发 |
|---|---|---|---|
| SFX_UI_Click | 0.08s | soft interface click, gentle tap | 各 Button.onClick 首行统一 `AudioManager.PlayUI("Click")` |
| SFX_UI_Confirm（v1=现 SFX_Reward）+ v2 | 0.2s | bright confirmation chime, two ascending notes | Hud 选卡确认 L395 / MapScreen 购买 L208（替换现 PlayReward） |
| SFX_UI_Cancel | 0.15s | soft descending blip, gentle decline | 取消/关闭路径 |
| SFX_UI_Cycle | 0.10s | short two-tone blip, switching sound | `Hud` 球种切换按钮（SetBallIndex/CycleBallType 路径） |
| SFX_UI_Error | 0.2s | low short buzz, denied feedback | 购买不足/非法操作 |
| SFX_UI_Hover | 0.05s | extremely subtle tick | 地图节点 hover（vol 0.2，慎用可关闭） |

---

## 18. MVP 资产清单总表

### 18.1 数量统计

| 类别 | AI 采样 | 程序化（ToneSynth，零文件） |
|---|---|---|
| Ball | 9 组 14 文件 | 分裂双音/阶梯按需 |
| Geometry | 5 组 15 文件 | 身份音 7 条缓存 |
| Mechanism | 11 组 14 文件 | 引力 pitch 由循环源承担 |
| Explosion | 3 组 3 文件 | — |
| Core/Boss | 6 组 8 文件 | 警戒脉冲/胜利琶音/失败下行 |
| UI | 6 组 6 文件 | — |
| BGM | 4 stem | — |
| **合计** | **~41 文件 + 10 现有复用 → 净新增 ~31** | **~35 条运行时生成** |

总量落在 50–80 目标区间（AI 41 + 程序化 35 ≈ 76"虚拟资产"），符合"少量采样 + 大量 Randomization/Pitch/Layering/DSP 变化"原则。

### 18.2 生成与导入规范

1. **落盘路径（强制）**：`Assets/_Game/Data/Resources/Audio/`（Resources.Load 只认 Resources 目录——已验证的项目坑）；
2. `generate_sound_effect` 的 duration_seconds 最小 1s → 生成后**裁尾**：编辑器小工具按 -40dB 阈值裁掉尾静音（10 行 AudioUtil.Trim，Phase 9B 顺手做）；
3. 导入设置：SFX 全部 `Force To Mono + Decompress On Load`；BGM `Streaming + Load In Background`；
4. 全部 Prompt 严格带 §9/§10 表中前缀后缀，风格漂移时整组重生成（不逐条打补丁）。

---

## 19. Unity/Tuanjie 接入指南

### 19.1 Hook 总表（改哪里，一行不少）

| 文件 | 方法/位置 | 调用 | 备注 |
|---|---|---|---|
| GameBootstrap | 音频初始化处 | `AudioManager.Init()`（升级版） | 现有 |
| Ball | ForceLaunch() | `Play("Ball_Launch")` | 替换现 PlayLaunch |
| Ball | OnCollisionEnter2D 穿透分支 | `Play("Ball_Pierce", ball)` | 撞可破坏且 pierceLeft>0 |
| Ball | OnCollisionEnter2D 单向墙通过分支 | `Play("OneWay_Pass")` | dot>0 穿过后 |
| BallManager | Recycle() | `Play("Ball_Return")` | 替换现 PlayRecycle |
| BallManager | SpawnChild() 返回前 | `PlayBallSplit(parent)` | shimmer+ToneSynth 双音 |
| AudioManager | HandleBallHit（事件分发升级） | 按 target 类型分发：Core→§15.1；Bomb/Triangle/Diamond/Mirror/Block→§10；无实体→撞墙 A2/A3 | 类型 switch 见 19.2 |
| AudioManager | 新订阅 OnChainChanged | Combo 音符系统（§12） | |
| AudioManager | 新订阅 OnAllBallsConsumed | Defeat（§17.2） | |
| CoreBlock | TakeDamage | `PlayCoreHit(ratio)`；护盾分支 `PlayShieldClank()` | Build 记录 _maxHp |
| StageManager | EnterBattle | `PlayBossIntro(type)`；Boss 标志传 AudioManager | |
| PortalPair | Teleport() | `PlayPortalEnter(); PlayPortalExit(delay 0.08s)` | 替换现 PlayPortal |
| DiamondAccelerator | OnBallCollide 加速生效帧 | `PlayDiamondAccel()` | 现有代码位置 |
| GravityField | FixedUpdate 球循环 | §10.4 片段 | 新增 ~10 行 |
| BombBlock | 新增 Update | §10.3 片段 | 新增 ~12 行 |
| TimeGate | Update 的 `open != _isOpen` 块 | `Play("TimeGate_Open/Close")` | 1 行 |
| Hud / MapScreen | 各 onClick 首行 | `PlayUI("Click"/"Confirm"/"Cycle"/"Error")` | 替换 PlayReward 调用点 |

### 19.2 碰撞类型分发（HandleBallHit 升级版逻辑）

```csharp
void HandleBallHit(BallCollisionEvent e)
{
    var ent = e.target != null ? e.target.GetComponent<GeometryEntity>() : null;
    if (ent == null) { Play("Ball_Bounce", ball: e.ball); return; }          // 墙
    switch (ent)
    {
        case CoreBlock core:   PlayCoreHit(core.HpRatio); break;               // §15.1
        case TriangleReflector t: Play("Triangle_Reflect", ball: e.ball); IdentityTone(1); break;
        case DiamondAccelerator d: /* 音由 Diamond 自身触发，跳过 */ break;
        case MirrorLine m:     Play("Mirror_Ting", ball: e.ball); IdentityTone(6, consecutiveBoost: true); break;
        case BombBlock b:     Play("Block_Hit", ball: e.ball); IdentityTone(4); break; // 爆炸另走 OnExplosion
        default:               Play("Block_Hit", ball: e.ball); IdentityTone(0);
                                 if (!ent.Alive) Play("Block_Shatter"); break; // 死亡叠碎裂
    }
}
```

### 19.3 Tuanjie 2022.3 API 注意事项（音频专项）

1. `AudioSource.pitch` 是**源级**属性 → PlayOneShot 前必须独占设置，这正是 14 声部池存在的原因（3 源轮换在 6 球高并发下必然互相改 pitch，Phase 8 的已知局限）；
2. `AudioClip.Create(name, samples, channels, frequency, stream)` + `SetData` 是唯一运行时合成路径，主线程 Init 期一次做完；
3. `AudioLowPassFilter.cutoffFrequency` 挂在 BGM 源 GameObject 上即可，不需要 Mixer；
4. 音频不经过 Transform → 本项目最大坑（AddComponent<RectTransform> 重建 Transform）对音频无影响，但 AudioManager 子物体一律用 `new GameObject` + `SetParent`，与现有写法一致；
5. 移动端预算：AudioSource 总数 ≤ 20（14 池 + 3 BGM stem + 力场循环 ≤2 + rush 1），PlayOneShot 无 GC 压力，ToneSynth 只在 Init 生成。

### 19.4 测试矩阵（延续 T 编号；音频不可视 → 全部结构化断言）

| 测试 | 方法 | 断言 |
|---|---|---|
| T29 ToneSynth | Init 后读 `_tones` | 11×3 配方非空、采样长度=44100×dur、C4 频率峰值≈262Hz（FFT/DFT 断言） |
| T30 限声 | 测试场 50 球同帧齐撞 | 同类 liveVoices ≤ voiceLimit；全局同时 isPlaying ≤14；无 Console 报错 |
| T31 Combo 音阶 | RegisterChain ×6 | 逐次触发的 ToneSynth 音符 idx 序列 = 0,1,2,3,4,5（日志断言） |
| T32 Ducking | 触发 OnExplosion + 30 连发 PlayOneShot | BGM 总线 gain < 0.7×基线；2s 内回升 ≥0.95×基线 |
| T33 核心警戒 | 直接调 PlayCoreHit(0.5/0.25/0.1) | warn state 1/2/3；对应脉冲间隔 2.2/1.2/0.6s（时间戳断言） |
| T34 核心破坏时序 | RaiseVictory | t0.45 Master gain ≈0.12；t0.5 后 victory 音符触发（时间戳日志） |
| T35 路线=旋律 | 测试关卡按序撞 □△◆◯◇◎ | IdentityTone idx 序列 = 0,1,2,3,4,5（音高身份表断言） |
| T36 移动端预算 | 场景全开统计 | AudioSource 总数 ≤20；AudioClip 总内存 <30MB |

---

## 20. 从 0 到完整音频系统的制作顺序（Phase 9 路线图）

| 阶段 | 内容 | 交付 | 验证 |
|---|---|---|---|
| **9A 框架**（纯代码，零资产） | AudioBus / VoicePool / SfxDef / 限声管线 / ToneSynth / 音量设置 API | AudioManager v2 替换现有（旧 10 clip 以新 id 注册进新管线，游戏声不回归） | T29 T30 + 全机关零报错 |
| **9B 球声** | 球速映射（§8）+ Ball 组 A1–A6 生成裁剪导入 + 分裂双音 | 发射/反弹（6 变体）/高速/回收/分裂/穿透全触发 | 试听 + 密集撞击无截断 |
| **9C 机关语言** | Geometry 组 B1–B5 + Mechanism 组 C1–C11 + 类型分发 + 身份音 + 炸弹/力场 hook | 十机关十种声音，听声辨物 | T35 前置 + BombBlock/GravityField 新增代码 review |
| **9D Combo 音乐化** | OnChainChanged→音符 + 分层 + 阶梯 | 连锁=旋律 | T31 |
| **9E 动态音乐** | BGM×4 stem + MusicDirector 状态机 + Ducking 密度计 | 状态编曲 + BGM 自动让路 | T32 + stem 对齐试听（不行切方案 B） |
| **9F 核心/Boss/胜负** | Core Hit/警戒/破坏三层 + Boss Intro/护盾/阵营 + Victory/Defeat 仪式 | 整局最强声音事件完成 | T33 T34 |
| **9G 调音与设置** | 全表音量微调（附录 B 基准）+ 设置 API 接 UI + 移动端预算 | 可发布音频状态 | T36 + 全流程试听（附录 A） |

**每阶段一个可回滚的完整提交**；9A 完成前不动任何资产文件。

### 20.1 风险与对策

| 风险 | 对策 |
|---|---|
| AI 采样风格漂移 | 统一前后缀 Prompt；漂移整组重生成（§18.2-4） |
| 生成时长 ≥1s 与规格不符 | AudioUtil.Trim 裁尾工具（9B 交付物） |
| BGM stem 不对齐 | §13.3 三级降级方案 |
| 高并发爆音/丢音 | 限声管线 + 饱和闸门（§6）+ T30 压测 |
| 音量失衡回归 | 附录 B 基准表 + 9G 统一调音期，不做过程中零散调音 |

---

## 附录 A：最终听觉体验验收时序（一局"演奏"的期望谱）

```
发射 WHOOSH → 三角 TING(D) → 加速 DING↑(E→G) → 方块 TAK(C) → 镜面 TING-TING(D5↑)
→ 炸弹 beep-beep-beep(A) → BOOM 三层 → 连锁 C→D→E→G→A(+Bass 进场)
→ 分裂 PLING-PLING(C→E) → 群体碎裂 TINGTINGTING → 核心 DOOM(C5，渐沉)
→ 核心 BOOM / SHATTER / TINGGGGG → 突然安静 0.45s → Victory 琶音 C5-E5-G5-C6
```
验收标准：上述每一步都能**盲听**指出发生了什么（球速/机关类型/连锁增长/核心血量状态），且整局任意时刻无爆音、无 BGM 抢戏、无相同声音连续 3 次原样重复。

## 附录 B：默认音量/音高基准速查（9G 调音用）

| 组 | vol | pitch | 备注 |
|---|---|---|---|
| Ball_Bounce | 0.40 | 1.0×speed | 防疲劳刻意压低 |
| Block_Hit | 0.62 | 1.0×speed | 主节奏声部 |
| Triangle/Mirror | 0.70/0.65 | 1.05/1.10 | 方向感强调 |
| Portal Enter/Exit | 0.60/0.60 | 0.95/1.05 | 入低出高 |
| Explode 三层 | 0.85/0.60/0.55 | 1.0 | 低频层主导 |
| Core_Hit | 0.65+0.2(1-ratio) | Lerp(1.0,0.82) | 越残越沉越响 |
| Combo 音符 | 0.50 (+层 0.18–0.30) | 五声表 | 层不过三 |
| BGM stem | 0.30–0.32 状态增益表 | 1.0 | Boss 0.93 |
| UI | 0.40 | 1.0 | — |
| 身份音 | 0.22 | 五声表 | 永远陪衬 |

## 附录 C：需求覆盖对照表

| 需求条目（二十/1–20） | 章节 |
|---|---|
| 1 风格 / 2–4 音效清单·设计·时长 / 5 AI Prompt / 6 变体 / 7 Pitch·Vol | §1 §9 §10 §18 |
| 8 Combo 音阶 / 9 机关音符对应 | §11 §12 |
| 10 动态音乐 / 12 Mixer / 13 优先级 / 14 防堆叠 | §13 §5 §6 |
| 11 AudioManager / 15 Boss / 16 Core / 17 胜负 | §4 §16 §15 §17 |
| 18 MVP 清单 / 19 Unity 接入 / 20 制作顺序 | §18 §19 §20 |
