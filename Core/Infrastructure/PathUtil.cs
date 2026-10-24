using System.IO;

namespace AutoUnpackTool.Core.Infrastructure
{
    /// <summary>
    /// 路径相关纯函数工具（无状态，静态）。集中原本散落的"归档内相对路径归一化"与"扩展名小写化"。
    /// 作为"注入式服务"约定的有意例外：纯函数无依赖、无状态，无需纳入 DI 容器。
    /// </summary>
    public static class PathUtil
    {
        /// <summary>归一化归档内相对路径：统一分隔符为反斜杠、去除首部分隔符。</summary>
        public static string NormalizeRelativePath(string pathValue)
        {
            if (string.IsNullOrWhiteSpace(pathValue))
                return string.Empty;
            return pathValue.Trim().Replace('/', '\\').TrimStart('\\');
        }

        /// <summary>取小写扩展名（含点），无扩展名时返回空串。</summary>
        public static string LowerExtension(string path)
            => Path.GetExtension(path).ToLowerInvariant();
    }
}
