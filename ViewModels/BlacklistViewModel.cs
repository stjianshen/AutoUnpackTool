using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoUnpackTool.ViewModels
{
    /// <summary>
    /// 黑名单管理视图模型：规则的增删移/示例插入/正则校验/实时测试预览都在此。
    /// 表格自身的编辑提交与行定位通过 CommitEditsRequested / EditRowRequested 交回视图。
    /// </summary>
    public partial class BlacklistViewModel : ObservableObject
    {
        private readonly IDialogService _dialogs;
        private readonly AppSettings _settings;

        /// <summary>推荐示例规则（根据常见推广文件整理，可用正则合并大量同类条目）</summary>
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

        /// <summary>规则集合（编辑副本，取消时不落盘）</summary>
        public ObservableCollection<BlacklistRule> Rules { get; }

        /// <summary>请求视图提交表格中正在编辑的单元格</summary>
        public event Action? CommitEditsRequested;

        /// <summary>请求视图选中并进入指定规则行的编辑状态</summary>
        public event Action<BlacklistRule>? EditRowRequested;

        /// <summary>关闭请求：true=保存，false=取消</summary>
        public event Action<bool>? RequestClose;

        [ObservableProperty]
        private BlacklistRule? _selectedRule;

        [ObservableProperty]
        private string _testFileName = string.Empty;

        [ObservableProperty]
        private string _testResult = "输入文件名试一下";

        /// <summary>测试结果颜色语义（Gray/SeaGreen/Red/DarkOrange），由 XAML DataTrigger 转画刷</summary>
        [ObservableProperty]
        private string _testResultColor = "Gray";

        [ObservableProperty]
        private string _ruleSummary = "共 0 条规则";

        public BlacklistViewModel(AppSettings settings, IDialogService dialogs)
        {
            _dialogs = dialogs;
            _settings = settings ?? new AppSettings();

            // 编辑副本，点取消时不改动原配置
            Rules = new ObservableCollection<BlacklistRule>(
                (_settings.BlacklistRules ?? new List<BlacklistRule>()).Select(r => new BlacklistRule
                {
                    Enabled = r.Enabled,
                    IsRegex = r.IsRegex,
                    Pattern = r.Pattern,
                    Note = r.Note
                }));

            Rules.CollectionChanged += Rules_CollectionChanged;
            foreach (var rule in Rules)
                AttachRule(rule);

            UpdateRuleSummary();
            UpdateTestResult();
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

        private void Rules_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateRuleSummary();
        }

        partial void OnTestFileNameChanged(string value)
        {
            UpdateTestResult();
        }

        /// <summary>单元格编辑结束/规则变化后刷新统计与测试结果（供视图回调）</summary>
        public void RefreshTestState()
        {
            UpdateRuleSummary();
            UpdateTestResult();
        }

        /// <summary>新增一条空规则</summary>
        [RelayCommand]
        private void AddRule()
        {
            CommitEditsRequested?.Invoke();

            var rule = new BlacklistRule { Enabled = true, IsRegex = false, Pattern = string.Empty, Note = string.Empty };
            AttachRule(rule);
            Rules.Add(rule);

            // 新增后自动进入“匹配模式”列的编辑状态
            EditRowRequested?.Invoke(rule);
        }

        /// <summary>删除选中的规则（多选；选中项由视图传入）</summary>
        public void DeleteRules(List<BlacklistRule>? selected)
        {
            CommitEditsRequested?.Invoke();

            if (selected == null || selected.Count == 0)
            {
                _dialogs.ShowWarning("请先在表格中选择要删除的规则。");
                return;
            }

            foreach (var rule in selected)
                Rules.Remove(rule);

            UpdateTestResult();
        }

        /// <summary>上移选中规则</summary>
        [RelayCommand]
        private void MoveUp() => MoveSelectedRule(-1);

        /// <summary>下移选中规则</summary>
        [RelayCommand]
        private void MoveDown() => MoveSelectedRule(1);

        private void MoveSelectedRule(int offset)
        {
            CommitEditsRequested?.Invoke();

            if (SelectedRule == null)
                return;

            int index = Rules.IndexOf(SelectedRule);
            int newIndex = index + offset;
            if (index < 0 || newIndex < 0 || newIndex >= Rules.Count)
                return;

            Rules.Move(index, newIndex);
            EditRowRequested?.Invoke(SelectedRule);
        }

        /// <summary>插入示例规则（去重）</summary>
        [RelayCommand]
        private void InsertSamples()
        {
            CommitEditsRequested?.Invoke();

            int added = 0;
            foreach (var (pattern, isRegex, note) in SampleRules)
            {
                bool exists = Rules.Any(r =>
                    string.Equals(r.Pattern?.Trim(), pattern, StringComparison.OrdinalIgnoreCase) &&
                    r.IsRegex == isRegex);
                if (exists)
                    continue;

                var rule = new BlacklistRule { Enabled = true, IsRegex = isRegex, Pattern = pattern, Note = note };
                AttachRule(rule);
                Rules.Add(rule);
                added++;
            }

            RefreshTestState();

            if (added == 0)
                _dialogs.ShowWarning("示例规则已全部存在，无需重复插入。", "提示");
        }

        /// <summary>清空全部规则</summary>
        [RelayCommand]
        private void ClearRules()
        {
            if (Rules.Count == 0)
                return;

            var result = _dialogs.ShowConfirm($"确定要清空全部 {Rules.Count} 条黑名单规则吗？", "确认");
            if (!result)
                return;

            Rules.Clear();
            UpdateTestResult();
        }

        /// <summary>保存（正则语法校验 + 写回配置）</summary>
        [RelayCommand]
        private void Save()
        {
            // 结束正在编辑的单元格，确保最新输入写回数据源
            CommitEditsRequested?.Invoke();

            // 校验正则规则语法
            var invalidPatterns = new List<string>();
            foreach (var rule in Rules.Where(r => r.Enabled && r.IsRegex && !string.IsNullOrWhiteSpace(r.Pattern)))
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
                _dialogs.ShowWarning("以下正则规则无法解析，请修正后再保存：\n\n" + string.Join("\n", invalidPatterns.Take(10)), "正则语法错误");
                return;
            }

            // 过滤空白模式后写回配置
            _settings.BlacklistRules = Rules
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
                RequestClose?.Invoke(true);
            }
            catch (Exception ex)
            {
                _dialogs.ShowWarning($"保存黑名单失败：\n{ex.Message}", "错误");
            }
        }

        /// <summary>取消</summary>
        [RelayCommand]
        private void Cancel()
        {
            RequestClose?.Invoke(false);
        }

        /// <summary>
        /// 用当前规则实时测试输入的文件名/路径
        /// </summary>
        private void UpdateTestResult()
        {
            string input = TestFileName?.Trim() ?? string.Empty;
            if (input.Length == 0)
            {
                TestResult = "输入文件名试一下";
                TestResultColor = "Gray";
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

            foreach (var rule in Rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern)))
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
                        TestResult = $"命中：{typeName} {pattern}{note}";
                        TestResultColor = "SeaGreen";
                        return;
                    }
                }
                catch (ArgumentException ex)
                {
                    TestResult = $"规则无法解析：{pattern}（{ex.Message}）";
                    TestResultColor = "Red";
                    return;
                }
            }

            TestResult = "未命中任何规则";
            TestResultColor = "DarkOrange";
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
            int enabled = Rules.Count(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Pattern));
            RuleSummary = $"共 {Rules.Count} 条规则（启用 {enabled} 条）";
        }
    }
}
