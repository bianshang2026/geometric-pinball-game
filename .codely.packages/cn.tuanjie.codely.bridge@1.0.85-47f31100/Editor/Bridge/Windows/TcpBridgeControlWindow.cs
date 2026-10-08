using UnityEditor;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Windows
{
    public class TcpBridgeControlWindow : EditorWindow
    {
        private enum BridgeState { NotStarted, Starting, Started }

        private double lastStatusUpdate;
        private const double StatusUpdateInterval = 0.5;
        private BridgeState currentState = BridgeState.NotStarted;
        private bool isStarting = false;
        private double startingTime = 0;
        private const double StartingTimeout = 3.0;

        // Window title icon
        private Texture2D titleIcon;

        // Client icons
        private Texture2D codelyIcon;
        private Texture2D vscodeIcon;
        private Texture2D visualStudioIcon;
        private Texture2D jetbrainsIcon;
        private Texture2D cliIcon;
        private Texture2D unityEditorIcon;
        private Texture2D tuanjieIcon;
        private Texture2D appIcon;
        private Texture2D unknownIcon;

        // Fonts
        private Font firaMono;
        private Font inter;

        // Button textures
        private Texture2D connectTex;
        private Texture2D connectHoverTex;
        private Texture2D connectPressedTex;
        private Texture2D connectingTex;
        private Texture2D disconnectTex;
        private Texture2D disconnectHoverTex;
        private Texture2D disconnectPressedTex;

        // Connection tracking (count per client type)
        private int connectedToCLI;
        private int connectedToVSCode;
        private int connectedToVisualStudio;
        private int connectedToJetBrains;
        private int connectedToUnityEditor;
        private int connectedToTuanjie;
        private int connectedToApp;
        private int connectedToUnknown;

        private static readonly Color GreenColor  = new Color(0x01 / 255f, 0xA7 / 255f, 0x7F / 255f);
        private static readonly Color GrayColor   = new Color(0.5f, 0.5f, 0.5f);
        private static readonly Color SubtextColor = new Color(0x9C / 255f, 0xA3 / 255f, 0xAF / 255f);

        // ── Base sizes (ScaleFactor = 1) ─────────────────────────────────────────
        private const float BaseWindowWidth          = 430f;
        private const float BaseLeftMargin           = 25f;
        private const float BaseTopPadding           = 40f;
        private const float BaseBottomPadding        = 40f;
        private const float BaseCodelyIconSize       = 40f;
        private const float BaseSpaceAfterIcon       = 25f;
        private const int   BaseTitleFontSize        = 20;
        private const float BaseSpaceAfterTitle      = 15f;
        private const int   BaseBodyFontSize         = 17;
        private const float BaseSpaceAfterBody       = 30f;
        private const int   BaseStatusFontSize       = 12;
        private const float BaseSpaceBeforeStatus    = 30f;
        private const int   BaseConnectedFontSize    = 16;
        private const float BaseConnectedItemSpacing = 6f;
        private const float BaseClientIconSize       = 24f;
        private const float BaseClientIconLabelGap   = 6f;
        private const int   BaseClientLabelFontSize  = 13;
        private const float BaseClientLabelHeight    = 18f;
        private const float BaseClientItemSpacing    = 12f;
        private const float BaseConnectBtnW          = 159f;
        private const float BaseConnectBtnH          = 40f;

        private const float MinWindowHeight = 80f;
        private const float MinWindowWidth = 430f;

        // ── Scale ────────────────────────────────────────────────────────────────
        private float ScaleFactor => position.width / MinWindowWidth;

        // ── Scaled values ─────────────────────────────────────────────────────────
        
        
        private float LeftMargin           => BaseLeftMargin           * ScaleFactor;
        private float TopPadding           => BaseTopPadding           * ScaleFactor;
        private float BottomPadding        => BaseBottomPadding        * ScaleFactor;
        private float CodelyIconSize       => BaseCodelyIconSize       * ScaleFactor;
        private float SpaceAfterIcon       => BaseSpaceAfterIcon       * ScaleFactor;
        private int   TitleFontSize        => Mathf.RoundToInt(BaseTitleFontSize       * ScaleFactor);
        private float SpaceAfterTitle      => BaseSpaceAfterTitle      * ScaleFactor;
        private int   BodyFontSize         => Mathf.RoundToInt(BaseBodyFontSize        * ScaleFactor);
        private float SpaceAfterBody       => BaseSpaceAfterBody       * ScaleFactor;
        private int   StatusFontSize       => Mathf.RoundToInt(BaseStatusFontSize      * ScaleFactor);
        private float SpaceBeforeStatus    => BaseSpaceBeforeStatus    * ScaleFactor;
        private int   ConnectedFontSize    => Mathf.RoundToInt(BaseConnectedFontSize   * ScaleFactor);
        private float ConnectedItemSpacing => BaseConnectedItemSpacing  * ScaleFactor;
        private float ClientIconSize       => BaseClientIconSize        * ScaleFactor;
        private float ClientIconLabelGap   => BaseClientIconLabelGap   * ScaleFactor;
        private int   ClientLabelFontSize  => Mathf.RoundToInt(BaseClientLabelFontSize * ScaleFactor);
        private float ClientLabelHeight    => BaseClientLabelHeight     * ScaleFactor;
        private float ClientItemSpacing    => BaseClientItemSpacing     * ScaleFactor;
        private float ConnectBtnW          => BaseConnectBtnW           * ScaleFactor;
        private float ConnectBtnH          => BaseConnectBtnH           * ScaleFactor;
        [MenuItem("AI/Check Connections", priority = 998)]
        public static void ShowWindow()
        {
            var window = GetWindow<TcpBridgeControlWindow>("Tuanjie AI Bridge");
            window.minSize = new Vector2(MinWindowWidth, MinWindowHeight);
            window.Show();
        }

        private void OnEnable()
        {
            EditorPrefs.SetBool("UnityTcp.DebugLogs", true);
            // Set a text-only fallback immediately so the tab is never blank.
            titleContent = new GUIContent(" Tuanjie AI Bridge");
            LoadIcons();
            // AssetDatabase / icon textures may not be ready in OnEnable
            // (fresh import, package install, layout restore, domain reload).
            // Defer until the editor is idle, then assign the icon.
            EditorApplication.delayCall += ApplyTitleIcon;
            UpdateStatus();
            UnityTcpBridge.OnClientPlatformsChanged += OnClientPlatformsChanged;
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= ApplyTitleIcon;
            UnityTcpBridge.OnClientPlatformsChanged -= OnClientPlatformsChanged;
        }

        // Cached padded copy. Static so it's shared and easier to detect "destroyed
        // by domain reload" (== null) without losing the field reference.
        private static Texture2D paddedTitleIcon;

        private void ApplyTitleIcon()
        {
            if (this == null) return; // window destroyed before delayCall fired
            if (titleIcon == null)
                titleIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(
                    EditorGUIUtility.isProSkin
                        ? "Packages/cn.tuanjie.codely.bridge/Editor/Bridge/Icons/title_icon.png"
                        : "Packages/cn.tuanjie.codely.bridge/Editor/Bridge/Icons/title_icon_light_theme.png");
            if (titleIcon == null) return;

            titleContent = new GUIContent(" Tuanjie AI Bridge", titleIcon);
        }



        private void OnClientPlatformsChanged() => UpdateConnectionStatus();

        private static Texture2D Load(string path) =>
            AssetDatabase.LoadAssetAtPath<Texture2D>(path);

        private void LoadIcons()
        {
            const string b = "Packages/cn.tuanjie.codely.bridge/Editor/Bridge/Icons/";
            codelyIcon = Load(b + (EditorGUIUtility.isProSkin ? "codely_logo.png" : "codely_logo_light_theme.png"));
            vscodeIcon = Load(b + "vscode.png");
            visualStudioIcon = Load(b + "visualstudio.png");
            jetbrainsIcon = Load(b + "jetbrains.png");
            cliIcon = Load(b + "cli.png");
            unityEditorIcon = Load(b + "unity_editor.png");
            tuanjieIcon = Load(b + "tuanjie.png");
            appIcon = Load(b + "app.png");
            unknownIcon = Load(b + "unknown.png");
            connectTex = Load(b + "connect.png");
            connectHoverTex = Load(b + "connect_hover.png");
            connectPressedTex = Load(b + "connect_pressed.png");
            connectingTex = Load(b + "connecting.png");
            disconnectTex = Load(b + "disconnect.png");
            disconnectHoverTex = Load(b + "disconnect_hover.png");
            disconnectPressedTex = Load(b + "disconnect_pressed.png");

            firaMono = AssetDatabase.LoadAssetAtPath<Font>("Packages/cn.tuanjie.codely.bridge/Editor/Bridge/fonts/FiraMono-Regular.ttf");
            inter    = AssetDatabase.LoadAssetAtPath<Font>("Packages/cn.tuanjie.codely.bridge/Editor/Bridge/fonts/Inter.ttc");
        }

        private void OnGUI()
        {
            // Self-heal: after a domain reload the cached icon may be a "missing"
            // UnityEngine.Object. Re-apply if the tab lost its icon.
            if (titleContent == null || titleContent.image == null)
                ApplyTitleIcon();

            if (EditorApplication.timeSinceStartup - lastStatusUpdate > StatusUpdateInterval)
            {
                UpdateStatus();
                lastStatusUpdate = EditorApplication.timeSinceStartup;
            }

            DrawUI();
        }

        private static readonly Color BgColor = new Color(0.18f, 0.18f, 0.18f);

        private void DrawUI()
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), BgColor);

            bool started = currentState == BridgeState.Started;

            // ── Top padding ──────────────────────────────────────────────────────
            GUILayout.Space(TopPadding);

            // ── Codely icon ──────────────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            GUILayout.Space(LeftMargin);
            if (codelyIcon != null)
                GUILayout.Box(codelyIcon, GUIStyle.none, GUILayout.Width(CodelyIconSize), GUILayout.Height(CodelyIconSize));
            GUILayout.EndHorizontal();

            GUILayout.Space(SpaceAfterIcon);

            // ── Title ────────────────────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            GUILayout.Space(LeftMargin);
            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = TitleFontSize, wordWrap = false, font = inter };
            if (started) {
                titleStyle.normal.textColor = GreenColor;
                titleStyle.hover.textColor = GreenColor;
            }
            else {
                titleStyle.normal.textColor = Color.white;
                titleStyle.hover.textColor = Color.white;
            }
            GUILayout.Label(
                started ? "Tuanjie AI bridge is ready  \u2713" : "Connect to Tuanjie AI Agent",
                titleStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(SpaceAfterTitle);

            // ── Body (Form 1: client list | Form 2: text + status) ───────────────
            bool hasClients = started && (connectedToCLI > 0 || connectedToVSCode > 0 || connectedToVisualStudio > 0
                                       || connectedToJetBrains > 0 || connectedToUnityEditor > 0 || connectedToTuanjie > 0
                                       || connectedToApp > 0 || connectedToUnknown > 0);

            GUILayout.BeginHorizontal();
            GUILayout.Space(LeftMargin);
            GUILayout.BeginVertical();
            if (hasClients)
                DrawClientListBody();
            else
                DrawNoClientBody();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.Space(SpaceAfterBody);

            // ── Action button ────────────────────────────────────────────────────
            Texture2D btnNormal = null, btnPressed = null, btnHover = null;
            bool btnEnabled = true;
            System.Action btnAction = null;

            switch (currentState)
            {
                case BridgeState.NotStarted:
                    btnNormal  = connectTex;
                    btnPressed = connectPressedTex;
                    btnHover   = connectHoverTex;
                    btnAction  = StartBridge;
                    break;
                case BridgeState.Starting:
                    btnNormal  = connectingTex;
                    btnPressed = connectingTex;
                    btnHover   = connectingTex;
                    btnEnabled = false;
                    break;
                case BridgeState.Started:
                    btnNormal  = disconnectTex;
                    btnPressed = disconnectPressedTex;
                    btnHover   = disconnectHoverTex;
                    btnAction  = StopBridge;
                    break;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Space(LeftMargin);

            if (btnNormal != null)
            {
                float btnW = ConnectBtnW;
                float btnH = ConnectBtnH;

                var style = new GUIStyle(GUIStyle.none);
                style.normal.background = btnNormal;
                style.active.background = btnPressed ?? btnNormal;
                style.hover.background  = btnHover   ?? btnNormal;

                if (!btnEnabled) GUI.enabled = false;
                if (GUILayout.Button(GUIContent.none, style, GUILayout.Width(btnW), GUILayout.Height(btnH))
                    && btnAction != null)
                    btnAction();
                if (!btnEnabled) GUI.enabled = true;

            }
            else
            {
                string fallback = currentState == BridgeState.NotStarted ? "Connect \u2192"
                                : currentState == BridgeState.Starting   ? "Connecting..."
                                : "Disconnect";
                if (!btnEnabled) GUI.enabled = false;
                if (GUILayout.Button(fallback, GUILayout.Width(ConnectBtnW), GUILayout.Height(ConnectBtnH))
                    && btnAction != null)
                    btnAction();
                if (!btnEnabled) GUI.enabled = true;
            }

            GUILayout.EndHorizontal();

            // ── Bottom padding ───────────────────────────────────────────────────
            GUILayout.Space(BottomPadding);

            // ── Auto-resize height to fit content ────────────────────────────────
            if (Event.current.type == EventType.Repaint)
            {
                float targetH = GUILayoutUtility.GetLastRect().yMax;
#if UNITY_2020_1_OR_NEWER
                bool isDocked = docked;
#else
                bool isDocked = IsWindowDocked();
#endif
                if (!isDocked)
                {
                    minSize = new Vector2(MinWindowWidth, targetH);
                    maxSize = new Vector2(8192, targetH);
                }
            }
        }

#if !UNITY_2020_1_OR_NEWER
        // EditorWindow.docked became public in 2020.1; on older editors the property is internal,
        // so probe it through reflection. Returns false if reflection fails.
        private bool IsWindowDocked()
        {
            try
            {
                var prop = typeof(UnityEditor.EditorWindow).GetProperty(
                    "docked",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (prop != null && prop.PropertyType == typeof(bool))
                {
                    return (bool)prop.GetValue(this, null);
                }
            }
            catch { /* ignore */ }
            return false;
        }
#endif

        // Form 1 – bridge started and at least one client is connected
        private void DrawClientListBody()
        {
            var labelStyle = new GUIStyle(EditorStyles.label) { fontSize = ConnectedFontSize, font = inter };
            labelStyle.normal.textColor = SubtextColor;
            labelStyle.hover.textColor  = SubtextColor;

            GUILayout.BeginHorizontal();

            // Left column: "Connected to :" label, top-aligned
            float labelW = labelStyle.CalcSize(new GUIContent("Connected to :")).x + 4f * ScaleFactor;
            GUILayout.Label("Connected to :", labelStyle, GUILayout.Width(labelW));

            // Right column: all clients stacked vertically
            GUILayout.BeginVertical();
            if (connectedToVSCode > 0) DrawClient(vscodeIcon, "VS Code");
            if (connectedToVisualStudio > 0) DrawClient(visualStudioIcon, "Visual Studio");
            if (connectedToJetBrains > 0) DrawClient(jetbrainsIcon, "JetBrains");
            if (connectedToCLI > 0) DrawClient(cliIcon, "CLI");
            if (connectedToUnityEditor > 0) DrawClient(unityEditorIcon, "Unity Editor");
            if (connectedToTuanjie > 0) DrawClient(tuanjieIcon, "Tuanjie");
            if (connectedToApp > 0) DrawClient(appIcon, "Cowork");
            if (connectedToUnknown > 0) DrawClient(unknownIcon, "Unknown");

            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        // Form 2 – not started, starting, or started but no clients yet
        private void DrawNoClientBody()
        {
            string bodyText = currentState == BridgeState.Started
                ? "Waiting for clients..."
                : "Start Tuanjie AI Bridge to connect to Tuanjie AI \nAgents.";

            var bodyStyle = new GUIStyle(EditorStyles.label) { fontSize = BodyFontSize, wordWrap = true, font = inter};
            bodyStyle.normal.textColor = SubtextColor;
            bodyStyle.hover.textColor  = SubtextColor;
            GUILayout.Label(bodyText, bodyStyle);

            GUILayout.Space(SpaceBeforeStatus);

            string dots = new string('.', (int)(EditorApplication.timeSinceStartup * 2) % 3 + 1);
            string statusText = currentState == BridgeState.Starting ? $"\u2022 Status: Connecting{dots}"
                              : currentState == BridgeState.Started  ? "\u2022 Status: Waiting for clients..."
                              :                                         "\u2022 Status: Disconnected";

            GUILayout.Label(
                statusText,
                new GUIStyle(EditorStyles.label)
                {
                    fontSize = StatusFontSize,
                    font     = firaMono,
                    normal   = { textColor = GrayColor },
                    hover    = { textColor = GrayColor }
                });
        }

        private void DrawClient(Texture2D icon, string label)
        {
            GUILayout.BeginHorizontal();
            if (icon != null)
                GUILayout.Box(icon, GUIStyle.none, GUILayout.Width(ClientIconSize), GUILayout.Height(ClientIconSize));
            else
                GUILayout.Space(ClientIconSize);
            GUILayout.Space(ClientIconLabelGap);
            GUILayout.Label(
                label,
                new GUIStyle(EditorStyles.label) { fontSize = ClientLabelFontSize, font = inter, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white }, hover = { textColor = Color.white } },
                GUILayout.Height(ClientLabelHeight));
            GUILayout.EndHorizontal();
            GUILayout.Space(ClientItemSpacing);
        }

        private void StartBridge()
        {
            try
            {
                UnityTcpBridge.Start();
                isStarting   = true;
                startingTime = EditorApplication.timeSinceStartup;
                currentState = BridgeState.Starting;
                CodelyLogger.Log("Codely Bridge started via Control Window");
            }
            catch (System.Exception ex)
            {
                CodelyLogger.LogError($"Failed to start Codely Bridge: {ex.Message}");
                currentState = BridgeState.NotStarted;
                isStarting   = false;
            }
        }

        private void StopBridge()
        {
            try
            {
                UnityTcpBridge.Stop(isManualStop: true);
                CodelyLogger.Log("Codely Bridge manually stopped via Control Window");
                currentState = BridgeState.NotStarted;
                isStarting   = false;
                ResetConnections();
            }
            catch (System.Exception ex)
            {
                CodelyLogger.LogError($"Failed to stop Codely Bridge: {ex.Message}");
            }
        }

        private void ResetConnections()
        {
            connectedToCLI = connectedToVSCode = connectedToVisualStudio =
            connectedToJetBrains = connectedToUnityEditor = connectedToTuanjie = connectedToApp = connectedToUnknown = 0;
        }

        private void UpdateStatus()
        {
            bool isRunning = UnityTcpBridge.IsRunning;

            if (isStarting && isRunning)
            {
                currentState = BridgeState.Started;
                isStarting   = false;
            }
            else if (isStarting && !isRunning)
            {
                if (EditorApplication.timeSinceStartup - startingTime > StartingTimeout)
                {
                    currentState = BridgeState.NotStarted;
                    isStarting   = false;
                }
            }
            else if (!isStarting && isRunning && currentState != BridgeState.Started)
            {
                currentState = BridgeState.Started;
            }
            else if (!isRunning && currentState == BridgeState.Started)
            {
                currentState = BridgeState.NotStarted;
                ResetConnections();
            }

            if (currentState == BridgeState.Started)
                UpdateConnectionStatus();
        }

        private void UpdateConnectionStatus()
        {
            var clients = UnityTcpBridge.GetConnectedClients();
            ResetConnections();

            foreach (var platform in clients.Values)
            {
                var p = platform.ToLower();
                if      (p.Contains("cli"))
                    connectedToCLI++;
                else if (p.Contains("vscode") || p.Contains("vs code"))
                    connectedToVSCode++;
                else if (p.Contains("visualstudio") || p.Contains("visual studio"))
                    connectedToVisualStudio++;
                else if (p.Contains("jetbrains") || p.Contains("intellij") || p.Contains("rider"))
                    connectedToJetBrains++;
                else if (p.Contains("tuanjie") || p.Contains("codely") || p.Contains("agent"))
                    connectedToTuanjie++;
                else if (p.Contains("unity"))
                    connectedToUnityEditor++;
                else if (p.Contains("app"))
                    connectedToApp++;
                else
                    connectedToUnknown++;
            }
        }

        private void OnInspectorUpdate() => Repaint();
    }
}
