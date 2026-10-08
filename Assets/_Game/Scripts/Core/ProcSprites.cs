using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 运行时程序化生成全部 2D 视觉素材（Texture2D -> Sprite），零外部美术。
    /// 所有精灵统一 64 像素/单位，即贴图边长/64 = 世界单位尺寸。
    /// </summary>
    public static class ProcSprites
    {
        static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        static Sprite Cache(string key, System.Func<Sprite> make)
        {
            if (_cache.TryGetValue(key, out var s)) return s;
            s = make();
            _cache[key] = s;
            return s;
        }

        static Sprite Bake(int size, System.Func<int, int, Color32> pixel, FilterMode filter = FilterMode.Bilinear)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
            };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = pixel(x, y);
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 64f);
        }

        /// <summary>柔边实心圆（球芯、粒子用）。</summary>
        public static Sprite CircleSoft() => Cache("circle", () =>
            Bake(64, (x, y) =>
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(31.5f, 31.5f)) / 32f;
                byte a = (byte)(Mathf.Clamp01(1f - d) * 255f);
                return new Color32(255, 255, 255, a);
            }));

        /// <summary>径向衰减光晕（辉光、瞄准光点）。</summary>
        public static Sprite Glow() => Cache("glow", () =>
            Bake(256, (x, y) =>
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(127.5f, 127.5f)) / 128f;
                float f = Mathf.Clamp01(1f - d);
                f *= f;
                return new Color32(255, 255, 255, (byte)(f * 255f));
            }));

        /// <summary>空心圆环（冲击波、反弹点标记）。</summary>
        public static Sprite Ring() => Cache("ring", () =>
            Bake(128, (x, y) =>
            {
                float d = Mathf.Abs(Vector2.Distance(new Vector2(x, y), new Vector2(63.5f, 63.5f)) - 64f);
                byte a = (byte)(Mathf.Clamp01(1f - d / 7f) * 255f);
                return new Color32(255, 255, 255, a);
            }));

        /// <summary>方形描边（霓虹方块轮廓、墙线）。</summary>
        public static Sprite SquareOutline() => Cache("sqo", () =>
            Bake(64, (x, y) =>
            {
                float dx = Mathf.Abs(x - 31.5f);
                float dy = Mathf.Abs(y - 31.5f);
                float d = 32f - Mathf.Max(dx, dy);
                byte a = (byte)(Mathf.Clamp01(d / 4f) * 255f);
                return new Color32(255, 255, 255, a);
            }));

        /// <summary>实心方形（墙线、方块底色）。</summary>
        public static Sprite SquareFill() => Cache("sqf", () =>
            Bake(32, (x, y) => new Color32(255, 255, 255, 255)));

        /// <summary>实心三角（球体红色瓣装饰）。</summary>
        public static Sprite Triangle() => Cache("tri", () =>
            Bake(64, (x, y) =>
            {
                var p = new Vector2(x, y);
                var a = new Vector2(32f, 4f);
                var b = new Vector2(6f, 58f);
                var c = new Vector2(58f, 58f);
                float s1 = Sign(p, a, b), s2 = Sign(p, b, c), s3 = Sign(p, c, a);
                bool neg = s1 < 0f || s2 < 0f || s3 < 0f;
                bool pos = s1 > 0f || s2 > 0f || s3 > 0f;
                return (neg && pos)
                    ? new Color32(255, 255, 255, 0)
                    : new Color32(255, 255, 255, 255);
            }));

        static float Sign(Vector2 p, Vector2 a, Vector2 b)
            => (p.x - a.x) * (b.y - a.y) - (b.x - a.x) * (p.y - a.y);

        /// <summary>四角星（背景点缀）。</summary>
        public static Sprite Star4() => Cache("star", () =>
            Bake(128, (x, y) =>
            {
                Vector2 p = new Vector2(x, y) - new Vector2(63.5f, 63.5f);
                float spikeX = Mathf.Clamp01(1f - Mathf.Abs(p.y) / 10f) * Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(p.x) / 64f), 2f);
                float spikeY = Mathf.Clamp01(1f - Mathf.Abs(p.x) / 10f) * Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(p.y) / 64f), 2f);
                float core = Mathf.Clamp01(1f - p.magnitude / 12f);
                byte a = (byte)(Mathf.Clamp01(Mathf.Max(Mathf.Max(spikeX, spikeY), core)) * 255f);
                return new Color32(255, 255, 255, a);
            }));

        /// <summary>空心三角描边（尖朝上，几何尺寸与 s=0.92 的等边三角碰撞体对齐，贴图1.5单位）。</summary>
        public static Sprite TriangleOutline() => Cache("trioutline", () =>
            Bake(96, (x, y) =>
            {
                var p = new Vector2(x, y);
                var a = new Vector2(48f, 82f);    // 顶点（上）
                var b = new Vector2(19f, 31f);    // 左下
                var c = new Vector2(77f, 31f);    // 右下
                float d = Mathf.Min(DistSeg(p, a, b), DistSeg(p, b, c), DistSeg(p, c, a));
                byte alpha = (byte)(Mathf.Clamp01(1f - d / 4.5f) * 255f);
                return new Color32(255, 255, 255, alpha);
            }));

        /// <summary>实心三角底（与 TriangleOutline 同形内缩约 2px、边缘 2.5px 软化——三角反射器玻璃底）。</summary>
        public static Sprite TriangleFill() => Cache("trifill", () =>
            Bake(96, (x, y) =>
            {
                var p = new Vector2(x, y);
                var a = new Vector2(48f, 79.5f);   // 顶点（上，内缩）
                var b = new Vector2(21f, 33f);    // 左下
                var c = new Vector2(75f, 33f);    // 右下
                float s1 = Sign(p, a, b), s2 = Sign(p, b, c), s3 = Sign(p, c, a);
                bool neg = s1 < 0f || s2 < 0f || s3 < 0f;
                bool pos = s1 > 0f || s2 > 0f || s3 > 0f;
                if (neg && pos) return new Color32(255, 255, 255, 0);   // 三角外全透明
                float d = Mathf.Min(DistSeg(p, a, b), DistSeg(p, b, c), DistSeg(p, c, a));
                byte al = (byte)(Mathf.Clamp01(d / 2.5f) * 255f);       // 边缘软化
                return new Color32(255, 255, 255, al);
            }));

        static float DistSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 0.0001f));
            return Vector2.Distance(p, a + ab * t);
        }

        /// <summary>空心六边形描边（核心◎轮廓，贴图1.5单位，顶点半径44px≈0.69u）。</summary>
        public static Sprite HexagonOutline() => Cache("hexoutline", () =>
            Bake(96, (x, y) =>
            {
                var p = new Vector2(x - 48f, y - 48f);
                float d = 999f;
                Vector2 prev = default;
                for (int i = 0; i <= 6; i++)
                {
                    float a = i * 60f * Mathf.Deg2Rad;
                    var v = new Vector2(Mathf.Cos(a) * 44f, Mathf.Sin(a) * 44f);
                    if (i > 0) d = Mathf.Min(d, DistSeg(p, prev, v));
                    prev = v;
                }
                byte alpha = (byte)(Mathf.Clamp01(1f - d / 4.5f) * 255f);
                return new Color32(255, 255, 255, alpha);
            }));

        /// <summary>整幅背景网格：每 64px(=1 世界单位) 一条 1px 淡线，背景色烘焙进贴图。</summary>
        public static Sprite GridBackground(int cellsX, int cellsY) => Cache($"grid_{cellsX}x{cellsY}", () =>
        {
            const int P = 64;
            int w = cellsX * P, h = cellsY * P;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            Color bg = NeonStyle.Background;
            Color line = NeonStyle.GridLine;
            Color baked = Color.Lerp(bg, line, line.a);
            baked.a = 1f;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = (x % P == 0 || y % P == 0 || x == w - 1 || y == h - 1)
                        ? (Color32)baked
                        : (Color32)bg;
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), P);
        });
    }
}
