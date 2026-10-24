using System;
using System.IO;
using System.Security.Cryptography;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Configuration;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>冲突对象的类型。目录无法用长度判重，Skip 策略会退化为 Rename。</summary>
    public enum ConflictKind
    {
        File,
        Directory
    }

    /// <summary>
    /// 唯一名的造法。保留两种风格是为了不破坏「临时前缀 + 事后去前缀」那套两步协议
    /// （最外层扁平化用 GUID 前缀，序号后缀可能被协议里后续要创建的名字撞掉）。
    /// </summary>
    public enum NamingStyle
    {
        /// <summary>name_1.ext、name_2.ext …（清理目录用，用户可读）</summary>
        NumericSuffix,
        /// <summary>1a2b3c4d_name（扁平化协议用，事后会被去掉）</summary>
        GuidPrefix
    }

    /// <summary>一次重名冲突的裁决结果。</summary>
    public sealed class ConflictDecision
    {
        /// <summary>最终落地路径（可能已被改名）</summary>
        public required string FinalPath { get; init; }

        /// <summary>true = 按策略跳过，源文件留在原地不要动</summary>
        public bool ShouldSkip { get; init; }

        /// <summary>true = 移动前需要先删掉已存在的目标</summary>
        public bool ShouldDeleteTarget { get; init; }

        /// <summary>true = 确实发生了冲突（false 时其余字段基本是直通）</summary>
        public bool Conflict { get; init; }

        /// <summary>要写进日志的说明，null 表示无需记录</summary>
        public string? Note { get; init; }

        /// <summary>Note 的日志级别</summary>
        public ConsoleColor NoteColor { get; init; } = ConsoleColor.Gray;
    }

    /// <summary>
    /// 输出产物重名冲突的唯一决策点。
    ///
    /// 迁移前这类判断散在 13 处、策略互相矛盾（覆盖 / 跳过 / GUID 改名 / 序号改名 / 无护栏递归删除），
    /// 本类把「是不是重复」「撞了怎么办」「唯一名怎么造」收拢到一处，并强制一条全局不变量：
    /// **目标不是工具自己生成的（未在解压节点表里登记）时，任何策略都不许删除它**，一律降级为重命名保留两份。
    /// 这条不变量顺带修掉了智能路径步骤3 里那处无护栏的 Directory.Delete(path, true)。
    ///
    /// 作用范围：工具自身的整理/移动/合并阶段。7z 解压写入本身仍由 -y 决定（始终覆盖），
    /// 因为改 -ao 会连带影响密码测试与多轮递归扫描的既有语义。
    /// </summary>
    public sealed class ConflictResolver
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;

        public ConflictResolver(AppSettings settings, IAppLogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>当前生效的策略（实时读配置，改设置无需重启）</summary>
        public DuplicateFilePolicy Policy => _settings.DuplicateFileAction;

        /// <summary>
        /// 裁决一次冲突。目标不存在时直接放行。
        /// </summary>
        /// <param name="sourcePath">准备落地/移入的源</param>
        /// <param name="destPath">计划落地路径</param>
        /// <param name="kind">冲突对象类型</param>
        /// <param name="targetOwnedByTool">目标是否是工具自己生成的（解压节点/扁平化产物）。false 时禁止删除。</param>
        /// <param name="style">需要改名时采用的命名风格</param>
        public ConflictDecision Resolve(string sourcePath, string destPath, ConflictKind kind,
                                        bool targetOwnedByTool, NamingStyle style = NamingStyle.GuidPrefix)
        {
            bool targetExists = kind == ConflictKind.Directory
                ? Directory.Exists(destPath)
                : File.Exists(destPath);

            if (!targetExists)
                return new ConflictDecision { FinalPath = destPath, Conflict = false };

            string name = Path.GetFileName(destPath);
            string dirOfDest = Path.GetDirectoryName(destPath) ?? ".";

            // ---- 目录：无法用内容判重，走单独分支 ----
            // 工具自己生成的旧目录（解压节点）直接替换，保持迁移前行为；
            // 用户原始目录任何策略都不许递归删除，只能改名。Rename 策略下两者都改名。
            if (kind == ConflictKind.Directory)
            {
                if (targetOwnedByTool && Policy != DuplicateFilePolicy.Rename)
                {
                    return new ConflictDecision
                    {
                        FinalPath = destPath,
                        Conflict = true,
                        ShouldDeleteTarget = true,
                        Note = $"替换已存在的解压产物目录: {name}",
                        NoteColor = ConsoleColor.Gray
                    };
                }

                string renamedDir = BuildUniquePath(dirOfDest, name, style);
                return new ConflictDecision
                {
                    FinalPath = renamedDir,
                    Conflict = true,
                    Note = $"目标目录已存在{(!targetOwnedByTool ? "且非本工具生成，禁止删除" : "")}，改用安全名称: {Path.GetFileName(renamedDir)}",
                    NoteColor = ConsoleColor.Yellow
                };
            }

            // ---- 文件 ----
            // 全局不变量：不是工具生成的东西，禁止覆盖删除
            if (!targetOwnedByTool && Policy == DuplicateFilePolicy.Overwrite)
            {
                string demoted = BuildUniquePath(dirOfDest, name, style);
                return new ConflictDecision
                {
                    FinalPath = demoted,
                    Conflict = true,
                    Note = $"⚠ 目标已存在且非本工具生成，禁止覆盖，改用安全名称: {Path.GetFileName(demoted)}",
                    NoteColor = ConsoleColor.Yellow
                };
            }

            switch (Policy)
            {
                case DuplicateFilePolicy.Overwrite:
                    return new ConflictDecision
                    {
                        FinalPath = destPath,
                        Conflict = true,
                        ShouldDeleteTarget = true,
                        Note = $"覆盖已存在的文件: {name}",
                        NoteColor = ConsoleColor.Gray
                    };

                case DuplicateFilePolicy.Rename:
                    string renamed2 = BuildUniquePath(dirOfDest, name, style);
                    return new ConflictDecision
                    {
                        FinalPath = renamed2,
                        Conflict = true,
                        Note = $"目标已存在，保留两份: {name} -> {Path.GetFileName(renamed2)}",
                        NoteColor = ConsoleColor.Yellow
                    };

                default: // Skip
                    if (IsLikelyDuplicate(sourcePath, destPath))
                    {
                        return new ConflictDecision
                        {
                            FinalPath = destPath,
                            Conflict = true,
                            ShouldSkip = true,
                            Note = $"跳过重复文件（同名同内容）: {name}",
                            NoteColor = ConsoleColor.Gray
                        };
                    }

                    string safe = BuildUniquePath(dirOfDest, name, style);
                    return new ConflictDecision
                    {
                        FinalPath = safe,
                        Conflict = true,
                        Note = $"同名文件内容不同，改用安全名称保留: {name} -> {Path.GetFileName(safe)}",
                        NoteColor = ConsoleColor.Yellow
                    };
            }
        }

        /// <summary>
        /// 按策略执行移动：自动处理跳过/删目标/改名，并写日志。
        /// </summary>
        /// <returns>实际落地路径；null 表示按策略跳过（源文件未动）</returns>
        public string? TryMove(string sourcePath, string destPath, ConflictKind kind,
                               bool targetOwnedByTool, NamingStyle style = NamingStyle.GuidPrefix)
        {
            var d = Resolve(sourcePath, destPath, kind, targetOwnedByTool, style);
            if (d.Note != null)
                _logger.Log($"[重名处理] {d.Note}", d.NoteColor);

            if (d.ShouldSkip)
                return null;

            if (d.ShouldDeleteTarget)
            {
                if (kind == ConflictKind.Directory)
                    Directory.Delete(d.FinalPath, true);
                else
                    File.Delete(d.FinalPath);
            }

            if (kind == ConflictKind.Directory)
                Directory.Move(sourcePath, d.FinalPath);
            else
                File.Move(sourcePath, d.FinalPath);

            return d.FinalPath;
        }

        /// <summary>造一个目录内不存在的唯一名字。</summary>
        public string BuildUniquePath(string dir, string fileName, NamingStyle style)
        {
            for (int i = 1; i < 10000; i++)
            {
                string candidate = style == NamingStyle.GuidPrefix
                    ? $"{Guid.NewGuid().ToString("N")[..8]}_{fileName}"
                    : MakeNumericName(fileName, i);

                string full = Path.Combine(dir, candidate);
                if (!File.Exists(full) && !Directory.Exists(full))
                    return full;
            }
            throw new IOException($"无法为 {fileName} 生成唯一名称");
        }

        private static string MakeNumericName(string fileName, int n)
        {
            string name = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            return $"{name}_{n}{ext}";
        }

        /// <summary>
        /// 近似判重：长度相等 + 首/中/尾三段抽样 SHA-256 相同。
        /// 是启发式而非强一致证明（刻意构造的文件可绕过），但远强于迁移前只比 Length。
        /// 任何读取失败都按“不重复”处理，宁可保留两份也不丢数据。
        /// </summary>
        public static bool IsLikelyDuplicate(string fileA, string fileB)
        {
            try
            {
                var a = new FileInfo(fileA);
                var b = new FileInfo(fileB);
                if (a.Length != b.Length)
                    return false;
                if (a.Length == 0)
                    return true;

                return SampleHash(a) == SampleHash(b);
            }
            catch
            {
                return false;
            }
        }

        private static string SampleHash(FileInfo info)
        {
            const long window = 64 * 1024;
            using var fs = info.OpenRead();
            using var sha = SHA256.Create();
            sha.Initialize();

            long len = fs.Length;
            foreach (long start in new long[] { 0, len / 2, Math.Max(0, len - window) })
            {
                fs.Seek(start, SeekOrigin.Begin);
                int size = (int)Math.Min(window, len - start);
                byte[] buf = new byte[size];
                int read = 0;
                while (read < size)
                {
                    int n = fs.Read(buf, read, size - read);
                    if (n <= 0) break;
                    read += n;
                }
                sha.TransformBlock(buf, 0, read, null, 0);
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
    }
}
