using System;
using System.IO;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// WPF 侧的日志实现：把 Core 层日志交给 LogHub 汇聚。
    /// 线程封送、视图过滤、批量刷新全部由 LogHub 负责，这里只做条目构造，
    /// 因此调用方（后台工作线程）不会被 UI 阻塞。
    /// </summary>
    public sealed class UiAppLogger : IAppLogger
    {
        private readonly LogHub _hub;

        public UiAppLogger(LogHub hub)
        {
            _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        }

        public void Log(string message, ConsoleColor color, FileItem? node = null)
        {
            _hub.Append(new LogEntry
            {
                Timestamp = DateTime.Now,
                Message = message,
                Color = color,
                Owner = node
            });
        }

        public void SetActivityNode(FileItem? node) => LogHub.SetActivity(node);

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
