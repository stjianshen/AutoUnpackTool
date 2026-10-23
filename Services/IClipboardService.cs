namespace AutoUnpackTool.Services
{
    /// <summary>
    /// 剪贴板服务：ViewModel 不直接调用 WPF Clipboard（单元测试与解耦考虑）。
    /// </summary>
    public interface IClipboardService
    {
        /// <summary>写入文本，失败抛异常由调用方处理。</summary>
        void SetText(string text);
    }
}
