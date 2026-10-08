using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 瞄准计算（架构文档 §12 AimSystem 层）：纯逻辑、零输入依赖，可单测/回放。
    /// 行为与 P3 重构前 BallLauncher 逐参数一致（决策点⑦：现版手感为验收基线）。
    /// 直指瞄准：发射方向 = 指针相对发射点的方向，钳制到 [10°,170°] 仰角扇区（禁直射回收区）。
    /// </summary>
    public static class AimSystem
    {
        /// <summary>
        /// 由按压起点、当前指针、发射点计算瞄准。
        /// 拖动距离 &lt; cancelDist 视为点击取消 → 返回 false。
        /// </summary>
        public static bool TryComputeAim(Vector2 pressStart, Vector2 pointerWorld, Vector2 origin,
            float cancelDist, float minElevRad, out Vector2 dir)
        {
            dir = Vector2.up;
            if (Vector2.Distance(pointerWorld, pressStart) < cancelDist) return false;
            dir = ClampAim(pointerWorld - origin, minElevRad);
            return true;
        }

        /// <summary>方向钳制到仰角扇区 [minElev, 180°-minElev]；下方输入归到最近水平向。</summary>
        public static Vector2 ClampAim(Vector2 dir, float minElevRad)
        {
            if (dir.sqrMagnitude < 0.0001f) return Vector2.up;
            float ang = Mathf.Atan2(dir.y, dir.x);
            if (dir.y < 0f) ang = dir.x < 0f ? Mathf.PI : 0f;      // 下方输入 → 折到水平
            ang = Mathf.Clamp(ang, minElevRad, Mathf.PI - minElevRad);
            return new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
        }
    }
}
