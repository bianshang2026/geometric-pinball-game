using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 30 张地图库（P7，架构文档 §9/§15）：5 区域 × 6 图，代码定义区域表（Data Class 数据驱动）。
    /// 采样全部走 RngService（runSeed 派生）——同 Run 同节点必得同图（断点续玩/重试一致）。
    /// 主题模型（决策点①）：Run 主题区域从"已解锁池"随机（击败 B_k 解锁 R_{k+1}）；
    /// Boss 节点独立抽一个已解锁区域的 Boss 图。
    /// </summary>
    public static class StageLibrary
    {
        /// <summary>区域定义（Index 0..4 → R1..R5；每区 6 图，第 6 张为该区 Boss 图）。</summary>
        public class RegionInfo
        {
            public int Index;
            public string Name;
            public int FirstStageId;                      // 1, 7, 13, 19, 25
            public int BossStageId => FirstStageId + 5;
        }

        public static readonly RegionInfo[] Regions =
        {
            new RegionInfo { Index = 0, Name = "矩阵实验场", FirstStageId = 1 },
            new RegionInfo { Index = 1, Name = "镜面回廊",   FirstStageId = 7 },
            new RegionInfo { Index = 2, Name = "混沌裂隙",   FirstStageId = 13 },
            new RegionInfo { Index = 3, Name = "引力穹界",   FirstStageId = 19 },
            new RegionInfo { Index = 4, Name = "核心圣殿",   FirstStageId = 25 },
        };

        static readonly StageData[] _stages = new StageData[31];   // 下标 1..30
        public static bool Ready { get; private set; }

        /// <summary>加载 Stage01..30（GameBootstrap 调用；任一缺失则回退旧 4 图流程）。</summary>
        public static void Init()
        {
            Ready = false;
            for (int i = 1; i <= 30; i++)
                _stages[i] = Resources.Load<StageData>("Stage" + i.ToString("00"));
            Ready = _stages[1] != null && _stages[30] != null;
            if (!Ready) Debug.LogWarning("[StageLibrary] 30 图资产缺失，回退旧关卡流程");
        }

        public static StageData Get(int id)
            => id >= 1 && id <= 30 ? _stages[id] : null;

        // ---------------- 解锁池（决策点①：击败 B_k → R_{k+1} 入池） ----------------

        /// <summary>已解锁区域数（B1 恒可用；BossK1..4 击杀记录逐级 +1；封顶 5）。</summary>
        public static int UnlockedBossCount
        {
            get
            {
                int n = 1;
                for (int k = 1; k <= 4; k++)
                    if (MetaProgress.Loaded && MetaProgress.HasUnlock("BossK" + k)) n++;
                return Mathf.Min(n, Regions.Length);
            }
        }

        /// <summary>Run 主题区域（决定本 Run 全部战斗图的风格池）。</summary>
        public static int PickThemeRegion(int runSeed)
            => RngService.For(runSeed, 900, 1).Next(UnlockedBossCount);

        /// <summary>Boss 节点区域（独立于主题抽一个已解锁区域 → 对应 Boss 图）。</summary>
        public static int PickBossRegion(int runSeed)
            => RngService.For(runSeed, 901, 2).Next(UnlockedBossCount);

        /// <summary>P9.5 层位制：地图行 → 区域池（一 Run 跨 4 区域 ≈20 张图，杀重复感）。
        /// Boss 区域仍从解锁池抽（决策点①）；区域解锁只影响 Boss 遭遇。</summary>
        public static int RegionForRow(int row)
        {
            if (row <= 1) return 0;                    // L0-1 → R1
            if (row <= 3) return 1;                    // L2-3 → R2
            if (row <= 4) return 2;                    // L4 → R3
            return 3;                                  // L5+ → R4
        }

        /// <summary>抽一张该区域的常规战斗图（前 5 张；P9.5 层内去重：
        /// (seed,row) 生成 0-4 排列，本层第 column 场取排列对应项——同层不同节点必不同图）。</summary>
        public static StageData SampleBattle(int regionIdx, int runSeed, int row, int column)
        {
            var r = Regions[Mathf.Clamp(regionIdx, 0, Regions.Length - 1)];
            var rng = RngService.For(runSeed, 800 + row, 1);   // 同层共用流 → 层内排列
            int[] slots = { 0, 1, 2, 3, 4 };
            for (int i = slots.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (slots[i], slots[j]) = (slots[j], slots[i]);
            }
            return Get(r.FirstStageId + slots[Mathf.Clamp(column, 0, 4)]);
        }

        public static StageData BossStage(int regionIdx)
            => Get(Regions[Mathf.Clamp(regionIdx, 0, Regions.Length - 1)].BossStageId);
    }
}
