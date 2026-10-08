using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GeoBreaker
{
    /// <summary>
    /// AI 生成 UI 素材加载（UI 美化阶段）：统一从 Resources/UI/ 加载 Sprite，
    /// 带缓存；素材缺失返回 false 由调用方回退程序占位（纯色块）——绝不出黑块。
    /// 约定命名：MainMenuBG / MapBG / BtnMain / BtnSub / BtnNav / BtnCard /
    /// PanelBase / LogoEmblem / NodeBattle / NodeElite / NodeBoss / NodeShop / NodeChest / NodeStart。
    /// </summary>
    public static class UIArt
    {
        static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        public static Sprite Load(string name)
        {
            if (_cache.TryGetValue(name, out var s)) return s;
            s = Resources.Load<Sprite>("UI/" + name);
            _cache[name] = s;                               // null 也缓存：缺失时每帧 Load 同样是浪费
            if (s == null) Debug.LogWarning("[UIArt] 缺素材 UI/" + name + "（回退程序占位）");
            return s;
        }

        /// <summary>尝试给 Image 应用素材（color 置白显示原图）；缺素材返回 false。</summary>
        public static bool TryApply(Image img, string name)
        {
            if (img == null) return false;
            var s = Load(name);
            if (s == null) return false;
            img.sprite = s;
            img.color = Color.white;
            return true;
        }

        /// <summary>尝试应用素材并保留调用方 tint（Image color 与 sprite 相乘——节点状态色等语义）。</summary>
        public static bool TryApplyTint(Image img, string name, Color tint)
        {
            if (!TryApply(img, name)) return false;
            img.color = tint;
            return true;
        }

        /// <summary>把素材按 preserveAspect 塞进 RectTransform 尺寸（九宫格不适用的整图按钮）。</summary>
        public static void PreserveAspect(Image img)
        {
            if (img == null) return;
            img.preserveAspect = true;
        }

        // ---------------- 摆放覆盖表（场景视图直编 UI，编辑器工具写入 JSON） ----------------

        [System.Serializable]
        public class UILayoutEntry
        {
            public string path;                 // 相对屏幕根的路径（"StartBtn"、"CoreAnim/Emblem"）
            public float x, y;                   // anchoredPosition
            public float w, h;                   // sizeDelta
        }

        [System.Serializable]
        public class UILayout
        {
            public UILayoutEntry[] items;
        }

        /// <summary>
        /// 应用摆放覆盖表（Resources/UI/Layout_{name}.json）。
        /// 无表=代码默认摆放；有表=以场景视图编辑结果为准（UI 布局编辑器产出）。
        /// 仅覆盖点锚定对象（anchorMin==anchorMax）；Stretch 拉伸对象不适用 anchoredPosition/sizeDelta 语义故跳过。
        /// </summary>
        public static void ApplyLayout(Transform root, string name)
        {
            var ta = Resources.Load<TextAsset>("UI/Layout_" + name);
            if (ta == null || root == null) return;
            var layout = JsonUtility.FromJson<UILayout>(ta.text);
            if (layout == null || layout.items == null) return;
            int applied = 0;
            foreach (var e in layout.items)
            {
                if (e == null || string.IsNullOrEmpty(e.path)) continue;
                if (!(root.Find(e.path) is RectTransform rt)) continue;
                if (rt.anchorMin != rt.anchorMax) continue;
                rt.anchoredPosition = new Vector2(e.x, e.y);
                rt.sizeDelta = new Vector2(e.w, e.h);
                applied++;
            }
            Debug.Log("[UIArt] 布局覆盖表 " + name + " 已应用 " + applied + "/" + layout.items.Length + " 项");
        }
    }
}
