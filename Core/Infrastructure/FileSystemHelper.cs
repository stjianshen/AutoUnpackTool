using System;
using System.IO;

namespace AutoUnpackTool.Core.Infrastructure
{
    /// <summary>
    /// 文件系统相关的通用辅助方法
    /// </summary>
    public static class FileSystemHelper
    {
        /// <summary>
        /// 生成不冲突的目标文件路径（同名时追加 (2)、(3)...）
        /// </summary>
        public static string GetUniqueFilePath(string directory, string fileName)
        {
            string path = Path.Combine(directory, fileName);
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;

            string name = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            for (int i = 2; i < 1000; i++)
            {
                path = Path.Combine(directory, $"{name} ({i}){ext}");
                if (!File.Exists(path) && !Directory.Exists(path))
                    return path;
            }

            return Path.Combine(directory, $"{name}_{Guid.NewGuid():N}{ext}");
        }

        /// <summary>
        /// 清理文件名中的非法字符（裁剪产物以载体名命名）
        /// </summary>
        public static string SanitizePathSegment(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            return sb.ToString().Trim().TrimEnd('.');
        }

        /// <summary>
        /// 7z 类型名转文件扩展名（gzip -> gz, bzip2 -> bz2）
        /// </summary>
        public static string ArchiveTypeToExtension(string type)
        {
            switch (type.ToLowerInvariant())
            {
                case "gzip": return "gz";
                case "bzip2": return "bz2";
                default: return type.ToLowerInvariant();
            }
        }
    }
}
