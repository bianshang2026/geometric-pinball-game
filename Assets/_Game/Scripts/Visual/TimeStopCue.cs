using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace GeoBreaker
{
    /// <summary>
    /// 时停演出（时间球）：命中墙/机关瞬间——
    /// ①世界定格：timeScale 骤降 + 定格帧白闪（hit-stop"砸钟"手感）；
    /// ②全屏暗幕+背景整片洗金（黑底上也看得见）：sorting -1 盖世界不遮 HUD，raycastTarget=false 不拦输入；
    /// ③命中点时停魔法阵：六边阵 + 六符文环绕 + 内环，世界慢了它们照转（全 unscaled）——"时间归你管"；
    /// ④三连冲击环 + 金色火花 + "时停"大字（钳制进画面，防顶墙处被 HUD 遮挡）。
    /// 结界期间（MechanismTime.Slowed）暗幕与魔法阵保持，结束后淡出。
    /// </summary>
    public class TimeStopCue : MonoBehaviour
    {
        public static TimeStopCue I { get; private set; }

        GameConfig _cfg;
        Image _dim;                 // 全屏洗金蒙版（结界期间）
        Image _flash;               // 定格帧白闪
        float _dimAlpha, _flashAlpha, _circleShown;
        Transform _hex;             // 外层六边阵（慢正转）
        Transform _runes;           // 环绕符文组（快反转——方向性读得出旋转）
        Transform _innerRing;       // 内环（快正转）
        bool _freezing;

        /// <summary>测试读数：全屏暗幕当前透明度。</summary>
        public float OverlayAlpha => _dimAlpha;
        /// <summary>测试读数：魔法阵是否在场。</summary>
        public bool CircleActive => _circleShown > 0.05f;

        public void Init(GameConfig cfg)
        {
            I = this;
            _cfg = cfg;

            // 演出画布：ScreenSpaceOverlay 排序 -1——盖住世界、位于 HUD（排序 0）之下
            var cvGo = new GameObject("TimeStopOverlay");
            cvGo.transform.SetParent(transform, false);
            var cv = cvGo.AddComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = -1;

            _dim = MakeFullImage(cv.transform, "Dim");
            _dim.color = new Color(0.01f, 0.0f, 0.03f, 0f);
            _flash = MakeFullImage(cv.transform, "Flash");
            _flash.color = new Color(1f, 0.85f, 0.45f, 0f);

            // 时停魔法阵：六边阵（实心描边，锐利）+ 六符文环绕 + 内环（整体收紧——顶墙命中也完整入画）
            _hex = MakePart("TimeHex", ProcSprites.HexagonOutline(), 3.4f, 0.85f, 40, false);
            _runes = new GameObject("TimeRunes").transform;
            _runes.SetParent(transform, false);
            for (int i = 0; i < 6; i++)
            {
                float a = i * 60f;
                var sr = NeonStyle.MakeSprite(_runes, "Rune", ProcSprites.TriangleOutline(), NeonStyle.Yellow, 41, false);
                sr.color = new Color(NeonStyle.Yellow.r, NeonStyle.Yellow.g, NeonStyle.Yellow.b, 0.9f);
                sr.transform.localRotation = Quaternion.Euler(0f, 0f, a);
                sr.transform.localPosition = (Vector2)(Quaternion.Euler(0f, 0f, a) * Vector2.up) * 2.35f;
                sr.transform.localScale = Vector3.one * 0.45f;
            }
            _innerRing = MakePart("TimeInnerRing", ProcSprites.Ring(), 1.5f, 0.55f, 42, true);
            HideParts();
        }

        Image MakeFullImage(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;                       // 绝不拦截输入
            var rt = (RectTransform)img.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return img;
        }

        Transform MakePart(string name, Sprite sprite, float scale, float alpha, int order, bool additive)
        {
            var sr = NeonStyle.MakeSprite(transform, name, sprite, NeonStyle.Yellow, order, additive);
            sr.color = new Color(NeonStyle.Yellow.r, NeonStyle.Yellow.g, NeonStyle.Yellow.b, alpha);
            sr.transform.localScale = Vector3.one * scale;
            return sr.transform;
        }

        void HideParts()
        {
            if (_hex != null) _hex.gameObject.SetActive(false);
            if (_runes != null) _runes.gameObject.SetActive(false);
            if (_innerRing != null) _innerRing.gameObject.SetActive(false);
        }

        /// <summary>时停爆发（Ball.Time 分支调用；fresh=全新结界——只有全新结界才定格+爆闪，续期只续法阵）。</summary>
        public void Burst(Vector2 point, bool fresh)
        {
            // 世界定格：全场近乎完全冻结半秒（释放时只动自己设的值——防误伤暂停面板的 timeScale=0）
            if (fresh && !_freezing)
            {
                _freezing = true;
                Time.timeScale = _cfg.timeStopFreezeScale;
                _flashAlpha = 0.75f;                         // 定格帧全屏金闪，随后真实时间淡出
                StartCoroutine(FreezeRelease());
            }

            if (_hex != null)
            {
                // 魔法阵与文字钳制进画面（收紧后法阵半径≈1.7，钳 4.8 → 顶点 6.5 < 视野顶 8.0，完整入画）
                float cy = Mathf.Min(point.y, 4.8f);
                Vector3 p = new Vector3(point.x, cy, 0f);
                _hex.position = p;
                _runes.position = p;
                _innerRing.position = p;
                _hex.gameObject.SetActive(true);
                _runes.gameObject.SetActive(true);
                _innerRing.gameObject.SetActive(true);
            }

            StartCoroutine(RingSalvo(point));                 // 真实时间错开——定格期间演出照常走

            if (EffectManager.I != null)
                EffectManager.I.SpawnSparks(point, Vector2.up, 22, 2.6f, NeonStyle.Yellow);
            if (FloatingText.I != null)
            {
                float ty = Mathf.Min(point.y + 0.8f, 5.4f);   // 大字钳制进画面中央区
                FloatingText.I.Spawn(new Vector3(point.x, ty, 0f), "时停", NeonStyle.Yellow, 1.1f);
            }
        }

        IEnumerator FreezeRelease()
        {
            yield return new WaitForSecondsRealtime(_cfg.timeStopFreezeSeconds);
            if (Mathf.Approximately(Time.timeScale, _cfg.timeStopFreezeScale))
                Time.timeScale = 1f;
            if (EffectManager.I != null)
                EffectManager.I.Shake(0.4f, 0.4f);            // 定格解除瞬间震屏——"时间重启"
            _freezing = false;
        }

        IEnumerator RingSalvo(Vector2 point)
        {
            for (int i = 0; i < 3; i++)
            {
                if (EffectManager.I != null)
                    EffectManager.I.PlayRing(point, Color.white, 0.2f, 6.5f + i * 1.5f, 1.1f);   // 白环——金幕上对比清晰
                yield return new WaitForSecondsRealtime(0.22f);
            }
        }

        void Update()
        {
            bool slowed = MechanismTime.Slowed;
            float dt = Time.unscaledDeltaTime;                // 演出全走真实时间——定格期间也要动

            // 全屏洗金蒙版：金色 Image 直接盖在网格/世界上（相机背景被网格遮住，靠它洗金不可见）
            float dimRate = slowed ? 2.2f : 1.1f;             // 快进慢出——结界解除要"缓一口气"
            _dimAlpha = Mathf.MoveTowards(_dimAlpha, slowed ? _cfg.timeStopOverlayAlpha : 0f, dt * dimRate);
            if (_dim != null)
                _dim.color = new Color(0.55f, 0.38f, 0.10f, _dimAlpha);

            // 定格帧白闪淡出（更亮更久——0.4s 全屏金闪，粗采样也拍得到）
            _flashAlpha = Mathf.MoveTowards(_flashAlpha, 0f, dt * 1.7f);
            if (_flash != null)
                _flash.color = new Color(1f, 0.85f, 0.45f, _flashAlpha);

            // 魔法阵：世界慢了，时停之阵照常旋转——结束时淡出
            _circleShown = Mathf.MoveTowards(_circleShown, slowed ? 1f : 0f, dt * 2.5f);
            bool show = _circleShown > 0.02f;
            if (_hex != null)
            {
                _hex.gameObject.SetActive(show);
                _runes.gameObject.SetActive(show);
                _innerRing.gameObject.SetActive(show);
                if (show)
                {
                    _hex.Rotate(0f, 0f, 25f * dt, Space.Self);
                    _runes.Rotate(0f, 0f, -80f * dt, Space.Self);
                    _innerRing.Rotate(0f, 0f, 45f * dt, Space.Self);
                    float k = 0.7f + 0.3f * _circleShown;
                    float pulse = 1f + 0.05f * Mathf.Sin(Time.unscaledTime * 6f);
                    _hex.localScale = Vector3.one * (3.4f * k * pulse);
                    _runes.localScale = Vector3.one * k;
                    _innerRing.localScale = Vector3.one * (1.5f * k);
                }
            }
        }

        void OnDestroy()
        {
            if (I == this) I = null;
            // 防定格残留：退出 Play/域重载前把 timeScale 归位（只动自己设的值）
            if (_cfg != null && Mathf.Approximately(Time.timeScale, _cfg.timeStopFreezeScale))
                Time.timeScale = 1f;
        }
    }
}
