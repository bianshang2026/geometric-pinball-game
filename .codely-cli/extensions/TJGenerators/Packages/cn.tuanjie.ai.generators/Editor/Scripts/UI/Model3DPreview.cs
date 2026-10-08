#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using TJGenerators.Utils;

namespace TJGenerators.UI
{
    /// <summary>
    /// 3D 模型预览组件 - 支持拖拽旋转、滚轮缩放、双击重置
    /// </summary>
    public class Model3DPreview : IDisposable
    {
        private const float DefaultYaw = -135f;
        private const float DefaultPitch = 25f;

        private PreviewRenderUtility _previewRenderUtility;
        private GameObject _previewInstance;
        private string _currentModelPath;
        private float _yaw = DefaultYaw;
        private float _pitch = DefaultPitch;
        private float _zoom = 1f;
        private static Material _fallbackMaterial;

        /// <summary>
        /// 绘制 3D 预览
        /// </summary>
        /// <param name="modelPath">模型资源路径</param>
        /// <param name="maxWidth">最大宽度</param>
        /// <param name="modelRotationEuler">模型基础旋转（欧拉角），用于与生成结果一致的朝向</param>
        /// <param name="repaintCallback">需要重绘时调用的回调</param>
        public void Draw(string modelPath, float maxWidth, Vector3 modelRotationEuler, Action repaintCallback)
        {
            if (_previewRenderUtility == null)
            {
                _previewRenderUtility = new PreviewRenderUtility();
                _previewRenderUtility.cameraFieldOfView = 30f;
            }

            if (_currentModelPath != modelPath)
            {
                if (_previewInstance != null)
                {
                    UnityEngine.Object.DestroyImmediate(_previewInstance);
                    _previewInstance = null;
                }

                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (asset != null)
                {
                    _previewInstance = _previewRenderUtility.InstantiatePrefabInScene(asset);
                    TryFixPinkMaterials(_previewInstance);
                    _currentModelPath = modelPath;
                    _yaw = DefaultYaw;
                    _pitch = DefaultPitch;
                    _zoom = 1f;
                }
            }

            if (_previewInstance == null) return;

            float previewWidth = Mathf.Min(maxWidth, 300f);
            float previewHeight = previewWidth * 0.7f;
            Rect previewRect = GUILayoutUtility.GetRect(previewWidth, previewHeight);

            EditorGUI.DrawRect(previewRect, new Color(0.15f, 0.15f, 0.15f, 1f));

            HandleInput(previewRect, repaintCallback);

            // 模型只应用配置朝向；交互旋转改为相机绕模型轨道运动，避免转到背面后俯仰方向反转
            _previewInstance.transform.rotation = Quaternion.Euler(modelRotationEuler);

            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            var renderers = _previewInstance.GetComponentsInChildren<Renderer>();
            bool hasBounds = false;
            foreach (var renderer in renderers)
            {
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds) return;

            float boundsSize = bounds.size.magnitude;
            float distance = boundsSize * 2f / _zoom;
            Vector3 cameraOffset = Quaternion.Euler(_pitch, _yaw, 0f) * new Vector3(0f, 0f, -distance);
            _previewRenderUtility.camera.transform.position = bounds.center + cameraOffset;
            _previewRenderUtility.camera.transform.LookAt(bounds.center);
            _previewRenderUtility.camera.nearClipPlane = 0.001f;
            _previewRenderUtility.camera.farClipPlane = distance * 10f;

            bool hasTexture = CheckModelHasTexture(renderers);

            _previewRenderUtility.lights[0].intensity = hasTexture ? 1.35f : 0.8f;
            _previewRenderUtility.lights[0].transform.rotation = Quaternion.Euler(50f, 50f, 0f);
            _previewRenderUtility.lights[0].color = Color.white;

            if (_previewRenderUtility.lights.Length > 1)
            {
                _previewRenderUtility.lights[1].intensity = hasTexture ? 1.15f : 0.5f;
                _previewRenderUtility.lights[1].transform.rotation = Quaternion.Euler(-30f, -60f, 0f);
                _previewRenderUtility.lights[1].color = new Color(0.9f, 0.95f, 1f);
            }

            _previewRenderUtility.ambientColor = new Color(0.72f, 0.72f, 0.76f);

            _previewRenderUtility.BeginPreview(previewRect, GUIStyle.none);
            _previewRenderUtility.camera.Render();
            var texture = _previewRenderUtility.EndPreview();
            GUI.DrawTexture(previewRect, texture, ScaleMode.StretchToFill, false);

            var hintRect = new Rect(previewRect.x, previewRect.yMax - 28, previewRect.width, 28);
            GUI.Label(hintRect, TJGeneratorsL10n.L("拖拽旋转 | 滚轮缩放 | 双击重置"), CommonStyles.CenteredGreyMiniLabelStyleSmall);
        }

