using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>
    /// 文件收集：拖入文件/文件夹后的命名清洗、黑名单过滤、分卷与压缩包识别，
    /// 并把条目写入文件树（ObservableCollection<FileItem>）。
    /// </summary>
    public sealed class FileCollector
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly ArchiveDetector _detector;
        private readonly VolumeDetector _volumeDetector;
        private readonly ArchiveNameCleaner _archiveNameCleaner;
        private readonly BlacklistService _blacklistService;
        private readonly FileOperationService _fileOps;
        private ObservableCollection<FileItem> _fileList = null!;

        /// <summary>鏍硅妭鐐规爣棰橀渶瑕佸埛鏂版椂鐨勫涓诲洖璋冿紙鏍囬鏄?UI 缁戝畾鏂囨湰锛夈€?/summary>
        public Action? OnTitleChanged { get; set; }

        public FileCollector(AppSettings settings, IAppLogger logger, ArchiveDetector detector,
            VolumeDetector volumeDetector, ArchiveNameCleaner archiveNameCleaner, BlacklistService blacklistService, FileOperationService fileOps)
        {
            _settings = settings;
            _logger = logger;
            _detector = detector;
            _volumeDetector = volumeDetector;
            _archiveNameCleaner = archiveNameCleaner;
            _blacklistService = blacklistService;
            _fileOps = fileOps;
        }

        /// <summary>绑定文件树集合（集合实例不做替换）</summary>
        public void AttachFileList(ObservableCollection<FileItem> fileList)
        {
            _fileList = fileList;
        }

        /// <summary>
        /// 输入侧去重的唯一入口：判据是「完整路径」，不比文件名、不比内容。
        ///
        /// 命中同名路径时先判断已存在节点是否为历史残留：终态 + 原文件已不在磁盘上
        /// 且子树无在途任务 → 回收该节点（连同子树）并放行新文件。
        /// 不这样处理的话，上一个同名压缩包解压完被删掉后，带子包的成功节点会永久留在树里
        /// （CheckAndMarkParentComplete 只移除无子项的顶级节点），后续重新拖入同名文件会被静默跳过。
        /// </summary>
        private bool IsAlreadyTracked(string filePath)
        {
            for (int i = _fileList.Count - 1; i >= 0; i--)
            {
                var node = _fileList[i];
                // Windows 路径大小写不敏感，用 OrdinalIgnoreCase（原来的 == 会漏判大小写不同的同一文件）
                if (!string.Equals(node.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsStaleHistoryNode(node))
                {
                    _fileList.RemoveAt(i);
                    _logger.Log($"[输入去重] 同名旧任务「{node.FileName}」已完成且原文件已不在磁盘上，回收历史节点以重新处理", ConsoleColor.Cyan);
                    continue;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// 是否可回收的历史节点：顶级 + 已进入终态 + 原文件已不存在 + 整棵子树没有在途任务。
        /// 四个条件缺一不可：解压失败的节点原文件还在（不回收）、正在处理的节点子树还活着（不回收）。
        /// 终态词表与在途判定都来自 FileItem，与设置页旁的「清理已完成节点」共用同一套语义。
        /// </summary>
        private static bool IsStaleHistoryNode(FileItem node)
        {
            return node.Parent == null
                && !node.HasInFlightInSubtree()
                && node.IsFinished
                && !File.Exists(node.FilePath);
        }

        /// <summary>
        /// 添加单个文件到列表
        /// </summary>
        public void AddFileToList(string filePath, ref int addedCount, List<FileItem>? newFileItems = null, string? sourceFolderName = null, FileOrigin origin = FileOrigin.DroppedFile)
        {
            // 检查是否已存在（含历史残留节点的回收）
            if (IsAlreadyTracked(filePath))
                return;

            var fileInfo = new FileInfo(filePath);
            var fileItem = new FileItem
            {
                FileName = fileInfo.Name,
                FilePath = filePath,
                Status = "等待处理",
                FileSize = fileInfo.Length,
                SourceFolderName = sourceFolderName,
                Origin = origin
            };
            _fileList.Add(fileItem);
            addedCount++;
            OnTitleChanged?.Invoke();
            
            // 记录新文件
            if (newFileItems != null)
            {
                newFileItems.Add(fileItem);
            }
        }

        /// <summary>
        /// 处理单个文件（包括黑名单检查和分卷检测）
        /// 注意：只处理当前文件及其分卷，不扫描同级目录的其他压缩文件
        /// </summary>
        public void ProcessSingleFile(string filePath, ref int addedCount, List<FileItem>? newFileItems = null)
        {
            // 防和谐文件名预清理：检测前先清理压缩扩展名后的防和谐字符（分卷整组原子重命名）
            filePath = _archiveNameCleaner.PreCleanFileNamesForSingleFile(filePath);

            // 检查黑名单
            if (_blacklistService.IsBlacklistedFile(filePath, out string matchedPattern))
            {
                _blacklistService.DeleteBlacklistedFile(filePath, matchedPattern);
                return;  // 跳过后续处理
            }

            // 检测是否为压缩文件
            if (_detector.IsArchiveFile(filePath))
            {
                // 如果是分卷压缩包，自动检测所有分卷（仅在同级目录中查找分卷文件）
                if (_settings.EnableMultiVolumeDetection && _volumeDetector.IsMultiVolumeArchive(filePath))
                {
                    var volumeInfo = _volumeDetector.DetectMultiVolumeArchive(filePath);
                    
                    if (volumeInfo.HasVolumeList)
                    {
                        _logger.Log($"检测到分卷压缩包: {volumeInfo.VolumeCount} 个分卷", ConsoleColor.Cyan);
                        
                        // 只添加主卷到列表
                        var mainFileInfo = new FileInfo(volumeInfo.MainVolumePath);
                        var fileItem = new FileItem
                        {
                            FileName = mainFileInfo.Name,
                            FilePath = volumeInfo.MainVolumePath,
                            Status = volumeInfo.IsMultiVolume
                                ? $"等待处理 (分卷: {volumeInfo.VolumeCount})"
                                : $"等待处理 (分卷主文件)",
                            FileSize = volumeInfo.AllVolumePaths.Sum(p => new FileInfo(p).Length)
                        };
                        
                        // 存储分卷信息
                        fileItem.VolumeInfo = volumeInfo;
                        
                        if (!IsAlreadyTracked(volumeInfo.MainVolumePath))
                        {
                            _fileList.Add(fileItem);
                            addedCount++;
                            OnTitleChanged?.Invoke();
                            
                            // 记录新文件
                            if (newFileItems != null)
                            {
                                newFileItems.Add(fileItem);
                            }
                        }
                    }
                    else
                    {
                        // 不是真正的分卷，当作普通文件处理
                        AddFileToList(filePath, ref addedCount, newFileItems);
                    }
                }
                else
                {
                    AddFileToList(filePath, ref addedCount, newFileItems);
                }
            }
        }

        /// <summary>
        /// 递归扫描文件夹中的压缩文件
        /// 注意：只扫描指定文件夹内部（包括子文件夹），不处理同级目录的其他文件或文件夹
        /// </summary>
        public void ScanDirectoryForArchives(string directoryPath, ref int addedCount, List<FileItem>? newFileItems = null)
        {
            try
            {
                // 获取源文件夹名称，用于 UI 显示
                string folderName = Path.GetFileName(directoryPath);
                
                // 获取文件夹中的所有文件（包括子文件夹）
                string[] files = _fileOps.EnumerateFilesSafe(directoryPath, "*.*", allDirectories: true).ToArray();
                
                _logger.Log($"正在扫描文件夹: {directoryPath}", ConsoleColor.Gray);
                
                // 【防和谐清理】检测压缩文件前，先清理文件名中的防和谐字符（分卷整组原子重命名）
                files = _archiveNameCleaner.CleanDirtyArchiveFileNames(files.ToList()).ToArray();
                
                _logger.Log($"找到 {files.Length} 个文件，正在筛选压缩文件...", ConsoleColor.Gray);
                
                // 首先收集所有可能的压缩文件
                var potentialArchives = new List<string>();
                foreach (string file in files)
                {
                    // 检查黑名单
                    if (_blacklistService.IsBlacklistedFile(file, out string matchedPattern))
                    {
                        _blacklistService.DeleteBlacklistedFile(file, matchedPattern);
                        continue;
                    }

                    // 初步筛选：统一走 Classify（文件夹来源应用排除列表，解压产物伪装后缀会被魔术数识破）
                    if (_detector.Classify(file, FileOrigin.FolderScan) != ArchiveDetector.ArchiveKind.NotArchive)
                    {
                        potentialArchives.Add(file);
                    }
                }
                
                _logger.Log($"发现 {potentialArchives.Count} 个压缩文件，正在处理分卷合并...", ConsoleColor.Gray);
                
                // 处理分卷压缩：将同一组分卷合并为一个条目
                var processedVolumes = new HashSet<string>(); // 已处理的分卷文件
                
                // 首先按目录分组，以便更好地检测分卷
                var archivesByDirectory = potentialArchives.GroupBy(f => Path.GetDirectoryName(f) ?? string.Empty);
                
                foreach (var directoryGroup in archivesByDirectory)
                {
                    string currentDir = directoryGroup.Key;
                    var archivesInDir = directoryGroup.ToList();
                    
                    foreach (string archiveFile in archivesInDir)
                    {
                        // 如果这个文件已经被作为分卷的一部分处理过了，跳过
                        if (processedVolumes.Contains(archiveFile))
                            continue;
                        
                        // 检查是否为分卷压缩
                        if (_settings.EnableMultiVolumeDetection && _volumeDetector.IsMultiVolumeArchive(archiveFile))
                        {
                            var volumeInfo = _volumeDetector.DetectMultiVolumeArchive(archiveFile);
                            
                            if (volumeInfo.HasVolumeList)
                            {
                                _logger.Log($"检测到分卷压缩包: {volumeInfo.VolumeCount} 个分卷", ConsoleColor.Cyan);
                                
                                // 标记所有分卷为已处理
                                foreach (var volumePath in volumeInfo.AllVolumePaths)
                                {
                                    processedVolumes.Add(volumePath);
                                }
                                
                                // 只添加主卷到列表
                                var mainFileInfo = new FileInfo(volumeInfo.MainVolumePath);
                                var fileItem = new FileItem
                                {
                                    FileName = mainFileInfo.Name,
                                    FilePath = volumeInfo.MainVolumePath,
                                    Status = volumeInfo.IsMultiVolume
                                        ? $"等待处理 (分卷: {volumeInfo.VolumeCount})"
                                        : $"等待处理 (分卷主文件)",
                                    FileSize = volumeInfo.AllVolumePaths.Sum(p => new FileInfo(p).Length),
                                    SourceFolderName = folderName,
                                    Origin = FileOrigin.FolderScan
                                };
                                
                                // 存储分卷信息
                                fileItem.VolumeInfo = volumeInfo;
                                
                                if (!IsAlreadyTracked(volumeInfo.MainVolumePath))
                                {
                                    _fileList.Add(fileItem);
                                    addedCount++;
                                    OnTitleChanged?.Invoke();
                                    _logger.Log($"  已添加分卷压缩包: {mainFileInfo.Name}", ConsoleColor.Cyan);
                                    
                                    // 记录新文件
                                    if (newFileItems != null)
                                    {
                                        newFileItems.Add(fileItem);
                                    }
                                }
                            }
                            else
                            {
                                // 不是真正的分卷，当作普通文件处理
                                if (!processedVolumes.Contains(archiveFile))
                                {
                                    AddFileToList(archiveFile, ref addedCount, newFileItems, folderName, FileOrigin.FolderScan);
                                    processedVolumes.Add(archiveFile);
                                }
                            }
                        }
                        else
                        {
                            // 普通压缩文件
                            if (!processedVolumes.Contains(archiveFile))
                            {
                                AddFileToList(archiveFile, ref addedCount, newFileItems, folderName, FileOrigin.FolderScan);
                                processedVolumes.Add(archiveFile);
                            }
                        }
                    }
                }
                
                _logger.Log($"文件夹扫描完成，共添加 {addedCount} 个压缩文件", ConsoleColor.Blue);
            }
            catch (Exception ex)
            {
                _logger.Log($"扫描文件夹时出错: {ex.Message}", ConsoleColor.Red);
            }
        }

    }
}
