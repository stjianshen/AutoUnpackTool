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
        /// 记录一条日志。node 不为空时该条日志归属到此节点（按节点过滤时可见）。
        /// </summary>
        void Log(string message, ConsoleColor color, FileItem? node = null);

        /// <summary>
        /// 声明“当前执行上下文正在处理哪个节点”：不带 node 的全局日志会自动归属到它，
        /// 使按节点过滤能看到该任务从头到尾的完整流程（含线程调度、收尾阶段等系统日志）。
        /// 基于异步上下文传递，多个 worker 并发时互不串台；处理结束传 null 撤销。
        /// </summary>
        void SetActivityNode(FileItem? node);

        /// <summary>
        /// 诊断日志：直接落盘，绕过 UI。
        /// UI 线程被阻塞时诊断轨迹依然可用。
        /// </summary>
        void Diag(string message);
    }
}
