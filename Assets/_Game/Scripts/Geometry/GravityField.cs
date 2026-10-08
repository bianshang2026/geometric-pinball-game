using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 力场（文档五/六）：范围内球持续受向心/离心力，轨迹由直线变弧线。
    /// polarity=+1 引力圆（青），-1 斥力圆（橙）。无碰撞体——纯力场。
    /// 球既有速度归一化只锚定速率，方向由力场塑造，天然兼容。
    /// </summary>
    public class GravityField : GeometryEntity
    {
        public int polarity = 1;                       // 1=引力 -1=斥力
        float _radius;
        float _strengthOverride;                      // >0 时覆盖 cfg 强度（Boss 黑洞注入）
        Transform _spin;

        public GravityField Build(Vector2 pos, int polarityIn, GameConfig cfg)
        {
            Setup(cfg, polarityIn >= 0 ? NeonStyle.Cyan : NeonStyle.Orange);
            polarity = polarityIn >= 0 ? 1 : -1;
            name = (polarity >= 0 ? "GravityField(" : "Repulsor(") + pos.x + "," + pos.y + ")";
            transform.position = new Vector3(pos.x, pos.y, 0f);
            indestructible = true;
            punchOnHit = false;                       // 无碰撞体，永远不会被击中
            _radius = cfg.gravityFieldRadius;

            // 视觉：双环（外淡内浓）+ 中心点 + 缓慢旋转
            var outer = NeonStyle.MakeSprite(transform, "OuterRing", ProcSprites.Ring(), MainColor, 6, true);
            outer.color = new Color(MainColor.r, MainColor.g, MainColor.b, 0.30f);
            outer.transform.localScale = Vector3.one * _radius;          // Ring 贴图2单位 → 直径=2r

            var inner = NeonStyle.MakeSprite(transform, "InnerRing", ProcSprites.Ring(), MainColor, 6, true);
            inner.color = new Color(MainColor.r, MainColor.g, MainColor.b, 0.55f);
            inner.transform.localScale = Vector3.one * _radius * 0.45f;

            var glow = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), MainColor, 5, true);
            glow.color = new Color(MainColor.r, MainColor.g, MainColor.b, 0.20f);
            glow.transform.localScale = Vector3.one * (_radius * 0.6f);

            var core = NeonStyle.MakeSprite(transform, "CenterDot", ProcSprites.CircleSoft(), MainColor, 7, false);
            core.color = new Color(MainColor.r, MainColor.g, MainColor.b, 0.9f);
            core.transform.localScale = Vector3.one * 0.16f;
            _spin = outer.transform;
            return this;
        }

        /// <summary>Boss 黑洞等动态场：注入强度/半径（BossController 调用）。</summary>
        public void SetStrength(float strength, float radius)
        {
            _strengthOverride = strength;
            _radius = radius;
            var outer = transform.Find("OuterRing");
            if (outer != null) outer.localScale = Vector3.one * _radius;
            var inner = transform.Find("InnerRing");
            if (inner != null) inner.localScale = Vector3.one * _radius * 0.45f;
        }

        void FixedUpdate()
        {
            var bm = BallManager.I;
            if (bm == null) return;
            float strength = _strengthOverride > 0f ? _strengthOverride : _cfg.gravityFieldStrength;
            Vector2 c = transform.position;
            foreach (var ball in bm.GetComponentsInChildren<Ball>(true))
            {
                if (ball == null || !ball.IsLive || ball.RB == null) continue;
                Vector2 d = c - (Vector2)ball.transform.position;
                float dist = d.magnitude;
                if (dist > _radius || dist < _cfg.gravityFieldMinDist) continue;
                float falloff = 1f - dist / _radius;                 // 近强远弱
                Vector2 force = d / dist * (polarity * strength * falloff);
                ball.RB.AddForce(force, ForceMode2D.Force);
            }
        }

        void Update()
        {
            if (_spin != null) _spin.Rotate(0f, 0f, polarity * 25f * Time.deltaTime, Space.Self);
        }
    }
}
