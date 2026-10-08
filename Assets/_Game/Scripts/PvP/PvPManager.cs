using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// PvP 对局中枢（弹球对撞模式 v2）：双方各发射一次，球在场内对撞掉血——
    /// 先碎者负、超时双活平局、三局两胜。撞墙/障碍零伤（只有球对球结算）。
    /// 回合流程：Aim（双方瞄准发射，可迟到）→ Live（对撞）→ Between（回合幕）→ 下一局/终局。
    /// </summary>
    public class PvPManager : MonoBehaviour
    {
        public static PvPManager I { get; private set; }
        public static bool Active => I != null;

        public const int P1 = 0, P2 = 1;
        public const float DRAW = -1f;

        public enum Phase { Aim, Live, Between, MatchOver }

        GameConfig _cfg;
        PhysicsMaterial2D _bouncy;
        TrajectoryPredictor _predictor;
        readonly Ball[] _ball = new Ball[2];
        readonly BallData[] _ballData = new BallData[2];
        readonly int[] _wins = new int[2];
        readonly bool[] _roundDead = new bool[2];
        readonly List<DecoyRec> _decoys = new List<DecoyRec>();
        AIPlayer _ai;
        PvPHud _hud;
        Phase _phase = Phase.Aim;
        int _round = 1;
        float _timer;
        float _betweenEndsAt;
        float _roundResult = float.NaN;                // DRAW=平局；0/1=胜者；NaN=进行中
        bool _aiming;
        Vector2 _aimStart;
        List<RecycleZone> _suspendedZones;

        class DecoyRec { public Ball Ball; public float Until; }

        public float Timer => _timer;
        public int Round => _round;
        public Phase CurrentPhase => _phase;
        public int WinsOf(int idx) => _wins[idx];
        public BallData ChosenBall(int idx) => _ballData[idx];
        public Ball BallOf(int idx) => _ball[idx];
        public int ArenaTheme { get; private set; }
        public Vector2 LaunchPos(int idx) => new Vector2(0f, idx == P1 ? -5.8f : 5.8f);

        /// <summary>本局还能发射吗（每局每方一次；回合进行中即可补射）。</summary>
        public bool CanLaunch(int idx)
        {
            if (_phase != Phase.Aim && _phase != Phase.Live) return false;
            if (_ball[idx] != null || _roundDead[idx]) return false;
            return _timer > 0f;
        }

        // ---------------- 对局入口 ----------------

        public static PvPManager Begin(GameConfig cfg, PhysicsMaterial2D bouncy,
            TrajectoryPredictor predictor, BallData p1Ball, string p1BuffId, int arenaTheme)
        {
            if (I != null) return I;
            var go = new GameObject("=== PvP ===");
            var m = go.AddComponent<PvPManager>();
            I = m;

            m._cfg = cfg;
            m._bouncy = bouncy;
            m._predictor = predictor;
            m._ballData[P1] = p1Ball;
            int n = BallManager.I.KindCount;
            m._ballData[P2] = BallManager.I.KindAt(Random.Range(0, n));
            m.ArenaTheme = arenaTheme;
            PvPDamage.ResetForMatch(p1BuffId);

            // PvE 侧挂起：发射器停摆、待发球收起、大厅与 PvE HUD 隐去、回收区停用（球永不消失）
            var launcher = Object.FindObjectOfType<BallLauncher>();
            if (launcher != null) launcher.enabled = false;
            BallManager.I.SuspendIdle();
            MainMenuScreen.I?.SetLobbyVisible(false);
            var hud = Object.FindObjectOfType<Hud>();
            var safe = hud != null ? hud.transform.Find("SafeArea") : null;
            if (safe != null) safe.gameObject.SetActive(false);
            m._suspendedZones = new List<RecycleZone>(Object.FindObjectsOfType<RecycleZone>());
            foreach (var z in m._suspendedZones) z.gameObject.SetActive(false);

            m._ai = go.AddComponent<AIPlayer>();
            m._hud = go.AddComponent<PvPHud>();
            m._hud.Init(m);
            ArenaController.Build(cfg, bouncy, arenaTheme, go.transform);
            m.StartRound(1);
            return m;
        }

        // ---------------- 回合流程 ----------------

        void StartRound(int round)
        {
            _round = round;
            _phase = Phase.Aim;
            _timer = PvPRule.RoundSeconds;
            _roundResult = float.NaN;
            _roundDead[0] = _roundDead[1] = false;
            ClearBalls();
            _hud?.OnRoundStart();
        }

        void ClearBalls()
        {
            for (int i = 0; i < 2; i++)
                if (_ball[i] != null) DespawnBall(_ball[i]);
            _ball[0] = _ball[1] = null;
            for (int i = _decoys.Count - 1; i >= 0; i--) DespawnBall(_decoys[i].Ball);
            _decoys.Clear();
        }

        void EndRound(float result)
        {
            if (_phase == Phase.Between || _phase == Phase.MatchOver) return;
            _roundResult = result;
            _phase = Phase.Between;
            _betweenEndsAt = Time.time + PvPRule.BetweenRoundSeconds;
            if (result == P1 || result == P2) _wins[(int)result]++;
            _hud?.OnRoundEnd(result);
        }

        void EndMatch()
        {
            _phase = Phase.MatchOver;
            int result = _wins[P1] > _wins[P2] ? P1 : _wins[P2] > _wins[P1] ? P2 : 2;
            MetaProgress.AddStardust(result == P1 ? PvPRule.WinStardust
                : result == P2 ? PvPRule.LoseStardust : PvPRule.DrawStardust);
            _hud?.ShowMatchResult(result);
        }

        // ---------------- 发射（同时发射：P1 松手瞬间 AI 球齐发） ----------------

        public bool TryLaunch(int idx, Vector2 dir)
        {
            if (!CanLaunch(idx)) return false;
            if (dir.sqrMagnitude < 0.0001f) return false;
            var data = _ballData[idx];
            if (data == null) return false;

            var stats = PvPBalance.Stats(data.kind);
            float speed = Mathf.Clamp(data.speed * (idx == P1 ? PvPDamage.P1SpeedMult : 1f),
                _cfg.minSpeed, _cfg.maxSpeed);
            _ball[idx] = SpawnBall(data, idx, stats.hp * (idx == P1 ? PvPDamage.P1HpMult : 1f));
            _ball[idx].ForceLaunch(dir.normalized * speed);
            if (_phase == Phase.Aim) _phase = Phase.Live;

            // 同时发射：玩家松手瞬间，AI 球同帧齐发（瞄向玩家球方向 ±7° 误差——迎头对撞开局）
            if (idx == P1) LaunchAiCounterpart(dir);
            return true;
        }

        /// <summary>AI 联动发射：瞄向玩家球方向 ±7°（与玩家球同帧齐发）。</summary>
        void LaunchAiCounterpart(Vector2 playerDir)
        {
            if (!CanLaunch(P2)) return;
            var from = LaunchPos(P2);
            var target = (Vector2)LaunchPos(P1) + playerDir * 3f;         // 瞄玩家球飞行前方
            Vector2 dir = (target - from).normalized;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector2.down;

            float err = Random.Range(-7f, 7f);
            float r = err * Mathf.Deg2Rad;
            float c = Mathf.Cos(r), s = Mathf.Sin(r);
            dir = new Vector2(dir.x * c - dir.y * s, dir.x * s + dir.y * c);

            var aiBall = SpawnBall(_ballData[P2], P2, PvPBalance.Stats(_ballData[P2].kind).hp);
            aiBall.transform.position = (Vector3)LaunchPos(P2);
            aiBall.ForceLaunch(dir.normalized * Mathf.Clamp(
                _ballData[P2].speed, _cfg.minSpeed, _cfg.maxSpeed));
            _ball[P2] = aiBall;
        }

        Ball SpawnBall(BallData data, int owner, float hp)
        {
            var ball = Pools.Ball.Take(() => NewBall(data));
            ball.ApplyData(data);
            ball.gameObject.SetActive(true);
            ball.name = owner == P1 ? "PvpBall1" : "PvpBall2";
            ball.transform.SetParent(BallManager.I.transform, false);
            ball.transform.position = (Vector3)LaunchPos(owner);
            ball.Owner = owner;
            ball.PvpDecoy = false;
            ball.DamageMult = 1f;
            ball.PvpHpMax = Mathf.Max(1f, hp);
            ball.PvpHp = ball.PvpHpMax;
            ball.SetPvpRadius(PvPRule.PvpBallRadius);            // PvP 球体放大：对撞截面翻倍
            return ball;
        }

        Ball NewBall(BallData data)
        {
            var go = new GameObject("PvpBall");
            var b = go.AddComponent<Ball>();
            b.Init(data, _cfg, _bouncy);
            return b;
        }

        void DespawnBall(Ball b)
        {
            if (b == null) return;
            if (b.IsLive) b.MarkNotLive();
            Pools.Ball.Release(b);
        }

        // ---------------- 对撞结算 ----------------

        /// <summary>球对球对撞（双方 OnCollisionEnter 都会调，本帧成对去重靠冷却窗）。</summary>
        public void HandleClash(Ball me, Ball other, Vector2 point, Vector2 normal, Vector2 postVel)
        {
            if (_phase != Phase.Aim && _phase != Phase.Live) return;
            if (me == null || other == null || !me.IsLive || !other.IsLive) return;
            if (me.PvpDecoy && other.PvpDecoy) return;                       // 替身对替身：无结算

            if (Time.time - me.PvpLastClash < PvPRule.ClashCooldown ||
                Time.time - other.PvpLastClash < PvPRule.ClashCooldown) return;
            me.PvpLastClash = other.PvpLastClash = Time.time;

            // 伤害计算（攻防双向）
            float atkMe = AtkOf(me);
            float atkOther = AtkOf(other);
            float dmgToOther = atkMe;
            float dmgToMe = atkOther;

            // 相位球：闪避判定——穿身而过零伤，仅半伤回报
            bool meDodge = me.Data.kind == BallKind.Phase && Random.value < PvPRule.PhaseDodgeChance;
            bool otherDodge = other.Data.kind == BallKind.Phase && Random.value < PvPRule.PhaseDodgeChance;
            if (meDodge && otherDodge) { meDodge = otherDodge = false; }     // 双闪避对消：正常对撞

            // 穿透球：穿身而过，只吃 40% 反伤
            bool mePierce = me.Data.kind == BallKind.Pierce && !otherDodge;
            bool otherPierce = other.Data.kind == BallKind.Pierce && !meDodge;

            if (otherDodge) dmgToOther *= 0.5f;
            if (meDodge) dmgToMe = 0f;
            if (mePierce) dmgToMe *= PvPRule.PierceTakenMult;
            if (otherPierce) dmgToOther *= PvPRule.PierceTakenMult;

            ApplyDamage(other, dmgToOther, point, NeonStyle.Cyan);
            ApplyDamage(me, dmgToMe, point, NeonStyle.Red);

            // 激光球：对撞附加灼伤
            if (me.Data.kind == BallKind.Prism) ChipDamage(other, PvPRule.LaserChipDamage, point);
            if (other.Data.kind == BallKind.Prism) ChipDamage(me, PvPRule.LaserChipDamage, point);

            // 控制系：冻结叠层 / 时缓+时停
            if (me.Data.kind == BallKind.Frost) FrostHit(other);
            if (other.Data.kind == BallKind.Frost) FrostHit(me);
            if (me.Data.kind == BallKind.Time)
            {
                other.PvpSlow(PvPRule.SlowBallFactor, PvPRule.SlowBallSeconds);
                other.PvpFreeze(PvPRule.TimeStopBallSeconds);    // 时停：对方球也定身一瞬（真·时停）
            }
            if (other.Data.kind == BallKind.Time)
            {
                me.PvpSlow(PvPRule.SlowBallFactor, PvPRule.SlowBallSeconds);
                me.PvpFreeze(PvPRule.TimeStopBallSeconds);
            }

            // 击退系：回旋（拧飞）/ 爆炸（轰开 + BOOM 大字）
            if (me.Data.kind == BallKind.Curve) Shove(other, point, PvPRule.KnockbackCurve);
            if (other.Data.kind == BallKind.Curve) Shove(me, point, PvPRule.KnockbackCurve);
            if (me.Data.kind == BallKind.Explosive)
            {
                Shove(other, point, PvPRule.KnockbackExplosive);
                Shove(me, point, PvPRule.KnockbackExplosive);
                if (EffectManager.I != null)
                {
                    EffectManager.I.PlayRing(point, NeonStyle.Red, 0.3f, 2.6f, 0.35f);
                    EffectManager.I.Flash(point, NeonStyle.Red, 2.4f, 0.25f);
                    EffectManager.I.Shake(0.14f, 0.22f);
                }
                if (FloatingText.I != null)
                    FloatingText.I.Spawn(point, "BOOM!", NeonStyle.Red, 0.9f);
            }
            if (other.Data.kind == BallKind.Explosive)
            {
                Shove(me, point, PvPRule.KnockbackExplosive);
                Shove(other, point, PvPRule.KnockbackExplosive);
                if (EffectManager.I != null)
                {
                    EffectManager.I.PlayRing(point, NeonStyle.Red, 0.3f, 2.6f, 0.35f);
                    EffectManager.I.Flash(point, NeonStyle.Red, 2.4f, 0.25f);
                    EffectManager.I.Shake(0.14f, 0.22f);
                }
                if (FloatingText.I != null)
                    FloatingText.I.Spawn(point, "BOOM!", NeonStyle.Red, 0.9f);
            }

            // 数量系：分裂（对撞生双替身，每局一次）
            if (me.Data.kind == BallKind.Splitter && !me.PvpDecoy && !me.PvpSpawnedHelper) SpawnDecoys(me, 2);
            if (other.Data.kind == BallKind.Splitter && !other.PvpDecoy && !other.PvpSpawnedHelper) SpawnDecoys(other, 2);

            // 镜像系：被对撞时把 50% 伤害镜像回攻击方（以彼之道还施彼身——与分裂的"生替身"彻底分工）
            if (other.Data.kind == BallKind.Mirror && !other.PvpDecoy && dmgToOther > 0f)
                ApplyDamage(me, dmgToOther * 0.5f, point, NeonStyle.White);
            if (me.Data.kind == BallKind.Mirror && !me.PvpDecoy && dmgToMe > 0f)
                ApplyDamage(other, dmgToMe * 0.5f, point, NeonStyle.White);

            // 穿身物理：恢复入射方向穿过对方（不反弹）
            Vector2 inDirMe = Vector2.Reflect(postVel.normalized, normal);
            if (mePierce && !meDodge) me.PvpPierceThrough(other.GetComponent<Collider2D>(), inDirMe);
            if (meDodge) me.PvpPierceThrough(other.GetComponent<Collider2D>(), inDirMe);

            // 演出：火花 + 闪光 + 小震
            var fx = EffectManager.I;
            if (fx != null)
            {
                fx.SpawnSparks(point, (me.RB.velocity + other.RB.velocity) * 0.25f, 14, 2.2f, NeonStyle.White);
                fx.PlayRing(point, NeonStyle.White, 0.2f, 1.1f, 0.25f);
                fx.Shake(0.08f, 0.15f);
            }
        }

        float AtkOf(Ball b)
        {
            float atk = b.PvpDecoy ? PvPBalance.DecoyAtk : PvPBalance.Stats(b.Data.kind).atk;
            if (!b.PvpDecoy && b.Owner == P1) atk *= PvPDamage.P1AtkMult;
            if (!b.PvpDecoy && b.Data.kind == BallKind.Rebound && b.Data != null)
                atk *= Mathf.Clamp(b.TargetSpeed / Mathf.Max(1f, b.Data.speed), 1f, 1.8f);
            return atk;
        }

        void ApplyDamage(Ball b, float dmg, Vector2 point, Color numColor)
        {
            if (dmg <= 0f || b == null || !b.IsLive) return;
            b.PvpHp -= dmg;
            if (FloatingText.I != null)
                FloatingText.I.Spawn(point, Mathf.RoundToInt(dmg).ToString(), numColor, 0.5f);
            if (b.PvpHp <= 0f) EliminateBall(b);
        }

        void ChipDamage(Ball b, float dmg, Vector2 point)
        {
            if (b == null || !b.IsLive) return;
            b.PvpHp -= dmg;
            if (FloatingText.I != null)
                FloatingText.I.Spawn(point + Vector2.up * 0.4f, Mathf.RoundToInt(dmg).ToString(),
                    new Color(0.75f, 0.45f, 1f), 0.35f);
            if (b.PvpHp <= 0f) EliminateBall(b);
        }

        void FrostHit(Ball b)
        {
            if (b == null || !b.IsLive) return;
            b.PvpFrostStacks++;
            if (b.PvpFrostStacks >= PvPRule.FreezeStacks)
            {
                b.PvpFrostStacks = 0;
                b.PvpFreeze(PvPRule.FreezeBallSeconds);
                if (FloatingText.I != null)
                    FloatingText.I.Spawn(b.transform.position, "冻结!", new Color(0.65f, 0.88f, 1f), 0.8f);
            }
        }

        void Shove(Ball b, Vector2 point, float impulse)
        {
            if (b == null || !b.IsLive || b.RB == null) return;
            Vector2 dir = (Vector2)b.transform.position - point;
            if (dir.sqrMagnitude < 0.01f) dir = Vector2.up;
            b.RB.velocity = b.RB.velocity.normalized * b.TargetSpeed + dir.normalized * impulse;
        }

        /// <summary>PvP 球体掉血统一入口（激光/对撞/机关）：血量归零 → 击碎路由。</summary>
        public void DamageBall(Ball b, float dmg, Vector2 point)
        {
            if (b == null || !b.IsLive) return;
            b.PvpHp -= dmg;
            if (FloatingText.I != null)
                FloatingText.I.Spawn(point, Mathf.RoundToInt(dmg).ToString(), NeonStyle.Portal, 0.4f);
            if (b.PvpHp <= 0f) EliminateBall(b);
        }

        /// <summary>球被击碎：本体→回合结束；替身→仅消散；无主球（PvE 误入）→直接消散不结算。</summary>
        public void EliminateBall(Ball b)
        {
            if (b == null) return;
            if (b.Owner < 0) { DespawnBall(b); return; }            // 防御：PvE 无主球不参与回合胜负结算
            var fx = EffectManager.I;
            if (fx != null)
            {
                Vector3 p = b.transform.position;
                fx.SpawnSparks(p, Vector2.up, 18, 2.6f, b.Data != null ? b.Data.coreColor : NeonStyle.Cyan);
                fx.PlayRing(p, NeonStyle.White, 0.25f, 1.6f, 0.35f);
                fx.Flash(p, NeonStyle.White, 2.2f, 0.22f);
                fx.Shake(0.12f, 0.25f);
            }

            if (b.PvpDecoy)
            {
                _decoys.RemoveAll(d => d.Ball == b);
                DespawnBall(b);
                return;
            }

            int loser = b.Owner;
            _roundDead[loser] = true;
            _ball[loser] = null;
            DespawnBall(b);

            if (_roundDead[0] && _roundDead[1]) EndRound(DRAW);             // 同帧互毁：平局
            else EndRound(1 - loser);
        }

        // ---------------- 替身 ----------------

        public void SpawnDecoys(Ball source, int count)
        {
            if (source == null) return;
            source.PvpSpawnedHelper = true;
            var v = (Vector2)source.RB.velocity;
            if (v.sqrMagnitude < 0.01f) return;
            v.Normalize();
            float step = count > 1 ? 50f : 0f;
            for (int i = 0; i < count; i++)
            {
                float a = (i - (count - 1) * 0.5f) * step;
                float r = a * Mathf.Deg2Rad;
                float cs = Mathf.Cos(r), sn = Mathf.Sin(r);
                Vector2 d = new Vector2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
                var ball = Pools.Ball.Take(() => NewBall(source.Data));
                ball.ApplyData(source.Data);
                ball.gameObject.SetActive(true);
                ball.name = "PvpDecoy";
                ball.transform.SetParent(BallManager.I.transform, false);
                ball.transform.position = source.transform.position + (Vector3)(d * 0.35f);
                ball.Owner = source.Owner;
                ball.PvpDecoy = true;
                ball.DamageMult = 1f;
                ball.PvpHpMax = PvPBalance.DecoyHp;
                ball.PvpHp = ball.PvpHpMax;
                ball.ForceLaunch(d * source.TargetSpeed);
                _decoys.Add(new DecoyRec { Ball = ball, Until = Time.time + PvPRule.DecoyLifetime });
            }
        }

        void TickDecoys()
        {
            for (int i = _decoys.Count - 1; i >= 0; i--)
            {
                var d = _decoys[i];
                if (d.Ball == null || !d.Ball.IsLive || Time.time >= d.Until)
                {
                    _decoys.RemoveAt(i);
                    if (d.Ball != null && d.Ball.IsLive) DespawnBall(d.Ball);
                }
            }
        }

        /// <summary>在场 PvP 球列表（引力球/引力场遍历用）。</summary>
        public List<Ball> GetLiveBalls()
        {
            var list = new List<Ball>();
            for (int i = 0; i < 2; i++)
                if (_ball[i] != null && _ball[i].IsLive) list.Add(_ball[i]);
            for (int i = 0; i < _decoys.Count; i++)
                if (_decoys[i].Ball != null && _decoys[i].Ball.IsLive) list.Add(_decoys[i].Ball);
            return list;
        }

        // ---------------- 主循环 ----------------

        void Update()
        {
            if (_phase == Phase.MatchOver) return;
            TickDecoys();

            if (_phase == Phase.Between)
            {
                if (Time.time >= _betweenEndsAt)
                {
                    if (_wins[P1] >= PvPRule.WinRounds || _wins[P2] >= PvPRule.WinRounds || _round >= PvPRule.MaxRounds)
                        EndMatch();
                    else
                        StartRound(_round + 1);
                }
                return;
            }

            _timer -= Time.deltaTime;

            // P1 瞄准/发射（每局一次；发射后纯观战）
            if (CanLaunch(P1))
            {
                if (!_aiming && InputManager.PressedDown())
                {
                    _aiming = true;
                    _aimStart = InputManager.PointerWorld();
                }
                if (_aiming)
                {
                    float minElev = _cfg.minAimElevationDeg * Mathf.Deg2Rad;
                    if (InputManager.Pressed())
                    {
                        if (AimSystem.TryComputeAim(_aimStart, InputManager.PointerWorld(), LaunchPos(P1),
                            _cfg.cancelDragDist, minElev, out var aimDir))
                            _predictor?.Show(LaunchPos(P1), aimDir);
                        else
                            _predictor?.Hide();
                    }
                    else
                    {
                        _aiming = false;
                        _predictor?.Hide();
                        if (AimSystem.TryComputeAim(_aimStart, InputManager.PointerWorld(), LaunchPos(P1),
                            _cfg.cancelDragDist, minElev, out var dir))
                            TryLaunch(P1, dir);
                    }
                }
            }
            else if (_aiming)
            {
                _aiming = false;
                _predictor?.Hide();
            }

            // AI 发射已并入 TryLaunch 的同帧联动（LaunchAiCounterpart）——不再独立 Tick

            if (_timer <= 0f)
                EndRound(DRAW);                                            // 超时双活：平局
        }

        // ---------------- 出口 ----------------

        /// <summary>回大厅（结算面板按钮）：清场 + PvE 全恢复。</summary>
        public void Exit()
        {
            ClearBalls();
            PvPDamage.Clear();

            var launcher = Object.FindObjectOfType<BallLauncher>();
            if (launcher != null) launcher.enabled = true;
            if (BallManager.I != null)
            {
                BallManager.I.ResumeIdle();
                if (_predictor != null && BallManager.I.CurrentBallData != null)
                    _predictor.Init(_cfg, BallManager.I.CurrentBallData.radius);
            }
            if (_suspendedZones != null)
                foreach (var z in _suspendedZones)
                    if (z != null) z.gameObject.SetActive(true);
            var hud = Object.FindObjectOfType<Hud>();
            var safe = hud != null ? hud.transform.Find("SafeArea") : null;
            if (safe != null) safe.gameObject.SetActive(true);
            MainMenuScreen.I?.SetLobbyVisible(true);

            I = null;
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (I == this) I = null;
        }
    }
}
