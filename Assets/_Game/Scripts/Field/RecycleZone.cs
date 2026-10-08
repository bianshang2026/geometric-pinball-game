using System.Collections;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>底部回收区：球进入即回收（消耗 1 球）。琥珀色呼吸线提示。</summary>
    public class RecycleZone : MonoBehaviour
    {
        SpriteRenderer _line;

        public void SetVisual(SpriteRenderer line)
        {
            _line = line;
            StartCoroutine(Pulse());
        }

        IEnumerator Pulse()
        {
            while (true)
            {
                yield return null;
                if (_line == null) yield break;
                float a = 0.55f + 0.25f * Mathf.Sin(Time.time * 2.2f);
                _line.color = new Color(NeonStyle.Amber.r, NeonStyle.Amber.g, NeonStyle.Amber.b, a);
            }
        }

        void OnTriggerEnter2D(Collider2D other)
        {
            var ball = other.GetComponent<Ball>();
            if (ball == null || !ball.IsLive) return;
            if (PvPManager.Active) return;                      // PvP v2：回收区已停用，球永不离场（防御性兜底）
            if (BallManager.I != null) BallManager.I.Recycle(ball);
        }
    }
}
