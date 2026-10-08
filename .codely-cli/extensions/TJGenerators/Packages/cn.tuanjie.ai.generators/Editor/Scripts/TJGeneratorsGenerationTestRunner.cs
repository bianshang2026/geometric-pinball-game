#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using TJGenerators.Config;
using TJGenerators.Generators;
using TJGenerators.UI;
using TJGenerators.Utils;
using UnityEditor;
using UnityEngine;

namespace TJGenerators
{
    public class TJGeneratorsGenerationTestRunner : EditorWindow
    {
        private const string PrefKeyPrefix = "TJGeneratorsGenerationTestRunner.";

        private string _selectedGeneratorId = "tripo";
        private string[] _generatorIds;
        private string[] _generatorDisplayNames;
        private int _selectedGeneratorIndex;
        private string _prompt = "a wooden chair";
        private string _imagePath = "";
        private bool _preferImageInput = false;
        private GameObject _targetPrefab;

        private TJGeneratorsTaskHandle _activeHandle;
        private string _statusMessage;
        private string _lastError;
        private string _lastModelPath;

#if TJGENERATORS_DEBUG
        /// <summary>由 <see cref="TJGeneratorsMenuItems.OpenGenerationTestRunnerWindow"/> 或外部代码调用。</summary>
        public static void Open()
        {
            GetWindow<TJGeneratorsGenerationTestRunner>(TJGeneratorsL10n.L("TJGenerators 生成测试"));
        }
#endif

        private void OnEnable()
        {
            RefreshGeneratorList();

            _prompt = EditorPrefs.GetString(PrefKeyPrefix + "Prompt", _prompt);
            _imagePath = EditorPrefs.GetString(PrefKeyPrefix + "ImagePath", _imagePath);
            _preferImageInput = EditorPrefs.GetBool(PrefKeyPrefix + "PreferImage", _preferImageInput);
            _selectedGeneratorId = EditorPrefs.GetString(PrefKeyPrefix + "GeneratorId", _selectedGeneratorId);

            // 根据保存的ID找到对应的索引
            _selectedGeneratorIndex = 0;
            if (_generatorIds != null)
            {
                for (int i = 0; i < _generatorIds.Length; i++)
                {
                    if (_generatorIds[i] == _selectedGeneratorId)
                    {
                        _selectedGeneratorIndex = i;
                        break;
                    }
                }
            }

            var targetGuid = EditorPrefs.GetString(PrefKeyPrefix + "TargetGuid", "");
            if (!string.IsNullOrEmpty(targetGuid))
            {
                var targetPath = AssetDatabase.GUIDToAssetPath(targetGuid);
                _targetPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(targetPath);
            }
        }

