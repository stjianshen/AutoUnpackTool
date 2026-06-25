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

namespace AutoUnpackTool
{
    public partial class MainWindow : Window
    {
        // Windows API: SHFileOperation for silent recycle bin
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;  // 允许撤销（移动到回收站）
        private const ushort FOF_NOCONFIRMATION = 0x0010;  // 不显示确认对话框
        private const ushort FOF_SILENT = 0x0004;  // 静默模式

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        private RootNode _rootNode = new RootNode { Title = "待解压任务(共0个文件)" };
        private ObservableCollection<FileItem> _fileList => _rootNode.Children;
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
        
        // 暂停状态标志 - 用于优雅暂停（等待当前任务完成后再停止）
        private bool _isPaused = false;
        
        // 任务完成信号
        private TaskCompletionSource<bool> _testCompletionSource = new();
        private TaskCompletionSource<bool> _extractCompletionSource = new();
        
        // 记录所有顶级 FileItem
        private List<FileItem> _topLevelItems = new();
        
        // 记录所有解压产生的目录节点（用于扁平化时只处理解压的目录）
        private HashSet<string> _extractedDirectoryNodes = new(StringComparer.OrdinalIgnoreCase);
        
        // 记录拖入的文件夹（用于后续智能路径处理，即使没有压缩包）
        private HashSet<string> _droppedFolders = new(StringComparer.OrdinalIgnoreCase);
        
        // 记录已处理的扁平化目录（防止重复处理导致死循环）
        private HashSet<string> _processedFlattenDirs = new(StringComparer.OrdinalIgnoreCase);
        
        private readonly ConcurrentDictionary<string, bool> _stegoProbeCache = new(StringComparer.OrdinalIgnoreCase);
        // 缓存隐写文件检测到的具体压缩类型（如 zip/7z/rar），null 表示非隐写或未检测到类型
        private readonly ConcurrentDictionary<string, string?> _stegoArchiveTypeCache = new(StringComparer.OrdinalIgnoreCase);
        // 缓存隐写文件内包含的真实文件列表（白名单），用于过滤 -t# 提取产生的误匹配垃圾文件
        private readonly ConcurrentDictionary<string, HashSet<string>> _stegoArchiveValidFilesCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> StegoCarrierExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".webm", ".mov", ".m4v"
        };

        public MainWindow()
        {
            InitializeComponent();
            LstFiles.ItemsSource = new ObservableCollection<RootNode> { _rootNode };
            
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
            
            // 初始化强制解压模式复选框状态
            ChkForceExtractMode.IsChecked = _settings.ForceExtractMode;
            
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
                        foreach (var folder in _droppedFolders)
                        {
                            _ = Task.Run(() => ProcessSmartPathBatchAsync(folder));
                        }
                        // 处理完后清空记录
                        _droppedFolders.Clear();
                    }
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
                                AddFileToList(archiveFile, ref addedCount, newFileItems);
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
            KillSevenZipProcesses();
            
            // 4. Kill掉所有NanaZip进程
            KillNanaZipProcesses();
            
