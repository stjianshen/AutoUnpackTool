using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.Core.Infrastructure;
using AutoUnpackTool.Core.Models;
using AutoUnpackTool.Core.Services;
using AutoUnpackTool.Services;

namespace AutoUnpackTool
{
    public partial class MainWindow : Window
    {
        private RootNode _rootNode = new RootNode { Title = "待解压任务(共0个文件)" };
        private ObservableCollection<FileItem> _fileList => _rootNode.Children;
        private AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly BlacklistService _blacklistService;
        private readonly ArchiveNameCleaner _archiveNameCleaner;
        private readonly VolumeDetector _volumeDetector;
        private readonly ArchiveDetector _detector;
        private readonly StegoService _stego;
        private readonly FileCleanupService _cleanup;
        private readonly SmartPathService _smartPath;
        private PasswordMap _passwordMap = new PasswordMap(); // 密码映射表

        private bool _suppressBlacklistToggleEvent = true; // 初始化期间不响应开关变化
        
        // 双队列架构：待处理队列（测试密码）和待解压队列（已测试密码）
        private ConcurrentQueue<FileItem> _pendingQueue = new();     // 待处理队列
        private ConcurrentQueue<FileItem> _extractQueue = new();     // 待解压队列
        
        // 队列状态监控 - 用于唤醒休眠的线程
        private ManualResetEventSlim _pendingQueueSignal = new(false);   // 待处理队列有数据信号
        private ManualResetEventSlim _extractQueueSignal = new(false);   // 待解压队列有数据信号
        
        private CancellationTokenSource? _testCancellationTokenSource;
        private CancellationTokenSource? _extractCancellationTokenSource;
        private CancellationTokenSource? _globalCancellationTokenSource; // 全局取消令牌，用于清空时终止所有操作
        private bool _isTesting = false;
        private bool _isExtracting = false;
        private bool _isMonitoring = false;
        
        // 暂停状态标志 - 用于优雅暂停（等待当前任务完成后再停止）
        private bool _isPaused = false;
        
        // 任务完成信号
        private TaskCompletionSource<bool> _testCompletionSource = new();
        private TaskCompletionSource<bool> _extractCompletionSource = new();
        
        // 记录所有顶级 FileItem
        private List<FileItem> _topLevelItems = new();
        
        // 记录所有解压产生的目录节点（用于扁平化时只处理解压的目录）
        // 【并发修复】ConcurrentDictionary 保证解压线程 Add 与收尾线程遍历/移除的线程安全
        // （值无意义，仅用作线程安全集合的占位）
        // 共享集合由 SmartPathService 持有，MainWindow 侧通过属性转发，避免大范围改名
        private ConcurrentDictionary<string, byte> _extractedDirectoryNodes => _smartPath.ExtractedDirectoryNodes;
        
        // 记录扁平化结果映射：被扁平化删除的解压目录 -> 内容最终所在路径（用于最终路径追踪）
        private Dictionary<string, string> _flattenedFinalPathMap => _smartPath.FlattenedFinalPathMap;
        
        // 记录拖入的文件夹（用于后续智能路径处理，即使没有压缩包）
        private HashSet<string> _droppedFolders = new(StringComparer.OrdinalIgnoreCase);
        
        // 记录已处理的扁平化目录（防止重复处理导致死循环）
        private HashSet<string> _processedFlattenDirs => _smartPath.ProcessedFlattenDirs;

        // 【并发修复】收尾流程串行化锁：多个顶级文件并发完成时，收尾（清理+智能路径扁平化）必须串行执行，
        // 否则并发移动同一目录树会导致移动中断、目录冲突时生成 GUID 新目录、图片分散到多个文件夹。
        private readonly SemaphoreSlim _smartPathFinalizeLock = new(1, 1);

        // 【并发修复】防止同一顶级文件被重复触发收尾（完成回调可能从多条路径触发，如叶子收尾与父链上报）
        private readonly HashSet<string> _finalizingTopArchives = new(StringComparer.OrdinalIgnoreCase);

        // 【并发修复】保护 _isTesting/_isExtracting 的 check-then-act：多个后台线程（解压线程唤醒、监控线程重启）
        // 可能同时看到 false 而启动重复线程组，必须原子地检查并设置
        private readonly object _threadStateLock = new();


        public MainWindow()
        {
            InitializeComponent();

            // 日志通道：Core 层只依赖 IAppLogger，实际呈现由本窗口负责
            _logger = new UiAppLogger(Dispatcher, entry =>
            {
                TxtLog.AppendText($"[{entry.Timestamp:HH:mm:ss}]--> {entry.Message}\r\n");
                TxtLog.ScrollToEnd();
            });

            LstFiles.ItemsSource = new ObservableCollection<RootNode> { _rootNode };
            
            // 加载设置
            _settings = AppSettings.Load();

            // 黑名单服务（规则编译缓存 + 匹配 + 删除）
            _blacklistService = new BlacklistService(_settings, _logger);

            // 防和谐文件名清理 / 分卷识别
            _archiveNameCleaner = new ArchiveNameCleaner(_settings, _logger);
            _volumeDetector = new VolumeDetector();

            // 压缩包/隐写检测（Stego 与 Detector 互相引用，构造后回填）
            _detector = new ArchiveDetector(_settings, _logger, _volumeDetector);
            _stego = new StegoService(_settings, _logger, _detector);
            _detector.Stego = _stego;

            // 原文件/分卷清理
            _cleanup = new FileCleanupService(_settings, _logger);

            // 智能路径（扁平化）处理；状态回调留在窗口内（需要 Dispatcher 封送）
            _smartPath = new SmartPathService(_settings, _logger, _detector, _volumeDetector, _blacklistService);
            _smartPath.OnSmartPathCompleted = UpdateSmartPathCompletedStatus;
            _smartPath.GetAllItemsSnapshot = SnapshotAllItems;
            
            // 根据设置初始化单选按钮状态
            if (_settings.ExtractMode == ExtractMode.Auto)
            {
                RdoAutoMode.IsChecked = true;
            }
            else
            {
                RdoManualMode.IsChecked = true;
            }
            
            // 注册单选按钮状态变化事件
            RdoAutoMode.Checked += (s, e) => 
            { 
                _settings.ExtractMode = ExtractMode.Auto;
                _settings.Save(); // 保存配置到文件
                UpdateUiState();
                
                // 切换到自动模式时，检查是否有待解压的文件
                CheckAndTriggerAutoExtract();
            };
            RdoManualMode.Checked += (s, e) => 
            { 
                _settings.ExtractMode = ExtractMode.Manual;
                _settings.Save(); // 保存配置到文件
                UpdateUiState();
            };
            
            AppendLog("系统就绪，请拖拽压缩文件到窗口...", ConsoleColor.Green);
            
            // 如果有设置，显示提示
            if (!string.IsNullOrEmpty(_settings.SevenZipPath))
            {
                AppendLog($"已加载配置: 7z.exe = {_settings.SevenZipPath}", ConsoleColor.Cyan);
                
                // 检查是否是 NanaZip 的 WindowsApps 路径
                if (_settings.SevenZipPath.Contains("WindowsApps") || _settings.SevenZipPath.Contains("NanaZip"))
                {
                    AppendLog("⚠️  警告: 检测到 NanaZip (UWP版本)", ConsoleColor.Yellow);
                    AppendLog("   UWP应用可能有权限限制，建议使用标准 7-Zip", ConsoleColor.Yellow);
                    AppendLog("   下载地址: https://www.7-zip.org/", ConsoleColor.Gray);
                }
            }
            else
            {
                AppendLog("提示：请先在\"软件设置\"中配置 7z.exe 路径", ConsoleColor.Yellow);
            }
            
            // 初始化UI状态
            UpdateUiState();
            
            // 初始化强制解压模式复选框状态
            ChkForceExtractMode.IsChecked = _settings.ForceExtractMode;
            
            // 初始化黑名单启用开关（SkipBlacklist 取反）
            _suppressBlacklistToggleEvent = true;
            ChkEnableBlacklist.IsChecked = !_settings.SkipBlacklist;
            _suppressBlacklistToggleEvent = false;
            
            // 初始化最大解压层数下拉框
            InitMaxExtractDepthComboBox();
        }

        #region 拖拽事件

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            // 判断是否有文件拖拽
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                int addedCount = 0;
                var newFileItems = new List<FileItem>();  // 记录新添加的文件
                
                foreach (string path in files)
                {
                    // 判断是文件还是文件夹
                    if (File.Exists(path))
                    {
                        // 处理单个文件：只处理该文件及其分卷，不扫描同级目录的其他压缩文件
                        ProcessSingleFile(path, ref addedCount, newFileItems);
                    }
                    else if (Directory.Exists(path))
                    {
                        // 处理文件夹：只扫描该文件夹内部（包括子文件夹），不处理同级目录的其他文件或文件夹
                        AppendLog($"检测到文件夹: {path}", ConsoleColor.Cyan);
                        
                        // 记录拖入的文件夹，即使没有压缩包也会进行智能路径处理
                        _droppedFolders.Add(path);
                        
                        ScanDirectoryForArchives(path, ref addedCount, newFileItems);
                    }
                }

