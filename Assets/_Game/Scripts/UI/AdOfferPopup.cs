using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GeoBreaker
{
    /// <summary>
    /// 星尘不足广告补充弹窗（广告按钮系统 · 简约版）：
    /// 独立顶层 Canvas（sortingOrder 800，盖游戏 UI、被模拟广告层 900 盖）+ 中央面板 + 看广告/取消两键。
    /// 看广告成功 → MetaProgress.AddStardust(cfg.adStardustGain) → onGained 回调刷新调用方 UI。
    /// </summary>
    public static class AdOfferPopup
    {
        static GameObject _panel;

        public static bool IsOpen => _panel != null;

        public static void Show(string title, string desc, Action onGained)
        {
            if (_panel != null) return;                       // 已有弹窗
            var cfg = Resources.Load<GameConfig>("GameConfig");
            if (cfg == null) return;
            int gain = cfg.adStardustGain;

            _panel = new GameObject("AdOfferPopup", typeof(Canvas));
            var cv = _panel.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 800;
            _panel.AddComponent<GraphicRaycaster>();          // 独立 Canvas 必须显式挂，按钮才可点

            var dim = new GameObject("Dim", typeof(Image)).GetComponent<Image>();
            dim.transform.SetParent(_panel.transform, false);
            dim.color = new Color(0f, 0f, 0f, 0.6f);
            Stretch(dim.rectTransform);

            var card = new GameObject("Card", typeof(Image)).GetComponent<Image>();
            card.transform.SetParent(_panel.transform, false);
            card.color = new Color(0.02f, 0.06f, 0.09f, 0.98f);
            var crt = card.rectTransform;
            crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f);
            crt.sizeDelta = new Vector2(780f, 500f);

            var t = MakeText(title, 58, NeonStyle.Amber);
            t.transform.SetParent(card.transform, false);
            Place(t.rectTransform, new Vector2(0f, 160f), new Vector2(740f, 90f));

            var d = MakeText(desc, 40, Color.white);
            d.transform.SetParent(card.transform, false);
            Place(d.rectTransform, new Vector2(0f, 60f), new Vector2(720f, 80f));

            // 看广告（琥珀主键）
            var adBtn = new GameObject("AdBtn", typeof(Image), typeof(Button));
            adBtn.transform.SetParent(card.transform, false);
            var aimg = adBtn.GetComponent<Image>();
            aimg.color = new Color(0.30f, 0.20f, 0.02f, 0.98f);
            Place(aimg.rectTransform, new Vector2(0f, -70f), new Vector2(600f, 130f));
            var abt = adBtn.GetComponent<Button>();
            abt.targetGraphic = aimg;
            abt.onClick.AddListener(() =>
            {
                aimg.raycastTarget = false;                    // 播放中防连点
                PlatformManager.Ad.ShowRewarded(ok =>
                {
                    if (!ok) return;
                    MetaProgress.AddStardust(gain);
                    Close();
                    onGained?.Invoke();
                });
            });
            var alab = MakeText("看广告  +" + gain + " 星尘", 46, NeonStyle.Amber);
            alab.transform.SetParent(adBtn.transform, false);
            Stretch(alab.rectTransform);

            // 取消
            var cancel = new GameObject("CancelBtn", typeof(Image), typeof(Button));
            cancel.transform.SetParent(card.transform, false);
            var cimg = cancel.GetComponent<Image>();
            cimg.color = new Color(0.06f, 0.12f, 0.16f, 0.95f);
            Place(cimg.rectTransform, new Vector2(0f, -210f), new Vector2(600f, 110f));
            var cbt = cancel.GetComponent<Button>();
            cbt.targetGraphic = cimg;
            cbt.onClick.AddListener(Close);
            var clab = MakeText("取消", 40, NeonStyle.TextDim);
            clab.transform.SetParent(cancel.transform, false);
            Stretch(clab.rectTransform);
        }

        static void Close()
        {
            if (_panel == null) return;
            UnityEngine.Object.Destroy(_panel);
            _panel = null;
        }

        static TextMeshProUGUI MakeText(string s, float size, Color c)
        {
            var go = new GameObject("Txt", typeof(TextMeshProUGUI));
            var t = go.GetComponent<TextMeshProUGUI>();
            t.text = s;
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.color = c;
            t.raycastTarget = false;
            return t;
        }

        static void Place(RectTransform rt, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }
    }
}
