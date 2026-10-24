using System;
using System.Collections.Generic;
using System.Windows;
using AutoUnpackTool.Core.Configuration;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// 基于 Wpf.Ui 的主题实现：负责深浅色切换、Mica 背板，以及「跟随系统」的 WndProc 监听。
    /// 只有 Mode=System 时才挂监听，强制浅色/深色时立刻解除，避免系统主题覆盖用户选择。
    /// </summary>
    public sealed class ThemeService : IThemeService
    {
        private readonly HashSet<Window> _watched = new();

        public AppThemeMode Mode { get; private set; } = AppThemeMode.System;

        public bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

        public event EventHandler? ThemeChanged;

        public void Apply(AppThemeMode mode)
        {
            Mode = mode;
            ApplicationThemeManager.Apply(Resolve(mode), WindowBackdropType.Mica, true);
            ConsoleColorToBrushConverter.ApplyPalette(IsDark);

            var app = Application.Current;
            if (app != null)
            {
                foreach (Window window in app.Windows)
                {
                    if (window.IsLoaded)
                    {
                        AttachWindow(window);
                    }
                }
            }

            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void AttachWindow(Window window)
        {
            if (window == null)
            {
                return;
            }

            if (Mode == AppThemeMode.System)
            {
                if (_watched.Add(window))
                {
                    SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);
                }
            }
            else
            {
                DetachWindow(window);
            }
        }

        public void DetachWindow(Window window)
        {
            if (window == null)
            {
                return;
            }

            if (_watched.Remove(window))
            {
                try
                {
                    SystemThemeWatcher.UnWatch(window);
                }
                catch
                {
                    // 窗口已销毁时解除钩子可能失败，忽略
                }
            }
        }

        /// <summary>把配置里的主题模式换算成 Wpf.Ui 的主题枚举，跟随系统时查询系统当前值。</summary>
        private static ApplicationTheme Resolve(AppThemeMode mode) => mode switch
        {
            AppThemeMode.Light => ApplicationTheme.Light,
            AppThemeMode.Dark => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() switch
            {
                SystemTheme.Dark or SystemTheme.HCBlack or SystemTheme.HC1 => ApplicationTheme.Dark,
                _ => ApplicationTheme.Light
            }
        };
    }
}
