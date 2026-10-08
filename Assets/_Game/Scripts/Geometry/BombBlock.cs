using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 炸弹◇：一击即爆。死亡时不走碎裂特效，而是交给 ExplosionSystem
    /// 产生范围伤害与连锁爆炸。
    /// P11 池化：Build 幂等（重建复位状态+重启闪烁），死亡回 Pools.Bomb。
    /// </summary>
    public class BombBlock : GeometryEntity
    {
        SpriteRenderer _outline;
        BoxCollider2D _col;
        bool _built;

        public BombBlock Build(Vector2 pos, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            Setup(cfg, NeonStyle.BombRed);
            name = $"Bomb({pos.x:0.##},{pos.y:0.##})";
            transform.SetPositionAndRotation(new Vector3(pos.x, pos.y, 0f), Quaternion.Euler(0f, 0f, 45f));
            transform.localScale = Vector3.one;
            indestructible = false;
            punchOnHit = true;
            hp = 1f;
            Alive = true;

            if (!_built)
            {
                _built = true;
                _col = gameObject.AddComponent<BoxCollider2D>();
                _col.size = new Vector2(0.62f, 0.62f);
                OwnCollider = _col;

                var fill = NeonStyle.MakeSprite(transform, "Fill", ProcSprites.SquareFill(),
                    new Color(NeonStyle.BombRed.r, NeonStyle.BombRed.g, NeonStyle.BombRed.b, 0.10f), 9, false);
                fill.transform.localScale = Vector3.one * 0.62f;

                _outline = NeonStyle.MakeSprite(transform, "Outline", ProcSprites.SquareOutline(), NeonStyle.BombRed, 10, false);
                _outline.transform.localScale = Vector3.one * 0.62f;

                var glow = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), NeonStyle.BombRed, 8, true);
                glow.color = new Color(NeonStyle.BombRed.r, NeonStyle.BombRed.g, NeonStyle.BombRed.b, 0.13f);
                glow.transform.localScale = Vector3.one * 0.22f;

                var core = NeonStyle.MakeSprite(transform, "Core", ProcSprites.CircleSoft(), NeonStyle.BombRed, 11, false);
                core.transform.localScale = Vector3.one * 0.18f;
            }
            else
            {
                _col.enabled = true;                        // P11：复用重置
            }
            _col.sharedMaterial = bouncy;

            StartCoroutine(Blink());                         // 复用时重启（停用即已自动停止）
            return this;
        }

        System.Collections.IEnumerator Blink()
        {
            float phase = Random.value * 6f;
            while (true)
            {
                yield return null;
                if (_outline == null) yield break;
                float a = 0.65f + 0.35f * Mathf.Sin(Time.time * 5f + phase);
                _outline.color = new Color(NeonStyle.BombRed.r, NeonStyle.BombRed.g, NeonStyle.BombRed.b, a);
            }
        }

        protected override void Die(Vector2 hitPoint)
        {
            if (!Alive) return;
            Alive = false;
            // 爆炸特效与范围伤害统一由爆炸系统表现
            if (ExplosionSystem.I != null)
                ExplosionSystem.I.Explode(transform.position);
            ReturnToPool();
        }

        /// <summary>P11 池化：死亡回 Pools.Bomb（StageBuilder/BossController 复用）。</summary>
        protected override void ReturnToPool() => Pools.Bomb.Release(this);
    }
}
