using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 轨迹预测：迭代 CircleCast 模拟前 N 次反弹（默认 3 次，可配置）。
    /// 完全弹性材质下 CircleCast 反射与 Box2D 实际反弹一致。
    /// 视觉：第一段黄白色"激光点"，后续青色渐隐；反弹点空心环；命中回收区为琥珀色终止标记。
    /// </summary>
    public class TrajectoryPredictor : MonoBehaviour
    {
        GameConfig _cfg;
        float _radius;

        readonly List<SpriteRenderer> _dots = new List<SpriteRenderer>();
        readonly List<SpriteRenderer> _rings = new List<SpriteRenderer>();
        int _dotCursor, _ringCursor;
        int _teleports;

        // 结构化测试读数
        public int ActiveDots { get; private set; }
        public int ActiveRings { get; private set; }
        public bool RecycleHit { get; private set; }
        public string FirstHit { get; private set; }
        public bool FirstHitIsTrigger { get; private set; }

        public void Init(GameConfig cfg, float ballRadius)
        {
            _cfg = cfg;
            _radius = ballRadius * 0.9f;
        }

        public void Hide()
        {
            for (int i = 0; i < _dots.Count; i++) _dots[i].gameObject.SetActive(false);
            for (int i = 0; i < _rings.Count; i++) _rings[i].gameObject.SetActive(false);
        }

        public void Show(Vector2 origin, Vector2 dir)
        {
            Hide();
            _dotCursor = _ringCursor = 0;
            _teleports = 0;
            ActiveDots = ActiveRings = 0;
            RecycleHit = false;
            FirstHit = null;
            FirstHitIsTrigger = false;

            MarkRing(origin, NeonStyle.Cyan, 0.9f, 0.32f);      // 发射点参考环

            Vector2 pos = origin;
            Vector2 v = dir.normalized;
            int n = Mathf.Max(1, _cfg.predictionBounces);
            for (int bounce = 0; bounce <= n; bounce++)
            {
                var hit = Physics2D.CircleCast(pos, _radius, v, 100f);
                Vector2 end = hit ? hit.centroid : pos + v * 6f;

                if (hit && FirstHit == null)
                {
                    FirstHit = hit.collider != null ? hit.collider.name : "?";
                    FirstHitIsTrigger = hit.collider != null && hit.collider.isTrigger;
                }

                DrawSegment(pos + v * 0.06f, v, end, bounce, n);

                if (!hit) break;

                if (hit.collider != null && hit.collider.isTrigger)
                {
                    var node = hit.collider.GetComponent<PortalNode>();
                    if (node != null)
                    {
                        if (node.Other != null && _teleports < 4)
                        {
                            // 传送门：紫色环标记，并从出口沿原方向继续投射
                            MarkRing(hit.centroid, NeonStyle.Portal, 0.8f, 0.4f);
                            _teleports++;
                            pos = (Vector2)node.Other.transform.position
                                + v * (node.Radius + _radius / 0.9f + 0.08f);
                            bounce--;            // 传送不消耗反弹段数（受传送次数上限保护）
                            continue;
                        }
                        break;                    // 超出传送上限：停止预测
                    }
                    MarkRing(hit.centroid, NeonStyle.Amber, 0.9f, 0.48f);   // 回收区：琥珀终止标记
                    RecycleHit = true;
                    break;
                }

                if (bounce == n) break;

                MarkRing(hit.centroid, NeonStyle.Cyan, 0.8f, 0.26f);
                v = Vector2.Reflect(v, hit.normal);
                pos = end + v * 0.03f;
            }

            ActiveDots = _dotCursor;
            ActiveRings = _ringCursor;
        }

        void DrawSegment(Vector2 start, Vector2 v, Vector2 end, int depth, int n)
        {
            float total = Vector2.Distance(start, end);
            float alpha = Mathf.Lerp(0.85f, 0.35f, (float)depth / n);
            Color c = depth == 0 ? NeonStyle.Yellow : NeonStyle.Cyan;
            float size = Mathf.Lerp(0.05f, 0.032f, (float)depth / n);

            for (float d = _cfg.predictionDotSpacing; d <= total; d += _cfg.predictionDotSpacing)
            {
                var dot = GetPooled(_dots, _dotCursor++, "Dot", ProcSprites.Glow(), 15);
                dot.transform.position = start + v * d;
                dot.color = new Color(c.r, c.g, c.b, alpha);
                dot.transform.localScale = Vector3.one * size;
            }
        }

        void MarkRing(Vector2 p, Color c, float alpha, float scale)
        {
            var ring = GetPooled(_rings, _ringCursor++, "PRing", ProcSprites.Ring(), 16);
            ring.transform.position = p;
            ring.color = new Color(c.r, c.g, c.b, alpha);
            ring.transform.localScale = Vector3.one * scale;
        }

        SpriteRenderer GetPooled(List<SpriteRenderer> pool, int idx, string name, Sprite sprite, int order)
        {
            while (pool.Count <= idx)
            {
                var sr = NeonStyle.MakeSprite(transform, name, sprite, Color.white, order, true);
                sr.gameObject.SetActive(false);
                pool.Add(sr);
            }
            var s = pool[idx];
            s.gameObject.SetActive(true);
            return s;
        }
    }
}
