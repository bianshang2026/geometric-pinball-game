using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 战报统计（P6，架构文档 §13）：静态聚合器，零依赖、零 GC。
    /// 伤害埋点在 GeometryEntity.TakeDamage（球击/爆炸/核心全管线共用）；
    /// 连锁经 GameEvents.OnChainChanged 订阅。
    /// 分段：Battle*=当前节点战斗内；Run*=整 Run 累计最大。
    /// </summary>
    public static class RunStats
    {
        // ---- 当前战斗段 ----
        public static int BattleMaxChain { get; private set; }
        public static float BattleMaxHit { get; private set; }
        public static int BattleHits { get; private set; }

        // ---- Run 段 ----
        public static int RunMaxChain { get; private set; }
        public static float RunMaxHit { get; private set; }

        static bool _hooked;

        /// <summary>GameBootstrap 启动时挂事件（只挂一次；静态域重载后重新挂）。</summary>
        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            GameEvents.OnChainChanged += OnChain;
        }

        static void OnChain(int level)
        {
            if (level > BattleMaxChain) BattleMaxChain = level;
            if (level > RunMaxChain) RunMaxChain = level;
        }

        /// <summary>伤害埋点（GeometryEntity.TakeDamage 唯一入口，含连锁乘数后入账值）。</summary>
        public static void RecordDamage(float damage)
        {
            if (damage <= 0f) return;
            BattleHits++;
            if (damage > BattleMaxHit) BattleMaxHit = damage;
            if (damage > RunMaxHit) RunMaxHit = damage;
        }

        /// <summary>进战斗清零战斗段（StageManager.BuildFromData 调用）。</summary>
        public static void ResetBattle()
        {
            BattleMaxChain = 0;
            BattleMaxHit = 0f;
            BattleHits = 0;
        }

        /// <summary>新 Run 清零全部（Hud.StartNewRun 调用）。</summary>
        public static void ResetRun()
        {
            ResetBattle();
            RunMaxChain = 0;
            RunMaxHit = 0f;
        }
    }
}
