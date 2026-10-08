using System.Collections.Generic;
using TJGenerators.Utils;
using UnityEditor;
using UnityEngine;

namespace TJGenerators
{
    /// <summary>
    /// TJGenerators 生成标签管理
    /// </summary>
    public static class TJGeneratorsGenerationLabel
    {
        public const string Label = "TuanjieAI";
        public const string SpriteSheetLabel = "TuanjieAI_Frontier";
        public const string SessionLabelPrefix = "Session_";

        private const string BootstrapAssetPath = "Packages/cn.tuanjie.ai.generators/package.json";

        private static bool s_labelsBootstrapped;

        [InitializeOnLoadMethod]
        private static void ScheduleLabelBootstrap()
        {
            EditorApplication.delayCall += BootstrapProjectLabelsOnce;
        }

        private static void BootstrapProjectLabelsOnce()
        {
            if (s_labelsBootstrapped)
                return;

            var bootstrapAsset = AssetDatabase.LoadAssetAtPath<Object>(BootstrapAssetPath);
            if (bootstrapAsset == null)
                return;

            if (TryApplyLabels(bootstrapAsset, Label, SpriteSheetLabel))
                s_labelsBootstrapped = true;
        }

        /// <summary>
        /// 将 <paramref name="labelsToEnsure"/> 中尚不存在的标签写入资产。
        /// </summary>
        /// <returns>所需标签已全部存在，或 SetLabels 成功时为 true。</returns>
        private static bool ApplyMissingLabels(Object asset, string errorContext, params string[] labelsToEnsure)
        {
            if (asset == null || labelsToEnsure == null || labelsToEnsure.Length == 0)
                return false;

            var labels = new List<string>(AssetDatabase.GetLabels(asset));
            bool changed = false;
            foreach (var label in labelsToEnsure)
            {
                if (string.IsNullOrEmpty(label) || labels.Contains(label))
                    continue;

                labels.Add(label);
                changed = true;
            }

            if (!changed)
                return true;

            try
            {
                AssetDatabase.SetLabels(asset, labels.ToArray());
                return true;
            }
            catch (System.Exception ex)
            {
                TJLog.LogError($"SetLabels failed for '{errorContext}': {ex.Message}");
                return false;
            }
        }

        private static bool TryApplyLabels(Object asset, params string[] labelsToEnsure)
        {
            return ApplyMissingLabels(asset, BootstrapAssetPath, labelsToEnsure);
        }

        private static void AddLabels(Object asset, params string[] labelsToAdd)
        {
            if (asset == null || labelsToAdd == null || labelsToAdd.Length == 0)
                return;

            BootstrapProjectLabelsOnce();
            ApplyMissingLabels(asset, asset.name, labelsToAdd);
        }

        /// <summary>
        /// 为资产添加生成标签
        /// </summary>
        public static void EnableLabel(Object asset)
        {
            if (asset == null)
                return;

            AddLabels(asset, Label);
        }

        /// <summary>
        /// 为资产引用添加生成标签
        /// </summary>
        public static void EnableLabel(TJGeneratorsAssetReference assetRef)
        {
            if (assetRef == null || !assetRef.IsValid())
                return;

            var asset = AssetDatabase.LoadAssetAtPath<Object>(assetRef.GetPath());
            EnableLabel(asset);
        }

        /// <summary>
        /// 为资产追加 session 标签（sessionId 为空时直接返回）
        /// </summary>
        public static void EnableSessionLabel(Object asset, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || asset == null)
                return;

            AddLabels(asset, SessionLabelPrefix + sessionId);
        }

        /// <summary>
        /// 为资产引用追加 session 标签（sessionId 为空时直接返回）
        /// </summary>
        public static void EnableSessionLabel(TJGeneratorsAssetReference assetRef, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || assetRef == null || !assetRef.IsValid())
                return;

            var asset = AssetDatabase.LoadAssetAtPath<Object>(assetRef.GetPath());
            EnableSessionLabel(asset, sessionId);
        }

        private static bool AssetHasLabel(Object asset, string targetLabel)
        {
            if (asset == null)
                return false;

            var labels = AssetDatabase.GetLabels(asset);
            foreach (var label in labels)
            {
                if (label == targetLabel)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 检查资产是否有生成标签
        /// </summary>
        public static bool HasLabel(Object asset) => AssetHasLabel(asset, Label);

        /// <summary>
        /// 为资产添加精灵表序列帧标签
        /// </summary>
        public static void EnableSpriteSheetLabel(Object asset)
        {
            if (asset == null)
                return;

            AddLabels(asset, SpriteSheetLabel, Label);
        }

        /// <summary>
        /// 检查资产是否有精灵表序列帧标签
        /// </summary>
        public static bool HasSpriteSheetLabel(Object asset) => AssetHasLabel(asset, SpriteSheetLabel);
    }
}
