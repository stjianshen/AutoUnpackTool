using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Configuration;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>
    /// 黑名单规则匹配与清理服务。
    /// 规则支持通配符（*、?）与 Perl/.NET 正则，编译结果带指纹缓存。
    /// </summary>
    public sealed class BlacklistService
    {
        // 黑名单规则编译缓存（规则未变化时复用，避免逐文件重复解析正则）
        private sealed class CompiledBlacklistRule
        {
            public string SourcePattern = string.Empty;
            public Regex? Matcher;
            public bool MatchesFullPath;
        }

        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly FileOperationService _fileOps;

        private List<CompiledBlacklistRule> _compiledBlacklistRules = new();
        private string _compiledBlacklistSignature = string.Empty;
        private readonly object _blacklistCompileLock = new();
        private readonly HashSet<string> _reportedInvalidBlacklistPatterns = new(StringComparer.OrdinalIgnoreCase);

        public BlacklistService(AppSettings settings, IAppLogger logger, FileOperationService fileOps)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fileOps = fileOps ?? throw new ArgumentNullException(nameof(fileOps));
        }

        /// <summary>
        /// 检查文件是否匹配黑名单规则（支持通配符与正则，规则顺序优先）
        /// </summary>
        /// <param name="filePath">文件或文件夹完整路径</param>
        /// <param name="matchedPattern">匹配到的模式（输出）</param>
        /// <returns>是否为黑名单文件</returns>
        public bool IsBlacklistedFile(string filePath, out string matchedPattern)
        {
            matchedPattern = string.Empty;

            // 跳过黑名单处理：直接返回不匹配
            if (_settings.SkipBlacklist)
                return false;

            var rules = GetCompiledBlacklistRules();
            if (rules.Count == 0)
                return false;

            string fileName = Path.GetFileName(filePath);
            string fullPath = Path.GetFullPath(filePath);

            foreach (var rule in rules)
            {
                string target = rule.MatchesFullPath ? fullPath : fileName;
                if (rule.Matcher != null && rule.Matcher.IsMatch(target))
                {
                    matchedPattern = rule.SourcePattern;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 获取黑名单规则的编译缓存。规则未变化时直接复用；
        /// 通配符预转正则、正则预编译，避免逐文件重复解析
        /// </summary>
        private List<CompiledBlacklistRule> GetCompiledBlacklistRules()
        {
            var rules = _settings.GetBlacklistRules();

            // 规则指纹：规则增删改或启用状态变化时重建缓存
            string signature = string.Join("\u0001", rules.Select(r => $"{(r.IsRegex ? "R" : "W")}\u0002{r.Pattern.Trim()}"));

            List<CompiledBlacklistRule> compiled;
            List<string> invalidWarnings = new();
            lock (_blacklistCompileLock)
            {
                if (string.Equals(signature, _compiledBlacklistSignature, StringComparison.Ordinal))
                    return _compiledBlacklistRules;

                compiled = new List<CompiledBlacklistRule>(rules.Count);
                foreach (var rule in rules)
                {
                    string pattern = rule.Pattern.Trim();
                    bool matchesFullPath = pattern.Contains('\\') || pattern.Contains('/');

                    try
                    {
                        Regex matcher;
                        if (rule.IsRegex)
                        {
                            // 正则规则：Perl/.NET 兼容语法，默认忽略大小写
                            matcher = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        }
                        else
                        {
                            // 通配符规则：* 任意字符、? 单字符，全名匹配
                            string regexPattern = "^" + Regex.Escape(pattern)
                                .Replace("\\*", ".*")
                                .Replace("\\?", ".") + "$";
                            matcher = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        }

                        compiled.Add(new CompiledBlacklistRule
                        {
                            SourcePattern = pattern,
                            Matcher = matcher,
                            MatchesFullPath = matchesFullPath
                        });
                    }
                    catch (ArgumentException ex)
                    {
                        // 同一无效规则只提示一次，避免扫描时刷屏
                        if (_reportedInvalidBlacklistPatterns.Add(pattern))
                        {
                            invalidWarnings.Add($"[黑名单] 无效的{(rule.IsRegex ? "正则" : "通配符")}规则已跳过: {pattern}（{ex.Message}）");
                        }
                    }
                }

                _compiledBlacklistRules = compiled;
                _compiledBlacklistSignature = signature;
            }

            // 日志可能跨线程调度，必须放在锁外，避免与 UI 线程互等
            foreach (string warning in invalidWarnings)
                _logger.Log(warning, ConsoleColor.Yellow);

            return compiled;
        }

        /// <summary>
        /// 永久删除黑名单文件（带占用重试）。收集阶段与解压产物阶段统一复用此入口。
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="matchedPattern">匹配到的模式</param>
        /// <param name="maxRetries">最大尝试次数（含首次）</param>
        /// <returns>是否删除成功</returns>
        public bool TryDeleteWithRetry(string filePath, string matchedPattern, int maxRetries = 3)
        {
            string fileName = Path.GetFileName(filePath);
            _logger.Log($"[黑名单] 检测到黑名单文件: {fileName} (匹配模式: {matchedPattern})", ConsoleColor.Yellow);

            // 统一走 FileOperationService 的同步带重试删除（占用时 200ms 退避）
            bool ok = _fileOps.TryDelete(filePath, maxRetries, 200);
            if (ok)
                _logger.Log($"[黑名单] 已永久删除: {fileName}", ConsoleColor.Green);
            else
                _logger.Log($"[黑名单] 删除失败（不存在/权限不足/被占用）: {fileName}", ConsoleColor.Red);
            return ok;
        }

        /// <summary>
        /// 永久删除黑名单文件（兼容旧调用点，内部委托带重试删除）
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="matchedPattern">匹配到的模式</param>
        public void DeleteBlacklistedFile(string filePath, string matchedPattern)
        {
            TryDeleteWithRetry(filePath, matchedPattern);
        }

        /// <summary>
        /// 在智能路径扁平化之前，对解压目录递归删除黑名单匹配的文件和文件夹（含解压残留的压缩包）。
        /// 否则同级黑名单文件或文件夹会导致「恰好一个子目录且无文件」条件不满足而无法扁平化。
        /// </summary>
        public void DeleteBlacklistedFilesUnderDirectory(string rootDir)
        {
            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
                return;

            // 黑名单已停用时无需扫描目录
            if (_settings.SkipBlacklist)
                return;

            var activeRules = _settings.GetBlacklistRules();
            if (activeRules == null || activeRules.Count == 0)
                return;

            try
            {
                _logger.Log($"[批量智能路径] 扁平化前黑名单清理: {rootDir}", ConsoleColor.Gray);

                // 先删除黑名单文件
                foreach (string file in _fileOps.EnumerateFilesSafe(rootDir, "*.*", allDirectories: true))
                {
                    if (!File.Exists(file))
                        continue;
                    if (IsBlacklistedFile(file, out string matchedPattern))
                        DeleteBlacklistedFile(file, matchedPattern);
                }

                // 再删除黑名单文件夹（从最深层次开始删除，避免父目录还有内容时无法删除）
                var allDirs = _fileOps.EnumerateDirectoriesSafe(rootDir, allDirectories: true)
                    .OrderByDescending(d => d.Length); // 从最深层次开始删除

                foreach (string dir in allDirs)
                {
                    if (!Directory.Exists(dir))
                        continue;
                    if (IsBlacklistedFile(dir, out string matchedPattern))
                    {
                        try
                        {
                            Directory.Delete(dir, true); // 递归删除目录及其所有内容
                            _logger.Log($"[批量智能路径] [黑名单] 已删除文件夹: {Path.GetFileName(dir)} (匹配模式: {matchedPattern})", ConsoleColor.Yellow);
                        }
                        catch (Exception ex)
                        {
                            _logger.Log($"[批量智能路径] [黑名单] 删除文件夹失败: {Path.GetFileName(dir)} - {ex.Message}", ConsoleColor.Red);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"[批量智能路径] 扁平化前黑名单扫描失败: {ex.Message}", ConsoleColor.Yellow);
            }
        }

        /// <summary>
        /// 构建匹配器（供黑名单管理界面的规则校验/预览复用）
        /// </summary>
        public static Regex BuildMatcher(string pattern, bool isRegex)
        {
            if (isRegex)
            {
                return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            string regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}

