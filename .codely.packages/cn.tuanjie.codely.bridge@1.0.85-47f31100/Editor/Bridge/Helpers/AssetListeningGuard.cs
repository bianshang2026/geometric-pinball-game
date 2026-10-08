using System;
using UnityEditor;
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Project-level manual toggle for Unity's automatic asset listening (the
    /// "pause auto refresh" switch in the Cowork chat input bar).
    ///
    /// Pausing does exactly one thing: block the focus-driven AutoRefresh
    /// disk scan via the native latch (AssetDatabase.DisallowAutoRefresh).
    /// Disk changes accumulate, and resuming releases the latch and schedules
    /// one full scan (AllowAutoRefresh + a next-frame AssetDatabase.Refresh())
    /// that imports/compiles everything written while paused. Tool-invoked
    /// refreshes and imports (manage_asset, manage_scene,
    /// dirty-hook batches, ghost-heal, request_compile, ...) are NOT
    /// intercepted and run normally whether or not listening is paused.
    ///
    /// The paused state is a plain bool in SessionState: it survives domain
    /// reloads and resets when the editor closes (auto refresh restored —
    /// the safe default). The native latch counter shares that lifecycle, so
    /// no re-arming is needed after a reload. <see cref="SetPaused"/> is
    /// idempotent.
    /// </summary>
    public static class AssetListeningGuard
    {
        private const string PausedKey = "Codely.AssetListeningGuard.Paused";

        public static bool IsPaused
        {
            get => SessionState.GetBool(PausedKey, false);
            private set => SessionState.SetBool(PausedKey, value);
        }

        /// <summary>
        /// Idempotent project-level toggle. Pausing blocks focus-driven
        /// AutoRefresh; resuming releases the latch and schedules one full
        /// scan that imports/compiles everything written while paused.
        /// </summary>
        public static void SetPaused(bool paused)
        {
            if (paused == IsPaused)
            {
                return;
            }

            IsPaused = paused;

            if (paused)
            {
                AssetDatabase.DisallowAutoRefresh();
                CodelyLogger.Log(
                    "[AssetListeningGuard] Asset listening paused for this project: " +
                    "focus-driven AutoRefresh is blocked until the toggle is switched off.");
            }
            else
            {
                AssetDatabase.AllowAutoRefresh();
                // AssetDatabase.ScheduleRefresh() is removed in u6, so defer
                // the full scan to the next editor frame ourselves.
                EditorApplication.delayCall += () => AssetDatabase.Refresh();
                CodelyLogger.Log(
                    "[AssetListeningGuard] Asset listening resumed: latch released, " +
                    "full scan scheduled for accumulated changes.");
            }
        }

        /// <summary>
        /// Snapshot for the _internal_asset_listening set/status responses.
        /// </summary>
        public static object GetState()
        {
            return new
            {
                paused = IsPaused,
            };
        }
    }
}
