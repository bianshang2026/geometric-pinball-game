using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace GeoBreaker
{
    /// <summary>
    /// 战斗 HUD（P4 重排，架构文档 §11）：SafeArea 容器内
    /// TopBar=暂停(左)/进度(中)/设置(右)；BottomBar=状态合并行(左)+球种按钮(左下拇指区)；
    /// 暂停面板（timeScale 冻结）与音量设置面板；胜利/失败/奖励浮层沿用。
    /// </summary>
    public class Hud : MonoBehaviour
    {
        GameConfig _cfg;
        BallManager _balls;
        BallLauncher _launcher;
        RectTransform _safe;                      // SafeArea 容器（刘海/手势条自适应）
        TextMeshProUGUI _stock;                   // 合并状态行：球 ×N · 强化 ×N · 水晶 ×N
        TextMeshProUGUI _hint;
        TextMeshProUGUI _chain;
        TextMeshProUGUI _ballBtnLabel;
        TextMeshProUGUI _stageLabel;
        TextMeshProUGUI _winTitle;
        TextMeshProUGUI _winChain;
        TextMeshProUGUI _winHit;
        TextMeshProUGUI _winCount;
        TextMeshProUGUI _winReward;
        Coroutine _chainPulse;
        GameObject _over;
        GameObject _win;
        GameObject _runOver;
        GameObject _pausePanel;
        GameObject _settingsPanel;
        int _rewardPickCount;
        GameObject _reward;
        readonly System.Collections.Generic.List<GameObject> _cards =
            new System.Collections.Generic.List<GameObject>();

        public bool OverlayActive => _over != null && _over.activeSelf;
        public bool VictoryActive => _win != null && _win.activeSelf;
        public bool RewardActive => _reward != null && _reward.activeSelf;
        public bool PauseOpen => _pausePanel != null && _pausePanel.activeSelf;
        public bool SettingsOpen => _settingsPanel != null && _settingsPanel.activeSelf;
        public RectTransform SafeAreaTransform => _safe;

        public void Init(GameConfig cfg, BallManager balls, BallLauncher launcher)
        {
            _cfg = cfg;
            _balls = balls;
            _launcher = launcher;

            var canvas = gameObject.GetComponent<Canvas>();
            if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = gameObject.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;

            if (gameObject.GetComponent<GraphicRaycaster>() == null)
                gameObject.AddComponent<GraphicRaycaster>();
            if (FindObjectOfType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            // SafeArea 容器：全部 UI 挂其下（刘海/手势条自适应；编辑器全屏无损）
            var safeGo = new GameObject("SafeArea", typeof(RectTransform));
            safeGo.transform.SetParent(transform, false);
            _safe = safeGo.GetComponent<RectTransform>();
            Stretch(_safe);
            safeGo.AddComponent<SafeAreaRoot>();

            // ---- TopBar：暂停(左) / 进度(中) / 设置(右) ----
            var pauseBtn = new GameObject("PauseBtn", typeof(Image), typeof(Button));
            pauseBtn.transform.SetParent(_safe, false);
            var pimg = pauseBtn.GetComponent<Image>();
            pimg.color = new Color(0.04f, 0.18f, 0.22f, 0.85f);
            Place(pimg.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -24f), new Vector2(96f, 96f));
            var pbt = pauseBtn.GetComponent<Button>();
            pbt.targetGraphic = pimg;
            pbt.onClick.AddListener(OpenPause);
            var pauseLabel = MakeText("PauseLabel", TextAlignmentOptions.Center, 52, NeonStyle.Cyan);
            pauseLabel.text = "Ⅱ";
            pauseLabel.transform.SetParent(pauseBtn.transform, false);
            Stretch(pauseLabel.rectTransform);

            _stageLabel = MakeText("Stage", TextAlignmentOptions.Center, 42, NeonStyle.TextDim);
            _stageLabel.transform.SetParent(_safe, false);
            Place(_stageLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -48f), new Vector2(640f, 76f));

            var setBtn = new GameObject("SettingsBtn", typeof(Image), typeof(Button));
            setBtn.transform.SetParent(_safe, false);
            var simg = setBtn.GetComponent<Image>();
            simg.color = new Color(0.04f, 0.18f, 0.22f, 0.85f);
            bool hasSetIcon = UIArt.TryApply(simg, "SettingsIcon");   // AI 设置图标（徽标底部裁剪；缺素材回退色块+"设"字）
            if (hasSetIcon) simg.preserveAspect = true;
            Place(simg.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, -24f), new Vector2(96f, 96f));
            var sbt = setBtn.GetComponent<Button>();
            sbt.targetGraphic = simg;
            sbt.onClick.AddListener(OpenSettings);
            var setLabel = MakeText("SettingsLabel", TextAlignmentOptions.Center, 46, NeonStyle.Cyan);
            setLabel.text = "设";                   // ⚙ 在 TMP 默认字体缺字形，用汉字（有图标时隐藏）
            setLabel.transform.SetParent(setBtn.transform, false);
            setLabel.gameObject.SetActive(!hasSetIcon);
            Stretch(setLabel.rectTransform);

            // ---- 连锁（TopBar 下方，仅 ≥2 显示） ----
            _chain = MakeText("Chain", TextAlignmentOptions.Center, 62, NeonStyle.Yellow);
            _chain.transform.SetParent(_safe, false);
            Place(_chain.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -160f), new Vector2(800f, 90f));
            _chain.alpha = 0f;

            // ---- BottomBar：状态合并行(左下偏上) + 球种按钮(左下拇指区) + 提示 ----
            _stock = MakeText("Stock", TextAlignmentOptions.BottomLeft, 40, Color.white);
            _stock.transform.SetParent(_safe, false);
            Place(_stock.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(30f, 124f), new Vector2(1020f, 60f));

            _hint = MakeText("Hint", TextAlignmentOptions.Center, 40, NeonStyle.TextDim);
            _hint.transform.SetParent(_safe, false);
            Place(_hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 240f), new Vector2(1000f, 70f));
            _hint.text = "按住屏幕拖动瞄准 · 松手发射";

            BuildBallButton();
            BuildPausePanel();
            BuildSettingsPanel();
            BuildGameOver();
            BuildVictoryOverlay();
            BuildRewardPanel();
            BuildRunOverOverlay();

            GameEvents.OnBallLaunched += OnLaunched;
            GameEvents.OnAllBallsConsumed += ShowGameOver;
            GameEvents.OnChainChanged += OnChainChanged;
            GameEvents.OnVictory += ShowVictory;
        }

        void OnDestroy()
        {
            GameEvents.OnBallLaunched -= OnLaunched;
            GameEvents.OnAllBallsConsumed -= ShowGameOver;
            GameEvents.OnChainChanged -= OnChainChanged;
            GameEvents.OnVictory -= ShowVictory;
            Time.timeScale = 1f;                    // 防残留：销毁时恢复时间流
        }

        void ShowGameOver()
        {
            CloseAllPanels();
            if (RunMap.I != null) RunMap.I.MarkCurrentFailed();      // 失败标记（地图红显，可原地重试）
            RunManager.Save();                                       // 失败状态也落盘（断点续玩）
            _over.SetActive(true);
        }

        void OnChainChanged(int level)
        {
            if (_chain == null) return;
            if (level >= 2)
            {
                _chain.text = $"CHAIN x{level}";
                _chain.color = Color.Lerp(NeonStyle.Yellow, NeonStyle.Red, Mathf.Clamp01((level - 2) / 8f));
                _chain.alpha = 1f;
                if (_chainPulse != null) StopCoroutine(_chainPulse);
                _chainPulse = StartCoroutine(PulseChain());
            }
            else
            {
                _chain.alpha = 0f;
            }
        }

        IEnumerator PulseChain()
        {
            float t = 0f;
            while (t < 0.18f)
            {
                t += Time.deltaTime;
                _chain.transform.localScale = Vector3.one * Mathf.Lerp(1.28f, 1f, t / 0.18f);
                yield return null;
            }
            _chain.transform.localScale = Vector3.one;
        }

        void BuildBallButton()
        {
            var btn = new GameObject("BallTypeBtn", typeof(Image), typeof(Button));
            btn.transform.SetParent(_safe, false);
            var img = btn.GetComponent<Image>();
            img.color = new Color(0.04f, 0.18f, 0.22f, 0.9f);
            Place(img.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(30f, 30f), new Vector2(430f, 86f));
            var bt = btn.GetComponent<Button>();
            bt.targetGraphic = img;
            bt.onClick.AddListener(OnBallButtonClicked);

            _ballBtnLabel = MakeText("BallTypeLabel", TextAlignmentOptions.Center, 38, NeonStyle.Cyan);
            _ballBtnLabel.transform.SetParent(btn.transform, false);
            Stretch(_ballBtnLabel.rectTransform);
            RefreshBallLabel();
        }

        void OnBallButtonClicked()
        {
            if (_balls == null) return;
            _balls.CycleBallType();
            RefreshBallLabel();
        }

        void RefreshBallLabel()
        {
            if (_ballBtnLabel != null && _balls != null && _balls.CurrentBallData != null)
                _ballBtnLabel.text = $"球种：{_balls.CurrentBallData.ballName}";
        }

        void Update()
        {
            // BottomBar 状态合并行：球 / 强化 / 水晶
            if (BallManager.I != null)
                _stock.text = $"球 ×{BallManager.I.BallsLeft}    强化 ×{(BuildState.I != null ? BuildState.I.TotalStacks : 0)}    水晶 ×{(RunMap.I != null ? RunMap.I.Crystals : 0)}";
            if (StageManager.I != null && StageManager.I.Current != null)
                _stageLabel.text = $"关卡 {StageManager.I.Index + 1}·{StageManager.I.Current.stageName}";
            // P9：球种标签帧同步（装备切换/关卡重置后 CurrentBallData 变化）
            if (_ballBtnLabel != null && _balls != null && _balls.CurrentBallData != null)
            {
                var t = $"球种：{_balls.CurrentBallData.ballName}";
                if (_ballBtnLabel.text != t) _ballBtnLabel.text = t;
            }
            // 防御式同步：胜利浮层跟随 StageManager 真值（绕过按钮的直接换关也能正确收起）
            if (_win != null && _win.activeSelf && StageManager.I != null && !StageManager.I.Victory)
                _win.SetActive(false);
        }

        void OnLaunched(Ball b)
        {
            if (_hint != null && _hint.alpha > 0f) StartCoroutine(FadeHint());
        }

        IEnumerator FadeHint()
        {
            float t = 0f;
            while (t < 0.6f)
            {
                t += Time.deltaTime;
                if (_hint == null) yield break;
                _hint.alpha = Mathf.Lerp(1f, 0f, t / 0.6f);
                yield return null;
            }
            _hint.alpha = 0f;
        }

        // ---------- 暂停面板（P4）：继续 / 重玩本关 / 音量设置 / 返回地图 ----------

        void BuildPausePanel()
        {
            _pausePanel = new GameObject("PausePanel");
            _pausePanel.transform.SetParent(_safe, false);
            var img = _pausePanel.AddComponent<Image>();
            img.color = new Color(0f, 0.02f, 0.04f, 0.82f);
            Stretch(img.rectTransform);

            var title = MakeText("PauseTitle", TextAlignmentOptions.Center, 92, NeonStyle.Cyan);
            title.text = "暂停";
            title.transform.SetParent(_pausePanel.transform, false);
            Place(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 430f), new Vector2(700f, 120f));

            float y = 240f;
            y = MakePauseButton("ResumeBtn", "继续战斗", y);
            y = MakePauseButton("RetryBtn", "重玩本关", y);
            y = MakePauseButton("VolBtn", "音量设置", y);
            y = MakePauseButton("MapBtn", "返回地图", y);

            _pausePanel.SetActive(false);
        }

        float MakePauseButton(string name, string text, float y)
        {
            var btn = new GameObject(name, typeof(Image), typeof(Button));
            btn.transform.SetParent(_pausePanel.transform, false);
            var img = btn.GetComponent<Image>();
            img.color = new Color(0.04f, 0.22f, 0.28f, 0.95f);
            Place(img.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, y), new Vector2(520f, 120f));
            var bt = btn.GetComponent<Button>();
            bt.targetGraphic = img;
            switch (name)
            {
                case "ResumeBtn": bt.onClick.AddListener(ClosePause); break;
                case "RetryBtn": bt.onClick.AddListener(() => { ClosePause(); RestartAll(); }); break;
                case "VolBtn": bt.onClick.AddListener(OpenSettings); break;
                case "MapBtn": bt.onClick.AddListener(BackToMap); break;
            }
            var label = MakeText(name + "Label", TextAlignmentOptions.Center, 46, NeonStyle.Cyan);
            label.text = text;
            label.transform.SetParent(btn.transform, false);
            Stretch(label.rectTransform);
            return y - 160f;
        }

        public void OpenPause()
        {
            if (OverlayActive || VictoryActive || RewardActive || (_runOver != null && _runOver.activeSelf)) return;
            Time.timeScale = 0f;                    // 冻结物理与协程（真暂停）
            if (_launcher != null) _launcher.ResetState();
            _pausePanel.SetActive(true);
        }

        public void ClosePause()
        {
            if (_pausePanel != null) _pausePanel.SetActive(false);
            if (!SettingsOpen) Time.timeScale = 1f;
        }

        /// <summary>返回地图：当前节点未通关 → 地图允许点当前节点重进（MapScreen.OpenAllowRetry）。</summary>
        void BackToMap()
        {
            ClosePause();
            if (StageManager.I != null && StageManager.I.BattleNode != null && RunMap.I != null && MapScreen.I != null)
            {
                StageManager.I.RestartStage();      // 清场上球，避免回地图后残留飞行球
                MapScreen.I.OpenAllowRetry();
            }
            else RestartAll();
            if (_launcher != null) _launcher.ResetState();
            _hint.alpha = 1f;
        }

        /// <summary>关闭全部弹层并恢复时间流（胜利/失败/换关前调用）。</summary>
        void CloseAllPanels()
        {
            if (_pausePanel != null) _pausePanel.SetActive(false);
            if (_settingsPanel != null) _settingsPanel.SetActive(false);
            Time.timeScale = 1f;
        }

        // ---------- 音量设置面板（P4）：4 滑条接 AudioManager（PlayerPrefs 持久化） ----------

        void BuildSettingsPanel()
        {
            _settingsPanel = new GameObject("SettingsPanel");
            _settingsPanel.transform.SetParent(_safe, false);
            var img = _settingsPanel.AddComponent<Image>();
            img.color = new Color(0f, 0.03f, 0.06f, 0.97f);   // 近不透明：防下层暂停面板文字叠影
            Stretch(img.rectTransform);

            var title = MakeText("VolTitle", TextAlignmentOptions.Center, 80, NeonStyle.Cyan);
            title.text = "音量设置";
            title.transform.SetParent(_settingsPanel.transform, false);
            Place(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 470f), new Vector2(700f, 110f));

            MakeVolumeRow("Master", "总音量", 330f, PlayerPrefs.GetFloat("GB_Audio_Master", 1f), AudioManager.SetMasterVolume);
            MakeVolumeRow("BGM", "音乐", 200f, PlayerPrefs.GetFloat("GB_Audio_BGM", 1f), AudioManager.SetBgmVolume);
            MakeVolumeRow("SFX", "音效", 70f, PlayerPrefs.GetFloat("GB_Audio_SFX", 1f), AudioManager.SetSfxVolume);
            MakeVolumeRow("UI", "界面", -60f, PlayerPrefs.GetFloat("GB_Audio_UI", 1f), AudioManager.SetUiVolume);

            var close = new GameObject("VolCloseBtn", typeof(Image), typeof(Button));
            close.transform.SetParent(_settingsPanel.transform, false);
            var cimg = close.GetComponent<Image>();
            cimg.color = new Color(0.06f, 0.14f, 0.18f, 0.95f);
            Place(cimg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -230f), new Vector2(420f, 110f));
            var cbt = close.GetComponent<Button>();
            cbt.targetGraphic = cimg;
            cbt.onClick.AddListener(CloseSettings);
            var clabel = MakeText("VolCloseLabel", TextAlignmentOptions.Center, 46, NeonStyle.TextDim);
            clabel.text = "关闭";
            clabel.transform.SetParent(close.transform, false);
            Stretch(clabel.rectTransform);

            _settingsPanel.SetActive(false);
        }

        void MakeVolumeRow(string name, string label, float y, float init,
            UnityEngine.Events.UnityAction<float> onSet)
        {
            var row = new GameObject("Vol" + name, typeof(RectTransform));
            row.transform.SetParent(_settingsPanel.transform, false);
            Place((RectTransform)row.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, y), new Vector2(820f, 100f));

            var txt = MakeText("Vol" + name + "Label", TextAlignmentOptions.MidlineLeft, 42, NeonStyle.Cyan);
            txt.text = label;
            txt.transform.SetParent(row.transform, false);
            Place(txt.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 36f), new Vector2(240f, 70f));

            // uGUI Slider 代码构建：BG / Fill / Handle
            var bgGo = new GameObject("BG", typeof(Image));
            bgGo.transform.SetParent(row.transform, false);
            var bgImg = bgGo.GetComponent<Image>();
            bgImg.color = new Color(0.06f, 0.14f, 0.18f, 1f);
            Place(bgImg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(140f, -14f), new Vector2(620f, 28f));

            var fillGo = new GameObject("Fill", typeof(Image));
            fillGo.transform.SetParent(bgGo.transform, false);
            var fillImg = fillGo.GetComponent<Image>();
            fillImg.color = NeonStyle.Cyan;
            var frt = fillImg.rectTransform;
            frt.anchorMin = Vector2.zero;
            frt.anchorMax = Vector2.one;
            frt.offsetMin = Vector2.zero;
            frt.offsetMax = Vector2.zero;

            var handleGo = new GameObject("Handle", typeof(Image));
            handleGo.transform.SetParent(bgGo.transform, false);
            var handleImg = handleGo.GetComponent<Image>();
            handleImg.color = Color.white;
            Place(handleImg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(22f, 44f));

            var slider = row.AddComponent<Slider>();
            slider.fillRect = frt;
            slider.handleRect = handleImg.rectTransform;
            slider.targetGraphic = handleImg;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = init;
            slider.onValueChanged.AddListener(onSet);
        }

        public void OpenSettings()
        {
            Time.timeScale = 0f;                    // 战斗中调音量也冻结（防误发射）
            if (_launcher != null) _launcher.ResetState();
            if (_settingsPanel != null) _settingsPanel.SetActive(true);
        }

        public void CloseSettings()
        {
            if (_settingsPanel != null) _settingsPanel.SetActive(false);
            if (!PauseOpen) Time.timeScale = 1f;    // 从暂停面板进入的：保持冻结
        }

        void BuildGameOver()
        {
            _over = new GameObject("GameOver");
            _over.transform.SetParent(_safe, false);
            var img = _over.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.75f);
            Stretch(img.rectTransform);

            var title = MakeText("Title", TextAlignmentOptions.Center, 100, Color.white);
            title.text = "球已耗尽";
            title.transform.SetParent(_over.transform, false);
            Place(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 120f), new Vector2(900f, 140f));

            var sub = MakeText("Sub", TextAlignmentOptions.Center, 44, NeonStyle.TextDim);
            sub.text = "规划路线 · 让球走得更远";
            sub.transform.SetParent(_over.transform, false);
            Place(sub.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -30f), new Vector2(900f, 80f));

            // 广告复活（琥珀主键）——看广告补球原地复活，场上状态保留
            var reviveBtn = new GameObject("ReviveBtn", typeof(Image), typeof(Button));
            reviveBtn.transform.SetParent(_over.transform, false);
            var rimg = reviveBtn.GetComponent<Image>();
            rimg.color = new Color(0.30f, 0.20f, 0.02f, 0.98f);
            Place(rimg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -210f), new Vector2(520f, 130f));
            var rbt = reviveBtn.GetComponent<Button>();
            rbt.targetGraphic = rimg;
            rbt.onClick.AddListener(ReviveByAd);
            var rlabel = MakeText("ReviveLabel", TextAlignmentOptions.Center, 48, NeonStyle.Amber);
            rlabel.text = "看广告复活  +" + _cfg.adReviveBalls + " 球";
            rlabel.transform.SetParent(reviveBtn.transform, false);
            Stretch(rlabel.rectTransform);

            var btn = new GameObject("RestartBtn", typeof(Image), typeof(Button));
            btn.transform.SetParent(_over.transform, false);
            var bimg = btn.GetComponent<Image>();
            bimg.color = new Color(0.04f, 0.18f, 0.22f, 0.95f);
            Place(bimg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -370f), new Vector2(400f, 120f));
            var bt = btn.GetComponent<Button>();
            bt.targetGraphic = bimg;
            bt.onClick.AddListener(RestartAll);

            var label = MakeText("BtnLabel", TextAlignmentOptions.Center, 52, NeonStyle.Cyan);
            label.text = "重新开始";
            label.transform.SetParent(btn.transform, false);
            Stretch(label.rectTransform);

            // P9：失败第二出路——返回大厅（存档保留，可继续挑战）
            var lobbyBtn = new GameObject("LobbyBtn", typeof(Image), typeof(Button));
            lobbyBtn.transform.SetParent(_over.transform, false);
            var limg = lobbyBtn.GetComponent<Image>();
            limg.color = new Color(0.06f, 0.12f, 0.16f, 0.9f);
            Place(limg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -500f), new Vector2(400f, 110f));
            var lbt = lobbyBtn.GetComponent<Button>();
            lbt.targetGraphic = limg;
            lbt.onClick.AddListener(GoLobby);
            var llabel = MakeText("LobbyLabel", TextAlignmentOptions.Center, 44, NeonStyle.TextDim);
            llabel.text = "返回大厅";
            llabel.transform.SetParent(lobbyBtn.transform, false);
            Stretch(llabel.rectTransform);

            _over.SetActive(false);
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

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

        static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        public void RestartAll()
        {
            CloseAllPanels();
            if (RunMap.I != null) RunMap.I.UnmarkCurrentFailed();      // 重玩=清除失败标记
            _over.SetActive(false);
            if (_win != null) _win.SetActive(false);
            if (StageManager.I != null) StageManager.I.RestartStage();    // 完整重建关卡
            else if (BallManager.I != null) BallManager.I.ResetAll();
            _launcher.ResetState();
            _hint.alpha = 1f;
        }

        /// <summary>广告复活：看广告补球原地继续（场上残留状态保留，区别于"重新开始"整关重建）。</summary>
        void ReviveByAd()
        {
            PlatformManager.Ad.ShowRewarded(ok =>
            {
                if (!ok) return;
                if (RunMap.I != null) RunMap.I.UnmarkCurrentFailed();   // 复活=撤销失败标记
                RunManager.Save();
                CloseAllPanels();
                _over.SetActive(false);
                if (_win != null) _win.SetActive(false);
                if (BallManager.I != null) BallManager.I.ReviveStock(_cfg.adReviveBalls);
                _launcher.ResetState();
                _hint.alpha = 1f;
                if (FloatingText.I != null)
                    FloatingText.I.Spawn(new Vector3(0f, -5.2f, 0f), "复活 +" + _cfg.adReviveBalls + " 球", NeonStyle.Amber, 0.7f);
                AudioManager.PlayReward();
            });
        }

        void ShowVictory()
        {
            CloseAllPanels();
            _over.SetActive(false);          // 若同时有"球已耗尽"浮层则收起
            if (_winTitle != null)
            {
                bool boss = StageManager.I != null && StageManager.I.BattleNode != null
                         && StageManager.I.BattleNode.type == NodeType.Boss;
                _winTitle.text = boss ? "BOSS 击破！" : "核心击破！";
            }
            _win.SetActive(true);
            UpdateVictoryReport();
        }

        /// <summary>刷新胜利战报（P6：最大连锁/最大伤害/命中/奖励预览；公开供测试）。</summary>
        public void UpdateVictoryReport()
        {
            if (_winChain == null) return;
            _winChain.text = "最大连锁 ×" + RunStats.BattleMaxChain;
            _winHit.text = "最大单发伤害 " + Mathf.RoundToInt(RunStats.BattleMaxHit);
            _winCount.text = "命中 " + RunStats.BattleHits + " 次";
            var node = StageManager.I != null ? StageManager.I.BattleNode : null;
            int stardust = MetaProgress.Loaded && node != null ? MetaProgress.PreviewNodeReward(node.type) : 0;
            _winReward.text = "获得水晶 +2 · 星尘 +" + stardust;
        }

        void GoNextStage()          // 胜利主按钮：先选强化，再回地图
        {
            _rewardPickCount = 0;
            _win.SetActive(false);
            _over.SetActive(false);
            OpenRewardPanel();
        }

        void EnterNextStage()
        {
            CloseAllPanels();
            if (_reward != null) _reward.SetActive(false);
            _win.SetActive(false);
            _over.SetActive(false);

            // 地图模式：结算节点 → 回地图（Boss 胜利 → Run 通关浮层）
            if (StageManager.I != null && StageManager.I.BattleNode != null && RunMap.I != null)
            {
                RunMap.I.OnNodeCleared();
                if (RunMap.I.RunFinished)
                {
                    RunManager.FinishRun();                          // 通关：删除中断存档
                    if (_runOver != null) _runOver.SetActive(true);
                }
                else
                {
                    RunManager.Save();                               // 节点进度落盘
                    if (MapScreen.I != null) MapScreen.I.Open();
                }
            }
            else if (StageManager.I != null)
            {
                StageManager.I.NextStage();           // 非地图回退路径
            }
            _launcher.ResetState();
            _hint.alpha = 1f;
        }

        // ---------- 三选一奖励面板 ----------

        void BuildRewardPanel()
        {
            _reward = new GameObject("RewardPanel");
            _reward.transform.SetParent(_safe, false);
            var img = _reward.AddComponent<Image>();
            img.color = new Color(0f, 0.02f, 0.04f, 0.86f);
            Stretch(img.rectTransform);

            var title = MakeText("RewardTitle", TextAlignmentOptions.Center, 84, NeonStyle.Cyan);
            title.text = "选择一项强化";
            title.transform.SetParent(_reward.transform, false);
            Place(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 480f), new Vector2(900f, 120f));

            var skip = new GameObject("SkipRewardBtn", typeof(Image), typeof(Button));
            skip.transform.SetParent(_reward.transform, false);
            var simg = skip.GetComponent<Image>();
            simg.color = new Color(0.06f, 0.12f, 0.16f, 0.9f);
            Place(simg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -560f), new Vector2(420f, 100f));
            var sbt = skip.GetComponent<Button>();
            sbt.targetGraphic = simg;
            sbt.onClick.AddListener(EnterNextStage);
            var slabel = MakeText("SkipLabel", TextAlignmentOptions.Center, 40, NeonStyle.TextDim);
            slabel.text = "跳过，直接下一关";
            slabel.transform.SetParent(skip.transform, false);
            Stretch(slabel.rectTransform);

            _reward.SetActive(false);
        }

        void OpenRewardPanel()
        {
            if (BuildState.I == null)
            {
                EnterNextStage();
                return;
            }
            var picks = BuildState.I.Roll(3);
            if (picks.Count == 0)
            {
                EnterNextStage();      // 全部满层：跳过奖励
                return;
            }

            foreach (var c in _cards) if (c != null) Destroy(c);
            _cards.Clear();

            float y = 240f;
            foreach (var def in picks)
            {
                var card = new GameObject("Card", typeof(Image), typeof(Button));
                card.transform.SetParent(_reward.transform, false);
                var cimg = card.GetComponent<Image>();
                cimg.color = new Color(0.04f, 0.22f, 0.28f, 0.95f);
                Place(cimg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, y), new Vector2(760f, 210f));
                var cbt = card.GetComponent<Button>();
                cbt.targetGraphic = cimg;
                UpgradeDef captured = def;
                cbt.onClick.AddListener(() => PickReward(captured));

                var t = MakeText("CardTitle", TextAlignmentOptions.MidlineLeft, 56, NeonStyle.Cyan);
                t.text = def.title;
                t.transform.SetParent(card.transform, false);
                Place(t.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(60f, 36f), new Vector2(640f, 76f));

                var d = MakeText("CardDesc", TextAlignmentOptions.MidlineLeft, 36, NeonStyle.TextDim);
                d.text = def.desc;
                d.transform.SetParent(card.transform, false);
                Place(d.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(60f, -52f), new Vector2(640f, 70f));

                _cards.Add(card);
                y -= 270f;
            }
            _reward.SetActive(true);
        }

        void PickReward(UpgradeDef def)
        {
            if (BuildState.I != null) BuildState.I.Apply(def);
            AudioManager.PlayReward();                  // 构筑·奖励确认音
            _rewardPickCount++;
            // 精英战斗双奖励：选完第一张再选一张
            bool elite = StageManager.I != null && StageManager.I.BattleNode != null
                       && StageManager.I.BattleNode.type == NodeType.Elite;
            if (elite && _rewardPickCount < 2)
            {
                OpenRewardPanel();
                return;
            }
            _rewardPickCount = 0;
            EnterNextStage();
        }

        // ---------- Run 通关浮层（Phase 6） ----------

        void BuildRunOverOverlay()
        {
            _runOver = new GameObject("RunOver");
            _runOver.transform.SetParent(_safe, false);
            var img = _runOver.AddComponent<Image>();
            img.color = new Color(0f, 0.02f, 0.05f, 0.88f);
            Stretch(img.rectTransform);

            var title = MakeText("RunTitle", TextAlignmentOptions.Center, 110, NeonStyle.Cyan);
            title.text = "Run 通关！";
            title.transform.SetParent(_runOver.transform, false);
            Place(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 220f), new Vector2(900f, 160f));

            var sub = MakeText("RunSub", TextAlignmentOptions.Center, 44, NeonStyle.TextDim);
            sub.text = "几何弹球已被你征服 · 再来一 Run？";
            sub.transform.SetParent(_runOver.transform, false);
            Place(sub.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 40f), new Vector2(900f, 90f));

            var btn = new GameObject("NewRunBtn", typeof(Image), typeof(Button));
            btn.transform.SetParent(_runOver.transform, false);
            var bimg = btn.GetComponent<Image>();
            bimg.color = new Color(0.04f, 0.25f, 0.30f, 0.95f);
            Place(bimg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -190f), new Vector2(440f, 130f));
            var bt = btn.GetComponent<Button>();
            bt.targetGraphic = bimg;
            bt.onClick.AddListener(GoLobby);

            var label = MakeText("NewRunLabel", TextAlignmentOptions.Center, 54, NeonStyle.Cyan);
            label.text = "返回大厅";
            label.transform.SetParent(btn.transform, false);
            Stretch(label.rectTransform);

            _runOver.SetActive(false);
        }

        /// <summary>回大厅（P9）：清场 + 打开主菜单。Run 已通关则清中断档。</summary>
        public void GoLobby()
        {
            CloseAllPanels();
            _over.SetActive(false);
            if (_win != null) _win.SetActive(false);
            if (_runOver != null) _runOver.SetActive(false);
            if (MapScreen.I != null) MapScreen.I.Close();
            if (RunMap.I != null && RunMap.I.RunFinished) RunManager.FinishRun();
            if (StageManager.I != null) StageManager.I.ClearStage();
            if (MainMenuScreen.I != null) MainMenuScreen.I.OpenRoot();
            _launcher.ResetState();
        }

        /// <summary>新 Run：重置构筑与地图，回到起点战斗。</summary>
        public void StartNewRun()
        {
            CloseAllPanels();
            if (_runOver != null) _runOver.SetActive(false);
            RunManager.FinishRun();                                  // 清旧中断存档
            RunStats.ResetRun();                                    // P6：新 Run 清空战报
            if (BuildState.I != null) BuildState.I.Reset();
            if (RunMap.I != null) RunMap.I.Generate();
            if (MapScreen.I != null) MapScreen.I.Close();
            if (StageManager.I != null && RunMap.I != null)
                StageManager.I.EnterBattle(RunMap.I.Current);
            _launcher.ResetState();
            _hint.alpha = 1f;
        }

        void BuildVictoryOverlay()
        {
            _win = new GameObject("Victory");
            _win.transform.SetParent(_safe, false);
            var img = _win.AddComponent<Image>();
            img.color = new Color(0f, 0.03f, 0.06f, 0.78f);
            Stretch(img.rectTransform);

            var title = MakeText("WinTitle", TextAlignmentOptions.Center, 100, NeonStyle.Cyan);
            title.text = "核心击破！";
            title.transform.SetParent(_win.transform, false);
            Place(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 300f), new Vector2(900f, 140f));
            _winTitle = title;

            // ---- P6 战报行 ----
            _winChain = MakeText("WinChain", TextAlignmentOptions.Center, 46, NeonStyle.Yellow);
            _winChain.text = "最大连锁 ×0";
            _winChain.transform.SetParent(_win.transform, false);
            Place(_winChain.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 150f), new Vector2(900f, 70f));

            _winHit = MakeText("WinHit", TextAlignmentOptions.Center, 46, NeonStyle.White);
            _winHit.text = "最大单发伤害 0";
            _winHit.transform.SetParent(_win.transform, false);
            Place(_winHit.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 78f), new Vector2(900f, 70f));

            _winCount = MakeText("WinCount", TextAlignmentOptions.Center, 42, NeonStyle.TextDim);
            _winCount.text = "命中 0 次";
            _winCount.transform.SetParent(_win.transform, false);
            Place(_winCount.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 8f), new Vector2(900f, 64f));

            _winReward = MakeText("WinReward", TextAlignmentOptions.Center, 44, NeonStyle.Amber);
            _winReward.text = "获得水晶 +2 · 星尘 +3";
            _winReward.transform.SetParent(_win.transform, false);
            Place(_winReward.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -70f), new Vector2(900f, 70f));

            var next = new GameObject("NextStageBtn", typeof(Image), typeof(Button));
            next.transform.SetParent(_win.transform, false);
            var nimg = next.GetComponent<Image>();
            nimg.color = new Color(0.04f, 0.25f, 0.30f, 0.95f);
            Place(nimg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -215f), new Vector2(420f, 120f));
            var nbt = next.GetComponent<Button>();
            nbt.targetGraphic = nimg;
            nbt.onClick.AddListener(GoNextStage);

            var nlabel = MakeText("NextLabel", TextAlignmentOptions.Center, 52, NeonStyle.Cyan);
            nlabel.text = "选择强化";
            nlabel.transform.SetParent(next.transform, false);
            Stretch(nlabel.rectTransform);

            var replay = new GameObject("ReplayStageBtn", typeof(Image), typeof(Button));
            replay.transform.SetParent(_win.transform, false);
            var rimg = replay.GetComponent<Image>();
            rimg.color = new Color(0.06f, 0.12f, 0.16f, 0.9f);
            Place(rimg.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -370f), new Vector2(420f, 110f));
            var rbt = replay.GetComponent<Button>();
            rbt.targetGraphic = rimg;
            rbt.onClick.AddListener(RestartAll);

            var rlabel = MakeText("ReplayLabel", TextAlignmentOptions.Center, 44, NeonStyle.TextDim);
            rlabel.text = "重玩本关";
            rlabel.transform.SetParent(replay.transform, false);
            Stretch(rlabel.rectTransform);

            _win.SetActive(false);
        }
    }
}
