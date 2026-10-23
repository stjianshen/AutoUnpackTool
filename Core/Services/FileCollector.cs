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
        private ObservableCollection<FileItem> _fileList = null!;

        /// <summary>鏍硅妭鐐规爣棰橀渶瑕佸埛鏂版椂鐨勫涓诲洖璋冿紙鏍囬鏄?UI 缁戝畾鏂囨湰锛夈€?/summary>
        public Action? OnTitleChanged { get; set; }

        public FileCollector(AppSettings settings, IAppLogger logger, ArchiveDetector detector,
            VolumeDetector volumeDetector, ArchiveNameCleaner archiveNameCleaner, BlacklistService blacklistService)
        {
            _settings = settings;
            _logger = logger;
            _detector = detector;
            _volumeDetector = volumeDetector;
            _archiveNameCleaner = archiveNameCleaner;
            _blacklistService = blacklistService;
        }

        /// <summary>绑定文件树集合（集合实例不做替换）</summary>
        public void AttachFileList(ObservableCollection<FileItem> fileList)
        {
            _fileList = fileList;
        }

        /// <summary>
        /// 添加单个文件到列表
        /// </summary>
        public void AddFileToList(string filePath, ref int addedCount, List<FileItem>? newFileItems = null, string? sourceFolderName = null)
        {
            // 检查是否已存在
            if (_fileList.Any(f => f.FilePath == filePath))
                return;

            var fileInfo = new FileInfo(filePath);
            var fileItem = new FileItem
            {
                FileName = fileInfo.Name,
                FilePath = filePath,
                Status = "等待处理",
                FileSize = fileInfo.Length,
                SourceFolderName = sourceFolderName
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
        /// 扫描目录中的压缩文件并添加到测试列表
        /// </summary>
        /// <param name="directoryPath">要扫描的目录路径</param>
        public void ScanAndAddArchiveFiles(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                _logger.Log($"目录不存在: {directoryPath}", ConsoleColor.Red);
                return;
            }

            try
            {
                var archiveFiles = Directory.GetFiles(directoryPath)
                    .Where(f => _detector.IsArchiveFile(f, false))
                    .ToList();

                int addedCount = 0;
                foreach (var file in archiveFiles)
                {
                    // 检查是否已在列表中
                    if (_fileList.Any(f => f.FilePath == file))
                        continue;

                    var fileInfo = new FileInfo(file);
                    var fileItem = new FileItem
                    {
                        FileName = fileInfo.Name,
                        FilePath = file,
                        Status = "等待处理",
                        FileSize = fileInfo.Length
                    };
                    _fileList.Add(fileItem);
                    addedCount++;
                    OnTitleChanged?.Invoke();
                }

                if (addedCount > 0)
                {
                    _logger.Log($"从目录中发现 {addedCount} 个新的压缩文件", ConsoleColor.Cyan);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"扫描目录失败: {ex.Message}", ConsoleColor.Red);
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
                        
                        if (!_fileList.Any(f => f.FilePath == volumeInfo.MainVolumePath))
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
                string[] files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories);
                
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

                    // 初步筛选：通过扩展名或分卷格式判断
                    if (_detector.IsArchiveFileByExtension(file) || _volumeDetector.IsMultiVolumeArchive(file))
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
                                    SourceFolderName = folderName
                                };
                                
                                // 存储分卷信息
                                fileItem.VolumeInfo = volumeInfo;
                                
                                if (!_fileList.Any(f => f.FilePath == volumeInfo.MainVolumePath))
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
                                    AddFileToList(archiveFile, ref addedCount, newFileItems, folderName);
                                    processedVolumes.Add(archiveFile);
                                }
                            }
                        }
                        else
                        {
                            // 普通压缩文件
                            if (!processedVolumes.Contains(archiveFile))
                            {
                                AddFileToList(archiveFile, ref addedCount, newFileItems, folderName);
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
