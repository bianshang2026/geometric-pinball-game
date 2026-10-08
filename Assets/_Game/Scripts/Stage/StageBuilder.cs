using System.Collections.Generic;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>从 StageData 构建关卡实体（数据驱动；精英/Boss 修饰由 StageManager 传入）。返回核心（供护盾挂载）。
    /// P11 池化：可破坏方块/炸弹一律 Pools 取放（死亡回池，重建复用），机关家具为常驻一次性构建。</summary>
    public static class StageBuilder
    {
        static readonly System.Func<GeometryBlock> NewBlock = NewBlockInstance;
        static readonly System.Func<BombBlock> NewBomb = NewBombInstance;

        static GeometryBlock NewBlockInstance()
        {
            var go = new GameObject("BlockRoot");
            go.transform.SetParent(Pools.Root, false);
            return go.AddComponent<GeometryBlock>();
        }

        static BombBlock NewBombInstance()
        {
            var go = new GameObject("BombRoot");
            go.transform.SetParent(Pools.Root, false);
            return go.AddComponent<BombBlock>();
        }
        public static CoreBlock Build(Transform root, StageData data, GameConfig cfg, PhysicsMaterial2D bouncy,
            bool eliteMark = false, float coreScale = 1f, bool suppressShieldRing = false)
        {
            var portalA = new List<StageEntry>();
            var portalB = new List<StageEntry>();

            if (data.entries != null)
            {
                foreach (var e in data.entries)
                {
                    switch (e.kind)
                    {
                        case GeometryKind.Block: MakeBlock(root, e, cfg, bouncy); break;
                        case GeometryKind.Bomb: MakeBomb(root, e, cfg, bouncy); break;
                        case GeometryKind.Triangle: MakeTriangle(root, e, cfg, bouncy); break;
                        case GeometryKind.Diamond: MakeDiamond(root, e, cfg, bouncy); break;
                        case GeometryKind.Mirror: MakeMirror(root, e, cfg, bouncy); break;
                        case GeometryKind.Gravity: MakeGravity(root, e, cfg); break;
                        case GeometryKind.OneWay: MakeOneWay(root, e, cfg, bouncy); break;
                        case GeometryKind.TimeGate: MakeTimeGate(root, e, cfg); break;
                        case GeometryKind.EnergyNode: MakeEnergyNode(root, e, cfg); break;
                        case GeometryKind.BlackHole: MakeBlackHole(root, e, cfg); break;
                        case GeometryKind.MovingWall: MakeMovingWall(root, e, cfg); break;
                        case GeometryKind.SplitPrism: MakeSplitPrism(root, e, cfg); break;
                        case GeometryKind.FloatBlock: MakeFloatBlock(root, e, cfg, bouncy); break;
                        case GeometryKind.Portal:
                            if (e.variant % 2 == 0) portalA.Add(e); else portalB.Add(e);
                            break;
                    }
                }
            }

            for (int i = 0; i < Mathf.Min(portalA.Count, portalB.Count); i++)
            {
                var pair = new GameObject("PortalPair").AddComponent<PortalPair>();
                pair.transform.SetParent(root, false);
                pair.Build(portalA[i].position, portalB[i].position, cfg);
            }

            var coreGo = new GameObject("CoreRoot");
            coreGo.transform.SetParent(root, false);
            var core = coreGo.AddComponent<CoreBlock>().Build(data.corePos, data.coreHp, cfg, bouncy, eliteMark, coreScale);

            // Boss 护盾（文档二十八）：核心外圈 10 块小方块，护盾在则核心免疫。
            // P8：BossData 驱动的 Boss 由 BossController 按阶段建环（suppressShieldRing 抑制静态环防双重叠加）
            if (eliteMark == false && coreScale > 1f && !suppressShieldRing && core != null)
            {
                const int count = 10;
                const float radius = 1.75f;
                for (int i = 0; i < count; i++)
                {
                    float a = i * (360f / count) + 18f;
                    Vector2 p = data.corePos + (Vector2)(Quaternion.Euler(0f, 0f, a) * Vector3.up) * radius;
                    var shield = Pools.Block.Take(NewBlock);
                    shield.transform.SetParent(root, false);
                    shield.gameObject.SetActive(true);
                    shield.Build(p, 2f, cfg, bouncy, 0.55f);          // 护盾小块（Build 内重设 Fill/Outline 尺寸）
                    shield.name = "ShieldBlock";
                    core.AttachShield(shield);
                }
            }
            return core;
        }

        static GeometryBlock MakeBlock(Transform root, StageEntry e, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            var b = Pools.Block.Take(NewBlock);
            b.transform.SetParent(root, false);
            b.gameObject.SetActive(true);
            return b.Build(e.position, e.hp, cfg, bouncy);
        }

        static BombBlock MakeBomb(Transform root, StageEntry e, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            var bomb = Pools.Bomb.Take(NewBomb);
            bomb.transform.SetParent(root, false);
            bomb.gameObject.SetActive(true);
            return bomb.Build(e.position, cfg, bouncy);
        }

        static TriangleReflector MakeTriangle(Transform root, StageEntry e, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            var go = new GameObject("TriRoot");
            go.transform.SetParent(root, false);
            var bottom = e.variant == 0 ? EdgeRule.SpeedUp : EdgeRule.SlowDown;
            var tri = go.AddComponent<TriangleReflector>()
                .Build(e.position, e.rotationDeg, cfg, bouncy, bottom, EdgeRule.Normal, EdgeRule.Normal);
            tri.rotationSpeedDeg = e.spinDeg;                 // P7：旋转哨卫（数据驱动自旋）
            return tri;
        }

        static DiamondAccelerator MakeDiamond(Transform root, StageEntry e, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            var go = new GameObject("DiaRoot");
            go.transform.SetParent(root, false);
            return go.AddComponent<DiamondAccelerator>().Build(e.position, cfg, bouncy);
        }

        static MirrorLine MakeMirror(Transform root, StageEntry e, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            var go = new GameObject("MirRoot");
            go.transform.SetParent(root, false);
            return go.AddComponent<MirrorLine>().Build(e.position, e.rotationDeg, e.length, cfg, bouncy);
        }

        static GravityField MakeGravity(Transform root, StageEntry e, GameConfig cfg)
        {
            var go = new GameObject("GravRoot");
            go.transform.SetParent(root, false);
            return go.AddComponent<GravityField>().Build(e.position, e.variant, cfg);
        }

        static OneWayWall MakeOneWay(Transform root, StageEntry e, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            var go = new GameObject("OneWayRoot");
            go.transform.SetParent(root, false);
            return go.AddComponent<OneWayWall>().Build(e.position, e.rotationDeg, e.length, cfg, bouncy);
        }

        static TimeGate MakeTimeGate(Transform root, StageEntry e, GameConfig cfg)
        {
            var go = new GameObject("GateRoot");
            go.transform.SetParent(root, false);
            // rotationDeg: 0=横向门 90=竖向门；variant=相位档位(×0.6s)
            return go.AddComponent<TimeGate>().Build(e.position, Mathf.Abs(e.rotationDeg) > 45f, e.variant, cfg);
        }

        // ---------------- 第三批机关 ----------------

        static EnergyNode MakeEnergyNode(Transform root, StageEntry e, GameConfig cfg)
        {
            var go = new GameObject("EnergyRoot");
            go.transform.SetParent(root, false);
            return go.AddComponent<EnergyNode>().Build(e.position, cfg);
        }

        static BlackHole MakeBlackHole(Transform root, StageEntry e, GameConfig cfg)
        {
            var go = new GameObject("HoleRoot");
            go.transform.SetParent(root, false);
            // variant=组 id：同组任意两洞互通
            return go.AddComponent<BlackHole>().Build(e.position, e.variant, cfg);
        }

        static MovingWall MakeMovingWall(Transform root, StageEntry e, GameConfig cfg)
        {
            var go = new GameObject("MWallRoot");
            go.transform.SetParent(root, false);
            // length=振幅；spinDeg=周期秒；rotationDeg 0=横移 90=竖移
            return go.AddComponent<MovingWall>().Build(e.position, e.length, e.spinDeg > 0.5f ? e.spinDeg : 3f,
                Mathf.Abs(e.rotationDeg) > 45f, cfg);
        }

        static SplitPrism MakeSplitPrism(Transform root, StageEntry e, GameConfig cfg)
        {
            var go = new GameObject("PrismRoot");
            go.transform.SetParent(root, false);
            return go.AddComponent<SplitPrism>().Build(e.position, cfg);
        }

        static FloatBlock MakeFloatBlock(Transform root, StageEntry e, GameConfig cfg, PhysicsMaterial2D bouncy)
        {
            var f = Pools.Float.Take(NewFloat);
            f.transform.SetParent(root, false);
            f.gameObject.SetActive(true);
            return f.Build(e.position, e.hp, cfg, bouncy);
        }

        static readonly System.Func<FloatBlock> NewFloat = NewFloatInstance;
        static FloatBlock NewFloatInstance()
        {
            var go = new GameObject("FloatRoot");
            go.transform.SetParent(Pools.Root, false);
            return go.AddComponent<FloatBlock>();
        }
    }
}
