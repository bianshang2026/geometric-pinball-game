using System.Collections;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 几何实体基类：订阅统一碰撞事件总线（球核心代码不改动），
    /// 提供伤害入口、受击脉冲、死亡碎裂。子类：Block/Bomb/Triangle/Diamond/Mirror。
    /// </summary>
    public abstract class GeometryEntity : MonoBehaviour
    {
        public bool indestructible = true;
        public float hp = 1f;
        public float collisionPunch = 0.15f;
        public bool Alive { get; protected set; } = true;

        float _frozenUntil;
        System.Collections.Generic.List<SpriteRenderer> _frozenSrs;
        System.Collections.Generic.List<Color> _frozenColors;

        /// <summary>冻结中（冻结球）：自转/开合/Boss 机制停摆——机关各 Update 惰性检查本属性。</summary>
        public bool Frozen => _frozenUntil > Time.time;

        /// <summary>全场景实体注册表（引力球"最近目标"查询用；OnEnable/OnDisable 自动维护）。</summary>
        public static readonly System.Collections.Generic.List<GeometryEntity> All =
            new System.Collections.Generic.List<GeometryEntity>();

        protected Collider2D OwnCollider;
        protected GameConfig _cfg;
        protected Color MainColor = NeonStyle.Cyan;
        protected bool punchOnHit = true;

        protected virtual void OnEnable()
        {
            GameEvents.OnBallCollision += OnBallHit;
            All.Add(this);
        }

        protected virtual void OnDisable()
        {
            GameEvents.OnBallCollision -= OnBallHit;
            All.Remove(this);
            // P11：停用即取消受击脉冲并还原基线缩放（池化复用/场景销毁都防脏缩放）
            if (EffectManager.I != null) EffectManager.I.CancelPunch(transform);
            UnfreezeVisual();                                 // 池化复用防冻结 tint 残留
        }

        /// <summary>冻结（冻结球）：停转/停开合/Boss 停摆 + 冰蓝视觉；到期由 FrostSystem 恢复。</summary>
        public void Freeze(float duration)
        {
            bool wasFrozen = Frozen;
            _frozenUntil = Time.time + duration;
            if (wasFrozen) return;                            // 续冻不重复上色/浮字
            FrostSystem.Register(this);
            ApplyFrostTint();
            if (FloatingText.I != null)
                FloatingText.I.Spawn(transform.position, "冻结", new Color(0.65f, 0.88f, 1f), 0.5f);
        }

        void ApplyFrostTint()
        {
            _frozenSrs = new System.Collections.Generic.List<SpriteRenderer>();
            _frozenColors = new System.Collections.Generic.List<Color>();
            foreach (var sr in GetComponentsInChildren<SpriteRenderer>())
            {
                if (sr == null) continue;
                _frozenSrs.Add(sr);
                _frozenColors.Add(sr.color);
                var c = sr.color;
                sr.color = new Color(c.r * 0.45f + 0.22f, c.g * 0.6f + 0.32f, 1f, c.a);   // 偏冰蓝
            }
        }

        /// <summary>恢复冻结前的颜色（FrostSystem 到期调用；重复调用安全）。</summary>
        public void UnfreezeVisual()
        {
            if (_frozenSrs == null) return;
            for (int i = 0; i < _frozenSrs.Count; i++)
                if (_frozenSrs[i] != null) _frozenSrs[i].color = _frozenColors[i];
            _frozenSrs = null;
            _frozenColors = null;
            _frozenUntil = 0f;
        }

        /// <summary>由布局构建器调用：注入配置与主色。</summary>
        protected GeometryEntity Setup(GameConfig cfg, Color mainColor)
        {
            _cfg = cfg;
            MainColor = mainColor;
            return this;
        }

        void OnBallHit(BallCollisionEvent e)
        {
            if (e.target != OwnCollider || !Alive) return;
            OnBallCollide(e);
            if (!indestructible && AcceptsBallDamage(e))
                TakeDamage(ChainSystem.Apply(e.damage), e.point);   // 连锁伤害加成
            else if (punchOnHit)
                Punch();
        }

        /// <summary>球撞到本实体：速度规则等子类行为（反弹本身已由弹性物理完成）。</summary>
        protected virtual void OnBallCollide(BallCollisionEvent e) { }

        /// <summary>是否接受这颗球的伤害结算（PvP 玩家核心：己方弹命中己方核心=只弹不伤）。</summary>
        protected virtual bool AcceptsBallDamage(BallCollisionEvent e) => true;

        /// <summary>伤害入口（球击/爆炸连锁共用；RunStats 战报埋点）。</summary>
        public virtual void TakeDamage(float damage, Vector2 point)
        {
            if (!Alive || indestructible) return;
            hp -= damage;
            RunStats.RecordDamage(damage);                     // P6：战报统计
            if (punchOnHit) Punch();
            if (hp <= 0f) Die(point);
        }

        protected virtual void Die(Vector2 hitPoint)
        {
            if (!Alive) return;
            Alive = false;
            Shatter(hitPoint);
            ReturnToPool();
        }

        /// <summary>死亡终点（P11 池化）：默认销毁；可破坏实体子类覆写为回池复用。</summary>
        protected virtual void ReturnToPool() => Destroy(gameObject);

        protected void Punch()
        {
            if (EffectManager.I != null)
                EffectManager.I.ScalePunch(transform, collisionPunch, 0.12f);
        }

        /// <summary>碎裂：飞散碎片 + 火花 + 冲击环 + 闪光 + 震屏。</summary>
        protected virtual void Shatter(Vector2 hitPoint)
        {
            Vector3 pos = transform.position;
            var fx = EffectManager.I;
            if (fx != null)
            {
                Vector2 dir = hitPoint != (Vector2)pos ? (hitPoint - (Vector2)pos).normalized : Vector2.up;
                fx.SpawnSparks(pos, dir, 14, 1.6f, MainColor);
                fx.PlayRing(pos, MainColor, 0.2f, 0.95f, 0.3f);
                fx.Flash(pos, MainColor, 1.3f, 0.18f);
                fx.Shake(0.09f, 0.2f);
            }
            for (int i = 0; i < 6; i++)
            {
                // P11：碎片走池（池尽则省略——预算内优雅降级，结构性零创建）
                if (!Pools.Debris.TryTake(out var d)) break;
                d.Launch(pos, Random.insideUnitCircle.normalized * Random.Range(1.6f, 4.2f),
                    Random.Range(0.05f, 0.13f), i % 2 == 0, MainColor);
            }
        }
    }

    /// <summary>死亡碎片：飞散 + 旋转 + 缩小淡出。P11 池化：寿命尽回池（Debris×40 预热）。</summary>
    public class Debris : MonoBehaviour
    {
        SpriteRenderer _sr;
        Vector2 _vel;
        float _spin;

        /// <summary>一次性构建（池创建/溢出新建时调用）：碎片精灵子物体。</summary>
        public void Build()
        {
            if (_sr == null)
                _sr = NeonStyle.MakeSprite(transform, "Shard", ProcSprites.SquareOutline(), Color.white, 30, false);
        }

        public void Launch(Vector3 pos, Vector2 vel, float scale, bool square, Color color)
        {
            Build();                                        // 溢出新建路径兜底
            gameObject.SetActive(true);
            transform.position = pos + (Vector3)Random.insideUnitCircle * 0.2f;
            transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            transform.localScale = Vector3.one * scale;
            _sr.sprite = square ? ProcSprites.SquareOutline() : ProcSprites.Triangle();
            _sr.color = color;
            _vel = vel;
            _spin = Random.Range(-420f, 420f);
            StartCoroutine(Life(0.55f));
        }

        IEnumerator Life(float dur)
        {
            Vector3 baseScale = transform.localScale;
            Color c = _sr.color;
            float t = 0f;
            while (t < dur)
            {
                t += Time.deltaTime;
                transform.position += (Vector3)(_vel * Time.deltaTime);
                _vel *= 1f - 2.4f * Time.deltaTime;
                transform.Rotate(0f, 0f, _spin * Time.deltaTime, Space.Self);
                float k = Mathf.Clamp01(t / dur);
                transform.localScale = baseScale * (1f - 0.55f * k);
                _sr.color = new Color(c.r, c.g, c.b, 1f - k);
                yield return null;
            }
            Pools.Debris.Release(this);                     // P11：回池（不再 Destroy）
        }
    }
}
