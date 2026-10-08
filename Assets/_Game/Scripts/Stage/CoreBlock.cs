using UnityEngine;
using TMPro;

namespace GeoBreaker
{
    /// <summary>
    /// 关卡核心◎：击破=立即胜利（方块只是路线与保护，不是目标）。
    /// 视觉 = 六边形轮廓 + 双旋转环 + 白色内核 + 光晕 + HP 数字。
    /// 可被球击/爆炸伤害（走 GeometryEntity 通用管线，连锁乘数生效）。
    /// </summary>
    public class CoreBlock : GeometryEntity
    {
        TextMeshPro _label;
        Transform _outerRing;
        Transform _innerRing;
        Transform _glow;
        float _glowScale;

        // Boss 护盾（文档二十八）：护盾存活时核心免疫
        readonly System.Collections.Generic.List<GeometryEntity> _shields =
            new System.Collections.Generic.List<GeometryEntity>();

        public void AttachShield(GeometryEntity shield)
        {
            if (shield != null) _shields.Add(shield);
        }

        /// <summary>存活护盾数（每帧清理已死亡的）。</summary>
        public int LiveShields
        {
            get
            {
                _shields.RemoveAll(s => s == null || !s.Alive);
                return _shields.Count;
            }
        }

        // Boss 旋转弱点（Phase 10）：护盾全灭后生效——弧内暴击 / 弧外护甲
        Transform _weakArc;
        float _arcSpeedDeg;
        float _arcCritMult = 2f;
        float _arcArmorMult = 0.2f;
        bool _arcOn = true;
        public bool WeakArcActive => _weakArc != null && _arcOn;
        /// <summary>弱点弧中心方向（度；以"上"为 0°，与 transform 旋转同基）。</summary>
        public float WeakArcAngleDeg => _weakArc != null ? _weakArc.eulerAngles.z : 0f;
        /// <summary>Boss 阶段数据驱动开关（BossController 置 false 时 Build 不建弧，由阶段注入）。</summary>
        public bool WeakArcEnabledByBoss = true;

        /// <summary>阶段参数注入（BossController 按阶段调用；弧视觉随开关显隐，引用保留可再开）。</summary>
        public void SetWeakArc(bool arcOn, float speedDeg, float critMult, float armorMult)
        {
            _arcOn = arcOn;
            _arcSpeedDeg = speedDeg;
            _arcCritMult = critMult;
            _arcArmorMult = armorMult;
            if (_weakArc != null) _weakArc.gameObject.SetActive(arcOn);
        }

        public CoreBlock Build(Vector2 pos, float hpValue, GameConfig cfg, PhysicsMaterial2D bouncy,
            bool eliteMark = false, float scale = 1f)
        {
            Setup(cfg, NeonStyle.Cyan);
            name = "Core";
            transform.position = new Vector3(pos.x, pos.y, 0f);
            transform.localScale = Vector3.one * scale;    // Boss 巨型核心：视觉+碰撞体同步放大
            indestructible = false;
            hp = hpValue;
            collisionPunch = 0.08f / scale;                // 放大后脉冲幅度回归

            var col = gameObject.AddComponent<CircleCollider2D>();
            col.radius = 0.7f;
            col.sharedMaterial = bouncy;
            OwnCollider = col;

            var hex = NeonStyle.MakeSprite(transform, "Hex", ProcSprites.HexagonOutline(), NeonStyle.Cyan, 10, false);
            hex.transform.localScale = Vector3.one * 1.05f;

            var outerSr = NeonStyle.MakeSprite(transform, "OuterRing", ProcSprites.Ring(), NeonStyle.Cyan, 9, true);
            outerSr.color = new Color(NeonStyle.Cyan.r, NeonStyle.Cyan.g, NeonStyle.Cyan.b, 0.8f);
            _outerRing = outerSr.transform;
            _outerRing.localScale = Vector3.one * 0.72f;        // Ring 贴图2单位 → 直径1.44

            var innerSr = NeonStyle.MakeSprite(transform, "InnerRing", ProcSprites.Ring(), NeonStyle.White, 9, true);
            innerSr.color = new Color(1f, 1f, 1f, 0.5f);
            _innerRing = innerSr.transform;
            _innerRing.localScale = Vector3.one * 0.44f;

            var core = NeonStyle.MakeSprite(transform, "CoreDot", ProcSprites.CircleSoft(), Color.white, 11, false);
            core.transform.localScale = Vector3.one * 0.34f;

            var glowSr = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), NeonStyle.Cyan, 8, true);
            glowSr.color = new Color(NeonStyle.Cyan.r, NeonStyle.Cyan.g, NeonStyle.Cyan.b, 0.16f);
            _glow = glowSr.transform;
            _glowScale = 0.52f;                                  // 光晕直径约2.1
            _glow.localScale = Vector3.one * _glowScale;

