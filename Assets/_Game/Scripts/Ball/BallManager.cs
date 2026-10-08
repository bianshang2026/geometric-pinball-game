using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 球库存与球种管理：待发球生成、库存球回收扣减、分裂子球（不耗库存）、
    /// 失败判定（库存 0 且全场无存活球）、完整重置、球种切换。
    /// P11 池化：球一律 Pools.Ball 取放（Init 一次性构建 + ApplyData 复用重配），
    /// 战斗中零 Instantiate/Destroy；LiveCount 改为计数器维护（原 GetComponentsInChildren 每次查询有 GC 分配）。
    /// </summary>
    public class BallManager : MonoBehaviour
    {
        public static BallManager I { get; private set; }

        GameConfig _cfg;
        BallData[] _allKinds;                     // 全量球种（含未解锁）
        BallData[] _kinds;                        // 当前可用球种（按局外解锁过滤）
        int _kindIndex;
        PhysicsMaterial2D _bouncy;
        int _liveCount;                           // P11：在场存活球计数（Launch/Spawn 增、Recycle 减）
        System.Func<Ball> _createBall;            // P11：池新建回调缓存（防每次 Take 委托分配）
        readonly List<Ball> _liveBalls = new List<Ball>();   // 在场存活球列表（引力球互吸查询，零分配遍历）

        public Ball IdleBall { get; private set; }
        public int BallsLeft { get; private set; }
        public BallData CurrentBallData => _kinds != null && _kinds.Length > 0 ? _kinds[_kindIndex] : null;
        public int KindCount => _kinds != null ? _kinds.Length : 0;

        public void Init(GameConfig cfg, BallData[] kinds, PhysicsMaterial2D bouncy)
        {
            I = this;
            _cfg = cfg;

            var list = new List<BallData>();
            if (kinds != null)
                foreach (var k in kinds)
                    if (k != null) list.Add(k);
            if (list.Count == 0)
                list.Add(Resources.Load<BallData>("NormalBall"));
            _allKinds = list.ToArray();
            RefreshKinds();                        // 按局外解锁过滤（引力球）

            _bouncy = bouncy;
            _createBall = CreateBallForPool;
            BallsLeft = cfg.ballStock;
            SpawnIdle();
        }

        /// <summary>按局外解锁重过滤可用球种（MetaProgress 购买/重置后调用，含解锁回归）。</summary>
        public void RefreshKinds()
        {
            var list = new List<BallData>();
            foreach (var k in _allKinds)
            {
                if (k.kind == BallKind.Gravity && !(MetaProgress.Loaded && MetaProgress.GravityBallUnlocked))
                    continue;
                if (k.kind == BallKind.Pierce && !(MetaProgress.Loaded && MetaProgress.HasUnlock("PierceBall")))
                    continue;
                if (k.kind == BallKind.Curve && !(MetaProgress.Loaded && MetaProgress.HasUnlock("CurveBall")))
                    continue;
                if (k.kind == BallKind.Mirror && !(MetaProgress.Loaded && MetaProgress.HasUnlock("MirrorBall")))
                    continue;
                if (k.kind == BallKind.Phase && !(MetaProgress.Loaded && MetaProgress.HasUnlock("PhaseBall")))
                    continue;
                if (k.kind == BallKind.Time && !(MetaProgress.Loaded && MetaProgress.HasUnlock("TimeBall")))
                    continue;
                if (k.kind == BallKind.Frost && !(MetaProgress.Loaded && MetaProgress.HasUnlock("FrostBall")))
                    continue;
                if (k.kind == BallKind.Rebound && !(MetaProgress.Loaded && MetaProgress.HasUnlock("ReboundBall")))
                    continue;
                list.Add(k);
            }
            if (list.Count == 0)
            {
                var n = Resources.Load<BallData>("NormalBall");
                if (n != null) list.Add(n);
            }
            _kinds = list.ToArray();
            _kindIndex = Mathf.Clamp(_kindIndex, 0, Mathf.Max(0, _kinds.Length - 1));
        }

        /// <summary>装备首发球种（球库页/启动恢复调用；kindName=枚举名字符串）。</summary>
        public void ApplyLoadout(string kindName)
        {
            if (_kinds == null) return;
            for (int i = 0; i < _kinds.Length; i++)
            {
                if (_kinds[i].kind.ToString() == kindName)
                {
                    SetBallIndex(i);
                    return;
                }
            }
        }

        /// <summary>启动时恢复上次装备（P10：读 Profile.loadout——迁移自旧 PlayerPrefs GB_Loadout）。</summary>
        public void RestoreLoadout()
        {
            string saved = MetaProgress.Loaded && !string.IsNullOrEmpty(MetaProgress.Loadout)
                ? MetaProgress.Loadout
                : PlayerPrefs.GetString("GB_Loadout", "Normal");   // 兼容未迁移旧档
            if (saved != _kinds[_kindIndex].kind.ToString())
                ApplyLoadout(saved);                        // 未解锁/缺资产时保持当前
        }

        /// <summary>可用球种数据（PvP 备战页/测试读用）。</summary>
        public BallData KindAt(int i) => _kinds != null && i >= 0 && i < _kinds.Length ? _kinds[i] : null;

        /// <summary>PvE 待发球收起（PvP 对局期间让位；IdleBall 回池不扣库存）。</summary>
        public void SuspendIdle()
        {
            if (IdleBall != null) { Pools.Ball.Release(IdleBall); IdleBall = null; }
        }

        /// <summary>PvP 结束恢复 PvE 待发球。</summary>
        public void ResumeIdle()
        {
            if (IdleBall == null && BallsLeft > 0) SpawnIdle();
        }

        /// <summary>全场存活球数（库存球 + 子球；P11 计数器维护，零查询开销）。</summary>
        public int LiveCount => _liveCount;

        /// <summary>在场存活球列表（引力球互吸遍历用；只读，勿修改）。</summary>
        public List<Ball> GetLiveBalls() => _liveBalls;

        /// <summary>是否允许分裂出新子球（全局在场球数上限）。</summary>
        public bool CanSpawnChild => _liveCount < _cfg.maxConcurrentBalls;

        /// <summary>池新建回调（P11 预热/溢出共用）：完整构建一颗球（未激活由调用方/池管理）。</summary>
        public Ball CreateBallForPool()
        {
            var go = new GameObject("Ball");
            go.transform.SetParent(Pools.Root, false);
            var ball = go.AddComponent<Ball>();
            ball.Init(_kinds != null && _kinds.Length > 0 ? _kinds[_kindIndex] : CurrentBallData, _cfg, _bouncy);
            return ball;
        }

        Ball TakeBall(string goName, BallData data, bool original)
        {
            var ball = Pools.Ball.Take(_createBall ?? CreateBallForPool);
            ball.name = goName;
            ball.transform.SetParent(transform, false);
            ball.ApplyData(data);
            ball.IsOriginal = original;
            ball.gameObject.SetActive(true);
            return ball;
        }

        void SpawnIdle()
        {
            IdleBall = TakeBall("Ball", CurrentBallData, true);
            IdleBall.transform.position = (Vector2)_cfg.launchPos;
        }

        public void LaunchIdle(Vector2 dir)
        {
            if (IdleBall == null) return;
            var ball = IdleBall;
            IdleBall = null;
            ball.Launch(dir);
            _liveCount++;
            _liveBalls.Add(ball);
        }

        /// <summary>分裂子球：不消耗库存，出生即活动，共享母球速度。</summary>
        public Ball SpawnChild(Ball parent, Vector2 pos, Vector2 dir)
        {
            if (parent == null || !CanSpawnChild) return null;
            var ball = TakeBall("BallChild", parent.Data, false);
            ball.transform.position = pos;
            ball.ForceLaunch(dir * parent.TargetSpeed);
            _liveCount++;
            _liveBalls.Add(ball);
            return ball;
        }

        /// <summary>
        /// 回收：库存球扣 1 球并在有库存时生成下一颗待发球；子球不扣库存。
        /// 库存归零且全场无存活球 → 失败（子球续命机制）。
        /// P11：回池复用，不再 Destroy。
        /// </summary>
        public void Recycle(Ball ball)
        {
            if (ball == null || !ball.IsLive) return;
            Vector3 pos = ball.transform.position;          // 构筑·爆裂需要在回收前取位置
            ball.MarkNotLive();
            _liveCount--;
            _liveBalls.Remove(ball);
            AudioManager.PlayRecycle();                     // 构筑·回收音
            GameEvents.RaiseBallRecycled(ball);

            // 构筑·爆裂：球被回收时产生爆炸
            if (BuildState.I != null && BuildState.I.BlastOnDeath && ExplosionSystem.I != null)
                ExplosionSystem.I.BallBlast(pos);

            if (IdleBall == ball) IdleBall = null;
            bool wasOriginal = ball.IsOriginal;
            Pools.Ball.Release(ball);

            if (wasOriginal)
            {
                BallsLeft--;
                if (BallsLeft > 0)
                {
                    SpawnIdle();
                    return;
                }
            }
            // 胜利后剩余球陆续回收不判失败（胜利已锁定）
            if (StageManager.I != null && StageManager.I.Victory) return;
            if (BallsLeft <= 0 && _liveCount == 0)
                GameEvents.RaiseAllBallsConsumed();
        }

        public void SetBallIndex(int idx)
        {
            if (_kinds == null || _kinds.Length == 0) return;
            _kindIndex = ((idx % _kinds.Length) + _kinds.Length) % _kinds.Length;
            if (IdleBall != null)
            {
                Pools.Ball.Release(IdleBall);
                IdleBall = null;
            }
            if (BallsLeft > 0) SpawnIdle();
            TutorialSystem.OnBallChanged(_kinds[_kindIndex].kind);   // 新手教学：球种切换介绍（仅局内）
        }

        public void CycleBallType() => SetBallIndex(_kindIndex + 1);

        /// <summary>事件节点：献祭 1 颗球库存（保底 1）。</summary>
        public void LoseOneStock()
        {
            if (BallsLeft <= 1) return;                       // 保底 1 球
            BallsLeft--;
            if (IdleBall != null)
            {
                Pools.Ball.Release(IdleBall);
                IdleBall = null;
            }
            SpawnIdle();
        }

        /// <summary>广告复活：补充库存并重生待机球（场上残留状态保持不动）。</summary>
        public void ReviveStock(int add)
        {
            BallsLeft += Mathf.Max(1, add);
            if (IdleBall == null && _liveCount == 0) SpawnIdle();
        }

        /// <summary>按指定库存完整重置（关卡构建/重玩用）。</summary>
        public void ResetForStage(int stock)
        {
            foreach (var b in GetComponentsInChildren<Ball>(true))
                if (b.gameObject.activeSelf) Pools.Ball.Release(b);
            IdleBall = null;
            _liveCount = 0;
            _liveBalls.Clear();
            BallsLeft = Mathf.Max(1, stock);
            SpawnIdle();
            if (ChainSystem.I != null) ChainSystem.I.ResetChain();
        }

        public void ResetAll() => ResetForStage(_cfg.ballStock);
    }
}
