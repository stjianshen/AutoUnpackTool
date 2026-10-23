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

    /// <summary>
    /// 智能路径处理模式
    /// </summary>
    public enum SmartPathMode
    {
        Concatenate,        // 拼接文件夹名字
        SmartSelect         // 智能选择（优先选择长的、有日文的）
    }
}
