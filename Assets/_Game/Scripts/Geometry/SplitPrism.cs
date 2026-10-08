using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 分裂棱镜△（第三批机关）：透明触发区。任何球穿过 → 分裂出两个不同方向的
    /// 不耗库存子球（±35° 保速）——机关流分裂源；激光球穿过=双激光分身。
    /// 全局短冷却防同球多次触发。
    /// </summary>
    public class SplitPrism : GeometryEntity
    {
        static float _lastSplitAt = -999f;

        public SplitPrism Build(Vector2 pos, GameConfig cfg)
        {
            Setup(cfg, NeonStyle.Portal);
            name = $"SplitPrism({pos.x:0.##},{pos.y:0.##})";
            transform.position = new Vector3(pos.x, pos.y, 0f);
            indestructible = true;
            punchOnHit = false;

            var col = gameObject.AddComponent<CircleCollider2D>();
            col.radius = 0.45f;
            col.isTrigger = true;                     // 穿过触发（不反弹）
            OwnCollider = col;

            var pc = NeonStyle.Portal;
            var tri = NeonStyle.MakeSprite(transform, "Tri", ProcSprites.TriangleOutline(), pc, 10, true);
            tri.color = new Color(pc.r, pc.g, pc.b, 0.4f);
            tri.transform.localScale = Vector3.one * 1.1f;
            var glow = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), pc, 8, true);
            glow.color = new Color(pc.r, pc.g, pc.b, 0.12f);
            glow.transform.localScale = Vector3.one * 1.2f;
            return this;
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            var ball = other.GetComponent<Ball>();
            if (ball == null || !ball.IsLive) return;
            if (Time.time - _lastSplitAt < 0.4f) return;               // 机关全局冷却
            if (BallManager.I == null || !BallManager.I.CanSpawnChild) return;
            var v = (Vector2)ball.RB.velocity;
            if (v.sqrMagnitude < 0.01f) return;
            _lastSplitAt = Time.time;

            Vector2 dir = v.normalized;
            Vector2 origin = ball.transform.position;
            float speed = Mathf.Max(v.magnitude, ball.TargetSpeed);
            int spawned = 0;
            foreach (float a in new[] { 35f, -35f })
            {
                if (!BallManager.I.CanSpawnChild) break;
                float r = a * Mathf.Deg2Rad;
                float cs = Mathf.Cos(r), sn = Mathf.Sin(r);
                Vector2 d = new Vector2(dir.x * cs - dir.y * sn, dir.x * sn + dir.y * cs);
                Vector2 pos = origin + d * (ball.Data.radius + 0.18f);
                var child = BallManager.I.SpawnChild(ball, pos, d);
                if (child != null)
                {
                    child.TargetSpeed = speed;
                    spawned++;
                }
            }
            if (spawned > 0)
            {
                var fx = EffectManager.I;
                if (fx != null) fx.Flash(origin, NeonStyle.Portal, 1.5f, 0.2f);
                if (FloatingText.I != null)
                    FloatingText.I.Spawn(origin + Vector2.up * 0.7f, "分光", NeonStyle.Portal, 0.5f);
            }
        }

        void Update()
        {
            transform.Rotate(0f, 0f, 40f * MechanismTime.Scale * Time.deltaTime, Space.Self);   // 缓慢自转+时缓联动
        }
    }
}
