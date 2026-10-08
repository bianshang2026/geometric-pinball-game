using System;
using System.Collections.Generic;
using System.Linq;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;
using UnityTcp.Editor.Native;
using UnityTcp.Editor.Tools;

namespace UnityTcp.Editor
{
    /// <summary>
    /// Command structure for JSON deserialization
    /// </summary>
    public class Command
    {
        public string type { get; set; }
        public JObject @params { get; set; }
    }

    public class Notification
    {
        public string notification_type { get; set; }
        public JObject payload { get; set; }
    }

    [InitializeOnLoad]
    public static partial class UnityTcpBridge
    {
        private static bool isRunning = false;
        // SessionState (not EditorPrefs): a manual stop must survive domain reloads but be
        // forgotten when the editor process exits, so a relaunch starts the bridge again.
        private const string ManualStopKey = "UnityTcp.ManualStop";
        private const string ResumeAfterReloadKey = "UnityTcp.ResumeAfterReload";
        public static event Action OnClientPlatformsChanged;
        // Heartbeat file writing lives entirely in native (configured via
        // NativeUnityTcpBridgeHost.StartOrAttach); this just gates how often
        // C# refreshes the NWB stream_port native doesn't know on its own.
        private static double nextStreamPortSyncAt = 0.0;
        private static string serverVer = "1.0.0-beta.1";
        private static int currentUnityPort = -1;
        private const ulong MaxFrameBytes = 64UL * 1024 * 1024; // 64 MiB hard cap for framed payloads
        private const int FrameIOTimeoutMs = 3000; // Per-read timeout to avoid stalled clients
        private const int NativePollLimitPerTick = 128;
        private static int previousConnectionCount = -1;

        // Debug helpers
        private unsafe static bool IsDebugEnabled()
        {
            try { return EditorPrefs.GetBool("UnityTcp.DebugLogs", false); } catch { return false; }
        }

        private static void LogBreadcrumb(string stage)
        {
            if (IsDebugEnabled())
            {
                CodelyLogger.Verbose($"[{stage}]");
            }
        }

        public static bool IsRunning => isRunning;
        public static int GetCurrentPort() => currentUnityPort;

        /// <summary>
        /// Get all connected clients with their platform types
        /// </summary>
        public static Dictionary<string, string> GetConnectedClients()
        {
            if (!isRunning)
            {
                return new Dictionary<string, string>();
            }

            return NativeUnityTcpBridgeHost.GetConnectedClients();
        }



        static UnityTcpBridge()
        {
            // Initialize main thread ID for safe thread checks
            MainThreadHelper.InitializeMainThreadId();

            // Register the FindObjectByInstruction delegate for UnityEngineObjectConverter
            UnityTcp.Editor.Serialization.UnityEngineObjectConverter.FindObjectByInstruction = ManageGameObject.FindObjectByInstruction;

            // Auto-start eligibility does not revoke an already-running bridge's
            // reload lifecycle (including explicit starts in secondary Editors).
            if (EditorAutomationGuard.ShouldSkipBridgeAutoStart()
                && !SessionState.GetBool(ResumeAfterReloadKey, false))
            {
                return;
            }

            // Drop the pre-1.0.70 EditorPrefs flag, which would otherwise keep the bridge
            // stopped forever for anyone who manually stopped it on an older version.
            EditorPrefs.DeleteKey(ManualStopKey);

#if UNITY_2020_1_OR_NEWER
            // Subscribed in the static ctor (not Start) so it survives Stop(),
            // which is exactly when it is needed — see OnPackagesRegistered.
            UnityEditor.PackageManager.Events.registeredPackages -= OnPackagesRegistered;
            UnityEditor.PackageManager.Events.registeredPackages += OnPackagesRegistered;
#endif

            // Respect manual stop across domain reloads (cleared on editor relaunch).
            if (!SessionState.GetBool(ManualStopKey, false))
            {
                Start();
            }

        }

#if UNITY_2020_1_OR_NEWER
        // Counterpart to NativeDllLoader.OnPackagesRegistering, which stops
        // the bridge and unloads the DLL when this package is updated. The
        // update normally ends in a domain reload that relaunches the bridge
        // via [InitializeOnLoad] — but an update carrying no script changes
        // skips the reload, and without this hook that Stop() would be
        // permanent. registeredPackages fires in the same domain once the
        // update completes, so relaunch here. When a reload does follow,
        // both orderings stay correct: if this fires first, Start() runs and
        // the reload just reattaches; if the reload lands first, this
        // callback dies with the old domain and [InitializeOnLoad] handles
        // the relaunch as usual.
        private static void OnPackagesRegistered(UnityEditor.PackageManager.PackageRegistrationEventArgs _)
        {
            // Loader marks pending when a changedFrom/changedTo pair has
            // different native plugin bytes. Relaunch only when pending.
            if (!BridgeUpdateRestartPrompt.IsPending())
                return;

            // Deferred a tick so the relaunch never runs mid-registration,
            // and so it is silently dropped when a domain reload follows
            // (delayCall does not survive the reload — [InitializeOnLoad]
            // owns the relaunch in that case).
            EditorApplication.delayCall += () =>
            {
                if (!isRunning && !SessionState.GetBool(ManualStopKey, false))
                    Start();
                BridgeUpdateRestartPrompt.TryShowPendingPrompt();
            };
        }
#endif

