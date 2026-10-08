using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 能量节点◎（第三批机关）：不可破坏中继器。球击中 → 激活 2.5s——
    /// 全场时间门强制开启 + 半径内炸弹引爆（"门打开/炸弹启动"的解谜联动）。
    /// 视觉 v3（用户反馈 v2 太大太糊）：单环+短辐条+白热核，砍掉第二环与六角徽记，
    /// 整体直径 ~1.25 世界单位（≈一个半方块），环/辐条/核之间留出清晰空隙；激活态增幅；时缓/冻结联动。
    /// </summary>
    public class EnergyNode : GeometryEntity
    {
        float _activeUntil;
        float _lastActivate = -999f;
        SpriteRenderer _core;
        SpriteRenderer _glowBg;
        SpriteRenderer _ring;
        SpriteRenderer[] _spokes;
        Transform _spokesRoot;
        float _animClock;
        float _spinClock;

        public bool IsActive => Time.time < _activeUntil;

        public EnergyNode Build(Vector2 pos, GameConfig cfg)
        {
            Setup(cfg, NeonStyle.Amber);
            name = $"EnergyNode({pos.x:0.##},{pos.y:0.##})";
            transform.position = new Vector3(pos.x, pos.y, 0f);
            indestructible = true;
            punchOnHit = true;

            var col = gameObject.AddComponent<CircleCollider2D>();
            col.radius = 0.42f;
            OwnCollider = col;

            // 视觉 v3：淡地光 → 单旋转环 → 短辐条（环内留隙） → 白热核
            _glowBg = NeonStyle.MakeSprite(transform, "GlowBg", ProcSprites.Glow(), NeonStyle.Amber, 7, true);
            _glowBg.transform.localScale = Vector3.one * 0.8f;

            _ring = NeonStyle.MakeSprite(transform, "Ring", ProcSprites.Ring(), NeonStyle.Amber, 8, true);
            _ring.transform.localScale = Vector3.one * 0.62f;

            _spokesRoot = new GameObject("Spokes").transform;
            _spokesRoot.SetParent(transform, false);
            _spokes = new SpriteRenderer[6];
            for (int i = 0; i < 6; i++)
            {
                float ang = i * 60f * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                var sr = NeonStyle.MakeSprite(_spokesRoot, "Spoke", ProcSprites.SquareFill(), NeonStyle.Amber, 9, true);
                sr.transform.localPosition = dir * 0.32f;
                sr.transform.localRotation = Quaternion.Euler(0f, 0f, i * 60f);
                sr.transform.localScale = new Vector3(0.16f, 0.05f, 1f);
                _spokes[i] = sr;
            }

            _core = NeonStyle.MakeSprite(transform, "Core", ProcSprites.CircleSoft(), NeonStyle.Amber, 10, false);
            _core.transform.localScale = Vector3.one * 0.34f;
            var hot = NeonStyle.MakeSprite(_core.transform, "Hot", ProcSprites.CircleSoft(), Color.white, 11, true);
            hot.transform.localScale = Vector3.one * 0.35f;
            return this;
        }

        protected override void OnBallCollide(BallCollisionEvent e)
        {
            Activate();
        }

        /// <summary>激活（球击入口；public 供测试直接调用）。</summary>
        public void Activate()
        {
            if (Time.time - _lastActivate < 1f) return;           // 连击防抖
            _lastActivate = Time.time;
            _activeUntil = Time.time + _cfg.energyNodeDuration;

            // 联动 1：全场时间门强制开启
            foreach (var ent in All)
                if (ent is TimeGate gate) gate.ForceOpen(_cfg.energyNodeDuration);

            // 联动 2：半径内炸弹引爆（连锁启动）——快照遍历：Die→回池→All.Remove 会破坏遍历
            int bombs = 0;
            Vector2 c = transform.position;
            var snapshot = new System.Collections.Generic.List<GeometryEntity>(All);
            foreach (var ent in snapshot)
            {
                if (ent is BombBlock bomb && ent.Alive
                    && Vector2.Distance(c, ent.transform.position) <= _cfg.energyNodeBlastRadius)
                {
                    bombs++;
                    ent.TakeDamage(999f, ent.transform.position);   // 触发 Die → Explode
                }
            }

            var fx = EffectManager.I;
            if (fx != null)
            {
                fx.Flash(c, NeonStyle.Amber, 2.4f, 0.25f);
                fx.PlayRing(c, NeonStyle.Amber, 0.3f, _cfg.energyNodeBlastRadius * 2f, 0.45f);
                fx.SpawnSparks(c, Vector2.up, 12, 1.4f, NeonStyle.Amber);
            }
            if (FloatingText.I != null)
                FloatingText.I.Spawn(c + Vector2.up * 0.8f, bombs > 0 ? "引暴 ×" + bombs : "供能", NeonStyle.Amber, 0.55f);
        }

        void Update()
        {
            if (_core == null) return;
            if (Frozen) return;                                  // 冻结球：动画全停
            float dt = Time.deltaTime * MechanismTime.Scale;    // 时间球：动画减速
            _animClock += dt;
            float boost = IsActive ? 2.6f : 1f;                  // 激活态增幅（速度/亮度/半径）
            _spinClock += dt * 60f * boost;                      // 累积式旋转：boost 变化无跳变

            _ring.transform.localRotation = Quaternion.Euler(0f, 0f, _spinClock * 0.66f);
            SetAlpha(_ring, IsActive ? 0.78f : 0.45f);

            _spokesRoot.localRotation = Quaternion.Euler(0f, 0f, -_spinClock * 0.30f);   // 辐条整体反向缓旋
            for (int i = 0; i < _spokes.Length; i++)
            {
                float ang = i * 60f * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                float w = 0.5f + 0.5f * Mathf.Sin(_animClock * 3.2f + i * (Mathf.PI / 3f));
                _spokes[i].transform.localPosition = dir * (0.32f + 0.06f * w + (IsActive ? 0.06f : 0f));
                SetAlpha(_spokes[i], 0.35f + 0.50f * w + (IsActive ? 0.15f : 0f));
            }

            float k = IsActive
                ? 1.06f + 0.14f * Mathf.Sin(_animClock * 14f)
                : 0.95f + 0.05f * Mathf.Sin(_animClock * 3f);
            _core.transform.localScale = Vector3.one * (0.34f * k);
            _core.color = new Color(NeonStyle.Amber.r, NeonStyle.Amber.g, NeonStyle.Amber.b, IsActive ? 1f : 0.7f);

            SetAlpha(_glowBg, 0.06f + 0.02f * Mathf.Sin(_animClock * 2f) + (IsActive ? 0.06f : 0f));
        }

        static void SetAlpha(SpriteRenderer sr, float a)
        {
            var col = sr.color; col.a = a; sr.color = col;
        }
    }
}
