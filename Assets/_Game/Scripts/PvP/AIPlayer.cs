using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// P2 AI 对手（弹球对撞模式 v2.2）：**同时发射**——玩家松手瞬间，AI 球同帧齐发
    /// （PvPManager.LaunchAiCounterpart 驱动，瞄向玩家球飞行前方 ±7° 误差）。
    /// 换真人对战 = 本组件替换为网络输入层（发射方向由远端玩家输入决定，接口不变）。
    /// </summary>
    public class AIPlayer : MonoBehaviour
    {
        // v2.2：AI 发射已并入 PvPManager.TryLaunch 的同帧联动（LaunchAiCounterpart）。
        // 本组件保留作为 AI 身份锚点（后续可挂难度/性格参数）。
    }
}
