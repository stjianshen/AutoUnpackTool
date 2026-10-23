using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutoUnpackTool.Core.Configuration;

namespace AutoUnpackTool
{
    /// <summary>
    /// 黑名单管理对话框：表格化的规则列表，一行一条规则
    /// 支持通配符（*、?）与 Perl/.NET 正则表达式
    /// </summary>
    public partial class BlacklistManagerDialog : Window
    {
        private readonly AppSettings _settings;
        private readonly ObservableCollection<BlacklistRule> _rules;

        /// <summary>
        /// 推荐示例规则（根据常见推广文件整理，可用正则合并大量同类条目）
        /// </summary>
        private static readonly (string Pattern, bool IsRegex, string Note)[] SampleRules =
        {
            (@"^renys[._]top(_a?\d+)?\.jpg$", true, "renys.top 系列推广图"),
            (@"^(靴下福利组|ACG海量资源免费下载|加入迅雷群一键保存|更多资源欢迎加入萌域|免下载在线看小说)\.(jpg|jpeg|png|gif|txt)$", true, "常见推广文件名"),
            (@"^(梯子 需要自取|5元包月加速器推荐 全平台通用)$", true, "推广文件夹/文件"),
            (@"^【解压码在这】.*\.text$", true, "解压码推广文本"),
            (@"^防盗链.*\.txt$", true, "防盗链说明文本"),
            (@"^\[资源更新群二维码\.jpg$", true, "群二维码推广图"),
            ("*只是个表情包*.jpg", false, "表情包图片（通配符）"),
            (".DS_Store", false, "macOS 元数据文件"),
            ("descript.ion", false, "ACDSee 描述文件"),
            ("Tsuki+ni+Murakumo.txt", false, "指定文本文件")
        };

        public BlacklistManagerDialog(AppSettings settings)
        {
            InitializeComponent();

            _settings = settings ?? new AppSettings();

            // 编辑副本，点取消时不改动原配置
            _rules = new ObservableCollection<BlacklistRule>(
                (_settings.BlacklistRules ?? new List<BlacklistRule>()).Select(r => new BlacklistRule
                {
                    Enabled = r.Enabled,
                    IsRegex = r.IsRegex,
                    Pattern = r.Pattern,
                    Note = r.Note
                }));

            _rules.CollectionChanged += Rules_CollectionChanged;
            foreach (var rule in _rules)
                AttachRule(rule);

            DgRules.ItemsSource = _rules;
            UpdateRuleSummary();
        }

        private void AttachRule(BlacklistRule rule)
        {
            rule.PropertyChanged += Rule_PropertyChanged;
        }

        private void Rule_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // 启用状态/正则开关/模式变更时实时刷新统计与测试结果
            UpdateRuleSummary();
            UpdateTestResult();
        }

        /// <summary>
        /// 提交表格中正在编辑的单元格
        /// </summary>
        private void CommitPendingEdits()
        {
            DgRules.CommitEdit(DataGridEditingUnit.Cell, true);
            DgRules.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private void Rules_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateRuleSummary();
        }

