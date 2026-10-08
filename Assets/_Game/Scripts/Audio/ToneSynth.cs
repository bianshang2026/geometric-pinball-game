using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 程序化合成器（文档 §7）：运行时以 PCM 生成准调五声音符，零资产文件。
    /// 击打质感由 AI 采样承担，音准由本类保证 —— "球=乐器"的音准引擎。
    /// Init 期一次性生成并缓存（44100Hz 单声道，内存 &lt;2MB）。
    /// </summary>
    public static class ToneSynth
    {
        public const int SampleRate = 44100;

        /// <summary>C 大调五声音阶（文档 §7.2）：C4 D4 E4 G4 A4 C5 D5 E5 G5 A5 C6。</summary>
        public static readonly float[] Pentatonic =
        {
            261.63f, 293.66f, 329.63f, 392.00f, 440.00f,
            523.25f, 587.33f, 659.26f, 783.99f, 880.00f, 1046.50f
        };

        /// <summary>音色配方（文档 §7.3）。</summary>
        public enum Flavor { Crystal, Sub, Blip, Pad }

        static readonly Dictionary<string, AudioClip> _cache = new Dictionary<string, AudioClip>();

        /// <summary>按度数取频率（idx 自动循环 11 音）。</summary>
        public static float Freq(int idx) => Pentatonic[((idx % Pentatonic.Length) + Pentatonic.Length) % Pentatonic.Length];

        /// <summary>按度数取缓存音符（Combo/身份音标准入口）。</summary>
        public static AudioClip Get(Flavor flavor, int noteIdx)
            => GetByFreq(flavor, Freq(noteIdx));

        /// <summary>任意频率合成（阶梯/双音/失谐等临时音符），同样缓存。</summary>
        public static AudioClip GetByFreq(Flavor flavor, float freq)
        {
            string key = $"{flavor}_{freq:0.##}";
            if (_cache.TryGetValue(key, out var c) && c != null) return c;
            c = Synth(flavor, freq, DefaultDur(flavor));
            _cache[key] = c;
            return c;
        }

        /// <summary>自定义时长的临时音符（不缓存，慎用）。</summary>
        public static AudioClip Make(Flavor flavor, float freq, float dur) => Synth(flavor, freq, dur);

        static float DefaultDur(Flavor flavor)
        {
            switch (flavor)
            {
                case Flavor.Crystal: return 0.18f;
                case Flavor.Sub: return 0.30f;
                case Flavor.Blip: return 0.07f;
                default: return 0.40f;      // Pad
            }
        }

        /// <summary>合成：正弦谐波叠加 + 快攻指数衰减包络。</summary>
        static AudioClip Synth(Flavor flavor, float f, float dur)
        {
            int n = Mathf.RoundToInt(dur * SampleRate);   // Round：0.18f×44100=7938.0003 → 7938（Ceil 会 +1 采样）
            var data = new float[n];
            float attackN = Mathf.Min(0.005f * SampleRate, n * 0.25f);

            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float k = i / (float)n;                       // 0..1 进度
                float s, env;

                switch (flavor)
                {
                    case Flavor.Crystal:                      // TING：sin + 0.42·2次 + 0.18·3次
                        s = (Mathf.Sin(2f * Mathf.PI * f * t)
                          + 0.42f * Mathf.Sin(4f * Mathf.PI * f * t)
                          + 0.18f * Mathf.Sin(6f * Mathf.PI * f * t)) / 1.6f;
                        env = i < attackN ? i / attackN : Mathf.Pow(1f - k, 3.2f);
                        break;
                    case Flavor.Sub:                          // DOOM：sin + 0.15·2次，慢衰减
                        s = (Mathf.Sin(2f * Mathf.PI * f * t)
                          + 0.15f * Mathf.Sin(4f * Mathf.PI * f * t)) / 1.15f;
                        env = i < attackN ? i / attackN : Mathf.Pow(1f - k, 1.6f);
                        break;
                    case Flavor.Blip:                         // BEEP：纯 sin + 3 采样点瞬态，极快衰减
                        s = Mathf.Sin(2f * Mathf.PI * f * t);
                        if (i < 3) s = 1f;
                        env = Mathf.Pow(1f - k, 4f);
                        break;
                    default:                                   // Pad：sin + 0.3·2次，20ms 攻 + 线性释
                        s = (Mathf.Sin(2f * Mathf.PI * f * t)
                          + 0.3f * Mathf.Sin(4f * Mathf.PI * f * t)) / 1.3f;
                        env = Mathf.Min(i / (0.02f * SampleRate), 1f) * (1f - k);
                        break;
                }
                data[i] = s * env * 0.9f;
            }

            var clip = AudioClip.Create($"{flavor}_{f:0.##}Hz", n, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>缓存条数（测试 T29）。</summary>
        public static int CacheCount => _cache.Count;
    }
}
