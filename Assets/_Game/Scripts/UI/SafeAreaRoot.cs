using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 安全区容器（架构文档 §11/§2.1）：Screen.safeArea → RectTransform 锚点，
    /// 刘海/挖孔/手势条设备自动收窄；编辑器与 PC 全屏（无损）。
    /// 所有战斗/大厅 UI 元素应挂在本容器下。
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class SafeAreaRoot : MonoBehaviour
    {
        RectTransform _rt;

        void Awake()
        {
            _rt = (RectTransform)transform;
            Apply();
        }

        void OnEnable() => Apply();

        public void Apply()
        {
            if (_rt == null) _rt = (RectTransform)transform;
            Rect sa = Screen.safeArea;
            Vector2 min = new Vector2(sa.xMin / Screen.width, sa.yMin / Screen.height);
            Vector2 max = new Vector2(sa.xMax / Screen.width, sa.yMax / Screen.height);
            _rt.anchorMin = min;
            _rt.anchorMax = max;
            _rt.offsetMin = Vector2.zero;
            _rt.offsetMax = Vector2.zero;
        }
    }
}
