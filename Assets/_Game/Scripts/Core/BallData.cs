using UnityEngine;

namespace GeoBreaker
{
    public enum BallKind { Normal, Splitter, Explosive, Prism, Gravity, Pierce, Curve, Mirror, Phase, Time, Frost, Rebound }

    /// <summary>球数据（数据驱动：速度/伤害/半径/颜色/球种行为，后续新增球种只加资产不改代码）。</summary>
    [CreateAssetMenu(fileName = "BallData", menuName = "GeoBreaker/BallData")]
    public class BallData : ScriptableObject
    {
        public string ballName = "普通球";
        public BallKind kind = BallKind.Normal;
        public float speed = 12f;
        public int damage = 1;
        public float radius = 0.28f;
        public float splitAngleDeg = 25f;     // 分裂子球偏转角
        public Color coreColor = Color.white;
        public Color glowColor = new Color(0.086f, 0.91f, 1f, 1f);
        public Color trailColor = new Color(1f, 0.88f, 0f, 1f);
    }
}
