using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>程序化网格背景 + 稀疏闪烁十字星（参考图的"太空弹球"氛围）。</summary>
    public class BackgroundGrid : MonoBehaviour
    {
        class Star
        {
            public Transform t;
            public float baseScale;
            public float phase;
            public float speed;
        }

        readonly List<Star> _stars = new List<Star>();

        public void Build(GameConfig cfg)
        {
            var sprite = ProcSprites.GridBackground(
                Mathf.RoundToInt(cfg.fieldWidth), Mathf.RoundToInt(cfg.fieldHeight));
            var bg = NeonStyle.MakeSprite(transform, "Grid", sprite, Color.white, 0, false);
            bg.transform.position = Vector3.zero;

            SpawnStar(new Vector3(-3.1f, 6.2f), NeonStyle.White, 0.55f);
            SpawnStar(new Vector3(3.4f, 2.4f), NeonStyle.Blue, 0.42f);
            SpawnStar(new Vector3(-2.2f, -3.4f), NeonStyle.White, 0.3f);

            StartCoroutine(Twinkle());
        }

        void SpawnStar(Vector3 pos, Color c, float size)
        {
            var sr = NeonStyle.MakeSprite(transform, "Star", ProcSprites.Star4(), c, 1, true);
            sr.color = new Color(c.r, c.g, c.b, 0.8f);
            sr.transform.position = pos;
            sr.transform.localScale = Vector3.one * size;
            _stars.Add(new Star
            {
                t = sr.transform,
                baseScale = size,
                phase = Random.value * Mathf.PI * 2f,
                speed = Random.Range(1.5f, 3f),
            });
        }

        IEnumerator Twinkle()
        {
            while (true)
            {
                yield return null;
                for (int i = 0; i < _stars.Count; i++)
                {
                    var s = _stars[i];
                    if (s.t == null) continue;
                    float k = 0.7f + 0.3f * Mathf.Sin(Time.time * s.speed + s.phase);
                    s.t.localScale = Vector3.one * (s.baseScale * k);
                    s.t.Rotate(0f, 0f, 8f * Time.deltaTime);
                }
            }
        }
    }
}
