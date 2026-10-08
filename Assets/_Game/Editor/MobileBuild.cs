using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeoBreaker.EditorTools
{
    /// <summary>
    /// 移动端打包工具（Phase 10）：Android 平台切换 + 开发版 APK 构建。
    /// 菜单：GeoBreaker/移动端/*。构建产物 Builds/GeoBreaker.apk（调试签名，可直接安装）。
    /// </summary>
    public static class MobileBuild
    {
        const string ApkPath = "Builds/GeoBreaker.apk";
        const string ScenePath = "Assets/Scenes/Main.scene";

        [MenuItem("GeoBreaker/移动端/检查 Android 模块")]
        public static void CheckSupport()
        {
            bool supported = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);
            Debug.Log($"[MobileBuild] Android 模块已安装: {supported}；当前平台: {EditorUserBuildSettings.activeBuildTarget}；" +
                      $"脚本后端: {PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android)}");
        }

        [MenuItem("GeoBreaker/移动端/切换到 Android")]
        public static void SwitchAndroid()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android) return;
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                Debug.LogError("[MobileBuild] 缺少 Android Build Support 模块，请用 Tuanjie Hub/CLI 安装后再试。");
                return;
            }
            ApplyPortraitSettings();
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        }

        [MenuItem("GeoBreaker/移动端/切回 Windows")]
        public static void SwitchStandalone()
            => EditorUserBuildSettings.SwitchActiveBuildTarget(
                BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);

        [MenuItem("GeoBreaker/移动端/构建 Android APK")]
        public static void BuildAndroid()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                Debug.LogError("[MobileBuild] 缺少 Android Build Support 模块，无法打包。先运行「检查 Android 模块」。");
                return;
            }

            ApplyPortraitSettings();
            EditorUserBuildSettings.development = true;          // 开发构建（调试签名，免手动配置 keystore）

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                Debug.Log("[MobileBuild] 切换到 Android 平台…");
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
                {
                    Debug.LogError("[MobileBuild] 平台切换失败。");
                    return;
                }
            }

            Directory.CreateDirectory("Builds");
            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.Development,
            };
            var report = BuildPipeline.BuildPlayer(opts);
            var summary = report.summary;
            string result = summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded
                ? $"成功：{Path.GetFullPath(ApkPath)}（{summary.totalSize / 1024 / 1024} MB）"
                : $"失败：{summary.result}（错误 {summary.totalErrors} 个）";
            Debug.Log($"[MobileBuild] APK 构建{result}");
        }

        /// <summary>竖屏锁定（本项目相机按 9:16 竖屏正交构建）。</summary>
        static void ApplyPortraitSettings()
        {
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
        }
    }
}
