using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>局外解锁项定义（代码驱动，增项只改表）。</summary>
    public class MetaUnlockDef
    {
        public string id;
        public string title;
        public string desc;
        public int cost;
        public bool repeatable;
        public int maxStacks = 1;
    }

    /// <summary>
    /// 局外进度（P10 = PlayerProfile 统一门面）：星尘/解锁/装备/最高层数，
    /// 全部经 IStorageService KV 持久化（小游戏可换 Stark 实现）。
    /// P10 前的 File 档案（geobreaker_meta.json）首次 Load 时一次性迁入 KV 并删除。
    /// </summary>
    public static class MetaProgress
    {
        [Serializable]
        class SaveData
        {
            public int stardust;
            public List<string> unlocks = new List<string>();   // 可重复项以多条目记堆叠数
            public string loadout = "";                         // 首发球种（kind 枚举名）
            public int bestLayer;                               // 最高层数纪录
        }

        public static readonly MetaUnlockDef[] UnlockDefs =
        {
            new MetaUnlockDef { id = "StartCrystalsPlus", title = "星图罗盘", desc = "每局开局水晶 +2", cost = 6,  repeatable = true,  maxStacks = 5 },
            new MetaUnlockDef { id = "StartStockPlus",   title = "备用弹体", desc = "每局开局球库存 +1", cost = 12, repeatable = false, maxStacks = 1 },
            new MetaUnlockDef { id = "StartUpgrade",     title = "预载构筑", desc = "每局开局获得 1 层随机强化", cost = 16, repeatable = false, maxStacks = 1 },
            new MetaUnlockDef { id = "GravityBall",      title = "引力球",   desc = "解锁球种：引力场吸引其他球与浮空方块聚团", cost = 20, repeatable = false, maxStacks = 1 },
            new MetaUnlockDef { id = "PierceBall",       title = "穿透球",   desc = "解锁球种：每发可穿透 2 个目标", cost = 18, repeatable = false, maxStacks = 1 },
            new MetaUnlockDef { id = "CurveBall",        title = "回旋球",   desc = "解锁球种：持续旋向偏转，飞行弹道成弧线", cost = 14, repeatable = false, maxStacks = 1 },
            new MetaUnlockDef { id = "MirrorBall",       title = "镜像球",   desc = "解锁球种：碰撞生成 50% 伤害的限时镜像", cost = 18, repeatable = false, maxStacks = 1 },
            new MetaUnlockDef { id = "PhaseBall",        title = "相位球",   desc = "解锁球种：节奏型幽灵穿墙——伤害后穿透方块/机关，直捣核心", cost = 22, repeatable = false, maxStacks = 1 },
            new MetaUnlockDef { id = "TimeBall",         title = "时间球",   desc = "解锁球种：命中即定格对方球一瞬，时停结界内机关减速 60%/2.5s", cost = 16, repeatable = false, maxStacks = 1 },
            new MetaUnlockDef { id = "FrostBall",        title = "冻结球",   desc = "解锁球种：命中目标冻结 2.5s（机关/Boss 停摆）", cost = 20, repeatable = false, maxStacks = 1 },
            new MetaUnlockDef { id = "ReboundBall",      title = "反弹球",   desc = "解锁球种：每次碰撞弹速 +8%，几何高手的连弹", cost = 12, repeatable = false, maxStacks = 1 },
        };

        static SaveData _d = new SaveData();
        public static bool Loaded { get; private set; }

        /// <summary>测试/工具注入存储实现（null=PlatformManager.Storage）。</summary>
        public static IStorageService StorageOverride;

        /// <summary>旧 File 档案路径（P10 前格式；迁移源，可被 SavePathOverride 覆盖以隔离测试）。</summary>
        public static string SavePathOverride;

        const string Key = "geobreaker_meta";
        static IStorageService S => StorageOverride ?? PlatformManager.Storage;
        static string LegacyFilePath =>
            SavePathOverride ?? Path.Combine(Application.persistentDataPath, "geobreaker_meta.json");

        public static void Load()
        {
            Loaded = true;
            try
            {
                // 一次性迁移：P10 前的 File 档案 → KV（迁完即删）
                if (!S.HasKey(Key) && File.Exists(LegacyFilePath))
                {
                    string legacy = File.ReadAllText(LegacyMetaFilePath());
                    if (!string.IsNullOrEmpty(legacy)) S.Set(Key, legacy);
                    File.Delete(LegacyFilePath);
                }
                string json = S.Get(Key);
                _d = string.IsNullOrEmpty(json) ? new SaveData() : (JsonUtility.FromJson<SaveData>(json) ?? new SaveData());
                MigrateLoadout();                           // GB_Loadout(PlayerPrefs) → loadout
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Meta] 存档读取失败，重置：" + e.Message);
                _d = new SaveData();
            }
        }

        /// <summary>旧 File 路径兼容：RunManager 也复用同一覆盖路径。</summary>
        internal static string LegacyMetaFilePath() => LegacyFilePath;

        public static void Save()
        {
            try { S.Set(Key, JsonUtility.ToJson(_d, true)); S.Flush(); }
            catch (Exception e) { Debug.LogWarning("[Meta] 存档写入失败：" + e.Message); }
        }

        /// <summary>装备字段迁移：旧 PlayerPrefs GB_Loadout → Profile.loadout。</summary>
        static void MigrateLoadout()
        {
            if (!string.IsNullOrEmpty(_d.loadout)) return;
            if (PlayerPrefs.HasKey("GB_Loadout"))
            {
                _d.loadout = PlayerPrefs.GetString("GB_Loadout");
                PlayerPrefs.DeleteKey("GB_Loadout");
                Save();
            }
        }

        public static int Stardust => _d.stardust;

        public static void AddStardust(int n)
        {
            _d.stardust = Mathf.Max(0, _d.stardust + n);
            Save();
        }

        public static int Stacks(string id)
        {
            int n = 0;
            foreach (var u in _d.unlocks) if (u == id) n++;
            return n;
        }

        public static bool HasUnlock(string id) => Stacks(id) > 0;

        /// <summary>按定义表购买（重复项受堆叠上限约束）。成功即存盘。</summary>
        public static bool Buy(string id)
        {
            var def = Find(id);
            if (def == null || Stacks(id) >= def.maxStacks || _d.stardust < def.cost) return false;
            _d.stardust -= def.cost;
            _d.unlocks.Add(id);
            Save();
            return true;
        }

        public static MetaUnlockDef Find(string id)
        {
            foreach (var d in UnlockDefs) if (d.id == id) return d;
            return null;
        }

        /// <summary>记录型解锁（Boss 击杀 BossK1..4 等，无成本；已存在则忽略）。</summary>
        public static void Record(string id)
        {
            if (!Loaded || HasUnlock(id)) return;
            _d.unlocks.Add(id);
            Save();
        }

        // ---------------- 开局加成（RunMap.Generate / StageManager 读） ----------------

        public static int StartCrystalBonus => Stacks("StartCrystalsPlus") * 2;
        public static int StartStockBonus => HasUnlock("StartStockPlus") ? 1 : 0;
        public static bool StartWithUpgrade => HasUnlock("StartUpgrade");
        public static bool GravityBallUnlocked => HasUnlock("GravityBall");

        // ---------------- 装备/纪录（P10 并入 Profile） ----------------

        /// <summary>首发球种（kind 枚举名；BallManager.RestoreLoadout 读）。</summary>
        public static string Loadout => _d.loadout;

        /// <summary>设置装备并持久化（MainMenuScreen 球库调用）。</summary>
        public static void SetLoadout(string kindName)
        {
            _d.loadout = kindName;
            Save();
        }

        /// <summary>最高层数纪录（RunManager.Save 时刷新；大厅显示）。</summary>
        public static int BestLayer => _d.bestLayer;

        public static void SetBestLayer(int layer)
        {
            if (layer <= _d.bestLayer) return;
            _d.bestLayer = layer;
            Save();
        }

        // ---------------- 结算奖励（RunMap.OnNodeCleared 调用） ----------------

        /// <summary>战斗节点通关：普通+3 / 精英+5 / Boss+10。</summary>
        public static void AwardNodeClear(NodeType t)
            => AddStardust(t == NodeType.Boss ? 10 : t == NodeType.Elite ? 5 : 3);

        /// <summary>结算预览（胜利战报显示用；不落账）。</summary>
        public static int PreviewNodeReward(NodeType t)
        {
            int n = t == NodeType.Boss ? 10 : t == NodeType.Elite ? 5 : 3;
            if (t == NodeType.Boss) n += 8;                     // Run 通关奖励并入预览
            return n;
        }

        /// <summary>Run 通关（Boss 节点击破）额外奖励。</summary>
        public static void AwardRunFinish() => AddStardust(8);

        /// <summary>清空存档（地图面板/大厅"重置存档"按钮；连带清旧 File 与装备 PlayerPrefs）。</summary>
        public static void ResetAll()
        {
            _d = new SaveData();
            Save();
            try
            {
                if (File.Exists(LegacyFilePath)) File.Delete(LegacyFilePath);
                if (PlayerPrefs.HasKey("GB_Loadout")) { PlayerPrefs.DeleteKey("GB_Loadout"); PlayerPrefs.Save(); }
            }
            catch { }
        }
    }
}
