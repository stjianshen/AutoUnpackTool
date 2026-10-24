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
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>
    /// 压缩包识别：扩展名 / 魔术数 / 7z 实际验证 / 隐写探测命中的容器识别，
    /// 以及输出目录与压缩包文件夹名推导。
    /// </summary>
    public sealed class ArchiveDetector
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly VolumeDetector _volumeDetector;
        private readonly SevenZipRunner _runner;
        private readonly ConcurrentDictionary<string, bool> _stegoProbeCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 自指冲突（无扩展名压缩包）输出目录的本轮解析结果。
        /// 这条分支的判据依赖源包是否存在，而收尾阶段2 会把源包移进回收站：
        /// 之后再算就会退回「同名目录 = 源包路径」这个已经不存在的目录，
        /// 阶段3 的过滤根对不上登记过的真实输出目录，扁平化整体空转（0 个解压目录节点）。
        /// 所以首次解析（解压时，源包还在）的结果必须在整轮运行内保持不变。
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _selfRefOutputDirs = new(StringComparer.OrdinalIgnoreCase);

        private HashSet<string>? _archiveExtSetCache;
        private string _archiveExtSetCacheKey = string.Empty;

        public ArchiveDetector(AppSettings settings, IAppLogger logger, VolumeDetector volumeDetector, SevenZipRunner runner)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _volumeDetector = volumeDetector ?? throw new ArgumentNullException(nameof(volumeDetector));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        public StegoService Stego { get; set; } = null!;

        /// <summary>
        /// 去掉目录名末尾的压缩包后缀（支持复合后缀，如 .tar.zst）
        /// </summary>
        public string NormalizeArchiveLikeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return name;

            string result = name;
            var archiveExtensions = _settings.GetArchiveExtensions()
                .OrderByDescending(x => x.Length)
                .ToList();

            bool changed;
            do
            {
                changed = false;
                foreach (var ext in archiveExtensions)
                {
                    if (result.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        string trimmed = result.Substring(0, result.Length - ext.Length);
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            result = trimmed;
                            changed = true;
                            break;
                        }
                    }
                }
            } while (changed);

            return result;
        }

        /// <summary>
        /// 丢弃本轮积累的探测结果。本类的可变状态只有 _stegoProbeCache 与扩展名集合缓存；
        /// 新增字段级状态时必须同步登记到这里。
        /// </summary>
        public void ResetRunState()
        {
            _stegoProbeCache.Clear();
            _archiveExtSetCache = null;
            _archiveExtSetCacheKey = string.Empty;
        }

        /// <summary>
        /// 用标准 7z l（不带 -t#）验证文件是否为真正的压缩包
        /// -t# 哈希扫描可能将媒体文件的二进制数据误识别为压缩包签名，
        /// 产生假阳性垃圾文件。标准 7z l 从文件头开始解析，不会误匹配。
        /// 返回 true 表示该文件是可通过标准方式列出内容的真正压缩包。
        /// </summary>
        public bool VerifyIsRealArchive(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            if (!_runner.IsAvailable)
                return false;

            // 标准 7z l 从文件头解析，退出码 0 表示可正常列出内容 → 真正的压缩包；
            // 超时/不可用/异常统一由 RunCapture 归一为非 0（-1），此处判 false。
            var (exitCode, _, _) = _runner.RunCapture($"l -sccUTF-8 \"{filePath}\"");
            return exitCode == 0;
        }

        /// <summary>
        /// 从 7z l -slt 输出中解析第一条 "Type = xxx" 行，提取压缩格式
        /// 逐行解析，只取顶层容器的类型（-slt 输出中顶层 Type 行在最前）
        /// </summary>
        public static string? ParseArchiveTypeFromOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            // 注意：不能用跨行正则匹配。 -t# 输出中条目存在空 "Type = " 行，
            // 正则的 \s* 会吞掉换行符，把下一行的键名（如 Comment）误当作类型值，
            // 导致解析出的类型无效（如 "comment"）而回退到 -t# 提取。
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("Type = ", StringComparison.OrdinalIgnoreCase))
                    continue;

                string type = trimmed.Substring("Type = ".Length).Trim().ToLowerInvariant();

                // -t# 哈希扫描模式的占位类型，不是真实格式，交由调用方回退处理
                if (type == "#")
                    return null;

                // 只返回已知支持的格式
                if (IsKnownArchiveType(type))
                {
                    return type;
                }

                // 顶层类型不是已知格式，后面的条目 Type 行不作数
                return null;
            }
            return null;
        }

        /// <summary>
        /// 判断是否为 7z 可识别的压缩格式类型
        /// </summary>
        public static bool IsKnownArchiveType(string type)
        {
            switch (type.ToLowerInvariant())
            {
                case "zip":
                case "7z":
                case "rar":
                case "tar":
                case "gzip":
                case "gz":
                case "bzip2":
                case "bz2":
                case "xz":
                case "lzma":
                case "zstd":
                case "zst":
                case "cab":
                case "arj":
                case "z":
                case "lzh":
                case "iso":
                case "wim":
                    return true;
                default:
                    return false;
            }
        }

        public bool IsStegoFileBy7ZipProbe(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            if (_stegoProbeCache.TryGetValue(filePath, out bool cached))
                return cached;

            string? detectedType = Stego.DetectStegoArchiveType(filePath);
            bool detected = detectedType != null;
            _stegoProbeCache[filePath] = detected;
            return detected;
        }

        /// <summary>
        /// 压缩包判定结果（统一判定入口 Classify 的返回值）。
        /// </summary>
        public enum ArchiveKind
        {
            /// <summary>不是压缩包</summary>
            NotArchive,
            /// <summary>已验证的真实压缩包（魔术数/分卷/强制模式命中）</summary>
            VerifiedArchive,
            /// <summary>隐写容器（魔术数失败但隐写探测命中）</summary>
            StegoCarrier
        }

        /// <summary>
        /// 检测强度等级（Classify 的 level 参数）：产物数量短路/安装包模式下降级扫描用，
        /// 避免对成千上万个杂文件逐个打开读魔术数。
        /// </summary>
        public enum DetectionLevel
        {
            /// <summary>现状：逐文件魔术数验证（可发现伪装后缀的压缩包）</summary>
            Full,
            /// <summary>强信号：仅 分卷文件名模式 / 扩展名命中包含列表 的文件才做魔术数验证，
            /// 其余不开文件（隐写载体分支不受降级影响）</summary>
            StrongSignalsOnly
        }

        /// <param name="filePath">文件路径</param>
        /// <param name="logVerification">是否输出验证日志，默认 false</param>
        /// <param name="origin">文件来源，决定排除列表/强制模式/隐写探测的适用范围</param>
        /// <returns>是否为压缩文件</returns>
        public bool IsArchiveFile(string filePath, bool logVerification = false, FileOrigin origin = FileOrigin.DroppedFile)
        {
            return Classify(filePath, origin, logVerification) != ArchiveKind.NotArchive;
        }

        /// <summary>
        /// 是否将解压产物扫描降级为强信号模式：产物文件数超过阈值（阈值&gt;0时），
        /// 或安装包解压模式开启。阈值短路与安装包模式共用一条降级路径，判定统一收拢在此。
        /// </summary>
        public bool ShouldUseStrongSignalScan(int newFileCount)
        {
            if (_settings.InstallerExtractMode)
                return true;
            int threshold = _settings.FinalArtifactFileCountThreshold;
            return threshold > 0 && newFileCount > threshold;
        }

        /// <summary>
        /// 缓存的压缩包扩展名包含集合：GetArchiveExtensions 每次调用都会新建 HashSet，
        /// 强信号扫描逐文件调用时开销明显，配置字符串未变时复用同一实例。
        /// </summary>
        private HashSet<string> GetArchiveExtensionsCached()
        {
            string key = _settings.ArchiveExtensions ?? string.Empty;
            var cache = _archiveExtSetCache;
            if (cache != null && string.Equals(_archiveExtSetCacheKey, key, StringComparison.Ordinal))
                return cache;
            var fresh = _settings.GetArchiveExtensions();
            _archiveExtSetCache = fresh;
            _archiveExtSetCacheKey = key;
            return fresh;
        }

        /// <summary>
        /// 统一的压缩包判定入口：整合强制模式、排除列表、扩展名/分卷、魔术数、隐写探测的决策，
        /// 供收集、密码测试、解压产物扫描、智能路径各阶段共用，消除多处重复且严格程度不一致的判定。
        /// 决策矩阵：
        /// - 强制模式：仅对 DroppedFile 生效，直接判为压缩包；
        /// - 排除列表：仅对 FolderScan / ExtractedArtifact 生效；DroppedFile 完全跳过（拖入即确认）；
        ///   安装包模式下隐写载体后缀不受排除列表拦截（留给隐写探测分支）；
        /// - 分卷：文件名即强信号，各卷无法可靠魔术数验证，直接判为压缩包；
        /// - 其余：一律魔术数验证（修复伪装后缀）；魔术数失败时，隐写探测默认仅对 DroppedFile / FolderScan 启用，
        ///   安装包模式下对 ExtractedArtifact 也放开；
        /// - level=StrongSignalsOnly：仅扩展名命中包含列表的文件才开文件做魔术数验证，杂文件直接不命中。
        /// </summary>
        public ArchiveKind Classify(string filePath, FileOrigin origin, bool logVerification = false,
                                    DetectionLevel level = DetectionLevel.Full)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return ArchiveKind.NotArchive;

            if (!File.Exists(filePath))
                return ArchiveKind.NotArchive;

            // 强制解压模式：仅对最初拖入的单个文件生效，解压产物与文件夹扫描仍正常检测
            if (_settings.ForceExtractMode && origin == FileOrigin.DroppedFile)
            {
                if (logVerification)
                    _logger.Log($"  [强制模式] {Path.GetFileName(filePath)}: 跳过检测，直接视为压缩文件", ConsoleColor.Yellow);
                return ArchiveKind.VerifiedArchive;
            }

            string extension = PathUtil.LowerExtension(filePath);

            // 排除列表：仅对文件夹扫描与解压产物生效；用户直接拖入的文件不受排除列表拦截
            if (origin != FileOrigin.DroppedFile)
            {
                var excludedExtensions = _settings.GetExcludedExtensions();
                // 安装包解压模式下隐写载体后缀不被排除列表拦截：安装包产物常把压缩包藏在视频容器里，
                // 必须放行到下方隐写探测分支（探测本身仍受开关/后缀/大小下限/缓存约束）
                if (excludedExtensions.Contains(extension)
                    && !(_settings.InstallerExtractMode && Stego.IsStegoCarrierExtension(filePath)))
                    return ArchiveKind.NotArchive;
            }

            // 分卷压缩包：文件名即强信号，非主卷无法独立魔术数验证，直接判定（仅主卷会入队）
            if (_volumeDetector.IsMultiVolumeArchive(filePath))
            {
                if (logVerification)
                    _logger.Log($"  [确认] {Path.GetFileName(filePath)}: 命中分卷格式，视为压缩文件", ConsoleColor.Green);
                return ArchiveKind.VerifiedArchive;
            }

            // 统一：无论扩展名是否命中包含列表，都用魔术数验证（修复解压产物伪装后缀）；
            // 强信号等级下仅对扩展名命中包含列表的文件开文件验证，杂文件不验证也不判非压缩包（仍可走隐写分支）
            bool magicEligible = level != DetectionLevel.StrongSignalsOnly
                                 || GetArchiveExtensionsCached().Contains(extension);
            if (magicEligible && IsArchiveFileByMagicNumber(filePath, extension))
            {
                if (logVerification)
                    _logger.Log($"  [确认] {Path.GetFileName(filePath)}: 魔术数验证通过，是压缩文件", ConsoleColor.Green);
                return ArchiveKind.VerifiedArchive;
            }

            // 魔术数失败：隐写探测默认仅对 DroppedFile / FolderScan 启用（递归发现的媒体文件是最终产物，不再当容器）；
            // 安装包解压模式下对解压产物也放开（安装包产物中最多藏包的就是视频容器）
            if ((origin != FileOrigin.ExtractedArtifact || _settings.InstallerExtractMode)
                && Stego.IsStegoDetectionActiveForFile(filePath))
            {
                if (logVerification)
                    _logger.Log($"  [确认] {Path.GetFileName(filePath)}: 魔术数失败但隐写探测命中", ConsoleColor.Green);
                return ArchiveKind.StegoCarrier;
            }

            return ArchiveKind.NotArchive;
        }

        /// <summary>
        /// 基于扩展名判断压缩文件
        /// </summary>
        public bool IsArchiveFileByExtension(string filePath, bool isDroppedFile = true)
        {
            // 如果启用了强制觧压模式，跳过所有检测，直接返回 true
            // 但只对最初拖入的文件生效
            if (_settings.ForceExtractMode && isDroppedFile)
            {
                return true;
            }
        
            string extension = PathUtil.LowerExtension(filePath);
            
            // 获取排除的扩展名列表
            var excludedExtensions = _settings.GetExcludedExtensions();
            
            // 先检查是否在排除列表中（优先级最高）
            if (excludedExtensions.Contains(extension))
            {
                return false;
            }
            
            // 获取需要检测的压缩文件扩展名列表
            var archiveExtensions = _settings.GetArchiveExtensions();
            
            // 检查完整扩展名（处理 .tar.zst 这类复合扩展名）
            string fileName = Path.GetFileName(filePath).ToLowerInvariant();
            foreach (var ext in archiveExtensions)
            {
                if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 再检查单层扩展名
            if (archiveExtensions.Contains(extension))
                return true;

            // 检查分卷压缩包格式
            if (_volumeDetector.IsMultiVolumeArchive(filePath))
                return true;

            return false;
        }

        /// <summary>
        /// 基于文件头特征（魔数）判断压缩文件
        /// 常见压缩文件的文件头魔数：
        /// - ZIP: 50 4B 03 04 或 50 4B 05 06 (空ZIP) 或 50 4B 07 08
        /// - RAR: 52 61 72 21 1A 07 (RAR 1.5) 或 52 61 72 21 1A 07 01 00 (RAR 5.0)
        /// - 7Z: 37 7A BC AF 27 1C
        /// - GZ: 1F 8B
        /// - BZ2: 42 5A 68
        /// - XZ: FD 37 7A 58 5A 00
        /// - TAR: (在257字节偏移处有 "ustar")
        /// - CAB: 4D 53 43 46
        /// - ISO: 43 44 30 30 31 (在32769字节偏移处)
        /// - ARJ: 60 EA
        /// - LZH: (需要特殊检测)
        /// - ZST: 28 B5 2F FD
        /// </summary>
        public bool IsArchiveFileByMagicNumber(string filePath, string? extension = null)
        {
            try
            {
                // 文件太小不可能包含有效压缩数据
                if (new FileInfo(filePath).Length < 16)
                    return false;

                // 使用 FileShare.ReadWrite 允许其他进程同时访问文件
                // 添加重试机制，避免文件被临时占用导致失败
                for (int retry = 0; retry < 3; retry++)
                {
                    try
                    {
                        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new BinaryReader(fs))
                        {
                            // 读取文件头（最多读取64字节用于检测）
                            int bytesToRead = (int)Math.Min(64, fs.Length);
                            byte[] header = reader.ReadBytes(bytesToRead);

                            if (header.Length < 2)
                                return false;

                            // ZIP: 50 4B 03 04 或 50 4B 05 06 或 50 4B 07 08
                            if (header.Length >= 4 &&
                                header[0] == 0x50 && header[1] == 0x4B &&
                                (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07) &&
                                (header[3] == 0x04 || header[3] == 0x06 || header[3] == 0x08))
                                return true;

                            // RAR 5.0: 52 61 72 21 1A 07 01 00
                            if (header.Length >= 8 &&
                                header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21 &&
                                header[4] == 0x1A && header[5] == 0x07 && header[6] == 0x01 && header[7] == 0x00)
                                return true;

                            // RAR 1.5: 52 61 72 21 1A 07 00
                            if (header.Length >= 7 &&
                                header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21 &&
                                header[4] == 0x1A && header[5] == 0x07 && header[6] == 0x00)
                                return true;

                            // 7Z: 37 7A BC AF 27 1C
                            if (header.Length >= 6 &&
                                header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC &&
                                header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C)
                                return true;

                            // GZ: 1F 8B，且第三字节 CM（压缩方法）必须为 08(deflate)，降低两字节误报
                            if (header.Length >= 3 && header[0] == 0x1F && header[1] == 0x8B && header[2] == 0x08)
                                return true;

                            // BZ2: 42 5A 68
                            if (header.Length >= 3 && header[0] == 0x42 && header[1] == 0x5A && header[2] == 0x68)
                                return true;

                            // XZ: FD 37 7A 58 5A 00
                            if (header.Length >= 6 &&
                                header[0] == 0xFD && header[1] == 0x37 && header[2] == 0x7A &&
                                header[3] == 0x58 && header[4] == 0x5A && header[5] == 0x00)
                                return true;

                            // ZSTD: 28 B5 2F FD
                            if (header.Length >= 4 &&
                                header[0] == 0x28 && header[1] == 0xB5 &&
                                header[2] == 0x2F && header[3] == 0xFD)
                                return true;

                            // CAB: 4D 53 43 46
                            if (header.Length >= 4 &&
                                header[0] == 0x4D && header[1] == 0x53 &&
                                header[2] == 0x43 && header[3] == 0x46)
                                return true;

                            // ARJ: 60 EA
                            if (header.Length >= 2 && header[0] == 0x60 && header[1] == 0xEA)
                                return true;

                            // TAR: 统一交由下方偏移 257 处的 "ustar" 签名判定（含 GNU/POSIX），
                            // 不再仅凭扩展名+长度放行，降低非压缩包误判；非标准 tar 交由扩展名+强制模式兜底

                            if (fs.Length >= 263)
                            {
                                fs.Position = 257;
                                byte[] tarHeader = reader.ReadBytes(5);
                                if (tarHeader.Length == 5 &&
                                    tarHeader[0] == 0x75 && tarHeader[1] == 0x73 &&
                                    tarHeader[2] == 0x74 && tarHeader[3] == 0x61 &&
                                    tarHeader[4] == 0x72)
                                    return true;
                            }

                            return false;
                        }
                    }
                    catch (IOException) when (retry < 2)
                    {
                        // 文件被占用，等待后重试
                        System.Threading.Thread.Sleep(100);
                        continue;
                    }
                }
                
                // 所有重试都失败
                return false;
            }
            catch (Exception ex)
            {
                // 读取失败时，不判断为压缩文件
                _logger.Log($"  文件头检测失败 {Path.GetFileName(filePath)}: {ex.Message}", ConsoleColor.Gray);
                return false;
            }
        }
        /// <summary>
        /// 根据输出模式计算输出目录
        /// </summary>
        public string GetOutputDirectory(string archivePath)
        {
            string archiveDir = Path.GetDirectoryName(archivePath) ?? string.Empty;
            string archiveNameWithoutExt = GetArchiveFolderName(archivePath);

            // 判断是否为隐写文件（视频容器等）
            bool isStegoCarrier = Stego.IsStegoCarrierExtension(archivePath) && _settings.EnableStegoDetection;

            // 对于隐写文件，强制使用 ArchiveFolder 逻辑，避免影响同级目录的其他文件
            OutputMode effectiveMode = isStegoCarrier ? OutputMode.ArchiveFolder : _settings.OutputMode;

            switch (effectiveMode)
            {
                case OutputMode.SpecificDir:
                    // 输出到指定目录
                    return _settings.OutputDir;

                case OutputMode.ArchiveDir:
                    // 输出到压缩文件所在目录
                    return archiveDir;

                case OutputMode.ArchiveFolder:
                    // 输出到压缩文件同名文件夹（在压缩包目录下创建）
                    string targetFolder = Path.Combine(archiveDir, archiveNameWithoutExt);

                    // 【自指冲突】无扩展名压缩包（被改名的 232/233 这类）去掉后缀后名字与自身相同，
                    // targetFolder 就是压缩包自己的路径。NTFS 上文件与目录共享命名空间，不能建同名目录；
                    // 但此处绝不能降级到 archiveDir：包内条目往往与包同名，产物会落回源包路径，
                    // 而 7z 正持有源包的读取句柄（未共享 DELETE），于是报
                    // “Cannot delete output file : 另一个程序正在使用此文件”（占用者就是它自己）。
                    // 判据绝不能写成 File.Exists(targetFolder)：收尾阶段2 删掉源包后它会翻转，
                    // 导致同一压缩包在「解压时」和「收尾时」算出两个不同的输出目录。
                    // 用「不是目录」代替「是文件」：源包存在时和进回收站后都为真，解析结果稳定。
                    if (!Directory.Exists(archivePath) && PathsReferToSameFile(targetFolder, archivePath))
                    {
                        string key;
                        try { key = Path.GetFullPath(archivePath); }
                        catch { key = archivePath; }

                        if (!_selfRefOutputDirs.TryGetValue(key, out string? selfFolder) || selfFolder == null)
                        {
                            selfFolder = NextFreeSiblingPath(archiveDir, archiveNameWithoutExt + "_解压");
                            _selfRefOutputDirs[key] = selfFolder;
                            _logger.Log($"  同名输出目录即压缩包自身 '{archiveNameWithoutExt}'，改用独立目录 '{Path.GetFileName(selfFolder)}'（避免产物写回正在读取的源包）", ConsoleColor.Yellow);
                        }
                        return selfFolder;
                    }

                    // 检查是否存在同名文件冲突
                    if (File.Exists(targetFolder))
                    {
                        _logger.Log($"  警告: 存在同名文件 '{archiveNameWithoutExt}',自动切换为'解压到当前目录'模式", ConsoleColor.Yellow);
                        return archiveDir;
                    }
                    
                    return targetFolder;

                default:
                    return archiveDir;
            }
        }

        /// <summary>
        /// 丢弃自指冲突输出目录的本轮解析结果。新一轮解压开始时必须调用，
        /// 否则重新拖入的同名压缩包会被并进上一轮的产物目录里。
        /// （与 SmartPathService.ResetRunState 同一层语义：本轮任务状态，不是缓存）
        /// </summary>
        public void ResetOutputDirectoryCache()
        {
            _selfRefOutputDirs.Clear();
        }

        /// <summary>两个路径是否指向同一个文件（归一化后忽略大小写比较）。</summary>
        private static bool PathsReferToSameFile(string pathA, string pathB)
        {
            try
            {
                return string.Equals(Path.GetFullPath(pathA), Path.GetFullPath(pathB), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>在同一父目录下取一个既不等于现有文件、也不等于现有目录的自由路径名。</summary>
        private static string NextFreeSiblingPath(string dir, string baseName)
        {
            string candidate = Path.Combine(dir, baseName);

            for (int i = 1; Directory.Exists(candidate) || File.Exists(candidate); i++)
            {
                candidate = i < 1000
                    ? Path.Combine(dir, $"{baseName}_{i}")
                    : Path.Combine(dir, $"{baseName}_{Guid.NewGuid().ToString("N")[..8]}");
            }

            return candidate;
        }

        /// <summary>
        /// 获取压缩包的文件夹名称（智能去掉分卷后缀和压缩格式后缀）
        /// 例如: ba360.7z.001 -> ba360
        ///       test.rar -> test
        ///       document.zip -> document
        ///       sample.tar.gz -> sample
        ///       3_hidden_23.mp4 -> 3_hidden_23 (隐写文件)
        /// </summary>
        public string GetArchiveFolderName(string archivePath)
        {
            string fileName = Path.GetFileName(archivePath);
            
            // 对于隐写文件（视频容器），直接去掉扩展名
            bool isStegoCarrier = Stego.IsStegoCarrierExtension(archivePath) && _settings.EnableStegoDetection;
            if (isStegoCarrier)
            {
                return Path.GetFileNameWithoutExtension(fileName);
            }
            
            // 1. 处理 7z 分卷格式：xxx.7z.001 -> xxx
            var match7z = System.Text.RegularExpressions.Regex.Match(fileName, @"^(.+)\.7z\.\d{3,}$");
            if (match7z.Success)
            {
                return match7z.Groups[1].Value;
            }
            
            // 2. 处理 RAR 分卷格式：xxx.part1.rar -> xxx
            var matchRarPart = System.Text.RegularExpressions.Regex.Match(fileName, @"^(.+)\.part\d+\.rar$");
            if (matchRarPart.Success)
            {
                return matchRarPart.Groups[1].Value;
            }
            
            // 3. 处理 ZIP 分卷格式：xxx.z01 -> xxx
            var matchZip = System.Text.RegularExpressions.Regex.Match(fileName, @"^(.+)\.z\d{2,}$");
            if (matchZip.Success)
            {
                return matchZip.Groups[1].Value;
            }
            
            // 4. 处理 RAR 老式分卷：xxx.r00 -> xxx
            var matchRarOld = System.Text.RegularExpressions.Regex.Match(fileName, @"^(.+)\.r\d{2,}$");
            if (matchRarOld.Success)
            {
                return matchRarOld.Groups[1].Value;
            }
            
            // 5. 处理通用数字分卷：xxx.001 -> xxx
            var matchGeneric = System.Text.RegularExpressions.Regex.Match(fileName, @"^(.+)\.\d{3,}$");
            if (matchGeneric.Success)
            {
                return matchGeneric.Groups[1].Value;
            }
            
            // 6. 普通压缩包：去掉后缀（支持复合后缀）
            // xxx.7z -> xxx
            // xxx.tar.zst -> xxx
            string normalized = NormalizeArchiveLikeName(fileName);
            
            // 7. 如果 NormalizeArchiveLikeName 未能去除扩展名（如 .exe 等非标准压缩格式），
            //    使用 Path.GetFileNameWithoutExtension 兜底去除
            if (normalized.IndexOf('.') > 0 && normalized == fileName)
            {
                return Path.GetFileNameWithoutExtension(fileName);
            }
            
            return normalized;
        }
    }
}
