using System;

namespace AutoUnpackTool.Core.Models
{
    /// <summary>
    /// 日志条目
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; } = string.Empty;
        public ConsoleColor Color { get; set; }
    }
}
