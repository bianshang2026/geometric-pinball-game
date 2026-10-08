using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 发射状态机（架构文档 §12 LaunchSystem 层）：Idle -> Aiming -> Flying -> (回收后) Idle / GameOver。
    /// P3 重构：输入经 InputManager 抽象（鼠标/触摸统一，Gameplay 不直读 Input），
    /// 瞄准计算经 AimSystem 纯逻辑层；状态机与手感与旧版逐参数一致（决策点⑦基线锁定）。
    /// </summary>
    public class BallLauncher : MonoBehaviour
    {
        enum State { Idle, Aiming, Flying, GameOver }

        State _state = State.Idle;
        GameConfig _cfg;
        TrajectoryPredictor _predictor;
        float _minElevRad;
        Vector2 _pressStart;

        /// <summary>结构化测试读数（T41）。</summary>
        public string DebugState => _state.ToString();

        public void Init(GameConfig cfg, Camera cam, TrajectoryPredictor predictor)
        {
            _cfg = cfg;
            _predictor = predictor;               // cam 的指针换算已移交 InputManager（签名保留兼容 Bootstrap）
            _minElevRad = cfg.minAimElevationDeg * Mathf.Deg2Rad;
            GameEvents.OnAllBallsConsumed += OnGameOver;
        }

        void OnDestroy() => GameEvents.OnAllBallsConsumed -= OnGameOver;

        void OnGameOver()
        {
            _state = State.GameOver;
            _predictor.Hide();
        }

        public void ResetState()
        {
            _state = State.Idle;
            _predictor.Hide();
        }

        void Update()
        {
            switch (_state)
            {
                case State.Idle:
                    if (BallManager.I != null && BallManager.I.IdleBall != null && InputManager.PressedDown())
                    {
                        _pressStart = InputManager.PointerWorld();
                        _state = State.Aiming;
                    }
                    break;

                case State.Aiming:
                    if (!InputManager.Pressed()) Release();
                    else UpdateAim();
                    break;

                case State.Flying:
                    if (BallManager.I.IdleBall != null) _state = State.Idle;
                    break;
            }
        }

        void UpdateAim()
        {
            var ball = BallManager.I.IdleBall;
            if (ball == null) { _state = State.Flying; return; }
            Vector2 origin = ball.transform.position;
            if (AimSystem.TryComputeAim(_pressStart, InputManager.PointerWorld(), origin,
                    _cfg.cancelDragDist, _minElevRad, out Vector2 dir))
                _predictor.Show(origin, dir);
            else
                _predictor.Hide();
        }

        void Release()
        {
            _predictor.Hide();
            var ball = BallManager.I.IdleBall;
            if (ball != null && AimSystem.TryComputeAim(_pressStart, InputManager.PointerWorld(),
                    ball.transform.position, _cfg.cancelDragDist, _minElevRad, out Vector2 dir))
            {
                BallManager.I.LaunchIdle(dir);
                _state = State.Flying;
            }
            else
            {
                _state = State.Idle;                            // 视为取消（点击未拖动）
            }
        }
    }
}
