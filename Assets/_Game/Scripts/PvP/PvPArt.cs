using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>PvP 机关美术资产加载（Resources/PvP/*.png，Additive 渲染黑底自动透明）。</summary>
    public static class PvPArt
    {
        static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        public static Sprite Load(string name)
        {
            if (_cache.TryGetValue(name, out var s)) return s;
            s = Resources.Load<Sprite>("PvP/" + name);
            _cache[name] = s;                                   // null 也缓存（缺失只刷一次警告）
            if (s == null)
                Debug.LogWarning("[PvPArt] sprite missing: PvP/" + name);
            return s;
        }
    }
}
