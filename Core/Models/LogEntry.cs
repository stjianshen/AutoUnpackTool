using System;

namespace AutoUnpackTool.Core.Models
{
    /// <summary>
    /// 日志条目。
    /// Owner 表示这条日志归属的文件树节点：由调用方显式传入；
    /// 未传入时由 LogHub 兜底挂到「当前活动节点」上（见 LogHub.ActivityNode），
    /// 仍取不到则为 null，代表纯系统日志（只出现在「全部日志」视图里）。
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; } = string.Empty;
        public ConsoleColor Color { get; set; }

        /// <summary>归属的树节点（null=系统级日志）</summary>
        public FileItem? Owner { get; set; }
    }
}
