using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.Core.Models;
using AutoUnpackTool.Core.Services;
using AutoUnpackTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoUnpackTool.ViewModels
{
    /// <summary>
    /// 主窗口视图模型：承载原 MainWindow.xaml.cs 的全部业务编排（拖拽、按钮、线程状态、日志过滤）。
    /// 与视图的契约只有三样：绑定属性、Command、以及后台线程上报用的 Apply* 回调。
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly Dispatcher _dispatcher;
        private readonly IDialogService _dialogs;
        private readonly LogHub _logHub;

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
        private readonly DroppedFolderService _droppedFolders;
        private readonly ExtractionPipeline _pipeline;
        private readonly FileCollector _collector;

        private bool _suppressBlacklistToggleEvent = true; // 初始化期间不响应开关变化

        // 以下状态由 ExtractionPipeline 持有，ViewModel 侧只转发真正需要在宿主读写的几项。
        // 转发别名每一个都是“窗口能直接动管线内部”的缺口，所以只保留有使用处的；
        // 复位路径已全部收敛到 ExtractionPipeline.ResetRunState()，不再从这里逐个改。
        private ConcurrentQueue<FileItem> _pendingQueue { get => _pipeline.PendingQueue; set => _pipeline.PendingQueue = value; }
        private ConcurrentQueue<FileItem> _extractQueue { get => _pipeline.ExtractQueue; set => _pipeline.ExtractQueue = value; }
        private bool _isTesting { get => _pipeline.IsTesting; set => _pipeline.IsTesting = value; }
        private bool _isExtracting { get => _pipeline.IsExtracting; set => _pipeline.IsExtracting = value; }
        private bool _isPaused { get => _pipeline.IsPaused; set => _pipeline.IsPaused = value; }
        private PasswordMap _passwordMap { get => _pipeline.PasswordMap; set => _pipeline.PasswordMap = value; } // 密码映射表

        // 【并发修复】ConcurrentDictionary 保证解压线程 Add 与收尾线程遍历/移除的线程安全
        // （值无意义，仅用作线程安全集合的占位）
        // 共享集合由 SmartPathService 持有，ViewModel 侧通过属性转发，避免大范围改名
        // （清理已完成节点时要逐条注销登记；整片清空则走 SmartPathService.ResetRunState）
        private ConcurrentDictionary<string, byte> _extractedDirectoryNodes => _smartPath.ExtractedDirectoryNodes;

        // 收尾串行锁不再转发给宿主：唯一还在用它的批量兜底扁平化已下沉到
        // SmartPathService，锁只在 Core 内部几个流程之间共用，窗口不需要碰它。

        #region 绑定状态

        /// <summary>日志条目（替代原 TextBox 追加文本，虚拟化 ListBox 呈现）</summary>
        public ObservableCollection<LogEntry> Logs => _logHub.Entries;

        /// <summary>日志面板当前视图范围（标题提示，避免不知道看的是谁的日志）</summary>
        [ObservableProperty]
        private string _logScopeText = "信息输出 · 全部日志";

        /// <summary>文件树根节点集合（绑定 TreeView）</summary>
        public ObservableCollection<RootNode> Roots { get; } = new ObservableCollection<RootNode>();

        /// <summary>当前选中的树节点（TreeView 无 SelectedItem 绑定，由 View 回填）</summary>
        [ObservableProperty]
        private object? _selectedItem;

        [ObservableProperty]
        private string _statusText = "状态: 未开始";

        /// <summary>状态颜色语义（Gray/Green/Orange），由 XAML DataTrigger 转成画刷</summary>
        [ObservableProperty]
        private string _statusColor = "Gray";

        [ObservableProperty]
        private string _mainActionText = "开始解压";

        [ObservableProperty]
        private bool _isMainActionEnabled = true;

        [ObservableProperty]
        private bool _isBusy;

        /// <summary>
        /// 第二行清理按钮的文案：空闲时是「清理已完成」，运行中自动变成「强制中止」。
        /// 强制中止只在真正需要它的时刻（有任务在跑、可能卡住）出现，空闲时把位置让给日常清理。
        /// </summary>
        public string ClearButtonLabel => IsBusy ? "强制中止" : "清理已完成";

        partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ClearButtonLabel));

        private bool _isPausedBinding;
        /// <summary>线程是否处于暂停态（供绑定/诊断使用）</summary>
        public bool IsPaused
        {
            get => _isPausedBinding;
            set => SetProperty(ref _isPausedBinding, value);
        }

        [ObservableProperty]
        private bool _isAutoMode;

        [ObservableProperty]
        private bool _isManualMode = true;

        [ObservableProperty]
        private bool _isForceExtractMode;

        [ObservableProperty]
        private bool _isBlacklistEnabled;

        /// <summary>安装包解压模式（主界面快速开关，与设置对话框绑定同一 AppSettings 字段）</summary>
        [ObservableProperty]
        private bool _isInstallerExtractMode;

        /// <summary>快速密码测试（主界面快速开关，对应 AppSettings.FastPasswordTest）</summary>
        [ObservableProperty]
        private bool _isFastPasswordTest;

        [ObservableProperty]
        private int _extractProgress;

        [ObservableProperty]
        private string _currentPasswordText = "测试中的密码: 无";

        [ObservableProperty]
        private string _finalPathText = "无";

        [ObservableProperty]
        private string _testThreadText = "测试线程: 空闲";

        [ObservableProperty]
        private string _extractThreadText = "解压线程: 空闲";

        [ObservableProperty]
        private string _outputDirectory = string.Empty;

        [ObservableProperty]
        private string _maxExtractDepthText = "不限制";

        // 输出产物重名策略不再在这里暴露：已移到“软件设置 → 基础设置”，
        // 由 SettingsViewModel.DuplicatePolicyIndex 写入 AppSettings.DuplicateFileAction，ConflictResolver 实时读取。

        /// <summary>Dispatcher（UiTaskObserver 需要它做 UI 线程转投）</summary>
        public Dispatcher Dispatcher => _dispatcher;

        #endregion

        public MainViewModel(
            AppSettings settings,
            IAppLogger logger,
            IDialogService dialogs,
            LogHub logHub,
            Dispatcher dispatcher,
            BlacklistService blacklistService,
            ArchiveNameCleaner archiveNameCleaner,
            VolumeDetector volumeDetector,
            ArchiveDetector detector,
            StegoService stego,
            FileCleanupService cleanup,
            SmartPathService smartPath,
            ConflictResolver conflicts,
            FileOperationService fileOps,
            SevenZipExtractorFactory extractorFactory,
            DroppedFolderService droppedFolders)
        {
            _settings = settings;
            _logger = logger;
            _dialogs = dialogs;
            _logHub = logHub;
            _dispatcher = dispatcher;
            _blacklistService = blacklistService;
            _archiveNameCleaner = archiveNameCleaner;
            _volumeDetector = volumeDetector;
            _detector = detector;
            _stego = stego;
            _cleanup = cleanup;
            _smartPath = smartPath;
            _droppedFolders = droppedFolders;

            Roots.Add(_rootNode);

            // 智能路径（扁平化）处理；状态回调留在 ViewModel 内（需要 Dispatcher 封送）
            _smartPath.OnSmartPathCompleted = UpdateSmartPathCompletedStatus;

            // 拖入文件夹的收尾：忙判断直接在调用点算（不再用属性注入的委托），
            // 收尾结果回写到“最终路径”显示（ApplyFinalPath 内部已做 UI 封送）。
            _droppedFolders.OnFinalPathResolved = ApplyFinalPath;

            // 解压管线：双队列 + 递归扫描 + 收尾；UI 交互通过 ITaskObserver 上报
            _pipeline = new ExtractionPipeline(_settings, _logger, new UiTaskObserver(this),
                _detector, _stego, _archiveNameCleaner, _blacklistService, _cleanup, _smartPath, _volumeDetector, conflicts, fileOps, extractorFactory, _droppedFolders);
            _pipeline.AttachFileList(_fileList);

            // 文件收集（拖拽/目录扫描 → 文件树）
            _collector = new FileCollector(_settings, _logger, _detector, _volumeDetector, _archiveNameCleaner, _blacklistService, fileOps);
            _collector.AttachFileList(_fileList);
            _collector.OnTitleChanged = UpdateRootNodeTitle;
            _smartPath.GetAllItemsSnapshot = _pipeline.SnapshotAllItems;
            _pipeline.OnTopLevelCompleted = UpdateTopLevelItemCompletedStatus;

            _logger.Log("系统就绪，请拖拽压缩文件到窗口...", ConsoleColor.Green);

            // 如果有设置，显示提示
            if (!string.IsNullOrEmpty(_settings.SevenZipPath))
            {
                _logger.Log($"已加载配置: 7z.exe = {_settings.SevenZipPath}", ConsoleColor.Cyan);

                // 检查是否是 NanaZip 的 WindowsApps 路径
                if (_settings.SevenZipPath.Contains("WindowsApps") || _settings.SevenZipPath.Contains("NanaZip"))
                {
                    _logger.Log("⚠️  警告: 检测到 NanaZip (UWP版本)", ConsoleColor.Yellow);
                    _logger.Log("   UWP应用可能有权限限制，建议使用标准 7-Zip", ConsoleColor.Yellow);
                    _logger.Log("   下载地址: https://www.7-zip.org/", ConsoleColor.Gray);
                }
            }
            else
            {
                _logger.Log("提示：请先在\"软件设置\"中配置 7z.exe 路径", ConsoleColor.Yellow);
            }

            // 根据设置初始化解压模式（等价于原 RdoAutoMode/RdoManualMode 的 IsChecked 赋值）
            if (_settings.ExtractMode == ExtractMode.Auto)
            {
                IsAutoMode = true;
                IsManualMode = false;
            }
            else
            {
                IsAutoMode = false;
                IsManualMode = true;
            }

            // 初始化UI状态
            UpdateUiState();

            // 初始化强制解压模式复选框状态
            IsForceExtractMode = _settings.ForceExtractMode;

            // 初始化安装包解压模式 / 快速密码测试复选框状态
            IsInstallerExtractMode = _settings.InstallerExtractMode;
            IsFastPasswordTest = _settings.FastPasswordTest;

            // 初始化黑名单启用开关（SkipBlacklist 取反）
            _suppressBlacklistToggleEvent = true;
            IsBlacklistEnabled = !_settings.SkipBlacklist;
            _suppressBlacklistToggleEvent = false;

            // 初始化最大解压层数下拉框
            InitMaxExtractDepthComboBox();
        }

        #region 绑定属性变更（等价于原 XAML 事件处理）

        partial void OnIsAutoModeChanged(bool value)
        {
            if (!value)
                return;

            IsManualMode = false;
            ApplyExtractMode(ExtractMode.Auto);
        }

        partial void OnIsManualModeChanged(bool value)
        {
            if (!value)
                return;

            IsAutoMode = false;
            ApplyExtractMode(ExtractMode.Manual);
        }

        private void ApplyExtractMode(ExtractMode mode)
        {
            _settings.ExtractMode = mode;
            _settings.Save(); // 保存配置到文件
            UpdateUiState();

            // 切换到自动模式时，检查是否有待解压的文件
            if (mode == ExtractMode.Auto)
            {
                CheckAndTriggerAutoExtract();
            }
        }

        partial void OnIsForceExtractModeChanged(bool value)
        {
            _settings.ForceExtractMode = value;
            _settings.Save();

            if (_settings.ForceExtractMode)
            {
                _logger.Log("已启用强制解压模式：所有拖入的文件将跳过检测直接尝试觧压", ConsoleColor.Yellow);
            }
            else
            {
                _logger.Log("已关闭强制解压模式：恢复正常的压缩文件检测", ConsoleColor.Cyan);
            }
        }

        partial void OnIsBlacklistEnabledChanged(bool value)
        {
            if (_suppressBlacklistToggleEvent)
                return;

            _settings.SkipBlacklist = !value;
            _settings.Save();

            if (_settings.SkipBlacklist)
            {
                _logger.Log("已停用黑名单：匹配的文件将不会被删除", ConsoleColor.Yellow);
            }
            else
            {
                _logger.Log($"已启用黑名单：匹配的文件将被永久删除（{GetEnabledBlacklistRuleCount()} 条规则）", ConsoleColor.Cyan);
            }
        }

        partial void OnIsInstallerExtractModeChanged(bool value)
        {
            _settings.InstallerExtractMode = value;
            _settings.Save();

            if (_settings.InstallerExtractMode)
            {
                _logger.Log("已启用安装包解压模式：产物跳过黑名单与智能路径扁平化，仅识别分卷/隐写产物", ConsoleColor.Yellow);
            }
            else
            {
                _logger.Log("已关闭安装包解压模式：产物恢复黑名单清理与智能路径处理", ConsoleColor.Cyan);
            }
        }

        partial void OnIsFastPasswordTestChanged(bool value)
        {
            _settings.FastPasswordTest = value;
            _settings.Save();

            if (_settings.FastPasswordTest)
            {
                _logger.Log("已启用快速密码测试：先探测加密标志，未加密包免全量校验", ConsoleColor.Cyan);
            }
            else
            {
                _logger.Log("已关闭快速密码测试：恢复传统全量 7z t 密码校验", ConsoleColor.Cyan);
            }
        }

        #endregion

        #region 拖拽事件

        /// <summary>拖拽经过窗口：仅接受文件拖放</summary>
        public void HandleDragOver(DragEventArgs e)
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

        /// <summary>拖拽放下：收集压缩文件并启动双队列处理</summary>
        public void HandleDrop(IDataObject data)
        {
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])data.GetData(DataFormats.FileDrop);
                int addedCount = 0;
                var newFileItems = new List<FileItem>();  // 记录新添加的文件

                foreach (string path in files)
                {
                    // 判断是文件还是文件夹
                    if (File.Exists(path))
                    {
                        // 处理单个文件：只处理该文件及其分卷，不扫描同级目录的其他压缩文件
                        _collector.ProcessSingleFile(path, ref addedCount, newFileItems);
                    }
                    else if (Directory.Exists(path))
                    {
                        // 处理文件夹：只扫描该文件夹内部（包括子文件夹），不处理同级目录的其他文件或文件夹
                        _logger.Log($"检测到文件夹: {path}", ConsoleColor.Cyan);

                        // 记录拖入的文件夹，即使没有压缩包也会进行智能路径处理
                        _droppedFolders.Register(path);

                        _collector.ScanDirectoryForArchives(path, ref addedCount, newFileItems);
                    }
                }

                if (addedCount > 0)
                {
                    _logger.Log($"已添加 {addedCount} 个压缩文件到列表", ConsoleColor.Blue);

                    // 将新文件添加到待处理队列（避免重复添加）
                    foreach (var fileItem in newFileItems)
                    {
                        _pendingQueue.Enqueue(fileItem);
                        _logger.Log($"  [队列] {fileItem.FileName} 已加入待测试队列", ConsoleColor.Gray);
                    }

                    _logger.Log("自动开始处理流程...", ConsoleColor.Cyan);

                    // 完全空闲则整体启动双队列，已有 worker 在跑就只叫醒测试侧
                    // （解压侧会在测试完成后被自动唤醒）——这套判断由管线自己拥有
                    _pipeline.KickoffAfterEnqueue();
                }
                else
                {
                    _logger.Log("未检测到压缩文件", ConsoleColor.Yellow);

                    // 即使没有压缩包，也要对拖入的文件夹做智能路径处理。
                    // 只在完全空闲时就地收尾（此时不会启动监控循环，没别人会接手）；
                    // 若还有任务在跑，登记留在集合里由流程结束时的收尾处理，
                    // 避免抢在解压完成前动目录，也避免把其他正在处理的文件夹一起提前扁平化。
                    _droppedFolders.ScheduleFinalize("拖入时未扫到压缩包", isBusy: _isTesting || _isExtracting);
                }
            }
        }

        #endregion

        #region 命令

        /// <summary>选择解压输出目录</summary>
        [RelayCommand]
        private void SelectOutputDirectory()
        {
            var folder = _dialogs.SelectFolder("选择解压输出目录");
            if (folder != null)
            {
                OutputDirectory = folder;
                _logger.Log($"输出目录设置为: {folder}", ConsoleColor.Cyan);
            }
        }

        /// <summary>
        /// 统一操作按钮（根据状态自动切换功能：继续 / 暂停 / 开始解压）
        /// </summary>
        [RelayCommand]
        private void MainAction()
        {
            if (_isPaused)
            {
                // 暂停状态 → 继续
                _logger.Log("\n继续处理...", ConsoleColor.Cyan);
                _pipeline.ResumeProcessing();
                UpdateUiState();
            }
            else if (_isTesting || _isExtracting)
            {
                // 运行中 → 暂停
                _logger.Log("\n正在暂停...（等待当前任务完成后停止）", ConsoleColor.Yellow);
                _isPaused = true;
                UpdateUiState();
            }
            else
            {
                // 未开始 → 开始解压
                StartExtract();
            }
        }

        /// <summary>开始/继续解压</summary>
        [RelayCommand]
        private void StartExtract()
        {
            // 自动模式下，检查是否有待处理的任务需要继续
            if (_settings.ExtractMode == ExtractMode.Auto)
            {
                // 如果处于暂停状态，先清除暂停标志
                if (_isPaused)
                {
                    _logger.Log("\n继续处理...", ConsoleColor.Cyan);
                    _pipeline.ResumeProcessing();

                    UpdateUiState();
                    return;
                }

                bool needTestThread = !_isTesting && !_pendingQueue.IsEmpty;
                bool needExtractThread = !_isExtracting && !_extractQueue.IsEmpty;

                if (!needTestThread && !needExtractThread)
                {
                    if (_isTesting || _isExtracting)
                        _logger.Log("处理流程已在运行中...", ConsoleColor.Yellow);
                    else
                        _logger.Log("队列为空，等待新文件...", ConsoleColor.Gray);
                    return;
                }

                _logger.Log("继续处理剩余任务...", ConsoleColor.Cyan);

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
                _dialogs.ShowWarning("请先添加文件！");
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.SevenZipPath) || !File.Exists(_settings.SevenZipPath))
            {
                _dialogs.ShowWarning("请先在\"软件设置\"中配置 7z.exe 路径！");
                return;
            }

            // 调试日志：输出密码映射表状态
            _logger.Log($"[调试] 密码映射表Count: {_passwordMap.Count}", ConsoleColor.Magenta);
            foreach (var fileItem in _fileList)
            {
                if (_passwordMap.TryGetValue(fileItem.FilePath, out var pwd))
                {
                    _logger.Log($"[调试] {fileItem.FileName} -> 密码: {pwd ?? "(无密码)"}", ConsoleColor.Magenta);
                }
                else
                {
                    _logger.Log($"[调试] {fileItem.FileName} -> 未找到密码", ConsoleColor.Magenta);
                }
            }

            if (_passwordMap.Count == 0)
            {
                _dialogs.ShowWarning("请先进行密码测试！");
                return;
            }

            // 启动双队列处理流程
            _pipeline.StartDualQueueProcessing();
        }

        /// <summary>
        /// 仅执行密码测试，不解压
        /// </summary>
        [RelayCommand]
        private void StartPasswordTestOnly()
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
        /// 打开密码本管理
        /// </summary>
        [RelayCommand]
        private void OpenPasswords()
        {
            if (_dialogs.ShowPasswords(_settings))
            {
                // 设置已保存
                _logger.Log("密码本已更新", ConsoleColor.Cyan);
            }
        }

        /// <summary>
        /// 打开软件设置
        /// </summary>
        [RelayCommand]
        private void OpenSettings()
        {
            var updated = _dialogs.ShowSettings(_settings);
            if (updated != null)
            {
                // 设置已保存，更新本地引用
                _settings = updated;
                _logger.Log("设置已更新", ConsoleColor.Cyan);
                _logger.Log($"7z.exe 路径: {_settings.SevenZipPath}", ConsoleColor.Cyan);
            }
        }

        /// <summary>
        /// 打开使用说明
        /// </summary>
        [RelayCommand]
        private void OpenHelp()
        {
            _dialogs.ShowHelp();
        }

        /// <summary>清理按钮统一入口：运行中 = 强制中止并复位，空闲 = 只清理已完成节点。</summary>
        [RelayCommand]
        private void SmartClear()
        {
            if (IsBusy)
                ForceReset();
            else
                CleanCompletedNodes();
        }

        /// <summary>
        /// 清理已完成节点：只摘掉已进入终态且整棵子树无在途任务的顶级节点。
        /// 不动 7z 进程、不清日志、不重置队列——那些是「强制中止」的职责。
        ///
        /// 顺带闭掉一个安全隐患：已完成任务留下的解压节点登记如果不清，日后用户在同一个路径
        /// 建了同名目录，工具会认为“这是自己生成的目录”而允许递归删除它。
        /// </summary>
        private void CleanCompletedNodes()
        {
            var finishedDirs = new List<string>();
            int removed = 0;

            for (int i = _fileList.Count - 1; i >= 0; i--)
            {
                var node = _fileList[i];
                if (node.HasInFlightInSubtree() || !node.IsFinished)
                    continue;

                CollectFinishedNodeDirs(node, finishedDirs);
                _fileList.RemoveAt(i);
                removed++;
            }

            if (removed == 0)
            {
                _logger.Log("没有已完成的节点可清理", ConsoleColor.Gray);
                return;
            }

            // 只动 ConcurrentDictionary 的解压节点登记：空闲时虽然不应有后台任务，但
            // FlattenedFinalPathMap / ProcessedFlattenDirs 是普通 Dictionary/HashSet，
            // 在 UI 线程改它们会引入数据竞争，不如只清真正涉及安全的那一个。
            // （整片清空由 SmartPathService.ResetRunState 在强制中止时负责）
            foreach (var dir in finishedDirs)
                _extractedDirectoryNodes.TryRemove(dir, out _);

            UpdateRootNodeTitle();
            _logger.Log($"已清理 {removed} 个已完成节点，列表剩余 {_fileList.Count} 项", ConsoleColor.Cyan);
        }

        /// <summary>递归收集节点及其子树的解压输出目录（即可能留在登记表里的键）。</summary>
        private void CollectFinishedNodeDirs(FileItem node, List<string> sink)
        {
            try
            {
                string outputDir = _detector.GetOutputDirectory(node.FilePath);
                if (!string.IsNullOrEmpty(outputDir))
                    sink.Add(outputDir);
            }
            catch
            {
                // 路径已不存在等情况不影响清理，少清一个登记只会让工具更保守
            }

            if (!string.IsNullOrEmpty(node.FinalOutputPath))
                sink.Add(node.FinalOutputPath!);

            foreach (var child in node.Children)
                CollectFinishedNodeDirs(child, sink);
        }

        /// <summary>强制中止并复位到初始状态（原「清空内容」，运行中卡住时的唯一逃生口）</summary>
        private void ForceReset()
        {
            // 1. 取消所有正在运行的操作
            _pipeline.CancelAllOperations();

            // 2. 等待一小段时间让线程退出
            Task.Delay(500).Wait();

            // 3. Kill掉所有7z进程
            _cleanup.KillSevenZipProcesses();

            // 4. Kill掉所有NanaZip进程
            _cleanup.KillNanaZipProcesses();

            // 5. 本轮状态一律交给管线复位（清单在 ExtractionPipeline.ResetRunState，只有一处），
            //    日志面板与根节点标题是本类自己的东西
            _pipeline.ResetRunState();
            _logHub.Clear();
            UpdateRootNodeTitle();

            _logger.Log("已强制中止并复位到初始状态（列表、队列、缓存与日志已清空，7z/NanaZip 进程已结束）", ConsoleColor.Yellow);

            // 6. 把按钮/状态栏拉回空闲态
            UpdateUiState();
        }

        /// <summary>
        /// 窗口关闭：等待线程退出并清理 7z/NanaZip 进程
        /// </summary>
        public void Shutdown()
        {
            try
            {
                _logger.Log("\n正在关闭程序...", ConsoleColor.Yellow);

                // 2. 等待一小段时间让线程退出
                Task.Delay(500).Wait();

                // 3. Kill掉所有7z进程（包括 NanaZip）
                _cleanup.KillSevenZipProcesses();
                _cleanup.KillNanaZipProcesses();

                _logger.Log("程序已关闭", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                // 即使出错也允许关闭
                _logger.Log($"关闭时发生错误: {ex.Message}", ConsoleColor.Red);
            }
        }

        #endregion

        #region 最大解压层数

        /// <summary>
        /// 获取启用且模式非空的黑名单规则数量
        /// </summary>
        private int GetEnabledBlacklistRuleCount()
        {
            return _settings.BlacklistRules?.Count(r => r != null && r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern)) ?? 0;
        }

        /// <summary>
        /// 初始化最大解压层数下拉框文本
        /// </summary>
        private void InitMaxExtractDepthComboBox()
        {
            MaxExtractDepthText = FormatMaxExtractDepth(_settings.MaxExtractDepth);
        }

        private static string FormatMaxExtractDepth(int depth)
        {
            switch (depth)
            {
                case 0: return "不限制";
                case 1: return "1 层";
                case 2: return "2 层";
                case 3: return "3 层";
                case 5: return "5 层";
                case 10: return "10 层";
                default: return $"{depth} 层(自定义)";
            }
        }

        /// <summary>
        /// 保存解压层数设置
        /// </summary>
        private void SaveMaxExtractDepth(int depth)
        {
            _settings.MaxExtractDepth = depth;
            _settings.Save();
            MaxExtractDepthText = FormatMaxExtractDepth(depth);
            _logger.Log(depth == 0 ? "最大解压层数设置为: 不限制（递归解压所有嵌套压缩包）" : $"最大解压层数设置为: {depth} 层", ConsoleColor.Cyan);
        }

        /// <summary>
        /// 提交下拉框文本（预定义选项或自定义输入），等价于原 SelectionChanged + LostFocus
        /// </summary>
        public void CommitMaxExtractDepthText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            string value = text!.Trim();
            if (value == "不限制")
            {
                SaveMaxExtractDepth(0);
                return;
            }

            string numeric = value.Replace(" 层(自定义)", string.Empty).Replace(" 层", string.Empty).Trim();
            if (int.TryParse(numeric, out int depth) && depth >= 0)
            {
                SaveMaxExtractDepth(depth);
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
            _dispatcher.Invoke(() =>
            {
                bool isAutoMode = _settings.ExtractMode == ExtractMode.Auto;
                bool isProcessing = isTesting || isExtracting;

                IsBusy = isProcessing;
                IsPaused = isPaused;

                // 更新顶部状态栏
                if (isPaused)
                {
                    StatusText = "状态: 已暂停";
                    StatusColor = "Orange";
                }
                else if (isProcessing)
                {
                    StatusText = "状态: 运行中";
                    StatusColor = "Green";
                }
                else
                {
                    StatusText = "状态: 未开始";
                    StatusColor = "Gray";
                }

                // 更新统一操作按钮
                if (isPaused)
                {
                    MainActionText = "继续";
                }
                else if (isProcessing)
                {
                    MainActionText = "暂停";
                }
                else
                {
                    MainActionText = "开始解压";
                }

                // 手动模式下，未开始时才启用按钮；自动模式始终启用
                IsMainActionEnabled = isAutoMode || !isProcessing;

                TestThreadText = $"测试线程: {(isTesting ? (isPaused ? "已暂停" : "工作中") : "空闲")}";
                ExtractThreadText = $"解压线程: {(isExtracting ? (isPaused ? "已暂停" : "工作中") : "空闲")}";
            });
        }

        /// <summary>
        /// 强制把状态栏置为“未开始”（ITaskObserver.OnIdleStatus 的落地实现）
        /// </summary>
        public void ApplyIdleStatus()
        {
            _dispatcher.Invoke(() =>
            {
                StatusText = "状态: 未开始";
                StatusColor = "Gray";
            });
        }

        /// <summary>解压进度（ITaskObserver.OnExtractProgress 的落地实现）</summary>
        public void ApplyExtractProgress(int percent)
        {
            _dispatcher.Invoke(() =>
            {
                ExtractProgress = percent;
            });
        }

        /// <summary>当前测试密码（ITaskObserver.OnCurrentPassword 的落地实现）</summary>
        public void ApplyCurrentPassword(string text)
        {
            _dispatcher.Invoke(() =>
            {
                CurrentPasswordText = text;
            });
        }

        /// <summary>最终输出路径（ITaskObserver.OnFinalPath 的落地实现）</summary>
        public void ApplyFinalPath(string text)
        {
            _dispatcher.Invoke(() =>
            {
                FinalPathText = text;
            });
        }

        /// <summary>把动作封送到 UI 线程（等价于原 Dispatcher.Invoke）</summary>
        public void InvokeOnUi(Action action) => _dispatcher.Invoke(action);

        /// <summary>当前是否已在 UI 线程（等价于原 Dispatcher.CheckAccess）</summary>
        public bool CheckUiAccess() => _dispatcher.CheckAccess();

        /// <summary>
        /// 检查是否有待解压的文件，并在自动模式下触发解压
        /// </summary>
        private void CheckAndTriggerAutoExtract()
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
        /// 更新顶级FileItem的状态为"解压完成-扁平化处理完成"
        /// </summary>
        public void UpdateTopLevelItemCompletedStatus(string topArchivePath)
        {
            try
            {
                var topLevelItem = _pipeline.FindFileItemInTreeThreadSafe(topArchivePath);
                if (topLevelItem == null)
                {
                    _logger.Log($"[状态更新] 未找到顶级FileItem: {Path.GetFileName(topArchivePath)}", ConsoleColor.Yellow);
                    return;
                }

                var passwordInfo = topLevelItem.PasswordSuffix;

                // 获取最终扁平化后的路径
                // 直接使用 FindFinalFlattenedPath，它内部已处理目录不存在/被扁平化重命名等所有情况
                string outputDir = _detector.GetOutputDirectory(topArchivePath);
                _logger.Log($"[状态更新] [DEBUG] outputDir(计算值)={outputDir}, 存在={Directory.Exists(outputDir)}", ConsoleColor.Gray);

                string? finalPath = _smartPath.FindFinalFlattenedPath(outputDir);
                _logger.Log($"[状态更新] [DEBUG] FindFinalFlattenedPath({outputDir}) = {finalPath}", ConsoleColor.Gray);

                _dispatcher.Invoke(() =>
                {
                    topLevelItem.Status = $"解压完成-扁平化处理完成{passwordInfo}";
                    topLevelItem.FinalOutputPath = finalPath;

                    // 如果当前选中的是该节点，立即更新最终路径显示
                    if (SelectedItem is FileItem selectedItem && selectedItem == topLevelItem)
                    {
                        FinalPathText = finalPath ?? "无";
                    }
                });

                _logger.Log($"[{topLevelItem.FileName}] 状态已更新: 解压完成-扁平化处理完成", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                _logger.Log($"[状态更新] 失败: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 更新扁平化处理完成后的FileItem状态
        /// </summary>
        public void UpdateSmartPathCompletedStatus(string rootDir)
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
                    _logger.Log($"[批量智能路径] [状态更新] 未找到相关的FileItem", ConsoleColor.Gray);
                    return;
                }

                _logger.Log($"[批量智能路径] [状态更新] 找到 {relatedItems.Count} 个相关的FileItem", ConsoleColor.Gray);

                // 获取最终扁平化后的路径
                string? finalPath = _smartPath.FindFinalFlattenedPath(rootDir);
                _logger.Log($"[批量智能路径] [状态更新] 最终路径: {finalPath}", ConsoleColor.Gray);

                foreach (var item in relatedItems)
                {
                    // 【修复】跳过顶级项（Parent == null）——这些会由 UpdateTopLevelItemCompletedStatus 统一处理
                    // 只更新非顶级项中状态包含"解压成功"的子压缩包节点
                    if (item.Parent != null && item.Status.Contains("解压成功"))
                    {
                        var passwordInfo = item.PasswordSuffix;

                        _dispatcher.Invoke(() =>
                        {
                            // 【修复】FinalOutputPath 需要在 UI 线程设置（触发 OnPropertyChanged 通知）
                            item.FinalOutputPath = finalPath;
                            item.Status = $"解压完成-扁平化处理完成{passwordInfo}";

                            // 如果当前选中的是该节点，立即更新最终路径显示
                            if (SelectedItem is FileItem selectedItem && selectedItem == item)
                            {
                                FinalPathText = finalPath ?? "无";
                            }
                        });

                        _logger.Log($"[{item.FileName}] 状态已更新: 解压完成-扁平化处理完成", ConsoleColor.Green, item);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"[批量智能路径] [状态更新] 失败: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// TreeView 选择改变（View 侧回调）：切换日志视图。
        /// 根节点/无选中 = 全部日志（含不属于任何节点的系统日志）；
        /// 文件节点 = 该节点及其整棵子树的日志（包含自动归属到它的活动日志）。
        /// </summary>
        public void OnTreeSelectionChanged(object? selectedItem)
        {
            SelectedItem = selectedItem;

            if (selectedItem is FileItem fileItem)
            {
                // 沿 Parent 链回溯判定归属：只读引用字段，不枚举 Children（Children 是 UI 绑定的集合，
                // 而过滤谓词会在后台线程的 Append 里执行）
                _logHub.SetViewFilter(entry => BelongsToNode(entry.Owner, fileItem));
                LogScopeText = $"信息输出 · {fileItem.FileName} 及其子项";

                // 显示最终路径：优先使用节点的 FinalOutputPath，其次沿父链向上查找
                FinalPathText = ResolveFinalPathForDisplay(fileItem) ?? "无";
            }
            else
            {
                // 根节点或其他：显示全量日志，不再丢日志
                _logHub.SetViewFilter(null);
                LogScopeText = "信息输出 · 全部日志";
                FinalPathText = "无";
            }
        }

        /// <summary>
        /// owner 是否为 node 自身或其子孙节点（沿 Parent 链上溯）。
        /// owner 为 null 的纯系统日志不属于任何节点，只在全部视图可见。
        /// </summary>
        private static bool BelongsToNode(FileItem? owner, FileItem node)
        {
            for (var current = owner; current != null; current = current.Parent)
            {
                if (ReferenceEquals(current, node))
                    return true;
            }
            return false;
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
        /// 更新根节点标题
        /// </summary>
        private void UpdateRootNodeTitle()
        {
            _rootNode.UpdateTitle();
        }

        #endregion
    }
}
