using System.Windows;

namespace AutoUnpackTool
{
    /// <summary>
    /// 使用说明对话框：纯静态展示，无业务逻辑，仅负责加载 XAML 与关闭。
    /// </summary>
    public partial class HelpDialog : Wpf.Ui.Controls.FluentWindow
    {
        public HelpDialog()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
