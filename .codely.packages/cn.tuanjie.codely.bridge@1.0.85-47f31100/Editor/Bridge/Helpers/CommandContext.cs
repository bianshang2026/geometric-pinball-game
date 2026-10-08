using System;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Ambient context for the command currently being dispatched. The dispatching loop sets it
    /// right before a handler runs and clears it afterwards, so a <c>HandleCommand(JObject)</c>
    /// can read the native request id (to start a runner job) without changing its prototype.
    ///
    /// Commands are dispatched serially per thread — the editor main loop and the
    /// <see cref="BackgroundCommandPump"/> worker each set/clear around a handler — so the
    /// backing fields are thread-local to keep the two dispatchers from seeing each other's
    /// context.
    /// </summary>
    public static class CommandContext
    {
        [ThreadStatic] private static ulong  _requestId;
        [ThreadStatic] private static string _commandType;

        /// <summary>Native request id of the command being handled (for EnqueueResponse).</summary>
        public static ulong RequestId => _requestId;

        /// <summary>Command type of the command being handled (usable as a job name).</summary>
        public static string CommandType => _commandType;

        public static void Set(ulong requestId, string commandType)
        {
            _requestId = requestId;
            _commandType = commandType;
        }

        public static void Clear()
        {
            _requestId = 0;
            _commandType = null;
        }
    }
}