                if (addedCount > 0)
                {
                    AppendLog($"已添加 {addedCount} 个压缩文件到列表", ConsoleColor.Blue);
                    
                    // 将新文件添加到待处理队列（避免重复添加）
                    foreach (var fileItem in newFileItems)
                    {
                        _pendingQueue.Enqueue(fileItem);
                        AppendLog($"  [队列] {fileItem.FileName} 已加入待测试队列", ConsoleColor.Gray);
                    }
                    
                    AppendLog("自动开始处理流程...", ConsoleColor.Cyan);
                    
                    // 启动双队列处理流程（如果线程未运行）
                    // StartDualQueueProcessing 内部已经包含了唤醒逻辑
                    StartDualQueueProcessing();
                    
                    // 如果线程已经在运行，只需要唤醒测试线程即可
                    // （解压线程会在测试完成后被自动唤醒）
                    if (_isTesting || _isExtracting)
                    {
                        WakeupTestThread();
                    }
                }
                else
                {
                    AppendLog("未检测到压缩文件", ConsoleColor.Yellow);
                    
                    // 即使没有压缩包，如果启用了智能路径处理，也应该对拖入的文件夹进行处理
                    if (_settings.EnableSmartPathProcessing && _settings.OutputMode == OutputMode.ArchiveFolder && _droppedFolders.Count > 0)
                    {
                        AppendLog("虽然没有压缩文件，但将对拖入的文件夹进行智能路径处理", ConsoleColor.Cyan);
                        var folders = _droppedFolders.ToList();
                        _droppedFolders.Clear();
                        
                        _ = Task.Run(async () =>
                        {
                            // 【并发修复】持锁串行执行，避免与解压收尾流程并发移动同一目录树
                            await _smartPathFinalizeLock.WaitAsync();
                            try
                            {
                                foreach (var folder in folders)
                                {
                                    await _smartPath.ProcessSmartPathBatchAsync(folder);
                                    
                                    // 更新最终路径显示
                                    string? finalPath = _smartPath.FindFinalFlattenedPath(folder);
                                    Dispatcher.Invoke(() =>
                                    {
                                        TxtFinalPath.Text = finalPath ?? folder;
                                        AppendLog($"[智能路径] 文件夹最终路径: {TxtFinalPath.Text}", ConsoleColor.Green);
                                    });
                                }
                            }
                            finally
                            {
                                _smartPathFinalizeLock.Release();
                            }
                        });
                    }
                }
            }
        }

        /// <summary>
        /// 添加单个文件到列表
        /// </summary>
        private void AddFileToList(string filePath, ref int addedCount, List<FileItem>? newFileItems = null, string? sourceFolderName = null)
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
            UpdateRootNodeTitle();
            
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
        private void ProcessSingleFile(string filePath, ref int addedCount, List<FileItem>? newFileItems = null)
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
                        AppendLog($"检测到分卷压缩包: {volumeInfo.VolumeCount} 个分卷", ConsoleColor.Cyan);
                        
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
                            UpdateRootNodeTitle();
                            
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
        private void ScanDirectoryForArchives(string directoryPath, ref int addedCount, List<FileItem>? newFileItems = null)
        {
            try
            {
                // 获取源文件夹名称，用于 UI 显示
                string folderName = Path.GetFileName(directoryPath);
                
                // 获取文件夹中的所有文件（包括子文件夹）
                string[] files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories);
                
                AppendLog($"正在扫描文件夹: {directoryPath}", ConsoleColor.Gray);
                
                // 【防和谐清理】检测压缩文件前，先清理文件名中的防和谐字符（分卷整组原子重命名）
                files = _archiveNameCleaner.CleanDirtyArchiveFileNames(files.ToList()).ToArray();
                
                AppendLog($"找到 {files.Length} 个文件，正在筛选压缩文件...", ConsoleColor.Gray);
                
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
                
                AppendLog($"发现 {potentialArchives.Count} 个压缩文件，正在处理分卷合并...", ConsoleColor.Gray);
                
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
                                AppendLog($"检测到分卷压缩包: {volumeInfo.VolumeCount} 个分卷", ConsoleColor.Cyan);
                                
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
                                    UpdateRootNodeTitle();
                                    AppendLog($"  已添加分卷压缩包: {mainFileInfo.Name}", ConsoleColor.Cyan);
                                    
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
                
                AppendLog($"文件夹扫描完成，共添加 {addedCount} 个压缩文件", ConsoleColor.Blue);
            }
            catch (Exception ex)
            {
                AppendLog($"扫描文件夹时出错: {ex.Message}", ConsoleColor.Red);
            }
        }

        #endregion

        #region 按钮事件

        private void BtnSelectDirectory_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择解压输出目录"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtOutputDirectory.Text = dialog.FolderName;
                AppendLog($"输出目录设置为: {dialog.FolderName}", ConsoleColor.Cyan);
            }
        }

        private async void BtnStartExtract_Click(object? sender, RoutedEventArgs e)
        {
            // 自动模式下，检查是否有待处理的任务需要继续
            if (_settings.ExtractMode == ExtractMode.Auto)
            {
                // 如果处于暂停状态，先清除暂停标志
                if (_isPaused)
                {
                    AppendLog("\n继续处理...", ConsoleColor.Cyan);
                    _isPaused = false;
                    
                    // 唤醒线程继续处理
                    _pendingQueueSignal.Set();
                    _extractQueueSignal.Set();
                    
                    UpdateUiState();
                    return;
                }
                
                bool needTestThread = !_isTesting && !_pendingQueue.IsEmpty;
                bool needExtractThread = !_isExtracting && !_extractQueue.IsEmpty;

                if (!needTestThread && !needExtractThread)
                {
                    if (_isTesting || _isExtracting)
                        AppendLog("处理流程已在运行中...", ConsoleColor.Yellow);
                    else
                        AppendLog("队列为空，等待新文件...", ConsoleColor.Gray);
                    return;
                }

                AppendLog("继续处理剩余任务...", ConsoleColor.Cyan);

                if (needTestThread && needExtractThread)
                {
                    StartDualQueueProcessing();
                }
                else if (needTestThread)
                {
                    WakeupTestThread();
                }
                else
                {
                    WakeupExtractThread();
                }
                return;
            }

            // 手动模式：开始解压
            if (_fileList.Count == 0)
            {
                MessageBox.Show("请先添加文件！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.SevenZipPath) || !File.Exists(_settings.SevenZipPath))
            {
                MessageBox.Show("请先在\"软件设置\"中配置 7z.exe 路径！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 调试日志：输出密码映射表状态
            AppendLog($"[调试] 密码映射表Count: {_passwordMap.Count}", ConsoleColor.Magenta);
            foreach (var fileItem in _fileList)
            {
                if (_passwordMap.TryGetValue(fileItem.FilePath, out var pwd))
                {
                    AppendLog($"[调试] {fileItem.FileName} -> 密码: {pwd ?? "(无密码)"}", ConsoleColor.Magenta);
                }
                else
                {
                    AppendLog($"[调试] {fileItem.FileName} -> 未找到密码", ConsoleColor.Magenta);
                }
            }

            if (_passwordMap.Count == 0)
            {
                MessageBox.Show("请先进行密码测试！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 启动双队列处理流程
            StartDualQueueProcessing();
        }

        /// <summary>
        /// 仅执行密码测试，不解压
        /// </summary>
        private async void StartPasswordTestOnly()
        {
            // 将文件添加到待处理队列
            foreach (var fileItem in _fileList)
            {
                if (!_pendingQueue.Contains(fileItem))
                {
                    _pendingQueue.Enqueue(fileItem);
                }
            }
            
            // 启动双队列处理流程（只会运行测试线程）
            StartDualQueueProcessing();
            
            // 唤醒测试线程
            WakeupTestThread();
        }

        /// <summary>
        /// 密码配置按钮点击事件
        /// </summary>
        private void BtnPasswordConfig_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new PasswordManagerDialog(_settings);
            dialog.Owner = this;
            
            if (dialog.ShowDialog() == true)
            {
                // 设置已保存
                AppendLog("密码本已更新", ConsoleColor.Cyan);
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsDialog(_settings);
            dialog.Owner = this;
            
            if (dialog.ShowDialog() == true)
            {
                // 设置已保存，更新本地引用
                _settings = dialog.Settings;
                AppendLog("设置已更新", ConsoleColor.Cyan);
                AppendLog($"7z.exe 路径: {_settings.SevenZipPath}", ConsoleColor.Cyan);
            }
        }

        /// <summary>
        /// 强制解压模式复选框状态变化事件
        /// </summary>
        private void ChkForceExtractMode_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkForceExtractMode.IsChecked.HasValue)
            {
                _settings.ForceExtractMode = ChkForceExtractMode.IsChecked.Value;
                _settings.Save();
                
                if (_settings.ForceExtractMode)
                {
                    AppendLog("已启用强制解压模式：所有拖入的文件将跳过检测直接尝试觧压", ConsoleColor.Yellow);
                }
                else
                {
                    AppendLog("已关闭强制解压模式：恢复正常的压缩文件检测", ConsoleColor.Cyan);
                }
            }
        }

        /// <summary>
        /// 黑名单启用开关状态变化事件
        /// </summary>
        private void ChkEnableBlacklist_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressBlacklistToggleEvent)
                return;

            if (ChkEnableBlacklist.IsChecked.HasValue)
            {
                _settings.SkipBlacklist = !ChkEnableBlacklist.IsChecked.Value;
                _settings.Save();
                
                if (_settings.SkipBlacklist)
                {
                    AppendLog("已停用黑名单：匹配的文件将不会被删除", ConsoleColor.Yellow);
                }
                else
                {
                    AppendLog($"已启用黑名单：匹配的文件将被永久删除（{GetEnabledBlacklistRuleCount()} 条规则）", ConsoleColor.Cyan);
                }
            }
        }

        /// <summary>
        /// 获取启用且模式非空的黑名单规则数量
        /// </summary>
        private int GetEnabledBlacklistRuleCount()
        {
            return _settings.BlacklistRules?.Count(r => r != null && r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern)) ?? 0;
        }

        /// <summary>
        /// 初始化最大解压层数下拉框
        /// </summary>
        private void InitMaxExtractDepthComboBox()
        {
            int depth = _settings.MaxExtractDepth;
            
            // 在预定义项中查找匹配
            foreach (var item in CmbMaxExtractDepth.Items)
            {
                if (item is ComboBoxItem comboItem && comboItem.Content is string content)
                {
                    int itemDepth = content == "不限制" ? 0 : int.TryParse(content.Replace(" 层", ""), out int d) ? d : -1;
                    if (itemDepth == depth)
                    {
                        CmbMaxExtractDepth.SelectedItem = comboItem;
                        return;
                    }
                }
            }
            
            // 自定义值：设置文本
            CmbMaxExtractDepth.Text = depth == 0 ? "不限制" : $"{depth} 层(自定义)";
        }

        /// <summary>
        /// 保存解压层数设置
        /// </summary>
        private void SaveMaxExtractDepth(int depth)
        {
            _settings.MaxExtractDepth = depth;
            _settings.Save();
            AppendLog(depth == 0 ? "最大解压层数设置为: 不限制（递归解压所有嵌套压缩包）" : $"最大解压层数设置为: {depth} 层", ConsoleColor.Cyan);
        }

        /// <summary>
        /// 最大解压层数下拉框选择变化事件
        /// </summary>
        private void CmbMaxExtractDepth_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbMaxExtractDepth.SelectedItem is ComboBoxItem item && item.Content is string content && !string.IsNullOrEmpty(content))
            {
                int depth = content == "不限制" ? 0 : int.Parse(content.Replace(" 层", ""));
                SaveMaxExtractDepth(depth);
            }
        }

        /// <summary>
        /// 最大解压层数下拉框失去焦点事件（处理自定义输入）
        /// </summary>
        private void CmbMaxExtractDepth_LostFocus(object sender, RoutedEventArgs e)
        {
            // 如果选中了预定义项，由 SelectionChanged 处理
            if (CmbMaxExtractDepth.SelectedItem is ComboBoxItem)
                return;
            
            string text = CmbMaxExtractDepth.Text.Trim();
            if (int.TryParse(text, out int depth) && depth >= 0)
            {
                SaveMaxExtractDepth(depth);
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            // 1. 取消所有正在运行的操作
            CancelAllOperations();
            
            // 2. 等待一小段时间让线程退出
            Task.Delay(500).Wait();
            
            // 3. Kill掉所有7z进程
            _cleanup.KillSevenZipProcesses();
            
            // 4. Kill掉所有NanaZip进程
            _cleanup.KillNanaZipProcesses();
            
            // 5. 清空UI和数据
            _fileList.Clear();
            _passwordMap = new PasswordMap();
            _pendingQueue = new ConcurrentQueue<FileItem>();
            _extractQueue = new ConcurrentQueue<FileItem>();
            _topLevelItems.Clear();
            _extractedDirectoryNodes.Clear();  // 清空觧压目录标记
            _flattenedFinalPathMap.Clear();    // 清空扁平化结果映射
            _detector.ClearStegoProbeCache();
            _stego.ClearStegoArchiveTypeCache();
            _stego.ClearStegoArchiveValidFilesCache();
            _stego.ClearStegoSubStreamCache();
            TxtLog.Clear();
            UpdateRootNodeTitle();
            
            // 6. 重置状态
            _isTesting = false;
            _isExtracting = false;
            _isPaused = false;
            
            // 7. 重置信号
            _pendingQueueSignal.Reset();
            _extractQueueSignal.Reset();
            
            // 8. 重置完成信号
            _testCompletionSource = new TaskCompletionSource<bool>();
            _extractCompletionSource = new TaskCompletionSource<bool>();
            
            AppendLog("已清空内容，程序已重置到初始状态", ConsoleColor.Gray);
            
            // 9. 更新UI状态
            UpdateUiState();
        }
        
        /// <summary>
        /// 取消所有正在运行的操作
        /// </summary>
        private void CancelAllOperations()
        {
            // 取消全局操作
            _globalCancellationTokenSource?.Cancel();
            _globalCancellationTokenSource?.Dispose();
            _globalCancellationTokenSource = new CancellationTokenSource();
            
            // 取消测试线程
            if (_testCancellationTokenSource != null && !_testCancellationTokenSource.IsCancellationRequested)
            {
                AppendLog("正在取消测试操作...", ConsoleColor.Yellow);
                _testCancellationTokenSource.Cancel();
            }
            
            // 取消解压线程
            if (_extractCancellationTokenSource != null && !_extractCancellationTokenSource.IsCancellationRequested)
            {
                AppendLog("正在取消解压操作...", ConsoleColor.Yellow);
                _extractCancellationTokenSource.Cancel();
            }
        }

        /// <summary>
        /// 统一操作按钮点击事件（根据状态自动切换功能）
        /// </summary>
        private void BtnMainAction_Click(object sender, RoutedEventArgs e)
        {
            if (_isPaused)
            {
                // 暂停状态 → 继续
                AppendLog("\n继续处理...", ConsoleColor.Cyan);
                _isPaused = false;
                _pendingQueueSignal.Set();
                _extractQueueSignal.Set();
                UpdateUiState();
            }
            else if (_isTesting || _isExtracting)
            {
                // 运行中 → 暂停
                AppendLog("\n正在暂停...（等待当前任务完成后停止）", ConsoleColor.Yellow);
                _isPaused = true;
                UpdateUiState();
            }
            else
            {
                // 未开始 → 开始解压
                BtnStartExtract_Click(sender, e);
            }
        }

        /// <summary>
        /// 窗口关闭事件处理
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                AppendLog("\n正在关闭程序...", ConsoleColor.Yellow);
                
                // 2. 等待一小段时间让线程退出
                Task.Delay(500).Wait();
                
                // 3. Kill掉所有7z进程（包括 NanaZip）
                _cleanup.KillSevenZipProcesses();
                _cleanup.KillNanaZipProcesses();
                
                AppendLog("程序已关闭", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                // 即使出错也允许关闭
                AppendLog($"关闭时发生错误: {ex.Message}", ConsoleColor.Red);
            }
        }

        #endregion

        #region 核心流程

        /// <summary>
        /// 启动双队列处理流程：测试线程 + 解压线程
        /// </summary>
        private void StartDualQueueProcessing()
        {
            // 如果线程正在运行，直接返回
            if (_isTesting || _isExtracting)
            {
                AppendLog("处理流程已在运行中...", ConsoleColor.Yellow);
                return;
            }

            // 重置暂停标志
            _isPaused = false;
            
            // 重置完成信号
            _testCompletionSource = new TaskCompletionSource<bool>();
            _extractCompletionSource = new TaskCompletionSource<bool>();
            
            // 注意：不要在这里重置队列信号！
            // 因为文件可能已经加入队列，需要在启动线程后通过 Wakeup 方法设置信号

            // 启动测试线程
            StartTestThread();
            
            // 启动解压线程
            StartExtractThread();
            
            // 监听完成事件
            MonitorProcessingComplete();
            
            // 立即唤醒两个线程，确保它们开始处理队列中的文件
            // （即使文件已经在队列中，也需要设置信号，因为线程可能还没进入等待状态）
            _pendingQueueSignal.Set();
            _extractQueueSignal.Set();
            AppendLog("[系统] 已唤醒测试和解压线程", ConsoleColor.Gray);
        }

        /// <summary>
        /// 唤醒测试线程（当有待处理文件加入队列时调用）
        /// </summary>
        private void WakeupTestThread()
        {
            // 如果测试线程不在运行中，需要重新启动它
            if (!_isTesting)
            {
                AppendLog("[系统] 测试线程未运行，重新启动...", ConsoleColor.Gray);
                
                // 重置完成信号
                _testCompletionSource = new TaskCompletionSource<bool>();
                
                // 启动测试线程
                StartTestThread();
            }
            
            // 设置信号唤醒正在等待的测试线程（如果有的话）
            _pendingQueueSignal.Set();
            AppendLog("[系统] 已唤醒测试线程", ConsoleColor.Gray);
        }

        /// <summary>
        /// 唤醒解压线程（当有待解压文件加入队列时调用）
        /// </summary>
        private void WakeupExtractThread()
        {
            // 如果解压线程不在运行中，需要重新启动它
            if (!_isExtracting)
            {
                AppendLog("[系统] 解压线程未运行，重新启动...", ConsoleColor.Gray);
                
                // 重置完成信号（让 MonitorProcessingComplete 能等待新的解压线程）
                _extractCompletionSource = new TaskCompletionSource<bool>();
                
                // 重新启动解压线程
                StartExtractThread();
            }
            
            // 设置信号唤醒正在等待的解压线程（如果有的话）
            _extractQueueSignal.Set();
            AppendLog("[系统] 已唤醒解压线程", ConsoleColor.Gray);
        }

        /// <summary>
        /// 启动测试线程：从待处理队列取文件，测试密码后放入待解压队列
        /// </summary>
        private async void StartTestThread()
        {
            // 【并发修复】check-then-act 原子化：解压线程唤醒与监控线程重启可能并发调用，
            // 无锁时两个线程可同时看到 _isTesting==false 而启动重复测试线程
            lock (_threadStateLock)
            {
                if (_isTesting)
                    return;

                _isTesting = true;
            }
            var tokenSource = new CancellationTokenSource();
            _testCancellationTokenSource = tokenSource;
            var token = tokenSource.Token;

            try
            {
                UpdateUiState();
                AppendLog($"\n========== 测试线程启动 ==========", ConsoleColor.Cyan);

                var extractor = new SevenZipExtractor(_settings.SevenZipPath);

                AppendLog($"加载密码本: 共 {_settings.PermanentPasswords.Count} 个永久密码 + {_settings.OneTimePasswords.Count} 个一次性密码", ConsoleColor.Cyan);

                while (!token.IsCancellationRequested)
                {
                    // 检查是否被暂停（优雅暂停：完成当前任务后停止）
                    if (_isPaused)
                    {
                        AppendLog("[测试线程] 检测到暂停标志，等待继续...", ConsoleColor.Yellow);
                        
                        // 等待直到取消暂停或被取消
                        while (_isPaused && !token.IsCancellationRequested)
                        {
                            await Task.Delay(100, token);
                        }
                        
                        if (token.IsCancellationRequested)
                            break;
                            
                        AppendLog("[测试线程] 继续处理", ConsoleColor.Cyan);
                    }
                    
                    if (_pendingQueue.TryDequeue(out var fileItem))
                    {
                        try
                        {
                            // 每次处理文件时重新获取最新的已排序密码列表（LRU算法）
                            var currentPasswords = _settings.GetAllPasswords();
                            await ProcessPendingFile(fileItem, extractor, currentPasswords, token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            DiagLog($"[DIAG] 测试文件异常: {fileItem.FileName}: {ex.GetType().Name}: {ex.Message}");
                            Dispatcher.Invoke(() => fileItem.Status = $"测试异常: {ex.Message}");
                            AppendLog($"[{fileItem.FileName}] 测试异常: {ex.Message}", ConsoleColor.Red);
                        }
                    }
                    else
                    {
                        // 队列为空，等待信号或超时
                        AppendLog("[测试线程] 队列为空，等待新任务...", ConsoleColor.Gray);
                        
                        // 等待信号（最多等待10秒）或取消请求
                        bool signaled = false;
                        try
                        {
                            signaled = await Task.Run(() => 
                                _pendingQueueSignal.Wait(TimeSpan.FromSeconds(10), token), token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        
                        if (!signaled)
                        {
                            // 超时或取消，再次检查是否真的为空
                            if (_pendingQueue.IsEmpty)
                            {
                                AppendLog("[测试线程] 超时且队列为空，测试线程完成", ConsoleColor.Gray);
                                break;
                            }
                            else
                            {
                                // 超时但队列不为空，继续处理
                                AppendLog($"[测试线程] 超时但队列仍有{_pendingQueue.Count}个任务，继续处理...", ConsoleColor.Yellow);
                            }
                        }
                        else if (_pendingQueue.IsEmpty && !_isExtracting && _extractQueue.IsEmpty)
                        {
                            // 被信号唤醒但队列为空、解压线程已退出：整个流程已结束，直接退出
                            AppendLog("[测试线程] 队列为空且解压线程已退出，测试线程完成", ConsoleColor.Gray);
                            break;
                        }
                        
                        // 重置信号，准备下一次等待
                        _pendingQueueSignal.Reset();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AppendLog("\n测试线程已取消", ConsoleColor.Yellow);
            }
            catch (Exception ex)
            {
                DiagLog($"[DIAG] 测试线程宿主异常: {ex.GetType().Name}: {ex.Message}");
                AppendLog($"\n测试线程出错: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                // 只有当前tokenSource仍是自己创建的时才清理（暂停时会置null，新线程会创建新的）
                if (_testCancellationTokenSource == tokenSource)
                {
                    _isTesting = false;
                    _isPaused = false; // 重置暂停标志
                    _testCancellationTokenSource?.Dispose();
                    _testCancellationTokenSource = null;
                    UpdateUiState();
                }

                _testCompletionSource.TrySetResult(true);

                // 测试线程正常完成后，检查待解压队列是否有内容
                // 如果被取消（暂停），不自动重启解压线程
                if (!_extractQueue.IsEmpty && !_isExtracting && !token.IsCancellationRequested)
                {
                    AppendLog("[测试线程] 检测到有待解压文件，但解压线程未运行，重新启动...", ConsoleColor.Cyan);
                    _extractCompletionSource = new TaskCompletionSource<bool>();
                    StartExtractThread();
                    _extractQueueSignal.Set();
                }
            }
        }

        /// <summary>
        /// 处理待处理文件：判断是否压缩包，测试密码，然后加入待解压队列
        /// </summary>
        private async Task ProcessPendingFile(FileItem fileItem, SevenZipExtractor extractor, List<string> passwords, CancellationToken token)
        {
            AppendLog($"[DEBUG-测试] ProcessPendingFile 被调用: {fileItem.FileName}, 路径: {fileItem.FilePath}", ConsoleColor.Magenta, fileItem);
            
            // 检查层数限制（0表示不限制）
            if (_settings.MaxExtractDepth > 0 && fileItem.CurrentDepth > _settings.MaxExtractDepth)
            {
                Dispatcher.Invoke(() => fileItem.Status = $"跳过: 超过最大层数({_settings.MaxExtractDepth})");
                AppendLog($"[{fileItem.FileName}] 当前层数 {fileItem.CurrentDepth} 超过最大层数 {_settings.MaxExtractDepth}，跳过处理", ConsoleColor.Yellow, fileItem);
                
                // 检查父项完成（传入子节点）
                if (fileItem.Parent != null)
                {
                    UpdateParentStatusWhenChildrenComplete(fileItem);
                }
                return;
            }
            
            Dispatcher.Invoke(() => fileItem.Status = "正在测试密码...");

            // 步骤1：判断是否是压缩文件
            // 有父节点的文件是解压过程中发现的（非用户直接拖入），isDroppedFile 应为 false
            bool isDroppedFile = fileItem.Parent == null;
            bool isArchive = _detector.IsArchiveFile(fileItem.FilePath, isDroppedFile: isDroppedFile);
            AppendLog($"[DEBUG-测试] {fileItem.FileName} IsArchiveFile 结果: {isArchive}", ConsoleColor.Magenta, fileItem);
            
            if (!isArchive)
            {
                Dispatcher.Invoke(() => fileItem.Status = "非压缩文件，跳过");
                AppendLog($"[DEBUG-测试] {fileItem.FileName} 被判定为非压缩文件", ConsoleColor.Yellow, fileItem);
                
                // 检查父项完成（传入子节点）
                if (fileItem.Parent != null)
                {
                    UpdateParentStatusWhenChildrenComplete(fileItem);
                }
                return;
            }

            string? foundPassword = null;
            bool stegoMode = _stego.IsStegoDetectionActiveForFile(fileItem.FilePath);

            if (stegoMode)
            {
                _passwordMap.Add(fileItem.FilePath, null);
                Dispatcher.Invoke(() =>
                {
                    fileItem.FoundPassword = null;
                    TxtStatusCurrentPassword.Text = "测试中的密码: 无";
                    fileItem.Status = "隐写容器（无需密码）";
                });

                _extractQueue.Enqueue(fileItem);
                AppendLog($"[{fileItem.FileName}] 识别为隐写容器，已加入待觧压队列", ConsoleColor.Cyan, fileItem);
                WakeupExtractThread();
                await Task.Delay(100, token);
                return;
            }

            // 步骤2：尝试无密码
            Dispatcher.Invoke(() =>
            {
                TxtStatusCurrentPassword.Text = "测试中的密码: (无密码)";
                fileItem.Status = "测试: 无密码";
            });

            AppendLog($"[DEBUG-测试] {fileItem.FileName} 开始无密码测试", ConsoleColor.Cyan, fileItem);
            
            bool noPasswordSuccess = await extractor.TestPasswordAsync(
                fileItem.FilePath, 
                "", 
                onOutput: (msg) => { },
                cancellationToken: token);

            AppendLog($"[DEBUG-测试] {fileItem.FileName} 无密码测试结果: {noPasswordSuccess}", ConsoleColor.Cyan, fileItem);

            if (noPasswordSuccess)
            {
                foundPassword = null;
            }
            else
            {
                // 步骤3：测试密码本
                AppendLog($"[DEBUG-测试] {fileItem.FileName} 无密码失败，开始测试密码本 (共{passwords.Count}个密码)", ConsoleColor.Yellow, fileItem);
                
                int testedCount = 0;
                foreach (var password in passwords)
                {
                    if (token.IsCancellationRequested)
                        break;

                    testedCount++;
                    Dispatcher.Invoke(() =>
                    {
                        TxtStatusCurrentPassword.Text = $"测试中的密码: {password}";
                        fileItem.Status = $"测试: {password} ({testedCount}/{passwords.Count})";
                    });

                    bool isValid = await extractor.TestPasswordAsync(
                        fileItem.FilePath, 
                        password,
                        onOutput: (msg) => { },
                        cancellationToken: token);

                    if (isValid)
                    {
                        foundPassword = password;
                        AppendLog($"[DEBUG-测试] {fileItem.FileName} 找到密码: {password}", ConsoleColor.Green, fileItem);
                        _settings.RecordPasswordUsage(password);
                        break;
                    }

                    await Task.Delay(50, token);
                }
                
                if (foundPassword == null)
                {
                    AppendLog($"[DEBUG-测试] {fileItem.FileName} 密码本测试完成，未找到密码", ConsoleColor.Red, fileItem);
                }
            }

            // 记录到密码映射表
            _passwordMap.Add(fileItem.FilePath, foundPassword);

            // 更新UI
            Dispatcher.Invoke(() =>
            {
                fileItem.FoundPassword = foundPassword;
                TxtStatusCurrentPassword.Text = "测试中的密码: 无";
                
                // 注意：foundPassword 为 null 表示无密码，空字符串也是有效密码
                // 只有 noPasswordSuccess = false 且密码本测试也失败才算“未找到密码”
                if (noPasswordSuccess)
                {
                    // 无密码测试成功
                    fileItem.Status = "解压成功(无密码)";
                    
                    // 加入待解压队列
                    _extractQueue.Enqueue(fileItem);
                    AppendLog($"[{fileItem.FileName}] 无密码测试成功，已加入待觧压队列", ConsoleColor.Green, fileItem);
                    WakeupExtractThread();
                }
                else if (foundPassword != null)
                {
                    // 找到密码
                    fileItem.Status = $"密码正确: {foundPassword}";
                    
                    // 找到密码，加入待解压队列
                    _extractQueue.Enqueue(fileItem);
                    AppendLog($"[{fileItem.FileName}] 已加入待觧压队列", ConsoleColor.Gray, fileItem);
                    WakeupExtractThread();
                }
                else
                {
                    // 未找到密码，设置失败状态，不加入解压队列
                    fileItem.Status = "fail-未找到密码";
                    AppendLog($"[{fileItem.FileName}] 未找到密码，跳过觧压", ConsoleColor.Red, fileItem);
                    
                    // 检查父项完成（传入子节点）
                    if (fileItem.Parent != null)
                    {
                        UpdateParentStatusWhenChildrenComplete(fileItem);
                    }
                    else
                    {
                        // 顶级文件没有找到密码，检查是否有子项需要等待
                        CheckAndMarkParentComplete(fileItem);
                    }
                }
            });

            await Task.Delay(100, token);
        }

        /// <summary>
        /// 启动解压线程：从待解压队列取文件，解压后扫描新文件加入待处理队列
        /// </summary>
        private async void StartExtractThread()
        {
            // 【并发修复】check-then-act 原子化：测试线程唤醒与监控线程重启可能并发调用，
            // 无锁时两个线程可同时看到 _isExtracting==false 而启动重复解压线程组
            lock (_threadStateLock)
            {
                if (_isExtracting)
                    return;

                _isExtracting = true;
            }
            var tokenSource = new CancellationTokenSource();
            _extractCancellationTokenSource = tokenSource;
            var token = tokenSource.Token;

            try
            {
                UpdateUiState();
                AppendLog($"\n========== 解压线程启动 ==========", ConsoleColor.Cyan);
                AppendLog($"并发线程数: {_settings.ThreadCount}", ConsoleColor.Cyan);

                var extractor = new SevenZipExtractor(_settings.SevenZipPath);
                var tasks = new List<Task>();

                // 创建多个并发任务
                for (int i = 0; i < _settings.ThreadCount; i++)
                {
                    int taskId = i + 1;
                    var task = Task.Run(async () =>
                    {
                        AppendLog($"[解压线程 {taskId}] 启动", ConsoleColor.Gray);

                        while (!token.IsCancellationRequested)
                        {
                            // 检查是否被暂停（优雅暂停：完成当前任务后停止）
                            if (_isPaused)
                            {
                                AppendLog($"[解压线程 {taskId}] 检测到暂停标志，等待继续...", ConsoleColor.Yellow);
                                
                                // 等待直到取消暂停或被取消
                                while (_isPaused && !token.IsCancellationRequested)
                                {
                                    await Task.Delay(100, token);
                                }
                                
                                if (token.IsCancellationRequested)
                                    break;
                                    
                                AppendLog($"[解压线程 {taskId}] 继续处理", ConsoleColor.Cyan);
                            }
                            
                            if (_extractQueue.TryDequeue(out var fileItem))
                            {
                                // 尝试获取文件锁，防止竞争
                                if (!fileItem.TryLock())
                                {
                                    // 已被其他线程处理，重新入队
                                    _extractQueue.Enqueue(fileItem);
                                    await Task.Delay(100, token);
                                    continue;
                                }

                                try
                                {
                                    await ProcessExtractFile(fileItem, extractor, taskId, token);
                                }
                                finally
                                {
                                    fileItem.ReleaseLock();
                                }
                            }
                            else
                            {
                                // 队列为空，等待信号或超时
                                AppendLog($"[解压线程 {taskId}] 队列为空，等待新任务...", ConsoleColor.Gray);
                                
                                // 等待信号（最多等待5秒）或取消请求
                                bool signaled = false;
                                try
                                {
                                    signaled = await Task.Run(() => 
                                        _extractQueueSignal.Wait(TimeSpan.FromSeconds(5), token), token);
                                }
                                catch (OperationCanceledException)
                                {
                                    break;
                                }
                                
                                if (!signaled)
                                {
                                    // 超时或取消，再次检查是否真的为空
                                    if (_extractQueue.IsEmpty)
                                    {
                                        AppendLog($"[解压线程 {taskId}] 超时且队列为空，解压线程完成", ConsoleColor.Gray);
                                        break;
                                    }
                                }
                                
                                // 重置信号，准备下一次等待
                                _extractQueueSignal.Reset();
                            }
                        }

                        AppendLog($"[解压线程 {taskId}] 完成", ConsoleColor.Gray);
                    }, token);

                    tasks.Add(task);
                }

                // 等待所有线程完成
                await Task.WhenAll(tasks);

                AppendLog($"\n========== 解压线程完成 ==========", ConsoleColor.Green);
            }
            catch (OperationCanceledException)
            {
                AppendLog("\n解压线程已取消", ConsoleColor.Yellow);
            }
            catch (Exception ex)
            {
                DiagLog($"[DIAG] 解压线程宿主异常: {ex.GetType().Name}: {ex.Message}");
                AppendLog($"\n解压线程出错: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                // 只有当前tokenSource仍是自己创建的时才清理（暂停时会置null，新线程会创建新的）
                if (_extractCancellationTokenSource == tokenSource)
                {
                    _isExtracting = false;
                    _isPaused = false; // 重置暂停标志

                    // 解压线程全部退出后唤醒测试线程，避免其空等到超时才退出
                    _pendingQueueSignal.Set();

                    _extractCancellationTokenSource?.Dispose();
                    _extractCancellationTokenSource = null;
                    UpdateUiState();
                }

                _extractCompletionSource.TrySetResult(true);
            }
        }

        /// <summary>
        /// 监控处理流程完成（支持线程重启）
        /// </summary>
        private async void MonitorProcessingComplete()
        {
            if (_isMonitoring)
                return;
            _isMonitoring = true;
            try
            {
                int emptyCheckCount = 0;
                const int maxEmptyChecks = 3; // 连续检测到空状态的次数阈值
                
                while (true)
                {
                    // 检查全局取消令牌
                    if (_globalCancellationTokenSource?.Token.IsCancellationRequested == true)
                    {
                        AppendLog("[监控] 检测到全局取消请求，退出监控", ConsoleColor.Gray);
                        break;
                    }
                    
                    // 获取当前的完成信号 Task
                    var testTask = _testCompletionSource.Task;
                    var extractTask = _extractCompletionSource.Task;
                    
                    // 等待完成（带超时）
                    // 【BUG修复】WhenAny 返回的是被传入的原始任务之一；此前比较 testTask/extractTask
                    // 永远不相等（即使线程已退出），导致任务完成被误判为超时，只能靠 15 秒空轮询退出
                    var allTasksTask = Task.WhenAll(testTask, extractTask);
                    var completedTask = await Task.WhenAny(
                        allTasksTask,
                        Task.Delay(5000) // 5秒超时
                    );
                    
                    // 如果是因为超时而非任务完成
                    if (completedTask != allTasksTask)
                    {
                        DiagLog($"[DIAG] 监控超时轮询: _isTesting={_isTesting}, _isExtracting={_isExtracting}, 待处理={_pendingQueue.Count}, 待解压={_extractQueue.Count}");
                        // 检查是否应该退出
                        if (!_isTesting && !_isExtracting && _extractQueue.IsEmpty && _pendingQueue.IsEmpty)
                        {
                            emptyCheckCount++;
                            if (emptyCheckCount >= maxEmptyChecks)
                            {
                                AppendLog("[监控] 连续检测到空状态，退出监控", ConsoleColor.Gray);
                                break;
                            }
                        }
                        else
                        {
                            emptyCheckCount = 0;
                            
                            // 主动检查：如果线程已退出但队列不为空，重新启动线程
                            if (!_isTesting && !_pendingQueue.IsEmpty)
                            {
                                AppendLog($"[监控] 检测到测试线程未运行，但待处理队列有{_pendingQueue.Count}个任务，重新启动...", ConsoleColor.Yellow);
                                _testCompletionSource = new TaskCompletionSource<bool>();
                                StartTestThread();
                                _pendingQueueSignal.Set();
                            }
                            
                            if (!_isExtracting && !_extractQueue.IsEmpty)
                            {
                                AppendLog($"[监控] 检测到解压线程未运行，但待解压队列有{_extractQueue.Count}个任务，重新启动...", ConsoleColor.Yellow);
                                _extractCompletionSource = new TaskCompletionSource<bool>();
                                StartExtractThread();
                                _extractQueueSignal.Set();
                            }
                        }
                        continue;
                    }
                    
                    // 任务完成，重置计数器
                    emptyCheckCount = 0;
                    
                    // 等待完成后，检查是否还有线程在运行
                    if (!_isTesting && !_isExtracting)
                    {
                        // 两个线程都不在运行，但需要再等待一小段时间
                        // 确保不会有新的任务被加入队列（比如测试线程刚完成并唤醒解压线程）
                        await Task.Delay(1000);
                        
                        // 再次检查线程状态和队列状态
                        if (!_isTesting && !_isExtracting && _extractQueue.IsEmpty && _pendingQueue.IsEmpty)
                        {
                            // 所有线程都完成了，且队列都为空，真正退出循环
                            AppendLog("[监控] 所有任务完成且队列为空，退出监控", ConsoleColor.Gray);
                            break;
                        }
                        else if (!_isTesting && !_pendingQueue.IsEmpty)
                        {
                            // 测试线程已退出，但待处理队列中还有任务，重新启动测试线程
                            AppendLog($"[监控] 检测到测试线程已退出，但待处理队列仍有{_pendingQueue.Count}个任务，重新启动测试线程...", ConsoleColor.Yellow);
                            _testCompletionSource = new TaskCompletionSource<bool>();
                            StartTestThread();
                            _pendingQueueSignal.Set();
                        }
                        else if (!_isExtracting && !_extractQueue.IsEmpty)
                        {
                            // 解压线程已退出，但待解压队列中还有任务，重新启动解压线程
                            AppendLog($"[监控] 检测到解压线程已退出，但待解压队列仍有{_extractQueue.Count}个任务，重新启动解压线程...", ConsoleColor.Yellow);
                            _extractCompletionSource = new TaskCompletionSource<bool>();
                            StartExtractThread();
                            _extractQueueSignal.Set();
                        }
                    }
                    
                    // 还有线程在运行或队列不为空，继续循环等待
                    if (_isTesting || _isExtracting)
                    {
                        AppendLog("[监控] 检测到线程仍在运行，继续等待...", ConsoleColor.Gray);
                    }
                    else if (!_extractQueue.IsEmpty || !_pendingQueue.IsEmpty)
                    {
                        AppendLog($"[监控] 检测到队列仍有任务（待处理:{_pendingQueue.Count}, 待解压:{_extractQueue.Count}），继续等待...", ConsoleColor.Gray);
                    }
                }
                
                // 所有处理流程完成
                if (_globalCancellationTokenSource?.Token.IsCancellationRequested != true)
                {
                    DiagLog("[DIAG] 监控循环退出，即将打印完成日志");
                    AppendLog($"\n========== 所有处理流程完成 ==========", ConsoleColor.Green);
                    DiagLog("[DIAG] 完成日志已打印");
                    
                    // 更新状态栏为“未开始”
                    Dispatcher.Invoke(() =>
                    {
                        TxtStatusMain.Text = "状态: 未开始";
                        TxtStatusMain.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
                    });
                    
                    // 对拖入的文件夹进行智能路径处理（即使没有压缩包）
                    if (_settings.EnableSmartPathProcessing && _settings.OutputMode == OutputMode.ArchiveFolder && _droppedFolders.Count > 0)
                    {
                        AppendLog($"[智能路径] 开始处理拖入的文件夹（{_droppedFolders.Count} 个）...", ConsoleColor.Cyan);
                        
                        // 使用 Task.Run 在后台处理，不阻塞主流程
                        var folders = _droppedFolders.ToList();
                        _droppedFolders.Clear();
                        
                        _ = Task.Run(async () =>
                        {
                            // 【并发修复】持锁串行执行，避免与解压收尾流程并发移动同一目录树
                            await _smartPathFinalizeLock.WaitAsync();
                            try
                            {
                                foreach (var folder in folders)
                                {
                                    if (Directory.Exists(folder))
                                    {
                                        try
                                        {
                                            AppendLog($"[智能路径] 处理拖入的文件夹: {folder}", ConsoleColor.Cyan);
                                            await _smartPath.ProcessSmartPathBatchAsync(folder);
                                            
                                            // 更新最终路径显示
                                            string? finalPath = _smartPath.FindFinalFlattenedPath(folder);
                                            Dispatcher.Invoke(() =>
                                            {
                                                TxtFinalPath.Text = finalPath ?? folder;
                                                AppendLog($"[智能路径] 文件夹最终路径: {TxtFinalPath.Text}", ConsoleColor.Green);
                                            });
                                        }
                                        catch (Exception ex)
                                        {
                                            AppendLog($"[智能路径] 处理文件夹失败 {folder}: {ex.Message}", ConsoleColor.Red);
                                        }
                                    }
                                }
                                
                                // 智能路径处理完成后，更新状态
                                Dispatcher.Invoke(() =>
                                {
                                    AppendLog($"[智能路径] 所有拖入文件夹处理完成", ConsoleColor.Green);
                                });
                            }
                            finally
                            {
                                _smartPathFinalizeLock.Release();
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog($"[DIAG] 监控流程异常: {ex.GetType().Name}: {ex.Message}");
                AppendLog($"监控流程出错: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                _isMonitoring = false;
            }
        }

        /// <summary>
        /// 递归收集树形结构中的所有 FileItem（包括子项）
        /// </summary>
        private List<FileItem> CollectAllFileItems(ObservableCollection<FileItem> items)
        {
            var allItems = new List<FileItem>();
            
            foreach (var item in items)
            {
                allItems.Add(item);
                
                // 递归收集子项
                if (item.Children.Count > 0)
                {
                    allItems.AddRange(CollectAllFileItems(item.Children));
                }
            }
            
            return allItems;
        }

        /// <summary>
        /// 【并发修复】在 UI 线程上获取全树快照（递归收集所有节点，含子节点）。
        /// ObservableCollection 非线程安全：后台线程（收尾/清理）直接遍历会与 UI 线程的增删并发冲突。
        /// </summary>
        private List<FileItem> SnapshotAllItems()
        {
            if (Dispatcher.CheckAccess())
                return CollectAllFileItems(_fileList);

            List<FileItem> snapshot = new();
            Dispatcher.Invoke(() => snapshot = CollectAllFileItems(_fileList));
            return snapshot;
        }

        /// <summary>
        /// 【并发修复】在 UI 线程上获取顶级列表快照（不含子节点）。
        /// 用于后台线程需要按顶级项过滤的场景。
        /// </summary>
        private List<FileItem> SnapshotTopLevelItems()
        {
            if (Dispatcher.CheckAccess())
                return _fileList.ToList();

            List<FileItem> snapshot = new();
            Dispatcher.Invoke(() => snapshot = _fileList.ToList());
            return snapshot;
        }

        /// <summary>
        /// 【并发修复】在 UI 线程上执行树查找（FindFileItemInTree 递归遍历 ObservableCollection Children，
        /// 必须在 UI 线程上调用才能避免与 UI 线程的增删并发冲突）。
        /// </summary>
        private FileItem? FindFileItemInTreeThreadSafe(string filePath)
        {
            if (Dispatcher.CheckAccess())
                return FindFileItemInTree(filePath);

            FileItem? result = null;
            Dispatcher.Invoke(() => result = FindFileItemInTree(filePath));
            return result;
        }

        /// <summary>
        /// 检查并标记项为完成（如果所有子项都已完成）
        /// 对于顶级文件，如果解压完成且所有子项也完成，则从列表移除
        /// 如果解压失败（包括未找到密码），则保留在列表中显示失败状态
        /// </summary>
        private void CheckAndMarkParentComplete(FileItem fileItem)
        {
            // 如果有子项，等待所有子项完成
            if (fileItem.Children.Count > 0)
            {
                // 不立即更新状态，而是等待子项处理完成
                AppendLog($"[{fileItem.FileName}] 有 {fileItem.Children.Count} 个子压缩包，等待子项处理完成", ConsoleColor.Cyan);
            }
            else
            {
                // 没有子项，如果是顶级文件
                if (fileItem.Parent == null)
                {
                    // 只有解压成功时才处理原文件并从列表移除
                    bool extractSuccess = fileItem.GetSelfExtractResult();
                    bool isPasswordNotFound = fileItem.Status.Contains("fail-未找到密码");
                    
                    if (extractSuccess && !isPasswordNotFound)
                    {
                        // 解压成功，处理原文件（移动到回收站/删除等）
                        // 由于这个方法被多处调用，且都是同步调用，我们使用 fire-and-forget 模式
                        _ = _cleanup.HandleOriginalFileAsync(fileItem.FilePath, fileItem);
                        
                        // 从列表移除
                        Dispatcher.Invoke(() =>
                        {
                            if (_fileList.Contains(fileItem))
                            {
                                _fileList.Remove(fileItem);
                                AppendLog($"[{fileItem.FileName}] 无子压缩包，已从待处理列表移除", ConsoleColor.Gray);
                            }
                        });
                    }
                    else if (isPasswordNotFound)
                    {
                        // 未找到密码，保留在列表中显示失败状态
                        AppendLog($"[{fileItem.FileName}] 未找到密码，保留在列表中", ConsoleColor.Yellow);
                    }
                    else
                    {
                        // 解压失败，保留原文件和列表记录
                        AppendLog($"[{fileItem.FileName}] 解压失败，保留原文件和列表记录", ConsoleColor.Yellow);
                    }
                }
            }
        }

        /// <summary>
        /// 阶段1：清理树中所有子压缩包（非顶级节点的原文件）
        /// </summary>
        private async Task CleanupChildArchivesInTree()
        {
            // 【关键修复】先等待所有子压缩包解压完成，再执行清理
            // 避免竞态条件：子压缩包还在解压时就被判断为"解压失败"
            AppendLog($"  等待所有子压缩包解压完成...", ConsoleColor.Gray);
            await WaitForAllChildrenExtractComplete();
            AppendLog($"  所有子压缩包已解压完成", ConsoleColor.Gray);
            
            var allItems = SnapshotAllItems();
            foreach (var item in allItems)
            {
                if (item.Parent == null)
                    continue; // 跳过顶级节点，顶级由阶段3单独处理
                
                // 【关键修复】只清理解压成功的子压缩包，失败的必须保留
                bool extractSuccess = item.GetSelfExtractResult();
                if (!extractSuccess)
                {
                    AppendLog($"  跳过清理 [{item.FileName}]：解压失败，保留原文件", ConsoleColor.Yellow);
                    continue;
                }
                
                // 【修复】跳过隐写载体文件（如 .mp4, .mkv 等媒体文件）
                // 这些文件不是传统的压缩包，即使被识别为隐写容器也不应删除
                if (_stego.IsStegoCarrierExtension(item.FilePath))
                {
                    AppendLog($"  跳过清理 [{item.FileName}]：隐写载体文件（非传统压缩包），保留原文件", ConsoleColor.Cyan);
                    continue;
                }
                
                try
                {
                    // 子压缩包不受解压后原文件处理设置影响，一律直接删除
                    if (item.VolumeInfo?.HasVolumeList == true)
                    {
                        // 分卷压缩包：删除所有分卷
                        await _cleanup.DeleteAllVolumes(item.VolumeInfo);
                    }
                    else
                    {
                        await _cleanup.DeleteFilePermanentlyAsync(item.FilePath);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"  清理子压缩包失败 [{item.FileName}]: {ex.Message}", ConsoleColor.Red);
                }
            }
        }
        
        /// <summary>
        /// 等待所有子压缩包解压完成
        /// 通过轮询检查所有有父节点的FileItem，确保它们的解压状态已设置
        /// </summary>
        private async Task WaitForAllChildrenExtractComplete()
        {
            const int maxWaitSeconds = 30; // 最多等待30秒
            const int checkInterval = 500; // 每500ms检查一次
            
            int waitedMs = 0;
            while (waitedMs < maxWaitSeconds * 1000)
            {
                var allItems = SnapshotAllItems();
                bool allChildrenDone = true;
                
                foreach (var item in allItems)
                {
                    // 跳过顶级节点
                    if (item.Parent == null)
                        continue;
                    
                    // 检查子节点是否有解压状态（SetSelfExtractResult已被调用）
                    // 如果解压还在进行中，_selfExtractSuccess会是false（默认值）
                    // 我们需要区分"还在解压中"和"解压失败"
                    // 通过检查Status来判断：如果Status包含"正在解压"说明还在进行中
                    if (item.Status.Contains("正在解压"))
                    {
                        allChildrenDone = false;
                        break;
                    }
                }
                
                if (allChildrenDone)
                {
                    return; // 所有子节点都已完成（成功或失败）
                }
                
                await Task.Delay(checkInterval);
                waitedMs += checkInterval;
            }
            
            AppendLog($"  警告：等待子压缩包完成超时({maxWaitSeconds}秒)，继续执行清理", ConsoleColor.Yellow);
        }

        /// <summary>
        /// 顶级解压链完成时，分三个阶段顺序执行：
        /// 阶段1：清理所有子压缩包（确保目录内干净）
        /// 阶段2：清理顶级原压缩包（先删归档，让父目录变干净，方便后续扁平化）
        /// 阶段3：批量智能路径处理（扁平化）+ 遗留空目录 + 更新状态
        /// </summary>
        private async Task FinalizeTopLevelExtractAndSmartPathAsync(string topArchivePath)
        {
            DiagLog($"[DIAG] Finalize 入口: {topArchivePath}");
            // 【并发修复】同一顶级文件的收尾只执行一次：
            // 完成回调可能从多条路径触发（顶级叶子收尾 / 子节点完成父链上报），
            // 重复执行会在已扁平化的目录上再次移动/合并，产生 GUID 新目录和图片分散。
            lock (_finalizingTopArchives)
            {
                if (!_finalizingTopArchives.Add(topArchivePath))
                {
                    AppendLog($"[收尾] {Path.GetFileName(topArchivePath)} 已在收尾流程中，跳过重复触发", ConsoleColor.Yellow);
                    return;
                }
            }

            // 【并发修复】收尾流程全程持锁串行执行：多个顶级文件并发完成时，
            // 阶段1-4 涉及共享集合（_extractedDirectoryNodes/_processedFlattenDirs）与目录移动，
            // 并发执行会互相干扰：移动中断（图片没移动完成）、目录冲突生成 GUID 新目录。
            await _smartPathFinalizeLock.WaitAsync();
            DiagLog("[DIAG] Finalize 已获取收尾锁");
            try
            {
                await FinalizeTopLevelExtractAndSmartPathCoreAsync(topArchivePath);
            }
            finally
            {
                _smartPathFinalizeLock.Release();
                lock (_finalizingTopArchives)
                {
                    _finalizingTopArchives.Remove(topArchivePath);
                }
            }
        }

        /// <summary>
        /// 顶级解压链收尾的核心实现（调用方已持有 _smartPathFinalizeLock 串行化锁，保证不会并发执行）
        /// </summary>
        private async Task FinalizeTopLevelExtractAndSmartPathCoreAsync(string topArchivePath)
        {
            DiagLog($"[DIAG] FinalizeCore 入口: {topArchivePath}");
            AppendLog($"[阶段1] 开始清理子压缩包...", ConsoleColor.Cyan);
            try
            {
                await CleanupChildArchivesInTree();
                AppendLog($"[阶段1] 子压缩包清理完成", ConsoleColor.Cyan);
            }
            catch (Exception ex)
            {
                DiagLog($"[DIAG] 阶段1异常: {ex.GetType().Name}: {ex.Message}");
                AppendLog($"[阶段1] 清理子压缩包失败: {ex.Message}", ConsoleColor.Red);
            }
        
            AppendLog($"[阶段2] 开始清理顶级原压缩包: {Path.GetFileName(topArchivePath)}", ConsoleColor.Cyan);
            // 阶段2：清理顶级原压缩包（含分卷）
            // 提前清理让目录变干净，扁平化时不会被归档文件阻塞
            try
            {
                if (_settings.FileAfterExtract != FileAction.Keep)
                {
                    AppendLog($"[阶段2] 等待文件解锁并清理...", ConsoleColor.Cyan);
                    // 文件项（含分卷信息）必须在 UI 线程查找后传入清理服务
                    FileItem? topFileItem = null;
                    Dispatcher.Invoke(() =>
                    {
                        topFileItem = _fileList.FirstOrDefault(f => f.FilePath == topArchivePath);
                        if (topFileItem == null)
                        {
                            topFileItem = FindFileItemInTree(topArchivePath);
                        }
                    });
                    await _cleanup.HandleOriginalFile(topArchivePath, topFileItem);
                    AppendLog($"[阶段2] 顶级原压缩包清理完成", ConsoleColor.Cyan);
                }
                else
                {
                    AppendLog($"[阶段2] 配置为保留原文件，跳过清理", ConsoleColor.Cyan);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[阶段2] 处理原文件时出错: {ex.Message}", ConsoleColor.Red);
            }
        
            AppendLog($"[阶段3] 开始批量智能路径处理...", ConsoleColor.Cyan);
            // 阶段3：批量智能路径处理（扁平化）
            try
            {
                DiagLog("[DIAG] 阶段3 ProcessBatchSmartPathSynchronous 开始");
                await _smartPath.ProcessBatchSmartPathSynchronous(topArchivePath);
                DiagLog("[DIAG] 阶段3 ProcessBatchSmartPathSynchronous 完成");
                AppendLog($"[阶段3] 批量智能路径处理完成", ConsoleColor.Cyan);
            }
            catch (Exception ex)
            {
                AppendLog($"[阶段3] 批量智能路径失败: {ex.Message}", ConsoleColor.Red);
            }
        
            // 阶段4: 检查归档父目录是否需要扁平化（合并唯一子目录）
            // 拖入的文件夹不在 _extractedDirectoryNodes 中，阶段3不会处理它。
            // 但归档清理后（阶段2），父目录可能变得只需1个子目录，此时应将子目录内容合并入父目录。
            // 【修复】不再调用 ProcessSmartPathBatchAsync（它会向上扁平化到祖目录），
            // 改为就地合并：将唯一子目录的内容提升到 archiveParentDir 内，不越过 root 节点。
            // 优化：仅当父目录恰好有1个子目录且无非压缩包文件时才处理，避免无意义的遍历。
            string? archiveParentDir = Path.GetDirectoryName(topArchivePath);
            if (!string.IsNullOrEmpty(archiveParentDir) && Directory.Exists(archiveParentDir))
            {
                var parentSubDirs = Directory.GetDirectories(archiveParentDir);
                var parentFiles = Directory.GetFiles(archiveParentDir);
                var parentNonArchiveFiles = parentFiles.Where(f => !_detector.IsArchiveFile(f)).ToArray();
                
                if (parentSubDirs.Length == 1 && parentNonArchiveFiles.Length == 0)
                {
                    string soleSubDir = parentSubDirs[0];
                    
                    // 如果唯一的子目录是阶段3智能路径处理的产物，说明阶段3已经做了正确的扁平化，
                    // 不应再合并，否则会丢失有意义的名字（如将 [しゅにち関数...] 合并入父目录 137）
                    if (_extractedDirectoryNodes.ContainsKey(soleSubDir))
                    {
                        AppendLog($"[阶段4] 归档父目录仅有1个子目录 '{Path.GetFileName(soleSubDir)}'，但它是阶段3智能路径处理的产物，跳过合并保留其名称", ConsoleColor.Gray);
                    }
                    else
                    {
                        AppendLog($"[阶段4] 归档父目录仅有1个子目录 '{Path.GetFileName(soleSubDir)}' 且无其他非压缩包文件，将子目录内容合并入父目录: {archiveParentDir}", ConsoleColor.Cyan);
                        try
                        {
                        await Task.Run(() =>
                        {
                            // 将子目录的所有内容移动到父目录
                            foreach (var entry in Directory.EnumerateFileSystemEntries(soleSubDir))
                            {
                                string entryName = Path.GetFileName(entry);
                                string destPath = Path.Combine(archiveParentDir, entryName);
                                
                                if (Directory.Exists(entry))
                                {
                                    // 如果目标已存在且是解压节点，先删除再移动
                                    if (Directory.Exists(destPath) && _extractedDirectoryNodes.ContainsKey(destPath))
                                    {
                                        Directory.Delete(destPath, true);
                                        AppendLog($"[阶段4]   已删除已存在的解压节点目录: {entryName}", ConsoleColor.Gray);
                                    }
                                    Directory.Move(entry, destPath);
                                    AppendLog($"[阶段4]   已移动目录: {entryName} -> {archiveParentDir}", ConsoleColor.Gray);
                                    
                                    // 同步更新 _extractedDirectoryNodes
                                    if (_extractedDirectoryNodes.ContainsKey(entry))
                                    {
                                        _extractedDirectoryNodes.TryRemove(entry, out _);
                                        _extractedDirectoryNodes.TryAdd(destPath, 0);
                                    }
                                }
                                else
                                {
                                    // 如果目标文件已存在，覆盖
                                    if (File.Exists(destPath))
                                    {
                                        File.Delete(destPath);
                                    }
                                    File.Move(entry, destPath);
                                    AppendLog($"[阶段4]   已移动文件: {entryName}", ConsoleColor.Gray);
                                }
                            }
                            
                            // 删除已清空的子目录
                            if (Directory.Exists(soleSubDir))
                            {
                                Directory.Delete(soleSubDir);
                                AppendLog($"[阶段4]   已删除空子目录: {Path.GetFileName(soleSubDir)}", ConsoleColor.Gray);
                            }
                        });
                        AppendLog($"[阶段4] 归档父目录合并完成: {archiveParentDir}", ConsoleColor.Cyan);
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[阶段4] 归档父目录合并失败: {ex.Message}", ConsoleColor.Red);
                    }
                    }
                }
                else if (parentSubDirs.Length == 0)
                {
                    AppendLog($"[阶段4] 归档父目录无子目录，跳过: {archiveParentDir}", ConsoleColor.Gray);
                }
                else
                {
                    AppendLog($"[阶段4] 归档父目录有 {parentSubDirs.Length} 个子目录或 {parentNonArchiveFiles.Length} 个非压缩包文件，无需合并，跳过: {archiveParentDir}", ConsoleColor.Gray);
                }
            }
        
            try
            {
                string outputDir = _detector.GetOutputDirectory(topArchivePath);
                if (Directory.Exists(outputDir))
                {
                    if (!Directory.EnumerateFileSystemEntries(outputDir).Any())
                    {
                        Directory.Delete(outputDir);
                        AppendLog($"  已清理空输出目录: {Path.GetFileName(outputDir)}", ConsoleColor.Gray);
                    }
                    else
                    {
                        AppendLog($"[批量智能路径] [DEBUG] 输出目录非空，保留: {outputDir}", ConsoleColor.Gray);
                        foreach (var entry in Directory.EnumerateFileSystemEntries(outputDir))
                        {
                            string entryName = Path.GetFileName(entry);
                            bool isDir = Directory.Exists(entry);
                            AppendLog($"[批量智能路径]   残留: {(isDir ? "[DIR]" : "[FILE]")} {entryName}", ConsoleColor.Gray);
                        }
                    }
                }
            }
            catch
            {
                // 目录可能不为空或有权限问题，忽略
            }
                    
            // 【关键修复】所有阶段完成后，更新状态为"解压完成-扁平化处理完成"
            UpdateTopLevelItemCompletedStatus(topArchivePath);
                    
            AppendLog($"[收尾] 顶级解压链处理完成: {Path.GetFileName(topArchivePath)}", ConsoleColor.Green);
        }

        /// <summary>
        /// 检查是否还有待处理的文件，如果有则触发新一轮解压
        /// </summary>


        /// <summary>
        /// 处理单个文件的解压
        /// </summary>
        private async Task ProcessExtractFile(FileItem fileItem, SevenZipExtractor extractor, int taskId, CancellationToken token)
        {
            // 从密码映射表获取密码
            if (!_passwordMap.TryGetValue(fileItem.FilePath, out var password))
            {
                Dispatcher.Invoke(() => fileItem.Status = "跳过: 未找到密码");
                
                // 如果没有密码，检查父项是否可以完成（传入子节点）
                if (fileItem.Parent != null)
                {
                    UpdateParentStatusWhenChildrenComplete(fileItem);
                }
                else
                {
                    // 顶级文件没有密码，检查是否有子项需要等待
                    CheckAndMarkParentComplete(fileItem);
                }
                return;
            }

            Dispatcher.Invoke(() => fileItem.Status = "正在解压...");
            
            string outputDir = _detector.GetOutputDirectory(fileItem.FilePath);
            
            // 在解压前记录输出目录中已存在的文件列表（用于过滤隐写文件解压时误添加已存在的压缩包）
            HashSet<string> existingFilesBeforeExtract = new HashSet<string>();
            if (Directory.Exists(outputDir))
            {
                try
                {
                    var allFiles = Directory.GetFiles(outputDir, "*.*", SearchOption.AllDirectories);
                    foreach (var f in allFiles)
                    {
                        existingFilesBeforeExtract.Add(f);
                    }
                }
                catch
                {
                    // 忽略访问权限等问题
                }
            }
            
            // 记录觧压目录用于调试
            bool isStego = _stego.IsStegoDetectionActiveForFile(fileItem.FilePath);
            string? detectedType = isStego ? _stego.DetectStegoArchiveType(fileItem.FilePath) : null;
            // 类型无法确认时不能再用 7z x -t#：它会把载体自身的字节和误匹配碎片写成 "1"、"2" 这类序号文件，
            // 改为按 -t# 扫描出的 Offset/Size 裁剪出真实压缩包再解压
            bool stegoCarveMode = isStego && (string.IsNullOrEmpty(detectedType) || detectedType == "#");
            AppendLog($"\n[线程 {taskId}] 觧压: {fileItem.FileName}", ConsoleColor.White, fileItem);
            AppendLog($"  输出目录: {outputDir}", ConsoleColor.Gray, fileItem);
            AppendLog($"  隐写模式: {isStego}", ConsoleColor.Gray, fileItem);
            if (isStego)
            {
                AppendLog(stegoCarveMode
                    ? "  压缩类型: 未确定（按字节区间裁剪）"
                    : $"  压缩类型: -t{detectedType}", ConsoleColor.Gray, fileItem);
            }
            AppendLog($"  密码: {password ?? "(无密码)"}", ConsoleColor.Gray, fileItem);

            try
            {
                (bool Success, string Message) result;
                if (stegoCarveMode)
                {
                    result = await Task.Run(() => _stego.CarveStegoPayloads(fileItem.FilePath, outputDir, taskId, token), token);
                }
                else
                {
                    result = await extractor.ExtractAsync(
                        fileItem.FilePath,
                        outputDir,
                        password,
                        onProgress: (msg) => AppendLog($"  {msg}", ConsoleColor.Gray),
                        onPercentChanged: (percent) =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                ProgressExtract.Value = percent;
                                TxtExtractProgress.Text = $"{percent}%";
                            });
                        },
                        showCliWindow: _settings.ShowCliWindow,
                        cancellationToken: token,
                        stegoArchiveType: isStego ? detectedType : null);
                }

                if (result.Success)
                {
                    // 重置进度条
                    Dispatcher.Invoke(() =>
                    {
                        ProgressExtract.Value = 0;
                        TxtExtractProgress.Text = "0%";
                    });
                    
                    // 不在这里立即设置状态，而是根据是否有子节点来决定
                    // 标记自身解压成功
                    fileItem.SetSelfExtractResult(true);
                    
                    // 记录觧压产生的目录节点（用于扁平化时只处理觧压的目录）
                    AppendLog($"[线程 {taskId}] [DEBUG] 检查解压目录是否存在: {outputDir}, 存在={Directory.Exists(outputDir)}", ConsoleColor.Gray, fileItem);
                    if (Directory.Exists(outputDir))
                    {
                        _extractedDirectoryNodes.TryAdd(outputDir, 0);
                        AppendLog($"[线程 {taskId}] {fileItem.FileName}: 已标记压目录节点 {outputDir}", ConsoleColor.Gray, fileItem);
                    }
                    else
                    {
                        AppendLog($"[线程 {taskId}] [警告] {fileItem.FileName}: 解压目录不存在，未标记节点: {outputDir}", ConsoleColor.Yellow, fileItem);
                    }
                    
                    // 从密码映射表中移除已完成处理的文件记录
                    string filePath = fileItem.FilePath;
                    _passwordMap.Remove(filePath);
                    AppendLog($"[线程 {taskId}] {fileItem.FileName}: 已从密码映射表清除", ConsoleColor.Gray, fileItem);

                    // 添加短暂延迟，让7z.exe进程完全释放文件句柄
                    await Task.Delay(500);

                    // 先检查解压后的文件是否包含新的压缩文件
                    // 这会添加子项到 fileItem.Children，并将新文件加入待处理队列
                    // 传入 existingFilesBeforeExtract 过滤掉解压前已存在的文件
                    await ScanExtractedFilesForArchives(outputDir, taskId, fileItem, existingFilesBeforeExtract);
                    
                    // 【残留清理】隐写文件解压后，删除非压缩包的残留文件
                    // -t# 可能产生非存档垃圾，这些不会被加入队列，直接删除即可
                    if (isStego)
                    {
                        // 真实类型确认时，容器内白名单文件（如封面图）是真实内容，
                        // 不属于 -t# 碎片残留，不应删除
                        string? stegoType = _stego.DetectStegoArchiveType(fileItem.FilePath);
                        bool stegoTypeTrusted = !string.IsNullOrEmpty(stegoType) && stegoType != "#";
                        HashSet<string>? whitelist = stegoTypeTrusted
                            ? _stego.GetStegoArchiveValidFiles(fileItem.FilePath)
                            : null;

                        // 仅当真实类型确认且白名单非空时才清理：
                        // 类型回退为 #（无法确定真实格式）时，-t# 提取产物无法区分
                        // 误匹配碎片与真实内容文件，删除有丢失产物风险，一律保留
                        if (whitelist != null && whitelist.Count > 0)
                        {
                            try
                            {
                                var topFiles = Directory.GetFiles(outputDir, "*", SearchOption.TopDirectoryOnly);
                                foreach (var f in topFiles)
                                {
                                    string fName = Path.GetFileName(f);
                                    if (whitelist.Contains(fName))
                                        continue;
                                    if (!_detector.IsArchiveFile(f, false, isDroppedFile: false))
                                    {
                                        AppendLog($"[线程 {taskId}] [残留清理] 删除非压缩包残留: {fName}", ConsoleColor.Yellow, fileItem);
                                        try { File.Delete(f); } catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                        else
                        {
                            AppendLog($"[线程 {taskId}] [残留清理] 跳过：隐写类型未确认({stegoType ?? "null"})，保留全部提取产物", ConsoleColor.Yellow, fileItem);
                        }
                    }
                    
                    // 修改：根据是否有子节点来决定显示状态
                    if (fileItem.Children.Count > 0)
                    {
                        // 有子节点，显示等待状态
                        var passwordInfo = password != null
                            ? $" (密码: {password})"
                            : " (无密码)";
                        Dispatcher.Invoke(() =>
                        {
                            fileItem.Status = $"等待子节点完成{passwordInfo} ({fileItem.GetProgressInfo()})";
                        });
                        AppendLog($"[{fileItem.FileName}] 已添加 {fileItem.Children.Count} 个子压缩包到待处理队列，等待递归处理...", ConsoleColor.Cyan, fileItem);
                    }
                    // 叶子节点已在 ScanExtractedFilesForArchives 中处理完成
                }
                else
                {
                    // 重置进度条
                    Dispatcher.Invoke(() =>
                    {
                        ProgressExtract.Value = 0;
                        TxtExtractProgress.Text = "0%";
                    });
                    
                    Dispatcher.Invoke(() => fileItem.Status = $"压失败: {result.Message}");
                    AppendLog($"[线程 {taskId}] {fileItem.FileName}: 觧压失败 - {result.Message}", ConsoleColor.Red, fileItem);
                    
                    // 标记自身解压失败
                    fileItem.SetSelfExtractResult(false);
                    
                    // 解压失败时不处理原文件，只检查父项状态（传入子节点）
                    if (fileItem.Parent != null)
                    {
                        UpdateParentStatusWhenChildrenComplete(fileItem);
                    }
                    else
                    {
                        CheckAndMarkParentComplete(fileItem);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => fileItem.Status = "已取消");
                AppendLog($"[线程 {taskId}] {fileItem.FileName}: 已取消", ConsoleColor.Yellow, fileItem);
                
                // 取消时也需要检查父项状态（传入子节点）
                if (fileItem.Parent != null)
                {
                    UpdateParentStatusWhenChildrenComplete(fileItem);
                }
                else
                {
                    CheckAndMarkParentComplete(fileItem);
                }
                throw;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => fileItem.Status = $"异常: {ex.Message}");
                AppendLog($"[线程 {taskId}] {fileItem.FileName}: 异常 - {ex.Message}", ConsoleColor.Red, fileItem);
                
                // 异常时也需要检查父项状态（传入子节点）
                if (fileItem.Parent != null)
                {
                    UpdateParentStatusWhenChildrenComplete(fileItem);
                }
                else
                {
                    CheckAndMarkParentComplete(fileItem);
                }
            }
        }

        /// <summary>
        /// 扫描后无（或不再有）待入队子压缩包时，对当前解压项做叶子收尾：更新状态、按设置清理当前归档、再向上汇报。
        /// 须与「发现候选但 addedCount==0」（例如全部被黑名单删除）分支共用，否则会跳过清理中间层压缩包。
        /// </summary>
        private void FinishLeafAfterScanNoChildArchivesToQueue(FileItem leafItem, int taskId, string debugSuffix = "")
        {
            if (leafItem == null)
                return;

            string suffix = string.IsNullOrEmpty(debugSuffix) ? "" : " " + debugSuffix;
            AppendLog($"[线程 {taskId}] [DEBUG] 无子压缩包，处理原文件并更新父项状态: {leafItem.FileName}{suffix}", ConsoleColor.Magenta, leafItem);
            
            var passwordInfo = leafItem.FoundPassword != null
                ? $" (密码: {leafItem.FoundPassword})"
                : " (无密码)";
            Dispatcher.Invoke(() =>
            {
                leafItem.Status = $"觧压成功{passwordInfo}";
            });
            DiagLog($"[DIAG] FinishLeaf 2441 前: {leafItem.FileName}");
            AppendLog($"[{leafItem.FileName}] 叶子节点觧压完成: {leafItem.Status}", ConsoleColor.Green, leafItem);
            DiagLog($"[DIAG] FinishLeaf 2441 后: {leafItem.FileName}, Parent={(leafItem.Parent?.FileName ?? "NULL")}");
            
            if (leafItem.Parent == null)
            {
                // 顶级叶子节点：走统一收尾流程（先智能路径扁平化，再清理原压缩包）
                AppendLog($"[{leafItem.FileName}] 顶级叶子节点觧压完成，触发智能路径处理...", ConsoleColor.Cyan, leafItem);
                _ = FinalizeTopLevelExtractAndSmartPathAsync(leafItem.FilePath);
            }
            else
            {
                // 非顶级叶子节点：仅向上汇报完成（子压缩包清理统一在后续阶段处理）
                UpdateParentStatusWhenChildrenComplete(leafItem);
            }
        }

        /// <summary>
        /// 扫描解压后的文件，检测是否包含新的压缩文件并添加到列表
        /// 注意：此方法只负责扫描和添加文件，不测试密码，不启动解压
        /// 密码测试和解压由 CheckAndTriggerNextRoundExtractAsync 统一处理
        /// </summary>
        /// <param name="outputDir">输出目录</param>
        /// <param name="taskId">任务ID</param>
        /// <param name="parentFileItem">父文件项</param>
        /// <param name="existingFilesBeforeExtract">解压前已存在的文件列表，用于过滤</param>
        private async Task ScanExtractedFilesForArchives(string outputDir, int taskId, FileItem? parentFileItem = null, HashSet<string>? existingFilesBeforeExtract = null)
        {
            try
            {
                AppendLog($"[线程 {taskId}] [DEBUG] 开始扫描压文件: {outputDir}", ConsoleColor.Magenta, parentFileItem);
                            
                if (!Directory.Exists(outputDir))
                {
                    AppendLog($"[线程 {taskId}] [DEBUG] 输出目录不存在: {outputDir}", ConsoleColor.Magenta, parentFileItem);
                    // 目录不存在说明当前项没有产生任何输出（如隐写容器解压后无隐藏数据）
                    // 需要通知父节点当前叶子节点已完成
                    if (parentFileItem != null)
                    {
                        FinishLeafAfterScanNoChildArchivesToQueue(parentFileItem, taskId, "(输出目录不存在)");
                    }
                    return;
                }
            
                // 先收集输出目录中所有文件，过滤掉解压前已存在的文件，只对新解压的文件做压缩包检测
                var archiveExtensions = _settings.GetArchiveExtensions();
                var allArchiveFiles = new List<string>();

                // 获取输出目录中所有文件
                var allFiles = Directory.GetFiles(outputDir, "*.*", SearchOption.AllDirectories);

                // 【关键优化】先过滤掉解压前已存在的文件，只保留新解压的文件
                // 这样在 ArchiveDir 模式下不会误扫描整个源目录
                List<string> filesToScan;
                if (existingFilesBeforeExtract != null && existingFilesBeforeExtract.Count > 0)
                {
                    filesToScan = allFiles.Where(f => !existingFilesBeforeExtract.Contains(f)).ToList();
                    AppendLog($"[线程 {taskId}] [DEBUG] 输出目录总文件: {allFiles.Length}, 新解压文件: {filesToScan.Count}", ConsoleColor.Magenta, parentFileItem);
                }
                else
                {
                    filesToScan = allFiles.ToList();
                }

                // 【防和谐清理】解压发现的嵌套文件也可能带防和谐脏名，检测前先清理
                filesToScan = _archiveNameCleaner.CleanDirtyArchiveFileNames(filesToScan);

                // 只对新解压的文件进行压缩包检测
                foreach (var file in filesToScan)
                {
                    // 1. 先检查扩展名（快速路径）
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (archiveExtensions.Contains(ext) || _volumeDetector.IsMultiVolumeArchive(file))
                    {
                        allArchiveFiles.Add(file);
                        continue;
                    }

                    // 2. 使用魔术数检测（不依赖扩展名，仅在非扩展名匹配时使用）
                    // 注意：解压后发现的文件不受强制模式影响，始终进行正常检测
                    if (_detector.IsArchiveFile(file, false, isDroppedFile: false))
                    {
                        allArchiveFiles.Add(file);
                    }
                }

                // 【隐写垃圾过滤】-t# 哈希扫描产生的文件用标准 7z l（不带 -t#）逐文件验证
                // 真正的压缩包通得过标准检测，-t# 误匹配的垃圾文件通不过
                if (parentFileItem != null && _stego.IsStegoDetectionActiveForFile(parentFileItem.FilePath))
                {
                    // 白名单：探测阶段已确认容器内真实文件列表，命中白名单的直接通过，
                    // 避免标准验证对完整文件产生误伤（仅真实类型确认后启用）
                    string? stegoType = _stego.DetectStegoArchiveType(parentFileItem.FilePath);
                    bool stegoTypeTrusted = !string.IsNullOrEmpty(stegoType) && stegoType != "#";
                    HashSet<string>? whitelist = stegoTypeTrusted
                        ? _stego.GetStegoArchiveValidFiles(parentFileItem.FilePath)
                        : null;
                    // 仅当真实类型确认且白名单非空时，验证失败的文件才允许删除；
                    // 否则无法区分 -t# 误匹配碎片与真实内容，删除会丢失产物
                    bool canDeleteJunk = whitelist != null && whitelist.Count > 0;

                    var beforeCount = allArchiveFiles.Count;
                    var verifiedFiles = new List<string>();
                    foreach (var f in allArchiveFiles)
                    {
                        string fName = Path.GetFileName(f);
                        AppendLog($"[线程 {taskId}] [隐写验证] 验证文件: {fName}", ConsoleColor.Gray, parentFileItem);
                        if (whitelist != null && whitelist.Count > 0 && whitelist.Contains(fName))
                        {
                            verifiedFiles.Add(f);
                            AppendLog($"[线程 {taskId}] [隐写验证]   ✓ 白名单通过: {fName}", ConsoleColor.Green, parentFileItem);
                            continue;
                        }
                        if (_detector.VerifyIsRealArchive(f))
                        {
                            verifiedFiles.Add(f);
                            AppendLog($"[线程 {taskId}] [隐写验证]   ✓ 通过: {fName}", ConsoleColor.Green, parentFileItem);
                        }
                        else
                        {
                            if (canDeleteJunk)
                            {
                                // 标准 7z l 验证失败 → -t# 误匹配垃圾，从磁盘删除
                                AppendLog($"[线程 {taskId}] [隐写验证]   ✗ 垃圾文件，删除: {fName}", ConsoleColor.Yellow, parentFileItem);
                                try { File.Delete(f); } catch { }
                            }
                            else
                            {
                                // 隐写类型未确认，无法判定是否为垃圾，保留文件避免丢失产物
                                AppendLog($"[线程 {taskId}] [隐写验证]   ⚠ 类型未确认，保留文件（不入队）: {fName}", ConsoleColor.Yellow, parentFileItem);
                            }
                        }
                    }
                    allArchiveFiles = verifiedFiles;
                    AppendLog($"[线程 {taskId}] [隐写验证] 过滤前: {beforeCount} → 过滤后: {allArchiveFiles.Count}", ConsoleColor.Cyan, parentFileItem);
                }

                AppendLog($"[线程 {taskId}] [DEBUG] 找到 {allArchiveFiles.Count} 个候选压缩文件", ConsoleColor.Magenta, parentFileItem);

                if (allArchiveFiles.Count == 0)
                {
                    AppendLog($"[线程 {taskId}] 未发现新的压缩文件", ConsoleColor.Gray, parentFileItem);
                    if (parentFileItem != null)
                        FinishLeafAfterScanNoChildArchivesToQueue(parentFileItem, taskId);
                    return;
                }

                AppendLog($"[线程 {taskId}] 发现 {allArchiveFiles.Count} 个新的压缩文件", ConsoleColor.Cyan, parentFileItem);

                int addedCount = 0;
                foreach (var archiveFile in allArchiveFiles)
                {
                    // 检查黑名单 - 如果是黑名单文件则直接删除，不加入处理队列
                    if (_blacklistService.IsBlacklistedFile(archiveFile, out string matchedPattern))
                    {
                        try
                        {
                            // 带重试的删除操作
                            bool deleted = false;
                            for (int retry = 0; retry < 3; retry++)
                            {
                                try
                                {
                                    File.Delete(archiveFile);
                                    deleted = true;
                                    break;
                                }
                                catch (IOException) when (retry < 2)
                                {
                                    // 文件被占用，等待后重试
                                    System.Threading.Thread.Sleep(200);
                                }
                            }
                            
                            if (deleted)
                            {
                                AppendLog($"[线程 {taskId}] [黑名单] 已删除: {Path.GetFileName(archiveFile)} (匹配模式: {matchedPattern})", ConsoleColor.Yellow);
                            }
                            else
                            {
                                AppendLog($"[线程 {taskId}] [黑名单] 删除失败: {Path.GetFileName(archiveFile)} - 文件被占用", ConsoleColor.Red);
                            }
                        }
                        catch (Exception delEx)
                        {
                            AppendLog($"[线程 {taskId}] [黑名单] 删除失败: {Path.GetFileName(archiveFile)} - {delEx.Message}", ConsoleColor.Red);
                        }
                        continue;  // 跳过后续处理，不添加到列表
                    }
                    
                    // 检查是否已在列表中
                    FileItem? existingItem = null;
                    Dispatcher.Invoke(() =>
                    {
                        existingItem = FindFileItemInTree(archiveFile);
                    });

                    if (existingItem != null)
                    {
                        AppendLog($"[线程 {taskId}] [DEBUG] 文件已存在: {Path.GetFileName(archiveFile)}", ConsoleColor.Magenta);
                        continue;
                    }

                    // 如果是分卷压缩包，检测所有分卷并只添加主卷
                    if (_settings.EnableMultiVolumeDetection && _volumeDetector.IsMultiVolumeArchive(archiveFile))
                    {
                        var volumeInfo = _volumeDetector.DetectMultiVolumeArchive(archiveFile);
                        
                        if (volumeInfo.HasVolumeList)
                        {
                            AppendLog($"[线程 {taskId}] 检测到分卷压缩包: {volumeInfo.VolumeCount} 个分卷", ConsoleColor.Cyan);
                            
                            // 只添加主卷到列表
                            var mainFileInfo = new FileInfo(volumeInfo.MainVolumePath);
                            var mainVolumeItem = new FileItem
                            {
                                FileName = mainFileInfo.Name,
                                FilePath = volumeInfo.MainVolumePath,
                                Status = "等待处理",
                                FileSize = volumeInfo.AllVolumePaths.Sum(p => new FileInfo(p).Length),
                                Parent = parentFileItem,
                                VolumeInfo = volumeInfo,  // 设置分卷信息！
                                CurrentDepth = parentFileItem != null ? parentFileItem.CurrentDepth + 1 : 1  // 子压缩包层数+1
                            };

                            Dispatcher.Invoke(() =>
                            {
                                if (parentFileItem != null)
                                {
                                    parentFileItem.Children.Add(mainVolumeItem);
                                }
                                else
                                {
                                    _fileList.Add(mainVolumeItem);
                                }
                            });

                            addedCount++;
                            AppendLog($"[线程 {taskId}] 添加分卷压缩包（主卷）: {mainVolumeItem.FileName} ({volumeInfo.VolumeCount} 个分卷)", ConsoleColor.Green);
                            continue;  // 跳过后续单个文件添加逻辑
                        }
                    }

                    // 创建新的文件项（普通文件或非分卷压缩包）
                    var fileInfo = new FileInfo(archiveFile);
                    var fileItem = new FileItem
                    {
                        FileName = fileInfo.Name,
                        FilePath = archiveFile,
                        Status = "等待处理",  // 统一初始状态
                        FileSize = fileInfo.Length,
                        Parent = parentFileItem,
                        TaskId = taskId,
                        CurrentDepth = parentFileItem != null ? parentFileItem.CurrentDepth + 1 : 1  // 子压缩包层数+1
                    };

                    Dispatcher.Invoke(() =>
                    {
                        if (parentFileItem != null)
                        {
                            parentFileItem.Children.Add(fileItem);
                            AppendLog($"[线程 {taskId}] {parentFileItem.FileName} 的子压缩包: {fileItem.FileName}", ConsoleColor.Gray);
                        }
                        else
                        {
                            _fileList.Add(fileItem);
                        }
                    });

                    addedCount++;
                    AppendLog($"[线程 {taskId}] 添加新压缩文件: {fileItem.FileName}", ConsoleColor.Green);
                }

                if (addedCount > 0)
                {
                    AppendLog($"[线程 {taskId}] 共添加 {addedCount} 个新压缩文件到待处理队列", ConsoleColor.Cyan);
                    
                    // 设置父节点的预期子节点数量
                    if (parentFileItem != null)
                    {
                        parentFileItem.SetExpectedChildrenCount(addedCount);
                        AppendLog($"[线程 {taskId}] {parentFileItem.FileName} 设置了预期子节点数: {addedCount}", ConsoleColor.Gray);
                    }
                    
                    // 将新文件加入待处理队列
                    Dispatcher.Invoke(() =>
                    {
                        var children = parentFileItem?.Children ?? new ObservableCollection<FileItem>();
                        foreach (var child in children)
                        {
                            if (child.Status == "等待处理")
                            {
                                _pendingQueue.Enqueue(child);
                                AppendLog($"[线程 {taskId}] {child.FileName} 已加入待处理队列", ConsoleColor.Gray);
                            }
                        }
                    });
                    
                    // 唤醒测试线程
                    WakeupTestThread();
                }
                else
                {
                    // 有候选但未入队（黑名单删除、已在树中等）：须走与「零候选」相同的叶子收尾，否则会跳过清理当前层归档且父链不完整
                    if (parentFileItem != null)
                    {
                        AppendLog($"[线程 {taskId}] 没有新文件入队，执行叶子收尾: {parentFileItem.FileName}", ConsoleColor.Gray);
                        FinishLeafAfterScanNoChildArchivesToQueue(parentFileItem, taskId, "(候选未入队，可能已按黑名单删除或跳过)");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[线程 {taskId}] 扫描解压文件失败: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 在树形结构中查找文件项
        /// </summary>
        private FileItem? FindFileItemInTree(string filePath)
        {
            foreach (var item in _fileList)
            {
                var found = FindFileItemInItem(item, filePath);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>
        /// 在单个文件项及其子项中查找文件
        /// </summary>
        private FileItem? FindFileItemInItem(FileItem item, string filePath)
        {
            if (item.FilePath == filePath)
                return item;
            
            // 检查分卷信息
            if (item.VolumeInfo?.AllVolumePaths.Contains(filePath) == true)
                return item;
            
            // 递归检查子项
            foreach (var child in item.Children)
            {
                var found = FindFileItemInItem(child, filePath);
                if (found != null)
                    return found;
            }
            
            return null;
        }

        /// <summary>
        /// 检查文件是否已存在于树形结构中
        /// </summary>
        private bool CheckFileExistsInTree(string filePath)
        {
            return FindFileItemInTree(filePath) != null;
        }

        /// <summary>
        /// 当子项全部完成时，更新父项状态（反向传播机制）
        /// </summary>
        private void UpdateParentStatusWhenChildrenComplete(FileItem childItem)
        {
            var parentItem = childItem.Parent;
            if (parentItem == null)
                return;  // 没有父节点，不需要上报

            // 标记父节点的一个子节点完成
            DiagLog($"[DIAG] UpdateParent 入口: child={childItem.FileName}, parent={parentItem.FileName}");
            bool allCompleted = parentItem.MarkChildComplete();
            DiagLog($"[DIAG] MarkChildComplete 完成: {parentItem.FileName}, allCompleted={allCompleted}");
            
            DiagLog($"[DIAG] UpdateParent 2798 前: {parentItem.FileName}");
            AppendLog($"[{parentItem.FileName}] 子节点完成进度: {parentItem.GetProgressInfo()}", ConsoleColor.Gray);
            DiagLog($"[DIAG] UpdateParent 2798 后: {parentItem.FileName}");

            // 如果所有子节点都已完成，更新父节点状态并继续向上上报
            if (allCompleted)
            {
                // 检查父节点自身的解压结果
                bool parentSelfSuccess = parentItem.GetSelfExtractResult();
                
                if (parentSelfSuccess)
                {
                    // 父节点自身解压成功，更新状态为完成
                    var passwordInfo = parentItem.FoundPassword != null 
                        ? $" (密码: {parentItem.FoundPassword})" 
                        : " (无密码)";
                    
                    Dispatcher.Invoke(() =>
                    {
                        parentItem.Status = $"解压成功{passwordInfo} [包含 {parentItem.Children.Count} 个子压缩包]";
                    });
                    
                    AppendLog($"[{parentItem.FileName}] ✓ 所有子压缩包处理完成，父压缩包标记为完成", ConsoleColor.Green);
                    
                    // 从密码映射表中移除
                    _passwordMap.Remove(parentItem.FilePath);
                    // 子压缩包清理统一在 FinalizeTopLevelExtractAndSmartPathAsync 阶段处理
                }
                else
                {
                    // 父节点自身解压失败，保留失败状态
                    AppendLog($"[{parentItem.FileName}] ⚠ 父节点自身解压失败，即使所有子节点完成也不处理原文件", ConsoleColor.Yellow);
                }
                
                // 递归处理：向上一级上报
                if (parentItem.Parent != null)
                {
                    AppendLog($"[{parentItem.FileName}] 向上一级上报完成状态...", ConsoleColor.Cyan);
                    UpdateParentStatusWhenChildrenComplete(parentItem);
                }
                else
                {
                    // 到达顶级，整个树形结构处理完成
                    AppendLog($"[{parentItem.FileName}] ✓ 已到达顶级，整个树形结构处理完成", ConsoleColor.Green);
                    
                    if (parentSelfSuccess)
                        _ = FinalizeTopLevelExtractAndSmartPathAsync(parentItem.FilePath);
                    else
                        // 异步触发，避免阻塞后台线程
                        _ = Dispatcher.InvokeAsync(() => CheckAndTriggerBatchSmartPathProcessing());
                }
            }
            else
            {
                // 还有子节点未完成，更新状态显示进度
                Dispatcher.Invoke(() =>
                {
                    parentItem.Status = $"等待子节点完成 ({parentItem.GetProgressInfo()})";
                });
                AppendLog($"[{parentItem.FileName}] 还有子节点未完成，当前状态: {parentItem.Status}", ConsoleColor.Gray);
            }
        }

        /// <summary>
        /// 更新UI状态
        /// </summary>
        private void UpdateUiState()
        {
            Dispatcher.Invoke(() =>
            {
                bool isAutoMode = _settings.ExtractMode == ExtractMode.Auto;
                bool isProcessing = _isTesting || _isExtracting;
                
                // 更新顶部状态栏
                if (_isPaused)
                {
                    TxtStatusMain.Text = "状态: 已暂停";
                    TxtStatusMain.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);
                }
                else if (isProcessing)
                {
                    TxtStatusMain.Text = "状态: 运行中";
                    TxtStatusMain.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
                }
                else
                {
                    TxtStatusMain.Text = "状态: 未开始";
                    TxtStatusMain.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
                }
                
                // 更新统一操作按钮
                if (_isPaused)
                {
                    BtnMainAction.Content = "继续";
                }
                else if (isProcessing)
                {
                    BtnMainAction.Content = "暂停";
                }
                else
                {
                    BtnMainAction.Content = "开始解压";
                }
                
                // 手动模式下，未开始时才启用按钮
                if (!isAutoMode)
                {
                    BtnMainAction.IsEnabled = !isProcessing;
                }
                else
                {
                    // 自动模式始终启用
                    BtnMainAction.IsEnabled = true;
                }

                TxtStatusTestThread.Text = $"测试线程: {(_isTesting ? (_isPaused ? "已暂停" : "工作中") : "空闲")}";
                TxtStatusExtractThread.Text = $"解压线程: {(_isExtracting ? (_isPaused ? "已暂停" : "工作中") : "空闲")}";
            });
        }

        /// <summary>
        /// 检查是否有待解压的文件，并在自动模式下触发解压
        /// </summary>
        private async void CheckAndTriggerAutoExtract()
        {
            // 只有在自动模式下才检查
            if (_settings.ExtractMode != ExtractMode.Auto)
                return;

            // 如果正在测试或解压，不触发
            if (_isTesting || _isExtracting)
                return;

            // 启动双队列处理流程
            StartDualQueueProcessing();
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 检查是否所有顶级文件项都已完成，如果是则触发批量智能路径处理
        /// </summary>
        private void CheckAndTriggerBatchSmartPathProcessing()
        {
            // 添加调试日志
            AppendLog($"[批量智能路径] 检查触发条件: EnableSmartPathProcessing={_settings.EnableSmartPathProcessing}, OutputMode={_settings.OutputMode}", ConsoleColor.Gray);
            
            if (!_settings.EnableSmartPathProcessing)
            {
                AppendLog($"[批量智能路径] 智能路径处理未启用，跳过处理", ConsoleColor.Gray);
                return;
            }

            // 检查是否所有顶级文件项都已完成
            var topLevelItems = _fileList.Where(f => f.Parent == null).ToList();
            AppendLog($"[批量智能路径] 顶级文件项数量: {topLevelItems.Count}", ConsoleColor.Gray);
            
            if (topLevelItems.Count == 0)
            {
                AppendLog($"[批量智能路径] 没有顶级文件项，跳过处理", ConsoleColor.Gray);
                return;
            }

            // 打印每个顶级项的状态用于调试
            foreach (var item in topLevelItems)
            {
                AppendLog($"[批量智能路径] 顶级项: {item.FileName}, 状态: {item.Status}", ConsoleColor.Gray);
            }

            bool allCompleted = topLevelItems.All(item => 
            {
                // 检查状态是否表示完成（扩大匹配范围）
                return item.Status.Contains("解压成功") || 
                       item.Status.Contains("完成") ||
                       item.Status.Contains("跳过") ||
                       item.Status.Contains("非压缩文件") ||
                       item.Status.Contains("失败") ||
                       item.Status.Contains("异常") ||
                       item.Status.Contains("取消");
            });

            AppendLog($"[批量智能路径] 所有项是否完成: {allCompleted}", ConsoleColor.Gray);

            if (allCompleted)
            {
                AppendLog($"[批量智能路径] 所有顶级文件项已完成，开始批量处理...", ConsoleColor.Cyan);
                
                // 收集所有解压生成的文件夹（而不是目标目录）
                var extractedFolders = new HashSet<string>();
                foreach (var item in topLevelItems)
                {
                    // 在 ArchiveFolder 模式下，输出目录是压缩包同名文件夹
                    // 例如: ba366.7z.001 -> D:\tem\ba366
                    string extractedFolder = _detector.GetOutputDirectory(item.FilePath);
                    
                    // 对于 ArchiveDir 模式（解压到当前目录），不进行扁平化处理
                    // 因为输出目录就是压缩包所在目录，会误处理其他无关文件夹
                    string archiveDir = Path.GetDirectoryName(item.FilePath) ?? string.Empty;
                    if (extractedFolder.Equals(archiveDir, StringComparison.OrdinalIgnoreCase))
                    {
                        AppendLog($"[批量智能路径] 顶级项 {item.FileName} 使用 ArchiveDir 模式，跳过扁平化", ConsoleColor.Gray);
                        continue;
                    }
                    
                    // SpecificDir 模式（统一输出到指定目录），不进行扁平化处理
                    if (_settings.OutputMode == OutputMode.SpecificDir)
                    {
                        AppendLog($"[批量智能路径] 顶级项 {item.FileName} 使用 SpecificDir 模式，跳过扁平化", ConsoleColor.Gray);
                        continue;
                    }
                    
                    AppendLog($"[批量智能路径] 顶级项 {item.FileName} 的解压文件夹: {extractedFolder}", ConsoleColor.Gray);
                    
                    if (!string.IsNullOrEmpty(extractedFolder) && Directory.Exists(extractedFolder))
                    {
                        extractedFolders.Add(extractedFolder);
                    }
                }

                AppendLog($"[批量智能路径] 找到 {extractedFolders.Count} 个解压文件夹待处理", ConsoleColor.Cyan);

                // 对每个解压生成的文件夹执行批量扁平化（fire-and-forget）
                // 【并发修复】持锁串行执行，避免与 FinalizeTopLevelExtractAndSmartPathAsync 的收尾并发移动同一目录树
                _ = Task.Run(async () =>
                {
                    await _smartPathFinalizeLock.WaitAsync();
                    try
                    {
                        foreach (var extractedFolder in extractedFolders)
                        {
                            await _smartPath.ProcessSmartPathBatchAsync(extractedFolder);
                        }
                    }
                    finally
                    {
                        _smartPathFinalizeLock.Release();
                    }
                });
            }
        }

        /// <summary>
        /// 更新顶级FileItem的状态为"解压完成-扁平化处理完成"
        /// </summary>
        private void UpdateTopLevelItemCompletedStatus(string topArchivePath)
        {
            try
            {
                var topLevelItem = FindFileItemInTreeThreadSafe(topArchivePath);
                if (topLevelItem == null)
                {
                    AppendLog($"[状态更新] 未找到顶级FileItem: {Path.GetFileName(topArchivePath)}", ConsoleColor.Yellow);
                    return;
                }

                var passwordInfo = topLevelItem.FoundPassword != null
                    ? $" (密码: {topLevelItem.FoundPassword})"
                    : " (无密码)";

                // 获取最终扁平化后的路径
                // 直接使用 FindFinalFlattenedPath，它内部已处理目录不存在/被扁平化重命名等所有情况
                string outputDir = _detector.GetOutputDirectory(topArchivePath);
                AppendLog($"[状态更新] [DEBUG] outputDir(计算值)={outputDir}, 存在={Directory.Exists(outputDir)}", ConsoleColor.Gray);
                
                string? finalPath = _smartPath.FindFinalFlattenedPath(outputDir);
                AppendLog($"[状态更新] [DEBUG] FindFinalFlattenedPath({outputDir}) = {finalPath}", ConsoleColor.Gray);

                Dispatcher.Invoke(() =>
                {
                    topLevelItem.Status = $"解压完成-扁平化处理完成{passwordInfo}";
                    topLevelItem.FinalOutputPath = finalPath;

                    // 如果当前选中的是该节点，立即更新最终路径显示
                    if (LstFiles.SelectedItem is FileItem selectedItem && selectedItem == topLevelItem)
                    {
                        TxtFinalPath.Text = finalPath ?? "无";
                    }
                });

                AppendLog($"[{topLevelItem.FileName}] 状态已更新: 解压完成-扁平化处理完成", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"[状态更新] 失败: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 更新扁平化处理完成后的FileItem状态
        /// </summary>
        private void UpdateSmartPathCompletedStatus(string rootDir)
        {
            try
            {
                // 找到所有输出目录在 rootDir 下的 FileItem
                var relatedItems = SnapshotTopLevelItems().Where(item => 
                {
                    string outputPath = _detector.GetOutputDirectory(item.FilePath);
                    return outputPath.Equals(rootDir, StringComparison.OrdinalIgnoreCase) ||
                           outputPath.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                }).ToList();

                if (relatedItems.Count == 0)
                {
                    AppendLog($"[批量智能路径] [状态更新] 未找到相关的FileItem", ConsoleColor.Gray);
                    return;
                }

                AppendLog($"[批量智能路径] [状态更新] 找到 {relatedItems.Count} 个相关的FileItem", ConsoleColor.Gray);
                
                // 获取最终扁平化后的路径
                string? finalPath = _smartPath.FindFinalFlattenedPath(rootDir);
                AppendLog($"[批量智能路径] [状态更新] 最终路径: {finalPath}", ConsoleColor.Gray);

                foreach (var item in relatedItems)
                {
                    // 【修复】跳过顶级项（Parent == null）——这些会由 UpdateTopLevelItemCompletedStatus 统一处理
                    // 只更新非顶级项中状态包含"解压成功"的子压缩包节点
                    if (item.Parent != null && item.Status.Contains("解压成功"))
                    {
                        var passwordInfo = item.FoundPassword != null 
                            ? $" (密码: {item.FoundPassword})" 
                            : " (无密码)";
                                        
                        Dispatcher.Invoke(() =>
                        {
                            // 【修复】FinalOutputPath 需要在 UI 线程设置（触发 OnPropertyChanged 通知）
                            item.FinalOutputPath = finalPath;
                            item.Status = $"解压完成-扁平化处理完成{passwordInfo}";
                                            
                            // 如果当前选中的是该节点，立即更新最终路径显示
                            if (LstFiles.SelectedItem is FileItem selectedItem && selectedItem == item)
                            {
                                TxtFinalPath.Text = finalPath ?? "无";
                            }
                        });
                                                                
                        AppendLog($"[{item.FileName}] 状态已更新: 解压完成-扁平化处理完成", ConsoleColor.Green, item);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[批量智能路径] [状态更新] 失败: {ex.Message}", ConsoleColor.Red);
            }
        }
        



        /// <summary>
        /// 扫描目录中的压缩文件并添加到测试列表
        /// </summary>
        /// <param name="directoryPath">要扫描的目录路径</param>
        private void ScanAndAddArchiveFiles(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                AppendLog($"目录不存在: {directoryPath}", ConsoleColor.Red);
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
                    UpdateRootNodeTitle();
                }

                if (addedCount > 0)
                {
                    AppendLog($"从目录中发现 {addedCount} 个新的压缩文件", ConsoleColor.Cyan);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"扫描目录失败: {ex.Message}", ConsoleColor.Red);
            }
        }


        /// <summary>
        /// TreeView 选择改变事件 - 显示选中节点及其子节点的日志
        /// </summary>
        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var selectedItem = e.NewValue;
            
            List<LogEntry> logsToShow = new List<LogEntry>();
            
            if (selectedItem is RootNode rootNode)
            {
                // 根节点:显示所有日志
                logsToShow = rootNode.CollectAllLogs();
                
                // 调试：打印日志统计
                System.Diagnostics.Debug.WriteLine($"[根节点日志统计] 共收集到 {logsToShow.Count} 条日志");
                foreach (var child in rootNode.Children)
                {
                    var childLogs = child.CollectAllLogs();
                    System.Diagnostics.Debug.WriteLine($"  - {child.FileName}: {childLogs.Count} 条日志");
                }
                
                // 清空最终路径显示
                TxtFinalPath.Text = "无";
            }
            else if (selectedItem is FileItem fileItem)
            {
                // 文件节点:显示该节点及所有子节点的日志
                logsToShow = fileItem.CollectAllLogs();
                
                // 调试：打印日志统计
                System.Diagnostics.Debug.WriteLine($"[文件节点日志统计] {fileItem.FileName} 收集到 {logsToShow.Count} 条日志");
                
                // 显示最终路径：优先使用节点的 FinalOutputPath，其次递归查找子节点中顶级项的路径
                string? pathToShow = ResolveFinalPathForDisplay(fileItem);
                TxtFinalPath.Text = pathToShow ?? "无";
                
                System.Diagnostics.Debug.WriteLine($"[文件节点路径] {fileItem.FileName}: FinalOutputPath={fileItem.FinalOutputPath}, 显示={TxtFinalPath.Text}");
            }
            else
            {
                // 选中项为 null 或其他类型，清空显示
                TxtFinalPath.Text = "无";
            }
            
            // 显示日志(不清空,直接重新渲染)
            DisplayFilteredLogs(logsToShow, clearFirst: true);
        }

        /// <summary>
        /// 为选中的文件节点解析最终路径显示
        /// 如果是顶级节点(没有 Parent)，直接返回 FinalOutputPath
        /// 如果是子节点，沿父链向上查找顶级节点的 FinalOutputPath
        /// </summary>
        private string? ResolveFinalPathForDisplay(FileItem fileItem)
        {
            // 如果当前节点就有 FinalOutputPath，直接使用
            if (!string.IsNullOrEmpty(fileItem.FinalOutputPath))
                return fileItem.FinalOutputPath;
            
            // 沿父链向上查找顶级节点的 FinalOutputPath
            var current = fileItem.Parent;
            while (current != null)
            {
                if (!string.IsNullOrEmpty(current.FinalOutputPath))
                    return current.FinalOutputPath;
                current = current.Parent;
            }
            
            // 如果当前节点是顶级节点（Parent 为 null），返回 null（表示无路径）
            return null;
        }

        /// <summary>
        /// 显示过滤后的日志
        /// </summary>
        /// <param name="logs">要显示的日志列表</param>
        /// <param name="clearFirst">是否先清空日志窗口</param>
        private void DisplayFilteredLogs(List<LogEntry> logs, bool clearFirst = false)
        {
            Dispatcher.Invoke(() =>
            {
                if (clearFirst)
                {
                    TxtLog.Clear();
                }
                
                foreach (var log in logs)
                {
                    string timestamp = log.Timestamp.ToString("HH:mm:ss");
                    TxtLog.AppendText($"[{timestamp}]--> {log.Message}\r\n");
                }
                TxtLog.ScrollToEnd();
            });
        }

        /// <summary>
        /// 检查 target 是否是 node 或其子孙节点
        /// </summary>
        private bool IsNodeOrDescendant(FileItem node, FileItem target)
        {
            if (node == target) return true;
            
            foreach (var child in node.Children)
            {
                if (IsNodeOrDescendant(child, target)) return true;
            }
            
            return false;
        }

        /// <summary>
        /// [DIAG] 文件诊断日志：直接写文件，绕过 UI。
        /// Dispatcher.Invoke 挂起时 AppendLog 会静默阻塞，诊断轨迹必须落盘才能定位中断点。
        /// </summary>
        private void DiagLog(string message)
        {
            _logger.Diag(message);
        }

        private void AppendLog(string message, ConsoleColor color, FileItem? targetNode = null)
        {
            _logger.Log(message, color, targetNode);
        }

        /// <summary>
        /// 更新根节点标题
        /// </summary>
        private void UpdateRootNodeTitle()
        {
            _rootNode.UpdateTitle();
        }

        #endregion
    }
}








