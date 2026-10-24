using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoUnpackTool.Core.Abstractions;
using AutoUnpackTool.Core.Configuration;
using AutoUnpackTool.Core.Infrastructure;
using AutoUnpackTool.Core.Models;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>
    /// 原文件/分卷清理：永久删除、回收站、移动至清理目录，以及等待文件解锁与残留压缩软件进程清理。
    /// </summary>
    public sealed class FileCleanupService
    {
        private readonly AppSettings _settings;
        private readonly IAppLogger _logger;
        private readonly ConflictResolver _conflicts;
        private readonly FileOperationService _fileOps;

        public FileCleanupService(AppSettings settings, IAppLogger logger, ConflictResolver conflicts, FileOperationService fileOps)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _conflicts = conflicts ?? throw new ArgumentNullException(nameof(conflicts));
            _fileOps = fileOps ?? throw new ArgumentNullException(nameof(fileOps));
        }
        
        /// <summary>
        /// Kill掉所有7z进程
        /// </summary>
        public void KillSevenZipProcesses()
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("7z");
                if (processes.Length > 0)
                {
                    _logger.Log($"发现 {processes.Length} 个7z进程，正在终止...", ConsoleColor.Yellow);
                    foreach (var process in processes)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill();
                                process.WaitForExit(1000); // 等待1秒让进程退出
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Log($"终止7z进程失败: {ex.Message}", ConsoleColor.Red);
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                    _logger.Log("所有7z进程已终止", ConsoleColor.Green);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"查找7z进程失败: {ex.Message}", ConsoleColor.Red);
            }
        }

        
        /// <summary>
        /// Kill掉所有 NanaZip 进程
        /// </summary>
        public void KillNanaZipProcesses()
        {
            try
            {
                // NanaZip 的进程名可能不同，尝试查找
                var processNames = new[] { "NanaZip", "NanaZipC", "NanaZip.Universal.Console", "7zG", "NanaZipG" };
                
                foreach (var processName in processNames)
                {
                    var processes = System.Diagnostics.Process.GetProcessesByName(processName);
                    if (processes.Length > 0)
                    {
                        _logger.Log($"发现 {processes.Length} 个 {processName} 进程，正在终止...", ConsoleColor.Yellow);
                        foreach (var process in processes)
                        {
                            try
                            {
                                if (!process.HasExited)
                                {
                                    process.Kill();
                                    process.WaitForExit(1000); // 等待1秒让进程退出
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Log($"终止 {processName} 进程失败: {ex.Message}", ConsoleColor.Red);
                            }
                            finally
                            {
                                process.Dispose();
                            }
                        }
                    }
                }
                
                _logger.Log("所有压缩软件进程已终止", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                _logger.Log($"查找压缩软件进程失败: {ex.Message}", ConsoleColor.Red);
            }
        }


        /// <summary>
        /// 异步处理原文件（fire-and-forget 模式）
        /// </summary>
        public async Task HandleOriginalFileAsync(string filePath, FileItem? fileItem)
        {
            try
            {
                if (_settings.FileAfterExtract != FileAction.Keep)
                {
                    await HandleOriginalFile(filePath, fileItem);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"处理原文件时出错: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 永久删除文件（带重试逻辑），不受 FileAfterExtract 设置影响
        /// 用于清理子压缩包——子压缩包总是直接删除，不保留
        /// </summary>
        public async Task DeleteFilePermanentlyAsync(string filePath)
        {
            if (!File.Exists(filePath))
                return;

            // 统一走 FileOperationService：5 次尝试，每轮先等待解锁(8s)，失败随机退避(1~3s)
            var policy = new DeleteRetryPolicy
            {
                MaxRetries = 5,
                Backoff = BackoffKind.Random,
                BaseDelayMs = 1000,
                UnlockTimeoutMs = 8000,
                LogPrefix = "  "
            };
            bool ok = await _fileOps.DeleteWithRetryAsync(filePath, policy);
            if (ok)
                _logger.Log($"  子压缩包已删除: {Path.GetFileName(filePath)}", ConsoleColor.Gray);
        }


        /// <summary>
        /// 静默删除文件到回收站（不显示系统对话框）
        /// </summary>
        public async Task<bool> SilentDeleteToRecycleBinAsync(string filePath)
        {
            try
            {
                await Task.Run(() =>
                {
                    // 确保路径以双 null 结尾
                    string path = filePath + "\0";
                    
                    var fileOp = new NativeMethods.SHFILEOPSTRUCT
                    {
                        hwnd = IntPtr.Zero,
                        wFunc = NativeMethods.FO_DELETE,
                        pFrom = path,
                        pTo = string.Empty,
                        fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_SILENT,
                        fAnyOperationsAborted = false,
                        hNameMappings = IntPtr.Zero,
                        lpszProgressTitle = string.Empty
                    };

                    int result = NativeMethods.SHFileOperation(ref fileOp);
                    
                    // 返回0表示成功
                    if (result != 0)
                    {
                        throw new Exception($"SHFileOperation failed with error code: {result}");
                    }
                });
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.Log($"  删除文件到回收站失败: {ex.Message}", ConsoleColor.Red);
                return false;
            }
        }

        public async Task HandleOriginalFile(string filePath, FileItem? fileItem)
        {
            try
            {
                // 文件项（含分卷信息）由调用方在 UI 线程查找后传入
                
                switch (_settings.FileAfterExtract)
                {
                    case FileAction.MoveToRecycleBin:
                        // 移动到回收站
                        if (fileItem?.VolumeInfo?.HasVolumeList == true)
                        {
                            // 分卷压缩包：移动所有分卷
                            await MoveAllVolumesToRecycleBin(fileItem.VolumeInfo);
                        }
                        else
                        {
                            // 普通文件：移动到回收站
                            bool success = false;
                            int retryCount = 3;
                                                    
                            for (int i = 0; i < retryCount; i++)
                            {
                                // 尝试等待文件解锁（每次等待5秒）
                                bool unlocked = await WaitForFileUnlock(filePath, 5000);
                                                        
                                if (!unlocked)
                                {
                                    int randomWait = Random.Shared.Next(500, 2001); // 500-2000ms 随机等待
                                    _logger.Log($"  原文件仍在占用中，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                    await Task.Delay(randomWait);
                                    continue;
                                }
                            
                                try
                                {
                                    // 使用Windows API静默删除到回收站
                                    _logger.Log($"  文件已解锁，开始移动到回收站: {Path.GetFileName(filePath)}", ConsoleColor.Gray);
                                    success = await SilentDeleteToRecycleBinAsync(filePath);
                                    if (success)
                                    {
                                        break;
                                    }
                                    else
                                    {
                                        int randomWait = Random.Shared.Next(500, 2001); // 500-2000ms 随机等待
                                        _logger.Log($"  删除失败，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                        await Task.Delay(randomWait);
                                    }
                                }
                                catch (IOException ex)
                                {
                                    int randomWait = Random.Shared.Next(500, 2001); // 500-2000ms 随机等待
                                    _logger.Log($"  原文件被占用 ({ex.Message})，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                    await Task.Delay(randomWait);
                                }
                            }

                            if (success)
                            {
                                _logger.Log($"  原文件已移动到回收站: {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                            }
                            else
                            {
                                _logger.Log($"  移动原文件失败（多次重试后仍被占用）: {Path.GetFileName(filePath)}", ConsoleColor.Red);
                            }
                        }
                        break;

                    case FileAction.Delete:
                        // 直接删除
                        if (fileItem?.VolumeInfo?.HasVolumeList == true)
                        {
                            // 分卷压缩包：删除所有分卷
                            await DeleteAllVolumes(fileItem.VolumeInfo);
                        }
                        else
                        {
                            // 普通文件：直接删除
                            bool success = false;
                            int retryCount = 5;  // 增加重试次数到5次
                                                    
                            for (int i = 0; i < retryCount; i++)
                            {
                                // 尝试等待文件解锁（增加等待时间到8秒）
                                bool unlocked = await WaitForFileUnlock(filePath, 8000);
                                                        
                                if (!unlocked)
                                {
                                    int randomWait = Random.Shared.Next(1000, 3001); // 1000-3000ms 随机等待
                                    _logger.Log($"  原文件仍在占用中，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                    await Task.Delay(randomWait);
                                    continue;
                                }
                            
                                try
                                {
                                    await Task.Run(() => File.Delete(filePath));
                                    success = true;
                                    break;
                                }
                                catch (IOException ex)
                                {
                                    int randomWait = Random.Shared.Next(1000, 3001); // 1000-3000ms 随机等待
                                    _logger.Log($"  原文件被占用 ({ex.Message})，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                                    await Task.Delay(randomWait);
                                }
                            }

                            if (success)
                            {
                                _logger.Log($"  原文件已删除: {Path.GetFileName(filePath)}", ConsoleColor.Yellow);
                            }
                            else
                            {
                                _logger.Log($"   删除原文件失败（多次重试后仍被占用）: {Path.GetFileName(filePath)}", ConsoleColor.Red);
                                _logger.Log($"  提示：请关闭可能占用该文件的程序（如资源管理器、杀毒软件等），然后手动删除", ConsoleColor.Gray);
                            }
                        }
                        break;

                    case FileAction.MoveToSpecificDir:
                        // 移动到指定目录
                        if (fileItem?.VolumeInfo?.HasVolumeList == true)
                        {
                            // 分卷压缩包：移动所有分卷
                            await MoveAllVolumesToSpecificDir(fileItem.VolumeInfo);
                        }
                        else
                        {
                            // 普通文件：移动到指定目录
                            await MoveFileToSpecificDir(filePath);
                        }
                        break;

                    case FileAction.Keep:
                    default:
                        // 保留原文件，不做处理
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"  处理原文件失败: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// 移动所有分卷到回收站
        /// </summary>
        public async Task MoveAllVolumesToRecycleBin(ArchiveVolumeInfo volumeInfo)
        {
            _logger.Log($"  准备移动 {volumeInfo.VolumeCount} 个分卷到回收站...", ConsoleColor.Cyan);
            
            int movedCount = 0;
            foreach (var volumePath in volumeInfo.AllVolumePaths)
            {
                try
                {
                    if (File.Exists(volumePath))
                    {
                        bool success = false;
                        int retryCount = 3;
                        
                        for (int i = 0; i < retryCount; i++)
                        {
                            // 尝试等待文件解锁（每次等待5秒）
                            bool unlocked = await WaitForFileUnlock(volumePath, 5000);
                            
                            if (!unlocked)
                            {
                                int randomWait = Random.Shared.Next(500, 2001); // 500-2000ms 随机等待
                                _logger.Log($"  分卷文件仍在占用中，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                                await Task.Delay(randomWait);
                                continue;
                            }

                            try
                            {
                                // 使用Windows API静默删除到回收站
                                success = await SilentDeleteToRecycleBinAsync(volumePath);
                                if (success)
                                {
                                    break;
                                }
                                else
                                {
                                    int randomWait = Random.Shared.Next(500, 2001); // 500-2000ms 随机等待
                                    _logger.Log($"  删除失败，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                                    await Task.Delay(randomWait);
                                }
                            }
                            catch (IOException ex)
                            {
                                int randomWait = Random.Shared.Next(500, 2001); // 500-2000ms 随机等待
                                _logger.Log($"  分卷文件被占用 ({ex.Message})，等待 {randomWait/1000.0:F1} 秒后重试 ({i + 1}/{retryCount}): {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                                await Task.Delay(randomWait);
                            }
                            catch (Exception ex)
                            {
                                _logger.Log($"  移动分卷失败 ({ex.GetType().Name}: {ex.Message}): {Path.GetFileName(volumePath)}", ConsoleColor.Red);
                                break;
                            }
                        }

                        if (success)
                        {
                            _logger.Log($"  已移动分卷到回收站: {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                            movedCount++;
                        }
                        else
                        {
                            _logger.Log($"  移动分卷失败（多次重试后仍被占用）: {Path.GetFileName(volumePath)}", ConsoleColor.Red);
                        }
                    }
                }
                catch (Exception ex)
                {
                    string errorMsg = ex.Message;
                    if (string.IsNullOrWhiteSpace(errorMsg) && ex.InnerException != null)
                    {
                        errorMsg = ex.InnerException.Message;
                    }
                    _logger.Log($"  移动分卷失败 {Path.GetFileName(volumePath)}: {errorMsg}", ConsoleColor.Red);
                }
            }
            
            _logger.Log($"  分卷压缩包已处理（已移动 {movedCount}/{volumeInfo.VolumeCount} 个分卷到回收站）", ConsoleColor.Green);
        }

        /// <summary>
        /// 等待文件解锁（文件不再被其他进程占用）
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>true=文件已解锁, false=超时</returns>
        public async Task<bool> WaitForFileUnlock(string filePath, int timeoutMs)
            => await _fileOps.WaitForUnlockAsync(filePath, timeoutMs);

        /// <summary>
        /// 删除所有分卷
        /// </summary>
        public async Task DeleteAllVolumes(ArchiveVolumeInfo volumeInfo)
        {
            _logger.Log($"  准备删除 {volumeInfo.VolumeCount} 个分卷...", ConsoleColor.Cyan);
            
            int deletedCount = 0;
            foreach (var volumePath in volumeInfo.AllVolumePaths)
            {
                try
                {
                    if (File.Exists(volumePath))
                    {
                        // 统一走 FileOperationService：3 次尝试，每轮先等待解锁(5s)，失败线性退避(1s,2s)
                        var policy = new DeleteRetryPolicy
                        {
                            MaxRetries = 3,
                            Backoff = BackoffKind.Linear,
                            BaseDelayMs = 1000,
                            UnlockTimeoutMs = 5000,
                            LogPrefix = "  "
                        };
                        if (await _fileOps.DeleteWithRetryAsync(volumePath, policy))
                        {
                            _logger.Log($"  已删除分卷: {Path.GetFileName(volumePath)}", ConsoleColor.Yellow);
                            deletedCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    string errorMsg = ex.Message;
                    if (string.IsNullOrWhiteSpace(errorMsg) && ex.InnerException != null)
                    {
                        errorMsg = ex.InnerException.Message;
                    }
                    _logger.Log($"  删除分卷失败 {Path.GetFileName(volumePath)}: {errorMsg}", ConsoleColor.Red);
                }
            }
            
            _logger.Log($"  分卷压缩包已处理（已删除 {deletedCount}/{volumeInfo.VolumeCount} 个分卷）", ConsoleColor.Green);
        }

        /// <summary>
        /// 移动所有分卷到指定目录
        /// </summary>
        public async Task MoveAllVolumesToSpecificDir(ArchiveVolumeInfo volumeInfo)
        {
            if (string.IsNullOrWhiteSpace(_settings.ArchiveCleanupDir))
            {
                _logger.Log($"  警告：未设置分卷清理目录", ConsoleColor.Yellow);
                return;
            }

            if (!Directory.Exists(_settings.ArchiveCleanupDir))
            {
                Directory.CreateDirectory(_settings.ArchiveCleanupDir);
            }

            _logger.Log($"  准备移动 {volumeInfo.VolumeCount} 个分卷到: {_settings.ArchiveCleanupDir}", ConsoleColor.Cyan);
            
            int movedCount = 0;
            foreach (var volumePath in volumeInfo.AllVolumePaths)
            {
                try
                {
                    if (File.Exists(volumePath))
                    {
                        string destPath = Path.Combine(_settings.ArchiveCleanupDir, Path.GetFileName(volumePath));

                        // 清理目录是工具自建的收集箱，固定“保留两份”而不受重名策略影响；
                        // 唯一名的造法统一走 ConflictResolver 的序号风格。
                        if (File.Exists(destPath))
                            destPath = _conflicts.BuildUniquePath(_settings.ArchiveCleanupDir,
                                Path.GetFileName(volumePath), NamingStyle.NumericSuffix);

                        await Task.Run(() => File.Move(volumePath, destPath));
                        _logger.Log($"  已移动分卷: {Path.GetFileName(volumePath)} -> {Path.GetFileName(destPath)}", ConsoleColor.Yellow);
                        movedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"  移动分卷失败 {Path.GetFileName(volumePath)}: {ex.Message}", ConsoleColor.Red);
                }
            }
            
            _logger.Log($"  分卷压缩包已处理（已移动 {movedCount}/{volumeInfo.VolumeCount} 个分卷）", ConsoleColor.Green);
        }

        /// <summary>
        /// 移动单个文件到指定目录
        /// </summary>
        public async Task MoveFileToSpecificDir(string filePath)
        {
            if (string.IsNullOrWhiteSpace(_settings.ArchiveCleanupDir))
            {
                _logger.Log($"  警告：未设置清理目录", ConsoleColor.Yellow);
                return;
            }

            if (!Directory.Exists(_settings.ArchiveCleanupDir))
            {
                Directory.CreateDirectory(_settings.ArchiveCleanupDir);
            }

            try
            {
                string destPath = Path.Combine(_settings.ArchiveCleanupDir, Path.GetFileName(filePath));

                // 同上：清理目录固定保留两份，序号命名由 ConflictResolver 统一生成
                if (File.Exists(destPath))
                    destPath = _conflicts.BuildUniquePath(_settings.ArchiveCleanupDir,
                        Path.GetFileName(filePath), NamingStyle.NumericSuffix);

                await Task.Run(() => File.Move(filePath, destPath));
                _logger.Log($"  原文件已移动: {Path.GetFileName(filePath)} -> {destPath}", ConsoleColor.Yellow);
            }
            catch (Exception ex)
            {
                _logger.Log($"  移动文件失败: {ex.Message}", ConsoleColor.Red);
            }
        }
    }
}
