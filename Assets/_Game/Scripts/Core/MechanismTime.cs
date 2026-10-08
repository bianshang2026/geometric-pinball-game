using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 机关时间流（时间球）：全局机关减速——自转/开合/Boss 机制/后续移动机关统一读取 Scale。
    /// 球与物理不受影响（只慢机关，不慢游戏）。惰性判断零开销，无需 tick。
    /// </summary>
    public static class MechanismTime
    {
        static float _scale = 1f;
        static float _until = -999f;

        /// <summary>当前机关时间流速（1=正常；Time 球触发期 &lt;1）。</summary>
        public static float Scale => Time.time < _until ? _scale : 1f;

        public static bool Slowed => Time.time < _until;

        /// <summary>触发机关减速（factor=0.5 即慢 50%；重复触发刷新时长）。</summary>
        public static void Slow(float factor, float duration)
        {
            _scale = factor;
            _until = Time.time + duration;
        }
    }
}
