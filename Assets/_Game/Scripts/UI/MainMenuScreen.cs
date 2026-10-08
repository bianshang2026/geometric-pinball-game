using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace GeoBreaker
{
    /// <summary>
    /// 局外大厅（P9，架构 §4）：Logo/纪录/星尘 + 中央几何核心动画 +
    /// 【开始挑战 / 继续挑战】+ 底部四页（球库/构筑/图鉴/商店）。
    /// 全 Panel 互斥切换（单类多面板，与 MapScreen 同风格）。
    /// </summary>
    public class MainMenuScreen : MonoBehaviour
    {
        public static MainMenuScreen I { get; private set; }

        GameObject _root;
        RectTransform _safe;
        Canvas _canvas;
        RectTransform _animRingA, _animRingB;
        TextMeshProUGUI _record;
        TextMeshProUGUI _stardust;
        TextMeshProUGUI _loadoutLabel;
        GameObject _resumeBtn;
        GameObject _subPanel;                     // 当前子页（null=主界面）

        readonly List<GameObject> _ballCards = new List<GameObject>();
        readonly List<GameObject> _buildRows = new List<GameObject>();
        readonly List<GameObject> _shopCards = new List<GameObject>();
        readonly List<GameObject> _codexRows = new List<GameObject>();
        RectTransform _codexScroll;               // 图鉴内容层（拖动滚动）
        float _codexScrollY, _codexScrollMax;

        public bool IsOpen => _root != null && _root.activeSelf;

        public void Init()
        {
            I = this;
            _canvas = GetComponentInParent<Canvas>();
            BuildUi();
            _root.SetActive(false);
        }

        void OnDestroy() { if (I == this) I = null; }

        /// <summary>PvP 对局期间隐藏/恢复大厅（PvPManager.Begin/Exit 调用）。</summary>
        public void SetLobbyVisible(bool v)
        {
            if (_root != null) _root.SetActive(v);
        }

        void OnPvP()
        {
            foreach (var old in GetComponents<PvPSetupScreen>())
                old.Close();                               // 清光所有残留组件（同帧连点会撞上半死组件）
            var s = gameObject.AddComponent<PvPSetupScreen>();
            s.Open();
        }

        // ---------------- 主界面 ----------------

        void BuildUi()
        {
            _root = new GameObject("MainMenu");
            _root.transform.SetParent(transform, false);
            var bg = _root.AddComponent<Image>();
            bg.color = new Color(0.01f, 0.02f, 0.04f, 1f);       // 完全不透明：遮战斗区
            UIArt.TryApply(bg, "MainMenuBG");                   // AI 背景图（缺失回退纯色）
            Stretch(bg.rectTransform);
            _root.AddComponent<SafeAreaRoot>().enabled = true;
            _safe = (RectTransform)_root.transform;

            // TopBar：标题（顶部居中） + 星尘（标题正下方居中——避开角落安全区/挖孔遮挡）
            var logo = MakeText("Logo", TextAlignmentOptions.Center, 72, NeonStyle.Cyan);
            logo.text = "几何弹球";
            logo.transform.SetParent(_safe, false);
            Place(logo.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -44f), new Vector2(640f, 100f));

            _stardust = MakeText("Stardust", TextAlignmentOptions.Center, 46, NeonStyle.Amber);
            _stardust.transform.SetParent(_safe, false);
            Place(_stardust.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -136f), new Vector2(560f, 80f));

            // 广告按钮：星尘右侧"+"——看广告获得星尘（与星尘文本同高：顶锚-136 = 中锚+824）
            var plusBtn = MakeButton("StardustPlus", "+", 48, new Vector2(240f, 824f), new Vector2(84f, 84f), NeonStyle.Amber, "BtnNav", true);
            plusBtn.transform.SetParent(_safe, false);
            plusBtn.GetComponent<Button>().onClick.AddListener(OnStardustPlus);

            // 中央几何核心动画（uGUI Image——Overlay Canvas 下世界 SpriteRenderer 会错位）
            var animGo = new GameObject("CoreAnim", typeof(RectTransform));
            animGo.transform.SetParent(_safe, false);
            var animRt = (RectTransform)animGo.transform;
            Place(animRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 210f), new Vector2(360f, 360f));
            // AI 徽标做核心视觉（缺失时回退程序六边形）
            var emblemGo = new GameObject("Emblem", typeof(Image));
            emblemGo.transform.SetParent(animRt, false);
            var eImg = emblemGo.GetComponent<Image>();
            eImg.raycastTarget = false;
            ((RectTransform)emblemGo.transform).sizeDelta = new Vector2(360f, 430f);
            if (UIArt.TryApply(eImg, "LogoEmblem"))
                eImg.preserveAspect = true;
            else
                MakeUIImage("Hex", ProcSprites.HexagonOutline(), NeonStyle.Cyan, animRt, 320f, 0.95f);
            _animRingA = MakeUIImage("RingA", ProcSprites.Ring(), new Color(NeonStyle.Cyan.r, NeonStyle.Cyan.g, NeonStyle.Cyan.b, 0.55f), animRt, 210f, 0.8f);
            _animRingB = MakeUIImage("RingB", ProcSprites.Ring(), new Color(1f, 1f, 1f, 0.35f), animRt, 130f, 0.6f);
            MakeUIImage("Glow", ProcSprites.Glow(), new Color(NeonStyle.Cyan.r, NeonStyle.Cyan.g, NeonStyle.Cyan.b, 0.16f), animRt, 420f, 0.5f);

            // 纪录行（核心动画下方）
            _record = MakeText("Record", TextAlignmentOptions.Center, 40, NeonStyle.TextDim);
            _record.transform.SetParent(_safe, false);
            Place(_record.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -110f), new Vector2(900f, 70f));

            // 装备展示行
            _loadoutLabel = MakeText("Loadout", TextAlignmentOptions.Center, 42, NeonStyle.Cyan);
            _loadoutLabel.transform.SetParent(_safe, false);
            Place(_loadoutLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -190f), new Vector2(900f, 70f));

            // 继续挑战（有中断 Run 时显示）——胶囊按钮框（拉伸填充，文字必在框内）
            _resumeBtn = MakeButton("ResumeBtn", "继续挑战", 52, new Vector2(0f, -270f), new Vector2(560f, 120f), NeonStyle.Amber, "BtnSub", true);
            _resumeBtn.transform.SetParent(_safe, false);
            _resumeBtn.GetComponent<Button>().onClick.AddListener(OnResume);

            // 开始挑战——六边形主按钮框（BtnMain 素材 1.30:1，框区按比例贴合）
            var startBtn = MakeButton("StartBtn", "开始挑战", 56, new Vector2(0f, -520f), new Vector2(560f, 340f), NeonStyle.Cyan, "BtnMain");
            startBtn.transform.SetParent(_safe, false);
            startBtn.GetComponent<Button>().onClick.AddListener(OnStart);

            // 底部导航：球库 / 构筑 / 图鉴 / 商店——方形按钮框（拉伸填充）
            string[] nav = { "球库", "构筑", "图鉴", "商店" };
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                var btn = MakeButton("Nav" + nav[i], nav[i], 44,
                    new Vector2(-360f + i * 240f, 40f), new Vector2(220f, 110f), NeonStyle.Cyan, "BtnNav", true);
                btn.transform.SetParent(_safe, false);
                btn.GetComponent<Button>().onClick.AddListener(() => OpenSub(idx));
            }

            // PvP 入口（开始挑战右侧·方形钮）
            var pvpBtn = MakeButton("PvPBtn", "PvP\n对战", 40, new Vector2(420f, -520f), new Vector2(200f, 200f), NeonStyle.Red, "BtnNav", true);
            pvpBtn.transform.SetParent(_safe, false);
            pvpBtn.GetComponent<Button>().onClick.AddListener(OnPvP);

            UIArt.ApplyLayout(_root.transform, "MainMenu");   // 场景视图摆放覆盖表（UILayoutEditor 产出）
        }

        void Update()
        {
            if (!IsOpen) return;
            if (_animRingA != null)
                _animRingA.localEulerAngles += new Vector3(0f, 0f, 24f * Time.deltaTime);
            if (_animRingB != null)
                _animRingB.localEulerAngles -= new Vector3(0f, 0f, 38f * Time.deltaTime);
        }

        /// <summary>uGUI 图片（程序 Sprite 直挂 Image；Overlay Canvas 专用视觉）。</summary>
        static RectTransform MakeUIImage(string name, Sprite sprite, Color c, Transform parent, float size, float alpha)
        {
            var go = new GameObject(name, typeof(Image));
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.color = new Color(c.r, c.g, c.b, alpha);
            img.raycastTarget = false;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(size, size);
            return rt;
        }

        /// <summary>打开大厅主界面（刷新纪录/星尘/继续按钮；关闭子页）。</summary>
        public void OpenRoot()
        {
            CloseSub();
            _stardust.text = "星尘 ×" + MetaProgress.Stardust;
            int unlocked = StageLibrary.UnlockedBossCount;
            int best = MetaProgress.Loaded ? MetaProgress.BestLayer : 0;
            _record.text = "已解锁区域 " + unlocked + " / 5 · 最高层数 " + (best > 0 ? best.ToString() : "—");
            _loadoutLabel.text = "首发球种：" + (BallManager.I != null && BallManager.I.CurrentBallData != null
                ? BallManager.I.CurrentBallData.ballName : "普通球");
            _resumeBtn.SetActive(RunManager.HasResumableRun);
            _root.SetActive(true);
            TutorialSystem.EnsureAskPanel();               // 新手教学：首次进大厅询问是否开启
        }

        // ---------------- 流程 ----------------

        void OnStart()
        {
            RunManager.FinishRun();                          // 清旧档
            RunMap.I.Generate();
            _root.SetActive(false);
            StageManager.I.EnterBattle(RunMap.I.Current);   // 起点直进战斗（打完回地图选路线）
        }

        void OnResume()
        {
            if (RunManager.LoadAndResume())
            {
                _root.SetActive(false);
                MapScreen.I.OpenAllowRetry();              // 当前节点可点重进 / 选下层
            }
            else OnStart();
        }

        /// <summary>广告按钮：大厅星尘"+"——先弹询问面板，确认后看广告获得星尘。</summary>
        void OnStardustPlus()
        {
            AdOfferPopup.Show("星尘补给", "看广告获得星尘？", () => _stardust.text = "星尘 ×" + MetaProgress.Stardust);
        }

        // ---------------- 子页路由 ----------------

        int _subIndex = -1;                                 // 当前子页（广告补星尘后重建刷新用）

        void OpenSub(int idx)
        {
            CloseSub();
            _subIndex = idx;
            switch (idx)
            {
                case 0: _subPanel = BuildBallLibrary(); break;
                case 1: _subPanel = BuildBuildPage(); break;
                case 2: _subPanel = BuildCodexPage(); break;
                case 3: _subPanel = BuildShopPage(); break;
            }
        }

        void CloseSub()
        {
            if (_subPanel != null) { Destroy(_subPanel); _subPanel = null; }
            _codexScroll = null;
        }

        // ---------------- 球库（架构 §5）：6 球种卡片，解锁/装备 ----------------

        GameObject BuildBallLibrary()
        {
            var panel = MakeSubPanel("BallLibrary", "球库 · 选择首发球种");
            var kinds = new[]
            {
                Resources.Load<BallData>("NormalBall"),
                Resources.Load<BallData>("SplitBall"),
                Resources.Load<BallData>("ExplosiveBall"),
                Resources.Load<BallData>("PierceBall"),
                Resources.Load<BallData>("PrismBall"),
                Resources.Load<BallData>("GravityBall"),
                Resources.Load<BallData>("CurveBall"),
                Resources.Load<BallData>("MirrorBall"),
                Resources.Load<BallData>("PhaseBall"),
                Resources.Load<BallData>("TimeBall"),
                Resources.Load<BallData>("FrostBall"),
            };
            string loadout = PlayerPrefs.GetString("GB_Loadout", "Normal");
            float y = 620f;
            foreach (var k in kinds)
            {
                if (k == null) continue;
                bool unlocked = IsUnlocked(k.kind);
                bool equipped = loadout == k.kind.ToString();
                string state = !unlocked
                    ? "星尘 " + UnlockOf(k.kind).cost + " 解锁"
                    : (equipped ? "已装备" : "点击装备");
                var card = MakeButton("BallCard", k.ballName + " ｜ " + BallDesc(k.kind) + " ｜ " + state, 32,
                    new Vector2(0f, y), new Vector2(880f, 112f),
                    !unlocked ? NeonStyle.TextDim : equipped ? NeonStyle.Amber : NeonStyle.Cyan, "BtnSub", true);
                card.transform.SetParent(panel.transform, false);
                BallData captured = k;
                card.GetComponent<Button>().onClick.AddListener(() => OnBallCard(captured));
                _ballCards.Add(card);
                y -= 130f;                                   // 11 卡压缩排版：620→-680，全部入屏不压返回钮
            }
            return panel;
        }

        // ---------------- 球种解锁统一映射（球库/购买/装备共用） ----------------

        /// <summary>球种的解锁项 id 与星尘价（id=null 表示默认解锁）。</summary>
        static (string id, float cost) UnlockOf(BallKind kind) => kind switch
        {
            BallKind.Pierce => ("PierceBall", 18f),
            BallKind.Gravity => ("GravityBall", 20f),
            BallKind.Curve => ("CurveBall", 14f),
            BallKind.Mirror => ("MirrorBall", 18f),
            BallKind.Phase => ("PhaseBall", 22f),
            BallKind.Time => ("TimeBall", 16f),
            BallKind.Frost => ("FrostBall", 20f),
            _ => (null, 0f),
        };

        static bool IsUnlocked(BallKind kind)
        {
            var (id, _) = UnlockOf(kind);
            return id == null || MetaProgress.HasUnlock(id);
        }

        void OnBallCard(BallData k)
        {
            if (!IsUnlocked(k.kind))
            {
                if (MetaProgress.Buy(UnlockOf(k.kind).id))
                {
                    AudioManager.PlayReward();
                    BallManager.I.RefreshKinds();
                }
                else
                {
                    AdOfferPopup.Show("星尘不足", "看广告补充星尘？", () => OpenSub(_subIndex));
                    return;                                 // 星尘不足：广告补充后由回调刷新
                }
            }
            else
            {
                PlayerPrefs.SetString("GB_Loadout", k.kind.ToString());
                BallManager.I.ApplyLoadout(k.kind.ToString());
                AudioManager.PlayReward();
            }
            OpenSub(0);                                      // 重建刷新状态
            // 同步主界面装备行
            _loadoutLabel.text = "首发球种：" + k.ballName;
        }

        static string BallDesc(BallKind k) => k switch
        {
            BallKind.Normal => "基础弹射",
            BallKind.Splitter => "碰撞 1→2 球海",
            BallKind.Explosive => "首撞范围爆破",
            BallKind.Pierce => "每发穿透 2 目标",
            BallKind.Prism => "每次碰撞发射穿射激光",
            BallKind.Gravity => "引力场吸引其他球/浮空方块聚团",
            BallKind.Curve => "持续旋向飞行，弹道成可见弧线",
            BallKind.Mirror => "碰撞生成 50% 伤害限时镜像",
            BallKind.Phase => "幽灵穿墙：伤害后穿透方块/机关",
            BallKind.Time => "命中即定格对方球，时停结界减速 60%",
            BallKind.Frost => "命中目标冻结 2.5s 停摆",
            BallKind.Rebound => "每次碰撞弹速+8%，越弹越快",
            _ => "",
        };

        // ---------------- 构筑页（架构 §6）：局外解锁总览 ----------------

        GameObject BuildBuildPage()
        {
            var panel = MakeSubPanel("BuildPage", "构筑 · 局外解锁总览");
            float y = 620f;
            foreach (var def in MetaProgress.UnlockDefs)
            {
                int stacks = MetaProgress.Stacks(def.id);
                bool maxed = stacks >= def.maxStacks;
                string state = maxed
                    ? "已解锁" + (def.maxStacks > 1 ? " ×" + stacks : "")
                    : "星尘 " + def.cost + (stacks > 0 ? "（已有 ×" + stacks + "）" : "");
                var row = MakeButton("BuildRow", def.title + " ｜ " + def.desc + " ｜ " + state, 28,
                    new Vector2(0f, y), new Vector2(880f, 112f),
                    maxed ? NeonStyle.TextDim : NeonStyle.Amber, "BtnSub", true);
                row.transform.SetParent(panel.transform, false);
                MetaUnlockDef captured = def;
                row.GetComponent<Button>().onClick.AddListener(() => OnBuildRow(captured));
                _buildRows.Add(row);
                y -= 130f;                                   // 11 行压缩排版：620→-680，全部入屏不压返回钮
            }
            return panel;
        }

        void OnBuildRow(MetaUnlockDef def)
        {
            if (MetaProgress.Buy(def.id))
            {
                AudioManager.PlayReward();
                if (def.id == "GravityBall" || def.id == "PierceBall")
                    BallManager.I.RefreshKinds();
                OpenSub(1);
            }
            else AdOfferPopup.Show("星尘不足", "看广告补充星尘？", () => OpenSub(_subIndex));   // 构筑/商店共用
        }

        // ---------------- 商店页：星尘购买（与构筑页同源数据） ----------------

        GameObject BuildShopPage()
        {
            var panel = MakeSubPanel("ShopPage", "商店 · 星尘兑换");
            var balance = MakeText("ShopBalance", TextAlignmentOptions.Center, 44, NeonStyle.Amber);
            balance.text = "星尘 ×" + MetaProgress.Stardust;
            balance.transform.SetParent(panel.transform, false);
            Place(balance.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 690f), new Vector2(700f, 70f));

            float y = 550f;
            foreach (var def in MetaProgress.UnlockDefs)
            {
                int stacks = MetaProgress.Stacks(def.id);
                bool maxed = stacks >= def.maxStacks;
                string state = maxed ? "已满层" : "星尘 " + def.cost + (stacks > 0 ? "（已有 ×" + stacks + "）" : "");
                var card = MakeButton("ShopCard", def.title + " ｜ " + def.desc + " ｜ " + state, 28,
                    new Vector2(0f, y), new Vector2(880f, 104f),
                    maxed ? NeonStyle.TextDim : NeonStyle.Amber, "BtnSub", true);
                card.transform.SetParent(panel.transform, false);
                MetaUnlockDef captured = def;
                card.GetComponent<Button>().onClick.AddListener(() => OnBuildRow(captured));
                _shopCards.Add(card);
                y -= 122f;                                   // 11 卡压缩排版：550→-670，全部入屏不压返回钮
            }
            return panel;
        }

        // ---------------- 图鉴页（架构 §5.3）：三册条目 + 拖动滚动 ----------------

        GameObject BuildCodexPage()
        {
            var panel = MakeSubPanel("CodexPage", "图鉴 · 几何圣典");
            var content = new GameObject("CodexScroll", typeof(RectTransform));
            content.transform.SetParent(panel.transform, false);
            var crt = content.GetComponent<RectTransform>();
            Place(crt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000f, 1660f));
            _codexScroll = crt;
            panel.AddComponent<CodexDrag>().Host = this;

            float y = 760f;
            string lastCat = "";
            foreach (var e in CodexData.Entries)
            {
                string cat = e.category.ToString();
                if (cat != lastCat)
                {
                    lastCat = cat;
                    var head = MakeText("Cat", TextAlignmentOptions.TopLeft, 48, NeonStyle.Amber);
                    head.text = cat == "Mechanism" ? "—— 机关 ——" : cat == "Ball" ? "—— 球种 ——" : "—— Boss ——";
                    head.transform.SetParent(crt, false);
                    Place(head.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, y), new Vector2(900f, 80f));
                    y -= 95f;
                }
                var row = MakeText("Entry", TextAlignmentOptions.TopLeft, 36, NeonStyle.TextDim);
                row.text = e.title + "：\n" + e.desc;
                row.transform.SetParent(crt, false);
                Place(row.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, y), new Vector2(920f, 120f));
                _codexRows.Add(row.gameObject);
                y -= 130f;
            }
            _codexScrollMax = Mathf.Max(0f, -(y + 660f));
            _codexScrollY = 0f;
            crt.anchoredPosition = Vector2.zero;
            return panel;
        }

        public void OnCodexDrag(float deltaScreenY)
        {
            if (_codexScroll == null) return;
            float scale = _canvas != null ? Mathf.Max(0.01f, _canvas.scaleFactor) : 1f;
            _codexScrollY = Mathf.Clamp(_codexScrollY + deltaScreenY / scale, 0f, _codexScrollMax);
            _codexScroll.anchoredPosition = new Vector2(0f, _codexScrollY);
        }

        // ---------------- 子页底板 ----------------

        GameObject MakeSubPanel(string name, string title)
        {
            var panel = new GameObject(name);
            panel.transform.SetParent(_root.transform, false);
            var img = panel.AddComponent<Image>();
            img.color = new Color(0f, 0.03f, 0.06f, 1f);      // 完全不透明：防主界面文字透出
            UIArt.TryApply(img, "PanelBase");                 // AI 面板底板（缺失回退纯色）
            Stretch(img.rectTransform);
            var t = MakeText(name + "Title", TextAlignmentOptions.Center, 72, NeonStyle.Cyan);
            t.text = title;
            t.transform.SetParent(panel.transform, false);
            Place(t.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 800f), new Vector2(900f, 110f));
            var back = MakeButton(name + "Back", "返回", 44, new Vector2(0f, -820f), new Vector2(360f, 100f), NeonStyle.TextDim, "BtnNav", true);
            back.transform.SetParent(panel.transform, false);
            back.GetComponent<Button>().onClick.AddListener(OpenRoot);
            return panel;
        }

        // ---------------- UI 工具（与 MapScreen 同风格） ----------------

        static TextMeshProUGUI MakeText(string name, TextAlignmentOptions align, float size, Color color)
        {
            var go = new GameObject(name, typeof(TextMeshProUGUI));
            var t = go.GetComponent<TextMeshProUGUI>();
            t.fontSize = size;
            t.alignment = align;
            t.color = color;
            t.raycastTarget = false;
            return t;
        }

        static GameObject MakeButton(string name, string text, float fontSize, Vector2 pos, Vector2 size, Color color,
            string art = null, bool stretch = false)
        {
            var btn = new GameObject(name, typeof(Image), typeof(Button));
            var img = btn.GetComponent<Image>();
            img.color = new Color(0.04f, 0.18f, 0.22f, 0.95f);
            if (art != null && UIArt.TryApply(img, art))
                img.preserveAspect = !stretch;          // stretch=true：胶囊拉伸填满（长条行）
            Place(img.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size);
            var label = MakeText(name + "Label", TextAlignmentOptions.Center, fontSize, color);
            label.text = text;
            label.transform.SetParent(btn.transform, false);
            Stretch(label.rectTransform);
            return btn;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }
    }

    /// <summary>图鉴面板拖动滚动接收器（复用 MapDrag 模式：背景即拖动面）。</summary>
    class CodexDrag : MonoBehaviour, UnityEngine.EventSystems.IBeginDragHandler, UnityEngine.EventSystems.IDragHandler
    {
        public MainMenuScreen Host;
        public void OnBeginDrag(UnityEngine.EventSystems.PointerEventData e) { }
        public void OnDrag(UnityEngine.EventSystems.PointerEventData e)
        {
            Host?.OnCodexDrag(e.delta.y);
        }
    }
}
