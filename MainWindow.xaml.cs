using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AutoUnpackTool
{
    public partial class MainWindow : Window
    {
        private ObservableCollection<FileItem> _fileList = new ObservableCollection<FileItem>();
        private AppSettings _settings;
        private PasswordMap _passwordMap = new PasswordMap(); // 密码映射表
        
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
        
        // 任务完成信号
        private TaskCompletionSource<bool> _testCompletionSource = new();
        private TaskCompletionSource<bool> _extractCompletionSource = new();
        
        // 记录所有顶级 FileItem
        private List<FileItem> _topLevelItems = new();
        private readonly ConcurrentDictionary<string, bool> _stegoProbeCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> StegoCarrierExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".webm", ".mov", ".m4v"
        };

        public MainWindow()
        {
            InitializeComponent();
            LstFiles.ItemsSource = _fileList;
            
            // 加载设置
            _settings = AppSettings.Load();
            
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
                    
                    // 启动双队列处理流程
                    StartDualQueueProcessing();
                    
                    // 唤醒测试线程（如果已经在运行）
                    WakeupTestThread();
                }
                else
                {
                    AppendLog("未检测到压缩文件", ConsoleColor.Yellow);
                }
            }
        }

        /// <summary>
        /// 添加单个文件到列表
        /// </summary>
        private void AddFileToList(string filePath, ref int addedCount, List<FileItem>? newFileItems = null)
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
                FileSize = fileInfo.Length
            };
            _fileList.Add(fileItem);
            addedCount++;
            
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
            // 检查黑名单
            if (IsBlacklistedFile(filePath, out string matchedPattern))
            {
                DeleteBlacklistedFile(filePath, matchedPattern);
                return;  // 跳过后续处理
            }

            // 检测是否为压缩文件
            if (IsArchiveFile(filePath))
            {
                // 如果是分卷压缩包，自动检测所有分卷（仅在同级目录中查找分卷文件）
                if (_settings.EnableMultiVolumeDetection && IsMultiVolumeArchive(filePath))
                {
                    var volumeInfo = DetectMultiVolumeArchive(filePath);
                    
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
                // 获取文件夹中的所有文件（包括子文件夹）
                string[] files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories);
                
                AppendLog($"正在扫描文件夹: {directoryPath}", ConsoleColor.Gray);
                AppendLog($"找到 {files.Length} 个文件，正在筛选压缩文件...", ConsoleColor.Gray);
                
                // 首先收集所有可能的压缩文件
                var potentialArchives = new List<string>();
                foreach (string file in files)
                {
                    // 检查黑名单
                    if (IsBlacklistedFile(file, out string matchedPattern))
                    {
                        DeleteBlacklistedFile(file, matchedPattern);
                        continue;
                    }

                    // 初步筛选：通过扩展名或分卷格式判断
                    if (IsArchiveFileByExtension(file) || IsMultiVolumeArchive(file))
                    {
                        potentialArchives.Add(file);
                    }
                }
                
                AppendLog($"发现 {potentialArchives.Count} 个潜在压缩文件，正在进行魔术数验证...", ConsoleColor.Gray);
                
                // 对潜在压缩文件进行魔术数验证
                var confirmedArchives = new List<string>();
                foreach (string file in potentialArchives)
                {
                    if (IsArchiveFileByMagicNumber(file))
                    {
                        confirmedArchives.Add(file);
                        AppendLog($"  [确认] {Path.GetFileName(file)}: 是压缩文件", ConsoleColor.Green);
                    }
                    else
                    {
                        AppendLog($"  [拒绝] {Path.GetFileName(file)}: 不是压缩文件", ConsoleColor.Yellow);
                    }
                }
                
                AppendLog($"确认 {confirmedArchives.Count} 个压缩文件，正在处理分卷合并...", ConsoleColor.Gray);
                
                // 处理分卷压缩：将同一组分卷合并为一个条目
                var processedVolumes = new HashSet<string>(); // 已处理的分卷文件
                
                // 首先按目录分组，以便更好地检测分卷
                var archivesByDirectory = confirmedArchives.GroupBy(f => Path.GetDirectoryName(f) ?? string.Empty);
                
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
                        if (_settings.EnableMultiVolumeDetection && IsMultiVolumeArchive(archiveFile))
                        {
                            var volumeInfo = DetectMultiVolumeArchive(archiveFile);
                            
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
                                    FileSize = volumeInfo.AllVolumePaths.Sum(p => new FileInfo(p).Length)
                                };
                                
                                // 存储分卷信息
                                fileItem.VolumeInfo = volumeInfo;
                                
                                if (!_fileList.Any(f => f.FilePath == volumeInfo.MainVolumePath))
                                {
                                    _fileList.Add(fileItem);
                                    addedCount++;
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
                                    AddFileToList(archiveFile, ref addedCount, newFileItems);
                                    processedVolumes.Add(archiveFile);
                                }
                            }
                        }
                        else
                        {
                            // 普通压缩文件
                            if (!processedVolumes.Contains(archiveFile))
                            {
                                AddFileToList(archiveFile, ref addedCount);
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

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            // 1. 取消所有正在运行的操作
            CancelAllOperations();
            
            // 2. 等待一小段时间让线程退出
            Task.Delay(500).Wait();
            
            // 3. Kill掉所有7z进程
            KillSevenZipProcesses();
            
            // 4. Kill掉所有NanaZip进程
            KillNanaZipProcesses();
            
            // 5. 清空UI和数据
            _fileList.Clear();
            _passwordMap = new PasswordMap();
            _pendingQueue = new ConcurrentQueue<FileItem>();
            _extractQueue = new ConcurrentQueue<FileItem>();
            _topLevelItems.Clear();
            TxtLog.Clear();
            
            // 6. 重置状态
            _isTesting = false;
            _isExtracting = false;
            
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
        /// Kill掉所有7z进程
        /// </summary>
        private void KillSevenZipProcesses()
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("7z");
                if (processes.Length > 0)
                {
                    AppendLog($"发现 {processes.Length} 个7z进程，正在终止...", ConsoleColor.Yellow);
                    foreach (var process in processes)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill();
                                process.WaitForExit(1000); // 等待1秒让进程退出
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"终止7z进程失败: {ex.Message}", ConsoleColor.Red);
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                    AppendLog("所有7z进程已终止", ConsoleColor.Green);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"查找7z进程失败: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 暂停/停止按钮点击事件
        /// </summary>
        private void BtnPauseStop_Click(object sender, RoutedEventArgs e)
        {
            // 如果正在测试密码，取消测试
            if (_isTesting && _testCancellationTokenSource != null)
            {
                AppendLog("\n正在暂停密码测试...", ConsoleColor.Yellow);
                
                // 立即更新UI状态，不需要等待线程退出
                _isTesting = false;
                
                // 取消并清理token source，防止启动新线程时冲突
                var oldTokenSource = _testCancellationTokenSource;
                _testCancellationTokenSource = null;
                oldTokenSource.Cancel();
                oldTokenSource.Dispose();
                
                UpdateUiState();
                AppendLog("测试已暂停。点击'开始解压'可继续处理", ConsoleColor.Cyan);
                return;
            }

            // 如果正在解压，取消解压
            if (_isExtracting && _extractCancellationTokenSource != null)
            {
                AppendLog("\n正在暂停解压操作...", ConsoleColor.Yellow);
                
                // 立即更新UI状态，不需要等待线程退出
                _isExtracting = false;
                
                // 取消并清理token source，防止启动新线程时冲突
                var oldTokenSource = _extractCancellationTokenSource;
                _extractCancellationTokenSource = null;
                oldTokenSource.Cancel();
                oldTokenSource.Dispose();
                
                UpdateUiState();
                AppendLog("解压已暂停。点击'开始解压'可继续处理", ConsoleColor.Cyan);
                return;
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
                
                // 1. 取消所有正在运行的操作
                CancelAllOperations();
                
                // 2. 等待一小段时间让线程退出
                Task.Delay(500).Wait();
                
                // 3. Kill掉所有7z进程（包括 NanaZip）
                KillSevenZipProcesses();
                KillNanaZipProcesses();
                
                AppendLog("程序已关闭", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                // 即使出错也允许关闭
                AppendLog($"关闭时发生错误: {ex.Message}", ConsoleColor.Red);
            }
        }
        
        /// <summary>
        /// Kill掉所有 NanaZip 进程
        /// </summary>
        private void KillNanaZipProcesses()
        {
            try
            {
                // NanaZip 的进程名可能不同，尝试查找
                var processNames = new[] { "NanaZip", "NanaZipC", "NanaZip.Universal.Console", "7zG", "NanaZipG" };
                
                foreach (var processName in processNames)
                {
                    var processes = System.Diagnostics.Process.GetProcessesByName(processName);
                    if (processes.Length > 0)
                    {
                        AppendLog($"发现 {processes.Length} 个 {processName} 进程，正在终止...", ConsoleColor.Yellow);
                        foreach (var process in processes)
                        {
                            try
                            {
                                if (!process.HasExited)
                                {
                                    process.Kill();
                                    process.WaitForExit(1000); // 等待1秒让进程退出
                                }
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"终止 {processName} 进程失败: {ex.Message}", ConsoleColor.Red);
                            }
                            finally
                            {
                                process.Dispose();
                            }
                        }
                    }
                }
                
                AppendLog("所有压缩软件进程已终止", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"查找压缩软件进程失败: {ex.Message}", ConsoleColor.Red);
            }
        }

        #endregion

        #region 核心流程

        /// <summary>
        /// 启动双队列处理流程：测试线程 + 解压线程
        /// </summary>
        private void StartDualQueueProcessing()
        {
            if (_isTesting || _isExtracting)
            {
                AppendLog("处理流程已在运行中...", ConsoleColor.Yellow);
                return;
            }

            // 重置完成信号
            _testCompletionSource = new TaskCompletionSource<bool>();
            _extractCompletionSource = new TaskCompletionSource<bool>();
            
            // 重置队列信号
            _pendingQueueSignal.Reset();
            _extractQueueSignal.Reset();

            // 启动测试线程
            StartTestThread();
            
            // 启动解压线程
            StartExtractThread();
            
            // 监听完成事件
            MonitorProcessingComplete();
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
            if (_isTesting)
                return;

            _isTesting = true;
            var tokenSource = new CancellationTokenSource();
            _testCancellationTokenSource = tokenSource;
            var token = tokenSource.Token;

            try
            {
                UpdateUiState();
                AppendLog($"\n========== 测试线程启动 ==========", ConsoleColor.Cyan);

                var extractor = new SevenZipExtractor(_settings.SevenZipPath);
                var passwords = _settings.GetAllPasswords();

                AppendLog($"加载密码本: 共 {passwords.Count} 个密码", ConsoleColor.Cyan);

                while (!token.IsCancellationRequested)
                {
                    if (_pendingQueue.TryDequeue(out var fileItem))
                    {
                        try
                        {
                            await ProcessPendingFile(fileItem, extractor, passwords, token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() => fileItem.Status = $"测试异常: {ex.Message}");
                            AppendLog($"[{fileItem.FileName}] 测试异常: {ex.Message}", ConsoleColor.Red);
                        }
                    }
                    else
                    {
                        // 队列为空，等待信号或超时
                        AppendLog("[测试线程] 队列为空，等待新任务...", ConsoleColor.Gray);
                        
                        // 等待信号（最多等待30秒）或取消请求
                        bool signaled = false;
                        try
                        {
                            signaled = await Task.Run(() => 
                                _pendingQueueSignal.Wait(TimeSpan.FromSeconds(30), token), token);
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
                AppendLog($"\n测试线程出错: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                // 只有当前tokenSource仍是自己创建的时才清理（暂停时会置null，新线程会创建新的）
                if (_testCancellationTokenSource == tokenSource)
                {
                    _isTesting = false;
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
            Dispatcher.Invoke(() => fileItem.Status = "正在测试密码...");

            // 步骤1：判断是否是压缩文件
            if (!IsArchiveFile(fileItem.FilePath))
            {
                Dispatcher.Invoke(() => fileItem.Status = "非压缩文件，跳过");
                
                // 检查父项完成（传入子节点）
                if (fileItem.Parent != null)
                {
                    UpdateParentStatusWhenChildrenComplete(fileItem);
                }
                return;
            }

            string? foundPassword = null;
            bool stegoMode = IsStegoDetectionActiveForFile(fileItem.FilePath);

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
                AppendLog($"[{fileItem.FileName}] 识别为隐写容器，已加入待解压队列（-t#）", ConsoleColor.Cyan);
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

            bool noPasswordSuccess = await extractor.TestPasswordAsync(
                fileItem.FilePath, 
                "", 
                onOutput: (msg) => { },
                cancellationToken: token);

            if (noPasswordSuccess)
            {
                foundPassword = null;
            }
            else
            {
                // 步骤3：测试密码本
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
                        _settings.RecordPasswordUsage(password);
                        break;
                    }

                    await Task.Delay(50, token);
                }
            }

            // 记录到密码映射表
            _passwordMap.Add(fileItem.FilePath, foundPassword);

            // 更新UI
            Dispatcher.Invoke(() =>
            {
                fileItem.FoundPassword = foundPassword;
                TxtStatusCurrentPassword.Text = "测试中的密码: 无";
                
                if (foundPassword != null)
                {
                    fileItem.Status = $"密码正确: {foundPassword}";
                }
                else
                {
                    fileItem.Status = "无密码";
                }
            });

            // 步骤4：将文件加入待解压队列
            _extractQueue.Enqueue(fileItem);
            AppendLog($"[{fileItem.FileName}] 已加入待解压队列", ConsoleColor.Gray);
            
            // 唤醒解压线程
            WakeupExtractThread();

            await Task.Delay(100, token);
        }

        /// <summary>
        /// 启动解压线程：从待解压队列取文件，解压后扫描新文件加入待处理队列
        /// </summary>
        private async void StartExtractThread()
        {
            if (_isExtracting)
                return;

            _isExtracting = true;
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
                                
                                // 等待信号（最多等待30秒）或取消请求
                                bool signaled = false;
                                try
                                {
                                    signaled = await Task.Run(() => 
                                        _extractQueueSignal.Wait(TimeSpan.FromSeconds(30), token), token);
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
                AppendLog($"\n解压线程出错: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                // 只有当前tokenSource仍是自己创建的时才清理（暂停时会置null，新线程会创建新的）
                if (_extractCancellationTokenSource == tokenSource)
                {
                    _isExtracting = false;
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
                    var completedTask = await Task.WhenAny(
                        Task.WhenAll(testTask, extractTask),
                        Task.Delay(5000) // 5秒超时
                    );
                    
                    // 如果是因为超时而非任务完成
                    if (completedTask != testTask && completedTask != extractTask)
                    {
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
                    AppendLog($"\n========== 所有处理流程完成 ==========", ConsoleColor.Green);
                }
            }
            catch (Exception ex)
            {
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
        /// 检查并标记项为完成（如果所有子项都已完成）
        /// 对于顶级文件，如果解压完成且所有子项也完成，则从列表移除
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
                // 没有子项，如果是顶级文件，从列表移除
                if (fileItem.Parent == null)
                {
                    // 只有解压成功时才处理原文件
                    bool extractSuccess = fileItem.GetSelfExtractResult();
                    
                    if (extractSuccess)
                    {
                        // 处理原文件（移动到回收站/删除等）
                        // 由于这个方法被多处调用，且都是同步调用，我们使用 fire-and-forget 模式
                        _ = HandleOriginalFileAsync(fileItem.FilePath);
                    }
                    else
                    {
                        AppendLog($"[{fileItem.FileName}] 解压失败，保留原文件", ConsoleColor.Yellow);
                    }
                    
                    Dispatcher.Invoke(() =>
                    {
                        if (_fileList.Contains(fileItem))
                        {
                            _fileList.Remove(fileItem);
                            AppendLog($"[{fileItem.FileName}] 无子压缩包，已从待处理列表移除", ConsoleColor.Gray);
                        }
                    });
                }
            }
        }

        /// <summary>
        /// 异步处理原文件（fire-and-forget 模式）
        /// </summary>
        private async Task HandleOriginalFileAsync(string filePath)
        {
            try
            {
                if (_settings.FileAfterExtract != FileAction.Keep)
                {
                    await HandleOriginalFile(filePath);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"处理原文件时出错: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 阶段1：清理树中所有子压缩包（非顶级节点的原文件）
        /// </summary>
        private async Task CleanupChildArchivesInTree()
        {
            var allItems = CollectAllFileItems(_fileList);
            foreach (var item in allItems)
            {
                if (item.Parent == null)
                    continue; // 跳过顶级节点，顶级由阶段3单独处理
                try
                {
                    if (_settings.KeepChildArchives)
                        await HandleOriginalFileAsync(item.FilePath);
                    else
                        await HandleOriginalFile(item.FilePath);
                }
                catch (Exception ex)
                {
                    AppendLog($"  清理子压缩包失败 [{item.FileName}]: {ex.Message}", ConsoleColor.Red);
                }
            }
        }

        /// <summary>
        /// 顶级解压链完成时，分三个阶段顺序执行：
        /// 阶段1：清理所有子压缩包（确保目录干净后再扁平化）
        /// 阶段2：批量智能路径处理（扁平化）
        /// 阶段3：清理顶级原压缩包（含分卷）+ 遗留空目录
        /// </summary>
        private async Task FinalizeTopLevelExtractAndSmartPathAsync(string topArchivePath)
        {
            // 阶段1：清理所有子压缩包
            try
            {
                await CleanupChildArchivesInTree();
            }
            catch (Exception ex)
            {
                AppendLog($"[阶段1] 清理子压缩包失败: {ex.Message}", ConsoleColor.Red);
            }

            // 阶段2：批量智能路径处理（扁平化）
            try
            {
                await Dispatcher.InvokeAsync(() => CheckAndTriggerBatchSmartPathProcessing());
            }
            catch (Exception ex)
            {
                AppendLog($"[阶段2] 批量智能路径失败: {ex.Message}", ConsoleColor.Red);
            }

            // 阶段3：清理顶级原压缩包（含分卷）+ 遗留空目录
            try
            {
                if (_settings.FileAfterExtract != FileAction.Keep)
                    await HandleOriginalFile(topArchivePath);
            }
            catch (Exception ex)
            {
                AppendLog($"[阶段3] 处理原文件时出错: {ex.Message}", ConsoleColor.Red);
            }

            try
            {
                string outputDir = GetOutputDirectory(topArchivePath);
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
            
            string outputDir = GetOutputDirectory(fileItem.FilePath);
            AppendLog($"\n[线程 {taskId}] 解压: {fileItem.FileName}", ConsoleColor.White);
            AppendLog($"  输出目录: {outputDir}", ConsoleColor.Gray);
            AppendLog($"  密码: {password ?? "(无密码)"}", ConsoleColor.Gray);

            try
            {
                var result = await extractor.ExtractAsync(
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
                    useHashTypeMode: IsStegoDetectionActiveForFile(fileItem.FilePath));

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

                    // 从密码映射表中移除已完成处理的文件记录
                    string filePath = fileItem.FilePath;
                    _passwordMap.Remove(filePath);
                    AppendLog($"[线程 {taskId}] {fileItem.FileName}: 已从密码映射表清除", ConsoleColor.Gray);

                    // 先检查解压后的文件是否包含新的压缩文件
                    // 这会添加子项到 fileItem.Children，并将新文件加入待处理队列
                    await ScanExtractedFilesForArchives(outputDir, taskId, fileItem);
                    
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
                        AppendLog($"[{fileItem.FileName}] 已添加 {fileItem.Children.Count} 个子压缩包到待处理队列，等待递归处理...", ConsoleColor.Cyan);
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
                    
                    Dispatcher.Invoke(() => fileItem.Status = $"解压失败: {result.Message}");
                    AppendLog($"[线程 {taskId}] {fileItem.FileName}: 解压失败 - {result.Message}", ConsoleColor.Red);
                    
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
                AppendLog($"[线程 {taskId}] {fileItem.FileName}: 已取消", ConsoleColor.Yellow);
                
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
                AppendLog($"[线程 {taskId}] {fileItem.FileName}: 异常 - {ex.Message}", ConsoleColor.Red);
                
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
            AppendLog($"[线程 {taskId}] [DEBUG] 无子压缩包，处理原文件并更新父项状态: {leafItem.FileName}{suffix}", ConsoleColor.Magenta);

            var passwordInfo = leafItem.FoundPassword != null
                ? $" (密码: {leafItem.FoundPassword})"
                : " (无密码)";
            Dispatcher.Invoke(() =>
            {
                leafItem.Status = $"解压成功{passwordInfo}";
            });
            AppendLog($"[{leafItem.FileName}] 叶子节点解压完成: {leafItem.Status}", ConsoleColor.Green);

            if (leafItem.Parent == null)
            {
                // 顶级叶子节点：走统一收尾流程（先智能路径扁平化，再清理原压缩包）
                AppendLog($"[{leafItem.FileName}] 顶级叶子节点解压完成，触发智能路径处理...", ConsoleColor.Cyan);
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
                AppendLog($"[线程 {taskId}] [DEBUG] 开始扫描解压文件: {outputDir}", ConsoleColor.Magenta);
                
                if (!Directory.Exists(outputDir))
                {
                    AppendLog($"[线程 {taskId}] [DEBUG] 输出目录不存在: {outputDir}", ConsoleColor.Magenta);
                    return;
                }

                // 递归查找输出目录中解压出来的新压缩文件
                // 只扫描压缩包扩展名和分卷压缩格式，不扫描其他文件
                var archiveExtensions = _settings.GetArchiveExtensions();
                var allArchiveFiles = new List<string>();
                
                // 1. 扫描具有压缩文件扩展名的文件
                foreach (var ext in archiveExtensions)
                {
                    // ext 可能是 ".rar", ".zip" 等格式
                    string searchPattern = ext.StartsWith(".") ? $"*{ext}" : $"*.{ext}";
                    try
                    {
                        var files = Directory.GetFiles(outputDir, searchPattern, SearchOption.AllDirectories);
                        allArchiveFiles.AddRange(files);
                    }
                    catch
                    {
                        // 忽略访问权限等问题
                    }
                }
                
                // 2. 对所有其他文件使用魔术数检测（不依赖扩展名）
                var allFiles = Directory.GetFiles(outputDir, "*.*", SearchOption.AllDirectories);
                foreach (var file in allFiles)
                {
                    // 跳过已通过扩展名匹配的文件
                    if (allArchiveFiles.Contains(file))
                        continue;
                        
                    // 使用魔术数检测是否为压缩包
                    if (IsArchiveFile(file))
                    {
                        allArchiveFiles.Add(file);
                    }
                }
                
                // 去重
                allArchiveFiles = allArchiveFiles.Distinct().ToList();

                // 过滤掉解压前已存在的文件，只保留新解压的文件
                if (existingFilesBeforeExtract != null && existingFilesBeforeExtract.Count > 0)
                {
                    var newlyExtractedFiles = allArchiveFiles.Where(f => !existingFilesBeforeExtract.Contains(f)).ToList();
                    AppendLog($"[线程 {taskId}] [DEBUG] 过滤前: {allArchiveFiles.Count} 个, 过滤后: {newlyExtractedFiles.Count} 个新文件", ConsoleColor.Magenta);
                    allArchiveFiles = newlyExtractedFiles;
                }

                AppendLog($"[线程 {taskId}] [DEBUG] 找到 {allArchiveFiles.Count} 个候选压缩文件", ConsoleColor.Magenta);

                if (allArchiveFiles.Count == 0)
                {
                    AppendLog($"[线程 {taskId}] 未发现新的压缩文件", ConsoleColor.Gray);
                    if (parentFileItem != null)
                        FinishLeafAfterScanNoChildArchivesToQueue(parentFileItem, taskId);
                    return;
                }

                AppendLog($"[线程 {taskId}] 发现 {allArchiveFiles.Count} 个新的压缩文件", ConsoleColor.Cyan);

                int addedCount = 0;
                foreach (var archiveFile in allArchiveFiles)
                {
                    // 检查黑名单 - 如果是黑名单文件则直接删除，不加入处理队列
                    if (IsBlacklistedFile(archiveFile, out string matchedPattern))
                    {
                        try
                        {
                            File.Delete(archiveFile);
                            AppendLog($"[线程 {taskId}] [黑名单] 已删除: {Path.GetFileName(archiveFile)} (匹配模式: {matchedPattern})", ConsoleColor.Yellow);
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
                    if (_settings.EnableMultiVolumeDetection && IsMultiVolumeArchive(archiveFile))
                    {
                        var volumeInfo = DetectMultiVolumeArchive(archiveFile);
                        
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
                                VolumeInfo = volumeInfo  // 设置分卷信息！
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
                        TaskId = taskId
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
            bool allCompleted = parentItem.MarkChildComplete();
            
            AppendLog($"[{parentItem.FileName}] 子节点完成进度: {parentItem.GetProgressInfo()}", ConsoleColor.Gray);

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
                        Dispatcher.Invoke(() => CheckAndTriggerBatchSmartPathProcessing());
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
                
                // 开始解压按钮：
                // - 自动模式：始终启用（兼任"继续"功能，点击后由 BtnStartExtract_Click 判断具体操作）
                // - 手动模式：仅在不处理时启用
                if (isAutoMode)
                {
                    BtnStartExtract.IsEnabled = true;
                }
                else
                {
                    BtnStartExtract.IsEnabled = !isProcessing;
                }

                // 暂停/停止按钮：只在处理中时启用
                BtnPauseStop.IsEnabled = isProcessing;

                TxtStatusTestThread.Text = $"测试线程: {(_isTesting ? "工作中" : "空闲")}";
                TxtStatusExtractThread.Text = $"解压线程: {(_isExtracting ? "工作中" : "空闲")}";
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
        /// 批量智能路径处理：当整个任务的所有文件节点处理完成后执行
        /// 执行时机：任务级别的所有解压完成后统一处理
        /// </summary>
        private async Task BatchProcessSmartPathForTask(int taskId)
        {
            if (!_settings.EnableSmartPathProcessing || _settings.OutputMode != OutputMode.ArchiveFolder)
                return;

            AppendLog($"[任务 {taskId}] 开始批量智能路径处理...", ConsoleColor.Cyan);

            // 收集该任务涉及的所有解压生成的文件夹
            var allItems = CollectAllFileItems(_fileList);
            var taskExtractedFolders = new HashSet<string>();

            foreach (var item in allItems)
            {
                // 收集该任务的所有文件项（包括子压缩包）的解压文件夹
                if (item.TaskId == taskId)
                {
                    string extractedFolder = GetOutputDirectory(item.FilePath);
                    if (!string.IsNullOrEmpty(extractedFolder) && Directory.Exists(extractedFolder))
                    {
                        taskExtractedFolders.Add(extractedFolder);
                    }
                }
            }

            // 对每个解压生成的文件夹执行批量扁平化
            foreach (var extractedFolder in taskExtractedFolders)
            {
                await Task.Run(() => ProcessSmartPathBatch(extractedFolder));
            }

            AppendLog($"[任务 {taskId}] ✓ 批量智能路径处理完成", ConsoleColor.Green);
        }

        /// <summary>
        /// 检查是否所有顶级文件项都已完成，如果是则触发批量智能路径处理
        /// </summary>
        private void CheckAndTriggerBatchSmartPathProcessing()
        {
            // 添加调试日志
            AppendLog($"[批量智能路径] 检查触发条件: EnableSmartPathProcessing={_settings.EnableSmartPathProcessing}, OutputMode={_settings.OutputMode}", ConsoleColor.Gray);
            
            if (!_settings.EnableSmartPathProcessing || _settings.OutputMode != OutputMode.ArchiveFolder)
            {
                AppendLog($"[批量智能路径] 条件不满足，跳过处理", ConsoleColor.Gray);
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
                    string extractedFolder = GetOutputDirectory(item.FilePath);
                    AppendLog($"[批量智能路径] 顶级项 {item.FileName} 的解压文件夹: {extractedFolder}", ConsoleColor.Gray);
                    
                    if (!string.IsNullOrEmpty(extractedFolder) && Directory.Exists(extractedFolder))
                    {
                        extractedFolders.Add(extractedFolder);
                    }
                }

                AppendLog($"[批量智能路径] 找到 {extractedFolders.Count} 个解压文件夹待处理", ConsoleColor.Cyan);

                // 对每个解压生成的文件夹执行批量扁平化（fire-and-forget）
                foreach (var extractedFolder in extractedFolders)
                {
                    _ = Task.Run(() => ProcessSmartPathBatch(extractedFolder));
                }
            }
        }

        /// <summary>
        /// 在智能路径扁平化之前，对解压目录递归删除黑名单匹配的文件（含解压残留的压缩包）。
        /// 否则同级黑名单文件会导致「恰好一个子目录且无文件」条件不满足而无法扁平化。
        /// </summary>
        private void DeleteBlacklistedFilesUnderDirectory(string rootDir)
        {
            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
                return;

            var patterns = _settings.GetBlacklistPatterns();
            if (patterns == null || patterns.Count == 0)
                return;

            try
            {
                AppendLog($"[批量智能路径] 扁平化前黑名单清理: {rootDir}", ConsoleColor.Gray);
                foreach (string file in Directory.GetFiles(rootDir, "*.*", SearchOption.AllDirectories))
                {
                    if (!File.Exists(file))
                        continue;
                    if (IsBlacklistedFile(file, out string matchedPattern))
                        DeleteBlacklistedFile(file, matchedPattern);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[批量智能路径] 扁平化前黑名单扫描失败: {ex.Message}", ConsoleColor.Yellow);
            }
        }

        /// <summary>
        /// 在智能路径扁平化之前，清理所有子压缩包文件（如果 KeepChildArchives = false）。
        /// 这是阶段1清理的补充，确保扫描目录中所有子压缩包并清理，而不仅仅依赖文件树记录。
        /// </summary>
        private void DeleteChildArchivesUnderDirectory(string rootDir)
        {
            if (_settings.KeepChildArchives)
                return; // 如果配置保留子压缩包，则不清理

            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
                return;

            try
            {
                AppendLog($"[批量智能路径] 扁平化前子压缩包清理: {rootDir}", ConsoleColor.Gray);
                int cleanedCount = 0;
                foreach (string file in Directory.GetFiles(rootDir, "*.*", SearchOption.AllDirectories))
                {
                    if (!File.Exists(file))
                        continue;
                    
                    // 检查是否是压缩文件
                    if (IsArchiveFile(file) && !IsMultiVolumeArchive(file))
                    {
                        try
                        {
                            File.Delete(file);
                            AppendLog($"[批量智能路径]   已清理子压缩包: {Path.GetFileName(file)}", ConsoleColor.Gray);
                            cleanedCount++;
                        }
                        catch (Exception delEx)
                        {
                            AppendLog($"[批量智能路径]   清理子压缩包失败 [{Path.GetFileName(file)}]: {delEx.Message}", ConsoleColor.Yellow);
                        }
                    }
                }
                if (cleanedCount > 0)
                    AppendLog($"[批量智能路径]   共清理 {cleanedCount} 个子压缩包", ConsoleColor.Gray);
            }
            catch (Exception ex)
            {
                AppendLog($"[批量智能路径] 扁平化前子压缩包扫描失败: {ex.Message}", ConsoleColor.Yellow);
            }
        }

        /// <summary>
        /// 批量处理单个输出目录的智能路径扁平化（从叶子节点向上遍历）
        /// </summary>
        private void ProcessSmartPathBatch(string rootDir)
        {
            if (!Directory.Exists(rootDir))
                return;

            AppendLog($"[批量智能路径] 开始处理: {rootDir}", ConsoleColor.Cyan);

            // 步骤1: 清理黑名单文件
            DeleteBlacklistedFilesUnderDirectory(rootDir);

            // 步骤2: 清理所有子压缩包（如果 KeepChildArchives = false）
            DeleteChildArchivesUnderDirectory(rootDir);

            // 步骤3: 后序遍历：先处理深层嵌套，再处理浅层
            ProcessDirectoryTreePostOrder(rootDir);

            AppendLog($"[批量智能路径] ✓ 处理完成: {Path.GetFileName(rootDir)}", ConsoleColor.Green);
        }

        /// <summary>
        /// 后序遍历目录树，从叶子节点开始处理
        /// 注意：只处理压缩包解压出的文件夹结构，不处理非压缩包（如游戏应用）的原始结构
        /// </summary>
        private void ProcessDirectoryTreePostOrder(string dirPath)
        {
            try
            {
                // 检查目录是否存在（可能在之前的扁平化操作中被删除）
                if (!Directory.Exists(dirPath))
                {
                    AppendLog($"[批量智能路径] 目录已不存在，跳过: {dirPath}", ConsoleColor.Gray);
                    return;
                }

                // 检查当前目录是否是压缩包解压出来的
                // 如果不是（即原始文件结构），则不进行扁平化
                if (!IsExtractedArchiveFolder(dirPath))
                {
                    AppendLog($"[批量智能路径] [DEBUG] {Path.GetFileName(dirPath)}: 非压缩包解压文件夹，跳过扁平化", ConsoleColor.Gray);
                    return;
                }

                AppendLog($"[批量智能路径] [DEBUG] 开始遍历目录: {dirPath}", ConsoleColor.Gray);

                // 1. 先递归处理所有子目录（深度优先）
                var subDirs = Directory.GetDirectories(dirPath);
                AppendLog($"[批量智能路径] [DEBUG] 找到 {subDirs.Length} 个子目录", ConsoleColor.Gray);
                
                foreach (var subDir in subDirs)
                {
                    AppendLog($"[批量智能路径] [DEBUG] 递归处理子目录: {Path.GetFileName(subDir)}", ConsoleColor.Gray);
                    // 对每个子目录的遍历进行独立异常处理
                    try
                    {
                        ProcessDirectoryTreePostOrder(subDir);
                    }
                    catch (Exception subEx)
                    {
                        // 单个子目录处理失败不影响其他子目录
                        AppendLog($"[批量智能路径] 处理子目录失败 {subDir}: {subEx.Message}", ConsoleColor.Yellow);
                    }
                }

                // 2. 处理当前目录（此时子目录已经处理完毕）
                // 再次检查目录是否存在（可能子目录处理时被删除）
                if (Directory.Exists(dirPath))
                {
                    AppendLog($"[批量智能路径] [DEBUG] 开始处理当前目录: {Path.GetFileName(dirPath)}", ConsoleColor.Gray);
                    ProcessSingleDirectoryIfNeeded(dirPath);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[批量智能路径] 遍历目录失败 {dirPath}: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 判断目录是否是压缩包解压出来的
        /// 通过检查目录下是否有压缩包解压的典型特征来判断
        /// </summary>
        private bool IsExtractedArchiveFolder(string dirPath)
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
                    string fileExt = Path.GetExtension(file).ToLowerInvariant();
                    
                    // 检查是否是分卷压缩包（.001, .002, .part1.rar 等）
                    bool isVolumeArchive = IsMultiVolumeArchive(file);
                    
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
                    if (IsArchiveFile(file))
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
        private void ProcessSingleDirectoryIfNeeded(string dirPath)
        {
            try
            {
                var subDirs = Directory.GetDirectories(dirPath);
                var allFiles = Directory.GetFiles(dirPath);
                // 忽略压缩包文件（含分卷），这些残留文件将在后续 HandleOriginalFile 中统一清理
                var nonArchiveFiles = allFiles.Where(f => !IsArchiveFile(f)).ToArray();

                AppendLog($"[批量智能路径] [DEBUG] {Path.GetFileName(dirPath)}: {subDirs.Length} 个子目录, {allFiles.Length} 个文件 (非压缩包: {nonArchiveFiles.Length})", ConsoleColor.Gray);

                // 触发条件：恰好一个子目录且没有非压缩包文件
                if (subDirs.Length != 1 || nonArchiveFiles.Length > 0)
                {
                    if (subDirs.Length != 1)
                        AppendLog($"[批量智能路径] [DEBUG] {Path.GetFileName(dirPath)}: 子目录数量不为1 ({subDirs.Length})，跳过扁平化", ConsoleColor.Gray);
                    if (nonArchiveFiles.Length > 0)
                        AppendLog($"[批量智能路径] [DEBUG] {Path.GetFileName(dirPath)}: 存在 {nonArchiveFiles.Length} 个非压缩包文件，跳过扁平化", ConsoleColor.Gray);
                    return;
                }

                string singleSubDir = subDirs[0];
                string subDirName = Path.GetFileName(singleSubDir);
                string parentDirName = Path.GetFileName(dirPath);

                AppendLog($"[批量智能路径] 检测到可扁平化: {parentDirName} -> {subDirName}", ConsoleColor.Gray);

                // 根据配置决定最终名称
                string finalFolderName = DetermineFolderName(parentDirName, subDirName);
                AppendLog($"[批量智能路径] [DEBUG] 最终文件夹名: {finalFolderName}", ConsoleColor.Gray);

                // 执行扁平化：将子目录内容提升到父目录
                FlattenDirectory(dirPath, singleSubDir, finalFolderName);
            }
            catch (Exception ex)
            {
                AppendLog($"[批量智能路径] 处理目录失败 {dirPath}: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 去掉目录名末尾的压缩包后缀（支持复合后缀，如 .tar.zst）
        /// </summary>
        private string NormalizeArchiveLikeName(string name)
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
        /// 根据配置模式决定最终的文件夹名称
        /// </summary>
        private string DetermineFolderName(string parentName, string childName)
        {
            string normalizedParentName = NormalizeArchiveLikeName(parentName);
            string normalizedChildName = NormalizeArchiveLikeName(childName);

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
        private void FlattenDirectory(string parentDir, string childDir, string finalName)
        {
            try
            {
                string? parentDirPath = Path.GetDirectoryName(parentDir);
                if (string.IsNullOrEmpty(parentDirPath))
                    return;

                // 步骤1: 移动子目录到临时位置（避免冲突）
                string tempName = $"{Guid.NewGuid().ToString().Substring(0, 8)}_{finalName}";
                string tempPath = Path.Combine(parentDirPath, tempName);

                Directory.Move(childDir, tempPath);
                AppendLog($"[批量智能路径]   已移动: {Path.GetFileName(childDir)} -> {tempName}", ConsoleColor.Gray);

                // 步骤2: 删除空的父目录（可能因残留压缩包而失败，不阻断流程）
                if (Directory.Exists(parentDir))
                {
                    try
                    {
                        Directory.Delete(parentDir);
                        AppendLog($"[批量智能路径]   已删除空目录: {Path.GetFileName(parentDir)}", ConsoleColor.Gray);
                    }
                    catch (Exception deleteEx)
                    {
                        // 目录非空（分卷等残留文件），打印内容物便于排查
                        AppendLog($"[批量智能路径] ⚠ 父目录未空，跳过删除: {deleteEx.Message}", ConsoleColor.Yellow);
                        try
                        {
                            foreach (var entry in Directory.EnumerateFileSystemEntries(parentDir))
                            {
                                string entryName = Path.GetFileName(entry);
                                bool isDir = Directory.Exists(entry);
                                AppendLog($"[批量智能路径]   残留: {(isDir ? "[DIR]" : "[FILE]")} {entryName}", ConsoleColor.Yellow);
                            }
                        }
                        catch { }
                    }
                }

                // 步骤3: 重命名为最终名称
                string finalPath = Path.Combine(parentDirPath, finalName);
                
                if (Directory.Exists(finalPath))
                {
                    Directory.Delete(finalPath, true);
                    AppendLog($"[批量智能路径]   已删除已存在的目录: {finalName}", ConsoleColor.Gray);
                }

                Directory.Move(tempPath, finalPath);
                AppendLog($"[批量智能路径]   ✓ 扁平化完成: {finalName}", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"[批量智能路径] 扁平化失败: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 智能路径处理：扁平化多层嵌套文件夹（旧版本，已废弃）
        /// 执行时机：每次解压完成后立即处理
        /// </summary>
        /// <param name="extractedFolder">7z 实际解压到的目录</param>
        private void ProcessSmartPath(string extractedFolder)
        {
            // 等待一下确保文件操作完成
            Thread.Sleep(500);
            
            AppendLog($"[智能路径] 开始处理: {extractedFolder}", ConsoleColor.Cyan);
            
            if (!Directory.Exists(extractedFolder)) 
            {
                AppendLog($"[智能路径] 目录不存在", ConsoleColor.Yellow);
                return;
            }

            // 循环处理多层嵌套，直到没有符合条件的嵌套为止
            int maxIterations = 10; // 防止无限循环
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                if (!Directory.Exists(extractedFolder))
                {
                    AppendLog($"[智能路径] 目录不存在，停止处理", ConsoleColor.Gray);
                    break;
                }

                if (!ProcessSmartPathOnce(ref extractedFolder))
                {
                    break; // 没有可处理的嵌套了
                }
            }
            
            AppendLog($"[智能路径] ✓ 智能路径处理完成", ConsoleColor.Green);
        }

        /// <summary>
        /// 执行一次智能路径扁平化处理
        /// </summary>
        /// <param name="extractedFolder">当前处理的目录（可能被修改）</param>
        /// <returns>是否成功执行了扁平化（true=有处理，false=没有可处理的）</returns>
        private bool ProcessSmartPathOnce(ref string extractedFolder)
        {
            try
            {
                // 检查当前目录下是否只有一个子文件夹
                var subDirs = Directory.GetDirectories(extractedFolder);
                var files = Directory.GetFiles(extractedFolder);
                
                AppendLog($"[智能路径] 当前目录: {Path.GetFileName(extractedFolder)} - 子文件夹数: {subDirs.Length}, 文件数: {files.Length}", ConsoleColor.Gray);

                // 如果当前目录包含文件，或者不包含恰好一个子文件夹，则不处理
                if (files.Length > 0 || subDirs.Length != 1)
                {
                    AppendLog($"[智能路径] 不符合处理条件（文件数={files.Length}, 子文件夹数={subDirs.Length}）", ConsoleColor.Gray);
                    return false;
                }

                // 只有一个子文件夹，获取其名称
                string singleSubDir = subDirs[0];
                string subDirName = Path.GetFileName(singleSubDir);
                
                AppendLog($"[智能路径] 找到唯一子文件夹: {subDirName}", ConsoleColor.Cyan);
                
                // 根据配置的处理模式决定最终文件夹名称
                string finalFolderName;
                
                if (_settings.SmartPathProcessingMode == SmartPathMode.Concatenate)
                {
                    // 拼接模式：将父文件夹名和子文件夹名用下划线连接
                    string parentDirName = Path.GetFileName(extractedFolder);
                    finalFolderName = $"{parentDirName}_{subDirName}";
                    AppendLog($"[智能路径] 使用拼接模式: {parentDirName} + {subDirName} = {finalFolderName}", ConsoleColor.Gray);
                }
                else // SmartPathMode.SmartSelect
                {
                    // 智能选择模式：优先选择长的、有日文的文件夹名
                    string parentDirName = Path.GetFileName(extractedFolder);
                    
                    bool parentHasJapanese = System.Text.RegularExpressions.Regex.IsMatch(parentDirName, @"[\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF]");
                    bool subHasJapanese = System.Text.RegularExpressions.Regex.IsMatch(subDirName, @"[\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF]");
                    
                    bool parentIsLong = parentDirName.Length > 10;
                    bool subIsLong = subDirName.Length > 10;
                    
                    AppendLog($"[智能路径] 父文件夹: '{parentDirName}' (长度: {parentDirName.Length}, 日文: {parentHasJapanese})", ConsoleColor.Gray);
                    AppendLog($"[智能路径] 子文件夹: '{subDirName}' (长度: {subDirName.Length}, 日文: {subHasJapanese})", ConsoleColor.Gray);
                    
                    // 评分系统：日文+100分，长名称+50分
                    int parentScore = (parentHasJapanese ? 100 : 0) + (parentIsLong ? 50 : 0);
                    int subScore = (subHasJapanese ? 100 : 0) + (subIsLong ? 50 : 0);
                    
                    AppendLog($"[智能路径] 父文件夹评分: {parentScore}, 子文件夹评分: {subScore}", ConsoleColor.Gray);
                    
                    // 选择评分高的，如果评分相同则选择较长的
                    if (subScore > parentScore || (subScore == parentScore && subDirName.Length >= parentDirName.Length))
                    {
                        finalFolderName = subDirName;
                        AppendLog($"[智能路径] 选择子文件夹名称: {finalFolderName}", ConsoleColor.Cyan);
                    }
                    else
                    {
                        finalFolderName = parentDirName;
                        AppendLog($"[智能路径] 选择父文件夹名称: {finalFolderName}", ConsoleColor.Cyan);
                    }
                }
                
                // 生成临时名称（带 GUID 前缀）
                string tempName = $"{Guid.NewGuid().ToString().Substring(0, 8)}_{finalFolderName}";
                string tempPath = Path.Combine(Path.GetDirectoryName(extractedFolder) ?? "", tempName);
                
                AppendLog($"[智能路径] 步骤1: 移动子文件夹到上级目录", ConsoleColor.Cyan);
                
                // 移动子文件夹到上级目录（带临时前缀）
                Directory.Move(singleSubDir, tempPath);
                AppendLog($"[智能路径]   已移动: {subDirName} -> {tempName}", ConsoleColor.Green);
                
                // 删除原来的空文件夹
                AppendLog($"[智能路径] 步骤2: 删除空文件夹", ConsoleColor.Cyan);
                
                if (Directory.Exists(extractedFolder))
                {
                    try
                    {
                        Directory.Delete(extractedFolder);
                        AppendLog($"[智能路径]   已删除: {Path.GetFileName(extractedFolder)}", ConsoleColor.Gray);
                    }
                    catch (Exception deleteEx)
                    {
                        AppendLog($"[智能路径] ⚠ 删除空文件夹失败: {deleteEx.Message}", ConsoleColor.Yellow);
                        AppendLog($"[智能路径]   保留原文件夹: {Path.GetFileName(extractedFolder)}", ConsoleColor.Yellow);
                    }
                }
                
                // 重命名，去掉 GUID 前缀
                string finalPath = Path.Combine(Path.GetDirectoryName(tempPath) ?? "", finalFolderName);
                AppendLog($"[智能路径] 步骤3: 重命名（去掉临时前缀）", ConsoleColor.Cyan);
                
                try
                {
                    // 如果目标已存在，先删除
                    if (Directory.Exists(finalPath))
                    {
                        Directory.Delete(finalPath, true);
                        AppendLog($"[智能路径]   已删除已存在的: {finalFolderName}", ConsoleColor.Gray);
                    }
                    
                    Directory.Move(tempPath, finalPath);
                    AppendLog($"[智能路径]   已重命名: {tempName} -> {finalFolderName}", ConsoleColor.Green);
                    
                    // 更新 extractedFolder 指向新位置，以便循环继续处理
                    extractedFolder = finalPath;
                    return true;
                }
                catch (Exception renameEx)
                {
                    AppendLog($"[智能路径] ⚠ 重命名失败: {renameEx.Message}", ConsoleColor.Yellow);
                    AppendLog($"[智能路径]   保留临时名称: {tempName}", ConsoleColor.Yellow);
                    return false;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[智能路径] 处理失败: {ex.Message}", ConsoleColor.Red);
                AppendLog($"[智能路径] 堆栈: {ex.StackTrace}", ConsoleColor.Red);
                return false;
            }
        }

        /// <summary>
        /// 递归清理空目录
        /// </summary>
        private void CleanEmptyDirectories(string path)
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(path))
                {
                    CleanEmptyDirectories(dir);
                }

                if (Directory.GetFiles(path).Length == 0 && Directory.GetDirectories(path).Length == 0)
                {
                    Directory.Delete(path);
                    AppendLog($"[智能路径] 已删除空目录: {Path.GetFileName(path)}", ConsoleColor.Gray);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[智能路径] 清理空目录失败: {ex.Message}", ConsoleColor.Red);
            }
        }

        #region 黑名单文件处理

        /// <summary>
        /// 检查文件是否匹配黑名单模式
        /// </summary>
        /// <param name="filePath">文件完整路径</param>
        /// <param name="matchedPattern">匹配到的模式（输出）</param>
        /// <returns>是否为黑名单文件</returns>
        private bool IsBlacklistedFile(string filePath, out string matchedPattern)
        {
            matchedPattern = string.Empty;

            // 获取黑名单模式列表
            var patterns = _settings.GetBlacklistPatterns();
            if (patterns == null || patterns.Count == 0)
                return false;

            string fileName = Path.GetFileName(filePath);
            string fullPath = Path.GetFullPath(filePath).ToLowerInvariant();

            foreach (var pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;

                string normalizedPattern = pattern.Trim().ToLowerInvariant();

                // 判断是文件名模式还是完整路径模式
                bool isPathPattern = normalizedPattern.Contains("\\") || normalizedPattern.Contains("/");
                string target = isPathPattern ? fullPath : fileName.ToLowerInvariant();

                // 将通配符转换为正则表达式
                string regexPattern = "^" + Regex.Escape(normalizedPattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";

                try
                {
                    if (Regex.IsMatch(target, regexPattern, RegexOptions.IgnoreCase))
                    {
                        matchedPattern = pattern;
                        return true;
                    }
                }
                catch (RegexParseException)
                {
                    // 忽略无效的正则表达式
                    AppendLog($"[黑名单] 无效的模式: {pattern}", ConsoleColor.Yellow);
                }
            }

            return false;
        }

        /// <summary>
        /// 永久删除黑名单文件
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="matchedPattern">匹配到的模式</param>
        private void DeleteBlacklistedFile(string filePath, string matchedPattern)
        {
            try
            {
                string fileName = Path.GetFileName(filePath);
                AppendLog($"[黑名单] 检测到黑名单文件: {fileName} (匹配模式: {matchedPattern})", ConsoleColor.Yellow);

                File.Delete(filePath);
                AppendLog($"[黑名单] 已永久删除: {fileName}", ConsoleColor.Green);
            }
            catch (UnauthorizedAccessException ex)
            {
                AppendLog($"[黑名单] 删除失败（权限不足）: {Path.GetFileName(filePath)} - {ex.Message}", ConsoleColor.Red);
            }
            catch (IOException ex)
            {
                AppendLog($"[黑名单] 删除失败（文件被占用）: {Path.GetFileName(filePath)} - {ex.Message}", ConsoleColor.Red);
            }
            catch (Exception ex)
            {
                AppendLog($"[黑名单] 删除失败: {Path.GetFileName(filePath)} - {ex.Message}", ConsoleColor.Red);
            }
        }

        #endregion


        /// 流程：
        /// 1. 先检查扩展名是否在排除列表中，如果是则直接返回 false
        /// 2. 再检查扩展名是否在包含列表中，如果是则用魔术数验证
        /// 3. 如果扩展名不在包含列表中，直接用魔术数检测
        /// </summary>
        private bool IsStegoCarrierExtension(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return StegoCarrierExtensions.Contains(ext);
        }

        private bool IsStegoDetectionActiveForFile(string filePath)
        {
            if (!_settings.EnableStegoDetection)
                return false;

            if (!IsStegoCarrierExtension(filePath))
                return false;

            long minSizeBytes = (long)Math.Max(1, _settings.StegoDetectionMinFileSizeMB) * 1024L * 1024L;
            long fileSizeBytes = new FileInfo(filePath).Length;
            if (fileSizeBytes < minSizeBytes)
            {
                AppendLog(
                    $"  [隐写探测] {Path.GetFileName(filePath)}: 文件过小 ({fileSizeBytes / 1024 / 1024}MB)，低于下限 {_settings.StegoDetectionMinFileSizeMB}MB，跳过探测",
                    ConsoleColor.Gray);
                return false;
            }

            return IsStegoFileBy7ZipProbe(filePath);
        }

        private bool IsStegoFileBy7ZipProbe(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            if (_stegoProbeCache.TryGetValue(filePath, out bool cached))
                return cached;

            bool detected = false;
            try
            {
                if (string.IsNullOrWhiteSpace(_settings.SevenZipPath) || !File.Exists(_settings.SevenZipPath))
                    return false;

                var startInfo = new ProcessStartInfo
                {
                    FileName = _settings.SevenZipPath,
                    Arguments = $"l -t# -sccUTF-8 \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();

                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(); } catch { }
                    AppendLog($"  [隐写探测] {Path.GetFileName(filePath)}: 探测超时，跳过", ConsoleColor.Gray);
                    detected = false;
                }
                else if (process.ExitCode == 0)
                {
                    string lower = (output + Environment.NewLine + error).ToLowerInvariant();
                    detected = lower.Contains(".zip") ||
                               lower.Contains(".7z") ||
                               lower.Contains(".rar") ||
                               lower.Contains(".tar") ||
                               lower.Contains("listing archive");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"  [隐写探测] {Path.GetFileName(filePath)}: {ex.Message}", ConsoleColor.Gray);
                detected = false;
            }

            _stegoProbeCache[filePath] = detected;
            return detected;
        }

        /// <param name="filePath">文件路径</param>
        /// <returns>是否为压缩文件</returns>
        private bool IsArchiveFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            if (!File.Exists(filePath))
                return false;

            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            // 步骤1：获取排除的扩展名列表
            var excludedExtensions = _settings.GetExcludedExtensions();
            
            // 如果在排除列表中，直接返回 false，结束解压流程
            if (excludedExtensions.Contains(extension))
            {
                if (IsStegoDetectionActiveForFile(filePath))
                {
                    AppendLog($"  [确认] {Path.GetFileName(filePath)}: 命中隐写探测，按隐写容器处理", ConsoleColor.Green);
                    return true;
                }

                return false;
            }

            // 步骤2：获取需要检测的压缩文件扩展名列表
            var archiveExtensions = _settings.GetArchiveExtensions();
            
            // 如果扩展名在包含列表中，用魔术数验证是否真的是压缩文件
            bool extensionMatched = false;
            string fileName = Path.GetFileName(filePath).ToLowerInvariant();
            foreach (var ext in archiveExtensions)
            {
                if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) || extension == ext)
                {
                    extensionMatched = true;
                    break;
                }
            }
            
            // 检查分卷压缩包格式
            if (!extensionMatched && IsMultiVolumeArchive(filePath))
            {
                extensionMatched = true;
            }
            
            if (extensionMatched)
            {
                // 扩展名匹配，用魔术数验证
                if (IsArchiveFileByMagicNumber(filePath))
                {
                    AppendLog($"  [确认] {Path.GetFileName(filePath)}: 魔术数验证通过，是压缩文件", ConsoleColor.Green);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                // 扩展名不匹配，直接用魔术数检测
                if (IsArchiveFileByMagicNumber(filePath))
                {
                    AppendLog($"  [确认] {Path.GetFileName(filePath)}: 魔术数检测通过，是压缩文件", ConsoleColor.Green);
                    return true;
                }
                else
                {
                    if (IsStegoDetectionActiveForFile(filePath))
                    {
                        AppendLog($"  [确认] {Path.GetFileName(filePath)}: 魔术数失败但隐写探测命中", ConsoleColor.Green);
                        return true;
                    }

                    return false;
                }
            }
        }

        /// <summary>
        /// 基于扩展名判断压缩文件
        /// </summary>
        private bool IsArchiveFileByExtension(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            
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
            if (IsMultiVolumeArchive(filePath))
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
        private bool IsArchiveFileByMagicNumber(string filePath)
        {
            try
            {
                // 文件太小不可能包含有效压缩数据
                if (new FileInfo(filePath).Length < 16)
                    return false;

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

                    // GZ: 1F 8B
                    if (header.Length >= 2 && header[0] == 0x1F && header[1] == 0x8B)
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

                    // TAR: 在257字节偏移处有 "ustar"
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
            catch (Exception ex)
            {
                // 读取失败时，不判断为压缩文件
                AppendLog($"  文件头检测失败 {Path.GetFileName(filePath)}: {ex.Message}", ConsoleColor.Gray);
                return false;
            }
        }

        /// <summary>
        /// 判断文件是否为分卷压缩包的一部分
        /// 支持的分卷格式：
        /// - .7z.001, .7z.002, ...
        /// - .part1.rar, .part2.rar, ...
        /// - .001, .002, ... (通用分卷)
        /// - .z01, .z02, ... (ZIP 分卷)
        /// - .r00, .r01, ... (RAR 分卷)
        /// </summary>
        private bool IsMultiVolumeArchive(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            string fileName = Path.GetFileName(filePath).ToLowerInvariant();
            
            // 7z 分卷格式：.7z.001, .7z.002, ...
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.7z\.\d{3,}$"))
                return true;

            // RAR 分卷格式：.part1.rar, .part2.rar, ...
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.part\d+\.rar$"))
                return true;

            // 通用数字分卷：.001, .002, .003, ...
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.\d{3,}$"))
                return true;

            // ZIP 分卷：.z01, .z02, ...
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.z\d{2,}$"))
                return true;

            // RAR 老式分卷：.r00, .r01, ...
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.r\d{2,}$"))
                return true;

            return false;
        }

        /// <summary>
        /// 检测并获取分卷压缩包的所有分卷文件
        /// </summary>
        /// <param name="filePath">任意一个分卷文件路径</param>
        /// <returns>分卷压缩包信息</returns>
        private ArchiveVolumeInfo DetectMultiVolumeArchive(string filePath)
        {
            var volumeInfo = new ArchiveVolumeInfo();
            
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return volumeInfo;

            string directory = Path.GetDirectoryName(filePath) ?? string.Empty;
            string fileName = Path.GetFileName(filePath);
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
            
            // 判断分卷类型并查找所有分卷
            var allVolumes = new List<string>();

            // 1. 7z 分卷格式：xxx.7z.001, xxx.7z.002, ...
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.7z\.\d{3,}$"))
            {
                string baseName = fileName.Substring(0, fileName.LastIndexOf(".7z."));
                allVolumes = FindSequentialVolumes(directory, baseName + ".7z.", 3);
            }
            // 2. RAR 分卷格式：xxx.part1.rar, xxx.part2.rar, ...
            else if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.part\d+\.rar$"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(.+)\.part(\d+)\.rar$");
                if (match.Success)
                {
                    string baseName = match.Groups[1].Value;
                    allVolumes = FindRarPartVolumes(directory, baseName);
                }
            }
            // 3. 通用数字分卷：xxx.001, xxx.002, ...
            else if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.\d{3,}$"))
            {
                string extension = Path.GetExtension(fileName);
                string baseName = fileNameWithoutExt;
                allVolumes = FindSequentialVolumes(directory, baseName + ".", extension.TrimStart('.').Length);
            }
            // 4. ZIP 分卷：xxx.z01, xxx.z02, ...
            else if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.z\d{2,}$"))
            {
                string baseName = fileName.Substring(0, fileName.LastIndexOf(".z"));
                allVolumes = FindSequentialVolumes(directory, baseName + ".z", 2);
            }
            // 5. RAR 老式分卷：xxx.r00, xxx.r01, ...
            else if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.r\d{2,}$"))
            {
                string baseName = fileName.Substring(0, fileName.LastIndexOf(".r"));
                allVolumes = FindSequentialVolumes(directory, baseName + ".r", 2);
            }

            volumeInfo.AllVolumePaths = allVolumes;
            
            // 主卷通常是第一个分卷
            if (allVolumes.Count > 0)
            {
                volumeInfo.MainVolumePath = allVolumes[0];
            }

            return volumeInfo;
        }

        /// <summary>
        /// 查找顺序编号的分卷文件（如 .001, .002, .003）
        /// </summary>
        private List<string> FindSequentialVolumes(string directory, string prefix, int numberLength)
        {
            var volumes = new List<string>();
            int volumeNumber = 1;

            while (true)
            {
                string volumeName = $"{prefix}{volumeNumber.ToString($"D{numberLength}")}";
                string volumePath = Path.Combine(directory, volumeName);

                if (File.Exists(volumePath))
                {
                    volumes.Add(volumePath);
                    volumeNumber++;
                }
                else
                {
                    break;
                }
            }

            return volumes;
        }

        /// <summary>
        /// 查找 RAR part 格式的分卷文件
        /// </summary>
        private List<string> FindRarPartVolumes(string directory, string baseName)
        {
            var volumes = new List<string>();
            int partNumber = 1;

            while (true)
            {
                string volumeName = $"{baseName}.part{partNumber}.rar";
                string volumePath = Path.Combine(directory, volumeName);

                if (File.Exists(volumePath))
                {
                    volumes.Add(volumePath);
                    partNumber++;
                }
                else
                {
                    break;
                }
            }

            return volumes;
        }

        /// <summary>
        /// 根据输出模式计算输出目录
        /// </summary>
        private string GetOutputDirectory(string archivePath)
        {
            string archiveDir = Path.GetDirectoryName(archivePath) ?? string.Empty;
            string archiveNameWithoutExt = GetArchiveFolderName(archivePath);

            switch (_settings.OutputMode)
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
                    
                    // 检查是否存在同名文件冲突
                    if (File.Exists(targetFolder))
                    {
                        AppendLog($"  警告: 存在同名文件 '{archiveNameWithoutExt}',自动切换为'解压到当前目录'模式", ConsoleColor.Yellow);
                        return archiveDir;
                    }
                    
                    return targetFolder;

                default:
                    return archiveDir;
            }
        }

        /// <summary>
        /// 获取压缩包的文件夹名称（智能去掉分卷后缀和压缩格式后缀）
        /// 例如: ba360.7z.001 -> ba360
        ///       test.rar -> test
        ///       document.zip -> document
        ///       sample.tar.gz -> sample
        /// </summary>
        private string GetArchiveFolderName(string archivePath)
        {
            string fileName = Path.GetFileName(archivePath);
            
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
            return NormalizeArchiveLikeName(fileName);
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
                    .Where(IsArchiveFile)
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

        private async Task HandleOriginalFile(string filePath)
        {
            try
            {
                // 获取文件项以检查是否为分卷压缩包
                FileItem? fileItem = null;
                Dispatcher.Invoke(() =>
                {
                    fileItem = _fileList.FirstOrDefault(f => f.FilePath == filePath);
                    if (fileItem == null)
                    {
                        // 尝试在子项中查找
                        fileItem = FindFileItemInTree(filePath);
                    }
                });
                
                switch (_settings.FileAfterExtract)
                {
                    case FileAction.MoveToRecycleBin:
                        // 移动到回收站
                        if (fileItem?.VolumeInfo?.HasVolumeList == true)
                        {
                            // 分卷压缩包：移动所有分卷
                            await MoveAllVolumesToRecycleBin(fileItem.VolumeInfo);
                        }
                        else
                        {
                            // 普通文件：移动到回收站
                            // 等待文件解锁
                            if (!await WaitForFileUnlock(filePath, 3000))
                            {
                                AppendLog($"  警告：原文件仍在占用中: {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                break;
                            }

                            bool success = false;
                            int retryCount = 3;
                            for (int i = 0; i < retryCount; i++)
                            {
                                try
                                {
                                    await Task.Run(() =>
                                    {
                                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(filePath, 
                                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                                    });
                                    success = true;
                                    break;
                                }
                                catch (IOException) when (i < retryCount - 1)
                                {
                                    AppendLog($"  原文件被占用，等待后重试 ({i + 1}/{retryCount}): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                    await Task.Delay(1000 * (i + 1));
                                }
                            }

                            if (success)
                            {
                                AppendLog($"  原文件已移动到回收站: {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                            }
                            else
                            {
                                AppendLog($"  移动原文件失败（多次重试后仍被占用）: {Path.GetFileName(filePath)}", ConsoleColor.Red);
                            }
                        }
                        break;

                    case FileAction.Delete:
                        // 直接删除
                        if (fileItem?.VolumeInfo?.HasVolumeList == true)
                        {
                            // 分卷压缩包：删除所有分卷
                            await DeleteAllVolumes(fileItem.VolumeInfo);
                        }
                        else
                        {
                            // 普通文件：直接删除
                            // 等待文件解锁
                            if (!await WaitForFileUnlock(filePath, 3000))
                            {
                                AppendLog($"  警告：原文件仍在占用中: {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                break;
                            }

                            bool success = false;
                            int retryCount = 3;
                            for (int i = 0; i < retryCount; i++)
                            {
                                try
                                {
                                    await Task.Run(() => File.Delete(filePath));
                                    success = true;
                                    break;
                                }
                                catch (IOException) when (i < retryCount - 1)
                                {
                                    AppendLog($"  原文件被占用，等待后重试 ({i + 1}/{retryCount}): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                    await Task.Delay(1000 * (i + 1));
                                }
                            }

                            if (success)
                            {
                                AppendLog($"  原文件已删除: {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                            }
                            else
                            {
                                AppendLog($"  删除原文件失败（多次重试后仍被占用）: {Path.GetFileName(filePath)}", ConsoleColor.Red);
                            }
                        }
                        break;

                    case FileAction.MoveToSpecificDir:
                        // 移动到指定目录
                        if (fileItem?.VolumeInfo?.HasVolumeList == true)
                        {
                            // 分卷压缩包：移动所有分卷
                            await MoveAllVolumesToSpecificDir(fileItem.VolumeInfo);
                        }
                        else
                        {
                            // 普通文件：移动到指定目录
                            await MoveFileToSpecificDir(filePath);
                        }
                        break;

                    case FileAction.Keep:
                    default:
                        // 保留原文件，不做处理
                        break;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"  处理原文件失败: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 移动所有分卷到回收站
        /// </summary>
        private async Task MoveAllVolumesToRecycleBin(ArchiveVolumeInfo volumeInfo)
        {
            AppendLog($"  准备移动 {volumeInfo.VolumeCount} 个分卷到回收站...", ConsoleColor.Cyan);
            
            int movedCount = 0;
            foreach (var volumePath in volumeInfo.AllVolumePaths)
            {
                try
                {
                    if (File.Exists(volumePath))
                    {
                        // 等待文件解锁（解压进程可能还在占用文件）
                        if (!await WaitForFileUnlock(volumePath, 3000))
                        {
                            AppendLog($"  警告：分卷文件仍在占用中: {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                            continue;
                        }

                        bool success = false;
                        int retryCount = 3;
                        for (int i = 0; i < retryCount; i++)
                        {
                            try
                            {
                                await Task.Run(() =>
                                {
                                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(volumePath, 
                                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                                });
                                success = true;
                                break;
                            }
                            catch (IOException) when (i < retryCount - 1)
                            {
                                // 文件被占用，等待后重试
                                AppendLog($"  分卷文件被占用，等待后重试 ({i + 1}/{retryCount}): {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                                await Task.Delay(1000 * (i + 1));
                            }
                        }

                        if (success)
                        {
                            AppendLog($"  已移动分卷到回收站: {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                            movedCount++;
                        }
                        else
                        {
                            AppendLog($"  移动分卷失败（多次重试后仍被占用）: {Path.GetFileName(volumePath)}", ConsoleColor.Red);
                        }
                    }
                }
                catch (Exception ex)
                {
                    string errorMsg = ex.Message;
                    if (string.IsNullOrWhiteSpace(errorMsg) && ex.InnerException != null)
                    {
                        errorMsg = ex.InnerException.Message;
                    }
                    AppendLog($"  移动分卷失败 {Path.GetFileName(volumePath)}: {errorMsg}", ConsoleColor.Red);
                }
            }
            
            AppendLog($"  分卷压缩包已处理（已移动 {movedCount}/{volumeInfo.VolumeCount} 个分卷到回收站）", ConsoleColor.Green);
        }

        /// <summary>
        /// 等待文件解锁（文件不再被其他进程占用）
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>true=文件已解锁, false=超时</returns>
        private async Task<bool> WaitForFileUnlock(string filePath, int timeoutMs)
        {
            int waited = 0;
            int interval = 100; // 每100ms检查一次

            while (waited < timeoutMs)
            {
                try
                {
                    // 独占读即可检测占用；ReadWrite 在只读属性/ACL 上会失败，导致误判未解锁而跳过清理
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        return true; // 成功打开，文件已解锁
                    }
                }
                catch (IOException)
                {
                    // 文件被占用，等待后重试
                    await Task.Delay(interval);
                    waited += interval;
                }
                catch
                {
                    // 其他错误（如文件不存在），认为已解锁
                    return true;
                }
            }

            return false; // 超时
        }

        /// <summary>
        /// 删除所有分卷
        /// </summary>
        private async Task DeleteAllVolumes(ArchiveVolumeInfo volumeInfo)
        {
            AppendLog($"  准备删除 {volumeInfo.VolumeCount} 个分卷...", ConsoleColor.Cyan);
            
            int deletedCount = 0;
            foreach (var volumePath in volumeInfo.AllVolumePaths)
            {
                try
                {
                    if (File.Exists(volumePath))
                    {
                        // 等待文件解锁
                        if (!await WaitForFileUnlock(volumePath, 3000))
                        {
                            AppendLog($"  警告：分卷文件仍在占用中: {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                            continue;
                        }

                        bool success = false;
                        int retryCount = 3;
                        for (int i = 0; i < retryCount; i++)
                        {
                            try
                            {
                                await Task.Run(() => File.Delete(volumePath));
                                success = true;
                                break;
                            }
                            catch (IOException) when (i < retryCount - 1)
                            {
                                AppendLog($"  分卷文件被占用，等待后重试 ({i + 1}/{retryCount}): {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                                await Task.Delay(1000 * (i + 1));
                            }
                        }

                        if (success)
                        {
                            AppendLog($"  已删除分卷: {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                            deletedCount++;
                        }
                        else
                        {
                            AppendLog($"  删除分卷失败（多次重试后仍被占用）: {Path.GetFileName(volumePath)}", ConsoleColor.Red);
                        }
                    }
                }
                catch (Exception ex)
                {
                    string errorMsg = ex.Message;
                    if (string.IsNullOrWhiteSpace(errorMsg) && ex.InnerException != null)
                    {
                        errorMsg = ex.InnerException.Message;
                    }
                    AppendLog($"  删除分卷失败 {Path.GetFileName(volumePath)}: {errorMsg}", ConsoleColor.Red);
                }
            }
            
            AppendLog($"  分卷压缩包已处理（已删除 {deletedCount}/{volumeInfo.VolumeCount} 个分卷）", ConsoleColor.Green);
        }

        /// <summary>
        /// 移动所有分卷到指定目录
        /// </summary>
        private async Task MoveAllVolumesToSpecificDir(ArchiveVolumeInfo volumeInfo)
        {
            if (string.IsNullOrWhiteSpace(_settings.ArchiveCleanupDir))
            {
                AppendLog($"  警告：未设置分卷清理目录", ConsoleColor.Yellow);
                return;
            }

            if (!Directory.Exists(_settings.ArchiveCleanupDir))
            {
                Directory.CreateDirectory(_settings.ArchiveCleanupDir);
            }

            AppendLog($"  准备移动 {volumeInfo.VolumeCount} 个分卷到: {_settings.ArchiveCleanupDir}", ConsoleColor.Cyan);
            
            int movedCount = 0;
            foreach (var volumePath in volumeInfo.AllVolumePaths)
            {
                try
                {
                    if (File.Exists(volumePath))
                    {
                        string destPath = Path.Combine(_settings.ArchiveCleanupDir, Path.GetFileName(volumePath));
                        
                        // 如果目标文件已存在，添加序号
                        if (File.Exists(destPath))
                        {
                            string nameWithoutExt = Path.GetFileNameWithoutExtension(destPath);
                            string ext = Path.GetExtension(destPath);
                            int counter = 1;
                            while (File.Exists(destPath))
                            {
                                destPath = Path.Combine(_settings.ArchiveCleanupDir, $"{nameWithoutExt}_{counter}{ext}");
                                counter++;
                            }
                        }
                        
                        await Task.Run(() => File.Move(volumePath, destPath));
                        AppendLog($"  已移动分卷: {Path.GetFileName(volumePath)} -> {Path.GetFileName(destPath)}", ConsoleColor.Yellow);
                        movedCount++;
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"  移动分卷失败 {Path.GetFileName(volumePath)}: {ex.Message}", ConsoleColor.Red);
                }
            }
            
            AppendLog($"  分卷压缩包已处理（已移动 {movedCount}/{volumeInfo.VolumeCount} 个分卷）", ConsoleColor.Green);
        }

        /// <summary>
        /// 移动单个文件到指定目录
        /// </summary>
        private async Task MoveFileToSpecificDir(string filePath)
        {
            if (string.IsNullOrWhiteSpace(_settings.ArchiveCleanupDir))
            {
                AppendLog($"  警告：未设置清理目录", ConsoleColor.Yellow);
                return;
            }

            if (!Directory.Exists(_settings.ArchiveCleanupDir))
            {
                Directory.CreateDirectory(_settings.ArchiveCleanupDir);
            }

            try
            {
                string destPath = Path.Combine(_settings.ArchiveCleanupDir, Path.GetFileName(filePath));
                
                // 如果目标文件已存在，添加序号
                if (File.Exists(destPath))
                {
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(destPath);
                    string ext = Path.GetExtension(destPath);
                    int counter = 1;
                    while (File.Exists(destPath))
                    {
                        destPath = Path.Combine(_settings.ArchiveCleanupDir, $"{nameWithoutExt}_{counter}{ext}");
                        counter++;
                    }
                }
                
                await Task.Run(() => File.Move(filePath, destPath));
                AppendLog($"  原文件已移动: {Path.GetFileName(filePath)} -> {destPath}", ConsoleColor.Yellow);
            }
            catch (Exception ex)
            {
                AppendLog($"  移动文件失败: {ex.Message}", ConsoleColor.Red);
            }
        }


        private void AppendLog(string message, ConsoleColor color)
        {
            Dispatcher.Invoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                TxtLog.AppendText($"[{timestamp}]--> {message}\r\n");
                TxtLog.ScrollToEnd();
            });
        }

        #endregion
    }

    public class FileItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _fileName = string.Empty;
        private string _filePath = string.Empty;
        private string _status = string.Empty;
        private long _fileSize = 0;
        private string? _foundPassword = null;
        
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
        
        public string FileName 
        { 
            get => _fileName; 
            set 
            { 
                _fileName = value; 
                OnPropertyChanged(nameof(FileName));
            } 
        }
        
        public string FilePath 
        { 
            get => _filePath; 
            set 
            { 
                _filePath = value; 
                OnPropertyChanged(nameof(FilePath));
            } 
        }
        
        public string Status 
        { 
            get => _status; 
            set 
            { 
                _status = value; 
                OnPropertyChanged(nameof(Status));
            } 
        }
        
        public long FileSize 
        { 
            get => _fileSize; 
            set 
            { 
                _fileSize = value; 
                OnPropertyChanged(nameof(FileSize));
            } 
        }
        
        public string? FoundPassword 
        { 
            get => _foundPassword; 
            set 
            { 
                _foundPassword = value; 
                OnPropertyChanged(nameof(FoundPassword));
            } 
        }
        
        public ArchiveVolumeInfo? VolumeInfo { get; set; }
        
        // 新增：父子关系支持
        public ObservableCollection<FileItem> Children { get; set; } = new ObservableCollection<FileItem>();
        public FileItem? Parent { get; set; }
        public bool IsParent => Children.Count > 0;
        
        /// <summary>
        /// 所属的任务ID（用于批量处理时识别任务范围）
        /// </summary>
        public int TaskId { get; set; } = -1;
        
        // 新增：子节点完成状态跟踪（用于反向传播）
        private int _expectedChildrenCount = 0;      // 预期的子节点总数
        private int _completedChildrenCount = 0;     // 已完成的子节点数
        private bool _isMarkedComplete = false;      // 是否已标记为完成（防止重复上报）
        private bool _selfExtractSuccess = false;    // 自身解压是否成功
        
        /// <summary>
        /// 设置自身解压结果
        /// </summary>
        public void SetSelfExtractResult(bool success)
        {
            _selfExtractSuccess = success;
        }
        
        /// <summary>
        /// 获取自身解压结果
        /// </summary>
        public bool GetSelfExtractResult()
        {
            return _selfExtractSuccess;
        }
        
        /// <summary>
        /// 设置预期的子节点数量（在扫描完解压目录后调用）
        /// </summary>
        public void SetExpectedChildrenCount(int count)
        {
            _expectedChildrenCount = count;
            _completedChildrenCount = 0;
            _isMarkedComplete = false;
        }
        
        /// <summary>
        /// 标记一个子节点完成，并返回是否所有子节点都已完成
        /// </summary>
        public bool MarkChildComplete()
        {
            lock (_lockObject)
            {
                if (_isMarkedComplete)
                    return false;  // 已经标记完成，不再处理
                    
                _completedChildrenCount++;
                
                // 检查是否所有子节点都已完成
                bool allCompleted = _completedChildrenCount >= _expectedChildrenCount && _expectedChildrenCount > 0;
                
                if (allCompleted)
                {
                    _isMarkedComplete = true;
                }
                
                return allCompleted;
            }
        }
        
        /// <summary>
        /// 获取完成进度信息
        /// </summary>
        public string GetProgressInfo()
        {
            return $"{_completedChildrenCount}/{_expectedChildrenCount}";
        }
        
        // 用于线程安全的状态标记
        private readonly object _lockObject = new object();
        private bool _isBeingProcessed = false;
        
        /// <summary>
        /// 尝试获取处理锁，防止多线程竞争
        /// </summary>
        public bool TryLock()
        {
            lock (_lockObject)
            {
                if (_isBeingProcessed)
                    return false;
                _isBeingProcessed = true;
                return true;
            }
        }
        
        /// <summary>
        /// 释放处理锁
        /// </summary>
        public void ReleaseLock()
        {
            lock (_lockObject)
            {
                _isBeingProcessed = false;
            }
        }
    }
    
    /// <summary>
    /// 密码映射表（线程安全）
    /// </summary>
    public class PasswordMap
    {
        private readonly Dictionary<string, string?> _map = new Dictionary<string, string?>();
        private readonly object _lock = new object();
        
        public void Add(string filePath, string? password)
        {
            lock (_lock)
            {
                _map[filePath] = password;
            }
        }
        
        public bool TryGetValue(string filePath, out string? password)
        {
            lock (_lock)
            {
                return _map.TryGetValue(filePath, out password);
            }
        }

        public bool ContainsKey(string filePath)
        {
            lock (_lock)
            {
                return _map.ContainsKey(filePath);
            }
        }
        
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _map.Count;
                }
            }
        }

        public bool Remove(string filePath)
        {
            lock (_lock)
            {
                return _map.Remove(filePath);
            }
        }
    }

}








