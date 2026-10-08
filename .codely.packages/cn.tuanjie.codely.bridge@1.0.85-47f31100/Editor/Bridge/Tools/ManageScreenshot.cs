using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Codely.Newtonsoft.Json;
using System.Reflection;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityTcp.Editor.Helpers;

#if UNITY_2021_2_OR_NEWER
using UnityEngine.UIElements;
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Handles screenshot capture operations within Unity Editor.
    ///
    /// Actions:
    ///   capture_game_view        – Disabled legacy entry retained for a clear migration error.
    ///   capture_scene_view       – Capture the Scene View from one or more directions.
    ///   capture_main_camera      – Render Camera.main to a texture.
    ///   capture_specific_camera  – Render a named camera to a texture.
    ///   capture_asset            – Instantiate asset(s) in the current scene and render a preview.
    ///   capture_ui_toolkit       – Render a UIDocument (.uxml) to a texture.
    ///   start_game_view_recording  – Start a background Game View MP4 recording session.
    ///   finish_game_view_recording – Wait for and return the active MP4 recording result.
    ///
    /// Legacy aliases:
    ///   capture          → disabled legacy capture_game_view entry
    ///   capture_scene_camera → capture_scene_view
    ///
    /// Common parameters (most actions):
    ///   path (string)         – Custom save directory (default: <ProjectRoot>/screenshots).
    ///   filename (string)     – Custom filename (with or without extension).
    ///   allow_static_in_play_mode (bool) – Explicit override for genuinely static Play Mode
    ///                           camera output. Requires static_reason. Without this override,
    ///                           capture_main_camera / capture_specific_camera are rejected in
    ///                           Play Mode.
    ///   static_reason (string) – Why a single frame is sufficient for Play Mode verification.
    ///   over_time (string)    – "N" or "NxS": capture N frames with S simulation steps
    ///                           between each.
    ///                           Applies to: capture_scene_view, capture_main_camera,
    ///                                       capture_specific_camera.
    ///                           Ignored when as_gif is given (GIF capture takes precedence).
    ///   as_gif                – When present (truthy, or an object holding the sub-params
    ///                           below) the capture records multiple frames and saves them as
    ///                           an animated .gif instead of a PNG. Takes precedence over
    ///                           over_time. Supported by capture_scene_view, capture_main_camera,
    ///                           capture_specific_camera.
    ///                           Sub-parameters (accepted as siblings of as_gif or nested in it):
    ///                             frameCount (int, 1-200)  – REQUIRED. Number of frames to capture.
    ///                             fps        (int, 10-30)  – Playback frames per second (default 25).
    ///                             colorCount (int, 64-256) – GIF palette color count (default 128).
    ///                             startDelay (float, 0-5)  – Seconds to wait before capture starts
    ///                                                        (default 0).
    ///   start_game_view_recording:
    ///     durationSeconds (float, 1-8) – Recording duration in real-time seconds (default 5).
    ///     fps             (int, 5-30)   – Capture frame rate: how many frames per second are
    ///                                     grabbed from the Game View (default 15). Higher values
    ///                                     capture more visual detail.
    ///     encodeFps       (int, 1-30)   – MP4 metadata frame rate (default 2). This controls how
    ///                                     the model samples the video. When encodeFps < fps, the MP4
    ///                                     plays in slow motion so a 2fps-sampling VLM (e.g. GLM) sees
    ///                                     every captured frame instead of skipping most of them.
    ///                                     Example: 5s @ capture 15fps + encode 2fps → 75 frames in
    ///                                     a 37.5s slow-motion MP4; a 2fps model samples all 75.
    ///     startDelay      (float, 0-5)  – Pre-roll before frames are recorded (default 0).
    ///     scale           (float, 0.25-1.0) – Game View resolution scale (default 0.5).
    ///     Windows uses the Unity Editor MediaEncoder backed by the native platform media stack.
    ///     No external encoder executable is required.
    ///   finish_game_view_recording:
    ///     recording_id (string) – Required ID returned by start_game_view_recording.
    ///
    /// capture_scene_view / capture_asset:
    ///   width (int)           – Output width in pixels (default: 1024).
    ///   height (int)          – Output height in pixels (default: 1024).
    ///   view (string)         – Direction(s) to render. Applies to both capture_scene_view and
    ///                           capture_asset. "current" (default for capture_scene_view) = WYSIWYG
    ///                           editor view; "cardinal" (default for capture_asset) = front + right
    ///                           + top + iso; "all" = all 7 directions; any named direction renders
    ///                           a single view. Multi-view captures are stitched into a grid image.
    ///   orthographic (bool)   – Use orthographic projection instead of perspective (default false).
    ///                           Applies to both capture_scene_view and capture_asset.
    ///   render_mode (string)  – "shaded" (default), "wireframe", or "shadedwireframe".
    ///                           Controls how geometry is drawn. Only applies to capture_scene_view.
    ///                           Non-shaded modes use the GL.wireframe renderer path (gizmos and
    ///                           scene grid are not composited in those modes).
    ///
    /// capture_main_camera / capture_specific_camera:
    ///   Always renders from the camera's current position, rotation and projection.
    ///   width (int)           – Output width in pixels (default: camera pixel width).
    ///   height (int)          – Output height in pixels (default: camera pixel height).
    ///   Note: view and orthographic parameters are not applicable to camera captures.
    ///
    /// Auto-scaling:
    ///   When neither width nor height is supplied, each individual screenshot is
    ///   downsampled so its longest edge equals 256 pixels (aspect ratio preserved).
    ///   Supplying either width or height disables this behavior.
    /// </summary>
    public static class ManageScreenshot
    {
        private static readonly List<string> ValidActions = new List<string>
        {
            "capture_game_view",
            "capture_scene_view",
            "capture_main_camera",
            "capture_specific_camera",
            "capture_asset",
            "capture_ui_toolkit",
            "start_game_view_recording",
            "finish_game_view_recording",
            // Legacy aliases
            "capture",
            "capture_scene_camera",
        };

        // ======================== sync entry point (over_time forced to 1) ========================

        /// <summary>
        /// Command entry point. Starts the capture coroutine under the CoroutineRunner (capture may
        /// span multiple editor frames) and returns the job — the response is delivered later when
        /// the coroutine finishes.
        /// </summary>
        public static object HandleCommand(JObject @params)
        {
            var ctx = CoroutineRunner.CreateJob(CommandContext.RequestId, CommandContext.CommandType);
            CoroutineRunner.RunJob(ctx, HandleCommandCoroutine(@params, r => ctx.SetResult(r)));
            return ctx;
        }

        // ======================== coroutine entry point ========================

        public static IEnumerator HandleCommandCoroutine(JObject @params, Action<object> setResult)
        {
            if (@params == null)
            {
                setResult(Response.Error("Parameters cannot be null."));
                yield break;
            }

            if (!ActionRouter.TryResolve(@params, ValidActions, out var action, out var resolveError))
            {
                setResult(resolveError);
                yield break;
            }

            object captureResult = null;
            IEnumerator inner = null;
            Exception caughtEx = null;

            try
            {
                switch (action)
                {
                    case "capture_game_view":
                    case "capture":
                        captureResult = Response.Error(
                            $"'{action}' is disabled. Use record_game_view on exec_runtime_script " +
                            "(or legacy execute_csharp_script) " +
                            "for dynamic Play Mode verification.");
                        break;

                    case "capture_scene_view":
                    case "capture_scene_camera":
                        inner = CaptureSceneViewCoroutine(@params, r => captureResult = r);
                        break;

                    case "capture_main_camera":
                    {
                        Camera cam = Camera.main;
                        if (cam == null) { captureResult = Response.Error("No main camera (Camera.main) found in the scene."); break; }
                        inner = CaptureFromCameraCoroutine(cam, @params, "MainCamera", r => captureResult = r);
                        break;
                    }

                    case "capture_specific_camera":
                    {
                        string camName = @params["camera_name"]?.ToString();
                        if (string.IsNullOrEmpty(camName)) { captureResult = Response.Error("'camera_name' parameter is required."); break; }
                        Camera cam = FindCameraByName(camName);
                        if (cam == null) { captureResult = Response.Error($"Camera '{camName}' not found in the scene."); break; }
                        inner = CaptureFromCameraCoroutine(cam, @params, camName, r => captureResult = r);
                        break;
                    }

                    case "capture_asset":
                        captureResult = CaptureAsset(@params);
                        break;

                    case "capture_ui_toolkit":
                        inner = CaptureUIToolkitCoroutine(@params, r => captureResult = r);
                        break;

                    case "start_game_view_recording":
                        captureResult = StartGameViewMp4Recording(@params);
                        break;

                    case "finish_game_view_recording":
                        inner = FinishGameViewMp4RecordingCoroutine(@params, r => captureResult = r);
                        break;

                    default:
                        captureResult = Response.Error($"Unknown action: '{action}'.");
                        break;
                }
            }
            catch (Exception e)
            {
                CodelyLogger.LogError($"[ManageScreenshot] Action '{action}' failed: {e}");
                setResult(Response.Error($"Internal error processing action '{action}': {e.Message}"));
                yield break;
            }

            if (inner != null)
            {
                while (true)
                {
                    bool hasNext;
                    try { hasNext = inner.MoveNext(); }
                    catch (Exception e) { caughtEx = e; break; }
                    if (!hasNext) break;
                    yield return inner.Current;
                }
            }
            else
            {
                yield return null;
            }

            if (caughtEx != null)
            {
                CodelyLogger.LogError($"[ManageScreenshot] Action '{action}' failed: {caughtEx}");
                setResult(Response.Error($"Internal error: {caughtEx.Message}"));
            }
            else
            {
                setResult(captureResult ?? Response.Error("No result produced."));
            }
        }

        // ==================== Background Game View MP4 recording ====================
        //
        // The start/finish split lets callers start recording, trigger a short-lived runtime
        // effect with another tool call, then wait for the recording result. A single blocking
        // capture command cannot reliably observe short-lived effects because they may finish
        // before the screenshot command begins.

        internal enum Mp4RecordingOwner
        {
            Split,
            ExecuteCSharp,
        }

        internal enum Mp4RecordingStatus
        {
            Pending,
            Finished,
            Unavailable,
        }

        private sealed class Mp4RecordingSession
        {
            public string Id;
            public Mp4RecordingOwner Owner;
            public string OwnerCommandName;
            public int Waiters;
            public string OutputPath;
            public string EncoderPath;
            public int Fps;
            public int EncodeFps;
            public int TargetFrames;
            public int AttemptedFrames;
            public int CapturedFrames;
            public int DroppedFrames;
            public int Width;
            public int Height;
            public float Scale;
            public double NextCaptureTime;
            public double ActualStartTime;
            public double ActualEndTime;
            public volatile bool Finished;
            public object Result;
            public string ResultJson;
            public float StartDelay;
            public float DurationSeconds;
            public IMp4FrameEncoder Encoder;
        }

        internal static Func<string, int, int, int, IMp4FrameEncoder> Mp4EncoderFactoryOverride;

        internal sealed class Mp4RecordingHandle
        {
            internal string Id;
            internal string OutputPath;
            internal int Fps;
            internal int EncodeFps;
            internal int TargetFrames;
            internal float DurationSeconds;
            internal float Scale;
        }

        private static Mp4RecordingSession s_Mp4Recording;
        private static bool s_TickingMp4;
        private static string s_LastCollectedRecordingId;
        private static object s_LastCollectedRecordingResult;

        internal static bool HasGameViewMp4Recording =>
            s_Mp4Recording != null;

        internal static bool HasSplitGameViewMp4Recording =>
            s_Mp4Recording != null &&
            s_Mp4Recording.Owner == Mp4RecordingOwner.Split;

        internal static bool CanAttachExecuteCSharpGameViewRecording() =>
            s_Mp4Recording != null &&
            s_Mp4Recording.Owner == Mp4RecordingOwner.ExecuteCSharp;

        private static object StartGameViewMp4Recording(JObject p)
        {
            if (!TryBeginGameViewMp4Recording(p, out Mp4RecordingHandle handle, out object error))
                return error;

            return Response.Success("Game View MP4 recording started.", new
            {
                recording_id = handle.Id,
                durationSeconds = handle.DurationSeconds,
                fps = handle.Fps,
                encodeFps = handle.EncodeFps,
                targetFrames = handle.TargetFrames,
                scale = handle.Scale,
                path = handle.OutputPath,
            });
        }

        internal static bool TryBeginOrAttachExecuteCSharpGameViewRecording(
            JObject p, out Mp4RecordingHandle handle, out bool attached, out object error)
        {
            handle = null;
            attached = false;
            error = null;

            if (s_Mp4Recording != null)
            {
                if (s_Mp4Recording.Owner != Mp4RecordingOwner.ExecuteCSharp)
                {
                    TryGetActiveGameViewMp4RecordingConflict(out error);
                    return false;
                }

                s_Mp4Recording.Waiters++;
                handle = ToHandle(s_Mp4Recording);
                attached = true;
                return true;
            }

            if (!TryBeginOwnedGameViewMp4Recording(
                    p, Mp4RecordingOwner.ExecuteCSharp, out handle, out error))
                return false;

            return true;
        }

        internal static bool TryBeginGameViewMp4Recording(
            JObject p, out Mp4RecordingHandle handle, out object error)
        {
            return TryBeginOwnedGameViewMp4Recording(
                p, Mp4RecordingOwner.Split, out handle, out error);
        }

        internal static float ResolveMp4DurationSeconds(JObject p)
        {
            return Mathf.Clamp(
                AsFloat(p?["durationSeconds"] ?? p?["duration_seconds"]) ?? 5f,
                1f,
                8f);
        }

        private static bool TryBeginOwnedGameViewMp4Recording(
            JObject p, Mp4RecordingOwner owner, out Mp4RecordingHandle handle, out object error)
        {
            handle = null;
            error = null;

            if (TryGetActiveGameViewMp4RecordingConflict(out error))
                return false;

            if (!EditorApplication.isPlaying)
            {
                error = Response.Error(
                    "Game View MP4 recording requires Play Mode. " +
                    "Use exec_runtime_script with record_game_view " +
                    "(or enter play first and use legacy execute_csharp_script) " +
                    "and trigger or restart the effect in that script. " +
                    "Use start_game_view_recording only for input or external events " +
                    "that cannot be restarted from a script.");
                return false;
            }

            if (!PlatformMp4Encoder.IsSupported && Mp4EncoderFactoryOverride == null)
            {
                error = Response.Error(PlatformMp4Encoder.UnsupportedMessage);
                return false;
            }

            float duration = ResolveMp4DurationSeconds(p);
            int fps = Mathf.Clamp(AsInt(p?["fps"]) ?? 15, 5, 30);
            int encodeFps = Mathf.Clamp(AsInt(p?["encodeFps"] ?? p?["encode_fps"]) ?? fps, 1, 30);
            float startDelay = Mathf.Clamp(
                AsFloat(p?["startDelay"] ?? p?["start_delay"]) ?? 0f, 0f, 5f);
            float scale = Mathf.Clamp(AsFloat(p?["scale"]) ?? 0.5f, 0.25f, 1f);

            if (!TryResolveMp4OutputDirectory(
                    p?["path"]?.ToString(), out string outputDirectory, out string pathError))
            {
                error = Response.Error(pathError);
                return false;
            }

            string filename = p?["filename"]?.ToString();
            if (string.IsNullOrEmpty(filename))
                filename = $"GameView_{Timestamp()}.mp4";
            else
            {
                if (Path.IsPathRooted(filename) ||
                    !string.Equals(filename, Path.GetFileName(filename), StringComparison.Ordinal) ||
                    filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    error = Response.Error(
                        "'filename' must be a file name only, without a directory, absolute path, or '..'.");
                    return false;
                }

                if (!filename.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    filename += ".mp4";
            }

            try
            {
                Directory.CreateDirectory(outputDirectory);
                string id = Guid.NewGuid().ToString("N");
                string outputPath = Path.Combine(outputDirectory, filename);
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                string encoderPath = ResolveMp4EncoderPath(outputPath);

                double now = EditorApplication.timeSinceStartup;
                s_Mp4Recording = new Mp4RecordingSession
                {
                    Id = id,
                    Owner = owner,
                    OwnerCommandName = owner == Mp4RecordingOwner.ExecuteCSharp
                        ? ExecuteCSharpScript.DisplayCommandName
                        : null,
                    Waiters = 1,
                    OutputPath = outputPath,
                    EncoderPath = encoderPath,
                    Fps = fps,
                    EncodeFps = encodeFps,
                    TargetFrames = Mathf.Clamp(Mathf.CeilToInt(duration * fps), 1, 450),
                    Scale = scale,
                    StartDelay = startDelay,
                    DurationSeconds = duration,
                    NextCaptureTime = now + startDelay,
                };

                HookMp4RecordingCallbacks();

                CodelyLogger.Log(
                    $"[ManageScreenshot] Started MP4 recording {id}: " +
                    $"{duration:F2}s @ capture {fps}fps → encode {encodeFps}fps, scale={scale:F2}");

                handle = ToHandle(s_Mp4Recording);
                return true;
            }
            catch (Exception e)
            {
                CleanupMp4Recording(false);
                error = Response.Error($"Failed to start MP4 recording: {e.Message}");
                return false;
            }
        }

        internal static bool IsGameViewMp4RecordingFinished(Mp4RecordingHandle handle)
        {
            return GetGameViewMp4RecordingStatus(handle) == Mp4RecordingStatus.Finished;
        }

        internal static Mp4RecordingStatus GetGameViewMp4RecordingStatus(
            Mp4RecordingHandle handle)
        {
            if (handle == null)
                return Mp4RecordingStatus.Unavailable;

            if (s_Mp4Recording != null)
            {
                if (!string.Equals(handle.Id, s_Mp4Recording.Id, StringComparison.Ordinal))
                    return Mp4RecordingStatus.Unavailable;

                return s_Mp4Recording.Finished
                    ? Mp4RecordingStatus.Finished
                    : Mp4RecordingStatus.Pending;
            }

            return string.Equals(handle.Id, s_LastCollectedRecordingId, StringComparison.Ordinal)
                ? Mp4RecordingStatus.Finished
                : Mp4RecordingStatus.Unavailable;
        }

        internal static object CollectGameViewMp4Recording(Mp4RecordingHandle handle)
        {
            if (handle == null)
                return Response.Error("The Game View MP4 recording is no longer available.");

            if (s_Mp4Recording == null)
            {
                if (string.Equals(handle.Id, s_LastCollectedRecordingId, StringComparison.Ordinal))
                    return s_LastCollectedRecordingResult;

                return Response.Error("The Game View MP4 recording is no longer available.");
            }

            if (!string.Equals(handle.Id, s_Mp4Recording.Id, StringComparison.Ordinal))
                return Response.Error("The Game View MP4 recording is no longer available.");

            if (!s_Mp4Recording.Finished)
                return Response.Error("The Game View MP4 recording has not finished yet.");

            object result = s_Mp4Recording.Result ??
                Response.Error("MP4 recording finished without producing a result.");
            s_LastCollectedRecordingId = handle.Id;
            s_LastCollectedRecordingResult = result;
            ReleaseRecordingWaiter(true);
            return result;
        }

        internal static void CancelGameViewMp4Recording(Mp4RecordingHandle handle)
        {
            if (handle == null || s_Mp4Recording == null ||
                !string.Equals(handle.Id, s_Mp4Recording.Id, StringComparison.Ordinal))
                return;

            ReleaseRecordingWaiter(false);
        }

        private static Mp4RecordingHandle ToHandle(Mp4RecordingSession session)
        {
            return new Mp4RecordingHandle
            {
                Id = session.Id,
                DurationSeconds = session.DurationSeconds,
                Fps = session.Fps,
                EncodeFps = session.EncodeFps,
                TargetFrames = session.TargetFrames,
                Scale = session.Scale,
                OutputPath = session.OutputPath,
            };
        }

        private static void ReleaseRecordingWaiter(bool preserveOutput)
        {
            if (s_Mp4Recording == null) return;
            s_Mp4Recording.Waiters--;
            if (s_Mp4Recording.Waiters > 0)
                return;

            CleanupMp4Recording(preserveOutput);
        }

        /// <summary>
        /// True when another Game View MP4 recording is already active or finished
        /// but not yet collected. Callers that want to start a new recording must
        /// fail closed instead of discarding that session.
        /// </summary>
        internal static bool TryGetActiveGameViewMp4RecordingConflict(out object error)
        {
            error = null;
            if (s_Mp4Recording == null)
                return false;

            string state = s_Mp4Recording.Finished ? "finished but not yet collected" : "still active";
            string ownerCommand = string.IsNullOrEmpty(s_Mp4Recording.OwnerCommandName)
                ? "exec_runtime_script or legacy execute_csharp_script"
                : s_Mp4Recording.OwnerCommandName;
            string resolution = s_Mp4Recording.Owner == Mp4RecordingOwner.Split
                ? "Collect it with finish_game_view_recording and the matching recording_id."
                : $"Wait for the owning {ownerCommand} command " +
                  "to collect it automatically.";
            error = Response.Error(
                $"An MP4 recording is already {state} (recording_id={s_Mp4Recording.Id}). " +
                resolution);
            return true;
        }

        /// <summary>
        /// Explicit leftover cleanup for tests and aborted encode jobs.
        /// Do not call this to make room for a new record_game_view attempt;
        /// an active session must fail closed so the owner can collect it.
        /// </summary>
        internal static void DiscardActiveGameViewMp4Recording()
        {
            s_LastCollectedRecordingId = null;
            s_LastCollectedRecordingResult = null;
            if (s_Mp4Recording == null) return;

            CodelyLogger.LogWarning(
                $"[ManageScreenshot] Discarding leftover MP4 recording {s_Mp4Recording.Id}.");
            CleanupMp4Recording(false);
        }

        private static void HookMp4RecordingCallbacks()
        {
            EditorApplication.update -= TickGameViewMp4Recording;
            EditorApplication.update += TickGameViewMp4Recording;
        }

        private static void RememberFinishedMp4Result(Mp4RecordingSession session, object result)
        {
            if (session == null) return;
            session.Result = result;
            session.ResultJson = result == null ? null : JsonConvert.SerializeObject(result);
            session.Finished = true;
        }

        private static void TickGameViewMp4Recording()
        {
            if (s_TickingMp4) return;

            Mp4RecordingSession session = s_Mp4Recording;
            if (session == null || session.Finished)
            {
                if (session == null || session.Finished)
                    EditorApplication.update -= TickGameViewMp4Recording;
                return;
            }

            s_TickingMp4 = true;
            try
            {
                TickGameViewMp4RecordingBody(session);
            }
            finally
            {
                s_TickingMp4 = false;
            }
        }

        private static void TickGameViewMp4RecordingBody(Mp4RecordingSession session)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < session.NextCaptureTime) return;

            if (!EditorApplication.isPlaying)
            {
                FinalizeMp4Recording(session, "Play Mode ended before the requested recording duration.");
                return;
            }

            session.AttemptedFrames++;
            Texture2D frame = null;
            bool writingNativeFrame = false;
            try
            {
                frame = CaptureGameViewTexture();
                if (frame != null)
                    frame = ScaleTextureByFactor(frame, session.Scale);
                if (frame != null)
                    frame = PrepareMp4Frame(frame);

                if (frame == null)
                {
                    session.DroppedFrames++;
                }
                else if (session.CapturedFrames > 0 &&
                         (frame.width != session.Width || frame.height != session.Height))
                {
                    session.DroppedFrames++;
                    UnityEngine.Object.DestroyImmediate(frame);
                    frame = null;
                    CodelyLogger.LogWarning(
                        "[ManageScreenshot] Dropped MP4 frame because the Game View size changed during recording.");
                }
                else
                {
                    if (session.CapturedFrames == 0)
                    {
                        session.Width = frame.width;
                        session.Height = frame.height;
                        session.ActualStartTime = now;
                        writingNativeFrame = true;
                        session.Encoder = CreateMp4Encoder(
                            session.EncoderPath, session.Width, session.Height, session.EncodeFps);
                    }

                    writingNativeFrame = true;
                    if (session.Encoder == null || !session.Encoder.AddFrame(frame))
                        throw new InvalidOperationException(
                            "The Windows native media encoder rejected a Game View frame.");

                    session.CapturedFrames++;
                    session.ActualEndTime = now;
                    UnityEngine.Object.DestroyImmediate(frame);
                    frame = null;
                }
            }
            catch (Exception e)
            {
                if (frame != null) UnityEngine.Object.DestroyImmediate(frame);
                if (writingNativeFrame)
                {
                    FailMp4Recording(
                        session,
                        $"Failed to encode Game View MP4 frame {session.AttemptedFrames - 1} " +
                        $"with the Windows native media encoder: {e.Message}");
                    return;
                }

                session.DroppedFrames++;
                CodelyLogger.LogWarning(
                    $"[ManageScreenshot] MP4 frame {session.AttemptedFrames - 1} capture failed: {e.Message}");
            }

            session.NextCaptureTime = now + 1.0 / session.Fps;
            if (session.AttemptedFrames >= session.TargetFrames)
                FinalizeMp4Recording(session, null);
        }

        private static IEnumerator FinishGameViewMp4RecordingCoroutine(
            JObject p, Action<object> setResult)
        {
            Mp4RecordingSession session = s_Mp4Recording;
            if (session == null)
            {
                setResult(Response.Error("No Game View MP4 recording exists. Call start_game_view_recording first."));
                yield break;
            }

            string requestedId = p?["recording_id"]?.ToString();
            if (string.IsNullOrWhiteSpace(requestedId))
            {
                setResult(Response.Error(
                    "'recording_id' is required for finish_game_view_recording. " +
                    "Use the ID returned by start_game_view_recording."));
                yield break;
            }

            if (!string.Equals(requestedId, session.Id, StringComparison.Ordinal))
            {
                setResult(Response.Error(
                    $"recording_id '{requestedId}' does not match the active recording '{session.Id}'."));
                yield break;
            }

            if (session.Owner != Mp4RecordingOwner.Split)
            {
                setResult(Response.Error(
                    "finish_game_view_recording cannot collect a recording owned by " +
                    "exec_runtime_script or legacy execute_csharp_script; " +
                    "that command collects its recording automatically."));
                yield break;
            }

            while (!session.Finished)
                yield return null;

            object result = session.Result ??
                Response.Error("MP4 recording finished without producing a result.");
            s_LastCollectedRecordingId = session.Id;
            s_LastCollectedRecordingResult = result;
            setResult(result);
            ReleaseRecordingWaiter(true);
        }

        internal static void FinalizeActiveMp4RecordingForTests(string warning = null)
        {
            FinalizeMp4Recording(s_Mp4Recording, warning);
        }

        private static IMp4FrameEncoder CreateMp4Encoder(
            string outputPath, int width, int height, int fps)
        {
            return Mp4EncoderFactoryOverride != null
                ? Mp4EncoderFactoryOverride(outputPath, width, height, fps)
                : PlatformMp4Encoder.Create(outputPath, width, height, fps);
        }

        private static string PlatformEncoderName
        {
            get
            {
#if UNITY_EDITOR_WIN
                return "windows_media_foundation";
#elif UNITY_EDITOR_OSX
                return "avfoundation";
#else
                return "unsupported";
#endif
            }
        }

        private static void FinalizeMp4Recording(Mp4RecordingSession session, string warning)
        {
            if (session == null || session.Finished) return;
            EditorApplication.update -= TickGameViewMp4Recording;

            if (session.CapturedFrames == 0)
            {
                DisposeMp4EncoderBestEffort(session);
                RememberFinishedMp4Result(session, Response.Error(
                    "Failed to capture any Game View frames for MP4 recording."));
                return;
            }

            try
            {
                IMp4FrameEncoder encoder = session.Encoder;
                session.Encoder = null;
                encoder?.Dispose();

                if (!File.Exists(session.EncoderPath))
                    throw new IOException("The Windows native media encoder produced no output file.");

                if (!string.Equals(session.EncoderPath, session.OutputPath, StringComparison.Ordinal))
                    File.Move(session.EncoderPath, session.OutputPath);

                long sizeBytes = new FileInfo(session.OutputPath).Length;
                double actualCaptureSeconds = session.ActualEndTime > session.ActualStartTime
                    ? session.ActualEndTime - session.ActualStartTime
                    : 0.0;

                CodelyLogger.Log(
                    $"[ManageScreenshot] Saved MP4 with Windows Media Foundation: {session.OutputPath} " +
                    $"({session.CapturedFrames} frames, capture {session.Fps}fps → encode {session.EncodeFps}fps)");

                RememberFinishedMp4Result(session, Response.Success("Game View MP4 recording completed.", new
                {
                    recording_id = session.Id,
                    path = session.OutputPath,
                    format = "mp4",
                    codec = "h264",
                    encoder = PlatformEncoderName,
                    width = session.Width,
                    height = session.Height,
                    frames = session.CapturedFrames,
                    captureFps = session.Fps,
                    encodeFps = session.EncodeFps,
                    durationSeconds = (double)session.CapturedFrames / session.EncodeFps,
                    actualCaptureSeconds,
                    droppedFrames = session.DroppedFrames,
                    sizeBytes,
                    warning,
                    note = session.EncodeFps < session.Fps
                        ? $"Slow-motion MP4: captured at {session.Fps}fps over {actualCaptureSeconds:F1}s of real-time, " +
                          $"encoded at {session.EncodeFps}fps so a {session.EncodeFps}fps-sampling VLM sees every frame. " +
                          $"Playback duration is {(double)session.CapturedFrames / session.EncodeFps:F1}s " +
                          $"({(double)session.Fps / session.EncodeFps:F1}x slower than real-time). " +
                          $"Interpret motion accordingly — objects move slower than they did in the actual game."
                        : null,
                }));
            }
            catch (Exception e)
            {
                FailMp4Recording(
                    session,
                    $"Failed to finalize MP4 with the Windows native media encoder: {e.Message}");
            }
        }

        private static void FailMp4Recording(Mp4RecordingSession session, string message)
        {
            if (session == null || session.Finished) return;
            EditorApplication.update -= TickGameViewMp4Recording;
            DisposeMp4EncoderBestEffort(session);
            try
            {
                DeleteMp4SessionFiles(session);
            }
            catch { }
            RememberFinishedMp4Result(session, Response.Error(message));
        }

        private static void DisposeMp4EncoderBestEffort(Mp4RecordingSession session)
        {
            if (session?.Encoder == null) return;
            IMp4FrameEncoder encoder = session.Encoder;
            session.Encoder = null;
            try { encoder.Dispose(); }
            catch (Exception e)
            {
                CodelyLogger.LogWarning(
                    $"[ManageScreenshot] Failed to dispose the native MP4 encoder: {e.Message}");
            }
        }

        private static bool TryResolveMp4OutputDirectory(
            string requested, out string resolved, out string error)
        {
            resolved = null;
            error = null;
            try
            {
                string projectRoot = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
                string candidate = string.IsNullOrWhiteSpace(requested)
                    ? Path.Combine(projectRoot, "screenshots")
                    : requested.Trim().Trim('"');
                if (!Path.IsPathRooted(candidate))
                    candidate = Path.Combine(projectRoot, candidate);

                candidate = Path.GetFullPath(candidate);
#if UNITY_EDITOR_WIN
                StringComparison comparison = StringComparison.OrdinalIgnoreCase;
#else
                StringComparison comparison = StringComparison.Ordinal;
#endif
                string rootWithSeparator =
                    projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                if (!string.Equals(candidate, projectRoot, comparison) &&
                    !candidate.StartsWith(rootWithSeparator, comparison))
                {
                    error =
                        "'path' for Game View recording must stay inside the current Unity project.";
                    return false;
                }

                resolved = candidate;
                return true;
            }
            catch (Exception e)
            {
                error = $"Invalid Game View recording output path: {e.Message}";
                return false;
            }
        }

        /// <summary>
        /// Unity's native MediaEncoder re-opens the MP4 during Dispose to rewrite the track
        /// timestamps, and that pass fails (WindowsVideoMedia 0x8000ffff, "Erase timestamps")
        /// when the path contains non-ASCII characters. In that case the encoder targets a
        /// staging file under %TEMP%; FinalizeMp4Recording moves it into place after Dispose
        /// succeeds, since System.IO handles Unicode paths correctly.
        /// </summary>
        private static string ResolveMp4EncoderPath(string outputPath)
        {
#if UNITY_EDITOR_WIN
            if (outputPath.Any(c => c > 127))
                return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4");
#endif
            return outputPath;
        }

        private static void DeleteMp4SessionFiles(Mp4RecordingSession session)
        {
            if (!string.IsNullOrEmpty(session.OutputPath) && File.Exists(session.OutputPath))
                File.Delete(session.OutputPath);
            if (!string.IsNullOrEmpty(session.EncoderPath) &&
                !string.Equals(session.EncoderPath, session.OutputPath, StringComparison.Ordinal) &&
                File.Exists(session.EncoderPath))
                File.Delete(session.EncoderPath);
        }

        private static void CleanupMp4Recording(bool preserveOutput)
        {
            EditorApplication.update -= TickGameViewMp4Recording;
            Mp4RecordingSession session = s_Mp4Recording;
            s_Mp4Recording = null;
            if (session == null) return;

            DisposeMp4EncoderBestEffort(session);
            if (!preserveOutput)
            {
                try
                {
                    DeleteMp4SessionFiles(session);
                }
                catch { }
            }
        }

        // ==================== Incremental in-memory GIF capture ====================
        //
        // A stateful, frame-by-frame Game View capture API for callers OUTSIDE the command
        // pipeline (other editor modules call these directly — they are NOT routed through
        // HandleCommand). Unlike CaptureFramesToGif, which owns its own frame pacing inside a
        // coroutine, here the CALLER decides when each frame is taken: one Texture2D is grabbed
        // from the Game View per call and held in memory until EndCaptureToGif encodes the whole
        // sequence into an animated .gif on disk.
        //
        // Typical usage (all calls must be on the main thread; e.g. once per editor tick):
        //   ManageScreenshot.BeginCapture();                          // starts a session + grabs frame 0
        //   ManageScreenshot.CaptureFrame();                          // frame 1
        //   ManageScreenshot.CaptureFrame();                          // frame 2 …
        //   string path = ManageScreenshot.EndCaptureToGif("Clip.gif"); // encode + write, returns full path
        //
        // Per-frame GIF display durations are derived from the real wall-clock interval between
        // successive captures, so playback tracks the caller's actual pacing.
        //
        // Not thread-safe and not reentrant: only one session may be active at a time. Calling
        // BeginCapture while a session is already open discards the previous (uncaptured) frames.

        private static List<Texture2D> s_SessionFrames;
        private static List<double>    s_SessionTimes;   // EditorApplication.timeSinceStartup per frame
        private static float           s_SessionScale = 1f;
        private static bool            s_SessionActive;

        /// <summary>True while a BeginCapture session is open (awaiting more frames / a save).</summary>
        public static bool IsCapturing => s_SessionActive;

        /// <summary>Number of frames captured in the current session (0 when none active).</summary>
        public static int CapturedFrameCount => s_SessionFrames?.Count ?? 0;

        /// <summary>
        /// Starts a new capture session and immediately grabs the first Game View frame into memory.
        /// </summary>
        /// <param name="scale">Resolution scale (0.25–1.0) applied to every captured frame to keep
        /// the GIF small; 1.0 = full Game View resolution.</param>
        /// <returns>True if the first frame was captured; false if no Game View frame was available.</returns>
        public static bool BeginCapture(float scale = 1f)
        {
            if (s_SessionActive)
            {
                CodelyLogger.LogWarning("[ManageScreenshot] BeginCapture called while a session was already active; discarding the previous session.");
                DisposeSession();
            }

            s_SessionScale  = Mathf.Clamp(scale, 0.25f, 1f);
            s_SessionFrames = new List<Texture2D>();
            s_SessionTimes  = new List<double>();
            s_SessionActive = true;

            return CaptureFrame();
        }

        /// <summary>
        /// Captures one more Game View frame into the active session's in-memory buffer.
        /// </summary>
        /// <returns>True if a frame was captured; false when no session is active or the Game
        /// View produced nothing.</returns>
        public static bool CaptureFrame()
        {
            if (!s_SessionActive)
            {
                CodelyLogger.LogWarning("[ManageScreenshot] CaptureFrame called with no active session. Call BeginCapture first.");
                return false;
            }

            Texture2D tex = null;
            try
            {
                tex = CaptureGameViewTexture();
                if (tex == null)
                {
                    CodelyLogger.LogWarning("[ManageScreenshot] CaptureFrame produced no texture (is a Game View open?).");
                    return false;
                }

                // ScaleTextureByFactor destroys the source and returns the scaled copy (no-op at
                // 1.0). It only destroys the source as its final step, so if it throws earlier
                // 'tex' still points at the original and is cleaned up in the catch below.
                tex = ScaleTextureByFactor(tex, s_SessionScale);
            }
            catch (Exception e)
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                CodelyLogger.LogWarning($"[ManageScreenshot] CaptureFrame failed: {e.Message}");
                return false;
            }

            s_SessionFrames.Add(tex);
            s_SessionTimes.Add(EditorApplication.timeSinceStartup);
            return true;
        }

        /// <summary>
        /// Ends the active session, encodes all buffered frames into an animated .gif, writes it to
        /// disk, and frees the in-memory frames.
        /// </summary>
        /// <param name="filePath">Destination path. If it has no directory it is written under
        /// &lt;ProjectRoot&gt;/screenshots; a missing ".gif" extension is appended. Pass null/empty
        /// for an auto-named file in the screenshots folder.</param>
        /// <param name="fps">Nominal playback frames per second (used for the final frame's delay
        /// and clamped to 1–50).</param>
        /// <param name="colorCount">GIF palette size (2–256).</param>
        /// <returns>The full path of the written file, or null if nothing was captured / on error.</returns>
        public static string EndCaptureToGif(string filePath, int fps = 25, int colorCount = 128)
        {
            if (!s_SessionActive)
            {
                CodelyLogger.LogWarning("[ManageScreenshot] EndCaptureToGif called with no active session.");
                return null;
            }

            // Detach the session state up front so an early return / exception cannot leave a
            // half-open session behind; the frames are disposed in the finally below.
            List<Texture2D> frames = s_SessionFrames;
            List<double>    times  = s_SessionTimes;
            s_SessionActive = false;
            s_SessionFrames = null;
            s_SessionTimes  = null;

            try
            {
                if (frames.Count == 0)
                {
                    CodelyLogger.LogWarning("[ManageScreenshot] EndCaptureToGif: no frames captured; nothing written.");
                    return null;
                }

                fps        = Mathf.Clamp(fps, 1, 50);
                colorCount = Mathf.Clamp(colorCount, 2, 256);
                int nominalDelayCs = Mathf.Max(1, Mathf.RoundToInt(100f / fps));

                // Each frame's display duration (1/100 s) is the real interval that elapsed
                // before the NEXT frame was taken; the last frame uses the nominal fps interval.
                List<int> delaysCs = new List<int>(frames.Count);
                for (int i = 1; i < frames.Count; i++)
                {
                    int gapCs = Mathf.Max(1, Mathf.RoundToInt((float)((times[i] - times[i - 1]) * 100.0)));
                    delaysCs.Add(gapCs);
                }
                delaysCs.Add(nominalDelayCs);

                // GIF frames must share dimensions; drop any odd-sized frame (e.g. a Game View
                // resize mid-capture), keeping the delay list aligned.
                int w = frames[0].width, h = frames[0].height;
                List<Texture2D> usable      = new List<Texture2D>(frames.Count);
                List<int>       usableDelay = new List<int>(frames.Count);
                for (int i = 0; i < frames.Count; i++)
                {
                    Texture2D f = frames[i];
                    if (f == null) continue;
                    if (f.width == w && f.height == h)
                    {
                        usable.Add(f);
                        usableDelay.Add(i < delaysCs.Count ? delaysCs[i] : nominalDelayCs);
                    }
                    else
                    {
                        CodelyLogger.LogWarning("[ManageScreenshot] Dropping session GIF frame with mismatched size.");
                    }
                }
                if (usable.Count == 0) { usable.Add(frames[0]); usableDelay.Add(nominalDelayCs); }

                byte[] bytes = GifEncoder.Encode(usable, colorCount, usableDelay);

                // Resolve destination directory + filename.
                string defaultDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "screenshots");
                string dir, fileName;
                if (string.IsNullOrEmpty(filePath))
                {
                    dir      = defaultDir;
                    fileName = $"Capture_{Timestamp()}.gif";
                }
                else
                {
                    if (!filePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                        filePath += ".gif";
                    dir      = Path.GetDirectoryName(filePath);
                    fileName = Path.GetFileName(filePath);
                    if (string.IsNullOrEmpty(dir)) dir = defaultDir;
                }

                Directory.CreateDirectory(dir);
                string savePath = Path.Combine(dir, fileName);
                File.WriteAllBytes(savePath, bytes);

                CodelyLogger.Log($"[ManageScreenshot] Saved session GIF: {savePath} ({usable.Count} frames @ {fps}fps)");
                return savePath;
            }
            catch (Exception e)
            {
                CodelyLogger.LogError($"[ManageScreenshot] EndCaptureToGif failed: {e}");
                return null;
            }
            finally
            {
                foreach (Texture2D f in frames)
                    if (f != null) UnityEngine.Object.DestroyImmediate(f);
            }
        }

        /// <summary>
        /// Aborts the active session (if any) without writing a file, freeing all buffered frames.
        /// </summary>
        public static void AbortCapture()
        {
            if (!s_SessionActive) return;
            CodelyLogger.Log($"[ManageScreenshot] Capture session aborted ({CapturedFrameCount} frames discarded).");
            DisposeSession();
        }

        private static void DisposeSession()
        {
            if (s_SessionFrames != null)
                foreach (Texture2D f in s_SessionFrames)
                    if (f != null) UnityEngine.Object.DestroyImmediate(f);

            s_SessionFrames = null;
            s_SessionTimes  = null;
            s_SessionActive = false;
        }

        // ======================== capture_game_view ========================
        // Parameters:
        //   over_time (string) – "N" or "NxS": capture N frames, yielding S editor ticks between each.
        //   path, filename
        private static IEnumerator CaptureGameViewCoroutine(JObject p, Action<object> setResult)
        {
            string customPath = p["path"]?.ToString();
            string customFile = p["filename"]?.ToString();
            float  scale      = ParseScale(p);
            GifParams gif     = ParseGifParams(p);

            // Legacy Game View still frames stay available so older clients keep working.
            // New clients should not call this action; they record an MP4 instead.

            // --- GIF capture: record N frames and encode an animated .gif ---
            if (gif != null)
            {
                IEnumerator gifCo = CaptureFramesToGif(
                    gif,
                    () => { Texture2D t = CaptureGameViewTexture(); return t == null ? null : ScaleTextureByFactor(t, scale); },
                    customPath, customFile, "GameView", setResult,
                    beforeFrame: EnsureGameViewFresh);
                while (gifCo.MoveNext()) yield return gifCo.Current;
                yield break;
            }

            OverTimeParams ot = ParseOverTime(p["over_time"]);
            List<Texture2D> captures = new List<Texture2D>();

            for (int i = 0; i < ot.Count; i++)
            {
                if (i > 0)
                {
                    int steps = Math.Max(1, ot.StepsPerInterval);
                    for (int s = 0; s < steps; s++)
                        yield return null;
                }

                IEnumerator fresh = EnsureGameViewFresh();
                while (fresh.MoveNext()) yield return fresh.Current;

                Texture2D tex = CaptureGameViewTexture();
                if (tex == null) continue;
                tex = ScaleTextureByFactor(tex, scale);
                if (ot.Count > 1) DrawLabelOnTexture(tex, FrameLabel(i));
                captures.Add(tex);
            }

            if (captures.Count == 0)
                setResult(Response.Error("Failed to capture Game View. Ensure a Game View window exists."));
            else
                setResult(BuildImageResponse(StitchOrSingle(captures), customPath, customFile, "GameView"));
        }

        // ======================== capture_scene_view ========================
        private static IEnumerator CaptureSceneViewCoroutine(JObject p, Action<object> setResult)
        {
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null) { setResult(Response.Error("No active Scene View found. Please ensure a Scene View is open.")); yield break; }
            Camera sceneCam = sv.camera;
            if (sceneCam == null) { setResult(Response.Error("Scene View camera not found.")); yield break; }

            int    width      = p["width"]?.ToObject<int?>()  ?? 960;
            int    height     = p["height"]?.ToObject<int?>() ?? 540;
            string customPath = p["path"]?.ToString();
            string customFile = p["filename"]?.ToString();
            string viewParam  = p["view"]?.ToString()?.ToLower() ?? "current";
            bool   orthographic = p["orthographic"]?.ToObject<bool?>() ?? false;
            DrawCameraMode drawMode = ParseSceneViewDrawMode(sv, p["render_mode"]);
            GifParams gif     = ParseGifParams(p);
            OverTimeParams ot = ParseOverTime(p["over_time"]);

            Bounds? selBounds = null;
            string captureWarning = null;
            JObject focusObjEntry = p["focus_object"] as JObject;
            if (focusObjEntry != null)
            {
                string objPath  = focusObjEntry["focusobject"]?.ToString();
                int    objIndex = focusObjEntry["index"]?.ToObject<int?>() ?? 0;
                if (!string.IsNullOrEmpty(objPath))
                {
                    GameObject go = FindGameObjectByPath(objPath, objIndex);
                    if (go != null)
                    {
                        selBounds = CalculateSingleObjectBounds(go);
                        sv.Frame(selBounds.Value, true);
                    }
                    else
                    {
                        captureWarning = $"focus_object not found: path='{objPath}' index={objIndex}. Falling back to scene bounds.";
                        CodelyLogger.LogWarning($"[ManageScreenshot] {captureWarning}");
                    }
                }
            }

            List<string>    views    = GetViewList(viewParam);
            List<Texture2D> captures = new List<Texture2D>();
            Quaternion origRot   = sv.rotation;
            bool       origOrtho = sv.orthographic;
            Vector3    origCamPosition = sceneCam.transform.position;
            Quaternion origCamRotation = sceneCam.transform.rotation;
            bool       origCamOrtho = sceneCam.orthographic;
            float      origCamOrthoSize = sceneCam.orthographicSize;
            Bounds captureBounds = selBounds ?? GetSceneBounds();

            try
            {
                // --- GIF capture: record N frames over time ---
                // Each frame renders every requested view and stitches them into a single
                // grid texture (same layout as a still multi-view capture), so a multi-view
                // GIF animates the whole grid. A single view yields an ungridded frame.
                if (gif != null)
                {
                    bool multiView = views.Count > 1;
                    IEnumerator gifCo = CaptureFramesToGif(
                        gif,
                        () =>
                        {
                            List<Texture2D> viewTextures = new List<Texture2D>(views.Count);
                            foreach (string view in views)
                            {
                                if (view == "current")
                                {
                                    sceneCam.transform.SetPositionAndRotation(origCamPosition, origCamRotation);
                                    sceneCam.orthographic     = origCamOrtho;
                                    sceneCam.orthographicSize = origCamOrthoSize;
                                }
                                else
                                {
                                    PositionCameraForView(sceneCam, captureBounds, view, orthographic);
                                }

                                string frameWarning;
                                Texture2D t = RenderSceneViewCameraToTexture(
                                    sv, sceneCam, width, height, drawMode,
                                    view == "current" ? sv.pivot : captureBounds.center,
                                    out frameWarning);
                                if (!string.IsNullOrEmpty(frameWarning))
                                {
                                    captureWarning = AppendWarning(captureWarning, frameWarning);
                                    CodelyLogger.LogWarning($"[ManageScreenshot] {frameWarning}");
                                }
                                if (t == null) continue;
                                if (multiView) DrawLabelOnTexture(t, ViewAbbreviation(view));
                                viewTextures.Add(t);
                            }
                            // StitchOrSingle merges the per-view textures into one frame and
                            // destroys the inputs; ownership of the result transfers to the GIF.
                            return viewTextures.Count == 0 ? null : StitchOrSingle(viewTextures);
                        },
                        customPath, customFile, "SceneView", setResult);
                    while (gifCo.MoveNext()) yield return gifCo.Current;
                    yield break;
                }

                for (int ti = 0; ti < ot.Count; ti++)
                {
                    if (ti > 0)
                    {
                        int steps = Math.Max(1, ot.StepsPerInterval);
                        for (int s = 0; s < steps; s++)
                            yield return null;
                    }

                    foreach (string view in views)
                    {
                        if (view == "current")
                        {
                            sceneCam.transform.SetPositionAndRotation(origCamPosition, origCamRotation);
                            sceneCam.orthographic     = origCamOrtho;
                            sceneCam.orthographicSize = origCamOrthoSize;
                        }
                        else
                        {
                            PositionCameraForView(sceneCam, captureBounds, view, orthographic);
                        }

                        string frameWarning;
                        Texture2D tex = RenderSceneViewCameraToTexture(
                            sv, sceneCam, width, height, drawMode,
                            view == "current" ? sv.pivot : captureBounds.center,
                            out frameWarning);
                        if (!string.IsNullOrEmpty(frameWarning))
                        {
                            captureWarning = AppendWarning(captureWarning, frameWarning);
                            CodelyLogger.LogWarning($"[ManageScreenshot] {frameWarning}");
                        }
                        if (tex != null)
                        {
                            tex = MaybeAutoScale(p, tex);
                            bool multiView = views.Count > 1;
                            bool multiTime = ot.Count > 1;
                            if (multiView && multiTime)       DrawLabelOnTexture(tex, ViewAbbreviation(view) +"-"+ FrameLabel(ti));
                            else if (multiView)              DrawLabelOnTexture(tex, ViewAbbreviation(view));
                            else if (multiTime)              DrawLabelOnTexture(tex, FrameLabel(ti));
                            captures.Add(tex);
                        }
                    }
                }
            }
            finally
            {
                sceneCam.transform.SetPositionAndRotation(origCamPosition, origCamRotation);
                sceneCam.orthographic = origCamOrtho;
                sceneCam.orthographicSize = origCamOrthoSize;
                sv.rotation     = origRot;
                sv.orthographic = origOrtho;
            }

            if (captures.Count == 0)
                setResult(Response.Error("Failed to capture any Scene View screenshots."));
            else
                setResult(BuildImageResponse(StitchOrSingle(captures), customPath, customFile, "SceneView", captureWarning));
        }

        // ======================== capture_asset ========================
        // Instantiates one or more assets, frames them with a temporary camera, renders one or
        // more views, stitches the result into a grid, and destroys all temporary objects.
        //
        // Supported asset types: GameObject/Prefab, Mesh, Material, Texture2D, Sprite.
        //
        // Parameters:
        //   assets (string[], required) – Asset paths (e.g. "Assets/Prefabs/Hero.prefab").
        //   scene        – Path to a .unity scene file (e.g. "Assets/Scenes/Preview.unity").
        //                  When provided the scene is opened additively, assets are instantiated
        //                  into it, and the scene is closed and removed when capture finishes.
        //                  Without this parameter assets are staged far from the origin in the
        //                  currently active scene.
        //   view         – Direction(s) to render; defaults to "cardinal".
        //   orthographic – bool, default false.
        //   width, height, path, filename
        private static object CaptureAsset(JObject p)
        {
            if (EditorApplication.isPlaying)
                return Response.Error("capture_asset requires Edit Mode. Exit Play Mode first.");

            if (ParseGifParams(p) != null)
                return Response.Error("as_gif is only supported by capture_game_view, capture_main_camera and capture_specific_camera.");

            JArray assetsArray = p["assets"] as JArray;
            if (assetsArray == null || assetsArray.Count == 0)
                return Response.Error("'assets' must be a non-empty array of asset paths.");

            Camera svCam   = SceneView.lastActiveSceneView?.camera;
            int    width   = p["width"]?.ToObject<int?>()  ?? svCam?.pixelWidth  ?? 960;
            int    height  = p["height"]?.ToObject<int?>() ?? svCam?.pixelHeight ?? 540;
            string customPath  = p["path"]?.ToString();
            string customFile  = p["filename"]?.ToString();
            string viewParam   = p["view"]?.ToString()?.ToLower() ?? "cardinal";
            bool   orthographic= p["orthographic"]?.ToObject<bool?>() ?? false;
            string scenePath   = p["scene"]?.ToString();

            // --- Open target scene additively (if requested) ---
            UnityEngine.SceneManagement.Scene originalScene =
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            UnityEngine.SceneManagement.Scene? targetScene = null;

            if (!string.IsNullOrEmpty(scenePath))
            {
                string sceneRel = NormalizePath(scenePath);
                if (string.IsNullOrEmpty(sceneRel) ||
                    (!sceneRel.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) 
                    && !sceneRel.EndsWith(".scene", StringComparison.OrdinalIgnoreCase)))
                    return Response.Error($"Invalid scene path: '{scenePath}'. Must be a .unity file.");

                try
                {
                    var opened = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                        sceneRel, UnityEditor.SceneManagement.OpenSceneMode.Additive);
                    if (!opened.IsValid())
                        return Response.Error($"Failed to open scene: '{scenePath}'.");
                    targetScene = opened;
                    UnityEditor.SceneManagement.EditorSceneManager.SetActiveScene(opened);
                }
                catch (Exception e)
                {
                    return Response.Error($"Failed to open scene '{scenePath}': {e.Message}");
                }
            }

            // Staging area far from origin so instantiated objects don't interfere
            Vector3 stageOrigin = new Vector3(50000f, 50000f, 50000f);

            List<GameObject> instantiated = new List<GameObject>();
            GameObject tempCamGO = null;

            try
            {
                // --- Instantiate assets ---
                float xCursor = 0f;
                foreach (var token in assetsArray)
                {
                    string assetPath = token.ToString();
                    try
                    {
                        string rel = NormalizePath(assetPath);
                        if (string.IsNullOrEmpty(rel)) continue;

                        Type assetType = AssetDatabase.GetMainAssetTypeAtPath(rel);
                        if (assetType == null) continue;

                        GameObject obj = InstantiateAsset(rel, assetType);
                        if (obj == null) continue;

                        instantiated.Add(obj);

                        Bounds objBounds = CalculateSingleObjectBounds(obj);
                        obj.transform.position = stageOrigin + new Vector3(xCursor + objBounds.extents.x, 0f, 0f);
                        xCursor += objBounds.size.x + 1f;
                    }
                    catch (Exception e)
                    {
                        CodelyLogger.LogWarning($"[ManageScreenshot] Failed to instantiate '{assetPath}': {e.Message}");
                    }
                }

                if (instantiated.Count == 0)
                    return Response.Error("No assets could be instantiated. Verify the asset paths are correct.");

                // --- Set up temp camera ---
                tempCamGO = new GameObject("__CodelyAssetPreviewCam__");
                Camera cam = tempCamGO.AddComponent<Camera>();
                cam.enabled         = false;
               
                cam.cullingMask     = -1;
                cam.nearClipPlane   = 0.01f;
                cam.farClipPlane    = 10000f;
                cam.fieldOfView     = 45f;
                cam.orthographic    = orthographic;

                foreach (GameObject go in instantiated)
                    foreach (Canvas canvas in go.GetComponentsInChildren<Canvas>())
                    {
                        canvas.renderMode  = RenderMode.WorldSpace;
                        canvas.worldCamera = cam;
                    }

                // --- Render views ---
                Bounds overallBounds = CalculateBounds(instantiated);
                List<string>    views    = GetViewList(viewParam);
                List<Texture2D> captures = new List<Texture2D>();

                foreach (string view in views)
                {
                    PositionCameraForView(cam, overallBounds, view, orthographic);
                    Texture2D tex = RenderCameraToTexture(cam, width, height);
                    if (tex != null)
                    {
                        tex = MaybeAutoScale(p, tex);
                        if (views.Count > 1)
                            DrawLabelOnTexture(tex, ViewAbbreviation(view));
                        captures.Add(tex);
                    }
                }

                if (captures.Count == 0)
                    return Response.Error("Failed to render asset preview.");

                Texture2D result = StitchOrSingle(captures);
                return BuildImageResponse(result, customPath, customFile, "AssetPreview");
            }
            finally
            {
                foreach (GameObject go in instantiated)
                {
                    if (go == null) continue;
                    // Destroy runtime materials created by InstantiateAsset (not project assets)
                    foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
                        if (r.sharedMaterial != null && !AssetDatabase.Contains(r.sharedMaterial))
                            UnityEngine.Object.DestroyImmediate(r.sharedMaterial);
                    UnityEngine.Object.DestroyImmediate(go);
                }
                if (tempCamGO != null) UnityEngine.Object.DestroyImmediate(tempCamGO);

                // Close the additively opened scene and restore the original active scene
                if (targetScene.HasValue && targetScene.Value.IsValid())
                {
                    UnityEditor.SceneManagement.EditorSceneManager.SetActiveScene(originalScene);
                    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(targetScene.Value, true);
                }
            }
        }

        // ======================== capture_ui_toolkit ========================
        // Renders a UIDocument (.uxml file) to a RenderTexture via UIElements PanelSettings.
        // Note: UIElements rendering is driven by the Unity player loop. This method forces
        // an immediate panel repaint via reflection; the first call may yield a blank image if
        // no prior editor frame has processed the panel. Running the command a second time
        // (after at least one editor update) should produce the correct result.
        //
        // Parameters:
        //   document_path (string, required) – Path to the .uxml asset (e.g. "Assets/UI/HUD.uxml").
        //   width, height – Defaults to the main GameView size or 1920×1080.
        //   format        – "png" (default, preserves alpha) or "jpg".
        //   path, filename

        private static IEnumerator CaptureUIToolkitCoroutine(JObject p, Action<object> setResult)
        {
#if UNITY_2021_2_OR_NEWER
            if (EditorApplication.isPlaying)
            {
                CodelyLogger.LogError("capture_ui_toolkit requires Edit Mode. Exit Play Mode first.");
                setResult(Response.Error("capture_ui_toolkit requires Edit Mode. Exit Play Mode first."));
                yield break;
            }

            string docPath = p["document_path"]?.ToString();
            if (string.IsNullOrEmpty(docPath))
            {
                setResult(Response.Error("'document_path' parameter is required."));
                yield break;
            }

            string rel = NormalizePath(docPath);
            if (string.IsNullOrEmpty(rel))
            {
                setResult(Response.Error($"Invalid document_path: '{docPath}'."));
                yield break;
            }

            string customPath = p["path"]?.ToString();
            string customFile = p["filename"]?.ToString();

            Vector2 gameViewSize = Handles.GetMainGameViewSize();
            int width  = p["width"]?.ToObject<int?>()  ?? Math.Min((int)gameViewSize.x, 960);
            int height = p["height"]?.ToObject<int?>() ?? Math.Min((int)gameViewSize.y, 540);

            // Force reimport so any unsaved changes to the uxml are picked up
            if (AssetDatabase.GetMainAssetTypeAtPath(rel) == typeof(VisualTreeAsset))
                AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceSynchronousImport);

            VisualTreeAsset vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(rel);
            if (vta == null)
            {
                setResult(Response.Error($"UIDocument asset not found at: '{docPath}'."));
                yield break;
            }

            PanelSettings ps = null;
            RenderTexture rt = null;
            GameObject    go = null;

            try
            {
                ps                   = ScriptableObject.CreateInstance<PanelSettings>();
                ps.hideFlags         = HideFlags.HideAndDontSave;
                // ConstantPixelSize + scale=1 + matching DPI keeps the panel's resolved
                // layout size equal to the RT dimensions regardless of the editor's HiDPI
                // setting. Without this, on HiDPI displays the panel may lay out at half
                // size or report a 0x0 logical viewport and render nothing.
                ps.scaleMode         = PanelScaleMode.ConstantPixelSize;
                ps.scale             = 1f;
                ps.referenceDpi      = 96f;
                ps.fallbackDpi       = 96f;
                ps.clearColor        = true;
                // Must clear depth too — otherwise stale depth values from previous
                // retry iterations cause subsequent UI quads to be depth-rejected and
                // the texture stays blank.
                ps.clearDepthStencil = true;
                ps.colorClearValue   = Color.clear;

                rt           = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                rt.hideFlags = HideFlags.HideAndDontSave;
                rt.Create();
                ps.targetTexture = rt;

                go = new GameObject("__CodelyTempUIDocument__");
                // DontSave (not HideAndDontSave): the GameObject must be a regular
                // active scene member so the runtime panel system registers its panel
                // for off-screen rendering.
                go.hideFlags = HideFlags.DontSave;

                UIDocument uiDoc = go.AddComponent<UIDocument>();
                uiDoc.panelSettings   = ps;
                uiDoc.visualTreeAsset = vta;

                ThemeStyleSheet theme = GetUIBuilderTheme(rel) ?? GetDefaultRuntimeTheme();
                if (theme != null)
                    ps.themeStyleSheet = theme;

                // The root element inherits its size from the panel, which in edit mode
                // may resolve to 0x0 before the first layout tick. Pin it to the RT size
                // so the very first paint produces a non-empty image.
                VisualElement root = uiDoc.rootVisualElement;
                if (root != null)
                {
                    root.style.position = Position.Absolute;
                    root.style.left     = 0;
                    root.style.top      = 0;
                    root.style.width    = width;
                    root.style.height   = height;
                }
            }
            catch (Exception e)
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
                if (ps != null) UnityEngine.Object.DestroyImmediate(ps);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
                setResult(Response.Error($"Failed to set up UIDocument: {e.Message}"));
                yield break;
            }

            UIDocument uiDocRef = go.GetComponent<UIDocument>();

            // Kick the runtime panel system immediately so the panel runs its first
            // layout pass and produces a non-empty image before we start sampling.
            TryForceUIDocumentRepaint(uiDocRef);

            // In edit mode, runtime panels do not get repainted automatically — neither
            // the player loop nor UIElementsRuntimeUtility.RepaintOverlayPanels() will
            // flush draw commands to a targetTexture-bound PanelSettings. The actual
            // work is done by TryForceUIDocumentRepaint, which on Unity 2021/2022 calls
            // the panel's Repaint(Event) and on Unity 6 additionally calls the panel's
            // parameterless Render() to synchronously flush UIR draws on this thread
            // (no foreground / player-loop dependency). The QueuePlayerLoopUpdate /
            // RepaintAllViews calls below give Yoga's layout pass a chance to settle
            // between paints (useful when stylesheets reference assets that load async).
            for (int i = 0; i < 10; i++)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                InternalEditorUtility.RepaintAllViews();
                yield return null;
                TryForceUIDocumentRepaint(uiDocRef);
            }

            const int maxRetry = 60;
            for (int i = 0; i < maxRetry && IsRenderTextureBlack(rt); i++)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                InternalEditorUtility.RepaintAllViews();
                yield return null;
                TryForceUIDocumentRepaint(uiDocRef);
            }

            try
            {
                Texture2D tex = ReadRenderTexture(rt, width, height);
                if (tex == null)
                    setResult(Response.Error("Failed to read UIDocument render texture."));
                else
                {
                    tex = MaybeAutoScale(p, tex);
                    setResult(BuildImageResponse(tex, customPath, customFile, "UIToolkit"));
                }
            }
            catch (Exception e)
            {
                setResult(Response.Error($"Failed to capture UIDocument: {e.Message}"));
            }
            finally
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
                if (ps != null) UnityEngine.Object.DestroyImmediate(ps);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
            }
