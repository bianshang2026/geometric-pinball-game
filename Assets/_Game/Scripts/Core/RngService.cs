using System;

namespace GeoBreaker
{
    /// <summary>
    /// 确定性随机（架构文档 §10）：地图/修饰全部由 runSeed 派生子流，
    /// 同 seed 完全可复现（测试/回放/未来挑战码分享）。
    /// 子流互不干扰：layer/index 参与哈希。
    /// </summary>
    public static class RngService
    {
        public static int NewSeed() => UnityEngine.Random.Range(int.MinValue, int.MaxValue);

        /// <summary>派生子随机流：For(seed, 3, 0) 与 For(seed, 2, 30) 结果互不影响。</summary>
        public static Random For(int seed, int layer, int index)
            => new Random(Hash(seed, layer, index));

        static int Hash(int seed, int a, int b)
            => unchecked(seed ^ (a * 73856093) ^ (b * 19349663));
    }
}