        private static void OnNativeDllUnload(string reason) => Stop(reason: reason);

        private static void OnEditorQuitting()
        {
            Stop(reason: "unity_quit");
        }

        public static void Start()
        {
            // Clear any prior manual-stop so the bridge can run (and survive future domain reloads).
            SessionState.EraseBool(ManualStopKey);

            // isRunning is guarded first to short-circuit the IsRunning() call (requires DLL loaded).
            if (isRunning && NativeUnityTcpBridgeHost.IsRunning())
            {
                if (IsDebugEnabled())
                    CodelyLogger.Log($"<b><color=#2EA3FF>Codely-Bridge</color></b>: UnityTcpBridge already running on port {currentUnityPort}");
                return;
            }

            try
            {
                int initialStreamPort = 0;
                initialStreamPort = NativeWindowBridgeHost.GetBoundPort();

                // Unified native bridge: one TCP listener serves both commands and notifications.
                // StartOrAttach loads the DLL if needed and handles the already-running case
                // natively, and (re)configures the native heartbeat writer in the same call —
                // idempotent, so safe to pass every time even on a post-reload reattach.
                if (!NativeUnityTcpBridgeHost.StartOrAttach(
                        0, (int)MaxFrameBytes,
                        PortManager.GetRegistryFilePath(), Application.dataPath, 500, initialStreamPort,
                        out int boundPort))
                    throw new Exception("Native Codely Bridge failed to start.");

                currentUnityPort = boundPort;
                isRunning = true;
                previousConnectionCount = NativeUnityTcpBridgeHost.GetConnectionCount();

                // Force DetachedJobs' static init here, on the main thread — its Load() reads
                // SessionState, a main-thread-only API. Once the pump starts, a manage_job
                // command on the worker thread could otherwise be the store's first toucher
                // and silently lose everything persisted across the last reload.
                DetachedJobs.EnsureLoaded();
                // Restore and reconcile jobs before the worker can serve manage_job.
                StepJobRunner.EnsureInitialized();

                // A modal dialog may block afterAssemblyReload.
                // Initialize DialogWatcher on Unity's main thread before starting the command worker;
                // otherwise its static initialization could run on the worker thread.
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                    typeof(DialogWatcher).TypeHandle);

                // Fetch commands off the main thread and execute background-eligible ones
                // (manage_job/manage_dialog) on the pump's worker thread; everything else is
                // drained from the pump's main-thread queue in ProcessCommands.
                BackgroundCommandPump.Start(
                    NativeUnityTcpBridgeHost.TryDequeueCommand,
                    ProcessBackgroundCommand,
                    RespondQueuedError);

                EditorApplication.update -= ProcessCommands;
                EditorApplication.update += ProcessCommands;
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload  -= OnAfterAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload  += OnAfterAssemblyReload;
                EditorApplication.quitting -= OnEditorQuitting;
                EditorApplication.quitting += OnEditorQuitting;
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                Native.NativeDllLoader.OnUnload -= OnNativeDllUnload;
                Native.NativeDllLoader.OnUnload += OnNativeDllUnload;
                nextStreamPortSyncAt = EditorApplication.timeSinceStartup + 0.5f;

                // The native gate protects the gap between managed consumers, not Unity's
                // complete reload callback sequence. At this point the new consumer and its
                // modal-dialog escape path are ready, even if afterAssemblyReload is blocked.
                CodelyLogger.Log("ResumeManagedCommandProcessing");
                NativeUnityTcpBridgeHost.ResumeManagedCommandProcessing();
                SessionState.EraseBool(ResumeAfterReloadKey);

                CodelyLogger.Log($"<b><color=#2EA3FF>Codely-Bridge</color></b>: Native Codely Bridge running on port {currentUnityPort}. (OS={Application.platform}, server={serverVer})");
            }
            catch (Exception ex)
            {
                isRunning = false;
                CodelyLogger.LogError($"<b><color=#2EA3FF>Codely-Bridge</color></b>: Native-only mode failed to start. {ex.Message}");
            }
        }

