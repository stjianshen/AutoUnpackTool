using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.ViewModels;

namespace AutoUnpackTool
{
    /// <summary>
    /// 黑名单管理对话框：只保留表格相关的视图操作（编辑提交、行定位、多选删除转发），
    /// 规则增删移、示例插入、正则校验与测试预览都在 BlacklistViewModel。
    /// </summary>
    public partial class BlacklistManagerDialog : Window
    {
        private readonly BlacklistViewModel _vm;

        public BlacklistManagerDialog(BlacklistViewModel viewModel)
        {
            InitializeComponent();
            _vm = viewModel;
            DataContext = viewModel;

            viewModel.CommitEditsRequested += CommitPendingEdits;
            viewModel.EditRowRequested += rule => Dispatcher.BeginInvoke(new Action(() =>
            {
                DgRules.SelectedItem = rule;
                DgRules.ScrollIntoView(rule);
                DgRules.CurrentCell = new DataGridCellInfo(rule, DgRules.Columns[2]);
                DgRules.BeginEdit();
            }), DispatcherPriority.Background);

            viewModel.RequestClose += ok =>
            {
                DialogResult = ok;
                Close();
            };
        }

        /// <summary>
        /// 提交表格中正在编辑的单元格
        /// </summary>
        private void CommitPendingEdits()
        {
            DgRules.CommitEdit(DataGridEditingUnit.Cell, true);
            DgRules.CommitEdit(DataGridEditingUnit.Row, true);
        }

        /// <summary>
        /// 删除选中（DataGrid 多选无法直接绑定，由视图把选中项交给 ViewModel）
        /// </summary>
        private void BtnDeleteRule_Click(object sender, RoutedEventArgs e)
        {
            var selected = DgRules.SelectedItems?.Cast<BlacklistRule>().ToList();
            _vm.DeleteRules(selected);
        }

        private void DgRules_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            // 单元格提交后刷新测试结果与统计
            Dispatcher.BeginInvoke(new Action(() => _vm.RefreshTestState()), DispatcherPriority.Background);
        }
    }
}
