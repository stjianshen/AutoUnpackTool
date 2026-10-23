using System;

namespace AutoUnpackTool.Core.Models
{
    /// <summary>
    /// 密码本列表行（原 PasswordManagerDialog 内嵌类，迁入 Core 供 ViewModel 与服务共用）
    /// </summary>
    public class PasswordItem
    {
        public int Index { get; set; }
        public string Password { get; set; } = string.Empty;
        public int UsageCount { get; set; }
        public string Type { get; set; } = string.Empty;
        public DateTime LastUsedTime { get; set; } = DateTime.MinValue;
    }
}
