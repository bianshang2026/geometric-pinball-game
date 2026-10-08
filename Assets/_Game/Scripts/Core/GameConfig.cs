using UnityEngine;

namespace GeoBreaker
{
    /// <summary>全局配置（数据驱动，避免写死）。</summary>
    [CreateAssetMenu(fileName = "GameConfig", menuName = "GeoBreaker/GameConfig")]
    public class GameConfig : ScriptableObject
    {
        [Header("场地")]
        public float fieldWidth = 9f;
        public float fieldHeight = 16f;
        public float wallThickness = 0.5f;
        public float recycleZoneHeight = 0.5f;
        public Vector2 launchPos = new Vector2(0f, -6.9f);

        [Header("球")]
        public int ballStock = 3;
        public float minSpeed = 8f;
        public float maxSpeed = 24f;
        public float maxFlightSeconds = 30f;
        public float minAimElevationDeg = 10f;
        public float cancelDragDist = 0.3f;

        [Header("轨迹预测")]
        public int predictionBounces = 3;
        public float predictionDotSpacing = 0.4f;

        [Header("机关-菱形加速器")]
        public float diamondSpeedMultiplier = 1.25f;
        public float diamondCooldownSeconds = 0.2f;

        [Header("机关-三角反射器")]
        public float triangleSpeedFactor = 1.15f;
        public float triangleSlowFactor = 0.87f;

        [Header("机关-炸弹")]
        public float bombExplosionRadius = 1.6f;
        public float bombExplosionDamage = 2f;
        public float chainDelaySeconds = 0.08f;

        [Header("机关-传送门")]
        public float portalCooldownSeconds = 0.25f;
        public float portalRadius = 0.45f;

        [Header("连锁系统")]
        public float chainWindowSeconds = 1.6f;
        public float chainDamagePerLevel = 0.10f;
        public float chainDamageMaxMultiplier = 2.0f;

        [Header("球种-分裂/爆炸")]
        public int maxConcurrentBalls = 6;
        public float splitCooldownSeconds = 0.12f;
        public float ballBlastRadius = 1.6f;             // P9.5：爆炸球范围 1.4→1.6
        public float ballBlastDamage = 3f;               // P9.5：爆炸球伤害 2→3

        [Header("球种-棱镜激光")]
        public float prismLaserCooldown = 0.12f;         // 每球独立冷却：高频爽感但限速
        public float prismLaserDamage = 1f;              // 每个被穿目标伤害
        public float prismLaserLength = 14f;             // 激光最大长度（遇不可破坏体截断；PvP 远程压制）

        [Header("PvP-激光球周期蓄力")]
        public float pvpLaserInterval = 1.5f;            // PvP 激光：自动射击间隔秒
        public float pvpLaserChargeSeconds = 0.4f;       // PvP 激光：蓄力时长秒
        public float pvpLaserDamagePvp = 6f;             // PvP 激光：命中对方球伤害

        [Header("球种-回旋/镜像/相位")]
        public float curveAngleDeg = 22f;                // 回旋球（旧）：反弹附加偏转角（已被持续偏转取代，保留兼容）
        public float curveRateDegPerSec = 90f;           // 回旋球：持续旋向偏转速率（度/秒，旋向发射时随机 ±1）——弹道成可见弧线
        public float mirrorEchoDuration = 3f;            // 镜像球：影子存在时长
        public float mirrorEchoDamageMult = 0.5f;        // 镜像球：影子伤害倍率
        public float mirrorEchoSplitAngle = 38f;         // 镜像球：影子与本体反弹方向的岔开角
        public float phaseDuration = 0.3f;               // 相位球：相位态时长（无碰撞穿越）
        public float phaseCooldown = 1.5f;               // 相位球：相位触发冷却

        [Header("球种-时间/冻结")]
        public float timeSlowFactor = 0.4f;              // 时间球：机关减速系数（0.4=慢 60%）
        public float timeSlowDuration = 2.5f;              // 时间球：减速持续
        public float timeSlowCooldown = 0.8f;            // 时间球：触发冷却
        public float timeStopFreezeScale = 0.03f;           // 时停定格：瞬间的全局时间流速（近全停）
        public float timeStopFreezeSeconds = 0.5f;          // 时停定格：持续（真实秒，仅全新结界触发）
        public float timeStopOverlayAlpha = 0.35f;          // 时停暗幕：全屏强度
        public float freezeDuration = 2.5f;              // 冻结球：目标冻结时长

        [Header("机关-能量节点")]
        public float energyNodeDuration = 2.5f;          // 能量节点：激活持续（时间门强制开+炸弹引爆联动）
        public float energyNodeBlastRadius = 3f;         // 能量节点：炸弹引爆半径

        [Header("机关-力场(引力/斥力)")]
        public float gravityFieldRadius = 1.6f;
        public float gravityFieldStrength = 45f;
        public float gravityFieldMinDist = 0.55f;

        [Header("机关-时间门")]
        public float timeGatePeriod = 2.4f;
        public float timeGateOpenRatio = 0.55f;

        [Header("Boss-旋转弱点")]
        public float bossWeakArcAngle = 100f;         // 弧度数
        public float bossWeakArcSpeedDeg = 55f;       // 旋转速度（度/秒）
        public float bossWeakCritMultiplier = 2f;     // 弧内命中伤害倍率
        public float bossWeakArmorMultiplier = 0.2f;   // 弧外护甲伤害倍率

        [Header("球种-引力球")]
        public float gravityBallPullRadius = 2.2f;    // 感知半径
        public float gravityBallPullStrength = 60f;    // 拉力强度（近强远弱）

        [Header("广告（当前为模拟实现；P12 接 Stark 换实现，业务零改动）")]
        public int adReviveBalls = 3;                // 看广告复活：补充球数
        public int adStardustGain = 15;              // 看广告：获得星尘
    }
}
