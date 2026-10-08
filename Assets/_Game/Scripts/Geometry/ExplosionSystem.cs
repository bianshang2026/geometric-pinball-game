using System.Collections;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 爆炸系统：范围伤害 + 连锁起爆调度 + 爆炸特效。
    /// 炸弹死亡只调用一次 Explode；半径内其他炸弹经由延迟伤害死亡，
    /// 其 Die 再次调度 Explode —— 形成逐级起爆的连锁链。
    /// 爆炸均计入连锁系统（GameEvents.OnExplosion）。
    /// </summary>
    public class ExplosionSystem : MonoBehaviour
    {
        public static ExplosionSystem I { get; private set; }

        GameConfig _cfg;

        public void Init(GameConfig cfg)
        {
            I = this;
            _cfg = cfg;
        }

        void OnDestroy() => I = null;

        float BlastRadiusMult => BuildState.I != null ? BuildState.I.BlastRadiusMult : 1f;

        /// <summary>炸弹爆炸：大特效 + BOOM 字样。</summary>
        public void Explode(Vector2 center)
        {
            var fx = EffectManager.I;
            if (fx != null)
            {
                fx.Flash(center, NeonStyle.Yellow, 4.5f, 0.22f);
                fx.PlayRing(center, NeonStyle.White, 0.3f, 2.2f, 0.3f);
                fx.PlayRing(center, NeonStyle.Orange, 0.2f, 1.4f, 0.4f);
                fx.SpawnSparks(center, Vector2.up, 22, 2f);
                fx.Shake(0.18f, 0.3f);
            }
            if (FloatingText.I != null)
                FloatingText.I.Spawn(center, "BOOM", NeonStyle.Orange, 0.7f);
            GameEvents.RaiseExplosion(center);
            RadiusDamage(center, _cfg.bombExplosionRadius * BlastRadiusMult, _cfg.bombExplosionDamage);
        }

        /// <summary>爆炸球周期爆破/爆裂球回收爆破：P9.5 特效放大（可感知度）。</summary>
        public void BallBlast(Vector2 center)
        {
            var fx = EffectManager.I;
            if (fx != null)
            {
                fx.Flash(center, NeonStyle.Orange, 3.4f, 0.22f);
                fx.PlayRing(center, NeonStyle.White, 0.3f, 1.8f, 0.3f);
                fx.PlayRing(center, NeonStyle.Orange, 0.2f, 1.2f, 0.4f);
                fx.SpawnSparks(center, Vector2.up, 20, 1.8f);
                fx.Shake(0.14f, 0.25f);
            }
            if (FloatingText.I != null)
                FloatingText.I.Spawn(center, "爆！", NeonStyle.Orange, 0.55f);
            GameEvents.RaiseExplosion(center);
            RadiusDamage(center, _cfg.ballBlastRadius * BlastRadiusMult, _cfg.ballBlastDamage);
        }

        /// <summary>余震（构筑强化）：击中方块后的小冲击波——轻量范围伤害，计入连锁。</summary>
        public void Shockwave(Vector2 center)
        {
            var fx = EffectManager.I;
            if (fx != null)
            {
                fx.PlayRing(center, NeonStyle.White, 0.1f, 0.55f, 0.2f);
                fx.SpawnSparks(center, Vector2.up, 5, 0.8f);
            }
            GameEvents.RaiseExplosion(center);
            RadiusDamage(center, 0.9f * BlastRadiusMult, 1f);
        }

        void RadiusDamage(Vector2 center, float radius, float damage)
        {
            // 范围伤害（按距离延迟，制造"波"的连锁观感）
            var hits = Physics2D.OverlapCircleAll(center, radius);
            for (int i = 0; i < hits.Length; i++)
            {
                var ent = hits[i].GetComponent<GeometryEntity>();
                if (ent == null || !ent.Alive) continue;
                float d = Vector2.Distance(center, ent.transform.position);
                StartCoroutine(DelayedDamage(ent, damage, _cfg.chainDelaySeconds + d * 0.03f));
            }
        }

        IEnumerator DelayedDamage(GeometryEntity ent, float damage, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (ent != null && ent.Alive)
                ent.TakeDamage(ChainSystem.Apply(damage), ent.transform.position);
        }
    }
}
