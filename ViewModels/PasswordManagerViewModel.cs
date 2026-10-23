using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.Core.Models;
using AutoUnpackTool.Core.Services;
using AutoUnpackTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoUnpackTool.ViewModels
{
    /// <summary>
    /// 密码本管理对话框 ViewModel。数据操作用 PasswordService，弹窗用 IDialogService，
    /// 剪贴板用 IClipboardService，视图自身只负责 DataGrid 选区与 Ctrl+A 这类控件能力。
    /// </summary>
    public partial class PasswordManagerViewModel : ObservableObject
    {
        private readonly AppSettings _settings;
        private readonly IDialogService _dialogs;
        private readonly IClipboardService _clipboard;
        private readonly PasswordService _passwordService;

        /// <summary>全量密码行（未过滤），DataGrid 绑定的是过滤后的 Passwords。</summary>
        private readonly List<PasswordItem> _passwordList = new List<PasswordItem>();

        public PasswordManagerViewModel(AppSettings settings, IDialogService dialogs, IClipboardService clipboard)
        {
            _settings = settings;
            _dialogs = dialogs;
            _clipboard = clipboard;
            _passwordService = new PasswordService(settings);

            _passwordFilePath = string.IsNullOrEmpty(_settings.PasswordFilePath)
                ? "未设置（使用默认密码）"
                : _settings.PasswordFilePath;

            LoadPasswords();
        }

        /// <summary>关闭对话框请求（由视图订阅并 Close()）。</summary>
        public event EventHandler? RequestClose;

        /// <summary>
        /// 由视图注入：返回 DataGrid 当前选中项（WPF 的 SelectedItems 不可绑定）。
        /// </summary>
        public Func<IReadOnlyList<PasswordItem>>? SelectedItemsProvider { get; set; }

        /// <summary>DataGrid 显示列表（过滤后）。</summary>
        public ObservableCollection<PasswordItem> Passwords { get; } = new ObservableCollection<PasswordItem>();

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private string _newPassword = string.Empty;

        [ObservableProperty]
        private bool _isOneTime;

        [ObservableProperty]
        private string _selectedCountText = "已选择: 0 项";

        [ObservableProperty]
        private string _passwordFilePath;

        [ObservableProperty]
        private PasswordItem? _selectedItem;

        partial void OnSearchTextChanged(string value)
        {
            ApplySearchFilter();
        }

        /// <summary>
        /// 加载密码列表：永久密码（已排序）+ 一次性密码（插到最前）。
        /// </summary>
        public void LoadPasswords()
        {
            _passwordList.Clear();
            _passwordList.AddRange(_passwordService.BuildItems());

            ApplySearchFilter();
        }

        /// <summary>重新编号（从 1 开始）。</summary>
        private void ReindexPasswords()
        {
            PasswordService.Reindex(_passwordList);
        }

        /// <summary>
        /// 应用搜索过滤：密码或类型包含搜索词即命中。
        /// </summary>
        private void ApplySearchFilter()
        {
            string searchText = (SearchText ?? string.Empty).Trim();

            Passwords.Clear();

            if (string.IsNullOrWhiteSpace(searchText))
            {
                foreach (var item in _passwordList)
                {
                    Passwords.Add(item);
                }
            }
            else
            {
                foreach (var item in _passwordList)
                {
                    if (item.Password.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        item.Type.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Passwords.Add(item);
                    }
                }
            }

            // 过滤后选区被清空，计数归零
            UpdateSelectedCount(0);
        }

        /// <summary>由视图在选区变化时回传选中数量。</summary>
        public void UpdateSelectedCount(int count)
        {
            SelectedCountText = $"已选择: {count} 项";
        }

        [RelayCommand]
        private void ClearSearch()
        {
            SearchText = string.Empty;
        }

        [RelayCommand]
        private void AddPassword()
        {
            string password = (NewPassword ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(password))
            {
                _dialogs.ShowWarning("请输入密码！");
                return;
            }

            // 检查是否已存在
            if (_passwordList.Any(p => p.Password == password))
            {
                _dialogs.ShowWarning("该密码已存在！");
                return;
            }

            bool isOneTime = IsOneTime;

            if (isOneTime)
            {
                // 一次性密码：添加到列表最前面
                _passwordList.Insert(0, new PasswordItem
                {
                    Index = 1,
                    Password = password,
                    UsageCount = 0,
                    Type = "一次性"
                });
                _settings.AddOneTimePassword(password);
            }
            else
            {
                // 永久密码：也添加到最前面（新密码排在最前）
                _passwordList.Insert(0, new PasswordItem
                {
                    Index = 1,
                    Password = password,
                    UsageCount = 0,
                    Type = "永久"
                });
                _settings.AddPermanentPassword(password, 0);
            }

            ReindexPasswords();

            // 清空输入框
            NewPassword = string.Empty;
            IsOneTime = false;

            ApplySearchFilter();

            _dialogs.ShowInfo($"密码已添加{(isOneTime ? "（一次性）" : "（永久）")}！", "成功");
        }

        /// <summary>删除单个选中项（右键菜单）。</summary>
        [RelayCommand]
        private void DeletePassword()
        {
            if (SelectedItem == null)
            {
                _dialogs.ShowWarning("请先选择要删除的密码！");
                return;
            }

            var selected = SelectedItem;

            if (selected.Type == "一次性")
            {
                _settings.RemoveOneTimePassword(selected.Password);
            }
            else
            {
                // 从配置文件中删除永久密码
                _settings.RemovePermanentPassword(selected.Password);
                // 立即保存到配置文件（带备份）
                SaveSettingsWithBackup();
            }

            _passwordList.Remove(selected);
            ReindexPasswords();
            ApplySearchFilter();

            _dialogs.ShowInfo("密码已删除！", "成功");
        }

        [RelayCommand]
        private void MoveUp()
        {
            if (SelectedItem == null)
                return;

            int currentIndex = _passwordList.IndexOf(SelectedItem);
            if (currentIndex > 0)
            {
                _passwordList.RemoveAt(currentIndex);
                _passwordList.Insert(currentIndex - 1, SelectedItem);
                ReindexPasswords();
                ApplySearchFilter();
            }
        }

        [RelayCommand]
        private void MoveDown()
        {
            if (SelectedItem == null)
                return;

            int currentIndex = _passwordList.IndexOf(SelectedItem);
            if (currentIndex >= 0 && currentIndex < _passwordList.Count - 1)
            {
                _passwordList.RemoveAt(currentIndex);
                _passwordList.Insert(currentIndex + 1, SelectedItem);
                ReindexPasswords();
                ApplySearchFilter();
            }
        }

        /// <summary>
        /// 批量删除选中项（批量操作栏）。
        /// </summary>
        [RelayCommand]
        private void BatchDelete()
        {
            var selected = SelectedItemsProvider?.Invoke() ?? Array.Empty<PasswordItem>();
            if (selected.Count == 0)
            {
                _dialogs.ShowWarning("请先选择要删除的密码！");
                return;
            }

            if (!_dialogs.ShowConfirm($"确定要删除选中的 {selected.Count} 个密码吗？", "确认删除"))
                return;

            var itemsToDelete = new List<PasswordItem>(selected);
            bool hasPermanentDeleted = false;

            foreach (var item in itemsToDelete)
            {
                if (item.Type == "一次性")
                {
                    _settings.RemoveOneTimePassword(item.Password);
                }
                else
                {
                    // 从配置文件中删除永久密码
                    _settings.RemovePermanentPassword(item.Password);
                    hasPermanentDeleted = true;
                }

                _passwordList.Remove(item);
            }

            // 如果有永久密码被删除，立即保存到配置文件（带备份）
            if (hasPermanentDeleted)
            {
                SaveSettingsWithBackup();
            }

            ReindexPasswords();
            ApplySearchFilter();

            _dialogs.ShowInfo($"已删除 {itemsToDelete.Count} 个密码！", "成功");
        }

        /// <summary>复制选中密码到剪贴板（每行一个）。</summary>
        [RelayCommand]
        private void CopyPasswords()
        {
            var selected = SelectedItemsProvider?.Invoke() ?? Array.Empty<PasswordItem>();
            if (selected.Count == 0)
            {
                _dialogs.ShowWarning("请先选择要复制的密码！");
                return;
            }

            // 只提取密码，每行一个
            var passwords = selected.Select(item => item.Password).ToList();
            string clipboardText = string.Join(Environment.NewLine, passwords);

            try
            {
                _clipboard.SetText(clipboardText);

                string message = passwords.Count == 1
                    ? "已复制 1 个密码到剪贴板"
                    : $"已复制 {passwords.Count} 个密码到剪贴板";

                _dialogs.ShowInfo(message, "成功");
            }
            catch (Exception ex)
            {
                _dialogs.ShowError($"复制到剪贴板失败：{ex.Message}");
            }
        }

        /// <summary>浏览密码本文件并自动加载到当前列表（临时，一次性使用）。</summary>
        [RelayCommand]
        private void BrowseFile()
        {
            string? filePath = _dialogs.SelectFile("选择密码本文件", "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*");
            if (string.IsNullOrEmpty(filePath))
                return;

            PasswordFilePath = filePath;

            try
            {
                var (loadedCount, _) = _passwordService.LoadFromFile(filePath, _passwordList);
                if (loadedCount > 0)
                {
                    ReindexPasswords();
                    ApplySearchFilter();
                }
            }
            catch (Exception ex)
            {
                _dialogs.ShowError($"加载失败：{ex.Message}");
            }
        }

        /// <summary>把当前列表（跳过一次性）永久写入配置文件。</summary>
        [RelayCommand]
        private void ImportToSettings()
        {
            if (_passwordList.Count == 0)
            {
                _dialogs.ShowWarning("当前密码列表为空，请先添加或加载密码！");
                return;
            }

            try
            {
                var (importedCount, skippedCount) = _passwordService.ImportToSettings(_passwordList);

                // 保存到配置文件
                _settings.Save();

                // 重新加载密码列表以更新UI
                LoadPasswords();

                string message = importedCount > 0
                    ? $"导入成功！\n\n新增密码: {importedCount} 个\n更新密码: {skippedCount} 个\n配置文件中总密码数: {_passwordService.PermanentPasswordCount} 个\n\n密码已永久保存到配置文件，下次启动自动加载。"
                    : "导入完成！\n\n所有密码都已存在于配置文件中。";

                _dialogs.ShowInfo(message, "导入完成");
            }
            catch (Exception ex)
            {
                _dialogs.ShowError($"导入失败：{ex.Message}");
            }
        }

        /// <summary>导出密码本到文本文件。</summary>
        [RelayCommand]
        private void ExportToFile()
        {
            string? filePath = _dialogs.SelectSaveFile("导出密码本", "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*", "passwords.txt");
            if (string.IsNullOrEmpty(filePath))
                return;

            try
            {
                _passwordService.ExportToFile(filePath);
                _dialogs.ShowInfo($"密码本导出成功！\n\n导出路径：{filePath}", "成功");
            }
            catch (Exception ex)
            {
                _dialogs.ShowError($"导出失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 保存配置到文件（带备份）。
        /// </summary>
        private void SaveSettingsWithBackup()
        {
            try
            {
                _passwordService.SaveWithBackup();
            }
            catch (Exception ex)
            {
                _dialogs.ShowError($"保存配置失败：{ex.Message}");
            }
        }

        [RelayCommand]
        private void Close()
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }
}
