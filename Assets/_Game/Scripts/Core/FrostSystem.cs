using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 冻结系统（冻结球）：登记被冻实体，到期恢复视觉（行为侧由 Frozen 惰性判断控制）。
    /// 挂 Bootstrap 的 fx 根，全场景唯一。
    /// </summary>
    public class FrostSystem : MonoBehaviour
    {
        public static FrostSystem I { get; private set; }

        readonly List<GeometryEntity> _frozen = new List<GeometryEntity>();

        void Awake() => I = this;
        void OnDestroy() { if (I == this) I = null; }

        public static void Register(GeometryEntity e)
        {
            if (I == null || e == null || I._frozen.Contains(e)) return;
            I._frozen.Add(e);
        }

        void Update()
        {
            for (int i = _frozen.Count - 1; i >= 0; i--)
            {
                var e = _frozen[i];
                if (e == null || !e.Frozen)
                {
                    if (e != null) e.UnfreezeVisual();
                    _frozen.RemoveAt(i);
                }
            }
        }
    }
}
