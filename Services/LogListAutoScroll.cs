using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// ListBox 日志自动滚到底部的附加行为（等价于原 TxtLog.ScrollToEnd）。
    /// 滚动必须延后到布局空闲执行：在 CollectionChanged 里同步 ScrollIntoView 会在
    /// 容器生成过程中强制 Measure，触发“某个 ItemsControl 与它的项源不一致”异常。
    /// 同时做排队合并：切换日志视图时可能一口气追加上万条，不能逐条触发一次滚动。
    /// </summary>
    public static class LogListAutoScroll
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(LogListAutoScroll),
            new PropertyMetadata(false, OnEnabledChanged));

        public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

        public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ListBox listBox)
                return;

            if ((bool)e.NewValue)
            {
                // 闭包共享的“已排队”标记：批量追加期间只排一次滚动
                bool[] scrollPending = new bool[1];

                listBox.Loaded += (s, args) => RequestScrollToEnd(listBox, scrollPending);
                ((INotifyCollectionChanged)listBox.Items).CollectionChanged += (s, args) => RequestScrollToEnd(listBox, scrollPending);
            }
        }

        /// <summary>请求一次延后到底部的滚动；已有同类请求在排队时直接合并。</summary>
        private static void RequestScrollToEnd(ListBox listBox, bool[] pending)
        {
            if (pending[0])
                return;

            pending[0] = true;
            listBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                pending[0] = false;
                ScrollToEnd(listBox);
            }), DispatcherPriority.Background);
        }

        private static void ScrollToEnd(ListBox listBox)
        {
            if (!listBox.IsLoaded)
                return;

            listBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!listBox.IsLoaded)
                    return;

                int count = listBox.Items.Count;
                if (count == 0)
                    return;

                try
                {
                    listBox.ScrollIntoView(listBox.Items[count - 1]);
                }
                catch (InvalidOperationException)
                {
                    // 布局/容器生成过程中滚动失败：忽略，下一次日志追加会再次触发滚动
                }
            }), DispatcherPriority.Background);
        }
    }
}
