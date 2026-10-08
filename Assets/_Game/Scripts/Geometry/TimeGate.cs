using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// 时间门（文档十三）：周期性开合的几何门。开=球可通过；关=细条碰撞体弹回。
    /// 玩家必须计算球到达时门的状态（相位由 variant 档位错开，可组合出交替门）。
    /// </summary>
    public class TimeGate : GeometryEntity
    {
        float _phaseOffset;
        float _clock;                              // 累积时钟（受时间球减速/冻结球停止影响）
        float _forcedOpenUntil;                    // 能量节点强制开门期（优先于周期开合）
        bool _isOpen = true;
        BoxCollider2D _col;
        SpriteRenderer _line;
        SpriteRenderer _capA;
        SpriteRenderer _capB;

        public bool IsOpen => _isOpen;

        /// <summary>能量节点联动：强制开门 duration 秒。</summary>
        public void ForceOpen(float duration) => _forcedOpenUntil = Time.time + duration;

        public TimeGate Build(Vector2 pos, bool vertical, int phaseStep, GameConfig cfg)
        {
            Setup(cfg, NeonStyle.White);
            name = $"TimeGate({pos.x:0.##},{pos.y:0.##})";
            transform.position = new Vector3(pos.x, pos.y, 0f);
            indestructible = true;
            punchOnHit = false;
            _phaseOffset = phaseStep * 0.6f;

            float len = vertical ? 2.2f : 1.4f;
            _col = gameObject.AddComponent<BoxCollider2D>();
            _col.size = vertical ? new Vector2(0.14f, len) : new Vector2(len, 0.14f);
            _col.sharedMaterial = null;                 // 关门反弹由弹性材质给足：用弹力材质
            _col.isTrigger = false;
            // 弹性：直接给默认（球自身材质 bounce=1，平均已足够弹性）
            OwnCollider = _col;

            // 视觉：门线（竖/横）+ 两端方块端点
            _line = NeonStyle.MakeSprite(transform, "Line", ProcSprites.SquareFill(), NeonStyle.White, 12, false);
            _line.transform.localScale = vertical ? new Vector3(0.07f, len, 1f) : new Vector3(len, 0.07f, 1f);

            _capA = NeonStyle.MakeSprite(transform, "CapA", ProcSprites.SquareOutline(), NeonStyle.Cyan, 11, false);
            _capB = NeonStyle.MakeSprite(transform, "CapB", ProcSprites.SquareOutline(), NeonStyle.Cyan, 11, false);
            _capA.transform.localScale = _capB.transform.localScale = Vector3.one * 0.22f;
            if (vertical)
            {
                _capA.transform.localPosition = new Vector3(0f, len * 0.5f, 0f);
                _capB.transform.localPosition = new Vector3(0f, -len * 0.5f, 0f);
            }
            else
            {
                _capA.transform.localPosition = new Vector3(len * 0.5f, 0f, 0f);
                _capB.transform.localPosition = new Vector3(-len * 0.5f, 0f, 0f);
            }
            return this;
        }

        void Update()
        {
            if (!Frozen) _clock += Time.deltaTime * MechanismTime.Scale;   // 冻结停摆 / 时间球减速
            float cycle = Mathf.Repeat(_clock + _phaseOffset, _cfg.timeGatePeriod) / _cfg.timeGatePeriod;
            bool open = Time.time < _forcedOpenUntil || cycle < _cfg.timeGateOpenRatio;   // 能量节点强制开优先
            if (open != _isOpen)
            {
                _isOpen = open;
                _col.enabled = !open;
                if (EffectManager.I != null && EffectManager.I != null)
                    EffectManager.I.Flash(transform.position, NeonStyle.White, 1.0f, 0.12f);
            }
            // 视觉状态：开=暗淡虚化；关=亮线
            Color c = open
                ? new Color(0.6f, 0.8f, 0.9f, 0.15f)
                : new Color(NeonStyle.White.r, NeonStyle.White.g, NeonStyle.White.b, 0.9f);
            if (_line != null) _line.color = c;
        }
    }
}
