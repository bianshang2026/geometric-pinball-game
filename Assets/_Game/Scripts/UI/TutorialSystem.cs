using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GeoBreaker
{
    /// <summary>
    /// 新手教学（简约版）：首次进大厅询问是否开启；开启后每种机关/球种首次遭遇时弹一次性介绍卡，
    /// 介绍过的永久静默（存档位图）。文案直接复用 CodexData（机制册/球种册）。
    /// 状态存 PlatformManager.Storage KV：GB_TutAsked（是否问过）/GB_TutOn（是否开启）/GB_TutSeen（位图：
    /// bit0-13=GeometryKind，bit16-27=BallKind）。
    /// </summary>
    public static class TutorialSystem
    {
        const string KeyAsked = "GB_TutAsked";
        const string KeyOn = "GB_TutOn";
        const string KeySeen = "GB_TutSeen";

        static readonly List<(int bit, string title, string desc)> _queue
            = new List<(int, string, string)>();
        static GameObject _panel;

        static IStorageService S => PlatformManager.Storage;

        public static bool Enabled => S != null && S.Get(KeyOn, "0") == "1";
        public static bool ShouldAsk => S != null && S.Get(KeyAsked, "0") != "1";
        public static bool PanelOpen => _panel != null;

        static int SeenMask
        {
            get { int.TryParse(S.Get(KeySeen, "0"), out int v); return v; }
        }

        static bool WasSeen(int bit) => (SeenMask & (1 << bit)) != 0;

        static void MarkSeen(int bit)
        {
            S.Set(KeySeen, (SeenMask | (1 << bit)).ToString());
            S.Flush();
        }

        // ---------------- 首玩询问 ----------------

        /// <summary>大厅打开时调用：第一次玩则询问是否开启教学（问过一次不再问）。</summary>
        public static void EnsureAskPanel()
        {
            if (!ShouldAsk || _panel != null) return;
            var card = BuildBase("新手教学",
                "第一次玩《几何弹球》吗？\n开启教学后，每种机关与球种首次遭遇时会弹出介绍。", 640f,
                out var titleRt, out var descRt);
            // 标题下移给两行描述留位
            titleRt.anchoredPosition = new Vector2(0f, 210f);
            descRt.anchoredPosition = new Vector2(0f, 60f);

            AddButton(card, "开启教学", NeonStyle.Amber, new Vector2(0f, -130f), new Vector2(560f, 120f),
                () => { S.Set(KeyOn, "1"); S.Set(KeyAsked, "1"); S.Flush(); Close(); });
            AddButton(card, "跳过", NeonStyle.TextDim, new Vector2(0f, -260f), new Vector2(420f, 100f),
                () => { S.Set(KeyOn, "0"); S.Set(KeyAsked, "1"); S.Flush(); Close(); });
        }

        // ---------------- 局内触发 ----------------

        /// <summary>关卡构建完成（StageManager.BuildFromData 尾部调用）：扫描本关机关 + 当前球种入队。</summary>
        public static void OnStageBuilt(StageData data)
        {
            if (!Enabled || data == null) return;
            if (data.entries != null)
            {
                foreach (var e in data.entries)
                {
                    if (e.kind == GeometryKind.Block) continue;               // 普通方块无需教学
                    string title = e.kind == GeometryKind.Gravity && e.variant == 1
                        ? "斥力场" : TitleFor(e.kind);
                    if (title == null) continue;
                    Queue((int)e.kind, title, DescOf(CodexData.Category.Mechanism, title));
                }
            }
            var ball = BallManager.I != null && BallManager.I.CurrentBallData != null
                ? BallManager.I.CurrentBallData.kind : BallKind.Normal;
            QueueBall(ball);
            ShowNext();
        }

        /// <summary>球种切换（BallManager.SetBallIndex 尾部调用；仅局内生效）。</summary>
        public static void OnBallChanged(BallKind k)
        {
            if (!Enabled || StageManager.I == null || StageManager.I.Current == null) return;
            QueueBall(k);
            ShowNext();
        }

        static void QueueBall(BallKind k)
        {
            string title = TitleFor(k);
            if (title == null) return;
            Queue(16 + (int)k, title, DescOf(CodexData.Category.Ball, title));
        }

        static void Queue(int bit, string title, string desc)
        {
            if (WasSeen(bit)) return;
            foreach (var q in _queue)
                if (q.bit == bit) return;                                    // 已在队
            _queue.Add((bit, title, desc));
        }

        static void ShowNext()
        {
            if (_panel != null || _queue.Count == 0) return;
            var item = _queue[0];
            _queue.RemoveAt(0);
            MarkSeen(item.bit);                                             // 展示即记（重启不重播）
            var card = BuildBase("教学 · " + item.title, item.desc, 560f,
                out var titleRt, out var descRt);
            titleRt.anchoredPosition = new Vector2(0f, 180f);
            descRt.anchoredPosition = new Vector2(0f, 20f);
            AddButton(card, "知道了", NeonStyle.Cyan, new Vector2(0f, -180f), new Vector2(560f, 120f),
                () => { Close(); ShowNext(); });
        }

        // ---------------- 面板构件（与 AdOfferPopup 同风格） ----------------

        static Transform BuildBase(string title, string desc, float cardH,
                                   out RectTransform titleRt, out RectTransform descRt)
        {
            _panel = new GameObject("TutorialPanel", typeof(Canvas));
            var cv = _panel.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 750;                                           // 盖游戏 UI，被广告层(900)盖
            _panel.AddComponent<GraphicRaycaster>();

            var dim = new GameObject("Dim", typeof(Image)).GetComponent<Image>();
            dim.transform.SetParent(_panel.transform, false);
            dim.color = new Color(0f, 0f, 0f, 0.55f);
            Stretch(dim.rectTransform);

            var cardGo = new GameObject("Card", typeof(Image));
            cardGo.transform.SetParent(_panel.transform, false);
            var card = cardGo.GetComponent<Image>();
            card.color = new Color(0.02f, 0.06f, 0.09f, 0.98f);
            var crt = card.rectTransform;
            crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f);
            crt.sizeDelta = new Vector2(820f, cardH);

            var t = MakeText(title, 56, NeonStyle.Amber);
            t.transform.SetParent(cardGo.transform, false);
            titleRt = t.rectTransform;
            titleRt.anchorMin = titleRt.anchorMax = new Vector2(0.5f, 0.5f);
            titleRt.sizeDelta = new Vector2(780f, 90f);

            var d = MakeText(desc, 38, Color.white);
            d.transform.SetParent(cardGo.transform, false);
            descRt = d.rectTransform;
            descRt.anchorMin = descRt.anchorMax = new Vector2(0.5f, 0.5f);
            descRt.sizeDelta = new Vector2(740f, 220f);
            return cardGo.transform;
        }

        static void AddButton(Transform card, string label, Color color, Vector2 pos, Vector2 size, System.Action onClick)
        {
            var go = new GameObject("TutBtn", typeof(Image), typeof(Button));
            go.transform.SetParent(card, false);
            var img = go.GetComponent<Image>();
            img.color = new Color(color.r, color.g, color.b, 0.18f);
            img.rectTransform.anchorMin = img.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            img.rectTransform.anchoredPosition = pos;
            img.rectTransform.sizeDelta = size;
            var bt = go.GetComponent<Button>();
            bt.targetGraphic = img;
            bt.onClick.AddListener(() => onClick());
            var t = MakeText(label, 44, color);
            t.transform.SetParent(go.transform, false);
            Stretch(t.rectTransform);
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

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        // ---------------- 文案映射（CodexData 标题） ----------------

        static string TitleFor(GeometryKind k) => k switch
        {
            GeometryKind.Bomb => "炸弹",
            GeometryKind.Triangle => "三角反射器",
            GeometryKind.Diamond => "菱形加速器",
            GeometryKind.Mirror => "镜面",
            GeometryKind.Portal => "传送门",
            GeometryKind.Gravity => "引力场",
            GeometryKind.OneWay => "单向墙",
            GeometryKind.TimeGate => "时间门",
            GeometryKind.EnergyNode => "能量节点",
            GeometryKind.BlackHole => "黑洞",
            GeometryKind.MovingWall => "移动墙",
            GeometryKind.SplitPrism => "分裂棱镜",
            GeometryKind.FloatBlock => "浮空方块",
            _ => null,                        // Block 不教
        };

        static string TitleFor(BallKind k) => k switch
        {
            BallKind.Normal => "普通球",
            BallKind.Splitter => "分裂球",
            BallKind.Explosive => "爆炸球",
            BallKind.Pierce => "穿透球",
            BallKind.Prism => "激光球",
            BallKind.Gravity => "引力球",
            BallKind.Curve => "回旋球",
            BallKind.Mirror => "镜像球",
            BallKind.Phase => "相位球",
            BallKind.Time => "时间球",
            BallKind.Frost => "冻结球",
            BallKind.Rebound => "反弹球",
            _ => null,
        };

        static string DescOf(CodexData.Category c, string title)
        {
            foreach (var e in CodexData.Entries)
                if (e.category == c && e.title == title) return e.desc;
            return "";
        }
    }
}
