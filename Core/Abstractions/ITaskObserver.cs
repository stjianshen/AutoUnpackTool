using System;
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Core.Abstractions
{
    /// <summary>
    /// 任务观察者：后台线程（测试/解压/监控）不直接操作控件，
    /// 通过此接口把线程状态、进度、当前密码、最终路径等上报给 UI 层。
    /// </summary>
    public interface ITaskObserver
    {
        /// <summary>把动作封送到 UI 线程执行（等价于原来的 Dispatcher.Invoke）。</summary>
        void InvokeOnUi(Action action);

        /// <summary>当前是否已在 UI 线程（等价于原来的 Dispatcher.CheckAccess）。</summary>
        bool CheckUiAccess();

        /// <summary>线程状态变化：测试中 / 解压中 / 暂停中。</summary>
        void OnThreadState(bool isTesting, bool isExtracting, bool isPaused);

        /// <summary>强制把状态栏置为“未开始”（全部流程结束时使用）。</summary>
        void OnIdleStatus();

        /// <summary>批量智能路径检查的触发请求（需要在 UI 线程再次转入宿主）。</summary>
        void RequestBatchSmartPathCheck();

        /// <summary>解压进度百分比。</summary>
        void OnExtractProgress(int percent);

        /// <summary>当前测试中的密码文本。</summary>
        void OnCurrentPassword(string text);

        /// <summary>最终输出路径文本。</summary>
        void OnFinalPath(string text);
    }
}