#else
            setResult(Response.Error("capture_ui_toolkit requires Unity 2021.2 or newer."));
            yield break;
#endif
        }

        // ==================== Shared helpers ====================

        // Shared implementation for capture_main_camera and capture_specific_camera.
        // Always renders from the camera's current position/rotation/projection.
        // Supports over_time: captures across multiple simulation frames.
        private static IEnumerator CaptureFromCameraCoroutine(Camera cam, JObject p, string label, Action<object> setResult)
        {
            int    width      = p["width"]?.ToObject<int?>()  ?? cam?.pixelWidth ?? 960;
            int    height     = p["height"]?.ToObject<int?>() ?? cam?.pixelHeight ?? 540;
            string customPath = p["path"]?.ToString();
            string customFile = p["filename"]?.ToString();
            GifParams gif     = ParseGifParams(p);

            if (gif == null && !ValidateStaticPlayModeCapture(p, setResult))
                yield break;

            // --- GIF capture: record N frames and encode an animated .gif ---
            if (gif != null)
            {
                IEnumerator gifCo = CaptureFramesToGif(
                    gif,
                    () => RenderCameraToTexture(cam, width, height),
                    customPath, customFile, label, setResult);
                while (gifCo.MoveNext()) yield return gifCo.Current;
                yield break;
            }

            OverTimeParams ot = ParseOverTime(p["over_time"]);

            RenderTexture origRT = cam.targetTexture;
            List<Texture2D> captures = new List<Texture2D>();

            try
            {
                for (int ti = 0; ti < ot.Count; ti++)
                {
                    if (ti > 0)
                    {
                        int steps = Math.Max(1, ot.StepsPerInterval);
                        for (int s = 0; s < steps; s++)
                            yield return null;
                    }

                    Texture2D tex = RenderCameraToTexture(cam, width, height);
                    if (tex != null)
                    {
                        tex = MaybeAutoScale(p, tex);
                        if (ot.Count > 1) DrawLabelOnTexture(tex, FrameLabel(ti));
                        captures.Add(tex);
                    }
                }
            }
            finally
            {
                cam.targetTexture = origRT;
            }

            if (captures.Count == 0)
                setResult(Response.Error($"Failed to capture from camera '{label}'."));
            else
                setResult(BuildImageResponse(StitchOrSingle(captures), customPath, customFile, label));
        }

        private static Camera FindCameraByName(string name)
        {
            var go = GameObject.Find(name);
            Camera cam = go?.GetComponent<Camera>();
            if (cam != null) return cam;
#if UNITY_6000_5_OR_NEWER
            foreach (Camera c in UnityEngine.Object.FindObjectsByType<Camera>())
#elif UNITY_2022_2_OR_NEWER
            foreach (Camera c in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
#else
            foreach (Camera c in UnityEngine.Object.FindObjectsOfType<Camera>())
#endif
                if (c.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        // ==================== Scene View editor-camera capture ====================

        private static DrawCameraMode ParseSceneViewDrawMode(SceneView sceneView, JToken token)
        {
            if (token == null || string.IsNullOrWhiteSpace(token.ToString()))
                return sceneView.cameraMode.drawMode;

            string raw = token.ToString().Trim().ToLower().Replace("_", "").Replace("-", "");
            switch (raw)
            {
                case "wireframe":       return DrawCameraMode.Wireframe;
                case "shadedwireframe": return DrawCameraMode.TexturedWire;
                default:                return DrawCameraMode.Textured;
            }
        }

        private static CaptureRenderMode ToFallbackRenderMode(DrawCameraMode drawMode)
        {
            if (drawMode == DrawCameraMode.Wireframe)
                return CaptureRenderMode.Wireframe;
            if (drawMode == DrawCameraMode.TexturedWire)
                return CaptureRenderMode.ShadedWireframe;
            return CaptureRenderMode.Shaded;
        }

        private static string AppendWarning(string existing, string next)
        {
            if (string.IsNullOrEmpty(next)) return existing;
            if (string.IsNullOrEmpty(existing)) return next;
            if (existing.Contains(next)) return existing;
            return existing + " " + next;
        }

        private static string Timestamp() =>
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");

        private static bool TryDrawSceneViewSelection(out string warning)
        {
            warning = null;
            if (Selection.gameObjects == null || Selection.gameObjects.Length == 0)
                return true;

            const BindingFlags flags =
                BindingFlags.Static | BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                Assembly editorAssembly = typeof(SceneView).Assembly;
                Type annotationType = editorAssembly.GetType("UnityEditor.AnnotationUtility");
                Type outlineModeType = editorAssembly.GetType("UnityEditor.OutlineDrawMode");
                if (annotationType == null || outlineModeType == null)
                    throw new MissingMemberException(
                        "Selection annotation types are unavailable.");

                int outlineMode = 0;
                PropertyInfo showOutline = annotationType.GetProperty(
                    "showSelectionOutline", flags);
                PropertyInfo showWire = annotationType.GetProperty(
                    "showSelectionWire", flags);
                if (showOutline != null && (bool)showOutline.GetValue(null))
                    outlineMode |= 1;
                if (showWire != null && (bool)showWire.GetValue(null))
                    outlineMode |= 2;
                if (outlineMode == 0)
                    return true;

                MethodInfo filterInstanceIds = typeof(HandleUtility).GetMethod(
                    "FilterInstanceIDs", flags);
                MethodInfo drawOutline = typeof(Handles).GetMethod(
                    "DrawOutlineOrWireframeInternal", flags);
                if (filterInstanceIds == null || drawOutline == null)
                    throw new MissingMethodException(
                        "Selection outline methods are unavailable.");

                object[] filterArgs = { Selection.gameObjects, null, null };
                filterInstanceIds.Invoke(null, filterArgs);
                int[] parentRenderers = filterArgs[1] as int[];
                int[] childRenderers = filterArgs[2] as int[];

                Color parentColor = SceneView.selectedOutlineColor;
                Color childColor = parentColor;
                FieldInfo childColorField = typeof(SceneView).GetField(
                    "kSceneViewSelectedChildrenOutline", flags);
                object prefColor = childColorField?.GetValue(null);
                PropertyInfo colorProperty = prefColor?.GetType().GetProperty(
                    "Color", flags);
                if (colorProperty != null)
                    childColor = (Color)colorProperty.GetValue(prefColor);

                drawOutline.Invoke(
                    null,
                    new object[]
                    {
                        parentColor,
                        childColor,
                        1f,
                        parentRenderers,
                        childRenderers,
                        Enum.ToObject(outlineModeType, outlineMode),
                        true,
                    });
                return true;
            }
            catch (Exception e)
            {
                Exception actual =
                    e is TargetInvocationException && e.InnerException != null
                        ? e.InnerException
                        : e;
                warning =
                    $"Selection outline rendering was unavailable ({actual.Message}).";
                return false;
            }
        }

        private const string CaptureSceneViewTitle = "__CodelySceneViewCapture__";
        private static SceneView s_CaptureSceneView;

        [InitializeOnLoadMethod]
        private static void ScheduleStaleCaptureWindowCleanup()
        {
            if (EditorAutomationGuard.ShouldSkipBridgeAutoStart()) return;

            AssemblyReloadEvents.beforeAssemblyReload -= CleanupRecordingBeforeDomainExit;
            AssemblyReloadEvents.beforeAssemblyReload += CleanupRecordingBeforeDomainExit;
            EditorApplication.quitting -= CleanupRecordingBeforeDomainExit;
            EditorApplication.quitting += CleanupRecordingBeforeDomainExit;

            EditorApplication.delayCall += () =>
            {
                foreach (SceneView view in Resources.FindObjectsOfTypeAll<SceneView>())
                {
                    if (view != null &&
                        view.titleContent != null &&
                        view.titleContent.text == CaptureSceneViewTitle)
                    {
                        try { view.Close(); }
                        catch { UnityEngine.Object.DestroyImmediate(view); }
                    }
                }
                s_CaptureSceneView = null;
            };
        }

        private static void CleanupRecordingBeforeDomainExit()
        {
            if (s_Mp4Recording != null)
                DiscardActiveGameViewMp4Recording();
        }

        private static MethodInfo FindInstanceMethod(Type type, string name, int parameterCount)
        {
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type current = type; current != null; current = current.BaseType)
            {
                foreach (MethodInfo method in current.GetMethods(flags))
                {
                    if (method.Name == name &&
                        method.GetParameters().Length == parameterCount)
                        return method;
                }
            }
            return null;
        }

        private static SceneView GetOrCreateCaptureSceneView(
            int width, int height, out string warning)
        {
            warning = null;
            // Never reuse a previous capture window. In particular, older versions
            // used ContainerWindow.SetInvisible(), which creates a transparent
            // topmost window that still intercepts input.
            if (s_CaptureSceneView != null)
            {
                try { s_CaptureSceneView.Close(); }
                catch { UnityEngine.Object.DestroyImmediate(s_CaptureSceneView); }
                s_CaptureSceneView = null;
            }

            foreach (SceneView existing in Resources.FindObjectsOfTypeAll<SceneView>())
            {
                if (existing != null &&
                    existing.titleContent != null &&
                    existing.titleContent.text == CaptureSceneViewTitle)
                {
                    try { existing.Close(); }
                    catch { UnityEngine.Object.DestroyImmediate(existing); }
                }
            }

            try
            {
                Assembly editorAssembly = typeof(EditorWindow).Assembly;
                Type hostViewType = editorAssembly.GetType("UnityEditor.HostView");
                Type containerWindowType = editorAssembly.GetType("UnityEditor.ContainerWindow");
                Type showModeType = editorAssembly.GetType("UnityEditor.ShowMode");
                if (hostViewType == null || containerWindowType == null ||
                    showModeType == null)
                    throw new MissingMemberException(
                        "Unity editor window host types are unavailable.");

                SceneView view = ScriptableObject.CreateInstance<SceneView>();
                view.titleContent = new GUIContent(CaptureSceneViewTitle);
                view.position = new Rect(-32000f, -32000f, width, height);
                view.minSize = new Vector2(64f, 64f);

                ScriptableObject host =
                    ScriptableObject.CreateInstance(hostViewType);
                ScriptableObject container =
                    ScriptableObject.CreateInstance(containerWindowType);

                MethodInfo setActualView = FindInstanceMethod(
                    hostViewType, "SetActualViewInternal", 2);
                PropertyInfo containerPosition = containerWindowType.GetProperty(
                    "position",
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic);
                PropertyInfo rootView = containerWindowType.GetProperty(
                    "rootView",
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic);
                MethodInfo makeSettings = FindInstanceMethod(
                    typeof(EditorWindow), "MakeParentsSettingsMatchMe", 0);
                MethodInfo show = FindInstanceMethod(
                    containerWindowType, "Show", 4);

                if (setActualView == null || containerPosition == null ||
                    rootView == null || makeSettings == null || show == null)
                    throw new MissingMemberException(
                        "Unity editor window host methods are unavailable.");

                setActualView.Invoke(host, new object[] { view, false });
                containerPosition.SetValue(
                    container,
                    new Rect(-32000f, -32000f, width, height));
                rootView.SetValue(container, host);
                makeSettings.Invoke(view, null);
                object utilityMode = Enum.Parse(showModeType, "Utility");
                show.Invoke(
                    container,
                    new object[] { utilityMode, false, false, false });

                view.position =
                    new Rect(-32000f, -32000f, width, height);
                s_CaptureSceneView = view;
            }
            catch (Exception e)
            {
                Exception actual =
                    e is TargetInvocationException && e.InnerException != null
                        ? e.InnerException
                        : e;
                warning =
                    $"Failed to create the capture Scene View ({actual.Message}).";
                return null;
            }

            return s_CaptureSceneView;
        }

        private static void CloseCaptureSceneView()
        {
            if (s_CaptureSceneView == null)
                return;
            try { s_CaptureSceneView.Close(); }
            catch
            {
                UnityEngine.Object.DestroyImmediate(s_CaptureSceneView);
            }
            finally
            {
                s_CaptureSceneView = null;
            }
        }

        /// <summary>
        /// Renders with the real SceneView camera and Unity/Tuanjie's internal
        /// editor-camera pipeline. This captures the grid, engine gizmos and
        /// selection rendering without creating another Editor window.
        /// </summary>
        private static Texture2D RenderSceneViewCameraToTexture(
            SceneView sceneView,
            Camera camera,
            int width,
            int height,
            DrawCameraMode drawMode,
            Vector3 gridPivot,
            out string warning)
        {
            warning = null;
            if (sceneView == null || camera == null || width < 1 || height < 1)
                return null;

            // When a non-shaded render mode is requested, bypass the Tuanjie/Unity internal
            // editor-camera reflection path entirely. That path passes drawMode to
            // Internal_DrawCameraWithGrid/Internal_DrawCameraWithFilter but the engine may
            // silently ignore it (no exception, just renders shaded anyway). The GL.wireframe-
            // based RenderCameraToTexture path is renderer-level and reliably produces the
            // requested mode, at the cost of losing the Scene View grid overlay and editor
            // gizmos for that capture.
            if (drawMode != DrawCameraMode.Textured)
                return RenderCameraToTexture(camera, width, height, ToFallbackRenderMode(drawMode));

            const BindingFlags instanceFlags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags staticFlags =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            Rect previousRect = camera.rect;
            RenderTexture rt = RenderTexture.GetTemporary(
                width, height, 24, RenderTextureFormat.Default);
            Texture2D result = null;
            Exception editorRenderError = null;
            bool editorCameraNeedsFinish = false;

            try
            {
                camera.targetTexture = rt;
                camera.rect = new Rect(0f, 0f, 1f, 1f);
                RenderTexture.active = rt;

                MethodInfo setupCamera = typeof(Handles).GetMethod(
                    "Internal_SetupCamera", staticFlags);
                MethodInfo clearCamera = typeof(Handles).GetMethod(
                    "Internal_ClearCamera", staticFlags);
                MethodInfo finishCamera = typeof(Handles).GetMethod(
                    "Internal_FinishDrawingCamera", staticFlags, null,
                    new[] { typeof(Camera), typeof(bool) }, null);

                if (setupCamera == null || clearCamera == null ||
                    finishCamera == null)
                    throw new MissingMethodException(
                        "Required Handles editor-camera methods are unavailable.");

                setupCamera.Invoke(null, new object[] { camera });
                clearCamera.Invoke(null, new object[] { camera });

                bool renderedWithGrid = false;
                PropertyInfo gridsProperty = typeof(SceneView).GetProperty(
                    "sceneViewGrids", instanceFlags);
                object grids = gridsProperty?.GetValue(sceneView);
                if (grids != null)
                {
                    MethodInfo prepareGrid = grids.GetType().GetMethod(
                        "PrepareGridRender", instanceFlags);
                    MethodInfo drawWithGrid = typeof(Handles).GetMethod(
                        "Internal_DrawCameraWithGrid", staticFlags);
                    if (prepareGrid != null && drawWithGrid != null)
                    {
                        float gridSize = camera.orthographic
                            ? Mathf.Max(camera.orthographicSize * 2f, 0.0001f)
                            : sceneView.size;
                        object gridParams = prepareGrid.Invoke(
                            grids,
                            new object[]
                            {
                                camera,
                                gridPivot,
                                camera.transform.rotation,
                                gridSize,
                                camera.orthographic,
                            });
                        drawWithGrid.Invoke(
                            null,
                            new object[]
                            {
                                camera,
                                drawMode,
                                gridParams,
                                sceneView.drawGizmos,
                                true,
                            });
                        renderedWithGrid = true;
                    }
                }

                if (!renderedWithGrid)
                {
                    MethodInfo drawCamera = typeof(Handles).GetMethod(
                        "Internal_DrawCameraWithFilter", staticFlags);
                    if (drawCamera == null)
                        throw new MissingMethodException(
                            "Handles.Internal_DrawCameraWithFilter is unavailable.");
                    drawCamera.Invoke(
                        null,
                        new object[]
                        {
                            camera,
                            drawMode,
                            sceneView.drawGizmos,
                            true,
                            null,
                        });
                    warning =
                        "Scene View grid API was unavailable; captured editor gizmos without the grid.";
                }

                string selectionWarning;
                if (!TryDrawSceneViewSelection(out selectionWarning))
                    warning = AppendWarning(warning, selectionWarning);

                editorCameraNeedsFinish = true;
                finishCamera.Invoke(
                    null, new object[] { camera, sceneView.drawGizmos });
                editorCameraNeedsFinish = false;
                result = ReadRenderTexture(rt, width, height);
            }
            catch (Exception e)
            {
                editorRenderError =
                    e is TargetInvocationException && e.InnerException != null
                        ? e.InnerException
                        : e;
            }
            finally
            {
                if (editorCameraNeedsFinish)
                {
                    try
                    {
                        MethodInfo finishCamera = typeof(Handles).GetMethod(
                            "Internal_FinishDrawingCamera", staticFlags, null,
                            new[] { typeof(Camera), typeof(bool) }, null);
                        finishCamera?.Invoke(
                            null,
                            new object[] { camera, sceneView.drawGizmos });
                    }
                    catch
                    {
                        // Best-effort cleanup after a partially completed render.
                    }
                }

                camera.targetTexture = previousTarget;
                camera.rect = previousRect;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);
            }

            if (result != null)
                return result;

            string detail =
                editorRenderError?.Message ?? "unknown editor-renderer failure";
            warning = AppendWarning(
                warning,
                $"Editor Scene View rendering was unavailable ({detail}); fell back to Camera.Render without full gizmos/grid.");
            return RenderCameraToTexture(
                camera, width, height, ToFallbackRenderMode(drawMode));
        }

        // ==================== Game View RT capture ====================

        // The Game View may not repaint when the editor is unfocused. A queued Repaint is
        // not sufficient in that state, so use the same synchronous native path Unity uses
        // for graphics capture: update the scene, finish GPU work, then repaint the GUIView.
        private static IEnumerator EnsureGameViewFresh()
        {
            List<EditorWindow> windows = GetPlayModeViewWindows();
            EditorWindow target = windows.Count > 0 ? windows[0] : null;
            if (target == null) yield break;

            target.Focus();

            EditorApplication.QueuePlayerLoopUpdate();
            InternalEditorUtility.RepaintAllViews();
            yield return null;

            Canvas.ForceUpdateCanvases();
            RenderGameViewImmediately(target);
            yield return null;
        }

        private static void RenderGameViewImmediately(EditorWindow window)
        {
            FieldInfo parentField = typeof(EditorWindow).GetField(
                "m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
            object guiView = parentField?.GetValue(window);
            if (guiView == null)
                throw new InvalidOperationException("Game View has no GUIView parent.");

            MethodInfo renderScene = FindInstanceMethod(
                guiView.GetType(), "RenderCurrentSceneForCapture", 0);
            MethodInfo repaintImmediately = FindInstanceMethod(
                window.GetType(), "RepaintImmediately", 0);
            if (renderScene == null || repaintImmediately == null)
                throw new MissingMethodException(
                    "GUIView capture/repaint methods are unavailable in this editor version.");

            renderScene.Invoke(guiView, null);
            // Invoke through EditorWindow so Unity verifies that this window is the
            // HostView's actual selected view before forwarding to GUIView.DoPaint().
            repaintImmediately.Invoke(window, null);
        }

        private static Texture2D CaptureGameViewTexture()
        {
            // Primary path plus fallbacks: read a play-view window's internal RenderTexture
            // via reflection. We probe every play-mode view (Game View, Device Simulator, HMI
            // Simulator, …) most-likely-live first; the first source that yields a non-black
            // frame wins. A black capture is discarded in favor of the next source, and if
            // every source is black we still return the first frame we got.
            try
            {
                Texture2D firstCapture = null; // first non-null (possibly black) capture, kept as last resort
                List<EditorWindow> windows = GetPlayModeViewWindows();
                if (windows.Count == 0)
                    CodelyLogger.LogWarning("[ManageScreenshot] No PlayModeView windows found to capture.");

                foreach (EditorWindow window in windows)
                {
                    if (window == null) continue;
                    RenderTexture srcRT = GetGameViewRT(window);
                    Texture2D tex = CaptureWindowRT(window);
                    bool black = tex == null || IsTextureBlack(tex);
                    string rtInfo = srcRT == null
                        ? "null"
                        : $"{srcRT.width}x{srcRT.height} {srcRT.format} created={srcRT.IsCreated()}";
                    string outcome = tex == null ? "no-texture" : (black ? "black" : "FRAME");
                    CodelyLogger.Log($"[ManageScreenshot] probe {window.GetType().FullName}: rt={rtInfo} -> {outcome}");
                    if (tex == null) continue;
                    if (!black) return tex;                        // a real frame — done
                    if (firstCapture == null) firstCapture = tex;  // remember, keep probing later sources
                    else UnityEngine.Object.DestroyImmediate(tex);
                }

                if (firstCapture != null) return firstCapture;
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[ManageScreenshot] GameView RT reflection failed: {e.Message}. Falling back to camera rendering.");
            }

            // Fallback: render all active cameras using the actual Game View size
            Vector2 gameViewSize = Handles.GetMainGameViewSize();
            int fw = Mathf.Max((int)gameViewSize.x, 1);
            int fh = Mathf.Max((int)gameViewSize.y, 1);

            return RenderAllCamerasToTexture(fw, fh);
        }

        // Blits a play-view window's internal RenderTexture (Game View / Simulator /
        // HMISimulator) into a CPU-side, vertically-corrected Texture2D. Returns null when
        // the window is null or exposes no readable render texture.
        private static Texture2D CaptureWindowRT(EditorWindow window)
        {
            if (window == null) return null;
            RenderTexture rt = GetGameViewRT(window);
            if (rt == null || rt.width <= 0 || rt.height <= 0) return null;

            int w = rt.width;
            int h = rt.height;
            RenderTexture tmp = RenderTexture.GetTemporary(w, h, 0, rt.format);
            Graphics.Blit(rt, tmp);
            Texture2D tex = ReadRenderTexture(tmp, w, h);
            RenderTexture.ReleaseTemporary(tmp);
            if (tex != null) FlipTextureVertically(tex);
            return tex;
        }


        // CPU-side companion to IsRenderTextureBlack (which is gated to newer Unity and reads
        // from a RenderTexture). Samples a center block of an already-read texture and reports
        // true when every sampled pixel is (near-)black. Alpha is ignored so an opaque black
        // frame — the usual "nothing rendered" case for a play view — counts as black.
        private static bool IsTextureBlack(Texture2D tex)
        {
            if (tex == null) return true;
            int sampleW = Mathf.Min(32, tex.width);
            int sampleH = Mathf.Min(32, tex.height);
            if (sampleW <= 0 || sampleH <= 0) return true;
            int startX = (tex.width  - sampleW) / 2;
            int startY = (tex.height - sampleH) / 2;

            Color[] block = tex.GetPixels(startX, startY, sampleW, sampleH);
            foreach (Color c in block)
                if (c.r > 0.01f || c.g > 0.01f || c.b > 0.01f)
                    return false;
            return true;
        }

        // Returns the play-mode view windows to probe for a rendered frame.
        //
        // Discovery is by base-type NAME, not by a resolved Type. The Device/HMI simulators are
        // built into separate editor module assemblies (UnityEditor.DeviceSimulatorModule /
        // UnityEditor.HMISimulatorModule) whose assembly-qualified names differ across
        // Unity/Tuanjie versions, so Type.GetType("…,UnityEditor") returns null for them and
        // they get skipped — the original bug. Instead we enumerate every loaded EditorWindow
        // and keep the ones whose base-type chain contains UnityEditor.PlayModeView (Game View,
        // Device Simulator, HMI Simulator, …), regardless of which assembly each lives in.
        //
        // Ordering puts the most-likely-live source first: the focused window, then simulators
        // ahead of the plain Game View (so an active simulated frame wins over a stale Game
        // View RT). The caller still discards any black frame in favor of the next source.
        private static List<EditorWindow> GetPlayModeViewWindows()
        {
            var result = new List<EditorWindow>();

            EditorWindow[] all = Resources.FindObjectsOfTypeAll<EditorWindow>();
            foreach (EditorWindow w in all)
                if (w != null && IsPlayModeView(w.GetType()))
                    result.Add(w);

            EditorWindow focused = EditorWindow.focusedWindow;
            result.Sort((a, b) => RankPlayModeView(a, focused).CompareTo(RankPlayModeView(b, focused)));
            return result;
        }

        // True when any type in the chain is the internal UnityEditor.PlayModeView base class.
        private static bool IsPlayModeView(Type t)
        {
            for (; t != null && t != typeof(object); t = t.BaseType)
                if (t.Name == "PlayModeView")
                    return true;
            return false;
        }

        // Lower rank = probed earlier. Focused window first, then simulators, then Game View.
        private static int RankPlayModeView(EditorWindow w, EditorWindow focused)
        {
            if (w == focused) return 0;
            string n = w.GetType().Name;
            if (n.IndexOf("Simulator", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (n.IndexOf("GameView", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 3;
        }

        // Reads the RenderTexture a play-mode view last rendered into. Two storage schemes exist:
        //   GameView                  -> its own private "m_RenderTexture"
        //   Device SimulatorWindow    -> base PlayModeView's private "m_TargetTexture"
        //   HMI SimulatorWindow       -> NEITHER (Tuanjie only). It bypasses base RenderView() and
        //                                renders each screen into its DeviceView.PreviewTexture,
        //                                leaving m_TargetTexture null.
        // So we first try the RT fields (walking the type hierarchy, because PlayModeView's
        // m_TargetTexture is a *private base-class* field that GetField won't surface from the
        // derived type without DeclaredOnly + climbing BaseType). If that yields nothing — the
        // HMI case, compiled only on Tuanjie — we walk the window's visual tree for an element
        // exposing a RenderTexture "PreviewTexture", which is how the simulators publish frames.
        private static RenderTexture GetGameViewRT(EditorWindow gv)
        {
            if (gv == null) return null;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public |
                                       BindingFlags.Instance | BindingFlags.DeclaredOnly;
            string[] candidates = { "targetTexture", "m_TargetTexture", "m_RenderTexture" };

            foreach (string name in candidates)
            {
                for (Type t = gv.GetType(); t != null && t != typeof(object); t = t.BaseType)
                {
                    PropertyInfo pi = t.GetProperty(name, flags);
                    if (pi != null && pi.GetValue(gv) is RenderTexture prt && prt.IsCreated()) return prt;

                    FieldInfo fi = t.GetField(name, flags);
                    if (fi != null && fi.GetValue(gv) is RenderTexture frt && frt.IsCreated()) return frt;
                }
            }

#if TUANJIE_1_0_OR_NEWER
            // HMI Simulator is a Tuanjie-only window that renders into DeviceView.PreviewTexture
            // rather than the base PlayModeView render texture, so reach into its visual tree.
            // Tuanjie is 2022.3-based, so UNITY_2021_2_OR_NEWER (UIElements `using`) always holds.
            RenderTexture preview = FindPreviewTextureInVisualTree(gv.rootVisualElement);
            if (preview != null) return preview;
#endif
            return null;
        }

#if TUANJIE_1_0_OR_NEWER
        // Depth-first search of a window's UI Toolkit tree for an element that publishes its
        // rendered frame through a "RenderTexture PreviewTexture" property (both the Device and
        // HMI simulators' DeviceView do this). Used for the Tuanjie-only HMI Simulator, whose
        // frame never reaches the base PlayModeView render texture. Reflection keeps us
        // decoupled from the simulators' internal, separately-compiled DeviceView types.
        private static RenderTexture FindPreviewTextureInVisualTree(VisualElement element)
        {
            if (element == null) return null;

            PropertyInfo pi = element.GetType().GetProperty("PreviewTexture",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (pi != null && pi.PropertyType == typeof(RenderTexture)
                && pi.GetValue(element) is RenderTexture rt && rt.IsCreated())
                return rt;

            int childCount = element.hierarchy.childCount;
            for (int i = 0; i < childCount; i++)
            {
                RenderTexture found = FindPreviewTextureInVisualTree(element.hierarchy[i]);
                if (found != null) return found;
            }
            return null;
        }
#endif

        private static Texture2D RenderAllCamerasToTexture(int width, int height)
        {
            RenderTexture rt        = new RenderTexture(width, height, 24);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.black);

            Camera[] cameras = Camera.allCameras;
            System.Array.Sort(cameras, (a, b) => a.depth.CompareTo(b.depth));

            foreach (Camera cam in cameras)
            {
                if (cam == null || !cam.enabled || !cam.gameObject.activeInHierarchy) continue;
                RenderTexture prev = cam.targetTexture;
                cam.targetTexture  = rt;
                cam.Render();
                cam.targetTexture  = prev;
            }

            Texture2D tex = ReadRenderTexture(rt, width, height);
            RenderTexture.active = prevActive;
            UnityEngine.Object.DestroyImmediate(rt);
            return tex;
        }

        // ==================== Camera helpers ====================

        private static Texture2D RenderCameraToTexture(Camera cam, int width, int height)
        {
            return RenderCameraToTexture(cam, width, height, CaptureRenderMode.Shaded);
        }

        // How geometry is drawn when rendering a camera to a texture.
        //   Shaded          – normal lit/textured render (default).
        //   Wireframe       – wireframe only (GL.wireframe during the render pass).
        //   ShadedWireframe – a shaded render with the wireframe edges composited on top.
        private enum CaptureRenderMode { Shaded, Wireframe, ShadedWireframe }

        private static CaptureRenderMode ParseRenderMode(JToken token)
        {
            string raw = token?.ToString()?.Trim().ToLower().Replace("_", "").Replace("-", "");
            switch (raw)
            {
                case "wireframe":       return CaptureRenderMode.Wireframe;
                case "shadedwireframe": return CaptureRenderMode.ShadedWireframe;
                default:                return CaptureRenderMode.Shaded;
            }
        }

        private static Texture2D RenderCameraToTexture(Camera cam, int width, int height, CaptureRenderMode renderMode)
        {
            if (cam == null) return null;

            if (renderMode == CaptureRenderMode.ShadedWireframe)
                return RenderShadedWireframe(cam, width, height);

            RenderTexture rt   = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.Default);
            RenderTexture prev = cam.targetTexture;
            cam.targetTexture  = rt;

            // GL.wireframe only affects rendering issued while it is true, and must be reset
            // afterwards so it doesn't leak into unrelated editor/scene-view drawing.
            bool prevWireframe = GL.wireframe;
            try
            {
                GL.wireframe = renderMode == CaptureRenderMode.Wireframe;
                cam.Render();
            }
            finally
            {
                GL.wireframe = prevWireframe;
                cam.targetTexture = prev;
            }

            Texture2D tex = ReadRenderTexture(rt, width, height);
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        // Shaded surfaces with the wireframe edges drawn on top. Composited at the texture
        // level so it is independent of render-pipeline / clear-flag behavior (an in-place
        // second pass with clearFlags=Nothing is not honored under SRPs, which is why it
        // previously looked identical to plain wireframe):
        //   1. a normal shaded pass;
        //   2. two wireframe passes over solid black and solid white backgrounds.
        // Opaque edge pixels render identically over either background, while untouched
        // background pixels differ wildly — that agreement is a reliable wireframe mask that
        // does not depend on the geometry's material color. Mask pixels darken the shaded
        // image to read as edges.
        private static Texture2D RenderShadedWireframe(Camera cam, int width, int height)
        {
            RenderTexture rt   = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.Default);
            RenderTexture prev = cam.targetTexture;
            bool  prevWireframe = GL.wireframe;
            CameraClearFlags origFlags = cam.clearFlags;
            Color origBg = cam.backgroundColor;

            Color32[] shaded, wireBlack, wireWhite;
            try
            {
                cam.targetTexture = rt;

                // Shaded pass (keep the camera's own clear flags / background).
                GL.wireframe = false;
                cam.Render();
                shaded = ReadPixels32(rt, width, height);

                // Wireframe passes over two solid backgrounds.
                GL.wireframe = true;
                cam.clearFlags = CameraClearFlags.SolidColor;

                cam.backgroundColor = Color.black;
                cam.Render();
                wireBlack = ReadPixels32(rt, width, height);

                cam.backgroundColor = Color.white;
                cam.Render();
                wireWhite = ReadPixels32(rt, width, height);
            }
            finally
            {
                GL.wireframe        = prevWireframe;
                cam.clearFlags      = origFlags;
                cam.backgroundColor = origBg;
                cam.targetTexture   = prev;
                RenderTexture.ReleaseTemporary(rt);
            }

            for (int i = 0; i < shaded.Length; i++)
            {
                int diff = Math.Abs(wireBlack[i].r - wireWhite[i].r)
                         + Math.Abs(wireBlack[i].g - wireWhite[i].g)
                         + Math.Abs(wireBlack[i].b - wireWhite[i].b);
                if (diff < 24) // drawn edge (same regardless of background) → darken to a line
                    shaded[i] = new Color32((byte)(shaded[i].r / 4), (byte)(shaded[i].g / 4), (byte)(shaded[i].b / 4), 255);
            }

            Texture2D tex = new Texture2D(width, height, TextureFormat.ARGB32, false);
            tex.SetPixels32(shaded);
            tex.Apply();
            return tex;
        }

        // Reads the active contents of a RenderTexture into a Color32[] (top-up Unity order).
        private static Color32[] ReadPixels32(RenderTexture rt, int width, int height)
        {
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tmp = new Texture2D(width, height, TextureFormat.ARGB32, false);
            tmp.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tmp.Apply();
            RenderTexture.active = prevActive;
            Color32[] px = tmp.GetPixels32();
            UnityEngine.Object.DestroyImmediate(tmp);
            return px;
        }

        private static Texture2D ReadRenderTexture(RenderTexture rt, int width, int height)
        {
            if (rt == null) return null;
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(width, height, TextureFormat.ARGB32, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;
            return tex;
        }

        private static void CopyCameraSettings(Camera src, Camera dst)
        {
            dst.clearFlags      = src.clearFlags;
            dst.backgroundColor = src.backgroundColor;
            dst.cullingMask     = src.cullingMask;
            dst.nearClipPlane   = src.nearClipPlane;
            dst.farClipPlane    = src.farClipPlane;
            dst.fieldOfView     = src.fieldOfView;
            dst.renderingPath   = src.renderingPath;
            dst.allowHDR        = src.allowHDR;
            dst.allowMSAA       = src.allowMSAA;
        }

        // ==================== Asset instantiation ====================

        private static GameObject InstantiateAsset(string relPath, Type assetType)
        {
            if (assetType == typeof(GameObject))
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(relPath);
                if (prefab == null) return null;
                return (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            }

            if (assetType == typeof(Mesh))
            {
                Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(relPath);
                if (mesh == null) return null;
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                return go;
            }

            if (assetType == typeof(Material))
            {
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(relPath);
                if (mat == null) return null;
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.transform.localScale = Vector3.one * 3f;
                go.GetComponent<Renderer>().sharedMaterial = mat;
                return go;
            }

            if (assetType == typeof(Texture2D))
            {
                // Try as sprite first; fall back to a textured quad
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(relPath);
                if (sprite != null)
                {
                    GameObject go = new GameObject("SpritePreview");
                    go.AddComponent<SpriteRenderer>().sprite = sprite;
                    return go;
                }

                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(relPath);
                if (tex == null) return null;
                GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Material mat = new Material(Shader.Find("Unlit/Texture")) { mainTexture = tex };
                quad.GetComponent<Renderer>().sharedMaterial = mat;
                return quad;
            }

            return null;
        }

        // ==================== Over-time helpers ====================

        private struct OverTimeParams
        {
            /// <summary>Total number of frames to capture.</summary>
            public int Count;
            /// <summary>
            /// How many simulation steps to advance between captures.
            /// 0 means no stepping (captures taken at same instant).
            /// </summary>
            public int StepsPerInterval;
        }

        /// <summary>
        /// Parses the <c>over_time</c> parameter.
        /// Accepted formats:
        ///   "N"    – capture N frames with no simulation stepping.
        ///   "NxS"  – capture N frames, stepping S simulation frames between each.
        /// Examples: "4", "4x1", "6x3"
        /// </summary>
        private static OverTimeParams ParseOverTime(JToken token)
        {
            if (token == null) return new OverTimeParams { Count = 1, StepsPerInterval = 0 };

            string raw = token.ToString().Trim();
            if (string.IsNullOrEmpty(raw)) return new OverTimeParams { Count = 1, StepsPerInterval = 0 };

            int separatorIdx = raw.IndexOf('x');
            if (separatorIdx < 0)
            {
                int.TryParse(raw, out int count);
                return new OverTimeParams { Count = Math.Max(1, count), StepsPerInterval = 0 };
            }
            else
            {
                int.TryParse(raw.Substring(0, separatorIdx), out int count);
                int.TryParse(raw.Substring(separatorIdx + 1), out int steps);
                return new OverTimeParams
                {
                    Count            = Math.Max(1, count),
                    StepsPerInterval = Math.Max(0, steps),
                };
            }
        }


        /// <summary>Short uppercase label for a zero-based frame index ("F0", "F1", …).</summary>
        private static string FrameLabel(int frameIndex) => "FRAME" + frameIndex;

        // ==================== View direction / framing ====================

        /// <summary>Expands "all" / "cardinal" shorthands into a list of named directions.</summary>
        private static List<string> GetViewList(string viewParam)
        {
            switch (viewParam)
            {
                case "all":      return new List<string> { "front", "back", "left", "right", "top", "bottom", "iso" };
                case "cardinal": return new List<string> { "front", "right", "top", "iso" };
                default:         return new List<string> { string.IsNullOrEmpty(viewParam) ? "current" : viewParam };
            }
        }

        private static string ViewAbbreviation(string view)
        {
            switch (view)
            {
                case "front":  return "FRONT";
                case "back":   return "BACK";
                case "left":   return "LEFT";
                case "right":  return "RIGHT";
                case "top":    return "TOP";
                case "bottom": return "BOTTOM";
                case "iso":    return "ISO";
                default:       return view.ToUpper();
            }
        }

        /// <summary>
        /// Positions and orients <paramref name="cam"/> so that <paramref name="bounds"/> fills
        /// the view from the given named direction, with a 20% padding margin.
        /// </summary>
        private static void PositionCameraForView(Camera cam, Bounds bounds, string view, bool orthographic)
        {
            

            Vector3 center = bounds.center;
            Vector3 dir, up;

            switch (view)
            {
                case "front":  dir = Vector3.back;             up = Vector3.up;      break;
                case "back":   dir = Vector3.forward;          up = Vector3.up;      break;
                case "right":  dir = Vector3.left;             up = Vector3.up;      break;
                case "left":   dir = Vector3.right;            up = Vector3.up;      break;
                case "top":    dir = Vector3.down;             up = Vector3.forward; break;
                case "bottom": dir = Vector3.up;               up = Vector3.back;    break;
                case "iso":    dir = new Vector3(-1, -1, -1).normalized; up = Vector3.up; 
                               orthographic = true;
                               break;
                default: return; // "current" — caller is responsible for positioning
            }

            float maxExtent = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
            const float padding = 2f;

            if (orthographic)
            {
                cam.orthographic = orthographic;
                cam.orthographicSize = maxExtent * padding;
                float dist = maxExtent * 3f + cam.nearClipPlane;
                cam.transform.position = center - dir * dist;
            }
            else
            {
                float fovRad = cam.fieldOfView * Mathf.Deg2Rad;
                float dist   = (maxExtent * padding) / Mathf.Tan(fovRad * 0.5f);
                dist = Mathf.Max(dist, cam.nearClipPlane + 0.1f);
                cam.transform.position = center - dir * dist;
            }

            cam.transform.rotation = Quaternion.LookRotation(dir, up);
        }

        private static Bounds GetSceneBounds()
        {
#if UNITY_6000_5_OR_NEWER
            Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>();
#elif UNITY_2022_2_OR_NEWER
            Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
#else
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
#endif
            if (renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                foreach (Renderer r in renderers)
                    b.Encapsulate(r.bounds);
                return b;
            }
            return new Bounds(Vector3.zero, Vector3.one * 10f);
        }

        private static Bounds CalculateBounds(List<GameObject> objects)
        {
            if (objects == null || objects.Count == 0)
                return new Bounds(Vector3.zero, Vector3.one);

            Bounds b = new Bounds(objects[0].transform.position, Vector3.zero);
            foreach (GameObject go in objects)
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
                    b.Encapsulate(r.bounds);
            return b;
        }

        private static Bounds CalculateSingleObjectBounds(GameObject go)
        {
            Bounds b = new Bounds(go.transform.position, Vector3.zero);
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
                b.Encapsulate(r.bounds);
            // Ensure non-zero extents for objects without renderers (e.g. empty prefabs)
            if (b.size == Vector3.zero)
                b.size = Vector3.one;
            return b;
        }

        // ==================== GameObject path lookup ====================

        /// <summary>
        /// Finds a GameObject in the active scene by hierarchy path.
        /// Path segments are separated by "/" with no index notation in the string.
        /// <paramref name="targetIndex"/> selects the nth same-named sibling only at the
        /// final path segment (0-based, default 0). Intermediate segments always resolve
        /// to the first matching child.
        /// Examples:
        ///   FindGameObjectByPath("Player", 0)          – first root named "Player"
        ///   FindGameObjectByPath("Root/Enemies/Boss")  – nested, first match each level
        ///   FindGameObjectByPath("Root/Bullet", 2)     – 3rd child of Root named "Bullet"
        /// Returns null if any segment cannot be resolved.
        /// </summary>
        private static GameObject FindGameObjectByPath(string path, int targetIndex = 0)
        {
            if (string.IsNullOrEmpty(path)) return null;

            path = path.Replace('\\', '/').TrimStart('/');

            // Fast path: index 0 is handled directly by the built-in method which
            // supports "/" separated hierarchy paths and returns the first match.
            if (targetIndex == 0)
                return GameObject.Find(path);

            // index > 0: find the parent via built-in, then pick the nth same-named
            // sibling at the final segment manually.
            int lastSlash = path.LastIndexOf('/');
            string finalName = lastSlash < 0 ? path : path.Substring(lastSlash + 1);

            // Collect candidates at the final level
            List<Transform> candidates = new List<Transform>();
            if (lastSlash < 0)
            {
                // Root-level objects
                foreach (var r in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                    if (r.name == finalName) candidates.Add(r.transform);
            }
            else
            {
                string parentPath = path.Substring(0, lastSlash);
                GameObject parent = GameObject.Find(parentPath);
                if (parent == null) return null;
                foreach (Transform child in parent.transform)
                    if (child.name == finalName) candidates.Add(child);
            }

            return targetIndex < candidates.Count ? candidates[targetIndex].gameObject : null;
        }

        // ==================== Image encoding / saving ====================

        private static object BuildImageResponse(
            Texture2D tex,
            string customPath, string customFilename,
            string prefix, string warning = null)
        {
            try
            {
                byte[] bytes = tex.EncodeToPNG();

                string fn = !string.IsNullOrEmpty(customFilename)
                    ? (customFilename.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        ? customFilename
                        : customFilename + ".png")
                    : $"{prefix}_{Timestamp()}.png";

                string dir = !string.IsNullOrEmpty(customPath)
                    ? customPath
                    : Path.Combine(Path.GetDirectoryName(Application.dataPath), "screenshots");

                Directory.CreateDirectory(dir);
                string savePath = Path.Combine(dir, fn);
                File.WriteAllBytes(savePath, bytes);
                CodelyLogger.Log($"[ManageScreenshot] Saved: {savePath}");

                int tw = tex.width, th = tex.height;
                UnityEngine.Object.DestroyImmediate(tex);
                return Response.Success("Screenshot captured successfully.", new
                {
                    path    = savePath,
                    width   = tw,
                    height  = th,
                    warning = string.IsNullOrEmpty(warning) ? null : warning,
                });
            }
            catch (Exception e)
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                return Response.Error($"Failed to encode/save screenshot: {e.Message}");
            }
        }

        // ==================== Texture utilities ====================

        // When neither width nor height is supplied by the caller, captured textures
        // are downsampled so their longest edge equals this value.
        private const int AutoScaleMaxEdge = 960;

        private static Texture2D MaybeAutoScale(JObject p, Texture2D tex)
        {
            if (tex == null) return tex;
            int? w = p?["width"]?.ToObject<int?>();
            int? h = p?["height"]?.ToObject<int?>();
            if (w != null || h != null) return tex;
            return ScaleTextureToMaxEdge(tex, AutoScaleMaxEdge);
        }

        private static Texture2D ScaleTextureToMaxEdge(Texture2D src, int maxEdge)
        {
            if (src == null) return null;
            int srcW = src.width;
            int srcH = src.height;
            int longest = Mathf.Max(srcW, srcH);
            if (longest <= maxEdge) return src;

            float scale = (float)maxEdge / longest;
            int dstW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            int dstH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));

            RenderTexture rt = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;
            FilterMode prevFilter = src.filterMode;
            src.filterMode = FilterMode.Bilinear;
            RenderTexture prevActive = RenderTexture.active;

            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            Texture2D scaled = new Texture2D(dstW, dstH, TextureFormat.ARGB32, false);
            scaled.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0);
            scaled.Apply();

            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
            src.filterMode = prevFilter;
            UnityEngine.Object.DestroyImmediate(src);
            return scaled;
        }

        // Downscales (or returns unchanged) a texture by a uniform 0..1 factor. Destroys the
        // source and returns a new texture when scaling is applied; returns the source as-is
        // when factor is effectively 1.0.
        private static Texture2D ScaleTextureByFactor(Texture2D src, float factor)
        {
            if (src == null) return null;
            if (factor >= 0.999f) return src;

            int dstW = Mathf.Max(1, Mathf.RoundToInt(src.width  * factor));
            int dstH = Mathf.Max(1, Mathf.RoundToInt(src.height * factor));

            RenderTexture rt = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;
            FilterMode prevFilter = src.filterMode;
            src.filterMode = FilterMode.Bilinear;
            RenderTexture prevActive = RenderTexture.active;

            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            Texture2D scaled = new Texture2D(dstW, dstH, TextureFormat.ARGB32, false);
            scaled.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0);
            scaled.Apply();

            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
            src.filterMode = prevFilter;
            UnityEngine.Object.DestroyImmediate(src);
            return scaled;
        }

        private static Texture2D PrepareMp4Frame(Texture2D src)
        {
            if (src == null) return null;
            int dstW = Mathf.Max(2, src.width - src.width % 2);
            int dstH = Mathf.Max(2, src.height - src.height % 2);
            if (dstW == src.width && dstH == src.height &&
                src.format == TextureFormat.RGBA32)
                return src;

            RenderTexture rt = RenderTexture.GetTemporary(
                dstW, dstH, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var resized = new Texture2D(dstW, dstH, TextureFormat.RGBA32, false);
                resized.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0);
                resized.Apply();
                UnityEngine.Object.DestroyImmediate(src);
                return resized;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static void FlipTextureVertically(Texture2D tex)
        {
            if (tex == null || tex.height <= 1) return;
            int w = tex.width, h = tex.height;
            Color[] pixels = tex.GetPixels();
            for (int y = 0; y < h / 2; y++)
            {
                int top = y * w, bottom = (h - 1 - y) * w;
                for (int x = 0; x < w; x++)
                {
                    Color tmp          = pixels[top + x];
                    pixels[top + x]    = pixels[bottom + x];
                    pixels[bottom + x] = tmp;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
        }

        private static Texture2D StitchOrSingle(List<Texture2D> textures)
        {
            if (textures.Count == 1) return textures[0];

            int cols  = Mathf.CeilToInt(Mathf.Sqrt(textures.Count));
            int rows  = Mathf.CeilToInt((float)textures.Count / cols);
            int cellW = textures[0].width;
            int cellH = textures[0].height;

            Texture2D result = new Texture2D(cellW * cols, cellH * rows, TextureFormat.RGB24, false);

            for (int i = 0; i < textures.Count; i++)
            {
                int col = i % cols;
                int row = rows - 1 - (i / cols); // Unity Y=0 is bottom
                if (textures[i] != null)
                    result.SetPixels(col * cellW, row * cellH, cellW, cellH, textures[i].GetPixels());
            }

            result.Apply();

            foreach (var t in textures)
                if (t != null) UnityEngine.Object.DestroyImmediate(t);

            return result;
        }

        // Minimal 5×7 bitmap font for label rendering on captured textures.
        // Each entry contains 7 row bitmasks; bit 4 = leftmost pixel, bit 0 = rightmost.
        private static readonly Dictionary<char, int[]> s_BitmapFont = new Dictionary<char, int[]>
        {
            // Letters
            ['A'] = new[] { 14, 17, 17, 31, 17, 17,  0 },
            ['B'] = new[] { 30, 17, 17, 30, 17, 17, 30 },
            ['C'] = new[] { 14, 17, 16, 16, 16, 17, 14 },
            ['D'] = new[] { 30, 17, 17, 17, 17, 17, 30 },
            ['E'] = new[] { 31, 16, 16, 28, 16, 16, 31 },
            ['F'] = new[] { 31, 16, 30, 16, 16, 16,  0 },
            ['G'] = new[] { 15, 16, 16, 19, 17, 14,  0 },
            ['H'] = new[] { 17, 17, 31, 17, 17, 17,  0 },
            ['I'] = new[] { 31,  4,  4,  4,  4, 31,  0 },
            ['K'] = new[] { 17, 18, 20, 24, 20, 18, 17 },
            ['L'] = new[] { 16, 16, 16, 16, 16, 16, 31 },
            ['M'] = new[] { 17, 27, 21, 17, 17, 17,  0 },
            ['N'] = new[] { 17, 25, 21, 19, 17, 17,  0 },
            ['O'] = new[] { 14, 17, 17, 17, 17, 14,  0 },
            ['P'] = new[] { 30, 17, 17, 30, 16, 16,  0 },
            ['R'] = new[] { 30, 17, 17, 30, 20, 18,  0 },
            ['S'] = new[] { 14, 17, 16, 14,  1, 17, 14 },
            ['T'] = new[] { 31,  4,  4,  4,  4,  4,  0 },
            ['-'] = new[] {  0,  0,  0, 31,  0,  0,  0 },
            // Digits (for frame labels: F0, F1, …)
            ['0'] = new[] { 14, 17, 19, 21, 25, 17, 14 },
            ['1'] = new[] {  4, 12,  4,  4,  4,  4, 14 },
            ['2'] = new[] { 14, 17,  1,  6,  8, 16, 31 },
            ['3'] = new[] { 14, 17,  1,  6,  1, 17, 14 },
            ['4'] = new[] {  2,  6, 10, 18, 31,  2,  2 },
            ['5'] = new[] { 31, 16, 30,  1,  1, 17, 14 },
            ['6'] = new[] {  6,  8, 16, 30, 17, 17, 14 },
            ['7'] = new[] { 31,  1,  2,  4,  8,  8,  8 },
            ['8'] = new[] { 14, 17, 17, 14, 17, 17, 14 },
            ['9'] = new[] { 14, 17, 17, 15,  1,  2, 12 },
        };

        private static void DrawLabelOnTexture(Texture2D tex, string label)
        {
            if (tex == null || string.IsNullOrEmpty(label)) return;

            const int charW = 5, charH = 7, spacing = 1, pad = 2;
            // Cap the label box to 1/5 of the image height (and keep it within the width),
            // shrinking the pixel scale on small captures so the label can't dominate the frame.
            int maxScaleByHeight = Mathf.Max(1, (tex.height / 5 - pad * 2) / charH);
            int maxScaleByWidth  = Mathf.Max(1, (tex.width  - pad * 2) / (label.Length * (charW + spacing)));
            int scale = Mathf.Clamp(7, 1, Mathf.Min(maxScaleByHeight, maxScaleByWidth));

            int totalW = label.Length * (charW + spacing) * scale - spacing * scale + pad * 2;
            int totalH = charH * scale + pad * 2;

            Color[] pixels = tex.GetPixels();
            int texW = tex.width, texH = tex.height;
            int bgX = pad, bgY = texH - totalH - pad;

            // Darken background box
            for (int y = bgY; y < bgY + totalH; y++)
                for (int x = bgX; x < bgX + totalW; x++)
                {
                    if (x < 0 || x >= texW || y < 0 || y >= texH) continue;
                    Color c = pixels[y * texW + x];
                    pixels[y * texW + x] = new Color(c.r * 0.25f, c.g * 0.25f, c.b * 0.25f, 1f);
                }

            // Draw characters in white
            for (int ci = 0; ci < label.Length; ci++)
            {
                if (!s_BitmapFont.TryGetValue(label[ci], out int[] rows)) continue;
                int charOX = bgX + pad + ci * (charW + spacing) * scale;
                int charOY = bgY + pad;

                for (int row = 0; row < charH; row++)
                {
                    int bits = rows[row];
                    int py   = charOY + (charH - 1 - row) * scale;
                    for (int col = 0; col < charW; col++)
                    {
                        if (((bits >> (charW - 1 - col)) & 1) == 0) continue;
                        int px = charOX + col * scale;
                        for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++)
                            {
                                int fx = px + sx, fy = py + sy;
                                if (fx >= 0 && fx < texW && fy >= 0 && fy < texH)
                                    pixels[fy * texW + fx] = Color.white;
                            }
                    }
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
        }

        // ==================== Path utilities ====================

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            path = path.Replace('\\', '/');

            string dataPath   = Application.dataPath.Replace('\\', '/');
            string projectRoot = dataPath.Substring(0, dataPath.LastIndexOf('/'));

            if (path.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                path = path.Substring(projectRoot.Length).TrimStart('/');

            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                path = "Assets/" + path.TrimStart('/');

            return path;
        }

        // ==================== UIToolkit helpers ====================

#if UNITY_2021_2_OR_NEWER
        // Samples a small grid of pixels from the center of the RT to decide whether
        // the panel has rendered anything yet. Returns true when every sampled pixel
        // is opaque black (i.e. nothing has been drawn).
        private static bool IsRenderTextureBlack(RenderTexture rt)
        {
            if (rt == null) return true;
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            // Read a 4×4 block from the center of the texture.
            int sampleW = Mathf.Min(4, rt.width);
            int sampleH = Mathf.Min(4, rt.height);
            int startX  = (rt.width  - sampleW) / 2;
            int startY  = (rt.height - sampleH) / 2;

            Texture2D probe = new Texture2D(sampleW, sampleH, TextureFormat.ARGB32, false);
            probe.ReadPixels(new Rect(startX, startY, sampleW, sampleH), 0, 0);
            probe.Apply();
            RenderTexture.active = prev;

            Color32[] pixels = probe.GetPixels32();
            UnityEngine.Object.DestroyImmediate(probe);

            foreach (Color32 c in pixels)
            {
                // If any pixel is non-black or has alpha > 0, the panel has rendered.
                if (c.r > 2 || c.g > 2 || c.b > 2 || c.a > 2)
                    return false;
            }
            return true;
        }

        // Unity ships a default runtime theme under the com.unity.ui package. When the
        // UI Builder's per-uxml theme map can't be located (Builder package not present,
        // or this uxml hasn't been opened in Builder), we fall back to this so text and
        // controls render with sensible defaults instead of an empty stylesheet.
        private static ThemeStyleSheet GetDefaultRuntimeTheme()
        {
            string[] candidates =
            {
                "Packages/com.unity.ui/PackageResources/StyleSheets/Generated/UnityDefaultRuntimeTheme.tss",
                "Packages/com.unity.toolkits.ui/PackageResources/StyleSheets/Generated/UnityDefaultRuntimeTheme.tss",
                "UI Toolkit/UnityDefaultRuntimeTheme.tss",
            };
            foreach (string p in candidates)
            {
                var tss = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(p);
                if (tss != null) return tss;
            }
            return null;
        }

        private static ThemeStyleSheet GetUIBuilderTheme(string uxmlPath)
        {
            Type builderDocType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                builderDocType = asm.GetType("Unity.UI.Builder.BuilderDocument");
                if (builderDocType != null) break;
            }
            if (builderDocType == null) return null;

            UnityEngine.Object[] instances = Resources.FindObjectsOfTypeAll(builderDocType);
            if (instances.Length == 0) return null;

            var instance = instances[0];
            BindingFlags nonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
            BindingFlags pub = BindingFlags.Instance | BindingFlags.Public;

            if (uxmlPath != null)
            {
                var listField = builderDocType.GetField("m_SavedBuilderUxmlToThemeStyleSheetList", nonPublic);
                if (listField?.GetValue(instance) is IList list)
                {
                    foreach (object entry in list)
                    {
                        Type entryType = entry.GetType();
                        if (entryType.GetField("UxmlURI", pub)?.GetValue(entry) is string uxmlUri
                            && uxmlUri.Contains(uxmlPath)
                            && entryType.GetField("ThemeStyleSheetURI", pub)?.GetValue(entry) is string themeUri
                            && themeUri.StartsWith("project://database/"))
                        {
                            int qIdx = themeUri.IndexOf('?');
                            if (qIdx < 0) qIdx = themeUri.Length;
                            string assetPath = Uri.UnescapeDataString(
                                themeUri.Substring("project://database/".Length, qIdx - "project://database/".Length));
                            ThemeStyleSheet tss = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(assetPath);
                            if (tss != null) return tss;
                        }
                    }
                }
            }

            return builderDocType.GetField("m_CurrentCanvasThemeStyleSheetReference", nonPublic)
                ?.GetValue(instance) as ThemeStyleSheet;
        }

        // ----- Why this exists (bisected 2026-05-14, verified on this codebase) -----
        // In Play Mode, a UIDocument's panel renders via UIElementsRuntimeUtility's
        // player-loop hook. In Edit Mode that hook is dormant, so a PanelSettings with
        // targetTexture set will lay out correctly but never flush draw commands —
        // the RT stays at its clear color forever. Neither
        //   UIElementsRuntimeUtility.UpdateRuntimePanels()   (runs layout only)
        //   UIElementsRuntimeUtility.RepaintOverlayPanels()  (no-op in edit mode here)
        //   PanelSettings.GetOrCreatePanel()                 (panel already exists)
        //   gameView.Repaint()                               (works, but redundant)
        // is required.
        //
        // Unity 2021/2022:
        //   BaseRuntimePanel.Repaint(Event) runs both the visual-tree pass AND the
        //   synchronous draw submission, so calling that one method is enough.
        //
        // Unity 6 (6000.x):
        //   The pipeline was split. Repaint(Event) now only marks UIR's render chain
        //   as needing work; the draw submission moved into a separate zero-arg
        //   Render()-family method that the player loop normally calls. In Edit Mode
        //   nothing calls it, AND a foregrounded editor frame is required for the
        //   render thread to actually flush UIR's mesh chunks. So on Unity 6 we must
        //   ALSO invoke DirectPanelRender(), which is the synchronous "do the draw
        //   right now on this thread" entry that bypasses focus / player-loop gates.
        //
        // The UpdateRuntimePanels call is kept as a cheap defensive step so Yoga's
        // layout pass can settle if a stylesheet loads asynchronously.
        // ---------------------------------------------------------------------------

        private static MethodInfo s_UpdateRuntimePanelsMethod;
        private static bool       s_UpdateRuntimePanelsSearched;

        // Cached per panel type — Unity 6 added a zero-arg synchronous draw entry on
        // BaseRuntimePanel; the exact name has churned across versions, so we resolve
        // by signature (parameterless instance method) and cache the first hit.
        private static MethodInfo s_DirectRenderMethod;
        private static Type       s_DirectRenderOwnerType;

        private static void EnsureUpdateRuntimePanelsLoaded()
        {
            if (s_UpdateRuntimePanelsSearched) return;
            s_UpdateRuntimePanelsSearched = true;
            try
            {
                Type runtimeUtilType = typeof(VisualElement).Assembly
                    .GetType("UnityEngine.UIElements.UIElementsRuntimeUtility");
                s_UpdateRuntimePanelsMethod = runtimeUtilType?.GetMethod(
                    "UpdateRuntimePanels",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            }
            catch { /* type not available in this Unity version */ }
        }

        // Walks up the panel's type hierarchy to find Repaint(Event) and invokes it
        // with a null event. On 2021/2022 this also issues draws; on Unity 6 this
        // only requests them — DirectPanelRender below performs the actual submission.
        private static void DirectPanelRepaint(IPanel panel)
        {
            if (panel == null) return;
            Type t = panel.GetType();
            while (t != null)
            {
                MethodInfo repaint = t.GetMethod("Repaint",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(Event) }, null);
                if (repaint != null)
                {
                    try { repaint.Invoke(panel, new object[] { null }); return; }
                    catch { /* try base class */ }
                }
                t = t.BaseType;
            }
        }

        // Synchronously flushes the panel's UIR command list to PanelSettings.targetTexture.
        // No-op on Unity versions that don't have a parameterless render method (2021/2022
        // — there DirectPanelRepaint already does the draw). On Unity 6 this is what
        // removes the dependency on the editor being the foreground OS window: the call
        // executes inline on the calling thread instead of waiting for a focus-throttled
        // editor frame submission.
        private static void DirectPanelRender(IPanel panel)
        {
            if (panel == null) return;

            Type panelType = panel.GetType();
            if (s_DirectRenderMethod == null || s_DirectRenderOwnerType != panelType)
            {
                s_DirectRenderOwnerType = panelType;
                s_DirectRenderMethod    = null;

                // Names seen across UI Toolkit revisions. First parameterless match wins.
                // We deliberately exclude "Repaint" — that one's handled separately and
                // taking a parameterless overload would skip its Event-driven setup.
                string[] candidates = { "Render", "RenderImmediately", "DoRender", "DoRepaint" };

                Type t = panelType;
                while (t != null && s_DirectRenderMethod == null)
                {
                    foreach (string name in candidates)
                    {
                        MethodInfo m = t.GetMethod(name,
                            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                            null, Type.EmptyTypes, null);
                        if (m != null) { s_DirectRenderMethod = m; break; }
                    }
                    t = t.BaseType;
                }
            }

            if (s_DirectRenderMethod != null)
            {
                try { s_DirectRenderMethod.Invoke(panel, null); } catch { /* ignore */ }
            }
        }

        private static void TryForceUIDocumentRepaint(UIDocument uiDoc)
        {
            if (uiDoc == null) return;
            try
            {
                EnsureUpdateRuntimePanelsLoaded();

                VisualElement root = uiDoc.rootVisualElement;
                if (root != null) root.MarkDirtyRepaint();

                // Layout pass (lets Yoga settle if anything is still resolving).
                try { s_UpdateRuntimePanelsMethod?.Invoke(null, null); } catch { }

                IPanel panel = root?.panel;
                // 2021/2022: this also draws. Unity 6: this only requests a draw.
                DirectPanelRepaint(panel);
                // Unity 6 only: actually submit the draw on this thread. No-op elsewhere.
                DirectPanelRender(panel);
            }
            catch { /* Ignore — reflection targets may not exist in all Unity versions */ }
        }
#endif

        // ==================== GIF capture (as_gif) ====================

        private sealed class GifParams
        {
            public int   FrameCount;   // -1 when not supplied (caller treats as error)
            public int   Fps;
            public int   ColorCount;
            public float StartDelay;   // seconds
        }

        // Returns null when as_gif is absent / falsey. Sub-parameters are accepted either as
        // siblings of as_gif or nested inside an as_gif object; camelCase and snake_case keys
        // are both honored. frameCount is required — when missing it is left at -1 so the
        // capture coroutine can surface a clear error.
        private static GifParams ParseGifParams(JObject p)
        {
            JToken ag = p?["as_gif"];
            if (ag == null || ag.Type == JTokenType.Null) return null;
            if (ag.Type == JTokenType.Boolean && !ag.Value<bool>()) return null;
            if (ag.Type == JTokenType.String)
            {
                string s = ag.ToString().Trim().ToLower();
                if (s == "false" || s == "0" || s == "no" || s == "") return null;
            }

            JObject src = ag as JObject;
            Func<string[], JToken> get = keys =>
            {
                foreach (string k in keys) { JToken t = src?[k]; if (t != null && t.Type != JTokenType.Null) return t; }
                foreach (string k in keys) { JToken t = p[k];    if (t != null && t.Type != JTokenType.Null) return t; }
                return null;
            };

            int?   fc    = AsInt(get(new[] { "frameCount", "frame_count" }));
            int    fps   = Mathf.Clamp(AsInt(get(new[] { "fps" }))                          ?? 25,  10, 30);
            int    cc    = Mathf.Clamp(AsInt(get(new[] { "colorCount", "color_count" }))     ?? 128, 64, 256);
            float  delay = Mathf.Clamp(AsFloat(get(new[] { "startDelay", "start_delay" }))   ?? 0f,  0f, 5f);

            return new GifParams
            {
                FrameCount = fc.HasValue ? Mathf.Clamp(fc.Value, 1, 200) : -1,
                Fps        = fps,
                ColorCount = cc,
                StartDelay = delay,
            };
        }

        // capture_game_view resolution scale factor (0.25–1.0, default 0.5).
        private static float ParseScale(JObject p)
        {
            return Mathf.Clamp(AsFloat(p?["scale"]) ?? 1.0f, 0.25f, 1.0f);
        }

        private static bool ValidateStaticPlayModeCapture(
            JObject p, Action<object> setResult)
        {
            if (!EditorApplication.isPlaying) return true;

            bool allow = false;
            JToken allowToken =
                p?["allow_static_in_play_mode"] ?? p?["allowStaticInPlayMode"];
            if (allowToken != null)
            {
                try { allow = allowToken.ToObject<bool>(); }
                catch { allow = false; }
            }

            string reason = (
                p?["static_reason"] ?? p?["staticReason"]
            )?.ToString()?.Trim();
            if (allow && !string.IsNullOrEmpty(reason))
                return true;

            setResult(Response.Error(
                "Static camera capture is blocked in Play Mode because a single frame " +
                "cannot verify animation, movement, particles, physics, transitions, or other " +
                "dynamic behavior. Do not retry this static capture. Pass record_game_view on " +
                "the same exec_runtime_script (or legacy execute_csharp_script) that triggers " +
                "or restarts the effect. Only when " +
                "the user explicitly needs genuinely static Play Mode output may you retry with " +
                "allow_static_in_play_mode=true and a non-empty static_reason."));
            return false;
        }

        private static int? AsInt(JToken t)
        {
            if (t == null) return null;
            try { return t.ToObject<int>(); } catch { }
            return int.TryParse(t.ToString(), out int v) ? v : (int?)null;
        }

        private static float? AsFloat(JToken t)
        {
            if (t == null) return null;
            try { return t.ToObject<float>(); } catch { }
            return float.TryParse(t.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : (float?)null;
        }

        // Captures gif.FrameCount frames from the supplied producer, pacing capture to the
        // target fps and recording each frame's ACTUAL measured interval as its GIF display
        // duration (so playback tracks real recording speed, including editor hitches), after
        // an optional startDelay. The producer must return a fresh Texture2D per call
        // (ownership transfers here — frames are destroyed after encoding). beforeFrame, when
        // supplied, runs to completion before each capture (used to repaint a Game View);
        // its yields count toward the frame's measured display duration.
        private static IEnumerator CaptureFramesToGif(
            GifParams gif, Func<Texture2D> produceFrame,
            string customPath, string customFile, string prefix,
            Action<object> setResult, Func<IEnumerator> beforeFrame = null)
        {
            if (gif.FrameCount < 1)
            {
                setResult(Response.Error("as_gif requires 'frameCount' in the range 1-200."));
                yield break;
            }

            // startDelay: wait the requested wall-clock seconds before capturing.
            if (gif.StartDelay > 0f)
            {
                double until = EditorApplication.timeSinceStartup + gif.StartDelay;
                while (EditorApplication.timeSinceStartup < until)
                    yield return null;
            }

            double targetInterval = 1.0 / Mathf.Max(1, gif.Fps);
            int    nominalDelayCs = Mathf.Max(1, Mathf.RoundToInt(100f / Mathf.Max(1, gif.Fps)));

            List<Texture2D> frames   = new List<Texture2D>(gif.FrameCount);
            List<int>       delaysCs = new List<int>(gif.FrameCount);

            double prevTime = EditorApplication.timeSinceStartup;
            for (int i = 0; i < gif.FrameCount; i++)
            {
                // yield one editor tick so the player loop advances before each capture.
                // runInBackground is set to true by the play action, so the player loop
                // runs at full speed even when the editor is not focused.
                yield return null;

                // Pace to the target fps.
                double waitUntil = prevTime + targetInterval;
                while (EditorApplication.timeSinceStartup < waitUntil)
                    yield return null;

                if (beforeFrame != null)
                {
                    IEnumerator prep = beforeFrame();
                    while (prep.MoveNext()) yield return prep.Current;
                }

                double now = EditorApplication.timeSinceStartup;
                int gapCs  = Mathf.Max(1, Mathf.RoundToInt((float)((now - prevTime) * 100.0)));
                prevTime   = now;

                Texture2D tex = null;
                try { tex = produceFrame(); }
                catch (Exception e) { CodelyLogger.LogWarning($"[ManageScreenshot] GIF frame {i} capture failed: {e.Message}"); }

                if (tex != null)
                {
                    // The interval just elapsed is the display duration of the PREVIOUS frame.
                    if (frames.Count > 0) delaysCs.Add(gapCs);
                    frames.Add(tex);
                }
            }
            if (frames.Count > 0) delaysCs.Add(nominalDelayCs); // last frame: nominal interval

            if (frames.Count == 0)
            {
                setResult(Response.Error("Failed to capture any frames for GIF."));
                yield break;
            }

            object result;
            try { result = BuildGifResponse(frames, delaysCs, gif, customPath, customFile, prefix); }
            catch (Exception e)
            {
                foreach (Texture2D f in frames) if (f != null) UnityEngine.Object.DestroyImmediate(f);
                CodelyLogger.LogError($"[ManageScreenshot] GIF encode failed: {e}");
                result = Response.Error($"Failed to encode GIF: {e.Message}");
            }
            setResult(result);
        }

        private static object BuildGifResponse(
            List<Texture2D> frames, List<int> delaysCs, GifParams gif,
            string customPath, string customFilename, string prefix)
        {
            // GIF frames must share dimensions; drop any mismatched frame (shouldn't happen
            // for a single capture source, but a Game View resize mid-capture could cause it).
            // The per-frame delay list is filtered in lockstep so timing stays aligned.
            int w = frames[0].width, h = frames[0].height;
            List<Texture2D> usable     = new List<Texture2D>(frames.Count);
            List<int>       usableDelay = new List<int>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                Texture2D f = frames[i];
                if (f == null) continue;
                if (f.width == w && f.height == h)
                {
                    usable.Add(f);
                    usableDelay.Add(i < delaysCs.Count ? delaysCs[i] : 4);
                }
                else { CodelyLogger.LogWarning("[ManageScreenshot] Dropping GIF frame with mismatched size."); UnityEngine.Object.DestroyImmediate(f); }
            }
            if (usable.Count == 0) { usable.Add(frames[0]); usableDelay.Add(4); }

            byte[] bytes = GifEncoder.Encode(usable, gif.ColorCount, usableDelay);

            string fn = !string.IsNullOrEmpty(customFilename)
                ? (customFilename.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? customFilename : customFilename + ".gif")
                : $"{prefix}_{Timestamp()}.gif";

            string dir = !string.IsNullOrEmpty(customPath)
                ? customPath
                : Path.Combine(Path.GetDirectoryName(Application.dataPath), "screenshots");

            Directory.CreateDirectory(dir);
            string savePath = Path.Combine(dir, fn);
            File.WriteAllBytes(savePath, bytes);

            int gw = usable[0].width, gh = usable[0].height, frameCount = usable.Count;
            foreach (Texture2D f in usable) UnityEngine.Object.DestroyImmediate(f);
            CodelyLogger.Log($"[ManageScreenshot] Saved GIF: {savePath} ({frameCount} frames @ {gif.Fps}fps)");

            return Response.Success("GIF captured successfully.", new
            {
                path   = savePath,
                width  = gw,
                height = gh,
                frames = frameCount,
                fps    = gif.Fps,
            });
        }

        // Self-contained animated GIF89a encoder: a median-cut global palette (built from a
        // sample of all frames) plus standard GIF LZW compression. No external dependencies.
        private static class GifEncoder
        {
            // frameDelaysCs holds each frame's display duration in 1/100 s. It is matched to
            // the actual measured capture interval so playback runs at real recording speed;
            // a missing/short entry falls back to the last known delay.
            public static byte[] Encode(List<Texture2D> frames, int colorCount, IList<int> frameDelaysCs)
            {
                colorCount = Mathf.Clamp(colorCount, 2, 256);
                int width  = frames[0].width;
                int height = frames[0].height;

                // Pull pixels once (Unity returns bottom-up rows); reused for palette + indexing.
                Color32[][] pixels = new Color32[frames.Count][];
                for (int i = 0; i < frames.Count; i++)
                    pixels[i] = frames[i].GetPixels32();

                Color32[] palette = BuildPalette(pixels, colorCount);
                byte[]    cube    = BuildCubeMap(palette);

                int bits = 1;
                while ((1 << bits) < palette.Length) bits++;
                bits = Mathf.Clamp(bits, 1, 8);
                int tableSize   = 1 << bits;                 // padded global color table entries
                int minCodeSize = Math.Max(2, bits);

                using (MemoryStream ms = new MemoryStream())
                {
                    WriteAscii(ms, "GIF89a");

                    // Logical Screen Descriptor
                    WriteUInt16(ms, (ushort)width);
                    WriteUInt16(ms, (ushort)height);
                    ms.WriteByte((byte)(0x80 | ((bits - 1) << 4) | (bits - 1))); // GCT flag + color res + GCT size
                    ms.WriteByte(0); // background color index
                    ms.WriteByte(0); // pixel aspect ratio

                    // Global Color Table (padded to tableSize)
                    for (int i = 0; i < tableSize; i++)
                    {
                        if (i < palette.Length) { ms.WriteByte(palette[i].r); ms.WriteByte(palette[i].g); ms.WriteByte(palette[i].b); }
                        else                    { ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); }
                    }

                    // Netscape 2.0 application extension — loop forever
                    ms.WriteByte(0x21); ms.WriteByte(0xFF); ms.WriteByte(0x0B);
                    WriteAscii(ms, "NETSCAPE2.0");
                    ms.WriteByte(0x03); ms.WriteByte(0x01);
                    WriteUInt16(ms, 0); // 0 = infinite loop
                    ms.WriteByte(0x00);

                    byte[] indices = new byte[width * height];
                    int lastDelay = 4;
                    for (int fi = 0; fi < pixels.Length; fi++)
                    {
                        Color32[] frame = pixels[fi];

                        int delayCs = (frameDelaysCs != null && fi < frameDelaysCs.Count) ? frameDelaysCs[fi] : lastDelay;
                        delayCs = Mathf.Clamp(delayCs, 1, 65535);
                        lastDelay = delayCs;

                        // Graphic Control Extension (per-frame delay)
                        ms.WriteByte(0x21); ms.WriteByte(0xF9); ms.WriteByte(0x04);
                        ms.WriteByte(0x00);            // packed: no transparency, disposal 0
                        WriteUInt16(ms, (ushort)delayCs);
                        ms.WriteByte(0x00);            // transparent color index (unused)
                        ms.WriteByte(0x00);            // block terminator

                        // Image Descriptor
                        ms.WriteByte(0x2C);
                        WriteUInt16(ms, 0); WriteUInt16(ms, 0);                       // left, top
                        WriteUInt16(ms, (ushort)width); WriteUInt16(ms, (ushort)height);
                        ms.WriteByte(0x00);            // no local color table, not interlaced

                        // Flip rows top-down so the GIF matches the orientation EncodeToPNG
                        // produces from the same bottom-up Unity texture.
                        int o = 0;
                        for (int y = height - 1; y >= 0; y--)
                        {
                            int row = y * width;
                            for (int x = 0; x < width; x++)
                            {
                                Color32 c = frame[row + x];
                                indices[o++] = cube[((c.r >> 3) << 10) | ((c.g >> 3) << 5) | (c.b >> 3)];
                            }
                        }

                        WriteImageData(ms, indices, minCodeSize);
                    }

                    ms.WriteByte(0x3B); // trailer
                    return ms.ToArray();
                }
            }

            // ---- palette (median cut) ----

            private static Color32[] BuildPalette(Color32[][] pixels, int colorCount)
            {
                long total = 0;
                foreach (Color32[] f in pixels) total += f.Length;

                const int targetSamples = 24000;
                int step = (int)Math.Max(1, total / targetSamples);

                List<Color32> samples = new List<Color32>(Math.Min((int)Math.Min(total, int.MaxValue), targetSamples + 16));
                int counter = 0;
                foreach (Color32[] frame in pixels)
                    for (int i = 0; i < frame.Length; i++)
                        if ((counter++ % step) == 0) samples.Add(frame[i]);

                if (samples.Count == 0) samples.Add(new Color32(0, 0, 0, 255));
                return MedianCut(samples, colorCount);
            }

            private static int ChannelVal(Color32 c, int ch) => ch == 0 ? c.r : (ch == 1 ? c.g : c.b);

            private static Color32[] MedianCut(List<Color32> pixels, int maxColors)
            {
                List<List<Color32>> boxes = new List<List<Color32>> { pixels };

                while (boxes.Count < maxColors)
                {
                    int bestIdx = -1, bestRange = -1, bestChannel = 0;
                    for (int b = 0; b < boxes.Count; b++)
                    {
                        List<Color32> box = boxes[b];
                        if (box.Count < 2) continue;

                        int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
                        foreach (Color32 c in box)
                        {
                            if (c.r < rMin) rMin = c.r; if (c.r > rMax) rMax = c.r;
                            if (c.g < gMin) gMin = c.g; if (c.g > gMax) gMax = c.g;
                            if (c.b < bMin) bMin = c.b; if (c.b > bMax) bMax = c.b;
                        }
                        int rR = rMax - rMin, gR = gMax - gMin, bR = bMax - bMin;
                        int range = Math.Max(rR, Math.Max(gR, bR));
                        if (range > bestRange)
                        {
                            bestRange   = range;
                            bestIdx     = b;
                            bestChannel = (rR >= gR && rR >= bR) ? 0 : (gR >= bR ? 1 : 2);
                        }
                    }

                    if (bestIdx < 0) break; // every box is a single color — can't split further

                    List<Color32> target = boxes[bestIdx];
                    int ch = bestChannel;
                    target.Sort((x, y) => ChannelVal(x, ch).CompareTo(ChannelVal(y, ch)));
                    int mid = target.Count / 2;
                    boxes[bestIdx] = target.GetRange(0, mid);
                    boxes.Add(target.GetRange(mid, target.Count - mid));
                }

                Color32[] palette = new Color32[boxes.Count];
                for (int i = 0; i < boxes.Count; i++)
                {
                    long r = 0, g = 0, b = 0;
                    foreach (Color32 c in boxes[i]) { r += c.r; g += c.g; b += c.b; }
                    int n = Math.Max(1, boxes[i].Count);
                    palette[i] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
                return palette;
            }

            // Precomputes nearest-palette-index for a 32×32×32 RGB cube (5 bits/channel) so
            // per-pixel mapping is a single array lookup instead of an O(palette) search.
            private static byte[] BuildCubeMap(Color32[] palette)
            {
                byte[] cube = new byte[32768];
                for (int r = 0; r < 32; r++)
                    for (int g = 0; g < 32; g++)
                        for (int b = 0; b < 32; b++)
                        {
                            int cr = (r << 3) | 4, cg = (g << 3) | 4, cb = (b << 3) | 4;
                            int best = 0; long bestD = long.MaxValue;
                            for (int i = 0; i < palette.Length; i++)
                            {
                                int dr = cr - palette[i].r, dg = cg - palette[i].g, db = cb - palette[i].b;
                                long d = (long)dr * dr + (long)dg * dg + (long)db * db;
                                if (d < bestD) { bestD = d; best = i; }
                            }
                            cube[(r << 10) | (g << 5) | b] = (byte)best;
                        }
                return cube;
            }

            // ---- LZW image data ----

            private static void WriteImageData(MemoryStream ms, byte[] indices, int minCodeSize)
            {
                ms.WriteByte((byte)minCodeSize);

                const int maxBits     = 12;
                const int maxMaxCode  = 1 << maxBits; // 4096

                int clearCode = 1 << minCodeSize;
                int endCode   = clearCode + 1;
                int codeSize  = minCodeSize + 1;
                int nextCode  = endCode + 1;
                int maxCode   = (1 << codeSize) - 1;
                bool clearFlag = false;

                Dictionary<int, int> dict = new Dictionary<int, int>();
                List<byte> outBytes = new List<byte>();
                int bitBuffer = 0, bitCount = 0;

                // Mirrors the canonical (Poskanzer/UNIX-compress) GIF LZW output routine: a
                // code is written at the CURRENT code size, then the code size grows lazily —
                // only once nextCode has already exceeded the current size's range. Bumping any
                // earlier writes a code one bit too wide and desyncs every GIF decoder.
                Action<int> emit = code =>
                {
                    bitBuffer |= code << bitCount;
                    bitCount += codeSize;
                    while (bitCount >= 8)
                    {
                        outBytes.Add((byte)(bitBuffer & 0xFF));
                        bitBuffer >>= 8;
                        bitCount   -= 8;
                    }

                    if (clearFlag)
                    {
                        codeSize  = minCodeSize + 1;
                        maxCode   = (1 << codeSize) - 1;
                        clearFlag = false;
                    }
                    else if (nextCode > maxCode && codeSize < maxBits)
                    {
                        codeSize++;
                        maxCode = (codeSize == maxBits) ? maxMaxCode : ((1 << codeSize) - 1);
                    }
                };

                emit(clearCode);

                if (indices.Length > 0)
                {
                    int current = indices[0];
                    for (int i = 1; i < indices.Length; i++)
                    {
                        int k   = indices[i];
                        int key = (current << 8) | k;
                        if (dict.TryGetValue(key, out int code))
                        {
                            current = code;
                        }
                        else
                        {
                            emit(current);
                            current = k;
                            if (nextCode < maxMaxCode)
                            {
                                dict[key] = nextCode++;
                            }
                            else
                            {
                                // Table full: reset, then emit a clear code (written at the
                                // current size; emit() then drops the size back to the start).
                                dict.Clear();
                                nextCode  = endCode + 1;
                                clearFlag = true;
                                emit(clearCode);
                            }
                        }
                    }
                    emit(current);
                }

                emit(endCode);
                if (bitCount > 0) outBytes.Add((byte)(bitBuffer & 0xFF));

                // Pack into sub-blocks of at most 255 data bytes, terminated by a zero block.
                int offset = 0;
                while (offset < outBytes.Count)
                {
                    int chunk = Math.Min(255, outBytes.Count - offset);
                    ms.WriteByte((byte)chunk);
                    for (int i = 0; i < chunk; i++) ms.WriteByte(outBytes[offset + i]);
                    offset += chunk;
                }
                ms.WriteByte(0x00);
            }

            // ---- little-endian / ascii writers ----

            private static void WriteUInt16(MemoryStream ms, ushort v)
            {
                ms.WriteByte((byte)(v & 0xFF));
                ms.WriteByte((byte)((v >> 8) & 0xFF));
            }

            private static void WriteAscii(MemoryStream ms, string s)
            {
                foreach (char c in s) ms.WriteByte((byte)c);
            }
        }

        // ==================== Public API (backward-compatible) ====================

        public static void CaptureScreenshotMenuItem() => CaptureGameViewAndSave();

        public static string CaptureGameViewAndSave(
            string customPath = null, string customFilename = null,
            int? width = null, int? height = null)
        {
            Texture2D tex = CaptureGameViewTexture();
            if (tex == null) return null;
            FlipTextureVertically(tex);

            string fn  = !string.IsNullOrEmpty(customFilename)
                ? (customFilename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? customFilename : customFilename + ".png")
                : $"GameView-{Timestamp()}.png";

            string dir = !string.IsNullOrEmpty(customPath)
                ? customPath
                : Path.Combine(Path.GetDirectoryName(Application.dataPath), "Screenshots");

            Directory.CreateDirectory(dir);
            string savePath = Path.Combine(dir, fn);
            File.WriteAllBytes(savePath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            CodelyLogger.Log($"[ManageScreenshot] Saved: {savePath}");
            return savePath;
        }

        public static string CaptureAndSave(
            string customPath = null, string customFilename = null,
            int? width = null, int? height = null,
            Camera specificCamera = null, string cameraPrefix = null)
        {
            Camera cam = specificCamera ?? Camera.main;
            if (cam == null) { CodelyLogger.LogError("[ManageScreenshot] No camera found!"); return null; }

            int w = width  ?? 1920;
            int h = height ?? 1080;
            Texture2D tex = RenderCameraToTexture(cam, w, h);
            if (tex == null) return null;

            byte[] bytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            string prefix = cameraPrefix ?? (specificCamera != null ? specificCamera.name : "MainCamera");
            string fn = !string.IsNullOrEmpty(customFilename)
                ? (customFilename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? customFilename : customFilename + ".png")
                : $"{prefix}-{Timestamp()}.png";

            string dir = !string.IsNullOrEmpty(customPath)
                ? customPath
                : Path.Combine(Path.GetDirectoryName(Application.dataPath), "Screenshots");

            Directory.CreateDirectory(dir);
            string savePath = Path.Combine(dir, fn);
            File.WriteAllBytes(savePath, bytes);
            CodelyLogger.Log($"[ManageScreenshot] Saved: {savePath}");
            return savePath;
        }
    }
}
