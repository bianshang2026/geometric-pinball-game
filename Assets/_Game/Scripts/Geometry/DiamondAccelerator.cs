using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 菱形加速器◆：不可摧毁；球撞击后速度 ×1.25（TargetSpeed 同步、钳制最大速度），
    /// 连续撞击越来越快。每球独立冷却防贴脸连触发。
    /// 视觉 v2（与障碍区分）：四角星核心 + 反向旋转星环 + 四向加速刺——
    /// "能量放大器"语言（黄色系），绝非方块障碍的轮廓；命中触发爆闪+冲击环。
    /// </summary>
    public class DiamondAccelerator : GeometryEntity
    {
        readonly Dictionary<int, float> _cooldown = new Dictionary<int, float>();
        Transform _starCore;               // 四角星核心（脉冲）
        Transform _ringA, _ringB;          // 双星环（反向旋转）
        Transform _spikes;                 // 四向加速刺（随核心同转）
        Transform _glow;                   // 底光晕（呼吸）
        SpriteRenderer _starSr;
        float _pulse;

        public DiamondAccelerator Build(Vector2 pos, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            Setup(cfg, NeonStyle.Yellow);
            name = $"Accelerator({pos.x:0.##},{pos.y:0.##})";   // 不再自称菱形/方块
            transform.position = new Vector3(pos.x, pos.y, 0f);
            transform.localRotation = Quaternion.identity;      // 旋转交给星环动画，本体不转
            indestructible = true;

            var col = gameObject.AddComponent<BoxCollider2D>();
            col.size = new Vector2(0.62f, 0.62f);
            col.sharedMaterial = bouncy;
            OwnCollider = col;

            // 四角星核心：能量放大的符号语言（非方形轮廓）
            var star = NeonStyle.MakeSprite(transform, "Star", ProcSprites.Star4(), NeonStyle.Yellow, 10, true);
            star.color = new Color(NeonStyle.Yellow.r, NeonStyle.Yellow.g, NeonStyle.Yellow.b, 0.95f);
            star.transform.localScale = Vector3.one * 0.85f;
            _starSr = star;
            _starCore = star.transform;

            // 内星环（正转）
            _ringA = NewRing("RingA", 1.05f, 0.5f, 11);
            // 外星环（反转）
            _ringB = NewRing("RingB", 1.35f, 0.3f, 11);
            // 四向加速刺：指向四角的》"形三角（速度语言）
            _spikes = new GameObject("Spikes").transform;
            _spikes.SetParent(transform, false);
            for (int i = 0; i < 4; i++)
            {
                float a = 45f + i * 90f;
                var tri = NeonStyle.MakeSprite(_spikes, "Spike", ProcSprites.TriangleOutline(), NeonStyle.Yellow, 9, true);
                tri.color = new Color(NeonStyle.Yellow.r, NeonStyle.Yellow.g, NeonStyle.Yellow.b, 0.7f);
                tri.transform.localRotation = Quaternion.Euler(0f, 0f, a + 180f);   // 尖端朝外
                tri.transform.localPosition = (Vector2)(Quaternion.Euler(0f, 0f, a) * Vector2.right) * 1.25f;
                tri.transform.localScale = Vector3.one * 0.4f;
            }

            var glowSr = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), NeonStyle.Yellow, 8, true);
            glowSr.color = new Color(NeonStyle.Yellow.r, NeonStyle.Yellow.g, NeonStyle.Yellow.b, 0.20f);
            _glow = glowSr.transform;
            _glow.localScale = Vector3.one * 0.5f;
            return this;
        }

        Transform NewRing(string name, float scale, float alpha, int order)
        {
            var sr = NeonStyle.MakeSprite(transform, name, ProcSprites.Ring(), NeonStyle.Yellow, order, true);
            sr.color = new Color(NeonStyle.Yellow.r, NeonStyle.Yellow.g, NeonStyle.Yellow.b, alpha);
            sr.transform.localScale = Vector3.one * scale;
            return sr.transform;
        }

        void Update()
        {
            _pulse += Time.unscaledDeltaTime;
            // 星环高速旋转 + 核心强脉冲（存在感拉满——"这是活的能量装置"）
            float f = MechanismTime.Slowed ? 0.4f : 1f;
            float t = Time.time * f;
            if (_starCore != null)
                _starCore.localRotation = Quaternion.Euler(0f, 0f, t * 90f);
            if (_ringA != null)
                _ringA.localRotation = Quaternion.Euler(0f, 0f, t * 160f);
            if (_ringB != null)
                _ringB.localRotation = Quaternion.Euler(0f, 0f, -t * 220f);
            if (_spikes != null)
                _spikes.localRotation = Quaternion.Euler(0f, 0f, -t * 120f);
            if (_starSr != null)
                _starSr.color = new Color(NeonStyle.Yellow.r, NeonStyle.Yellow.g, NeonStyle.Yellow.b,
                    0.6f + 0.35f * Mathf.Sin(t * 8f));
        }

        protected override void OnBallCollide(BallCollisionEvent e)
        {
            var ball = e.ball;
            if (ball == null || ball.RB == null || !ball.IsLive) return;

            int id = ball.GetInstanceID();
            if (_cooldown.TryGetValue(id, out float last) &&
                Time.time - last < _cfg.diamondCooldownSeconds) return;
            _cooldown[id] = Time.time;

            ball.TargetSpeed = Mathf.Clamp(
                ball.TargetSpeed * _cfg.diamondSpeedMultiplier, _cfg.minSpeed, _cfg.maxSpeed);
            ball.RB.velocity = ball.RB.velocity.normalized * ball.TargetSpeed;

            if (EffectManager.I != null)
            {
                EffectManager.I.Flash(transform.position, NeonStyle.Yellow, 2.6f, 0.28f);
                EffectManager.I.PlayRing(transform.position, NeonStyle.Yellow, 0.5f, 3.2f, 0.4f);   // 加速波纹（大而亮）
                EffectManager.I.SpawnSparks(transform.position, Vector2.up, 10, 2.0f, NeonStyle.Yellow);
            }
        }
    }
}