        private void RefreshGeneratorList()
        {
            var generators = ConfigManager.GetGenerators(ConfigType.Generator);
            if (generators == null || generators.Count == 0)
            {
                _generatorIds = new[] { "tripo" };
                _generatorDisplayNames = new[] { "Tripo 3D" };
                return;
            }

            var ids = new List<string>();
            var names = new List<string>();
            foreach (var gen in generators)
            {
                if (gen.enabled)
                {
                    ids.Add(gen.id);
                    names.Add(TJGeneratorsL10n.L(gen.displayName ?? gen.id));
                }
            }

            _generatorIds = ids.ToArray();
            _generatorDisplayNames = names.ToArray();
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(PrefKeyPrefix + "Prompt", _prompt ?? "");
            EditorPrefs.SetString(PrefKeyPrefix + "ImagePath", _imagePath ?? "");
            EditorPrefs.SetBool(PrefKeyPrefix + "PreferImage", _preferImageInput);
            EditorPrefs.SetString(PrefKeyPrefix + "GeneratorId", _selectedGeneratorId ?? "tripo");

            var targetGuid = _targetPrefab != null
                ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_targetPrefab))
                : "";
            EditorPrefs.SetString(PrefKeyPrefix + "TargetGuid", targetGuid);
        }

        private void OnGUI()
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), CommonStyles.WindowBackgroundColor);
            EditorGUILayout.LabelField(TJGeneratorsL10n.L("测试参数"), EditorStyles.boldLabel);

            // 生成器选择下拉框
            EditorGUI.BeginChangeCheck();
            _selectedGeneratorIndex = EditorGUILayout.Popup(TJGeneratorsL10n.L("生成器"), _selectedGeneratorIndex, _generatorDisplayNames);
            if (EditorGUI.EndChangeCheck() && _generatorIds != null && _selectedGeneratorIndex < _generatorIds.Length)
            {
                _selectedGeneratorId = _generatorIds[_selectedGeneratorIndex];
            }

            _prompt = EditorGUILayout.TextField(TJGeneratorsL10n.L("提示词"), _prompt);

            using (new EditorGUILayout.HorizontalScope())
            {
                _imagePath = EditorGUILayout.TextField(TJGeneratorsL10n.L("图片路径"), _imagePath);
                if (GUILayout.Button(TJGeneratorsL10n.L("选择..."), GUILayout.Width(60)))
                {
                    var path = EditorUtility.OpenFilePanel(TJGeneratorsL10n.L("选择图片"), "", "png,jpg,jpeg");
                    if (!string.IsNullOrEmpty(path))
                    {
                        _imagePath = path;
                    }
                }
            }

            _preferImageInput = EditorGUILayout.Toggle(TJGeneratorsL10n.L("优先使用图片"), _preferImageInput);

            _targetPrefab = (GameObject)EditorGUILayout.ObjectField(TJGeneratorsL10n.L("目标预制体（可选）"), _targetPrefab, typeof(GameObject), false);
            EditorGUILayout.HelpBox(TJGeneratorsL10n.L("不指定目标预制体时，会自动创建并绑定新的预制体。"), MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(TJGeneratorsL10n.L("使用选中预制体")))
                {
                    if (Selection.activeObject is GameObject selected)
                    {
                        var selectedPath = AssetDatabase.GetAssetPath(selected);
                        var selectedType = AssetDatabase.GetMainAssetTypeAtPath(selectedPath);
                        if (selectedType == typeof(GameObject))
                        {
                            _targetPrefab = selected;
                        }
                    }
                }

                if (GUILayout.Button(TJGeneratorsL10n.L("创建并选择测试预制体")))
                {
                    _targetPrefab = CreateTestPrefab();
                }
            }

            EditorGUILayout.Space();

            if (GUILayout.Button(TJGeneratorsL10n.L("运行测试")))
            {
                RunTest();
            }

            EditorGUILayout.Space();
            DrawStatus();
        }

        private void RunTest()
        {
            _lastError = null;
            _lastModelPath = null;
            _statusMessage = "准备中...";

            var generator = CreateGenerator();
            var targetRef = ResolveTargetAsset();
            var context = new TJGeneratorsGenerationContext
            {
                TargetAsset = targetRef,
                AutoCreateTargetPrefab = true
            };

            _activeHandle = TJGeneratorsGenerationService.Generate(generator, context);
            HookHandle(_activeHandle);
            Repaint();
        }

        private ModelGeneratorBase CreateGenerator()
        {
            var genConfig = ConfigManager.GetGeneratorConfig(ConfigType.Generator, _selectedGeneratorId);
            if (genConfig == null)
            {
                TJLog.LogError($"[TJGeneratorsTest] 未找到生成器配置: {_selectedGeneratorId}");
                return null;
            }

            var generator = new DynamicGenerator(genConfig);

            // 使用公开API设置输入
            if (_preferImageInput && !string.IsNullOrEmpty(_imagePath))
            {
                generator.SetImagePath(_imagePath);
            }
            else
            {
                generator.SetTextPrompt(_prompt ?? "");
            }

            return generator;
        }

        private TJGeneratorsAssetReference ResolveTargetAsset()
        {
            if (_targetPrefab == null)
                return null;

            var path = AssetDatabase.GetAssetPath(_targetPrefab);
            if (string.IsNullOrEmpty(path))
                return null;

            return TJGeneratorsAssetReference.FromPath(path);
        }

        private void HookHandle(TJGeneratorsTaskHandle handle)
        {
            if (handle == null)
                return;

            handle.OnCreated += h =>
            {
                _statusMessage = string.Format(TJGeneratorsL10n.L("已创建任务: {0}"), h.BackendTaskId);
                TJLog.Log($"[TJGeneratorsTest] Created task: {h.BackendTaskId}");
                Repaint();
            };

            handle.OnProgress += h =>
            {
                _statusMessage = $"{h.Status} {h.Progress}%";
                Repaint();
            };

            handle.OnCompleted += h =>
            {
                _statusMessage = "完成";
                _lastModelPath = h.ModelPath;
                TJLog.Log($"[TJGeneratorsTest] Completed: {h.ModelPath}");
                Repaint();
            };

            handle.OnFailed += h =>
            {
                _statusMessage = "失败";
                _lastError = h.ErrorMessage;
                TJLog.LogError($"[TJGeneratorsTest] Failed: {h.ErrorMessage}");
                Repaint();
            };
        }

        private void DrawStatus()
        {
            EditorGUILayout.LabelField(TJGeneratorsL10n.L("状态"), EditorStyles.boldLabel);

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.LabelField(TJGeneratorsL10n.L("进度"), _statusMessage);
            }

            if (_activeHandle != null)
            {
                EditorGUILayout.LabelField(TJGeneratorsL10n.L("任务 ID"), _activeHandle.BackendTaskId ?? "-");
                EditorGUILayout.LabelField(TJGeneratorsL10n.L("本地 ID"), _activeHandle.LocalTaskId ?? "-");
            }

            if (!string.IsNullOrEmpty(_lastModelPath))
            {
                EditorGUILayout.LabelField(TJGeneratorsL10n.L("模型路径"), _lastModelPath);
            }

            if (!string.IsNullOrEmpty(_lastError))
            {
                EditorGUILayout.HelpBox(_lastError, MessageType.Error);
            }
        }

        private static GameObject CreateTestPrefab()
        {
            if (!AssetDatabase.IsValidFolder("Assets/TJGenerators"))
            {
                AssetDatabase.CreateFolder("Assets", "TJGenerators");
            }

            var prefabPath = AssetDatabase.GenerateUniqueAssetPath("Assets/TJGenerators/Test Mesh.prefab");
            var rootGameObject = new GameObject("Generated Mesh");
            try
            {
                var placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
                placeholder.name = "Placeholder";
                placeholder.transform.SetParent(rootGameObject.transform);
                placeholder.transform.localPosition = Vector3.zero;
                placeholder.transform.localRotation = Quaternion.identity;
                placeholder.transform.localScale = Vector3.one;

                PrefabUtility.SaveAsPrefabAsset(rootGameObject, prefabPath);
            }
            finally
            {
                DestroyImmediate(rootGameObject);
            }

            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
            TJGeneratorsGenerationLabel.EnableLabel(TJGeneratorsAssetReference.FromPath(prefabPath));

            return AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        }
    }
}
#endif
