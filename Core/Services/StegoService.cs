using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.Core.Infrastructure;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>
    /// 隐写容器探测与裁剪：通过 7z -t# 标准列表定位内嵌压缩包，
    /// 再按字节区间裁出真实压缩包，避免提取产生序号命名的碎片文件。
    /// </summary>
    public sealed class StegoService
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly ArchiveDetector _detector;
        private readonly SevenZipRunner _runner;

        private readonly ConcurrentDictionary<string, string?> _stegoArchiveTypeCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, HashSet<string>> _stegoArchiveValidFilesCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, List<(string Name, string Type, long Offset, long Size)>> _stegoSubStreamCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> StegoCarrierExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".webm", ".mov", ".m4v"
        };

        public StegoService(AppSettings settings, IAppLogger logger, ArchiveDetector detector, SevenZipRunner runner)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        /// <summary>
        /// 丢弃本轮积累的全部隐写缓存。
        /// 三个缓存都是“文件内容”的推断结果，文件在两轮之间可能被改过或被删，
        /// 强制中止后一律作废比留着更便宜。新增隐写缓存时必须同步登记到这里。
        /// </summary>
        public void ResetRunState()
        {
            _stegoArchiveTypeCache.Clear();
            _stegoArchiveValidFilesCache.Clear();
            _stegoSubStreamCache.Clear();
        }

        /// 流程：
        /// 1. 先检查扩展名是否在排除列表中，如果是则直接返回 false
        /// 2. 再检查扩展名是否在包含列表中，如果是则用魔术数验证
        /// 3. 如果扩展名不在包含列表中，直接用魔术数检测
        /// </summary>
        public bool IsStegoCarrierExtension(string filePath)
        {
            string ext = PathUtil.LowerExtension(filePath);
            return StegoCarrierExtensions.Contains(ext);
        }

        public bool IsStegoDetectionActiveForFile(string filePath)
        {
            if (!_settings.EnableStegoDetection)
                return false;

            if (!IsStegoCarrierExtension(filePath))
                return false;

            long minSizeBytes = (long)Math.Max(1, _settings.StegoDetectionMinFileSizeMB) * 1024L * 1024L;
            long fileSizeBytes = new FileInfo(filePath).Length;
            if (fileSizeBytes < minSizeBytes)
            {
                _logger.Log(
                    $"  [隐写探测] {Path.GetFileName(filePath)}: 文件过小 ({fileSizeBytes / 1024 / 1024}MB)，低于下限 {_settings.StegoDetectionMinFileSizeMB}MB，跳过探测",
                    ConsoleColor.Gray);
                return false;
            }

            return _detector.IsStegoFileBy7ZipProbe(filePath);
        }

        /// <summary>
        /// 从 7z l -t# -slt 输出中检测隐藏压缩包的具体类型（如 zip/7z/rar/tar）
        /// 同时解析出存档内包含的真实文件列表（白名单），用于过滤 -t# 提取的误匹配垃圾
        /// 并缓存子数据段（Offset/Size），供类型无法确认时按区间裁剪真实压缩包
        /// 返回 null 表示非隐写文件或无法确定类型
        /// 结果会被缓存，同一文件只探测一次
        /// </summary>
        public string? DetectStegoArchiveType(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            if (_stegoArchiveTypeCache.TryGetValue(filePath, out string? cachedType))
            {
                return cachedType;
            }

            string? detectedType = null;
            HashSet<string> validFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!_runner.IsAvailable)
                    return null;

                var (probeExitCode, output, error) = _runner.RunCapture($"l -t# -slt -sccUTF-8 \"{filePath}\"");
                bool probeExited = probeExitCode != -1;

                if (!probeExited)
                {
                    _logger.Log($"  [隐写探测] {Path.GetFileName(filePath)}: 探测超时，跳过", ConsoleColor.Gray);
                }
                else if (probeExitCode == 0)
                {
                    string combined = output + Environment.NewLine + error;
                    string lower = combined.ToLowerInvariant();

                    // 缓存子数据段（Offset/Size）：类型无法确认时按区间裁剪真实压缩包，
                    // 不再用 7z x -t# 提取出 "1"、"2" 这类序号命名的碎片
                    var parsedSubStreams = ParseStegoSubStreamsFromSlt(combined);
                    _stegoSubStreamCache[filePath] = parsedSubStreams;

                    // 解析 "Type = xxx" 行获取具体压缩格式
                    // -t# 输出的顶层 Type 恒为 "#"，解析不到真实格式时会返回 null
                    detectedType = ArchiveDetector.ParseArchiveTypeFromOutput(combined);
                    
                    // -t# 拿不到真实格式时，用标准 7z l（不带 -t#）再探测一次：
                    // 对 7z 包伪装成视频、zip 追加在视频尾部等场景，标准探测能拿到真实类型，
                    // 提取时才不会用 -t# 把嵌套压缩包拆成碎片
                    if (detectedType == null)
                    {
                        var stdProbe = ProbeArchiveByStandardList(filePath);
                        detectedType = stdProbe.Type;
                        validFiles = stdProbe.ValidFiles;
                        _logger.Log($"  [隐写探测] {Path.GetFileName(filePath)}: 标准探测{(detectedType != null ? $"解析到类型 {detectedType}" : "失败")}", ConsoleColor.Gray);
                    }
                    else
                    {
                        // 从 -t# 输出解析真实文件列表（白名单）
                        validFiles = ParseStegoFileListFromSltOutput(combined, filePath);
                    }
                    
                    // 标准探测也失败时，使用 # 作为回退（提取时会用 -t# 哈希扫描）
                    if (detectedType == null)
                    {
                        bool isArchive = lower.Contains(".zip") ||
                                         lower.Contains(".7z") ||
                                         lower.Contains(".rar") ||
                                         lower.Contains(".tar") ||
                                         lower.Contains("listing archive");
                        if (isArchive)
                        {
                            detectedType = "#";
                            validFiles = ParseStegoFileListFromSltOutput(combined, filePath);
                        }
                    }
                    
                    if (detectedType != null)
                    {
                        _logger.Log($"  [隐写探测] {Path.GetFileName(filePath)}: 解析到 {validFiles.Count} 个白名单文件", ConsoleColor.Gray);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"  [隐写探测] {Path.GetFileName(filePath)}: {ex.Message}", ConsoleColor.Gray);
            }

            _stegoArchiveTypeCache[filePath] = detectedType;
            _stegoArchiveValidFilesCache[filePath] = validFiles;
            return detectedType;
        }

        /// <summary>
        /// 用标准 7z l -slt（不带 -t#）探测真实压缩格式及真实文件列表。
        /// -t# 哈希扫描的顶层 Type 恒为 "#"，且会把固实压缩流中的字节签名
        /// 误识别为嵌套压缩包碎片（把完整 zip 拆碎）；
        /// 标准探测从文件头/尾部解析，对 7z 包伪装成视频、zip 追加在视频尾部
        /// 等场景能返回真实类型和真实文件列表。
        /// </summary>
        public (string? Type, HashSet<string> ValidFiles) ProbeArchiveByStandardList(string filePath)
        {
            var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!_runner.IsAvailable)
                    return (null, empty);

                var (exitCode, output, error) = _runner.RunCapture($"l -slt -sccUTF-8 \"{filePath}\"");

                if (exitCode != 0)
                    return (null, empty);

                string combined = output + Environment.NewLine + error;
                string? type = ArchiveDetector.ParseArchiveTypeFromOutput(combined);
                if (type == null)
                    return (null, empty);

                return (type, ParseStegoFileListFromSltOutput(combined, filePath));
            }
            catch
            {
                return (null, empty);
            }
        }

        /// <summary>
        /// 从 7z l -t# -slt 输出中解析真实包含的文件名列表（白名单）
        /// -slt 格式每行是 "Key = Value"，文件的 Path= 行即文件名
        /// 跳过第一个 Path=（存档自身路径），后续的 Path= 均为存档内文件
        /// </summary>
        public static HashSet<string> ParseStegoFileListFromSltOutput(string combined, string archivePath)
        {
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool firstPathFound = false;
            string archiveFileName = Path.GetFileName(archivePath);
            
            foreach (var line in combined.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("Path = ", StringComparison.OrdinalIgnoreCase))
                    continue;
                
                string pathValue = trimmed.Substring("Path = ".Length).Trim();
                
                // 跳过第一个 Path（存档自身路径）
                if (!firstPathFound)
                {
                    firstPathFound = true;
                    continue;
                }
                
                // 保留归档内相对路径（统一分隔符），避免不同子目录同名文件互相误判
                string relPath = PathUtil.NormalizeRelativePath(pathValue);
                if (!string.IsNullOrEmpty(relPath))
                {
                    fileNames.Add(relPath);
                }
            }
            
            return fileNames;
        }

        /// <summary>
        /// 判断解压落盘文件是否命中隐写白名单：
        /// 优先按相对路径精确匹配（避免子目录同名碰撞），
        /// 7z 报告路径与实际落盘路径不一致时回退到纯文件名匹配（倾向于保留真实内容）。
        /// </summary>
        public static bool IsInWhitelist(HashSet<string>? whitelist, string outputDir, string fullPath)
        {
            if (whitelist == null || whitelist.Count == 0)
                return false;

            try
            {
                string rel = PathUtil.NormalizeRelativePath(Path.GetRelativePath(outputDir, fullPath));
                if (rel.Length > 0 && whitelist.Contains(rel))
                    return true;
            }
            catch { /* 跨卷等情况 GetRelativePath 可能抛异常，降级到文件名回退 */ }

            string fn = Path.GetFileName(fullPath);
            foreach (var w in whitelist)
            {
                if (string.Equals(Path.GetFileName(w), fn, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 获取隐写文件内包含的真实文件白名单（已缓存）
        /// </summary>
        public HashSet<string>? GetStegoArchiveValidFiles(string filePath)
        {
            if (_stegoArchiveValidFilesCache.TryGetValue(filePath, out var files))
                return files;
            return null;
        }

        /// <summary>
        /// 隐写容器真实类型无法确认时的提取方式：按 -t# 扫描出的 Offset/Size 把
        /// 数据段中真正的压缩包按字节区间裁剪成独立文件，再交给子压缩包流程解压。
        /// 7z x -t# 会把载体自身的字节和误匹配碎片也写出来，命名成 "1"、"2" 这类序号文件；
        /// 区间裁剪只产出确认过的压缩包，不会产生这些碎片。
        /// </summary>
        public (bool Success, string Message) CarveStegoPayloads(string carrierPath, string outputDir, int taskId, CancellationToken token)
        {
            if (File.Exists(outputDir))
                return (false, $"输出路径已存在同名文件: {outputDir}，请更换输出模式");

            if (!_stegoSubStreamCache.TryGetValue(carrierPath, out var streams) || streams == null || streams.Count == 0)
                return (false, "未获取到 -t# 扫描的数据段信息，无法裁剪");

            // 同一区间可能被重复列出，按 Offset 去重（保留最大 Size）
            var candidates = streams
                .GroupBy(s => s.Offset)
                .Select(g => g.OrderByDescending(s => s.Size).First())
                .ToList();

            _logger.Log($"[线程 {taskId}] [隐写裁剪] {Path.GetFileName(carrierPath)}: 定位到 {candidates.Count} 个候选压缩数据段", ConsoleColor.Cyan);

            string baseName = FileSystemHelper.SanitizePathSegment(_detector.GetArchiveFolderName(carrierPath));
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "payload";

            string stagingDir = Path.Combine(Path.GetTempPath(), "AutoUnpackTool_stego", Guid.NewGuid().ToString("N"));
            var carved = new List<(string TempPath, string Ext, bool Verified)>();

            try
            {
                Directory.CreateDirectory(stagingDir);

                using (var source = new FileStream(carrierPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int index = 0;
                    foreach (var candidate in candidates)
                    {
                        token.ThrowIfCancellationRequested();
                        index++;

                        string ext = FileSystemHelper.ArchiveTypeToExtension(candidate.Type);
                        string tempPath = Path.Combine(stagingDir, $"carve{index}.{ext}");
                        if (!TryCopyByteRange(source, candidate.Offset, candidate.Size, tempPath, token))
                        {
                            _logger.Log($"[线程 {taskId}] [隐写裁剪]   数据段 #{index} 区间读取失败，跳过 (Offset={candidate.Offset}, Size={candidate.Size})", ConsoleColor.Yellow);
                            continue;
                        }

                        bool verified = _detector.VerifyIsRealArchive(tempPath);
                        carved.Add((tempPath, ext, verified));
                        _logger.Log($"[线程 {taskId}] [隐写裁剪]   数据段 #{index}: Type={candidate.Type} Offset={candidate.Offset} Size={candidate.Size} 标准验证={(verified ? "通过" : "未通过")}",
                            verified ? ConsoleColor.Green : ConsoleColor.Yellow);
                    }
                }

                if (carved.Count == 0)
                    return (false, "隐写容器内未能裁出压缩数据段");

                // 有通过标准验证的段时，其余未通过的都是 -t# 误匹配的固实流碎片，不写入输出目录；
                // 全部未通过时退回魔术数判断（如表头加密的压缩包），避免丢失真实内容
                bool anyVerified = carved.Any(c => c.Verified);
                var publish = anyVerified
                    ? carved.Where(c => c.Verified).ToList()
                    : carved.Where(c => _detector.IsArchiveFileByMagicNumber(c.TempPath, "." + c.Ext)).ToList();

                if (publish.Count == 0)
                    return (false, "隐写容器内的数据段均无法确认为压缩包，原文件已保留");

                Directory.CreateDirectory(outputDir);

                int published = 0;
                for (int i = 0; i < publish.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    string fileName = publish.Count == 1
                        ? $"{baseName}.{publish[i].Ext}"
                        : $"{baseName}_{i + 1}.{publish[i].Ext}";
                    string destPath = FileSystemHelper.GetUniqueFilePath(outputDir, fileName);
                    File.Move(publish[i].TempPath, destPath);
                    published++;
                    _logger.Log($"[线程 {taskId}] [隐写裁剪]   已裁出: {Path.GetFileName(destPath)}", ConsoleColor.Cyan);
                }

                int skipped = carved.Count - published;
                if (skipped > 0)
                    _logger.Log($"[线程 {taskId}] [隐写裁剪]   已丢弃 {skipped} 个无法验证的数据段（-t# 误匹配碎片）", ConsoleColor.Gray);

                return (true, $"已裁出 {published} 个压缩数据段");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, $"隐写裁剪失败: {ex.Message}");
            }
            finally
            {
                try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            }
        }

        /// <summary>
        /// 按字节区间从载体文件中复制出压缩数据段
        /// </summary>
        public static bool TryCopyByteRange(FileStream source, long offset, long size, string destPath, CancellationToken token)
        {
            try
            {
                if (offset < 0 || size <= 0 || offset + size > source.Length)
                    return false;

                const int bufferSize = 1024 * 1024;
                byte[] buffer = new byte[bufferSize];

                source.Position = offset;
                long remaining = size;
                using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = source.Read(buffer, 0, toRead);
                    if (read <= 0)
                        return false;
                    dest.Write(buffer, 0, read);
                    remaining -= read;
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从 7z l -t# -slt 输出中解析子数据段（Path/Type/Offset/Size）
        /// -t# 把载体切成若干数据段：识别为压缩包的段名形如 "2.zip"，
        /// 识别不出的段只有序号名（如 "1"）且 Type 为空，不应参与裁剪
        /// </summary>
        public static List<(string Name, string Type, long Offset, long Size)> ParseStegoSubStreamsFromSlt(string combined)
        {
            var result = new List<(string Name, string Type, long Offset, long Size)>();
            string? path = null;
            string type = string.Empty;
            long offset = -1;
            long size = -1;

            void Flush()
            {
                // 只保留带区间信息且类型可识别的数据段（顶层容器条目没有 Offset）
                if (!string.IsNullOrEmpty(path) && offset >= 0 && size > 0 && ArchiveDetector.IsKnownArchiveType(type))
                    result.Add((path!, type, offset, size));

                path = null;
                type = string.Empty;
                offset = -1;
                size = -1;
            }

            // 注意：不能按字符 { '\r', '\n' } 切分。Windows 换行 "\r\n" 会被切成两个分隔符，
            // 每两行之间多出一个空元素，而空行是条目边界（Flush），会导致
            // Path/Type/Offset/Size 永远凑不齐、解析结果恒为空
            foreach (var line in combined.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                string trimmed = line.Trim();

                // 空行 / 分隔线是 -slt 的条目边界
                if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
                {
                    Flush();
                    continue;
                }

                if (trimmed.StartsWith("Path = ", StringComparison.OrdinalIgnoreCase))
                    path = trimmed.Substring("Path = ".Length).Trim();
                else if (trimmed.StartsWith("Type =", StringComparison.OrdinalIgnoreCase))
                    type = trimmed.Substring("Type =".Length).Trim();
                else if (trimmed.StartsWith("Offset = ", StringComparison.OrdinalIgnoreCase))
                    long.TryParse(trimmed.Substring("Offset = ".Length).Trim(), out offset);
                else if (trimmed.StartsWith("Size = ", StringComparison.OrdinalIgnoreCase))
                    long.TryParse(trimmed.Substring("Size = ".Length).Trim(), out size);
            }
            Flush();

            return result;
        }
    }
}
