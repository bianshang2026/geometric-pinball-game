using UnityEngine;
using TMPro;

namespace GeoBreaker
{
    /// <summary>正式方块：HP + 中央数字标签 + 受击浮动伤害数字 + 碎裂死亡。
    /// P11 池化：Build 幂等（首建组件/子物体，重建只复位状态），死亡回 Pools.Block。</summary>
    public class GeometryBlock : GeometryEntity
    {
        TextMeshPro _label;
        SpriteRenderer _fill;
        SpriteRenderer _outline;
        SpriteRenderer _glow;
        BoxCollider2D _col;
        bool _built;

        public GeometryBlock Build(Vector2 pos, float hpValue, GameConfig cfg, PhysicsMaterial2D bouncy,
            float fillScale = 0.92f)
        {
            Setup(cfg, NeonStyle.Cyan);
            name = $"Block({pos.x:0.##},{pos.y:0.##})";
            transform.SetPositionAndRotation(new Vector3(pos.x, pos.y, 0f), Quaternion.identity);
            transform.localScale = Vector3.one;             // P11：复用前复位（护盾块曾受父级缩放补偿）
            indestructible = false;
            punchOnHit = true;
            hp = hpValue;
            Alive = true;

            if (!_built)
            {
                _built = true;
                _col = gameObject.AddComponent<BoxCollider2D>();
                _col.size = new Vector2(0.92f, 0.92f);
                OwnCollider = _col;

                _fill = NeonStyle.MakeSprite(transform, "Fill", ProcSprites.SquareFill(), NeonStyle.BlockFill, 9, false);
                _outline = NeonStyle.MakeSprite(transform, "Outline", ProcSprites.SquareOutline(), NeonStyle.Cyan, 10, false);
                _glow = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), NeonStyle.Cyan, 8, true);
                _glow.color = new Color(NeonStyle.Cyan.r, NeonStyle.Cyan.g, NeonStyle.Cyan.b, 0.08f);
                _glow.transform.localScale = Vector3.one * 0.29f;

                var labelGo = new GameObject("HP", typeof(TextMeshPro));
                labelGo.transform.SetParent(transform, false);
                _label = labelGo.GetComponent<TextMeshPro>();
                _label.fontSize = 12;
                _label.transform.localScale = Vector3.one * 0.35f;
                _label.alignment = TextAlignmentOptions.Center;
                _label.color = new Color(1f, 1f, 1f, 0.92f);
                _label.GetComponent<MeshRenderer>().sortingOrder = 12;
            }
            else
            {
                _col.enabled = true;                        // P11：复用重置
            }
            _col.sharedMaterial = bouncy;

            // 每次构建重设填充/描边尺寸（普通块 0.92 / 核心&Boss 护盾块 0.55）
            var s = new Vector3(fillScale, fillScale, 1f);
            _fill.transform.localScale = s;
            _outline.transform.localScale = s;
            _label.text = Mathf.Max(1, Mathf.RoundToInt(hp)).ToString();
            return this;
        }

        public override void TakeDamage(float damage, Vector2 point)
        {
            if (!Alive || indestructible) return;
            base.TakeDamage(damage, point);
            if (Alive)
                _label.text = Mathf.Max(0, Mathf.CeilToInt(hp)).ToString();
            if (FloatingText.I != null)
                FloatingText.I.Spawn(point, Mathf.Max(1, Mathf.RoundToInt(damage)).ToString(), NeonStyle.Orange);
        }

        /// <summary>P11 池化：死亡回 Pools.Block（StageBuilder/BossController 复用）。</summary>
        protected override void ReturnToPool() => Pools.Block.Release(this);
    }
}
