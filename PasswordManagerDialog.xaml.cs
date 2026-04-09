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
        private AppSettings _settings;
        private bool _isDirty = false;

        public PasswordManagerDialog(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            DgPasswords.ItemsSource = _passwordList;

            // 加载密码
            LoadPasswords();

            // 显示当前密码本文件路径
            TxtPasswordFilePath.Text = string.IsNullOrEmpty(_settings.PasswordFilePath) 
                ? "未设置（使用默认密码）" 
                : _settings.PasswordFilePath;
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
        }

        private void ReindexPasswords()
        {
            for (int i = 0; i < _passwordList.Count; i++)
            {
                _passwordList[i].Index = i + 1;
            }
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
            _isDirty = true;

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
                    _settings.RemovePermanentPassword(selected.Password);
                }

                _passwordList.Remove(selected);
                ReindexPasswords();
                _isDirty = true;

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
                    _isDirty = true;
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
                    _isDirty = true;
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
                TxtPasswordFilePath.Text = dialog.FileName;
            }
        }

        private void BtnLoadFile_Click(object sender, RoutedEventArgs e)
        {
            string filePath = TxtPasswordFilePath.Text;
            if (string.IsNullOrEmpty(filePath) || filePath == "未设置（使用默认密码）")
            {
                MessageBox.Show("请先选择密码本文件！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!File.Exists(filePath))
            {
                MessageBox.Show("文件不存在！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                _settings.PasswordFilePath = filePath;
                LoadPasswords();
                _isDirty = true;
                MessageBox.Show("密码本加载成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 从密码本文件导入到配置文件（JSON）
        /// </summary>
        private void BtnImportPassword_Click(object sender, RoutedEventArgs e)
        {
            string filePath = TxtPasswordFilePath.Text;
            if (string.IsNullOrEmpty(filePath) || filePath == "未设置（使用默认密码）")
            {
                MessageBox.Show("请先选择密码本文件！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!File.Exists(filePath))
            {
                MessageBox.Show("文件不存在！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                // 导入密码，获取详细结果信息
                string resultMessage = _settings.ImportPasswordsFromOldFormat(filePath);
                
                // 更新密码本文件路径
                _settings.PasswordFilePath = filePath;
                
                // 显示导入结果
                MessageBox.Show(resultMessage, "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
                
                // 重新加载密码列表以更新UI
                LoadPasswords();
                _isDirty = true;
                
                Console.WriteLine("密码导入完成，配置文件已更新");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"导入失败: {ex.Message}");
                Console.WriteLine($"异常堆栈: {ex.StackTrace}");
                MessageBox.Show($"导入失败：{ex.Message}\n\n请查看控制台输出获取详细信息。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 保存配置文件（包括密码本文件路径设置）
            _settings.Save();

            // 如果有密码更改，保存密码到配置文件
            if (_isDirty)
            {
                _settings.SavePasswordsToFile();
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
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
