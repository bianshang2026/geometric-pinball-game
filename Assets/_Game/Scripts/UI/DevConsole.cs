using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace GeoBreaker
{
    /// <summary>
    /// 开发者控制台（F1 开关）：
    /// - 星尘/水晶资源加减
    /// - 一键解锁所有球种/全部解锁项
    /// - 球种即时装备（自动记录解锁，绕过星尘）
    /// 仅调试用，随 UI 根常驻，F1 切换显隐。
    /// </summary>
    public class DevConsole : MonoBehaviour
    {
        public static DevConsole I { get; private set; }

        GameObject _panel;
        TMP_Text _status;
        bool _built;
        float _rowY;                                // 行布局游标（自上而下）

        public void Init()
        {
            I = this;
            Build();
            _panel.SetActive(false);
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1)) Toggle();
        }

        public void Toggle()
        {
            if (!_built) return;
            _panel.SetActive(!_panel.activeSelf);
            if (_panel.activeSelf) RefreshStatus();
        }

        void Build()
        {
            _built = true;
            _panel = new GameObject("DevConsole");
            _panel.transform.SetParent(transform, false);
            var img = _panel.AddComponent<Image>();
            img.color = new Color(0f, 0.04f, 0.02f, 0.97f);
            var rt = (RectTransform)_panel.transform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = rt.anchorMin;
            rt.pivot = rt.anchorMin;
            rt.sizeDelta = new Vector2(980f, 1560f);

            var title = MakeText("Title", "开发者控制台 (F1)", 52, NeonStyle.Cyan);
            title.transform.SetParent(_panel.transform, false);
            Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -60f), new Vector2(900f, 90f));

            Row("星尘 +500", () => MetaProgress.AddStardust(500));
            Row("星尘 +5000", () => MetaProgress.AddStardust(5000));
            Row("水晶 +50", () => { if (RunMap.I != null) for (int i = 0; i < 25; i++) RunMap.I.AddCrystals(2); });
            Row("解锁全部球种", () =>
            {
                MetaProgress.Record("GravityBall"); MetaProgress.Record("PierceBall");
                MetaProgress.Record("CurveBall"); MetaProgress.Record("MirrorBall");
                MetaProgress.Record("PhaseBall"); MetaProgress.Record("TimeBall");
                MetaProgress.Record("FrostBall");
                if (BallManager.I != null) BallManager.I.RefreshKinds();
            });
            Row("解锁全部强化（各+1）", () =>
            {
                foreach (var def in MetaProgress.UnlockDefs)
                    if (!MetaProgress.HasUnlock(def.id)) MetaProgress.Record(def.id);
                if (BallManager.I != null) BallManager.I.RefreshKinds();
            });

            string[] kinds = { "Normal", "Splitter", "Explosive", "Prism", "Gravity", "Pierce", "Curve", "Mirror", "Phase", "Time", "Frost" };
            string[] labels = { "普通", "分裂", "爆炸", "激光", "引力", "穿透", "回旋", "镜像", "相位", "时间", "冻结" };
            for (int i = 0; i < kinds.Length; i++)
            {
                string k = kinds[i];
                Row("装备 · " + labels[i], () =>
                {
                    MetaProgress.Record(k);                          // 自动补解锁（绕过星尘）
                    if (BallManager.I != null) BallManager.I.RefreshKinds();
                    BallManager.I.ApplyLoadout(k);
                    PlayerPrefs.SetString("GB_Loadout", k);
                });
            }

            _status = MakeText("Status", "", 34, NeonStyle.TextDim);
            _status.transform.SetParent(_panel.transform, false);
            Place(_status.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, -80f), new Vector2(920f, 640f));

            var hint = MakeText("Hint", "F1 关闭", 30, NeonStyle.TextDim);
            hint.transform.SetParent(_panel.transform, false);
            Place(hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 60f), new Vector2(400f, 50f));
        }

        int _rowIdx;                                // 行序号（布局用）

        /// <summary>添加一行操作按钮（自动纵向布局；点击后刷新状态行）。</summary>
        void Row(string label, System.Action action)
        {
            var btn = MakeButton(label, action);
            btn.transform.SetParent(_panel.transform, false);       // 关键：按钮必须挂进面板（Canvas 下才渲染）
        }

        GameObject MakeButton(string label, System.Action action)
        {
            var btn = new GameObject("Btn_" + label, typeof(Image), typeof(Button));
            var img = btn.GetComponent<Image>();
            img.color = new Color(0.04f, 0.18f, 0.22f, 0.95f);
            var rt = (RectTransform)btn.transform;
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = rt.anchorMin;
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, _rowY);
            rt.sizeDelta = new Vector2(880f, 88f);

            var lbGo = new GameObject("L", typeof(TextMeshProUGUI));
            lbGo.transform.SetParent(btn.transform, false);
            var lt = lbGo.GetComponent<TextMeshProUGUI>();
            lt.fontSize = 32;
            lt.alignment = TextAlignmentOptions.Left;
            lt.color = Color.white;
            lt.text = label;
            lt.raycastTarget = false;
            var lrt = (RectTransform)lbGo.transform;
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(24f, 0f);
            lrt.offsetMax = new Vector2(-24f, 0f);

            btn.GetComponent<Button>().onClick.AddListener(() => { action(); RefreshStatus(); });

            _rowY -= 104f;
            _rowIdx++;
            return btn;
        }

        void RefreshStatus()
        {
            if (_status == null) return;
            _status.text = "星尘 " + (MetaProgress.Loaded ? MetaProgress.Stardust.ToString() : "-")
                + " ｜ 水晶 " + (RunMap.I != null ? RunMap.I.Crystals.ToString() : "-")
                + " ｜ 可用球种 " + (BallManager.I != null ? BallManager.I.KindCount.ToString() : "-") + " 种"
                + "\n当前装备 " + (BallManager.I != null && BallManager.I.CurrentBallData != null ? BallManager.I.CurrentBallData.ballName : "-");
        }

        static TextMeshProUGUI MakeText(string name, string text, float size, Color color)
        {
            var go = new GameObject(name, typeof(TextMeshProUGUI));
            var t = go.GetComponent<TextMeshProUGUI>();
            t.fontSize = size;
            t.color = color;
            t.text = text;
            t.raycastTarget = false;
            return t;
        }

        static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }
    }
}
