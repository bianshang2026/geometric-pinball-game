using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 游戏入口：场景里只需要这一个组件，其余（相机/背景/场地/球/UI/特效）
    /// 全部在 Awake 中以代码构建 —— 满足"零外部美术、零预制"的纯代码要求。
    /// </summary>
    public class GameBootstrap : MonoBehaviour
    {
        void Awake()
        {
            // 无重力弹球：球靠反弹与回收区流转
            Physics2D.gravity = Vector2.zero;
            Application.targetFrameRate = 60;

            // 局外进度必须最早加载（球种过滤/开局加成均依赖解锁状态）
            PlatformManager.Init();                               // P10：平台服务注册（存储/广告/分享）
            MetaProgress.Load();                                  // Profile 加载（含旧 File 档迁移）
            RunStats.Hook();                                        // P6：战报统计订阅（域重载后重挂）

            var cfg = Resources.Load<GameConfig>("GameConfig");

            // 相机（竖屏 9:16，正交）
            var cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }
            cam.orthographic = true;
            cam.orthographicSize = cfg.fieldHeight * 0.5f;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = NeonStyle.Background;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 50f;
            foreach (var l in FindObjectsOfType<Light>())
                Destroy(l.gameObject);

            // 模块根（ui 持有 GameObject：Hud 加 Canvas 时 Transform 会被替换成 RectTransform）
            var bg = NewRoot("=== Background ===");
            var field = NewRoot("=== Field ===");
            var fx = NewRoot("=== Effects ===");
            var balls = NewRoot("=== Balls ===");
            var ui = NewRoot("=== UI ===").gameObject;

            // 背景
            var grid = bg.AddComponent<BackgroundGrid>();
            grid.Build(cfg);

            // 场地 + Phase 2 演示关卡（六种几何机关）
            var battleField = field.AddComponent<BattleField>();
            battleField.Build(cfg);

            // 连锁系统：必须先于所有几何实体订阅，保证同一帧"先记连锁后结算伤害"
            var chain = fx.AddComponent<ChainSystem>();
            chain.Init(cfg);

            // 构筑系统（必须在 StageManager 之前：StageBuilder 读 ExtraStock）
            var upgrades = new[]
            {
                Resources.Load<UpgradeDef>("Upgrade_SpeedUp"),
                Resources.Load<UpgradeDef>("Upgrade_Pierce"),
                Resources.Load<UpgradeDef>("Upgrade_AutoSplit"),
                Resources.Load<UpgradeDef>("Upgrade_BlastOnDeath"),
                Resources.Load<UpgradeDef>("Upgrade_Aftershock"),
                Resources.Load<UpgradeDef>("Upgrade_StockPlus"),
                Resources.Load<UpgradeDef>("Upgrade_DamagePlus"),
                Resources.Load<UpgradeDef>("Upgrade_BlastRadiusPlus"),
                Resources.Load<UpgradeDef>("Upgrade_ChainDamagePlus"),
            };
            var build = fx.AddComponent<BuildState>();
            build.Init(upgrades);

            // 特效 + 伤害数字 + 爆炸系统 + 音频
            var fxm = fx.AddComponent<EffectManager>();
            fxm.Init(cam);
            var ftext = fx.AddComponent<FloatingText>();
            ftext.Init();
            var explosions = fx.AddComponent<ExplosionSystem>();
            explosions.Init(cfg);
            fx.AddComponent<FrostSystem>();                      // 冻结系统（冻结球：到期恢复视觉）
            var timeStop = fx.AddComponent<TimeStopCue>();
            timeStop.Init(cfg);                                  // 时停演出（时间球）：定格+暗幕+魔法阵
            var audio = fx.AddComponent<AudioManager>();
            audio.Init(cfg);                                   // cfg：球速映射用 min/maxSpeed

            // 球库存（基础球种 + 引力球/穿透球（局外解锁）；HUD 左下按钮切换）
            var ballKinds = new[]
            {
                Resources.Load<BallData>("NormalBall"),
                Resources.Load<BallData>("SplitBall"),
                Resources.Load<BallData>("ExplosiveBall"),
                Resources.Load<BallData>("PrismBall"),
                Resources.Load<BallData>("GravityBall"),
                Resources.Load<BallData>("PierceBall"),
                Resources.Load<BallData>("CurveBall"),
                Resources.Load<BallData>("MirrorBall"),
                Resources.Load<BallData>("PhaseBall"),
                Resources.Load<BallData>("TimeBall"),
                Resources.Load<BallData>("FrostBall"),
                Resources.Load<BallData>("ReboundBall"),
            };
            var ballMgr = balls.AddComponent<BallManager>();
            ballMgr.Init(cfg, ballKinds, battleField.Bouncy);

            // 关卡系统（P7：30 图库优先；旧 4 图作为库缺失时的回退）
            StageLibrary.Init();                                // Resources 加载 Stage01..30
            var stages = new[]
            {
                Resources.Load<StageData>("Stage1"),
                Resources.Load<StageData>("Stage2"),
                Resources.Load<StageData>("Stage3"),
                Resources.Load<StageData>("Stage4"),
            };
            var stageMgr = field.AddComponent<StageManager>();
            stageMgr.Init(cfg, battleField.Bouncy, stages);

            // 输入抽象（P3）：鼠标/触摸统一，Gameplay 层禁止直读 Input
            var input = fx.AddComponent<InputManager>();
            input.Init(cam);

            // 瞄准 / 预测
            var predictor = fx.AddComponent<TrajectoryPredictor>();
            predictor.Init(cfg, ballMgr.CurrentBallData.radius);
            var launcher = balls.AddComponent<BallLauncher>();
            launcher.Init(cfg, cam, predictor);

            // HUD
            var hud = ui.AddComponent<Hud>();
            hud.Init(cfg, ballMgr, launcher);

            // 开发者控制台（F1 开关）
            var dev = ui.AddComponent<DevConsole>();
            dev.Init();

            // Roguelite 地图 v2（P5）：8 层分支 + 断点续玩
            var runMap = ui.AddComponent<RunMap>();
            runMap.Init();
            var mapScreen = ui.AddComponent<MapScreen>();
            mapScreen.Init();

            // 局外大厅（P9）：启动进大厅——开始挑战=新 Run；有中断存档显示"继续挑战"
            var mainMenu = ui.AddComponent<MainMenuScreen>();
            mainMenu.Init();
            ballMgr.RestoreLoadout();                     // 恢复上次装备的球种
            mainMenu.OpenRoot();
        }

        static Transform NewRoot(string name)
        {
            var go = new GameObject(name);
            return go.transform;
        }
    }
}
