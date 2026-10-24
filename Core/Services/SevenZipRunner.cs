using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
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

        /// <summary>
        /// 构造统一的 7z 进程启动信息。hidden=true（默认）时重定向标准输出/错误并按 UTF-8
        /// 解码，供捕获输出使用；hidden=false 时不重定向、允许窗口显示，用于「显示命令行窗口」
        /// 的解压模式（此时不能设 Encoding，否则与不重定向冲突）。
        /// </summary>
        public ProcessStartInfo BuildStartInfo(string args, bool hidden = true)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _settings.SevenZipPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = hidden
            };

            if (hidden)
            {
                startInfo.RedirectStandardInput = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.StandardOutputEncoding = Encoding.UTF8;
                startInfo.StandardErrorEncoding = Encoding.UTF8;
            }

            return startInfo;
        }

        /// <summary>
        /// 捕获式执行：启动进程、异步读标准输出/错误、等待至超时则 Kill。
        /// 返回 (ExitCode, Output, Error)。7z 真实退出码非负；-1 表示不可用/超时/取消/异常。
        /// </summary>
        public (int ExitCode, string Output, string Error) RunCapture(string args, int timeoutMs = DefaultTimeoutMs)
            => RunCapture(args, timeoutMs, CancellationToken.None);

        /// <summary>
        /// 捕获式执行（可取消）。两个关键约束：
        /// 1. 必须异步读两个流。先 ReadToEnd() 再 WaitForExit(timeout) 的写法会让超时形同虚设
        ///    （ReadToEnd 自己就无限期阻塞），且 stderr 填满管道缓冲区时双向互锁。
        /// 2. 必须接管并关闭标准输入。7z 遇到表头加密的压缩包会打印 “Enter password :”
        ///    并等控制台输入；GUI 宿主没有控制台时它无限重试（进程 CPU 0%、永不退出）。
        ///    给一个已关闭的 stdin，提示读到 EOF 后 7z 直接以错误退出。
        /// </summary>
        public (int ExitCode, string Output, string Error) RunCapture(string args, int timeoutMs, CancellationToken cancellationToken)
        {
            if (!IsAvailable)
                return (-1, string.Empty, string.Empty);

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            try
            {
                using var process = new Process { StartInfo = BuildStartInfo(args) };
                process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (outputBuilder) outputBuilder.Append(e.Data).Append('\n'); } };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (errorBuilder) errorBuilder.Append(e.Data).Append('\n'); } };

                process.Start();

                try { process.StandardInput.Close(); } catch { }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!WaitForExitOrCancel(process, timeoutMs, cancellationToken))
                {
                    try { process.Kill(); } catch { }
                    try { process.WaitForExit(1000); } catch { }
                    return (-1, Snapshot(outputBuilder), Snapshot(errorBuilder));
                }

                // 无参 WaitForExit 会等异步读取器把缓冲区里的最后一行送完，否则输出会被截断
                process.WaitForExit();
                return (process.ExitCode, Snapshot(outputBuilder), Snapshot(errorBuilder));
            }
            catch
            {
                return (-1, Snapshot(outputBuilder), Snapshot(errorBuilder));
            }
        }

        /// <summary>分片等待退出：同时受超时与取消令牌约束，两者任一命中都返回 false。</summary>
        private bool WaitForExitOrCancel(Process process, int timeoutMs, CancellationToken cancellationToken)
        {
            int waited = 0;
            const int sliceMs = 100;

            while (!process.WaitForExit(sliceMs))
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                waited += sliceMs;
                if (waited >= timeoutMs)
                    return false;
            }

            return !cancellationToken.IsCancellationRequested;
        }

        private static string Snapshot(StringBuilder builder)
        {
            lock (builder) return builder.ToString();
        }
    }
}
