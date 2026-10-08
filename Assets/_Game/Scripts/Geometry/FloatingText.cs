using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace GeoBreaker
{
    /// <summary>世界空间伤害数字池（TMP，无需画布）：上浮 + 淡出。
    /// P11 池化：固定 20 实例（架构 §19），Init 预热，池尽轮转抢占最旧——战斗中零创建零销毁。</summary>
    public class FloatingText : MonoBehaviour
    {
        public static FloatingText I { get; private set; }

        const int Capacity = 20;                    // 架构 §19：浮字×20

        class Item
        {
            public TextMeshPro tmp;
            public Coroutine routine;
        }

        readonly List<Item> _pool = new List<Item>();
        int _rr;

        /// <summary>池规模读数（T50 压测断言：Init 预热后恒定）。</summary>
        public int ItemCount => _pool.Count;

        public void Init()
        {
            I = this;
            for (int i = 0; i < Capacity; i++) _pool.Add(CreateItem());
        }

        Item CreateItem()
        {
            var go = new GameObject("FxText", typeof(TextMeshPro));
            go.transform.SetParent(transform, false);
            var tmp = go.GetComponent<TextMeshPro>();
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = 14;
            tmp.GetComponent<MeshRenderer>().sortingOrder = 50;
            go.SetActive(false);
            return new Item { tmp = tmp };
        }

        Item GetItem()
        {
            var it = _pool[_rr];                        // 池尽轮转抢占最旧
            _rr = (_rr + 1) % _pool.Count;
            return it;
        }

        /// <summary>生成一个浮动数字/文本（size 为期望字高，世界单位）。</summary>
        public void Spawn(Vector3 pos, string text, Color color, float size = 0.45f)
        {
            var it = GetItem();
            Transform t = it.tmp.transform;
            t.position = new Vector3(pos.x, pos.y, 0f) + (Vector3)(Random.insideUnitCircle * 0.12f);
            t.localScale = Vector3.one * (size / 1.4f);       // fontSize 14 ≈ 1.4 单位字高
            it.tmp.text = text;
            it.tmp.color = color;
            it.tmp.gameObject.SetActive(true);
            if (it.routine != null) StopCoroutine(it.routine);
            it.routine = StartCoroutine(Rise(it, 0.7f));
        }

        IEnumerator Rise(Item it, float dur)
        {
            Transform t = it.tmp.transform;
            Vector3 start = t.position;
            Color c = it.tmp.color;
            float k = 0f;
            while (k < 1f)
            {
                k += Time.deltaTime / dur;
                t.position = start + Vector3.up * (0.9f * k);
                it.tmp.color = new Color(c.r, c.g, c.b, Mathf.Clamp01(2.2f * (1f - k)));
                yield return null;
            }
            it.tmp.gameObject.SetActive(false);
        }
    }
}
