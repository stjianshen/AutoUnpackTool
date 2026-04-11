using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
        private bool _isTesting = false;
        private bool _isExtracting = false;
        
        // 任务完成信号
        private TaskCompletionSource<bool> _testCompletionSource = new();
        private TaskCompletionSource<bool> _extractCompletionSource = new();

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
                
                foreach (string file in files)
                {
                    if (File.Exists(file))
                    {
                        // 检测是否为压缩文件
                        if (IsArchiveFile(file))
                        {
                            // 如果是分卷压缩包，自动检测所有分卷
                            if (_settings.EnableMultiVolumeDetection && IsMultiVolumeArchive(file))
                            {
                                var volumeInfo = DetectMultiVolumeArchive(file);
                                
                                if (volumeInfo.IsMultiVolume)
                                {
                                    AppendLog($"检测到分卷压缩包: {volumeInfo.VolumeCount} 个分卷", ConsoleColor.Cyan);
                                    
                                    // 只添加主卷到列表
                                    var mainFileInfo = new FileInfo(volumeInfo.MainVolumePath);
                                    var fileItem = new FileItem
                                    {
                                        FileName = mainFileInfo.Name,
                                        FilePath = volumeInfo.MainVolumePath,
                                        Status = $"等待处理 (分卷: {volumeInfo.VolumeCount})",
                                        FileSize = volumeInfo.AllVolumePaths.Sum(p => new FileInfo(p).Length)
                                    };
                                    
                                    // 存储分卷信息
                                    fileItem.VolumeInfo = volumeInfo;
                                    
                                    if (!_fileList.Any(f => f.FilePath == volumeInfo.MainVolumePath))
                                    {
                                        _fileList.Add(fileItem);
                                        addedCount++;
                                    }
                                }
                                else
                                {
                                    // 不是真正的分卷，当作普通文件处理
                                    AddFileToList(file, ref addedCount);
                                }
                            }
                            else
                            {
                                AddFileToList(file, ref addedCount);
                            }
                        }
                        else
                        {
                            AppendLog($"跳过非压缩文件: {Path.GetFileName(file)}", ConsoleColor.Yellow);
                        }
                    }
                }

                if (addedCount > 0)
                {
                    AppendLog($"已添加 {addedCount} 个压缩文件到列表", ConsoleColor.Blue);
                    
                    // 将新文件添加到待处理队列
                    foreach (var file in files)
                    {
                        if (File.Exists(file) && IsArchiveFile(file))
                        {
                            var fileItem = _fileList.FirstOrDefault(f => f.FilePath == file);
                            if (fileItem != null)
                            {
                                _pendingQueue.Enqueue(fileItem);
                            }
                        }
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
        private void AddFileToList(string filePath, ref int addedCount)
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
            // 自动模式下此按钮应被禁用，这里作为防御性编程
            if (_settings.ExtractMode == ExtractMode.Auto)
            {
                AppendLog("自动模式下无需点击此按钮", ConsoleColor.Yellow);
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
            _fileList.Clear();
            _passwordMap = new PasswordMap(); // 清空密码映射
            TxtLog.Clear();
            AppendLog("已清空内容", ConsoleColor.Gray);
        }

        /// <summary>
        /// 暂停/停止按钮点击事件
        /// </summary>
        private void BtnPauseStop_Click(object sender, RoutedEventArgs e)
        {
            // 如果正在测试密码，取消测试
            if (_isTesting && _testCancellationTokenSource != null)
            {
                AppendLog("\n正在取消密码测试...", ConsoleColor.Yellow);
                _testCancellationTokenSource.Cancel();
                return;
            }

            // 如果正在解压，取消解压
            if (_isExtracting && _extractCancellationTokenSource != null)
            {
                AppendLog("\n正在取消解压操作...", ConsoleColor.Yellow);
                _extractCancellationTokenSource.Cancel();
                return;
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
            _pendingQueueSignal.Set();
            AppendLog("[系统] 已唤醒测试线程", ConsoleColor.Gray);
        }

        /// <summary>
        /// 唤醒解压线程（当有待解压文件加入队列时调用）
        /// </summary>
        private void WakeupExtractThread()
        {
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
            _testCancellationTokenSource = new CancellationTokenSource();
            var token = _testCancellationTokenSource.Token;

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
                _isTesting = false;
                _testCancellationTokenSource?.Dispose();
                _testCancellationTokenSource = null;
                UpdateUiState();
                _testCompletionSource.TrySetResult(true);
            }
        }

        /// <summary>
        /// 处理待处理文件：判断是否压缩包，测试密码，然后加入待解压队列
        /// </summary>
        private async Task ProcessPendingFile(FileItem fileItem, SevenZipExtractor extractor, List<string> passwords, CancellationToken token)
        {
            Dispatcher.Invoke(() => fileItem.Status = "正在测试密码...");
            AppendLog($"\n[测试] {fileItem.FileName}", ConsoleColor.White);

            // 步骤1：判断是否是压缩文件
            if (!IsArchiveFile(fileItem.FilePath))
            {
                Dispatcher.Invoke(() => fileItem.Status = "非压缩文件，跳过");
                AppendLog($"[{fileItem.FileName}] 非压缩文件，跳过", ConsoleColor.Gray);
                
                // 检查父项完成（传入子节点）
                if (fileItem.Parent != null)
                {
                    UpdateParentStatusWhenChildrenComplete(fileItem);
                }
                return;
            }

            string? foundPassword = null;

            // 步骤2：尝试无密码
            Dispatcher.Invoke(() =>
            {
                TxtStatusCurrentPassword.Text = "测试中的密码: (无密码)";
                fileItem.Status = "测试: 无密码";
            });

            AppendLog("  测试: 无密码...", ConsoleColor.Cyan);
            bool noPasswordSuccess = await extractor.TestPasswordAsync(fileItem.FilePath, "", cancellationToken: token);

            if (noPasswordSuccess)
            {
                foundPassword = null;
                AppendLog("  ✓ 无密码测试成功", ConsoleColor.Green);
            }
            else
            {
                // 步骤3：测试密码本
                AppendLog("  ✗ 无密码失败，测试密码本...", ConsoleColor.Gray);
                
                foreach (var password in passwords)
                {
                    if (token.IsCancellationRequested)
                        break;

                    Dispatcher.Invoke(() =>
                    {
                        TxtStatusCurrentPassword.Text = $"测试中的密码: {password}";
                        fileItem.Status = $"测试: {password}";
                    });

                    bool isValid = await extractor.TestPasswordAsync(fileItem.FilePath, password, cancellationToken: token);

                    if (isValid)
                    {
                        foundPassword = password;
                        _settings.RecordPasswordUsage(password);
                        AppendLog($"  ✓ 找到正确密码: {password}", ConsoleColor.Green);
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

            AppendLog($"[{fileItem.FileName}] 测试结果: {foundPassword ?? "无密码"}", 
                foundPassword != null ? ConsoleColor.Green : ConsoleColor.Cyan);

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
            _extractCancellationTokenSource = new CancellationTokenSource();
            var token = _extractCancellationTokenSource.Token;

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
                _isExtracting = false;
                _extractCancellationTokenSource?.Dispose();
                _extractCancellationTokenSource = null;
                UpdateUiState();
                _extractCompletionSource.TrySetResult(true);
            }
        }

        /// <summary>
        /// 监控处理流程完成
        /// </summary>
        private async void MonitorProcessingComplete()
        {
            try
            {
                await Task.WhenAll(_testCompletionSource.Task, _extractCompletionSource.Task);
                AppendLog($"\n========== 所有处理流程完成 ==========", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"监控流程出错: {ex.Message}", ConsoleColor.Red);
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
                    // 处理原文件（移动到回收站/删除等）
                    // 由于这个方法被多处调用，且都是同步调用，我们使用 fire-and-forget 模式
                    _ = HandleOriginalFileAsync(fileItem.FilePath);
                    
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
                AppendLog($"[线程 {taskId}] 跳过 {fileItem.FileName}: 未找到密码", ConsoleColor.Yellow);
                
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
                    showCliWindow: _settings.ShowCliWindow,
                    cancellationToken: token);

                if (result.Success)
                {
                    Dispatcher.Invoke(() =>
                    {
                        fileItem.Status = password != null
                            ? $"解压成功 (密码: {password})"
                            : "解压成功 (无密码)";
                    });
                    AppendLog($"[线程 {taskId}] {fileItem.FileName}: 解压成功", ConsoleColor.Green);

                    // 不在这里立即处理原文件，而是延迟到所有子项完成后处理
                    // 原文件处理逻辑移到了 UpdateParentStatusWhenChildrenComplete 和 CheckAndMarkParentComplete 中

                    // 从密码映射表中移除已完成处理的文件记录
                    string filePath = fileItem.FilePath;
                    _passwordMap.Remove(filePath);
                    AppendLog($"[线程 {taskId}] {fileItem.FileName}: 已从密码映射表清除", ConsoleColor.Gray);

                    // 检查解压后的文件是否包含新的压缩文件
                    // 这会添加子项到 fileItem.Children，并将新文件加入待处理队列
                    await ScanExtractedFilesForArchives(outputDir, taskId, fileItem);
                    
                    // 注意：原文件处理逻辑已经移到了 ScanExtractedFilesForArchives 和 UpdateParentStatusWhenChildrenComplete 中
                    // - 叶子节点（无子项）：ScanExtractedFilesForArchives 中处理
                    // - 父节点（有子项）：UpdateParentStatusWhenChildrenComplete 中所有子项完成后处理
                    
                    // 如果有子项，等待子项处理完成后会自动通过 UpdateParentStatusWhenChildrenComplete 更新父项状态
                    if (fileItem.Children.Count > 0)
                    {
                        AppendLog($"[{fileItem.FileName}] 已添加 {fileItem.Children.Count} 个子压缩包到待处理队列，等待递归处理...", ConsoleColor.Cyan);
                    }
                    // 叶子节点已在 ScanExtractedFilesForArchives 中处理完成
                }
                else
                {
                    Dispatcher.Invoke(() => fileItem.Status = $"解压失败: {result.Message}");
                    AppendLog($"[线程 {taskId}] {fileItem.FileName}: 解压失败 - {result.Message}", ConsoleColor.Red);
                    
                    // 解压失败时也需要检查父项状态（传入子节点）
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
        /// 扫描解压后的文件，检测是否包含新的压缩文件并添加到列表
        /// 注意：此方法只负责扫描和添加文件，不测试密码，不启动解压
        /// 密码测试和解压由 CheckAndTriggerNextRoundExtractAsync 统一处理
        /// </summary>
        private async Task ScanExtractedFilesForArchives(string outputDir, int taskId, FileItem? parentFileItem = null)
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
                var newArchiveFiles = Directory.GetFiles(outputDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => IsArchiveFile(f) && !IsMultiVolumeArchive(f))
                    .ToList();

                var allArchiveFiles = newArchiveFiles.Distinct().ToList();

                AppendLog($"[线程 {taskId}] [DEBUG] 找到 {allArchiveFiles.Count} 个候选压缩文件", ConsoleColor.Magenta);

                if (allArchiveFiles.Count == 0)
                {
                    AppendLog($"[线程 {taskId}] 未发现新的压缩文件", ConsoleColor.Gray);
                    
                    if (parentFileItem != null)
                    {
                        AppendLog($"[线程 {taskId}] [DEBUG] 无子压缩包，处理原文件并更新父项状态: {parentFileItem.FileName}", ConsoleColor.Magenta);
                        
                        // 当前节点（叶子节点）：先处理自身原文件
                        AppendLog($"[{parentFileItem.FileName}] 叶子节点解压完成，处理原文件...", ConsoleColor.Cyan);
                        _ = HandleOriginalFileAsync(parentFileItem.FilePath);
                        
                        // 然后向父节点传播完成状态
                        UpdateParentStatusWhenChildrenComplete(parentFileItem);
                    }
                    return;
                }

                AppendLog($"[线程 {taskId}] 发现 {allArchiveFiles.Count} 个新的压缩文件", ConsoleColor.Cyan);

                int addedCount = 0;
                foreach (var archiveFile in allArchiveFiles)
                {
                    // 检查是否已在列表中
                    FileItem? existingItem = null;
                    Dispatcher.Invoke(() =>
                    {
                        existingItem = FindFileItemInTree(archiveFile);
                    });

                    if (existingItem != null)
                    {
                        AppendLog($"[线程 {taskId}] [DEBUG] 文件已存在，跳过: {Path.GetFileName(archiveFile)}", ConsoleColor.Magenta);
                        continue;
                    }

                    // 创建新的文件项
                    var fileInfo = new FileInfo(archiveFile);
                    var fileItem = new FileItem
                    {
                        FileName = fileInfo.Name,
                        FilePath = archiveFile,
                        Status = "等待处理",  // 统一初始状态
                        FileSize = fileInfo.Length,
                        Parent = parentFileItem
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
                    // 没有添加新文件，检查父项是否可以完成
                    if (parentFileItem != null)
                    {
                        AppendLog($"[线程 {taskId}] 没有新文件，更新父项状态: {parentFileItem.FileName}", ConsoleColor.Gray);
                        UpdateParentStatusWhenChildrenComplete(parentFileItem);
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
                // 保留原有的密码信息，更新状态为完成
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
                
                // 处理原文件（移动到回收站/删除等）
                // 由于这个方法是同步调用，使用 fire-and-forget 模式
                _ = HandleOriginalFileAsync(parentItem.FilePath);
                
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
            // 自动模式下，开始解压按钮应该禁用（因为会自动触发）
            bool isAutoMode = _settings.ExtractMode == ExtractMode.Auto;
            
            // 如果正在测试或解压，禁用开始解压按钮
            // 如果是自动模式，也禁用开始解压按钮
            if (_isTesting || _isExtracting || isAutoMode)
            {
                BtnStartExtract.IsEnabled = false;
            }
            else
            {
                BtnStartExtract.IsEnabled = true;
            }

            TxtStatusTestThread.Text = $"测试线程: {(_isTesting ? "工作中" : "空闲")}";
            TxtStatusExtractThread.Text = $"解压线程: {(_isExtracting ? "工作中" : "空闲")}";
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
        /// 判断文件是否为压缩文件（基于扩展名 + 文件头特征）
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>是否为压缩文件</returns>
        private bool IsArchiveFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            if (!File.Exists(filePath))
                return false;

            // 方法1：先检查扩展名（快速判断）
            if (IsArchiveFileByExtension(filePath))
                return true;

            // 方法2：如果扩展名不匹配，检查文件头特征（魔数）
            if (IsArchiveFileByMagicNumber(filePath))
                return true;

            return false;
        }

        /// <summary>
        /// 基于扩展名判断压缩文件
        /// </summary>
        private bool IsArchiveFileByExtension(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            // 支持的压缩格式扩展名
            var archiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // 常见压缩格式
                ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz",
                // ZSTD 压缩
                ".zst", ".zstd",
                // 复合格式（如 .tar.gz, .tar.bz2, .tar.xz, .tar.zst）
                ".tgz", ".tbz2", ".tbz", ".txz", ".tlz",
                // 其他压缩格式
                ".cab", ".iso", ".wim", ".arj", ".lzh",
                ".cpio", ".rpm", ".deb",
                // 双层扩展名处理（如 .tar.zst）
                ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.zstd",
                ".tar.lz", ".tar.lzma", ".tar.lzo"
            };

            // 先检查完整扩展名（处理 .tar.zst 这类复合扩展名）
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
        /// </summary>
        private string GetArchiveFolderName(string archivePath)
        {
            string fileName = Path.GetFileName(archivePath);
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(archivePath);
            
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
            
            // 6. 普通压缩包：去掉一层扩展名
            // xxx.7z -> xxx
            // xxx.rar -> xxx
            // xxx.zip -> xxx
            return fileNameWithoutExt;
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
                        if (fileItem?.VolumeInfo?.IsMultiVolume == true)
                        {
                            // 分卷压缩包：移动所有分卷
                            await MoveAllVolumesToRecycleBin(fileItem.VolumeInfo);
                        }
                        else
                        {
                            // 普通文件：移动到回收站
                            await Task.Run(() =>
                            {
                                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(filePath, 
                                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                            });
                            AppendLog($"  原文件已移动到回收站: {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                        }
                        break;

                    case FileAction.Delete:
                        // 直接删除
                        if (fileItem?.VolumeInfo?.IsMultiVolume == true)
                        {
                            // 分卷压缩包：删除所有分卷
                            await DeleteAllVolumes(fileItem.VolumeInfo);
                        }
                        else
                        {
                            // 普通文件：直接删除
                            await Task.Run(() => File.Delete(filePath));
                            AppendLog($"  原文件已删除: {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                        }
                        break;

                    case FileAction.MoveToSpecificDir:
                        // 移动到指定目录
                        if (fileItem?.VolumeInfo?.IsMultiVolume == true)
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
                        await Task.Run(() =>
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(volumePath, 
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        });
                        AppendLog($"  已移动分卷到回收站: {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                        movedCount++;
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"  移动分卷失败 {Path.GetFileName(volumePath)}: {ex.Message}", ConsoleColor.Red);
                }
            }
            
            AppendLog($"  分卷压缩包已处理（已移动 {movedCount}/{volumeInfo.VolumeCount} 个分卷到回收站）", ConsoleColor.Green);
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
                        await Task.Run(() => File.Delete(volumePath));
                        AppendLog($"  已删除分卷: {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"  删除分卷失败 {Path.GetFileName(volumePath)}: {ex.Message}", ConsoleColor.Red);
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
                AppendLog($"  警告：未设置分卷清理目录，跳过移动", ConsoleColor.Yellow);
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
                AppendLog($"  警告：未设置清理目录，跳过移动", ConsoleColor.Yellow);
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
        
        // 新增：子节点完成状态跟踪（用于反向传播）
        private int _expectedChildrenCount = 0;      // 预期的子节点总数
        private int _completedChildrenCount = 0;     // 已完成的子节点数
        private bool _isMarkedComplete = false;      // 是否已标记为完成（防止重复上报）
        
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








