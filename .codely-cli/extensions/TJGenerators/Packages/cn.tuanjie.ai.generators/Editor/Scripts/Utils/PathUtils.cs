#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using TJGenerators.Config;

namespace TJGenerators.Utils
{
    /// <summary>
    /// 路径与通用路径取值：项目/资源路径转换，以及响应对象上的点路径反射取值（字段/属性、数组下标）。
    /// </summary>
    public static class PathUtils
    {
        /// <summary>
        /// 解析本包（cn.tuanjie.ai.generators）在磁盘上的根目录，用于 git/本地 Package、以及 Editor/ 相对路径。
        /// </summary>
        public static string TryGetTjGeneratorsPackageRoot()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ConfigManager).Assembly);
                if (info != null
                    && !string.IsNullOrEmpty(info.resolvedPath)
                    && Directory.Exists(info.resolvedPath))
                    return info.resolvedPath;
            }
            catch
            {
                // ignored
            }

            return null;
        }

        /// <summary>
        /// 若绝对路径位于本工程 Assets 下，返回 Assets 相对路径；否则返回 null。
        /// </summary>
        public static string TryGetAssetsRelativePathFromAbsolute(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                return null;

            absolutePath = Path.GetFullPath(absolutePath);
            string dataPath = Path.GetFullPath(Application.dataPath);
            if (!absolutePath.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                return null;

            string tail = absolutePath.Substring(dataPath.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return "Assets/" + tail.Replace('\\', '/');
        }

        // Path.GetInvalidFileNameChars() is OS-specific (Linux allows : | * ? " < >).
        // Unity Assets are shared across platforms, so always strip Windows-invalid chars too.
        private static readonly char[] CrossPlatformInvalidFileNameChars =
        {
            '/', '\\', ':', '*', '?', '"', '<', '>', '|',
        };

        /// <summary>
        /// 将名称规范为可作为 Assets 下文件夹名的片段（去除非法文件名字符）。
        /// 使用跨平台字符集（含 Windows 非法字符），避免 Linux 上保留 <c>:|</c> 等导致工程在 Windows 上无法打开。
        /// </summary>
        public static string SanitizeAssetFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Model";

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            foreach (char c in CrossPlatformInvalidFileNameChars)
                name = name.Replace(c, '_');

            name = name.Trim();
            return string.IsNullOrEmpty(name) ? "Model" : name;
        }

        /// <summary>
        /// 规范化模型目录路径：去掉 Assets/Assets/ 多余前缀，并统一斜杠方向。
        /// </summary>
        public static string NormalizeModelDirectory(string modelDir)
        {
            const string assetsPrefix = "Assets/";
            if (modelDir != null && modelDir.StartsWith(assetsPrefix + assetsPrefix, StringComparison.OrdinalIgnoreCase))
                modelDir = modelDir.Substring(assetsPrefix.Length);
            return modelDir?.Replace("\\", "/") ?? "";
        }

        /// <summary>
        /// 将 CDN / HTTP(S) URL 中的 Windows 反斜杠统一为正斜杠，避免路径拼接混入 <c>\</c> 导致下载失败。
        /// </summary>
        public static string NormalizeRemoteUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;
            return url.IndexOf('\\') >= 0 ? url.Replace('\\', '/') : url;
        }

        /// <summary>
        /// 对 URL 数组逐元素调用 <see cref="NormalizeRemoteUrl"/>；null 或空数组原样返回。
        /// </summary>
        public static string[] NormalizeUrlArray(string[] urls)
        {
            if (urls == null || urls.Length == 0)
                return urls;

            var result = new string[urls.Length];
            for (int i = 0; i < urls.Length; i++)
                result[i] = NormalizeRemoteUrl(urls[i]);
            return result;
        }

        /// <summary>
        /// 将项目相对路径转换为绝对路径。
        /// </summary>
        /// <param name="projectRelativePath">项目相对路径（可以是 "Assets/..." 或其他相对路径）</param>
        /// <returns>绝对路径</returns>
        /// <remarks>
        /// - 如果路径为空或已经是绝对路径，直接返回原路径
        /// - "Packages/..." 解析到项目根下的 Packages（与 Assets 同级），不会错误落到 Assets/Packages
        /// - "Assets/..." 会转换为 Application.dataPath 下的绝对路径
        /// - "Editor/..." 解析为当前 TJGenerators 包根下的相对路径（便于随包分发、不依赖 UPM 文件夹名）
        /// - 其他相对路径按「位于 Assets 目录下」与 Application.dataPath 组合（兼容旧用法）
        /// - 会自动规范化路径中的反斜杠为正斜杠
        /// </remarks>
        public static string ToAbsoluteAssetPath(string projectRelativePath)
        {
            if (string.IsNullOrEmpty(projectRelativePath)) return projectRelativePath;
            if (Path.IsPathRooted(projectRelativePath)) return projectRelativePath.Replace('\\', '/');

            projectRelativePath = projectRelativePath.Replace("\\", "/");

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');

            if (projectRelativePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(Path.Combine(projectRoot, projectRelativePath)).Replace('\\', '/');

            const string assetsPrefix = "Assets/";
            if (projectRelativePath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Application.dataPath, projectRelativePath.Substring(assetsPrefix.Length)).Replace('\\', '/');

            if (projectRelativePath.StartsWith("Editor/", StringComparison.OrdinalIgnoreCase))
            {
                string pkgRoot = TryGetTjGeneratorsPackageRoot();
                if (!string.IsNullOrEmpty(pkgRoot))
                    return Path.GetFullPath(Path.Combine(pkgRoot, projectRelativePath)).Replace('\\', '/');
            }

            return Path.Combine(Application.dataPath, projectRelativePath).Replace('\\', '/');
        }

        /// <summary>
        /// 将磁盘绝对路径转为 Unity 资源路径（Assets/...）。
        /// 不要求路径对应文件已存在（与 <see cref="TryGetAssetsRelativePathFromAbsolute"/> 不同）。
        /// 若路径不在本工程 Assets 目录下，返回传入的 <paramref name="absolutePath"/> 原样。
        /// </summary>
        public static string AbsolutePathToAssetsRelative(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return absolutePath;

            string normalized = Path.GetFullPath(absolutePath).Replace('\\', '/');
            string dataPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            if (normalized.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                return "Assets" + normalized.Substring(dataPath.Length);
            return absolutePath;
        }

        /// <summary>
        /// 判断磁盘绝对路径是否落在本工程 Assets 目录下（含 Assets 根本身）。
        /// </summary>
        public static bool IsAbsolutePathUnderAssets(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return false;

            string normalized = Path.GetFullPath(absolutePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string dataPath = Path.GetFullPath(Application.dataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalized, dataPath, StringComparison.OrdinalIgnoreCase))
                return true;

            string prefix = dataPath + Path.DirectorySeparatorChar;
            return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 将 CustomTool 的 <c>output_path</c> 规范为 Unity Assets 相对路径（正斜杠）。
        /// 接受 <c>Assets/...</c>，或落在本工程 Assets 下的绝对路径；拒绝工程外路径与非 Assets 相对路径。
        /// </summary>
        public static bool TryNormalizeOutputAssetPath(
            string outputPath, out string normalizedAssetPath, out string errorMessage)
        {
            normalizedAssetPath = null;
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                errorMessage = TJGeneratorsL10n.L("'output_path' 不能为空。");
                return false;
            }

            string trimmed = outputPath.Trim().Replace('\\', '/');
            string absolute;
            if (Path.IsPathRooted(trimmed))
            {
                absolute = Path.GetFullPath(trimmed);
            }
            else
            {
                if (!trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(trimmed, "Assets", StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = TJGeneratorsL10n.L(
                        "'output_path' 必须是 Assets 相对路径（Assets/...），或位于工程 Assets 目录下的绝对路径。收到：'{0}'",
                        outputPath);
                    return false;
                }

                absolute = Path.GetFullPath(ToAbsoluteAssetPath(trimmed));
            }

            if (!IsAbsolutePathUnderAssets(absolute))
            {
                errorMessage = TJGeneratorsL10n.L(
                    "'output_path' 必须解析到工程 Assets 目录下。收到：'{0}'",
                    outputPath);
                return false;
            }

            string relative = AbsolutePathToAssetsRelative(absolute)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(relative)
                || (!relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(relative, "Assets", StringComparison.OrdinalIgnoreCase)))
            {
                errorMessage = TJGeneratorsL10n.L(
                    "'output_path' 无法转换为 Assets 相对路径。收到：'{0}'",
                    outputPath);
                return false;
            }

            normalizedAssetPath = relative;
            return true;
        }

        /// <summary>
        /// 调用 <see cref="AssetDatabase.GenerateUniqueAssetPath"/>，并校验返回值仍是合法 Assets 路径；
        /// 空/非法时回退到 <paramref name="fallbackAssetPath"/>（再不行则用 preferred）。
        /// </summary>
        public static string GenerateUniqueAssetPathChecked(string preferredAssetPath, string fallbackAssetPath = null)
        {
            if (string.IsNullOrEmpty(preferredAssetPath))
                preferredAssetPath = fallbackAssetPath;

            if (string.IsNullOrEmpty(preferredAssetPath))
                return preferredAssetPath;

            preferredAssetPath = preferredAssetPath.Replace('\\', '/');
            string unique = AssetDatabase.GenerateUniqueAssetPath(preferredAssetPath);
            if (!string.IsNullOrEmpty(unique)
                && TryNormalizeOutputAssetPath(unique, out string normalized, out _))
                return normalized;

            if (!string.IsNullOrEmpty(fallbackAssetPath)
                && TryNormalizeOutputAssetPath(fallbackAssetPath, out string fallbackNormalized, out _))
                return fallbackNormalized;

            return preferredAssetPath;
        }

        /// <summary>
        /// Project 窗口创建资源时的目标文件夹：当前选中文件夹，或选中资产所在目录；无选中时为 Assets。
        /// </summary>
        public static string GetProjectBrowserInsertionFolderAssetPath()
        {
            foreach (UnityEngine.Object obj in Selection.GetFiltered<UnityEngine.Object>(SelectionMode.Assets))
            {
                string p = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(p))
                    continue;
                p = p.Replace('\\', '/');
                if (AssetDatabase.IsValidFolder(p))
                    return p;
                string dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir))
                {
                    dir = dir.Replace('\\', '/');
                    if (AssetDatabase.IsValidFolder(dir))
                        return dir;
                }
                break;
            }
            return "Assets";
        }

        /// <summary>
        /// 在 Assets 目录下查找扩展名为 .fbx / .obj 的模型资源路径（Unity 工程相对路径，如 Assets/…）。
        /// 说明：避免使用 FindAssets("", Assets) 扫全库——在工程很大时会在 OnGUI 中反复触发并可能导致编辑器堆内存暴涨崩溃。
        /// Unity 2021.2+ 使用 glob 仅匹配目标扩展名；更早版本退化为全库扫描（调用方应节流，勿每帧调用）。
        /// </summary>
        public static List<string> FindMeshModelAssetPathsInAssets()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

