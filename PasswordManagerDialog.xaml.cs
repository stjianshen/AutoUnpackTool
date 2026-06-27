using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AutoUnpackTool
{
    public partial class PasswordManagerDialog : Window
    {
        private ObservableCollection<PasswordItem> _passwordList = new ObservableCollection<PasswordItem>();
        private ObservableCollection<PasswordItem> _filteredPasswordList = new ObservableCollection<PasswordItem>();
        private AppSettings _settings;
        private string _searchText = "";

        public PasswordManagerDialog(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            DgPasswords.ItemsSource = _filteredPasswordList; // 使用过滤后的列表

            // 加载密码
            LoadPasswords();

            // 显示当前密码本文件路径
            TxtPasswordFilePath.Text = string.IsNullOrEmpty(_settings.PasswordFilePath) 
                ? "未设置（使用默认密码）" 
                : _settings.PasswordFilePath;

            // 绑定选择变化事件以更新选中计数
            DgPasswords.SelectionChanged += DgPasswords_SelectionChanged;
            
            // 绑定键盘事件以支持Ctrl+A
            DgPasswords.PreviewKeyDown += DgPasswords_PreviewKeyDown;
        }

        private void LoadPasswords()
        {
            _passwordList.Clear();

            // 加载永久密码（从文件）- 已按综合评分排序
            var permanentPasswords = _settings.LoadPasswordsFromFile();
            int index = 1;
            foreach (var pwd in permanentPasswords)
            {
                _passwordList.Add(new PasswordItem
                {
                    Index = index++,
                    Password = pwd.Password,
                    UsageCount = pwd.UsageCount,
                    Type = "永久",
                    LastUsedTime = pwd.LastUsedTime
                });
            }

            // 加载一次性密码（本次运行添加的）- 插入到最前面
            for (int i = _settings.OneTimePasswords.Count - 1; i >= 0; i--)
            {
                _passwordList.Insert(0, new PasswordItem
                {
                    Index = 0,
                    Password = _settings.OneTimePasswords[i],
                    UsageCount = 0,
                    Type = "一次性",
                    LastUsedTime = DateTime.Now
                });
            }

            // 重新编号
            ReindexPasswords();
            
            // 应用搜索过滤
            ApplySearchFilter();
        }

        private void ReindexPasswords()
        {
            for (int i = 0; i < _passwordList.Count; i++)
            {
                _passwordList[i].Index = i + 1;
            }
        }

        /// <summary>
        /// 应用搜索过滤
        /// </summary>
        private void ApplySearchFilter()
        {
            _filteredPasswordList.Clear();
            
            if (string.IsNullOrWhiteSpace(_searchText))
            {
                // 没有搜索文本，显示所有密码
                foreach (var item in _passwordList)
                {
                    _filteredPasswordList.Add(item);
                }
            }
            else
            {
                // 根据搜索文本过滤
                foreach (var item in _passwordList)
                {
                    if (item.Password.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        item.Type.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _filteredPasswordList.Add(item);
                    }
                }
            }
            
            // 更新选中计数
            UpdateSelectedCount();
        }

        /// <summary>
        /// 搜索文本变化事件
        /// </summary>
        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = TxtSearch.Text.Trim();
            ApplySearchFilter();
        }

        /// <summary>
        /// 清除搜索
        /// </summary>
        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Clear();
            _searchText = "";
            ApplySearchFilter();
        }

        /// <summary>
        /// 全选
        /// </summary>
        private void BtnSelectAll_Click(object? sender, RoutedEventArgs? e)
        {
            DgPasswords.SelectAll();
            UpdateSelectedCount();
        }

        /// <summary>
        /// 反选
        /// </summary>
        private void BtnInvertSelection_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = new List<PasswordItem>(DgPasswords.SelectedItems.Cast<PasswordItem>());
            
            // 先取消所有选择
            DgPasswords.UnselectAll();
            
            // 选择之前未选中的项
            foreach (var item in _filteredPasswordList)
            {
                if (!selectedItems.Contains(item))
                {
                    DgPasswords.SelectedItems.Add(item);
                }
            }
            
            UpdateSelectedCount();
        }

        /// <summary>
        /// 批量删除选中项
        /// </summary>
        private void BtnBatchDelete_Click(object sender, RoutedEventArgs e)
        {
            if (DgPasswords.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先选择要删除的密码！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"确定要删除选中的 {DgPasswords.SelectedItems.Count} 个密码吗？", 
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
                
            if (result != MessageBoxResult.Yes)
                return;

            // 收集要删除的项
            var itemsToDelete = new List<PasswordItem>(DgPasswords.SelectedItems.Cast<PasswordItem>());
            
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
            ApplySearchFilter(); // 重新应用过滤
            
            MessageBox.Show($"已删除 {itemsToDelete.Count} 个密码！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// DataGrid选择变化事件
        /// </summary>
        private void DgPasswords_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectedCount();
        }

        /// <summary>
        /// DataGrid键盘事件 - 支持Ctrl+A全选
        /// </summary>
        private void DgPasswords_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // 检测Ctrl+A组合键
            if (e.Key == System.Windows.Input.Key.A && 
                (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
            {
                BtnSelectAll_Click(null, null);
                e.Handled = true; // 阻止默认行为
            }
        }

        /// <summary>
        /// 更新选中计数显示
        /// </summary>
        private void UpdateSelectedCount()
        {
            TxtSelectedCount.Text = $"已选择: {DgPasswords.SelectedItems.Count} 项";
        }

        /// <summary>
        /// 复制选中的密码到剪贴板
        /// </summary>
        private void CopySelectedPasswordsToClipboard()
        {
            if (DgPasswords.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先选择要复制的密码！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedItems = DgPasswords.SelectedItems.Cast<PasswordItem>().ToList();
            
            // 只提取密码，每行一个
            var passwords = selectedItems.Select(item => item.Password).ToList();
            string clipboardText = string.Join(Environment.NewLine, passwords);

            try
            {
                System.Windows.Clipboard.SetText(clipboardText);
                
                string message = passwords.Count == 1
                    ? $"已复制 1 个密码到剪贴板"
                    : $"已复制 {passwords.Count} 个密码到剪贴板";
                    
                MessageBox.Show(message, "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"复制到剪贴板失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 右键菜单 - 复制密码
        /// </summary>
        private void MenuItem_CopyPassword_Click(object sender, RoutedEventArgs e)
        {
            CopySelectedPasswordsToClipboard();
        }

        /// <summary>
        /// 批量操作栏 - 复制密码按钮
        /// </summary>
        private void BtnCopyPasswords_Click(object sender, RoutedEventArgs e)
        {
            CopySelectedPasswordsToClipboard();
        }

        private void BtnAddPassword_Click(object sender, RoutedEventArgs e)
        {
            string password = TxtNewPassword.Text.Trim();
            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show("请输入密码！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 检查是否已存在
            if (_passwordList.Any(p => p.Password == password))
            {
                MessageBox.Show("该密码已存在！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool isOneTime = ChkOneTime.IsChecked == true;

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
            TxtNewPassword.Clear();
            ChkOneTime.IsChecked = false;

            MessageBox.Show($"密码已添加{(isOneTime ? "（一次性）" : "（永久）")}！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnDeletePassword_Click(object sender, RoutedEventArgs e)
        {
            if (DgPasswords.SelectedItem is PasswordItem selected)
            {
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
                ApplySearchFilter(); // 重新应用过滤以刷新显示
                

                MessageBox.Show("密码已删除！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("请先选择要删除的密码！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (DgPasswords.SelectedItem is PasswordItem selected)
            {
                int currentIndex = _passwordList.IndexOf(selected);
                if (currentIndex > 0)
                {
                    _passwordList.Move(currentIndex, currentIndex - 1);
                    ReindexPasswords();
                    
                }
            }
        }

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (DgPasswords.SelectedItem is PasswordItem selected)
            {
                int currentIndex = _passwordList.IndexOf(selected);
                if (currentIndex < _passwordList.Count - 1)
                {
                    _passwordList.Move(currentIndex, currentIndex + 1);
                    ReindexPasswords();
                    
                }
            }
        }

        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择密码本文件",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                string filePath = dialog.FileName;
                TxtPasswordFilePath.Text = filePath;
                
                // 自动加载密码到当前列表（一次性使用，去重）
                LoadPasswordsFromFileToUI(filePath);
            }
        }

        /// <summary>
        /// 从文件加载密码到UI列表（一次性使用，不与配置文件合并）
        /// </summary>
        private void LoadPasswordsFromFileToUI(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(filePath);
                int loadedCount = 0;
                int skippedCount = 0;

                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                        continue;

                    // 解析密码（支持TAB分隔格式）
                    string password = trimmed;
                    string[] parts = trimmed.Split(new[] { "\t\t" }, StringSplitOptions.None);
                    if (parts.Length >= 1)
                    {
                        password = parts[0].Trim();
                    }

                    if (string.IsNullOrEmpty(password))
                        continue;

                    // 去重：检查是否已存在于当前UI列表
                    if (_passwordList.Any(p => p.Password == password))
                    {
                        skippedCount++;
                        continue;
                    }

                    // 添加到UI列表（永久类型，但不保存到配置，仅本次会话使用）
                    _passwordList.Add(new PasswordItem
                    {
                        Index = _passwordList.Count + 1,
                        Password = password,
                        UsageCount = 0,
                        Type = "临时",
                        LastUsedTime = DateTime.MinValue
                    });
                    loadedCount++;
                }

                if (loadedCount > 0)
                {
                    
                    Console.WriteLine($"从文件加载 {loadedCount} 个密码，跳过重复 {skippedCount} 个");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载密码文件失败: {ex.Message}");
                MessageBox.Show($"加载失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 从密码本文件导入到配置文件（JSON）- 将当前UI列表的密码写入配置
        /// </summary>
        private void BtnImportPassword_Click(object sender, RoutedEventArgs e)
        {
            if (_passwordList.Count == 0)
            {
                MessageBox.Show("当前密码列表为空，请先添加或加载密码！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                int importedCount = 0;
                int skippedCount = 0;

                // 遍历当前UI列表中的所有密码（除一次性密码外）
                foreach (var item in _passwordList)
                {
                    // 跳过一次性密码（它们不会导入到配置）
                    if (item.Type == "一次性")
                    {
                        continue;
                    }

                    // 如果密码不存在于配置文件，添加它
                    if (!_settings.PermanentPasswords.Any(p => p.Password == item.Password))
                    {
                        _settings.PermanentPasswords.Add(new PasswordEntry
                        {
                            Password = item.Password,
                            UsageCount = item.UsageCount,
                            LastUsedTime = item.LastUsedTime
                        });
                        importedCount++;
                    }
                    else
                    {
                        // 如果已存在，更新使用信息
                        var existing = _settings.PermanentPasswords.First(p => p.Password == item.Password);
                        existing.UsageCount = Math.Max(existing.UsageCount, item.UsageCount);
                        if (item.LastUsedTime > existing.LastUsedTime)
                        {
                            existing.LastUsedTime = item.LastUsedTime;
                        }
                        skippedCount++;
                    }
                }

                // 保存到配置文件
                _settings.Save();

                // 重新加载密码列表以更新UI
                LoadPasswords();
                

                string message = importedCount > 0
                    ? $"导入成功！\n\n新增密码: {importedCount} 个\n更新密码: {skippedCount} 个\n配置文件中总密码数: {_settings.PermanentPasswords.Count} 个\n\n密码已永久保存到配置文件，下次启动自动加载。"
                    : $"导入完成！\n\n所有密码都已存在于配置文件中。";

                MessageBox.Show(message, "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
                Console.WriteLine($"密码导入完成，配置文件已更新。新增: {importedCount}, 更新: {skippedCount}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"导入失败: {ex.Message}");
                MessageBox.Show($"导入失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 从配置文件导出密码本到文本文件
        /// </summary>
        private void BtnExportPassword_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出密码本",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = "txt",
                FileName = "passwords.txt"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    ExportPasswordsToFile(dialog.FileName);
                    MessageBox.Show($"密码本导出成功！\n\n导出路径：{dialog.FileName}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// 导出密码到文本文件
        /// </summary>
        private void ExportPasswordsToFile(string filePath)
        {
            // 获取所有永久密码（按综合评分排序）
            var passwords = _settings.LoadPasswordsFromFile();
            
            var lines = new List<string>();
            foreach (var pwd in passwords)
            {
                // 格式：密码\t\t使用次数\t\t最后使用时间(Ticks)
                lines.Add($"{pwd.Password}\t\t{pwd.UsageCount}\t\t{pwd.LastUsedTime.Ticks}");
            }

            File.WriteAllLines(filePath, lines);
        }

        /// <summary>
        /// 保存配置到文件（带备份）
        /// </summary>
        private void SaveSettingsWithBackup()
        {
            try
            {
                // 检查配置文件是否存在
                string configPath = AppSettings.ConfigFilePath;
                
                if (File.Exists(configPath))
                {
                    // 创建备份文件路径（只保留一个备份）
                    string backupPath = configPath + ".backup";
                    
                    // 删除旧备份（如果存在）
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                    
                    // 复制当前配置到备份
                    File.Copy(configPath, backupPath);
                    Console.WriteLine($"配置文件已备份: {backupPath}");
                }
                
                // 保存新配置
                _settings.Save();
                Console.WriteLine("配置文件已保存");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存配置文件失败: {ex.Message}");
                MessageBox.Show($"保存配置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public class PasswordItem
    {
        public int Index { get; set; }
        public string Password { get; set; } = string.Empty;
        public int UsageCount { get; set; }
        public string Type { get; set; } = string.Empty;
        public DateTime LastUsedTime { get; set; } = DateTime.MinValue;
    }
}
