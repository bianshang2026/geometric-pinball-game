using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace GeoBreaker
{
    /// <summary>
    /// PvP 备战面板：选出战球（已解锁）+ 初始强化三选一（词条池随机抽 3）+ 开始对战。
    /// 竞技场主题每局随机（镜像回廊/重力穹界/冰晶窄巷/虚空裂隙）。
    /// </summary>
    public class PvPSetupScreen : MonoBehaviour
    {
        GameObject _canvasGo;
        readonly List<BallData> _kinds = new List<BallData>();
        readonly List<Image> _ballCells = new List<Image>();
        readonly List<Image> _buffCells = new List<Image>();
        readonly List<PvPDamage.BuffDef> _buffOffers = new List<PvPDamage.BuffDef>();
        int _chosenBall = -1;
        string _chosenBuff;

        static readonly Color CellNormal = new Color(0.05f, 0.18f, 0.22f, 0.96f);
        static readonly Color CellChosen = new Color(0.75f, 0.55f, 0.12f, 0.98f);

        public void Open()
        {
            if (_canvasGo != null) return;                           // 已开面板——防重入（连点/残留组件复用会叠画布）
            _kinds.Clear();
            var bm = BallManager.I;
            if (bm != null)
                for (int i = 0; i < bm.KindCount; i++)
                    if (bm.KindAt(i) != null) _kinds.Add(bm.KindAt(i));
            if (_kinds.Count == 0) return;

            // 默认选当前装备球
            for (int i = 0; i < _kinds.Count; i++)
                if (bm != null && bm.CurrentBallData == _kinds[i]) _chosenBall = i;
            if (_chosenBall < 0) _chosenBall = 0;

            // 词条池随机抽 3
            _buffOffers.Clear();
            var pool = new List<PvPDamage.BuffDef>(PvPDamage.Buffs);
            while (_buffOffers.Count < 3 && pool.Count > 0)
            {
                int i = Random.Range(0, pool.Count);
                _buffOffers.Add(pool[i]);
                pool.RemoveAt(i);
            }
            _chosenBuff = _buffOffers.Count > 0 ? _buffOffers[0].id : null;

            _canvasGo = new GameObject("PvPSetupCanvas", typeof(Canvas));
            _canvasGo.transform.SetParent(transform, false);          // ui 根子画布：继承缩放
            var cv = _canvasGo.GetComponent<Canvas>();
            cv.overrideSorting = true;
            cv.sortingOrder = 6;
            _canvasGo.AddComponent<GraphicRaycaster>();                 // 子画布不继承父级 Raycaster——缺它所有按钮收不到点击
            var rt = (RectTransform)_canvasGo.transform;

            // 底幕
            var dimGo = new GameObject("Dim", typeof(Image));
            var dim = dimGo.GetComponent<Image>();
            dim.color = new Color(0f, 0.01f, 0.03f, 0.96f);
            var drt = (RectTransform)dimGo.transform;
            drt.SetParent(rt, false);
            drt.anchorMin = Vector2.zero;
            drt.anchorMax = Vector2.one;
            drt.offsetMin = Vector2.zero;
            drt.offsetMax = Vector2.zero;

            MakeText(rt, "Title", TextAlignmentOptions.Center, 64, NeonStyle.Cyan,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 780f), new Vector2(940f, 96f)).text = "PvP 竞技场 · 1v1";
            MakeText(rt, "Sub1", TextAlignmentOptions.Center, 42, NeonStyle.TextDim,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 672f), new Vector2(700f, 56f)).text = "选择出战球";

            // 球种网格（4 列 × 最多 3 行）
            _ballCells.Clear();
            for (int i = 0; i < _kinds.Count; i++)
            {
                int idx = i;
                int col = i % 4, row = i / 4;
                var cell = MakeCell(rt, "BallCell" + i, _kinds[i].ballName, 38, NeonStyle.Cyan,
                    new Vector2(-360f + col * 240f, 540f - row * 168f), new Vector2(224f, 148f));
                _ballCells.Add(cell);
                int captured = idx;
                cell.GetComponent<Button>().onClick.AddListener(() => { _chosenBall = captured; RefreshTints(); });
            }

            MakeText(rt, "Sub2", TextAlignmentOptions.Center, 42, NeonStyle.TextDim,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 40f), new Vector2(700f, 56f)).text = "初始强化（三选一）";

            _buffCells.Clear();
            for (int i = 0; i < _buffOffers.Count; i++)
            {
                int idx = i;
                var cell = MakeCell(rt, "BuffCell" + i,
                    _buffOffers[i].name + "\n" + _buffOffers[i].desc, 32, NeonStyle.Amber,
                    new Vector2(-340f + i * 340f, -150f), new Vector2(320f, 170f));
                _buffCells.Add(cell);
                string id = _buffOffers[i].id;
                cell.GetComponent<Button>().onClick.AddListener(() => { _chosenBuff = id; RefreshTints(); });
            }

            MakeText(rt, "ArenaNote", TextAlignmentOptions.Center, 36, NeonStyle.TextDim,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -288f), new Vector2(900f, 50f)).text = "弹球对撞 · 每局一发 · 球对球掉血 · 三局两胜 · 竞技场随机";

            var start = MakeCell(rt, "PvpStartBtn", "开始对战", 56, NeonStyle.Cyan,
                new Vector2(0f, -460f), new Vector2(480f, 150f));
            start.GetComponent<Button>().onClick.AddListener(OnStart);

            var back = MakeCell(rt, "PvpBackBtn", "返回", 44, NeonStyle.TextDim,
                new Vector2(0f, -640f), new Vector2(320f, 110f));
            back.GetComponent<Button>().onClick.AddListener(Close);

            RefreshTints();
        }

        void OnStart()
        {
            var cfg = Resources.Load<GameConfig>("GameConfig");
            var bf = Object.FindObjectOfType<BattleField>();
            var predictor = Object.FindObjectOfType<TrajectoryPredictor>();
            if (cfg == null || bf == null || predictor == null || _chosenBall < 0 || _chosenBall >= _kinds.Count) return;
            var ball = _kinds[_chosenBall];
            Close();
            PvPManager.Begin(cfg, bf.Bouncy, predictor, ball, _chosenBuff, UnityEngine.Random.Range(0, 4));
        }

        void RefreshTints()
        {
            for (int i = 0; i < _ballCells.Count; i++)
                _ballCells[i].color = i == _chosenBall ? CellChosen : CellNormal;
            for (int i = 0; i < _buffCells.Count; i++)
                _buffCells[i].color = _buffOffers[i].id == _chosenBuff ? CellChosen : CellNormal;
        }

        public void Close()
        {
            if (_canvasGo != null) { _canvasGo.SetActive(false); Destroy(_canvasGo); }   // 先同步隐藏：Destroy 帧末才生效
            Destroy(this);
        }

        void OnDestroy()
        {
            if (_canvasGo != null) Destroy(_canvasGo);
        }

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

        static Image MakeCell(Transform parent, string name, string label, float fontSize, Color c,
            Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(Image), typeof(Button));
            var img = go.GetComponent<Image>();
            img.color = CellNormal;
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
            lrt.offsetMin = new Vector2(8f, 8f);
            lrt.offsetMax = new Vector2(-8f, -8f);
            return img;
        }
    }
}
