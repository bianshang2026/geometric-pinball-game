using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Native
{
    // Popup menu interception — build, send, and execute popup menus on the
    // frontend HTML overlay instead of native Win32/macOS popup windows.
    // Handles GenericMenu (MenuController), DisplayPopupMenu (main menu),
    // FlexibleMenu, and other popup types. Extracts menu items via
    // reflection, serialises them to JSON, and sends through DataChannel.
    internal static partial class NativeWindowBridgeHost
    {
        // ----------------------------------------------------------------
        // Popup-specific fields & constants
        // ----------------------------------------------------------------

        // Last SceneView UITK toolbar trigger rect (in Unity pixel coordinates).
        // Used to provide pixel-accurate popup/panel anchor from Unity side.
        private static bool s_HasToolbarAnchorRectPx;
        private static Rect s_LastToolbarAnchorRectPx;
        // Keep popup payload under typical remote SCTP/DataChannel max message size.
        private const int kMaxDataChannelPayloadBytes = 63 * 1024;
        // Keep chunk body small enough after JSON escaping overhead.
        private const int kPopupChunkDataMaxChars = 12 * 1024;

        // Path used by C++ MenuController for temporary popup menus.
        private const string kPopupMenuPath = "CONTEXT/TEMPORARY-OBJECT-DISPLAY";

        // Reflection cache for Menu.RemoveMenuItem — used to cancel native delayed popups.
        private static MethodInfo s_MenuRemoveMenuItemMethod;
        private static MethodInfo s_MenuGetMenuItemsMethod;
        private static bool s_MenuRemoveMenuItemReflectionDone;

        // Reflection cache for Unsupported.GetSubmenus/GetSubmenusCommands fallback
        // when Menu.GetMenuItems is not available (e.g. Unity 2019).
        private static MethodInfo s_UnsupportedGetSubmenusMethod;
        private static MethodInfo s_UnsupportedGetSubmenusCommandsMethod;
        private static MethodInfo s_MenuGetCheckedMethod;
        private static MethodInfo s_MenuGetEnabledMethod;
        private static MethodInfo s_MenuMenuItemExistsMethod;
        private static bool s_UnsupportedMenuReflectionDone;

        // Cached original popup indices from Unsupported.GetSubmenusCommands,
        // used to map frontend selection index back to the original popup index.
        private static int[] s_CachedPopupOriginalIndices;

        // Cached popup item full paths for ExecuteMenuItem callback.
        private static string[] s_CachedPopupItemPaths;
        // Cached FlexibleMenu callback and PopupWindow reference for selection.
        private static object s_CachedFlexibleMenu;
        private static object s_CachedFlexibleMenuItemProvider;
        private static System.Delegate s_CachedFlexibleMenuCallback;
        // Cached PopupList (ProjectBrowser search filter) selection callback/items.
        private static System.Delegate s_CachedPopupListSelectDelegate;
        private static object[] s_CachedPopupListItems;
        // Cached ConnectionTreeViewWindow items and their onSelected callbacks.
        private static System.Action[] s_CachedConnectionCallbacks;
        // Cached IGameViewSizeMenuUser for header toggle actions.
        private static object s_CachedGameViewSizeMenuUser;
        // Cached PopupCallbackInfo for delayed popup replay.
        // We intentionally clear PopupCallbackInfo.instance to suppress native popup UI,
        // so we keep this object and restore it when applying popup_select.
        private static object s_CachedDelayedPopupCallbackInfo;
        // Popup source type: "generic" (MenuController) or "flexible" (FlexibleMenu)
        private static string s_PopupSourceType;
        // Suppress target Repaint while a frontend popup overlay is active.
        private static bool s_SuppressTargetRepaintWhilePopupOpen;
        // Set on remote right-mousedown for Project/Hierarchy and cleared once
        // the menu is forwarded or cancelled. Prevents composite per-slot
        // Repaint from firing ShowDelayedContextMenu before mouseup/ContextClick.
        private static bool s_ExpectContextMenuForward;

        // Signature of the last MenuController items we sent to the frontend.
        // Format: "count|firstPath". Used to detect stale gDelayedContextMenu
        // data left by a failed ShowDelayedContextMenu call.
        // A new GenericMenu popup will have a different signature, allowing it through.
        private static string s_LastMenuControllerSignature;

        // When set, StripPopupMenuPathPrefix also strips this prefix from item paths.
        // Used for DisplayPopupMenu forwarding (e.g., "Assets", "GameObject").
        private static string s_DisplayPopupMenuStripPrefix;

        // Background auto-dismiss flag for TrackPopupMenuEx modal loops.
        // Set to 1 while SendEvent calls may trigger a native popup; a
        // ThreadPool worker posts WM_CANCELMODE to break the block.
        private static volatile int s_PopupAutoDismissActive;

        // Set to 1 by the auto-dismiss worker when it finds and dismisses
        // a native #32768 popup window. Used by the main thread to confirm
        // that TrackPopupMenuEx actually fired (timing is unreliable).
        private static volatile int s_AutoDismissFoundNativePopup;

        // ----------------------------------------------------------------
        // Popup payload chunking
        // ----------------------------------------------------------------

        /// <summary>
        /// Send oversized popup payload via chunked DataChannel messages.
        /// The browser reassembles payload and then handles it as normal popup_show JSON.
        /// </summary>
        private static bool SendPopupPayloadChunked(string popupId, string payloadJson, int payloadBytes)
        {
            if (string.IsNullOrEmpty(popupId) || string.IsNullOrEmpty(payloadJson)) return false;
            int totalChunks = (payloadJson.Length + kPopupChunkDataMaxChars - 1) / kPopupChunkDataMaxChars;
            if (totalChunks <= 0) totalChunks = 1;

            string begin = "{\"type\":\"popup_payload_begin\",\"id\":\"" +
                EscapeJsonString(popupId) +
                "\",\"totalChunks\":" + totalChunks +
                ",\"totalBytes\":" + payloadBytes + "}";
            if (!SendDataChannelMessage(begin)) return false;

            for (int i = 0; i < totalChunks; i++)
            {
                int start = i * kPopupChunkDataMaxChars;
                int len = Math.Min(kPopupChunkDataMaxChars, payloadJson.Length - start);
                string chunk = payloadJson.Substring(start, len);

                var sb = new StringBuilder(len + 128);
                sb.Append("{\"type\":\"popup_payload_chunk\",\"id\":\"");
                sb.Append(EscapeJsonString(popupId));
                sb.Append("\",\"seq\":");
                sb.Append(i);
                sb.Append(",\"data\":\"");
                sb.Append(EscapeJsonString(chunk));
                sb.Append("\"}");
                if (!SendDataChannelMessage(sb.ToString())) return false;
            }

            string end = "{\"type\":\"popup_payload_end\",\"id\":\"" + EscapeJsonString(popupId) + "\"}";
            return SendDataChannelMessage(end);
        }

        // ----------------------------------------------------------------
        // Toolbar anchor rect (for pixel-accurate popup positioning)
        // ----------------------------------------------------------------

        /// <summary>
        /// Try to capture the clicked UITK toolbar element rect in SceneView.
        /// The rect is stored in Unity pixel coordinates for frontend anchoring.
        /// </summary>
        private static void TryCaptureToolbarAnchorRect(float x, float y, float ppp)
        {
            s_HasToolbarAnchorRectPx = false;
            if (s_OffscreenTarget == null || ppp <= 0f) return;
            if (!(s_OffscreenTarget is SceneView)) return;

            try
            {
                var root = s_OffscreenTarget.rootVisualElement;
                if (root == null || root.panel == null) return;

                var mousePt = new Vector2(x, y);
                var picked = root.panel.Pick(mousePt);
                if (picked == null) return;

                VisualElement anchor = null;
                for (var el = picked; el != null; el = el.parent)
                {
                    Rect wb = el.worldBound;
                    if (!wb.Contains(mousePt)) continue;
                    if (wb.width < 14f || wb.width > 180f) continue;
                    if (wb.height < 12f || wb.height > 40f) continue;
                    anchor = el;
                }
                if (anchor == null) return;

                Rect bound = anchor.worldBound;
                s_LastToolbarAnchorRectPx = new Rect(
                    bound.x * ppp,
                    bound.y * ppp,
                    bound.width * ppp,
                    bound.height * ppp);
                s_HasToolbarAnchorRectPx = s_LastToolbarAnchorRectPx.width > 0.1f &&
                                           s_LastToolbarAnchorRectPx.height > 0.1f;

                if (s_HasToolbarAnchorRectPx)
                {
                    LogVerbose($"[NWB-AnchorRect] Captured anchor rect px=({s_LastToolbarAnchorRectPx.x:F0},{s_LastToolbarAnchorRectPx.y:F0},{s_LastToolbarAnchorRectPx.width:F0},{s_LastToolbarAnchorRectPx.height:F0}) type={anchor.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-AnchorRect] Capture failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Append optional Unity-side anchor rect for pixel-accurate alignment.
        /// </summary>
        private static void AppendToolbarAnchorRectJson(StringBuilder sb)
        {
            if (sb == null || !s_HasToolbarAnchorRectPx) return;
            if (s_LastToolbarAnchorRectPx.width <= 0f || s_LastToolbarAnchorRectPx.height <= 0f) return;

            sb.Append(",\"anchorRect\":{\"x\":");
            sb.Append(Mathf.RoundToInt(s_LastToolbarAnchorRectPx.x));
            sb.Append(",\"y\":");
            sb.Append(Mathf.RoundToInt(s_LastToolbarAnchorRectPx.y));
            sb.Append(",\"w\":");
            sb.Append(Mathf.RoundToInt(s_LastToolbarAnchorRectPx.width));
            sb.Append(",\"h\":");
            sb.Append(Mathf.RoundToInt(s_LastToolbarAnchorRectPx.height));
            sb.Append("}");
        }

        /// <summary>
        /// Convert a logical screen-space point to frontend frame pixels.
        /// In composite mode this applies frame bounds/scale/offset mapping.
        /// </summary>
        private static Vector2 ConvertLogicalScreenPointToFramePixels(Vector2 logicalScreenPt, float ppp)
        {
            if (s_CompositeActive)
            {
                float scaleX = Mathf.Max(s_CompositeFrameScalePixels.x * ppp, 0.001f);
                float scaleY = Mathf.Max(s_CompositeFrameScalePixels.y * ppp, 0.001f);
                return new Vector2(
                    s_CompositeFrameOffsetPixels.x + (logicalScreenPt.x - s_CompositeFrameBoundsLogical.xMin) * scaleX,
                    s_CompositeFrameOffsetPixels.y + (logicalScreenPt.y - s_CompositeFrameBoundsLogical.yMin) * scaleY);
            }

            // Single-view mode expects local pixels (point * ppp).
            return logicalScreenPt * ppp;
        }

        /// <summary>
        /// Convert a view-local point to frontend frame pixels.
        /// </summary>
        private static bool TryConvertViewLocalPointToFramePixels(object sourceView, Vector2 localPt, float ppp, out Vector2 framePx)
        {
            framePx = Vector2.zero;
            if (ppp <= 0f) return false;

            if (sourceView == null)
            {
                framePx = localPt * ppp;
                return true;
            }

            if (!s_CompositeActive)
            {
                framePx = localPt * ppp;
                return true;
            }

            Rect viewRect = GetViewScreenPosition(sourceView);
            Vector2 logicalScreen = new Vector2(viewRect.xMin + localPt.x, viewRect.yMin + localPt.y);
            framePx = ConvertLogicalScreenPointToFramePixels(logicalScreen, ppp);
            return true;
        }

        /// <summary>
        /// Resolve popup source view. Uses explicit view when provided,
        /// otherwise falls back to current offscreen target's parent view.
        /// </summary>
        private static object ResolvePopupSourceView(object explicitSourceView = null)
        {
            if (explicitSourceView != null) return explicitSourceView;
            return s_OffscreenTarget != null ? GetParentView(s_OffscreenTarget) : null;
        }

        /// <summary>
        /// Convert local click coordinates to popup position in frame pixels.
        /// popY = max(minTopLocalY, clickY + clickYOffsetLocal) after conversion.
        /// </summary>
        private static void ComputePopupPositionFromLocalClick(
            float clickX,
            float clickY,
            float minTopLocalY,
            float clickYOffsetLocal,
            out float popX,
            out float popY,
            object explicitSourceView = null,
            float? explicitPpp = null)
        {
            float ppp = explicitPpp ?? EditorGUIUtility.pixelsPerPoint;
            object sourceView = ResolvePopupSourceView(explicitSourceView);

            TryConvertViewLocalPointToFramePixels(
                sourceView, new Vector2(clickX, clickY), ppp, out Vector2 clickPxFrame);
            TryConvertViewLocalPointToFramePixels(
                sourceView, new Vector2(clickX, minTopLocalY), ppp, out Vector2 minTopPxFrame);
            TryConvertViewLocalPointToFramePixels(
                sourceView, new Vector2(clickX, clickY + clickYOffsetLocal), ppp, out Vector2 clickOffsetPxFrame);

            popX = clickPxFrame.x;
            popY = Mathf.Max(minTopPxFrame.y, clickOffsetPxFrame.y);
        }

        // ----------------------------------------------------------------
        // MenuController helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Check whether MenuController currently has queued items under
        /// kPopupMenuPath. RemoveMenuItem logs an internal Unity error when
        /// the menu does not exist, so we must guard before removing.
        /// </summary>
        private static bool HasPendingPopupMenuItems()
        {
            try
            {
                if (s_MenuGetMenuItemsMethod != null)
                {
                    var items = s_MenuGetMenuItemsMethod.Invoke(null,
                        new object[] { kPopupMenuPath, true, false }) as System.Array;
                    return items != null && items.Length > 0;
                }
                // Fallback: Unsupported.GetSubmenus (public API, available in Unity 2019)
                EnsureUnsupportedMenuReflection();
                if (s_UnsupportedGetSubmenusMethod != null)
                {
                    var subs = s_UnsupportedGetSubmenusMethod.Invoke(null,
                        new object[] { kPopupMenuPath }) as string[];
                    return subs != null && subs.Length > 0;
                }
                // Last resort: Menu.MenuItemExists
                if (s_MenuMenuItemExistsMethod != null)
                    return (bool)s_MenuMenuItemExistsMethod.Invoke(null, new object[] { kPopupMenuPath });
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Initialize reflection cache for Unsupported.GetSubmenus/GetSubmenusCommands
        /// and Menu.GetChecked/GetEnabled — used as fallback when Menu.GetMenuItems
        /// does not exist (e.g. Unity 2019).
        /// </summary>
        private static void EnsureUnsupportedMenuReflection()
        {
            if (s_UnsupportedMenuReflectionDone) return;
            s_UnsupportedMenuReflectionDone = true;
            try
            {
                var asm = typeof(EditorWindow).Assembly;
                var unsupportedType = asm.GetType("UnityEditor.Unsupported");
                if (unsupportedType != null)
                {
                    s_UnsupportedGetSubmenusMethod = unsupportedType.GetMethod("GetSubmenus",
                        BindingFlags.Static | BindingFlags.Public,
                        null, new System.Type[] { typeof(string) }, null);
                    s_UnsupportedGetSubmenusCommandsMethod = unsupportedType.GetMethod("GetSubmenusCommands",
                        BindingFlags.Static | BindingFlags.Public,
                        null, new System.Type[] { typeof(string) }, null);
                }
                var menuType = asm.GetType("UnityEditor.Menu");
                if (menuType != null)
                {
                    s_MenuGetCheckedMethod = menuType.GetMethod("GetChecked",
                        BindingFlags.Static | BindingFlags.Public,
                        null, new System.Type[] { typeof(string) }, null);
                    s_MenuGetEnabledMethod = menuType.GetMethod("GetEnabled",
                        BindingFlags.Static | BindingFlags.Public,
                        null, new System.Type[] { typeof(string) }, null);
                    s_MenuMenuItemExistsMethod = menuType.GetMethod("MenuItemExists",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        null, new System.Type[] { typeof(string) }, null);
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-MenuFallback] Reflection init error: {ex.Message}");
            }
        }

        /// <summary>
        /// Build popup JSON from Unsupported.GetSubmenus + GetSubmenusCommands.
        /// Generic fallback for Unity 2019 where Menu.GetMenuItems is unavailable.
        /// Works for ANY DoPopup-based menu, not just specific EditorWindow types.
        /// </summary>
        private static bool SendDelayedPopupViaSubmenus(
            float relX, float relY, float ppp)
        {
            EnsureUnsupportedMenuReflection();
            if (s_UnsupportedGetSubmenusMethod == null)
            {
                LogVerbose("[NWB-MenuFallback] Unsupported.GetSubmenus not available");
                return false;
            }

            string[] submenus = s_UnsupportedGetSubmenusMethod.Invoke(null,
                new object[] { kPopupMenuPath }) as string[];
            if (submenus == null || submenus.Length == 0)
            {
                LogVerbose("[NWB-MenuFallback] No items under " + kPopupMenuPath);
                return false;
            }

            // Read original popup indices (command = IntToString(originalIndex))
            string[] commands = null;
            if (s_UnsupportedGetSubmenusCommandsMethod != null)
            {
                commands = s_UnsupportedGetSubmenusCommandsMethod.Invoke(null,
                    new object[] { kPopupMenuPath }) as string[];
            }

            string prefix = kPopupMenuPath + "/";

            s_FrontendPopupIdCounter++;
            s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;

            var sb = new StringBuilder(2048);
            sb.Append("{\"type\":\"popup_show\",\"id\":\"");
            sb.Append(s_CurrentFrontendPopupId);
            sb.Append("\",\"x\":");
            sb.Append(Mathf.RoundToInt(relX));
            sb.Append(",\"y\":");
            sb.Append(Mathf.RoundToInt(relY));
            sb.Append(",\"width\":0,\"height\":0,\"items\":[");

            s_CachedPopupItemPaths = new string[submenus.Length];
            s_CachedPopupOriginalIndices = new int[submenus.Length];

            for (int i = 0; i < submenus.Length; i++)
            {
                if (i > 0) sb.Append(',');
                string fullPath = submenus[i];
                s_CachedPopupItemPaths[i] = fullPath;

                // Parse original index from command string
                int origIdx = i;
                if (commands != null && i < commands.Length)
                {
                    int.TryParse(commands[i], out origIdx);
                }
                s_CachedPopupOriginalIndices[i] = origIdx;

                // Strip prefix for display label
                string label = fullPath.StartsWith(prefix, StringComparison.Ordinal)
                    ? fullPath.Substring(prefix.Length)
                    : fullPath;

                bool isChecked = false;
                bool isEnabled = true;
                try
                {
                    if (s_MenuGetCheckedMethod != null)
                        isChecked = (bool)s_MenuGetCheckedMethod.Invoke(null, new object[] { fullPath });
                    if (s_MenuGetEnabledMethod != null)
                        isEnabled = (bool)s_MenuGetEnabledMethod.Invoke(null, new object[] { fullPath });
                }
                catch { }

                sb.Append("{\"index\":");
                sb.Append(i);
                sb.Append(",\"path\":\"");
                sb.Append(EscapeJsonString(fullPath));
                sb.Append("\",\"label\":\"");
                sb.Append(EscapeJsonString(label));
                sb.Append("\",\"tooltip\":\"\",\"enabled\":");
                sb.Append(isEnabled ? "true" : "false");
                sb.Append(",\"checked\":");
                sb.Append(isChecked ? "true" : "false");
                sb.Append(",\"separator\":false}");
            }
            sb.Append("]}");

            string json = sb.ToString();
            bool sent = SendDataChannelMessage(json);
            s_FrontendPopupSent = sent;
            if (sent)
            {
                // Suppress target repaints while popup is open. On Windows,
                // ShowDelayedContextMenu runs at the START of the next
                // OnInputEvent. If we don't suppress repaints, the offscreen
                // capture loop sends Layout/Repaint events that trigger the
                // native TrackPopupMenuEx popup alongside our frontend popup.
                s_SuppressTargetRepaintWhilePopupOpen = true;
                TryDismissWin32TrackPopupMenu();
                s_PendingNativePopupCloseFrames = Math.Max(
                    s_PendingNativePopupCloseFrames, 60);
            }
            LogVerbose($"[NWB-MenuFallback] Sent popup via Unsupported.GetSubmenus: id={s_CurrentFrontendPopupId} items={submenus.Length} sent={sent}");
            return sent;
        }

        // ----------------------------------------------------------------
        // Native popup dismissal
        // ----------------------------------------------------------------

        /// <summary>
        /// Enumerate ALL windows (visible AND hidden) belonging to the current
        /// Unity process. Unlike GetAllUnityHwnds() which filters by
        /// IsWindowVisible, this includes hidden windows — essential during
        /// streaming when all editor windows are hidden via HWND_BOTTOM.
        /// </summary>
        private static List<IntPtr> GetAllProcessHwnds()
        {
            var result = new List<IntPtr>();
#if UNITY_EDITOR_WIN
            uint currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr hWnd = GetWindow(GetDesktopWindow(), GW_CHILD);
            int safety = 0;
            while (hWnd != IntPtr.Zero && safety < 4096)
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == currentPid)
                    result.Add(hWnd);
                hWnd = GetWindow(hWnd, GW_HWNDNEXT);
                safety++;
            }
#endif
            return result;
        }

        /// <summary>
        /// Dismiss any leftover native Win32 popup menu by targeting ONLY the
        /// popup menu window (#32768). This is called AFTER we have already
        /// read and forwarded the menu items, so there is no TrackPopupMenuEx
        /// modal loop to break. We only need to close a visible popup.
        ///
        /// IMPORTANT: Do NOT send WM_KEYDOWN+VK_ESCAPE to ordinary editor
        /// windows — the ESC key events accumulate in the message queue and
        /// can close the StreamingMaskWindow or disrupt other windows.
        /// </summary>
        private static void TryDismissWin32TrackPopupMenu()
        {
#if UNITY_EDITOR_WIN
            try
            {
                uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

                // Only target the popup menu window (#32768) — never blast
                // WM_CANCELMODE or VK_ESCAPE to all process windows.
                IntPtr popupHwnd = FindOwnPopupMenuWindow(myPid);
                if (popupHwnd != IntPtr.Zero)
                {
                    const uint WM_CLOSE = 0x0010;
                    PostMessageW(popupHwnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                    PostMessageW(popupHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    LogVerbose($"[NWB-ContextMenu] Posted WM_CLOSE to popup #32768 hwnd=0x{popupHwnd.ToInt64():X}");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-ContextMenu] dismiss failed: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// Start a background worker that aggressively dismisses any
        /// TrackPopupMenuEx modal loop that appears during our SendEvent calls.
        ///
        /// TrackPopupMenuEx runs a modal message loop (xxxMNLoop) that blocks
        /// the calling thread. During streaming the editor windows are hidden,
        /// so the popup appears on the desktop and the user cannot interact with
        /// the streamed view.
        ///
        /// Dismiss strategies:
        ///   1. WM_CANCELMODE to all pre-captured process windows (harmless to
        ///      normal windows, triggers xxxEndMenu in the menu modal loop)
        ///   2. FindOwnPopupMenuWindow() to locate the popup menu window that is
        ///      created AFTER we start, then send WM_CANCELMODE + VK_ESCAPE
        ///      specifically to it (the menu loop processes keyboard input)
        ///   3. WM_CANCELMODE to the foreground window
        ///
        /// IMPORTANT: VK_ESCAPE is ONLY sent to #32768 popup menu windows.
        /// Sending VK_ESCAPE to ordinary editor windows causes ESC key events
        /// to accumulate in the message queue, which can close the
        /// StreamingMaskWindow and terminate the streaming session.
        /// </summary>
        /// <param name="detectOnly">When true, the worker only detects and
        /// dismisses the #32768 popup directly (no broadcast WM_CANCELMODE
        /// to editor windows). Uses SpinWait for fast polling so the popup
        /// is caught within ~10μs of creation. Use for DisplayPopupMenu
        /// targets where broadcast WM_CANCELMODE would kill the popup
        /// via the TrackPopupMenuEx modal loop before
        /// FindOwnPopupMenuWindow can detect it (race condition).</param>
        private static void ScheduleNativePopupAutoDismiss(bool detectOnly = false)
        {
#if UNITY_EDITOR_WIN
            System.Threading.Interlocked.Exchange(ref s_PopupAutoDismissActive, 1);
            System.Threading.Interlocked.Exchange(ref s_AutoDismissFoundNativePopup, 0);
            var preHwnds = GetAllProcessHwnds();
            long startTick = System.Diagnostics.Stopwatch.GetTimestamp();
            double tickFreq = System.Diagnostics.Stopwatch.Frequency;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                const uint VK_ESCAPE = 0x1B;
                int iteration = 0;
                bool loggedStart = false;

                while (System.Threading.Interlocked.CompareExchange(
                    ref s_PopupAutoDismissActive, 0, 0) == 1)
                {
                    try
                    {
                        iteration++;
                        double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTick)
                            / tickFreq * 1000.0;

                        if (!loggedStart)
                        {
                            loggedStart = true;
                            LogVerbose($"[NWB-AutoDismiss] Worker started: {preHwnds.Count} " +
                                $"pre-captured HWNDs, iteration={iteration}, elapsed={elapsedMs:F1}ms" +
                                $" detectOnly={detectOnly}");
                        }

                        // Strategy 1: Detect #32768 popup owned by our
                        // process. Uses FindWindowExW iteration instead of
                        // FindWindowW, because FindWindowW returns the first
                        // #32768 window (which may belong to another process
                        // like Explorer or the taskbar).
                        IntPtr popupHwnd = FindOwnPopupMenuWindow(myPid);
                        if (popupHwnd != IntPtr.Zero)
                        {
                            System.Threading.Interlocked.Exchange(
                                ref s_AutoDismissFoundNativePopup, 1);
                            // TrackPopupMenuEx's modal loop only responds to
                            // WM_CANCELMODE sent to the OWNER window, not
                            // to the popup window itself. Broadcast to all
                            // pre-captured HWNDs (which include the owner).
                            foreach (IntPtr hwnd in preHwnds)
                                PostMessageW(hwnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                            PostMessageW(popupHwnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                            PostMessageW(popupHwnd, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero);
                            if (iteration <= 3 || iteration % 10 == 0)
                            {
                                LogVerbose($"[NWB-AutoDismiss] Found popup #32768 hwnd=0x{popupHwnd.ToInt64():X}, " +
                                    $"iter={iteration}, elapsed={elapsedMs:F1}ms detectOnly={detectOnly}");
                            }
                        }

                        if (!detectOnly)
                        {
                            // Strategy 2: Pre-emptive broadcast WM_CANCELMODE
                            // to all process windows BEFORE popup is found.
                            // Skipped in detectOnly mode to avoid stale
                            // WM_CANCELMODE dismissing popup before detection.
                            // (When popup IS found, broadcast happens above.)
                            foreach (IntPtr hwnd in preHwnds)
                            {
                                PostMessageW(hwnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                            }

                            // Strategy 3: Also target the foreground window with
                            // WM_CANCELMODE only (no VK_ESCAPE).
                            IntPtr fg = GetForegroundWindow();
                            if (fg != IntPtr.Zero)
                            {
                                GetWindowThreadProcessId(fg, out uint fgPid);
                                if (fgPid == myPid)
                                    PostMessageW(fg, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                            }
                        }

                        if (iteration == 50)
                        {
                            LogVerbose($"[NWB-AutoDismiss] Still running after 50 iterations, " +
                                $"elapsed={elapsedMs:F1}ms, popup=#32768 found={popupHwnd != IntPtr.Zero}");
                        }
                    }
                    catch { }

                    // In detectOnly mode, use SpinWait for fast polling (~10μs
                    // per iteration) so the popup is caught within microseconds
                    // of creation. Switch to Sleep(1) after 20ms to limit CPU
                    // in edge cases (e.g., popup never created).
                    if (detectOnly)
                    {
                        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startTick)
                            / tickFreq * 1000.0;
                        if (ms < 20.0)
                            System.Threading.Thread.SpinWait(100);
                        else
                            System.Threading.Thread.Sleep(1);
                    }
                    else
                    {
                        System.Threading.Thread.Sleep(1);
                    }
                }

                double totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTick)
                    / tickFreq * 1000.0;
                LogVerbose($"[NWB-AutoDismiss] Worker stopped after {iteration} iterations, " +
                    $"total={totalMs:F1}ms detectOnly={detectOnly}");
            });
#endif
        }

        private static void CancelNativePopupAutoDismiss()
        {
            System.Threading.Interlocked.Exchange(ref s_PopupAutoDismissActive, 0);
        }

        /// <summary>
        /// Combined cleanup: close Unity ContainerWindow popups (sm=2)
        /// and dismiss Win32 #32768 TrackPopupMenuEx popups. These two
        /// operations are almost always needed together after intercepting
        /// a native popup.
        /// </summary>
        private static void DismissAllNativePopups()
        {
            ScanAndCloseNativePopups();
            TryDismissWin32TrackPopupMenu();
        }

#if UNITY_EDITOR_WIN
        /// <summary>
        /// Find a popup menu window (#32768 class) owned by our process.
        /// Unlike FindWindowW which only returns the FIRST #32768 window
        /// (which may belong to another process like the taskbar or
        /// Explorer), this iterates through ALL #32768 windows until
        /// one matching our PID is found.
        /// </summary>
        private static IntPtr FindOwnPopupMenuWindow(uint myPid)
        {
            IntPtr hwnd = IntPtr.Zero;
            while (true)
            {
                hwnd = FindWindowExW(IntPtr.Zero, hwnd, "#32768", null);
                if (hwnd == IntPtr.Zero) break;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == myPid) return hwnd;
            }
            return IntPtr.Zero;
        }
#endif

        /// <summary>
        /// Probe whether DisplayPopupMenu queued a gDelayedContextMenu.
        /// Sends a Layout event wrapped with auto-dismiss protection:
        /// GUIView::OnInputEvent checks HasDelayedContextMenu() at the top
        /// of EVERY event — if true, ShowDelayedContextMenu() fires
        /// TrackPopupMenuEx. The auto-dismiss worker detects the #32768
        /// popup and dismisses it immediately. Returns true if a popup
        /// was found and dismissed (meaning DisplayPopupMenu was called
        /// during the preceding mousedown).
        /// </summary>
        private static bool ProbeAndDismissDelayedContextMenu()
        {
#if UNITY_EDITOR_WIN
            if (s_OffscreenTarget == null) return false;

            System.Threading.Interlocked.Exchange(ref s_AutoDismissFoundNativePopup, 0);
            ScheduleNativePopupAutoDismiss(detectOnly: true);
            long probeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                s_OffscreenTarget.SendEvent(new Event { type = EventType.Layout });
            }
            catch (Exception) { }
            finally
            {
                CancelNativePopupAutoDismiss();
            }
            TryDismissWin32TrackPopupMenu();

            double probeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - probeStart)
                / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
            bool found = System.Threading.Interlocked
                .CompareExchange(ref s_AutoDismissFoundNativePopup, 0, 0) == 1;
            LogVerbose($"[NWB-DisplayPopup] ProbeDelayed: found={found} elapsed={probeMs:F1}ms");
            return found;
#else
            return false;
#endif
        }

        /// <summary>
        /// Clear gDelayedContextMenu by removing menu items first (so
        /// ShowDelayedContextMenu finds nothing and skips TrackPopupMenuEx),
        /// then sending a Layout event through GUIView.OnInputEvent which
        /// checks HasDelayedContextMenu() and clears the global.
        ///
        /// IMPORTANT: This method removes items from MenuController via
        /// CancelNativeDelayedPopup, so it must NOT be called while the
        /// frontend popup is active and waiting for popup_select — calling
        /// it prematurely would cause ExecuteMenuItem to fail.
        /// Safe call sites:
        ///   1. ClearFrontendPopupState (AFTER ExecuteMenuItem)
        ///   2. FAILED path (DC send failed, no frontend popup)
        ///   3. Exception handler cleanup
        /// </summary>
        private static void ConsumeDelayedContextMenu()
        {
#if UNITY_EDITOR_WIN
            if (s_OffscreenTarget == null) return;

            long consumeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            double freq = System.Diagnostics.Stopwatch.Frequency;

            // Step 1: Remove menu items BEFORE triggering the consume event.
            bool hadItems = HasPendingPopupMenuItems();
            CancelNativeDelayedPopup();
            bool hasItemsAfter = HasPendingPopupMenuItems();
            LogVerbose($"[NWB-ConsumeCtx] Step1: hadItems={hadItems} hasItemsAfter={hasItemsAfter}");

            // Suppress C++ ErrorString("Menu %s couldn't be found")
            var prevFilter = Debug.unityLogger.filterLogType;
            Debug.unityLogger.filterLogType = LogType.Exception;

            // Step 2: Send Layout event to trigger HasDelayedContextMenu check.
            ScheduleNativePopupAutoDismiss();
            try
            {
                long preSend = System.Diagnostics.Stopwatch.GetTimestamp();
                s_OffscreenTarget.SendEvent(new Event { type = EventType.Layout });
                double sendMs = (System.Diagnostics.Stopwatch.GetTimestamp() - preSend) / freq * 1000.0;
                bool stillHasItems = HasPendingPopupMenuItems();
                double totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - consumeStart) / freq * 1000.0;
                LogVerbose($"[NWB-ConsumeCtx] Step2: Layout sent in {sendMs:F1}ms, itemsAfterLayout={stillHasItems}, total={totalMs:F1}ms");
            }
            catch (Exception ex)
            {
                CodelyLogger.Log($"[NWB-ConsumeCtx] Step2 FAILED: {ex.Message}");
            }
            finally
            {
                CancelNativePopupAutoDismiss();
                Debug.unityLogger.filterLogType = prevFilter;
            }

            TryDismissWin32TrackPopupMenu();
#endif
        }

        /// <summary>
        /// Start a background thread that monitors for #32768 popup windows
        /// and dismisses them for a fixed duration. Self-terminates after
        /// maxDurationMs without requiring explicit cancellation.
        /// Used during StopOffscreenCapture to catch ShowDelayedContextMenu
        /// that fires on the next C++ DoRepaint after windows are restored.
        /// </summary>
        private static void ScheduleTimeLimitedPopupDismiss(int maxDurationMs)
        {
#if UNITY_EDITOR_WIN
            var preHwnds = GetAllProcessHwnds();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                const uint VK_ESCAPE = 0x1B;
                long startTick = System.Diagnostics.Stopwatch.GetTimestamp();
                double tickFreq = System.Diagnostics.Stopwatch.Frequency;
                int dismissCount = 0;

                while (true)
                {
                    double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - startTick)
                        / tickFreq * 1000.0;
                    if (elapsed > maxDurationMs) break;

                    try
                    {
                        IntPtr popupHwnd = FindOwnPopupMenuWindow(myPid);
                        if (popupHwnd != IntPtr.Zero)
                        {
                            foreach (IntPtr hwnd in preHwnds)
                                PostMessageW(hwnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                            PostMessageW(popupHwnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                            PostMessageW(popupHwnd, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero);
                            dismissCount++;
                            if (dismissCount <= 3)
                                LogVerbose($"[NWB-StopGuard] Dismissed #32768 hwnd=0x{popupHwnd.ToInt64():X} at {elapsed:F0}ms");
                        }
                    }
                    catch { }
                    System.Threading.Thread.Sleep(5);
                }

                if (dismissCount > 0)
                    LogVerbose($"[NWB-StopGuard] Finished: dismissed={dismissCount}");
            });
#endif
        }

        private static void CancelNativeDelayedPopup(bool forceRemove = false)
        {
#if UNITY_EDITOR_WIN
            try
            {

                if (!s_MenuRemoveMenuItemReflectionDone)
                {
                    s_MenuRemoveMenuItemReflectionDone = true;
                    var menuType = typeof(EditorWindow).Assembly.GetType("UnityEditor.Menu");
                    if (menuType != null)
                    {
                        s_MenuGetMenuItemsMethod = menuType.GetMethod("GetMenuItems",
                            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                            null, new[] { typeof(string), typeof(bool), typeof(bool) }, null);
                        s_MenuRemoveMenuItemMethod = menuType.GetMethod("RemoveMenuItem",
                            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                            null, new[] { typeof(string) }, null);
                    }
                    if (s_MenuRemoveMenuItemMethod == null)
                        CodelyLogger.LogWarning("[NWB-PopupCancel] Menu.RemoveMenuItem not found");
                }
                if (s_MenuRemoveMenuItemMethod != null)
                {
                    // Guard: avoid Unity internal error "Menu ... couldn't be found"
                    // when this path is called for non-MenuController popups.
                    if (!HasPendingPopupMenuItems())
                    {
                        LogVerbose("[NWB-PopupCancel] Skip remove: no pending MenuController popup items");
                        return;
                    }
                    // Temporarily suppress Unity internal "Menu ... couldn't be found"
                    // error that occurs due to TOCTOU race: HasPendingPopupMenuItems()
                    // returns true, but menu items are consumed by native code
                    // (e.g. during domain reload) before RemoveMenuItem executes.
                    var prevFilter = Debug.unityLogger.filterLogType;
                    Debug.unityLogger.filterLogType = LogType.Exception;
                    try
                    {
                        s_MenuRemoveMenuItemMethod.Invoke(null, new object[] { kPopupMenuPath });
                    }
                    finally
                    {
                        Debug.unityLogger.filterLogType = prevFilter;
                    }
                    LogVerbose("[NWB-PopupCancel] Removed delayed popup menu to prevent native TrackPopupMenuEx");
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-PopupCancel] Error: {ex.Message}");
            }
#endif
        }

        // ----------------------------------------------------------------
        // Popup extraction & dispatch
        // ----------------------------------------------------------------

        /// <summary>
        /// Try to extract popup menu data from MenuController and send it to
        /// the browser frontend as a JSON message. MenuController stores the
        /// GenericMenu items under "CONTEXT/TEMPORARY-OBJECT-DISPLAY/" when
        /// DisplayCustomContextPopupMenu is called.
        /// Returns true if popup data was successfully extracted and sent.
        /// </summary>
        private static bool TryExtractAndSendPopupToFrontend(object containerWindow, int showMode,
            Rect cwPos, Rect targetScreenPos, float ppp)
        {
            if (s_FrontendPopupSent) return true;

            try
            {
                if (showMode != 1) return false;

                float relX = (cwPos.x - targetScreenPos.x) * ppp;
                float relY = (cwPos.y - targetScreenPos.y) * ppp;
                float popW = cwPos.width * ppp;
                float popH = cwPos.height * ppp;
                s_LastFrontendPopupX = relX;
                s_LastFrontendPopupY = relY;

                // Strategy 1: PopupWindowContent-based popups.
                // If the ContainerWindow hosts popup content, check for known
                // types first (SceneRenderModeWindow, etc.), then fall back to
                // FlexibleMenu extraction. Do NOT fall through to Strategy 2.
                if (ContainerWindowHasPopupContent(containerWindow))
                {
                    // Check for SceneRenderModeWindow (draw mode popup) before FlexibleMenu.
                    string contentTypeName = GetPopupWindowContentTypeName(containerWindow);
                    if (contentTypeName == "SceneRenderModeWindow")
                    {
                        LogVerbose("[NWB-PopupIntercept] Using DrawMode extraction path");
                        return TryExtractDrawModePopup(relX, relY);
                    }
                    if (contentTypeName == "SceneFXWindow")
                    {
                        LogVerbose("[NWB-PopupIntercept] Using SceneFX extraction path");
                        return TryExtractSceneFXPopup(relX, relY);
                    }
                    if (contentTypeName == "SceneViewCameraWindow")
                    {
                        LogVerbose("[NWB-PopupIntercept] Using SceneCamera panel extraction path");
                        return TryExtractSceneViewCameraPanel(relX, relY);
                    }

                    if (contentTypeName == "ConnectionTreeViewWindow")
                    {
                        LogVerbose("[NWB-PopupIntercept] Using ConnectionTreeView extraction path");
                        return TryExtractConnectionTreeViewPopup(containerWindow, relX, relY, popW, popH);
                    }

                    LogVerbose("[NWB-PopupIntercept] Detected PopupWindowContent, using FlexibleMenu path");
                    return TryExtractFlexibleMenuPopup(containerWindow, relX, relY, popW, popH);
                }

                // Strategy 1.5: Detect hosted EditorWindow type and dispatch
                // to specialized extractors. AnnotationWindow (Gizmos) does
                // not use MenuController; its data comes from
                // AnnotationUtility.GetAnnotations(). Other hosted
                // EditorWindows fall through to Strategy 2 where the
                // signature comparison filters stale MenuController data.
                {
                    object rootView = null;
                    var rvProp = containerWindow.GetType().GetProperty("rootView",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    rootView = rvProp?.GetValue(containerWindow);
                    if (rootView != null)
                    {
                        object hostedWindow = null;
                        var wcField2 = rootView.GetType().GetField("m_WindowContent",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        hostedWindow = wcField2?.GetValue(rootView);
                        if (hostedWindow == null)
                        {
                            var avProp = rootView.GetType().GetProperty("actualView",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            hostedWindow = avProp?.GetValue(rootView);
                        }
                        if (hostedWindow is EditorWindow)
                        {
                            System.Type hostedType = hostedWindow.GetType();
                            string hostedName = hostedType.Name;
                            LogVerbose($"[NWB-PopupIntercept] Hosted EditorWindow: {hostedName}");

                            if (hostedName == "AnnotationWindow")
                            {
                                LogVerbose("[NWB-PopupIntercept] Using AnnotationUtility extraction path");
                                return TryExtractAnnotationWindowPopupFull(relX, relY);
                            }

                            if (hostedName == "IconSelector")
                            {
                                LogVerbose("[NWB-PopupIntercept] Using IconSelector extraction path");
                                return TryExtractIconSelectorPopup(
                                    (EditorWindow)hostedWindow, relX, relY, popW, popH);
                            }

                            if (hostedName == "AddComponentWindow")
                            {
                                LogVerbose("[NWB-PopupIntercept] Using AddComponent extraction path");
                                return TryExtractAddComponentPopup(
                                    (EditorWindow)hostedWindow, relX, relY, popW, popH);
                            }

                            if (hostedName == "AdvancedDropdownWindow")
                            {
                                if (TryExtractAdvancedDropdownPopup(
                                        (EditorWindow)hostedWindow, relX, relY, popW, popH))
                                {
                                    LogVerbose("[NWB-PopupIntercept] Using AdvancedDropdown extraction path");
                                    return true;
                                }
                            }

                            // GridSettingsWindow: extract as a panel with interactive controls.
                            if (hostedName == "GridSettingsWindow")
                            {
                                LogVerbose("[NWB-PopupIntercept] Using Grid panel extraction path");
                                return TryExtractGridSettingsPanel(relX, relY);
                            }

                            // SnapIncrementSettingsWindow: extract as a panel with interactive controls.
                            if (hostedName == "SnapIncrementSettingsWindow")
                            {
                                LogVerbose("[NWB-PopupIntercept] Using SnapIncrement panel extraction path");
                                return TryExtractSnapIncrementPanel(relX, relY);
                            }

                            // SnapSettingsWindow (Grid Snapping): extract as panel.
                            if (hostedName == "SnapSettingsWindow")
                            {
                                LogVerbose("[NWB-PopupIntercept] Using SnapSettings (Grid Snapping) panel extraction path");
                                return TryExtractSnapSettingsPanel(relX, relY);
                            }

                            // Other SettingsWindows are not yet supported.
                            if (hostedName.EndsWith("SettingsWindow"))
                            {
                                LogVerbose($"[NWB-PopupIntercept] {hostedName} is a complex UITK window, skipping menu extraction");
                                return false;
                            }
                        }
                    }
                }

                // Strategy 2: GenericMenu popups (e.g. right-click context menus, Gizmos).
                // Read item data from MenuController and compare its signature against
                // the last sent set to avoid re-sending stale gDelayedContextMenu data.
                System.Type menuType = typeof(EditorWindow).Assembly.GetType("UnityEditor.Menu");
                if (menuType == null)
                {
                    CodelyLogger.LogWarning("[NWB-FrontendPopup] Menu type not found");
                    return false;
                }

                MethodInfo getMenuItemsMethod = menuType.GetMethod("GetMenuItems",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(string), typeof(bool), typeof(bool) },
                    null);

                string sig = GetMenuControllerSignature();

                if (getMenuItemsMethod != null)
                {
                    System.Array menuItems = getMenuItemsMethod.Invoke(null,
                        new object[] { kPopupMenuPath, true, false }) as System.Array;

                    if (menuItems == null || menuItems.Length == 0)
                    {
                        LogVerbose("[NWB-FrontendPopup] No MenuController items found");
                        return false;
                    }

                    if (sig == s_LastMenuControllerSignature)
                    {
                        LogVerbose($"[NWB-FrontendPopup] MenuController data unchanged (sig={sig}, last={s_LastMenuControllerSignature}) — stale, skipping. This popup will be handled by TryInterceptDelayedPopup instead.");
                        return false;
                    }
                    LogVerbose($"[NWB-FrontendPopup] MenuController signature changed: last={s_LastMenuControllerSignature} new={sig}, sending via Strategy 2");

                    return SendGenericMenuPopup(menuItems, menuType, relX, relY, popW, popH);
                }

                // Fallback: Unsupported.GetSubmenus (Unity 2019)
                if (sig == "0|" || sig == s_LastMenuControllerSignature)
                {
                    LogVerbose($"[NWB-FrontendPopup] No items or unchanged (sig={sig}) — skipping fallback");
                    return false;
                }
                LogVerbose($"[NWB-FrontendPopup] Using Unsupported.GetSubmenus fallback, sig={sig}");
                return SendDelayedPopupViaSubmenus(relX, relY, 1f);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-FrontendPopup] Error extracting popup data: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        // ----------------------------------------------------------------
        // ContainerWindow popup content detection
        // ----------------------------------------------------------------

        /// <summary>
        /// Return true if the ContainerWindow contains a PopupWindowContent
        /// (e.g. FlexibleMenu, AnnotationWindow, GameViewSizeMenu).
        /// Such windows are created via PopupWindow.Show(), NOT via
        /// GenericMenu/DisplayCustomMenu, so their data must be read from
        /// the window content, NOT from MenuController.
        /// </summary>
        private static bool ContainerWindowHasPopupContent(object containerWindow)
        {
            try
            {
                // Get rootView — try property first, then field (Unity version safety)
                object rootView = null;
                PropertyInfo rootViewProp = containerWindow.GetType().GetProperty("rootView",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                rootView = rootViewProp?.GetValue(containerWindow);

                if (rootView == null)
                {
                    // Some Unity versions expose it as a field
                    FieldInfo rootViewField = containerWindow.GetType().GetField("m_RootView",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    rootView = rootViewField?.GetValue(containerWindow);
                }

                if (rootView == null)
                {
                    CodelyLogger.LogWarning("[NWB-PopupContentCheck] rootView is null on ContainerWindow");
                    return false;
                }

                LogVerbose($"[NWB-PopupContentCheck] rootView type={rootView.GetType().Name}");
                return ViewHierarchyHasPopupWindowContent(rootView);
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-PopupContentCheck] Exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check whether the given type name (or any of its base types) is "PopupWindowContent".
        /// Uses name comparison to avoid cross-assembly IsAssignableFrom failures.
        /// </summary>
        private static bool IsPopupWindowContentType(System.Type type)
        {
            while (type != null && type != typeof(object))
            {
                if (type.Name == "PopupWindowContent") return true;
                type = type.BaseType;
            }
            return false;
        }

        private static bool ViewHierarchyHasPopupWindowContent(object view)
        {
            if (view == null) return false;
            System.Type viewType = view.GetType();

            // Check 1: PopupView type name (Unity internal class for PopupWindow.Show()).
            if (viewType.Name == "PopupView")
            {
                LogVerbose("[NWB-PopupContentCheck] Found PopupView in view hierarchy");
                return true;
            }

            // Check 2: HostView → hosted EditorWindow → m_Content (PopupWindowContent).
            // Unity's HostView exposes the hosted window via several possible members.
            // Try m_WindowContent first, then actualView property (used in some versions).
            object hostedWindow = null;
            FieldInfo wcField = viewType.GetField("m_WindowContent",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (wcField != null)
                hostedWindow = wcField.GetValue(view);

            // Some Unity versions use "actualView" property on HostView
            if (hostedWindow == null)
            {
                PropertyInfo avProp = viewType.GetProperty("actualView",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (avProp != null)
                    hostedWindow = avProp.GetValue(view);
            }

            if (hostedWindow != null)
            {
                System.Type hwType = hostedWindow.GetType();
                LogVerbose($"[NWB-PopupContentCheck] hostedWindow type={hwType.FullName}");

                // 2a: hostedWindow itself is PopupWindowContent
                if (IsPopupWindowContentType(hwType))
                {
                    LogVerbose($"[NWB-PopupContentCheck] Found PopupWindowContent: {hwType.Name}");
                    return true;
                }

                // 2b: hostedWindow type name contains "Popup" (e.g. PopupWindow, PopupWindowWithOverlay)
                // These EditorWindow subclasses host PopupWindowContent in m_Content.
                if (hwType.Name.Contains("Popup"))
                {
                    LogVerbose($"[NWB-PopupContentCheck] hostedWindow is Popup-type: {hwType.Name}");
                    return true;
                }

                // 2c: Explicit m_Content field check for any EditorWindow
                FieldInfo contentField = hwType.GetField("m_Content",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (contentField != null)
                {
                    object content = contentField.GetValue(hostedWindow);
                    if (content != null)
                    {
                        LogVerbose($"[NWB-PopupContentCheck] m_Content type={content.GetType().FullName}");
                        if (IsPopupWindowContentType(content.GetType()))
                        {
                            LogVerbose($"[NWB-PopupContentCheck] Found PopupWindowContent via m_Content: {content.GetType().Name}");
                            return true;
                        }
                    }
                }
            }
            else
            {
                LogVerbose("[NWB-PopupContentCheck] No hostedWindow found on HostView");
            }

            return false;
        }

        // ----------------------------------------------------------------
        // Menu tree building & JSON serialization
        // ----------------------------------------------------------------

        private sealed class PopupMenuTreeNode
        {
            public string Label = "";
            public string Path = "";
            public int Index = -1;
            public bool Separator;
            public bool Enabled = true;
            public bool Checked;
            public string Tooltip = "";
            public readonly List<PopupMenuTreeNode> Children = new List<PopupMenuTreeNode>();
        }

        private static string StripPopupMenuPathPrefix(string itemPath)
        {
            if (string.IsNullOrEmpty(itemPath))
                return "";

            string path = itemPath;

            string popupPrefix = kPopupMenuPath + "/";
            if (path.StartsWith(popupPrefix, StringComparison.Ordinal))
                path = path.Substring(popupPrefix.Length);

            // Strip DisplayPopupMenu prefix (e.g., "Assets/", "GameObject/") when
            // forwarding main-menu-based context menus to the frontend overlay.
            if (!string.IsNullOrEmpty(s_DisplayPopupMenuStripPrefix))
            {
                string dpPrefix = s_DisplayPopupMenuStripPrefix + "/";
                if (path.StartsWith(dpPrefix, StringComparison.Ordinal))
                    path = path.Substring(dpPrefix.Length);
            }

            // Merged component context menus (CONTEXT/GameObject, CONTEXT/Transform, …)
            // can retain parent chains that omit TEMPORARY-OBJECT-DISPLAY from the
            // extracted path. Strip the internal CONTEXT/<type>/ namespace so we do
            // not render a spurious root "CONTEXT" submenu in the HTML overlay.
            if (path.StartsWith("CONTEXT/", StringComparison.Ordinal))
            {
                string rest = path.Substring("CONTEXT/".Length);
                int slash = rest.IndexOf('/');
                path = slash >= 0 ? rest.Substring(slash + 1) : "";
            }

            if (path == "CONTEXT")
                return "";

            return path;
        }

        private static string[] NormalizePopupMenuSegments(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return Array.Empty<string>();

            string[] segments = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0 &&
                string.Equals(segments[0], "CONTEXT", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Length == 1)
                    return Array.Empty<string>();

                var trimmed = new string[segments.Length - 1];
                Array.Copy(segments, 1, trimmed, 0, trimmed.Length);
                return trimmed;
            }

            return segments;
        }

        private static PopupMenuTreeNode FindOrCreateChild(
            List<PopupMenuTreeNode> siblings, string segment)
        {
            foreach (PopupMenuTreeNode existing in siblings)
            {
                if (!existing.Separator && existing.Label == segment)
                    return existing;
            }

            var created = new PopupMenuTreeNode { Label = segment };
            siblings.Add(created);
            return created;
        }

        /// <summary>
        /// Unity GetMenuItems(includeSeparators:true) emits submenu parent entries
        /// (e.g. "Prefab") before/after "Prefab/..." children. If a parent was first
        /// registered as a leaf, clear its leaf metadata once it becomes a branch.
        /// </summary>
        private static void PromoteMenuTreeNodeToBranch(PopupMenuTreeNode node)
        {
            if (node == null || node.Children.Count == 0)
                return;

            node.Path = "";
            node.Index = -1;
            node.Separator = false;
        }

        private static void MergeDuplicateMenuTreeNodes(List<PopupMenuTreeNode> nodes)
        {
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                PopupMenuTreeNode node = nodes[i];
                MergeDuplicateMenuTreeNodes(node.Children);

                if (node.Separator || string.IsNullOrEmpty(node.Label))
                    continue;

                for (int j = 0; j < i; j++)
                {
                    PopupMenuTreeNode other = nodes[j];
                    if (other.Separator || other.Label != node.Label)
                        continue;

                    other.Children.AddRange(node.Children);
                    PromoteMenuTreeNodeToBranch(other);
                    if (string.IsNullOrEmpty(other.Path) && !string.IsNullOrEmpty(node.Path))
                    {
                        other.Path = node.Path;
                        other.Index = node.Index;
                    }
                    nodes.RemoveAt(i);
                    break;
                }
            }
        }

        private static List<PopupMenuTreeNode> BuildHierarchicalPopupMenuTree(
            System.Array menuItems, System.Type menuType,
            PropertyInfo pathProp, PropertyInfo isSepProp,
            PropertyInfo tooltipProp, PropertyInfo contentProp,
            MethodInfo getCheckedMethod, MethodInfo getEnabledMethod,
            bool forceAllEnabled = false)
        {
            var roots = new List<PopupMenuTreeNode>();
            s_CachedPopupItemPaths = new string[menuItems.Length];

            for (int i = 0; i < menuItems.Length; i++)
            {
                object mi = menuItems.GetValue(i);
                string itemPath = pathProp?.GetValue(mi) as string ?? "";
                bool isSep = isSepProp != null && (bool)isSepProp.GetValue(mi);
                string itemTooltip = "";
                try
                {
                    if (tooltipProp != null)
                        itemTooltip = tooltipProp.GetValue(mi) as string ?? "";
                    if (string.IsNullOrEmpty(itemTooltip) && contentProp != null)
                    {
                        object content = contentProp.GetValue(mi);
                        if (content != null)
                        {
                            PropertyInfo gcTooltip = content.GetType().GetProperty("tooltip",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            itemTooltip = gcTooltip?.GetValue(content) as string ?? "";
                        }
                    }
                }
                catch { }

                s_CachedPopupItemPaths[i] = itemPath;
                string relativePath = StripPopupMenuPathPrefix(itemPath).TrimEnd('/');
                if (string.IsNullOrEmpty(relativePath))
                {
                    if (isSep)
                    {
                        roots.Add(new PopupMenuTreeNode { Separator = true });
                    }
                    continue;
                }

                bool isChecked = false;
                bool isEnabled = true;
                if (!isSep && !string.IsNullOrEmpty(itemPath))
                {
                    try { isChecked = (bool)getCheckedMethod?.Invoke(null, new object[] { itemPath }); } catch { }
                    // When forceAllEnabled is set, items were cached before
                    // CancelNativeDelayedPopup removed them from MenuController.
                    // Menu.GetEnabled returns false for removed items, causing
                    // grayed-out text in the frontend popup.
                    if (!forceAllEnabled)
                    {
                        try { isEnabled = (bool)getEnabledMethod?.Invoke(null, new object[] { itemPath }); } catch { }
                    }
                }

                string[] segments = NormalizePopupMenuSegments(relativePath);
                if (segments.Length == 0) continue;

                List<PopupMenuTreeNode> level = roots;
                for (int s = 0; s < segments.Length; s++)
                {
                    bool isLeaf = s == segments.Length - 1;
                    PopupMenuTreeNode node = FindOrCreateChild(level, segments[s]);
                    if (isLeaf)
                    {
                        // Submenu parent rows (e.g. "Prefab") are emitted alongside
                        // "Prefab/..." children. Keep them as branches, not leaves.
                        if (node.Children.Count > 0)
                        {
                            node.Enabled = isEnabled;
                            node.Checked = isChecked;
                            if (!string.IsNullOrEmpty(itemTooltip))
                                node.Tooltip = itemTooltip;
                        }
                        else
                        {
                            node.Path = itemPath;
                            node.Index = i;
                            node.Separator = isSep;
                            node.Enabled = isEnabled;
                            node.Checked = isChecked;
                            node.Tooltip = itemTooltip;
                        }
                    }
                    else
                    {
                        // A submenu parent row may have been registered as a leaf
                        // before its "Parent/Child" entries arrive — reopen as branch.
                        if (!string.IsNullOrEmpty(node.Path))
                        {
                            node.Path = "";
                            node.Index = -1;
                            node.Separator = false;
                        }
                        level = node.Children;
                    }
                }
            }

            MergeDuplicateMenuTreeNodes(roots);
            return roots;
        }

        private static void AppendPopupMenuTreeJson(StringBuilder sb, List<PopupMenuTreeNode> nodes, ref int validCount)
        {
            for (int n = 0; n < nodes.Count; n++)
            {
                PopupMenuTreeNode node = nodes[n];
                if (node.Separator && node.Children.Count == 0 && string.IsNullOrEmpty(node.Path))
                {
                    if (validCount > 0) sb.Append(',');
                    sb.Append("{\"separator\":true}");
                    validCount++;
                    continue;
                }

                if (string.IsNullOrEmpty(node.Label) && node.Children.Count == 0)
                    continue;

                if (string.Equals(node.Label, "CONTEXT", StringComparison.OrdinalIgnoreCase))
                {
                    if (node.Children.Count > 0)
                        AppendPopupMenuTreeJson(sb, node.Children, ref validCount);
                    continue;
                }

                if (node.Children.Count == 0 && string.IsNullOrEmpty(node.Path) && node.Index < 0)
                    continue;

                if (validCount > 0) sb.Append(',');
                sb.Append("{\"index\":");
                sb.Append(node.Index);
                sb.Append(",\"path\":\"");
                sb.Append(EscapeJsonString(node.Path));
                sb.Append("\",\"label\":\"");
                sb.Append(EscapeJsonString(node.Label));
                sb.Append("\",\"tooltip\":\"");
                sb.Append(EscapeJsonString(node.Tooltip));
                sb.Append("\",\"enabled\":");
                sb.Append(node.Enabled ? "true" : "false");
                sb.Append(",\"checked\":");
                sb.Append(node.Checked ? "true" : "false");
                sb.Append(",\"separator\":");
                sb.Append(node.Separator ? "true" : "false");

                if (node.Children.Count > 0)
                {
                    sb.Append(",\"submenu\":true,\"children\":[");
                    int childCount = 0;
                    AppendPopupMenuTreeJson(sb, node.Children, ref childCount);
                    sb.Append(']');
                }

                sb.Append('}');
                validCount++;
            }
        }

        // ----------------------------------------------------------------
        // GenericMenu popup sending
        // ----------------------------------------------------------------

        /// <summary>
        /// Build and send popup_show JSON for GenericMenu items from MenuController.
        /// </summary>
        private static bool SendGenericMenuPopup(System.Array menuItems, System.Type menuType,
            float relX, float relY, float popW, float popH,
            bool hierarchicalContextMenu = false,
            bool forceAllEnabled = false)
        {
            System.Type smiType = menuItems.GetValue(0).GetType();
            PropertyInfo pathProp = smiType.GetProperty("path",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            PropertyInfo isSepProp = smiType.GetProperty("isSeparator",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            PropertyInfo tooltipProp = smiType.GetProperty("tooltip",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            PropertyInfo contentProp = smiType.GetProperty("content",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            MethodInfo getCheckedMethod = menuType.GetMethod("GetChecked",
                BindingFlags.Static | BindingFlags.Public,
                null, new System.Type[] { typeof(string) }, null);
            MethodInfo getEnabledMethod = menuType.GetMethod("GetEnabled",
                BindingFlags.Static | BindingFlags.Public,
                null, new System.Type[] { typeof(string) }, null);

            s_FrontendPopupIdCounter++;
            s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;
            s_PopupSourceType = hierarchicalContextMenu ? "generic_context" : "generic";

            var sb = new StringBuilder(4096);
            sb.Append("{\"type\":\"popup_show\",\"id\":\"");
            sb.Append(s_CurrentFrontendPopupId);
            sb.Append("\",\"sourceType\":\"");
            sb.Append(s_PopupSourceType);
            sb.Append("\",\"x\":");
            sb.Append(Mathf.RoundToInt(relX));
            sb.Append(",\"y\":");
            sb.Append(Mathf.RoundToInt(relY));
            sb.Append(",\"width\":");
            sb.Append(Mathf.RoundToInt(popW));
            sb.Append(",\"height\":");
            sb.Append(Mathf.RoundToInt(popH));
            sb.Append(",\"items\":[");

            int validCount = 0;
            if (hierarchicalContextMenu)
            {
                List<PopupMenuTreeNode> roots = BuildHierarchicalPopupMenuTree(
                    menuItems, menuType, pathProp, isSepProp, tooltipProp, contentProp,
                    getCheckedMethod, getEnabledMethod, forceAllEnabled);
                AppendPopupMenuTreeJson(sb, roots, ref validCount);
            }
            else
            {
                s_CachedPopupItemPaths = new string[menuItems.Length];
                for (int i = 0; i < menuItems.Length; i++)
                {
                    object mi = menuItems.GetValue(i);
                    string itemPath = pathProp?.GetValue(mi) as string ?? "";
                    bool isSep = isSepProp != null && (bool)isSepProp.GetValue(mi);
                    string itemTooltip = "";
                    try
                    {
                        if (tooltipProp != null)
                            itemTooltip = tooltipProp.GetValue(mi) as string ?? "";
                        if (string.IsNullOrEmpty(itemTooltip) && contentProp != null)
                        {
                            object content = contentProp.GetValue(mi);
                            if (content != null)
                            {
                                PropertyInfo gcTooltip = content.GetType().GetProperty("tooltip",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                itemTooltip = gcTooltip?.GetValue(content) as string ?? "";
                            }
                        }
                    }
                    catch { }

                    s_CachedPopupItemPaths[i] = itemPath;

                    string label = StripPopupMenuPathPrefix(itemPath);

                    bool isChecked = false;
                    bool isEnabled = true;
                    if (!isSep && !string.IsNullOrEmpty(itemPath))
                    {
                        try { isChecked = (bool)getCheckedMethod?.Invoke(null, new object[] { itemPath }); } catch { }
                        // When forceAllEnabled is set, items were cached before
                        // CancelNativeDelayedPopup removed them from MenuController.
                        // Menu.GetEnabled would return false for removed items, causing
                        // grayed-out text in the frontend popup.
                        if (!forceAllEnabled)
                        {
                            try { isEnabled = (bool)getEnabledMethod?.Invoke(null, new object[] { itemPath }); } catch { }
                        }
                    }

                    if (validCount > 0) sb.Append(',');
                    sb.Append("{\"index\":");
                    sb.Append(i);
                    sb.Append(",\"path\":\"");
                    sb.Append(EscapeJsonString(itemPath));
                    sb.Append("\",\"label\":\"");
                    sb.Append(EscapeJsonString(label));
                    sb.Append("\",\"tooltip\":\"");
                    sb.Append(EscapeJsonString(itemTooltip));
                    sb.Append("\",\"enabled\":");
                    sb.Append(isEnabled ? "true" : "false");
                    sb.Append(",\"checked\":");
                    sb.Append(isChecked ? "true" : "false");
                    sb.Append(",\"separator\":");
                    sb.Append(isSep ? "true" : "false");
                    sb.Append('}');
                    validCount++;
                }
            }

            sb.Append("]}");

            string json = sb.ToString();
            long dcSendTick = System.Diagnostics.Stopwatch.GetTimestamp();
            bool sent = SendDataChannelMessage(json);
            double dcSendMs = (System.Diagnostics.Stopwatch.GetTimestamp() - dcSendTick)
                / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
            LogVerbose($"[NWB-CTX-DIAG] popup_show DC send: sent={sent} payload={json.Length}B took={dcSendMs:F1}ms");
            if (!sent)
            {
                float hbSecs = NativeWindowBridgeAPI.NWB_GetSecondsSinceLastHeartbeat();
                CodelyLogger.LogWarning($"[NWB-FrontendPopup] DataChannel send failed " +
                    $"(payload={json.Length}B, heartbeatAge={hbSecs:F2}s, " +
                    $"offscreenActive={NativeWindowBridgeAPI.NWB_IsOffscreenActive()})");
                // DataChannel may be recovering after TrackPopupMenuEx modal
                // loop was broken by WM_CANCELMODE. Use progressive backoff
                // with multiple retries to ride out the transient stall.
                int[] delaysMs = { 10, 20, 40, 80, 150 };
                for (int r = 0; r < delaysMs.Length && !sent; r++)
                {
                    System.Threading.Thread.Sleep(delaysMs[r]);
                    sent = SendDataChannelMessage(json);
                }
                if (sent)
                    LogVerbose("[NWB-FrontendPopup] DataChannel send succeeded after retry");
            }
            s_FrontendPopupSent = sent;
            if (sent)
            {
                s_SuppressTargetRepaintWhilePopupOpen = true;

                if (hierarchicalContextMenu)
                {
                    // Close any lingering #32768 or ContainerWindow popups.
                    DismissAllNativePopups();
                    s_PendingNativePopupCloseFrames = Math.Max(s_PendingNativePopupCloseFrames, 60);
                }

                string firstCachedPath = s_CachedPopupItemPaths != null && s_CachedPopupItemPaths.Length > 0
                    ? s_CachedPopupItemPaths[0] : "";
                s_LastMenuControllerSignature = $"{menuItems.Length}|{firstCachedPath}";
                LogVerbose($"[NWB-FrontendPopup] Sent GenericMenu popup: id={s_CurrentFrontendPopupId} items={validCount} hierarchical={hierarchicalContextMenu} pos=({relX:F0},{relY:F0}) sig={s_LastMenuControllerSignature}");
            }
            else
                CodelyLogger.LogWarning("[NWB-FrontendPopup] Failed to send GenericMenu popup via DataChannel");

            return sent;
        }

        // ----------------------------------------------------------------
        // Repaint suppression & context-menu state
        // ----------------------------------------------------------------

        private static bool ShouldSuppressTargetRepaint()
        {
            return s_SuppressTargetRepaintWhilePopupOpen;
        }

        /// <summary>
        /// Block Repaint passes that would call ShowDelayedContextMenu while a
        /// remote context-menu gesture is in flight or a frontend overlay is open.
        /// </summary>
        private static bool ShouldBlockNativeContextMenuRepaint()
        {
            if (ShouldSuppressTargetRepaint()) return true;
            if (s_FrontendPopupSent) return true;
            if (s_PaneOptionsMouseDownPending) return true;
            if (s_ExpectContextMenuForward) return true;
            if (s_PendingNativePopupCloseFrames > 0) return true;

            double elapsed = EditorApplication.timeSinceStartup - s_LastRemoteMousedownTime;
            if (elapsed <= kRemotePopupGracePeriod && HasPendingPopupMenuItems())
                return true;

            return false;
        }

        private static void ClearContextMenuForwardState()
        {
            s_ExpectContextMenuForward = false;
        }

        // ----------------------------------------------------------------
        // DisplayPopupMenu forwarding (ProjectBrowser, SceneHierarchyWindow)
        // ----------------------------------------------------------------

        /// <summary>
        /// Map the current offscreen target window type to the main menu prefix
        /// used by EditorUtility.DisplayPopupMenu for its context menu.
        /// Returns null for window types that use GenericMenu.DropDown instead.
        /// </summary>
        private static string GetDisplayPopupMenuPrefix()
        {
            if (s_OffscreenTargetType == null) return null;
            string name = s_OffscreenTargetType.Name;
            switch (name)
            {
                case "ProjectBrowser": return "Assets";
                case "SceneHierarchyWindow": return "GameObject";
                default: return null;
            }
        }

        /// <summary>
        /// Read main menu items under the given prefix and forward them as a
        /// hierarchical context menu to the frontend overlay. Used for windows
        /// that call EditorUtility.DisplayPopupMenu (e.g., ProjectBrowser →
        /// "Assets/", SceneHierarchyWindow → "GameObject/") which stores items
        /// in the main menu bar rather than MenuController's temporary path.
        /// </summary>
        private static bool TryForwardDisplayPopupMenu(float clickX, float clickY, string menuPrefix)
        {
            if (s_FrontendPopupSent) return false;
            if (string.IsNullOrEmpty(menuPrefix)) return false;

            try
            {
                System.Type menuType = typeof(EditorWindow).Assembly.GetType("UnityEditor.Menu");
                if (menuType == null)
                {
                    LogVerbose("[NWB-DisplayPopup] Menu type not found");
                    return false;
                }

                MethodInfo getMenuItemsMethod = menuType.GetMethod("GetMenuItems",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(string), typeof(bool), typeof(bool) },
                    null);
                if (getMenuItemsMethod == null)
                {
                    LogVerbose("[NWB-DisplayPopup] GetMenuItems method not found");
                    return false;
                }

                System.Array menuItems = getMenuItemsMethod.Invoke(null,
                    new object[] { menuPrefix, true, false }) as System.Array;
                if (menuItems == null || menuItems.Length == 0)
                {
                    LogVerbose($"[NWB-DisplayPopup] No items under '{menuPrefix}'");
                    return false;
                }

                ComputePopupPositionFromLocalClick(
                    clickX, clickY, s_TabBarOffsetY + 4f, 4f,
                    out float popX, out float popY);

                // Set the strip prefix so BuildHierarchicalPopupMenuTree renders
                // relative labels (e.g., "Create" instead of "Assets/Create").
                s_DisplayPopupMenuStripPrefix = menuPrefix;
                try
                {
                    bool sent = SendGenericMenuPopup(
                        menuItems, menuType, popX, popY, 0, 0,
                        hierarchicalContextMenu: true);
                    if (sent)
                    {
                        s_PopupSourceType = "display_popup_menu";
                        s_PendingNativePopupCloseFrames = Math.Max(
                            s_PendingNativePopupCloseFrames, 60);
                        // gDelayedContextMenu was already consumed by
                        // ProbeAndDismissDelayedContextMenu before this
                        // method is called. Clean up any residual popups.
                        DismissAllNativePopups();
                        LogVerbose($"[NWB-DisplayPopup] Forwarded '{menuPrefix}' menu: " +
                            $"id={s_CurrentFrontendPopupId} items={menuItems.Length}");
                    }
                    else
                    {
                        CodelyLogger.LogWarning($"[NWB-DisplayPopup] Failed to send '{menuPrefix}' popup_show");
                    }
                    return sent;
                }
                finally
                {
                    s_DisplayPopupMenuStripPrefix = null;
                }
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-DisplayPopup] Error forwarding '{menuPrefix}': {ex.Message}");
                s_DisplayPopupMenuStripPrefix = null;
                return false;
            }
        }

        // ----------------------------------------------------------------
        // Context-click synthesis & forwarding
        // ----------------------------------------------------------------

        private static bool TrySynthesizeAndForwardContextMenuPopup(
            float clickX, float clickY, EventModifiers mods, Vector2 mousePos, Vector2 mouseDelta)
        {
            // If the popup was already forwarded during mousedown (when
            // GenericMenu.ShowAsContext fires inside SendEvent(MouseDown)),
            // skip ContextClick synthesis entirely.
            if (s_FrontendPopupSent) return true;

            // For GameView/SceneView, only synthesize ContextClick when the click
            // is in the DockArea tab header region. The tab header context menu
            // (Close Tab, Maximize, etc.) requires ContextClick to appear, but
            // we must not interfere with game input or scene navigation below it.
            if (!ShouldSynthesizeContextClickForTarget())
            {
                bool isInTabHeader = s_TabBarOffsetY > 0 && mousePos.y <= s_TabBarOffsetY;
                if (!isInTabHeader)
                    return false;
            }

            // Tab-header right-click: show DockArea tab menu (Close Tab, Lock,
            // Maximize, etc.) instead of the content context menu. Must be
            // checked BEFORE GetDisplayPopupMenuPrefix to avoid the Assets/
            // menu stealing the click in the tab strip region.
            if (s_TabBarOffsetY > 0 && mousePos.y <= s_TabBarOffsetY)
            {
                bool tabMenuSent = TrySendTabHeaderContextMenu(clickX, clickY);
                if (tabMenuSent)
                {
                    ClearContextMenuForwardState();
                    return true;
                }
            }

            // Windows that use EditorUtility.DisplayPopupMenu (ProjectBrowser,
            // SceneHierarchyWindow) queue a C++ gDelayedContextMenu with a main
            // menu prefix (e.g., "Assets/"). Sending ContextClick would trigger
            // DisplayPopupMenu → ShowDelayedContextMenu → TrackPopupMenuEx on the
            // next SendEvent, blocking the main thread and showing a native popup
            // on the hidden editor window. Instead, read items directly from the
            // main menu bar and forward them without ever queuing the delayed popup.
            string displayPrefix = GetDisplayPopupMenuPrefix();
            if (!string.IsNullOrEmpty(displayPrefix))
            {
                s_SuppressTargetRepaintWhilePopupOpen = true;
                bool forwarded = TryForwardDisplayPopupMenu(clickX, clickY, displayPrefix);
                if (forwarded)
                {
                    ClearContextMenuForwardState();
                    return true;
                }
                s_SuppressTargetRepaintWhilePopupOpen = false;
                LogVerbose($"[NWB-DisplayPopup] Direct forwarding failed for prefix='{displayPrefix}', falling through to ContextClick synthesis");
            }

            s_SuppressTargetRepaintWhilePopupOpen = true;
            long synthStart = System.Diagnostics.Stopwatch.GetTimestamp();
            double synthFreq = System.Diagnostics.Stopwatch.Frequency;
            ScheduleNativePopupAutoDismiss();
            try
            {
                Event warmMove = new Event
                {
                    type = EventType.MouseMove,
                    mousePosition = mousePos,
                    delta = mouseDelta,
                    modifiers = mods
                };
                s_OffscreenTarget.SendEvent(warmMove);

                long preLayoutTick = System.Diagnostics.Stopwatch.GetTimestamp();
                s_OffscreenTarget.SendEvent(new Event { type = EventType.Layout });
                double layoutMs = (System.Diagnostics.Stopwatch.GetTimestamp() - preLayoutTick) / synthFreq * 1000.0;
                LogVerbose($"[NWB-CTX-DIAG] Layout sent in {layoutMs:F1}ms, s_FrontendPopupSent={s_FrontendPopupSent}");

                DismissAllNativePopups();

                long preCtxTick = System.Diagnostics.Stopwatch.GetTimestamp();
                Event contextEvt = new Event
                {
                    type = EventType.ContextClick,
                    mousePosition = mousePos,
                    button = 1,
                    modifiers = mods
                };
                s_OffscreenTarget.SendEvent(contextEvt);
                double ctxMs = (System.Diagnostics.Stopwatch.GetTimestamp() - preCtxTick) / synthFreq * 1000.0;
                string postCtxSig = GetMenuControllerSignature();
                LogVerbose($"[NWB-CTX-DIAG] ContextClick at ({mousePos.x:F0},{mousePos.y:F0}) for {s_OffscreenTargetType?.Name}, took {ctxMs:F1}ms, postMCSig={postCtxSig}");
                if (ctxMs > 200)
                    CodelyLogger.LogWarning($"[NWB-ContextClick] SendEvent(ContextClick) BLOCKED for {ctxMs:F0}ms");
            }
            catch (Exception ctxEx)
            {
                CodelyLogger.LogWarning($"[NWB-ContextClick] Failed: {ctxEx.Message}");
                s_SuppressTargetRepaintWhilePopupOpen = false;
                ConsumeDelayedContextMenu();
                CancelNativeDelayedPopup();
                return false;
            }
            finally
            {
                CancelNativePopupAutoDismiss();
                // Transition to a self-terminating guard that keeps
                // dismissing #32768 popups during Phase2/Phase3 (DC send).
                // The aggressive ScheduleNativePopupAutoDismiss (which
                // targets ALL HWNDs) is no longer needed, but a targeted
                // #32768-only guard must stay active to cover any late
                // TrackPopupMenuEx triggered by implicit events.
                ScheduleTimeLimitedPopupDismiss(10000);
            }
            TryDismissWin32TrackPopupMenu();

            double postTryMs = (System.Diagnostics.Stopwatch.GetTimestamp() - synthStart) / synthFreq * 1000.0;
            LogVerbose($"[NWB-CTX-DIAG] PostTry: elapsed={postTryMs:F1}ms, s_FrontendPopupSent={s_FrontendPopupSent}");

            // ── Phase 2: cache items from MenuController ──
            // gDelayedContextMenu is already CLEARED by ContextClick processing:
            //   OnInputEvent(ContextClick) → HasDelayedContextMenu() TRUE
            //   → ShowDelayedContextMenu() → TrackPopupMenuEx (dismissed by AutoDismiss)
            //   → gDelayedContextMenu = NULL.
            // Items MUST remain in MenuController so HandleFrontendPopupSelect
            // can execute them via EditorApplication.ExecuteMenuItem later.
            System.Array cachedMenuItems = null;
            System.Type cachedMenuType = null;
            try
            {
                cachedMenuType = typeof(EditorWindow).Assembly.GetType("UnityEditor.Menu");
                if (cachedMenuType != null)
                {
                    MethodInfo getItemsMethod = cachedMenuType.GetMethod("GetMenuItems",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        null,
                        new System.Type[] { typeof(string), typeof(bool), typeof(bool) },
                        null);
                    if (getItemsMethod != null)
                    {
                        cachedMenuItems = getItemsMethod.Invoke(null,
                            new object[] { kPopupMenuPath, true, false }) as System.Array;
                    }
                }
            }
            catch (Exception cacheEx)
            {
                CodelyLogger.LogWarning($"[NWB-CTX-DIAG] Phase2 cache FAILED: {cacheEx.Message}");
            }

            int cachedCount = cachedMenuItems?.Length ?? 0;
            bool itemsPresent = HasPendingPopupMenuItems();
            LogVerbose($"[NWB-CTX-DIAG] Phase2: cached={cachedCount} items, itemsPresent={itemsPresent}, s_FrontendPopupSent={s_FrontendPopupSent}");

            // Close any ContainerWindow (showMode=2) created by the implicit
            // SendLayoutEvent inside OnInputEvent which triggers
            // GenericMenu.ShowAsContext via m_RightClickedDelayFrame.
            DismissAllNativePopups();

            // ── Phase 3: send cached items to frontend via DataChannel ──
            float prePhase3HbAge = NativeWindowBridgeAPI.NWB_GetSecondsSinceLastHeartbeat();
            int prePhase3OffActive = NativeWindowBridgeAPI.NWB_IsOffscreenActive();
            LogVerbose($"[NWB-CTX-DIAG] Phase3: cachedCount={cachedCount}, " +
                $"s_FrontendPopupSent={s_FrontendPopupSent}, " +
                $"heartbeatAge={prePhase3HbAge:F2}s, offActive={prePhase3OffActive}");
            if (cachedCount > 0 && !s_FrontendPopupSent)
            {
                ComputePopupPositionFromLocalClick(
                    clickX, clickY, s_TabBarOffsetY + 4f, 4f,
                    out float popX, out float popY);

                for (int attempt = 0; attempt < 3 && !s_FrontendPopupSent; attempt++)
                {
                    if (attempt > 0)
                    {
                        System.Threading.Thread.Sleep(30 + attempt * 30);
                        LogVerbose($"[NWB-ContextClick] DataChannel retry #{attempt + 1}");
                    }

                    bool sent = SendGenericMenuPopup(
                        cachedMenuItems, cachedMenuType, popX, popY, 0, 0,
                        hierarchicalContextMenu: true,
                        forceAllEnabled: true);
                    if (sent)
                    {
                        s_PendingNativePopupCloseFrames = Math.Max(s_PendingNativePopupCloseFrames, 60);
                        DismissAllNativePopups();
                        ClearContextMenuForwardState();
                        break;
                    }
                }
            }

            double totalSynthMs = (System.Diagnostics.Stopwatch.GetTimestamp() - synthStart) / synthFreq * 1000.0;
            if (s_FrontendPopupSent)
            {
                // Drain gDelayedContextMenu: ConsoleWindow's
                // m_RightClickedDelayFrame may still be >0. Each Layout
                // decrements it; when it reaches 0 GenericMenu.ShowAsContext
                // fires and sets gDelayedContextMenu. Without draining,
                // the next capture-loop Repaint would trigger
                // ShowDelayedContextMenu → TrackPopupMenuEx → visible
                // native popup flash. Send a few protected Layout events
                // to exhaust the countdown and break any resulting
                // TrackPopupMenuEx immediately. Menu items are NOT removed
                // — they remain for ExecuteMenuItem on popup_select.
                for (int drain = 0; drain < 4; drain++)
                {
                    ScheduleNativePopupAutoDismiss();
                    try { s_OffscreenTarget.SendEvent(new Event { type = EventType.Layout }); }
                    catch (Exception) { }
                    finally { CancelNativePopupAutoDismiss(); }
                    DismissAllNativePopups();
                }
                bool finalItems = HasPendingPopupMenuItems();
                CodelyLogger.Log($"[NWB-CTX-DIAG] SUCCESS in {totalSynthMs:F1}ms, itemsKept={finalItems}");
                ScheduleTimeLimitedPopupDismiss(3000);
                return true;
            }

            // Failed path: always clean up gDelayedContextMenu to prevent
            // native popup appearing even when our DataChannel send failed.
            LogVerbose("[NWB-CTX-DIAG] FAILED path: cleanup gDelayedContextMenu");
            ConsumeDelayedContextMenu();
            s_SuppressTargetRepaintWhilePopupOpen = false;

            // Force-close ContainerWindows: the normal grace period
            // (kRemotePopupGracePeriod=0.5s) has expired during the DC
            // retry loop (~1.2s). Without forceClose, ScanAndCloseNativePopups
            // won't close showMode==2 containers created by GenericMenu.
            s_PendingNativePopupCloseFrames = Math.Max(s_PendingNativePopupCloseFrames, 120);
            DismissAllNativePopups();

            // Start a long-running background guard thread that keeps
            // dismissing #32768 popups independent of the main thread.
            // This covers any TrackPopupMenuEx that fires during the
            // subsequent mouseup event processing or later Layout/Repaint
            // cycles after this function returns.
            ScheduleTimeLimitedPopupDismiss(15000);

            float failHbAge = NativeWindowBridgeAPI.NWB_GetSecondsSinceLastHeartbeat();
            CodelyLogger.LogWarning($"[NWB-ContextClick] FAILED — DataChannel send failed after {totalSynthMs:F1}ms " +
                $"(cachedItems={cachedCount}, heartbeatAge={failHbAge:F2}s, " +
                $"offscreenActive={NativeWindowBridgeAPI.NWB_IsOffscreenActive()})");
            return false;
        }

        // ----------------------------------------------------------------
        // Menu item execution
        // ----------------------------------------------------------------

        private static bool TryExecuteForwardedMenuItem(string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath)) return false;

            bool executed = EditorApplication.ExecuteMenuItem(menuPath);
            if (executed)
            {
                LogVerbose($"[NWB-FrontendPopup] ExecuteMenuItem succeeded: {menuPath}");
                return true;
            }

            UnityEngine.Object[] ctx = Selection.objects;
            if (ctx != null && ctx.Length > 0)
            {
                MethodInfo withCtx = typeof(EditorApplication).GetMethod(
                    "ExecuteMenuItemWithTemporaryContext",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (withCtx != null)
                {
                    try
                    {
                        executed = (bool)withCtx.Invoke(null, new object[] { menuPath, ctx });
                        if (executed)
                        {
                            LogVerbose($"[NWB-FrontendPopup] ExecuteMenuItemWithTemporaryContext succeeded: {menuPath}");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        CodelyLogger.LogWarning($"[NWB-FrontendPopup] ExecuteMenuItemWithTemporaryContext error: {ex.Message}");
                    }
                }
            }

            CodelyLogger.LogWarning($"[NWB-FrontendPopup] ExecuteMenuItem returned false for: {menuPath}");
            return false;
        }

        
        /// <summary>
        /// Extract popup items from UnityEditor.PopupList and send as popup_show.
        /// PopupList is used by ProjectBrowser search filter dropdowns.
        /// </summary>
        private static bool TryExtractPopupListFromWindowContent(object windowContent, float relX, float relY, float popW, float popH)
        {
            try
            {
                if (windowContent == null) return false;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                FieldInfo dataField = windowContent.GetType().GetField("m_Data", flags);
                object data = dataField?.GetValue(windowContent);
                if (data == null)
                {
                    LogVerbose("[NWB-PopupList] m_Data is null; cannot extract");
                    return false;
                }

                FieldInfo listField = data.GetType().GetField("m_ListElements", flags);
                System.Collections.IList elements = listField?.GetValue(data) as System.Collections.IList;
                if (elements == null || elements.Count == 0)
                {
                    LogVerbose("[NWB-PopupList] m_ListElements is empty");
                    return false;
                }

                FieldInfo cbField = data.GetType().GetField("m_OnSelectCallback", flags);
                s_CachedPopupListSelectDelegate = cbField?.GetValue(data) as System.Delegate;

                s_FrontendPopupIdCounter++;
                s_CurrentFrontendPopupId = "popup_" + s_FrontendPopupIdCounter;
                s_PopupSourceType = "popup_list";
                s_CachedPopupItemPaths = new string[elements.Count];
                s_CachedPopupListItems = new object[elements.Count];

                var sb = new StringBuilder(2048);
                sb.Append("{\"type\":\"popup_show\",\"id\":\"");
                sb.Append(s_CurrentFrontendPopupId);
                sb.Append("\",\"x\":");
                sb.Append(Mathf.RoundToInt(relX));
                sb.Append(",\"y\":");
                sb.Append(Mathf.RoundToInt(relY));
                sb.Append(",\"width\":");
                sb.Append(Mathf.RoundToInt(popW));
                sb.Append(",\"height\":");
                sb.Append(Mathf.RoundToInt(popH));
                sb.Append(",\"items\":[");

                int validCount = 0;
                for (int i = 0; i < elements.Count; i++)
                {
                    object element = elements[i];
                    if (element == null) continue;
                    string label = GetPopupListElementText(element);
                    if (string.IsNullOrEmpty(label)) continue;

                    bool selected = GetPopupListElementBool(element, "selected", "m_Selected");
                    bool enabled = !HasPopupListElementBool(element, "enabled", "m_Enabled") ||
                                   GetPopupListElementBool(element, "enabled", "m_Enabled");

                    s_CachedPopupListItems[i] = element;
                    s_CachedPopupItemPaths[i] = label;

                    if (validCount > 0) sb.Append(',');
                    sb.Append("{\"index\":");
                    sb.Append(i);
                    sb.Append(",\"path\":\"");
                    sb.Append(EscapeJsonString(label));
                    sb.Append("\",\"label\":\"");
                    sb.Append(EscapeJsonString(label));
                    sb.Append("\",\"tooltip\":\"\",\"enabled\":");
                    sb.Append(enabled ? "true" : "false");
                    sb.Append(",\"checked\":");
                    sb.Append(selected ? "true" : "false");
                    sb.Append(",\"separator\":false}");
                    validCount++;
                }
                sb.Append("]}");

                if (validCount == 0)
                {
                    LogVerbose("[NWB-PopupList] No valid entries after extraction");
                    return false;
                }

                bool sent = SendDataChannelMessage(sb.ToString());
                s_FrontendPopupSent = sent;
                if (sent)
                {
                    s_SuppressTargetRepaintWhilePopupOpen = true;
                    CancelNativeDelayedPopup();
                    LogVerbose($"[NWB-PopupList] Sent popup: id={s_CurrentFrontendPopupId} items={validCount}");
                }
                else
                {
                    CodelyLogger.LogWarning("[NWB-PopupList] Failed to send popup_show");
                }
                return sent;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-PopupList] Extraction error: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static string GetPopupListElementText(object element)
        {
            if (element == null) return "";
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            PropertyInfo textProp = element.GetType().GetProperty("text", flags);
            if (textProp != null && textProp.PropertyType == typeof(string))
            {
                try { return textProp.GetValue(element, null) as string ?? ""; } catch { }
            }

            FieldInfo contentField = element.GetType().GetField("m_Content", flags);
            GUIContent content = contentField?.GetValue(element) as GUIContent;
            return content?.text ?? "";
        }

        private static bool HasPopupListElementBool(object element, string propertyName, string fieldName)
        {
            if (element == null) return false;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return element.GetType().GetProperty(propertyName, flags) != null ||
                   element.GetType().GetField(fieldName, flags) != null;
        }

        private static bool GetPopupListElementBool(object element, string propertyName, string fieldName)
        {
            if (element == null) return false;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo prop = element.GetType().GetProperty(propertyName, flags);
            if (prop != null && prop.PropertyType == typeof(bool))
            {
                try { return (bool)prop.GetValue(element, null); } catch { }
            }
            FieldInfo field = element.GetType().GetField(fieldName, flags);
            if (field != null && field.FieldType == typeof(bool))
            {
                try { return (bool)field.GetValue(element); } catch { }
            }
            return false;
        }

        private static void HandlePopupListSelect(int index, string path)
        {
            if (s_CachedPopupListSelectDelegate == null)
            {
                CodelyLogger.LogWarning($"[NWB-PopupList] Missing select callback for index={index}");
                return;
            }

            object item = (s_CachedPopupListItems != null && index >= 0 && index < s_CachedPopupListItems.Length)
                ? s_CachedPopupListItems[index]
                : null;

            if (item == null && !string.IsNullOrEmpty(path) && s_CachedPopupItemPaths != null && s_CachedPopupListItems != null)
            {
                for (int i = 0; i < s_CachedPopupItemPaths.Length && i < s_CachedPopupListItems.Length; i++)
                {
                    if (string.Equals(s_CachedPopupItemPaths[i], path, StringComparison.OrdinalIgnoreCase))
                    {
                        item = s_CachedPopupListItems[i];
                        break;
                    }
                }
            }

            if (item == null)
            {
                CodelyLogger.LogWarning($"[NWB-PopupList] Missing list element for index={index}, path={path}");
                return;
            }

            try
            {
                s_CachedPopupListSelectDelegate.DynamicInvoke(item);
                LogVerbose($"[NWB-PopupList] Invoked m_OnSelectCallback for index={index}");
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NWB-PopupList] Select callback invoke failed: {ex.Message}");
            }
        }
    }
}