            // Boss 旋转弱点弧：10 段琥珀色弧条绕核旋转（本地半径 0.95，世界=×scale）
            // P8：BossController 接管阶段弧（WeakArcEnabledByBoss=false 则不建，由 SetWeakArc/Rebuild 注入）
            if (scale > 1.001f && WeakArcEnabledByBoss)
                BuildWeakArc(cfg.bossWeakArcAngle);

            var labelGo = new GameObject("HP", typeof(TextMeshPro));
            labelGo.transform.SetParent(transform, false);
            _label = labelGo.GetComponent<TextMeshPro>();
            _label.fontSize = 12;
            _label.transform.localScale = Vector3.one * 0.4f;
            _label.alignment = TextAlignmentOptions.Center;
            _label.color = new Color(1f, 1f, 1f, 0.95f);
            _label.text = Mathf.Max(1, Mathf.RoundToInt(hp)).ToString();
            _label.GetComponent<MeshRenderer>().sortingOrder = 12;

            // 精英标记（Phase 6）
            if (eliteMark)
            {
                var tagGo = new GameObject("EliteTag", typeof(TextMeshPro));
                tagGo.transform.SetParent(transform, false);
                var tag = tagGo.GetComponent<TextMeshPro>();
                tag.fontSize = 12;
                tag.transform.localScale = Vector3.one * 0.38f;
                tag.transform.localPosition = new Vector3(0f, -1.15f, 0f);
                tag.alignment = TextAlignmentOptions.Center;
                tag.color = NeonStyle.Orange;
                tag.text = "精英";
                tag.GetComponent<MeshRenderer>().sortingOrder = 13;
            }
            return this;
        }

        /// <summary>弱点弧视觉构建（Build 与 BossController.RebuildWeakArc 共用）。</summary>
        void BuildWeakArc(float angleDeg)
        {
            _arcSpeedDeg = _cfg != null ? _cfg.bossWeakArcSpeedDeg : _arcSpeedDeg;
            var arcGo = new GameObject("WeakArc");
            arcGo.transform.SetParent(transform, false);
            _weakArc = arcGo.transform;
            _weakArc.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

            const int segs = 10;
            const float arcRadius = 0.95f;
            float segLen = 2f * Mathf.PI * arcRadius * (angleDeg / 360f) / segs * 1.15f;
            float half = angleDeg * 0.5f;
            for (int i = 0; i < segs; i++)
            {
                float a = -half + angleDeg * (i + 0.5f) / segs;
                var seg = NeonStyle.MakeSprite(_weakArc, "Seg", ProcSprites.SquareFill(), NeonStyle.Amber, 14, false);
                seg.transform.localRotation = Quaternion.Euler(0f, 0f, a + 90f);   // 切向排布成弧
                seg.transform.localPosition = (Vector2)(Quaternion.Euler(0f, 0f, a) * Vector2.up) * arcRadius;
                seg.transform.localScale = new Vector3(0.06f, segLen, 1f);
                seg.color = new Color(NeonStyle.Amber.r, NeonStyle.Amber.g, NeonStyle.Amber.b, 0.85f);
            }
        }

        public override void TakeDamage(float damage, Vector2 point)
        {
            if (!Alive || indestructible) return;
            // Boss 护盾免疫：护盾存在时核心不掉血（受击提示）
            if (LiveShields > 0)
            {
                if (EffectManager.I != null) EffectManager.I.SpawnSparks(point, Vector2.up, 6, 1f, NeonStyle.Cyan);
                if (FloatingText.I != null)
                    FloatingText.I.Spawn(point, "护盾", NeonStyle.Cyan, 0.42f);
                return;
            }

            // Boss 旋转弱点：命中角 vs 弧角 → 弧内暴击 / 弧外护甲减伤（P8：_arcOn 由阶段驱动）
            if (_weakArc != null && _arcOn && _cfg != null)
            {
                Vector2 dir = point - (Vector2)transform.position;
                if (dir.sqrMagnitude > 0.0001f)
                {
                    float hitDeg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
                    bool inArc = Mathf.Abs(Mathf.DeltaAngle(hitDeg, _weakArc.eulerAngles.z))
                                 <= _cfg.bossWeakArcAngle * 0.5f;
                    if (inArc)
                    {
                        damage *= _arcCritMult;
                        if (FloatingText.I != null)
                            FloatingText.I.Spawn(point, "弱点!", NeonStyle.Amber, 0.42f);
                    }
                    else
                    {
                        damage *= _arcArmorMult;
                        if (FloatingText.I != null)
                            FloatingText.I.Spawn(point, "护甲", NeonStyle.TextDim, 0.35f);
                    }
                }
            }

            base.TakeDamage(damage, point);
            if (Alive && _label != null)
                _label.text = Mathf.Max(0, Mathf.CeilToInt(hp)).ToString();
            if (FloatingText.I != null)
                FloatingText.I.Spawn(point, Mathf.Max(1, Mathf.RoundToInt(damage)).ToString(), NeonStyle.Cyan, 0.5f);
        }

        protected override void Die(Vector2 hitPoint)
        {
            if (!Alive) return;
            Alive = false;
            GameEvents.RaiseVictory();                            // 击破核心=胜利
            Shatter(hitPoint);
            Destroy(gameObject);
        }

        protected override void Shatter(Vector2 hitPoint)
        {
            Vector3 pos = transform.position;
            var fx = EffectManager.I;
            if (fx != null)
            {
                fx.Flash(pos, NeonStyle.White, 5f, 0.3f);
                fx.PlayRing(pos, NeonStyle.Cyan, 0.3f, 2.6f, 0.45f);
                fx.PlayRing(pos, NeonStyle.White, 0.5f, 1.8f, 0.3f);
                fx.SpawnSparks(pos, Vector2.up, 30, 2.5f);
                fx.Shake(0.22f, 0.4f);
            }
            if (FloatingText.I != null)
                FloatingText.I.Spawn(pos, "胜利！", NeonStyle.Cyan, 0.9f);
            for (int i = 0; i < 10; i++)
            {
                if (!Pools.Debris.TryTake(out var d)) break;   // P11：碎片池（池尽省略）
                d.Launch(pos, Random.insideUnitCircle.normalized * Random.Range(2f, 5.5f),
                    Random.Range(0.07f, 0.16f), i % 2 == 0, NeonStyle.Cyan);
            }
        }

        void Update()
        {
            if (!Frozen)                                        // 冻结球：环/弧停摆
            {
                float dt = Time.deltaTime * MechanismTime.Scale;   // 时间球：机关减速
                if (_outerRing != null) _outerRing.Rotate(0f, 0f, 18f * dt, Space.Self);
                if (_innerRing != null) _innerRing.Rotate(0f, 0f, -30f * dt, Space.Self);
                if (_weakArc != null) _weakArc.Rotate(0f, 0f, _arcSpeedDeg * dt, Space.Self);
            }
            if (_glow != null)
                _glow.localScale = Vector3.one * (_glowScale * (1f + 0.12f * Mathf.Sin(Time.time * 2.4f)));
        }
    }
}