        // reason is surfaced in the heartbeat file while the bridge stays
        // stopped, so clients can tell why it went down. When omitted, a
        // manual stop reports "manually_stopped" and anything else the
        // generic "stopped".
        public static void Stop(bool isManualStop = false, string reason = null)
        {
            SessionState.EraseBool(ResumeAfterReloadKey);
            if (isManualStop)
                SessionState.SetBool(ManualStopKey, true);
            if (!isRunning) return;

            isRunning = false;

            EditorApplication.update -= ProcessCommands;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload  -= OnAfterAssemblyReload;
            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            Native.NativeDllLoader.OnUnload -= OnNativeDllUnload;

            // Stop the pump first: its fetch thread calls into native, which StopServer tears
            // down, and its queued commands must be answered while the listener is still up.
            BackgroundCommandPump.Stop("Bridge stopped while command was queued.");
            // Drain before stopping: EnqueueResponse calls into native memory that StopServer frees.
            DrainPendingCommandsWithError("Bridge stopped while command was queued.");
            // Step jobs survive domain reloads but not a bridge stop — there is no longer anyone to
            // deliver their result to, so retire them and clear their persisted state.
            StepJobRunner.CancelAll("Bridge stopped while step job was running.");
            // Writes the final heartbeat (unity_port: -1, reason as resolved
            // below) itself, as its last action before tearing down the
            // listener — see NTB_Stop / NTB_StopWithReason.
            NativeUnityTcpBridgeHost.StopServer(reason ?? (isManualStop ? "manually_stopped" : null));

            currentUnityPort = -1;

            if (IsDebugEnabled()) CodelyLogger.Log("<b><color=#2EA3FF>Codely-Bridge</color></b>: UnityTcpBridge stopped.");
        }

        private static void DrainPendingCommandsWithError(string reason)
        {
            // Fail any in-flight async-runner jobs on their request ids so nothing is left hanging.
            AsyncTaskRunner.CancelAll(reason);
            CoroutineRunner.CancelAll(reason);
        }

        private static void ProcessCommands()
        {
            if (!isRunning) return;

            if (!NativeUnityTcpBridgeHost.IsRunning())
            {
                // The native server died out from under us — run the full teardown rather than
                // just flipping the flag, which would leave the pump's threads running, step
                // jobs never ticked (their clients hanging), and a later Stop() a no-op behind
                // its !isRunning early-return.
                Stop(reason: "native_stopped");
                return;
            }

            int polled = 0;
            if (BackgroundCommandPump.IsRunning)
            {
                // The pump's fetch thread owns the native dequeue; the main thread only drains
                // the queue of commands routed to it.
                while (polled < NativePollLimitPerTick
                       && BackgroundCommandPump.TryDequeueForMainThread(out ulong requestId, out string commandText))
                {
                    polled++;
                    ProcessSingleCommand(requestId, commandText);
                }
            }
            else
            {
                // Fallback when the pump could not start (thread creation failed): poll native
                // directly, as before the pump existed. Still gated — a fetch thread abandoned
                // by a timed-out pump stop may be stuck inside the native dequeue, which is not
                // safe to call concurrently.
                while (polled < NativePollLimitPerTick
                       && BackgroundCommandPump.GatedTryDequeue(
                              NativeUnityTcpBridgeHost.TryDequeueCommand,
                              out ulong requestId, out string commandText))
                {
                    polled++;
                    ProcessSingleCommand(requestId, commandText);
                }
            }

            double now = EditorApplication.timeSinceStartup;
            if (now >= nextStreamPortSyncAt)
            {
                NativeUnityTcpBridgeHost.SetStreamPort(NativeWindowBridgeHost.GetBoundPort());

                nextStreamPortSyncAt = now + 0.5f;
            }

            NotifyClientPlatformChangesIfNeeded();

            // Advance async jobs (Tasks / coroutines) and enqueue responses for finished ones.
            AsyncTaskRunner.Tick();
            CoroutineRunner.Tick();
            // Step jobs advance one step per frame and survive domain reloads, so they are ticked
            // here but deliberately left out of DrainPendingCommandsWithError.
            StepJobRunner.Tick();
            // DetachedJobs may have been mutated from the pump's worker thread (detach, status
            // collection); persistence is main-thread only, so flush its dirty state here.
            DetachedJobs.FlushIfDirty();
        }

