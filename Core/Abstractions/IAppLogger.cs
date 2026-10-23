using System;
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Core.Abstractions
{
    /// <summary>
    /// 应用日志通道。Core 层的所有业务代码只依赖此接口，
    /// 由 UI 层实现具体的呈现方式（文本框、列表、文件等）。
    /// 使用 ConsoleColor 而非自定义枚举，是为了完整保留现有 8 种日志颜色语义。
    /// </summary>
    public interface IAppLogger
    {
        /// <summary>
        /// 记录一条日志。node 不为空时同时写入该节点的日志集合。
        /// </summary>
        void Log(string message, ConsoleColor color, FileItem? node = null);

        /// <summary>
        /// 诊断日志：直接落盘，绕过 UI。
        /// UI 线程被阻塞时诊断轨迹依然可用。
        /// </summary>
        void Diag(string message);
    }
}