            // 5. 清空UI和数据
            _fileList.Clear();
            _passwordMap = new PasswordMap();
            _pendingQueue = new ConcurrentQueue<FileItem>();
            _extractQueue = new ConcurrentQueue<FileItem>();
            _topLevelItems.Clear();
            _extractedDirectoryNodes.Clear();  // 清空觧压目录标记
            _stegoProbeCache.Clear();
            _stegoArchiveTypeCache.Clear();
            _stegoArchiveValidFilesCache.Clear();
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
                            Dispatcher.Invoke(() => fileItem.Status = $"测试异常: {ex.Message}");
                            AppendLog($"[{fileItem.FileName}] 测试异常: {ex.Message}", ConsoleColor.Red);
                        }
                    }
                    else
                    {
                        // 队列为空，等待信号或超时
                        AppendLog("[测试线程] 队列为空，等待新任务...", ConsoleColor.Gray);
                        
                        // 等待信号（最多等待60秒）或取消请求
                        bool signaled = false;
                        try
                        {
                            signaled = await Task.Run(() => 
                                _pendingQueueSignal.Wait(TimeSpan.FromSeconds(60), token), token);
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
            bool isArchive = IsArchiveFile(fileItem.FilePath, isDroppedFile: isDroppedFile);
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
                    _isPaused = false; // 重置暂停标志
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
                    AppendLog($"\n========== 所有处理流程完成 ==========", ConsoleColor.Green);
                    
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
                            foreach (var folder in folders)
                            {
                                if (Directory.Exists(folder))
                                {
                                    try
                                    {
                                        AppendLog($"[智能路径] 处理拖入的文件夹: {folder}", ConsoleColor.Cyan);
                                        await ProcessSmartPathBatchAsync(folder);
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
                        });
                    }
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
                        _ = HandleOriginalFileAsync(fileItem.FilePath);
                        
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
        /// 永久删除文件（带重试逻辑），不受 FileAfterExtract 设置影响
        /// 用于清理子压缩包——子压缩包总是直接删除，不保留
        /// </summary>
        private async Task DeleteFilePermanentlyAsync(string filePath)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (!File.Exists(filePath))
                        return;
                    
                    bool unlocked = await WaitForFileUnlock(filePath, 8000);
                    if (!unlocked)
                    {
                        int randomWait = Random.Shared.Next(1000, 3001);
                        AppendLog($"  文件仍在占用中，等待 {randomWait / 1000.0:F1} 秒后重试 ({i + 1}/5): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                        await Task.Delay(randomWait);
                        continue;
                    }
                    
                    await Task.Run(() => File.Delete(filePath));
                    AppendLog($"  子压缩包已删除: {Path.GetFileName(filePath)}", ConsoleColor.Gray);
                    return;
                }
                catch (IOException ex)
                {
                    if (i < 4)
                    {
                        int randomWait = Random.Shared.Next(1000, 3001);
                        AppendLog($"  删除失败 ({ex.Message})，等待 {randomWait / 1000.0:F1} 秒后重试 ({i + 1}/5): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                        await Task.Delay(randomWait);
                    }
                    else
                    {
                        AppendLog($"  删除失败（多次重试后仍失败）: {Path.GetFileName(filePath)}", ConsoleColor.Red);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"  删除子压缩包出错: {Path.GetFileName(filePath)}: {ex.Message}", ConsoleColor.Red);
                    return;
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
            
            var allItems = CollectAllFileItems(_fileList);
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
                if (IsStegoCarrierExtension(item.FilePath))
                {
                    AppendLog($"  跳过清理 [{item.FileName}]：隐写载体文件（非传统压缩包），保留原文件", ConsoleColor.Cyan);
                    continue;
                }
                
                try
                {
                    // 子压缩包不受解压后原文件处理设置影响，一律直接删除
                    await DeleteFilePermanentlyAsync(item.FilePath);
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
                var allItems = CollectAllFileItems(_fileList);
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
        /// 阶段1：清理所有子压缩包（确保目录干净后再扁平化）
        /// 阶段2：批量智能路径处理（扁平化）
        /// 阶段3：清理顶级原压缩包（含分卷）+ 遗留空目录 + 更新状态
        /// </summary>
        private async Task FinalizeTopLevelExtractAndSmartPathAsync(string topArchivePath)
        {
            AppendLog($"[阶段1] 开始清理子压缩包...", ConsoleColor.Cyan);
            // 阶段1：清理所有子压缩包
            try
            {
                await CleanupChildArchivesInTree();
                AppendLog($"[阶段1] 子压缩包清理完成", ConsoleColor.Cyan);
            }
            catch (Exception ex)
            {
                AppendLog($"[阶段1] 清理子压缩包失败: {ex.Message}", ConsoleColor.Red);
            }
        
            AppendLog($"[阶段2] 开始批量智能路径处理...", ConsoleColor.Cyan);
            // 阶段2：批量智能路径处理（扁平化）
            // 【关键修复】必须等待扁平化完成，才能继续阶段3
            try
            {
                await ProcessBatchSmartPathSynchronous(topArchivePath);
                AppendLog($"[阶段2] 批量智能路径处理完成", ConsoleColor.Cyan);
            }
            catch (Exception ex)
            {
                AppendLog($"[阶段2] 批量智能路径失败: {ex.Message}", ConsoleColor.Red);
            }
        
            AppendLog($"[阶段3] 开始清理顶级原压缩包: {Path.GetFileName(topArchivePath)}", ConsoleColor.Cyan);
            // 阶段3：清理顶级原压缩包（含分卷）+ 遗留空目录
            try
            {
                if (_settings.FileAfterExtract != FileAction.Keep)
                {
                    AppendLog($"[阶段3] 等待文件解锁并清理...", ConsoleColor.Cyan);
                    await HandleOriginalFile(topArchivePath);
                    AppendLog($"[阶段3] 顶级原压缩包清理完成", ConsoleColor.Cyan);
                }
                else
                {
                    AppendLog($"[阶段3] 配置为保留原文件，跳过清理", ConsoleColor.Cyan);
                }
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
            
            string outputDir = GetOutputDirectory(fileItem.FilePath);
            
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
            bool isStego = IsStegoDetectionActiveForFile(fileItem.FilePath);
            string? detectedType = isStego ? DetectStegoArchiveType(fileItem.FilePath) : null;
            AppendLog($"\n[线程 {taskId}] 觧压: {fileItem.FileName}", ConsoleColor.White, fileItem);
            AppendLog($"  输出目录: {outputDir}", ConsoleColor.Gray, fileItem);
            AppendLog($"  隐写模式: {isStego}", ConsoleColor.Gray, fileItem);
            if (isStego)
                AppendLog($"  压缩类型: -t{detectedType ?? "(null)"}", ConsoleColor.Gray, fileItem);
            AppendLog($"  密码: {password ?? "(无密码)"}", ConsoleColor.Gray, fileItem);

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
                    stegoArchiveType: isStego ? "#" : null);

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
                        _extractedDirectoryNodes.Add(outputDir);
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
                        try
                        {
                            var topFiles = Directory.GetFiles(outputDir, "*", SearchOption.TopDirectoryOnly);
                            foreach (var f in topFiles)
                            {
                                if (!IsArchiveFile(f, false, isDroppedFile: false))
                                {
                                    string fName = Path.GetFileName(f);
                                    AppendLog($"[线程 {taskId}] [残留清理] 删除非压缩包残留: {fName}", ConsoleColor.Yellow, fileItem);
                                    try { File.Delete(f); } catch { }
                                }
                            }
                        }
                        catch { }
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
            AppendLog($"[{leafItem.FileName}] 叶子节点觧压完成: {leafItem.Status}", ConsoleColor.Green, leafItem);
            
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
                    // 注意：解压后发现的文件不受强制模式影响，始终进行正常检测
                    if (IsArchiveFile(file, false, isDroppedFile: false))
                    {
                        allArchiveFiles.Add(file);
                    }
                }
                
                // 去重
                allArchiveFiles = allArchiveFiles.Distinct().ToList();

                // 【隐写垃圾过滤】-t# 哈希扫描产生的文件用标准 7z l（不带 -t#）逐文件验证
                // 真正的压缩包通得过标准检测，-t# 误匹配的垃圾文件通不过
                if (parentFileItem != null && IsStegoDetectionActiveForFile(parentFileItem.FilePath))
                {
                    var beforeCount = allArchiveFiles.Count;
                    var verifiedFiles = new List<string>();
                    foreach (var f in allArchiveFiles)
                    {
                        string fName = Path.GetFileName(f);
                        AppendLog($"[线程 {taskId}] [隐写验证] 验证文件: {fName}", ConsoleColor.Gray, parentFileItem);
                        if (VerifyIsRealArchive(f))
                        {
                            verifiedFiles.Add(f);
                            AppendLog($"[线程 {taskId}] [隐写验证]   ✓ 通过: {fName}", ConsoleColor.Green, parentFileItem);
                        }
                        else
                        {
                            // 标准 7z l 验证失败 → -t# 误匹配垃圾，从磁盘删除
                            AppendLog($"[线程 {taskId}] [隐写验证]   ✗ 垃圾文件，删除: {fName}", ConsoleColor.Yellow, parentFileItem);
                            try { File.Delete(f); } catch { }
                        }
                    }
                    allArchiveFiles = verifiedFiles;
                    AppendLog($"[线程 {taskId}] [隐写验证] 过滤前: {beforeCount} → 过滤后: {allArchiveFiles.Count}", ConsoleColor.Cyan, parentFileItem);
                }

                // 过滤掉解压前已存在的文件，只保留新解压的文件
                if (existingFilesBeforeExtract != null && existingFilesBeforeExtract.Count > 0)
                {
                    var newlyExtractedFiles = allArchiveFiles.Where(f => !existingFilesBeforeExtract.Contains(f)).ToList();
                    AppendLog($"[线程 {taskId}] [DEBUG] 过滤前: {allArchiveFiles.Count} 个, 过滤后: {newlyExtractedFiles.Count} 个新文件", ConsoleColor.Magenta, parentFileItem);
                    allArchiveFiles = newlyExtractedFiles;
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
                    if (IsBlacklistedFile(archiveFile, out string matchedPattern))
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
                    // 对于隐写文件等使用 ArchiveDir 模式解压的文件，不进行扁平化处理
                    // 因为输出目录就是压缩包所在目录，会误处理其他无关文件夹
                    string extractedFolder = GetOutputDirectory(item.FilePath);
                    
                    // 检查是否为 ArchiveDir 模式（输出目录等于压缩包所在目录）
                    string archiveDir = Path.GetDirectoryName(item.FilePath) ?? string.Empty;
                    if (extractedFolder.Equals(archiveDir, StringComparison.OrdinalIgnoreCase))
                    {
                        // ArchiveDir 模式：跳过扁平化处理
                        continue;
                    }
                    
                    if (!string.IsNullOrEmpty(extractedFolder) && Directory.Exists(extractedFolder))
                    {
                        taskExtractedFolders.Add(extractedFolder);
                    }
                }
            }

            // 对每个解压生成的文件夹执行批量扁平化
            foreach (var extractedFolder in taskExtractedFolders)
            {
                await Task.Run(() => ProcessSmartPathBatchAsync(extractedFolder));
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
                    
                    // 对于 ArchiveDir 模式（解压到当前目录），不进行扁平化处理
                    // 因为输出目录就是压缩包所在目录，会误处理其他无关文件夹
                    string archiveDir = Path.GetDirectoryName(item.FilePath) ?? string.Empty;
                    if (extractedFolder.Equals(archiveDir, StringComparison.OrdinalIgnoreCase))
                    {
                        AppendLog($"[批量智能路径] 顶级项 {item.FileName} 使用 ArchiveDir 模式，跳过扁平化", ConsoleColor.Gray);
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
                foreach (var extractedFolder in extractedFolders)
                {
                    _ = Task.Run(() => ProcessSmartPathBatchAsync(extractedFolder));
                }
            }
        }

        /// <summary>
        /// 在智能路径扁平化之前，对解压目录递归删除黑名单匹配的文件和文件夹（含解压残留的压缩包）。
        /// 否则同级黑名单文件或文件夹会导致「恰好一个子目录且无文件」条件不满足而无法扁平化。
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
                
                // 先删除黑名单文件
                foreach (string file in Directory.GetFiles(rootDir, "*.*", SearchOption.AllDirectories))
                {
                    if (!File.Exists(file))
                        continue;
                    if (IsBlacklistedFile(file, out string matchedPattern))
                        DeleteBlacklistedFile(file, matchedPattern);
                }
                
                // 再删除黑名单文件夹（从最深层次开始删除，避免父目录还有内容时无法删除）
                var allDirs = Directory.GetDirectories(rootDir, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length); // 从最深层次开始删除
                
                foreach (string dir in allDirs)
                {
                    if (!Directory.Exists(dir))
                        continue;
                    if (IsBlacklistedFile(dir, out string matchedPattern))
                    {
                        try
                        {
                            Directory.Delete(dir, true); // 递归删除目录及其所有内容
                            AppendLog($"[批量智能路径] [黑名单] 已删除文件夹: {Path.GetFileName(dir)} (匹配模式: {matchedPattern})", ConsoleColor.Yellow);
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"[批量智能路径] [黑名单] 删除文件夹失败: {Path.GetFileName(dir)} - {ex.Message}", ConsoleColor.Red);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[批量智能路径] 扁平化前黑名单扫描失败: {ex.Message}", ConsoleColor.Yellow);
            }
        }

        /// <summary>
        /// 在智能路径扁平化之前，清理所有子压缩包文件。
        /// 子压缩包不受 FileAfterExtract 设置影响，一律直接删除。
        /// 这是阶段1清理的补充，确保扫描目录中所有子压缩包并清理，而不仅仅依赖文件树记录。
        /// </summary>
        private async Task DeleteChildArchivesUnderDirectoryAsync(string rootDir)
        {
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
                    if (IsArchiveFile(file, false) && !IsMultiVolumeArchive(file))
                    {
                        try
                        {
                            // 直接删除（不经过回收站），子压缩包不保留
                            await Task.Run(() => File.Delete(file));
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
        /// 同步处理批量智能路径扁平化（等待所有操作完成）
        /// 与 ProcessBatchSmartPathProcessingAsync 不同，这个方法会等待所有扁平化操作完成
        /// </summary>
        private async Task ProcessBatchSmartPathSynchronous(string topArchivePath)
        {
            AppendLog($"[同步批量智能路径] [DEBUG] 开始检查条件...", ConsoleColor.Gray);
            AppendLog($"[同步批量智能路径] [DEBUG] topArchivePath={topArchivePath}", ConsoleColor.Gray);
                    
            if (!_settings.EnableSmartPathProcessing || _settings.OutputMode != OutputMode.ArchiveFolder)
            {
                AppendLog($"[同步批量智能路径] 条件不满足，跳过处理 (EnableSmartPathProcessing={_settings.EnableSmartPathProcessing}, OutputMode={_settings.OutputMode})", ConsoleColor.Yellow);
                return;
            }
        
            // 【关键修复】遍历所有解压目录，而不是只处理顶级压缩包的输出目录
            // 这样可以确保子压缩包的解压目录也能被扁平化处理
            var extractedDirs = _extractedDirectoryNodes.ToList();
            AppendLog($"[同步批量智能路径] [DEBUG] 找到 {extractedDirs.Count} 个解压目录节点", ConsoleColor.Gray);
            
            // 打印所有解压目录，方便调试
            foreach (var dir in extractedDirs)
            {
                AppendLog($"[同步批量智能路径] [DEBUG]   - {dir}", ConsoleColor.Gray);
            }
            
            // 清空已处理标记（每次批量处理都是独立的）
            _processedFlattenDirs.Clear();
            
            int processedCount = 0;
            foreach (var outputDir in extractedDirs)
            {
                if (!Directory.Exists(outputDir))
                {
                    AppendLog($"[同步批量智能路径] [DEBUG] 目录不存在，跳过: {outputDir}", ConsoleColor.Gray);
                    continue;
                }
            
                // 对于 ArchiveDir 模式（压到当前目录），不进行扁平化处理
                string archiveDir = Path.GetDirectoryName(topArchivePath) ?? string.Empty;
                
                // 检查该目录是否是 ArchiveDir 模式（即 outputDir == archiveDir）
                if (outputDir.Equals(archiveDir, StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog($"[同步批量智能路径] [DEBUG] 目录是 ArchiveDir 模式，跳过扁平化: {outputDir}", ConsoleColor.Gray);
                    AppendLog($"[同步批量智能路径] [DEBUG]   topArchivePath={topArchivePath}, archiveDir={archiveDir}", ConsoleColor.Gray);
                    continue;
                }
                
                // 检查是否已经被处理过（防止重复处理）
                if (_processedFlattenDirs.Contains(outputDir))
                {
                    AppendLog($"[同步批量智能路径] [DEBUG] 目录已处理过，跳过: {outputDir}", ConsoleColor.Gray);
                    continue;
                }
            
                AppendLog($"[同步批量智能路径] 开始处理: {outputDir}", ConsoleColor.Cyan);
            
                // 同步处理这个觧压生成的文件夹（等待完成）
                await Task.Run(() => ProcessSmartPathBatchAsync(outputDir));
                
                // 标记为已处理
                _processedFlattenDirs.Add(outputDir);
                processedCount++;
            }
        
            AppendLog($"[同步批量智能路径] ✓ 共处理 {processedCount}/{extractedDirs.Count} 个目录", ConsoleColor.Green);
        }

        /// <summary>
        /// 更新顶级FileItem的状态为"解压完成-扁平化处理完成"
        /// </summary>
        private void UpdateTopLevelItemCompletedStatus(string topArchivePath)
        {
            try
            {
                var topLevelItem = FindFileItemInTree(topArchivePath);
                if (topLevelItem == null)
                {
                    AppendLog($"[状态更新] 未找到顶级FileItem: {Path.GetFileName(topArchivePath)}", ConsoleColor.Yellow);
                    return;
                }

                var passwordInfo = topLevelItem.FoundPassword != null
                    ? $" (密码: {topLevelItem.FoundPassword})"
                    : " (无密码)";

                // 获取最终扁平化后的路径
                string outputDir = GetOutputDirectory(topArchivePath);
                string? finalPath = FindFinalFlattenedPath(outputDir);

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
        /// 批量处理单个输出目录的智能路径扁平化（从叶子节点向上遍历）
        /// </summary>
        private async Task ProcessSmartPathBatchAsync(string rootDir)
        {
            AppendLog($"[批量智能路径] [DEBUG] 进入 ProcessSmartPathBatchAsync, rootDir={rootDir}", ConsoleColor.Gray);
                    
            if (!Directory.Exists(rootDir))
            {
                AppendLog($"[批量智能路径] [DEBUG] rootDir 不存在，直接返回", ConsoleColor.Gray);
                return;
            }
        
            AppendLog($"[批量智能路径] 开始处理: {rootDir}", ConsoleColor.Cyan);
        
            // 步骤1: 清理黑名单文件
            AppendLog($"[批量智能路径] [DEBUG] 步骤1: 清理黑名单文件", ConsoleColor.Gray);
            DeleteBlacklistedFilesUnderDirectory(rootDir);
        
            // 步骤2: 后序遍历：先处理深层嵌套，再处理浅层
            AppendLog($"[批量智能路径] [DEBUG] 步骤2: 开始后序遍历", ConsoleColor.Gray);
            ProcessDirectoryTreePostOrder(rootDir);
        
            // 步骤3: 处理最外层目录（如果需要）
            // 当拖入的是文件夹且输出模式为ArchiveFolder时，检查是否需要扁平化最外层
            // rootDir 是解压文件夹（如 D:\写真\B-088\课件_003\课件_003）
            // 需要获取最外层文件夹（如 D:\写真\B-088\课件_003）
            string? topLevelFolder = Path.GetDirectoryName(rootDir);
            AppendLog($"[批量智能路径] [DEBUG] 步骤3: topLevelFolder={topLevelFolder}", ConsoleColor.Gray);
                    
            if (!string.IsNullOrEmpty(topLevelFolder) && Directory.Exists(topLevelFolder))
            {
                AppendLog($"[批量智能路径] [DEBUG] 开始处理最外层目录", ConsoleColor.Gray);
                ProcessTopLevelFolderIfNeeded(topLevelFolder);
            }
        
            AppendLog($"[批量智能路径] ✓ 处理完成: {Path.GetFileName(rootDir)}", ConsoleColor.Green);
                    
            // 更新所有相关的FileItem状态为"扁平化处理完成"
            UpdateSmartPathCompletedStatus(rootDir);
        }

        /// <summary>
        /// 更新扁平化处理完成后的FileItem状态
        /// </summary>
        private void UpdateSmartPathCompletedStatus(string rootDir)
        {
            try
            {
                // 找到所有输出目录在 rootDir 下的 FileItem
                var relatedItems = _fileList.Where(item => 
                {
                    string outputPath = GetOutputDirectory(item.FilePath);
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
                string? finalPath = FindFinalFlattenedPath(rootDir);
                AppendLog($"[批量智能路径] [状态更新] 最终路径: {finalPath}", ConsoleColor.Gray);

                foreach (var item in relatedItems)
                {
                    // 只更新顶级项或状态中包含“解压成功”的项
                    if (item.Parent == null || item.Status.Contains("解压成功"))
                    {
                        var passwordInfo = item.FoundPassword != null 
                            ? $" (密码: {item.FoundPassword})" 
                            : " (无密码)";
                        
                        // 记录最终路径到节点
                        item.FinalOutputPath = finalPath;
                                                
                        Dispatcher.Invoke(() =>
                        {
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
        /// 递归查找最深层的解压目录标记节点
        /// </summary>
        private string? FindDeepestMarkedNode(string dir)
        {
            if (!Directory.Exists(dir))
                return null;

            var subDirs = Directory.GetDirectories(dir);
            foreach (var subDir in subDirs)
            {
                if (_extractedDirectoryNodes.Contains(subDir))
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
        private string? FindFinalFlattenedPath(string rootDir)
        {
            AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath 开始: rootDir={rootDir}", ConsoleColor.Gray);
            
            try
            {
                // 如果 rootDir 不存在，说明扁平化时它被删除了
                // 需要查找扁平化后的实际目录（在父目录下查找被标记的解压节点）
                if (!Directory.Exists(rootDir))
                {
                    AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: rootDir 不存在 {rootDir}，查找扁平化后的目录", ConsoleColor.Gray);
                    
                    string? parentDir = Path.GetDirectoryName(rootDir);
                    if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir))
                    {
                        AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 父目录不存在，返回 rootDir {rootDir}", ConsoleColor.Gray);
                        return rootDir;
                    }
                    
                    // 在父目录下递归查找最深层的标记节点
                    string? deepestNode = FindDeepestMarkedNode(parentDir);
                    if (deepestNode != null)
                    {
                        AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 找到最深层的扁平化目录 {deepestNode}", ConsoleColor.Gray);
                        return deepestNode;
                    }
                    
                    AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 未找到标记节点，返回 rootDir {rootDir}", ConsoleColor.Gray);
                    return rootDir;
                }
                
                // rootDir 存在，递归查找其下最深层的标记节点
                string? deepest = FindDeepestMarkedNode(rootDir);
                if (deepest != null)
                {
                    AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 递归找到最深层的标记节点 {deepest}", ConsoleColor.Gray);
                    return deepest;
                }
                    
                var subDirsInRoot = Directory.GetDirectories(rootDir);
                
                AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: {rootDir}, 子目录数={subDirsInRoot.Length}", ConsoleColor.Gray);
                
                if (subDirsInRoot.Length == 1)
                {
                    // 只有一个子目录，说明已扁平化，返回该子目录
                    AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 返回子目录 {subDirsInRoot[0]}", ConsoleColor.Gray);
                    return subDirsInRoot[0];
                }
                else if (subDirsInRoot.Length == 0)
                {
                    // 没有子目录，返回 rootDir 本身
                    AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 返回 rootDir {rootDir}", ConsoleColor.Gray);
                    return rootDir;
                }
                else
                {
                    // 有多个子目录，递归查找最深层的标记节点
                    string? deepestNode = FindDeepestMarkedNode(rootDir);
                    if (deepestNode != null)
                    {
                        AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 找到最深层的标记节点 {deepestNode}", ConsoleColor.Gray);
                        return deepestNode;
                    }
                    
                    // 如果没有找到标记的节点，返回第一个子目录（如果有）或 rootDir
                    if (subDirsInRoot.Length > 0)
                    {
                        AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 返回第一个子目录 {subDirsInRoot[0]}", ConsoleColor.Gray);
                        return subDirsInRoot[0];
                    }
                    else
                    {
                        AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath: 返回 rootDir {rootDir}", ConsoleColor.Gray);
                        return rootDir;
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[批量智能路径] [DEBUG] FindFinalFlattenedPath 异常: {ex.Message}", ConsoleColor.Red);
                return rootDir;
            }
        }

        /// <summary>
        /// 后序遍历目录树，从叶子节点开始处理
        /// 注意：只处理压缩包解压出的文件夹结构，不处理非压缩包（如游戏应用）的原始结构
        /// </summary>
        private void ProcessDirectoryTreePostOrder(string dirPath, bool isInExtractedTree = false)
        {
            try
            {
                // 检查目录是否存在（可能在之前的扁平化操作中被删除）
                if (!Directory.Exists(dirPath))
                {
                    AppendLog($"[批量智能路径] 目录已不存在，跳过: {dirPath}", ConsoleColor.Gray);
                    return;
                }

                // 防止重复处理同一个目录（避免死循环）
                string normalizedPath = Path.GetFullPath(dirPath).TrimEnd(Path.DirectorySeparatorChar);
                if (_processedFlattenDirs.Contains(normalizedPath))
                {
                    AppendLog($"[批量智能路径] [DEBUG] 目录已处理过，跳过: {Path.GetFileName(dirPath)}", ConsoleColor.Gray);
                    return;
                }

                AppendLog($"[批量智能路径] [DEBUG] 开始遍历目录: {dirPath}", ConsoleColor.Gray);

                // 检查当前目录是否是被标记的解压目录节点，或者在解压树中
                bool isExtractedNode = isInExtractedTree || _extractedDirectoryNodes.Contains(dirPath);
                
                AppendLog($"[批量智能路径] [DEBUG] {Path.GetFileName(dirPath)}: isInExtractedTree={isInExtractedTree}, 在标记节点中={_extractedDirectoryNodes.Contains(dirPath)}, isExtractedNode={isExtractedNode}", ConsoleColor.Gray);
                
                // 1. 先递归处理所有子目录（深度优先）- 如果在解压树中，子目录也继承这个状态
                var subDirs = Directory.GetDirectories(dirPath);
                AppendLog($"[批量智能路径] [DEBUG] 找到 {subDirs.Length} 个子目录", ConsoleColor.Gray);
                
                foreach (var subDir in subDirs)
                {
                    AppendLog($"[批量智能路径] [DEBUG] 递归处理子目录: {Path.GetFileName(subDir)}", ConsoleColor.Gray);
                    // 对每个子目录的遍历进行独立异常处理
                    try
                    {
                        ProcessDirectoryTreePostOrder(subDir, isExtractedNode);  // 传递解压树状态
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
                    // 只有在解压树中的目录才进行扁平化
                    if (isExtractedNode)
                    {
                        AppendLog($"[批量智能路径] [DEBUG] 开始处理当前目录: {Path.GetFileName(dirPath)}", ConsoleColor.Gray);
                        ProcessSingleDirectoryIfNeeded(dirPath);
                        
                        // 标记为已处理（防止重复处理）
                        _processedFlattenDirs.Add(normalizedPath);  // 复用上面定义的 normalizedPath
                    }
                    else
                    {
                        AppendLog($"[批量智能路径] [DEBUG] {Path.GetFileName(dirPath)}: 非标记的解压节点，跳过扁平化", ConsoleColor.Gray);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[批量智能路径] 遍历目录失败 {dirPath}: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 递归检查目录中是否包含压缩包文件
        /// </summary>
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
                    if (IsArchiveFile(file, false))
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
                
                // 【新增】详细列出所有文件及其类型，用于调试
                foreach (var file in allFiles)
                {
                    bool isArchive = IsArchiveFile(file);
                    string fileName = Path.GetFileName(file);
                    AppendLog($"[批量智能路径] [DEBUG-文件列表] {fileName}: {(isArchive ? "压缩包" : "非压缩包")}", ConsoleColor.Gray);
                }

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
        /// 处理最外层目录：如果拖入的是文件夹，且解压后最外层目录只包含一个子目录，则扁平化
        /// </summary>
        private void ProcessTopLevelFolderIfNeeded(string rootDir)
        {
            try
            {
                // 再次检查目录是否存在（可能内层扁平化已经改变了结构）
                if (!Directory.Exists(rootDir))
                {
                    AppendLog($"[批量智能路径] [最外层检查] 目录已不存在，跳过: {rootDir}", ConsoleColor.Gray);
                    return;
                }

                // 检查最外层目录是否只有一个子目录
                var subDirs = Directory.GetDirectories(rootDir);
                var allFiles = Directory.GetFiles(rootDir);
                var archiveFiles = allFiles.Where(f => IsArchiveFile(f)).ToArray();
                var nonArchiveFiles = allFiles.Where(f => !IsArchiveFile(f)).ToArray();

                AppendLog($"[批量智能路径] [最外层检查] {Path.GetFileName(rootDir)}: {subDirs.Length} 个子目录, {archiveFiles.Length} 个压缩包, {nonArchiveFiles.Length} 个非压缩包文件", ConsoleColor.Gray);

                // 只有当最外层目录恰好一个子目录且只有压缩包文件时才扁平化
                if (subDirs.Length != 1 || nonArchiveFiles.Length > 0)
                {
                    if (subDirs.Length != 1)
                        AppendLog($"[批量智能路径] [最外层检查] 子目录数量不为1 ({subDirs.Length})，不处理最外层", ConsoleColor.Gray);
                    if (nonArchiveFiles.Length > 0)
                        AppendLog($"[批量智能路径] [最外层检查] 存在 {nonArchiveFiles.Length} 个非压缩包文件，不处理最外层", ConsoleColor.Gray);
                    return;
                }

                // 此时应该只有 1 个子目录和若干压缩包文件
                string singleSubDir = subDirs[0];
                string subDirName = Path.GetFileName(singleSubDir);
                string parentDirName = Path.GetFileName(rootDir);
                string? parentDirFullPath = Path.GetDirectoryName(rootDir);
                
                // 如果无法获取父目录路径，则跳过处理
                if (string.IsNullOrEmpty(parentDirFullPath))
                {
                    AppendLog($"[批量智能路径] [最外层扁平化] 无法获取父目录路径，跳过处理", ConsoleColor.Yellow);
                    return;
                }

                AppendLog($"[批量智能路径] [最外层扁平化] 检测到可扁平化: {parentDirName} -> {subDirName}", ConsoleColor.Cyan);

                // 构建新的路径：在父目录中创建新的文件夹
                string newPath = Path.Combine(parentDirFullPath, subDirName);

                // 检查目标路径是否已存在
                bool needRenameAfterMove = false;
                if (Directory.Exists(newPath))
                {
                    AppendLog($"[批量智能路径] [最外层扁平化] 目标目录已存在: {newPath}，添加临时前缀", ConsoleColor.Yellow);
                    // 添加临时GUID前缀避免冲突（后续会去掉）
                    string tempPrefix = Guid.NewGuid().ToString().Substring(0, 8);
                    newPath = Path.Combine(parentDirFullPath, $"{tempPrefix}_{subDirName}");
                    needRenameAfterMove = true; // 标记需要后续重命名
                }

                // 移动子目录到父目录的同级
                Directory.Move(singleSubDir, newPath);
                AppendLog($"[批量智能路径] [最外层扁平化] 已移动: {subDirName} -> {Path.GetFileName(newPath)}", ConsoleColor.Gray);

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
                    AppendLog($"[批量智能路径] [最外层扁平化] 已移动压缩包: {archiveFileName} -> {Path.GetFileName(destPath)}", ConsoleColor.Gray);
                }
                
                // 现在目录应该只有空文件夹了，可以安全删除
                Directory.Delete(rootDir);
                AppendLog($"[批量智能路径] [最外层扁平化] 已删除空目录: {parentDirName}", ConsoleColor.Gray);
                
                // 去掉压缩包的临时前缀
                foreach (var kvp in archivesToRename)
                {
                    string currentPath = kvp.Key;
                    string originalFileName = kvp.Value;
                    string finalPath = Path.Combine(parentDirFullPath, originalFileName);
                    
                    try
                    {
                        if (File.Exists(finalPath))
                        {
                            File.Delete(finalPath);
                            AppendLog($"[批量智能路径] [最外层扁平化] 已删除已存在的文件: {originalFileName}", ConsoleColor.Gray);
                        }
                        
                        File.Move(currentPath, finalPath);
                        AppendLog($"[批量智能路径] [最外层扁平化] ✓ 已重命名压缩包去掉临时前缀: {Path.GetFileName(currentPath)} -> {originalFileName}", ConsoleColor.Green);
                    }
                    catch (Exception renameEx)
                    {
                        AppendLog($"[批量智能路径] [最外层扁平化] ⚠ 重命名压缩包失败，保留临时名称: {renameEx.Message}", ConsoleColor.Yellow);
                    }
                }
                
                // 如果使用了临时前缀，现在去掉它
                if (needRenameAfterMove)
                {
                    string finalPath = Path.Combine(parentDirFullPath, subDirName);
                    try
                    {
                        if (Directory.Exists(finalPath))
                        {
                            Directory.Delete(finalPath, true);
                            AppendLog($"[批量智能路径] [最外层扁平化] 已删除已存在的最终目录: {subDirName}", ConsoleColor.Gray);
                        }
                        
                        Directory.Move(newPath, finalPath);
                        AppendLog($"[批量智能路径] [最外层扁平化] ✓ 已重命名去掉临时前缀: {Path.GetFileName(newPath)} -> {subDirName}", ConsoleColor.Green);
                        newPath = finalPath; // 更新路径为最终路径
                    }
                    catch (Exception renameEx)
                    {
                        AppendLog($"[批量智能路径] [最外层扁平化] ⚠ 重命名失败，保留临时名称: {renameEx.Message}", ConsoleColor.Yellow);
                    }
                }
                
                AppendLog($"[批量智能路径] [最外层扁平化] ✓ 扁平化完成: {Path.GetFileName(newPath)}", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"[批量智能路径] [最外层扁平化] 处理失败: {ex.Message}", ConsoleColor.Red);
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
                        AppendLog($"[批量智能路径]   已移动: {Path.GetFileName(childDir)} -> {tempName}", ConsoleColor.Gray);
                        
                        // 更新解压节点标记：将旧路径替换为新路径
                        if (_extractedDirectoryNodes.Contains(childDir))
                        {
                            _extractedDirectoryNodes.Remove(childDir);
                            _extractedDirectoryNodes.Add(tempPath);
                            AppendLog($"[批量智能路径] [DEBUG] 更新解压节点标记: {childDir} -> {tempPath}", ConsoleColor.Gray);
                        }
                    }
                    catch (IOException ioEx) when (retryCount < maxRetries - 1)
                    {
                        retryCount++;
                        AppendLog($"[批量智能路径] ⚠ 移动失败（文件占用？），重试 {retryCount}/{maxRetries}: {ioEx.Message}", ConsoleColor.Yellow);
                        Thread.Sleep(500); // 等待500ms后重试
                    }
                }
                
                if (!moveSuccess)
                {
                    AppendLog($"[批量智能路径] ✗ 移动失败，跳过扁平化: {Path.GetFileName(childDir)}", ConsoleColor.Red);
                    return;
                }

                // 步骤2: 删除空的父目录（可能因残留文件而失败，必须恢复临时目录）
                if (Directory.Exists(parentDir))
                {
                    try
                    {
                        Directory.Delete(parentDir);
                        AppendLog($"[批量智能路径]   已删除空目录: {Path.GetFileName(parentDir)}", ConsoleColor.Gray);
                    }
                    catch (Exception deleteEx)
                    {
                        // 目录非空（有其他残留文件），移动失败会导致GUID前缀目录成为孤儿残留
                        // 必须立即将临时目录恢复回原位置
                        AppendLog($"[批量智能路径] ⚠ 父目录非空，跳过删除: {deleteEx.Message}", ConsoleColor.Yellow);
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
                        
                        // 【修复】父目录非空，必须将临时目录恢复回原位置，避免GUID前缀目录残留
                        try
                        {
                            if (Directory.Exists(tempPath) && !Directory.Exists(childDir))
                            {
                                Directory.Move(tempPath, childDir);
                                AppendLog($"[批量智能路径]   已恢复临时目录到原始位置（父目录非空）", ConsoleColor.Yellow);
                                
                                // 还原解压节点标记
                                if (_extractedDirectoryNodes.Contains(tempPath))
                                {
                                    _extractedDirectoryNodes.Remove(tempPath);
                                    _extractedDirectoryNodes.Add(childDir);
                                }
                            }
                        }
                        catch (Exception restoreEx)
                        {
                            AppendLog($"[批量智能路径] ⚠ 恢复临时目录失败: {restoreEx.Message}", ConsoleColor.Red);
                        }
                        
                        return;  // 跳过步骤3，不执行扁平化
                    }
                }

                // 步骤3: 重命名为最终名称
                string finalPath = Path.Combine(parentDirPath, finalName);
                
                if (Directory.Exists(finalPath))
                {
                    Directory.Delete(finalPath, true);
                    AppendLog($"[批量智能路径]   已删除已存在的目录: {finalName}", ConsoleColor.Gray);
                }

                // 添加重试机制，防止文件占用导致卡住
                retryCount = 0;
                moveSuccess = false;
                
                while (retryCount < maxRetries && !moveSuccess)
                {
                    try
                    {
                        Directory.Move(tempPath, finalPath);
                        moveSuccess = true;
                        AppendLog($"[批量智能路径]   ✓ 扁平化完成: {finalName}", ConsoleColor.Green);
                        
                        // 更新解压节点标记：将临时路径替换为最终路径
                        if (_extractedDirectoryNodes.Contains(tempPath))
                        {
                            _extractedDirectoryNodes.Remove(tempPath);
                            _extractedDirectoryNodes.Add(finalPath);
                            AppendLog($"[批量智能路径] [DEBUG] 更新解压节点标记: {tempPath} -> {finalPath}", ConsoleColor.Gray);
                        }
                    }
                    catch (IOException ioEx) when (retryCount < maxRetries - 1)
                    {
                        retryCount++;
                        AppendLog($"[批量智能路径] ⚠ 重命名失败（文件占用？），重试 {retryCount}/{maxRetries}: {ioEx.Message}", ConsoleColor.Yellow);
                        Thread.Sleep(500); // 等待500ms后重试
                    }
                }
                
                if (!moveSuccess)
                {
                    AppendLog($"[批量智能路径] ✗ 重命名失败，保留临时目录: {tempName}", ConsoleColor.Red);
                    
                    // 尝试清理：将临时目录重命名回原始位置，避免残留
                    try
                    {
                        if (Directory.Exists(tempPath) && !Directory.Exists(childDir))
                        {
                            Directory.Move(tempPath, childDir);
                            AppendLog($"[批量智能路径]   已恢复临时目录到原始位置", ConsoleColor.Yellow);
                        }
                    }
                    catch (Exception restoreEx)
                    {
                        AppendLog($"[批量智能路径]  恢复临时目录失败: {restoreEx.Message}", ConsoleColor.Yellow);
                    }
                    
                    return;
                }

                // 步骤4: 扁平化后，重新处理新生成的目录（可能仍满足扁平化条件）
                if (Directory.Exists(finalPath))
                {
                    // 清除旧目录的已处理标记（因为目录结构已经改变）
                    string normalizedOldPath = Path.GetFullPath(parentDir).TrimEnd(Path.DirectorySeparatorChar);
                    _processedFlattenDirs.Remove(normalizedOldPath);
                    
                    AppendLog($"[批量智能路径] [DEBUG] 扁平化后重新处理: {finalName}", ConsoleColor.Gray);
                    ProcessDirectoryTreePostOrder(finalPath, true);  // 标记为解压树中的节点
                }
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

        /// <summary>
        /// 从 7z l -t# -slt 输出中检测隐藏压缩包的具体类型（如 zip/7z/rar/tar）
        /// 同时解析出存档内包含的真实文件列表（白名单），用于过滤 -t# 提取的误匹配垃圾
        /// 返回 null 表示非隐写文件或无法确定类型
        /// 结果会被缓存，同一文件只探测一次
        /// </summary>
        private string? DetectStegoArchiveType(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            if (_stegoArchiveTypeCache.TryGetValue(filePath, out string? cachedType))
                return cachedType;

            string? detectedType = null;
            HashSet<string> validFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrWhiteSpace(_settings.SevenZipPath) || !File.Exists(_settings.SevenZipPath))
                    return null;

                var startInfo = new ProcessStartInfo
                {
                    FileName = _settings.SevenZipPath,
                    Arguments = $"l -t# -slt -sccUTF-8 \"{filePath}\"",
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
                }
                else if (process.ExitCode == 0)
                {
                    string combined = output + Environment.NewLine + error;
                    string lower = combined.ToLowerInvariant();
                    
                    // 解析 "Type = zip" 行获取具体压缩格式
                    detectedType = ParseArchiveTypeFromOutput(combined);
                    
                    // 如果无法解析具体类型，使用 # 作为回退（提取时会用 -t# 哈希扫描）
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
                        }
                    }
                    
                    // 从 -slt 输出解析真实文件列表（白名单）
                    if (detectedType != null)
                    {
                        validFiles = ParseStegoFileListFromSltOutput(combined, filePath);
                        AppendLog($"  [隐写探测] {Path.GetFileName(filePath)}: 解析到 {validFiles.Count} 个白名单文件", ConsoleColor.Gray);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"  [隐写探测] {Path.GetFileName(filePath)}: {ex.Message}", ConsoleColor.Gray);
            }

            _stegoArchiveTypeCache[filePath] = detectedType;
            _stegoArchiveValidFilesCache[filePath] = validFiles;
            return detectedType;
        }

        /// <summary>
        /// 从 7z l -t# -slt 输出中解析真实包含的文件名列表（白名单）
        /// -slt 格式每行是 "Key = Value"，文件的 Path= 行即文件名
        /// 跳过第一个 Path=（存档自身路径），后续的 Path= 均为存档内文件
        /// </summary>
        private static HashSet<string> ParseStegoFileListFromSltOutput(string combined, string archivePath)
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
                
                // 取纯文件名
                string fileName = Path.GetFileName(pathValue);
                if (!string.IsNullOrEmpty(fileName))
                {
                    fileNames.Add(fileName);
                }
            }
            
            return fileNames;
        }

        /// <summary>
        /// 获取隐写文件内包含的真实文件白名单（已缓存）
        /// </summary>
        private HashSet<string>? GetStegoArchiveValidFiles(string filePath)
        {
            if (_stegoArchiveValidFilesCache.TryGetValue(filePath, out var files))
                return files;
            return null;
        }

        /// <summary>
        /// 用标准 7z l（不带 -t#）验证文件是否为真正的压缩包
        /// -t# 哈希扫描可能将媒体文件的二进制数据误识别为压缩包签名，
        /// 产生假阳性垃圾文件。标准 7z l 从文件头开始解析，不会误匹配。
        /// 返回 true 表示该文件是可通过标准方式列出内容的真正压缩包。
        /// </summary>
        private bool VerifyIsRealArchive(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                if (string.IsNullOrWhiteSpace(_settings.SevenZipPath) || !File.Exists(_settings.SevenZipPath))
                    return false;

                var startInfo = new ProcessStartInfo
                {
                    FileName = _settings.SevenZipPath,
                    Arguments = $"l -sccUTF-8 \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();

                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                // 退出码 0 表示成功列出内容 → 真正的压缩包
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从 7z l -t# 输出中解析 "Type = xxx" 行，提取压缩格式
        /// </summary>
        private static string? ParseArchiveTypeFromOutput(string output)
        {
            // 匹配 "Type = zip" 或 "Type = 7z" 等
            var match = System.Text.RegularExpressions.Regex.Match(
                output, @"Type\s*=\s*(\w+)", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            if (match.Success)
            {
                string type = match.Groups[1].Value.ToLowerInvariant();
                // 只返回已知支持的格式
                if (type == "zip" || type == "7z" || type == "rar" || type == "tar" ||
                    type == "gzip" || type == "gz" || type == "bzip2" || type == "bz2" ||
                    type == "xz" || type == "lzma" || type == "cab" || type == "arj" ||
                    type == "z" || type == "lzh" || type == "iso" || type == "wim")
                {
                    return type;
                }
            }
            return null;
        }

        private bool IsStegoFileBy7ZipProbe(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            if (_stegoProbeCache.TryGetValue(filePath, out bool cached))
                return cached;

            string? detectedType = DetectStegoArchiveType(filePath);
            bool detected = detectedType != null;
            _stegoProbeCache[filePath] = detected;
            return detected;
        }

        /// <param name="filePath">文件路径</param>
        /// <param name="logVerification">是否输出魔术数验证日志，默认 false</param>
        /// <returns>是否为压缩文件</returns>
        private bool IsArchiveFile(string filePath, bool logVerification = false, bool isDroppedFile = true)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            if (!File.Exists(filePath))
                return false;

            // 如果启用了强制压模式，跳过所有检测，直接返回 true
            // 但只对最初拖入的文件生效，解压后发现的新文件仍进行正常检测
            if (_settings.ForceExtractMode && isDroppedFile)
            {
                if (logVerification)
                    AppendLog($"  [强制模式] {Path.GetFileName(filePath)}: 跳过检测，直接视为压缩文件", ConsoleColor.Yellow);
                return true;
            }

            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            // 步骤1：获取排除的扩展名列表
            var excludedExtensions = _settings.GetExcludedExtensions();
            
            // 如果在排除列表中，直接返回 false，结束解压流程
            if (excludedExtensions.Contains(extension))
            {
                // 【修复】只对最初拖入的文件进行隐写探测
                // 解压过程中递归发现的新文件（如.mp4等媒体文件）是最终产物，不应作为隐写容器处理
                if (isDroppedFile && IsStegoDetectionActiveForFile(filePath))
                {
                    if (logVerification)
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
                if (IsArchiveFileByMagicNumber(filePath, extension))
                {
                    if (logVerification)
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
                if (IsArchiveFileByMagicNumber(filePath, extension))
                {
                    if (logVerification)
                        AppendLog($"  [确认] {Path.GetFileName(filePath)}: 魔术数检测通过，是压缩文件", ConsoleColor.Green);
                    return true;
                }
                else
                {
                    if (IsStegoDetectionActiveForFile(filePath))
                    {
                        if (logVerification)
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
        private bool IsArchiveFileByExtension(string filePath, bool isDroppedFile = true)
        {
            // 如果启用了强制觧压模式，跳过所有检测，直接返回 true
            // 但只对最初拖入的文件生效
            if (_settings.ForceExtractMode && isDroppedFile)
            {
                return true;
            }
        
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
        private bool IsArchiveFileByMagicNumber(string filePath, string? extension = null)
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
                            // 注意：很多 TAR 文件不是标准的 ustar 格式，所以扩展名匹配就放行
                            if (extension == ".tar")
                            {
                                // TAR 格式检测比较复杂，不同工具生成的 TAR 格式可能不同
                                // 如果扩展名是 .tar 且文件大小合理，直接放行
                                if (fs.Length >= 16)
                                {
                                    return true;
                                }
                            }
                            
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

            // 判断是否为隐写文件（视频容器等）
            bool isStegoCarrier = IsStegoCarrierExtension(archivePath) && _settings.EnableStegoDetection;

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
        ///       3_hidden_23.mp4 -> 3_hidden_23 (隐写文件)
        /// </summary>
        private string GetArchiveFolderName(string archivePath)
        {
            string fileName = Path.GetFileName(archivePath);
            
            // 对于隐写文件（视频容器），直接去掉扩展名
            bool isStegoCarrier = IsStegoCarrierExtension(archivePath) && _settings.EnableStegoDetection;
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
                    .Where(f => IsArchiveFile(f, false))
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
        /// 静默删除文件到回收站（不显示系统对话框）
        /// </summary>
        private async Task<bool> SilentDeleteToRecycleBinAsync(string filePath)
        {
            try
            {
                await Task.Run(() =>
                {
                    // 确保路径以双 null 结尾
                    string path = filePath + "\0";
                    
                    var fileOp = new SHFILEOPSTRUCT
                    {
                        hwnd = IntPtr.Zero,
                        wFunc = FO_DELETE,
                        pFrom = path,
                        pTo = string.Empty,
                        fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
                        fAnyOperationsAborted = false,
                        hNameMappings = IntPtr.Zero,
                        lpszProgressTitle = string.Empty
                    };

                    int result = SHFileOperation(ref fileOp);
                    
                    // 返回0表示成功
                    if (result != 0)
                    {
                        throw new Exception($"SHFileOperation failed with error code: {result}");
                    }
                });
                
                return true;
            }
            catch (Exception ex)
            {
                AppendLog($"  删除文件到回收站失败: {ex.Message}", ConsoleColor.Red);
                return false;
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
                            bool success = false;
                            int retryCount = 3;
                                                    
                            for (int i = 0; i < retryCount; i++)
                            {
                                // 尝试等待文件解锁（每次等待5秒）
                                bool unlocked = await WaitForFileUnlock(filePath, 5000);
                                                        
                                if (!unlocked)
                                {
                                    int randomWait = Random.Shared.Next(500, 2001); // 500-2000ms 随机等待
                                    AppendLog($"  原文件仍在占用中，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                    await Task.Delay(randomWait);
                                    continue;
                                }
                            
                                try
                                {
                                    // 使用Windows API静默删除到回收站
                                    AppendLog($"  文件已解锁，开始移动到回收站: {Path.GetFileName(filePath)}", ConsoleColor.Gray);
                                    success = await SilentDeleteToRecycleBinAsync(filePath);
                                    if (success)
                                    {
                                        break;
                                    }
                                    else
                                    {
                                        int randomWait = Random.Shared.Next(500, 2001); // 500-2000ms 随机等待
                                        AppendLog($"  删除失败，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                        await Task.Delay(randomWait);
                                    }
                                }
                                catch (IOException ex)
                                {
                                    int randomWait = Random.Shared.Next(500, 2001); // 500-2000ms 随机等待
                                    AppendLog($"  原文件被占用 ({ex.Message})，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                    await Task.Delay(randomWait);
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
                            bool success = false;
                            int retryCount = 5;  // 增加重试次数到5次
                                                    
                            for (int i = 0; i < retryCount; i++)
                            {
                                // 尝试等待文件解锁（增加等待时间到8秒）
                                bool unlocked = await WaitForFileUnlock(filePath, 8000);
                                                        
                                if (!unlocked)
                                {
                                    int randomWait = Random.Shared.Next(1000, 3001); // 1000-3000ms 随机等待
                                    AppendLog($"  原文件仍在占用中，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                    await Task.Delay(randomWait);
                                    continue;
                                }
                            
                                try
                                {
                                    await Task.Run(() => File.Delete(filePath));
                                    success = true;
                                    break;
                                }
                                catch (IOException ex)
                                {
                                    int randomWait = Random.Shared.Next(1000, 3001); // 1000-3000ms 随机等待
                                    AppendLog($"  原文件被占用 ({ex.Message})，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                    await Task.Delay(randomWait);
                                }
                            }

                            if (success)
                            {
                                AppendLog($"  原文件已删除: {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                            }
                            else
                            {
                                AppendLog($"   删除原文件失败（多次重试后仍被占用）: {Path.GetFileName(filePath)}", ConsoleColor.Red);
                                AppendLog($"  提示：请关闭可能占用该文件的程序（如资源管理器、杀毒软件等），然后手动删除", ConsoleColor.Gray);
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
                        bool success = false;
                        int retryCount = 3;
                        
                        for (int i = 0; i < retryCount; i++)
                        {
                            // 尝试等待文件解锁（每次等待5秒）
                            bool unlocked = await WaitForFileUnlock(volumePath, 5000);
                            
                            if (!unlocked)
                            {
                                int randomWait = Random.Shared.Next(500, 2001); // 500-2000ms 随机等待
                                AppendLog($"  分卷文件仍在占用中，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                                await Task.Delay(randomWait);
                                continue;
                            }

                            try
                            {
                                // 使用Windows API静默删除到回收站
                                success = await SilentDeleteToRecycleBinAsync(volumePath);
                                if (success)
                                {
                                    break;
                                }
                                else
                                {
                                    int randomWait = Random.Shared.Next(500, 2001); // 500-2000ms 随机等待
                                    AppendLog($"  删除失败，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                                    await Task.Delay(randomWait);
                                }
                            }
                            catch (IOException ex)
                            {
                                int randomWait = Random.Shared.Next(500, 2001); // 500-2000ms 随机等待
                                AppendLog($"  分卷文件被占用 ({ex.Message})，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                                await Task.Delay(randomWait);
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"  移动分卷失败 ({ex.GetType().Name}: {ex.Message}): {Path.GetFileName(volumePath)}", ConsoleColor.Red);
                                break;
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
                        // 等待文件解锁（每次等待5秒，符合规范）
                        if (!await WaitForFileUnlock(volumePath, 5000))
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
                Dispatcher.Invoke(() => TxtFinalPath.Text = "无");
            }
            else if (selectedItem is FileItem fileItem)
            {
                // 文件节点:显示该节点及所有子节点的日志
                logsToShow = fileItem.CollectAllLogs();
                
                // 调试：打印日志统计
                System.Diagnostics.Debug.WriteLine($"[文件节点日志统计] {fileItem.FileName} 收集到 {logsToShow.Count} 条日志");
                
                // 显示最终路径
                Dispatcher.Invoke(() =>
                {
                    if (!string.IsNullOrEmpty(fileItem.FinalOutputPath))
                    {
                        TxtFinalPath.Text = fileItem.FinalOutputPath;
                    }
                    else
                    {
                        TxtFinalPath.Text = "无";
                    }
                });
            }
            
            // 显示日志(不清空,直接重新渲染)
            DisplayFilteredLogs(logsToShow, clearFirst: true);
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

        private void AppendLog(string message, ConsoleColor color, FileItem? targetNode = null)
        {
            // 如果指定了目标节点,记录到该节点
            if (targetNode != null)
            {
                targetNode.AddLog(message, color);
            }
            
            // 实时显示到日志窗口(始终显示,不过滤)
            Dispatcher.Invoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                TxtLog.AppendText($"[{timestamp}]--> {message}\r\n");
                TxtLog.ScrollToEnd();
            });
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
        
        /// <summary>
        /// 当前解压层数（从拖入的文件开始计算，拖入的文件为第1层）
        /// </summary>
        public int CurrentDepth { get; set; } = 1;
        
        // 新增：子节点完成状态跟踪（用于反向传播）
        private int _expectedChildrenCount = 0;      // 预期的子节点总数
        private int _completedChildrenCount = 0;     // 已完成的子节点数
        private bool _isMarkedComplete = false;      // 是否已标记为完成（防止重复上报）
        private bool _selfExtractSuccess = false;    // 自身压是否成功
                
        /// <summary>
        /// 扁平化处理后的最终路径（用于在日志区域上方显示）
        /// </summary>
        public string? FinalOutputPath { get; set; }
        
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
        
        // 日志存储
        private List<LogEntry> _logs = new List<LogEntry>();
        private readonly object _logLock = new object();
        
        /// <summary>
        /// 节点关联的日志列表
        /// </summary>
        public List<LogEntry> Logs
        {
            get { lock (_logLock) return new List<LogEntry>(_logs); }
        }
        
        /// <summary>
        /// 添加日志到当前节点
        /// </summary>
        public void AddLog(string message, ConsoleColor color)
        {
            lock (_logLock)
            {
                _logs.Add(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Message = message,
                    Color = color
                });
            }
        }
        
        /// <summary>
        /// 递归收集当前节点及所有子节点的日志
        /// </summary>
        public List<LogEntry> CollectAllLogs()
        {
            var allLogs = new List<LogEntry>();
            
            lock (_logLock)
            {
                allLogs.AddRange(_logs);
            }
            
            // 递归收集子节点日志
            foreach (var child in Children)
            {
                allLogs.AddRange(child.CollectAllLogs());
            }
            
            // 按时间排序
            allLogs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            return allLogs;
        }
    }
    
    /// <summary>
    /// 日志条目
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; } = string.Empty;
        public ConsoleColor Color { get; set; }
    }
    
    /// <summary>
    /// 根节点类
    /// </summary>
    public class RootNode : System.ComponentModel.INotifyPropertyChanged
    {
        private string _title = string.Empty;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        
        public string Title
        {
            get => _title;
            set
            {
                _title = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Title)));
            }
        }
        
        public ObservableCollection<FileItem> Children { get; set; } = new ObservableCollection<FileItem>();
        
        public int FileCount
        {
            get
            {
                int count = 0;
                foreach (var child in Children)
                {
                    count += CountFilesInTree(child);
                }
                return count;
            }
        }
        
        private int CountFilesInTree(FileItem item)
        {
            int count = 1;
            foreach (var child in item.Children)
            {
                count += CountFilesInTree(child);
            }
            return count;
        }
        
        public void UpdateTitle()
        {
            Title = $"待解压任务(共{FileCount}个文件)";
        }
        
        /// <summary>
        /// 收集所有日志
        /// </summary>
        public List<LogEntry> CollectAllLogs()
        {
            var allLogs = new List<LogEntry>();
            foreach (var child in Children)
            {
                allLogs.AddRange(child.CollectAllLogs());
            }
            allLogs.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            return allLogs;
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








