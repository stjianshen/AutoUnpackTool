using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.Core.Services;
using AutoUnpackTool.Services;
using AutoUnpackTool.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AutoUnpackTool
{
    public partial class App : Application
    {
        private ServiceProvider? _services;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 注册代码页编码提供程序（支持 GBK 等编码）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // 注册全局异常处理
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            base.OnStartup(e);

            _services = BuildServiceProvider();

            // ArchiveDetector 与 StegoService 互相引用：构造完成后回填，避免循环依赖
            var detector = _services.GetRequiredService<ArchiveDetector>();
            detector.Stego = _services.GetRequiredService<StegoService>();

            // 启动主窗口（原 App.xaml 的 StartupUri="MainWindow.xaml" 已移除）
            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }

        /// <summary>
        /// 构建 DI 容器：全部 Core 服务/视图模型/窗口均由容器创建。
        /// 队列状态由 MainViewModel 内部的 ExtractionPipeline 单实例持有。
        /// </summary>
        private static ServiceProvider BuildServiceProvider()
        {
            var services = new ServiceCollection();

            // 基础设施
            services.AddSingleton(Current.Dispatcher);
            services.AddSingleton<LogHub>();
            services.AddSingleton<IAppLogger>(sp => new UiAppLogger(
                sp.GetRequiredService<Dispatcher>(),
                sp.GetRequiredService<LogHub>().Append));
            services.AddSingleton<IDialogService, DialogService>();

            // 配置（配置文件路径与重构前一致：%AppData%\AutoUnpackTool\settings.json）
            services.AddSingleton(_ => AppSettings.Load());

            // Core 服务（Singleton：保持缓存/状态在进程内共享）
            services.AddSingleton<BlacklistService>();
            services.AddSingleton<ArchiveNameCleaner>();
            services.AddSingleton<VolumeDetector>();
            services.AddSingleton<ArchiveDetector>();
            services.AddSingleton<StegoService>();
            services.AddSingleton<FileCleanupService>();
            services.AddSingleton<SmartPathService>();

            // 视图模型与窗口
            services.AddTransient<MainViewModel>();
            services.AddSingleton<MainWindow>();

            return services.BuildServiceProvider();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _services?.Dispose();
            base.OnExit(e);
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"程序发生未处理的异常：\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = (Exception)e.ExceptionObject;
            MessageBox.Show(
                $"程序发生致命错误：\n\n{ex.Message}\n\n{ex.StackTrace}",
                "致命错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            MessageBox.Show(
                $"异步任务发生异常：\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "异步任务错误",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.SetObserved();
        }
    }
}
