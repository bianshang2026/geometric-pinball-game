#if UNITY_EDITOR_WIN
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;

using UnityTcp.Editor.Helpers;
namespace Cn.Tuanjie.Codely.Editor
{
    /// <summary>
    /// Locate the Win32 HWND that backs a Unity EditorWindow.
    ///
    /// EditorWindow.position is in DPI-scaled "points" while Win32 reports
    /// physical pixels, and Unity's point↔pixel mapping for monitors other
    /// than the primary is not a single linear factor. A purely absolute
    /// rect comparison therefore only works reliably on the primary monitor —
    /// on secondary monitors it used to never match and the embedded panel
    /// hung on "Loading" forever. The lookup below layers several strategies,
    /// most reliable first:
    ///
    /// 1. Reflection: the host GUIView's internal nativeHandle. If it is our
    ///    process's UnityGUIViewWndClass window, done — coordinate-free.
    /// 2. If reflection yields a valid window of ours with a different class
    ///    (e.g. the ContainerWindow), restrict the search to that top-level
    ///    window; a sole GUIView child there is accepted outright (typical
    ///    for floating panels).
    /// 3. Rect search over UnityGUIViewWndClass children, testing containment
    ///    under several coordinate hypotheses per candidate:
    ///      (a) legacy: window rect × EditorGUIUtility.pixelsPerPoint versus
    ///          physical pixels — exact on the primary monitor.
    ///      (b) pure scale: candidate physical rect ÷ its own GetDpiForWindow
    ///          factor versus the window rect in points.
    ///      (d) monitor-anchored: like (b) but preserving the monitor's
    ///          physical origin (points = monOrigin + (phys − monOrigin)/scale).
    ///      (c) container-relative: compare the candidate's offset within its
    ///          top-level window against the view's offset within its
    ///          ContainerWindow (both in points). Relative offsets never cross
    ///          monitors, so this holds under ANY per-monitor mapping.
    ///    Smallest matching candidate wins (the dock host's GUIView fully
    ///    contains its hosted EditorWindow's content rect; slack absorbs the
    ///    tab strip / scrollbar offset).
    /// 4. When everything fails, a throttled diagnostic dump of every
    ///    candidate is logged so field reports pin down the actual mapping.
    ///
    /// IMPORTANT: the EnumWindowsProc delegates are stored in static fields so
    /// they're allocated once. Repeatedly creating fresh delegate instances on
    /// every editor tick crashes Mono's delegate_hash_table when the marshal
    /// cache fills up (mono-2.0-bdwgc:marshal.c delegate_hash_table_add assert).
    /// </summary>
    public static class NativeWindowHelper
    {
        // ── Win32 ──────────────────────────────────────────────────────────

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);

        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        static extern int GetClassNameW(IntPtr hwnd, [Out] char[] buf, int max);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);

        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO mi);

        // Windows 10 1607+; call sites guard with s_dpiApiAvailable.
        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        const uint GA_ROOT = 2;
        const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width  => Right  - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

        const string kGUIViewClass = "UnityGUIViewWndClass";

        // tab strip / scrollbar / window chrome can make the host GUIView several
        // dozen pixels larger than the EditorWindow's inner content rect — give
        // enough slack on each side to absorb that. Used as physical pixels in
        // hypothesis (a) and as points elsewhere; both stay well below the size
        // of a real dock area.
        const int Slack = 64;

        static readonly int s_currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;

        // Static delegate instances. Created once at type init, kept alive forever,
        // so EnumWindows / EnumChildWindows always see the same managed callback.
        static readonly EnumWindowsProc s_outerProc = OuterEnumProc;
        static readonly EnumWindowsProc s_innerProc = InnerEnumProc;

        // Per-call state. Safe to use static state because GetHWND is only invoked
        // from the editor main thread and EnumWindows is synchronous.
        static readonly char[] s_classBuf = new char[256];
        static int s_targetLeft, s_targetTop, s_targetRight, s_targetBottom;            // physical px, hypothesis (a)
        static float s_targetPtLeft, s_targetPtTop, s_targetPtRight, s_targetPtBottom;  // points, hypotheses (b)/(d)
        static bool s_haveContainer;            // hypothesis (c) inputs resolved
        static bool s_relIsExact;               // rel rect comes from the GUIView itself → near-equality match
        static float s_containerW, s_containerH;                                        // container size, points
        static float s_relLeft, s_relTop, s_relRight, s_relBottom;                      // rect relative to container, points
        static IntPtr s_best;
        static long s_bestArea;
        static int s_candidateCount;
        static IntPtr s_firstCandidate;

        // Flipped to false on pre-1607 Windows where GetDpiForWindow is missing;
        // per-monitor hypotheses are then skipped and behavior matches the legacy search.
        static bool s_dpiApiAvailable = true;

        // Diagnostics: dump all candidates on the first failure and every 32nd
        // one after that, so a stuck editor produces a conclusive log without
        // flooding the console (retries run about twice per second).
        static int s_failCount;
        static StringBuilder s_diag;
        static IntPtr s_diagReflected;
        static string s_diagReflectedClass = "";

        // Cached reflection lookups for hypothesis (c). Resolved once; any miss
        // simply disables that hypothesis.
        static bool s_reflectionResolved;
        static FieldInfo s_fiParent;            // EditorWindow.m_Parent
        static PropertyInfo s_piWindow;         // View.window → ContainerWindow
        static PropertyInfo s_piScreenPosition; // View.screenPosition (points)
        static PropertyInfo s_piContainerPosition; // ContainerWindow.position (points)

        // ── Public API ─────────────────────────────────────────────────────

        public static IntPtr GetHWND(EditorWindow win)
        {
            if (win == null) return IntPtr.Zero;

            try
            {
                // Strategy 1/2: reflected native handle, exact or as search scope.
                IntPtr searchRoot;
                IntPtr reflected = TryReflectedHandle(win, out searchRoot);
                if (reflected != IntPtr.Zero)
                {
                    s_failCount = 0;
                    return reflected;
                }

                Rect pos = win.position;
                if (pos.width < 1f || pos.height < 1f) return IntPtr.Zero;

                PrepareTargets(win, pos);

                bool collectDiag = s_failCount == 0 || (s_failCount % 32) == 0;
                s_diag = collectDiag ? new StringBuilder() : null;

                IntPtr found = IntPtr.Zero;
                if (searchRoot != IntPtr.Zero)
                {
                    if (s_diag != null) s_diag.AppendLine($"-- scoped pass under root=0x{searchRoot.ToInt64():X} --");
                    found = SearchScoped(searchRoot);
                }
                if (found == IntPtr.Zero)
                {
                    if (s_diag != null) s_diag.AppendLine("-- global pass --");
                    found = SearchGlobal();
                }

                if (found != IntPtr.Zero)
                {
                    s_failCount = 0;
                    s_diag = null;
                    return found;
                }

                s_failCount++;
                if (s_diag != null)
                {
                    LogDiagnostics(win, pos, searchRoot);
                    s_diag = null;
                }
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                CodelyLogger.LogWarning($"[NativeWindowHelper] GetHWND exception: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        // ── Strategy 1/2: reflection ───────────────────────────────────────

        /// <summary>
        /// Read GUIView.nativeHandle through the shared reflection helper. If it
        /// is our process's UnityGUIViewWndClass window, return it. If it is a
        /// valid window of ours with a different class (some editor builds hand
        /// out the container window instead), expose its top-level ancestor via
        /// <paramref name="searchRoot"/> so the rect search can be scoped to the
        /// right container. Anything else (zero, foreign, not a window at all —
        /// e.g. a native object pointer) is ignored.
        /// </summary>
        static IntPtr TryReflectedHandle(EditorWindow win, out IntPtr searchRoot)
        {
            searchRoot = IntPtr.Zero;
            s_diagReflected = IntPtr.Zero;
            s_diagReflectedClass = "";
            try
            {
                IntPtr handle = EditorWindowNativeHandleHelper.GetGUIViewHandle(win);
                s_diagReflected = handle;
                if (handle == IntPtr.Zero || !IsWindow(handle)) return IntPtr.Zero;

                GetWindowThreadProcessId(handle, out uint pid);
                if ((int)pid != s_currentPid) return IntPtr.Zero;

                string cls = ClassNameOf(handle);
                s_diagReflectedClass = cls;
                if (cls == kGUIViewClass) return handle;

                searchRoot = GetAncestor(handle, GA_ROOT);
                return IntPtr.Zero;
            }
            catch
            {
                // Reflection target changed shape or the native view is mid-teardown.
                return IntPtr.Zero;
            }
        }

        // ── Strategy 3: rect search ────────────────────────────────────────

        static IntPtr SearchScoped(IntPtr root)
        {
            ResetSearchState();
            EnumChildWindows(root, s_innerProc, IntPtr.Zero);
            if (s_best != IntPtr.Zero) return s_best;

            // The scope IS the window's own container; if it hosts exactly one
            // GUIView there is nothing to disambiguate — take it even when no
            // coordinate hypothesis matched (typical floating panel).
            if (s_candidateCount == 1) return s_firstCandidate;
            return IntPtr.Zero;
        }

        static IntPtr SearchGlobal()
        {
            ResetSearchState();
            EnumWindows(s_outerProc, IntPtr.Zero);
            return s_best;
        }

        static void ResetSearchState()
        {
            s_best = IntPtr.Zero;
            s_bestArea = long.MaxValue;
            s_candidateCount = 0;
            s_firstCandidate = IntPtr.Zero;
        }

        /// <summary>Fill the per-call matching targets for all hypotheses.</summary>
        static void PrepareTargets(EditorWindow win, Rect pos)
        {
            Rect target = ScaleToPixels(pos);
            s_targetLeft   = (int)target.x;
            s_targetTop    = (int)target.y;
            s_targetRight  = (int)(target.x + target.width);
            s_targetBottom = (int)(target.y + target.height);

            s_targetPtLeft   = pos.x;
            s_targetPtTop    = pos.y;
            s_targetPtRight  = pos.x + pos.width;
            s_targetPtBottom = pos.y + pos.height;

            // Hypothesis (c): the window's rect relative to its ContainerWindow,
            // in points. Prefer the host GUIView's own screenPosition — it should
            // equal the candidate HWND's rect, allowing a near-equality match.
            s_haveContainer = false;
            s_relIsExact = false;
            try
            {
                ResolveReflection();
                object parent = s_fiParent?.GetValue(win);
                if (parent == null) return;

                object container = s_piWindow?.GetValue(parent, null);
                if (container == null || s_piContainerPosition == null) return;

                Rect containerPos = (Rect)s_piContainerPosition.GetValue(container, null);
                if (containerPos.width < 1f || containerPos.height < 1f) return;

                Rect basis = pos;
                if (s_piScreenPosition != null)
                {
                    Rect sp = (Rect)s_piScreenPosition.GetValue(parent, null);
                    if (sp.width >= 1f && sp.height >= 1f)
                    {
                        basis = sp;
                        s_relIsExact = true;
                    }
                }

                s_containerW = containerPos.width;
                s_containerH = containerPos.height;
                s_relLeft   = basis.x - containerPos.x;
                s_relTop    = basis.y - containerPos.y;
                s_relRight  = s_relLeft + basis.width;
                s_relBottom = s_relTop + basis.height;
                s_haveContainer = true;
            }
            catch
            {
                s_haveContainer = false;
            }
        }

        static void ResolveReflection()
        {
            if (s_reflectionResolved) return;
            s_reflectionResolved = true;
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                s_fiParent = typeof(EditorWindow).GetField("m_Parent", flags);

                Type viewType = s_fiParent?.FieldType;          // HostView (derives from GUIView/View)
                s_piWindow = viewType?.GetProperty("window", flags);
                s_piScreenPosition = viewType?.GetProperty("screenPosition", flags);

                Type containerType = s_piWindow?.PropertyType;  // ContainerWindow
                s_piContainerPosition = containerType?.GetProperty("position", flags);
            }
            catch
            {
                // Editor internals changed shape; hypothesis (c) stays disabled.
            }
        }

        // ── Internals ──────────────────────────────────────────────────────

        // EditorWindow.position is in DPI-scaled "points"; Win32 GetWindowRect
        // returns physical pixels. Convert before comparing. Note this uses the
        // CALLER's pixelsPerPoint, which only matches the window's monitor when
        // both share a DPI scale — the other hypotheses cover the rest.
        static Rect ScaleToPixels(Rect r)
        {
            float dpi;
            try { dpi = EditorGUIUtility.pixelsPerPoint; }
            catch { dpi = 1f; }
            if (dpi <= 0f) dpi = 1f;
            return new Rect(r.x * dpi, r.y * dpi, r.width * dpi, r.height * dpi);
        }

        static string ClassNameOf(IntPtr hwnd)
        {
            int clen = GetClassNameW(hwnd, s_classBuf, s_classBuf.Length);
            return clen > 0 ? new string(s_classBuf, 0, clen) : "";
        }

        [AOT.MonoPInvokeCallback(typeof(EnumWindowsProc))]
        static bool OuterEnumProc(IntPtr topHwnd, IntPtr param)
        {
            try
            {
                GetWindowThreadProcessId(topHwnd, out uint pid);
                if ((int)pid != s_currentPid) return true;
                EnumChildWindows(topHwnd, s_innerProc, IntPtr.Zero);
            }
            catch { /* ignore */ }
            return true;
        }

        [AOT.MonoPInvokeCallback(typeof(EnumWindowsProc))]
        static bool InnerEnumProc(IntPtr childHwnd, IntPtr param)
        {
            try
            {
                if (ClassNameOf(childHwnd) != kGUIViewClass) return true;

                if (!GetWindowRect(childHwnd, out RECT wr)) return true;

                s_candidateCount++;
                if (s_candidateCount == 1) s_firstCandidate = childHwnd;

                float scale = s_dpiApiAvailable ? GetWindowScale(childHwnd) : 0f;

                // (a) physical-pixel comparison with the caller-context scale
                bool a =
                    wr.Left   - Slack <= s_targetLeft   &&
                    wr.Top    - Slack <= s_targetTop    &&
                    wr.Right  + Slack >= s_targetRight  &&
                    wr.Bottom + Slack >= s_targetBottom;

                // (b) point-space comparison with the candidate's own monitor scale
                bool b = false;
                if (!a && scale > 0f)
                {
                    b =
                        wr.Left   / scale - Slack <= s_targetPtLeft   &&
                        wr.Top    / scale - Slack <= s_targetPtTop    &&
                        wr.Right  / scale + Slack >= s_targetPtRight  &&
                        wr.Bottom / scale + Slack >= s_targetPtBottom;
                }

                // (d) like (b) but anchored at the candidate's monitor origin
                bool d = false;
                if (!a && !b && scale > 0f)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    IntPtr hmon = MonitorFromWindow(childHwnd, MONITOR_DEFAULTTONEAREST);
                    if (hmon != IntPtr.Zero && GetMonitorInfoW(hmon, ref mi))
                    {
                        float mx = mi.rcMonitor.Left, my = mi.rcMonitor.Top;
                        d =
                            mx + (wr.Left   - mx) / scale - Slack <= s_targetPtLeft   &&
                            my + (wr.Top    - my) / scale - Slack <= s_targetPtTop    &&
                            mx + (wr.Right  - mx) / scale + Slack >= s_targetPtRight  &&
                            my + (wr.Bottom - my) / scale + Slack >= s_targetPtBottom;
                    }
                }

                // (c) container-relative comparison — mapping-independent
                bool c = false;
                if (!a && !b && !d && scale > 0f && s_haveContainer)
                {
                    c = MatchesContainerRelative(childHwnd, wr, scale);
                }

                if (s_diag != null)
                {
                    // Extra geometry so a single failed dump reconstructs the mapping.
                    string monStr = "?", rootStr = "?";
                    var dmi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    IntPtr dmon = MonitorFromWindow(childHwnd, MONITOR_DEFAULTTONEAREST);
                    if (dmon != IntPtr.Zero && GetMonitorInfoW(dmon, ref dmi))
                        monStr = $"({dmi.rcMonitor.Left},{dmi.rcMonitor.Top})";
                    IntPtr droot = GetAncestor(childHwnd, GA_ROOT);
                    if (droot != IntPtr.Zero && GetClientRect(droot, out RECT drc))
                    {
                        var dorigin = new POINT();
                        if (ClientToScreen(droot, ref dorigin))
                            rootStr = $"0x{droot.ToInt64():X}@({dorigin.X},{dorigin.Y}) client={drc.Right}x{drc.Bottom}";
                    }
                    s_diag.AppendLine(
                        $"  cand=0x{childHwnd.ToInt64():X} phys=({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom}) " +
                        $"scale={scale:F2} mon={monStr} root={rootStr} a={a} b={b} d={d} c={c}");
                }

                if (!(a || b || d || c)) return true;

                long area = (long)wr.Width * wr.Height;
                if (area < s_bestArea)
                {
                    s_bestArea = area;
                    s_best = childHwnd;
                }
            }
            catch { /* window may have closed mid-enumeration */ }
            return true;
        }

        static bool MatchesContainerRelative(IntPtr childHwnd, RECT wr, float scale)
        {
            IntPtr root = GetAncestor(childHwnd, GA_ROOT);
            if (root == IntPtr.Zero) return false;
            if (!GetClientRect(root, out RECT rc)) return false;
            var origin = new POINT();
            if (!ClientToScreen(root, ref origin)) return false;

            // The right top-level window has (about) the ContainerWindow's size.
            // Unity's ContainerWindow.position may describe the client area or the
            // full frame depending on chrome — accept either within slack.
            float clientW = rc.Right / scale, clientH = rc.Bottom / scale;
            bool sizeOk =
                Math.Abs(clientW - s_containerW) <= Slack &&
                Math.Abs(clientH - s_containerH) <= Slack;
            if (!sizeOk && GetWindowRect(root, out RECT rwr))
            {
                sizeOk =
                    Math.Abs(rwr.Width  / scale - s_containerW) <= Slack &&
                    Math.Abs(rwr.Height / scale - s_containerH) <= Slack;
            }
            if (!sizeOk) return false;

            float relLeft   = (wr.Left   - origin.X) / scale;
            float relTop    = (wr.Top    - origin.Y) / scale;
            float relRight  = (wr.Right  - origin.X) / scale;
            float relBottom = (wr.Bottom - origin.Y) / scale;

            if (s_relIsExact)
            {
                // Comparing the GUIView's own rect — edges should line up.
                return
                    Math.Abs(relLeft   - s_relLeft)   <= Slack &&
                    Math.Abs(relTop    - s_relTop)    <= Slack &&
                    Math.Abs(relRight  - s_relRight)  <= Slack &&
                    Math.Abs(relBottom - s_relBottom) <= Slack;
            }

            // Comparing the hosted EditorWindow content rect — the GUIView contains it.
            return
                relLeft   - Slack <= s_relLeft   &&
                relTop    - Slack <= s_relTop    &&
                relRight  + Slack >= s_relRight  &&
                relBottom + Slack >= s_relBottom;
        }

        static float GetWindowScale(IntPtr hwnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                return dpi > 0 ? dpi / 96f : 0f;
            }
            catch (EntryPointNotFoundException)
            {
                s_dpiApiAvailable = false;
                return 0f;
            }
        }

        static void LogDiagnostics(EditorWindow win, Rect pos, IntPtr searchRoot)
        {
            try
            {
                float ppp;
                try { ppp = EditorGUIUtility.pixelsPerPoint; } catch { ppp = -1f; }

                var header = new StringBuilder();
                header.AppendLine($"[NativeWindowHelper] diagnose (fail #{s_failCount}): no GUIView matched {win.GetType().Name}");
                header.AppendLine($"  win.position=({pos.x:F1},{pos.y:F1},{pos.width:F1},{pos.height:F1}) pixelsPerPoint={ppp:F2} dpiApi={s_dpiApiAvailable}");
                header.AppendLine($"  reflected=0x{s_diagReflected.ToInt64():X} class='{s_diagReflectedClass}' searchRoot=0x{searchRoot.ToInt64():X}");
                header.AppendLine(s_haveContainer
                    ? $"  container={s_containerW:F1}x{s_containerH:F1} rel=({s_relLeft:F1},{s_relTop:F1})-({s_relRight:F1},{s_relBottom:F1}) exact={s_relIsExact}"
                    : "  container=<unavailable>");
                header.Append(s_diag);
                CodelyLogger.LogWarning(header.ToString());
            }
            catch { /* diagnostics must never break the lookup */ }
        }
    }
}
#endif
