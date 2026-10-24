using System.Windows;
using AutoUnpackTool.ViewModels;

namespace AutoUnpackTool
{
    /// <summary>
    /// 设置对话框：只负责加载 XAML 与关闭窗口，字段加载/校验/保存映射都在 SettingsViewModel。
    /// </summary>
    public partial class SettingsDialog : Wpf.Ui.Controls.FluentWindow
    {
        public SettingsDialog(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            viewModel.RequestClose += ok =>
            {
                DialogResult = ok;
                Close();
            };
        }
    }
}
