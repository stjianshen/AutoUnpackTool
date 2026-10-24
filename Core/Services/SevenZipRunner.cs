using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Configuration;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>
    /// 统一 7z/NanaZip 外部进程调用：封装 SevenZipPath 可用性校验、ProcessStartInfo 样板
    /// （重定向标准输出/错误 + UTF-8 编码 + 隐藏窗口），以及"启动→读全输出→超时 Kill"的
    /// 捕获式执行，消除 ArchiveDetector / StegoService / SevenZipExtractor 里的重复样板。
    /// </summary>
    public sealed class SevenZipRunner
    {
        private const int DefaultTimeoutMs = 15000;

        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;

        public SevenZipRunner(AppSettings settings, IAppLogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>SevenZipPath 是否已配置且文件存在。</summary>
        public bool IsAvailable
            => !string.IsNullOrWhiteSpace(_settings.SevenZipPath) && File.Exists(_settings.SevenZipPath);

        /// <summary>构造统一的 7z 进程启动信息（重定向 + UTF-8 + 无窗口）。</summary>
        public ProcessStartInfo BuildStartInfo(string args, bool hidden = true)
        {
            return new ProcessStartInfo
            {
                FileName = _settings.SevenZipPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = hidden,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        /// <summary>
        /// 捕获式执行：启动进程、读全标准输出/错误、等待至超时则 Kill。
        /// 返回 (ExitCode, Output, Error)。7z 真实退出码非负；-1 表示不可用/超时/异常。
        /// </summary>
        public (int ExitCode, string Output, string Error) RunCapture(string args, int timeoutMs = DefaultTimeoutMs)
        {
            if (!IsAvailable)
                return (-1, string.Empty, string.Empty);

            try
            {
                using var process = new Process { StartInfo = BuildStartInfo(args) };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    return (-1, output, error);
                }

                return (process.ExitCode, output, error);
            }
            catch
            {
                return (-1, string.Empty, string.Empty);
            }
        }
    }
}
