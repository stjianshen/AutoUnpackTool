using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoUnpackTool.Core.Abstractions;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>删除重试退避策略。</summary>
    public enum BackoffKind { Fixed, Linear, Random }

    /// <summary>带重试删除的参数：次数、退避方式、每轮删除前的解锁等待时长、日志前缀。</summary>
    public sealed class DeleteRetryPolicy
    {
        public int MaxRetries = 3;
        public BackoffKind Backoff = BackoffKind.Fixed;
        public int BaseDelayMs = 200;
        public int UnlockTimeoutMs = 0; // 0 = 不做显式解锁等待，直接依赖 File.Delete 的 IOException 重试
        public string LogPrefix = string.Empty;

        public static DeleteRetryPolicy Default => new() { MaxRetries = 3, Backoff = BackoffKind.Fixed, BaseDelayMs = 200 };
    }

    /// <summary>
    /// 文件操作基础服务：统一"等待文件解锁 + 带退避重试删除"逻辑，消除
    /// FileCleanupService / BlacklistService / ExtractionPipeline / SmartPathService 里的多套删除重试实现。
    /// </summary>
    public sealed class FileOperationService
    {
        private readonly IAppLogger _logger;

        public FileOperationService(IAppLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>等待文件解锁（独占可读即视为已解锁）；true=已解锁/文件不存在，false=超时仍被占用。</summary>
        public async Task<bool> WaitForUnlockAsync(string filePath, int timeoutMs)
        {
            int waited = 0;
            const int interval = 100; // 每 100ms 检查一次

            while (waited < timeoutMs)
            {
                try
                {
                    // 独占读即可检测占用；用 Read 而非 ReadWrite，避免只读属性/ACL 误判未解锁而跳过清理
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                    await Task.Delay(interval);
                    waited += interval;
                }
                catch
                {
                    // 其他错误（如文件不存在），认为已解锁
                    return true;
                }
            }

            return false; // 超时
        }

        /// <summary>
        /// 带重试的异步删除：每轮可选先等待解锁，再删除，失败按策略退避。
        /// 文件不存在视为已达成目标返回 true；日志前缀由 policy.LogPrefix 提供。
        /// </summary>
        public async Task<bool> DeleteWithRetryAsync(string filePath, DeleteRetryPolicy? policy = null)
        {
            policy ??= DeleteRetryPolicy.Default;
            string name = Path.GetFileName(filePath);

            if (!File.Exists(filePath))
                return true;

            for (int i = 0; i < policy.MaxRetries; i++)
            {
                if (!File.Exists(filePath))
                    return true;

                if (policy.UnlockTimeoutMs > 0 && !await WaitForUnlockAsync(filePath, policy.UnlockTimeoutMs))
                {
                    int delay = ComputeDelay(policy, i);
                    if (i < policy.MaxRetries - 1)
                    {
                        _logger.Log($"{policy.LogPrefix}文件仍在占用中，等待 {delay}ms 后重试 ({i + 1}/{policy.MaxRetries}): {name}", ConsoleColor.Yellow);
                        await Task.Delay(delay);
                        continue;
                    }
                    _logger.Log($"{policy.LogPrefix}文件仍在占用中，跳过: {name}", ConsoleColor.Yellow);
                    return false;
                }

                try
                {
                    await Task.Run(() => File.Delete(filePath));
                    return true;
                }
                catch (IOException) when (i < policy.MaxRetries - 1)
                {
                    int delay = ComputeDelay(policy, i);
                    _logger.Log($"{policy.LogPrefix}删除被占用，等待 {delay}ms 后重试 ({i + 1}/{policy.MaxRetries}): {name}", ConsoleColor.Yellow);
                    await Task.Delay(delay);
                }
                catch (Exception ex)
                {
                    _logger.Log($"{policy.LogPrefix}删除失败: {name} - {ex.Message}", ConsoleColor.Red);
                    return false;
                }
            }

            _logger.Log($"{policy.LogPrefix}删除失败（多次重试后仍被占用）: {name}", ConsoleColor.Red);
            return false;
        }

        /// <summary>同步轻量删除：替换裸 try { File.Delete } catch {}，占用时短暂重试；不存在返回 false。</summary>
        public bool TryDelete(string filePath, int retries = 3, int backoffMs = 200)
        {
            if (!File.Exists(filePath))
                return false;

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    File.Delete(filePath);
                    return true;
                }
                catch (IOException) when (i < retries - 1)
                {
                    Thread.Sleep(backoffMs);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>安全枚举文件：出错（无权限/路径失效等）返回空集合并记一条日志，不抛异常。</summary>
        public IReadOnlyList<string> EnumerateFilesSafe(string dir, string pattern = "*.*", bool allDirectories = false)
        {
            try
            {
                var opt = allDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                return Directory.GetFiles(dir, pattern, opt);
            }
            catch (Exception ex)
            {
                _logger.Log($"枚举文件失败: {dir} - {ex.Message}", ConsoleColor.Yellow);
                return Array.Empty<string>();
            }
        }

        /// <summary>安全枚举子目录：出错返回空集合并记一条日志。</summary>
        public IReadOnlyList<string> EnumerateDirectoriesSafe(string dir, bool allDirectories = false)
        {
            try
            {
                var opt = allDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                return Directory.GetDirectories(dir, "*", opt);
            }
            catch (Exception ex)
            {
                _logger.Log($"枚举目录失败: {dir} - {ex.Message}", ConsoleColor.Yellow);
                return Array.Empty<string>();
            }
        }

        /// <summary>仅当目录存在且为空时删除；出错记日志并返回 false。</summary>
        public bool TryDeleteEmptyDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"删除空目录失败: {dir} - {ex.Message}", ConsoleColor.Yellow);
            }
            return false;
        }

        private static int ComputeDelay(DeleteRetryPolicy p, int attempt) => p.Backoff switch
        {
            BackoffKind.Linear => p.BaseDelayMs * (attempt + 1),
            BackoffKind.Random => Random.Shared.Next(p.BaseDelayMs, p.BaseDelayMs * 3 + 1),
            _ => p.BaseDelayMs
        };
    }
}