#if UNITY_2021_2_OR_NEWER
            void AddGlob(string glob)
            {
                foreach (string guid in AssetDatabase.FindAssets(glob, new[] { "Assets" }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (AssetDatabase.IsValidFolder(path))
                        continue;
                    set.Add(path);
                }
            }

            AddGlob("glob:\"**/*.fbx\"");
            AddGlob("glob:\"**/*.obj\"");
#else
            string[] guids = AssetDatabase.FindAssets("", new[] { "Assets" });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path))
                    continue;

                string ext = Path.GetExtension(path);
                if (
                    ext.Equals(".fbx", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".obj", StringComparison.OrdinalIgnoreCase)
                )
                    set.Add(path);
            }
#endif

            var results = new List<string>(set);
            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        /// <summary>
        /// 在对象上按点路径取值（字段/属性）；段为数字时对 <see cref="Array"/> 按下标访问（如 resultFiles.0.url）。
        /// </summary>
        public static object GetRaw(object obj, string path)
        {
            if (obj == null || string.IsNullOrEmpty(path))
                return null;

            var parts = path.Split('.');
            object current = obj;

            foreach (var part in parts)
            {
                if (current == null)
                    return null;

                if (
                    current is Array arr
                    && int.TryParse(part, out int idx)
                    && idx >= 0
                    && idx < arr.Length
                )
                {
                    current = arr.GetValue(idx);
                    continue;
                }

                var type = current.GetType();
                var field = type.GetField(part);
                if (field != null)
                {
                    current = field.GetValue(current);
                }
                else
                {
                    var prop = type.GetProperty(part);
                    if (prop != null)
                    {
                        current = prop.GetValue(current);
                    }
                    else
                    {
                        return null;
                    }
                }
            }

            return current;
        }

        /// <summary>
        /// 从响应对象按点路径读取远程 URL 字符串：同 <see cref="GetRaw"/> 取值后 ToString，
        /// 数组取首元素；返回值经 <see cref="NormalizeRemoteUrl"/> 将反斜杠转为正斜杠。
        /// </summary>
        public static string GetUrlString(object obj, string path)
        {
            object current = GetRaw(obj, path);
            if (current == null)
                return null;

            string value;
            if (current is Array arr)
                value = arr.Length > 0 ? arr.GetValue(0)?.ToString() : null;
            else
                value = current.ToString();

            return NormalizeRemoteUrl(value);
        }

        /// <summary>
        /// 递归确保 Assets 下指定路径的资源文件夹存在（Unity AssetDatabase 视角）。
        /// 路径必须以 "Assets" 开头；空路径或已存在则直接返回。
        /// </summary>
        public static void EnsureAssetFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            if (AssetDatabase.IsValidFolder(folderPath)) return;
            string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) return;
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureAssetFolder(parent);
            string name = Path.GetFileName(folderPath);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static readonly string[] ModelAssetExtensions =
        {
            ".fbx", ".obj",
        };

        /// <summary>
        /// 是否为 Unity ModelImporter 支持的网格资产路径。
        /// </summary>
        public static bool IsModelAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            string ext = Path.GetExtension(assetPath);
            if (string.IsNullOrEmpty(ext))
                return false;

            foreach (string modelExt in ModelAssetExtensions)
            {
                if (ext.Equals(modelExt, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 确保 ModelImporter 开启 Read/Write，避免 Player 运行时网格不可读导致模型不可见。
        /// </summary>
        /// <returns>是否触发了重新导入。</returns>
        public static bool EnsureModelImporterReadable(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !IsModelAssetPath(assetPath))
                return false;

            assetPath = assetPath.Replace('\\', '/');
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null || importer.isReadable)
                return false;

            importer.isReadable = true;
            importer.SaveAndReimport();
            return true;
        }

        /// <summary>
        /// 磁盘写入后导入指定资产。优先 <see cref="AssetDatabase.ImportAsset"/>，
        /// 仅在资产仍不可加载时做一次全量 <see cref="SafeRefresh"/> 兜底。
        /// 模型资产会额外确保 <see cref="ModelImporter.isReadable"/> 为 true。
        /// </summary>
        public static void ImportAssetAfterDiskWrite(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            assetPath = assetPath.Replace('\\', '/');
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            EnsureModelImporterReadable(assetPath);
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null)
                return;

            SafeRefresh();
            EnsureModelImporterReadable(assetPath);
        }

        /// <summary>
        /// Guards <see cref="AssetDatabase.Refresh"/> against being called during script
        /// compilation. When <see cref="EditorApplication.isCompiling"/> is true, defers
        /// the Refresh to after compilation completes. This breaks the cycle:
        /// Domain Reload → recovery → Refresh → detect .cs changes → recompile → repeat.
        /// </summary>
        private static bool s_refreshPending;

        public static void SafeRefresh(ImportAssetOptions options = ImportAssetOptions.Default)
        {
            if (EditorApplication.isCompiling)
            {
                if (!s_refreshPending)
                {
                    s_refreshPending = true;
                    EditorApplication.update += FlushPendingRefresh;
                }
                return;
            }
            AssetDatabase.Refresh(options);
        }

        private static void FlushPendingRefresh()
        {
            if (EditorApplication.isCompiling) return;
            EditorApplication.update -= FlushPendingRefresh;
            s_refreshPending = false;
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// 导入文件夹下所有 Assets 内文件（用于 ZIP 解压等多文件落盘），避免全工程 Refresh。
        /// </summary>
        public static void ImportAssetsUnderFolderAfterDiskWrite(string folderAssetPath)
        {
            if (string.IsNullOrEmpty(folderAssetPath))
                return;

            folderAssetPath = folderAssetPath.Replace('\\', '/').TrimEnd('/');
            string absFolder = ToAbsoluteAssetPath(folderAssetPath);
            if (!Directory.Exists(absFolder))
                return;

            foreach (string file in Directory.GetFiles(absFolder, "*", SearchOption.AllDirectories))
            {
                string rel = AbsolutePathToAssetsRelative(file);
                if (string.IsNullOrEmpty(rel) || !rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    continue;
                AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceUpdate);
                EnsureModelImporterReadable(rel);
            }
        }

        /// <summary>
        /// 安全删除文件：路径为空/文件不存在直接跳过；IO 异常吞掉不抛出。
        /// </summary>
        public static void SafeDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { /* ignore */ }
        }

        /// <summary>
        /// 在目标目录下生成唯一的占位路径，跨 <paramref name="siblingExtensions"/> 中所有扩展名检查同基名占用。
        /// 解决 <see cref="AssetDatabase.GenerateUniqueAssetPath"/> 只检查单一扩展名、导致同基名异扩展文件被误判为"未占用"的问题。
        /// 递增规则与 Unity 内置命名一致：「名称」→「名称 1」→「名称 2」，且已有后缀数字时从该数字起递增。
        /// 返回路径的扩展名始终等于 <paramref name="preferredAssetPath"/> 的扩展名。
        /// </summary>
        /// <param name="preferredAssetPath">期望路径（资产相对路径，如 "Assets/Foo/New Image.jpg"）。</param>
        /// <param name="siblingExtensions">与占位文件共享命名空间的扩展名集合（含点，如 ".jpg"、".png"）。</param>
        public static string GenerateUniqueCrossExtensionPath(string preferredAssetPath, string[] siblingExtensions)
        {
            if (string.IsNullOrEmpty(preferredAssetPath))
                return preferredAssetPath;

            preferredAssetPath = preferredAssetPath.Replace('\\', '/');
            string directory = Path.GetDirectoryName(preferredAssetPath);
            if (string.IsNullOrEmpty(directory))
                directory = "Assets";

            string baseStem = Path.GetFileNameWithoutExtension(preferredAssetPath);
            if (string.IsNullOrEmpty(baseStem))
                baseStem = "New Asset";

            string preferredExt = Path.GetExtension(preferredAssetPath);
            if (string.IsNullOrEmpty(preferredExt))
                preferredExt = ".asset";

            if (!AssetStemIsOccupied(directory, baseStem, siblingExtensions))
                return $"{directory}/{baseStem}{preferredExt}";

            string rootStem = baseStem;
            int suffixFrom = 1;
            if (TryParseUnityTrailingNumericSuffix(baseStem, out string parsedRoot, out int k))
            {
                rootStem = parsedRoot;
                suffixFrom = k;
            }

            for (int n = suffixFrom; ; n++)
            {
                string candidateStem = $"{rootStem} {n}";
                if (!AssetStemIsOccupied(directory, candidateStem, siblingExtensions))
                    return $"{directory}/{candidateStem}{preferredExt}";
            }
        }

        private static bool AssetStemIsOccupied(string directoryAssetPath, string stem, string[] extensions)
        {
            foreach (var ext in extensions)
            {
                string assetPath = $"{directoryAssetPath}/{stem}{ext}";
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null)
                    return true;
                string abs = ToAbsoluteAssetPath(assetPath);
                if (!string.IsNullOrEmpty(abs) && File.Exists(abs))
                    return true;
            }
            return false;
        }

        /// <summary>例如 "New Image 1" → root "New Image", trailing 1；无尾部数字则返回 false。</summary>
        private static bool TryParseUnityTrailingNumericSuffix(string stem, out string rootStem, out int trailing)
        {
            rootStem = stem;
            trailing = 0;
            if (string.IsNullOrEmpty(stem))
                return false;
            var m = Regex.Match(stem.TrimEnd(), @"^(.+)\s(\d+)$");
            if (!m.Success)
                return false;
            rootStem = m.Groups[1].Value.TrimEnd();
            if (rootStem.Length == 0 || !int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out trailing))
                return false;
            return trailing >= 0;
        }
    }
}
#endif
