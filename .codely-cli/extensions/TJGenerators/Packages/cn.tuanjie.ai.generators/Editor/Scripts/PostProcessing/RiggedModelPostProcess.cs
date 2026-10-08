#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using TJGenerators.Utils;

namespace TJGenerators.PostProcessing
{
    public static class RiggedModelPostProcess
    {
        public static void SetupRiggedCharacterImport(string assetPath)
        {
            ModelImporter modelImporter = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (modelImporter != null)
            {
                modelImporter.animationType = ModelImporterAnimationType.Human;
                modelImporter.importAnimation = false;
                modelImporter.isReadable = true;
                modelImporter.SaveAndReimport();
                TJLog.Log($"[RiggedModelPostProcess] 绑骨模型 Humanoid 导入（不内嵌动画剪辑）: {assetPath}");
            }
        }

        /// <summary>
        /// 绑骨 FBX 导入收尾：Humanoid 配置 → Refresh → 骨映射修复 → 从源模型/渲染图恢复贴图。
        /// </summary>
        public static void FinalizeRiggedImport(
            string assetPath,
            string sourceModelPath = null,
            string renderedImagePath = null)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            SetupRiggedCharacterImport(assetPath);
            PathUtils.ImportAssetAfterDiskWrite(assetPath);
            TryFixHumanoidBoneMapping(assetPath);

            if (!string.IsNullOrEmpty(sourceModelPath) || !string.IsNullOrEmpty(renderedImagePath))
            {
                ApplyTexturesFromSourceToRiggedModel(sourceModelPath, assetPath, renderedImagePath);
            }
        }

        /// <summary>
        /// 在 SetupRiggedCharacterImport + Refresh() 之后调用。
        /// 若 Avatar.isHuman 仍为 False，扫描 FBX 内的标准命名骨骼并补充缺失的 Humanoid 映射，
        /// 再触发一次重新导入。适用于 UniRig 输出中 Chest/UpperChest/Neck/Head 未被自动识别的情况。
        /// </summary>
        /// <returns>修复后 avatar.isHuman 是否为 True。</returns>
        public static bool TryFixHumanoidBoneMapping(string assetPath)
        {
            // 已经是 Humanoid 则不处理
            var existingAvatar = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                                              .OfType<Avatar>()
                                              .FirstOrDefault(a => a != null && a.isValid);
            if (existingAvatar != null && existingAvatar.isHuman)
                return true;

            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return false;

            // 收集 FBX 中所有骨骼名
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go == null) return false;
            var allBones = new HashSet<string>(
                go.GetComponentsInChildren<Transform>(true).Select(t => t.name));

            // 已映射的 humanName 集合
            var desc           = importer.humanDescription;
            var alreadyMapped  = new HashSet<string>(
                (desc.human ?? new HumanBone[0]).Select(h => h.humanName));
            var humanBones     = new List<HumanBone>(desc.human ?? new HumanBone[0]);
            bool changed       = false;

            // 候选：(humanName, boneName in FBX)
            // 先列直接同名映射，Head 条目按优先级排列（先尝试 "Head"，再用 "Neck_end" 兜底）
            var candidates = new (string humanName, string boneName)[]
            {
                ("Chest",          "Chest"),
                ("UpperChest",     "UpperChest"),
                ("Neck",           "Neck"),
                ("Head",           "Head"),
                ("Head",           "Neck_end"),    // UniRig 常见兜底
                ("LeftShoulder",   "LeftShoulder"),
                ("RightShoulder",  "RightShoulder"),
                ("LeftToes",       "LeftToes"),
                ("RightToes",      "RightToes"),
            };

            foreach (var (humanName, boneName) in candidates)
            {
                if (alreadyMapped.Contains(humanName)) continue;
                if (!allBones.Contains(boneName)) continue;

                var hb = new HumanBone();
                hb.humanName           = humanName;
                hb.boneName            = boneName;
                hb.limit.useDefaultValues = true;
                humanBones.Add(hb);
                alreadyMapped.Add(humanName);   // 防止同 humanName 的兜底条目重复添加
                changed = true;
                TJLog.Log($"[RiggedModelPostProcess] 补充 Humanoid 骨映射: {humanName} → {boneName}");
            }

            if (!changed)
            {
                TJLog.LogWarning($"[RiggedModelPostProcess] Avatar 不是 Humanoid 且无法自动补全缺失映射: {assetPath}");
                return false;
            }

            desc.human            = humanBones.ToArray();
            importer.humanDescription = desc;
            importer.SaveAndReimport();

