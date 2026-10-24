using System;
using System.Windows;
using AutoUnpackTool.Core.Configuration;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// 主题服务抽象：ViewModel 只表达「想要哪种主题」，具体 Fluent 主题/背板实现留在 UI 层，
    /// 避免 Wpf.Ui 的类型泄漏进 ViewModels 与 Core。
    /// </summary>
    public interface IThemeService
    {
        /// <summary>当前生效的主题模式（来自配置）。</summary>
        AppThemeMode Mode { get; }

        /// <summary>当前是否为深色。</summary>
        bool IsDark { get; }

        /// <summary>主题切换后触发，供日志配色等视图资源同步更新。</summary>
        event EventHandler? ThemeChanged;

        /// <summary>应用主题到整个应用，并重新挂接所有已打开窗口的系统主题监听。</summary>
        void Apply(AppThemeMode mode);

        /// <summary>窗口显示后注册系统深浅色跟随（仅 Mode=System 时生效）。</summary>
        void AttachWindow(Window window);

        /// <summary>窗口关闭后解除监听，避免残留 WndProc 钩子。</summary>
        void DetachWindow(Window window);
    }
}
