using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace AutoUnpackTool
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 注册代码页编码提供程序（支持 GBK 等编码）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            
            // 注册全局异常处理
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            
            base.OnStartup(e);
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
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
