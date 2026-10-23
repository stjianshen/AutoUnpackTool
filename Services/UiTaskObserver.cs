using System;
using AutoUnpackTool.Core.Abstractions;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// ITaskObserver 的 WPF 实现：把 Core 层上报的线程状态、进度、密码、最终路径
    /// 封送到主窗口的 Dispatcher 上更新控件。行为与重构前逐处 Dispatcher.Invoke 等价。
    /// </summary>
    public sealed class UiTaskObserver : ITaskObserver
    {
        private readonly MainWindow _window;

        public UiTaskObserver(MainWindow window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
        }

        public MainWindow Window => _window;

        public void InvokeOnUi(Action action)
        {
            _window.Dispatcher.Invoke(action);
        }

        public bool CheckUiAccess()
        {
            return _window.Dispatcher.CheckAccess();
        }

        public void OnThreadState(bool isTesting, bool isExtracting, bool isPaused)
        {
            _window.ApplyThreadState(isTesting, isExtracting, isPaused);
        }

        public void OnIdleStatus()
        {
            _window.ApplyIdleStatus();
        }

        public void RequestBatchSmartPathCheck()
        {
            _ = _window.Dispatcher.InvokeAsync(() => _window.CheckAndTriggerBatchSmartPathProcessing());
        }

        public void OnExtractProgress(int percent)
        {
            _window.ApplyExtractProgress(percent);
        }

        public void OnCurrentPassword(string text)
        {
            _window.ApplyCurrentPassword(text);
        }

        public void OnFinalPath(string text)
        {
            _window.ApplyFinalPath(text);
        }
    }
}
