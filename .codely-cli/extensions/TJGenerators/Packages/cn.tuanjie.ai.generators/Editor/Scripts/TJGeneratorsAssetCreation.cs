using System;
using System.IO;
using UnityEditor;
using UnityEditor.ProjectWindowCallback;
using UnityEngine;
using UnityEngine.Video;
using TJGenerators.Utils;

namespace TJGenerators
{
    /// <summary>
    /// 在 Project 中创建占位资产并打开对应生成窗口。
    /// </summary>
    internal static class TJGeneratorsAssetCreation
    {
        internal const string DefaultNewAssetName = "New Mesh";

        internal static void CreateSkyboxAssetWithCallback(string defaultName)
        {
            var icon = EditorGUIUtility.ObjectContent(null, typeof(Cubemap))?.image as Texture2D;
            var doCreate = ScriptableObject.CreateInstance<DoCreateTJGeneratorsAsset>();
            doCreate.action = (path, resourceFile) =>
            {
                path = AssetDatabase.GenerateUniqueAssetPath(path);
                path = TJGeneratorsSkyboxWindow.CreateBlankSkybox(path);
                if (string.IsNullOrEmpty(path))
                    return;

                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
                if (cubemap != null)
                {
                    Selection.activeObject = cubemap;
                    EditorGUIUtility.PingObject(cubemap);
                }

                TJGeneratorsSkyboxWindow.OpenForAsset(path);
            };

            StartNameEditingIfProjectWindowExists(doCreate, defaultName, icon, null);
        }

        internal static void CreateWorldAssetWithCallback(string defaultName)
        {
            var icon = EditorGUIUtility.ObjectContent(null, typeof(Texture2D))?.image as Texture2D;
            var doCreate = ScriptableObject.CreateInstance<DoCreateTJGeneratorsAsset>();
            doCreate.action = (path, resourceFile) =>
            {
                path = AssetDatabase.GenerateUniqueAssetPath(path);
                path = TJGeneratorsWorldWindow.CreateBlankWorldPreview(path);
                if (string.IsNullOrEmpty(path))
                    return;

                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture != null)
                {
                    Selection.activeObject = texture;
                    EditorGUIUtility.PingObject(texture);
                }

                EditorApplication.delayCall += () => TJGeneratorsWorldWindow.OpenForAsset(path);
            };

            StartNameEditingIfProjectWindowExists(doCreate, defaultName, icon, null);
        }

        internal static void CreateSpriteAssetWithCallback(string defaultName)
        {
            var icon = EditorGUIUtility.ObjectContent(null, typeof(Texture2D))?.image as Texture2D;
            var doCreate = ScriptableObject.CreateInstance<DoCreateTJGeneratorsAsset>();
            doCreate.action = (path, resourceFile) =>
            {
                path = AssetDatabase.GenerateUniqueAssetPath(path);
                path = TJGeneratorsSpriteWindow.CreateBlankSprite(path);
                if (string.IsNullOrEmpty(path))
                    return;

                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture != null)
                {
                    Selection.activeObject = texture;
                    EditorGUIUtility.PingObject(texture);
                }

                EditorApplication.delayCall += () => TJGeneratorsSpriteWindow.OpenForAsset(path);
            };

