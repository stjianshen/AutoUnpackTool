using System;
using System.IO;
using System.Windows.Threading;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// WPF 侧的日志实现：把 Core 层日志封送到界面线程后交给 sink 呈现。
    /// 保持与原 AppendLog 一致的同步阻塞语义（Dispatcher.Invoke）。
    /// </summary>
    public sealed class UiAppLogger : IAppLogger
    {
        private readonly Dispatcher _dispatcher;
        private readonly Action<LogEntry> _sink;

        public UiAppLogger(Dispatcher dispatcher, Action<LogEntry> sink)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public void Log(string message, ConsoleColor color, FileItem? node = null)
        {
            // 如果指定了目标节点,记录到该节点
            if (node != null)
            {
                node.AddLog(message, color);
            }

            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Message = message,
                Color = color
            };

            if (_dispatcher.CheckAccess())
            {
                _sink(entry);
            }
            else
            {
                _dispatcher.Invoke(() => _sink(entry));
            }
        }

        public void Diag(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diag_trace.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] [TID:{Environment.CurrentManagedThreadId}] {message}\r\n");
            }
            catch { }
        }
    }
}
