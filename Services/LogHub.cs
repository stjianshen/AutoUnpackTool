using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// 日志汇聚点：UiAppLogger 写入，MainViewModel 通过 Logs 绑定呈现。
    /// 上限 5000 条，超出后从最早一条开始丢弃（避免长时间运行内存膨胀）。
    /// </summary>
    public sealed class LogHub
    {
        private const int MaxEntries = 5000;

        public ObservableCollection<LogEntry> Entries { get; } = new ObservableCollection<LogEntry>();

        /// <summary>追加一条日志（必须在 UI 线程调用；UiAppLogger 已负责封送）。</summary>
        public void Append(LogEntry entry)
        {
            Entries.Add(entry);
            while (Entries.Count > MaxEntries)
            {
                Entries.RemoveAt(0);
            }
        }

        /// <summary>用一批日志整体替换当前内容（节点切换时的过滤显示）。</summary>
        public void Replace(IEnumerable<LogEntry> entries)
        {
            Entries.Clear();
            foreach (var entry in entries)
            {
                Entries.Add(entry);
            }
        }

        /// <summary>清空日志。</summary>
        public void Clear() => Entries.Clear();
    }
}
