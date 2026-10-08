using System;
using System.Collections.Generic;
using Codely.Newtonsoft.Json.Linq;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Shared action dispatch for Unity tool handlers: read/normalize <c>action</c>,
    /// optional alias remap, unknown-action error, and try/catch around the handler.
    /// </summary>
    public static class ActionRouter
    {
        public static bool TryResolve(
            JObject @params,
            IEnumerable<string> validActions,
            out string action,
            out object error,
            IReadOnlyDictionary<string, string> aliases = null,
            string defaultAction = null)
        {
            action = @params?["action"]?.ToString()?.ToLower();
            if (string.IsNullOrEmpty(action))
            {
                action = defaultAction;
            }

            if (string.IsNullOrEmpty(action))
            {
                error = Response.Error("Action parameter is required.");
                return false;
            }

            if (aliases != null && aliases.TryGetValue(action, out var canonical))
            {
                action = canonical;
            }

            if (validActions != null)
            {
                bool known = false;
                foreach (var valid in validActions)
                {
                    if (valid == action)
                    {
                        known = true;
                        break;
                    }
                }

                if (!known)
                {
                    error = Response.Error(
                        $"Unknown action: '{action}'. Valid actions are: {string.Join(", ", validActions)}"
                    );
                    return false;
                }
            }

            error = null;
            return true;
        }

        public static object Route(
            JObject @params,
            IReadOnlyDictionary<string, Func<JObject, object>> handlers,
            IReadOnlyDictionary<string, string> aliases = null,
            string defaultAction = null)
        {
            if (!TryResolve(@params, handlers.Keys, out var action, out var error, aliases, defaultAction))
            {
                return error;
            }

            try
            {
                return handlers[action](@params);
            }
            catch (Exception e)
            {
                return Response.Error(
                    $"Internal error processing action '{action}': {e.Message}"
                );
            }
        }
    }
}
