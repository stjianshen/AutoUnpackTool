using System;
using System.Collections.Generic;
using System.IO;
using AutoUnpackTool.Core.Configuration;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>
    /// 分卷压缩包识别：判断分卷形态并枚举同组的所有卷。
    /// </summary>
    public sealed class VolumeDetector
    {
        /// <summary>
        /// 判断文件是否为分卷压缩包的一部分
        /// 支持的分卷格式：
        /// - .7z.001, .7z.002, ...
        /// - .part1.rar, .part2.rar, ...
        /// - .001, .002, ... (通用分卷)
        /// - .z01, .z02, ... (ZIP 分卷)
        /// - .r00, .r01, ... (RAR 分卷)
        /// </summary>

        public bool IsMultiVolumeArchive(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            string fileName = Path.GetFileName(filePath).ToLowerInvariant();
            
            // 7z 分卷格式：.7z.001, .7z.002, ...
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.7z\.\d{3,}$"))
                return true;

            // RAR 分卷格式：.part1.rar, .part2.rar, ...
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.part\d+\.rar$"))
                return true;

            // 通用数字分卷：.001, .002, .003, ...
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.\d{3,}$"))
                return true;

            // ZIP 分卷：.z01, .z02, ...
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.z\d{2,}$"))
                return true;

            // RAR 老式分卷：.r00, .r01, ...
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.r\d{2,}$"))
                return true;

            return false;
        }

        /// <summary>
        /// 检测并获取分卷压缩包的所有分卷文件
        /// </summary>
        /// <param name="filePath">任意一个分卷文件路径</param>
        /// <returns>分卷压缩包信息</returns>
        public ArchiveVolumeInfo DetectMultiVolumeArchive(string filePath)
        {
            var volumeInfo = new ArchiveVolumeInfo();
            
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return volumeInfo;

            string directory = Path.GetDirectoryName(filePath) ?? string.Empty;
            string fileName = Path.GetFileName(filePath);
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
            
            // 判断分卷类型并查找所有分卷
            var allVolumes = new List<string>();

            // 1. 7z 分卷格式：xxx.7z.001, xxx.7z.002, ...
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.7z\.\d{3,}$"))
            {
                string baseName = fileName.Substring(0, fileName.LastIndexOf(".7z."));
                allVolumes = FindSequentialVolumes(directory, baseName + ".7z.", 3);
            }
            // 2. RAR 分卷格式：xxx.part1.rar, xxx.part2.rar, ...
            else if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.part\d+\.rar$"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(.+)\.part(\d+)\.rar$");
                if (match.Success)
                {
                    string baseName = match.Groups[1].Value;
                    allVolumes = FindRarPartVolumes(directory, baseName);
                }
            }
            // 3. 通用数字分卷：xxx.001, xxx.002, ...
            else if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.\d{3,}$"))
            {
                string extension = Path.GetExtension(fileName);
                string baseName = fileNameWithoutExt;
                allVolumes = FindSequentialVolumes(directory, baseName + ".", extension.TrimStart('.').Length);
            }
            // 4. ZIP 分卷：xxx.z01, xxx.z02, ...
            else if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.z\d{2,}$"))
            {
                string baseName = fileName.Substring(0, fileName.LastIndexOf(".z"));
                allVolumes = FindSequentialVolumes(directory, baseName + ".z", 2);
            }
            // 5. RAR 老式分卷：xxx.r00, xxx.r01, ...
            else if (System.Text.RegularExpressions.Regex.IsMatch(fileName, @"\.r\d{2,}$"))
            {
                string baseName = fileName.Substring(0, fileName.LastIndexOf(".r"));
                allVolumes = FindSequentialVolumes(directory, baseName + ".r", 2);
            }

            volumeInfo.AllVolumePaths = allVolumes;
            
            // 主卷通常是第一个分卷
            if (allVolumes.Count > 0)
            {
                volumeInfo.MainVolumePath = allVolumes[0];
            }

            return volumeInfo;
        }

        /// <summary>
        /// 查找顺序编号的分卷文件（如 .001, .002, .003）
        /// </summary>
        public List<string> FindSequentialVolumes(string directory, string prefix, int numberLength)
        {
            var volumes = new List<string>();
            int volumeNumber = 1;

            while (true)
            {
                string volumeName = $"{prefix}{volumeNumber.ToString($"D{numberLength}")}";
                string volumePath = Path.Combine(directory, volumeName);

                if (File.Exists(volumePath))
                {
                    volumes.Add(volumePath);
                    volumeNumber++;
                }
                else
                {
                    break;
                }
            }

            return volumes;
        }

        /// <summary>
        /// 查找 RAR part 格式的分卷文件
        /// </summary>
        public List<string> FindRarPartVolumes(string directory, string baseName)
        {
            var volumes = new List<string>();
            int partNumber = 1;

            while (true)
            {
                string volumeName = $"{baseName}.part{partNumber}.rar";
                string volumePath = Path.Combine(directory, volumeName);

                if (File.Exists(volumePath))
                {
                    volumes.Add(volumePath);
                    partNumber++;
                }
                else
                {
                    break;
                }
            }

            return volumes;
        }

    }
}
