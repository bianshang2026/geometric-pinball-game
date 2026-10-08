using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    public enum EdgeRule { Normal, SpeedUp, SlowDown }

    /// <summary>
    /// 三角反射器△：不可摧毁。三条边独立反弹规则：
    /// Normal=正常反弹 | SpeedUp=加速边(黄) | SlowDown=减速边(蓝)。
    /// 命中边 = 接触法线与各边外法线最大点积。支持可选自转（rotationSpeedDeg）。
    /// 视觉 v2：玻璃底+双层描边+规则边三层霓虹管+外向符号（加速=箭头外涌/减速=刻度慢闪），时缓/冻结联动。
    /// </summary>
    public class TriangleReflector : GeometryEntity
    {
        public float rotationSpeedDeg = 0f;

        readonly Vector2[] _edgeNormals = new Vector2[3];   // 局部系边外法线
        readonly EdgeRule[] _rules = new EdgeRule[3];        // [0]底 [1]右 [2]左

        // 视觉 v2 动画部件（规则边专属；普通边为静态细线）
        sealed class Glyph
        {
            public SpriteRenderer sr;
            public Vector2 basePos;    // 边上锚点（本地系）
            public Vector2 outDir;     // 边外法线（本地系）
            public int index;
        }
        readonly List<Glyph> _flow = new List<Glyph>();                     // 加速边外向箭头
        readonly List<Glyph> _ticks = new List<Glyph>();                   // 减速边刻度
        readonly List<SpriteRenderer> _tubes = new List<SpriteRenderer>(); // 规则边霓虹管（呼吸）
        float _animClock;

        public TriangleReflector Build(Vector2 pos, float rotationDeg, GameConfig cfg,
            PhysicsMaterial2D bouncy, EdgeRule bottom, EdgeRule left, EdgeRule right)
        {
            Setup(cfg, NeonStyle.Cyan);
            name = $"Triangle({pos.x:0.##},{pos.y:0.##})";
            transform.position = new Vector3(pos.x, pos.y, 0f);
            transform.localRotation = Quaternion.Euler(0f, 0f, rotationDeg);
            indestructible = true;

            const float s = 0.92f;
            Vector2 top = new Vector2(0f, s * 0.58f);
            Vector2 bl = new Vector2(-s * 0.5f, -s * 0.29f);
            Vector2 br = new Vector2(s * 0.5f, -s * 0.29f);

            var poly = gameObject.AddComponent<PolygonCollider2D>();
            poly.points = new[] { top, bl, br };
            poly.sharedMaterial = bouncy;
            OwnCollider = poly;

            // 边定义：[0]底 bl->br  [1]右 br->top  [2]左 top->bl
            Vector2 centroid = (top + bl + br) / 3f;
            Vector2[] a = { bl, br, top };
            Vector2[] b = { br, top, bl };
            for (int i = 0; i < 3; i++)
            {
                Vector2 e = b[i] - a[i];
                Vector2 n = new Vector2(-e.y, e.x).normalized;
                if (Vector2.Dot(n, (a[i] + b[i]) * 0.5f - centroid) < 0f) n = -n;
                _edgeNormals[i] = n;
                _rules[i] = i == 0 ? bottom : (i == 1 ? right : left);
            }

            // 视觉 v2：玻璃底 → 亮描边 → 内嵌套描边 → 光晕 → 规则边三层霓虹管+外向符号
            var fillCol = NeonStyle.BlockFill; fillCol.a = 0.5f;
            NeonStyle.MakeSprite(transform, "Fill", ProcSprites.TriangleFill(), fillCol, 9, false);

            NeonStyle.MakeSprite(transform, "Outline", ProcSprites.TriangleOutline(), NeonStyle.Cyan, 10, false);

            var inner = NeonStyle.MakeSprite(transform, "Inner", ProcSprites.TriangleOutline(), NeonStyle.Cyan, 10, true);
            inner.color = new Color(NeonStyle.Cyan.r, NeonStyle.Cyan.g, NeonStyle.Cyan.b, 0.22f);
            inner.transform.localScale = Vector3.one * 0.55f;

            var glow = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), NeonStyle.Cyan, 8, true);
            glow.color = new Color(NeonStyle.Cyan.r, NeonStyle.Cyan.g, NeonStyle.Cyan.b, 0.13f);
            glow.transform.localScale = Vector3.one * 0.3f;

            for (int i = 0; i < 3; i++)
            {
                Color c = _rules[i] == EdgeRule.SpeedUp ? NeonStyle.Yellow
                        : _rules[i] == EdgeRule.SlowDown ? NeonStyle.Blue
                        : new Color(NeonStyle.Cyan.r, NeonStyle.Cyan.g, NeonStyle.Cyan.b, 0.35f);
                MakeEdgeBar(a[i], b[i], c, _edgeNormals[i], _rules[i]);
            }
            return this;
        }

        void MakeEdgeBar(Vector2 a, Vector2 b, Color c, Vector2 n, EdgeRule rule)
        {
            Vector2 mid = (a + b) * 0.5f;
            float len = Vector2.Distance(a, b);
            float ang = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;

            if (rule == EdgeRule.Normal)                          // 普通边：静态细线即可
            {
                var core = NeonStyle.MakeSprite(transform, "Edge", ProcSprites.SquareFill(), c, 13, false);
                PlaceBar(core, mid, ang, new Vector3(len, 0.07f, 1f));
                return;
            }

            // 规则边：三层霓虹管（外晕/管/芯）
            var halo = NeonStyle.MakeSprite(transform, "Halo", ProcSprites.SquareFill(), c, 11, true);
            PlaceBar(halo, mid, ang, new Vector3(len, 0.36f, 1f));
            SetSrAlpha(halo, 0.10f);

            var tube = NeonStyle.MakeSprite(transform, "Tube", ProcSprites.SquareFill(), c, 12, true);
            PlaceBar(tube, mid, ang, new Vector3(len, 0.17f, 1f));
            SetSrAlpha(tube, 0.32f);
            _tubes.Add(tube);

            var edgeCore = NeonStyle.MakeSprite(transform, "Edge", ProcSprites.SquareFill(), c, 13, false);
            PlaceBar(edgeCore, mid, ang, new Vector3(len, 0.08f, 1f));

            // 外向符号：加速边=外涌箭头，减速边=阻尼刻度
            for (int i = 0; i < 3; i++)
            {
                Vector2 anchor = Vector2.Lerp(a, b, 0.2f + 0.3f * i);
                var g = new Glyph { basePos = anchor, outDir = n, index = i };
                if (rule == EdgeRule.SpeedUp)
                {
                    var sr = NeonStyle.MakeSprite(transform, "Chev", ProcSprites.Triangle(), c, 14, true);
                    PlaceBar(sr, anchor, Mathf.Atan2(n.x, -n.y) * Mathf.Rad2Deg, Vector3.one * 0.24f);
                    g.sr = sr;
                    _flow.Add(g);
                }
                else
                {
                    var sr = NeonStyle.MakeSprite(transform, "Tick", ProcSprites.SquareFill(), c, 14, true);
                    PlaceBar(sr, anchor, Mathf.Atan2(n.y, n.x) * Mathf.Rad2Deg, new Vector3(0.06f, 0.18f, 1f));
                    g.sr = sr;
                    _ticks.Add(g);
                }
            }
        }

        static void PlaceBar(SpriteRenderer sr, Vector2 pos, float deg, Vector3 scale)
        {
            sr.transform.localPosition = pos;
            sr.transform.localRotation = Quaternion.Euler(0f, 0f, deg);
            sr.transform.localScale = scale;
        }

        static void SetSrAlpha(SpriteRenderer sr, float a)
        {
            var col = sr.color; col.a = a; sr.color = col;
        }

        protected override void OnBallCollide(BallCollisionEvent e)
        {
            int best = 0;
            float bestDot = -10f;
            for (int i = 0; i < 3; i++)
            {
                Vector2 wn = transform.TransformVector(_edgeNormals[i]).normalized;
                float d = Vector2.Dot(e.normal, wn);
                if (d > bestDot) { bestDot = d; best = i; }
            }
            ApplyRule(best, e);
        }

        void ApplyRule(int edgeIdx, BallCollisionEvent e)
        {
            var rule = _rules[edgeIdx];
            if (rule == EdgeRule.Normal) return;
            var ball = e.ball;
            if (ball == null || ball.RB == null || !ball.IsLive) return;

            float factor = rule == EdgeRule.SpeedUp ? _cfg.triangleSpeedFactor : _cfg.triangleSlowFactor;
            ball.TargetSpeed = Mathf.Clamp(ball.TargetSpeed * factor, _cfg.minSpeed, _cfg.maxSpeed);
            ball.RB.velocity = ball.RB.velocity.normalized * ball.TargetSpeed;

            Color c = rule == EdgeRule.SpeedUp ? NeonStyle.Yellow : NeonStyle.Blue;
            if (EffectManager.I != null)
                EffectManager.I.Flash(e.point, c, 1.2f, 0.16f);
        }

        void Update()
        {
            if (rotationSpeedDeg != 0f && !Frozen)
                transform.Rotate(0f, 0f, rotationSpeedDeg * MechanismTime.Scale * Time.deltaTime, Space.Self);   // 时间球：减速

            if (Frozen) return;                                  // 冻结球：全部动画停
            _animClock += Time.deltaTime * MechanismTime.Scale;  // 时间球：动画同步减速

            for (int i = 0; i < _tubes.Count; i++)               // 规则边霓虹管呼吸
                SetSrAlpha(_tubes[i], 0.28f + 0.10f * (0.5f + 0.5f * Mathf.Sin(_animClock * 3f)));

            for (int i = 0; i < _flow.Count; i++)                 // 加速边：箭头沿法线外涌 + 波动
            {
                var g = _flow[i];
                float w = 0.5f + 0.5f * Mathf.Sin(Mathf.Repeat(_animClock * 1.1f + g.index / 3f, 1f) * Mathf.PI * 2f);
                g.sr.transform.localPosition = g.basePos + g.outDir * (0.13f + 0.09f * w);
                SetSrAlpha(g.sr, 0.10f + 0.85f * w);
            }

            for (int i = 0; i < _ticks.Count; i++)                // 减速边：刻度慢闪
            {
                var g = _ticks[i];
                float w = 0.5f + 0.5f * Mathf.Sin(_animClock * 2.2f + g.index * 2.1f);
                SetSrAlpha(g.sr, 0.20f + 0.55f * w);
            }
        }
    }
}
