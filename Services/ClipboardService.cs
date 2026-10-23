using System.Windows;

namespace AutoUnpackTool.Services
{
    /// <summary>IClipboardService 的 WPF 实现（WPF Clipboard 只出现在 App 层）。</summary>
    public sealed class ClipboardService : IClipboardService
    {
        public void SetText(string text)
        {
            Clipboard.SetText(text);
        }
    }
}
