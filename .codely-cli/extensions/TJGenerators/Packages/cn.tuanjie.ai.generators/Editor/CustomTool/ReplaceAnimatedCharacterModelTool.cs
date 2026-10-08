using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;

#if UNITY_EDITOR
using TJGenerators;
using TJGenerators.Pipeline;
using TJGenerators.Utils;
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Shared helpers for assigning AnimatorController / Avatar on animated character prefabs
    /// and configuring Humanoid FBX import settings.
    /// </summary>
    public static class ReplaceAnimatedCharacterModelTool
    {
#if UNITY_EDITOR
        /// <summary>
        /// Creates a Capsule placeholder prefab (used by animated character / rigging tools).
        /// </summary>
        internal static string CreateBlankPrefab(string path)
        {
            path = Path.ChangeExtension(path, ".prefab").Replace("\\", "/");

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var root = new GameObject(Path.GetFileNameWithoutExtension(path));
            try
            {
                var placeholder = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                placeholder.name = "Placeholder";
                placeholder.transform.SetParent(root.transform);
                placeholder.transform.localPosition = Vector3.zero;
                placeholder.transform.localRotation = Quaternion.identity;
                placeholder.transform.localScale = Vector3.one;

                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));

            return path;
        }

        /// <summary>
        /// Configure a FBX file's ModelImporter so it is imported as a Humanoid rig.
        /// When it is the main model and separate animation files exist, animation import is
        /// disabled on the main model (animations live in the dedicated files).
        /// </summary>
        internal static void ConfigureFbxImport(string path, bool isMainModel, bool hasAnimationFiles = false)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) return;
            importer.animationType = ModelImporterAnimationType.Human;
            if (isMainModel && hasAnimationFiles)
                importer.importAnimation = false;
            importer.isReadable = true;
            AssetDatabase.WriteImportSettingsIfDirty(path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        /// <summary>
        /// Assigns the AnimatorController (named {baseName}_Controller.controller in the model
        /// directory) to the prefab's root Animator, but only when the Animator has no controller.
        /// Also sets Avatar from the model Animator when one is present.
        /// </summary>
        internal static void AssignAnimatorControllerIfMissing(string prefabPath, string modelPath)
        {
            if (string.IsNullOrEmpty(prefabPath) || string.IsNullOrEmpty(modelPath)) return;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return;

            string dir = Path.GetDirectoryName(modelPath);
            string baseName = Path.GetFileNameWithoutExtension(modelPath);
            string ctrlPath = Path.Combine(dir, baseName + "_Controller.controller").Replace("\\", "/");
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ctrlPath);
            if (controller == null && !string.IsNullOrEmpty(dir))
            {
                string[] guids = AssetDatabase.FindAssets("t:AnimatorController", new[] { dir.Replace("\\", "/") });
                foreach (var g in guids)
                {
                    string candidatePath = AssetDatabase.GUIDToAssetPath(g);
                    if (!string.IsNullOrEmpty(candidatePath) &&
                        Path.GetFileNameWithoutExtension(candidatePath).Contains(baseName))
                    {
                        controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(candidatePath);
                        if (controller != null)
                        {
                            ctrlPath = candidatePath;
                            break;
                        }
                    }
                }
            }

            var existingAnim = prefab.GetComponent<Animator>();
            bool hasController = existingAnim != null && existingAnim.runtimeAnimatorController != null;
            bool hasAvatar = existingAnim != null && existingAnim.avatar != null;
            if (controller == null && hasController && hasAvatar) return;

            var modelGO = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var modelAnim = modelGO?.GetComponent<Animator>();
            var avatar = modelAnim?.avatar;
            if (avatar == null)
            {
                var subAssets = AssetDatabase.LoadAllAssetsAtPath(modelPath);
                avatar = subAssets?.OfType<Avatar>().FirstOrDefault(a => a != null && a.isValid);
            }

            string prefabAssetPath = prefabPath.Replace("\\", "/");
            using (var scope = new PrefabContentsEditScope(prefabAssetPath))
            {
                var root = scope.prefabContentsRoot;
                var animator = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
                if (controller != null && animator.runtimeAnimatorController == null)
                    animator.runtimeAnimatorController = controller;
                if (avatar != null && animator.avatar == null)
                    animator.avatar = avatar;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(prefabAssetPath, ImportAssetOptions.ForceUpdate);

            var sceneAnimators = UnityObjectCompat.FindObjectsOfTypeIncludingInactive<Animator>();
            foreach (var anim in sceneAnimators)
            {
                if (anim == null) continue;
                string sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(anim.gameObject);
                if (!string.Equals(sourcePath, prefabAssetPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool changed = false;
                if (controller != null && anim.runtimeAnimatorController == null)
                {
                    anim.runtimeAnimatorController = controller;
                    changed = true;
                }
                if (avatar != null && anim.avatar == null)
                {
                    anim.avatar = avatar;
                    changed = true;
                }

                if (changed)
                {
                    EditorUtility.SetDirty(anim);
                    TJLog.Log($"[ReplaceAnimatedCharacterModelTool] Synced Animator on scene instance '{anim.gameObject.name}'.");
                }
            }

            TJLog.Log($"[ReplaceAnimatedCharacterModelTool] AnimatorController assigned: {ctrlPath}");
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>
    /// Lightweight IGenerationPipelineHost for headless model replacement in animated character prefabs.
    /// </summary>
    internal class AnimatedCharacterReplaceHost : HeadlessPipelineHostBase
    {
        private readonly string _prefabPath;

        public AnimatedCharacterReplaceHost(string prefabPath) => _prefabPath = prefabPath;

        protected override string DialogLogTag => "ReplaceAnimatedCharacterModelTool";

        public override TJGeneratorsAssetReference GetTargetAsset() => TJGeneratorsAssetReference.FromPath(_prefabPath);
    }
#endif
}
