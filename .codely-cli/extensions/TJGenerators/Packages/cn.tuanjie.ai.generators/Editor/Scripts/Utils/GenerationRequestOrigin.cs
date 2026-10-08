#if UNITY_EDITOR
using TJGenerators.Config;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 生成请求来源标识。通过 HTTP 头 <see cref="HeaderName"/>（与 "source" 并列）上报，
    /// 让后端区分本次生成是由编辑器 UI 面板发起，还是由 AI custom tool（agent）发起。
    /// </summary>
    public static class GenerationRequestOrigin
    {
        /// <summary>请求头名称。</summary>
        public const string HeaderName = "fromMethod";

        /// <summary>包版本请求头名称，与 fromMethod 并列上报。</summary>
        public const string PackageVersionHeaderName = "X-Package-Version";

        /// <summary>Agent 会话 ID 请求头名称，用于按 session 分组查询任务。</summary>
        public const string SessionIdHeaderName = "X-Session-Id";

        /// <summary>Workspace 名请求头名称，与 X-Session-Id 并列上报，用于按项目/工作区分组统计。</summary>
        public const string WorkspaceNameHeaderName = "X-Workspace-Name";

        /// <summary>编辑器 UI 面板发起的生成。</summary>
        public const string Ui = "ui";

        /// <summary>AI custom tool（agent）发起的生成。</summary>
        public const string Agent = "agent";

        private static string _workspaceName = "";

        /// <summary>
        /// 当前 workspace 名（codely-cli 在 execute_custom_tool 参数中注入 workspace_name，
        /// 各 custom tool 入口读取后调用 <see cref="SetWorkspaceName"/> 缓存）。
        /// 同一编辑器会话内不变；为空表示未知，此时不上报该头。
        /// </summary>
        public static string CurrentWorkspaceName
        {
            get { return _workspaceName; }
        }

        /// <summary>缓存 workspace 名；空值忽略（保留已缓存值）。</summary>
        public static void SetWorkspaceName(string value)
        {
            if (!string.IsNullOrEmpty(value))
                _workspaceName = value;
        }

        /// <summary>
        /// 获取当前包版本号，通过 PackageManager API 动态读取。
        /// 获取失败时返回空字符串（后端会将空版本归入"未知"分组）。
        /// </summary>
        public static string GetPackageVersion()
        {
            try
            {
                // 优先通过 Assembly 查找（对注册到 Package Manager 的包有效）
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ConfigManager).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.version))
                    return info.version;

                // 本地包安装时 FindForAssembly 可能返回 null，
                // 通过包内已知资源路径回退查找
                info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(
                    "Packages/cn.tuanjie.ai.generators/package.json");
                if (info != null && !string.IsNullOrEmpty(info.version))
                    return info.version;

                return "";
            }
            catch
            {
                return "";
            }
        }
    }
}
#endif