        private static void ProcessSingleCommand(ulong requestId, string commandText)
        {
            if (!NativeUnityTcpBridgeHost.TryBeginCommand(requestId)) return;

            commandText = commandText?.Trim();

            if (string.IsNullOrEmpty(commandText))
            {
                NativeUnityTcpBridgeHost.EnqueueResponse(requestId, JsonConvert.SerializeObject(Response.Error("Empty command received")));
                return;
            }

            if (!JsonCommandHelper.IsValidJson(commandText))
            {
                NativeUnityTcpBridgeHost.EnqueueResponse(requestId, JsonConvert.SerializeObject(Response.Error("Invalid JSON format", new
                {
                    receivedText = commandText.Length > 50 ? commandText.Substring(0, 50) + "..." : commandText
                })));
                return;
            }

            Command command = JsonConvert.DeserializeObject<Command>(commandText);
            if (command == null)
            {
                NativeUnityTcpBridgeHost.EnqueueResponse(requestId, JsonConvert.SerializeObject(Response.Error("Command deserialized to null")));
                return;
            }

            try
            {
                // Expose the request id to the handler (so async tools can start a runner job on
                // it) without changing the HandleCommand(JObject) prototype.
                CommandContext.Set(requestId, command.type);
                // A null response means the handler started an async runner job that will enqueue
                // the real response later (see JobContext / CoroutineRunner).
                var response = ExecuteCommand(command);
                if (response != null)
                    NativeUnityTcpBridgeHost.EnqueueResponse(requestId, response);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogError($"Error processing command: {ex.Message}\n{ex.StackTrace}");
                NativeUnityTcpBridgeHost.EnqueueResponse(requestId, JsonConvert.SerializeObject(Response.Error(ex.Message, new
                {
                    commandType = command.type,
                    stackTrace = ex.StackTrace
                })));
            }
            finally
            {
                CommandContext.Clear();
            }
        }

        // Answers a command the pump fetched but will never execute (pump stopped, worker crash).
        private static void RespondQueuedError(ulong requestId, string reason)
            => NativeUnityTcpBridgeHost.EnqueueResponse(
                requestId, JsonConvert.SerializeObject(Response.Error(reason)));

        // Runs on the BackgroundCommandPump worker thread. Only commands BackgroundCommandPump.RunsInBackground
        // admitted arrive here — manage_job (thread-safe job control) and manage_dialog (clicks
        // a dialog that is blocking the main thread). Anything else is answered with an error
        // rather than risking a Unity API call off the main thread. The response envelope
        // matches the main-thread path (ExecuteCommand).
        private static void ProcessBackgroundCommand(ulong requestId, string commandText)
        {
            if (!NativeUnityTcpBridgeHost.TryBeginCommand(requestId)) return;

            string responseJson;
            try
            {
                var command = JsonConvert.DeserializeObject<Command>(commandText);
                var paramsObject = command?.@params ?? new JObject();

                if (command?.type == "manage_job" || command?.type == "manage_dialog")
                {
                    CommandContext.Set(requestId, command.type);
                    try
                    {
                        object result = command.type == "manage_job"
                            ? ManageJob.HandleCommand(paramsObject)
                            : ManageDialog.HandleCommand(paramsObject);
                        responseJson = JsonConvert.SerializeObject(
                            Response.Success("Command executed successfully", result));
                    }
                    finally
                    {
                        CommandContext.Clear();
                    }
                }
                else
                {
                    responseJson = JsonConvert.SerializeObject(Response.Error(
                        $"Command '{command?.type}' cannot run on the background executor."));
                }
            }
            catch (Exception ex)
            {
                responseJson = JsonConvert.SerializeObject(Response.Error(ex.Message));
            }
            NativeUnityTcpBridgeHost.EnqueueResponse(requestId, responseJson);
        }

