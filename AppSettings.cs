using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoUnpackTool
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

    public class PasswordEntry
    {
        public string Password { get; set; } = string.Empty;
        public int UsageCount { get; set; } = 0;
        public DateTime LastUsedTime { get; set; } = DateTime.MinValue;
        
        /// <summary>
        /// 计算密码的综合评分（用于排序）
        /// 优先级：最近使用时间 > 使用频率
        /// - 1小时内使用过：获得极高的基础分（10000分），确保排在最前
        /// - 24小时内使用过：获得高分（5000分）
        /// - 7天内使用过：获得中等分（1000分）
        /// - 超过7天：仅按使用次数排序
        /// </summary>
        public double Score
        {
            get
            {
                double recencyBonus = 0;
                
                // 根据最近使用时间给予不同的基础加分
                if (LastUsedTime != DateTime.MinValue)
                {
                    double hoursSinceLastUse = (DateTime.Now - LastUsedTime).TotalHours;
                    
                    if (hoursSinceLastUse <= 1)
                    {
                        // 1小时内使用过：极高优先级
                        recencyBonus = 10000;
                    }
                    else if (hoursSinceLastUse <= 24)
                    {
                        // 24小时内使用过：高优先级
                        recencyBonus = 5000;
                    }
                    else if (hoursSinceLastUse <= 168) // 7天
                    {
                        // 7天内使用过：中等优先级
                        recencyBonus = 1000;
                    }
                    else
                    {
                        // 超过7天：使用时间衰减
                        double daysSinceLastUse = hoursSinceLastUse / 24;
                        recencyBonus = Math.Max(0, 500 - daysSinceLastUse * 10);
                    }
                }
                
                // 使用次数作为次要排序因素
                double frequencyScore = UsageCount * 10;
                
                return recencyBonus + frequencyScore;
            }
        }
    }

    public class AppSettings
    {
        // 配置文件路径（公开属性）
        public static string ConfigFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AutoUnpackTool",
            "settings.json");

        private static readonly string ConfigPath = ConfigFilePath;

        public string SevenZipPath { get; set; } = string.Empty;
        public string PasswordFilePath { get; set; } = string.Empty;
        public string OutputDir { get; set; } = string.Empty;
        public int ThreadCount { get; set; } = 2;
        public FileAction FileAfterExtract { get; set; } = FileAction.Keep;
        public OutputMode OutputMode { get; set; } = OutputMode.SpecificDir;
        public ExtractMode ExtractMode { get; set; } = ExtractMode.Manual;
        public bool ShowCliWindow { get; set; } = false; // 是否显示CLI窗口
        
        /// <summary>
        /// 分卷压缩包清理后的目标目录（当 FileAfterExtract = MoveToSpecificDir 时使用）
        /// </summary>
        public string ArchiveCleanupDir { get; set; } = string.Empty;
        
        /// <summary>
        /// 是否启用分卷压缩包自动检测
        /// </summary>
        public bool EnableMultiVolumeDetection { get; set; } = true;
        
        /// <summary>
        /// 是否启用智能路径处理（解压后自动扁平化多层嵌套文件夹）
        /// </summary>
        public bool EnableSmartPathProcessing { get; set; } = false;

        /// <summary>
        /// 是否启用隐写文件探测（MP4/MKV 等容器内嵌压缩包）
        /// 默认关闭以减少扫描开销
        /// </summary>
        public bool EnableStegoDetection { get; set; } = false;

        /// <summary>
        /// 隐写探测文件大小下限（MB），低于该值不做隐写探测
        /// 默认 10MB（视频文件通常不会太小）
        /// </summary>
        public int StegoDetectionMinFileSizeMB { get; set; } = 10;
        
        /// <summary>
        /// 智能路径处理模式
        /// </summary>
        public SmartPathMode SmartPathProcessingMode { get; set; } = SmartPathMode.SmartSelect;
        
        /// <summary>
        /// 需要检测的压缩文件扩展名列表（逗号分隔）
        /// </summary>
        public string ArchiveExtensions { get; set; } = ".zip,.rar,.7z,.tar,.gz,.bz2,.xz,.zst,.zstd,.tgz,.tbz2,.tbz,.txz,.cab,.iso,.wim,.arj,.lzh,.cpio,.rpm,.deb";
        
        /// <summary>
        /// 需要排除的文件扩展名列表（逗号分隔，优先级高于包含列表）
        /// </summary>
        public string ExcludedExtensions { get; set; } = ".exe,.dll,.msi,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.pdf,.jpg,.jpeg,.png,.gif,.bmp,.mp3,.mp4,.avi,.mkv";

        /// <summary>
        /// 黑名单文件模式列表（逗号或换行分隔，支持通配符 * 和 ?），匹配的文件将被永久删除
        /// </summary>
        public string BlacklistFiles { get; set; } = "";

        // 永久密码（按使用次数排序）
        public List<PasswordEntry> PermanentPasswords { get; set; } = new List<PasswordEntry>();
        
        // 向后兼容：保留私有字段的引用
        private List<PasswordEntry> _permanentPasswords 
        { 
            get => PermanentPasswords; 
            set => PermanentPasswords = value; 
        }
        
        // 一次性密码（本次运行添加，排在最前）
        // 注意：添加 JsonIgnore 特性，确保一次性密码不会被序列化到配置文件
        // 这样一次性密码只在内存中生效，重启后自动清除
        [JsonIgnore]
        public List<string> OneTimePasswords { get; set; } = new List<string>();

        /// <summary>
        /// 从文件加载配置
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    
                    // 添加更详细的错误处理
                    try
                    {
                        var settings = JsonSerializer.Deserialize<AppSettings>(json);
                        if (settings != null)
                        {
                            // 确保 PermanentPasswords 不为 null
                            if (settings.PermanentPasswords == null)
                            {
                                settings.PermanentPasswords = new List<PasswordEntry>();
                            }
                            
                            Console.WriteLine($"✅ 配置加载成功: {ConfigPath}");
                            return settings;
                        }
                    }
                    catch (System.Text.Json.JsonException jsonEx)
                    {
                        // JSON 格式错误，备份并创建新配置
                        string backupPath = ConfigPath + ".backup";
                        try
                        {
                            File.Copy(ConfigPath, backupPath, true);
                            Console.WriteLine($"⚠️  配置文件JSON格式错误，已备份到: {backupPath}");
                        }
                        catch { }
                        
                        throw new Exception($"配置文件JSON格式错误，请检查文件内容。\n\n原始错误: {jsonEx.Message}", jsonEx);
                    }
                }
                else
                {
                    Console.WriteLine($"ℹ️  配置文件不存在，将创建新配置: {ConfigPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 加载配置失败: {ex.Message}");
                // 不抛出异常，返回默认配置
            }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                string? directory = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(ConfigPath, json);
                
                // 验证是否保存成功
                if (!File.Exists(ConfigPath))
                {
                    throw new Exception("配置文件创建失败");
                }
            }
            catch (Exception ex)
            {
                // 重新抛出异常，让调用者处理
                throw new Exception($"保存配置文件失败。\n\n配置路径: {ConfigPath}\n\n错误信息: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 从文件加载密码（支持两个Tab分隔符）
        /// 格式：密码\t\t使用次数\t\t最后使用时间(Ticks)
        /// 注意：优先从配置文件加载，如果配置文件没有密码则从文件加载
        /// </summary>
        public List<PasswordEntry> LoadPasswordsFromFile()
        {
            // 优先从配置文件加载永久密码
            if (PermanentPasswords != null && PermanentPasswords.Count > 0)
            {
                // 按综合评分排序后返回
                return PermanentPasswords.OrderByDescending(p => p.Score).ToList();
            }

            // 如果配置文件没有密码，则从外部文件加载
            if (string.IsNullOrWhiteSpace(PasswordFilePath) || !File.Exists(PasswordFilePath))
            {
                // 返回默认密码
                return new List<PasswordEntry>
                {
                    new PasswordEntry { Password = "123456", UsageCount = 0, LastUsedTime = DateTime.MinValue },
                    new PasswordEntry { Password = "password", UsageCount = 0, LastUsedTime = DateTime.MinValue },
                    new PasswordEntry { Password = "12345678", UsageCount = 0, LastUsedTime = DateTime.MinValue },
                    new PasswordEntry { Password = "admin", UsageCount = 0, LastUsedTime = DateTime.MinValue },
                    new PasswordEntry { Password = "root", UsageCount = 0, LastUsedTime = DateTime.MinValue }
                };
            }

            try
            {
                string[] lines = File.ReadAllLines(PasswordFilePath);
                var passwords = new List<PasswordEntry>();
                
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        // 解析密码、使用次数和最后使用时间（使用两个Tab分隔）
                        string password = trimmed;
                        int usageCount = 0;
                        DateTime lastUsedTime = DateTime.MinValue;
                        
                        // 尝试分割密码、使用次数和时间戳
                        string[] parts = trimmed.Split(new[] { "\t\t" }, StringSplitOptions.None);
                        if (parts.Length >= 2)
                        {
                            password = parts[0].Trim();
                            int.TryParse(parts[1].Trim(), out usageCount);
                            
                            // 如果有时间戳，解析它
                            if (parts.Length >= 3 && long.TryParse(parts[2].Trim(), out long ticks))
                            {
                                lastUsedTime = new DateTime(ticks);
                            }
                        }
                        
                        // 检查是否已存在
                        if (!passwords.Any(p => p.Password == password))
                        {
                            passwords.Add(new PasswordEntry 
                            { 
                                Password = password, 
                                UsageCount = usageCount,
                                LastUsedTime = lastUsedTime
                            });
                        }
                    }
                }

                // 按综合评分降序排序（考虑使用次数和最近使用时间）
                PermanentPasswords = passwords.OrderByDescending(p => p.Score).ToList();
                return PermanentPasswords;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载密码本失败: {ex.Message}");
                // 失败时返回默认密码
                return new List<PasswordEntry>
                {
                    new PasswordEntry { Password = "123456", UsageCount = 0, LastUsedTime = DateTime.MinValue },
                    new PasswordEntry { Password = "password", UsageCount = 0, LastUsedTime = DateTime.MinValue },
                    new PasswordEntry { Password = "12345678", UsageCount = 0, LastUsedTime = DateTime.MinValue },
                    new PasswordEntry { Password = "admin", UsageCount = 0, LastUsedTime = DateTime.MinValue },
                    new PasswordEntry { Password = "root", UsageCount = 0, LastUsedTime = DateTime.MinValue }
                };
            }
        }

        /// <summary>
        /// 从旧格式的密码本文件导入密码（支持TAB分隔格式）
        /// </summary>
        /// <returns>返回导入结果信息</returns>
        public string ImportPasswordsFromOldFormat(string oldPasswordFilePath)
        {
            if (string.IsNullOrWhiteSpace(oldPasswordFilePath) || !File.Exists(oldPasswordFilePath))
            {
                throw new ArgumentException("密码本文件路径无效或文件不存在");
            }

            try
            {
                string[] lines = File.ReadAllLines(oldPasswordFilePath);
                int importedCount = 0;
                int skippedCount = 0;
                int emptyCount = 0;

                Console.WriteLine($"开始导入密码本: {oldPasswordFilePath}");
                Console.WriteLine($"文件总行数: {lines.Length}");
                Console.WriteLine($"配置文件中现有密码数: {PermanentPasswords.Count}");

                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                    {
                        emptyCount++;
                        continue;
                    }

                    // 解析密码（支持TAB分隔格式：密码\t\t使用次数\t\t时间）
                    string password = trimmed;
                    int usageCount = 0;
                    DateTime lastUsedTime = DateTime.MinValue;

                    // 尝试分割
                    string[] parts = trimmed.Split(new[] { "\t\t" }, StringSplitOptions.None);
                    if (parts.Length >= 1)
                    {
                        password = parts[0].Trim();
                        
                        if (parts.Length >= 2)
                        {
                            int.TryParse(parts[1].Trim(), out usageCount);
                        }

                        if (parts.Length >= 3 && long.TryParse(parts[2].Trim(), out long ticks))
                        {
                            lastUsedTime = new DateTime(ticks);
                        }
                    }

                    // 验证密码不为空
                    if (string.IsNullOrEmpty(password))
                    {
                        Console.WriteLine($"跳过空密码");
                        continue;
                    }

                    // 如果密码不存在，添加它
                    if (!PermanentPasswords.Any(p => p.Password == password))
                    {
                        PermanentPasswords.Add(new PasswordEntry
                        {
                            Password = password,
                            UsageCount = usageCount,
                            LastUsedTime = lastUsedTime
                        });
                        importedCount++;
                        Console.WriteLine($"导入新密码: {password}");
                    }
                    else
                    {
                        skippedCount++;
                        Console.WriteLine($"跳过重复密码: {password}");
                    }
                }

                Console.WriteLine($"导入完成 - 新导入: {importedCount}, 跳过重复: {skippedCount}, 空行: {emptyCount}");
                Console.WriteLine($"配置文件现有密码总数: {PermanentPasswords.Count}");

                if (importedCount > 0)
                {
                    // 重新排序
                    PermanentPasswords = PermanentPasswords.OrderByDescending(p => p.Score).ToList();
                    
                    // 自动保存到配置文件
                    Console.WriteLine($"正在保存配置文件: {ConfigPath}");
                    Save();
                    Console.WriteLine($"配置文件保存成功");
                    
                    // 验证保存结果
                    if (File.Exists(ConfigPath))
                    {
                        string savedJson = File.ReadAllText(ConfigPath);
                        if (savedJson.Contains("\"PermanentPasswords\""))
                        {
                            Console.WriteLine("✓ 验证成功: PermanentPasswords 已写入配置文件");
                        }
                        else
                        {
                            Console.WriteLine("✗ 警告: PermanentPasswords 未找到在配置文件中");
                        }
                    }
                    
                    return $"导入成功！\n\n" +
                           $"新增密码: {importedCount} 个\n" +
                           $"跳过重复: {skippedCount} 个\n" +
                           $"空行跳过: {emptyCount} 个\n" +
                           $"总计密码: {PermanentPasswords.Count} 个\n\n" +
                           $"密码已保存到配置文件:\n{ConfigPath}";
                }
                else
                {
                    return $"导入完成！\n\n" +
                           $"新增密码: 0 个\n" +
                           $"跳过重复: {skippedCount} 个\n" +
                           $"所有密码都已存在于配置文件中。";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"导入旧密码本失败: {ex.Message}");
                Console.WriteLine($"异常堆栈: {ex.StackTrace}");
                throw new Exception($"导入密码本失败：{ex.Message}", ex);
            }
        }

        /// <summary>
        /// 获取所有密码（一次性密码 + 永久密码，按综合评分排序）
        /// </summary>
        public List<string> GetAllPasswords()
        {
            var allPasswords = new List<string>();

            // 1. 先添加一次性密码（排在最前）
            allPasswords.AddRange(OneTimePasswords);

            // 2. 再添加永久密码（按综合评分排序：使用次数 + 最近使用时间）
            allPasswords.AddRange(PermanentPasswords
                .OrderByDescending(p => p.Score)
                .Select(p => p.Password));

            return allPasswords;
        }

        /// <summary>
        /// 添加一次性密码
        /// </summary>
        public void AddOneTimePassword(string password)
        {
            if (!OneTimePasswords.Contains(password))
            {
                // 插入到最前面
                OneTimePasswords.Insert(0, password);
            }
        }

        /// <summary>
        /// 移除一次性密码
        /// </summary>
        public void RemoveOneTimePassword(string password)
        {
            OneTimePasswords.Remove(password);
        }

        /// <summary>
        /// 添加永久密码（新密码排在最前面，给予高初始评分）
        /// </summary>
        public void AddPermanentPassword(string password, int usageCount = 0)
        {
            if (!PermanentPasswords.Any(p => p.Password == password))
            {
                // 新添加的密码给予高初始评分，确保排在最前面
                // 设置一个虚拟的"最近使用时间"，让它优先测试
                var entry = new PasswordEntry 
                { 
                    Password = password, 
                    UsageCount = usageCount,
                    LastUsedTime = DateTime.Now // 设置为当前时间，确保高评分
                };
                
                // 插入到列表开头
                PermanentPasswords.Insert(0, entry);
            }
            else
            {
                // 如果密码已存在，更新其最后使用时间为现在
                var entry = PermanentPasswords.First(p => p.Password == password);
                entry.LastUsedTime = DateTime.Now;
            }
        }

        /// <summary>
        /// 移除永久密码
        /// </summary>
        public void RemovePermanentPassword(string password)
        {
            PermanentPasswords.RemoveAll(p => p.Password == password);
        }

        /// <summary>
        /// 记录密码使用（更新使用次数和最后使用时间）
        /// </summary>
        public void RecordPasswordUsage(string password)
        {
            var entry = PermanentPasswords.FirstOrDefault(p => p.Password == password);
            if (entry != null)
            {
                entry.UsageCount++;
                entry.LastUsedTime = DateTime.Now;
                
                // 重新排序（按综合评分）
                PermanentPasswords = PermanentPasswords.OrderByDescending(p => p.Score).ToList();
            }
        }

        /// <summary>
        /// 保存密码到文件（使用两个Tab分隔符）
        /// 格式：密码\t\t使用次数\t\t最后使用时间(Ticks)
        /// </summary>
        public void SavePasswordsToFile()
        {
            if (string.IsNullOrWhiteSpace(PasswordFilePath))
            {
                return;
            }

            try
            {
                // 按综合评分排序后保存
                var sortedPasswords = PermanentPasswords.OrderByDescending(p => p.Score).ToList();
                var lines = sortedPasswords.Select(p => 
                    $"{p.Password}\t\t{p.UsageCount}\t\t{p.LastUsedTime.Ticks}").ToList();
                File.WriteAllLines(PasswordFilePath, lines);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存密码本失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取需要检测的压缩文件扩展名列表
        /// </summary>
        public HashSet<string> GetArchiveExtensions()
        {
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(ArchiveExtensions))
            {
                foreach (var ext in ArchiveExtensions.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = ext.Trim().ToLowerInvariant();
                    if (!trimmed.StartsWith("."))
                        trimmed = "." + trimmed;
                    extensions.Add(trimmed);
                }
            }
            return extensions;
        }
        
        /// <summary>
        /// 获取需要排除的文件扩展名列表
        /// </summary>
        public HashSet<string> GetExcludedExtensions()
        {
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(ExcludedExtensions))
            {
                foreach (var ext in ExcludedExtensions.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = ext.Trim().ToLowerInvariant();
                    if (!trimmed.StartsWith("."))
                        trimmed = "." + trimmed;
                    extensions.Add(trimmed);
                }
            }
            return extensions;
        }

        /// <summary>
        /// 获取黑名单文件模式列表（逗号、中文逗号、换行均可分隔多条）
        /// </summary>
        public List<string> GetBlacklistPatterns()
        {
            if (string.IsNullOrWhiteSpace(BlacklistFiles))
                return new List<string>();

            var separators = new[] { ',', '，' };
            var patterns = new List<string>();
            foreach (var line in BlacklistFiles.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var part in line.Split(separators, StringSplitOptions.RemoveEmptyEntries))
                {
                    string p = part.Trim();
                    if (p.Length > 0)
                        patterns.Add(p);
                }
            }

            return patterns;
        }

        [Obsolete("使用 LoadPasswordsFromFile 代替")]
        public List<string> LoadPasswords()
        {
            return GetAllPasswords();
        }
    }
}
