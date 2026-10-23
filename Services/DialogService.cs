using System.Windows;
using AutoUnpackTool.Core.Configuration;
using Microsoft.Win32;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// IDialogService 的 WPF 实现。ViewModel 只依赖接口，弹窗由本类负责创建。
    /// Owner 统一取当前主窗口，保证模态窗口居中且不遮挡主窗口。
    /// </summary>
    public sealed class DialogService : IDialogService
    {
        private static Window? Owner => Application.Current?.MainWindow;

        public string? SelectFolder(string title)
        {
            var dialog = new OpenFolderDialog
            {
                Title = title
            };

            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }

        public AppSettings? ShowSettings(AppSettings current)
        {
            var dialog = new SettingsDialog(current) { Owner = Owner };
            return dialog.ShowDialog() == true ? dialog.Settings : null;
        }

        public bool ShowPasswords(AppSettings current)
        {
            var dialog = new PasswordManagerDialog(current) { Owner = Owner };
            return dialog.ShowDialog() == true;
        }

        public bool ShowBlacklist(AppSettings current)
        {
            var dialog = new BlacklistManagerDialog(current) { Owner = Owner };
            return dialog.ShowDialog() == true;
        }

        public void ShowWarning(string message, string title = "提示")
        {
            var owner = Owner;
            if (owner != null)
            {
                MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
