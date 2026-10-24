using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Core.Abstractions
{
    /// <summary>
    /// ITaskObserver 便捷扩展：把"封送到 UI 线程设置节点状态"这一高频样板收敛为一行。
    /// FileItem.Status 的 INotifyPropertyChanged 行为保持不变。
    /// </summary>
    public static class TaskObserverExtensions
    {
        /// <summary>在 UI 线程安全地更新文件节点状态文本。</summary>
        public static void SetStatusOnUiThread(this ITaskObserver ui, FileItem item, string status)
            => ui.InvokeOnUi(() => item.Status = status);
    }
}
