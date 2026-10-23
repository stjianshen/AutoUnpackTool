using System.Collections.Generic;

namespace AutoUnpackTool.Core.Configuration
{
    /// <summary>
    /// 分卷压缩包信息
    /// </summary>
    public class ArchiveVolumeInfo
    {
        /// <summary>
        /// 主卷文件路径（通常是 .001, .part1.rar, .7z.001 等）
        /// </summary>
        public string MainVolumePath { get; set; } = string.Empty;

        /// <summary>
        /// 所有分卷文件路径列表
        /// </summary>
        public List<string> AllVolumePaths { get; set; } = new List<string>();

        /// <summary>
        /// 分卷总数
        /// </summary>
        public int VolumeCount => AllVolumePaths.Count;

        /// <summary>
        /// 是否为多分卷（至少两个物理分卷文件）
        /// </summary>
        public bool IsMultiVolume => VolumeCount > 1;

        /// <summary>
        /// 是否按分卷列表处理（含仅存在 .001 单卷的命名格式，用于清理时遍历 AllVolumePaths）
        /// </summary>
        public bool HasVolumeList => AllVolumePaths != null && AllVolumePaths.Count > 0;
    }
}