            var newAvatar = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                                         .OfType<Avatar>()
                                         .FirstOrDefault(a => a != null && a.isValid);
            bool success = newAvatar?.isHuman == true;
            if (success)
                TJLog.Log($"[RiggedModelPostProcess] Humanoid 骨映射修复成功: {assetPath}");
            else
                TJLog.LogWarning($"[RiggedModelPostProcess] 修复后 Avatar 仍非 Humanoid，骨骼结构可能不兼容: {assetPath}");
            return success;
        }

        public static void SetupAnimationImport(string assetPath, bool loopTime = true)
        {
            ModelImporter modelImporter = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (modelImporter != null)
            {
                modelImporter.animationType = ModelImporterAnimationType.Human;
                modelImporter.importAnimation = true;
                modelImporter.isReadable = true;
                var clips = modelImporter.defaultClipAnimations;
                if (clips != null && clips.Length > 0)
                {
                    foreach (var clip in clips)
                        clip.loopTime = loopTime;
                    modelImporter.clipAnimations = clips;
                }
                modelImporter.SaveAndReimport();
                TJLog.Log($"[RiggedModelPostProcess] 动画导入配置完成: {assetPath}, loopTime={loopTime}");
            }
        }

        /// <summary>
        /// 检查模型上是否至少有一个材质槽绑定了有效主贴图（mainTexture / _BaseMap / _MainTex）。
        /// </summary>
        private static bool HasValidMainTexture(string assetPath)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (root == null) return false;
            foreach (var rend in root.GetComponentsInChildren<Renderer>(true))
            {
                if (rend.sharedMaterials == null) continue;
                foreach (var mat in rend.sharedMaterials)
                {
                    if (mat == null) continue;
                    if (mat.mainTexture != null) return true;
                    if (mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") != null) return true;
                    if (mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 将进 UniRig 前模型上的材质赋给绑骨后的模型。
        /// 优先按 Renderer 数量一致时按下标一一对应；否则按层级相对路径匹配；再否则按较短列表长度按下标对齐。
        /// </summary>
        /// <returns>成功写入材质的 Renderer 数量。</returns>
        public static int ApplyMaterialsFromSourceModelToRiggedModel(string sourceAssetPath, string riggedAssetPath)
        {
            if (string.IsNullOrEmpty(sourceAssetPath) || string.IsNullOrEmpty(riggedAssetPath))
                return 0;

            var sourceRoot = AssetDatabase.LoadAssetAtPath<GameObject>(sourceAssetPath);
            var riggedRoot = AssetDatabase.LoadAssetAtPath<GameObject>(riggedAssetPath);
            if (sourceRoot == null || riggedRoot == null)
            {
                TJLog.LogWarning(
                    $"[RiggedModelPostProcess] 绑骨后材质复用：无法加载模型 source={sourceAssetPath} rigged={riggedAssetPath}"
                );
                return 0;
            }

            Renderer[] src = sourceRoot.GetComponentsInChildren<Renderer>(true);
            Renderer[] dst = riggedRoot.GetComponentsInChildren<Renderer>(true);
            if (src.Length == 0 || dst.Length == 0)
                return 0;

            int applied = 0;

            if (src.Length == dst.Length)
            {
                for (int i = 0; i < src.Length; i++)
                {
                    if (src[i].sharedMaterials == null)
                        continue;
                    dst[i].sharedMaterials = src[i].sharedMaterials;
                    applied++;
                }
            }
            else
            {
                var byPath = new Dictionary<string, Material[]>(StringComparer.Ordinal);
                foreach (var r in src)
                {
                    string p = RendererHierarchyPath(sourceRoot.transform, r);
                    if (p == null)
                        continue;
                    if (!byPath.ContainsKey(p) && r.sharedMaterials != null)
                        byPath[p] = r.sharedMaterials;
                }

                var matchedDst = new bool[dst.Length];
                for (int i = 0; i < dst.Length; i++)
                {
                    string p = RendererHierarchyPath(riggedRoot.transform, dst[i]);
                    if (p != null && byPath.TryGetValue(p, out Material[] mats) && mats != null)
                    {
                        dst[i].sharedMaterials = mats;
                        matchedDst[i] = true;
                        applied++;
                    }
                }

                if (applied == 0)
                {
                    int n = Math.Min(src.Length, dst.Length);
                    for (int i = 0; i < n; i++)
                    {
                        if (src[i].sharedMaterials == null)
                            continue;
                        dst[i].sharedMaterials = src[i].sharedMaterials;
                        applied++;
                    }
                }
            }

            if (applied > 0)
                AssetDatabase.SaveAssets();

            TJLog.Log(
                $"[RiggedModelPostProcess] 绑骨后从源模型复用材质: {applied}/{dst.Length} 个 Renderer，源={sourceAssetPath}"
            );
            return applied;
        }

        /// <summary>
        /// 使用外部动作 FBX 中的剪辑，为绑骨后主模型创建单状态循环 AnimatorController。
        /// </summary>
        /// <returns>创建的 AnimatorController 资产路径；若失败返回 null。</returns>
        public static string CreateSingleClipLoopAnimatorControllerFromMotionClip(
            string modelDir,
            string targetRigBaseName,
            string motionFbxUnityPath)
        {
            try
            {
                modelDir = PathUtils.NormalizeModelDirectory(modelDir);

                string controllerPath =
                    Path.Combine(modelDir, targetRigBaseName + "_Controller.controller").Replace("\\", "/");
                string controllerDir = Path.GetDirectoryName(controllerPath)?.Replace("\\", "/") ?? "";
                string absoluteControllerDir = PathUtils.ToAbsoluteAssetPath(controllerDir);
                if (!string.IsNullOrEmpty(absoluteControllerDir) && !Directory.Exists(absoluteControllerDir))
                    Directory.CreateDirectory(absoluteControllerDir);

                AnimationClip clip = GetAnimationClipFromFbx(motionFbxUnityPath);
                if (clip == null)
                {
                    TJLog.LogWarning(
                        $"[RiggedModelPostProcess] 无法从动作 FBX 提取剪辑，跳过控制器创建: {motionFbxUnityPath}"
                    );
                    return null;
                }

                // 若 Controller 已存在（重新生成同一任务时），删除后重建，确保新动作剪辑生效
                if (AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controllerPath) != null)
                {
                    AssetDatabase.DeleteAsset(controllerPath);
                    TJLog.Log($"[RiggedModelPostProcess] 已删除旧 Animator Controller，重新创建: {controllerPath}");
                }

                var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
                if (controller == null)
                {
                    TJLog.LogWarning($"[RiggedModelPostProcess] 无法创建 Animator Controller: {controllerPath}");
                    return null;
                }

                var sm = controller.layers[0].stateMachine;
                AnimatorState previousDefault = sm.defaultState;
                string stateName = string.IsNullOrEmpty(clip.name) ? "Motion" : clip.name;
                var motionState = sm.AddState(stateName);
                motionState.motion = clip;
                motionState.writeDefaultValues = true;
                sm.defaultState = motionState;
                if (previousDefault != null && previousDefault != motionState)
                    sm.RemoveState(previousDefault);

                var selfLoop = motionState.AddTransition(motionState);
                selfLoop.hasExitTime = true;
                selfLoop.exitTime = 1f;
                selfLoop.duration = 0f;
                selfLoop.offset = 0f;
                selfLoop.hasFixedDuration = true;

                AssetDatabase.SaveAssets();
                PathUtils.SafeRefresh();

                TJLog.Log(
                    $"[RiggedModelPostProcess] 单剪辑循环 Animator Controller 已创建: {controllerPath} (clip={clip.name})"
                );
                return controllerPath;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[RiggedModelPostProcess] 创建 Animator Controller 失败: {e.Message}");
                return null;
            }
        }

        public static AnimationClip GetAnimationClipFromFbx(string fbxPath)
        {
            if (string.IsNullOrEmpty(fbxPath) || !File.Exists(PathUtils.ToAbsoluteAssetPath(fbxPath)))
                return null;

            var clips = AssetDatabase.LoadAllAssetsAtPath(fbxPath)
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__"))
                .ToList();

            if (clips.Count > 0)
            {
                TJLog.Log($"[RiggedModelPostProcess] 从 {fbxPath} 找到 {clips.Count} 个动画剪辑");
                return clips[0];
            }

            TJLog.LogWarning($"[RiggedModelPostProcess] 未在 {fbxPath} 中找到动画剪辑");
            return null;
        }

        /// <summary>
        /// 将绑骨 FBX 中嵌入的材质提取为外部 .mat 文件，应用贴图后通过 ModelImporter.AddRemap 建立持久映射。
        /// 解决直接修改嵌入材质后在历史记录切换、域重载等场景中贴图丢失的问题。
        /// 提取目录为 &lt;modelDir&gt;/&lt;baseName&gt;.fbm/，与主模型后处理保持一致。
        /// </summary>
        /// <param name="riggedAssetPath">绑骨模型的 Unity 相对路径（Assets/...）</param>
        /// <param name="textureAssetPath">要写入的贴图 Unity 相对路径；为 null/empty 时仅提取材质不赋贴图</param>
        /// <returns>成功处理的材质数量</returns>
        public static int ExtractMaterialsAndApplyTextureToRiggedModel(string riggedAssetPath, string textureAssetPath)
        {
            if (string.IsNullOrEmpty(riggedAssetPath))
                return 0;

            Texture2D texture = null;
            if (!string.IsNullOrEmpty(textureAssetPath))
            {
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(textureAssetPath);
                if (texture == null)
                {
                    TJLog.LogWarning($"[RiggedModelPostProcess] 无法加载贴图，跳过材质写入: {textureAssetPath}");
                    return 0;
                }
            }

            return ExtractMaterialsAndApplyTextureToRiggedModel(riggedAssetPath, texture,
                texture != null ? $"→ {textureAssetPath}" : "，未赋贴图");
        }

        /// <summary>
        /// 将绑骨 FBX 中嵌入的材质提取为外部 .mat，并写入 Texture2D。
        /// </summary>
        public static int ExtractMaterialsAndApplyTextureToRiggedModel(string riggedAssetPath, Texture2D texture)
        {
            if (string.IsNullOrEmpty(riggedAssetPath))
                return 0;

            string label = texture != null ? $"→ {texture.name}" : "，未赋贴图";
            return ExtractMaterialsAndApplyTextureToRiggedModel(riggedAssetPath, texture, label);
        }

        private static int ExtractMaterialsAndApplyTextureToRiggedModel(
            string riggedAssetPath, Texture2D texture, string textureLabel)
        {
            var importer = AssetImporter.GetAtPath(riggedAssetPath) as ModelImporter;
            if (importer == null) return 0;

            string modelDir = Path.GetDirectoryName(riggedAssetPath)?.Replace("\\", "/") ?? "";
            string baseName = Path.GetFileNameWithoutExtension(riggedAssetPath);
            string matDir = string.IsNullOrEmpty(modelDir)
                ? $"{baseName}.fbm"
                : $"{modelDir}/{baseName}.fbm";

            string absMatDir = PathUtils.ToAbsoluteAssetPath(matDir);
            if (!string.IsNullOrEmpty(absMatDir) && !Directory.Exists(absMatDir))
                Directory.CreateDirectory(absMatDir);

            var embeddedMaterials = AssetDatabase.LoadAllAssetsAtPath(riggedAssetPath)
                .OfType<Material>()
                .ToList();

            if (embeddedMaterials.Count == 0)
            {
                TJLog.LogWarning($"[RiggedModelPostProcess] 绑骨模型中无嵌入材质，跳过材质提取: {riggedAssetPath}");
                return 0;
            }

            int processed = 0;
            foreach (var embMat in embeddedMaterials)
            {
                string matPath = $"{matDir}/{embMat.name}.mat";

                Material extMat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (extMat == null)
                {
                    extMat = new Material(embMat);
                    AssetDatabase.CreateAsset(extMat, matPath);
                }

                if (texture != null)
                {
                    extMat.mainTexture = texture;
                    if (extMat.HasProperty("_BaseMap"))
                        extMat.SetTexture("_BaseMap", texture);
                    if (extMat.HasProperty("_MainTex"))
                        extMat.SetTexture("_MainTex", texture);
                    EditorUtility.SetDirty(extMat);
                }

                importer.AddRemap(
                    new AssetImporter.SourceAssetIdentifier(typeof(Material), embMat.name),
                    extMat
                );
                processed++;
            }

            if (processed > 0)
            {
                importer.SaveAndReimport();
                AssetDatabase.SaveAssets();
            }

            TJLog.Log(
                $"[RiggedModelPostProcess] 绑骨模型材质外部化完成: {processed} 个材质{textureLabel}，模型={riggedAssetPath}"
            );
            return processed;
        }

        /// <summary>
        /// 绑骨完成后从源模型恢复贴图：① 复制材质 → ② rendered_image → ③ .fbm。
        /// </summary>
        public static int ApplyTexturesFromSourceToRiggedModel(
            string sourceAssetPath,
            string riggedAssetPath,
            string renderedTexturePath = null)
        {
            if (string.IsNullOrEmpty(riggedAssetPath))
                return 0;

            if (!string.IsNullOrEmpty(sourceAssetPath))
            {
                int materialsApplied = ApplyMaterialsFromSourceModelToRiggedModel(sourceAssetPath, riggedAssetPath);
                if (materialsApplied > 0 && HasValidMainTexture(riggedAssetPath))
                    return materialsApplied;
                // 贴图槽为空：继续走后续 rendered_image / .fbm 路径
                if (materialsApplied > 0)
                    TJLog.LogWarning($"[RiggedModelPostProcess] 源模型材质已复用但主贴图为空，继续尝试 rendered_image: {riggedAssetPath}");
            }

            if (!string.IsNullOrEmpty(renderedTexturePath))
            {
                int extracted = ExtractMaterialsAndApplyTextureToRiggedModel(riggedAssetPath, renderedTexturePath);
                if (extracted > 0)
                    return extracted;
                TJLog.LogWarning($"[RiggedModelPostProcess] 材质外部化失败，rendered_image 贴图未写入: {riggedAssetPath}");
                return 0;
            }

            if (string.IsNullOrEmpty(sourceAssetPath))
                return 0;

            string texturePath = FindMainTextureFromSourceModel(sourceAssetPath);
            if (!string.IsNullOrEmpty(texturePath))
            {
                int extracted = ExtractMaterialsAndApplyTextureToRiggedModel(riggedAssetPath, texturePath);
                if (extracted > 0)
                    return extracted;
            }

            return 0;
        }

        /// <summary>
        /// 从源模型目录查找主贴图（优先 .fbm 子文件夹）。
        /// </summary>
        public static string FindMainTextureFromSourceModel(string sourceAssetPath)
        {
            if (string.IsNullOrEmpty(sourceAssetPath)) return null;

            string modelDir = Path.GetDirectoryName(sourceAssetPath)?.Replace("\\", "/") ?? "";
            string baseName = Path.GetFileNameWithoutExtension(sourceAssetPath);
            string fbmDir = string.IsNullOrEmpty(modelDir)
                ? $"{baseName}.fbm"
                : $"{modelDir}/{baseName}.fbm";
            string absFbmDir = PathUtils.ToAbsoluteAssetPath(fbmDir);

            if (!string.IsNullOrEmpty(absFbmDir) && Directory.Exists(absFbmDir))
            {
                string[] texFiles = Directory.GetFiles(absFbmDir, "*.png", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(absFbmDir, "*.jpg",  SearchOption.TopDirectoryOnly))
                    .Concat(Directory.GetFiles(absFbmDir, "*.jpeg", SearchOption.TopDirectoryOnly))
                    .Concat(Directory.GetFiles(absFbmDir, "*.webp", SearchOption.TopDirectoryOnly))
                    .ToArray();

                bool hasWebp = texFiles.Any(f => f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
                if (hasWebp)
                    TJLog.LogWarning($"[RiggedModelPostProcess] 贴图目录含 .webp 文件，Unity 2021.2 以下版本可能无法导入: {absFbmDir}");

                string primary = texFiles.FirstOrDefault(f =>
                {
                    string lower = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    return lower.Contains("albedo") || lower.Contains("diffuse") ||
                           lower.Contains("basecolor") || lower.Contains("color");
                });
                if (primary != null)
                    return PathUtils.AbsolutePathToAssetsRelative(primary.Replace("\\", "/"));

                string nonPbr = texFiles.FirstOrDefault(f =>
                {
                    string lower = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    return !lower.Contains("normal") && !lower.Contains("roughness") &&
                           !lower.Contains("metallic") && !lower.Contains("occlusion") &&
                           !lower.Contains("emission") && !lower.Contains("height");
                });
                if (nonPbr != null)
                    return PathUtils.AbsolutePathToAssetsRelative(nonPbr.Replace("\\", "/"));

                if (texFiles.Length > 0)
                    return PathUtils.AbsolutePathToAssetsRelative(texFiles[0].Replace("\\", "/"));
            }

            if (!string.IsNullOrEmpty(modelDir))
            {
                string absModelDir = PathUtils.ToAbsoluteAssetPath(modelDir);
                if (!string.IsNullOrEmpty(absModelDir) && Directory.Exists(absModelDir))
                {
                    foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
                    {
                        string absTexPath = Path.Combine(absModelDir, baseName + ext).Replace("\\", "/");
                        if (File.Exists(absTexPath))
                        {
                            if (ext == ".webp")
                                TJLog.LogWarning($"[RiggedModelPostProcess] 找到 .webp 贴图，Unity 2021.2 以下版本可能无法导入: {absTexPath}");
                            return PathUtils.AbsolutePathToAssetsRelative(absTexPath);
                        }
                    }
                }
            }

            return null;
        }

        public static string RendererHierarchyPath(Transform modelRoot, Renderer renderer)
        {
            if (modelRoot == null || renderer == null)
                return null;

            Transform t = renderer.transform;
            var parts = new List<string>();
            while (t != null && t != modelRoot)
            {
                parts.Add(t.name);
                t = t.parent;
            }

            if (t != modelRoot)
                return null;

            parts.Reverse();
            return parts.Count == 0 ? string.Empty : string.Join("/", parts);
        }
    }
}
#endif
