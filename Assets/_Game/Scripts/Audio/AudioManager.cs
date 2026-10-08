using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>音频总线（文档 §5）：基准音量 + 长驻源登记（BGM/力场循环等）。</summary>
    public class AudioBus
    {
        public float baseVol = 1f;
        public readonly List<AudioSource> loops = new List<AudioSource>();
    }

    /// <summary>声部池单元（文档 §6）：轮换 + 优先级抢占的最小单位。</summary>
    public class Voice
    {
        public AudioSource src;
        public string defId;
        public int priority;
        public float startedAt;                 // unscaled
        public float busyUntil;                 // unscaled
        public bool IsBusy => Time.unscaledTime < busyUntil;
    }

    /// <summary>音频定义（文档 §4）：一条 SFX = 变体组 + 总线 + 限声参数。</summary>
    public class SfxDef
    {
        public string id;
        public AudioClip[] variants;
        public string bus;
        public int priority = 20;
        public int voiceLimit = 3;              // 同类最大并发
        public float cooldown = 0.04f;          // 同类最小触发间隔（含同帧去重）
        public float vol = 0.65f;               // 基准音量（附录 B）
        public float pitch = 1f;                // 基准 pitch（运行时叠随机/球速）
        public bool speedScaled;               // 叠加球速映射（文档 §8）
    }

    /// <summary>
    /// 音频管理器 v2（文档 §3-§8，Phase 9A）：
    /// 代码总线（等效 AudioMixer）+ 14 声部池（优先级抢占）+ SfxDef 限声管线
    /// + ToneSynth 程序化五声音阶 + 音量设置 API（PlayerPrefs 持久化）。
    /// 所有 GameObject 一律不持有 AudioSource，只调本类静态入口。
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        public static AudioManager I { get; private set; }

        public const int PoolSize = 14;

        GameConfig _cfg;
        readonly Dictionary<string, SfxDef> _defs = new Dictionary<string, SfxDef>();
        readonly Dictionary<string, AudioBus> _buses = new Dictionary<string, AudioBus>();
        readonly List<Voice> _pool = new List<Voice>();
        readonly List<AudioSource> _allSources = new List<AudioSource>();
        readonly List<AudioLowPassFilter> _allLpfs = new List<AudioLowPassFilter>();
        readonly Dictionary<string, float> _lastPlay = new Dictionary<string, float>();
        readonly Dictionary<string, int> _execCount = new Dictionary<string, int>();   // 每类累计成功播放数（测试）
        readonly Queue<float> _playTimes = new Queue<float>();      // Ducking 密度计（9E 启用）

        float _userMaster = 1f, _userBgm = 1f, _userSfx = 1f, _userUi = 1f;
        int _windowCount;

        public void Init(GameConfig cfg)
        {
            I = this;
            _cfg = cfg;

            // 总线（文档 §5.1）：Master 下挂 7 组
            MakeBus("BGM", 0.25f);
            MakeBus("Ambient", 0.30f);
            MakeBus("Ball", 1f);
            MakeBus("Geom", 1f);
            MakeBus("Mech", 1f);
            MakeBus("Boom", 1f);
            MakeBus("UI", 1f);

            // 声部池：14 源轮换（pitch 是源级属性 → PlayOneShot 必须独占声部）
            var voicesRoot = new GameObject("Voices");
            voicesRoot.transform.SetParent(transform, false);
            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject($"V{i}");
                go.transform.SetParent(voicesRoot.transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 0f;           // 2D
                var lpf = go.AddComponent<AudioLowPassFilter>();
                lpf.cutoffFrequency = 22000f;     // 中性（9F 核心爆炸压暗用）
                _allSources.Add(src);
                _allLpfs.Add(lpf);
                _pool.Add(new Voice { src = src });
            }

            LoadSettings();
            RegisterLegacyDefs();
            WarmToneSynth();
            StartBgm();

            GameEvents.OnBallCollision += HandleBallHit;
            GameEvents.OnExplosion += HandleExplosion;
            GameEvents.OnVictory += HandleVictory;
        }

        void OnDestroy()
        {
            GameEvents.OnBallCollision -= HandleBallHit;
            GameEvents.OnExplosion -= HandleExplosion;
            GameEvents.OnVictory -= HandleVictory;
            if (I == this) I = null;
        }

        // ---------------- 总线 ----------------

        void MakeBus(string name, float baseVol)
            => _buses[name] = new AudioBus { baseVol = baseVol };

        /// <summary>总线增益 = Master × 用户设置 × 总线基准。</summary>
        float BusGain(string bus)
        {
            float g = _userMaster;
            if (bus == "BGM" || bus == "Ambient") g *= _userBgm;
            else if (bus == "UI") g *= _userUi;
            else g *= _userSfx;
            if (_buses.TryGetValue(bus, out var b)) g *= b.baseVol;
            return g;
        }

        /// <summary>长驻源（BGM/循环）登记：音量随总线/设置实时生效。</summary>
        AudioSource RegisterLoop(string bus)
        {
            var go = new GameObject(bus + "_Loop");
            go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            var lpf = go.AddComponent<AudioLowPassFilter>();
            lpf.cutoffFrequency = 22000f;
            _buses[bus].loops.Add(src);
            _allSources.Add(src);
            _allLpfs.Add(lpf);
            src.volume = BusGain(bus);
            return src;
        }

        void ApplyLoopVolumes()
        {
            foreach (var kv in _buses)
            {
                float gain = BusGain(kv.Key);
                foreach (var src in kv.Value.loops) src.volume = gain;
            }
        }

        void StartBgm()
        {
            var src = RegisterLoop("BGM");
            src.clip = Resources.Load<AudioClip>("Audio/BGM_Field");
            src.loop = true;
            if (src.clip != null) src.Play();
        }

        // ---------------- 音量设置（文档 §5.3，PlayerPrefs 持久化） ----------------

        static float Taper(float v) => v * v;    // 二次曲线近似人耳响度（0.5 → -12dB）

        void LoadSettings()
        {
            _userMaster = Taper(PlayerPrefs.GetFloat("GB_Audio_Master", 1f));
            _userBgm = Taper(PlayerPrefs.GetFloat("GB_Audio_BGM", 1f));
            _userSfx = Taper(PlayerPrefs.GetFloat("GB_Audio_SFX", 1f));
            _userUi = Taper(PlayerPrefs.GetFloat("GB_Audio_UI", 1f));
            ApplyLoopVolumes();
        }

        public static void SetMasterVolume(float v) { if (I == null) return; PlayerPrefs.SetFloat("GB_Audio_Master", v); I._userMaster = Taper(v); I.ApplyLoopVolumes(); }
        public static void SetBgmVolume(float v) { if (I == null) return; PlayerPrefs.SetFloat("GB_Audio_BGM", v); I._userBgm = Taper(v); I.ApplyLoopVolumes(); }
        public static void SetSfxVolume(float v) { if (I == null) return; PlayerPrefs.SetFloat("GB_Audio_SFX", v); I._userSfx = Taper(v); }
        public static void SetUiVolume(float v) { if (I == null) return; PlayerPrefs.SetFloat("GB_Audio_UI", v); I._userUi = Taper(v); }

        /// <summary>全局低通（9F 核心爆炸压暗用；22000=中性）。</summary>
        public static void SetMasterLpf(float cutoff)
        {
            if (I == null) return;
            for (int i = 0; i < I._allLpfs.Count; i++) I._allLpfs[i].cutoffFrequency = cutoff;
        }

        // ---------------- SfxDef 注册（Phase 9A：旧 10 clip 迁入新管线） ----------------

        void AddDef(string id, string file, string bus, int priority, int voiceLimit,
            float cooldown, float vol, float pitch, bool speedScaled)
        {
            var clip = Resources.Load<AudioClip>("Audio/" + file);
            if (clip == null)
            {
                Debug.LogWarning($"[Audio] 缺少音频资产: {file}");
                return;
            }
            _defs[id] = new SfxDef
            {
                id = id,
                variants = new[] { clip },
                bus = bus,
                priority = priority,
                voiceLimit = voiceLimit,
                cooldown = cooldown,
                vol = vol,
                pitch = pitch,
                speedScaled = speedScaled,
            };
        }

        void RegisterLegacyDefs()
        {
            // 球（文档 §9，9B 补变体组）
            AddDef("Ball_Launch", "SFX_Launch", "Ball", 20, 3, 0.05f, 0.60f, 1f, false);
            AddDef("Ball_Bounce", "SFX_Bounce", "Ball", 20, 4, 0.06f, 0.40f, 1f, true);
            AddDef("Ball_Return", "SFX_Recycle", "Ball", 20, 3, 0.10f, 0.50f, 1f, false);
            // 几何（9C 补类型分发与身份音）
            AddDef("Block_Hit", "SFX_HitBlock", "Geom", 20, 4, 0.03f, 0.62f, 1f, true);
            AddDef("Core_Hit", "SFX_HitCore", "Geom", 50, 3, 0.06f, 0.65f, 1f, true);
            AddDef("Mech_Generic", "SFX_HitMech", "Mech", 20, 3, 0.05f, 0.55f, 1f, true);
            AddDef("Portal_Exit", "SFX_Portal", "Mech", 60, 3, 0.10f, 0.60f, 1f, false);
            // 爆炸（9B 拆三层）
            AddDef("Explode_Low", "SFX_Explode", "Boom", 80, 2, 0.12f, 0.85f, 1f, false);
            // UI
            AddDef("UI_Confirm", "SFX_Reward", "UI", 10, 3, 0.05f, 0.80f, 1f, false);
        }

        /// <summary>ToneSynth 预热：Combo 11 音 ×3 音色 + 机关身份音（文档 §7.4/§11.1）。</summary>
        void WarmToneSynth()
        {
            for (int i = 0; i < ToneSynth.Pentatonic.Length; i++)
            {
                ToneSynth.Get(ToneSynth.Flavor.Crystal, i);
                ToneSynth.Get(ToneSynth.Flavor.Sub, i);
                ToneSynth.Get(ToneSynth.Flavor.Pad, i);
            }
            ToneSynth.GetByFreq(ToneSynth.Flavor.Sub, 196f);     // 传送门入 WHOOM（G3）
        }

        // ---------------- 播放管线（文档 §4/§6/§8） ----------------

        /// <summary>统一播放：冷却 → 限声/抢占 → 随机化 → 球速映射 → PlayOneShot。</summary>
        public static void Play(string id, float volScale = 1f, float pitchScale = 1f, Ball ball = null)
        {
            var m = I;
            if (m == null) return;
            if (!m._defs.TryGetValue(id, out var def) || def.variants == null || def.variants.Length == 0) return;
            float now = Time.unscaledTime;

            // 饱和闸门：0.5s 窗口 >40 次播放时丢弃低优先级（50 球齐撞保底）
            if (m._windowCount > 40 && def.priority <= 20) return;

            // 同类冷却（同帧第二次触发在此被去重）
            if (m._lastPlay.TryGetValue(id, out float last) && now - last < def.cooldown) return;

            // 球速映射（文档 §8）：k=0..1 → pitch 0.92→1.28 / vol 0.45→1.0
            float pitchMul = 1f, volMul = 1f;
            if (ball != null && def.speedScaled && m._cfg != null && ball.RB != null)
            {
                float k = Mathf.InverseLerp(m._cfg.minSpeed, m._cfg.maxSpeed, ball.RB.velocity.magnitude);
                pitchMul = 0.92f + 0.36f * k;
                volMul = 0.45f + 0.55f * k;
            }

            var v = m.AcquireVoice(def);
            if (v == null) return;                            // 抢占失败 → 丢弃（防堆叠）

            var clip = def.variants[Random.Range(0, def.variants.Length)];
            v.src.pitch = def.pitch * Random.Range(0.95f, 1.05f) * pitchScale * pitchMul;
            v.src.volume = m.BusGain(def.bus) * def.vol * Random.Range(0.85f, 1f) * volScale * volMul;
            v.src.PlayOneShot(clip);
            v.defId = id;
            v.priority = def.priority;
            v.startedAt = now;
            v.busyUntil = now + clip.length / Mathf.Max(0.1f, v.src.pitch);
            m._lastPlay[id] = now;
            m._execCount.TryGetValue(id, out int c);
            m._execCount[id] = c + 1;
            m._playTimes.Enqueue(now);                         // Ducking 密度计（9E 启用）
        }

        /// <summary>声部获取：①同类超限盖旧 → ②空闲 → ③全局抢占更低优先级。</summary>
        Voice AcquireVoice(SfxDef def)
        {
            int live = 0;
            Voice oldestSame = null;
            for (int i = 0; i < _pool.Count; i++)
            {
                var v = _pool[i];
                if (!v.IsBusy || v.defId != def.id) continue;
                live++;
                if (oldestSame == null || v.startedAt < oldestSame.startedAt) oldestSame = v;
            }
            if (live >= def.voiceLimit && oldestSame != null)
            {
                oldestSame.src.Stop();
                return oldestSame;                             // 盖旧不叠新
            }
            for (int i = 0; i < _pool.Count; i++)
                if (!_pool[i].IsBusy) return _pool[i];
            Voice victim = null;
            for (int i = 0; i < _pool.Count; i++)
            {
                var v = _pool[i];
                if (v.priority >= def.priority) continue;
                if (victim == null || v.priority < victim.priority ||
                    (v.priority == victim.priority && v.startedAt < victim.startedAt))
                    victim = v;
            }
            if (victim != null) victim.src.Stop();
            return victim;
        }

        void Update()
        {
            float now = Time.unscaledTime;
            while (_playTimes.Count > 0 && now - _playTimes.Peek() > 0.5f)
                _playTimes.Dequeue();
            _windowCount = _playTimes.Count;
        }

        // ---------------- 触发接线 ----------------

        void HandleBallHit(BallCollisionEvent e)
        {
            var ent = e.target != null ? e.target.GetComponent<GeometryEntity>() : null;
            if (ent != null && !ent.indestructible)
                Play(ent is CoreBlock ? "Core_Hit" : "Block_Hit", ball: e.ball);
            else
                Play("Ball_Bounce", ball: e.ball);             // 墙/不可破坏机关：9C 按类型细分
        }

        void HandleExplosion(Vector2 center) => Play("Explode_Low");

        void HandleVictory() => Play("UI_Confirm");            // 9F 换核心破坏全时序

        // ---------------- 旧静态入口（保持调用点零改动；修复 Phase 8 静态 clip 从未赋值的哑音） ----------------

        /// <summary>发射音（Ball.ForceLaunch 调用）。</summary>
        public static void PlayLaunch() => Play("Ball_Launch");

        /// <summary>回收音（BallManager.Recycle 调用）。</summary>
        public static void PlayRecycle() => Play("Ball_Return");

        /// <summary>奖励确认音（Hud 选卡 / MapScreen 购买调用）。</summary>
        public static void PlayReward() => Play("UI_Confirm");

        /// <summary>传送门音（PortalPair.Teleport 调用；9C 拆入/出双音）。</summary>
        public static void PlayPortal() => Play("Portal_Exit");

        // ---------------- 诊断（测试 T29/T30 用） ----------------

        public static int DefCount => I != null ? I._defs.Count : 0;
        public static int WindowPlayCount => I != null ? I._windowCount : 0;

        /// <summary>每类累计成功播放次数（测试 T30 用）。</summary>
        public static int PlayCountOf(string id)
            => I != null && I._execCount.TryGetValue(id, out int n) ? n : 0;

        public static int LiveVoicesOf(string id)
        {
            if (I == null) return -1;
            int n = 0;
            for (int i = 0; i < I._pool.Count; i++)
                if (I._pool[i].IsBusy && I._pool[i].defId == id) n++;
            return n;
        }

        public static int TotalLiveVoices
        {
            get
            {
                if (I == null) return -1;
                int n = 0;
                for (int i = 0; i < I._pool.Count; i++)
                    if (I._pool[i].IsBusy) n++;
                return n;
            }
        }
    }
}
