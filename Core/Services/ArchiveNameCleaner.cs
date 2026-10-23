using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Configuration;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>
    /// 防和谐文件名清理：解析压缩扩展名后的垃圾字符，分卷文件整组原子重命名。
    /// </summary>
    public sealed class ArchiveNameCleaner
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;

        public ArchiveNameCleaner(AppSettings settings, IAppLogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 尝试从"脏文件名"中解析出干净的文件名（删除压缩扩展名后的防和谐垃圾字符）。
        /// 例如 "xxx.part1.rar删1" -> "xxx.part1.rar"、"xxx.zip删" -> "xxx.zip"。
        /// 返回 null 表示无需清理。
        /// </summary>
        private string? GetCleanedArchiveFileName(string fileName, List<string> archiveExtensions)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            // 垃圾后缀：压缩扩展名之后、不含点号、包含中文"删"字的尾部片段
            const string garbagePattern = @"[^.]*删[^.]*$";

            // 1. 分卷形式（与 IsMultiVolumeArchive 保持一致，顺序即优先级）
            string[] volumePatterns = new[]
            {
                @"\.part\d+\.rar",   // xxx.part1.rar
                @"\.7z\.\d{3,}",     // xxx.7z.001
                @"\.z\d{2,}",        // xxx.z01
                @"\.r\d{2,}",        // xxx.r00
                @"\.\d{3,}",         // xxx.001
            };
            foreach (string volPattern in volumePatterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    fileName,
                    $@"^(?<head>.+{volPattern})(?<garbage>{garbagePattern})",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                    return match.Groups["head"].Value;
            }

            // 2. 标准压缩扩展名（按长度降序，正确处理 .tar.zst 这类复合扩展名）
            foreach (string ext in archiveExtensions)
            {
                string escaped = System.Text.RegularExpressions.Regex.Escape(ext);
                var match = System.Text.RegularExpressions.Regex.Match(
                    fileName,
                    $@"^(?<head>.+{escaped})(?<garbage>{garbagePattern})",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                    return match.Groups["head"].Value;
            }

            return null;
        }

        /// <summary>
        /// 解析分卷组键（目录 + 分卷类型 + 基础名），用于分卷文件的整组原子处理。
        /// 与 DetectMultiVolumeArchive 使用相同的分卷类型判定，非分卷文件返回 null。
        /// </summary>
        private string? GetVolumeGroupKey(string filePath)
        {
            string directory = Path.GetDirectoryName(filePath) ?? string.Empty;
            string fileName = Path.GetFileName(filePath);

            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.part\d+\.rar$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(?<base>.+)\.part\d+\.rar$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                    return $"{directory}|part|{match.Groups["base"].Value}";
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.7z\.\d{3,}$"))
            {
                string baseName = fileName.Substring(0, fileName.LastIndexOf(".7z.", StringComparison.OrdinalIgnoreCase));
                return $"{directory}|7z|{baseName}";
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.z\d{2,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string baseName = fileName.Substring(0, fileName.LastIndexOf(".z", StringComparison.OrdinalIgnoreCase));
                return $"{directory}|zip|{baseName}";
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.r\d{2,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string baseName = fileName.Substring(0, fileName.LastIndexOf(".r", StringComparison.OrdinalIgnoreCase));
                return $"{directory}|rarold|{baseName}";
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.\d{3,}$"))
            {
                return $"{directory}|num|{Path.GetFileNameWithoutExtension(fileName)}";
            }

            return null;
        }

        /// <summary>
        /// 防和谐文件名自动清理（分组原子重命名）。
        /// 删除压缩扩展名后的"删"字等防和谐字符；分卷文件整组一致重命名：
        /// 组内任一文件校验失败或重命名失败，整组放弃并保持原名。
        /// 返回与输入等长的最终路径数组（重命名成功的为新路径，其余为原路径）。
        /// </summary>
        public List<string> CleanDirtyArchiveFileNames(List<string> filePaths)
        {
            var result = new List<string>(filePaths); // 默认保持原样
            if (!_settings.AutoCleanAntiCensorshipNames || filePaths == null || filePaths.Count == 0)
                return result;

            try
            {
                var archiveExtensions = _settings.GetArchiveExtensions()
                    .OrderByDescending(x => x.Length)
                    .ToList();

                // 1. 计算每个文件是否需要清理（仅记录需要重命名的文件）
                var dirtyPlan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string filePath in filePaths)
                {
                    if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                        continue;

                    string fileName = Path.GetFileName(filePath);
                    string? cleanedName = GetCleanedArchiveFileName(fileName, archiveExtensions);
                    if (string.IsNullOrEmpty(cleanedName) ||
                        string.Equals(cleanedName, fileName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string cleanedPath = Path.Combine(Path.GetDirectoryName(filePath) ?? string.Empty, cleanedName);
                    if (!string.Equals(cleanedPath, filePath, StringComparison.OrdinalIgnoreCase))
                        dirtyPlan[filePath] = cleanedPath;
                }

                if (dirtyPlan.Count == 0)
                    return result;

                // 2. 按分卷组键分组（非分卷文件单独成组）
                var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in dirtyPlan)
                {
                    string groupKey = GetVolumeGroupKey(kvp.Value) ?? $"single|{kvp.Key}";
                    if (!groups.TryGetValue(groupKey, out var list))
                    {
                        list = new List<string>();
                        groups[groupKey] = list;
                    }
                    list.Add(kvp.Key);
                }

                // 3. 每组校验并原子执行
                foreach (var group in groups.Values)
                {
                    TryRenameGroupAtomically(group, dirtyPlan);
                }

                // 4. 生成最终路径数组：重命名成功的条目其新路径已存在，失败条目自动回退原路径
                for (int i = 0; i < filePaths.Count; i++)
                {
                    if (dirtyPlan.TryGetValue(filePaths[i], out string? newPath) && File.Exists(newPath))
                        result[i] = newPath;
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"[防和谐清理] 处理失败: {ex.Message}", ConsoleColor.Red);
            }

            return result;
        }

        /// <summary>
        /// 对一个分组执行原子重命名：校验全部通过才统一执行，
        /// 执行中途失败则回滚已重命名的卷并整组放弃。
        /// </summary>
        private void TryRenameGroupAtomically(List<string> groupFiles, Dictionary<string, string> dirtyPlan)
        {
            // 校验阶段：目标路径不能已存在（除非是组内其他成员的旧路径），且目标之间不能重复
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string oldPath in groupFiles)
            {
                string newPath = dirtyPlan[oldPath];
                if (File.Exists(newPath) && !groupFiles.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.Log($"[防和谐清理] {Path.GetFileName(oldPath)}: 目标已存在 {newPath}，整组放弃重命名", ConsoleColor.Yellow);
                    return;
                }
                if (!targets.Add(newPath))
                {
                    _logger.Log($"[防和谐清理] 多个文件清理后重名 {Path.GetFileName(newPath)}，整组放弃重命名", ConsoleColor.Yellow);
                    return;
                }
            }

            // 执行阶段：逐个重命名，失败则回滚整组
            var moved = new List<(string Old, string New)>();
            foreach (string oldPath in groupFiles)
            {
                string newPath = dirtyPlan[oldPath];
                try
                {
                    File.Move(oldPath, newPath);
                    moved.Add((oldPath, newPath));
                    _logger.Log($"[防和谐清理] {Path.GetFileName(oldPath)} → {Path.GetFileName(newPath)}", ConsoleColor.Cyan);
                }
                catch (Exception ex)
                {
                    _logger.Log($"[防和谐清理] {Path.GetFileName(oldPath)} 重命名失败: {ex.Message}，整组回滚", ConsoleColor.Yellow);
                    foreach (var m in moved)
                    {
                        try { File.Move(m.New, m.Old); }
                        catch { /* 回滚尽力而为 */ }
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// 拖入单文件时的防和谐文件名预清理。
        /// 若当前文件需要清理，则收集同目录所有文件参与分组原子清理，
        /// 保证分卷文件整组一致重命名。返回清理后的当前文件路径。
        /// </summary>
        public string PreCleanFileNamesForSingleFile(string filePath)
        {
            if (!_settings.AutoCleanAntiCensorshipNames || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return filePath;

            string fileName = Path.GetFileName(filePath);
            var archiveExtensions = _settings.GetArchiveExtensions()
                .OrderByDescending(x => x.Length)
                .ToList();
            if (string.IsNullOrEmpty(GetCleanedArchiveFileName(fileName, archiveExtensions)))
                return filePath; // 无需清理，避免不必要的目录扫描

            string directory = Path.GetDirectoryName(filePath) ?? string.Empty;
            try
            {
                var allFiles = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly).ToList();
                var cleaned = CleanDirtyArchiveFileNames(allFiles);
                for (int i = 0; i < allFiles.Count; i++)
                {
                    if (string.Equals(allFiles[i], filePath, StringComparison.OrdinalIgnoreCase))
                        return cleaned[i];
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"[防和谐清理] {fileName}: {ex.Message}", ConsoleColor.Yellow);
            }

            return filePath;
        }

    }
}