            StartNameEditingIfProjectWindowExists(doCreate, defaultName, icon, null);
        }

        internal static void CreateImageAssetWithCallback(string defaultName, bool openAsSpriteSheetSequence = false)
        {
            string folder = PathUtils.GetProjectBrowserInsertionFolderAssetPath();
            if (string.IsNullOrWhiteSpace(defaultName))
                defaultName = "New Image.jpg";
            string fileName = Path.GetFileName(defaultName.Trim().Replace('\\', '/'));
            if (string.IsNullOrEmpty(fileName))
                fileName = "New Image.jpg";

            string defaultNameForDialog = Path.GetFileName(
                TJGeneratorsImageAssetPathUtility.GenerateUniqueImagePath($"{folder}/{fileName}")
            );

            var icon = EditorGUIUtility.ObjectContent(null, typeof(Texture2D))?.image as Texture2D;
            var doCreate = ScriptableObject.CreateInstance<DoCreateTJGeneratorsAsset>();
            doCreate.action = (path, resourceFile) =>
            {
                path = TJGeneratorsImageAssetPathUtility.GenerateUniqueImagePath(path);
                path = TJGeneratorsImageWindow.CreateBlankImage(path);
                if (string.IsNullOrEmpty(path))
                    return;

                if (openAsSpriteSheetSequence)
                {
                    var spriteSheetAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    TJGeneratorsGenerationLabel.EnableSpriteSheetLabel(spriteSheetAsset);
                }

                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture != null)
                {
                    Selection.activeObject = texture;
                    EditorGUIUtility.PingObject(texture);
                }

                EditorApplication.delayCall += () =>
                {
                    if (openAsSpriteSheetSequence)
                        TJGeneratorsSpriteSheetSequenceWindow.OpenForAsset(path);
                    else
                        TJGeneratorsImageWindow.OpenForAsset(path);
                };
            };

            StartNameEditingIfProjectWindowExists(doCreate, defaultNameForDialog, icon, null);
        }

        internal static void CreateMaterialAssetWithCallback(string defaultName)
        {
            var icon = EditorGUIUtility.ObjectContent(null, typeof(Material))?.image as Texture2D;
            var doCreate = ScriptableObject.CreateInstance<DoCreateTJGeneratorsAsset>();
            doCreate.action = (path, resourceFile) =>
            {
                path = AssetDatabase.GenerateUniqueAssetPath(path);

                var surfaceShader = TJMaterialShaderUtility.ResolveSurfaceLitShader()
                                    ?? Shader.Find("Unlit/Texture");
                Material material = new Material(surfaceShader);
                AssetDatabase.CreateAsset(material, path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

                var loadedMaterial = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (loadedMaterial != null)
                {
                    Selection.activeObject = loadedMaterial;
                    EditorGUIUtility.PingObject(loadedMaterial);
                }

                EditorApplication.delayCall += () => TJGeneratorsMaterialWindow.OpenForMaterialAsset(path);
            };

            StartNameEditingIfProjectWindowExists(doCreate, defaultName, icon, null);
        }

        internal static void CreateVideoAssetWithCallback(string defaultName)
        {
            string folder = PathUtils.GetProjectBrowserInsertionFolderAssetPath();
            if (string.IsNullOrWhiteSpace(defaultName))
                defaultName = "New Video.mp4";
            string fileName = Path.GetFileName(defaultName.Trim().Replace('\\', '/'));
            if (string.IsNullOrEmpty(fileName))
                fileName = "New Video.mp4";

            string defaultNameForDialog = Path.GetFileName(
                AssetDatabase.GenerateUniqueAssetPath($"{folder}/{fileName}"));

            var icon = EditorGUIUtility.ObjectContent(null, typeof(VideoClip))?.image as Texture2D;
            var doCreate = ScriptableObject.CreateInstance<DoCreateTJGeneratorsAsset>();
            doCreate.action = (path, resourceFile) =>
            {
                path = AssetDatabase.GenerateUniqueAssetPath(path);
                path = TJGeneratorsVideoUtils.CreateBlankVideoClip(path);
                if (string.IsNullOrEmpty(path))
                    return;

                TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));

                var clip = AssetDatabase.LoadAssetAtPath<VideoClip>(path);
                if (clip != null)
                {
                    Selection.activeObject = clip;
                    EditorGUIUtility.PingObject(clip);
                }

                EditorApplication.delayCall += () => TJGeneratorsVideoWindow.OpenForAsset(path);
            };

            StartNameEditingIfProjectWindowExists(doCreate, defaultNameForDialog, icon, null);
        }

        internal static void CreateAudioClipAssetWithCallback(string defaultName)
        {
            string folder = PathUtils.GetProjectBrowserInsertionFolderAssetPath();
            if (string.IsNullOrWhiteSpace(defaultName))
                defaultName = "New AudioClip.wav";
            string fileName = Path.GetFileName(defaultName.Trim().Replace('\\', '/'));
            if (string.IsNullOrEmpty(fileName))
                fileName = "New AudioClip.wav";
            string defaultNameForDialog = Path.GetFileName(
                TJGeneratorsAudioAssetPathUtility.GenerateUniquePlaceholderWavPath($"{folder}/{fileName}"));

            var icon = EditorGUIUtility.ObjectContent(null, typeof(UnityEngine.AudioClip))?.image as Texture2D;
            var doCreate = ScriptableObject.CreateInstance<DoCreateTJGeneratorsAsset>();
            doCreate.action = (path, resourceFile) =>
            {
                path = TJGeneratorsAudioAssetPathUtility.GenerateUniquePlaceholderWavPath(path);
                path = TJGeneratorsAudioUtils.CreateBlankAudioClip(path);
                if (string.IsNullOrEmpty(path))
                    return;
                var clip = AssetDatabase.LoadAssetAtPath<UnityEngine.AudioClip>(path);
                if (clip != null)
                {
                    Selection.activeObject = clip;
                    EditorGUIUtility.PingObject(clip);
                }

                EditorApplication.delayCall += () => TJGeneratorsMusicWindow.OpenForAsset(path);
            };

            StartNameEditingIfProjectWindowExists(doCreate, defaultNameForDialog, icon, null);
        }

        internal static void CreateAnimationClipAssetWithCallback(string defaultName)
        {
            string folder = PathUtils.GetProjectBrowserInsertionFolderAssetPath();
            if (string.IsNullOrWhiteSpace(defaultName))
                defaultName = "New Sprite Sequence.anim";
            string fileName = Path.GetFileName(defaultName.Trim().Replace('\\', '/'));
            if (string.IsNullOrEmpty(fileName))
                fileName = "New Sprite Sequence.anim";

            string defaultNameForDialog = Path.GetFileName(
                AssetDatabase.GenerateUniqueAssetPath($"{folder}/{fileName}")
            );

            var icon = EditorGUIUtility.ObjectContent(null, typeof(AnimationClip))?.image as Texture2D;
            var doCreate = ScriptableObject.CreateInstance<DoCreateTJGeneratorsAsset>();
            doCreate.action = (path, resourceFile) =>
            {
                path = AssetDatabase.GenerateUniqueAssetPath(path);

                var clip = new AnimationClip();
                AssetDatabase.CreateAsset(clip, path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

                var loaded = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (loaded != null)
                {
                    Selection.activeObject = loaded;
                    EditorGUIUtility.PingObject(loaded);
                }

                EditorApplication.delayCall += () => TJGeneratorsSpriteSequenceWindow.OpenForAsset(path);
            };

            StartNameEditingIfProjectWindowExists(doCreate, defaultNameForDialog, icon, null);
        }

        internal static void CreatePrefabAssetWithCallback(string defaultName, bool enableLabel, GameObject sceneParentForInstance)
        {
            var icon = EditorGUIUtility.ObjectContent(null, typeof(GameObject))?.image as Texture2D;
            var doCreate = ScriptableObject.CreateInstance<DoCreateTJGeneratorsAsset>();
            var capturedParent = sceneParentForInstance;
            doCreate.action = (path, resourceFile) =>
            {
                HandlePrefabCreation(path, enableLabel, capturedParent);
            };

            StartNameEditingIfProjectWindowExists(doCreate, defaultName, icon, null);
        }

        private static void HandlePrefabCreation(string path, bool enableLabel, GameObject sceneParentForInstance)
        {
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            path = CreateBlankPrefab(path);
            if (string.IsNullOrEmpty(path))
                return;

            if (enableLabel)
                TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                TJLog.LogError($"无法加载 Prefab: {path}");
                return;
            }

            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance != null)
            {
                if (sceneParentForInstance != null)
                    GameObjectUtility.SetParentAndAlign(instance, sceneParentForInstance);
                Undo.RegisterCreatedObjectUndo(instance, $"Create {instance.name}");
                Selection.activeObject = instance;
            }
            else
            {
                TJLog.LogError($"无法实例化 Prefab: {prefab.name}");
                return;
            }

            TJGenerators3DModelWindow.OpenForAsset(path);
        }

        private static string CreateBlankPrefab(string path)
        {
            path = Path.ChangeExtension(path, ".prefab");

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var rootGameObject = new GameObject("Generated Mesh");
            try
            {
                var placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
                placeholder.name = "Placeholder";
                placeholder.transform.SetParent(rootGameObject.transform);
                placeholder.transform.localPosition = Vector3.zero;
                placeholder.transform.localRotation = Quaternion.identity;
                placeholder.transform.localScale = Vector3.one;

                PrefabUtility.SaveAsPrefabAsset(rootGameObject, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rootGameObject);
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(path));

            return path;
        }

        private static void StartNameEditingIfProjectWindowExists(
            DoCreateTJGeneratorsAsset endAction,
            string pathName,
            Texture2D icon,
            string resourceFile)
        {
#if UNITY_6000_5_OR_NEWER
            ProjectWindowUtil.StartNameEditingIfProjectWindowExists(default, endAction, pathName, icon, resourceFile);
#else
            ProjectWindowUtil.StartNameEditingIfProjectWindowExists(0, endAction, pathName, icon, resourceFile);
#endif
        }
    }

    /// <summary>
    /// 用于 ProjectWindowUtil 的资产创建回调
    /// </summary>
    internal class DoCreateTJGeneratorsAsset
#if UNITY_6000_5_OR_NEWER
        : AssetCreationEndAction
#else
        : EndNameEditAction
#endif
    {
        public delegate void ActionHandler(string pathName, string resourceFile);
        public ActionHandler action { get; set; }

#if UNITY_6000_5_OR_NEWER
        public override void Action(EntityId entityId, string pathName, string resourceFile)
#else
        public override void Action(int instanceId, string pathName, string resourceFile)
#endif
        {
            action?.Invoke(pathName, resourceFile);
        }
    }
}
