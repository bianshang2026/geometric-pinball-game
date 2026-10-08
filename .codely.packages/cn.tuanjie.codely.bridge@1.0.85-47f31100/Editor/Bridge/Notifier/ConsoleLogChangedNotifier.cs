using System;
using System.Collections.Generic;
using System.IO;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Notifier
{
    /// <summary>
    /// Batches new Unity console activity and forwards it to notifier clients as a
    /// "console_log_changed" event (one flush per editor tick).
    ///
    /// Source: <see cref="Application.logMessageReceivedThreaded"/>, which fires for
    /// every console entry regardless of the thread it was raised on — Debug.* calls,
    /// uncaught exceptions, AND C# compiler errors/warnings (Unity routes those through
    /// the log system too; their file/line/column are embedded in the message text,
    /// e.g. "Assets/Foo.cs(12,3): error CS1002: ; expected"). Subscribing to this one
    /// callback covers all of them without duplicates — also hooking
    /// CompilationPipeline.assemblyCompilationFinished would re-deliver every compiler
    /// message a second time.
    ///
    /// The payload mirrors what <see cref="UnityTcp.Editor.Tools.ReadConsole"/>'s
    /// action="get" with types=["all"] returns, i.e.
    /// <see cref="Response.Success(string,object,bool,object)"/> wrapping the entry
    /// array: <c>{ success, message, rev, data: [ { type, message, file, line, stackTrace }, … ] }</c>.
    /// </summary>
    // [InitializeOnLoad] // disable this notifier
    public static class ConsoleLogChangedNotifier
    {
        private const string EventType = "console_log_changed";

        // Safety valve for pathological single-tick bursts (e.g. Debug.Log in a tight
        // loop): never queue more than this many entries per flush.
        private const int MaxEntriesPerFlush = 5000;

        private static readonly object _gate = new object();
        private static readonly List<object> _pending = new List<object>();

        // Re-entrancy guard: a log emitted while we're flushing (e.g. CodelyLogger.LogError
        // on failure) must not be re-queued and re-flushed forever.
        private static volatile bool _flushing;

        static ConsoleLogChangedNotifier()
        {
            Application.logMessageReceivedThreaded -= OnLogMessageThreaded;
            Application.logMessageReceivedThreaded += OnLogMessageThreaded;

            // Drained on the main thread regardless of which thread enqueued.
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        // ---- Source ---------------------------------------------------------- //

        private static void OnLogMessageThreaded(string message, string stackTrace, LogType type)
        {
            if (_flushing) return; // ignore logs caused by our own flush
            Enqueue(MakeEntry(
                type: type.ToString(),
                message: message,
                file: null,
                line: 0,
                stackTrace: string.IsNullOrEmpty(stackTrace) ? null : stackTrace));
        }

        // Entry shape matches ReadConsole's "detailed" formatting exactly:
        //   { type, message, file, line, stackTrace }
        private static Dictionary<string, object> MakeEntry(
            string type, string message, string file, int line, string stackTrace)
        {
            return new Dictionary<string, object>
            {
                { "type", type },
                { "message", message },
                { "file", file },
                { "line", line },
                { "stackTrace", stackTrace },
            };
        }

        // ---- Queue + flush --------------------------------------------------- //

        private static void Enqueue(Dictionary<string, object> entry)
        {
            lock (_gate)
            {
                if (_pending.Count >= MaxEntriesPerFlush) return;
                _pending.Add(entry);
            }
        }

        private static void Tick() => Flush();

        private static void Flush()
        {
            object[] entries;
            lock (_gate)
            {
                if (_pending.Count == 0) return;
                entries = _pending.ToArray();
                _pending.Clear();
            }

            _flushing = true;
            try
            {
                if (!UnityTcpBridge.IsRunning) return; // can't deliver — drop the batch

                // Same shape as ReadConsole.HandleCommand(action="get", types=["all"]):
                //   Response.Success("Retrieved N log entries.", <entries array>)
                object response = Response.Success($"Retrieved {entries.Length} log entries.", entries);
                JObject payload = JObject.FromObject(response);
                UnityTcpBridge.NotifyAll(EventType, payload);
            }
            catch (Exception e)
            {
                try { CodelyLogger.LogError($"[ConsoleLogChangedNotifier] flush failed: {e.Message}"); } catch { }
            }
            finally
            {
                _flushing = false;
            }
        }
    }
}
