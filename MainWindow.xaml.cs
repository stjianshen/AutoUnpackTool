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
        private readonly ExtractionPipeline _pipeline;

        private bool _suppressBlacklistToggleEvent = true; // 初始化期间不响应开关变化

        // 以下状态由 ExtractionPipeline 持有，窗口侧同名转发（保持既有代码不变）
        private ConcurrentQueue<FileItem> _pendingQueue { get => _pipeline.PendingQueue; set => _pipeline.PendingQueue = value; }
        private ConcurrentQueue<FileItem> _extractQueue { get => _pipeline.ExtractQueue; set => _pipeline.ExtractQueue = value; }
        private ManualResetEventSlim _pendingQueueSignal => _pipeline.PendingQueueSignal;
        private ManualResetEventSlim _extractQueueSignal => _pipeline.ExtractQueueSignal;
        private CancellationTokenSource? _testCancellationTokenSource { get => _pipeline.TestCancellationSource; set => _pipeline.TestCancellationSource = value; }
        private CancellationTokenSource? _extractCancellationTokenSource { get => _pipeline.ExtractCancellationSource; set => _pipeline.ExtractCancellationSource = value; }
        private CancellationTokenSource? _globalCancellationTokenSource { get => _pipeline.GlobalCancellationSource; set => _pipeline.GlobalCancellationSource = value; }
        private bool _isTesting { get => _pipeline.IsTesting; set => _pipeline.IsTesting = value; }
        private bool _isExtracting { get => _pipeline.IsExtracting; set => _pipeline.IsExtracting = value; }
        private bool _isPaused { get => _pipeline.IsPaused; set => _pipeline.IsPaused = value; }
        private TaskCompletionSource<bool> _testCompletionSource { get => _pipeline.TestCompletionSource; set => _pipeline.TestCompletionSource = value; }
        private TaskCompletionSource<bool> _extractCompletionSource { get => _pipeline.ExtractCompletionSource; set => _pipeline.ExtractCompletionSource = value; }
        private List<FileItem> _topLevelItems => _pipeline.TopLevelItems;
        private PasswordMap _passwordMap { get => _pipeline.PasswordMap; set => _pipeline.PasswordMap = value; } // 密码映射表
        private HashSet<string> _droppedFolders => _pipeline.DroppedFolders;

        // 【并发修复】ConcurrentDictionary 保证解压线程 Add 与收尾线程遍历/移除的线程安全
        // （值无意义，仅用作线程安全集合的占位）
        // 共享集合由 SmartPathService 持有，MainWindow 侧通过属性转发，避免大范围改名
        private ConcurrentDictionary<string, byte> _extractedDirectoryNodes => _smartPath.ExtractedDirectoryNodes;
        
        // 记录扁平化结果映射：被扁平化删除的解压目录 -> 内容最终所在路径（用于最终路径追踪）
        private Dictionary<string, string> _flattenedFinalPathMap => _smartPath.FlattenedFinalPathMap;
        
        // 记录拖入的文件夹（用于后续智能路径处理，即使没有压缩包）——由 _pipeline 持有
        
        // 记录已处理的扁平化目录（防止重复处理导致死循环）
        private HashSet<string> _processedFlattenDirs => _smartPath.ProcessedFlattenDirs;

        // 【并发修复】收尾流程串行化锁：多个顶级文件并发完成时，收尾（清理+智能路径扁平化）必须串行执行，
        // 否则并发移动同一目录树会导致移动中断、目录冲突时生成 GUID 新目录、图片分散到多个文件夹。
        private SemaphoreSlim _smartPathFinalizeLock => _pipeline.SmartPathFinalizeLock;

        // 【并发修复】防止同一顶级文件被重复触发收尾（完成回调可能从多条路径触发，如叶子收尾与父链上报）
        private HashSet<string> _finalizingTopArchives => _pipeline.FinalizingTopArchives;

        // 【并发修复】保护 _isTesting/_isExtracting 的 check-then-act：多个后台线程（解压线程唤醒、监控线程重启）
        // 可能同时看到 false 而启动重复线程组，必须原子地检查并设置
        private object _threadStateLock => _pipeline.ThreadStateLock;


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

            // 解压管线：双队列 + 递归扫描 + 收尾；UI 交互通过 ITaskObserver 上报
            _pipeline = new ExtractionPipeline(_settings, _logger, new UiTaskObserver(this),
                _detector, _stego, _archiveNameCleaner, _blacklistService, _cleanup, _smartPath, _volumeDetector);
            _pipeline.AttachFileList(_fileList);
            _smartPath.GetAllItemsSnapshot = _pipeline.SnapshotAllItems;
            _pipeline.OnTopLevelCompleted = UpdateTopLevelItemCompletedStatus;
            
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
                    _pipeline.StartDualQueueProcessing();
                    
                    // 如果线程已经在运行，只需要唤醒测试线程即可
                    // （解压线程会在测试完成后被自动唤醒）
                    if (_isTesting || _isExtracting)
                    {
                        _pipeline.WakeupTestThread();
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
                    _pipeline.StartDualQueueProcessing();
                }
                else if (needTestThread)
                {
                    _pipeline.WakeupTestThread();
                }
                else
                {
                    _pipeline.WakeupExtractThread();
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
            _pipeline.StartDualQueueProcessing();
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
            _pipeline.StartDualQueueProcessing();
            
            // 唤醒测试线程
            _pipeline.WakeupTestThread();
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
            _pipeline.CancelAllOperations();
            
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
        /// 更新UI状态（等价于 ApplyThreadState(_isTesting, _isExtracting, _isPaused)）
        /// </summary>
        private void UpdateUiState()
        {
            ApplyThreadState(_isTesting, _isExtracting, _isPaused);
        }

        /// <summary>
        /// 根据传入的线程状态刷新状态栏与主按钮（ITaskObserver.OnThreadState 的落地实现）
        /// </summary>
        public void ApplyThreadState(bool isTesting, bool isExtracting, bool isPaused)
        {
            Dispatcher.Invoke(() =>
            {
                bool isAutoMode = _settings.ExtractMode == ExtractMode.Auto;
                bool isProcessing = isTesting || isExtracting;

                // 更新顶部状态栏
                if (isPaused)
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
                if (isPaused)
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

                TxtStatusTestThread.Text = $"测试线程: {(isTesting ? (isPaused ? "已暂停" : "工作中") : "空闲")}";
                TxtStatusExtractThread.Text = $"解压线程: {(isExtracting ? (isPaused ? "已暂停" : "工作中") : "空闲")}";
            });
        }

        /// <summary>
        /// 强制把状态栏置为“未开始”（ITaskObserver.OnIdleStatus 的落地实现）
        /// </summary>
        public void ApplyIdleStatus()
        {
            Dispatcher.Invoke(() =>
            {
                TxtStatusMain.Text = "状态: 未开始";
                TxtStatusMain.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
            });
        }

        /// <summary>解压进度（ITaskObserver.OnExtractProgress 的落地实现）</summary>
        public void ApplyExtractProgress(int percent)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressExtract.Value = percent;
                TxtExtractProgress.Text = $"{percent}%";
            });
        }

        /// <summary>当前测试密码（ITaskObserver.OnCurrentPassword 的落地实现）</summary>
        public void ApplyCurrentPassword(string text)
        {
            Dispatcher.Invoke(() =>
            {
                TxtStatusCurrentPassword.Text = text;
            });
        }

        /// <summary>最终输出路径（ITaskObserver.OnFinalPath 的落地实现）</summary>
        public void ApplyFinalPath(string text)
        {
            Dispatcher.Invoke(() =>
            {
                TxtFinalPath.Text = text;
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
            _pipeline.StartDualQueueProcessing();
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 检查是否所有顶级文件项都已完成，如果是则触发批量智能路径处理
        /// </summary>
        public void CheckAndTriggerBatchSmartPathProcessing()
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
                var topLevelItem = _pipeline.FindFileItemInTreeThreadSafe(topArchivePath);
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
                var relatedItems = _pipeline.SnapshotTopLevelItems().Where(item => 
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








