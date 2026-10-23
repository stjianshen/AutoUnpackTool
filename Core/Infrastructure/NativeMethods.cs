using System;
using System.Runtime.InteropServices;

namespace AutoUnpackTool.Core.Infrastructure
{
    /// <summary>
    /// Windows Shell API 声明（SHFileOperation 用于静默移动到回收站）
    /// </summary>
    public static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        public const uint FO_DELETE = 0x0003;
        public const ushort FOF_ALLOWUNDO = 0x0040;  // 允许撤销（移动到回收站）
        public const ushort FOF_NOCONFIRMATION = 0x0010;  // 不显示确认对话框
        public const ushort FOF_SILENT = 0x0004;  // 静默模式

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
    }
}
