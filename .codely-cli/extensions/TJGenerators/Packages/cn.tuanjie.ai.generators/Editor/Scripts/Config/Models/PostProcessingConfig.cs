using System;
using UnityEngine;

namespace TJGenerators.Config
{
    /// <summary>
    /// 后处理配置
    /// </summary>
    [Serializable]
    public class PostProcessingConfig
    {
        public float modelScale = 1f;
        /// <summary>
        /// 为 true 时，在动画已嵌入同一主 FBX 的流程末尾，
        /// 从主 FBX 取出剪辑并创建仅含 default 状态 + 自循环过渡的 AnimatorController（适用于混元 Motion 等）。
        /// </summary>
        public bool singleClipLoopAnimatorController = false;
        /// <summary>为 true 时在 3D 窗口显示「后处理 / 添加动作」面板（UniRig + 混元 Motion）。</summary>
        public bool enableMotion = false;
        public bool applyScaleToVertices = false;
        public Vector3Config rotation;
        public ImportSettingsConfig importSettings;
    }

    [Serializable]
    public class Vector3Config
    {
        public float x = 0;
        public float y = 0;
        public float z = 0;
        public Vector3 ToVector3() => new Vector3(x, y, z);
    }

    [Serializable]
    public class ImportSettingsConfig
    {
        public string materialImportMode;
        public string animationType;
        public bool importBlendShapes = true;
        public bool importVisibility = true;
        public bool importCameras = false;
        public bool importLights = false;
    }
}