        /// <summary>
        /// 绘制 3D 预览（兼容旧调用：使用默认 0° X 旋转）
        /// </summary>
        public void Draw(string modelPath, float maxWidth, Action repaintCallback)
        {
            Draw(modelPath, maxWidth, new Vector3(0f, 0f, 0f), repaintCallback);
        }

        private void HandleInput(Rect previewRect, Action repaintCallback)
        {
            Event e = Event.current;

            if (previewRect.Contains(e.mousePosition))
            {
                if (e.type == EventType.MouseDrag && e.button == 0)
                {
                    float rotationSpeed = 0.4f;
                    _yaw += e.delta.x * rotationSpeed;
                    _pitch += e.delta.y * rotationSpeed;
                    _pitch = Mathf.Clamp(_pitch, -89f, 89f);

                    e.Use();
                    repaintCallback?.Invoke();
                }

                if (e.type == EventType.MouseDown && e.button == 0 && e.clickCount == 2)
                {
                    _yaw = DefaultYaw;
                    _pitch = DefaultPitch;
                    _zoom = 1f;
                    e.Use();
                    repaintCallback?.Invoke();
                }

                if (e.type == EventType.ScrollWheel)
                {
                    _zoom -= e.delta.y * 0.05f;
                    _zoom = Mathf.Clamp(_zoom, 0.3f, 3f);
                    e.Use();
                    repaintCallback?.Invoke();
                }

                EditorGUIUtility.AddCursorRect(previewRect, MouseCursor.Pan);
            }
        }

        /// <summary>
        /// 将缺失或错误着色器的材质替换为统一的白色材质，仅作用于预览实例。
        /// 优先使用项目里的 DefaultWhite.mat（克隆一份再按管线校正），否则按当前工程渲染管线创建 Lit/Unlit 后备，
        /// 避免 URP 工程里没有内置 Standard 时后备创建失败、预览整片洋红。
        /// </summary>
        private static void TryFixPinkMaterials(GameObject root)
        {
            if (root == null) return;

            var fallback = GetOrCreatePreviewFallbackMaterial();
            if (fallback == null)
                return;

            var renderers = root.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;

                var mats = renderer.sharedMaterials;
                if (mats == null || mats.Length == 0)
                {
                    renderer.sharedMaterials = new[] { fallback };
                    continue;
                }

                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    bool missingShader = mat == null || mat.shader == null ||
                                         mat.shader.name == "Hidden/InternalErrorShader";
                    if (missingShader)
                    {
                        mats[i] = fallback;
                        changed = true;
                    }
                }

                if (changed)
                {
                    renderer.sharedMaterials = mats;
                }
            }
        }

        private static Material GetOrCreatePreviewFallbackMaterial()
        {
            if (_fallbackMaterial != null)
                return _fallbackMaterial;

            const string defaultMatPath = "Assets/TJGenerators/DefaultWhite.mat";
            var assetMat = AssetDatabase.LoadAssetAtPath<Material>(defaultMatPath);
            if (assetMat != null && assetMat.shader != null &&
                assetMat.shader.name != "Hidden/InternalErrorShader")
            {
                _fallbackMaterial = new Material(assetMat)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                TJMaterialShaderUtility.EnsureCompatibleSurfaceShader(_fallbackMaterial);
                ApplyWhitePreviewAlbedo(_fallbackMaterial);
                return _fallbackMaterial;
            }

            var shader = TJMaterialShaderUtility.ResolveSurfaceLitShader();
            if (shader == null)
                return null;

            _fallbackMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            ApplyWhitePreviewAlbedo(_fallbackMaterial);
            return _fallbackMaterial;
        }

        private static void ApplyWhitePreviewAlbedo(Material mat)
        {
            if (mat == null) return;
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", Color.white);
        }

        private bool CheckModelHasTexture(Renderer[] renderers)
        {
            foreach (var renderer in renderers)
            {
                if (renderer.sharedMaterials != null)
                {
                    foreach (var mat in renderer.sharedMaterials)
                    {
                        if (mat != null)
                        {
                            if (mat.mainTexture != null || mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null)
                            {
                                return true;
                            }
                            if (mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") != null)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        public void Cleanup()
        {
            if (_previewInstance != null)
            {
                UnityEngine.Object.DestroyImmediate(_previewInstance);
                _previewInstance = null;
            }
            if (_previewRenderUtility != null)
            {
                _previewRenderUtility.Cleanup();
                _previewRenderUtility = null;
            }
            _currentModelPath = null;
        }

        public void Dispose()
        {
            Cleanup();
        }
    }
}
#endif
