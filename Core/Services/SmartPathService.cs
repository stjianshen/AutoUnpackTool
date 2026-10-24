using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.Core.Infrastructure;
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>
    /// 智能路径（扁平化）处理：解压后目录树的后序遍历扁平化、单子目录合并、
    /// 顶层文件夹归并、空目录清理，并维护解压目录标记集合与扁平化结果映射。
    /// </summary>
    public sealed class SmartPathService
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly ArchiveDetector _detector;
        private readonly VolumeDetector _volumeDetector;
        private readonly BlacklistService _blacklistService;
        private readonly ConflictResolver _conflicts;
        private readonly FileOperationService _fileOps;

        public ConcurrentDictionary<string, byte> ExtractedDirectoryNodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> FlattenedFinalPathMap { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ProcessedFlattenDirs { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 智能路径移动操作的串行化锁。
        /// 【并发修复】多个顶级文件并发完成时，收尾（清理+扁平化）必须串行执行，
        /// 否则并发移动同一目录树会导致移动中断（图片没移完）、目录冲突时生成 GUID 新目录、
        /// 图片分散到多个文件夹。凡是会调用本服务做目录移动的流程（解压收尾、拖入文件夹收尾、
        /// 批量扁平化）都必须先持有这把锁，所以锁放在被保护的对象上而不是某个调用方里。
        /// </summary>
        public SemaphoreSlim FinalizeLock { get; } = new(1, 1);

        public Action<string>? OnSmartPathCompleted { get; set; }

        /// <summary>
        /// 全树快照提供者（需在 UI 线程遍历 ObservableCollection，由宿主窗口注入）
        /// </summary>
        public Func<List<FileItem>> GetAllItemsSnapshot { get; set; } = null!;

        public SmartPathService(AppSettings settings, IAppLogger logger, ArchiveDetector detector, VolumeDetector volumeDetector, BlacklistService blacklistService, ConflictResolver conflicts, FileOperationService fileOps)
        {
            _settings = settings;
            _logger = logger;
            _detector = detector;
            _volumeDetector = volumeDetector;
            _blacklistService = blacklistService;
            _conflicts = conflicts;
            _fileOps = fileOps;
        }

        // 与 MainWindow 原字段同名转发，保证方法体逐行未变
        private ConcurrentDictionary<string, byte> _extractedDirectoryNodes => ExtractedDirectoryNodes;
        private Dictionary<string, string> _flattenedFinalPathMap => FlattenedFinalPathMap;
        private HashSet<string> _processedFlattenDirs => ProcessedFlattenDirs;

        /// <summary>
        /// 丢弃本轮运行的工作集。
        /// 上面三个集合不是“缓存”而是“本轮任务状态”，却活在进程级单例里，
        /// 所以它们的清理全靠调用方记得收 —— 新增任何本轮级状态都必须同步登记到这里。
        /// 残留的实际危害：_extractedDirectoryNodes 里的旧登记会让 ConflictResolver
        /// 把日后同路径的用户目录误判为“本工具生成的”，从而允许递归删除它。
        /// FlattenedFinalPathMap / ProcessedFlattenDirs 是普通 Dictionary/HashSet，
        /// 只能从 UI 线程调用（与调用方 ForceReset 的既有约定一致）。
        /// </summary>
        public void ResetRunState()
        {
            ExtractedDirectoryNodes.Clear();
            FlattenedFinalPathMap.Clear();
            ProcessedFlattenDirs.Clear();
        }

        /// <summary>
        /// 智能路径总开关。输出模式不参与这一层判断：ArchiveDir 模式下根目录不扁平化、
        /// 但子压缩包的输出目录仍要处理（见 ProcessBatchSmartPathSynchronous），
        /// SpecificDir 模式则由逐项跳过处理——模式只能在“某个目录该不该动”的地方判，
        /// 不能当全局门槛。原先 4 处各写一份组合条件、其中两处省了 mode，
        /// 就因为这个区别没讲清。
        /// </summary>
        public bool IsProcessingEnabled => _settings.EnableSmartPathProcessing;

        /// <summary>
        /// 用户直接拖入的纯文件夹要不要收尾扁平化：除总开关外还要求 ArchiveFolder 模式
        /// （沿用改造前 MainViewModel 拖拽分支的原条件，其它输出模式下拖入文件夹不动）。
        /// </summary>
        public bool IsDroppedFolderFlattenEnabled
            => IsProcessingEnabled && _settings.OutputMode == OutputMode.ArchiveFolder;

        /// <summary>
        /// 扁平化阶段的压缩包判定：这里扫到的一律是解压产物（或输入侧已按 FolderScan
        /// 判定过的用户文件），所以固定用 ExtractedArtifact：受排除列表约束、
        /// 不被强制模式误判为压缩包、也不再重复跑隐写探测（那会逐个文件起 7z 子进程）。
        /// 这些点原先走 IsArchiveFile 的默认 origin=DroppedFile，等于把「用户亲手拖入
        /// 即确认」的豁免套到了产物目录上：排除列表失效；开启强制模式时，
        /// 「删除残留压缩包」会把目录里任意文件当成压缩包直接删掉。
        /// </summary>
        private bool IsArchiveArtifact(string filePath)
            => _detector.Classify(filePath, FileOrigin.ExtractedArtifact) != ArchiveDetector.ArchiveKind.NotArchive;

        /// <summary>
        /// 在智能路径扁平化之前，清理所有子压缩包文件。
        /// 子压缩包不受 FileAfterExtract 设置影响，一律直接删除。
        /// 这是阶段1清理的补充，确保扫描目录中所有子压缩包并清理，而不仅仅依赖文件树记录。
        /// </summary>
        public async Task DeleteChildArchivesUnderDirectoryAsync(string rootDir)
        {
            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
                return;

            try
            {
                _logger.Log($"[批量智能路径] 扁平化前子压缩包清理: {rootDir}", ConsoleColor.Gray);
                int cleanedCount = 0;
                foreach (string file in _fileOps.EnumerateFilesSafe(rootDir, "*.*", allDirectories: true))
                {
                    if (!File.Exists(file))
                        continue;
                    
                    // 检查是否是压缩文件
                    if (IsArchiveArtifact(file) && !_volumeDetector.IsMultiVolumeArchive(file))
                    {
                        // 直接删除（不经过回收站），子压缩包不保留；统一走 FileOperationService 带重试
                        if (_fileOps.TryDelete(file))
                        {
                            _logger.Log($"[批量智能路径]   已清理子压缩包: {Path.GetFileName(file)}", ConsoleColor.Gray);
                            cleanedCount++;
                        }
                        else
                        {
                            _logger.Log($"[批量智能路径]   清理子压缩包失败（被占用或不存在）: {Path.GetFileName(file)}", ConsoleColor.Yellow);
                        }
                    }
                }
                if (cleanedCount > 0)
                    _logger.Log($"[批量智能路径]   共清理 {cleanedCount} 个子压缩包", ConsoleColor.Gray);
            }
            catch (Exception ex)
            {
                _logger.Log($"[批量智能路径] 扁平化前子压缩包扫描失败: {ex.Message}", ConsoleColor.Yellow);
            }
        }

        /// <summary>
        /// 同步处理批量智能路径扁平化（等待所有操作完成）
        /// 与异步触发的收尾不同，这个方法会等待所有扁平化操作真正完成后再返回。
        /// </summary>
        public async Task ProcessBatchSmartPathSynchronous(string topArchivePath)
        {
            _logger.Log($"[同步批量智能路径] [DEBUG] 开始检查条件...", ConsoleColor.Gray);
            _logger.Log($"[同步批量智能路径] [DEBUG] topArchivePath={topArchivePath}", ConsoleColor.Gray);
                    
            if (!IsProcessingEnabled)
            {
                _logger.Log($"[同步批量智能路径] 智能路径处理未启用，跳过处理", ConsoleColor.Yellow);
                return;
            }

            // 【并发修复】只处理当前顶级文件的输出目录树，避免同一父目录下多个顶级文件并发收尾时
            // 互相处理对方的目录（会导致移动冲突、GUID 新目录、图片分散）。
            // 用 _detector.GetOutputDirectory(topArchivePath) 作为树根：
            // - ArchiveFolder 模式：根 = 当前文件的同名输出文件夹，只包含本文件的解压节点
            // - ArchiveDir 模式：根 = 父目录（与旧逻辑一致，行下方有专门的跳过扁平化保护）
            // - SpecificDir 模式：根 = 统一输出目录（行下方有跳过保护）
            string archiveDir = Path.GetDirectoryName(topArchivePath) ?? string.Empty;
            string ownOutputDir = _detector.GetOutputDirectory(topArchivePath);

            // 【并发修复】排除正在解压/测试密码的文件对应的输出目录：收尾与解压线程之间没有锁，
            // ArchiveDir 模式下其他文件的中间层目录可能在快照范围内，移动正在被解压写入的目录会失败。
            var busyDirs = GetAllItemsSnapshot()
                .Where(f => f.IsInFlight)
                .Select(f => _detector.GetOutputDirectory(f.FilePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            
            var allExtractedDirs = _extractedDirectoryNodes.Keys
                .Where(dir => (dir.Equals(ownOutputDir, StringComparison.OrdinalIgnoreCase)
                               || dir.StartsWith(ownOutputDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                              && !busyDirs.Contains(dir))
                .ToList();
            _logger.Log($"[同步批量智能路径] [DEBUG] 共 {allExtractedDirs.Count} 个解压目录节点（已过滤，仅当前批次）", ConsoleColor.Gray);
            foreach (var dir in allExtractedDirs)
            {
                _logger.Log($"[同步批量智能路径] [DEBUG]   - {dir}", ConsoleColor.Gray);
            }

            // 找出最顶层解压目录：父目录不在当前批次解压目录中的才是顶层
            // 【修复】跨批次标记污染：_extractedDirectoryNodes 全局累积、批次完成后不清理，
            // 上一批次残留的父目录标记（如隐写容器 mp4 的输出目录）会让本批次真正的顶层目录
            // （如重新拖入的 2.zip 输出目录）被误判为非顶层，导致扁平化遍历整体跳过（阶段3空转）。
            // 改为仅与当前批次过滤后的集合（allExtractedDirs）比较，父子关系只在批内判定。
            // 后序遍历会自然递归覆盖所有子孙目录，无需逐个处理
            var batchDirSet = allExtractedDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var topLevelDirs = allExtractedDirs
                .Where(dir =>
                {
                    string? parent = Path.GetDirectoryName(dir);
                    return parent == null || !batchDirSet.Contains(parent);
                })
                .ToList();

            _logger.Log($"[同步批量智能路径] [DEBUG] 其中 {topLevelDirs.Count} 个为最顶层: {string.Join(", ", topLevelDirs.Select(d => Path.GetFileName(d)))}", ConsoleColor.Gray);
            
            // 清空已处理标记（每次批量处理都是独立的）
            _processedFlattenDirs.Clear();
            
            int processedCount = 0;
            
            foreach (var outputDir in topLevelDirs)
            {
                if (!Directory.Exists(outputDir))
                {
                    _logger.Log($"[同步批量智能路径] [DEBUG] 目录不存在，跳过: {outputDir}", ConsoleColor.Gray);
                    continue;
                }
            
                // ArchiveDir 模式（解压到当前目录），根目录不扁平化，但子压缩包的输出目录仍需处理
                if (outputDir.Equals(archiveDir, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Log($"[同步批量智能路径] [DEBUG] 目录是 ArchiveDir 模式，根目录跳过扁平化: {outputDir}", ConsoleColor.Gray);
                    
                    // 查找 archiveDir 的直接子目录中属于 _extractedDirectoryNodes 的（即子压缩包解压目录）
                    var childExtractedDirs = allExtractedDirs
                        .Where(dir =>
                        {
                            string? parent = Path.GetDirectoryName(dir);
                            return parent != null && parent.Equals(outputDir, StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();
                    
                    if (childExtractedDirs.Count > 0)
                    {
                        _logger.Log($"[同步批量智能路径] ArchiveDir 根目录下有 {childExtractedDirs.Count} 个标记子目录需要扁平化: {string.Join(", ", childExtractedDirs.Select(d => Path.GetFileName(d)))}", ConsoleColor.Cyan);
                        foreach (var childDir in childExtractedDirs)
                        {
                            if (Directory.Exists(childDir))
                            {
                                _logger.Log($"[同步批量智能路径] 开始处理子目录: {childDir}", ConsoleColor.Cyan);
                                await Task.Run(() => ProcessSmartPathBatchAsync(childDir));
                                _processedFlattenDirs.Add(childDir);
                                processedCount++;
                            }
                        }
                    }
                    continue;
                }
            
                // SpecificDir 模式（统一输出到指定目录），不进行扁平化处理
                // 所有压缩包输出到同一目录，扁平化会误操作其他压缩包的内容
                if (_settings.OutputMode == OutputMode.SpecificDir)
                {
                    _logger.Log($"[同步批量智能路径] [DEBUG] 目录是 SpecificDir 模式，跳过扁平化: {outputDir}", ConsoleColor.Gray);
                    continue;
                }
            
                _logger.Log($"[同步批量智能路径] 开始处理: {outputDir}", ConsoleColor.Cyan);
            
                await Task.Run(() => ProcessSmartPathBatchAsync(outputDir));
                _processedFlattenDirs.Add(outputDir);
                processedCount++;
            }
        
            _logger.Log($"[同步批量智能路径] ✓ 共处理 {processedCount}/{topLevelDirs.Count} 个顶层目录", ConsoleColor.Green);
        }

        /// <summary>
        /// 兜底批量扁平化：没有任何在途任务时，把「正规收尾没覆盖到」的顶级输出目录补做扁平化。
        /// 典型场景是父压缩包自身解压失败、但子压缩包全部成功：顶级上报时走不到正规收尾
        /// （见 ExtractionPipeline.UpdateParentStatusWhenChildrenComplete 的 parentSelfSuccess 分支），
        /// 这批目录就只能在这里补。
        /// 门槛用 FileItem 的状态语义而不是另写一份词表：既要求整棵树无在途（否则会动到
        /// 正在被写入的目录），又要求每个顶级项已进入终态（「等待处理」不算，不然会把还没
        /// 解压的目录提前搬走）。旧版手写的词表不含 "fail"，导致只要有一个
        /// 「fail-未找到密码」的顶级项，这个兜底就永远不触发。
        /// </summary>
        public async Task FlattenCompletedTopLevelDirsAsync()
        {
            if (!IsProcessingEnabled)
            {
                _logger.Log($"[批量智能路径] [兜底] 智能路径处理未启用，跳过处理", ConsoleColor.Gray);
                return;
            }

            // 顶级项就存在快照集合里 Parent == null 那一批（子项挂在 Children 下）
            var topLevelItems = GetAllItemsSnapshot().Where(f => f.Parent == null).ToList();
            _logger.Log($"[批量智能路径] [兜底] 顶级文件项数量: {topLevelItems.Count}", ConsoleColor.Gray);

            if (topLevelItems.Count == 0)
            {
                _logger.Log($"[批量智能路径] [兜底] 没有顶级文件项，跳过处理", ConsoleColor.Gray);
                return;
            }

            foreach (var item in topLevelItems)
                _logger.Log($"[批量智能路径] [兜底] 顶级项: {item.FileName}, 状态: {item.Status}", ConsoleColor.Gray);

            if (!topLevelItems.All(item => item.IsFinished && !item.HasInFlightInSubtree()))
            {
                _logger.Log($"[批量智能路径] [兜底] 仍有节点在途或未进入终态，本轮不补收尾", ConsoleColor.Gray);
                return;
            }

            // 收集需要补收尾的输出目录（而不是目标目录）
            var extractedFolders = new HashSet<string>();
            foreach (var item in topLevelItems)
            {
                string extractedFolder = _detector.GetOutputDirectory(item.FilePath);
                string archiveDir = Path.GetDirectoryName(item.FilePath) ?? string.Empty;

                // ArchiveDir 模式：输出目录就是压缩包所在目录，扁平化它会把隔壁无关文件夹一起动了
                if (extractedFolder.Equals(archiveDir, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Log($"[批量智能路径] [兜底] 顶级项 {item.FileName} 使用 ArchiveDir 模式，跳过扁平化", ConsoleColor.Gray);
                    continue;
                }

                // SpecificDir 模式：所有压缩包输出到同一目录，扁平化会误操作其他压缩包的内容
                if (_settings.OutputMode == OutputMode.SpecificDir)
                {
                    _logger.Log($"[批量智能路径] [兜底] 顶级项 {item.FileName} 使用 SpecificDir 模式，跳过扁平化", ConsoleColor.Gray);
                    continue;
                }

                _logger.Log($"[批量智能路径] [兜底] 顶级项 {item.FileName} 的解压文件夹: {extractedFolder}", ConsoleColor.Gray);

                if (!string.IsNullOrEmpty(extractedFolder) && Directory.Exists(extractedFolder))
                {
                    extractedFolders.Add(extractedFolder);
                }
            }

            _logger.Log($"[批量智能路径] [兜底] 找到 {extractedFolders.Count} 个解压文件夹待处理", ConsoleColor.Cyan);
            if (extractedFolders.Count == 0)
                return;

            // 持同一把串行锁：避免与正规收尾、拖入文件夹收尾并发移动同一棵目录树
            await FinalizeLock.WaitAsync();
            try
            {
                foreach (var extractedFolder in extractedFolders)
                {
                    await Task.Run(() => ProcessSmartPathBatchAsync(extractedFolder));
                }
            }
            finally
            {
                FinalizeLock.Release();
            }
        }


        /// <summary>
        /// 批量处理单个输出目录的智能路径扁平化（从叶子节点向上遍历）
        /// </summary>
        public async Task ProcessSmartPathBatchAsync(string rootDir)
        {
            _logger.Log($"[批量智能路径] [DEBUG] 进入 ProcessSmartPathBatchAsync, rootDir={rootDir}", ConsoleColor.Gray);

            // 安装包解压模式：产物目录结构不可移动（扁平化/合并会破坏安装包路径依赖），
            // 直接视为处理完成，仅回调状态不执行任何文件操作；
            // 必须触发 OnSmartPathCompleted 后再 return，避免 FileItem 停在"等待扁平化处理"死状态
            if (_settings.InstallerExtractMode)
            {
                _logger.Log($"[智能路径] 安装包解压模式：跳过扁平化处理: {rootDir}", ConsoleColor.Yellow);
                OnSmartPathCompleted?.Invoke(rootDir);
                return;
            }
                    
            if (!Directory.Exists(rootDir))
            {
                _logger.Log($"[批量智能路径] [DEBUG] rootDir 不存在，直接返回", ConsoleColor.Gray);
                return;
            }
        
            _logger.Log($"[批量智能路径] 开始处理: {rootDir}", ConsoleColor.Cyan);
        
            // 步骤1: 清理黑名单文件
            _logger.Log($"[批量智能路径] [DEBUG] 步骤1: 清理黑名单文件", ConsoleColor.Gray);
            _blacklistService.DeleteBlacklistedFilesUnderDirectory(rootDir);
        
            // 步骤2: 后序遍历——从叶子节点向上逐级扁平化
            // 只处理解压树中的目录（_extractedDirectoryNodes），非解压目录跳过
            // 后序遍历自然覆盖所有层级（包括最外层），无需额外的"最外层"检查
            _logger.Log($"[批量智能路径] [DEBUG] 步骤2: 开始后序遍历, rootDir={rootDir}", ConsoleColor.Gray);
            ProcessDirectoryTreePostOrder(rootDir, isInExtractedTree: false);
        
            // 步骤3: 根目录兜底检查——拖入的文件夹本身不在 _extractedDirectoryNodes 中，
            // 后序遍历会跳过它。但如果它恰好只有一个子目录且无其他非压缩包文件，仍需扁平化。
            string normalizedRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(rootDir) && !_processedFlattenDirs.Contains(normalizedRoot))
            {
                _logger.Log($"[批量智能路径] [DEBUG] 步骤3: 检查根目录是否需要扁平化: {rootDir}", ConsoleColor.Gray);
                ProcessSingleDirectoryIfNeeded(rootDir);
                _processedFlattenDirs.Add(normalizedRoot);
            }
        
            _logger.Log($"[批量智能路径] ✓ 处理完成: {Path.GetFileName(rootDir)}", ConsoleColor.Green);
                    
            // 更新所有相关的FileItem状态为"扁平化处理完成"
            OnSmartPathCompleted?.Invoke(rootDir);
        }


        /// <summary>
        /// 递归查找最深层的解压目录标记节点
        /// </summary>
        public string? FindDeepestMarkedNode(string dir)
        {
            if (!Directory.Exists(dir))
                return null;

            var subDirs = Directory.GetDirectories(dir);
            foreach (var subDir in subDirs)
            {
                if (_extractedDirectoryNodes.ContainsKey(subDir))
                {
                    // 递归查找更深层标记节点
                    string? deeper = FindDeepestMarkedNode(subDir);
                    if (deeper != null)
                        return deeper;
                    return subDir;
                }
            }
            return null;
        }

        /// <summary>
        /// 查找最终扁平化后的路径
        /// 注意：在扁平化处理后调用，此时子目录的内容可能已经被提升到 rootDir
        /// 如果 rootDir 不存在（被扁平化删除），则查找扁平化后的实际目录
        /// </summary>
        public string? FindFinalFlattenedPath(string rootDir)
        {
            _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath 开始: rootDir={rootDir}", ConsoleColor.Gray);
            
            try
            {
                // 【修复】优先查询扁平化结果映射：rootDir 可能在扁平化中被删除，映射直接指向内容最终位置
                if (_flattenedFinalPathMap.TryGetValue(rootDir, out string? mappedPath) && Directory.Exists(mappedPath))
                {
                    _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 命中扁平化映射 {rootDir} -> {mappedPath}", ConsoleColor.Gray);
                    return mappedPath;
                }
                
                // 如果 rootDir 不存在，说明扁平化时它被删除了
                // 需要查找扁平化后的实际目录（在父目录下查找被标记的解压节点）
                if (!Directory.Exists(rootDir))
                {
                    _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: rootDir 不存在 {rootDir}，查找扁平化后的目录", ConsoleColor.Gray);
                    
                    string? parentDir = Path.GetDirectoryName(rootDir);
                    if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
                    {
                        // 父目录也不存在（被最外层扁平化删除了），在祖目录中查找被移动的同名目录
                        string? grandParent = Path.GetDirectoryName(parentDir);
                        if (!string.IsNullOrEmpty(grandParent) && Directory.Exists(grandParent))
                        {
                            string dirName = Path.GetFileName(rootDir);
                            string? candidatePath = Path.Combine(grandParent, dirName);
                            _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 尝试在祖目录查找被移动的目录 {candidatePath}", ConsoleColor.Gray);
                            if (Directory.Exists(candidatePath))
                            {
                                _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 找到被最外层扁平化移动的目录 {candidatePath}", ConsoleColor.Gray);
                                return FindFinalFlattenedPath(candidatePath);
                            }
                            
                            // 【修复】同名目录未找到（可能被扁平化时改名），尝试在祖目录中递归查找标记节点
                            string? deepestInGrandParent = FindDeepestMarkedNode(grandParent);
                            if (deepestInGrandParent != null)
                            {
                                _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 在祖目录找到最深层的标记节点 {deepestInGrandParent}", ConsoleColor.Gray);
                                return deepestInGrandParent;
                            }
                        }
                        _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 父目录不存在，返回 rootDir {rootDir}", ConsoleColor.Gray);
                        return rootDir;
                    }
                    
                    // 在父目录下递归查找最深层的标记节点
                    string? deepestNode = FindDeepestMarkedNode(parentDir);
                    if (deepestNode != null)
                    {
                        _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 找到最深层的扁平化目录 {deepestNode}", ConsoleColor.Gray);
                        return deepestNode;
                    }
                    
                    _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 未找到标记节点，返回 rootDir {rootDir}", ConsoleColor.Gray);
                    return rootDir;
                }
                
                // rootDir 存在，先检查子目录数量判断是否被扁平化
                var subDirsInRoot = Directory.GetDirectories(rootDir);
                
                _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: {rootDir}, 子目录数={subDirsInRoot.Length}", ConsoleColor.Gray);
                
                if (subDirsInRoot.Length == 1)
                {
                    // 只有一个子目录，可能已扁平化，递归查找最深层的标记节点确认最终路径
                    string? deepest = FindDeepestMarkedNode(rootDir);
                    if (deepest != null)
                    {
                        _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 递归找到最深层的标记节点 {deepest}", ConsoleColor.Gray);
                        return deepest;
                    }
                    // 没有标记节点，返回唯一的子目录
                    _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 返回子目录 {subDirsInRoot[0]}", ConsoleColor.Gray);
                    return subDirsInRoot[0];
                }
                else if (subDirsInRoot.Length == 0)
                {
                    // 没有子目录，返回 rootDir 本身
                    _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 返回 rootDir {rootDir}", ConsoleColor.Gray);
                    return rootDir;
                }
                else
                {
                    // 有多个子目录，说明当前层级未发生扁平化，返回 rootDir 本身
                    _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 有多个子目录({subDirsInRoot.Length})，未扁平化，返回 rootDir {rootDir}", ConsoleColor.Gray);
                    return rootDir;
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"[批量智能路径] [DEBUG] FindFinalFlattenedPath 异常: {ex.Message}", ConsoleColor.Red);
                return rootDir;
            }
        }

        /// <summary>
        /// 后序遍历目录树，从叶子节点开始处理
        /// 注意：只处理压缩包解压出的文件夹结构，不处理非压缩包（如游戏应用）的原始结构
        /// </summary>
        public void ProcessDirectoryTreePostOrder(string dirPath, bool isInExtractedTree = false)
        {
            try
            {
                // 检查目录是否存在（可能在之前的扁平化操作中被删除）
                if (!Directory.Exists(dirPath))
                {
                    _logger.Log($"[批量智能路径] 目录已不存在，跳过: {dirPath}", ConsoleColor.Gray);
                    return;
                }

                // 防止重复处理同一个目录（避免死循环）
                string normalizedPath = Path.GetFullPath(dirPath).TrimEnd(Path.DirectorySeparatorChar);
                if (_processedFlattenDirs.Contains(normalizedPath))
                {
                    _logger.Log($"[批量智能路径] [DEBUG] 目录已处理过，跳过: {Path.GetFileName(dirPath)}", ConsoleColor.Gray);
                    return;
                }

                _logger.Log($"[批量智能路径] [DEBUG] 开始遍历目录: {dirPath}", ConsoleColor.Gray);

                // 检查当前目录是否是被标记的解压目录节点，或者在解压树中
                bool isExtractedNode = isInExtractedTree || _extractedDirectoryNodes.ContainsKey(dirPath);
                
                _logger.Log($"[批量智能路径] [DEBUG] {Path.GetFileName(dirPath)}: isInExtractedTree={isInExtractedTree}, 在标记节点中={_extractedDirectoryNodes.ContainsKey(dirPath)}, isExtractedNode={isExtractedNode}", ConsoleColor.Gray);
                
                // 1. 先递归处理所有子目录（深度优先）- 如果在解压树中，子目录也继承这个状态
                var subDirs = Directory.GetDirectories(dirPath);
                _logger.Log($"[批量智能路径] [DEBUG] 找到 {subDirs.Length} 个子目录", ConsoleColor.Gray);
                
                foreach (var subDir in subDirs)
                {
                    _logger.Log($"[批量智能路径] [DEBUG] 递归处理子目录: {Path.GetFileName(subDir)}", ConsoleColor.Gray);
                    // 对每个子目录的遍历进行独立异常处理
                    try
                    {
                        ProcessDirectoryTreePostOrder(subDir, isExtractedNode);  // 传递解压树状态
                    }
                    catch (Exception subEx)
                    {
                        // 单个子目录处理失败不影响其他子目录
                        _logger.Log($"[批量智能路径] 处理子目录失败 {subDir}: {subEx.Message}", ConsoleColor.Yellow);
                    }
                }

                // 2. 处理当前目录（此时子目录已经处理完毕）
                // 再次检查目录是否存在（可能子目录处理时被删除）
                if (Directory.Exists(dirPath))
                {
                    // 只有在解压树中的目录才进行扁平化
                    if (isExtractedNode)
                    {
                        _logger.Log($"[批量智能路径] [DEBUG] 开始处理当前目录: {Path.GetFileName(dirPath)}", ConsoleColor.Gray);
                        ProcessSingleDirectoryIfNeeded(dirPath);
                        
                        // 标记为已处理（防止重复处理）
                        _processedFlattenDirs.Add(normalizedPath);  // 复用上面定义的 normalizedPath
                    }
                    else
                    {
                        _logger.Log($"[批量智能路径] [DEBUG] {Path.GetFileName(dirPath)}: 非标记的解压节点，跳过扁平化", ConsoleColor.Gray);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"[批量智能路径] 遍历目录失败 {dirPath}: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 递归检查目录中是否包含压缩包文件
        /// </summary>
        /// <summary>
        /// 判断目录是否是压缩包解压出来的
        /// 通过检查目录下是否有压缩包解压的典型特征来判断
        /// </summary>
        public bool IsExtractedArchiveFolder(string dirPath)
        {
            try
            {
                string dirName = Path.GetFileName(dirPath);
                string? parentDir = Path.GetDirectoryName(dirPath);
                
                if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
                {
                    return false;
                }

                var archiveExtensions = _settings.GetArchiveExtensions();
                
                // 方法1：检查目录名是否与某个压缩包文件名匹配（去掉扩展名后）
                // 需要考虑分卷压缩包的情况（.001, .part1.rar 等）
                var parentFiles = Directory.GetFiles(parentDir);
                foreach (var file in parentFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    string fileExt = PathUtil.LowerExtension(file);
                    
                    // 检查是否是分卷压缩包（.001, .002, .part1.rar 等）
                    bool isVolumeArchive = _volumeDetector.IsMultiVolumeArchive(file);
                    
                    // 获取压缩包的基础名称（去掉所有扩展名）
                    string baseName = fileName;
                    
                    // 去掉可能的分卷后缀
                    if (isVolumeArchive)
                    {
                        // 对于 .7z.001 这样的文件，fileName 是 "bb.7z"
                        // 需要进一步去掉 .7z
                        foreach (var ext in archiveExtensions.OrderByDescending(x => x.Length))
                        {
                            if (baseName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                            {
                                baseName = baseName.Substring(0, baseName.Length - ext.Length);
                                break;
                            }
                        }
                    }
                    
                    // 检查目录名是否匹配
                    if (dirName.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                
                // 方法2：如果目录下有压缩包，则认为这是压缩包解压出的文件夹
                var files = Directory.GetFiles(dirPath);
                foreach (var file in files)
                {
                    if (IsArchiveArtifact(file))
                    {
                        return true;
                    }
                }
                
                // 方法3：递归检查子目录
                var subDirs = Directory.GetDirectories(dirPath);
                foreach (var subDir in subDirs)
                {
                    if (IsExtractedArchiveFolder(subDir))
                    {
                        return true;
                    }
                }
                
                // 默认返回false，避免误判
                return false;
            }
            catch
            {
                // 如果检查失败，默认不进行扁平化（安全优先）
                return false;
            }
        }

        /// <summary>
        /// 检查并处理单个目录的扁平化（如果符合条件）
        /// </summary>
        public void ProcessSingleDirectoryIfNeeded(string dirPath)
        {
            try
            {
                var subDirs = Directory.GetDirectories(dirPath);
                var allFiles = Directory.GetFiles(dirPath);
                // 忽略压缩包文件（含分卷），这些残留文件将在后续 HandleOriginalFile 中统一清理
                var nonArchiveFiles = allFiles.Where(f => !IsArchiveArtifact(f)).ToArray();

                _logger.Log($"[批量智能路径] [DEBUG] {Path.GetFileName(dirPath)}: {subDirs.Length} 个子目录, {allFiles.Length} 个文件 (非压缩包: {nonArchiveFiles.Length})", ConsoleColor.Gray);
                
                // 【新增】详细列出所有文件及其类型，用于调试
                foreach (var file in allFiles)
                {
                    bool isArchive = IsArchiveArtifact(file);
                    string fileName = Path.GetFileName(file);
                    _logger.Log($"[批量智能路径] [DEBUG-文件列表] {fileName}: {(isArchive ? "压缩包" : "非压缩包")}", ConsoleColor.Gray);
                }

                // 【修复：同名嵌套优先】压缩包内顶层文件夹与输出目录同名时（如 [GalACG]EX592878201 内
                // 含同名顶层文件夹），即使父目录存在非压缩包文件也必须就地合并消除冗余嵌套。
                // 就地合并只上移子目录内容、不移动父目录本身，安全无歧义；
                // 同名文件/目录冲突由 MergeSingleSubDirInPlace 内部的安全策略处理（不覆盖丢失数据）。
                if (subDirs.Length == 1)
                {
                    string singleSubDir = subDirs[0];
                    string subDirName = Path.GetFileName(singleSubDir);
                    string parentDirName = Path.GetFileName(dirPath);

                    if (string.Equals(subDirName, parentDirName, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Log($"[批量智能路径] 检测到同名嵌套（{parentDirName} -> {subDirName}），执行就地合并消除嵌套", ConsoleColor.Cyan);
                        MergeSingleSubDirInPlace(dirPath, singleSubDir);
                        return;
                    }
                }

                // 触发条件：恰好一个子目录且没有非压缩包文件
                if (subDirs.Length != 1 || nonArchiveFiles.Length > 0)
                {
                    if (subDirs.Length != 1)
                        _logger.Log($"[批量智能路径] [DEBUG] {Path.GetFileName(dirPath)}: 子目录数量不为1 ({subDirs.Length})，跳过扁平化", ConsoleColor.Gray);
                    if (nonArchiveFiles.Length > 0)
                        _logger.Log($"[批量智能路径] [DEBUG] {Path.GetFileName(dirPath)}: 存在 {nonArchiveFiles.Length} 个非压缩包文件，跳过扁平化", ConsoleColor.Gray);
                    return;
                }

                string singleSubDir2 = subDirs[0];
                string subDirName2 = Path.GetFileName(singleSubDir2);
                string parentDirName2 = Path.GetFileName(dirPath);

                _logger.Log($"[批量智能路径] 检测到可扁平化: {parentDirName2} -> {subDirName2}", ConsoleColor.Gray);

                // 顶层拖入的文件夹与普通解压层级一视同仁：都通过 DetermineFolderName 对比父目录名
                // 与子目录名（含日文 +100 分、长名 +50 分），胜出者成为最终文件夹名。
                // - 子目录名胜出（如 [坂本商店...] 胜过 187）：拖入的文件夹被删除，内容目录提升到其父目录下
                // - 父目录名胜出（如 188 胜过 11n）：效果等同就地合并，内容摊平在拖入的文件夹中
                // FlattenDirectory 内含完整的失败恢复逻辑（残留清理/删除失败回滚/重命名失败回滚），不会丢产物。

                // 根据配置决定最终名称
                string finalFolderName = DetermineFolderName(parentDirName2, subDirName2);
                _logger.Log($"[批量智能路径] [DEBUG] 最终文件夹名: {finalFolderName}", ConsoleColor.Gray);

                // 执行扁平化：将子目录内容提升到父目录
                FlattenDirectory(dirPath, singleSubDir2, finalFolderName);
            }
            catch (Exception ex)
            {
                _logger.Log($"[批量智能路径] 处理目录失败 {dirPath}: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 将唯一子目录的内容就地合并进 dirPath（不移动 dirPath 本身、不改名）。
        /// 用于子目录与 dirPath 同名的情况（压缩包内顶层文件夹与输出目录同名导致的嵌套）。
        /// 合并完成后删除已清空的子目录，并同步更新 _extractedDirectoryNodes 标记。
        /// </summary>
        public void MergeSingleSubDirInPlace(string dirPath, string singleSubDir)
        {
            try
            {
                string dirName = Path.GetFileName(dirPath);
                string subDirName = Path.GetFileName(singleSubDir);
                _logger.Log($"[批量智能路径] 就地合并: {dirName} <- {subDirName}", ConsoleColor.Cyan);

                int movedCount = 0;
                // 【修复】先物化条目列表，避免合并过程中移动/删除目录打断惰性枚举
                foreach (var entry in Directory.EnumerateFileSystemEntries(singleSubDir).ToArray())
                {
                    string entryName = Path.GetFileName(entry);
                    string destPath = Path.Combine(dirPath, entryName);

                    if (Directory.Exists(entry))
                    {
                        // 【修复】条目目录名与 dirPath 同名（三层同名嵌套 A\A\A）时，
                        // destPath 就是正在遍历的 singleSubDir 本身：绝不能删除它
                        // （会把尚未处理的条目一并递归删除，导致图片丢失）。
                        // 改为递归就地合并该目录的内容，摊平嵌套。
                        if (string.Equals(destPath, singleSubDir, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Log($"[批量智能路径]   同名嵌套目录，递归合并: {entryName}", ConsoleColor.Gray);
                            MergeSingleSubDirInPlace(dirPath, entry);
                            continue;
                        }

                        // 重名冲突交给 ConflictResolver 统一裁决（护栏：非解压节点目录禁止递归删除）
                        string? movedDir = _conflicts.TryMove(entry, destPath, ConflictKind.Directory,
                            _extractedDirectoryNodes.ContainsKey(destPath), NamingStyle.GuidPrefix);
                        if (movedDir == null)
                            continue;
                        destPath = movedDir;
                        _logger.Log($"[批量智能路径]   已移动目录: {entryName}", ConsoleColor.Gray);

                        // 同步更新解压节点标记
                        if (_extractedDirectoryNodes.ContainsKey(entry))
                        {
                            _extractedDirectoryNodes.TryRemove(entry, out _);
                            if (!_extractedDirectoryNodes.ContainsKey(destPath))
                                _extractedDirectoryNodes.TryAdd(destPath, 0);
                        }
                    }
                    else
                    {
                        // 文件类冲突同样交给 resolver：Skip 时“同名同内容→丢弃，内容不同→保留两份”。
                        // 这里 targetOwnedByTool 传 true：7z 解压阶段本来就是 -y 覆盖语义，
                        // 对单个文件开放“覆盖”策略与之一致；递归删除的护栏只加在目录上。
                        string? movedFile = _conflicts.TryMove(entry, destPath, ConflictKind.File,
                            true, NamingStyle.GuidPrefix);
                        if (movedFile == null)
                            continue;
                        destPath = movedFile;
                        _logger.Log($"[批量智能路径]   已移动文件: {entryName}", ConsoleColor.Gray);
                    }
                    movedCount++;
                }

                // 删除已清空的子目录
                if (_fileOps.TryDeleteEmptyDirectory(singleSubDir))
                    _logger.Log($"[批量智能路径]   已删除空子目录: {subDirName}", ConsoleColor.Gray);

                // 子目录本身已删除，从解压节点标记中移除
                if (_extractedDirectoryNodes.ContainsKey(singleSubDir))
                {
                    _extractedDirectoryNodes.TryRemove(singleSubDir, out _);
                }

                _logger.Log($"[批量智能路径] ✓ 就地合并完成: {dirName} (共移动 {movedCount} 项)", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                _logger.Log($"[批量智能路径] 就地合并失败 {dirPath}: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 处理最外层目录：如果拖入的是文件夹，且解压后最外层目录只包含一个子目录，则扁平化
        /// </summary>

        public string? ProcessTopLevelFolderIfNeeded(string rootDir)
        {
            try
            {
                string? resultPath = null;
                
                // 再次检查目录是否存在（可能内层扁平化已经改变了结构）
                if (!Directory.Exists(rootDir))
                {
                    _logger.Log($"[批量智能路径] [最外层检查] 目录已不存在，跳过: {rootDir}", ConsoleColor.Gray);
                    return null;
                }

                // 检查最外层目录是否只有一个子目录
                var subDirs = Directory.GetDirectories(rootDir);
                var allFiles = Directory.GetFiles(rootDir);
                var archiveFiles = allFiles.Where(f => IsArchiveArtifact(f)).ToArray();
                var nonArchiveFiles = allFiles.Where(f => !IsArchiveArtifact(f)).ToArray();

                _logger.Log($"[批量智能路径] [最外层检查] {Path.GetFileName(rootDir)}: {subDirs.Length} 个子目录, {archiveFiles.Length} 个压缩包, {nonArchiveFiles.Length} 个非压缩包文件", ConsoleColor.Gray);

                // 只有当最外层目录恰好一个子目录且只有压缩包文件时才扁平化
                if (subDirs.Length != 1 || nonArchiveFiles.Length > 0)
                {
                    if (subDirs.Length != 1)
                        _logger.Log($"[批量智能路径] [最外层检查] 子目录数量不为1 ({subDirs.Length})，不处理最外层", ConsoleColor.Gray);
                    if (nonArchiveFiles.Length > 0)
                        _logger.Log($"[批量智能路径] [最外层检查] 存在 {nonArchiveFiles.Length} 个非压缩包文件，不处理最外层", ConsoleColor.Gray);
                    return null;
                }

                // 此时应该只有 1 个子目录和若干压缩包文件
                string singleSubDir = subDirs[0];
                string subDirName = Path.GetFileName(singleSubDir);
                string parentDirName = Path.GetFileName(rootDir);
                string? parentDirFullPath = Path.GetDirectoryName(rootDir);
                
                // 如果无法获取父目录路径，则跳过处理
                if (string.IsNullOrEmpty(parentDirFullPath))
                {
                    _logger.Log($"[批量智能路径] [最外层扁平化] 无法获取父目录路径，跳过处理", ConsoleColor.Yellow);
                    return null;
                }

                _logger.Log($"[批量智能路径] [最外层扁平化] 检测到可扁平化: {parentDirName} -> {subDirName}", ConsoleColor.Cyan);

                // 构建新的路径：在父目录中创建新的文件夹
                string newPath = Path.Combine(parentDirFullPath, subDirName);

                // 检查目标路径是否已存在
                bool needRenameAfterMove = false;
                if (Directory.Exists(newPath))
                {
                    _logger.Log($"[批量智能路径] [最外层扁平化] 目标目录已存在: {newPath}，添加临时前缀", ConsoleColor.Yellow);
                    // 添加临时GUID前缀避免冲突（后续会去掉）
                    string tempPrefix = Guid.NewGuid().ToString().Substring(0, 8);
                    newPath = Path.Combine(parentDirFullPath, $"{tempPrefix}_{subDirName}");
                    needRenameAfterMove = true; // 标记需要后续重命名
                }

                // 移动子目录到父目录的同级
                Directory.Move(singleSubDir, newPath);
                _logger.Log($"[批量智能路径] [最外层扁平化] 已移动: {subDirName} -> {Path.GetFileName(newPath)}", ConsoleColor.Gray);

                // 【关键】同步更新 _extractedDirectoryNodes，确保后续后序遍历能找到新路径
                if (_extractedDirectoryNodes.ContainsKey(singleSubDir))
                {
                    _extractedDirectoryNodes.TryRemove(singleSubDir, out _);
                    _extractedDirectoryNodes.TryAdd(newPath, 0);
                    _logger.Log($"[批量智能路径] [最外层扁平化] 已同步 _extractedDirectoryNodes: {singleSubDir} -> {newPath}", ConsoleColor.Gray);
                }

                // 删除最外层目录（压缩包会被保留，因为已经移动到 newPath 的父目录了）
                // 注意：不能直接递归删除，因为压缩包还在里面
                // 需要先移动压缩包，再删除目录
                var archivesToRename = new Dictionary<string, string>(); // Key: 当前路径, Value: 原始文件名（需要去掉前缀的）
                foreach (var archiveFile in archiveFiles)
                {
                    string archiveFileName = Path.GetFileName(archiveFile);
                    string destPath = Path.Combine(parentDirFullPath, archiveFileName);
                    
                    // 如果目标文件已存在，添加临时前缀
                    if (File.Exists(destPath))
                    {
                        string tempPrefix = Guid.NewGuid().ToString().Substring(0, 8);
                        destPath = Path.Combine(parentDirFullPath, $"{tempPrefix}_{archiveFileName}");
                        archivesToRename[destPath] = archiveFileName; // 记录需要重命名的文件
                    }
                    
                    File.Move(archiveFile, destPath);
                    _logger.Log($"[批量智能路径] [最外层扁平化] 已移动压缩包: {archiveFileName} -> {Path.GetFileName(destPath)}", ConsoleColor.Gray);
                }
                
                // 现在目录应该只有空文件夹了，可以安全删除
                Directory.Delete(rootDir);
                _logger.Log($"[批量智能路径] [最外层扁平化] 已删除空目录: {parentDirName}", ConsoleColor.Gray);
                
                // 去掉压缩包的临时前缀
                foreach (var kvp in archivesToRename)
                {
                    string currentPath = kvp.Key;
                    string originalFileName = kvp.Value;
                    string finalPath = Path.Combine(parentDirFullPath, originalFileName);
                    
                    try
                    {
                        // 去前缀时撞名：迁移前这里无条件 File.Delete(finalPath)，会静默删掉父目录里
                        // 原本就存在的同名压缩包。finalPath 未被工具登记过，按不变量传 targetOwnedByTool=false，
                        // 于是「覆盖」策略会被降级为保留两份，内容一致时才丢弃临时件。
                        string? renamed = _conflicts.TryMove(currentPath, finalPath, ConflictKind.File, false);
                        if (renamed == null)
                            _logger.Log($"[批量智能路径] [最外层扁平化] 与已有同名压缩包内容一致，丢弃临时件: {Path.GetFileName(currentPath)}", ConsoleColor.Gray);
                        else
                            _logger.Log($"[批量智能路径] [最外层扁平化] ✓ 已重命名压缩包去掉临时前缀: {Path.GetFileName(currentPath)} -> {Path.GetFileName(renamed)}", ConsoleColor.Green);
                    }
                    catch (Exception renameEx)
                    {
                        _logger.Log($"[批量智能路径] [最外层扁平化] ⚠ 重命名压缩包失败，保留临时名称: {renameEx.Message}", ConsoleColor.Yellow);
                    }
                }
                
                // 如果使用了临时前缀，现在去掉它
                if (needRenameAfterMove)
                {
                    string finalPath = Path.Combine(parentDirFullPath, subDirName);
                    try
                    {
                        // 同样收拢到 ConflictResolver：只有登记的解压节点目录才允许被替换删除，
                        // 用户原始目录一律改用安全名称保留两份。
                        string? renamed = _conflicts.TryMove(newPath, finalPath, ConflictKind.Directory,
                            _extractedDirectoryNodes.ContainsKey(finalPath), NamingStyle.GuidPrefix);
                        if (renamed != null)
                        {
                            finalPath = renamed;
                            newPath = finalPath; // 更新路径为最终路径
                            _logger.Log($"[批量智能路径] [最外层扁平化] ✓ 已重命名去掉临时前缀: {Path.GetFileName(finalPath)}", ConsoleColor.Green);
                        }
                    }
                    catch (Exception renameEx)
                    {
                        _logger.Log($"[批量智能路径] [最外层扁平化] ⚠ 重命名失败，保留临时名称: {renameEx.Message}", ConsoleColor.Yellow);
                    }
                }
                
                _logger.Log($"[批量智能路径] [最外层扁平化] ✓ 扁平化完成: {Path.GetFileName(newPath)}", ConsoleColor.Green);
                resultPath = newPath;
                return resultPath;
            }
            catch (Exception ex)
            {
                _logger.Log($"[批量智能路径] [最外层扁平化] 处理失败: {ex.Message}", ConsoleColor.Red);
                return null;
            }
        }

        /// <summary>
        /// 根据配置模式决定最终的文件夹名称
        /// </summary>
        public string DetermineFolderName(string parentName, string childName)
        {
            string normalizedParentName = _detector.NormalizeArchiveLikeName(parentName);
            string normalizedChildName = _detector.NormalizeArchiveLikeName(childName);

            if (_settings.SmartPathProcessingMode == SmartPathMode.Concatenate)
            {
                // 拼接模式
                return $"{normalizedParentName}_{normalizedChildName}";
            }
            else
            {
                // 智能选择模式
                bool parentHasJapanese = System.Text.RegularExpressions.Regex.IsMatch(
                    normalizedParentName, @"[\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF]");
                bool childHasJapanese = System.Text.RegularExpressions.Regex.IsMatch(
                    normalizedChildName, @"[\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF]");

                bool parentIsLong = normalizedParentName.Length > 10;
                bool childIsLong = normalizedChildName.Length > 10;

                int parentScore = (parentHasJapanese ? 100 : 0) + (parentIsLong ? 50 : 0);
                int childScore = (childHasJapanese ? 100 : 0) + (childIsLong ? 50 : 0);

                // 选择评分高的，评分相同选较长的
                if (childScore > parentScore || (childScore == parentScore && normalizedChildName.Length > normalizedParentName.Length))
                {
                    return normalizedChildName;
                }
                else
                {
                    return normalizedParentName;
                }
            }
        }

        /// <summary>
        /// 执行目录扁平化：将子目录内容提升到父目录并重命名
        /// </summary>
        public void FlattenDirectory(string parentDir, string childDir, string finalName)
        {
            try
            {
                string? parentDirPath = Path.GetDirectoryName(parentDir);
                if (string.IsNullOrEmpty(parentDirPath))
                    return;

                // 步骤1: 移动子目录到临时位置（避免冲突）
                string tempName = $"{Guid.NewGuid().ToString().Substring(0, 8)}_{finalName}";
                string tempPath = Path.Combine(parentDirPath, tempName);

                // 添加重试机制，防止文件占用导致卡住
                int retryCount = 0;
                const int maxRetries = 3;
                bool moveSuccess = false;
                
                while (retryCount < maxRetries && !moveSuccess)
                {
                    try
                    {
                        Directory.Move(childDir, tempPath);
                        moveSuccess = true;
                        _logger.Log($"[批量智能路径]   已移动: {Path.GetFileName(childDir)} -> {tempName}", ConsoleColor.Gray);
                        
                        // 更新解压节点标记：将旧路径替换为新路径
                        if (_extractedDirectoryNodes.ContainsKey(childDir))
                        {
                            _extractedDirectoryNodes.TryRemove(childDir, out _);
                            _extractedDirectoryNodes.TryAdd(tempPath, 0);
                            _logger.Log($"[批量智能路径] [DEBUG] 更新解压节点标记: {childDir} -> {tempPath}", ConsoleColor.Gray);
                        }
                    }
                    catch (IOException ioEx) when (retryCount < maxRetries - 1)
                    {
                        retryCount++;
                        _logger.Log($"[批量智能路径] ⚠ 移动失败（文件占用？），重试 {retryCount}/{maxRetries}: {ioEx.Message}", ConsoleColor.Yellow);
                        Thread.Sleep(500); // 等待500ms后重试
                    }
                }
                
                if (!moveSuccess)
                {
                    _logger.Log($"[批量智能路径] ✗ 移动失败，跳过扁平化: {Path.GetFileName(childDir)}", ConsoleColor.Red);
                    return;
                }

                // 步骤1.5: 清理父目录中残留的压缩包文件，避免阻塞后续的目录删除
                // 场景：子压缩包（如 .rar）解压后，CleanupChildArchivesInTree 因文件锁定等原因未能删除，
                // 残留的压缩包会导致 Directory.Delete 失败，从而使扁平化被跳过
                var residualFiles = Directory.GetFiles(parentDir);
                foreach (var file in residualFiles)
                {
                    try
                    {
                        if (IsArchiveArtifact(file))
                        {
                            File.Delete(file);
                            _logger.Log($"[批量智能路径]   删除残留压缩包: {Path.GetFileName(file)}", ConsoleColor.Gray);
                        }
                        else
                        {
                            _logger.Log($"[批量智能路径]   跳过非压缩包残留文件: {Path.GetFileName(file)}", ConsoleColor.Yellow);
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.Log($"[批量智能路径]   删除残留文件失败: {Path.GetFileName(file)} - {cleanupEx.Message}", ConsoleColor.Yellow);
                    }
                }

                // 步骤2: 删除空的父目录（可能因残留文件而失败，必须恢复临时目录）
                if (Directory.Exists(parentDir))
                {
                    try
                    {
                        Directory.Delete(parentDir);
                        _logger.Log($"[批量智能路径]   已删除空目录: {Path.GetFileName(parentDir)}", ConsoleColor.Gray);
                        
                        // 【修复】清理死标记：目录已删除，从解压节点集合中移除，避免后续路径追踪被死路径干扰
                        _extractedDirectoryNodes.TryRemove(parentDir, out _);
                    }
                    catch (Exception deleteEx)
                    {
                        // 目录非空（有其他残留文件），移动失败会导致GUID前缀目录成为孤儿残留
                        // 必须立即将临时目录恢复回原位置
                        _logger.Log($"[批量智能路径] ⚠ 父目录非空，跳过删除: {deleteEx.Message}", ConsoleColor.Yellow);
                        try
                        {
                            foreach (var entry in Directory.EnumerateFileSystemEntries(parentDir))
                            {
                                string entryName = Path.GetFileName(entry);
                                bool isDir = Directory.Exists(entry);
                                _logger.Log($"[批量智能路径]   残留: {(isDir ? "[DIR]" : "[FILE]")} {entryName}", ConsoleColor.Yellow);
                            }
                        }
                        catch { }
                        
                        // 【修复】父目录非空，必须将临时目录恢复回原位置，避免GUID前缀目录残留
                        try
                        {
                            if (Directory.Exists(tempPath) && !Directory.Exists(childDir))
                            {
                                Directory.Move(tempPath, childDir);
                                _logger.Log($"[批量智能路径]   已恢复临时目录到原始位置（父目录非空）", ConsoleColor.Yellow);
                                
                                // 还原解压节点标记
                                if (_extractedDirectoryNodes.ContainsKey(tempPath))
                                {
                                    _extractedDirectoryNodes.TryRemove(tempPath, out _);
                                    _extractedDirectoryNodes.TryAdd(childDir, 0);
                                }
                            }
                        }
                        catch (Exception restoreEx)
                        {
                            _logger.Log($"[批量智能路径] ⚠ 恢复临时目录失败: {restoreEx.Message}", ConsoleColor.Red);
                        }
                        
                        return;  // 跳过步骤3，不执行扁平化
                    }
                }

                // 步骤3: 重命名为最终名称
                string finalPath = Path.Combine(parentDirPath, finalName);
                
                // 重名裁决统一交给 ConflictResolver：非解压节点的用户原始目录在任何策略下都不会被递归删除
                var renameDecision = _conflicts.Resolve(tempPath, finalPath, ConflictKind.Directory,
                    _extractedDirectoryNodes.ContainsKey(finalPath), NamingStyle.GuidPrefix);
                if (renameDecision.Note != null)
                    _logger.Log($"[批量智能路径] {renameDecision.Note}", renameDecision.NoteColor);
                if (renameDecision.ShouldDeleteTarget)
                    Directory.Delete(renameDecision.FinalPath, true);
                finalPath = renameDecision.FinalPath;

                // 添加重试机制，防止文件占用导致卡住
                retryCount = 0;
                moveSuccess = false;
                
                while (retryCount < maxRetries && !moveSuccess)
                {
                    try
                    {
                        Directory.Move(tempPath, finalPath);
                        moveSuccess = true;
                        _logger.Log($"[批量智能路径]   ✓ 扁平化完成: {Path.GetFileName(finalPath)}", ConsoleColor.Green);
                        
                        // 更新解压节点标记：将临时路径替换为最终路径（避免重复条目）
                        if (_extractedDirectoryNodes.ContainsKey(tempPath))
                        {
                            _extractedDirectoryNodes.TryRemove(tempPath, out _);
                            if (!_extractedDirectoryNodes.ContainsKey(finalPath))
                            {
                                _extractedDirectoryNodes.TryAdd(finalPath, 0);
                                _logger.Log($"[批量智能路径] [DEBUG] 更新解压节点标记: {tempPath} -> {finalPath}", ConsoleColor.Gray);
                            }
                            else
                            {
                                _logger.Log($"[批量智能路径] [DEBUG] 解压节点标记已存在，跳过重复添加: {finalPath}", ConsoleColor.Gray);
                            }
                        }
                        
                        // 【修复】记录扁平化结果映射：parentDir 已被删除，其内容最终位于 finalPath
                        // 同时链式更新：之前映射到 childDir/tempPath 的条目也指向 finalPath（覆盖多层嵌套扁平化）
                        foreach (var key in new List<string>(_flattenedFinalPathMap.Keys))
                        {
                            if (string.Equals(_flattenedFinalPathMap[key], childDir, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(_flattenedFinalPathMap[key], tempPath, StringComparison.OrdinalIgnoreCase))
                            {
                                _flattenedFinalPathMap[key] = finalPath;
                            }
                        }
                        _flattenedFinalPathMap[parentDir] = finalPath;
                        _logger.Log($"[批量智能路径] [DEBUG] 记录扁平化映射: {parentDir} -> {finalPath}", ConsoleColor.Gray);
                    }
                    catch (IOException ioEx) when (retryCount < maxRetries - 1)
                    {
                        retryCount++;
                        _logger.Log($"[批量智能路径] ⚠ 重命名失败（文件占用？），重试 {retryCount}/{maxRetries}: {ioEx.Message}", ConsoleColor.Yellow);
                        Thread.Sleep(500); // 等待500ms后重试
                    }
                }
                
                if (!moveSuccess)
                {
                    _logger.Log($"[批量智能路径] ✗ 重命名失败，保留临时目录: {tempName}", ConsoleColor.Red);
                    
                    // 尝试清理：将临时目录重命名回原始位置，避免残留
                    try
                    {
                        if (Directory.Exists(tempPath) && !Directory.Exists(childDir))
                        {
                            Directory.Move(tempPath, childDir);
                            _logger.Log($"[批量智能路径]   已恢复临时目录到原始位置", ConsoleColor.Yellow);
                        }
                    }
                    catch (Exception restoreEx)
                    {
                        _logger.Log($"[批量智能路径]  恢复临时目录失败: {restoreEx.Message}", ConsoleColor.Yellow);
                    }
                    
                    return;
                }

                // 【已移除步骤4】后序遍历本身就会自然上溯处理父目录，无需额外递归重新处理
            }
            catch (Exception ex)
            {
                _logger.Log($"[批量智能路径] 扁平化失败: {ex.Message}", ConsoleColor.Red);
            }
        }

    }
}
