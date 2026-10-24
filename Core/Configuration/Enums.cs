namespace AutoUnpackTool.Core.Configuration
{
    public enum FileAction
    {
        Keep,               // 保留原文件
        MoveToRecycleBin,   // 移动到回收站
        Delete,             // 删除文件
        MoveToSpecificDir   // 移动到指定目录
    }

    public enum OutputMode
    {
        SpecificDir,        // 输出到指定目录
        ArchiveDir,         // 输出到压缩文件所在目录
        ArchiveFolder       // 输出到压缩文件同名文件夹
    }

    public enum ExtractMode
    {
        Manual,             // 手动解压（默认）
        Auto                // 自动解压
    }

    /// <summary>界面主题模式（阶段 7：Fluent 外观）。命名避开 WPF 自带的 System.Windows.ThemeMode。</summary>
    public enum AppThemeMode
    {
        System,             // 跟随系统深浅色（默认）
        Light,              // 强制浅色
        Dark                // 强制深色
    }

    /// <summary>
    /// 输出产物重名冲突的处理策略（由 ConflictResolver 统一执行）。
    /// 作用于工具自身的整理/移动/合并阶段；7z 解压写入本身始终覆盖（-y），不受本设置影响。
    /// </summary>
    public enum DuplicateFilePolicy
    {
        Skip,               // 跳过：同名且内容一致则丢弃新产物；内容不一致仍会保留两份（默认）
        Overwrite,          // 覆盖：删掉已存在的目标再写入（仅限本工具生成的目标）
        Rename              // 重命名：一律保留两份，给新产物加唯一后缀
    }

    /// <summary>
    /// 智能路径处理模式
    /// </summary>
    public enum SmartPathMode
    {
        Concatenate,        // 拼接文件夹名字
        SmartSelect         // 智能选择（优先选择长的、有日文的）
    }
}
