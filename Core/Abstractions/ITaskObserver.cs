namespace AutoUnpackTool.Core.Abstractions
{
    /// <summary>
    /// 任务状态观察者：Core 层向 UI 上报线程状态、进度等信息，
    /// UI 层实现负责封送到界面线程并更新绑定属性。
    /// </summary>
    public interface ITaskObserver
    {
        /// <summary>
        /// 测试/解压线程状态变化
        /// </summary>
        void OnThreadState(bool isTesting, bool isExtracting, bool isPaused);

        /// <summary>
        /// 解压进度百分比（0-100）
        /// </summary>
        void OnExtractProgress(int percent);

        /// <summary>
        /// 当前正在测试的密码（null 表示无）
        /// </summary>
        void OnCurrentPassword(string? password);

        /// <summary>
        /// 智能路径处理后的最终路径
        /// </summary>
        void OnFinalPath(string? path);
    }
}
