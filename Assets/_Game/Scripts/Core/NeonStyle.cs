using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 霓虹视觉风格：全局调色板 + 共享材质。
    /// 配色源自参考图：纯黑负空间 + 淡网格 + 青/黄/红三个高饱和霓虹色相。
    /// </summary>
    public static class NeonStyle
    {
        public static readonly Color Background = RGB(0x05, 0x05, 0x07, 1f);
        public static readonly Color GridLine    = RGB(0x10, 0x18, 0x1C, 0.55f);
        public static readonly Color Cyan        = RGB(0x16, 0xE8, 0xFF, 1f);
        public static readonly Color BlockFill   = RGB(0x0A, 0x2A, 0x33, 0.16f);
        public static readonly Color Yellow      = RGB(0xFF, 0xE0, 0x00, 1f);
        public static readonly Color Red         = RGB(0xE8, 0x18, 0x18, 1f);
        public static readonly Color Orange      = RGB(0xFF, 0xA0, 0x00, 1f);
        public static readonly Color Amber       = RGB(0xFF, 0xB0, 0x00, 1f);
        public static readonly Color Blue        = RGB(0x1F, 0xA2, 0xFF, 1f);
        public static readonly Color White       = Color.white;
        public static readonly Color TextDim     = RGB(0x8A, 0x9A, 0xA8, 0.9f);
        public static readonly Color Portal      = RGB(0xB4, 0x6B, 0xFF, 1f);
        public static readonly Color BombRed     = RGB(0xFF, 0x50, 0x30, 1f);
        public static readonly Color Mirror      = RGB(0xE8, 0xFF, 0xFF, 1f);

        static Material _additive;
        /// <summary>共享的 Additive 辉光材质（拖尾/光晕/瞄准点）。</summary>
        public static Material Additive
        {
            get
            {
                if (_additive == null)
                    _additive = new Material(Shader.Find("GeoBreaker/AdditiveGlow"));
                return _additive;
            }
        }

        static Material _sprite;
        /// <summary>共享的普通 Alpha 混合精灵材质。</summary>
        public static Material Sprite
        {
            get
            {
                if (_sprite == null)
                    _sprite = new Material(Shader.Find("Sprites/Default"));
                return _sprite;
            }
        }

        static Color RGB(float r, float g, float b, float a)
            => new Color(r / 255f, g / 255f, b / 255f, a);

        /// <summary>在父物体下创建一个带 SpriteRenderer 的子物体。</summary>
        public static SpriteRenderer MakeSprite(Transform parent, string name, Sprite sprite, Color color, int order, bool additive)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = order;
            sr.sharedMaterial = additive ? Additive : Sprite;
            sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sr.receiveShadows = false;
            return sr;
        }
    }
}
