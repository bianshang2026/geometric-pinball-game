using System.Collections.Generic;

namespace GeoBreaker
{
    /// <summary>
    /// PvP 平衡表（弹球对撞模式 v2）：每球 HP/ATK 顾名思义——
    /// 普通球=最肉无特长、爆炸球=高攻脆皮、穿透球=穿身减伤、相位球=概率闪避、
    /// 冻结/时间=控制流、分裂/镜像=数量流、回旋=机动、反弹=成长、激光=灼伤、引力=场控。
    /// </summary>
    public static class PvPBalance
    {
        public class KindStats
        {
            public float hp;
            public float atk;
            public KindStats(float hp, float atk) { this.hp = hp; this.atk = atk; }
        }

        public static readonly Dictionary<BallKind, KindStats> Table = new Dictionary<BallKind, KindStats>
        {
            { BallKind.Normal,    new KindStats(130f, 10f) },   // 稳定：无特长但最肉
            { BallKind.Splitter,  new KindStats(100f, 8f)  },   // 数量压制：对撞生 2 替身
            { BallKind.Explosive, new KindStats(85f, 16f)  },   // 爆发：最高对撞伤+大击退，脆皮
            { BallKind.Prism,     new KindStats(95f, 12f)  },   // 激光灼伤：对撞附加 4 点
            { BallKind.Gravity,   new KindStats(105f, 10f) },   // 场控：引力场把对方球拽进自己节奏
            { BallKind.Pierce,    new KindStats(110f, 12f) },   // 穿透：对撞穿身而过，只吃 40% 反伤
            { BallKind.Curve,     new KindStats(100f, 10f) },   // 机动：弧线难打中对撞强击退
            { BallKind.Mirror,    new KindStats(95f, 9f)   },   // 镜像：受击生镜像（半伤分身）
            { BallKind.Phase,     new KindStats(100f, 9f)  },   // 规避：35% 概率相位穿身零伤
            { BallKind.Time,      new KindStats(100f, 9f)  },   // 节奏：对撞减速对方球 50%/2s
            { BallKind.Frost,     new KindStats(100f, 9f)  },   // 控制：叠 3 层冻结对方球停摆 1s
            { BallKind.Rebound,   new KindStats(100f, 8f)  },   // 成长：弹速越快对撞越疼（×1.0~×1.8）
        };

        public const float DecoyHp = 20f;                 // 替身耐久
        public const float DecoyAtk = 4f;                // 替身对撞伤

        public static KindStats Stats(BallKind k)
        {
            return Table.TryGetValue(k, out var s) ? s : Table[BallKind.Normal];
        }
    }

    /// <summary>PvP 局内词条（P1 专用增益；AI 保持基准线）。</summary>
    public static class PvPDamage
    {
        public static float P1AtkMult = 1f;
        public static float P1HpMult = 1f;
        public static float P1SpeedMult = 1f;

        public class BuffDef
        {
            public string id;
            public string name;
            public string desc;
        }

        public static readonly BuffDef[] Buffs =
        {
            new BuffDef { id = "AtkUp",   name = "对撞强化", desc = "对撞伤害 ×1.25" },
            new BuffDef { id = "HpUp",    name = "装甲强化", desc = "球体耐久 ×1.2" },
            new BuffDef { id = "SpeedUp", name = "弹速强化", desc = "球速 ×1.12" },
        };

        public static void ResetForMatch(string buffId)
        {
            Clear();
            if (string.IsNullOrEmpty(buffId)) return;
            switch (buffId)
            {
                case "AtkUp": P1AtkMult = 1.25f; break;
                case "HpUp": P1HpMult = 1.2f; break;
                case "SpeedUp": P1SpeedMult = 1.12f; break;
            }
        }

        public static void Clear()
        {
            P1AtkMult = P1HpMult = P1SpeedMult = 1f;
        }
    }
}
