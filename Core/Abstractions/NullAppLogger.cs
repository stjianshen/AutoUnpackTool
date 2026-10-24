using System;
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Core.Abstractions
{
    /// <summary>
    /// 空实现：用于单元测试或未注入日志的场景（全部丢弃，不产生任何副作用）
    /// </summary>
    public sealed class NullAppLogger : IAppLogger
    {
        public static readonly NullAppLogger Instance = new NullAppLogger();

        public void Log(string message, ConsoleColor color, FileItem? node = null)
        {
        }

        public void SetActivityNode(FileItem? node)
        {
        }

        public void Diag(string message)
        {
        }
    }
}
