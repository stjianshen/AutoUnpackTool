using System.Linq;
using System.Windows;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.ViewModels;
using Microsoft.Win32;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// IDialogService 的 WPF 实现。ViewModel 只依赖接口，弹窗由本类负责创建。
    /// Owner 统一取当前主窗口，保证模态窗口居中且不遮挡主窗口。
    /// </summary>
    public sealed class DialogService : IDialogService
    {
        private readonly IClipboardService _clipboard;
        private readonly IThemeService _theme;

        public DialogService(IClipboardService clipboard, IThemeService theme)
        {
            _clipboard = clipboard;
            _theme = theme;
        }

        private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
            ?? Application.Current?.MainWindow;

        /// <summary>
        /// 统一装配对话框：指定 Owner，并在句柄就绪后挂上系统深浅色监听（关闭时解除）。
        /// 三个 Fluent 对话框共用这一套，避免在每个窗口的构造函数里重复。
        /// </summary>
        private T Prepare<T>(T dialog) where T : Window
        {
            dialog.Owner = Owner;
            dialog.SourceInitialized += (_, _) => _theme.AttachWindow(dialog);
            dialog.Closed += (_, _) => _theme.DetachWindow(dialog);
            return dialog;
        }

        public string? SelectFolder(string title)
        {
            var dialog = new OpenFolderDialog
            {
                Title = title
            };

            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }

        public string? SelectFile(string title, string filter, string? fileName = null)
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                FileName = fileName
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public string? SelectSaveFile(string title, string filter, string? fileName = null)
        {
            var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = filter,
                DefaultExt = "txt",
                FileName = fileName
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public AppSettings? ShowSettings(AppSettings current)
        {
            var vm = new SettingsViewModel(current, this);
            var dialog = Prepare(new SettingsDialog(vm));
            if (dialog.ShowDialog() != true)
                return null;

            // 主题在设置关闭时立即生效（无需重启），同时已随 Settings.Save() 落盘
            _theme.Apply(vm.Settings.Theme);
            return vm.Settings;
        }

        public bool ShowPasswords(AppSettings current)
        {
            var vm = new PasswordManagerViewModel(current, this, _clipboard);
            return Prepare(new PasswordManagerDialog(vm)).ShowDialog() == true;
        }

        public bool ShowBlacklist(AppSettings current)
        {
            var vm = new BlacklistViewModel(current, this);
            return Prepare(new BlacklistManagerDialog(vm)).ShowDialog() == true;
        }

        public void ShowHelp()
        {
            Prepare(new HelpDialog()).ShowDialog();
        }

        public void ShowWarning(string message, string title = "提示")
            => Show(message, title, MessageBoxImage.Warning);

        public void ShowInfo(string message, string title = "提示")
            => Show(message, title, MessageBoxImage.Information);

        public void ShowError(string message, string title = "错误")
            => Show(message, title, MessageBoxImage.Error);

        private static void Show(string message, string title, MessageBoxImage icon)
        {
            var owner = Owner;
            if (owner != null)
            {
                MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
            }
            else
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, icon);
            }
        }

        public bool ShowConfirm(string message, string title = "确认")
        {
            var owner = Owner;
            var result = owner != null
                ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }
    }
}
