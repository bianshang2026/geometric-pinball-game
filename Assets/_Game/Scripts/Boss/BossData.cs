using UnityEngine;

namespace GeoBreaker
{
    /// <summary>Boss 阶段定义（架构 §15）：每阶段独立机制组合；核心 HP 跌入本段即激活。</summary>
    [System.Serializable]
    public class BossPhase
    {
        [Range(0f, 1f)] public float hpTo;               // 本阶段覆盖的 HP 上限比例（末阶段=0）
        public float shieldRingCount;                     // 旋转护盾环块数（0=无）
        public float shieldRingRotSpeed;                  // 度/秒
        public bool weakArc;                              // 旋转弱点弧
        public float weakArcSpeedDeg;                     // 度/秒
        public float weakCritMultiplier = 2f;
        public float weakArmorMultiplier = 0.2f;
        public float gravityStrength;                     // 0=无引力场
        public float gravityRadius = 2.5f;
        public float spawnerPeriod;                       // 0=不生成；秒
        public int spawnerMax;                            // 场上生成物上限
    }

    /// <summary>
    /// BossData（P8）：5 区域 Boss 的数据驱动定义。机制全部由现有系统组合：
    /// 护盾环/弱点弧（CoreBlock 已有）+ 引力场（GravityField）+ 生成器（BossController 新增）。
    /// </summary>
    [CreateAssetMenu(fileName = "BossData", menuName = "GeoBreaker/BossData")]
    public class BossData : ScriptableObject
    {
        public string bossName = "旋转六边堡";
        public BossPhase[] phases;
    }
}
