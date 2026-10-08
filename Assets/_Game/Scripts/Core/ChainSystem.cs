using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 连锁系统（文档十八 Chain Combo）：
    /// 时间窗口内连续触发（球击可破坏几何体 / 任意爆炸）则 CHAIN 等级累加，超时归零。
    /// 连锁提供伤害加成（chainDamagePerLevel/级，封顶 maxMultiplier），并广播给 HUD。
    /// 注意：必须在所有几何实体之前 Init，保证同一帧内先记连锁后结算伤害。
    /// </summary>
    public class ChainSystem : MonoBehaviour
    {
        public static ChainSystem I { get; private set; }

        GameConfig _cfg;
        float _lastEventTime = -999f;

        public int Chain { get; private set; }

        /// <summary>当前连锁伤害乘数（链=1 时为 1；含构筑"连锁伤害"加成）。</summary>
        public float CurrentMultiplier => 1f + Mathf.Min(
            Mathf.Max(0, Chain - 1) * (_cfg.chainDamagePerLevel + (BuildState.I != null ? BuildState.I.ChainBonusPerLevel : 0f)),
            _cfg.chainDamageMaxMultiplier - 1f);

        public void Init(GameConfig cfg)
        {
            I = this;
            _cfg = cfg;
            GameEvents.OnBallCollision += HandleBallHit;
            GameEvents.OnExplosion += HandleExplosion;
        }

        void OnDestroy()
        {
            GameEvents.OnBallCollision -= HandleBallHit;
            GameEvents.OnExplosion -= HandleExplosion;
            if (I == this) I = null;
        }

        void HandleBallHit(BallCollisionEvent e)
        {
            var ent = e.target != null ? e.target.GetComponent<GeometryEntity>() : null;
            if (ent != null && !ent.indestructible && ent.Alive)
                RegisterChain(e.point);
        }

        void HandleExplosion(Vector2 center) => RegisterChain(center);

        /// <summary>连锁事件（可破坏几何体受击/爆炸）。公共入口，测试可直接调用。</summary>
        public void RegisterChain(Vector2 pos)
        {
            if (Time.time - _lastEventTime > _cfg.chainWindowSeconds)
                Chain = 0;
            _lastEventTime = Time.time;
            Chain++;
            GameEvents.RaiseChainChanged(Chain);
        }

        public void ResetChain()
        {
            Chain = 0;
            _lastEventTime = -999f;
            GameEvents.RaiseChainChanged(0);
        }

        void Update()
        {
            if (Chain > 0 && Time.time - _lastEventTime > _cfg.chainWindowSeconds)
                ResetChain();
        }

        /// <summary>伤害乘算（null 安全）：球击与爆炸统一走这里。</summary>
        public static float Apply(float damage)
            => I != null ? damage * I.CurrentMultiplier : damage;
    }
}
