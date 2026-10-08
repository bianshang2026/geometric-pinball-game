using UnityEngine;

namespace GeoBreaker
{
    /// <summary>传送门节点：触发圆 + 旋转视觉（轨迹预测器靠本组件识别类型）。</summary>
    public class PortalNode : MonoBehaviour
    {
        public PortalPair Pair;
        public PortalNode Other;
        public float Radius;

        Transform _spin;

        public void SetSpin(Transform spin) => _spin = spin;

        void Update()
        {
            if (_spin != null) _spin.Rotate(0f, 0f, 70f * Time.deltaTime, Space.Self);
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            var ball = other.GetComponent<Ball>();
            if (ball == null || !ball.IsLive || Pair == null || Other == null) return;
            if (Time.time - ball.LastTeleportTime < Pair.CooldownSeconds) return;
            Pair.Teleport(this, ball);
        }
    }

    /// <summary>
    /// 圆环传送门◯：成对出现；球进入一端，从另一端保持速度矢量射出。
    /// 出口偏移 = 半径+球半径+安全边，冷却防止 A↔B 往返抖动。紫色双环旋转。
    /// </summary>
    public class PortalPair : MonoBehaviour
    {
        GameConfig _cfg;

        public float CooldownSeconds => _cfg != null ? _cfg.portalCooldownSeconds : 0.25f;

        public PortalPair Build(Vector2 posA, Vector2 posB, GameConfig cfg)
        {
            _cfg = cfg;
            name = "Portals";
            var a = MakeNode("PortalA", posA);
            var b = MakeNode("PortalB", posB);
            a.Pair = this; b.Pair = this;
            a.Other = b; b.Other = a;
            return this;
        }

        PortalNode MakeNode(string n, Vector2 pos)
        {
            var go = new GameObject(n);
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(pos.x, pos.y, 0f);

            var col = go.AddComponent<CircleCollider2D>();
            col.radius = _cfg.portalRadius;
            col.isTrigger = true;

            var node = go.AddComponent<PortalNode>();
            node.Radius = _cfg.portalRadius;

            var spin = new GameObject("Spin").transform;
            spin.SetParent(go.transform, false);
            node.SetSpin(spin);

            Color pc = NeonStyle.Portal;
            var ring = NeonStyle.MakeSprite(spin, "Ring", ProcSprites.Ring(), pc, 6, true);
            ring.color = new Color(pc.r, pc.g, pc.b, 0.9f);
            ring.transform.localScale = Vector3.one * _cfg.portalRadius;      // Ring 贴图2单位 → 直径=2×半径

            var ring2 = NeonStyle.MakeSprite(spin, "RingInner", ProcSprites.Ring(), pc, 6, true);
            ring2.color = new Color(pc.r, pc.g, pc.b, 0.45f);
            ring2.transform.localScale = Vector3.one * (_cfg.portalRadius * 0.62f);

            var glow = NeonStyle.MakeSprite(spin, "Glow", ProcSprites.Glow(), pc, 5, true);
            glow.color = new Color(pc.r, pc.g, pc.b, 0.16f);
            glow.transform.localScale = Vector3.one * (_cfg.portalRadius * 1.4f);

            var core = NeonStyle.MakeSprite(spin, "Core", ProcSprites.CircleSoft(), Color.white, 7, false);
            core.color = new Color(1f, 1f, 1f, 0.65f);
            core.transform.localScale = Vector3.one * 0.12f;

            return node;
        }

        public void Teleport(PortalNode from, Ball ball)
        {
            if (from == null || from.Other == null || ball == null || ball.RB == null) return;
            PortalNode exit = from.Other;
            ball.LastTeleportTime = Time.time;

            Vector2 v = ball.RB.velocity;
            Vector2 dir = v.sqrMagnitude > 0.01f ? v.normalized : Vector2.up;
            ball.RB.position = (Vector2)exit.transform.position + dir * (from.Radius + ball.Data.radius + 0.06f);
            ball.RB.velocity = v;                                   // 速度矢量保持
            AudioManager.PlayPortal();                              // 构筑·传送门音

            var fx = EffectManager.I;
            if (fx != null)
            {
                Vector3 a = from.transform.position;
                Vector3 b = exit.transform.position;
                fx.SpawnSparks(a, dir, 10, 1.2f, NeonStyle.Portal);
                fx.SpawnSparks(b, dir, 10, 1.2f, NeonStyle.Portal);
                fx.PlayRing(a, NeonStyle.Portal, 0.4f, 0.12f, 0.22f);
                fx.PlayRing(b, NeonStyle.Portal, 0.12f, 0.42f, 0.22f);
                fx.Flash(a, NeonStyle.Portal, 1.3f, 0.15f);
                fx.Flash(b, NeonStyle.Portal, 1.3f, 0.15f);
            }
        }
    }
}
