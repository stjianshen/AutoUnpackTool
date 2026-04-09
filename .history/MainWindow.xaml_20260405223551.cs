
using System;
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
        private string[] _defaultPasswords = new string[] { "123456", "password", "12345678", "admin", "root" };
        
        private bool _isManualMode = true; // 默认为手动模式
        private bool _isProcessing = false;
        private CancellationTokenSource? _cancellationTokenSource;

        public MainWindow()
        {
            InitializeComponent();
            LstFiles.ItemsSource = _fileList;
            
            // 加载设置
            _settings = AppSettings.Load();
            
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
                
                foreach (string file in files)
                {
                    if (File.Exists(file))
                    {
                        var fileInfo = new FileInfo(file);
                        var fileItem = new FileItem
                        {
                            FileName = fileInfo.Name,
                            FilePath = file,
                            Status = "等待处理",
                            FileSize = fileInfo.Length
                        };
                        _fileList.Add(fileItem);
                    }
                }

                AppendLog($"已添加 {_fileList.Count} 个文件到列表", ConsoleColor.Blue);
                
                // 如果是自动模式且有文件，自动开始
                if (!_isManualMode && _fileList.Count > 0 && !_isProcessing)
                {
                    StartExtractProcess();
                }
            }
        }

        #endregion

        #region 按钮事件

        private void ExtractMode_Checked(object sender, RoutedEventArgs e)
        {
            // 更新解压模式
            if (RdoManualMode?.IsChecked == true)
            {
                _isManualMode = true;
                AppendLog("切换到手动解压模式", ConsoleColor.Cyan);
            }
            else if (RdoAutoMode?.IsChecked == true)
            {
                _isManualMode = false;
                AppendLog("切换到自动解压模式", ConsoleColor.Cyan);
            }
        }

        private void BtnSelectDirectory_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择解压输出目录"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtOutputDirectory.Text = dialog.FolderName;
                _settings.OutputDir = dialog.FolderName;
                AppendLog($"输出目录设置为: {dialog.FolderName}", ConsoleColor.Cyan);
            }
        }

        private async void BtnStartTest_Click(object sender, RoutedEventArgs e)
        {
            // 手动模式：只测试密码，不解压
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

            await StartPasswordTestProcess();
        }

        private async void BtnStartExtract_Click(object sender, RoutedEventArgs e)
        {
            // 手动模式：开始解压已测试密码的文件
            // 自动模式：测试并解压
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

            if (_isManualMode)
            {
                // 手动模式：只解压已测试成功的文件
                await StartExtractOnlyProcess();
            }
            else
            {
                // 自动模式：测试并解压
                await StartExtractProcess();
            }
        }

        private async void BtnPauseStop_Click(object sender, RoutedEventArgs e)
        {
            // 暂停/停止处理
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                AppendLog("正在停止处理...", ConsoleColor.Yellow);
                
                // 等待当前任务稍微响应取消信号，避免立即重置UI导致状态不一致
                await Task.Delay(100);
                
                _isProcessing = false;
                UpdateUiState(false);
                TxtStatusCurrentPassword.Text = "测试中的密码: 无";
                AppendLog("处理已停止", ConsoleColor.Yellow);
            }
        }

        private void BtnPasswordConfig_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new PasswordManagerDialog(_settings);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true)
            {
                AppendLog("密码本已更新", ConsoleColor.Cyan);
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsDialog(_settings);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true)
            {
                AppendLog("设置已保存", ConsoleColor.Cyan);
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _fileList.Clear();
            TxtLog.Clear();
            AppendLog("已清空内容", ConsoleColor.Gray);
        }

        #endregion

        #region 核心处理逻辑

        private void UpdateUiState(bool isProcessing)
        {
            _isProcessing = isProcessing;
            BtnStartExtract.IsEnabled = !isProcessing;
            BtnStartTest.IsEnabled = !isProcessing;
            BtnPauseStop.IsEnabled = isProcessing;
            
            string statusText = isProcessing ? "工作中" : "空闲";
            TxtStatusTestThread.Text = $"测试线程: {statusText}";
            TxtStatusExtractThread.Text = $"解压线程: {statusText}";
        }

        /// <summary>
        /// 手动模式：只测试密码，不解压
        /// </summary>
        private async Task StartPasswordTestProcess()
        {
            UpdateUiState(true);
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            try
            {
                var passwords = _settings.GetAllPasswords();
                var extractor = new SevenZipExtractor(_settings.SevenZipPath);

                AppendLog($"开始密码测试: 共 {_fileList.Count} 个文件, {passwords.Count} 个密码", ConsoleColor.Cyan);

                foreach (var fileItem in _fileList.ToList())
                {
                    if (token.IsCancellationRequested) break;

                    Dispatcher.Invoke(() => 
                    {
                        fileItem.Status = "正在测试密码...";
                        fileItem.FoundPassword = null; // 重置
                    });
                    
                    AppendLog($"\n[测试] {fileItem.FileName}", ConsoleColor.White);

                    // 使用 TestPasswordAsync 进行纯测试
                    var result = await extractor.TestPasswordAsync(
                        fileItem.FilePath,
                        passwords,
                        onPasswordTest: (pwd) =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                TxtStatusCurrentPassword.Text = $"测试中的密码: {pwd}";
                                if (!token.IsCancellationRequested) fileItem.Status = $"测试: {pwd}";
                            });
                        },
                        onPasswordFound: (pwd) =>
                        {
                            _settings.RecordPasswordUsage(pwd);
                            // 记录密码到 FileItem 以便后续手动解压使用
                            Dispatcher.Invoke(() => fileItem.FoundPassword = pwd);
                        });

                    Dispatcher.Invoke(() =>
                    {
                        TxtStatusCurrentPassword.Text = "测试中的密码: 无";
                        if (result.Success)
                        {
                            // 更新 FoundPassword 以防万一 TestPasswordAsync 没触发回调（例如无密码情况）
                            if (result.FoundPassword != null) fileItem.FoundPassword = result.FoundPassword;
                            
                            fileItem.Status = result.FoundPassword != null
                                ? $"密码正确: {result.FoundPassword}"
                                : "无密码";
                            AppendLog($"  -> {result.Message}", ConsoleColor.Green);
                        }
                        else
                        {
                            fileItem.Status = $"密码错误/未知: {result.Message}";
                            AppendLog($"  -> {result.Message}", ConsoleColor.Red);
                        }
                    });
                    
                    await Task.Delay(100, token);
                }
                
                _settings.SavePasswordsToFile();
                AppendLog("\n========== 密码测试完成 ==========", ConsoleColor.Green);
            }
            catch (OperationCanceledException)
            {
                AppendLog("\n操作已取消", ConsoleColor.Yellow);
            }
            catch (Exception ex)
            {
                AppendLog($"\n测试过程出错: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                UpdateUiState(false);
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        /// <summary>
        /// 手动模式：只解压已测试成功的文件
        /// </summary>
        private async Task StartExtractOnlyProcess()
        {
            UpdateUiState(true);
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            try
            {
                var extractor = new SevenZipExtractor(_settings.SevenZipPath);
                AppendLog($"开始手动解压 (仅解压已知密码文件)...", ConsoleColor.Cyan);

                foreach (var fileItem in _fileList.ToList())
                {
                    if (token.IsCancellationRequested) break;

                    // 判断是否可以解压：状态包含成功标记，或者我们在测试阶段记录了 FoundPassword
                    // 注意："无密码" 也是一种成功状态
                    bool isKnown = fileItem.Status.Contains("密码正确") || 
                                   fileItem.Status.Contains("无密码") || 
                                   fileItem.Status.Contains("解压成功");
                    
                    if (!isKnown)
                    {
                        AppendLog($"[跳过] {fileItem.FileName} (未确认密码或测试失败)", ConsoleColor.Yellow);
                        continue;
                    }

                    Dispatcher.Invoke(() => fileItem.Status = "正在解压...");
                    string fileOutputDir = GetOutputDirectory(fileItem.FilePath);
                    AppendLog($"\n[解压] {fileItem.FileName} -> {fileOutputDir}", ConsoleColor.White);

                    // 构建一个只包含已知密码的列表，提高解压效率并避免错误密码尝试
                    List<string> passwordsToUse = new List<string>();
                    if (!string.IsNullOrEmpty(fileItem.FoundPassword))
                    {
                        passwordsToUse.Add(fileItem.FoundPassword);
                    }
                    else
                    {
                        // 如果是"无密码"状态，传入空列表或特定标识，或者依赖 AutoExtractAsync 处理空密码逻辑
                        // 这里为了稳妥，如果之前测试显示无密码，我们可以尝试传空列表或者直接调用不带密码的提取（如果API支持）
                        // 鉴于目前 API 是 AutoExtractAsync，我们传入一个包含空字符串或已知逻辑的列表
                        // 如果 FoundPassword 为 null 但状态是无密码，通常意味着不需要密码。
                        // 许多库将 null 或 empty 视为无密码。
                    }

                    // 使用 AutoExtractAsync，但因为我们已经知道密码（或无密码），它应该能迅速成功
                    // 如果 fileItem.FoundPassword 有值，我们可以优化 SevenZipExtractor 内部逻辑（如果可能），
                    // 或者这里简单地再次调用 AutoExtractAsync。
                    // 为了更好的体验，如果知道确切密码，最好只传那个密码。
                    
                    var result = await extractor.AutoExtractAsync(
                         fileItem.FilePath,
                         fileOutputDir,
                         passwordsToUse.Count > 0 ? passwordsToUse : _settings.GetAllPasswords(), // 如果不知道具体密码但标记为成功（比如无密码），传入全部以防万一，或者优化逻辑
                         onProgress: (msg) => AppendLog($"  {msg}", ConsoleColor.Gray));

                    Dispatcher.Invoke(() =>
                    {
                        if (result.Success)
                        {
                            fileItem.Status = $"解压成功: {result.FoundPassword ?? "无密码"}";
                            AppendLog($"  -> 成功", ConsoleColor.Green);
                        }
                        else
                        {
                            fileItem.Status = $"解压失败: {result.Message}";
                            AppendLog($"  -> 失败: {result.Message}", ConsoleColor.Red);
                        }
                    });

                    if (result.Success && _settings.FileAfterExtract != FileAction.Keep)
                    {
                        await HandleOriginalFile(fileItem.FilePath);
                    }
                    
                    await Task.Delay(100, token);
                }
                
                AppendLog("\n========== 手动解压完成 ==========", ConsoleColor.Green);
            }
            catch (OperationCanceledException)
            {
                AppendLog("\n操作已取消", ConsoleColor.Yellow);
            }
            catch (Exception ex)
            {
                AppendLog($"\n解压过程出错: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                UpdateUiState(false);
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        /// <summary>
        /// 自动解压流程：测试密码并解压
        /// </summary>
        private async Task StartExtractProcess()
        {
            UpdateUiState(true);
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            try
            {
                var passwords = _settings.GetAllPasswords();
                var extractor = new SevenZipExtractor(_settings.SevenZipPath);
                
                AppendLog($"加载密码本: 共 {passwords.Count} 个密码", ConsoleColor.Cyan);
                AppendLog($"开始自动解压流程...", ConsoleColor.Cyan);

                foreach (var fileItem in _fileList.ToList())
                {
                    if (token.IsCancellationRequested) break;

                    Dispatcher.Invoke(() => 
                    {
                        fileItem.Status = "正在处理...";
                        fileItem.FoundPassword = null;
                    });
                    
                    string fileOutputDir = GetOutputDirectory(fileItem.FilePath);
                    AppendLog($"\n========== 开始处理: {fileItem.FileName} ==========", ConsoleColor.White);
                    AppendLog($"输出目录: {fileOutputDir}", ConsoleColor.Cyan);

                    var result = await extractor.AutoExtractAsync(
                        fileItem.FilePath,
                        fileOutputDir,
                        passwords,
                        onProgress: (msg) =>
                        {
                            AppendLog($"  {msg}", ConsoleColor.Gray);
                        },
                        onPasswordTest: (pwd) =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                TxtStatusCurrentPassword.Text = $"测试中的密码: {pwd}";
                                if(!token.IsCancellationRequested) fileItem.Status = $"测试密码: {pwd}";
                            });
                        },
                        onPasswordFound: (pwd) =>
                        {
                            _settings.RecordPasswordUsage(pwd);
                            Dispatcher.Invoke(() => fileItem.FoundPassword = pwd);
                        });

                    Dispatcher.Invoke(() =>
                    {
                        TxtStatusCurrentPassword.Text = "测试中的密码: 无";
                        
                        if (result.Success)
                        {
                            fileItem.Status = result.FoundPassword != null 
                                ? $"解压成功 (密码: {result.FoundPassword})" 
                                : "解压成功 (无密码)";
                            AppendLog($"[{fileItem.FileName}] {result.Message}", ConsoleColor.Green);
                        }
                        else
                        {
                            fileItem.Status = $"解压失败: {result.Message}";
                            AppendLog($"[{fileItem.FileName}] {result.Message}", ConsoleColor.Red);
                        }
                    });

                    if (result.Success && _settings.FileAfterExtract != FileAction.Keep)
                    {
                        await HandleOriginalFile(fileItem.FilePath);
                    }

                    await Task.Delay(500, token); 
                }

                _settings.SavePasswordsToFile();
                AppendLog("\n========== 所有文件处理完成 ==========", ConsoleColor.Green);
            }
            catch (OperationCanceledException)
            {
                AppendLog("\n操作已取消", ConsoleColor.Yellow);
            }
            catch (Exception ex)
            {
                AppendLog($"\n处理过程出错: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                UpdateUiState(false);
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        #endregion

        #region 辅助方法

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
                    return Path.Combine(archiveDir, archiveNameWithoutExt);

                default:
                    return archiveDir;
            }
        }

        private async Task HandleOriginalFile(string filePath)
        {
            try
            {
                switch (_settings.FileAfterExtract)
                {
                    case FileAction.MoveToRecycleBin:
                        // 需要添加 Microsoft.VisualBasic.FileIO 引用
                        // Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(filePath, 
                        //     Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        //     Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        AppendLog($"  跳过移动到回收站（需要添加 Microsoft.VisualBasic 引用）", ConsoleColor.Yellow);
                        break;

                    case FileAction.Delete:
                        await Task.Run(() => File.Delete(filePath));
                        AppendLog($"  原文件已删除: {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                        break;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"  处理原文件失败: {ex.Message}", ConsoleColor.Red);
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
        
        /// <summary>
        /// 存储测试阶段找到的密码，用于手动解压阶段
        /// </summary>
        public string? FoundPassword { get; set; }
    }
}




