using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// 日志汇聚点：全量日志的唯一数据源 + 按树节点过滤的显示视图。
    ///
    /// 三条设计约定（对应旧实现丢日志/串台/阻塞的三个问题）：
    /// 1. _all 是唯一真相（每条带 Owner 归属），Entries 只是当前视图的投影；
    ///    切换节点时从 _all 重建视图，不带 node 的系统日志不再被 Clear 覆盖丢失。
    /// 2. Append 任意线程可调：命中当前视图的条目先入 _pending，再由 UI 线程合并刷新，
    ///    日志写入不再同步阻塞后台工作线程（旧 Dispatcher.Invoke 会反向卡死管线）。
    /// 3. 不带 node 的全局日志兜底归属到 AsyncLocal 的「当前活动节点」，
    ///    每个 worker 一份执行上下文，并发解压时互不串台。
    /// </summary>
    public sealed class LogHub
    {
        /// <summary>全量缓冲上限，超出后丢最早的条目（节点视图随之不再包含它们）。</summary>
        private const int MaxEntries = 20000;

        private static readonly AsyncLocal<FileItem?> _activity = new AsyncLocal<FileItem?>();

        private readonly Dispatcher _dispatcher;
        private readonly object _sync = new object();
        private readonly Queue<LogEntry> _all = new Queue<LogEntry>();
        private List<LogEntry> _pending = new List<LogEntry>();
        private Func<LogEntry, bool> _filter = _ => true;
        private bool _flushScheduled;

        /// <summary>视图集合（ListBox 绑定，只在 UI 线程增删）。</summary>
        public ObservableCollection<LogEntry> Entries { get; } = new ObservableCollection<LogEntry>();

        /// <summary>已进入当前视图的条目（引用相等），防止快照与增量刷新重复呈现。</summary>
        private readonly HashSet<LogEntry> _inView = new HashSet<LogEntry>(ReferenceComparer.Instance);

        public LogHub(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        /// <summary>当前执行上下文的节点（全局日志的兜底归属，取不到为 null）。</summary>
        public static FileItem? ActivityNode => _activity.Value;

        /// <summary>声明当前执行上下文正在处理的节点。</summary>
        public static void SetActivity(FileItem? node) => _activity.Value = node;

        /// <summary>追加一条日志（任意线程安全）：入全量缓冲，命中当前视图则等批量刷新。</summary>
        public void Append(LogEntry entry)
        {
            if (entry == null)
                return;

            entry.Owner ??= ActivityNode;

            lock (_sync)
            {
                _all.Enqueue(entry);
                while (_all.Count > MaxEntries)
                    _all.Dequeue();

                if (_filter(entry))
                    _pending.Add(entry);

                // UI 线程长时间不消化（卡死/繁忙）时不让待刷队列无限长大：
                // 丢掉的条目仍在全量缓冲里，下次切换视图会从 _all 重建
                while (_pending.Count > MaxEntries)
                    _pending.RemoveAt(0);

                if (_pending.Count > 0 && !_flushScheduled)
                {
                    _flushScheduled = true;
                    _dispatcher.BeginInvoke(new Action(FlushPending), DispatcherPriority.Background);
                }
            }
        }

        /// <summary>
        /// 切换显示视图：按谓词从全量缓冲重建 Entries。传 null 表示显示全部日志。
        /// 快照在锁内取，视图增删只在 UI 线程做，避免与增量刷新互踩。
        /// </summary>
        public void SetViewFilter(Func<LogEntry, bool>? filter)
        {
            List<LogEntry> snapshot;
            lock (_sync)
            {
                _filter = filter ?? (_ => true);
                // 待刷新的条目已在 _all 中，清掉避免与快照重复（漏掉的由后续 Append 补上）
                _pending.Clear();
                snapshot = new List<LogEntry>(_all.Where(_filter));
            }

            _dispatcher.Invoke(() =>
            {
                Entries.Clear();
                _inView.Clear();
                foreach (var entry in snapshot)
                {
                    if (_inView.Add(entry))
                        Entries.Add(entry);
                }
            });
        }

        /// <summary>清空全量缓冲与视图（复位/清空列表时调用）。</summary>
        public void Clear()
        {
            lock (_sync)
            {
                _all.Clear();
                _pending.Clear();
            }

            _dispatcher.Invoke(() =>
            {
                Entries.Clear();
                _inView.Clear();
            });
        }

        /// <summary>UI 线程：把攒下的命中条目一次性刷进视图（合并刷新，避免逐条布局）。</summary>
        private void FlushPending()
        {
            List<LogEntry> batch;
            lock (_sync)
            {
                batch = _pending;
                _pending = new List<LogEntry>();
                _flushScheduled = false;
            }

            foreach (var entry in batch)
            {
                if (_inView.Add(entry))
                    Entries.Add(entry);
            }

            while (Entries.Count > MaxEntries)
            {
                _inView.Remove(Entries[0]);
                Entries.RemoveAt(0);
            }
        }

        /// <summary>按引用比较的相等器（日志条目不做值相等语义）。</summary>
        private sealed class ReferenceComparer : IEqualityComparer<LogEntry>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(LogEntry? x, LogEntry? y) => ReferenceEquals(x, y);

            public int GetHashCode(LogEntry obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
