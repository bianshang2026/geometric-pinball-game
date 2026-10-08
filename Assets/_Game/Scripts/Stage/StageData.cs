using UnityEngine;

namespace GeoBreaker
{
    public enum GeometryKind { Block, Bomb, Triangle, Diamond, Mirror, Portal,
                               Gravity, OneWay, TimeGate, EnergyNode, BlackHole, MovingWall, SplitPrism, FloatBlock }

    /// <summary>关卡条目（数据驱动布局）。</summary>
    [System.Serializable]
    public class StageEntry
    {
        public GeometryKind kind;
        public Vector2 position;
        public float hp = 3f;
        public float rotationDeg = 0f;
        public float length = 1f;       // Mirror 长度
        public int variant;              // Triangle: 0=加速底边 1=减速底边；Portal: 0=A 1=B；
                                         // Gravity: 0=引力 1=斥力；TimeGate: 相位档位(×0.6s)；OneWay: 未用
        public float spinDeg = 0f;       // Triangle 自旋速度（度/秒，P7 旋转哨卫）
    }

    /// <summary>关卡数据：加关卡=加资产，不改代码。</summary>
    [CreateAssetMenu(fileName = "StageData", menuName = "GeoBreaker/StageData")]
    public class StageData : ScriptableObject
    {
        public int stageId;             // 1-30（Hud 进度/存档定位）
        public string stageName = "新关卡";
        public int ballStock = 3;
        public float coreHp = 10f;
        public Vector2 corePos = new Vector2(0f, 6.5f);
        public StageEntry[] entries = new StageEntry[0];
    }
}
