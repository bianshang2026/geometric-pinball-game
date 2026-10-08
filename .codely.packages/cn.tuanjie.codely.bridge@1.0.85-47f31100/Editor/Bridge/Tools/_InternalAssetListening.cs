using System;
using System.Collections.Generic;
using Codely.Newtonsoft.Json.Linq;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// INTERNAL TOOL - NOT EXPOSED TO LLM.
    /// Project-level manual toggle for Unity's automatic asset listening (the
    /// Cowork chat input bar switch). While paused, ONLY the focus-driven
    /// AutoRefresh scan is blocked (native latch); tool-invoked
    /// refreshes/imports run normally. Resuming releases the latch and
    /// schedules ONE full scan so everything written while paused is
    /// imported and compiled in a single batch.
    ///
    /// This tool should only be called by the agent execution layer (the
    /// CLI's asset_listening extension methods), not directly by LLM tool
    /// invocations.
    /// </summary>
    public static class _InternalAssetListening
    {
        private static readonly Dictionary<string, Func<JObject, object>> ActionHandlers =
            new Dictionary<string, Func<JObject, object>>
            {
                { "set", Set },
                { "status", _ => GetStatus() },
            };

        public static object HandleCommand(JObject @params)
            => ActionRouter.Route(@params, ActionHandlers);

        private static object Set(JObject @params)
        {
            try
            {
                bool paused = @params.Value<bool?>("paused") == true;
                AssetListeningGuard.SetPaused(paused);

                // Broadcast the new state so every connected client — and
                // every Cowork window behind them (external console and the
                // editor-embedded window alike) — can sync its toggle.
                UnityTcpBridge.NotifyAll(
                    "asset_listening_state",
                    new JObject { ["paused"] = AssetListeningGuard.IsPaused }
                );

                return Response.Success(
                    "[INTERNAL] Asset listening set.",
                    AssetListeningGuard.GetState()
                );
            }
            catch (Exception e)
            {
                return Response.Error($"[INTERNAL] Failed to set asset listening: {e.Message}");
            }
        }

        private static object GetStatus()
        {
            try
            {
                return Response.Success(
                    "[INTERNAL] Asset listening state.",
                    AssetListeningGuard.GetState()
                );
            }
            catch (Exception e)
            {
                return Response.Error($"[INTERNAL] Failed to read asset listening state: {e.Message}");
            }
        }
    }
}
