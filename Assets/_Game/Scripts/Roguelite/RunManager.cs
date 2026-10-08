using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GeoBreaker
{
    /// <summary>Run 中断存档（架构 §7.5/§18）：同 seed 重建地图 + 进度/水晶/词缀回放。</summary>
    [Serializable]
    public class RunData
    {
        public int runSeed;
        public int currentId;
        public int crystals;
        public bool currentFailed;
        public int bonusStock;                             // 宝箱"弹药补给"：本 Run 球库存加成
        public List<int> clearedIds = new List<int>();
        public List<int> buildKinds = new List<int>();     // 与 buildCounts 平行（词缀堆叠）
        public List<int> buildCounts = new List<int>();
    }

    /// <summary>
    /// Run 生命周期门面（P10 存储化）：断点续玩经 IStorageService KV（geobreaker_run），
    /// P10 前的 File 档案（geobreaker_run.json）首次 HasResumableRun 时一次性迁入并删除。
    /// 存档时机：Travel 进节点后 / 节点胜利结算后 / 失败标记后；Boss 通关删除存档。
    /// </summary>
    public static class RunManager
    {
        const string Key = "geobreaker_run";

        /// <summary>测试注入存储实现（null=PlatformManager.Storage）。</summary>
        public static IStorageService StorageOverride;

        /// <summary>旧 File 档案路径覆盖（兼容 P5 测试与迁移源隔离）。</summary>
        public static string SavePathOverride;

        static IStorageService S => StorageOverride ?? PlatformManager.Storage;
        static string LegacyFilePath =>
            SavePathOverride ?? Path.Combine(Application.persistentDataPath, "geobreaker_run.json");

        /// <summary>有可恢复 Run（KV 有档，或旧 File 档待迁移）。</summary>
        public static bool HasResumableRun
        {
            get
            {
                if (S.HasKey(Key)) return true;
                try { return File.Exists(LegacyFilePath); }
                catch { return false; }
            }
        }

        /// <summary>保存当前 Run 进度（RunFinished 时不保存；同步刷新最高层数纪录）。</summary>
        public static void Save()
        {
            var map = RunMap.I;
            if (map == null || map.RunFinished) return;
            try
            {
                var d = new RunData
                {
                    runSeed = map.RunSeed,
                    currentId = map.Current != null ? map.Current.id : 0,
                    crystals = map.Crystals,
                    currentFailed = map.CurrentFailed,
                    bonusStock = map.BonusStock,
                };
                foreach (var row in map.Rows)
                    foreach (var n in row)
                        if (n.cleared) d.clearedIds.Add(n.id);
                if (BuildState.I != null)
                {
                    foreach (var kv in BuildState.I.AllStacks)
                    {
                        d.buildKinds.Add((int)kv.Key);
                        d.buildCounts.Add(kv.Value);
                    }
                }
                S.Set(Key, JsonUtility.ToJson(d, true));
                S.Flush();
                if (MetaProgress.Loaded) MetaProgress.SetBestLayer(map.RunLayer);
            }
            catch (Exception e) { Debug.LogWarning("[Run] 存档写入失败：" + e.Message); }
        }

        /// <summary>恢复中断 Run（含旧 File 档一次性迁移）。成功返回 true。</summary>
        public static bool LoadAndResume()
        {
            var map = RunMap.I;
            if (map == null || !HasResumableRun) return false;
            try
            {
                // 旧 File 档迁移：KV 无档但 File 有 → 迁入
                RunData d;
                if (S.HasKey(Key))
                {
                    d = JsonUtility.FromJson<RunData>(S.Get(Key));
                }
                else
                {
                    string legacy = File.ReadAllText(LegacyFilePath);
                    d = string.IsNullOrEmpty(legacy) ? null : JsonUtility.FromJson<RunData>(legacy);
                    if (d != null) S.Set(Key, legacy);
                    File.Delete(LegacyFilePath);
                }
                if (d == null) return false;
                map.Restore(d);
                if (BuildState.I != null && d.buildKinds != null)
                {
                    var stacks = new List<KeyValuePair<UpgradeKind, int>>();
                    for (int i = 0; i < d.buildKinds.Count; i++)
                        stacks.Add(new KeyValuePair<UpgradeKind, int>((UpgradeKind)d.buildKinds[i], d.buildCounts[i]));
                    BuildState.I.SetStacks(stacks);
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Run] 存档读取失败：" + e.Message);
                return false;
            }
        }

        /// <summary>Run 结束（通关/放弃新开）：删除中断存档。</summary>
        public static void FinishRun()
        {
            try { S.DeleteKey(Key); } catch { }
            try { if (System.IO.File.Exists(LegacyFilePath)) System.IO.File.Delete(LegacyFilePath); } catch { }
        }
    }
}
