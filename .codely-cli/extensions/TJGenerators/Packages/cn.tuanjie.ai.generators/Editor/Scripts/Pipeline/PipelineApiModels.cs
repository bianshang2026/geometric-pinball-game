#if UNITY_EDITOR
using System;

namespace TJGenerators.Pipeline
{
    /// <summary>
    /// 混元 Motion 任务提交体（与 GeneratorConfig hunyuan-motion 字段对齐）
    /// </summary>
    [Serializable]
    public class HyMotionPostPayload
    {
        public string inputText;
        public float actionDuration = 5f;
        public float cfgStrength = 5f;
        public string randomSeedList = "0";
    }
}
#endif
