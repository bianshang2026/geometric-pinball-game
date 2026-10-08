using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 单向墙（文档十二）：允许方向一侧穿透通过，反方向正常反弹。
    /// 穿透逻辑复用 Phase 5 管线：Ball 检测到正向入射时 IgnoreCollision + 恢复入射方向。
    /// forward = rotationDeg 旋转后的右方向。
    /// </summary>
    public class OneWayWall : GeometryEntity
    {
        public Vector2 Forward { get; private set; }

        public OneWayWall Build(Vector2 center, float rotationDeg, float length, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            Setup(cfg, NeonStyle.Blue);
            name = $"OneWayWall({center.x:0.##},{center.y:0.##})";
            transform.position = new Vector3(center.x, center.y, 0f);
            transform.localRotation = Quaternion.Euler(0f, 0f, rotationDeg);
            indestructible = true;
            punchOnHit = false;                       // 细墙禁用脉冲（避免推动球）
            Forward = (Vector2)(Quaternion.Euler(0f, 0f, rotationDeg) * Vector3.right);

            var col = gameObject.AddComponent<BoxCollider2D>();
            col.size = new Vector2(length, 0.1f);
            col.sharedMaterial = bouncy;
            OwnCollider = col;

            // 视觉：主线 + 沿线箭头三角（指示允许穿过的方向）
            var glow = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), NeonStyle.Blue, 4, true);
            glow.color = new Color(NeonStyle.Blue.r, NeonStyle.Blue.g, NeonStyle.Blue.b, 0.10f);
            glow.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            glow.transform.localScale = new Vector3(0.10f, length / 4f + 0.1f, 1f);

            var line = NeonStyle.MakeSprite(transform, "Line", ProcSprites.SquareFill(), NeonStyle.Blue, 12, false);
            line.transform.localScale = new Vector3(length, 0.06f, 1f);

            int arrows = Mathf.Max(2, Mathf.RoundToInt(length / 0.45f));
            for (int i = 0; i < arrows; i++)
            {
                float t = (i + 0.5f) / arrows - 0.5f;             // -0.5..0.5
                var a = NeonStyle.MakeSprite(transform, "Arrow", ProcSprites.Triangle(), NeonStyle.Blue, 13, false);
                a.color = new Color(NeonStyle.Blue.r, NeonStyle.Blue.g, NeonStyle.Blue.b, 0.85f);
                a.transform.localPosition = new Vector3(t * length, 0.09f, 0f);
                a.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);   // 三角尖朝 forward(+x)
                a.transform.localScale = Vector3.one * 0.12f;
            }
            return this;
        }
    }
}
