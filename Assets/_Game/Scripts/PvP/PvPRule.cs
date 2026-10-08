namespace GeoBreaker
{
    /// <summary>PvP 规则常量（弹球对撞模式 v2）：单局倒计时、对撞冷却、三局两胜。</summary>
    public static class PvPRule
    {
        public const float RoundSeconds = 45f;           // 单局总时长（含瞄准与飞行）
        public const float ClashCooldown = 0.35f;       // 同对球对撞伤害冷却（防贴脸连跳秒杀）
        public const int MaxRounds = 3;                 // 三局两胜
        public const int WinRounds = 2;
        public const float BetweenRoundSeconds = 2.4f;   // 回合间幕时长
        public const float DecoyLifetime = 4f;           // 替身存活
        public const float FreezeStacks = 3f;           // 冻结所需冰冻值层数
        public const float FreezeBallSeconds = 1f;      // 冻结停摆时长
        public const float SlowBallSeconds = 2f;        // 时缓时长
        public const float SlowBallFactor = 0.5f;       // 时缓系数
        public const float TimeStopBallSeconds = 0.7f;  // 时间球时停：对方球定身时长（真·时停）
        public const float PhaseDodgeChance = 0.35f;    // 相位球闪避概率
        public const float PierceTakenMult = 0.4f;      // 穿透球承受反伤系数
        public const float KnockbackCurve = 4f;         // 回旋球击退冲量
        public const float KnockbackExplosive = 6f;     // 爆炸球击退冲量
        public const float LaserChipDamage = 4f;         // 激光球对撞附加灼伤
        public const float PvpBallRadius = 0.55f;        // PvP 球体半径（比 PvE 0.28 大一倍——对撞截面翻倍，双方更容易碰到）
        public const int WinStardust = 10;
        public const int LoseStardust = 3;
        public const int DrawStardust = 5;
    }
}
