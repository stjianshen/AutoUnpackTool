using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoUnpackTool.ViewModels
{
    /// <summary>
    /// 设置对话框视图模型：字段加载/校验/保存映射全部在此，视图只做绑定与关闭。
    /// 保存成功时通过对 Settings 属性原地写入并抛出 RequestClose(true)。
    /// </summary>
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly IDialogService _dialogs;

        /// <summary>对话框保存后的设置实例（与传入实例一致）</summary>
        public AppSettings Settings { get; }

        /// <summary>关闭请求：true=保存，false=取消</summary>
        public event Action<bool>? RequestClose;

        public string ConfigPath => AppSettings.ConfigFilePath;

        [ObservableProperty]
        private string _sevenZipPath = string.Empty;

        [ObservableProperty]
        private string _outputDir = string.Empty;

        [ObservableProperty]
        private double _threadCountValue = 2;

        [ObservableProperty]
        private bool _showCliWindow;

        [ObservableProperty]
        private bool _smartPathProcessing;

        [ObservableProperty]
        private bool _multiVolumeDetection;

        [ObservableProperty]
        private bool _autoCleanNames;

        [ObservableProperty]
        private bool _stegoDetection;

        [ObservableProperty]
        private string _stegoMinSizeMB = "10";

        [ObservableProperty]
        private string _archiveExtensions = string.Empty;

        [ObservableProperty]
        private string _excludedExtensions = string.Empty;

        [ObservableProperty]
        private string _blacklistSummary = string.Empty;

        // 解压后文件处理方式
        [ObservableProperty]
        private bool _keepOriginal;

        [ObservableProperty]
        private bool _moveToRecycleBin;

        [ObservableProperty]
        private bool _deleteOriginal;

        // 解压模式
        [ObservableProperty]
        private bool _manualExtract;

        [ObservableProperty]
        private bool _autoExtract;

        // 输出模式
        [ObservableProperty]
        private bool _outputToSpecificDir;

        [ObservableProperty]
        private bool _outputToArchiveDir;

        [ObservableProperty]
        private bool _outputToArchiveFolder;

        // 智能路径处理模式
        [ObservableProperty]
        private bool _smartPathConcatenate;

        [ObservableProperty]
        private bool _smartPathSmartSelect;

        /// <summary>智能路径处理模式面板可见性</summary>
        public bool IsSmartPathModeVisible => SmartPathProcessing;

        /// <summary>输出目录面板可见性（仅“输出到指定目录”时显示）</summary>
        public bool IsOutputDirVisible => OutputToSpecificDir;

        /// <summary>线程数（整数）</summary>
        public int ThreadCount => (int)Math.Round(ThreadCountValue);

        /// <summary>线程数显示文本</summary>
        public string ThreadCountText => ThreadCount.ToString();

        public SettingsViewModel(AppSettings currentSettings, IDialogService dialogs)
        {
            _dialogs = dialogs;
            Settings = currentSettings ?? new AppSettings();

            LoadSettings();
        }

        partial void OnThreadCountValueChanged(double value)
        {
            OnPropertyChanged(nameof(ThreadCount));
            OnPropertyChanged(nameof(ThreadCountText));
        }

        partial void OnSmartPathProcessingChanged(bool value)
        {
            OnPropertyChanged(nameof(IsSmartPathModeVisible));
        }

        partial void OnOutputToSpecificDirChanged(bool value)
        {
            OnPropertyChanged(nameof(IsOutputDirVisible));
        }

        private void LoadSettings()
        {
            SevenZipPath = Settings.SevenZipPath;
            OutputDir = Settings.OutputDir;
            ThreadCountValue = Settings.ThreadCount;
            ShowCliWindow = Settings.ShowCliWindow;
            SmartPathProcessing = Settings.EnableSmartPathProcessing;
            MultiVolumeDetection = Settings.EnableMultiVolumeDetection;
            AutoCleanNames = Settings.AutoCleanAntiCensorshipNames;
            StegoDetection = Settings.EnableStegoDetection;
            StegoMinSizeMB = Settings.StegoDetectionMinFileSizeMB.ToString();

            // 加载智能路径处理模式
            switch (Settings.SmartPathProcessingMode)
            {
                case SmartPathMode.Concatenate:
                    SmartPathConcatenate = true;
                    break;
                case SmartPathMode.SmartSelect:
                    SmartPathSmartSelect = true;
                    break;
            }

            // 设置文件处理方式
            switch (Settings.FileAfterExtract)
            {
                case FileAction.Keep:
                    KeepOriginal = true;
                    break;
                case FileAction.MoveToRecycleBin:
                    MoveToRecycleBin = true;
                    break;
                case FileAction.Delete:
                    DeleteOriginal = true;
                    break;
            }

            // 设置输出模式
            switch (Settings.OutputMode)
            {
                case OutputMode.SpecificDir:
                    OutputToSpecificDir = true;
                    break;
                case OutputMode.ArchiveDir:
                    OutputToArchiveDir = true;
                    break;
                case OutputMode.ArchiveFolder:
                    OutputToArchiveFolder = true;
                    break;
            }

            // 设置解压模式
            switch (Settings.ExtractMode)
            {
                case ExtractMode.Manual:
                    ManualExtract = true;
                    break;
                case ExtractMode.Auto:
                    AutoExtract = true;
                    break;
            }

            // 加载扩展名设置
            ArchiveExtensions = Settings.ArchiveExtensions;
            ExcludedExtensions = Settings.ExcludedExtensions;

            // 加载黑名单规则摘要
            UpdateBlacklistSummary();
        }

        /// <summary>
        /// 刷新黑名单规则摘要
        /// </summary>
        private void UpdateBlacklistSummary()
        {
            int total = Settings.BlacklistRules?.Count ?? 0;
            int enabled = Settings.BlacklistRules?.Count(r => r != null && r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern)) ?? 0;
            BlacklistSummary = $"共 {total} 条规则（启用 {enabled} 条）";
        }

        /// <summary>
        /// 浏览选择 7z.exe
        /// </summary>
        [RelayCommand]
        private void BrowseSevenZip()
        {
            var path = _dialogs.SelectFile("选择 7z.exe 文件", "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*", "7z.exe");
            if (path != null)
            {
                SevenZipPath = path;
            }
        }

        /// <summary>
        /// 浏览选择输出目录
        /// </summary>
        [RelayCommand]
        private void BrowseOutputDir()
        {
            var folder = _dialogs.SelectFolder("选择输出目录");
            if (folder != null)
            {
                OutputDir = folder;
            }
        }

        /// <summary>
        /// 打开配置文件所在文件夹
        /// </summary>
        [RelayCommand]
        private void OpenConfigFolder()
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
                    _dialogs.ShowWarning($"配置文件路径不存在：\n{configPath}", "错误");
                }
            }
            catch (Exception ex)
            {
                _dialogs.ShowWarning($"打开文件夹失败：\n{ex.Message}", "错误");
            }
        }

        /// <summary>
        /// 打开黑名单表格管理对话框
        /// </summary>
        [RelayCommand]
        private void ManageBlacklist()
        {
            _dialogs.ShowBlacklist(Settings);
            UpdateBlacklistSummary();
        }

        /// <summary>
        /// 保存配置（校验 + 字段映射 + 落盘）
        /// </summary>
        [RelayCommand]
        private void Save()
        {
            // 验证 7z.exe 路径
            if (string.IsNullOrWhiteSpace(SevenZipPath))
            {
                _dialogs.ShowWarning("请设置 7z.exe 的路径！");
                return;
            }

            if (!File.Exists(SevenZipPath))
            {
                _dialogs.ShowWarning($"7z.exe 文件不存在，请检查路径！\n\n 路径: {SevenZipPath}", "错误");
                return;
            }

            // 验证输出目录（如果选择了输出到指定目录）
            if (OutputToSpecificDir && string.IsNullOrWhiteSpace(OutputDir))
            {
                _dialogs.ShowWarning("请选择输出目录！");
                return;
            }

            // 验证隐写探测大小下限
            if (!int.TryParse(StegoMinSizeMB.Trim(), out int stegoMinSizeMb) || stegoMinSizeMb <= 0)
            {
                _dialogs.ShowWarning("隐写探测文件大小下限必须是大于 0 的整数（单位MB）！");
                return;
            }

            try
            {
                // 保存设置
                Settings.SevenZipPath = SevenZipPath;
                Settings.OutputDir = OutputDir;
                Settings.ThreadCount = ThreadCount;

                // 保存文件处理方式
                if (KeepOriginal)
                    Settings.FileAfterExtract = FileAction.Keep;
                else if (MoveToRecycleBin)
                    Settings.FileAfterExtract = FileAction.MoveToRecycleBin;
                else if (DeleteOriginal)
                    Settings.FileAfterExtract = FileAction.Delete;

                // 保存输出模式
                if (OutputToSpecificDir)
                    Settings.OutputMode = OutputMode.SpecificDir;
                else if (OutputToArchiveDir)
                    Settings.OutputMode = OutputMode.ArchiveDir;
                else if (OutputToArchiveFolder)
                    Settings.OutputMode = OutputMode.ArchiveFolder;

                // 保存解压模式
                if (ManualExtract)
                    Settings.ExtractMode = ExtractMode.Manual;
                else if (AutoExtract)
                    Settings.ExtractMode = ExtractMode.Auto;

                // 保存CLI窗口设置
                Settings.ShowCliWindow = ShowCliWindow;

                // 保存智能路径处理设置
                Settings.EnableSmartPathProcessing = SmartPathProcessing;

                // 保存分卷检测设置
                Settings.EnableMultiVolumeDetection = MultiVolumeDetection;

                // 保存防和谐文件名清理设置
                Settings.AutoCleanAntiCensorshipNames = AutoCleanNames;

                Settings.EnableStegoDetection = StegoDetection;
                Settings.StegoDetectionMinFileSizeMB = stegoMinSizeMb;

                // 保存智能路径处理模式
                if (SmartPathConcatenate)
                    Settings.SmartPathProcessingMode = SmartPathMode.Concatenate;
                else if (SmartPathSmartSelect)
                    Settings.SmartPathProcessingMode = SmartPathMode.SmartSelect;

                // 保存扩展名设置
                Settings.ArchiveExtensions = ArchiveExtensions.Trim();
                Settings.ExcludedExtensions = ExcludedExtensions.Trim();

                // 黑名单规则由 BlacklistManagerDialog 直接保存到 Settings.BlacklistRules

                // 保存到文件
                Settings.Save();

                // 在主窗口日志中显示保存成功，不弹窗
                System.Diagnostics.Debug.WriteLine("✅ 配置保存成功！");

                RequestClose?.Invoke(true);
            }
            catch (Exception ex)
            {
                _dialogs.ShowWarning($"❌ 保存设置失败：\n{ex.Message}", "错误");
            }
        }

        /// <summary>
        /// 取消
        /// </summary>
        [RelayCommand]
        private void Cancel()
        {
            RequestClose?.Invoke(false);
        }
    }
}
