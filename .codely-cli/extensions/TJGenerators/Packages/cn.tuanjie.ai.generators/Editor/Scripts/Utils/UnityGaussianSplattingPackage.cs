#if UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Unity.EditorCoroutines.Editor;

namespace TJGenerators.Utils
{
    /// <summary>
    /// UnityGaussianSplatting 依赖包检查与安装工具。
    /// 默认不安装，仅在用户点击「生成世界」菜单时检查并按需安装。
    /// 包位于 git 仓库的 package 子目录，使用 ?path=package 参数安装。
    /// 国内可选用 GitHub 代理（临时 git insteadOf 规则，安装后自动清除）。
    /// </summary>
    internal static class UnityGaussianSplattingPackage
    {
        private const string GitUrl = "https://github.com/Liuweixian/UnityGaussianSplatting.git?path=package";
        private const string ProxyPrefix = "https://gh-proxy.com/";
        private const string GitHubPrefix = "https://github.com/";

        /// <summary>
        /// 检查 UnityGaussianSplatting 包是否已安装（同步，仅 Unity 2021.2+）。
        /// 更早版本须通过 <see cref="EnsureInstalledThenCreateWorld"/> 内的协程查询 Package Manager。
        /// </summary>
        private static bool IsInstalled()
        {
#if !UNITY_2021_2_OR_NEWER
            return false;
#else
            try
            {
                return IsInstalledFromRegisteredPackages();
            }
            catch (Exception e)
            {
                TJLog.LogWarning($"[UnityGaussianSplatting] 检查包安装状态失败: {e.Message}");
            }
            return false;
#endif
        }

#if UNITY_2021_2_OR_NEWER
        private static bool IsInstalledFromRegisteredPackages()
        {
            var allPackages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            if (allPackages == null)
                return false;

            foreach (var pkg in allPackages)
            {
                if (MatchesGaussianSplattingPackage(pkg?.name) || MatchesGaussianSplattingPackage(pkg?.packageId))
                    return true;
            }
            return false;
        }
#endif

#if !UNITY_2021_2_OR_NEWER
        private static IEnumerator IsInstalledFromPackageListCoroutine(Action<bool> onComplete)
        {
            var listRequest = UnityEditor.PackageManager.Client.List(offlineMode: true);
            while (!listRequest.IsCompleted)
                yield return null;

            bool installed = false;
            if (listRequest.Status == UnityEditor.PackageManager.StatusCode.Success)
            {
                foreach (var pkg in listRequest.Result)
                {
                    if (MatchesGaussianSplattingPackage(pkg?.name) || MatchesGaussianSplattingPackage(pkg?.packageId))
                    {
                        installed = true;
                        break;
                    }
                }
            }

            onComplete?.Invoke(installed);
        }
#endif

