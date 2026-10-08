using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace GeoBreaker
{
    /// <summary>
    /// 地图界面 v2（P5，架构文档 §7）：节点状态化渲染（Locked/Available/Current/Completed/Failed/Boss）、
    /// 连线（通关点亮动画）、8 层竖排拖动滚动 + 当前层自动居中；商店/事件/宝箱/局外解锁子面板沿用。
    /// </summary>
    public class MapScreen : MonoBehaviour
    {
        public static MapScreen I { get; private set; }

        GameObject _root;                          // 全屏面板
        RectTransform _nodeLayer;
        readonly List<GameObject> _nodeButtons = new List<GameObject>();
        readonly List<GameObject> _edges = new List<GameObject>();
        readonly List<(Image img, int fromId)> _edgeImgs = new List<(Image, int)>();
        float _scroll, _scrollMax, _dragMoved;
        Canvas _canvas;
        Coroutine _fadeIn;
        GameObject _shopPanel;
        readonly List<GameObject> _shopCards = new List<GameObject>();

        GameObject _eventPanel;
        GameObject _chestPanel;                              // 宝箱三选一面板（每次打开动态重建）
        GameObject _metaPanel;
        readonly List<GameObject> _metaCards = new List<GameObject>();
        TextMeshProUGUI _metaTitle;
        TextMeshProUGUI _title;
        TextMeshProUGUI _crystalLabel;
        bool _allowRetry;                          // 战斗中途返回地图：当前节点可点重进

        public bool IsOpen => _root != null && _root.activeSelf;
        public bool SubPanelOpen => (_shopPanel != null && _shopPanel.activeSelf)
                                 || (_eventPanel != null && _eventPanel.activeSelf);

        public void Init()
        {
            I = this;
            BuildUi();
        }

        void OnDestroy() { if (I == this) I = null; }

        // ---------------- 主面板 ----------------

        void BuildUi()
        {
            _root = new GameObject("MapScreen");
            _root.transform.SetParent(transform, false);
            var img = _root.AddComponent<Image>();
            img.color = new Color(0.01f, 0.02f, 0.04f, 0.99f);   // 近不透明：防战斗 HUD 文字透出
            UIArt.TryApply(img, "MapBG");                        // AI 星航图背景（缺失回退纯色）
            Stretch(img.rectTransform);
            _canvas = _root.GetComponentInParent<Canvas>();
            _root.AddComponent<MapDragCatcher>().Host = this;   // 拖动滚动（背景即拖动面）

            _title = MakeText("MapTitle", TextAlignmentOptions.Center, 64, NeonStyle.Cyan);
            _title.transform.SetParent(_root.transform, false);
            Place(_title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -80f), new Vector2(900f, 100f));

            _crystalLabel = MakeText("MapCrystal", TextAlignmentOptions.Center, 44, NeonStyle.Blue);
            _crystalLabel.transform.SetParent(_root.transform, false);
            Place(_crystalLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -170f), new Vector2(600f, 70f));

            _nodeLayer = new GameObject("NodeLayer", typeof(RectTransform)).transform as RectTransform;
            _nodeLayer.SetParent(_root.transform, false);

            // 局外解锁入口（左下角，避开节点网格）
            var metaBtn = MakeButton("MetaBtn", "局外解锁", 38, new Vector2(0f, 0f), new Vector2(480f, 96f), NeonStyle.Amber, "BtnSub", true);
            metaBtn.transform.SetParent(_root.transform, false);
            Place(metaBtn.GetComponent<Image>().rectTransform,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(60f, 50f), new Vector2(480f, 96f));
            metaBtn.GetComponent<Button>().onClick.AddListener(OpenMeta);

            _root.SetActive(false);

            UIArt.ApplyLayout(_root.transform, "MapScreen");  // 场景视图摆放覆盖表（UILayoutEditor 产出）
        }

        /// <summary>打开地图（刷新节点与水晶）。胜利后正常返回：只有下一层邻接可点。</summary>
        public void Open()
        {
            _allowRetry = false;
            RefreshAll();
            _root.SetActive(true);
        }

        /// <summary>战斗中途返回地图：当前未通关节点也加入可点集合（原地重试入口）。</summary>
        public void OpenAllowRetry()
        {
            _allowRetry = true;
            RefreshAll();
            _root.SetActive(true);
        }

        /// <summary>上次重建节点后的可点数量（结构化测试）。</summary>
        public int ClickableCount { get; private set; }

        public void Close() => _root.SetActive(false);

        void RefreshAll()
        {
            var map = RunMap.I;
            _title.text = "Roguelite 地图 · 第 " + map.RunLayer + "/" + RunMap.LayerCount + " 层";
            _crystalLabel.text = "水晶 ×" + map.Crystals;
            RebuildNodes();
        }

        void RebuildNodes()
        {
            foreach (var b in _nodeButtons) if (b != null) Destroy(b);
            _nodeButtons.Clear();
            foreach (var e in _edges) if (e != null) Destroy(e);
            _edges.Clear();
            _edgeImgs.Clear();
            if (_fadeIn != null) { StopCoroutine(_fadeIn); _fadeIn = null; }
            var map = RunMap.I;

            // 可点集合：邻接下一层；中途返回（_allowRetry）时当前节点也可点（重进）
            var clickable = new HashSet<MapNode>(map.UnlockedNext());
            if (_allowRetry && map.Current != null) clickable.Add(map.Current);
            ClickableCount = clickable.Count;

            // 连线（先建，垫在节点下）
            for (int r = 0; r < map.Rows.Count - 1; r++)
                foreach (var n in map.Rows[r])
                    foreach (var to in n.next)
                        MakeEdge(NodePos(n), NodePos(to), n.cleared, n.id);

            // 节点（状态化渲染）
            float currentY = 300f;
            for (int r = 0; r < map.Rows.Count; r++)
            {
                var row = map.Rows[r];
                for (int c = 0; c < row.Count; c++)
                {
                    var state = map.StateOf(row[c], clickable);
                    CreateNodeButton(row[c], NodePos(row[c]), state);
                    if (row[c] == map.Current) currentY = NodePos(row[c]).y;
                }
            }

            // 点亮动画：刚通关节点的出边依次淡入
            if (map.LastClearedId >= 0)
            {
                var targets = new List<Image>();
                foreach (var e in _edgeImgs)
                    if (e.fromId == map.LastClearedId) targets.Add(e.img);
                if (targets.Count > 0) _fadeIn = StartCoroutine(FadeEdgesIn(targets));
            }

            // 滚动范围与当前层自动居中（1080×1920 基准，视口半高 960）
            float minY = 300f - (map.Rows.Count - 1) * 260f - 100f;
            _scrollMax = Mathf.Max(0f, -minY - 960f + 140f);
            _scroll = Mathf.Clamp(-currentY - 700f, 0f, _scrollMax);
            _nodeLayer.anchoredPosition = new Vector2(0f, _scroll);
        }

        Vector2 NodePos(MapNode n)
        {
            float total = RunMap.I.Rows[n.row].Count;
            return new Vector2((n.column - (total - 1) * 0.5f) * 340f, 300f - n.row * 260f);
        }

        void MakeEdge(Vector2 a, Vector2 b, bool lit, int fromId)
        {
            var go = new GameObject("Edge", typeof(Image));
            go.transform.SetParent(_nodeLayer, false);
            var img = go.GetComponent<Image>();
            float alpha = lit ? 0.8f : 0.15f;
            img.color = new Color(NeonStyle.Cyan.r, NeonStyle.Cyan.g, NeonStyle.Cyan.b, alpha);
            var rt = img.rectTransform;
            Vector2 d = b - a;
            Place(rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), (a + b) * 0.5f, new Vector2(d.magnitude, 6f));
            rt.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            _edges.Add(go);
            _edgeImgs.Add((img, fromId));
        }

        IEnumerator FadeEdgesIn(List<Image> imgs)
        {
            for (int i = 0; i < imgs.Count; i++)
            {
                var img = imgs[i];
                if (img == null) continue;
                var c = img.color;
                img.color = new Color(c.r, c.g, c.b, 0f);
                StartCoroutine(FadeOneEdge(img, c.a));
                yield return new WaitForSeconds(0.06f);      // 逐条 stagger 点亮
            }
        }

        IEnumerator FadeOneEdge(Image img, float target)
        {
            var c = img.color;
            float t = 0f;
            while (t < 0.25f && img != null)
            {
                t += Time.unscaledDeltaTime;
                img.color = new Color(c.r, c.g, c.b, Mathf.Lerp(0f, target, t / 0.25f));
                yield return null;
            }
        }

        void CreateNodeButton(MapNode node, Vector2 pos, NodeState state)
        {
            bool clickable = state == NodeState.Available
                           || state == NodeState.Current
                           || state == NodeState.Failed;
            Color bg, fg;
            switch (state)
            {
                case NodeState.Available: bg = new Color(0.04f, 0.30f, 0.36f, 0.95f); fg = NeonStyle.Cyan; break;
                case NodeState.Current:   bg = new Color(0.30f, 0.32f, 0.36f, 0.95f); fg = Color.white; break;
                case NodeState.Failed:    bg = new Color(0.36f, 0.08f, 0.10f, 0.95f); fg = NeonStyle.Red; break;
                case NodeState.Completed: bg = new Color(0.05f, 0.16f, 0.14f, 0.60f); fg = NeonStyle.TextDim; break;
                default:                  bg = new Color(0.10f, 0.12f, 0.14f, 0.85f); fg = NeonStyle.TextDim; break;   // Locked
            }
            if (node.type == NodeType.Boss && state != NodeState.Completed) fg = NeonStyle.Red;

            // 当前节点：程序光晕垫底（素材/回退两路径通用）
            if (state == NodeState.Current)
            {
                var glowGo = new GameObject("NodeGlow", typeof(Image));
                glowGo.transform.SetParent(_nodeLayer, false);
                var gImg = glowGo.GetComponent<Image>();
                gImg.sprite = ProcSprites.Glow();
                gImg.color = new Color(1f, 1f, 1f, 0.38f);
                gImg.raycastTarget = false;
                float gSize = node.type == NodeType.Boss ? 510f : 390f;
                Place(gImg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, new Vector2(gSize, gSize));
                _nodeButtons.Add(glowGo);
            }

            // AI 节点图标（UI 美化）：素材可用则按类型换图 + 状态 tint；缺失回退原色块
            string art = ArtForNode(node.type);
            float nodeSize = node.type == NodeType.Boss ? 300f : 230f;

            if (art != null && UIArt.Load(art) != null)
            {
                var btn = new GameObject("NodeBtn", typeof(Image), typeof(Button));
                btn.transform.SetParent(_nodeLayer, false);
                var img = btn.GetComponent<Image>();
                img.color = bg;                                  // 回退色（素材失败时）
                Place(img.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, new Vector2(nodeSize, nodeSize));
                if (UIArt.TryApplyTint(img, art, TintForState(state)))
                    img.preserveAspect = true;
                else
                    img.rectTransform.sizeDelta = new Vector2(280f, 170f);   // 回退旧色块尺寸

                var label = MakeText("NodeLabel", TextAlignmentOptions.Center, node.type == NodeType.Boss ? 52 : 42, fg);
                label.text = (state == NodeState.Completed ? "√" : "") + NodeText(node.type);
                label.transform.SetParent(btn.transform, false);
                Stretch(label.rectTransform);

                if (clickable)
                {
                    var bt = btn.GetComponent<Button>();
                    bt.targetGraphic = img;
                    MapNode captured = node;
                    bt.onClick.AddListener(() => OnNodeClicked(captured));
                }
                _nodeButtons.Add(btn);
                return;
            }

            // —— 无素材回退：原占位实现 ——
            var btnOld = new GameObject("NodeBtn", typeof(Image), typeof(Button));
            btnOld.transform.SetParent(_nodeLayer, false);
            var imgOld = btnOld.GetComponent<Image>();
            imgOld.color = bg;
            Place(imgOld.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, new Vector2(280f, 170f));

            var labelOld = MakeText("NodeLabel", TextAlignmentOptions.Center, 46, fg);
            labelOld.text = (state == NodeState.Completed ? "√" : "") + NodeText(node.type);
            labelOld.transform.SetParent(btnOld.transform, false);
            Stretch(labelOld.rectTransform);

            if (clickable)
            {
                var bt = btnOld.GetComponent<Button>();
                bt.targetGraphic = imgOld;
                MapNode captured = node;
                bt.onClick.AddListener(() => OnNodeClicked(captured));
            }
            _nodeButtons.Add(btnOld);
        }

        /// <summary>节点类型的素材名映射（UI 美化；Event 暂无专属素材→回退色块）。</summary>
        static string ArtForNode(NodeType t) => t switch
        {
            NodeType.Battle => "NodeBattle",
            NodeType.Elite => "NodeElite",
            NodeType.Boss => "NodeBoss",
            NodeType.Shop => "NodeShop",
            NodeType.Chest => "NodeChest",
            NodeType.Event => "NodeEvent",
            _ => null,
        };

        /// <summary>图标素材的状态着色（与 sprite 相乘）：白=原图、红=失败、暗=已过、更暗=锁定。</summary>
        static Color TintForState(NodeState state) => state switch
        {
            NodeState.Available => Color.white,
            NodeState.Current => Color.white,
            NodeState.Failed => new Color(1f, 0.5f, 0.5f, 1f),
            NodeState.Completed => new Color(0.55f, 0.55f, 0.55f, 0.85f),
            _ => new Color(0.45f, 0.45f, 0.45f, 0.6f),
        };

        // ---------------- 拖动滚动 ----------------

        public void OnMapBeginDrag() { _dragMoved = 0f; }

        public void OnMapDrag(Vector2 screenDelta)
        {
            bool metaOpen = _metaPanel != null && _metaPanel.activeSelf;
            if (SubPanelOpen || metaOpen) return;               // 子面板打开时不滚
            float scale = _canvas != null ? Mathf.Max(0.01f, _canvas.scaleFactor) : 1f;
            float dy = screenDelta.y / scale;
            _dragMoved += Mathf.Abs(dy);
            _scroll = Mathf.Clamp(_scroll + dy, 0f, _scrollMax);
            _nodeLayer.anchoredPosition = new Vector2(0f, _scroll);
        }

        public static string NodeText(NodeType t) => t switch
        {
            NodeType.Battle => "战斗",
            NodeType.Elite => "精英",
            NodeType.Shop => "商店",
            NodeType.Event => "事件",
            NodeType.Chest => "宝箱",
            NodeType.Boss => "BOSS",
            _ => "?",
        };

        void OnNodeClicked(MapNode node)
        {
            if (_dragMoved > 30f) { _dragMoved = 0f; return; }  // 拖动地图的松手不算点击
            if (!RunMap.I.Travel(node)) return;
            RunManager.Save();                                   // 进节点即存档（断点续玩）
            switch (node.type)
            {
                case NodeType.Battle:
                case NodeType.Elite:
                case NodeType.Boss:
                    Close();
                    StageManager.I.EnterBattle(node);        // 进战斗
                    break;
                case NodeType.Shop:
                    OpenShop();
                    break;
                case NodeType.Event:
                    OpenEvent();
                    break;
                case NodeType.Chest:
                    OpenChest();
                    break;
            }
        }

        // ---------------- 商店 ----------------

        void BuildShopPanel()
        {
            _shopPanel = new GameObject("ShopPanel");
            _shopPanel.transform.SetParent(_root.transform, false);
            var img = _shopPanel.AddComponent<Image>();
            img.color = new Color(0f, 0.05f, 0.08f, 0.96f);
            UIArt.TryApply(img, "PanelBase");                     // AI 面板底板（缺失回退纯色）
            Stretch(img.rectTransform);

            var title = MakeText("ShopTitle", TextAlignmentOptions.Center, 64, NeonStyle.Blue);
            title.text = "商店 · 选择要购买的强化";
            title.transform.SetParent(_shopPanel.transform, false);
            Place(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 420f), new Vector2(900f, 100f));

            var close = MakeButton("ShopCloseBtn", "离开商店", 40,
                new Vector2(0f, -600f), new Vector2(420f, 100f), NeonStyle.TextDim, "BtnNav", true);
            close.transform.SetParent(_shopPanel.transform, false);
            close.GetComponent<Button>().onClick.AddListener(() => _shopPanel.SetActive(false));

            _shopPanel.SetActive(false);
        }

        void OpenShop()
        {
            if (_shopPanel == null) BuildShopPanel();
            foreach (var c in _shopCards) if (c != null) Destroy(c);
            _shopCards.Clear();

            var picks = BuildState.I.Roll(3);
            float y = 300f;
            foreach (var def in picks)
            {
                var card = MakeButton("ShopCard", def.title + "  |  " + def.desc + "  |  水晶2", 40,
                    new Vector2(0f, y), new Vector2(860f, 190f), NeonStyle.Cyan, "BtnSub", true);
                card.transform.SetParent(_shopPanel.transform, false);
                UpgradeDef captured = def;
                card.GetComponent<Button>().onClick.AddListener(() => Buy(captured));
                _shopCards.Add(card);
                y -= 250f;
            }
            _shopPanel.SetActive(true);
        }

        void Buy(UpgradeDef def)
        {
            const int price = 2;
            if (RunMap.I.SpendCrystals(price) && BuildState.I.Apply(def))
            {
                AudioManager.PlayReward();           // 购买确认音
                RefreshAll();                        // 扣款成功 → 刷新水晶
            }
        }

        // ---------------- 事件 ----------------

        void BuildEventPanel()
        {
            _eventPanel = new GameObject("EventPanel");
            _eventPanel.transform.SetParent(_root.transform, false);
            var img = _eventPanel.AddComponent<Image>();
            img.color = new Color(0f, 0.05f, 0.08f, 0.96f);
            UIArt.TryApply(img, "PanelBase");
            Stretch(img.rectTransform);

            var title = MakeText("EventTitle", TextAlignmentOptions.Center, 64, NeonStyle.Portal);
            title.text = "几何裂隙 · 选择你的回应";
            title.transform.SetParent(_eventPanel.transform, false);
            Place(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 420f), new Vector2(900f, 100f));

            _eventPanel.SetActive(false);
        }

        void OpenEvent()
        {
            if (_eventPanel == null) BuildEventPanel();
            foreach (Transform child in _eventPanel.transform)
                if (child.name == "EvBtn") Destroy(child.gameObject);

            var map = RunMap.I;
            var picks = BuildState.I.Roll(2);
            UpgradeDef upA = picks.Count > 0 ? picks[0] : null;

            // 选项A：献祭 1 球库存 ↔ 获得随机强化
            var a = MakeButton("EvBtn", "献祭 1 颗球库存 → 获得：「" + (upA != null ? upA.title : "强化") + "」", 40,
                new Vector2(0f, 240f), new Vector2(860f, 170f), NeonStyle.Cyan, "BtnSub", true);
            a.transform.SetParent(_eventPanel.transform, false);
            a.GetComponent<Button>().onClick.AddListener(() =>
            {
                BuildState.I.Apply(upA);
                BallManager.I.LoseOneStock();
                _eventPanel.SetActive(false);
                RefreshAll();
            });

            // 选项B：+3 水晶
            var b = MakeButton("EvBtn", "搜刮裂隙 → 获得 3 水晶", 40,
                new Vector2(0f, 20f), new Vector2(860f, 170f), NeonStyle.Blue, "BtnSub", true);
            b.transform.SetParent(_eventPanel.transform, false);
            b.GetComponent<Button>().onClick.AddListener(() =>
            {
                map.AddCrystals(3);
                _eventPanel.SetActive(false);
                RefreshAll();
            });

            var c = MakeButton("EvBtn", "离开", 38,
                new Vector2(0f, -220f), new Vector2(420f, 110f), NeonStyle.TextDim, "BtnNav", true);
            c.transform.SetParent(_eventPanel.transform, false);
            c.GetComponent<Button>().onClick.AddListener(() => _eventPanel.SetActive(false));

            _eventPanel.SetActive(true);
        }

        // ---------------- 宝箱 ----------------

        void OpenChest()
        {
            var node = RunMap.I != null ? RunMap.I.Current : null;
            if (node != null && node.cleared) return;            // 已开过防重领（原地重试重复点击）

            if (_chestPanel != null) { Destroy(_chestPanel); _chestPanel = null; }
            _chestPanel = new GameObject("ChestPanel");
            _chestPanel.transform.SetParent(_root.transform, false);
            var img = _chestPanel.AddComponent<Image>();
            img.color = new Color(0f, 0.05f, 0.08f, 0.96f);
            UIArt.TryApply(img, "PanelBase");
            Stretch(img.rectTransform);

            var title = MakeText("ChestTitle", TextAlignmentOptions.Center, 64, NeonStyle.Amber);
            title.text = "宝箱 · 选择一件补给";
            title.transform.SetParent(_chestPanel.transform, false);
            Place(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 420f), new Vector2(900f, 100f));

            // 道具池：4 种（各有真实机制），随机出 3 件供选择
            var pool = new List<(string label, Color color, System.Action apply)>
            {
                ("弹药补给 ｜ 本 Run 球库存 +2，后续每关生效", NeonStyle.Cyan,
                 () => RunMap.I.AddBonusStock(2)),
                ("水晶袋 ｜ 立即获得 6 水晶", NeonStyle.Blue,
                 () => RunMap.I.AddCrystals(6)),
                ("星辰结晶 ｜ 星尘 +5（局外永久货币）", NeonStyle.Amber,
                 () => { if (MetaProgress.Loaded) MetaProgress.AddStardust(5); }),
            };
            var roll = BuildState.I != null ? BuildState.I.Roll(1) : null;
            if (roll != null && roll.Count > 0)
                pool.Insert(UnityEngine.Random.Range(0, pool.Count),
                    ("强化补给 ｜ 随机获得一层强化：「" + roll[0].title + "」", NeonStyle.Portal,
                     () => BuildState.I.Apply(roll[0])));

            // 洗牌取 3
            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            int offers = Mathf.Min(3, pool.Count);

            for (int i = 0; i < offers; i++)
            {
                int idx = i;
                var item = pool[i];
                var b = MakeButton("ChestItem", item.label, 38,
                    new Vector2(0f, 220f - i * 210f), new Vector2(860f, 170f), item.color, "BtnSub", true);
                b.transform.SetParent(_chestPanel.transform, false);
                b.GetComponent<Button>().onClick.AddListener(() =>
                {
                    item.apply();
                    if (FloatingText.I != null)
                        FloatingText.I.Spawn(new Vector3(0f, 2f, 0f), "宝箱：" + item.label.Split('｜')[0].Trim(), NeonStyle.Amber, 0.6f);
                    if (node != null) node.cleared = true;          // 选完即标记（地图灰显 + 防重领）
                    RunManager.Save();
                    _chestPanel.SetActive(false);
                    RefreshAll();
                });
            }

            _chestPanel.SetActive(true);
        }

        // ---------------- 局外解锁（Phase 10） ----------------

        void BuildMetaPanel()
        {
            _metaPanel = new GameObject("MetaPanel");
            _metaPanel.transform.SetParent(_root.transform, false);
            var img = _metaPanel.AddComponent<Image>();
            img.color = new Color(0f, 0.04f, 0.07f, 0.96f);
            UIArt.TryApply(img, "PanelBase");
            Stretch(img.rectTransform);

            _metaTitle = MakeText("MetaTitle", TextAlignmentOptions.Center, 64, NeonStyle.Amber);
            _metaTitle.text = "局外解锁";
            _metaTitle.transform.SetParent(_metaPanel.transform, false);
            Place(_metaTitle.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 420f), new Vector2(900f, 100f));

            var close = MakeButton("MetaCloseBtn", "离开", 40, new Vector2(-240f, -740f), new Vector2(420f, 100f), NeonStyle.TextDim, "BtnNav", true);
            close.transform.SetParent(_metaPanel.transform, false);
            close.GetComponent<Button>().onClick.AddListener(() => _metaPanel.SetActive(false));

            var reset = MakeButton("MetaResetBtn", "重置存档", 34, new Vector2(240f, -740f), new Vector2(420f, 90f), NeonStyle.TextDim, "BtnNav", true);
            reset.transform.SetParent(_metaPanel.transform, false);
            reset.GetComponent<Button>().onClick.AddListener(() =>
            {
                MetaProgress.ResetAll();
                if (BallManager.I != null) BallManager.I.RefreshKinds();
                OpenMeta();
            });

            _metaPanel.SetActive(false);
        }

        void OpenMeta()
        {
            if (_metaPanel == null) BuildMetaPanel();
            foreach (var c in _metaCards) if (c != null) Destroy(c);
            _metaCards.Clear();

            _metaTitle.text = "局外解锁 · 星尘 ×" + MetaProgress.Stardust;

            // 两列网格：10 项解锁（5 行）单列必撞"离开"——行距压缩 + 按钮下移并排
            float y = 190f;
            for (int i = 0; i < MetaProgress.UnlockDefs.Length; i++)
            {
                var def = MetaProgress.UnlockDefs[i];
                int stacks = MetaProgress.Stacks(def.id);
                bool maxed = stacks >= def.maxStacks;
                string state = maxed
                    ? "已解锁" + (def.maxStacks > 1 ? " ×" + stacks : "")
                    : "星尘 " + def.cost + (stacks > 0 ? "（已有 ×" + stacks + "）" : "");
                float cx = (i % 2 == 0) ? -235f : 235f;
                float cy = y - (i / 2) * 165f;
                var card = MakeButton("MetaCard", def.title + "｜" + def.desc + "｜" + state, 26,
                    new Vector2(cx, cy), new Vector2(450f, 155f),
                    maxed ? NeonStyle.TextDim : NeonStyle.Amber, "BtnSub", true);
                card.transform.SetParent(_metaPanel.transform, false);
                MetaUnlockDef captured = def;
                card.GetComponent<Button>().onClick.AddListener(() => BuyMeta(captured));
                _metaCards.Add(card);
            }
            _metaPanel.SetActive(true);
        }

        void BuyMeta(MetaUnlockDef def)
        {
            if (!MetaProgress.Buy(def.id))
            {
                AdOfferPopup.Show("星尘不足", "看广告补充星尘？", OpenMeta);   // 广告补充后重建刷新
                return;
            }
            AudioManager.PlayReward();                          // 购买确认音
            if (def.id == "GravityBall" && BallManager.I != null)
                BallManager.I.RefreshKinds();                  // 球种循环立即生效
            OpenMeta();                                        // 重建刷新星尘与状态
        }

        // ---------------- UI 工具 ----------------

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
                img.preserveAspect = !stretch;          // stretch=true：胶囊/底板拉伸填满（长条卡片）
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

    /// <summary>地图拖动滚动接收器（P5）：挂 _root——背景即拖动面；
    /// 自节点按钮冒泡（Button 不消费 drag），子面板打开时由 Host 忽略。</summary>
    class MapDragCatcher : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public MapScreen Host;

        public void OnBeginDrag(PointerEventData e) => Host?.OnMapBeginDrag();
        public void OnDrag(PointerEventData e) => Host?.OnMapDrag(e.delta);
        public void OnEndDrag(PointerEventData e) { }
    }
}
