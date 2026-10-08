using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoBreaker
{
    /// <summary>
    /// 弹球：完全弹性物理 + 速度归一化（防无限加速/减速），碰撞后发布统一事件。
    /// 视觉 = 白芯 + Additive 光晕 + 红瓣装饰 + 速度感应拖尾。
    /// P11 池化：Init 一次性构建（组件/子物体/环绕环常驻），ApplyData 每次复用重配数据与状态。
    /// </summary>
    public class Ball : MonoBehaviour
    {
        public BallData Data { get; private set; }
        public float TargetSpeed { get; set; }
        public bool IsLive { get; private set; }
        public bool IsOriginal { get; set; } = true;       // 库存球；分裂子球=false
        public bool IsEcho { get; private set; }            // 镜像球影子：伤害减半+限时回收+不再生镜像
        public bool InPhase { get; private set; }           // 相位球：相位态（无碰撞穿越中）
        public bool HasExploded { get; private set; }       // 爆炸球首撞标记
        public float LastTeleportTime { get; set; } = -999f;
        public Rigidbody2D RB { get; private set; }

        GameConfig _cfg;
        CircleCollider2D _col;
        TrailRenderer _trail;
        SpriteRenderer _core;
        SpriteRenderer _glowSr;
        Transform _petals;
        SpriteRenderer _orbitSr;                           // 引力球环绕环视觉（常驻，非引力球停用）
        Gradient _grad;                                    // 拖尾渐变（复用，仅换 keys）
        readonly List<Collider2D> _ignoredColliders = new List<Collider2D>(4);   // 穿透/单向墙忽略态账本（复用前清账）
        float _glowScale;
        Vector2 _lastDir = Vector2.up;
        bool _renorm;
        float _launchTime;
        float _lastSplit = -999f;
        float _lastLaser = -999f;                          // 棱镜激光冷却计时（每球独立）
        float _echoExpireAt;                               // 镜像影子到期时刻
        float _phaseUntil;                                 // 相位态结束时刻
        float _lastPhase = -999f;                          // 相位触发冷却
        float _lastTimeSlow = -999f;                       // 时间球触发冷却
        int _curveDir = 1;                                 // 回旋方向（发射时随机 ±1）
        int _pierceLeft;                                   // 构筑·穿透：本次发射剩余次数
        int _hitCounter;                                    // 构筑·自动分裂：碰撞计数
        int _blastCounter;                                  // 爆炸球：周期爆破计数（P9.5）
        public int Owner = -1;                              // PvP 所有权：-1=PvE；0=P1(底/玩家) 1=P2(顶/AI)
        public bool PvpDecoy;                               // PvP 分裂替身（半伤分身、可被击碎）
        public float DamageMult = 1f;                      // 伤害系数（PvP 兼容保留）
        public float PvpHp, PvpHpMax;                       // PvP 球体耐久（对撞掉血）
        public float PvpLastClash = -999f;                  // 上次对撞时刻（成对冷却去重）
        public float PvpFrozenUntil;                        // PvP 冻结停摆截止
        public float PvpSlowUntil, PvpSlowFactor = 1f;      // PvP 时缓截止/系数
        public int PvpFrostStacks;                           // PvP 冰冻值层数
        public bool PvpSpawnedHelper;                       // PvP 本局替身已生成（每局一次）
        float _pvpLaserNext;                                // PvP 激光下次蓄力发射时刻
        bool _pvpLaserCharging;                             // 蓄力协程进行中
        SpriteRenderer _symbolSr;                           // 球种符号贴片（12 球外观差异化的形状标识）
        Transform _pullBeam;                                // 引力球牵引光束（指向最近被拉对象）
        Color _pvpFrozenCoreColor;                          // 冻结前白芯原色（解冻恢复）
        bool _pvpFrozenTinted;                              // 冻结 tint 是否已应用
        bool _pvpWasFrozen;                                 // 上一帧冻结中（解冻检测）

        public void Init(BallData data, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            _cfg = cfg;
            name = "Ball";

            RB = gameObject.AddComponent<Rigidbody2D>();
            RB.bodyType = RigidbodyType2D.Kinematic;          // 待发射
            RB.gravityScale = 0f;
            RB.drag = 0f;
            RB.angularDrag = 0f;
            RB.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            RB.interpolation = RigidbodyInterpolation2D.Interpolate;
            RB.constraints = RigidbodyConstraints2D.FreezeRotation;
            RB.sleepMode = RigidbodySleepMode2D.NeverSleep;

            _col = gameObject.AddComponent<CircleCollider2D>();
            _col.sharedMaterial = bouncy;
            _col.enabled = false;                             // 瞄准阶段不参与碰撞/预测投射

            _core = NeonStyle.MakeSprite(transform, "BallCore", ProcSprites.CircleSoft(), Color.white, 20, false);

            _glowSr = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), Color.white, 19, true);

            // 红瓣装饰：按半径 1 烘焙定位，ApplyData 以 data.radius 整体缩放（池化复用零重建）
            _petals = new GameObject("Petals").transform;
            _petals.SetParent(transform, false);
            for (int i = 0; i < 6; i++)
            {
                float ang = i * 60f;
                var p = NeonStyle.MakeSprite(_petals, "Petal", ProcSprites.Triangle(), NeonStyle.Red, 21, false);
                p.transform.localRotation = Quaternion.Euler(0f, 0f, ang);
                p.transform.localPosition = (Vector2)(Quaternion.Euler(0f, 0f, ang) * Vector2.up) * 0.62f;
                p.transform.localScale = Vector3.one * 0.55f;
            }

            // 引力球环绕环：一次性建好，非引力球种停用（避免复用时中途创建）
            _orbitSr = NeonStyle.MakeSprite(transform, "OrbitRing", ProcSprites.Ring(), NeonStyle.Portal, 19, true);

            // 球种符号贴片：挂在白芯子级（随球缩放），ApplyData 按球种切换——12 球外观差异化的形状标识
            var symGo = new GameObject("KindSymbol");
            symGo.transform.SetParent(_core.transform, false);
            _symbolSr = symGo.AddComponent<SpriteRenderer>();
            _symbolSr.sortingOrder = 21;                          // 白芯(20)之上、花瓣(21)同层——符号最显眼
            _symbolSr.color = new Color(0.1f, 0.12f, 0.18f, 0.9f); // 深色符号：白芯上高对比

            // 引力球牵引光束：Additive 紫色光束，FixedUpdate 指向最近被拉对象
            var beamGo = new GameObject("PullBeam");
            beamGo.transform.SetParent(transform, false);
            var beamSr = beamGo.AddComponent<SpriteRenderer>();
            beamSr.sprite = ProcSprites.SquareFill();
            beamSr.material = NeonStyle.Additive;
            beamSr.sortingOrder = 14;                             // 白芯(20)之下——光束从球体后方射出
            beamSr.color = new Color(NeonStyle.Portal.r, NeonStyle.Portal.g, NeonStyle.Portal.b, 0.0f); // 初始透明
            _pullBeam = beamGo.transform;
            _pullBeam.gameObject.SetActive(false);                // 非引力球/无目标时隐藏

            _trail = gameObject.AddComponent<TrailRenderer>();
            _trail.time = 0.4f;
            _trail.minVertexDistance = 0.05f;
            _trail.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            _trail.material = NeonStyle.Additive;
            _trail.sortingOrder = 18;
            _trail.numCapVertices = 2;
            _trail.shadowCastingMode = ShadowCastingMode.Off;
            _trail.receiveShadows = false;
            _trail.emitting = false;

            ApplyData(data);
        }

        /// <summary>
        /// 球种数据注入（P11 池化复用核心）：视觉/碰撞/拖尾按 data 重配 + 运行状态全复位。
        /// 每次从池取出（含新建首配）后、激活前调用；IsOriginal 由 BallManager 指定。
        /// </summary>
        public void ApplyData(BallData data)
        {
            Data = data;

            // 池化安全：清掉上次飞行遗留的穿透/单向墙 IgnoreCollision 脏状态
            ClearIgnoredCollisions();

            _col.radius = data.radius;
            _col.enabled = false;
            _core.color = data.coreColor;
            _core.transform.localScale = Vector3.one * (data.radius * 2f);
            var gc = data.glowColor;
            _glowSr.color = new Color(gc.r, gc.g, gc.b, 0.55f);
            _glowScale = data.radius * 6f / 4f;                // Glow 贴图 4 单位宽
            _glowSr.transform.localScale = Vector3.one * _glowScale;
            _petals.localScale = Vector3.one * data.radius;

            _orbitSr.gameObject.SetActive(data.kind == BallKind.Gravity);
            if (data.kind == BallKind.Gravity)
            {
                var pc = NeonStyle.Portal;
                _orbitSr.color = new Color(pc.r, pc.g, pc.b, 0.5f);
                // 重做：环绕环=力场范围可视化（吸引其他球的圆形场）
                float fieldR = _cfg != null ? _cfg.gravityBallPullRadius : data.radius * 2.8f;
                _orbitSr.transform.localScale = Vector3.one * (fieldR * 0.95f);
            }

            // 引力牵引光束：非引力球种隐藏
            if (_pullBeam != null)
                _pullBeam.gameObject.SetActive(data.kind == BallKind.Gravity);

            _trail.widthMultiplier = data.radius * 1.2f;
            if (_grad == null) _grad = new Gradient();
            _grad.SetKeys(
                new[] { new GradientColorKey(data.trailColor, 0f), new GradientColorKey(data.trailColor, 1f) },
                new[] { new GradientAlphaKey(0.85f, 0f), new GradientAlphaKey(0f, 1f) });
            _trail.colorGradient = _grad;
            _trail.emitting = false;
            _trail.Clear();

            // 运行状态复位：复用即全新球
            IsLive = false;
            IsEcho = false;
            HasExploded = false;
            TargetSpeed = 0f;
            _lastDir = Vector2.up;
            _renorm = false;
            _launchTime = 0f;
            _lastSplit = -999f;
            _lastLaser = -999f;
            _echoExpireAt = 0f;
            InPhase = false;
            _phaseUntil = 0f;
            _lastPhase = -999f;
            _lastTimeSlow = -999f;
            _curveDir = Random.value < 0.5f ? 1 : -1;      // 回旋球：每次发射随机旋向
            LastTeleportTime = -999f;
            _pierceLeft = 0;
            _hitCounter = 0;
            _blastCounter = 0;
            Owner = -1;                                     // PvP 状态复位（PvPManager 在 ApplyData 之后指派）
            PvpDecoy = false;
            DamageMult = 1f;
            PvpHp = PvpHpMax = 0f;
            PvpLastClash = -999f;
            PvpFrozenUntil = 0f;
            PvpSlowUntil = 0f;
            PvpSlowFactor = 1f;
            PvpFrostStacks = 0;
            PvpSpawnedHelper = false;
            PvpUnfreezeVisual();                                // 冻结 tint 复位
            _pvpWasFrozen = false;
            _pvpLaserNext = 0f;
            _pvpLaserCharging = false;
            SetSymbol(data != null ? data.kind : BallKind.Normal);    // 球种符号切换（外观差异化）
            StopAllCoroutines();                                  // PvP 复位：清残留蓄力协程
            RB.bodyType = RigidbodyType2D.Kinematic;
            RB.velocity = Vector2.zero;
            RB.angularVelocity = 0f;
        }

        /// <summary>球种符号切换：12 球外观差异化的形状标识（深色符号印在白芯上，清晰可辨）。</summary>
        void SetSymbol(BallKind k)
        {
            if (_symbolSr == null) return;
            _symbolSr.transform.localRotation = Quaternion.identity;
            _symbolSr.transform.localScale = Vector3.one * 0.55f;  // 相对白芯（core scale=r×2→符号 world ≈0.55×2r×0.55）
            switch (k)
            {
                case BallKind.Splitter:
                    _symbolSr.sprite = ProcSprites.Triangle(); _symbolSr.enabled = true; break;
                case BallKind.Explosive:
                    _symbolSr.sprite = ProcSprites.Star4(); _symbolSr.enabled = true; break;
                case BallKind.Prism:
                    _symbolSr.sprite = ProcSprites.SquareOutline(); _symbolSr.enabled = true;
                    _symbolSr.transform.localRotation = Quaternion.Euler(0f, 0f, 45f); break;
                case BallKind.Gravity:
                    _symbolSr.sprite = ProcSprites.Ring(); _symbolSr.enabled = true; break;
                case BallKind.Pierce:
                    _symbolSr.sprite = ProcSprites.SquareFill(); _symbolSr.enabled = true;
                    _symbolSr.transform.localRotation = Quaternion.identity;
                    _symbolSr.transform.localScale = new Vector3(0.16f, 0.7f, 1f); break;   // 竖条=穿刺
                case BallKind.Curve:
                    _symbolSr.sprite = ProcSprites.HexagonOutline(); _symbolSr.enabled = true; break;
                case BallKind.Mirror:
                    _symbolSr.sprite = ProcSprites.SquareOutline(); _symbolSr.enabled = true; break;
                case BallKind.Phase:
                    _symbolSr.sprite = ProcSprites.Ring(); _symbolSr.enabled = true;
                    _symbolSr.transform.localScale = Vector3.one * 0.72f; break;            // 淡环=虚
                case BallKind.Time:
                    _symbolSr.sprite = ProcSprites.Triangle(); _symbolSr.enabled = true;
                    _symbolSr.transform.localRotation = Quaternion.Euler(0f, 0f, 180f); break; // 倒三角=沙漏
                case BallKind.Frost:
                    _symbolSr.sprite = ProcSprites.Star4(); _symbolSr.enabled = true;
                    _symbolSr.transform.localScale = Vector3.one * 0.7f; break;              // 六向=雪花
                case BallKind.Rebound:
                    _symbolSr.sprite = ProcSprites.TriangleOutline(); _symbolSr.enabled = true; break;
                default:
                    _symbolSr.enabled = false; break;             // 普通球：素球无符号
            }
        }

        public void Launch(Vector2 dir)
        {
            if (dir.sqrMagnitude < 0.0001f) dir = Vector2.up;
            dir = dir.normalized;
            ForceLaunch(dir * Mathf.Clamp(Data.speed, _cfg.minSpeed, _cfg.maxSpeed));
            GameEvents.RaiseBallLaunched(this);
        }

        /// <summary>直接以给定速度激活（库存球 Launch 与分裂子球共用）。</summary>
        public void ForceLaunch(Vector2 velocity)
        {
            float speed = Mathf.Clamp(velocity.magnitude, _cfg.minSpeed, _cfg.maxSpeed);
            Vector2 dir = speed < 0.01f ? Vector2.up : velocity / speed;
            TargetSpeed = speed;
            _lastDir = dir;
            _col.enabled = true;
            RB.bodyType = RigidbodyType2D.Dynamic;
            RB.velocity = dir * speed;
            _trail.Clear();
            _trail.emitting = true;
            IsLive = true;
            _launchTime = Time.time;
            _pierceLeft = (Data.kind == BallKind.Pierce ? 2 : 0)                  // 穿透球：每发基础 2 次
                        + (BuildState.I != null ? BuildState.I.PierceStacks : 0); // 与局内穿透强化叠加（决策②）
            _hitCounter = 0;
            _blastCounter = 0;                             // 爆炸球：每次发射重新起爆（P9.5）
            AudioManager.PlayLaunch();                    // 构筑·发射音
        }

        public void MarkNotLive() => IsLive = false;

        void OnCollisionEnter2D(Collision2D c)
        {
            _renorm = true;
            var cp = c.GetContact(0);
            var vel = RB.velocity;                          // 求解后（已反弹）的速度
            if (vel.sqrMagnitude > 0.01f) _lastDir = vel.normalized;

            var ent = c.collider != null ? c.collider.GetComponent<GeometryEntity>() : null;
            bool destructible = ent != null && !ent.indestructible && ent.Alive;

            // PvP 弹球对撞模式：球对球 → 对撞掉血（双向结算在 PvPManager.HandleClash，成对冷却去重）
            if (PvPManager.Active)
            {
                var otherBall = c.collider != null ? c.collider.GetComponent<Ball>() : null;
                if (otherBall != null && otherBall.IsLive)
                {
                    PvPManager.I.HandleClash(this, otherBall, cp.point, cp.normal, vel);
                    return;                                 // 对撞已结算——不吃 PvE 方块/机关行为（穿身由 HandleClash 接管）
                }
            }

            // 单向墙（文档十二）：正向入射 → 穿透通过；反向 → 正常反弹
            if (ent is OneWayWall oneWay)
            {
                Vector2 inDir1 = Vector2.Reflect(vel.normalized, cp.normal);
                if (Vector2.Dot(inDir1, oneWay.Forward) > 0f)
                {
                    _renorm = false;
                    IgnorePair(c.collider);
                    StartCoroutine(ReEnableCollision(c.collider, 0.5f));
                    RB.velocity = inDir1 * TargetSpeed;
                    _lastDir = inDir1;
                }
            }

            // 构筑·穿透：无视弹开，恢复入射方向继续飞（仍造成伤害）
            if (destructible && _pierceLeft > 0)
            {
                _pierceLeft--;
                _renorm = false;
                Vector2 inDir = Vector2.Reflect(vel.normalized, cp.normal);   // 反射自反性 → 反推入射
                IgnorePair(c.collider);
                StartCoroutine(ReEnableCollision(c.collider, 0.6f));
                RB.velocity = inDir * TargetSpeed;
                _lastDir = inDir;
            }

            // 相位球（强化）：冷却就绪时撞任意实体（方块/机关）→ 已造成伤害（事件照常入账）+ 幽灵穿越 0.3s——
            // 节奏型穿墙：无视护墙/护盾直捣核心。边界墙（ent==null）永不触发（防穿出场外被出界保险回收自毁）。
            if (Data.kind == BallKind.Phase && ent != null
                && Time.time - _lastPhase > _cfg.phaseCooldown)
            {
                _renorm = false;
                Vector2 inDir = Vector2.Reflect(vel.normalized, cp.normal);
                _lastPhase = Time.time;
                RB.velocity = inDir * TargetSpeed;
                _lastDir = inDir;
                EnterPhase();
            }

            float dmgOut = Data.damage + (BuildState.I != null ? BuildState.I.ExtraDamage : 0);
            if (IsEcho) dmgOut *= _cfg.mirrorEchoDamageMult;                     // 镜像影子：伤害减半
            dmgOut *= DamageMult;                                                // PvP 替身/词条系数

            GameEvents.RaiseBallCollision(new BallCollisionEvent
            {
                ball = this,
                target = c.collider,
                point = cp.point,
                normal = cp.normal,
                velocity = vel,
                damage = dmgOut,
            });

            // 构筑·命中强化（只对可破坏目标生效）
            if (destructible && BuildState.I != null)
            {
                float bonus = BuildState.I.HitSpeedBonusPerHit;      // 加速：击中目标速度+5%/层
                if (bonus > 0f)
                    TargetSpeed = Mathf.Clamp(TargetSpeed * (1f + bonus), _cfg.minSpeed, _cfg.maxSpeed);
                if (BuildState.I.Aftershock && ExplosionSystem.I != null)
                    ExplosionSystem.I.Shockwave(cp.point);           // 余震：小冲击波
                if (BuildState.I.AutoSplitEvery > 0)                // 自动分裂：每3次碰撞
                {
                    _hitCounter++;
                    if (_hitCounter >= BuildState.I.AutoSplitEvery)
                    {
                        _hitCounter = 0;
                        TrySplit(cp.point);
                    }
                }
            }

            // 反弹球（第 12 球·PvP 系）：每次碰撞弹速 +8%（速度归一化锚定 TargetSpeed，效果持续累积封顶 maxSpeed）
            if (Data.kind == BallKind.Rebound)
                TargetSpeed = Mathf.Min(TargetSpeed * 1.08f, _cfg.maxSpeed);

            // 球种行为：分裂球持续分裂 / 爆炸球周期爆破 / 棱镜球每次碰撞发射穿射激光
            if (Data.kind == BallKind.Splitter)
            {
                TrySplit(cp.point);
            }
            else if (Data.kind == BallKind.Explosive)
            {
                // P9.5：每 3 次碰撞爆破一次（首撞必爆）——不再是首撞后变白板
                _blastCounter++;
                if (_blastCounter % 3 == 1 && ExplosionSystem.I != null)
                    ExplosionSystem.I.BallBlast(cp.point);
            }
            else if (Data.kind == BallKind.Prism)
            {
                // 棱镜重做：任意碰撞 → 沿反弹方向射出穿伤激光（每球独立冷却限速）
                TryFireLaser(cp.point, vel.normalized);
            }
            else if (Data.kind == BallKind.Mirror)
            {
                // 镜像球：碰撞生成限时影子（50% 伤害岔开飞行；影子不再生影子）
                TrySpawnEcho(cp.point, vel.normalized);
            }
            else if (Data.kind == BallKind.Time)
            {
                // 时间球：撞不可破坏之物（场地墙/机关）→ 世界定格一瞬 + 时停结界（机关减速 60%/2.5s）。
                // 演出（定格/暗幕/魔法阵/三连环/大字）统一由 TimeStopCue 负责
                if (!destructible && Time.time - _lastTimeSlow > _cfg.timeSlowCooldown)
                {
                    _lastTimeSlow = Time.time;
                    bool fresh = !GeoBreaker.MechanismTime.Slowed;   // 全新结界才配得上"定格+爆闪"（续期只续法阵）
                    MechanismTime.Slow(_cfg.timeSlowFactor, _cfg.timeSlowDuration);
                    if (TimeStopCue.I != null)
                        TimeStopCue.I.Burst(cp.point, fresh);
                    else if (EffectManager.I != null)                     // 兜底：Cue 缺席时保底演出
                    {
                        EffectManager.I.PlayRing(cp.point, NeonStyle.Yellow, 0.3f, 4.5f, 0.6f);
                        if (FloatingText.I != null) FloatingText.I.Spawn(cp.point, "时停", NeonStyle.Yellow, 0.5f);
                    }
                }
            }
            else if (Data.kind == BallKind.Frost)
            {
                // 冻结球：命中可破坏目标 → 冻结（停转/停开合/Boss 停摆；不增伤，可继续撞击）
                if (destructible && ent != null)
                    ent.Freeze(_cfg.freezeDuration);
            }
        }

        /// <summary>镜像球：生成影子（复用分裂子球管线，不耗库存；IsEcho 标记走限时+减伤）。</summary>
        void TrySpawnEcho(Vector2 contactPoint, Vector2 reflectDir)
        {
            if (IsEcho) return;                                             // 影子不再生影子（防递归）
            if (PvPManager.Active) return;                 // PvP 不走 BallManager 子球（防 _liveBalls 脏账）
            if (Time.time - _lastSplit < _cfg.splitCooldownSeconds) return;
            if (BallManager.I == null || !BallManager.I.CanSpawnChild) return;
            if (reflectDir.sqrMagnitude < 0.01f) return;
            _lastSplit = Time.time;

            Vector2 echoDir = Rotate(reflectDir, -_cfg.mirrorEchoSplitAngle);   // 与本体岔开
            Vector2 spawn = contactPoint + echoDir * (Data.radius + 0.16f);
            if (SpawnBlocked(spawn, _col)) return;
            var echo = BallManager.I.SpawnChild(this, spawn, echoDir);
            if (echo != null) echo.MarkAsEcho(_cfg.mirrorEchoDuration);
        }

        /// <summary>标记为镜像影子：限时回收 + 伤害减半 + 半透明视觉（公开供 BallManager 生成后调用）。</summary>
        public void MarkAsEcho(float duration)
        {
            IsEcho = true;
            _echoExpireAt = Time.time + duration;
            name = "Echo";
            if (_core != null) _core.color = new Color(_core.color.r, _core.color.g, _core.color.b, 0.55f);
            if (_glowSr != null) _glowSr.color = new Color(_glowSr.color.r, _glowSr.color.g, _glowSr.color.b, 0.3f);
        }

        /// <summary>相位球：进入相位态（关闭碰撞 0.3s 全穿地形；结束前做出墙检测防卡墙内）。</summary>
        void EnterPhase()
        {
            InPhase = true;
            _phaseUntil = Time.time + _cfg.phaseDuration;
            _col.enabled = false;                                  // 墙/家具/目标皆无碰撞（伤害已在触发帧入账）
            if (EffectManager.I != null)
                EffectManager.I.Flash(transform.position, NeonStyle.Portal, 1.6f, 0.2f);
            if (FloatingText.I != null)
                FloatingText.I.Spawn(transform.position, "相位", NeonStyle.Portal, 0.45f);
            StartCoroutine(PhaseVisual());
        }

        System.Collections.IEnumerator PhaseVisual()
        {
            // 相位闪烁：白芯半透明高频脉动
            while (InPhase && _core != null)
            {
                float a = 0.3f + 0.25f * Mathf.Sin(Time.time * 28f);
                _core.color = new Color(_core.color.r, _core.color.g, _core.color.b, a);
                yield return null;
            }
            if (_core != null) _core.color = new Color(_core.color.r, _core.color.g, _core.color.b, 1f);
        }

        /// <summary>相位到期实体化：若仍卡在不可破坏体内则顺延 0.1s（防卡墙）。</summary>
        void TrySolidify()
        {
            var hits = Physics2D.OverlapCircleAll(transform.position, Data.radius * 0.95f);
            foreach (var h in hits)
            {
                if (h == _col || h.isTrigger) continue;
                _phaseUntil = Time.time + 0.1f;                     // 还在障碍内：顺延相位
                return;
            }
            InPhase = false;
            _phaseUntil = 0f;
            _col.enabled = true;
            if (_core != null) _core.color = new Color(_core.color.r, _core.color.g, _core.color.b, 1f);
        }

        /// <summary>棱镜激光：从撞击点沿反弹方向穿射——路径上所有可破坏实体受伤，墙/家具截断。</summary>
        void TryFireLaser(Vector2 contactPoint, Vector2 dir)
        {
            if (Time.time - _lastLaser < _cfg.prismLaserCooldown) return;   // 高频爽感但限速
            if (dir.sqrMagnitude < 0.01f) return;
            _lastLaser = Time.time;
            LaserSystem.Fire(contactPoint, dir, _cfg, _col);
        }

        // ---------------- PvP 弹球对撞模式（v2） ----------------

        /// <summary>PvP 时缓（时间球对撞）：限速 slowFactor，持续 seconds。</summary>
        public void PvpSlow(float slowFactor, float seconds)
        {
            PvpSlowFactor = slowFactor;
            PvpSlowUntil = Time.time + seconds;
        }

        /// <summary>PvP 冻结（冻结球叠满）：完全停摆+冰蓝结冰视觉，持续 seconds；解冻恢复原色。</summary>
        public void PvpFreeze(float seconds)
        {
            PvpFrozenUntil = Time.time + seconds;
            if (!_pvpFrozenTinted)
            {
                _pvpFrozenCoreColor = _core.color;
                _pvpFrozenTinted = true;
                _core.color = new Color(0.55f, 0.8f, 1f, 1f);       // 冰蓝
                _glowSr.color = new Color(0.55f, 0.8f, 1f, 0.5f);
                if (EffectManager.I != null)
                {
                    EffectManager.I.SpawnSparks(transform.position, Vector2.up, 12, 1.8f,
                        new Color(0.65f, 0.88f, 1f));
                    EffectManager.I.Flash(transform.position, new Color(0.65f, 0.88f, 1f), 1.8f, 0.25f);
                }
            }
        }

        /// <summary>PvP 解冻恢复原色（FixedUpdate 检测冻结到期调用）。</summary>
        void PvpUnfreezeVisual()
        {
            if (!_pvpFrozenTinted) return;
            _pvpFrozenTinted = false;
            _core.color = _pvpFrozenCoreColor;
        }

        /// <summary>PvP 穿身（穿透球/相位闪避）：无视对方球继续飞行，短时忽略其碰撞。</summary>
        public void PvpPierceThrough(Collider2D other, Vector2 inDir)
        {
            _renorm = false;
            IgnorePair(other);
            StartCoroutine(ReEnableCollision(other, 0.6f));
            RB.velocity = inDir * TargetSpeed;
            _lastDir = inDir;
        }

        /// <summary>PvP 球体放大（对撞模式）：碰撞体+白芯+光晕随半径放大，红瓣保持原版大小。</summary>
        public void SetPvpRadius(float r)
        {
            if (_col == null || r <= 0f) return;
            _col.radius = r;
            _core.transform.localScale = Vector3.one * (r * 2f);   // 白芯同步放大
            _glowSr.transform.localScale = Vector3.one * (r * 1.5f); // 光晕随球
            // 红瓣保持原版大小（用户要求：三角不放大）
        }

        /// <summary>穿透/单向墙忽略：记账（池化复用前 ClearIgnoredCollisions 清账防脏忽略态）。</summary>
        void IgnorePair(Collider2D other)
        {
            Physics2D.IgnoreCollision(_col, other, true);
            if (!_ignoredColliders.Contains(other)) _ignoredColliders.Add(other);
        }

        System.Collections.IEnumerator ReEnableCollision(Collider2D other, float delay)
        {
            yield return new WaitForSeconds(delay);
            _ignoredColliders.Remove(other);
            if (other != null && _col != null)
                Physics2D.IgnoreCollision(_col, other, false);
        }

        /// <summary>清掉所有忽略态（球回收复用前调用）。</summary>
        void ClearIgnoredCollisions()
        {
            for (int i = 0; i < _ignoredColliders.Count; i++)
            {
                var c = _ignoredColliders[i];
                if (c != null && _col != null) Physics2D.IgnoreCollision(_col, c, false);
            }
            _ignoredColliders.Clear();
        }

        void TrySplit(Vector2 contactPoint)
        {
            if (PvPManager.Active) return;                 // PvP 分裂改走替身管线（PlayerCore→SpawnDecoys），不走 BallManager 子球
            if (Time.time - _lastSplit < _cfg.splitCooldownSeconds) return;
            if (BallManager.I == null || !BallManager.I.CanSpawnChild) return;
            var dir = (Vector2)RB.velocity;
            if (dir.sqrMagnitude < 0.01f) return;
            dir.Normalize();
            _lastSplit = Time.time;

            float side = Random.value < 0.5f ? 1f : -1f;
            Vector2 childDir = Rotate(dir, Data.splitAngleDeg * side);
            Vector2 spawn = contactPoint + childDir * (Data.radius + 0.14f);
            if (SpawnBlocked(spawn, _col))
            {
                childDir = Rotate(dir, -Data.splitAngleDeg * side);
                spawn = contactPoint + childDir * (Data.radius + 0.14f);
                if (SpawnBlocked(spawn, _col)) return;   // 两侧都被挡：放弃本次分裂
            }
            BallManager.I.SpawnChild(this, spawn, childDir);
        }

        static bool SpawnBlocked(Vector2 p, Collider2D self)
        {
            var hits = Physics2D.OverlapCircleAll(p, 0.2f);
            for (int i = 0; i < hits.Length; i++)
                if (hits[i] != self && !hits[i].isTrigger) return true;
            return false;
        }

        static Vector2 Rotate(Vector2 v, float deg)
        {
            float r = deg * Mathf.Deg2Rad;
            float c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        void FixedUpdate()
        {
            if (!IsLive) return;

            // PvP 冻结：完全停摆（活靶——冰冻球"控制"的对撞价值）
            if (PvpFrozenUntil > Time.time)
            {
                RB.velocity = Vector2.zero;
                _pvpWasFrozen = true;
                return;
            }
            if (_pvpWasFrozen && _pvpFrozenTinted)
            {
                PvpUnfreezeVisual();                               // 解冻恢复原色
                _pvpWasFrozen = false;
            }

            // PvP 时缓：限速（时间球"节奏控制"）
            if (PvpSlowUntil > Time.time)
            {
                float lim = TargetSpeed * PvpSlowFactor;
                if (RB.velocity.magnitude > lim)
                    RB.velocity = RB.velocity.normalized * lim;
            }

            // 镜像影子到期自动回收（不扣库存）
            if (IsEcho && Time.time > _echoExpireAt)
            {
                if (PvPManager.Active) PvPManager.I.EliminateBall(this);   // PvP 替身到期=消散
                else if (BallManager.I != null) BallManager.I.Recycle(this);
                return;
            }

            // 相位到期实体化（含出墙保护）
            if (InPhase && Time.time >= _phaseUntil)
                TrySolidify();

            if (_renorm)
            {
                _renorm = false;
                Renormalize();
            }

            var v = RB.velocity;
            float sp = v.magnitude;
            if (sp < 0.5f)                                     // 防卡死
            {
                RB.velocity = _lastDir * TargetSpeed;
                return;
            }
            if (sp < _cfg.minSpeed * 0.95f || sp > _cfg.maxSpeed * 1.05f)
                RB.velocity = v.normalized * Mathf.Clamp(sp, _cfg.minSpeed, _cfg.maxSpeed);

            // 回旋球（重做）：持续旋向偏转——每物理帧旋转速度方向，弹道成肉眼可见的弧线
            if (Data.kind == BallKind.Curve)
            {
                var cv = RB.velocity;
                if (cv.sqrMagnitude > 0.01f)
                {
                    float w = _cfg.curveRateDegPerSec * _curveDir * Mathf.Deg2Rad * Time.fixedDeltaTime;
                    float c = Mathf.Cos(w), s = Mathf.Sin(w);
                    RB.velocity = new Vector2(cv.x * c - cv.y * s, cv.x * s + cv.y * c);
                    _lastDir = RB.velocity.normalized;
                }
            }

            // 激光球（PvP 专属）：固定周期蓄力→沿当前方向放大射出激光
            if (Data.kind == BallKind.Prism && PvPManager.Active && !_pvpLaserCharging &&
                Time.time >= _pvpLaserNext)
            {
                _pvpLaserNext = Time.time + _cfg.pvpLaserInterval;
                StartCoroutine(PvpLaserCharge());
            }

            // 引力球（重做）：球体产生圆形引力场——吸引带 RB 的其他球/浮块（弹道弯曲可视化）
            // （任何动态机关/后续物理化对象同样适用；聚编队→爆炸球连锁的聚怪价值）
            if (Data.kind == BallKind.Gravity && (PvPManager.Active || BallManager.I != null))
            {
                Vector2 c = transform.position;
                var others = PvPManager.Active ? PvPManager.I.GetLiveBalls() : BallManager.I.GetLiveBalls();
                for (int i = 0; i < others.Count; i++)
                {
                    var b = others[i];
                    if (b == this || b == null || !b.IsLive || b.RB == null) continue;
                    Vector2 d = c - (Vector2)b.transform.position;
                    float dist = d.magnitude;
                    if (dist > 0.15f && dist < _cfg.gravityBallPullRadius)
                    {
                        float falloff = 1f - dist / _cfg.gravityBallPullRadius;
                        b.RB.AddForce(d / dist * (_cfg.gravityBallPullStrength * 1.4f * falloff), ForceMode2D.Force);
                    }
                }
                var floats = FloatBlock.All;
                for (int i = 0; i < floats.Count; i++)
                {
                    var f = floats[i];
                    if (f == null || !f.Alive || f.Body == null) continue;
                    Vector2 d = c - (Vector2)f.transform.position;
                    float dist = d.magnitude;
                    if (dist > 0.2f && dist < _cfg.gravityBallPullRadius)
                    {
                        float falloff = 1f - dist / _cfg.gravityBallPullRadius;
                        f.Body.AddForce(d / dist * (_cfg.gravityBallPullStrength * 2f * falloff), ForceMode2D.Force);
                    }
                }

                // 引力光束：指向场内最近的可拉对象（球或浮块）——"谁被拽"一目了然
                if (_pullBeam != null)
                {
                    Transform nearest = null;
                    float nd = float.MaxValue;
                    foreach (var b in others)
                    {
                        if (b == this || b == null || !b.IsLive) continue;
                        float dd = Vector2.Distance(c, b.transform.position);
                        if (dd < nd && dd > 0.15f && dd < _cfg.gravityBallPullRadius) { nd = dd; nearest = b.transform; }
                    }
                    foreach (var f in floats)
                    {
                        if (f == null || !f.Alive) continue;
                        float dd = Vector2.Distance(c, f.transform.position);
                        if (dd < nd && dd > 0.2f && dd < _cfg.gravityBallPullRadius) { nd = dd; nearest = f.transform; }
                    }
                    if (nearest != null)
                    {
                        _pullBeam.gameObject.SetActive(true);
                        Vector2 mid = (c + (Vector2)nearest.position) * 0.5f;
                        Vector2 dir = (Vector2)nearest.position - c;
                        float dist = dir.magnitude;
                        _pullBeam.position = new Vector3(mid.x, mid.y, 0f);
                        _pullBeam.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
                        float pulse = 1f + 0.2f * Mathf.Sin(Time.time * 12f);
                        _pullBeam.localScale = new Vector3(dist, 0.12f * pulse, 1f);
                    }
                    else
                    {
                        _pullBeam.gameObject.SetActive(false);
                    }
                }
            }
            else if (_pullBeam != null && _pullBeam.gameObject.activeSelf)
            {
                _pullBeam.gameObject.SetActive(false);                 // 非引力球：隐藏光束
            }

            // 拖尾长度随速度：速度越快拖尾越长
            float k = Mathf.InverseLerp(_cfg.minSpeed, _cfg.maxSpeed, sp);
            _trail.time = Mathf.Lerp(0.25f, 0.7f, k);

            // 超时安全阀：单次飞行最长时限，自动回收（PvP：回合计时兜底，飞行上限=回合时长）
            float maxFlight = PvPManager.Active ? PvPManager.I.Timer + 1f : _cfg.maxFlightSeconds;
            if (Time.time - _launchTime > maxFlight)
            {
                if (PvPManager.Active) PvPManager.I.EliminateBall(this);   // PvP：出界/超时按击碎处理
                else if (BallManager.I != null) BallManager.I.Recycle(this);
                return;
            }

            // 场外保险：任何原因导致的出界（机关异常/退化接触）直接回收
            var pos = transform.position;
            if (Mathf.Abs(pos.x) > _cfg.fieldWidth * 0.5f + 2f ||
                Mathf.Abs(pos.y) > _cfg.fieldHeight * 0.5f + 2f)
            {
                if (PvPManager.Active) PvPManager.I.EliminateBall(this);   // PvP：出界=球碎
                else if (BallManager.I != null) BallManager.I.Recycle(this);
                return;
            }
        }

        // ---------------- PvP 激光球：周期蓄力→放大射出 ----------------

        /// <summary>蓄力 0.4s（白芯收缩变亮+聚能环收缩）→ 沿当前方向爆射激光。</summary>
        System.Collections.IEnumerator PvpLaserCharge()
        {
            _pvpLaserCharging = true;
            var baseScale = _core.transform.localScale;
            var baseColor = _core.color;
            var glowBase = _glowSr.transform.localScale;

            // 蓄能环从大到小收拢
            var ringGo = new GameObject("PvpChargeRing");
            ringGo.transform.SetParent(transform, false);
            var ringSr = ringGo.AddComponent<SpriteRenderer>();
            ringSr.sprite = ProcSprites.Ring();
            ringSr.material = NeonStyle.Additive;
            ringSr.sortingOrder = 17;
            ringSr.color = new Color(NeonStyle.Portal.r, NeonStyle.Portal.g, NeonStyle.Portal.b, 0.9f);
            var ringT = ringGo.transform;

            float t = 0f;
            while (t < _cfg.pvpLaserChargeSeconds && IsLive && PvPManager.Active)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / _cfg.pvpLaserChargeSeconds);
                _core.transform.localScale = baseScale * (1f - 0.35f * k);            // 白芯收缩聚能
                _core.color = Color.Lerp(baseColor, Color.white, k);                  // 变亮
                ringT.localScale = Vector3.one * (1.8f * (1f - k) + 0.35f);           // 聚能环收拢
                var rc = ringSr.color; rc.a = 0.9f * (0.4f + 0.6f * k);
                ringSr.color = rc;
                yield return null;
            }
            if (ringGo != null) Destroy(ringGo);

            if (!IsLive || !PvPManager.Active)                                        // 蓄力途中球碎/回收
            {
                _pvpLaserCharging = false;
                yield break;
            }

            // 放大射出：沿当前方向爆射激光 + 爆闪
            LaserSystem.Fire(transform.position, _lastDir, _cfg, _col, Owner, _cfg.pvpLaserDamagePvp);
            if (EffectManager.I != null)
            {
                EffectManager.I.Flash(transform.position, NeonStyle.Portal, 3.2f, 0.3f);
                EffectManager.I.Shake(0.15f, 0.2f);
            }
            _core.transform.localScale = baseScale;
            _core.color = baseColor;
            _pvpLaserCharging = false;
        }

        /// <summary>引力球目标查询（公开供测试）：半径内最近的可破坏几何体（含核心/护盾/炸弹）。</summary>
        public GeometryEntity NearestDestructible(float radius)
        {
            GeometryEntity best = null;
            float bestD = radius;
            foreach (var e in GeometryEntity.All)
            {
                if (e == null || !e.Alive || e.indestructible) continue;
                float d = Vector2.Distance(transform.position, e.transform.position);
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        void Renormalize()
        {
            var v = RB.velocity;
            RB.velocity = (v.sqrMagnitude < 0.01f)
                ? _lastDir * TargetSpeed
                : v.normalized * TargetSpeed;
        }

        void Update()
        {
            _petals.Rotate(0f, 0f, (IsLive ? 220f : 60f) * Time.deltaTime);
            if (_orbitSr != null) _orbitSr.transform.Rotate(0f, 0f, -140f * Time.deltaTime, Space.Self);
            if (!IsLive)
            {
                float pulse = 1f + 0.15f * Mathf.Sin(Time.time * 3f);
                _glowSr.transform.localScale = Vector3.one * (_glowScale * pulse);
            }
        }
    }
}
