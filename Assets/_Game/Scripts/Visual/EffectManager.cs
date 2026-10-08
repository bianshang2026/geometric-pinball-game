using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 碰撞反馈中心（P11 全池化，架构 §19）：火花=单 PS 固定 60 粒子上限（池上限即预算）、
    /// 冲击环/点爆闪光=固定 8 实例轮转抢占、缩放脉冲=基线字典防叠加膨胀、屏幕震动。
    /// Init 一次性预热全部实例——战斗中零 Instantiate/Destroy、池零增长（压测断言依据）。
    /// 订阅统一碰撞事件总线，与 Ball 完全解耦。
    /// </summary>
    public class EffectManager : MonoBehaviour
    {
        public static EffectManager I { get; private set; }

        const int RingCapacity = 8;     // 架构 §19：冲击环×8
        const int GlowCapacity = 8;     // 点爆闪光同规格轮转
        const int SparkBudget = 60;     // 架构 §19：火花×60（粒子预算）
        const int LaserCapacity = 8;    // 棱镜激光线（固定池轮转抢占）

        ParticleSystem _sparks;
        readonly List<SpriteRenderer> _ringPool = new List<SpriteRenderer>();
        readonly List<SpriteRenderer> _glowPool = new List<SpriteRenderer>();
        readonly List<SpriteRenderer> _laserPool = new List<SpriteRenderer>();
        readonly Dictionary<SpriteRenderer, Coroutine> _ringCo = new Dictionary<SpriteRenderer, Coroutine>();
        readonly Dictionary<SpriteRenderer, Coroutine> _glowCo = new Dictionary<SpriteRenderer, Coroutine>();
        readonly Dictionary<SpriteRenderer, Coroutine> _laserCo = new Dictionary<SpriteRenderer, Coroutine>();
        readonly Dictionary<Transform, Vector3> _punchBase = new Dictionary<Transform, Vector3>();
        readonly Dictionary<Transform, Coroutine> _punchCo = new Dictionary<Transform, Coroutine>();
        int _ringRR, _glowRR, _laserRR;
        Transform _camRoot;
        Vector3 _camOrigin;
        Coroutine _shakeRoutine;

        /// <summary>池规模读数（压测断言：Init 预热后恒定不变）。</summary>
        public int RingCount => _ringPool.Count;
        public int GlowCount => _glowPool.Count;
        public int LaserCount => _laserPool.Count;
        public int SparkCap => _sparks.main.maxParticles;

        public void Init(Camera cam)
        {
            I = this;
            _camRoot = cam.transform;
            _camOrigin = _camRoot.position;
            _sparks = BuildSparks();

            // P11：固定容量预热——战斗期轮转抢占最旧实例，零创建零销毁
            for (int i = 0; i < RingCapacity; i++)
            {
                var sr = NeonStyle.MakeSprite(transform, "FxRing", ProcSprites.Ring(), Color.white, 45, true);
                sr.gameObject.SetActive(false);
                _ringPool.Add(sr);
            }
            for (int i = 0; i < GlowCapacity; i++)
            {
                var sr = NeonStyle.MakeSprite(transform, "FxGlow", ProcSprites.Glow(), Color.white, 46, true);
                sr.gameObject.SetActive(false);
                _glowPool.Add(sr);
            }
            for (int i = 0; i < LaserCapacity; i++)
            {
                var sr = NeonStyle.MakeSprite(transform, "FxLaser", ProcSprites.SquareFill(), Color.white, 44, true);
                sr.gameObject.SetActive(false);
                _laserPool.Add(sr);
            }

            GameEvents.OnBallCollision += HandleCollision;
            GameEvents.OnBallRecycled += HandleRecycled;
        }

        void OnDestroy()
        {
            GameEvents.OnBallCollision -= HandleCollision;
            GameEvents.OnBallRecycled -= HandleRecycled;
        }

        ParticleSystem BuildSparks()
        {
            var go = new GameObject("Sparks");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.55f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.11f);
            main.gravityModifier = 0.5f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = SparkBudget;                    // P11：火花池上限即粒子预算
            main.startColor = new ParticleSystem.MinMaxGradient(NeonStyle.Orange, NeonStyle.Yellow);

            var em = ps.emission;
            em.enabled = false;                                 // 只手动 Emit 爆点

            var rs = ps.GetComponent<ParticleSystemRenderer>();
            rs.material = NeonStyle.Additive;
            rs.sortingOrder = 40;
            return ps;
        }

        void HandleCollision(BallCollisionEvent e)
        {
            SpawnSparks(e.point, e.normal, 8, 1f);
            PlayRing(e.point, NeonStyle.Cyan, 0.11f, 0.5f, 0.22f);
            Shake(0.05f, 0.12f);
        }

        void HandleRecycled(Ball b)
        {
            if (b == null) return;
            Vector3 p = b.transform.position;
            SpawnSparks(p, Vector2.up, 14, 1.5f, NeonStyle.Amber);
            PlayRing(p, NeonStyle.Amber, 0.16f, 0.75f, 0.3f);
            Shake(0.08f, 0.15f);
        }

        public void SpawnSparks(Vector3 point, Vector2 normal, int count, float speedScale)
            => SpawnSparks(point, normal, count, speedScale, null);

        public void SpawnSparks(Vector3 point, Vector2 normal, int count, float speedScale, Color? colorOverride)
        {
            for (int i = 0; i < count; i++)
            {
                var ep = new ParticleSystem.EmitParams
                {
                    position = point,
                    velocity = ((Vector2)(normal * Random.Range(0.5f, 1f) + Random.insideUnitCircle * 0.8f).normalized
                        * Random.Range(2f, 5f) * speedScale),
                    startLifetime = Random.Range(0.25f, 0.55f),
                    startSize = Random.Range(0.04f, 0.11f),
                    startColor = colorOverride ?? (Random.value < 0.5f ? NeonStyle.Orange : NeonStyle.Yellow),
                };
                _sparks.Emit(ep, 1);
            }
        }

        /// <summary>冲击圆环：从 s0 扩散到 s1 并淡出（池尽轮转抢占最旧）。</summary>
        public void PlayRing(Vector3 point, Color color, float s0, float s1, float dur)
        {
            var sr = _ringPool[_ringRR];
            _ringRR = (_ringRR + 1) % _ringPool.Count;
            if (_ringCo.TryGetValue(sr, out var prev)) StopCoroutine(prev);
            _ringCo[sr] = StartCoroutine(RingAnim(sr, point, color, s0, s1, dur));
        }

        /// <summary>棱镜激光束：from→to 直线——三层（粗紫外晕+中霓虹层+亮白芯）缓慢淡出（8 条池轮转，每发占 3 条）。</summary>
        public void PlayLaser(Vector2 from, Vector2 to, Color color)
        {
            var halo = _laserPool[_laserRR];
            _laserRR = (_laserRR + 1) % _laserPool.Count;
            var mid = _laserPool[_laserRR];
            _laserRR = (_laserRR + 1) % _laserPool.Count;
            var core = _laserPool[_laserRR];
            _laserRR = (_laserRR + 1) % _laserPool.Count;
            if (_laserCo.TryGetValue(halo, out var pH)) StopCoroutine(pH);
            if (_laserCo.TryGetValue(mid, out var pM)) StopCoroutine(pM);
            if (_laserCo.TryGetValue(core, out var pC)) StopCoroutine(pC);
            _laserCo[halo] = StartCoroutine(LaserAnim(halo, from, to, color, 1.2f, 0.55f, 0.34f));
            _laserCo[mid] = StartCoroutine(LaserAnim(mid, from, to, Color.white, 0.45f, 0.8f, 0.26f));
            _laserCo[core] = StartCoroutine(LaserAnim(core, from, to, Color.white, 0.2f, 1f, 0.3f));
        }

        IEnumerator LaserAnim(SpriteRenderer sr, Vector2 from, Vector2 to, Color color, float thickness, float alphaMul, float dur)
        {
            var d = to - from;
            float len = d.magnitude;
            if (len < 0.05f) yield break;
            var mid = (from + to) * 0.5f;
            sr.transform.position = new Vector3(mid.x, mid.y, 0f);
            sr.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            sr.transform.localScale = new Vector3(len, thickness, 1f);      // SquareFill 1×1 → 光束
            sr.gameObject.SetActive(true);
            float t = 0f;
            while (t < dur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / dur);
                // 宽度脉动：能量流动感
                float wobble = 1f + 0.25f * Mathf.Sin(t * 40f);
                sr.transform.localScale = new Vector3(len, thickness * wobble, 1f);
                var c = color;
                sr.color = new Color(
                    Mathf.Lerp(1f, c.r, k * 0.55f),
                    Mathf.Lerp(1f, c.g, k * 0.55f),
                    Mathf.Lerp(1f, c.b, k * 0.55f),
                    (1f - k) * alphaMul);                                    // 白芯起 → 紫褪
                yield return null;
            }
            sr.gameObject.SetActive(false);
        }

        /// <summary>点爆闪光：Glow 精灵快速放大淡出（爆炸/传送/击破强调；池尽轮转抢占）。</summary>
        public void Flash(Vector3 point, Color color, float size, float dur)
        {
            var sr = _glowPool[_glowRR];
            _glowRR = (_glowRR + 1) % _glowPool.Count;
            if (_glowCo.TryGetValue(sr, out var prev)) StopCoroutine(prev);
            _glowCo[sr] = StartCoroutine(FlashAnim(sr, point, color, size, dur));
        }

        IEnumerator FlashAnim(SpriteRenderer sr, Vector3 point, Color color, float size, float dur)
        {
            sr.transform.position = point;
            sr.gameObject.SetActive(true);
            float t = 0f;
            while (t < dur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / dur);
                sr.transform.localScale = Vector3.one * Mathf.Lerp(size * 0.25f, size, k);
                sr.color = new Color(color.r, color.g, color.b, (1f - k) * 0.85f);
                yield return null;
            }
            sr.gameObject.SetActive(false);
        }

        IEnumerator RingAnim(SpriteRenderer sr, Vector3 point, Color color, float s0, float s1, float dur)
        {
            sr.transform.position = point;
            sr.gameObject.SetActive(true);
            float t = 0f;
            while (t < dur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / dur);
                sr.transform.localScale = Vector3.one * Mathf.Lerp(s0, s1, k);
                sr.color = new Color(color.r, color.g, color.b, 1f - k);
                yield return null;
            }
            sr.gameObject.SetActive(false);
        }

        /// <summary>受击缩放脉冲：Scale 1 -> 1+amp -> 1。
        /// P11：基线字典防叠加膨胀（快速连击重复脉冲曾以膨胀值为基线导致永久放大）；
        /// 池化实体停用经 CancelPunch 还原，复用 Build 重建缩放。</summary>
        public void ScalePunch(Transform target, float amp, float dur)
        {
            if (target == null) return;
            if (_punchCo.TryGetValue(target, out var prev)) StopCoroutine(prev);
            if (_punchBase.TryGetValue(target, out var bs)) target.localScale = bs;   // 先还原基线再起新脉冲
            _punchBase[target] = target.localScale;
            _punchCo[target] = StartCoroutine(PunchRoutine(target, amp, dur));
        }

        /// <summary>取消脉冲并还原基线缩放（GeometryEntity.OnDisable 调用）。</summary>
        public void CancelPunch(Transform target)
        {
            if (target == null || !_punchCo.TryGetValue(target, out var co)) return;
            if (co != null) StopCoroutine(co);
            if (_punchBase.TryGetValue(target, out var bs) && target != null) target.localScale = bs;
            _punchCo.Remove(target);
            _punchBase.Remove(target);
        }

        IEnumerator PunchRoutine(Transform target, float amp, float dur)
        {
            Vector3 baseScale = _punchBase[target];
            float t = 0f;
            while (t < dur && target != null)
            {
                t += Time.deltaTime;
                float k = Mathf.Sin(Mathf.Clamp01(t / dur) * Mathf.PI);
                target.localScale = baseScale * (1f + amp * k);
                yield return null;
            }
            if (target != null) target.localScale = baseScale;
            _punchCo.Remove(target);
            _punchBase.Remove(target);
        }

        public void Shake(float magnitude, float dur)
        {
            if (_shakeRoutine != null) StopCoroutine(_shakeRoutine);
            _shakeRoutine = StartCoroutine(ShakeRoutine(magnitude, dur));
        }

        IEnumerator ShakeRoutine(float mag, float dur)
        {
            float t = 0f;
            while (t < dur)
            {
                t += Time.deltaTime;
                float k = 1f - Mathf.Clamp01(t / dur);
                Vector2 off = Random.insideUnitCircle * (mag * k);
                _camRoot.position = _camOrigin + new Vector3(off.x, off.y, 0f);
                yield return null;
            }
            _camRoot.position = _camOrigin;
        }
    }
}
