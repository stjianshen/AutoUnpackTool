using AutoUnpackTool.Core.Configuration;

namespace AutoUnpackTool.Services
{
    /// <summary>
    /// 对话框服务：ViewModel 不直接 new Window，弹窗一律通过此接口。
    /// 返回值约定：取消/关闭返回 null 或 false。
    /// </summary>
    public interface IDialogService
    {
        /// <summary>选择文件夹，取消返回 null。</summary>
        string? SelectFolder(string title);

        /// <summary>选择文件，取消返回 null。</summary>
        string? SelectFile(string title, string filter, string? fileName = null);

        /// <summary>打开设置对话框；保存返回对话框内的设置实例，取消返回 null。</summary>
        AppSettings? ShowSettings(AppSettings current);

        /// <summary>打开密码管理对话框；保存返回 true。</summary>
        bool ShowPasswords(AppSettings current);

        /// <summary>打开黑名单管理对话框；保存返回 true。</summary>
        bool ShowBlacklist(AppSettings current);

        /// <summary>警告提示框。</summary>
        void ShowWarning(string message, string title = "提示");

        /// <summary>确定/取消确认框，确定返回 true。</summary>
        bool ShowConfirm(string message, string title = "确认");
    }
}
