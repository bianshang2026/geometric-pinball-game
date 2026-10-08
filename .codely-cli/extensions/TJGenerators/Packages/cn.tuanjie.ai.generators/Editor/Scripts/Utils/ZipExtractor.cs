#if UNITY_EDITOR
using System;
using System.IO;
using System.IO.Compression;

namespace TJGenerators.Utils
{
    internal static class ZipExtractor
    {
        public static string ExtractZipAndGetModelPath(byte[] zipData, string originalPath)
        {
            try
            {
                // Absolute paths avoid ...\Assets\Assets\... when cwd is under Assets
                string directory = Path.GetDirectoryName(originalPath).Replace("\\", "/");
                string extractFolderRelative = Path.Combine(directory, Path.GetFileNameWithoutExtension(originalPath)).Replace("\\", "/");
                string extractFolder = PathUtils.ToAbsoluteAssetPath(extractFolderRelative);

                if (Directory.Exists(extractFolder))
                    Directory.Delete(extractFolder, true);
                Directory.CreateDirectory(extractFolder);

                string tempZipPath = Path.Combine(Path.GetTempPath(), $"TJGenerators_temp_{Guid.NewGuid()}.zip");
                File.WriteAllBytes(tempZipPath, zipData);

                ExtractZipToDirectory(tempZipPath, extractFolder);
                File.Delete(tempZipPath);

                string[] objFiles = Directory.GetFiles(extractFolder, "*.obj", SearchOption.AllDirectories);
                if (objFiles.Length > 0)
                {
                    string objFile = objFiles[0];
                    string objDir = Path.GetDirectoryName(objFile);

                    if (objDir != extractFolder)
                    {
                        foreach (string file in Directory.GetFiles(objDir))
                        {
                            string destFile = Path.Combine(extractFolder, Path.GetFileName(file));
                            if (!File.Exists(destFile))
                            {
                                File.Move(file, destFile);
                            }
                        }
                        objFile = Path.Combine(extractFolder, Path.GetFileName(objFile));
                    }

                    TJLog.Log($"[ZipExtractor] 解压完成，找到OBJ文件: {objFile}");
                    string relativePath = PathUtils.AbsolutePathToAssetsRelative(objFile);
                    ImportExtractedModelFolder(relativePath);
                    return relativePath;
                }

                string[] fbxFiles = Directory.GetFiles(extractFolder, "*.fbx", SearchOption.AllDirectories);
                if (fbxFiles.Length > 0)
                {
                    TJLog.Log($"[ZipExtractor] 解压完成，找到FBX文件: {fbxFiles[0]}");
                    string relativePath = PathUtils.AbsolutePathToAssetsRelative(fbxFiles[0]);
                    ImportExtractedModelFolder(relativePath);
                    return relativePath;
                }

                TJLog.LogError("[ZipExtractor] ZIP文件中未找到支持的模型文件（OBJ/FBX）");
                return null;
            }
            catch (Exception e)
            {
                TJLog.LogError($"[ZipExtractor] 解压ZIP文件失败: {e.Message}");
                return null;
            }
        }

        public static void ExtractZipToDirectory(string zipPath, string extractFolder)
        {
            using (var archive = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    string destinationPath = Path.Combine(extractFolder, entry.FullName);
                    string destinationDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationDir))
                        Directory.CreateDirectory(destinationDir);
                    if (File.Exists(destinationPath))
                        File.Delete(destinationPath);
                    using (Stream input = entry.Open())
                    using (FileStream output = File.Create(destinationPath))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }

        public static void ImportExtractedModelFolder(string modelAssetPath)
        {
            string folder = Path.GetDirectoryName(modelAssetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder))
                PathUtils.ImportAssetsUnderFolderAfterDiskWrite(folder);
        }
    }
}
#endif
