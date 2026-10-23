using System;
using AutoUnpackTool.ViewModels;
using AutoUnpackTool.Core.Abstractions;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// ITaskObserver 的 WPF 侧实现：把 Core 后台线程的上报转投给 MainViewModel。
    /// 线程封送由 ViewModel 的 Apply* / InvokeOnUi 负责（与原 Dispatcher.Invoke 等价）。
    /// </summary>
    public sealed class UiTaskObserver : ITaskObserver
    {
        private readonly MainViewModel _vm;

        public UiTaskObserver(MainViewModel vm)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        }

        public void InvokeOnUi(Action action) => _vm.InvokeOnUi(action);

        public bool CheckUiAccess() => _vm.CheckUiAccess();

        public void OnThreadState(bool isTesting, bool isExtracting, bool isPaused) => _vm.ApplyThreadState(isTesting, isExtracting, isPaused);

        public void OnIdleStatus() => _vm.ApplyIdleStatus();

        public void RequestBatchSmartPathCheck() => _vm.RequestBatchSmartPathCheck();

        public void OnExtractProgress(int percent) => _vm.ApplyExtractProgress(percent);

        public void OnCurrentPassword(string text) => _vm.ApplyCurrentPassword(text);

        public void OnFinalPath(string text) => _vm.ApplyFinalPath(text);
    }
}
