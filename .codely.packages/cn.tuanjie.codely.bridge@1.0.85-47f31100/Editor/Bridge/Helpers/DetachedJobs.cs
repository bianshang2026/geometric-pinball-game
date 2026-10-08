using System;
using System.Collections.Generic;
using System.Linq;
using Codely.Newtonsoft.Json;
using UnityEditor;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Memo store for detached jobs. A detached job does not respond on its original request —
    /// the client early-returns with the job id and polls later. While running, the job sits in
    /// <see cref="_pending"/>; when it finishes, the runner moves it to <see cref="_completed"/>
    /// (holding its response JSON) until the client picks it up via <see cref="Check"/>.
    ///
    /// Entries are held as plain strings and mirrored into SessionState, so they survive a domain
    /// reload — a <see cref="StepJobRunner"/> job outlives the domain it started in, and its result
    /// has to still be collectable afterwards. Jobs from the runtime-only runners do not survive
    /// the reload themselves, but the error outcome recorded when they were cancelled does.
    ///
    /// Thread-safe: a mid-flight detach (see <see cref="JobControl"/>) marks a job pending from
    /// whatever thread requested it, and <c>manage_job.status</c> polls from the background command
    /// executor — including while the main thread is blocked. All state is guarded by one lock and
    /// the TTL clock is <see cref="Environment.TickCount"/> (readable off the main thread, unlike
    /// the editor session clock). SessionState is main-thread only, so mutations just mark the
    /// store dirty; the main thread persists via <see cref="FlushIfDirty"/> (called once per editor
    /// frame and on beforeAssemblyReload). The store must be first touched on the main thread —
    /// guaranteed by the [InitializeOnLoad] bridge/runner initializers — so Load and the reload
    /// hook run there.
    ///
    /// Completed entries that are never collected expire after <see cref="CompletedTtlSeconds"/>
    /// so the store cannot grow unbounded. Nothing survives an editor restart, which clears
    /// SessionState.
    /// </summary>
    public static class DetachedJobs
    {
        public enum Status
        {
            Unknown,   // no such job id (never existed, already collected, or expired)
            Pending,   // still running
            Complete,  // finished — result returned and removed
        }

        private sealed class Completed
        {
            public string Json;    // the response JSON to hand back to the client
            public int    AtTick;  // Environment.TickCount when it finished
        }

        private static readonly object _gate = new object();

        // jobId → job name; guarded by _gate
        private static readonly Dictionary<string, string> _pending = new Dictionary<string, string>();
        private static readonly Dictionary<string, Completed> _completed = new Dictionary<string, Completed>();
        private static bool _dirty;

        private const int CompletedTtlSeconds = 300;
        private const string SessionKeyPending   = "Codely_DetachedJobs_Pending";
        private const string SessionKeyCompleted = "Codely_DetachedJobs_Completed";

        static DetachedJobs()
        {
            Load();
            // Mutations from non-main threads can't persist themselves; make sure whatever is
            // dirty at reload time still gets written before the domain goes down.
            AssemblyReloadEvents.beforeAssemblyReload -= FlushIfDirty;
            AssemblyReloadEvents.beforeAssemblyReload += FlushIfDirty;
        }

        /// <summary>
        /// No-op whose only purpose is forcing the static constructor — whose Load() reads
        /// SessionState, a main-thread-only API — to run on the caller's thread. Call from the
        /// main thread before any background thread (the command pump's worker) can be the
        /// store's first toucher; a background-thread first touch would throw inside Load,
        /// silently starting from an empty store.
        /// </summary>
        internal static void EnsureLoaded() { }

        /// <summary>Records a detached job as running. Called when the runner enrolls it.</summary>
        internal static void MarkPending(JobContext ctx) => MarkPending(ctx.JobId, ctx.Name);

        /// <summary>
        /// Same, keyed directly by id — used for mid-flight detaches (see <see cref="JobControl"/>),
        /// which may run on any thread. If the job's outcome is already stored (the detach lost a
        /// race against completion), the pending marker is skipped so the result is not shadowed.
        /// </summary>
        internal static void MarkPending(string jobId, string name)
        {
            if (string.IsNullOrEmpty(jobId)) return;
            lock (_gate)
            {
                if (_completed.ContainsKey(jobId)) return;
                _pending[jobId] = name;
                _dirty = true;
            }
        }

        /// <summary>Memorizes a finished detached job's outcome until the client collects it.</summary>
        internal static void Store(JobContext ctx) => StoreJson(ctx.JobId, ctx.ToResponseJson());

        /// <summary>
        /// Memorizes an already-serialized outcome. Used by <see cref="StepJobRunner"/>, whose jobs
        /// carry their outcome across domain reloads as JSON rather than as a live object.
        /// </summary>
        internal static void StoreJson(string jobId, string responseJson)
        {
            if (string.IsNullOrEmpty(jobId)) return;

            lock (_gate)
            {
                _pending.Remove(jobId);
                _completed[jobId] = new Completed { Json = responseJson, AtTick = Environment.TickCount };
                PruneLocked();
                _dirty = true;
            }
        }

        /// <summary>
        /// Client poll: looks up a detached job by id. On <see cref="Status.Complete"/> the
        /// response JSON is returned and the entry removed (single collection). Thread-safe —
        /// <c>manage_job.status</c> runs on the background executor.
        /// </summary>
        public static Status Check(string jobId, out string responseJson)
        {
            responseJson = null;
            if (string.IsNullOrEmpty(jobId)) return Status.Unknown;

            lock (_gate)
            {
                PruneLocked();
                if (_completed.TryGetValue(jobId, out var completed))
                {
                    responseJson = completed.Json;
                    _completed.Remove(jobId);
                    _dirty = true;
                    return Status.Complete;
                }
                return _pending.ContainsKey(jobId) ? Status.Pending : Status.Unknown;
            }
        }

        /// <summary>
        /// Fails every pending entry whose job id is not in <paramref name="aliveJobIds"/>. Called
        /// by <see cref="StepJobRunner"/> once per domain init: pending markers persist, but only
        /// step jobs survive a reload, so anything else left pending died with the old domain and
        /// would otherwise poll as <see cref="Status.Pending"/> forever.
        /// </summary>
        internal static void ReconcilePending(ICollection<string> aliveJobIds)
        {
            lock (_gate)
            {
                var orphans = _pending.Where(kv => !aliveJobIds.Contains(kv.Key)).ToList();
                if (orphans.Count == 0) return;

                foreach (var orphan in orphans)
                {
                    _pending.Remove(orphan.Key);
                    _completed[orphan.Key] = new Completed
                    {
                        Json = JsonConvert.SerializeObject(Response.Error(
                            $"Job '{orphan.Value}' did not survive the domain reload.")),
                        AtTick = Environment.TickCount,
                    };
                }
                _dirty = true;
            }
            FlushIfDirty(); // domain init runs on the main thread — persist right away
        }

        // Unchecked subtraction stays correct across TickCount wraparound (~24.9 days).
        private static void PruneLocked()
        {
            if (_completed.Count == 0) return;
            int now = Environment.TickCount;
            var expired = _completed
                .Where(kv => unchecked(now - kv.Value.AtTick) > CompletedTtlSeconds * 1000)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in expired)
                _completed.Remove(key);
        }

        // ---- persistence ---------------------------------------------------- //

        /// <summary>
        /// Persists the store when anything changed since the last flush. Main thread only
        /// (SessionState); called once per editor frame from the command loop and on
        /// beforeAssemblyReload.
        /// </summary>
        public static void FlushIfDirty()
        {
            string pendingJson = null;
            string completedJson = null;
            int pendingCount, completedCount;

            lock (_gate)
            {
                if (!_dirty) return;
                pendingCount = _pending.Count;
                completedCount = _completed.Count;
                try
                {
                    if (pendingCount > 0)   pendingJson   = JsonConvert.SerializeObject(_pending);
                    if (completedCount > 0) completedJson = JsonConvert.SerializeObject(_completed);
                }
                catch (Exception ex)
                {
                    // _dirty stays set so the next flush retries — clearing it here would
                    // silently pin SessionState at a stale snapshot until the next mutation.
                    CodelyLogger.LogWarning($"[DetachedJobs] Failed to serialize jobs: {ex.Message}");
                    return;
                }
                _dirty = false;
            }

            try
            {
                WriteOrErase(SessionKeyPending, pendingCount, pendingJson);
                WriteOrErase(SessionKeyCompleted, completedCount, completedJson);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[DetachedJobs] Failed to persist jobs: {ex.Message}");
                lock (_gate) _dirty = true;   // retry on the next flush
            }
        }

        private static void WriteOrErase(string key, int count, string json)
        {
            if (count == 0) SessionState.EraseString(key);
            else            SessionState.SetString(key, json);
        }

        private static void Load()
        {
            try
            {
                var pending = Read<Dictionary<string, string>>(SessionKeyPending);
                var completed = Read<Dictionary<string, Completed>>(SessionKeyCompleted);
                lock (_gate)
                {
                    if (pending != null)
                        foreach (var kv in pending) _pending[kv.Key] = kv.Value;
                    if (completed != null)
                    {
                        int now = Environment.TickCount;
                        foreach (var kv in completed)
                        {
                            // An entry persisted by an older build (which stamped a different
                            // field) deserializes with AtTick = 0, and TickCount counts from
                            // boot — PruneLocked would expire it instantly, losing a result
                            // that was never collected. Restart its TTL instead.
                            if (kv.Value.AtTick == 0)
                            {
                                kv.Value.AtTick = now;
                                _dirty = true;   // persist the restamp so the TTL survives the next reload
                            }
                            _completed[kv.Key] = kv.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[DetachedJobs] Failed to read persisted jobs: {ex.Message}");
            }
        }

        private static T Read<T>(string key) where T : class
        {
            string json = SessionState.GetString(key, null);
            return string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<T>(json);
        }
    }
}
