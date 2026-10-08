using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 棱镜激光（P·棱镜球重做）：从起点沿方向射出穿伤激光——
    /// 路径上所有可破坏实体受伤（穿伤），第一个不可破坏体/墙截断光束。
    /// PvP 模式：激光还能命中敌方球（点到射线距离判定）——先命中的球截断光束并掉血。
    /// 静态核心 + 视觉走 EffectManager.PlayLaser（固定 8 条线池轮转）。
    /// PvE 每次碰撞触发；PvP 固定周期蓄力自动射（Ball.PvpLaserTick）。
    /// </summary>
    public static class LaserSystem
    {
        /// <summary>发射一道激光。pvpOwner ≥ 0 时启用 PvP 球命中判定。返回被穿伤的目标数。</summary>
        public static int Fire(Vector2 origin, Vector2 dir, GameConfig cfg, Collider2D ignore = null,
            int pvpOwner = -1, float pvpDamage = 0f)
        {
            if (cfg == null || dir.sqrMagnitude < 0.0001f) return 0;
            dir = dir.normalized;
            var start = origin + dir * 0.12f;                       // 起点外推：避免自撞发射球
            var hits = Physics2D.RaycastAll(start, dir, cfg.prismLaserLength);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            int damaged = 0;
            Vector2 end = start + dir * cfg.prismLaserLength;      // 兜底终点（全穿）

            // PvP 球命中：先于实体判定——找到激光路径上第一个敌方球
            Ball pvpHit = null;
            float pvpHitDist = float.MaxValue;
            if (pvpOwner >= 0 && PvPManager.Active)
            {
                foreach (var b in PvPManager.I.GetLiveBalls())
                {
                    if (b == null || !b.IsLive || b.Owner == pvpOwner) continue;
                    var toBall = (Vector2)b.transform.position - start;
                    float along = Vector2.Dot(toBall, dir);
                    if (along < 0f || along > cfg.prismLaserLength) continue;
                    float perp = Vector2.Distance(start + dir * along, (Vector2)b.transform.position);
                    if (perp > b.GetComponent<CircleCollider2D>()?.radius + 0.1f) continue;
                    if (along < pvpHitDist) { pvpHit = b; pvpHitDist = along; }
                }
            }

            // 实体穿伤（含 PvP 球截断）
            float pvpCutDist = pvpHit != null ? pvpHitDist : float.MaxValue;
            foreach (var h in hits)
            {
                var col = h.collider;
                if (col == null || col == ignore || col.isTrigger) continue;
                if (h.distance > pvpCutDist) break;                  // PvP 球更近：光束在球处截断
                var ent = col.GetComponent<GeometryEntity>();
                if (ent == null || !ent.Alive)
                {
                    end = h.centroid;                                // 墙/非实体：截断光束
                    break;
                }
                if (ent.indestructible)
                {
                    end = h.centroid;                                // 家具（镜面/单向墙等）：挡光不受损
                    break;
                }
                ent.TakeDamage(ChainSystem.Apply(cfg.prismLaserDamage), h.centroid);
                damaged++;
                if (EffectManager.I != null)
                    EffectManager.I.SpawnSparks(h.centroid, dir, 4, 1f, NeonStyle.Portal);
            }

            // PvP 球掉血（激光灼伤）
            if (pvpHit != null && PvPManager.Active)
            {
                PvPManager.I.DamageBall(pvpHit, pvpDamage > 0f ? pvpDamage : cfg.prismLaserDamage,
                    pvpHit.transform.position);
                damaged++;
            }

            if (EffectManager.I != null && damaged >= 0)
                EffectManager.I.PlayLaser(origin, end, NeonStyle.Portal);
            return damaged;
        }
    }
}
