using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>存储抽象（架构 §2.4）：KV 语义——抖音小游戏无文件系统，全部存档走本接口。</summary>
    public interface IStorageService
    {
        void Set(string key, string value);
        string Get(string key, string defaultValue = null);
        bool HasKey(string key);
        void DeleteKey(string key);
        void Flush();                                   // 小游戏 KV 防抖落盘；Unity 直通
    }

    /// <summary>编辑器/Win/Android 实现：PlayerPrefs 直通。</summary>
    public class UnityStorageService : IStorageService
    {
        public void Set(string key, string value) => PlayerPrefs.SetString(key, value);
        public string Get(string key, string defaultValue = null)
            => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : defaultValue;
        public bool HasKey(string key) => PlayerPrefs.HasKey(key);
        public void DeleteKey(string key) => PlayerPrefs.DeleteKey(key);
        public void Flush() => PlayerPrefs.Save();
    }

    /// <summary>内存实现（测试/回放专用；域重载即清空）。</summary>
    public class MemoryStorageService : IStorageService
    {
        readonly Dictionary<string, string> _data = new Dictionary<string, string>();
        public void Set(string key, string value) => _data[key] = value;
        public string Get(string key, string defaultValue = null)
            => _data.TryGetValue(key, out var v) ? v : defaultValue;
        public bool HasKey(string key) => _data.ContainsKey(key);
        public void DeleteKey(string key) => _data.Remove(key);
        public void Flush() { }
    }

    /// <summary>激励视频抽象（架构 §2.4 广告点位）：onDone(true)=完整观看、发放奖励；false=中途关闭/不可用。</summary>
    public interface IAdService
    {
        void ShowRewarded(System.Action<bool> onDone);
    }

    /// <summary>
    /// 编辑器/Win/Android 模拟实现：顶层全屏"广告播放中（模拟）"面板，约 1.2 秒后自动成功。
    /// P12 抖音：换 StarkAdService（穿山胶激励视频），业务层调用点零改动。
    /// </summary>
    public class SimulatedAdService : MonoBehaviour, IAdService
    {
        bool _playing;

        public void ShowRewarded(System.Action<bool> onDone)
        {
            if (_playing) { onDone?.Invoke(false); return; }   // 播放中防重入
            _playing = true;
            StartCoroutine(Play(() => { _playing = false; onDone?.Invoke(true); }));
        }

        System.Collections.IEnumerator Play(System.Action onDone)
        {
            var canvasGo = new GameObject("SimAdCanvas", typeof(Canvas));
            var cv = canvasGo.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 900;                                       // 全局最高层，盖过所有 UI
            canvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();        // 独立 Canvas 必须显式挂

            var bgGo = new GameObject("BG", typeof(UnityEngine.UI.Image));
            bgGo.transform.SetParent(canvasGo.transform, false);
            var bg = bgGo.GetComponent<UnityEngine.UI.Image>();
            bg.color = new Color(0f, 0f, 0f, 0.94f);
            bg.rectTransform.anchorMin = Vector2.zero;
            bg.rectTransform.anchorMax = Vector2.one;
            bg.rectTransform.offsetMin = bg.rectTransform.offsetMax = Vector2.zero;

            var txtGo = new GameObject("Txt", typeof(TMPro.TextMeshProUGUI));
            txtGo.transform.SetParent(canvasGo.transform, false);
            var txt = txtGo.GetComponent<TMPro.TextMeshProUGUI>();
            txt.text = "▶ 广告播放中…（模拟）";
            txt.fontSize = 54;
            txt.alignment = TMPro.TextAlignmentOptions.Center;
            txt.color = NeonStyle.Amber;
            txt.rectTransform.anchorMin = txt.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            txt.rectTransform.sizeDelta = new Vector2(860f, 120f);

            yield return new WaitForSecondsRealtime(1.2f);   // Realtime：不受 timeScale 冻结影响

            UnityEngine.Object.Destroy(canvasGo);
            onDone?.Invoke();
        }
    }

    /// <summary>
    /// 平台服务注册处（架构 §2.4）：业务层只认接口；实现按平台切换。
    /// P12 抖音：在此追加 StarkStorageService/StarkAdService 等实现，业务零改动。
    /// </summary>
    public static class PlatformManager
    {
        public static IStorageService Storage { get; private set; }
        public static IAdService Ad { get; private set; }

        public static void Init()
        {
            if (Storage == null) Storage = new UnityStorageService();   // P12: 按平台切 Stark 实现
            if (Ad == null)
            {
                var go = new GameObject("=== AdService ===");
                Ad = go.AddComponent<SimulatedAdService>();             // P12: 换 StarkAdService
            }
        }
    }
}
