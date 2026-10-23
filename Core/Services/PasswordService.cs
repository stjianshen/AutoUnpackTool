using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>
    /// 密码本业务逻辑（原 PasswordManagerDialog 内的加载/导入/导出/备份逻辑迁出）。
    /// 只做数据处理，不弹窗、不碰控件；消息提示由 ViewModel 负责。
    /// </summary>
    public class PasswordService
    {
        private readonly AppSettings _settings;

        public PasswordService(AppSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// 构建密码本行集合：永久密码（已按综合评分排序）在前，一次性密码倒序插入最前。
        /// </summary>
        public List<PasswordItem> BuildItems()
        {
            var items = new List<PasswordItem>();

            // 加载永久密码（从文件）- 已按综合评分排序
            var permanentPasswords = _settings.LoadPasswordsFromFile();
            int index = 1;
            foreach (var pwd in permanentPasswords)
            {
                items.Add(new PasswordItem
                {
                    Index = index++,
                    Password = pwd.Password,
                    UsageCount = pwd.UsageCount,
                    Type = "永久",
                    LastUsedTime = pwd.LastUsedTime
                });
            }

            // 加载一次性密码（本次运行添加的）- 插入到最前面
            for (int i = _settings.OneTimePasswords.Count - 1; i >= 0; i--)
            {
                items.Insert(0, new PasswordItem
                {
                    Index = 0,
                    Password = _settings.OneTimePasswords[i],
                    UsageCount = 0,
                    Type = "一次性",
                    LastUsedTime = DateTime.Now
                });
            }

            Reindex(items);
            return items;
        }

        /// <summary>重新编号（从 1 开始）。</summary>
        public static void Reindex(IList<PasswordItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Index = i + 1;
            }
        }

        /// <summary>
        /// 从文本文件加载密码到目标列表（一次性使用，不与配置文件合并，去重）。
        /// 支持 TAB 分隔格式：密码\t\t使用次数\t\t最后使用时间(Ticks)。
        /// </summary>
        /// <returns>(加载数量, 跳过重复数量)</returns>
        public (int loaded, int skipped) LoadFromFile(string filePath, IList<PasswordItem> target)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return (0, 0);
            }

            string[] lines = File.ReadAllLines(filePath);
            int loadedCount = 0;
            int skippedCount = 0;

            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                // 解析密码（支持TAB分隔格式）
                string password = trimmed;
                string[] parts = trimmed.Split(new[] { "\t\t" }, StringSplitOptions.None);
                if (parts.Length >= 1)
                {
                    password = parts[0].Trim();
                }

                if (string.IsNullOrEmpty(password))
                    continue;

                // 去重：检查是否已存在于当前列表
                if (target.Any(p => p.Password == password))
                {
                    skippedCount++;
                    continue;
                }

                // 添加到列表（临时类型，不保存到配置，仅本次会话使用）
                target.Add(new PasswordItem
                {
                    Index = target.Count + 1,
                    Password = password,
                    UsageCount = 0,
                    Type = "临时",
                    LastUsedTime = DateTime.MinValue
                });
                loadedCount++;
            }

            return (loadedCount, skippedCount);
        }

        /// <summary>
        /// 把当前列表中的密码（跳过一次性）写入配置文件的永久密码，已存在的合并使用信息。
        /// </summary>
        /// <returns>(新增数量, 已存在数量)</returns>
        public (int imported, int updated) ImportToSettings(IEnumerable<PasswordItem> items)
        {
            int importedCount = 0;
            int skippedCount = 0;

            foreach (var item in items)
            {
                // 跳过一次性密码（它们不会导入到配置）
                if (item.Type == "一次性")
                {
                    continue;
                }

                // 如果密码不存在于配置文件，添加它
                if (!_settings.PermanentPasswords.Any(p => p.Password == item.Password))
                {
                    _settings.PermanentPasswords.Add(new PasswordEntry
                    {
                        Password = item.Password,
                        UsageCount = item.UsageCount,
                        LastUsedTime = item.LastUsedTime
                    });
                    importedCount++;
                }
                else
                {
                    // 如果已存在，更新使用信息
                    var existing = _settings.PermanentPasswords.First(p => p.Password == item.Password);
                    existing.UsageCount = Math.Max(existing.UsageCount, item.UsageCount);
                    if (item.LastUsedTime > existing.LastUsedTime)
                    {
                        existing.LastUsedTime = item.LastUsedTime;
                    }
                    skippedCount++;
                }
            }

            return (importedCount, skippedCount);
        }

        /// <summary>
        /// 导出永久密码到文本文件，格式：密码\t\t使用次数\t\t最后使用时间(Ticks)。
        /// </summary>
        public void ExportToFile(string filePath)
        {
            var passwords = _settings.LoadPasswordsFromFile();

            var lines = new List<string>();
            foreach (var pwd in passwords)
            {
                lines.Add($"{pwd.Password}\t\t{pwd.UsageCount}\t\t{pwd.LastUsedTime.Ticks}");
            }

            File.WriteAllLines(filePath, lines);
        }

        /// <summary>
        /// 保存配置到文件（只保留一个 .backup 备份）。
        /// </summary>
        public void SaveWithBackup()
        {
            string configPath = AppSettings.ConfigFilePath;

            if (File.Exists(configPath))
            {
                string backupPath = configPath + ".backup";

                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                File.Copy(configPath, backupPath);
            }

            _settings.Save();
        }

        /// <summary>永久密码总数（用于导入结果提示）。</summary>
        public int PermanentPasswordCount => _settings.PermanentPasswords.Count;
    }
}
