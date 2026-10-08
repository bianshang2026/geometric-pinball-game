using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace GeoBreaker
{
    /// <summary>PvP 战斗 HUD（弹球对撞模式 v2）：双方球体血条、局分与倒计时、
    /// 回合幕（本局胜/负/平）、终局结算浮层。</summary>
    public class PvPHud : MonoBehaviour
    {
        PvPManager _m;
        RectTransform _p1Fill, _p2Fill;
        TextMeshProUGUI _timer, _p1Label, _p2Label, _hint, _score, _banner, _resultTitle, _resultSub;
        GameObject _resultRoot;

        public void Init(PvPManager m)
        {
            _m = m;
            var go = new GameObject("PvPHudCanvas", typeof(Canvas));
            go.transform.SetParent(m.transform, false);
            var cv = go.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 5;
            go.AddComponent<GraphicRaycaster>();
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
            var rt = (RectTransform)go.transform;

            // 对手（顶）
            _p2Label = MakeText(rt, "P2Label", TextAlignmentOptions.Center, 46, NeonStyle.Red,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -104f), new Vector2(900f, 64f));
            _p2Fill = MakeBar(rt, "P2Bar",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -176f), new Vector2(820f, 42f), NeonStyle.Red);
            _timer = MakeText(rt, "Timer", TextAlignmentOptions.Center, 64, NeonStyle.Amber,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -248f), new Vector2(300f, 84f));

            // 我方（底）
            _p1Label = MakeText(rt, "P1Label", TextAlignmentOptions.Center, 46, NeonStyle.Cyan,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 208f), new Vector2(900f, 64f));
            _p1Fill = MakeBar(rt, "P1Bar",
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 140f), new Vector2(820f, 42f), NeonStyle.Cyan);
            _hint = MakeText(rt, "Hint", TextAlignmentOptions.Center, 38, NeonStyle.TextDim,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 66f), new Vector2(900f, 52f));
            _hint.text = "球就绪 — 拖动瞄准 · 松手发射（每局一发）";

            // 局分（中）
            _score = MakeText(rt, "Score", TextAlignmentOptions.Center, 44, NeonStyle.White,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -320f), new Vector2(700f, 60f));

            // 回合幕
            _banner = MakeText(rt, "Banner", TextAlignmentOptions.Center, 96, NeonStyle.Amber,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 420f), new Vector2(1000f, 130f));
            _banner.text = "";

            // 终局浮层
            _resultRoot = new GameObject("Result", typeof(Image));
            var dim = _resultRoot.GetComponent<Image>();
            dim.color = new Color(0f, 0f, 0.02f, 0.93f);
            dim.raycastTarget = true;
            var rrt = (RectTransform)_resultRoot.transform;
            rrt.SetParent(rt, false);
            rrt.anchorMin = Vector2.zero;
            rrt.anchorMax = Vector2.one;
            rrt.offsetMin = Vector2.zero;
            rrt.offsetMax = Vector2.zero;
            _resultTitle = MakeText(rrt, "Title", TextAlignmentOptions.Center, 110, NeonStyle.Cyan,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 260f), new Vector2(900f, 160f));
            _resultSub = MakeText(rrt, "Sub", TextAlignmentOptions.Center, 48, NeonStyle.Amber,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 90f), new Vector2(900f, 80f));
            var back = MakeButton(rrt, "BackBtn", "回大厅", 52, NeonStyle.Cyan,
                new Vector2(0f, -180f), new Vector2(440f, 140f));
            back.GetComponent<Button>().onClick.AddListener(() => { if (PvPManager.I != null) PvPManager.I.Exit(); });
            _resultRoot.SetActive(false);
        }

        void Update()
        {
            if (_m == null) return;
            if (_timer != null) _timer.text = Mathf.CeilToInt(Mathf.Max(0f, _m.Timer)).ToString();

            var b1 = _m.BallOf(PvPManager.P1);
            var b2 = _m.BallOf(PvPManager.P2);
            UpdateSide(_p1Fill, _p1Label, "你", b1, _m.ChosenBall(PvPManager.P1));
            UpdateSide(_p2Fill, _p2Label, "对手", b2, _m.ChosenBall(PvPManager.P2));

            if (_hint != null)
                _hint.gameObject.SetActive(_m.CurrentPhase != PvPManager.Phase.MatchOver && _m.CanLaunch(PvPManager.P1));

            if (_score != null)
                _score.text = "第 " + _m.Round + "/" + PvPRule.MaxRounds + " 局 · 你 " + _m.WinsOf(PvPManager.P1) +
                    " - " + _m.WinsOf(PvPManager.P2) + " 对手";
        }

        void UpdateSide(RectTransform fill, TextMeshProUGUI label, string who, Ball ball, BallData data)
        {
            if (label != null && data != null)
                label.text = who + " · " + data.ballName;
            if (fill == null) return;
            if (ball != null && ball.IsLive)
                fill.anchorMax = new Vector2(Mathf.Clamp01(ball.PvpHp / ball.PvpHpMax), 1f);
            else if (ball == null)
                fill.anchorMax = new Vector2(1f, 1f);                      // 未发射：满条
            else
                fill.anchorMax = new Vector2(0f, 1f);                       // 已碎：空条
        }

        public void OnRoundStart()
        {
            if (_banner != null) _banner.text = "";
        }

        public void OnRoundEnd(float result)
        {
            if (_banner == null) return;
            _banner.text = result == PvPManager.P1 ? "本局拿下！"
                : result == PvPManager.P2 ? "本局失利…"
                : "平 局";
            _banner.color = result == PvPManager.P1 ? NeonStyle.Cyan
                : result == PvPManager.P2 ? NeonStyle.Red : NeonStyle.Amber;
        }

        public void ShowMatchResult(int result)
        {
            if (_resultRoot == null) return;
            _resultRoot.SetActive(true);
            if (_banner != null) _banner.text = "";
            if (_resultTitle != null)
            {
                _resultTitle.text = result == 0 ? "胜  利！" : result == 1 ? "败  北…" : "平  局";
                _resultTitle.color = result == 0 ? NeonStyle.Cyan : result == 1 ? NeonStyle.Red : NeonStyle.Amber;
            }
            if (_resultSub != null)
                _resultSub.text = "获得星尘 +" + (result == 0 ? PvPRule.WinStardust
                    : result == 1 ? PvPRule.LoseStardust : PvPRule.DrawStardust);
        }

        bool IsMatchOver => _m != null && _m.CurrentPhase == PvPManager.Phase.MatchOver;

        // ---------------- uGUI 原语 ----------------

        static TextMeshProUGUI MakeText(Transform parent, string name, TextAlignmentOptions align,
            float fontSize, Color c, Vector2 aMin, Vector2 aMax, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(TextMeshProUGUI));
            var t = go.GetComponent<TextMeshProUGUI>();
            t.alignment = align;
            t.fontSize = fontSize;
            t.color = c;
            t.raycastTarget = false;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = aMin;
            rt.anchorMax = aMax;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return t;
        }

        static RectTransform MakeBar(Transform parent, string name, Vector2 aMin, Vector2 aMax,
            Vector2 pos, Vector2 size, Color c)
        {
            var go = new GameObject(name, typeof(Image));
            var img = go.GetComponent<Image>();
            img.color = new Color(0.02f, 0.05f, 0.08f, 0.92f);
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = aMin;
            rt.anchorMax = aMax;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var fgo = new GameObject(name + "Fill", typeof(Image));
            var fimg = fgo.GetComponent<Image>();
            fimg.color = c;
            fimg.raycastTarget = false;
            var frt = (RectTransform)fgo.transform;
            frt.SetParent(rt, false);
            frt.anchorMin = Vector2.zero;
            frt.anchorMax = Vector2.one;
            frt.offsetMin = Vector2.zero;
            frt.offsetMax = Vector2.zero;
            return frt;
        }

        static GameObject MakeButton(Transform parent, string name, string label, float fontSize, Color c,
            Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(Image), typeof(Button));
            var img = go.GetComponent<Image>();
            img.color = new Color(0.05f, 0.2f, 0.24f, 0.97f);
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var lblGo = new GameObject(name + "Label", typeof(TextMeshProUGUI));
            var lbl = lblGo.GetComponent<TextMeshProUGUI>();
            lbl.alignment = TextAlignmentOptions.Center;
            lbl.fontSize = fontSize;
            lbl.color = c;
            lbl.raycastTarget = false;
            lbl.text = label;
            var lrt = (RectTransform)lblGo.transform;
            lrt.SetParent(go.transform, false);
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero;
            lrt.offsetMax = Vector2.zero;
            return go;
        }
    }
}
