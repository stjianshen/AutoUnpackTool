using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace AutoUnpackTool
{
    public partial class SettingsDialog : Window
    {
        public AppSettings Settings { get; private set; }

        public SettingsDialog(AppSettings currentSettings)
        {
            InitializeComponent();
            Settings = currentSettings ?? new AppSettings();

            // 显示配置文件路径
            TxtConfigPath.Text = AppSettings.ConfigFilePath;

            // 加载当前设置
            LoadSettings();

            // 滑块值变化事件
            SliderThreads.ValueChanged += (s, e) =>
            {
                TxtThreadsCount.Text = ((int)e.NewValue).ToString();
            };
            
            // 智能路径处理复选框变化事件
            ChkSmartPathProcessing.Checked += (s, e) =>
            {
                PnlSmartPathMode.Visibility = Visibility.Visible;
            };
            
            ChkSmartPathProcessing.Unchecked += (s, e) =>
            {
                PnlSmartPathMode.Visibility = Visibility.Collapsed;
            };
        }

        private void LoadSettings()
        {
            TxtSevenZipPath.Text = Settings.SevenZipPath;
            TxtOutputDir.Text = Settings.OutputDir;
            SliderThreads.Value = Settings.ThreadCount;
            TxtThreadsCount.Text = Settings.ThreadCount.ToString();
            ChkShowCliWindow.IsChecked = Settings.ShowCliWindow;
            ChkSmartPathProcessing.IsChecked = Settings.EnableSmartPathProcessing;
            ChkStegoDetection.IsChecked = Settings.EnableStegoDetection;
            TxtStegoMinSizeMB.Text = Settings.StegoDetectionMinFileSizeMB.ToString();
            
            // 根据智能路径处理是否启用来显示/隐藏模式选择
            PnlSmartPathMode.Visibility = Settings.EnableSmartPathProcessing ? Visibility.Visible : Visibility.Collapsed;
            
            // 加载智能路径处理模式
            switch (Settings.SmartPathProcessingMode)
            {
                case SmartPathMode.Concatenate:
                    RdoSmartPathConcatenate.IsChecked = true;
                    break;
                case SmartPathMode.SmartSelect:
                    RdoSmartPathSmartSelect.IsChecked = true;
                    break;
            }

            // 设置文件处理方式
            switch (Settings.FileAfterExtract)
            {
                case FileAction.Keep:
                    RdoKeepOriginal.IsChecked = true;
                    break;
                case FileAction.MoveToRecycleBin:
                    RdoMoveToRecycleBin.IsChecked = true;
                    break;
                case FileAction.Delete:
                    RdoDeleteOriginal.IsChecked = true;
                    break;
            }

            // 设置输出模式
            switch (Settings.OutputMode)
            {
                case OutputMode.SpecificDir:
                    RdoOutputToSpecificDir.IsChecked = true;
                    PnlOutputDir.Visibility = Visibility.Visible;
                    break;
                case OutputMode.ArchiveDir:
                    RdoOutputToArchiveDir.IsChecked = true;
                    PnlOutputDir.Visibility = Visibility.Collapsed;
                    break;
                case OutputMode.ArchiveFolder:
                    RdoOutputToArchiveFolder.IsChecked = true;
                    PnlOutputDir.Visibility = Visibility.Collapsed;
                    break;
            }

            // 设置解压模式
            switch (Settings.ExtractMode)
            {
                case ExtractMode.Manual:
                    RdoManualExtract.IsChecked = true;
                    break;
                case ExtractMode.Auto:
                    RdoAutoExtract.IsChecked = true;
                    break;
            }
            
            // 加载扩展名设置
            TxtArchiveExtensions.Text = Settings.ArchiveExtensions;
            TxtExcludedExtensions.Text = Settings.ExcludedExtensions;
            
            // 加载黑名单设置
            TxtBlacklistFiles.Text = Settings.BlacklistFiles;
        }

        private void BtnBrowse7z_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 7z.exe 文件",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                FileName = "7z.exe"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtSevenZipPath.Text = dialog.FileName;
            }
        }

        private void OutputMode_Checked(object sender, RoutedEventArgs e)
        {
            // 根据选择的输出模式显示/隐藏输出目录选择
            if (RdoOutputToSpecificDir?.IsChecked == true)
            {
                PnlOutputDir.Visibility = Visibility.Visible;
            }
            else
            {
                PnlOutputDir.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnBrowseOutputDir_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择输出目录"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtOutputDir.Text = dialog.FolderName;
            }
        }

        private void BtnOpenConfigFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string configPath = AppSettings.ConfigFilePath;
                string? folderPath = Path.GetDirectoryName(configPath);
                
                if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
                {
                    // 打开文件夹并选中文件
                    Process.Start("explorer.exe", $"/select,\"{configPath}\"");
                }
                else
                {
                    MessageBox.Show($"配置文件路径不存在：\n{configPath}", 
                        "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开文件夹失败：\n{ex.Message}", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 验证 7z.exe 路径
            if (string.IsNullOrWhiteSpace(TxtSevenZipPath.Text))
            {
                MessageBox.Show("请设置 7z.exe 的路径！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!File.Exists(TxtSevenZipPath.Text))
            {
                MessageBox.Show($"7z.exe 文件不存在，请检查路径！\n\n路径: {TxtSevenZipPath.Text}", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 验证输出目录（如果选择了输出到指定目录）
            if (RdoOutputToSpecificDir?.IsChecked == true && string.IsNullOrWhiteSpace(TxtOutputDir.Text))
            {
                MessageBox.Show("请选择输出目录！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 验证隐写探测大小下限
            if (!int.TryParse(TxtStegoMinSizeMB.Text.Trim(), out int stegoMinSizeMb) || stegoMinSizeMb <= 0)
            {
                MessageBox.Show("隐写探测文件大小下限必须是大于 0 的整数（单位MB）！",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // 保存设置
                Settings.SevenZipPath = TxtSevenZipPath.Text;
                Settings.OutputDir = TxtOutputDir.Text;
                Settings.ThreadCount = (int)SliderThreads.Value;

                // 保存文件处理方式
                if (RdoKeepOriginal.IsChecked == true)
                    Settings.FileAfterExtract = FileAction.Keep;
                else if (RdoMoveToRecycleBin.IsChecked == true)
                    Settings.FileAfterExtract = FileAction.MoveToRecycleBin;
                else if (RdoDeleteOriginal.IsChecked == true)
                    Settings.FileAfterExtract = FileAction.Delete;

                // 保存输出模式
                if (RdoOutputToSpecificDir?.IsChecked == true)
                    Settings.OutputMode = OutputMode.SpecificDir;
                else if (RdoOutputToArchiveDir?.IsChecked == true)
                    Settings.OutputMode = OutputMode.ArchiveDir;
                else if (RdoOutputToArchiveFolder?.IsChecked == true)
                    Settings.OutputMode = OutputMode.ArchiveFolder;

                // 保存解压模式
                if (RdoManualExtract?.IsChecked == true)
                    Settings.ExtractMode = ExtractMode.Manual;
                else if (RdoAutoExtract?.IsChecked == true)
                    Settings.ExtractMode = ExtractMode.Auto;

                // 保存CLI窗口设置
                Settings.ShowCliWindow = ChkShowCliWindow.IsChecked == true;
                
                // 保存智能路径处理设置
                Settings.EnableSmartPathProcessing = ChkSmartPathProcessing.IsChecked == true;
                Settings.EnableStegoDetection = ChkStegoDetection.IsChecked == true;
                Settings.StegoDetectionMinFileSizeMB = stegoMinSizeMb;
                
                // 保存智能路径处理模式
                if (RdoSmartPathConcatenate?.IsChecked == true)
                    Settings.SmartPathProcessingMode = SmartPathMode.Concatenate;
                else if (RdoSmartPathSmartSelect?.IsChecked == true)
                    Settings.SmartPathProcessingMode = SmartPathMode.SmartSelect;
                
                // 保存扩展名设置
                Settings.ArchiveExtensions = TxtArchiveExtensions.Text.Trim();
                Settings.ExcludedExtensions = TxtExcludedExtensions.Text.Trim();
                
                // 保存黑名单设置
                Settings.BlacklistFiles = TxtBlacklistFiles.Text.Trim();

                // 保存到文件
                Settings.Save();

                // 在主窗口日志中显示保存成功，不弹窗
                System.Diagnostics.Debug.WriteLine("✅ 配置保存成功！");
                
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ 保存设置失败：\n{ex.Message}", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