        private static void NotifyClientPlatformChangesIfNeeded()
        {
            int current = NativeUnityTcpBridgeHost.GetConnectionCount();
            if (current != previousConnectionCount)
            {
                previousConnectionCount = current;
                try
                {
                    OnClientPlatformsChanged?.Invoke();
                }
                catch
                {
                    // Keep polling path resilient even if UI callbacks fail.
                }
            }
        }



        private static string ExecuteCommand(Command command)
        {
            try
            {
                if (string.IsNullOrEmpty(command.type))
                {
                    var errorResponse = Response.Error("Command type cannot be empty",
                        "A valid command type is required for processing");
                    return JsonConvert.SerializeObject(errorResponse);
                }

                // Use JObject for parameters as the new handlers likely expect this
                JObject paramsObject = command.@params ?? new JObject();
                string action = paramsObject["action"]?.ToString();

                // Central Play Mode write guard. Clients are stateless and rely on
                // the bridge to reject writes while in Play/Paused mode; handlers
                // may additionally enforce stricter rules of their own.
                var writeBlocked = WriteGuard.CheckCommandWriteAllowed(command.type, action);
                if (writeBlocked != null)
                {
                    return JsonConvert.SerializeObject(
                        Response.Error("Command blocked by write guard", writeBlocked));
                }

                // Route command based on the tool structure from the existing project
                object result;
                switch (command.type)
                {
                    // Removed tool: answer with the replacement instead of the generic
                    // "unknown command" error so an AI client knows what to call next.
                    case "manage_workflow":
                        result = Response.ErrorWithCode(
                            "manage_workflow_removed",
                            "manage_workflow has been removed. Use manage_editor with action " +
                            "'request_compile' to compile and validate scripts instead.");
                        break;
                    case "manage_scene":
                        result = HandleManageScene(paramsObject)
                            ?? throw new TimeoutException($"manage_scene timed out after {FrameIOTimeoutMs} ms on main thread");
                        break;
                    case "manage_editor":
                        result = ManageEditor.HandleCommand(paramsObject);
                        break;
                    case "manage_gameobject":
                        result = ManageGameObject.HandleCommand(paramsObject);
                        break;
                    case "manage_asset":
                        result = ManageAsset.HandleCommand(paramsObject);
                        break;
                    case "manage_shader":
                        result = ManageShader.HandleCommand(paramsObject);
                        break;
                    case "read_console":
                        result = ReadConsole.HandleCommand(paramsObject);
                        break;
                    case "execute_menu_item":
                        result = ExecuteMenuItem.HandleCommand(paramsObject);
                        break;
                    case "execute_csharp_script":
                        result = ExecuteCSharpScript.HandleCommand(paramsObject);
                        break;
                    case "exec_editor_script":
                        result = ExecuteScriptCommand.HandleEditorScript(paramsObject);
                        break;
                    case "exec_runtime_script":
                        result = ExecuteScriptCommand.HandleRuntimeScript(paramsObject);
                        break;
                    case "manage_package":
                        result = ManagePackage.HandleCommand(paramsObject);
                        break;
                    case "manage_bake":
                        result = ManageBake.HandleCommand(paramsObject);
                        break;
                    case "execute_custom_tool":
                        result = ExecuteCustomTool.HandleCommand(paramsObject);
                        break;
                    case "get_custom_tools":
                        result = ExecuteCustomTool.GetCustomToolsInfo();
                        break;
                    case "_internal_state_dirty":
                        result = _InternalStateDirtyNotifier.HandleCommand(paramsObject);
                        break;
                    case "_internal_asset_listening":
                        result = _InternalAssetListening.HandleCommand(paramsObject);
                        break;
                    case "manage_window_bridge":
                        result = ManageWindowBridge.HandleCommand(paramsObject);
                        break;
                    case "manage_gameview":
                        result = ManageGameView.HandleCommand(paramsObject);
                        break;
                    case "manage_screenshot":
                        result = ManageScreenshot.HandleCommand(paramsObject);
                        break;
                    case "manage_input":
                        result = ManageInput.HandleCommand(paramsObject);
                        break;
                    case "manage_dialog":
                        result = ManageDialog.HandleCommand(paramsObject);
                        break;
                    // Normally runs on the pump's background worker (see ProcessBackgroundCommand);
                    // this main-thread route covers the no-pump fallback path.
                    case "manage_job":
                        result = ManageJob.HandleCommand(paramsObject);
                        break;
                    default:
                        throw new ArgumentException(
                            $"Unknown or unsupported command type: {command.type}"
                        );
                }

                // A JobContext means the handler started an async runner job: the response will be
                // enqueued later by the runner, so don't produce one now.
                if (result is JobContext)
                    return null;

                // Convert result to success response format compatible with Response helper.
                // Responses carry only the tool's own result; editor state is an
                // explicit read (manage_editor.get_state).
                return JsonConvert.SerializeObject(
                    Response.Success("Command executed successfully", result));
            }
            catch (Exception ex)
            {
                // Log the detailed error in Unity for debugging
                CodelyLogger.LogError(
                    $"Error executing command '{command?.type ?? "Unknown"}': {ex.Message}\n{ex.StackTrace}"
                );

                // Use Response helper for consistent error format
                var response = Response.Error(ex.Message, new
                {
                    command = command?.type ?? "Unknown",
                    stackTrace = ex.StackTrace,
                    paramsSummary = command?.@params != null
                        ? JsonCommandHelper.GetParamsSummary(command.@params)
                        : "No parameters"
                });
                return JsonConvert.SerializeObject(response);
            }
        }

