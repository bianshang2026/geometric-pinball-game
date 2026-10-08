using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityTcp.Editor.Helpers; // For Response class

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Handles reading and clearing Unity Editor console log entries.
    /// Uses reflection to access internal LogEntry methods/properties.
    /// To read only the output of an operation, clear the console first and read after.
    /// </summary>
    public static class ReadConsole
    {
        private const int MaxConsoleEntries = 1000;

        // Reflection members for accessing internal LogEntry data
        // private static MethodInfo _getEntriesMethod; // Removed as it's unused and fails reflection
        private static MethodInfo _startGettingEntriesMethod;
        private static MethodInfo _endGettingEntriesMethod; // Renamed from _stopGettingEntriesMethod, trying End...
        private static MethodInfo _clearMethod;
        private static MethodInfo _getCountMethod;
        private static MethodInfo _getEntryCountMethod;
        private static MethodInfo _getEntryMethod;
        private static PropertyInfo _consoleFlagsProperty;
        private static MethodInfo _setConsoleFlagMethod;
        private static MethodInfo _setFilteringTextMethod;
        private static MethodInfo _getFilteringTextMethod;
        private static FieldInfo _modeField;
        private static FieldInfo _messageField;
        private static FieldInfo _fileField;
        private static FieldInfo _lineField;
        private static FieldInfo _instanceIdField;

        // Note: Timestamp is not directly available in LogEntry; need to parse message or find alternative?

        private const int ConsoleFlagCollapse = 1 << 0;
        private const int ConsoleFlagLogLevelLog = 1 << 7;
        private const int ConsoleFlagLogLevelWarning = 1 << 8;
        private const int ConsoleFlagLogLevelError = 1 << 9;

        // Static constructor for reflection setup
        static ReadConsole()
        {
            try
            {
                Type logEntriesType = typeof(EditorApplication).Assembly.GetType(
                    "UnityEditor.LogEntries"
                );
                if (logEntriesType == null)
                    throw new Exception("Could not find internal type UnityEditor.LogEntries");
                
                

                // Include NonPublic binding flags as internal APIs might change accessibility
                BindingFlags staticFlags =
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                BindingFlags instanceFlags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _startGettingEntriesMethod = logEntriesType.GetMethod(
                    "StartGettingEntries",
                    staticFlags
                );
                if (_startGettingEntriesMethod == null)
                    throw new Exception("Failed to reflect LogEntries.StartGettingEntries");

                // Try reflecting EndGettingEntries based on warning message
                _endGettingEntriesMethod = logEntriesType.GetMethod(
                    "EndGettingEntries",
                    staticFlags
                );
                if (_endGettingEntriesMethod == null)
                    throw new Exception("Failed to reflect LogEntries.EndGettingEntries");

                _clearMethod = logEntriesType.GetMethod("Clear", staticFlags);
                if (_clearMethod == null)
                    throw new Exception("Failed to reflect LogEntries.Clear");

                _getCountMethod = logEntriesType.GetMethod("GetCount", staticFlags);
                if (_getCountMethod == null)
                    throw new Exception("Failed to reflect LogEntries.GetCount");

                _getEntryCountMethod = logEntriesType.GetMethod(
                    "GetEntryCount",
                    staticFlags,
                    null,
                    new[] { typeof(int) },
                    null
                );

                _getEntryMethod = logEntriesType.GetMethod("GetEntryInternal", staticFlags);
                if (_getEntryMethod == null)
                    throw new Exception("Failed to reflect LogEntries.GetEntryInternal");

                // Optional: bypass Console toolbar filters (severity / Collapse / search).
                _consoleFlagsProperty = logEntriesType.GetProperty("consoleFlags", staticFlags);
                _setConsoleFlagMethod = logEntriesType.GetMethod(
                    "SetConsoleFlag",
                    staticFlags,
                    null,
                    new[] { typeof(int), typeof(bool) },
                    null
                );
                _setFilteringTextMethod = logEntriesType.GetMethod(
                    "SetFilteringText",
                    staticFlags,
                    null,
                    new[] { typeof(string) },
                    null
                );
                _getFilteringTextMethod = logEntriesType.GetMethod(
                    "GetFilteringText",
                    staticFlags,
                    null,
                    Type.EmptyTypes,
                    null
                );

                Type logEntryType = typeof(EditorApplication).Assembly.GetType(
                    "UnityEditor.LogEntry"
                );
                if (logEntryType == null)
                    throw new Exception("Could not find internal type UnityEditor.LogEntry");

                _modeField = logEntryType.GetField("mode", instanceFlags);
                if (_modeField == null)
                    throw new Exception("Failed to reflect LogEntry.mode");

                _messageField = logEntryType.GetField("message", instanceFlags);
                if (_messageField == null)
                    throw new Exception("Failed to reflect LogEntry.message");

                _fileField = logEntryType.GetField("file", instanceFlags);
                if (_fileField == null)
                    throw new Exception("Failed to reflect LogEntry.file");

                _lineField = logEntryType.GetField("line", instanceFlags);
                if (_lineField == null)
                    throw new Exception("Failed to reflect LogEntry.line");

                _instanceIdField = logEntryType.GetField("instanceID", instanceFlags)
                    ?? logEntryType.GetField("entityId", instanceFlags);
                // Optional: log entry instance id is not used when reading messages.
                
                // (Calibration removed)
                
            }
            catch (Exception e)
            {
                CodelyLogger.LogError(
                    $"[ReadConsole] Static Initialization Failed: Could not setup reflection for LogEntries/LogEntry. Console reading/clearing will likely fail. Specific Error: {e.Message}"
                );
                // Set members to null to prevent NullReferenceExceptions later, HandleCommand should check this.
                _startGettingEntriesMethod =
                    _endGettingEntriesMethod =
                    _clearMethod =
                    _getCountMethod =
                    _getEntryMethod =
                        null;
                _getEntryCountMethod = null;
                _consoleFlagsProperty = null;
                _setConsoleFlagMethod = null;
                _setFilteringTextMethod = null;
                _getFilteringTextMethod = null;
                _modeField = _messageField = _fileField = _lineField = _instanceIdField = null;
            }
        }

        // --- Main Handler ---

        public static object HandleCommand(JObject @params)
        {
            // Check if ALL required reflection members were successfully initialized.
            if (
                _startGettingEntriesMethod == null
                || _endGettingEntriesMethod == null
                || _clearMethod == null
                || _getCountMethod == null
                || _getEntryMethod == null
                || _modeField == null
                || _messageField == null
                || _fileField == null
                || _lineField == null
            )
            {
                // Log the error here as well for easier debugging in Unity Console
                CodelyLogger.LogError(
                    "[ReadConsole] HandleCommand called but reflection members are not initialized. Static constructor might have failed silently or there's an issue."
                );
                string requestedAction = (@params?["action"]?.ToString() ?? "get").ToLowerInvariant();
                object initializationError = Response.Error(
                    "ReadConsole handler failed to initialize due to reflection errors. Cannot access console logs."
                );
                return requestedAction == "get" || requestedAction == "set_collapse"
                    ? AddCollapseStatus(initializationError, ReadCollapseState())
                    : initializationError;
            }

            return ActionRouter.Route(@params, ActionHandlers, defaultAction: "get");
        }

        private static readonly Dictionary<string, Func<JObject, object>> ActionHandlers =
            new Dictionary<string, Func<JObject, object>>
            {
                { "clear", ClearAction },
                { "get", GetAction },
                { "set_collapse", SetCollapseAction },
            };

        private static object ClearAction(JObject @params)
        {
            string scope = (@params["scope"]?.ToString() ?? "all").ToLower();
            return ClearConsole(scope);
        }

        private static object GetAction(JObject @params)
        {
            try
            {
                var types =
                    (@params["types"] as JArray)?.Select(t => t.ToString().ToLower()).ToList()
                    ?? new List<string> { "error", "warning", "log" };
                int? count = @params["count"]?.ToObject<int?>();
                string filterText = @params["filterText"]?.ToString();
                string sinceTimestampStr = @params["sinceTimestamp"]?.ToString();
                string format = (@params["format"]?.ToString() ?? "detailed").ToLower();
                bool includeStacktrace =
                    @params["includeStacktrace"]?.ToObject<bool?>() ?? true;

                if (types.Contains("all"))
                {
                    types = new List<string> { "error", "warning", "log" };
                }

                if (!string.IsNullOrEmpty(sinceTimestampStr))
                {
                    CodelyLogger.LogWarning(
                        "[ReadConsole] Filtering by 'since_timestamp' is not currently implemented."
                    );
                }

                bool? effectiveCollapse = ReadCollapseState();
                if (!effectiveCollapse.HasValue)
                {
                    return AddCollapseStatus(
                        Response.Error(
                            "Console collapse state is unavailable because UnityEditor.LogEntries.consoleFlags could not be reflected."
                        ),
                        null
                    );
                }

                object response = GetConsoleEntries(
                    types,
                    count,
                    filterText,
                    format,
                    includeStacktrace,
                    effectiveCollapse.Value
                );
                return AddCollapseStatus(response, effectiveCollapse.Value);
            }
            catch (Exception e)
            {
                return AddCollapseStatus(Response.Error(e.Message), ReadCollapseState());
            }
        }

        private static object SetCollapseAction(JObject @params)
        {
            bool? current = ReadCollapseState();
            JToken token = @params["collapse"];
            if (token == null || token.Type != JTokenType.Boolean)
            {
                return AddCollapseStatus(
                    Response.Error("collapse must be true or false for set_collapse action"),
                    current
                );
            }

            try
            {
                bool effective = SetCollapseState(token.ToObject<bool>());
                return AddCollapseStatus(
                    Response.Success(effective ? "Console collapse enabled." : "Console collapse disabled."),
                    effective
                );
            }
            catch (Exception e)
            {
                return AddCollapseStatus(Response.Error(e.Message), ReadCollapseState());
            }
        }

        /// <summary>
        /// Reads the effective post-compile Console view for
        /// <c>start_compilation_pipeline</c>. The result intentionally respects the
        /// persistent Unity Console Collapse state: collapsed entries represent groups
        /// and include occurrence counts. Keeping this adapter here ensures the
        /// standalone Console tool and compilation pipeline use the same filtering,
        /// formatting, truncation, Collapse, and full-log-file behavior.
        /// </summary>
        internal static JObject ReadForCompilationPipeline()
        {
            object raw = HandleCommand(new JObject
            {
                ["action"] = "get",
                ["types"] = new JArray("all"),
                ["format"] = "detailed",
                ["includeStacktrace"] = true,
            });

            JObject response;
            try
            {
                response = raw as JObject ?? JObject.FromObject(raw);
            }
            catch (Exception e)
            {
                return new JObject
                {
                    ["included"] = true,
                    ["cleared"] = true,
                    ["read_success"] = false,
                    ["entries"] = new JArray(),
                    ["error"] = $"Failed to serialize console response: {e.Message}",
                };
            }

            bool readSuccess = response?["success"]?.ToObject<bool?>() == true;
            var result = new JObject
            {
                ["included"] = true,
                ["cleared"] = true,
                ["read_success"] = readSuccess,
                ["entries"] = response?["data"] is JArray entries ? entries : new JArray(),
                ["truncated"] = response?["truncated"]?.ToObject<bool?>() ?? false,
            };

            CopyIfPresent(response, result, "totalCount");
            CopyIfPresent(response, result, "returnedCount");
            CopyIfPresent(response, result, "fullLogFile");
            CopyIfPresent(response, result, "collapse");

            if (!readSuccess)
            {
                result["error"] =
                    response?["message"]?.ToString()
                    ?? response?["error"]?.ToString()
                    ?? "Failed to read the Unity console after compilation.";
            }

            return result;
        }

        private static void CopyIfPresent(JObject source, JObject destination, string name)
        {
            if (source?[name] != null)
                destination[name] = source[name].DeepClone();
        }

        // --- Action Implementations ---

        /// <summary>
        /// Clears the console with optional scope.
        /// </summary>
        /// <param name="scope">"all" clears everything (default), "errors_only" clears only error messages</param>
        private static object ClearConsole(string scope = "all")
        {
            try
            {
                if (scope == "errors_only")
                {
                    // Note: Unity's LogEntries.Clear() clears everything.
                    // For errors_only, we would need to iterate and selectively remove,
                    // which is not directly supported by Unity's API.
                    // For now, we log a warning and clear all (future enhancement could filter).
                    CodelyLogger.LogWarning("[ReadConsole] 'errors_only' scope is not fully supported by Unity's internal API. Clearing all messages.");
                }
                
                _clearMethod.Invoke(null, null); // Static method, no instance, no parameters

                StateComposer.UpdateConsoleState(0, new object[0]);

                return new
                {
                    success = true,
                    message = scope == "errors_only"
                        ? "Console cleared (errors_only scope limited by Unity API - all messages cleared)."
                        : "Console cleared successfully.",
                    data = new
                    {
                        scope = scope
                    }
                };
            }
            catch (Exception e)
            {
                CodelyLogger.LogError($"[ReadConsole] Failed to clear console: {e}");
                return Response.Error($"Failed to clear console: {e.Message}");
            }
        }

        private static object[] ExtractErrorsFromList(System.Collections.IList list)
        {
            var errors = new List<object>();

            if (list == null) return errors.ToArray();

            foreach (var entry in list)
            {
                var entryType = entry?.GetType();
                if (entryType == null) continue;

                var typeProp = entryType.GetProperty("type");
                var typeValue = typeProp?.GetValue(entry)?.ToString();
                if (typeValue == "Error" || typeValue == "Exception")
                {
                    var messageProp = entryType.GetProperty("message");
                    var fileProp = entryType.GetProperty("file");
                    var lineProp = entryType.GetProperty("line");

                    errors.Add(new
                    {
                        message = messageProp?.GetValue(entry)?.ToString(),
                        file = fileProp?.GetValue(entry)?.ToString(),
                        line = lineProp?.GetValue(entry)
                    });
                }
            }

            return errors.Take(10).ToArray();
        }

        private static object GetConsoleEntries(
            List<string> types,
            int? count,
            string filterText,
            string format,
            bool includeStacktrace,
            bool effectiveCollapse
        )
        {
            return GetConsoleEntriesFromIndex(
                0,
                types,
                count,
                filterText,
                format,
                includeStacktrace,
                effectiveCollapse
            );
        }
        
        /// <summary>
        /// Gets console entries starting from a specific index.
        /// </summary>
        private static object GetConsoleEntriesFromIndex(
            int startIndex,
            List<string> types,
            int? count,
            string filterText,
            string format,
            bool includeStacktrace,
            bool effectiveCollapse
        )
        {
            if (effectiveCollapse && _getEntryCountMethod == null)
            {
                return Response.Error(
                    "Console collapse counts are unavailable because UnityEditor.LogEntries.GetEntryCount could not be reflected."
                );
            }

            List<object> formattedEntries = new List<object>();

            SafeEndGettingEntries();
            using (BeginConsoleUiBypass())
            {
                try
                {
                    object started = _startGettingEntriesMethod.Invoke(null, null);
                    int totalEntries = started is int n ? n : (int)_getCountMethod.Invoke(null, null);

                    Type logEntryType = typeof(EditorApplication).Assembly.GetType(
                        "UnityEditor.LogEntry"
                    );
                    if (logEntryType == null)
                        throw new Exception(
                            "Could not find internal type UnityEditor.LogEntry during GetConsoleEntries."
                        );
                    object logEntryInstance = Activator.CreateInstance(logEntryType);

                    if (startIndex < 0) startIndex = 0;
                    if (startIndex > totalEntries) startIndex = totalEntries;

                    for (int i = startIndex; i < totalEntries; i++)
                    {
                        object[] getArgs = { i, logEntryInstance };
                        if (_getEntryMethod.Invoke(null, getArgs) is bool ok && !ok)
                            continue;
                        logEntryInstance = getArgs[1];

                        int mode = (int)_modeField.GetValue(logEntryInstance);
                        string message = (string)_messageField.GetValue(logEntryInstance);
                        string file = (string)_fileField.GetValue(logEntryInstance);
                        int line = (int)_lineField.GetValue(logEntryInstance);

                        if (string.IsNullOrEmpty(message))
                            continue;

                        LogType unityType = InferTypeFromMessage(message);
                        bool isExplicitDebug = IsExplicitDebugLog(message);
                        if (!isExplicitDebug && unityType == LogType.Log)
                            unityType = GetLogTypeFromMode(mode);

                        bool want;
                        if (unityType == LogType.Exception)
                            want = types.Contains("error") || types.Contains("exception");
                        else if (unityType == LogType.Assert)
                            want = types.Contains("error") || types.Contains("assert");
                        else
                            want = types.Contains(unityType.ToString().ToLowerInvariant());

                        if (!want) continue;

                        if (
                            !string.IsNullOrEmpty(filterText)
                            && message.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) < 0
                        )
                            continue;

                        // LogEntry.message embeds the callstack in the text itself, so the
                        // stack must always be split off; includeStacktrace only decides
                        // whether the split-off part is returned in the stackTrace field.
                        // IMPORTANT: the message body may itself be multi-line — take every
                        // line before the stack start, not only the first line.
                        SplitMessageAndStack(message, out string messageOnly, out string embeddedStack);
                        string stackTrace = includeStacktrace ? embeddedStack : null;
                        int occurrenceCount = effectiveCollapse
                            ? (int)_getEntryCountMethod.Invoke(null, new object[] { i })
                            : 1;

                        object formattedEntry;
                        switch (format)
                        {
                            case "plain":
                                formattedEntry = effectiveCollapse
                                    ? $"[x{occurrenceCount}] {messageOnly}"
                                    : messageOnly;
                                break;
                            case "json":
                            case "detailed":
                            default:
                                formattedEntry = effectiveCollapse
                                    ? (object)new
                                    {
                                        type = unityType.ToString(),
                                        message = messageOnly,
                                        file = file,
                                        line = line,
                                        stackTrace = stackTrace,
                                        count = occurrenceCount,
                                    }
                                    : new
                                    {
                                        type = unityType.ToString(),
                                        message = messageOnly,
                                        file = file,
                                        line = line,
                                        stackTrace = stackTrace,
                                    };
                                break;
                        }

                        formattedEntries.Add(formattedEntry);
                    }
                }
                catch (Exception e)
                {
                    CodelyLogger.LogError($"[ReadConsole] Error while retrieving log entries: {e}");
                    SafeEndGettingEntries();
                    return Response.Error($"Error retrieving log entries: {e.Message}");
                }
                finally
                {
                    SafeEndGettingEntries();
                }
            }

            // Keep the composed editor state's console section fresh on every read.
            StateComposer.UpdateConsoleState(formattedEntries.Count, ExtractErrorsFromList(formattedEntries));

            int totalCount = formattedEntries.Count;
            int returnLimit = count.HasValue ? Math.Min(count.Value, MaxConsoleEntries) : MaxConsoleEntries;

            if (totalCount > returnLimit)
            {
                string tempFilePath = WriteFullLogToTempFile(formattedEntries);
                var truncatedEntries = formattedEntries.Take(returnLimit).ToList();

                return new
                {
                    success = true,
                    message = effectiveCollapse
                        ? $"Console has {totalCount} collapsed log groups, showing first {returnLimit}. Full log saved to temp file."
                        : $"Console has {totalCount} entries, showing first {returnLimit}. Full log saved to temp file.",
                    data = truncatedEntries,
                    truncated = true,
                    totalCount = totalCount,
                    returnedCount = truncatedEntries.Count,
                    fullLogFile = tempFilePath,
                    hint = $"To read the complete log, use the file reading tool on: {tempFilePath}"
                };
            }

            var finalEntries = count.HasValue ? formattedEntries.Take(count.Value).ToList() : formattedEntries;
            string successMessage = effectiveCollapse
                ? $"Retrieved collapsed log groups: {finalEntries.Count}."
                : $"Retrieved {finalEntries.Count} log entries.";
            return Response.Success(successMessage, finalEntries);
        }

        private sealed class ConsoleUiBypassRestore : IDisposable
        {
            private readonly int _savedFlags;
            private readonly string _savedFilter;

            public ConsoleUiBypassRestore(int savedFlags, string savedFilter)
            {
                _savedFlags = savedFlags;
                _savedFilter = savedFilter;
            }

            public void Dispose()
            {
                SafeEndGettingEntries();
                if (_setFilteringTextMethod != null)
                {
                    try { _setFilteringTextMethod.Invoke(null, new object[] { _savedFilter ?? string.Empty }); }
                    catch { /* ignore */ }
                }
                if (_consoleFlagsProperty != null)
                {
                    try { _consoleFlagsProperty.SetValue(null, _savedFlags, null); }
                    catch (Exception e)
                    {
                        CodelyLogger.LogError($"[ReadConsole] Failed to restore consoleFlags: {e.Message}");
                    }
                }
            }
        }

        private static int ReadConsoleFlags()
        {
            if (_consoleFlagsProperty == null) return 0;
            return (int)_consoleFlagsProperty.GetValue(null, null);
        }

        private static bool? ReadCollapseState()
        {
            if (_consoleFlagsProperty == null) return null;
            try
            {
                return (ReadConsoleFlags() & ConsoleFlagCollapse) != 0;
            }
            catch
            {
                return null;
            }
        }

        private static bool SetCollapseState(bool collapse)
        {
            if (_setConsoleFlagMethod == null || _consoleFlagsProperty == null)
                throw new InvalidOperationException(
                    "Console collapse is unavailable because the required UnityEditor.LogEntries APIs could not be reflected."
                );

            SafeEndGettingEntries();
            _setConsoleFlagMethod.Invoke(null, new object[] { ConsoleFlagCollapse, collapse });
            bool? effective = ReadCollapseState();
            if (!effective.HasValue)
                throw new InvalidOperationException("Failed to read Console collapse state after setting it.");
            return effective.Value;
        }

        private static JObject AddCollapseStatus(object response, bool? collapse)
        {
            JObject result = response as JObject ?? JObject.FromObject(response);
            result["collapse"] = collapse.HasValue
                ? JToken.FromObject(collapse.Value)
                : JValue.CreateNull();
            return result;
        }

        private static ConsoleUiBypassRestore BeginConsoleUiBypass()
        {
            int savedFlags = ReadConsoleFlags();
            string savedFilter = _getFilteringTextMethod?.Invoke(null, null) as string;
            var restore = new ConsoleUiBypassRestore(savedFlags, savedFilter);
            if (_setConsoleFlagMethod == null)
                return restore;

            try
            {
                SafeEndGettingEntries();
                _setConsoleFlagMethod.Invoke(null, new object[] { ConsoleFlagLogLevelLog, true });
                _setConsoleFlagMethod.Invoke(null, new object[] { ConsoleFlagLogLevelWarning, true });
                _setConsoleFlagMethod.Invoke(null, new object[] { ConsoleFlagLogLevelError, true });
                if (_setFilteringTextMethod != null)
                    _setFilteringTextMethod.Invoke(null, new object[] { string.Empty });

                return restore;
            }
            catch
            {
                restore.Dispose();
                throw;
            }
        }

        private static void SafeEndGettingEntries()
        {
            if (_endGettingEntriesMethod == null) return;
            try { _endGettingEntriesMethod.Invoke(null, null); }
            catch { /* ignore */ }
        }

        // --- Internal Helpers ---

        // Mapping bits from LogEntry.mode. These may vary by Unity version.
        private const int ModeBitError = 1 << 0;
        private const int ModeBitAssert = 1 << 1;
        private const int ModeBitWarning = 1 << 2;
        private const int ModeBitLog = 1 << 3;
        private const int ModeBitException = 1 << 4; // often combined with Error bits
        private const int ModeBitScriptingError = 1 << 9;
        private const int ModeBitScriptingWarning = 1 << 10;
        private const int ModeBitScriptingLog = 1 << 11;
        private const int ModeBitScriptingException = 1 << 18;
        private const int ModeBitScriptingAssertion = 1 << 22;

        private static LogType GetLogTypeFromMode(int mode)
        {
            if ((mode & (ModeBitException | ModeBitScriptingException)) != 0) return LogType.Exception;
            if ((mode & (ModeBitError | ModeBitScriptingError)) != 0) return LogType.Error;
            if ((mode & (ModeBitAssert | ModeBitScriptingAssertion)) != 0) return LogType.Assert;
            if ((mode & (ModeBitWarning | ModeBitScriptingWarning)) != 0) return LogType.Warning;
            return LogType.Log;
        }

        // (Calibration helpers removed)

        /// <summary>
        /// Classifies severity using message/stacktrace content. Works across Unity versions.
        /// </summary>
        private static LogType InferTypeFromMessage(string fullMessage)
        {
            if (string.IsNullOrEmpty(fullMessage)) return LogType.Log;

            // Fast path: look for explicit Debug API names in the appended stack trace
            // e.g., "UnityEngine.Debug:LogError (object)" or "LogWarning"
            if (fullMessage.IndexOf("LogError", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Error;
            if (fullMessage.IndexOf("LogWarning", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Warning;

            // Compiler diagnostics (C#): "warning CSxxxx" / "error CSxxxx"
            if (fullMessage.IndexOf(" warning CS", StringComparison.OrdinalIgnoreCase) >= 0
                || fullMessage.IndexOf(": warning CS", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Warning;
            if (fullMessage.IndexOf(" error CS", StringComparison.OrdinalIgnoreCase) >= 0
                || fullMessage.IndexOf(": error CS", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Error;

            // Exceptions (avoid misclassifying compiler diagnostics)
            if (fullMessage.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Exception;

            // Unity assertions
            if (fullMessage.IndexOf("Assertion", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Assert;

            return LogType.Log;
        }

        private static bool IsExplicitDebugLog(string fullMessage)
        {
            if (string.IsNullOrEmpty(fullMessage)) return false;
            if (fullMessage.IndexOf("Debug:Log (", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fullMessage.IndexOf("UnityEngine.Debug:Log (", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Applies the "one level lower" remapping for filtering, like the old version.
        /// This ensures compatibility with the filtering logic that expects remapped types.
        /// </summary>
        private static LogType GetRemappedTypeForFiltering(LogType unityType)
        {
            switch (unityType)
            {
                case LogType.Error:
                    return LogType.Warning; // Error becomes Warning
                case LogType.Warning:
                    return LogType.Log; // Warning becomes Log
                case LogType.Assert:
                    return LogType.Assert; // Assert remains Assert
                case LogType.Log:
                    return LogType.Log; // Log remains Log
                case LogType.Exception:
                    return LogType.Warning; // Exception becomes Warning
                default:
                    return LogType.Log; // Default fallback
            }
        }

        private static string WriteFullLogToTempFile(List<object> entries)
        {
            try
            {
                string tempDir = Path.Combine(Application.dataPath, "..", "Temp");
                Directory.CreateDirectory(tempDir);
                string fileName = $"unity_console_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.json";
                string filePath = Path.GetFullPath(Path.Combine(tempDir, fileName));

                var jArray = new JArray();
                foreach (var entry in entries)
                {
                    if (entry is string s)
                    {
                        jArray.Add(s);
                    }
                    else
                    {
                        jArray.Add(JObject.FromObject(entry));
                    }
                }

                File.WriteAllText(filePath, jArray.ToString(Codely.Newtonsoft.Json.Formatting.Indented), System.Text.Encoding.UTF8);
                return filePath;
            }
            catch (Exception e)
            {
                CodelyLogger.LogError($"[ReadConsole] Failed to write full log to temp file: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Splits a Unity LogEntry.message into the log body (which may itself be multi-line)
        /// and the appended callstack. Previously only the first line was kept as the message,
        /// which truncated multi-line log bodies whenever a stack was present.
        /// </summary>
        internal static void SplitMessageAndStack(
            string fullMessage,
            out string messageBody,
            out string stackTrace
        )
        {
            messageBody = fullMessage ?? "";
            stackTrace = null;
            if (string.IsNullOrEmpty(fullMessage))
                return;

            string[] lines = fullMessage.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.None
            );

            // Drop trailing empty lines from the split, but keep internal blank lines in the body.
            int lastNonEmpty = lines.Length - 1;
            while (lastNonEmpty >= 0 && string.IsNullOrEmpty(lines[lastNonEmpty]))
                lastNonEmpty--;
            if (lastNonEmpty < 0)
            {
                messageBody = "";
                return;
            }

            int stackStartIndex = -1;
            for (int i = 1; i <= lastNonEmpty; ++i)
            {
                string trimmedLine = lines[i].TrimStart();
                if (
                    trimmedLine.StartsWith("at ")
                    || trimmedLine.StartsWith("UnityEngine.")
                    || trimmedLine.StartsWith("UnityEditor.")
                    || trimmedLine.Contains("(at ")
                    || (
                        trimmedLine.Length > 0
                        && char.IsUpper(trimmedLine[0])
                        && trimmedLine.Contains(".")
                    )
                )
                {
                    stackStartIndex = i;
                    break;
                }
            }

            if (stackStartIndex > 0)
            {
                messageBody = string.Join(
                    "\n",
                    lines,
                    0,
                    stackStartIndex
                ).TrimEnd('\r', '\n');
                stackTrace = string.Join(
                    "\n",
                    lines,
                    stackStartIndex,
                    lastNonEmpty - stackStartIndex + 1
                );
            }
            else
            {
                messageBody = string.Join("\n", lines, 0, lastNonEmpty + 1);
            }
        }

        /// <summary>
        /// Attempts to extract the stack trace part from a log message.
        /// Prefer <see cref="SplitMessageAndStack"/> when you also need the message body.
        /// </summary>
        private static string ExtractStackTrace(string fullMessage)
        {
            SplitMessageAndStack(fullMessage, out _, out string stackTrace);
            return stackTrace;
        }

        /* LogEntry.mode bits exploration (based on Unity decompilation/observation):
           May change between versions.

           Basic Types:
           kError = 1 << 0 (1)
           kAssert = 1 << 1 (2)
           kWarning = 1 << 2 (4)
           kLog = 1 << 3 (8)
           kFatal = 1 << 4 (16) - Often treated as Exception/Error

           Modifiers/Context:
           kAssetImportError = 1 << 7 (128)
           kAssetImportWarning = 1 << 8 (256)
           kScriptingError = 1 << 9 (512)
           kScriptingWarning = 1 << 10 (1024)
           kScriptingLog = 1 << 11 (2048)
           kScriptCompileError = 1 << 12 (4096)
           kScriptCompileWarning = 1 << 13 (8192)
           kStickyError = 1 << 14 (16384) - Stays visible even after Clear On Play
           kMayIgnoreLineNumber = 1 << 15 (32768)
           kReportBug = 1 << 16 (65536) - Shows the "Report Bug" button
           kDisplayPreviousErrorInStatusBar = 1 << 17 (131072)
           kScriptingException = 1 << 18 (262144)
           kDontExtractStacktrace = 1 << 19 (524288) - Hint to the console UI
           kShouldClearOnPlay = 1 << 20 (1048576) - Default behavior
           kGraphCompileError = 1 << 21 (2097152)
           kScriptingAssertion = 1 << 22 (4194304)
           kVisualScriptingError = 1 << 23 (8388608)

           Example observed values:
           Log: 2048 (ScriptingLog) or 8 (Log)
           Warning: 1028 (ScriptingWarning | Warning) or 4 (Warning)
           Error: 513 (ScriptingError | Error) or 1 (Error)
           Exception: 262161 (ScriptingException | Error | kFatal?) - Complex combination
           Assertion: 4194306 (ScriptingAssertion | Assert) or 2 (Assert)
        */
    }
}

