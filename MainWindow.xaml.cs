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
        private CancellationTokenSource? _testCancellationTokenSource;
        private CancellationTokenSource? _extractCancellationTokenSource;
        private bool _isTesting = false;
        private bool _isExtracting = false;

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
                    
                    // 自动开始密码测试和解压流程
                    AppendLog("自动开始密码测试...", ConsoleColor.Cyan);
                    
                    // 在UI线程上启动异步任务，避免跨线程访问问题
                    Dispatcher.InvokeAsync(async () =>
                    {
                        try
                        {
                            // 先执行密码测试
                            await StartPasswordTestProcess();
                            
                            // 如果是自动模式且测试成功，自动开始解压
                            if (_settings.ExtractMode == ExtractMode.Auto && _passwordMap.Count > 0)
                            {
                                AppendLog("自动模式：密码测试完成，开始解压...", ConsoleColor.Cyan);
                                await Task.Delay(500); // 稍微延迟让用户看到测试结果
                                await StartExtractProcess();
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"自动处理流程出错: {ex.Message}", ConsoleColor.Red);
                        }
                    });
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

            await StartExtractProcess();
        }

        /// <summary>
        /// 仅执行密码测试，不解压
        /// </summary>
        private async void StartPasswordTestOnly()
        {
            try
            {
                BtnStartExtract.IsEnabled = false;
                TxtStatusTestThread.Text = "测试线程: 工作中";

                // 获取所有密码（包含一次性密码和永久密码）
                var passwords = _settings.GetAllPasswords();
                AppendLog($"加载密码本: 共 {passwords.Count} 个密码（一次性: {_settings.OneTimePasswords.Count}, 永久: {passwords.Count - _settings.OneTimePasswords.Count}）", ConsoleColor.Cyan);
                
                // 详细日志：列出所有密码
                if (passwords.Count == 0)
                {
                    AppendLog("警告：密码本为空，请先在\"软件设置\"中导入密码本！", ConsoleColor.Red);
                }
                else
                {
                    AppendLog($"永久密码数量: {_settings.PermanentPasswords.Count}", ConsoleColor.Cyan);
                    AppendLog($"密码来源: {(_settings.PermanentPasswords.Count > 0 ? "配置文件" : "外部文件")}", ConsoleColor.Cyan);
                }

                // 创建解压器
                var extractor = new SevenZipExtractor(_settings.SevenZipPath);

                foreach (var fileItem in _fileList.ToList())
                {
                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            fileItem.Status = "正在测试密码...";
                        });

                        AppendLog($"\n========== 开始测试: {fileItem.FileName} ==========", ConsoleColor.White);

                        string? foundPassword = null;

                        // 第一步：先尝试无密码解压
                        Dispatcher.Invoke(() =>
                        {
                            TxtStatusCurrentPassword.Text = "测试中的密码: (无密码)";
                            fileItem.Status = "测试: 无密码";
                        });
                        
                        AppendLog("  测试: 无密码解压...", ConsoleColor.Cyan);
                        // StartPasswordTestOnly 方法中的第一处调用，通常也建议加上 Token，但用户只问了第2处。
                        // 为了保持一致性，这里暂时不动，只动第2处。
                        bool noPasswordSuccess = await extractor.TestPasswordAsync(fileItem.FilePath, "");
                        
                        if (noPasswordSuccess)
                        {
                            foundPassword = null; // 无密码
                            AppendLog("  ✓ 无密码解压成功！", ConsoleColor.Green);
                            Dispatcher.Invoke(() =>
                            {
                                fileItem.FoundPassword = null;
                                fileItem.Status = "无密码";
                            });
                            
                            // 无密码成功，跳过密码本测试
                            Dispatcher.Invoke(() =>
                            {
                                TxtStatusCurrentPassword.Text = "测试中的密码: 无";
                            });
                            AppendLog($"[{fileItem.FileName}] 测试结果: 无密码压缩包", ConsoleColor.Green);
                            
                            // 关键修复：将无密码状态添加到映射表
                            _passwordMap.Add(fileItem.FilePath, null);
                            
                            await Task.Delay(100);
                            continue; // 继续下一个文件
                        }
                        else
                        {
                            AppendLog("  ✗ 无密码解压失败，开始测试密码本...", ConsoleColor.Gray);
                        }

                        // 第二步：测试密码本中的所有密码
                        foreach (var password in passwords)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                TxtStatusCurrentPassword.Text = $"测试中的密码: {password}";
                                fileItem.Status = $"测试密码: {password}";
                            });

                            bool isValid = await extractor.TestPasswordAsync(fileItem.FilePath, password, cancellationToken: _testCancellationTokenSource?.Token ?? CancellationToken.None);

                            if (isValid)
                            {
                                foundPassword = password;
                                AppendLog($"  ✓ 找到正确密码: {password}", ConsoleColor.Green);
                                
                                // 记录密码使用次数
                                _settings.RecordPasswordUsage(password);
                                break; // 找到正确密码，停止测试
                            }

                            await Task.Delay(50); // 避免过于频繁
                        }

                        // 关键修复：将测试结果添加到密码映射表
                        _passwordMap.Add(fileItem.FilePath, foundPassword);

                        Dispatcher.Invoke(() =>
                        {
                            TxtStatusCurrentPassword.Text = "测试中的密码: 无";
                            
                            if (foundPassword != null)
                            {
                                fileItem.FoundPassword = foundPassword;
                                fileItem.Status = $"密码正确: {foundPassword}";
                                AppendLog($"[{fileItem.FileName}] 测试结果: 找到正确密码 '{foundPassword}'", ConsoleColor.Green);
                            }
                            else
                            {
                                fileItem.Status = "密码错误/未找到";
                                AppendLog($"[{fileItem.FileName}] 测试结果: 所有密码均不正确", ConsoleColor.Red);
                            }
                        });

                        await Task.Delay(100);
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            fileItem.Status = $"异常: {ex.Message}";
                        });
                        AppendLog($"[{fileItem.FileName}] 测试异常: {ex.Message}", ConsoleColor.Red);
                    }
                }

                // 保存密码使用记录
                _settings.SavePasswordsToFile();

                BtnStartExtract.IsEnabled = true;
                TxtStatusTestThread.Text = "测试线程: 空闲";
                AppendLog("\n========== 所有文件密码测试完成 ==========", ConsoleColor.Green);
                
                // 自动模式下自动开始解压
                if (_settings.ExtractMode == ExtractMode.Auto && _passwordMap.Count > 0)
                {
                    AppendLog("自动模式: 3秒后开始解压...", ConsoleColor.Cyan);
                    await Task.Delay(3000);
                    
                    // 重新启用解压按钮并触发解压
                    // 注意：StartExtractProcess 内部会再次检查状态并更新 UI
                    await StartExtractProcess();
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    BtnStartExtract.IsEnabled = true;
                    TxtStatusTestThread.Text = "测试线程: 空闲";
                    MessageBox.Show($"密码测试过程出错：\n{ex.Message}\n\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                });
                AppendLog($"\n密码测试过程出错: {ex.Message}", ConsoleColor.Red);
            }
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
                    Dispatcher.Invoke(() =>
                    {
                        if (_fileList.Contains(fileItem))
                        {
                            _fileList.Remove(fileItem);
                            AppendLog($"[{fileItem.FileName}] 无子压缩包，已从待处理列表移除", ConsoleColor.Gray);
                        }
                    });
                    
                    // 如果没有子项且是顶级文件，检查是否还有待处理的文件
                    // 如果有，触发新一轮的解压
                    CheckAndTriggerNextRoundExtract();
                }
            }
        }

        /// <summary>
        /// 检查是否还有待处理的文件，如果有则触发新一轮解压
        /// </summary>
        private async void CheckAndTriggerNextRoundExtract()
        {
            // 等待当前解压流程完全结束
            await Task.Delay(500);
            
            // 收集树形结构中所有待处理的文件
            var pendingFiles = CollectAllFileItems(_fileList).Where(f => 
                !f.Status.Contains("解压成功") && 
                !f.Status.Contains("跳过") && 
                !f.Status.Contains("异常") &&
                _passwordMap.TryGetValue(f.FilePath, out var pwd)
            ).ToList();
            
            if (pendingFiles.Count > 0 && !_isExtracting)
            {
                AppendLog($"发现 {pendingFiles.Count} 个待解压文件（包括子压缩包），开始新一轮解压...", ConsoleColor.Cyan);
                await StartExtractProcess(extractOnlyNewFiles: true);
            }
        }

        /// <summary>
        /// 阶段1：密码测试流程
        /// </summary>
        /// <param name="testOnlyNewFiles">如果为true，则只测试尚未有密码记录的文件（增量测试）</param>
        private async Task StartPasswordTestProcess(bool testOnlyNewFiles = false)
        {
            if (_isTesting)
            {
                AppendLog("密码测试正在进行中...", ConsoleColor.Yellow);
                return;
            }

            _isTesting = true;
            _testCancellationTokenSource = new CancellationTokenSource();
            var token = _testCancellationTokenSource.Token;

            try
            {
                // 更新UI状态
                UpdateUiState();
                
                // 获取所有密码
                var passwords = _settings.GetAllPasswords();
                AppendLog($"\n========== 开始密码测试 ==========", ConsoleColor.Cyan);
                AppendLog($"加载密码本: 共 {passwords.Count} 个密码（一次性: {_settings.OneTimePasswords.Count}, 永久: {passwords.Count - _settings.OneTimePasswords.Count}）", ConsoleColor.Cyan);

                if (passwords.Count == 0)
                {
                    AppendLog("警告：密码本为空！", ConsoleColor.Red);
                }

                // 如果不是只测试新文件，清空之前的密码映射
                if (!testOnlyNewFiles)
                {
                    _passwordMap = new PasswordMap();
                }

                var extractor = new SevenZipExtractor(_settings.SevenZipPath);

                // 确定要测试的文件列表（包括所有子项）
                List<FileItem> filesToTest;
                if (testOnlyNewFiles)
                {
                    // 收集树形结构中所有尚未有密码记录的文件
                    filesToTest = CollectAllFileItems(_fileList)
                        .Where(f => !_passwordMap.ContainsKey(f.FilePath))
                        .ToList();
                    AppendLog($"增量测试：发现 {filesToTest.Count} 个新文件待测试", ConsoleColor.Cyan);
                }
                else
                {
                    // 测试所有文件（包括子项）
                    filesToTest = CollectAllFileItems(_fileList);
                }

                // 如果没有文件需要测试，直接返回
                if (!filesToTest.Any())
                {
                    AppendLog("没有需要测试的新文件。", ConsoleColor.Cyan);
                    return;
                }

                // 遍历所有文件进行测试
                foreach (var fileItem in filesToTest)
                {
                    if (token.IsCancellationRequested)
                        break;

                    try
                    {
                        Dispatcher.Invoke(() => fileItem.Status = "正在测试密码...");
                        AppendLog($"\n[测试] {fileItem.FileName}", ConsoleColor.White);

                        string? foundPassword = null;

                        // 步骤1：尝试无密码
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
                            // 步骤2：测试密码本
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

                        await Task.Delay(100, token);
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

                // 保存密码使用记录
                _settings.SavePasswordsToFile();

                AppendLog($"\n========== 密码测试完成 ==========", ConsoleColor.Green);
                
                // 只统计本次测试的文件
                int successCount = filesToTest.Count(f => _passwordMap.TryGetValue(f.FilePath, out var pwd) && pwd != null);
                AppendLog($"本次测试 {filesToTest.Count} 个文件，找到 {successCount} 个有效密码", ConsoleColor.Cyan);

                // 自动模式下自动开始解压
                // 只有当本次测试找到了至少一个有效密码时，才触发自动解压
                if (_settings.ExtractMode == ExtractMode.Auto && successCount > 0)
                {
                    AppendLog("自动模式：3秒后开始解压...", ConsoleColor.Cyan);
                    await Task.Delay(3000, token);
                    
                    if (!token.IsCancellationRequested)
                    {
                        await StartExtractProcess(testOnlyNewFiles);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AppendLog("\n密码测试已取消", ConsoleColor.Yellow);
            }
            catch (Exception ex)
            {
                AppendLog($"\n密码测试出错: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                _isTesting = false;
                _testCancellationTokenSource?.Dispose();
                _testCancellationTokenSource = null;
                UpdateUiState();
            }
        }

        /// <summary>
        /// 阶段2：解压流程（支持多线程并发）
        /// </summary>
        private async Task StartExtractProcess(bool extractOnlyNewFiles = false)
        {
            if (_isExtracting)
            {
                AppendLog("解压正在进行中...", ConsoleColor.Yellow);
                return;
            }

            // 确定要解压的文件列表（包括所有子项）
            List<FileItem> filesToExtract;
            if (extractOnlyNewFiles)
            {
                // 收集树形结构中所有新增的文件（尚未解压过的）
                filesToExtract = CollectAllFileItems(_fileList).Where(f => 
                {
                    // 检查是否已有解压成功状态
                    return !f.Status.Contains("解压成功") && 
                           !f.Status.Contains("跳过") && 
                           !f.Status.Contains("异常") &&
                           _passwordMap.TryGetValue(f.FilePath, out var pwd);
                }).ToList();
            }
            else
            {
                // 解压所有有密码的文件（包括子项）
                filesToExtract = CollectAllFileItems(_fileList).Where(f => _passwordMap.TryGetValue(f.FilePath, out var pwd)).ToList();
            }

            if (filesToExtract.Count == 0)
            {
                AppendLog("没有可解压的文件（请先进行密码测试）", ConsoleColor.Yellow);
                return;
            }

            _isExtracting = true;
            _extractCancellationTokenSource = new CancellationTokenSource();
            var token = _extractCancellationTokenSource.Token;

            try
            {
                UpdateUiState();
                
                AppendLog($"\n========== 开始解压 ==========", ConsoleColor.Cyan);
                AppendLog($"并发线程数: {_settings.ThreadCount}", ConsoleColor.Cyan);
                AppendLog($"待解压文件数: {filesToExtract.Count}", ConsoleColor.Cyan);

                var extractor = new SevenZipExtractor(_settings.SevenZipPath);
                var fileQueue = new ConcurrentQueue<FileItem>(filesToExtract);
                var tasks = new List<Task>();

                // 创建多个并发任务
                for (int i = 0; i < _settings.ThreadCount; i++)
                {
                    int taskId = i + 1;
                    var task = Task.Run(async () =>
                    {
                        AppendLog($"[解压线程 {taskId}] 启动", ConsoleColor.Gray);

                        while (!token.IsCancellationRequested && !fileQueue.IsEmpty)
                        {
                            if (fileQueue.TryDequeue(out var fileItem))
                            {
                                // 尝试获取文件锁，防止竞争
                                if (!fileItem.TryLock())
                                {
                                    // 已被其他线程处理，重新入队
                                    fileQueue.Enqueue(fileItem);
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
                                await Task.Delay(100, token);
                            }
                        }

                        AppendLog($"[解压线程 {taskId}] 完成", ConsoleColor.Gray);
                    }, token);

                    tasks.Add(task);
                }

                // 等待所有线程完成
                await Task.WhenAll(tasks);

                AppendLog($"\n========== 解压完成 ==========", ConsoleColor.Green);
            }
            catch (OperationCanceledException)
            {
                AppendLog("\n解压已取消", ConsoleColor.Yellow);
            }
            catch (Exception ex)
            {
                AppendLog($"\n解压出错: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                _isExtracting = false;
                _extractCancellationTokenSource?.Dispose();
                _extractCancellationTokenSource = null;
                UpdateUiState();
            }
        }

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
                
                // 如果没有密码，检查父项是否可以完成
                if (fileItem.Parent != null)
                {
                    UpdateParentStatusWhenChildrenComplete(fileItem.Parent);
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

                    // 处理原文件
                    if (_settings.FileAfterExtract != FileAction.Keep)
                    {
                        await HandleOriginalFile(fileItem.FilePath);
                    }

                    // 从密码映射表中移除已完成处理的文件记录
                    string filePath = fileItem.FilePath;
                    _passwordMap.Remove(filePath);
                    AppendLog($"[线程 {taskId}] {fileItem.FileName}: 已从密码映射表清除", ConsoleColor.Gray);

                    // 检查解压后的文件是否包含新的压缩文件
                    // 这会添加子项到 fileItem.Children，并异步触发密码测试和子项解压
                    await ScanExtractedFilesForArchives(outputDir, taskId, fileItem);
                    
                    // 注意：不在这里直接调用 CheckAndMarkParentComplete
                    // 因为 ScanExtractedFilesForArchives 内部会异步触发子项的密码测试和解压流程
                    // 父项的完成状态应该由子项完成时通过 UpdateParentStatusWhenChildrenComplete 来更新
                    
                    // 如果没有子项，说明解压流程真正完成了
                    if (fileItem.Children.Count == 0)
                    {
                        CheckAndMarkParentComplete(fileItem);
                    }
                    // 如果有子项，等待子项处理完成后会自动通过 UpdateParentStatusWhenChildrenComplete 更新父项状态
                    else
                    {
                        AppendLog($"[{fileItem.FileName}] 已添加 {fileItem.Children.Count} 个子压缩包，等待子项递归处理...", ConsoleColor.Cyan);
                    }
                }
                else
                {
                    Dispatcher.Invoke(() => fileItem.Status = $"解压失败: {result.Message}");
                    AppendLog($"[线程 {taskId}] {fileItem.FileName}: 解压失败 - {result.Message}", ConsoleColor.Red);
                    
                    // 解压失败时也需要检查父项状态
                    if (fileItem.Parent != null)
                    {
                        UpdateParentStatusWhenChildrenComplete(fileItem.Parent);
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
                
                // 取消时也需要检查父项状态
                if (fileItem.Parent != null)
                {
                    UpdateParentStatusWhenChildrenComplete(fileItem.Parent);
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
                
                // 异常时也需要检查父项状态
                if (fileItem.Parent != null)
                {
                    UpdateParentStatusWhenChildrenComplete(fileItem.Parent);
                }
                else
                {
                    CheckAndMarkParentComplete(fileItem);
                }
            }
        }

        /// <summary>
        /// 扫描解压后的文件，检测是否包含新的压缩文件并添加到测试列表
        /// </summary>
        /// <param name="outputDir">输出目录</param>
        /// <param name="taskId">线程ID</param>
        /// <param name="parentFileItem">父级文件项（如果是从压缩包中解压出来的）</param>
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
                    .Where(f => IsArchiveFile(f) && !IsMultiVolumeArchive(f)) // 排除分卷文件（已在上面处理）
                    .ToList();

                // 合并两种文件
                var allArchiveFiles = newArchiveFiles.Distinct().ToList();

                AppendLog($"[线程 {taskId}] [DEBUG] 找到 {allArchiveFiles.Count} 个候选压缩文件", ConsoleColor.Magenta);

                if (allArchiveFiles.Count == 0)
                {
                    AppendLog($"[线程 {taskId}] 未发现新的压缩文件，解压流程完成", ConsoleColor.Gray);
                    
                    // 如果没有子压缩包，直接标记父项为完成
                    if (parentFileItem != null)
                    {
                        AppendLog($"[线程 {taskId}] [DEBUG] 无子压缩包，更新父项状态: {parentFileItem.FileName}", ConsoleColor.Magenta);
                        UpdateParentStatusWhenChildrenComplete(parentFileItem);
                    }
                    return;
                }

                AppendLog($"[线程 {taskId}] 发现 {allArchiveFiles.Count} 个新的压缩文件", ConsoleColor.Cyan);

                int addedCount = 0;
                int retryCount = 0; // 记录重试的文件数
                foreach (var archiveFile in allArchiveFiles)
                {
                    // 检查是否已在列表中（包括子项）
                    FileItem? existingItem = null;
                    Dispatcher.Invoke(() =>
                    {
                        existingItem = FindFileItemInTree(archiveFile);
                    });

                    if (existingItem != null)
                    {
                        // 文件已存在，检查是否需要重试
                        bool needsRetry = existingItem.Status.Contains("解压失败") || 
                                         existingItem.Status.Contains("异常") ||
                                         existingItem.Status == "等待处理";
                        
                        if (needsRetry)
                        {
                            AppendLog($"[线程 {taskId}] [DEBUG] 文件已存在但需要重试: {Path.GetFileName(archiveFile)} (状态: {existingItem.Status})", ConsoleColor.Magenta);
                            
                            // 重置状态为等待处理
                            Dispatcher.Invoke(() =>
                            {
                                existingItem.Status = "等待处理";
                            });
                            
                            retryCount++;
                        }
                        else
                        {
                            AppendLog($"[线程 {taskId}] [DEBUG] 文件已存在且状态正常，跳过: {Path.GetFileName(archiveFile)} (状态: {existingItem.Status})", ConsoleColor.Magenta);
                        }
                        continue;
                    }

                    // 普通压缩文件
                    var fileInfo = new FileInfo(archiveFile);
                    var fileItem = new FileItem
                    {
                        FileName = fileInfo.Name,
                        FilePath = archiveFile,
                        Status = "等待处理",
                        FileSize = fileInfo.Length,
                        Parent = parentFileItem // 记录父级
                    };

                    Dispatcher.Invoke(() =>
                    {
                        if (parentFileItem != null)
                        {
                            // 作为子项添加
                            parentFileItem.Children.Add(fileItem);
                            AppendLog($"[线程 {taskId}] {parentFileItem.FileName} 的子压缩包: {fileItem.FileName}", ConsoleColor.Gray);
                        }
                        else
                        {
                            // 作为顶级项添加
                            _fileList.Add(fileItem);
                        }
                    });

                    addedCount++;
                    AppendLog($"[线程 {taskId}] 添加新压缩文件: {fileItem.FileName}", ConsoleColor.Green);
                }

                if (addedCount > 0 || retryCount > 0)
                {
                    AppendLog($"[线程 {taskId}] 共添加 {addedCount} 个新压缩文件，重试 {retryCount} 个失败文件", ConsoleColor.Cyan);
                    AppendLog($"[线程 {taskId}] [DEBUG] 添加完成，等待500ms确保UI更新...", ConsoleColor.Magenta);
                    
                    // 延迟一下确保UI更新完成
                    await Task.Delay(500);
                    
                    // 复用主流程：测试密码 → 自动解压（如果是自动模式）
                    AppendLog($"[线程 {taskId}] 开始测试新文件的密码...", ConsoleColor.Cyan);
                    AppendLog($"[线程 {taskId}] [DEBUG] 准备启动密码测试流程", ConsoleColor.Magenta);
                    
                    // 直接await异步方法，而不是使用Dispatcher.InvokeAsync
                    // 这样可以确保等待密码测试和解压流程完全完成
                    try
                    {
                        // 在UI线程上执行密码测试（只测试新文件）
                        await Dispatcher.Invoke(async () =>
                        {
                            AppendLog($"[线程 {taskId}] [DEBUG] 在UI线程上开始密码测试", ConsoleColor.Magenta);
                            await StartPasswordTestProcess(testOnlyNewFiles: true);
                            AppendLog($"[线程 {taskId}] [DEBUG] 密码测试完成", ConsoleColor.Magenta);
                            
                            // 密码测试完成后，检查是否有待解压的文件
                            var pendingFiles = CollectAllFileItems(_fileList).Where(f => 
                                !f.Status.Contains("解压成功") && 
                                !f.Status.Contains("跳过") && 
                                !f.Status.Contains("异常") &&
                                _passwordMap.TryGetValue(f.FilePath, out var pwd)
                            ).ToList();
                            
                            AppendLog($"[线程 {taskId}] [DEBUG] 找到 {pendingFiles.Count} 个待解压文件", ConsoleColor.Magenta);
                            
                            if (pendingFiles.Count > 0 && !_isExtracting)
                            {
                                AppendLog($"[线程 {taskId}] 发现 {pendingFiles.Count} 个待解压的子压缩包，开始解压...", ConsoleColor.Cyan);
                                AppendLog($"[线程 {taskId}] [DEBUG] 准备启动解压流程", ConsoleColor.Magenta);
                                await StartExtractProcess(extractOnlyNewFiles: true);
                                AppendLog($"[线程 {taskId}] [DEBUG] 解压流程完成", ConsoleColor.Magenta);
                            }
                            else
                            {
                                AppendLog($"[线程 {taskId}] [DEBUG] 没有待解压文件或正在解压中", ConsoleColor.Magenta);
                            }
                        });
                        
                        AppendLog($"[线程 {taskId}] [DEBUG] Dispatcher.Invoke 返回，子项处理完成", ConsoleColor.Magenta);
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[线程 {taskId}] 处理新文件失败: {ex.Message}", ConsoleColor.Red);
                        AppendLog($"[线程 {taskId}] [DEBUG] 异常堆栈: {ex.StackTrace}", ConsoleColor.Magenta);
                    }
                    
                    AppendLog($"[线程 {taskId}] 子项处理流程已完成", ConsoleColor.Gray);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[线程 {taskId}] 扫描解压文件失败: {ex.Message}", ConsoleColor.Red);
                AppendLog($"[线程 {taskId}] [DEBUG] 异常堆栈: {ex.StackTrace}", ConsoleColor.Magenta);
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
        /// 当子项全部完成时，更新父项状态
        /// </summary>
        private void UpdateParentStatusWhenChildrenComplete(FileItem parentItem)
        {
            // 检查是否所有子项都已完成
            bool allChildrenComplete = true;
            foreach (var child in parentItem.Children)
            {
                if (!child.Status.Contains("解压成功") && 
                    !child.Status.Contains("无密码") &&
                    !child.Status.Contains("跳过") &&
                    !child.Status.Contains("异常") &&
                    !child.Status.Contains("已取消") &&
                    !child.Status.Contains("失败"))
                {
                    allChildrenComplete = false;
                    break;
                }
            }

            // 如果所有子项都完成，更新父项状态
            if (allChildrenComplete)
            {
                // 保留原有的密码信息，更新状态为完成
                var passwordInfo = parentItem.FoundPassword != null 
                    ? $" (密码: {parentItem.FoundPassword})" 
                    : " (无密码)";
                
                Dispatcher.Invoke(() =>
                {
                    parentItem.Status = $"解压成功{passwordInfo} [包含 {parentItem.Children.Count} 个子压缩包]";
                });
                
                AppendLog($"[{parentItem.FileName}] 所有子压缩包处理完成，父压缩包标记为完成", ConsoleColor.Green);
                
                // 从密码映射表中移除
                _passwordMap.Remove(parentItem.FilePath);
                
                // 递归处理父级的父级
                if (parentItem.Parent != null)
                {
                    UpdateParentStatusWhenChildrenComplete(parentItem.Parent);
                }
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

            // 检查密码映射表中是否有待解压的文件
            var filesWithPassword = _fileList.Where(f => _passwordMap.TryGetValue(f.FilePath, out var pwd)).ToList();
            
            if (filesWithPassword.Count > 0)
            {
                AppendLog("自动模式：发现待解压文件，开始解压...", ConsoleColor.Cyan);
                await Task.Delay(500); // 短暂延迟让用户看到状态变化
                await StartExtractProcess();
            }
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
            string archiveNameWithoutExt = Path.GetFileNameWithoutExtension(archivePath);

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
                var fileItem = _fileList.FirstOrDefault(f => f.FilePath == filePath);
                
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
                            // 需要添加 Microsoft.VisualBasic.FileIO 引用
                            // Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(filePath, 
                            //     Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            //     Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                            AppendLog($"  跳过移动到回收站（需要添加 Microsoft.VisualBasic 引用）", ConsoleColor.Yellow);
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
            
            foreach (var volumePath in volumeInfo.AllVolumePaths)
            {
                try
                {
                    if (File.Exists(volumePath))
                    {
                        // 需要添加 Microsoft.VisualBasic.FileIO 引用
                        // Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(volumePath, 
                        //     Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        //     Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        AppendLog($"  跳过移动到回收站: {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"  移动分卷失败 {Path.GetFileName(volumePath)}: {ex.Message}", ConsoleColor.Red);
                }
            }
            
            AppendLog($"  分卷压缩包已处理（移动到回收站）", ConsoleColor.Green);
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

    public class FileItem
    {
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string? FoundPassword { get; set; }
        public ArchiveVolumeInfo? VolumeInfo { get; set; }
        
        // 新增：父子关系支持
        public ObservableCollection<FileItem> Children { get; set; } = new ObservableCollection<FileItem>();
        public FileItem? Parent { get; set; }
        public bool IsParent => Children.Count > 0;
        
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








