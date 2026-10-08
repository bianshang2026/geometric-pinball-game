using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 黑洞（第三批机关）：成组出现（variant=组 id）。球进入任一洞——
    /// 从组内随机另一个洞吐出，且出口方向随机（区别传送门的保速定向）。
    /// 深紫吸积视觉，冷却是全局共享防抖（复用 Ball.LastTeleportTime 管线）。
    /// </summary>
    public class BlackHole : MonoBehaviour
    {
        public static readonly List<BlackHole> All = new List<BlackHole>();    // OnEnable/OnDisable 维护
        public static readonly Dictionary<int, List<BlackHole>> Groups = new Dictionary<int, List<BlackHole>>();

        public int GroupId;
        GameConfig _cfg;
        Transform _spin;
        bool _groupRegistered;

        public BlackHole Build(Vector2 pos, int groupId, GameConfig cfg)
        {
            _cfg = cfg;
            GroupId = groupId;
            Register();                     // 修复：AddComponent 的 OnEnable 先于 Build 执行，此时 GroupId 仍是默认 0——组注册必须在 GroupId 注入后进行
            name = "BlackHole";
            transform.position = new Vector3(pos.x, pos.y, 0f);

            var col = gameObject.AddComponent<CircleCollider2D>();
            col.radius = 0.38f;
            col.isTrigger = true;

            _spin = new GameObject("Spin").transform;
            _spin.SetParent(transform, false);

            var pc = new Color(0.55f, 0.3f, 0.95f);                        // 深紫
            var disk = NeonStyle.MakeSprite(transform, "Disk", ProcSprites.CircleSoft(),
                new Color(0.05f, 0.02f, 0.12f, 0.95f), 9, false);
            disk.transform.localScale = Vector3.one * 0.55f;
            var ring = NeonStyle.MakeSprite(_spin, "Accretion", ProcSprites.Ring(), pc, 8, true);
            ring.color = new Color(pc.r, pc.g, pc.b, 0.75f);
            ring.transform.localScale = Vector3.one * 0.95f;
            var glow = NeonStyle.MakeSprite(transform, "Glow", ProcSprites.Glow(), pc, 7, true);
            glow.color = new Color(pc.r, pc.g, pc.b, 0.2f);
            glow.transform.localScale = Vector3.one * 1.3f;
            return this;
        }

        void OnEnable()
        {
            All.Add(this);
            if (_groupRegistered) Register();   // 仅复活重挂；首次激活时 GroupId 尚未注入，注册交给 Build
        }

        void OnDisable()
        {
            All.Remove(this);
            if (Groups.TryGetValue(GroupId, out var list)) list.Remove(this);
            _groupRegistered = false;
        }

        void Register()
        {
            if (!Groups.TryGetValue(GroupId, out var list))
            {
                list = new List<BlackHole>();
                Groups[GroupId] = list;
            }
            if (!list.Contains(this)) list.Add(this);
            _groupRegistered = true;
        }

        void Update()
        {
            if (_spin != null) _spin.Rotate(0f, 0f, -95f * MechanismTime.Scale * Time.deltaTime, Space.Self);
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            var ball = other.GetComponent<Ball>();
            if (ball == null || !ball.IsLive) return;
            if (Time.time - ball.LastTeleportTime < _cfg.portalCooldownSeconds) return;

            if (!Groups.TryGetValue(GroupId, out var group) || group.Count < 2) return;
            // 组内随机另一个洞
            BlackHole exit = this;
            for (int i = 0; i < 8; i++)
            {
                var cand = group[Random.Range(0, group.Count)];
                if (cand != null && cand != this) { exit = cand; break; }
            }
            if (exit == this) return;

            ball.LastTeleportTime = Time.time;
            float speed = Mathf.Max(ball.RB.velocity.magnitude, ball.TargetSpeed);
            float ang = Random.Range(0f, 360f);                            // 出口方向随机
            Vector2 dir = new Vector2(Mathf.Cos(ang * Mathf.Deg2Rad), Mathf.Sin(ang * Mathf.Deg2Rad));
            ball.RB.position = (Vector2)exit.transform.position + dir * (0.5f + ball.Data.radius + 0.06f);
            ball.RB.velocity = dir * speed;
            ball.TargetSpeed = speed;
            AudioManager.PlayPortal();

            var fx = EffectManager.I;
            if (fx != null)
            {
                var pc = new Color(0.55f, 0.3f, 0.95f);
                Vector2 a = transform.position, b = exit.transform.position;
                fx.SpawnSparks(a, -dir, 12, 1.3f, pc);                     // 吸入（逆方向）
                fx.SpawnSparks(b, dir, 12, 1.3f, pc);                      // 吐出
                fx.PlayRing(a, pc, 0.5f, 0.1f, 0.25f);                      // 吸缩环
                fx.PlayRing(b, pc, 0.1f, 0.5f, 0.25f);                      // 迸发环
                fx.Flash(a, pc, 1.4f, 0.18f);
                fx.Flash(b, pc, 1.4f, 0.18f);
            }
        }
    }
}
