#if UNITY_EDITOR
using System.Collections.Generic;
using Codely.Newtonsoft.Json.Linq;
using TJGenerators.Utils;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// 下载完成后的 unitypackage 导入队列（SessionState 持久化），跨 domain reload 保留。
    /// 两级队列：
    /// <para><b>ImportQueue</b>（<see cref="SessionKeyImportQueue"/>）：尚未开始 ImportPackage 的条目。含 .cs 包排队尾等 in-flight 清零，无 .cs 包排队首立即导入。</para>
    /// <para><b>PendingPostImport</b>（<see cref="SessionKeyPendingPostImport"/>）：ImportPackage 已调用、等待后处理（MoveAsset / metadata / MarkCompleted）的条目。</para>
    /// SessionState 键名与字段结构与重构前完全一致，保证重构前后 session 状态可互读。
    /// </summary>
    public static class AssetImportQueue
    {
        private const string SessionKeyImportQueue       = "TJGen_ImportQueue";
        private const string SessionKeyPendingPostImport = "TJGen_PendingPostImport";

        // ---------- Enqueue ----------

        /// <summary>
        /// 加入导入队列：无 .cs 包加到队首（立刻可导入），含 .cs 包加到队尾（等 in-flight == 0）。
        /// </summary>
        public static void Enqueue(AssetImportQueueEntry entry)
        {
            if (entry == null) return;
            var queue = SessionJsonStore.LoadJsonArray(SessionKeyImportQueue);
            var jo = ToJObject(entry);
            if (entry.HasScripts) queue.Add(jo);
            else                  queue.Insert(0, jo);
            SessionJsonStore.SaveJsonArray(SessionKeyImportQueue, queue);
        }

        // ---------- ImportQueue ----------

        /// <summary>读取队首条目；队列为空返回 null。</summary>
        public static AssetImportQueueEntry PeekImportQueue()
        {
            var queue = SessionJsonStore.LoadJsonArray(SessionKeyImportQueue);
            if (queue.Count == 0) return null;
            return FromJObject(queue[0] as JObject);
        }

        /// <summary>丢弃队首条目。</summary>
        public static void DropImportQueueHead()
        {
            var queue = SessionJsonStore.LoadJsonArray(SessionKeyImportQueue);
            if (queue.Count == 0) return;
            queue.RemoveAt(0);
            SessionJsonStore.SaveJsonArray(SessionKeyImportQueue, queue);
        }

        // ---------- PendingPostImport ----------

        /// <summary>将条目追加到 PendingPostImport 队列。</summary>
        public static void PushPending(AssetImportQueueEntry entry)
        {
            if (entry == null) return;
            var pending = SessionJsonStore.LoadJsonArray(SessionKeyPendingPostImport);
            pending.Add(ToJObject(entry));
            SessionJsonStore.SaveJsonArray(SessionKeyPendingPostImport, pending);
        }

        /// <summary>读取所有待后处理条目快照（调用方负责决定保留哪些条目）。</summary>
        public static List<AssetImportQueueEntry> GetPending()
        {
            var pending = SessionJsonStore.LoadJsonArray(SessionKeyPendingPostImport);
            var result = new List<AssetImportQueueEntry>(pending.Count);
            foreach (var token in pending)
            {
                var e = FromJObject(token as JObject);
                if (e != null) result.Add(e);
            }
            return result;
        }

        /// <summary>用给定列表覆盖 PendingPostImport 队列（原子整体替换）。</summary>
        public static void SavePending(List<AssetImportQueueEntry> entries)
        {
            var arr = new JArray();
            if (entries != null)
                foreach (var e in entries)
                    if (e != null) arr.Add(ToJObject(e));
            SessionJsonStore.SaveJsonArray(SessionKeyPendingPostImport, arr);
        }

        /// <summary>判断指定 taskId 是否仍在 PendingPostImport 队列中（用于回调匹配，不依赖包名）。</summary>
        public static bool IsPending(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return false;
            var pending = SessionJsonStore.LoadJsonArray(SessionKeyPendingPostImport);
            foreach (var token in pending)
                if ((token as JObject)?["taskId"]?.ToString() == taskId)
                    return true;
            return false;
        }

        /// <summary>按 taskId 从 PendingPostImport 移除所有匹配条目。</summary>
        public static void RemovePending(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return;
            var pending = SessionJsonStore.LoadJsonArray(SessionKeyPendingPostImport);
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if ((pending[i] as JObject)?["taskId"]?.ToString() == taskId)
                    pending.RemoveAt(i);
            }
            SessionJsonStore.SaveJsonArray(SessionKeyPendingPostImport, pending);
        }

        // ---------- JObject ↔ POCO（字段名与重构前保持一致） ----------

        private static JObject ToJObject(AssetImportQueueEntry entry)
        {
            var files = entry.ImportedFiles ?? new List<string>();
            return new JObject
            {
                ["taskId"]             = entry.TaskId            ?? "",
                ["tempPath"]           = entry.TempPath          ?? "",
                ["prefabPath"]         = entry.PrefabPath        ?? "",
                ["packageDir"]         = entry.PackageDir        ?? "",
                ["importedFiles"]      = JArray.FromObject(files),
                ["hasScripts"]         = entry.HasScripts,
                ["sessionId"]          = entry.SessionId         ?? "",
                ["instantiateInScene"] = entry.InstantiateInScene,
            };
        }

        private static AssetImportQueueEntry FromJObject(JObject o)
        {
            if (o == null) return null;
            var files = new List<string>();
            if (o["importedFiles"] is JArray arr)
                foreach (var t in arr)
                {
                    string s = t?.ToString();
                    if (!string.IsNullOrEmpty(s)) files.Add(s);
                }

            return new AssetImportQueueEntry
            {
                TaskId             = o["taskId"]?.ToString(),
                TempPath           = o["tempPath"]?.ToString(),
                PrefabPath         = o["prefabPath"]?.ToString(),
                PackageDir         = o["packageDir"]?.ToString(),
                ImportedFiles      = files,
                HasScripts         = o["hasScripts"]?.ToObject<bool>()         ?? false,
                SessionId          = o["sessionId"]?.ToString()                ?? "",
                InstantiateInScene = o["instantiateInScene"]?.ToObject<bool>() ?? false,
            };
        }
    }

    /// <summary>
    /// 下载 → 导入流水线中的一条待处理条目。与 SessionState 里持久化的 JObject 字段一一对应。
    /// </summary>
    public sealed class AssetImportQueueEntry
    {
        public string       TaskId;
        public string       TempPath;
        public string       PrefabPath;
        public string       PackageDir;
        public List<string> ImportedFiles = new List<string>();
        public bool         HasScripts;
        public string       SessionId;
        /// <summary>导入完成后是否自动在当前场景中实例化 Prefab。</summary>
        public bool         InstantiateInScene;
    }
}
#endif
