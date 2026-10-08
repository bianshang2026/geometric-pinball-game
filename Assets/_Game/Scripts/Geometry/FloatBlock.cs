using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 浮空方块（第三批机关）：带 dynamic Rigidbody 的可破坏方块——
    /// 可被球撞飞、可被引力球吸走（引力场价值闭环：浮块聚团）、漂移掩体。
    /// 注册表 All 供引力球互吸遍历（OnEnable/OnDisable 维护）。
    /// </summary>
    public class FloatBlock : GeometryEntity
    {
        public static readonly new List<FloatBlock> All = new List<FloatBlock>();
        Rigidbody2D _rb;
        bool _built;

        public FloatBlock Build(Vector2 pos, float hpValue, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            Setup(cfg, NeonStyle.Cyan);
            name = $"Float({pos.x:0.##},{pos.y:0.##})";
            transform.SetPositionAndRotation(new Vector3(pos.x, pos.y, 0f), Quaternion.identity);
            transform.localScale = Vector3.one;
            indestructible = false;
            punchOnHit = true;
            hp = hpValue;
            Alive = true;

            if (!_built)
            {
                _built = true;
                _rb = gameObject.AddComponent<Rigidbody2D>();
                _rb.bodyType = RigidbodyType2D.Dynamic;
                _rb.gravityScale = 0f;
                _rb.drag = 2.2f;                            // 漂移衰减：被撞/被吸后缓慢停住
                _rb.angularDrag = 2.5f;
                _rb.mass = 0.7f;
                _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
                _rb.constraints = RigidbodyConstraints2D.None;   // 可旋转翻滚

                var col = gameObject.AddComponent<BoxCollider2D>();
                col.size = new Vector2(0.7f, 0.7f);
                col.sharedMaterial = bouncy;
                OwnCollider = col;

                var fill = NeonStyle.MakeSprite(transform, "Fill", ProcSprites.SquareFill(), NeonStyle.BlockFill, 9, false);
                fill.transform.localScale = new Vector3(0.7f, 0.7f, 1f);
                var outline = NeonStyle.MakeSprite(transform, "Outline", ProcSprites.SquareOutline(), NeonStyle.Cyan, 10, false);
                outline.transform.localScale = new Vector3(0.7f, 0.7f, 1f);
                var glow = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), NeonStyle.Cyan, 8, true);
                glow.color = new Color(NeonStyle.Cyan.r, NeonStyle.Cyan.g, NeonStyle.Cyan.b, 0.1f);
                glow.transform.localScale = Vector3.one * 0.26f;
            }
            else
            {
                ((BoxCollider2D)OwnCollider).enabled = true;
                _rb.velocity = Vector2.zero;
                _rb.angularVelocity = 0f;
                _rb.rotation = 0f;
            }
            return this;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            All.Add(this);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            All.Remove(this);
        }

        protected override void ReturnToPool() => Pools.Float.Release(this);

        /// <summary>引力球可吸：供 Ball.Gravity 分支遍历施力。</summary>
        public Rigidbody2D Body => _rb;
    }
}
