#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace TJGenerators
{
    /// <summary>
    /// Domain reload 后检查 UnityTcp.CustomTool 是否加载，未加载则输出可操作的修复指引。
    /// 本类属于 tjgenerators.Editor，不依赖 UnityTcp.Editor，可在 CustomTool 编译失败时独立运行。
    /// </summary>
    [InitializeOnLoad]
    internal static class TJGeneratorsCustomToolDiagnostic
    {
        private const string CustomToolAssemblyName  = "UnityTcp.CustomTool";
        private const string BridgePackageId         = "cn.tuanjie.codely.bridge";

        // SessionState key: persists across domain reloads within the same Unity session.
        // Prevents RequestScriptCompilation() from being called more than once per session.
        private const string RecompileAttemptedKey = "TJGenerators.DiagnosticRecompileAttempted";

        static TJGeneratorsCustomToolDiagnostic()
        {
            // Double delayCall：等 CompilationPipeline 的错误列表也稳定下来再检查
            EditorApplication.delayCall += () =>
                EditorApplication.delayCall += CheckCustomToolAssembly;
        }

        private static void CheckCustomToolAssembly()
        {
            if (IsCustomToolAssemblyLoaded())
                return;

            Debug.LogWarning(
                $"[TJGenerators] {CustomToolAssemblyName} 程序集未加载，" +
                $"Codely AI 生成工具不可用。\n" +
                $"可能原因：{BridgePackageId}（Codely Bridge）未安装，或与当前版本不兼容导致编译失败。\n" +
                $"修复步骤：\n" +
                $"  1. 打开 Window > Package Manager，搜索 Codely Bridge 并安装。\n" +
                $"  2. 若使用 Codely CLI，请重启 CLI 后重新连接 Unity，CLI 将自动检测并安装所需包。\n" +
                $"  3. 或手动在 Packages/manifest.json 中添加：\n" +
                $"       \"{BridgePackageId}\": \"<version>\"");

            TryRequestBridgePackage();
        }

        private static bool IsCustomToolAssemblyLoaded()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(asm.GetName().Name, CustomToolAssemblyName,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 若 Bridge 包未安装，尝试通过 PackageManager 触发安装。
        /// 失败静默（不阻断用户工作流）。
        /// </summary>
        private static void TryRequestBridgePackage()
        {
            try
            {
                // 先用离线模式快速查询已安装包列表
                var listReq = UnityEditor.PackageManager.Client.List(offlineMode: true);
                void OnUpdate()
                {
                    if (!listReq.IsCompleted) return;
                    EditorApplication.update -= OnUpdate;

                    if (listReq.Status != UnityEditor.PackageManager.StatusCode.Success)
                        return;

                    bool bridgeInstalled = false;
                    foreach (var pkg in listReq.Result)
                    {
                        if (pkg.name == BridgePackageId)
                        {
                            bridgeInstalled = true;
                            break;
                        }
                    }

                    if (!bridgeInstalled)
                    {
                        // Bridge 确实未安装；提示而非静默安装（Bridge 版本由 CLI 管理）
                        Debug.LogWarning(
                            $"[TJGenerators] 检测到 {BridgePackageId} 未在本项目中安装。\n" +
                            $"Codely CLI 连接 Unity 时会自动安装此包；" +
                            $"若已连接 CLI 但工具仍不可用，请尝试断开后重新连接。");
                    }
                    else
                    {
                        // Bridge 已装但程序集仍未加载 → 尝试触发一次重新编译。
                        // 用 SessionState 保证整个编辑器会话内最多只触发一次，
                        // 避免问题未解决时 domain reload 循环往复。
                        if (SessionState.GetBool(RecompileAttemptedKey, false))
                        {
                            Debug.LogWarning(
                                $"[TJGenerators] {BridgePackageId} 已安装但 {CustomToolAssemblyName} 仍未加载，" +
                                $"且本次会话中已尝试过重新编译。\n" +
                                $"请检查 Console 中是否有 {BridgePackageId} 相关的编译错误，" +
                                $"或在 Package Manager 中重新安装该包后手动重新编译。");
                        }
                        else
                        {
                            SessionState.SetBool(RecompileAttemptedKey, true);
                            Debug.LogWarning(
                                $"[TJGenerators] {BridgePackageId} 已安装但 {CustomToolAssemblyName} 仍未加载，" +
                                $"正在触发重新编译…");
                            CompilationPipeline.RequestScriptCompilation();
                        }
                    }
                }
                EditorApplication.update += OnUpdate;
            }
            catch (Exception ex)
            {
                // 诊断本身不应阻断用户
                Debug.LogWarning($"[TJGenerators] 自动诊断失败（非致命）：{ex.Message}");
            }
        }
    }
}
#endif
