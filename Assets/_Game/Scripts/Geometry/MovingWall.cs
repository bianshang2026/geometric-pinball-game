using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 移动墙（第三批机关）：不可破坏，沿横/竖方向正弦往复移动（缝隙随时间漂移）。
    /// 受时间球减速与冻结球停摆（第三批控制流球种的全联动示范）。
    /// </summary>
    public class MovingWall : GeometryEntity
    {
        Vector2 _base;
        float _amplitude;
        float _period;
        bool _vertical;
        float _clock;

        public MovingWall Build(Vector2 pos, float amplitude, float period, bool vertical, GameConfig cfg)
        {
            Setup(cfg, NeonStyle.Blue);
            name = $"MovingWall({pos.x:0.##},{pos.y:0.##})";
            _base = pos;
            transform.position = new Vector3(pos.x, pos.y, 0f);
            _amplitude = amplitude;
            _period = Mathf.Max(0.5f, period);
            _vertical = vertical;
            indestructible = true;
            punchOnHit = false;

            float len = 1.6f;
            var col = gameObject.AddComponent<BoxCollider2D>();
            col.size = vertical ? new Vector2(0.3f, len) : new Vector2(len, 0.3f);
            col.sharedMaterial = null;
            OwnCollider = col;

            var fill = NeonStyle.MakeSprite(transform, "Fill", ProcSprites.SquareFill(),
                new Color(NeonStyle.Blue.r, NeonStyle.Blue.g, NeonStyle.Blue.b, 0.3f), 9, false);
            fill.transform.localScale = vertical ? new Vector3(0.3f, len, 1f) : new Vector3(len, 0.3f, 1f);
            var outline = NeonStyle.MakeSprite(transform, "Outline", ProcSprites.SquareOutline(), NeonStyle.Blue, 10, false);
            outline.transform.localScale = vertical ? new Vector3(0.3f, len, 1f) : new Vector3(len, 0.3f, 1f);
            var glow = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), NeonStyle.Blue, 8, true);
            glow.color = new Color(NeonStyle.Blue.r, NeonStyle.Blue.g, NeonStyle.Blue.b, 0.14f);
            glow.transform.localScale = Vector3.one * 0.5f;
            return this;
        }

        void Update()
        {
            if (Frozen) return;                                          // 冻结停摆
            _clock += Time.deltaTime * MechanismTime.Scale;             // 时间球减速
            float k = Mathf.Sin(_clock * (2f * Mathf.PI) / _period);
            var off = _vertical ? new Vector2(0f, k * _amplitude) : new Vector2(k * _amplitude, 0f);
            transform.position = new Vector3(_base.x + off.x, _base.y + off.y, 0f);
        }
    }
}
