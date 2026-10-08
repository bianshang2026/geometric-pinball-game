#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;
using TJGenerators.Pipeline;
using TJGenerators.Utils;

namespace TJGenerators.PostProcessing
{
    /// <summary>
    /// 绿幕视频后处理：创建 ChromaKey 材质，配合 VideoPlayer + RenderTexture 实时抠像。
    /// 不抽帧，不转码——保持原视频不变，播放时通过 shader 实时绿幕抠除。
    /// </summary>
    public static class GreenScreenVideoPostProcess
    {
        private const string ChromaKeyShaderName = "TJGenerators/ChromaKey";
        private const float DefaultSpillRemoval = 0.7f;

        /// <summary>
        /// 为绿幕视频创建 ChromaKey 材质（输出到视频同目录）。
        /// </summary>
        public static PostProcessResult EnsureChromaKeyMaterial(string videoAssetPath)
        {
            string outputFolder = Path.GetDirectoryName(videoAssetPath)?.Replace('\\', '/');
            return ProcessVideo(videoAssetPath, outputFolder);
        }

        /// <summary>
        /// 为绿幕视频创建 ChromaKey 材质。
        /// 使用方式：VideoPlayer.renderMode = APIOnly/RenderTexture → 材质赋给 Quad → shader 实时抠除绿色背景。
        /// </summary>
        public static PostProcessResult ProcessVideo(string videoAssetPath, string outputFolder)
        {
            var result = new PostProcessResult();

            if (string.IsNullOrEmpty(videoAssetPath))
            {
                result.Error = "Video asset path is empty";
                return result;
            }

            string videoAbsPath = PathUtils.ToAbsoluteAssetPath(videoAssetPath);
            if (!File.Exists(videoAbsPath))
            {
                result.Error = $"Video file not found: {videoAbsPath}";
                return result;
            }

            PathUtils.EnsureAssetFolder(outputFolder);

            // 查找 ChromaKey shader
            var shader = Shader.Find(ChromaKeyShaderName);
            if (shader == null)
            {
                // Fallback: 通过 AssetDatabase 查找
                string[] guids = AssetDatabase.FindAssets("ChromaKey t:Shader");
                if (guids.Length > 0)
                {
                    string shaderPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                    shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
                }
            }

            if (shader == null)
            {
                result.Error = $"ChromaKey shader '{ChromaKeyShaderName}' not found";
                return result;
            }

            // 创建材质
            string effectName = Path.GetFileNameWithoutExtension(videoAssetPath);
            string materialPath = $"{outputFolder}/{effectName}_ChromaKey.mat";
            materialPath = AssetDatabase.GenerateUniqueAssetPath(materialPath);

            var mat = new Material(shader);
            mat.name = effectName + "_ChromaKey";

            // 默认参数（与 SpriteSequencePostProcess 绿幕抠图默认值对齐）
            mat.SetFloat("_ChromaTolerance", SpriteSequencePostProcess.DefaultChromaTolerance);
            mat.SetFloat("_ChromaFeather", SpriteSequencePostProcess.DefaultChromaFeather);
            mat.SetFloat("_SpillRemoval", DefaultSpillRemoval);

            AssetDatabase.CreateAsset(mat, materialPath);
            AssetDatabase.SaveAssets();

            TJLog.Log($"[GreenScreenPostProcess] ChromaKey material created: {materialPath}");

            result.Success = true;
            result.MaterialPath = materialPath;
            result.VideoPath = videoAssetPath;
            result.ShaderName = ChromaKeyShaderName;

            return result;
        }

        /// <summary>
        /// 在场景中创建特效视频播放 GameObject：Quad + VideoPlayer + EffectVideoController + ChromaKey。
        /// materialPath 为空时自动 EnsureChromaKeyMaterial。
        /// </summary>
        /// <returns>创建的 GameObject；失败返回 null。</returns>
        public static GameObject SetupEffectVideoInScene(string videoPath, string materialPath = null)
        {
            try
            {
                var clip = AssetDatabase.LoadAssetAtPath<VideoClip>(videoPath);
                if (clip == null)
                {
                    TJLog.LogWarning($"[GreenScreenPostProcess] Cannot setup effect video: clip not found at {videoPath}");
                    return null;
                }

                if (string.IsNullOrEmpty(materialPath))
                {
                    var result = EnsureChromaKeyMaterial(videoPath);
                    if (!result.Success)
                    {
                        TJLog.LogWarning($"[GreenScreenPostProcess] ChromaKey material creation failed: {result.Error}");
                        return null;
                    }
                    materialPath = result.MaterialPath;
                }

                var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (mat == null)
                {
                    TJLog.LogWarning($"[GreenScreenPostProcess] Cannot setup effect video: material not found at {materialPath}");
                    return null;
                }

                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "EffectVideo_" + Path.GetFileNameWithoutExtension(videoPath);
                go.transform.position = new Vector3(0, 1, 0);

                int w = (int)clip.width;
                int h = (int)clip.height;
                if (w > 0 && h > 0)
                {
                    float aspect = (float)w / h;
                    go.transform.localScale = new Vector3(aspect, 1, 1);
                }

                var renderer = go.GetComponent<Renderer>();
                renderer.sharedMaterial = mat;

                var player = go.AddComponent<VideoPlayer>();
                player.clip = clip;
                player.renderMode = VideoRenderMode.RenderTexture;
                player.isLooping = true;
                player.playOnAwake = true;
                player.audioOutputMode = VideoAudioOutputMode.None;

                // EffectVideoController manages RT lifecycle across domain reloads
                var controller = go.AddComponent<EffectVideoController>();
                controller.Initialize(mat);

                var cam = Camera.main;
                if (cam == null) cam = UnityObjectCompat.FindObjectOfType<Camera>();
                if (cam != null)
                {
                    cam.transform.position = new Vector3(0, 1, -3);
                    cam.transform.LookAt(go.transform);
                    cam.fieldOfView = 60f;
                }

                EditorUtility.SetDirty(go);
                Undo.RegisterCreatedObjectUndo(go, "Create Effect Video Player");
                Selection.activeGameObject = go;
                SceneView.lastActiveSceneView?.Frame(new Bounds(go.transform.position, Vector3.one * 2), false);

                TJLog.Log($"[GreenScreenPostProcess] Effect video GameObject created: {go.name}");
                return go;
            }
            catch (Exception e)
            {
                TJLog.LogWarning($"[GreenScreenPostProcess] SetupEffectVideoInScene error: {e.Message}");
                return null;
            }
        }

        public struct PostProcessResult
        {
            public bool Success;
            public string Error;
            public string VideoPath;
            public string MaterialPath;
            public string ShaderName;
        }
    }
}
#endif
