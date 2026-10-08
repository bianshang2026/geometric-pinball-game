using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 通用组件池（P11 性能，架构 §19）：Take 弹出（池空才 create，计入 CreatedTotal）、
    /// Release 回收（停用+挂池根，脱离战斗场景根防随关卡销毁）。
    /// 战斗构建期 EnsureCount 预热 + MarkWarm 快照，此后战斗中零 Instantiate/Destroy
    /// —— CreatedAfterWarm 即压测统计断言依据。
    /// </summary>
    public class ComponentPool<T> where T : Component
    {
        readonly Stack<T> _free = new Stack<T>();
        readonly string _name;

        public ComponentPool(string name) => _name = name;

        public int CreatedTotal { get; private set; }
        public int ReleasedTotal { get; private set; }
        public int WarmSnapshot { get; private set; }
        public int CreatedAfterWarm => CreatedTotal - WarmSnapshot;
        public int FreeCount => _free.Count;
        public string Name => _name;

        /// <summary>取实例：优先弹池（含死引用防御）；池空时 create 新建（计入 CreatedTotal）。</summary>
        public T Take(Func<T> create)
        {
            while (_free.Count > 0)
            {
                var item = _free.Pop();
                if (item != null) return item;
            }
            CreatedTotal++;
            return create();
        }

        /// <summary>非破坏性取用：池空返回 false（调用方降级，结构性保证零创建）。</summary>
        public bool TryTake(out T item)
        {
            while (_free.Count > 0)
            {
                var it = _free.Pop();
                if (it != null) { item = it; return true; }
            }
            item = null;
            return false;
        }

        /// <summary>预热到 count 个（战斗构建期调用；实例创建后立即回收停用）。</summary>
        public void EnsureCount(int count, Func<T> create)
        {
            while (CreatedTotal < count)
            {
                var item = create();
                CreatedTotal++;
                Release(item);
            }
        }

        /// <summary>回收：停用 + 挂池根。仅在运行中回收（Play 退出销毁期跳过，防挂死根）。</summary>
        public void Release(T item)
        {
            if (item == null || !Application.isPlaying) return;
            var go = item.gameObject;
            if (!go.activeSelf) return;                       // 防重复回收
            go.SetActive(false);
            go.transform.SetParent(Pools.Root, false);
            _free.Push(item);
            ReleasedTotal++;
        }

        public void MarkWarm() => WarmSnapshot = CreatedTotal;
    }

    /// <summary>
    /// 全局池门面 + 池根。五类池对应架构 §19：Ball×8 / Debris×40 / 浮字×20 / 火花×60 / 冲击环×8
    /// （浮字/环/闪光/火花为固定容量内建池，在各自管理器 Init 预热，规模恒定零增长）。
    /// </summary>
    public static class Pools
    {
        static Transform _root;
        public static Transform Root
        {
            get
            {
                if (_root == null)
                {
                    var go = new GameObject("=== Pools ===");
                    _root = go.transform;
                }
                return _root;
            }
        }

        public static readonly ComponentPool<Ball> Ball = new ComponentPool<Ball>("Ball");
        public static readonly ComponentPool<Debris> Debris = new ComponentPool<Debris>("Debris");
        public static readonly ComponentPool<GeometryBlock> Block = new ComponentPool<GeometryBlock>("Block");
        public static readonly ComponentPool<BombBlock> Bomb = new ComponentPool<BombBlock>("Bomb");
        public static readonly ComponentPool<FloatBlock> Float = new ComponentPool<FloatBlock>("Float");

        /// <summary>战斗构建完成标记（此后 CreatedAfterWarm 应保持 0——压测断言）。</summary>
        public static void MarkWarm()
        {
            Ball.MarkWarm();
            Debris.MarkWarm();
            Block.MarkWarm();
            Bomb.MarkWarm();
            Float.MarkWarm();
        }

        public static int CreatedAfterWarmTotal
            => Ball.CreatedAfterWarm + Debris.CreatedAfterWarm + Block.CreatedAfterWarm + Bomb.CreatedAfterWarm
             + Float.CreatedAfterWarm;

        public static string Report()
        {
            return "Ball " + Ball.CreatedAfterWarm + "/" + Ball.CreatedTotal
                 + " | Debris " + Debris.CreatedAfterWarm + "/" + Debris.CreatedTotal
                 + " | Block " + Block.CreatedAfterWarm + "/" + Block.CreatedTotal
                 + " | Bomb " + Bomb.CreatedAfterWarm + "/" + Bomb.CreatedTotal
                 + " | Float " + Float.CreatedAfterWarm + "/" + Float.CreatedTotal
                 + " | 释放 Ball:" + Ball.ReleasedTotal + " Debris:" + Debris.ReleasedTotal
                 + " Block:" + Block.ReleasedTotal + " Bomb:" + Bomb.ReleasedTotal + " Float:" + Float.ReleasedTotal;
        }
    }
}
