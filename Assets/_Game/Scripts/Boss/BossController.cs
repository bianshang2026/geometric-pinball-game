using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>
    /// Boss 行为控制器（P8，架构 §15）：挂在 Boss 核心上，按 BossData.phases 驱动。
    /// 组合现有系统——CoreBlock 护盾/弱点弧 + GravityField 引力 + 生成器（方块/炸弹屏障）。
    /// 阶段推进：核心 HP 比例跌破当前段 hpTo → 切换下一阶段（重建环/弧/场/生成器）。
    /// P11 池化：护盾环容器/引力场 Init 一次性常驻（阶段切换只做池取放+开关），
    /// 护盾块/生成物一律 Pools.Block/Pools.Bomb 取放——战斗中零 Instantiate/Destroy。
    /// </summary>
    public class BossController : MonoBehaviour
    {
        CoreBlock _core;
        BossData _data;
        GameConfig _cfg;
        PhysicsMaterial2D _bouncy;
        System.Func<GeometryBlock> _newBlock;
        System.Func<BombBlock> _newBomb;

        int _phaseIndex = -1;
        float _maxHp;
        float _spawnTimer;
        readonly List<GeometryEntity> _spawned = new List<GeometryEntity>();

        // 本阶段活动部件（切阶段清理重建；P11：容器常驻，只做池取放/开关）
        Transform _shieldRoot;
        readonly List<GeometryEntity> _shieldBlocks = new List<GeometryEntity>();
        GravityField _gravity;
        float _arcSpeed, _critMult, _armorMult;
        bool _arcOn;
        float _ringRotSpeed;

        /// <summary>当前阶段索引（测试 T46 用；-1=未激活）。</summary>
        public int PhaseIndex => _phaseIndex;
        public int ShieldCount
        {
            get
            {
                int n = 0;
                foreach (var s in _shieldBlocks)
                    if (s != null && s.Alive) n++;
                return n;
            }
        }
        public int SpawnedCount => _spawned.Count;
        public bool GravityOn => _gravity != null && _gravity.gameObject.activeSelf;

        public void Init(CoreBlock core, BossData data, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            _core = core;
            _data = data;
            _cfg = cfg;
            _bouncy = bouncy;
            _newBlock = NewBlockInstance;
            _newBomb = NewBombInstance;
            _maxHp = core.hp;
            core.WeakArcEnabledByBoss = data != null && data.phases != null && data.phases.Length > 0;

            // P11：部件容器构建期一次性建好（挂核心下，随核心销毁），阶段切换零创建
            if (UsesRing())
            {
                var go = new GameObject("BossShieldRing");
                go.transform.SetParent(_core.transform, false);
                _shieldRoot = go.transform;
                _shieldRoot.gameObject.SetActive(false);
            }
            if (UsesGravity())
            {
                var gGo = new GameObject("BossGravity");
                gGo.transform.SetParent(_core.transform, false);
                gGo.transform.position = _core.transform.position;
                _gravity = gGo.AddComponent<GravityField>();
                _gravity.Build((Vector2)_core.transform.position, 0, _cfg);
                _gravity.gameObject.SetActive(false);
            }
            AdvancePhase();
        }

        bool UsesRing()
        {
            if (_data == null || _data.phases == null) return false;
            foreach (var p in _data.phases)
                if (p.shieldRingCount > 0f) return true;
            return false;
        }

        bool UsesGravity()
        {
            if (_data == null || _data.phases == null) return false;
            foreach (var p in _data.phases)
                if (p.gravityStrength > 0f) return true;
            return false;
        }

        GeometryBlock NewBlockInstance()
        {
            var go = new GameObject("BossBlock");
            go.transform.SetParent(Pools.Root, false);
            return go.AddComponent<GeometryBlock>();
        }

        BombBlock NewBombInstance()
        {
            var go = new GameObject("BossBomb");
            go.transform.SetParent(Pools.Root, false);
            return go.AddComponent<BombBlock>();
        }

        void OnDestroy()
        {
            // P11：部件回池（核心 GO 随关卡销毁；已回收实例挂在池根不受影响）
            foreach (var s in _shieldBlocks)
                if (s != null && s.Alive && s is GeometryBlock sb) Pools.Block.Release(sb);
            _shieldBlocks.Clear();
            foreach (var s in _spawned)
            {
                if (s == null || !s.Alive) continue;
                if (s is BombBlock b) Pools.Bomb.Release(b);
                else if (s is GeometryBlock g) Pools.Block.Release(g);
            }
            _spawned.Clear();
        }

        void Update()
        {
            if (_core == null || !_core.Alive || _data == null) return;
            if (_core.Frozen) return;                         // 冻结球命中核心：Boss 机制停摆（环/弧/生成器）

            // 阶段推进：HP 跌破当前段下限
            float ratio = _core.hp / _maxHp;
            var ph = _data.phases[_phaseIndex];
            if (ratio <= ph.hpTo && _phaseIndex < _data.phases.Length - 1)
            {
                AdvancePhase();
                return;
            }

            // 护盾环旋转（时间球：机关减速）
            if (_shieldRoot != null && _shieldRoot.gameObject.activeSelf)
                _shieldRoot.Rotate(0f, 0f, _ringRotSpeed * MechanismTime.Scale * Time.deltaTime, Space.Self);

            // 生成器（B3 孕核屏障；时间球：减速）
            if (ph.spawnerPeriod > 0f)
            {
                _spawnTimer += Time.deltaTime * MechanismTime.Scale;
                if (_spawnTimer >= ph.spawnerPeriod)
                {
                    _spawnTimer = 0f;
                    TrySpawnWave(ph);
                }
                _spawned.RemoveAll(s => s == null || !s.Alive);   // P11：回池实例以 !Alive 语义清除
            }
        }

        void AdvancePhase()
        {
            _phaseIndex++;
            if (_data == null || _data.phases == null || _phaseIndex >= _data.phases.Length) return;
            var ph = _data.phases[_phaseIndex];
            _spawnTimer = 0f;

            ClearPhaseParts();

            // 护盾环（P11：容器常驻，块走池）
            if (ph.shieldRingCount > 0f)
            {
                _shieldRoot.gameObject.SetActive(true);
                int count = Mathf.RoundToInt(ph.shieldRingCount);
                const float radius = 1.75f;
                for (int i = 0; i < count; i++)
                {
                    float a = i * (360f / count) + 18f;
                    Vector2 dirS = Quaternion.Euler(0f, 0f, a) * Vector3.up;
                    Vector2 p = (Vector2)_core.transform.position + dirS * radius;
                    var shield = Pools.Block.Take(_newBlock);
                    shield.gameObject.SetActive(true);
                    shield.Build(p, 2f, _cfg, _bouncy);
                    shield.name = "BossShieldBlock";
                    shield.transform.SetParent(_shieldRoot, true);      // worldPositionStays：核心 1.5 缩放下保持世界尺寸
                    _core.AttachShield(shield);
                    _shieldBlocks.Add(shield);
                }
                _ringRotSpeed = ph.shieldRingRotSpeed;
            }

            // 弱点弧（参数注入 CoreBlock）
            _arcOn = ph.weakArc;
            _arcSpeed = ph.weakArcSpeedDeg;
            _critMult = ph.weakCritMultiplier;
            _armorMult = ph.weakArmorMultiplier;
            _core.SetWeakArc(_arcOn, _arcSpeed, _critMult, _armorMult);

            // 引力场（黑洞；P11：常驻部件按阶段开关）
            if (ph.gravityStrength > 0f)
            {
                _gravity.gameObject.SetActive(true);
                _gravity.transform.position = _core.transform.position;
                _gravity.SetStrength(ph.gravityStrength, ph.gravityRadius);
            }

            if (EffectManager.I != null && _phaseIndex > 0)
            {
                EffectManager.I.Flash(_core.transform.position, NeonStyle.Red, 2.6f, 0.25f);
                if (FloatingText.I != null)
                    FloatingText.I.Spawn(_core.transform.position, "阶段 " + (_phaseIndex + 1), NeonStyle.Red, 0.6f);
            }
        }

        void ClearPhaseParts()
        {
            // P11：护盾块回池、容器停用（不再 Destroy）
            foreach (var s in _shieldBlocks)
                if (s != null && s.Alive && s is GeometryBlock sb) Pools.Block.Release(sb);
            _shieldBlocks.Clear();
            if (_shieldRoot != null) _shieldRoot.gameObject.SetActive(false);
            if (_gravity != null) _gravity.gameObject.SetActive(false);
        }

        /// <summary>B3 波：2 方块 + 1 炸弹，环位随机角度生成（场上限约束；P11 池取放）。</summary>
        void TrySpawnWave(BossPhase ph)
        {
            _spawned.RemoveAll(s => s == null || !s.Alive);
            if (_spawned.Count >= ph.spawnerMax) return;
            Vector2 c = _core.transform.position;
            for (int i = 0; i < 3 && _spawned.Count < ph.spawnerMax; i++)
            {
                float a = Random.Range(0f, 360f);
                Vector2 dir = Quaternion.Euler(0f, 0f, a) * Vector3.up;
                Vector2 p = c + dir * Random.Range(2.2f, 3.2f);
                bool isBomb = i == 2;
                GeometryEntity ent;
                if (isBomb)
                {
                    var bomb = Pools.Bomb.Take(_newBomb);
                    bomb.gameObject.SetActive(true);
                    bomb.Build(p, _cfg, _bouncy);
                    ent = bomb;
                }
                else
                {
                    var blk = Pools.Block.Take(_newBlock);
                    blk.gameObject.SetActive(true);
                    blk.Build(p, 2f, _cfg, _bouncy);
                    ent = blk;
                }
                _spawned.Add(ent);
            }
        }
    }
}