        private static object HandleManageScene(JObject paramsObject)
        {
            try
            {
                if (IsDebugEnabled()) CodelyLogger.Log("[TCP] manage_scene: dispatching to main thread");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = MainThreadHelper.InvokeOnMainThreadWithTimeout(() => ManageScene.HandleCommand(paramsObject), FrameIOTimeoutMs);
                sw.Stop();
                if (IsDebugEnabled()) CodelyLogger.Log($"[TCP] manage_scene: completed in {sw.ElapsedMilliseconds} ms");
                return r ?? Response.Error("manage_scene returned null (timeout or error)");
            }
            catch (Exception ex)
            {
                return Response.Error($"manage_scene dispatch error: {ex.Message}");
            }
        }


        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                // Fail in-flight jobs before Unity tears down play-mode objects — otherwise the
                // runners would advance coroutines against destroyed objects.
                DrainPendingCommandsWithError("Play mode exiting.");
            }
        }

        // Heartbeat/status helpers
        private static void OnBeforeAssemblyReload()
        {
            if (isRunning)
                SessionState.SetBool(ResumeAfterReloadKey, true);

            // Best-effort: a REPL session is about to be silently wiped by this reload (its static
            // fields don't survive), so tell clients before that happens. Native send is async and
            // reload timing can cut it off, so this is a courtesy, not the only way a client learns
            // its session is gone — the next execute against a stale id gets the same fact from
            // `session_status`.
            var destroyedSessionId = ExecuteCSharpScript.ActiveSessionId;
            if (destroyedSessionId != null)
                NotifyAll("script_session_destroyed", new JObject { ["script_session_id"] = destroyedSessionId });

            CodelyLogger.Log("PauseManagedCommandProcessing");
            NativeUnityTcpBridgeHost.PauseManagedCommandProcessing();
            // The pump's threads and delegates die with this domain. Native has restored the
            // leases, so discard only the managed queue copies without answering them.
            BackgroundCommandPump.StopForReload();
            DrainPendingCommandsWithError("C# is performing domain reloading.");
            // The cancels above recorded outcomes for any detached jobs; persist them here
            // explicitly instead of relying on DetachedJobs' own beforeAssemblyReload
            // subscriber, whose order relative to this one is unspecified.
            DetachedJobs.FlushIfDirty();
        }

        private static void OnAfterAssemblyReload()
        {
            // Reset custom tool registry so the next call rescans the reloaded domain.
            ExecuteCustomTool.InvalidateCache();
        }

        // ---- Notify API ---------------------------------------------------- //

        /// <summary>
        /// Broadcasts a push notification to every client that advertised CLIENT_VERSION>=2.
        /// In the unified bridge, notifications share the same socket as commands.
        /// </summary>
        /// <param name="eventType">Event type identifier (e.g. "scene_changed").</param>
        /// <param name="payload">Optional payload object; serialized to JSON.</param>
        /// <returns>True if the notification was enqueued to at least one client.</returns>
        public static bool NotifyAll(string eventType, JObject payload = null)
        {
            if (!isRunning || string.IsNullOrWhiteSpace(eventType))
                return false;

            string json = JsonConvert.SerializeObject(new Notification { notification_type = eventType, payload = payload });
            return NativeUnityTcpBridgeHost.NotifyAll(json);
        }
    }
}
