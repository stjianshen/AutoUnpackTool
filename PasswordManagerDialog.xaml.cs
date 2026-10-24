using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoUnpackTool.Core.Models;
using AutoUnpackTool.ViewModels;

namespace AutoUnpackTool
{
    /// <summary>
    /// 密码本管理对话框：只保留视图专属能力（DataGrid 多选、Ctrl+A、反选、关闭）。
    /// 全部业务逻辑在 PasswordManagerViewModel。
    /// </summary>
    public partial class PasswordManagerDialog : Wpf.Ui.Controls.FluentWindow
    {
        private readonly PasswordManagerViewModel _vm;

        public PasswordManagerDialog(PasswordManagerViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            // DataGrid.SelectedItems 不可绑定，由视图回传给 ViewModel
            vm.SelectedItemsProvider = GetSelectedItems;
            vm.RequestClose += (s, e) =>
            {
                DialogResult = true;
                Close();
            };
        }

        private IReadOnlyList<PasswordItem> GetSelectedItems()
            => DgPasswords.SelectedItems.OfType<PasswordItem>().ToList();

        private void DgPasswords_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => _vm.UpdateSelectedCount(DgPasswords.SelectedItems.Count);

        /// <summary>
        /// DataGrid键盘事件 - 支持Ctrl+A全选
        /// </summary>
        private void DgPasswords_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                BtnSelectAll_Click(null, null);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 全选
        /// </summary>
        private void BtnSelectAll_Click(object? sender, RoutedEventArgs? e)
        {
            DgPasswords.SelectAll();
            _vm.UpdateSelectedCount(DgPasswords.SelectedItems.Count);
        }

        /// <summary>
        /// 反选
        /// </summary>
        private void BtnInvertSelection_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedItems();

            DgPasswords.UnselectAll();

            foreach (var item in DgPasswords.Items.OfType<PasswordItem>())
            {
                if (!selected.Contains(item))
                {
                    DgPasswords.SelectedItems.Add(item);
                }
            }

            _vm.UpdateSelectedCount(DgPasswords.SelectedItems.Count);
        }
    }
}
