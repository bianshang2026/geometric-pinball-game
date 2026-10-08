using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 关卡流程：从 StageData[] 构建关卡、换关（循环）、重玩本关（完整重建实体+球+连锁）。
    /// 核心击破 → GameEvents.OnVictory → 胜利状态（HUD 出浮层）。
    /// Phase 6：支持地图节点进入战斗（普通/精英/Boss）——运行时克隆 StageData 施加修饰，绝不污染资产。
    /// </summary>
    public class StageManager : MonoBehaviour
    {
        public static StageManager I { get; private set; }

        GameConfig _cfg;
        PhysicsMaterial2D _bouncy;
        StageData[] _stages;                 // 旧 4 图（库缺失时的回退）
        StageData _currentData;              // 本次构建的克隆数据（P7：Current/Hud 标签读这里）
        Transform _root;
        int _index;                          // 当前关卡号-1（Hud 显示 Index+1 = 采样图 id）
        bool _victory;
        bool _eliteMark;
        float _bossScale = 1f;
        CoreBlock _bossCore;

        // 当前战斗对应的地图节点（胜利后回地图；非地图模式为 null）
        MapNode _battleNode;

        /// <summary>当前关卡数据（P7：返回本次构建克隆；不再索引旧数组——_index 已是 1-30 图号）。</summary>
        public StageData Current => _currentData
            ?? (_stages != null && _stages.Length > 0
                ? _stages[Mathf.Clamp(_index, 0, _stages.Length - 1)]
                : null);
        public int Index => _index;
        public bool Victory => _victory;
        public MapNode BattleNode => _battleNode;

        public void Init(GameConfig cfg, PhysicsMaterial2D bouncy, StageData[] stages)
        {
            I = this;
            _cfg = cfg;
            _bouncy = bouncy;
            var list = new List<StageData>();
            if (stages != null)
                foreach (var s in stages)
                    if (s != null) list.Add(s);
            _stages = list.ToArray();
            GameEvents.OnVictory += OnVictory;
            // P9：不再预构建旧关卡——战斗由大厅/地图的 EnterBattle 按需构建
        }

        void OnDestroy()
        {
            GameEvents.OnVictory -= OnVictory;
            if (I == this) I = null;
        }

        void OnVictory()
        {
            if (_victory) return;          // once 守卫
            _victory = true;

            // P8：Boss 击杀记录（解锁下一区域池——决策点①）
            if (_battleNode != null && _battleNode.type == NodeType.Boss
                && _currentData != null && StageLibrary.Ready)
            {
                int bossIdx = _currentData.stageId / 6;             // 06→1 .. 30→5
                if (bossIdx >= 1 && bossIdx <= 4)
                    MetaProgress.Record("BossK" + bossIdx);        // K1..K4 → 解锁 R2..R5
            }
        }

        public void BuildStage(int i)
        {
            if (_stages == null || _stages.Length == 0) return;
            _index = ((i % _stages.Length) + _stages.Length) % _stages.Length;
            _eliteMark = false;
            _bossScale = 1f;
            BuildFromData(_stages[_index]);
        }

        /// <summary>按地图节点进入战斗（P7：30 图库采样）——
        /// 普通/精英=主题区域抽前 5 图；Boss=独立抽已解锁区域 Boss 图（coreHp×3+巨型+护盾）；
        /// 精英=全量 HP×2+精英标记。同 seed 同节点必同图（重试/续档一致）。</summary>
        public void EnterBattle(MapNode node)
        {
            if (node == null) return;
            _battleNode = node;
            int seed = RunMap.I != null ? RunMap.I.RunSeed : 0;

            StageData template;
            int stageNum;                                          // Hud 显示"关卡 N"用
            if (StageLibrary.Ready)
            {
                if (node.type == NodeType.Boss)
                {
                    // 难度曲线：Boss=已解锁的最高区域——每局终局必是本 Run 最难关，且随击败 B_k 逐级走难（B1→B5）
                    int bossRegion = StageLibrary.UnlockedBossCount - 1;
                    template = StageLibrary.BossStage(bossRegion);
                }
                else
                {
                    // P9.5：层位制采样——L0-1→R1 / L2-3→R2 / L4→R3 / L5+→R4，层内去重
                    int region = StageLibrary.RegionForRow(node.row);
                    template = StageLibrary.SampleBattle(region, seed, node.row, node.column);
                }
                if (template == null) { Debug.LogError("[Stage] 采样失败"); return; }
                stageNum = template.stageId;
            }
            else
            {
                int stageIdx = node.type == NodeType.Battle ? _index : _stages.Length - 1;   // 旧 4 图回退
                template = _stages[stageIdx];
                stageNum = stageIdx + 1;
            }

            var data = Object.Instantiate(template);               // 每次进入战斗都取新克隆（修饰不污染资产）

            if (node.type == NodeType.Elite)
            {
                data.coreHp *= 2f;
                for (int i = 0; i < data.entries.Length; i++)
                    data.entries[i].hp *= 2f;
                _eliteMark = true;
                _bossScale = 1f;
            }
            else if (node.type == NodeType.Boss)
            {
                data.coreHp *= 3f;
                _eliteMark = false;
                _bossScale = 1.5f;
            }
            else
            {
                _eliteMark = false;
                _bossScale = 1f;
            }

            _index = stageNum - 1;
            BuildFromData(data);
        }

        void BuildFromData(StageData data)
        {
            _currentData = data;                       // P7：Current/Hud 标签的数据源
            _victory = false;
            RunStats.ResetBattle();                       // P6：战报统计·战斗段清零
            TeardownStage();                               // P11：可破坏实体先回池，再销毁场景根
            var go = new GameObject("=== Stage ===");
            _root = go.transform;

            // P11：战斗期池预热（构建期一次性补齐；此后战斗中零 Instantiate/Destroy）
            PrewarmBattle(data);

            // P8：Boss 图（06/12/18/24/30）加载对应 BossData → 由 BossController 阶段驱动（抑制静态护盾环）
            BossData bossData = null;
            if (data.stageId >= 6 && data.stageId % 6 == 0 && StageLibrary.Ready)
            {
                int bossIdx = Mathf.Clamp(data.stageId / 6, 1, 5);   // 06→1, 12→2, 18→3, 24→4, 30→5
                bossData = Resources.Load<BossData>("Boss" + bossIdx);
            }
            var core = StageBuilder.Build(_root, data, _cfg, _bouncy, _eliteMark, _bossScale,
                suppressShieldRing: bossData != null);

            // Boss 护盾（文档二十八）：核心外圈 10 块小方块（StageBuilder 已挂到 core），护盾在则核心免疫
            if (core != null && _bossScale > 1f)
                _bossCore = core;

            if (core != null && bossData != null)
            {
                var ctl = core.gameObject.AddComponent<BossController>();
                ctl.Init(core, bossData, _cfg, _bouncy);
            }

            if (BallManager.I != null)
                BallManager.I.ResetForStage(data.ballStock
                    + (BuildState.I != null ? BuildState.I.ExtraStock : 0)      // 构筑·球数+1
                    + (MetaProgress.Loaded ? MetaProgress.StartStockBonus : 0)  // 局外·备用弹体
                    + (RunMap.I != null ? RunMap.I.BonusStock : 0));            // 宝箱·弹药补给

            Pools.MarkWarm();                          // P11：战斗构建完成——压测从此断言零创建
            TutorialSystem.OnStageBuilt(data);         // 新手教学：本关机关/球种首遇介绍
        }

        // ---------------- P11：战斗池预热（构建期一次性，幂等） ----------------

        System.Func<GeometryBlock> _prewarmBlock;
        System.Func<BombBlock> _prewarmBomb;
        System.Func<Debris> _prewarmDebris;

        void InitPrewarmFuncs()
        {
            if (_prewarmBlock != null) return;
            _prewarmBlock = NewPrewarmBlock;
            _prewarmBomb = NewPrewarmBomb;
            _prewarmDebris = NewPrewarmDebris;
        }

        GeometryBlock NewPrewarmBlock()
        {
            var go = new GameObject("BlockRoot");
            go.transform.SetParent(Pools.Root, false);
            var b = go.AddComponent<GeometryBlock>();
            b.Build(Vector2.zero, 1f, _cfg, _bouncy);          // 完整构建：子物体/碰撞体在预热期建好
            return b;
        }

        BombBlock NewPrewarmBomb()
        {
            var go = new GameObject("BombRoot");
            go.transform.SetParent(Pools.Root, false);
            var b = go.AddComponent<BombBlock>();
            b.Build(Vector2.zero, _cfg, _bouncy);
            return b;
        }

        FloatBlock NewPrewarmFloat()
        {
            var go = new GameObject("FloatRoot");
            go.transform.SetParent(Pools.Root, false);
            var f = go.AddComponent<FloatBlock>();
            f.Build(Vector2.zero, 1f, _cfg, _bouncy);
            return f;
        }

        Debris NewPrewarmDebris()
        {
            var go = new GameObject("Debris");
            go.transform.SetParent(Pools.Root, false);
            var d = go.AddComponent<Debris>();
            d.Build();
            return d;
        }

        /// <summary>
        /// 构建期预热：Block 40 覆盖 关卡方块≤12 + Boss 环≤12 + 生成波≤14 并发峰值；
        /// Bomb 16 覆盖关卡炸弹 + Boss 生成波；Debris 40（溢出 TryTake 优雅降级）；Ball=max(8, 库存+4)。
        /// 预热实例全部完整构建——战斗期取用只做状态复位，零子物体创建。
        /// </summary>
        void PrewarmBattle(StageData data)
        {
            InitPrewarmFuncs();
            Pools.Block.EnsureCount(40, _prewarmBlock);
            Pools.Bomb.EnsureCount(16, _prewarmBomb);
            Pools.Debris.EnsureCount(40, _prewarmDebris);
            Pools.Float.EnsureCount(8, NewPrewarmFloat);
            if (BallManager.I != null)
                Pools.Ball.EnsureCount(Mathf.Max(8, data.ballStock + 4), BallManager.I.CreateBallForPool);
        }

        /// <summary>围绕 Boss 核心生成护盾环：10 块 hp2 小方块，半径 1.75，弦间隙 0.53<球径，球无法直穿。</summary>
        public int BossShieldCount => _bossCore != null ? _bossCore.LiveShields : 0;

        public void NextStage() => BuildStage(_index + 1);

        /// <summary>重玩本节点：同 seed 同节点 → 采样确定 → 同图重建（StageManager 采样一致性的验收点）。</summary>
        public void RestartStage()
        {
            if (_battleNode != null) { EnterBattle(_battleNode); return; }
            BuildStage(_index);
        }

        /// <summary>清场（P9：回大厅时调用——销毁战斗实体，保留配置）。
        /// P11：可破坏实体（方块/炸弹/Boss 生成物）先回池，再销毁场景根——池跨关卡永不枯竭。</summary>
        public void ClearStage()
        {
            TeardownStage();
            _currentData = null;
            _battleNode = null;
            _bossCore = null;
            _victory = false;
        }

        void TeardownStage()
        {
            if (_root == null) return;
            // 活跃方块/炸弹（含静态护盾块/Boss 环块）回池；机关家具为一次性构建物随根销毁
            var blocks = _root.GetComponentsInChildren<GeometryBlock>(true);
            for (int i = 0; i < blocks.Length; i++)
                if (blocks[i].gameObject.activeSelf) Pools.Block.Release(blocks[i]);
            var bombs = _root.GetComponentsInChildren<BombBlock>(true);
            for (int i = 0; i < bombs.Length; i++)
                if (bombs[i].gameObject.activeSelf) Pools.Bomb.Release(bombs[i]);
            Destroy(_root.gameObject);
            _root = null;
        }
    }
}