        private static bool MatchesGaussianSplattingPackage(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf("gaussian-splatting", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 确保包已安装，然后创建世界资产生成窗口。
        /// </summary>
        public static void EnsureInstalledThenCreateWorld(string defaultAssetName)
        {
#if UNITY_2021_2_OR_NEWER
            if (IsInstalled())
            {
                TJGeneratorsAssetCreation.CreateWorldAssetWithCallback(defaultAssetName);
                return;
            }

            PromptInstallOrCancel(defaultAssetName);
#else
            EditorCoroutineUtility.StartCoroutineOwnerless(EnsureInstalledThenCreateWorldCoroutine(defaultAssetName));
#endif
        }

#if !UNITY_2021_2_OR_NEWER
        private static IEnumerator EnsureInstalledThenCreateWorldCoroutine(string defaultAssetName)
        {
            bool installed = false;
            yield return IsInstalledFromPackageListCoroutine(result => installed = result);

            if (installed)
            {
                TJGeneratorsAssetCreation.CreateWorldAssetWithCallback(defaultAssetName);
                yield break;
            }

            PromptInstallOrCancel(defaultAssetName);
        }
#endif

        private static void PromptInstallOrCancel(string defaultAssetName)
        {
            // 三按钮对话框：直接安装 / 使用代理 / 取消
            int choice = EditorUtility.DisplayDialogComplex(
                TJGeneratorsL10n.L("安装依赖包"),
                TJGeneratorsL10n.L(
                    "世界生成功能需要安装 UnityGaussianSplatting 依赖包。\n\n" +
                    "来源：https://github.com/Liuweixian/UnityGaussianSplatting.git\n" +
                    "子目录：package\n\n" +
                    "如果网络无法访问 GitHub，可选择「使用代理安装」。"),
                TJGeneratorsL10n.L("直接安装"),
                TJGeneratorsL10n.L("使用代理安装"),
                TJGeneratorsL10n.L("取消")
            );

            if (choice == 2) // 取消
                return;

            bool useProxy = (choice == 1);
            EditorCoroutineUtility.StartCoroutineOwnerless(InstallPackageCoroutine(defaultAssetName, useProxy));
        }

        private static IEnumerator InstallPackageCoroutine(string defaultAssetName, bool useProxy)
        {
            if (useProxy)
            {
                // 临时设置 git insteadOf 规则：将 github.com 替换为代理地址
                // Unity Package Manager 内部使用 git clone，此规则会自动生效
                TJLog.Log($"[UnityGaussianSplatting] 设置临时 git 代理: {ProxyPrefix}");
                RunGitCommand($"config --global url.\"{ProxyPrefix}{GitHubPrefix}\".insteadOf \"{GitHubPrefix}\"");
            }

            EditorUtility.DisplayProgressBar(
                TJGeneratorsL10n.L("安装依赖包"),
                TJGeneratorsL10n.L("正在安装 UnityGaussianSplatting...\n这可能需要几分钟，请耐心等待。"),
                0.1f
            );

            var addRequest = UnityEditor.PackageManager.Client.Add(GitUrl);

            while (!addRequest.IsCompleted)
            {
                if (addRequest.Status == UnityEditor.PackageManager.StatusCode.Failure)
                    break;
                EditorUtility.DisplayProgressBar(
                    TJGeneratorsL10n.L("安装依赖包"),
                    TJGeneratorsL10n.L("正在安装 UnityGaussianSplatting...\n这可能需要几分钟，请耐心等待。"),
                    0.5f
                );
                yield return null;
            }

            EditorUtility.ClearProgressBar();

            // 清除临时代理规则
            if (useProxy)
            {
                TJLog.Log("[UnityGaussianSplatting] 清除临时 git 代理规则");
                RunGitCommand("config --global --unset url.\"" + ProxyPrefix + GitHubPrefix + "\".insteadOf");
            }

            if (addRequest.Status == UnityEditor.PackageManager.StatusCode.Success)
            {
                TJLog.Log($"[UnityGaussianSplatting] 包安装成功: {addRequest.Result?.packageId}");
                PathUtils.SafeRefresh();
                EditorApplication.delayCall += () =>
                {
                    TJGeneratorsAssetCreation.CreateWorldAssetWithCallback(defaultAssetName);
                };
            }
            else
            {
                string error = addRequest.Error?.message ?? "Unknown error";
                TJLog.LogError($"[UnityGaussianSplatting] 包安装失败: {error}");
                ErrorDialogUtils.ShowErrorDialog(
                    TJGeneratorsL10n.L("安装失败"),
                    string.Format(TJGeneratorsL10n.L(
                        "UnityGaussianSplatting 安装失败：{0}\n\n" +
                        "你也可以手动克隆仓库后通过 Package Manager 从本地磁盘安装：\n" +
                        "1. git clone https://github.com/Liuweixian/UnityGaussianSplatting.git\n" +
                        "2. Window → Package Manager → + → Add package from disk\n" +
                        "3. 选择克隆下来的 package 目录中的 package.json"), error),
                    "[UnityGaussianSplatting]"
                );
            }
        }

        private static void RunGitCommand(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var process = Process.Start(psi))
                {
                    process?.WaitForExit();
                }
            }
            catch (Exception e)
            {
                TJLog.LogWarning($"[UnityGaussianSplatting] git 命令执行失败: {e.Message}");
            }
        }
    }
}
#endif
