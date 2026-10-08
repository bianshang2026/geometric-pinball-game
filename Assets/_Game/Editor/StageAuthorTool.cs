using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GeoBreaker.EditorTools
{
    /// <summary>
    /// 30 张战斗地图生成器（P7，架构文档 §9）：从紧凑布局表批量产出 Stage01..30 资产 +
    /// 穿透球 PierceBall 资产。菜单：GeoBreaker/生成/重建30图+穿透球。
    /// 改图=改本表再执行；数据即注释。
    /// </summary>
    public static class StageAuthorTool
    {
        // ---------------- 条目构造 DSL ----------------

        static StageEntry E(GeometryKind k, float x, float y, float hp = 3f,
            float rot = 0f, float len = 1f, int variant = 0, float spin = 0f)
            => new StageEntry { kind = k, position = new Vector2(x, y), hp = hp, rotationDeg = rot, length = len, variant = variant, spinDeg = spin };

        static StageEntry B(float x, float y, float hp = 1f) => E(GeometryKind.Block, x, y, hp);
        static StageEntry Tri(float x, float y, float rot = 0f, int variant = 0, float spin = 0f)
            => E(GeometryKind.Triangle, x, y, 3f, rot, 1f, variant, spin);
        static StageEntry Dia(float x, float y) => E(GeometryKind.Diamond, x, y);
        static StageEntry Mir(float x, float y, float rot, float len = 2f) => E(GeometryKind.Mirror, x, y, 3f, rot, len);
        static StageEntry Bomb(float x, float y) => E(GeometryKind.Bomb, x, y);
        static StageEntry PA(float x, float y) => E(GeometryKind.Portal, x, y, 3f, 0f, 1f, 0);
        static StageEntry PB(float x, float y) => E(GeometryKind.Portal, x, y, 3f, 0f, 1f, 1);
        static StageEntry Grav(float x, float y, int variant = 0) => E(GeometryKind.Gravity, x, y, 3f, 0f, 1f, variant);
        static StageEntry OW(float x, float y, float rot, float len = 2f) => E(GeometryKind.OneWay, x, y, 3f, rot, len);
        static StageEntry Gate(float x, float y, bool vertical, int phase)
            => E(GeometryKind.TimeGate, x, y, 3f, vertical ? 90f : 0f, 1f, phase);
        static StageEntry FB(float x, float y, float hp = 3f) => E(GeometryKind.FloatBlock, x, y, hp);
        static StageEntry EN(float x, float y) => E(GeometryKind.EnergyNode, x, y);
        static StageEntry BH(float x, float y, int group) => E(GeometryKind.BlackHole, x, y, 3f, 0f, 1f, group);
        static StageEntry MW(float x, float y, float amp, float period, bool vertical)
            => E(GeometryKind.MovingWall, x, y, 3f, vertical ? 90f : 0f, amp, 0, period);
        static StageEntry SP(float x, float y) => E(GeometryKind.SplitPrism, x, y);

        [MenuItem("GeoBreaker/生成/重建30图+穿透球")]
        public static void Build()
        {
            int made = 0;
            for (int id = 1; id <= 30; id++)
            {
                var d = MakeStage(id);
                if (d == null) continue;
                WriteStage(d);
                made++;
            }
            WritePierceBall();
            WriteBossData();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[StageAuthor] 生成 {made} 张地图 + 穿透球 + 5 Boss 完成");
        }

        // ---------------- 5 BossData（架构 §15）----------------

        static void WriteBossData()
        {
            for (int i = 1; i <= 5; i++)
            {
                string path = $"Assets/_Game/Data/Resources/Boss{i}.asset";
                if (AssetDatabase.LoadAssetAtPath<BossData>(path) != null)
                    AssetDatabase.DeleteAsset(path);
                var b = ScriptableObject.CreateInstance<BossData>();
                switch (i)
                {
                    case 1:  // B1 旋转六边堡：护盾环 12 块 45°/s + 弱点弧（教学弧窗口）
                        b.bossName = "旋转六边堡";
                        b.phases = new[] { P(hpTo: 0f, ring: 12, ringRot: 45f, arc: true, arcSpeed: 60f) };
                        break;
                    case 2:  // B2 镜面三棱核：镜面装甲三片（Mirror 阵列留在关卡布局）+ 阶段快弧
                        b.bossName = "镜面三棱核";
                        b.phases = new BossPhase[] {
                            P(hpTo: 0.5f, ring: 6, ringRot: 25f, arc: false),
                            P(hpTo: 0f, ring: 0, ringRot: 0, arc: true, arcSpeed: 75f, armor: 0.15f) };
                        break;
                    case 3:  // B3 混沌孕核：活体屏障生成（上限 14）——炸弹连锁开路
                        b.bossName = "混沌孕核";
                        b.phases = new BossPhase[] {
                            P(hpTo: 0f, arc: true, arcSpeed: 40f, crit: 1.5f, spawn: 7f, spawnMax: 14) };
                        break;
                    case 4:  // B4 黑洞引力核：引力拉球入轨 + 相位快弧 80°/s 弧外×0.1
                        b.bossName = "黑洞引力核";
                        b.phases = new BossPhase[] {
                            P(hpTo: 0f, arc: true, arcSpeed: 80f, armor: 0.1f, grav: 67f, gravR: 3.2f) };
                        break;
                    case 5:  // B5 终焉几何核：三阶段（护盾环→快弧+生成→狂暴引力+生成+100°/s）
                        b.bossName = "终焉几何核";
                        b.phases = new BossPhase[] {
                            P(hpTo: 0.66f, ring: 10, ringRot: 50f, arc: false),
                            P(hpTo: 0.33f, ring: 0, ringRot: 0, arc: true, arcSpeed: 70f, armor: 0.15f,
                              spawn: 8f, spawnMax: 10),
                            P(hpTo: 0f, ring: 0, ringRot: 0, arc: true, arcSpeed: 100f, armor: 0.1f,
                              grav: 60f, gravR: 3f, spawn: 6f, spawnMax: 12) };
                        break;
                }
                AssetDatabase.CreateAsset(b, path);
            }
        }

        static BossPhase P(float hpTo, float ring = 0, float ringRot = 0, bool arc = false,
            float arcSpeed = 0f, float crit = 2f, float armor = 0.2f,
            float grav = 0f, float gravR = 2.5f, float spawn = 0f, int spawnMax = 0)
            => new BossPhase
            {
                hpTo = hpTo,
                shieldRingCount = ring,
                shieldRingRotSpeed = ringRot,
                weakArc = arc,
                weakArcSpeedDeg = arcSpeed,
                weakCritMultiplier = crit,
                weakArmorMultiplier = armor,
                gravityStrength = grav,
                gravityRadius = gravR,
                spawnerPeriod = spawn,
                spawnerMax = spawnMax,
            };

        static void WriteStage(StageData d)
        {
            string path = $"Assets/_Game/Data/Resources/Stage{d.stageId:00}.asset";
            if (AssetDatabase.LoadAssetAtPath<StageData>(path) != null)
                AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(d, path);
        }

        static void WritePierceBall()
        {
            string path = "Assets/_Game/Data/Resources/PierceBall.asset";
            if (AssetDatabase.LoadAssetAtPath<BallData>(path) != null)
                AssetDatabase.DeleteAsset(path);
            var b = ScriptableObject.CreateInstance<BallData>();
            b.ballName = "穿透球";
            b.kind = BallKind.Pierce;
            b.speed = 13f;
            b.damage = 1;
            b.radius = 0.28f;
            b.splitAngleDeg = 25f;
            b.coreColor = Color.white;
            b.glowColor = new Color(0.25f, 0.55f, 1f, 1f);          // 蓝晕
            b.trailColor = new Color(0.85f, 0.95f, 1f, 1f);         // 亮白拖尾
            AssetDatabase.CreateAsset(b, path);
        }

        // ---------------- 30 图布局表 ----------------

        static StageData MakeStage(int id)
        {
            var d = ScriptableObject.CreateInstance<StageData>();
            d.stageId = id;
            d.corePos = new Vector2(0f, 6.5f);
            var e = new List<StageEntry>();
            switch (id)
            {
                // ===== R1 矩阵实验场（01-06）P9.5 密度强化 =====
                case 1: d.stageName = "初识矩阵"; d.coreHp = 10; d.ballStock = 4;
                    e.Add(B(-1.2f, 3.2f)); e.Add(B(0, 3.2f)); e.Add(B(1.2f, 3.2f));
                    e.Add(B(-1.2f, 4.4f)); e.Add(B(0, 4.4f)); e.Add(B(1.2f, 4.4f));
                    e.Add(Tri(0, 5.6f, 0f, 1));
                    e.Add(B(-2.8f, 4.8f)); e.Add(B(2.8f, 4.8f));
                    break;
                case 2: d.stageName = "边之规则"; d.coreHp = 11;
                    e.Add(Tri(-1.8f, 4.2f, 0f, 1, 35f)); e.Add(Tri(1.8f, 4.2f, 0f, 0, -35f));
                    e.Add(B(-0.8f, 5.4f)); e.Add(B(0.8f, 5.4f)); e.Add(B(0, 6.1f));
                    e.Add(B(-2.9f, 3.4f)); e.Add(B(2.9f, 3.4f));
                    e.Add(Dia(0, 3.0f));
                    e.Add(SP(-1.6f, 1.8f));
                    break;
                case 3: d.stageName = "加速走廊"; d.coreHp = 11;
                    e.Add(Dia(-1.5f, 4.0f)); e.Add(Dia(0, 4.6f)); e.Add(Dia(1.5f, 5.2f));
                    e.Add(B(-2.6f, 3.8f)); e.Add(B(2.6f, 3.8f));
                    e.Add(B(-2.6f, 5.0f)); e.Add(B(2.6f, 5.0f));
                    e.Add(Tri(0, 6.2f, 0f, 1)); e.Add(B(-3.2f, 4.6f, 2f));
                    e.Add(MW(0, 2.2f, 1.6f, 3.2f, false));
                    break;
                case 4: d.stageName = "护墙之后"; d.coreHp = 12;
                    e.Add(B(-1.5f, 5.4f, 2f)); e.Add(B(0, 5.7f, 2f)); e.Add(B(1.5f, 5.4f, 2f));
                    e.Add(B(-2.7f, 4.2f)); e.Add(B(2.7f, 4.2f));
                    e.Add(Dia(-1.2f, 3.2f)); e.Add(Dia(1.2f, 3.2f));
                    e.Add(Tri(0, 4.4f, 0f, 0, 30f)); e.Add(B(-3.3f, 5.8f)); e.Add(B(3.3f, 5.8f));
                    break;
                case 5: d.stageName = "双三角夹击"; d.coreHp = 12;
                    e.Add(Tri(-2.4f, 4.2f, 0f, 0, -40f)); e.Add(Tri(2.4f, 4.2f, 0f, 1, 40f));
                    e.Add(B(-1.1f, 5.0f)); e.Add(B(0, 5.4f)); e.Add(B(1.1f, 5.0f));
                    e.Add(Dia(0, 3.4f)); e.Add(B(-3.2f, 3.0f)); e.Add(B(3.2f, 3.0f));
                    e.Add(B(-2.0f, 6.2f)); e.Add(B(2.0f, 6.2f));
                    e.Add(FB(-3.0f, 5.6f)); e.Add(FB(3.0f, 5.6f));
                    break;
                case 6: d.stageName = "实验场Boss关"; d.coreHp = 12;
                    e.Add(B(-1.2f, 4.4f)); e.Add(B(1.2f, 4.4f));
                    e.Add(Dia(0, 3.2f)); e.Add(Tri(-2.6f, 5.6f, 0f, 0)); e.Add(Tri(2.6f, 5.6f, 0f, 1));
                    e.Add(B(-2.4f, 4.0f)); e.Add(B(2.4f, 4.0f)); e.Add(B(0, 5.8f));
                    break;

                // ===== R2 镜面回廊（07-12）P9.5 密度强化 =====
                case 7: d.stageName = "第一面镜"; d.coreHp = 12;
                    e.Add(Mir(-2.4f, 5.0f, -45f, 2.4f)); e.Add(Mir(2.4f, 3.4f, 30f, 2.0f));
                    e.Add(B(1.0f, 4.4f)); e.Add(B(1.0f, 5.6f)); e.Add(B(-1.0f, 4.0f));
                    e.Add(Tri(0, 6.2f, 0f, 1, 35f)); e.Add(B(-3.3f, 4.6f)); e.Add(B(3.3f, 4.6f));
                    e.Add(SP(-1.6f, 2.2f));
                    break;
                case 8: d.stageName = "镜梯"; d.coreHp = 13;
                    e.Add(Mir(-2.4f, 3.6f, -40f, 2.2f)); e.Add(Mir(0, 4.8f, 40f, 2.2f)); e.Add(Mir(2.4f, 6.0f, -40f, 2.2f));
                    e.Add(B(-1.5f, 5.8f)); e.Add(B(1.5f, 3.4f));
                    e.Add(Tri(-3.0f, 4.4f, 0f, 0)); e.Add(B(0, 6.4f));
                    e.Add(FB(3.2f, 4.8f));
                    break;
                case 9: d.stageName = "单向之门"; d.coreHp = 13;
                    e.Add(OW(-1.8f, 4.8f, 0f, 2f)); e.Add(OW(1.8f, 4.8f, 0f, 2f));
                    e.Add(B(0, 6.0f)); e.Add(B(-0.8f, 3.6f)); e.Add(B(0.8f, 3.6f));
                    e.Add(Mir(-3.0f, 5.6f, 60f, 1.8f)); e.Add(Tri(0, 5.2f, 0f, 1)); e.Add(B(-3.0f, 3.4f)); e.Add(B(3.0f, 3.4f));
                    e.Add(MW(0, 3.2f, 0.5f, 2.6f, true));
                    break;
                case 10: d.stageName = "旋转哨卫"; d.coreHp = 14;
                    e.Add(Tri(-2.0f, 4.6f, 0f, 0, 40f)); e.Add(Tri(2.0f, 4.6f, 0f, 1, -40f));
                    e.Add(Mir(0, 6.2f, 0f, 1.8f));
                    e.Add(B(-1.1f, 3.6f)); e.Add(B(1.1f, 3.6f)); e.Add(B(-3.2f, 5.2f)); e.Add(B(3.2f, 5.2f));
                    e.Add(Dia(0, 4.8f));
                    e.Add(BH(-3.4f, 3.2f, 10)); e.Add(BH(3.4f, 6.6f, 10));
                    break;
                case 11: d.stageName = "回廊迷影"; d.coreHp = 14;
                    e.Add(Mir(-2.6f, 3.8f, 60f, 1.8f)); e.Add(Mir(2.6f, 3.8f, -60f, 1.8f));
                    e.Add(Mir(0, 6.2f, 0f, 1.8f));
                    e.Add(B(-1.0f, 5.0f)); e.Add(B(1.0f, 5.0f));
                    e.Add(Tri(-2.0f, 6.2f, 0f, 0)); e.Add(B(-3.4f, 4.8f)); e.Add(B(3.4f, 4.8f)); e.Add(B(0, 3.0f, 2f));
                    e.Add(FB(-0.9f, 4.2f)); e.Add(FB(0.9f, 4.2f));
                    break;
                case 12: d.stageName = "回廊Boss关"; d.coreHp = 14;
                    e.Add(Mir(-2.8f, 4.4f, 55f, 2f)); e.Add(Mir(2.8f, 4.4f, -55f, 2f));
                    e.Add(B(-1.2f, 3.4f)); e.Add(B(1.2f, 3.4f)); e.Add(Dia(0, 5.4f));
                    e.Add(Tri(-1.8f, 6.0f, 0f, 1, -45f)); e.Add(B(-3.2f, 5.6f)); e.Add(B(3.2f, 5.6f)); e.Add(B(0, 6.4f));
                    break;

                // ===== R3 混沌裂隙（13-18）P9.5 密度强化 =====
                case 13: d.stageName = "裂隙入口"; d.coreHp = 13;
                    e.Add(PA(-2.4f, 4.2f)); e.Add(PB(2.4f, 4.2f));
                    e.Add(B(0, 5.2f)); e.Add(B(0, 6.2f)); e.Add(B(-1.4f, 3.6f)); e.Add(B(1.4f, 3.6f));
                    e.Add(Tri(-3.0f, 5.0f, 0f, 0)); e.Add(Dia(0, 4.4f)); e.Add(B(-3.2f, 3.4f)); e.Add(B(3.2f, 3.4f));
                    e.Add(FB(-0.7f, 2.6f)); e.Add(FB(0.7f, 2.6f));
                    break;
                case 14: d.stageName = "双门快递"; d.coreHp = 14;
                    e.Add(PA(-2.6f, 3.4f)); e.Add(PB(2.6f, 3.4f)); e.Add(PA(-2.6f, 6.2f)); e.Add(PB(2.6f, 6.2f));
                    e.Add(Dia(0, 4.8f)); e.Add(B(-1.3f, 4.4f)); e.Add(B(1.3f, 4.4f));
                    e.Add(Mir(0, 5.4f, 0f, 1.6f)); e.Add(Tri(0, 6.4f, 0f, 1));
                    e.Add(BH(-3.4f, 2.4f, 14)); e.Add(BH(3.4f, 2.4f, 14));
                    break;
                case 15: d.stageName = "炸弹阵 I"; d.coreHp = 13;
                    e.Add(Bomb(-1.4f, 4.6f)); e.Add(Bomb(1.4f, 4.6f)); e.Add(Bomb(0, 5.8f));
                    e.Add(B(-2.6f, 5.4f)); e.Add(B(2.6f, 5.4f));
                    e.Add(Tri(-2.2f, 3.8f, 0f, 0)); e.Add(B(0, 4.6f, 2f));
                    e.Add(Gate(0, 3.6f, false, 1));               // EN 联动门：激活强制开 → 直通炸弹阵
                    e.Add(EN(0, 2.2f));
                    break;
                case 16: d.stageName = "相位之门"; d.coreHp = 14;
                    e.Add(Gate(0, 3.6f, false, 0)); e.Add(Gate(0, 5.4f, false, 0)); e.Add(Bomb(0, 6.4f));
                    e.Add(B(-1.4f, 4.5f)); e.Add(B(1.4f, 4.5f));
                    e.Add(Tri(-3.0f, 4.0f, 0f, 1)); e.Add(B(-2.4f, 5.6f)); e.Add(B(2.4f, 5.6f));
                    e.Add(FB(-2.8f, 2.8f)); e.Add(FB(2.8f, 2.8f));
                    break;
                case 17: d.stageName = "交错相位"; d.coreHp = 15;
                    e.Add(Gate(-1.2f, 4.2f, true, 0)); e.Add(Gate(1.2f, 4.2f, true, 2));
                    e.Add(PA(-2.8f, 3.6f)); e.Add(PB(2.8f, 3.6f));
                    e.Add(B(-1.0f, 6.4f)); e.Add(B(1.0f, 6.4f));
                    e.Add(Tri(0, 4.0f, 0f, 0)); e.Add(B(0, 5.6f, 2f));
                    e.Add(EN(0, 2.4f));
                    break;
                case 18: d.stageName = "裂隙Boss关"; d.coreHp = 15;
                    e.Add(Bomb(-1.5f, 4.6f)); e.Add(Bomb(1.5f, 4.6f)); e.Add(Bomb(0, 6.0f));
                    e.Add(Gate(0, 3.4f, false, 1)); e.Add(B(-2.6f, 5.8f)); e.Add(B(2.6f, 5.8f));
                    e.Add(Tri(-2.0f, 4.2f, 0f, 1)); e.Add(Mir(0, 5.2f, 0f, 1.4f)); e.Add(B(0, 4.4f));
                    break;
                // R4/R5 见表尾追加
                default: return MakeStage2(id, d, e);
            }
            d.entries = e.ToArray();
            return d;
        }
        // ---------------- R4/R5（19-30）P9.5 密度强化 ----------------

        static StageData MakeStage2(int id, StageData d, List<StageEntry> e)
        {
            switch (id)
            {
                // ===== R4 引力穹界（19-24）=====
                case 19: d.stageName = "引力初现"; d.coreHp = 13;
                    e.Add(Grav(0, 4.6f, 0));
                    e.Add(B(-1.6f, 6.0f)); e.Add(B(1.6f, 6.0f));
                    e.Add(Tri(-3.0f, 4.4f, 0f, 0)); e.Add(Dia(0, 5.6f));
                    e.Add(B(-1.0f, 3.2f)); e.Add(B(1.0f, 3.2f)); e.Add(B(-3.2f, 3.2f)); e.Add(B(3.2f, 3.2f));
                    e.Add(FB(-2.0f, 5.2f)); e.Add(FB(2.0f, 5.2f)); e.Add(FB(0, 2.2f));
                    break;
                case 20: d.stageName = "推拉之间"; d.coreHp = 14;
                    e.Add(Grav(-2.2f, 4.8f, 0)); e.Add(Grav(2.2f, 4.8f, 1)); e.Add(Mir(0, 6.2f, 0f, 1.4f));
                    e.Add(B(-1.0f, 3.6f)); e.Add(B(1.0f, 3.6f));
                    e.Add(Tri(0, 4.6f, 0f, 1)); e.Add(B(-3.2f, 5.6f)); e.Add(B(3.2f, 5.6f)); e.Add(Dia(0, 3.2f));
                    e.Add(FB(0, 2.4f)); e.Add(FB(-1.9f, 6.4f)); e.Add(FB(1.9f, 6.4f));
                    break;
                case 21: d.stageName = "轨道加速环"; d.coreHp = 15;
                    e.Add(Grav(0, 4.8f, 0)); e.Add(Dia(-1.4f, 3.8f)); e.Add(Dia(1.4f, 3.8f)); e.Add(Dia(0, 6.2f));
                    e.Add(Tri(-3.0f, 4.8f, 0f, 0)); e.Add(B(-2.6f, 6.2f)); e.Add(B(2.6f, 6.2f));
                    e.Add(B(-1.0f, 3.0f)); e.Add(B(1.0f, 3.0f));
                    e.Add(FB(2.9f, 3.4f)); e.Add(FB(-1.9f, 5.6f)); e.Add(FB(1.9f, 5.6f));
                    break;
                case 22: d.stageName = "黑洞窄巷"; d.coreHp = 15;
                    e.Add(Grav(-0.8f, 5.0f, 0)); e.Add(Grav(0.8f, 5.0f, 0));
                    e.Add(OW(-2.6f, 4.0f, 90f, 2.4f)); e.Add(OW(2.6f, 4.0f, 90f, 2.4f));
                    e.Add(Tri(0, 3.4f, 0f, 0)); e.Add(B(-3.2f, 5.6f)); e.Add(B(3.2f, 5.6f));
                    e.Add(B(0, 6.4f)); e.Add(Dia(0, 4.0f));
                    e.Add(FB(-1.7f, 3.0f)); e.Add(FB(1.7f, 3.0f)); e.Add(FB(0, 2.0f));
                    break;
                case 23: d.stageName = "双星系统"; d.coreHp = 15;
                    e.Add(Grav(-1.8f, 4.6f, 0)); e.Add(Grav(1.8f, 4.6f, 0));
                    e.Add(PA(-3.4f, 6.4f)); e.Add(PB(3.4f, 6.4f));
                    e.Add(Tri(0, 3.6f, 0f, 0)); e.Add(B(-1.0f, 3.2f)); e.Add(B(1.0f, 3.2f));
                    e.Add(B(0, 6.2f)); e.Add(Dia(-0.8f, 5.4f));
                    e.Add(FB(0, 4.6f)); e.Add(FB(-2.6f, 2.8f)); e.Add(FB(2.6f, 2.8f));
                    break;
                case 24: d.stageName = "穹界Boss关"; d.coreHp = 16;
                    e.Add(Grav(0, 4.4f, 0)); e.Add(Bomb(-2.0f, 6.0f)); e.Add(Bomb(2.0f, 6.0f));
                    e.Add(Mir(-3.0f, 5.0f, 60f, 1.6f)); e.Add(Mir(3.0f, 5.0f, -60f, 1.6f));
                    e.Add(Tri(0, 3.4f, 0f, 0)); e.Add(B(-1.2f, 4.8f)); e.Add(B(1.2f, 4.8f)); e.Add(B(0, 6.4f));
                    e.Add(FB(-2.0f, 3.4f)); e.Add(FB(2.0f, 3.4f)); e.Add(FB(0, 2.2f));
                    break;

                // ===== R5 核心圣殿（25-30）=====
                case 25: d.stageName = "圣殿前厅"; d.coreHp = 16;
                    e.Add(B(-1, 4.0f)); e.Add(B(1, 4.0f)); e.Add(Dia(0, 5.2f));
                    e.Add(Mir(-2.6f, 5.8f, 50f, 1.6f)); e.Add(Grav(2.4f, 4.4f, 1));
                    e.Add(Tri(0, 3.4f, 0f, 0)); e.Add(B(-3.2f, 3.6f)); e.Add(B(3.2f, 3.6f)); e.Add(B(0, 6.4f));
                    e.Add(FB(-1.6f, 2.6f)); e.Add(FB(1.6f, 2.6f));
                    break;
                case 26: d.stageName = "精锐走廊"; d.coreHp = 18;
                    e.Add(B(-1.6f, 3.6f, 2)); e.Add(B(0, 4.2f, 2)); e.Add(B(1.6f, 3.6f, 2));
                    e.Add(Dia(0, 5.6f)); e.Add(Tri(-2.4f, 5.0f, 0f, 0)); e.Add(Tri(2.4f, 5.0f, 0f, 1));
                    e.Add(B(-3.2f, 4.0f)); e.Add(B(3.2f, 4.0f));
                    e.Add(B(-1.2f, 5.0f)); e.Add(B(1.2f, 5.0f)); e.Add(B(0, 6.6f));
                    e.Add(SP(1.8f, 2.0f)); e.Add(MW(-2.2f, 2.6f, 1.2f, 3.0f, false));
                    break;
                case 27: d.stageName = "连锁大教堂"; d.coreHp = 18;
                    e.Add(Bomb(-1.8f, 4.2f)); e.Add(Bomb(0, 5.0f)); e.Add(Bomb(1.8f, 4.2f));
                    e.Add(Bomb(-0.9f, 6.0f)); e.Add(Bomb(0.9f, 6.0f));
                    e.Add(Tri(-2.8f, 4.8f, 0f, 0)); e.Add(Tri(2.8f, 4.8f, 0f, 0));
                    e.Add(B(-3.2f, 6.2f)); e.Add(B(3.2f, 6.2f));
                    e.Add(Gate(0, 3.5f, false, 1));               // EN 联动门：激活强制开 → 直通炸弹链
                    e.Add(EN(0, 2.6f));
                    break;
                case 28: d.stageName = "镜与引力"; d.coreHp = 18;
                    e.Add(Mir(-2.4f, 4.2f, 55f, 2f)); e.Add(Mir(2.4f, 4.2f, -55f, 2f));
                    e.Add(Grav(0, 5.4f, 0)); e.Add(Gate(0, 3.2f, false, 1));
                    e.Add(Tri(-3.2f, 5.4f, 0f, 1));                     e.Add(B(-1.2f, 4.4f)); e.Add(B(1.2f, 4.4f));
                    e.Add(B(0, 6.8f));
                    e.Add(FB(-1.4f, 6.2f)); e.Add(FB(1.4f, 6.2f)); e.Add(FB(0, 2.2f));
                    break;
                case 29: d.stageName = "门后之王"; d.coreHp = 18;
                    e.Add(Gate(0, 4.0f, true, 0)); e.Add(Gate(-1.6f, 5.6f, true, 2)); e.Add(Gate(1.6f, 5.6f, true, 2));
                    e.Add(OW(0, 6.9f, 0f, 1.6f)); e.Add(Bomb(-1.0f, 6.6f)); e.Add(Bomb(1.0f, 6.6f));
                    e.Add(Tri(0, 3.0f, 0f, 0)); e.Add(B(-3.0f, 3.6f)); e.Add(B(3.0f, 3.6f));
                    e.Add(EN(-2.6f, 5.0f));
                    break;
                case 30: d.stageName = "终焉圣殿"; d.coreHp = 20;
                    e.Add(B(-2, 4.0f, 2)); e.Add(B(2, 4.0f, 2));
                    e.Add(Tri(-1, 5.4f, 0f, 1)); e.Add(Tri(1, 5.4f, 0f, 1));
                    e.Add(Dia(0, 4.6f)); e.Add(Bomb(-2.6f, 6.0f)); e.Add(Bomb(2.6f, 6.0f));
                    e.Add(Mir(0, 6.8f, 0f, 1.4f)); e.Add(Grav(0, 3.4f, 0));
                    e.Add(B(-3.2f, 4.4f)); e.Add(B(3.2f, 4.4f)); e.Add(B(0, 3.2f, 2f));
                    e.Add(FB(-1.4f, 2.4f)); e.Add(FB(1.4f, 2.4f));
                    break;
                default: return null;                             // 未知 id：跳过
            }
            d.entries = e.ToArray();
            return d;
        }
    }
}
