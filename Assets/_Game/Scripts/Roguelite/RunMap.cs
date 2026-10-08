using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    public enum NodeType { Battle, Elite, Shop, Event, Chest, Boss }

    /// <summary>地图节点状态（架构文档 §7.3；由 cleared/Current/可点集合推导）。</summary>
    public enum NodeState { Locked, Available, Current, Completed, Failed, Boss }

    /// <summary>地图节点（运行时对象；id=row*100+column 供存档）。</summary>
    public class MapNode
    {
        public NodeType type;
        public int row;
        public int column;
        public int id;                                     // 存档标识
        public readonly List<MapNode> next = new List<MapNode>();   // 指向下一行的连线
        public bool cleared;                                // 已通关（战斗类）
    }

    /// <summary>
    /// Roguelite 地图 v2（P5，架构文档 §7）：8 层分支——
    /// L0 起点 → L1-L5（2-3 节点，L3/L5 保证精英）→ L6 休整层（商店+宝箱）→ L7 Boss；
    /// 访问节点数=8（一 Run 12-18 分钟）；同 runSeed 完全可复现（RngService 派生子流）。
    /// 管理水晶货币与 Run 生命周期标记。
    /// </summary>
    public class RunMap : MonoBehaviour
    {
        public static RunMap I { get; private set; }

        public const int LayerCount = 8;

        public List<List<MapNode>> Rows { get; private set; } = new List<List<MapNode>>();
        public MapNode Current { get; private set; }
        public int Crystals { get; private set; }
        public int BonusStock { get; private set; }
        public int RunLayer => Current != null ? Current.row + 1 : 0;
        public bool RunFinished { get; private set; }

        /// <summary>本 Run 种子（Restore/复现用；Generate 时确定）。</summary>
        public int RunSeed { get; private set; }
        /// <summary>当前节点战斗失败标记（地图红显；重进/胜利后清除）。</summary>
        public bool CurrentFailed { get; private set; }
        /// <summary>最近一次胜利结算的节点 id（MapScreen 连线点亮动画用；-1=无）。</summary>
        public int LastClearedId { get; private set; } = -1;

        public void Init()
        {
            I = this;
        }

        void OnDestroy() { if (I == this) I = null; }

        // ---------------- 生成（v2：8 层 + 确定性 seed） ----------------

        /// <summary>重新生成地图（新 Run）。seedOverride 供测试/复现；applyStartPerk=局外"预载构筑"（Restore 时不重复给）。</summary>
        public MapNode Generate(int? seedOverride = null, bool applyStartPerk = true)
        {
            RunSeed = seedOverride ?? RngService.NewSeed();
            RunFinished = false;
            CurrentFailed = false;
            LastClearedId = -1;
            Crystals = 6 + (MetaProgress.Loaded ? MetaProgress.StartCrystalBonus : 0);
            Rows.Clear();

            for (int r = 0; r < LayerCount; r++)
            {
                var rowList = new List<MapNode>();
                int count = LayerNodeCount(r);
                for (int c = 0; c < count; c++)
                    rowList.Add(new MapNode { row = r, column = c, id = r * 100 + c, type = RollType(r, c) });
                Rows.Add(rowList);
            }

            GuaranteeLayer(3, NodeType.Elite);              // 精英保证层
            GuaranteeLayer(5, NodeType.Elite);
            FixRestLayer();                                // L6 休整层：1 商店 + 1 宝箱

            for (int r = 0; r < LayerCount - 1; r++)
                ConnectRows(r);

            Current = Rows[0][0];
            Current.cleared = true;                          // 起点已占据（首次战斗由 Bootstrap 进入）

            // 局外解锁·预载构筑：新 Run 开局随机 1 层强化（BuildState.Reset 之后调用才有效；Restore 恢复时不重复给）
            if (applyStartPerk && MetaProgress.Loaded && MetaProgress.StartWithUpgrade && BuildState.I != null)
            {
                var pick = BuildState.I.Roll(1);
                if (pick.Count > 0) BuildState.I.Apply(pick[0]);
            }
            return Current;
        }

        /// <summary>由存档恢复：同 seed 重建 + 回放进度（RunManager.LoadAndResume 调用）。</summary>
        public void Restore(RunData d)
        {
            Generate(d.runSeed, applyStartPerk: false);
            var cleared = new HashSet<int>(d.clearedIds ?? new List<int>());
            foreach (var row in Rows)
                foreach (var n in row)
                    n.cleared = cleared.Contains(n.id);
            Current = Find(d.currentId) ?? Rows[0][0];
            Crystals = d.crystals;
            BonusStock = d.bonusStock;
            CurrentFailed = d.currentFailed;
            LastClearedId = -1;
        }

        int LayerNodeCount(int r)
        {
            if (r == 0 || r == LayerCount - 1) return 1;             // 起点 / Boss
            if (r == LayerCount - 2) return 2;                       // 休整层固定 2 节点
            return 2 + RngService.For(RunSeed, r, 77).Next(0, 2);    // L1-L5：2-3 节点（确定性）
        }

        /// <summary>层类型权重表（代码内数据驱动；后续需要调参可资产化为 LayerConfig）。
        /// 注意：LayerNodeCount/Generate 依赖实例 RunSeed，故表访问放实例方法。</summary>
        static readonly (NodeType type, int weight)[][] LayerTables =
        {
            new[] { (NodeType.Battle, 1) },                                                          // L0 起点
            new[] { (NodeType.Battle, 50), (NodeType.Chest, 20), (NodeType.Event, 20), (NodeType.Shop, 10) },
            new[] { (NodeType.Battle, 40), (NodeType.Elite, 15), (NodeType.Chest, 15), (NodeType.Event, 15), (NodeType.Shop, 15) },
            new[] { (NodeType.Battle, 35), (NodeType.Elite, 40), (NodeType.Chest, 10), (NodeType.Event, 15) },   // 精英保证层
            new[] { (NodeType.Battle, 30), (NodeType.Elite, 20), (NodeType.Event, 25), (NodeType.Shop, 25) },
            new[] { (NodeType.Battle, 30), (NodeType.Elite, 45), (NodeType.Chest, 10), (NodeType.Event, 15) },   // 精英保证层
            new[] { (NodeType.Shop, 50), (NodeType.Chest, 50) },                                     // L6 休整
            new[] { (NodeType.Boss, 1) },                                                            // L7
        };

        NodeType RollType(int layer, int column)
        {
            var rng = RngService.For(RunSeed, layer, column);
            var table = LayerTables[layer];
            int total = 0;
            foreach (var t in table) total += t.weight;
            int v = rng.Next(total);
            foreach (var t in table)
            {
                v -= t.weight;
                if (v < 0) return t.type;
            }
            return table[0].type;
        }

        /// <summary>保证层：该层没有指定类型 → 把一个非该类型节点（优先战斗）转换过去。</summary>
        void GuaranteeLayer(int layer, NodeType type)
        {
            var row = Rows[layer];
            bool has = false;
            foreach (var n in row) if (n.type == type) { has = true; break; }
            if (has) return;
            var rng = RngService.For(RunSeed, layer, 999);
            var candidates = new List<MapNode>();
            foreach (var n in row) if (n.type != type) candidates.Add(n);
            if (candidates.Count == 0) return;
            // 优先转战斗节点（保住商店/宝箱的路线价值）
            var target = candidates.Find(n => n.type == NodeType.Battle);
            if (target == null) target = candidates[rng.Next(candidates.Count)];
            target.type = type;
        }

        /// <summary>L6 休整层：恰好 1 商店 + 1 宝箱（顺序随机）。</summary>
        void FixRestLayer()
        {
            var row = Rows[6];
            bool shopFirst = RngService.For(RunSeed, 6, 888).Next(2) == 0;
            row[0].type = shopFirst ? NodeType.Shop : NodeType.Chest;
            row[1].type = shopFirst ? NodeType.Chest : NodeType.Shop;
        }

        void ConnectRows(int r)
        {
            var from = Rows[r];
            var to = Rows[r + 1];
            foreach (var node in from)
            {
                var rng = RngService.For(RunSeed, r, 100 + node.column);
                int links = rng.Next(1, 3);                       // 1-2 条出边
                var shuffled = new List<MapNode>(to);
                for (int i = shuffled.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
                }
                for (int i = 0; i < Mathf.Min(links, shuffled.Count); i++)
                    if (!node.next.Contains(shuffled[i]))
                        node.next.Add(shuffled[i]);
            }
            // 补边：确保下一行每个节点至少有一条入边（无死节点）
            foreach (var target in to)
            {
                bool hasIn = false;
                foreach (var f in from)
                    if (f.next.Contains(target)) { hasIn = true; break; }
                if (!hasIn)
                {
                    var rng = RngService.For(RunSeed, r, 500 + target.column);
                    from[rng.Next(from.Count)].next.Add(target);
                }
            }
        }

        // ---------------- 状态推导与旅行 ----------------

        /// <summary>节点状态推导（MapScreen 渲染/测试用）。clickable=当前可点集合（含中途重试的 Current）。</summary>
        public NodeState StateOf(MapNode n, ICollection<MapNode> clickable = null)
        {
            if (n == null) return NodeState.Locked;
            if (n.cleared) return NodeState.Completed;
            if (n == Current)
                return CurrentFailed ? NodeState.Failed : NodeState.Current;
            if (clickable != null && clickable.Contains(n)) return NodeState.Available;
            return NodeState.Locked;
        }

        /// <summary>当前节点的可前往相邻节点。</summary>
        public IReadOnlyList<MapNode> UnlockedNext()
            => Current != null ? Current.next : (IReadOnlyList<MapNode>)System.Array.Empty<MapNode>();

        /// <summary>前进到相邻节点（必须是 UnlockedNext 之一）。</summary>
        public bool Travel(MapNode node)
        {
            if (node == null || !UnlockedNextContains(node)) return false;
            Current = node;
            return true;
        }

        bool UnlockedNextContains(MapNode node)
        {
            if (Current == null) return false;
            foreach (var n in Current.next)
                if (n == node) return true;
            return false;
        }

        /// <summary>战斗节点胜利结算（水晶 + 星尘 + 失败标记清除）。</summary>
        public void OnNodeCleared()
        {
            if (Current != null)
            {
                Current.cleared = true;
                LastClearedId = Current.id;
            }
            AddCrystals(2);                        // 战斗奖励水晶
            CurrentFailed = false;
            if (MetaProgress.Loaded)
                MetaProgress.AwardNodeClear(Current != null ? Current.type : NodeType.Battle);
            if (Current != null && Current.type == NodeType.Boss)
            {
                RunFinished = true;
                if (MetaProgress.Loaded) MetaProgress.AwardRunFinish();
            }
        }

        /// <summary>当前节点战斗失败（球耗尽）——地图红显，可原地重试（决策点⑥）。</summary>
        public void MarkCurrentFailed() => CurrentFailed = true;

        /// <summary>清除失败标记（重玩本关时调用）。</summary>
        public void UnmarkCurrentFailed() => CurrentFailed = false;

        public MapNode Find(int id)
        {
            foreach (var row in Rows)
                foreach (var n in row)
                    if (n.id == id) return n;
            return null;
        }

        public void AddCrystals(int n) => Crystals = Mathf.Max(0, Crystals + n);

        /// <summary>宝箱"弹药补给"：本 Run 球库存加成（每关构建时计入）。</summary>
        public void AddBonusStock(int n) => BonusStock = Mathf.Max(0, BonusStock + n);

        public bool SpendCrystals(int n)
        {
            if (Crystals < n) return false;
            Crystals -= n;
            return true;
        }
    }
}
