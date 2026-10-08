using System;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 统一碰撞事件：Ball 只负责发布，特效/机关/连锁系统各自订阅，
    /// 后续新增机关不需要修改 Ball 核心代码。
    /// </summary>
    public struct BallCollisionEvent
    {
        public Ball ball;
        public Collider2D target;
        public Vector2 point;
        public Vector2 normal;
        public Vector2 velocity;
        public float damage;
    }

    /// <summary>静态事件总线。</summary>
    public static class GameEvents
    {
        public static event Action<BallCollisionEvent> OnBallCollision;
        public static event Action<Ball> OnBallLaunched;
        public static event Action<Ball> OnBallRecycled;
        public static event Action OnAllBallsConsumed;

        /// <summary>连锁等级变化（0=断链）。爆炸/球击可破坏几何体均会累加。</summary>
        public static event Action<int> OnChainChanged;
        /// <summary>任意来源的爆炸（炸弹/爆炸球），参数=爆心。</summary>
        public static event Action<Vector2> OnExplosion;
        /// <summary>核心击破=关卡胜利（单关由 CoreBlock.Die 触发一次）。</summary>
        public static event Action OnVictory;

        public static void RaiseBallCollision(BallCollisionEvent e) => OnBallCollision?.Invoke(e);
        public static void RaiseBallLaunched(Ball b) => OnBallLaunched?.Invoke(b);
        public static void RaiseBallRecycled(Ball b) => OnBallRecycled?.Invoke(b);
        public static void RaiseAllBallsConsumed() => OnAllBallsConsumed?.Invoke();
        public static void RaiseChainChanged(int level) => OnChainChanged?.Invoke(level);
        public static void RaiseExplosion(Vector2 center) => OnExplosion?.Invoke(center);
        public static void RaiseVictory() => OnVictory?.Invoke();
    }
}
