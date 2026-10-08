using System.Collections;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 镜面╱：薄斜线（EdgeCollider2D），完全反射（弹性物理天然支持），
    /// 白色高亮，被击中时白闪。用于精确路线规划。
    /// </summary>
    public class MirrorLine : GeometryEntity
    {
        SpriteRenderer _line;

        public MirrorLine Build(Vector2 center, float angleDeg, float length, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            Setup(cfg, NeonStyle.Mirror);
            name = $"Mirror({center.x:0.##},{center.y:0.##})";
            transform.position = new Vector3(center.x, center.y, 0f);
            transform.localRotation = Quaternion.Euler(0f, 0f, angleDeg);
            indestructible = true;
            punchOnHit = false;                     // 细线缩放会推动球，禁用受击脉冲

            var edge = gameObject.AddComponent<EdgeCollider2D>();
            edge.points = new[] { new Vector2(-length * 0.5f, 0f), new Vector2(length * 0.5f, 0f) };
            edge.sharedMaterial = bouncy;
            OwnCollider = edge;

            var glow = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), NeonStyle.Mirror, 4, true);
            glow.color = new Color(NeonStyle.Mirror.r, NeonStyle.Mirror.g, NeonStyle.Mirror.b, 0.10f);
            glow.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            glow.transform.localScale = new Vector3(0.12f, length / 4f + 0.1f, 1f);

            _line = NeonStyle.MakeSprite(transform, "Line", ProcSprites.SquareFill(), NeonStyle.Mirror, 12, false);
            _line.transform.localScale = new Vector3(length, 0.05f, 1f);
            return this;
        }

        protected override void OnBallCollide(BallCollisionEvent e)
            => StartCoroutine(FlashLine());

        IEnumerator FlashLine()
        {
            float t = 0f;
            while (t < 0.35f)
            {
                t += Time.deltaTime;
                _line.color = Color.Lerp(NeonStyle.White, NeonStyle.Mirror, t / 0.35f);
                yield return null;
            }
        }
    }
}
