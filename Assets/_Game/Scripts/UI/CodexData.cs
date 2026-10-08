namespace GeoBreaker
{
    /// <summary>
    /// 图鉴条目（P9 MVP：静态信息册；发现跟踪待 P10 PlayerProfile）。
    /// 三册：机关 / 球种 / Boss。
    /// </summary>
    public static class CodexData
    {
        public enum Category { Mechanism, Ball, Boss }

        public struct Entry
        {
            public Category category;
            public string title;
            public string desc;
            public Entry(Category c, string t, string d) { category = c; title = t; desc = d; }
        }

        public static readonly Entry[] Entries =
        {
            // ---- 机关 ----
            new Entry(Category.Mechanism, "方块", "标准可破坏几何体。连锁的基础节拍：C 音。"),
            new Entry(Category.Mechanism, "炸弹", "一击即爆，范围伤害并引发连锁起爆。听见 beep-beep 意味着它注意到了你的球。"),
            new Entry(Category.Mechanism, "三角反射器", "三条边各有规则：加速边（黄）/减速边（蓝）/普通边。决定球的进出速度。"),
            new Entry(Category.Mechanism, "菱形加速器", "不可摧毁。每次撞击球速 ×1.25，连续撞击叠加——路线规划的发动机。"),
            new Entry(Category.Mechanism, "镜面", "完全反射的细线。反射决定弹道走向；镜面链是精确几何的艺术。"),
            new Entry(Category.Mechanism, "引力场", "把球拉入弧线轨道。弹道在这里不是直线而是作曲。"),
            new Entry(Category.Mechanism, "斥力场", "推开一切靠近的球。用得好是弹弓，用不好是灾难。"),
            new Entry(Category.Mechanism, "单向墙", "正向穿透、反向弹回。进出自由度由方向决定。"),
            new Entry(Category.Mechanism, "时间门", "周期开合。玩家必须计算球到达时门的状态——相位的艺术。"),
            new Entry(Category.Mechanism, "传送门", "成对出现，保持速度矢量射出。WHOOM → PLING。"),
            new Entry(Category.Mechanism, "能量节点", "球击中激活 2.5 秒：全场时间门强制开启，并引爆周围炸弹。解谜中继器。"),
            new Entry(Category.Mechanism, "黑洞", "成组出现。球进入一洞，从组内另一洞以随机方向吐出——混沌的传送。"),
            new Entry(Category.Mechanism, "移动墙", "正弦往复的不可破坏墙。缝隙随时间漂移，是活的地形；会被时间球减速、冻结球停摆。"),
            new Entry(Category.Mechanism, "分裂棱镜", "球穿过即分裂出两个 ±35° 子球（保速、不耗库存）。球海机关流的分裂源。"),
            new Entry(Category.Mechanism, "浮空方块", "漂浮的可破坏方块：会被球撞飞、被引力球吸走——漂移的掩体。"),
            // ---- 球种 ----
            new Entry(Category.Ball, "普通球", "基础弹射。一切构筑的起点。"),
            new Entry(Category.Ball, "分裂球", "每次碰撞 1→2，球海战术。"),
            new Entry(Category.Ball, "爆炸球", "首次撞击范围爆破。开路先锋。"),
            new Entry(Category.Ball, "穿透球", "每发可穿透 2 个目标，直线洞穿。星尘 18 解锁。"),
            new Entry(Category.Ball, "激光球", "每次碰撞向反弹方向发射穿射激光——直线上目标全受伤。星尘 16 解锁。"),
            new Entry(Category.Ball, "引力球", "引力场吸引其他球与浮空方块聚团——编队与聚怪打法。星尘 20 解锁。"),
            new Entry(Category.Ball, "回旋球", "持续旋向偏转——飞行弹道成肉眼可见的弧线，覆盖刁钻角度。星尘 14 解锁。"),
            new Entry(Category.Ball, "镜像球", "碰撞生成限时镜像（50% 伤害，3 秒）。双线操作，机关流最爱。星尘 18 解锁。"),
            new Entry(Category.Ball, "相位球", "撞方块/机关造成伤害后幽灵穿透（节奏冷却）——无视护墙护盾，直捣核心。星尘 22 解锁。"),
            new Entry(Category.Ball, "时间球", "命中墙/机关——世界定格一瞬，对方球也被定身，结界内机关减速 60%/2.5s。星尘 16 解锁。"),
            new Entry(Category.Ball, "冻结球", "命中目标冻结 2.5s：机关停摆、Boss 停转——控制核心的玩法。星尘 20 解锁。"),
            new Entry(Category.Ball, "反弹球", "每次碰撞弹速 +8%——封闭竞技场里越弹越快的几何连弹。星尘 12 解锁。"),
            // ---- Boss ----
            new Entry(Category.Boss, "旋转六边堡", "护盾环 12 块环绕 + 旋转弱点弧。学会掐弧窗口。"),
            new Entry(Category.Boss, "镜面三棱核", "先破环，再追 75°/s 快弧。反射的艺术。"),
            new Entry(Category.Boss, "混沌孕核", "持续生成方块+炸弹屏障——炸弹连锁开路。"),
            new Entry(Category.Boss, "黑洞引力核", "引力把球拉入轨道 + 相位快弧 80°/s，弧外仅 ×0.1。"),
            new Entry(Category.Boss, "终焉几何核", "三阶段：护盾环 → 弧+生成 → 狂暴引力+100°/s。终局考试。"),
        };
    }
}