        private void BtnAddRule_Click(object sender, RoutedEventArgs e)
        {
            CommitPendingEdits();

            var rule = new BlacklistRule { Enabled = true, IsRegex = false, Pattern = string.Empty, Note = string.Empty };
            AttachRule(rule);
            _rules.Add(rule);

            // 新增后自动进入“匹配模式”列的编辑状态
            Dispatcher.BeginInvoke(new Action(() =>
            {
                DgRules.SelectedItem = rule;
                DgRules.ScrollIntoView(rule);
                DgRules.CurrentCell = new DataGridCellInfo(rule, DgRules.Columns[2]);
                DgRules.BeginEdit();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void BtnDeleteRule_Click(object sender, RoutedEventArgs e)
        {
            CommitPendingEdits();

            var selected = DgRules.SelectedItems?.Cast<BlacklistRule>().ToList();
            if (selected == null || selected.Count == 0)
            {
                MessageBox.Show("请先在表格中选择要删除的规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var rule in selected)
                _rules.Remove(rule);

            UpdateTestResult();
        }

        private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedRule(-1);
        }

        private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedRule(1);
        }

        private void MoveSelectedRule(int offset)
        {
            CommitPendingEdits();

            if (DgRules.SelectedItem is not BlacklistRule rule)
                return;

            int index = _rules.IndexOf(rule);
            int newIndex = index + offset;
            if (index < 0 || newIndex < 0 || newIndex >= _rules.Count)
                return;

            _rules.Move(index, newIndex);
            DgRules.SelectedItem = rule;
            DgRules.ScrollIntoView(rule);
        }

        private void BtnInsertSamples_Click(object sender, RoutedEventArgs e)
        {
            CommitPendingEdits();

            int added = 0;
            foreach (var (pattern, isRegex, note) in SampleRules)
            {
                bool exists = _rules.Any(r =>
                    string.Equals(r.Pattern?.Trim(), pattern, StringComparison.OrdinalIgnoreCase) &&
                    r.IsRegex == isRegex);
                if (exists)
                    continue;

                var rule = new BlacklistRule { Enabled = true, IsRegex = isRegex, Pattern = pattern, Note = note };
                AttachRule(rule);
                _rules.Add(rule);
                added++;
            }

            UpdateRuleSummary();
            UpdateTestResult();

            if (added == 0)
                MessageBox.Show("示例规则已全部存在，无需重复插入。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClearRules_Click(object sender, RoutedEventArgs e)
        {
            if (_rules.Count == 0)
                return;

            if (MessageBox.Show($"确定要清空全部 {_rules.Count} 条黑名单规则吗？", "确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _rules.Clear();
            UpdateTestResult();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 结束正在编辑的单元格，确保最新输入写回数据源
            CommitPendingEdits();

            // 校验正则规则语法
            var invalidPatterns = new List<string>();
            foreach (var rule in _rules.Where(r => r.Enabled && r.IsRegex && !string.IsNullOrWhiteSpace(r.Pattern)))
            {
                try
                {
                    _ = new Regex(rule.Pattern.Trim(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                catch (ArgumentException ex)
                {
                    invalidPatterns.Add($"{rule.Pattern}（{ex.Message}）");
                }
            }

            if (invalidPatterns.Count > 0)
            {
                MessageBox.Show("以下正则规则无法解析，请修正后再保存：\n\n" + string.Join("\n", invalidPatterns.Take(10)),
                    "正则语法错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 过滤空白模式后写回配置
            _settings.BlacklistRules = _rules
                .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
                .Select(r => new BlacklistRule
                {
                    Enabled = r.Enabled,
                    IsRegex = r.IsRegex,
                    Pattern = r.Pattern.Trim(),
                    Note = r.Note?.Trim() ?? string.Empty
                })
                .ToList();

            try
            {
                _settings.Save();
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存黑名单失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void DgRules_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            // 单元格提交后刷新测试结果与统计
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateRuleSummary();
                UpdateTestResult();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void TxtTestFileName_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateTestResult();
        }

        /// <summary>
        /// 用当前规则实时测试输入的文件名/路径
        /// </summary>
        private void UpdateTestResult()
        {
            if (TxtTestResult == null)
                return;

            string input = TxtTestFileName?.Text?.Trim() ?? string.Empty;
            if (input.Length == 0)
            {
                TxtTestResult.Text = "输入文件名试一下";
                TxtTestResult.Foreground = Brushes.Gray;
                return;
            }

            string fileName;
            try
            {
                fileName = Path.GetFileName(input);
            }
            catch
            {
                fileName = input;
            }

            foreach (var rule in _rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern)))
            {
                string pattern = rule.Pattern.Trim();
                bool matchesFullPath = pattern.Contains('\\') || pattern.Contains('/');
                string target = matchesFullPath ? input : fileName;

                try
                {
                    Regex matcher = BuildMatcher(pattern, rule.IsRegex);
                    if (matcher.IsMatch(target))
                    {
                        string typeName = rule.IsRegex ? "正则" : "通配符";
                        string note = string.IsNullOrWhiteSpace(rule.Note) ? string.Empty : $"（{rule.Note}）";
                        TxtTestResult.Text = $"命中：{typeName} {pattern}{note}";
                        TxtTestResult.Foreground = Brushes.SeaGreen;
                        return;
                    }
                }
                catch (ArgumentException ex)
                {
                    TxtTestResult.Text = $"规则无法解析：{pattern}（{ex.Message}）";
                    TxtTestResult.Foreground = Brushes.Red;
                    return;
                }
            }

            TxtTestResult.Text = "未命中任何规则";
            TxtTestResult.Foreground = Brushes.DarkOrange;
        }

        private static Regex BuildMatcher(string pattern, bool isRegex)
        {
            if (isRegex)
                return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            string regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private void UpdateRuleSummary()
        {
            if (TxtRuleSummary == null)
                return;

            int enabled = _rules.Count(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern));
            TxtRuleSummary.Text = $"共 {_rules.Count} 条规则（启用 {enabled} 条）";
        }
    }
}
