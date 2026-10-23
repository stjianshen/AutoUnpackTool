using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AutoUnpackTool.ViewModels;

namespace AutoUnpackTool
{
    /// <summary>
    /// 主窗口：只保留视图层职责（XAML 加载、拖放/选中事件转投、日志自动滚动）。
    /// 全部业务逻辑位于 MainViewModel，窗口不再持有任何服务或流程状态。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        public MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            // 日志自动滚动由附加行为 LogListAutoScroll 负责（见 MainWindow.xaml）
        }

        private void Window_DragOver(object sender, DragEventArgs e) => _vm.HandleDragOver(e);

        private void Window_Drop(object sender, DragEventArgs e) => _vm.HandleDrop(e.Data);

        private void Window_Closing(object? sender, CancelEventArgs e) => _vm.Shutdown();

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => _vm.OnTreeSelectionChanged(e.NewValue);

        /// <summary>
        /// 最大解压层数：选中预定义项时立即保存（等价于原 SelectionChanged）
        /// </summary>
        private void MaxDepth_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 初始化期间（Text 绑定回填导致的选择变化）不写配置
            if (!IsLoaded)
                return;

            if (CmbMaxExtractDepth.SelectedItem is ComboBoxItem item && item.Content is string content)
            {
                _vm.CommitMaxExtractDepthText(content);
            }
        }

        /// <summary>
        /// 最大解压层数：自定义输入失去焦点时保存（等价于原 LostFocus）
        /// </summary>
        private void MaxDepth_LostFocus(object sender, RoutedEventArgs e)
        {
            // 如果选中了预定义项，由 SelectionChanged 处理
            if (CmbMaxExtractDepth.SelectedItem is ComboBoxItem)
                return;

            _vm.CommitMaxExtractDepthText(CmbMaxExtractDepth.Text);
        }
    }
}
